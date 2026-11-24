"""Prototype: clouds that follow the weather (the HOI4 look from its screenshots: a dark cloud mass over thunderstorm
provinces, white puffs over a blizzard, lighter cloud over rain and snow; grey mist when zoomed in), before the port.

Model (the port: Archon's atmosphere clouds plus a GAME cloud mask):
    mask  = per map texel (1/8 map px): the region's weather -> (cloud colour rgb, coverage a), blurred
    cloud = noise above a threshold, as the engine's clouds; where the mask has coverage the threshold drops to it
    colour = mask colour, darker in the thin parts (the engine's cloud shade)
    ground shadow under the weather clouds (darker for dark clouds)

    python Tools/prototypes/weather/weather_clouds.py --date 1936-04-15 [--crop ...] [--scale 1]
Background: snow_render.py with the same crop and scale. Writes clouds_<date>.png (gitignored).
"""
import argparse
import json
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter, zoom

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "supply"))
from render_rails import DEFAULT_HOI4, province_ids  # noqa: E402

HERE = Path(__file__).resolve().parent
# weather -> (r, g, b, coverage): HOI4 screenshots (thunderstorm black cloud, blizzard white)
WEATHER_CLOUD = {
    "no_phenomenon": (0.0, 0.0, 0.0, 0.0),
    "rain_light": (0.62, 0.64, 0.68, 0.55),
    "rain_heavy": (0.16, 0.17, 0.2, 0.8),
    "snow": (0.85, 0.87, 0.92, 0.5),
    "blizzard": (0.97, 0.97, 1.0, 0.85),
    "sandstorm": (0.8, 0.66, 0.45, 0.7),
}


def fbm(h, w, scale, seed):
    rng = np.random.default_rng(seed)
    out = np.zeros((h, w), np.float32)
    amp, total = 1.0, 0.0
    for octave in range(5):
        s = max(2, int(scale / (2 ** octave)))
        g = rng.random((h // s + 3, w // s + 3)).astype(np.float32)
        up = zoom(g, s, order=3)[:h, :w]
        out += amp * up
        total += amp
        amp *= 0.5
    return out / total


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=DEFAULT_HOI4)
    ap.add_argument("--date", default="1936-04-15")
    ap.add_argument("--crop", default="2400,200,3600,800")
    ap.add_argument("--scale", type=float, default=1.0)
    ap.add_argument("--texel", type=int, default=8, help="mask texel size, map px")
    ap.add_argument("--blur", type=float, default=2.5, help="mask blur, texels")
    ap.add_argument("--opacity", type=float, default=0.85)
    ap.add_argument("--softness", type=float, default=0.22)
    ap.add_argument("--noisescale", type=float, default=90, help="cloud noise feature size, map px")
    args = ap.parse_args()
    x0, y0, x1, y1 = map(int, args.crop.split(","))
    S = args.scale

    subprocess.run([sys.executable, str(HERE / "snow_render.py"), "--date", args.date, "--crop", args.crop,
                    "--scale", str(S), "--tag", "cloudbg"], check=True, stdout=subprocess.DEVNULL)
    bg = np.asarray(Image.open(HERE / f"snow_{args.date}_cloudbg.png").convert("RGB")).astype(np.float32) / 255
    H, W = bg.shape[:2]

    state = json.loads((HERE / f"state_{args.date}.json").read_text())
    kinds = defaultdict(Counter)
    prov_region = {}
    for p, v in state.items():
        kinds[v[5]][v[4]] += 1
        prov_region[int(p)] = v[5]
    region_kind = {r: c.most_common(1)[0][0] for r, c in kinds.items()}

    # mask at texel resolution over the crop (+ margin)
    T = args.texel
    ids = province_ids(Path(args.hoi4))
    mx0, my0 = (x0 // T - 4) * T, (y0 // T - 4) * T
    mw, mh = (x1 - mx0) // T + 8, (y1 - my0) // T + 8
    mask = np.zeros((mh, mw, 4), np.float32)
    for j in range(mh):
        for i in range(mw):
            px, py = mx0 + i * T + T // 2, my0 + j * T + T // 2
            if 0 <= px < ids.shape[1] and 0 <= py < ids.shape[0]:
                r = prov_region.get(int(ids[py, px]))
                if r is not None:
                    mask[j, i] = WEATHER_CLOUD[region_kind.get(r, "no_phenomenon")]
    # colour weighted by coverage, then blurred
    rgb = mask[..., :3] * mask[..., 3:]
    a = mask[..., 3]
    rgb = np.dstack([gaussian_filter(rgb[..., c], args.blur) for c in range(3)])
    a = gaussian_filter(a, args.blur)
    rgb = rgb / np.maximum(a[..., None], 1e-4)
    # sample at output pixels (bilinear)
    oy, ox = np.mgrid[0:H, 0:W]
    mxp, myp = x0 + (ox + 0.5) / S, y0 + (oy + 0.5) / S
    u, v = (mxp - mx0) / T - 0.5, (myp - my0) / T - 0.5
    iu, iv = np.floor(u).astype(int), np.floor(v).astype(int)
    fu, fv = (u - iu)[..., None], (v - iv)[..., None]
    def at(arr, i, j):
        return arr[np.clip(j, 0, mh - 1), np.clip(i, 0, mw - 1)]
    m4 = np.dstack([rgb, a])
    m = ((at(m4, iu, iv) * (1 - fu) + at(m4, iu + 1, iv) * fu) * (1 - fv)
         + (at(m4, iu, iv + 1) * (1 - fu) + at(m4, iu + 1, iv + 1) * fu) * fv)

    noise = fbm(H, W, args.noisescale * S, 3)
    noise = (noise - noise.min()) / (noise.max() - noise.min())
    cover = m[..., 3]
    threshold = 1.0 - cover
    soft = args.softness * 0.5
    density = np.clip((noise - (threshold - soft)) / (2 * soft + 1e-6), 0, 1)
    density = density * density * (3 - 2 * density) * (cover > 0.01)
    thickness = np.clip((noise - threshold) / args.softness, 0, 1)
    colour = m[..., :3] * (0.7 + 0.3 * thickness[..., None] ** 0.7)
    alpha = (density * args.opacity)[..., None]
    # ground shadow (offset, darker under dark clouds)
    sh = np.roll(np.roll(density, int(30 * S), axis=0), int(-40 * S), axis=1)
    shade = sh * 0.35 * (1.2 - m[..., :3].mean(-1))
    out = bg * (1 - np.clip(shade, 0, 0.8))[..., None]
    out = out * (1 - alpha) + colour * alpha
    name = HERE / f"clouds_{args.date}.png"
    Image.fromarray((np.clip(out, 0, 1) * 255).astype(np.uint8)).save(name)
    print(f"{args.date}: weather clouds over {np.mean(cover > 0.2) * 100:.0f}% of the crop; wrote {name}")


if __name__ == "__main__":
    main()
