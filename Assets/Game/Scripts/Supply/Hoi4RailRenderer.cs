using System.Collections;
using System.Collections.Generic;
using Core.Graph;
using Map.Rendering.Lines;
using UnityEngine;

namespace Hoi4.Supply
{
    /// <summary>
    /// GAME (presentation): HOI4 railways on the map, through two engine MapLineLayers.
    ///
    ///   - Track: every rail link, drawn with the line class rail_LEVEL (visual style file: HOI4's railroad.dds tile
    ///     for that level, 0.4 map px wide, faded in at the unit counters' zoom). Links are joined into chains through
    ///     provinces with exactly two links of the same level, so the curve runs smoothly through them; chains end at
    ///     junctions, line ends and level changes.
    ///   - Converting: a grey strip (rail_converting) over the links whose provinces their controller can't use yet,
    ///     on a second layer above the track.
    ///   - Supply map mode (Hoi4SupplyMapMode.Active): the same chains, straight from rail point to rail point, as HOI4's white railway map arrows
    ///     (rail_supply_LEVEL: wider with each level, shown at every zoom) on a third layer, the track hidden
    ///     meanwhile. A layer draws only while its component is enabled.
    ///   - Also in the supply map mode, on a fourth layer above: the player's railway orders in orange (rail_planned)
    ///     and the line being laid out from the construction screen in yellow (rail_preview).
    ///
    /// Path: through each province's rail point (Hoi4Railways.TryGetRailPoint: HOI4's supply_node spot), as a centripetal
    /// Catmull-Rom curve. Each link gets its own piece of the curve, so the converting overlay follows the track exactly.
    /// Prototype: Tools/prototypes/supply/render_rails.py (scale checked against an HOI4 screenshot).
    ///
    /// Rebuilds: the track when the rail network changes (construction, later); the overlay when a conversion starts
    /// or ends (at most daily). Reads only; never touches the simulation.
    /// </summary>
    public sealed class Hoi4RailRenderer : MonoBehaviour
    {
        const int OverlayQueue = MapLineLayer.DefaultQueue + 6; // above the five track levels, below borders (2900)
        const float SamplesPerMapPx = 1.5f;

        /// <summary>One drawn stretch of railway of one level: where its level badge stands (the middle of its curve,
        /// map px, y down) and its links (indices into Hoi4Railways.Network).</summary>
        public struct Chain { public Vector2 Mid; public int Level; public int[] Links; public ushort[] Provinces; }

        /// <summary>A railway order of the player as drawn: where its level badge stands, the level it builds to, its
        /// last province's rail point, and whether it ends nowhere (no rail, hub, port or further order there: HOI4's
        /// roadblock).</summary>
        public struct Plan { public int OrderId; public Vector2 Mid; public int Level; public Vector2 End; public bool DeadEnd; }

        /// <summary>The player's railway orders as last drawn (supply map mode), for Hoi4PortIcons.</summary>
        public static readonly List<Plan> Plans = new List<Plan>();
        public static int PlansVersion { get; private set; }

        static List<ushort> preview;
        static int previewVersion;

        /// <summary>The railway the player is laying out (construction screen: start chosen, the cursor at its end),
        /// drawn in yellow; null for none. Presentation only.</summary>
        public static void SetPreview(List<ushort> path)
        {
            if (path == null && preview == null) return;
            if (path != null && preview != null && path.Count == preview.Count)
            {
                bool same = true;
                for (int i = 0; same && i < path.Count; i++) same = path[i] == preview[i];
                if (same) return; // asked again for the line already shown
            }
            preview = path;
            previewVersion++;
        }

        /// <summary>The chains as last built, for the construction view's rail level badges (Hoi4PortIcons).</summary>
        public static readonly List<Chain> Chains = new List<Chain>();
        public static int ChainsVersion { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetChains() { Chains.Clear(); ChainsVersion = 0; Plans.Clear(); PlansVersion = 0; preview = null; previewVersion = 0; }

        MapLineLayer track, overlay, supplyTrack, plans;
        int builtOrders = -1, builtPreview = -1;
        ushort builtViewer = ushort.MaxValue;
        int builtNetwork = -1, builtConversion = -1;
        // each link's piece of the curve, from LinkFrom to LinkTo
        readonly Dictionary<int, Vector2[]> linkCurves = new Dictionary<int, Vector2[]>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4RailRenderer");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4RailRenderer>();
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.TextureManager == null
                   || Engine.ArchonEngine.Instance.MapMeshRenderer == null || !Hoi4Railways.Available)
                yield return null;
            var engine = Engine.ArchonEngine.Instance;
            track = gameObject.AddComponent<MapLineLayer>();
            overlay = gameObject.AddComponent<MapLineLayer>();
            supplyTrack = gameObject.AddComponent<MapLineLayer>();
            supplyTrack.enabled = false;
            plans = gameObject.AddComponent<MapLineLayer>();
            plans.enabled = false;
            plans.Initialize(engine.TextureManager, engine.MapMeshRenderer.transform, OverlayQueue + 1);
            if (!track.Initialize(engine.TextureManager, engine.MapMeshRenderer.transform)
                || !overlay.Initialize(engine.TextureManager, engine.MapMeshRenderer.transform, OverlayQueue)
                || !supplyTrack.Initialize(engine.TextureManager, engine.MapMeshRenderer.transform))
            {
                ArchonLogger.LogWarning("Hoi4RailRenderer: could not set up the line layers", "game_units");
                enabled = false;
            }
        }

        void Update()
        {
            if (track == null || !track.IsInitialized) return;
            var network = Hoi4Railways.Network;
            if (network == null) return;
            if (builtNetwork != network.Version)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                BuildTrack(network);
                builtNetwork = network.Version;
                builtConversion = -1;
                ArchonLogger.Log($"Hoi4RailRenderer: {track.LineCount} rail chains from {network.LinkCount} links ({sw.ElapsedMilliseconds} ms)", "game_units");
            }
            if (builtConversion != Hoi4Railways.ConversionVersion)
            {
                BuildOverlay(network);
                builtConversion = Hoi4Railways.ConversionVersion;
            }
            bool supplyMode = Hoi4SupplyMapMode.Active;
            if (supplyTrack.enabled != supplyMode)
            {
                supplyTrack.enabled = supplyMode;
                track.enabled = !supplyMode;
            }
            if (plans != null && plans.IsInitialized)
            {
                if (plans.enabled != supplyMode) plans.enabled = supplyMode;
                var player = StarterKit.Initializer.Instance?.PlayerState;
                ushort viewer = player != null && player.HasPlayerCountry ? player.PlayerCountryId : (ushort)0;
                int orders = Hoi4.Construction.Hoi4Construction.Version;
                if (supplyMode && (builtOrders != orders || builtPreview != previewVersion || builtViewer != viewer))
                {
                    builtOrders = orders;
                    builtPreview = previewVersion;
                    builtViewer = viewer;
                    BuildPlans(viewer);
                }
            }
        }

        void BuildTrack(ProvinceLinkNetwork network)
        {
            linkCurves.Clear();
            Chains.Clear();
            ChainsVersion++;
            var lines = new List<MapLine>();
            var supplyLines = new List<MapLine>();
            var used = new bool[network.LinkCount];
            var chainProvinces = new List<ushort>();
            var chainLinks = new List<int>();

            // chains start at provinces that aren't a plain pass-through (two links, same level), then whatever is left
            // (closed loops) starts anywhere
            foreach (bool loops in new[] { false, true })
                for (int link = 0; link < network.LinkCount; link++)
                {
                    if (used[link] || network.Level(link) == 0) continue;
                    ushort a = network.From(link), b = network.To(link);
                    ushort start;
                    if (!PassThrough(network, a, link)) start = a;
                    else if (!PassThrough(network, b, link)) start = b;
                    else if (loops) start = a;
                    else continue;

                    chainProvinces.Clear();
                    chainLinks.Clear();
                    chainProvinces.Add(start);
                    ushort at = start;
                    int next = link;
                    while (next >= 0 && !used[next])
                    {
                        used[next] = true;
                        chainLinks.Add(next);
                        at = network.Other(next, at);
                        chainProvinces.Add(at);
                        next = PassThrough(network, at, next) ? OtherLink(network, at, next) : -1;
                    }
                    var curve = Curve(network, chainProvinces, chainLinks);
                    if (curve != null)
                    {
                        // the supply map mode draws railways straight from rail point to rail point (HOI4: only the
                        // track on the default map curves); the level badge stands halfway along that line
                        var straight = Through(chainProvinces) ?? curve;
                        Chains.Add(new Chain { Mid = Halfway(straight), Level = network.Level(link), Links = chainLinks.ToArray(), Provinces = chainProvinces.ToArray() });
                        lines.Add(new MapLine { Points = curve, ClassName = "rail_" + network.Level(link) });
                        supplyLines.Add(new MapLine { Points = straight, ClassName = "rail_supply_" + Mathf.Clamp(network.Level(link), 1, 5) });
                    }
                }
            track.SetLines(lines);
            supplyTrack.SetLines(supplyLines);
        }

        /// <summary>The player's railway orders in orange (rail_planned: every stretch of the path still below the
        /// order's level) and the line being laid out in yellow (rail_preview), with the orders' badge spots.</summary>
        void BuildPlans(ushort viewer)
        {
            var lines = new List<MapLine>();
            Plans.Clear();
            PlansVersion++;
            var construction = Hoi4.Construction.Hoi4Construction.Instance;
            var run = new List<ushort>();
            if (construction != null && viewer != 0)
                foreach (var item in construction.Queue(viewer))
                {
                    if (item.Path == null) continue;
                    bool marked = false;
                    run.Clear();
                    for (int k = 1; k <= item.Path.Count; k++)
                    {
                        bool pending = k < item.Path.Count && Hoi4Railways.RailLevel(item.Path[k - 1], item.Path[k]) < item.RailLevel;
                        if (pending)
                        {
                            if (run.Count == 0) run.Add(item.Path[k - 1]);
                            run.Add(item.Path[k]);
                            continue;
                        }
                        if (run.Count < 2) { run.Clear(); continue; }
                        var curve = Through(run);
                        run.Clear();
                        if (curve == null) continue;
                        lines.Add(new MapLine { Points = curve, ClassName = "rail_planned" });
                        if (marked) continue;
                        marked = true;
                        // the end that leads nowhere, if one does (lines start on the network when they can)
                        ushort last = item.Path[item.Path.Count - 1];
                        if (!Loose(construction, viewer, item, last) && Loose(construction, viewer, item, item.Path[0])) last = item.Path[0];
                        Hoi4Railways.TryGetRailPoint(last, out var end);
                        Plans.Add(new Plan
                        {
                            OrderId = item.Id, Mid = Halfway(curve), Level = item.RailLevel, End = end,
                            DeadEnd = Loose(construction, viewer, item, last),
                        });
                    }
                }
            if (preview != null && preview.Count >= 2)
            {
                var curve = Through(preview);
                if (curve != null) lines.Add(new MapLine { Points = curve, ClassName = "rail_preview" });
            }
            plans.SetLines(lines);
        }

        /// <summary>The order's end in this province leads nowhere: no built rail, hub or port there and no other
        /// order running through it.</summary>
        static bool Loose(Hoi4.Construction.Hoi4Construction construction, ushort country, Hoi4.Construction.ConstructionItem item, ushort province) =>
            Hoi4Railways.RailLevel(province) == 0 && !Hoi4Railways.IsHubSite(province) && Hoi4Ports.Level(province) == 0
            && !OtherOrderThrough(construction, country, item, province);

        /// <summary>Another railway order of the country runs through the province: a line laid on from there, so
        /// this one doesn't end nowhere.</summary>
        static bool OtherOrderThrough(Hoi4.Construction.Hoi4Construction construction, ushort country,
                                      Hoi4.Construction.ConstructionItem except, ushort province)
        {
            foreach (var other in construction.Queue(country))
                if (other != except && other.Path != null && other.Path.Contains(province)) return true;
            return false;
        }

        /// <summary>The point halfway along a line, by length.</summary>
        static Vector2 Halfway(Vector2[] line)
        {
            float total = 0f;
            for (int i = 1; i < line.Length; i++) total += Vector2.Distance(line[i - 1], line[i]);
            float left = total * 0.5f;
            for (int i = 1; i < line.Length; i++)
            {
                float d = Vector2.Distance(line[i - 1], line[i]);
                if (left <= d && d > 0f) return Vector2.Lerp(line[i - 1], line[i], left / d);
                left -= d;
            }
            return line[line.Length / 2];
        }

        /// <summary>Straight from rail point to rail point, like HOI4 draws every railway in the supply map mode, built,
        /// planned or being built (only the track on the default map gets the smooth curve).</summary>
        static Vector2[] Through(List<ushort> provinces)
        {
            var p = new Vector2[provinces.Count];
            for (int i = 0; i < p.Length; i++)
                if (!Hoi4Railways.TryGetRailPoint(provinces[i], out p[i])) return null;
            return p;
        }

        /// <summary>The province has exactly two links (counting live ones), of the same level as this one.</summary>
        static bool PassThrough(ProvinceLinkNetwork network, ushort province, int link)
        {
            int count = 0;
            bool sameLevel = true;
            foreach (int l in network.LinksOf(province))
            {
                if (network.Level(l) == 0) continue;
                count++;
                if (network.Level(l) != network.Level(link)) sameLevel = false;
            }
            return count == 2 && sameLevel;
        }

        static int OtherLink(ProvinceLinkNetwork network, ushort province, int link)
        {
            foreach (int l in network.LinksOf(province))
                if (l != link && network.Level(l) > 0) return l;
            return -1;
        }

        /// <summary>A centripetal Catmull-Rom curve through the chain's rail points; stores each link's piece.</summary>
        Vector2[] Curve(ProvinceLinkNetwork network, List<ushort> provinces, List<int> links)
        {
            int n = provinces.Count;
            var p = new Vector2[n];
            for (int i = 0; i < n; i++)
                if (!Hoi4Railways.TryGetRailPoint(provinces[i], out p[i])) return null;
            bool closed = n > 2 && provinces[0] == provinces[n - 1];

            var all = new List<Vector2> { p[0] };
            for (int i = 0; i < n - 1; i++)
            {
                Vector2 p0 = i > 0 ? p[i - 1] : closed ? p[n - 2] : p[i] * 2 - p[i + 1];
                Vector2 p3 = i + 2 < n ? p[i + 2] : closed ? p[1] : p[i + 1] * 2 - p[i];
                Vector2 p1 = p[i], p2 = p[i + 1];
                int samples = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(p1, p2) * SamplesPerMapPx), 2, 48);
                var piece = new Vector2[samples + 1];
                piece[0] = p1;
                for (int k = 1; k <= samples; k++)
                    piece[k] = CatmullRom(p0, p1, p2, p3, k / (float)samples);
                piece[samples] = p2;
                for (int k = 1; k <= samples; k++) all.Add(piece[k]);

                int link = links[i];
                if (network.From(link) != provinces[i]) System.Array.Reverse(piece); // store From -> To
                linkCurves[link] = piece;
            }
            return all.ToArray();
        }

        /// <summary>Centripetal Catmull-Rom (alpha 0.5) between p1 and p2: no cusps or loops on uneven spacing.</summary>
        static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
        {
            float t0 = 0f;
            float t1 = t0 + Mathf.Max(Mathf.Sqrt(Vector2.Distance(p0, p1)), 1e-4f);
            float t2 = t1 + Mathf.Max(Mathf.Sqrt(Vector2.Distance(p1, p2)), 1e-4f);
            float t3 = t2 + Mathf.Max(Mathf.Sqrt(Vector2.Distance(p2, p3)), 1e-4f);
            float u = Mathf.Lerp(t1, t2, t);
            Vector2 a1 = (t1 - u) / (t1 - t0) * p0 + (u - t0) / (t1 - t0) * p1;
            Vector2 a2 = (t2 - u) / (t2 - t1) * p1 + (u - t1) / (t2 - t1) * p2;
            Vector2 a3 = (t3 - u) / (t3 - t2) * p2 + (u - t2) / (t3 - t2) * p3;
            Vector2 b1 = (t2 - u) / (t2 - t0) * a1 + (u - t0) / (t2 - t0) * a2;
            Vector2 b2 = (t3 - u) / (t3 - t1) * a2 + (u - t1) / (t3 - t1) * a3;
            return (t2 - u) / (t2 - t1) * b1 + (u - t1) / (t2 - t1) * b2;
        }

        void BuildOverlay(ProvinceLinkNetwork network)
        {
            var lines = new List<MapLine>();
            if (Hoi4Railways.Converting > 0)
                for (int link = 0; link < network.LinkCount; link++)
                {
                    if (network.Level(link) == 0) continue;
                    if (!Hoi4Railways.IsConverting(network.From(link)) && !Hoi4Railways.IsConverting(network.To(link))) continue;
                    if (linkCurves.TryGetValue(link, out var piece))
                        lines.Add(new MapLine { Points = piece, ClassName = "rail_converting" });
                }
            overlay.SetLines(lines);
        }
    }
}
