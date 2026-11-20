"""Prototype: HOI4 move arrow (maparrows.txt move_thick) along a curved path, using the converted
Hoi4Data/gfx/maparrows mask and pattern. Checks the texture layout (left half body tiling along
the arrow at the middle body_uv_scale of the height, right half head), the taper and the head."""
import json
from pathlib import Path
import numpy as np
from PIL import Image

D = Path(__file__).resolve().parents[3] / "Hoi4Data" / "gfx" / "maparrows"
P = json.loads((D / "move_arrow.json5").read_text().split("\n", 1)[1])
mask = np.asarray(Image.open(D / "move_mask.png").convert("RGBA")).astype(np.float32) / 255
pat = np.asarray(Image.open(D / "move_pattern.png").convert("RGBA")).astype(np.float32) / 255
TH, TW = mask.shape[:2]


def catmull(points, step=1.0):
    pts = [points[0]] + points + [points[-1]]
    out = []
    for i in range(1, len(pts) - 2):
        p0, p1, p2, p3 = map(np.array, pts[i - 1:i + 3])
        n = max(2, int(np.linalg.norm(p2 - p1) / step))
        for t in np.linspace(0, 1, n, endpoint=False):
            out.append(0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3))
    out.append(np.array(points[-1], float))
    return np.array(out)


def sample(tex, u, v):
    x = np.clip((u * TW).astype(int), 0, TW - 1)
    y = np.clip((v * TH).astype(int), 0, TH - 1)
    return tex[y, x]


def render(points, size, scale_px=3.0, color=(0.32, 0.72, 0.2)):
    curve = catmull(points) * scale_px
    seg = np.diff(curve, axis=0)
    seglen = np.linalg.norm(seg, axis=1)
    cum = np.concatenate([[0], np.cumsum(seglen)])
    L = cum[-1]
    s = np.clip(L / scale_px / 200.0, 0.5, 1.0)  # short arrows smaller (global_scale)
    w0, w1 = P["body_width"][0] * s * scale_px, P["body_width"][1] * s * scale_px
    head = P["head_length"] * s * scale_px
    head_w = w1 / P["body_uv_scale"]
    H, W = size
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    pix = np.stack([xx, yy], -1).reshape(-1, 2)
    # nearest point on the polyline
    best_d = np.full(len(pix), 1e9); along = np.zeros(len(pix)); side = np.zeros(len(pix))
    for i in range(len(seg)):
        a, d = curve[i], seg[i]
        t = np.clip(((pix - a) @ d) / max(d @ d, 1e-9), 0, 1)
        q = a + t[:, None] * d
        dist = np.linalg.norm(pix - q, axis=1)
        better = dist < best_d
        best_d[better] = dist[better]; along[better] = cum[i] + t[better] * seglen[i]
        n = np.array([-d[1], d[0]]) / max(seglen[i], 1e-9)
        side[better] = ((pix[better] - q[better]) @ n)
    is_head = along >= L - head
    width = np.where(is_head, head_w * (1 - 0) , w0 + (w1 - w0) * np.clip(along / max(L - head, 1e-6), 0, 1))
    v = side / width + 0.5
    inside = (v >= 0) & (v <= 1) & (best_d <= width)
    tile = w0 * (TW / 2) / (TH * P["body_uv_scale"])  # body tile length keeps the texture aspect
    u_body = np.mod(along / tile, 1.0) * 0.5
    v_body = (v - 0.5) * P["body_uv_scale"] + 0.5
    u_head = 0.5 + np.clip((along - (L - head)) / head, 0, 0.999) * 0.5
    u = np.where(is_head, u_head, u_body); vt = np.where(is_head, v, v_body)
    m = sample(mask, u, vt); pt = sample(pat, u, vt)
    alpha = np.clip(m[:, 0] + m[:, 1] + m[:, 2], 0, 1) * m[:, 3] * inside
    alpha *= np.where(is_head, 1.0, np.clip(along / (P["fade_in"] * L), 0, 1))
    rgb = pt[:, :3] * np.array(color) * 1.6
    img = np.zeros((len(pix), 4)); img[:, :3] = np.clip(rgb, 0, 1); img[:, 3] = alpha
    return Image.fromarray((img.reshape(H, W, 4) * 255).astype(np.uint8))


bg = Image.new("RGBA", (900, 420), (92, 110, 80, 255))
bg.alpha_composite(render([(20, 40), (60, 30), (100, 50), (140, 45), (180, 70), (230, 60)], (420, 900)))
bg.alpha_composite(render([(30, 110), (60, 120), (90, 112)], (420, 900)))
out = Path(__file__).with_name("arrow.png"); bg.save(out); print("wrote", out)
