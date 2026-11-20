using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Core.Systems;
using Newtonsoft.Json.Linq;
using StarterKit;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UIElements;
using TextFontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's top bar, built from HOI4's own interface sprites. Tools/hoi4_convert.py
    /// (convert_topbar) exports them with the layout of interface/topbar.gui already resolved to
    /// the left or right screen edge (gfx/interface/topbar/topbar.json5).
    ///
    /// Left: the player's shield flag, political power, stability, war support, recruitable
    /// manpower (population x mobilisation law, common/hoi4_country_stats.json5).
    /// Right: the date pill (click or Space: pause), speed down/up (-/+) and five speed steps
    /// (click to pick; green = up to the current speed, red while paused).
    /// Replaces StarterKit's TimeUI. Prototype: Tools/prototypes/topbar.
    /// </summary>
    public class Hoi4TopBar : MonoBehaviour
    {
        const string LayoutFile = "gfx/interface/topbar/topbar.json5";
        const string StatsFile = "common/hoi4_country_stats.json5";
        const string FontFile = "gfx/fonts/Oswald.ttf";

        /// <summary>Game speed (TimeManager multiplier, in-game days per second) for HOI4's speeds 1-5.</summary>
        public int[] speeds = { 1, 2, 5, 15, 50 };
        /// <summary>HOI4's base political power gain per day.</summary>
        public float politicalPowerPerDay = 2f;
        public Color textColor = new Color(0.93f, 0.9f, 0.82f);
        public int fontSize = 15;

        static readonly string[] MonthNames =
            { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        GameState gameState;
        TimeManager time;
        PlayerState player;
        UIDocument document;
        VisualElement bar;
        string dir;
        TextFontAsset font;
        readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        readonly Dictionary<string, JObject> stats = new Dictionary<string, JObject>();
        IDisposable[] subscriptions;

        VisualElement flag, pill;
        Label politicalPower, stability, warSupport, manpower, date, threat;
        readonly VisualElement[] steps = new VisualElement[5];
        Texture2D pillRunning, pillPaused;
        Texture2D[] stepFrames = Array.Empty<Texture2D>();
        float pp;
        ushort shownCountry;
        int lastLevel = -1;
        bool lastPaused;
        string lastDate;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            var go = new GameObject("Hoi4TopBar");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4TopBar>();
        }

        IEnumerator Start()
        {
            while (Initializer.Instance == null || !Initializer.Instance.IsInitialized)
                yield return null;
            gameState = Engine.ArchonEngine.Instance != null ? Engine.ArchonEngine.Instance.GameState : null;
            time = gameState != null ? gameState.Time : null;
            player = Initializer.Instance.PlayerState;
            if (time == null || player == null || !DataFileResolver.IsInitialized)
            {
                ArchonLogger.LogWarning("Hoi4TopBar: engine time or player state missing, top bar disabled", "game_ui");
                yield break;
            }

            string layoutPath = DataFileResolver.Resolve(LayoutFile);
            if (!File.Exists(layoutPath))
            {
                ArchonLogger.LogWarning($"Hoi4TopBar: {layoutPath} missing (run Tools/hoi4_convert.py), top bar disabled", "game_ui");
                yield break;
            }
            dir = Path.GetDirectoryName(layoutPath);

            // Borrow the panel settings (theme, scaling) of the scene's UI
            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4TopBar: no UIDocument panel settings in the scene, top bar disabled", "game_ui");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 60;
            yield return null; // let the document create its root

            LoadFont();
            LoadStats();
            Build(Json5Loader.LoadJson5File(layoutPath));

            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<DailyTickEvent>(_ => pp += politicalPowerPerDay),
            };
            ArchonLogger.Log("Hoi4TopBar: ready", "game_ui");
        }

        void OnDestroy()
        {
            if (subscriptions != null)
                foreach (var s in subscriptions) s?.Dispose();
            foreach (var t in textures.Values) if (t != null) Destroy(t);
            if (font != null) Destroy(font);
        }

        // --- Build ---------------------------------------------------------------------------

        void Build(JObject layout)
        {
            var root = document.rootVisualElement;
            bar = new VisualElement { name = "hoi4-topbar", pickingMode = PickingMode.Ignore };
            bar.style.position = Position.Absolute;
            bar.style.left = 0; bar.style.right = 0; bar.style.top = 0; bar.style.height = 90;
            root.Add(bar);

            foreach (JObject e in (JArray)layout["elements"])
            {
                string name = (string)e["name"];
                string kind = (string)e["kind"];
                var ve = kind == "text" ? MakeLabel(e) : MakeImage(e);
                if (ve == null) continue;
                Place(ve, e);
                bar.Add(ve);

                switch (name)
                {
                    case "player_flag": flag = ve; break;
                    case "pol_power": politicalPower = (Label)ve; break;
                    case "stability": stability = (Label)ve; break;
                    case "war_support": warSupport = (Label)ve; break;
                    case "manpower": manpower = (Label)ve; break;
                    case "DateText": date = (Label)ve; break;
                    case "threat_value": threat = (Label)ve; threat.text = "0%"; break;
                    case "pause_button_date":
                        pill = ve;
                        pillRunning = Tex((string)e["image"]);
                        ve.RegisterCallback<ClickEvent>(_ => time.TogglePause());
                        break;
                    case "button_speeddown":
                        ve.RegisterCallback<ClickEvent>(_ => StepSpeed(-1));
                        break;
                    case "button_speedup":
                        ve.RegisterCallback<ClickEvent>(_ => StepSpeed(+1));
                        break;
                }
                if (name.StartsWith("button_speedstep") && int.TryParse(name.Substring("button_speedstep".Length), out int n) && n >= 1 && n <= 5)
                {
                    steps[n - 1] = ve;
                    int level = n - 1;
                    ve.RegisterCallback<ClickEvent>(_ => SetLevel(level));
                    if (stepFrames.Length == 0 && e["frames"] is JArray frames)
                    {
                        stepFrames = new Texture2D[frames.Count];
                        for (int i = 0; i < frames.Count; i++) stepFrames[i] = Tex((string)frames[i]);
                    }
                }
                if (kind == "button") AddHover(ve);
            }
            if (layout["paused_image"] != null)
                pillPaused = Tex((string)layout["paused_image"]);

            // The date sits on the pill, right of its play/pause symbol; clicks fall through to the pill
            if (date != null)
            {
                date.BringToFront();
                date.style.paddingLeft = 26;
                date.style.fontSize = 12;
            }
            AddBlockers();
            bar.style.display = DisplayStyle.None; // shown once a country is picked
        }

        /// <summary>
        /// Invisible boxes over the opaque parts of the bar background, so clicks there don't reach
        /// the map. UI Toolkit picks whole rectangles, and the background image is transparent right
        /// of its lower panel. The input system treats picked UI Toolkit elements as UI.
        /// </summary>
        void AddBlockers()
        {
            var top = new VisualElement { name = "topbar-block-top" };
            top.style.position = Position.Absolute;
            top.style.left = 0; top.style.right = 0; top.style.top = 0; top.style.height = 34;
            var left = new VisualElement { name = "topbar-block-left" };
            left.style.position = Position.Absolute;
            left.style.left = 0; left.style.top = 0; left.style.width = 700; left.style.height = 82;
            bar.Insert(1, top);  // above the background, below the widgets
            bar.Insert(2, left);
        }

        VisualElement MakeImage(JObject e)
        {
            var ve = new VisualElement { name = (string)e["name"] };
            string image = (string)e["image"];
            if (image != null && !image.Contains("{"))
            {
                var tex = Tex(image);
                if (tex == null) return null;
                ve.style.backgroundImage = new StyleBackground(tex);
            }
            ve.style.width = (float)e["w"];
            ve.style.height = (float)e["h"];
            // Only buttons take the mouse (map clicks under the bar are stopped by Blockers)
            ve.pickingMode = (string)e["kind"] == "button" ? PickingMode.Position : PickingMode.Ignore;
            return ve;
        }

        Label MakeLabel(JObject e)
        {
            var l = new Label("") { name = (string)e["name"], pickingMode = PickingMode.Ignore };
            l.style.width = (float)e["w"];
            l.style.height = 20;
            l.style.color = textColor;
            l.style.fontSize = fontSize;
            l.style.unityFontStyleAndWeight = FontStyle.Bold;
            l.style.paddingLeft = 0; l.style.paddingRight = 0; l.style.paddingTop = 0; l.style.paddingBottom = 0;
            l.style.marginLeft = 0; l.style.marginRight = 0; l.style.marginTop = 0; l.style.marginBottom = 0;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            l.style.overflow = Overflow.Hidden;
            string format = (string)e["format"];
            l.style.unityTextAlign = format == "right" ? TextAnchor.MiddleRight
                : format == "center" ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft;
            if (font != null) l.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
            return l;
        }

        static void Place(VisualElement ve, JObject e)
        {
            ve.style.position = Position.Absolute;
            float x = (float)e["x"], y = (float)e["y"];
            ve.style.top = y;
            if ((string)e["edge"] == "right")
                ve.style.right = -x - (float)(e["w"] ?? 0f); // x is the left side's offset from the right screen edge
            else
                ve.style.left = x;
        }

        static void AddHover(VisualElement ve)
        {
            // HOI4's buttonstate effect: brighter on hover, darker while pressed
            ve.RegisterCallback<PointerEnterEvent>(_ => ve.style.unityBackgroundImageTintColor = new Color(1.25f, 1.25f, 1.25f));
            ve.RegisterCallback<PointerLeaveEvent>(_ => ve.style.unityBackgroundImageTintColor = Color.white);
            ve.RegisterCallback<PointerDownEvent>(_ => ve.style.unityBackgroundImageTintColor = new Color(0.8f, 0.8f, 0.8f));
            ve.RegisterCallback<PointerUpEvent>(_ => ve.style.unityBackgroundImageTintColor = new Color(1.25f, 1.25f, 1.25f));
        }

        Texture2D Tex(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;
            if (textures.TryGetValue(file, out var t)) return t;
            string path = Path.Combine(dir, file);
            t = null;
            if (File.Exists(path))
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Hoi4TopBar_" + file, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
                if (!t.LoadImage(File.ReadAllBytes(path))) { Destroy(t); t = null; }
            }
            else ArchonLogger.LogWarning($"Hoi4TopBar: missing {path}", "game_ui");
            textures[file] = t;
            return t;
        }

        void LoadFont()
        {
            string path = DataFileResolver.Resolve(FontFile);
            if (!File.Exists(path)) return;
            try
            {
                font = TextFontAsset.CreateFontAsset(path, 0, 64, 6, GlyphRenderMode.SDFAA, 1024, 1024);
            }
            catch (Exception ex)
            {
                ArchonLogger.LogWarning($"Hoi4TopBar: could not load {path} ({ex.Message}), using the default font", "game_ui");
            }
        }

        void LoadStats()
        {
            string path = DataFileResolver.Resolve(StatsFile);
            if (!File.Exists(path)) return;
            foreach (var p in Json5Loader.LoadJson5File(path).Properties())
                if (p.Value is JObject o) stats[p.Name] = o;
        }

        // --- Update --------------------------------------------------------------------------

        void Update()
        {
            if (bar == null || time == null) return;

            bool visible = player.HasPlayerCountry;
            bar.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (!visible) return;

            if (Input.GetKeyDown(KeyCode.Space)) time.TogglePause();
            if (Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals)) StepSpeed(+1);
            if (Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Minus)) StepSpeed(-1);

            if (shownCountry != player.PlayerCountryId) ShowCountry(player.PlayerCountryId);
            if (politicalPower != null) politicalPower.text = Mathf.FloorToInt(pp).ToString();

            var t = time.GetCurrentGameTime();
            string d = $"{t.Hour}:00, {t.Day} {MonthNames[Mathf.Clamp(t.Month - 1, 0, 11)]} {t.Year}";
            if (date != null && d != lastDate) { date.text = d; lastDate = d; }

            int level = CurrentLevel();
            bool paused = time.IsPaused;
            if (level != lastLevel || paused != lastPaused)
            {
                lastLevel = level; lastPaused = paused;
                if (pill != null)
                {
                    var tex = paused && pillPaused != null ? pillPaused : pillRunning;
                    if (tex != null) pill.style.backgroundImage = new StyleBackground(tex);
                }
                for (int i = 0; i < steps.Length; i++)
                {
                    if (steps[i] == null || stepFrames.Length < 2) continue;
                    // frame 0 off, 1 on (green), 2 on while paused (red)
                    int frame = i <= level ? (paused && stepFrames.Length > 2 ? 2 : 1) : 0;
                    steps[i].style.backgroundImage = new StyleBackground(stepFrames[frame]);
                }
            }
        }

        void ShowCountry(ushort country)
        {
            shownCountry = country;
            pp = 0f;
            string tag = gameState.Countries.GetCountryTag(country);
            if (flag != null)
            {
                var tex = Tex($"flags/{tag}.png");
                flag.style.backgroundImage = tex != null ? new StyleBackground(tex) : new StyleBackground();
            }
            if (stats.TryGetValue(tag ?? "", out var s))
            {
                if (stability != null) stability.text = $"{Mathf.RoundToInt((float)s["stability"] * 100f)}%";
                if (warSupport != null) warSupport.text = $"{Mathf.RoundToInt((float)s["war_support"] * 100f)}%";
                if (manpower != null) manpower.text = FormatManpower((double)s["population"] * (double)s["conscription"]);
            }
            else
            {
                if (stability != null) stability.text = "50%";
                if (warSupport != null) warSupport.text = "50%";
                if (manpower != null) manpower.text = "-";
            }
        }

        static string FormatManpower(double m)
        {
            if (m >= 1e6) return $"{m / 1e6:0.00}M";
            if (m >= 1e3) return $"{m / 1e3:0}K";
            return $"{m:0}";
        }

        // --- Speed ---------------------------------------------------------------------------

        /// <summary>Highest speed step at or below the current game speed (0..4).</summary>
        int CurrentLevel()
        {
            int speed = time.GameSpeed, level = 0;
            for (int i = 0; i < speeds.Length; i++)
                if (speeds[i] <= speed) level = i;
            return level;
        }

        void SetLevel(int level)
        {
            level = Mathf.Clamp(level, 0, speeds.Length - 1);
            time.SetSpeed(speeds[level]);
        }

        void StepSpeed(int delta) => SetLevel(CurrentLevel() + delta);
    }
}
