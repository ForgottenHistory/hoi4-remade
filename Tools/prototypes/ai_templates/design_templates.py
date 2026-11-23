"""AI division templates by role (prototype).

Each AI country keeps one template per role, improved in place (no copies, no decommissioning):
  defence    - defence infantry: holds the line cheaply (defense, organisation, HP)
  offence    - offensive infantry: pushes (soft attack, breakthrough), artillery heavy
  mobile     - motorized / cavalry: as fast as its mobile battalions, then attack
  breakthrough - tanks: breakthrough, hardness, armour, soft attack (only with tank production or stock)

Start: the 1936 templates are sorted into roles (the most common infantry one is defence, offence starts as a copy of
it, the fastest non-tank one mobile, the one with most tanks breakthrough); the rest train no more and go once empty.
Design: hill climbing from the role's template over single edits (add / remove / swap a battalion, add / remove a
support or regimental company), keeping the designer's rules, scoring with the designer's stat sums (as
Hoi4Divisions.Stats). Sub-units: the country's (unlocked, equipment buildable); their equipment's production cost counts.
Recruitment then trains roles by a mix, mostly defence.

    python Tools/prototypes/ai_templates/design_templates.py [TAG ...]
"""
import sys
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Tools"))
sys.path.insert(0, str(ROOT / "Tools" / "prototypes" / "designer"))
from manpower_pools import json5  # noqa: E402
from render_designer import stats  # noqa: E402

COLUMNS, ROWS, SUPPORT, REGIMENTAL_NEEDS = 5, 4, 5, 3

# Battalions each role may use (by sub-unit group), its target combat width, and its stat weights. Stats are taken
# per combat width (sums) or as they are (organisation, hardness, speed), relative to a plain infantry division.
ROLES = {
    "defence": dict(groups={"infantry", "combat_support"}, width=(16, 20), org_min=50,
                    weights={"defense": 1.0, "org": 1.2, "hp": 0.8, "soft": 0.3, "breakthrough": 0.1},
                    # engineers: a must (their terrain and entrenchment bonuses aren't in the designer's sums);
                    # support artillery: cheap fire for the line
                    required=("engineer", "artillery"),
                    cost=1.0),
    "offence": dict(groups={"infantry", "combat_support"}, width=(18, 22), org_min=35,
                    weights={"soft": 1.2, "breakthrough": 0.8, "hard": 0.3, "defense": 0.4, "org": 0.6, "hp": 0.3},
                    cost=0.5),
    "mobile": dict(groups={"mobile", "mobile_combat_support"}, width=(16, 22), org_min=35,
                   weights={"soft": 0.8, "breakthrough": 0.6, "defense": 0.6, "org": 0.8, "hp": 0.3},
                   cost=0.5),
    "breakthrough": dict(groups={"armor", "mobile", "armor_combat_support", "mobile_combat_support"}, width=(18, 30), org_min=20,
                         weights={"breakthrough": 1.0, "soft": 0.8, "hard": 0.4, "hardness": 0.8, "armor": 0.4,
                                  "org": 0.4, "hp": 0.2},
                         cost=0.3),
}
# battalions kept out of the general roles (special forces and odd ones)
SPECIAL = {"mountaineers", "marine", "paratrooper", "ranger_battalion", "marine_commando", "penal_battalion",
           "militia", "irregular_infantry", "bicycle_battalion", "camelry", "bus", "armored_car", "land_cruiser",
           "amphibious_armor", "amphibious_light_armor", "amphibious_medium_armor", "amphibious_heavy_armor",
           "amphibious_mechanized", "super_heavy_armor", "super_heavy_tank_destroyer_brigade",
           "super_heavy_sp_artillery_brigade", "super_heavy_sp_anti_air_brigade", "railway_gun",
           "super_heavy_artillery", "self_propelled_super_heavy_artillery"}
PER_WIDTH = {"defense", "hp", "soft", "hard", "breakthrough"}
IMPROVE = 1.01  # an edit must score 1% better
TANK_ARCHETYPES = ("tank_chassis", "armor")
# equipment that makes a division mobile or armoured: infantry roles take no sub-unit needing it (keeps trucks and
# tanks for the mobile and tank divisions)
VEHICLES = ("motorized", "mechanized", "tank_chassis", "armored_car", "armor")


def needs_vehicles(sub):
    return any(v in a for a in sub["need"] for v in VEHICLES)
# recruitment mix (share of new divisions); roles the country lacks hand their share to defence
# after a player's Germany of 1939: five infantry armies of 24, 10-12 motorized, 4-6 tank divisions
MIX = {"defence": 0.70, "offence": 0.18, "mobile": 0.08, "breakthrough": 0.04}
# production plan: a country takes up trucks (motorized replaces cavalry) and tanks once its military industry can
# carry them, or at once when it already makes them
# roles by military factories: below these a country can't keep the role's equipment lines going (its stock still
# serves the divisions it has); defence and cavalry always
OFFENCE_FACTORIES, TRUCK_FACTORIES, TANK_FACTORIES = 4, 5, 8
FACTORY_OUTPUT, PLAN_EFFICIENCY = 4.5, 0.6
MIN_SHARE = 0.05  # an archetype with this share of the plan's IC gets at least one factory  # IC a factory a day at full efficiency; a year's rough average


class Design:
    """Columns of (group, [battalions]), divisional support companies, regimental company per column."""

    def __init__(self, columns, support, regimental):
        self.columns = [(g, list(b)) for g, b in columns]
        self.support = list(support)
        self.regimental = dict(regimental)

    @staticmethod
    def of(template, subs):
        grid = template.get("grid") or [[i // 3, i % 3] for i in range(len(template["regiments"]))]
        cols = {}
        for name, (x, _) in zip(template["regiments"], grid):
            cols.setdefault(x, []).append(name)
        columns = [(subs[cols[x][0]]["group"], cols[x]) for x in sorted(cols)]
        reg = dict(zip(template.get("regimental_grid", []), template.get("regimental_support", [])))
        return Design(columns, template["support"], reg)

    def copy(self):
        return Design(self.columns, self.support, self.regimental)

    def template(self):
        return {"regiments": [b for _, bs in self.columns for b in bs], "support": self.support,
                "regimental_support": [self.regimental[x] for x in sorted(self.regimental)]}

    def key(self):
        return (tuple((g, tuple(sorted(b))) for g, b in self.columns), tuple(sorted(self.support)),
                tuple(sorted(self.regimental.items())))

    def text(self, subs):
        cols = " | ".join("+".join(f"{n}x{subs[b]['abbreviation']}" for b, n in Counter(bs).items())
                          + (f" [{subs[self.regimental[i]]['abbreviation']}]" if i in self.regimental else "")
                          for i, (_, bs) in enumerate(self.columns))
        sup = ", ".join(subs[s]["abbreviation"] for s in self.support)
        return f"{cols}" + (f"  ; support {sup}" if sup else "")


def can_field(name, country, subs):
    return name in country["sub_units"] and name in subs


def edits(d, battalions, divisional, regimental, required=()):
    """Every design one edit away that keeps the designer's rules (required companies are never removed)."""
    out = []
    for i, (g, bs) in enumerate(d.columns):
        if len(bs) < ROWS:  # add a battalion of the column's group
            for b in battalions.get(g, []):
                n = d.copy(); n.columns[i][1].append(b); out.append(n)
        for j, b in enumerate(bs):  # remove one
            n = d.copy(); del n.columns[i][1][j]
            if not n.columns[i][1]:
                del n.columns[i]
                n.regimental = {(x if x < i else x - 1): r for x, r in n.regimental.items() if x != i}
            elif len(n.columns[i][1]) < REGIMENTAL_NEEDS:
                n.regimental.pop(i, None)
            out.append(n)
            for o in battalions.get(g, []):  # swap one
                if o != b:
                    n = d.copy(); n.columns[i][1][j] = o; out.append(n)
    if len(d.columns) < COLUMNS:  # a new column of any allowed group
        for g, bs in battalions.items():
            for b in bs:
                n = d.copy(); n.columns.append((g, [b])); out.append(n)
    for s in divisional:
        if s in required:
            continue
        if s in d.support:
            n = d.copy(); n.support.remove(s); out.append(n)
        elif len(d.support) < SUPPORT:
            n = d.copy(); n.support.append(s); out.append(n)
    for i, (g, bs) in enumerate(d.columns):
        if len(bs) < REGIMENTAL_NEEDS:
            continue
        if i in d.regimental:
            n = d.copy(); del n.regimental[i]; out.append(n)
        for r, groups in regimental.items():
            if g in groups and d.regimental.get(i) != r:
                n = d.copy(); n.regimental[i] = r; out.append(n)
    return out


def score(d, role, ref, subs, costs):
    st = stats(d.template(), subs)
    w = st["width"]
    lo, hi = role["width"]
    if w <= 0 or w > hi:
        return None, st
    value = 0.0
    for k, weight in role["weights"].items():
        v = st[k] / w if k in PER_WIDTH else st[k]
        r = ref[k] / ref["width"] if k in PER_WIDTH else ref[k]
        ratio = v / r if r else v / 10
        value += weight * (2 * ratio ** 0.5 - 1)  # diminishing returns: twice the stat is worth ~1.4x
    ic = sum(n * costs.get(a, 5.0) for a, n in st["need"].items())
    value -= role["cost"] * 0.5 * (ic / w) / (ref["ic"] / ref["width"])
    value -= 0.3 * (st["manpower"] / w) / (ref["manpower"] / ref["width"])
    if w < lo:  # under the target width: as if the rest of the frontage stood empty
        value *= w / lo
    if st["org"] < role["org_min"]:  # too brittle for the role
        value -= 4 * (role["org_min"] - st["org"]) / role["org_min"]
    return value, st


def design(start, role, ref, subs, costs, battalions, divisional, regimental):
    best, (best_score, _) = start, score(start, role, ref, subs, costs)
    best_score = best_score if best_score is not None else -1e9
    seen = {best.key()}
    for _ in range(200):
        step = None
        for n in edits(best, battalions, divisional, regimental, role.get("required", ())):
            if n.key() in seen:
                continue
            seen.add(n.key())
            s, _ = score(n, role, ref, subs, costs)
            if s is not None and s > (step[0] if step else best_score * IMPROVE if best_score > 0 else best_score):
                step = (s, n)
        if not step:
            break
        best_score, best = step
    return best, best_score


def run(tag, oob, excluded=frozenset()):
    subs, c = oob["sub_units"], oob["countries"].get(tag)
    if not c:
        print(f"{tag}: no army")
        return
    costs = {a: e["cost"] for a, e in c.get("equipment", {}).items()}
    produced = {p["archetype"] for p in c.get("production", [])} | {a for a, n in c.get("stockpile", {}).items() if n > 0}
    military = sum(p["factories"] for p in c.get("production", []))
    buildable = set(c.get("equipment", {}))
    planned = set()  # archetypes the plan takes up
    has_trucks = "motorized_equipment" in produced
    produced = {a for a in produced if a != "motorized_equipment" and not any(k in a for k in TANK_ARCHETYPES)}
    if military >= TRUCK_FACTORIES and "motorized_equipment" in buildable:
        produced.add("motorized_equipment")
        if not has_trucks:
            planned.add("motorized_equipment")
    if military >= TANK_FACTORIES:
        chassis = next((a for a in ("light_tank_chassis", "medium_tank_chassis") if a in buildable), None)
        if chassis:
            produced.add(chassis)
    tanks = any(any(t in a for t in TANK_ARCHETYPES) for a in produced)

    # reference: a plain 9-infantry division with the country's costs
    ref = stats({"regiments": ["infantry"] * 9, "support": []}, subs)
    ref["ic"] = sum(n * costs.get(a, 5.0) for a, n in ref["need"].items())

    # sub-units whose equipment the plan doesn't make (and isn't stocked) are left out
    usable = [n for n in c["sub_units"] if n in subs and n not in SPECIAL and not set(subs[n]["need"]) & excluded]
    divisional = [n for n in usable if subs[n]["group"] == "support" and subs[n].get("divisional", True)]
    regimental = {n: set(subs[n]["battalion_groups"]) for n in usable if subs[n].get("regimental")}

    # sort the starting templates into roles
    used = Counter(d["template"] for d in c["divisions"])
    roles = {}
    cands = []
    for name, t in c["templates"].items():
        groups = {subs[r]["group"] for r in t["regiments"] if r in subs}
        cands.append((name, groups, stats(t, subs)))
    inf = [x for x in cands if x[1] <= {"infantry", "combat_support"} and not any(r in SPECIAL for r in c["templates"][x[0]]["regiments"])]
    if inf:
        roles["defence"] = max(inf, key=lambda x: (used[x[0]] * x[2]["width"], x[2]["width"]))[0]  # most frontage
        roles["offence"] = roles["defence"]
    trucks = "motorized_equipment" in produced and "motorized_equipment" not in excluded
    mobile_main = "motorized" if trucks and "motorized" in usable else "cavalry"
    # the mobile role's template: one of its main battalion (a cavalry template is left over once trucks come)
    mob = [x for x in cands if not x[1] & {"armor"} and mobile_main in c["templates"][x[0]]["regiments"]]
    if mob:
        roles["mobile"] = max(mob, key=lambda x: (x[2]["speed"], used[x[0]]))[0]
    arm = [x for x in cands if x[1] & {"armor"}]
    if arm:
        roles["breakthrough"] = max(arm, key=lambda x: (sum(subs[r]["group"] == "armor" for r in c["templates"][x[0]]["regiments"]), used[x[0]]))[0]

    print(f"\n=== {tag}: {len(c['divisions'])} divisions, produces {sorted(produced)}")
    leftover = [n for n in c["templates"] if n not in roles.values()]
    if leftover:
        print(f"  no role (train no more, go once empty): {', '.join(f'{n} ({used[n]})' for n in leftover)}")
    mix, designs = {}, {}
    for role_name, role in ROLES.items():
        battalions = {}
        for n in usable:
            g = subs[n]["group"]
            if g in role["groups"] and subs[n].get("land"):
                battalions.setdefault(g, []).append(n)
        # tanks only of a chassis the country makes or holds; infantry roles no vehicles at all
        battalions = {g: [b for b in bs if not (subs[b]["group"] == "armor" and not any(a in produced for a in subs[b]["need"]))]
                      for g, bs in battalions.items()}
        battalions = {g: bs for g, bs in battalions.items() if bs}
        if role_name in ("defence", "offence"):
            battalions = {g: [b for b in bs if not needs_vehicles(subs[b])] for g, bs in battalions.items()}
        if role_name == "breakthrough" and (not tanks or "armor" not in battalions):
            print(f"  {role_name:12} - none (under {TANK_FACTORIES} military factories)")
            continue
        if role_name == "offence" and military < OFFENCE_FACTORIES:
            print(f"  {role_name:12} - none (under {OFFENCE_FACTORIES} military factories)")
            continue
        if role_name == "mobile" and "mobile" not in battalions:
            print(f"  {role_name:12} - none (no mobile battalions)")
            continue
        if role_name == "mobile":
            battalions["mobile"] = [mobile_main] if mobile_main in usable else []
            if not trucks:
                battalions = {g: [x for x in bs if not needs_vehicles(subs[x])] for g, bs in battalions.items()}
            battalions = {g: bs for g, bs in battalions.items() if bs}
        # mobile: no slower battalion than its main one
        if role_name in ("mobile", "breakthrough") and battalions:
            fastest = max(subs[b]["speed"] for bs in battalions.values() for b in bs if subs[b]["group"] in ("mobile", "armor"))
            # breakthrough: nothing slower than its tanks (no cavalry); mobile: nothing slower than its fastest
            slow_ok = (min(subs[b]["speed"] for b in battalions.get("armor", [])) if role_name == "breakthrough"
                       else fastest)
            battalions = {g: [b for b in bs if subs[b]["speed"] >= min(slow_ok, fastest)] for g, bs in battalions.items()}
            div = [s for s in divisional if subs[s]["speed"] >= min(slow_ok, fastest) or not subs[s].get("affects_speed", True)]
            if role_name == "mobile" and not trucks:
                div = [s for s in div if not needs_vehicles(subs[s])]
        else:
            div = [s for s in divisional if not needs_vehicles(subs[s])]
        reg = {r: g for r, g in regimental.items() if role_name not in ("defence", "offence") or not needs_vehicles(subs[r])}
        if role_name == "mobile" and not trucks:
            reg = {r: g for r, g in reg.items() if not needs_vehicles(subs[r])}
        if role_name in roles:
            start = Design.of(c["templates"][roles[role_name]], subs)
            start.columns = [(g, bs) for g, bs in start.columns if g in role["groups"]]
            origin = roles[role_name]
            pass
            # the role's template keeps only what the role may use (a tank division drops its cavalry)
            start.columns = [(g, [x for x in bs if x in battalions.get(g, [])]) for g, bs in start.columns]
            start.columns = [(g, bs) for g, bs in start.columns if bs]
            start.regimental = {}
        else:
            base = {"mobile": mobile_main,
                    "breakthrough": next((b for b in ("light_armor", "medium_armor") if b in usable), None)}.get(role_name, "infantry")
            if base is None:
                continue
            start = Design([(subs[base]["group"], [base, base])], [], {})
            origin = "(new)"
        for s in role.get("required", ()):
            if s in div and s not in start.support and len(start.support) < SUPPORT:
                start.support.append(s)
        s0, st0 = score(start, role, ref, subs, costs)
        best, s1 = design(start, role, ref, subs, costs, battalions, div, reg)
        st1 = stats(best.template(), subs)
        mix[role_name] = MIX[role_name]
        designs[role_name] = st1
        print(f"  {role_name:12} from {origin}")
        for label, d, st, sc in (("was", start, st0, s0), ("now", best, st1, s1)):
            ic = sum(n * costs.get(a, 5.0) for a, n in st["need"].items())
            print(f"    {label}: {d.text(subs)}")
            print(f"         w{st['width']:.0f} org {st['org']:.1f} hp {st['hp']:.0f} def {st['defense']:.0f} soft {st['soft']:.0f} "
                  f"hard {st['hard']:.1f} brk {st['breakthrough']:.0f} hardness {st['hardness'] * 100:.0f}% armor {st['armor']:.1f} "
                  f"speed {st['speed']:.0f}  IC {ic:.0f} men {st['manpower']}  score {sc if sc is None else round(sc, 2)}")
    total = sum(mix.values())
    lacking = 1 - total
    shares = {r: v + (lacking if r == "defence" else 0) for r, v in mix.items()}
    print("  recruitment mix: " + ", ".join(f"{r} {v * 100:.0f}%" for r, v in shares.items()))
    if excluded:
        print(f"  left out (no factory for them, none stocked): {', '.join(sorted(excluded))}")
    return plan(c, military, planned - excluded, designs, shares, costs)


def plan(c, military, planned, designs, shares, costs):
    """Factories split by the equipment the recruitment mix needs: per archetype, the IC its share of each role's
    design takes. (In the game the field's shortfall comes first, as now; this is where the rest goes.)"""
    ic = {}
    for role_name, st in designs.items():
        for a, n in st["need"].items():
            ic[a] = ic.get(a, 0) + shares[role_name] * n * costs.get(a, 5.0)
    total = sum(ic.values())
    # factories by largest remainder; an archetype under half a factory shares one with nothing (left out)
    raw = {a: military * v / total for a, v in ic.items()}
    # every archetype worth half a factory gets one (largest first), the rest by largest remainder
    split = {a: 0 for a in raw}
    for a in sorted(raw, key=lambda a: -raw[a]):
        if raw[a] >= 0.5 and sum(split.values()) < military:
            split[a] = 1
    while sum(split.values()) < military:
        a = max(raw, key=lambda a: raw[a] - split[a])
        split[a] += 1
    now = {}
    for p_ in c.get("production", []):
        now[p_["archetype"]] = now.get(p_["archetype"], 0) + p_["factories"]
    per_year = military * FACTORY_OUTPUT * PLAN_EFFICIENCY * 365 / total if total else 0
    print(f"  production plan ({military} military factories{', takes up ' + ', '.join(sorted(planned)) if planned else ''}):")
    for a in sorted(set(split) | set(now), key=lambda a: -raw.get(a, 0)):
        print(f"    {a:28} {split.get(a, 0):2} factories (now {now.get(a, 0)})  {100 * ic.get(a, 0) / total:4.0f}% of the IC")
    print(f"    about {per_year:.0f} divisions a year by the mix (equipment only; {PLAN_EFFICIENCY:.0%} efficiency)")
    return split


def main():
    tags = sys.argv[1:] or ["FRA", "GER", "ITA", "SOV", "ENG", "POL", "HUN", "CHI"]
    oob = json5(ROOT / "Hoi4Data" / "history" / "units" / "oob_1936.json5")
    import contextlib, io
    for tag in tags:
        c = oob["countries"].get(tag)
        stock = {a for a, n in (c or {}).get("stockpile", {}).items() if n > 0}
        excluded = set()
        for _ in range(4):  # design, plan, drop what gets no factory, design again
            with contextlib.redirect_stdout(io.StringIO()):
                split = run(tag, oob, frozenset(excluded))
            zero = {a for a, f in (split or {}).items() if f == 0 and a not in stock}
            if not zero:
                break
            excluded |= zero
        run(tag, oob, frozenset(excluded))


if __name__ == "__main__":
    main()
