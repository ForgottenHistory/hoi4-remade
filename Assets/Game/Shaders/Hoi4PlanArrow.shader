// GAME: offensive battle plan arrows (Hoi4BattlePlans). HOI4 draws them with the move_thick textures
// (default_mask / default_pattern) in the army colour, wider than a unit's move arrow, with a soft drop
// shadow. The mesh is a ribbon built on the CPU in map-plane world xz and draped on the terrain here.
//   uv0: x = body: tiles along the arrow / head: 0..1 over the head; y = 0..1 across
//   uv1: x = 1 head, y = root fade-in (0 at the root, 1 = fully drawn)
//   uv2: x = 1 shadow copy (drawn first, offset, dark and blurred)
//   uv3: centre-line xz (both edges take its terrain height)
//   uv4: texture uv per map px (u, v), for the shadow blur. The shadow's mesh is padded (uv past 0..1) so
//        the blur fades out instead of being cut off at the mesh edge
//   colour: the army colour
// Texture layout: body columns 0..0.63 (uniform; its visible band the middle 0.3 of the height, the
// ribbon maps the middle body_uv_scale = 0.4), head columns 0.63..1 (a chevron, the head mesh is
// ribbon / 0.4 wide and maps the full height). The pattern's white shape sits a little inside the mask: that dark rim is the outline.
// Prototype: Tools/prototypes/battleplan/offensive.py
Shader "Hoi4/PlanArrow"
{
    Properties
    {
        _MaskTex ("Arrow Mask", 2D) = "white" {}
        _UnassignedMaskTex ("Arrow Unassigned Mask", 2D) = "white" {}
        _PatternTex ("Arrow Pattern", 2D) = "white" {}
        _HeightmapTexture ("Heightmap", 2D) = "gray" {}
        _HeightScale ("Height Scale", Float) = 10.0
        _MapWorldBounds ("Map World Bounds (min x, min z, size x, size z)", Vector) = (-5, -5, 10, 10)
        _RimColor ("Rim Colour", Color) = (0.16, 0.18, 0.24, 1)
        _ShadowColor ("Shadow Colour", Color) = (0.05, 0.06, 0.10, 0.55)
        _ShadowBlur ("Shadow blur radius (map px)", Float) = 1.0
        _ArrowParams ("Body uv scale, opacity, root fade, lift", Vector) = (0.4, 0.8, 1.0, 0.07)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+85"
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
            TEXTURE2D(_UnassignedMaskTex);
            SAMPLER(sampler_UnassignedMaskTex);
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
                float4 _RimColor;
                float4 _ShadowColor;
                float _ShadowBlur;
                float4 _ArrowParams;
            CBUFFER_END

            static const float HeadX = 0.63;

            struct Attributes
            {
                float3 positionOS : POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 info : TEXCOORD1;
                float2 kind : TEXCOORD2;
                float2 center : TEXCOORD3;
                float2 blur : TEXCOORD4;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 color : COLOR;
                float2 uv : TEXCOORD0;
                float2 info : TEXCOORD1;
                float shadow : TEXCOORD2;
                float2 blur : TEXCOORD3;
                float unassigned : TEXCOORD4; // move_thick's alternative mask: an offensive nobody is assigned to
            };

            float TerrainHeight(float2 xz)
            {
                float2 terrainUV = (xz - _MapWorldBounds.xy) / _MapWorldBounds.zw;
                float2 heightUV = TRANSFORM_TEX(float2(terrainUV.x, 1.0 - terrainUV.y), _HeightmapTexture);
                float h = SAMPLE_TEXTURE2D_LOD(_HeightmapTexture, sampler_HeightmapTexture, heightUV, 0).r;
                return (h - 0.5) * _HeightScale;
            }

            Varyings vert(Attributes input)
            {
                Varyings o;
                float3 p = input.positionOS;
                p.y = TerrainHeight(input.center) + _ArrowParams.w;
                o.positionCS = TransformWorldToHClip(p);
                o.color = input.color;
                o.uv = input.uv;
                o.info = input.info;
                o.shadow = input.kind.x;
                o.unassigned = input.kind.y;
                o.blur = input.blur;
                return o;
            }

            float2 TexUV(float2 uv, float isHead)
            {
                float across = saturate(uv.y);
                return isHead > 0.5
                    ? float2(HeadX + (1.0 - HeadX) * clamp(uv.x, 0.0, 0.998), across)
                    : float2(0.1 + 0.45 * frac(uv.x), (across - 0.5) * _ArrowParams.x + 0.5);
            }

            float Coverage(float2 tuv, float unassigned)
            {
                half4 m = unassigned > 0.5
                    ? SAMPLE_TEXTURE2D_LOD(_UnassignedMaskTex, sampler_UnassignedMaskTex, tuv, 0)
                    : SAMPLE_TEXTURE2D_LOD(_MaskTex, sampler_MaskTex, tuv, 0);
                return saturate(m.r + m.g + m.b) * m.a;
            }

            half4 frag(Varyings i) : SV_Target
            {
                float isHead = i.info.x > 0.5 ? 1.0 : 0.0;
                float2 tuv = TexUV(i.uv, isHead);
                float fade = isHead > 0.5 ? 1.0 : smoothstep(0.0, 1.0, i.info.y / max(_ArrowParams.z, 1e-3)); // a soft start

                if (i.shadow > 0.5)
                {
                    // Unclamped uv: the padded mesh reaches past the shape (the clamped texture is empty there)
                    float2 suv = isHead > 0.5
                        ? float2(HeadX + (1.0 - HeadX) * i.uv.x, i.uv.y)
                        : float2(0.1 + 0.45 * frac(i.uv.x), (i.uv.y - 0.5) * _ArrowParams.x + 0.5);
                    // Gaussian-weighted disc of taps, _ShadowBlur map px across (centre, two rings)
                    float c = 0.0, wsum = 0.0;
                    [unroll] for (int k = 0; k < 19; k++)
                    {
                        float ring = k == 0 ? 0.0 : (k <= 6 ? 0.5 : 1.0);
                        float ang = k <= 6 ? k * 1.0472 : k * 0.5236 + 0.2618;
                        float w = exp(-ring * ring * 2.0);
                        float2 tap = suv + float2(cos(ang), sin(ang)) * ring * _ShadowBlur * i.blur;
                        // The head's shadow takes only the head's own columns; the body's shadow covers the rest
                        float keep = isHead > 0.5 ? step(HeadX, tap.x) : 1.0;
                        c += Coverage(tap, i.unassigned) * keep * w;
                        wsum += w;
                    }
                    c = c / wsum * fade;
                    clip(c - 0.002);
                    return half4(_ShadowColor.rgb, c * _ShadowColor.a);
                }

                float coverage = Coverage(tuv, i.unassigned) * fade;
                clip(coverage - 0.002);
                half4 pattern = SAMPLE_TEXTURE2D_LOD(_PatternTex, sampler_PatternTex, tuv, 0);
                float lum = dot(pattern.rgb, float3(0.299, 0.587, 0.114));
                float3 rgb = lerp(_RimColor.rgb, saturate(i.color.rgb * 1.05), lum);
                return half4(rgb, coverage * _ArrowParams.y);
            }
            ENDHLSL
        }
    }
}
