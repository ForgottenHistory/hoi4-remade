"""
HOI4 -> Archon data converter.

Reads map/state/country data from a local Hearts of Iron IV install and writes an
Archon-format data directory (same layout as Assets/Archon-Engine/Template-Data).

The output is derived from game files you own; it's meant for local engine testing
and is gitignored. Do not redistribute it.

Usage:
    python Tools/hoi4_convert.py [--hoi4 PATH] [--out PATH]
"""
import argparse
import filecmp
import copy
import json
import colorsys
import os
import re
import shutil
import struct
import sys
from pathlib import Path

import numpy as np
from PIL import Image

DEFAULT_HOI4 = r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV"
PROJECT_ROOT = Path(__file__).resolve().parent.parent
DEFAULT_OUT = PROJECT_ROOT / "Hoi4Data"
TEMPLATE = PROJECT_ROOT / "Assets" / "Archon-Engine" / "Template-Data"

# HOI4 province terrain -> Archon terrain category (color, movement cost, water)
TERRAIN = {
    "ocean":    ((8, 31, 130),    1.0, True),
    "lakes":    ((55, 90, 220),   1.0, True),
    "plains":   ((200, 214, 107), 1.0, False),
    "forest":   ((42, 85, 22),    1.5, False),
    "hills":    ((120, 140, 60),  1.5, False),
    "mountain": ((95, 70, 45),    2.0, False),
    "desert":   ((206, 169, 99),  1.05, False),
    "jungle":   ((0, 110, 40),    1.5, False),
    "marsh":    ((75, 147, 174),  2.0, False),
    "urban":    ((150, 150, 150), 1.0, False),
    "unknown":  ((200, 214, 107), 1.0, False),
    # Engine falls back to these names by key; keep them defined.
    "grasslands": ((86, 124, 27), 1.0, False),
}


# ---------------------------------------------------------------------------
# Minimal Paradox script parser
# ---------------------------------------------------------------------------
_TOKEN_RE = re.compile(r'"(?:[^"\\]|\\.)*"|[{}]|<=|>=|!=|[=<>]|[^\s{}=<>"#]+|#[^\n]*')


def parse_pdx(text):
    """Parse Paradox script into a list of (key, op, value) tuples. Blocks become lists."""
    tokens = [t for t in _TOKEN_RE.findall(text) if not t.startswith("#")]
    pos = 0

    def unq(t):
        return t[1:-1] if len(t) >= 2 and t[0] == '"' and t[-1] == '"' else t

    def parse_block():
        nonlocal pos
        items = []
        while pos < len(tokens):
            t = tokens[pos]
            if t == "}":
                pos += 1
                return items
            if t == "{":  # anonymous block
                pos += 1
                items.append((None, None, parse_block()))
                continue
            # key [op value] or bare value
            if pos + 1 < len(tokens) and tokens[pos + 1] in ("=", "<", ">", "<=", ">=", "!="):
                key, op = unq(t), tokens[pos + 1]
                pos += 2
                if pos >= len(tokens):
                    break
                v = tokens[pos]
                if v == "{":
                    pos += 1
                    items.append((key, op, parse_block()))
                else:
                    pos += 1
                    # color = rgb { ... } / hsv { ... }
                    if pos < len(tokens) and tokens[pos] == "{" and v.lower() in ("rgb", "hsv", "hsv360"):
                        pos += 1
                        items.append((key, op, (v.lower(), parse_block())))
                    else:
                        items.append((key, op, unq(v)))
            else:
                pos += 1
                items.append((None, None, unq(t)))
        return items

    return parse_block()


def get(items, key, default=None):
    for k, _, v in items:
        if k == key:
            return v
    return default


def get_all(items, key):
    return [v for k, _, v in items if k == key]


def values(items):
    return [v for k, _, v in items if k is None and isinstance(v, str)]


def read_text(path):
    return Path(path).read_text(encoding="utf-8-sig", errors="replace")


def parse_color(v):
    """Returns (r,g,b) 0-255 or None."""
    space = "rgb"
    if isinstance(v, tuple):
        space, v = v
    if not isinstance(v, list):
        return None
    nums = []
    for x in values(v):
        try:
            nums.append(float(x))
        except ValueError:
            pass
    if len(nums) < 3:
        return None
    if space == "hsv":
        r, g, b = colorsys.hsv_to_rgb(*nums[:3])
        return tuple(int(round(c * 255)) for c in (r, g, b))
    if space == "hsv360":
        r, g, b = colorsys.hsv_to_rgb(nums[0] / 360, nums[1] / 100, nums[2] / 100)
        return tuple(int(round(c * 255)) for c in (r, g, b))
    if all(c <= 1.0 for c in nums[:3]) and any(c != int(c) for c in nums[:3]):
        return tuple(int(round(c * 255)) for c in nums[:3])
    return tuple(int(max(0, min(255, c))) for c in nums[:3])


def load_localisation(loc_dir):
    loc = {}
    line_re = re.compile(r'^\s*([A-Za-z0-9_.\-]+):\d*\s*"(.*)"\s*(?:#.*)?$')
    for f in Path(loc_dir).rglob("*_l_english.yml"):
        for line in read_text(f).splitlines():
            m = line_re.match(line)
            if m:
                loc[m.group(1)] = m.group(2)
    return loc


def yml_escape(s):
    return s.replace('"', "'")


def safe_filename(s):
    return re.sub(r'[^A-Za-z0-9_\-]', "_", s)


# ---------------------------------------------------------------------------
# Conversion
# ---------------------------------------------------------------------------
def clear_output(out: Path):
    """Delete everything under out, file by file. The Unity editor keeps some files open (the
    label font), and on Windows those can't be deleted: they are left in place and replaced
    later only if their content changes (copy_if_changed). A plain rmtree stopped halfway."""
    locked = []
    for path in sorted(out.rglob("*"), key=lambda p: len(p.parts), reverse=True):
        try:
            if path.is_dir():
                path.rmdir()
            else:
                path.unlink()
        except PermissionError:
            locked.append(path)
        except OSError:
            pass  # directory still holding a locked file
    if locked:
        print(f"Kept {len(locked)} file(s) in use by another program: {', '.join(p.name for p in locked)}")


def copy_if_changed(src: Path, dst: Path):
    """copy2, skipped when dst already has the same content (it may be locked, see clear_output)."""
    if dst.exists() and filecmp.cmp(src, dst, shallow=False):
        return
    try:
        shutil.copy2(src, dst)
    except PermissionError:
        sys.exit(f"{dst} is in use and changed; close the program holding it (Unity?) and run again.")


def convert(hoi4: Path, out: Path):
    hmap = hoi4 / "map"
    if not (hmap / "provinces.bmp").exists():
        sys.exit(f"provinces.bmp not found under {hmap}")

    if out.exists():
        # Preserve nothing: caches (.pixels/.adjacency) are tied to the old images anyway.
        clear_output(out)
    (out / "map").mkdir(parents=True, exist_ok=True)

    loc = load_localisation(hoi4 / "localisation" / "english")
    print(f"Localisation keys: {len(loc)}")

    # --- definition.csv ----------------------------------------------------
    provinces = []  # (id, r, g, b, type, terrain)
    for line in read_text(hmap / "definition.csv").splitlines():
        p = line.strip().split(";")
        if len(p) < 7:
            continue
        try:
            pid = int(p[0])
        except ValueError:
            continue
        if pid <= 0:
            continue
        provinces.append((pid, int(p[1]), int(p[2]), int(p[3]), p[4].strip(), p[6].strip()))
    print(f"Provinces: {len(provinces)}")

    # --- states --------------------------------------------------------------
    province_state = {}   # pid -> state id
    province_owner = {}   # pid -> (owner, controller)
    state_names = {}      # state id -> display name
    impassable_states = set()
    vp_names = {}         # pid -> name
    state_top_vp = {}     # state id -> (value, pid) of its biggest victory point (the state's main city)
    owner_manpower = {}   # (tag, is core) -> summed state manpower (HOI4 recruitable manpower)
    for f in sorted((hoi4 / "history" / "states").glob("*.txt")):
        root = parse_pdx(read_text(f))
        st = get(root, "state")
        if not isinstance(st, list):
            continue
        sid = int(get(st, "id", 0))
        if get(st, "impassable") == "yes":
            impassable_states.add(sid)
        name_key = get(st, "name", "")
        state_names[sid] = loc.get(name_key, name_key)
        hist = get(st, "history", [])
        owner = get(hist, "owner") if isinstance(hist, list) else None
        if owner:
            try:
                # Core and non-core population apart: HOI4 recruits only a fraction of non-core states
                cores = {v for k, _, v in hist if k == "add_core_of"} if isinstance(hist, list) else set()
                key = (owner, owner in cores)
                owner_manpower[key] = owner_manpower.get(key, 0) + int(float(get(st, "manpower", 0)))
            except ValueError:
                pass
        controller = owner
        if isinstance(hist, list):
            for vp in get_all(hist, "victory_points"):
                vals = values(vp) if isinstance(vp, list) else []
                if vals:
                    vp_pid = int(vals[0])
                    try:
                        vp_value = float(vals[1]) if len(vals) > 1 else 0.0
                    except ValueError:
                        vp_value = 0.0
                    if sid not in state_top_vp or vp_value > state_top_vp[sid][0]:
                        state_top_vp[sid] = (vp_value, vp_pid)
                    n = loc.get(f"VICTORY_POINTS_{vp_pid}")
                    if n:
                        vp_names[vp_pid] = n
            # Explicit controller overrides (e.g. set_province_controller) are rare at 1936 start
            for k, _, v in hist:
                if k == "controller" and isinstance(v, str):
                    controller = v
        provs = get(st, "provinces", [])
        for pv in values(provs) if isinstance(provs, list) else []:
            pid = int(pv)
            province_state[pid] = sid
            if owner:
                province_owner[pid] = (owner, controller)
    print(f"States: {len(state_names)}, owned provinces: {len(province_owner)}")

    # --- countries -----------------------------------------------------------
    tags = {}  # tag -> country file rel path
    for line in read_text(hoi4 / "common" / "country_tags" / "00_countries.txt").splitlines():
        m = re.match(r'^\s*([A-Z0-9]{3})\s*=\s*"([^"]+)"', line)
        if m:
            tags[m.group(1)] = m.group(2)

    colors_root = parse_pdx(read_text(hoi4 / "common" / "countries" / "colors.txt"))
    color_override = {k: parse_color(get(v, "color")) for k, _, v in colors_root if isinstance(v, list)}

    ruling_party = {}
    capitals = {}  # tag -> capital province (top victory point of the capital state)
    country_stats = {}  # tag -> {stability, war_support, conscription} (top-level set_*, else the first nested one)

    # Mobilisation laws: law -> conscription factor (common/ideas/_manpower.txt)
    conscription_of = {}
    mp_file = hoi4 / "common" / "ideas" / "_manpower.txt"
    if mp_file.exists():
        laws = get(get(parse_pdx(read_text(mp_file)), "ideas", []), "mobilization_laws", [])
        for law, _, body in laws if isinstance(laws, list) else []:
            if isinstance(body, list):
                mod = get(body, "modifier")
                c = get(mod, "conscription") if isinstance(mod, list) else None
                try:
                    if law and c is not None:
                        conscription_of[law] = float(c)
                except ValueError:
                    pass

    def country_law(items):
        # add_ideas at the top level first (the 1936 start), then anywhere
        for k, _, v in items:
            if k == "add_ideas":
                for idea in (values(v) if isinstance(v, list) else [v]):
                    if idea in conscription_of:
                        return idea
        for _, _, v in items:
            if isinstance(v, list):
                r = country_law(v)
                if r:
                    return r
        return None

    def first_value(items, key):
        v = get(items, key)
        if v is not None and not isinstance(v, list):
            return v
        for _, _, sub in items:
            if isinstance(sub, list):
                r = first_value(sub, key)
                if r is not None:
                    return r
        return None
    for f in (hoi4 / "history" / "countries").glob("*.txt"):
        tag = f.name[:3]
        root = parse_pdx(read_text(f))
        try:
            cap_state = int(get(root, "capital"))
        except (TypeError, ValueError):
            cap_state = None
        if cap_state is not None:
            if cap_state in state_top_vp:
                capitals[tag] = state_top_vp[cap_state][1]
            else:
                state_provs = sorted(pid for pid, sid in province_state.items() if sid == cap_state)
                if state_provs:
                    capitals[tag] = state_provs[0]
        pol = get(root, "set_politics")
        if isinstance(pol, list):
            ruling_party[tag] = get(pol, "ruling_party")
        stats = {}
        for key, out_key in (("set_stability", "stability"), ("set_war_support", "war_support")):
            v = first_value(root, key)
            try:
                stats[out_key] = round(float(v), 3) if v is not None else 0.5
            except ValueError:
                stats[out_key] = 0.5
        law = country_law(root) or "volunteer_only"
        stats["conscription"] = conscription_of.get(law, 0.015)
        country_stats[tag] = stats

    (out / "common" / "countries").mkdir(parents=True)
    (out / "common" / "country_tags").mkdir(parents=True)
    tag_lines = ["# Generated from HOI4 common/country_tags/00_countries.txt"]
    country_loc = ["\ufeffl_english:"]
    for tag, rel in tags.items():
        cfile = hoi4 / "common" / rel
        color, gfx = None, "western_european_gfx"
        if cfile.exists():
            croot = parse_pdx(read_text(cfile))
            color = parse_color(get(croot, "color"))
            gfx = get(croot, "graphical_culture", gfx)
        color = color_override.get(tag) or color or (128, 128, 128)
        fname = f"{tag}.json5"
        tag_lines.append(f'{tag} = "countries/{fname}"')
        (out / "common" / "countries" / fname).write_text(
            f'{{\n  tag: "{tag}",\n  graphical_culture: "{gfx}",\n  color: [{color[0]}, {color[1]}, {color[2]}]\n}}\n',
            encoding="utf-8")

        party = ruling_party.get(tag) or "neutrality"
        name = loc.get(f"{tag}_{party}") or loc.get(tag) or loc.get(f"{tag}_neutrality") or tag
        adj = loc.get(f"{tag}_{party}_ADJ") or loc.get(f"{tag}_ADJ") or loc.get(f"{tag}_neutrality_ADJ") or name
        country_loc.append(f' {tag}:0 "{yml_escape(name)}"')
        country_loc.append(f' {tag}_ADJ:0 "{yml_escape(adj)}"')
    (out / "common" / "country_tags" / "00_countries.txt").write_text("\n".join(tag_lines) + "\n", encoding="utf-8")
    print(f"Countries: {len(tags)}")

    # --- province names, definition, history -----------------------------------
    def province_name(pid):
        if pid in vp_names:
            return vp_names[pid]
        sid = province_state.get(pid)
        if sid is not None:
            return state_names.get(sid, f"Province {pid}")
        return f"Province {pid}"

    def_lines = ["province;red;green;blue;name;x"]
    prov_loc = ["\ufeffl_english:"]
    hist_dir = out / "history" / "provinces"
    hist_dir.mkdir(parents=True)
    unmapped_terrain = set()
    for pid, r, g, b, ptype, terr in provinces:
        is_water = ptype in ("sea", "lake")
        def_lines.append(f"{pid};{r};{g};{b};Province_{pid};{'x' if is_water else ''}")
        prov_loc.append(f' PROV{pid}:0 "{yml_escape(province_name(pid))}"')

        if terr not in TERRAIN:
            unmapped_terrain.add(terr)
            terr = "ocean" if ptype == "sea" else "lakes" if ptype == "lake" else "plains"

        fields = [f'  terrain: "{terr}"']
        if pid in province_state:
            fields.append(f"  state: {province_state[pid]}")
        if pid in province_owner:
            o, c = province_owner[pid]
            fields.append(f'  owner: "{o}"')
            fields.append(f'  controller: "{c}"')
        (hist_dir / f"{pid}-Province_{pid}.json5").write_text(
            f"// {province_name(pid)}\n{{\n" + ",\n".join(fields) + "\n}\n", encoding="utf-8")
    (out / "map" / "definition.csv").write_text("\n".join(def_lines) + "\n", encoding="utf-8")
    write_regions(hoi4, out, provinces, province_state, impassable_states, capitals)
    write_weather(hoi4, out)
    convert_adjacencies(hmap, out, provinces)
    if unmapped_terrain:
        print(f"WARNING: unmapped terrain types (fell back): {sorted(unmapped_terrain)}")

    # --- localisation --------------------------------------------------------
    loc_out = out / "localisation" / "english"
    loc_out.mkdir(parents=True)
    (loc_out / "countries_l_english.yml").write_text("\n".join(country_loc) + "\n", encoding="utf-8")
    (loc_out / "provinces_l_english.yml").write_text("\n".join(prov_loc) + "\n", encoding="utf-8")
    terr_loc = ["\ufeffl_english:"] + [f' {k}:0 "{k.replace("_", " ").title()}"' for k in TERRAIN]
    (loc_out / "terrain_l_english.yml").write_text("\n".join(terr_loc) + "\n", encoding="utf-8")
    state_loc = ["\ufeffl_english:"] + [f' STATE_{s}:0 "{yml_escape(n)}"' for s, n in sorted(state_names.items())]
    (loc_out / "states_l_english.yml").write_text("\n".join(state_loc) + "\n", encoding="utf-8")

    # --- terrain.json5 -------------------------------------------------------
    tj = ["// Generated from HOI4 province terrain types (definition.csv column 7).",
          "// combat_width, combat_support_width (per extra attack direction) and attack (the attacker's",
          "// attack and breakthrough modifier) and supply_flow_penalty_factor (added to the hub supply lost per",
          "// province step) are GAME fields from common/terrain/00_terrain.txt.",
          "{", "  categories: {"]
    combat = load_terrain_combat(hoi4)
    entries = []
    for k, (c, cost, water) in TERRAIN.items():
        e = f"    {k}: {{\n      color: [{c[0]}, {c[1]}, {c[2]}],\n      movement_cost: {cost}"
        if water:
            e += ",\n      is_water: true"
        for field, v in combat.get(k, {}).items():
            e += f",\n      {field}: {v}"
        entries.append(e + "\n    }")
    tj.append(",\n".join(entries))
    tj += ["  }", "}"]
    (out / "map" / "terrain.json5").write_text("\n".join(tj) + "\n", encoding="utf-8")

    # --- images ----------------------------------------------------------------
    print("Converting images...")
    prov_img = Image.open(hmap / "provinces.bmp").convert("RGB")
    w, h = prov_img.size
    prov_img.save(out / "map" / "provinces.png", optimize=False, compress_level=1)

    # terrain.png: each province painted with its gameplay terrain color
    px = np.asarray(prov_img, dtype=np.uint32)
    packed = (px[..., 0] << 16) | (px[..., 1] << 8) | px[..., 2]
    keys = np.array([(r << 16) | (g << 8) | b for _, r, g, b, _, _ in provinces], dtype=np.uint32)
    cols = np.array([TERRAIN.get(t, TERRAIN["plains"])[0] for *_, t in provinces], dtype=np.uint8)
    order = np.argsort(keys)
    keys, cols = keys[order], cols[order]
    idx = np.searchsorted(keys, packed).clip(0, len(keys) - 1)
    found = keys[idx] == packed
    terrain_px = np.where(found[..., None], cols[idx], np.array(TERRAIN["ocean"][0], dtype=np.uint8))
    missing = int((~found).sum())
    if missing:
        print(f"WARNING: {missing} pixels in provinces.bmp have no definition.csv entry")
    Image.fromarray(terrain_px.astype(np.uint8), "RGB").save(out / "map" / "terrain.png", compress_level=1)

    Image.open(hmap / "heightmap.bmp").convert("L").convert("RGB").save(out / "map" / "heightmap.png", compress_level=1)

    normal = Image.open(hmap / "world_normal.bmp").convert("RGB")
    if normal.size != (w, h):
        normal = normal.resize((w, h), Image.BILINEAR)
    normal.save(out / "map" / "world_normal.png", compress_level=1)

    water_keys = np.array([t in ("sea", "lake") for _, _, _, _, t, _ in provinces], dtype=bool)[order]
    water_px = found & water_keys[idx]
    prov_ids = np.array([p[0] for p in provinces], dtype=np.int32)[order]
    write_river_crossings(hoi4, out, np.where(found & ~water_px, prov_ids[idx], 0))
    # State centres (mean pixel of their land, map px, y down): the peace conference's influence distances
    land_ids = np.where(found & ~water_px, prov_ids[idx], 0)
    write_province_centres(out, land_ids)
    write_railways(hoi4, out, land_ids)
    write_impassable_borders(out, land_ids, {pid for pid, sid in province_state.items() if sid in impassable_states})
    state_lut = np.zeros(int(land_ids.max()) + 1, dtype=np.int64)
    for pid, sid in province_state.items():
        if pid < len(state_lut):
            state_lut[pid] = sid
    state_px = state_lut[land_ids]
    sy, sx = np.nonzero(state_px)
    sv = state_px[sy, sx]
    n_px = np.bincount(sv)
    with np.errstate(invalid="ignore", divide="ignore"):
        mx = np.bincount(sv, weights=sx) / n_px
        my = np.bincount(sv, weights=sy) / n_px
    state_centers = {int(s): [int(round(mx[s])), int(round(my[s]))] for s in np.nonzero(n_px)[0]}
    convert_gfx(hoi4, out, water_px)

    (out / "map" / "default.json5").write_text(
        f'{{\n  // Generated from HOI4 map\n  "width": {w},\n  "height": {h},\n'
        f'  "max_provinces": {max(p[0] for p in provinces) + 1},\n'
        f'  "sea_starts": [],\n  "lake_starts": [],\n  "ocean_starts": []\n}}\n', encoding="utf-8")

    # --- carry over StarterKit-required template data ---------------------------
    # (units, buildings, UI strings, visual style) so the StarterKit game layer runs.
    for rel in ["units", "common/buildings", "localisation/english/ui_l_english.yml"]:
        src = TEMPLATE / rel
        dst = out / rel
        if src.is_dir():
            shutil.copytree(src, dst, ignore=shutil.ignore_patterns("*.meta"), dirs_exist_ok=True)
        elif src.exists():
            dst.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(src, dst)

    # 1936 land armies (after the template copy: our unit types replace the template infantry)
    oob_countries, main_types, land_subs = convert_oob(hoi4, out, {o for o, _ in province_owner.values()}, ruling_party)
    convert_counter_gfx(hoi4, out, {t: c["ideology"] for t, c in oob_countries.items()}, main_types)
    convert_unit_panel(hoi4, out, main_types)
    convert_army_cards(hoi4, out)
    convert_battleplan_gfx(hoi4, out)
    convert_peace_gfx(hoi4, out)
    convert_production_gfx(hoi4, out, oob_countries)
    convert_research_gfx(hoi4, out)
    convert_construction_gfx(hoi4, out)
    convert_deployment_gfx(hoi4, out)
    convert_designer_gfx(hoi4, out, land_subs)
    convert_front_line(hoi4, out)
    convert_state_view(hoi4, out, state_centers)
    convert_move_arrow(hoi4, out)
    write_country_stats(out, tags, country_stats, owner_manpower, ruling_party)
    write_country_economy(hoi4, out, tags)
    write_stability(hoi4, out)
    write_technologies(hoi4, out, tags, loc)
    convert_techtree_gfx(hoi4, out, loc)
    convert_scrollbar_gfx(hoi4, out)
    convert_topbar(hoi4, out, {t: ruling_party.get(t) or "neutrality" for t in tags})

    # HOI4 visual style data for the GAME terrain shader
    shutil.copy2(Path(__file__).with_name("hoi4_visual_style.json5"), out / "map" / "visual_style.json5")

    # Map label font (OFL; see Tools/fonts/OFL.txt)
    (out / "gfx" / "fonts").mkdir(parents=True, exist_ok=True)
    for font in Path(__file__).with_name("fonts").glob("*.ttf"):
        copy_if_changed(font, out / "gfx" / "fonts" / font.name)

    print(f"Done -> {out}")


WATER_SLOT = 255  # terrain index value for sea/lake pixels (rendered as water by the GAME shader)

# DDS -> PNG copies for the GAME terrain shader: (source under HOI4/map/terrain, output name)
GFX_TEXTURES = [
    ("atlas0.dds", "atlas_diffuse.png"),             # 4x4 tiles, rgb diffuse, a gloss
    ("atlas_normal0.dds", "atlas_normal.png"),       # 4x4 tiles, rgb normal (.rbg), a spec
    ("colormap_rgb_cityemissivemask_a.dds", "colormap.png"),  # land tint, a city lights mask
    ("colormap_water_0.dds", "colormap_water.png"),  # sea tint
    ("underwater_terrain_0.dds", "underwater.png"),  # tiled sea floor
    ("citylights_rgb_snowmask_a_0.dds", "citylights.png"),  # tiled city lights (rgb), a snow mask
    ("snow_normal_rgb_diffuse_a.dds", "snow.png"),    # tiled snow cover: rgb normal (.rbg), a diffuse
    ("mud_diffuse_rgb_gloss_a_0.dds", "mud_diffuse.png"),  # tiled mud: rgb diffuse, a gloss
    ("mud_normal_rgb_spec_a_0.dds", "mud_normal.png"),     # tiled mud: rgb normal (.rbg), a spec
    ("ice_diffuse.dds", "ice_diffuse.png"),          # tiled sea ice: rgb normal (.rbg), a diffuse
    ("ice_noise_0.dds", "ice_noise.png"),            # sea ice break-up noise
]


def write_regions(hoi4: Path, out: Path, provinces, province_state, impassable_states, capitals):
    """GAME data: province groupings the engine doesn't model (states, strategic regions), plus
    country capitals (province ids). Used by the HOI4 border classifier (state / sea region /
    impassable borders) and the HOI4 map label source (which piece of a country gets its name)."""
    regions = {}
    for f in sorted((hoi4 / "map" / "strategicregions").glob("*.txt")):
        root = parse_pdx(read_text(f))
        sr = get(root, "strategic_region")
        if not isinstance(sr, list):
            continue
        rid = int(get(sr, "id", 0))
        provs = get(sr, "provinces", [])
        regions[rid] = [int(v) for v in values(provs)] if isinstance(provs, list) else []

    states = {}
    for pid, sid in province_state.items():
        states.setdefault(sid, []).append(pid)
    water = sorted(pid for pid, _, _, _, ptype, _ in provinces if ptype in ("sea", "lake"))

    def block(name, groups):
        rows = [f'    "{k}": [{", ".join(map(str, sorted(v)))}]' for k, v in sorted(groups.items())]
        return f"  {name}: {{\n" + ",\n".join(rows) + "\n  }"

    text = ("// Generated by hoi4_convert.py: HOI4 states and strategic regions (province id lists)\n{\n"
            + block("states", states) + ",\n"
            + block("strategic_regions", regions) + ",\n"
            + f"  impassable_states: [{', '.join(map(str, sorted(impassable_states)))}],\n"
            + f"  water_provinces: [{', '.join(map(str, water))}],\n"
            + "  capitals: {" + ", ".join(f'"{t}": {p}' for t, p in sorted(capitals.items())) + "}\n}\n")
    (out / "map" / "hoi4_regions.json5").write_text(text, encoding="utf-8")
    print(f"Regions: {len(states)} states, {len(regions)} strategic regions, {len(impassable_states)} impassable, {len(capitals)} capitals")


def write_weather(hoi4: Path, out: Path):
    """GAME data: each strategic region's weather periods (map/strategicregions/*.txt weather = { period = {...} }) as
    map/hoi4_weather.json5, integers so the game reads them exactly: per region a list of
    [from_month, from_day, to_month, to_day (1-based), temp_lo x10, temp_hi x10, weights x1000 (no_phenomenon,
    rain_light, rain_heavy, snow, blizzard, sandstorm), mud x1000]. The rules (common/weather.txt) are constants in
    Hoi4Weather.cs. Prototype: Tools/prototypes/weather/weather.py."""
    kinds = ["no_phenomenon", "rain_light", "rain_heavy", "snow", "blizzard", "sandstorm"]
    rows = []
    for f in sorted((hoi4 / "map" / "strategicregions").glob("*.txt")):
        root = parse_pdx(read_text(f))
        sr = get(root, "strategic_region")
        if not isinstance(sr, list):
            continue
        rid = int(get(sr, "id", 0))
        weather = get(sr, "weather")
        periods = []
        for per in (get_all(weather, "period") if isinstance(weather, list) else []):
            if not isinstance(per, list):
                continue
            a, z = (str(v) for v in values(get(per, "between", [])))
            lo, hi = (float(v) for v in values(get(per, "temperature", [0, 0])))
            d0, m0 = (int(v) for v in a.split("."))
            d1, m1 = (int(v) for v in z.split("."))
            row = [m0 + 1, d0 + 1, m1 + 1, d1 + 1, round(lo * 10), round(hi * 10)]
            row += [round(float(get(per, k, 0)) * 1000) for k in kinds]
            row.append(round(float(get(per, "mud", 1)) * 1000))
            periods.append(row)
        if periods:
            rows.append(f'    "{rid}": [' + ", ".join("[" + ", ".join(map(str, p)) + "]" for p in periods) + "]")
    text = ("// Generated by hoi4_convert.py: weather periods per strategic region, see write_weather\n{\n  regions: {\n"
            + ",\n".join(rows) + "\n  }\n}\n")
    (out / "map" / "hoi4_weather.json5").write_text(text, encoding="utf-8")
    print(f"Weather: {len(rows)} strategic regions with weather periods")


def write_railways(hoi4: Path, out: Path, land_ids):
    """GAME data: the 1936 railways and supply hubs. map/railways.txt has one railway per line,
    "LEVEL COUNT PROVINCE..": each consecutive pair of its provinces is one rail link (they always border each
    other or share a strait). A link on several lines takes the highest level. map/supply_nodes.txt has
    "LEVEL PROVINCE" per hub. Written as map/hoi4_railways.json5: links [a, b, level] with a < b, sorted,
    hubs {province: level}, and points {province: [x, y]}: where rail runs through each land province, in map
    pixels (y down), from its supply_node spot in map/buildings.txt ("STATE;supply_node;X;HEIGHT;Z;..", Z up
    from the bottom), else its mean pixel. Checked in Tools/prototypes/supply/rail_network.py and render_rails.py.
    Also the track texture: gfx/models/supply/railroad.dds holds five 64x64 tiles stacked, one per rail level
    (rails along x), written as gfx/map/rails/rail_<level>.png."""
    links = {}
    for raw in read_text(hoi4 / "map" / "railways.txt").splitlines():
        v = [int(x) for x in raw.split() if x.isdigit()]
        if len(v) < 4:
            continue
        level, provs = v[0], v[2:2 + v[1]]
        for a, b in zip(provs, provs[1:]):
            if a != b:
                k = (min(a, b), max(a, b))
                links[k] = max(links.get(k, 0), level)
    hubs = {}
    for raw in read_text(hoi4 / "map" / "supply_nodes.txt").splitlines():
        v = raw.split()
        if len(v) == 2 and v[0].isdigit() and v[1].isdigit():
            hubs[int(v[1])] = int(v[0])
    # rail points: the supply_node spot of each land province, else its mean pixel
    h = land_ids.shape[0]
    points = {}
    for line in read_text(hoi4 / "map" / "buildings.txt").splitlines():
        c = line.split(";")
        if len(c) < 5 or c[1] != "supply_node":
            continue
        try:
            x, z = float(c[2]), float(c[4])
        except ValueError:
            continue
        px, py = int(x), int(h - 1 - z)
        if 0 <= px < land_ids.shape[1] and 0 <= py < h and land_ids[py, px]:
            points.setdefault(int(land_ids[py, px]), (round(x, 2), round(h - z, 2)))
    needed = {p for k in links for p in k} | set(hubs)
    missing = sorted(p for p in needed if p not in points)
    if missing:
        flat = land_ids.ravel()
        n = int(flat.max()) + 1
        ys, xs = np.divmod(np.arange(flat.size, dtype=np.int64), land_ids.shape[1])
        cnt = np.bincount(flat, minlength=n)
        sx = np.bincount(flat, xs, minlength=n)
        sy = np.bincount(flat, ys, minlength=n)
        for p in missing:
            if p < n and cnt[p]:
                points[p] = (round(sx[p] / cnt[p] + 0.5, 2), round(sy[p] / cnt[p] + 0.5, 2))
    pts = ", ".join(f'"{p}": [{points[p][0]}, {points[p][1]}]' for p in sorted(needed) if p in points)

    rails = out / "gfx" / "map" / "rails"
    rails.mkdir(parents=True, exist_ok=True)
    track = Image.open(hoi4 / "gfx" / "models" / "supply" / "railroad.dds").convert("RGBA")
    tile = track.width
    for level in range(1, track.height // tile + 1):
        track.crop((0, (level - 1) * tile, tile, level * tile)).save(rails / f"rail_{level}.png")
    convert_supply_rails(hoi4, out)

    rows = ",\n".join(f"    [{a}, {b}, {l}]" for (a, b), l in sorted(links.items()))
    text = ("// Generated by hoi4_convert.py from map/railways.txt and map/supply_nodes.txt\n{\n"
            + "  links: [\n" + rows + "\n  ],\n"
            + "  hubs: {" + ", ".join(f'"{p}": {l}' for p, l in sorted(hubs.items())) + "},\n"
            + "  points: {" + pts + "}\n}\n")
    (out / "map" / "hoi4_railways.json5").write_text(text, encoding="utf-8")
    print(f"Railways: {len(links)} rail links, {len(hubs)} supply hubs, {len(needed) - len(missing)} supply_node "
          f"points, {len(missing)} from mean pixels; track tiles {track.height // tile}")


def convert_rivers(hoi4: Path, out: Path, gfx: Path, water_px=None):
    """map/rivers.png: river width level per pixel (R=G=B, 0 = no river, 1..9 narrow..wide),
    from HOI4 rivers.bmp (palette 3..11 = widths; 0/1/2 = source/join/split markers, which take
    their neighbours' width). Plus gfx/map/rivers/river.png, a strip texture baked from HOI4's
    RiverSurface textures: U along the river, V across; water in the middle half, banks outside.
    River pixels on sea/lake provinces are dropped: HOI4 draws its water surface over river mouths,
    our river meshes are drawn on top of everything, so they would show in the water."""
    a = np.asarray(Image.open(hoi4 / "map" / "rivers.bmp")).astype(np.int16)
    level = np.where((a >= 3) & (a <= 11), a - 2, 0).astype(np.uint8)
    markers = np.argwhere((a <= 2) & (level == 0))
    H, W = a.shape
    for _ in range(3):
        for y, x in markers:
            if level[y, x]:
                continue
            nb = level[max(y - 1, 0):y + 2, max(x - 1, 0):x + 2]
            if nb.max():
                level[y, x] = nb.max()
    level[(a <= 2) & (level == 0)] = 1
    dropped = 0
    if water_px is not None and water_px.shape == level.shape:
        dropped = int(((level > 0) & water_px).sum())
        level[water_px] = 0
    Image.fromarray(np.repeat(level[..., None], 3, axis=2), "RGB").save(out / "map" / "rivers.png", compress_level=1)

    # Water color: HOI4 colormap_water averaged under river pixels, at WATER_COLOR_LIGHTNESS-ish brightness
    cw = Image.open(hoi4 / "map" / "terrain" / "colormap_water_0.dds").convert("RGB").resize((W, H), Image.BILINEAR)
    water = np.asarray(cw).astype(np.float32)[level > 0].mean(axis=0) / 255.0
    water = np.clip(water * 0.9 + 0.08, 0, 1)

    diffuse = np.asarray(Image.open(hoi4 / "map" / "terrain" / "RiverSurface_diffuse_0.dds").convert("RGB")).astype(np.float32) / 255.0
    masks = np.asarray(Image.open(hoi4 / "map" / "terrain" / "RiverSurface_masks.dds").convert("RGBA")).astype(np.float32) / 255.0
    dh, dw = diffuse.shape[:2]
    mh = masks.shape[0]
    TW, TH = dw, 64
    tex = np.zeros((TH, TW, 4), np.float32)
    for row in range(TH):
        v = (row + 0.5) / TH
        t = abs(v - 0.5) * 2.0                    # 0 center .. 1 edge
        if t <= 0.5:
            tex[row, :, :3] = water
            tex[row, :, 3] = 1.0
            continue
        sv = (t - 0.5) * 2.0                       # 0 inner bank .. 1 edge (HOI4 v*4-3 on the folded strip)
        mrow = masks[min(int(sv * mh), mh - 1)].mean(axis=0)
        bank = mrow[2]                             # b: water -> bank
        alpha = mrow[0]                            # r: fades out at the very edge
        drow = diffuse[min(int((1.0 - sv) * dh), dh - 1)]
        tex[row, :, :3] = water * (1 - bank) + drow * bank
        tex[row, :, 3] = alpha
    rivers_gfx = gfx / "rivers"
    rivers_gfx.mkdir(parents=True, exist_ok=True)
    Image.fromarray((np.clip(tex, 0, 1) * 255).astype(np.uint8), "RGBA").save(rivers_gfx / "river.png")
    print(f"Rivers: {int((level > 0).sum())} river pixels ({dropped} on water dropped), water color {np.round(water, 3).tolist()}")


def read_dds_cubemap(path: Path):
    """Faces (+X, -X, +Y, -Y, +Z, -Z) of an uncompressed 32-bit DDS cubemap, each HxWx3 RGB float.
    Pillow only reads the first face, so this walks the file: header, then per face the full mip chain."""
    b = path.read_bytes()
    h, w = struct.unpack_from("<II", b, 12)
    mips = max(struct.unpack_from("<I", b, 28)[0], 1)
    bits = struct.unpack_from("<I", b, 88)[0]
    rmask = struct.unpack_from("<I", b, 92)[0]
    if bits != 32 or not (struct.unpack_from("<I", b, 112)[0] & 0x200):
        sys.exit(f"{path.name}: expected an uncompressed 32-bit cubemap")
    order = [2, 1, 0] if rmask == 0xFF0000 else [0, 1, 2]  # BGRA or RGBA
    faces, off = [], 128
    for _ in range(6):
        px = np.frombuffer(b, np.uint8, w * h * 4, off).reshape(h, w, 4)
        faces.append(px[..., order].astype(np.float32) / 255.0)
        off += sum(max(w >> m, 1) * max(h >> m, 1) * 4 for m in range(mips))
    return faces


def cubemap_to_latlong(faces, width=512):
    """Bake a D3D cubemap to a lat-long image (row 0 = straight up, u = 0.5 looking north (+z),
    u increasing towards east (+x)). Same axes as the map: x east, y up, z north."""
    height = width // 2
    lon = ((np.arange(width) + 0.5) / width - 0.5) * 2 * np.pi
    lat = (0.5 - (np.arange(height) + 0.5) / height) * np.pi
    lon, lat = np.meshgrid(lon, lat)
    d = np.stack([np.cos(lat) * np.sin(lon), np.sin(lat), np.cos(lat) * np.cos(lon)], axis=-1)
    ax = np.abs(d)
    major = np.argmax(ax, axis=-1)
    x, y, z = d[..., 0], d[..., 1], d[..., 2]
    face = np.where(major == 0, np.where(x > 0, 0, 1), np.where(major == 1, np.where(y > 0, 2, 3), np.where(z > 0, 4, 5)))
    # D3D face coordinates (sc, tc) / |major axis|, t pointing down
    sc = np.choose(face, [-z, z, x, x, x, -x])
    tc = np.choose(face, [-y, -y, z, -z, -y, -y])
    ma = np.choose(major, [ax[..., 0], ax[..., 1], ax[..., 2]])
    size = faces[0].shape[0]
    u = np.clip(((sc / ma + 1) * 0.5 * size).astype(int), 0, size - 1)
    v = np.clip(((tc / ma + 1) * 0.5 * size).astype(int), 0, size - 1)
    out = np.zeros((height, width, 3), np.float32)
    for f in range(6):
        m = face == f
        out[m] = faces[f][v[m], u[m]]
    return out


def convert_water(hoi4: Path, gfx: Path):
    """HOI4 pdxwater inputs: LEAN wave maps (lean1/lean2, data), the per-map wave mask
    (fow_rgb_waterspec_a alpha: 0 = calm/sheltered, higher = open sea), and the sky reflection
    cubemap baked to lat-long (water_sky.png) because the visual style only loads 2D textures."""
    t = hoi4 / "map" / "terrain"
    water = gfx / "water"
    water.mkdir(parents=True, exist_ok=True)
    for n in ("lean1", "lean2"):
        Image.open(t / f"{n}.dds").convert("RGBA").save(water / f"{n}.png")
    spec = np.asarray(Image.open(t / "fow_rgb_waterspec_a.dds").convert("RGBA"))[..., 3]
    Image.fromarray(np.repeat(spec[..., None], 3, axis=2), "RGB").save(water / "water_spec.png", compress_level=1)
    sky = cubemap_to_latlong(read_dds_cubemap(t / "reflection.dds"))
    Image.fromarray((np.clip(sky, 0, 1) * 255).astype(np.uint8), "RGB").save(water / "water_sky.png")
    print("Water: LEAN waves, wave mask, sky reflection")


COAST_BAND = 2            # map px from the province coast where the heightmap decides the coast
COAST_HEIGHT_MIN = 8.5    # coast height field range (HOI4 height units, sea level 9.5)
COAST_HEIGHT_RANGE = 2.0


def coast_channels(hoi4: Path, index):
    """HOI4 has no coastline geometry: its water plane fades out where the 9-tap blurred
    heightmap reaches sea level, which gives a smooth coast instead of province pixel steps.
    Returns (nearest land tile per pixel, coast height field) for the terrain index G/B channels.
    The field is the blurred heightmap mapped to COAST_HEIGHT_MIN..+RANGE, forced to full land
    or water more than COAST_BAND px from the province coast (so inland depressions below sea
    level stay land). Nearest land tile lets the smooth coast draw land on sea-province pixels."""
    from scipy import ndimage
    water = index == WATER_SLOT
    hm = np.asarray(Image.open(hoi4 / "map" / "heightmap.bmp").convert("L")).astype(np.float32) * 0.1
    if hm.shape != index.shape:
        sys.exit(f"heightmap {hm.shape} != terrain {index.shape}")
    hm = ndimage.convolve(hm, np.array([[1, 2, 1], [2, 4, 2], [1, 2, 1]], np.float32) / 16, mode="nearest")
    field = np.clip((hm - COAST_HEIGHT_MIN) / COAST_HEIGHT_RANGE, 0, 1)
    field[~water & (ndimage.distance_transform_edt(~water) > COAST_BAND)] = 1
    field[water & (ndimage.distance_transform_edt(water) > COAST_BAND)] = 0
    iy, ix = ndimage.distance_transform_edt(water, return_distances=False, return_indices=True)
    nearest = index[iy, ix]
    return nearest.astype(np.uint8), (field * 255 + 0.5).astype(np.uint8)


COAST_DISTANCE_SCALE = 16  # terrain index A: water distance from the smooth coast, 1/16 map px per step (up to ~16 px)


def coast_distance(coast):
    """Distance of each water pixel from the smooth coast (the shore isoline of the coast height field, where the
    GAME shader's land coverage crosses 0.5), in map px, measured at twice the resolution so it has no province
    pixel steps. For effects that hug the coast (sea ice). Returns uint8: distance * COAST_DISTANCE_SCALE, land 0."""
    from scipy import ndimage
    field = coast.astype(np.float32) / 255
    up = ndimage.zoom(field, 2, order=1, mode="nearest", grid_mode=True)
    land = (((8.5 + up * 2.0) / 9.57 - 0.954) * 25.0) > 0.5   # Hoi4ShoreAlpha > 0.5
    del up
    dist = ndimage.distance_transform_edt(~land).astype(np.float32) / 2.0
    del land
    h, w = coast.shape
    dist = dist.reshape(h, 2, w, 2).mean(axis=(1, 3))
    return np.clip(dist * COAST_DISTANCE_SCALE + 0.5, 0, 255).astype(np.uint8)


OOB_START = "1936"


def collect_oob_names(items, out, dlc_here=False, dlc_outside=False):
    """set_oob / oob at the top level of a country history file or inside if/else blocks (not
    date blocks). Appends (name, is_in_a_has_dlc_branch) pairs. Keywords may be upper case (IF,
    ELSE_IF); an else/else_if nested inside an if block is that if's alternative, so it takes
    the DLC status from outside the if (the no-DLC branch of `if has_dlc ... else`)."""
    for k, _, v in items:
        key = k.lower()
        if key in ("oob", "set_oob") and isinstance(v, str):
            out.append((v, dlc_here))
        elif key in ("if", "else_if", "else") and isinstance(v, list):
            limit = get(v, "limit")
            dlc = isinstance(limit, list) and any(kk.lower() == "has_dlc" for kk, _, _ in limit)
            if key == "if":
                collect_oob_names(v, out, dlc_here or dlc, dlc_here)
            else:
                collect_oob_names(v, out, dlc_outside or dlc, dlc_outside)


def ordinal_en(n):
    return f"{n}{'th' if 10 <= n % 100 <= 20 else {1: 'st', 2: 'nd', 3: 'rd'}.get(n % 10, 'th')}"


def format_division_name(fmt, n):
    """HOI4 name formats: %d / %s = the number; %dth = English ordinal; other letters after %d
    (de, ya, e, ...) are language endings and stay as written."""
    fmt = fmt.replace("%dth", ordinal_en(n))
    return fmt.replace("%d", str(n)).replace("%s", str(n))


def load_division_name_groups(hoi4: Path):
    """common/units/names_divisions: group -> (fallback format, {number: format})."""
    groups = {}
    for f in sorted((hoi4 / "common" / "units" / "names_divisions").glob("*.txt")):
        for key, _, body in parse_pdx(read_text(f)):
            if not isinstance(body, list):
                continue
            ordered = {}
            for num, _, names in (get(body, "ordered") or []):
                if isinstance(names, list) and names and num.isdigit():
                    first = names[0][0] if isinstance(names[0], tuple) else names[0]
                    ordered[int(num)] = first
            groups[key] = (get(body, "fallback_name"), ordered)
    return groups


COMBAT_STATS = ("soft_attack", "hard_attack", "defense", "breakthrough", "hardness", "armor_value", "ap_attack",
                "air_attack", "reliability")


def load_equipment_combat(hoi4: Path, year=1936):
    """{archetype: combat stats} of the newest equipment design available in YEAR (the oldest
    designs carry no stats of their own and inherit from `parent`, then from the archetype)."""
    entries = {}
    for f in sorted((hoi4 / "common" / "units" / "equipment").glob("*.txt")):
        for _, _, block in parse_pdx(read_text(f)):
            for name, _, eq in (block if isinstance(block, list) else []):
                if not isinstance(eq, list):
                    continue
                stats = {}
                for k in COMBAT_STATS:
                    v = get(eq, k)
                    try:
                        stats[k] = float(v)
                    except (TypeError, ValueError):
                        pass
                y = get(eq, "year")
                entries[name] = {"stats": stats, "archetype": get(eq, "archetype"), "parent": get(eq, "parent"),
                                 "year": int(y) if y and str(y).isdigit() else 0,
                                 "is_archetype": get(eq, "is_archetype") == "yes"}

    def resolve(name, depth=0):
        e = entries.get(name)
        if e is None or depth > 8:
            return {}
        out = {}
        for link in (e["archetype"], e["parent"]):
            if link and link != name:
                out.update(resolve(link, depth + 1))
        out.update(e["stats"])
        return out

    best = {}
    for name, e in entries.items():
        arch = name if e["is_archetype"] else e["archetype"]
        if not arch or e["year"] > year:
            continue
        if arch not in best or e["year"] >= entries[best[arch]]["year"]:
            best[arch] = name
    return {arch: resolve(name) for arch, name in best.items()}


def load_sub_units(hoi4: Path):
    """common/units sub_units, with speed (km/h) from their equipment: per archetype the slowest
    variant (the oldest design, fitting 1936; tank chassis archetypes only carry a base 4) or the
    archetype's own speed; per sub-unit the fastest of its equipment (motorized needs rifles at
    4 km/h and trucks at 12, and moves at truck speed)."""
    archetype_speed = {}
    variant_speeds = {}
    for f in sorted((hoi4 / "common" / "units" / "equipment").glob("*.txt")):
        for _, _, block in parse_pdx(read_text(f)):
            for name, _, eq in (block if isinstance(block, list) else []):
                if not isinstance(eq, list) or not get(eq, "maximum_speed"):
                    continue
                if get(eq, "is_archetype") == "yes":
                    archetype_speed[name] = float(get(eq, "maximum_speed"))
                elif get(eq, "archetype"):
                    variant_speeds.setdefault(get(eq, "archetype"), []).append(float(get(eq, "maximum_speed")))
    for name, speeds in variant_speeds.items():
        archetype_speed[name] = min(speeds)
    combat_eq = load_equipment_combat(hoi4)
    subs = {}
    for f in sorted((hoi4 / "common" / "units").glob("*.txt")):
        for key, _, block in parse_pdx(read_text(f)):
            if key != "sub_units" or not isinstance(block, list):
                continue
            for name, _, u in block:
                if not isinstance(u, list):
                    continue
                needs = [k for k, _, _ in (get(u, "need") or [])]
                speeds = [archetype_speed[n] for n in needs if n in archetype_speed]

                def num(k, d=0.0):
                    v = get(u, k, d)
                    try:
                        return float(v)
                    except (TypeError, ValueError):
                        return d
                need = {}
                for k, _, v in (get(u, "need") or []):
                    try:
                        need[k] = int(float(v))
                    except (TypeError, ValueError):
                        pass
                eq = [combat_eq.get(n, {}) for n in needs]
                stat = lambda k: sum(e.get(k, 0.0) for e in eq) * (1 + num(k))
                rel = [e["reliability"] for e in eq if "reliability" in e]
                subs[name] = {
                    "abbreviation": get(u, "abbreviation", name[:3].upper()),
                    "group": get(u, "group", "infantry"),
                    "icon": get(u, "map_icon_category", "infantry"),
                    "priority": int(num("priority")),
                    "combat_width": num("combat_width"),
                    "max_strength": num("max_strength"),
                    "max_organisation": num("max_organisation"),
                    "manpower": int(num("manpower")),
                    "training_time": int(num("training_time")),  # days; a division trains as long as its slowest
                    "speed": max(speeds) if speeds else 4.0,
                    "soft_attack": round(stat("soft_attack"), 2),
                    "hard_attack": round(stat("hard_attack"), 2),
                    "defense": round(stat("defense"), 2),
                    "breakthrough": round(stat("breakthrough"), 2),
                    "hardness": round(max((e.get("hardness", 0.0) for e in eq), default=0.0), 3),
                    "armor": round(max((e.get("armor_value", 0.0) for e in eq), default=0.0), 1),
                    "piercing": round(max((e.get("ap_attack", 0.0) for e in eq), default=0.0), 1),
                    "air_attack": round(stat("air_attack"), 2),
                    # division designer: base stats of the sub-unit itself
                    "recovery": num("default_morale"),        # HOI4 recovery rate
                    "recon": num("recon"),
                    "suppression": num("suppression"),
                    "weight": num("weight"),
                    "supply_use": num("supply_consumption"),
                    "entrenchment": num("entrenchment"),
                    "initiative": num("initiative"),
                    "reliability": round(sum(rel) / len(rel), 3) if rel else 0.0,  # average of its equipment's
                    "sprite": get(u, "sprite", name),         # GFX_unit_<sprite>_icon_medium
                    "active": get(u, "active", "yes") != "no",  # usable without a technology
                    "land": get(u, "group") is not None,         # ships, planes and missiles have no group
                    # where a support company goes: the divisional support column (divisional = no keeps it out) and/or
                    # under a brigade as regimental support (regimental = no keeps it out) of the battalion groups in
                    # allowed_battalion_groups; affects_speed = no leaves it out of the division's max speed
                    "divisional": get(u, "group") == "support" and get(u, "divisional") != "no",
                    "regimental": get(u, "group") == "support" and get(u, "regimental") != "no"
                                  and isinstance(get(u, "allowed_battalion_groups"), list),
                    "battalion_groups": [v for _, _, v in get(u, "allowed_battalion_groups")]
                                        if isinstance(get(u, "allowed_battalion_groups"), list) else [],
                    "affects_speed": get(u, "affects_speed") != "no",
                    # terrain modifiers: `forest = { attack movement }`, `urban = { defence }`... and river (attacks
                    # across one) and amphibious (over a strait or from the sea); only the non-zero ones
                    "terrain": terrain_modifiers(u),
                    "need": need,  # equipment archetype -> amount (HOI4 `need`)
                }
    return subs


TERRAIN_MODIFIER_STATS = ("attack", "defence", "movement")


def terrain_modifiers(unit_items):
    """{terrain: {attack, defence, movement}} of a sub-unit's terrain blocks (any block holding only those stats:
    forest, hills, mountain, jungle, marsh, plains, urban, desert, river, amphibious, fort)."""
    mods = {}
    for k, _, v in unit_items:
        if not isinstance(v, list) or not v or any(kk not in TERRAIN_MODIFIER_STATS for kk, _, _ in v):
            continue
        stats = {}
        for kk, _, vv in v:
            try:
                f = float(vv)
            except (TypeError, ValueError):
                continue
            if f:
                stats[kk] = f
        if stats:
            mods[k] = stats
    return mods


def load_equipment_info(hoi4: Path):
    """{equipment name: {"archetype", "cost"}} for every equipment in common/units/equipment: its archetype (itself
    for an archetype) and build_cost_ic, inherited from `parent`, then the archetype, when it has none."""
    entries = {}
    for f in sorted((hoi4 / "common" / "units" / "equipment").glob("*.txt")):
        for _, _, block in parse_pdx(read_text(f)):
            for name, _, eq in (block if isinstance(block, list) else []):
                if not isinstance(eq, list):
                    continue
                try:
                    cost = float(get(eq, "build_cost_ic"))
                except (TypeError, ValueError):
                    cost = None
                try:
                    year = int(float(get(eq, "year", 0) or 0))
                except (TypeError, ValueError):
                    year = 0
                entries[name] = {"archetype": name if get(eq, "is_archetype") == "yes" else get(eq, "archetype"),
                                 "parent": get(eq, "parent"), "cost": cost, "year": year, "picture": get(eq, "picture"),
                                 "buildable": get(eq, "is_buildable") != "no" and get(eq, "is_archetype") != "yes"}

    def cost_of(name, depth=0):
        e = entries.get(name)
        if e is None or depth > 8:
            return None
        if e["cost"] is not None:
            return e["cost"]
        for link in (e["parent"], e["archetype"]):
            if link and link != name:
                c = cost_of(link, depth + 1)
                if c is not None:
                    return c
        return None

    def picture_of(name, depth=0):
        e = entries.get(name)
        if e is None or depth > 8:
            return None
        if e["picture"]:
            return e["picture"]
        for link in (e["parent"], e["archetype"]):
            if link and link != name:
                pic = picture_of(link, depth + 1)
                if pic:
                    return pic
        return None

    return {n: {"archetype": e["archetype"], "cost": cost_of(n), "year": e["year"], "buildable": e["buildable"],
                "picture": picture_of(n)}
            for n, e in entries.items() if e["archetype"]}


def describe_equipment(tag, buildable, equipment, tech_equipment, loc, sprite_names):
    """Adds what the production screen shows to each of a country's buildable equipment (in place): `name` (the
    country's design name, FRA_infantry_equipment_1 "MAS-36", else the generic one), `general` (infantry_equipment_1
    "Infantry Equipment I"), `archetype_name` and `sprite`, the picture HOI4 uses: the technology that unlocks it
    (GFX_FRA_infantry_weapons1_medium, then GFX_infantry_weapons1_medium), then the equipment, then its `picture`."""
    tech_of = {}
    for tech, es in sorted(tech_equipment.items()):
        for e in es:
            tech_of.setdefault(e, tech)
    for arch, b in buildable.items():
        eq = b["equipment"]
        general = loc.get(eq) or loc.get(arch) or eq
        b["name"] = loc.get(f"{tag}_{eq}") or general
        b["general"] = general
        b["archetype_name"] = loc.get(arch) or arch
        techs = [tech_of.get(eq), tech_of.get(eq.replace("_equipment_", "_chassis_"))]
        names = []
        for tech in [x for x in techs if x]:
            for v in (tech, tech.replace("_chassis", "")):
                names += [f"GFX_{tag}_{v}_medium", f"GFX_{v}_medium"]
        names += [f"GFX_{tag}_{eq}_medium", f"GFX_{eq}_medium"]
        pic = (equipment.get(eq) or {}).get("picture")
        if pic:
            names += [f"GFX_{tag}_{pic}_medium", f"GFX_{pic}_medium"]
        names.append(f"GFX_archetype_{arch}_medium")
        b["sprite"] = next((n for n in names if n in sprite_names), None)


def load_tech_equipment(hoi4: Path):
    """{technology: [equipment it enables]} from common/technologies (enable_equipments)."""
    techs = {}
    for f in sorted((hoi4 / "common" / "technologies").glob("*.txt")):
        for key, _, block in parse_pdx(read_text(f)):
            if key != "technologies" or not isinstance(block, list):
                continue
            for name, _, t in block:
                if isinstance(t, list):
                    en = get(t, "enable_equipments")
                    if isinstance(en, list):
                        techs[name] = values(en)
    return techs


def load_tech_subunits(hoi4: Path):
    """{technology: [sub-units it enables]} from common/technologies (enable_subunits; DLC-limited ones too)."""
    techs = {}
    for f in sorted((hoi4 / "common" / "technologies").glob("*.txt")):
        for key, _, block in parse_pdx(read_text(f)):
            if key != "technologies" or not isinstance(block, list):
                continue
            for name, _, t in block:
                if not isinstance(t, list):
                    continue
                for k, _, en in t:
                    if k == "enable_subunits" and isinstance(en, list):
                        techs.setdefault(name, []).extend(v for kk, _, v in en if kk is None or kk not in ("limit",))
                        techs[name].extend(kk for kk, _, v in en if kk not in (None, "limit") and v is None)
    for name, subs in techs.items():
        techs[name] = sorted({s for s in subs if isinstance(s, str)})
    return techs


# sub-units the division designer never offers (army HQs, intelligence decoys)
DESIGNER_EXCLUDED = ("fake_intel_unit",)


def designer_sub_units(subs):
    """Land sub-units a division template can use: battalions and support companies, not HQs."""
    return {n for n, s in subs.items() if s["land"] and not n.startswith("hq_") and n not in DESIGNER_EXCLUDED}


def known_technologies(country_root):
    """Technologies a country's history sets at the start."""
    known = set()
    for block in collect_blocks(country_root, "set_technology", []):
        for k, _, v in block:
            if isinstance(k, str) and str(v) != "0":
                known.add(k)
    return known


def available_equipment(country_root, equipment, tech_equipment, land_archetypes):
    """What a country can build at the start, per land archetype: the newest buildable equipment that its history's
    set_technology unlocks, or that no technology unlocks (always available). {archetype: {"equipment", "cost"}}.
    Tanks: a bare chassis is not buildable here (the 1936 lines build designs, which cost the modules too), and a
    design (light_tank_equipment_N) needs the technology of its chassis (light_tank_chassis_N)."""
    known = known_technologies(country_root)
    locked = {e for es in tech_equipment.values() for e in es}
    unlocked = {e for t in known for e in tech_equipment.get(t, [])}
    best = {}
    for name, info in sorted(equipment.items()):
        arch = info["archetype"]
        if arch not in land_archetypes or not info["buildable"] or not info["cost"]:
            continue
        if "chassis" in name or (name in locked and name not in unlocked):
            continue
        chassis = name.replace("_equipment_", "_chassis_")
        if chassis != name and chassis in locked and chassis not in unlocked:
            continue
        rank = (info["year"], info["cost"])
        if arch not in best or rank > best[arch][0]:
            best[arch] = (rank, {"equipment": name, "cost": info["cost"]})
    return {a: b for a, (_, b) in best.items()}


def collect_blocks(items, key, out):
    """Every block called KEY at any depth (inside if blocks and the like). Of an if / else_if / else chain only
    the if branch counts: HOI4 runs one of them, and the history files repeat the same lines in each (DLC variants).
    Date blocks after the 1936 start (1939.1.1 = { ... }, the later bookmark) are skipped."""
    for k, _, v in (items if isinstance(items, list) else []):
        if isinstance(k, str) and k.lower() in ("else", "else_if"):
            continue
        if isinstance(k, str) and re.fullmatch(r"\d+\.\d+\.\d+", k) and \
                tuple(int(x) for x in k.split(".")) > (int(OOB_START), 1, 1):
            continue
        if k == key and isinstance(v, list):
            out.append(v)
        elif isinstance(v, list):
            collect_blocks(v, key, out)
    return out


def starting_equipment(hoi4: Path, oob_root, country_root, equipment, land_archetypes):
    """A country's 1936 production lines (add_equipment_production in its OOB's instant_effect) and starting stockpile
    (add_equipment_to_stockpile in its history), land equipment only. Lines: equipment, archetype, factories,
    efficiency (0..1), cost. Stockpile: archetype -> amount; the history often lists the same stock twice, in a DLC
    and a no-DLC branch, so per archetype the largest amount counts rather than the sum."""
    lines = []
    for p in collect_blocks(oob_root, "add_equipment_production", []):
        eq = get(p, "equipment")
        kind = get(eq, "type") if isinstance(eq, list) else None
        info = equipment.get(kind or "")
        if not info or info["archetype"] not in land_archetypes or not info["cost"]:
            continue
        try:
            factories = int(float(get(p, "requested_factories", 1)))
            efficiency = float(get(p, "efficiency", 10)) / 100
        except (TypeError, ValueError):
            continue
        lines.append({"equipment": kind, "archetype": info["archetype"], "factories": factories,
                      "efficiency": efficiency, "cost": info["cost"]})
    stock = {}
    for s in collect_blocks(country_root, "add_equipment_to_stockpile", []):
        info = equipment.get(get(s, "type") or "")
        if not info or info["archetype"] not in land_archetypes:
            continue
        try:
            amount = int(float(get(s, "amount", 0)))
        except (TypeError, ValueError):
            continue
        stock[info["archetype"]] = max(stock.get(info["archetype"], 0), amount)
    return lines, stock


def convert_oob(hoi4: Path, out: Path, existing_tags, ruling_party):
    """HOI4 1936 land orders of battle -> history/units/oob_1936.json5 (GAME data: templates,
    divisions with resolved names, regiment stats) plus one engine unit type per division main
    regiment type (units/<type>.json5, the format the StarterKit unit system loads).

    Uses each country's base-game land OOB (the no-DLC branch; DLC variants only add officers
    and equipment variants). Naval and air OOBs are not converted. Countries that own no land
    at the start (releasables) are skipped."""
    names = load_division_name_groups(hoi4)
    subs = load_sub_units(hoi4)
    import hoi4_gui as gui
    equipment = load_equipment_info(hoi4)
    tech_equipment = load_tech_equipment(hoi4)
    tech_subunits = load_tech_subunits(hoi4)
    tech_gated = {s for v in tech_subunits.values() for s in v}
    designer_subs = designer_sub_units(subs)
    loc = load_localisation(hoi4 / "localisation" / "english")
    def has_texture(s):
        tex = s.get("texture")
        if not tex:
            return False
        f = hoi4 / tex
        return any(f.with_suffix(ext).exists() for ext in (f.suffix, ".dds", ".tga", ".png"))
    sprite_names = {n for n, s in gui.load_sprites(hoi4).items() if has_texture(s)}  # pictures that exist
    land_archetypes = {a for n in designer_subs for a in subs[n]["need"]}
    countries = {}
    used_subs = set()
    main_types = set()
    for f in sorted((hoi4 / "history" / "countries").glob("*.txt")):
        tag = f.name[:3]
        if tag not in existing_tags:
            continue
        found = []
        country_root = parse_pdx(read_text(f))
        collect_oob_names(country_root, found)
        base = [o for o, dlc in found if not dlc and o.startswith(f"{tag}_{OOB_START}")]
        base += [o for o, _ in found if o == f"{tag}_{OOB_START}"]  # fallback: plain TAG_1936
        path = hoi4 / "history" / "units" / f"{base[0]}.txt" if base else None
        if path is None or not path.exists():
            continue
        root = parse_pdx(read_text(path))

        templates = {}
        def slots(block):
            """[(sub-unit, x, y)] of a template's regiments / support block, in file order."""
            out = []
            for k, _, v in (block or []):
                if k in subs:
                    out.append((k, int(float(get(v, "x", 0) or 0)), int(float(get(v, "y", 0) or 0))))
            return out

        for t in get_all(root, "division_template"):
            reg_slots = slots(get(t, "regiments"))
            sup_slots = slots(get(t, "support"))
            rs_slots = slots(get(t, "regimental_support"))  # one company under a brigade: x is its column
            regs = [k for k, _, _ in reg_slots]
            sups = [k for k, _, _ in sup_slots]
            rsups = [k for k, _, _ in rs_slots]
            if not regs:
                continue
            # HOI4 shows a division by its highest-priority regiment
            main = max(regs, key=lambda r: subs[r]["priority"])
            # priority: reinforcement priority of the template, 0 reserve, 1 normal (default), 2 elite
            templates[get(t, "name")] = {"regiments": regs, "support": sups, "main": main,
                                         # division designer grid: [column, row] per regiment, row per support
                                         "grid": [[x, y] for _, x, y in reg_slots],
                                         "support_grid": [y for _, _, y in sup_slots],
                                         "regimental_support": rsups, "regimental_grid": [x for _, x, _ in rs_slots],
                                         "priority": int(get(t, "priority", 1) or 0),
                                         "names_group": get(t, "division_names_group")}

        # new divisions are named from the template's names group: its fallback format, numbered after the
        # highest number the 1936 divisions of the template use
        for t in templates.values():
            fallback, _ = names.get(t["names_group"], (None, {}))
            t["name_format"] = fallback
            t["next_number"] = 1

        divisions = []
        counters = {}
        for units in get_all(root, "units"):
            for d in get_all(units, "division"):
                tname = get(d, "division_template")
                t = templates.get(tname)
                if t is None:
                    continue
                name = get(d, "name")
                dn = get(d, "division_name")
                if not name and isinstance(dn, list):
                    n = int(get(dn, "name_order", 0) or 0)
                    fallback, ordered = names.get(t["names_group"], (None, {}))
                    fmt = ordered.get(n) or fallback
                    name = format_division_name(fmt, n) if fmt and n else None
                    t["next_number"] = max(t["next_number"], n + 1)
                if not name:
                    counters[tname] = counters.get(tname, 0) + 1
                    name = f"{tname} {counters[tname]}"
                divisions.append({
                    "name": name,
                    "template": tname,
                    "location": int(get(d, "location", 0)),
                    "experience": float(get(d, "start_experience_factor", 0) or 0),
                    "equipment": float(get(d, "start_equipment_factor", 1) or 1),
                })
                used_subs.update(t["regiments"] + t["support"] + t["regimental_support"])
                main_types.add(t["main"])
        if divisions:
            lines_, stock = starting_equipment(hoi4, root, country_root, equipment, land_archetypes)
            buildable = available_equipment(country_root, equipment, tech_equipment, land_archetypes)
            for p in lines_:  # what a 1936 line builds is buildable, whatever the technology check says
                buildable[p["archetype"]] = {"equipment": p["equipment"], "cost": p["cost"]}
            describe_equipment(tag, buildable, equipment, tech_equipment, loc, sprite_names)
            known = known_technologies(country_root)
            unlocked = {s for tech in known for s in tech_subunits.get(tech, [])}
            # a technology unlocks it, or no technology gates it and it is active (active alone doesn't do:
            # mechanized is active and still needs its technology)
            # and it has its equipment: every archetype it needs is buildable or in the stockpile (rocket artillery
            # and anti-air are active and ungated, their equipment is not)
            has_equipment = set(buildable) | set(stock)
            usable = sorted(n for n in designer_subs
                            if (n in unlocked or (subs[n]["active"] and n not in tech_gated))
                            and all(a in has_equipment for a in subs[n]["need"]))
            countries[tag] = {"ideology": ruling_party.get(tag) or "neutrality",
                              "sub_units": usable,  # what its division designer offers at the start
                              "equipment": buildable,
                              "templates": {k: {kk: vv for kk, vv in v.items() if kk != "names_group"}
                                            for k, v in templates.items()},
                              "divisions": divisions, "production": lines_, "stockpile": stock}

    # Only land equipment (sub-units also cover ships and planes): what the designer's sub-units use
    land_archetypes = {a for name in designer_subs for a in subs[name]["need"]}
    for c in countries.values():
        c["production"] = [p for p in c["production"] if p["archetype"] in land_archetypes]
        c["stockpile"] = {a: n for a, n in c["stockpile"].items() if a in land_archetypes}
        c["equipment"] = {a: e for a, e in sorted(c["equipment"].items()) if a in land_archetypes}

    def j(v):
        return json.dumps(v, ensure_ascii=False)

    lines = ["{", f"  // Generated from HOI4 history/units ({OOB_START} land OOBs) and common/units",
             f'  date: "{OOB_START}.1.1",', "  sub_units: {"]
    for name in sorted(used_subs | designer_subs):
        subs[name]["name"] = loc.get(name) or name.replace("_", " ").capitalize()  # display name (localisation)
        lines.append(f"    {name}: {j(subs[name])},")
    lines += ["  },"]
    # sub-unit groups (the division designer's battalion categories): group -> name (group_<g>_title)
    groups = sorted({subs[n]["group"] for n in used_subs | designer_subs})
    lines.append(f"  sub_unit_groups: {j({g: loc.get(f'group_{g}_title') or g.replace('_', ' ').title() for g in groups})},")
    lines += ["  countries: {"]
    for tag, c in sorted(countries.items()):
        lines.append(f"    {tag}: {{")
        lines.append(f"      ideology: {j(c['ideology'])},")
        lines.append(f"      sub_units: {j(c['sub_units'])},")
        lines.append("      templates: {")
        for tname, t in c["templates"].items():
            lines.append(f"        {j(tname)}: {j(t)},")
        lines.append("      },")
        lines.append("      divisions: [")
        for d in c["divisions"]:
            lines.append(f"        {j(d)},")
        lines.append("      ],")
        # 1936 production lines (land equipment) and starting stockpile, archetype -> amount
        lines.append("      production: [")
        for p in c["production"]:
            lines.append(f"        {j(p)},")
        lines.append("      ],")
        lines.append(f"      stockpile: {j(c['stockpile'])},")
        # what it can build at the start: archetype -> newest unlocked equipment and its build cost
        lines.append(f"      equipment: {j(c['equipment'])},")
        lines.append("    },")
    lines += ["  }", "}", ""]
    (out / "history" / "units").mkdir(parents=True, exist_ok=True)
    (out / "history" / "units" / f"oob_{OOB_START}.json5").write_text("\n".join(lines), encoding="utf-8")

    # Engine unit types (StarterKit format), one per division main type. speed = days per province. Every battalion
    # type counts: a template made in the division designer can have any of them as its main type.
    main_types |= {n for n in designer_subs if subs[n]["group"] != "support"}
    (out / "units").mkdir(parents=True, exist_ok=True)
    for f in (out / "units").glob("*.json5"):
        f.unlink()
    for name in sorted(main_types):
        u = subs[name]
        days = max(1, round(12.0 / max(u["speed"], 1.0)))
        text = (
            "{\n  // Generated from HOI4 common/units: a division's main regiment type\n"
            f'  id: "{name}",\n  name: {j(name.replace("_", " ").title())},\n  category: "land",\n  version: 1,\n'
            "  cost: { gold: 20 },\n  maintenance: { gold: 1 },\n"
            f"  stats: {{ speed: {days} }},\n"
            '  visual: { type: "billboard", icon: "Icons/Infantry", color_source: "country" },\n'
            f"  hoi4: {{ icon: {j(u['icon'])}, group: {j(u['group'])}, abbreviation: {j(u['abbreviation'])} }}\n}}\n")
        (out / "units" / f"{name}.json5").write_text(text, encoding="utf-8")
    total = sum(len(c["divisions"]) for c in countries.values())
    print(f"Equipment: {sum(len(c['production']) for c in countries.values())} land production lines, "
          f"{sum(1 for c in countries.values() if c['stockpile'])} countries with a starting stockpile, "
          f"archetypes needed {sorted(land_archetypes)}")
    print(f"Armies {OOB_START}: {total} divisions in {len(countries)} countries, "
          f"{sum(len(c['templates']) for c in countries.values())} templates, unit types {sorted(main_types)}")
    return countries, main_types, {n: subs[n] for n in used_subs | designer_subs}


COUNTER_IDEOLOGIES = ["democratic", "communism", "fascism", "neutrality"]  # frame order of the ideology sprite


def convert_front_line(hoi4: Path, out: Path):
    """The battle plan front line (gfx/maparrows arrowType defence_line): mask and pattern as PNGs
    (front_mask.png / front_pattern.png). The band is the top row (own side), the teeth point at the enemy."""
    src = hoi4 / "gfx" / "maparrows"
    dst = out / "gfx" / "maparrows"
    dst.mkdir(parents=True, exist_ok=True)
    Image.open(src / "defensive_line_mask.dds").convert("RGBA").save(dst / "front_mask.png")
    Image.open(src / "defensive_line.dds").convert("RGBA").save(dst / "front_pattern.png")
    # defence_line's alternative mask: the line of a front nobody holds (separate teeth, no band)
    Image.open(src / "defensive_line_unassigned_mask.dds").convert("RGBA").save(dst / "front_unassigned_mask.png")
    # Unconfirmed line while drawing (arrowType magnet_line): the green dashes
    Image.open(src / "stripe_mask.dds").convert("RGBA").save(dst / "preview_mask.png")
    Image.open(src / "stripe_pattern.dds").convert("RGBA").save(dst / "preview_pattern.png")
    # Offensive plan objective line (arrowType offence_line): band in the bottom rows, a tooth pointing up
    Image.open(src / "offensive_line_mask.dds").convert("RGBA").save(dst / "offence_mask.png")
    Image.open(src / "offensive_line.dds").convert("RGBA").save(dst / "offence_pattern.png")
    # offence_line's alternative mask: an offensive nobody is assigned to (hollow teeth)
    Image.open(src / "offensive_line_unassigned_mask.dds").convert("RGBA").save(dst / "offence_unassigned_mask.png")
    # Dragging an offensive: the dotted line through the picked provinces (arrowType virtual_line)
    Image.open(src / "dot_mask.dds").convert("RGBA").save(dst / "dot_mask.png")
    Image.open(src / "dot_pattern.dds").convert("RGBA").save(dst / "dot_pattern.png")
    print("Front line textures")


def convert_move_arrow(hoi4: Path, out: Path, arrow_name="move_thick"):
    """HOI4's unit move arrow (gfx/maparrows/maparrows.txt arrowType move_thick): mask and pattern
    textures as PNGs under gfx/maparrows/ plus its shape parameters in move_arrow.json5.
    Texture layout: left half = body (tiles along the arrow, uses the middle body_uv_scale of the
    height), right half = head; mask rgb*a = coverage, pattern = grayscale colour detail."""
    src = hoi4 / "gfx" / "maparrows"
    root = parse_pdx(read_text(src / "maparrows.txt"))
    arrow = next((a for a in get_all(root, "arrowType") if get(a, "name") == arrow_name), None)
    if arrow is None:
        print(f"  map arrow {arrow_name} not found")
        return
    dst = out / "gfx" / "maparrows"
    dst.mkdir(parents=True, exist_ok=True)
    mask = values(get(arrow, "texture_masks"))[0]
    pattern = values(get(arrow, "texture_patterns"))[0]
    Image.open(hoi4 / mask).convert("RGBA").save(dst / "move_mask.png")
    masks = values(get(arrow, "texture_masks"))
    if len(masks) > 1:  # the alternative mask: an offensive arrow nobody is assigned to (hollow, striped)
        Image.open(hoi4 / masks[1]).convert("RGBA").save(dst / "move_unassigned_mask.png")
    Image.open(hoi4 / pattern).convert("RGBA").save(dst / "move_pattern.png")

    def num(key, default):
        try:
            return float(get(arrow, key, default))
        except (TypeError, ValueError):
            return default
    body = [float(v) for v in values(get(arrow, "body_width"))] or [20.5, 6.0]
    params = {
        "body_width": body,                         # map pixels at the root and at the head
        "head_length": num("head_length", 19.5),    # map pixels
        "body_uv_scale": num("body_uv_scale", 0.4),
        "fade_in": num("fade_in", 0.2),             # fraction of the length
        "global_scale_min_size": num("global_scale_min_size", 0.1),
        "global_scale_max_points": num("global_scale_max_points", 40),
        "texture_anim_speed": num("texture_anim_speed", 0.1),
    }
    (dst / "move_arrow.json5").write_text(
        f"// Generated from HOI4 gfx/maparrows/maparrows.txt arrowType {arrow_name}\n{json.dumps(params, indent=2)}\n",
        encoding="utf-8")
    print(f"Move arrow: {arrow_name} {params['body_width']} head {params['head_length']}")


# Modifiers the economy uses (consumer goods and construction speed), from ideas (`modifier = {}`) and technologies
def load_idea_modifiers(hoi4: Path):
    """{idea: {"category", "law", "default", "mods"}} from common/ideas (every numeric modifier in mods)."""
    ideas = {}
    for f in sorted((hoi4 / "common" / "ideas").glob("*.txt")):
        root = get(parse_pdx(read_text(f)), "ideas", [])
        for cat, _, body in root if isinstance(root, list) else []:
            if not isinstance(body, list):
                continue
            law = get(body, "law") == "yes"
            for name, _, idea in body:
                if not isinstance(idea, list) or name is None:
                    continue
                mods = {}
                m = get(idea, "modifier")
                for k, _, v in (m if isinstance(m, list) else []):
                    if isinstance(k, str):
                        try:
                            mods[k] = mods.get(k, 0.0) + float(v)
                        except (TypeError, ValueError):
                            pass
                ideas[name] = {"category": cat, "law": law, "default": get(idea, "default") == "yes", "mods": mods}
    return ideas


def history_values(items, key, out):
    """Every value of KEY (a single value or a block's values) at any depth, skipping else branches and date
    blocks after the start, like collect_blocks, and branches for a missing DLC (every DLC is assumed owned:
    `if = { limit = { NOT = { has_dlc = ... } } }` would add a no-DLC idea beside its DLC twin)."""
    for k, _, v in (items if isinstance(items, list) else []):
        if isinstance(k, str) and k.lower() in ("else", "else_if"):
            continue
        if isinstance(k, str) and k.lower() == "if" and isinstance(v, list) and has_not_dlc(get(v, "limit")):
            continue
        if isinstance(k, str) and re.fullmatch(r"\d+\.\d+\.\d+", k) and \
                tuple(int(x) for x in k.split(".")) > (int(OOB_START), 1, 1):
            continue
        if k == key:
            out.extend(values(v) if isinstance(v, list) else [v])
        elif isinstance(v, list):
            history_values(v, key, out)
    return out


def numeric_modifiers(block):
    """{key: value} of a modifier block's numeric entries."""
    mods = {}
    for k, _, v in (block if isinstance(block, list) else []):
        if isinstance(k, str):
            try:
                mods[k] = mods.get(k, 0.0) + float(v)
            except (TypeError, ValueError):
                pass
    return mods


STATIC_MODIFIERS = ("stability_good_modifier", "stability_bad_modifier", "war_support_during_war")


def write_stability(hoi4: Path, out: Path):
    """common/hoi4_static_modifiers.json5: the static modifiers stability scales (common/modifiers
    00_static_modifiers: stability_good_modifier above 50%, stability_bad_modifier below), every numeric key.
    common/hoi4_landmarks.json5: the 1936 landmarks (history/states province buildings named in
    common/buildings/01_landmark_buildings, every DLC assumed owned): province, building, the controllers their
    country modifiers go to (enable_for_controllers; empty: any controller) and those modifiers (Germany's
    Reichstag: +5% stability, which its set_stability leaves out)."""
    root = parse_pdx(read_text(hoi4 / "common" / "modifiers" / "00_static_modifiers.txt"))
    static = {n: numeric_modifiers(get(root, n, [])) for n in STATIC_MODIFIERS}
    (out / "common" / "hoi4_static_modifiers.json5").write_text(
        "// Generated by hoi4_convert.py from common/modifiers/00_static_modifiers.txt\n"
        + json.dumps(static, indent=1) + "\n", encoding="utf-8")

    defs = {}
    for f in sorted((hoi4 / "common" / "buildings").glob("*.txt")):
        for name, _, body in get(parse_pdx(read_text(f)), "buildings", []) or []:
            cm = get(body, "country_modifiers") if isinstance(body, list) else None
            if not isinstance(name, str) or not name.startswith("landmark_") or not isinstance(cm, list):
                continue
            ctrl = get(cm, "enable_for_controllers", [])
            defs[name] = {"controllers": sorted(values(ctrl)) if isinstance(ctrl, list) else [],
                          "modifiers": numeric_modifiers(get(cm, "modifiers", []))}
    landmarks = []
    for f in sorted((hoi4 / "history" / "states").glob("*.txt")):
        st = get(parse_pdx(read_text(f)), "state")
        hist = get(st, "history", []) if isinstance(st, list) else []
        b = get(hist, "buildings", []) if isinstance(hist, list) else []
        for prov, _, pb in (b if isinstance(b, list) else []):
            if not (isinstance(prov, str) and prov.isdigit() and isinstance(pb, list)):
                continue
            for name, _, lb in pb:
                if name in defs and defs[name]["modifiers"]:
                    landmarks.append({"building": name, "province": int(prov), **defs[name]})
    (out / "common" / "hoi4_landmarks.json5").write_text(
        "// Generated by hoi4_convert.py from history/states and common/buildings (landmarks)\n"
        + json.dumps(sorted(landmarks, key=lambda x: (x["province"], x["building"])), indent=1) + "\n",
        encoding="utf-8")
    print(f"Stability: {len(static)} static modifiers, {len(landmarks)} landmarks with country modifiers")


def write_country_economy(hoi4: Path, out: Path, tags):
    """common/hoi4_country_economy.json5: per country its research slots (set_research_slots, else
    BASE_RESEARCH_SLOTS), its laws and its starting ideas (laws it doesn't set take the category's default law).
    common/hoi4_ideas.json5: those ideas and every law, with their category and modifiers (every numeric key).
    The game adds the ideas' and the known technologies' modifiers to the engine's modifier system (Hoi4Modifiers)
    and reads consumer goods, construction and research speed from it."""
    ideas = load_idea_modifiers(hoi4)
    defaults = {}
    for name, i in sorted(ideas.items()):
        # the generic default law of each law category, not a country's own law (ETH_chitet_law)
        if i["law"] and i["default"] and not re.match(r"[A-Z]{3}_", name):
            defaults.setdefault(i["category"], name)
    lines = ["// Generated by hoi4_convert.py from history/countries and common/ideas", "{"]
    used = set()
    for tag in sorted(tags):
        files = sorted((hoi4 / "history" / "countries").glob(f"{tag} - *.txt"))
        if not files:
            continue
        root = parse_pdx(read_text(files[0]))
        own = [i for i in history_values(root, "add_ideas", []) if isinstance(i, str) and i in ideas]
        cats = {ideas[i]["category"] for i in own if ideas[i]["law"]}
        own += [law for cat, law in defaults.items() if cat not in cats]
        # research slots: the start's set_research_slots, else NCountry BASE_RESEARCH_SLOTS (2)
        slots = [v for v in history_values(root, "set_research_slots", []) if str(v).isdigit()]
        e = {"research_slots": int(slots[-1]) if slots else BASE_RESEARCH_SLOTS,
             "laws": sorted(i for i in own if ideas[i]["law"]),
             "ideas": sorted(set(own))}
        used.update(own)
        lines.append(f"  {tag}: {json.dumps(e)},")
    lines.append("}")
    (out / "common" / "hoi4_country_economy.json5").write_text("\n".join(lines) + "\n", encoding="utf-8")
    # the ideas countries start with, and every law (a country may change law)
    kept = {n: {"category": i["category"], "law": i["law"],
                "modifiers": {k: round(v, 6) for k, v in sorted(i["mods"].items())}}
            for n, i in sorted(ideas.items()) if n in used or i["law"]}
    (out / "common" / "hoi4_ideas.json5").write_text(
        "// Generated by hoi4_convert.py from common/ideas: the ideas countries start with and every law\n"
        + json.dumps(kept, indent=1) + "\n", encoding="utf-8")
    print(f"Country economy: {len(lines) - 3} countries, {len(kept)} ideas and laws")


# Keys of a technology block that are not modifiers (scalars) or unit bonuses (blocks)
TECH_STRUCTURE = {
    "research_cost", "start_year", "folder", "path", "categories", "ai_will_do", "allow", "allow_branch",
    "enable_equipments", "enable_equipment_modules", "enable_subunits", "enable_building", "on_research_complete",
    "sub_technologies", "dependencies", "xor", "xp_research_type", "xp_unlock_cost", "xp_boost_cost",
    "xp_research_bonus", "doctrine", "doctrine_name", "show_effect_as_desc", "desc", "force_use_small_tech_layout",
    "is_special_project_tech", "special_project_specialization", "ai_research_weights", "show_equipment_icon",
    "enable_tactic", "on_research_complete_limit", "sub_technology", "potential", "visible",
}


def loc_refs(text, loc):
    """Localisation text with its $key$ references resolved ("$gw_armored_car_equipment$" -> "Armored Car I")."""
    return re.sub(r"\$(\w+)\$", lambda m: loc.get(m.group(1), m.group(1)), text or "")


def has_not_dlc(block):
    """Whether a trigger block asks for a DLC to be missing (NOT = { has_dlc = ... })."""
    for k, _, v in block if isinstance(block, list) else []:
        if isinstance(k, str) and k.lower() == "not" and isinstance(v, list) and any(
                isinstance(kk, str) and kk.lower() == "has_dlc" for kk, _, _ in v):
            return True
        if isinstance(v, list) and has_not_dlc(v):
            return True
    return False


def pdx_json(items):
    """A parsed trigger block as JSON: [[key, op, value or nested list], ...] (keys lower case)."""
    out = []
    for k, op, v in items if isinstance(items, list) else []:
        if isinstance(v, tuple):
            continue
        out.append([k.lower() if isinstance(k, str) else None, op, pdx_json(v) if isinstance(v, list) else v])
    return out


def tech_ai(block):
    """A technology's ai_will_do: base (base or factor, 1 when missing) and the modifiers, each a factor or an add
    with its trigger (the modifier's other lines, ANDed). The GAME evaluates the triggers it knows."""
    if not isinstance(block, list):
        return None
    ai = {"base": 1.0, "modifiers": []}
    for k, _, v in block:
        if k in ("base", "factor") and not isinstance(v, list):
            try:
                ai["base"] = float(v)
            except ValueError:
                pass
        elif k == "modifier" and isinstance(v, list):
            m = {}
            for kk, _, vv in v:
                if kk in ("factor", "add") and not isinstance(vv, list):
                    try:
                        m[kk] = float(vv)
                    except ValueError:
                        pass
            if m:
                m["if"] = pdx_json([x for x in v if x[0] not in ("factor", "add")])
                ai["modifiers"].append(m)
    return ai


def research_strategies(hoi4: Path):
    """AI strategies (common/ai_strategy) that weight technologies: name, allowed / enable / abort triggers, the
    research_weight_factor per tech (value 100 = +100%) and research_tech (research it as soon as possible)."""
    out = []
    for f in sorted((hoi4 / "common" / "ai_strategy").glob("*.txt")):
        for name, _, s in parse_pdx(read_text(f)):
            if not isinstance(s, list):
                continue
            weights, force = {}, []
            for k, _, v in s:
                if k == "ai_strategy" and isinstance(v, list):
                    kind, tech = get(v, "type"), get(v, "id")
                    try:
                        value = float(get(v, "value", 0))
                    except ValueError:
                        continue
                    if kind == "research_weight_factor" and tech:
                        weights[tech] = value
                    elif kind == "research_tech" and tech and value > 0:
                        force.append(tech)
            if weights or force:
                out.append({"name": name, "allowed": pdx_json(get(s, "allowed") or []),
                            "enable": pdx_json(get(s, "enable") or []), "abort": pdx_json(get(s, "abort") or []),
                            "weights": weights, "force": force})
    return out


def write_technologies(hoi4: Path, out: Path, tags, loc):
    """common/hoi4_technologies.json5 for the GAME's research (Hoi4Research): every researchable technology of the
    tech tree folders (doctrines are their own system, common/doctrines, not here) and each country's 1936 ones.
    Assumes every DLC is owned (like the reference games): folders and branches only shown without a DLC are left out
    (armour_folder for nsb_armour_folder, naval_folder for the Man the Guns ones, air_techs_folder for By Blood Alone).
    Per technology: name (English), cost (research_cost; x BASE_TECH_COST 110 = research points), year (start_year,
    the ahead-of-time penalty counts from 1 January of it), folder and x/y (grid position), leads_to ([tech, cost
    coefficient]), requires (dependencies: every one needed), xor (excludes), categories, what it enables (equipment,
    sub-units, buildings with the level), modifiers (country-wide, e.g. research_speed_factor), bonuses (per sub-unit
    or category stat blocks), focus_only (allow = { always = no }: only a focus or event gives it) and ai (ai_will_do:
    base and modifiers with their triggers). ai_strategies: the AI strategies weighting technologies.
    Prototype: Tools/prototypes/research/research_cost.py (cost and speed checked against a real game)."""
    folders = {}
    tags_file = hoi4 / "common" / "technology_tags" / "00_technology.txt"
    for key, _, block in parse_pdx(read_text(tags_file)):
        if key != "technology_folders" or not isinstance(block, list):
            continue
        for name, _, f in block:
            if not isinstance(f, list) or str(get(f, "doctrine", "no")).lower() == "yes":
                continue
            if has_not_dlc(get(f, "available") or []):
                continue
            folders[name] = {"ledger": get(f, "ledger") or ""}

    techs = {}
    raw_parents = {}   # every path in the files, the techs left out included
    for f in sorted((hoi4 / "common" / "technologies").glob("*.txt")):
        for key, _, block in parse_pdx(read_text(f)):
            if key != "technologies" or not isinstance(block, list):
                continue
            consts = {k: v for k, _, v in block if isinstance(k, str) and k.startswith("@")}

            def num(v, default=0.0):
                v = consts.get(v, v) if isinstance(v, str) else v
                try:
                    return float(v)
                except (TypeError, ValueError):
                    return default
            for name, _, t in block:
                if not isinstance(t, list) or not isinstance(name, str) or name.startswith("@"):
                    continue
                for k, _, pth in t:
                    if k == "path" and isinstance(pth, list) and get(pth, "leads_to_tech"):
                        raw_parents.setdefault(get(pth, "leads_to_tech"), set()).add(name)
                # a tech may sit in several folders (motorised_infantry: infantry and support); the first one we
                # keep is its folder, every one we keep goes in places [[folder, x, y], ...]
                places = []
                for k, _, f in t:
                    if k == "folder" and isinstance(f, list) and get(f, "name") in folders:
                        pos = get(f, "position")
                        places.append([get(f, "name"), num(get(pos, "x")) if isinstance(pos, list) else 0.0,
                                       num(get(pos, "y")) if isinstance(pos, list) else 0.0])
                if not places or has_not_dlc(get(t, "allow_branch") or []):
                    continue
                tech = {"name": loc.get(name) or name, "cost": num(get(t, "research_cost")),
                        "year": int(num(get(t, "start_year"))), "folder": places[0][0],
                        "x": places[0][1], "y": places[0][2], "places": places,
                        "leads_to": [], "requires": [], "xor": [], "categories": [], "equipment": [], "subunits": [],
                        "buildings": {}, "modifiers": {}, "bonuses": {}}
                for k, _, v in t:
                    k = k.lower() if isinstance(k, str) else k   # the files write XOR and xor
                    if k == "path" and isinstance(v, list) and get(v, "leads_to_tech"):
                        tech["leads_to"].append([get(v, "leads_to_tech"), num(get(v, "research_cost_coeff"), 1.0)])
                    elif k == "dependencies" and isinstance(v, list):
                        tech["requires"] += [kk for kk, _, _ in v if isinstance(kk, str)]
                    elif k == "xor" and isinstance(v, list):
                        tech["xor"] += values(v)
                    elif k == "categories" and isinstance(v, list):
                        tech["categories"] += values(v)
                    elif k == "enable_equipments" and isinstance(v, list):
                        tech["equipment"] += values(v)
                    elif k == "enable_subunits" and isinstance(v, list):
                        tech["subunits"] += [x for x in values(v)]
                    elif k == "enable_building" and isinstance(v, list) and get(v, "building"):
                        tech["buildings"][get(v, "building")] = int(num(get(v, "level"), 1))
                    elif k == "ai_will_do":
                        tech["ai"] = tech_ai(v)
                    elif k == "allow" and isinstance(v, list) and str(get(v, "always", "")).lower() == "no":
                        tech["focus_only"] = True
                    elif isinstance(k, str) and k not in TECH_STRUCTURE:
                        if isinstance(v, list):
                            stats = {kk: num(vv) for kk, _, vv in v if isinstance(kk, str) and not isinstance(vv, list)}
                            if stats:
                                tech["bonuses"][k] = stats
                        else:
                            n = num(v, None)
                            if n is not None:
                                tech["modifiers"][k] = n
                # a tech that enables equipment or a sub-unit has no name of its own: HOI4 shows the equipment's
                if tech["name"] == name:
                    tech["name"] = next((loc[e] for e in tech["equipment"] + tech["subunits"] if e in loc), name)
                tech["name"] = loc_refs(tech["name"], loc)
                # the large tech-tree card shows the equipment's design name: the country's own (GER_infantry_equipment_0
                # "Gewehr 98") where it has one, else the generic one
                if tech["equipment"]:
                    e = tech["equipment"][0]

                    def short(key):   # the card shows the short name (GER_motorized_equipment_0_short "ALZ 13")
                        return loc.get(f"{key}_short") or loc.get(key)
                    tech["equipment_name"] = loc_refs(short(e) or tech["name"], loc)
                    own = {tag: loc_refs(short(f"{tag}_{e}"), loc) for tag in tags if short(f"{tag}_{e}")}
                    if own:
                        tech["equipment_names"] = own
                techs[name] = {k: v for k, v in tech.items() if v or k in ("cost", "year", "x", "y")}

    # a branch reached only through techs left out is hidden too (tech_special_forces, no-DLC only, is the one way to
    # advanced_special_forces and what follows it)
    while True:
        orphans = [n for n in techs if raw_parents.get(n) and not any(p in techs for p in raw_parents[n])]
        if not orphans:
            break
        for n in orphans:
            del techs[n]
    # paths into branches left out (the no-DLC variants) go nowhere here
    for t in techs.values():
        for k in ("leads_to", "requires", "xor"):
            if k in t:
                t[k] = [x for x in t[k] if (x[0] if isinstance(x, list) else x) in techs]
                if not t[k]:
                    del t[k]
    countries = {}
    for tag in sorted(tags):
        files = sorted((hoi4 / "history" / "countries").glob(f"{tag} - *.txt"))
        if files:
            countries[tag] = sorted(t for t in known_technologies(parse_pdx(read_text(files[0]))) if t in techs)
    strategies = [s for s in research_strategies(hoi4)
                  if any(t in techs for t in list(s["weights"]) + s["force"])]
    doc = {"folders": folders, "technologies": techs, "countries": countries, "ai_strategies": strategies}
    (out / "common" / "hoi4_technologies.json5").write_text(
        "// Generated by hoi4_convert.py from common/technologies, common/technology_tags and history/countries\n"
        + json.dumps(doc, indent=1, ensure_ascii=False) + "\n", encoding="utf-8")
    print(f"Technologies: {len(techs)} in {len(folders)} folders, {len(countries)} countries, "
          f"{len(strategies)} AI research strategies")


def write_country_stats(out: Path, tags, country_stats, owner_manpower, ruling_party=None):
    """common/hoi4_country_stats.json5: per country starting stability, war support (0..1),
    population of its owned core states and of its owned non-core states (sums of the states' `manpower`),
    and conscription (mobilisation law factor), for the GAME top bar and manpower pool. HOI4 recruitable
    manpower = (population + non-core population * LOCAL_MANPOWER_ACCESSIBLE_NON_CORE_FACTOR) * conscription.
    `ideology` is the ruling party's ideology group at the start (peace conference cost modifiers)."""
    lines = ["// Generated from HOI4 history/countries (set_stability, set_war_support, mobilisation law)",
             "// and history/states (state manpower = population, summed per owner; core states apart from non-core)", "{"]
    for tag in tags:
        st = country_stats.get(tag, {})
        lines.append(f"  {tag}: {{ stability: {st.get('stability', 0.5)}, war_support: {st.get('war_support', 0.5)}, "
                     f"population: {owner_manpower.get((tag, True), 0)}, "
                     f"noncore_population: {owner_manpower.get((tag, False), 0)}, "
                     f"conscription: {st.get('conscription', 0.015)}, "
                     f"ideology: \"{(ruling_party or {}).get(tag) or 'neutrality'}\" }},")
    lines.append("}")
    (out / "common" / "hoi4_country_stats.json5").write_text("\n".join(lines) + "\n", encoding="utf-8")


# Top bar elements we show (names from interface/topbar.gui); the rest needs systems we don't have.
TOPBAR_KEEP = [
    "background", "armyoverview_buttons_bg", "player_flag",
    "pol_power_value_bg", "pol_power_icon", "pol_power",
    "sability_value_bg", "stability_icon", "stability",
    "war_support_value_bg", "war_support_icon", "war_support",
    "manpower_value_bg", "manpower_icon", "manpower",
    "pause_button_date", "button_speeddown", "button_speedup",
    "button_speedstep1", "button_speedstep2", "button_speedstep3", "button_speedstep4", "button_speedstep5",
    "DateText", "menu_button", "help_button", "threat_button", "threat_value",
    "production_button", "production_button_glow", "construction_button", "deployment_button", "technology_button",
]
# Sprite overrides: the extended background has room for DLC menu buttons we don't show
TOPBAR_SPRITE = {"background": "GFX_topbar_background"}


def convert_topbar(hoi4: Path, out: Path, tag_ideology):
    """HOI4 top bar (interface/topbar.gui) for the GAME UI: the sprites as PNGs and the layout
    resolved to screen edges, under gfx/interface/topbar/ (topbar.json5), plus the shield flag
    (GFX_shield_medium) per country in flags/. Prototype: Tools/prototypes/topbar."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "topbar"
    (dst / "flags").mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    win = gui.window(hoi4, "topbar.gui", "topbar")
    # Lay out at two widths: elements that move with the width hang off the right screen edge
    a = gui.layout_window(win, 1920, 1080)
    b = gui.layout_window(win, 2560, 1080)

    def save(img, name):
        img.save(dst / name)
        return name, img.width, img.height

    elements = []
    for name in TOPBAR_KEEP:
        if name not in a:
            print(f"  topbar: {name} not in topbar.gui")
            continue
        kind, x, y, block = a[name]
        right = abs(b[name][1] - x) > 0.5
        e = {"name": name, "edge": "right" if right else "left",
             "x": round(x - 1920 if right else x, 1), "y": round(y, 1)}
        if kind in ("instantTextBoxType", "textBoxType"):
            e.update(kind="text", w=float(gui.kv(block, "maxWidth") or 0) or 60.0, h=float(gui.kv(block, "maxHeight") or 0),
                     format=(gui.kv(block, "format") or "left").lower(), font=gui.kv(block, "font") or "")
        else:
            spr = TOPBAR_SPRITE.get(name) or gui.kv(block, "spriteType") or gui.kv(block, "quadTextureSprite")
            e["kind"] = "button" if kind == "buttonType" else "image"
            if name == "player_flag":
                e.update(image="flags/{tag}.png", sprite=spr)
                w = h = 0
                for tag, ideology in tag_ideology.items():
                    img = gui.masked_flag(hoi4, sprites, spr, tag, ideology)
                    if img is not None:
                        img.save(dst / "flags" / f"{tag}.png")
                        w, h = img.size
                e.update(w=w, h=h)
            else:
                frames = gui.sprite_frames(hoi4, sprites, spr)
                if not frames:
                    print(f"  topbar: no sprite {spr} for {name}")
                    continue
                base = name.rstrip("12345") if name.startswith("button_speedstep") else name
                names = []
                for i, fr in enumerate(frames):
                    fn = f"{base}.png" if len(frames) == 1 else f"{base}_{i}.png"
                    if not (dst / fn).exists() or base == name:
                        fr.save(dst / fn)
                    names.append(fn)
                e.update(image=names[0], frames=names, w=frames[0].width, h=frames[0].height)
        elements.append(e)

    # Paused date pill: frame 1 of GFX_date_pause_button (striped, pause symbol)
    paused = gui.sprite_frames(hoi4, sprites, "GFX_date_pause_button")
    extra = {}
    if len(paused) > 1:
        paused[1].save(dst / "pause_button_date_paused.png")
        extra["paused_image"] = "pause_button_date_paused.png"

    doc = {"reference_width": 1920, "elements": elements, **extra}
    (dst / "topbar.json5").write_text("// Generated from HOI4 interface/topbar.gui by hoi4_convert.py\n"
                                     + json.dumps(doc, indent=1) + "\n", encoding="utf-8")
    print(f"Top bar: {len(elements)} elements, {len(list((dst / 'flags').glob('*.png')))} flags")


# Unit list panel (interface/unitview.gui army_division_list_view) sprites: name -> GFX sprite.
UNITPANEL_SPRITES = {
    "header": "GFX_unit_list_header", "btn_delete": "GFX_NV_btn_delete", "btn_close": "GFX_NV_btn_close",
    "btn_unassign": "GFX_UV_btn_unassign", "consolidate": "GFX_consolidate", "select_half": "GFX_select_half",
    "hold": "GFX_hold", "mini_bg": "GFX_generic_mini_bg", "convoys": "GFX_topbar_convoys",
    "entry_bg": "GFX_UV_unit_entry_bg", "unassigned_bg": "GFX_unitlist_unassigned_bg",
    "unitinfo": "GFX_unitlist_unitinfo_button", "level": "GFX_unit_level", "sort": "GFX_tiling_sort_button_thin",
    "column": "GFX_division_list_column_button", "close_small": "GFX_NV_btn_close",
    "dig_in": "GFX_unitlist_dig_in_icon",
    # disband confirmation (interface/popupwindow.gui default_confirmation_popup)
    "popup_bg": "GFX_tiled_window_pol_goal", "button_148": "GFX_button_148x34",
}


def unit_icon(hoi4: Path, sprites, sub_unit, sprite=None, suffix="_icon_medium"):
    """Frame 0 of a sub-unit's pictogram (GFX_unit_<sub_unit><suffix>, else GFX_unit_<sprite><suffix>), None when
    neither exists. HOI4 names most sub-units' own icon even when their `sprite` is a generic one."""
    import hoi4_gui as gui
    for cand in (sub_unit, sprite):
        if cand and f"GFX_unit_{cand}{suffix}" in sprites:
            frames = gui.sprite_frames(hoi4, sprites, f"GFX_unit_{cand}{suffix}")
            if frames:
                return frames[0]
    return None


def convert_unit_panel(hoi4: Path, out: Path, main_types):
    """Sprites of HOI4's selected-units list (gui layout is hardcoded in Hoi4DivisionPanel.cs) as PNGs
    under gfx/interface/unitpanel/: the window frame tile, buttons, entry background frames, bars
    and a medium pictogram per division main type (icons/)."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "unitpanel"
    (dst / "icons").mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    sizes = {}
    for name, spr in UNITPANEL_SPRITES.items():
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            print(f"  unit panel: no sprite {spr}")
            continue
        for i, fr in enumerate(frames):
            fr.save(dst / (f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"))
        sizes[name] = [frames[0].width, frames[0].height, len(frames)]
    # window frame: 192x192 tile, 64 px borders (GFX_tiled_window_1b_thin_border)
    tile = gui.load_image(hoi4, "gfx/interface/tiles/tiled_window_1b_thin_border.dds")
    tile.save(dst / "window.png")
    sizes["window"] = [tile.width, tile.height, 1]
    # progress bars: fill (textureFile1) and background (textureFile2)
    for name, spr in (("bar_org", "GFX_navieslist_bar_org"), ("bar_str", "GFX_navieslist_bar_str"),
                      ("bar_prepare", "GFX_unitlist_bar_prepare")):
        s = sprites.get(spr)
        for part, rel in (("", s and s["overlay"]), ("_bg", s and s["mask"])):
            img = gui.load_image(hoi4, rel)
            if img is not None:
                img.save(dst / f"{name}{part}.png")
                sizes[name + part] = [img.width, img.height, 1]
    for t in sorted(main_types):
        img = unit_icon(hoi4, sprites, t) or unit_icon(hoi4, sprites, "infantry")
        if img is None:
            print(f"  unit panel icon missing for {t}")
            continue
        img.save(dst / "icons" / f"{t}.png")
        sizes["icon_" + t] = [img.width, img.height, 1]
    (dst / "unitpanel.json5").write_text(
        "// Generated from HOI4 interface/unitview.gui sprites: [w, h, frames]" + chr(10)
        + json.dumps({"window_border": 64, "sprites": sizes}, indent=1) + chr(10), encoding="utf-8")
    print(f"Unit panel: {len(sizes)} sprites")


def convert_army_cards(hoi4: Path, out: Path):
    """Sprites of the army cards at the bottom of the screen (interface/leadergroups.gui) as PNGs under
    gfx/interface/armies/: the card frame states, the 'new army' plus button (and a grey copy for when
    nothing is selected), the unknown-leader portrait, the disband button and the battle plan execute / stop
    buttons above the card."""
    import hoi4_gui as gui
    from PIL import Image
    dst = out / "gfx" / "interface" / "armies"
    dst.mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    sizes = {}

    def save(name, img):
        img.save(dst / f"{name}.png")
        sizes[name] = [img.width, img.height]

    frames = gui.sprite_frames(hoi4, sprites, "GFX_LG_group_frame") or []
    for i, fr in enumerate(frames):
        save(f"frame_{i}", fr)
    add = gui.sprite_frames(hoi4, sprites, "GFX_LG_select_group_btn")
    if add:
        save("add", add[0])
        rgba = add[0].convert("RGBA")
        r, g, b, a = rgba.split()
        grey = Image.merge("RGB", (r, g, b)).convert("L").point(lambda v: int(v * 0.8))
        save("add_grey", Image.merge("RGBA", (grey, grey, grey, a)))
    for name, spr in (("portrait", "GFX_leader_unknown"), ("disband", "GFX_LG_disband_group_btn"),
                      ("exec_bg", "GFX_execute_stop_small_bg")):
        fr = gui.sprite_frames(hoi4, sprites, spr)
        if fr:
            save(name, fr[0])
    # Battle plan execute / stop buttons above the card (4 frames each) and the scrolling chevrons over the
    # execute button while the plan runs (12 frames, 24 fps)
    for name, spr in (("exec_play", "GFX_execute_button_play_small"), ("exec_stop", "GFX_execute_button_stop_small"),
                      ("exec_anim", "GFX_active_battleplan_anim"),
                      ("plan_value", "GFX_plan_value_strip")):   # plan grade over the play button: -, !, :, tick
        for i, fr in enumerate(gui.sprite_frames(hoi4, sprites, spr)):
            save(f"{name}_{i}", fr)
    (dst / "armies.json5").write_text(
        "// Generated from HOI4 interface/leadergroups.gui sprites: [w, h]" + chr(10)
        + json.dumps(sizes, indent=1) + chr(10), encoding="utf-8")
    print(f"Army cards: {len(sizes)} sprites")


def convert_battleplan_gfx(hoi4: Path, out: Path):
    """Sprites of the Battle Plans window (interface/battleplantools.gui) under gfx/interface/battleplans/:
    the window background and the draw offence / draw defence / assign / unassign / delete buttons (2 frames each:
    idle, pressed)."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "battleplans"
    dst.mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    for name, spr in (("bg", "GFX_BPT_bg"), ("offence", "GFX_BPT_order_draw_offence_btn"),
                      ("defence", "GFX_BPT_order_draw_defence_btn"), ("delete", "GFX_BPT_order_delete_btn"),
                      ("assign", "GFX_BPT_order_assign_units_btn"), ("unassign", "GFX_BPT_order_unassign_units_btn")):
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            print(f"  battle plans: no sprite {spr}")
            continue
        for i, fr in enumerate(frames):
            fr.save(dst / (f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"))
    print("Battle plan sprites written")


# Peace conference (interface/peaceconferencewindow.gui) sprites: file name -> GFX sprite. Tiled ones are 9-sliced
# in USS with their borderSize.
PEACE_SPRITES = {
    "upper_bar": "GFX_tiled_peace_upper_bar", "top_art": "GFX_top_art_winning_conference",
    "button_261": "GFX_button_261x34", "button_148": "GFX_button_148x34", "plain_bg": "GFX_tiled_plain_bg",
    "window_small_small": "GFX_tiled_window_small_small", "window_thin_border": "GFX_tiled_window_thin_border2",
    "window_small": "GFX_tiled_window_small", "window_border": "GFX_tiled_window2_1b_border",
    "overlay": "GFX_tiled_generic_overlay_bg1_small", "icon_button": "GFX_icon_button_46x46",
    "take_state": "GFX_icon_occupied_territories", "action_header": "GFX_conference_action_header",
    "action_bottom": "GFX_peace_action_bottom", "turn_bg": "GFX_peace_turn_bg",
    "country_entry": "GFX_load_save_entry2", "flag_frame": "GFX_small_flag_frame_thin2",
    "entry": "GFX_conference_action_entry_bg", "victory_entry": "GFX_peace_conference_victory_entry",
    "tab": "GFX_sort_button_140x29", "lobby_button": "GFX_main_lobby_button", "icons": "GFX_peace_conf_icons",
    "player": "GFX_players_icon", "score": "GFX_victory_points", "participation": "GFX_war_score",
    "expand": "GFX_peace_drop_down_icon",
}


def convert_peace_gfx(hoi4: Path, out: Path):
    """Sprites of the peace conference screen (interface/peaceconferencewindow.gui) under gfx/interface/peace/
    for Hoi4PeaceConferencePanel: PEACE_SPRITES, multi-frame ones as name_0.png, name_1.png..."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "peace"
    dst.mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    for name, spr in PEACE_SPRITES.items():
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            print(f"  peace conference: no sprite {spr}")
            continue
        for i, fr in enumerate(frames):
            fr.save(dst / (f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"))
    print("Peace conference sprites written")


# Production screen (interface/countryproductionlineview.gui) sprites: file name -> GFX sprite. Multi-frame ones are
# written as name_0.png, name_1.png...; tiled ones (corneredTileSpriteType) are 9-sliced in USS with their borderSize.
PRODUCTION_SPRITES = {
    "plain_bg": "GFX_tiled_plain_bg", "header_bg": "GFX_header_bg", "close": "GFX_closebutton",
    "win_top": "GFX_production_win_top", "lines_border": "GFX_tiled_window2_1b_border",
    "item": "GFX_production_item", "item_selected": "GFX_production_line_selected",
    "priority_frame": "GFX_conveyor_group_btn_frame", "efficiency_frame": "GFX_efficiency_progressbar_frame",
    "efficiency_icon": "GFX_efficiency_icon", "equipment_bg": "GFX_prod_land_equipment_item_large",
    "line_glow": "GFX_prod_line_glow", "btn_x10": "GFX_btn_x10", "btn_x5": "GFX_btn_x5", "delete": "GFX_NV_btn_delete",
    "need_strip": "GFX_prod_line_target_strip", "factory": "GFX_factory_item", "factory_bg": "GFX_factory_bg",
    "sub_1": "GFX_subtract_one", "sub_5": "GFX_subtract_5", "sub_10": "GFX_subtract_10",
    "add_1": "GFX_add_one", "add_5": "GFX_add_5", "add_10": "GFX_add_10",
    "arrow_up": "GFX_arrow_up_small", "arrow_down": "GFX_arrow_down_small",
    "add_inf_art": "GFX_add_prod_inf_art_line", "add_armour": "GFX_add_prod_armour_line",
    "add_aircraft": "GFX_add_prod_aircraft_line", "new_equipment_glow": "GFX_prod_new_equipment_glow",
    "military_factory_icon": "GFX_military_factory_icon", "output_icon": "GFX_production_output_icon",
    "efficiency_cap_icon": "GFX_efficiency_cap_icon", "efficiency_loss_icon": "GFX_production_efficiency_loss_icon",
    "show_inf_art": "GFX_inf_art_checkbox", "show_armour": "GFX_armour_checkbox", "show_air": "GFX_air_checkbox",
    "show_stockpiling": "GFX_stockpiling_checkbox", "show_reinforcing": "GFX_reinforcing_checkbox",
    "show_capital_ships": "GFX_capital_ships_checkbox", "show_screen_ships": "GFX_screen_ships_checkbox",
    "show_other_ships": "GFX_other_ships_checkbox", "show_refitting": "GFX_refit_ships_checkbox",
    "show_resources": "GFX_resources_checkbox", "show_outdated": "GFX_outdated_checkbox",
    "show_upgrading": "GFX_upgrading_checkbox",
    "picker_item": "GFX_equipment_item_large_02", "picker_glow": "GFX_new_equipment_glow_large",
    "resource_item": "resource_item_background",
}
# Progress bars (progressbartype): file name -> texture under the HOI4 install (fill, background)
PRODUCTION_BARS = {
    "efficiency_bar": "gfx/interface/efficiency_progressbar.dds",
    "efficiency_bar_max": "gfx/interface/efficiency_progressbar_bg_max.dds",
    "efficiency_bar_bg": "gfx/interface/efficiency_progressbar_bg.dds",
    "progress_bar": "gfx/interface/prod_progress_bar2.dds",
    "progress_bar_bg": "gfx/interface/prod_progress_bar2_bg.dds",
}


# Construction screen (interface/countryconstructionsview.gui) sprites: file name -> GFX sprite (frames as name_N.png).
# The building buttons use the state view's building strip (gfx/interface/stateview/buildings_N.png).
CONSTRUCTION_SPRITES = {
    "window_bg": "GFX_tiled_window_thin_border", "plain_bg": "GFX_tiled_plain_bg",
    "column_border": "GFX_tiled_window2_1b_border", "top_bg": "GFX_construction_screen_top_bg",
    "header_bg": "GFX_construction_header_bg", "close": "GFX_closebutton", "minimize": "GFX_production_line_arrow_left",
    "civ_factory_icon": "GFX_non_available_factory_icon", "civ_output_icon": "GFX_civ_factory_output_icon",
    "speed_icon": "GFX_construction_speed_icon", "section_bg": "GFX_diplo_details_header",
    "state_constructions": "GFX_state_constructions", "shared_constructions": "GFX_shared_constructions",
    "province_constructions": "GFX_province_constructions", "facility_constructions": "GFX_facility_constructions",
    "special_entry_bg": "GFX_construction_special_entry_bg", "entry_bg": "GFX_construction_entry_bg",
    "consumer_goods_icon": "GFX_consumer_goods_icon", "button_glow": "GFX_construction_glow_overlay",
    "count_bg": "GFX_generic_box_80", "factory_wrench": "GFX_factory_icon_with_wrench",
    "sub_1": "GFX_subtract_one", "add_1": "GFX_add_one", "button_94": "GFX_button_94x31",
    "lines_overlay": "GFX_tiled_generic_overlay_bg1_small",
    # queue entries (production_building_line_entry)
    "ongoing": "GFX_ongoing_contruction_anim", "amount_add": "GFX_zoom_in", "amount_sub": "GFX_zoom_out",
    "arrow_up": "GFX_arrow_up_small", "arrow_down": "GFX_arrow_down_small", "delete": "GFX_closebutton_small",
    "line_glow": "GFX_construction_line_glow",
    # state boxes on the map while placing (mapicons.gui construction_info_mapicon)
    "map_box": "GFX_construction_province_background", "infra_texticon": "GFX_infrastructure_texticon",
    "backhoe": "GFX_backhoe_icon_small",
}
# Progress bar textures (progressbartype GFX_construction_progressbar): file name -> texture under the HOI4 install
CONSTRUCTION_BARS = {
    "progress_bar": "gfx/interface/construction_progressbar.dds",
    "progress_bar_bg": "gfx/interface/construction_progressbar_bg.dds",
}


def convert_construction_gfx(hoi4: Path, out: Path):
    """Sprites of the construction screen under gfx/interface/construction/ for Hoi4ConstructionPanel."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "construction"
    dst.mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    for name, spr in CONSTRUCTION_SPRITES.items():
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            print(f"  construction: no sprite {spr}")
            continue
        for i, fr in enumerate(frames):
            fr.save(dst / (f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"))
    for name, rel in CONSTRUCTION_BARS.items():
        img = gui.load_image(hoi4, rel)
        if img is None:
            print(f"  construction: no texture {rel}")
            continue
        img.save(dst / f"{name}.png")
    print("Construction sprites written")


# Recruit & deploy screen (interface/countrydeploymentview.gui): file name -> HOI4 sprite
DEPLOYMENT_SPRITES = {
    # main window
    "window_bg": "GFX_tiled_window", "header_bg": "GFX_header_bg", "binding": "GFX_deployment_binding",
    "column_border": "GFX_tiled_window2_1b_border", "lines_overlay": "GFX_tiled_generic_overlay_bg1",
    "close": "GFX_closebutton",
    # equipment priority rows (deploy_entry)
    "entry_reinforcements": "GFX_deploy_reinforcements_entry", "entry_upgrades": "GFX_deploy_upgrades_entry",
    "entry_supply": "GFX_deploy_supply_entry", "entry_operations": "GFX_deploy_operations_entry",
    "entry_garrisons": "GFX_deploy_garrisons_entry", "icon_bg": "GFX_deploy_icon_tiled_bg",
    "icon_reinforcements": "GFX_reinforcements_icon", "icon_upgrades": "GFX_upgrades_icon",
    "icon_supply": "GFX_deploy_supply_icon", "icon_operations": "GFX_operations_deploy_icon",
    "icon_garrisons": "GFX_garrison_deploy_icon", "icon_raids": "GFX_deploy_raids_icon",
    "title_bg": "GFX_deploy_priority_title_bg", "meter_bg": "GFX_deploy_priority_equipment_meter_bg",
    "equipment_icon": "GFX_equipment_icon", "priority": "GFX_deploy_priority",
    # orders (military_deployment_conveyor_view) and their summary and lines
    "order_bg": "GFX_military_deployment_conveyor_view_bg", "template_icon_bg": "GFX_technology_medium",
    "priority_strip": "GFX_div_deploy_priority_icon_strip", "location": "GFX_conveyor_location_btn",
    "decrease": "GFX_naval_decrease_amount", "increase": "GFX_naval_increase_amount",
    "add_line": "GFX_military_deployment_add_line_btn", "deploy_order": "GFX_deploy_conveyor_button",
    "cancel_order": "GFX_cancel_conveyor_button",
    "summary_bg": "GFX_military_deployment_end_line_view_bg", "collapse": "GFX_conveyor_collapse_btn",
    "order_arrows": "GFX_change_conveyor_order_btn", "equipment_state_icon": "GFX_equipment_state_icon",
    "end_line": "GFX_military_deployment_end_line", "line_bg": "GFX_military_deployment_line_view_bg",
    "name_dropdown": "GFX_collapse_or_close", "unit_level": "GFX_unit_level", "deploy_line": "GFX_deploy_line_button",
    "cancel_line": "GFX_closebutton_small",
    # templates window
    "templates_bg": "GFX_tiled_window_1b_thin_border", "templates_header_bg": "GFX_subview_header_bg_375x101",
    "special_forces_icon": "GFX_special_forces_icon", "plain_bg": "GFX_tiled_plain_bg_small",
    "checkbox": "GFX_generic_checkbox", "designer_button": "GFX_division_designer_button",
    "foreign_templates": "GFX_foreign_templates_dropdown_button", "template_bg": "GFX_deployment_named_division_bg",
    "small_button": "GFX_small_button_71x26", "delete_template": "GFX_delete_template_button",
    "obsolete_toggle": "GFX_toggle_obsolete_button",
    # a division's template priority in the division list (unitview.gui army_division_view unitlist_priority_icon)
    "unit_priority": "GFX_unitlist_priority_icon_strip",
    "infinity": "GFX_infinity_icon",  # an order that keeps training (INFINITY = "£infinity_icon!")
}
# Progress bars (progressbartype): file name -> texture
DEPLOYMENT_BARS = {
    "entry_bar": "gfx/interface/deployment_entry_progressbar.dds",
    "entry_bar_stalled": "gfx/interface/deployment_entry_stalled_progressbar.dds",
    "entry_bar_bg": "gfx/interface/deployment_entry_progressbar_bg.dds",
    "priority_bar": "gfx/interface/deployment_progressbar.dds",
    "priority_bar_bg": "gfx/interface/deployment_progressbar_bg.dds",
}


# Division designer (interface/divisiondesignerview.gui) sprites: file name -> GFX sprite; multi-frame ones as name_N.png
DESIGNER_SPRITES = {
    "popup_bg": "GFX_division_designer_popup_bg", "regiment_bg": "GFX_div_unit_item_bg_wide",
    "support_bg": "GFX_div_subunit_item_bg", "locked": "GFX_locked_icon", "blocked": "GFX_blocked_icon", "unlocked": "GFX_unlocked_icon",
    "icon_bg": "GFX_division_icon_bg", "priority_bg": "GFX_division_priority_bg", "priority": "GFX_div_priority_strip",
    "priority_icon": "GFX_division_priority_icon_strip", "composition_icon": "GFX_army_composition_icon",
    "hardness_icon": "GFX_armor_softness_icon", "org_icon": "GFX_organization_texticon",
    "strength_icon": "GFX_strenght_texticon", "speed_icon": "GFX_MODIFIER_ARMY_SPEED_FACTOR",
    "value_bg": "GFX_value_bg", "mini_tooltip": "GFX_mini_tooltip", "button_123": "GFX_button_123x34",
    "button_123_alt": "GFX_button_123x34_alt", "button_reset": "GFX_button_reset_123x34",
    "button_221": "GFX_button_221x34", "close": "GFX_closebutton", "cost_bg": "GFX_generic_background",
    "nametag": "GFX_division_nametag_icon", "army_xp": "GFX_army_experience",
    "new_slot": "GFX_new_regiment_slot", "replace_slot": "GFX_replace_regiment_slot", "remove": "GFX_remove_icon",
    "template_icon": "GFX_division_template_icon", "softness_frame": "GFX_softness_ratio_frame",
    "picker_bg": "GFX_tiled_window_1b_border",
}


def convert_designer_gfx(hoi4: Path, out: Path, land_subs):
    """Sprites of the division designer under gfx/interface/designer/ (DESIGNER_SPRITES), plus a medium
    pictogram per land sub-unit (icons/<sub-unit>.png: unit_icon, its own GFX_unit_<name>_icon_medium first)."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "designer"
    (dst / "icons").mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    for name, spr in DESIGNER_SPRITES.items():
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            print(f"  designer: no sprite {spr}")
            continue
        for i, fr in enumerate(frames):
            fr.save(dst / (f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"))
    missing = []
    for name, s in sorted(land_subs.items()):
        img = unit_icon(hoi4, sprites, name, s.get("sprite"))
        if img is None:
            missing.append(name)
            continue
        img.save(dst / "icons" / f"{name}.png")
    if missing:
        print(f"  designer: no icon for {missing}")
    print(f"Division designer sprites written ({len(land_subs) - len(missing)} sub-unit icons)")


def convert_deployment_gfx(hoi4: Path, out: Path):
    """Sprites of the recruit & deploy screen under gfx/interface/deployment/ for Hoi4DeploymentPanel."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "deployment"
    dst.mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    for name, spr in DEPLOYMENT_SPRITES.items():
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            print(f"  deployment: no sprite {spr}")
            continue
        for i, fr in enumerate(frames):
            fr.save(dst / (f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"))
    for name, rel in DEPLOYMENT_BARS.items():
        img = gui.load_image(hoi4, rel)
        if img is None:
            print(f"  deployment: no texture {rel}")
            continue
        img.save(dst / f"{name}.png")
    print("Deployment sprites written")


def convert_production_gfx(hoi4: Path, out: Path, oob_countries):
    """Sprites of the production screen under gfx/interface/production/ for Hoi4ProductionPanel: PRODUCTION_SPRITES,
    PRODUCTION_BARS, and every equipment picture a country can build (equipment/<GFX name>.png, named by the
    `sprite` describe_equipment put in the OOB's equipment block)."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "production"
    (dst / "equipment").mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    for name, spr in PRODUCTION_SPRITES.items():
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            print(f"  production: no sprite {spr}")
            continue
        for i, fr in enumerate(frames):
            fr.save(dst / (f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"))
    for name, rel in PRODUCTION_BARS.items():
        img = gui.load_image(hoi4, rel)
        if img is None:
            print(f"  production: no texture {rel}")
            continue
        img.save(dst / f"{name}.png")
    pictures = {b["sprite"] for c in oob_countries.values() for b in c.get("equipment", {}).values() if b.get("sprite")}
    for spr in sorted(pictures):
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if frames:
            frames[0].save(dst / "equipment" / f"{spr}.png")
        else:
            print(f"  production: no picture {spr}")
    print(f"Production sprites written ({len(pictures)} equipment pictures)")


# Research screen (interface/countrytechnologyview.gui) sprites: file name -> GFX sprite (frames as name_N.png)
RESEARCH_SPRITES = {
    "plain_bg": "GFX_tiled_plain_bg", "header_bg": "GFX_header_bg", "close": "GFX_closebutton", "tab": "GFX_tab_large",
    "slots_border": "GFX_tiled_window2_1b_border", "overlay_bg": "GFX_tiled_generic_overlay_bg1",
    "top_win": "GFX_research_top_win", "focus_bonuses_icon": "GFX_focus_bonuses_icon",
    "speed_icon": "GFX_research_speed_icon", "line_bg": "GFX_research_line_bg",
    "empty_glow": "GFX_empty_research_slot_glow", "progress_frame": "GFX_research_progressbar_frame",
    "win_bottom": "GFX_production_win_bottom",
}
# Gfx_research_progressbar (progressbartype, 377 x 4): file name -> texture (fill, background)
RESEARCH_BARS = {
    "progress_bar": "gfx/interface/research_progressbar.dds",
    "progress_bar_bg": "gfx/interface/research_progressbar_bg.dds",
}
BASE_RESEARCH_SLOTS = 2  # defines NCountry BASE_RESEARCH_SLOTS


def convert_research_gfx(hoi4: Path, out: Path):
    """Sprites of the research screen under gfx/interface/research/ for Hoi4ResearchPanel.
    Prototype: Tools/prototypes/research/render_research.py"""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "research"
    dst.mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    for name, spr in RESEARCH_SPRITES.items():
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            print(f"  research: no sprite {spr}")
            continue
        for i, fr in enumerate(frames):
            fr.save(dst / (f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"))
    for name, rel in RESEARCH_BARS.items():
        img = gui.load_image(hoi4, rel)
        if img is None:
            print(f"  research: no texture {rel}")
            continue
        img.save(dst / f"{name}.png")
    print("Research sprites written")


SCROLLBAR_SPRITES = {"vertical_bg": "GFX_scrollbar_vertical_bg", "horizontal_bg": "GFX_scrollbar_horisontal_bg",
                     "drager": "GFX_scroll_drager", "track": "GFX_scroll_track",
                     "track_horizontal": "GFX_scroll_track_horisontal", "up": "GFX_scroll_up", "down": "GFX_scroll_down",
                     "left": "GFX_scroll_left", "right": "GFX_scroll_right"}


def convert_scrollbar_gfx(hoi4: Path, out: Path):
    """HOI4's scrollbars (interface/core.gui right_vertical_slider, bottom_horizontal_slider) for the Hoi4Theme
    hoi4-scroll class: gfx/interface/scrollbar/<part>.png (frame 0 of each)."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "scrollbar"
    dst.mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    for name, spr in SCROLLBAR_SPRITES.items():
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if frames:
            frames[0].save(dst / f"{name}.png")


TECHTREE_STATES = ("unavailable", "available", "researched", "currently_researching", "branch")
TECHTREE_LINE_SPRITES = {"line_up": "GFX_techtree_line_up_states", "line_down": "GFX_techtree_line_down_states",
                         "line_left": "GFX_techtree_line_left_states", "line_right": "GFX_techtree_line_right_states",
                         "line_center": "GFX_techline_center_all_states"}
TECHTREE_SPRITES = {"window_bg": "GFX_tiled_plain_bg2", "folder_bg": "GFX_tiled_window_2b_border",
                    "close": "GFX_closebutton"}


def convert_techtree_gfx(hoi4: Path, out: Path, loc):
    """The tech tree screen (interface/countrytechtreeview.gui) for Hoi4TechTreePanel, under gfx/interface/techtree/:
    techtree.json5 with, per folder we research in (hoi4_technologies.json5), its tab, its content size, backgrounds,
    labels (years, subtitles; positions in the folder's content) and gridboxes (root tech, position, slot size, format:
    LEFT puts a tech's y across and its x down, the others x across and y down), and per folder the small and large
    item templates (size, offset, icon and name places); the sprites: tabs (frame 1 open), item backgrounds per folder,
    size and state (the folder's own, else the generic; the researching one a 9-frame strip), the link line pieces
    (4 frames: researched, available, unavailable, researching... see the prototype), and every tech's picture
    (icons/<tech>.png, country versions icons/<TAG>_<tech>.png).
    Prototype: Tools/prototypes/research/render_techtree.py"""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "techtree"
    (dst / "icons").mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    techdoc = json.loads((out / "common" / "hoi4_technologies.json5").read_text(encoding="utf-8").split("\n", 1)[1])
    techs, folders = techdoc["technologies"], techdoc["folders"]

    def save(spr, name):
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            return None
        names = []
        for i, fr in enumerate(frames):
            fn = f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"
            fr.save(dst / fn)
            names.append(fn)
        return {"image": names[0], "frames": names, "w": frames[0].width, "h": frames[0].height,
                "border": list(sprites[spr]["border"]) if sprites[spr].get("border") else None}

    root = gui.load_gui(hoi4, "countrytechtreeview.gui")
    types = {gui.kv(v, "name"): v for k, _, v in gui.kv(root, "guiTypes") or [] if isinstance(v, list)}
    win = types["countrytechtreeview"]
    doc = {"window": {k: save(s, k) for k, s in TECHTREE_SPRITES.items()}, "folders": {}, "items": {}}
    tabs = next(v for k, _, v in win if isinstance(v, list) and gui.kv(v, "name") == "folder_tabs")
    tab_pos = {}
    for k, _, b in tabs:
        if isinstance(b, list) and k == "buttonType":
            pos = gui.kv(b, "position")
            tab_pos[gui.kv(b, "name")] = (float(gui.kv(pos, "x") or 0), float(gui.kv(pos, "y") or 0),
                                          gui.kv(b, "quadTextureSprite") or gui.kv(b, "spriteType"))

    for k, _, f in win:
        name = gui.kv(f, "name") if isinstance(f, list) else None
        if k != "containerWindowType" or name not in folders:
            continue
        margin = gui.kv(f, "margin") or []
        folder = {"margin": {s: float(gui.kv(margin, s) or 0) for s in ("top", "left", "bottom", "right")},
                  "images": [], "labels": [], "grids": [], "width": 0, "height": 0}
        tx, ty, tspr = tab_pos.get(f"{name}_tab", (0, 0, None))
        folder["tab"] = {"x": tx, "y": ty, **(save(tspr, f"tab_{name}") or {})} if tspr else None
        # the content container (techtree_stripes, size_filler) is its size or the visible folder, whichever is
        # larger (min = 100%%); what it holds may anchor to its centre or right edge (artillery_subtitles center_up,
        # artillery_year_right upper_right). Laid out at two widths, ax is how far an element moves per pixel of
        # extra width (0 left, 0.5 centre, 1 right): x on screen = x + ax * (content width - width).
        def stretchable(block):
            sz = gui.kv(block, "size") if isinstance(block, list) else None
            return isinstance(sz, list) and gui.kv(sz, "min") is not None

        def laid_out(extra):
            fb = copy.deepcopy(f)
            for k, _, v in fb:
                if k == "containerWindowType" and stretchable(v):
                    sz = gui.kv(v, "size")
                    for j, (kk, op, vv) in enumerate(sz):
                        if kk == "width":
                            sz[j] = (kk, op, str(gui.num(vv) + extra))
            out = []
            gui.layout(fb, (0, 0, 1920, 1080), out)
            return out
        laid, wide = laid_out(0), laid_out(1000)
        for (kind, el, x, y, block), (_, _, x2, _, _) in zip(laid, wide):
            kind = kind.lower()
            ax = round((x2 - x) / 1000, 3)
            if kind == "containerwindowtype" and stretchable(block) and not folder["width"]:
                sz = gui.kv(block, "size") or []
                folder["width"] = gui.num(gui.kv(sz, "width"))
                folder["height"] = gui.num(gui.kv(sz, "height"))
            elif kind == "icontype":
                spr = gui.kv(block, "spriteType")
                if spr and not str(el).startswith("highlight") and str(gui.kv(block, "hide") or "no") != "yes":
                    img = save(spr, f"bg_{name}_{el}")
                    if img:
                        folder["images"].append({"x": x, "y": y, "ax": ax, **img})
            elif kind == "instanttextboxtype":
                text = gui.kv(block, "text") or ""
                folder["labels"].append({"x": x, "y": y, "ax": ax, "text": loc_refs(loc.get(text, text), loc),
                                         "w": float(gui.kv(block, "maxWidth") or 0),
                                         "h": float(gui.kv(block, "maxHeight") or 0),
                                         "font": gui.kv(block, "font") or "",
                                         "header": "36header" in (gui.kv(block, "font") or "")})
            elif kind == "gridboxtype":
                ss = gui.kv(block, "slotsize") or []
                folder["grids"].append({"root": str(el)[:-5] if str(el).endswith("_tree") else el, "x": x, "y": y,
                                        "w": float(gui.kv(ss, "width") or 70), "h": float(gui.kv(ss, "height") or 70),
                                        "format": (gui.kv(block, "format") or "UPPER_LEFT").strip('"').upper()})
        doc["folders"][name] = folder

        # item templates: the large card (techtree_<folder>_item) and the small icon (techtree_<folder>_small_item)
        items = {}
        for size, tname in (("large", f"techtree_{name}_item"), ("small", f"techtree_{name}_small_item")):
            t = types.get(tname)
            if t is None:
                continue
            pos, sz = gui.kv(t, "position") or [], gui.kv(t, "size") or []
            item = {"x": float(gui.kv(pos, "x") or 0), "y": float(gui.kv(pos, "y") or 0),
                    "w": float(gui.kv(sz, "width") or 0), "h": float(gui.kv(sz, "height") or 0), "bg": {}}
            for kk, _, e in t:
                if not isinstance(e, list):
                    continue
                en, ep = gui.kv(e, "name"), gui.kv(e, "position") or []
                if kk == "iconType" and en == "Icon":
                    item["icon"] = {"x": float(gui.kv(ep, "x") or 0), "y": float(gui.kv(ep, "y") or 0),
                                    "center": str(gui.kv(e, "centerposition") or "no") == "yes"}
                elif kk.lower() == "instanttextboxtype" and en == "Name":
                    item["name"] = {"x": float(gui.kv(ep, "x") or 0), "y": float(gui.kv(ep, "y") or 0),
                                    "w": float(gui.kv(e, "maxWidth") or 0), "h": float(gui.kv(e, "maxHeight") or 0)}
            prefix = "small_" if size == "small" else ""
            for state in TECHTREE_STATES:
                for spr in (f"GFX_technology_{name}_{prefix}{state}_item_bg", f"GFX_technology_{prefix}{state}_item_bg",
                            f"GFX_technology_{state}_item_bg"):
                    if spr in sprites:
                        img = save(spr, f"item_{name}_{size}_{state}")
                        if img:
                            item["bg"][state] = img
                            break
            items[size] = item
        doc["items"][name] = items

    doc["lines"] = {k: save(s, k) for k, s in TECHTREE_LINE_SPRITES.items()}
    n_country = 0
    for tech in techs:
        frames = gui.sprite_frames(hoi4, sprites, f"GFX_{tech}_medium")
        if frames:
            frames[0].save(dst / "icons" / f"{tech}.png")
    for spr in sprites:
        m = re.fullmatch(r"GFX_([A-Z]{3})_(\w+)_medium", spr)
        if m and m.group(2) in techs:
            frames = gui.sprite_frames(hoi4, sprites, spr)
            if frames:
                frames[0].save(dst / "icons" / f"{m.group(1)}_{m.group(2)}.png")
                n_country += 1
    (dst / "techtree.json5").write_text("// Generated from HOI4 interface/countrytechtreeview.gui by hoi4_convert.py\n"
                                        + json.dumps(doc, indent=1) + "\n", encoding="utf-8")
    print(f"Tech tree: {len(doc['folders'])} folders, {len(techs)} tech pictures, {n_country} country versions")


# State view (interface/countrystateview.gui) sprites: name -> GFX sprite
STATEVIEW_SPRITES = {
    "bg": "GFX_ww_stateview_bg", "slot_bg": "GFX_build_slot_bg", "no_building": "GFX_no_building",
    "status_overlay": "GFX_building_status_overlay", "entry_bg": "GFX_ww_building_standing_entry_stateview",
    "text_bg": "GFX_generic_text_bg_48", "population": "GFX_population_icon",
    "vp_bg": "GFX_victorypoint_stateview_bg", "close": "GFX_closebutton_small",
    "buildings": "GFX_buildings_strip", "resources": "GFX_resources_strip",
    "flag_frame": "GFX_diplo_countrylist_flag_frame", "flag_frame_thin": "GFX_small_flag_frame_thin",
}


def write_province_centres(out: Path, land_ids):
    """map/hoi4_province_centres.csv: every land province's centre (mean pixel, map px, y down, rounded), for the
    GAME's movement times (km between centres x MAP_SCALE_PIXEL_TO_KM)."""
    flat = land_ids.ravel()
    n = int(flat.max()) + 1
    ys, xs = np.divmod(np.arange(flat.size, dtype=np.int64), land_ids.shape[1])
    cnt = np.bincount(flat, minlength=n)
    sx = np.bincount(flat, xs, minlength=n)
    sy = np.bincount(flat, ys, minlength=n)
    rows = ["id;x;y"]
    for pid in range(1, n):
        if cnt[pid]:
            rows.append(f"{pid};{int(round(sx[pid] / cnt[pid]))};{int(round(sy[pid] / cnt[pid]))}")
    (out / "map" / "hoi4_province_centres.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")
    print(f"Province centres: {len(rows) - 1}")


def write_impassable_borders(out: Path, land_ids, impassable):
    """map/hoi4_impassable_borders.csv: for every impassable province (in an impassable state), the length of its
    border (pixel edges) with each land neighbour, passable or impassable (an impassable neighbour counts for whoever
    controls it; the GAME skips neighbours in the same state). HOI4 gives impassable land to whoever controls most
    of its border (the GAME's Hoi4ImpassableControl)."""
    imp = np.zeros(int(land_ids.max()) + 1, dtype=bool)
    for pid in impassable:
        if pid < len(imp):
            imp[pid] = True
    lengths = {}
    for a, b in ((land_ids[:, :-1], land_ids[:, 1:]), (land_ids[:-1, :], land_ids[1:, :])):
        m = (a != b) & (a > 0) & (b > 0) & (imp[a] | imp[b])
        x, y = a[m].astype(np.int64), b[m].astype(np.int64)
        # each impassable side gets the edge (both, when both are impassable)
        i = np.concatenate([x[imp[x]], y[imp[y]]])
        n = np.concatenate([y[imp[x]], x[imp[y]]])
        pairs, counts = np.unique(i << 16 | n, return_counts=True)
        for pr, c in zip(pairs.tolist(), counts.tolist()):
            lengths[pr] = lengths.get(pr, 0) + c
    rows = ["impassable;neighbour;length"]
    for pr in sorted(lengths):
        rows.append(f"{pr >> 16};{pr & 0xFFFF};{lengths[pr]}")
    (out / "map" / "hoi4_impassable_borders.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")
    print(f"Impassable borders: {len(rows) - 1} edges around {len({pr >> 16 for pr in lengths})} provinces")


def convert_adjacencies(hmap: Path, out: Path, provinces):
    """HOI4 map/adjacencies.csv -> Archon map/adjacencies.csv (the engine's adjacency overrides,
    From;To;Type;Through;Comment): the land crossings over water ("sea" between two land provinces, through the
    sea province, e.g. the Danish straits) and the impassable borders. Sea-to-sea rules (canals and straits for
    ships, which have rules and no type) are left out: there is no navy."""
    land = {pid for pid, _, _, _, ptype, _ in provinces if ptype == "land"}
    rows = ["From;To;Type;Through;Comment"]
    kept = {"sea": 0, "impassable": 0}
    for line in read_text(hmap / "adjacencies.csv").splitlines()[1:]:
        f = line.split(";")
        if len(f) < 4:
            continue
        try:
            a, b, through = int(f[0]), int(f[1]), int(f[3])
        except ValueError:
            continue
        kind = f[2].strip()
        if kind not in kept or a not in land or b not in land:
            continue
        comment = f[9].strip() if len(f) > 9 else ""
        rows.append(f"{a};{b};{kind};{through if through > 0 else -1};{comment}")
        kept[kind] += 1
    (out / "map" / "adjacencies.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")
    print(f"Adjacencies: {kept['sea']} crossings, {kept['impassable']} impassable borders")


def convert_state_view(hoi4: Path, out: Path, state_centers=None):
    """HOI4 state view data and sprites for the GAME state panel (Hoi4StatePanel.cs):
    map/hoi4_state_data.json5 (building definitions, state categories, per-state owner, cores,
    manpower, resources, victory points, buildings at the 1936 start) and gfx/interface/stateview/.
    Each state also gets its HOI4 state value at the start (`value`: 10 + 1 per 100,000 people + base resources
    + 4 per building slot of its category + the `value` of its state buildings; provincial ones don't count, checked
    against a live peace conference) and its centre on the map (`center`, map px) for the peace conference."""
    import hoi4_gui as gui
    dst = out / "gfx" / "interface" / "stateview"
    (dst / "terrain").mkdir(parents=True, exist_ok=True)
    sprites = gui.load_sprites(hoi4)
    for name, spr in STATEVIEW_SPRITES.items():
        frames = gui.sprite_frames(hoi4, sprites, spr)
        if not frames:
            print(f"  state view: no sprite {spr}")
            continue
        for i, fr in enumerate(frames):
            fr.save(dst / (f"{name}.png" if len(frames) == 1 else f"{name}_{i}.png"))
    tdir = hoi4 / "gfx" / "interface" / "terrains"
    for f in sorted(tdir.glob("terrain_*.dds")):
        n = f.stem[len("terrain_"):]
        if any(s in n for s in ("night", "rain", "snow", "storm", "winter")):
            continue
        img = gui.load_image(hoi4, f"gfx/interface/terrains/{f.name}")
        if img is not None:
            img.save(dst / "terrain" / f"{n.replace('_day', '')}.png")

    # building definitions
    defs = {}
    broot = get(parse_pdx(read_text(hoi4 / "common" / "buildings" / "00_buildings.txt")), "buildings", [])
    for name, _, body in broot:
        if not isinstance(body, list) or name is None:
            continue
        cap = get(body, "level_cap", [])
        d = {"frame": int(get(body, "icon_frame", 0) or 0), "coastal": get(body, "only_costal") == "yes"}
        if get(body, "value") is not None:
            d["value"] = float(get(body, "value"))  # peace conference / state value points per level
        # construction: cost of a level (base_cost + per_level_extra_cost x current level) and whether the state's
        # infrastructure speeds it up
        try:
            if get(body, "base_cost") is not None:
                d["cost"] = float(get(body, "base_cost"))
            if get(body, "per_level_extra_cost") is not None:
                d["cost_per_level"] = float(get(body, "per_level_extra_cost"))
        except (TypeError, ValueError):
            pass
        d["infra_speed"] = get(body, "infrastructure_construction_effect") == "yes"
        # construction screen section (countryconstructionsview possible_*_constructions), in file order:
        # facility (special project buildings), province, shared, state; none for what the screen never offers
        # (not buildable, hidden until a tech we don't model, built through naval base modules)
        special = get(body, "special_icon") or ""
        if get(body, "is_buildable") == "no" or get(body, "hide_if_missing_tech") == "yes" or get(body, "modules") is not None:
            section = ""
        elif special.startswith("GFX_specialization_"):
            section = "facility"
        elif isinstance(cap, list) and get(cap, "province_max") is not None:
            section = "province"
        elif isinstance(cap, list) and get(cap, "shares_slots") == "yes":
            section = "shared"
        else:
            section = "state"
        if section:
            d["section"] = section
        if isinstance(cap, list):
            d["shared"] = get(cap, "shares_slots") == "yes"
            if get(cap, "state_max") is not None:
                d["state_max"] = int(float(get(cap, "state_max")))
            if get(cap, "province_max") is not None:
                d["province_max"] = int(float(get(cap, "province_max")))
        defs[name] = d

    categories, category_caps = {}, {}
    for f in sorted((hoi4 / "common" / "state_category").glob("*.txt")):
        for cat, _, body in get(parse_pdx(read_text(f)), "state_categories", []):
            if isinstance(body, list):
                categories[cat] = int(float(get(body, "local_building_slots", 0) or 0))
                # islands cap some buildings below the building's own state_max (air_base 6/4/2, naval_base 8/6/3)
                caps = get(body, "buildings_max_level")
                if isinstance(caps, list):
                    category_caps[cat] = {k: int(float(v)) for k, _, v in caps if k and isinstance(v, str)}

    def start_ok(key):
        try:
            y, m, d = (int(x) for x in key.split("."))
            return (y, m, d) <= (1936, 1, 1)
        except ValueError:
            return False

    def read_buildings(bl, state_b, prov_b):
        for k, _, v in bl:
            if k is None:
                continue
            if isinstance(v, list):
                if k.isdigit():
                    for bk, _, bv in v:
                        lvl = get(bv, "level") if isinstance(bv, list) else bv
                        if bk in defs and lvl is not None:
                            prov_b.setdefault(int(k), {})[bk] = int(float(lvl))
                elif k in defs:
                    lvl = get(v, "level")
                    if lvl is not None:
                        state_b[k] = int(float(lvl))
            elif k in defs:
                try:
                    state_b[k] = int(float(v))
                except ValueError:
                    pass

    # coastal provinces (map/definition.csv: id;r;g;b;type;coastal;terrain;continent), for dockyards and naval bases
    coastal = set()
    for line in read_text(hoi4 / "map" / "definition.csv").splitlines():
        cols = line.split(";")
        if len(cols) > 5 and cols[0].isdigit() and cols[4] == "land" and cols[5] == "true":
            coastal.add(int(cols[0]))

    states = {}
    for f in sorted((hoi4 / "history" / "states").glob("*.txt")):
        st = get(parse_pdx(read_text(f)), "state")
        if not isinstance(st, list):
            continue
        sid = int(get(st, "id", 0))
        hist = get(st, "history", [])
        if not isinstance(hist, list):
            continue
        e = {"category": get(st, "state_category", "") or "", "manpower": int(float(get(st, "manpower", 0) or 0)),
             "owner": get(hist, "owner") or "", "cores": [c for c in get_all(hist, "add_core_of") if isinstance(c, str)]}
        provs = get(st, "provinces", [])
        if any(int(v) in coastal for v in (values(provs) if isinstance(provs, list) else []) if str(v).isdigit()):
            e["coastal"] = True  # a coastal province: dockyards and other only_costal buildings allowed
        res = get(st, "resources", [])
        if isinstance(res, list):
            e["resources"] = {k: int(float(v)) for k, _, v in res if k and isinstance(v, str)}
        vps = {}
        for vp in get_all(hist, "victory_points"):
            vals = values(vp) if isinstance(vp, list) else []
            if len(vals) >= 2:
                vps[int(vals[0])] = int(float(vals[1]))
        if vps:
            e["vps"] = vps
        local = float(get(st, "local_supplies", 0) or 0)
        if local:
            e["local_supplies"] = local  # HOI4 state base supply (x SUPPLY_BASE_MULT in the state's supply pool)
        extra = 0
        for v in get_all(hist, "add_extra_state_shared_building_slots"):
            try:
                extra += int(float(v))
            except (TypeError, ValueError):
                pass
        if extra:
            e["extra_slots"] = extra  # shared slots on top of the category's (Belgium +2)
        sb, pb = {}, {}
        for b in get_all(hist, "buildings"):
            if isinstance(b, list):
                read_buildings(b, sb, pb)
        for k, _, v in hist:
            if k and k[0].isdigit() and "." in k and start_ok(k) and isinstance(v, list):
                if isinstance(get(v, "owner"), str):
                    e["owner"] = get(v, "owner")
                for b in get_all(v, "buildings"):
                    if isinstance(b, list):
                        read_buildings(b, sb, pb)
        if sb:
            e["buildings"] = sb
        if pb:
            e["provincial"] = {str(p): b for p, b in pb.items()}
        e["value"] = round(10 + e["manpower"] / 100000 + sum(e.get("resources", {}).values())
                           + 4 * categories.get(e["category"], 0)
                           + sum(defs.get(k, {}).get("value", 0) * n for k, n in sb.items()), 2)
        if state_centers and int(sid) in state_centers:
            e["center"] = state_centers[int(sid)]
        states[sid] = e

    # small shield flags (GFX_flag_small2) for owners and cores
    (dst / "flags").mkdir(exist_ok=True)
    tags = {e["owner"] for e in states.values()} | {c for e in states.values() for c in e["cores"]}
    nflags = 0
    for tag in sorted(t for t in tags if t):
        img = gui.masked_flag(hoi4, sprites, "GFX_flag_small2", tag, "neutrality")
        if img is not None:
            img.save(dst / "flags" / f"{tag}.png")
            nflags += 1
    print(f"  state view flags: {nflags}")

    doc = {"buildings": defs, "categories": categories, "category_caps": category_caps, "states": states}
    (out / "map" / "hoi4_state_data.json5").write_text(
        "// Generated by hoi4_convert.py from history/states, common/buildings, common/state_category" + chr(10)
        + json.dumps(doc, indent=0) + chr(10), encoding="utf-8")
    print(f"State view: {len(states)} states, {len(defs)} building types")


def convert_supply_strip(hoi4: Path, out: Path):
    """GFX_unitcounter_supply_status_strip (unitcontrol/supply_strip.dds, 3 frames: yellow, orange, red crates) as
    gfx/counters/supply_1..3.png: the low supply mark on a division counter (mapicons.gui low_supply)."""
    dst = out / "gfx" / "counters"
    dst.mkdir(parents=True, exist_ok=True)
    strip = Image.open(hoi4 / "gfx" / "interface" / "unitcontrol" / "supply_strip.dds").convert("RGBA")
    w = strip.width // 3
    for i in range(3):
        strip.crop((i * w, 0, (i + 1) * w, strip.height)).save(dst / f"supply_{i + 1}.png")


def convert_supply_rails(hoi4: Path, out: Path):
    """The supply map mode's railways: HOI4's railway_map_arrow (gfx/maparrows) in its three widths, as
    gfx/map/rails/supply_<thin|medium|thick>.png: the pattern's colours (white rails and ties, dark outline) with the
    mask's shape as alpha (mask alpha x green, mask_outer { 0 1 0 }). Rails along x, like rail_<level>.png.
    Prototype: Tools/prototypes/supply/supply_mapmode.py."""
    src = hoi4 / "gfx" / "maparrows"
    dst = out / "gfx" / "map" / "rails"
    dst.mkdir(parents=True, exist_ok=True)
    for name, suffix in (("thin", "_thin"), ("medium", ""), ("thick", "_thick")):
        mask = np.asarray(Image.open(src / f"railway_mask{suffix}.dds").convert("RGBA"), np.float32)
        pat = np.asarray(Image.open(src / f"railway_pattern{suffix}.dds").convert("RGBA"), np.uint8).copy()
        pat[..., 3] = np.clip(mask[..., 3] * mask[..., 1] / 255, 0, 255).astype(np.uint8)
        Image.fromarray(pat, "RGBA").save(dst / f"supply_{name}.png")


def convert_counter_gfx(hoi4: Path, out: Path, tag_ideology, main_types):
    """On-map division counter sprites (interface/mapicons.gui unit_counter) as PNGs under
    gfx/counters/: plate, ideology corner frames, org/strength bars, a type icon per division main
    type (GFX_unit_<type>_icon_medium_white, pictogram frame), the small flag per country for its
    ruling ideology, and a digit strip for the stack count."""
    from PIL import ImageDraw, ImageFont
    dst = out / "gfx" / "counters"
    (dst / "icons").mkdir(parents=True, exist_ok=True)
    (dst / "flags").mkdir(parents=True, exist_ok=True)
    gi = hoi4 / "gfx" / "interface"

    def frame(im, i, n):
        w = im.width // n
        return im.crop((i * w, 0, (i + 1) * w, im.height))

    Image.open(gi / "onmap_unit_counter.dds").convert("RGBA").save(dst / "plate.png")       # 3 frames
    Image.open(gi / "onmap_unit_counter_selected.dds").convert("RGBA").save(dst / "selected.png")
    Image.open(gi / "onmap_unit_counter_ideology.dds").convert("RGBA").save(dst / "ideology.png")  # 4 frames
    act = Image.open(gi / "onmap_action_icon.dds").convert("RGBA")  # 14 frames
    for name, i in (("defend", 2), ("move", 4), ("attack", 5)):
        frame(act, i, 14).save(dst / f"action_{name}.png")
    # GFX_mapicon_unit_no_orders: the red exclamation mark left of a division without orders
    Image.open(gi / "mapicons" / "mapicon_unit_no_orders.dds").convert("RGBA").save(dst / "no_orders.png")
    convert_supply_strip(hoi4, out)
    for kind in ("org", "str"):
        bar = Image.open(gi / "mapicons" / f"mapicon_bar_{kind}_sprite.dds").convert("RGBA")
        bar.rotate(90, expand=True).save(dst / f"bar_{kind}.png")  # 17x3, full end on the left

    import hoi4_gui as gui
    sprites = gui.load_sprites(hoi4)
    for t in sorted(main_types):
        img = unit_icon(hoi4, sprites, t, suffix="_icon_medium_white") or \
            unit_icon(hoi4, sprites, "infantry", suffix="_icon_medium_white")
        if img is not None:
            img.save(dst / "icons" / f"{t}.png")
        else:
            print(f"  counter icon missing for {t}")

    flags = hoi4 / "gfx" / "flags" / "small"
    for tag, ideology in sorted(tag_ideology.items()):
        for cand in (f"{tag}_{ideology}.tga", f"{tag}.tga", f"{tag}_neutrality.tga"):
            if (flags / cand).exists():
                Image.open(flags / cand).convert("RGBA").save(dst / "flags" / f"{tag}.png")
                break

    # Digits 0-9 in fixed 9x14 cells (white, alpha = glyph), drawn by the game in the count box
    try:
        font = ImageFont.truetype("arialbd.ttf", 13)
    except OSError:
        font = ImageFont.load_default()
    cw, ch = 9, 14
    digits = Image.new("RGBA", (cw * 10, ch), (0, 0, 0, 0))
    d = ImageDraw.Draw(digits)
    for i in range(10):
        d.text((i * cw + cw / 2, ch / 2), str(i), font=font, fill=(255, 255, 255, 255), anchor="mm")
    digits.save(dst / "digits.png")
    countries_j = ", ".join(f"{t}: {json.dumps(i)}" for t, i in sorted(tag_ideology.items()))
    (dst / "counters.json5").write_text(
        "{\n  // Generated: HOI4 division counter sprites (layout from interface/mapicons.gui)\n"
        f"  ideology_frames: {json.dumps(COUNTER_IDEOLOGIES)},\n"
        f"  digit_cell: [{cw}, {ch}],\n"
        f"  countries: {{ {countries_j} }}\n}}\n",
        encoding="utf-8")
    print(f"Counters: {len(list((dst / 'icons').glob('*.png')))} icons, {len(list((dst / 'flags').glob('*.png')))} flags")


BORDER_TEXTURES = ["country", "state", "province", "sea", "sea_region", "impassable"]


def load_terrain_combat(hoi4: Path):
    """Land combat fields per terrain category from common/terrain/00_terrain.txt: combat_width,
    combat_support_width (added per extra attack direction) and units.attack (the attacker's penalty)."""
    root = parse_pdx(read_text(hoi4 / "common" / "terrain" / "00_terrain.txt"))
    result = {}
    for block in get_all(root, "categories"):
        if not isinstance(block, list):
            continue
        for key, _, entry in block:
            if not isinstance(entry, list) or get(entry, "is_water") == "yes":
                continue
            fields = {}
            for f in ("combat_width", "combat_support_width"):
                v = get(entry, f)
                if v is not None:
                    fields[f] = float(v)
            units = get(entry, "units")
            if isinstance(units, list) and get(units, "attack") is not None:
                fields["attack"] = float(get(units, "attack"))
            # added to the hub supply lost per province step into this terrain (urban: negative, it loses less)
            if get(entry, "supply_flow_penalty_factor") is not None:
                fields["supply_flow_penalty_factor"] = float(get(entry, "supply_flow_penalty_factor"))
            result[key] = fields
    print("Terrain combat: " + ", ".join(f"{k} {v}" for k, v in result.items()))
    return result


RIVER_CROSSING_MIN_PIXELS = 4  # Tools/prototypes/rivers/river_crossings.py


def write_river_crossings(hoi4: Path, out: Path, land):
    """map/hoi4_river_crossings.json5: pairs of neighbouring land provinces with a river between them,
    [a, b, size] with size 1 small, 2 large (HOI4 RIVER_CROSSING_PENALTY / _LARGE). HOI4 draws rivers along
    province borders: a river pixel whose 3x3 window holds land of both provinces counts for the pair, and
    a pair needs RIVER_CROSSING_MIN_PIXELS of them (a river flowing through a border touches it at one or two).
    rivers.bmp palette 0..11 is river (3..6 small, 7..11 large: defines RIVER_SMALL_STOP_INDEX = 6,
    RIVER_LARGE_STOP_INDEX = 11); large when most of the pair's river pixels are. land: province id per
    pixel, 0 for water and unknown. Prototype: Tools/prototypes/rivers/river_crossings.py."""
    a = np.asarray(Image.open(hoi4 / "map" / "rivers.bmp")).astype(np.int16)
    if a.shape != land.shape:
        print(f"WARNING: rivers.bmp {a.shape} != provinces {land.shape}; no river crossings")
        return
    H, W = land.shape
    ys, xs = np.nonzero((a >= 0) & (a <= 11))
    big = a[ys, xs] >= 7
    win = np.stack([land[np.clip(ys + dy, 0, H - 1), np.clip(xs + dx, 0, W - 1)]
                    for dy in (-1, 0, 1) for dx in (-1, 0, 1)], axis=1)
    counts, large = {}, {}
    for row, lg in zip(win, big):
        u = np.unique(row[row > 0])
        for i in range(len(u)):
            for j in range(i + 1, len(u)):
                k = (int(u[i]), int(u[j]))
                counts[k] = counts.get(k, 0) + 1
                large[k] = large.get(k, 0) + int(lg)
    pairs = sorted((k[0], k[1], 2 if large[k] * 2 > n else 1)
                   for k, n in counts.items() if n >= RIVER_CROSSING_MIN_PIXELS)
    lines = ["// Generated by Tools/hoi4_convert.py: neighbouring land provinces with a river between them,",
             "// [a, b, size], size 1 small river, 2 large river.", "{", "  crossings: ["]
    lines += [f"    [{x}, {y}, {z}]," for x, y, z in pairs]
    lines += ["  ]", "}"]
    (out / "map" / "hoi4_river_crossings.json5").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"River crossings: {len(pairs)} ({sum(1 for p in pairs if p[2] == 2)} large)")


def graphical_terrain_slots(hoi4: Path):
    """terrain.bmp palette index -> atlas tile (0-15) from common/terrain/00_terrain.txt 'terrain' block."""
    root = parse_pdx(read_text(hoi4 / "common" / "terrain" / "00_terrain.txt"))
    slots = {}
    for block in get_all(root, "terrain"):
        if not isinstance(block, list):
            continue
        for _, _, entry in block:
            if not isinstance(entry, list):
                continue
            col, tex = get(entry, "color"), get(entry, "texture")
            if not isinstance(col, list) or tex is None:
                continue
            for c in values(col):
                slots[int(c)] = int(tex)
    return slots


def convert_gfx(hoi4: Path, out: Path, water_px):
    gfx = out / "gfx" / "map"
    gfx.mkdir(parents=True, exist_ok=True)

    # Terrain index: R = atlas tile per map pixel, WATER_SLOT for sea/lake provinces;
    # G = nearest land tile, B = coast height field (see coast_channels)
    slots = graphical_terrain_slots(hoi4)
    lut = np.zeros(256, dtype=np.uint8)
    for pal, tex in slots.items():
        if 0 <= pal < 256:
            lut[pal] = tex if tex < 16 else WATER_SLOT
    terrain_bmp = Image.open(hoi4 / "map" / "terrain.bmp")
    if terrain_bmp.mode != "P":
        sys.exit(f"terrain.bmp expected palette mode, got {terrain_bmp.mode}")
    index = lut[np.asarray(terrain_bmp, dtype=np.uint8)]
    if index.shape == water_px.shape:
        index[water_px] = WATER_SLOT
    nearest_land, coast = coast_channels(hoi4, index)
    Image.fromarray(np.dstack([index, nearest_land, coast, coast_distance(coast)]), "RGBA").save(gfx / "terrain_index.png", compress_level=1)
    print(f"Terrain index: {len(slots)} graphical terrains")

    for src, dst in GFX_TEXTURES:
        Image.open(hoi4 / "map" / "terrain" / src).convert("RGBA").save(gfx / dst, compress_level=1)

    # World normal at native (half) resolution; sampled bilinearly by the shader
    Image.open(hoi4 / "map" / "world_normal.bmp").convert("RGB").save(gfx / "world_normal.png", compress_level=1)

    convert_rivers(hoi4, out, gfx, water_px)
    convert_water(hoi4, gfx)

    # Border strip textures (U along the border, V across). Only the full-size _0 version:
    # _1/_2 are the same profile at lower resolution, which mipmapping already covers.
    borders = gfx / "borders"
    borders.mkdir(parents=True, exist_ok=True)
    for kind in BORDER_TEXTURES:
        Image.open(hoi4 / "map" / "terrain" / f"border_{kind}_0.dds").convert("RGBA").save(borders / f"{kind}.png")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=DEFAULT_HOI4)
    ap.add_argument("--out", default=str(DEFAULT_OUT))
    a = ap.parse_args()
    convert(Path(a.hoi4), Path(a.out))
