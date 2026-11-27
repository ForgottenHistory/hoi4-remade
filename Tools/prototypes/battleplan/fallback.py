"""
Fallback line prototype (France, 1936; between La Rochelle and Tours, the view of the HOI4 screenshots).
The fallback tool stripes the provinces a line may cross (own and allied land) yellow. A right-drag picks the
provinces the cursor crosses (green), like the offensive drag (drag.py): the cursor trail dashed (magnet_line),
a ring at each end; but the line through the picked provinces' centres is the fallback line itself, HOI4's
arrowType fallback_line (fall_back_line mask / pattern: one X per tile), in the army's colour. Placed, only
the X line stays.

Measured on the HOI4 screenshot (about 20 screen px per map px there): an X is ~2.1 map px across the line
and they repeat every ~2.9 map px, so a tile spans 1.4 line widths.
Outputs fallback_preview.png and fallback_placed.png (map pixels scaled by S).

Usage: python fallback.py [width_px] [tile_widths] [smooth_passes]
"""
import os
import sys
ARGS = sys.argv[1:]
sys.argv = sys.argv[:1]   # battleplan_line reads its own arguments on import
import numpy as np
import common
common.CACHE = os.path.join(common.HERE, '_cache_fallback.pkl')   # its own crop: keeps the other prototypes' cache
from common import load
from battleplan_line import tex, background, draw_line, save, COLORS
from battleplan_line import relax
from common import centroid
from drag import dense, picked_stripes, ring, TRAIL_WIDTH, RING
from offensive import enemy_stripes

CROP = (2775, 560, 84, 60)
WIDTH = float(ARGS[0]) if len(ARGS) > 0 else 2.2      # map px (fallback_line body_width 1.5, a front line's 1.0)
TILE = float(ARGS[1]) if len(ARGS) > 1 else 1.4       # an X every this many widths
SMOOTH = int(ARGS[2]) if len(ARGS) > 2 else 60       # coarse relax passes
ARMY = (0.87, 0.41, 0.47)
# The cursor's way in the screenshot: from the coast north of La Rochelle to between Tours and Chateauroux
TRAIL = np.array([(19.0, 43.0), (23.0, 37.5), (30.0, 33.0), (40.0, 30.5), (50.0, 28.5), (58.0, 27.5)])


def picked_provinces(ids, valid, trail):
    """The valid provinces under the trail, in the order the cursor reached them."""
    out = []
    for x, y in dense(trail, 0.25):
        p = int(ids[int(y), int(x)])
        if p in valid and p not in out:
            out.append(p)
    return out


def spine(ids, provinces):
    """The line: through the provinces' centres in drag order, as one smooth curve like HOI4's. Relaxed coarse
    (1 px spacing: the bends between centres ~10 px apart go) and then fine. drag.py's spine (0.25 px, 40
    passes) keeps a corner at every centre, which skews the X there."""
    c = np.array([centroid(ids, p) for p in provinces])
    if len(c) == 1:
        return c
    return relax(relax(c, 1.0, SMOOTH), 0.25, 4)


def main():
    ids, owner, arcs = load(CROP)
    COLORS["FRA"] = (96, 110, 82)
    valid = {int(p) for p in np.unique(ids) if owner.get(p) == "FRA"}
    picked = picked_provinces(ids, valid, TRAIL)
    sp = spine(ids, picked)
    print("line provinces", picked, "length", round(float(np.hypot(*np.diff(sp, axis=0).T).sum()), 1), "map px")
    bg = background(ids, owner)
    line = (tex("fall_back_line_mask"), tex("fall_back_line"), TILE)

    pre = enemy_stripes(bg.copy(), ids, valid - set(picked))
    pre = picked_stripes(pre, ids, picked)
    pre = draw_line(pre, [dense(TRAIL)], TRAIL_WIDTH, tex("stripe_mask"), tex("stripe_pattern"), 1.0, tuple(np.array(ARMY) * 0.9))
    pre = draw_line(pre, [sp], WIDTH, *line, ARMY)
    pre = ring(pre, sp[0], RING, (0.9, 0.9, 0.85))
    pre = ring(pre, sp[-1], RING, (0.9, 0.9, 0.85))
    save(pre, "fallback_preview.png")

    placed = draw_line(bg.copy(), [sp], WIDTH, *line, ARMY)
    save(placed, "fallback_placed.png")
    # Nobody on it: fallback_line's alternative mask
    save(draw_line(bg.copy(), [sp], WIDTH, tex("fall_back_line_unassigned_mask"), tex("fall_back_line"), TILE, ARMY),
         "fallback_unassigned.png")


if __name__ == "__main__":
    main()
