"""Equipment outlook from the converted 1936 data, as Hoi4Equipment runs it: per country and archetype, what its
divisions lack at the start (need x (1 - start_equipment_factor)), what its stockpile holds, what its production
lines make a day (factories x 4.5 x efficiency / cost), and the days until the shortfall is covered.

    python Tools/equipment_outlook.py [TAG ...] [--data Hoi4Data]
"""
import argparse
from pathlib import Path

from manpower_pools import json5

FACTORY_OUTPUT = 4.5


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("tags", nargs="*", default=["GER", "FRA", "ENG", "SOV", "ITA", "POL", "CZE", "JAP", "BEL", "HOL"])
    ap.add_argument("--data", default=str(Path(__file__).resolve().parent.parent / "Hoi4Data"))
    a = ap.parse_args()
    oob = json5(Path(a.data) / "history" / "units" / "oob_1936.json5")
    subs = oob["sub_units"]
    for tag in a.tags:
        c = oob["countries"].get(tag)
        if not c:
            print(f"{tag}: no divisions")
            continue
        need, lack = {}, {}
        for d in c["divisions"]:
            t = c["templates"][d["template"]]
            for s in t.get("regiments", []) + t.get("support", []):
                for arch, n in subs.get(s, {}).get("need", {}).items():
                    need[arch] = need.get(arch, 0) + n
                    lack[arch] = lack.get(arch, 0) + n * (1 - d.get("equipment", 1))
        per_day = {}
        for p in c.get("production", []):
            per_day[p["archetype"]] = per_day.get(p["archetype"], 0) + p["factories"] * FACTORY_OUTPUT * min(p["efficiency"], 0.5) / p["cost"]
        stock = c.get("stockpile", {})
        print(f"{tag}: {len(c['divisions'])} divisions")
        for arch in sorted(set(need) | set(per_day)):
            short = max(0, lack.get(arch, 0) - stock.get(arch, 0))
            rate = per_day.get(arch, 0)
            days = f"{short / rate:5.0f} days" if rate > 0 else ("  covered" if short == 0 else "    never")
            print(f"   {arch:22} need {need.get(arch, 0):7.0f}  lacking {lack.get(arch, 0):7.0f}  stock {stock.get(arch, 0):5}"
                  f"  makes {rate:6.1f}/day  {days}")


if __name__ == "__main__":
    main()
