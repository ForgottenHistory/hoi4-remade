using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Core;
using Core.Loaders;
using Core.Modding;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: one HOI4 division. The engine unit (Core.Units.UnitState) only holds province,
    /// country and unit type; everything HOI4-specific lives here, keyed by the engine unit ID.
    /// All numbers are FixedPoint64 (simulation state must be the same on every machine); presentation
    /// reads them with ToFloat().
    /// </summary>
    public sealed class Hoi4Division
    {
        public ushort UnitId;
        public string Tag;
        public string Name;
        public string Template;
        public string MainType;        // highest-priority regiment type (HOI4 shows a division by it)
        public int Regiments;
        public int SupportCompanies;
        public FP MaxStrength;         // HOI4 HP: sum of sub-unit max_strength
        public FP Strength;
        public FP MaxOrganisation;     // average of sub-unit max_organisation
        public FP Organisation;
        public FP CombatWidth;         // sum over regiments
        public FP SpeedKmh;            // slowest sub-unit
        public int Manpower;
        public FP Experience;
        public FP Planning;            // planning bonus, 0..0.3 (Hoi4BattlePlans: builds on the front line with an offensive)
        public FP SoftAttack, HardAttack, Defense, Breakthrough; // sums over sub-units (1936 equipment)
        public FP Hardness, Armor, Piercing;
    }

    /// <summary>
    /// GAME: spawns the HOI4 1936 land armies from history/units/oob_1936.json5 (written by
    /// Tools/hoi4_convert.py) as engine units once the StarterKit is up, and keeps the
    /// per-division HOI4 data. Engine unit types are the division main types (units/*.json5).
    /// </summary>
    public sealed class Hoi4Divisions : MonoBehaviour
    {
        const string OobFile = "history/units/oob_1936.json5";

        public static Hoi4Divisions Instance { get; private set; }

        readonly Dictionary<ushort, Hoi4Division> divisions = new Dictionary<ushort, Hoi4Division>();

        public bool IsLoaded { get; private set; }
        public IReadOnlyDictionary<ushort, Hoi4Division> All => divisions;

        public Hoi4Division Get(ushort unitId) => divisions.TryGetValue(unitId, out var d) ? d : null;

        public void Remove(ushort unitId) => divisions.Remove(unitId);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            if (Instance != null) return;
            var go = new GameObject("Hoi4Divisions");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Divisions>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        IEnumerator Start()
        {
            // Wait for the StarterKit: it creates the unit system and loads the unit types
            while (StarterKit.Initializer.Instance == null || !StarterKit.Initializer.Instance.IsInitialized)
                yield return null;

            var gameState = Engine.ArchonEngine.Instance != null ? Engine.ArchonEngine.Instance.GameState : null;
            var unitTypes = StarterKit.Initializer.Instance.UnitSystem;
            if (gameState == null || gameState.Units == null || unitTypes == null)
            {
                ArchonLogger.LogWarning("Hoi4Divisions: game state or unit system missing, no armies spawned", "game_units");
                yield break;
            }
            if (gameState.Units.GetUnitCount() > 0)
                yield break; // units already exist (loaded save)

            string path = DataFileResolver.Resolve(OobFile);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4Divisions: {OobFile} not found (rerun Tools/hoi4_convert.py)", "game_units");
                yield break;
            }

            JObject root;
            try
            {
                root = Json5Loader.LoadJson5File(path);
            }
            catch (System.Exception e)
            {
                ArchonLogger.LogError($"Hoi4Divisions: failed to read {path}: {e.Message}", "game_units");
                yield break;
            }

            var subUnits = root["sub_units"] as JObject;
            var countries = root["countries"] as JObject;
            if (subUnits == null || countries == null)
                yield break;

            int created = 0, skipped = 0;
            var missingTags = new List<string>();
            foreach (var country in countries.Properties())
            {
                string tag = country.Name;
                ushort countryId = gameState.Countries.GetCountryIdFromTag(tag);
                if (countryId == 0)
                {
                    missingTags.Add(tag);
                    continue;
                }

                var templates = country.Value["templates"] as JObject;
                var list = country.Value["divisions"] as JArray;
                if (templates == null || list == null) continue;

                foreach (var d in list)
                {
                    string templateName = (string)d["template"];
                    var template = templates[templateName] as JObject;
                    ushort province = (ushort)(int)d["location"];
                    if (template == null || !gameState.Provinces.HasProvince(province))
                    {
                        skipped++;
                        continue;
                    }

                    string main = (string)template["main"];
                    var unitType = unitTypes.GetUnitType(main) ?? unitTypes.GetUnitType("infantry");
                    if (unitType == null)
                    {
                        skipped++;
                        continue;
                    }

                    ushort unitId = gameState.Units.CreateUnit(province, countryId, unitType.ID);
                    if (unitId == 0)
                    {
                        skipped++;
                        continue;
                    }

                    var division = Build(unitId, tag, d, templateName, template, subUnits);
                    divisions[unitId] = division;
                    created++;
                }
            }

            IsLoaded = true;
            ArchonLogger.Log($"Hoi4Divisions: spawned {created} divisions in {countries.Count - missingTags.Count} countries" +
                             (skipped > 0 ? $", skipped {skipped}" : "") +
                             (missingTags.Count > 0 ? $", unknown tags: {string.Join(", ", missingTags)}" : ""), "game_units");
        }

        /// <summary>A number from the data as fixed point (parsed as a double, which is exact and the same
        /// everywhere, then converted once; no float arithmetic on the way).</summary>
        static FP Num(JToken t) => t != null && t.Type != JTokenType.Null ? FP.FromDouble((double)t) : FP.Zero;
        static FP Val(JObject s, string key) => Num(s[key]);

        static readonly FP P3 = FP.FromFraction(3, 10), P4 = FP.FromFraction(4, 10), P6 = FP.FromFraction(6, 10), P7 = FP.FromFraction(7, 10);

        static Hoi4Division Build(ushort unitId, string tag, JToken d, string templateName, JObject template, JObject subUnits)
        {
            var regiments = template["regiments"]?.Select(t => (string)t).ToList() ?? new List<string>();
            var support = template["support"]?.Select(t => (string)t).ToList() ?? new List<string>();

            FP strength = FP.Zero, org = FP.Zero, width = FP.Zero, speed = FP.MaxValue;
            FP soft = FP.Zero, hard = FP.Zero, def = FP.Zero, brk = FP.Zero, hardness = FP.Zero;
            FP armorMax = FP.Zero, armorSum = FP.Zero, pierceMax = FP.Zero, pierceSum = FP.Zero;
            int manpower = 0, count = 0;
            foreach (string name in regiments.Concat(support))
            {
                if (!(subUnits[name] is JObject s)) continue;
                strength += Val(s, "max_strength");
                org += Val(s, "max_organisation");
                manpower += (int)s["manpower"];
                speed = FP.Min(speed, Val(s, "speed"));
                soft += Val(s, "soft_attack"); hard += Val(s, "hard_attack");
                def += Val(s, "defense"); brk += Val(s, "breakthrough");
                hardness += Val(s, "hardness");
                armorMax = FP.Max(armorMax, Val(s, "armor")); armorSum += Val(s, "armor");
                pierceMax = FP.Max(pierceMax, Val(s, "piercing")); pierceSum += Val(s, "piercing");
                count++;
            }
            foreach (string name in regiments)
                if (subUnits[name] is JObject s)
                    width += Val(s, "combat_width");

            FP equipment = d["equipment"] != null ? Num(d["equipment"]) : FP.One;
            var division = new Hoi4Division
            {
                UnitId = unitId,
                Tag = tag,
                Name = (string)d["name"],
                Template = templateName,
                MainType = (string)template["main"],
                Regiments = regiments.Count,
                SupportCompanies = support.Count,
                MaxStrength = strength,
                Strength = strength * FP.Clamp(equipment, FP.Zero, FP.One),
                MaxOrganisation = count > 0 ? org / count : FP.Zero,
                CombatWidth = width,
                SpeedKmh = speed == FP.MaxValue ? FP.FromInt(4) : speed,
                Manpower = manpower,
                Experience = Num(d["experience"]),
                SoftAttack = soft, HardAttack = hard, Defense = def, Breakthrough = brk,
                Hardness = count > 0 ? hardness / count : FP.Zero,
                Armor = count > 0 ? P3 * armorMax + P7 * armorSum / count : FP.Zero,
                Piercing = count > 0 ? P4 * pierceMax + P6 * pierceSum / count : FP.Zero,
            };
            division.Organisation = division.MaxOrganisation;
            return division;
        }
    }
}
