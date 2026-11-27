using System.Collections.Generic;
using System.IO;
using Core;
using Core.Graph;
using Core.Loaders;
using Core.Modding;
using Hoi4.Construction;
using Hoi4.Map;
using Hoi4.Units;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Supply
{
    /// <summary>
    /// GAME: HOI4 railways, supply hubs and ports, and which of them are connected to their country's capital (by
    /// rail, or by sea between ports; no rivers as links).
    ///
    /// Data: map/hoi4_railways.json5 (hoi4_convert.py, from map/railways.txt and map/supply_nodes.txt): rail links
    /// between bordering provinces with a level (1-5), and the 1936 hubs. The links live in the engine's
    /// ProvinceLinkNetwork; this class holds the HOI4 rules on top.
    ///
    /// Rules (HOI4 NSupply defines and the Logistics wiki page):
    ///   - A country uses a rail link when it controls both of its provinces and both are converted. A province whose
    ///     controller changed can't be used by the new controller for RAILWAY_CONVERSION_COOLDOWN days (10), or
    ///     RAILWAY_CONVERSION_COOLDOWN_CORE (5) when the province's state is a core of the new controller. The time is
    ///     counted from the engine's control change tick (ProvinceSystem.TryGetControlChangeTick). Hubs themselves need
    ///     no conversion, but the rail in the hub's province does.
    ///   - A country's capital is always a hub (152 HOI4 capitals aren't in supply_nodes.txt, most of them tags that
    ///     don't exist in 1936). Such a capital-only province is a hub only while its controller's capital is there.
    ///   - A hub belongs to the controller of its province. It is connected when its controller also controls its
    ///     capital and a usable rail path joins them. Its connection level is the bottleneck: the best path's lowest
    ///     link level (ProvinceLinkNetwork.WidestPaths).
    ///   - A connected hub's capacity: RAILWAY_BASE_FLOW + RAILWAY_FLOW_PER_LEVEL * level (10 + 5 per level).
    ///   - A naval base (Hoi4Ports) is a hub too, of its province's controller. One the capital reaches by rail is
    ///     connected like any hub. One it doesn't is supplied by sea from another of the country's connected ports
    ///     on the same body of water (no navy yet: nothing blocks the way, no convoys are needed). Its capacity is
    ///     the smallest along the way: NAVAL_BASE_FLOW + NAVAL_FLOW_PER_LEVEL * level (5 + 3 per level: 8 at level
    ///     1, 35 at 10) of both ports, and the source port's own capacity. Rail is preferred: one sea route opens
    ///     at a time (the one that carries the most, then the shortest; a port takes the source nearest by sea that
    ///     carries at least half of what its best one could), and the hubs and ports with rail to the
    ///     port it reaches are connected through it, at most its capacity, before the next route is chosen. Their
    ///     ports can supply further ports. A rail network across the sea so gets one route, not one per port.
    ///   - The capital's capacity: CAPITAL_SUPPLY_BASE + 0.3 per civilian factory + 0.6 per military factory + 0.4 per
    ///     dockyard, over the states whose buildings work for the country (Hoi4Buildings.UserOf).
    ///
    /// Runs daily from Hoi4Simulation, before anything that will read supply. It recomputes only when control,
    /// buildings, rail or a conversion finishing changed something, and then for every country at once (one widest
    /// path per capital: about 3,300 links in all). Deterministic: integer levels, fixed-point capacities, province
    /// and country order fixed. Version counts the recomputes that changed a hub, for caches and the recorder.
    /// </summary>
    public static class Hoi4Railways
    {
        public const string RailwaysFile = "map/hoi4_railways.json5";

        public const int MaxLevel = 5;                 // NSupply.MAX_RAILWAY_LEVEL
        public const int ConversionDays = 10;          // NSupply.RAILWAY_CONVERSION_COOLDOWN
        public const int ConversionDaysCore = 5;       // NSupply.RAILWAY_CONVERSION_COOLDOWN_CORE
        static readonly FP RailwayBaseFlow = FP.FromInt(10);          // NSupply.RAILWAY_BASE_FLOW
        static readonly FP RailwayFlowPerLevel = FP.FromInt(5);       // NSupply.RAILWAY_FLOW_PER_LEVEL
        static readonly FP CapitalSupplyBase = FP.FromInt(5);         // NSupply.CAPITAL_SUPPLY_BASE
        static readonly FP CapitalPerCivilian = FP.FromFraction(3, 10);  // NSupply.CAPITAL_SUPPLY_CIVILIAN_FACTORIES
        static readonly FP CapitalPerMilitary = FP.FromFraction(6, 10);  // NSupply.CAPITAL_SUPPLY_MILITARY_FACTORIES
        static readonly FP CapitalPerDockyard = FP.FromFraction(4, 10);  // NSupply.CAPITAL_SUPPLY_DOCKYARDS
        static readonly FP NavalBaseFlow = FP.FromInt(5);             // NSupply.NAVAL_BASE_FLOW
        static readonly FP NavalFlowPerLevel = FP.FromInt(3);         // NSupply.NAVAL_FLOW_PER_LEVEL

        /// <summary>One supply hub as of the last recompute.</summary>
        public struct Hub
        {
            public ushort Province;
            /// <summary>Its province's controller (0: none).</summary>
            public ushort Country;
            /// <summary>The controller's capital province.</summary>
            public bool IsCapital;
            /// <summary>The controller holds its capital and a usable rail path joins them (always true for the capital).</summary>
            public bool Connected;
            /// <summary>The connection's bottleneck rail level (0 when not connected; the capital's is 0 too).</summary>
            public int Level;
            /// <summary>Rail links from the capital on the path found (0 for the capital and when not connected).</summary>
            public int Distance;
            /// <summary>Supply the hub can pass on: the capital's supply for the capital, else 10 + 5 * Level, or what
            /// its sea route carries; 0 when not connected.</summary>
            public FP Capacity;
            /// <summary>A naval base and no supply hub of its own: supply spreads from it with the naval base numbers.</summary>
            public bool IsPort;
            /// <summary>Connected by sea (not by rail) from the port Source.</summary>
            public bool BySea;
            /// <summary>The port this hub hangs on: its sea source, or the sea-supplied port its rail starts at (0: the capital).</summary>
            public ushort Source;
        }

        static ProvinceLinkNetwork network;
        // provinces with a rail link or a hub, sorted (the only ones whose conversion matters)
        static ushort[] railProvinces = System.Array.Empty<ushort>();
        static ushort[] hubProvinces = System.Array.Empty<ushort>(); // sorted: supply_nodes.txt hubs and every capital
        static bool[] listedHub = System.Array.Empty<bool>();        // per hubProvinces entry: in supply_nodes.txt
        static readonly HashSet<ushort> listed = new HashSet<ushort>(); // supply_nodes.txt
        static int hubListPorts = -1;                                // Hoi4Ports.Version the hub list was built at

        // conversion as of the last daily check, by province id (true: its controller can use its rail)
        static bool[] converted = System.Array.Empty<bool>();
        static int converting;

        // where rail runs through each province, map px (y down): presentation only, never read by the simulation
        static readonly Dictionary<ushort, UnityEngine.Vector2> points = new Dictionary<ushort, UnityEngine.Vector2>();

        // results
        static readonly Dictionary<ushort, int> hubSlot = new Dictionary<ushort, int>(); // province -> index in hubs
        static Hub[] hubs = System.Array.Empty<Hub>();
        static readonly Dictionary<ushort, FP> capitalSupply = new Dictionary<ushort, FP>();
        // the factories behind each country's capital supply, as of the last recompute
        static readonly Dictionary<ushort, int> civ = new Dictionary<ushort, int>(), mil = new Dictionary<ushort, int>(),
                                                dock = new Dictionary<ushort, int>();
        static readonly Dictionary<ushort, List<int>> hubsByCountry = new Dictionary<ushort, List<int>>();
        static readonly WidestPathResult paths = new WidestPathResult();

        static bool loaded, available, started;
        static GameState lastState;
        static int lastControl = -1, lastOwnership = -1, lastBuildings = -1, lastUsers = -1, lastNetwork = -1;

        /// <summary>Raised by every recompute that changed a hub.</summary>
        public static int Version { get; private set; }

        /// <summary>Raised whenever a rail province starts or finishes converting (for drawing).</summary>
        public static int ConversionVersion { get; private set; }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            network = null;
            railProvinces = hubProvinces = System.Array.Empty<ushort>();
            listedHub = System.Array.Empty<bool>();
            listed.Clear(); hubListPorts = -1;
            converted = System.Array.Empty<bool>();
            converting = 0;
            points.Clear();
            ConversionVersion = 0;
            hubSlot.Clear(); hubs = System.Array.Empty<Hub>();
            capitalSupply.Clear(); hubsByCountry.Clear();
            civ.Clear(); mil.Clear(); dock.Clear();
            loaded = available = started = false;
            lastState = null;
            lastControl = lastOwnership = lastBuildings = lastUsers = lastNetwork = -1;
            Version = 0;
        }

        public static bool Available => EnsureLoaded();

        // --- queries ------------------------------------------------------------------------------------

        /// <summary>The rail network (levels per link). Read only outside this class: changes go through it.</summary>
        public static ProvinceLinkNetwork Network => EnsureLoaded() ? network : null;

        /// <summary>The rail level between two provinces (0: no rail link).</summary>
        public static int RailLevel(ushort a, ushort b) => EnsureLoaded() ? network.Level(a, b) : 0;

        /// <summary>The best rail level through a province (0: no railway).</summary>
        public static int RailLevel(ushort province) => EnsureLoaded() ? network.BestLevel(province) : 0;

        /// <summary>Every province that is or can be a hub (supply_nodes.txt, every capital, every naval base), sorted.</summary>
        public static IReadOnlyList<ushort> HubProvinces => EnsureLoaded() ? hubProvinces : (IReadOnlyList<ushort>)System.Array.Empty<ushort>();

        /// <summary>The hub in this province as of the last recompute (false when there is none: a capital-only province
        /// that isn't its controller's capital has none).</summary>
        public static bool TryGetHub(ushort province, out Hub hub)
        {
            if (EnsureLoaded() && hubSlot.TryGetValue(province, out int i) && i < hubs.Length && hubs[i].Country != 0)
            { hub = hubs[i]; return true; }
            hub = default;
            return false;
        }

        /// <summary>The hubs a country controls, in province order (connected or not).</summary>
        public static IEnumerable<Hub> HubsOf(ushort country)
        {
            if (!hubsByCountry.TryGetValue(country, out var list)) yield break;
            foreach (int i in list) yield return hubs[i];
        }

        /// <summary>The country's capital supply (0 when it doesn't hold its capital).</summary>
        public static FP CapitalSupply(ushort country) => capitalSupply.TryGetValue(country, out FP v) ? v : FP.Zero;

        /// <summary>What the country's capital supply is made of (HOI4's capital hub tooltip): the base, and the
        /// shares from its civilian factories, military factories and dockyards.</summary>
        public static void CapitalSupplyParts(ushort country, out FP fromBase, out FP fromCivilian, out FP fromMilitary, out FP fromDockyards)
        {
            civ.TryGetValue(country, out int nc);
            mil.TryGetValue(country, out int nm);
            dock.TryGetValue(country, out int nd);
            fromBase = CapitalSupplyBase;
            fromCivilian = CapitalPerCivilian * FP.FromInt(nc);
            fromMilitary = CapitalPerMilitary * FP.FromInt(nm);
            fromDockyards = CapitalPerDockyard * FP.FromInt(nd);
        }

        /// <summary>Where rail runs through the province, map px (y down): its HOI4 supply_node spot. Presentation only.</summary>
        public static bool TryGetRailPoint(ushort province, out UnityEngine.Vector2 point)
        {
            point = default;
            return EnsureLoaded() && points.TryGetValue(province, out point);
        }

        /// <summary>The province's controller can't use its rail yet (as of the last daily check).</summary>
        public static bool IsConverting(ushort province) => province < converted.Length && !converted[province];

        /// <summary>Rail provinces still converting for their controller, as of the last daily check.</summary>
        public static int Converting => converting;

        /// <summary>The first game day (days since the start) the province's controller can use its rail; 0 when it
        /// already can.</summary>
        public static int ReadyDay(GameState gameState, ushort province)
        {
            ulong ready = ReadyTick(gameState, province, Hoi4Occupation.ControllerOf(gameState, province));
            return ready <= gameState.Time.CurrentTick ? 0 : (int)((ready + 23) / 24);
        }

        // --- simulation ---------------------------------------------------------------------------------

        /// <summary>From Hoi4Simulation once a day: which rail provinces are converted, then the hub connections when
        /// anything they depend on changed.</summary>
        public static void SimulateDay(GameState gameState)
        {
            if (gameState == null || !EnsureLoaded()) return;
            var provinces = gameState.Provinces;
            Hoi4Occupation.Snapshot(gameState, converted.Length, out var ctrl, out _);
            ulong now = gameState.Time.CurrentTick;

            bool portsChanged = Hoi4Ports.Available && hubListPorts != Hoi4Ports.Version;
            if (portsChanged) BuildHubList(); // a naval base built or lost
            bool dirty = !started || lastState != gameState || portsChanged
                         || lastControl != provinces.ControlVersion || lastOwnership != provinces.OwnershipVersion
                         || lastNetwork != network.Version;
            started = true;
            lastState = gameState;
            lastControl = provinces.ControlVersion;
            lastOwnership = provinces.OwnershipVersion;
            lastNetwork = network.Version;

            converting = 0;
            bool conversionChanged = false;
            for (int i = 0; i < railProvinces.Length; i++)
            {
                ushort p = railProvinces[i];
                bool ok = ReadyTick(gameState, p, p < ctrl.Length ? ctrl[p] : (ushort)0) <= now;
                if (!ok) converting++;
                if (ok == converted[p]) continue;
                converted[p] = ok;
                dirty = true;
                conversionChanged = true;
            }
            if (conversionChanged) ConversionVersion++;

            int users = Hoi4Buildings.UsersVersion(gameState);
            if (Hoi4Buildings.Version != lastBuildings || users != lastUsers) dirty = true; // factories move the capital's supply
            lastBuildings = Hoi4Buildings.Version;
            lastUsers = users;

            if (dirty) Recompute(gameState, ctrl);
        }

        /// <summary>The tick from which the controller can use the province's rail (0: always could).</summary>
        static ulong ReadyTick(GameState gameState, ushort province, ushort controller)
        {
            if (!gameState.Provinces.TryGetControlChangeTick(province, out ulong changed)) return 0;
            int days = IsCoreOf(gameState, province, controller) ? ConversionDaysCore : ConversionDays;
            return changed + (ulong)(days * 24);
        }

        static bool IsCoreOf(GameState gameState, ushort province, ushort country)
        {
            if (country == 0) return false;
            var info = Hoi4StateData.Get(Hoi4Regions.State(province));
            return info != null && info.Cores.Contains(gameState.Countries.GetCountryTag(country));
        }

        static void Recompute(GameState gameState, ushort[] ctrl)
        {
            // capital supply from the factories each country's states give it
            civ.Clear(); mil.Clear(); dock.Clear();
            foreach (var kv in Hoi4Buildings.Users(gameState))
            {
                Add(civ, kv.Value, Hoi4Buildings.Level(kv.Key, "industrial_complex"));
                Add(mil, kv.Value, Hoi4Buildings.Level(kv.Key, "arms_factory"));
                Add(dock, kv.Value, Hoi4Buildings.Level(kv.Key, "dockyard"));
            }

            // the countries holding a hub, in id order (a capital-only province counts while it is its controller's capital)
            hubsByCountry.Clear();
            var next = new Hub[hubProvinces.Length];
            for (int i = 0; i < hubProvinces.Length; i++)
            {
                ushort p = hubProvinces[i];
                ushort c = p < ctrl.Length ? ctrl[p] : (ushort)0;
                int portLevel = Hoi4Ports.Level(p);
                if (c != 0 && !listedHub[i] && portLevel == 0 && Hoi4Regions.Capital(gameState.Countries.GetCountryTag(c)) != p) c = 0;
                next[i] = new Hub { Province = p, Country = c, IsPort = !listedHub[i] && portLevel > 0 };
                if (c == 0) continue;
                if (!hubsByCountry.TryGetValue(c, out var list)) hubsByCountry[c] = list = new List<int>();
                list.Add(i);
            }
            var countries = new List<ushort>(hubsByCountry.Keys);
            countries.Sort();

            capitalSupply.Clear();
            ushort current = 0;
            System.Func<ushort, bool> usable = p => p < ctrl.Length && ctrl[p] == current && converted[p];
            foreach (ushort country in countries)
            {
                ushort capital = Hoi4Regions.Capital(gameState.Countries.GetCountryTag(country));
                if (capital == 0 || capital >= ctrl.Length || ctrl[capital] != country) continue; // capital lost: nothing connects
                civ.TryGetValue(country, out int nc);
                mil.TryGetValue(country, out int nm);
                dock.TryGetValue(country, out int nd);
                FP supply = CapitalSupplyBase + CapitalPerCivilian * FP.FromInt(nc) + CapitalPerMilitary * FP.FromInt(nm)
                            + CapitalPerDockyard * FP.FromInt(nd);
                capitalSupply[country] = supply;

                current = country;
                network.WidestPaths(capital, usable, paths); // a retaken capital's own rail must convert too
                foreach (int i in hubsByCountry[country])
                {
                    ushort p = hubProvinces[i];
                    if (p == capital)
                    {
                        next[i].IsCapital = true;
                        next[i].IsPort = false;
                        next[i].Connected = true;
                        next[i].Capacity = supply;
                        continue;
                    }
                    if (!paths.Reached(p)) continue;
                    int level = paths.Level(p);
                    next[i].Connected = true;
                    next[i].Level = level;
                    next[i].Distance = paths.Hops(p);
                    next[i].Capacity = RailwayBaseFlow + RailwayFlowPerLevel * FP.FromInt(level);
                }
                ConnectBySea(gameState, country, next, usable);
            }

            bool changed = next.Length != hubs.Length;
            for (int i = 0; !changed && i < next.Length; i++)
            {
                var a = next[i];
                var b = hubs[i];
                changed = a.Country != b.Country || a.Connected != b.Connected || a.Level != b.Level
                          || a.IsCapital != b.IsCapital || a.Capacity != b.Capacity || a.Distance != b.Distance
                          || a.IsPort != b.IsPort || a.BySea != b.BySea || a.Source != b.Source;
            }
            hubs = next;
            if (changed) Version++;
        }

        static readonly List<int> seaSources = new List<int>(), seaRoots = new List<int>();

        /// <summary>The throughput a naval base of this level can take or give by sea.</summary>
        public static FP PortFlow(int level) => NavalBaseFlow + NavalFlowPerLevel * FP.FromInt(level);

        /// <summary>The country's ports no rail reached, by sea. Rail comes first, as in HOI4 (its tooltip for Toulon:
        /// one transfer to Bizerte, with Algiers and Oran fed from Bizerte by rail): each round opens one sea route
        /// and connects everything that has rail to the port it reaches before the next route is chosen. A port's
        /// route comes from the connected port with the shortest way by sea (Hoi4Ports.SailingDistance) among those
        /// that carry at least half of what the best one could: Dakar hangs on Casablanca, not on bigger Bizerte far
        /// away, and a small port next door doesn't throttle a big one. The round's route is the one
        /// that carries the most, then the shortest, then the lower province. So a rail network across
        /// the sea gets one route, not one per port.</summary>
        static void ConnectBySea(GameState gameState, ushort country, Hub[] next, System.Func<ushort, bool> usable)
        {
            if (!Hoi4Ports.Available) return;
            var mine = hubsByCountry[country];
            seaSources.Clear();
            seaRoots.Clear(); // here: the ports still waiting
            foreach (int i in mine)
            {
                if (Hoi4Ports.Level(hubProvinces[i]) <= 0) continue;
                if (next[i].Connected) seaSources.Add(i); else seaRoots.Add(i);
            }
            while (seaRoots.Count > 0 && seaSources.Count > 0)
            {
                int best = -1, bestSource = -1;
                FP bestFlow = FP.Zero;
                long bestDist = 0;
                foreach (int i in seaRoots) // province order
                {
                    if (next[i].Connected) continue; // rail reached it in an earlier round
                    ushort p = hubProvinces[i];
                    FP own = PortFlow(Hoi4Ports.Level(p));
                    // the most any source can give it, then the source nearest by sea giving at least half of it
                    FP most = FP.Zero;
                    bool any = false;
                    foreach (int s in seaSources)
                    {
                        ushort sp = hubProvinces[s];
                        FP flow = FP.Min(next[s].Capacity, FP.Min(PortFlow(Hoi4Ports.Level(sp)), own));
                        if ((any && flow <= most) || !Hoi4Ports.SameWater(gameState, sp, p)) continue;
                        most = flow; any = true;
                    }
                    if (!any) continue;
                    int source = -1;
                    FP sourceFlow = FP.Zero;
                    long sourceDist = 0;
                    foreach (int s in seaSources)
                    {
                        ushort sp = hubProvinces[s];
                        FP flow = FP.Min(next[s].Capacity, FP.Min(PortFlow(Hoi4Ports.Level(sp)), own));
                        if (flow * 2 < most) continue;
                        long dist = Hoi4Ports.SailingDistance(gameState, sp, p);
                        if (dist < 0) continue;
                        if (source >= 0 && (dist > sourceDist || (dist == sourceDist && sp >= hubProvinces[source]))) continue;
                        source = s; sourceFlow = flow; sourceDist = dist;
                    }
                    if (best >= 0 && (sourceFlow < bestFlow || (sourceFlow == bestFlow && sourceDist >= bestDist))) continue; // ties: the lower province
                    best = i; bestSource = source; bestFlow = sourceFlow; bestDist = sourceDist;
                }
                if (best < 0) return;
                next[best].Connected = true;
                next[best].BySea = true;
                next[best].Source = hubProvinces[bestSource];
                next[best].Capacity = bestFlow;
                seaSources.Add(best);
                network.WidestPaths(hubProvinces[best], usable, paths);
                foreach (int i in mine)
                {
                    ushort p = hubProvinces[i];
                    if (next[i].Connected || !paths.Reached(p)) continue;
                    int level = paths.Level(p);
                    next[i].Connected = true;
                    next[i].Level = level;
                    next[i].Distance = paths.Hops(p);
                    next[i].Source = hubProvinces[best];
                    next[i].Capacity = FP.Min(bestFlow, RailwayBaseFlow + RailwayFlowPerLevel * FP.FromInt(level));
                    if (Hoi4Ports.Level(p) > 0) seaSources.Add(i);
                }
            }
        }

        /// <summary>The hub provinces: supply_nodes.txt, every capital and every naval base, sorted. Rebuilt when a
        /// naval base appears or goes; the hubs found so far are dropped (the caller recomputes).</summary>
        static void BuildHubList()
        {
            var hubList = new SortedSet<ushort>(listed);
            foreach (var cap in Hoi4Regions.Capitals)
                if (cap.Value != 0) hubList.Add(cap.Value);
            if (Hoi4Ports.Available)
            {
                foreach (ushort p in Hoi4Ports.Ports) hubList.Add(p);
                hubListPorts = Hoi4Ports.Version;
            }
            hubProvinces = new ushort[hubList.Count];
            hubList.CopyTo(hubProvinces);
            listedHub = new bool[hubProvinces.Length];
            hubSlot.Clear();
            int size = converted.Length;
            for (int i = 0; i < hubProvinces.Length; i++)
            {
                hubSlot[hubProvinces[i]] = i;
                listedHub[i] = listed.Contains(hubProvinces[i]);
                size = System.Math.Max(size, hubProvinces[i] + 1);
            }
            hubs = System.Array.Empty<Hub>();
            hubsByCountry.Clear();

            var rail = new SortedSet<ushort>(hubProvinces);
            foreach (ushort p in network.Provinces()) rail.Add(p);
            railProvinces = new ushort[rail.Count];
            rail.CopyTo(railProvinces);
            if (converted.Length < size)
            {
                int old = converted.Length;
                System.Array.Resize(ref converted, size);
                for (int i = old; i < size; i++) converted[i] = true;
            }
        }

        static void Add(Dictionary<ushort, int> d, ushort key, int n)
        {
            if (n == 0) return;
            d.TryGetValue(key, out int have);
            d[key] = have + n;
        }

        // --- loading ------------------------------------------------------------------------------------

        static bool EnsureLoaded()
        {
            if (loaded) return available;
            if (!DataFileResolver.IsInitialized || !Hoi4Regions.Available) return false; // try again on the next call
            loaded = true;
            string path = DataFileResolver.Resolve(RailwaysFile);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4Railways: {path} not found (rerun hoi4_convert.py); no railways or supply hubs", "map_initialization");
                return available = false;
            }
            try
            {
                JObject root = Json5Loader.LoadJson5File(path);
                network = new ProvinceLinkNetwork(MaxLevel);
                int size = 1;
                if (root["links"] is JArray links)
                    foreach (var t in links)
                    {
                        if (!(t is JArray l) || l.Count < 3) continue;
                        ushort a = (ushort)(int)l[0], b = (ushort)(int)l[1];
                        if (network.SetLevel(a, b, System.Math.Max(1, (int)l[2])) >= 0)
                            size = System.Math.Max(size, System.Math.Max(a, b) + 1);
                    }
                if (root["hubs"] is JObject hubObj)
                    foreach (var h in hubObj.Properties()) listed.Add(ushort.Parse(h.Name));

                if (root["points"] is JObject pts)
                    foreach (var pt in pts.Properties())
                        if (pt.Value is JArray xy && xy.Count == 2)
                            points[ushort.Parse(pt.Name)] = new UnityEngine.Vector2((float)xy[0], (float)xy[1]);

                converted = new bool[size];
                for (int i = 0; i < converted.Length; i++) converted[i] = true;
                BuildHubList();
                lastNetwork = network.Version;
                ArchonLogger.Log($"Hoi4Railways: {network.LinkCount} rail links, {listed.Count} supply hubs, {hubProvinces.Length - listed.Count} capitals and naval bases besides", "map_initialization");
                return available = true;
            }
            catch (System.Exception e)
            {
                ArchonLogger.LogError($"Hoi4Railways: failed to read {path}: {e.Message}", "map_initialization");
                return available = false;
            }
        }
    }
}
