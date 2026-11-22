using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Localization;
using Hoi4.Construction;
using Hoi4.Map;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's construction screen (interface/countryconstructionsview.gui), left of the map, opened by the top
    /// bar's construction button or T. It shares the left side with the production screen: opening one closes the other.
    /// View of the UI Presenter pattern: structure is Resources/Hoi4UI/ConstructionPanel.uxml (+ ConstructionLine.uxml
    /// per order), look is ConstructionPanelStyle.uss + Hoi4Theme.uss. Every change is a construction command.
    /// - Top: civilian factories in use (consumer goods + on orders) of all (red while some stand idle), owned,
    ///   construction speed. Consumer goods row first in the list.
    /// - Orders: location, building with its level count, +/- levels (Shift: 5; + greyed while the state is full,
    ///   - on the last level removes the order), factories on it out of 15 over a
    ///   box that animates faster the more factories work on it, progress of the level being built, priority arrows
    ///   (Shift: to the top or bottom), delete.
    /// - Building buttons per section (state, shared, province, experimental facilities) from the building data in
    ///   HOI4's order. With one selected, clicking a province queues a level of it in that state (the state panel stays
    ///   shut); clicking the button again, Escape or closing the screen drops the selection. Only civilian and military
    ///   factories and infrastructure can be built so far; the rest select but never queue.
    /// Values refresh four times a real second and the animation runs on real time (presentation, not game time);
    /// the list is rebuilt only when the country's queue changes.
    /// </summary>
    public sealed class Hoi4ConstructionPanel : MonoBehaviour
    {
        const string BuildingIcons = "gfx/interface/stateview/buildings_";
        const string Ongoing = "gfx/interface/construction/ongoing_";
        const float RefreshSeconds = 0.25f;
        const float AnimationFps = 24f; // GFX_ongoing_contruction_anim, at 15 factories
        const int AnimationFrames = 12;
        static readonly (string id, string name)[] Sections =
        {
            ("state", "State Buildings"), ("shared", "Shared Buildings"),
            ("province", "Province Buildings"), ("facility", "Experimental Facilities"),
        };
        const int SectionHeader = 52, ButtonRow = 44, ButtonsPerRow = 4;

        public static Hoi4ConstructionPanel Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance.open;

        /// <summary>The building a province click queues (null when none is selected): the state panel stays shut then.</summary>
        public static string Placing => IsOpen ? Instance.placing : null;

        UIDocument document;
        VisualElement panel, buildings, selected;
        ScrollView lineList;
        VisualElement consumerRow;
        VisualTreeAsset lineTree;
        GameState gameState;
        global::Map.Interaction.ProvinceSelector selector;
        readonly object buildingsHover = new object(); // the building column hangs past the window's edge
        readonly List<VisualElement> entries = new List<VisualElement>();
        readonly List<int> shownIds = new List<int>();
        readonly List<float> phase = new List<float>();
        string placing;
        bool open, structureDirty = true;
        float nextRefresh;
        ushort shownCountry;

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
            if (selector != null) selector.OnProvinceClicked -= OnProvinceClicked;
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.ProvinceSelector == null)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            selector = Engine.ArchonEngine.Instance.ProvinceSelector;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/ConstructionPanel");
            lineTree = Resources.Load<VisualTreeAsset>("Hoi4UI/ConstructionLine");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/ConstructionPanelStyle");
            if (tree == null || lineTree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4ConstructionPanel: Resources/Hoi4UI/ConstructionPanel.uxml, its entry or stylesheets are missing, panel disabled", "game_ui");
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
            lineList = panel.Q<ScrollView>("co-lines");
            consumerRow = lineList.Q(className: "co-consumer");
            panel.Q("co-close").RegisterCallback<ClickEvent>(_ => Close());
            panel.Q("co-minimize").RegisterCallback<ClickEvent>(_ => Close());
            selector.OnProvinceClicked += OnProvinceClicked;

            while (!Hoi4StateData.Available) yield return null;
            BuildSections(panel.Q("co-sections"));

            while (Hoi4Construction.Instance == null) yield return null;
            Hoi4Construction.Instance.QueueChanged += c => { if (c == shownCountry) structureDirty = true; };
        }

        static ushort Player
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        // ---- building buttons -------------------------------------------------------------------------

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
            var button = new VisualElement { pickingMode = PickingMode.Position, tooltip = BuildingName(def.Name) };
            button.AddToClassList("co-building");
            var icon = Hoi4Sprites.Make("co-building-icon", "hoi4-button");
            Hoi4Sprites.Apply(icon, BuildingIcons + (def.Frame - 1) + ".png");
            icon.pickingMode = PickingMode.Ignore;
            button.Add(icon);
            button.Add(Hoi4Sprites.Make("co-building-glow"));
            button.userData = def.Name;
            button.RegisterCallback<ClickEvent>(_ => Select(button));
            return button;
        }

        void Select(VisualElement button)
        {
            selected?.RemoveFromClassList("selected");
            selected = selected == button ? null : button;
            selected?.AddToClassList("selected");
            placing = selected?.userData as string;
        }

        static string BuildingName(string building)
        {
            string name = LocalizationManager.Get(building);
            return string.IsNullOrEmpty(name) || name == building ? building : name;
        }

        /// <summary>A province click while a building is selected: one level of it in the province's state.</summary>
        void OnProvinceClicked(ushort province)
        {
            if (!open || placing == null || province == 0 || shownCountry == 0) return;
            int state = Hoi4Regions.State(province);
            if (state < 0) return;
            int count = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? 5 : 1;
            for (int i = 0; i < count; i++)
                if (!gameState.TryExecuteCommand(new QueueConstructionCommand { CountryId = shownCountry, State = state, Building = placing }, out _))
                    break;
            nextRefresh = 0f;
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
            structureDirty = true;
            nextRefresh = 0f;
        }

        public void Close()
        {
            open = false;
            if (selected != null) Select(selected);
            if (panel != null) panel.style.display = DisplayStyle.None;
        }

        // ---- orders -----------------------------------------------------------------------------------

        static int ShiftOr(int plain, int shifted) =>
            Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? shifted : plain;

        void ChangeCount(int id, int sign)
        {
            var item = Hoi4Construction.Instance.Item(shownCountry, id);
            if (item == null) return;
            int to = item.Count + sign * ShiftOr(1, 5);
            if (to < 1)
            {
                Remove(id); // - on the last level takes the order off, like HOI4
                return;
            }
            if (to != item.Count)
                gameState.TryExecuteCommand(new SetConstructionCountCommand { CountryId = shownCountry, ItemId = id, Count = to }, out _);
            nextRefresh = 0f;
        }

        void Move(int id, int delta)
        {
            var queue = Hoi4Construction.Instance.Queue(shownCountry);
            int at = -1;
            for (int i = 0; i < queue.Count; i++) if (queue[i].Id == id) at = i;
            if (at < 0) return;
            int to = ShiftOr(at + delta, delta < 0 ? 0 : queue.Count - 1);
            to = Mathf.Clamp(to, 0, queue.Count - 1);
            if (to != at)
                gameState.TryExecuteCommand(new MoveConstructionCommand { CountryId = shownCountry, ItemId = id, Position = to }, out _);
        }

        void Remove(int id) =>
            gameState.TryExecuteCommand(new RemoveConstructionCommand { CountryId = shownCountry, ItemId = id }, out _);

        void Rebuild(Hoi4Construction construction)
        {
            structureDirty = false;
            foreach (var e in entries) e.RemoveFromHierarchy();
            entries.Clear();
            shownIds.Clear();
            foreach (var item in construction.Queue(shownCountry))
            {
                var e = lineTree.Instantiate().Q(className: "cl-entry");
                Hoi4Sprites.Bind(e);
                int id = item.Id;
                e.Q("cl-add").RegisterCallback<ClickEvent>(_ => ChangeCount(id, +1));
                e.Q("cl-sub").RegisterCallback<ClickEvent>(_ => ChangeCount(id, -1));
                e.Q("cl-up").RegisterCallback<ClickEvent>(_ => Move(id, -1));
                e.Q("cl-down").RegisterCallback<ClickEvent>(_ => Move(id, +1));
                e.Q("cl-delete").RegisterCallback<ClickEvent>(_ => Remove(id));
                e.Q<Label>("cl-location").text = Hoi4StatePresenter.StateName(item.State);
                var def = Hoi4StateData.Def(item.Building);
                if (def != null) Hoi4Sprites.Apply(e.Q("cl-picture"), BuildingIcons + (def.Frame - 1) + ".png");
                e.Q("cl-picture").tooltip = BuildingName(item.Building);
                lineList.Add(e);
                entries.Add(e);
                shownIds.Add(id);
            }
            while (phase.Count < entries.Count) phase.Add(0f);
            nextRefresh = 0f;
        }

        void Refresh(Hoi4Construction construction)
        {
            int civ = construction.Civilian(shownCountry);
            int cg = construction.ConsumerGoods(shownCountry);
            var queue = construction.Queue(shownCountry);
            int onOrders = 0;
            foreach (var i in queue) onOrders += i.Factories;
            int used = Mathf.Min(civ, cg + onOrders);
            var factories = panel.Q<Label>("co-factories");
            factories.text = $"{used}/{civ}";
            factories.EnableInClassList("all-used", used >= civ);
            panel.Q<Label>("co-owned").text = civ.ToString();
            var speed = construction.EconomyOf(shownCountry).Speed.ToFloat();
            var speedLabel = panel.Q<Label>("co-speed");
            speedLabel.text = (speed >= 0 ? "+" : "") + Mathf.RoundToInt(speed * 100) + "%";
            speedLabel.EnableInClassList("negative", speed < 0);
            consumerRow.Q<Label>("co-consumer-count").text = cg.ToString();

            for (int k = 0; k < entries.Count && k < queue.Count; k++)
            {
                var item = queue[k];
                var e = entries[k];
                e.Q<Label>("cl-count").text = item.Count.ToString();
                e.Q("cl-add").EnableInClassList("disabled", construction.Room(item.State, item.Building) <= 0);
                e.Q<Label>("cl-factories").text = $"{item.Factories}/{Hoi4Construction.MaxFactoriesPerItem}";
                float progress = item.Cost > Core.Data.FixedPoint64.Zero ? Mathf.Clamp01((item.Progress / item.Cost).ToFloat()) : 0f;
                e.Q("cl-progress").style.width = Mathf.Round(60 * progress);
            }
        }

        /// <summary>The factories box animates while factories work on the order, faster the more there are
        /// (HOI4's 24 fps at 15 factories). Real time: presentation only.</summary>
        void Animate(Hoi4Construction construction)
        {
            var queue = construction.Queue(shownCountry);
            for (int k = 0; k < entries.Count && k < queue.Count; k++)
            {
                int f = queue[k].Factories;
                var box = entries[k].Q("cl-ongoing");
                if (f <= 0)
                {
                    if (box.style.display != DisplayStyle.None) box.style.display = DisplayStyle.None;
                    continue;
                }
                box.style.display = DisplayStyle.Flex;
                phase[k] = (phase[k] + Time.unscaledDeltaTime * AnimationFps * f / Hoi4Construction.MaxFactoriesPerItem) % AnimationFrames;
                Hoi4Sprites.Apply(box, Ongoing + (int)phase[k] + ".png");
            }
        }

        void Update()
        {
            if (panel == null) return;
            var construction = Hoi4Construction.Instance;
            if (Player == 0 || construction == null || !construction.IsReady || Hoi4.War.Hoi4War.Instance?.Conference != null)
            {
                if (open) Close();
                return;
            }
            if (Input.GetKeyDown(KeyCode.T)) Toggle();
            if (open && Input.GetKeyDown(KeyCode.Escape))
            {
                if (selected != null) Select(selected); else Close();
            }
            Hoi4UiHover.Report(this, open ? panel : null);
            Hoi4UiHover.Report(buildingsHover, open ? buildings : null);
            if (!open) return;

            if (shownCountry != Player)
            {
                shownCountry = Player;
                structureDirty = true;
            }
            if (structureDirty) Rebuild(construction);
            Animate(construction);
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh(construction);
        }
    }
}
