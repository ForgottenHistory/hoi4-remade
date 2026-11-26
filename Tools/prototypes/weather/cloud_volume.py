"""Prototype: clouds with volume instead of flat noise sheets. Mirrors the engine's MapAtmosphere shader (MakeField,
CloudTop, CloudVolume), so a look tuned here ports by copying the numbers.

The clouds were a 2D noise above a threshold, drawn in one colour: a soft grey plane. Here the same noise is a HEIGHT
FIELD over a flat cloud base (what a cumulus is: flat underneath, domed on top), drawn as a terrain shader draws a
heightmap:

    shape    Perlin-Worley noise (round billows, not smooth blobs) above the coverage threshold, with Worley billows
             on top that also eat the thin edge. The shape noise is equalised, so coverage 0.25 is a quarter of the sky.
    parallax the view ray is marched down through the cloud slab: towers stand up and hide what is behind them
    light    sun on the top surface (normal from the height gradient, with finer billows), self shadow, darker valleys
    alpha    from the cloud's thickness: thin cloud is see-through, only the thick middle is solid
    colour   the weather's colour is the cloud's albedo: a thunderstorm stays dark, with lit crests

    change   a swirling flow stirs the noise (cloudChurn, map px per second): every part of a cloud moves, in
             eddies, with no common direction, and clouds form and dissolve as it goes (see Field). Weather clouds
             have no wind (maskWind): they are stirred in place over their weather; the banks also ride the wind.

Two fields, as in the engine: the cloud banks (big, everywhere, seen zoomed out) and the weather clouds (the GAME's
mask, smaller, seen closer in). The banks part where the weather clouds are, so the two never show through each other.

Check EVERY zoom before porting (the first port was only looked at close up, and the banks buried the world view):

    python Tools/prototypes/weather/cloud_volume.py --sheet zoom     world, continent, region, close: one image
    python Tools/prototypes/weather/cloud_volume.py --sheet time     the clouds drifting, a camera pan: one image
    python Tools/prototypes/weather/cloud_volume.py --crop x0,y0,x1,y1 --scale 3 [--force rain_heavy --flash x,y]
Background: snow_render.py with the same crop and scale. Writes cloudvol_<date>_<tag>.png (gitignored).
"""
import argparse
import json
import subprocess
import sys
from collections import Counter, defaultdict
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from scipy.ndimage import gaussian_filter

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "supply"))
from render_rails import DEFAULT_HOI4, province_ids  # noqa: E402

HERE = Path(__file__).resolve().parent
MAP_W, MAP_H = 5632, 2048
# weather -> (r, g, b, coverage), as Hoi4WeatherSky.WeatherCloud
WEATHER_CLOUD = {
    "no_phenomenon": (0.0, 0.0, 0.0, 0.0),
    "rain_light": (0.62, 0.64, 0.68, 0.40),
    "rain_heavy": (0.2, 0.21, 0.25, 0.60),
    "snow": (0.85, 0.87, 0.92, 0.25),
    "blizzard": (0.97, 0.97, 1.0, 0.55),
    "sandstorm": (0.8, 0.66, 0.45, 0.55),
}
# the atmosphere style (Tools/hoi4_visual_style.json5)
STYLE = dict(
    cloudColor=(0.95, 0.95, 0.97), cloudShade=(0.6, 0.64, 0.72),
    cloudCoverage=0.22, cloudSoftness=0.8, cloudOpacity=0.85, cloudScale=1400, cloudZoom=(0.35, 0.9),
    cloudThickness=40, cloudHeight=60, shadowStrength=0.3, shadowOffset=(-40, 30),
    maskOpacity=0.9, maskScale=260, maskShadow=0.4, maskZoom=(2.0, 3.0), maskSoftness=0.6, maskWarp=60,
    cloudVariance=0.6, cloudChurn=2.0, maskWind=(0.0, 0.0),
    maskThickness=10, cloudSteps=12, cloudRelief=0.7, cloudBillows=0.5, cloudVeil=1.0,
)


# ---------------------------------------------------------------------------------------------- the noise texture

def _fade(t):
    return t * t * t * (t * (t * 6 - 15) + 10)


def perlin(size, freq, seed):
    rng = np.random.default_rng(seed)
    ang = rng.random((freq, freq)) * 2 * np.pi
    gx, gy = np.cos(ang), np.sin(ang)
    c = (np.arange(size) + 0.5) / size * freq
    x, y = np.meshgrid(c, c)
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = x - x0, y - y0

    def dot(ix, iy, dx, dy):
        ix, iy = ix % freq, iy % freq
        return gx[iy, ix] * dx + gy[iy, ix] * dy

    sx, sy = _fade(fx), _fade(fy)
    a = dot(x0, y0, fx, fy) * (1 - sx) + dot(x0 + 1, y0, fx - 1, fy) * sx
    b = dot(x0, y0 + 1, fx, fy - 1) * (1 - sx) + dot(x0 + 1, y0 + 1, fx - 1, fy - 1) * sx
    return a * (1 - sy) + b * sy


def worley(size, freq, seed):
    """Tileable inverted cell noise: 1 at a cell's point, falling with the squared distance (a round top)."""
    rng = np.random.default_rng(seed)
    px, py = rng.random((freq, freq)), rng.random((freq, freq))
    c = (np.arange(size) + 0.5) / size * freq
    x, y = np.meshgrid(c, c)
    cx, cy = np.floor(x).astype(int), np.floor(y).astype(int)
    best = np.full(x.shape, 9.0)
    for j in (-1, 0, 1):
        for i in (-1, 0, 1):
            ix, iy = cx + i, cy + j
            dx = ix + px[iy % freq, ix % freq] - x
            dy = iy + py[iy % freq, ix % freq] - y
            best = np.minimum(best, dx * dx + dy * dy)
    return 1.0 - np.clip(best, 0, 1)


def fbm(fn, size, freq, octaves, seed, gain=0.5):
    out, amp, total = np.zeros((size, size)), 1.0, 0.0
    for o in range(octaves):
        out += amp * fn(size, freq << o, seed + o * 101)
        total += amp
        amp *= gain
    return out / total


def stretch(a):
    lo, hi = np.percentile(a, 1), np.percentile(a, 99)
    return np.clip((a - lo) / (hi - lo), 0, 1)


def equalise(a):
    """Rank transform: the values become uniform in 0..1, so a threshold of 1 - c leaves exactly c of the area."""
    flat = a.ravel()
    rank = np.empty(flat.size)
    rank[np.argsort(flat, kind="stable")] = np.arange(flat.size)
    return (rank / (flat.size - 1)).reshape(a.shape)


def make_noise(size=256):
    p = stretch(fbm(perlin, size, 4, 5, 11))
    w = stretch(fbm(worley, size, 4, 3, 23, gain=0.4))
    pw = equalise((p - (1 - w) * 0.6) / (1 - (1 - w) * 0.6 + 1e-6))
    g = stretch(fbm(perlin, size, 3, 4, 37))
    b = stretch(fbm(worley, size, 8, 3, 51, gain=0.55))
    a = stretch(fbm(perlin, size, 3, 4, 67))
    return np.dstack([pw, g, b, a]).astype(np.float32)


def tex(noise, u, v):
    n = noise.shape[0]
    x, y = u * n - 0.5, v * n - 0.5
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = (x - x0)[..., None], (y - y0)[..., None]
    x0, y0, x1, y1 = x0 % n, y0 % n, (x0 + 1) % n, (y0 + 1) % n
    return ((noise[y0, x0] * (1 - fx) + noise[y0, x1] * fx) * (1 - fy)
            + (noise[y1, x0] * (1 - fx) + noise[y1, x1] * fx) * fy)


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


# ---------------------------------------------------------------------------------------------- the shader

WIND = np.array([5.0, 1.5])
DETAIL, FINE = 1.8, 5.3


FLOW_REACH = 0.15  # tiles: how far the flow carries the noise before that reading is faded out and restarted


class Field:
    """MakeField: one cloud field's noise tile, its drift with the wind, and the FLOW that stirs it (once per pixel).

    The flow is a slow swirling velocity field (two noise channels): every point of the cloud moves, but in eddies, with
    no common direction. A flow can't be followed forever (the noise would smear), so the shape noise is read twice,
    each reading carried along the flow for a while and then restarted, half a cycle apart; each is faded out around its
    restart and the other covers for it (a flow map). Clouds are stirred, and form and dissolve as the readings trade."""

    def __init__(self, noise, px, py, scale, t, wind):
        self.noise, self.scale = noise, scale
        self.drift = np.array(wind, float) * t / scale
        u, v = px / scale - self.drift[0], py / scale - self.drift[1]
        w = tex(noise, u * 0.35, v * 0.35)                      # broad eddies: the flow stirs, it must not shear puffs into ribbons
        flow = (w[..., [1, 3]] - 0.5) * 2.0                       # -1..1
        self.warp = flow * 0.04
        cycle = FLOW_REACH * scale / max(STYLE["cloudChurn"], 1e-3)   # seconds
        tt = t / cycle + w[..., 0]                                # each place at its own point of the cycle: no pulse
        p1, p2 = tt % 1.0, (tt + 0.5) % 1.0
        c1, c2 = np.floor(tt), np.floor(tt + 0.5)
        self.w1 = 1 - np.abs(2 * p1 - 1)
        carry1 = -flow * (FLOW_REACH * (p1 - 0.5))[..., None]
        carry2 = -flow * (FLOW_REACH * (p2 - 0.5))[..., None]
        self.o1 = carry1 + np.dstack([(c1 * 0.37) % 1.0, (c1 * 0.71) % 1.0])   # a new piece of noise each cycle
        self.o2 = carry2 * 0.83 + np.dstack([(c2 * 0.53) % 1.0 + 0.37, (c2 * 0.29) % 1.0 + 0.11])
        self.ob = carry1 * self.w1[..., None] + carry2 * (1 - self.w1)[..., None]   # the billows go with whichever shows

    def uv(self, px, py):
        return (px / self.scale - self.drift[0] + self.warp[..., 0],
                py / self.scale - self.drift[1] + self.warp[..., 1])


def shape_noise(f, u, v):
    """ShapeNoise: the two flow readings, blended. A weighted mean of two uniform values is trapezoidal; its CDF makes
    it uniform again, so a coverage stays a share of the sky whatever the weights are."""
    a = tex(f.noise, u + f.o1[..., 0], v + f.o1[..., 1])[..., 0]
    b = tex(f.noise, u * 0.83 + f.o2[..., 0], v * 0.83 + f.o2[..., 1])[..., 0]
    w = f.w1
    m = a * w + b * (1 - w)
    lo = np.maximum(np.minimum(w, 1 - w), 1e-3)
    hi = 1 - lo
    return np.where(m < lo, m * m / (2 * lo * hi),
                    np.where(m < hi, (2 * m - lo) / (2 * hi), 1 - (1 - m) ** 2 / (2 * lo * hi)))


def cloud_top(f, px, py, cover, billows, fine=False, with_shape=False):
    """CloudTop: height 0..1 over the base. 2 samples; `fine` adds the small billows (the lit surface, not the march)."""
    u, v = f.uv(px, py)
    base = shape_noise(f, u, v)
    bu, bv = (u + f.ob[..., 0]) * DETAIL, (v + f.ob[..., 1]) * DETAIL   # the billows are stirred with the shapes
    bill = tex(f.noise, bu, bv)[..., 2]
    if fine:
        bill = bill * 0.7 + tex(f.noise, (u + f.ob[..., 0]) * FINE, (v + f.ob[..., 1]) * FINE)[..., 2] * 0.3
    shape = np.clip((base - (1 - cover)) / np.maximum(cover, 1e-3), 0, 1)
    dome = 1 - (1 - np.clip(shape * 1.4, 0, 1)) ** 2
    e = billows
    h = np.clip(dome * (1 - e + e * bill) - (1 - bill) * e * 0.35, 0, 1)
    h = h * np.clip((cover - 0.02) * 10, 0, 1)
    return (h, shape) if with_shape else h


def cloud_volume(f, px, py, lean, cover, albedo, slab, softness, opacity, S, st, sun, lights, variance=0.0):
    """CloudVolume: premultiplied rgb, alpha."""
    H, W = px.shape
    bil = st["cloudBillows"]
    steps = st["cloudSteps"]
    ax, ay = lean[..., 0] * slab, lean[..., 1] * slab
    hit = np.zeros((H, W), bool)
    s_hit = np.ones((H, W), np.float32)
    prev = np.ones((H, W), np.float32)
    for i in range(steps + 1):
        s = i / steps
        hh = cloud_top(f, px + ax * s, py + ay * s, cover, bil)
        d = (1 - s) - hh
        now = (~hit) & (d <= 0) & (hh > 0.004)
        t = np.where(now, prev / np.maximum(prev - d, 1e-5), 0)
        s_hit = np.where(now, np.clip(s - (1 - t) / steps, 0, 1), s_hit)
        hit |= now
        prev = np.where(hit, prev, d)
    hx, hy = px + ax * s_hit, py + ay * s_hit
    z = np.where(hit, 1 - s_hit, 0)

    ambient = np.array(st["cloudShade"])
    sunlight = np.array([1.0, 0.97, 0.9]) * 0.56
    e = max(f.scale * 0.006, 0.75 / S)
    k = slab * st["cloudRelief"] / (2 * e)
    dx = (cloud_top(f, hx + e, hy, cover, bil, True) - cloud_top(f, hx - e, hy, cover, bil, True)) * k
    dy = (cloud_top(f, hx, hy + e, cover, bil, True) - cloud_top(f, hx, hy - e, cover, bil, True)) * k
    nrm = np.dstack([-dx, -dy, np.ones_like(dx)])
    nrm /= np.linalg.norm(nrm, axis=-1, keepdims=True)
    diffuse = np.clip(nrm @ sun * 0.5 + 0.5, 0, 1)
    to_sun = sun[:2] / max(sun[2], 0.2) * slab
    occ = np.zeros((H, W), np.float32)
    for rise, wgt in ((0.12, 1.0), (0.3, 0.8), (0.6, 0.6)):
        occ += np.clip((cloud_top(f, hx + to_sun[0] * rise, hy + to_sun[1] * rise, cover, bil) - (z + rise)) / 0.25, 0, 1) * wgt
    lit = diffuse * np.exp(-occ * 1.1)
    valley = 0.55 + 0.45 * smoothstep(0.0, 0.7, z)
    _, inside = cloud_top(f, hx, hy, cover, bil, with_shape=True)
    # colour variance: a cloud is not one colour. Its dense heart is darker (more water, less light gets through),
    # broad patches drift between lighter and darker, and between a cooler and a warmer grey.
    u0, v0 = f.uv(hx, hy)
    patch_n = tex(f.noise, (u0 + f.ob[..., 0]) * 2.3 + 0.19, (v0 + f.ob[..., 1]) * 2.3 + 0.43)
    tone = 1 + variance * ((patch_n[..., 1] - 0.5) * 0.9 - smoothstep(0.2, 1.0, inside) * 0.45)
    hue = (patch_n[..., 3] - 0.5) * variance * 0.16
    albedo = albedo * tone[..., None] * (1 + hue[..., None] * np.array([1.0, 0.1, -1.0]))
    lum = albedo.mean(-1, keepdims=True)
    colour = (albedo + (1 - lum) * 0.18 * lit[..., None] ** 2) * (ambient * valley[..., None] + sunlight * lit[..., None])
    # alpha as light through smoke: 1 - exp(-thickness / softness). Never solid: the map shows through all but the
    # thickest cloud (HOI4's clouds are translucent smoke, not objects). And it thins out toward the cloud's outline
    # (how far inside the coverage threshold the surface point is), so there is no cut-out edge.
    alpha = hit * (1 - np.exp(-z / softness)) * smoothstep(0.0, 0.45, inside) * opacity

    u, v = f.uv(px, py)
    wide = np.clip((shape_noise(f, u, v) - (1 - cover) + 0.14) / 0.3, 0, 1) * np.clip((cover - 0.02) * 10, 0, 1)
    fine = tex(f.noise, (u + f.ob[..., 0]) * 6.0, (v + f.ob[..., 1]) * 6.0)[..., 2]
    veil = smoothstep(0.0, 1.0, wide) * (0.1 + 0.4 * fine) * st["cloudVeil"] * opacity   # wisps well beyond the towers
    veil_colour = np.clip(albedo * (1 + variance * 0.5), 0, 1) * (ambient * 0.85 + sunlight * 0.3)   # wisps are lighter
    rgb = colour * alpha[..., None] + veil_colour * (veil * (1 - alpha))[..., None]
    alpha = alpha + veil * (1 - alpha)
    for lx, ly, radius, power in lights:
        d2 = ((hx - lx) ** 2 + (hy - ly) ** 2) / radius ** 2
        glow = np.exp(-d2 * 2.0) * power * (0.35 + 0.65 * np.exp(-z * 2.2))
        rgb = rgb + (glow * alpha)[..., None] * np.array([0.75, 0.82, 1.0])
    return rgb, alpha


def zoom_factor(S, zoom):
    return 1.0 - float(smoothstep(zoom[0], zoom[1], np.float32(S)))


# ---------------------------------------------------------------------------------------------- one frame

class World:
    def __init__(self, args):
        self.args = args
        self.noise = make_noise()
        state = json.loads((HERE / f"state_{args.date}.json").read_text())
        kinds = defaultdict(Counter)
        prov_region = {}
        for p, v in state.items():
            kinds[v[5]][v[4]] += 1
            prov_region[int(p)] = v[5]
        region_kind = {r: c.most_common(1)[0][0] for r, c in kinds.items()}
        if args.force:
            region_kind = {r: (args.force if k != "no_phenomenon" else k) for r, k in region_kind.items()}
        # the weather mask over the whole map, as Hoi4WeatherSky paints it (16 map px texels, blurred)
        T = self.T = 16
        ids = province_ids(Path(args.hoi4))
        centres = ids[T // 2::T, T // 2::T]
        lut = np.zeros((int(ids.max()) + 1, 4), np.float32)
        for p, r in prov_region.items():
            if p < len(lut):
                lut[p] = WEATHER_CLOUD[region_kind.get(r, "no_phenomenon")]
        mask = lut[centres]
        rgb = mask[..., :3] * mask[..., 3:]
        a = gaussian_filter(mask[..., 3], 1.6)
        rgb = np.dstack([gaussian_filter(rgb[..., c], 1.6) for c in range(3)]) / np.maximum(a[..., None], 1e-4)
        self.mask = np.dstack([rgb, a])

    def mask_at(self, px, py):
        m4, T = self.mask, self.T
        mh, mw = m4.shape[:2]
        u, v = px / T - 0.5, py / T - 0.5
        iu, iv = np.floor(u).astype(int), np.floor(v).astype(int)
        fu, fv = (u - iu)[..., None], (v - iv)[..., None]

        def at(i, j):
            return m4[np.clip(j, 0, mh - 1), np.clip(i, 0, mw - 1)]
        return ((at(iu, iv) * (1 - fu) + at(iu + 1, iv) * fu) * (1 - fv)
                + (at(iu, iv + 1) * (1 - fu) + at(iu + 1, iv + 1) * fu) * fv)

    def warped_mask(self, px, py, t):
        st = STYLE
        slide = st["cloudChurn"] / st["maskScale"] * t * 0.25  # the outline wanders slowly, with no wind
        q = tex(self.noise, px / (st["maskScale"] * 1.7) + slide, py / (st["maskScale"] * 1.7) - slide * 0.7)
        return self.mask_at(px + (q[..., 3] - 0.5) * 2 * st["maskWarp"], py + (q[..., 1] - 0.5) * 2 * st["maskWarp"])

    def frame(self, crop, S, t=0.0, lights=(), pan=(0.0, 0.0)):
        args, st, noise = self.args, STYLE, self.noise
        x0, y0, x1, y1 = crop
        subprocess.run([sys.executable, str(HERE / "snow_render.py"), "--date", args.date, "--crop",
                        ",".join(str(int(c)) for c in crop), "--scale", str(S), "--tag", "cloudbg"],
                       check=True, stdout=subprocess.DEVNULL)
        bg = np.asarray(Image.open(HERE / f"snow_{args.date}_cloudbg.png").convert("RGB")).astype(np.float32) / 255
        H, W = bg.shape[:2]
        oy, ox = np.mgrid[0:H, 0:W]
        px, py = x0 + (ox + 0.5) / S, y0 + (oy + 0.5) / S

        # the camera: above a point south of the view's centre, so rays lean north and outward (perspective)
        view_h = (y1 - y0)
        cam_height = 1.3 * view_h
        nadir = np.array([(x0 + x1) / 2 + pan[0], (y0 + y1) / 2 + 0.75 * cam_height + pan[1]])
        lean = np.dstack([px - nadir[0], py - nadir[1]]) / cam_height
        lean = lean / np.maximum(np.linalg.norm(lean, axis=-1, keepdims=True) / 0.7, 1.0)   # at most 35 degrees: no smeared towers

        off = np.array(st["shadowOffset"], float)
        sun = np.array([-off[0], -off[1], st["cloudHeight"]])
        sun = sun / np.linalg.norm(sun)
        bank_fade = zoom_factor(S, st["cloudZoom"])
        # weather clouds come in only once the banks are fully gone, and go out again closer in
        zoom_in = (st["cloudZoom"][1], st["cloudZoom"][1] * 1.6)
        mask_fade = zoom_factor(S, st["maskZoom"]) * (1 - zoom_factor(S, zoom_in))

        def layers(qx, qy):
            """Both fields at these positions: the banks part where the weather clouds are."""
            m = self.warped_mask(qx, qy, t) if mask_fade > 0.001 else np.zeros(qx.shape + (4,), np.float32)
            bank_cover = st["cloudCoverage"] * (1 - np.clip(m[..., 3] * 3.0, 0, 1) * mask_fade)
            return bank_cover, m

        # ground: shadows
        out = bg.copy()
        sx, sy = px - off[0], py - off[1]
        bank_cover, m = layers(sx, sy)
        if bank_fade > 0.001:
            f = Field(noise, sx, sy, st["cloudScale"], t, WIND)
            h, inside = cloud_top(f, sx, sy, bank_cover, st["cloudBillows"], with_shape=True)
            shade = smoothstep(0.0, 0.8, h) * smoothstep(0.0, 0.45, inside) * st["shadowStrength"] * bank_fade
            out *= (1 - shade)[..., None]
        if mask_fade > 0.001:
            f = Field(noise, sx, sy, st["maskScale"], t, st["maskWind"])
            dark = 1.2 - m[..., :3].mean(-1)
            h, inside = cloud_top(f, sx, sy, m[..., 3], st["cloudBillows"], with_shape=True)
            shade = np.clip(smoothstep(0.0, 0.8, h) * smoothstep(0.0, 0.45, inside) * st["maskShadow"] * dark * mask_fade, 0, 1)
            out *= (1 - shade)[..., None]

        # clouds
        bank_cover, m = layers(px, py)
        rgb = np.zeros_like(bg)
        alpha = np.zeros((H, W), np.float32)
        if bank_fade > 0.001:
            f = Field(noise, px, py, st["cloudScale"], t, WIND)
            albedo = np.broadcast_to(np.array(st["cloudColor"], np.float32), bg.shape)
            rgb, alpha = cloud_volume(f, px, py, lean, bank_cover, albedo, st["cloudThickness"], st["cloudSoftness"],
                                      st["cloudOpacity"] * bank_fade, S, st, sun, (), st["cloudVariance"] * 0.4)
        if mask_fade > 0.001:
            f = Field(noise, px, py, st["maskScale"], t, st["maskWind"])
            crgb, ca = cloud_volume(f, px, py, lean, m[..., 3], m[..., :3], st["maskThickness"], st["maskSoftness"],
                                    st["maskOpacity"] * mask_fade, S, st, sun, lights, st["cloudVariance"])
            rgb = crgb + rgb * (1 - ca)[..., None]
            alpha = ca + alpha * (1 - ca)
        out = out * (1 - alpha[..., None]) + np.clip(rgb, 0, 1)
        return (np.clip(out, 0, 1) * 255).astype(np.uint8), float(np.mean(alpha > 0.3))


def label(img, text):
    im = Image.fromarray(img)
    d = ImageDraw.Draw(im)
    d.rectangle([0, 0, 8 + 7 * len(text), 18], fill=(0, 0, 0))
    d.text((4, 3), text, fill=(255, 255, 255))
    return np.asarray(im)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=DEFAULT_HOI4)
    ap.add_argument("--date", default="1936-01-20")
    ap.add_argument("--crop", default="3050,430,3450,690")
    ap.add_argument("--scale", type=float, default=3.0, help="output px per map px (the zoom)")
    ap.add_argument("--tag", default="near")
    ap.add_argument("--sheet", default="", help="zoom | time")
    ap.add_argument("--force", default="", help="weather for every region that has any (rain_heavy: a thunderstorm)")
    ap.add_argument("--flash", default="", help="lightning inside the cloud at map px x,y")
    for key, value in STYLE.items():
        if isinstance(value, (int, float)):
            ap.add_argument("--" + key, type=type(value), default=None)
    args = ap.parse_args()
    for key in STYLE:
        if getattr(args, key, None) is not None:
            STYLE[key] = getattr(args, key)
    world = World(args)
    lights = [(float(args.flash.split(",")[0]), float(args.flash.split(",")[1]), 55.0, 0.55)] if args.flash else []

    if args.sheet == "zoom":
        # the same place from the world view down to a few provinces: 1200 x 440 each
        cx, cy = 3250, 560
        tiles = []
        for S in (0.22, 0.45, 0.8, 1.4, 2.2, 3.5):
            w, h = 1200 / S, 440 / S
            E = 40  # snow_render reads a few px past its crop
            x0 = min(max(cx - w / 2, E), MAP_W - E - w) if w < MAP_W - 2 * E else E
            y0 = min(max(cy - h / 2, E), MAP_H - E - h) if h < MAP_H - 2 * E else E
            crop = (int(x0), int(y0), int(min(x0 + w, MAP_W - E)), int(min(y0 + h, MAP_H - E)))
            img, share = world.frame(crop, S)
            tile = np.zeros((440, 1200, 3), np.uint8)
            tile[:min(440, img.shape[0]), :min(1200, img.shape[1])] = img[:440, :1200]
            tiles.append(label(tile, f"{S} screen px per map px, cloud over {share * 100:.0f}%"))
            print(f"zoom {S}: cloud over {share * 100:.0f}%")
        sheet = np.vstack([np.hstack(tiles[0:2]), np.hstack(tiles[2:4]), np.hstack(tiles[4:6])])
        name = HERE / f"cloudvol_{args.date}_zoomsheet.png"
    elif args.sheet == "time":
        # top row: the cloud banks drifting and changing (zoomed out); bottom row: weather clouds churning in place
        tiles = []
        for S, crop in ((0.45, (2300, 200, 3189, 644)), (2.0, (3000, 400, 3200, 500))):
            for t in (0, 3, 6, 9, 12, 15):
                img, _ = world.frame(crop, S, t=float(t))
                tiles.append(label(img[:200, :400], f"{S} px/map px, t = {t} s"))
        sheet = np.vstack([np.hstack(tiles[0:6]), np.hstack(tiles[6:12])])
        name = HERE / f"cloudvol_{args.date}_timesheet.png"
    else:
        sheet, share = world.frame(tuple(int(c) for c in args.crop.split(",")), args.scale, lights=lights)
        print(f"cloud over {share * 100:.0f}% of the crop")
        name = HERE / f"cloudvol_{args.date}_{args.tag}.png"
    Image.fromarray(sheet).save(name)
    print(f"wrote {name}")


if __name__ == "__main__":
    main()
