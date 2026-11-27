using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Hoi4.Map;
using Newtonsoft.Json.Linq;

namespace Hoi4.Supply
{
    /// <summary>
    /// GAME: HOI4 ports (naval bases): where they stand, their levels, and which can reach each other by sea.
    ///
    /// Data: map/hoi4_ports.json5 (hoi4_convert.py write_ports, from map/buildings.txt naval_base_spawn): for every
    /// coastal land province the spot on the shore where its naval base stands and the sea province its ships leave
    /// into, and every water province's visual centre. The 1936 levels come from the states' history
    /// (Hoi4StateData.Provincial, naval_base 1-10).
    ///
    /// A port does three things:
    ///   - Supply (Hoi4Railways, Hoi4Supply): it is a supply hub, fed by rail from the capital or by sea from another
    ///     of its country's ports on the same body of water.
    ///   - Naval transfer (Hoi4MovementRules, Hoi4Movement): divisions go to sea from a port their side controls,
    ///     into the port's sea province, and land only at such a port.
    ///   - The map (Hoi4PortIcons, Hoi4SeaRoutes): an anchor icon, and the sea supply routes in the supply map mode.
    ///
    /// There is no navy yet: every sea province is open to everyone, nothing is intercepted, no convoys are needed.
    /// Two ports reach each other by sea when their sea provinces are in the same body of water (the oceans are one;
    /// the Caspian and the lakes are their own), computed once from the province adjacency.
    /// Levels change only through SetLevel, from the deterministic simulation; Version counts the changes.
    /// Prototype: Tools/prototypes/ports/sea_supply.py.
    /// </summary>
    public static class Hoi4Ports
    {
        public const string PortsFile = "map/hoi4_ports.json5";
        public const string Building = "naval_base";
        public const int MaxLevel = 10; // naval_base province_max

        // by province id
        static int[] level = System.Array.Empty<int>();
        static ushort[] seaOf = System.Array.Empty<ushort>();   // a coastal province's sea province (0: no spot)
        static int[] spotX = System.Array.Empty<int>(), spotY = System.Array.Empty<int>(); // tenths of a map px, y down
        static int[] body = System.Array.Empty<int>();          // water province -> its body of water (0: not water)
        static readonly Dictionary<ushort, UnityEngine.Vector2> seaCentre = new Dictionary<ushort, UnityEngine.Vector2>();
        static readonly List<ushort> ports = new List<ushort>(); // provinces with a base (level > 0), sorted
        static bool portsDirty;

        // the water graph, compact (neighbours of p at wNbr[wOff[p]..wOff[p + 1]]), for bodies and drawn routes
        static int[] wOff;
        static ushort[] wNbr;

        static bool loaded, available;

        /// <summary>Raised by every level change.</summary>
        public static int Version { get; private set; }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            level = System.Array.Empty<int>(); seaOf = System.Array.Empty<ushort>();
            spotX = spotY = System.Array.Empty<int>(); body = System.Array.Empty<int>();
            seaCentre.Clear(); ports.Clear(); portsDirty = false;
            wOff = null; wNbr = null;
            seaDistance.Clear(); seaHeap.Clear();
            loaded = available = false;
            Version = 0;
        }

        public static bool Available => EnsureLoaded();

        // --- queries ------------------------------------------------------------------------------------

        /// <summary>The naval base level in the province (0: none).</summary>
        public static int Level(ushort province) => EnsureLoaded() && province < level.Length ? level[province] : 0;

        /// <summary>The sea province the province's naval base opens on (0: not coastal).</summary>
        public static ushort Sea(ushort province) => EnsureLoaded() && province < seaOf.Length ? seaOf[province] : (ushort)0;

        /// <summary>Every province with a naval base, sorted.</summary>
        public static IReadOnlyList<ushort> Ports
        {
            get
            {
                if (!EnsureLoaded()) return System.Array.Empty<ushort>();
                if (portsDirty)
                {
                    ports.Clear();
                    for (int p = 1; p < level.Length; p++) if (level[p] > 0) ports.Add((ushort)p);
                    portsDirty = false;
                }
                return ports;
            }
        }

        /// <summary>Where the province's naval base stands, map px (y down). Presentation.</summary>
        public static bool TryGetSpot(ushort province, out UnityEngine.Vector2 spot)
        {
            spot = default;
            if (Sea(province) == 0) return false;
            spot = new UnityEngine.Vector2(spotX[province] / 10f, spotY[province] / 10f);
            return true;
        }

        /// <summary>A point for sailing distances, in tenths of a map px (y down): a water province's centre, a
        /// coastal province's naval base spot.</summary>
        public static bool TryGetPoint(ushort province, out int x, out int y)
        {
            x = y = 0;
            if (!EnsureLoaded() || province >= seaOf.Length) return false;
            if (seaOf[province] == 0 && !seaCentre.ContainsKey(province)) return false;
            x = spotX[province];
            y = spotY[province];
            return true;
        }

        /// <summary>How far ships sail between two ports, for comparing routes: the shortest way over water between
        /// their sea provinces (whole map px between the provinces' centres), then the straight distance between
        /// the ports' spots to tell ports on the same waters apart. -1 when no way exists. Integers only. The
        /// distances from a sea province are worked out the first time a port on it asks and kept: the sea doesn't
        /// change (about 3,000 water provinces a search).</summary>
        public static long SailingDistance(GameState gameState, ushort portA, ushort portB)
        {
            if (!EnsureWater(gameState)) return -1;
            ushort a = Sea(portA), b = Sea(portB);
            if (a == 0 || b == 0 || a >= body.Length || b >= body.Length || body[a] == 0 || body[a] != body[b]) return -1;
            if (!seaDistance.TryGetValue(a, out int[] dist)) seaDistance[a] = dist = SeaDistances(a);
            if (dist[b] < 0) return -1;
            long dx = spotX[portA] - spotX[portB], dy = spotY[portA] - spotY[portB];
            return ((long)dist[b] << 32) | (uint)System.Math.Min(uint.MaxValue, dx * dx + dy * dy);
        }

        static readonly Dictionary<ushort, int[]> seaDistance = new Dictionary<ushort, int[]>(); // sea province -> px to every water province (-1: none)
        static readonly List<(int d, ushort p)> seaHeap = new List<(int, ushort)>();

        /// <summary>Dijkstra over the water provinces from one, by the whole px between their centres.</summary>
        static int[] SeaDistances(ushort from)
        {
            var dist = new int[wOff.Length - 1];
            for (int i = 0; i < dist.Length; i++) dist[i] = -1;
            dist[from] = 0;
            seaHeap.Clear();
            HeapPush(0, from);
            while (seaHeap.Count > 0)
            {
                var (d, u) = HeapPop();
                if (d != dist[u]) continue; // a stale entry
                for (int i = wOff[u]; i < wOff[u + 1]; i++)
                {
                    ushort v = wNbr[i];
                    if (v >= spotX.Length) continue;
                    long dx = (spotX[u] - spotX[v]) / 10, dy = (spotY[u] - spotY[v]) / 10;
                    int nd = d + (int)System.Math.Sqrt(dx * dx + dy * dy); // whole px: exact for these sizes
                    if (dist[v] >= 0 && dist[v] <= nd) continue;
                    dist[v] = nd;
                    HeapPush(nd, v);
                }
            }
            return dist;
        }

        static bool HeapBefore((int d, ushort p) a, (int d, ushort p) b) => a.d < b.d || (a.d == b.d && a.p < b.p);

        static void HeapPush(int d, ushort p)
        {
            seaHeap.Add((d, p));
            int i = seaHeap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!HeapBefore(seaHeap[i], seaHeap[parent])) break;
                (seaHeap[i], seaHeap[parent]) = (seaHeap[parent], seaHeap[i]);
                i = parent;
            }
        }

        static (int, ushort) HeapPop()
        {
            var top = seaHeap[0];
            seaHeap[0] = seaHeap[seaHeap.Count - 1];
            seaHeap.RemoveAt(seaHeap.Count - 1);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < seaHeap.Count && HeapBefore(seaHeap[l], seaHeap[m])) m = l;
                if (r < seaHeap.Count && HeapBefore(seaHeap[r], seaHeap[m])) m = r;
                if (m == i) break;
                (seaHeap[i], seaHeap[m]) = (seaHeap[m], seaHeap[i]);
                i = m;
            }
            return top;
        }

        /// <summary>Ships can sail between the two ports: their sea provinces are in the same body of water.</summary>
        public static bool SameWater(GameState gameState, ushort portA, ushort portB)
        {
            if (!EnsureWater(gameState)) return false;
            ushort a = Sea(portA), b = Sea(portB);
            return a != 0 && b != 0 && a < body.Length && b < body.Length && body[a] != 0 && body[a] == body[b];
        }

        /// <summary>Set a province's naval base level (construction, damage). Only coastal provinces with a spot.</summary>
        public static void SetLevel(ushort province, int newLevel)
        {
            if (Sea(province) == 0) return;
            newLevel = System.Math.Max(0, System.Math.Min(MaxLevel, newLevel));
            if (level[province] == newLevel) return;
            level[province] = newLevel;
            portsDirty = true;
            Version++;
        }

        /// <summary>The line a sea route between two ports is drawn along, map px (y down): the source's spot, the
        /// centres of the sea provinces on the shortest way between them, the destination's spot. Presentation only
        /// (floats); false when there is no way.</summary>
        public static bool TryGetRoute(GameState gameState, ushort fromPort, ushort toPort, List<UnityEngine.Vector2> points)
        {
            points.Clear();
            if (!EnsureWater(gameState) || !SameWater(gameState, fromPort, toPort)) return false;
            if (!TryGetSpot(fromPort, out var start) || !TryGetSpot(toPort, out var end)) return false;
            ushort a = Sea(fromPort), b = Sea(toPort);
            points.Add(start);
            if (a != b)
            {
                // Dijkstra over the water provinces by the distance between their centres
                var dist = new Dictionary<ushort, float> { [a] = 0f };
                var prev = new Dictionary<ushort, ushort>();
                var open = new SortedSet<(float d, ushort p)> { (0f, a) };
                while (open.Count > 0)
                {
                    var (d, u) = open.Min;
                    open.Remove(open.Min);
                    if (u == b) break;
                    if (d > dist[u] || !seaCentre.TryGetValue(u, out var cu)) continue;
                    for (int i = wOff[u]; i < wOff[u + 1]; i++)
                    {
                        ushort v = wNbr[i];
                        if (!seaCentre.TryGetValue(v, out var cv)) continue;
                        float dx = cu.x - cv.x, dy = cu.y - cv.y;
                        float nd = d + UnityEngine.Mathf.Sqrt(dx * dx + dy * dy);
                        if (dist.TryGetValue(v, out float had) && had <= nd) continue;
                        dist[v] = nd;
                        prev[v] = u;
                        open.Add((nd, v));
                    }
                }
                if (!prev.ContainsKey(b)) { points.Clear(); return false; }
                // the first and last sea provinces touch the ports: the ports' spots stand in for their centres
                var mid = new List<UnityEngine.Vector2>();
                for (ushort at = prev[b]; at != a; at = prev[at]) mid.Add(seaCentre[at]);
                mid.Reverse();
                points.AddRange(mid);
            }
            points.Add(end);
            return true;
        }

        // --- loading ------------------------------------------------------------------------------------

        static bool EnsureLoaded()
        {
            if (loaded) return available;
            if (!DataFileResolver.IsInitialized || !Hoi4StateData.Available || !Hoi4Regions.Available) return false; // try again on the next call
            loaded = true;
            string path = DataFileResolver.Resolve(PortsFile);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4Ports: {path} not found (rerun hoi4_convert.py); no ports", "map_initialization");
                return available = false;
            }
            try
            {
                JObject root = Json5Loader.LoadJson5File(path);
                int size = 1;
                var sites = root["ports"] as JObject;
                if (sites != null) foreach (var p in sites.Properties()) size = System.Math.Max(size, int.Parse(p.Name) + 1);
                var sea = root["sea"] as JObject;
                if (sea != null) foreach (var p in sea.Properties()) size = System.Math.Max(size, int.Parse(p.Name) + 1);
                level = new int[size];
                seaOf = new ushort[size];
                spotX = new int[size];
                spotY = new int[size];
                body = new int[size];
                if (sites != null)
                    foreach (var p in sites.Properties())
                    {
                        if (!(p.Value is JArray v) || v.Count < 3) continue;
                        int id = int.Parse(p.Name);
                        spotX[id] = (int)System.Math.Round((double)v[0] * 10);
                        spotY[id] = (int)System.Math.Round((double)v[1] * 10);
                        seaOf[id] = (ushort)(int)v[2];
                    }
                if (sea != null)
                    foreach (var p in sea.Properties())
                        if (p.Value is JArray v && v.Count == 2)
                        {
                            ushort id = ushort.Parse(p.Name);
                            seaCentre[id] = new UnityEngine.Vector2((float)v[0], (float)v[1]);
                            spotX[id] = (int)v[0] * 10; // water provinces keep their centre where ports keep their spot
                            spotY[id] = (int)v[1] * 10;
                        }

                int bases = 0, noSpot = 0;
                foreach (int state in Hoi4Regions.States)
                {
                    var info = Hoi4StateData.Get(state);
                    if (info == null) continue;
                    foreach (var kv in info.Provincial)
                    {
                        if (!kv.Value.TryGetValue(Building, out int n) || n <= 0) continue;
                        if (kv.Key <= 0 || kv.Key >= size || seaOf[kv.Key] == 0) { noSpot++; continue; }
                        level[kv.Key] = System.Math.Min(MaxLevel, n);
                        bases++;
                    }
                }
                portsDirty = true;
                ArchonLogger.Log($"Hoi4Ports: {sites?.Count ?? 0} naval base spots, {bases} naval bases"
                                 + (noSpot > 0 ? $", {noSpot} without a spot (left out)" : ""), "map_initialization");
                return available = true;
            }
            catch (System.Exception e)
            {
                ArchonLogger.LogError($"Hoi4Ports: failed to read {path}: {e.Message}", "map_initialization");
                return available = false;
            }
        }

        /// <summary>The water graph and the bodies of water, once the province adjacency exists.</summary>
        static bool EnsureWater(GameState gameState)
        {
            if (!EnsureLoaded()) return false;
            if (wOff != null) return true;
            if (gameState?.Adjacencies == null) return false;
            gameState.Adjacencies.GetCompactGraph(out int[] allOff, out ushort[] allNbr);
            int size = allOff.Length - 1;
            var off = new int[size + 1];
            var nbr = new List<ushort>();
            for (int p = 0; p < size; p++)
            {
                off[p] = nbr.Count;
                if (p == 0 || !Hoi4Regions.IsWater((ushort)p)) continue;
                for (int i = allOff[p]; i < allOff[p + 1]; i++)
                    if (Hoi4Regions.IsWater(allNbr[i])) nbr.Add(allNbr[i]);
            }
            off[size] = nbr.Count;
            wNbr = nbr.ToArray();
            if (body.Length < size) System.Array.Resize(ref body, size);
            int bodies = 0;
            var stack = new Stack<ushort>();
            for (int p = 1; p < size; p++)
            {
                if (body[p] != 0 || !Hoi4Regions.IsWater((ushort)p)) continue;
                body[p] = ++bodies;
                stack.Push((ushort)p);
                while (stack.Count > 0)
                {
                    ushort u = stack.Pop();
                    for (int i = off[u]; i < off[u + 1]; i++)
                    {
                        ushort v = wNbr[i];
                        if (v >= body.Length || body[v] != 0) continue;
                        body[v] = bodies;
                        stack.Push(v);
                    }
                }
            }
            wOff = off; // last: the graph is ready
            ArchonLogger.Log($"Hoi4Ports: {bodies} bodies of water", "map_initialization");
            return true;
        }
    }
}
