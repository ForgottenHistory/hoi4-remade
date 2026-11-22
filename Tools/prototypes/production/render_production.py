"""Prototype of the HOI4 production screen (interface/countryproductionlineview.gui), drawn from the gui layout and
the sprites the converter writes (Hoi4Data/gfx/interface/production), with a country's 1936 lines from the OOB and its
military factories from the state data. Check it against a HOI4 screenshot before porting to UXML.

    python Tools/prototypes/production/render_production.py [TAG] [--free N] [--picker]
"""
import argparse
import json
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
GFX = DATA / "gfx" / "interface" / "production"
OUT = Path(__file__).resolve().parent
FACTORY_OUTPUT, BASE_EFF, CAP = 4.5, 0.1, 0.5
INF_ART = ["infantry_equipment", "support_equipment", "artillery_equipment", "motorized_equipment"]
ARMOUR = ["light_tank_chassis"]


def img(name):
    return Image.open(GFX / name).convert("RGBA")


def font(size):
    return ImageFont.truetype(str(DATA / "gfx" / "fonts" / "Oswald.ttf"), size)


def nine(name, w, h, border):
    """corneredTileSpriteType: corners fixed, edges and middle stretched (HOI4 tiles them; close enough here)."""
    src = img(name)
    sw, sh = src.size
    b = border
    out = Image.new("RGBA", (int(w), int(h)))
    for (sx0, sx1, dx0, dx1) in ((0, b, 0, b), (b, sw - b, b, w - b), (sw - b, sw, w - b, w)):
        for (sy0, sy1, dy0, dy1) in ((0, b, 0, b), (b, sh - b, b, h - b), (sh - b, sh, h - b, h)):
            if dx1 > dx0 and dy1 > dy0:
                out.alpha_composite(src.crop((sx0, sy0, sx1, sy1)).resize((int(dx1 - dx0), int(dy1 - dy0))), (int(dx0), int(dy0)))
    return out


def text(d, xy, s, size, fill=(230, 230, 230), anchor="la", w=None):
    f = font(size)
    if w is not None:
        while d.textlength(s, font=f) > w and len(s) > 3:
            s = s[:-4] + "..."
    d.text(xy, s, font=f, fill=fill, anchor=anchor)


def factories(tag):
    t = (DATA / "map" / "hoi4_state_data.json5").read_text(encoding="utf-8")
    sd = json.loads(re.sub(r"^\s*//.*$", "", t, flags=re.M))
    return sum(s.get("buildings", {}).get("arms_factory", 0) for s in sd["states"].values() if s.get("owner") == tag)


def resources(tag):
    t = (DATA / "map" / "hoi4_state_data.json5").read_text(encoding="utf-8")
    sd = json.loads(re.sub(r"^\s*//.*$", "", t, flags=re.M))
    out = {}
    for s in sd["states"].values():
        if s.get("owner") == tag:
            for k, v in s.get("resources", {}).items():
                out[k] = out.get(k, 0) + v
    return out


def entries(win_block, name):
    for k, _, v in gui.kv(gui.load_gui(HOI4, "countryproductionlineview.gui"), "guiTypes") or []:
        if k == "containerWindowType" and gui.kv(v, "name") == name:
            return {n: (x, y) for _, n, x, y, _ in _layout(v)}
    raise KeyError(name)


def _layout(block, w=500, h=111):
    out = []
    gui.layout(block, (0, 0, w, h), out)
    return out


def draw_line(canvas, ox, oy, pos, n, line, eq, active, free_glow, tick):
    """One production_military_line_entry at (ox, oy)."""
    d = ImageDraw.Draw(canvas)
    P = lambda k: (int(ox + pos[k][0]), int(oy + pos[k][1]))
    canvas.alpha_composite(img("item.png"), (ox, oy))
    canvas.alpha_composite(img("priority_frame.png"), P("priority_frame"))
    text(d, (P("priority")[0] + 12, P("priority")[1] + 9), str(n), 15, anchor="mm")
    text(d, (P("name")[0], P("name")[1] - 1), eq["name"], 17, w=154)
    # efficiency: background with the cap part, fill to the current value, frame on top
    eff = line["eff"]
    bx, by = P("efficiency_progressbar")
    # efficiency on HOI4's 0-100% scale: efficiency_bar_max (red, yellow tick at its right end) up to the cap, the grey
    # fill (middle 9 px row of efficiency_bar.png) over it up to the value: at the 50% cap half full, tick in the middle
    canvas.alpha_composite(img("efficiency_bar_bg.png").crop((0, 0, 93, 9)), (bx, by))
    cw = max(3, round(93 * CAP))
    canvas.alpha_composite(img("efficiency_bar_max.png").crop((93 - cw, 0, 93, 9)), (bx, by))
    fw = min(cw - 3, round(93 * eff))
    if fw > 0:
        canvas.alpha_composite(img("efficiency_bar.png").crop((0, 9, fw, 18)), (bx, by))
    canvas.alpha_composite(img("efficiency_frame.png"), P("efficiency_progressbar_frame"))
    canvas.alpha_composite(img("efficiency_icon.png"), P("efficiency_icon"))
    # equipment picture, general name, output a day and progress to the next unit
    canvas.alpha_composite(img("equipment_bg.png"), P("equipment_button"))
    pic = GFX / "equipment" / f"{eq['sprite']}.png"
    if pic.exists():
        p = Image.open(pic).convert("RGBA")
        ex, ey = P("equipment_button")
        canvas.alpha_composite(p, (ex + 4 + (150 - p.width) // 2, ey + 81 - p.height - 2))
    gx, gy = P("general_name")
    text(d, (gx, gy - 1), eq["general"], 14, w=140)
    sx, sy = P("progressbar")
    canvas.alpha_composite(img("progress_bar_bg.png").resize((118, 21)), (sx, sy))
    prog = (tick * 0.37) % 1
    canvas.alpha_composite(img("progress_bar.png").crop((0, 0, max(1, int(118 * prog)), 21)), (sx, sy))  # row 0 of 3
    per_day = active * FACTORY_OUTPUT * eff / eq["cost"]
    rate = f"{per_day:.2f}/day" if per_day >= 1 else f"{per_day * 7:.2f}/week"
    text(d, (sx + 59, sy + 10), rate, 16, anchor="mm")
    # factories: +/- with the count, the 5x3 grid (one icon per factory, grey where the line wants more than it has)
    canvas.alpha_composite(img("factory_0.png"), P("factories_used"))
    canvas.alpha_composite(img("sub_1.png"), P("factories_sub_1x"))
    canvas.alpha_composite(img("add_1.png"), P("factories_add_1x"))
    fx, fy = P("factories_used_txt")
    text(d, (fx + 25, fy + 11), str(line["factories"]), 16, anchor="mm")
    canvas.alpha_composite(img("delete.png"), P("close_production_line"))
    canvas.alpha_composite(img("btn_x10_0.png"), P("btn_x10"))
    canvas.alpha_composite(img("btn_x5_0.png"), P("btn_x5"))
    gx0, gy0 = P("factories_grid")
    step = 10 if line["factories"] > 75 else 5 if line["factories"] > 15 else 1
    frame_off = {1: 0, 5: 5, 10: 10}[step]
    icons = -(-line["factories"] // step)
    for i in range(min(15, icons)):
        cx, cy = gx0 + (i % 5) * 29, gy0 + (i // 5) * 23
        canvas.alpha_composite(img("factory_bg.png"), (cx, cy))
        colour = 0 if i * step < active else 3  # green working, dark grey wanted but missing
        canvas.alpha_composite(img(f"factory_{frame_off + colour}.png"), (cx + 3, cy + 2))
    if free_glow:
        canvas.alpha_composite(img(f"line_glow_{tick % 2}.png"), P("prod_line_glow"))
    for k in ("increase_priority", "decrease_priority"):
        canvas.alpha_composite(img("arrow_up.png" if k.startswith("inc") else "arrow_down.png"), P(k))
    # under the picture: what the divisions lack (frame 2, reinforcing) or else the stockpile (frame 0, stockpiling)
    nx, ny = P("total_need")
    reinforcing = line["need"] > 0
    text(d, (nx + 100, ny + 1), f"{line['need'] if reinforcing else line['stock']:,}", 14, anchor="ra")
    canvas.alpha_composite(img(f"need_strip_{2 if reinforcing else 0}.png"), P("need_state_icon"))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("tag", nargs="?", default="FRA")
    ap.add_argument("--free", type=int, default=None, help="pretend this many factories are free")
    ap.add_argument("--picker", action="store_true", help="also draw the add-line equipment picker")
    ap.add_argument("--below-cap", action="store_true", help="put the first line at 30% to show a line below the cap")
    a = ap.parse_args()

    oob = json5(DATA / "history" / "units" / "oob_1936.json5")
    c = oob["countries"][a.tag]
    total = factories(a.tag)
    lines = [{"arch": p["archetype"], "factories": p["factories"], "eff": min(max(p["efficiency"], BASE_EFF), CAP),
              "need": 0, "stock": int(c.get("stockpile", {}).get(p["archetype"], 0))} for p in c["production"]]
    # what the divisions lack per archetype at the start (need x (1 - start_equipment_factor))
    lack = {}
    for dv in c["divisions"]:
        t = c["templates"][dv["template"]]
        for s in t["regiments"] + t["support"]:
            for arch, n in oob["sub_units"][s]["need"].items():
                lack[arch] = lack.get(arch, 0) + n * (1 - dv["equipment"])
    for ln in lines:
        ln["need"] = int(lack.get(ln["arch"], 0))
    if a.below_cap and lines:
        lines[0]["eff"] = 0.3
    used = sum(ln["factories"] for ln in lines)
    free = a.free if a.free is not None else total - used

    win = gui.window(HOI4, "countryproductionlineview.gui", "countryproductionlineview")
    W, H = 550, 1000
    pos = {n: (x, y) for _, n, x, y, _ in _layout(win, W, H)}
    line_pos = entries(None, "production_military_line_entry")

    canvas = Image.new("RGBA", (W + (500 if a.picker else 0), H), (40, 50, 40, 255))
    canvas.alpha_composite(nine("plain_bg.png", W, H, 64), (0, 0))
    d = ImageDraw.Draw(canvas)
    P = lambda k: (int(pos[k][0]), int(pos[k][1]))
    canvas.alpha_composite(img("header_bg.png"), P("production_header_bg"))
    text(d, (pos["production_title"][0], pos["production_title"][1] + 2), "PRODUCTION", 22)
    canvas.alpha_composite(img("close.png"), (int(pos["close_button"][0]), int(pos["close_button"][1])))
    top = pos["production_win_top"]
    # the gui puts production_lines at 253, but production_win_top (y 44, 253 high) reaches 297 and HOI4 shows the
    # lines under both filter rows
    lines_y = top[1] + 249
    canvas.alpha_composite(nine("lines_border.png", W, H - lines_y, 64), (0, int(lines_y)))
    canvas.alpha_composite(img("win_top.png"), (int(top[0]), int(top[1])))
    P = lambda k: (int(pos[k][0]), int(pos[k][1]))
    # resource strip: the country's own resources (no trade), HOI4 order
    rx, ry = P("resource_strip_container")
    res = resources(a.tag)
    for i, r in enumerate(("oil", "aluminium", "rubber", "tungsten", "steel", "chromium", "coal")):
        x = int(rx + 240 - 245 + 15 + i * 74)
        canvas.alpha_composite(img("resource_item.png"), (x, ry + 1))
        canvas.alpha_composite(Image.open(DATA / "gfx/interface/stateview" / f"resources_{i}.png").convert("RGBA"), (x + 4, ry + 3))
        text(d, (x + 35, ry + 8), str(res.get(r, 0)), 15, fill=(120, 220, 90) if res.get(r) else (200, 200, 200))
    # modifiers row: output, efficiency cap, efficiency growth (dockyards, loss and bombing defence hidden)
    for icon, k, v in (("output_icon.png", "production_output", "+0%"), ("efficiency_cap_icon.png", "efficiency_cap", "50%"),
                       ("efficiency_icon.png", "efficiency_growth", "+0%")):
        canvas.alpha_composite(img(icon), P(f"{k}_icon"))
        text(d, (P(f"{k}_value")[0], P(f"{k}_value")[1]), v, 15, fill=(120, 220, 90))
    canvas.alpha_composite(img("military_factory_icon.png"), P("military_factory_icon"))
    text(d, P("operational_factories"), "Military Factories", 16)
    text(d, (P("operational_factories_value")[0] + 40, P("operational_factories_value")[1]), f"{total - free}/{total}", 16,
         fill=(255, 200, 60) if free else (230, 230, 230), anchor="ra")
    for spr, k in (("add_inf_art", "infantry_button"), ("add_armour", "tank_button")):
        canvas.alpha_composite(img(f"{spr}_0.png"), P(k))
    if free:
        canvas.alpha_composite(img("new_equipment_glow_0.png"), P("infantry_button_glow"))
    inf = sum(ln["factories"] for ln in lines if ln["arch"] in INF_ART)
    arm = sum(ln["factories"] for ln in lines if ln["arch"] in ARMOUR)
    # both filter rows, all checked (frame 1: yellow border and tick); counts right-aligned (row 1) or centred (row 2)
    filters = (("show_inf_art", "show_equipment", "factories_used_equipment", inf),
               ("show_armour", "show_armor", "factories_used_armor", arm), ("show_air", "show_air", "factories_used_air", 0),
               ("show_capital_ships", "show_capital_ships", "factories_used_capital_ships", 0),
               ("show_screen_ships", "show_screen_ships", "factories_used_screen_ships", 0),
               ("show_other_ships", "show_other_ships", "factories_used_other_ships", 0),
               ("show_refitting", "show_refitting", "factories_used_refitting", 0),
               ("show_resources", "show_resources", "factories_used_resources", 0),
               ("show_outdated", "show_outdated", "factories_used_outdated", 0),
               ("show_stockpiling", "show_stockpiling", "factories_used_stockpiling", sum(ln["factories"] for ln in lines if not ln["need"])),
               ("show_reinforcing", "show_reinforcing", "factories_used_reinforcing", sum(ln["factories"] for ln in lines if ln["need"])),
               ("show_upgrading", "show_upgrading", "factories_used_upgrading", 0))
    for i, (spr, k, tk, n) in enumerate(filters):
        canvas.alpha_composite(img(f"{spr}_1.png"), P(k))
        x, y = P(tk)
        if i < 6:
            text(d, (x + 40, y + 10), str(n), 16, anchor="rm")
        else:
            text(d, (x + 20, y + 10), str(n), 16, anchor="mm")
    gx, gy = 14, lines_y + 9
    left = total
    for i, ln in enumerate(lines):
        active = max(0, min(ln["factories"], left))
        left -= active
        draw_line(canvas, int(gx), int(gy + i * 111), line_pos, i + 1, ln, c["equipment"][ln["arch"]], active, free > 0, i)
    if a.picker:
        draw_picker(canvas, W, c)
    out = OUT / f"production_{a.tag}{'_picker' if a.picker else ''}.png"
    canvas.save(out)
    print(f"{a.tag}: {total} factories, {used} on lines, free {free} -> {out}")


def draw_picker(canvas, x0, c):
    """production_equipment_window_military to the right: one military_equipment_info_entry per buildable equipment."""
    W, H = 495, canvas.height
    canvas.alpha_composite(nine("plain_bg.png", W, H, 64), (x0, 0))
    d = ImageDraw.Draw(canvas)
    pos = entries(None, "military_equipment_info_entry")
    y = 85
    for arch in INF_ART + ARMOUR:
        eq = c["equipment"].get(arch)
        if not eq:
            continue
        ox, oy = x0 + 10, y
        canvas.alpha_composite(img("picker_item_0.png"), (ox + int(pos["equipment_button"][0]), oy + int(pos["equipment_button"][1])))
        pic = GFX / "equipment" / f"{eq['sprite']}.png"
        if pic.exists():
            p = Image.open(pic).convert("RGBA")
            canvas.alpha_composite(p, (ox + 260 - p.width // 2, oy + 82 - p.height))
        text(d, (ox + pos["name"][0], oy + pos["name"][1]), eq["name"], 17, w=230)
        text(d, (ox + pos["general_name"][0], oy + pos["general_name"][1] + 3), eq["general"], 14, fill=(180, 180, 160), w=230)
        text(d, (ox + pos["production_cost"][0], oy + pos["production_cost"][1]), f"Cost {eq['cost']:g}", 14, fill=(255, 200, 60))
        y += 84


if __name__ == "__main__":
    main()
