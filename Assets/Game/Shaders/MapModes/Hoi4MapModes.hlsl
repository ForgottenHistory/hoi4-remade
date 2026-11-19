// Hoi4MapModes.hlsl
// GAME: map mode dispatcher. Every mode is drawn over the HOI4 terrain surface:
//   0  political  - country color painted over terrain (unowned land/sea show terrain/water)
//   1  terrain    - plain terrain
//   2+ custom     - province palette color painted over terrain (alpha 0 = terrain only)
// Lighting is done inside Hoi4Render, so the engine lighting stage is disabled here.

#ifndef HOI4_MAP_MODES_INCLUDED
#define HOI4_MAP_MODES_INCLUDED

#include "Assets/Archon-Engine/Shaders/Includes/MapModeCommon.hlsl"
#include "Hoi4Terrain.hlsl"

// Country color with bilinear blending between the 4 neighbour pixels' owners (soft 1px edges)
float4 Hoi4CountryColor(float2 uv)
{
    float2 correctedUV = float2(uv.x, 1.0 - uv.y);
    uint w, h;
    _ProvinceOwnerTexture.GetDimensions(w, h);
    float2 p = correctedUV * float2(w, h) - 0.5;
    int2 i0 = (int2)floor(p);
    float2 f = p - floor(p);
    int2 maxI = int2(w - 1, h - 1);

    float4 c[4];
    int2 offsets[4] = { int2(0, 0), int2(1, 0), int2(0, 1), int2(1, 1) };
    [unroll]
    for (int k = 0; k < 4; k++)
    {
        uint owner = (uint)(LOAD_TEXTURE2D(_ProvinceOwnerTexture, clamp(i0 + offsets[k], int2(0, 0), maxI)).r + 0.5);
        // Unowned contributes no color (alpha 0), so it blends into plain terrain
        c[k] = owner == 0 ? float4(0, 0, 0, 0)
                          : float4(SAMPLE_TEXTURE2D_LOD(_CountryColorPalette, sampler_CountryColorPalette, GetColorUV(owner), 0).rgb, 1);
    }
    float4 color = lerp(lerp(c[0], c[1], f.x), lerp(c[2], c[3], f.x), f.y);
    if (color.a > 0) color.rgb /= color.a; // un-premultiply so edges keep the hue
    return color;
}

float4 Hoi4PaletteColor(uint provinceID)
{
    int rowsPerMode = (_MaxProvinceID + 255) / 256;
    int col = provinceID % 256;
    int row = (provinceID / 256) + (_CustomMapModeIndex * rowsPerMode);

    float2 paletteSize;
    _ProvincePaletteTexture.GetDimensions(paletteSize.x, paletteSize.y);
    float2 paletteUV = float2((col + 0.5) / paletteSize.x, (row + 0.5) / paletteSize.y);
    return SAMPLE_TEXTURE2D_LOD(_ProvincePaletteTexture, sampler_ProvincePaletteTexture, paletteUV, 0);
}

float4 RenderMapMode(int mapMode, uint provinceID, float2 uv, float3 positionWS)
{
    float blend = HOI4_OR(_Hoi4PoliticalBlend, 0.65);

    if (mapMode == 1)
    {
        return Hoi4Render(uv, positionWS, 0);
    }
    if (mapMode >= 2 && mapMode < 100)
    {
        float4 palette = Hoi4PaletteColor(provinceID);
        return Hoi4Render(uv, positionWS, float4(palette.rgb, palette.a < 0.01 ? 0 : blend));
    }

    // Political (0) and fallback
    float4 country = Hoi4CountryColor(uv);
    country.rgb = lerp(dot(country.rgb, float3(0.299, 0.587, 0.114)).xxx, country.rgb, _CountryColorSaturation);
    return Hoi4Render(uv, positionWS, float4(country.rgb, country.a * blend));
}

// Pixels outside every province (ID 0) still get HOI4 water
#define ARCHON_OVERRIDE_OCEAN
float4 ArchonOcean(ArchonMapContext ctx)
{
    return Hoi4Render(ctx.uv, ctx.positionWS, 0);
}

// Hoi4Render already lit the pixel
#define ARCHON_OVERRIDE_LIGHTING
float4 ArchonLighting(float4 color, ArchonMapContext ctx)
{
    return color;
}

#endif // HOI4_MAP_MODES_INCLUDED
