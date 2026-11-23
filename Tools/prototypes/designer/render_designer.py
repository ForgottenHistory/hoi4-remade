"""Prototype of the HOI4 division designer (interface/divisiondesignerview.gui countrydivisiondesignerview, 1200 x 720):
the template header (icon, name, name list, reinforcement priority), the composition summary (hardness,
organisation, HP, speed, combat width), the brigade grid (5 columns x 5 rows) with the support column and the
regimental support row, the three stat sheets (base, combat, equipment cost), and the bottom buttons. Drawn from the
gui layout and the sprites the converter writes (Hoi4Data/gfx/interface/designer), with a country's 1936 template
from the converted OOB, its stats computed the way the designer sums them. Check it against a HOI4 screenshot before
porting to UXML.

Slots follow HOI4's fill-in-order rule: in a used column the slot under its last battalion is open (+), deeper ones
locked; the first unused column is open, later ones locked; row 5 is locked (a doctrine unlocks it,
MIN_DIVISION_BRIGADE_HEIGHT 4). The support column: one open slot after the last company, then one locked.
--picker draws the battalion category list an empty brigade's + opens (groups of common/units sub-units, names
from group_<g>_title, "New group" BASE_DIVISION_BRIGADE_GROUP_COST + _CHANGE_COST for a group the template lacks).

    python Tools/prototypes/designer/render_designer.py [TAG] [TEMPLATE]
"""
import argparse
import re
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parents[3]
DATA = ROOT / "Hoi4Data"
GFX = DATA / "gfx" / "interface" / "designer"
OUT = Path(__file__).resolve().parent

COLUMNS, ROWS, OPEN_ROWS, SUPPORT_ROWS = 5, 5, 4, 5  # MAX_DIVISION_BRIGADE_WIDTH/HEIGHT, MIN_..._HEIGHT, MAX_..._SUPPORT_HEIGHT
PAPER_INK = (52, 40, 28)


def img(name):
    return Image.open(GFX / name).convert("RGBA")


def font(size):
    return ImageFont.truetype(str(DATA / "gfx" / "fonts" / "Oswald.ttf"), size)


def text(d, xy, s, size, fill=(235, 235, 230), anchor="la"):
    d.text((int(xy[0]), int(xy[1])), s, font=font(size), fill=fill, anchor=anchor)


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


def load_oob():
    import json5  # the converter's output is JSON5
    return json5.loads((DATA / "history" / "units" / "oob_1936.json5").read_text(encoding="utf-8"))


def stats(template, subs):
    """The designer's sums: HP, suppression, weight, supply, attacks, defence, breakthrough, manpower and equipment add
    up; organisation, recovery, reliability and hardness average over every sub-unit (support included); speed is the
    slowest; combat width counts battalions only; armour and piercing are 30/40% the best plus 70/60% the mean."""
    names = template["regiments"] + template["support"] + template.get("regimental_support", [])
    units = [subs[n] for n in names if n in subs]
    n = max(1, len(units))
    s = lambda k: sum(u.get(k, 0) for u in units)
    avg = lambda k: s(k) / n
    need = {}
    for u in units:
        for a, v in u["need"].items():
            need[a] = need.get(a, 0) + v
    return {
        "speed": min((u["speed"] for u in units[:len(template["regiments"]) + len(template["support"])]
                      if u.get("affects_speed", True)), default=4.0), "hp": s("max_strength"), "org": avg("max_organisation"),
        "recovery": avg("recovery"), "recon": s("recon"), "suppression": s("suppression"), "weight": s("weight"),
        "supply": s("supply_use"), "reliability": avg("reliability"),
        "soft": s("soft_attack"), "hard": s("hard_attack"), "air": s("air_attack"), "defense": s("defense"),
        "breakthrough": s("breakthrough"),
        "armor": 0.3 * max((u["armor"] for u in units), default=0) + 0.7 * avg("armor"),
        "piercing": 0.4 * max((u["piercing"] for u in units), default=0) + 0.6 * avg("piercing"),
        "initiative": avg("initiative"), "entrenchment": s("entrenchment"),
        "width": sum(subs[r]["combat_width"] for r in template["regiments"] if r in subs),
        "hardness": avg("hardness"), "manpower": s("manpower"),
        "training": max((u["training_time"] for u in units), default=0), "need": need,
    }


def draw_slot(canvas, d, x, y, kind, icon=None, support=False):
    """One slot entry (designer_division_regiment_slot_entry 76 x 53, background at -12, 11; support 64 x 53 at 0, 21)."""
    if support:
        canvas.alpha_composite(img("support_bg_0.png"), (x, y + 21))
        cx, cy = x + 32, y + 21 + 16
    else:
        canvas.alpha_composite(img("regiment_bg_0.png"), (x - 12, y + 11))
        cx, cy = x - 12 + 50, y + 11 + 21
    if kind == "unit" and icon is not None:
        ic = icon if not support else icon.resize((int(icon.width * 0.8), int(icon.height * 0.8)))
        canvas.alpha_composite(ic, (cx - ic.width // 2, cy - ic.height // 2))
    else:
        sprite = img({"open": "new_slot.png", "blocked": "blocked.png"}.get(kind, "locked.png"))
        if support:
            sprite = sprite.resize((int(sprite.width * 0.8), int(sprite.height * 0.8)))
        canvas.alpha_composite(sprite, (cx - sprite.width // 2, cy - sprite.height // 2))


# the category list (designerregimentswindow): group -> the battalion that pictures it, in HOI4's order
GROUP_ICONS = {"combat_support": "artillery_brigade", "mobile_combat_support": "mot_artillery_brigade",
               "mobile": "motorized", "armor": "light_armor", "armor_combat_support": "light_sp_artillery_brigade",
               "infantry": "infantry"}
GROUP_COST, CHANGE_COST = 20, 5  # BASE_DIVISION_BRIGADE_GROUP_COST, BASE_DIVISION_BRIGADE_CHANGE_COST


def draw_picker(canvas, d, oob, country, t):
    """designerregimentswindow at (0, 40), 660 x 580 on the bordered tile: the categories the country has battalions
    of, 2 per row in 256 x 69 slots from (72, 40); "New group 25" where the template has none of the group."""
    canvas.alpha_composite(nine("picker_bg.png", 660, 580, 64), (0, 40))
    text(d, (30, 40 + 27 + 10), "Make a selection to change or add battalion", 17, anchor="lm")
    b = img("button_123.png")
    canvas.alpha_composite(b, (506, 40 + 20))
    text(d, (506 + b.width / 2, 40 + 20 + b.height / 2), "Back", 15, anchor="mm")
    subs = oob["sub_units"]
    names = oob.get("sub_unit_groups", {})
    have = {subs[r]["group"] for r in t["regiments"]}
    groups = [g for g in GROUP_ICONS if any(subs[n]["group"] == g for n in country["sub_units"])]
    for i, g in enumerate(groups):
        x, y = 72 + (i % 2) * 256, 40 + 76 + (i // 2) * 69
        canvas.alpha_composite(nine("mini_tooltip.png", 250, 66, 8), (x, y))
        canvas.alpha_composite(img(f"icons/{GROUP_ICONS[g]}.png"), (x + 4, y + 17))
        words, line, lines = names.get(g, g).split(), "", []
        for w in words:  # wrap at 160 px
            trial = (line + " " + w).strip()
            if font(16).getlength(trial) > 160 and line:
                lines.append(line)
                line = w
            else:
                line = trial
        lines.append(line)
        for k, s in enumerate(lines[:2]):
            text(d, (x + 85, y + 5 + k * 18), s, 16)
        if g not in have:
            text(d, (x + 85, y + 41), "New group", 16, fill=(224, 69, 58))
            w = font(16).getlength("New group ")
            text(d, (x + 85 + w, y + 41), str(GROUP_COST + CHANGE_COST), 16, fill=(247, 212, 90))
            canvas.alpha_composite(img("army_xp.png"), (int(x + 85 + w + font(16).getlength("25 ")), y + 45))


def render(tag, template_name, picker=False):
    oob = load_oob()
    subs = oob["sub_units"]
    country = oob["countries"][tag]
    template_name = template_name or next(iter(country["templates"]))
    t = country["templates"][template_name]
    st = stats(t, subs)

    canvas = img("popup_bg.png")
    d = ImageDraw.Draw(canvas)
    text(d, (600, 22), "D I V I S I O N   D E S I G N E R", 24, anchor="mm")
    canvas.alpha_composite(img("close.png"), (1200 - 44, 6))

    # header: template icon, name, names list, priority, equipment
    canvas.alpha_composite(img("icon_bg.png"), (29, 50))
    main_icon = img(f"icons/{t['main']}.png")
    canvas.alpha_composite(main_icon, (29 + 48 - main_icon.width // 2, 50 + 22 - main_icon.height // 2))
    text(d, (160, 72), template_name, 17, anchor="lm")
    text(d, (160, 129), (t.get("name_format") or "").replace("%d", "1"), 17, anchor="lm")
    canvas.alpha_composite(img("priority_bg.png"), (508, 107))
    pr = t.get("priority", 1)
    for i, x in enumerate((517, 553, 589)):
        frame = i + (3 if i == pr else 0)  # frames 0-2 reserve, normal, elite; 3-5 the same ringed (selected)
        canvas.alpha_composite(img(f"priority_{frame}.png"), (x, 110 if i < 2 else 109))
    btn = img("button_123_alt.png")
    canvas.alpha_composite(btn, (511, 52))
    text(d, (511 + btn.width / 2, 52 + btn.height / 2), "Equipment", 15, anchor="mm")

    # composition summary
    canvas.alpha_composite(img("composition_icon.png"), (30, 159))
    text(d, (60, 170), "Divisional Composition", 16, anchor="lm")
    for (x, w, icon, value) in ((86, 100, "hardness_icon.png", f"{st['hardness'] * 100:.1f}%"),
                                (216, 80, "org_icon.png", f"{st['org']:.1f}"),
                                (326, 80, "strength_icon.png", f"{st['hp']:.1f}"),
                                (436, 115, "speed_icon.png", f"{st['speed']:.1f} km/h")):
        canvas.alpha_composite(nine("cost_bg.png", w, 24, 6), (x, 192))
        ic = img(icon)
        canvas.alpha_composite(ic, (x + 7, 192 + 12 - ic.height // 2))
        text(d, (x + w - 12, 204), value, 15, fill=(250, 210, 90), anchor="rm")
    text(d, (290 + 20, 230 + 10 + 10), f"{st['width']:g}", 15, anchor="mm")

    # brigade grid: subunits container at y 170; regiments_grid (50, 109) slots 102 x 46
    grid = {(c, r): name for name, (c, r) in zip(t["regiments"], t["grid"])}
    used_cols = sorted({c for c, _ in grid})
    first_free = max(used_cols) + 1 if used_cols else 0
    for c in range(COLUMNS):
        count = sum(1 for (cc, _) in grid if cc == c)
        for r in range(ROWS):
            x, y = 50 + c * 102, 170 + 109 + r * 46
            if (c, r) in grid:
                kind = "unit"
            elif count:                       # used column: the next slot open, one lock under it (row 5: doctrine)
                nxt = count + 1 if count < OPEN_ROWS else count
                kind = "open" if r == count and r < OPEN_ROWS else ("blocked" if r >= OPEN_ROWS else "locked") if r == nxt else None
            elif c == first_free:             # the next column: its first slot open, the one under it locked
                kind = "open" if r == 0 else "locked" if r == 1 else None
            else:                             # later columns: only a locked first slot
                kind = "locked" if r == 0 else None
            if kind:
                draw_slot(canvas, d, x, y, kind, img(f"icons/{grid[(c, r)]}.png") if kind == "unit" else None)
    # regimental support row (54, 342): one company under a brigade of 3 battalions (REGIMENTAL_SUPPORT_REQUIRED_
    # BATTALIONS), locked under smaller ones
    reg = dict(zip(t.get("regimental_grid", []), t.get("regimental_support", [])))
    for c in range(COLUMNS):
        x, y = 54 + c * 102, 170 + 342
        count = sum(1 for (cc, _) in grid if cc == c)
        kind = "unit" if c in reg else "open" if count >= 3 else "locked"
        draw_slot(canvas, d, x, y, kind, img(f"icons/{reg[c]}.png") if c in reg else None, support=True)
    text(d, (165 + 125, 170 + 400 + 10), "Regimental Support", 15, anchor="mm")

    # support column (552, 130) slots 82 x 35
    text(d, (530 + 55, 170 + 128 + 10), "Support", 15, anchor="mm")
    sup = {y: name for name, y in zip(t["support"], t["support_grid"])}
    for r in range(SUPPORT_ROWS):
        x, y = 552, 170 + 130 + r * 35
        if r in sup:
            draw_slot(canvas, d, x, y, "unit", img(f"icons/{sup[r]}.png"), support=True)
        elif r == len(sup):
            draw_slot(canvas, d, x, y, "open", support=True)
        elif r == len(sup) + 1:
            draw_slot(canvas, d, x, y, "locked", support=True)  # one locked slot under the open one, as HOI4

    # stat sheets: info (660, 13); each sheet's grid of 155 x 20 rows
    def sheet(x0, title, rows):
        text(d, (660 + x0 + 7, 13 + 32 + 10), title, 20, fill=PAPER_INK, anchor="lm")
        for i, (label, value) in enumerate(rows):
            y = 13 + 55 + 5 + i * 20 + 10
            text(d, (660 + x0 + 5, y), label, 13, fill=PAPER_INK, anchor="lm")
            text(d, (660 + x0 + 5 + 160, y), value, 13, fill=PAPER_INK, anchor="rm")

    sheet(7, "Base Stats", [("Max Speed:", f"{st['speed']:.1f} km/h"), ("HP:", f"{st['hp']:.1f}"),
                            ("Organization:", f"{st['org']:.1f}"), ("Recovery Rate:", f"{st['recovery']:.2f}"),
                            ("Reconnaissance:", f"{st['recon']:.1f}"), ("Suppression:", f"{st['suppression']:.1f}"),
                            ("Weight:", f"{st['weight']:.1f}"), ("Supply Use:", f"{st['supply']:.2f}"),
                            ("Av. Reliability:", f"{st['reliability'] * 100:.1f}%")])
    sheet(181, "Combat Stats", [("Soft Attack:", f"{st['soft']:.1f}"), ("Hard Attack:", f"{st['hard']:.1f}"),
                                ("Air Attack:", f"{st['air']:.1f}"), ("Defense:", f"{st['defense']:.1f}"),
                                ("Breakthrough:", f"{st['breakthrough']:.1f}"), ("Armor:", f"{st['armor']:.1f}"),
                                ("Piercing:", f"{st['piercing']:.1f}"), ("Initiative:", f"{st['initiative'] * 100:.2f}%"),
                                ("Entrenchment:", f"{st['entrenchment']:.1f}"), ("Combat Width:", f"{st['width']:g}")])
    names = {a: (country["equipment"].get(a) or {}).get("archetype_name") or a.replace("_", " ").title()
             for a in st["need"]}
    sheet(353, "Equipment Cost", [("Manpower:", f"{st['manpower']}"), ("Training Time:", f"{st['training']}")]
          + [(names[a], f"{v}") for a, v in sorted(st["need"].items())])

    # bottom buttons
    for x, y, sprite, label in ((20, 620, "button_reset.png", "Reset"), (150, 620, "button_123.png", "Duplicate"),
                                (490, 667, "button_221.png", "Save")):
        b = img(sprite)
        canvas.alpha_composite(b, (x, y))
        text(d, (x + b.width / 2, y + b.height / 2), label, 15, anchor="mm")
    canvas.alpha_composite(nine("cost_bg.png", 70, 29, 6), (285 + 15, 624))
    text(d, (285 + 50, 624 + 14), "0", 15, anchor="mm")

    if picker:
        draw_picker(canvas, d, oob, country, t)
    safe = re.sub(r"[^A-Za-z0-9]+", "_", template_name) + ("_picker" if picker else "")
    path = OUT / f"designer_{tag}_{safe}.png"
    canvas.save(path)
    print(path)
    for k in ("hp", "org", "recovery", "suppression", "weight", "supply", "reliability", "soft", "hard", "defense",
              "breakthrough", "armor", "piercing", "width", "manpower", "training"):
        print(f"  {k}: {st[k]:.3f}" if isinstance(st[k], float) else f"  {k}: {st[k]}")
    print("  need:", st["need"])


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("tag", nargs="?", default="FRA")
    ap.add_argument("template", nargs="?", default=None)
    ap.add_argument("--picker", action="store_true", help="draw the battalion category list over it")
    a = ap.parse_args()
    sys.exit(render(a.tag, a.template, a.picker))
