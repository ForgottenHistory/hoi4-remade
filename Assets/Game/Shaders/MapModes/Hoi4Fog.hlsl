#ifndef HOI4_FOG_INCLUDED
#define HOI4_FOG_INCLUDED

// GAME: the fog of rain and thunderstorms when zoomed in. From a HOI4 screenshot: a pale haze over the terrain, which stays
// readable through it but loses its colour, with lighter puffs drifting in it; greyer and denser over a thunderstorm.
// HOI4 makes it with cloud puff particles around each weather position (gfx/particles/environment): rain_clouds are
// cloud_6.dds puffs ~60 map px, additive, bluish, living 2.6 s; lightning_storm_clouds the same puffs in grey, 10 s.
// Prototype: Tools/prototypes/weather/weather_fog.py.
//
// Shared by the terrain shader (Hoi4FogGround: washes the colour out of the ground under the fog) and Hoi4/Precipitation
// (the haze and the puffs over everything, trees and borders included), so both use the same fog amount:
//     f = smoothstep(0.15, 0.85, rain mask * 1.15 + (noise(map px / 80) - 0.5) * 0.5) * zoom fade
// _Hoi4PrecipTex (Hoi4WeatherSky): r rain, g snow, b thunderstorm. _Hoi4Precip.yz: the zoom range (screen px per map px,
// hidden at or below y, full at or above z), shared with the falling rain and snow.
// _Hoi4Fog: x ground desaturation (0.35), y haze alpha (0.3), z puff alpha (0.6), w puff size map px (50).
// Taken as they are: all zero (or no visual style) turns the fog off.

TEXTURE2D(_Hoi4PrecipTex);
float4 _Hoi4Precip;
float4 _Hoi4Fog;

float Hoi4FogHash(float2 p)
{
    p = frac(p * float2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return frac(p.x * p.y);
}

float Hoi4FogValueNoise(float2 x)
{
    float2 i = floor(x);
    float2 f = frac(x);
    f = f * f * (3.0 - 2.0 * f);
    return lerp(lerp(Hoi4FogHash(i), Hoi4FogHash(i + float2(1, 0)), f.x),
                lerp(Hoi4FogHash(i + float2(0, 1)), Hoi4FogHash(i + 1.0), f.x), f.y);
}

float Hoi4FogNoise(float2 x)
{
    return 0.55 * Hoi4FogValueNoise(x) + 0.3 * Hoi4FogValueNoise(x * 2.1 + 7.3) + 0.15 * Hoi4FogValueNoise(x * 4.3 - 3.1);
}

// The weather mask at a map uv, bilinear by hand (no sampler: the terrain shader is short of them)
float4 Hoi4PrecipAt(float2 uv)
{
    uint w, h;
    _Hoi4PrecipTex.GetDimensions(w, h);
    float2 p = uv * float2(w, h) - 0.5;
    int2 i = (int2)floor(p);
    float2 f = p - i;
    int2 hi = int2(w, h) - 1;
    float4 a = LOAD_TEXTURE2D(_Hoi4PrecipTex, clamp(i, 0, hi));
    float4 b = LOAD_TEXTURE2D(_Hoi4PrecipTex, clamp(i + int2(1, 0), 0, hi));
    float4 c = LOAD_TEXTURE2D(_Hoi4PrecipTex, clamp(i + int2(0, 1), 0, hi));
    float4 d = LOAD_TEXTURE2D(_Hoi4PrecipTex, clamp(i + int2(1, 1), 0, hi));
    return lerp(lerp(a, b, f.x), lerp(c, d, f.x), f.y);
}

float Hoi4FogZoom(float screenPxPerMapPx)
{
    float2 zoom = _Hoi4Precip.z > _Hoi4Precip.y ? _Hoi4Precip.yz : float2(1.0, 2.0);
    return smoothstep(zoom.x, zoom.y, screenPxPerMapPx);
}

// Fog amount (0..1, before the zoom fade) at a map pixel from the weather mask there; its edge broken up by noise
float Hoi4FogAmount(float2 mapPx, float4 m)
{
    float fogMask = saturate(m.r * 1.15);
    if (fogMask <= 0.001) return 0;
    float edge = Hoi4FogNoise(mapPx / 80.0);
    return smoothstep(0.15, 0.85, fogMask + (edge - 0.5) * 0.5);
}

// One puff layer, like HOI4's particles: at most one puff per grid cell (60%), at a hashed spot near the cell centre,
// gaussian, fading in and out over its life (HOI4 smoke_fade) at its own phase, drifting with the wind while it lives and
// re-seeded each life. Jitter + drift + reach stay within the 3x3 cells looked at, so no puff is cut off.
float Hoi4FogPuffLayer(float2 mapPx, float cell, float life, float seed)
{
    const float2 wind = float2(2.0, -0.7);                       // map px/s (map y is up here)
    float2 c = floor(mapPx / cell);
    float sum = 0;
    [unroll] for (int j = -1; j <= 1; j++)
    [unroll] for (int i = -1; i <= 1; i++)
    {
        float2 id = c + float2(i, j) + seed;
        if (Hoi4FogHash(id) > 0.6) continue;
        float clock = _Time.y / life + Hoi4FogHash(id + 3.1);
        float ph = frac(clock);
        float gen = fmod(floor(clock), 64.0);
        float fade = smoothstep(0.2, 0.5, ph) * smoothstep(1.0, 0.8, ph);
        float2 jitter = float2(Hoi4FogHash(id + gen * 1.7 + 5.3), Hoi4FogHash(id + gen * 2.3 + 9.1));
        float2 centre = (c + float2(i, j) + 0.3 + 0.4 * jitter) * cell + wind * (ph - 0.5) * life;
        float radius = cell * (0.5 + 0.3 * Hoi4FogHash(id + gen * 0.9 + 1.3));
        float2 d = mapPx - centre;
        sum += exp(-dot(d, d) / (radius * radius) * 2.5) * fade;
    }
    return sum;
}

// Puffs (0..1) in the fog: big puffs and smaller ones, overlapping into soft patches, their edges eroded by noise so
// they billow like HOI4's cloud_6 texture. Rain puffs live briefly, thunderstorm ones long (HOI4 2.6 s and 10 s).
float Hoi4FogPuffs(float2 mapPx, float storm)
{
    float size = _Hoi4Fog.w > 0 ? _Hoi4Fog.w : 50.0;
    float life = lerp(3.0, 10.0, storm);
    float gauss = Hoi4FogPuffLayer(mapPx, size, life, 0.0) + 0.6 * Hoi4FogPuffLayer(mapPx, size * 0.55, life, 37.0);
    float detail = saturate((Hoi4FogNoise(mapPx / (size * 0.35)) - 0.25) / 0.5);
    return saturate(gauss * 1.3 - (1.0 - detail) * 0.55);
}

// Terrain: wash the colour out of the ground under the fog (the overlay alone cannot, it only blends over)
float3 Hoi4FogGround(float3 color, float2 uv, float2 mapSize)
{
    float2 mapPx = uv * mapSize;
    float screenPxPerMapPx = 1.41421 / max(length(fwidth(mapPx)), 1e-5);   // derivatives before any branch
    if (_Hoi4Fog.x <= 0) return color;
    float4 m = Hoi4PrecipAt(uv);
    if (m.r <= 0.01) return color;
    float f = Hoi4FogAmount(mapPx, m) * Hoi4FogZoom(screenPxPerMapPx);
    float grey = dot(color, float3(0.2126, 0.7152, 0.0722));
    return lerp(color, grey.xxx, f * _Hoi4Fog.x);
}

#endif // HOI4_FOG_INCLUDED
