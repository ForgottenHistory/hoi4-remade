// GAME: battle plan lines (Hoi4BattlePlans). A ribbon along a province border, built on the CPU in map-plane
// world xz and draped on the terrain like Hoi4/MoveArrow. uv.x = distance along the line in ribbon widths,
// uv.y = across (0 = own side, 1 = enemy side), vertex colour = line colour, uv1.x = style:
//   0 = dashed preview line (the hover before placing), 1 = placed front line: a solid band with
//   ticks pointing at the enemy side, 2 = an offensive plan's objective line (HOI4 offence_line): a band
//   with teeth pointing at the left of travel (the target).
Shader "Hoi4/PlanLine"
{
    Properties
    {
        _MaskTex ("Mask", 2D) = "white" {}
        _PatternTex ("Pattern", 2D) = "white" {}
        _PreviewMaskTex ("Preview Mask", 2D) = "white" {}
        _PreviewPatternTex ("Preview Pattern", 2D) = "white" {}
        _OffenceMaskTex ("Objective Line Mask", 2D) = "white" {}
        _OffencePatternTex ("Objective Line Pattern", 2D) = "white" {}
        _OffenceTile ("Objective line tooth spacing (widths)", Float) = 0.8
        _HeightmapTexture ("Heightmap", 2D) = "gray" {}
        _HeightScale ("Height Scale", Float) = 10.0
        _MapWorldBounds ("Map World Bounds (min x, min z, size x, size z)", Vector) = (-5, -5, 10, 10)
        _Lift ("Lift", Float) = 0.06
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+80"
            "RenderPipeline" = "UniversalPipeline"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);
            TEXTURE2D(_PatternTex);
            SAMPLER(sampler_PatternTex);
            TEXTURE2D(_PreviewMaskTex);
            SAMPLER(sampler_PreviewMaskTex);
            TEXTURE2D(_PreviewPatternTex);
            SAMPLER(sampler_PreviewPatternTex);
            TEXTURE2D(_OffenceMaskTex);
            SAMPLER(sampler_OffenceMaskTex);
            TEXTURE2D(_OffencePatternTex);
            SAMPLER(sampler_OffencePatternTex);
            TEXTURE2D(_HeightmapTexture);
            SAMPLER(sampler_HeightmapTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _MaskTex_ST;
                float4 _PreviewMaskTex_ST;
                float4 _PreviewPatternTex_ST;
                float4 _PatternTex_ST;
                float4 _OffenceMaskTex_ST;
                float4 _OffencePatternTex_ST;
                float _OffenceTile;
                float4 _HeightmapTexture_ST;
                float _HeightScale;
                float4 _MapWorldBounds;
                float _Lift;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;  // world xz (y ignored)
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 style : TEXCOORD1;
                float2 center : TEXCOORD2;     // centre-line xz: both edges take its terrain height
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float style : TEXCOORD1;
            };

            float TerrainHeight(float3 posWS)
            {
                float2 terrainUV = (posWS.xz - _MapWorldBounds.xy) / _MapWorldBounds.zw;
                float2 heightUV = TRANSFORM_TEX(float2(terrainUV.x, 1.0 - terrainUV.y), _HeightmapTexture);
                float h = SAMPLE_TEXTURE2D_LOD(_HeightmapTexture, sampler_HeightmapTexture, heightUV, 0).r;
                return (h - 0.5) * _HeightScale;
            }

            Varyings vert(Attributes input)
            {
                Varyings o;
                float3 p = input.positionOS;
                p.y = TerrainHeight(float3(input.center.x, 0.0, input.center.y)) + _Lift;
                o.positionCS = TransformWorldToHClip(p);
                o.color = input.color;
                o.uv = input.uv;
                o.style = input.style.x;
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                bool preview = i.style < 0.5;
                bool objective = i.style > 1.5;
                // HOI4 arrowTypes: unconfirmed = magnet_line (stripe textures, texture_loop 1), placed = defence_line
                // (texture_loop 3.2: one tile spans 3.2 widths). The texture's top row (the band) is the own side, v = 0.
                // Objective line = offence_line: band in the bottom rows at v = 0, the tooth tips (top rows) at v = 1.
                float2 uv = objective
                    ? float2(frac(i.uv.x / max(_OffenceTile, 0.05)), saturate(i.uv.y))
                    : float2(frac(i.uv.x / (preview ? 1.0 : 3.2)), 1.0 - saturate(i.uv.y));
                half4 mask, pattern;
                if (objective)
                {
                    mask = SAMPLE_TEXTURE2D_LOD(_OffenceMaskTex, sampler_OffenceMaskTex, uv, 0);
                    pattern = SAMPLE_TEXTURE2D_LOD(_OffencePatternTex, sampler_OffencePatternTex, uv, 0);
                }
                else if (preview)
                {
                    mask = SAMPLE_TEXTURE2D_LOD(_PreviewMaskTex, sampler_PreviewMaskTex, uv, 0);
                    pattern = SAMPLE_TEXTURE2D_LOD(_PreviewPatternTex, sampler_PreviewPatternTex, uv, 0);
                }
                else
                {
                    mask = SAMPLE_TEXTURE2D_LOD(_MaskTex, sampler_MaskTex, uv, 0);
                    pattern = SAMPLE_TEXTURE2D_LOD(_PatternTex, sampler_PatternTex, uv, 0);
                }
                float coverage = saturate(mask.r + mask.g + mask.b) * mask.a;
                clip(coverage - 0.002);
                float3 c = i.color.rgb;
                float grey = dot(c, float3(0.299, 0.587, 0.114));
                c = saturate(lerp(grey.xxx, c, 1.5));
                float3 rgb = saturate(pattern.rgb * c);
                float alpha = 1.0;
                return half4(rgb, coverage * alpha);
            }
            ENDHLSL
        }
    }
}
