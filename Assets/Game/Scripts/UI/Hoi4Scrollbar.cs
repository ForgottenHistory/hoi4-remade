using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's scrollbars (interface/core.gui right_vertical_slider and bottom_horizontal_slider) on a UI Toolkit
    /// ScrollView: the 18 px bar background (GFX_scrollbar_vertical_bg, 52 px end caps holding the arrows), the 12 px
    /// groove (GFX_scroll_track, 4 px border) and the round 16 x 16 knob (GFX_scroll_drager), which keeps its size
    /// instead of growing with the visible share like Unity's: the knob is drawn from the scroll position, and dragging
    /// or clicking the groove maps the pointer the same way (Unity's own dragger is left invisible and unreached). Art from gfx/interface/scrollbar/ (convert_scrollbar_gfx).
    /// Positions are the gui's: up arrow at (2, 3), down 17 from the right and 19 from the bottom, groove at x 3.
    /// </summary>
    public static class Hoi4Scrollbar
    {
        const string Dir = "gfx/interface/scrollbar/";
        const float Bar = 18f, Arrow = 16f, Knob = 16f, Groove = 12f;
        const float ArrowInset = 3f;                       // the arrow's gap to the bar's end
        const float ArrowSpan = ArrowInset + Arrow + 1f;   // where the groove starts, past the arrow

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
            // the arrows sit at the bar's very ends, over everything (up at 3 from the top, down 3 from the bottom);
            // the groove and knob run between them
            Pin(s.lowButton, vertical, true);
            Pin(s.highButton, vertical, false);
            s.lowButton.BringToFront();
            s.highButton.BringToFront();

            var slider = s.slider;
            slider.style.marginLeft = slider.style.marginRight = slider.style.marginTop = slider.style.marginBottom = 0;
            if (vertical) { slider.style.marginTop = ArrowSpan; slider.style.marginBottom = ArrowSpan; }
            else { slider.style.marginLeft = ArrowSpan; slider.style.marginRight = ArrowSpan; }

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

            // dragging is ours too: Unity's dragger moves over its own (short) travel, which no longer matches the
            // knob. A hit area over the groove maps the pointer the way the knob is drawn: the knob's centre follows it
            // (a grab keeps where on the knob it took hold); a click on the groove jumps there.
            var hit = new VisualElement { name = "hoi4-scroll-hit", pickingMode = PickingMode.Position };
            hit.style.position = Position.Absolute;
            hit.style.left = 0; hit.style.top = 0; hit.style.right = 0; hit.style.bottom = 0;
            container.Add(hit);              // over the groove and Unity's dragger
            knob.BringToFront();             // drawn on top, but not picked
            dragger.pickingMode = PickingMode.Ignore;
            float grab = 0f;
            float Along(Vector2 local) => vertical ? local.y : local.x;
            float Length() => vertical ? container.resolvedStyle.height : container.resolvedStyle.width;
            float KnobStart()
            {
                float range = slider.highValue - slider.lowValue;
                float t = range > 0f ? Mathf.Clamp01((slider.value - slider.lowValue) / range) : 0f;
                return t * Mathf.Max(0f, Length() - Knob);
            }
            void Seek(float along)
            {
                float travel = Length() - Knob;
                if (float.IsNaN(travel) || travel <= 0f) return;
                float t = Mathf.Clamp01((along - grab - Knob / 2f) / travel);
                slider.value = slider.lowValue + t * (slider.highValue - slider.lowValue);
            }
            hit.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                float along = Along(e.localPosition), start = KnobStart();
                grab = along >= start && along <= start + Knob ? along - (start + Knob / 2f) : 0f;
                hit.CapturePointer(e.pointerId);
                Seek(along);
                e.StopPropagation();
            });
            hit.RegisterCallback<PointerMoveEvent>(e =>
            {
                if (!hit.HasPointerCapture(e.pointerId)) return;
                Seek(Along(e.localPosition));
                e.StopPropagation();
            });
            hit.RegisterCallback<PointerUpEvent>(e =>
            {
                if (!hit.HasPointerCapture(e.pointerId)) return;
                hit.ReleasePointer(e.pointerId);
                e.StopPropagation();
            });
        }

        /// <summary>An arrow pinned to its end of the bar: 3 px in along the bar, 1 px in across it.</summary>
        static void Pin(VisualElement b, bool vertical, bool low)
        {
            b.style.position = Position.Absolute;
            b.style.marginLeft = b.style.marginRight = b.style.marginTop = b.style.marginBottom = 0;
            b.style.left = b.style.right = b.style.top = b.style.bottom = StyleKeyword.Auto;
            if (vertical)
            {
                b.style.left = 1;
                if (low) b.style.top = ArrowInset; else b.style.bottom = ArrowInset;
            }
            else
            {
                b.style.top = 1;
                if (low) b.style.left = ArrowInset; else b.style.right = ArrowInset;
            }
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
