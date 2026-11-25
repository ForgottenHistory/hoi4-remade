using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Core.Modifiers;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Modifiers
{
    /// <summary>
    /// GAME: HOI4's country modifiers (common/ideas, common/technologies) on the engine's ModifierSystem
    /// (GameState.Modifiers). The engine stores and sums them per country; this is the policy:
    /// - Keys: only those the game reads (Keys, and every production_speed_[building]_factor) get an engine modifier
    ///   type id; HOI4 has 625 keys across ideas and technologies, the engine 512 types a scope. Ids are given in
    ///   sorted order at load, so every machine agrees.
    /// - Sources: a country's ideas and laws (common/hoi4_ideas.json5, listed per country in hoi4_country_economy)
    ///   are Policy sources (id: the idea's index), its researched technologies Technology sources (id: the tech's
    ///   index, added by Hoi4Research at the start and as each one finishes).
    /// - Values: HOI4 sums a key's values across its sources, so each is stored additive and read as the sum
    ///   (Sum). consumer_goods_factor stacks multiplicatively, product of (1 + value) (Product).
    /// - Other sources: stability's scaled static modifier and landmarks (Hoi4Stability, through SetSource).
    /// Changed fires with the country when its modifiers change (a tech, its stability, a landmark).
    /// </summary>
    public static class Hoi4Modifiers
    {
        const string IdeasFile = "common/hoi4_ideas.json5";
        const string EconomyFile = "common/hoi4_country_economy.json5";
        const string BuildingSpeedPrefix = "production_speed_", BuildingSpeedSuffix = "_factor";

        // the keys the game reads
        public const string ConsumerGoods = "consumer_goods_expected_value";   // the economy law's share
        public const string ConsumerGoodsFactor = "consumer_goods_factor";     // stacks multiplicatively
        public const string ConstructionSpeed = "production_speed_buildings_factor";
        public const string ResearchSpeed = "research_speed_factor";
        public const string FactoryOutput = "industrial_capacity_factory";                    // military factory output
        public const string MaxEfficiency = "production_factory_max_efficiency_factor";       // + points on the cap
        public const string StartEfficiency = "production_factory_start_efficiency_factor";   // + points on the start
        public const string EfficiencyGain = "production_factory_efficiency_gain_factor";     // x (1 + value) growth
        public const string StabilityFactor = "stability_factor";                             // + stability (Hoi4Stability)
        public const string LineChange = "line_change_production_efficiency_factor";         // retention bonus
        static readonly string[] Keys = { ConsumerGoods, ConsumerGoodsFactor, ConstructionSpeed, ResearchSpeed,
                                          FactoryOutput, MaxEfficiency, StartEfficiency, EfficiencyGain, StabilityFactor,
                                          LineChange };

        static readonly Dictionary<string, ushort> ids = new Dictionary<string, ushort>();
        static readonly List<string> ideaNames = new List<string>();
        static readonly Dictionary<string, int> ideaIndex = new Dictionary<string, int>();
        static readonly List<List<(ushort id, FP value)>> ideaModifiers = new List<List<(ushort, FP)>>();
        static readonly Dictionary<ushort, List<int>> countryIdeas = new Dictionary<ushort, List<int>>();
        static readonly Dictionary<ushort, int> version = new Dictionary<ushort, int>();
        static readonly Dictionary<(ushort country, ushort id), (int version, FP value)> products =
            new Dictionary<(ushort, ushort), (int, FP)>();
        static ModifierSystem system;

        public static bool IsReady { get; private set; }

        /// <summary>A country's modifiers changed (a technology finished; later a law or idea).</summary>
        public static event System.Action<ushort> Changed;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            ids.Clear();
            ideaNames.Clear();
            ideaIndex.Clear();
            ideaModifiers.Clear();
            countryIdeas.Clear();
            version.Clear();
            products.Clear();
            system = null;
            IsReady = false;
            Changed = null;
        }

        /// <summary>Load the ideas and give every country its starting ones. Hoi4Research calls this with the
        /// technologies' modifier keys (they get ids too), then adds each country's known technologies.</summary>
        public static void Initialize(GameState gameState, IEnumerable<string> technologyKeys)
        {
            var listeners = Changed;
            Reset();
            Changed = listeners; // subscribers made before the load stay
            system = gameState.Modifiers;
            JObject ideas = null;
            string path = DataFileResolver.Resolve(IdeasFile);
            if (File.Exists(path)) ideas = Json5Loader.LoadJson5File(path);
            else ArchonLogger.LogWarning($"Hoi4Modifiers: no {IdeasFile} (run Tools/hoi4_convert.py); no idea modifiers", "game_economy");

            // ids: the keys read, sorted, then every per-building construction speed seen, sorted
            var used = new SortedSet<string>(System.StringComparer.Ordinal);
            foreach (string k in Keys) used.Add(k);
            void See(string k) { if (IsBuildingSpeed(k)) used.Add(k); }
            foreach (string k in technologyKeys) See(k);
            if (ideas != null)
                foreach (var p in ideas.Properties())
                    if (p.Value["modifiers"] is JObject m) foreach (var x in m.Properties()) See(x.Name);
            foreach (string k in used)
            {
                if (ids.Count >= ModifierSet.MAX_MODIFIER_TYPES - 1)
                {
                    ArchonLogger.LogWarning($"Hoi4Modifiers: more keys than the engine's {ModifierSet.MAX_MODIFIER_TYPES} types; {k} and later left out", "game_economy");
                    break;
                }
                ids[k] = (ushort)(ids.Count + 1);
            }

            if (ideas != null)
                foreach (var p in ideas.Properties())
                {
                    var mods = new List<(ushort, FP)>();
                    if (p.Value["modifiers"] is JObject m)
                        foreach (var x in m.Properties())
                            if (ids.TryGetValue(x.Name, out ushort id)) mods.Add((id, FP.FromDouble((double)x.Value)));
                    ideaIndex[p.Name] = ideaNames.Count;
                    ideaNames.Add(p.Name);
                    ideaModifiers.Add(mods);
                }

            path = DataFileResolver.Resolve(EconomyFile);
            if (File.Exists(path))
                foreach (var p in Json5Loader.LoadJson5File(path).Properties())
                {
                    ushort country = gameState.Countries.GetCountryIdFromTag(p.Name);
                    if (country == 0 || !(p.Value["ideas"] is JArray list)) continue;
                    var own = new List<int>();
                    foreach (var i in list)
                        if (ideaIndex.TryGetValue((string)i, out int idx)) { own.Add(idx); AddIdea(country, idx); }
                    countryIdeas[country] = own;
                }
            IsReady = true;
            ArchonLogger.Log($"Hoi4Modifiers: {ids.Count} modifier keys, {ideaNames.Count} ideas, {countryIdeas.Count} countries", "game_economy");
        }

        static bool IsBuildingSpeed(string k) =>
            k.StartsWith(BuildingSpeedPrefix, System.StringComparison.Ordinal) && k.EndsWith(BuildingSpeedSuffix, System.StringComparison.Ordinal);

        /// <summary>The engine type id of a key, 0 when the game doesn't read it.</summary>
        public static ushort Id(string key) => key != null && ids.TryGetValue(key, out ushort id) ? id : (ushort)0;

        /// <summary>The id of production_speed_[building]_factor (0: no source has it).</summary>
        public static ushort BuildingSpeedId(string building) => Id(BuildingSpeedPrefix + building + BuildingSpeedSuffix);

        /// <summary>Whether the country has its starting ideas (the economy file listed it).</summary>
        public static bool HasCountry(ushort country) => countryIdeas.ContainsKey(country);

        /// <summary>The ideas and laws the country has, by name.</summary>
        public static IEnumerable<string> Ideas(ushort country)
        {
            if (!countryIdeas.TryGetValue(country, out var own)) yield break;
            foreach (int i in own) yield return ideaNames[i];
        }

        // ---- sources ------------------------------------------------------------------------------------

        static void AddIdea(ushort country, int idea)
        {
            foreach (var (id, value) in ideaModifiers[idea])
                system.AddCountryModifier(country, ModifierSource.CreatePermanent(
                    ModifierSource.SourceType.Policy, (uint)idea, id, value, isMultiplicative: false));
        }

        /// <summary>A researched technology's modifiers (key -> value, from its definition). quiet: at the start,
        /// before anything listens.</summary>
        public static void AddTechnology(ushort country, int tech, IReadOnlyDictionary<string, FP> modifiers, bool quiet = false)
        {
            if (system == null || modifiers == null) return;
            bool any = false;
            foreach (var kv in modifiers)
            {
                ushort id = Id(kv.Key);
                if (id == 0) continue;
                system.AddCountryModifier(country, ModifierSource.CreatePermanent(
                    ModifierSource.SourceType.Technology, (uint)tech, id, kv.Value, isMultiplicative: false));
                any = true;
            }
            if (any) Touch(country, quiet);
        }

        /// <summary>Replace one source's modifiers on a country (key -> value; null or empty: just remove it).
        /// Keys the game doesn't read are skipped. Returns whether anything changed; the caller fires Changed
        /// (NotifyChanged) once it has set all its sources.</summary>
        public static bool SetSource(ushort country, ModifierSource.SourceType type, uint id,
                                     IEnumerable<KeyValuePair<string, FP>> modifiers)
        {
            if (system == null || country == 0) return false;
            bool any = system.RemoveCountryModifiersBySource(country, type, id) > 0;
            if (modifiers != null)
                foreach (var kv in modifiers)
                {
                    ushort mid = Id(kv.Key);
                    if (mid == 0 || kv.Value == FP.Zero) continue;
                    system.AddCountryModifier(country, ModifierSource.CreatePermanent(type, id, mid, kv.Value, isMultiplicative: false));
                    any = true;
                }
            if (any) Touch(country, quiet: true);
            return any;
        }

        /// <summary>Fire Changed for a country after SetSource.</summary>
        public static void NotifyChanged(ushort country) => Changed?.Invoke(country);

        static void Touch(ushort country, bool quiet)
        {
            version[country] = (version.TryGetValue(country, out int v) ? v : 0) + 1;
            if (!quiet) Changed?.Invoke(country);
        }

        // ---- queries ------------------------------------------------------------------------------------

        /// <summary>The sum of a key's values over the country's sources (0 without any).</summary>
        public static FP Sum(ushort country, ushort id) =>
            system == null || id == 0 ? FP.Zero : system.GetCountryModifier(country, id, FP.Zero);

        public static FP Sum(ushort country, string key) => Sum(country, Id(key));

        /// <summary>Product of (1 + value) over the country's sources of a multiplying key (1 without any).
        /// Cached per country until its modifiers change.</summary>
        public static FP Product(ushort country, ushort id)
        {
            if (system == null || id == 0) return FP.One;
            int v = version.TryGetValue(country, out int x) ? x : 0;
            if (products.TryGetValue((country, id), out var c) && c.version == v) return c.value;
            FP product = FP.One;
            system.ForEachCountryModifierWithInheritance(country, (source, _) =>
            {
                if (source.ModifierTypeId == id) product = product * (FP.One + source.Value);
            });
            products[(country, id)] = (v, product);
            return product;
        }

        public static FP Product(ushort country, string key) => Product(country, Id(key));
    }
}
