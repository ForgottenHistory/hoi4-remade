"""Prototype: the fog of rain and thunderstorms when zoomed in (HOI4 screenshot: a pale haze over the terrain, which
stays readable through it, with lighter puffs drifting in it; greyer and denser over a thunderstorm), before the port.

HOI4 makes it with cloud puff particles around each weather position (gfx/particles/environment):
    rain_clouds:            cloud_6.dds puffs ~60 map px (+-20), additive, colour (110,120,150) alpha 180, life 2.6 s,
                            drifting ~5 px/s
    lightning_storm_clouds: the same puffs alpha-blended, grey 160, alpha 150, life 10 s
Model (the port: procedural in Hoi4Precipitation, no particles):
    f     = fog amount from the weather mask (rain, storm), blurred, its edge broken up by noise
    grey  = lerp(terrain, its grey, f * desat)                         (the colour washes out)
    haze  = lerp(grey, haze colour, f * hazeAlpha)                   (rain: bright bluish, storm: grey)
    puffs = soft round gaussian blobs, one per ~puff map px cell at most, each fading in and out over its life (the
            particles), plus a layer of smaller ones; lerp toward the puff colour (rain: light, storm: grey)

    python Tools/prototypes/weather/weather_fog.py --date 1936-04-15 [--crop x0,y0,x1,y1] [--scale 5]
Background: snow_render.py with the same crop and scale. Writes fog_<date>.png (gitignored with the other previews).
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
# weather -> (fog amount, storm share)
WEATHER_FOG = {"rain_light": (0.7, 0.0), "rain_heavy": (1.0, 1.0)}


def fbm(h, w, scale, seed, octaves=4):
    rng = np.random.default_rng(seed)
    out = np.zeros((h, w), np.float32)
    amp, total = 1.0, 0.0
    for octave in range(octaves):
        s = max(2, int(scale / (2 ** octave)))
        g = rng.random((h // s + 3, w // s + 3)).astype(np.float32)
        out += amp * zoom(g, s, order=3)[:h, :w]
        total += amp
        amp *= 0.5
    out /= total
    return (out - out.min()) / (out.max() - out.min() + 1e-6)


def puff_layer(H, W, S, x0, y0, cell, phase_t, life, seed):
    """Soft round puffs like HOI4's particles (the port's Hoi4FogPuffLayer): one per grid cell at most (60%), at a
    hashed spot, gaussian falloff, fading in and out over its life (HOI4 smoke_fade), each at its own phase."""
    rng = np.random.default_rng(seed)
    oy, ox = np.mgrid[0:H, 0:W]
    mx, my = x0 + (ox + 0.5) / S, y0 + (oy + 0.5) / S
    cx0, cy0 = int(np.floor(x0 / cell)) - 1, int(np.floor(y0 / cell)) - 1
    cx1, cy1 = int(np.ceil((x0 + W / S) / cell)) + 1, int(np.ceil((y0 + H / S) / cell)) + 1
    out = np.zeros((H, W), np.float32)
    for cy in range(cy0, cy1 + 1):
        for cx in range(cx0, cx1 + 1):
            h = rng.random(5)
            if h[0] > 0.6:
                continue
            ph = (phase_t / life + h[1]) % 1.0
            fade = smoothstep(0.2, 0.5, ph) * smoothstep(1.0, 0.8, ph)
            drift = (ph - 0.5) * life * np.array([2.0, 0.7])    # the wind, map px/s
            px, py = (cx + 0.3 + 0.4 * h[2]) * cell + drift[0], (cy + 0.3 + 0.4 * h[3]) * cell + drift[1]
            radius = cell * (0.5 + 0.3 * h[4])                 # overlapping, so they gather into patches
            r2 = ((mx - px) ** 2 + (my - py) ** 2) / radius ** 2
            out += np.exp(-r2 * 2.5) * fade
    return out


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=DEFAULT_HOI4)
    ap.add_argument("--date", default="1936-04-15")
    ap.add_argument("--crop", default="", help="map px x0,y0,x1,y1; default: around a thunderstorm edge")
    ap.add_argument("--scale", type=float, default=5.0)
    ap.add_argument("--texel", type=int, default=16)
    ap.add_argument("--blur", type=float, default=2.0, help="mask blur, texels")
    ap.add_argument("--desat", type=float, default=0.35, help="terrain desaturation at full fog")
    ap.add_argument("--haze", type=float, default=0.3, help="haze alpha at full fog")
    ap.add_argument("--puffs", type=float, default=0.6, help="puff alpha at full fog")
    ap.add_argument("--puff", type=float, default=50, help="puff size, map px")
    args = ap.parse_args()

    state = json.loads((HERE / f"state_{args.date}.json").read_text())
    kinds = defaultdict(Counter)
    prov_region = {}
    for p, v in state.items():
        kinds[v[5]][v[4]] += 1
        prov_region[int(p)] = v[5]
    region_kind = {r: c.most_common(1)[0][0] for r, c in kinds.items()}
    ids = province_ids(Path(args.hoi4))

    if args.crop:
        x0, y0, x1, y1 = map(int, args.crop.split(","))
    else:  # a storm province on land next to a non-storm one
        storm = [p for p, r in prov_region.items() if region_kind.get(r) == "rain_heavy"]
        if not storm:
            sys.exit("no thunderstorm on this date; pass --crop")
        ys, xs = np.nonzero(np.isin(ids, storm[: max(1, len(storm) // 2)]))
        k = len(xs) // 2
        cx, cy = int(xs[k]), int(ys[k])
        x0, y0, x1, y1 = cx - 150, cy - 100, cx + 150, cy + 100
    S = args.scale
    crop = f"{x0},{y0},{x1},{y1}"

    subprocess.run([sys.executable, str(HERE / "snow_render.py"), "--date", args.date, "--crop", crop,
                    "--scale", str(S), "--tag", "fogbg"], check=True, stdout=subprocess.DEVNULL)
    bg = np.asarray(Image.open(HERE / f"snow_{args.date}_fogbg.png").convert("RGB")).astype(np.float32) / 255
    H, W = bg.shape[:2]

    # mask (fog, storm) per texel, blurred, sampled at output pixels
    T = args.texel
    mx0, my0 = (x0 // T - 4) * T, (y0 // T - 4) * T
    mw, mh = (x1 - mx0) // T + 8, (y1 - my0) // T + 8
    mask = np.zeros((mh, mw, 2), np.float32)
    for j in range(mh):
        for i in range(mw):
            px, py = mx0 + i * T + T // 2, my0 + j * T + T // 2
            if 0 <= px < ids.shape[1] and 0 <= py < ids.shape[0]:
                r = prov_region.get(int(ids[py, px]))
                if r is not None:
                    mask[j, i] = WEATHER_FOG.get(region_kind.get(r), (0.0, 0.0))
    mask = np.dstack([gaussian_filter(mask[..., c], args.blur) for c in range(2)])
    oy, ox = np.mgrid[0:H, 0:W]
    u = (x0 + (ox + 0.5) / S - mx0) / T - 0.5
    v = (y0 + (oy + 0.5) / S - my0) / T - 0.5
    iu, iv = np.floor(u).astype(int), np.floor(v).astype(int)
    fu, fv = (u - iu)[..., None], (v - iv)[..., None]
    at = lambda i, j: mask[np.clip(j, 0, mh - 1), np.clip(i, 0, mw - 1)]
    m = ((at(iu, iv) * (1 - fu) + at(iu + 1, iv) * fu) * (1 - fv)
         + (at(iu, iv + 1) * (1 - fu) + at(iu + 1, iv + 1) * fu) * fv)
    fog_mask, storm = m[..., 0], m[..., 1]

    edge = fbm(H, W, 80 * S, 7)
    f = smoothstep(0.15, 0.85, fog_mask + (edge - 0.5) * 0.5)
    gauss = puff_layer(H, W, S, x0, y0, args.puff, 1.3, 3.0, 11) + 0.6 * puff_layer(H, W, S, x0, y0, args.puff * 0.55, 2.1, 3.0, 12)
    detail = fbm(H, W, args.puff * 0.35 * S, 13, octaves=3)
    puffs = np.clip(gauss * 1.3 - (1 - detail) * 0.55, 0, 1)        # noise erodes the edges: billowy, like cloud_6
    st = storm[..., None]
    haze_c = (1 - st) * np.array([0.80, 0.81, 0.85]) + st * np.array([0.62, 0.62, 0.64])
    puff_c = (1 - st) * np.array([0.95, 0.95, 0.97]) + st * np.array([0.86, 0.86, 0.88])
    grey = bg.mean(-1, keepdims=True)
    out = bg + (grey - bg) * (f * args.desat)[..., None]                    # fog washes the colour out
    out = out + (haze_c - out) * (f * args.haze)[..., None]
    out = out + (puff_c - out) * (f * puffs * args.puffs)[..., None]

    name = HERE / f"fog_{args.date}.png"
    Image.fromarray((np.clip(out, 0, 1) * 255).astype(np.uint8)).save(name)
    print(f"{args.date} crop {crop}: fog over {np.mean(f > 0.2) * 100:.0f}% (storm {np.mean(storm > 0.5) * 100:.0f}%); wrote {name}")


if __name__ == "__main__":
    main()
