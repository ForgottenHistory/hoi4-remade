using System;
using System.Collections;
using Core;
using Core.Diplomacy;
using Core.Localization;
using Core.Systems;
using Hoi4.Map;
using Map.MapModes;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's diplomacy view, reduced to its core. Right-clicking a country's land (with no
    /// divisions selected, because then the click is a move order) opens the panel for that country and
    /// switches the map to the diplomatic mode; closing it (X or Escape) restores the previous mode.
    /// The panel shows the country, our relation to it and a Declare War / Make Peace button.
    /// View of the UI Presenter pattern: structure is Resources/Hoi4UI/DiplomacyPanel.uxml, look is
    /// DiplomacyPanelStyle.uss + Hoi4Theme.uss. Single player: the diplomacy system is called directly.
    /// </summary>
    public sealed class Hoi4DiplomacyPanel : MonoBehaviour
    {
        const string FlagDir = "gfx/interface/topbar/flags/";

        GameState gameState;
        global::Map.Interaction.ProvinceSelector selector;
        MapModeManager mapModes;
        UIDocument document;
        VisualElement panel, flag, action;
        Label country, relation, opinion, actionText;
        IDisposable[] subscriptions;

        public static bool IsOpen { get; private set; }

        ushort target;
        bool open, dirty;
        MapMode previousMode = MapMode.Political;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4DiplomacyPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4DiplomacyPanel>();
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.ProvinceSelector == null
                   || Engine.ArchonEngine.Instance.GameState == null || Engine.ArchonEngine.Instance.MapModeManager == null
                   || !Engine.ArchonEngine.Instance.MapModeManager.IsInitialized)
                yield return null;
            var engine = Engine.ArchonEngine.Instance;
            selector = engine.ProvinceSelector;
            gameState = engine.GameState;
            mapModes = engine.MapModeManager;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/DiplomacyPanel");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/DiplomacyPanelStyle");
            if (tree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4DiplomacyPanel: Resources/Hoi4UI/DiplomacyPanel.uxml or its stylesheets are missing, panel disabled", "game_ui");
                yield break;
            }

            if (Hoi4DiplomaticMapMode.Register(mapModes) == null)
            {
                ArchonLogger.LogWarning("Hoi4DiplomacyPanel: could not register the diplomatic map mode, panel disabled", "game_ui");
                yield break;
            }

            // Borrow the panel settings (theme, scaling) of the scene's UI
            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4DiplomacyPanel: no UIDocument panel settings in the scene, panel disabled", "game_ui");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 55;
            yield return null; // let the document create its root

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            panel = tree.Instantiate().Q("diplomacy-panel");
            Hoi4Sprites.Bind(panel);
            Hoi4Sprites.Hook(panel); // the window frame is the root itself, which Bind skips
            panel.style.display = DisplayStyle.None;
            flag = panel.Q("flag");
            country = panel.Q<Label>("country");
            relation = panel.Q<Label>("relation");
            opinion = panel.Q<Label>("opinion");
            action = panel.Q("action");
            actionText = panel.Q<Label>("action-text");
            panel.Q("close").RegisterCallback<ClickEvent>(_ => Close());
            action.RegisterCallback<ClickEvent>(_ => OnAction());
            root.Add(panel);

            selector.OnProvinceRightClicked += OnRightClicked;
            selector.OnProvinceClicked += OnLeftClicked;
            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<DiplomacyWarDeclaredEvent>(_ => OnRelationsChanged()),
                gameState.EventBus.Subscribe<DiplomacyPeaceMadeEvent>(_ => OnRelationsChanged()),
            };
        }

        void OnDestroy()
        {
            if (selector != null) selector.OnProvinceRightClicked -= OnRightClicked;
            if (selector != null) selector.OnProvinceClicked -= OnLeftClicked;
            if (subscriptions != null)
                foreach (var s in subscriptions) s?.Dispose();
            Hoi4UiHover.Remove(this);
        }

        static bool HasCountry()
        {
            var init = StarterKit.Initializer.Instance;
            return init != null && init.PlayerState != null && init.PlayerState.HasPlayerCountry;
        }

        static ushort Player => StarterKit.Initializer.Instance.PlayerState.PlayerCountryId;

        void OnLeftClicked(ushort province)
        {
            if (!open || panel == null) return; // in diplomacy mode a left click switches country
            ushort owner = gameState.ProvinceQueries.GetOwner(province);
            if (owner != 0) Open(owner);
        }

        void OnRightClicked(ushort province)
        {
            if (panel == null || !HasCountry()) return;
            ushort owner = gameState.ProvinceQueries.GetOwner(province);
            selector.ClearSelection(); // right-click always deselects the state, even on empty land
            if (owner != 0) Open(owner);
        }

        void Open(ushort country)
        {
            target = country;
            if (!open)
            {
                open = true;
                previousMode = mapModes.CurrentMode;
                if (previousMode == MapMode.Diplomatic) previousMode = MapMode.Political;
            }
            IsOpen = true;
            Hoi4DiplomaticMapMode.Target = country;
            panel.style.display = DisplayStyle.Flex;
            mapModes.SetMapMode(MapMode.Diplomatic, forceUpdate: true);
            Refresh();
        }

        void Close()
        {
            if (!open) return;
            open = false;
            IsOpen = false;
            Hoi4DiplomaticMapMode.Target = 0;
            panel.style.display = DisplayStyle.None;
            mapModes.SetMapMode(previousMode, forceUpdate: true);
        }

        void OnRelationsChanged()
        {
            dirty = true;
        }

        void Update()
        {
            if (panel == null) return;
            Hoi4UiHover.Report(this, panel);
            if (!open) return;
            if (!HasCountry() || Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            if (dirty)
            {
                dirty = false;
                mapModes.SetMapMode(MapMode.Diplomatic, forceUpdate: true); // recolor wars and peace
                Refresh();
            }
        }

        // --- Content -------------------------------------------------------------------------

        void Refresh()
        {
            string tag = gameState.Countries.GetCountryTag(target);
            Hoi4Sprites.Apply(flag, string.IsNullOrEmpty(tag) ? null : FlagDir + tag + ".png");
            country.text = CountryName(tag);

            var diplomacy = gameState.Diplomacy;
            ushort player = Player;
            bool self = target == player;
            bool war = !self && diplomacy != null && diplomacy.IsAtWar(player, target);
            bool allied = !self && diplomacy != null && diplomacy.AreAllied(player, target);

            relation.text = self ? "<color=#8fd96b>Your country</color>"
                : war ? "<color=#e0584f>At war</color>"
                : allied ? "<color=#6fb0f0>Allied</color>"
                : "Neutral";

            if (self || diplomacy == null)
                opinion.text = "";
            else
            {
                int tick = (int)gameState.Time.CurrentTick;
                opinion.text = war
                    ? $"Surrender: theirs {SurrenderText(target)}, ours {SurrenderText(player)}"
                    : $"Opinion of us: {diplomacy.GetOpinion(target, player, tick).ToInt():+0;-0;0}";
            }

            action.style.display = self ? DisplayStyle.None : DisplayStyle.Flex;
            action.EnableInClassList("war", !war);
            actionText.text = war ? "Make Peace" : "Declare War";
        }

        /// <summary>HOI4 surrender progress (Hoi4War): 100% is capitulation.</summary>
        static string SurrenderText(ushort country)
        {
            var war = Hoi4.War.Hoi4War.Instance;
            return war != null ? $"{(war.SurrenderProgress(country) * 100).ToInt()}%" : "?";
        }

        void OnAction()
        {
            var diplomacy = gameState.Diplomacy;
            if (!open || diplomacy == null || target == 0 || target == Player) return;
            ushort player = Player;
            int tick = (int)gameState.Time.CurrentTick;

            if (diplomacy.IsAtWar(player, target))
            {
                diplomacy.MakePeace(player, target, tick);
            }
            else
            {
                // Declaring war ends any guarantee and military access between the two
                if (diplomacy.IsGuaranteeing(player, target)) diplomacy.RevokeGuarantee(player, target, tick);
                if (diplomacy.HasMilitaryAccess(player, target)) diplomacy.RevokeMilitaryAccess(player, target, tick);
                if (diplomacy.AreAllied(player, target)) diplomacy.BreakAlliance(player, target, tick);
                diplomacy.DeclareWar(player, target, tick);
            }
            dirty = true;
        }

        static string CountryName(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return "Unknown";
            string name = LocalizationManager.Get(tag);
            return string.IsNullOrEmpty(name) || name == tag ? tag : name;
        }
    }
}
