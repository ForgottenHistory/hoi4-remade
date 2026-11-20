"""
Rasterize the highlight glow the way the GPU does, to see where the spokes come from.

Builds the outline strip with outline_strip.py's pipeline (3 vertices per point: L/C/R) and
shades every triangle like BorderMesh.shader's soft profile: alpha = (2 * edge)^2 * opacity,
edge = 0.5 - |v - 0.5|, v interpolated across the triangle.

Panels:
  blend    - alpha blending (current engine): overlapping folded triangles accumulate
  max      - BlendOp Max: overlaps take the brighter value instead of adding up
  distance - alpha from the true distance to the strip's own segment (no v interpolation), Max blend

Usage: python glow_raster.py <x> <y> [half_width_px] [crop] [scale] [out.png]
"""
import os, sys
import numpy as np
from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import outline_strip as os_

OPACITY = 0.45


def strip_triangles(p, closed, hw):
    """Triangles as (verts[3], v[3], segment(a, b)) for the L/C/R strip."""
    L, C, R = [], [], []
    for i in range(len(p)):
        n = os_.perpendicular(p, i, closed)
        c = np.array(p[i], float)
        L.append(c - n * hw); C.append(c); R.append(c + n * hw)
    tris = []
    for i in range(len(p) - 1):
        seg = (C[i], C[i + 1])
        tris += [((L[i], C[i], L[i + 1]), (0, .5, 0), seg), ((C[i], C[i + 1], L[i + 1]), (.5, .5, 0), seg),
                 ((C[i], R[i], C[i + 1]), (.5, 1, .5), seg), ((R[i], R[i + 1], C[i + 1]), (1, 1, .5), seg)]
    return tris


def raster(tris, shape, scale, hw, mode):
    H, W = shape[0] * scale, shape[1] * scale
    acc = np.zeros((H, W))
    for verts, vs, (sa, sb) in tris:
        P = np.array(verts) * scale
        x0, y0 = np.floor(P.min(0)).astype(int); x1, y1 = np.ceil(P.max(0)).astype(int)
        x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, W - 1), min(y1, H - 1)
        if x1 < x0 or y1 < y0: continue
        ys, xs = np.mgrid[y0:y1 + 1, x0:x1 + 1] + 0.5
        (ax, ay), (bx, by), (cx, cy) = P
        det = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(det) < 1e-9: continue
        w0 = ((by - cy) * (xs - cx) + (cx - bx) * (ys - cy)) / det
        w1 = ((cy - ay) * (xs - cx) + (ax - cx) * (ys - cy)) / det
        w2 = 1 - w0 - w1
        inside = (w0 >= 0) & (w1 >= 0) & (w2 >= 0)
        if not inside.any(): continue
        if mode == "distance":
            # distance from pixel to this triangle's segment, in map px
            px, py = xs / scale, ys / scale
            a, b = np.array(sa), np.array(sb); d = b - a; dd = d @ d
            t = np.clip(((px - a[0]) * d[0] + (py - a[1]) * d[1]) / dd, 0, 1) if dd > 1e-12 else 0
            dist = np.hypot(px - (a[0] + t * d[0]), py - (a[1] + t * d[1]))
            soft = np.clip(1 - dist / hw, 0, 1)
        else:
            v = w0 * vs[0] + w1 * vs[1] + w2 * vs[2]
            soft = np.clip(2 * (0.5 - np.abs(v - 0.5)), 0, 1)
        a_ = np.where(inside, soft * soft * OPACITY, 0)
        sub = acc[y0:y1 + 1, x0:x1 + 1]
        if mode == "blend":
            acc[y0:y1 + 1, x0:x1 + 1] = sub * (1 - a_) + a_   # src-over of a white glow (coverage)
        else:
            acc[y0:y1 + 1, x0:x1 + 1] = np.maximum(sub, a_)
    return acc


def main():
    px, py = int(sys.argv[1]), int(sys.argv[2])
    hw = float(sys.argv[3]) if len(sys.argv) > 3 else 4.0
    crop = int(sys.argv[4]) if len(sys.argv) > 4 else 16
    scale = int(sys.argv[5]) if len(sys.argv) > 5 else 24
    out = sys.argv[6] if len(sys.argv) > 6 else os.path.join(os.path.dirname(__file__), "glow_raster.png")

    ids = os_.load_ids(px - crop, py - crop, px + crop, py + crop)
    target = ids[crop, crop]
    arcs = [(c, os_.smooth(pts, c)) for a, b, c, pts in os_.trace(ids) if (a == target) != (b == target)]
    loops = os_.chain(arcs)
    tris = []
    for closed, poly in loops:
        tris += strip_triangles(os_.subdivide(poly), closed, hw)
    print(f"province {target}: {len(loops)} loops, {len(tris)} triangles, half width {hw}px")

    base = np.where(ids == target, 70, 40).astype(float)
    base = np.kron(base, np.ones((scale, scale)))
    panels = []
    for mode in ("blend", "max", "distance"):
        acc = raster(tris, ids.shape, scale, hw, mode)
        img = base * (1 - acc) + 255 * acc
        panels.append(np.clip(img, 0, 255).astype(np.uint8))
    gap = np.full((panels[0].shape[0], 8), 0, np.uint8)
    Image.fromarray(np.hstack([panels[0], gap, panels[1], gap, panels[2]])).save(out)
    print("wrote", out, "(panels: blend | max | distance)")


if __name__ == "__main__":
    main()
