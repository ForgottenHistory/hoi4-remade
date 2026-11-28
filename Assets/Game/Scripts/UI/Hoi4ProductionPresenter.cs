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
    /// GAME: display logic of the production screen (Hoi4ProductionPanel is the View). Fills the window top
    /// (resources, factories, per-category counts) and each line entry from Hoi4Production, the stockpiles and the
    /// country's divisions. Reads only; changes go through the production commands in the View.
    /// </summary>
    public static class Hoi4ProductionPresenter
    {
        const string Dir = "gfx/interface/production/";
        public const int GridSlots = 15;

        /// <summary>HOI4's add-line categories: infantry and artillery (everything but tanks), and armour.</summary>
        public static bool IsArmour(int archetype)
        {
            var names = Hoi4Divisions.Archetypes;
            // tank chassis (light_tank_chassis, light_tank_destroyer_chassis...); anti_tank_equipment is artillery
            return archetype >= 0 && archetype < names.Length && names[archetype].EndsWith("_chassis");
        }

        /// <summary>What the country's divisions lack of each archetype (need - have, summed).</summary>
        public static FP[] Lack(GameState gameState, ushort country)
        {
            var lack = new FP[Hoi4Divisions.Archetypes.Length];
            var divisions = Hoi4Divisions.Instance;
            if (divisions == null) return lack;
            var units = gameState.Units;
            foreach (var d in divisions.All.Values)
            {
                if (d.Need == null || !units.HasUnit(d.UnitId) || units.GetUnit(d.UnitId).countryID != country) continue;
                for (int a = 0; a < lack.Length && a < d.Need.Length; a++)
                    if (d.Have[a] < d.Need[a]) lack[a] += d.Need[a] - d.Have[a];
            }
            return lack;
        }

        public static int ActiveFactories(Hoi4Production production, ushort country)
        {
            int n = 0;
            foreach (var l in production.Lines(country)) n += l.Active;
            return n;
        }

        /// <summary>The country's resource balance for the screen: what its states give (Supply), what its lines' working
        /// factories use (Used), and per line what it could have of each resource (Available: the supply less what the
        /// other lines use). Indexed like Hoi4StateData.ResourceOrder. Display only: lacking a resource costs nothing yet.</summary>
        public sealed class ResourceUse
        {
            public readonly int[] Supply = new int[Hoi4.Map.Hoi4StateData.ResourceOrder.Length];
            public readonly int[] Used = new int[Hoi4.Map.Hoi4StateData.ResourceOrder.Length];
            public readonly Dictionary<int, int[]> Available = new Dictionary<int, int[]>(); // line id -> per resource
        }

        public static ResourceUse Use(Hoi4Production production, ushort country, Dictionary<string, int> supply)
        {
            var use = new ResourceUse();
            var order = Hoi4.Map.Hoi4StateData.ResourceOrder;
            for (int i = 0; i < order.Length; i++) supply.TryGetValue(order[i], out use.Supply[i]);
            var lines = production.Lines(country);
            var own = new int[lines.Count][];
            for (int k = 0; k < lines.Count; k++)
            {
                own[k] = new int[order.Length];
                var res = production.Describe(country, lines[k].Equipment).Resources;
                if (res == null) continue;
                for (int i = 0; i < order.Length; i++)
                    if (res.TryGetValue(order[i], out int n)) { own[k][i] = n * lines[k].Active; use.Used[i] += own[k][i]; }
            }
            for (int k = 0; k < lines.Count; k++)
            {
                var available = new int[order.Length];
                for (int i = 0; i < order.Length; i++) available[i] = use.Supply[i] - (use.Used[i] - own[k][i]);
                use.Available[lines[k].Id] = available;
            }
            return use;
        }

        /// <summary>The window top: the resource balance (the states' resources less what the lines use; red with the
        /// flame icon when the lines use more), factories used/total, factories per category.</summary>
        public static void ShowTop(VisualElement panel, GameState gameState, Hoi4Production production, ushort country,
                                   ResourceUse use, FP[] lack)
        {
            int total = production.Factories(country);
            int used = System.Math.Min(total, production.Requested(country));
            var factories = panel.Q<Label>("pr-factories");
            factories.text = $"{used}/{total}";
            factories.EnableInClassList("free", used < total);
            factories.EnableInClassList("full", total > 0 && used >= total);

            // factories per filter: category, and reinforcing (the divisions lack the equipment) or stockpiling
            int inf = 0, armour = 0, reinforcing = 0, stockpiling = 0;
            foreach (var l in production.Lines(country))
            {
                if (IsArmour(l.Archetype)) armour += l.Active; else inf += l.Active;
                if (l.Archetype < lack.Length && lack[l.Archetype] > FP.Zero) reinforcing += l.Active; else stockpiling += l.Active;
            }
            panel.Q<Label>("pr-filter-inf-count").text = inf.ToString();
            panel.Q<Label>("pr-filter-armour-count").text = armour.ToString();
            panel.Q<Label>("pr-filter-reinforcing-count").text = reinforcing.ToString();
            panel.Q<Label>("pr-filter-stockpiling-count").text = stockpiling.ToString();

            var strip = panel.Q("pr-resources");
            if (strip.childCount == 0)
                for (int i = 0; i < Hoi4.Map.Hoi4StateData.ResourceOrder.Length; i++)
                {
                    var item = new VisualElement { pickingMode = PickingMode.Ignore };
                    item.AddToClassList("pr-resource");
                    item.Add(Hoi4Sprites.Make("pr-resource-bg"));
                    var icon = Hoi4Sprites.Make("pr-resource-icon");
                    icon.name = "icon";
                    item.Add(icon);
                    var value = new Label { name = "value", pickingMode = PickingMode.Ignore };
                    value.AddToClassList("hoi4-label");
                    value.AddToClassList("pr-resource-value");
                    item.Add(value);
                    strip.Add(item);
                }
            for (int i = 0; i < strip.childCount; i++)
            {
                int n = use.Supply[i] - use.Used[i];
                var value = strip[i].Q<Label>("value");
                value.text = n.ToString();
                value.EnableInClassList("none", n == 0);
                value.EnableInClassList("lack", n < 0);
                SetResourceIcon(strip[i].Q("icon"), i, n < 0);
            }
        }

        /// <summary>The resources in the states the country owns (no trade: all of it is the country's).</summary>
        public static Dictionary<string, int> Resources(GameState gameState, ushort country)
        {
            var sum = new Dictionary<string, int>();
            if (!Hoi4.Map.Hoi4StateData.Available || !Hoi4.Map.Hoi4Regions.Available) return sum;
            var provinces = gameState.Provinces;
            foreach (int state in Hoi4.Map.Hoi4Regions.States)
            {
                var info = Hoi4.Map.Hoi4StateData.Get(state);
                if (info == null || info.Resources.Count == 0) continue;
                var provs = Hoi4.Map.Hoi4Regions.ProvincesOf(state);
                if (provs.Count == 0 || provinces.GetProvinceOwner(provs[0]) != country) continue;
                foreach (var kv in info.Resources)
                {
                    sum.TryGetValue(kv.Key, out int n);
                    sum[kv.Key] = n + kv.Value;
                }
            }
            return sum;
        }

        /// <summary>Factories one icon of a line's grid stands for: the line's x5 / x10 button (STEP 5 or 10, else 1),
        /// and more when the line asks for more than the 15 slots hold that way (75 with x5 icons).</summary>
        public static int Group(int requested, int step) =>
            System.Math.Max(step == 10 ? 10 : step == 5 ? 5 : 1, requested > 75 ? 10 : requested > GridSlots ? 5 : 1);

        /// <summary>Icons the line's factories take in its grid.</summary>
        public static int Icons(int requested, int step) => (requested + Group(requested, step) - 1) / Group(requested, step);

        /// <summary>The factories a line would ask for after a click on a grid slot: up to and with that slot, as
        /// HOI4 does (an empty slot assigns, a factory further back takes the ones after it off). It may be more than
        /// the country has free: the rest are placeholders for future factories.</summary>
        public static int SlotTarget(ProductionLine line, int slot, int step)
        {
            int to = (slot + 1) * Group(line.Requested, step);
            return System.Math.Min(slot < Icons(line.Requested, step) ? line.Requested : Hoi4Production.MaxFactoriesPerLine, to);
        }

        /// <summary>HOI4's land equipment role icon (GFX_land_equipment_role_icons frame) of the towed guns, -1 for
        /// the rest (their picture says it).</summary>
        public static int RoleFrame(int archetype)
        {
            var names = Hoi4Divisions.Archetypes;
            switch (archetype >= 0 && archetype < names.Length ? names[archetype] : null)
            {
                case "anti_tank_equipment": return 0;
                case "artillery_equipment": return 1;
                case "anti_air_equipment": return 2;
                case "rocket_artillery_equipment": return 3;
                default: return -1;
            }
        }

        /// <summary>Load the sprites the lines switch to while shown (bar colours, factory frames, flame icons, the
        /// compact entry), so the first switch does not show a frame without them.</summary>
        public static void Preload()
        {
            foreach (string n in new[] { "progress_bar", "progress_bar_yellow", "progress_bar_red", "item_collapsed",
                         "equipment_collapsed", "outdated_overlay", "outdated_overlay_collapsed", "column_button_1",
                         "column_button_7", "btn_x5_1", "btn_x10_1", "sub_5", "sub_10", "add_5", "add_10" })
                Hoi4UiAssets.Texture($"{Dir}{n}.png", true);
            for (int i = 0; i < 15; i++) Hoi4UiAssets.Texture($"{Dir}factory_{i}.png", true);
            for (int i = 0; i < Hoi4.Map.Hoi4StateData.ResourceOrder.Length; i++)
            {
                Hoi4UiAssets.Texture($"{Dir}missing_resource_{i}.png", true);
                Hoi4UiAssets.Texture($"gfx/interface/stateview/resources_{i}.png", true);
            }
        }

        static void ShowRole(VisualElement icon, int archetype)
        {
            int frame = RoleFrame(archetype);
            icon.style.display = frame >= 0 ? DisplayStyle.Flex : DisplayStyle.None;
            if (frame < 0 || (icon.userData is int f && f == frame)) return;
            icon.userData = frame;
            Hoi4Sprites.Apply(icon, $"{Dir}role_icon_{frame}.png");
        }

        /// <summary>A resource's icon, or its flame version (GFX_missing_resources_strip) when the country lacks it.</summary>
        static void SetResourceIcon(VisualElement icon, int resource, bool missing)
        {
            int key = missing ? resource + 100 : resource;
            if (icon.userData is int f && f == key) return;
            icon.userData = key;
            Hoi4Sprites.Apply(icon, missing ? $"{Dir}missing_resource_{resource}.png" : $"gfx/interface/stateview/resources_{resource}.png");
        }

        /// <summary>A resources grid: an icon and amount (per factory x `factories`) for each resource the equipment
        /// uses, in HOI4's resource order, at most `slots`. With `available` (a line: what the country has for it of
        /// each resource): a line short of a resource shows what it gets, red, with the flame icon; while the grid
        /// previews a click (`preview`) the amounts are green when the country has that much and red when not.</summary>
        public static void ShowResources(VisualElement grid, IReadOnlyDictionary<string, int> resources, int factories, int slots,
                                         int[] available = null, bool preview = false)
        {
            while (grid.childCount < slots)
            {
                var item = new VisualElement { pickingMode = PickingMode.Ignore };
                item.AddToClassList("res-item");
                var icon = new VisualElement { name = "icon", pickingMode = PickingMode.Ignore };
                icon.AddToClassList("spr");
                icon.AddToClassList("res-icon");
                item.Add(icon);
                var amount = new Label { name = "amount", pickingMode = PickingMode.Ignore };
                amount.AddToClassList("hoi4-label");
                amount.AddToClassList("center");
                amount.AddToClassList("res-amount");
                item.Add(amount);
                grid.Add(item);
            }
            int shown = 0;
            var order = Hoi4.Map.Hoi4StateData.ResourceOrder;
            if (resources != null)
                for (int i = 0; i < order.Length && shown < slots; i++)
                {
                    if (!resources.TryGetValue(order[i], out int n) || n <= 0) continue;
                    var item = grid[shown++];
                    item.style.display = DisplayStyle.Flex;
                    int need = n * factories;
                    bool lacking = available != null && need > available[i];
                    SetResourceIcon(item.Q("icon"), i, lacking && !preview);
                    item.Q<Label>("amount").text = (lacking && !preview ? System.Math.Max(0, available[i]) : need).ToString();
                    item.EnableInClassList("ok", preview && !lacking);
                    item.EnableInClassList("lack", lacking);
                }
            for (; shown < grid.childCount; shown++) grid[shown].style.display = DisplayStyle.None;
        }

        const float CriticalPenalty = 0.8f; // RESOURCE_PENALTY_WARNING_CRITICAL_RATIO: the bar turns red over it

        /// <summary>HOI4's penalty for lacking resources, averaged over the line's factories (wiki "Production",
        /// PRODUCTION_RESOURCE_LACK_PENALTY): each factory in turn loses 5% per unit it misses of the resource it
        /// misses most, counting the factories before it, up to 100%. For the bar's colour only: the output does not
        /// take the penalty yet.</summary>
        static float LackPenalty(IReadOnlyDictionary<string, int> resources, int factories, int[] available)
        {
            if (resources == null || available == null || factories <= 0) return 0f;
            var order = Hoi4.Map.Hoi4StateData.ResourceOrder;
            float sum = 0f;
            for (int f = 1; f <= factories; f++)
            {
                int missing = 0;
                for (int i = 0; i < order.Length; i++)
                    if (resources.TryGetValue(order[i], out int n))
                        missing = System.Math.Max(missing, n * f - System.Math.Max(0, available[i]));
                sum += Mathf.Min(1f, 0.05f * missing);
            }
            return sum / factories;
        }

        static string Speed(float perDay) => perDay >= 1f ? $"{perDay:0.00}/day" : $"{perDay * 7f:0.00}/week";

        /// <summary>One line entry. `free` is the country's free factories (they light the grid's glow); `available` what the country has for this line of each resource (ResourceUse). `step` is the line's own x5 / x10
        /// button (1, 5 or 10): the +/- step and what one grid icon stands for. `outdated` draws HOI4's outdated
        /// equipment background. `collapsed` is HOI4's compact entry. `hoverSlot` is the grid slot under the pointer (-1: none): the grid then previews the click
        /// (SlotTarget: pale green factories to assign, orange ones to take off) and the output text shows what that
        /// many factories make at the efficiency cap, green or red.</summary>
        public static void ShowLine(VisualElement e, ProductionLine line, int position, Hoi4Production.Buildable info,
                                    string name, string general, string picture, FP lack, FP stock, int free, int step,
                                    bool collapsed, int hoverSlot, bool outdated, int[] available)
        {
            bool freeFactories = free > 0;
            e.EnableInClassList("collapsed", collapsed);
            e.Q("pl-outdated").style.display = outdated ? DisplayStyle.Flex : DisplayStyle.None;
            if (collapsed) hoverSlot = -1;
            e.Q<Label>("pl-priority").text = (position + 1).ToString();
            e.Q<Label>("pl-name").text = name;
            e.Q<Label>("pl-general").text = general;
            // HOI4 shows "11/25" while the line gets fewer factories than it asks for
            e.Q<Label>("pl-factories").text = line.Active < line.Requested ? $"{line.Active}/{line.Requested}" : line.Requested.ToString();

            // efficiency on HOI4's 0-100% scale: the to-max bar (red, yellow tick at its end) up to the cap, the grey
            // fill over it up to the current value, so a line at the 50% cap is half full with the tick in the middle
            // and a line below the cap shows red between its value and the cap
            float cap = Mathf.Max(3f, Mathf.Round(93f * Mathf.Clamp01(line.Rates.Cap.ToFloat())));
            float fill = Mathf.Min(cap - 3f, Mathf.Round(93f * Mathf.Clamp01(line.AverageEfficiency.ToFloat())));
            e.Q("pl-eff-max").style.width = cap;
            e.Q("pl-eff-fill").style.width = Mathf.Max(0f, fill);

            // progress to the next unit and units a day (a week when under one a day)
            float progress = line.Cost > FP.Zero ? Mathf.Clamp01((line.Progress / line.Cost).ToFloat()) : 0f;
            int group = Group(line.Requested, step);
            int icons = Icons(line.Requested, step);
            bool removing = hoverSlot >= 0 && hoverSlot < icons; // the pointer is on one of the line's factories
            int target = hoverSlot >= 0 ? SlotTarget(line, hoverSlot, step) : line.Requested;
            int targetIcons = (target + group - 1) / group;
            var speed = e.Q<Label>("pl-speed");
            float perDay = line.Cost <= FP.Zero ? 0f
                : hoverSlot >= 0 ? (Hoi4Production.FactoryOutput * line.Rates.Cap * line.Rates.Output * target / line.Cost).ToFloat()
                : (Hoi4Production.LineOutput(line) / line.Cost).ToFloat();
            speed.text = Speed(perDay);
            // the bar behind the output: full while the line makes one or more a day, else the progress to the next
            // unit; yellow while the line lacks a resource
            float made = line.Cost > FP.Zero ? (Hoi4Production.LineOutput(line) / line.Cost).ToFloat() : 0f;
            var bar = e.Q("pl-progress");
            bar.style.width = made >= 1f ? 118f : Mathf.Round(118f * progress);
            float penalty = LackPenalty(info.Resources, line.Active, available);
            bar.EnableInClassList("lacking", penalty > 0f && penalty <= CriticalPenalty);
            bar.EnableInClassList("critical", penalty > CriticalPenalty);
            speed.EnableInClassList("add", hoverSlot >= 0 && !removing);
            speed.EnableInClassList("remove", removing);
            // what its working factories use; under the pointer, what the previewed factories would
            ShowResources(e.Q("pl-resources"), info.Resources, hoverSlot >= 0 ? target : line.Active, 3, available, hoverSlot >= 0);
            ShowRole(e.Q("pl-role"), line.Archetype);

            // reinforcing (the divisions lack some: how much) or stockpiling (what is in stock)
            bool reinforcing = lack > FP.Zero;
            e.Q<Label>("pl-need").text = (reinforcing ? lack : stock).ToInt().ToString("N0");
            Hoi4Sprites.Apply(e.Q("pl-need-icon"), Dir + (reinforcing ? "need_strip_2.png" : "need_strip_0.png"));

            var pic = e.Q("pl-picture");
            if (pic.userData as string != picture)
            {
                pic.userData = picture;
                Hoi4Sprites.Apply(pic, picture);
            }

            // factory grid: one icon per factory, a x5 or x10 icon each when there are more than the 15 slots hold;
            // green while working, dark grey where the line asks for more than it gets. Under the pointer: pale green
            // up to an empty slot (to assign), orange from a factory on (to take off)
            var grid = e.Q("pl-grid");
            if (grid.childCount == 0)
                for (int i = 0; i < GridSlots; i++)
                {
                    var slot = new VisualElement { pickingMode = PickingMode.Ignore };
                    slot.AddToClassList("pl-slot");
                    slot.Add(Hoi4Sprites.Make("pl-slot-bg"));
                    var icon = new VisualElement { name = "icon", pickingMode = PickingMode.Ignore };
                    icon.AddToClassList("spr");
                    icon.AddToClassList("pl-slot-icon");
                    slot.Add(icon);
                    grid.Add(slot);
                }
            int frameBase = group == 10 ? 10 : group == 5 ? 5 : 0;
            for (int i = 0; i < GridSlots; i++)
            {
                var slot = grid[i];
                bool shown = i < icons || (hoverSlot >= 0 && i < targetIcons);
                slot.style.visibility = shown ? Visibility.Visible : Visibility.Hidden;
                if (!shown) continue;
                int frame = frameBase + (removing && i >= hoverSlot ? 4 : i >= icons ? 1 : i * group < line.Active ? 0 : 3);
                var icon = slot.Q("icon");
                if (!(icon.userData is int f) || f != frame)
                {
                    icon.userData = frame;
                    Hoi4Sprites.Apply(icon, $"{Dir}factory_{frame}.png");
                }
            }
            e.Q("pl-glow").style.display = freeFactories && !collapsed ? DisplayStyle.Flex : DisplayStyle.None;

            foreach (string n in new[] { "pl-sub", "pl-add" })
            {
                var b = e.Q(n);
                b.EnableInClassList("x5", step == 5);
                b.EnableInClassList("x10", step == 10);
            }
            e.Q("pl-x5").EnableInClassList("on", step == 5);
            e.Q("pl-x10").EnableInClassList("on", step == 10);
            e.Q("pl-up").style.visibility = position > 0 ? Visibility.Visible : Visibility.Hidden;
        }

        /// <summary>A picker entry: the equipment's design name, general name, cost, picture, role icon and the
        /// resources a factory building it uses. `obsolete`: marked outdated (HOI4's brown background, the round
        /// button dark).</summary>
        public static void ShowPickerEntry(VisualElement e, int archetype, Hoi4Production.Buildable b, string general, bool obsolete)
        {
            e.Q("pp-outdated").style.display = obsolete ? DisplayStyle.Flex : DisplayStyle.None;
            e.Q("pp-obsolete").EnableInClassList("off", obsolete);
            e.Q<Label>("pp-name").text = b.Name;
            e.Q<Label>("pp-general").text = general;
            float cost = b.Cost.ToFloat(); // HOI4: "3", "0.50"
            e.Q<Label>("pp-cost").text = cost.ToString(Mathf.Approximately(cost, Mathf.Round(cost)) ? "0" : "0.00");
            Hoi4Sprites.Apply(e.Q("pp-picture"), b.Picture);
            ShowRole(e.Q("pp-role"), archetype);
            ShowResources(e.Q("pp-resources"), b.Resources, 1, 3);
        }
    }
}
