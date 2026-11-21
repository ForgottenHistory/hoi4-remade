"""
Peace conference map highlight prototype (France over Belgium, 1936 map). Compares with a HOI4 screenshot of
the same conference: the loser's states in its own colour (occupation hidden), broad green bands rising to the
right at ~35 degrees over every state still on the table, blue bands over the states the player has demanded,
the stripes fading out softly at the country's edge where a glow of the country colour shows.

Measured on the screenshot (2558 px wide): bands ~90 screen px apart, Belgium ~840 screen px wide.

Usage: python peace_stripes.py [period_map_px] [angle_deg]
Writes peace_stripes.png (map px scaled by S, battleplan prototypes' crop).
"""
import os
import sys
import json
ARGS = sys.argv[1:]
sys.argv = sys.argv[:1]
HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(HERE, "..", "battleplan"))
import numpy as np  # noqa: E402
from scipy.ndimage import gaussian_filter  # noqa: E402
from common import load  # noqa: E402
from battleplan_line import CROP, S, background, save  # noqa: E402

ROOT = os.path.join(HERE, "..", "..", "..")
GREEN = np.array([0.30, 0.95, 0.25])
BLUE = np.array([0.30, 0.55, 1.00])


def province_states():
    path = os.path.join(ROOT, "Hoi4Data", "map", "hoi4_regions.json5")
    result = {}
    inside = False
    for line in open(path, encoding="utf-8"):
        s = line.strip()
        if s.startswith("states:"):
            inside = True
            continue
        if inside:
            if s.startswith("}"):
                break
            key, _, provinces = s.partition(":")         # "1": [3838, 9851, 11804],
            for p in json.loads(provinces.strip().rstrip(",")):
                result[p] = int(key.strip('"'))
    return result


def stripes(img, ids, provinces, colour, period, angle):
    H, W = ids.shape
    m = np.isin(ids, list(provinces)).astype(np.float32)
    big = np.repeat(np.repeat(m, S, 0), S, 1)
    soft = gaussian_filter(big, S * 0.6)                       # bands fade in from the edge
    core = np.clip((soft - 0.35) / 0.5, 0, 1)
    yy, xx = np.mgrid[0:H * S, 0:W * S] / S
    a = np.deg2rad(angle)
    across = -np.sin(a) * xx - np.cos(a) * yy                  # bands rise to the right ('/')
    band = np.clip((np.sin(across * 2 * np.pi / period) + 0.1) / 0.5, 0, 1)
    k = (core * band * 0.62)[..., None]
    dark = (core * (1 - band) * 0.18)[..., None]               # between the bands the land darkens a little
    return img * (1 - k - dark) + colour * k


def edge_glow(img, ids, provinces, colour):
    m = np.isin(ids, list(provinces)).astype(np.float32)
    big = np.repeat(np.repeat(m, S, 0), S, 1)
    inner = np.clip(big - gaussian_filter(big, S * 1.6), 0, 1) * 1.6
    k = np.clip(inner, 0, 0.55)[..., None]
    return img * (1 - k) + colour * k


def main():
    period = float(ARGS[0]) if len(ARGS) > 0 else 4.0
    angle = float(ARGS[1]) if len(ARGS) > 1 else 35.0
    ids, owner, _ = load(CROP)
    pstate = province_states()
    bel = {p for p in np.unique(ids) if owner.get(p) == "BEL"}
    xs = np.nonzero(np.isin(ids, list(bel)))[1]
    width = xs.max() - xs.min() + 1
    print(f"Belgium {width} map px wide -> HOI4 band period {90 * width / 840:.1f} map px (using {period})")
    demanded_state = pstate.get(516)                            # Brussels' state, as if the player demanded it
    demanded = {p for p in bel if pstate.get(p) == demanded_state}
    rest = bel - demanded
    img = background(ids, owner)
    img = edge_glow(img, ids, bel, np.array([0.95, 0.88, 0.35]))
    img = stripes(img, ids, rest, GREEN, period, angle)
    img = stripes(img, ids, demanded, BLUE, period, angle)
    save(img, os.path.join(HERE, "peace_stripes.png"))
    print(f"demanded state {demanded_state}: {len(demanded)} provinces; on the table {len(rest)}")


if __name__ == "__main__":
    main()
