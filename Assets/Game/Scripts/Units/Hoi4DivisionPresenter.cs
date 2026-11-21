using System.Collections.Generic;
using Core.Localization;
using Hoi4.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: stateless presentation logic for the division list. Fills the elements of DivisionPanel.uxml
    /// (structure and look live in the UXML/USS, not here).
    /// </summary>
    public static class Hoi4DivisionPresenter
    {
        const string Dir = "gfx/interface/unitpanel/";
        const int MaxCards = 60;
        const int CardHeight = 33;
        const int VisibleCards = 10;
        const int ListTop = 61;
        const int FooterHeight = 54;

        /// <summary>Fill the panel for the current selection. False when nothing is selected.</summary>
        public static bool Show(VisualElement panel, Core.GameState gameState, Hoi4UnitSelection selection, Hoi4Divisions divisions)
        {
            if (selection == null || divisions == null || gameState == null || selection.Selected.Count == 0)
                return false;

            var selected = selection.Selected;
            var context = selection.Context; // selection plus the army mates / split-off divisions
            int count = selected.Count;
            int total = context.Count;
            int cards = Mathf.Min(total, MaxCards);
            var army = total > 0 ? Hoi4Armies.Instance?.ArmyOf(context[0]) : null;
            if (army != null)
                foreach (ushort u in context) if (Hoi4Armies.Instance.ArmyOf(u) != army) { army = null; break; }
            int shown = Mathf.Min(cards, VisibleCards);

            panel.Q<Label>("header").text = army != null ? army.Name
                : count == 1 && total == 1 ? (divisions.Get(selected[0])?.Name ?? "Division")
                : $"<color=#8fd96b>{count}</color> Divisions";

            var list = panel.Q<ScrollView>("list");
            list.Clear();
            list.style.height = shown * CardHeight;
            for (int i = 0; i < cards; i++)
            {
                ushort id = context[i];
                list.Add(Card(id, selection.IsSelected(id), divisions.Get(id), gameState.Units.GetUnit(id), gameState, selection));
            }

            float footerY = ListTop + shown * CardHeight + 8;
            panel.Q("footer").style.top = footerY;
            panel.Q<Label>("note").text = total > cards
                ? $"+{total - cards} more divisions"
                : army != null ? ""
                : "These Divisions are unassigned. Right-Click an existing Army or create a new one to assign them.";
            panel.style.height = footerY + FooterHeight + 8;
            return true;
        }

        /// <summary>Keep the first half of the selection (HOI4 select-half button).</summary>
        public static void SelectHalf(Hoi4UnitSelection selection)
        {
            var selected = selection.Selected;
            if (selected.Count == 0) return;
            var half = new List<ushort>();
            for (int i = 0; i < (selected.Count + 1) / 2; i++) half.Add(selected[i]);
            selection.SelectSubset(half);
        }

        // --- Pieces ---------------------------------------------------------------------------

        static VisualElement Card(ushort id, bool isSelected, Hoi4Division d, Core.Units.UnitState unit, Core.GameState gameState, Hoi4UnitSelection selection)
        {
            var card = new VisualElement { pickingMode = PickingMode.Position };
            card.AddToClassList("dv-card");

            var bg = Hoi4Sprites.Make("dv-card-bg");
            bg.pickingMode = PickingMode.Position;
            if (!isSelected) bg.AddToClassList("dv-card-bg-off"); // dark: listed but not selected
            card.Add(bg);

            card.Add(Bar("dv-bar-org", d != null && d.MaxOrganisation.ToFloat() > 0 ? d.Organisation.ToFloat() / d.MaxOrganisation.ToFloat() : 1f));
            card.Add(Bar("dv-bar-str", d != null && d.MaxStrength.ToFloat() > 0 ? d.Strength.ToFloat() / d.MaxStrength.ToFloat() : 1f));

            card.Add(Hoi4Sprites.Make("dv-info"));
            var icon = Hoi4Sprites.Make("dv-icon");
            string type = d?.MainType ?? "infantry";
            string iconPath = Dir + $"icons/{type}.png";
            if (Hoi4UiAssets.Texture(iconPath) == null) iconPath = Dir + "icons/infantry.png";
            Hoi4Sprites.Apply(icon, iconPath);
            card.Add(icon);
            card.Add(Hoi4Sprites.Make("dv-level"));
            // unitview.gui bar_prepare: the planning bonus, of its maximum
            card.Add(Bar("dv-bar-plan", d != null ? d.Planning.ToFloat() / Hoi4BattlePlans.MaxPlanning.ToFloat() : 0f));

            card.Add(Text("dv-name", d?.Name ?? $"Unit {id}"));
            card.Add(Text("dv-status", Status(id, unit, gameState)));

            var close = Hoi4Sprites.Make("dv-card-close", "hoi4-button");
            close.pickingMode = PickingMode.Position;
            close.RegisterCallback<ClickEvent>(e =>
            {
                var rest = new List<ushort>();
                foreach (ushort other in selection.Selected) if (other != id) rest.Add(other);
                if (rest.Count == 0) selection.Clear(); else selection.SelectSubset(rest);
                e.StopPropagation();
            });
            card.Add(close);

            card.RegisterCallback<ClickEvent>(_ => selection.SelectSubset(new[] { id }));
            return card;
        }

        /// <summary>Thin vertical bar that fills upwards (organisation green, strength orange via USS).</summary>
        static VisualElement Bar(string cls, float fill)
        {
            var back = new VisualElement { pickingMode = PickingMode.Ignore };
            back.AddToClassList("dv-bar");
            back.AddToClassList(cls);
            var front = new VisualElement { pickingMode = PickingMode.Ignore };
            front.AddToClassList("dv-bar-fill");
            front.style.height = 28f * Mathf.Clamp01(fill);
            back.Add(front);
            return back;
        }

        static Label Text(string cls, string text)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("hoi4-label");
            l.AddToClassList(cls);
            return l;
        }

        static string Status(ushort id, Core.Units.UnitState unit, Core.GameState gameState)
        {
            var queue = gameState.Units.MovementQueue;
            if (queue != null && queue.TryGetMovementState(id, out var state))
            {
                var path = queue.GetFullPath(id);
                ushort goal = path.Count > 0 ? path[path.Count - 1] : state.destinationProvinceID;
                int hops = Mathf.Max(path.Count - 1, 1);
                return $"Moving to {ProvinceName(goal)} · {hops} province{(hops == 1 ? "" : "s")} left, next in {queue.DaysRemaining(state)}d";
            }
            return $"In {ProvinceName(unit.provinceID)}";
        }

        static string ProvinceName(ushort province)
        {
            string key = $"PROV{province}";
            string name = LocalizationManager.Get(key);
            return string.IsNullOrEmpty(name) || name == key ? $"province {province}" : name;
        }
    }
}
