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
    /// Construction, also in the supply map mode: a naval base or supply hub the player has on order shows HOI4's
    /// crossed hammers over its icon (grey crates where nothing stands yet). While a supply building is being
    /// placed from the construction screen (Hoi4ConstructionPanel.PlacingSupply) every stretch of railway carries
    /// its level on a dark disc at its middle (mapicons.gui railway_map_icon; Hoi4RailRenderer.Chains), in white,
    /// and every railway order the level it builds to, in orange under the hammers (Hoi4RailRenderer.Plans; it
    /// stands in for the badge of a stretch it is raising). The badges can be clicked (TryPickBadge: the
    /// construction screen's railway tool). An order whose line ends nowhere has a roadblock at its end.
    ///
    /// Drawn like the division counters: one mesh of camera-facing quads through the Hoi4/UnitCounter shader (a
    /// screen-space size at the anchor, shrinking and fading with the zoom), under the counters. The sprites and
    /// the digits come from gfx/map/ports/ (hoi4_convert.py write_ports) in one small atlas.
    /// Rebuilt when a port changes hands or level, the player's country or the map mode changes, and in the supply
    /// map mode when the hubs, the day's draw, the construction queues, the building being placed or the drawn
    /// railways change. Reads only.
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
        RectInt blueRect, greyRect, textRect, hubRect, hubGreyRect, anchorRect, cutRect, bubbleRect, hexRect, textBgRect, hammersRect, badgeRect, orangeRect, blockRect; // atlas pixels, y up
        int cellW;
        int mapWidth, mapHeight;
        Vector4 bounds;
        float worldPerMapPixel;
        int builtControl = -1, builtPorts = -1;
        ushort builtViewer = ushort.MaxValue;
        bool builtSupply;
        int builtHubs = -1, builtDay = -1, builtOrders = -1, builtChains = -1, builtPlans = -1;
        // the rail level badges drawn, for clicks: where, and the railway order (order) or chain (index) behind it
        readonly List<(Vector3 anchor, bool order, int id)> badges = new List<(Vector3, bool, int)>();

        public static Hoi4PortIcons Instance { get; private set; }
        void Awake() => Instance = this;

        /// <summary>The rail level badge under a screen position (the construction screen's railway tool): a railway
        /// order's (id = the order) or a stretch of railway's (id = its index in Hoi4RailRenderer.Chains). Mirrors
        /// the Hoi4/UnitCounter shader's size and placement.</summary>
        public bool TryPickBadge(Vector2 screen, out bool order, out int id)
        {
            order = false; id = -1;
            var cam = Camera.main;
            var counters = Hoi4UnitCounters.Instance;
            if (cam == null || counters == null || material == null) return false;
            float p11 = Mathf.Abs(cam.projectionMatrix[1, 1]);
            float bestDist = float.MaxValue;
            foreach (var b in badges)
            {
                var anchor = b.anchor;
                anchor.y = counters.TerrainHeight(anchor);
                var sp = cam.WorldToScreenPoint(anchor);
                if (sp.z <= 0f) continue;
                float worldPerScreenPx = 2f * Mathf.Max(sp.z, 1e-3f) / (p11 * Screen.height);
                float zoom = worldPerMapPixel / worldPerScreenPx;
                if (zoom < fadeHiddenZoom) continue;
                float scale = pixelScale * Mathf.Clamp(zoom / Mathf.Max(fullSizeZoom, 0.01f), Mathf.Clamp01(minSizeFraction), 1f);
                float reach = Mathf.Max(9f, badgeRect.width * 0.5f * scale + 3f);
                float d = Vector2.Distance(screen, new Vector2(sp.x, sp.y));
                if (d > reach || d >= bestDist) continue;
                bestDist = d; order = b.order; id = b.id;
            }
            return id >= 0;
        }
        string builtPlacing;

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
                || builtSupply != supply || (supply && (builtHubs != Hoi4Railways.Version || builtDay != Hoi4Supply.DayVersion
                    || builtOrders != Hoi4.Construction.Hoi4Construction.Version || builtChains != Hoi4RailRenderer.ChainsVersion
                    || builtPlacing != Hoi4.UI.Hoi4ConstructionPanel.PlacingSupply || builtPlans != Hoi4RailRenderer.PlansVersion)))
            {
                builtPlans = Hoi4RailRenderer.PlansVersion;
                builtOrders = Hoi4.Construction.Hoi4Construction.Version;
                builtChains = Hoi4RailRenderer.ChainsVersion;
                builtPlacing = Hoi4.UI.Hoi4ConstructionPanel.PlacingSupply;
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
            if (mesh.vertexCount > 0) // also while the construction screen hides the division counters
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
            badges.Clear();
            if (supplyMode && viewer != 0) Construction(gs, viewer, hubHere, verts, uvs, offsets, tris);
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, offsets);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f); // the shader moves the quads in screen space: never cull
        }

        Vector3 World(Vector2 spot) =>
            new Vector3(bounds.x + spot.x / mapWidth * bounds.z, 0f, bounds.y + (mapHeight - spot.y) / mapHeight * bounds.w);

        /// <summary>The player's supply construction: hammers over the naval bases and hubs on order, and while a
        /// supply building is being placed the level badge on every stretch of railway.</summary>
        void Construction(Core.GameState gs, ushort viewer, HashSet<ushort> hubHere, List<Vector3> verts, List<Vector2> uvs,
                          List<Vector2> offsets, List<int> tris)
        {
            var construction = Hoi4.Construction.Hoi4Construction.Instance;
            if (construction == null) return;
            float x0 = -hubRect.width * 0.5f, y0 = HubLiftPx;
            var marked = new HashSet<ushort>();
            foreach (var item in construction.Queue(viewer))
            {
                if (item.Province == 0 || !marked.Add(item.Province)) continue;
                bool naval = item.Building == Hoi4.Construction.Hoi4Construction.NavalBase;
                if (!(naval && Hoi4Ports.TryGetSpot(item.Province, out var spot)) && !Hoi4Railways.TryGetRailPoint(item.Province, out spot)) continue;
                var anchor = World(spot);
                if (!hubHere.Contains(item.Province)) // nothing stands here yet: the grey crates it will be
                {
                    Quad(verts, uvs, offsets, tris, anchor, bubbleRect, x0, y0);
                    Quad(verts, uvs, offsets, tris, anchor, hubGreyRect, x0, y0);
                    if (naval) Quad(verts, uvs, offsets, tris, anchor, anchorRect, x0, y0);
                }
                Quad(verts, uvs, offsets, tris, anchor, hammersRect, x0 + 8f, y0 + hubRect.height - 4f); // mapicons.gui: x 8, y -16
            }

            // a railway order that leads nowhere: the roadblock at its end
            foreach (var plan in Hoi4RailRenderer.Plans)
                if (plan.DeadEnd) Quad(verts, uvs, offsets, tris, World(plan.End), blockRect, -blockRect.width * 0.5f, -blockRect.height * 0.5f);

            if (Hoi4.UI.Hoi4ConstructionPanel.PlacingSupply == null) return;
            // the orders' own badges: the level they build to, orange, under the hammers
            foreach (var plan in Hoi4RailRenderer.Plans)
            {
                var at = World(plan.Mid);
                float h = badgeRect.width * 0.5f;
                Quad(verts, uvs, offsets, tris, at, badgeRect, -h, -h);
                Quad(verts, uvs, offsets, tris, at, new RectInt(orangeRect.x + Mathf.Clamp(plan.Level, 0, 9) * cellW, orangeRect.y, cellW, orangeRect.height),
                     -cellW * 0.5f, -orangeRect.height * 0.5f);
                Quad(verts, uvs, offsets, tris, at, hammersRect, -hammersRect.width * 0.5f, h - 2f);
                badges.Add((at, true, plan.OrderId));
            }
            var building = new HashSet<uint>();
            construction.RailLinksQueued(viewer, building);
            var network = Hoi4Railways.Network;
            for (int ci = 0; ci < Hoi4RailRenderer.Chains.Count; ci++)
            {
                var chain = Hoi4RailRenderer.Chains[ci];
                if (chain.Links.Length == 0 || network == null) continue;
                // the player's railways: the chain's first link lies in land it controls
                ushort first = network.From(chain.Links[0]);
                if (Hoi4Occupation.ControllerOf(gs, first) != viewer) continue;
                bool raising = false;
                foreach (int link in chain.Links)
                {
                    ushort a = network.From(link), b = network.To(link);
                    if (building.Contains(a < b ? (uint)a << 16 | b : (uint)b << 16 | a)) { raising = true; break; }
                }
                if (raising) continue; // the order's own badge stands for it
                var anchor = World(chain.Mid);
                float half = badgeRect.width * 0.5f;
                Quad(verts, uvs, offsets, tris, anchor, badgeRect, -half, -half);
                int i = Mathf.Clamp(chain.Level, 0, 9);
                Quad(verts, uvs, offsets, tris, anchor, new RectInt(textRect.x + i * cellW, textRect.y, cellW, textRect.height),
                     -cellW * 0.5f, -textRect.height * 0.5f);
                badges.Add((anchor, false, ci));
            }
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
            var hammers = Load("gfx/map/ports/hammers.png");
            var badge = Load("gfx/map/ports/rail_badge.png");
            var orange = Load("gfx/map/ports/text_orange.png");
            var block = Load("gfx/map/ports/rail_block.png");
            if (textBg == null || hammers == null || badge == null || orange == null || block == null) return false;
            if (blue == null || grey == null || text == null || hub == null || anchor == null || cut == null || bubble == null
                || hex == null || hubGrey == null) return false;
            int w = Pad * 15 + block.width + textBg.width + hammers.width + badge.width + orange.width + blue.width + grey.width + text.width + hub.width + anchor.width + cut.width + bubble.width + hex.width + hubGrey.width;
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
            hammersRect = Place(hammers, ref x);
            badgeRect = Place(badge, ref x);
            orangeRect = Place(orange, ref x);
            blockRect = Place(block, ref x);
            cellW = text.width / Chars.Length;
            atlas.Apply(false);
            Destroy(blue); Destroy(grey); Destroy(text); Destroy(hub); Destroy(anchor); Destroy(cut); Destroy(bubble); Destroy(hex); Destroy(hubGrey); Destroy(textBg); Destroy(hammers); Destroy(badge); Destroy(orange); Destroy(block);
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
