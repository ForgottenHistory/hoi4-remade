// GAME: HOI4 on-map division counters (Hoi4UnitCounters). Each quad's four vertices sit at the
// counter anchor on the map plane; the vertex shader lifts it to the terrain height and stands
// the counter up on it, facing the camera, sized in screen pixels at the anchor. Size and fade follow the zoom (screen px per map px): full size
// when zoomed in, shrinking as the camera rises, hidden where province borders fade out.
Shader "Hoi4/UnitCounter"
{
    Properties
    {
        _MainTex ("Counter Atlas", 2D) = "white" {}
        _HeightmapTexture ("Heightmap", 2D) = "gray" {}
        _HeightScale ("Height Scale", Float) = 10.0
        _MapWorldBounds ("Map World Bounds (min x, min z, size x, size z)", Vector) = (-5, -5, 10, 10)
        _CounterParams ("Pixel scale, hidden zoom, visible zoom, lift", Vector) = (1.5, 3, 5, 0)
        _CounterSize ("Full size zoom, min size fraction, world per map px", Vector) = (12, 0.45, 0.1, 0)
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent+100"
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

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);
            TEXTURE2D(_HeightmapTexture);
            SAMPLER(sampler_HeightmapTexture);

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _HeightmapTexture_ST;
                float _HeightScale;
                float4 _MapWorldBounds;
                float4 _CounterParams;
                float4 _CounterSize;
            CBUFFER_END

            struct Attributes
            {
                float3 positionOS : POSITION;  // counter anchor on the map plane (world)
                float2 uv : TEXCOORD0;         // atlas uv
                float2 offset : TEXCOORD1;     // corner offset from the anchor, counter pixels (x right, y up)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float fade : TEXCOORD1;
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
                float3 anchor = input.positionOS;
                anchor.y = TerrainHeight(anchor) + _CounterParams.w;

                float4 cs = TransformWorldToHClip(anchor);

                // Zoom as screen pixels per map pixel at the anchor (same measure as border fades)
                float worldPerScreenPx = 2.0 * max(cs.w, 1e-3) / (abs(UNITY_MATRIX_P[1][1]) * _ScreenParams.y);
                float zoom = _CounterSize.z / worldPerScreenPx;
                float size = clamp(zoom / _CounterSize.x, _CounterSize.y, 1.0);
                float2 pixels = input.offset * _CounterParams.x * size;
                // Stand the counter up on the map: a camera-facing quad in world space whose bottom
                // edge rests on the anchor (offsets have y = 0 at the bottom), sized from screen
                // pixels at the anchor. Facing the camera avoids the squash of lying flat under
                // the tilted camera; standing on the ground point keeps it pinned to the map.
                float3 camRight = UNITY_MATRIX_V[0].xyz;
                float3 camUp = UNITY_MATRIX_V[1].xyz;
                float3 corner = anchor + (camRight * pixels.x + camUp * pixels.y) * worldPerScreenPx;
                cs = TransformWorldToHClip(corner);
                if (cs.w <= 0.0) cs = float4(0, 0, -1, 1); // behind the camera
                output.positionCS = cs;
                output.uv = input.uv;

                output.fade = saturate((zoom - _CounterParams.y) / max(_CounterParams.z - _CounterParams.y, 1e-3));
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 c = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, input.uv);
                c.a *= input.fade;
                clip(c.a - 0.002);
                return c;
            }
            ENDHLSL
        }
    }
}
