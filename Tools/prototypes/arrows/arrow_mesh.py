"""Prototype: the exact Hoi4MoveArrows.cs mesh pipeline (centripetal Catmull-Rom, side directions
over a body-width window, body + head pairs on the curve, head kept on the last leg) rasterised
with the Hoi4/MoveArrow shader's texture lookup. Renders several routes, plus 4x zooms of each
head joint, to arrow_mesh.png. Units: map pixels (worldPerPx = 1)."""
from pathlib import Path
import json
import numpy as np
from PIL import Image

D = Path(__file__).resolve().parents[3] / "Hoi4Data" / "gfx" / "maparrows"
P = json.loads((D / "move_arrow.json5").read_text().split("\n", 1)[1])
MASK = np.asarray(Image.open(D / "move_mask.png").convert("RGBA")).astype(np.float32) / 255
PAT = np.asarray(Image.open(D / "move_pattern.png").convert("RGBA")).astype(np.float32) / 255
TH, TW = MASK.shape[:2]

EVEN_BODY_WIDTH = 9.0
FULL_SIZE_LENGTH, MIN_SCALE = 200.0, 0.5
BODY_UV = P["body_uv_scale"]
HEAD_LEN = P["head_length"]


# --- curve (Hoi4MoveArrows.CatmullRom) -------------------------------------------------------
def knot(a, b):
    return max(np.sqrt(np.linalg.norm(b - a)), 1e-4)


def blend(a, b, ta, tb, t):
    d = tb - ta
    return a * ((tb - t) / d) + b * ((t - ta) / d) if d > 1e-6 else a


def catmull(pts, step, end_phantom=None):
    pts = [np.array(p, float) for p in pts]
    n = len(pts)
    res, first_end, last_start = [], 0, 0
    for i in range(n - 1):
        p1, p2 = pts[i], pts[i + 1]
        p0 = pts[i - 1] if i > 0 else p1 + (p1 - p2)
        p3 = pts[i + 2] if i + 2 < n else (np.array(end_phantom, float) if end_phantom is not None else p2 + (p2 - p1))
        t0 = 0.0; t1 = t0 + knot(p0, p1); t2 = t1 + knot(p1, p2); t3 = t2 + knot(p2, p3)
        if i == n - 2:
            last_start = len(res)
        steps = int(np.clip(np.ceil(np.linalg.norm(p2 - p1) / step), 1, 256))
        for k in range(steps):
            t = t1 + (t2 - t1) * k / steps
            a1 = blend(p0, p1, t0, t1, t); a2 = blend(p1, p2, t1, t2, t); a3 = blend(p2, p3, t2, t3, t)
            b1 = blend(a1, a2, t0, t2, t); b2 = blend(a2, a3, t1, t3, t)
            res.append(blend(b1, b2, t1, t2, t))
        if i == 0:
            first_end = len(res)
    res.append(pts[-1])
    return res, first_end, last_start


def point_at(c, cum, s):
    for i in range(1, len(c)):
        if cum[i] >= s or i == len(c) - 1:
            seg = cum[i] - cum[i - 1]
            t = np.clip((s - cum[i - 1]) / seg, 0, 1) if seg > 1e-6 else 1.0
            d = c[i] - c[i - 1]
            dn = d / np.linalg.norm(d) if np.linalg.norm(d) > 1e-5 else np.array([0, 1.0])
            return c[i - 1] + (c[i] - c[i - 1]) * t, dn
    return c[-1], np.array([0, 1.0])


def direction_at(c, cum, s, window):
    L = cum[-1]
    a, _ = point_at(c, cum, max(s - window * 0.5, 0))
    b, fb = point_at(c, cum, min(s + window * 0.5, L))
    d = b - a
    return d / np.linalg.norm(d) if np.linalg.norm(d) > 1e-5 else fb


# --- mesh (Hoi4MoveArrows.AddArrow) ----------------------------------------------------------
def build(controls):
    verts, uvs, infos, tris = [], [], [], []

    def pair(p, d, half, u, is_head, along, connect):
        side = np.array([-d[1], d[0]]) * half
        v = len(verts)
        verts.extend([p - side, p + side]); uvs.extend([(u, 0), (u, 1)]); infos.extend([(is_head, along)] * 2)
        if connect:
            tris.extend([(v - 2, v, v - 1), (v - 1, v, v + 1)])

    curve, first_end, last_start = catmull(controls, 2.0)
    cum = np.concatenate([[0], np.cumsum([np.linalg.norm(curve[i] - curve[i - 1]) for i in range(1, len(curve))])])
    L = cum[-1]
    scale = np.clip(L / FULL_SIZE_LENGTH, MIN_SCALE, 1)
    w0 = w1 = EVEN_BODY_WIDTH * scale
    last_leg = L - cum[min(last_start, len(curve) - 1)]
    head = min(HEAD_LEN * scale, min(L * 0.6, last_leg * 0.85))
    head_w = w1 / BODY_UV
    body_len = L - head
    tile = w0 * 0.5 * 2 / BODY_UV
    window = max(w0, w1)
    # Straight head: along the chord of the curve's last `head` length, ending at the tip. The body
    # is re-curved to end at the head's start with its end tangent aimed at the tip.
    tip = curve[-1]
    chord_start, _ = point_at(curve, cum, body_len)
    hd = tip - chord_start
    hd = hd / np.linalg.norm(hd) if np.linalg.norm(hd) > 1e-6 else np.array([1.0, 0])
    hs_p = tip - hd * head
    body_controls = [np.array(c, float) for c in controls[:-1]] + [hs_p]
    if len(body_controls) >= 2 and np.linalg.norm(body_controls[-1] - body_controls[-2]) < 1e-3:
        body_controls.pop(-2)
    bcurve, _, _ = catmull(body_controls, 2.0, end_phantom=tip) if len(body_controls) >= 2 else ([hs_p], 0, 0)
    bcum = np.concatenate([[0], np.cumsum([np.linalg.norm(bcurve[i] - bcurve[i - 1]) for i in range(1, len(bcurve))])])
    blen = bcum[-1]
    for i in range(len(bcurve)):
        d = hd if i == len(bcurve) - 1 else direction_at(bcurve, bcum, bcum[i], window)
        pair(bcurve[i], d, w0 * 0.5, bcum[i] / tile, 0, bcum[i] / (blen + head), i > 0)
    for k in range(5):
        f = k / 4
        pair(hs_p + hd * head * f, hd, head_w * 0.5, f, 1, (blen + f * head) / (blen + head), k > 0)
    return np.array(verts), np.array(uvs), np.array(infos), tris, dict(head=head, head_w=head_w, body_w=w0, L=L)


# --- raster (Hoi4/MoveArrow frag) ------------------------------------------------------------
def sample(tex, u, v):
    # bilinear, clamp; v = 0 is the bottom row (Unity)
    x = np.clip(u * TW - 0.5, 0, TW - 1); y = np.clip((1 - v) * TH - 0.5, 0, TH - 1)
    x0 = np.floor(x).astype(int); y0 = np.floor(y).astype(int)
    x1 = np.minimum(x0 + 1, TW - 1); y1 = np.minimum(y0 + 1, TH - 1)
    fx = (x - x0)[..., None]; fy = (y - y0)[..., None]
    return (tex[y0, x0] * (1 - fx) * (1 - fy) + tex[y0, x1] * fx * (1 - fy) + tex[y1, x0] * (1 - fx) * fy + tex[y1, x1] * fx * fy)


def raster(img, verts, uvs, infos, tris, ox, oy, zoom, wire=False):
    H, W = img.shape[:2]
    color = np.array([0.78, 0.56, 0.16])
    for a, b, c in tris:
        P3 = (verts[[a, b, c]] - [ox, oy]) * zoom
        x0, y0 = np.floor(P3.min(0)).astype(int); x1, y1 = np.ceil(P3.max(0)).astype(int)
        x0, y0 = max(x0, 0), max(y0, 0); x1, y1 = min(x1, W - 1), min(y1, H - 1)
        if x1 < x0 or y1 < y0:
            continue
        yy, xx = np.mgrid[y0:y1 + 1, x0:x1 + 1] + 0.5
        (ax, ay), (bx, by), (cx, cy) = P3
        den = (by - cy) * (ax - cx) + (cx - bx) * (ay - cy)
        if abs(den) < 1e-9:
            continue
        l1 = ((by - cy) * (xx - cx) + (cx - bx) * (yy - cy)) / den
        l2 = ((cy - ay) * (xx - cx) + (ax - cx) * (yy - cy)) / den
        l3 = 1 - l1 - l2
        inside = (l1 >= 0) & (l2 >= 0) & (l3 >= 0)
        if not inside.any():
            continue
        uv = l1[..., None] * uvs[a] + l2[..., None] * uvs[b] + l3[..., None] * uvs[c]
        info = l1[..., None] * infos[a] + l2[..., None] * infos[b] + l3[..., None] * infos[c]
        is_head = info[..., 0] > 0.5
        across = np.clip(uv[..., 1], 0, 1)
        bu = np.clip(np.mod(uv[..., 0], 1) * 0.5, 0.001, 0.499)
        bv = (across - 0.5) * BODY_UV + 0.5
        tu = np.where(is_head, 0.5 + np.clip(uv[..., 0], 0, 0.998) * 0.5, bu)
        tv = np.where(is_head, across, bv)
        m = sample(MASK, tu, tv); pt = sample(PAT, tu, tv)
        cov = np.clip(m[..., 0] + m[..., 1] + m[..., 2], 0, 1) * m[..., 3] * inside
        rgb = np.clip(pt[..., :3] * color * 1.4, 0, 1)
        sub = img[y0:y1 + 1, x0:x1 + 1]
        sub[..., :3] = sub[..., :3] * (1 - cov[..., None]) + rgb * cov[..., None]
    if wire:
        from PIL import ImageDraw
        im = Image.fromarray((img * 255).astype(np.uint8)); d = ImageDraw.Draw(im)
        for a, b, c in tris:
            pts = [tuple((verts[i] - [ox, oy]) * zoom) for i in (a, b, c, a)]
            col = (255, 0, 255) if infos[a][0] > 0.5 else (0, 200, 255)
            d.line(pts, fill=col, width=1)
        img[:] = np.asarray(im).astype(np.float32) / 255


ROUTES = [
    [(20, 40), (55, 30), (90, 48), (120, 44), (150, 70), (175, 62)],     # wiggly, long
    [(20, 120), (60, 115), (95, 125), (100, 150)],                       # sharp turn at the end
    [(150, 120), (190, 110), (215, 135)],                                 # medium
    [(30, 180), (50, 176)],                                               # short single leg
]

S = 4  # overview px per map px
over = np.ones((220 * S, 260 * S, 3), np.float32) * [0.36, 0.43, 0.31]
zooms = []
for route in ROUTES:
    v, uv, info, tris, meta = build(route)
    raster(over, v, uv, info, tris, 0, 0, S)
    # zoom on the head joint
    hv = v[info[:, 0] > 0.5]
    c = hv.mean(0)
    Z = 16; half = 18
    z = np.ones((2 * half * Z, 2 * half * Z, 3), np.float32) * [0.36, 0.43, 0.31]
    raster(z, v, uv, info, tris, c[0] - half, c[1] - half, Z, wire=True)
    zooms.append(z)
    print({k: round(float(x), 2) for k, x in meta.items()})

zw = sum(z.shape[1] for z in zooms)
sheet = np.ones((over.shape[0] + zooms[0].shape[0], max(over.shape[1], zw), 3), np.float32) * 0.1
sheet[:over.shape[0], :over.shape[1]] = over
x = 0
for z in zooms:
    sheet[over.shape[0]:, x:x + z.shape[1]] = z; x += z.shape[1]
out = Path(__file__).with_name("arrow_mesh.png")
Image.fromarray((sheet * 255).astype(np.uint8)).save(out)
print("wrote", out)
