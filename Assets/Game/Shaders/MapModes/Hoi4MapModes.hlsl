// Hoi4MapModes.hlsl
// GAME: map mode dispatcher. Every mode is drawn over the HOI4 terrain surface:
//   0  political  - country color painted over terrain (unowned land/sea show terrain/water)
//   1  terrain    - plain terrain
//   2+ custom     - province palette color painted over terrain (alpha 0 = terrain only)
// Lighting is done inside Hoi4Render, so the engine lighting stage is disabled here.

#ifndef HOI4_MAP_MODES_INCLUDED
#define HOI4_MAP_MODES_INCLUDED

#include "Assets/Archon-Engine/Shaders/Includes/MapModeCommon.hlsl"
#include "Assets/Archon-Engine/Shaders/Includes/DayNight.hlsl"
#include "Hoi4Terrain.hlsl"

// City lights at night (HOI4: citylights texture tiled over the map, masked by colormap alpha)
TEXTURE2D(_Hoi4CityLights);
float _Hoi4CityLightsIntensity;  // HOI4 CITY_LIGHTS_INTENSITY 5.5
float _Hoi4CityLightsTiling;     // repeats per map pixel, HOI4 CITY_LIGHTS_TILING 0.09103

float _Hoi4CountryEdgeSoftness; // 1 = gradient over ~3-4 map px between countries, 0 = near-sharp line

// Diplomatic mode (shader mode 5) reuses the political country blend, but with per-country colors
// from this 4096x1 texture (rgb = color, a = 0 for countries drawn as plain terrain)
TEXTURE2D(_Hoi4DiplomacyColors);
float _Hoi4DiplomacyActive;

float4 Hoi4OwnerColor(uint owner)
{
    if (_Hoi4DiplomacyActive > 0.5)
    {
        float4 d = LOAD_TEXTURE2D(_Hoi4DiplomacyColors, int2(min(owner, 4095u), 0));
        return float4(d.rgb * d.a, d.a); // premultiplied, like the political colors
    }
    // Unowned contributes no color (alpha 0), so it blends into plain terrain
    return owner == 0 ? float4(0, 0, 0, 0)
                      : float4(SAMPLE_TEXTURE2D_LOD(_CountryColorPalette, sampler_CountryColorPalette, GetColorUV(owner), 0).rgb, 1);
}

// Country color with smooth edges (prototype: Tools/prototypes/country_fill). Each country's
// share of a 4x4 pixel window (tent weights, ~1.9 px radius) is its blend weight, so the colors
// mix on smooth curves instead of pixel steps, also where 3-4 countries meet.
// _Hoi4CountryEdgeSoftness 1 = plain weighted blend (gradient over ~3-4 map px); lower values
// sharpen the shares (power up to 8) towards a line between countries.
#define HOI4_COUNTRY_SLOTS 4
float4 Hoi4CountryColor(float2 uv)
{
    float2 correctedUV = float2(uv.x, 1.0 - uv.y);
    uint w, h;
    _ProvinceOwnerTexture.GetDimensions(w, h);
    float2 q = correctedUV * float2(w, h) - 0.5;   // pixel-center coordinates
    int2 i0 = (int2)floor(q);
    int2 maxI = int2(w - 1, h - 1);

    // Distinct owners in the window and their summed weights
    uint owners[HOI4_COUNTRY_SLOTS] = { 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF, 0xFFFFFFFF };
    float shares[HOI4_COUNTRY_SLOTS] = { 0, 0, 0, 0 };
    [unroll]
    for (int oy = -1; oy <= 2; oy++)
    {
        [unroll]
        for (int ox = -1; ox <= 2; ox++)
        {
            int2 pixel = i0 + int2(ox, oy);
            float2 tent = saturate(1.5 - abs((float2)pixel - q) / 1.25);
            float weight = tent.x * tent.y;
            if (weight <= 0) continue;
            uint owner = (uint)(LOAD_TEXTURE2D(_ProvinceOwnerTexture, clamp(pixel, int2(0, 0), maxI)).r + 0.5);

            bool placed = false;
            [unroll]
            for (int k = 0; k < HOI4_COUNTRY_SLOTS; k++)
            {
                if (!placed && (owners[k] == owner || owners[k] == 0xFFFFFFFF))
                {
                    owners[k] = owner;
                    shares[k] += weight;
                    placed = true;
                }
            }
            // A 5th owner in one window is dropped (its weight just renormalizes away)
        }
    }

    float power = exp2(3.0 * (1.0 - saturate(_Hoi4CountryEdgeSoftness)));
    float4 color = 0;
    float total = 0;
    [unroll]
    for (int k = 0; k < HOI4_COUNTRY_SLOTS; k++)
    {
        if (owners[k] == 0xFFFFFFFF) continue;
        float share = pow(shares[k], power);
        color += Hoi4OwnerColor(owners[k]) * share;   // premultiplied: unowned adds alpha 0
        total += share;
    }
    color /= max(total, 1e-6);
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

// HOI4 "gradient borders": country color is strongest along the country border and fades
// towards the interior; zooming out fills the whole country. Uses the engine country-border
// distance field (_DistanceFieldBorderTexture.r, 0-1 over 32 map pixels).
float4 _Hoi4Gradient;          // x = band thickness (map px), y = edge blend, z = interior blend, w = rim darkening
float4 _Hoi4GradientFillZoom;  // x..y = map px per screen px where the fill goes from band-only to full

float Hoi4CountryBlend(float2 uv)
{
    float thickness = HOI4_OR(_Hoi4Gradient.x, 10.0);
    float edgeBlend = HOI4_OR(_Hoi4Gradient.y, 0.75);
    float interiorBlend = _Hoi4Gradient.z;

    float dist = SAMPLE_TEXTURE2D(_DistanceFieldBorderTexture, sampler_DistanceFieldBorderTexture,
                                  float2(uv.x, 1.0 - uv.y)).r * 32.0;

    // Zoom: map pixels per screen pixel (small = zoomed in)
    uint w, h;
    _ProvinceOwnerTexture.GetDimensions(w, h);
    float mapPxPerScreenPx = length(fwidth(uv * float2(w, h)));
    float2 fillZoom = _Hoi4GradientFillZoom.y > _Hoi4GradientFillZoom.x ? _Hoi4GradientFillZoom.xy : float2(0.6, 1.6);
    float fill = smoothstep(fillZoom.x, fillZoom.y, mapPxPerScreenPx);

    // HOI4 gradient_border_distance_to_alpha: band near the border, widening to full fill
    float band = 1.0 - saturate(dist / thickness);
    float alpha = 1.0 - saturate((1.0 - band) * (1.0 - fill));
    return lerp(interiorBlend, edgeBlend, alpha);
}

// Darkening right at the country border (HOI4 draws a darker outline edge)
float Hoi4CountryRim(float2 uv)
{
    float dist = SAMPLE_TEXTURE2D(_DistanceFieldBorderTexture, sampler_DistanceFieldBorderTexture,
                                  float2(uv.x, 1.0 - uv.y)).r * 32.0;
    return saturate(_Hoi4Gradient.w) * (1.0 - smoothstep(0.5, 2.0, dist));
}

float4 RenderMapMode(int mapMode, uint provinceID, float2 uv, float3 positionWS)
{
    float blend = HOI4_OR(_Hoi4PoliticalBlend, 0.65);

    if (mapMode == 1)
    {
        return Hoi4Render(uv, positionWS, 0);
    }
    if (mapMode >= 2 && mapMode < 100 && mapMode != 5)
    {
        float4 palette = Hoi4PaletteColor(provinceID);
        return Hoi4Render(uv, positionWS, float4(palette.rgb, palette.a < 0.01 ? 0 : blend));
    }

    // Political (0) and fallback
    float4 country = Hoi4CountryColor(uv);
    country.rgb = lerp(dot(country.rgb, float3(0.299, 0.587, 0.114)).xxx, country.rgb, (_Hoi4DiplomacyActive > 0.5 ? 1.0 : _CountryColorSaturation));
    country.rgb *= 1.0 - Hoi4CountryRim(uv) * 0.5;
    return Hoi4Render(uv, positionWS, float4(country.rgb, country.a * Hoi4CountryBlend(uv)));
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

// Night: city lights glow on the dark side, then the engine night look (HOI4 order)
#define ARCHON_OVERRIDE_NIGHT
float4 ArchonNight(float4 color, ArchonMapContext ctx)
{
    float night = ArchonNightFactor(ctx.uv);
    if (night <= 0.0) return color;

    // HOI4's emissive mask spills into the sea around coastal cities (HOI4 draws water over it);
    // keep the lights on land
    float mask = SAMPLE_TEXTURE2D(_Hoi4Colormap, sampler_trilinear_clamp, ctx.uv).a;
    if (mask > 0.0)
        mask *= Hoi4LandFraction(ctx.uv);
    if (mask > 0.0 && _Hoi4CityLightsIntensity > 0.0)
    {
        uint w, h;
        _ProvinceOwnerTexture.GetDimensions(w, h);
        float2 mapPx = float2(ctx.uv.x * w, (1.0 - ctx.uv.y) * h);
        float3 lights = SAMPLE_TEXTURE2D(_Hoi4CityLights, sampler_trilinear_repeat, mapPx * _Hoi4CityLightsTiling).rgb;
        color.rgb += lights * _Hoi4CityLightsIntensity * mask * night;
    }
    color.rgb = ArchonApplyNight(color.rgb, ctx.uv, 1.0);
    return color;
}

#endif // HOI4_MAP_MODES_INCLUDED
