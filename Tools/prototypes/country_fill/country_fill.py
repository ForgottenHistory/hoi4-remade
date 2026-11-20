"""Prototype: smooth country color edges (vs bilinear owner colors, which show pixel steps).

Renders a map crop, magnified, with three methods side by side:
  A  current: bilinear of the 4 neighbour owners' colors
  B  HOI4 gradient borders: unsigned border distance field (border pixels on both sides),
     9-tap blurred like gradient_border_multisample_alpha, fill fades out ~1 px before the border
  C  same-owner coverage: blur the "neighbour has my owner" indicator, cut at 0.5
     (neighbouring countries meet on one smooth line, no gap)

Usage: python country_fill.py [x0 y0 w h] (map px, top-down). Writes country_fill.png next to it.
"""
import re
import sys
import csv
from pathlib import Path

import numpy as np
from PIL import Image
from scipy import ndimage

ROOT = Path(__file__).resolve().parents[3]
DATA = ROOT / "Hoi4Data"
x0, y0, w, h = (int(v) for v in sys.argv[1:5]) if len(sys.argv) >= 5 else (2900, 420, 160, 110)
S = 8  # magnification

pm = np.asarray(Image.open(DATA / "map" / "provinces.png").convert("RGB"))[y0 - 3:y0 + h + 3, x0 - 3:x0 + w + 3].astype(np.int64)
key = (pm[..., 0] << 16) | (pm[..., 1] << 8) | pm[..., 2]
color_to_id = {}
for row in csv.reader(open(DATA / "map" / "definition.csv"), delimiter=";"):
    try:
        color_to_id[(int(row[1]) << 16) | (int(row[2]) << 8) | int(row[3])] = int(row[0])
    except (ValueError, IndexError):
        pass
owner_of = {}
for f in (DATA / "history" / "provinces").glob("*.json5"):
    m = re.search(r'owner:\s*"(\w+)"', f.read_text(encoding="utf-8"))
    if m:
        owner_of[int(f.name.split("-")[0])] = m.group(1)
tags = sorted(set(owner_of.values()))
tag_index = {t: i + 1 for i, t in enumerate(tags)}
palette = np.zeros((len(tags) + 1, 3))
for t, i in tag_index.items():
    m = re.search(r"color:\s*\[(\d+),\s*(\d+),\s*(\d+)\]", (DATA / "common" / "countries" / f"{t}.json5").read_text(encoding="utf-8"))
    palette[i] = [int(v) for v in m.groups()] if m else [128, 128, 128]
palette /= 255.0

ids = np.vectorize(lambda k: color_to_id.get(k, 0))(key)
owner = np.vectorize(lambda p: tag_index.get(owner_of.get(p, ""), 0))(ids)

H, W = owner.shape
TERRAIN = np.array([0.55, 0.52, 0.42])
SEA = np.array([0.15, 0.22, 0.32])


def sample_grid():
    ys = (np.arange(h * S) + 0.5) / S + 3 - 0.5
    xs = (np.arange(w * S) + 0.5) / S + 3 - 0.5
    return np.meshgrid(xs, ys)


def bilinear(a, X, Y):
    ix, iy = np.floor(X).astype(int), np.floor(Y).astype(int)
    fx, fy = X - ix, Y - iy
    if a.ndim == 3:
        fx, fy = fx[..., None], fy[..., None]
    return ((a[iy, ix] * (1 - fx) + a[iy, ix + 1] * fx) * (1 - fy)
            + (a[iy + 1, ix] * (1 - fx) + a[iy + 1, ix + 1] * fx) * fy)


X, Y = sample_grid()
nearest = owner[np.clip(np.round(Y).astype(int), 0, H - 1), np.clip(np.round(X).astype(int), 0, W - 1)]
base = np.where((nearest == 0)[..., None], SEA, TERRAIN)
BLEND = 0.7

# A: bilinear owner colors (premultiplied by owned)
col = palette[owner]
owned = (owner > 0).astype(float)
ca = bilinear(col * owned[..., None], X, Y)
aa = bilinear(owned, X, Y)
A = base * (1 - aa[..., None] * BLEND) + ca * BLEND

# B: distance field to border pixels (either side), 9-tap blur, fill from 0.75..1.75 px
border = np.zeros_like(owner, bool)
for dy, dx in ((0, 1), (0, -1), (1, 0), (-1, 0)):
    border |= owner != np.roll(np.roll(owner, dy, 0), dx, 1)
dist = ndimage.distance_transform_edt(~border)
X9 = [(X + ox, Y + oy) for ox in (-0.5, 0, 0.5) for oy in (-0.5, 0, 0.5)]
d = sum(bilinear(dist, x, y) for x, y in X9) / 9
fillB = np.clip((d - 0.75) / 1.0, 0, 1) * (nearest > 0)
B = base * (1 - fillB[..., None] * BLEND) + palette[nearest] * fillB[..., None] * BLEND

# C: coverage of the nearest pixel's owner, blurred (tent over +-1.5 px), cut at 0.5
cov = np.zeros(X.shape)
wsum = np.zeros(X.shape)
ix0, iy0 = np.floor(X).astype(int), np.floor(Y).astype(int)
for oy in range(-1, 3):
    for ox in range(-1, 3):
        px, py = ix0 + ox, iy0 + oy
        wgt = np.clip(1.5 - np.abs(px - X + 0.0) / 1.25, 0, None) * np.clip(1.5 - np.abs(py - Y) / 1.25, 0, None)
        cov += wgt * (owner[py, px] == nearest)
        wsum += wgt
cov /= wsum
# anti-aliased step at 0.5 (one output px wide)
edge = np.clip((cov - 0.5) * S * 0.5 + 0.5, 0, 1)
fillC = edge * (nearest > 0)
C = base * (1 - fillC[..., None] * BLEND) + palette[nearest] * fillC[..., None] * BLEND
# where coverage < 0.5 the other side owns it: show the other side's color there
other = np.zeros(X.shape, int)
best = np.zeros(X.shape)
for oy in range(-1, 3):
    for ox in range(-1, 3):
        o = owner[iy0 + oy, ix0 + ox]
        better = (o != nearest) & (best == 0)
        other = np.where(better, o, other)
        best = np.where(better, 1, best)
fillO = (1 - edge) * (other > 0)
C = C * (1 - fillO[..., None]) + (TERRAIN * (1 - BLEND) + palette[other] * BLEND) * fillO[..., None]

sep = np.ones((h * S, 6, 3))
img = np.concatenate([A, sep, B, sep, C], axis=1)
Image.fromarray((np.clip(img, 0, 1) * 255).astype(np.uint8)).save(Path(__file__).with_name("country_fill.png"))
print("wrote", Path(__file__).with_name("country_fill.png"))
