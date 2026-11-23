using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Core;
using Hoi4.Deployment;
using Hoi4.Units;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's division designer (interface/divisiondesignerview.gui), centred over the map, opened by a template's
    /// Edit button or the Division Designer button of the recruit screen's templates window. View of the UI Presenter
    /// pattern: structure is Resources/Hoi4UI/DivisionDesigner.uxml, look is DivisionDesignerStyle.uss + Hoi4Theme.uss.
    /// Prototype: Tools/prototypes/designer/render_designer.py.
    /// - It edits a working copy (Hoi4TemplateDesign) of one template, or of a new one; nothing changes until Save
    ///   (SaveTemplateCommand), which updates the template and every division of it, in the field and in training.
    /// - Brigade grid: 5 columns of up to 4 battalions (the fifth row needs a doctrine), filled in order as HOI4 does:
    ///   one open slot (+) under a column's last battalion and in the next column, locks after them. A left click on
    ///   an open or filled slot opens the picker of the sub-units the country may use there (a brigade keeps one group:
    ///   infantry, mobile, armor...); a right click on a battalion removes it. The support column works alike (one of
    ///   each divisional company: not divisional = no). Regimental support: one company under each brigade of 3
    ///   battalions, of the types whose allowed_battalion_groups take the brigade's group, at half a company's cost.
    /// - The composition summary and the three stat sheets follow every change (Hoi4Divisions.Stats).
    /// - Name (typed), reinforcement priority, Reset (back to the saved layout), Duplicate (a saved copy, then edits
    ///   it, unsaved: closing drops it), Save. The experience cost of the changes is shown
    ///   (BASE_DIVISION_BRIGADE_CHANGE_COST 5 a battalion, BASE_DIVISION_SUPPORT_SLOT_COST 10 a company,
    ///   BASE_DIVISION_BRIGADE_GROUP_COST 20 a brigade of a new group) but not charged: there is no army experience yet.
    /// </summary>
    public sealed class Hoi4DivisionDesigner : MonoBehaviour
    {
        public static Hoi4DivisionDesigner Instance { get; private set; }
        /// <summary>The designer is up, or closed this frame (its Escape shouldn't also close the screen under it).</summary>
        public static bool IsOpen => Instance != null && (Instance.open || Instance.closedFrame == Time.frameCount);
        int closedFrame = -1;

        const string Gfx = "gfx/interface/designer/";
        const int ChangeCost = 5, SupportCost = 10, GroupCost = 20;
        const int RegimentalCost = SupportCost / 2; // REGIMENTAL_SUPPORT_SLOT_COST_MULTIPLIER 0.5

        UIDocument document;
        VisualElement root, slots, sheets, picker;
        ScrollView pickerList;
        TextField nameField;
        GameState gameState;
        bool open;

        ushort country;
        string tag;
        string original;                    // template being edited, null for a new one
        Hoi4TemplateDesign saved, design;   // the layout as saved, and the working copy

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4DivisionDesigner");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4DivisionDesigner>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Hoi4UiHover.Remove(this);
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/DivisionDesigner");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/DivisionDesignerStyle");
            if (tree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4DivisionDesigner: Resources/Hoi4UI/DivisionDesigner.uxml / DivisionDesignerStyle.uss missing, designer disabled", "game_ui");
                yield break;
            }
            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null) yield break;
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 80; // over the recruit screen and the other HOI4 panels, under confirmations
            yield return null;

            var docRoot = document.rootVisualElement;
            docRoot.pickingMode = PickingMode.Ignore;
            docRoot.styleSheets.Add(theme);
            docRoot.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(docRoot);
            root = tree.Instantiate().Q("designer");
            Hoi4Sprites.Bind(root);
            Hoi4Sprites.Hook(root);
            docRoot.Add(root);
            slots = root.Q("ddg-slots");
            sheets = root.Q("ddg-sheets");
            picker = root.Q("ddg-picker");
            pickerList = root.Q<ScrollView>("ddg-picker-list");
            nameField = root.Q<TextField>("ddg-name");
            nameField.RegisterValueChangedCallback(evt => { if (design != null) { design.Name = evt.newValue; Refresh(); } });
            // a click anywhere else in the designer ends typing (hotkeys come back: Utils.KeyboardFocus)
            root.RegisterCallback<PointerDownEvent>(evt =>
            {
                for (var e = evt.target as VisualElement; e != null; e = e.parent) if (e == nameField) return;
                nameField.Blur();
            }, TrickleDown.TrickleDown);

            root.Q("ddg-close").RegisterCallback<ClickEvent>(evt => Close());
            root.Q("ddg-picker-cancel").RegisterCallback<ClickEvent>(evt => PickerBack());
            for (int p = 0; p < 3; p++)
            {
                int priority = p;
                root.Q($"ddg-prio-{p}").RegisterCallback<ClickEvent>(evt => { design.Priority = priority; Refresh(); });
            }
            root.Q("ddg-reset").RegisterCallback<ClickEvent>(evt => { design = saved.Clone(); ClosePicker(); Refresh(); });
            root.Q("ddg-duplicate").RegisterCallback<ClickEvent>(evt => Duplicate());
            root.Q("ddg-save").RegisterCallback<ClickEvent>(evt => Save());
        }

        static ushort Player
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        // ---- opening -----------------------------------------------------------------------------------------

        /// <summary>Edit a template of the player's (null: a new, empty template).</summary>
        public void Open(string template)
        {
            var divisions = Hoi4Divisions.Instance;
            if (root == null || divisions == null || Player == 0) return;
            country = Player;
            tag = gameState.Countries.GetCountryTag(country);
            original = template != null && divisions.HasTemplate(tag, template) ? template : null;
            saved = original != null ? divisions.Design(tag, original) : new Hoi4TemplateDesign { Name = UniqueName("Division Template") };
            design = saved.Clone();
            open = true;
            root.style.display = DisplayStyle.Flex;
            ClosePicker();
            Refresh();
        }

        public void Close()
        {
            if (open) closedFrame = Time.frameCount;
            open = false;
            nameField?.Blur();
            if (root != null) root.style.display = DisplayStyle.None;
        }

        string UniqueName(string name)
        {
            var divisions = Hoi4Divisions.Instance;
            if (!divisions.HasTemplate(tag, name)) return name;
            for (int n = 2; ; n++)
                if (!divisions.HasTemplate(tag, $"{name} {n}")) return $"{name} {n}";
        }

        // ---- saving --------------------------------------------------------------------------------------------

        void Save()
        {
            var divisions = Hoi4Divisions.Instance;
            design.Name = (design.Name ?? "").Trim();
            if (!divisions.CanSave(tag, original, design, out _)) { Refresh(); return; }
            var command = new SaveTemplateCommand { CountryId = country, Original = original ?? "", Design = design.Clone() };
            if (!gameState.TryExecuteCommand(command, out _)) { Refresh(); return; }
            original = design.Name;
            saved = divisions.Design(tag, original) ?? design.Clone();
            design = saved.Clone();
            Refresh();
        }

        /// <summary>Duplicate: the designer goes on with an unsaved copy (a new template under a free name); nothing
        /// exists until Save, so closing drops it. The copy is its own baseline: Reset goes back to it and only changes
        /// made to it cost experience.</summary>
        void Duplicate()
        {
            var copy = design.Clone();
            copy.Name = UniqueName(string.IsNullOrWhiteSpace(design.Name) ? "Division Template" : design.Name.Trim());
            original = null;
            saved = copy;
            design = copy.Clone();
            ClosePicker();
            Refresh();
        }

        /// <summary>Army experience the changes would cost (HOI4 defines): 5 per battalion slot changed, 10 per support
        /// slot changed, 20 per brigade whose group is new to that column.</summary>
        int ExperienceCost()
        {
            var divisions = Hoi4Divisions.Instance;
            int cost = 0;
            for (int x = 0; x < Hoi4TemplateDesign.Columns; x++)
            {
                for (int y = 0; y < Hoi4TemplateDesign.Rows; y++)
                    if (design.RegimentAt(x, y) != saved.RegimentAt(x, y)) cost += ChangeCost;
                string now = GroupOf(design, x), was = GroupOf(saved, x);
                if (now != null && now != was && !(original == null && x == 0)) cost += GroupCost; // a new template's first brigade is free
            }
            for (int y = 0; y < Hoi4TemplateDesign.SupportRows; y++)
                if (design.SupportAt(y) != saved.SupportAt(y)) cost += SupportCost;
            for (int x = 0; x < Hoi4TemplateDesign.Columns; x++)
                if (design.RegimentalAt(x) != saved.RegimentalAt(x)) cost += RegimentalCost;
            return cost;

            string GroupOf(Hoi4TemplateDesign d, int x)
            {
                foreach (var s in d.Regiments) if (s.X == x) return divisions.SubUnitGroup(s.Type);
                return null;
            }
        }

        // ---- slots -----------------------------------------------------------------------------------------------

        // Blocked: the doctrine-locked fifth row (GFX_blocked_icon, lock with helmet and chess piece)
        enum SlotKind { None, Unit, Open, Locked, Blocked }

        SlotKind RegimentKind(int x, int y)
        {
            if (design.RegimentAt(x, y) != null) return SlotKind.Unit;
            int count = design.ColumnCount(x);
            // a used brigade: its battalions, one "+" and one lock under it, nothing further (as HOI4); the lock in
            // the fifth row is the doctrine one
            if (count > 0)
            {
                if (y == count && y < Hoi4TemplateDesign.OpenRows) return SlotKind.Open;
                int next = count < Hoi4TemplateDesign.OpenRows ? count + 1 : count;
                if (y != next) return SlotKind.None;
                return y >= Hoi4TemplateDesign.OpenRows ? SlotKind.Blocked : SlotKind.Locked;
            }
            int firstFree = 0;
            foreach (var s in design.Regiments) firstFree = Mathf.Max(firstFree, s.X + 1);
            if (x == firstFree) return y == 0 ? SlotKind.Open : y == 1 ? SlotKind.Locked : SlotKind.None;
            return y == 0 ? SlotKind.Locked : SlotKind.None;
        }

        SlotKind SupportKind(int y)
        {
            if (design.SupportAt(y) != null) return SlotKind.Unit;
            return y == design.Support.Count ? SlotKind.Open : y == design.Support.Count + 1 ? SlotKind.Locked : SlotKind.None;
        }

        void BuildSlots()
        {
            slots.Clear();
            var divisions = Hoi4Divisions.Instance;
            for (int x = 0; x < Hoi4TemplateDesign.Columns; x++)
                for (int y = 0; y < Hoi4TemplateDesign.Rows; y++)
                {
                    var kind = RegimentKind(x, y);
                    if (kind == SlotKind.None) continue;
                    // regiments_grid (50, 109) in the subunits container (y 170), slots 102 x 46; designer_division_
                    // regiment_slot_entry: background at (-12, 11) so its stub meets the brigade line, icon at (1, 10)
                    var slot = MakeSlot("ddg-slot", kind, design.RegimentAt(x, y), 50 + x * 102 - 12, 170 + 109 + y * 46 + 11);
                    int cx = x, cy = y;
                    if (kind == SlotKind.Unit || kind == SlotKind.Open)
                        slot.RegisterCallback<PointerDownEvent>(evt =>
                        {
                            if (evt.button == 1 && design.RegimentAt(cx, cy) != null) { design.SetRegiment(cx, cy, null); ClosePicker(); Refresh(); }
                            else if (evt.button == 0) OpenPicker(cx, cy, false);
                        });
                    slot.tooltip = kind == SlotKind.Unit ? $"{divisions.SubUnitName(design.RegimentAt(x, y))}\nClick to replace, right click to remove"
                        : kind == SlotKind.Open ? "Click to add a battalion"
                        : y >= Hoi4TemplateDesign.OpenRows ? "A land doctrine unlocks the fifth row" : "Fill the slots before it first";
                    slots.Add(slot);
                }
            for (int y = 0; y < Hoi4TemplateDesign.SupportRows; y++)
            {
                var kind = SupportKind(y);
                if (kind == SlotKind.None) continue;
                // support_grid (552, 130), slots 82 x 35, background at +21
                var slot = MakeSlot("ddg-support", kind, design.SupportAt(y), 552, 170 + 130 + y * 35 + 21);
                int cy = y;
                if (kind == SlotKind.Unit || kind == SlotKind.Open)
                    slot.RegisterCallback<PointerDownEvent>(evt =>
                    {
                        if (evt.button == 1 && design.SupportAt(cy) != null) { design.SetSupport(cy, null); ClosePicker(); Refresh(); }
                        else if (evt.button == 0) OpenPicker(0, cy, true);
                    });
                slot.tooltip = kind == SlotKind.Unit ? $"{divisions.SubUnitName(design.SupportAt(y))}\nClick to replace, right click to remove"
                    : kind == SlotKind.Open ? "Click to add a support company" : "Fill the slot before it first";
                slots.Add(slot);
            }
            for (int x = 0; x < Hoi4TemplateDesign.Columns; x++)
            {
                // regiment_support_grid (54, 342): under a brigade of 3 battalions (REGIMENTAL_SUPPORT_REQUIRED_BATTALIONS)
                string type = design.RegimentalAt(x);
                bool open = design.ColumnCount(x) >= Hoi4TemplateDesign.RegimentalBattalions;
                var kind = type != null ? SlotKind.Unit : open ? SlotKind.Open : SlotKind.Locked;
                // the support slot entry (background at (0, 21), icon at (2, 20)), as HOI4's narrower regimental row
                var slot = MakeSlot("ddg-support", kind, type, 54 + x * 102, 170 + 342 + 21);
                int cx = x;
                if (kind != SlotKind.Locked)
                    slot.RegisterCallback<PointerDownEvent>(evt =>
                    {
                        if (evt.button == 1 && design.RegimentalAt(cx) != null) { design.SetRegimental(cx, null); ClosePicker(); Refresh(); }
                        else if (evt.button == 0) OpenPicker(cx, 0, false, true);
                    });
                slot.tooltip = kind == SlotKind.Unit ? $"{divisions.SubUnitName(type)}\nClick to replace, right click to remove"
                    : kind == SlotKind.Open ? "Click to add a regimental support company"
                    : $"This Regimental Support slot is locked: the brigade needs {Hoi4TemplateDesign.RegimentalBattalions} battalions";
                slots.Add(slot);
            }
        }

        VisualElement MakeSlot(string cls, SlotKind kind, string type, float left, float top)
        {
            var slot = Hoi4Sprites.Make(cls, kind.ToString().ToLowerInvariant());
            slot.pickingMode = PickingMode.Position;
            slot.style.left = left;
            slot.style.top = top;
            var pic = new VisualElement { pickingMode = PickingMode.Ignore };
            pic.AddToClassList("ddg-slot-pic");
            Hoi4Sprites.Apply(pic, kind == SlotKind.Unit ? Icon(type) : kind == SlotKind.Open ? Gfx + "new_slot.png"
                : kind == SlotKind.Blocked ? Gfx + "blocked.png" : Gfx + "locked.png");
            slot.Add(pic);
            return slot;
        }

        static string Icon(string subUnit)
        {
            string path = Gfx + "icons/" + subUnit + ".png";
            return Hoi4UiAssets.Texture(path) != null ? path : Gfx + "icons/infantry.png";
        }

        // ---- picker ----------------------------------------------------------------------------------------------

        // what the picker is choosing for: a brigade slot or a support row; with categories shown first or not
        int pickX, pickY;
        bool pickSupport, pickRegimental, pickFromCategories, showingCategories;

        // HOI4's category pictures: one battalion of each group stands for it (helmet, truck, tank, gun)
        static readonly Dictionary<string, string> GroupIcons = new Dictionary<string, string>
        {
            ["infantry"] = "infantry", ["mobile"] = "motorized", ["armor"] = "light_armor",
            ["combat_support"] = "artillery_brigade", ["mobile_combat_support"] = "mot_artillery_brigade",
            ["armor_combat_support"] = "light_sp_artillery_brigade", ["support"] = "artillery",
        };

        /// <summary>The picker (designerregimentswindow) for a slot. A battalion slot in a brigade that has other
        /// battalions lists that group's battalions; an empty brigade first lists the categories (sub-unit groups the
        /// country has battalions of), "New group" with its cost when the template has none of that group yet.
        /// Support slots list the support companies not in the template.</summary>
        void OpenPicker(int x, int y, bool support, bool regimental = false)
        {
            pickX = x; pickY = y; pickSupport = support; pickRegimental = regimental;
            var divisions = Hoi4Divisions.Instance;
            string group = support || regimental ? "support" : null;
            if (!support && !regimental)
                foreach (var s in design.Regiments)
                    if (s.X == x && s.Y != y) { group = divisions.SubUnitGroup(s.Type); break; }
            pickFromCategories = group == null;
            if (group == null) ShowCategories(); else ShowBattalions(group);
            picker.style.display = DisplayStyle.Flex;
        }

        void ShowCategories()
        {
            var divisions = Hoi4Divisions.Instance;
            showingCategories = true;
            pickerList.Clear();
            pickerList.scrollOffset = Vector2.zero;
            AddRemoveEntry();
            var groups = divisions.SubUnitsOf(tag).Select(divisions.SubUnitGroup).Where(g => g != "support")
                .Distinct().OrderBy(GroupOrder).ToList();
            foreach (string g in groups)
            {
                string group = g;
                bool present = FirstBrigade() || design.Regiments.Any(s => s.X != pickX && divisions.SubUnitGroup(s.Type) == g);
                var entry = PickerEntry(Icon(GroupIcons.TryGetValue(g, out var icon) ? icon : "infantry"), divisions.GroupName(g),
                    present ? null : $"<color={(CanAfford(GroupCost + ChangeCost) ? "#5BD35B" : "#E0453A")}>New group</color> <color=#F7D45A>{GroupCost + ChangeCost}</color>", !present);
                entry.tooltip = present ? "" : $"A brigade of a group the template doesn't have yet costs {GroupCost} army experience more";
                entry.RegisterCallback<ClickEvent>(evt => ShowBattalions(group));
                pickerList.Add(entry);
            }
            if (groups.Count == 0) pickerList.Add(MakeLabel("Your country has no battalions to add.", "ddg-entry-name"));
        }

        void ShowBattalions(string group)
        {
            var divisions = Hoi4Divisions.Instance;
            showingCategories = false;
            string current = Current();
            var taken = new HashSet<string>();
            if (pickSupport) foreach (var s in design.Support) if (s.Y != pickY) taken.Add(s.Type);
            // the support column takes divisional companies; a regimental slot those that allow its brigade's group
            string brigade = null;
            foreach (var s in design.Regiments) if (s.X == pickX) { brigade = divisions.SubUnitGroup(s.Type); break; }
            var options = divisions.SubUnitsOf(tag)
                .Where(n => pickRegimental ? divisions.CanSupportBrigade(n, brigade)
                          : pickSupport ? divisions.IsDivisionalSupport(n) && !taken.Contains(n)
                          : divisions.SubUnitGroup(n) == group)
                .OrderBy(n => divisions.SubUnitName(n)).ToList();
            pickerList.Clear();
            pickerList.scrollOffset = Vector2.zero;
            if (!pickFromCategories) AddRemoveEntry();
            // a battalion of a group the template doesn't have yet also pays for the new group (as the category says)
            bool newGroup = !pickSupport && !pickRegimental && !FirstBrigade() && !design.Regiments.Any(s => s.X != pickX && divisions.SubUnitGroup(s.Type) == group);
            int cost = pickRegimental ? RegimentalCost : pickSupport ? SupportCost : ChangeCost + (newGroup ? GroupCost : 0);
            foreach (string n in options)
            {
                string name = n;
                // HOI4 DESIGNER_ADD_DESC / DESIGNER_REPLACE_DESC: "Cost N (xp) to add", green when affordable, red when not;
                // the number is always yellow (|H)
                string colour = CanAfford(cost) ? "#5BD35B" : "#E0453A";
                var entry = n == current
                    ? PickerEntry(Icon(n), divisions.SubUnitName(n), "No cost to replace", false)
                    : PickerEntry(Icon(n), divisions.SubUnitName(n), $"<color={colour}>Cost</color> <color=#F7D45A>{cost}</color>", true,
                                  $"<color={colour}>{(current == null ? "to add" : "to replace")}</color>");
                entry.tooltip = EquipmentText(n);
                if (n == current) entry.style.unityBackgroundImageTintColor = new Color(1f, 0.9f, 0.55f);
                entry.RegisterCallback<ClickEvent>(evt => Pick(name));
                pickerList.Add(entry);
            }
            if (options.Count == 0) pickerList.Add(MakeLabel("Nothing your country can use fits here.", "ddg-entry-name"));
        }

        /// <summary>The player's army experience, null while the game doesn't model it (then every change counts as
        /// affordable). The picker's cost lines are green when affordable, red when not.</summary>
        static float? ArmyExperience(ushort country) => null;

        bool CanAfford(int cost)
        {
            float? have = ArmyExperience(country);
            return !have.HasValue || have.Value >= ExperienceCost() + cost;
        }

        /// <summary>A new template's first brigade: its group costs nothing extra (only later new groups do).</summary>
        bool FirstBrigade() => original == null && !design.Regiments.Any(s => s.X != pickX);

        void AddRemoveEntry()
        {
            if (Current() == null) return;
            var remove = PickerEntry(Gfx + "remove.png", "Remove", "No cost to remove", false);
            remove.RegisterCallback<ClickEvent>(evt => Pick(null));
            pickerList.Add(remove);
        }

        /// <summary>Back: from a group's battalions to the categories when they came first, else out of the picker.</summary>
        void PickerBack()
        {
            if (pickFromCategories && !showingCategories) ShowCategories(); else ClosePicker();
        }

        // HOI4's order in the category list
        static int GroupOrder(string group) => group switch
        {
            "combat_support" => 0, "mobile_combat_support" => 1, "mobile" => 2, "armor" => 3,
            "armor_combat_support" => 4, "infantry" => 5, _ => 6,
        };

        string EquipmentText(string subUnit)
        {
            var s = Hoi4Divisions.Instance.SubUnit(subUnit);
            if (s == null) return "";
            var production = Hoi4.Production.Hoi4Production.Instance;
            var parts = new List<string> { $"Manpower: {(int?)s["manpower"] ?? 0}" };
            if (s["need"] is Newtonsoft.Json.Linq.JObject need)
                foreach (var a in need.Properties())
                {
                    int index = System.Array.IndexOf(Hoi4Divisions.Archetypes, a.Name);
                    string label = production != null && index >= 0 ? production.ArchetypeName(index) : a.Name;
                    parts.Add($"{label}: {(int)a.Value}");
                }
            return string.Join("\n", parts);
        }

        /// <summary>designer_subunit_entry / designer_subunit_category_entry: icon, name, and the cost line (rich
        /// text, the army experience star after it when it costs experience).</summary>
        VisualElement PickerEntry(string icon, string name, string cost, bool experience, string after = null)
        {
            var entry = Hoi4Sprites.Make("ddg-entry");
            entry.pickingMode = PickingMode.Position;
            var pic = new VisualElement { pickingMode = PickingMode.Ignore };
            pic.AddToClassList("ddg-entry-icon");
            Hoi4Sprites.Apply(pic, icon);
            entry.Add(pic);
            // the name on up to two lines 18 px apart, as HOI4 (a wrapping label's own line height is too tall
            // and runs into the cost line): split once the label knows its font
            var line1 = MakeLabel(name, "ddg-entry-name");
            var line2 = MakeLabel("", "ddg-entry-name", "ddg-entry-name-2");
            line1.RegisterCallback<GeometryChangedEvent>(evt => SplitName(line1, line2, name));
            entry.Add(line1);
            entry.Add(line2);
            if (!string.IsNullOrEmpty(cost))
            {
                var row = new VisualElement { pickingMode = PickingMode.Ignore };
                row.AddToClassList("ddg-entry-cost-row");
                var text = MakeLabel(cost, "ddg-entry-cost");
                text.enableRichText = true;
                row.Add(text);
                if (experience)
                {
                    var star = new VisualElement { pickingMode = PickingMode.Ignore };
                    star.AddToClassList("ddg-entry-star");
                    Hoi4Sprites.Apply(star, Gfx + "army_xp.png");
                    row.Add(star);
                }
                if (!string.IsNullOrEmpty(after))
                {
                    var tail = MakeLabel(after, "ddg-entry-cost", "ddg-entry-cost-after");
                    tail.enableRichText = true;
                    row.Add(tail);
                }
                entry.Add(row);
            }
            return entry;
        }

        const float NameWidth = 160f; // designer_subunit_entry name maxWidth

        /// <summary>Put as many words as fit NameWidth on the first line and the rest on the second (shrunk if it still
        /// doesn't fit).</summary>
        static void SplitName(Label line1, Label line2, string name)
        {
            if (line1.userData is string done && done == name) return;
            line1.userData = name;
            float Width(string s) => line1.MeasureTextSize(s, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined).x;
            if (Width(name) <= NameWidth) { line1.text = name; line2.text = ""; return; }
            string[] words = name.Split(' ');
            int take = 1;
            while (take < words.Length && Width(string.Join(" ", words, 0, take + 1)) <= NameWidth) take++;
            line1.text = string.Join(" ", words, 0, take);
            line2.text = string.Join(" ", words, take, words.Length - take);
            Hoi4TextFit.Fit(line2);
        }

        string Current() => pickRegimental ? design.RegimentalAt(pickX) : pickSupport ? design.SupportAt(pickY) : design.RegimentAt(pickX, pickY);

        void Pick(string type)
        {
            if (pickRegimental) design.SetRegimental(pickX, type);
            else if (pickSupport) design.SetSupport(pickY, type);
            else design.SetRegiment(pickX, pickY, type);
            ClosePicker();
            Refresh();
        }

        void ClosePicker()
        {
            if (picker != null) picker.style.display = DisplayStyle.None;
        }

        static Label MakeLabel(string text, params string[] classes)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("hoi4-label");
            foreach (string c in classes) l.AddToClassList(c);
            return l;
        }

        // ---- refresh -------------------------------------------------------------------------------------------

        /// <summary>One decimal, cut rather than rounded, as HOI4's designer shows them (piercing 4.46 shows 4.4).</summary>
        static string D1(float v) => (Mathf.Floor(v * 10f + 1e-3f) / 10f).ToString("0.0");
        static string D2(float v) => (Mathf.Floor(v * 100f + 1e-3f) / 100f).ToString("0.00");

        void Refresh()
        {
            if (design == null) return;
            var divisions = Hoi4Divisions.Instance;
            if (nameField.value != design.Name) nameField.SetValueWithoutNotify(design.Name);
            var st = divisions.Stats(design.RegimentTypes(), design.SupportTypes(), design.RegimentalTypes());
            string main = design.Regiments.Count > 0
                ? design.RegimentTypes().OrderByDescending(r => (int?)divisions.SubUnit(r)?["priority"] ?? 0).First() : null;
            Hoi4Sprites.Apply(root.Q("ddg-icon"), main != null ? Icon(main) : null);
            string format = original != null ? divisions.NameFormat(tag, original) : null;
            root.Q<Label>("ddg-names").text = string.IsNullOrEmpty(format) ? "No name list" : format.Replace("%dth", "1st").Replace("%d", "1").Replace("%s", "1");
            for (int p = 0; p < 3; p++) root.Q($"ddg-prio-{p}").EnableInClassList("on", design.Priority == p);
            var chevrons = root.Q("ddg-icon-priority");
            chevrons.style.display = design.Priority == Hoi4Divisions.Normal ? DisplayStyle.None : DisplayStyle.Flex;
            if (design.Priority != Hoi4Divisions.Normal) Hoi4Sprites.Apply(chevrons, Gfx + $"priority_icon_{design.Priority}.png");

            root.Q<Label>("ddg-hardness").text = $"{D1(st.Hardness.ToFloat() * 100f)}%";
            root.Q<Label>("ddg-org").text = D1(st.MaxOrganisation.ToFloat());
            root.Q<Label>("ddg-hp").text = D1(st.MaxStrength.ToFloat());
            root.Q<Label>("ddg-speed").text = $"{D1(st.SpeedKmh.ToFloat())} km/h";
            root.Q<Label>("ddg-width").text = st.CombatWidth.ToFloat().ToString("0.#");
            BuildSlots();
            BuildSheets(st);

            int xp = ExperienceCost();
            root.Q<Label>("ddg-xp").text = xp.ToString();
            bool changed = !Same(design, saved) || original == null;
            bool canSave = divisions.CanSave(tag, original, design, out string why);
            root.Q<Label>("ddg-status").text = canSave || !changed ? "" : why;
            root.Q("ddg-save").EnableInClassList("disabled", !canSave || !changed);
            root.Q("ddg-save").tooltip = !changed ? "No changes" : canSave ? "Save the template: its divisions take the new layout" : why;
            root.Q<Label>("ddg-save-text").text = xp > 0 ? $"Save  ({xp} experience)" : "Save";
            root.Q("ddg-reset").EnableInClassList("disabled", !changed || original == null && design.Regiments.Count == 0);
            root.Q("ddg-duplicate").EnableInClassList("disabled", design.Regiments.Count == 0);
        }

        static bool Same(Hoi4TemplateDesign a, Hoi4TemplateDesign b)
        {
            if (a.Name != b.Name || a.Priority != b.Priority || a.Regiments.Count != b.Regiments.Count || a.Support.Count != b.Support.Count) return false;
            foreach (var s in a.Regiments) if (b.RegimentAt(s.X, s.Y) != s.Type) return false;
            foreach (var s in a.Support) if (b.SupportAt(s.Y) != s.Type) return false;
            if (a.Regimental.Count != b.Regimental.Count) return false;
            foreach (var s in a.Regimental) if (b.RegimentalAt(s.X) != s.Type) return false;
            return true;
        }

        void BuildSheets(Hoi4TemplateStats st)
        {
            sheets.Clear();
            Sheet(674, new[]
            {
                ("Max Speed:", $"{D1(st.SpeedKmh.ToFloat())} km/h"), ("HP:", D1(st.MaxStrength.ToFloat())),
                ("Organization:", D1(st.MaxOrganisation.ToFloat())), ("Recovery Rate:", D2(st.Recovery.ToFloat())),
                ("Reconnaissance:", D1(st.Recon.ToFloat())), ("Suppression:", D1(st.Suppression.ToFloat())),
                ("Weight:", D1(st.Weight.ToFloat())), ("Supply Use:", D2(st.SupplyUse.ToFloat())),
                ("Av. Reliability:", $"{D1(st.Reliability.ToFloat() * 100f)}%"),
            });
            Sheet(847, new[]
            {
                ("Soft Attack:", D1(st.SoftAttack.ToFloat())), ("Hard Attack:", D1(st.HardAttack.ToFloat())),
                ("Air Attack:", D1(st.AirAttack.ToFloat())), ("Defense:", D1(st.Defense.ToFloat())),
                ("Breakthrough:", D1(st.Breakthrough.ToFloat())), ("Armor:", D1(st.Armor.ToFloat())),
                ("Piercing:", D1(st.Piercing.ToFloat())), ("Initiative:", $"{D2(st.Initiative.ToFloat() * 100f)}%"),
                ("Entrenchment:", D1(st.Entrenchment.ToFloat())), ("Combat Width:", st.CombatWidth.ToFloat().ToString("0.#")),
            });
            var production = Hoi4.Production.Hoi4Production.Instance;
            var cost = new List<(string, string)> { ("Manpower:", st.Manpower.ToString()), ("Training Time:", st.TrainingDays.ToString()) };
            for (int a = 0; a < st.Need.Length; a++)
            {
                int amount = Mathf.RoundToInt(st.Need[a].ToFloat());
                if (amount > 0) cost.Add((production != null ? production.ArchetypeName(a) : Hoi4Divisions.Archetypes[a], amount.ToString()));
            }
            Sheet(1021, cost.ToArray(), 170);
        }

        void Sheet(float left, (string label, string value)[] rows, float width = 160)
        {
            for (int i = 0; i < rows.Length && i < 12; i++)
            {
                float top = 13 + 55 + 5 + i * 20;
                var label = MakeLabel(rows[i].label, "ddg-row-label");
                label.style.left = left; label.style.top = top; label.style.width = width - 50;
                Hoi4TextFit.Fit(label);
                var value = MakeLabel(rows[i].value, "ddg-row-value");
                value.style.left = left + width - 80; value.style.top = top; value.style.width = 80;
                sheets.Add(label);
                sheets.Add(value);
            }
        }

        void Update()
        {
            if (root == null) return;
            Hoi4UiHover.Report(this, open ? root : null);
            if (!open) return;
            if (Input.GetMouseButtonDown(0) && !global::Map.CameraControllers.BaseCameraController.ScrollCaptured) nameField.Blur(); // a map click
            if (Input.GetKeyDown(KeyCode.Escape) && !Hoi4ConfirmDialog.IsOpen)
            {
                if (global::Utils.KeyboardFocus.Typing) nameField.Blur();       // Escape first leaves the name field
                else if (picker.resolvedStyle.display == DisplayStyle.Flex) PickerBack();
                else Close();
                closedFrame = Time.frameCount;                                   // the screens under it ignore this Escape
            }
            if ((Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) && global::Utils.KeyboardFocus.Typing) nameField.Blur();
            if (Player != country || Hoi4.War.Hoi4War.Instance?.Conference != null) Close();
        }
    }
}
