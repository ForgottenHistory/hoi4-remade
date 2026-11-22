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
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's top bar (interface/topbar.gui). View of the UI Presenter pattern: structure is
    /// Resources/Hoi4UI/TopBar.uxml, look and layout are TopBarStyle.uss + Hoi4Theme.uss, and the sprites are
    /// loaded at runtime from the data directory (Hoi4Sprites), never baked into the project.
    ///
    /// Left: the player's shield flag, political power, stability, war support, recruitable
    /// manpower (population x mobilisation law, common/hoi4_country_stats.json5).
    /// Right: the date pill (click or Space: pause), speed down/up (-/+) and five speed steps
    /// (click to pick; green = up to the current speed, red while paused).
    /// Replaces StarterKit TimeUI.
    /// </summary>
    public class Hoi4TopBar : MonoBehaviour
    {
        const string StatsFile = "common/hoi4_country_stats.json5";
        const string FlagDir = "gfx/interface/topbar/flags/";

        /// <summary>Game speed (TimeManager multiplier, in-game days per second) for HOI4 speeds 1-5.</summary>
        public int[] speeds = { 1, 2, 5, 15, 50 };
        [Tooltip("Game hours per real second at speed 1 (24 = a day per second)")]
        public float baseHoursPerSecond = 6f;
        /// <summary>HOI4 base political power gain per day.</summary>
        public float politicalPowerPerDay = 2f;

        static readonly string[] MonthNames =
            { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };

        GameState gameState;
        /// <summary>The peace conference keeps the game paused until it ends.</summary>
        static bool InConference => Hoi4.War.Hoi4War.Instance?.Conference != null;

        TimeManager time;
        PlayerState player;
        UIDocument document;
        VisualElement bar, flag, pill, productionButton;
        Label politicalPower, stability, warSupport, manpower, date;
        readonly VisualElement[] steps = new VisualElement[5];
        readonly Dictionary<string, JObject> stats = new Dictionary<string, JObject>();
        IDisposable[] subscriptions;
        float pp;
        ushort shownCountry;
        long shownManpower = -1;
        int lastLevel = -1;
        bool lastPaused;
        string lastDate;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
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
            time?.SetBaseHoursPerSecond(baseHoursPerSecond);
            player = Initializer.Instance.PlayerState;
            if (time == null || player == null || !DataFileResolver.IsInitialized)
            {
                ArchonLogger.LogWarning("Hoi4TopBar: engine time or player state missing, top bar disabled", "game_ui");
                yield break;
            }

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/TopBar");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/TopBarStyle");
            if (tree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4TopBar: Resources/Hoi4UI/TopBar.uxml or its stylesheets are missing, top bar disabled", "game_ui");
                yield break;
            }

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

            LoadStats();

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            bar = tree.Instantiate().Q("topbar");
            Hoi4Sprites.Bind(bar);
            bar.style.display = DisplayStyle.None; // shown once a country is picked
            root.Add(bar);
            Wire();

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
        }

        void Wire()
        {
            flag = bar.Q("player-flag");
            politicalPower = bar.Q<Label>("political-power");
            stability = bar.Q<Label>("stability");
            warSupport = bar.Q<Label>("war-support");
            manpower = bar.Q<Label>("manpower");
            date = bar.Q<Label>("date");
            pill = bar.Q("pill");

            pill.RegisterCallback<ClickEvent>(_ => { if (!InConference) time.TogglePause(); });
            productionButton = bar.Q("production-button");
            productionButton.RegisterCallback<ClickEvent>(_ => Hoi4ProductionPanel.Instance?.Toggle());
            bar.Q("speed-down").RegisterCallback<ClickEvent>(_ => StepSpeed(-1));
            bar.Q("speed-up").RegisterCallback<ClickEvent>(_ => StepSpeed(+1));
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i] = bar.Q($"step-{i + 1}");
                int level = i;
                steps[i].RegisterCallback<ClickEvent>(_ => SetLevel(level));
            }
        }

        /// <summary>Production button: pressed look while the screen is open.</summary>
        void UpdateProductionButton()
        {
            if (productionButton != null) productionButton.EnableInClassList("open", Hoi4ProductionPanel.IsOpen);
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

            if (Input.GetKeyDown(KeyCode.Space) && !InConference) time.TogglePause();
            if (Input.GetKeyDown(KeyCode.KeypadPlus) || Input.GetKeyDown(KeyCode.Equals)) StepSpeed(+1);
            if (Input.GetKeyDown(KeyCode.KeypadMinus) || Input.GetKeyDown(KeyCode.Minus)) StepSpeed(-1);

            if (shownCountry != player.PlayerCountryId) ShowCountry(player.PlayerCountryId);
            UpdateProductionButton();
            politicalPower.text = Mathf.FloorToInt(pp).ToString();
            var pools = Hoi4.Units.Hoi4Manpower.Instance;
            if (pools != null && pools.IsReady)
            {
                long men = pools.Available(shownCountry).ToInt();
                if (men != shownManpower) { shownManpower = men; manpower.text = FormatManpower(men); }
            }

            var t = time.GetCurrentGameTime();
            string d = $"{t.Hour}:00, {t.Day} {MonthNames[Mathf.Clamp(t.Month - 1, 0, 11)]} {t.Year}";
            if (d != lastDate) { date.text = d; lastDate = d; }

            int level = CurrentLevel();
            bool paused = time.IsPaused;
            if (level != lastLevel || paused != lastPaused)
            {
                lastLevel = level; lastPaused = paused;
                pill.EnableInClassList("paused", paused);
                for (int i = 0; i < steps.Length; i++)
                {
                    steps[i].EnableInClassList("on", i <= level);
                    steps[i].EnableInClassList("paused", paused);
                }
            }
        }

        void ShowCountry(ushort country)
        {
            shownCountry = country;
            shownManpower = -1;
            pp = 0f;
            string tag = gameState.Countries.GetCountryTag(country);
            Hoi4Sprites.Apply(flag, string.IsNullOrEmpty(tag) ? null : $"{FlagDir}{tag}.png");
            if (stats.TryGetValue(tag ?? "", out var s))
            {
                stability.text = $"{Mathf.RoundToInt((float)s["stability"] * 100f)}%";
                warSupport.text = $"{Mathf.RoundToInt((float)s["war_support"] * 100f)}%";
                // Recruitable until the live pool (Hoi4Manpower) is up; non-core states count 2% (HOI4)
                double noncore = s["noncore_population"] != null ? (double)s["noncore_population"] : 0;
                manpower.text = FormatManpower(((double)s["population"] + 0.02 * noncore) * (double)s["conscription"]);
            }
            else
            {
                stability.text = "50%";
                warSupport.text = "50%";
                manpower.text = "-";
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
