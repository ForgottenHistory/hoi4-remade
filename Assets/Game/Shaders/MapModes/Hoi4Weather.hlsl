// Hoi4Weather.hlsl
// GAME: weather on the terrain, ported from HOI4's ApplySnow (gfx/FX/standardfuncsgfx.fxh, pdxmap.shader).
// Prototype: Tools/prototypes/weather/snow_render.py.
//
//   - _Hoi4WeatherTex (Hoi4WeatherMap): one texel per province id: r mud, g snow (amount / 80), b snow before,
//     a mud before; _Hoi4WeatherFade blends before -> now. HOI4's SnowMudData is a filtered map texture, so the
//     provinces around a pixel are blended bilinearly (spacing _Hoi4Snow.w map px) for a soft snow edge.
//   - isSnow = snow / the amount that reads as full snow (_Hoi4Snow.x; how HOI4 scales it is not in its shaders).
//   - HOI4 ApplySnow, positions in map px, height in HOI4 units (heightmap * 25.5):
//       noise = city lights alpha (HOI4 citylights_rgb_snowmask_a) at pos * 0.06; flat ground above height 11 fills in
//       snow  = saturate(saturate(noise' - (1 - isSnow)) * 5), frost = saturate(saturate(noise' + 0.5) - (1 - isSnow))
//       alpha = (snow + frost) * no cliffs (normal.y 0.7..0.9) * saturate(isSnow * 2.25) * zoom opacity (0.95 in, 0.2 out)
//       colour -> snow texture a * SNOW_COLOR by (1 - spec) * saturate(alpha + 0.4 * smoothstep(isSnow)),
//       normal -> the snow texture's normal by alpha, gloss + a * alpha * 0.2

#ifndef HOI4_WEATHER_INCLUDED
#define HOI4_WEATHER_INCLUDED

TEXTURE2D(_Hoi4WeatherTex);
TEXTURE2D(_Hoi4CityLights);  // HOI4 citylights_rgb_snowmask_a: rgb city lights (Hoi4MapModes), a snow noise
TEXTURE2D(_Hoi4SnowTex);     // HOI4 snow_normal_rgb_diffuse_a: rbg normal, a diffuse (linear, repeat)
float _Hoi4WeatherFade;      // 0 = before, 1 = now
float4 _Hoi4Snow;            // x snow amount that reads full (40), y..z map px per screen px for the zoom fade, w edge blend spacing map px
float4 _Hoi4SnowColor;       // HOI4 SNOW_COLOR (0.46, 0.48, 0.69), a = strength (1)

#define HOI4_SNOW_TILING 0.05
#define HOI4_SNOW_NOISE_TILING 0.06

// The province's snow (0..1 of 80) at a province id pixel, faded between days
float Hoi4SnowAt(int2 pixel, int2 maxPixel)
{
    uint id = DecodeProvinceID(LOAD_TEXTURE2D(_ProvinceIDTexture, clamp(pixel, int2(0, 0), maxPixel)).rg);
    float4 w = LOAD_TEXTURE2D(_Hoi4WeatherTex, int2(id % 256, id / 256));
    return lerp(w.b, w.g, _Hoi4WeatherFade);
}

// Snow amount (0..80) at uv: the provinces around it blended bilinearly on a grid of `spacing` map px
float Hoi4SnowAmount(float2 uv)
{
    uint w, h;
    _ProvinceIDTexture.GetDimensions(w, h);
    float spacing = HOI4_OR(_Hoi4Snow.w, 2.0);
    float2 p = float2(uv.x, 1.0 - uv.y) * float2(w, h) / spacing - 0.5;
    float2 i0 = floor(p);
    float2 f = p - i0;
    int2 maxPixel = int2(w - 1, h - 1);
    float s00 = Hoi4SnowAt(int2((i0 + float2(0.5, 0.5)) * spacing), maxPixel);
    float s10 = Hoi4SnowAt(int2((i0 + float2(1.5, 0.5)) * spacing), maxPixel);
    float s01 = Hoi4SnowAt(int2((i0 + float2(0.5, 1.5)) * spacing), maxPixel);
    float s11 = Hoi4SnowAt(int2((i0 + float2(1.5, 1.5)) * spacing), maxPixel);
    return lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y) * 80.0;
}

// HOI4 ApplySnow on a land surface. Call before the country colour (HOI4 draws its borders over the snow).
void Hoi4ApplySnow(inout Hoi4Surface s, float2 uv)
{
    uint w, h;
    _Hoi4TerrainIndex.GetDimensions(w, h);
    float2 pos = uv * float2(w, h);   // HOI4 vPos.xz: map px, z up = north
    // Derivatives outside the branch
    float2 snowUV = float2(pos.x, -pos.y) * HOI4_SNOW_TILING;     // HOI4 samples top-down
    float2 noiseUV = float2(pos.x, -pos.y) * HOI4_SNOW_NOISE_TILING;
    float2 sdx = ddx(snowUV), sdy = ddy(snowUV), ndx = ddx(noiseUV), ndy = ddy(noiseUV);
    float mapPxPerScreenPx = length(fwidth(pos));

    if (s.land <= 0) return;
    float isSnow = saturate(Hoi4SnowAmount(uv) / HOI4_OR(_Hoi4Snow.x, 40.0));
    if (isSnow <= 0.002) return;

    float height = SAMPLE_TEXTURE2D_LOD(_HeightmapTexture, sampler_HeightmapTexture, float2(uv.x, 1.0 - uv.y), 0).r * 25.5;
    float4 snowTex = SAMPLE_TEXTURE2D_GRAD(_Hoi4SnowTex, sampler_trilinear_repeat, snowUV, sdx, sdy);
    float noise = SAMPLE_TEXTURE2D_GRAD(_Hoi4CityLights, sampler_trilinear_repeat, noiseUV, ndx, ndy).a;

    float snowFade = saturate(height - 3.0);                                  // SNOW_START_HEIGHT
    float normalFade = saturate(saturate(s.normal.y - 0.7) * 5.0);            // SNOW_NORMAL_START, SNOW_CLIFFS
    float transp = saturate(noise + saturate(height - 11.0) * saturate((s.normal.y - 0.9) * 1000.0) * isSnow); // ridges
    float snow = saturate(saturate(transp - (1.0 - isSnow)) * 5.0);
    float frost = saturate(saturate(transp + 0.5) - (1.0 - isSnow));

    float2 zoom = _Hoi4Snow.z > _Hoi4Snow.y ? _Hoi4Snow.yz : float2(0.04, 0.25);
    float opacity = lerp(0.95, 0.2, saturate((mapPxPerScreenPx - zoom.x) / (zoom.y - zoom.x))); // SNOW_OPACITY_MIN/MAX

    float alpha = saturate(saturate(snow + frost) * snowFade * normalFade * saturate(isSnow * 2.25) * opacity);
    float minSnow = smoothstep(0.0, 1.0, isSnow);
    float3 snowColor = _Hoi4SnowColor.a > 0 ? _Hoi4SnowColor.rgb : float3(0.46, 0.48, 0.69);
    float strength = _Hoi4SnowColor.a > 0 ? _Hoi4SnowColor.a : 1.0;
    float k = (1.0 - s.spec) * saturate(alpha + 0.4 * minSnow) * strength; // SNOW_FROST_MIN_EFFECT
    s.albedo = lerp(s.albedo, snowTex.a * snowColor, k);

    float3 snowNormal = Hoi4RotateNormal(s.normal, normalize(snowTex.rbg - 0.5));
    s.normal = normalize(lerp(s.normal, normalize(snowNormal), alpha * strength));
    s.gloss += snowTex.a * alpha * 0.2;                                       // SNOW_SPEC_GLOSS_MULT
}

#endif // HOI4_WEATHER_INCLUDED
