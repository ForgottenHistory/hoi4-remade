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
    /// GAME: HOI4 railways and supply hubs, and which hubs are connected to their country's capital (no supply flow
    /// yet, no ports, no rivers as links).
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
            /// <summary>Supply the hub can pass on: the capital's supply for the capital, else 10 + 5 * Level; 0 when not connected.</summary>
            public FP Capacity;
        }

        static ProvinceLinkNetwork network;
        // provinces with a rail link or a hub, sorted (the only ones whose conversion matters)
        static ushort[] railProvinces = System.Array.Empty<ushort>();
        static ushort[] hubProvinces = System.Array.Empty<ushort>(); // sorted: supply_nodes.txt hubs and every capital
        static bool[] listedHub = System.Array.Empty<bool>();        // per hubProvinces entry: in supply_nodes.txt

        // conversion as of the last daily check, by province id (true: its controller can use its rail)
        static bool[] converted = System.Array.Empty<bool>();
        static int converting;

        // where rail runs through each province, map px (y down): presentation only, never read by the simulation
        static readonly Dictionary<ushort, UnityEngine.Vector2> points = new Dictionary<ushort, UnityEngine.Vector2>();

        // results
        static readonly Dictionary<ushort, int> hubSlot = new Dictionary<ushort, int>(); // province -> index in hubs
        static Hub[] hubs = System.Array.Empty<Hub>();
        static readonly Dictionary<ushort, FP> capitalSupply = new Dictionary<ushort, FP>();
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
            converted = System.Array.Empty<bool>();
            converting = 0;
            points.Clear();
            ConversionVersion = 0;
            hubSlot.Clear(); hubs = System.Array.Empty<Hub>();
            capitalSupply.Clear(); hubsByCountry.Clear();
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

        /// <summary>Every province that is or can be a hub (supply_nodes.txt and every capital), sorted.</summary>
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

            bool dirty = !started || lastState != gameState
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
            var civ = new Dictionary<ushort, int>();
            var mil = new Dictionary<ushort, int>();
            var dock = new Dictionary<ushort, int>();
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
                if (c != 0 && !listedHub[i] && Hoi4Regions.Capital(gameState.Countries.GetCountryTag(c)) != p) c = 0;
                next[i] = new Hub { Province = p, Country = c };
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
            }

            bool changed = next.Length != hubs.Length;
            for (int i = 0; !changed && i < next.Length; i++)
            {
                var a = next[i];
                var b = hubs[i];
                changed = a.Country != b.Country || a.Connected != b.Connected || a.Level != b.Level
                          || a.IsCapital != b.IsCapital || a.Capacity != b.Capacity || a.Distance != b.Distance;
            }
            hubs = next;
            if (changed) Version++;
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
                var hubList = new List<ushort>();
                if (root["hubs"] is JObject hubObj)
                    foreach (var h in hubObj.Properties()) hubList.Add(ushort.Parse(h.Name));
                var listed = new HashSet<ushort>(hubList);
                foreach (var cap in Hoi4Regions.Capitals)
                    if (cap.Value != 0 && !listed.Contains(cap.Value) && !hubList.Contains(cap.Value)) hubList.Add(cap.Value);
                hubList.Sort();
                hubProvinces = hubList.ToArray();
                listedHub = new bool[hubProvinces.Length];
                for (int i = 0; i < hubProvinces.Length; i++)
                {
                    hubSlot[hubProvinces[i]] = i;
                    listedHub[i] = listed.Contains(hubProvinces[i]);
                    size = System.Math.Max(size, hubProvinces[i] + 1);
                }

                if (root["points"] is JObject pts)
                    foreach (var pt in pts.Properties())
                        if (pt.Value is JArray xy && xy.Count == 2)
                            points[ushort.Parse(pt.Name)] = new UnityEngine.Vector2((float)xy[0], (float)xy[1]);

                var rail = new SortedSet<ushort>(hubProvinces);
                foreach (ushort p in network.Provinces()) rail.Add(p);
                railProvinces = new ushort[rail.Count];
                rail.CopyTo(railProvinces);

                converted = new bool[size];
                for (int i = 0; i < converted.Length; i++) converted[i] = true;
                lastNetwork = network.Version;
                ArchonLogger.Log($"Hoi4Railways: {network.LinkCount} rail links, {listed.Count} supply hubs, {hubProvinces.Length - listed.Count} capital-only", "map_initialization");
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
