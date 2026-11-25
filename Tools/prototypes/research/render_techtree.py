"""Prototype of HOI4's tech tree screen (interface/countrytechtreeview.gui), from the layout and sprites the converter
writes (Hoi4Data/gfx/interface/techtree/techtree.json5) and the technologies (common/hoi4_technologies.json5), for one
country at the game start. Check it against a HOI4 screenshot before porting to UXML.

Layout rules (checked against a GER 1936 screenshot of the infantry folder, UI scale 1.3):
- A folder's techs sit on gridboxes named <root tech>_tree. A tech goes on the gridbox of the first ancestor (through
  leads_to, inside the folder) that has one. Its folder position (x, y) counts slots from the gridbox: format LEFT puts
  y across and x down (the infantry folder: y is the year column), the others x across and y down.
- A gridbox other than LEFT starts its tree at its leftmost column (artillery's techs run from x -9, anti-air, to 7,
  anti-tank, on a gridbox at x 300; shifted, each column sits under its centre-anchored section label).
- The content is its own size or the visible folder, whichever is larger (min = 100%%); labels anchored to its right
  edge (artillery_year_right) follow it. The converter gives each label ax: 0 left, 0.5 centre, 1 right. Centre ones
  stay at the content's own width, where they sit over their trees.
- A tech that unlocks equipment is a large card (the folder's techtree_<folder>_item, 183 x 84, centred on its slot by
  its offset), the rest small icons (techtree_<folder>_small_item, 72 x 72). Folders without a small template (industry,
  electronics) use the large one for all.
- The card shows the country's own short equipment name (GER_motorized_equipment_0_short "ALZ 13"), else the generic
  one; the picture is GFX_<TAG>_<tech>_medium where the country has one, else GFX_<tech>_medium.
- Background by state: researched (green), available (tan), currently researching (the 9-frame chevron strip, 15 fps,
  sliding right), unavailable (grey stripes).
- Links: from every tech to each leads_to child on the same gridbox, through slot centres: one slot along y towards
  the child, then along x to the child's x, then along y to it (a straight line when they share x or y). The line's
  colour is the child's state: green researched, tan available or researching, grey otherwise. Items cover the lines.

    python Tools/prototypes/research/render_techtree.py [TAG] [--folder infantry_folder] [--researching TECH] [--frame N]
"""
import argparse
import json
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[3]
DATA = ROOT / "Hoi4Data"
GFX = DATA / "gfx" / "interface" / "techtree"
OUT = Path(__file__).resolve().parent
SCREEN = (1968, 1107)   # 2559 x 1439 at UI scale 1.3
WHITE = (235, 235, 235)


def load(path):
    return json.loads(path.read_text(encoding="utf-8").split("\n", 1)[1])


_imgs = {}


def img(name):
    if name not in _imgs:
        _imgs[name] = Image.open(GFX / name).convert("RGBA")
    return _imgs[name]


def font(size):
    return ImageFont.truetype(str(DATA / "gfx" / "fonts" / "Oswald.ttf"), size)


def tile(piece, w, h):
    out = Image.new("RGBA", (int(w), int(h)))
    for y in range(0, int(h), piece.height):
        for x in range(0, int(w), piece.width):
            out.alpha_composite(piece, (x, y))
    return out


def nine(name, w, h, border):
    """corneredTileSpriteType (tilingCenter = yes): corners fixed, edges and middle tiled, not stretched."""
    src = img(name)
    sw, sh = src.size
    bx, by = border or (0, 0)
    w, h = int(w), int(h)
    out = Image.new("RGBA", (w, h))
    for (sx0, sx1, dx0, dx1) in ((0, bx, 0, bx), (bx, sw - bx, bx, w - bx), (sw - bx, sw, w - bx, w)):
        for (sy0, sy1, dy0, dy1) in ((0, by, 0, by), (by, sh - by, by, h - by), (sh - by, sh, h - by, h)):
            if dx1 > dx0 and dy1 > dy0 and sx1 > sx0 and sy1 > sy0:
                out.alpha_composite(tile(src.crop((sx0, sy0, sx1, sy1)), dx1 - dx0, dy1 - dy0), (dx0, dy0))
    return out


def place_folder(techs, tree, folder):
    """{tech: (slot x, slot y, grid)} for the folder's techs, and the ones without a gridbox."""
    grids = {g["root"]: g for g in tree["folders"][folder]["grids"]}
    pos = {n: next((p[1], p[2]) for p in t["places"] if p[0] == folder)
           for n, t in techs.items() if any(p[0] == folder for p in t.get("places", []))}
    parents = {}
    for n, t in techs.items():
        for child, _ in t.get("leads_to", []):
            parents.setdefault(child, []).append(n)
    placed, lost, on = {}, [], {}
    for n in pos:
        seen, todo, grid = set(), [n], None
        while todo and grid is None:
            m = todo.pop(0)
            if m in seen:
                continue
            seen.add(m)
            if m in grids:
                grid = grids[m]
            todo += [p for p in parents.get(m, []) if p in pos]
        if grid is None:
            lost.append(n)
        else:
            on[n] = grid
    # a gridbox (other than LEFT) starts its tree at its leftmost column: artillery's run from x -9 (anti-air) to 7
    left = {}
    for n, g in on.items():
        left[g["root"]] = min(left.get(g["root"], 0.0), pos[n][0]) if g["format"] != "LEFT" else 0.0
    for n, grid in on.items():
        x, y = pos[n]
        x -= left[grid["root"]]
        if grid["format"] == "LEFT":
            sx, sy = grid["x"] + y * grid["w"], grid["y"] + x * grid["h"]
        else:
            sx, sy = grid["x"] + x * grid["w"], grid["y"] + y * grid["h"]
        placed[n] = (sx, sy, grid, x, y)
    return placed, lost


def states(doc, tag, researching):
    techs = doc["technologies"]
    known = set(doc["countries"].get(tag, []))
    parents = {}
    for n, t in techs.items():
        for child, _ in t.get("leads_to", []):
            parents.setdefault(child, []).append(n)
    out = {}
    for n, t in techs.items():
        if n in known:
            out[n] = "researched"
        elif n in researching:
            out[n] = "currently_researching"
        elif ((not parents.get(n) or any(p in known for p in parents[n]))
              and all(r in known for r in t.get("requires", []))
              and not any(x in known for x in t.get("xor", []))):
            out[n] = "available"
        else:
            out[n] = "unavailable"
    return out


LINE_FRAME = {"researched": 3, "available": 2, "currently_researching": 2}


def draw_line(canvas, a, b, frame):
    """A link segment between two slot centres, 16 px thick."""
    (x0, y0), (x1, y1) = a, b
    if y0 == y1:
        x0, x1 = sorted((x0, x1))
        piece = img(f"line_left_{frame}.png")
        canvas.alpha_composite(piece.resize((int(x1 - x0) + 16, piece.height)), (int(x0) - 8, int(y0) - piece.height // 2))
    else:
        y0, y1 = sorted((y0, y1))
        piece = img(f"line_up_{frame}.png")
        canvas.alpha_composite(piece.resize((piece.width, int(y1 - y0) + 16)), (int(x0) - piece.width // 2, int(y0) - 8))


def render(tag, folder, researching, frame):
    doc = load(DATA / "common" / "hoi4_technologies.json5")
    tree = load(GFX / "techtree.json5")
    techs = doc["technologies"]
    state = states(doc, tag, researching)
    placed, lost = place_folder(techs, tree, folder)
    f = tree["folders"][folder]
    items = tree["items"][folder]
    W, H = SCREEN

    canvas = Image.new("RGBA", SCREEN, (0, 0, 0, 255))
    win = tree["window"]
    wx, wy = -3, 78
    canvas.alpha_composite(nine(win["window_bg"]["image"], W + 3, H - wy, win["window_bg"]["border"]), (wx, wy))
    # tabs (frame 0 the open folder)
    for name, other in tree["folders"].items():
        tab = other.get("tab")
        if tab:
            canvas.alpha_composite(img(tab["frames"][0 if name == folder else 1]), (int(wx + tab["x"]), int(wy + 7 + tab["y"])))
    close = win["close"]
    canvas.alpha_composite(img(close["frames"][0]), (int(wx + W + 3 - 50), int(wy + 11)))

    fx, fy = wx, wy + 47
    fw, fh = W + 3, H - fy
    canvas.alpha_composite(nine(win["folder_bg"]["image"], fw, fh, win["folder_bg"]["border"]), (fx, fy))
    m = f["margin"]
    cx, cy = fx + m["left"], fy + m["top"]
    content = Image.new("RGBA", (int(max(f["width"], fw)), int(max(f["height"], fh))))
    d = ImageDraw.Draw(content)
    for im in f["images"]:
        content.alpha_composite(img(im["image"]), (int(im["x"]), int(im["y"])))
    for lab in f["labels"]:
        # right-anchored ones (artillery_year_right) follow the content's right edge: max(its width, the view)
        lx = lab["x"] + (content.width - f["width"] if lab.get("ax") == 1 else 0)
        if lab["header"]:
            d.text((lx, lab["y"]), lab["text"], font=font(26), fill=WHITE)
        else:
            d.text((lx, lab["y"]), lab["text"], font=font(24), fill=(120, 120, 120))

    # links under the items
    def centre(n):
        sx, sy, g, _, _ = placed[n]
        return sx + g["w"] / 2, sy + g["h"] / 2

    def slot_centre(g, x, y):
        if g["format"] == "LEFT":
            return g["x"] + y * g["w"] + g["w"] / 2, g["y"] + x * g["h"] + g["h"] / 2
        return g["x"] + x * g["w"] + g["w"] / 2, g["y"] + y * g["h"] + g["h"] / 2

    for n, (sx, sy, g, x, y) in placed.items():
        for child, _ in techs[n].get("leads_to", []):
            if child not in placed or placed[child][2] is not g:
                continue
            frame_ = LINE_FRAME.get(state[child], 1)
            _, _, _, x1, y1 = placed[child]
            if x == x1 or y == y1:
                pts = [(x, y), (x1, y1)]
            else:
                step = y + (1 if y1 > y else -1)
                pts = [(x, y), (x, step), (x1, step), (x1, y1)]
            for a, b in zip(pts, pts[1:]):
                if a != b:
                    draw_line(content, slot_centre(g, *a), slot_centre(g, *b), frame_)

    for n, (sx, sy, g, x, y) in placed.items():
        t = techs[n]
        size = "large" if (t.get("equipment") or "small" not in items) and "large" in items else "small"
        it = items[size]
        st = state[n]
        bg = it["bg"].get(st) or it["bg"]["unavailable"]
        bgi = bg["frames"][frame % len(bg["frames"])]
        ix, iy = int(sx + it["x"]), int(sy + it["y"])
        content.alpha_composite(img(bgi), (ix, iy))   # at its own size (industry: a 72 x 72 icon in a 204 wide item)
        pic = f"icons/{tag}_{n}.png" if (GFX / "icons" / f"{tag}_{n}.png").exists() else f"icons/{n}.png"
        p = img(pic)
        ic = it.get("icon", {"x": 3, "y": 3, "center": False})
        if ic["center"]:
            content.alpha_composite(p, (int(ix + ic["x"] - p.width / 2), int(iy + ic["y"] - p.height / 2)))
        else:
            content.alpha_composite(p, (int(ix + ic["x"]), int(iy + ic["y"])))
        if t.get("equipment") and "name" in it:
            name = t.get("equipment_names", {}).get(tag) or t.get("equipment_name") or t["name"]
            d.text((ix + it["name"]["x"] + 4, iy + it["name"]["y"] + 4), name, font=font(14), fill=WHITE)

    clip = content.crop((0, 0, int(fw - m["left"] - m["right"]), int(fh - m["top"] - m["bottom"])))
    canvas.alpha_composite(clip, (int(cx), int(cy)))
    return canvas, lost, len(placed)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("tag", nargs="?", default="GER")
    ap.add_argument("--folder", default="infantry_folder")
    ap.add_argument("--researching", nargs="*", default=["support_weapons"])
    ap.add_argument("--frame", type=int, default=0)
    a = ap.parse_args()
    canvas, lost, n = render(a.tag, a.folder, set(a.researching), a.frame)
    out = OUT / f"techtree_{a.tag}_{a.folder}.png"
    canvas.convert("RGB").save(out)
    print(f"{out.name}: {n} techs placed, no gridbox: {lost}")


if __name__ == "__main__":
    main()
