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
    ///   HOI4's order. While the screen is open unit counters are hidden, box selection and right clicks (diplomacy) are
    ///   off. With a building selected, clicking a province queues a level of it in that state, right clicking takes one
    ///   off the order there (the state panel stays
    ///   shut) and the map shows where it fits (Hoi4ConstructionMap); clicking the button again, Escape or closing
    ///   the screen drops the selection.
    /// - Supply buildings (naval base, supply hub, railway) are placed per province, over the supply map mode
    ///   (selecting one switches to it, dropping the selection switches back): a click queues a level in that
    ///   province, a right click takes one off. Railways: clicking a stretch's level number orders the whole
    ///   stretch one level up; clicking an order's number raises the level it builds to, right clicking lowers it.
    ///   A new railway takes two clicks: its start, which must be on the network (rail, a hub, a naval base or
    ///   another order), and its end, anywhere in the player's land; while the end is being picked the line
    ///   follows the cursor province by province, in yellow (Shift on the second click starts the next line from there; a right click drops the start, or
    ///   the order running through the province). Orders show in orange, with a roadblock where one ends nowhere. The map shows the
    ///   orders in bands (Hoi4ConstructionMap.ShowProvinces) and the rail levels as badges (Hoi4PortIcons).
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
        const string ProvinceBorderClass = "province"; // Hoi4BorderClassifier: same owner, same state

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
        // state boxes on the map while placing: state -> box, and the world point it sits on
        VisualElement boxLayer;
        readonly Dictionary<int, VisualElement> boxes = new Dictionary<int, VisualElement>();
        readonly Dictionary<int, Vector3> boxWorld = new Dictionary<int, Vector3>();
        int boxUsers = int.MinValue;
        string placing;
        int hoverState; // the state under the cursor while placing (its outline glows)
        readonly List<ushort> railTrail = new List<ushort>(); // the railway being laid out: the provinces the cursor went through
        ushort hoverProvince, railStart; // province buildings: the province under the cursor; a railway's first click
        global::Map.MapModes.MapMode modeBefore;
        bool tookMapMode; // the supply map mode is on because a supply building is selected

        /// <summary>The province building being placed (naval base, supply hub, railway), null otherwise.</summary>
        public static string PlacingSupply => Placing != null && Hoi4Construction.IsProvinceBuilding(Placing) ? Placing : null;
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
            Hoi4ConstructionMap.Hide();
            Hoi4UiHover.Remove(this);
            Hoi4UiHover.Remove(buildingsHover);
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

            boxLayer = new VisualElement { pickingMode = PickingMode.Ignore };
            boxLayer.AddToClassList("cm-layer");
            root.Add(boxLayer); // under the panel
            panel = tree.Instantiate().Q("construction-panel");
            Hoi4Sprites.Bind(panel);
            root.Add(panel);
            buildings = panel.Q(className: "co-buildings");
            lineList = panel.Q<ScrollView>("co-lines");
            Hoi4Scrollbar.ApplyAll(panel); // HOI4's scrollbars
            consumerRow = lineList.Q(className: "co-consumer");
            panel.Q("co-close").RegisterCallback<ClickEvent>(_ => Close());
            panel.Q("co-minimize").RegisterCallback<ClickEvent>(_ => Close());

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
            button.RegisterCallback<ClickEvent>(_ =>
            {
                if (!button.ClassListContains("locked")) Select(button);
            });
            return button;
        }

        void Select(VisualElement button)
        {
            selected?.RemoveFromClassList("selected");
            selected = selected == button ? null : button;
            selected?.AddToClassList("selected");
            placing = selected?.userData as string;
            // a supply building is placed over the supply map mode, like HOI4; back to the mode before when it is dropped
            bool supply = placing != null && Hoi4Construction.IsProvinceBuilding(placing);
            var modes = Engine.ArchonEngine.Instance?.MapModeManager;
            if (modes != null && supply && !tookMapMode)
            {
                modeBefore = modes.CurrentMode;
                tookMapMode = true;
                modes.SetMapMode(global::Map.MapModes.MapMode.Supply);
            }
            else if (modes != null && !supply && tookMapMode)
            {
                tookMapMode = false;
                modes.SetMapMode(modeBefore);
            }
            railStart = 0;
            hoverProvince = 0;
            Hoi4.Supply.Hoi4RailRenderer.SetPreview(null);
            if (supply) ClearBoxes();
            global::Map.Rendering.Labels.MapRegionLabels.Shown = placing != null && !supply; // state names while placing
            // HOI4 drops the borders between provinces of a state while placing a state-wide building
            string section = placing != null ? Hoi4StateData.Def(placing)?.Section : null;
            global::Map.Rendering.Border.BorderClassStyles.SetHidden(ProvinceBorderClass, section == "state" || section == "shared");
            hoverState = 0;
            if (placing == null) { Hoi4ConstructionMap.Hide(); ClearBoxes(); }
            nextRefresh = 0f; // repaint the map for the new building now
        }

        static string BuildingName(string building)
        {
            string name = LocalizationManager.Get(building);
            return string.IsNullOrEmpty(name) || name == building ? building : name;
        }

        /// <summary>From Hoi4UnitSelection's click interceptor. With a building selected the screen owns map clicks:
        /// left queues a level of it in the clicked state, right takes one off the order for it there (Shift: 5).
        /// Without one, clicks pass (left: state view, right: diplomacy; both close this screen).</summary>
        public bool OnClick(int button, Vector2 screen)
        {
            if (!open || placing == null) return false;
            var cam = Camera.main;
            if (cam == null || selector == null || shownCountry == 0) return true;
            ushort province = selector.GetProvinceAtScreenPosition(screen, cam);
            int state = province != 0 ? Hoi4Regions.State(province) : -1;
            if (state < 0) return true;
            int count = ShiftOr(1, 5);
            if (Hoi4Construction.IsProvinceBuilding(placing))
            {
                if (placing != Hoi4Construction.Railway || !ClickBadge(button, screen)) ClickProvince(button, province, count);
                nextRefresh = 0f;
                return true;
            }
            if (button == 0)
            {
                for (int i = 0; i < count; i++)
                    if (!gameState.TryExecuteCommand(new QueueConstructionCommand { CountryId = shownCountry, State = state, Building = placing }, out _))
                        break;
            }
            else
            {
                foreach (var item in Hoi4Construction.Instance.Queue(shownCountry))
                {
                    if (item.State != state || item.Building != placing) continue;
                    if (item.Count > count)
                        gameState.TryExecuteCommand(new SetConstructionCountCommand { CountryId = shownCountry, ItemId = item.Id, Count = item.Count - count }, out _);
                    else
                        Remove(item.Id);
                    break;
                }
            }
            nextRefresh = 0f;
            return true;
        }

        /// <summary>The railway tool on a rail level number: on a stretch of railway, the left button orders the
        /// whole stretch one level up; on an order, left raises the level it builds to and right lowers it (the order
        /// goes when nothing is left to build). False when no number is under the cursor.</summary>
        bool ClickBadge(int button, Vector2 screen)
        {
            var icons = Hoi4.Supply.Hoi4PortIcons.Instance;
            if (icons == null || !icons.TryPickBadge(screen, out bool order, out int id)) return false;
            if (order)
            {
                var item = Hoi4Construction.Instance.Item(shownCountry, id);
                if (item != null)
                    gameState.TryExecuteCommand(new SetRailOrderLevelCommand { CountryId = shownCountry, ItemId = id, Level = item.RailLevel + (button == 0 ? 1 : -1) }, out _);
            }
            else if (button == 0 && id < Hoi4.Supply.Hoi4RailRenderer.Chains.Count)
                gameState.TryExecuteCommand(new QueueRailwayCommand { CountryId = shownCountry, Path = new List<ushort>(Hoi4.Supply.Hoi4RailRenderer.Chains[id].Provinces) }, out _);
            return true;
        }

        /// <summary>A new railway starts where one can join the network: a province with rail, a supply hub or a
        /// naval base, or one a railway order of the player runs through.</summary>
        bool CanStartRail(ushort province)
        {
            if (Hoi4.Supply.Hoi4Railways.RailLevel(province) > 0 || Hoi4.Supply.Hoi4Railways.IsHubSite(province)
                || Hoi4.Supply.Hoi4Ports.Level(province) > 0) return true;
            foreach (var item in Hoi4Construction.Instance.Queue(shownCountry))
                if (item.Path != null && item.Path.Contains(province)) return true;
            return false;
        }

        /// <summary>The line being laid out, in yellow on the map: it follows the cursor province by province from the
        /// start (railTrail), like HOI4; moving back over it shortens it.</summary>
        void UpdateRailPreview()
        {
            if (placing != Hoi4Construction.Railway || railStart == 0) { railTrail.Clear(); Hoi4.Supply.Hoi4RailRenderer.SetPreview(null); return; }
            if (railTrail.Count == 0 || railTrail[0] != railStart) { railTrail.Clear(); railTrail.Add(railStart); }
            ushort at = hoverProvince;
            if (at != 0 && Hoi4.Units.Hoi4Occupation.ControllerOf(gameState, at) == shownCountry && !Hoi4Regions.IsImpassable(at))
            {
                int seen = railTrail.IndexOf(at);
                if (seen >= 0) railTrail.RemoveRange(seen + 1, railTrail.Count - seen - 1); // moved back along the line
                else
                {
                    // the next province the cursor entered; when it jumped (a fast move, over the screen), the
                    // shortest way there from the line's end
                    var step = RailPath(railTrail[railTrail.Count - 1], at);
                    if (step != null)
                        for (int k = 1; k < step.Count; k++)
                        {
                            int back = railTrail.IndexOf(step[k]);
                            if (back >= 0) railTrail.RemoveRange(back + 1, railTrail.Count - back - 1);
                            else railTrail.Add(step[k]);
                        }
                }
            }
            Hoi4.Supply.Hoi4RailRenderer.SetPreview(railTrail.Count >= 2 ? new List<ushort>(railTrail) : null);
        }

        /// <summary>A map click while a supply building is selected (see the class summary).</summary>
        void ClickProvince(int button, ushort province, int count)
        {
            var construction = Hoi4Construction.Instance;
            if (placing == Hoi4Construction.Railway)
            {
                if (button != 0)
                {
                    if (railStart != 0) { railStart = 0; return; }
                    foreach (var item in construction.Queue(shownCountry))
                        if (item.Path != null && item.Path.Contains(province)) { Remove(item.Id); break; }
                    return;
                }
                if (Hoi4Regions.IsWater(province) || Hoi4Regions.IsImpassable(province)
                    || Hoi4.Units.Hoi4Occupation.ControllerOf(gameState, province) != shownCountry) return;
                if (railStart == 0 || railStart == province)
                {
                    if (CanStartRail(province)) railStart = province;
                    return;
                }
                UpdateRailPreview(); // the line as the cursor drew it, ending here
                var path = railTrail.Count >= 2 && railTrail[railTrail.Count - 1] == province ? new List<ushort>(railTrail) : RailPath(railStart, province);
                railTrail.Clear();
                if (path != null)
                    gameState.TryExecuteCommand(new QueueRailwayCommand { CountryId = shownCountry, Path = path }, out _);
                railStart = ShiftOr(0, province) != 0 ? province : (ushort)0;
                return;
            }
            if (button == 0)
            {
                for (int i = 0; i < count; i++)
                    if (!gameState.TryExecuteCommand(new QueueProvinceConstructionCommand { CountryId = shownCountry, Province = province, Building = placing }, out _))
                        break;
                return;
            }
            foreach (var item in construction.Queue(shownCountry))
            {
                if (item.Province != province || item.Building != placing) continue;
                if (item.Count > count)
                    gameState.TryExecuteCommand(new SetConstructionCountCommand { CountryId = shownCountry, ItemId = item.Id, Count = item.Count - count }, out _);
                else
                    Remove(item.Id);
                break;
            }
        }

        /// <summary>The shortest way between two provinces through passable land the player controls (fewest
        /// provinces): how the line being laid out bridges a jump of the cursor. Null when there is none. Presentation: the command checks the path it is given.</summary>
        List<ushort> RailPath(ushort from, ushort to)
        {
            const int RailStep = 1, NewStep = 1; // every step alike: the line goes where the cursor leads it
            var dist = new Dictionary<ushort, int> { [from] = 0 };
            var prev = new Dictionary<ushort, ushort>();
            var open = new SortedSet<(int d, ushort p)> { (0, from) };
            var neighbours = new List<ushort>();
            while (open.Count > 0)
            {
                var (d, u) = open.Min;
                open.Remove(open.Min);
                if (u == to) break;
                if (d > dist[u]) continue;
                neighbours.Clear();
                gameState.Adjacencies.GetNeighborsWhere(u, p => !Hoi4Regions.IsWater(p) && !Hoi4Regions.IsImpassable(p)
                    && Hoi4.Units.Hoi4Occupation.ControllerOf(gameState, p) == shownCountry, neighbours);
                foreach (ushort v in neighbours)
                {
                    int nd = d + (Hoi4.Supply.Hoi4Railways.RailLevel(u, v) > 0 ? RailStep : NewStep);
                    if (dist.TryGetValue(v, out int had) && had <= nd) continue;
                    dist[v] = nd;
                    prev[v] = u;
                    open.Add((nd, v));
                }
            }
            if (!prev.ContainsKey(to)) return null;
            var path = new List<ushort> { to };
            while (path[path.Count - 1] != from) path.Add(prev[path[path.Count - 1]]);
            path.Reverse();
            return path;
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
            // the state view lives behind the left-side panels: drop the selected state (and its panel)
            Engine.ArchonEngine.Instance?.ProvinceSelector?.ClearSelection();
            if (Hoi4.Units.Hoi4UnitSelection.Instance != null) Hoi4.Units.Hoi4UnitSelection.Instance.Clear(); // and the divisions
            if (Hoi4DiplomacyPanel.IsOpen) Hoi4DiplomacyPanel.Instance.Close(); // back to the map mode before diplomacy
            if (Hoi4ProductionPanel.IsOpen) Hoi4ProductionPanel.Instance.Toggle();
            if (Hoi4DeploymentPanel.IsOpen) Hoi4DeploymentPanel.Instance.Close(); // they share the left side
            if (Hoi4ResearchPanel.IsOpen) Hoi4ResearchPanel.Instance.Close();
            if (Hoi4DiplomacyPanel.IsOpen) Hoi4DiplomacyPanel.Instance?.Close(); // construction takes the map
            open = true;
            panel.style.display = DisplayStyle.Flex;
            if (Hoi4.Units.Hoi4UnitCounters.Instance != null) Hoi4.Units.Hoi4UnitCounters.Instance.Hidden = true;
            Hoi4.Units.Hoi4UnitSelection.Instance?.Clear();
            structureDirty = true;
            nextRefresh = 0f;
        }

        public void Close()
        {
            if (selected != null) Select(selected);
            Hoi4ConstructionMap.Hide();
            global::Map.Rendering.Labels.MapRegionLabels.Shown = false;
            global::Map.Rendering.Border.BorderClassStyles.SetHidden(ProvinceBorderClass, false);
            ClearBoxes();
            // show the counters again, unless a peace conference (which hides them too) closed the screen
            if (open && Hoi4.Units.Hoi4UnitCounters.Instance != null && Hoi4.War.Hoi4War.Instance?.Conference == null)
                Hoi4.Units.Hoi4UnitCounters.Instance.Hidden = false;
            open = false;
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
            var speed = construction.ConstructionSpeed(shownCountry).ToFloat();
            var speedLabel = panel.Q<Label>("co-speed");
            speedLabel.text = (speed >= 0 ? "+" : "") + Mathf.RoundToInt(speed * 100) + "%";
            speedLabel.EnableInClassList("negative", speed < 0);
            consumerRow.Q<Label>("co-consumer-count").text = cg.ToString();
            // buildings the country can't build yet (not researched) are greyed out and can't be selected
            panel.Q("co-sections").Query(className: "co-building").ForEach(b =>
            {
                string building = b.userData as string;
                bool locked = System.Array.IndexOf(Hoi4Construction.Buildable, building) >= 0 && !construction.Researched(shownCountry, building);
                b.EnableInClassList("locked", locked);
                if (locked && b == selected) Select(b);
            });

            for (int k = 0; k < entries.Count && k < queue.Count; k++)
            {
                var item = queue[k];
                var e = entries[k];
                e.Q<Label>("cl-count").text = item.Count.ToString();
                e.Q("cl-add").EnableInClassList("disabled", construction.RoomOf(item) <= 0);
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

        // ---- state boxes ------------------------------------------------------------------------------

        void ClearBoxes()
        {
            boxLayer?.Clear();
            boxes.Clear();
            boxWorld.Clear();
            boxUsers = int.MinValue;
        }

        /// <summary>One box per state whose buildings work for the player (rebuilt when ownership or control
        /// changes), in the middle of the state: the mean of its land provinces' centres.</summary>
        void BuildBoxes()
        {
            ClearBoxes();
            var counters = Hoi4.Units.Hoi4UnitCounters.Instance;
            if (counters == null || boxLayer == null) return;
            boxUsers = Hoi4Buildings.UsersVersion(gameState);
            foreach (var kv in Hoi4Buildings.Users(gameState))
            {
                if (kv.Value != shownCountry) continue;
                int s = kv.Key;
                Vector3 sum = Vector3.zero;
                int n = 0;
                foreach (ushort p in Hoi4Regions.ProvincesOf(s))
                    if (!Hoi4Regions.IsWater(p) && counters.TryGetProvincePoint(p, out var point)) { sum += point; n++; }
                if (n == 0) continue;
                var world = sum / n;
                var box = new VisualElement { pickingMode = PickingMode.Ignore };
                box.AddToClassList("cm-box");
                box.Add(Hoi4Sprites.Make("cm-bg"));
                box.Add(BoxLabel("cm-levels", "center"));
                box.Add(Hoi4Sprites.Make("cm-infra-icon"));
                box.Add(BoxLabel("cm-infra", null));
                box.Add(Hoi4Sprites.Make("cm-slots-icon"));
                box.Add(BoxLabel("cm-slots", null));
                boxLayer.Add(box);
                boxes[s] = box;
                boxWorld[s] = world;
            }
        }

        static Label BoxLabel(string cls, string align)
        {
            var l = new Label { pickingMode = PickingMode.Ignore, enableRichText = true };
            l.AddToClassList("hoi4-label");
            l.AddToClassList("spr");
            l.AddToClassList(cls);
            if (align != null) l.AddToClassList(align);
            return l;
        }

        /// <summary>"built/most", with the queued levels in gold: "8+2/10".</summary>
        static string Levels(int built, int queued, int most) =>
            queued > 0 ? $"{built}<color=#f7d45a>+{queued}</color>/{most}" : $"{built}/{most}";

        void UpdateBoxes(Hoi4Construction construction)
        {
            if (boxUsers != Hoi4Buildings.UsersVersion(gameState)) BuildBoxes();
            foreach (var kv in boxes)
            {
                int s = kv.Key;
                var box = kv.Value;
                box.userData = construction.CanHold(s, placing); // no box where it can't be built (dockyard inland)
                box.Q<Label>(className: "cm-levels").text = Levels(Hoi4Buildings.Level(s, placing),
                    construction.Queued(s, placing), construction.MaxLevel(s, placing));
                int bonus = Hoi4Buildings.Level(s, "infrastructure") * 100 / Hoi4Construction.MaxInfrastructure;
                var infra = box.Q<Label>(className: "cm-infra");
                infra.text = $"{bonus}%";
                infra.EnableInClassList("mid", bonus >= 40 && bonus < 80);
                infra.EnableInClassList("low", bonus < 40);
                box.Q<Label>(className: "cm-slots").text = Levels(Hoi4Buildings.SharedUsed(s),
                    construction.Queued(s, null, true), Hoi4Buildings.SharedSlots(s));
            }
        }

        /// <summary>The state under the cursor (none over the screen's own UI); true when it changed.</summary>
        bool UpdateHover()
        {
            int state = 0;
            ushort province = 0;
            var cam = Camera.main;
            if (cam != null && selector != null && !global::Map.CameraControllers.BaseCameraController.ScrollCaptured)
            {
                ushort p = selector.GetProvinceAtScreenPosition(Input.mousePosition, cam);
                if (p != 0 && !Hoi4Regions.IsWater(p)) { state = Mathf.Max(0, Hoi4Regions.State(p)); province = p; }
            }
            if (state == hoverState && province == hoverProvince) return false;
            hoverState = state;
            hoverProvince = province;
            return true;
        }

        void ShowMap(bool supply)
        {
            UpdateRailPreview();
            if (supply) Hoi4ConstructionMap.ShowProvinces(gameState, shownCountry, placing, hoverProvince, railStart);
            else Hoi4ConstructionMap.Show(gameState, shownCountry, placing, hoverState);
        }

        void LateUpdate()
        {
            if (boxes.Count == 0 || boxLayer?.panel == null) return;
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var kv in boxes)
            {
                var sp = cam.WorldToScreenPoint(boxWorld[kv.Key]);
                if (sp.z <= 0f || kv.Value.userData is bool holds && !holds) { kv.Value.style.display = DisplayStyle.None; continue; }
                var pp = RuntimePanelUtils.ScreenToPanel(boxLayer.panel, new Vector2(sp.x, Screen.height - sp.y));
                kv.Value.style.display = DisplayStyle.Flex;
                kv.Value.style.left = pp.x;
                kv.Value.style.top = pp.y;
            }
        }

        void Update()
        {
            if (panel == null) return;
            var construction = Hoi4Construction.Instance;
            if (Player == 0 || construction == null || !construction.IsReady || Hoi4.War.Hoi4War.Instance?.Conference != null)
            {
                if (open) Close();
                Hoi4ConstructionMap.Hide();
                ClearBoxes();
                return;
            }
            if (global::Utils.KeyboardFocus.KeyDown(KeyCode.T)) Toggle();
            if (open && global::Utils.KeyboardFocus.KeyDown(KeyCode.Escape))
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
            bool supply = placing != null && Hoi4Construction.IsProvinceBuilding(placing);
            if (placing != null && UpdateHover()) ShowMap(supply);
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh(construction);
            if (placing != null)
            {
                ShowMap(supply);
                if (!supply) UpdateBoxes(construction);
            }
        }
    }
}
