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
    tj = ["// Generated from HOI4 province terrain types (definition.csv column 7)", "{", "  categories: {"]
    entries = []
    for k, (c, cost, water) in TERRAIN.items():
        e = f"    {k}: {{\n      color: [{c[0]}, {c[1]}, {c[2]}],\n      movement_cost: {cost}"
        if water:
            e += ",\n      is_water: true"
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
                }
    return subs


def convert_oob(hoi4: Path, out: Path, existing_tags, ruling_party):
    """HOI4 1936 land orders of battle -> history/units/oob_1936.json5 (GAME data: templates,
    divisions with resolved names, regiment stats) plus one engine unit type per division main
    regiment type (units/<type>.json5, the format the StarterKit unit system loads).

    Uses each country's base-game land OOB (the no-DLC branch; DLC variants only add officers
    and equipment variants). Naval and air OOBs are not converted. Countries that own no land
    at the start (releasables) are skipped."""
    names = load_division_name_groups(hoi4)
    subs = load_sub_units(hoi4)
    countries = {}
    used_subs = set()
    main_types = set()
    for f in sorted((hoi4 / "history" / "countries").glob("*.txt")):
        tag = f.name[:3]
        if tag not in existing_tags:
            continue
        found = []
        collect_oob_names(parse_pdx(read_text(f)), found)
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
            countries[tag] = {"ideology": ruling_party.get(tag) or "neutrality",
                              "templates": {k: {kk: vv for kk, vv in v.items() if kk != "names_group"}
                                            for k, v in templates.items()},
                              "divisions": divisions}

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
        lines += ["      ],", "    },"]
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
    print(f"Armies {OOB_START}: {total} divisions in {len(countries)} countries, "
          f"{sum(len(c['templates']) for c in countries.values())} templates, unit types {sorted(main_types)}")
    return countries, main_types


COUNTER_IDEOLOGIES = ["democratic", "communism", "fascism", "neutrality"]  # frame order of the ideology sprite


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
