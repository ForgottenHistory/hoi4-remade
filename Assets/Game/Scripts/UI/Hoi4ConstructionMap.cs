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
        static readonly int HoverId = Shader.PropertyToID("_Hoi4ConstructionHover");
        const byte Half = 128, Full = 255, GreenBand = 48; // g: 48 green bands (deploy locations), 128 yellow, 255 blue

        static Texture2D tex;
        static Color32[] pixels;
        static bool shown;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            tex = null; pixels = null; shown = false;
            // shader globals outlive play mode in the editor: a session stopped while placing would start on terrain only
            Shader.SetGlobalFloat(ActiveId, 0f);
            Shader.SetGlobalFloat(HoverId, 0f);
        }

        /// <summary>Paint the player's states for the building and show the overlay; hoverState (0 none) glows.</summary>
        public static void Show(GameState gameState, ushort country, string building, int hoverState = 0)
        {
            var construction = Hoi4Construction.Instance;
            if (construction == null || building == null) { Hide(); return; }
            Ensure();
            System.Array.Clear(pixels, 0, pixels.Length);
            if (hoverState > 0)
                foreach (ushort p in Hoi4Regions.ProvincesOf(hoverState))
                    if (p < pixels.Length && !Hoi4Regions.IsWater(p)) pixels[p] = new Color32(0, 0, Full, 255);
            foreach (var kv in Hoi4Buildings.Users(gameState))
            {
                if (kv.Value != country) continue;
                int state = kv.Key;
                if (!construction.CanHold(state, building)) continue; // can't be built there (dockyard inland): plain terrain
                int queued = construction.Queued(state, building);
                int room = construction.Room(state, building);
                byte fill = room + queued > 0 ? Half : Full;                       // green: room without this building's orders
                byte bands = queued == 0 ? (byte)0 : room > 0 ? Half : Full;      // yellow: room left after them
                var c = new Color32(fill, bands, state == hoverState ? Full : (byte)0, 255);
                foreach (ushort p in Hoi4Regions.ProvincesOf(state))
                    if (p < pixels.Length) pixels[p] = c;
            }
            Upload(hoverState);
        }

        /// <summary>Picking a deploy location (recruit screen): plain terrain, green bands over the states the player may
        /// deploy to (g = GreenBand), the hovered state glowing.</summary>
        public static void ShowStates(System.Collections.Generic.ICollection<int> states, int hoverState)
        {
            Ensure();
            System.Array.Clear(pixels, 0, pixels.Length);
            if (hoverState > 0)
                foreach (ushort p in Hoi4Regions.ProvincesOf(hoverState))
                    if (p < pixels.Length && !Hoi4Regions.IsWater(p)) pixels[p] = new Color32(0, 0, Full, 255);
            foreach (int state in states)
            {
                var c = new Color32(0, GreenBand, state == hoverState ? Full : (byte)0, 255);
                foreach (ushort p in Hoi4Regions.ProvincesOf(state))
                    if (p < pixels.Length && !Hoi4Regions.IsWater(p)) pixels[p] = c;
            }
            Upload(hoverState);
        }

        static void Ensure()
        {
            if (tex != null) return;
            tex = new Texture2D(TexWidth, TexWidth, TextureFormat.RGBA32, false, linear: true)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Hoi4ConstructionTex"
            };
            pixels = new Color32[TexWidth * TexWidth];
        }

        static void Upload(int hoverState)
        {
            tex.SetPixelData(pixels, 0);
            tex.Apply(false);
            Shader.SetGlobalTexture(TexId, tex);
            Shader.SetGlobalFloat(HoverId, hoverState > 0 ? 1f : 0f);
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
            Shader.SetGlobalFloat(HoverId, 0f);
            shown = false;
        }
    }
}
