"""Prototype: HOI4's supply map mode, checked against an HOI4 screenshot (Pomerania around Koeslin, 1939).

Colours (common/defines/00_graphics.lua):
  - SUPPLY_MAP_MODE_REACH_COLOR: a gradient over a province's supply / SUPPLY_MAP_MODE_BEST_FLOW_DISPLAY (12):
    0 purple #990066, 0.02 purple-blue, 0.12 dark blue, 0.2 blue, 0.4 light blue, 0.6 teal, 0.8 light teal,
    1 light green. Supply here = its hub supply + its state's pool (what a division standing there draws on).
  - SUPPLY_MAP_MODE_STATUS_COLOR: where our divisions' average supply is under SUPPLY_STATUS_DISPLAY_THRESHOLD (0.9)
    the province shows their status instead: 0 red #E60000, 0.7 orange #FA661A, 1 mustard #CCA333.
  - Only the viewer's land is coloured; the rest is plain terrain.
The colours are smoothed across provinces like HOI4's heatmap (a blur of RADIUS map px, normalised so the colour
doesn't fade near uncoloured land), but coverage stays the province's own: the colour stops at the country's edge.

Rails: HOI4's railway_map_arrow (gfx/maparrows): mask = shape with its dark outline, pattern = white rails and ties.
Thin texture for level 1, medium for 2-3, thick for 4-5 (RAILWAY_MAP_ARROW_*_LEVEL_THRESHOLD), and the width grows
with the level too (the user: level 1 thinnest .. 5 thickest): WIDTHS map px.

  python Tools/prototypes/supply/supply_mapmode.py [--crop x0,y0,x1,y1] [--scale 16] [--radius 2.5]
      [--starve PROV:RATIO ...] [--country GER]
"""
import argparse
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter
from scipy.spatial import cKDTree

HERE = Path(__file__).parent
sys.path.insert(0, str(HERE))
import supply_flow  # noqa: E402
from render_rails import chaikin, densify, mean_pixel, rail_points, read_rails  # noqa: E402

REACH = [(0.0, (0.6, 0.0, 0.4)), (0.02, (0.2, 0.17, 0.52)), (0.12, (0.04, 0.17, 0.60)), (0.2, (0.13, 0.36, 0.65)),
         (0.4, (0.11, 0.56, 0.75)), (0.6, (0.25, 0.71, 0.76)), (0.8, (0.47, 0.8, 0.73)), (1.0, (0.6, 0.82, 0.6))]
STATUS = [(0.0, (0.9, 0.0, 0.0)), (0.7, (0.98, 0.4, 0.1)), (1.0, (0.8, 0.64, 0.2))]
BEST_FLOW = 12.0
STATUS_THRESHOLD = 0.9
WIDTHS = {1: 0.9, 2: 1.05, 3: 1.2, 4: 1.35, 5: 1.5}   # map px per rail level (screenshot: ~1 map px, outline included)
TEXTURE = {1: "thin", 2: "", 3: "", 4: "thick", 5: "thick"}
TILE = 0.8   # map px per texture repeat along the line (texture_loop 0.8)


def gradient(stops, t):
    t = min(1.0, max(0.0, t))
    for (a, ca), (b, cb) in zip(stops, stops[1:]):
        if t <= b:
            k = (t - a) / (b - a)
            return tuple(ca[i] + (cb[i] - ca[i]) * k for i in range(3))
    return stops[-1][1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=supply_flow.DEFAULT_HOI4)
    ap.add_argument("--data", default="Hoi4Data")
    ap.add_argument("--country", default="GER")
    ap.add_argument("--crop", default="3030,420,3115,490", help="x0,y0,x1,y1 map px, y down (default Koeslin)")
    ap.add_argument("--scale", type=int, default=14)
    ap.add_argument("--radius", type=float, default=5.0, help="heatmap smoothing, map px")
    ap.add_argument("--alpha", type=float, default=0.8, help="heatmap opacity over terrain")
    ap.add_argument("--starve", nargs="*", default=["3381:0.05", "243:0.45"],
                    help="PROV:RATIO, our divisions' average supply there (status colours below 0.9)")
    ap.add_argument("--out", default=str(HERE / "supply_mapmode_preview.png"))
    args = ap.parse_args()
    hoi4, data = Path(args.hoi4), Path(args.data)
    x0, y0, x1, y1 = (int(v) for v in args.crop.split(","))
    S = args.scale

    m = supply_flow.compute(hoi4, data)
    status = {int(a): float(b) for a, b in (s.split(":") for s in args.starve)}

    ids = supply_flow_ids = None
    from render_rails import province_ids
    ids = province_ids(hoi4)
    H, W = ids.shape

    # per-province premultiplied colour (alpha 0 = terrain)
    crop_ids = ids[y0:y1, x0:x1]
    uniq, inv = np.unique(crop_ids, return_inverse=True)
    pal = np.zeros((len(uniq), 4))
    for i, p in enumerate(uniq.tolist()):
        if m.prov_owner.get(p) != args.country or m.prov_state.get(p) in m.impassable:
            continue
        if p in status and status[p] < STATUS_THRESHOLD:
            c = gradient(STATUS, status[p])
        else:
            v = m.hub_supply.get(p, (0, None))[0] + m.state_supply.get(m.prov_state.get(p), 0)
            c = gradient(REACH, v / BEST_FLOW)
        pal[i] = (*c, 1.0)
    heat = pal[inv.reshape(crop_ids.shape)]
    # upscale (nearest, like the province id texture), then the disc average ~ a gaussian of sigma radius/2
    heat = np.kron(heat, np.ones((S, S, 1)))
    for k in range(4):
        heat[..., k] = gaussian_filter(heat[..., k], args.radius / 2 * S)
    rgb = heat[..., :3] / np.maximum(heat[..., 3:4], 1e-4)   # normalised: no fade towards uncoloured land
    cover = np.kron(pal[inv.reshape(crop_ids.shape)][..., 3], np.ones((S, S)))
    a = gaussian_filter(cover, 0.6 * S)[..., None]            # the province's own coverage, edge softened ~1 map px

    cmap = Image.open(data / "gfx" / "map" / "colormap.png").convert("RGB").resize((W, H), Image.BILINEAR)
    bg = np.asarray(cmap, dtype=np.float32)[y0:y1, x0:x1] / 255
    bg = np.asarray(Image.fromarray((bg * 255).astype(np.uint8)).resize(((x1 - x0) * S, (y1 - y0) * S), Image.BILINEAR),
                    dtype=np.float32) / 255
    shade = bg.mean(axis=2, keepdims=True) / max(1e-3, bg.mean())   # keep the terrain's light and dark under the colour
    out = bg * (1 - a * args.alpha) + rgb * np.clip(shade, 0.6, 1.3) * a * args.alpha
    # province borders, faint; country border dark
    big = np.kron(crop_ids, np.ones((S, S), dtype=ids.dtype))
    edge = np.zeros(big.shape, bool)
    edge[:, 1:] |= big[:, 1:] != big[:, :-1]
    edge[1:, :] |= big[1:, :] != big[:-1, :]
    out[edge] *= 0.7

    # rails
    pts = rail_points(hoi4, ids)
    gy, gx = np.mgrid[0:out.shape[0], 0:out.shape[1]]
    mx, my = x0 + (gx + 0.5) / S, y0 + (gy + 0.5) / S
    maps = hoi4 / "gfx" / "maparrows"
    tex = {}
    for name in ("thin", "", "thick"):
        suffix = f"_{name}" if name else ""
        mask = np.asarray(Image.open(maps / f"railway_mask{suffix}.dds").convert("RGBA"), np.float32) / 255
        pat = np.asarray(Image.open(maps / f"railway_pattern{suffix}.dds").convert("RGBA"), np.float32) / 255
        tex[name] = (mask, pat)
    for level, chain in sorted(read_rails(hoi4)):
        poly = [q for q in (pts.get(p) or mean_pixel(ids, p) for p in chain) if q is not None]
        if len(poly) < 2:
            continue
        poly = np.array(poly)
        if poly[:, 0].max() < x0 - 5 or poly[:, 0].min() > x1 + 5 or poly[:, 1].max() < y0 - 5 or poly[:, 1].min() > y1 + 5:
            continue
        width = WIDTHS[min(5, max(1, level))]
        mask, pat = tex[TEXTURE[min(5, max(1, level))]]
        curve = densify(chaikin(poly, 3), 0.05)
        seg = np.diff(curve, axis=0)
        arc = np.concatenate([[0], np.cumsum(np.linalg.norm(seg, axis=1))])
        tang = np.vstack([seg, seg[-1:]])
        tang /= np.maximum(np.linalg.norm(tang, axis=1, keepdims=True), 1e-9)
        half = width / 2
        bx0, by0 = curve.min(axis=0) - half - 1
        bx1, by1 = curve.max(axis=0) + half + 1
        sel = (mx >= bx0) & (mx <= bx1) & (my >= by0) & (my <= by1)
        if not sel.any():
            continue
        qx, qy = mx[sel], my[sel]
        dist, idx = cKDTree(curve).query(np.column_stack([qx, qy]))
        near = dist <= half * 1.2
        if not near.any():
            continue
        idx = idx[near]
        dx, dy = qx[near] - curve[idx, 0], qy[near] - curve[idx, 1]
        along = dx * tang[idx, 0] + dy * tang[idx, 1]
        across = -dx * tang[idx, 1] + dy * tang[idx, 0]
        u = (arc[idx] + along) / TILE
        v = across / width + 0.5
        ok = (v >= 0) & (v < 1)
        tu = ((u[ok] % 1.0) * 128).astype(int) % 128
        tv = np.clip(v[ok] * 128, 0, 127.999).astype(int)
        alpha = mask[tv, tu, 3:4] * mask[tv, tu, 1:2]   # mask_outer uses green
        colour = pat[tv, tu, :3]
        rows, cols = gy[sel][near][ok], gx[sel][near][ok]
        out[rows, cols] = out[rows, cols] * (1 - alpha) + colour * alpha

    Image.fromarray((np.clip(out, 0, 1) * 255).astype(np.uint8)).save(args.out)
    print("wrote", args.out, out.shape[1], "x", out.shape[0])
    for p in (11372, 3394, 11320):
        v = m.hub_supply.get(p, (0, None))[0] + m.state_supply.get(m.prov_state.get(p), 0)
        print(f"  province {p}: supply {v:.1f} -> {v / BEST_FLOW:.2f} of the gradient")


if __name__ == "__main__":
    main()
