Shader "Hoi4/RiverMesh"
{
    // GAME: HOI4 rivers, the engine's mesh line pass (Archon BorderMeshCore.hlsl) plus HOI4's frozen rivers
    // (gfx/FX/river.shader ApplyRiverSnow). Named by the visual style's rivers section (shader: "Hoi4/RiverMesh").
    //
    //   isSnow = the snow where the river runs (Hoi4WeatherMap, like the terrain's snow; Hoi4Weather.hlsl)
    //   alpha  = smoothstep(0, 1, saturate(isSnow * zoom opacity (0.95 in, 0.2 out) * 1.7))
    //   colour -> lerp(SNOW_WATER_COLOR (0.3, 0.6, 1) * sky, SNOW_COLOR, smoothstep(0.4, 0.75, noise + bank)) by alpha
    // noise = city lights alpha at pos * 0.0625 (SNOW_ICE_NOISE_TILING); bank = 0 in the water, 1 at the strip edges
    // (HOI4's RiverData side alpha; ours from the strip's V). sky = HOI4's reflection cubemap (_Hoi4WaterSky, the sea's)
// at the mirrored view ray * 1.3, plus a thin dark line near the edges (HOI4's lit banks).
    // Properties and pass state: as Archon/BorderMesh.
    Properties
    {
        _BorderTex ("Border Texture (U along, V across)", 2D) = "white" {}
        _BorderUseTexture ("Use Texture", Float) = 0
        _BorderTexAlphaOnly ("Texture Alpha Only", Float) = 0
        _BorderSoftness ("Softness", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _BorderSrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _BorderDstBlend ("Dst Blend", Float) = 10
        [Enum(UnityEngine.Rendering.BlendOp)] _BorderBlendOp ("Blend Op", Float) = 0
        _BorderPremultiply ("Premultiply Output", Float) = 0
        _BorderColor ("Color", Color) = (1, 1, 1, 1)
        _BorderColorSource ("Color Source", Float) = 0
        _BorderWidth ("Width (map px)", Float) = 0.6
        _BorderWidthPerLevel ("Extra Width per Level (map px)", Float) = 0
        _BorderMinScreenWidth ("Min Width (screen px)", Float) = 1
        _BorderTile ("Texture Repeats per Map Pixel", Float) = 0.4
        _BorderFade ("Fade", Vector) = (0, 0, 0, 0)
        _HeightmapTexture ("Heightmap", 2D) = "gray" {}
        _HeightScale ("Height Scale", Float) = 10.0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-100"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "BorderMesh"

            Blend [_BorderSrcBlend] [_BorderDstBlend]
            BlendOp [_BorderBlendOp]
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex ArchonBorderMeshVert
            #pragma fragment Hoi4RiverFrag

            #include "Assets/Archon-Engine/Shaders/Includes/BorderMeshCore.hlsl"

            TEXTURE2D(_ProvinceIDTexture);   // engine global
            TEXTURE2D(_Hoi4WeatherTex);      // Hoi4WeatherMap: per province id, g snow now, b snow before
            TEXTURE2D(_Hoi4CityLights);      // a = HOI4 snow noise
            TEXTURE2D(_Hoi4WaterSky);        // HOI4's reflection cubemap baked to lat-long (top = up), as the sea uses
            SAMPLER(sampler_trilinear_repeat);
            float _Hoi4WeatherFade;
            float4 _Hoi4Snow;                // x snow amount that reads full, y..z zoom fade (map px per screen px), w edge blend
            float4 _Hoi4SnowColor;           // HOI4 SNOW_COLOR, a = strength

            float Hoi4RiverSnowAt(int2 pixel, int2 maxPixel)
            {
                float2 enc = LOAD_TEXTURE2D(_ProvinceIDTexture, clamp(pixel, int2(0, 0), maxPixel)).rg;
                uint id = ((uint)(enc.g * 255.0 + 0.5) << 8) | (uint)(enc.r * 255.0 + 0.5);
                float4 w = LOAD_TEXTURE2D(_Hoi4WeatherTex, int2(id % 256, id / 256));
                return lerp(w.b, w.g, _Hoi4WeatherFade);
            }

            // Snow amount (0..80) at map uv (v = 1 north), the provinces around it blended bilinearly
            float Hoi4RiverSnow(float2 uv, float2 size)
            {
                float spacing = _Hoi4Snow.w > 0 ? _Hoi4Snow.w : 2.0;
                float2 p = float2(uv.x, 1.0 - uv.y) * size / spacing - 0.5;
                float2 i0 = floor(p);
                float2 f = p - i0;
                int2 maxPixel = int2(size) - 1;
                float s00 = Hoi4RiverSnowAt(int2((i0 + float2(0.5, 0.5)) * spacing), maxPixel);
                float s10 = Hoi4RiverSnowAt(int2((i0 + float2(1.5, 0.5)) * spacing), maxPixel);
                float s01 = Hoi4RiverSnowAt(int2((i0 + float2(0.5, 1.5)) * spacing), maxPixel);
                float s11 = Hoi4RiverSnowAt(int2((i0 + float2(1.5, 1.5)) * spacing), maxPixel);
                return lerp(lerp(s00, s10, f.x), lerp(s01, s11, f.x), f.y) * 80.0;
            }

            half4 Hoi4RiverFrag(Varyings input) : SV_Target
            {
                float4 color;
                float alpha;
                ArchonBorderMeshColor(input, color, alpha);

                uint w, h;
                _ProvinceIDTexture.GetDimensions(w, h);
                float2 size = float2(w, h);
                float2 pos = input.mapUV * size;                       // map px, y up = north
                float2 noiseUV = float2(pos.x, -pos.y) * 0.0625;       // SNOW_ICE_NOISE_TILING
                float noise = SAMPLE_TEXTURE2D(_Hoi4CityLights, sampler_trilinear_repeat, noiseUV).a;
                float mapPxPerScreenPx = length(fwidth(pos));

                float isSnow = saturate(Hoi4RiverSnow(input.mapUV, size) / (_Hoi4Snow.x > 0 ? _Hoi4Snow.x : 40.0));
                float2 zoom = _Hoi4Snow.z > _Hoi4Snow.y ? _Hoi4Snow.yz : float2(0.04, 0.25);
                float opacity = lerp(0.95, 0.2, saturate((mapPxPerScreenPx - zoom.x) / (zoom.y - zoom.x)));
                float snowAlpha = smoothstep(0.0, 1.0, saturate(isSnow * opacity * 1.7));

                float bank = saturate((abs(input.uv.y - 0.5) - 0.25) * 4.0);   // HOI4: water where |v - 0.5| <= 0.25
                float3 snowColor = _Hoi4SnowColor.a > 0 ? _Hoi4SnowColor.rgb : float3(0.46, 0.48, 0.69);
                // HOI4: reflectiveColor = sky cubemap at the reflected view ray * 1.3 (flat river: mirror the view)
                float3 posWS = float3(_MapWorldBounds.x + input.mapUV.x * _MapWorldBounds.z, 0.0,
                                      _MapWorldBounds.y + input.mapUV.y * _MapWorldBounds.w);
                float3 V = normalize(GetCameraPositionWS() - posWS);
                float3 r = float3(-V.x, abs(V.y), -V.z);
                float2 skyUV = float2(atan2(r.x, r.z) * (0.5 / PI) + 0.5, asin(saturate(r.y)) * (1.0 / PI) + 0.5);
                float3 sky = SAMPLE_TEXTURE2D_LOD(_Hoi4WaterSky, sampler_trilinear_repeat, skyUV, 0).rgb * 1.3;
                float3 ice = lerp(saturate(float3(0.3, 0.6, 1.0) * sky), snowColor,
                                  smoothstep(0.4, 0.75, saturate(noise + bank)));
                // HOI4 keeps the banks' lighting under the snow, which shows as a thin dark line along the edge of the
                // ice; our lines are unlit and our bank texture is light, so draw it: a dark line near the strip edge
                float across = abs(input.uv.y - 0.5) * 2.0;                     // 0 centre .. 1 edge
                float lineWidth = 0.07 + fwidth(across);
                float edgeLine = saturate(1.0 - abs(across - 0.84) / lineWidth);
                ice *= 1.0 - 0.65 * edgeLine * edgeLine;
                color.rgb = lerp(color.rgb, ice, snowAlpha * (_Hoi4SnowColor.a > 0 ? _Hoi4SnowColor.a : 1.0));

                return ArchonBorderMeshOutput(input, color, alpha);
            }
            ENDHLSL
        }
    }
}
