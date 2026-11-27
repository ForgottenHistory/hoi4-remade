using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Core;
using Core.Loaders;
using Core.Modding;
using Hoi4.Map;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

using Hoi4.Modifiers;

namespace Hoi4.Construction
{
    /// <summary>One construction order: Count levels of a building in a state, built one after another (HOI4: queueing
    /// the same building in the same state again adds a level to its order).</summary>
    public sealed class ConstructionItem
    {
        public int Id;               // unique within the country
        public int State;
        public string Building;      // industrial_complex, arms_factory, infrastructure
        public int Count = 1;        // levels left to build, the one in progress included
        public FP Cost;              // of the level in progress: base_cost + per_level_extra_cost x the state's level
        public FP Progress;          // towards the level in progress
        public int Factories;        // civilian factories on it today
        public FP SpeedToday;        // IC a day it gets today
        public ushort Province;      // province buildings (naval_base, supply_node): where; 0 for a state building
        public List<ushort> Path;    // rail_way: the provinces the railway runs through, in order (null otherwise)
        public int RailLevel;        // rail_way: the level every link of the path is raised to
    }

    /// <summary>
    /// GAME: HOI4 construction (wiki "Construction", NDefines.NProduction / NBuildings).
    /// - Civilian factories: a state's industrial_complex levels count for the country its buildings work for
    ///   (Hoi4Buildings: owner holding more than half of it). Consumer goods take floor((civilian + military) x the
    ///   economy law's share x (1 + consumer goods factor)), at least 10% and 1 (MINIMUM_NUMBER_OF_FACTORIES_TAKEN_BY_
    ///   CONSUMER_GOODS_*); the rest are available. Checked against real HOI4: Germany 15 of 63, 20 available; France
    ///   14 of 41. Laws and factors per country from common/hoi4_country_economy.json5 (its 1936 ideas and techs).
    /// - Queue: per country, in order; an order holds one or more levels of one building in one state and builds them
    ///   one at a time. Each order takes up to 15 available civilian factories (MAX_CIV_FACTORIES_PER_LINE)
    ///   until they run out; each gives 5 IC a day (POWERED_FACTORY_SPEED) x (1 + construction speed + the building's
    ///   own speed) x (1 + state infrastructure / 5) for buildings infrastructure speeds up
    ///   (INFRA_MAX_CONSTRUCTION_COST_EFFECT 1, infrastructure max 5). A level is done when its progress reaches the
    ///   cost (civilian factory 10800, military factory 7200, infrastructure 6000); the state gets the level.
    /// - Buildings: civilian and military factories, infrastructure, air bases, anti-air, radar, dockyards (coastal
    ///   states only), synthetic refineries and fuel silos. Technology-gated ones (HOI4 enable_building: anti-air,
    ///   radar, refinery, fuel silo) need the country's 1936 technologies and go only as high as they allow.
    /// - Limits: shared buildings (factories) need a free shared slot (state category plus extra slots from history,
    ///   at most 25, counting what is queued); state buildings up to their own cap (infrastructure 5, air base 10,
    ///   anti-air 5, radar 6), air bases lower on islands (large 6, small 4, tiny 2: state category caps). Only in states whose buildings work for the country; an item in a state the
    ///   country loses is dropped.
    /// - Province buildings (supply): a naval base (Hoi4Ports: a coastal province, up to level 10, less on islands
    ///   by state category), a supply hub (one per province, not where a hub or naval base stands) and railways. A
    ///   railway order is a line from junction to junction (NormalizeRail lays the orders out again whenever track
    ///   is ordered) and the level its links are raised to: where the whole path already
    ///   has rail, one above its lowest link; else level 1 (new track, whatever it branches off).
    ///   Its "levels" are the single link levels still to build (170 + 130 x the link's level each), built in path
    ///   order, so the track grows link by link. The country must control the provinces; an order whose province
    ///   it loses is dropped. Finished levels go to Hoi4Ports and Hoi4Railways.
    /// - Not modelled: trade, the international market, repairs, conversion, forts, other state buildings, slot
    ///   bonuses from technology, law changes during the game.
    /// Changes come only through the commands (Hoi4ConstructionCommands); the AI (Hoi4ConstructionAI) uses the same ones.
    /// </summary>
    public sealed class Hoi4Construction : MonoBehaviour
    {
        const string EconomyFile = "common/hoi4_country_economy.json5";

        public static readonly FP CivilianOutput = FP.FromInt(5);                  // POWERED_FACTORY_SPEED
        public const int MaxFactoriesPerItem = 15;                                  // MAX_CIV_FACTORIES_PER_LINE
        static readonly FP MinConsumerGoodsShare = FP.FromFraction(1, 10);         // ..._CONSUMER_GOODS_PERCENT
        const int MinConsumerGoods = 1;                                             // ..._CONSUMER_GOODS_VALUE
        static readonly FP CivilianEconomy = FP.FromFraction(35, 100);             // civilian_economy's expected share
        public const int MaxInfrastructure = 5;
        public const int MaxQueue = 200;
        const int ReportEveryDays = 7;

        /// <summary>The buildings construction can build here: the factories and the state buildings.</summary>
        public static readonly string[] Buildable =
        {
            "industrial_complex", "arms_factory", "infrastructure", "air_base", "anti_air_building", "radar_station",
            "dockyard", "synthetic_refinery", "fuel_silo",
        };

        /// <summary>The province buildings construction can build: the supply network.</summary>
        public const string NavalBase = "naval_base", SupplyNode = "supply_node", Railway = "rail_way";
        public static bool IsProvinceBuilding(string building) => building == NavalBase || building == SupplyNode || building == Railway;

        /// <summary>Counts every change to any queue and every finished level (presentation caches).</summary>
        public static int Version { get; private set; }

        /// <summary>Most levels the country's technologies allow of the building: int.MaxValue when no technology
        /// gates it, 0 while it is not researched (HOI4 greys the building out), else the highest level unlocked
        /// (radar 1..6 by technology).</summary>
        public int TechCap(ushort country, string building) =>
            Hoi4.Research.Hoi4Research.Instance != null ? Hoi4.Research.Hoi4Research.Instance.BuildingLevel(country, building) : int.MaxValue;

        /// <summary>Whether the country can build it at all: researched, and one of Buildable.</summary>
        public bool Researched(ushort country, string building) =>
            IsProvinceBuilding(building) || (System.Array.IndexOf(Buildable, building) >= 0 && TechCap(country, building) > 0);

        public static Hoi4Construction Instance { get; private set; }

        /// <summary>A country's laws at the start. Consumer goods and construction speed come from the modifier system
        /// (Hoi4Modifiers), building levels from its technologies (Hoi4Research).</summary>
        public sealed class Economy
        {
            public readonly List<string> Laws = new List<string>();
        }

        sealed class CountryConstruction
        {
            public readonly List<ConstructionItem> Queue = new List<ConstructionItem>();
            public int NextId = 1;
            public int Built;
        }

        GameState gameState;
        readonly Dictionary<ushort, Economy> economies = new Dictionary<ushort, Economy>();
        readonly Dictionary<string, ushort> buildingSpeedIds = new Dictionary<string, ushort>(); // production_speed_[b]_factor
        ushort consumerGoodsId, consumerGoodsFactorId, constructionSpeedId;
        readonly SortedDictionary<ushort, CountryConstruction> countries = new SortedDictionary<ushort, CountryConstruction>();
        readonly Dictionary<ushort, int> civilian = new Dictionary<ushort, int>();
        readonly Dictionary<ushort, int> military = new Dictionary<ushort, int>();
        int countedUsers = int.MinValue, countedBuildings = -1;
        int day;

        public bool IsReady { get; private set; }

        /// <summary>Raised after a command changed a country's queue or a construction finished (UI refresh).</summary>
        public event System.Action<ushort> QueueChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Construction");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Construction>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Provinces == null || !DataFileResolver.IsInitialized
                   || !Hoi4Buildings.Available
                   // the countries must exist before their tags resolve (the divisions load after them)
                   || Hoi4.Units.Hoi4Divisions.Instance == null || !Hoi4.Units.Hoi4Divisions.Instance.IsLoaded
                   // consumer goods and speed: the modifier system, filled by research (ideas and technologies)
                   || Hoi4.Research.Hoi4Research.Instance == null || !Hoi4.Research.Hoi4Research.Instance.IsReady)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            consumerGoodsId = Hoi4Modifiers.Id(Hoi4Modifiers.ConsumerGoods);
            consumerGoodsFactorId = Hoi4Modifiers.Id(Hoi4Modifiers.ConsumerGoodsFactor);
            constructionSpeedId = Hoi4Modifiers.Id(Hoi4Modifiers.ConstructionSpeed);

            string path = DataFileResolver.Resolve(EconomyFile);
            if (File.Exists(path))
                foreach (var p in Json5Loader.LoadJson5File(path).Properties())
                {
                    ushort country = gameState.Countries.GetCountryIdFromTag(p.Name);
                    if (country == 0 || !(p.Value is JObject o)) continue;
                    var e = new Economy();
                    if (o["laws"] is JArray laws) foreach (var l in laws) e.Laws.Add((string)l);
                    economies[country] = e;
                }
            else
                ArchonLogger.LogWarning($"Hoi4Construction: no {EconomyFile} (run Tools/hoi4_convert.py); default civilian economy", "game_construction");

            if (economies.Count == 0)
                ArchonLogger.LogWarning($"Hoi4Construction: no country economies resolved from {EconomyFile}; every country uses the civilian economy default", "game_construction");
            IsReady = true;
            Count();
            int civ = 0;
            foreach (int n in civilian.Values) civ += n;
            ArchonLogger.Log($"Hoi4Construction: {economies.Count} economies, {civ} civilian factories", "game_construction");
        }

        // ---- queries ------------------------------------------------------------------------------------

        public Economy EconomyOf(ushort country)
        {
            if (!economies.TryGetValue(country, out var e)) economies[country] = e = new Economy();
            return e;
        }

        void Count()
        {
            if (!IsReady || !Hoi4Buildings.Available) return;
            int users = Hoi4Buildings.UsersVersion(gameState);
            if (countedUsers == users && countedBuildings == Hoi4Buildings.Version) return;
            countedUsers = users;
            countedBuildings = Hoi4Buildings.Version;
            civilian.Clear();
            military.Clear();
            foreach (var kv in Hoi4Buildings.Users(gameState))
            {
                int civ = Hoi4Buildings.Level(kv.Key, "industrial_complex");
                int mil = Hoi4Buildings.Level(kv.Key, "arms_factory");
                if (civ > 0) { civilian.TryGetValue(kv.Value, out int c); civilian[kv.Value] = c + civ; }
                if (mil > 0) { military.TryGetValue(kv.Value, out int m); military[kv.Value] = m + mil; }
            }
        }

        public int Civilian(ushort country) { Count(); return civilian.TryGetValue(country, out int n) ? n : 0; }
        public int Military(ushort country) { Count(); return military.TryGetValue(country, out int n) ? n : 0; }

        /// <summary>Civilian factories on consumer goods: floor((civilian + military) x share x (1 + factor)), at least
        /// 10% and 1, at most all civilian factories.</summary>
        public int ConsumerGoods(ushort country)
        {
            int civ = Civilian(country);
            if (civ == 0) return 0;
            // the economy law's share x every other idea's (1 + factor); a country without ideas: civilian economy
            FP expected = Hoi4Modifiers.HasCountry(country) ? Hoi4Modifiers.Sum(country, consumerGoodsId) : CivilianEconomy;
            FP share = FP.Max(MinConsumerGoodsShare, expected * Hoi4Modifiers.Product(country, consumerGoodsFactorId));
            int total = civ + Military(country);
            int cg = FP.Floor(share * total).ToInt();
            return System.Math.Min(civ, System.Math.Max(MinConsumerGoods, cg));
        }

        /// <summary>Construction speed: production_speed_buildings_factor of the country's ideas and technologies.</summary>
        public FP ConstructionSpeed(ushort country) => Hoi4Modifiers.Sum(country, constructionSpeedId);

        /// <summary>Civilian factories free for construction.</summary>
        public int Available(ushort country) => System.Math.Max(0, Civilian(country) - ConsumerGoods(country));

        public IReadOnlyList<ConstructionItem> Queue(ushort country) =>
            countries.TryGetValue(country, out var c) ? c.Queue : (IReadOnlyList<ConstructionItem>)System.Array.Empty<ConstructionItem>();

        public ConstructionItem Item(ushort country, int id)
        {
            if (!countries.TryGetValue(country, out var c)) return null;
            foreach (var i in c.Queue) if (i.Id == id) return i;
            return null;
        }

        /// <summary>Levels of a building already queued in a state (by any country: a state has one user); with
        /// sharedOnly, the levels of every shared building there.</summary>
        public int Queued(int state, string building, bool sharedOnly = false)
        {
            int n = 0;
            foreach (var c in countries.Values)
                foreach (var i in c.Queue)
                {
                    if (i.State != state || i.Province != 0 || i.Path != null) continue;
                    if (sharedOnly ? (Hoi4StateData.Def(i.Building)?.Shared ?? false) : i.Building == building) n += i.Count;
                }
            return n;
        }

        /// <summary>Most levels the state can hold of the building, 0 where it can't be built (a coastal building
        /// inland). Shared: the state's shared slots less the other shared buildings there, at most the building's own
        /// cap (refinery 3, dockyard 20). Others: their cap (infrastructure 5, air base 10, anti-air 5, radar 6), air
        /// bases lower on islands. Both at most what the technologies of the country using the state allow.</summary>
        public bool CanHold(int state, string building) => MaxLevel(state, building) > 0;

        public int MaxLevel(int state, string building)
        {
            var def = Hoi4StateData.Def(building);
            var info = Hoi4StateData.Get(state);
            if (def == null || info == null) return 0;
            if (def.Coastal && !info.Coastal) return 0;
            int max;
            if (def.Shared)
            {
                max = Hoi4Buildings.SharedSlots(state) - Hoi4Buildings.SharedUsed(state) + Hoi4Buildings.Level(state, building);
                if (def.StateMax > 0) max = System.Math.Min(max, def.StateMax);
            }
            else
            {
                int cap = Hoi4StateData.CategoryCap(info.Category, building);
                max = cap > 0 ? System.Math.Min(cap, def.StateMax) : def.StateMax;
            }
            ushort user = IsReady ? Hoi4Buildings.UserOf(gameState, state) : (ushort)0;
            if (user != 0) max = System.Math.Min(max, TechCap(user, building));
            return max;
        }

        /// <summary>Shared slots of the state still free, counting what is queued there.</summary>
        public int FreeSharedSlots(int state) =>
            Hoi4Buildings.SharedSlots(state) - Hoi4Buildings.SharedUsed(state) - Queued(state, null, true);

        /// <summary>Whether the country may queue one more level of the building in the state.</summary>
        public bool CanQueue(ushort country, int state, string building)
        {
            if (!IsReady || country == 0 || System.Array.IndexOf(Buildable, building) < 0) return false;
            if (Hoi4Buildings.UserOf(gameState, state) != country) return false;
            if (countries.TryGetValue(country, out var c) && c.Queue.Count >= MaxQueue) return false;
            var def = Hoi4StateData.Def(building);
            if (def == null || def.Cost <= 0 || !Researched(country, building)) return false;
            return Room(state, building) > 0;
        }

        /// <summary>IC a day one civilian factory gives an item: base x (1 + construction speed + the building's
        /// speed) x (1 + infrastructure / 5) when infrastructure speeds the building up.</summary>
        public FP SpeedPerFactory(ushort country, int state, string building)
        {
            if (!buildingSpeedIds.TryGetValue(building, out ushort ownId))
                buildingSpeedIds[building] = ownId = Hoi4Modifiers.BuildingSpeedId(building);
            FP own = Hoi4Modifiers.Sum(country, ownId);
            FP speed = CivilianOutput * FP.Max(FP.Zero, FP.One + ConstructionSpeed(country) + own);
            var def = Hoi4StateData.Def(building);
            if (def != null && def.InfraSpeed)
                speed = speed * (FP.One + FP.FromInt(Hoi4Buildings.Level(state, "infrastructure")) / MaxInfrastructure);
            return speed;
        }

        // ---- daily --------------------------------------------------------------------------------------

        /// <summary>Daily, from Hoi4Simulation before production (a military factory built today works tomorrow's
        /// lines): civilian factories to the queue in order, progress, finished levels to the states.</summary>
        public void SimulateDay()
        {
            if (!IsReady) return;
            day++;
            foreach (var kv in countries)
            {
                ushort country = kv.Key;
                var queue = kv.Value.Queue;
                int left = Available(country);
                bool changed = false;
                for (int k = 0; k < queue.Count; k++)
                {
                    var i = queue[k];
                    i.Factories = 0;
                    i.SpeedToday = FP.Zero;
                    if (i.Province != 0 || i.Path != null ? !Holds(country, i) : Hoi4Buildings.UserOf(gameState, i.State) != country)
                    {
                        Core.Recording.SessionRecorder.Note("construction", "dropped", country, 0, 0, 0,
                            $"{i.Building} in state {i.State}: the state is no longer ours");
                        queue.RemoveAt(k--);
                        changed = true;
                        continue;
                    }
                    if (left <= 0) continue;
                    i.Factories = System.Math.Min(MaxFactoriesPerItem, left);
                    left -= i.Factories;
                    i.SpeedToday = SpeedPerFactory(country, i.State, i.Building) * i.Factories;
                    i.Progress += i.SpeedToday;
                    if (i.Progress < i.Cost) continue;
                    kv.Value.Built++;
                    changed = true;
                    if (i.Province != 0 || i.Path != null)
                    {
                        FinishProvinceLevel(country, i);
                        if (i.Count > 0) { i.Progress = FP.Zero; continue; }
                        queue.RemoveAt(k--);
                        continue;
                    }
                    Hoi4Buildings.Add(i.State, i.Building, 1);
                    Core.Recording.SessionRecorder.Note("construction", "done", country, 0, 0, 0,
                        $"{i.Building} in state {i.State} (now {Hoi4Buildings.Level(i.State, i.Building)})");
                    if (--i.Count > 0)
                    {
                        i.Progress = FP.Zero; // the order's next level
                        i.Cost = LevelCost(i.State, i.Building, Hoi4Buildings.Level(i.State, i.Building));
                        continue;
                    }
                    queue.RemoveAt(k--);
                }
                if (changed) { Version++; QueueChanged?.Invoke(country); }
            }
            if (day % ReportEveryDays == 0) Report();
        }

        /// <summary>Weekly note per country with civilian factories: "civ 35 mil 28 cg 15 free 20 | queue 2 | built 1".</summary>
        void Report()
        {
            foreach (var kv in economies)
            {
                int civ = Civilian(kv.Key);
                if (civ == 0) continue;
                countries.TryGetValue(kv.Key, out var c);
                if (c == null && Military(kv.Key) == 0) continue;
                var sb = new StringBuilder($"civ {civ} mil {Military(kv.Key)} cg {ConsumerGoods(kv.Key)} free {Available(kv.Key)}");
                sb.Append($" | queue {c?.Queue.Count ?? 0} | built {c?.Built ?? 0}");
                if (c != null)
                    foreach (var i in c.Queue)
                        if (i.Factories > 0) sb.Append($" | {i.Building}@{i.State} {i.Factories}f {(i.Progress * 100 / i.Cost).ToInt()}%");
                Core.Recording.SessionRecorder.Note("construction", "week", kv.Key, 0, 0, 0, sb.ToString());
            }
        }

        // ---- changes (commands only) ---------------------------------------------------------------------

        static FP LevelCost(int state, string building, int level)
        {
            var def = Hoi4StateData.Def(building);
            return def == null ? FP.One : FP.FromDouble(System.Math.Max(1.0, def.Cost + def.CostPerLevel * level));
        }

        // ---- province buildings: naval bases, supply hubs, railways ------------------------------------------

        ushort Controller(ushort province) => Hoi4.Units.Hoi4Occupation.ControllerOf(gameState, province);

        /// <summary>The country still controls the order's province, or every province of its railway.</summary>
        bool Holds(ushort country, ConstructionItem i)
        {
            if (i.Path == null) return Controller(i.Province) == country;
            foreach (ushort p in i.Path) if (Controller(p) != country) return false;
            return true;
        }

        /// <summary>Levels of the province building there now (naval base 0-10, supply hub 0 or 1).</summary>
        public int ProvinceLevel(ushort province, string building) =>
            building == NavalBase ? Hoi4.Supply.Hoi4Ports.Level(province)
            : building == SupplyNode ? (Hoi4.Supply.Hoi4Railways.IsHubSite(province) ? 1 : 0) : 0;

        /// <summary>Most levels the province can hold: a naval base only on a coast (10, islands less by their state
        /// category); a supply hub on any passable land without a naval base (1).</summary>
        public int ProvinceMax(ushort province, string building)
        {
            if (province == 0 || Hoi4Regions.IsWater(province) || Hoi4Regions.IsImpassable(province)) return 0;
            if (building == NavalBase)
            {
                if (Hoi4.Supply.Hoi4Ports.Sea(province) == 0) return 0;
                var info = Hoi4StateData.Get(Hoi4Regions.State(province));
                int cap = info != null ? Hoi4StateData.CategoryCap(info.Category, NavalBase) : 0;
                return cap > 0 ? System.Math.Min(cap, Hoi4.Supply.Hoi4Ports.MaxLevel) : Hoi4.Supply.Hoi4Ports.MaxLevel;
            }
            if (building == SupplyNode) return Hoi4.Supply.Hoi4Ports.Level(province) > 0 ? 0 : 1;
            return 0;
        }

        /// <summary>Levels of the province building queued in the province (by any country).</summary>
        public int QueuedAt(ushort province, string building)
        {
            int n = 0;
            foreach (var c in countries.Values)
                foreach (var i in c.Queue)
                    if (i.Province == province && i.Building == building) n += i.Count;
            return n;
        }

        public int ProvinceRoom(ushort province, string building) =>
            System.Math.Max(0, ProvinceMax(province, building) - ProvinceLevel(province, building) - QueuedAt(province, building));

        public bool CanQueueProvince(ushort country, ushort province, string building)
        {
            if (!IsReady || country == 0 || (building != NavalBase && building != SupplyNode)) return false;
            if (Controller(province) != country) return false;
            if (countries.TryGetValue(country, out var c) && c.Queue.Count >= MaxQueue) return false;
            var def = Hoi4StateData.Def(building);
            return def != null && def.Cost > 0 && ProvinceRoom(province, building) > 0;
        }

        /// <summary>One level of a naval base or a supply hub in a province: onto the country's order for it there,
        /// else a new order at the end of the queue. Returns the order's id (0 when not allowed).</summary>
        public int EnqueueProvince(ushort country, ushort province, string building)
        {
            if (!CanQueueProvince(country, province, building)) return 0;
            if (!countries.TryGetValue(country, out var c)) countries[country] = c = new CountryConstruction();
            foreach (var o in c.Queue)
                if (o.Province == province && o.Building == building)
                {
                    o.Count++;
                    Version++;
                    QueueChanged?.Invoke(country);
                    return o.Id;
                }
            var item = new ConstructionItem
            {
                Id = c.NextId++, State = Hoi4Regions.State(province), Province = province, Building = building,
                Cost = LevelCost(0, building, ProvinceLevel(province, building)),
            };
            c.Queue.Add(item);
            Version++;
            QueueChanged?.Invoke(country);
            return item.Id;
        }

        /// <summary>The level a railway along the path would be raised to, 0 when it can't be built: fewer than two
        /// provinces, not all the country's, not each bordering the next, or nothing left to build. Where the whole
        /// path has rail: one above its lowest link (an upgrade). Else level 1: new track starts at the bottom, also
        /// off a higher line (the player raises it by its number).</summary>
        public int RailTarget(ushort country, IReadOnlyList<ushort> path)
        {
            if (!IsReady || country == 0 || path == null || path.Count < 2 || !Hoi4.Supply.Hoi4Railways.Available) return 0;
            int lowest = int.MaxValue;
            for (int k = 0; k < path.Count; k++)
            {
                ushort p = path[k];
                if (Hoi4Regions.IsWater(p) || Hoi4Regions.IsImpassable(p) || Controller(p) != country) return 0;
                if (k == 0) continue;
                if (path[k - 1] == p || !gameState.Adjacencies.IsAdjacent(path[k - 1], p)) return 0;
                lowest = System.Math.Min(lowest, Hoi4.Supply.Hoi4Railways.RailLevel(path[k - 1], p));
            }
            int target = lowest > 0 ? lowest + 1 : 1; // new track starts at level 1, whatever it branches off
            return target > Hoi4.Supply.Hoi4Railways.MaxLevel ? 0 : target;
        }

        /// <summary>Link levels a railway order still has to build, and the first link among them (false: none).</summary>
        static int RailSteps(ConstructionItem i, out ushort a, out ushort b)
        {
            a = b = 0;
            int steps = 0;
            for (int k = 1; k < i.Path.Count; k++)
            {
                int missing = i.RailLevel - Hoi4.Supply.Hoi4Railways.RailLevel(i.Path[k - 1], i.Path[k]);
                if (missing <= 0) continue;
                if (steps == 0) { a = i.Path[k - 1]; b = i.Path[k]; }
                steps += missing;
            }
            return steps;
        }

        /// <summary>A railway along the path (RailTarget's rules). The country's railway orders are then laid out
        /// again as lines (NormalizeRail), so the path may join, split or extend the orders already there. Returns
        /// 1 when something was ordered, 0 when not allowed.</summary>
        public int EnqueueRail(ushort country, List<ushort> path)
        {
            int target = RailTarget(country, path);
            if (target == 0) return 0;
            if (!countries.TryGetValue(country, out var c)) countries[country] = c = new CountryConstruction();
            if (c.Queue.Count >= MaxQueue) return 0;
            var item = new ConstructionItem
            {
                Id = c.NextId++, State = Hoi4Regions.State(path[0]), Building = Railway, Path = new List<ushort>(path), RailLevel = target,
            };
            item.Count = RailSteps(item, out _, out _);
            if (item.Count == 0) return 0;
            c.Queue.Add(item);
            NormalizeRail(c);
            Version++;
            QueueChanged?.Invoke(country);
            return 1;
        }

        /// <summary>Change the level a railway order builds its links to (the player clicks its number: up, or down
        /// with the right button). At most the rail maximum; an order left with nothing to build is taken off.</summary>
        public bool SetRailOrderLevel(ushort country, int id, int level)
        {
            var item = Item(country, id);
            if (item == null || item.Path == null || level == item.RailLevel || level > Hoi4.Supply.Hoi4Railways.MaxLevel) return false;
            if (!countries.TryGetValue(country, out var c)) return false;
            item.RailLevel = level;
            NormalizeRail(c);
            Version++;
            QueueChanged?.Invoke(country);
            return true;
        }

        static uint LinkKey(ushort a, ushort b) => a < b ? (uint)a << 16 | b : (uint)b << 16 | a;

        sealed class PendingLink { public ushort A, B; public int Level, Order, Id; public FP Progress; public bool Used; }

        /// <summary>Lay the country's railway orders out again as HOI4's lines. Every link still to build (below its
        /// order's level; where orders overlap, the highest level) goes into one pool; the pool is cut into lines
        /// that run from junction to junction: a line ends where it doesn't simply continue (not exactly two links
        /// to build meet), where the level changes, where new track meets an upgrade, and where new track touches
        /// built rail (it joins the network there). So pieces laid end to end are one order, a piece meeting an
        /// order in its middle makes three, and track ordered twice is ordered once.
        /// Each line is one order. It takes the queue position of the earliest old order it has a link of, and
        /// the id and progress of an old order whose first link to build lies on it. A line
        /// starts at its end on the network when it has one, so it grows outwards from the rail that exists.
        /// Deterministic: links by province pair, lines started from the lowest province.</summary>
        void NormalizeRail(CountryConstruction c)
        {
            var links = new SortedDictionary<uint, PendingLink>();
            for (int q = 0; q < c.Queue.Count; q++)
            {
                var o = c.Queue[q];
                if (o.Path == null) continue;
                bool first = true;
                for (int k = 1; k < o.Path.Count; k++)
                {
                    ushort a = o.Path[k - 1], b = o.Path[k];
                    if (Hoi4.Supply.Hoi4Railways.RailLevel(a, b) >= o.RailLevel) continue;
                    uint key = LinkKey(a, b);
                    if (!links.TryGetValue(key, out var l)) links[key] = l = new PendingLink { A = a, B = b, Order = q };
                    l.Level = System.Math.Max(l.Level, o.RailLevel);
                    if (first && l.Id == 0) { l.Id = o.Id; l.Progress = o.Progress; } // the link an order is working on
                    first = false;
                }
            }
            var at = new SortedDictionary<ushort, List<PendingLink>>();
            foreach (var l in links.Values)
            {
                if (!at.TryGetValue(l.A, out var la)) at[l.A] = la = new List<PendingLink>();
                if (!at.TryGetValue(l.B, out var lb)) at[l.B] = lb = new List<PendingLink>();
                la.Add(l);
                lb.Add(l);
            }
            System.Func<PendingLink, bool> isNew = l => Hoi4.Supply.Hoi4Railways.RailLevel(l.A, l.B) == 0;
            System.Func<ushort, bool> breaks = p =>
            {
                var here = at[p];
                if (here.Count != 2 || here[0].Level != here[1].Level || isNew(here[0]) != isNew(here[1])) return true;
                return isNew(here[0]) && OnNetwork(p); // new track touching built rail, a hub or a port: a junction
            };

            var lines = new List<ConstructionItem>();
            var keys = new List<int>(); // per line: the queue position it takes (its earliest order's)
            foreach (bool loops in new[] { false, true })
                foreach (var kv in at)
                {
                    if (!loops && !breaks(kv.Key)) continue;
                    foreach (var startLink in kv.Value)
                    {
                        if (startLink.Used) continue;
                        var path = new List<ushort> { kv.Key };
                        var line = new ConstructionItem { Building = Railway, Path = path, RailLevel = startLink.Level };
                        int order = int.MaxValue;
                        ushort node = kv.Key;
                        var link = startLink;
                        while (link != null && !link.Used)
                        {
                            link.Used = true;
                            if (link.Order < order) order = link.Order;
                            if (link.Id != 0 && line.Id == 0) { line.Id = link.Id; line.Progress = link.Progress; }
                            node = link.A == node ? link.B : link.A;
                            path.Add(node);
                            if (breaks(node)) break;
                            var here = at[node];
                            link = here[0] == link ? here[1] : here[0];
                        }
                        // build outwards from the network: start at the end that touches it
                        if (!OnNetwork(path[0]) && OnNetwork(path[path.Count - 1])) path.Reverse();
                        lines.Add(line);
                        keys.Add(order);
                    }
                }

            // the old railway orders go; each line takes the place of the order it continues, new lines the end
            var taken = new HashSet<int>();
            var queue = new List<ConstructionItem>();
            for (int q = 0; q < c.Queue.Count; q++)
            {
                if (c.Queue[q].Path == null) { queue.Add(c.Queue[q]); continue; }
                for (int i = 0; i < lines.Count; i++)
                    if (keys[i] == q) queue.Add(Finish(c, lines[i], taken));
            }
            c.Queue.Clear();
            c.Queue.AddRange(queue);
        }

        /// <summary>Built rail, a supply hub or a naval base in the province: track here is on the network.</summary>
        static bool OnNetwork(ushort province) =>
            Hoi4.Supply.Hoi4Railways.RailLevel(province) > 0 || Hoi4.Supply.Hoi4Railways.IsHubSite(province)
            || Hoi4.Supply.Hoi4Ports.Level(province) > 0;

        /// <summary>A line's order, ready for the queue: its own id (an old order's only once), state, levels to
        /// build and the cost of the first.</summary>
        static ConstructionItem Finish(CountryConstruction c, ConstructionItem line, HashSet<int> taken)
        {
            if (line.Id == 0 || !taken.Add(line.Id)) { line.Id = c.NextId++; line.Progress = FP.Zero; }
            line.State = Hoi4Regions.State(line.Path[0]);
            line.Count = RailSteps(line, out ushort a, out ushort b);
            line.Cost = LevelCost(0, Railway, Hoi4.Supply.Hoi4Railways.RailLevel(a, b));
            return line;
        }

        /// <summary>Every rail link the country's orders are still building: (lower province &lt;&lt; 16 | higher).</summary>
        public void RailLinksQueued(ushort country, HashSet<uint> into)
        {
            if (!countries.TryGetValue(country, out var c)) return;
            foreach (var i in c.Queue)
            {
                if (i.Path == null) continue;
                for (int k = 1; k < i.Path.Count; k++)
                {
                    ushort a = i.Path[k - 1], b = i.Path[k];
                    if (Hoi4.Supply.Hoi4Railways.RailLevel(a, b) < i.RailLevel) into.Add(a < b ? (uint)a << 16 | b : (uint)b << 16 | a);
                }
            }
        }

        /// <summary>A level of a province order is done: the port grows, the hub stands, or the order's next rail
        /// link gains a level. Sets what is left of the order (Count) and the next level's cost.</summary>
        void FinishProvinceLevel(ushort country, ConstructionItem i)
        {
            if (i.Path != null)
            {
                if (RailSteps(i, out ushort a, out ushort b) > 0)
                    Hoi4.Supply.Hoi4Railways.SetRailLevel(a, b, Hoi4.Supply.Hoi4Railways.RailLevel(a, b) + 1);
                i.Count = RailSteps(i, out a, out b);
                if (i.Count > 0) i.Cost = LevelCost(0, Railway, Hoi4.Supply.Hoi4Railways.RailLevel(a, b));
                Core.Recording.SessionRecorder.Note("construction", "done", country, 0, i.Path[0], i.Path[i.Path.Count - 1],
                    $"rail level {i.RailLevel}: {i.Count} link levels left");
                return;
            }
            if (i.Building == NavalBase) Hoi4.Supply.Hoi4Ports.SetLevel(i.Province, Hoi4.Supply.Hoi4Ports.Level(i.Province) + 1);
            else if (i.Building == SupplyNode) Hoi4.Supply.Hoi4Railways.AddHub(i.Province);
            i.Count--;
            if (i.Count > 0) i.Cost = LevelCost(0, i.Building, ProvinceLevel(i.Province, i.Building));
            Core.Recording.SessionRecorder.Note("construction", "done", country, 0, i.Province, 0,
                $"{i.Building} (now {ProvinceLevel(i.Province, i.Building)})");
        }

        /// <summary>More levels the order's place can still take (the + on an order).</summary>
        public int RoomOf(ConstructionItem i) =>
            i.Path != null ? 0 : i.Province != 0 ? ProvinceRoom(i.Province, i.Building) : Room(i.State, i.Building);

        /// <summary>One level: onto the country's order for the building in the state when it has one, else a new
        /// order at the end of the queue. Returns the order's id (0 when not allowed).</summary>
        public int Enqueue(ushort country, int state, string building)
        {
            if (!CanQueue(country, state, building)) return 0;
            if (!countries.TryGetValue(country, out var c)) countries[country] = c = new CountryConstruction();
            Version++;
            foreach (var o in c.Queue)
                if (o.State == state && o.Building == building && o.Province == 0 && o.Path == null)
                {
                    o.Count++;
                    QueueChanged?.Invoke(country);
                    return o.Id;
                }
            var item = new ConstructionItem
            {
                Id = c.NextId++, State = state, Building = building,
                Cost = LevelCost(state, building, Hoi4Buildings.Level(state, building)),
            };
            c.Queue.Add(item);
            QueueChanged?.Invoke(country);
            return item.Id;
        }

        /// <summary>More levels of the building the state can still take, counting what is built and queued there
        /// (for a shared building also the free shared slots).</summary>
        public int Room(int state, string building)
        {
            int room = MaxLevel(state, building) - Hoi4Buildings.Level(state, building) - Queued(state, building);
            var def = Hoi4StateData.Def(building);
            if (def != null && def.Shared) room = System.Math.Min(room, FreeSharedSlots(state));
            return System.Math.Max(0, room);
        }

        /// <summary>Set an order's levels: at least 1, and more only while the state has room.</summary>
        public bool SetCount(ushort country, int id, int count)
        {
            var item = Item(country, id);
            if (item == null || count < 1 || item.Path != null) return false; // a railway is its path: no levels to set
            if (count > item.Count)
                count = item.Count + System.Math.Max(0, System.Math.Min(RoomOf(item), count - item.Count));
            if (count == item.Count) return false;
            item.Count = count;
            Version++;
            QueueChanged?.Invoke(country);
            return true;
        }

        public bool Remove(ushort country, int id)
        {
            if (!countries.TryGetValue(country, out var c)) return false;
            int i = c.Queue.FindIndex(x => x.Id == id);
            if (i < 0) return false;
            bool rail = c.Queue[i].Path != null;
            c.Queue.RemoveAt(i);
            if (rail) NormalizeRail(c); // a junction may be gone: its lines join
            Version++;
            QueueChanged?.Invoke(country);
            return true;
        }

        /// <summary>Move an item to a position in the queue (0 = top: gets civilian factories first).</summary>
        public bool Move(ushort country, int id, int position)
        {
            if (!countries.TryGetValue(country, out var c)) return false;
            int i = c.Queue.FindIndex(x => x.Id == id);
            if (i < 0) return false;
            var item = c.Queue[i];
            c.Queue.RemoveAt(i);
            c.Queue.Insert(System.Math.Max(0, System.Math.Min(c.Queue.Count, position)), item);
            QueueChanged?.Invoke(country);
            return true;
        }

        static FP Num(JToken t) => t != null && t.Type != JTokenType.Null ? FP.FromDouble((double)t) : FP.Zero;
    }
}
