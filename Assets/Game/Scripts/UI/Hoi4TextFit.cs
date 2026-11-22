using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4 text boxes shrink their text to fit (instantTextboxType maxWidth): long names such as
    /// "Division d'Infanterie Motorisée" get a smaller font instead of being clipped. Fit(label) keeps the label's
    /// font size from its style while the text fits, else scales it down to the label's width (never below MinScale).
    /// Re-fits when the label's layout changes; call Apply after changing its text.
    /// </summary>
    public static class Hoi4TextFit
    {
        const float MinScale = 0.6f;
        const float Margin = 4f; // px kept free inside the box

        public static void Fit(Label label)
        {
            label.RegisterCallback<GeometryChangedEvent>(_ => Apply(label));
        }

        /// <summary>Fit again after setting a fitted label's text (layout events don't fire for a text change).</summary>
        public static void Apply(Label label)
        {
            float width = label.resolvedStyle.width - Margin;
            if (width <= 0 || string.IsNullOrEmpty(label.text)) return;
            // the base size: the style sheet's, read with our inline size cleared
            if (!(label.userData is float baseSize))
            {
                label.style.fontSize = StyleKeyword.Null;
                baseSize = label.resolvedStyle.fontSize;
                if (baseSize <= 0) return;
                label.userData = baseSize;
            }
            float current = label.resolvedStyle.fontSize > 0 ? label.resolvedStyle.fontSize : baseSize;
            float measured = label.MeasureTextSize(label.text, 0f, VisualElement.MeasureMode.Undefined, 0f, VisualElement.MeasureMode.Undefined).x;
            if (measured <= 0) return;
            float atBase = measured * baseSize / current;
            float size = atBase <= width ? baseSize : System.Math.Max(baseSize * MinScale, baseSize * width / atBase);
            if (System.Math.Abs(size - current) > 0.25f) label.style.fontSize = size;
        }
    }
}
