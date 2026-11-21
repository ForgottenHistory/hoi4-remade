"""
Battle plan front line prototype (France vs Belgium, 1936).
- Picks the traced border arcs between the player (FRA) and the target (BEL)
- Orients each arc so the enemy is on the left (+normal), chains them end to start
- Resamples + relaxes the chain (bends wider than the line)
- Renders the line by nearest point on the curve (u along, v across) with HOI4's own textures:
    preview = arrowType magnet_line (stripe_mask / stripe_pattern: the green dashes)
    placed  = arrowType defence_line (defensive_line mask / pattern: band + ticks), army colour
- Also the striped own-side provinces of the preview (blurred province mask, diagonal stripes)
Outputs preview.png and placed.png (map pixels scaled by S).

Usage: python battleplan_line.py [width_px] [relax_passes]
"""
import os
import sys
import numpy as np
from PIL import Image
from scipy.spatial import cKDTree
from scipy.ndimage import gaussian_filter
from common import load, HOI4, HERE

CROP = (2822, 496, 84, 56)
S = 12                      # display scale (HOI4 at that zoom: about 13 screen px per map px)
PLAYER, TARGET = "FRA", "BEL"
WIDTH = float(sys.argv[1]) if len(sys.argv) > 1 else 1.3       # placed line width, map px
RELAX = int(sys.argv[2]) if len(sys.argv) > 2 else 14
PREVIEW_WIDTH = WIDTH * 0.75
COLORS = {"FRA": (70, 100, 170), "BEL": (190, 175, 80), "HOL": (200, 130, 60), "LUX": (120, 160, 140),
          "GER": (90, 90, 90), "ENG": (160, 60, 60)}


def tex(name):
    return np.asarray(Image.open(os.path.join(HOI4, "gfx", "maparrows", name + ".dds")).convert("RGBA")).astype(np.float32) / 255


def border_arcs(owner, arcs):
    """The arcs between the player and the target, with the enemy province of each."""
    out = []
    for pts, a, b, closed in arcs:
        oa, ob = owner.get(a), owner.get(b)
        if {oa, ob} == {PLAYER, TARGET} and len(pts) >= 2:
            out.append((pts, a if oa == TARGET else b, b if oa == TARGET else a))
    return out


def chain(arcs):
    """Join arcs that share endpoints into polylines, ignoring their direction (like the engine's ChainArcs).
    Returns [(points, enemy provinces, own provinces)]."""
    key = lambda p: (round(p[0] * 2), round(p[1] * 2))
    by_end = {}
    for i, (pts, _, _) in enumerate(arcs):
        by_end.setdefault(key(pts[0]), []).append(i)
        by_end.setdefault(key(pts[-1]), []).append(i)
    used = [False] * len(arcs)
    out = []
    # Start at chain ends (an endpoint used by one arc) so open chains come out whole
    order = sorted(range(len(arcs)), key=lambda i: -(len(by_end[key(arcs[i][0][0])]) == 1 or len(by_end[key(arcs[i][0][-1])]) == 1))
    for s in order:
        if used[s]:
            continue
        used[s] = True
        pts = list(arcs[s][0])
        if len(by_end[key(pts[0])]) == 1:
            pass
        elif len(by_end[key(pts[-1])]) == 1:
            pts = pts[::-1]
        enemy, own = {arcs[s][1]}, {arcs[s][2]}
        for direction in range(2):          # grow forward, then from the front
            while True:
                end = key(pts[-1])
                nxt = next((j for j in by_end.get(end, []) if not used[j]), None)
                if nxt is None:
                    break
                used[nxt] = True
                np_ = list(arcs[nxt][0])
                if key(np_[0]) != end:
                    np_ = np_[::-1]
                pts.extend(np_[1:])
                enemy.add(arcs[nxt][1]); own.add(arcs[nxt][2])
            pts = pts[::-1]
        out.append((np.array(pts), enemy, own))
    return out


def orient(ids, chains):
    """Enemy on the left of travel: every segment samples the ids a little to each side and votes."""
    H, W = ids.shape
    out = []
    for pts, enemy, own in chains:
        votes = 0
        for m in range(len(pts) - 1):
            t = pts[m + 1] - pts[m]
            L = np.hypot(*t)
            if L < 1e-6:
                continue
            n = np.array([t[1], -t[0]]) / L     # left of travel in image coords (y down)
            mid = (pts[m] + pts[m + 1]) * 0.5
            for d in (0.7, 1.5, 2.5):
                for sgn in (1, -1):
                    x, y = int(np.floor(mid[0] + n[0] * d * sgn)), int(np.floor(mid[1] + n[1] * d * sgn))
                    if 0 <= x < W and 0 <= y < H:
                        pid = ids[y, x]
                        if pid in enemy: votes += sgn
                        elif pid in own: votes -= sgn
        out.append(pts if votes >= 0 else pts[::-1])
    return out


def relax(c, spacing, passes):
    seg = np.hypot(*np.diff(c, axis=0).T)
    s = np.concatenate([[0], np.cumsum(seg)])
    n = max(int(s[-1] / spacing), 1)
    t = np.linspace(0, s[-1], n + 1)
    r = np.column_stack([np.interp(t, s, c[:, 0]), np.interp(t, s, c[:, 1])])
    for _ in range(passes):
        r[1:-1] = (r[:-2] + 2 * r[1:-1] + r[2:]) * 0.25
    return r


def background(ids, owner):
    H, W = ids.shape
    col = np.zeros((H, W, 3), np.float32)
    for pid in np.unique(ids):
        col[ids == pid] = COLORS.get(owner.get(pid), (60, 70, 90))
    big = np.repeat(np.repeat(col, S, 0), S, 1) / 255
    idb = np.repeat(np.repeat(ids, S, 0), S, 1)
    edge = (idb != np.roll(idb, 1, 0)) | (idb != np.roll(idb, 1, 1))
    big[edge] *= 0.55
    return big


def stripes(img, ids, provinces, hot):
    """Own border provinces: blurred mask (soft edges instead of the pixel staircase), diagonal stripes."""
    H, W = ids.shape
    up = lambda m: gaussian_filter(np.repeat(np.repeat(m, S, 0), S, 1), S * 0.45)
    m = up(np.isin(ids, list(provinces)).astype(np.float32))
    h = up((ids == hot).astype(np.float32))
    yy, xx = np.mgrid[0:H * S, 0:W * S] / S
    st = np.clip((np.sin((xx + yy) * 2 * np.pi / 3.4) + 0.35) / 0.7, 0, 1)
    k = (m * (0.12 + 0.38 * st) + h * 0.15)[..., None]
    green = np.array([0.36, 0.92, 0.32])
    return img * (1 - k) + green * k + h[..., None] * 0.05


def draw_line(img, chains, width, mask, pattern, tile_widths, color):
    """Nearest point on the curve gives u (along, in tiles) and v (across, 0 own side .. 1 enemy side)."""
    Hs, Ws = img.shape[:2]
    samples, arclen, normals = [], [], []
    for c in chains:
        L = np.hypot(*np.diff(c, axis=0).T)
        s = np.concatenate([[0], np.cumsum(L)])
        t = np.linspace(0, s[-1], max(int(s[-1] / 0.02), 2))
        p = np.column_stack([np.interp(t, s, c[:, 0]), np.interp(t, s, c[:, 1])])
        d = np.gradient(p, axis=0)
        d /= np.hypot(*d.T)[:, None] + 1e-9
        samples.append(p); arclen.append(t); normals.append(np.column_stack([d[:, 1], -d[:, 0]]))
    P, A, N = np.vstack(samples), np.concatenate(arclen), np.vstack(normals)
    tree = cKDTree(P)
    yy, xx = np.mgrid[0:Hs, 0:Ws]
    q = np.column_stack([(xx.ravel() + 0.5) / S, (yy.ravel() + 0.5) / S])
    dist, idx = tree.query(q, distance_upper_bound=width)
    ok = np.isfinite(dist)
    qi, ii = np.nonzero(ok)[0], idx[ok]
    sd = np.einsum("ij,ij->i", q[qi] - P[ii], N[ii])
    v = 0.5 + sd / width
    u = A[ii] / (width * tile_widths)
    inside = (v >= 0) & (v <= 1)
    qi, u, v = qi[inside], u[inside], v[inside]
    th, tw = mask.shape[:2]
    tx = ((u % 1.0) * tw).astype(int) % tw
    ty = np.clip((v * th).astype(int), 0, th - 1)   # image top row (the band) = v 0 = own side; the teeth point at the enemy
    mk, pt = mask[ty, tx], pattern[ty, tx]
    cov = np.clip(mk[:, 0] + mk[:, 1] + mk[:, 2], 0, 1) * mk[:, 3]
    c = np.array(color)
    grey = c @ np.array([0.299, 0.587, 0.114])
    c = np.clip(grey + (c - grey) * 1.5, 0, 1)
    rgb = np.clip(pt[:, :3] * c * 1.4, 0, 1)
    flat = img.reshape(-1, 3)
    flat[qi] = flat[qi] * (1 - cov[:, None]) + rgb * cov[:, None]
    return flat.reshape(img.shape)


def save(img, name):
    Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).save(os.path.join(HERE, name))


def main():
    ids, owner, arcs = load(CROP)
    border = border_arcs(owner, arcs)
    chains = [relax(c, 0.25, RELAX) for c in orient(ids, chain(border))]
    print(f"{len(border)} arcs -> {len(chains)} chains, width {WIDTH} px, relax {RELAX}")
    own = set()
    for pts, a, b, closed in arcs:
        if {owner.get(a), owner.get(b)} == {PLAYER, TARGET}:
            own.add(a if owner.get(a) == PLAYER else b)
    bg = background(ids, owner)
    pre = stripes(bg.copy(), ids, own, hot=11483)
    save(draw_line(pre, chains, PREVIEW_WIDTH, tex("stripe_mask"), tex("stripe_pattern"), 1.0, (0.36, 0.92, 0.32)), "preview.png")
    save(draw_line(bg.copy(), chains, WIDTH, tex("defensive_line_mask"), tex("defensive_line"), 3.2, (1.0, 0.78, 0.6)), "placed.png")  # texture_loop 3.2


if __name__ == "__main__":
    main()
