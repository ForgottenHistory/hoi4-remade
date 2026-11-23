"""Prototype for the war AI's country layer (Docs/ai-layers.md): theatres, budgets, armies and their sections.

    python Tools/prototypes/ai_armies/theatres.py [REC] --country ITA [--date "1938-02-10"] [--days 7]

Reads a session recording (control replayed from control.tsv, divisions from divisions.tsv, the wars from the AI
lines in plans.tsv) and the map from Hoi4Data. For the country at that date:
- front provinces: its passable land next to passable land of an enemy it has a line against;
- theatres: front provinces joined through land adjacency, or through facing the same enemy province (a lake on our
  side), across enemies;
- per theatre: holding need max(1, ceil(provinces/3), ceil(0.8 x enemy divisions within 2 hops on their side)), a
  placeholder score (provinces / (facing + 1); the C# uses the route cost), the main effort the best score;
- budget: holding needs, the surplus to the main effort up to 2 per front province, overflow by score, rest reserve;
- armies: ceil(budget / 24) per theatre; the theatre's provinces ordered along the front (end-to-end walk) and
  split into that many contiguous chunks, each an army's section.
With --days N it runs again N days later, matches the armies by section overlap and prints how much each section
kept (stable ids and sections matter: an army whose section jumps marches across the theatre).
Renders ai_armies_<TAG>_<date>.png next to this file.
"""
import argparse, csv, math, os, pickle, sys
from collections import defaultdict
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Tools"))
sys.path.insert(0, str(ROOT / "Tools" / "recorder"))
from manpower_pools import json5  # noqa: E402

HERE = Path(__file__).resolve().parent
DATA = ROOT / "Hoi4Data" / "map"
ARMY = 24
CARRY_HOPS = 4
TRANSFER_KM = 1500   # an army takes over another theatre only this close (its old section's centre)
CAP = 2


def load_map():
    cache = HERE / "_map.pkl"  # ignored (Tools/prototypes/**/*.pkl): derived from the HOI4 map
    defs = {}
    for r in csv.reader(open(DATA / "definition.csv", encoding="utf-8", errors="replace"), delimiter=";"):
        if r and r[0].isdigit():
            defs[(int(r[1]) << 16) | (int(r[2]) << 8) | int(r[3])] = int(r[0])
    if cache.exists():
        with open(cache, "rb") as f:
            ids, pairs = pickle.load(f)
    else:
        img = np.asarray(Image.open(DATA / "provinces.png").convert("RGB")).astype(np.int32)
        key = (img[..., 0] << 16) | (img[..., 1] << 8) | img[..., 2]
        u, inv = np.unique(key, return_inverse=True)
        lut = np.array([defs.get(int(k), 0) for k in u], dtype=np.int32)
        ids = lut[inv].reshape(key.shape)
        a = np.concatenate([ids[:, :-1].ravel(), ids[:-1, :].ravel()])
        b = np.concatenate([ids[:, 1:].ravel(), ids[1:, :].ravel()])
        m = a != b
        pairs = np.unique(np.stack([np.minimum(a[m], b[m]), np.maximum(a[m], b[m])], 1), axis=0)
        with open(cache, "wb") as f:
            pickle.dump((ids, pairs), f)
    adj = defaultdict(set)
    for a, b in pairs:
        if a and b:
            adj[int(a)].add(int(b)); adj[int(b)].add(int(a))
    for r in csv.reader(open(DATA / "adjacencies.csv", encoding="utf-8"), delimiter=";"):
        if not r or not r[0].isdigit() or not r[1].isdigit():
            continue
        a, b, kind = int(r[0]), int(r[1]), r[2].strip()
        if kind == "impassable":
            adj[a].discard(b); adj[b].discard(a)
        else:
            adj[a].add(b); adj[b].add(a)
    reg = json5(DATA / "hoi4_regions.json5")
    water = set(reg["water_provinces"])
    impassable = {p for s in reg["impassable_states"] for p in reg["states"].get(str(s), [])}
    state_of = {p: int(s) for s, ps in reg["states"].items() for p in ps}
    sd = json5(DATA / "hoi4_state_data.json5")["states"]
    owner0 = {p: sd[str(s)]["owner"] for p, s in state_of.items() if str(s) in sd and sd[str(s)].get("owner")}
    return ids, adj, water, impassable, owner0


_XY = None


def centres_xy():
    global _XY
    if _XY is None:
        _XY = {int(r["id"]): (int(r["x"]), int(r["y"])) for r in csv.DictReader(open(DATA / "hoi4_province_centres.csv"), delimiter=";")}
    return _XY


def fill(t, army):
    """Provinces of the theatre without an army join the nearest army along the front (links; ties the lower id)."""
    q = sorted(army, key=lambda p: (army[p], p))
    for x in q:
        for y in sorted(t["links"][x]):
            if y not in army: army[y] = army[x]; q.append(y)
    for p in t["order"]:
        if p not in army: army[p] = min(army.values()) if army else 0
    return army


def rows(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        head = f.readline().rstrip("\n").split("\t")
        for line in f:
            p = line.rstrip("\n").split("\t")
            if len(p) >= len(head) and p[0].isdigit():
                yield dict(zip(head, p))


def state_at(rec, tick, owner0):
    ctrl = dict(owner0)
    for r in rows(rec / "control.tsv"):
        if int(r["tick"]) > tick:
            break
        ctrl[int(r["province"])] = r["to"]
    divs = {}
    for r in rows(rec / "divisions.tsv"):
        if int(r["tick"]) > tick:
            break
        if r.get("why") == "gone":
            divs.pop(r["id"], None)
        else:
            divs[r["id"]] = (r["tag"], int(r["province"]))
    # owner -> enemies it has an AI line against: the latest row of each AI plan at or before tick (rows are
    # written on change only), less plans removed by then (plans/removed notes)
    removed = {}
    for r in rows(rec / "notes.tsv"):
        if int(r["tick"]) > tick:
            break
        if r.get("system") == "plans" and r.get("kind") == "removed":
            removed[r["text"].split(",")[0].split()[-1]] = int(r["tick"])
    last = {}
    for r in rows(rec / "plans.tsv"):
        if int(r["tick"]) > tick:
            break
        if r.get("ai") == "1":
            last[r["plan"]] = r
    wars = defaultdict(set)
    for pid, r in last.items():
        if pid not in removed or removed[pid] < int(r["tick"]):
            wars[r["owner"]].add(r["country"])
    return ctrl, divs, wars


def tick_of(rec, date):
    for r in rows(rec / "divisions.tsv"):
        if r["date"] >= date:
            return int(r["tick"])
    return None


def plan(country, ctrl, divs, wars, adj, water, impassable, capital, prev=None, next_id=None):
    """prev: province -> army id from the last run (sections carry forward); next_id: [int] for new army ids."""
    enemies = wars.get(country, set())
    prev = prev or {}
    next_id = next_id if next_id is not None else [1]
    def land(p): return p not in water and p not in impassable and p in adj
    # our land in connected pieces: a theatre outside the piece with the capital is a pocket (its budget is
    # the divisions already in it: nobody else can get there)
    piece = {}
    for s in sorted(p for p, c in ctrl.items() if c == country and land(p)):
        if s in piece: continue
        piece[s] = s; q = [s]
        for x in q:
            for y in adj[x]:
                if y not in piece and ctrl.get(y) == country and land(y): piece[y] = s; q.append(y)
    home = piece.get(capital)
    if home is None and piece:
        sizes = defaultdict(int)
        for v in piece.values(): sizes[v] += 1
        home = max(sizes, key=lambda k: (sizes[k], -k))
    front = sorted(p for p, c in ctrl.items() if c == country and land(p)
                   and any(land(n) and ctrl.get(n) in enemies for n in adj[p]))
    fset = set(front)
    # theatres: union-find through adjacency, or facing the same enemy province
    root = {p: p for p in front}
    def find(x):
        while root[x] != x:
            root[x] = root[root[x]]; x = root[x]
        return x
    def union(a, b):
        a, b = find(a), find(b)
        if a != b: root[max(a, b)] = min(a, b)
    faced = defaultdict(list)
    for p in front:
        for n in adj[p]:
            if n in fset: union(p, n)
            elif land(n) and ctrl.get(n) in enemies: faced[n].append(p)
    for ps in faced.values():
        for q in ps[1:]: union(ps[0], q)
    groups = defaultdict(list)
    for p in front: groups[find(p)].append(p)
    theatres = []
    by_tag = defaultdict(list)
    for tag, p in divs.values(): by_tag[tag].append(p)
    for g in sorted(groups.values(), key=lambda g: min(g)):
        gs = set(g)
        # order along the front: walk distances (through front provinces, and through the enemy province two face)
        links = defaultdict(set)
        for p in g:
            for n in adj[p]:
                if n in gs: links[p].add(n)
        for ps in faced.values():
            qs = [q for q in ps if q in gs]
            for a in qs:
                for b in qs:
                    if a != b: links[a].add(b)
        def bfs(s):
            d = {s: 0}; q = [s]
            for x in q:
                for y in sorted(links[x]):
                    if y not in d: d[y] = d[x] + 1; q.append(y)
            return d
        d0 = bfs(min(g)); end = max(d0, key=lambda p: (d0[p], -p))
        d = bfs(end)
        order = sorted(g, key=lambda p: (d.get(p, 1 << 30), p))
        # facing enemy divisions within 2 hops on their side
        seen = set(g); ring = [n for p in g for n in adj[p] if land(n) and ctrl.get(n) in enemies and n not in seen]
        ring = sorted(set(ring)); seen |= set(ring)
        facing = 0
        enemy_divs = defaultdict(int)
        for tag, p in divs.values():
            if tag in enemies: enemy_divs[p] += 1
        for hop in (1, 2):
            facing += sum(enemy_divs[p] for p in ring)
            nxt = sorted({n for p in ring for n in adj[p] if land(n) and ctrl.get(n) in enemies and n not in seen})
            seen |= set(nxt); ring = nxt
        faces = sorted({ctrl[n] for p in g for n in adj[p] if land(n) and ctrl.get(n) in enemies})
        need = max(1, math.ceil(len(g) / 3), math.ceil(0.8 * facing))
        pocket = not any(piece.get(p) == home for p in g)
        inside = sum(1 for tag, q in divs.values() if tag == country and piece.get(q) == piece.get(g[0])) if pocket else 0
        theatres.append(dict(provinces=g, order=order, links=links, facing=facing, faces=faces, need=need,
                             score=0 if pocket else len(g) / (facing + 1), pocket=pocket, inside=inside))
    total = len(by_tag.get(country, []))
    # pockets keep what is in them; the rest is shared by the reachable theatres
    for th in theatres:
        if th["pocket"]: th["need"] = 0
    total_free = total - sum(th["inside"] for th in theatres if th["pocket"])
    open_t = [th for th in theatres if not th["pocket"]]
    total, all_t, theatres = total_free, theatres, open_t
    # budgets
    if theatres:
        main = max(range(len(theatres)), key=lambda i: (theatres[i]["score"], -i))
        need = sum(t["need"] for t in theatres)
        if need <= total:
            rest = total - need
            for t in theatres: t["budget"] = t["need"]
            for i in [main] + sorted((i for i in range(len(theatres)) if i != main), key=lambda i: -theatres[i]["score"]):
                room = max(0, len(theatres[i]["provinces"]) * CAP - theatres[i]["budget"])
                give = min(room, rest); theatres[i]["budget"] += give; rest -= give
            reserve = rest
        else:
            for t in theatres: t["budget"] = t["need"] * total // need
            theatres[main]["budget"] += total - sum(t["budget"] for t in theatres)
            reserve = 0
        for i, t in enumerate(theatres):
            t["main"] = i == main
    else:
        reserve = total
    for t in all_t:
        if t["pocket"]: t["budget"], t["main"] = t["inside"], False
    # carry by nearness: each front province takes the army of the nearest previous front province (land hops
    # through any passable land, up to CarryHops; ties the lower army id), so a front that moved keeps its armies
    near = {}
    if prev:
        q = sorted(prev, key=lambda p: (prev[p], p))
        dist = {p: 0 for p in q}
        near = dict(prev)
        for x in q:
            if dist[x] >= CARRY_HOPS: continue
            for y in sorted(adj[x]):
                if y not in dist and land(y):
                    dist[y] = dist[x] + 1; near[y] = near[x]; q.append(y)
    prev = near
    # armies: an army belongs to the theatre holding most of its carried provinces
    where = defaultdict(lambda: defaultdict(int))
    for i, t in enumerate(all_t):
        for p in t["provinces"]:
            if p in prev: where[prev[p]][i] += 1
    home_of = {a: max(c, key=lambda i: (c[i], -i)) for a, c in where.items()}
    xy = centres_xy()
    def centre(ps):
        v = [xy[p] for p in ps if p in xy]
        return (sum(x for x, _ in v) / len(v), sum(y for _, y in v) / len(v)) if v else (0, 0)
    # armies whose theatre is gone are free to take over another theatre's need (whole armies move)
    old_centre = {}
    for p, v in prev.items(): old_centre.setdefault(v, []).append(p)
    old_centre = {v: centre(ps) for v, ps in old_centre.items()}
    pool = {v for v in old_centre if v not in home_of}
    maps, wants = [], []
    # pass 1: carry, and merge away armies a theatre has too many of (into the pool)
    for i, t in enumerate(all_t):
        n = max(1, math.ceil(t["budget"] / ARMY)) if t["budget"] > 0 else 0
        mine = sorted(a for a, h in home_of.items() if h == i)
        o = t["order"]
        army = fill(t, {p: prev[p] for p in o if prev.get(p) in mine}) if mine and n else {}
        while army and len(set(army.values())) > n:
            size = defaultdict(int)
            for v in army.values(): size[v] += 1
            drop = min(size, key=lambda v: (size[v], -v))
            pool.add(drop)
            army = fill(t, {p: v for p, v in army.items() if v != drop})
        if not n:
            pool.update(mine)
        maps.append(army); wants.append(n)
        t["carried"] = bool(army)
    # pass 2: theatres short of armies take the nearest pooled army (by old section centre), else a new one;
    # a theatre without any is split evenly, one with some splits its largest
    for i, t in enumerate(all_t):
        n, army, o = wants[i], maps[i], t["order"]
        pos = {p: k for k, p in enumerate(o)}
        def take():
            c = centre(o)
            near = [v for v in pool if math.dist(old_centre[v], c) * 7.114 <= TRANSFER_KM]
            if near:
                v = min(near, key=lambda v: (math.dist(old_centre[v], c), v))
                pool.discard(v)
                return v
            v = next_id[0]; next_id[0] += 1
            return v
        if n and not army:
            got = [take() for _ in range(n)]
            for k in range(n):
                for p in o[len(o) * k // n: len(o) * (k + 1) // n]: army[p] = got[k]
        while n and len(set(army.values())) < n:
            size = defaultdict(int)
            for v in army.values(): size[v] += 1
            big = max(size, key=lambda v: (size[v], -v))
            ps = sorted((p for p, v in army.items() if v == big), key=lambda p: pos[p])
            aid = take()
            for p in ps[len(ps) // 2:]: army[p] = aid
        if not n:
            t["chunks"], t["ids"] = [], []
            continue
        # one connected stretch per army: its largest run along the front stays (ties: the one with the lowest
        # province), islands go to the army around them
        keep = {}
        for v in sorted(set(army.values())):
            mine_p = {p for p, x in army.items() if x == v}
            runs, seen = [], set()
            for s in sorted(mine_p):
                if s in seen: continue
                run = [s]; seen.add(s)
                for x in run:
                    for y in sorted(t["links"][x]):
                        if y in mine_p and y not in seen: seen.add(y); run.append(y)
                runs.append(run)
            best = max(runs, key=lambda r: (len(r), -min(r)))
            for p in best: keep[p] = v
        army = fill(t, keep)
        ids_here = sorted(set(army.values()), key=lambda v: min(pos[p] for p, x in army.items() if x == v))
        t["chunks"] = [[p for p in o if army[p] == v] for v in ids_here]
        t["ids"] = ids_here
    t_pool = sorted(pool)
    for t in all_t: t["disbanded"] = t_pool
    return all_t, total, reserve, by_tag.get(country, [])


def render(name, country, theatres, ctrl, divs_at, ids, water, enemies, title):
    allp = [p for t in theatres for p in t["provinces"]]
    if not allp:
        print("no front"); return
    mask = np.isin(ids, allp)
    ys, xs = np.nonzero(mask)
    pad = 120
    x0, x1 = max(0, xs.min() - pad), min(ids.shape[1], xs.max() + pad)
    y0, y1 = max(0, ys.min() - pad), min(ids.shape[0], ys.max() + pad)
    sub = ids[y0:y1, x0:x1]
    u, inv = np.unique(sub, return_inverse=True)
    col = np.zeros((len(u), 3), np.uint8)
    palette = [(230, 80, 60), (60, 140, 230), (90, 190, 80), (230, 170, 40), (170, 90, 210), (40, 190, 190),
               (220, 110, 170), (140, 140, 60), (110, 90, 60), (60, 90, 160)]
    army_of = {}
    k = 0
    for t in theatres:
        for c in t["chunks"]:
            for p in c: army_of[p] = k
            k += 1
    for i, p in enumerate(u):
        p = int(p); c = ctrl.get(p)
        if p in water: col[i] = (150, 180, 210)
        elif p in army_of: col[i] = palette[army_of[p] % len(palette)]
        elif c == country: col[i] = (215, 215, 205)
        elif c in enemies: col[i] = (240, 200, 195)
        else: col[i] = (245, 245, 240)
    img = col[inv].reshape(sub.shape + (3,))
    edge = np.zeros(sub.shape, bool)
    edge[:, 1:] |= sub[:, 1:] != sub[:, :-1]; edge[1:, :] |= sub[1:, :] != sub[:-1, :]
    img[edge] = (img[edge] * 0.75).astype(np.uint8)
    scale = 2 if max(sub.shape) < 1200 else 1
    im = Image.fromarray(img).resize((sub.shape[1] * scale, sub.shape[0] * scale), Image.NEAREST)
    dr = ImageDraw.Draw(im)
    # divisions: own black dots, enemy red
    cent = {}
    for p in set(divs_at[0]) | set(divs_at[1]):
        yy, xx = np.nonzero(sub == p)
        if len(xx): cent[p] = ((xx.mean()) * scale, (yy.mean()) * scale)
    for p in divs_at[1]:
        if p in cent: x, y = cent[p]; dr.ellipse((x - 2, y - 2, x + 2, y + 2), fill=(160, 0, 0))
    for p in divs_at[0]:
        if p in cent: x, y = cent[p]; dr.ellipse((x - 2, y - 2, x + 2, y + 2), fill=(0, 0, 0))
    k = 0
    for ti, t in enumerate(theatres):
        for ci, c in enumerate(t["chunks"]):
            yy, xx = np.nonzero(np.isin(sub, c))
            if len(xx):
                share = t["budget"] * len(c) // max(1, len(t["provinces"]))
                aid = t["ids"][ci] if "ids" in t else ci + 1
                dr.text((xx.mean() * scale, yy.mean() * scale), f"T{ti + 1} A{aid} {share}d", fill=(0, 0, 0))
            k += 1
    dr.rectangle((0, 0, im.size[0], 14), fill=(255, 255, 255))
    dr.text((4, 1), title, fill=(0, 0, 0))
    out = HERE / name
    im.save(out)
    print("wrote", out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("rec", nargs="?")
    ap.add_argument("--country", required=True)
    ap.add_argument("--date")
    ap.add_argument("--days", type=int, default=0)
    ap.add_argument("--step", type=int, default=1, help="days between runs (sections carry forward each run)")
    ap.add_argument("--verbose", action="store_true", help="armies and section sizes every step")
    a = ap.parse_args()
    import _engine  # noqa: F401  (engine tools on the path)
    import rec as recmod
    rec = Path(recmod.find(a.rec))
    ids, adj, water, impassable, owner0 = load_map()
    tick = tick_of(rec, a.date) if a.date else None
    if tick is None:
        tick = max(int(r["tick"]) for r in rows(rec / "plans.tsv"))
    ap_step = max(1, a.step)
    reg = json5(DATA / "hoi4_regions.json5")
    capital = reg["capitals"].get(a.country, 0)
    prev, next_id, first, fresh_first = {}, [1], None, None
    xy = {int(r["id"]): (int(r["x"]), int(r["y"])) for r in csv.DictReader(open(DATA / "hoi4_province_centres.csv"), delimiter=";")}
    def centres(army):
        acc = defaultdict(list)
        for p, aid in army.items():
            if p in xy: acc[aid].append(xy[p])
        return {aid: (sum(x for x, _ in v) / len(v), sum(y for _, y in v) / len(v)) for aid, v in acc.items()}
    seen_ids, shifts, fshifts, transfers, last_cen, last_fcen = set(), [], [], [], None, None
    days = list(range(0, a.days + 1, ap_step)) if a.days else [0]
    for k, day in enumerate(days):
        t = tick + day * 24
        ctrl, divs, wars = state_at(rec, t, owner0)
        theatres, total, reserve, own = plan(a.country, ctrl, divs, wars, adj, water, impassable, capital, prev, next_id)
        army = {p: th["ids"][ci] for th in theatres for ci, c in enumerate(th["chunks"]) for p in c}
        if first is None: first = army
        fresh, _, _, _ = plan(a.country, ctrl, divs, wars, adj, water, impassable, capital, {}, [1])
        fresh_army = {p: th["ids"][ci] for th in fresh for ci, c in enumerate(th["chunks"]) for p in c}
        cen = centres(army); fcen = centres(fresh_army)
        seen_ids.update(cen)
        if last_cen is not None:
            for aid, c in cen.items():
                if aid in last_cen:
                    d = math.dist(c, last_cen[aid]) * 7.114
                    (transfers if d > 600 else shifts).append(d)
            for aid, c in fcen.items():  # fresh: each section to the nearest earlier fresh section
                if last_fcen: fshifts.append(min(math.dist(c, o) for o in last_fcen.values()) * 7.114)
        last_cen, last_fcen = cen, fcen
        prev = army
        if a.verbose:
            print(day, ' | '.join(' '.join(f'A{aid}:{len(c)}' for aid, c in zip(th['ids'], th['chunks'])) + (' P' if th['pocket'] else '') for th in theatres))
        if k not in (0, len(days) - 1): continue
        enemies = wars.get(a.country, set())
        date = next((r["date"] for r in rows(rec / "divisions.tsv") if int(r["tick"]) >= t), "?")
        print(f"\n== {a.country} {date} (tick {t}): {total} divisions outside pockets, enemies {sorted(enemies)}, reserve {reserve}")
        for i, th in enumerate(theatres):
            sizes = [f"A{aid}:{len(c)}" for aid, c in zip(th["ids"], th["chunks"])]
            print(f"  T{i + 1}{' MAIN' if th['main'] else ''}{' POCKET' if th['pocket'] else ''}: {len(th['provinces'])} front provinces vs {','.join(th['faces'])}, "
                  f"facing {th['facing']} div, need {th['need']}, score {th['score']:.2f}, budget {th['budget']}, "
                  f"sections {' '.join(sizes)}{' (carried)' if th.get('carried') else ''}")
        enemy_pos = [p for tag, p in divs.values() if tag in enemies]
        render(f"ai_armies_{a.country}_{date[:10]}.png", a.country, theatres, ctrl, (own, enemy_pos), ids, water, enemies,
               f"{a.country} {date}: theatres (T) and army sections (A, budget); black own divisions, red enemy")
    if a.days:
        print(f"\n== {a.days} days, step {ap_step}: army ids seen {sorted(seen_ids)}; per step mean section-centre shift "
              f"{(sum(shifts) / len(shifts)) if shifts else 0:.0f} km (max {max(shifts) if shifts else 0:.0f}), "
              f"fresh even split each step {(sum(fshifts) / len(fshifts)) if fshifts else 0:.0f} km (max {max(fshifts) if fshifts else 0:.0f}); "
              f"army transfers to another theatre (over 600 km) {len(transfers)}: {[round(x) for x in transfers]}")
        both = [p for p in prev if p in first]
        same = sum(1 for p in both if prev[p] == first[p])
        print(f"\n== stability over {a.days} days (step {ap_step}): {len(both)} front provinces both times, "
              f"{same / max(1, len(both)):.0%} kept their army (carried forward)")
        # a fresh split at the end, ids matched to the start by overlap, for comparison
        match = {}
        for aid in set(fresh_army.values()):
            ps = [p for p, x in fresh_army.items() if x == aid]
            votes = defaultdict(int)
            for p in ps:
                if p in first: votes[first[p]] += 1
            match[aid] = max(votes, key=votes.get) if votes else None
        same_f = sum(1 for p in both if p in fresh_army and match.get(fresh_army[p]) == first[p])
        print(f"   a fresh even split instead: {same_f / max(1, len(both)):.0%} kept their army")

if __name__ == "__main__":
    main()
