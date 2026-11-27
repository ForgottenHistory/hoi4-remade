using System.Collections;
using System.Collections.Generic;
using System.IO;
using Core.Modding;
using Hoi4.Units;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hoi4.Supply
{
    /// <summary>
    /// GAME (presentation): HOI4's naval base map icons (interface/mapicons.gui naval_base_mapicon). Every province
    /// with a naval base shows the anchor button on its naval base spot (Hoi4Ports.TryGetSpot), its pointer on the
    /// spot: blue for the player's ports (GFX_mapicon_naval_base), grey for everyone else's. HOI4's capacity text
    /// beside the player's ports ("ships repairing/level") is not drawn yet: there are no ships.
    ///
    /// In the supply map mode (Hoi4SupplyMapMode.Active) the player's hubs show HOI4's supply_node_map_icon
    /// instead, on every hub the player holds (at the rail point, or the naval base spot for a port): crates on a
    /// bubble (the capital's on a hexagon), a small anchor over them for a naval base, a red cross when the hub
    /// isn't connected to the capital. The crates are orange while supply is demanded of the hub, with
    /// "demand/capacity" in a dark box under it: the supply drawn from it today (Hoi4Supply.Drawn) over what it can pass on
    /// (Hoi4Railways.Hub.Capacity), both rounded up ("1/20"). With nothing demanded the crates are grey and there
    /// is no text. Other countries' ports keep the grey anchor.
    ///
    /// Drawn like the division counters: one mesh of camera-facing quads through the Hoi4/UnitCounter shader (a
    /// screen-space size at the anchor, shrinking and fading with the zoom), under the counters. The sprites and
    /// the digits come from gfx/map/ports/ (hoi4_convert.py write_ports) in one small atlas.
    /// Rebuilt when a port changes hands or level, the player's country or the map mode changes, and in the supply
    /// map mode when the hubs or the day's draw change. Reads only.
    /// Prototype: Tools/prototypes/ports/sea_supply.py.
    /// </summary>
    public sealed class Hoi4PortIcons : MonoBehaviour
    {
        [Tooltip("Screen pixels per sprite pixel at full size")]
        public float pixelScale = 1.4f;
        [Tooltip("Zoom (screen px per map px) at which the icons reach full size")]
        public float fullSizeZoom = 10f;
        [Tooltip("Smallest size, as a fraction of full size")]
        public float minSizeFraction = 0.6f;
        [Tooltip("Zoom (screen px per map px) below which the icons are hidden / above which fully visible")]
        public float fadeHiddenZoom = 2f, fadeVisibleZoom = 3.5f;

        const int Pad = 2;           // transparent pixels between the atlas sprites
        const int PointerPx = 3;     // the pointer under the button: this far below the spot's line
        const int HubLiftPx = 6;     // the crates stand this far above the spot, the text under them
        const string Chars = "0123456789/";

        static readonly int AtlasId = Shader.PropertyToID("_MainTex");
        static readonly int HeightmapId = Shader.PropertyToID("_HeightmapTexture");
        static readonly int HeightScaleId = Shader.PropertyToID("_HeightScale");
        static readonly int BoundsId = Shader.PropertyToID("_MapWorldBounds");
        static readonly int ParamsId = Shader.PropertyToID("_CounterParams");
        static readonly int SizeId = Shader.PropertyToID("_CounterSize");

        Material material;
        Mesh mesh;
        Texture2D atlas;
        RectInt blueRect, greyRect, textRect, hubRect, hubGreyRect, anchorRect, cutRect, bubbleRect, hexRect, textBgRect; // atlas pixels, y up
        int cellW;
        int mapWidth, mapHeight;
        Vector4 bounds;
        float worldPerMapPixel;
        int builtControl = -1, builtPorts = -1;
        ushort builtViewer = ushort.MaxValue;
        bool builtSupply;
        int builtHubs = -1, builtDay = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4PortIcons");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4PortIcons>();
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.TextureManager == null
                   || Hoi4UnitCounters.Instance == null || !Hoi4UnitCounters.Instance.IsReady || !Hoi4Ports.Available)
                yield return null;
            var engine = Engine.ArchonEngine.Instance;
            var counters = Hoi4UnitCounters.Instance;
            var shader = Shader.Find("Hoi4/UnitCounter");
            if (shader == null || !BuildAtlas())
            {
                ArchonLogger.LogWarning("Hoi4PortIcons: shader or sprites under gfx/map/ports missing (rerun Tools/hoi4_convert.py)", "game_units");
                enabled = false;
                yield break;
            }
            mapWidth = engine.TextureManager.MapWidth;
            mapHeight = engine.TextureManager.MapHeight;
            bounds = counters.MapWorldBounds;
            worldPerMapPixel = counters.WorldPerMapPixel;
            material = new Material(shader) { name = "Hoi4PortIcons" };
            material.renderQueue -= 1; // under the division counters
            material.SetTexture(AtlasId, atlas);
            material.SetVector(BoundsId, bounds);
            if (counters.Heightmap != null) material.SetTexture(HeightmapId, counters.Heightmap);
            material.SetFloat(HeightScaleId, counters.HeightScale);
            mesh = new Mesh { name = "Hoi4PortIcons", indexFormat = IndexFormat.UInt32 };
        }

        void OnDestroy()
        {
            if (material != null) Destroy(material);
            if (mesh != null) Destroy(mesh);
            if (atlas != null) Destroy(atlas);
        }

        void LateUpdate()
        {
            if (material == null) return;
            var gs = Engine.ArchonEngine.Instance?.GameState;
            if (gs?.Provinces == null) return;
            var player = StarterKit.Initializer.Instance?.PlayerState;
            ushort viewer = player != null && player.HasPlayerCountry ? player.PlayerCountryId : (ushort)0;
            bool supply = Hoi4SupplyMapMode.Active;
            if (builtControl != gs.Provinces.ControlVersion || builtPorts != Hoi4Ports.Version || builtViewer != viewer
                || builtSupply != supply || (supply && (builtHubs != Hoi4Railways.Version || builtDay != Hoi4Supply.DayVersion)))
            {
                builtControl = gs.Provinces.ControlVersion;
                builtPorts = Hoi4Ports.Version;
                builtViewer = viewer;
                builtSupply = supply;
                builtHubs = Hoi4Railways.Version;
                builtDay = Hoi4Supply.DayVersion;
                Rebuild(gs, viewer, supply);
            }
            material.SetVector(ParamsId, new Vector4(pixelScale, fadeHiddenZoom, Mathf.Max(fadeVisibleZoom, fadeHiddenZoom + 0.01f), 0f));
            material.SetVector(SizeId, new Vector4(Mathf.Max(fullSizeZoom, 0.01f), Mathf.Clamp01(minSizeFraction), worldPerMapPixel, 0f));
            var counters = Hoi4UnitCounters.Instance;
            if (mesh.vertexCount > 0 && (counters == null || !counters.Hidden))
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);
        }

        void Rebuild(Core.GameState gs, ushort viewer, bool supplyMode)
        {
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var offsets = new List<Vector2>();
            var tris = new List<int>();
            // what to draw: (spot, port province or 0, hub or null)
            var items = new List<(Vector2 spot, ushort province, bool hub)>();
            var hubHere = new HashSet<ushort>();
            if (supplyMode && viewer != 0)
                foreach (var hub in Hoi4Railways.HubsOf(viewer))
                {
                    if (!Hoi4Ports.TryGetSpot(hub.Province, out var at) || Hoi4Ports.Level(hub.Province) <= 0)
                        if (!Hoi4Railways.TryGetRailPoint(hub.Province, out at)) continue;
                    items.Add((at, hub.Province, true));
                    hubHere.Add(hub.Province);
                }
            foreach (ushort p in Hoi4Ports.Ports)
                if (!hubHere.Contains(p) && Hoi4Ports.TryGetSpot(p, out var spot)) items.Add((spot, p, false));
            // north first, so southern icons (nearer the camera) draw on top
            items.Sort((a, b) => a.spot.y != b.spot.y ? a.spot.y.CompareTo(b.spot.y) : a.province.CompareTo(b.province));
            foreach (var (spot, p, isHub) in items)
            {
                var anchor = new Vector3(bounds.x + spot.x / mapWidth * bounds.z, 0f, bounds.y + (mapHeight - spot.y) / mapHeight * bounds.w);
                if (!isHub)
                {
                    // the anchor button, its pointer on the spot. HOI4's "repairing/level" text beside the player's
                    // ports is left out for now: it doesn't show in HOI4's default map mode either (no ships yet)
                    bool mine = viewer != 0 && Hoi4Occupation.ControllerOf(gs, p) == viewer;
                    var icon = mine ? blueRect : greyRect;
                    Quad(verts, uvs, offsets, tris, anchor, icon, -icon.width * 0.5f, -PointerPx);
                    continue;
                }
                // supply map mode: the hub's crates, an anchor on them for a naval base, a red cross when it is
                // cut off, and under it "demand/capacity" (what was drawn from it today / what it can pass on)
                Hoi4Railways.TryGetHub(p, out var hub);
                float x0 = -hubRect.width * 0.5f, y0 = HubLiftPx;
                float demand = Hoi4Supply.Drawn(p).ToFloat();
                Quad(verts, uvs, offsets, tris, anchor, hub.IsCapital ? hexRect : bubbleRect, x0, y0);
                Quad(verts, uvs, offsets, tris, anchor, demand > 0f ? hubRect : hubGreyRect, x0, y0);
                if (Hoi4Ports.Level(p) > 0) Quad(verts, uvs, offsets, tris, anchor, anchorRect, x0, y0);
                if (!hub.Connected) Quad(verts, uvs, offsets, tris, anchor, cutRect, x0, y0);
                if (demand <= 0f) continue; // nothing demanded: grey crates, no text
                // both rounded up, like HOI4 (0.1 of 20.0 reads 1/20, the capital's 48.2 reads 49)
                string text = Mathf.CeilToInt(demand - 0.001f) + "/" + Mathf.CeilToInt(hub.Capacity.ToFloat() - 0.001f);
                // the dark box behind the text (GFX_on_map_supply_values_bg), its top over the icon's foot as in
                // mapicons.gui (box at y 24, text at y 32 of the 36 px icon)
                float boxTop = y0 + hubRect.height - 24f;
                Quad(verts, uvs, offsets, tris, anchor, textBgRect, -textBgRect.width * 0.5f, boxTop - textBgRect.height);
                float tx = -(text.Length * (cellW - 1) + 1) * 0.5f;
                float ty = boxTop - textBgRect.height + (textBgRect.height - textRect.height) * 0.5f;
                foreach (char ch in text)
                {
                    int i = Chars.IndexOf(ch);
                    if (i >= 0) Quad(verts, uvs, offsets, tris, anchor, new RectInt(textRect.x + i * cellW, textRect.y, cellW, textRect.height), tx, ty);
                    tx += cellW - 1;
                }
            }
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, offsets);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f); // the shader moves the quads in screen space: never cull
        }

        /// <summary>One sprite: four vertices at the anchor, offsets in sprite pixels (x right, y up) from it.</summary>
        void Quad(List<Vector3> verts, List<Vector2> uvs, List<Vector2> offsets, List<int> tris, Vector3 anchor, RectInt r, float x0, float y0)
        {
            int v = verts.Count;
            float u0 = (float)r.x / atlas.width, u1 = (float)(r.x + r.width) / atlas.width;
            float v0 = (float)r.y / atlas.height, v1 = (float)(r.y + r.height) / atlas.height;
            float x1 = x0 + r.width, y1 = y0 + r.height;
            for (int k = 0; k < 4; k++) verts.Add(anchor);
            uvs.Add(new Vector2(u0, v0)); offsets.Add(new Vector2(x0, y0));
            uvs.Add(new Vector2(u0, v1)); offsets.Add(new Vector2(x0, y1));
            uvs.Add(new Vector2(u1, v1)); offsets.Add(new Vector2(x1, y1));
            uvs.Add(new Vector2(u1, v0)); offsets.Add(new Vector2(x1, y0));
            tris.Add(v); tris.Add(v + 1); tris.Add(v + 2);
            tris.Add(v); tris.Add(v + 2); tris.Add(v + 3);
        }

        bool BuildAtlas()
        {
            var blue = Load("gfx/map/ports/port.png");
            var grey = Load("gfx/map/ports/port_grey.png");
            var text = Load("gfx/map/ports/text.png");
            var hub = Load("gfx/map/ports/hub.png");
            var anchor = Load("gfx/map/ports/hub_anchor.png");
            var cut = Load("gfx/map/ports/hub_cut.png");
            var bubble = Load("gfx/map/ports/hub_bubble.png");
            var hex = Load("gfx/map/ports/hub_hex.png");
            var hubGrey = Load("gfx/map/ports/hub_grey.png");
            var textBg = Load("gfx/map/ports/hub_text_bg.png");
            if (textBg == null) return false;
            if (blue == null || grey == null || text == null || hub == null || anchor == null || cut == null || bubble == null
                || hex == null || hubGrey == null) return false;
            int w = Pad * 11 + textBg.width + blue.width + grey.width + text.width + hub.width + anchor.width + cut.width + bubble.width + hex.width + hubGrey.width;
            int h = Pad * 2 + Mathf.Max(Mathf.Max(blue.height, grey.height), Mathf.Max(text.height, Mathf.Max(hub.height, Mathf.Max(anchor.height, cut.height))));
            atlas = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                name = "Hoi4PortIconAtlas", filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            atlas.SetPixels32(new Color32[w * h]);
            int x = Pad;
            blueRect = Place(blue, ref x);
            greyRect = Place(grey, ref x);
            textRect = Place(text, ref x);
            hubRect = Place(hub, ref x);
            anchorRect = Place(anchor, ref x);
            cutRect = Place(cut, ref x);
            bubbleRect = Place(bubble, ref x);
            hexRect = Place(hex, ref x);
            hubGreyRect = Place(hubGrey, ref x);
            textBgRect = Place(textBg, ref x);
            cellW = text.width / Chars.Length;
            atlas.Apply(false);
            Destroy(blue); Destroy(grey); Destroy(text); Destroy(hub); Destroy(anchor); Destroy(cut); Destroy(bubble); Destroy(hex); Destroy(hubGrey); Destroy(textBg);
            return true;
        }

        RectInt Place(Texture2D src, ref int x)
        {
            atlas.SetPixels32(x, Pad, src.width, src.height, src.GetPixels32());
            var r = new RectInt(x, Pad, src.width, src.height);
            x += src.width + Pad;
            return r;
        }

        static Texture2D Load(string relative)
        {
            string path = DataFileResolver.Resolve(relative);
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (tex.LoadImage(File.ReadAllBytes(path))) return tex;
            Destroy(tex);
            return null;
        }
    }
}
