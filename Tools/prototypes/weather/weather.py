"""Prototype: HOI4 weather, day by day, from common/weather.txt and the strategic regions' weather periods.

HOI4's rules (common/weather.txt, the wiki's Weather page) and where we fill gaps (OURS):
  - Region phenomenon: each strategic region has one weather event at a time (no_phenomenon, rain_light, rain_heavy,
    snow, blizzard, sandstorm), picked by the weights of the month's period; it lasts a duration from `duration`
    (random in [min, max], the smaller of two rolls). OURS: one roll per region (HOI4 drifts weather systems).
  - Temperature per province, inside the period's range (+ terrain bias: mountain -7, hills -3, forest -1, urban +3,
    jungle +5): each day it moves up to temperature_variation (2) plus a pull to the region's drifting target, at most
    max_temperature_change (6), then is smoothed toward its land neighbours (temperature_neighbor_smoothing 0.3).
    OURS: the target walk (random within the range, a fifth of the way back to the middle each day).
  - Snow: while its region snows (blizzard) and it is 1 degree or colder, a province gains snow_gain_on_snowing 1
    (blizzard 5) a day, full rate from -4 down, scaled by terrain (mountain +80%, urban -40%, desert none), up to 80.
    20-40 is ground snow (weather_ground_snow_medium), 40+ deep snow. Not snowing, at 0 degrees or warmer it melts:
    a share from 0.001 (0 C) to 0.9 (12 C) a day, the melt becoming collected water. A warm province gets rain instead.
  - Water and mud: rain gives 0.5 (heavy 1.8) water a day, times the period's mud factor and the terrain (plains +50%,
    marsh +90%, mountain -30%) and cut by infrastructure (up to -80% at level 5, INFRASTRUCTURE_MUD_EFFECT), up to 200;
    it dries a share from 0.0005 (2 C) to 0.4 (38 C) a day, on rainy days a quarter of that (OURS, --drain: drying only
    on dry days left cool wet summers in mud); dry days under 5 it is gone. OURS: mud while water is
    15 or more (chance_increase mud { 15 0.10 }: it stays until under 15 x 0.9), frozen (no effect) at -1 or colder.

  python Tools/prototypes/weather/weather.py [--days 366] [--dates 1936-02-24 1936-04-15 1936-11-15] [--seed 1]
Writes weather_<date>.png (Europe: ground snow white, deep snow bright, mud brown, region weather tinted) and a
monthly table of snow and mud shares for a few regions.
"""
import argparse
import random
import re
import sys
from collections import defaultdict
from datetime import date, timedelta
from pathlib import Path

import numpy as np
from PIL import Image

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE.parent / "supply"))
import supply_flow  # noqa: E402
from rail_network import province_adjacency  # noqa: E402
from render_rails import province_ids  # noqa: E402

KINDS = ["no_phenomenon", "rain_light", "rain_heavy", "snow", "blizzard", "sandstorm"]
DURATION = {"no_phenomenon": (5, 12), "rain_light": (5, 20), "rain_heavy": (2, 15), "snow": (5, 20),
            "blizzard": (2, 15), "sandstorm": (2, 10)}
TEMP_BIAS = {"mountain": -7.0, "hills": -3.0, "forest": -1.0, "urban": 3.0, "jungle": 5.0}
SNOW_MOD = {"mountain": 0.8, "desert": -1.0, "urban": -0.4}
MUD_MOD = {"mountain": -0.3, "plains": 0.5, "marsh": 0.9, "desert": 0.1, "jungle": 0.2}
WATER_GAIN = {"rain_light": 0.5, "rain_heavy": 1.8}
SNOW_GAIN = {"snow": 1.0, "blizzard": 5.0}
MUD_AT, MUD_KEEP = float(sys.argv[sys.argv.index("--mud") + 1]) if "--mud" in sys.argv else 15.0, 0.0
MUD_KEEP = MUD_AT * 0.9


def lerp_clamped(x, x0, x1, y0, y1):
    t = min(1.0, max(0.0, (x - x0) / (x1 - x0)))
    return y0 + (y1 - y0) * t


def read_regions(hoi4: Path):
    """region id -> (provinces, periods [(start (month, day), end, lo, hi, weights, mud)])"""
    regions = {}
    for f in sorted((hoi4 / "map" / "strategicregions").glob("*.txt")):
        t = re.sub(r"#[^\n]*", "", f.read_text(encoding="latin-1"))
        rid = int(re.search(r"\bid\s*=\s*(\d+)", t).group(1))
        provs = [int(x) for x in re.search(r"provinces\s*=\s*\{([^}]*)\}", t).group(1).split()]
        periods = []
        for m in re.finditer(r"period\s*=\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}", t):
            b = m.group(1)
            a, z = re.search(r"between\s*=\s*\{\s*([\d.]+)\s+([\d.]+)", b).groups()
            lo, hi = (float(v) for v in re.search(r"temperature\s*=\s*\{\s*([-\d.]+)\s+([-\d.]+)", b).groups())
            w = {k: float((re.search(rf"\b{k}\s*=\s*([\d.]+)", b) or [0, 0])[1]) for k in KINDS}
            mud = float((re.search(r"\bmud\s*=\s*([\d.]+)", b) or [0, 1])[1])
            d0, m0 = (int(v) for v in a.split("."))
            d1, m1 = (int(v) for v in z.split("."))
            periods.append(((m0, d0), (m1, d1), lo, hi, w, mud))
        regions[rid] = (provs, periods)
    return regions


def period_for(periods, day: date):
    key = (day.month - 1, day.day - 1)
    for p in periods:
        if p[0] <= key <= p[1]:
            return p
    return periods[0] if periods else None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=supply_flow.DEFAULT_HOI4)
    ap.add_argument("--data", default="Hoi4Data")
    ap.add_argument("--days", type=int, default=366)
    ap.add_argument("--warmup", type=int, default=30, help="days of December run before 1 January (HOI4 init_run_passes)")
    ap.add_argument("--dates", nargs="*", default=["1936-02-24", "1936-04-10", "1936-11-15", "1936-12-31"])
    ap.add_argument("--crop", default="2500,150,4000,950")
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--drain", type=float, default=0.25, help="share of the drying rate that also works on rainy days")
    ap.add_argument("--mudmode", choices=["threshold", "chance"], default="threshold")
    ap.add_argument("--mud", type=float, default=15.0, help="water at which mud forms")
    ap.add_argument("--dry", choices=["share", "amount"], default="share", help="water_dryout_multiplier: a share or an amount a day")
    ap.add_argument("--melt", choices=["share", "amount"], default="share", help="snow_melt_multiplier: a share or an amount a day")
    ap.add_argument("--dump", action="store_true", help="also write state_<date>.json (snow, water, mud, temperature per province) for snow_render.py")
    ap.add_argument("--norender", action="store_true", help="skip the preview maps")
    args = ap.parse_args()
    hoi4 = Path(args.hoi4)
    rng = random.Random(args.seed)

    m = supply_flow.compute(hoi4, Path(args.data))
    regions = read_regions(hoi4)
    pairs, _ = province_adjacency(hoi4)
    land = {p for p, (is_land, _) in m.info.items() if is_land}
    nbrs = defaultdict(list)
    for a, b in pairs:
        if a in land and b in land:
            nbrs[a].append(b)
            nbrs[b].append(a)
    region_of = {}
    for rid, (provs, _) in regions.items():
        for p in provs:
            if p in land:
                region_of[p] = rid
    provs = sorted(region_of)
    terrain = {p: m.prov_terrain.get(p, "") for p in provs}
    infra = {p: m.state_infra.get(m.prov_state.get(p), 0) for p in provs}

    temp, snow, water, mud = {}, defaultdict(float), defaultdict(float), set()
    phen, left, target = {}, {}, {}
    history = defaultdict(lambda: defaultdict(list))
    world = defaultdict(list)
    temps = defaultdict(lambda: defaultdict(list))  # region -> month -> [(snow share, mud share)]

    def step(day: date, record: bool):
        # regions: weather event and temperature target
        per = {}
        for rid, (_, periods) in regions.items():
            p = period_for(periods, day)
            if p is None:
                continue
            per[rid] = p
            _, _, lo, hi, w, _ = p
            mid = (lo + hi) / 2
            t = target.get(rid, mid)
            target[rid] = min(hi, max(lo, t + rng.uniform(-3, 3) + 0.2 * (mid - t)))
            if left.get(rid, 0) <= 0:
                total = sum(w.values())
                r = rng.uniform(0, total) if total > 0 else 0
                kind = "no_phenomenon"
                for k in KINDS:
                    r -= w[k]
                    if r <= 0:
                        kind = k
                        break
                lo_d, hi_d = DURATION[kind]
                phen[rid] = kind
                left[rid] = min(rng.randint(lo_d, hi_d), rng.randint(lo_d, hi_d))
            left[rid] -= 1
        # provinces: temperature
        new = {}
        for p in provs:
            rid = region_of[p]
            if rid not in per:
                continue
            _, _, lo, hi, _, _ = per[rid]
            bias = TEMP_BIAS.get(terrain[p], 0.0)
            t = temp.get(p, target[rid] + bias)
            change = rng.uniform(-2, 2) + 0.5 * (target[rid] + bias - t)
            change = min(6.0, max(-6.0, change))
            new[p] = min(hi + bias, max(lo + bias, t + change))
        for p, t in new.items():
            ns = [new[n] for n in nbrs[p] if n in new]
            temp[p] = 0.7 * t + 0.3 * (sum(ns) / len(ns)) if ns else t
        # provinces: snow, water, mud
        for p in provs:
            rid = region_of[p]
            if rid not in per or p not in temp:
                continue
            t, kind, mudf = temp[p], phen.get(rid, "no_phenomenon"), per[rid][5]
            precipitating = kind in ("rain_light", "rain_heavy", "snow", "blizzard")
            if kind in SNOW_GAIN and t <= 1.0:
                snow[p] = min(80.0, snow[p] + SNOW_GAIN[kind] * lerp_clamped(t, 1.0, -4.0, 0.0, 1.0)
                              * max(0.0, 1 + SNOW_MOD.get(terrain[p], 0.0)))
            elif snow[p] > 0 and t >= 0.0:
                rate = lerp_clamped(t, 0.0, 12.0, 0.001, 0.9)
                melt = min(snow[p], rate) if args.melt == "amount" else snow[p] * rate
                snow[p] -= melt
                water[p] += melt
            if precipitating and not (kind in SNOW_GAIN and t <= 1.0):
                gain = WATER_GAIN.get(kind, WATER_GAIN["rain_light"] if kind == "snow" else WATER_GAIN["rain_heavy"])
                water[p] += gain * mudf * max(0.0, 1 + MUD_MOD.get(terrain[p], 0.0)) * (1 - 0.8 * infra[p] / 5)
            if t >= 2.0:
                rate = lerp_clamped(t, 2.0, 38.0, 0.0005, 0.4) * (args.drain if precipitating else 1.0)
                water[p] = max(0.0, water[p] - rate) if args.dry == "amount" else water[p] * (1 - rate)
            water[p] = min(200.0, water[p])
            if not precipitating and water[p] < 5.0:   # water_gain_min: what's left of drying out is gone
                water[p] = 0.0
            if args.mudmode == "chance":
                # chance_increase mud { 15 0.10 }: from 15 water a 10% chance a day to turn to mud; gone under 15
                if water[p] < MUD_AT:
                    mud.discard(p)
                elif p not in mud and rng.random() < 0.10:
                    mud.add(p)
            elif p in mud:
                if water[p] < MUD_KEEP:
                    mud.discard(p)
            elif water[p] >= MUD_AT:
                mud.add(p)
        if record:
            world[day.month].append(np.mean([p in mud and temp.get(p, 0) > -1 for p in provs]))
            for rid, (rp, _) in regions.items():
                lp = [p for p in rp if p in temp]
                if lp:
                    temps[rid][day.month].append(np.mean([temp[p] for p in lp]))
                    history[rid][day.month].append((np.mean([snow[p] >= 20 for p in lp]),
                                                    np.mean([p in mud and temp[p] > -1 for p in lp])))

    start = date(1936, 1, 1)
    for k in range(args.warmup):
        step(date(1935, 12, 1) + timedelta(days=k), False)
    want = {date.fromisoformat(d) for d in args.dates}
    ids = province_ids(hoi4) if want and not args.norender else None
    for k in range(args.days):
        day = start + timedelta(days=k)
        step(day, True)
        if day in want and args.dump:
            import json
            (Path(__file__).parent / f"state_{day.isoformat()}.json").write_text(json.dumps(
                {p: [round(snow[p], 2), round(water[p], 2), int(p in mud), round(temp.get(p, 0.0), 1)] for p in provs}))
        if day in want and not args.norender:
            render(hoi4, Path(args.data), ids, args.crop, day, temp, snow, mud, phen, region_of)

    names = {8: "Eastern Germany", 1: "Southern England", 155: "Manchuria"}
    for f in (hoi4 / "map" / "strategicregions").glob("*.txt"):
        for key, label in (("France", "North France"), ("Ukraine", "Ukraine"), ("Moscow", "Moscow"), ("Leningrad", "Leningrad")):
            if key in f.stem and label not in names.values():
                names[int(f.stem.split("-")[0])] = f.stem.split("-", 1)[1]
    print("monthly share of provinces with ground snow (20+) / mud (not frozen):")
    print(f"{'region':28s}" + "".join(f"{mo:>10d}" for mo in range(1, 13)))
    for rid, label in sorted(names.items()):
        h = history.get(rid)
        if not h:
            continue
        print(f"{label[:28]:28s}" + "".join(
            f"{np.mean([s for s, _ in h[mo]]) * 100:4.0f}/{np.mean([u for _, u in h[mo]]) * 100:3.0f}%" if h.get(mo) else "         -"
            for mo in range(1, 13)))
    stems = {int(f.stem.split("-")[0]): f.stem.split("-", 1)[1] for f in (hoi4 / "map" / "strategicregions").glob("*.txt")}
    summer = {rid: np.mean([u for mo in (7, 8) for _, u in h.get(mo, [])] or [0]) for rid, h in history.items()}
    sizes = {rid: len([p for p in regions[rid][0] if p in region_of]) for rid in summer}
    top = sorted(summer, key=lambda k: -summer[k] * sizes[k])[:8]
    print("most summer (Jul-Aug) mud:", ", ".join(f"{stems.get(k, k)} {summer[k] * 100:.0f}%" for k in top))
    print("world land in mud by month:", " ".join(f"{np.mean(world[mo]) * 100:.0f}%" for mo in range(1, 13)))
    print("Eastern Germany mean temperature by month:", " ".join(f"{np.mean(temps[8][mo]):.1f}" for mo in range(1, 13)))
    p = 11260
    print(f"province {p} (Pomerania): temp {temp.get(p, 0):.1f} snow {snow[p]:.1f} water {water[p]:.1f} at the end")


def render(hoi4, data, ids, crop, day, temp, snow, mud, phen, region_of):
    x0, y0, x1, y1 = (int(v) for v in crop.split(","))
    sub = ids[y0:y1, x0:x1]
    H, W = ids.shape
    cmap = Image.open(data / "gfx" / "map" / "colormap.png").convert("RGB").resize((W, H), Image.BILINEAR)
    out = np.asarray(cmap, np.float32)[y0:y1, x0:x1] * 0.8
    uniq, inv = np.unique(sub, return_inverse=True)
    col = np.zeros((len(uniq), 4), np.float32)
    tint = {"rain_light": (90, 140, 230), "rain_heavy": (40, 70, 200), "snow": (200, 220, 255), "blizzard": (255, 255, 255),
            "sandstorm": (230, 180, 90)}
    for i, p in enumerate(uniq.tolist()):
        if p not in temp:
            continue
        if snow[p] >= 40:
            col[i] = (255, 255, 255, 0.9)
        elif snow[p] >= 20:
            col[i] = (225, 230, 240, 0.7)
        elif p in mud and temp[p] > -1:
            col[i] = (110, 75, 35, 0.8)
        elif phen.get(region_of[p]) in tint:
            col[i] = (*tint[phen[region_of[p]]], 0.25)
    c = col[inv.reshape(sub.shape)]
    out = out * (1 - c[..., 3:]) + c[..., :3] * c[..., 3:]
    edge = np.zeros(sub.shape, bool)
    edge[:, 1:] |= sub[:, 1:] != sub[:, :-1]
    edge[1:, :] |= sub[1:, :] != sub[:-1, :]
    out[edge] *= 0.85
    path = HERE / f"weather_{day.isoformat()}.png"
    Image.fromarray(out.clip(0, 255).astype(np.uint8)).resize(((x1 - x0) // 2, (y1 - y0) // 2), Image.BOX).save(path)
    print("wrote", path)


if __name__ == "__main__":
    main()
