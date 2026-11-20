"""Prototype: compose HOI4 on-map division counters from the game's sprites (interface/mapicons.gui
layout) and render a preview sheet at 1x and 4x. Usage: python render_counter.py [--hoi4 PATH]"""
import argparse
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ap = argparse.ArgumentParser()
ap.add_argument("--hoi4", default=r"C:/Program Files (x86)/Steam/steamapps/common/Hearts of Iron IV")
args = ap.parse_args()
H = Path(args.hoi4)
OUT = Path(__file__).parent


def load(rel):
    return Image.open(H / rel).convert("RGBA")


def frame(im, i, n):
    w = im.width // n
    return im.crop((i * w, 0, (i + 1) * w, im.height))


bg = load("gfx/interface/onmap_unit_counter.dds")            # 3 frames 66x27
ideo = load("gfx/interface/onmap_unit_counter_ideology.dds")  # 4 frames 28x27
org = load("gfx/interface/mapicons/mapicon_bar_org_sprite.dds")  # 3x17
strb = load("gfx/interface/mapicons/mapicon_bar_str_sprite.dds")
IDEO = {"democratic": 0, "communism": 1, "fascism": 2, "neutrality": 3}
try:
    font = ImageFont.truetype("arialbd.ttf", 13)
except OSError:
    font = ImageFont.load_default()


def counter(tag, ideology, icon, count, org_f, str_f, bg_frame=0):
    c = Image.new("RGBA", (70, 28), (0, 0, 0, 0))
    c.alpha_composite(frame(bg, bg_frame, 3), (0, 0))
    c.alpha_composite(frame(ideo, IDEO[ideology], 4), (37, 0))
    flag = Image.open(H / f"gfx/flags/small/{tag}_{ideology}.tga").convert("RGBA")
    c.alpha_composite(flag, (5, 15))
    ic = frame(load(f"gfx/interface/counters/divisions_small/onmap_unit_{icon}_icon.dds"), 0, 2)
    c.alpha_composite(ic, (3, 2))
    for y, bar, f in ((16, org, org_f), (19, strb, str_f)):
        h = bar.rotate(90, expand=True)  # 17x3
        full = int(round(17 * f))
        dark = Image.new("RGBA", (17, 3), (20, 20, 20, 200))
        c.alpha_composite(dark, (16, y))
        if full:
            c.alpha_composite(h.crop((0, 0, full, 3)), (16, y))
    d = ImageDraw.Draw(c)
    d.text((46, 13), str(count), font=font, fill=(235, 235, 225, 255), anchor="mm")
    return c


rows = [("GER", "fascism", "infantry", 3, 1.0, 1.0), ("SOV", "communism", "cavalry", 12, 0.6, 0.9),
        ("ENG", "democratic", "motorized", 1, 1.0, 0.4), ("ITA", "fascism", "light_tank", 2, 0.3, 1.0),
        ("FRA", "democratic", "mountain", 5, 0.9, 0.8), ("JAP", "neutrality" if False else "fascism", "marine", 1, 1, 1),
        ("CHI", "neutrality", "militia", 24, 0.8, 0.5), ("POL", "neutrality", "art", 2, 1, 1)]
cs = [counter(*r) for r in rows]
sheet = Image.new("RGBA", (300 + 70 * 4 + 20, 32 * 4 * len(cs)), (86, 104, 78, 255))
for i, c in enumerate(cs):
    sheet.alpha_composite(c, (10, i * 128 + 50))
    sheet.alpha_composite(c.resize((280, 112), Image.NEAREST), (100, i * 128 + 8))
sheet.save(OUT / "counters.png")
print("wrote", OUT / "counters.png")
