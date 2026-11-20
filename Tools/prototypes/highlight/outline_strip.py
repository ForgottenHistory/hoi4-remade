"""
Prototype of the ENGINE highlight outline strip (MeshGeometryBorderRenderer + BorderMeshGenerator),
to find the spokes at outline joints.

Faithful port of: BorderGraph.Trace + Smooth (midpoints, RDP 0.75, Chaikin x3, RDP 0.15),
MeshGeometryBorderRenderer.ChainArcs, BorderMeshGenerator.Subdivide + Perpendicular.
Renders the strip quads (2 triangles per segment) over the province crop, magnified.

Usage: python outline_strip.py <x> <y> [half_width_px] [crop] [scale] [out.png]
  x, y: a map pixel inside the province to outline (image coords, y down)
"""
import os, sys, csv
import numpy as np
from PIL import Image, ImageDraw

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
MAP = os.path.join(ROOT, "Hoi4Data", "map")

LOOKAHEAD = float(os.environ.get("LOOKAHEAD", "1.5"))
MAX_SEG = 3.0


def load_ids(x0, y0, x1, y1):
    img = np.asarray(Image.open(os.path.join(MAP, "provinces.png")).convert("RGB")).astype(np.int64)
    key = (img[..., 0] << 16) | (img[..., 1] << 8) | img[..., 2]
    c2id = {}
    with open(os.path.join(MAP, "definition.csv"), newline="") as f:
        for row in csv.reader(f, delimiter=";"):
            try:
                c2id[(int(row[1]) << 16) | (int(row[2]) << 8) | int(row[3])] = int(row[0])
            except (ValueError, IndexError):
                pass
    crop = key[y0:y1, x0:x1]
    ids = np.vectorize(lambda k: c2id.get(int(k), 0))(crop).astype(np.int32)
    return ids


# ---------------------------------------------------------------- BorderGraph.Trace
def trace(ids):
    H, W = ids.shape
    RIGHT, DOWN, RV, DV = 1, 2, 4, 8
    CW = W + 1
    flags = np.zeros(CW * (H + 1), dtype=np.int32)
    for cy in range(1, H):
        for cx in range(W):
            if ids[cy - 1, cx] != ids[cy, cx]:
                flags[cy * CW + cx] |= RIGHT
    for cy in range(H):
        for cx in range(1, W):
            if ids[cy, cx - 1] != ids[cy, cx]:
                flags[cy * CW + cx] |= DOWN

    def degree(cx, cy):
        c = cy * CW + cx
        d = 0
        if flags[c] & RIGHT: d += 1
        if flags[c] & DOWN: d += 1
        if cx > 0 and flags[c - 1] & RIGHT: d += 1
        if cy > 0 and flags[c - CW] & DOWN: d += 1
        return d

    def is_node(cx, cy):
        d = degree(cx, cy)
        return d >= 3 or d == 1

    def has_unvisited(cx, cy, d):
        if d == 0:
            c = cy * CW + cx; return (flags[c] & (RIGHT | RV)) == RIGHT
        if d == 1:
            c = cy * CW + cx; return (flags[c] & (DOWN | DV)) == DOWN
        if d == 2:
            if cx == 0: return False
            c = cy * CW + cx - 1; return (flags[c] & (RIGHT | RV)) == RIGHT
        if cy == 0: return False
        c = (cy - 1) * CW + cx; return (flags[c] & (DOWN | DV)) == DOWN

    def mark(cx, cy, d):
        if d == 0:
            flags[cy * CW + cx] |= RV; return ids[cy - 1, cx], ids[cy, cx]
        if d == 1:
            flags[cy * CW + cx] |= DV; return ids[cy, cx - 1], ids[cy, cx]
        if d == 2:
            flags[cy * CW + cx - 1] |= RV; return ids[cy - 1, cx - 1], ids[cy, cx - 1]
        flags[(cy - 1) * CW + cx] |= DV; return ids[cy - 1, cx - 1], ids[cy - 1, cx]

    DX, DY = [1, 0, -1, 0], [0, 1, 0, -1]
    arcs = []

    def walk(cx, cy, d, closed):
        sx, sy = cx, cy
        p, q = mark(cx, cy, d)
        pts = [(cx, cy)]
        cx += DX[d]; cy += DY[d]
        while True:
            pts.append((cx, cy))
            if (cx == sx and cy == sy) if closed else is_node(cx, cy):
                break
            nxt = -1
            for dd in range(4):
                if has_unvisited(cx, cy, dd):
                    nxt = dd; break
            if nxt < 0:
                break
            mark(cx, cy, nxt)
            cx += DX[nxt]; cy += DY[nxt]
        return (min(p, q), max(p, q), closed, pts)

    for cy in range(H + 1):
        for cx in range(W + 1):
            c = cy * CW + cx
            any_edge = (c < len(flags) and flags[c] & (RIGHT | DOWN)) or \
                       (cx > 0 and flags[c - 1] & RIGHT) or (cy > 0 and flags[c - CW] & DOWN)
            if not any_edge or not is_node(cx, cy):
                continue
            for d in range(4):
                if has_unvisited(cx, cy, d):
                    arcs.append(walk(cx, cy, d, False))
    for cy in range(H + 1):
        for cx in range(W + 1):
            if has_unvisited(cx, cy, 0): arcs.append(walk(cx, cy, 0, True))
            if has_unvisited(cx, cy, 1): arcs.append(walk(cx, cy, 1, True))
    return arcs


# ---------------------------------------------------------------- BorderGraph.Smooth
def simplify(p, eps):
    n = len(p)
    if n < 3: return p
    keep = [False] * n; keep[0] = keep[-1] = True
    stack = [(0, n - 1)]
    while stack:
        i, j = stack.pop()
        if j <= i + 1: continue
        a = np.array(p[i]); d = np.array(p[j]) - a
        ln = np.hypot(*d)
        best, bk = -1, -1
        for k in range(i + 1, j):
            v = np.array(p[k]) - a
            dist = abs(v[0] * d[1] - v[1] * d[0]) / ln if ln > 1e-6 else np.hypot(*v)
            if dist > best: best, bk = dist, k
        if best > eps:
            keep[bk] = True; stack += [(i, bk), (bk, j)]
    return [p[k] for k in range(n) if keep[k]]


def simplify_closed(p, eps):
    q = simplify(p + [p[0]], eps)[:-1]
    return q if len(q) >= 3 else p


def chaikin_open(p):
    r = [p[0]]
    for i in range(len(p) - 1):
        a, b = np.array(p[i]), np.array(p[i + 1])
        if i > 0: r.append(tuple(a * .75 + b * .25))
        if i < len(p) - 2: r.append(tuple(a * .25 + b * .75))
    r.append(p[-1])
    return r


def chaikin_closed(p):
    r = []
    for i in range(len(p)):
        a, b = np.array(p[i]), np.array(p[(i + 1) % len(p)])
        r += [tuple(a * .75 + b * .25), tuple(a * .25 + b * .75)]
    return r


def smooth(corners, closed):
    pts = [] if closed else [corners[0]]
    for i in range(len(corners) - 1):
        pts.append(((corners[i][0] + corners[i + 1][0]) / 2, (corners[i][1] + corners[i + 1][1]) / 2))
    if not closed: pts.append(corners[-1])
    pts = simplify_closed(pts, 0.75) if closed else simplify(pts, 0.75)
    for _ in range(3):
        if len(pts) < 3: break
        pts = chaikin_closed(pts) if closed else chaikin_open(pts)
    if closed: pts.append(pts[0])
    return simplify(pts, 0.15)


# ---------------------------------------------------------------- ChainArcs
def end_key(p):
    return (int(np.floor(p[0] + 0.25)), int(np.floor(p[1] + 0.25)))


def chain(arcs):
    by_end = {}
    for i, (closed, pts) in enumerate(arcs):
        if closed or len(pts) < 2: continue
        by_end.setdefault(end_key(pts[0]), []).append(i)
        by_end.setdefault(end_key(pts[-1]), []).append(i)
    used = [False] * len(arcs)
    out = []
    for s in range(len(arcs)):
        if used[s] or len(arcs[s][1]) < 2: continue
        used[s] = True
        if arcs[s][0]: out.append((True, list(arcs[s][1]))); continue
        pts = list(arcs[s][1]); start = end_key(pts[0])
        while True:
            ek = end_key(pts[-1])
            if ek == start and len(pts) > 2: break
            nxt = next((c for c in by_end.get(ek, []) if not used[c]), -1)
            if nxt < 0: break
            used[nxt] = True
            np_ = arcs[nxt][1]
            pts += np_[1:] if end_key(np_[0]) == ek else list(reversed(np_[:-1]))
        closed = len(pts) > 3 and end_key(pts[-1]) == start
        if closed: pts[-1] = pts[0]
        out.append((closed, pts))
    return out


# ---------------------------------------------------------------- BorderMeshGenerator
def subdivide(poly):
    r = [poly[0]]
    for i in range(1, len(poly)):
        a, b = np.array(poly[i - 1]), np.array(poly[i])
        d = np.hypot(*(b - a))
        if d > MAX_SEG:
            steps = int(np.ceil(d / MAX_SEG))
            for s in range(1, steps):
                r.append(tuple(a + (b - a) * s / steps))
        r.append(poly[i])
    return r


def distinct_dir(p, i, step, closed):
    count = len(p) - 1 if closed else len(p)
    if count < 2: return np.zeros(2)
    if closed and i == len(p) - 1: i = 0
    fb, fbl = np.zeros(2), 1e-4
    for k in range(1, count):
        j = i + step * k
        if closed: j %= count
        elif j < 0 or j >= count: break
        d = (np.array(p[j]) - np.array(p[i])) if step > 0 else (np.array(p[i]) - np.array(p[j]))
        ln = np.hypot(*d)
        if ln >= LOOKAHEAD: return d / ln
        if ln > fbl: fb, fbl = d / ln, ln
    return fb


def perpendicular(p, i, closed):
    back, fwd = distinct_dir(p, i, -1, closed), distinct_dir(p, i, 1, closed)
    d = back + fwd
    if d @ d < 1e-6: d = fwd if fwd @ fwd > 0 else back
    d = d / np.hypot(*d) if d @ d > 1e-12 else np.array([1.0, 0.0])
    return np.array([-d[1], d[0]])


def main():
    px, py = int(sys.argv[1]), int(sys.argv[2])
    hw = float(sys.argv[3]) if len(sys.argv) > 3 else 3.0
    crop = int(sys.argv[4]) if len(sys.argv) > 4 else 70
    scale = int(sys.argv[5]) if len(sys.argv) > 5 else 10
    out = sys.argv[6] if len(sys.argv) > 6 else os.path.join(os.path.dirname(__file__), "outline_strip.png")

    x0, y0 = px - crop, py - crop
    ids = load_ids(x0, y0, px + crop, py + crop)
    target = ids[crop, crop]
    raw = trace(ids)
    arcs = [(c, smooth(pts, c)) for a, b, c, pts in raw if (a == target) != (b == target)]
    loops = chain(arcs)
    print(f"province {target}: {len(arcs)} arcs -> {len(loops)} loops, closed={[c for c, _ in loops]}")

    H, W = ids.shape
    rng = np.random.default_rng(1)
    pal = {i: tuple(int(c) for c in rng.integers(60, 140, 3)) for i in np.unique(ids)}
    pal[target] = (90, 90, 120)
    bg = Image.fromarray(np.array([[pal[i] for i in row] for row in ids], dtype=np.uint8))
    bg = bg.resize((W * scale, H * scale), Image.NEAREST).convert("RGBA")
    layer = Image.new("RGBA", bg.size, (0, 0, 0, 0))
    dr = ImageDraw.Draw(layer)
    S = lambda v: (v[0] * scale, v[1] * scale)

    mode = os.environ.get("MODE", "old")  # old: L/R per point; new: L/C/R (center vertex, v = 0.5)

    def iso(tri, vs, level=0.5):
        """Segment where interpolated v == level inside a triangle (what the shader draws as the line)."""
        pts = []
        for a, b in ((0, 1), (1, 2), (2, 0)):
            va, vb = vs[a], vs[b]
            if (va - level) * (vb - level) < 0:
                t = (level - va) / (vb - va)
                pts.append(tri[a] + (tri[b] - tri[a]) * t)
            elif va == level:
                pts.append(tri[a])
        return pts[:2] if len(pts) >= 2 else None

    spikes = []
    for closed, poly in loops:
        p = subdivide(poly)
        L, R = [], []
        for i in range(len(p)):
            n = perpendicular(p, i, closed)
            c = np.array(p[i])
            L.append(c - n * hw); R.append(c + n * hw)
        for i in range(len(p) - 1):
            C0, C1 = np.array(p[i], float), np.array(p[i + 1], float)
            if mode == "old":
                tris = [((L[i], R[i], L[i + 1]), (0, 1, 0)), ((R[i], R[i + 1], L[i + 1]), (1, 1, 0))]
            else:
                tris = [((L[i], C0, L[i + 1]), (0, .5, 0)), ((C0, C1, L[i + 1]), (.5, .5, 0)),
                        ((C0, R[i], C1), (.5, 1, .5)), ((R[i], R[i + 1], C1), (1, 1, .5))]
            for tri, vs in tris:
                dr.polygon([S(v) for v in tri], fill=(255, 220, 120, 50), outline=(255, 220, 120, 120))
                seg = iso(tri, vs)
                if seg is not None:
                    dr.line([S(seg[0]), S(seg[1])], fill=(0, 255, 0, 255), width=3)
            # quad twist check: left/right offsets swap order across the segment
            seg = np.array(p[i + 1]) - np.array(p[i])
            n0, n1 = (R[i] - L[i]), (R[i + 1] - L[i + 1])
            cosang = (n0 @ n1) / (np.hypot(*n0) * np.hypot(*n1))
            if cosang < 0.7:
                spikes.append((p[i], np.degrees(np.arccos(np.clip(cosang, -1, 1))), np.hypot(*seg)))
        dr.line([S(v) for v in p], fill=(255, 255, 255, 255), width=1)  # true center line
        for v in poly:
            dr.ellipse([S(v)[0] - 2, S(v)[1] - 2, S(v)[0] + 2, S(v)[1] + 2], fill=(255, 0, 0, 255))
    for (v, ang, seglen) in spikes:
        c = S(v); dr.ellipse([c[0] - 8, c[1] - 8, c[0] + 8, c[1] + 8], outline=(0, 255, 255, 255), width=2)
    print(f"normal jumps > 45deg between consecutive vertices: {len(spikes)}")
    for v, ang, seglen in spikes[:20]:
        print(f"  at ({v[0] + x0:.2f}, {v[1] + y0:.2f}) turn {ang:.0f}deg over {seglen:.3f}px")
    Image.alpha_composite(bg, layer).save(out)
    print("wrote", out)


if __name__ == "__main__":
    main()
