"""Prototype of the HOI4 construction screen (interface/countryconstructionsview.gui), drawn from the gui layout and
the sprites the converter writes (Hoi4Data/gfx/interface/construction, building icons from stateview), with a
country's civilian factories and consumer goods from the converted data. Check it against a HOI4 screenshot before
porting to UXML. Only the empty screen: header, factory summary, column headers, the consumer goods row and the
building buttons by section (no queue entries yet).

    python Tools/prototypes/construction/render_construction.py [TAG]
"""
import argparse
import json
import math
import re
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Tools"))
import hoi4_gui as gui  # noqa: E402
from manpower_pools import json5  # noqa: E402

HOI4 = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV")
DATA = ROOT / "Hoi4Data"
GFX = DATA / "gfx" / "interface" / "construction"
STATEVIEW = DATA / "gfx" / "interface" / "stateview"
OUT = Path(__file__).resolve().parent
SECTIONS = (("state", "State Buildings"), ("shared", "Shared Buildings"), ("province", "Province Buildings"),
            ("facility", "Experimental Facilities"))


def img(name, d=GFX):
    return Image.open(d / name).convert("RGBA")


def font(size, bold=False):
    return ImageFont.truetype(str(DATA / "gfx" / "fonts" / "Oswald.ttf"), size)


def nine(name, w, h, border):
    src = img(name)
    sw, sh = src.size
    b = border
    out = Image.new("RGBA", (int(w), int(h)))
    for (sx0, sx1, dx0, dx1) in ((0, b, 0, b), (b, sw - b, b, w - b), (sw - b, sw, w - b, w)):
        for (sy0, sy1, dy0, dy1) in ((0, b, 0, b), (b, sh - b, b, h - b), (sh - b, sh, h - b, h)):
            if dx1 > dx0 and dy1 > dy0:
                out.alpha_composite(src.crop((sx0, sy0, sx1, sy1)).resize((int(dx1 - dx0), int(dy1 - dy0))), (int(dx0), int(dy0)))
    return out


def text(d, xy, s, size, fill=(230, 230, 230), anchor="la"):
    d.text((int(xy[0]), int(xy[1])), s, font=font(size), fill=fill, anchor=anchor)


def country_factories(tag):
    t = (DATA / "map" / "hoi4_state_data.json5").read_text(encoding="utf-8")
    sd = json.loads(re.sub(r"^\s*//.*$", "", t, flags=re.M))
    civ = mil = 0
    for s in sd["states"].values():
        if s.get("owner") == tag:
            civ += s.get("buildings", {}).get("industrial_complex", 0)
            mil += s.get("buildings", {}).get("arms_factory", 0)
    return sd["buildings"], civ, mil


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("tag", nargs="?", default="FRA")
    a = ap.parse_args()

    defs, civ, mil = country_factories(a.tag)
    e = json5(DATA / "common" / "hoi4_country_economy.json5")[a.tag]
    share = max(0.1, e["consumer_goods"] * (1 + e["consumer_goods_factor"]))
    cg = min(civ, max(1, math.floor((civ + mil) * share)))

    win = gui.window(HOI4, "countryconstructionsview.gui", "countryconstructionsview")
    W, H = 550, 1000
    pos = {}
    out = []
    gui.layout(win, (0, 0, W, H), out)
    pos = {n: (x, y) for _, n, x, y, _ in out}
    P = lambda k: (int(pos[k][0]), int(pos[k][1]))

    canvas = Image.new("RGBA", (720, H), (40, 50, 40, 255))
    canvas.alpha_composite(nine("window_bg.png", W, H, 64), (0, 0))
    d = ImageDraw.Draw(canvas)
    canvas.alpha_composite(img("top_bg.png"), P("construction_screen_top_bg"))
    canvas.alpha_composite(img("header_bg.png"), P("production_header_bg"))
    text(d, (pos["construction_title"][0], pos["construction_title"][1] + 2), "CONSTRUCTION", 22)

    # factory summary: used (consumer goods + construction) / total civilian, from trade, owned, construction speed
    canvas.alpha_composite(img("civ_factory_icon.png"), P("civilian_factory_icon"))
    x, y = P("available_factories_value")
    text(d, (x + 43, y + 10), f"{cg}/{civ}", 17, fill=(230, 80, 60), anchor="mm")
    for k, v in (("from_trade", "From trade:"), ("local_factories", "Owned:")):
        text(d, P(k), v, 16)
    for k, v in (("traded_factories_value", 0), ("local_factories_value", civ)):
        x, y = P(k)
        text(d, (x + 70, y), str(v), 16, anchor="ra")
    canvas.alpha_composite(img("speed_icon.png"), P("construction_speed_icon"))
    x, y = P("construction_speed_value")
    speed = e["construction_speed"]
    text(d, (x + 70, y), f"{speed:+.0%}", 16, fill=(120, 220, 90) if speed >= 0 else (230, 80, 60), anchor="ra")

    # column headers
    for k, s, w in (("location", "Location", 150), ("task", "Task", 80), ("factories_amount", "#", 120),
                    ("priority", "Priority", 110)):
        x, y = P(k)
        text(d, (x + w / 2 + (12 if k == "factories_amount" else 0), y + 9), s, 17, anchor="mm")
    canvas.alpha_composite(img("civ_factory_icon.png"), (P("factories")[0] - 4, P("factories")[1]))

    # the queue list: border and corner overlay, the consumer goods row first
    lx, ly = P("production_lines")
    canvas.alpha_composite(nine("lines_overlay.png", 440, H - ly, 144), (lx, ly))
    canvas.alpha_composite(nine("column_border.png", 440, H - ly, 64), (lx, ly))
    ex, ey = lx + 13 + 1, ly + 13 + 5
    canvas.alpha_composite(img("special_entry_bg.png"), (ex, ey))
    text(d, (ex + 20, ey + 16), "Consumer Goods", 17)
    cgi = img("consumer_goods_icon.png")
    canvas.alpha_composite(cgi, (ex + 210 - cgi.width // 2, ey + 26 - cgi.height // 2))
    text(d, (ex + 254 + 25, ey + 27), str(cg), 16, anchor="mm")

    # count container (factories per new construction, - / +) and the priority button, minimize and close
    canvas.alpha_composite(img("count_bg.png"), P("bg"))
    canvas.alpha_composite(img("factory_wrench.png"), P("factories_used"))
    canvas.alpha_composite(img("sub_1.png"), P("factories_sub_1x"))
    canvas.alpha_composite(img("add_1.png"), P("factories_add_1x"))
    x, y = P("factories_used_txt")
    text(d, (x + 25, y + 9), "0", 16, anchor="mm")
    bx, by = P("repair_order_open_button")
    canvas.alpha_composite(img("button_94.png"), (bx, by))
    text(d, (bx + 47, by + 15), "Priority", 15, anchor="mm")
    canvas.alpha_composite(img("minimize.png"), P("minimize"))
    canvas.alpha_composite(img("close.png"), P("close_button"))

    # right column: the building buttons by section, each section 52 + 44 per row of 4 high
    cx, cy = P("possible_constructions")
    ch = H - cy
    canvas.alpha_composite(nine("plain_bg.png", 267, ch, 64), (cx, cy))
    canvas.alpha_composite(nine("column_border.png", 267, ch, 64), (cx, cy))
    y = cy
    for sec, label in SECTIONS:
        items = [(n, b) for n, b in defs.items() if b.get("section") == sec]
        canvas.alpha_composite(img("section_bg.png"), (cx + 5, y + 9))
        canvas.alpha_composite(img(f"{sec}_constructions.png"), (cx + 5, y + 9))
        text(d, (cx + 60, y + 18), label, 15)
        for i, (n, b) in enumerate(items):
            bx, by = cx + 28 + (i % 4) * 46, y + 52 + (i // 4) * 44 + 4
            canvas.alpha_composite(img(f"buildings_{b['frame'] - 1}.png", STATEVIEW), (bx, by))
        y += 52 + math.ceil(len(items) / 4) * 44

    path = OUT / f"construction_{a.tag}.png"
    canvas.save(path)
    print(f"{a.tag}: civ {civ}, mil {mil}, consumer goods {cg} -> {path}")


if __name__ == "__main__":
    main()
