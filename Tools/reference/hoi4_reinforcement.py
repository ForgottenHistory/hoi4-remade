"""How fast real HOI4 refills divisions, from a recording of the HOI4 reference workspace (hoi4-recorder):
day-to-day strength gains in its daily unit_snapshots.tsv.

    python Tools/reference/hoi4_reinforcement.py REC [TAG ...] [--from 1939-09-01] [--workspace DIR]

REC is a folder under <workspace>/hoi4-recorder/recordings (or a path). Strength is column i30 / 1000 (100 = full;
HOI4 strength is equipment AND manpower). A division below 10% the day before is skipped: those are new divisions
appearing at 0 or reused ids, not reinforcement. With battle_divisions.tsv in the recording, gains on a day the
division fought are counted apart ("in battle").

Measured 2026-11-21 (Poland 1939, from 1 September): Germany median 7.5 to 16.5 points of full strength a day,
middle half 2 to 32, a tenth of the days over 50 (deliveries arrive in lumps); Poland median 3 to 5, and about
half of its damaged division-days gained nothing (short of equipment). Hoi4Manpower refills 10% a day.
"""
import argparse
from collections import Counter, defaultdict
from pathlib import Path

DEFAULT_WORKSPACE = r"D:\Stuff\Coding\hoi4"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("rec")
    ap.add_argument("tags", nargs="*", default=["GER", "POL"])
    ap.add_argument("--from", dest="start", default="", help="first date (yyyy-mm-dd), e.g. the war's start")
    ap.add_argument("--workspace", default=DEFAULT_WORKSPACE)
    a = ap.parse_args()
    rec = Path(a.rec)
    if not rec.exists():
        rec = Path(a.workspace) / "hoi4-recorder" / "recordings" / a.rec
    tags = set(a.tags)

    snaps = []  # (date, {(handle, id): (owner, strength)}); a day's two blocks (handles 51 and 4713) together
    cols, handle = None, "51"
    for line in open(rec / "unit_snapshots.tsv", encoding="utf-8", errors="replace"):
        p = line.rstrip("\n").split("\t")
        if p[0] == "#snap":
            if not snaps or snaps[-1][0] != p[1]:
                snaps.append((p[1], {}))
            handle = p[2]
            cols = [c for c in p[3:] if not c.startswith("class=")]
            continue
        if not snaps or cols is None or len(p) < len(cols) + 1:
            continue
        row = dict(zip(cols, p[1:]))
        snaps[-1][1][(handle, int(p[0]))] = (row["c1c8"], int(row["i30"]) / 1000)
    snaps.sort()
    snaps = [s for s in snaps if s[0] >= a.start]
    if len(snaps) < 2:
        print("fewer than two days of snapshots")
        return

    in_battle = set()  # (date, division id)
    bd = rec / "battle_divisions.tsv"
    if bd.exists():
        for line in list(open(bd, encoding="utf-8"))[1:]:
            p = line.rstrip("\n").split("\t")
            in_battle.add((p[0], p[3]))

    gains, battle_gains = defaultdict(list), defaultdict(list)
    below, rose = Counter(), Counter()
    for (d0, day0), (d1, day1) in zip(snaps, snaps[1:]):
        for k, (owner, s1) in day1.items():
            if owner not in tags or k not in day0:
                continue
            s0 = day0[k][1]
            if s0 < 10:
                continue
            if s0 < 99:
                below[owner] += 1
            if s1 > s0 + 0.01:
                rose[owner] += 1
                fought = (d1, str(k[1])) in in_battle or (d0, str(k[1])) in in_battle
                (battle_gains if fought else gains)[owner].append(s1 - s0)

    print(f"{rec.name}: {len(snaps)} days {snaps[0][0]} .. {snaps[-1][0]}")
    for owner in sorted(tags):
        g = sorted(gains[owner] + battle_gains[owner])
        if not g:
            print(f"  {owner}: no gains")
            continue
        q = lambda f: g[min(len(g) - 1, int(f * len(g)))]
        print(f"  {owner}: {below[owner]} damaged division-days, {rose[owner]} gained; points of full strength a day: "
              f"median {q(0.5):.1f}, middle half {q(0.25):.1f} to {q(0.75):.1f}, top tenth over {q(0.9):.1f}, max {g[-1]:.1f}"
              + (f"; {len(battle_gains[owner])} of the gains on a battle day" if in_battle else ""))
        hist = Counter(min(int(x // 2) * 2, 30) for x in g)
        print("     2-point bins (30 = 30 or more):", " ".join(f"{k}:{hist[k]}" for k in sorted(hist)))


if __name__ == "__main__":
    main()
