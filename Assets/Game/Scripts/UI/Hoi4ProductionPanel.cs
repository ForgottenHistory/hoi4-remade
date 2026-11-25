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
    /// - Lines: +/- factories (step 1; 5 with Shift or the x5 button, 10 with Ctrl or x10), priority arrows, delete.
    ///   The factory grid glows while the country has free factories.
    /// - Add line: the infantry/artillery or armour button opens the equipment picker beside the window; a pick adds a
    ///   line at the bottom with the free factories (at least 1, at most 15).
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
        Dictionary<string, int> resources;
        bool open, pickerArmour, hideInf, hideArmour, hideStockpiling, hideReinforcing, structureDirty = true;
        FP[] lack = new FP[0];
        int pickerOpenFor = -1; // -1 closed, 0 infantry/artillery, 1 armour
        int switchLine;         // the picker switches this line's equipment (0: it adds a line)
        int step = 1;
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
            picker.Q<Label>("pr-picker-title").text = switchLine != 0 ? "Change equipment"
                : pickerArmour ? "Armour" : "Infantry and Artillery";
            if (production == null) return;
            foreach (int a in production.BuildableArchetypes(shownCountry))
            {
                if (Hoi4ProductionPresenter.IsArmour(a) != pickerArmour) continue;
                if (switchLine != 0 && !production.CanSwitch(shownCountry, switchLine, a)) continue;
                production.TryGetBuildable(shownCountry, a, out var b);
                var e = pickerTree.Instantiate().Q(className: "pp-entry");
                Hoi4Sprites.Bind(e);
                string general = b.General;
                if (switchLine != 0) // HOI4 shows the efficiency the line keeps
                    general += $"  (keeps {FP.Round(production.SwitchRetention(shownCountry, switchLine, a) * 100).ToInt()}% efficiency)";
                Hoi4ProductionPresenter.ShowPickerEntry(e, b.Name, general, b.Cost, b.Picture);
                int archetype = a, line = switchLine;
                e.RegisterCallback<ClickEvent>(_ => { if (line != 0) SwitchLine(line, archetype); else AddLine(archetype); });
                pickerList.Add(e);
            }
            if (switchLine != 0 && pickerList.childCount == 0)
                pickerList.Add(new Label("Nothing newer to switch to") { pickingMode = PickingMode.Ignore });
        }

        // ---- commands ---------------------------------------------------------------------------------

        void AddLine(int archetype)
        {
            var production = Hoi4Production.Instance;
            int factories = Mathf.Clamp(production.Free(shownCountry), 1, NewLineMaxFactories);
            gameState.TryExecuteCommand(new AddProductionLineCommand { CountryId = shownCountry, Archetype = archetype, Factories = factories }, out _);
            ClosePicker();
        }

        void SwitchLine(int lineId, int archetype)
        {
            gameState.TryExecuteCommand(new SwitchLineEquipmentCommand { CountryId = shownCountry, LineId = lineId, Archetype = archetype }, out _);
            ClosePicker();
            nextRefresh = 0f;
        }

        int Step()
        {
            if (Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl)) return 10;
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) return 5;
            return step;
        }

        void ChangeFactories(int lineId, int sign)
        {
            var line = Hoi4Production.Instance.Line(shownCountry, lineId);
            if (line == null) return;
            int to = Mathf.Clamp(line.Requested + sign * Step(), 0, Hoi4Production.MaxFactoriesPerLine);
            if (to != line.Requested)
                gameState.TryExecuteCommand(new SetLineFactoriesCommand { CountryId = shownCountry, LineId = lineId, Factories = to }, out _);
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
            if (!open) return;

            if (shownCountry != Player)
            {
                shownCountry = Player;
                resources = null;
                structureDirty = true;
                if (pickerOpenFor >= 0) FillPicker();
            }
            if (structureDirty) Rebuild(production);
            // HOI4 blends the glow frames: pulse the free-factory glows (real time, presentation only)
            float pulse = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 2.5f));
            panel.Q("pr-add-inf-glow").style.opacity = pulse;
            for (int i = 0; i < shownIds.Count; i++) entries[i].Q("pl-glow").style.opacity = pulse;
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh(production);
        }

        void Rebuild(Hoi4Production production)
        {
            structureDirty = false;
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
                e.Q("pl-x5").RegisterCallback<ClickEvent>(_ => { step = step == 5 ? 1 : 5; nextRefresh = 0f; });
                e.Q("pl-x10").RegisterCallback<ClickEvent>(_ => { step = step == 10 ? 1 : 10; nextRefresh = 0f; });
                var picture = e.Q("pl-picture");
                picture.pickingMode = PickingMode.Position;
                picture.AddToClassList("hoi4-button");
                picture.RegisterCallback<ClickEvent>(_ => OpenSwitch((int)e.userData));
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
            lack = Hoi4ProductionPresenter.Lack(gameState, shownCountry);
            // a line moved between reinforcing and stockpiling while one of them is hidden: rebuild the list
            int visible = 0;
            foreach (var l in production.Lines(shownCountry))
                if (Visible(l) && (visible >= shownIds.Count || shownIds[visible++] != l.Id)) { structureDirty = true; return; }
            if (visible != shownIds.Count) { structureDirty = true; return; }
            Hoi4ProductionPresenter.ShowTop(panel, gameState, production, shownCountry, resources, lack);
            bool free = production.Free(shownCountry) > 0;
            panel.Q("pr-add-inf-glow").style.display = free ? DisplayStyle.Flex : DisplayStyle.None;

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
                    b.Picture, lack[line.Archetype], stock, free, step);
                entries[i].Q("pl-down").style.visibility = position < lines.Count - 1 ? Visibility.Visible : Visibility.Hidden;
            }
        }
    }
}
