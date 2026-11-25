"""Prototype of HOI4 research cost and speed, checked against a real game: the reference workspace's daily country
blocks (save format) of a German 1936 game, D:/Stuff/Coding/hoi4/hoi4-recorder/recordings/vanilla_ger_1936
(played on hard: -15% research speed).

The model (NTechnology defines + what the save shows):
- A technology needs research_cost x BASE_TECH_COST (110) research points (a finished tech's research_points).
- A slot gains, every day:  max(MIN_RESEARCH_SPEED, 1 + research speed + bonuses) / (1 + ahead penalty)
  research speed: the sum of research_speed_factor (laws, ideas, technologies, difficulty: hard -0.15), so 1936
  Germany on normal makes 1.01 a day (0.86 on hard); a speed change counts from the next day.
  bonuses: a limited bonus (+1.0 for +100%), a design team...; added to the speed, not multiplied.
  ahead penalty: BASE_YEAR_AHEAD_PENALTY_FACTOR (2) x years before 1 January of the tech's start_year (days / 365),
  0 once there; recomputed every day, so ahead research speeds up as the year nears.
  The save caches the start day's value as the slot's points_factor; the daily gain follows today's.
- An empty slot saves a point a day up to BASE_RESEARCH_POINTS_SAVED (30); the next tech in the slot gets them at
  once, times the slot's rate (Germany, 4 January 1936: 3 saved days + the day = 4 x 0.86 = 3.44 points).
- A tech several others lead to needs any one of them (other games' research.tsv: started with one parent of two).

Checks:
1. every finished tech's research_points == research_cost x 110 (path research_cost_coeff ignored: all 1 here)
2. every slot's cached points_factor == (base + bonus) / (1 + 2 x years ahead), base from the day's plain slots
3. ahead-of-time starts: the start day the model needs lands 0..7 days before the slot was read
4. techs with several parents: started with all of them, or only some
5. the penalty day by day: an ahead tech's daily gain x (1 + 2 x years ahead today) stays constant (speed + bonus)

    python Tools/prototypes/research/research_cost.py [RECORDING]
"""
import datetime as dt
import gzip
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Tools"))
from hoi4_convert import parse_pdx, read_text, get  # noqa: E402

HOI4 = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV")
REC = Path(r"D:\Stuff\Coding\hoi4\hoi4-recorder\recordings\vanilla_ger_1936")
BASE_TECH_COST = 110
YEAR_AHEAD_FACTOR = 2


def load_techs():
    """{name: {cost, year}} from common/technologies (the @year constants resolved per file)."""
    techs = {}
    for f in sorted((HOI4 / "common" / "technologies").glob("*.txt")):
        for key, _, block in parse_pdx(read_text(f)):
            if key != "technologies" or not isinstance(block, list):
                continue
            for name, _, t in block:
                if not isinstance(t, list) or (isinstance(name, str) and name.startswith("@")):
                    continue
                try:
                    techs[name] = {"cost": float(get(t, "research_cost") or 0), "year": int(get(t, "start_year") or 0)}
                except ValueError:
                    pass
    return techs


def block(t, key, start=0):
    i = t.find(key + "={", start)
    if i < 0:
        return None
    d = 0
    for j in range(i + len(key) + 1, len(t)):
        if t[j] == "{":
            d += 1
        elif t[j] == "}":
            d -= 1
            if d == 0:
                return t[i:j + 1]
    return None


def years_ahead(day, year):
    return max(0.0, (dt.date(year, 1, 1) - day).days / 365.0) if year > 0 else 0.0


def main():
    rec = Path(sys.argv[1]) if len(sys.argv) > 1 else REC
    techs = load_techs()
    print(f"{len(techs)} technologies")
    files = sorted((rec / "country").glob("*.txt.gz"))
    # 1. finished techs: research_points == cost x 110
    last = gzip.open(files[-1], "rt", encoding="utf-8", errors="replace").read()
    done = re.findall(r"\n\t+(\w+)=\{\s*level=1\s*research_points=([\d.]+)", block(last, "technology"))
    bad = [(n, float(p), techs[n]["cost"] * BASE_TECH_COST) for n, p in done
           if n in techs and abs(float(p) - techs[n]["cost"] * BASE_TECH_COST) > 0.01]
    print(f"1. finished: {len(done)}, research_points != cost x {BASE_TECH_COST}: {len(bad)}", bad[:5])

    # 2. each newly started slot's points_factor against the model
    seen, rows = set(), []
    for f in files:
        day = dt.date.fromisoformat(f.name[:10])
        t = gzip.open(f, "rt", encoding="utf-8", errors="replace").read()
        tech = block(t, "technology")
        slots = block(tech, "slots") or ""
        entries = re.findall(r"(\w+)=\{[^}]*?points_factor=([\d.]+)", slots)
        # the day's base: the most common factor among slots researching a tech not ahead of time
        plain = [float(p) for n, p in entries if n in techs and years_ahead(day, techs[n]["year"]) == 0]
        base = max(set(plain), key=plain.count) if plain else None
        for n, p in entries:
            if n in seen or n not in techs:
                continue
            seen.add(n)
            ya = years_ahead(day, techs[n]["year"])
            rows.append((day, n, float(p), base, ya))
    print("2. slot factors (read the day the slot first shows; the research started up to a few days before)")
    print(f"   {'date':10} {'tech':34} {'factor':>8} {'base':>6} {'ahead y':>7} {'model':>7} {'bonus?':>7}")
    ok = 0
    for day, n, p, base, ya in rows:
        if base is None:
            continue
        model = base / (1 + YEAR_AHEAD_FACTOR * ya)
        bonus = p * (1 + YEAR_AHEAD_FACTOR * ya) - base   # what the numerator needs on top of the base
        hit = abs(p - model) <= 0.01
        ok += hit
        print(f"   {day} {n:34} {p:8.5f} {base:6.2f} {ya:7.3f} {model:7.4f} {'' if hit else f'{bonus:+.3f}':>7}")
    print(f"   {ok} of {sum(1 for r in rows if r[3] is not None)} match the base / (1 + 2 x years ahead); the rest carry a bonus"
          " (limited bonus +1.0, design team) or were read days after they started")

    # 3. ahead-of-time techs: the start day the model needs, for the plain speeds of the time (no bonus). The research
    # started on or a few days before the read, so a fit 0..7 days back supports the model.
    # plain speeds: every factor of a tech not ahead of time, without the big bonuses (limited bonus +1.0 and up)
    speeds = sorted({r[3] for r in rows if r[3]} | {r[2] for r in rows if r[4] == 0 and r[2] < 1.1})
    print("3. ahead of time: start day the model needs (base / factor = 1 + 2 x years ahead), days before the read")
    for day, n, p, base, ya in rows:
        if ya == 0:
            continue
        fits = []
        for s in speeds:
            start = dt.date(techs[n]["year"], 1, 1) - dt.timedelta(days=365 * (s / p - 1) / YEAR_AHEAD_FACTOR)
            fits.append(((day - start).days, s))
        near = [f for f in fits if 0 <= f[0] <= 7]
        print(f"   {day} {n:34} {p:8.5f}  " + (f"fits speed {near[0][1]:.2f}, started {near[0][0]} days before"
                                                if near else "no plain speed fits: a bonus on top"))


def check_parents(rec):
    """4. Prerequisites: a tech several others lead to (path leads_to_tech), does it need all of them or any one?
    For every tech a country started (research.tsv), which of its parents it had by then (its 1936 techs from
    Hoi4Data/common/hoi4_technologies.json5 plus what research.tsv shows done). Only some, but at least one: any."""
    import csv
    import json
    data = ROOT / "Hoi4Data" / "common" / "hoi4_technologies.json5"
    techs = json.loads(data.read_text(encoding="utf-8").split("\n", 1)[1])
    T = techs["technologies"]
    parents = {}
    for n, t in T.items():
        for child, _ in t.get("leads_to", []):
            parents.setdefault(child, []).append(n)
    known, some, every = {}, [], 0
    with open(rec / "research.tsv", encoding="utf-8") as f:
        for r in csv.DictReader(f, delimiter="\t"):
            c = r["country"]
            k = known.setdefault(c, set(techs["countries"].get(c, [])))
            if r["change"] == "done":
                k.add(r["tech"])
            elif r["tech"] and len(parents.get(r["tech"], [])) > 1 and r["tech"] not in k:
                have = [p for p in parents[r["tech"]] if p in k]
                if len(have) == len(parents[r["tech"]]):
                    every += 1
                else:
                    some.append((c, r["date"], r["tech"], "has " + ",".join(have) or "-",
                                 "lacks " + ",".join(p for p in parents[r["tech"]] if p not in k)))
    print(f"4. {rec.name}: techs with several parents started: {every} with all parents, {len(some)} with only some")
    for s in some[:12]:
        print("  ", *s)


def check_daily_penalty(rec, tech="artillery1", year=1939, plain=("fuel_refining2", "oil_processing"), months="1938-"):
    """5. An ahead tech's daily gain over ~10-day spans, x (1 + 2 x years ahead at the span's middle): constant when
    the penalty is recomputed daily (the speed and bonus don't change), against a plain tech's gain the same days."""
    snap = {}
    for f in sorted((rec / "country").glob(months + "*.txt.gz")):
        tech_block = block(gzip.open(f, "rt", encoding="utf-8", errors="replace").read(), "technology") or ""
        rp = {}
        for n in (tech,) + plain:
            m = re.search(r"\n\t+" + n + r"=\{\s*research_points=([\d.]+)", tech_block)
            if m:
                rp[n] = float(m.group(1))
        snap[dt.date.fromisoformat(f.name[:10])] = rp
    days = sorted(snap)
    print(f"5. {tech} (start_year {year}) day by day: gain x (1 + 2 x years ahead) should stay constant")
    i = 0
    while i < len(days):
        j = i + 1
        while j < len(days) and (days[j] - days[i]).days < 10:
            j += 1
        if j >= len(days):
            break
        a, b = snap[days[i]], snap[days[j]]
        n = (days[j] - days[i]).days
        if tech in a and tech in b:
            gain = (b[tech] - a[tech]) / n
            ya = years_ahead(days[i] + (days[j] - days[i]) / 2, year)
            ref = next(((b[p] - a[p]) / n for p in plain if p in a and p in b and b[p] > a[p]), None)
            print(f"   {days[i]}..{days[j]} gain {gain:.4f} ahead {ya:.3f} y  -> x (1 + 2y) = {gain * (1 + 2 * ya):.3f}"
                  + (f"   plain tech {ref:.3f}" if ref else ""))
        i = j


if __name__ == "__main__":
    main()
    check_parents(Path(sys.argv[1]) if len(sys.argv) > 1 else REC)
    check_parents(REC.parent / "meme_ger")  # a longer game: every country's research, some starting multi-parent techs
    check_daily_penalty(Path(sys.argv[1]) if len(sys.argv) > 1 else REC)
