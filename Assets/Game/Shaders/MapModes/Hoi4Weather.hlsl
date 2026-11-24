// Hoi4Weather.hlsl
// GAME: weather on the terrain, ported from HOI4's ApplySnow and GetMudColor (gfx/FX/standardfuncsgfx.fxh,
// pdxmap.shader). Prototype: Tools/prototypes/weather/snow_render.py.
//
//   - _Hoi4WeatherTex (Hoi4WeatherMap): one texel per province id: r mud, g snow (amount / 80), b snow before,
//     a mud before; _Hoi4WeatherFade blends before -> now. HOI4's SnowMudData is a filtered map texture, so the
//     provinces around a pixel are blended bilinearly (spacing _Hoi4Snow.w map px) for a soft edge.
//   - isSnow = snow / the amount that reads as full snow (_Hoi4Snow.x; how HOI4 scales it is not in its shaders).
//   - HOI4 ApplySnow, positions in map px, height in HOI4 units (heightmap * 25.5):
//       noise = city lights alpha (HOI4 citylights_rgb_snowmask_a) at pos * 0.06; flat ground above height 11 fills in
//       snow  = saturate(saturate(noise' - (1 - isSnow)) * 5), frost = saturate(saturate(noise' + 0.5) - (1 - isSnow))
//       alpha = (snow + frost) * no cliffs (normal.y 0.7..0.9) * saturate(isSnow * 2.25) * zoom opacity (0.95 in, 0.2 out)
//       colour -> snow texture a * SNOW_COLOR by (1 - spec) * saturate(alpha + 0.4 * smoothstep(isSnow)),
//       normal -> the snow texture's normal by alpha, gloss + a * alpha * 0.2
//   - HOI4 GetMudColor, after the snow: mud = the province's mud (0/1) * _Hoi4Mud.x (HOI4's scale is not in its
//     shaders; at 1 the ground turns solid brown), broken up by noise (pos * 0.01) as the camera zooms out;
//       colour -> overlay(mud texture, colormap tint, 0.5), normal/gloss/spec -> the mud textures' (pos * 0.09)

#ifndef HOI4_WEATHER_INCLUDED
#define HOI4_WEATHER_INCLUDED

TEXTURE2D(_Hoi4WeatherTex);
TEXTURE2D(_Hoi4CityLights);  // HOI4 citylights_rgb_snowmask_a: rgb city lights (Hoi4MapModes), a snow noise
TEXTURE2D(_Hoi4SnowTex);     // HOI4 snow_normal_rgb_diffuse_a: rbg normal, a diffuse (linear, repeat)
TEXTURE2D(_Hoi4MudDiffuse);  // HOI4 mud_diffuse_rgb_gloss_a (repeat)
TEXTURE2D(_Hoi4MudNormal);   // HOI4 mud_normal_rgb_spec_a: rbg normal, a spec (linear, repeat)
float _Hoi4WeatherFade;      // 0 = before, 1 = now
float4 _Hoi4Snow;            // x snow amount that reads full (40), y..z map px per screen px for the zoom fade, w edge blend spacing map px
float4 _Hoi4SnowColor;       // HOI4 SNOW_COLOR (0.46, 0.48, 0.69), a = strength (1)
float4 _Hoi4Mud;             // x mud strength (0.75)

#define HOI4_SNOW_TILING 0.05
#define HOI4_SNOW_NOISE_TILING 0.06
#define HOI4_MUD_TILING 0.09
#define HOI4_MUD_NOISE_TILING 0.01

// The province's (snow 0..1 of 80, mud 0..1) at a province id pixel, faded between days
float2 Hoi4WeatherAt(int2 pixel, int2 maxPixel)
{
    uint id = DecodeProvinceID(LOAD_TEXTURE2D(_ProvinceIDTexture, clamp(pixel, int2(0, 0), maxPixel)).rg);
    float4 w = LOAD_TEXTURE2D(_Hoi4WeatherTex, int2(id % 256, id / 256));
    return lerp(w.ba, w.gr, _Hoi4WeatherFade);
}

// (snow amount 0..80, mud 0..1) at uv: the provinces around it blended bilinearly on a grid of `spacing` map px
float2 Hoi4WeatherAmount(float2 uv)
{
    uint w, h;
    _ProvinceIDTexture.GetDimensions(w, h);
    float spacing = HOI4_OR(_Hoi4Snow.w, 2.0);
    float2 p = float2(uv.x, 1.0 - uv.y) * float2(w, h) / spacing - 0.5;
    float2 i0 = floor(p);
    float2 f = p - i0;
    int2 maxPixel = int2(w - 1, h - 1);
    float2 s00 = Hoi4WeatherAt(int2((i0 + float2(0.5, 0.5)) * spacing), maxPixel);
    float2 s10 = Hoi4WeatherAt(int2((i0 + float2(1.5, 0.5)) * spacing), maxPixel);
    float2 s01 = Hoi4WeatherAt(int2((i0 + float2(0.5, 1.5)) * spacing), maxPixel);
    float2 s11 = Hoi4WeatherAt(int2((i0 + float2(1.5, 1.5)) * spacing), maxPixel);
    return lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y) * float2(80.0, 1.0);
}

// HOI4 ApplySnow + GetMudColor on a land surface. Call before the country colour (HOI4 draws its borders over them).
void Hoi4ApplyWeather(inout Hoi4Surface s, float2 uv, float3 tint)
{
    uint w, h;
    _Hoi4TerrainIndex.GetDimensions(w, h);
    float2 pos = uv * float2(w, h);   // HOI4 vPos.xz: map px, z up = north
    // Derivatives outside the branches (HOI4 samples top-down)
    float2 snowUV = float2(pos.x, -pos.y) * HOI4_SNOW_TILING;
    float2 noiseUV = float2(pos.x, -pos.y) * HOI4_SNOW_NOISE_TILING;
    float2 mudUV = float2(pos.x, -pos.y) * HOI4_MUD_TILING;
    float2 mudNoiseUV = float2(pos.x, -pos.y) * HOI4_MUD_NOISE_TILING;
    float2 sdx = ddx(snowUV), sdy = ddy(snowUV), ndx = ddx(noiseUV), ndy = ddy(noiseUV);
    float2 mdx = ddx(mudUV), mdy = ddy(mudUV), mndx = ddx(mudNoiseUV), mndy = ddy(mudNoiseUV);
    float mapPxPerScreenPx = length(fwidth(pos));

    if (s.land <= 0) return;
    float2 amount = Hoi4WeatherAmount(uv);
    float2 zoomRange = _Hoi4Snow.z > _Hoi4Snow.y ? _Hoi4Snow.yz : float2(0.04, 0.25);
    float zoomOut = saturate((mapPxPerScreenPx - zoomRange.x) / (zoomRange.y - zoomRange.x)); // HOI4 cam_distance(50, 300)

    float isSnow = saturate(amount.x / HOI4_OR(_Hoi4Snow.x, 40.0));
    if (isSnow > 0.002)
    {
        float height = SAMPLE_TEXTURE2D_LOD(_HeightmapTexture, sampler_HeightmapTexture, float2(uv.x, 1.0 - uv.y), 0).r * 25.5;
        float4 snowTex = SAMPLE_TEXTURE2D_GRAD(_Hoi4SnowTex, sampler_trilinear_repeat, snowUV, sdx, sdy);
        float noise = SAMPLE_TEXTURE2D_GRAD(_Hoi4CityLights, sampler_trilinear_repeat, noiseUV, ndx, ndy).a;

        float snowFade = saturate(height - 3.0);                                  // SNOW_START_HEIGHT
        float normalFade = saturate(saturate(s.normal.y - 0.7) * 5.0);            // SNOW_NORMAL_START, SNOW_CLIFFS
        float transp = saturate(noise + saturate(height - 11.0) * saturate((s.normal.y - 0.9) * 1000.0) * isSnow); // ridges
        float snow = saturate(saturate(transp - (1.0 - isSnow)) * 5.0);
        float frost = saturate(saturate(transp + 0.5) - (1.0 - isSnow));
        float opacity = lerp(0.95, 0.2, zoomOut);                                 // SNOW_OPACITY_MIN/MAX

        float alpha = saturate(saturate(snow + frost) * snowFade * normalFade * saturate(isSnow * 2.25) * opacity);
        float minSnow = smoothstep(0.0, 1.0, isSnow);
        float3 snowColor = _Hoi4SnowColor.a > 0 ? _Hoi4SnowColor.rgb : float3(0.46, 0.48, 0.69);
        float strength = _Hoi4SnowColor.a > 0 ? _Hoi4SnowColor.a : 1.0;
        float k = (1.0 - s.spec) * saturate(alpha + 0.4 * minSnow) * strength;   // SNOW_FROST_MIN_EFFECT
        s.albedo = lerp(s.albedo, snowTex.a * snowColor, k);

        float3 snowNormal = Hoi4RotateNormal(s.normal, normalize(snowTex.rbg - 0.5));
        s.normal = normalize(lerp(s.normal, normalize(snowNormal), alpha * strength));
        s.gloss += snowTex.a * alpha * 0.2;                                       // SNOW_SPEC_GLOSS_MULT
    }

    float mud = amount.y * HOI4_OR(_Hoi4Mud.x, 0.75);
    if (mud > 0.002)
    {
        float noise = SAMPLE_TEXTURE2D_GRAD(_Hoi4CityLights, sampler_trilinear_repeat, mudNoiseUV, mndx, mndy).a;
        mud = saturate(mud * lerp(1.0, (0.5 + noise) * 0.5, zoomOut));
        float4 diffuse = SAMPLE_TEXTURE2D_GRAD(_Hoi4MudDiffuse, sampler_trilinear_repeat, mudUV, mdx, mdy);
        float4 normalSpec = SAMPLE_TEXTURE2D_GRAD(_Hoi4MudNormal, sampler_trilinear_repeat, mudUV, mdx, mdy);
        float3 mudNormal = Hoi4RotateNormal(s.normal, normalize(normalSpec.rbg - 0.5));
        s.normal = normalize(lerp(s.normal, normalize(mudNormal), mud));
        s.gloss = lerp(s.gloss, diffuse.a, mud);
        s.spec = lerp(s.spec, normalSpec.a, mud);
        s.albedo = lerp(s.albedo, Hoi4Overlay(diffuse.rgb, tint, 0.5), mud);   // COLORMAP_MUD_OVERLAY_STRENGTH
    }
}

#endif // HOI4_WEATHER_INCLUDED
