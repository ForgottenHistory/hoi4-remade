"""Measure HOI4's low-supply rules in a real game, from a reference recording's daily country blocks (every
division's army_current_supply_ratio, organisation, strength and equipment; see the reference workspace's CLAUDE.md).

  python Tools/reference/hoi4_supply.py [REC]     (default: meme_ger, Germany 1936-1941, 94,880 division-days)

1. How the supply ratio moves day to day (Hoi4Supply.RatioGainPerDay, RatioDropShare).
2. Organisation against supply: per template, max org = the 99th percentile at full supply; the share held by
   divisions 20+ days in a supply band (Hoi4Supply.OrganisationCap: capped only below half supply).
3. Daily equipment change by supply band, on days without reinforcement (Hoi4Supply.AttritionLossPerDay).
"""
import gzip
import re
import sys
from collections import defaultdict
from datetime import date
from pathlib import Path

import numpy as np

DEFAULT = Path(r"D:\Stuff\Coding\hoi4\hoi4-recorder\recordings\meme_ger")
START = re.compile(r"division=\{\s*id=\{ id=(\d+) type=\d+ \}")  # type 51 for 1936 divisions, 4713 for later ones


def read(rec: Path):
    """division id -> [(date, supply, org, strength, equipment, template)], by date."""
    by_div = defaultdict(list)
    for gz in sorted((rec / "country").glob("*.txt.gz")):
        day = date.fromisoformat(gz.name[:10])
        text = gzip.open(gz, "rt", encoding="utf-8", errors="replace").read()
        starts = [(m.start(), m.group(1)) for m in START.finditer(text)]
        for i, (pos, did) in enumerate(starts):
            body = text[pos:starts[i + 1][0] if i + 1 < len(starts) else len(text)]
            k = body.find("army_current_supply_ratio")
            if k < 0:
                continue
            body = body[:k + 40]

            def g(key):
                x = re.search(r"(?<![a-z_])" + key + r"=([-\d.]+)", body)
                return float(x.group(1)) if x else None
            tmpl = re.search(r"division_template_id=\{ id=(\d+)", body)
            eq = sum(float(a) for a in re.findall(r"(?<![a-z_])amount=([\d.]+)", body))
            s, o = g("army_current_supply_ratio"), g("organisation")
            if s is None or o is None:
                continue
            by_div[did].append((day, s, o, g("strength") or 0.0, eq, tmpl.group(1) if tmpl else ""))
    for v in by_div.values():
        v.sort()
    return by_div


def main():
    rec = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT
    by_div = read(rec)
    print(f"{rec.name}: {sum(len(v) for v in by_div.values())} division-days, {len(by_div)} divisions")

    ups, downs = [], []
    for v in by_div.values():
        for a, b in zip(v, v[1:]):
            if (b[0] - a[0]).days != 1:
                continue
            d = b[1] - a[1]
            if d > 0.01 and a[1] < 99:
                ups.append(d)
            elif d < -0.01:
                downs.append(d)
    ups, downs = np.array(ups), np.array(downs)
    print(f"1. supply day to day: rises {len(ups)} median +{np.median(ups):.1f} p90 +{np.percentile(ups, 90):.1f}; "
          f"drops {len(downs)} median {np.median(downs):.1f} p10 {np.percentile(downs, 10):.1f}")

    full = defaultdict(list)
    for v in by_div.values():
        for day, s, o, st, eq, t in v:
            if s >= 100:
                full[t].append(o)
    max_org = {t: np.percentile(o, 99) for t, o in full.items() if len(o) >= 30}
    bands = defaultdict(list)
    for v in by_div.values():
        run, prev = 0, None
        for day, s, o, st, eq, t in v:
            b = min(10, int(s // 10))
            run = run + 1 if b == prev else 1
            prev = b
            if t in max_org and run >= 20 and s < 100:
                bands[b].append(min(1.5, o / max_org[t]))
    print("2. org / max org after 20+ days in a supply band (ours: 1 - 0.65 x (0.5 - ratio) / 0.5 below half supply):")
    for b in sorted(bands):
        a = np.array(bands[b])
        ours = 1 - 0.65 * max(0.0, 0.5 - (b * 10 + 5) / 100) / 0.5
        print(f"   {b * 10:3d}-{b * 10 + 9:3d}%: n={len(a):5d} median {np.median(a):.2f} p90 {np.percentile(a, 90):.2f} "
              f"max {a.max():.2f}   ours {ours:.2f}")

    eqb = defaultdict(list)
    for v in by_div.values():
        for a, b in zip(v, v[1:]):
            if (b[0] - a[0]).days != 1 or a[4] <= 0 or b[3] > a[3] + 0.01:
                continue  # gaps, no equipment, or reinforced (strength rose)
            eqb[min(10, int(a[1] // 10))].append((b[4] - a[4]) / a[4])
    print("3. daily equipment change, no reinforcement (ours: 0 from 35%, -0.15% a day at no supply):")
    for b in sorted(eqb):
        a = np.array(eqb[b])
        print(f"   {b * 10:3d}%: n={len(a):6d} mean {a.mean() * 100:+.2f}%  share losing {np.mean(a < -1e-6) * 100:.0f}%")


if __name__ == "__main__":
    main()
