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
        // Equipment per archetype (index into Hoi4Divisions.Archetypes): what the template needs and what it has.
        // 1936 divisions start with Need x start_equipment_factor. Battle losses take 70% of the strength lost
        // (EQUIPMENT_COMBAT_LOSS_FACTOR); Hoi4Equipment refills from the country's stockpile.
        public FP[] Need, Have;
        public Part[] Parts;           // sub-units: their strength share and the archetypes they need
        public FP Men;                 // manpower it has, of Manpower (casualties take it, Hoi4Manpower refills it)

        public struct Part { public FP Weight; public int[] Archetypes; }

        /// <summary>
        /// Share of its strength the equipment allows (HOI4: a sub-unit is as strong as its equipment): per sub-unit
        /// the lowest fill of the archetypes it needs, weighted by the sub-unit's max strength.
        /// </summary>
        public FP EquipmentLevel
        {
            get
            {
                if (Parts == null || Parts.Length == 0) return FP.One;
                FP sum = FP.Zero, total = FP.Zero;
                foreach (var p in Parts)
                {
                    FP fill = FP.One;
                    foreach (int a in p.Archetypes)
                        fill = FP.Min(fill, Need[a] > FP.Zero ? FP.Min(FP.One, Have[a] / Need[a]) : FP.One);
                    sum += p.Weight * fill;
                    total += p.Weight;
                }
                return total > FP.Zero ? sum / total : FP.One;
            }
        }

        /// <summary>Share of its manpower it has.</summary>
        public FP ManpowerLevel => Manpower > 0 ? FP.Min(FP.One, Men / FP.FromInt(Manpower)) : FP.One;

        /// <summary>The strength its equipment and men allow: what reinforcement refills to, and "fully rested" for orders.</summary>
        public FP FullStrength => MaxStrength * FP.Min(EquipmentLevel, ManpowerLevel);

        /// <summary>Battle damage: men lost with the strength, and 70% of the equipment (the rest is recovered).
        /// Returns the men lost (casualties, for war score).</summary>
        public FP TakeLosses(FP strengthLost)
        {
            if (MaxStrength <= FP.Zero || strengthLost <= FP.Zero) return FP.Zero;
            FP share = FP.Min(FP.One, strengthLost / MaxStrength);
            FP before = Men;
            Men = FP.Max(FP.Zero, Men - FP.FromInt(Manpower) * share);
            if (Need != null)
                for (int a = 0; a < Need.Length; a++)
                    Have[a] = FP.Max(FP.Zero, Have[a] - Need[a] * share * EquipmentLossFactor);
            return before - Men;
        }

        public static readonly FP EquipmentLossFactor = FP.FromFraction(7, 10); // HOI4 EQUIPMENT_COMBAT_LOSS_FACTOR
        public FP MaxOrganisation;     // average of sub-unit max_organisation
        public FP Organisation;
        public FP CombatWidth;         // sum over regiments
        public FP SpeedKmh;            // slowest sub-unit
        public int Manpower;
        public FP Experience;
        public FP Planning;            // planning bonus, 0..0.3 (Hoi4BattlePlans: builds on the front line with an offensive)
        public FP Entrenchment;        // dug in, 0..5 (Hoi4Combat: +1 a day standing still out of battle, lost on moving)
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

        /// <summary>Equipment archetypes the divisions need (sorted): Hoi4Division.Need/Have and the stockpiles index them.</summary>
        public static string[] Archetypes { get; private set; } = new string[0];

        public Hoi4Division Get(ushort unitId) => divisions.TryGetValue(unitId, out var d) ? d : null;

        public void Remove(ushort unitId) => divisions.Remove(unitId);

        // ---- templates (new divisions: Hoi4.Deployment) ---------------------------------------------------

        JObject subUnitData;
        readonly Dictionary<string, JObject> templatesByTag = new Dictionary<string, JObject>();
        readonly Dictionary<string, int> nextNumber = new Dictionary<string, int>(); // "TAG/template" -> next division number

        /// <summary>The country's division templates, in file order (none when it has no 1936 army).</summary>
        public IEnumerable<string> Templates(string tag)
        {
            if (tag == null || !templatesByTag.TryGetValue(tag, out var t)) yield break;
            foreach (var p in t.Properties()) yield return p.Name;
        }

        public bool HasTemplate(string tag, string template) =>
            tag != null && template != null && templatesByTag.TryGetValue(tag, out var t) && t[template] is JObject;

        public const int Reserve = 0, Normal = 1, Elite = 2;
        readonly Dictionary<string, int> templatePriority = new Dictionary<string, int>(); // "TAG/template" -> changed priority

        /// <summary>The template's reinforcement priority (HOI4 division template priority): 0 reserve, 1 normal,
        /// 2 elite. From the 1936 history (54 reserve templates, 12 elite), changed by SetTemplatePriority.</summary>
        public int TemplatePriority(string tag, string template)
        {
            if (tag == null || template == null) return Normal;
            if (templatePriority.TryGetValue(tag + "/" + template, out int p)) return p;
            return HasTemplate(tag, template) ? Mathf.Clamp((int?)templatesByTag[tag][template]["priority"] ?? Normal, Reserve, Elite) : Normal;
        }

        public void SetTemplatePriority(string tag, string template, int priority)
        {
            if (HasTemplate(tag, template)) templatePriority[tag + "/" + template] = Mathf.Clamp(priority, Reserve, Elite);
        }

        /// <summary>The template's main regiment type (HOI4 shows a division by it; its icon), null when unknown.</summary>
        public string TemplateMain(string tag, string template) =>
            HasTemplate(tag, template) ? (string)templatesByTag[tag][template]["main"] : null;

        /// <summary>Days a division of the template trains: its slowest sub-unit's training_time (HOI4).</summary>
        public int TrainingDays(string tag, string template)
        {
            if (!HasTemplate(tag, template) || subUnitData == null) return 0;
            var tj = (JObject)templatesByTag[tag][template];
            int days = 0;
            foreach (string name in (tj["regiments"]?.Select(x => (string)x) ?? Enumerable.Empty<string>())
                         .Concat(tj["support"]?.Select(x => (string)x) ?? Enumerable.Empty<string>()))
                if (subUnitData[name] is JObject s) days = System.Math.Max(days, (int?)s["training_time"] ?? 0);
            return days;
        }

        /// <summary>A new division of the template that isn't on the map yet (UnitId 0): full manpower, no equipment,
        /// named from the template's names group with its next number (HOI4 division_names_group).</summary>
        public Hoi4Division NewDivision(string tag, string template)
        {
            if (!HasTemplate(tag, template) || subUnitData == null) return null;
            var tj = (JObject)templatesByTag[tag][template];
            string key = tag + "/" + template;
            int n = nextNumber.TryGetValue(key, out int v) ? v : ((int?)tj["next_number"] ?? 1);
            nextNumber[key] = n + 1;
            string format = (string)tj["name_format"];
            string name = string.IsNullOrEmpty(format) ? $"{template} {n}" : FormatName(format, n);
            var d = new JObject { ["name"] = name, ["equipment"] = 0, ["experience"] = 0 };
            var division = Build(0, tag, d, template, tj, subUnitData);
            division.Strength = FP.Zero;
            return division;
        }

        /// <summary>HOI4 name formats: %d / %s the number, %dth an English ordinal (Tools/hoi4_convert.py format_division_name).</summary>
        static string FormatName(string format, int n)
        {
            if (format.Contains("%dth"))
            {
                int h = n % 100, l = n % 10;
                string suffix = h >= 11 && h <= 13 ? "th" : l == 1 ? "st" : l == 2 ? "nd" : l == 3 ? "rd" : "th";
                format = format.Replace("%dth", n + suffix);
            }
            return format.Replace("%d", n.ToString()).Replace("%s", n.ToString());
        }

        /// <summary>Put a trained division on the map: an engine unit of its main type in the province, then it
        /// counts as a division (combat, plans, supply). Returns the unit id, 0 when the unit couldn't be made.</summary>
        public ushort Deploy(Hoi4Division division, ushort province, ushort country)
        {
            var gameState = Engine.ArchonEngine.Instance?.GameState;
            var unitTypes = StarterKit.Initializer.Instance?.UnitSystem;
            if (division == null || gameState == null || unitTypes == null || !gameState.Provinces.HasProvince(province)) return 0;
            var unitType = unitTypes.GetUnitType(division.MainType) ?? unitTypes.GetUnitType("infantry");
            if (unitType == null) return 0;
            ushort unitId = gameState.Units.CreateUnit(province, country, unitType.ID);
            if (unitId == 0) return 0;
            division.UnitId = unitId;
            divisions[unitId] = division;
            return unitId;
        }

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
            subUnitData = subUnits;

            var archetypes = new SortedSet<string>(System.StringComparer.Ordinal);
            foreach (var s in subUnits.Properties())
                if (s.Value["need"] is JObject need)
                    foreach (var a in need.Properties()) archetypes.Add(a.Name);
            Archetypes = archetypes.ToArray();
            archetypeIndex = new Dictionary<string, int>();
            for (int i = 0; i < Archetypes.Length; i++) archetypeIndex[Archetypes[i]] = i;

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
                templatesByTag[tag] = templates;

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

        static Dictionary<string, int> archetypeIndex = new Dictionary<string, int>();

        static Hoi4Division Build(ushort unitId, string tag, JToken d, string templateName, JObject template, JObject subUnits)
        {
            var regiments = template["regiments"]?.Select(t => (string)t).ToList() ?? new List<string>();
            var support = template["support"]?.Select(t => (string)t).ToList() ?? new List<string>();

            // Equipment: the template's need per archetype, and per sub-unit its strength share and archetypes
            var need = new FP[Archetypes.Length];
            var parts = new List<Hoi4Division.Part>();
            foreach (string name in regiments.Concat(support))
            {
                if (!(subUnits[name] is JObject s)) continue;
                var uses = new List<int>();
                if (s["need"] is JObject n)
                    foreach (var a in n.Properties())
                        if (archetypeIndex.TryGetValue(a.Name, out int i))
                        {
                            need[i] += Num(a.Value);
                            uses.Add(i);
                        }
                parts.Add(new Hoi4Division.Part { Weight = Val(s, "max_strength"), Archetypes = uses.ToArray() });
            }

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

            FP equipment = FP.Clamp(d["equipment"] != null ? Num(d["equipment"]) : FP.One, FP.Zero, FP.One);
            var have = new FP[need.Length];
            for (int i = 0; i < need.Length; i++) have[i] = need[i] * equipment; // HOI4 start_equipment_factor
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
                Need = need,
                Have = have,
                Parts = parts.ToArray(),
                Men = FP.FromInt(manpower), // HOI4 1936 divisions have their full manpower; equipment is what they lack
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
            division.Strength = division.FullStrength; // = max strength x start_equipment_factor
            return division;
        }
    }
}
