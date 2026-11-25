"""Prototype of HOI4's research AI, checked against the AI's real picks in the reference workspace's recordings
(research.tsv: every country's slots and finished techs; war_stats.tsv: wars and their start).

Score of a technology the country can start (Hoi4Data/common/hoi4_technologies.json5):
  ai_will_do: base, then each modifier whose trigger holds (factor multiplies, add adds), in order. Triggers we can't
              evaluate leave their modifier out.
  x strategies: each active ai_strategy research_weight_factor: x (1 + value / 100); research_tech: forced first.
  x the AI defines (NAI), as variants to compare:
      ahead:   RESEARCH_AHEAD_OF_TIME_FACTOR (4), MAX_AHEAD_RESEARCH_PENALTY (3: no tech whose penalty 2 x years ahead
               is past it)
      length:  RESEARCH_LENGTH_FACTOR (3), RESEARCH_BASE_DAYS (60): shorter techs first
      behind:  RESEARCH_YEARS_BEHIND_FACTOR (0.2): old techs first
Pick: at random among those scoring at least RESEARCH_WEIGHT_TRUNCATION_THRESHOLD (0.75) of the best.

The check replays each AI country's research (its 1936 techs plus what research.tsv shows done) and, at every pick it
made, scores the techs it could have started: how often the real pick is in the 75% band, its median rank, and a
random pick's chance of the same, for each variant.

    python Tools/prototypes/research/research_ai.py [RECORDING ...]
"""
import csv
import datetime as dt
import json
import random
import re
import statistics
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
DATA = ROOT / "Hoi4Data"
RECS = Path(r"D:\Stuff\Coding\hoi4\hoi4-recorder\recordings")
TRUNCATION = 0.75            # RESEARCH_WEIGHT_TRUNCATION_THRESHOLD
AHEAD_FACTOR = 4.0           # RESEARCH_AHEAD_OF_TIME_FACTOR
MAX_AHEAD_PENALTY = 3.0      # MAX_AHEAD_RESEARCH_PENALTY (includes BASE_YEAR_AHEAD_PENALTY_FACTOR 2)
LENGTH_FACTOR = 3.0          # RESEARCH_LENGTH_FACTOR
BASE_DAYS = 60.0             # RESEARCH_BASE_DAYS
BEHIND_FACTOR = 0.2          # RESEARCH_YEARS_BEHIND_FACTOR
MIN_MAJOR, MAJOR_RATIO = 7, 0.7   # NCountry MIN_MAJOR_COUNTRIES, ADDITIONAL_MAJOR_COUNTRIES_IC_RATIO
MONTHS = {m: i + 1 for i, m in enumerate(["January", "February", "March", "April", "May", "June", "July", "August",
                                          "September", "October", "November", "December"])}


def json5(path):
    t = path.read_text(encoding="utf-8")
    return json.loads(re.sub(r"^\s*//.*$", "", t, flags=re.M))


def rec_date(s):
    """'13:00, 4 January, 1936' -> date."""
    m = re.match(r"\d+:\d+, (\d+) (\w+), (\d+)", s)
    return dt.date(int(m.group(3)), MONTHS[m.group(2)], int(m.group(1)))


def pdx_date(s):
    p = [int(x) for x in str(s).split(".")[:3]]
    return dt.date(p[0], p[1] if len(p) > 1 else 1, p[2] if len(p) > 2 else 1)


def cmp(a, op, b):
    return {"=": a == b, ">": a > b, "<": a < b, ">=": a >= b, "<=": a <= b, "!=": a != b}.get(op)


class Country:
    def __init__(self, tag, known, factories, major):
        self.tag, self.known, self.f, self.major = tag, set(known), factories, major
        self.slots = {}
        self.wars = []   # start dates


def trigger(items, c, day):
    """AND of the lines; None when one can't be evaluated (and nothing else already makes it false)."""
    unknown = False
    for k, op, v in items:
        r = line(k, op, v, c, day)
        if r is False:
            return False
        if r is None:
            unknown = True
    return None if unknown else True


def line(k, op, v, c, day):
    if k in ("and",):
        return trigger(v, c, day)
    if k == "or":
        rs = [line(kk, oo, vv, c, day) for kk, oo, vv in v]
        return True if True in rs else (None if None in rs else False)
    if k == "not":
        r = trigger(v, c, day)
        return None if r is None else not r
    if k == "date":
        return cmp(day, op, pdx_date(v))
    if k in ("tag", "original_tag"):
        return c.tag == v
    if k == "is_major":
        return c.major == (v == "yes")
    if k == "has_war":
        return (any(w <= day for w in c.wars)) == (v == "yes")
    if k == "has_tech":
        return v in c.known
    if k in ("has_dlc", "always"):
        return True if k == "has_dlc" else v == "yes"
    if k in ("num_of_military_factories", "num_of_naval_factories", "num_of_factories", "num_of_civilian_factories"):
        n = {"num_of_military_factories": c.f["mil"], "num_of_naval_factories": c.f["nav"],
             "num_of_civilian_factories": c.f["civ"], "num_of_factories": c.f["mil"] + c.f["civ"] + c.f["nav"]}[k]
        try:
            return cmp(n, op, float(v))
        except (TypeError, ValueError):
            return None
    if k == "is_ai":
        return v == "yes"
    return None


def ai_will_do(t, c, day):
    ai = t.get("ai") or {"base": 1.0, "modifiers": []}
    w = ai["base"]
    for m in ai["modifiers"]:
        if trigger(m["if"], c, day) is True:
            if "factor" in m:
                w *= m["factor"]
            if "add" in m:
                w += m["add"]
    return w


def strategies(strats, c, day):
    """Active strategies' weights {tech: factor} and forced techs."""
    weights, force = {}, []
    for s in strats:
        if trigger(s["allowed"], c, day) is not True or trigger(s["enable"], c, day) is not True:
            continue
        if s["abort"] and trigger(s["abort"], c, day) is True:
            continue
        for tech, v in s["weights"].items():
            weights[tech] = weights.get(tech, 1.0) * (1 + v / 100)
        force += s["force"]
    return weights, force


def years_ahead(t, day):
    return max(0.0, (dt.date(t["year"], 1, 1) - day).days / 365) if t.get("year") else 0.0


def score(name, t, c, day, weights, variant):
    w = ai_will_do(t, c, day) * weights.get(name, 1.0)
    if w <= 0:
        return 0.0
    ya = years_ahead(t, day)
    if "ahead" in variant:
        if 2 * ya > MAX_AHEAD_PENALTY:
            return 0.0
        w /= 1 + AHEAD_FACTOR * 2 * ya
    if "length" in variant:
        days = t["cost"] * 110 * (1 + 2 * ya)       # at speed 1
        w *= (BASE_DAYS / (BASE_DAYS + days)) ** (LENGTH_FACTOR / 3)
    if "behind" in variant and t.get("year"):
        behind = max(0.0, (day - dt.date(t["year"], 1, 1)).days / 365)
        w *= 1 + BEHIND_FACTOR * behind
    return w


def candidates(T, c):
    out = []
    held = set(c.slots.values())
    for n, t in T.items():
        if n in c.known or n in held or t.get("focus_only"):
            continue
        if t.get("_parents") and not any(p in c.known for p in t["_parents"]):
            continue
        if any(r not in c.known for r in t.get("requires", [])) or any(x in c.known for x in t.get("xor", [])):
            continue
        out.append(n)
    return out


def factories():
    sd = json5(DATA / "map" / "hoi4_state_data.json5")
    f = {}
    for s in sd["states"].values():
        o = s.get("owner")
        b = s.get("buildings", {})
        x = f.setdefault(o, {"civ": 0, "mil": 0, "nav": 0})
        x["civ"] += b.get("industrial_complex", 0)
        x["mil"] += b.get("arms_factory", 0)
        x["nav"] += b.get("dockyard", 0)
    total = sorted(((v["civ"] + v["mil"] + v["nav"], k) for k, v in f.items()), reverse=True)
    top = total[:MIN_MAJOR]
    avg = sum(n for n, _ in top) / len(top)
    majors = {k for n, k in total if (n, k) in top or n > MAJOR_RATIO * avg}
    return f, majors


def replay(rec, data, variants, player="GER"):
    T, strats = data["technologies"], data.get("ai_strategies", [])
    f, majors = factories()
    wars = {}
    ws = rec / "war_stats.tsv"
    if ws.exists():
        with open(ws, encoding="utf-8") as fh:
            for r in csv.DictReader(fh, delimiter="\t"):
                start = pdx_date(r["start"])
                for side in ("first", "second"):
                    wars.setdefault(r[side], set()).add(start)
    countries = {}
    stats = {v: {"band": 0, "ranks": [], "n": 0, "random_band": 0.0} for v in variants}
    with open(rec / "research.tsv", encoding="utf-8") as fh:
        rows = list(csv.DictReader(fh, delimiter="\t"))
    for r in rows:
        tag = r["country"]
        if not tag:
            continue
        c = countries.get(tag)
        if c is None:
            c = countries[tag] = Country(tag, data["countries"].get(tag, []),
                                         f.get(tag, {"civ": 0, "mil": 0, "nav": 0}), tag in majors)
            c.wars = sorted(wars.get(tag, []))
        if r["change"] == "done":
            c.known.add(r["tech"])
            continue
        if r["change"] != "changed" or not r["slot"]:
            continue
        slot, tech = r["slot"], r["tech"]
        old = c.slots.get(slot)
        c.slots.pop(slot, None)
        # a pick: a tech into a slot that was empty or whose tech just finished (not a switch mid-research)
        # rows dated "?" (the recorder couldn't read the date) can't be scored
        if tech and tech in T and tag != player and (old is None or old in c.known) and r["date"] != "?":
            day = rec_date(r["date"])
            cands = candidates(T, c)
            if tech in cands and len(cands) > 1:
                weights, force = strategies(strats, c, day)
                for v in variants:
                    sc = {n: score(n, T[n], c, day, weights, v) for n in cands}
                    best = max(sc.values())
                    band = [n for n in cands if best > 0 and sc[n] >= TRUNCATION * best]
                    st = stats[v]
                    st["n"] += 1
                    st["band"] += tech in band
                    st["random_band"] += len(band) / len(cands)
                    order = sorted(cands, key=lambda n: -sc[n])
                    st["ranks"].append(order.index(tech) / (len(cands) - 1))
        if tech:
            c.slots[slot] = tech
    return stats


def main():
    recs = [Path(a) for a in sys.argv[1:]] or [RECS / "vanilla_ger_1936", RECS / "meme_ger"]
    data = json5(DATA / "common" / "hoi4_technologies.json5")
    for n, t in data["technologies"].items():
        for child, _ in t.get("leads_to", []):
            data["technologies"][child].setdefault("_parents", []).append(n)
    variants = ["plain", "ahead", "ahead+length", "ahead+behind", "ahead+length+behind"]
    for rec in recs:
        stats = replay(rec, data, variants)
        print(f"{rec.name}: AI picks scored (the real pick among the techs the country could start)")
        print(f"   {'variant':22} {'picks':>6} {'in 75% band':>12} {'random in band':>15} {'median rank':>12}")
        for v in variants:
            st = stats[v]
            if not st["n"]:
                continue
            print(f"   {v:22} {st['n']:6} {st['band'] / st['n']:12.1%} {st['random_band'] / st['n']:15.1%}"
                  f" {statistics.median(st['ranks']):12.2f}")
    print("median rank: 0 = the pick was our top score, 1 = our lowest")


if __name__ == "__main__":
    random.seed(1)
    main()
