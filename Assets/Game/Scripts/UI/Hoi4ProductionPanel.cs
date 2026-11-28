using System.Collections;
using System.Collections.Generic;
using Core;
using Hoi4.Production;
using Hoi4.Units;
using UnityEngine;
using UnityEngine.UIElements;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's production screen (interface/countryproductionlineview.gui), left of the map, opened by the top
    /// bar's production button or Y. View of the UI Presenter pattern: structure is Resources/Hoi4UI/ProductionPanel.uxml
    /// (+ ProductionLine.uxml and ProductionPickerEntry.uxml per entry), look is ProductionPanelStyle.uss + Hoi4Theme.uss,
    /// display logic is Hoi4ProductionPresenter. Every change is a production command (Hoi4ProductionCommands).
    /// - Lines: +/- factories (step 1; 5 with Shift or the line's x5 button, 10 with Ctrl or its x10; the button
    ///   also makes each icon of that line's grid stand for 5 or 10 factories), priority arrows, delete. A line
    ///   building outdated equipment has HOI4's outdated background.
    ///   The factory grid glows while the country has free factories. A line can ask for more than are free: the
    ///   rest show grey, placeholders that fill as factories come free (they are not taken from other lines). A click on an empty grid slot assigns factories
    ///   up to it, a click on a factory takes the ones after it off; hovering previews it (pale green / orange icons,
    ///   the output at the efficiency cap in green / red). The arrow by the priority number collapses the line to
    ///   HOI4's compact entry and back (a view setting, per line, not game state).
    /// - Dragging a line by its title bar moves it in the priority queue: it follows the pointer, the others slide
    ///   out of its way (a USS transition), and the drop is a MoveProductionLineCommand.
    /// - Resources at the top: the states' resources less what the lines' factories use (red with a flame when
    ///   negative). A line short of a resource shows it red with the flame; hovering the grid shows the previewed
    ///   need green or red. The top changes only when factories are placed.
    /// - Add line: the add button of a category glows while it has equipment the player has not looked at yet
    ///   (research unlocked it since the picker was last opened).
    /// - Add line: the infantry/artillery or armour button opens the equipment picker beside the window; a pick adds a
    ///   line at the bottom with the free factories (at least 1, at most 15). The picker's resource checkboxes hide
    ///   the equipment that uses an unchecked resource; "Show outdated equipment" also lists the outdated equipment
    ///   the country knows (Hoi4Production.KnownEquipment), and an entry's round button marks it outdated or current.
    /// - Change equipment: a line's picture opens the picker with what it can switch to (the country's current equipment
    ///   of each archetype in its category that differs from the line's) and the share of efficiency it keeps.
    /// - Filters (checked = shown, HOI4's yellow border and tick): infantry/artillery, armour, and reinforcing (the
    ///   divisions lack the equipment) or stockpiling. Air, naval, refit, resources, outdated and upgrading stay on at 0.
    /// Values refresh four times a real second (a presentation throttle, not game time); the list is rebuilt only
    /// when the lines or the filter change.
    /// </summary>
    public sealed class Hoi4ProductionPanel : MonoBehaviour
    {
        const float RefreshSeconds = 0.25f;
        const int NewLineMaxFactories = 15;

        public static Hoi4ProductionPanel Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance.open;

        UIDocument document;
        VisualElement panel, picker;
        ScrollView lineList, pickerList;
        VisualTreeAsset lineTree, pickerTree;
        GameState gameState;
        readonly object pickerHover = new object();
        readonly List<VisualElement> entries = new List<VisualElement>();
        readonly List<int> shownIds = new List<int>();
        readonly HashSet<int> collapsed = new HashSet<int>(); // line ids shown compact (of shownCountry)
        readonly Dictionary<int, int> lineStep = new Dictionary<int, int>(); // line id -> 5 or 10: its x5 / x10 button
        bool showOutdated;
        // equipment the player has seen in the picker (what the country built at the start counts as seen): an add
        // button glows while its category has current equipment not in here
        readonly HashSet<string> seenEquipment = new HashSet<string>();
        ushort seenCountry;
        Hoi4ProductionPresenter.ResourceUse resourceUse;
        // a line dragged by its title bar: its entry, where it started and where a drop would put it (indices into entries)
        const float TitleBarHeight = 28f, DragThreshold = 4f;
        VisualElement dragEntry;
        float dragStartY;
        bool dragMoved;
        int dragFrom, dragTo;
        // the picker's resource checkboxes in the gui's order (indices into Hoi4StateData.ResourceOrder)
        static readonly int[] PickerResources = { 0, 2, 4, 1, 3, 5, 6 };
        readonly bool[] hideResource = new bool[Hoi4.Map.Hoi4StateData.ResourceOrder.Length];
        int hoverLine, hoverSlot = -1; // the factory grid slot under the pointer
        Dictionary<string, int> resources;
        bool open, pickerArmour, hideInf, hideArmour, hideStockpiling, hideReinforcing, structureDirty = true;
        FP[] lack = new FP[0];
        int pickerOpenFor = -1; // -1 closed, 0 infantry/artillery, 1 armour
        int switchLine;         // the picker switches this line's equipment (0: it adds a line)
        float nextRefresh;
        ushort shownCountry;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4ProductionPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4ProductionPanel>();
        }

        void Awake() => Instance = this;

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/ProductionPanel");
            lineTree = Resources.Load<VisualTreeAsset>("Hoi4UI/ProductionLine");
            pickerTree = Resources.Load<VisualTreeAsset>("Hoi4UI/ProductionPickerEntry");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/ProductionPanelStyle");
            if (tree == null || lineTree == null || pickerTree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4ProductionPanel: Resources/Hoi4UI/ProductionPanel.uxml, its entries or stylesheets are missing, panel disabled", "game_ui");
                yield break;
            }

            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4ProductionPanel: no UIDocument panel settings in the scene, panel disabled", "game_ui");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 45; // over the state and unit panels, under the top bar
            yield return null;

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            var ui = tree.Instantiate();
            panel = ui.Q("production-panel");
            picker = ui.Q("production-picker");
            Hoi4Sprites.Bind(panel);
            Hoi4Sprites.Bind(picker);
            root.Add(panel);
            root.Add(picker);
            lineList = panel.Q<ScrollView>("pr-lines");
            pickerList = picker.Q<ScrollView>("pr-picker-list");
            Hoi4Scrollbar.Apply(lineList); // HOI4's scrollbars
            Hoi4Scrollbar.Apply(pickerList);

            panel.Q("pr-close").RegisterCallback<ClickEvent>(_ => Close());
            picker.Q("pr-picker-close").RegisterCallback<ClickEvent>(_ => ClosePicker());
            panel.Q("pr-add-inf").RegisterCallback<ClickEvent>(_ => TogglePicker(0));
            panel.Q("pr-add-armour").RegisterCallback<ClickEvent>(_ => TogglePicker(1));
            panel.Q("pr-filter-inf").RegisterCallback<ClickEvent>(_ => { hideInf = !hideInf; structureDirty = true; });
            panel.Q("pr-filter-armour").RegisterCallback<ClickEvent>(_ => { hideArmour = !hideArmour; structureDirty = true; });
            panel.Q("pr-filter-stockpiling").RegisterCallback<ClickEvent>(_ => { hideStockpiling = !hideStockpiling; structureDirty = true; });
            panel.Q("pr-filter-reinforcing").RegisterCallback<ClickEvent>(_ => { hideReinforcing = !hideReinforcing; structureDirty = true; });

            var outdated = picker.Q("pr-picker-outdated");
            outdated.RegisterCallback<ClickEvent>(_ =>
            {
                showOutdated = !showOutdated;
                outdated.EnableInClassList("on", showOutdated);
                FillPicker();
            });

            // the picker's resources row: icons 66 px apart, each with its checkbox 28 px to the right
            var resourceRow = picker.Q("pr-picker-resources");
            for (int i = 0; i < PickerResources.Length; i++)
            {
                int resource = PickerResources[i];
                var icon = Hoi4Sprites.Make("pr-picker-res-icon");
                icon.style.left = i * 66;
                Hoi4Sprites.Apply(icon, $"gfx/interface/stateview/resources_{resource}.png");
                resourceRow.Add(icon);
                var check = Hoi4Sprites.Make("hoi4-button", "pr-picker-res-check", "on");
                check.pickingMode = PickingMode.Position;
                check.style.left = i * 66 + 28;
                check.RegisterCallback<ClickEvent>(_ =>
                {
                    hideResource[resource] = !hideResource[resource];
                    check.EnableInClassList("on", !hideResource[resource]);
                    FillPicker();
                });
                resourceRow.Add(check);
            }

            Hoi4ProductionPresenter.Preload();
            while (Hoi4Production.Instance == null) yield return null;
            Hoi4Production.Instance.LinesChanged += c =>
            {
                if (c != shownCountry) return;
                structureDirty = true;
                if (pickerOpenFor >= 0) FillPicker(); // research unlocked something, or a line switched
            };
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Hoi4UiHover.Remove(this);
            Hoi4UiHover.Remove(pickerHover);
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

        /// <summary>The top bar's production button.</summary>
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
            if (Hoi4ConstructionPanel.IsOpen) Hoi4ConstructionPanel.Instance.Close(); // they share the left side
            if (Hoi4DeploymentPanel.IsOpen) Hoi4DeploymentPanel.Instance.Close();
            if (Hoi4ResearchPanel.IsOpen) Hoi4ResearchPanel.Instance.Close();
            open = true;
            panel.style.display = DisplayStyle.Flex;
            structureDirty = true;
            nextRefresh = 0f;
        }

        void Close()
        {
            open = false;
            ClosePicker();
            if (panel != null) panel.style.display = DisplayStyle.None;
        }

        void TogglePicker(int category)
        {
            if (pickerOpenFor == category && switchLine == 0) { ClosePicker(); return; }
            switchLine = 0;
            pickerOpenFor = category;
            pickerArmour = category == 1;
            MarkSeen(pickerArmour);
            picker.style.display = DisplayStyle.Flex;
            panel.Q("pr-add-inf").EnableInClassList("open", category == 0);
            panel.Q("pr-add-armour").EnableInClassList("open", category == 1);
            FillPicker();
        }

        /// <summary>A line's picture: the picker lists what the line can switch to (same category), with the share
        /// of its efficiency it keeps.</summary>
        void OpenSwitch(int lineId)
        {
            var line = Hoi4Production.Instance?.Line(shownCountry, lineId);
            if (line == null) return;
            if (pickerOpenFor >= 0 && switchLine == lineId) { ClosePicker(); return; }
            switchLine = lineId;
            pickerOpenFor = Hoi4ProductionPresenter.IsArmour(line.Archetype) ? 1 : 0;
            pickerArmour = pickerOpenFor == 1;
            MarkSeen(pickerArmour);
            picker.style.display = DisplayStyle.Flex;
            panel.Q("pr-add-inf").RemoveFromClassList("open");
            panel.Q("pr-add-armour").RemoveFromClassList("open");
            FillPicker();
        }

        void ClosePicker()
        {
            pickerOpenFor = -1;
            switchLine = 0;
            if (picker == null) return;
            picker.style.display = DisplayStyle.None;
            panel.Q("pr-add-inf").RemoveFromClassList("open");
            panel.Q("pr-add-armour").RemoveFromClassList("open");
        }

        void FillPicker()
        {
            var production = Hoi4Production.Instance;
            pickerList.Clear();
            if (production == null) return;
            foreach (string equipment in production.KnownEquipment(shownCountry))
            {
                int a = production.ArchetypeOf(shownCountry, equipment);
                if (Hoi4ProductionPresenter.IsArmour(a) != pickerArmour) continue;
                bool obsolete = production.IsObsolete(shownCountry, equipment);
                bool current = production.TryGetBuildable(shownCountry, a, out var now) && now.Equipment == equipment;
                // a line switches to what the country builds now; a new line can also build outdated equipment
                if (switchLine != 0 ? !current || !production.CanSwitch(shownCountry, switchLine, a) : obsolete && !showOutdated) continue;
                var b = production.Describe(shownCountry, equipment);
                if (b.Cost <= FP.Zero || UsesHiddenResource(b)) continue;
                var e = pickerTree.Instantiate().Q(className: "pp-entry");
                Hoi4Sprites.Bind(e);
                string general = b.General;
                if (switchLine != 0) // HOI4 shows the efficiency the line keeps
                    general += $"  (keeps {FP.Round(production.SwitchRetention(shownCountry, switchLine, a) * 100).ToInt()}% efficiency)";
                Hoi4ProductionPresenter.ShowPickerEntry(e, a, b, general, obsolete);
                int archetype = a, line = switchLine;
                e.RegisterCallback<ClickEvent>(_ => { if (line != 0) SwitchLine(line, archetype); else AddLine(archetype, equipment); });
                e.Q("pp-obsolete").RegisterCallback<ClickEvent>(evt =>
                {
                    evt.StopPropagation(); // not a pick of the entry
                    gameState.TryExecuteCommand(new SetEquipmentObsoleteCommand { CountryId = shownCountry, Equipment = equipment, Obsolete = !obsolete }, out _);
                });
                pickerList.Add(e);
            }
            if (switchLine != 0 && pickerList.childCount == 0)
            {
                var none = new Label("Nothing newer to switch to") { pickingMode = PickingMode.Ignore };
                none.AddToClassList("hoi4-label");
                none.AddToClassList("pr-picker-empty");
                pickerList.Add(none);
            }
        }

        /// <summary>The player has now seen the category's current equipment (its picker opened).</summary>
        void MarkSeen(bool armour)
        {
            var production = Hoi4Production.Instance;
            if (production == null) return;
            foreach (int a in production.BuildableArchetypes(shownCountry))
                if (Hoi4ProductionPresenter.IsArmour(a) == armour && production.TryGetBuildable(shownCountry, a, out var b))
                    seenEquipment.Add(b.Equipment);
        }

        /// <summary>The category has current equipment the player has not seen in the picker.</summary>
        bool HasUnseen(Hoi4Production production, bool armour)
        {
            foreach (int a in production.BuildableArchetypes(seenCountry))
                if (Hoi4ProductionPresenter.IsArmour(a) == armour && production.TryGetBuildable(seenCountry, a, out var b)
                    && !seenEquipment.Contains(b.Equipment))
                    return true;
            return false;
        }

        /// <summary>The equipment uses a resource whose checkbox in the picker is off.</summary>
        bool UsesHiddenResource(Hoi4Production.Buildable b)
        {
            if (b.Resources == null) return false;
            var order = Hoi4.Map.Hoi4StateData.ResourceOrder;
            for (int i = 0; i < order.Length; i++)
                if (hideResource[i] && b.Resources.TryGetValue(order[i], out int n) && n > 0) return true;
            return false;
        }

        // ---- commands ---------------------------------------------------------------------------------

        void AddLine(int archetype, string equipment)
        {
            var production = Hoi4Production.Instance;
            int factories = Mathf.Clamp(production.Free(shownCountry), 1, NewLineMaxFactories);
            gameState.TryExecuteCommand(new AddProductionLineCommand
                { CountryId = shownCountry, Archetype = archetype, Factories = factories, Equipment = equipment }, out _);
            ClosePicker();
        }

        void SwitchLine(int lineId, int archetype)
        {
            gameState.TryExecuteCommand(new SwitchLineEquipmentCommand { CountryId = shownCountry, LineId = lineId, Archetype = archetype }, out _);
            ClosePicker();
            nextRefresh = 0f;
        }

        /// <summary>The line's own x5 / x10 button: 5, 10, or 1 when neither is on.</summary>
        int LineStep(int lineId) => lineStep.TryGetValue(lineId, out int s) ? s : 1;

        void ToggleStep(int lineId, int to)
        {
            if (LineStep(lineId) == to) lineStep.Remove(lineId); else lineStep[lineId] = to;
            if (hoverLine == lineId) hoverSlot = -1; // the grid's icons changed under the pointer
            nextRefresh = 0f;
        }

        int Step(int lineId)
        {
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) return 10;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return 5;
            return LineStep(lineId);
        }

        void ChangeFactories(int lineId, int sign)
        {
            var line = Hoi4Production.Instance.Line(shownCountry, lineId);
            if (line == null) return;
            int to = Mathf.Clamp(line.Requested + sign * Step(lineId), 0, Hoi4Production.MaxFactoriesPerLine);
            if (to != line.Requested)
                gameState.TryExecuteCommand(new SetLineFactoriesCommand { CountryId = shownCountry, LineId = lineId, Factories = to }, out _);
            nextRefresh = 0f;
        }

        // ---- dragging a line ------------------------------------------------------------------------

        void BeginDrag(VisualElement e, PointerDownEvent evt)
        {
            // the title bar itself: its buttons pick for themselves
            if (evt.button != 0 || evt.target != e || evt.localPosition.y > TitleBarHeight || dragEntry != null) return;
            int at = entries.IndexOf(e);
            if (at < 0 || at >= shownIds.Count) return;
            dragEntry = e;
            dragFrom = dragTo = at;
            dragStartY = evt.position.y;
            dragMoved = false;
            e.CapturePointer(evt.pointerId);
        }

        void Drag(VisualElement e, PointerMoveEvent evt)
        {
            if (dragEntry != e || !e.HasPointerCapture(evt.pointerId)) return;
            float dy = evt.position.y - dragStartY;
            if (!dragMoved && Mathf.Abs(dy) < DragThreshold) return;
            // not past the first or the last line
            dy = Mathf.Clamp(dy, entries[0].layout.yMin - e.layout.yMin, entries[shownIds.Count - 1].layout.yMax - e.layout.yMax);
            if (!dragMoved)
            {
                dragMoved = true;
                e.AddToClassList("dragging");
            }
            e.style.translate = new Translate(0, dy);
            // every line the dragged one's centre has passed makes room: it slides the dragged line's height the other way
            float centre = e.layout.center.y + dy, height = e.layout.height;
            dragTo = dragFrom;
            for (int k = 0; k < shownIds.Count; k++)
            {
                if (k == dragFrom) continue;
                float shift = 0f;
                if (k < dragFrom && centre < entries[k].layout.center.y) { shift = height; dragTo = Mathf.Min(dragTo, k); }
                else if (k > dragFrom && centre > entries[k].layout.center.y) { shift = -height; dragTo = Mathf.Max(dragTo, k); }
                entries[k].style.translate = new Translate(0, shift);
            }
        }

        /// <summary>The drag ends: with DROP the line takes the place it was dragged to.</summary>
        void EndDrag(VisualElement e, int pointerId, bool drop)
        {
            if (dragEntry != e) return;
            dragEntry = null; // first: releasing the pointer comes back here
            if (e.HasPointerCapture(pointerId)) e.ReleasePointer(pointerId);
            bool move = drop && dragMoved && dragTo != dragFrom && dragFrom < shownIds.Count && dragTo < shownIds.Count;
            int lineId = move ? shownIds[dragFrom] : 0, targetId = move ? shownIds[dragTo] : 0;
            foreach (var entry in entries) // back to the layout's places at once, not sliding
            {
                var settled = entry;
                settled.AddToClassList("settling");
                settled.RemoveFromClassList("dragging");
                settled.style.translate = StyleKeyword.Null;
                settled.schedule.Execute(() => settled.RemoveFromClassList("settling")).StartingIn(50);
            }
            if (!move) return;
            var production = Hoi4Production.Instance;
            var lines = production.Lines(shownCountry);
            int position = -1;
            for (int i = 0; i < lines.Count; i++) if (lines[i].Id == targetId) position = i; // the place of the line it lands on
            if (position < 0) return;
            gameState.TryExecuteCommand(new MoveProductionLineCommand { CountryId = shownCountry, LineId = lineId, Position = position }, out _);
            Rebuild(production); // now, so no frame shows the old order
            Refresh(production);
        }

        // ---- factory grid ----------------------------------------------------------------------------

        /// <summary>The grid slot (0..14, rows of 5) at a position inside the grid: 29 x 23 px slots.</summary>
        static int SlotAt(Vector2 local) =>
            Mathf.Clamp((int)(local.y / 23f), 0, 2) * 5 + Mathf.Clamp((int)(local.x / 29f), 0, 4);

        void Hover(int lineId, int slot)
        {
            if (hoverLine == lineId && hoverSlot == slot) return;
            hoverLine = lineId;
            hoverSlot = slot;
            nextRefresh = 0f; // show the preview now
        }

        /// <summary>A click on a grid slot: the line asks for the factories up to it (Hoi4ProductionPresenter.SlotTarget).</summary>
        void ClickSlot(int lineId, int slot)
        {
            var line = Hoi4Production.Instance.Line(shownCountry, lineId);
            if (line == null) return;
            int to = Hoi4ProductionPresenter.SlotTarget(line, slot, LineStep(lineId));
            if (to != line.Requested)
                gameState.TryExecuteCommand(new SetLineFactoriesCommand { CountryId = shownCountry, LineId = lineId, Factories = to }, out _);
            nextRefresh = 0f;
        }

        void ToggleCollapsed(int lineId)
        {
            if (!collapsed.Remove(lineId)) collapsed.Add(lineId);
            if (hoverLine == lineId) hoverSlot = -1;
            nextRefresh = 0f;
        }

        void Move(int lineId, int delta)
        {
            var lines = Hoi4Production.Instance.Lines(shownCountry);
            int at = -1;
            for (int i = 0; i < lines.Count; i++) if (lines[i].Id == lineId) at = i;
            if (at < 0 || at + delta < 0 || at + delta >= lines.Count) return;
            gameState.TryExecuteCommand(new MoveProductionLineCommand { CountryId = shownCountry, LineId = lineId, Position = at + delta }, out _);
        }

        // ---- update -----------------------------------------------------------------------------------

        void Update()
        {
            if (panel == null) return;
            var production = Hoi4Production.Instance;
            if (Player == 0 || production == null || !production.IsReady || Hoi4.War.Hoi4War.Instance?.Conference != null)
            {
                if (open) Close();
                return;
            }
            if (global::Utils.KeyboardFocus.KeyDown(KeyCode.Y)) Toggle();
            if (open && global::Utils.KeyboardFocus.KeyDown(KeyCode.Escape))
            {
                if (pickerOpenFor >= 0) ClosePicker(); else Close();
            }
            Hoi4UiHover.Report(this, open ? panel : null);
            Hoi4UiHover.Report(pickerHover, pickerOpenFor >= 0 ? picker : null);
            if (seenCountry != Player) // what the country builds when the player takes it is nothing new
            {
                seenCountry = Player;
                seenEquipment.Clear();
                foreach (int a in production.BuildableArchetypes(seenCountry))
                    if (production.TryGetBuildable(seenCountry, a, out var b)) seenEquipment.Add(b.Equipment);
            }
            if (!open) return;

            if (shownCountry != Player)
            {
                shownCountry = Player;
                resources = null;
                collapsed.Clear(); // line ids are per country
                lineStep.Clear();
                hoverSlot = -1;
                structureDirty = true;
                if (pickerOpenFor >= 0) FillPicker();
            }
            if (structureDirty) Rebuild(production);
            // HOI4 blends the glow frames: pulse the free-factory glows (real time, presentation only)
            float pulse = 0.2f + 0.35f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 2.5f));
            panel.Q("pr-add-inf-glow").style.opacity = pulse;
            panel.Q("pr-add-armour-glow").style.opacity = pulse;
            for (int i = 0; i < shownIds.Count; i++) entries[i].Q("pl-glow").style.opacity = pulse;
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh(production);
        }

        void Rebuild(Hoi4Production production)
        {
            structureDirty = false;
            if (dragEntry != null) EndDrag(dragEntry, PointerId.mousePointerId, false); // the lines changed under the drag
            resources = Hoi4ProductionPresenter.Resources(gameState, shownCountry); // states change hands
            lack = Hoi4ProductionPresenter.Lack(gameState, shownCountry);
            shownIds.Clear();
            foreach (var l in production.Lines(shownCountry))
                if (Visible(l)) shownIds.Add(l.Id);
            while (entries.Count < shownIds.Count)
            {
                var e = lineTree.Instantiate().Q(className: "pl-entry");
                Hoi4Sprites.Bind(e);
                e.Q("pl-sub").RegisterCallback<ClickEvent>(_ => ChangeFactories((int)e.userData, -1));
                e.Q("pl-add").RegisterCallback<ClickEvent>(_ => ChangeFactories((int)e.userData, +1));
                e.Q("pl-delete").RegisterCallback<ClickEvent>(evt =>
                    gameState.TryExecuteCommand(new RemoveProductionLineCommand { CountryId = shownCountry, LineId = (int)e.userData }, out _));
                e.Q("pl-up").RegisterCallback<ClickEvent>(_ => Move((int)e.userData, -1));
                e.Q("pl-down").RegisterCallback<ClickEvent>(_ => Move((int)e.userData, +1));
                e.Q("pl-x5").RegisterCallback<ClickEvent>(_ => ToggleStep((int)e.userData, 5));
                e.Q("pl-x10").RegisterCallback<ClickEvent>(_ => ToggleStep((int)e.userData, 10));
                var picture = e.Q("pl-picture");
                picture.pickingMode = PickingMode.Position;
                picture.AddToClassList("hoi4-button");
                picture.RegisterCallback<ClickEvent>(_ => OpenSwitch((int)e.userData));
                e.Q("pl-expand").RegisterCallback<ClickEvent>(_ => ToggleCollapsed((int)e.userData));
                e.RegisterCallback<PointerDownEvent>(evt => BeginDrag(e, evt));
                e.RegisterCallback<PointerMoveEvent>(evt => Drag(e, evt));
                e.RegisterCallback<PointerUpEvent>(evt => EndDrag(e, evt.pointerId, true));
                e.RegisterCallback<PointerCaptureOutEvent>(_ => EndDrag(e, PointerId.mousePointerId, false));
                var grid = e.Q("pl-grid");
                grid.RegisterCallback<PointerMoveEvent>(evt => Hover((int)e.userData, SlotAt(evt.localPosition)));
                grid.RegisterCallback<PointerLeaveEvent>(_ => { if (hoverLine == (int)e.userData) Hover(0, -1); });
                grid.RegisterCallback<ClickEvent>(evt => ClickSlot((int)e.userData, SlotAt(evt.localPosition)));
                entries.Add(e);
            }
            lineList.Clear();
            for (int i = 0; i < shownIds.Count; i++)
            {
                entries[i].userData = shownIds[i];
                lineList.Add(entries[i]);
            }
            panel.Q("pr-filter-inf").EnableInClassList("on", !hideInf);
            panel.Q("pr-filter-armour").EnableInClassList("on", !hideArmour);
            panel.Q("pr-filter-stockpiling").EnableInClassList("on", !hideStockpiling);
            panel.Q("pr-filter-reinforcing").EnableInClassList("on", !hideReinforcing);
            nextRefresh = 0f;
        }

        bool Visible(ProductionLine l) =>
            !(Hoi4ProductionPresenter.IsArmour(l.Archetype) ? hideArmour : hideInf)
            && !(l.Archetype < lack.Length && lack[l.Archetype] > FP.Zero ? hideReinforcing : hideStockpiling);

        void Refresh(Hoi4Production production)
        {
            resources ??= Hoi4ProductionPresenter.Resources(gameState, shownCountry);
            resourceUse = Hoi4ProductionPresenter.Use(production, shownCountry, resources);
            lack = Hoi4ProductionPresenter.Lack(gameState, shownCountry);
            // a line moved between reinforcing and stockpiling while one of them is hidden: rebuild the list
            int visible = 0;
            foreach (var l in production.Lines(shownCountry))
                if (Visible(l) && (visible >= shownIds.Count || shownIds[visible++] != l.Id)) { structureDirty = true; return; }
            if (visible != shownIds.Count) { structureDirty = true; return; }
            Hoi4ProductionPresenter.ShowTop(panel, gameState, production, shownCountry, resourceUse, lack);
            int free = production.Free(shownCountry);
            // new equipment the player has not looked at (not free factories: the lines' grids glow for those)
            panel.Q("pr-add-inf-glow").style.display = HasUnseen(production, false) ? DisplayStyle.Flex : DisplayStyle.None;
            panel.Q("pr-add-armour-glow").style.display = HasUnseen(production, true) ? DisplayStyle.Flex : DisplayStyle.None;

            var equipment = Hoi4Equipment.Instance;
            var lines = production.Lines(shownCountry);
            for (int i = 0; i < shownIds.Count; i++)
            {
                ProductionLine line = null;
                int position = 0;
                for (int k = 0; k < lines.Count; k++) if (lines[k].Id == shownIds[i]) { line = lines[k]; position = k; }
                if (line == null) { structureDirty = true; return; }
                var b = production.Describe(shownCountry, line.Equipment); // the line's own equipment
                FP stock = equipment != null ? equipment.Stock(shownCountry, line.Archetype) : FP.Zero;
                Hoi4ProductionPresenter.ShowLine(entries[i], line, position, b, b.Name ?? line.Equipment, b.General ?? line.Equipment,
                    b.Picture, lack[line.Archetype], stock, free, LineStep(line.Id), collapsed.Contains(line.Id),
                    hoverLine == line.Id ? hoverSlot : -1, production.IsObsolete(shownCountry, line.Equipment),
                    resourceUse.Available.TryGetValue(line.Id, out var available) ? available : null);
                entries[i].Q("pl-down").style.visibility = position < lines.Count - 1 ? Visibility.Visible : Visibility.Hidden;
            }
        }
    }
}
