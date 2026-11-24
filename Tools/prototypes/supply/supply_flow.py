"""Prototype: HOI4 supply in every land province at the 1936 start (hub supply + state supply).

Builds on rail_network.py (railways, hubs, each hub's rail bottleneck to the capital). New here:

HUB SUPPLY (how far each hub's supply reaches over land), from NSupply in common/defines/00_defines.lua:
  Each source starts with an initial flow and loses a penalty on every province step away from it:
      flow(source) = INITIAL + NODE_FLOW_BONUS_PER_RAIL_LEVEL * rail level   (capital: best rail level at it;
                                                                            hub: its bottleneck to the capital)
      step to the d-th province (d = 1, 2, ...):
          penalty = STARTING + ADDED * falloff(d) + terrain(target)       terrain: supply_flow_penalty_factor
          penalty *= 1 - SUPPLY_FLOW_DROP_REDUCTION_AT_MAX_INFRA * infra(source state) / 5
          penalty += SUPPLY_FLOW_PENALTY_CROSSING_RIVERS                  when a river lies between the two
          falloff(d) = max(MIN_PENALTY_SCALE, SCALAR / (1 + e^(-K (d - MIDPOINT))))   (the "logistics curve")
      flow(next) = flow(prev) - penalty; it stops below SUPPLY_FLOW_REDUCTION_THRESHOLD.
  Sources: the capital (CAPITAL_* defines) and every hub connected to it by own rail (NODE_* defines).
  Flow only crosses provinces the country controls (land borders and straits). A province's hub supply is the SUM
  of its best three sources' flows (--combine max takes the best one only); the best source is the hub it draws from.
  Motorization is left out; --weather scales every penalty (bad weather > 1).

  This is OUR reading of the defines (the wiki gives no formula). Checked against three HOI4 tooltips in the Ruhr
  (1936-01-01, light rain): Dortmund hub 7.06, a Westfalen neighbour 6.37, Moselland by Arnhem 4.08. Summing three
  sources with --weather 1.1..1.2 gives 7.07..7.27, 6.10..6.37, 4.72..5.11; taking the best source alone gives at most
  3.14 at the hub, so HOI4 sums. --explain PROVINCE prints one source's chain; --probe PROV.. each source's flow.

STATE SUPPLY (a pool per state that every unit in the state shares, drawn first), from the wiki and defines:
  infrastructure * INFRA_TO_SUPPLY + manpower / 1e6 * AVAILABLE_MANPOWER_STATE_SUPPLY
  + for each VP: VP_TO_SUPPLY_BASE + VP_TO_SUPPLY_BONUS_CONVERSION * its value
  + local_supplies (history/states) * SUPPLY_BASE_MULT;  rounded to 0.1.
  Matches the tooltips exactly: Westfalen 2.60 (1.20 + 0.92 + Dortmund 0.45), Rhineland 3.50.

HUB CAPACITY (how much all provinces drawing on a hub can take together): 10 + 5 * bottleneck (rail_network.py);
the capital's is the capital supply. Demand against it needs divisions: the next step.

  python Tools/prototypes/supply/supply_flow.py [--country GER SOV ...] [--crop x0,y0,x1,y1] [--explain PROV]
Writes Tools/prototypes/supply/supply_preview.png (total supply per province, HOI4-style red..green).
"""
import argparse
import heapq
import math
import re
from collections import defaultdict
from types import SimpleNamespace
from pathlib import Path

import numpy as np
from PIL import Image

from rail_network import (DEFAULT_HOI4, load_json5, read_rails, read_hubs, province_adjacency, widest_paths,
                          RAILWAY_BASE_FLOW, RAILWAY_FLOW_PER_LEVEL, CAPITAL_BASE, CAP_CIV, CAP_MIL, CAP_DOCK)

HERE = Path(__file__).parent


def read_defines(hoi4: Path):
    text = (hoi4 / "common" / "defines" / "00_defines.lua").read_text(encoding="utf-8", errors="replace")
    block = text[text.index("NSupply = {"):]
    block = block[:block.index("\n}")]
    d = {}
    for m in re.finditer(r"^\s*([A-Z_0-9]+)\s*=\s*(-?[\d.]+)", block, flags=re.M):
        d[m.group(1)] = float(m.group(2))
    return d


def read_definitions(hoi4: Path):
    """id -> (land?, terrain), and the RGB key -> id lookup."""
    info, keys = {}, {}
    for line in (hoi4 / "map" / "definition.csv").read_text(encoding="latin-1").splitlines():
        c = line.split(";")
        if len(c) > 6 and c[0].isdigit():
            pid = int(c[0])
            info[pid] = (c[4] == "land", c[6])
            keys[(int(c[1]) << 16) | (int(c[2]) << 8) | int(c[3])] = pid
    return info, keys


def read_terrain_penalties(hoi4: Path):
    text = (hoi4 / "common" / "terrain" / "00_terrain.txt").read_text(encoding="latin-1")
    cats = text[text.index("categories"):]
    out, depth, name = {}, 0, None
    for tok in re.finditer(r"([A-Za-z_]+)\s*=\s*\{|\{|\}|supply_flow_penalty_factor\s*=\s*(-?[\d.]+)", cats):
        s = tok.group(0)
        if s.startswith("supply_flow"):
            out[name] = float(tok.group(2))
        elif s.endswith("{"):
            depth += 1
            if depth == 2 and tok.group(1):
                name = tok.group(1)
        else:
            depth -= 1
            if depth == 0:
                break
    return out


def read_local_supplies(hoi4: Path):
    out = {}
    for f in (hoi4 / "history" / "states").glob("*.txt"):
        t = f.read_text(encoding="utf-8-sig", errors="replace")
        sid = re.search(r"\bid\s*=\s*(\d+)", t)
        ls = re.search(r"\blocal_supplies\s*=\s*(-?[\d.]+)", t)
        if sid and ls:
            out[sid.group(1)] = float(ls.group(1))
    return out


class Flow:
    def __init__(self, D, terrain_pen, prov_terrain, prov_state, state_infra, rivers, weather=1.0):
        self.weather = weather
        self.D, self.terrain_pen, self.prov_terrain = D, terrain_pen, prov_terrain
        self.prov_state, self.state_infra, self.rivers = prov_state, state_infra, rivers

    def falloff(self, d):
        D = self.D
        s = D["SUPPLY_FLOW_DIST_LOGISTICS_FALLOFF_SCALAR"] / (
            1 + math.exp(-D["SUPPLY_FLOW_DIST_LOGISTICS_FALLOFF_K"] * (d - D["SUPPLY_FLOW_DIST_LOGISTICS_FALLOFF_MIDPOINT"])))
        return max(D["SUPPLY_FLOW_DIST_LOGISTICS_FALLOFF_MIN_PENALTY_SCALE"], s)

    def penalty(self, kind, src, dst, d):
        D = self.D
        pen = D[f"{kind}_STARTING_PENALTY_PER_PROVINCE"] + D[f"{kind}_ADDED_PENALTY_PER_PROVINCE"] * self.falloff(d)
        pen += self.terrain_pen.get(self.prov_terrain.get(dst, ""), 0.0)
        infra = self.state_infra.get(self.prov_state.get(src), 0)
        pen *= 1 - D["SUPPLY_FLOW_DROP_REDUCTION_AT_MAX_INFRA"] * min(infra, 5) / 5
        if (min(src, dst), max(src, dst)) in self.rivers:
            pen += D["SUPPLY_FLOW_PENALTY_CROSSING_RIVERS"]
        return pen * self.weather

    def spread(self, kind, source, start, graph):
        """Best flow from SOURCE to every province: {prov: (flow, hops, prev)}. Flow only decreases (penalties > 0),
        so a max-first label-setting search is exact for 'best remaining flow'."""
        thr = self.D["SUPPLY_FLOW_REDUCTION_THRESHOLD"]
        best = {source: (start, 0, None)}
        heap = [(-start, source)]
        done = set()
        while heap:
            f, u = heapq.heappop(heap)
            f = -f
            if u in done:
                continue
            done.add(u)
            d = best[u][1]
            for v in graph[u]:
                if v in done:
                    continue
                nf = f - self.penalty(kind, u, v, d + 1)
                if nf < thr:
                    continue
                if nf > best.get(v, (-1,))[0]:
                    best[v] = (nf, d + 1, u)
                    heapq.heappush(heap, (-nf, v))
        return best


def colour(v):
    """HOI4 supply map mode style: red (none) -> yellow -> green (plenty). v in supply points."""
    stops = [(0.0, (150, 30, 30)), (1.0, (215, 70, 40)), (2.5, (230, 200, 60)), (5.0, (120, 190, 70)), (9.0, (40, 140, 60))]
    if v <= stops[0][0]:
        return stops[0][1]
    for (a, ca), (b, cb) in zip(stops, stops[1:]):
        if v <= b:
            t = (v - a) / (b - a)
            return tuple(int(ca[i] + (cb[i] - ca[i]) * t) for i in range(3))
    return stops[-1][1]


def compute(hoi4: Path, data: Path, combine="sum", top=3, weather=1.0):
    """Every province's hub supply (and its sources) and every state's supply pool. Returns a namespace."""
    D = read_defines(hoi4)
    terrain_pen = read_terrain_penalties(hoi4)
    info, keys = read_definitions(hoi4)
    links, _ = read_rails(hoi4)
    hubs = read_hubs(hoi4)
    pairs, straits = province_adjacency(hoi4)
    local = read_local_supplies(hoi4)

    regions = load_json5(data / "map" / "hoi4_regions.json5")
    sdata = load_json5(data / "map" / "hoi4_state_data.json5")["states"]
    rivers = {(min(int(a), int(b)), max(int(a), int(b))) for a, b in
              re.findall(r"\[(\d+),\s*(\d+),\s*\d+\]", (data / "map" / "hoi4_river_crossings.json5").read_text())}
    impassable = set(str(s) for s in regions.get("impassable_states", []))
    prov_state, prov_owner = {}, {}
    for sid, provs in regions["states"].items():
        for p in provs:
            prov_state[p] = sid
            prov_owner[p] = sdata.get(sid, {}).get("owner", "")
    state_infra = {sid: s.get("buildings", {}).get("infrastructure", 0) for sid, s in sdata.items()}
    prov_terrain = {p: t for p, (land, t) in info.items()}

    # state supply pools
    state_supply = {}
    for sid, s in sdata.items():
        v = state_infra[sid] * D["INFRA_TO_SUPPLY"]
        v += s.get("manpower", 0) / 1e6 * D["AVAILABLE_MANPOWER_STATE_SUPPLY"]
        vps = s.get("vps", {})
        for vp in vps.values():  # each VP on its own (HOI4 tooltip: Essen 10 -> 0.70, Koln 5 -> 0.45)
            v += D["VP_TO_SUPPLY_BASE"] + D["VP_TO_SUPPLY_BONUS_CONVERSION"] * vp
        v += local.get(sid, 0.0) * D["SUPPLY_BASE_MULT"]
        state_supply[sid] = round(v, 1)  # the wiki: state supply is rounded to one decimal

    land_adj = defaultdict(set)
    for a, b in list(pairs) + list(straits):
        if a in prov_state and b in prov_state and prov_state[a] not in impassable and prov_state[b] not in impassable:
            land_adj[a].add(b)
            land_adj[b].add(a)

    flow = Flow(D, terrain_pen, prov_terrain, prov_state, state_infra, rivers, weather)
    contrib = defaultdict(list)  # prov -> [(flow, source)]
    source_kind = {}
    capacity = {}  # source -> throughput: capital supply, or 10 + 5 * rail bottleneck
    countries = sorted(set(regions["capitals"]))
    for tag in countries:
        cap = regions["capitals"][tag]
        own = {p for p, o in prov_owner.items() if o == tag}
        if cap not in own:
            continue
        rail = defaultdict(list)
        for (a, b), l in links.items():
            if a in own and b in own:
                rail[a].append((b, l))
                rail[b].append((a, l))
        reach = widest_paths(cap, rail)
        graph = {p: [q for q in land_adj[p] if q in own] for p in own}
        graph = defaultdict(list, graph)
        cap_level = max([l for _, l in rail[cap]], default=0)
        sources = [(cap, "CAPITAL", D["CAPITAL_INITIAL_SUPPLY_FLOW"] + D["NODE_FLOW_BONUS_PER_RAIL_LEVEL"] * cap_level)]
        for h in hubs:
            if h in own and h != cap and h in reach:
                sources.append((h, "NODE", D["NODE_INITIAL_SUPPLY_FLOW"] + D["NODE_FLOW_BONUS_PER_RAIL_LEVEL"] * reach[h]))
        civ = mil = dock = 0
        for sid, s in sdata.items():
            if s.get("owner") == tag:
                bl = s.get("buildings", {})
                civ += bl.get("industrial_complex", 0); mil += bl.get("arms_factory", 0); dock += bl.get("dockyard", 0)
        capacity[cap] = CAPITAL_BASE + CAP_CIV * civ + CAP_MIL * mil + CAP_DOCK * dock
        for h in hubs:
            if h in own and h != cap and h in reach:
                capacity[h] = RAILWAY_BASE_FLOW + RAILWAY_FLOW_PER_LEVEL * reach[h]
        for src, kind, start in sources:
            source_kind[src] = kind
            for p, (f, d, prev) in flow.spread(kind, src, start, graph).items():
                contrib[p].append((f, src))
    hub_supply = {}   # prov -> (supply, main source)
    for p, cs in contrib.items():
        cs.sort(reverse=True)
        total = cs[0][0] if combine == "max" else sum(f for f, _ in cs[:top])
        hub_supply[p] = (total, cs[0][1])
    return SimpleNamespace(**{k: v for k, v in locals().items() if k not in ('body',)})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=DEFAULT_HOI4)
    ap.add_argument("--data", default="Hoi4Data")
    ap.add_argument("--country", nargs="*", default=["GER", "SOV", "FRA", "ENG", "ITA", "POL", "USA", "JAP", "CHI"])
    ap.add_argument("--crop", default="2560,240,3600,900", help="x0,y0,x1,y1 in provinces.bmp pixels (default Europe)")
    ap.add_argument("--scale", type=float, default=1.0)
    ap.add_argument("--explain", type=int, help="print the flow chain into this province")
    ap.add_argument("--layer", choices=["total", "hub", "state"], default="total")
    ap.add_argument("--combine", choices=["max", "sum"], default="sum",
                    help="a province's hub supply: the best source, or the sum over sources (HOI4 lists several)")
    ap.add_argument("--top", type=int, default=3, help="with --combine sum: sources counted per province")
    ap.add_argument("--weather", type=float, default=1.0, help="multiplier on every step's penalty (bad weather > 1)")
    ap.add_argument("--probe", type=int, nargs="*", default=[], help="print each source's flow into these provinces")
    args = ap.parse_args()
    hoi4, data = Path(args.hoi4), Path(args.data)

    m = compute(hoi4, data, args.combine, args.top, args.weather)
    D, info, keys, links, hubs, regions = m.D, m.info, m.keys, m.links, m.hubs, m.regions
    sdata, rivers, impassable, prov_state, prov_owner = m.sdata, m.rivers, m.impassable, m.prov_state, m.prov_owner
    state_infra, prov_terrain, state_supply, land_adj = m.state_infra, m.prov_terrain, m.state_supply, m.land_adj
    flow, contrib, source_kind, hub_supply = m.flow, m.contrib, m.source_kind, m.hub_supply
    for p in args.probe:
        cs = contrib.get(p, [])
        print(f"probe {p} ({prov_owner.get(p)}, state {prov_state.get(p)}, {prov_terrain.get(p)}): "
              f"hub {hub_supply.get(p, (0,))[0]:.2f} = " + " + ".join(f"{f:.2f}@{s}" for f, s in cs[:6])
              + f"; state pool {state_supply.get(prov_state.get(p), 0):.2f}")

    # per-country report
    for tag in args.country:
        own = [p for p, o in prov_owner.items() if o == tag and prov_state[p] not in impassable]
        if not own:
            continue
        hs = np.array([hub_supply.get(p, (0, None))[0] for p in own])
        states = {prov_state[p] for p in own}
        ss = sum(state_supply[s] for s in states)
        by_src = defaultdict(int)
        for p in own:
            if p in hub_supply:
                by_src[hub_supply[p][1]] += 1
        print(f"{tag}: {len(own)} provinces, hub supply in {np.mean(hs > 0) * 100:.0f}% "
              f"(>=1 in {np.mean(hs >= 1) * 100:.0f}%, >=3 in {np.mean(hs >= 3) * 100:.0f}%), "
              f"mean {hs.mean():.2f}, max {hs.max():.2f}; {len(by_src)} sources; "
              f"state supply {ss:.1f} over {len(states)} states")

    if args.explain:
        p = args.explain
        if p not in hub_supply:
            print(f"province {p}: no hub supply (owner {prov_owner.get(p)})")
        else:
            f, src = hub_supply[p]
            tag = prov_owner[p]
            own = {q for q, o in prov_owner.items() if o == tag}
            graph = defaultdict(list, {q: [r for r in land_adj[q] if r in own] for q in own})
            kind = source_kind[src]
            start = None
            # recompute the source's spread to walk the chain back
            rail = defaultdict(list)
            for (a, b), l in links.items():
                if a in own and b in own:
                    rail[a].append((b, l)); rail[b].append((a, l))
            if kind == "CAPITAL":
                start = D["CAPITAL_INITIAL_SUPPLY_FLOW"] + D["NODE_FLOW_BONUS_PER_RAIL_LEVEL"] * max([l for _, l in rail[src]], default=0)
            else:
                start = D["NODE_INITIAL_SUPPLY_FLOW"] + D["NODE_FLOW_BONUS_PER_RAIL_LEVEL"] * widest_paths(regions["capitals"][tag], rail)[src]
            best = flow.spread(kind, src, start, graph)
            chain = [p]
            while best[chain[-1]][2] is not None:
                chain.append(best[chain[-1]][2])
            chain.reverse()
            print(f"province {p} ({tag}): hub supply {f:.2f} from {kind.lower()} {src}, start {start:.2f}")
            for a, b in zip(chain, chain[1:]):
                d = best[b][1]
                print(f"  {a} -> {b}  d={d} terrain={prov_terrain.get(b)} infra={state_infra.get(prov_state[a])} "
                      f"river={(min(a, b), max(a, b)) in rivers} falloff={flow.falloff(d):.2f} "
                      f"penalty={flow.penalty(kind, a, b, d):.2f} -> {best[b][0]:.2f}")
            print(f"  state {prov_state[p]} supply pool {state_supply[prov_state[p]]:.2f}")

    # render
    x0, y0, x1, y1 = (int(v) for v in args.crop.split(","))
    img = np.asarray(Image.open(hoi4 / "map" / "provinces.bmp").convert("RGB"))[y0:y1, x0:x1].astype(np.int32)
    key = (img[..., 0] << 16) | (img[..., 1] << 8) | img[..., 2]
    lut_keys = np.array(sorted(keys))
    ids = np.array([keys[k] for k in lut_keys])[np.searchsorted(lut_keys, key)]
    uniq, inv = np.unique(ids, return_inverse=True)
    pal = np.zeros((len(uniq), 3), np.uint8)
    for i, p in enumerate(uniq.tolist()):
        land = info.get(p, (False, ""))[0]
        if not land:
            pal[i] = (40, 60, 95)
        elif prov_state.get(p) in impassable or p not in prov_state:
            pal[i] = (70, 70, 70)
        else:
            h = hub_supply.get(p, (0, None))[0]
            s = state_supply.get(prov_state[p], 0)
            v = {"total": h + s, "hub": h, "state": s}[args.layer]
            pal[i] = colour(v)
    out = pal[inv.reshape(ids.shape)]
    # state borders dark, country borders black
    st = np.vectorize(lambda p: int(prov_state.get(p, "0")))(uniq)[inv.reshape(ids.shape)]
    tag_ids = {t: i for i, t in enumerate(sorted(set(prov_owner.values())), 1)}
    ow = np.array([tag_ids.get(prov_owner.get(p), 0) for p in uniq.tolist()])[inv.reshape(ids.shape)]
    sb = np.zeros(st.shape, bool)
    sb[:, 1:] |= st[:, 1:] != st[:, :-1]
    sb[1:, :] |= st[1:, :] != st[:-1, :]
    cb = np.zeros(st.shape, bool)
    cb[:, 1:] |= ow[:, 1:] != ow[:, :-1]
    cb[1:, :] |= ow[1:, :] != ow[:-1, :]
    out[sb] = (out[sb] * 0.6).astype(np.uint8)
    out[cb] = (15, 15, 15)
    # sources: capital white, hubs black dot
    pts = {p: xy for p, xy in ((int(r.split(";")[0]), r.split(";")[1:]) for r in
                                (data / "map" / "hoi4_province_centres.csv").read_text().splitlines()[1:])}
    for src, kind in source_kind.items():
        if src in pts:
            x, y = int(pts[src][0]) - x0, int(pts[src][1]) - y0
            if 2 <= x < out.shape[1] - 2 and 2 <= y < out.shape[0] - 2:
                out[y - 2:y + 3, x - 2:x + 3] = (255, 255, 255) if kind == "CAPITAL" else (0, 0, 0)
    im = Image.fromarray(out)
    if args.scale != 1:
        im = im.resize((int(im.width * args.scale), int(im.height * args.scale)), Image.NEAREST)
    path = HERE / f"supply_preview{'' if args.layer == 'total' else '_' + args.layer}.png"
    im.save(path)
    print("wrote", path)


if __name__ == "__main__":
    main()
