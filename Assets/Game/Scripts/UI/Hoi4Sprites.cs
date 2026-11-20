using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: binds runtime-loaded sprites to USS-styled elements. USS names the sprite with a custom
    /// property (--sprite: "gfx/interface/stateview/bg.png", a path under the data directory) on elements
    /// with the "spr" class; this loads it through Hoi4UiAssets, so no HOI4 art is ever baked into the
    /// project or a build. Data-driven sprites (flags, building icons) use Apply directly.
    /// </summary>
    public static class Hoi4Sprites
    {
        static readonly CustomStyleProperty<string> SpriteProperty = new CustomStyleProperty<string>("--sprite");

        /// <summary>Hook every ".spr" element under root (call on a freshly cloned tree, before it is attached).</summary>
        public static void Bind(VisualElement root)
        {
            root.Query(className: "spr").ForEach(Hook);
        }

        /// <summary>An element with the given USS classes whose --sprite is bound once styles resolve.</summary>
        public static VisualElement Make(params string[] classes)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            foreach (string c in classes) e.AddToClassList(c);
            e.AddToClassList("spr");
            Hook(e);
            return e;
        }

        public static void Hook(VisualElement e)
        {
            e.RegisterCallback<CustomStyleResolvedEvent>(OnStyleResolved);
        }

        static void OnStyleResolved(CustomStyleResolvedEvent evt)
        {
            var e = (VisualElement)evt.currentTarget;
            if (e.customStyle.TryGetValue(SpriteProperty, out string path))
                Apply(e, path.Trim('"'));
        }

        /// <summary>Set the element's background to a data-directory PNG (null clears it).</summary>
        public static void Apply(VisualElement e, string path)
        {
            var tex = string.IsNullOrEmpty(path) ? null : Hoi4UiAssets.Texture(path);
            e.style.backgroundImage = tex != null ? new StyleBackground(tex) : new StyleBackground(StyleKeyword.None);
        }

        /// <summary>The HOI4 UI font applied to a panel root (font properties inherit down the tree).</summary>
        public static void ApplyFont(VisualElement root)
        {
            var font = Hoi4UiAssets.Font;
            if (font != null) root.style.unityFontDefinition = FontDefinition.FromSDFFont(font);
        }
    }
}
