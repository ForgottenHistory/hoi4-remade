"""HOI4 tank designs (No Step Back): chassis, role chassis, modules, upgrades, each country's history designs and a
design's stats, from the game files. Shared by Tools/hoi4_convert.py (common/hoi4_tank_designs.json5, the catalogue's
designs) and the prototype Tools/prototypes/tanks/tank_designs.py (rules and checks there)."""
from pathlib import Path

from hoi4_convert import parse_pdx, read_text, get, collect_blocks

DEFAULT_HOI4 = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV")
STATS = ("soft_attack", "hard_attack", "ap_attack", "air_attack", "defense", "breakthrough", "armor_value", "hardness",
         "maximum_speed", "reliability", "build_cost_ic", "fuel_consumption")


def num(v, d=0.0):
    try:
        return float(v)
    except (TypeError, ValueError):
        return d


def stats_of(items):
    return {k: num(v) for k, _, v in (items or []) if isinstance(k, str) and not isinstance(v, list) and num(v, None) is not None}


def tokens(items):
    return [k if k else v for k, _, v in (items or []) if (k or v)]


class TankData:
    def __init__(self, hoi4):
        self.hoi4 = hoi4
        common = hoi4 / "common"
        self.eq = {}
        for f in sorted((common / "units" / "equipment").glob("*.txt")):
            if f.name.startswith("x_"):
                continue
            for top, _, block in parse_pdx(read_text(f)):
                if top == "equipments" and isinstance(block, list):
                    for name, _, body in block:
                        if isinstance(body, list):
                            self.eq[name] = body
        self.tank_archetypes = {n for n, b in self.eq.items() if get(b, "is_archetype") == "yes" and get(b, "module_slots")
                                and "tank" in n}
        self.base_of = {}  # role archetype -> the archetype it copies (light_tank_destroyer_chassis -> light_tank_chassis)
        self.duplicate_archetypes(common / "units" / "equipment" / "x_tank_chassis.txt")
        self.modules = {}
        for top, _, block in parse_pdx(read_text(common / "units" / "equipment" / "modules" / "00_tank_modules.txt")):
            if top == "equipment_modules" and isinstance(block, list):
                for name, _, body in block:
                    if isinstance(body, list):
                        self.modules[name] = body
        self.upgrades = {}
        for top, _, block in parse_pdx(read_text(common / "units" / "equipment" / "upgrades" / "land_upgrades.txt")):
            for name, _, body in (block if isinstance(block, list) else []):
                if isinstance(body, list) and name.startswith("tank_nsb"):
                    self.upgrades[name] = body
        # technologies: chassis and modules they enable
        self.tech_equipment, self.tech_modules = {}, {}
        for f in sorted((common / "technologies").glob("*.txt")):
            for top, _, block in parse_pdx(read_text(f)):
                for tech, _, body in (block if isinstance(block, list) else []):
                    if not isinstance(body, list):
                        continue
                    for k, _, v in body:
                        if k == "enable_equipments" and isinstance(v, list):
                            self.tech_equipment.setdefault(tech, []).extend(tokens(v))
                        elif k == "enable_equipment_modules" and isinstance(v, list):
                            self.tech_modules.setdefault(tech, []).extend(tokens(v))

    def duplicate_archetypes(self, path):
        """x_tank_chassis.txt: each role archetype copies its base archetype and every type of it (renamed), with
        the for_each overrides (hardness = { set = 0.5 }); the legacy designs listed there stay as they are."""
        for top, _, block in parse_pdx(read_text(path)):
            if not isinstance(block, list):
                continue
            if top == "duplicate_archetypes":
                for dup, _, body in block:
                    base = get(body, "archetype")
                    if base not in self.eq:
                        continue
                    sets = {}
                    for k, _, v in (get(body, "for_each") or []):
                        if isinstance(v, list) and get(v, "set") is not None:
                            sets[k] = get(v, "set")
                    roles = tokens(get(body, "type")) if isinstance(get(body, "type"), list) else [get(body, "type")]
                    self.eq[dup] = [x for x in self.eq[base] if x[0] != "type"] + [("type", "=", [(None, None, r) for r in roles])]
                    self.tank_archetypes.add(dup)
                    self.base_of[dup] = base
                    for name, b in list(self.eq.items()):
                        if get(b, "archetype") == base and name != base and name.startswith(base):
                            nb = [(k, o, dup + v[len(base):] if k == "parent" and isinstance(v, str) and v.startswith(base) else v)
                                  for k, o, v in b if k not in sets and k not in ("archetype", "derived_variant_name")]
                            nb += [("archetype", "=", dup)] + [(k, "=", v) for k, v in sets.items()]
                            self.eq[dup + name[len(base):]] = nb
            elif top == "equipments":
                for name, _, body in block:
                    if isinstance(body, list):
                        self.eq[name] = body

    # ---- one design --------------------------------------------------------------------------------------

    def archetype(self, t):
        b = self.eq.get(t, [])
        return t if get(b, "is_archetype") == "yes" else get(b, "archetype")

    def roles(self, t):
        r = get(self.eq.get(self.archetype(t), []), "type")
        return set(tokens(r)) if isinstance(r, list) else {r}

    def slots(self, t):
        """{slot: (required, [categories])} of the type (module_slots = inherit: the archetype's)."""
        ms = get(self.eq.get(t, []), "module_slots")
        if not isinstance(ms, list):
            ms = get(self.eq.get(self.archetype(t), []), "module_slots")
        out = {}
        for slot, _, body in (ms if isinstance(ms, list) else []):
            out[slot] = (get(body, "required") == "yes", tokens(get(body, "allowed_module_categories")))
        return out

    def default_modules(self, t):
        d = get(self.eq.get(t, []), "default_modules") or get(self.eq.get(self.archetype(t), []), "default_modules")
        return {k: v for k, _, v in (d or []) if v != "empty"}

    def fits(self, t, slot, module, chosen):
        """Whether MODULE may go in SLOT of a design of type T with the other modules CHOSEN ({slot: module})."""
        m = self.modules.get(module)
        if m is None:
            return False
        slots = self.slots(t)
        if slot not in slots:
            return False
        cat = get(m, "category")
        allowed = set(slots[slot][1])
        for other_slot, other in chosen.items():  # a turret widens the gun slot
            if other_slot == slot or other not in self.modules:
                continue
            for s, _, cats in (get(self.modules[other], "allowed_module_categories") or []):
                if s == slot:
                    allowed |= set(tokens(cats))
        if cat not in allowed:
            return False
        roles = self.roles(t)
        if roles & set(forbidden(m)):  # a role or a block of them (amphibious drive: no AA, SPG, TD, flame)
            return False
        if get(m, "forbid_equipment_type_exact_match") and roles == {get(m, "forbid_equipment_type_exact_match")}:
            return False  # anti-air guns and rockets: not on a plain tank
        for other_slot, other in list(chosen.items()) + [(slot, module)]:  # gun categories some turrets bar per role
            for c, _, role in (get(self.modules.get(other, []), "forbid_equipment_type_exact_match_for_category") or []):
                cats = {get(self.modules.get(x, []), "category") for x in list(chosen.values()) + [module]}
                if c in cats and roles == {role}:
                    return False
        return True

    def has_role_module(self, t, modules):
        """A role design (anti_tank, artillery, anti_air, flame, amphibious) needs a module that allows its role
        (allow_equipment_type: an anti-tank gun, a howitzer, an anti-air gun, a flamethrower, an amphibious drive);
        a plain tank takes any module (allow_equipment_type doesn't keep modules off it: the Neubaufahrzeug, a heavy
        tank, carries the close support gun, which allows artillery)."""
        role = self.roles(t) - {"armor"}
        if not role:
            return True
        allows = {v for m in modules for k, _, v in self.modules.get(m, []) if k == "allow_equipment_type"}
        return bool(role & allows)

    def stats(self, t, modules, upgrades=None):
        """The design's stats (see the module docstring)."""
        arch = self.archetype(t)
        st = stats_of(self.eq.get(arch, []))
        st.update(stats_of(self.eq.get(t, [])))
        boost = stats_of(get(self.eq.get(arch, []), "module_add_stats_multiplier"))
        boost.update(stats_of(get(self.eq.get(t, []), "module_add_stats_multiplier")))
        mult = {}
        for m in modules.values():
            body = self.modules.get(m, [])
            for k, v in stats_of(get(body, "add_stats")).items():
                st[k] = st.get(k, 0) + v * (1 + boost.get(k, 0))
            for k, v in stats_of(get(body, "multiply_stats")).items():
                mult[k] = mult.get(k, 0) + v
        for u, level in (upgrades or {}).items():
            body = self.upgrades.get(u, [])
            for k, v in stats_of(get(body, "add_stats")).items():
                st[k] = st.get(k, 0) + v * level
            for k, v in stats_of(body).items():  # its own stats: a share per level (reliability -1.5%)
                if k in STATS:
                    mult[k] = mult.get(k, 0) + v * level
        return {k: v * (1 + mult.get(k, 0)) for k, v in st.items() if k in STATS}

    # ---- what a country has ------------------------------------------------------------------------------

    def unlocked(self, known):
        """(chassis types, modules) the technologies KNOWN unlock; a role chassis comes with its base chassis."""
        types = {e for t in known for e in self.tech_equipment.get(t, []) if e in self.eq}
        for t in list(types):
            arch = self.archetype(t)
            if arch in self.tank_archetypes:
                for dup in self.tank_archetypes:
                    if get(self.eq.get(dup, []), "archetype") == arch:
                        types.add(dup + t[len(arch):])
        types = {t for t in types if self.archetype(t) in self.tank_archetypes}
        modules = {m for t in known for m in self.tech_modules.get(t, []) if m in self.modules}
        return types, modules


def country_designs(data, root):
    """The history's create_equipment_variant tank designs (the No Step Back branches)."""
    out = []
    for v in collect_blocks(root, "create_equipment_variant", []):
        t = get(v, "type")
        if data.archetype(t) not in data.tank_archetypes:
            continue
        mods = {k: m for k, _, m in (get(v, "modules") or []) if m != "empty"}
        ups = {k: int(num(x)) for k, _, x in (get(v, "upgrades") or [])}
        out.append({"name": get(v, "name"), "type": t, "modules": mods, "upgrades": ups,
                    "obsolete": get(v, "obsolete") == "yes"})
    return out


# ---- for the converter --------------------------------------------------------------------------------------

def type_info(data, t):
    """A chassis type's numbers for the game: archetype, year, base stats (archetype then type), the module boost
    (module_add_stats_multiplier), its roles, its parent tier and its family (the base chassis tier it copies: a
    light tank destroyer chassis I is light_tank_chassis_1's; production keeps 70% switching within a family)."""
    arch = data.archetype(t)
    st = stats_of(data.eq.get(arch, []))
    st.update(stats_of(data.eq.get(t, [])))
    boost = stats_of(get(data.eq.get(arch, []), "module_add_stats_multiplier"))
    boost.update(stats_of(get(data.eq.get(t, []), "module_add_stats_multiplier")))
    base = data.base_of.get(arch, arch)
    parent = get(data.eq.get(t, []), "parent")
    return {"archetype": arch, "year": int(num(get(data.eq.get(t, []), "year"))),
            "stats": {k: round(v, 4) for k, v in sorted(st.items()) if k in STATS},
            "boost": {k: v for k, v in sorted(boost.items()) if k in STATS},
            "roles": sorted(data.roles(t)), "parent": parent, "family": base + t[len(arch):]}


def forbidden(body):
    """forbid_equipment_type: one role, or a block of them."""
    f = get(body, "forbid_equipment_type")
    return tokens(f) if isinstance(f, list) else [f] if f else []


def module_info(data, m):
    body = data.modules[m]
    widen = {slot: tokens(cats) for slot, _, cats in (get(body, "allowed_module_categories") or []) if isinstance(cats, list)}
    return {"category": get(body, "category"),
            "add": {k: v for k, v in stats_of(get(body, "add_stats")).items() if k in STATS},
            "mult": {k: v for k, v in stats_of(get(body, "multiply_stats")).items() if k in STATS},
            "widen": widen, "forbid": forbidden(body),
            "forbid_exact": get(body, "forbid_equipment_type_exact_match"),
            "forbid_exact_category": {c: r for c, _, r in (get(body, "forbid_equipment_type_exact_match_for_category") or [])},
            "allows": [v for k, _, v in body if k == "allow_equipment_type"], "parent": get(body, "parent")}


def archetype_info(data, a):
    slots = data.slots(a)
    return {"slots": [[slot, req, cats] for slot, (req, cats) in slots.items()],
            "default": data.default_modules(a)}


def upgrade_info(data, u, pdx_json):
    body = data.upgrades[u]
    levels = [[int(num(n)), pdx_json(req)] for n, _, req in sorted((get(body, "level_requirements") or []), key=lambda x: num(x[0]))]
    return {"add": {k: v for k, v in stats_of(get(body, "add_stats")).items() if k in STATS},
            "mult": {k: v for k, v in stats_of(body).items() if k in STATS},
            "max": int(num(get(body, "max_level"), 20)), "levels": levels}


def ai_groups(data, pdx_json, priority_json):
    """common/ai_equipment's land design groups: roles, who may use them, priority, and each design's priority,
    enable, target (type, per-slot module specs, upgrades, match value) and allowed modules (C# Hoi4TankDesigns)."""
    out = []
    for f in sorted((data.hoi4 / "common" / "ai_equipment").glob("*.txt")):
        for group, _, body in parse_pdx(read_text(f)):
            if not isinstance(body, list) or get(body, "category") != "land":
                continue
            designs = []
            for name, _, spec in body:
                if not isinstance(spec, list) or get(spec, "target_variant") is None:
                    continue
                tv = get(spec, "target_variant")
                if data.archetype(get(tv, "type")) not in data.tank_archetypes:
                    continue
                mods = []
                for slot, op, want in (get(tv, "modules") or []):
                    if isinstance(want, list):
                        any_of = tokens(get(want, "any_of")) if get(want, "any_of") else None
                        mod = next(((o, v) for k, o, v in want if k == "module"), None)
                        mods.append([slot, op, {"any_of": any_of} if any_of else {"op": mod[0], "module": mod[1]} if mod else {}])
                    else:
                        mods.append([slot, op, want])
                ups = {}
                for u, _, lv in (get(tv, "upgrades") or []):
                    if isinstance(lv, list):
                        ups[u] = {"base": num(get(lv, "base")),
                                  "modifiers": [{"add": num(get(m, "add")), "if": pdx_json([x for x in m if x[0] != "add"])}
                                                for k, _, m in lv if k == "modifier" and isinstance(m, list)]}
                    else:
                        ups[u] = {"base": num(lv), "modifiers": []}
                designs.append({"name": name, "priority": priority_json(get(spec, "priority")),
                                "enable": pdx_json(get(spec, "enable")) if get(spec, "enable") is not None else None,
                                "type": get(tv, "type"), "match": num(get(tv, "match_value")), "modules": mods,
                                "upgrades": ups, "allowed": tokens(get(spec, "allowed_modules"))})
            if designs:
                out.append({"group": group, "roles": tokens(get(body, "roles")), "blocked": tokens(get(body, "blocked_for")),
                            "available": tokens(get(body, "available_for")), "priority": priority_json(get(body, "priority")),
                            "designs": designs})
    return out


CATALOGUE_STATS = ("soft_attack", "hard_attack", "defense", "breakthrough", "hardness", "armor_value", "ap_attack",
                   "air_attack", "reliability", "maximum_speed")


class Designs:
    """Every country's tank designs for the equipment catalogue: its history designs (id "TAG:name") and default
    designs ("TAG:type": the archetype's default modules, no gun) where it needs a tank archetype it has no design
    of. Stats and cost from TankData.stats; order is creation order (a later design of the same chassis is newer)."""

    def __init__(self, data):
        self.data = data
        self.all = {}
        self.order = 0

    def add(self, tag, name, t, modules, upgrades, obsolete=False, icon=None):
        did = f"{tag}:{name}"
        if did in self.all:
            return did
        st = self.data.stats(t, modules, upgrades)
        info = type_info(self.data, t)
        self.order += 1
        self.all[did] = {"tag": tag, "name": name, "type": t, "modules": modules, "upgrades": upgrades,
                         "obsolete": obsolete, "icon": icon, "archetype": info["archetype"], "year": info["year"],
                         "order": self.order, "family": info["family"], "chassis_parent": info["parent"],
                         "cost": round(st.get("build_cost_ic", 0), 3),
                         "stats": {k: round(st.get(k, 0), 3) for k in CATALOGUE_STATS}}
        return did

    def default(self, tag, t):
        return self.add(tag, t, t, self.data.default_modules(t), {})

    def history(self, tag, root):
        """The country's create_equipment_variant tank designs, in file order."""
        out = []
        for v in collect_blocks(root, "create_equipment_variant", []):
            t = get(v, "type")
            if self.data.archetype(t) not in self.data.tank_archetypes or not get(v, "name"):
                continue
            mods = {k: m for k, _, m in (get(v, "modules") or []) if m != "empty"}
            ups = {k: int(num(x)) for k, _, x in (get(v, "upgrades") or [])}
            out.append(self.add(tag, get(v, "name"), t, mods, ups, get(v, "obsolete") == "yes", get(v, "icon")))
        return out

    def best(self, tag, archetype, types):
        """The country's newest design of an archetype it may build (not obsolete, its chassis type unlocked)."""
        cands = [(d["year"], d["order"], did) for did, d in self.all.items()
                 if d["tag"] == tag and d["archetype"] == archetype and not d["obsolete"] and d["type"] in types]
        return max(cands)[2] if cands else None

    def newest_type(self, archetype, types):
        tiers = [t for t in types if self.data.archetype(t) == archetype]
        return max(tiers, key=lambda t: (num(get(self.data.eq.get(t, []), "year")), t)) if tiers else None

    def oldest_type(self, archetype):
        tiers = [t for t in self.data.eq if self.data.archetype(t) == archetype and t != archetype and "_chassis" in t]
        return min(tiers, key=lambda t: (num(get(self.data.eq.get(t, []), "year")), t)) if tiers else None
