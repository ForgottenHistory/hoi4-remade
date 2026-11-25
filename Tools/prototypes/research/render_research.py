"""Prototype of the HOI4 research screen's slots tab (interface/countrytechnologyview.gui), drawn from the gui layout
and the sprites the converter writes (Hoi4Data/gfx/interface/research), with a country's research slots and research
speed from common/hoi4_country_economy.json5 and the country's modifiers (Tools/hoi4_modifiers.py). Check it against a HOI4 screenshot before porting to UXML.

Empty slots glow (GFX_empty_research_slot_glow, two frames blended) and their bar shows the research saved while idle:
up to NTechnology BASE_RESEARCH_POINTS_SAVED (30) days, spent on the next technology picked for that slot.

    python Tools/prototypes/research/render_research.py [TAG] [--saved DAYS] [--frame 0|1]
"""
import argparse
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[3]
sys.path.insert(0, str(ROOT / "Tools"))
import hoi4_gui as gui  # noqa: E402
from manpower_pools import json5  # noqa: E402
from hoi4_modifiers import country_modifiers  # noqa: E402

HOI4 = Path(r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV")
DATA = ROOT / "Hoi4Data"
GFX = DATA / "gfx" / "interface" / "research"
OUT = Path(__file__).resolve().parent
SAVED_MAX_DAYS = 30.0  # NTechnology BASE_RESEARCH_POINTS_SAVED
WHITE, GREEN, GOLD = (230, 230, 230), (120, 220, 90), (255, 200, 60)


def img(name):
    return Image.open(GFX / name).convert("RGBA")


def font(size):
    return ImageFont.truetype(str(DATA / "gfx" / "fonts" / "Oswald.ttf"), size)


def nine(name, w, h, border):
    """corneredTileSpriteType: corners fixed, edges and middle stretched."""
    src = img(name)
    sw, sh = src.size
    bx, by = border if isinstance(border, tuple) else (border, border)
    out = Image.new("RGBA", (int(w), int(h)))
    for (sx0, sx1, dx0, dx1) in ((0, bx, 0, bx), (bx, sw - bx, bx, w - bx), (sw - bx, sw, w - bx, w)):
        for (sy0, sy1, dy0, dy1) in ((0, by, 0, by), (by, sh - by, by, h - by), (sh - by, sh, h - by, h)):
            if dx1 > dx0 and dy1 > dy0 and sx1 > sx0 and sy1 > sy0:
                out.alpha_composite(src.crop((sx0, sy0, sx1, sy1)).resize((int(dx1 - dx0), int(dy1 - dy0))), (int(dx0), int(dy0)))
    return out


def text(d, xy, s, size, fill=WHITE, anchor="la"):
    d.text(xy, s, font=font(size), fill=fill, anchor=anchor)


def entry_layout(name):
    for k, _, v in gui.kv(gui.load_gui(HOI4, "countrytechnologyview.gui"), "guiTypes") or []:
        if k == "containerWindowType" and gui.kv(v, "name") == name:
            out = []
            gui.layout(v, (0, 0, 500, 100), out)
            return {n: (x, y) for _, n, x, y, _ in out}
    raise KeyError(name)


def draw_slot(canvas, ox, oy, pos, saved_days, frame):
    """One research_slot_entry, empty: the glow, "Select a technology to research", the saved research as progress."""
    d = ImageDraw.Draw(canvas)
    P = lambda k: (int(ox + pos[k][0]), int(oy + pos[k][1]))
    canvas.alpha_composite(img("line_bg.png"), (ox, oy))
    canvas.alpha_composite(img(f"empty_glow_{frame}.png"), P("empty_research_slot_glow"))
    tx, ty = P("title")
    text(d, (tx, ty), "Select a technology to research", 15)
    canvas.alpha_composite(img("progress_frame.png"), P("research_progressbar_frame_icon"))
    bx, by = P("research_progressbar")
    canvas.alpha_composite(img("progress_bar_bg.png"), (bx, by))
    # the fill (progress_startend): research_progressbar.dds is three 4 px rows. Row 1 is the body, dark to green
    # along the whole bar, shown up to the progress; row 2 is the bright tip (fading in over its last ~80 px), shifted
    # so its right end sits at the progress. Row 0 is only a 1 px start cap.
    w = max(1, round(377 * min(1.0, saved_days / SAVED_MAX_DAYS)))
    bar = img("progress_bar.png")
    canvas.alpha_composite(bar.crop((0, 4, w, 8)), (bx, by))
    canvas.alpha_composite(bar.crop((377 - w, 8, 377, 12)), (bx, by))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("tag", nargs="?", default="GER")
    ap.add_argument("--saved", type=float, default=1.0, help="days of research saved in each empty slot")
    ap.add_argument("--frame", type=int, default=0, help="glow frame (HOI4 blends the two)")
    a = ap.parse_args()

    econ = json5(DATA / "common" / "hoi4_country_economy.json5")[a.tag]
    slots, speed = econ.get("research_slots", 2), country_modifiers(a.tag).sum("research_speed_factor")

    win = gui.window(HOI4, "countrytechnologyview.gui", "countrytechnologyview")
    W, H = 550, 1000
    out = []
    gui.layout(win, (0, 0, W, H), out)
    pos = {n: (x, y) for _, n, x, y, _ in out}
    P = lambda k: (int(pos[k][0]), int(pos[k][1]))
    slot_pos = entry_layout("research_slot_entry")

    canvas = Image.new("RGBA", (W, H), (40, 50, 40, 255))
    canvas.alpha_composite(nine("plain_bg.png", W, H, 64), (0, 0))
    d = ImageDraw.Draw(canvas)
    canvas.alpha_composite(img("header_bg.png"), P("production_header_bg"))
    text(d, (pos["research_title"][0], pos["research_title"][1] + 2), "RESEARCH", 22)
    canvas.alpha_composite(img("close.png"), P("close_button"))
    # tabs: frame = 1 / 2 in the gui are 1-based: slots shows frame 0 (selected), facilities frame 1
    for k, label, f in (("research_slots_tab", "Research Slots", 0), ("facilities_tab", "Experimental Facilities", 1)):
        x, y = P(k)
        canvas.alpha_composite(img(f"tab_{f}.png"), (x, y))
        text(d, (x + 129, y + 22), label, 16, anchor="mm")

    sx, sy = P("research_slots")
    canvas.alpha_composite(nine("slots_border.png", W, H - 5 - sy, 64), (sx, sy))
    canvas.alpha_composite(nine("overlay_bg.png", W - 1, H - 5 - sy, 268), (sx, sy))
    tx, ty = P("research_win_top")
    canvas.alpha_composite(img("top_win.png"), (tx, ty))
    text(d, P("limited_research_bonus_text"), "Limited bonuses:", 15)
    canvas.alpha_composite(img("focus_bonuses_icon.png"), P("focus_bonuses"))
    text(d, P("limited_research_bonus_value"), "0", 15, fill=GOLD)
    text(d, P("research_speed_text"), "Research speed:", 15)
    canvas.alpha_composite(img("speed_icon.png"), P("research_speed_icon"))
    text(d, P("research_speed_value"), f"{speed * 100:+.0f}%", 15, fill=GREEN if speed >= 0 else (230, 80, 60))

    gx, gy = P("research_slots_grid")
    for i in range(slots):
        draw_slot(canvas, gx, gy + i * 100, slot_pos, a.saved, a.frame)

    # research_win_bottom (LOWER_LEFT, 546 x 20): the bottom 20 px of GFX_production_win_bottom
    canvas.alpha_composite(img("win_bottom.png").crop((0, 46, 546, 66)), (P("research_win_bottom")[0], H - 20))
    path = OUT / f"research_{a.tag}.png"
    canvas.save(path)
    print(f"{a.tag}: {slots} slots, research speed {speed:+.2%} -> {path}")


if __name__ == "__main__":
    main()
