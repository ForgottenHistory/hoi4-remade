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
          "// attack and breakthrough modifier) are GAME fields from common/terrain/00_terrain.txt.",
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
    oob_countries, main_types = convert_oob(hoi4, out, {o for o, _ in province_owner.values()}, ruling_party)
    convert_counter_gfx(hoi4, out, {t: c["ideology"] for t, c in oob_countries.items()}, main_types)
    convert_unit_panel(hoi4, out, main_types)
    convert_army_cards(hoi4, out)
    convert_battleplan_gfx(hoi4, out)
    convert_peace_gfx(hoi4, out)
    convert_production_gfx(hoi4, out, oob_countries)
    convert_construction_gfx(hoi4, out)
    convert_front_line(hoi4, out)
    convert_state_view(hoi4, out, state_centers)
    convert_move_arrow(hoi4, out)
    write_country_stats(out, tags, country_stats, owner_manpower, ruling_party)
    write_country_economy(hoi4, out, tags)
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


COMBAT_STATS = ("soft_attack", "hard_attack", "defense", "breakthrough", "hardness", "armor_value", "ap_attack")


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
                subs[name] = {
                    "abbreviation": get(u, "abbreviation", name[:3].upper()),
                    "group": get(u, "group", "infantry"),
                    "icon": get(u, "map_icon_category", "infantry"),
                    "priority": int(num("priority")),
                    "combat_width": num("combat_width"),
                    "max_strength": num("max_strength"),
                    "max_organisation": num("max_organisation"),
                    "manpower": int(num("manpower")),
                    "speed": max(speeds) if speeds else 4.0,
                    "soft_attack": round(stat("soft_attack"), 2),
                    "hard_attack": round(stat("hard_attack"), 2),
                    "defense": round(stat("defense"), 2),
                    "breakthrough": round(stat("breakthrough"), 2),
                    "hardness": round(max((e.get("hardness", 0.0) for e in eq), default=0.0), 3),
                    "armor": round(max((e.get("armor_value", 0.0) for e in eq), default=0.0), 1),
                    "piercing": round(max((e.get("ap_attack", 0.0) for e in eq), default=0.0), 1),
                    "need": need,  # equipment archetype -> amount (HOI4 `need`)
                }
    return subs


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


def available_equipment(country_root, equipment, tech_equipment, land_archetypes):
    """What a country can build at the start, per land archetype: the newest buildable equipment that its history's
    set_technology unlocks, or that no technology unlocks (always available). {archetype: {"equipment", "cost"}}.
    Tanks: a bare chassis is not buildable here (the 1936 lines build designs, which cost the modules too), and a
    design (light_tank_equipment_N) needs the technology of its chassis (light_tank_chassis_N)."""
    known = set()
    for block in collect_blocks(country_root, "set_technology", []):
        for k, _, v in block:
            if isinstance(k, str) and str(v) != "0":
                known.add(k)
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
    loc = load_localisation(hoi4 / "localisation" / "english")
    def has_texture(s):
        tex = s.get("texture")
        if not tex:
            return False
        f = hoi4 / tex
        return any(f.with_suffix(ext).exists() for ext in (f.suffix, ".dds", ".tga", ".png"))
    sprite_names = {n for n, s in gui.load_sprites(hoi4).items() if has_texture(s)}  # pictures that exist
    land_archetypes = {a for s in subs.values() for a in s["need"]}
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
        for t in get_all(root, "division_template"):
            regs = [k for k, _, _ in (get(t, "regiments") or []) if k in subs]
            sups = [k for k, _, _ in (get(t, "support") or []) if k in subs]
            if not regs:
                continue
            # HOI4 shows a division by its highest-priority regiment
            main = max(regs, key=lambda r: subs[r]["priority"])
            templates[get(t, "name")] = {"regiments": regs, "support": sups, "main": main,
                                         "names_group": get(t, "division_names_group")}

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
                used_subs.update(t["regiments"] + t["support"])
                main_types.add(t["main"])
        if divisions:
            lines_, stock = starting_equipment(hoi4, root, country_root, equipment, land_archetypes)
            buildable = available_equipment(country_root, equipment, tech_equipment, land_archetypes)
            for p in lines_:  # what a 1936 line builds is buildable, whatever the technology check says
                buildable[p["archetype"]] = {"equipment": p["equipment"], "cost": p["cost"]}
            describe_equipment(tag, buildable, equipment, tech_equipment, loc, sprite_names)
            countries[tag] = {"ideology": ruling_party.get(tag) or "neutrality",
                              "equipment": buildable,
                              "templates": {k: {kk: vv for kk, vv in v.items() if kk != "names_group"}
                                            for k, v in templates.items()},
                              "divisions": divisions, "production": lines_, "stockpile": stock}

    # Only the equipment the converted land divisions use (sub-units also cover ships and planes)
    land_archetypes = {a for name in used_subs for a in subs[name]["need"]}
    for c in countries.values():
        c["production"] = [p for p in c["production"] if p["archetype"] in land_archetypes]
        c["stockpile"] = {a: n for a, n in c["stockpile"].items() if a in land_archetypes}
        c["equipment"] = {a: e for a, e in sorted(c["equipment"].items()) if a in land_archetypes}

    def j(v):
        return json.dumps(v, ensure_ascii=False)

    lines = ["{", f"  // Generated from HOI4 history/units ({OOB_START} land OOBs) and common/units",
             f'  date: "{OOB_START}.1.1",', "  sub_units: {"]
    for name in sorted(used_subs):
        lines.append(f"    {name}: {j(subs[name])},")
    lines += ["  },", "  countries: {"]
    for tag, c in sorted(countries.items()):
        lines.append(f"    {tag}: {{")
        lines.append(f"      ideology: {j(c['ideology'])},")
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

    # Engine unit types (StarterKit format), one per division main type. speed = days per province.
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
    return countries, main_types


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
ECONOMY_KEYS = ("consumer_goods_expected_value", "consumer_goods_factor", "production_speed_buildings_factor")
BUILDING_SPEED = re.compile(r"production_speed_(\w+)_factor")  # production_speed_<building>_factor


def economy_key(k):
    return isinstance(k, str) and (k in ECONOMY_KEYS or BUILDING_SPEED.fullmatch(k) is not None)


def load_idea_modifiers(hoi4: Path):
    """{idea: {"category", "law", "default", "mods"}} from common/ideas (only ECONOMY_KEYS in mods)."""
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
                    if economy_key(k):
                        try:
                            mods[k] = mods.get(k, 0.0) + float(v)
                        except (TypeError, ValueError):
                            pass
                ideas[name] = {"category": cat, "law": law, "default": get(idea, "default") == "yes", "mods": mods}
    return ideas


def load_tech_modifiers(hoi4: Path):
    """{technology: {key: value}} for ECONOMY_KEYS set directly in the technology."""
    techs = {}
    for f in sorted((hoi4 / "common" / "technologies").glob("*.txt")):
        for key, _, block in parse_pdx(read_text(f)):
            if key != "technologies" or not isinstance(block, list):
                continue
            for name, _, t in block:
                if not isinstance(t, list):
                    continue
                mods = {}
                for k, _, v in t:
                    if economy_key(k):
                        try:
                            mods[k] = float(v)
                        except (TypeError, ValueError):
                            pass
                if mods:
                    techs[name] = mods
    return techs


def load_building_techs(hoi4: Path):
    """{building: {technology: level}}: technologies that enable a building up to a level (enable_building).
    A building listed here needs one of them; without it HOI4 greys the building out."""
    gated = {}
    for f in sorted((hoi4 / "common" / "technologies").glob("*.txt")):
        for key, _, block in parse_pdx(read_text(f)):
            if key != "technologies" or not isinstance(block, list):
                continue
            for name, _, tech in block:
                if not isinstance(tech, list):
                    continue
                for k, _, v in tech:
                    if k == "enable_building" and isinstance(v, list) and get(v, "building"):
                        try:
                            gated.setdefault(get(v, "building"), {})[name] = int(float(get(v, "level", 1)))
                        except (TypeError, ValueError):
                            pass
    return gated


def history_values(items, key, out):
    """Every value of KEY (a single value or a block's values) at any depth, skipping else branches and date
    blocks after the start, like collect_blocks."""
    for k, _, v in (items if isinstance(items, list) else []):
        if isinstance(k, str) and k.lower() in ("else", "else_if"):
            continue
        if isinstance(k, str) and re.fullmatch(r"\d+\.\d+\.\d+", k) and \
                tuple(int(x) for x in k.split(".")) > (int(OOB_START), 1, 1):
            continue
        if k == key:
            out.extend(values(v) if isinstance(v, list) else [v])
        elif isinstance(v, list):
            history_values(v, key, out)
    return out


def write_country_economy(hoi4: Path, out: Path, tags):
    """common/hoi4_country_economy.json5: per country the start's consumer goods and construction speed, summed from
    its starting ideas (laws it doesn't set take the category's default law) and technologies:
    consumer_goods (the economy law's expected share), consumer_goods_factor (other ideas, stacking multiplicatively),
    construction_speed (production_speed_buildings_factor) and per building speed (production_speed_<b>_factor)."""
    ideas = load_idea_modifiers(hoi4)
    techs = load_tech_modifiers(hoi4)
    gated = load_building_techs(hoi4)
    defaults = {}
    for name, i in sorted(ideas.items()):
        # the generic default law of each law category, not a country's own law (ETH_chitet_law)
        if i["law"] and i["default"] and not re.match(r"[A-Z]{3}_", name):
            defaults.setdefault(i["category"], name)
    lines = ["// Generated by hoi4_convert.py from history/countries, common/ideas and common/technologies", "{"]
    for tag in sorted(tags):
        files = sorted((hoi4 / "history" / "countries").glob(f"{tag} - *.txt"))
        if not files:
            continue
        root = parse_pdx(read_text(files[0]))
        own = [i for i in history_values(root, "add_ideas", []) if isinstance(i, str) and i in ideas]
        cats = {ideas[i]["category"] for i in own if ideas[i]["law"]}
        own += [law for cat, law in defaults.items() if cat not in cats]
        known = set()
        for block in collect_blocks(root, "set_technology", []):
            known |= {k for k, _, v in block if isinstance(k, str) and str(v) != "0"}
        tech = sorted(k for k in known if k in techs)
        # tech-gated buildings: the most levels the country's 1936 technologies allow (0 = not researched)
        unlocked = {b: max([lvl for tn, lvl in by.items() if tn in known] or [0]) for b, by in sorted(gated.items())}
        total = {k: 0.0 for k in ECONOMY_KEYS}
        cg_factor = 1.0
        for src in [ideas[i]["mods"] for i in own] + [techs[t] for t in tech]:
            for k, v in src.items():
                if k == "consumer_goods_factor":
                    cg_factor *= 1.0 + v
                else:
                    total[k] = total.get(k, 0.0) + v
        speed = {}
        for k, v in total.items():
            m = BUILDING_SPEED.fullmatch(k)
            if m and m.group(1) not in ("buildings", "buildings_powered", "facility") and round(v, 4) != 0:
                speed[m.group(1)] = round(v, 4)
        e = {"consumer_goods": round(total["consumer_goods_expected_value"], 4),
             "consumer_goods_factor": round(cg_factor - 1.0, 4),
             "construction_speed": round(total["production_speed_buildings_factor"], 4),
             "speed": speed,
             "laws": sorted(i for i in own if ideas[i]["law"]),
             "unlocked": unlocked}
        lines.append(f"  {tag}: {json.dumps(e)},")
    lines.append("}")
    (out / "common" / "hoi4_country_economy.json5").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Country economy: {len(lines) - 3} countries")


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
    "production_button", "production_button_glow", "construction_button",
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
}


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
    icon_files = {}
    for name, tex in re.findall(r'name\s*=\s*"GFX_unit_(\w+?)_icon_medium"\s*textureFile\s*=\s*"?([^"\s}]+)',
                                read_text(hoi4 / "interface" / "subuniticons.gfx")):
        icon_files.setdefault(name, tex)
    for t in sorted(main_types):
        tex = icon_files.get(t) or icon_files.get("infantry")
        img = gui.load_image(hoi4, tex)
        if img is None:
            print(f"  unit panel icon missing for {t}")
            continue
        img = img.crop((0, 0, img.width // 2, img.height))  # frame 0 of 2
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


# State view (interface/countrystateview.gui) sprites: name -> GFX sprite
STATEVIEW_SPRITES = {
    "bg": "GFX_ww_stateview_bg", "slot_bg": "GFX_build_slot_bg", "no_building": "GFX_no_building",
    "status_overlay": "GFX_building_status_overlay", "entry_bg": "GFX_ww_building_standing_entry_stateview",
    "text_bg": "GFX_generic_text_bg_48", "population": "GFX_population_icon",
    "vp_bg": "GFX_victorypoint_stateview_bg", "close": "GFX_closebutton_small",
    "buildings": "GFX_buildings_strip", "resources": "GFX_resources_strip",
    "flag_frame": "GFX_diplo_countrylist_flag_frame", "flag_frame_thin": "GFX_small_flag_frame_thin",
}


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
    for kind in ("org", "str"):
        bar = Image.open(gi / "mapicons" / f"mapicon_bar_{kind}_sprite.dds").convert("RGBA")
        bar.rotate(90, expand=True).save(dst / f"bar_{kind}.png")  # 17x3, full end on the left

    icon_files = {}
    for name, tex in re.findall(r'name\s*=\s*"GFX_unit_(\w+?)_icon_medium_white"\s*textureFile\s*=\s*"([^"]+)"',
                                read_text(hoi4 / "interface" / "subuniticons.gfx")):
        icon_files.setdefault(name, tex)
    for t in sorted(main_types):
        tex = icon_files.get(t) or icon_files.get("infantry")
        if tex and (hoi4 / tex).exists():
            frame(Image.open(hoi4 / tex).convert("RGBA"), 0, 2).save(dst / "icons" / f"{t}.png")
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
    Image.fromarray(np.dstack([index, nearest_land, coast]), "RGB").save(gfx / "terrain_index.png", compress_level=1)
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
