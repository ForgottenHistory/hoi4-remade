// Hoi4Terrain.hlsl
// GAME: Clausewitz-style terrain surface for converted HOI4 map data.
//
// Technique (same idea as HOI4's pdxmap terrain pass):
//   - terrain index map: R = atlas tile (0-15) per map pixel, 255 = water; G = nearest land
//     tile; B = coast height field (smooth HOI4-style coast from the heightmap)
//   - 4x4 tile atlas (diffuse+gloss, normal+spec), manual LOD, 4 neighbour tiles blended bilinearly
//   - colormap overlay blend tints the tiles per map pixel
//   - world normal rotated by the tile normal, sun + ambient + Blinn-Phong spec
//   - water: pdxwater port: LEAN waves, refracted sea floor/tint, fresnel sky reflection, sun glint
//
// All inputs are GAME globals set from map/visual_style.json5 (textures + tuning).
// Textures loaded there (Texture2D.LoadImage) have row 0 = bottom, so map-space textures
// are sampled with the raw mesh UV (north = v 1). Engine simulation textures use 1 - v.

#ifndef HOI4_TERRAIN_INCLUDED
#define HOI4_TERRAIN_INCLUDED

#define HOI4_WATER_TILE 255

TEXTURE2D(_Hoi4TerrainIndex);
TEXTURE2D(_Hoi4AtlasDiffuse);
TEXTURE2D(_Hoi4AtlasNormal);
TEXTURE2D(_Hoi4Colormap);
TEXTURE2D(_Hoi4ColormapWater);
TEXTURE2D(_Hoi4Underwater);
TEXTURE2D(_Hoi4WorldNormal);
// D3D11 allows 16 samplers per pixel shader and the engine already uses most of them,
// so all HOI4 textures share two inline sampler states (style json5 filter/wrap are ignored).
SAMPLER(sampler_trilinear_clamp);
SAMPLER(sampler_trilinear_repeat);

float  _Hoi4TileFrequency;      // atlas tile repeats over map height (HOI4: 128)
float  _Hoi4ColormapStrength;   // HOI4: 0.75
float  _Hoi4PoliticalBlend;     // country color over terrain
float  _Hoi4SunIntensity;
float4 _Hoi4SunColor;
float4 _Hoi4SunDirection;       // direction TOWARDS the sun (xyz)
float4 _Hoi4AmbientColor;
float  _Hoi4SpecularStrength;
float  _Hoi4WaterLightness;     // HOI4: 0.5
float  _Hoi4WaterDarken;        // HOI4: 0.2
float  _Hoi4WaterFloorVisibility; // how much lit sea floor shows in shallow water (0-1)
float  _Hoi4WaterFloorRelief;   // how much bathymetry relief lights the sea floor (0 flat, 1 full)
float4 _Hoi4WaterSurfaceColor;  // optional color laid over the water (tuning aid, opacity 0 = off)
float  _Hoi4WaterSurfaceOpacity;// surface color strength in deep water (shallow water gets less)

// HOI4 pdxwater surface: LEAN-mapped animated waves, sky reflection, refraction, sun glint
TEXTURE2D(_Hoi4WaterLean1);     // LEAN maps (data, linear)
TEXTURE2D(_Hoi4WaterLean2);
TEXTURE2D(_Hoi4WaterSpec);      // wave mask per map pixel (HOI4 waterspec): 0 = calm, higher = open sea
TEXTURE2D(_Hoi4WaterSky);       // sky reflection, HOI4 cubemap baked to lat-long (top = up)
float  _Hoi4WaterWaveScale;     // wave texture repeats, x HOI4's 1000/700/534/300 per map height
float  _Hoi4WaterWaveSpeed;     // x HOI4 speed (WATER_TIME_SCALE 1/50)
float  _Hoi4WaterWaveMask;      // x the wave mask (1 = HOI4)
float  _Hoi4WaterFresnelBias;   // HOI4 0.5: sky share looking straight down, rising to 1 at grazing
float  _Hoi4WaterRefraction;    // sea floor distortion by the waves, in map pixels
float  _Hoi4WaterGlint;         // sun glint strength

// Unset globals read 0: fall back to HOI4 defaults so the map never renders black
#define HOI4_OR(v, d) ((v) > 0.0 ? (v) : (d))

struct Hoi4Surface
{
    float3 albedo;     // land diffuse (before lighting)
    float3 normal;     // world space, y up
    float gloss;
    float spec;
    float land;        // 0 = water, 1 = land (bilinear coverage, gives soft 1px coasts)
    float3 tint;       // colormap tint (the mud overlays it too)
};

// ----------------------------------------------------------------------------
// Atlas
// ----------------------------------------------------------------------------

// HOI4 sample_terrain(): tile (u,v) in a 4x4 atlas, inset by one texel at the current mip
// so bilinear filtering never bleeds into the neighbour tile. Computed top-down (like DX),
// then flipped for the bottom-up loaded PNG.
float2 Hoi4AtlasUV(uint tile, float2 tileRepeat, float mipTexels)
{
    float2 index = float2(tile % 4, tile / 4);
    float2 f = frac(tileRepeat);
    float texelsPerTile = mipTexels / 4.0;
    f *= (texelsPerTile - 1.0) / texelsPerTile;
    float2 uv = (index + f) / 4.0 + 0.5 / mipTexels;
    return float2(uv.x, 1.0 - uv.y);
}

void Hoi4SampleTile(uint tile, float2 tileRepeat, float mipTexels, float lod, out float4 diffuse, out float4 normal)
{
    float2 auv = Hoi4AtlasUV(tile, tileRepeat, mipTexels);
    diffuse = SAMPLE_TEXTURE2D_LOD(_Hoi4AtlasDiffuse, sampler_trilinear_clamp, auv, lod);
    normal = SAMPLE_TEXTURE2D_LOD(_Hoi4AtlasNormal, sampler_trilinear_clamp, auv, lod);
}

// HOI4 RotateVectorByVector: bend the world normal by a tangent-space tile normal
float3 Hoi4RotateNormal(float3 n, float3 t)
{
    float3 xaxis = normalize(cross(n, float3(0, 0, 1)));
    float3 yaxis = normalize(cross(xaxis, n));
    return xaxis * t.x + n * t.y + yaxis * t.z;
}

// HOI4 GetOverlay: overlay blend in gamma space
float3 Hoi4Overlay(float3 color, float3 overlay, float strength)
{
    float3 c = LinearToSRGB(color);
    float3 o = LinearToSRGB(overlay);
    float3 res = o < 0.5 ? (2.0 * o * c) : (1.0 - 2.0 * (1.0 - o) * (1.0 - c));
    return lerp(color, SRGBToLinear(res), strength);
}

// ----------------------------------------------------------------------------
// Surface
// ----------------------------------------------------------------------------

// Coast (HOI4 pdxwater alpha): HOI4 has no coastline geometry; its water fades out where the
// blurred heightmap reaches sea level, so the coast is a smooth height isoline, not province
// pixel steps. Terrain index B holds that height field (converter coast_channels, 8.5-10.5).
float Hoi4ShoreAlpha(float field)
{
    float height = 8.5 + field * 2.0;
    // HOI4 waterShore = saturate((height / 9.57 - 0.954) * 25): land from ~9.13 to fully at ~9.51
    return saturate((height * (1.0 / 9.57) - 0.954) * 25.0);
}

// Land coverage 0..1 at uv (bilinear coast field). Also for effects that must stay off water
// (city lights: HOI4's emissive mask is soft and spills into the sea).
float Hoi4LandFraction(float2 uv)
{
    uint w, h;
    _Hoi4TerrainIndex.GetDimensions(w, h);
    float2 p = uv * float2(w, h) - 0.5;
    int2 i0 = (int2)floor(p);
    float2 f = p - floor(p);
    int2 maxI = int2(w - 1, h - 1);

    float field[4];
    [unroll]
    for (int k = 0; k < 4; k++)
        field[k] = LOAD_TEXTURE2D(_Hoi4TerrainIndex, clamp(i0 + int2(k & 1, k >> 1), int2(0, 0), maxI)).b;
    return Hoi4ShoreAlpha(lerp(lerp(field[0], field[1], f.x), lerp(field[2], field[3], f.x), f.y));
}

Hoi4Surface Hoi4SampleSurface(float2 uv)
{
    Hoi4Surface s;

    uint w, h;
    _Hoi4TerrainIndex.GetDimensions(w, h);
    float2 size = float2(w, h);

    // 4 neighbour pixels around this point (HOI4: uv - half texel, frac(uv*size - 0.5))
    float2 p = uv * size - 0.5;
    int2 i0 = (int2)floor(p);
    float2 f = p - floor(p);
    int2 maxI = int2(w - 1, h - 1);

    // Index G = the pixel's land tile (nearest land tile on sea pixels, so the smooth coast has
    // terrain to draw where it crosses into sea provinces); B = coast height field
    uint tiles[4];
    float field[4];
    [unroll]
    for (int t = 0; t < 4; t++)
    {
        float4 texel = LOAD_TEXTURE2D(_Hoi4TerrainIndex, clamp(i0 + int2(t & 1, t >> 1), int2(0, 0), maxI));
        tiles[t] = (uint)(texel.g * 255.0 + 0.5);
        field[t] = texel.b;
    }

    float weights[4];
    weights[0] = (1 - f.x) * (1 - f.y);
    weights[1] = f.x * (1 - f.y);
    weights[2] = (1 - f.x) * f.y;
    weights[3] = f.x * f.y;

    float land = Hoi4ShoreAlpha(lerp(lerp(field[0], field[1], f.x), lerp(field[2], field[3], f.x), f.y));

    // Tile repeat in top-down orientation (matches HOI4 uv2), aspect corrected
    float2 tileRepeat = float2(uv.x * size.x / size.y, 1.0 - uv.y) * HOI4_OR(_Hoi4TileFrequency, 128.0);

    // Manual LOD (HOI4 mipmapLevel(): 512 texels per tile, biased -0.5, clamped 0-6)
    float2 dx = ddx(tileRepeat * 512.0);
    float2 dy = ddy(tileRepeat * 512.0);
    float lod = clamp(0.5 * log2(max(dot(dx, dx), dot(dy, dy))) - 0.5, 0.0, 6.0);
    float mipTexels = exp2(11.0 - lod);

    float4 diffuse = 0;
    float4 normalRaw = 0;

    bool allSame = tiles[0] == tiles[1] && tiles[0] == tiles[2] && tiles[0] == tiles[3];
    if (land > 0 && allSame)
    {
        Hoi4SampleTile(tiles[0], tileRepeat, mipTexels, lod, diffuse, normalRaw);
    }
    else if (land > 0)
    {
        [unroll]
        for (int k = 0; k < 4; k++)
        {
            float4 d, n;
            Hoi4SampleTile(tiles[k], tileRepeat, mipTexels, lod, d, n);
            diffuse += d * weights[k];
            normalRaw += n * weights[k];
        }
    }

    // Derivative-based samples stay outside the branches below
    // World normal (HOI4 HeightNormal: .rbg - 0.5, y up)
    float3 worldNormal = normalize(SAMPLE_TEXTURE2D(_Hoi4WorldNormal, sampler_trilinear_clamp, uv).rbg - 0.5);
    float3 tint = SAMPLE_TEXTURE2D(_Hoi4Colormap, sampler_trilinear_clamp, uv).rgb;

    if (land > 0)
    {
        float3 tileNormal = normalize(normalRaw.rbg - 0.5);
        s.normal = normalize(Hoi4RotateNormal(worldNormal, tileNormal));

        s.albedo = Hoi4Overlay(diffuse.rgb, tint, HOI4_OR(_Hoi4ColormapStrength, 0.75));
        s.gloss = diffuse.a;
        s.spec = normalRaw.a;
    }
    else
    {
        s.normal = worldNormal;
        s.albedo = 0;
        s.gloss = 0;
        s.spec = 0;
    }
    s.land = land;
    s.tint = tint;
    return s;
}

// ----------------------------------------------------------------------------
// Lighting
// ----------------------------------------------------------------------------

float3 Hoi4SunDir()
{
    float3 d = _Hoi4SunDirection.xyz;
    return dot(d, d) > 0 ? normalize(d) : normalize(float3(1.0, 1.0, -0.5)); // HOI4 LIGHT_DIRECTION (-1,-1,0.5), reversed
}

float3 Hoi4Light(float3 albedo, float3 normal, float3 positionWS, float gloss, float spec)
{
    float3 L = Hoi4SunDir();
    float3 V = normalize(GetCameraPositionWS() - positionWS);
    float3 H = normalize(L + V);

    float3 sun = (_Hoi4SunColor.a > 0 ? _Hoi4SunColor.rgb : float3(1, 1, 1)) * HOI4_OR(_Hoi4SunIntensity, 0.8);
    float3 ambient = _Hoi4AmbientColor.a > 0 ? _Hoi4AmbientColor.rgb : float3(0.42, 0.45, 0.5);

    float ndl = saturate(dot(normal, L));
    float specPower = lerp(8.0, 96.0, gloss);
    float specular = pow(saturate(dot(normal, H)), specPower) * spec * _Hoi4SpecularStrength;

    return albedo * (ambient + sun * ndl) + sun * specular;
}

// Snow and mud on land, ice on water
#include "Hoi4Weather.hlsl"
#include "Hoi4Fog.hlsl"

// ----------------------------------------------------------------------------
// Water
// ----------------------------------------------------------------------------

// HOI4 underwater pass: what the water surface refracts
float3 Hoi4Underwater(float2 uv, float3 positionWS, float3 worldNormal)
{
    uint w, h;
    _Hoi4TerrainIndex.GetDimensions(w, h);

    // Tiled sea floor, faded to the water tint with depth.
    // Heights in HOI4 units (heightmap 0-255 * 0.1; sea level 9.5).
    float height = SAMPLE_TEXTURE2D(_HeightmapTexture, sampler_HeightmapTexture, float2(uv.x, 1.0 - uv.y)).r * 25.5;
    float shallow = saturate((height - 5.3) / (13.0 - 5.3));

    float3 tint = SAMPLE_TEXTURE2D(_Hoi4ColormapWater, sampler_trilinear_clamp, uv).rgb * HOI4_OR(_Hoi4WaterLightness, 0.5);
    float2 floorUV = float2(uv.x, 1.0 - uv.y) * float2(w / 32.0, h / 32.0);
    float3 seaFloor = SAMPLE_TEXTURE2D(_Hoi4Underwater, sampler_trilinear_repeat, floorUV).rgb;
    // Flatten the bathymetry relief: HOI4's sea reads as a calm surface, not a lit sea floor
    float3 floorNormal = normalize(lerp(float3(0, 1, 0), worldNormal, saturate(_Hoi4WaterFloorRelief)));
    seaFloor = Hoi4Light(seaFloor, floorNormal, positionWS, 0, 0);

    float floorAmount = shallow * saturate(_Hoi4WaterFloorVisibility);
    float3 color = lerp(tint, seaFloor, floorAmount);

    // Surface color, strongest over deep water
    if (_Hoi4WaterSurfaceColor.a > 0)
        color = lerp(color, _Hoi4WaterSurfaceColor.rgb, saturate(_Hoi4WaterSurfaceOpacity) * (1.0 - floorAmount));

    return color;
}

// LEAN mapping (HOI4 standardfuncsgfx SampleLEAN / BlendLEAN): B = mean slope, M = second
// moments of the wave slopes, so the sun glint stays correct when the waves are minified.
void Hoi4SampleLean(float2 uv, out float2 B, out float3 M)
{
    float4 l1 = SAMPLE_TEXTURE2D(_Hoi4WaterLean1, sampler_trilinear_repeat, uv);
    float4 l2 = SAMPLE_TEXTURE2D(_Hoi4WaterLean2, sampler_trilinear_repeat, uv);
    const float s = 1.7;
    B = (2.0 * l2.xy - 1.0) * s;
    M = float3(l2.zw, (2.0 * l1.w - 1.0) * 0.5) * s * s;
}

void Hoi4BlendLean(float2 B1, float3 M1, float2 B2, float3 M2, out float2 B, out float3 M)
{
    // t = u = 0.5 (HOI4 always blends halves)
    B = (B1 + B2) * 0.5;
    M.x = (M1.x + M2.x) * 0.25 + 0.5 * B1.x * B2.x;
    M.y = (M1.y + M2.y) * 0.25 + 0.5 * B1.y * B2.y;
    M.z = (M1.z + M2.z) * 0.25 + 0.25 * (B1.x * B2.y + B1.y * B2.x);
}

// HOI4 SampleWater: 4 LEAN layers at different scales scrolling in different directions.
// waveUV: top-down map coordinates in map heights (square texels).
void Hoi4SampleWaves(float2 waveUV, float time, out float2 B, out float3 M)
{
    static const float2 scale[4] = { float2(900, 1000), float2(665, 735), float2(560.7, 507.3), float2(300, 300) };
    static const float2 speed[4] = { float2(1.0, 0.1), float2(0.1, 2.0), float2(-0.2, -2.0), float2(-1.0, -0.1) };
    float waveScale = HOI4_OR(_Hoi4WaterWaveScale, 0.36);

    float2 b[4];
    float3 m[4];
    [unroll]
    for (int i = 0; i < 4; i++)
        Hoi4SampleLean(waveUV * scale[i] * waveScale + time * speed[i], b[i], m[i]);

    float2 B1, B2;
    float3 M1, M2;
    Hoi4BlendLean(b[0], m[0], b[1], m[1], B1, M1);
    Hoi4BlendLean(b[2], m[2], b[3], m[3], B2, M2);
    Hoi4BlendLean(B1, M1, B2, M2, B, M);
}

// Sky reflection: lat-long lookup (u = 0.5 looking north (+z), top = straight up)
float3 Hoi4SkyReflection(float3 r)
{
    r.y = abs(r.y); // waves can tilt the reflection below the horizon; the sky has no "below"
    float2 skyUV = float2(atan2(r.x, r.z) * (0.5 / PI) + 0.5, asin(saturate(r.y)) * (1.0 / PI) + 0.5);
    // LOD 0: atan2 wraps at the back seam, which would make derivative mip selection blow up
    return SAMPLE_TEXTURE2D_LOD(_Hoi4WaterSky, sampler_trilinear_repeat, skyUV, 0).rgb;
}

// HOI4 pdxwater: refracted underwater + fresnel sky reflection, darkened, lit with the wave
// normal, plus a LEAN sun glint, and HOI4's sea ice near snowy coasts (Hoi4ApplyIce).
float3 Hoi4Water(float2 uv, float3 positionWS, float3 worldNormal)
{
    uint w, h;
    _Hoi4TerrainIndex.GetDimensions(w, h);
    float2 uvTop = float2(uv.x, 1.0 - uv.y);

    // Waves, in top-down map coordinates like HOI4
    float time = _Time.y * (1.0 / 50.0) * HOI4_OR(_Hoi4WaterWaveSpeed, 1.0);
    float2 B;
    float3 M;
    Hoi4SampleWaves(float2(uvTop.x * w / h, uvTop.y), time, B, M);

    // Wave mask flattens the waves in sheltered water (HOI4 vSpecMap)
    float mask = saturate(SAMPLE_TEXTURE2D(_Hoi4WaterSpec, sampler_trilinear_clamp, uv).r * HOI4_OR(_Hoi4WaterWaveMask, 1.0));
    float3 n = normalize(float3(B.x, 1.0, B.y));
    n.y += 1.0 - mask;
    n.xz *= mask;
    n = normalize(n);
    B *= mask;
    M *= mask * mask;
    // HOI4 uses (B.x, 1, B.y) as the world normal as is (both have z = north, y up)
    float3 normal = n;

    float3 V = normalize(GetCameraPositionWS() - positionWS);
    float3 L = Hoi4SunDir();

    // Refraction: the waves bend the view of the underwater pass
    float2 distortion = n.xz * _Hoi4WaterRefraction / float2(w, h);
    float3 refracted = Hoi4Underwater(uv + distortion, positionWS, worldNormal);

    // Sky reflection with HOI4's fresnel (bias, power 10 on half the cosine)
    float3 reflected = Hoi4SkyReflection(reflect(-V, normal));
    float bias = HOI4_OR(_Hoi4WaterFresnelBias, 0.5);
    float cosHalf = saturate(dot(V, normal)) * 0.5;
    float fresnel = saturate(bias + (1.0 - bias) * pow(1.0 - cosHalf, 10.0));
    float3 color = lerp(refracted, reflected, fresnel);

    // HOI4 ApplyIce: frozen water near snowy land, lit with the ice normal and without the wave glint
    float ice;
    color = Hoi4ApplyIce(color, uv, normal, ice);

    // HOI4 darkness offset, then lit with the wave normal
    color = lerp(color, 0, _Hoi4WaterDarken * (1.0 - ice));
    color = Hoi4Light(color, normal, positionWS, 0, 0);

    // LEAN glint: Gaussian slope distribution around the mean slope B
    float3 Hh = normalize(L + V);
    float2 hWave = Hh.xz / max(Hh.y, 1e-3) - B;
    float3 sigma = M - float3(B * B, B.x * B.y);
    float det = sigma.x * sigma.y - sigma.z * sigma.z;
    float e = hWave.x * hWave.x * sigma.y + hWave.y * hWave.y * sigma.x - 2.0 * hWave.x * hWave.y * sigma.z;
    float spec = det <= 1e-12 ? 0.0 : exp(-0.5 * e / det) * rsqrt(det);
    float3 sun = (_Hoi4SunColor.a > 0 ? _Hoi4SunColor.rgb : float3(1, 1, 1)) * HOI4_OR(_Hoi4SunIntensity, 0.8);
    // spec peaks in the hundreds to thousands; map it to 0-1 (HOI4 tames it in its lighting model)
    color += sun * (1.0 - exp(-spec * 0.02)) * _Hoi4WaterGlint * (1.0 - ice);

    return color;
}

// Full lit HOI4 pixel. countryColor.a = how much country color to paint over the land.
float4 Hoi4Render(float2 uv, float3 positionWS, float4 countryColor)
{
    Hoi4Surface s = Hoi4SampleSurface(uv);
    Hoi4ApplyWeather(s, uv, s.tint);

    // Water uses derivative-based samples, so it is not branched on s.land
    float3 water = Hoi4Water(uv, positionWS, s.normal);

    float3 land = 0;
    if (s.land > 0)
    {
        float3 albedo = lerp(s.albedo, countryColor.rgb, countryColor.a);
        land = Hoi4Light(albedo, s.normal, positionWS, s.gloss, s.spec * (1 - countryColor.a));
    }

    uint mw, mh;
    _Hoi4TerrainIndex.GetDimensions(mw, mh);
    return float4(Hoi4FogGround(lerp(water, land, s.land), uv, float2(mw, mh)), 1);
}

#endif // HOI4_TERRAIN_INCLUDED
