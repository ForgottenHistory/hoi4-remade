using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's scrollbars (interface/core.gui right_vertical_slider and bottom_horizontal_slider) on a UI Toolkit
    /// ScrollView: the 18 px bar background (GFX_scrollbar_vertical_bg, 52 px end caps holding the arrows), the 12 px
    /// groove (GFX_scroll_track, 4 px border) and the round 16 x 16 knob (GFX_scroll_drager), which keeps its size
    /// instead of growing with the visible share like Unity's (Unity's dragger stays, invisible, for dragging; the knob
    /// is drawn over it from the scroll position). Art from gfx/interface/scrollbar/ (convert_scrollbar_gfx).
    /// Positions are the gui's: up arrow at (2, 3), down 17 from the right and 19 from the bottom, groove at x 3.
    /// </summary>
    public static class Hoi4Scrollbar
    {
        const string Dir = "gfx/interface/scrollbar/";
        const float Bar = 18f, Arrow = 16f, Knob = 16f, Groove = 12f;

        /// <summary>Every ScrollView under root.</summary>
        public static void ApplyAll(VisualElement root) => root?.Query<ScrollView>().ForEach(Apply);

        /// <summary>Dress the view's scrollbars (once; later calls do nothing).</summary>
        public static void Apply(ScrollView view)
        {
            if (view == null || view.ClassListContains("hoi4-scroll")) return;
            view.AddToClassList("hoi4-scroll");
            Style(view.verticalScroller, true);
            Style(view.horizontalScroller, false);
        }

        static void Style(Scroller s, bool vertical)
        {
            Image(s, vertical ? "vertical_bg" : "horizontal_bg");
            s.style.backgroundColor = Color.clear;
            NoBorder(s);
            if (vertical)
            {
                s.style.width = Bar; s.style.minWidth = Bar;
                s.style.unitySliceTop = 52; s.style.unitySliceBottom = 52;
            }
            else
            {
                s.style.height = Bar; s.style.minHeight = Bar;
                s.style.unitySliceLeft = 52; s.style.unitySliceRight = 52;
            }

            Button(s.lowButton, vertical ? "up" : "left", vertical);
            Button(s.highButton, vertical ? "down" : "right", vertical);
            if (vertical)
            {
                s.lowButton.style.marginTop = 3; s.lowButton.style.marginLeft = 1;
                s.highButton.style.marginBottom = 3; s.highButton.style.marginLeft = 1;
            }
            else
            {
                s.lowButton.style.marginLeft = 3; s.lowButton.style.marginTop = 1;
                s.highButton.style.marginRight = 3; s.highButton.style.marginTop = 1;
            }

            var slider = s.slider;
            slider.style.marginLeft = slider.style.marginRight = slider.style.marginTop = slider.style.marginBottom = 0;
            if (vertical) { slider.style.marginTop = 2; slider.style.marginBottom = 2; }
            else { slider.style.marginLeft = 2; slider.style.marginRight = 2; }

            var tracker = slider.Q(className: "unity-base-slider__tracker");
            if (tracker != null)
            {
                Image(tracker, vertical ? "track" : "track_horizontal");
                tracker.style.backgroundColor = Color.clear;
                NoBorder(tracker);
                tracker.style.unitySliceLeft = tracker.style.unitySliceRight = 4;
                tracker.style.unitySliceTop = tracker.style.unitySliceBottom = 4;
                tracker.style.position = Position.Absolute;
                if (vertical) { tracker.style.left = 3; tracker.style.width = Groove; tracker.style.top = 0; tracker.style.bottom = 0; }
                else { tracker.style.top = 3; tracker.style.height = Groove; tracker.style.left = 0; tracker.style.right = 0; }
                tracker.style.marginLeft = tracker.style.marginRight = tracker.style.marginTop = tracker.style.marginBottom = 0;
            }
            slider.Q(className: "unity-base-slider__dragger-border")?.RemoveFromHierarchy();

            // Unity's dragger grows with the visible share and Unity keeps resizing it: leave it working but invisible
            // and draw HOI4's fixed knob on top, placed from the scroll position
            var dragger = slider.Q(className: "unity-base-slider__dragger");
            var container = slider.Q(className: "unity-base-slider__drag-container");
            if (dragger == null || container == null) return;
            dragger.style.backgroundColor = Color.clear;
            dragger.style.backgroundImage = new StyleBackground(StyleKeyword.None);
            NoBorder(dragger);
            var knob = new VisualElement { pickingMode = PickingMode.Ignore, name = "hoi4-knob" };
            Image(knob, "drager");
            knob.style.position = Position.Absolute;
            knob.style.width = Knob;
            knob.style.height = Knob;
            container.Add(knob);
            void Place()
            {
                float range = slider.highValue - slider.lowValue;
                float t = range > 0f ? Mathf.Clamp01((slider.value - slider.lowValue) / range) : 0f;
                if (vertical)
                {
                    float h = container.resolvedStyle.height;
                    knob.style.left = 1;
                    knob.style.top = float.IsNaN(h) ? 0f : t * Mathf.Max(0f, h - Knob);
                }
                else
                {
                    float w = container.resolvedStyle.width;
                    knob.style.top = 1;
                    knob.style.left = float.IsNaN(w) ? 0f : t * Mathf.Max(0f, w - Knob);
                }
            }
            slider.RegisterValueChangedCallback(_ => Place());
            container.RegisterCallback<GeometryChangedEvent>(_ => Place());
        }

        static void Button(VisualElement b, string image, bool vertical)
        {
            Image(b, image);
            b.style.backgroundColor = Color.clear;
            NoBorder(b);
            b.style.width = Arrow; b.style.height = Arrow;
            b.style.minWidth = Arrow; b.style.minHeight = Arrow;
            b.style.flexShrink = 0;
            b.style.display = DisplayStyle.Flex; // the default ScrollView theme hides them
            b.style.unityBackgroundImageTintColor = Color.white;
            foreach (var child in b.Children()) child.style.display = DisplayStyle.None; // Unity's own arrow
        }

        static void Image(VisualElement e, string name)
        {
            var tex = Hoi4UiAssets.Texture(Dir + name + ".png");
            if (tex != null) e.style.backgroundImage = new StyleBackground(tex);
        }

        static void NoBorder(VisualElement e)
        {
            e.style.borderLeftWidth = e.style.borderRightWidth = e.style.borderTopWidth = e.style.borderBottomWidth = 0;
        }
    }
}
