"""Prototype: HOI4 ports (naval bases) as supply hubs, the sea routes between them, and how they look on the map.

Rules (NSupply defines and the Logistics wiki page):
  - A naval base is a supply hub. One its country's capital reaches by rail works like any hub: throughput
    RAILWAY_BASE_FLOW + RAILWAY_FLOW_PER_LEVEL x the rail bottleneck (10 + 5 per level).
  - A port with no rail to the capital is supplied by sea from another of the country's connected ports when
    ships can sail between the two (the same body of water; no navy or blockade yet). Its throughput is the
    smallest along the way: NAVAL_BASE_FLOW + NAVAL_FLOW_PER_LEVEL x level (5 + 3 per level; 8 at level 1, 35 at
    10) of both ports, and what the source port gets itself.
  - Rail is preferred (HOI4's tooltip for Toulon in 1936: one supply transfer to Bizerte, with Algiers and Oran
    fed from Bizerte by rail). One sea route opens at a time: the one that carries the most, then the shortest.
    A port's route comes from the connected port nearest by sea among those that carry at least half of what its
    best one could (Dakar hangs on Casablanca, not on bigger Bizerte; a small port next door doesn't throttle).
    Hubs and ports with rail to the port it reaches are connected through it, capped by its throughput, before
    the next route is chosen; their ports can supply further ports. A rail network across the sea gets one route.
  - Supply spreads from a port like from a hub, with the naval base numbers: NAVAL_BASE_INITIAL_SUPPLY_FLOW 3.3
    (+ 0.34 per rail level when it is connected by rail), starting penalty 0.84, added penalty 1.1 per province.
    A province with a supply hub and a naval base spreads as a hub (the better numbers).

Data: map/hoi4_ports.json5 (hoi4_convert.py write_ports: each coastal province's naval base spot and sea
province, every water province's visual centre) and the 1936 naval base levels in hoi4_state_data.json5.

Prints each country's ports by connection and renders a preview: port icons (blue for the viewer and grey for
others, "0/LEVEL" beside the viewer's: ships repairing / level), and the viewer's sea routes as HOI4's white
convoy ships (gfx/maparrows trade_line), over the colormap.

  python Tools/prototypes/ports/sea_supply.py [--country GER] [--crop x0,y0,x1,y1] [--scale 8]
"""
import argparse
import heapq
import math
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.spatial import cKDTree

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE.parent / "supply"))
import supply_flow  # noqa: E402
from rail_network import load_json5, widest_paths  # noqa: E402
from render_rails import chaikin, densify, province_ids  # noqa: E402

RAILWAY_BASE_FLOW, RAILWAY_FLOW_PER_LEVEL = 10.0, 5.0
NAVAL_BASE_FLOW, NAVAL_FLOW_PER_LEVEL = 5.0, 3.0

# look, in map px (checked against an HOI4 screenshot of Bornholm and Kolberg: the two ports are 24 map px apart
# and 400 screen px in it, so 17 screen px per map px; the ships repeat every 50 px and are 12 px wide)
CONVOY_TILE = 3.0    # map px per ship
CONVOY_WIDTH = 0.75  # map px
ICON_SCREEN_PX = 27  # the icon is drawn at its own size on screen, whatever the zoom


def port_flow(level):
    return NAVAL_BASE_FLOW + NAVAL_FLOW_PER_LEVEL * level


_SEA_DIST = {}


def sailing(a, b, sea_adj, centre):
    """Whole map px over water between two sea provinces' centres (None: no way); kept per source."""
    if a not in _SEA_DIST:
        dist, heap = {a: 0}, [(0, a)]
        while heap:
            d, u = heapq.heappop(heap)
            if d != dist[u]:
                continue
            for v in sea_adj[u]:
                nd = d + int(math.dist(centre[u], centre[v]))
                if v not in dist or nd < dist[v]:
                    dist[v] = nd
                    heapq.heappush(heap, (nd, v))
        _SEA_DIST[a] = dist
    return _SEA_DIST[a].get(b)


def connect(tag, m, ports, levels, sea_adj, centre):
    """One country's hubs and ports: {province: (capacity, rail level, how, source)} with how in
    capital / rail / sea / rail-from-port; source = the port it hangs on (sea, rail-from-port)."""
    cap = m.regions["capitals"].get(tag)
    own = {p for p, o in m.prov_owner.items() if o == tag}
    if cap not in own:
        return {}
    rail = defaultdict(list)
    for (a, b), l in m.links.items():
        if a in own and b in own:
            rail[a].append((b, l))
            rail[b].append((a, l))
    nodes = {p for p in m.hubs if p in own} | {p for p in levels if p in own and levels[p] > 0}
    out = {cap: (m.capacity.get(cap, 0.0), max([l for _, l in rail[cap]], default=0), "capital", None)}

    def spread(root):
        reach = widest_paths(root, rail)
        limit = None if root == cap else out[root][0]
        for p in sorted(nodes):
            if p in out or p not in reach:
                continue
            c = RAILWAY_BASE_FLOW + RAILWAY_FLOW_PER_LEVEL * reach[p]
            out[p] = (c if limit is None else min(c, limit), reach[p], "rail" if root == cap else "rail-from-port",
                      None if root == cap else root)

    spread(cap)
    while True:
        # one sea route a round: the one that carries the most, then the shortest; then rail from the port reached
        best = None
        for p in sorted(nodes):
            if p in out or levels.get(p, 0) <= 0 or p not in ports:
                continue
            # the source nearest by sea among those that carry at least half of what the best one could
            cands = []
            for s in sorted(q for q in out if levels.get(q, 0) > 0 and q in ports):
                way = sailing(ports[s][2], ports[p][2], sea_adj, centre)
                if way is None:
                    continue
                c = min(out[s][0], port_flow(levels[s]), port_flow(levels[p]))
                cands.append(((way, (ports[s][0] - ports[p][0]) ** 2 + (ports[s][1] - ports[p][1]) ** 2), s, c))
            if not cands:
                continue
            most = max(c for _, _, c in cands)
            d, s, c = min(x for x in cands if x[2] * 2 >= most)
            key = (-c, d, p, s)
            if best is None or key < best[0]:
                best = (key, c, p, s)
        if best is None:
            return out
        _, c, p, s = best
        out[p] = (c, 0, "sea", s)
        spread(p)


def sea_route(a, b, sea_adj, centre):
    """Shortest way over water from sea province a to b (distance between centres)."""
    dist, prev, heap = {a: 0.0}, {}, [(0.0, a)]
    while heap:
        d, u = heapq.heappop(heap)
        if u == b:
            break
        if d > dist.get(u, 1e18):
            continue
        for v in sea_adj[u]:
            nd = d + math.dist(centre[u], centre[v])
            if nd < dist.get(v, 1e18):
                dist[v] = nd
                prev[v] = u
                heapq.heappush(heap, (nd, v))
    if b not in dist:
        return None
    path = [b]
    while path[-1] != a:
        path.append(prev[path[-1]])
    return path[::-1]


def route_curve(src, dst, ports, sea_adj, centre):
    """The drawn line: source spot, the sea provinces' centres between the two, destination spot; smoothed."""
    path = sea_route(ports[src][2], ports[dst][2], sea_adj, centre)
    if path is None:
        return None
    pts = [ports[src][:2]]
    # the first and last sea provinces touch the ports: their centres would be a detour, the ports' spots stand in
    pts += [centre[s] for s in path[1:-1]]
    pts.append(ports[dst][:2])
    pts = np.array(pts, float)
    return densify(chaikin(pts, 3), 0.05) if len(pts) > 2 else densify(pts, 0.05)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=supply_flow.DEFAULT_HOI4)
    ap.add_argument("--data", default="Hoi4Data")
    ap.add_argument("--country", default="GER")
    ap.add_argument("--list", nargs="*", default=["GER", "ENG", "FRA", "ITA", "JAP", "USA", "SOV", "DEN", "SWE"])
    ap.add_argument("--crop", default="3060,330,3330,470", help="x0,y0,x1,y1 map px, y down (default the southern Baltic)")
    ap.add_argument("--scale", type=int, default=8)
    ap.add_argument("--out", default=str(HERE / "sea_supply_preview.png"))
    args = ap.parse_args()
    hoi4, data = Path(args.hoi4), Path(args.data)

    m = supply_flow.compute(hoi4, data)
    pj = load_json5(data / "map" / "hoi4_ports.json5")
    ports = {int(p): (v[0], v[1], int(v[2])) for p, v in pj["ports"].items()}
    centre = {int(p): tuple(v) for p, v in pj["sea"].items()}
    levels = {}
    for sid, s in m.sdata.items():
        for p, b in s.get("provincial", {}).items():
            if b.get("naval_base", 0) > 0:
                levels[int(p)] = b["naval_base"]
    no_spot = sorted(p for p in levels if p not in ports)
    print(f"{len(ports)} naval base spots, {len(levels)} naval bases in 1936; bases without a spot: {no_spot}")
    print(f"bases in a supply hub's province: {sorted(p for p in levels if p in m.hubs)[:20]}")
    bad_sea = sorted(p for p, v in ports.items() if v[2] not in centre)
    print(f"spots whose sea province isn't water: {bad_sea[:10]}")

    # water graph and its bodies of water
    sea_adj = defaultdict(set)
    for a, b in m.pairs:
        if a in centre and b in centre:
            sea_adj[a].add(b)
            sea_adj[b].add(a)
    sea_comp, n_comp = {}, 0
    for s in sorted(centre):
        if s in sea_comp:
            continue
        n_comp += 1
        stack = [s]
        sea_comp[s] = n_comp
        while stack:
            u = stack.pop()
            for v in sea_adj[u]:
                if v not in sea_comp:
                    sea_comp[v] = n_comp
                    stack.append(v)
    sizes = defaultdict(int)
    for c in sea_comp.values():
        sizes[c] += 1
    print(f"{len(centre)} water provinces in {n_comp} bodies of water; the largest {sorted(sizes.values(), reverse=True)[:6]}")

    results = {}
    for tag in sorted(set(args.list) | {args.country}):
        res = results[tag] = connect(tag, m, ports, levels, sea_adj, centre)
        own_ports = sorted(p for p in levels if m.prov_owner.get(p) == tag)
        how = defaultdict(list)
        for p in own_ports:
            how[res[p][2] if p in res else "cut"].append(p)
        print(f"\n{tag}: {len(own_ports)} ports: " + ", ".join(f"{k} {len(v)}" for k, v in sorted(how.items())))
        for p in how.get("sea", [])[:8]:
            c, _, _, s = res[p]
            print(f"  port {p} (level {levels[p]}) by sea from {s} (level {levels[s]}, capacity {res[s][0]:.0f}): {c:.0f}")
        hubs_via = [p for p, v in res.items() if v[2] == "rail-from-port"]
        if hubs_via:
            print(f"  by rail from a sea-supplied port: {len(hubs_via)} {hubs_via[:8]}")
        if how.get("cut"):
            print(f"  cut off: {how['cut'][:10]}")

    # --- preview -------------------------------------------------------------------------------------
    x0, y0, x1, y1 = (int(v) for v in args.crop.split(","))
    S = args.scale
    ids = province_ids(hoi4)
    H, W = ids.shape
    cmap = Image.open(data / "gfx" / "map" / "colormap.png").convert("RGB").resize((W, H), Image.BILINEAR)
    bg = cmap.crop((x0, y0, x1, y1)).resize(((x1 - x0) * S, (y1 - y0) * S), Image.BILINEAR)
    out = np.asarray(bg, np.float32) / 255
    water = np.isin(ids[y0:y1, x0:x1], list(centre))
    out[np.kron(water, np.ones((S, S), bool))] *= np.array([0.55, 0.7, 0.95])  # the colormap's sea is pale: tint it
    big = np.kron(ids[y0:y1, x0:x1], np.ones((S, S), dtype=ids.dtype))
    edge = np.zeros(big.shape, bool)
    edge[:, 1:] |= big[:, 1:] != big[:, :-1]
    edge[1:, :] |= big[1:, :] != big[:-1, :]
    out[edge] *= 0.85

    # sea routes: the convoy ship tiled along the curve, pointing at the port supplied
    ship = np.asarray(Image.open(data / "gfx" / "map" / "ports" / "convoy.png").convert("RGBA"), np.float32) / 255
    th, tw = ship.shape[:2]
    gy, gx = np.mgrid[0:out.shape[0], 0:out.shape[1]]
    mx, my = x0 + (gx + 0.5) / S, y0 + (gy + 0.5) / S
    res = results[args.country]
    routes = 0
    for p, (c, _, how, src) in sorted(res.items()):
        if how != "sea":
            continue
        curve = route_curve(src, p, ports, sea_adj, centre)
        if curve is None:
            continue
        if curve[:, 0].max() < x0 or curve[:, 0].min() > x1 or curve[:, 1].max() < y0 or curve[:, 1].min() > y1:
            continue
        routes += 1
        seg = np.diff(curve, axis=0)
        arc = np.concatenate([[0], np.cumsum(np.linalg.norm(seg, axis=1))])
        tang = np.vstack([seg, seg[-1:]])
        tang /= np.maximum(np.linalg.norm(tang, axis=1, keepdims=True), 1e-9)
        half = CONVOY_WIDTH / 2
        sel = (mx >= curve[:, 0].min() - 2) & (mx <= curve[:, 0].max() + 2) & (my >= curve[:, 1].min() - 2) & (my <= curve[:, 1].max() + 2)
        qx, qy = mx[sel], my[sel]
        dist, idx = cKDTree(curve).query(np.column_stack([qx, qy]))
        near = dist <= half * 1.2
        idx = idx[near]
        dx, dy = qx[near] - curve[idx, 0], qy[near] - curve[idx, 1]
        along = dx * tang[idx, 0] + dy * tang[idx, 1]
        across = -dx * tang[idx, 1] + dy * tang[idx, 0]
        u = (arc[idx] + along) / CONVOY_TILE
        v = across / CONVOY_WIDTH + 0.5
        ok = (v >= 0) & (v < 1)
        tu = ((u[ok] % 1.0) * tw).astype(int) % tw
        tv = np.clip(v[ok] * th, 0, th - 0.001).astype(int)
        a = ship[tv, tu, 3:4] * 0.9
        rows, cols = gy[sel][near][ok], gx[sel][near][ok]
        out[rows, cols] = out[rows, cols] * (1 - a) + ship[tv, tu, :3] * a

    canvas = Image.fromarray((np.clip(out, 0, 1) * 255).astype(np.uint8)).convert("RGBA")
    gfx = data / "gfx" / "map" / "ports"
    icon, grey = Image.open(gfx / "port.png").convert("RGBA"), Image.open(gfx / "port_grey.png").convert("RGBA")
    text = Image.open(gfx / "text.png").convert("RGBA")
    cw, chars = text.width // 11, "0123456789/"
    shown = 0
    for p, lvl in sorted(levels.items()):
        x, y, _ = ports[p]
        if not (x0 <= x < x1 and y0 <= y < y1):
            continue
        shown += 1
        mine = m.prov_owner.get(p) == args.country
        sx, sy = int((x - x0) * S), int((y - y0) * S)
        im = icon if mine else grey
        canvas.alpha_composite(im, (sx - im.width // 2, sy - im.height + 3))  # the pointer under the button on the spot
        if mine:
            tx = sx + im.width // 2 + 1
            for ch in f"0/{lvl}":
                i = chars.index(ch)
                canvas.alpha_composite(text.crop((i * cw, 0, (i + 1) * cw, text.height)), (tx, sy - im.height // 2 - 5))
                tx += cw - 1
    canvas.convert("RGB").save(args.out)
    print(f"\nwrote {args.out} ({canvas.width} x {canvas.height}): {shown} ports, {routes} sea routes of {args.country}")


if __name__ == "__main__":
    main()
