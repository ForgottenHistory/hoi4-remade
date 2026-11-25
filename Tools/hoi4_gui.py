"""HOI4 (Clausewitz) interface helpers: sprite definitions (interface/*.gfx), .gui layout and sprite
images. Used by hoi4_convert.py (top bar export) and Tools/prototypes/topbar.

Layout rules:
- An element's `position` is an offset from the anchor point picked by its `Orientation`
  (UPPER_LEFT default, UPPER_RIGHT, LOWER_LEFT, LOWER_RIGHT, CENTER, CENTER_UP, CENTER_DOWN) on
  its parent container's rectangle. The element's own top-left corner goes there.
- Container `size` may be a percentage ("100%%") of the parent.
- Sprites with noOfFrames are horizontal strips; frame 0 is the normal state.
- Elements far off-screen (position -5000 or less) are hidden.
"""
import re
from pathlib import Path

from PIL import Image

from hoi4_convert import parse_pdx, read_text


def kv(items, key):
    """Case-insensitive get on a parsed block."""
    for k, _, v in items:
        if k is not None and k.lower() == key.lower():
            return v
    return None


def load_sprites(hoi4: Path):
    """name -> dict(texture, frames, mask, overlay, kind) from every interface/*.gfx."""
    sprites = {}
    for f in sorted((hoi4 / "interface").glob("*.gfx")):
        root = parse_pdx(read_text(f))
        for _, _, block in root:
            if not isinstance(block, list):
                continue
            for k, _, s in block:
                if not isinstance(s, list) or k is None:
                    continue
                name = kv(s, "name")
                if not name:
                    continue
                tex = kv(s, "texturefile")
                border = kv(s, "borderSize")
                sprites[name] = {
                    "border": (int(float(kv(border, "x") or 0)), int(float(kv(border, "y") or 0)))
                    if isinstance(border, list) else None,
                    "texture": tex.replace("//", "/") if tex else None,
                    "frames": int(kv(s, "noOfFrames") or 1),
                    "mask": kv(s, "textureFile2"),
                    "overlay": kv(s, "textureFile1"),
                    "kind": k,
                }
    return sprites


_image_cache = {}


def load_image(hoi4: Path, rel):
    """RGBA image of a file under the HOI4 install (tries .dds/.tga/.png when the named one is missing)."""
    if rel is None:
        return None
    rel = rel.replace("//", "/")
    if rel in _image_cache:
        return _image_cache[rel]
    p = hoi4 / rel
    if not p.exists():
        for ext in (".dds", ".tga", ".png"):
            q = p.with_suffix(ext)
            if q.exists():
                p = q
                break
    img = Image.open(p).convert("RGBA") if p.exists() else None
    _image_cache[rel] = img
    return img


def sprite_frames(hoi4: Path, sprites, name):
    """All frames of a sprite (list of RGBA images), or [] when it can't be found."""
    s = sprites.get(name)
    if s is None:
        return []
    img = load_image(hoi4, s["texture"])
    if img is None:
        return []
    n = max(1, s["frames"])
    fw = img.width // n
    return [img.crop((i * fw, 0, (i + 1) * fw, img.height)) for i in range(n)]


def flag_file(hoi4: Path, tag, ideology, size_dir=""):
    """gfx/flags[/size_dir]/TAG_ideology.tga, falling back to TAG.tga."""
    base = hoi4 / "gfx" / "flags"
    if size_dir:
        base = base / size_dir
    for cand in (f"{tag}_{ideology}.tga", f"{tag}.tga"):
        if (base / cand).exists():
            return base / cand
    return None


def masked_flag(hoi4: Path, sprites, name, tag, ideology):
    """A masked-flag sprite (e.g. GFX_shield_medium): the country flag cut by textureFile2, with
    textureFile1 drawn on top. None when the sprite or flag is missing."""
    s = sprites.get(name)
    if s is None or not s["mask"]:
        return None
    mask = load_image(hoi4, s["mask"])
    overlay = load_image(hoi4, s["overlay"])
    fpath = flag_file(hoi4, tag, ideology)
    if mask is None or fpath is None:
        return None
    w, h = mask.size
    img = Image.new("RGBA", (w, h))
    f = Image.open(fpath).convert("RGBA").resize((w, h), Image.LANCZOS)
    f.putalpha(mask.getchannel("A"))
    img.alpha_composite(f)
    if overlay is not None:
        img.alpha_composite(overlay.resize((w, h), Image.LANCZOS))
    return img


def num(v, default=0.0):
    """A gui number, forgiving the files' typos (countrytechtreeview.gui has "x = 275k"): its leading number."""
    if v is None:
        return default
    m = re.match(r"\s*-?\d+(\.\d+)?", str(v))
    return float(m.group(0)) if m else default


def _anchor(rect, orientation):
    x, y, w, h = rect
    o = (orientation or "UPPER_LEFT").upper().strip('"')
    ax = {"UPPER_LEFT": x, "LOWER_LEFT": x, "UPPER_RIGHT": x + w, "LOWER_RIGHT": x + w,
          "CENTER": x + w / 2, "CENTER_UP": x + w / 2, "CENTER_DOWN": x + w / 2}.get(o, x)
    ay = {"UPPER_LEFT": y, "UPPER_RIGHT": y, "CENTER_UP": y, "LOWER_LEFT": y + h, "LOWER_RIGHT": y + h,
          "CENTER_DOWN": y + h, "CENTER": y + h / 2}.get(o, y)
    return ax, ay


def _size(items, parent):
    sz = kv(items, "size")
    if not isinstance(sz, list):
        return 0.0, 0.0

    def dim(key, full):
        v = kv(sz, key)
        if v is None:
            return 0.0
        v = str(v)
        return full * num(v.rstrip("%")) / 100 if v.endswith("%") else num(v)

    return dim("width", parent[2]), dim("height", parent[3])


def layout(items, parent, out, depth=0, listing=False):
    """Lay out a container's children on the parent rect (x, y, w, h); appends
    (kind, name, x, y, block) for every visible element, containers included."""
    for k, _, v in items:
        if not isinstance(v, list) or k is None or not k.lower().endswith("type"):
            continue
        name = kv(v, "name")
        pos = kv(v, "position")
        px = num(kv(pos, "x")) if isinstance(pos, list) else 0.0
        py = num(kv(pos, "y")) if isinstance(pos, list) else 0.0
        ax, ay = _anchor(parent, kv(v, "Orientation"))
        x, y = ax + px, ay + py
        if listing:
            print("  " * depth + f"{k[:-4]:<20} {name:<32} ({px:g},{py:g}) {kv(v, 'Orientation') or ''} "
                  f"{kv(v, 'spriteType') or kv(v, 'quadTextureSprite') or ''}")
        if px <= -5000 or py <= -5000:
            continue
        out.append((k, name, x, y, v))
        if k == "containerWindowType":
            w, h = _size(v, parent)
            layout(v, (x, y, w, h), out, depth + 1, listing)


def resolve_constants(items, consts=None):
    """Replace @name values with the file's `@name = value` constants (in place, nested blocks too)."""
    if consts is None:
        consts = {k: v for k, _, v in items if isinstance(k, str) and k.startswith("@")}
    for i, (k, op, v) in enumerate(items):
        if isinstance(v, list):
            resolve_constants(v, consts)
        elif isinstance(v, str) and v.startswith("@") and v in consts:
            items[i] = (k, op, consts[v])
    return items


def load_gui(hoi4: Path, gui_file):
    """interface/<gui_file> parsed, with its @constants resolved."""
    return resolve_constants(parse_pdx(read_text(hoi4 / "interface" / gui_file)))


def window(hoi4: Path, gui_file, window_name):
    """The named top-level containerWindowType block of interface/<gui_file>."""
    gui = load_gui(hoi4, gui_file)
    for k, _, v in kv(gui, "guiTypes") or []:
        if k == "containerWindowType" and kv(v, "name") == window_name:
            return v
    raise KeyError(f"{window_name} not in {gui_file}")


def layout_window(win, screen_w, screen_h, listing=False):
    """Lay out a top-level window on a screen; returns {name: (kind, x, y, block)}."""
    pos = kv(win, "position")
    tx = float(kv(pos, "x") or 0) if isinstance(pos, list) else 0.0
    ty = float(kv(pos, "y") or 0) if isinstance(pos, list) else 0.0
    out = []
    layout(win, (tx, ty, screen_w, screen_h), out, listing=listing)
    return {name: (k, x, y, v) for k, name, x, y, v in out}
