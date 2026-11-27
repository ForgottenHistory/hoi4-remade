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

        /// <summary>Placing a province building (naval base, supply hub, railway) over the supply map mode: no fill;
        /// the provinces with an order for it in bands, yellow while the province has room for more levels and blue
        /// once the orders fill it (a supply hub has one level: blue at once); the hovered province glows, in blue
        /// bands when it is already full. For a railway the hovered province and the one the line starts from glow.</summary>
        public static void ShowProvinces(GameState gameState, ushort country, string building, ushort hover, ushort railStart)
        {
            var construction = Hoi4Construction.Instance;
            if (construction == null || building == null) { Hide(); return; }
            Ensure();
            System.Array.Clear(pixels, 0, pixels.Length);
            bool rail = building == Hoi4Construction.Railway;
            if (!rail)
                foreach (var item in construction.Queue(country))
                    if (item.Province != 0 && item.Building == building && item.Province < pixels.Length)
                        pixels[item.Province] = new Color32(0, construction.ProvinceRoom(item.Province, building) > 0 ? Half : Full, 0, 255);
            bool glow = false;
            if (hover != 0 && hover < pixels.Length && !Hoi4Regions.IsWater(hover)
                && Hoi4.Units.Hoi4Occupation.ControllerOf(gameState, hover) == country
                && (rail ? !Hoi4Regions.IsImpassable(hover) : construction.ProvinceMax(hover, building) > 0))
            {
                var c = pixels[hover];
                if (!rail && c.g == 0 && construction.ProvinceRoom(hover, building) == 0) c.g = Full; // nothing more fits
                pixels[hover] = new Color32(c.r, c.g, Full, 255);
                glow = true;
            }
            if (rail && railStart != 0 && railStart < pixels.Length)
            {
                pixels[railStart] = new Color32(0, Half, Full, 255);
                glow = true;
            }
            Upload(glow ? 1 : 0);
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
