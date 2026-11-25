"""Production modifiers in a real HOI4 game, from a recording's daily country blocks (country/DAY.txt.gz):
per day, over the country's military lines,
- output multiplier: line speed (IC a day) / (POWERED_FACTORY_SPEED_MIL 4.5 x the sum of its active factories'
  efficiencies), i.e. 1 + factory output modifiers (and any resource shortage);
- efficiency cap: the highest efficiency of any slot (lines grow up to it);
- start efficiency: what an unused slot sits at (the line's trailing slots).
Prints a row whenever one of them moves, with the techs finished and the laws/spirits changed since the last row
(research.tsv, politics.tsv), so each modifier can be matched to its source.

    python Tools/reference/hoi4_production_modifiers.py [REC] [--tag GER]
REC defaults to the reference workspace's vanilla_ger_1936.
"""
import argparse
import csv
import gzip
import re
import statistics
from pathlib import Path

REF = Path(r"D:\Stuff\Coding\hoi4\hoi4-recorder\recordings")
SPEED = 4.5  # NProduction POWERED_FACTORY_SPEED_MIL

LINE = re.compile(r"military_lines=\{(.*?)\n\t\t\t\}", re.S)


def lines(text):
    out = []
    for m in LINE.finditer(text):
        b = m.group(1)
        act = re.search(r"active_factories=(\d+)", b)
        spd = re.search(r"\bspeed=([\d.]+)", b)
        eff = re.search(r"factory_efficiencies=\{([^}]*)\}", b)
        eq = re.search(r"equipment_variant_index=\{ id=(\d+)", b)
        if not (act and spd and eff):
            continue
        out.append({"active": int(act.group(1)), "speed": float(spd.group(1)),
                    "eff": [float(x) for x in eff.group(1).split()], "eq": eq.group(1) if eq else "?"})
    return out


def tsv(rec, name):
    p = rec / name
    if not p.exists():
        return []
    with p.open(encoding="utf-8") as f:
        return list(csv.DictReader(f, delimiter="\t"))


MONTHS = {m: i + 1 for i, m in enumerate(("January", "February", "March", "April", "May", "June", "July", "August",
                                          "September", "October", "November", "December"))}


def iso(date):
    """'24:00, 25 April, 1936' (the recorders' read-by dates) -> '1936-04-25'; ISO dates pass through."""
    m = re.match(r"(?:[\d:]+, )?(\d+) (\w+), (\d+)", date)
    return f"{m.group(3)}-{MONTHS[m.group(2)]:02d}-{int(m.group(1)):02d}" if m else date[:10]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("rec", nargs="?", default=str(REF / "vanilla_ger_1936"))
    ap.add_argument("--tag", default="GER")
    a = ap.parse_args()
    rec = Path(a.rec)
    research = [r for r in tsv(rec, "research.tsv") if r.get("country") == a.tag and r.get("change") in ("done", "finished", "researched")]
    if not research:  # the change name for a finished tech
        research = [r for r in tsv(rec, "research.tsv") if r.get("country") == a.tag and r.get("slot") in ("", "-") and r.get("tech")]
    politics = [r for r in tsv(rec, "politics.tsv") if r.get("country") == a.tag and r.get("kind") in ("law", "spirit", "idea", "advisor")]
    last = None
    since = "0000"
    print(f"{'date':10} {'mult':>6} {'cap':>5} {'start':>5} {'lines':>5}  changes since the last row")
    for f in sorted((rec / "country").glob("*.txt.gz")):
        date = f.name[:10]
        text = gzip.open(f, "rt", encoding="utf-8", errors="replace").read()
        ls = [l for l in lines(text) if l["active"] > 0]
        if not ls:
            continue
        mults = [l["speed"] / (SPEED * sum(e / 100 for e in l["eff"][:l["active"]])) for l in ls if sum(l["eff"][:l["active"]]) > 0]
        cap = max(max(l["eff"]) for l in ls)
        idle = [l["eff"][-1] for l in ls if len(l["eff"]) > l["active"]]
        row = (round(statistics.median(mults), 3), round(cap, 1), round(statistics.median(idle), 1) if idle else None)
        if row != last:
            techs = [r.get("tech") for r in research if since < iso(r["date"]) <= date]
            pol = [f"{r.get('slot') or r.get('kind')}={r.get('value')}" for r in politics if since < iso(r["date"]) <= date]
            spread = f" (lines {min(mults):.3f}..{max(mults):.3f})" if max(mults) - min(mults) > 0.01 else ""
            print(f"{date} {row[0]:6.3f} {row[1]:5.1f} {row[2] if row[2] is not None else '-':>5} {len(ls):5d}  "
                  f"{' '.join(techs)} {' '.join(pol)}{spread}".rstrip())
            last = row
            since = date


if __name__ == "__main__":
    main()
