// GAME: HOI4 unit move arrows (Hoi4MoveArrows), a port of HOI4's maparrow technique for the
// move_thick arrow type. The mesh is a ribbon built on the CPU in map-plane world xz; this shader
// drapes it on the terrain and samples HOI4's arrow textures: left half = body (tiles along the
// arrow, middle _BodyUvScale of the height, pattern scrolls toward the head), right half = head.
// Coverage = mask rgb * a; colour = grayscale pattern * arrow colour.
Shader "Hoi4/MoveArrow"
{
    Properties
    {
        _MaskTex ("Arrow Mask", 2D) = "white" {}
        _PatternTex ("Arrow Pattern", 2D) = "white" {}
        _HeightmapTexture ("Heightmap", 2D) = "gray" {}
        _HeightScale ("Height Scale", Float) = 10.0
        _MapWorldBounds ("Map World Bounds (min x, min z, size x, size z)", Vector) = (-5, -5, 10, 10)
        _ArrowColor ("Arrow Color", Color) = (0.78, 0.56, 0.16, 1)
        _ProgressColor ("Travelled Color", Color) = (1, 0.95, 0.78, 1)
        _ArrowParams ("Body uv scale, anim speed, root fade, lift", Vector) = (0.4, 0.1, 0.04, 0.05)
        _StripeStrength ("Moving stripe strength", Range(0, 1)) = 0.08
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+90"
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
            TEXTURE2D(_HeightmapTexture);
            SAMPLER(sampler_HeightmapTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _MaskTex_ST;
                float4 _PatternTex_ST;
                float4 _HeightmapTexture_ST;
                float _HeightScale;
                float4 _MapWorldBounds;
                float4 _ArrowColor;
                float4 _ProgressColor;
                float4 _ArrowParams;
                float _StripeStrength;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION; // world xz on the map plane (y ignored)
                float2 uv : TEXCOORD0;        // x: body = tiles along the arrow, head = 0..1 over the head; y: 0..1 across
                float2 info : TEXCOORD1;      // x: 1 = head, y: position along the arrow 0..1
                float2 fill : TEXCOORD2;      // x: travelled fraction of the arrow (0..1)
                float2 center : TEXCOORD3;    // centre-line xz of this cross-section
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 info : TEXCOORD1;
                float fill : TEXCOORD2;
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
                Varyings output;
                // Both edges take the terrain height at the centre line: per-vertex heights tilt the
                // wide head differently from the narrow body on uneven ground, so they misalign
                float3 p = input.positionOS;
                p.y = TerrainHeight(float3(input.center.x, 0.0, input.center.y)) + _ArrowParams.w;
                output.positionCS = TransformWorldToHClip(p);
                output.uv = input.uv;
                output.info = input.info;
                output.fill = input.fill.x;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                float isHead = input.info.x > 0.5 ? 1.0 : 0.0;
                float across = saturate(input.uv.y);
                float bodyU = frac(input.uv.x - _Time.y * _ArrowParams.y) * 0.5;
                float bodyV = (across - 0.5) * _ArrowParams.x + 0.5;
                float2 uv = isHead > 0.5
                    ? float2(0.5 + clamp(input.uv.x, 0.0, 0.998) * 0.5, across)
                    : float2(clamp(bodyU, 0.001, 0.499), bodyV);

                // No mips: the body u wraps inside the left half, a mip lookup would bleed the head in
                half4 mask = SAMPLE_TEXTURE2D_LOD(_MaskTex, sampler_MaskTex, uv, 0);
                half4 pattern = SAMPLE_TEXTURE2D_LOD(_PatternTex, sampler_PatternTex, uv, 0);
                float coverage = saturate(mask.r + mask.g + mask.b) * mask.a;
                coverage *= isHead > 0.5 ? 1.0 : saturate(input.info.y / max(_ArrowParams.z, 1e-3));
                clip(coverage - 0.002);

                // HOI4 FxMask: diagonal stripes running toward the head on the body
                float stripe = cos((input.uv.x * 5.0 - _Time.y * _ArrowParams.y * 5.0) * 25.0 * 0.7071 + across * 25.0 * 0.7071 * 0.2);
                stripe = smoothstep(0.0, 1.0, stripe * 1.7) * (1.0 - isHead);

                // HOI4 boosts the arrow colour's saturation (x1.5) before multiplying the pattern
                // Travelled part (toward the next province) in the light colour, soft edge
                float travelled = 1.0 - smoothstep(input.fill - 0.004, input.fill + 0.004, input.info.y);
                travelled *= step(1e-4, input.fill);
                float3 c = lerp(_ArrowColor.rgb, _ProgressColor.rgb, travelled);
                float grey = dot(c, float3(0.299, 0.587, 0.114));
                c = saturate(lerp(grey.xxx, c, 1.5));
                float3 rgb = saturate(pattern.rgb * c * 1.4) * (1.0 - _StripeStrength * stripe);
                return half4(rgb, coverage * _ArrowColor.a);
            }
            ENDHLSL
        }
    }
}
