using System.Collections;
using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Core.Resources;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4 manpower and reinforcement. The pool is the engine's ResourceSystem resource "manpower" (so it is
    /// saved with the game); this class is the policy:
    /// - Start: recruitable manpower = (core population + non-core population x 2%) x the mobilisation law
    ///   (HOI4 LOCAL_MANPOWER_ACCESSIBLE_NON_CORE_FACTOR; common/hoi4_country_stats.json5), minus the manpower of the
    ///   divisions already in the field.
    /// - Daily: the pool grows by the yearly population growth (POPULATION_YEARLY_GROWTH_BASE, 1.5%) of the
    ///   recruitable manpower, a 365th a day.
    /// - Daily: every damaged division gets a chunk of its missing strength back (REINFORCEMENT_MANPOWER_CHUNK, 10% of
    ///   its full strength), paid in the men that strength stands for; with too little in the pool it gets what is left.
    ///   Countries and divisions go in id order, so the pool runs out the same way on every machine.
    /// No equipment yet (no production): a division refills only up to its equipment level (HOI4's
    /// start_equipment_factor, Hoi4Division.FullStrength). Men lost in battle are simply gone; refilling them is what
    /// drains the pool.
    /// </summary>
    public sealed class Hoi4Manpower : MonoBehaviour
    {
        const string StatsFile = "common/hoi4_country_stats.json5";

        /// <summary>The engine resource id of manpower.</summary>
        public const ushort ResourceId = 1;

        static readonly FP NonCoreFactor = FP.FromFraction(2, 100);          // LOCAL_MANPOWER_ACCESSIBLE_NON_CORE_FACTOR
        static readonly FP GrowthPerDay = FP.FromFraction(15, 1000 * 365);   // POPULATION_YEARLY_GROWTH_BASE, a day
        static readonly FP ReinforceChunk = FP.FromFraction(1, 10);          // REINFORCEMENT_MANPOWER_CHUNK, of full strength a day

        public static Hoi4Manpower Instance { get; private set; }

        GameState gameState;
        readonly Dictionary<ushort, FP> recruitable = new Dictionary<ushort, FP>();
        readonly List<ushort> countries = new List<ushort>(); // with a pool, in id order

        public bool IsReady { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Manpower");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Manpower>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>The country's manpower pool (men available to reinforce and recruit).</summary>
        public FP Available(ushort country) =>
            IsReady ? gameState.Resources.GetResource(country, ResourceId) : FP.Zero;

        /// <summary>The country's recruitable manpower (the pool before the divisions in the field took theirs).</summary>
        public FP Recruitable(ushort country) => recruitable.TryGetValue(country, out var r) ? r : FP.Zero;

        IEnumerator Start()
        {
            while (Hoi4Divisions.Instance == null || !Hoi4Divisions.Instance.IsLoaded
                   || Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Resources == null || !DataFileResolver.IsInitialized)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;

            var resources = gameState.Resources;
            if (!resources.IsInitialized)
                resources.Initialize(4096, gameState.EventBus); // nothing else in the scene uses the engine's resources
            if (!resources.IsResourceRegistered(ResourceId))
                resources.RegisterResource(ResourceId, new ResourceDefinition
                {
                    id = "manpower", displayName = "Manpower", icon = "manpower", startingAmount = 0, minValue = 0, maxValue = 0,
                });

            // Men already in the field, per country
            var fielded = new Dictionary<ushort, FP>();
            foreach (var d in Hoi4Divisions.Instance.All.Values)
            {
                if (!gameState.Units.HasUnit(d.UnitId)) continue;
                ushort c = gameState.Units.GetUnit(d.UnitId).countryID;
                fielded[c] = (fielded.TryGetValue(c, out var f) ? f : FP.Zero) + FP.FromInt(d.Manpower);
            }

            string path = DataFileResolver.Resolve(StatsFile);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4Manpower: no {StatsFile}; no manpower pools (rerun Tools/hoi4_convert.py)", "game_units");
                yield break;
            }
            resources.BeginBatch();
            foreach (var p in Json5Loader.LoadJson5File(path).Properties())
            {
                if (!(p.Value is JObject s)) continue;
                ushort country = gameState.Countries.GetCountryIdFromTag(p.Name);
                if (country == 0) continue;
                FP population = Num(s["population"]) + Num(s["noncore_population"]) * NonCoreFactor;
                FP total = population * Num(s["conscription"]);
                recruitable[country] = total;
                countries.Add(country);
                FP used = fielded.TryGetValue(country, out var u) ? u : FP.Zero;
                resources.SetResource(country, ResourceId, FP.Max(FP.Zero, total - used));
            }
            resources.EndBatch();
            countries.Sort();
            IsReady = true;
            ArchonLogger.Log($"Hoi4Manpower: pools for {countries.Count} countries", "game_units");
        }

        /// <summary>Daily, from Hoi4Simulation: growth, then reinforcement.</summary>
        public void SimulateDay()
        {
            if (!IsReady) return;
            var resources = gameState.Resources;
            var divisions = Hoi4Divisions.Instance;
            if (divisions == null) return;

            // Damaged divisions per country, in id order
            var byCountry = new Dictionary<ushort, List<Hoi4Division>>();
            foreach (var d in divisions.All.Values)
            {
                if (d.Strength >= d.FullStrength || !gameState.Units.HasUnit(d.UnitId)) continue;
                ushort c = gameState.Units.GetUnit(d.UnitId).countryID;
                if (!byCountry.TryGetValue(c, out var list)) byCountry[c] = list = new List<Hoi4Division>();
                list.Add(d);
            }

            resources.BeginBatch();
            foreach (ushort country in countries)
            {
                FP pool = resources.GetResource(country, ResourceId) + recruitable[country] * GrowthPerDay;
                FP spent = FP.Zero;
                int helped = 0;
                if (byCountry.TryGetValue(country, out var damaged))
                {
                    damaged.Sort((a, b) => a.UnitId.CompareTo(b.UnitId));
                    foreach (var d in damaged)
                    {
                        if (pool <= FP.Zero) break;
                        FP full = d.FullStrength;
                        if (full <= FP.Zero || d.Manpower <= 0) continue;
                        FP gain = FP.Min(full * ReinforceChunk, full - d.Strength);
                        FP men = FP.FromInt(d.Manpower) * gain / full; // the men that strength stands for
                        if (men > pool) { gain = gain * pool / men; men = pool; }
                        d.Strength = FP.Min(full, d.Strength + gain);
                        pool -= men;
                        spent += men;
                        helped++;
                    }
                }
                resources.SetResource(country, ResourceId, pool);
                if (helped > 0)
                    Core.Recording.SessionRecorder.Note("manpower", "reinforce", country, 0, 0, 0,
                        $"{helped} divisions, {spent.ToInt()} men, pool {pool.ToInt()}");
            }
            resources.EndBatch();
            if (byCountry.Count > 0) Hoi4UnitCounters.Instance?.MarkDirty(); // strength bars
        }

        static FP Num(JToken t) => t != null && t.Type != JTokenType.Null ? FP.FromDouble((double)t) : FP.Zero;
    }
}
