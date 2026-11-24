using System.Collections.Generic;
using Core;
using Hoi4.Construction;
using Hoi4.Map;
using Hoi4.Units;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Supply
{
    /// <summary>
    /// GAME: HOI4 supply in every land province and each division's supply ratio (no ports, convoys, air supply,
    /// weather, motorization or allies' hubs yet; nothing reads the ratio yet).
    /// Prototypes: Tools/prototypes/supply/supply_flow.py (province supply, checked against HOI4 tooltips in the
    /// Ruhr) and supply_demand.py (allocation). Numbers are NSupply defines unless said otherwise.
    ///
    /// HUB SUPPLY. Every connected hub (Hoi4Railways: the capital and the hubs with a rail path to it) sends supply
    /// over the land provinces its controller controls, losing some on every step:
    ///     start   = INITIAL + NODE_FLOW_BONUS_PER_RAIL_LEVEL (0.34) * rail level
    ///               (capital 5.0, at its best rail level; hub 2.8, at its bottleneck level to the capital)
    ///     step d  = (STARTING (0.5) + ADDED (capital 1.2, hub 0.7) * falloff(d) + terrain penalty of the province
    ///               entered) * (1 - 0.3 * infrastructure of the state left / 5) + 0.2 over a river
    ///     falloff = the defines' logistics curve: max(0.25, 0.9 / (1 + e^(-1.3 (d - 2.3)))), a table below
    /// It stops once below SUPPLY_FLOW_REDUCTION_THRESHOLD (0.1). A province's hub supply is the sum of its three
    /// best sources (HOI4's tooltip lists three flow sources; the sum matched Dortmund's 7.06 where the best source
    /// alone gives 3.1). Each step's formula is our reading of the defines, fitted to those tooltips.
    ///
    /// STATE SUPPLY. A pool per state: 0.3 per infrastructure level + 0.18 per million people + per victory point
    /// 0.2 + 0.05 x its value + local_supplies x 0.2, rounded to 0.1 (matched HOI4's tooltips exactly). A state split
    /// between controllers splits all but the VPs by provinces held; a VP's share goes to whoever holds it.
    ///
    /// DIVISIONS. Each needs its template's supply use (Hoi4Division.SupplyUse; at least MIN_SUPPLY_CONSUMPTION).
    /// Daily: the state pool goes first, evenly over the controller's divisions in the state (none takes more than
    /// it needs); then each province's remaining need draws its hub supply, split over its sources by their flow; a
    /// source asked for more than its throughput (Hoi4Railways.Hub.Capacity) scales down what everyone draws from
    /// it. Target ratio = what it got / what it needs. A division's Ratio climbs toward the target by at most
    /// RatioGainPerDay and falls to it at once (HOI4's divisions buffer supply; observed about +3 points a day).
    /// Divisions at sea, or in a province their country doesn't control, keep their ratio.
    ///
    /// Cost: province supply is recomputed only when control, buildings or hubs changed, and then only for the
    /// countries whose provinces, hubs or infrastructure changed (a province only gets its controller's flow): the
    /// first time all 622 hubs (36 ms), in a war the warring countries' hubs. The allocation runs daily over the
    /// divisions (about 0.6 ms). Deterministic: fixed point, province and unit id order.
    /// </summary>
    public static class Hoi4Supply
    {
        public const int Sources = 3;                                     // hub sources summed per province
        static readonly FP CapitalInitial = FP.FromInt(5);                // CAPITAL_INITIAL_SUPPLY_FLOW
        static readonly FP CapitalStarting = FP.FromFraction(5, 10);      // CAPITAL_STARTING_PENALTY_PER_PROVINCE
        static readonly FP CapitalAdded = FP.FromFraction(12, 10);        // CAPITAL_ADDED_PENALTY_PER_PROVINCE
        static readonly FP NodeInitial = FP.FromFraction(28, 10);         // NODE_INITIAL_SUPPLY_FLOW
        static readonly FP NodeStarting = FP.FromFraction(5, 10);         // NODE_STARTING_PENALTY_PER_PROVINCE
        static readonly FP NodeAdded = FP.FromFraction(7, 10);            // NODE_ADDED_PENALTY_PER_PROVINCE
        static readonly FP BonusPerRailLevel = FP.FromFraction(34, 100);  // NODE_FLOW_BONUS_PER_RAIL_LEVEL
        static readonly FP InfraReduction = FP.FromFraction(3, 10);       // SUPPLY_FLOW_DROP_REDUCTION_AT_MAX_INFRA
        static readonly FP RiverPenalty = FP.FromFraction(2, 10);         // SUPPLY_FLOW_PENALTY_CROSSING_RIVERS
        static readonly FP Threshold = FP.FromFraction(1, 10);            // SUPPLY_FLOW_REDUCTION_THRESHOLD
        static readonly FP InfraToSupply = FP.FromFraction(3, 10);        // INFRA_TO_SUPPLY
        static readonly FP PerMillionPeople = FP.FromFraction(18, 100);   // AVAILABLE_MANPOWER_STATE_SUPPLY
        static readonly FP VpBase = FP.FromFraction(2, 10);               // VP_TO_SUPPLY_BASE
        static readonly FP VpPerPoint = FP.FromFraction(5, 100);          // VP_TO_SUPPLY_BONUS_CONVERSION
        static readonly FP LocalMult = FP.FromFraction(2, 10);            // SUPPLY_BASE_MULT
        static readonly FP MinNeed = FP.FromFraction(5, 100);             // NMilitary.MIN_SUPPLY_CONSUMPTION
        public static readonly FP RatioGainPerDay = FP.FromFraction(3, 100); // observed in HOI4 (72 -> 91 in a week)
        static readonly FP FullTolerance = FP.FromFraction(1, 1000);
        const int MaxInfra = 5;

        // the logistics curve at steps 1, 2, 3...: max(0.25, 0.9 / (1 + e^(-1.3 (d - 2.3)))) in ten-thousandths
        // (SUPPLY_FLOW_DIST_LOGISTICS_FALLOFF_*), as a table so no floating point enters the simulation
        static readonly int[] FalloffTenThousandths = { 2500, 3633, 6417, 8110, 8739, 8927, 8980, 8995, 8999, 9000 };
        static FP[] falloff;

        // land graph (compact: neighbours of p at nbr[off[p]..off[p+1]]), impassable and water left out
        static int[] off;
        static ushort[] nbr;
        static int size;

        // per province: its best Sources sources, flow descending (slot p * Sources + k)
        static FP[] topFlow;
        static ushort[] topSource;
        static FP[] hubSupply;

        // state pools: (state << 16 | controller) -> supply
        static readonly Dictionary<long, FP> statePool = new Dictionary<long, FP>();
        // source province -> throughput
        static readonly Dictionary<ushort, FP> capacity = new Dictionary<ushort, FP>();
        static readonly List<ushort> sourceOrder = new List<ushort>();

        // divisions: unit id -> ratio now, and the last allocation's target and draw
        static readonly Dictionary<ushort, FP> ratio = new Dictionary<ushort, FP>();
        static readonly Dictionary<ushort, FP> target = new Dictionary<ushort, FP>();
        static readonly Dictionary<ushort, FP> sourceDraw = new Dictionary<ushort, FP>();
        static readonly Dictionary<ushort, CountrySupply> byCountry = new Dictionary<ushort, CountrySupply>();

        // search scratch
        static long[] best;
        static int[] hops, stamp, settled;
        static int currentStamp;
        static readonly List<(long flow, ushort p)> heap = new List<(long, ushort)>();

        static bool loaded, available, flowsReady;
        static GameState lastState;
        static int lastControl = -1, lastRail = -1, lastBuildings = -1;

        /// <summary>Hub searches the last recompute ran (all connected hubs at first, then only changed countries').</summary>
        public static int LastSearched { get; private set; }

        /// <summary>Raised whenever province supply was recomputed.</summary>
        public static int FlowVersion { get; private set; }

        /// <summary>One country's divisions after the last daily allocation.</summary>
        public struct CountrySupply
        {
            public int Divisions;
            public FP Need, FromState, FromHubs;
            /// <summary>Divisions at full supply, and under half.</summary>
            public int Full, UnderHalf;
            public FP RatioSum;
        }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            loaded = available = flowsReady = false;
            lastState = null;
            lastControl = lastRail = lastBuildings = -1;
            off = null; nbr = null; size = 0;
            topFlow = null; topSource = null; hubSupply = null;
            statePool.Clear(); capacity.Clear(); sourceOrder.Clear();
            prevCtrl = null; prevInfra = null; prevStart.Clear(); dirty.Clear();
            ratio.Clear(); target.Clear(); sourceDraw.Clear(); byCountry.Clear();
            best = null; hops = stamp = settled = null; currentStamp = 0;
            heap.Clear();
            FlowVersion = 0;
        }

        // --- queries ------------------------------------------------------------------------------------

        /// <summary>Hub supply in the province (sum of its best three sources), as of the last recompute.</summary>
        public static FP HubSupply(ushort province) =>
            hubSupply != null && province < hubSupply.Length ? hubSupply[province] : FP.Zero;

        /// <summary>The k-th best hub source of the province (0: the best) and its flow there; false past the last.</summary>
        public static bool TryGetSource(ushort province, int k, out ushort source, out FP flow)
        {
            source = 0; flow = FP.Zero;
            if (topSource == null || province >= size || k < 0 || k >= Sources) return false;
            source = topSource[province * Sources + k];
            flow = topFlow[province * Sources + k];
            return source != 0;
        }

        /// <summary>The supply pool of the province's state for its controller (state supply), as of the last recompute.</summary>
        public static FP StateSupply(GameState gameState, ushort province)
        {
            ushort c = Hoi4Occupation.ControllerOf(gameState, province);
            return statePool.TryGetValue(PoolKey(Hoi4Regions.State(province), c), out FP v) ? v : FP.Zero;
        }

        /// <summary>The division's supply ratio (1: fully supplied). 1 for a division never allocated.</summary>
        public static FP Ratio(ushort unitId) => ratio.TryGetValue(unitId, out FP r) ? r : FP.One;

        /// <summary>The ratio the division's supply moves toward (what it got today / what it needs).</summary>
        public static FP TargetRatio(ushort unitId) => target.TryGetValue(unitId, out FP r) ? r : Ratio(unitId);

        /// <summary>What the hub's provinces drew from it today, against its throughput.</summary>
        public static FP Drawn(ushort hubProvince) => sourceDraw.TryGetValue(hubProvince, out FP v) ? v : FP.Zero;

        public static bool TryGetCountry(ushort country, out CountrySupply s) => byCountry.TryGetValue(country, out s);

        public static IEnumerable<KeyValuePair<ushort, CountrySupply>> Countries => byCountry;

        // --- simulation ---------------------------------------------------------------------------------

        /// <summary>From Hoi4Simulation once a day, after Hoi4Railways: province supply when its inputs changed, then
        /// every division's share.</summary>
        public static void SimulateDay(GameState gameState, Hoi4Divisions divisions)
        {
            if (gameState == null || divisions == null || !EnsureLoaded(gameState) || !Hoi4Railways.Available) return;
            Hoi4Occupation.Snapshot(gameState, size, out var ctrl, out _);
            var provinces = gameState.Provinces;
            if (!flowsReady || lastState != gameState || lastControl != provinces.ControlVersion
                || lastRail != Hoi4Railways.Version || lastBuildings != Hoi4Buildings.Version)
            {
                if (lastState != gameState) prevCtrl = null; // a new game: search everything
                lastState = gameState;
                lastControl = provinces.ControlVersion;
                lastRail = Hoi4Railways.Version;
                lastBuildings = Hoi4Buildings.Version;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                RecomputeFlows(gameState, ctrl, out int searched);
                RecomputePools(gameState, ctrl);
                LastSearched = searched;
                if (FlowVersion == 0)
                {
                    int supplied = 0;
                    for (int p = 0; p < size; p++) if (hubSupply[p] > FP.Zero) supplied++;
                    // prototype (supply_flow.py) at the 1936 start: Dortmund hub 495 hub 7.46, Westfalen state 2.60
                    ArchonLogger.Log($"Hoi4Supply: {sourceOrder.Count} hub sources, hub supply in {supplied} provinces, "
                                     + $"province 495 hub {HubSupply(495).ToDouble():0.00} state {StateSupply(gameState, 495).ToDouble():0.0} ({sw.ElapsedMilliseconds} ms)", "game_units");
                }
                flowsReady = true;
                FlowVersion++;
            }
            Allocate(gameState, divisions, ctrl);
        }

        // what the last recompute saw: each province's controller, each state's infrastructure, each source's start
        static ushort[] prevCtrl;
        static int[] prevInfra;
        static readonly Dictionary<ushort, long> prevStart = new Dictionary<ushort, long>(); // source -> start raw (0: none)
        static readonly HashSet<ushort> dirty = new HashSet<ushort>();

        /// <summary>A province only ever gets flow from its controller's hubs, so only the countries whose provinces,
        /// hubs or infrastructure changed are searched again; their old and new provinces are cleared first.</summary>
        static void RecomputeFlows(GameState gameState, ushort[] ctrl, out int searched)
        {
            bool full = prevCtrl == null;
            dirty.Clear();
            if (!full)
            {
                for (int p = 1; p < size; p++)
                    if (ctrl[p] != prevCtrl[p]) { dirty.Add(ctrl[p]); dirty.Add(prevCtrl[p]); }
                foreach (int state in Hoi4Regions.States)
                {
                    if (state >= prevInfra.Length || Hoi4Buildings.Level(state, "infrastructure") == prevInfra[state]) continue;
                    foreach (ushort p in Hoi4Regions.ProvincesOf(state)) if (p < size) dirty.Add(ctrl[p]);
                }
            }

            // the sources now; a source whose start changed (connected, cut, new level, new owner) dirties its country
            capacity.Clear();
            sourceOrder.Clear();
            var seen = new HashSet<ushort>();
            foreach (ushort p in Hoi4Railways.HubProvinces) // sorted
            {
                if (!Hoi4Railways.TryGetHub(p, out var hub) || !hub.Connected || p >= size) continue;
                FP start = hub.IsCapital
                    ? CapitalInitial + BonusPerRailLevel * FP.FromInt(Hoi4Railways.RailLevel(p))
                    : NodeInitial + BonusPerRailLevel * FP.FromInt(hub.Level);
                capacity[p] = hub.Capacity;
                sourceOrder.Add(p);
                seen.Add(p);
                long sig = start.RawValue ^ ((long)hub.Country << 48) ^ (hub.IsCapital ? 1L << 47 : 0);
                if (!prevStart.TryGetValue(p, out long before) || before != sig) { dirty.Add(hub.Country); prevStart[p] = sig; }
            }
            var lost = new List<ushort>();
            foreach (var kv in prevStart) if (!seen.Contains(kv.Key)) lost.Add(kv.Key);
            foreach (ushort p in lost) { prevStart.Remove(p); dirty.Add(p < prevCtrl?.Length ? prevCtrl[p] : (ushort)0); dirty.Add(ctrl[p]); }
            dirty.Remove(0);

            if (full)
            {
                System.Array.Clear(topFlow, 0, topFlow.Length);
                System.Array.Clear(topSource, 0, topSource.Length);
            }
            else
                for (int p = 1; p < size; p++)
                {
                    if (!dirty.Contains(ctrl[p]) && !dirty.Contains(prevCtrl[p])) continue;
                    for (int k = 0; k < Sources; k++) { topFlow[p * Sources + k] = FP.Zero; topSource[p * Sources + k] = 0; }
                }

            searched = 0;
            foreach (ushort p in sourceOrder)
            {
                Hoi4Railways.TryGetHub(p, out var hub);
                if (!full && !dirty.Contains(hub.Country)) continue;
                searched++;
                FP start = hub.IsCapital
                    ? CapitalInitial + BonusPerRailLevel * FP.FromInt(Hoi4Railways.RailLevel(p))
                    : NodeInitial + BonusPerRailLevel * FP.FromInt(hub.Level);
                Spread(gameState, ctrl, p, hub.Country, start, hub.IsCapital ? CapitalStarting : NodeStarting,
                       hub.IsCapital ? CapitalAdded : NodeAdded);
            }
            for (int p = 0; p < size; p++)
            {
                FP sum = FP.Zero;
                for (int k = 0; k < Sources; k++) sum += topFlow[p * Sources + k];
                hubSupply[p] = sum;
            }

            if (prevCtrl == null) prevCtrl = new ushort[size];
            System.Array.Copy(ctrl, prevCtrl, System.Math.Min(size, ctrl.Length));
            int maxState = 0;
            foreach (int s in Hoi4Regions.States) maxState = System.Math.Max(maxState, s);
            if (prevInfra == null || prevInfra.Length <= maxState) prevInfra = new int[maxState + 1];
            foreach (int s in Hoi4Regions.States) prevInfra[s] = Hoi4Buildings.Level(s, "infrastructure");
        }

        /// <summary>Best remaining flow from the source to every province its country controls. Flow only falls (every
        /// step costs at least 0.5 x 0.7 - 0.2 > 0), so taking the province with the most flow left first is exact.</summary>
        static void Spread(GameState gameState, ushort[] ctrl, ushort source, ushort country, FP start, FP starting, FP added)
        {
            currentStamp++;
            heap.Clear();
            best[source] = start.RawValue;
            hops[source] = 0;
            stamp[source] = currentStamp;
            Push(start.RawValue, source);
            while (heap.Count > 0)
            {
                var (flowRaw, u) = Pop();
                if (settled[u] == currentStamp || flowRaw != best[u]) continue; // settled, or a stale entry
                settled[u] = currentStamp;
                int d = hops[u];
                Offer(u, FP.FromRaw(flowRaw), source);
                FP infra = FP.FromInt(System.Math.Min(MaxInfra, Hoi4Buildings.Level(Hoi4Regions.State(u), "infrastructure")));
                FP keep = FP.One - InfraReduction * infra / MaxInfra;
                FP stepBase = starting + added * falloff[System.Math.Min(d + 1, falloff.Length - 1)]; // the step to d + 1
                for (int i = off[u]; i < off[u + 1]; i++)
                {
                    ushort v = nbr[i];
                    if (v >= ctrl.Length || ctrl[v] != country || settled[v] == currentStamp) continue;
                    FP pen = (stepBase + Hoi4CombatTerrain.SupplyFlowPenalty(gameState, v)) * keep;
                    if (Hoi4CombatTerrain.River(gameState, u, v) > 0) pen += RiverPenalty;
                    FP nf = FP.FromRaw(flowRaw) - pen;
                    if (nf < Threshold) continue;
                    if (stamp[v] != currentStamp || nf.RawValue > best[v]
                        || (nf.RawValue == best[v] && hops[v] > d + 1))
                    {
                        stamp[v] = currentStamp;
                        best[v] = nf.RawValue;
                        hops[v] = d + 1;
                        Push(nf.RawValue, v);
                    }
                }
            }
        }

        /// <summary>Keep the source among the province's best Sources (flow descending, ties to the lower province id).</summary>
        static void Offer(ushort province, FP flow, ushort source)
        {
            int b = province * Sources;
            int at = Sources;
            for (int k = 0; k < Sources; k++)
            {
                ushort s = topSource[b + k];
                if (s == 0 || flow > topFlow[b + k] || (flow == topFlow[b + k] && source < s)) { at = k; break; }
            }
            if (at == Sources) return;
            for (int k = Sources - 1; k > at; k--)
            {
                topFlow[b + k] = topFlow[b + k - 1];
                topSource[b + k] = topSource[b + k - 1];
            }
            topFlow[b + at] = flow;
            topSource[b + at] = source;
        }

        // max-heap on flow, ties to the lower province id
        static bool Before((long flow, ushort p) a, (long flow, ushort p) b) => a.flow > b.flow || (a.flow == b.flow && a.p < b.p);

        static void Push(long flow, ushort p)
        {
            heap.Add((flow, p));
            int i = heap.Count - 1;
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!Before(heap[i], heap[parent])) break;
                (heap[i], heap[parent]) = (heap[parent], heap[i]);
                i = parent;
            }
        }

        static (long, ushort) Pop()
        {
            var top = heap[0];
            heap[0] = heap[heap.Count - 1];
            heap.RemoveAt(heap.Count - 1);
            int i = 0;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, m = i;
                if (l < heap.Count && Before(heap[l], heap[m])) m = l;
                if (r < heap.Count && Before(heap[r], heap[m])) m = r;
                if (m == i) break;
                (heap[i], heap[m]) = (heap[m], heap[i]);
                i = m;
            }
            return top;
        }

        static long PoolKey(int state, ushort controller) => ((long)state << 16) | controller;

        static void RecomputePools(GameState gameState, ushort[] ctrl)
        {
            statePool.Clear();
            var held = new Dictionary<ushort, int>();
            var raw = new Dictionary<ushort, FP>(); // this state's controllers -> unrounded share
            foreach (int state in Hoi4Regions.States)
            {
                raw.Clear();
                if (Hoi4Regions.IsImpassableState(state)) continue;
                var info = Hoi4StateData.Get(state);
                var provs = Hoi4Regions.ProvincesOf(state);
                if (info == null || provs.Count == 0) continue;
                FP shared = InfraToSupply * FP.FromInt(Hoi4Buildings.Level(state, "infrastructure"))
                            + PerMillionPeople * FP.FromInt(info.Manpower) / 1000000
                            + LocalMult * FP.FromDouble(info.LocalSupplies);
                held.Clear();
                foreach (ushort p in provs)
                {
                    ushort c = p < ctrl.Length ? ctrl[p] : (ushort)0;
                    if (c == 0) continue;
                    held.TryGetValue(c, out int n);
                    held[c] = n + 1;
                }
                FP total = shared;
                foreach (var kv in held)
                    Add(raw, kv.Key, shared * kv.Value / provs.Count);
                foreach (var vp in info.VictoryPoints)
                {
                    FP v = VpBase + VpPerPoint * FP.FromInt(vp.Value);
                    total += v;
                    ushort c = vp.Key >= 0 && vp.Key < ctrl.Length ? ctrl[vp.Key] : (ushort)0;
                    if (c != 0) Add(raw, c, v);
                }
                // HOI4 rounds the state's supply to 0.1; a split state's shares are scaled from the rounded total (a
                // small share rounded on its own would vanish)
                if (total <= FP.Zero) continue;
                FP scale = FP.Round(total * 10) / 10 / total;
                foreach (var kv in raw) statePool[PoolKey(state, kv.Key)] = kv.Value * scale;
            }
        }

        static void Add(Dictionary<ushort, FP> d, ushort key, FP v)
        {
            d.TryGetValue(key, out FP have);
            d[key] = have + v;
        }

        // allocation scratch
        sealed class Slot { public ushort Unit, Country, Province; public int State; public FP Need, FromState, FromHubs; }
        static readonly List<Slot> slots = new List<Slot>();
        static readonly Dictionary<long, List<Slot>> byPool = new Dictionary<long, List<Slot>>();
        static readonly Dictionary<ushort, List<Slot>> byProvince = new Dictionary<ushort, List<Slot>>();

        static void Allocate(GameState gameState, Hoi4Divisions divisions, ushort[] ctrl)
        {
            slots.Clear();
            byPool.Clear();
            byProvince.Clear();
            var units = gameState.Units;
            var ids = new List<ushort>(divisions.All.Keys);
            ids.Sort();
            foreach (ushort id in ids)
            {
                var d = divisions.All[id];
                if (!units.HasUnit(id)) continue;
                var u = units.GetUnit(id);
                ushort p = u.provinceID;
                if (p >= size || Hoi4Regions.IsWater(p) || ctrl[p] != u.countryID) continue; // at sea / not its ground: keeps its ratio
                var s = new Slot { Unit = id, Country = u.countryID, Province = p, State = Hoi4Regions.State(p),
                                   Need = FP.Max(MinNeed, d.SupplyUse) };
                slots.Add(s);
                long key = PoolKey(s.State, s.Country);
                if (!byPool.TryGetValue(key, out var list)) byPool[key] = list = new List<Slot>();
                list.Add(s);
                if (!byProvince.TryGetValue(p, out var pl)) byProvince[p] = pl = new List<Slot>();
                pl.Add(s);
            }

            // 1. state pools, evenly: the smallest needs are filled first, the rest share what is left
            var sorted = new List<Slot>();
            var poolKeys = new List<long>(byPool.Keys);
            poolKeys.Sort();
            foreach (long key in poolKeys)
            {
                FP pool = statePool.TryGetValue(key, out FP v) ? v : FP.Zero;
                sorted.Clear();
                sorted.AddRange(byPool[key]);
                sorted.Sort((a, b) => a.Need != b.Need ? a.Need.CompareTo(b.Need) : a.Unit.CompareTo(b.Unit));
                for (int i = 0; i < sorted.Count && pool > FP.Zero; i++)
                {
                    FP share = pool / (sorted.Count - i);
                    if (sorted[i].Need <= share)
                    {
                        sorted[i].FromState = sorted[i].Need;
                        pool -= sorted[i].Need;
                    }
                    else
                    {
                        for (int j = i; j < sorted.Count; j++) sorted[j].FromState = share;
                        pool = FP.Zero;
                    }
                }
            }

            // 2. hub supply: each province draws its remaining need, split over its sources by flow
            sourceDraw.Clear();
            var provKeys = new List<ushort>(byProvince.Keys);
            provKeys.Sort();
            var take = new Dictionary<ushort, FP>(); // province -> what it takes before the throughput caps
            foreach (ushort p in provKeys)
            {
                FP want = FP.Zero;
                foreach (var s in byProvince[p]) want += s.Need - s.FromState;
                FP avail = hubSupply[p];
                if (want <= FP.Zero || avail <= FP.Zero) continue;
                FP t = FP.Min(want, avail);
                take[p] = t;
                for (int k = 0; k < Sources; k++)
                {
                    ushort src = topSource[p * Sources + k];
                    if (src == 0) break;
                    sourceDraw.TryGetValue(src, out FP have);
                    sourceDraw[src] = have + t * topFlow[p * Sources + k] / avail;
                }
            }
            foreach (ushort p in provKeys)
            {
                if (!take.TryGetValue(p, out FP t)) continue;
                FP avail = hubSupply[p], got = FP.Zero;
                for (int k = 0; k < Sources; k++)
                {
                    ushort src = topSource[p * Sources + k];
                    if (src == 0) break;
                    FP part = t * topFlow[p * Sources + k] / avail;
                    FP drawn = sourceDraw[src];
                    FP cap = capacity.TryGetValue(src, out FP c) ? c : FP.Zero;
                    got += drawn > cap && drawn > FP.Zero ? part * cap / drawn : part;
                }
                FP want = FP.Zero;
                foreach (var s in byProvince[p]) want += s.Need - s.FromState;
                foreach (var s in byProvince[p]) s.FromHubs = got * (s.Need - s.FromState) / want;
            }

            // 3. ratios
            byCountry.Clear();
            foreach (var s in slots)
            {
                FP got = s.FromState + s.FromHubs;
                FP goal = got + FullTolerance >= s.Need ? FP.One : got / s.Need; // fixed-point shares round a hair short
                target[s.Unit] = goal;
                FP now = ratio.TryGetValue(s.Unit, out FP r) ? r : goal; // a new division starts at what it gets
                now = goal < now ? goal : FP.Min(goal, now + RatioGainPerDay);
                ratio[s.Unit] = now;
                byCountry.TryGetValue(s.Country, out var cs);
                cs.Divisions++;
                cs.Need += s.Need;
                cs.FromState += s.FromState;
                cs.FromHubs += s.FromHubs;
                if (now >= FP.One) cs.Full++;
                if (now < FP.Half) cs.UnderHalf++;
                cs.RatioSum += now;
                byCountry[s.Country] = cs;
            }
            // forget divisions that are gone
            if (ratio.Count > divisions.All.Count)
            {
                var gone = new List<ushort>();
                foreach (ushort id in ratio.Keys) if (!divisions.All.ContainsKey(id)) gone.Add(id);
                foreach (ushort id in gone) { ratio.Remove(id); target.Remove(id); }
            }
        }

        // --- loading ------------------------------------------------------------------------------------

        static bool EnsureLoaded(GameState gameState)
        {
            if (loaded) return available;
            if (gameState.Adjacencies == null || !Hoi4Regions.Available || !Hoi4StateData.Available) return false;
            loaded = true;
            gameState.Adjacencies.GetCompactGraph(out int[] allOff, out ushort[] allNbr);
            size = allOff.Length - 1;
            var o = new int[size + 1];
            var n = new List<ushort>(allNbr.Length);
            for (int p = 0; p < size; p++)
            {
                o[p] = n.Count;
                if (p == 0 || Hoi4Regions.IsWater((ushort)p) || Hoi4Regions.IsImpassable((ushort)p)) continue;
                for (int i = allOff[p]; i < allOff[p + 1]; i++)
                {
                    ushort q = allNbr[i];
                    if (!Hoi4Regions.IsWater(q) && !Hoi4Regions.IsImpassable(q)) n.Add(q);
                }
            }
            o[size] = n.Count;
            off = o;
            nbr = n.ToArray();
            topFlow = new FP[size * Sources];
            topSource = new ushort[size * Sources];
            hubSupply = new FP[size];
            best = new long[size];
            hops = new int[size];
            stamp = new int[size];
            settled = new int[size];
            falloff = new FP[FalloffTenThousandths.Length + 1]; // falloff[d]: the step to distance d (d >= 1)
            falloff[0] = FP.FromFraction(FalloffTenThousandths[0], 10000);
            for (int i = 0; i < FalloffTenThousandths.Length; i++) falloff[i + 1] = FP.FromFraction(FalloffTenThousandths[i], 10000);
            ArchonLogger.Log($"Hoi4Supply: land graph of {size} provinces, {nbr.Length} links", "map_initialization");
            return available = true;
        }
    }
}
