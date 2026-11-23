"""Prototype: how dangerous each front is to the homeland (Docs/ai-layers.md, "Defence weight").

    python Tools/prototypes/ai_armies/defence.py [REC] --country GER [--date "1943-03-04"]

The attack side scores a theatre by the cheapest route on to the ENEMY's victory points. This is the mirror: the
cheapest route an enemy standing on our front would take to OUR core victory points (victory points in states
we have a core on, that we still control), over land we control. A front with a cheap route home is dangerous
(central Germany against the Soviets); one with a long walk through non-core land is not (Finland -> Sweden ->
Denmark -> Germany). Here the cost of a step is 1 per province plus 1/2 for a small/large river (the C# uses the
terrain's movement cost and attack modifier too); each victory point starts at 8 / (1 + VP / 5) like the attack
side, so a capital pulls harder than a village.

Prints per theatre: front provinces, enemy divisions facing, the home route cost, the danger and what the
holding need would be with it; renders defence_<TAG>_<date>.png (our land shaded by its route cost home, core
victory points as dots, theatres outlined by danger).
"""
import argparse, csv, heapq, math, sys
from collections import defaultdict
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from theatres import DATA, ROOT, json5, load_map, plan, rows, state_at, tick_of  # noqa: E402

OBJECTIVE_BASE = 8.0
HOME_SCALE = 10.0   # danger = HOME_SCALE / (HOME_SCALE + cost): 0.5 at a route home of 10


def home_costs(country, ctrl, adj, water, impassable, cores_vp, crossings):
    """Cheapest route cost from each province we control to our core victory points (Dijkstra from the VPs)."""
    land = {p for p, c in ctrl.items() if c == country and p not in water and p not in impassable and p in adj}
    cost, heap = {}, []
    for p, vp in cores_vp.items():
        if p in land:
            c = OBJECTIVE_BASE / (1 + vp / 5) + 1
            if c < cost.get(p, 1e18):
                cost[p] = c; heapq.heappush(heap, (c, p))
    while heap:
        c, p = heapq.heappop(heap)
        if c > cost[p]: continue
        for n in adj[p]:
            if n not in land: continue
            nc = c + 1 + crossings.get((min(p, n), max(p, n)), 0)
            if nc < cost.get(n, 1e18):
                cost[n] = nc; heapq.heappush(heap, (nc, n))
    return cost


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("rec", nargs="?")
    ap.add_argument("--country", required=True)
    ap.add_argument("--date")
    a = ap.parse_args()
    import _engine  # noqa: F401
    import rec as recmod
    rec = Path(recmod.find(a.rec))
    ids, adj, water, impassable, owner0 = load_map()
    tick = tick_of(rec, a.date) if a.date else max(int(r["tick"]) for r in rows(rec / "plans.tsv"))
    ctrl, divs, wars = state_at(rec, tick, owner0)
    reg = json5(DATA / "hoi4_regions.json5")
    sd = json5(DATA / "hoi4_state_data.json5")["states"]
    cores_vp = {}
    for s, info in sd.items():
        if a.country in info.get("cores", []):
            for p, vp in info.get("vps", {}).items():
                cores_vp[int(p)] = vp
    crossings = {}
    for x, y, size in json5(DATA / "hoi4_river_crossings.json5")["crossings"]:
        crossings[(min(x, y), max(x, y))] = size
    capital = reg["capitals"].get(a.country, 0)
    theatres, total, reserve, own = plan(a.country, ctrl, divs, wars, adj, water, impassable, capital)
    cost = home_costs(a.country, ctrl, adj, water, impassable, cores_vp, crossings)
    date = next((r["date"] for r in rows(rec / "divisions.tsv") if int(r["tick"]) >= tick), "?")
    print(f"== {a.country} {date}: {total} divisions, {len(cores_vp)} core victory points "
          f"({sum(1 for p in cores_vp if ctrl.get(p) == a.country)} still ours)")
    out = []
    for i, th in enumerate(theatres):
        cs = [cost[p] for p in th["provinces"] if p in cost]
        c = min(cs) if cs else math.inf
        danger = HOME_SCALE / (HOME_SCALE + c) if cs else 0.0
        out.append((i, th, c, danger))
    for i, th, c, danger in sorted(out, key=lambda x: -x[3]):
        need_now = max(len(th["provinces"]), th["facing"])
        need_new = max(len(th["provinces"]), math.ceil(th["facing"] * (0.5 + danger)))
        cx, cy = np.mean([centre(p)[0] for p in th["provinces"]]), np.mean([centre(p)[1] for p in th["provinces"]])
        print(f"  T{i + 1} at ({cx:.0f},{cy:.0f}) vs {','.join(th['faces'])}: {len(th['provinces'])}p facing {th['facing']}, "
              f"route home {c:.1f}, danger {danger:.2f}, need {need_now} -> {need_new}")
    render(f"defence_{a.country}_{date[:10]}.png", a.country, ids, ctrl, cost, cores_vp, out, water)


_XY = None


def centre(p):
    global _XY
    if _XY is None:
        _XY = {int(r["id"]): (int(r["x"]), int(r["y"])) for r in csv.DictReader(open(DATA / "hoi4_province_centres.csv"), delimiter=";")}
    return _XY.get(p, (0, 0))


def render(name, country, ids, ctrl, cost, cores_vp, out, water):
    h, w = ids.shape
    img = np.full((h, w, 3), 235, np.uint8)
    maxid = int(ids.max()) + 1
    col = np.zeros((maxid, 3), np.uint8); col[:] = (235, 235, 235)
    for p in water:
        if p < maxid: col[p] = (170, 190, 215)
    for p, c in ctrl.items():
        if p < maxid and c != country and p not in water: col[p] = (205, 205, 205)
    for p, c in cost.items():
        k = max(0.0, min(1.0, c / 40.0))  # dark = close to home
        col[p] = (int(60 + 180 * k), int(40 + 160 * k), int(40 + 80 * k))
    for _, th, _, danger in out:
        g = int(255 * (1 - danger))
        for p in th["provinces"]:
            if p < maxid: col[p] = (255, g, 0)
    img = col[ids]
    xs = [centre(p) for p in cost] or [(0, 0)]
    x0, x1 = max(0, min(x for x, _ in xs) - 150), min(w, max(x for x, _ in xs) + 150)
    y0, y1 = max(0, min(y for _, y in xs) - 150), min(h, max(y for _, y in xs) + 150)
    im = Image.fromarray(img[y0:y1, x0:x1])
    d = ImageDraw.Draw(im)
    for p, vp in cores_vp.items():
        x, y = centre(p); r = 2 + min(6, vp // 5)
        d.ellipse([x - x0 - r, y - y0 - r, x - x0 + r, y - y0 + r], outline=(0, 0, 0), fill=(255, 255, 255))
    for i, th, c, danger in out:
        x, y = np.mean([centre(p)[0] for p in th["provinces"]]), np.mean([centre(p)[1] for p in th["provinces"]])
        d.text((x - x0, y - y0), f"T{i + 1} {danger:.2f}", fill=(0, 0, 0))
    im.save(HERE / name)
    print(f"  -> {HERE / name}")


if __name__ == "__main__":
    main()
