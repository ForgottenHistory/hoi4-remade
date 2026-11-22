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
    /// - Limits: shared buildings (factories) need a free shared slot (state category, at most 25, counting what is
    ///   queued); infrastructure up to 5. Only in states whose buildings work for the country; an item in a state the
    ///   country loses is dropped.
    /// - Not modelled: trade, the international market, repairs, conversion, province buildings (forts, naval bases,
    ///   railways), other state buildings, slot bonuses from technology, law changes during the game.
    /// Changes come only through the commands (Hoi4ConstructionCommands); the AI (Hoi4ConstructionAI) uses the same ones.
    /// </summary>
    public sealed class Hoi4Construction : MonoBehaviour
    {
        const string EconomyFile = "common/hoi4_country_economy.json5";

        public static readonly FP CivilianOutput = FP.FromInt(5);                  // POWERED_FACTORY_SPEED
        public const int MaxFactoriesPerItem = 15;                                  // MAX_CIV_FACTORIES_PER_LINE
        static readonly FP MinConsumerGoodsShare = FP.FromFraction(1, 10);         // ..._CONSUMER_GOODS_PERCENT
        const int MinConsumerGoods = 1;                                             // ..._CONSUMER_GOODS_VALUE
        public const int MaxInfrastructure = 5;
        public const int MaxQueue = 200;
        const int ReportEveryDays = 7;

        /// <summary>The buildings construction can build here.</summary>
        public static readonly string[] Buildable = { "industrial_complex", "arms_factory", "infrastructure" };

        public static Hoi4Construction Instance { get; private set; }

        /// <summary>A country's economy at the start: consumer goods and construction speed.</summary>
        public sealed class Economy
        {
            public FP ConsumerGoods;        // the economy law's share
            public FP ConsumerGoodsFactor;  // other ideas, stacked
            public FP Speed;                // construction speed
            public readonly Dictionary<string, FP> BuildingSpeed = new Dictionary<string, FP>();
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
                   || Hoi4.Units.Hoi4Divisions.Instance == null || !Hoi4.Units.Hoi4Divisions.Instance.IsLoaded)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;

            string path = DataFileResolver.Resolve(EconomyFile);
            if (File.Exists(path))
                foreach (var p in Json5Loader.LoadJson5File(path).Properties())
                {
                    ushort country = gameState.Countries.GetCountryIdFromTag(p.Name);
                    if (country == 0 || !(p.Value is JObject o)) continue;
                    var e = new Economy
                    {
                        ConsumerGoods = Num(o["consumer_goods"]),
                        ConsumerGoodsFactor = Num(o["consumer_goods_factor"]),
                        Speed = Num(o["construction_speed"]),
                    };
                    if (o["speed"] is JObject s) foreach (var b in s.Properties()) e.BuildingSpeed[b.Name] = Num(b.Value);
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
            if (!economies.TryGetValue(country, out var e))
                economies[country] = e = new Economy { ConsumerGoods = FP.FromFraction(35, 100) }; // civilian economy
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
            var e = EconomyOf(country);
            FP share = FP.Max(MinConsumerGoodsShare, e.ConsumerGoods * (FP.One + e.ConsumerGoodsFactor));
            int total = civ + Military(country);
            int cg = FP.Floor(share * total).ToInt();
            return System.Math.Min(civ, System.Math.Max(MinConsumerGoods, cg));
        }

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
                    if (i.State != state) continue;
                    if (sharedOnly ? (Hoi4StateData.Def(i.Building)?.Shared ?? false) : i.Building == building) n += i.Count;
                }
            return n;
        }

        /// <summary>Most levels the state can hold of the building: for a shared building the state's shared slots
        /// less the other shared buildings there, else its own cap (infrastructure 5).</summary>
        public int MaxLevel(int state, string building)
        {
            var def = Hoi4StateData.Def(building);
            if (def == null) return 0;
            if (def.Shared)
                return Hoi4Buildings.SharedSlots(state) - Hoi4Buildings.SharedUsed(state) + Hoi4Buildings.Level(state, building);
            return building == "infrastructure" ? MaxInfrastructure : def.StateMax;
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
            if (def == null || def.Cost <= 0) return false;
            if (def.Shared) return FreeSharedSlots(state) > 0;
            int max = building == "infrastructure" ? MaxInfrastructure : def.StateMax;
            return max <= 0 || Hoi4Buildings.Level(state, building) + Queued(state, building) < max;
        }

        /// <summary>IC a day one civilian factory gives an item: base x (1 + construction speed + the building's
        /// speed) x (1 + infrastructure / 5) when infrastructure speeds the building up.</summary>
        public FP SpeedPerFactory(ushort country, int state, string building)
        {
            var e = EconomyOf(country);
            e.BuildingSpeed.TryGetValue(building, out FP own);
            FP speed = CivilianOutput * FP.Max(FP.Zero, FP.One + e.Speed + own);
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
                    if (Hoi4Buildings.UserOf(gameState, i.State) != country)
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
                    Hoi4Buildings.Add(i.State, i.Building, 1);
                    kv.Value.Built++;
                    Core.Recording.SessionRecorder.Note("construction", "done", country, 0, 0, 0,
                        $"{i.Building} in state {i.State} (now {Hoi4Buildings.Level(i.State, i.Building)})");
                    changed = true;
                    if (--i.Count > 0)
                    {
                        i.Progress = FP.Zero; // the order's next level
                        i.Cost = LevelCost(i.State, i.Building, Hoi4Buildings.Level(i.State, i.Building));
                        continue;
                    }
                    queue.RemoveAt(k--);
                }
                if (changed) QueueChanged?.Invoke(country);
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

        /// <summary>One level: onto the country's order for the building in the state when it has one, else a new
        /// order at the end of the queue. Returns the order's id (0 when not allowed).</summary>
        public int Enqueue(ushort country, int state, string building)
        {
            if (!CanQueue(country, state, building)) return 0;
            if (!countries.TryGetValue(country, out var c)) countries[country] = c = new CountryConstruction();
            foreach (var o in c.Queue)
                if (o.State == state && o.Building == building)
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
            if (item == null || count < 1) return false;
            if (count > item.Count)
                count = item.Count + System.Math.Max(0, System.Math.Min(Room(item.State, item.Building), count - item.Count));
            if (count == item.Count) return false;
            item.Count = count;
            QueueChanged?.Invoke(country);
            return true;
        }

        public bool Remove(ushort country, int id)
        {
            if (!countries.TryGetValue(country, out var c)) return false;
            int i = c.Queue.FindIndex(x => x.Id == id);
            if (i < 0) return false;
            c.Queue.RemoveAt(i);
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
