using Map.Rendering.Atmosphere;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hoi4.Weather
{
    /// <summary>
    /// GAME (presentation): the weather in the sky, from HOI4 screenshots:
    ///
    ///   - Clouds, through the engine atmosphere's cloud mask (MapAtmosphereRenderer.SetCloudMask): a dark cloud mass
    ///     over thunderstorms, white clouds over a blizzard, lighter grey over rain and snow, sand over a sandstorm;
    ///     a grey mist when zoomed in. Prototype: Tools/prototypes/weather/weather_clouds.py (where they gather) and
    ///     cloud_volume.py (their volume and light, which the engine draws).
    ///   - Lightning inside the thunderclouds (MapAtmosphereRenderer.SetCloudLights): now and then a spot of a storm
    ///     in view flashes, twice in quick succession, and glows through the cloud around it.
    ///   - Falling weather when zoomed in (Hoi4/Precipitation on a map quad): rain streaks, snowflakes, lightning.
    ///
    /// Both masks have one texel per Texel map px, from the weather of the province under it (Hoi4Weather.KindAt;
    /// snowfall above 1 degree counts as rain, as the simulation treats it), blurred across Blur texels so region
    /// outlines do not show. Cloud mask (sRGB): rgb = cloud colour, a = coverage (WeatherCloud). Precipitation mask
    /// (_Hoi4PrecipTex, linear): r rain, g snow, b thunderstorm. Repainted when the weather changes (once a game day)
    /// and cross-faded over FadeSeconds (real time, presentation). Style: the atmosphere section's mask* fields and
    /// _Hoi4Precip in the visual style's parameters.
    /// </summary>
    public sealed class Hoi4WeatherSky : MonoBehaviour
    {
        const int Texel = 16;        // region-sized; the noise adds the detail (and the per-frame fade stays cheap)
        const int Blur = 2;          // box blur radius in texels, 2 passes (about 40 map px of soft edge)
        const float FadeSeconds = 2f;
        const int PrecipQueue = 2946; // over the engine clouds (2945), under labels (2950)

        // Hoi4Weather.Kind -> cloud colour (sRGB) and coverage (the share of the sky under cloud there)
        static readonly Color[] WeatherCloud =
        {
            new Color(0f, 0f, 0f, 0f),             // None
            new Color(0.62f, 0.64f, 0.68f, 0.40f), // RainLight
            new Color(0.2f, 0.21f, 0.25f, 0.60f),  // RainHeavy (thunderstorm: the dark mass)
            new Color(0.85f, 0.87f, 0.92f, 0.25f), // Snow
            new Color(0.97f, 0.97f, 1f, 0.55f),    // Blizzard (white clouds)
            new Color(0.8f, 0.66f, 0.45f, 0.55f),  // Sandstorm
        };
        // Hoi4Weather.Kind -> rain, snow, thunderstorm
        static readonly Vector3[] WeatherPrecip =
        {
            new Vector3(0f, 0f, 0f), new Vector3(0.6f, 0f, 0f), new Vector3(1f, 0f, 1f),
            new Vector3(0f, 0.6f, 0f), new Vector3(0f, 1f, 0f), new Vector3(0f, 0f, 0f),
        };

        static readonly int PrecipTexId = Shader.PropertyToID("_Hoi4PrecipTex");
        static readonly int MapWorldBoundsId = Shader.PropertyToID("_MapWorldBounds");
        static readonly int WorldPerMapPxId = Shader.PropertyToID("_WorldPerMapPx");
        static readonly int PrecipViewId = Shader.PropertyToID("_Hoi4PrecipView");

        Texture2D cloudTexture, precipTexture;
        ushort[] provinceOf;         // texel -> province id
        int width, height;
        float[] from, to, scratch;   // 8 floats per texel: cloud rgba (rgb premultiplied while blurring), rain, snow, storm, 0
        Color32[] cloudPixels, precipPixels;
        int shownVersion = -1;
        float fadeStart = -100f;
        bool fading;
        Material precipMaterial;
        Mesh quad;

        // Lightning in the clouds. Real seconds, not game time: it is presentation and should flicker the same at
        // every game speed and while paused, like the rain.
        const float FlashSeconds = 0.4f;       // a flash: two strokes and an afterglow
        const float FlashRadius = 55f;         // map px the glow reaches through the cloud
        const float FlashIntensity = 0.9f;
        static readonly Color FlashColor = new Color(0.75f, 0.82f, 1f, 1f);
        readonly Vector4[] flashes = new Vector4[MapAtmosphereRenderer.MaxCloudLights]; // x, y (from the north), radius, start time
        readonly Vector4[] lights = new Vector4[MapAtmosphereRenderer.MaxCloudLights];
        float nextFlash;
        bool lightsOn;
        Bounds mapBounds;
        float worldPerMapPx;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4WeatherSky");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4WeatherSky>();
        }

        void OnDestroy()
        {
            MapAtmosphereRenderer.SetCloudMask(null);
            MapAtmosphereRenderer.SetCloudLights(null, 0, Color.black);
            if (cloudTexture != null) Destroy(cloudTexture);
            if (precipTexture != null) Destroy(precipTexture);
            if (precipMaterial != null) Destroy(precipMaterial);
            if (quad != null) Destroy(quad);
        }

        bool Ready()
        {
            if (cloudTexture != null) return true;
            var engine = Engine.ArchonEngine.Instance;
            var tm = engine?.TextureManager;
            if (tm == null || tm.MapWidth <= 0 || engine.MapMeshRenderer == null || !Hoi4Weather.Available) return false;
            width = tm.MapWidth / Texel;
            height = tm.MapHeight / Texel;
            provinceOf = new ushort[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    provinceOf[y * width + x] = tm.GetProvinceID(x * Texel + Texel / 2, y * Texel + Texel / 2); // y from the south
            from = new float[width * height * 8];
            to = new float[width * height * 8];
            scratch = new float[width * height * 8];
            cloudPixels = new Color32[width * height];
            precipPixels = new Color32[width * height];
            cloudTexture = NewTexture("Hoi4WeatherClouds", linear: false);
            precipTexture = NewTexture("Hoi4Precipitation", linear: true);
            MapAtmosphereRenderer.SetCloudMask(cloudTexture);
            Shader.SetGlobalTexture(PrecipTexId, precipTexture);

            var shader = Shader.Find("Hoi4/Precipitation");
            if (shader != null)
            {
                var bounds = engine.MapMeshRenderer.bounds;
                mapBounds = bounds;
                worldPerMapPx = bounds.size.x / Mathf.Max(tm.MapWidth, 1);
                precipMaterial = new Material(shader) { name = "Hoi4Precipitation", renderQueue = PrecipQueue };
                precipMaterial.SetVector(MapWorldBoundsId, new Vector4(bounds.min.x, bounds.min.z, bounds.size.x, bounds.size.z));
                precipMaterial.SetFloat(WorldPerMapPxId, bounds.size.x / Mathf.Max(tm.MapWidth, 1));
                quad = Quad(bounds);
            }
            else
            {
                Debug.LogError("Hoi4WeatherSky: shader Hoi4/Precipitation not found");
            }
            return true;
        }

        Texture2D NewTexture(string name, bool linear)
        {
            var t = new Texture2D(width, height, TextureFormat.RGBA32, false, linear)
            {
                filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp, name = name
            };
            t.SetPixels32(new Color32[width * height]);
            t.Apply(false);
            return t;
        }

        static Mesh Quad(Bounds b)
        {
            float y = b.max.y;
            var m = new Mesh { name = "Hoi4Precipitation" };
            m.SetVertices(new[] { new Vector3(b.min.x, y, b.min.z), new Vector3(b.min.x, y, b.max.z),
                                  new Vector3(b.max.x, y, b.max.z), new Vector3(b.max.x, y, b.min.z) });
            m.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            m.bounds = new Bounds(b.center, b.size + Vector3.up * 1000f);
            return m;
        }

        void Update()
        {
            if (!Ready()) return;
            if (Hoi4Weather.Version != shownVersion)
            {
                bool first = shownVersion < 0;
                shownVersion = Hoi4Weather.Version;
                float k = first ? 1f : Smooth(Mathf.Clamp01((Time.unscaledTime - fadeStart) / FadeSeconds));
                for (int i = 0; i < from.Length; i++) from[i] = Mathf.Lerp(from[i], to[i], k); // fade from what is shown
                Paint();
                if (first) System.Array.Copy(to, from, to.Length);
                fadeStart = Time.unscaledTime;
                fading = true;
            }
            if (fading)
            {
                float t = Smooth(Mathf.Clamp01((Time.unscaledTime - fadeStart) / FadeSeconds));
                for (int i = 0, p = 0; p < cloudPixels.Length; p++, i += 8)
                {
                    cloudPixels[p] = new Color32(Lerp(i, t), Lerp(i + 1, t), Lerp(i + 2, t), Lerp(i + 3, t));
                    precipPixels[p] = new Color32(Lerp(i + 4, t), Lerp(i + 5, t), Lerp(i + 6, t), 255);
                }
                cloudTexture.SetPixels32(cloudPixels);
                cloudTexture.Apply(false);
                precipTexture.SetPixels32(precipPixels);
                precipTexture.Apply(false);
                if (t >= 1f) fading = false;
            }
            Vector4 view = View();
            Lightning(view);
            if (precipMaterial != null)
            {
                Shader.SetGlobalVector(PrecipViewId, view);
                Graphics.DrawMesh(quad, Matrix4x4.identity, precipMaterial, 0, null, 0, null, ShadowCastingMode.Off, false);
            }
        }

        /// <summary>Starts a flash now and then at a thunderstorm spot in view, and hands the lit ones to the clouds.</summary>
        void Lightning(Vector4 view)
        {
            float now = Time.unscaledTime;
            if (now >= nextFlash && view.z > 0f)
            {
                nextFlash = now + Random.Range(0.3f, 1.6f);
                // the view's rough extent on the map (it is wider at the far edge; near enough for picking a spot)
                float halfX = 0.6f / view.z, halfY = 0.6f / view.w;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    float x = view.x + Random.Range(-halfX, halfX), y = view.y + Random.Range(-halfY, halfY); // y from the south
                    int tx = Mathf.FloorToInt(x / Texel), ty = Mathf.FloorToInt(y / Texel);
                    if (tx < 0 || ty < 0 || tx >= width || ty >= height) continue;
                    if (to[(ty * width + tx) * 8 + 6] < 0.5f) continue; // not a thunderstorm
                    int slot = 0;
                    for (int i = 1; i < flashes.Length; i++)
                        if (flashes[i].w < flashes[slot].w) slot = i; // the oldest
                    flashes[slot] = new Vector4(x, height * Texel - y, FlashRadius * Random.Range(0.7f, 1.3f), now);
                    break;
                }
            }
            int count = 0;
            for (int i = 0; i < flashes.Length; i++)
            {
                float age = now - flashes[i].w;
                if (flashes[i].z <= 0f || age < 0f || age > FlashSeconds) continue;
                // two strokes, the second weaker, over a short afterglow
                float strokes = Mathf.Clamp01(1f - age / 0.07f) + 0.7f * Mathf.Clamp01(1f - Mathf.Abs(age - 0.14f) / 0.04f);
                float glow = 0.25f * Mathf.Clamp01(1f - age / FlashSeconds);
                lights[count++] = new Vector4(flashes[i].x, flashes[i].y, flashes[i].z, (strokes + glow) * FlashIntensity);
            }
            if (count > 0 || lightsOn) MapAtmosphereRenderer.SetCloudLights(lights, count, FlashColor);
            lightsOn = count > 0;
        }

        /// <summary>
        /// The falling weather's anchor: the map point at the screen centre (map px, y from the south) and how many
        /// viewport units one map px spans there (x across, y up the screen). z = 0 when the view misses the map.
        /// </summary>
        Vector4 View()
        {
            var cam = Camera.main;
            if (cam == null) return Vector4.zero;
            var ray = cam.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0f));
            var plane = new Plane(Vector3.up, new Vector3(0f, mapBounds.max.y, 0f));
            if (!plane.Raycast(ray, out float distance)) return Vector4.zero;
            var centre = ray.GetPoint(distance);
            var v0 = cam.WorldToViewportPoint(centre);
            var vx = cam.WorldToViewportPoint(centre + Vector3.right * worldPerMapPx);
            var vy = cam.WorldToViewportPoint(centre + Vector3.forward * worldPerMapPx);
            float kx = Mathf.Abs(vx.x - v0.x), ky = Mathf.Abs(vy.y - v0.y);
            if (kx < 1e-7f || ky < 1e-7f) return Vector4.zero;
            return new Vector4((centre.x - mapBounds.min.x) / worldPerMapPx, (centre.z - mapBounds.min.z) / worldPerMapPx, kx, ky);
        }

        static float Smooth(float t) => t * t * (3f - 2f * t);
        byte Lerp(int i, float t) => (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.Lerp(from[i], to[i], t) * 255f), 0, 255);

        /// <summary>The weather per texel into `to`, blurred.</summary>
        void Paint()
        {
            for (int p = 0, i = 0; p < provinceOf.Length; p++, i += 8)
            {
                ushort province = provinceOf[p];
                var kind = province == 0 ? Hoi4Weather.Kind.None : Hoi4Weather.KindAt(province);
                if ((kind == Hoi4Weather.Kind.Snow || kind == Hoi4Weather.Kind.Blizzard) && Hoi4Weather.Temperature(province).ToFloat() > 1f)
                    kind = kind == Hoi4Weather.Kind.Snow ? Hoi4Weather.Kind.RainLight : Hoi4Weather.Kind.RainHeavy;
                var c = WeatherCloud[(int)kind];
                var w = WeatherPrecip[(int)kind];
                to[i] = c.r * c.a; to[i + 1] = c.g * c.a; to[i + 2] = c.b * c.a; to[i + 3] = c.a;
                to[i + 4] = w.x; to[i + 5] = w.y; to[i + 6] = w.z; to[i + 7] = 0f;
            }
            for (int pass = 0; pass < 2; pass++)
            {
                BoxBlur(to, scratch, true);
                BoxBlur(scratch, to, false);
            }
            for (int i = 0; i < to.Length; i += 8)
            {
                float a = to[i + 3];
                if (a > 1e-4f) { to[i] /= a; to[i + 1] /= a; to[i + 2] /= a; }
                else { to[i] = to[i + 1] = to[i + 2] = 0f; }
            }
        }

        /// <summary>Running-sum box blur of radius Blur along x or y, edges clamped, all 8 channels.</summary>
        void BoxBlur(float[] src, float[] dst, bool alongX)
        {
            int lines = alongX ? height : width;
            int length = alongX ? width : height;
            float norm = 1f / (2 * Blur + 1);
            for (int line = 0; line < lines; line++)
                for (int ch = 0; ch < 8; ch++)
                {
                    float sum = 0f;
                    for (int k = -Blur; k <= Blur; k++) sum += src[Index(line, Mathf.Clamp(k, 0, length - 1), alongX) + ch];
                    for (int s = 0; s < length; s++)
                    {
                        dst[Index(line, s, alongX) + ch] = sum * norm;
                        int add = Mathf.Min(s + Blur + 1, length - 1), drop = Mathf.Max(s - Blur, 0);
                        sum += src[Index(line, add, alongX) + ch] - src[Index(line, drop, alongX) + ch];
                    }
                }
        }

        int Index(int line, int s, bool alongX) => (alongX ? line * width + s : s * width + line) * 8;
    }
}
