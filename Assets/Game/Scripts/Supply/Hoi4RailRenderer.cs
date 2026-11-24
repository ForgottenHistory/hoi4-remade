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

        MapLineLayer track, overlay;
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
            if (!track.Initialize(engine.TextureManager, engine.MapMeshRenderer.transform)
                || !overlay.Initialize(engine.TextureManager, engine.MapMeshRenderer.transform, OverlayQueue))
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
        }

        void BuildTrack(ProvinceLinkNetwork network)
        {
            linkCurves.Clear();
            var lines = new List<MapLine>();
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
                        lines.Add(new MapLine { Points = curve, ClassName = "rail_" + network.Level(link) });
                }
            track.SetLines(lines);
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
