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
import colorsys
import os
import re
import shutil
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
def convert(hoi4: Path, out: Path):
    hmap = hoi4 / "map"
    if not (hmap / "provinces.bmp").exists():
        sys.exit(f"provinces.bmp not found under {hmap}")

    if out.exists():
        # Preserve nothing: caches (.pixels/.adjacency) are tied to the old images anyway.
        shutil.rmtree(out)
    (out / "map").mkdir(parents=True)

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
    vp_names = {}         # pid -> name
    for f in sorted((hoi4 / "history" / "states").glob("*.txt")):
        root = parse_pdx(read_text(f))
        st = get(root, "state")
        if not isinstance(st, list):
            continue
        sid = int(get(st, "id", 0))
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
    for f in (hoi4 / "history" / "countries").glob("*.txt"):
        tag = f.name[:3]
        root = parse_pdx(read_text(f))
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

    # HOI4 visual style data for the GAME terrain shader
    shutil.copy2(Path(__file__).with_name("hoi4_visual_style.json5"), out / "map" / "visual_style.json5")

    print(f"Done -> {out}")


WATER_SLOT = 255  # terrain index value for sea/lake pixels (rendered as water by the GAME shader)

# DDS -> PNG copies for the GAME terrain shader: (source under HOI4/map/terrain, output name)
GFX_TEXTURES = [
    ("atlas0.dds", "atlas_diffuse.png"),             # 4x4 tiles, rgb diffuse, a gloss
    ("atlas_normal0.dds", "atlas_normal.png"),       # 4x4 tiles, rgb normal (.rbg), a spec
    ("colormap_rgb_cityemissivemask_a.dds", "colormap.png"),  # land tint, a city lights mask
    ("colormap_water_0.dds", "colormap_water.png"),  # sea tint
    ("underwater_terrain_0.dds", "underwater.png"),  # tiled sea floor
]


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

    # Terrain index: atlas tile per map pixel (R=G=B), WATER_SLOT for sea/lake provinces
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
    Image.fromarray(np.repeat(index[..., None], 3, axis=2), "RGB").save(gfx / "terrain_index.png", compress_level=1)
    print(f"Terrain index: {len(slots)} graphical terrains")

    for src, dst in GFX_TEXTURES:
        Image.open(hoi4 / "map" / "terrain" / src).convert("RGBA").save(gfx / dst, compress_level=1)

    # World normal at native (half) resolution; sampled bilinearly by the shader
    Image.open(hoi4 / "map" / "world_normal.bmp").convert("RGB").save(gfx / "world_normal.png", compress_level=1)

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
