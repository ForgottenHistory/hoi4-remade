// Hoi4MapModes.hlsl
// GAME: map mode dispatcher. Every mode is drawn over the HOI4 terrain surface:
//   0  political  - country color painted over terrain (unowned land/sea show terrain/water)
//   1  terrain    - plain terrain
//   2+ custom     - province palette color painted over terrain (alpha 0 = terrain only)
//   6  supply     - the palette smoothed across provinces like HOI4's supply heatmap (Hoi4SupplyMapMode)
// Lighting is done inside Hoi4Render, so the engine lighting stage is disabled here.

#ifndef HOI4_MAP_MODES_INCLUDED
#define HOI4_MAP_MODES_INCLUDED

#include "Assets/Archon-Engine/Shaders/Includes/MapModeCommon.hlsl"
#include "Assets/Archon-Engine/Shaders/Includes/DayNight.hlsl"
#include "Hoi4Terrain.hlsl"

// City lights at night (HOI4: citylights texture tiled over the map, masked by colormap alpha).
// _Hoi4CityLights is declared in Hoi4Weather.hlsl (its alpha is HOI4's snow noise).
float _Hoi4CityLightsIntensity;  // HOI4 CITY_LIGHTS_INTENSITY 5.5
float _Hoi4CityLightsTiling;     // repeats per map pixel, HOI4 CITY_LIGHTS_TILING 0.09103

float _Hoi4CountryEdgeSoftness; // 1 = gradient over ~3-4 map px between countries, 0 = near-sharp line

// Diplomatic mode (shader mode 5) reuses the political country blend, but with per-country colors
// from this 4096x1 texture (rgb = color, a = 0 for countries drawn as plain terrain)
TEXTURE2D(_Hoi4DiplomacyColors);
float _Hoi4DiplomacyActive;

// Occupation (Hoi4Occupation): one texel per province ID, 0 = not occupied, else the controlling country.
// HOI4 paints an occupied province in the controller's colour, so the country blend uses it instead of the owner.
TEXTURE2D(_Hoi4ControllerTex);
float _Hoi4HideOccupation; // 1 = owners only (the peace conference shows the loser in its own colour)

uint Hoi4Controller(uint provinceID)
{
    if (_Hoi4HideOccupation > 0.5) return 0u;
    return (uint)(LOAD_TEXTURE2D(_Hoi4ControllerTex, int2(provinceID % 256, provinceID / 256)).r + 0.5);
}

uint Hoi4ColorOwner(int2 pixel, uint owner)
{
    uint controller = Hoi4Controller(DecodeProvinceID(LOAD_TEXTURE2D(_ProvinceIDTexture, pixel).rg));
    return controller != 0u ? controller : owner;
}

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
            int2 cp = clamp(pixel, int2(0, 0), maxI);
            uint owner = Hoi4ColorOwner(cp, (uint)(LOAD_TEXTURE2D(_ProvinceOwnerTexture, cp).r + 0.5));

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

// Construction placing (Hoi4ConstructionMap): province-indexed RGBA, r = fill (0.5 green: the state has room for the
// building, 1 blue: full; the building's own orders not counted), g = bands (0.5 yellow: an order for the building,
// room left; 1 blue: the order fills the state), b = the state under the cursor (a soft green glow along its
// outline, coasts included). Broad '/' bands at 40 degrees, 7.4 map px apart.
// Prototype: Tools/prototypes/construction/construction_map.py
TEXTURE2D(_Hoi4ConstructionTex);
float _Hoi4ConstructionActive;
float _Hoi4ConstructionHover; // 1 while a state is hovered (skips the 6x6 glow window otherwise)

// Supply map mode (Hoi4SupplyMapMode, shader mode 6): the province palette colours averaged over a disc of
// _Hoi4SupplyMap.x map px (32 taps, weighted by alpha so uncoloured land doesn't fade them), shown with the coverage
// of the pixel's own surroundings over ~1 map px, so the colour flows across provinces but stops at the country's
// edge. _Hoi4SupplyMap.y = opacity over the terrain. Prototype: Tools/prototypes/supply/supply_mapmode.py
float4 _Hoi4SupplyMap;

float4 Hoi4SupplyColor(float2 uv)
{
    uint w, h;
    _ProvinceOwnerTexture.GetDimensions(w, h);
    float2 texel = 1.0 / float2(w, h);
    float radius = HOI4_OR(_Hoi4SupplyMap.x, 5.0);
    float3 sum = 0.0;
    float weight = 0.0;
    [loop] for (int i = 0; i < 32; i++)
    {
        float ang = i * 2.39996;
        float r = radius * sqrt((i + 0.5) / 32.0);
        float4 c = Hoi4PaletteColor(SampleProvinceID(uv + float2(cos(ang), -sin(ang)) * r * texel));
        sum += c.rgb * c.a;
        weight += c.a;
    }
    if (weight < 1e-3) return 0;
    float cover = 0.0;
    [unroll] for (int k = 0; k < 7; k++)
    {
        float ang = k * 2.39996;
        float r = k == 0 ? 0.0 : sqrt(k / 6.0);
        cover += Hoi4PaletteColor(SampleProvinceID(uv + float2(cos(ang), -sin(ang)) * r * texel)).a > 0.01 ? 1.0 : 0.0;
    }
    return float4(sum / weight, cover / 7.0);
}

float4 RenderMapMode(int mapMode, uint provinceID, float2 uv, float3 positionWS)
{
    float blend = HOI4_OR(_Hoi4PoliticalBlend, 0.65);

    if (mapMode == 1)
    {
        return Hoi4Render(uv, positionWS, 0);
    }
    if (mapMode == 6)
    {
        float4 supply = Hoi4SupplyColor(uv);
        return Hoi4Render(uv, positionWS, float4(supply.rgb, supply.a * HOI4_OR(_Hoi4SupplyMap.y, 0.8)));
    }
    if (mapMode >= 2 && mapMode < 100 && mapMode != 5)
    {
        float4 palette = Hoi4PaletteColor(provinceID);
        return Hoi4Render(uv, positionWS, float4(palette.rgb, palette.a < 0.01 ? 0 : blend));
    }

    // Political (0) and fallback; plain terrain while construction paints the states (Hoi4ConstructionOverlay)
    if (_Hoi4ConstructionActive > 0.5) return Hoi4Render(uv, positionWS, 0);
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

// Battle plan tool (Hoi4BattlePlans): province-indexed RGBA, rgb = colour, a = 0 none / 0.25 offensive
// target (yellow, broad "/" bands, a glow along the region's edge) / 0.375 a target picked by the drag
// (green, the same bands stronger) / 0.5 striped (front line tool) / 0.625 an area defence order's state
// (broad dim bands in the given colour) / 1 the province under the cursor. Prototype: Tools/prototypes/battleplan/offensive.py
TEXTURE2D(_Hoi4PlanTex);
float _Hoi4PlanActive;

float3 Hoi4PlanOverlay(float3 rgb, ArchonMapContext ctx)
{
    if (_Hoi4PlanActive < 0.5 || ctx.provinceID == 0u) return rgb;
    uint w, h;
    _ProvinceOwnerTexture.GetDimensions(w, h);
    float2 texel = 1.0 / float2(w, h);

    // The province id texture is hard-edged and pixelated; blur the coverage over a small disc so the
    // striped region has soft edges like HOI4's
    float cover = 0.0, hot = 0.0, offensive = 0.0, picked = 0.0, area = 0.0;
    float3 tint = 0.0;
    const float radius = 1.0; // map px: a European province is ~9 px across
    [unroll] for (int i = 0; i < 13; i++)
    {
        float ang = i * 2.39996;
        float r = i == 0 ? 0.0 : radius * sqrt(i / 12.0);
        float2 uv = ctx.uv + float2(cos(ang), -sin(ang)) * r * texel;
        uint id = SampleProvinceID(uv);
        float4 plan = LOAD_TEXTURE2D(_Hoi4PlanTex, int2(id % 256u, id / 256u));
        float m = plan.a > 0.01 ? 1.0 : 0.0;
        cover += m;
        hot += plan.a > 0.75 ? 1.0 : 0.0;
        float isPicked = (plan.a > 0.33 && plan.a < 0.42) ? 1.0 : 0.0;
        picked += isPicked;
        area += (plan.a > 0.56 && plan.a < 0.69) ? 1.0 : 0.0;
        offensive += ((plan.a > 0.15 && plan.a < 0.33) ? 1.0 : 0.0) + isPicked;
        tint += plan.rgb * m;
    }
    if (cover < 0.5) return rgb;
    tint /= cover;
    cover /= 13.0;
    hot /= 13.0;
    offensive /= 13.0;
    picked /= 13.0;
    area /= 13.0;

    float2 mapPx = float2(ctx.uv.x * w, (1.0 - ctx.uv.y) * h);
    if (area > cover * 0.5)
    {
        // An area defence order's states: broad bands like the picked targets', dim, no edge glow
        float acrossA = -0.4226 * mapPx.x - 0.9063 * mapPx.y;
        float bandA = smoothstep(-0.35, 0.35, sin(acrossA * 6.2832 / 4.5) + 0.1);
        return lerp(rgb, tint, cover * (0.04 + 0.5 * bandA));
    }
    if (offensive > 0.5)
    {
        // HOI4's offensive tool: the enemy's provinces in broad translucent bands rising to the right at
        // ~25 degrees, brighter along the region's edge (where the blurred coverage falls off)
        float across = -0.4226 * mapPx.x - 0.9063 * mapPx.y;
        float band = smoothstep(-0.4, 0.4, sin(across * 6.2832 / 3.3) + 0.2);
        float edge = saturate((1.0 - cover) * 2.5);
        float ko = cover * lerp(0.20 + 0.20 * band, 0.10 + 0.55 * band, saturate(picked / max(cover, 1e-3)))
                 + edge * cover * 0.35 + hot * 0.18;
        return lerp(rgb, tint, ko) + hot * 0.05;
    }
    float stripe = smoothstep(-0.35, 0.35, sin((mapPx.x + mapPx.y) * 6.2832 / 3.4));
    float k = cover * lerp(0.10, 0.42, stripe) + hot * 0.18;
    return lerp(rgb, tint, k) + hot * 0.05;
}

// The hovered state's (b channel) share of a 6x6 map pixel window around uv, tent-weighted like the country
// colour blend (Hoi4CountryColor) but wider: a smooth field that goes from 1 inside to 0 outside over ~4 map px,
// following the outline in curves instead of map pixel steps
float Hoi4HoverShare(float2 uv, uint w, uint h)
{
    float2 q = float2(uv.x, 1.0 - uv.y) * float2(w, h) - 0.5;   // pixel-centre coordinates
    int2 i0 = (int2)floor(q);
    int2 maxI = int2(w - 1, h - 1);
    float share = 0.0, total = 0.0;
    [unroll] for (int oy = -2; oy <= 3; oy++)
    {
        [unroll] for (int ox = -2; ox <= 3; ox++)
        {
            int2 pixel = i0 + int2(ox, oy);
            float2 tent = saturate(1.0 - abs((float2)pixel - q) / 2.6);
            float weight = tent.x * tent.y;
            int2 cp = clamp(pixel, int2(0, 0), maxI);
            uint id = DecodeProvinceID(LOAD_TEXTURE2D(_ProvinceIDTexture, cp).rg);
            share += weight * (LOAD_TEXTURE2D(_Hoi4ConstructionTex, int2(id % 256u, id / 256u)).b > 0.5 ? 1.0 : 0.0);
            total += weight;
        }
    }
    return share / max(total, 1e-4);
}

float3 Hoi4ConstructionOverlay(float3 rgb, ArchonMapContext ctx)
{
    if (_Hoi4ConstructionActive < 0.5 || ctx.provinceID == 0u) return rgb;
    uint w, h;
    _ProvinceOwnerTexture.GetDimensions(w, h);
    float2 texel = 1.0 / float2(w, h);

    // coverage blurred over a disc (the id texture is pixelated): soft state edges
    float green = 0.0, blue = 0.0, yellowBand = 0.0, blueBand = 0.0, greenBand = 0.0;
    const float radius = 1.4; // map px
    [unroll] for (int i = 0; i < 13; i++)
    {
        float ang = i * 2.39996;
        float r = i == 0 ? 0.0 : radius * sqrt(i / 12.0);
        float2 uv = ctx.uv + float2(cos(ang), -sin(ang)) * r * texel;
        uint id = SampleProvinceID(uv);
        float4 c = LOAD_TEXTURE2D(_Hoi4ConstructionTex, int2(id % 256u, id / 256u));
        green += (c.r > 0.25 && c.r < 0.75) ? 1.0 : 0.0;
        blue += c.r >= 0.75 ? 1.0 : 0.0;
        yellowBand += (c.g > 0.25 && c.g < 0.75) ? 1.0 : 0.0;
        blueBand += c.g >= 0.75 ? 1.0 : 0.0;
        greenBand += (c.g > 0.1 && c.g <= 0.25) ? 1.0 : 0.0; // deploy locations (recruit screen)
    }
    green /= 13.0; blue /= 13.0; yellowBand /= 13.0; blueBand /= 13.0; greenBand /= 13.0;

    // hovered state: a glowing line along its outline (where its smooth share is about half), brightest on the
    // line, fading out to both sides, pulsing slowly
    float hover = _Hoi4ConstructionHover > 0.5 ? Hoi4HoverShare(ctx.uv, w, h) : 0.0;
    float d = (hover - 0.5) / 0.24;
    float glow = exp(-d * d) * (hover > 0.001 && hover < 0.999 ? 1.0 : 0.0);
    glow *= 0.75 + 0.25 * sin(_Time.y * 3.5);
    const float3 glowColour = float3(0.45, 1.0, 0.62);
    if (green + blue + yellowBand + blueBand + greenBand < 0.01) return lerp(rgb, glowColour, glow) + glowColour * glow * 0.25;

    rgb = lerp(rgb, float3(0.30, 0.85, 0.25), green * 0.55);
    rgb = lerp(rgb, float3(0.20, 0.35, 1.00), blue * 0.60);
    float bands = yellowBand + blueBand + greenBand;
    if (bands > 0.01)
    {
        float2 mapPx = float2(ctx.uv.x * w, (1.0 - ctx.uv.y) * h);
        float across = -0.6428 * mapPx.x - 0.7660 * mapPx.y;   // 40 degrees, rising to the right ('/')
        float band = saturate((sin(across * 6.2832 / 7.4) + 0.05) / 0.35);
        float core = saturate((bands - 0.35) / 0.5);
        float3 colour = (yellowBand * float3(1.0, 0.9, 0.1) + blueBand * float3(0.2, 0.35, 1.0) + greenBand * float3(0.25, 1.0, 0.25)) / bands;
        rgb = lerp(rgb, colour, core * band * 0.9);
    }
    return lerp(rgb, glowColour, glow) + glowColour * glow * 0.25;
}

// Peace conference (Hoi4PeaceConferencePanel): province-indexed RGBA, rgb = band colour (green on the table,
// blue demanded), a > 0 = one of the loser's states. Broad bands rising to the right at ~35 degrees, 6.4 map px
// apart, fading in from the country's edge, where a glow of the loser's colour (_Hoi4PeaceGlow) shows; the land
// between the bands darkens a little. Prototype: Tools/prototypes/peace/peace_stripes.py
TEXTURE2D(_Hoi4PeaceTex);
float _Hoi4PeaceActive;
float4 _Hoi4PeaceGlow;

float3 Hoi4PeaceOverlay(float3 rgb, ArchonMapContext ctx)
{
    if (_Hoi4PeaceActive < 0.5 || ctx.provinceID == 0u) return rgb;
    uint w, h;
    _ProvinceOwnerTexture.GetDimensions(w, h);
    float2 texel = 1.0 / float2(w, h);

    // Coverage blurred over a disc (the id texture is pixelated): soft band ends and an inner edge for the glow
    float cover = 0.0, strong = 0.0;
    float3 tint = 0.0;
    const float radius = 1.6; // map px
    [unroll] for (int i = 0; i < 13; i++)
    {
        float ang = i * 2.39996;
        float r = i == 0 ? 0.0 : radius * sqrt(i / 12.0);
        float2 uv = ctx.uv + float2(cos(ang), -sin(ang)) * r * texel;
        uint id = SampleProvinceID(uv);
        float4 peace = LOAD_TEXTURE2D(_Hoi4PeaceTex, int2(id % 256u, id / 256u));
        float m = peace.a > 0.01 ? 1.0 : 0.0;
        cover += m;
        strong += peace.a * m;
        tint += peace.rgb * m;
    }
    if (cover < 0.5) return rgb;
    tint /= cover;
    strong /= cover;   // band opacity: 0.62 on the table, higher for demands
    cover /= 13.0;

    float2 mapPx = float2(ctx.uv.x * w, (1.0 - ctx.uv.y) * h);
    float across = -0.5736 * mapPx.x - 0.8192 * mapPx.y;   // 35 degrees, bands rise to the right ('/')
    float band = saturate((sin(across * 6.2832 / 6.4) + 0.1) / 0.5);
    float core = saturate((cover - 0.35) / 0.5);
    float edge = saturate((1.0 - cover) * 2.2) * cover;
    rgb = lerp(rgb, _Hoi4PeaceGlow.rgb, edge * 0.55);
    float k = core * band * strong;
    float dark = core * (1.0 - band) * 0.18;
    return rgb * (1.0 - k - dark) + tint * k;
}

// Hoi4Render already lit the pixel
#define ARCHON_OVERRIDE_LIGHTING
float4 ArchonLighting(float4 color, ArchonMapContext ctx)
{
    color.rgb = Hoi4PlanOverlay(color.rgb, ctx);
    color.rgb = Hoi4ConstructionOverlay(color.rgb, ctx);
    color.rgb = Hoi4PeaceOverlay(color.rgb, ctx);
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
