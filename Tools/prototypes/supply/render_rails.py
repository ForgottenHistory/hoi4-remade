"""Prototype: HOI4 railways drawn on the map, checked against an HOI4 screenshot (north-west Germany).

Path: each railway of map/railways.txt is a chain of provinces. A province's rail point is its supply_node spot
in map/buildings.txt (one per land province: "STATE;supply_node;X;HEIGHT;Z;ROT;SEA", map units, Z up from the
bottom); the mean pixel when a province has none. The chain's points are smoothed (Chaikin) into a curve.

Look: a ribbon textured with gfx/models/supply/railroad.dds: 64x320, five 64x64 tiles stacked, one per level
(1 rusty .. 5 shiny steel), rails along U. HOI4's railroad.shader wraps U along the length and V across, alpha
blended over the terrain. The ribbon is WIDTH map px wide and one tile is TILE map px long (0.4 each: an HOI4 screenshot at ~32 screen px per
map px shows a ~10 px wide track; Bremen to Hannover is ~22 map px there).

Render: a crop of the terrain colormap, province borders and rivers, upscaled SCALE times, with the rails over it.

  python Tools/prototypes/supply/render_rails.py [--hoi4 PATH] [--data Hoi4Data] [--width 0.4] [--tile 0.4]
      [--smooth 3] [--crop x0,y0,x1,y1] [--scale 28] [--supersample 2] [--out Tools/prototypes/supply/rails_preview.png]
"""
import argparse
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.spatial import cKDTree

DEFAULT_HOI4 = r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV"


def province_ids(hoi4: Path):
    defs = {}
    for line in (hoi4 / "map" / "definition.csv").read_text(encoding="latin-1").splitlines():
        c = line.split(";")
        if len(c) > 3 and c[0].isdigit():
            defs[(int(c[1]) << 16) | (int(c[2]) << 8) | int(c[3])] = int(c[0])
    img = np.asarray(Image.open(hoi4 / "map" / "provinces.bmp").convert("RGB")).astype(np.int32)
    key = (img[..., 0] << 16) | (img[..., 1] << 8) | img[..., 2]
    keys = np.array(sorted(defs))
    ids = np.array([defs[k] for k in keys])
    return ids[np.searchsorted(keys, key)]


def rail_points(hoi4: Path, ids):
    """Province -> (x, y) image coordinates (y down) of its supply_node spot, else its mean pixel."""
    h = ids.shape[0]
    pts = {}
    for line in (hoi4 / "map" / "buildings.txt").read_text(encoding="latin-1").splitlines():
        c = line.split(";")
        if len(c) < 5 or c[1] != "supply_node":
            continue
        x, z = float(c[2]), float(c[4])
        px, py = int(x), int(h - 1 - z)
        if 0 <= px < ids.shape[1] and 0 <= py < h:
            pts.setdefault(int(ids[py, px]), (x, h - z))
    return pts


def mean_pixel(ids, pid):
    ys, xs = np.nonzero(ids == pid)
    return (xs.mean() + 0.5, ys.mean() + 0.5) if len(xs) else None


def read_rails(hoi4: Path):
    lines = []
    for raw in (hoi4 / "map" / "railways.txt").read_text().split("\n"):
        v = [int(x) for x in raw.split()]
        if len(v) >= 4:
            lines.append((v[0], v[2:2 + v[1]]))
    return lines


def chaikin(p, it):
    p = np.asarray(p, dtype=np.float64)
    for _ in range(it):
        if len(p) < 3:
            break
        q = p[:-1] * 0.75 + p[1:] * 0.25
        r = p[:-1] * 0.25 + p[1:] * 0.75
        mid = np.empty((2 * (len(p) - 1), 2))
        mid[0::2], mid[1::2] = q, r
        p = np.vstack([p[:1], mid, p[-1:]])
    return p


def densify(p, step):
    out = [p[0]]
    for a, b in zip(p[:-1], p[1:]):
        n = max(1, int(np.ceil(np.linalg.norm(b - a) / step)))
        for k in range(1, n + 1):
            out.append(a + (b - a) * k / n)
    return np.array(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=DEFAULT_HOI4)
    ap.add_argument("--data", default="Hoi4Data")
    ap.add_argument("--width", type=float, default=0.4, help="ribbon width, map px")
    ap.add_argument("--tile", type=float, default=0.4, help="one texture tile's length, map px")
    ap.add_argument("--smooth", type=int, default=3, help="Chaikin iterations")
    ap.add_argument("--crop", default="2905,442,2972,492", help="x0,y0,x1,y1 in map px (y down)")
    ap.add_argument("--scale", type=int, default=28)
    ap.add_argument("--supersample", type=int, default=2)
    ap.add_argument("--out", default="Tools/prototypes/supply/rails_preview.png")
    args = ap.parse_args()
    hoi4, data = Path(args.hoi4), Path(args.data)
    x0, y0, x1, y1 = (int(v) for v in args.crop.split(","))
    S = args.scale * args.supersample

    ids = province_ids(hoi4)
    H, W = ids.shape
    pts = rail_points(hoi4, ids)
    print(f"supply_node spots: {len(pts)} provinces")

    # background: colormap (half resolution) + province borders + rivers, upscaled
    cmap = Image.open(data / "gfx" / "map" / "colormap.png").convert("RGB").resize((W, H), Image.BILINEAR)
    bg = np.asarray(cmap, dtype=np.float32)[y0:y1, x0:x1] * 0.85
    bg = np.asarray(Image.fromarray(bg.astype(np.uint8)).resize(((x1 - x0) * S, (y1 - y0) * S), Image.BILINEAR),
                    dtype=np.float32)
    big = np.kron(ids[y0:y1, x0:x1], np.ones((S, S), dtype=ids.dtype))
    edge = np.zeros(big.shape, bool)
    edge[:, 1:] |= big[:, 1:] != big[:, :-1]
    edge[1:, :] |= big[1:, :] != big[:-1, :]
    bg[edge] = bg[edge] * 0.25
    riv = np.asarray(Image.open(hoi4 / "map" / "rivers.bmp"))[y0:y1, x0:x1]
    riv_big = np.kron(riv < 200, np.ones((S, S), bool)) & ~edge
    bg[riv_big] = bg[riv_big] * 0.3 + np.array([70, 100, 150]) * 0.7

    tex = np.asarray(Image.open(hoi4 / "gfx" / "models" / "supply" / "railroad.dds").convert("RGBA"),
                     dtype=np.float32) / 255.0  # 320 x 64

    out = bg.copy()
    gy, gx = np.mgrid[0:out.shape[0], 0:out.shape[1]]
    # pixel centres in map px
    mx = x0 + (gx + 0.5) / S
    my = y0 + (gy + 0.5) / S
    half = args.width / 2
    drawn = 0
    for level, chain in read_rails(hoi4):
        poly = []
        for p in chain:
            q = pts.get(p) or mean_pixel(ids, p)
            if q is not None:
                poly.append(q)
        if len(poly) < 2:
            continue
        poly = np.array(poly)
        if (poly[:, 0].max() < x0 - 5 or poly[:, 0].min() > x1 + 5 or poly[:, 1].max() < y0 - 5
                or poly[:, 1].min() > y1 + 5):
            continue
        curve = densify(chaikin(poly, args.smooth), 0.05)
        seg = np.diff(curve, axis=0)
        seglen = np.linalg.norm(seg, axis=1)
        arc = np.concatenate([[0], np.cumsum(seglen)])
        tang = np.vstack([seg, seg[-1:]])
        tang /= np.maximum(np.linalg.norm(tang, axis=1, keepdims=True), 1e-9)
        # pixels near the curve
        bx0, by0 = curve.min(axis=0) - half - 1
        bx1, by1 = curve.max(axis=0) + half + 1
        m = (mx >= bx0) & (mx <= bx1) & (my >= by0) & (my <= by1)
        if not m.any():
            continue
        qx, qy = mx[m], my[m]
        tree = cKDTree(curve)
        dist, idx = tree.query(np.column_stack([qx, qy]))
        near = dist <= half * 1.2
        if not near.any():
            continue
        idx = idx[near]
        dx, dy = qx[near] - curve[idx, 0], qy[near] - curve[idx, 1]
        along = dx * tang[idx, 0] + dy * tang[idx, 1]
        across = -dx * tang[idx, 1] + dy * tang[idx, 0]
        u = (arc[idx] + along) / args.tile
        v = across / args.width + 0.5
        ok = (v >= 0) & (v < 1)
        tile = min(max(level, 1), 5) - 1
        tu = ((u[ok] % 1.0) * 64).astype(int) % 64
        tv = (tile * 64 + np.clip(v[ok] * 64, 0, 63.999)).astype(int)
        rgba = tex[tv, tu]
        rows, cols = gy[m][near][ok], gx[m][near][ok]
        a = rgba[:, 3:4]
        out[rows, cols] = out[rows, cols] * (1 - a) + rgba[:, :3] * 255 * a
        drawn += 1

    # hub markers (supply_nodes.txt) as small dots
    for raw in (hoi4 / "map" / "supply_nodes.txt").read_text().split("\n"):
        v = raw.split()
        if len(v) != 2:
            continue
        q = pts.get(int(v[1]))
        if q and x0 <= q[0] < x1 and y0 <= q[1] < y1:
            cx, cy = int((q[0] - x0) * S), int((q[1] - y0) * S)
            r = 4 * args.supersample
            out[max(0, cy - r):cy + r, max(0, cx - r):cx + r] = (230, 200, 60)

    img = Image.fromarray(np.clip(out, 0, 255).astype(np.uint8))
    if args.supersample > 1:  # rendered at scale * supersample: average down (the GPU filters the texture)
        img = img.resize((img.width // args.supersample, img.height // args.supersample), Image.BOX)
    img.save(args.out)
    print(f"rails drawn: {drawn}; wrote {args.out} ({out.shape[1]}x{out.shape[0]})")


if __name__ == "__main__":
    main()
