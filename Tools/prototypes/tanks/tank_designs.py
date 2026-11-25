"""Tank designs (No Step Back) from the HOI4 files: chassis, modules, upgrades, each country's 1936 designs, and the
AI designer (common/ai_equipment), before porting to the game.

  python Tools/prototypes/tanks/tank_designs.py [--hoi4 PATH] [--tags GER,SOV,POL] [--techs extra,techs]

Rules (game files; the stat formula is the reference workspace's hoi4rec/templates.py, which matches the real division
designer's Panzer-Division attacks):
- A design is an equipment type (a chassis tier of a role: light_tank_chassis_1, light_tank_destroyer_chassis_1 ...),
  a module per slot and upgrade levels. Role chassis (x_tank_chassis.txt duplicate_archetypes) copy the base
  chassis' slots and stats, some with their own hardness.
- Stats: the archetype's, then the type's; plus each module's add_stats x (1 + the chassis' module_add_stats_multiplier)
  and each upgrade's add_stats x level; all x (1 + the modules' multiply_stats + each upgrade's own stats x level).
- Slots: the archetype's module_slots (allowed categories, required); a turret can widen another slot's categories
  (allowed_module_categories); modules can be barred from equipment types (forbid_equipment_type;
  forbid_equipment_type_exact_match = armor keeps anti-air guns and rockets off plain tanks;
  forbid_equipment_type_exact_match_for_category); a role design needs a module that allows its role
  (allow_equipment_type: a tank destroyer an anti-tank gun...); module_count_limit caps repeats.
- Availability: technologies' enable_equipments (chassis; a role chassis comes with its base) and
  enable_equipment_modules (modules).
- AI: per design group (blocked_for / available_for, roles), per design (enable, priority with modifiers), the design
  built from target_variant (type; per slot a module, a category = its newest available, any_of = the first available,
  empty; upgrades capped by level_requirements) and allowed_modules for any slot left; per role the enabled, buildable
  design of the highest priority (group x design), then match_value.
"""
import argparse
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
from hoi4_convert import parse_pdx, read_text, get, known_technologies  # noqa: E402
from hoi4_tanks import DEFAULT_HOI4, TankData, country_designs, num, stats_of, tokens  # noqa: E402

# ---- AI designer ---------------------------------------------------------------------------------------

def trigger(items, ctx):
    """The AI files' triggers: has_tech, NOT, OR, AND, tag, has_war, num_of_factories, has_government; anything else
    is false (any_enemy_country, focuses: they only raise priorities)."""
    for k, op, v in (items or []):
        if k in ("factor", "add", "base"):
            continue
        if k == "has_tech":
            ok = v in ctx["known"]
        elif k == "NOT":
            ok = not any_true(v, ctx)
        elif k == "OR":
            ok = any_true(v, ctx)
        elif k == "AND":
            ok = trigger(v, ctx)
        elif k in ("tag", "TAG"):
            ok = v == ctx["tag"]
        elif k == "has_war":
            ok = (v == "yes") == ctx.get("war", False)
        elif k == "num_of_factories":
            ok = (ctx.get("factories", 0) > num(v)) if op == ">" else (ctx.get("factories", 0) < num(v))
        elif k == "has_government":
            ok = v == ctx.get("government")
        else:
            ok = False
        if not ok:
            return False
    return True


def any_true(items, ctx):
    return any(trigger([x], ctx) for x in (items or []) if x[0] not in ("factor", "add", "base"))


def weight(block, ctx):
    f = num(get(block, "factor"), 1.0)
    for k, _, v in (block or []):
        if k == "modifier" and isinstance(v, list) and trigger(v, ctx):
            f *= num(get(v, "factor"), 1.0)
    return f


def module_chain(data, module):
    """The module and its parents (tank_radio_2 -> tank_radio_1), for < and >."""
    out = [module]
    while get(data.modules.get(out[-1], []), "parent"):
        out.append(get(data.modules[out[-1]], "parent"))
    return out


def newest_of(data, category, available, ok):
    """The newest available module of a category that fits: the end of its parent chain (else the first in file
    order)."""
    cands = [m for m in data.modules if get(data.modules[m], "category") == category and m in available and ok(m)]
    if not cands:
        return None
    return max(cands, key=lambda m: len(module_chain(data, m)))


def upgrade_cap(data, u, ctx):
    """The highest level of an upgrade the country may use: levels from a level_requirements entry up need its
    technology (engine upgrade: 0-4 free, 5-9 basic engine, 10-14 improved, 15-17 advanced, 18-20 modern; wiki)."""
    body = data.upgrades.get(u, [])
    top = int(num(get(body, "max_level"), 20))
    for need, _, req in sorted((get(body, "level_requirements") or []), key=lambda x: num(x[0])):
        if not trigger(req, ctx):
            return min(top, int(num(need)) - 1)
    return top


def build_design(data, spec, types, modules, ctx):
    """A variant from an AI design's target_variant, or why the country can't build it (a string)."""
    tv = get(spec, "target_variant")
    t = get(tv, "type")
    if t in data.tank_archetypes:  # an archetype: its newest unlocked type
        tiers = [x for x in types if data.archetype(x) == t]
        t = max(tiers, key=lambda x: num(get(data.eq[x], "year"))) if tiers else t
    if t not in types:
        return f"type {t} not unlocked"
    slots = data.slots(t)
    chosen = {}
    wants = {slot: (op, want) for slot, op, want in (get(tv, "modules") or [])}
    for slot in slots:  # the chassis' order: turrets first (they widen the gun slot)
        if slot not in wants:
            continue
        op, want = wants[slot]
        ok = lambda m: data.fits(t, slot, m, chosen)
        pick = None
        if want == "empty" and op == "=":
            chosen[slot] = None
            continue
        if isinstance(want, list):
            options = tokens(get(want, "any_of")) if get(want, "any_of") else [get(want, "module")]
        else:
            options = [want]
        for o in options:
            if o in data.modules:
                if o in modules and ok(o):
                    pick = o
            else:  # a category
                pick = newest_of(data, o, modules, ok)
            if pick:
                break
        if pick is None:
            if slots[slot][0]:
                return f"{slot}: nothing of {options}"  # a required slot it can't fill
            continue
        chosen[slot] = pick
    for m in tokens(get(spec, "allowed_modules")):  # what's left, in the list's order
        for slot in slots:
            if slot not in chosen and m in modules and data.fits(t, slot, m, chosen):
                chosen[slot] = m
                break
    chosen = {k: v for k, v in chosen.items() if v}
    missing = [s for s, (req, _) in slots.items() if req and s not in chosen]
    if missing:
        return f"required {missing} empty"
    if not data.has_role_module(t, chosen.values()):
        return f"no module for its role {sorted(data.roles(t) - {'armor'})}"
    ups = {}
    for u, _, lv in (get(tv, "upgrades") or []):
        level = int(num(get(lv, "base"))) + sum(int(num(get(m, "add"))) for k, _, m in lv if k == "modifier" and trigger(m, ctx)) \
            if isinstance(lv, list) else int(num(lv))
        ups[u] = min(level, upgrade_cap(data, u, ctx))
    return {"type": t, "modules": chosen, "upgrades": ups, "match": num(get(tv, "match_value"))}


def ai_designs(data, ctx):
    """Per role the design the AI would make (see the module docstring)."""
    types, modules = data.unlocked(ctx["known"])
    best = {}
    for f in sorted((data.hoi4 / "common" / "ai_equipment").glob("*tank*.txt")):
        for group, _, body in parse_pdx(read_text(f)):
            if not isinstance(body, list) or get(body, "category") != "land":
                continue
            blocked = tokens(get(body, "blocked_for"))
            allowed = tokens(get(body, "available_for"))
            if ctx["tag"] in blocked or (allowed and ctx["tag"] not in allowed):
                continue
            gp = weight(get(body, "priority"), ctx)
            roles = tokens(get(body, "roles"))
            for name, _, spec in body:
                if not isinstance(spec, list) or get(spec, "target_variant") is None:
                    continue
                if get(spec, "enable") is not None and not trigger(get(spec, "enable"), ctx):
                    if ctx.get("why"):
                        print(f"     - {group}/{name}: not enabled")
                    continue
                p = gp * weight(get(spec, "priority"), ctx)
                if p <= 0:
                    if ctx.get("why"):
                        print(f"     - {group}/{name}: priority 0")
                    continue
                d = build_design(data, spec, types, modules, ctx)
                if isinstance(d, str):
                    if ctx.get("why"):
                        print(f"     x {group}/{name}: {d}")
                    continue
                d.update(name=name, group=group, priority=p)
                for r in roles:
                    if r not in best or (p, d["match"]) > (best[r]["priority"], best[r]["match"]):
                        best[r] = d
    return best


# ---- report --------------------------------------------------------------------------------------------

def fmt(st):
    return (f"soft {st.get('soft_attack', 0):5.1f} hard {st.get('hard_attack', 0):5.1f} ap {st.get('ap_attack', 0):5.1f} "
            f"def {st.get('defense', 0):5.1f} bt {st.get('breakthrough', 0):5.1f} armor {st.get('armor_value', 0):5.1f} "
            f"speed {st.get('maximum_speed', 0):4.1f} rel {st.get('reliability', 0):4.2f} cost {st.get('build_cost_ic', 0):5.2f}")


def country_root(hoi4, tag):
    f = next((hoi4 / "history" / "countries").glob(f"{tag} - *.txt"))
    return parse_pdx(read_text(f))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", type=Path, default=DEFAULT_HOI4)
    ap.add_argument("--tags", default="GER,SOV,ENG,POL")
    ap.add_argument("--techs", default="", help="extra technologies (a later year)")
    ap.add_argument("--why", action="store_true", help="why each AI design can't be built")
    ap.add_argument("--factories", type=int, default=30, help="the countries' factories (AI priorities)")
    a = ap.parse_args()
    data = TankData(a.hoi4)
    print(f"{len(data.tank_archetypes)} tank archetypes, {len(data.modules)} modules, {len(data.upgrades)} upgrades")

    # 1. chassis with their default modules against the wiki's chassis table (Tank designer page)
    wiki = {"light_tank_chassis_0": (5.1, 10, 0.80, 2), "light_tank_chassis_1": (6.3, 15, 0.95, 2.35),
            "light_tank_chassis_2": (7.4, 20, 1.10, 2.7), "light_tank_chassis_3": (8.6, 25, 1.30, 3),
            "medium_tank_chassis_0": (5.1, 20, 0.75, 3.5), "medium_tank_chassis_1": (6.3, 30, 0.80, 3.75),
            "medium_tank_chassis_2": (7.4, 40, 1.00, 4)}
    print("\nchassis (default modules' engine only, as the wiki table: speed, armour, reliability, chassis cost)")
    for t, (spd, arm, rel, cost) in wiki.items():
        st = data.stats(t, {"engine_type_slot": "tank_gasoline_engine"})
        chassis = data.stats(t, {})
        ok = int(st["maximum_speed"] * 10) / 10 == spd and chassis["armor_value"] == arm and \
            abs(chassis["reliability"] - rel) < 1e-6 and abs(chassis["build_cost_ic"] - cost) < 1e-6
        print(f"  {t:24} speed {st['maximum_speed']:.3f} (wiki {spd}) armour {chassis['armor_value']:.0f} ({arm}) "
              f"rel {chassis['reliability']:.2f} ({rel}) cost {chassis['build_cost_ic']:.2f} ({cost})  {'ok' if ok else 'DIFFERS'}")

    extra = {t for t in a.techs.split(",") if t}
    for tag in a.tags.split(","):
        root = country_root(a.hoi4, tag)
        known = known_technologies(root) | extra
        types, modules = data.unlocked(known)
        print(f"\n== {tag}: {len(types)} chassis types, {len(modules)} modules unlocked")
        for d in country_designs(data, root):
            print(f"  history {d['name'][:24]:24} {d['type']:28} {fmt(data.stats(d['type'], d['modules'], d['upgrades']))}"
                  + ("  (obsolete)" if d["obsolete"] else ""))
            print(f"          {' '.join(d['modules'].values())}  {d['upgrades']}")
        ctx = {"tag": tag, "known": known, "war": False, "factories": a.factories, "why": a.why}
        for role, d in sorted(ai_designs(data, ctx).items()):
            print(f"  ai {role:28} {d['name'][:26]:26} {d['type']:28} {fmt(data.stats(d['type'], d['modules'], d['upgrades']))}")
            print(f"          {' '.join(d['modules'].values())}  {d['upgrades']}")
        legacy = {n: stats_of(data.eq[n]) for n in ("light_tank_equipment_1", "light_tank_equipment_2") if n in data.eq}
        for n, st in legacy.items():
            print(f"  legacy {n:24} {fmt(st)}")


if __name__ == "__main__":
    main()
