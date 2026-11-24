Shader "Hoi4/Precipitation"
{
    // GAME: falling weather when zoomed in, from HOI4 screenshots (its particles, gfx/particles/environment): sparse thin
    // rain streaks falling from the top left to the bottom right of the screen (heavy rain: denser, brighter), small white
    // snowflakes drifting down (blizzard: denser), occasional lightning over thunderstorms. Further out the weather clouds take over (engine atmosphere cloud
    // mask, Hoi4WeatherSky).
    //
    // Procedural on one map quad (Hoi4WeatherSky draws it), where the mask has weather:
    //   - Rain and snow laid out in map px around the map point at the screen centre (_Hoi4PrecipView), with screen
    //     axes: they fall straight down the screen like HOI4's particles (painted on the tilted ground they would
    //     converge toward the horizon), yet pan and zoom with the map. Sizes like HOI4's (rain particles 6 map px,
    //     falling 25-45 map px/s). Cells hold at most one streak or flake at a hashed spot, the layer scrolling down; two
    //     layers of each. A streak has a bright head at the bottom and a tail fading upward, and reads the mask at its
    //     own spot, so it is whole or absent, never cut by the soft edge of a rain area. All layers fall at one speed
    //     (at different speeds, overlapping streaks of two layers drifted apart and read as one streak splitting).
    //   - Lightning in map space: now and then a patch of a thunderstorm lights up for a moment, shaped by cloud noise
    //     (HOI4: a flashing cloud puff, lightning_storm.asset), kept inside its 120 map px cell.
    // _Hoi4PrecipTex (Hoi4WeatherSky, map space): r rain, g snow, b thunderstorm, each 0..1, blurred.
    // _Hoi4Precip: x opacity, y..z zoom (screen px per map px: hidden at or below y, full at or above z), w density.
    Properties
    {
        _WorldPerMapPx ("World Units per Map Pixel", Float) = 0.01
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent-54"
            "RenderPipeline" = "UniversalPipeline"
        }

        Pass
        {
            Name "Hoi4Precipitation"

            Blend One OneMinusSrcAlpha
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/Archon-Engine/Shaders/Includes/DayNight.hlsl"

            TEXTURE2D(_Hoi4PrecipTex);
            SAMPLER(sampler_Hoi4PrecipTex);
            float4 _Hoi4Precip;
            float4 _Hoi4PrecipView;    // Hoi4WeatherSky, every frame: xy = map px at the screen centre (y from the south),
                                       // zw = viewport units per map px there (x across, y up the screen); z 0 = none

            CBUFFER_START(UnityPerMaterial)
                float _WorldPerMapPx;
                float4 _MapWorldBounds;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float Hash(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            float ValueNoise(float2 x)
            {
                float2 i = floor(x);
                float2 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);
                return lerp(lerp(Hash(i), Hash(i + float2(1, 0)), f.x), lerp(Hash(i + float2(0, 1)), Hash(i + 1.0), f.x), f.y);
            }

            float CloudNoise(float2 x)
            {
                return 0.55 * ValueNoise(x) + 0.3 * ValueNoise(x * 2.1 + 7.3) + 0.15 * ValueNoise(x * 4.3 - 3.1);
            }

            // Map size in map px, for reading the mask at a streak's own spot
            float2 MapSizePx() { return _MapWorldBounds.zw / _WorldPerMapPx; }

            // Scroll offset (map px) for a layer falling at `speed` map px/s, wrapped every 64 cells (float precision over
            // long sessions); cell rows hash with period 64 (RowId), so the wrap is seamless
            float Scroll(float cell, float speed) { return fmod(_Time.y, cell * 64.0 / speed) * speed; }
            float2 RowId(float2 id) { return float2(id.x, id.y - 64.0 * floor(id.y / 64.0)); }

            // One rain layer. p: view-anchored pattern position in map px (x right, y up the screen ~ north). Streaks
            // `len` map px long in cells of `cell`, falling at `speed` map px/s (fixed per layer); the slant is a shear, so
            // the layer scrolls along y only. Each streak reads the mask at its own spot (whole streaks, never cut by the
            // mask's edge): it shows when its hash is under density * rain there, doubled in a thunderstorm.
            float RainLayer(float2 p, float cell, float len, float speed, float halfWidth, float density, float seed)
            {
                const float slant = 1.0;                             // HOI4: 45 degrees, from the top left to the bottom right
                float scroll = Scroll(cell, speed);
                float2 q = float2(p.x + p.y * slant, p.y + scroll);
                float2 id = floor(q / cell);
                float2 hid = RowId(id);
                float h = Hash(hid + seed);
                if (h > density) return 0;
                float margin = len * 0.5 + halfWidth + 0.2;
                float2 centre = margin + float2(Hash(hid + seed + 1.7), Hash(hid + seed + 5.3)) * max(cell - 2.0 * margin, 0.01);
                float2 cq = id * cell + centre;                      // the streak, in layer coordinates
                float cy = cq.y - scroll;
                float2 at = float2(cq.x - cy * slant, cy) / MapSizePx();
                float4 m = SAMPLE_TEXTURE2D_LOD(_Hoi4PrecipTex, sampler_Hoi4PrecipTex, at, 0);
                float limit = density * m.r * (1.0 + saturate(m.b));
                if (h > limit) return 0;
                float edge = saturate((limit - h) / (0.25 * density));     // fades in and out at a rain edge, no popping
                float2 d = q - cq;
                // a falling drop: bright head at the bottom, tail fading upward
                float u = (d.y + len * 0.5) / len;                    // 0 head .. 1 tail end
                float a = saturate(1.0 - u) * saturate(u * 8.0);
                float b = saturate(1.0 - abs(d.x) / halfWidth);
                return a * a * b * edge * lerp(1.0, 1.3, saturate(m.b));   // brighter in a storm
            }

            // One snow layer: soft flakes of `radius` map px drifting down at `speed` map px/s (fixed per layer) with a
            // little sway; a flake shows when its hash is under density * snow at its spot (a blizzard is 1, snow 0.6)
            float SnowLayer(float2 p, float cell, float radius, float speed, float density, float seed)
            {
                float scroll = Scroll(cell, speed);
                float sway = sin(_Time.y * 0.7 + seed) * cell * 0.3;
                float2 q = p + float2(sway, scroll);
                float2 id = floor(q / cell);
                float2 hid = RowId(id);
                float h = Hash(hid + seed);
                if (h > density) return 0;
                float margin = radius + cell * 0.15 + 0.2;
                float2 centre = margin + float2(Hash(hid + seed + 2.1), Hash(hid + seed + 8.9)) * max(cell - 2.0 * margin, 0.01);
                centre.x += sin(_Time.y * 2.0 + h * 40.0) * cell * 0.12;
                float2 cq = id * cell + centre;
                float2 at = float2(cq.x - sway, cq.y - scroll) / MapSizePx();
                float4 m = SAMPLE_TEXTURE2D_LOD(_Hoi4PrecipTex, sampler_Hoi4PrecipTex, at, 0);
                float limit = density * m.g;
                if (h > limit) return 0;
                float r = length(q - cq) / radius;
                return saturate(1.0 - r * r) * saturate((limit - h) / (0.25 * density));
            }

            // Lightning in map px: now and then a cloud-shaped patch of a storm cell lights up, with a flicker
            float Lightning(float2 p)
            {
                const float cell = 120.0;
                const float radius = 38.0;                           // stays inside the cell (no square edges)
                float2 id = floor(p / cell);
                float h = Hash(id + 31.0);
                float clock = _Time.y * 0.3 + h * 7.0;
                float slot = floor(clock);
                if (Hash(id + slot * 1.37) < 0.8) return 0;
                float phase = frac(clock);
                float flash = saturate(1.0 - phase / 0.04) + 0.7 * saturate(1.0 - abs(phase - 0.08) / 0.02)
                            + 0.4 * saturate(1.0 - abs(phase - 0.13) / 0.015);
                if (flash <= 0.0) return 0;
                float2 jitter = float2(Hash(id + slot), Hash(id - slot)) - 0.5;
                float2 centre = (id + 0.5) * cell + jitter * (cell - 2.0 * radius - 4.0);
                float r = length(p - centre) / radius;
                float shape = saturate(1.0 - r * r);
                float cloud = smoothstep(0.35, 0.8, CloudNoise((p - centre) / 12.0 + slot * 3.1));
                return flash * shape * (0.35 + 0.65 * cloud);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float3 posWS = input.positionWS;
                float4 cs = TransformWorldToHClip(posWS);
                float worldPerScreenPx = 2.0 * max(cs.w, 1e-3) / (abs(UNITY_MATRIX_P[1][1]) * _ScreenParams.y);
                float screenPxPerMapPx = _WorldPerMapPx / worldPerScreenPx;
                float2 zoom = _Hoi4Precip.z > _Hoi4Precip.y ? _Hoi4Precip.yz : float2(1.0, 2.0);
                float fade = smoothstep(zoom.x, zoom.y, screenPxPerMapPx);
                if (fade <= 0.001) discard;

                float2 mapUV = ArchonWorldToMapUV(posWS, _MapWorldBounds);
                float4 m = SAMPLE_TEXTURE2D_LOD(_Hoi4PrecipTex, sampler_Hoi4PrecipTex, mapUV, 0);
                if (m.r + m.g + m.b <= 0.01) discard;

                // View-anchored pattern position in map px: around the map point at the screen centre, screen-aligned
                // (rain falls straight down the screen), so it pans and zooms with the map
                if (_Hoi4PrecipView.z <= 0) discard;
                float2 ndc = cs.xy / cs.w;
                ndc.y *= _ProjectionParams.x;                               // up the screen, flipped targets too
                float2 p = _Hoi4PrecipView.xy + ndc * 0.5 / _Hoi4PrecipView.zw;
                float2 pxPerUnit = _Hoi4PrecipView.zw * _ScreenParams.xy;   // screen px per pattern map px
                float density = _Hoi4Precip.w > 0 ? _Hoi4Precip.w : 1.0;

                float rain = 0;
                if (m.r > 0.01)
                {
                    float halfWidth = 1.1 / pxPerUnit.x;                   // ~1.5 screen px across the 45-degree streak
                    rain = RainLayer(p, 9.0, 4.0, 48.0, halfWidth, 0.12 * density, 0.0)
                         + 0.7 * RainLayer(p, 6.0, 2.8, 48.0, halfWidth * 0.8, 0.12 * density, 17.0);
                }
                float snow = 0;
                if (m.g > 0.01)
                {
                    float flakeR = max(0.15, 1.1 / pxPerUnit.y);           // at least about two screen px across
                    snow = SnowLayer(p, 4.0, flakeR, 4.5, 0.25 * density, 3.0)
                         + 0.7 * SnowLayer(p, 2.6, flakeR * 0.7, 4.5, 0.22 * density, 11.0);
                }
                float flash = m.b > 0.01 ? Lightning(mapUV * MapSizePx()) * saturate(m.b * 1.5) : 0;

                float opacity = (_Hoi4Precip.x > 0 ? _Hoi4Precip.x : 0.6) * fade;
                float a = saturate(rain * 0.55 + snow * 0.85) * opacity;
                float3 c = ArchonApplyNight(float3(0.9, 0.92, 1.0), mapUV, 1.0) * a;
                c += flash * 0.4 * fade * float3(0.85, 0.88, 1.0);           // additive: lights up the cloud below
                return half4(c, a);
            }
            ENDHLSL
        }
    }
}
