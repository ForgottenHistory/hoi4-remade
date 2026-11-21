"""
Offensive plan placed by dragging (HOI4): right-press on an enemy province, drag across the provinces to take,
release. Same view as offensive.py (France vs Belgium, 1936). A synthetic cursor trail like the HOI4 screenshot:
from Gent across Antwerp to the east.

While dragging (offensive tool on):
- enemy provinces yellow-striped as before; the provinces the cursor has crossed (the targets) green-striped
- the cursor trail as a dashed line: arrowType magnet_line (stripe_mask / stripe_pattern)
- the targets' centres joined by a smooth dotted line: arrowType virtual_line (dot_mask / dot_pattern,
  texture_loop 1: one dot per width), a ring at each end (HOI4's btn_drag buttons)
After release: the objective line along the far side of all targets (the side away from the arrow), the arrow
from the front into the middle of the targets.
Outputs drag_preview.png and drag_placed.png.

Usage: python drag.py
"""
import sys
import numpy as np
from scipy.ndimage import gaussian_filter
from common import load, centroid
import offensive as off
from offensive import (CROP, S, PLAYER, TARGET, WIDTH, tex, border_arcs, chain, orient, relax, background,
                       draw_line, save, enemy_stripes, draw_arrow, neighbours, ARMY, OBJ_WIDTH, OBJ_TILE, OBJ_SCALE,
                       ROOT_BACK, BEND)

TRAIL = np.array([(36.0, 12.5), (43, 9.5), (50, 8.0), (57, 7.0), (63, 7.5), (68, 9.0), (71.5, 10.5)])
TRAIL_WIDTH = 0.55          # map px (magnet_line 0.3 of a front line of ~1.8)
DOT_WIDTH = 1.0             # map px (virtual_line 0.5)
RING = 1.6                  # ring radius at each end of the dotted line, map px
PICKED = (0.36, 0.92, 0.32)


def dense(pts, step=0.25):
    seg = np.hypot(*np.diff(pts, axis=0).T)
    s = np.concatenate([[0], np.cumsum(seg)])
    t = np.linspace(0, s[-1], max(int(s[-1] / step), 2))
    return np.column_stack([np.interp(t, s, pts[:, 0]), np.interp(t, s, pts[:, 1])])


def picked_provinces(ids, owner, trail):
    """The enemy provinces under the trail, in the order the cursor reached them."""
    out = []
    for x, y in dense(trail, 0.25):
        p = int(ids[int(y), int(x)])
        if owner.get(p) == TARGET and p not in out:
            out.append(p)
    return out


def picked_stripes(img, ids, provinces):
    """Green bands (same pattern as the yellow ones, stronger) over the targets."""
    H, W = ids.shape
    m = np.isin(ids, list(provinces)).astype(np.float32)
    soft = gaussian_filter(np.repeat(np.repeat(m, S, 0), S, 1), S * 0.45)
    yy, xx = np.mgrid[0:H * S, 0:W * S] / S
    ang = np.deg2rad(25)
    across = -np.sin(ang) * xx - np.cos(ang) * yy
    band = np.clip((np.sin(across * 2 * np.pi / 3.3) + 0.2) / 0.6, 0, 1)
    k = (soft * (0.10 + 0.55 * band))[..., None]
    return img * (1 - k) + np.array(PICKED) * k


def spine(ids, targets):
    """The targets' centres in drag order, smoothed: the dotted line, and what the objective line follows."""
    c = np.array([centroid(ids, p) for p in targets])
    if len(c) == 1:
        return c
    return relax(c, 0.25, 40)


def ring(img, centre, radius, color, width=0.35):
    H, W = img.shape[:2]
    yy, xx = np.mgrid[0:H, 0:W]
    d = np.hypot((xx + 0.5) / S - centre[0], (yy + 0.5) / S - centre[1])
    k = np.clip(1 - np.abs(d - radius) / width, 0, 1)[..., None] * 0.55
    return img * (1 - k) + np.array(color) * k


def objective_far_side(ids, arcs, targets, sp, root):
    """The union of the targets' outline on the side of the spine away from the root, ordered along the spine,
    pulled towards it and relaxed. Ends wrap a little around the spine's ends."""
    tset = set(targets)
    pts = np.vstack([p for p, a, b, closed in arcs if (a in tset) != (b in tset) and len(p) >= 2])
    if len(sp) == 1:
        return off_single(ids, arcs, targets[0], root)
    d = np.gradient(sp, axis=0)
    d /= np.hypot(*d.T)[:, None]
    nrm = np.column_stack([d[:, 1], -d[:, 0]])
    # The normal pointing away from the root
    mid = len(sp) // 2
    if (root - sp[mid]) @ nrm[mid] > 0:
        nrm = -nrm
    keep, order = [], []
    for p in pts:
        i = np.argmin(np.hypot(*(sp - p).T))
        r = p - sp[i]
        along = r @ d[i]
        side = r @ nrm[i]
        end = (i == 0 and along < 0) or (i == len(sp) - 1 and along > 0)
        if side > 0 or (end and side > -0.5 * abs(along)):
            keep.append(sp[i] + r * OBJ_SCALE)
            order.append(i + along / 0.25 * 0.01 if not end else i + np.sign(along) * (2 - side / (abs(along) + 1e-6)))
    keep = np.array(keep)[np.argsort(order)]
    line = relax(keep, 0.25, 60)[3:-3]
    # Teeth point at the spine (left of travel in draw_line's convention: -normal side)
    m = len(line) // 2
    t = line[m + 1] - line[m - 1]
    n = np.array([t[1], -t[0]])
    if (line[m] - sp[np.argmin(np.hypot(*(sp - line[m]).T))]) @ n < 0:
        line = line[::-1]
    return line


def off_single(ids, arcs, target, root):
    off.TARGET_PROVINCE = target
    return off.objective_line(ids, arcs, root)


def main():
    ids, owner, arcs = load(CROP)
    front = [relax(c, 0.25, 14) for c in orient(ids, chain(border_arcs(owner, arcs)))]
    enemy = {p for p in np.unique(ids) if owner.get(p) == TARGET}
    targets = picked_provinces(ids, owner, TRAIL)
    print("targets", targets)
    sp = spine(ids, targets)
    bg = background(ids, owner)
    defence = (tex("defensive_line_mask"), tex("defensive_line"), 3.2)
    trail_col = tuple(np.array(ARMY) * 0.8)

    pre = enemy_stripes(bg.copy(), ids, enemy - set(targets))
    pre = picked_stripes(pre, ids, targets)
    pre = draw_line(pre, front, WIDTH, *defence, ARMY)
    pre = draw_line(pre, [dense(TRAIL)], TRAIL_WIDTH, tex("stripe_mask"), tex("stripe_pattern"), 1.0, trail_col)
    if len(sp) > 1:
        pre = draw_line(pre, [sp], DOT_WIDTH, tex("dot_mask"), tex("dot_pattern"), 1.0, (0.85, 0.8, 0.7))
    pre = ring(pre, sp[0], RING, (0.9, 0.9, 0.85))
    pre = ring(pre, sp[-1], RING, (0.9, 0.9, 0.85))
    save(pre, "drag_preview.png")

    # Placed: the arrow into the middle target, the objective line along the far side of all of them
    middle = targets[len(targets) // 2]
    off.TARGET_PROVINCE = middle
    path = off.offensive_path(ids, owner)
    tip = sp[len(sp) // 2]
    root, ctrl, _ = off.arrow_controls(ids, front, path)
    ctrl = root + (ctrl - root) / np.hypot(*(ctrl - root)) * np.hypot(*(tip - root)) * BEND
    placed = draw_arrow(bg.copy(), root, ctrl, tip)
    placed = draw_line(placed, front, WIDTH, *defence, ARMY)
    obj = objective_far_side(ids, arcs, targets, sp, root)
    placed = draw_line(placed, [obj], OBJ_WIDTH, tex("offensive_line_mask"), tex("offensive_line"), OBJ_TILE, ARMY)
    save(placed, "drag_placed.png")


if __name__ == "__main__":
    main()
