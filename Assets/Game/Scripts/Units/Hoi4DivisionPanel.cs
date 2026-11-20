using System;
using System.Collections;
using System.Collections.Generic;
using Hoi4.UI;
using Core.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4's selected-units list (interface/unitview.gui army_division_list_view), top left under
    /// the top bar: a header with the count and buttons, the column heads, one green entry per
    /// division (pictogram, name, status, organisation and strength bars, deselect) and the
    /// "unassigned" note. Sprites come from Tools/hoi4_convert.py (gfx/interface/unitpanel/).
    /// Clicking an entry selects only that division. Uses the scene's existing UI Toolkit panel settings.
    /// </summary>
    public sealed class Hoi4DivisionPanel : MonoBehaviour
    {
        const string Dir = "gfx/interface/unitpanel/";
        const int MaxCards = 60;
        const int PanelWidth = 422;
        const int CardHeight = 33;
        const int VisibleCards = 10;

        static readonly Color TextMain = new Color(0.96f, 0.95f, 0.9f, 1f);
        static readonly Color TextDim = new Color(0.78f, 0.82f, 0.72f, 1f);
        static readonly Color OrgColor = new Color(0.45f, 0.85f, 0.3f, 1f);
        static readonly Color StrColor = new Color(0.95f, 0.6f, 0.2f, 1f);
        static readonly Color BarBack = new Color(0.03f, 0.03f, 0.03f, 0.9f);

        UIDocument document;
        VisualElement panel;
        Label header;
        ScrollView list;
        bool dirty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            var go = new GameObject("Hoi4DivisionPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4DivisionPanel>();
        }

        IEnumerator Start()
        {
            while (Hoi4UnitSelection.Instance == null)
                yield return null;

            // Borrow the panel settings (theme, scaling) of the scene's UI
            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4DivisionPanel: no UIDocument panel settings in the scene, panel disabled", "game_units");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 50;
            yield return null; // let the document create its root
            Build();
            Hoi4UnitSelection.Instance.Changed += () => dirty = true;
            dirty = true;
        }

        // --- Element helpers -----------------------------------------------------------------

        static VisualElement At(VisualElement parent, float x, float y, float w, float h)
        {
            var ve = new VisualElement { pickingMode = PickingMode.Ignore };
            ve.style.position = Position.Absolute;
            ve.style.left = x; ve.style.top = y;
            ve.style.width = w; ve.style.height = h;
            parent.Add(ve);
            return ve;
        }

        static VisualElement Sprite(VisualElement parent, string file, float x, float y)
        {
            var tex = Hoi4UiAssets.Texture(Dir + file + ".png");
            var ve = At(parent, x, y, tex != null ? tex.width : 0, tex != null ? tex.height : 0);
            if (tex != null) ve.style.backgroundImage = new StyleBackground(tex);
            return ve;
        }

        static VisualElement Button(VisualElement parent, string file, float x, float y, Action click)
        {
            var ve = Sprite(parent, file, x, y);
            ve.pickingMode = PickingMode.Position;
            // HOI4's buttonstate effect: brighter on hover, darker while pressed
            ve.RegisterCallback<PointerEnterEvent>(_ => ve.style.unityBackgroundImageTintColor = new Color(1.25f, 1.25f, 1.25f));
            ve.RegisterCallback<PointerLeaveEvent>(_ => ve.style.unityBackgroundImageTintColor = Color.white);
            ve.RegisterCallback<PointerDownEvent>(_ => ve.style.unityBackgroundImageTintColor = new Color(0.8f, 0.8f, 0.8f));
            ve.RegisterCallback<PointerUpEvent>(_ => ve.style.unityBackgroundImageTintColor = new Color(1.25f, 1.25f, 1.25f));
            if (click != null)
                ve.RegisterCallback<ClickEvent>(e => { click(); e.StopPropagation(); });
            return ve;
        }

        static Label Text(VisualElement parent, float x, float y, float w, float h, int size, Color color, TextAnchor align = TextAnchor.MiddleLeft)
        {
            var l = new Label { pickingMode = PickingMode.Ignore };
            l.style.position = Position.Absolute;
            l.style.left = x; l.style.top = y; l.style.width = w; l.style.height = h;
            l.style.fontSize = size;
            l.style.color = color;
            l.style.unityTextAlign = align;
            l.style.whiteSpace = WhiteSpace.NoWrap;
            l.style.overflow = Overflow.Hidden;
            l.style.textOverflow = TextOverflow.Ellipsis;
            l.style.paddingLeft = l.style.paddingRight = l.style.paddingTop = l.style.paddingBottom = 0;
            l.style.marginLeft = l.style.marginRight = l.style.marginTop = l.style.marginBottom = 0;
            var font = Hoi4UiAssets.Font;
            if (font != null) l.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
            parent.Add(l);
            return l;
        }

        // --- Build ---------------------------------------------------------------------------

        void Build()
        {
            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;

            // The frame: HOI4's 190 px window tile with 64 px borders, stretched
            panel = new VisualElement { name = "hoi4-division-panel" };
            var st = panel.style;
            st.position = Position.Absolute;
            st.left = 1; st.top = 90;
            st.width = PanelWidth;
            st.display = DisplayStyle.None;
            var window = Hoi4UiAssets.Texture(Dir + "window.png");
            if (window != null)
            {
                st.backgroundImage = new StyleBackground(window);
                st.unitySliceLeft = st.unitySliceRight = st.unitySliceTop = st.unitySliceBottom = 64;
            }
            root.Add(panel);
        }

        void Update()
        {
            if (!dirty || panel == null) return;
            dirty = false;
            Refresh();
        }

        void Refresh()
        {
            var selection = Hoi4UnitSelection.Instance;
            var divisions = Hoi4Divisions.Instance;
            var gameState = Engine.ArchonEngine.Instance?.GameState;
            if (selection == null || divisions == null || gameState == null || selection.Selected.Count == 0)
            {
                panel.style.display = DisplayStyle.None;
                return;
            }
            panel.style.display = DisplayStyle.Flex;
            panel.Clear();

            var selected = selection.Selected;
            int count = selected.Count;
            int cards = Mathf.Min(count, MaxCards);
            int shown = Mathf.Min(cards, VisibleCards);

            // Header row (y 8): delete, count, select half, hold, close
            Sprite(panel, "header", 7, 8);
            Button(panel, "btn_delete", 9, 8, null);
            header = Text(panel, 75, 10, 200, 28, 17, TextMain);
            header.text = count == 1
                ? (divisions.Get(selected[0])?.Name ?? "Division")
                : $"<color=#8fd96b>{count}</color> Divisions";
            Button(panel, "select_half", 300, 8, () =>
            {
                var half = new List<ushort>();
                for (int i = 0; i < (count + 1) / 2; i++) half.Add(selected[i]);
                selection.Select(half);
            });
            Button(panel, "hold", 330, 8, null);
            Button(panel, "btn_close", 362, 8, () => selection.Clear());

            // Column heads (y 39)
            float cx = 8;
            foreach (int w in new[] { 19, 19, 57, 26, 236, 31 })
            {
                var sort = Sprite(panel, "sort", cx, 39);
                sort.style.width = w;
                sort.style.unitySliceLeft = sort.style.unitySliceRight = 6;
                cx += w;
            }

            // List (y 61)
            list = new ScrollView(ScrollViewMode.Vertical);
            list.style.position = Position.Absolute;
            list.style.left = 5; list.style.top = 61;
            list.style.width = PanelWidth - 12;
            list.style.height = shown * CardHeight;
            list.horizontalScrollerVisibility = ScrollerVisibility.Hidden;
            list.verticalScrollerVisibility = ScrollerVisibility.Auto;
            StyleScroller(list.verticalScroller);
            panel.Add(list);

            for (int i = 0; i < cards; i++)
            {
                ushort id = selected[i];
                var d = divisions.Get(id);
                var unit = gameState.Units.GetUnit(id);
                list.Add(Card(id, d, unit, gameState, selection));
            }

            // Footer note (HOI4 shows it for units without an army)
            float footerY = 61 + shown * CardHeight + 8;
            Sprite(panel, "unassigned_bg", 5, footerY);
            var note = Text(panel, 22, footerY + 4, 362, 46, 15, TextMain, TextAnchor.MiddleCenter);
            note.style.whiteSpace = WhiteSpace.Normal;
            note.style.textOverflow = TextOverflow.Clip;
            note.style.fontSize = 13;
            note.text = count > cards
                ? $"+{count - cards} more divisions selected"
                : "These Divisions are unassigned. Right-Click an existing Army or create a new one to assign them.";
            panel.style.height = footerY + 54 + 8;
        }

        VisualElement Card(ushort id, Hoi4Division d, Core.Units.UnitState unit, Core.GameState gameState, Hoi4UnitSelection selection)
        {
            var card = new VisualElement { pickingMode = PickingMode.Position };
            card.style.height = CardHeight;
            card.style.flexShrink = 0;

            var bg = Sprite(card, "entry_bg_1", 2, 0);
            bg.pickingMode = PickingMode.Position;

            // Organisation (green) and strength (orange): thin vertical bars, filling upwards
            VBar(card, 11, d != null && d.MaxOrganisation > 0 ? d.Organisation / d.MaxOrganisation : 1f, OrgColor);
            VBar(card, 19, d != null && d.MaxStrength > 0 ? d.Strength / d.MaxStrength : 1f, StrColor);

            Sprite(card, "unitinfo", 22, 1);
            string type = d?.MainType ?? "infantry";
            var icon = Hoi4UiAssets.Texture(Dir + $"icons/{type}.png") ?? Hoi4UiAssets.Texture(Dir + "icons/infantry.png");
            if (icon != null)
            {
                var ic = At(card, 21, -6, icon.width, icon.height);
                ic.style.backgroundImage = new StyleBackground(icon);
            }
            var levelTex = Hoi4UiAssets.Texture(Dir + "level_0.png");
            if (levelTex != null)
            {
                var lv = At(card, 99, 3, levelTex.width, levelTex.height);
                lv.style.backgroundImage = new StyleBackground(levelTex);
            }

            var name = Text(card, 135, 1, 215, 18, 15, TextMain);
            name.text = d?.Name ?? $"Unit {id}";
            var status = Text(card, 135, 17, 215, 14, 11, TextDim);
            status.text = Status(id, unit, gameState);

            Button(card, "btn_close", 357, 1, () =>
            {
                var rest = new List<ushort>();
                foreach (ushort other in selection.Selected) if (other != id) rest.Add(other);
                if (rest.Count == 0) selection.Clear(); else selection.Select(rest);
            });

            card.RegisterCallback<ClickEvent>(_ => Hoi4UnitSelection.Instance?.Select(new[] { id }));
            return card;
        }

        // Thin dark scrollbar instead of Unity's default, no arrow buttons
        static void StyleScroller(Scroller sc)
        {
            sc.style.width = 8;
            sc.style.backgroundColor = new Color(0.04f, 0.05f, 0.05f, 0.85f);
            sc.lowButton.style.display = DisplayStyle.None;
            sc.highButton.style.display = DisplayStyle.None;
            sc.slider.style.marginLeft = sc.slider.style.marginRight = 0;
            sc.slider.style.marginTop = sc.slider.style.marginBottom = 0;
            sc.slider.style.borderTopWidth = sc.slider.style.borderBottomWidth = 0;
            sc.slider.style.borderLeftWidth = sc.slider.style.borderRightWidth = 0;
            sc.slider.style.backgroundColor = Color.clear;
            var tracker = sc.slider.Q(className: "unity-base-slider__tracker");
            if (tracker != null) tracker.style.display = DisplayStyle.None;
            var drag = sc.slider.Q(className: "unity-base-slider__dragger");
            if (drag != null)
            {
                drag.style.width = 8;
                drag.style.backgroundColor = new Color(0.55f, 0.62f, 0.5f, 1f);
                drag.style.borderTopWidth = drag.style.borderBottomWidth = drag.style.borderLeftWidth = drag.style.borderRightWidth = 0;
                drag.style.borderTopLeftRadius = drag.style.borderTopRightRadius = drag.style.borderBottomLeftRadius = drag.style.borderBottomRightRadius = 2;
            }
        }

        static void VBar(VisualElement parent, float x, float fill, Color color)
        {
            var back = At(parent, x, 3, 6, 28);
            back.style.backgroundColor = BarBack;
            float h = 28f * Mathf.Clamp01(fill);
            var front = At(back, 0, 28 - h, 6, h);
            front.style.backgroundColor = color;
        }

        static string Status(ushort id, Core.Units.UnitState unit, Core.GameState gameState)
        {
            var queue = gameState.Units.MovementQueue;
            if (queue != null && queue.TryGetMovementState(id, out var state))
            {
                var path = queue.GetFullPath(id);
                ushort goal = path.Count > 0 ? path[path.Count - 1] : state.destinationProvinceID;
                int hops = Mathf.Max(path.Count - 1, 1);
                return $"Moving to {ProvinceName(goal)} · {hops} province{(hops == 1 ? "" : "s")} left, next in {state.daysRemaining}d";
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
