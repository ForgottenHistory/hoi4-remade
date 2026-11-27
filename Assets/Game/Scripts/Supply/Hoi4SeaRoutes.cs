using System.Collections;
using System.Collections.Generic;
using Map.Rendering.Lines;
using UnityEngine;

namespace Hoi4.Supply
{
    /// <summary>
    /// GAME (presentation): the sea supply routes in the supply map mode (Hoi4SupplyMapMode.Active): HOI4's white
    /// convoy ships (the traderoute map arrow) sailing from the port that supplies to the port supplied, for every
    /// port of the player that is connected by sea (Hoi4Railways.Hub.BySea and its Source). A route with supply
    /// moving over it (something was drawn today, Hoi4Supply.Drawn, from the port or from a hub fed through it) is
    /// drawn in full, line class sea_supply; an idle one faint and slower, sea_supply_idle. HOI4 lists a port's
    /// "supply transfers" only where supply is demanded, but there ships in port demand it too; we have none yet,
    /// so most routes would vanish.
    ///
    /// One engine MapLineLayer (visual style file, both classes: the ship texture gfx/map/ports/convoy.png,
    /// its size, and scroll, which moves the ships along the line). A route runs from the source port's spot over
    /// the centres of the sea provinces on the shortest way (Hoi4Ports.TryGetRoute) to the supplied port's spot,
    /// rounded off by corner cutting (Chaikin). A route that crosses the map's east-west seam is drawn in two
    /// pieces, one to each edge. Routes are kept per pair of ports: the sea doesn't change.
    /// Rebuilt when the hubs change (Hoi4Railways.Version), a day's supply was shared out (Hoi4Supply.DayVersion)
    /// or the player's country changes, and only while the map mode is shown. Reads only; never touches the simulation.
    /// Prototype: Tools/prototypes/ports/sea_supply.py (scale checked against an HOI4 screenshot).
    /// </summary>
    public sealed class Hoi4SeaRoutes : MonoBehaviour
    {
        const string LineClass = "sea_supply", IdleClass = "sea_supply_idle";
        const int Queue = MapLineLayer.DefaultQueue + 5; // with the supply railways
        const int Smoothing = 3;

        MapLineLayer layer;
        int builtHubs = -1, builtDay = -1;
        readonly HashSet<ushort> busy = new HashSet<ushort>(); // sea-supplied ports with supply moving to them
        ushort builtViewer = ushort.MaxValue;
        readonly Dictionary<uint, List<Vector2[]>> curves = new Dictionary<uint, List<Vector2[]>>(); // (source << 16 | port) -> its pieces
        float mapWidth;
        readonly List<Vector2> points = new List<Vector2>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4SeaRoutes");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4SeaRoutes>();
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.TextureManager == null
                   || Engine.ArchonEngine.Instance.MapMeshRenderer == null || !Hoi4Railways.Available || !Hoi4Ports.Available)
                yield return null;
            var engine = Engine.ArchonEngine.Instance;
            mapWidth = engine.TextureManager.MapWidth;
            layer = gameObject.AddComponent<MapLineLayer>();
            layer.enabled = false;
            if (!layer.Initialize(engine.TextureManager, engine.MapMeshRenderer.transform, Queue))
            {
                ArchonLogger.LogWarning("Hoi4SeaRoutes: could not set up the line layer", "game_units");
                enabled = false;
            }
        }

        void Update()
        {
            if (layer == null || !layer.IsInitialized) return;
            bool show = Hoi4SupplyMapMode.Active;
            if (layer.enabled != show) layer.enabled = show;
            if (!show) return;
            var gs = Engine.ArchonEngine.Instance?.GameState;
            var player = StarterKit.Initializer.Instance?.PlayerState;
            ushort viewer = player != null && player.HasPlayerCountry ? player.PlayerCountryId : (ushort)0;
            if (gs == null || (builtHubs == Hoi4Railways.Version && builtDay == Hoi4Supply.DayVersion && builtViewer == viewer)) return;
            builtHubs = Hoi4Railways.Version;
            builtDay = Hoi4Supply.DayVersion;
            builtViewer = viewer;

            // a hub that was drawn from marks every sea route on its way back to the capital
            busy.Clear();
            foreach (var hub in Hoi4Railways.HubsOf(viewer))
            {
                if (!hub.Connected || Hoi4Supply.Drawn(hub.Province) <= Core.Data.FixedPoint64.Zero) continue;
                var at = hub;
                for (int guard = 0; guard < 64; guard++)
                {
                    if (at.BySea) busy.Add(at.Province);
                    if (at.Source == 0 || !Hoi4Railways.TryGetHub(at.Source, out at)) break;
                }
            }

            var lines = new List<MapLine>();
            foreach (var hub in Hoi4Railways.HubsOf(viewer))
            {
                if (!hub.Connected || !hub.BySea || hub.Source == 0) continue;
                uint key = (uint)hub.Source << 16 | hub.Province;
                if (!curves.TryGetValue(key, out var pieces))
                    curves[key] = pieces = Hoi4Ports.TryGetRoute(gs, hub.Source, hub.Province, points) ? Pieces(points) : new List<Vector2[]>();
                foreach (var curve in pieces) lines.Add(new MapLine { Points = curve, ClassName = busy.Contains(hub.Province) ? LineClass : IdleClass });
            }
            layer.SetLines(lines);
        }

        /// <summary>The route as smooth lines: one, or two where it jumps over the map's east-west seam (each then
        /// runs on to its edge of the map).</summary>
        List<Vector2[]> Pieces(List<Vector2> route)
        {
            var result = new List<Vector2[]>();
            var run = new List<Vector2>();
            for (int i = 0; i < route.Count; i++)
            {
                if (i > 0 && Mathf.Abs(route[i].x - route[i - 1].x) > mapWidth * 0.5f)
                {
                    bool east = route[i - 1].x > route[i].x; // leaving over the east edge
                    float y = (route[i].y + route[i - 1].y) * 0.5f;
                    run.Add(new Vector2(east ? mapWidth : 0f, y));
                    if (run.Count > 1) result.Add(Smooth(run));
                    run = new List<Vector2> { new Vector2(east ? 0f : mapWidth, y) };
                }
                run.Add(route[i]);
            }
            if (run.Count > 1) result.Add(Smooth(run));
            return result;
        }

        /// <summary>Chaikin corner cutting, the ends kept: the route bends gently around the sea provinces' centres.</summary>
        static Vector2[] Smooth(List<Vector2> p)
        {
            var cur = new List<Vector2>(p);
            for (int it = 0; it < Smoothing && cur.Count > 2; it++)
            {
                var next = new List<Vector2>(cur.Count * 2) { cur[0] };
                for (int i = 0; i + 1 < cur.Count; i++)
                {
                    next.Add(Vector2.Lerp(cur[i], cur[i + 1], 0.25f));
                    next.Add(Vector2.Lerp(cur[i], cur[i + 1], 0.75f));
                }
                next.Add(cur[cur.Count - 1]);
                cur = next;
            }
            return cur.ToArray();
        }
    }
}
