"""Battles of a recording grouped into whole battles: outcome by terrain and river, by attacker, and why
defenders started a battle without entrenchment.

    python battles.py [REC]

A battle is the hours in battles.tsv with the same target province in a row. "Took it" means the province's
controller became one of the attackers' countries within two days of the battle's last hour (control.tsv).
Needs the terrain/river/entrenchment columns (recordings from 2026-11-21 on).
"""
import argparse
from collections import Counter, defaultdict

import _engine  # noqa: F401  (engine tools on the path, Recordings/ as the root)
import rec


def main():
    rec.setup_stdout()
    ap = argparse.ArgumentParser()
    ap.add_argument("rec", nargs="?")
    a = ap.parse_args()
    path = rec.find(a.rec)

    _, battles = rec.load(path, "battles")
    _, divs = rec.load(path, "divisions")
    _, control = rec.load(path, "control")
    if battles and "terrain" not in battles[0]:
        print("this recording has no terrain columns (older than 2026-11-21)")
        return

    tag, states = {}, defaultdict(list)  # division -> country; division -> [(tick, state)]
    for r in divs:
        tag[int(r["id"])] = r["tag"]
        states[int(r["id"])].append((int(r["tick"]), r["state"]))

    episodes, open_ = [], {}
    for r in battles:
        t, to = int(r["tick"]), r["to"]
        e = open_.get(to)
        if e and t - e["last"] <= 1:
            e["rows"].append(r)
            e["last"] = t
        else:
            open_[to] = e = {"to": to, "first": t, "last": t, "rows": [r]}
            episodes.append(e)

    flips = defaultdict(list)
    for r in control:
        flips[r["province"]].append((int(r["tick"]), r["to"]))

    def took(e):
        attackers = {tag.get(x) for x in rec.ids(e["rows"][0]["attackers"])}
        return any(e["first"] <= t <= e["last"] + 48 and c in attackers for t, c in flips[e["to"]])

    def still_hours(uid, tick):
        """Hours the division had stood still (idle or defending) before tick; -1 when it was on the move.
        Division rows are written on change, so a state holds until the next row."""
        since = None
        for t, state in states[uid]:
            if t >= tick:
                break
            since = (since if since is not None else t) if state in ("idle", "defend") else None
        return -1 if since is None else tick - since

    by_ground = defaultdict(lambda: [0, 0])
    by_side = defaultdict(lambda: [0, 0, 0])
    unentrenched = Counter()
    for e in episodes:
        r0 = e["rows"][0]
        won = took(e)
        g = by_ground[(r0["terrain"], r0["river"] or "-")]
        g[0] += 1
        g[1] += won
        s = by_side[tag.get(rec.ids(r0["attackers"])[0], "?")]
        s[0] += 1
        s[1] += won
        s[2] += len(e["rows"])
        if r0["defender_entrenchment"] in ("0", ""):
            hs = [still_hours(d, e["first"]) for d in rec.ids(r0["defender_front"])]
            if any(h == -1 for h in hs):
                unentrenched["a defender was on the move"] += 1
            elif hs and min(hs) < 24:
                unentrenched["stopped less than a day before"] += 1
            else:
                unentrenched["STILL FOR A DAY OR MORE (check)"] += 1

    print(f"# {path}: {len(episodes)} battles")
    print("\n## Terrain and river: battles, attacker took the province")
    for k in sorted(by_ground, key=lambda k: -by_ground[k][0]):
        n, w = by_ground[k]
        print(f"  {k[0]:9} {k[1]:6} {n:4}  {w:4} ({100 * w // n}%)")
    print("\n## By attacker: battles, took the province, mean length")
    for c, (n, w, h) in sorted(by_side.items(), key=lambda kv: -kv[1][0]):
        print(f"  {c:6} {n:4} battles, took {w} ({100 * w // n}%), {h / n:.0f}h")
    entrenched = sum(1 for e in episodes if e["rows"][0]["defender_entrenchment"] not in ("0", ""))
    print(f"\n## Entrenchment: defenders dug in at the start of {entrenched} of {len(episodes)} battles; the rest:")
    for k, n in unentrenched.most_common():
        print(f"  {n:4}  {k}")


if __name__ == "__main__":
    main()
