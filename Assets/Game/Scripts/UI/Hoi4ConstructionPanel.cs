using System.Collections;
using Core;
using Hoi4.Map;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's construction screen (interface/countryconstructionsview.gui), left of the map, opened by the top
    /// bar's construction button or T. It shares the left side with the production screen: opening one closes the other.
    /// View of the UI Presenter pattern: structure is Resources/Hoi4UI/ConstructionPanel.uxml, look is
    /// ConstructionPanelStyle.uss + Hoi4Theme.uss.
    /// Foundation only so far: the layout with placeholder values (factory summary, consumer goods row, count and
    /// priority controls) and the building buttons, built per section (state, shared, province, experimental
    /// facilities) from the building data in HOI4's order. Clicking a building only marks it; nothing reads
    /// Hoi4Construction or sends commands yet.
    /// </summary>
    public sealed class Hoi4ConstructionPanel : MonoBehaviour
    {
        const string BuildingIcons = "gfx/interface/stateview/buildings_";
        static readonly (string id, string name)[] Sections =
        {
            ("state", "State Buildings"), ("shared", "Shared Buildings"),
            ("province", "Province Buildings"), ("facility", "Experimental Facilities"),
        };
        const int SectionHeader = 52, ButtonRow = 44, ButtonsPerRow = 4;

        public static Hoi4ConstructionPanel Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance.open;

        UIDocument document;
        VisualElement panel, buildings, selected;
        readonly object buildingsHover = new object(); // the building column hangs past the window's edge
        bool open;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4ConstructionPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4ConstructionPanel>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Hoi4UiHover.Remove(this);
            Hoi4UiHover.Remove(buildingsHover);
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null)
                yield return null;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/ConstructionPanel");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/ConstructionPanelStyle");
            if (tree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4ConstructionPanel: Resources/Hoi4UI/ConstructionPanel.uxml or its stylesheets are missing, panel disabled", "game_ui");
                yield break;
            }

            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4ConstructionPanel: no UIDocument panel settings in the scene, panel disabled", "game_ui");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 45; // like the production screen: over the state and unit panels, under the top bar
            yield return null;

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            panel = tree.Instantiate().Q("construction-panel");
            Hoi4Sprites.Bind(panel);
            root.Add(panel);
            buildings = panel.Q(className: "co-buildings");
            panel.Q("co-close").RegisterCallback<ClickEvent>(_ => Close());
            panel.Q("co-minimize").RegisterCallback<ClickEvent>(_ => Close());

            while (!Hoi4StateData.Available) yield return null;
            BuildSections(panel.Q("co-sections"));
        }

        /// <summary>One section per building group: header, then the buttons in rows of four.</summary>
        void BuildSections(VisualElement host)
        {
            host.Clear();
            foreach (var (id, name) in Sections)
            {
                var section = new VisualElement { pickingMode = PickingMode.Ignore };
                section.AddToClassList("co-section");
                section.Add(Hoi4Sprites.Make("co-section-bg"));
                section.Add(Hoi4Sprites.Make("co-section-icon", $"co-section-{id}"));
                var label = new Label(name) { pickingMode = PickingMode.Ignore };
                label.AddToClassList("hoi4-label");
                label.AddToClassList("co-section-name");
                section.Add(label);
                var grid = new VisualElement { pickingMode = PickingMode.Ignore };
                grid.AddToClassList("co-section-grid");
                section.Add(grid);

                int count = 0;
                foreach (var def in Hoi4StateData.Defs)
                {
                    if (def.Section != id || def.Frame <= 0) continue;
                    grid.Add(MakeButton(def));
                    count++;
                }
                section.style.height = SectionHeader + (count + ButtonsPerRow - 1) / ButtonsPerRow * ButtonRow;
                host.Add(section);
            }
        }

        VisualElement MakeButton(Hoi4StateData.BuildingDef def)
        {
            var button = new VisualElement { pickingMode = PickingMode.Position, tooltip = def.Name };
            button.AddToClassList("co-building");
            var icon = Hoi4Sprites.Make("co-building-icon", "hoi4-button");
            Hoi4Sprites.Apply(icon, BuildingIcons + (def.Frame - 1) + ".png");
            icon.pickingMode = PickingMode.Ignore;
            button.Add(icon);
            button.Add(Hoi4Sprites.Make("co-building-glow"));
            button.RegisterCallback<ClickEvent>(_ => Select(button));
            return button;
        }

        void Select(VisualElement button)
        {
            selected?.RemoveFromClassList("selected");
            selected = selected == button ? null : button;
            selected?.AddToClassList("selected");
        }

        static ushort Player
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        // ---- open / close -----------------------------------------------------------------------------

        /// <summary>The top bar's construction button.</summary>
        public void Toggle()
        {
            if (open) Close(); else Open();
        }

        void Open()
        {
            if (panel == null || Player == 0) return;
            if (Hoi4ProductionPanel.IsOpen) Hoi4ProductionPanel.Instance.Toggle();
            open = true;
            panel.style.display = DisplayStyle.Flex;
        }

        public void Close()
        {
            open = false;
            Select(null);
            if (panel != null) panel.style.display = DisplayStyle.None;
        }

        void Update()
        {
            if (panel == null) return;
            if (Player == 0 || Hoi4.War.Hoi4War.Instance?.Conference != null)
            {
                if (open) Close();
                return;
            }
            if (Input.GetKeyDown(KeyCode.T)) Toggle();
            if (open && Input.GetKeyDown(KeyCode.Escape)) Close();
            Hoi4UiHover.Report(this, open ? panel : null);
            Hoi4UiHover.Report(buildingsHover, open ? buildings : null);
        }
    }
}
