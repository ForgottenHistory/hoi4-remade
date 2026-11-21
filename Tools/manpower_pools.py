"""Starting manpower pools as Hoi4Manpower computes them from the converted data: recruitable manpower
((core population + non-core population x 2%) x mobilisation law) minus the men in the country's 1936 divisions.

    python Tools/manpower_pools.py [TAG ...] [--data Hoi4Data]

Checked 2026-11-21: GER 1,368K against real HOI4's 1,316,296 on 1 January 1936 and 1,370,084 on the 2nd
(hoi4-recorder campaign1/country.tsv).
"""
import argparse
import json
import re
from pathlib import Path


def json5(path):
    """The converter's json5 subset: // comments, bare keys, trailing commas."""
    t = path.read_text(encoding="utf-8")
    t = re.sub(r"^\s*//.*$", "", t, flags=re.M)
    t = re.sub(r"([{,]\s*)([A-Za-z_][A-Za-z0-9_]*)\s*:", r'\1"\2":', t)
    t = re.sub(r",(\s*[}\]])", r"\1", t)
    return json.loads(t)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("tags", nargs="*", default=["GER", "FRA", "ENG", "SOV", "ITA", "POL", "CZE", "JAP"])
    ap.add_argument("--data", default=str(Path(__file__).resolve().parent.parent / "Hoi4Data"))
    a = ap.parse_args()
    data = Path(a.data)
    oob = json5(data / "history" / "units" / "oob_1936.json5")
    stats = json5(data / "common" / "hoi4_country_stats.json5")
    subs = oob["sub_units"]
    fielded, count = {}, {}
    for tag, c in oob["countries"].items():
        for d in c["divisions"]:
            t = c["templates"][d["template"]]
            fielded[tag] = fielded.get(tag, 0) + sum(subs[n]["manpower"] for n in t.get("regiments", []) + t.get("support", []) if n in subs)
            count[tag] = count.get(tag, 0) + 1
    for tag in a.tags:
        if tag not in stats:
            print(f"{tag}: not in the country stats")
            continue
        s = stats[tag]
        recruitable = (s["population"] + 0.02 * s.get("noncore_population", 0)) * s["conscription"]
        used = fielded.get(tag, 0)
        print(f"{tag}: recruitable {recruitable / 1e3:8.0f}K, {count.get(tag, 0):3} divisions with {used / 1e3:6.0f}K, "
              f"pool {max(0, recruitable - used) / 1e3:7.0f}K, growth {recruitable * 0.015 / 365:5.0f}/day")


if __name__ == "__main__":
    main()
