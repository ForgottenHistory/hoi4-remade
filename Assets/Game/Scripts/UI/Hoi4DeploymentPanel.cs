using System.Collections;
using System.Collections.Generic;
using System.Text;
using Core;
using Hoi4.Deployment;
using Hoi4.Map;
using Hoi4.Units;
using UnityEngine;
using UnityEngine.UIElements;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's recruit &amp; deploy screen (interface/countrydeploymentview.gui), left of the map, opened by the top
    /// bar's recruit button or U. It shares the left side with the production and construction screens: opening one
    /// closes the others. View of the UI Presenter pattern: structure is Resources/Hoi4UI/DeploymentPanel.uxml
    /// (+ DeploymentOrder.uxml per order, DeploymentLine.uxml per division in training), look is
    /// DeploymentPanelStyle.uss + Hoi4Theme.uss. Every change is a deployment command (Hoi4DeploymentCommands).
    /// - Equipment priority rows (reinforcements, upgrades, supply trucks, operations, garrisons, raids): low (red),
    ///   normal (yellow) or high (green). Reinforcements is real: the field's share of equipment against the training
    ///   lines (Hoi4Equipment), its bar the field's equipment level. The others are shown as in HOI4, not changeable
    ///   (the simulation has no such uses of equipment yet).
    /// - Orders (a series: each line trains the order's amount of divisions in turn, infinite by default):
    ///   template icon (with the template's reinforcement priority chevrons: reserve down, normal none, elite
    ///   up; the order's own priority is only its dots), name, -/+ the amount (infinite shows HOI4's infinity icon, - is off
    ///   there; + from infinite sets 1, then counts up, Shift: 5; - from 1 goes back to infinite), low/normal/high priority, deploy location (click it, then a province the player holds; Escape or a
    ///   right click cancels), Add Unit (one more line), deploy every division with 20% training,
    ///   cancel. Summary row: move the order
    ///   up or down, mean equipment, how many divisions, collapse. One row per line: its division's name, equipment and training
    ///   (yellow while stalled), its place in the series (2/5, 3/infinite), deploy early (from 20%), cancel.
    /// - Division templates window: the country's templates with their icons and priority chevrons (click to cycle
    ///   reserve, normal, elite: HOI4 sets it in the division designer); Train starts an order of one at the
    ///   capital. Edit, delete, the designer and decommissioning wait for a division designer (greyed out).
    /// Values refresh four times a real second (presentation, not game time); the order list is rebuilt only when the
    /// orders or their lines change.
    /// </summary>
    public sealed class Hoi4DeploymentPanel : MonoBehaviour
    {
        const float RefreshSeconds = 0.25f;
        const string Gfx = "gfx/interface/deployment/";
        const string UnitIcons = "gfx/interface/unitpanel/icons/";
        const float EntryBarWidth = 68f, PriorityBarWidth = 72f; // progressbartype sizes (not their textures')
        static readonly FP MinDeployTraining = FP.FromFraction(2, 10); // MIN_DIVISION_DEPLOYMENT_TRAINING
        // deploy_entry rows: name, entry background, icon (supply_deploy_entry is the taller one)
        static readonly (string name, string entry, string icon)[] PriorityRows =
        {
            ("Reinforcements", "entry_reinforcements", "icon_reinforcements"),
            ("Upgrades", "entry_upgrades", "icon_upgrades"),
            ("Supply Trucks", "entry_supply", "icon_supply"),
            ("Operations", "entry_operations", "icon_operations"),
            ("Garrisons", "entry_garrisons", "icon_garrisons"),
            ("Raids", "entry_reinforcements", "icon_raids"),
        };

        public static Hoi4DeploymentPanel Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance.open;

        UIDocument document;
        VisualElement root, window, templatesWindow, ordersHost, priorityHost;
        ScrollView templateList;
        VisualTreeAsset orderTree, lineTree;
        GameState gameState;
        global::Map.Interaction.ProvinceSelector selector;
        readonly object templatesHover = new object();
        readonly Dictionary<int, VisualElement> orderViews = new Dictionary<int, VisualElement>();
        readonly Dictionary<long, VisualElement> lineViews = new Dictionary<long, VisualElement>(); // order id << 32 | line id
        readonly HashSet<int> collapsed = new HashSet<int>();
        VisualElement reinforcementBar;
        readonly List<VisualElement> reinforcementDots = new List<VisualElement>();
        readonly Dictionary<string, VisualElement> templateStrips = new Dictionary<string, VisualElement>();
        string shownStructure = "", shownTemplates;
        int pickingOrder; // the order whose location the next map click sets (0: none)
        bool open, dirty = true;
        float nextRefresh;
        ushort shownCountry;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4DeploymentPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4DeploymentPanel>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Hoi4UiHover.Remove(this);
            Hoi4UiHover.Remove(templatesHover);
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.ProvinceSelector == null)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            selector = Engine.ArchonEngine.Instance.ProvinceSelector;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/DeploymentPanel");
            orderTree = Resources.Load<VisualTreeAsset>("Hoi4UI/DeploymentOrder");
            lineTree = Resources.Load<VisualTreeAsset>("Hoi4UI/DeploymentLine");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/DeploymentPanelStyle");
            if (tree == null || orderTree == null || lineTree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4DeploymentPanel: Resources/Hoi4UI/DeploymentPanel.uxml, its entries or stylesheets are missing, panel disabled", "game_ui");
                yield break;
            }

            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4DeploymentPanel: no UIDocument panel settings in the scene, panel disabled", "game_ui");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 45; // like the production and construction screens
            yield return null;

            var docRoot = document.rootVisualElement;
            docRoot.pickingMode = PickingMode.Ignore;
            docRoot.styleSheets.Add(theme);
            docRoot.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(docRoot);
            root = tree.Instantiate().Q("deployment-panel");
            Hoi4Sprites.Bind(root);
            docRoot.Add(root);
            window = root.Q("dp-window");
            templatesWindow = root.Q("dp-templates");
            ordersHost = root.Q("dp-orders");
            priorityHost = root.Q("dp-priorities");
            templateList = root.Q<ScrollView>("dt-list");
            root.Q("dp-close").RegisterCallback<ClickEvent>(evt => Close());
            BuildPriorityRows();

            while (Hoi4Deployment.Instance == null) yield return null;
            Hoi4Deployment.Instance.Changed += c => { if (c == shownCountry) dirty = true; };
            // selecting units closes the screen (the division panel takes the left side), as in HOI4
            while (Hoi4UnitSelection.Instance == null) yield return null;
            Hoi4UnitSelection.Instance.Changed += () => { if (open && Hoi4UnitSelection.Instance.Selected.Count > 0) Close(); };
        }

        static ushort Player
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        static int ShiftOr(int plain, int shifted) =>
            Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift) ? shifted : plain;

        static Label MakeLabel(string text, params string[] classes)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("hoi4-label");
            foreach (string c in classes) l.AddToClassList(c);
            return l;
        }

        /// <summary>An element showing a PNG at its own size, at (x, y) or centred on it.</summary>
        static VisualElement Picture(string path, float x, float y, bool centred)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.AddToClassList("dpp-icon");
            var tex = Hoi4UiAssets.Texture(path);
            if (tex != null)
            {
                e.style.backgroundImage = new StyleBackground(tex);
                e.style.width = tex.width;
                e.style.height = tex.height;
                e.style.left = centred ? x - tex.width / 2f : x;
                e.style.top = centred ? y - tex.height / 2f : y;
            }
            return e;
        }

        static void SetBar(VisualElement clip, float value, float width) =>
            clip.style.width = Mathf.Round(width * Mathf.Clamp01(value));

        // ---- equipment priority rows -----------------------------------------------------------------------

        void BuildPriorityRows()
        {
            priorityHost.Clear();
            foreach (var (name, entry, icon) in PriorityRows)
            {
                bool real = name == "Reinforcements";
            var row = new VisualElement
            {
                pickingMode = PickingMode.Position,
                tooltip = real ? "Reinforcing the divisions in the field: equipment goes to the highest priority first, equal shares within one priority"
                               : $"{name}: equipment priority (not changeable yet)",
            };
                row.AddToClassList("dpp");
                var bg = Hoi4Sprites.Make("dpp-bg");
                if (entry == "entry_supply") bg.AddToClassList("tall");
                Hoi4Sprites.Apply(bg, Gfx + entry + ".png");
                row.Add(bg);
                row.Add(Hoi4Sprites.Make("dpp-icon-bg"));
                row.Add(Picture(Gfx + icon + ".png", 48, 24, true));
                row.Add(Hoi4Sprites.Make("dpp-title-bg"));
                row.Add(MakeLabel(name, "center", "dpp-name"));
                row.Add(Hoi4Sprites.Make("dpp-meter-bg"));
                row.Add(Hoi4Sprites.Make("dpp-eq-icon"));
                row.Add(Hoi4Sprites.Make("dpp-bar-bg"));
                var clip = new VisualElement { pickingMode = PickingMode.Ignore };
                clip.AddToClassList("dpp-bar-clip");
                clip.Add(Hoi4Sprites.Make("dpp-bar-fill"));
                SetBar(clip, 1f, PriorityBarWidth);
                row.Add(clip);
                if (name == "Reinforcements") reinforcementBar = clip;
                for (int i = 0; i < 3; i++)
                {
                    var dot = Hoi4Sprites.Make("dpp-dot", "p" + i);
                    dot.style.left = 415 + 20 * i;
                    if (i == Hoi4Deployment.Normal) dot.AddToClassList("on");
                    if (real)
                    {
                        int priority = i;
                        dot.AddToClassList("hoi4-button");
                        dot.pickingMode = PickingMode.Position;
                        dot.tooltip = new[] { "Low priority", "Normal priority", "High priority" }[i];
                        dot.RegisterCallback<ClickEvent>(evt =>
                            gameState.TryExecuteCommand(new SetReinforcementPriorityCommand { CountryId = shownCountry, Priority = priority }, out _));
                        reinforcementDots.Add(dot);
                    }
                    row.Add(dot);
                }
                priorityHost.Add(row);
            }
        }

        // ---- open / close ---------------------------------------------------------------------------------

        /// <summary>The top bar's recruit button.</summary>
        public void Toggle()
        {
            if (open) Close(); else Open();
        }

        void Open()
        {
            if (root == null || Player == 0) return;
            if (Hoi4ProductionPanel.IsOpen) Hoi4ProductionPanel.Instance.Toggle(); // they share the left side
            if (Hoi4ConstructionPanel.IsOpen) Hoi4ConstructionPanel.Instance.Close();
            open = true;
            root.style.display = DisplayStyle.Flex;
            Hoi4UnitSelection.Instance?.Clear(); // the division panel would sit over the screen
            dirty = true;
            shownTemplates = null;
            nextRefresh = 0f;
        }

        public void Close()
        {
            open = false;
            SetPicking(0);
            if (root != null) root.style.display = DisplayStyle.None;
        }

        // ---- map clicks: the deploy location --------------------------------------------------------------

        void SetPicking(int orderId)
        {
            pickingOrder = orderId;
            foreach (var kv in orderViews) kv.Value.Q("dpo-location").EnableInClassList("picking", kv.Key == orderId);
        }

        /// <summary>Click interceptor (Hoi4UnitSelection): while a location is being picked, a left click on a province
        /// the player holds sets it, a right click cancels. True when the click was used.</summary>
        public bool OnClick(int button, Vector2 screen)
        {
            if (!open || pickingOrder == 0) return false;
            if (button == 1) { SetPicking(0); return true; }
            var cam = Camera.main;
            ushort province = cam != null && selector != null ? selector.GetProvinceAtScreenPosition(screen, cam) : (ushort)0;
            if (province != 0 && Hoi4Deployment.Instance.CanDeployTo(shownCountry, province))
            {
                gameState.TryExecuteCommand(new SetDeploymentLocationCommand { CountryId = shownCountry, OrderId = pickingOrder, Province = province }, out _);
                SetPicking(0);
                nextRefresh = 0f;
            }
            return true; // a click elsewhere keeps picking (and selects nothing)
        }

        // ---- commands ---------------------------------------------------------------------------------------

        void Train(string template)
        {
            gameState.TryExecuteCommand(new CreateDeploymentOrderCommand { CountryId = shownCountry, Template = template, Lines = 1 }, out _);
        }

        /// <summary>The series amount: + from infinite sets 1, then counts up (Shift: 5); - counts down, from 1 back to
        /// infinite, and does nothing at infinite.</summary>
        void ChangeAmount(int orderId, int sign)
        {
            var order = Hoi4Deployment.Instance.GetOrder(shownCountry, orderId);
            if (order == null) return;
            int to;
            if (order.Amount == 0) { if (sign < 0) return; to = 1; }
            else if (sign < 0 && order.Amount == 1) to = 0;
            else to = Mathf.Clamp(order.Amount + sign * ShiftOr(1, 5), 1, Hoi4Deployment.MaxAmount);
            if (to != order.Amount)
                gameState.TryExecuteCommand(new SetDeploymentAmountCommand { CountryId = shownCountry, OrderId = orderId, Amount = to }, out _);
        }

        void DeployAll(int orderId)
        {
            var order = Hoi4Deployment.Instance.GetOrder(shownCountry, orderId);
            if (order == null) return;
            var ids = new List<int>();
            foreach (var l in order.Lines) if (l.Started && l.Training >= MinDeployTraining) ids.Add(l.Id);
            foreach (int id in ids)
                gameState.TryExecuteCommand(new DeployEarlyCommand { CountryId = shownCountry, OrderId = orderId, LineId = id }, out _);
        }

        // ---- orders -----------------------------------------------------------------------------------------

        /// <summary>Orders and their lines, as ids: the list is rebuilt when this changes.</summary>
        static string Structure(IReadOnlyList<Hoi4Deployment.Order> orders)
        {
            var sb = new StringBuilder();
            foreach (var o in orders)
            {
                sb.Append(o.Id).Append(':');
                foreach (var l in o.Lines) sb.Append(l.Id).Append(',');
                sb.Append(';');
            }
            return sb.ToString();
        }

        void Rebuild(IReadOnlyList<Hoi4Deployment.Order> orders)
        {
            ordersHost.Clear();
            orderViews.Clear();
            lineViews.Clear();
            foreach (var order in orders)
            {
                var view = orderTree.Instantiate().Q(className: "dpo");
                Hoi4Sprites.Bind(view);
                int id = order.Id;
                string main = Hoi4Divisions.Instance?.TemplateMain(Hoi4Deployment.Instance.Tag(shownCountry), order.Template);
                Hoi4Sprites.Apply(view.Q("dpo-icon"), UnitIcon(main));
                var orderName = view.Q<Label>("dpo-name");
                orderName.text = order.Template;
                Hoi4TextFit.Fit(orderName);
                view.Q("dpo-sub").RegisterCallback<ClickEvent>(evt => ChangeAmount(id, -1));
                view.Q("dpo-add").RegisterCallback<ClickEvent>(evt => ChangeAmount(id, +1));
                for (int p = 0; p < 3; p++)
                {
                    int priority = p;
                    view.Q($"dpo-prio-{p}").RegisterCallback<ClickEvent>(evt =>
                        gameState.TryExecuteCommand(new SetDeploymentPriorityCommand { CountryId = shownCountry, OrderId = id, Priority = priority }, out _));
                }
                view.Q("dpo-location").RegisterCallback<ClickEvent>(evt => SetPicking(pickingOrder == id ? 0 : id));
                view.Q("dpo-add-line").RegisterCallback<ClickEvent>(evt =>
                    gameState.TryExecuteCommand(new AddDeploymentLinesCommand { CountryId = shownCountry, OrderId = id, Lines = 1 }, out _));
                view.Q("dpo-deploy").RegisterCallback<ClickEvent>(evt => DeployAll(id));
                view.Q("dpo-cancel").RegisterCallback<ClickEvent>(evt =>
                    gameState.TryExecuteCommand(new RemoveDeploymentOrderCommand { CountryId = shownCountry, OrderId = id }, out _));
                view.Q("dpo-up").RegisterCallback<ClickEvent>(evt =>
                    gameState.TryExecuteCommand(new MoveDeploymentOrderCommand { CountryId = shownCountry, OrderId = id, Delta = -1 }, out _));
                view.Q("dpo-down").RegisterCallback<ClickEvent>(evt =>
                    gameState.TryExecuteCommand(new MoveDeploymentOrderCommand { CountryId = shownCountry, OrderId = id, Delta = +1 }, out _));
                view.Q("dpo-collapse").RegisterCallback<ClickEvent>(evt =>
                {
                    if (!collapsed.Add(id)) collapsed.Remove(id);
                    dirty = true;
                });
                bool isCollapsed = collapsed.Contains(id);
                view.Q("dpo-collapse").EnableInClassList("collapsed", isCollapsed);
                view.Q("dpo-location").EnableInClassList("picking", pickingOrder == id);

                var linesHost = view.Q("dpo-lines");
                if (!isCollapsed)
                    for (int k = 0; k < order.Lines.Count; k++)
                    {
                        var line = order.Lines[k];
                        var lv = lineTree.Instantiate().Q(className: "dpl");
                        Hoi4Sprites.Bind(lv);
                        int lineId = line.Id;
                        var lineName = lv.Q<Label>("dpl-name");
                        lineName.text = line.Division.Name;
                        Hoi4TextFit.Fit(lineName);
                        lv.Q("dpl-deploy").RegisterCallback<ClickEvent>(evt =>
                            gameState.TryExecuteCommand(new DeployEarlyCommand { CountryId = shownCountry, OrderId = id, LineId = lineId }, out _));
                        lv.Q("dpl-cancel").RegisterCallback<ClickEvent>(evt =>
                            gameState.TryExecuteCommand(new RemoveDeploymentLineCommand { CountryId = shownCountry, OrderId = id, LineId = lineId }, out _));
                        linesHost.Add(lv);
                        lineViews[(long)id << 32 | (uint)lineId] = lv;
                    }
                ordersHost.Add(view);
                orderViews[id] = view;
            }
        }

        static string UnitIcon(string main)
        {
            if (!string.IsNullOrEmpty(main) && Hoi4UiAssets.Texture(UnitIcons + main + ".png") != null) return UnitIcons + main + ".png";
            return UnitIcons + "infantry.png";
        }

        void RefreshOrders(IReadOnlyList<Hoi4Deployment.Order> orders)
        {
            var deployment = Hoi4Deployment.Instance;
            foreach (var order in orders)
            {
                if (!orderViews.TryGetValue(order.Id, out var view)) continue;
                bool infinite = order.Amount == 0;
                view.Q<Label>("dpo-amount").text = infinite ? "" : order.Amount.ToString();
                view.Q("dpo-infinity").style.display = infinite ? DisplayStyle.Flex : DisplayStyle.None;
                view.Q("dpo-sub").EnableInClassList("disabled", infinite);
                view.Q("dpo-sub").tooltip = infinite ? "Trains without end: + sets how many each line trains" : order.Amount == 1 ? "Back to training without end" : "One fewer per line (Shift: 5)";
                view.Q("dpo-add").tooltip = infinite ? "Train one division per line, then stop" : "One more per line (Shift: 5)";
                SetPriorityStrip(view.Q("dpo-strip"), Hoi4Divisions.Instance.TemplatePriority(deployment.Tag(shownCountry), order.Template), false);
                view.Q("dpo-add").EnableInClassList("disabled", order.Amount >= Hoi4Deployment.MaxAmount);
                view.Q("dpo-add-line").EnableInClassList("disabled", order.Lines.Count >= Hoi4Deployment.MaxAmount);
                for (int p = 0; p < 3; p++) view.Q($"dpo-prio-{p}").EnableInClassList("on", order.Priority == p);

                var locText = view.Q<Label>("dpo-location-text");
                bool hasLocation = order.Province != 0 && deployment.CanDeployTo(shownCountry, order.Province);
                int state = order.Province != 0 ? Hoi4Regions.State(order.Province) : 0;
                locText.text = pickingOrder == order.Id ? "Click a province" : hasLocation && state > 0 ? Hoi4StatePresenter.StateName(state) : "No location set";
                locText.EnableInClassList("none", !hasLocation && pickingOrder != order.Id);

                float eqSum = 0f;
                bool anyDeployable = false, anyStalled = false;
                for (int k = 0; k < order.Lines.Count; k++)
                {
                    var line = order.Lines[k];
                    var d = line.Division;
                    float equipment = d.EquipmentLevel.ToFloat(), training = line.Training.ToFloat();
                    eqSum += equipment;
                    bool deployable = line.Started && line.Training >= MinDeployTraining;
                    anyDeployable |= deployable;
                    // stalled: waiting for manpower, or trained as far as its equipment allows (no equipment to go on)
                    bool stalled = !line.Started || (line.Training < FP.One && line.Training >= d.EquipmentLevel);
                    anyStalled |= stalled;
                    if (!lineViews.TryGetValue((long)order.Id << 32 | (uint)line.Id, out var lv)) continue;
                    SetBar(lv.Q("dpl-eq"), equipment, EntryBarWidth);
                    lv.Q(className: "dpl-eq-fill").EnableInClassList("stalled", stalled);
                    lv.Q(className: "dpl-tr-fill").EnableInClassList("stalled", stalled);
                    lv.Q("dpl-eq-icon").EnableInClassList("full", equipment >= 0.999f);
                    SetBar(lv.Q("dpl-tr"), training, EntryBarWidth);
                    int level = Hoi4Division.LevelOf(Hoi4Deployment.DeployExperience(line.Training));
                    var levelIcon = lv.Q("dpl-level");
                    if (!(levelIcon.userData is int shownLevel) || shownLevel != level)
                    {
                        Hoi4Sprites.Apply(levelIcon, Gfx + $"unit_level_{level}.png");
                        levelIcon.userData = level;
                    }
                    levelIcon.tooltip = $"Deploys {Hoi4Division.LevelNames[level]}" + (level == 0 ? " (Trained when fully trained)" : "");
                    lv.Q<Label>("dpl-number").text = infinite ? $"{line.Series}/" : $"{line.Series}/{order.Amount}";
                    lv.Q("dpl-infinity").style.display = infinite ? DisplayStyle.Flex : DisplayStyle.None;
                    lv.Q("dpl-eq-area").tooltip = !line.Started ? $"Waiting for manpower: {d.Manpower:N0} men"
                        : stalled ? $"Equipment {Mathf.RoundToInt(equipment * 100)}%: training waits for more equipment"
                        : $"Equipment {Mathf.RoundToInt(equipment * 100)}%: training goes no higher";
                    lv.Q("dpl-tr-area").tooltip = $"Training {Mathf.RoundToInt(training * 100)}% of {order.TrainingDays} days";
                    var deploy = lv.Q("dpl-deploy");
                    deploy.EnableInClassList("disabled", !deployable);
                    deploy.tooltip = deployable ? "Deploy now, with the equipment it has" : "Can deploy from 20% training";
                }
                float eq = order.Lines.Count > 0 ? eqSum / order.Lines.Count : 0f;
                SetBar(view.Q("dpo-eq"), eq, EntryBarWidth);
                view.Q(className: "dpo-eq-fill").EnableInClassList("stalled", anyStalled);
                view.Q("dpo-eq-icon").EnableInClassList("full", eq >= 0.999f);
                view.Q<Label>("dpo-count").text = order.Lines.Count == 1 ? "1 Division" : $"{order.Lines.Count} Divisions";
                view.Q("dpo-deploy").EnableInClassList("disabled", !anyDeployable);
            }
        }

        // ---- templates -----------------------------------------------------------------------------------

        void RebuildTemplates()
        {
            templateList.Clear();
            templateStrips.Clear();
            var divisions = Hoi4Divisions.Instance;
            string tag = Hoi4Deployment.Instance.Tag(shownCountry);
            shownTemplates = tag;
            if (divisions == null) return;
            var inUse = new HashSet<string>();
            foreach (var d in divisions.All.Values)
                if (d.Tag == tag) inUse.Add(d.Template);
            foreach (string name in divisions.Templates(tag))
            {
                string template = name;
                var entry = new VisualElement { pickingMode = PickingMode.Position };
                entry.AddToClassList("dte");
                entry.Add(Hoi4Sprites.Make("dte-bg"));
                var icon = Hoi4Sprites.Make("dte-icon");
                Hoi4Sprites.Apply(icon, UnitIcon(divisions.TemplateMain(tag, template)));
                entry.Add(icon);
                var strip = Hoi4Sprites.Make("dte-strip", "hoi4-button");
                strip.pickingMode = PickingMode.Position;
                strip.RegisterCallback<ClickEvent>(evt =>
                {
                    int next = (divisions.TemplatePriority(tag, template) + 1) % 3;
                    gameState.TryExecuteCommand(new SetTemplatePriorityCommand { CountryId = shownCountry, Template = template, Priority = next }, out _);
                    nextRefresh = 0f;
                });
                entry.Add(strip);
                templateStrips[template] = strip;
                var obsolete = Hoi4Sprites.Make("dte-obsolete");
                obsolete.tooltip = "Decommissioning templates: not available yet";
                obsolete.pickingMode = PickingMode.Position;
                entry.Add(obsolete);
                var templateName = MakeLabel(template, "center", "dte-name");
                Hoi4TextFit.Fit(templateName);
                entry.Add(templateName);

                var train = Hoi4Sprites.Make("dte-train", "hoi4-button");
                train.pickingMode = PickingMode.Position;
                train.tooltip = $"Train a {template} ({divisions.TrainingDays(tag, template)} days), deploying at the capital";
                train.Add(MakeLabel("Train", "center", "dte-button-text"));
                train.RegisterCallback<ClickEvent>(evt => Train(template));
                entry.Add(train);
                var edit = Hoi4Sprites.Make("dte-edit", "disabled");
                edit.pickingMode = PickingMode.Position;
                edit.tooltip = "Division designer: not available yet";
                edit.Add(MakeLabel("Edit", "center", "dte-button-text"));
                entry.Add(edit);
                var delete = Hoi4Sprites.Make("dte-delete", "disabled");
                delete.pickingMode = PickingMode.Position;
                delete.tooltip = inUse.Contains(template) ? "Divisions use this template" : "Deleting templates: not available yet";
                if (!inUse.Contains(template)) delete.RemoveFromClassList("disabled"); // HOI4: red while nothing uses it
                entry.Add(delete);
                templateList.Add(entry);
            }
        }

        /// <summary>Special forces battalions in use of the most allowed: max(24, 5% of the other battalions)
        /// (SPECIAL_FORCES_CAP_MIN, SPECIAL_FORCES_CAP_BASE). No 1936 template has special forces yet.</summary>
        void RefreshTemplates()
        {
            var divisions = Hoi4Divisions.Instance;
            if (divisions == null) return;
            string tag = Hoi4Deployment.Instance.Tag(shownCountry);
            int battalions = 0;
            FP fieldEquipment = FP.Zero;
            int fieldCount = 0;
            foreach (var d in divisions.All.Values)
            {
                if (d.Tag != tag) continue;
                battalions += d.Regiments;
                fieldEquipment += d.EquipmentLevel;
                fieldCount++;
            }
            int cap = Mathf.Max(24, battalions * 5 / 100);
            root.Q<Label>("dt-special").text = $"0/{cap}";
            if (reinforcementBar != null)
                SetBar(reinforcementBar, fieldCount > 0 ? (fieldEquipment / fieldCount).ToFloat() : 1f, PriorityBarWidth);
            foreach (var kv in templateStrips) SetPriorityStrip(kv.Value, divisions.TemplatePriority(tag, kv.Key), true);
            int reinforce = Hoi4Deployment.Instance.ReinforcementPriority(shownCountry);
            for (int i = 0; i < reinforcementDots.Count; i++) reinforcementDots[i].EnableInClassList("on", i == reinforce);
        }

        static readonly string[] PriorityNames = { "Reserve", "Normal", "Elite" };

        /// <summary>The template's reinforcement priority chevrons (reserve down, normal none, elite up).</summary>
        static void SetPriorityStrip(VisualElement strip, int priority, bool clickable)
        {
            strip.EnableInClassList("reserve", priority == Hoi4Divisions.Reserve);
            strip.EnableInClassList("elite", priority == Hoi4Divisions.Elite);
            string text = $"{PriorityNames[Mathf.Clamp(priority, 0, 2)]} template: " + (priority == Hoi4Divisions.Elite
                ? "reinforced before the others" : priority == Hoi4Divisions.Reserve ? "reinforced after the others" : "reinforced normally");
            strip.tooltip = clickable ? text + " (click to change)" : text;
        }

        // ---- frame ---------------------------------------------------------------------------------------

        void Update()
        {
            if (root == null) return;
            var deployment = Hoi4Deployment.Instance;
            if (Player == 0 || deployment == null || !deployment.IsReady || Hoi4.War.Hoi4War.Instance?.Conference != null)
            {
                if (open) Close();
                return;
            }
            if (Input.GetKeyDown(KeyCode.U)) Toggle();
            if (open && Input.GetKeyDown(KeyCode.Escape))
            {
                if (pickingOrder != 0) SetPicking(0); else Close();
            }
            Hoi4UiHover.Report(this, open ? window : null);
            Hoi4UiHover.Report(templatesHover, open ? templatesWindow : null);
            if (!open) return;

            if (shownCountry != Player)
            {
                shownCountry = Player;
                dirty = true;
                shownTemplates = null;
                collapsed.Clear();
            }
            var orders = deployment.Orders(shownCountry);
            if (pickingOrder != 0 && deployment.GetOrder(shownCountry, pickingOrder) == null) SetPicking(0);
            string structure = Structure(orders);
            if (dirty || structure != shownStructure)
            {
                dirty = false;
                shownStructure = structure;
                Rebuild(orders);
                nextRefresh = 0f;
            }
            if (shownTemplates != deployment.Tag(shownCountry)) RebuildTemplates();
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            RefreshOrders(orders);
            RefreshTemplates();
        }
    }
}
