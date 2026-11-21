"""
Offensive battle plan prototype (France vs Belgium, 1936; target Brussels, province 516).
Compares against HOI4 screenshots of the same view: the offensive tool's yellow-striped enemy country,
the offensive arrow from the front line to the target and the objective line wrapped around the target.

- Front line: as battleplan_line.py (arrowType defence_line, army colour)
- Preview (tool on): enemy provinces tinted yellow with diagonal bands and a bright inner glow at the
  country's edge
- Arrow: arrowType move_thick textures (default_mask / default_pattern: left half body, right half one
  chevron head), in the army colour, wider than a unit move arrow; root behind the line, tip in the
  target; soft dark drop shadow to the lower left
- Objective line: arrowType offence_line (offensive_line mask / pattern, texture_loop 2, band at the
  bottom row, a tooth in the middle pointing up = outward), around the far side of the target province
  (the half facing away from the arrow), relaxed smooth
Outputs offensive_preview.png and offensive_placed.png (map pixels scaled by S).

Usage: python offensive.py [ribbon_px] [bend] [target_province] [root_near_x root_near_y]
"""
import sys
ARGS = sys.argv[1:]
sys.argv = sys.argv[:1]   # battleplan_line reads its own arguments on import
import numpy as np
from scipy.ndimage import gaussian_filter, shift as nd_shift
from common import load, centroid
from battleplan_line import (CROP, S, PLAYER, TARGET, WIDTH, tex, border_arcs, chain, orient, relax,
                             background, draw_line, save)

TARGET_PROVINCE = int(ARGS[2]) if len(ARGS) > 2 else 516   # Brussels; 13068 = the HOI4 screenshot's
# Root: the front point nearest this (crop map px); default the target itself. 60 38 = the screenshot's Givet bulge
ROOT_NEAR = (float(ARGS[3]), float(ARGS[4])) if len(ARGS) > 4 else None
ARMY = (1.0, 0.78, 0.6)                    # the army colour of the front line
ARROW = (0.96, 0.80, 0.52)                 # arrow fill (army colour, lighter)
OPACITY = 0.8
SHADOW_OFFSET = (-1.2, 0.6)                # map px (x, y)
SHADOW = 0.55
OBJ_SCALE = 0.75                           # objective line pulled towards the target
OBJ_WIDTH = 2.0                            # map px
OBJ_TILE = 0.8                             # tooth spacing / width (HOI4: a tooth about every 1.6 map px)


def enemy_stripes(img, ids, provinces):
    """Yellow tint with diagonal bands over the enemy's provinces, brighter towards the country's edge."""
    H, W = ids.shape
    m = np.isin(ids, list(provinces)).astype(np.float32)
    big = np.repeat(np.repeat(m, S, 0), S, 1)
    soft = gaussian_filter(big, S * 0.45)
    inner = np.clip(soft - gaussian_filter(big, S * 2.2) * 0.85, 0, 1) * big   # glow just inside the edge
    yy, xx = np.mgrid[0:H * S, 0:W * S] / S
    ang = np.deg2rad(25)
    across = -np.sin(ang) * xx - np.cos(ang) * yy          # bands rise to the right ('/')
    band = np.clip((np.sin(across * 2 * np.pi / 3.3) + 0.2) / 0.6, 0, 1)
    yellow = np.array([0.90, 0.88, 0.32])
    k = (soft * (0.20 + 0.22 * band) + inner * 0.45)[..., None]
    return img * (1 - k) + yellow * k


# Arrow geometry: HOI4 arrowType move_thick. The body ribbon maps the middle BODY_UV of the texture
# height (its band, rows 0.35..0.65, is then 0.75 of the ribbon); the head mesh is ribbon / BODY_UV wide
# and maps the full height, so the head's barbs come out ~2.3x the visible body (as in HOI4).
RIBBON = float(ARGS[0]) if len(ARGS) > 0 else 7.5            # body ribbon width at the root, map px (visible ~5.6)
RIBBON_TAPER = 0.9      # at the head
BODY_UV = 0.4           # move_thick body_uv_scale
HEAD_LEN = 7.0          # map px
HEAD_X = 0.63           # head columns of default_mask start here
SHADOW_PX = (-1.8, 0.4)
FADE = float(ARGS[1]) if len(ARGS) > 1 else 7.0              # body fades in (smoothstep) over this, map px
ROOT_BACK = 4.0                                              # root this far behind the front line, map px


BEND = 0.5             # HOI4 bends once: the control point this far (of root-tip) out along the front's normal


def bezier(p0, p1, p2, n=200):
    t = np.linspace(0, 1, n)[:, None]
    return (1 - t) ** 2 * p0 + 2 * (1 - t) * t * p1 + t ** 2 * p2


def arrow_mesh(root, ctrl, tip):
    """Vertices (x, y, u, v, is_head, fade) and triangles, the same as the C# builder. One bend: a quadratic
    curve leaving the root along the front's normal (towards ctrl) and turning to the tip."""
    curve = bezier(root, ctrl, tip)
    cum = np.concatenate([[0], np.cumsum(np.hypot(*np.diff(curve, axis=0).T))])
    L = cum[-1]
    head = min(HEAD_LEN, L * 0.45)
    # Straight head along the chord of the curve's last `head` length; the body a cubic from the root (still
    # leaving towards ctrl) to the head's start, its end tangent the head's direction
    chord_start = curve[min(np.searchsorted(cum, L - head), len(curve) - 1)]
    hd = (tip - chord_start) / np.hypot(*(tip - chord_start))
    head_start = tip - hd * head
    c1 = root + (ctrl - root) * 0.66
    c2 = head_start - hd * (np.hypot(*(head_start - root)) * 0.45)
    t = np.linspace(0, 1, 120)[:, None]
    body = (1 - t) ** 3 * root + 3 * (1 - t) ** 2 * t * c1 + 3 * (1 - t) * t ** 2 * c2 + t ** 3 * head_start
    bcum = np.concatenate([[0], np.cumsum(np.hypot(*np.diff(body, axis=0).T))])
    blen = bcum[-1]
    tile = RIBBON * (0.45 * 256) / (BODY_UV * 128)  # a tile spans 0.45 of the texture width: keeps its aspect
    verts, tris = [], []

    def pair(p, d, half, u, ih, along, connect):
        n = len(verts)
        s = np.array([-d[1], d[0]]) * half
        verts.append((*(p - s), u, 0, ih, along)); verts.append((*(p + s), u, 1, ih, along))
        if connect:
            tris.extend([(n - 2, n, n - 1), (n - 1, n, n + 1)])

    for i in range(len(body)):
        a, b = body[max(i - 2, 0)], body[min(i + 2, len(body) - 1)]
        d = hd if i == len(body) - 1 else (b - a) / np.hypot(*(b - a))
        f = bcum[i] / blen
        w = RIBBON * (1 - (1 - RIBBON_TAPER) * f)
        pair(body[i], d, w / 2, bcum[i] / tile, 0, bcum[i] / FADE, i > 0)
    hw = RIBBON * RIBBON_TAPER / BODY_UV
    for k in range(9):
        f = k / 8
        pair(head_start + hd * head * f, hd, hw / 2, f, 1, 1, k > 0)
    return np.array(verts), tris


def raster(shape, verts, tris):
    """Coverage and pattern per output pixel, like Hoi4/PlanArrow's fragment shader."""
    mask, pattern = tex("default_mask"), tex("default_pattern")
    th, tw = mask.shape[:2]
    Hs, Ws = shape
    cov = np.zeros((Hs, Ws), np.float32)
    rgb = np.zeros((Hs, Ws, 3), np.float32)
    for a, b, c in tris:
        A, B, C = verts[a, :2] * S, verts[b, :2] * S, verts[c, :2] * S
        x0, x1 = int(max(min(A[0], B[0], C[0]), 0)), int(min(max(A[0], B[0], C[0]) + 1, Ws))
        y0, y1 = int(max(min(A[1], B[1], C[1]), 0)), int(min(max(A[1], B[1], C[1]) + 1, Hs))
        if x1 <= x0 or y1 <= y0:
            continue
        yy, xx = np.mgrid[y0:y1, x0:x1] + 0.5
        den = (B[1] - C[1]) * (A[0] - C[0]) + (C[0] - B[0]) * (A[1] - C[1])
        if abs(den) < 1e-9:
            continue
        w0 = ((B[1] - C[1]) * (xx - C[0]) + (C[0] - B[0]) * (yy - C[1])) / den
        w1 = ((C[1] - A[1]) * (xx - C[0]) + (A[0] - C[0]) * (yy - C[1])) / den
        w2 = 1 - w0 - w1
        m = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not m.any():
            continue
        att = w0[m, None] * verts[a, 2:] + w1[m, None] * verts[b, 2:] + w2[m, None] * verts[c, 2:]
        u, v, ih, along = att.T
        head = ih > 0.5
        tu = np.where(head, HEAD_X + (1 - HEAD_X) * np.clip(u, 0, 0.998), 0.1 + 0.45 * (u % 1))
        tv = np.where(head, np.clip(v, 0, 1), (np.clip(v, 0, 1) - 0.5) * BODY_UV + 0.5)
        # The image's row 0 is its top: v = 1
        ty = np.clip(((1 - tv) * th).astype(int), 0, th - 1)
        tx = np.clip((tu * tw).astype(int), 0, tw - 1)
        mk, pt = mask[ty, tx], pattern[ty, tx]
        x = np.clip(along, 0, 1)
        fade = np.where(head, 1, x * x * (3 - 2 * x))       # smoothstep: a soft start
        sub = cov[y0:y1, x0:x1]
        sub[m] = np.maximum(sub[m], np.clip(mk[:, :3].sum(-1), 0, 1) * mk[:, 3] * fade)
        rs = rgb[y0:y1, x0:x1]
        rs[m] = pt[:, :3]
    return cov, rgb


def draw_arrow(img, root, ctrl, tip):
    verts, tris = arrow_mesh(root, ctrl, tip)
    cov, pat = raster(img.shape[:2], verts, tris)
    sh = nd_shift(cov, (SHADOW_PX[1] * S, SHADOW_PX[0] * S), order=1)
    sh = gaussian_filter(sh, S * 0.45)
    img = img * (1 - SHADOW * sh[..., None])
    c = np.array(ARROW)
    rim = np.array([0.16, 0.18, 0.24])                    # the pattern's dark rim: slate, not black
    col = np.clip(rim + (c * 1.05 - rim) * pat, 0, 1)
    k = (cov * OPACITY)[..., None]
    return img * (1 - k) + col * k


def objective_line(ids, arcs, root):
    """The target province's outline on the side facing away from the arrow, relaxed."""
    c = centroid(ids, TARGET_PROVINCE)
    own = [(pts, b if a == TARGET_PROVINCE else a, TARGET_PROVINCE) for pts, a, b, closed in arcs
           if TARGET_PROVINCE in (a, b) and len(pts) >= 2]
    loops = chain(own)
    pts = max((p for p, _, _ in loops), key=len)
    to_root = (root - c) / np.hypot(*(root - c))
    rel = pts - c
    keep = (rel @ to_root) / (np.hypot(rel[:, 0], rel[:, 1]) + 1e-9) < 0.3
    # The longest run of kept points, wrapping around the closed outline
    k2 = np.concatenate([keep, keep])
    best, run, start, best_start = 0, 0, 0, 0
    for i, ok in enumerate(k2):
        if ok:
            if run == 0:
                start = i
            run += 1
            if run > best and run <= len(keep):
                best, best_start = run, start
        else:
            run = 0
    idx = [(best_start + j) % len(pts) for j in range(best)]
    line = relax(c + (pts[idx] - c) * OBJ_SCALE, 0.25, 60)
    line = line[3:-3]                     # the relaxed ends hook; trim them
    # Teeth (the texture's top rows) point inward: the -normal side of draw_line faces the target
    mid = line[len(line) // 2]
    tng = line[len(line) // 2 + 1] - line[len(line) // 2 - 1]
    nrm = np.array([tng[1], -tng[0]])
    if (mid - c) @ nrm < 0:              # teeth inward, towards the target
        line = line[::-1]
    return line


def neighbours(ids):
    """Province adjacency from the id map (4-neighbourhood)."""
    adj = {}
    for a, b in ((ids[:, :-1], ids[:, 1:]), (ids[:-1, :], ids[1:, :])):
        m = a != b
        for p, q in set(zip(a[m].tolist(), b[m].tolist())):
            adj.setdefault(p, set()).add(q)
            adj.setdefault(q, set()).add(p)
    return adj


def offensive_path(ids, owner, near=None):
    """HOI4 keeps an offensive line as province points: from the front to the target (the last). Breadth-first
    from the target through enemy land; the start is an own province on the front with the fewest steps (ties:
    the one nearest the target, or `near`); each step goes to the neighbour one step closer that keeps the path
    straightest (nearest the line to the target). Integer maths on province centres, so the C# can match it."""
    adj = neighbours(ids)
    cen = {p: np.round(centroid(ids, p)).astype(int) for p in adj}
    dist = {TARGET_PROVINCE: 0}
    queue = [TARGET_PROVINCE]
    for p in queue:
        for q in sorted(adj[p]):
            if q not in dist and owner.get(q) == TARGET:
                dist[q] = dist[p] + 1
                queue.append(q)
    starts = []
    for p in sorted(adj):
        if owner.get(p) != PLAYER:
            continue
        d = [dist[q] for q in adj[p] if q in dist]
        if d:
            ref = cen[TARGET_PROVINCE] if near is None else np.round(near).astype(int)
            starts.append((min(d) + 1, int(((cen[p] - ref) ** 2).sum()), p))
    _, _, start = min(starts)
    path = [start]
    goal = cen[TARGET_PROVINCE]
    while path[-1] != TARGET_PROVINCE:
        p = path[-1]
        here = dist.get(p, min(dist[q] for q in adj[p] if q in dist) + 1)
        cands = [q for q in sorted(adj[p]) if dist.get(q) == here - 1]
        # Straightest: the smallest distance from the centre to the line start -> target (cross product)
        a = cen[start]
        def off(q):
            v, w = goal - a, cen[q] - a
            return abs(int(v[0]) * int(w[1]) - int(v[1]) * int(w[0]))
        path.append(min(cands, key=lambda q: (off(q), q)))
    return path


def arrow_controls(ids, front, path):
    """HOI4's arrow bends once. The root: where the path crosses the front line (the line's point nearest the
    middle of the path's first two provinces), pulled back into own land; it leaves along the line's normal
    there (towards the enemy) and curves to the target's centre. Returns root, control point, tip."""
    cen = [centroid(ids, p) for p in path]
    pts = np.vstack(front)
    mid = (cen[0] + cen[1]) * 0.5
    i = np.argmin(np.hypot(*(pts - mid).T))
    near = pts[i]
    tng = pts[min(i + 12, len(pts) - 1)] - pts[max(i - 12, 0)]
    nrm = np.array([tng[1], -tng[0]]) / np.hypot(*tng)
    if nrm @ (cen[1] - near) < 0:
        nrm = -nrm
    root = near - nrm * ROOT_BACK
    tip = cen[-1]
    return root, root + nrm * np.hypot(*(tip - root)) * BEND, tip


def main():
    ids, owner, arcs = load(CROP)
    front = [relax(c, 0.25, 14) for c in orient(ids, chain(border_arcs(owner, arcs)))]
    enemy = {p for p in np.unique(ids) if owner.get(p) == TARGET}

    c = centroid(ids, TARGET_PROVINCE)
    path = offensive_path(ids, owner, ROOT_NEAR)
    print(f"target {TARGET_PROVINCE}, path {path}")
    root, ctrl, tip = arrow_controls(ids, front, path)

    bg = background(ids, owner)
    defence = (tex("defensive_line_mask"), tex("defensive_line"), 3.2)

    pre = enemy_stripes(bg.copy(), ids, enemy)
    pre = draw_line(pre, front, WIDTH, *defence, ARMY)
    save(pre, "offensive_preview.png")

    placed = draw_arrow(bg.copy(), root, ctrl, tip)
    placed = draw_line(placed, front, WIDTH, *defence, ARMY)
    obj = objective_line(ids, arcs, ctrl)   # open towards where the arrow comes in
    placed = draw_line(placed, [obj], OBJ_WIDTH, tex("offensive_line_mask"), tex("offensive_line"), OBJ_TILE, ARMY)
    save(placed, f"offensive_placed_{TARGET_PROVINCE}.png")


if __name__ == "__main__":
    main()
