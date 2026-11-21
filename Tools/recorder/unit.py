"""Everything one division did, in time order, across all files of a recording.

    python unit.py [REC] ID [--all]

Divisions rows (snapshots are skipped unless --all), notes about it, commands naming it, battles it fought in
(the first and last hour of each run of the same battle, not every hour).
"""
import argparse
import re

import _engine  # noqa: F401  (engine tools on the path, Recordings/ as the root)
import rec


def main():
    rec.setup_stdout()
    ap = argparse.ArgumentParser()
    ap.add_argument("args", nargs="+")
    ap.add_argument("--all", action="store_true")
    a = ap.parse_args()
    folder = a.args[0] if len(a.args) > 1 else None
    uid = int(a.args[-1])
    path = rec.find(folder)
    events = []

    _, divs = rec.load(path, "divisions")
    for r in divs:
        if int(r["id"]) != uid or (r["why"] == "snap" and not a.all):
            continue
        events.append((int(r["tick"]), 0, r["date"], "division",
                       f"{r['why']:6} {r['tag']} at {r['province']} dest {r['dest']} plan {r['plan']} slot {r['slot']} "
                       f"army {r['army']} {r['state']} org {r['org']}% str {r['str']}%"))

    _, notes = rec.load(path, "notes")
    for r in notes:
        if r["unit"] == str(uid):
            events.append((int(r["tick"]), 1, r["date"], "note",
                           f"{r['system']}/{r['kind']} at {r['province']} -> {r['target']}: {r['text']}"))

    _, cmds = rec.load(path, "commands")
    pat = re.compile(rf"(?<![\d]){uid}(?![\d])")
    for r in cmds:
        if pat.search(r["fields"]):
            events.append((int(r["tick"]), 2, r["date"], "command", f"{r['command']} {r['fields'][:300]}"))

    _, battles = rec.load(path, "battles")
    runs = {}  # (to, side) -> [first tick, first date, last tick, last date, percent first, percent last]
    for r in battles:
        side = "attacker" if uid in rec.ids(r["attackers"]) else "defender" if uid in rec.ids(r["defenders"]) else None
        if not side:
            continue
        key = (r["to"], side)
        t = int(r["tick"])
        front = uid in rec.ids(r["attacker_front"] if side == "attacker" else r["defender_front"])
        run = runs.get(key)
        if run and t - run[2] <= 1:
            run[2], run[3], run[5], run[6] = t, r["date"], r["percent"], run[6] or front
        else:
            if run:
                events.append(battle_event(key, run))
            runs[key] = [t, r["date"], t, r["date"], r["percent"], r["percent"], front]
    for key, run in runs.items():
        events.append(battle_event(key, run))

    events.sort(key=lambda e: (e[0], e[1]))
    if not events:
        print(f"nothing about division {uid} in {path}")
        return
    print(f"division {uid} in {path}")
    for t, _, date, kind, text in events:
        print(f"{t:6} {date}  {kind:8} {text}")


def battle_event(key, run):
    to, side = key
    hours = run[2] - run[0] + 1
    return (run[0], 3, run[1], "battle",
            f"{side} at {to} for {hours}h (until {run[3]}), attacker bar {run[4]}% -> {run[5]}%"
            + ("" if run[6] else ", in reserve"))


if __name__ == "__main__":
    main()
