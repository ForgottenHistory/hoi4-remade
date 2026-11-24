"""Prototype: HOI4 division supply demand at the 1936 start - who gets how much of the province supply.

Supply side: supply_flow.compute (hub supply per province, summed over its best sources; state supply pools; each
source's throughput: the capital supply, or 10 + 5 * rail bottleneck for a hub).

Demand: each division needs its template's supply use = the sum of its battalions' and support companies'
supply_consumption (common/units). Divisions in one province add up (checked in HOI4: two infantry divisions take
twice one). HOI4's designer shows 0.82 for the German infantry division where the sum is 0.72: some modifier we
don't have yet (--need-mult scales every need).

Allocation, in the order the wiki gives (state supply first, then hubs):
  1. State pool: shared evenly by every division in the state (equal shares, a division never takes more than it
     needs, what it leaves goes to the others).
  2. Hub supply: each province's remaining need draws on the province's hub supply, split over its sources in
     proportion to their flow. A source over its throughput scales down what every province draws from it.
     A province short of hub supply shares it in proportion to each division's remaining need.
  3. Supply ratio = (state + hub) / need, at most 1. HOI4's divisions move their stored supply toward this
     ratio over days (ARMY_SUPPLY_RATIO_*), not simulated here.
Ports, convoys and air supply are left out, so overseas divisions (Italy in East Africa) come out unsupplied.

  python Tools/prototypes/supply/supply_demand.py [--country GER ...] [--stack PROV N ...] [--crop x0,y0,x1,y1]
--stack PROV N adds N divisions of the province owner's first template there (a stress test: a big stack on a
border). Writes Tools/prototypes/supply/demand_preview.png: total supply per province (as supply_flow), each
province with divisions marked by a dot, green (supplied) .. red (starving), bigger for more divisions.
"""
import argparse
import re
import sys
from collections import defaultdict
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE.parent.parent))  # Tools/
from hoi4_convert import parse_pdx  # noqa: E402
from rail_network import DEFAULT_HOI4  # noqa: E402
from supply_flow import compute, colour  # noqa: E402


def read_subunit_supply(hoi4: Path):
    out = {}
    for f in (hoi4 / "common" / "units").glob("*.txt"):
        for key, _, val in parse_pdx(f.read_text(encoding="utf-8-sig", errors="replace")):
            if key == "sub_units" and isinstance(val, list):
                for name, _, body in val:
                    if isinstance(body, list):
                        for k, _, v in body:
                            if k == "supply_consumption":
                                out[name] = float(v)
    return out


def block(items, key):
    return [v for k, _, v in items if k == key]


def read_oobs(hoi4: Path):
    """tag -> list of (location, template name, [sub-unit names]). Picks the No Step Back OOB when there is one."""
    units_dir = hoi4 / "history" / "units"
    out = {}
    for f in (hoi4 / "history" / "countries").glob("*.txt"):
        tag = f.name[:3]
        names = re.findall(r'\b(?:set_)?oob\s*=\s*"?(\w+)"?', f.read_text(encoding="utf-8-sig", errors="replace"))
        names = [n for n in names if "naval" not in n and "air" not in n]
        names = [n for n in names if "1936" in n] or names[:1]
        if not names:
            continue
        pick = next((n for n in names if n.endswith("_nsb")), names[0])
        path = units_dir / f"{pick}.txt"
        if not path.exists():
            continue
        items = parse_pdx(path.read_text(encoding="utf-8-sig", errors="replace"))
        templates = {}
        for t in block(items, "division_template"):
            name = next(iter(block(t, "name")), "")
            subs = []
            for part in ("regiments", "support"):
                for b in block(t, part):
                    subs += [k for k, _, _ in b if k]
            templates[name] = subs
        divs = []
        for u in block(items, "units"):
            for d in block(u, "division"):
                loc = next(iter(block(d, "location")), None)
                tn = next(iter(block(d, "division_template")), None)
                if loc is not None and tn in templates:
                    divs.append((int(loc), tn, templates[tn]))
        out[tag] = (divs, templates)
    return out


def allocate(m, divisions):
    """divisions: list of dicts {tag, prov, need}. Fills got_state, got_hub, ratio. Returns per-source draw."""
    by_state = defaultdict(list)
    for d in divisions:
        d["got_state"] = d["got_hub"] = 0.0
        by_state[m.prov_state.get(d["prov"])].append(d)

    # 1. state pools, evenly (water-filling)
    for sid, ds in by_state.items():
        pool = m.state_supply.get(sid, 0.0)
        left = sorted(ds, key=lambda d: d["need"])
        while left and pool > 1e-9:
            share = pool / len(left)
            d = left[0]
            if d["need"] - d["got_state"] <= share:  # smallest need first: it is filled, the rest share what is left
                take = d["need"] - d["got_state"]
                d["got_state"] += take
                pool -= take
                left.pop(0)
            else:
                for d in left:
                    d["got_state"] += share
                pool = 0

    # 2. hub supply per province, split over sources; then throughput caps
    by_prov = defaultdict(list)
    for d in divisions:
        by_prov[d["prov"]].append(d)
    draw = {}  # prov -> {source: amount}
    for p, ds in by_prov.items():
        want = sum(d["need"] - d["got_state"] for d in ds)
        cs = m.contrib.get(p, [])[:m.top]
        avail = sum(f for f, _ in cs)
        if want <= 0 or avail <= 0:
            continue
        take = min(want, avail)
        draw[p] = {s: take * f / avail for f, s in cs}
    source_draw = defaultdict(float)
    for p, per in draw.items():
        for s, v in per.items():
            source_draw[s] += v
    scale = {s: min(1.0, m.capacity.get(s, 0.0) / v) if v > 0 else 1.0 for s, v in source_draw.items()}
    for p, per in draw.items():
        got = sum(v * scale[s] for s, v in per.items())
        ds = by_prov[p]
        want = sum(d["need"] - d["got_state"] for d in ds)
        for d in ds:
            d["got_hub"] = got * (d["need"] - d["got_state"]) / want
    for d in divisions:
        d["ratio"] = min(1.0, (d["got_state"] + d["got_hub"]) / d["need"]) if d["need"] > 0 else 1.0
    return source_draw, scale


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=DEFAULT_HOI4)
    ap.add_argument("--data", default="Hoi4Data")
    ap.add_argument("--country", nargs="*", default=["GER", "SOV", "FRA", "ENG", "ITA", "POL", "USA", "JAP", "CHI"])
    ap.add_argument("--stack", nargs=2, type=int, action="append", default=[], metavar=("PROV", "N"))
    ap.add_argument("--need-mult", type=float, default=1.0)
    ap.add_argument("--weather", type=float, default=1.0)
    ap.add_argument("--crop", default="2560,240,3600,900")
    ap.add_argument("--scale", type=float, default=1.0)
    args = ap.parse_args()
    hoi4, data = Path(args.hoi4), Path(args.data)

    m = compute(hoi4, data, weather=args.weather)
    supply_use = read_subunit_supply(hoi4)
    oobs = read_oobs(hoi4)

    divisions = []
    for tag, (divs, templates) in oobs.items():
        for loc, tn, subs in divs:
            need = sum(supply_use.get(s, 0.0) for s in subs) * args.need_mult
            divisions.append({"tag": tag, "prov": loc, "template": tn, "need": need})
    for prov, n in args.stack:
        tag = m.prov_owner.get(prov)
        divs, templates = oobs.get(tag, ([], {}))
        if not templates:
            print(f"--stack {prov}: owner {tag} has no templates")
            continue
        tn, subs = next(iter(templates.items()))
        need = sum(supply_use.get(s, 0.0) for s in subs) * args.need_mult
        divisions += [{"tag": tag, "prov": prov, "template": tn, "need": need, "stacked": True} for _ in range(n)]
        print(f"stacked {n} x {tag} '{tn}' (need {need:.2f}) in {prov}")

    source_draw, scale = allocate(m, divisions)
    print(f"{len(divisions)} divisions, {len(oobs)} countries with an OOB")
    gi = [d for d in divisions if d["tag"] == "GER" and d["template"] == "Infanterie-Division"]
    if gi:
        print(f"German infantry division need {gi[0]['need']:.2f} (HOI4 designer: 0.82)")

    for tag in args.country:
        ds = [d for d in divisions if d["tag"] == tag]
        if not ds:
            continue
        r = np.array([d["ratio"] for d in ds])
        need = sum(d["need"] for d in ds)
        st = sum(d["got_state"] for d in ds)
        hub = sum(d["got_hub"] for d in ds)
        over = [s for s in source_draw if m.prov_owner.get(s) == tag and scale[s] < 1]
        print(f"{tag}: {len(ds)} divisions need {need:.1f}; got state {st:.1f} + hub {hub:.1f}; "
              f"full {np.mean(r >= 0.999) * 100:.0f}%, <50% {np.mean(r < 0.5) * 100:.0f}%, mean ratio {r.mean():.2f}; "
              f"hubs over throughput: {len(over)}")
        worst = sorted({d["prov"]: d for d in ds}.values(), key=lambda d: d["ratio"])[:4]
        for d in worst:
            if d["ratio"] < 0.999:
                n = sum(1 for e in ds if e["prov"] == d["prov"])
                cs = m.contrib.get(d["prov"], [])[:3]
                print(f"   {d['prov']} ({m.prov_terrain.get(d['prov'])}, state {m.prov_state.get(d['prov'])}): {n} div, "
                      f"ratio {d['ratio']:.2f}, hub here {sum(f for f, _ in cs):.2f}, "
                      f"state pool {m.state_supply.get(m.prov_state.get(d['prov']), 0):.1f}")
    for prov, n in args.stack:
        ds = [d for d in divisions if d["prov"] == prov]
        if ds:
            d = ds[0]
            cs = m.contrib.get(prov, [])[:3]
            print(f"stack {prov}: {len(ds)} divisions need {sum(e['need'] for e in ds):.1f}, ratio {d['ratio']:.2f} "
                  f"(state {sum(e['got_state'] for e in ds):.2f} of pool {m.state_supply.get(m.prov_state.get(prov), 0):.1f}, "
                  f"hub {sum(e['got_hub'] for e in ds):.2f} of {sum(f for f, _ in cs):.2f}; sources "
                  + ", ".join(f"{s} {source_draw.get(s, 0):.1f}/{m.capacity.get(s, 0):.0f}" for _, s in cs) + ")")

    # render: total supply map with division dots
    x0, y0, x1, y1 = (int(v) for v in args.crop.split(","))
    img = np.asarray(Image.open(hoi4 / "map" / "provinces.bmp").convert("RGB"))[y0:y1, x0:x1].astype(np.int32)
    key = (img[..., 0] << 16) | (img[..., 1] << 8) | img[..., 2]
    lut_keys = np.array(sorted(m.keys))
    ids = np.array([m.keys[k] for k in lut_keys])[np.searchsorted(lut_keys, key)]
    uniq, inv = np.unique(ids, return_inverse=True)
    pal = np.zeros((len(uniq), 3), np.uint8)
    for i, p in enumerate(uniq.tolist()):
        if not m.info.get(p, (False, ""))[0]:
            pal[i] = (40, 60, 95)
        elif m.prov_state.get(p) in m.impassable or p not in m.prov_state:
            pal[i] = (70, 70, 70)
        else:
            v = m.hub_supply.get(p, (0, None))[0] + m.state_supply.get(m.prov_state[p], 0)
            c = np.array(colour(v), float)
            pal[i] = (c * 0.55 + 255 * 0.45).astype(np.uint8)  # washed out so the dots read
    out = pal[inv.reshape(ids.shape)]
    tag_ids = {t: i for i, t in enumerate(sorted(set(m.prov_owner.values())), 1)}
    ow = np.array([tag_ids.get(m.prov_owner.get(p), 0) for p in uniq.tolist()])[inv.reshape(ids.shape)]
    cb = np.zeros(ow.shape, bool)
    cb[:, 1:] |= ow[:, 1:] != ow[:, :-1]
    cb[1:, :] |= ow[1:, :] != ow[:-1, :]
    out[cb] = (60, 60, 60)
    im = Image.fromarray(out)
    if args.scale != 1:
        im = im.resize((int(im.width * args.scale), int(im.height * args.scale)), Image.NEAREST)
    draw = ImageDraw.Draw(im)
    pts = {int(r.split(";")[0]): (int(r.split(";")[1]), int(r.split(";")[2]))
           for r in (data / "map" / "hoi4_province_centres.csv").read_text().splitlines()[1:]}
    by_prov = defaultdict(list)
    for d in divisions:
        by_prov[d["prov"]].append(d)
    for p, ds in by_prov.items():
        if p not in pts:
            continue
        x, y = (pts[p][0] - x0) * args.scale, (pts[p][1] - y0) * args.scale
        if not (0 <= x < im.width and 0 <= y < im.height):
            continue
        ratio = min(d["ratio"] for d in ds)
        rad = (2 + 1.2 * len(ds) ** 0.5) * max(1.0, args.scale / 2)
        c = (int(220 * (1 - ratio) + 30 * ratio), int(40 * (1 - ratio) + 170 * ratio), 40)
        draw.ellipse([x - rad, y - rad, x + rad, y + rad], fill=c, outline=(0, 0, 0))
    path = HERE / "demand_preview.png"
    im.save(path)
    print("wrote", path)


if __name__ == "__main__":
    main()
