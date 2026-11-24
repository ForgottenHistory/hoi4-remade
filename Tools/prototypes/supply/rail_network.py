"""Prototype: HOI4 railways and supply hubs at the 1936 start (no supply flow yet).

Reads map/railways.txt ("LEVEL COUNT PROVINCE..": a chain of provinces, each consecutive pair one rail link),
map/supply_nodes.txt ("LEVEL PROVINCE"), province adjacency from map/provinces.bmp + adjacencies.csv,
and the converted state owners and capitals from Hoi4Data.

Checks:
  - every rail link joins two provinces that border each other (or a strait in adjacencies.csv)
  - links that appear in several lines (the level of a link is the highest line through it)
Then for each country, over rail links whose two provinces it owns:
  - each hub's bottleneck level to the capital (widest path: the best possible minimum level)
  - each hub's capacity: RAILWAY_BASE_FLOW + RAILWAY_FLOW_PER_LEVEL * bottleneck  (10 + 5 * level)
  - the capital's own capacity: 5 + 0.3 civ + 0.6 mil + 0.4 dockyards
  - hubs with no rail path to the capital

  python Tools/prototypes/supply/rail_network.py [--hoi4 PATH] [--data Hoi4Data] [--country GER ...]
"""
import argparse
import heapq
import json
import re
from collections import defaultdict
from pathlib import Path

import numpy as np
from PIL import Image

DEFAULT_HOI4 = r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV"
RAILWAY_BASE_FLOW, RAILWAY_FLOW_PER_LEVEL = 10.0, 5.0
CAPITAL_BASE, CAP_CIV, CAP_MIL, CAP_DOCK = 5.0, 0.3, 0.6, 0.4


def load_json5(path: Path):
    text = path.read_text(encoding="utf-8")
    text = re.sub(r"^\s*//.*$", "", text, flags=re.M)
    text = re.sub(r"(?m)^(\s*)([A-Za-z_]\w*)\s*:", r'\1"\2":', text)  # bare keys
    return json.loads(text)


def read_rails(hoi4: Path):
    links = {}  # (a, b) sorted -> level
    lines = 0
    for raw in (hoi4 / "map" / "railways.txt").read_text().split("\n"):
        v = [int(x) for x in raw.split()]
        if len(v) < 3:
            continue
        level, count, provs = v[0], v[1], v[2:]
        assert count == len(provs), raw
        lines += 1
        for a, b in zip(provs, provs[1:]):
            k = (min(a, b), max(a, b))
            links[k] = max(links.get(k, 0), level)
    return links, lines


def read_hubs(hoi4: Path):
    hubs = {}
    for raw in (hoi4 / "map" / "supply_nodes.txt").read_text().split("\n"):
        v = raw.split()
        if len(v) == 2:
            hubs[int(v[1])] = int(v[0])
    return hubs


def province_adjacency(hoi4: Path):
    defs = {}
    for line in (hoi4 / "map" / "definition.csv").read_text(encoding="latin-1").splitlines():
        c = line.split(";")
        if len(c) > 3 and c[0].isdigit():
            defs[(int(c[1]) << 16) | (int(c[2]) << 8) | int(c[3])] = int(c[0])
    img = np.asarray(Image.open(hoi4 / "map" / "provinces.bmp").convert("RGB")).astype(np.int32)
    key = (img[..., 0] << 16) | (img[..., 1] << 8) | img[..., 2]
    lut_keys = np.array(sorted(defs))
    lut_ids = np.array([defs[k] for k in lut_keys])
    ids = lut_ids[np.searchsorted(lut_keys, key)]
    pairs = set()
    for a, b in ((ids[:, :-1], ids[:, 1:]), (ids[:-1, :], ids[1:, :]), (ids[:, -1], ids[:, 0])):  # x wraps
        m = a != b
        lo, hi = np.minimum(a[m], b[m]), np.maximum(a[m], b[m])
        pairs.update(zip(lo.tolist(), hi.tolist()))
    straits = set()
    for line in (hoi4 / "map" / "adjacencies.csv").read_text(encoding="latin-1").splitlines()[1:]:
        c = line.split(";")
        if len(c) > 2 and c[0].lstrip("-").isdigit() and c[1].lstrip("-").isdigit():
            a, b = int(c[0]), int(c[1])
            if a > 0 and b > 0:
                straits.add((min(a, b), max(a, b)))
    return pairs, straits


def widest_paths(src, graph):
    """Bottleneck (max-min) level from src to every reachable province."""
    best = {src: 99}
    heap = [(-99, src)]
    while heap:
        w, u = heapq.heappop(heap)
        w = -w
        if w < best.get(u, -1):
            continue
        for v, lvl in graph[u]:
            nw = min(w, lvl)
            if nw > best.get(v, -1):
                best[v] = nw
                heapq.heappush(heap, (-nw, v))
    return best


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=DEFAULT_HOI4)
    ap.add_argument("--data", default="Hoi4Data")
    ap.add_argument("--country", nargs="*", default=["GER", "SOV", "FRA", "ENG", "ITA", "POL", "USA", "JAP", "CHI"])
    args = ap.parse_args()
    hoi4, data = Path(args.hoi4), Path(args.data)

    links, nlines = read_rails(hoi4)
    hubs = read_hubs(hoi4)
    pairs, straits = province_adjacency(hoi4)
    print(f"railways.txt: {nlines} lines, {len(links)} distinct links; supply_nodes.txt: {len(hubs)} hubs")
    lv = defaultdict(int)
    for l in links.values():
        lv[l] += 1
    print("  links by level:", dict(sorted(lv.items())))
    bad = [k for k in links if k not in pairs and k not in straits]
    via_strait = [k for k in links if k not in pairs and k in straits]
    print(f"  links between non-bordering provinces: {len(bad)} {bad[:10]}; over straits: {len(via_strait)}")
    rail_provs = {p for k in links for p in k}
    print(f"  hubs off every railway: {sorted(p for p in hubs if p not in rail_provs)[:20]}")

    regions = load_json5(data / "map" / "hoi4_regions.json5")
    sdata = load_json5(data / "map" / "hoi4_state_data.json5")
    prov_owner = {}
    for sid, provs in regions["states"].items():
        owner = sdata["states"].get(sid, {}).get("owner", "")
        for p in provs:
            prov_owner[p] = owner
    capitals = regions["capitals"]

    for tag in args.country:
        cap = capitals.get(tag)
        if cap is None:
            print(f"\n{tag}: no capital")
            continue
        graph = defaultdict(list)
        for (a, b), l in links.items():
            if prov_owner.get(a) == tag and prov_owner.get(b) == tag:
                graph[a].append((b, l))
                graph[b].append((a, l))
        reach = widest_paths(cap, graph)
        civ = mil = dock = 0
        for sid, s in sdata["states"].items():
            if s.get("owner") == tag:
                b = s.get("buildings", {})
                civ += b.get("industrial_complex", 0)
                mil += b.get("arms_factory", 0)
                dock += b.get("dockyard", 0)
        cap_supply = CAPITAL_BASE + CAP_CIV * civ + CAP_MIL * mil + CAP_DOCK * dock
        own_hubs = sorted(p for p in hubs if prov_owner.get(p) == tag)
        connected = [p for p in own_hubs if p in reach and p != cap]
        cut = [p for p in own_hubs if p not in reach]
        by_level = defaultdict(int)
        for p in connected:
            by_level[reach[p]] += 1
        print(f"\n{tag}: capital {cap} (hub there: {cap in hubs}), capital supply {cap_supply:.1f} "
              f"({civ} civ, {mil} mil, {dock} dock)")
        print(f"  hubs {len(own_hubs)}: connected {len(connected)} by bottleneck level {dict(sorted(by_level.items()))}, "
              f"no rail path {len(cut)} {cut[:12]}")
        if connected:
            caps = [RAILWAY_BASE_FLOW + RAILWAY_FLOW_PER_LEVEL * reach[p] for p in connected]
            print(f"  hub capacity: min {min(caps):.0f}, max {max(caps):.0f}, total {sum(caps):.0f}")


if __name__ == "__main__":
    main()
