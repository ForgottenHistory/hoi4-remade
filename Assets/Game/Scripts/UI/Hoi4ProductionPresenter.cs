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
            return archetype >= 0 && archetype < names.Length && names[archetype].Contains("tank");
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

        /// <summary>The window top: resources of the country's own states, factories used/total, factories per category.</summary>
        public static void ShowTop(VisualElement panel, GameState gameState, Hoi4Production production, ushort country,
                                   Dictionary<string, int> resources, FP[] lack)
        {
            int total = production.Factories(country);
            int used = System.Math.Min(total, production.Requested(country));
            var factories = panel.Q<Label>("pr-factories");
            factories.text = $"{used}/{total}";
            factories.EnableInClassList("free", used < total);

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
                    Hoi4Sprites.Apply(icon, $"gfx/interface/stateview/resources_{i}.png");
                    item.Add(icon);
                    var value = new Label { name = "value", pickingMode = PickingMode.Ignore };
                    value.AddToClassList("hoi4-label");
                    value.AddToClassList("pr-resource-value");
                    item.Add(value);
                    strip.Add(item);
                }
            for (int i = 0; i < strip.childCount; i++)
            {
                resources.TryGetValue(Hoi4.Map.Hoi4StateData.ResourceOrder[i], out int n);
                var value = strip[i].Q<Label>("value");
                value.text = n.ToString();
                value.EnableInClassList("none", n == 0);
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

        /// <summary>One line entry. `freeFactories` lights the factory grid's glow.</summary>
        public static void ShowLine(VisualElement e, ProductionLine line, int position, Hoi4Production.Buildable info,
                                    string name, string general, string picture, FP lack, FP stock, bool freeFactories, int step)
        {
            e.Q<Label>("pl-priority").text = (position + 1).ToString();
            e.Q<Label>("pl-name").text = name;
            e.Q<Label>("pl-general").text = general;
            e.Q<Label>("pl-factories").text = line.Requested.ToString();

            // efficiency on HOI4's 0-100% scale: the to-max bar (red, yellow tick at its end) up to the cap, the grey
            // fill over it up to the current value, so a line at the 50% cap is half full with the tick in the middle
            // and a line below the cap shows red between its value and the cap
            float cap = Mathf.Max(3f, Mathf.Round(93f * Mathf.Clamp01(Hoi4Production.MaxEfficiency.ToFloat())));
            float fill = Mathf.Min(cap - 3f, Mathf.Round(93f * Mathf.Clamp01(line.AverageEfficiency.ToFloat())));
            e.Q("pl-eff-max").style.width = cap;
            e.Q("pl-eff-fill").style.width = Mathf.Max(0f, fill);

            // progress to the next unit and units a day (a week when under one a day)
            float progress = line.Cost > FP.Zero ? Mathf.Clamp01((line.Progress / line.Cost).ToFloat()) : 0f;
            e.Q("pl-progress").style.width = Mathf.Round(118f * progress);
            float perDay = line.Cost > FP.Zero ? (Hoi4Production.LineOutput(line) / line.Cost).ToFloat() : 0f;
            e.Q<Label>("pl-speed").text = perDay >= 1f ? $"{perDay:0.00}/day" : $"{perDay * 7f:0.00}/week";

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
            // green while working, dark grey where the line asks for more than it gets
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
            int group = line.Requested > 75 ? 10 : line.Requested > GridSlots ? 5 : 1;
            int frameBase = group == 10 ? 10 : group == 5 ? 5 : 0;
            int icons = (line.Requested + group - 1) / group;
            for (int i = 0; i < GridSlots; i++)
            {
                var slot = grid[i];
                bool shown = i < icons;
                slot.style.visibility = shown ? Visibility.Visible : Visibility.Hidden;
                if (!shown) continue;
                int frame = frameBase + (i * group < line.Active ? 0 : 3);
                var icon = slot.Q("icon");
                if (!(icon.userData is int f) || f != frame)
                {
                    icon.userData = frame;
                    Hoi4Sprites.Apply(icon, $"{Dir}factory_{frame}.png");
                }
            }
            e.Q("pl-glow").style.display = freeFactories ? DisplayStyle.Flex : DisplayStyle.None;

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

        /// <summary>A picker entry: the equipment's design name, general name, cost and picture.</summary>
        public static void ShowPickerEntry(VisualElement e, string name, string general, FP cost, string picture)
        {
            e.Q<Label>("pp-name").text = name;
            e.Q<Label>("pp-general").text = general;
            e.Q<Label>("pp-cost").text = $"Production cost: {cost.ToFloat():0.##}";
            Hoi4Sprites.Apply(e.Q("pp-picture"), picture);
        }
    }
}
