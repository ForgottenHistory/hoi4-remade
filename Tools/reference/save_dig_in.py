"""Entrenchment of the divisions in every land battle of a plain-text HOI4 save: dig_in / dig_in_cap per division,
defenders and attackers, with the battle's length in hours.

    python Tools/reference/save_dig_in.py SAVE.hoi4 [--saves DIR]

SAVE is a file name in the HOI4 save games folder (or a path). A division without dig_in in the save has 0.
Checked 2026-11-21 (Poland 1939): defenders keep 5.0/5.0 through battles of 92 to 823 hours, attackers are at 0,
so a defensive battle does not use entrenchment up (as Hoi4Combat models it).
"""
import argparse
import os
import re
from pathlib import Path

DEFAULT_SAVES = Path(os.environ.get("USERPROFILE", "~")) / "Documents" / "Paradox Interactive" / "Hearts of Iron IV" / "save games"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("save")
    ap.add_argument("--saves", default=str(DEFAULT_SAVES))
    a = ap.parse_args()
    path = Path(a.save)
    if not path.exists():
        path = Path(a.saves) / a.save
    lines = path.read_text(encoding="utf-8", errors="replace").split("\n")
    if not lines or not lines[0].startswith("HOI4txt"):
        print("not a plain-text save (save with the game's plain-text option)")
        return

    unit_id = re.compile(r"^\t+id=\{ id=(\d+) type=51 \}")
    dig, cap = {}, {}
    cur = None
    start = next((i for i, l in enumerate(lines) if l == "combat={"), len(lines))
    for l in lines[:start]:
        m = unit_id.match(l)
        s = l.strip()
        if m:
            cur = int(m.group(1))
        elif cur is not None and s.startswith("dig_in=") and cur not in dig:
            dig[cur] = float(s.split("=")[1])
        elif cur is not None and s.startswith("dig_in_cap=") and cur not in cap:
            cap[cur] = float(s.split("=")[1])

    combats, c, side = [], None, None
    for l in lines[start:]:
        s = l.strip()
        if l == "\tland_combat={":
            c = {"att": [], "def": [], "dur": 0, "loc": 0}
            combats.append(c)
            side = None
        elif c is None:
            continue
        elif l == "\t\tattacker={":
            side = "att"
        elif l == "\t\tdefender={":
            side = "def"
        elif l.startswith("\t\tduration="):
            c["dur"] = int(s.split("=")[1])
        elif l.startswith("\t\tlocation="):
            c["loc"] = int(s.split("=")[1])
        elif side and l.startswith("\t\t\tunit={"):
            c[side].append(int(re.search(r"id=(\d+)", l).group(1)))
        elif l == "}":
            break

    fmt = lambda ids: " ".join(f"{dig.get(i, 0)}/{cap.get(i, '?')}" for i in ids)
    print(f"{path.name}: {len(combats)} land battles; dig_in/cap per division")
    for c in sorted(combats, key=lambda c: c["dur"]):
        print(f"  province {c['loc']:6} {c['dur']:4}h  defenders {fmt(c['def'])}   attackers {fmt(c['att'])}")


if __name__ == "__main__":
    main()
