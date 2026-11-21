"""Prototype: which pairs of neighbouring land provinces have a river between them (HOI4 river crossing).

HOI4 draws rivers along province borders, so a river between two provinces runs on pixels of one of them
right next to pixels of the other. A river pixel "sees" a pair (a, b) when its 3x3 window holds land pixels
of both. A pair is a crossing when enough river pixels see it (MIN_PIXELS): a river that flows *through* a
border instead of along it touches the pair at only a pixel or two. Size: HOI4 rivers.bmp palette 3..6 are
small rivers, 7..11 large (defines RIVER_SMALL_STOP_INDEX = 6, RIVER_LARGE_STOP_INDEX = 11).

    python Tools/prototypes/rivers/river_crossings.py [--hoi4 DIR] [--data Hoi4Data] [--crop x0,y0,x1,y1]

Writes river_crossings_preview.png next to this script: province borders grey, river pixels blue, and the
shared border of each crossing pair yellow (small) or red (large).
"""
import argparse
from pathlib import Path

import numpy as np
from PIL import Image

MIN_PIXELS = 4


def province_ids(data: Path):
    """Province id per pixel (0 = unknown) and the set of water province ids."""
    px = np.asarray(Image.open(data / "map" / "provinces.png").convert("RGB"), dtype=np.uint32)
    packed = (px[..., 0] << 16) | (px[..., 1] << 8) | px[..., 2]
    keys, ids, water = [], [], set()
    for line in (data / "map" / "definition.csv").read_text(encoding="utf-8").splitlines()[1:]:
        f = line.split(";")
        if len(f) < 4 or not f[0].isdigit():
            continue
        pid = int(f[0])
        keys.append((int(f[1]) << 16) | (int(f[2]) << 8) | int(f[3]))
        ids.append(pid)
        if len(f) > 5 and f[5].strip() == "x":
            water.add(pid)
    keys = np.array(keys, dtype=np.uint32)
    ids = np.array(ids, dtype=np.int32)
    order = np.argsort(keys)
    keys, ids = keys[order], ids[order]
    i = np.searchsorted(keys, packed).clip(0, len(keys) - 1)
    return np.where(keys[i] == packed, ids[i], 0), water


def river_crossings(prov, water, rivers_bmp: Path, min_pixels=MIN_PIXELS):
    """{(a, b): (pixels, large)} for land province pairs a < b with a river between them."""
    a = np.asarray(Image.open(rivers_bmp)).astype(np.int16)
    river = (a >= 0) & (a <= 11)  # 0..2 markers, 3..11 widths; 255 etc. = no river
    large = (a >= 7) & (a <= 11)
    land = prov.copy()
    if water:
        land[np.isin(prov, np.fromiter(water, dtype=np.int32))] = 0
    H, W = prov.shape
    ys, xs = np.nonzero(river)
    # 3x3 window of land ids around every river pixel
    win = np.stack([land[np.clip(ys + dy, 0, H - 1), np.clip(xs + dx, 0, W - 1)]
                    for dy in (-1, 0, 1) for dx in (-1, 0, 1)], axis=1)
    big = large[ys, xs]
    counts, sizes = {}, {}
    for row, lg in zip(win, big):
        u = np.unique(row[row > 0])
        if len(u) < 2:
            continue
        for i in range(len(u)):
            for j in range(i + 1, len(u)):
                k = (int(u[i]), int(u[j]))
                counts[k] = counts.get(k, 0) + 1
                sizes[k] = sizes.get(k, 0) + (1 if lg else 0)
    # Large when most of the pixels along the pair are large river
    return {k: (n, sizes[k] * 2 > n) for k, n in counts.items() if n >= min_pixels}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=r"C:\Program Files (x86)\Steam\steamapps\common\Hearts of Iron IV")
    ap.add_argument("--data", default=str(Path(__file__).resolve().parents[3] / "Hoi4Data"))
    ap.add_argument("--crop", default="2600,500,3400,1100", help="x0,y0,x1,y1 in map px (default: central Europe)")
    ap.add_argument("--scale", type=int, default=2)
    args = ap.parse_args()

    prov, water = province_ids(Path(args.data))
    rivers_bmp = Path(args.hoi4) / "map" / "rivers.bmp"
    cross = river_crossings(prov, water, rivers_bmp)
    hist = np.bincount(np.minimum([n for n, _ in cross.values()], 40))
    print(f"{len(cross)} crossings ({sum(1 for _, l in cross.values() if l)} large); pixels per pair histogram: {hist.tolist()}")

    # Also how many pairs are rejected (fewer than MIN_PIXELS): rivers crossing a border
    allp = river_crossings(prov, water, rivers_bmp, min_pixels=1)
    print(f"rejected as through-crossings: {len(allp) - len(cross)}")

    x0, y0, x1, y1 = map(int, args.crop.split(","))
    p = prov[y0:y1, x0:x1]
    a = np.asarray(Image.open(rivers_bmp)).astype(np.int16)[y0:y1, x0:x1]
    img = np.full(p.shape + (3,), 235, np.uint8)
    img[np.isin(p, np.fromiter(water, dtype=np.int32))] = (150, 180, 210)
    edge = np.zeros(p.shape, bool)
    edge[:-1] |= p[:-1] != p[1:]
    edge[:, :-1] |= p[:, :-1] != p[:, 1:]
    img[edge] = (160, 160, 160)
    img[(a >= 0) & (a <= 11)] = (30, 70, 220)
    # Crossing borders on top: border pixels between a crossing pair
    for dy, dx in ((1, 0), (0, 1)):
        q = np.roll(np.roll(p, -dy, 0), -dx, 1)
        lo, hi = np.minimum(p, q), np.maximum(p, q)
        diff = lo != hi
        for y, x in zip(*np.nonzero(diff)):
            c = cross.get((int(lo[y, x]), int(hi[y, x])))
            if c:
                img[y, x] = (220, 40, 30) if c[1] else (240, 190, 0)
    out = Path(__file__).with_name("river_crossings_preview.png")
    Image.fromarray(img).resize((img.shape[1] * args.scale, img.shape[0] * args.scale), Image.NEAREST).save(out)
    print(f"-> {out}")


if __name__ == "__main__":
    main()
