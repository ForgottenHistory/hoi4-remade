"""
Construction map mode prototype (northern France, 1936 map), compared with a HOI4 screenshot of civilian factory
placement: every state of the player tinted green while it has room for the building, blue when it is full;
states with an order for it get broad "/" bands, yellow while the state still has room, blue when the order
fills it. Other countries' land darkens.

Measured on the screenshot (1972 px wide, zoomed in): Ile de France ~440 screen px wide, the bands ~74 screen px
apart (centre to centre, measured across), band and gap about equal.

Usage: python construction_map.py
Writes construction_map.png (map px scaled by S).
"""
import json
import os
import re
import sys

import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.join(HERE, "..", "..", "..")
sys.path.insert(0, os.path.join(HERE, "..", "battleplan"))
from common import tb  # noqa: E402

S = 4
GREEN = np.array([0.30, 0.85, 0.25])
BLUE = np.array([0.20, 0.35, 1.00])
YELLOW = np.array([1.00, 0.90, 0.10])
# the screenshot's civilian factory orders: state -> (queued levels, the state full with them: blue bands, else yellow)
ORDERS = {16: (2, True), 24: (1, False)}       # Ile de France 8+2/10 (blue bands), Centre 2+1/5 (yellow)
# fill: blue when the state has no room for the building counting what is built and other buildings' orders, but
# not the orders for this building (those show as bands): Bourgogne 2 built + 4 military factories queued of 6
FULL = {26}


def states():
    t = open(os.path.join(ROOT, "Hoi4Data", "map", "hoi4_state_data.json5"), encoding="utf-8").read()
    return json.loads(re.sub(r"^\s*//.*$", "", t, flags=re.M))["states"]


def main():
    ids, _ = tb.load_ids()
    prov_state, state_owner = tb.load_states()
    own = {s for s, o in state_owner.items() if o == "FRA"}
    focus = [16, 24, 26, 15, 785, 29, 18, 27, 23]
    ys, xs = np.nonzero(np.isin(ids, [p for p, s in prov_state.items() if s in focus]))
    x0, y0, x1, y1 = xs.min() - 6, ys.min() - 6, xs.max() + 6, ys.max() + 6
    sub = ids[y0:y1, x0:x1]
    H, W = sub.shape
    state = np.vectorize(lambda p: prov_state.get(int(p), -1))(sub)

    idf = np.nonzero(state == 16)[1]
    width = idf.max() - idf.min() + 1
    period = 74 * width / 440
    print(f"crop {x0},{y0} {W}x{H}; Ile de France {width} map px wide -> band period {period:.1f} map px")

    up = lambda m: np.repeat(np.repeat(m.astype(np.float32), S, 0), S, 1)
    soft = lambda m: gaussian_filter(up(m), S * 0.6)
    # background: a flat land colour, province edges, our states lighter than the neighbours
    img = np.ones((H * S, W * S, 3), np.float32) * np.array([0.42, 0.45, 0.36])
    idb = np.repeat(np.repeat(sub, S, 0), S, 1)
    edge = (idb != np.roll(idb, 1, 0)) | (idb != np.roll(idb, 1, 1))
    img[edge] *= 0.8
    stb = np.repeat(np.repeat(state, S, 0), S, 1)
    sedge = (stb != np.roll(stb, 1, 0)) | (stb != np.roll(stb, 1, 1))
    img[sedge] *= 0.6

    mine = np.isin(state, list(own))
    full = np.isin(state, list(FULL))
    # tint: green with room, blue full; other countries darker
    k = (soft(mine & ~full) * 0.55)[..., None]
    img = img * (1 - k) + GREEN * k
    k = (soft(mine & full) * 0.6)[..., None]
    img = img * (1 - k) + BLUE * k
    img *= (1 - soft(~mine) * 0.35)[..., None]

    yy, xx = np.mgrid[0:H * S, 0:W * S] / S
    a = np.deg2rad(40)
    across = -np.sin(a) * xx - np.cos(a) * yy          # bands rise to the right ('/')
    band = np.clip((np.sin(across * 2 * np.pi / period) + 0.05) / 0.35, 0, 1)
    for s, (_, f) in ORDERS.items():
        m = soft(state == s)
        core = np.clip((m - 0.35) / 0.5, 0, 1)
        k = (core * band * 0.9)[..., None]
        img = img * (1 - k) + (BLUE if f else YELLOW) * k

    Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).save(os.path.join(HERE, "construction_map.png"))
    print("-> construction_map.png")


if __name__ == "__main__":
    main()
