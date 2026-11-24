"""Prototype: HOI4's snow cover on the terrain (pdxmap.shader ApplySnow, standardfuncsgfx.fxh), before the HLSL port.

Renders a crop of our terrain (atlas tiles + colormap overlay + world/tile normals + our sun, like Hoi4Terrain.hlsl)
with snow from a weather.py state dump:

    python Tools/prototypes/weather/weather.py --dump --norender --dates 1936-02-15 ...
    python Tools/prototypes/weather/snow_render.py --date 1936-02-15 [--crop x0,y0,x1,y1] [--scale 2] [--cam 0..1]

HOI4's ApplySnow, per pixel (positions in map pixels, height in HOI4 units = heightmap/255 * 25.5):
    isSnow     = the province's snow (SnowMudData g/b, faded between days), 0..1
    snowFade   = saturate(height - 3)                         (SNOW_START_HEIGHT: everywhere above the sea floor)
    normalFade = saturate(saturate(normal.y - 0.7) * 5)       (none on cliffs)
    noise      = citylights_rgb_snowmask_a alpha at pos * 0.06
    transp     = saturate(noise + saturate(height - 11) * (normal.y > 0.9) * isSnow)   (flat high ground fills in)
    snow       = saturate(saturate(transp - (1 - isSnow)) * 5)
    frost      = saturate(saturate(transp + 0.5) - (1 - isSnow))
    opacity    = 0.95 zoomed in .. 0.2 zoomed out (camera height 50..300)
    alpha      = saturate(saturate(snow + frost) * snowFade * normalFade * saturate(isSnow * 2.25) * opacity)
    colour     = lerp(colour, snowTex.a * (0.46, 0.48, 0.69), (1 - spec) * saturate(alpha + 0.4 * smoothstep(0, 1, isSnow)))
    normal     = lerp(normal, snow normal (snowTex.rbg) bent onto it, alpha)
How the engine turns a province's snow amount into isSnow is not in the shaders: --full sets the amount that reads 1.
And HOI4's GetMudColor after it (mud: the province's mud flag, 0/1):
    noise   = lerp(1, (0.5 + noise alpha at pos * 0.01) * 0.5, zoom out 0..1)   (zoomed out, mud breaks up)
    mud     = saturate(mud * noise)
    colour  = lerp(colour, overlay(mud texture rgb, colormap tint, 0.5), mud)    (mud_diffuse_rgb_gloss_a at pos * 0.09)
    normal, gloss, spec -> the mud texture's (mud_normal_rgb_spec_a) by mud
And HOI4's ApplyIce (pdxwater.shader) on water: the snow there (SnowMudData is filtered, so land snow bleeds onto
the water; here the snowiest land within reach x (1 - distance from the smooth coast / --icereach)):
    ice = saturate((saturate(isSnow * 8) * ice noise (pos * 0.1) * zoom-in opacity * north of 0.74 map height - 0.3) * 10)
    colour -> ICE_COLOR (0.5, 0.6, 0.9) * ice_diffuse.a, normal -> ice_diffuse.rbg (pos * 0.1)
Writes snow_<date>[_<tag>].png next to this script (gitignored).
"""
import argparse
import json
import sys
from pathlib import Path

import numpy as np
from PIL import Image
from scipy.ndimage import gaussian_filter

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "supply"))
from render_rails import DEFAULT_HOI4, province_ids  # noqa: E402

HERE = Path(__file__).resolve().parent
SNOW_COLOR = np.array([0.46, 0.48, 0.69])
SUN = np.array([1.0, 1.0, -0.5]) / np.linalg.norm([1.0, 1.0, -0.5])
SUN_INTENSITY, AMBIENT, SPEC_STRENGTH = 0.8, np.array([0.42, 0.45, 0.5]), 0.3


def srgb_to_linear(c):
    return np.where(c <= 0.04045, c / 12.92, ((c + 0.055) / 1.055) ** 2.4)


def linear_to_srgb(c):
    c = np.clip(c, 0, 1)
    return np.where(c <= 0.0031308, c * 12.92, 1.055 * c ** (1 / 2.4) - 0.055)


def overlay(color, over, strength):
    c, o = linear_to_srgb(color), linear_to_srgb(over)
    res = np.where(o < 0.5, 2 * o * c, 1 - 2 * (1 - o) * (1 - c))
    return color + (srgb_to_linear(res) - color) * strength


def rotate(n, t):
    """HOI4 RotateVectorByVector: bend world normal n by tangent-space t (both (..., 3), y up)."""
    x = np.cross(n, np.array([0.0, 0.0, 1.0]))
    x /= np.linalg.norm(x, axis=-1, keepdims=True) + 1e-9
    y = np.cross(x, n)
    y /= np.linalg.norm(y, axis=-1, keepdims=True) + 1e-9
    return x * t[..., :1] + n * t[..., 1:2] + y * t[..., 2:3]


def normalize(v):
    return v / (np.linalg.norm(v, axis=-1, keepdims=True) + 1e-9)


def bilinear(img, x, y):
    """img (H, W, C) float, x/y in pixel units (pixel centres at +0.5), clamped."""
    h, w = img.shape[:2]
    x, y = x - 0.5, y - 0.5
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    fx, fy = (x - x0)[..., None], (y - y0)[..., None]
    x0c, x1c = np.clip(x0, 0, w - 1), np.clip(x0 + 1, 0, w - 1)
    y0c, y1c = np.clip(y0, 0, h - 1), np.clip(y0 + 1, 0, h - 1)
    return ((img[y0c, x0c] * (1 - fx) + img[y0c, x1c] * fx) * (1 - fy)
            + (img[y1c, x0c] * (1 - fx) + img[y1c, x1c] * fx) * fy)


def wrap(img, u, v):
    """Repeat-sample img (H, W, C) at uv (tiles), bilinear."""
    h, w = img.shape[:2]
    return bilinear(np.pad(img, ((0, 1), (0, 1), (0, 0)), mode="wrap"), (u % 1) * w + 0.5, (v % 1) * h + 0.5)


LAND = None


def land_like(p):
    return LAND is None or int(p) in LAND


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--hoi4", default=DEFAULT_HOI4)
    ap.add_argument("--data", default="Hoi4Data")
    ap.add_argument("--date", default="1936-02-15")
    ap.add_argument("--crop", default="3000,280,3600,560", help="map pixels x0,y0,x1,y1 (top-down)")
    ap.add_argument("--scale", type=float, default=2.0, help="output pixels per map pixel")
    ap.add_argument("--cam", type=float, default=0.0, help="0 zoomed in (snow opacity 0.95) .. 1 zoomed out (0.2)")
    ap.add_argument("--full", type=float, default=40.0, help="snow amount that reads as full snow (isSnow 1)")
    ap.add_argument("--blur", type=float, default=1.5, help="SnowMudData smoothing across province edges, map px")
    ap.add_argument("--nosnow", action="store_true")
    ap.add_argument("--nomud", action="store_true")
    ap.add_argument("--icepow", type=float, default=3.0, help="falloff power: higher = the bled snow thins out sooner, a raggeder fringe")
    ap.add_argument("--icebright", type=float, default=1.5, help="x ICE_COLOR")
    ap.add_argument("--icereach", type=float, default=4.0, help="map px from the smooth coast the bled snow reaches")
    ap.add_argument("--icezoom", type=float, default=0.0, help="ice opacity zoom 0 in .. 1 out (HOI4 hides ice zoomed out)")
    ap.add_argument("--mudstrength", type=float, default=1.0, help="mud value a muddy province reads as (HOI4's scale is not in its shaders)")
    ap.add_argument("--tag", default="")
    args = ap.parse_args()
    hoi4, data = Path(args.hoi4), Path(args.data)
    x0, y0, x1, y1 = map(int, args.crop.split(","))
    S = args.scale
    W, H = int((x1 - x0) * S), int((y1 - y0) * S)

    # output pixel -> map position
    mx, my = np.meshgrid(x0 + (np.arange(W) + 0.5) / S, y0 + (np.arange(H) + 0.5) / S)
    mz = 2048 - my  # HOI4 world z (bottom-up)

    # ---- terrain surface (Hoi4SampleSurface)
    index4 = np.asarray(Image.open(data / "gfx" / "map" / "terrain_index.png").convert("RGBA"))
    index = index4[..., :3]
    pad = 2
    sub = index[y0 - pad:y1 + pad, x0 - pad:x1 + pad].astype(np.float32) / 255
    px, py = mx - x0 + pad - 0.5, my - y0 + pad - 0.5
    ix, iy = np.floor(px).astype(int), np.floor(py).astype(int)
    fx, fy = px - ix, py - iy
    weights = [(1 - fx) * (1 - fy), fx * (1 - fy), (1 - fx) * fy, fx * fy]
    offs = [(0, 0), (1, 0), (0, 1), (1, 1)]
    field = sum(w * sub[iy + dy, ix + dx, 2] for w, (dx, dy) in zip(weights, offs))
    land = np.clip(((8.5 + field * 2.0) / 9.57 - 0.954) * 25.0, 0, 1)

    texels_per_out = 512 / 16 / S  # atlas texels per output pixel (a tile spans 16 map px)
    mip = max(0, int(np.round(np.log2(max(texels_per_out, 1)))))
    tile_px = 512 >> mip
    atlas_d = np.asarray(Image.open(data / "gfx" / "map" / "atlas_diffuse.png").convert("RGBA")
                         .resize((tile_px * 4,) * 2, Image.LANCZOS)).astype(np.float32) / 255
    atlas_n = np.asarray(Image.open(data / "gfx" / "map" / "atlas_normal.png").convert("RGBA")
                         .resize((tile_px * 4,) * 2, Image.LANCZOS)).astype(np.float32) / 255
    tu, tv = (mx / 16) % 1, (my / 16) % 1
    diffuse = np.zeros((H, W, 4), np.float32)
    nraw = np.zeros((H, W, 4), np.float32)
    for w, (dx, dy) in zip(weights, offs):
        tile = np.round(sub[iy + dy, ix + dx, 1] * 255).astype(int)
        ax = ((tile % 4) + tu * (tile_px - 1) / tile_px) * tile_px + 0.5
        ay = ((tile // 4) + tv * (tile_px - 1) / tile_px) * tile_px + 0.5
        diffuse += w[..., None] * bilinear(atlas_d, ax, ay)
        nraw += w[..., None] * bilinear(atlas_n, ax, ay)

    cmap = np.asarray(Image.open(data / "gfx" / "map" / "colormap.png").convert("RGB")).astype(np.float32) / 255
    wn_img = np.asarray(Image.open(data / "gfx" / "map" / "world_normal.png").convert("RGB")).astype(np.float32) / 255
    tint = srgb_to_linear(bilinear(cmap, mx / 2, my / 2))
    wn = normalize(bilinear(wn_img, mx / 2, my / 2)[..., [0, 2, 1]] - 0.5)
    normal = normalize(rotate(wn, normalize(nraw[..., [0, 2, 1]] - 0.5)))
    albedo = overlay(srgb_to_linear(diffuse[..., :3]), tint, 0.75)
    gloss, spec = diffuse[..., 3], nraw[..., 3]

    hm = np.asarray(Image.open(data / "map" / "heightmap.png").convert("L")).astype(np.float32) / 255
    height = bilinear(hm[..., None], mx, my)[..., 0] * 25.5

    # ---- snow (ApplySnow)
    if not args.nosnow:
        state = {int(k): v for k, v in json.loads((HERE / f"state_{args.date}.json").read_text()).items()}
        global LAND
        LAND = set(state)  # the dump has land provinces only
        ids = province_ids(hoi4)[y0 - 8:y1 + 8, x0 - 8:x1 + 8]
        uniq, inv = np.unique(ids, return_inverse=True)
        amount = np.array([state.get(int(p), [0])[0] for p in uniq], np.float32)[inv].reshape(ids.shape)
        is_map = gaussian_filter(np.clip(amount / args.full, 0, 1), args.blur) if args.blur > 0 else np.clip(amount / args.full, 0, 1)
        is_snow = bilinear(is_map[..., None], mx - x0 + 8, my - y0 + 8)[..., 0]

        snow_tex = np.asarray(Image.open(hoi4 / "map" / "terrain" / "snow_normal_rgb_diffuse_a.dds").convert("RGBA")).astype(np.float32) / 255
        noise_tex = np.asarray(Image.open(hoi4 / "map" / "terrain" / "citylights_rgb_snowmask_a_0.dds").convert("RGBA")).astype(np.float32)[..., 3:] / 255
        if S < 4:  # rough mip for the minified textures
            k = int(4 / S)
            snow_tex = np.asarray(Image.fromarray((snow_tex * 255).astype(np.uint8)).resize((512 // k,) * 2, Image.LANCZOS)).astype(np.float32) / 255
        st = wrap(snow_tex, mx * 0.05, mz * 0.05)
        noise = wrap(noise_tex, mx * 0.06, mz * 0.06)[..., 0]

        snow_fade = np.clip(height - 3.0, 0, 1)
        normal_fade = np.clip(np.clip(normal[..., 1] - 0.7, 0, 1) * 5.0, 0, 1)
        transp = noise + np.clip(height - 11.0, 0, 1) * np.clip((normal[..., 1] - 0.9) * 1000, 0, 1) * is_snow
        transp = np.clip(transp, 0, 1)
        snow = np.clip(np.clip(transp - (1 - is_snow), 0, 1) * 5.0, 0, 1)
        frost = np.clip(np.clip(transp + 0.5, 0, 1) - (1 - is_snow), 0, 1)
        opacity = 0.95 + args.cam * (0.2 - 0.95)
        alpha = np.clip(np.clip(snow + frost, 0, 1) * snow_fade * normal_fade * np.clip(is_snow * 2.25, 0, 1) * opacity, 0, 1)
        min_snow = is_snow * is_snow * (3 - 2 * is_snow)
        k = ((1 - spec) * np.clip(alpha + 0.4 * min_snow, 0, 1))[..., None]
        albedo = albedo + (st[..., 3:] * SNOW_COLOR - albedo) * k
        sn = normalize(rotate(normal, normalize(st[..., [0, 2, 1]] - 0.5)))
        normal = normalize(normal + (sn - normal) * alpha[..., None])
        gloss = gloss + st[..., 3] * alpha * 0.2
        print(f"{args.date}: land isSnow mean {is_snow[land > 0.5].mean():.2f}, cover (alpha>0.5) {np.mean(alpha[land > 0.5] > 0.5) * 100:.0f}%")

    # ---- mud (GetMudColor)
    if not args.nomud:
        state = {int(k): v for k, v in json.loads((HERE / f"state_{args.date}.json").read_text()).items()}
        ids = province_ids(hoi4)[y0 - 8:y1 + 8, x0 - 8:x1 + 8]
        uniq, inv = np.unique(ids, return_inverse=True)
        # frozen mud does not count (Hoi4Weather.IsMud: temperature above -1)
        flag = np.array([float(state.get(int(p), [0, 0, 0, 0])[2] and state[int(p)][3] > -1) for p in uniq], np.float32)
        mud_map = flag[inv].reshape(ids.shape)
        if args.blur > 0:
            mud_map = gaussian_filter(mud_map, args.blur)
        mud_v = bilinear(mud_map[..., None], mx - x0 + 8, my - y0 + 8)[..., 0]
        noise_tex = np.asarray(Image.open(hoi4 / "map" / "terrain" / "citylights_rgb_snowmask_a_0.dds").convert("RGBA")).astype(np.float32)[..., 3:] / 255
        mnoise = wrap(noise_tex, mx * 0.01, mz * 0.01)[..., 0]
        mud = np.clip(mud_v * args.mudstrength * (1 + ((0.5 + mnoise) * 0.5 - 1) * args.cam), 0, 1)
        md = np.asarray(Image.open(hoi4 / "map" / "terrain" / "mud_diffuse_rgb_gloss_a_0.dds").convert("RGBA")).astype(np.float32) / 255
        mn = np.asarray(Image.open(hoi4 / "map" / "terrain" / "mud_normal_rgb_spec_a_0.dds").convert("RGBA")).astype(np.float32) / 255
        if S < 8:
            k = int(8 / S)
            md = np.asarray(Image.fromarray((md * 255).astype(np.uint8)).resize((1024 // k,) * 2, Image.LANCZOS)).astype(np.float32) / 255
            mn = np.asarray(Image.fromarray((mn * 255).astype(np.uint8)).resize((1024 // k,) * 2, Image.LANCZOS)).astype(np.float32) / 255
        mds, mns = wrap(md, mx * 0.09, mz * 0.09), wrap(mn, mx * 0.09, mz * 0.09)
        mnorm = normalize(rotate(normal, normalize(mns[..., [0, 2, 1]] - 0.5)))
        normal = normalize(normal + (mnorm - normal) * mud[..., None])
        gloss = gloss + (mds[..., 3] - gloss) * mud
        spec = spec + (mns[..., 3] - spec) * mud
        mix = overlay(srgb_to_linear(mds[..., :3]), tint, 0.5)
        albedo = albedo + (mix - albedo) * mud[..., None]
        print(f"{args.date}: land in mud {np.mean(mud_v[land > 0.5] > 0.5) * 100:.0f}%")

    # ---- light (Hoi4Light, camera straight above)
    ndl = np.clip(normal @ SUN, 0, None)[..., None]
    hv = normalize(SUN + np.array([0.0, 1.0, 0.0]))
    spec_power = 8 + (96 - 8) * gloss
    specular = (np.clip(normal @ hv, 0, None) ** spec_power * spec * SPEC_STRENGTH)[..., None]
    lit = albedo * (AMBIENT + SUN_INTENSITY * ndl) + SUN_INTENSITY * specular

    water = np.asarray(Image.open(data / "gfx" / "map" / "colormap_water.png").convert("RGB")).astype(np.float32) / 255
    sea = srgb_to_linear(bilinear(water, mx / 2, my / 2)) * 0.5 * (AMBIENT + SUN_INTENSITY * SUN[1])
    if not args.nosnow:
        # ---- ice (ApplyIce): HOI4's bled land snow = the snowiest land nearby x a falloff over the distance from the
        # smooth coast (terrain index A, coast_distance in hoi4_convert.py), so the ice edge follows the coastline
        from scipy.ndimage import maximum_filter
        land_snow = np.array([0.0 if not land_like(p) else state.get(int(p), [0])[0] for p in uniq], np.float32)[inv].reshape(ids.shape)
        r = int(args.icereach + 3)
        near = maximum_filter(land_snow, footprint=np.hypot(*np.mgrid[-r:r + 1, -r:r + 1]) <= r)
        near = bilinear(near[..., None], mx - x0 + 8, my - y0 + 8)[..., 0]
        dist_map = index4[y0 - pad:y1 + pad, x0 - pad:x1 + pad, 3].astype(np.float32) / 16
        dist = bilinear(dist_map[..., None], mx - x0 + pad, my - y0 + pad)[..., 0]
        ice_snow = np.clip(near / args.full, 0, 1) * np.clip(1 - dist / args.icereach, 0, 1) ** args.icepow
        ice_d = np.asarray(Image.open(hoi4 / "map" / "terrain" / "ice_diffuse.dds").convert("RGBA")).astype(np.float32) / 255
        ice_n = np.asarray(Image.open(hoi4 / "map" / "terrain" / "ice_noise_0.dds").convert("RGBA")).astype(np.float32)[..., :1] / 255
        idf = wrap(ice_d, mx * 0.1, mz * 0.1)
        inz = wrap(ice_n, (mx + 0.5) * 0.1, (mz + 0.5) * 0.1)[..., 0]
        limit = np.clip(np.clip(mz / 2048 - 0.74, 0, 1) * 800, 0, 1)
        fade = np.clip(ice_snow * 8, 0, None) * inz * (1 - args.icezoom) * limit
        fade = np.clip((fade - 0.3) * 10, 0, 1)
        inorm = normalize(idf[..., [0, 2, 1]] - 0.5)
        ice_col = np.array([0.5, 0.6, 0.9]) * idf[..., 3:] * args.icebright
        ndl_i = np.clip(inorm @ SUN, 0, None)[..., None]
        ice_lit = ice_col * (AMBIENT + SUN_INTENSITY * ndl_i)
        sea = sea + (ice_lit - sea) * fade[..., None]
        print(f"{args.date}: water under ice {np.mean(fade[land < 0.5] > 0.5) * 100:.0f}%")
    out = lit * land[..., None] + sea * (1 - land[..., None])
    name = f"snow_{args.date}{'_' + args.tag if args.tag else ''}.png"
    Image.fromarray((linear_to_srgb(out) * 255 + 0.5).astype(np.uint8)).save(HERE / name)
    print("wrote", HERE / name)


if __name__ == "__main__":
    main()
