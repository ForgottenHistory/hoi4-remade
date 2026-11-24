using UnityEngine;

namespace Hoi4.Weather
{
    /// <summary>
    /// GAME (presentation): the weather on the map. HOI4 keeps a map texture of each province's snow and mud
    /// (SnowMudData: r mud, g snow, b snow before, a mud before) that its terrain shader fades between days; ours is
    /// one texel per province id (_Hoi4WeatherTex, 256 x 256, linear):
    ///
    ///   r = mud now (0/1), g = snow now (amount / 80), b = snow before, a = mud before.
    ///
    /// "Before" is what was on screen when the day changed, and _Hoi4WeatherFade goes 0 to 1 over FadeSeconds (real
    /// time, presentation), so a fast game still fades smoothly. The shader (Hoi4Weather.hlsl) blends the provinces
    /// around each pixel, so the snow edge is soft, then lays HOI4's snow over the terrain. Rewritten once a game day.
    /// Prototype: Tools/prototypes/weather/snow_render.py.
    /// </summary>
    public sealed class Hoi4WeatherMap : MonoBehaviour
    {
        const int Width = 256, Rows = 256; // one texel per province id, up to 65536
        const float FadeSeconds = 1.5f;
        static readonly int TexId = Shader.PropertyToID("_Hoi4WeatherTex");
        static readonly int FadeId = Shader.PropertyToID("_Hoi4WeatherFade");

        Texture2D texture;
        Color32[] data;
        int shownVersion = -1;
        float fadeStart = -100f;
        float shownFade = -1f;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4WeatherMap");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4WeatherMap>();
        }

        void Start()
        {
            texture = new Texture2D(Width, Rows, TextureFormat.RGBA32, false, linear: true)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Hoi4WeatherTex"
            };
            data = new Color32[Width * Rows];
            texture.SetPixelData(data, 0);
            texture.Apply(false);
            Shader.SetGlobalTexture(TexId, texture);
            Shader.SetGlobalFloat(FadeId, 1f);
        }

        void OnDestroy()
        {
            if (texture != null) Destroy(texture);
        }

        void Update()
        {
            if (texture == null || !Hoi4Weather.Available) return;
            float fade = Mathf.Clamp01((Time.unscaledTime - fadeStart) / FadeSeconds);
            if (Hoi4Weather.Version != shownVersion)
            {
                bool first = shownVersion < 0;
                shownVersion = Hoi4Weather.Version;
                Write(first ? 1f : fade, first);
                fadeStart = Time.unscaledTime;
                fade = first ? 1f : 0f;
                if (first) fadeStart = -100f;
            }
            if (fade != shownFade)
            {
                shownFade = fade;
                Shader.SetGlobalFloat(FadeId, fade);
            }
        }

        void Write(float fade, bool first)
        {
            int range = Mathf.Min(Hoi4Weather.ProvinceRange, data.Length);
            for (int p = 1; p < range; p++)
            {
                var old = data[p];
                byte snow = (byte)Mathf.Clamp(Mathf.RoundToInt(Hoi4Weather.Snow((ushort)p).ToFloat() * (255f / 80f)), 0, 255);
                byte mud = Hoi4Weather.IsMud((ushort)p) ? (byte)255 : (byte)0;
                // before = what is on screen now (mid-fade when days pass faster than the fade)
                byte snowBefore = first ? snow : (byte)Mathf.RoundToInt(Mathf.Lerp(old.b, old.g, fade));
                byte mudBefore = first ? mud : (byte)Mathf.RoundToInt(Mathf.Lerp(old.a, old.r, fade));
                data[p] = new Color32(mud, snow, snowBefore, mudBefore);
            }
            texture.SetPixelData(data, 0);
            texture.Apply(false);
        }
    }
}
