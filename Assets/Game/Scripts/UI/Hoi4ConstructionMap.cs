using Core;
using Hoi4.Construction;
using Hoi4.Map;
using UnityEngine;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: the map while a building is being placed from the construction screen (HOI4's construction map mode):
    /// plain terrain, the player's states green while they have room for the building and blue when full (counting
    /// what is built and other buildings' orders, not this building's own orders), and states with an order for it in
    /// broad bands, yellow while room is left and blue once the orders fill the state. Drawn by
    /// Hoi4MapModes.hlsl Hoi4ConstructionOverlay from a province-indexed texture (r = fill, g = bands).
    /// Prototype: Tools/prototypes/construction/construction_map.py
    /// </summary>
    public static class Hoi4ConstructionMap
    {
        const int TexWidth = 256; // province id -> (id % 256, id / 256), like the other province-indexed overlays
        static readonly int TexId = Shader.PropertyToID("_Hoi4ConstructionTex");
        static readonly int ActiveId = Shader.PropertyToID("_Hoi4ConstructionActive");
        const byte Half = 128, Full = 255;

        static Texture2D tex;
        static Color32[] pixels;
        static bool shown;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            tex = null; pixels = null; shown = false;
            // shader globals outlive play mode in the editor: a session stopped while placing would start on terrain only
            Shader.SetGlobalFloat(ActiveId, 0f);
        }

        /// <summary>Paint the player's states for the building and show the overlay.</summary>
        public static void Show(GameState gameState, ushort country, string building)
        {
            var construction = Hoi4Construction.Instance;
            if (construction == null || building == null) { Hide(); return; }
            if (tex == null)
            {
                tex = new Texture2D(TexWidth, TexWidth, TextureFormat.RGBA32, false, linear: true)
                {
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Hoi4ConstructionTex"
                };
                pixels = new Color32[TexWidth * TexWidth];
            }
            System.Array.Clear(pixels, 0, pixels.Length);
            foreach (var kv in Hoi4Buildings.Users(gameState))
            {
                if (kv.Value != country) continue;
                int state = kv.Key;
                int queued = construction.Queued(state, building);
                int room = construction.Room(state, building);
                byte fill = room + queued > 0 ? Half : Full;                       // green: room without this building's orders
                byte bands = queued == 0 ? (byte)0 : room > 0 ? Half : Full;      // yellow: room left after them
                var c = new Color32(fill, bands, 0, 255);
                foreach (ushort p in Hoi4Regions.ProvincesOf(state))
                    if (p < pixels.Length) pixels[p] = c;
            }
            tex.SetPixelData(pixels, 0);
            tex.Apply(false);
            Shader.SetGlobalTexture(TexId, tex);
            if (!shown)
            {
                Shader.SetGlobalFloat(ActiveId, 1f);
                shown = true;
            }
        }

        public static void Hide()
        {
            if (!shown) return;
            Shader.SetGlobalFloat(ActiveId, 0f);
            shown = false;
        }
    }
}
