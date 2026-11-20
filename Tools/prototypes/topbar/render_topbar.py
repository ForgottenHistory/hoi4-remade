"""Prototype: lay out HOI4's top bar (interface/topbar.gui) from the game's own sprites and render it
to topbar.png, to check the Clausewitz GUI layout rules before porting them to Unity.

Layout rules (Clausewitz GUI):
- An element's `position` is an offset from the anchor point picked by its `Orientation`
  (UPPER_LEFT default, UPPER_RIGHT, LOWER_LEFT, LOWER_RIGHT, CENTER, CENTER_UP, CENTER_DOWN) on
  its parent container's rectangle. The element's own top-left corner goes there.
- Container `size` may be a percentage ("100%%") of the parent.
- Sprites with noOfFrames are horizontal strips; frame 0 is the normal state.
- Elements far off-screen (position -9000) are hidden.

Usage: python render_topbar.py [--hoi4 <install>] [--tag GER] [--list]
"""
import argparse
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

sys.path.insert(0, str(Path(__file__).resolve().parents[2]))
import hoi4_convert as hc  # noqa: E402

DEFAULT_HOI4 = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV")
SCREEN_W, SCREEN_H = 1920, 140

# Shown in our top bar. Everything else in topbar.gui (lobby, music, decisions, alerts, ...) is
# left out until we have something behind it.
KEEP = {
    "background", "player_flag",
    "pol_power_value_bg", "pol_power_icon", "pol_power",
    "sability_value_bg", "stability_icon", "stability",
    "war_support_value_bg", "war_support_icon", "war_support",
    "manpower_value_bg", "manpower_icon", "manpower",
    "time_control_window", "pause_button_date", "button_speeddown", "button_speedup",
    "button_speedstep1", "button_speedstep2", "button_speedstep3", "button_speedstep4", "button_speedstep5",
    "DateText", "menu_button",
}
IDEOLOGY = "fascism"
PAUSED = False
SAMPLE_TEXT = {"pol_power": "52", "stability": "68%", "war_support": "32%", "manpower": "1.25M",
               "DateText": "12:00, 1 January 1936"}


def kv(items, key):
    """Case-insensitive get."""
    for k, _, v in items:
        if k is not None and k.lower() == key.lower():
            return v
    return None


def load_sprites(hoi4: Path):
    """name -> dict(texture, frames, mask, overlay) from every interface/*.gfx."""
    sprites = {}
    for f in sorted((hoi4 / "interface").glob("*.gfx")):
        root = hc.parse_pdx(hc.read_text(f))
        for _, _, block in root:
            if not isinstance(block, list):
                continue
            for k, _, s in block:
                if not isinstance(s, list) or k is None:
                    continue
                name = kv(s, "name")
                if not name:
                    continue
                tex = kv(s, "texturefile") or kv(s, "textureFile")
                sprites[name] = {
                    "texture": tex.replace("//", "/") if tex else None,
                    "frames": int(kv(s, "noOfFrames") or 1),
                    "mask": kv(s, "textureFile2"),
                    "overlay": kv(s, "textureFile1"),
                    "kind": k,
                }
    return sprites


_image_cache = {}


def load_image(hoi4: Path, rel):
    if rel is None:
        return None
    if rel in _image_cache:
        return _image_cache[rel]
    p = hoi4 / rel
    if not p.exists():
        # .dds referenced, .tga on disk or vice versa
        for ext in (".dds", ".tga", ".png"):
            q = p.with_suffix(ext)
            if q.exists():
                p = q
                break
    img = Image.open(p).convert("RGBA") if p.exists() else None
    _image_cache[rel] = img
    return img


def sprite_image(hoi4, sprites, name, tag):
    s = sprites.get(name)
    if s is None:
        return None
    if s["mask"]:  # masked flag (GFX_shield_medium): flag under the mask, overlay on top
        mask = load_image(hoi4, s["mask"])
        overlay = load_image(hoi4, s["overlay"])
        flag = None
        for cand in (f"gfx/flags/{tag}_{IDEOLOGY}.tga", f"gfx/flags/{tag}.tga"):
            if (hoi4 / cand).exists():
                flag = load_image(hoi4, cand)
                break
        if mask is None or flag is None:
            return overlay
        w, h = mask.size
        img = Image.new("RGBA", (w, h))
        f = flag.resize((w, h), Image.LANCZOS)
        f.putalpha(mask.getchannel("A") if mask.mode == "RGBA" else mask.convert("L"))
        img.alpha_composite(f)
        if overlay is not None:
            img.alpha_composite(overlay.resize((w, h), Image.LANCZOS))
        return img
    img = load_image(hoi4, s["texture"])
    if img is None:
        return None
    if s["frames"] > 1:
        fw = img.width // s["frames"]
        img = img.crop((0, 0, fw, img.height))
    return img


def anchor(rect, orientation):
    x, y, w, h = rect
    o = (orientation or "UPPER_LEFT").upper().strip('"')
    ax = {"UPPER_LEFT": x, "LOWER_LEFT": x, "UPPER_RIGHT": x + w, "LOWER_RIGHT": x + w,
          "CENTER": x + w / 2, "CENTER_UP": x + w / 2, "CENTER_DOWN": x + w / 2}.get(o, x)
    ay = {"UPPER_LEFT": y, "UPPER_RIGHT": y, "CENTER_UP": y, "LOWER_LEFT": y + h, "LOWER_RIGHT": y + h,
          "CENTER_DOWN": y + h, "CENTER": y + h / 2}.get(o, y)
    return ax, ay


def size_of(items, parent):
    sz = kv(items, "size")
    if not isinstance(sz, list):
        return None
    def dim(key, full):
        v = kv(sz, key)
        if v is None:
            return 0
        v = str(v)
        return full * float(v.rstrip("%")) / 100 if v.endswith("%") else float(v)
    return dim("width", parent[2]), dim("height", parent[3])


def layout(items, parent, out, depth=0, listing=False):
    """Walk a container's children; append (kind, name, x, y, items) to out."""
    for k, _, v in items:
        if not isinstance(v, list) or k is None or not k.endswith("Type"):
            continue
        name = kv(v, "name")
        pos = kv(v, "position")
        px = float(kv(pos, "x") or 0) if isinstance(pos, list) else 0
        py = float(kv(pos, "y") or 0) if isinstance(pos, list) else 0
        ax, ay = anchor(parent, kv(v, "Orientation"))
        x, y = ax + px, ay + py
        if listing:
            print("  " * depth + f"{k[:-4]:<20} {name:<32} ({px:g},{py:g}) {kv(v, 'Orientation') or ''} "
                  f"{kv(v, 'spriteType') or kv(v, 'quadTextureSprite') or ''}")
        if px <= -5000 or py <= -5000:
            continue
        out.append((k, name, x, y, v))
        if k == "containerWindowType":
            sz = size_of(v, parent) or (0, 0)
            layout(v, (x, y, sz[0], sz[1]), out, depth + 1, listing)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", type=Path, default=DEFAULT_HOI4)
    ap.add_argument("--tag", default="GER")
    ap.add_argument("--list", action="store_true")
    ap.add_argument("--ideology", default="fascism")
    ap.add_argument("--paused", action="store_true")
    a = ap.parse_args()
    global IDEOLOGY, PAUSED
    IDEOLOGY, PAUSED = a.ideology, a.paused

    sprites = load_sprites(a.hoi4)
    gui = hc.parse_pdx(hc.read_text(a.hoi4 / "interface" / "topbar.gui"))
    top = next(v for k, _, v in kv(gui, "guiTypes") if k == "containerWindowType" and kv(v, "name") == "topbar")
    screen = (0, 0, SCREEN_W, SCREEN_H)
    out = []
    pos = kv(top, "position")
    tx, ty = float(kv(pos, "x")), float(kv(pos, "y"))
    layout(top, (tx, ty, SCREEN_W, SCREEN_H), out, listing=a.list)

    img = Image.new("RGBA", (SCREEN_W, SCREEN_H), (52, 64, 44, 255))  # map-ish backdrop
    draw = ImageDraw.Draw(img)
    try:
        font = ImageFont.truetype("arialbd.ttf", 15)
    except OSError:
        font = ImageFont.load_default()
    kept = {n for (_, n, *_rest) in out}
    for kind, name, x, y, v in out:
        if name not in KEEP:
            continue
        if kind == "containerWindowType":
            continue
        if kind in ("instantTextBoxType", "textBoxType"):
            text = SAMPLE_TEXT.get(name, name)
            mw = float(kv(v, "maxWidth") or 0)
            fmt = (kv(v, "format") or "left").lower()
            f = font
            if name == "DateText":  # between the play arrow and the + button
                mw = 122
                size = 15
                while size > 9 and draw.textlength(text, font=ImageFont.truetype("arialbd.ttf", size)) > mw:
                    size -= 1
                f = ImageFont.truetype("arialbd.ttf", size)
            tw = draw.textlength(text, font=f)
            tx_ = x + (mw - tw if fmt == "right" and mw else (mw - tw) / 2 if fmt == "center" and mw else 0)
            draw.text((tx_, y + 2), text, font=f, fill=(240, 230, 200, 255))
            continue
        spr = kv(v, "spriteType") or kv(v, "quadTextureSprite")
        if name == "pause_button_date" and PAUSED:
            pimg = load_image(a.hoi4, "gfx/interface/date_pause_button.dds")
            img.alpha_composite(pimg.crop((pimg.width // 2, 0, pimg.width, pimg.height)), (int(round(x)), int(round(y))))
            continue
        im = sprite_image(a.hoi4, sprites, spr, a.tag) if spr else None
        if im is None:
            print("missing sprite", name, spr)
            continue
        img.alpha_composite(im, (int(round(x)), int(round(y))))
    missing = KEEP - kept
    if missing:
        print("not found in gui:", sorted(missing))
    path = Path(__file__).with_name("topbar.png")
    img.save(path)
    print("wrote", path)


if __name__ == "__main__":
    main()
