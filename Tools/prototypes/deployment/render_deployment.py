"""Prototype of the HOI4 recruit & deploy screen (interface/countrydeploymentview.gui): the deployment window (equipment
priority rows, then the deployment orders with their summary row and training lines) and the division templates
window beside it, drawn from the gui layout and the sprites the converter writes (Hoi4Data/gfx/interface/deployment,
template icons from gfx/interface/unitpanel/icons), with a country's 1936 templates from the converted OOB. Check it
against a HOI4 screenshot before porting to UXML.

    python Tools/prototypes/deployment/render_deployment.py [TAG]
"""
import argparse
import json
import re
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[3]
DATA = ROOT / "Hoi4Data"
GFX = DATA / "gfx" / "interface" / "deployment"
ICONS = DATA / "gfx" / "interface" / "unitpanel" / "icons"
OUT = Path(__file__).resolve().parent

# equipment priority rows (deploy_entry): name, entry background, icon
PRIORITY_ROWS = (("Reinforcements", "entry_reinforcements", "icon_reinforcements"),
                 ("Upgrades", "entry_upgrades", "icon_upgrades"),
                 ("Supply Trucks", "entry_supply", "icon_supply"),
                 ("Operations", "entry_operations", "icon_operations"),
                 ("Garrisons", "entry_garrisons", "icon_garrisons"),
                 ("Raids", "entry_reinforcements", "icon_raids"))


def img(name, d=GFX):
    return Image.open(d / name).convert("RGBA")


def font(size):
    return ImageFont.truetype(str(DATA / "gfx" / "fonts" / "Oswald.ttf"), size)


def nine(name, w, h, border):
    src = img(name)
    sw, sh = src.size
    b = min(border, sw // 2, sh // 2)
    out = Image.new("RGBA", (int(w), int(h)))
    for (sx0, sx1, dx0, dx1) in ((0, b, 0, b), (b, sw - b, b, w - b), (sw - b, sw, w - b, w)):
        for (sy0, sy1, dy0, dy1) in ((0, b, 0, b), (b, sh - b, b, h - b), (sh - b, sh, h - b, h)):
            if dx1 > dx0 and dy1 > dy0:
                out.alpha_composite(src.crop((sx0, sy0, sx1, sy1)).resize((int(dx1 - dx0), int(dy1 - dy0))), (int(dx0), int(dy0)))
    return out


def text(d, xy, s, size, fill=(230, 230, 230), anchor="la"):
    d.text((int(xy[0]), int(xy[1])), s, font=font(size), fill=fill, anchor=anchor)


def bar(canvas, x, y, fill_name, bg_name, value):
    """A progressbartype at its declared size (deployment_progressbar 72 x 12, entry bars 68 x 12, not their
    textures'): background, then the fill cropped to the value."""
    size = (72, 12) if fill_name.startswith("priority") else (68, 12)
    bg = img(bg_name).resize(size)
    canvas.alpha_composite(bg, (x, y))
    f = img(fill_name).resize(size)
    w = int(round(f.width * max(0.0, min(1.0, value))))
    if w > 0:
        canvas.alpha_composite(f.crop((0, 0, w, f.height)), (x, y))


def prio(canvas, x, y, selected):
    """Low/normal/high dots (GFX_deploy_priority: frame 1 off, 2 low red, 3 normal yellow, 4 high green)."""
    for i in range(3):
        canvas.alpha_composite(img(f"priority_{i + 1 if i == selected else 0}.png"), (x + 20 * i, y))


def json5(path):
    t = path.read_text(encoding="utf-8")
    t = re.sub(r"^\s*//.*$", "", t, flags=re.M)
    t = re.sub(r"(\{|,)\s*([A-Za-z_][\w]*)\s*:", r'\1 "\2":', t)
    t = re.sub(r",(\s*[}\]])", r"\1", t)
    return json.loads(t)


def division_name(template, n):
    fmt = template.get("name_format")
    if not fmt:
        return f"Division {n}"
    if "%dth" in fmt:
        suffix = "th" if 11 <= n % 100 <= 13 else {1: "st", 2: "nd", 3: "rd"}.get(n % 10, "th")
        fmt = fmt.replace("%dth", f"{n}{suffix}")
    return fmt.replace("%d", str(n)).replace("%s", str(n))


def template_icon(canvas, x, y, main):
    """The template's unit icon (GFX_technology_medium is only the button's placeholder: a '+' tech icon)."""
    p = ICONS / f"{main}.png"
    if not p.exists():
        p = ICONS / "infantry.png"
    icon = img(p.name, ICONS)
    bg = img("template_icon_bg.png")
    canvas.alpha_composite(icon, (x + (bg.width - icon.width) // 2, y + (bg.height - icon.height) // 2))


def draw_order(canvas, ox, oy, name, main, location, lines, priority, template_priority=1):
    """military_deployment_conveyor_view (490x84), its summary row, the training lines and the end line. Returns the
    y below it."""
    d = ImageDraw.Draw(canvas)
    canvas.alpha_composite(img("order_bg.png"), (ox, oy))
    template_icon(canvas, ox + 12, oy + 22, main)
    canvas.alpha_composite(img(f"priority_strip_{template_priority}.png"), (ox + 62, oy + 22))  # the template's, not the order's
    text(d, (ox + 104, oy + 27), name, 17, anchor="lm")  # the name box is y 15..38 in the sprite
    canvas.alpha_composite(img("decrease.png"), (ox + 312, oy + 10))
    text(d, (ox + 364, oy + 27), str(len(lines)), 18, anchor="mm")
    canvas.alpha_composite(img("increase.png"), (ox + 383, oy + 10))
    prio(canvas, ox + 420, oy + 17, priority)
    canvas.alpha_composite(img("location_0.png"), (ox + 91, oy + 46))
    if location:
        text(d, (ox + 91 + 112, oy + 61), location, 15, anchor="mm")
    else:
        text(d, (ox + 91 + 112, oy + 61), "No location set", 15, fill=(220, 60, 50), anchor="mm")
    canvas.alpha_composite(img("add_line_0.png"), (ox + 315, oy + 50))
    text(d, (ox + 315 + 52, oy + 61), "Add Unit", 15, anchor="mm")
    canvas.alpha_composite(img("deploy_order.png"), (ox + 421, oy + 47))
    canvas.alpha_composite(img("cancel_order.png"), (ox + 450, oy + 47))
    y = oy + 84

    # summary (510x40, x=-15): order arrows, mean equipment, how many divisions, collapse
    sx = ox - 15
    canvas.alpha_composite(img("summary_bg.png"), (sx, y))
    canvas.alpha_composite(img("order_arrows_0.png"), (sx + 11, y + 4))
    canvas.alpha_composite(img("order_arrows_1.png"), (sx + 36, y + 4))
    eq = sum(e for _, e, _ in lines) / max(1, len(lines))
    canvas.alpha_composite(img(f"equipment_state_icon_{0 if eq >= 0.999 else 1}.png"), (sx + 215, y + 7))
    bar(canvas, sx + 237, y + 9, "entry_bar.png" if eq > 0 else "entry_bar_stalled.png", "entry_bar_bg.png", eq)
    text(d, (sx + 380, y + 19), f"{len(lines)} Division{'s' if len(lines) != 1 else ''}", 15, anchor="mm")
    canvas.alpha_composite(img("collapse_0.png"), (sx + 481, y + 4))
    y += 40

    # lines (514x40, x=-13): name, equipment, training, number in the order, deploy, cancel
    for i, (dname, equipment, training) in enumerate(lines):
        lx = ox - 13
        canvas.alpha_composite(img("line_bg.png"), (lx, y))
        text(d, (lx + 10, y + 20), dname, 15, anchor="lm")
        canvas.alpha_composite(img("name_dropdown_1.png"), (lx + 180, y + 9))
        canvas.alpha_composite(img(f"equipment_state_icon_{0 if equipment >= 0.999 else 1}.png"), (lx + 214, y + 12))
        bar(canvas, lx + 235, y + 13, "entry_bar.png" if equipment > 0 else "entry_bar_stalled.png", "entry_bar_bg.png", equipment)
        canvas.alpha_composite(img("unit_level_0.png"), (lx + 306, y + 6))
        bar(canvas, lx + 329, y + 13, "entry_bar.png", "entry_bar_bg.png", training)
        text(d, (lx + 420, y + 20), f"{i + 1}/{len(lines)}", 15, anchor="mm")
        deploy = img("deploy_line.png")
        if training < 0.2:
            deploy = Image.blend(deploy, Image.new("RGBA", deploy.size, (60, 60, 60, deploy.getchannel("A").getextrema()[1])), 0.6)
            deploy.putalpha(img("deploy_line.png").getchannel("A"))
        canvas.alpha_composite(deploy, (lx + 458, y + 7))
        canvas.alpha_composite(img("cancel_line.png"), (lx + 484, y + 7))
        y += 40
    canvas.alpha_composite(img("end_line.png"), (ox - 14, y - 3))
    return y + 9


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("tag", nargs="?", default="ENG")
    a = ap.parse_args()
    oob = json5(DATA / "history" / "units" / "oob_1936.json5")
    c = oob["countries"][a.tag]
    templates = c["templates"]
    in_use = {d["template"] for d in c["divisions"]}

    W, H = 550 + 375 - 12, 1080 - 78
    canvas = Image.new("RGBA", (W, H), (20, 20, 20, 255))
    d = ImageDraw.Draw(canvas)

    # --- deployment window (550 wide) ------------------------------------------------------------------------
    canvas.alpha_composite(nine("window_bg.png", 550, H, 64), (0, 0))
    canvas.alpha_composite(img("header_bg.png"), (5, 7))
    text(d, (45, 26), "RECRUIT & DEPLOY", 26, anchor="lm")
    canvas.alpha_composite(img("close.png"), (550 - 43, 9))
    cy = 42
    canvas.alpha_composite(nine("lines_overlay.png", 550, H - cy, 144), (0, cy))
    canvas.alpha_composite(nine("column_border.png", 550, H - cy, 64), (0, cy))
    canvas.alpha_composite(img("binding.png"), (9, 43 - 8 + 8))
    gx, y = 25, cy + 5 + 13

    for name, entry, icon in PRIORITY_ROWS:
        canvas.alpha_composite(img(f"{entry}.png"), (gx, y - 5))
        canvas.alpha_composite(nine("icon_bg.png", 77, 40, 16), (gx + 10, y + 4))
        ic = img(f"{icon}.png")
        canvas.alpha_composite(ic, (gx + 47 - ic.width // 2, y + 24 - ic.height // 2))
        canvas.alpha_composite(img("title_bg.png"), (gx + 110, y + 12))
        tb = img("title_bg.png")
        text(d, (gx + 110 + tb.width // 2, y + 12 + tb.height // 2), name, 15, anchor="mm")
        canvas.alpha_composite(img("meter_bg.png"), (gx + 280, y + 10))
        canvas.alpha_composite(img("equipment_icon.png"), (gx + 285, y + 15))
        bar(canvas, gx + 310, y + 19, "priority_bar.png", "priority_bar_bg.png", 0.9)
        prio(canvas, gx + 415, y + 15, 1)
        y += 48

    # one order per template the country trains most (as in the screenshot), and a second with progress
    names = list(templates)
    t0 = templates[names[0]]
    y = draw_order(canvas, gx, y, names[0], t0["main"], None,
                   [(division_name(t0, t0.get("next_number", 1)), 0.0, 0.0)], 1)
    if len(names) > 1:
        t1 = templates[names[1]]
        n = t1.get("next_number", 1)
        y = draw_order(canvas, gx, y + 4, names[1], t1["main"], "London",
                       [(division_name(t1, n), 1.0, 0.72), (division_name(t1, n + 1), 0.46, 0.46),
                        (division_name(t1, n + 2), 0.0, 0.0)], 2)

    # --- division templates window (375 wide, at x=538) --------------------------------------------------------
    tx = 538
    canvas.alpha_composite(nine("templates_bg.png", 375, H, 32), (tx, 0))
    canvas.alpha_composite(img("templates_header_bg.png"), (tx + 1, 0))
    text(d, (tx + 375 // 2, 26), "DIVISION TEMPLATES", 26, anchor="mm")
    canvas.alpha_composite(img("special_forces_icon_2.png"), (tx + 74, 63))
    text(d, (tx + 104, 74), "0/24", 16, anchor="lm")
    canvas.alpha_composite(img("designer_button.png"), (tx + 188, 56))
    text(d, (tx + 188 + 83, 72), "Division Designer", 15, anchor="mm")
    canvas.alpha_composite(nine("plain_bg.png", 373, 44, 16), (tx + 1, 96))
    canvas.alpha_composite(img("checkbox_0.png"), (tx + 1 + 18, 96 + 7))
    text(d, (tx + 1 + 59, 96 + 22), "Show decommissioned templates", 17, fill=(230, 200, 80), anchor="lm")

    ey = 138
    for name, t in templates.items():
        ex = tx + 2 + 10
        bg = img("template_bg.png")
        canvas.alpha_composite(bg, (ex - 5, ey))
        template_icon(canvas, ex + 5, ey + 16, t["main"])
        canvas.alpha_composite(img(f"priority_strip_{t.get('priority', 1)}.png"), (ex + 55, ey + 16))  # reserve/normal/elite
        canvas.alpha_composite(img("obsolete_toggle_0.png"), (ex + 302, ey + 22))
        text(d, (ex + 102 + 120, ey + 32), name, 17, anchor="mm")
        canvas.alpha_composite(img("small_button.png"), (ex + 158, ey + 48))
        text(d, (ex + 158 + 35, ey + 61), "Train", 15, anchor="mm")
        canvas.alpha_composite(img("small_button.png"), (ex + 228, ey + 48))
        text(d, (ex + 228 + 35, ey + 61), "Edit", 15, anchor="mm")
        delete = img("delete_template.png")
        if name in in_use:  # templates divisions use can't be deleted: greyed out
            grey = delete.convert("LA").convert("RGBA")
            grey.putalpha(delete.getchannel("A"))
            delete = grey
        canvas.alpha_composite(delete, (ex + 298, ey + 48))
        ey += 74

    out = OUT / f"deployment_{a.tag}.png"
    canvas.convert("RGB").save(out)
    print(out)


if __name__ == "__main__":
    main()
