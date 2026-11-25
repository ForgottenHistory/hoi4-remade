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
        // Which equipment versions its Have is made of, per archetype (null or empty: none known; HOI4 keeps every
        // version apart). Hoi4Equipment delivers the newest first and upgrades full divisions; its attack, defence,
        // armour and reliability are the held versions' stats weighted by amount (Hoi4Divisions.ApplyEquipmentStats).
        public Hoi4EquipmentMix[] Versions;
        internal bool VersionsChanged; // its stats need recomputing (Hoi4Equipment, end of the day)

        /// <summary>The version mix of an archetype, made on first use.</summary>
        public Hoi4EquipmentMix Mix(int a)
        {
            if (Versions == null || Versions.Length != Need.Length) System.Array.Resize(ref Versions, Need.Length);
            return Versions[a] ?? (Versions[a] = new Hoi4EquipmentMix());
        }
        public Part[] Parts;           // sub-units: their strength share and the archetypes they need
        public FP Men;                 // manpower it has, of Manpower (casualties take it, Hoi4Manpower refills it)
        // Terrain modifiers (forest, urban..., river, amphibious): its battalions' averaged, plus its support
        // companies' (Sum). Shared with the other divisions of its template: read only.
        public Dictionary<string, Hoi4TerrainModifier> Terrain;

        static readonly Dictionary<string, Hoi4TerrainModifier> NoTerrain = new Dictionary<string, Hoi4TerrainModifier>();

        /// <summary>Its modifiers in a terrain ("forest"), or for "river" and "amphibious"; zero when it has none.</summary>
        public Hoi4TerrainModifier TerrainModifier(string terrain) =>
            terrain != null && (Terrain ?? NoTerrain).TryGetValue(terrain, out var m) ? m : default;

        public struct Part
        {
            public FP Weight; public int[] Archetypes;
            // its own factors on its equipment's soft attack, hard attack, defence, breakthrough (HOI4 sub-unit stats)
            public FP SoftFactor, HardFactor, DefenseFactor, BreakthroughFactor;
        }

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
        public FP SupplyUse;           // supply it needs per day: sum of sub-unit supply_consumption (Hoi4Supply)
        public FP Reliability;         // average of sub-unit reliability: how little attrition costs it (Hoi4Supply)
        public FP MaxOrganisation;     // average of sub-unit max_organisation
        public FP Organisation;
        public FP CombatWidth;         // sum over regiments
        public FP SpeedKmh;            // slowest sub-unit
        public int Manpower;
        public FP Experience;          // 0..1; its level: Level
        // HOI4 UNIT_EXP_LEVELS: experience needed for trained, regular, seasoned, veteran
        static readonly FP[] LevelThresholds =
            { FP.FromFraction(1, 10), FP.FromFraction(3, 10), FP.FromFraction(75, 100), FP.FromFraction(9, 10) };
        public static readonly string[] LevelNames = { "Green", "Trained", "Regular", "Seasoned", "Veteran" };

        /// <summary>The experience level (0 green, 1 trained, 2 regular, 3 seasoned, 4 veteran) of an experience.</summary>
        public static int LevelOf(FP experience)
        {
            int level = 0;
            while (level < LevelThresholds.Length && experience >= LevelThresholds[level]) level++;
            return level;
        }

        /// <summary>Experience at the start of a level (Trained: 0.1).</summary>
        public static FP LevelStart(int level) => level <= 0 ? FP.Zero : LevelThresholds[System.Math.Min(level, LevelThresholds.Length) - 1];

        public int Level => LevelOf(Experience);

        /// <summary>Progress from this level's start to the next level (HOI4's experience bar), 1 at veteran.</summary>
        public FP LevelProgress
        {
            get
            {
                int level = Level;
                if (level >= LevelThresholds.Length) return FP.One;
                FP start = LevelStart(level), end = LevelThresholds[level];
                return FP.Clamp((Experience - start) / (end - start), FP.Zero, FP.One);
            }
        }
        public FP Planning;            // planning bonus, 0..0.3 (Hoi4BattlePlans: builds on the front line with an offensive)
        public FP Entrenchment;        // dug in, 0..5 (Hoi4Combat: +1 a day standing still out of battle, lost on moving)
        public FP SoftAttack, HardAttack, Defense, Breakthrough; // sums over sub-units (of the equipment it holds)
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

        /// <summary>Remove a division template from the country (callers check nothing uses it: Hoi4Deployment.TemplateInUse).</summary>
        public void DeleteTemplate(string tag, string template)
        {
            if (!HasTemplate(tag, template)) return;
            templatesByTag[tag].Remove(template);
            templatePriority.Remove(tag + "/" + template);
        }

        readonly Dictionary<string, List<string>> subUnitsByTag = new Dictionary<string, List<string>>();

        /// <summary>A sub-unit's data (common/units: stats, group, need, name), null when unknown.</summary>
        public JObject SubUnit(string name) => name != null && subUnitData != null ? subUnitData[name] as JObject : null;

        /// <summary>A sub-unit's display name (HOI4 localisation).</summary>
        public string SubUnitName(string name) => (string)SubUnit(name)?["name"] ?? name;

        readonly Dictionary<string, string> groupNames = new Dictionary<string, string>();

        /// <summary>A sub-unit group's name (HOI4 group_*_title: "Infantry Battalions", "Support Companies").</summary>
        public string GroupName(string group) =>
            group != null && groupNames.TryGetValue(group, out var n) ? n : (group ?? "").Replace('_', ' ');

        /// <summary>A sub-unit's group: infantry, mobile, armor, combat_support... or support (support companies).</summary>
        public string SubUnitGroup(string name) => (string)SubUnit(name)?["group"] ?? "infantry";

        /// <summary>Sub-units the country's division designer offers (1936 technology and equipment).</summary>
        public IReadOnlyList<string> SubUnitsOf(string tag) =>
            tag != null && subUnitsByTag.TryGetValue(tag, out var l) ? l : (IReadOnlyList<string>)System.Array.Empty<string>();

        /// <summary>The template's layout for the division designer (null when it doesn't exist).</summary>
        public Hoi4TemplateDesign Design(string tag, string template)
        {
            if (!HasTemplate(tag, template)) return null;
            var tj = (JObject)templatesByTag[tag][template];
            var design = new Hoi4TemplateDesign { Name = template, Priority = TemplatePriority(tag, template) };
            var regiments = tj["regiments"] as JArray ?? new JArray();
            var grid = tj["grid"] as JArray;
            for (int i = 0; i < regiments.Count; i++)
            {
                // without positions (older data): three to a column, as HOI4's 1936 templates mostly are
                int x = grid != null && i < grid.Count ? (int)grid[i][0] : i / 3, y = grid != null && i < grid.Count ? (int)grid[i][1] : i % 3;
                while (design.RegimentAt(x, y) != null) y++; // history files sometimes stack two on one slot
                design.Regiments.Add(new Hoi4TemplateDesign.Slot((string)regiments[i], x, y));
            }
            var regimental = tj["regimental_support"] as JArray ?? new JArray();
            var columns = tj["regimental_grid"] as JArray;
            for (int i = 0; i < regimental.Count; i++)
            {
                int x = columns != null && i < columns.Count ? (int)columns[i] : i;
                if (design.RegimentalAt(x) == null) design.Regimental.Add(new Hoi4TemplateDesign.Slot((string)regimental[i], x, 0));
            }
            var support = tj["support"] as JArray ?? new JArray();
            var rows = tj["support_grid"] as JArray;
            for (int i = 0; i < support.Count; i++)
                design.Support.Add(new Hoi4TemplateDesign.Slot((string)support[i], 0, rows != null && i < rows.Count ? (int)rows[i] : i));
            // HOI4 data can stack two companies on one row: the designer shows one per row
            design.Support.Sort((a, b) => a.Y.CompareTo(b.Y));
            for (int i = 0; i < design.Support.Count; i++) design.Support[i] = new Hoi4TemplateDesign.Slot(design.Support[i].Type, 0, i);
            return design;
        }

        /// <summary>Whether the country may save the design over the template (null: a new template). Sub-units must be
        /// ones it may use or already in the template; one group per brigade, battalions in the open rows (or where the
        /// template already had them), at most 5 support companies, one of each type; a unique name.</summary>
        public bool CanSave(string tag, string original, Hoi4TemplateDesign design, out string why)
        {
            why = null;
            if (design == null || !templatesByTag.ContainsKey(tag)) { why = "No templates"; return false; }
            string name = design.Name?.Trim();
            if (string.IsNullOrEmpty(name)) { why = "The template needs a name"; return false; }
            if (name != original && HasTemplate(tag, name)) { why = "Another template has this name"; return false; }
            if (design.Regiments.Count == 0) { why = "The template needs at least one battalion"; return false; }
            if (original != null && !HasTemplate(tag, original)) { why = "The template no longer exists"; return false; }
            var had = original != null ? Design(tag, original) : null;
            var allowed = new HashSet<string>(SubUnitsOf(tag));
            if (had != null)
            {
                foreach (var s in had.Regiments) allowed.Add(s.Type);
                foreach (var s in had.Support) allowed.Add(s.Type);
                foreach (var s in had.Regimental) allowed.Add(s.Type);
            }
            var groups = new Dictionary<int, string>();
            var seen = new HashSet<(int, int)>();
            foreach (var s in design.Regiments)
            {
                if (SubUnit(s.Type) == null || !allowed.Contains(s.Type) || SubUnitGroup(s.Type) == "support") { why = $"{s.Type} can't be used"; return false; }
                bool hadSlot = had != null && had.RegimentAt(s.X, s.Y) != null;
                if (s.X < 0 || s.X >= Hoi4TemplateDesign.Columns || s.Y < 0 || (s.Y >= Hoi4TemplateDesign.OpenRows && !hadSlot) || !seen.Add((s.X, s.Y)))
                { why = "A battalion is outside the open slots"; return false; }
                string g = SubUnitGroup(s.Type);
                if (groups.TryGetValue(s.X, out string other) && other != g) { why = "A brigade can only hold one kind of battalion"; return false; }
                groups[s.X] = g;
            }
            if (design.Support.Count > Hoi4TemplateDesign.SupportRows) { why = "At most 5 support companies"; return false; }
            var types = new HashSet<string>();
            foreach (var s in design.Support)
            {
                if (SubUnit(s.Type) == null || !allowed.Contains(s.Type) || !IsDivisionalSupport(s.Type)) { why = $"{SubUnitName(s.Type)} can't be divisional support"; return false; }
                if (!types.Add(s.Type)) { why = "Only one support company of each type"; return false; }
            }
            var under = new HashSet<int>();
            foreach (var s in design.Regimental)
            {
                if (SubUnit(s.Type) == null || !allowed.Contains(s.Type)) { why = $"{s.Type} can't be used"; return false; }
                if (!under.Add(s.X) || design.ColumnCount(s.X) < Hoi4TemplateDesign.RegimentalBattalions)
                { why = $"Regimental support needs a brigade of {Hoi4TemplateDesign.RegimentalBattalions} battalions"; return false; }
                if (!CanSupportBrigade(s.Type, groups.TryGetValue(s.X, out string g) ? g : null))
                { why = $"{SubUnitName(s.Type)} can't support that brigade"; return false; }
            }
            return true;
        }

        /// <summary>A support company for the divisional support column (not divisional = no).</summary>
        public bool IsDivisionalSupport(string name) => SubUnitGroup(name) == "support" && ((bool?)SubUnit(name)?["divisional"] ?? true);

        /// <summary>A regimental support company that may go under a brigade of the group (allowed_battalion_groups).</summary>
        public bool CanSupportBrigade(string name, string group)
        {
            var s = SubUnit(name);
            if (s == null || group == null || !((bool?)s["regimental"] ?? false)) return false;
            return s["battalion_groups"] is JArray groups && groups.Any(g => (string)g == group);
        }

        /// <summary>Raised after a template is saved: (tag, old name or null for a new one, new name).</summary>
        public event System.Action<string, string, string> TemplateSaved;

        /// <summary>Save a design (callers check CanSave): a new template, or over ORIGINAL (renaming it when the name
        /// changed). Divisions of the template take the new layout: equipment and men past what it needs go back to the
        /// stockpile and the manpower pool, strength and organisation stay at the same share of their new maximum.</summary>
        public void SaveTemplate(GameState gameState, ushort country, string original, Hoi4TemplateDesign design)
        {
            string tag = gameState.Countries.GetCountryTag(country);
            var templates = templatesByTag[tag];
            string name = design.Name.Trim();
            var regiments = design.RegimentTypes();
            var support = design.SupportTypes();
            var regimentalOrdered = new List<Hoi4TemplateDesign.Slot>(design.Regimental);
            regimentalOrdered.Sort((a, b) => a.X.CompareTo(b.X));
            var ordered = new List<Hoi4TemplateDesign.Slot>(design.Regiments);
            ordered.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            var supportOrdered = new List<Hoi4TemplateDesign.Slot>(design.Support);
            supportOrdered.Sort((a, b) => a.Y.CompareTo(b.Y));

            var tj = original != null && templates[original] is JObject old ? (JObject)old.DeepClone() : new JObject();
            tj["regiments"] = new JArray(regiments);
            tj["support"] = new JArray(support);
            tj["grid"] = new JArray(ordered.Select(s => new JArray(s.X, s.Y)));
            tj["support_grid"] = new JArray(supportOrdered.Select(s => s.Y));
            tj["regimental_support"] = new JArray(regimentalOrdered.Select(s => s.Type));
            tj["regimental_grid"] = new JArray(regimentalOrdered.Select(s => s.X));
            tj["main"] = MainOf(regiments, subUnitData);
            tj["priority"] = Mathf.Clamp(design.Priority, Reserve, Elite);
            if (tj["next_number"] == null) tj["next_number"] = 1;

            if (original != null && original != name)
            {
                // keep the template's place in the list under its new name
                var rebuilt = new JObject();
                foreach (var p in templates.Properties()) rebuilt[p.Name == original ? name : p.Name] = p.Name == original ? tj : p.Value;
                templates.RemoveAll();
                foreach (var p in rebuilt.Properties()) templates[p.Name] = p.Value;
                if (nextNumber.TryGetValue(tag + "/" + original, out int n)) { nextNumber.Remove(tag + "/" + original); nextNumber[tag + "/" + name] = n; }
            }
            else templates[name] = tj;
            templatePriority.Remove(tag + "/" + (original ?? name));
            templatePriority.Remove(tag + "/" + name);

            if (original != null)
                foreach (var d in divisions.Values)
                    if (d.Tag == tag && d.Template == original) Restructure(gameState, country, d, name, tj);
            TemplateSaved?.Invoke(tag, original, name);
            Core.Recording.SessionRecorder.Note("designer", original == null ? "template_new" : "template_saved", country, 0, 0, 0,
                $"{name}: {string.Join(" ", regiments)} | {string.Join(" ", support)} | {string.Join(" ", design.RegimentalTypes())}" + (original != null && original != name ? $" (was {original})" : ""));
        }

        /// <summary>A division (in the field or in training) takes its template's new layout.</summary>
        public void Restructure(GameState gameState, ushort country, Hoi4Division d, string template, JObject tj = null)
        {
            tj = tj ?? (HasTemplate(d.Tag, template) ? (JObject)templatesByTag[d.Tag][template] : null);
            if (tj == null) return;
            FP strengthShare = d.MaxStrength > FP.Zero ? d.Strength / d.MaxStrength : FP.One;
            FP orgShare = d.MaxOrganisation > FP.Zero ? d.Organisation / d.MaxOrganisation : FP.One;
            var oldHave = d.Have ?? new FP[0];
            d.Template = template;
            ApplyStructure(d, tj, subUnitData);
            var resources = gameState.Resources;
            d.Have = new FP[d.Need.Length];
            for (int a = 0; a < d.Need.Length; a++)
            {
                FP had = a < oldHave.Length ? oldHave[a] : FP.Zero;
                d.Have[a] = FP.Min(had, d.Need[a]);
                FP surplus = had - d.Have[a];
                if (surplus > FP.Zero) Hoi4Equipment.Return(gameState, country, d, a, surplus); // the same mix of versions
                if (d.Have[a] <= FP.Zero) d.Versions?[a]?.Clear();
            }
            ApplyEquipmentStats(d);
            FP men = FP.FromInt(d.Manpower);
            if (d.Men > men)
            {
                resources.SetResource(country, Hoi4Manpower.ResourceId, resources.GetResource(country, Hoi4Manpower.ResourceId) + (d.Men - men));
                d.Men = men;
            }
            d.Strength = FP.Min(d.MaxStrength * strengthShare, d.FullStrength);
            d.Organisation = d.MaxOrganisation * orgShare;
        }

        /// <summary>The template's division name format ("%d Infantry Division"), null when it has none.</summary>
        public string NameFormat(string tag, string template) =>
            HasTemplate(tag, template) ? (string)templatesByTag[tag][template]["name_format"] : null;

        /// <summary>The template's main regiment type (HOI4 shows a division by it; its icon), null when unknown.</summary>
        public string TemplateMain(string tag, string template) =>
            HasTemplate(tag, template) ? (string)templatesByTag[tag][template]["main"] : null;

        /// <summary>Days a division of the template trains: its slowest sub-unit's training_time (HOI4).</summary>
        /// <summary>Men a division of the template has at full strength (0 when it doesn't exist).</summary>
        public int TemplateManpower(string tag, string template)
        {
            var d = Design(tag, template);
            return d == null ? 0 : Totals(d.RegimentTypes().ToList(), d.SupportTypes().ToList(), d.RegimentalTypes().ToList()).manpower;
        }

        // Manpower and training days by composition (regiments | support | regimental): the deployment AI asked for
        // them for every template and line every day, each a full Sum over the sub-units' JSON. Keyed by the
        // composition itself, so a template saved with new battalions finds its own entry (nothing goes stale)
        readonly Dictionary<string, (int manpower, int days)> totalsCache = new Dictionary<string, (int, int)>();

        (int manpower, int days) Totals(List<string> regiments, List<string> support, List<string> regimental)
        {
            string key = string.Join(",", regiments) + "|" + string.Join(",", support) + "|" + string.Join(",", regimental);
            if (totalsCache.TryGetValue(key, out var v)) return v;
            var st = Sum(regiments, support, regimental, subUnitData ?? new JObject());
            return totalsCache[key] = (st.Manpower, st.TrainingDays);
        }

        public int TrainingDays(string tag, string template)
        {
            if (!HasTemplate(tag, template) || subUnitData == null) return 0;
            var tj = (JObject)templatesByTag[tag][template];
            return Totals(tj["regiments"]?.Select(x => (string)x).ToList() ?? new List<string>(),
                          tj["support"]?.Select(x => (string)x).ToList() ?? new List<string>(),
                          tj["regimental_support"]?.Select(x => (string)x).ToList() ?? new List<string>()).days;
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
            if (root["sub_unit_groups"] is JObject groups)
                foreach (var g in groups.Properties()) groupNames[g.Name] = (string)g.Value;

            var archetypes = new SortedSet<string>(System.StringComparer.Ordinal);
            foreach (var s in subUnits.Properties())
                if (s.Value["need"] is JObject need)
                    foreach (var a in need.Properties()) archetypes.Add(a.Name);
            Archetypes = archetypes.ToArray();
            archetypeIndex = new Dictionary<string, int>();
            for (int i = 0; i < Archetypes.Length; i++) archetypeIndex[Archetypes[i]] = i;

            Hoi4.Production.Hoi4EquipmentCatalog.EnsureLoaded();
            fallbackEquipment = new Hoi4.Production.Hoi4EquipmentCatalog.Entry[Archetypes.Length];
            if (root["archetype_stats"] is JObject archStats)
                for (int i = 0; i < Archetypes.Length; i++)
                    if (archStats[Archetypes[i]] is JObject st)
                        fallbackEquipment[i] = Hoi4.Production.Hoi4EquipmentCatalog.Entry.FromStats(Archetypes[i], Archetypes[i], st);
            startVersions.Clear();
            foreach (var country in countries.Properties())
            {
                var start = new Hoi4.Production.Hoi4EquipmentCatalog.Entry[Archetypes.Length];
                var eq = country.Value["equipment"] as JObject;
                for (int i = 0; i < Archetypes.Length; i++)
                    start[i] = Hoi4.Production.Hoi4EquipmentCatalog.Get((string)eq?[Archetypes[i]]?["equipment"])
                               ?? Hoi4.Production.Hoi4EquipmentCatalog.Oldest(Archetypes[i]); // none buildable (Finland's light tanks): the oldest design
                startVersions[country.Name] = start;
            }

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
                subUnitsByTag[tag] = (country.Value["sub_units"] as JArray)?.Select(x => (string)x).ToList() ?? new List<string>();

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
        // per country the version of each archetype its 1936 divisions and stockpile hold (its newest buildable)
        static readonly Dictionary<string, Hoi4.Production.Hoi4EquipmentCatalog.Entry[]> startVersions = new Dictionary<string, Hoi4.Production.Hoi4EquipmentCatalog.Entry[]>();
        // per archetype its newest 1936 design's stats: for archetypes no catalogue version covers (special projects)
        static Hoi4.Production.Hoi4EquipmentCatalog.Entry[] fallbackEquipment = new Hoi4.Production.Hoi4EquipmentCatalog.Entry[0];

        /// <summary>The version of an archetype the country started with (its newest buildable in 1936), null when
        /// none is known.</summary>
        public static Hoi4.Production.Hoi4EquipmentCatalog.Entry StartVersion(string tag, int archetype) =>
            tag != null && startVersions.TryGetValue(tag, out var v) && archetype >= 0 && archetype < v.Length ? v[archetype] : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            startVersions.Clear();
            fallbackEquipment = new Hoi4.Production.Hoi4EquipmentCatalog.Entry[0];
        }

        /// <summary>
        /// A division's attack, defence, breakthrough, hardness, armour, piercing and reliability from the equipment
        /// versions it holds: per archetype the versions' stats weighted by amount (no version held: its country's
        /// start version, else the archetype's 1936 design), per sub-unit its equipment's sum x (1 + its own factor)
        /// for attack and defence, the highest hardness, armour and piercing, the average reliability; per division
        /// the sub-units' sums, hardness and reliability averaged, armour 30% best + 70% average, piercing 40% best +
        /// 60% average (Sum's rules for the template's 1936 numbers, which this replaces).
        /// </summary>
        public static void ApplyEquipmentStats(Hoi4Division d)
        {
            if (d?.Parts == null || d.Parts.Length == 0) return;
            FP soft = FP.Zero, hard = FP.Zero, def = FP.Zero, bt = FP.Zero, hardness = FP.Zero, rel = FP.Zero;
            FP armorMax = FP.Zero, armorSum = FP.Zero, pierceMax = FP.Zero, pierceSum = FP.Zero;
            foreach (var p in d.Parts)
            {
                FP s = FP.Zero, h = FP.Zero, df = FP.Zero, b = FP.Zero, hd = FP.Zero, ar = FP.Zero, ap = FP.Zero, r = FP.Zero;
                int relN = 0;
                foreach (int a in p.Archetypes)
                {
                    var mix = d.Versions != null && a < d.Versions.Length ? d.Versions[a] : null;
                    FP es = FP.Zero, eh = FP.Zero, ed = FP.Zero, eb = FP.Zero, ehd = FP.Zero, ear = FP.Zero, eap = FP.Zero, er = FP.Zero;
                    bool hasRel = false;
                    if (mix != null && mix.Count > 0)
                        for (int i = 0; i < mix.Count; i++)
                        {
                            var e = mix.Entry(i);
                            FP w = mix.Share(i);
                            es += e.SoftAttack * w; eh += e.HardAttack * w; ed += e.Defense * w; eb += e.Breakthrough * w;
                            ehd += e.Hardness * w; ear += e.ArmorValue * w; eap += e.ApAttack * w; er += e.Reliability * w;
                            hasRel |= e.HasReliability;
                        }
                    else
                    {
                        var e = StartVersion(d.Tag, a) ?? (a < fallbackEquipment.Length ? fallbackEquipment[a] : null);
                        if (e == null) continue;
                        es = e.SoftAttack; eh = e.HardAttack; ed = e.Defense; eb = e.Breakthrough;
                        ehd = e.Hardness; ear = e.ArmorValue; eap = e.ApAttack; er = e.Reliability; hasRel = e.HasReliability;
                    }
                    s += es; h += eh; df += ed; b += eb;
                    hd = FP.Max(hd, ehd); ar = FP.Max(ar, ear); ap = FP.Max(ap, eap);
                    if (hasRel) { r += er; relN++; }
                }
                soft += s * (FP.One + p.SoftFactor);
                hard += h * (FP.One + p.HardFactor);
                def += df * (FP.One + p.DefenseFactor);
                bt += b * (FP.One + p.BreakthroughFactor);
                hardness += hd;
                armorMax = FP.Max(armorMax, ar); armorSum += ar;
                pierceMax = FP.Max(pierceMax, ap); pierceSum += ap;
                if (relN > 0) rel += r / relN;
            }
            FP n = FP.FromInt(d.Parts.Length);
            d.SoftAttack = soft; d.HardAttack = hard; d.Defense = def; d.Breakthrough = bt;
            d.Hardness = hardness / n;
            d.Armor = P3 * armorMax + P7 * armorSum / n;
            d.Piercing = P4 * pierceMax + P6 * pierceSum / n;
            d.Reliability = rel / n;
            d.VersionsChanged = false;
        }

        static Hoi4Division Build(ushort unitId, string tag, JToken d, string templateName, JObject template, JObject subUnits)
        {
            var division = new Hoi4Division
            {
                UnitId = unitId,
                Tag = tag,
                Name = (string)d["name"],
                Template = templateName,
                Experience = Num(d["experience"]),
            };
            ApplyStructure(division, template, subUnits);
            FP equipment = FP.Clamp(d["equipment"] != null ? Num(d["equipment"]) : FP.One, FP.Zero, FP.One);
            division.Have = new FP[division.Need.Length];
            for (int i = 0; i < division.Need.Length; i++)
            {
                division.Have[i] = division.Need[i] * equipment; // HOI4 start_equipment_factor
                // of its country's newest version (a real 1936 German division holds infantry equipment I, artillery
                // I, support equipment I: what its technologies unlock)
                var v = StartVersion(tag, i);
                if (division.Have[i] > FP.Zero && v != null) division.Mix(i).Set(v);
            }
            ApplyEquipmentStats(division);
            division.Men = FP.FromInt(division.Manpower); // HOI4 1936 divisions have their full manpower; equipment is what they lack
            division.Organisation = division.MaxOrganisation;
            division.Strength = division.FullStrength; // = max strength x start_equipment_factor
            return division;
        }

        /// <summary>A division's template-given numbers (sub-unit counts, main type, stats, equipment need and parts)
        /// from its template; what it has (equipment, men, strength, organisation) is left to the caller.</summary>
        static void ApplyStructure(Hoi4Division division, JObject template, JObject subUnits)
        {
            var regiments = template["regiments"]?.Select(x => (string)x).ToList() ?? new List<string>();
            var support = template["support"]?.Select(x => (string)x).ToList() ?? new List<string>();
            var regimental = template["regimental_support"]?.Select(x => (string)x).ToList() ?? new List<string>();
            var st = Sum(regiments, support, regimental, subUnits);
            division.MainType = (string)template["main"] ?? MainOf(regiments, subUnits);
            division.Regiments = regiments.Count;
            division.SupportCompanies = support.Count;
            division.MaxStrength = st.MaxStrength;
            division.Need = st.Need;
            division.Parts = st.Parts;
            division.MaxOrganisation = st.MaxOrganisation;
            division.SupplyUse = st.SupplyUse;
            division.Reliability = st.Reliability;
            division.CombatWidth = st.CombatWidth;
            division.SpeedKmh = st.SpeedKmh;
            division.Manpower = st.Manpower;
            division.SoftAttack = st.SoftAttack; division.HardAttack = st.HardAttack;
            division.Defense = st.Defense; division.Breakthrough = st.Breakthrough;
            division.Hardness = st.Hardness; division.Armor = st.Armor; division.Piercing = st.Piercing;
            division.Terrain = st.Terrain;
        }

        /// <summary>HOI4 shows a division by its highest-priority battalion (sub-unit `priority`).</summary>
        static string MainOf(List<string> regiments, JObject subUnits)
        {
            string main = null;
            int best = int.MinValue;
            foreach (string r in regiments)
            {
                int p = subUnits[r] is JObject s ? ((int?)s["priority"] ?? 0) : 0;
                if (main == null || p > best) { main = r; best = p; }
            }
            return main ?? "infantry";
        }

        /// <summary>What the sub-units add up to (Hoi4TemplateStats).</summary>
        public Hoi4TemplateStats Stats(IEnumerable<string> regiments, IEnumerable<string> support, IEnumerable<string> regimental) =>
            Sum(regiments.ToList(), support.ToList(), regimental.ToList(), subUnitData ?? new JObject());

        /// <summary>Regimental support companies count like support companies, except for speed (HOI4: they never
        /// affect it). How HOI4 scales them with their brigade's size isn't in the data; they count in full.
        /// Terrain modifiers (per terrain, river and amphibious): the line battalions' averaged, plus each support
        /// company's in full (a 1936 Panzer-Division's amphibious attack, 4 light tanks -0.4, 2 motorized -0.2 and
        /// engineers +0.15, is -0.183: with the -50% amphibious penalty the -68.33% a live HOI4 battle over a strait
        /// shows).</summary>
        static Hoi4TemplateStats Sum(List<string> regiments, List<string> support, List<string> regimental, JObject subUnits)
        {
            var st = new Hoi4TemplateStats { Need = new FP[Archetypes.Length] };
            var parts = new List<Hoi4Division.Part>();
            FP speed = FP.MaxValue, armorMax = FP.Zero, armorSum = FP.Zero, pierceMax = FP.Zero, pierceSum = FP.Zero;
            int index = -1;
            foreach (string name in regiments.Concat(support).Concat(regimental))
            {
                index++;
                if (!(subUnits[name] is JObject s)) continue;
                bool isRegimental = index >= regiments.Count + support.Count;
                var uses = new List<int>();
                if (s["need"] is JObject n)
                    foreach (var a in n.Properties())
                        if (archetypeIndex.TryGetValue(a.Name, out int i))
                        {
                            st.Need[i] += Num(a.Value);
                            uses.Add(i);
                        }
                var f = s["factors"] as JObject;
                parts.Add(new Hoi4Division.Part
                {
                    Weight = Val(s, "max_strength"), Archetypes = uses.ToArray(),
                    SoftFactor = f != null ? Val(f, "soft_attack") : FP.Zero, HardFactor = f != null ? Val(f, "hard_attack") : FP.Zero,
                    DefenseFactor = f != null ? Val(f, "defense") : FP.Zero, BreakthroughFactor = f != null ? Val(f, "breakthrough") : FP.Zero,
                });
                st.MaxStrength += Val(s, "max_strength");
                st.MaxOrganisation += Val(s, "max_organisation");
                st.Recovery += Val(s, "recovery");
                st.Recon += Val(s, "recon");
                st.Suppression += Val(s, "suppression");
                st.Weight += Val(s, "weight");
                st.SupplyUse += Val(s, "supply_use");
                st.Reliability += Val(s, "reliability");
                st.Initiative += Val(s, "initiative");
                st.Entrenchment += Val(s, "entrenchment");
                st.Manpower += (int?)s["manpower"] ?? 0;
                st.TrainingDays = System.Math.Max(st.TrainingDays, (int?)s["training_time"] ?? 0);
                if (!isRegimental && ((bool?)s["affects_speed"] ?? true)) speed = FP.Min(speed, Val(s, "speed"));
                st.SoftAttack += Val(s, "soft_attack"); st.HardAttack += Val(s, "hard_attack");
                st.AirAttack += Val(s, "air_attack");
                st.Defense += Val(s, "defense"); st.Breakthrough += Val(s, "breakthrough");
                st.Hardness += Val(s, "hardness");
                armorMax = FP.Max(armorMax, Val(s, "armor")); armorSum += Val(s, "armor");
                pierceMax = FP.Max(pierceMax, Val(s, "piercing")); pierceSum += Val(s, "piercing");
                st.Count++;
            }
            var terrain = new SortedDictionary<string, Hoi4TerrainModifier>(System.StringComparer.Ordinal);
            void AddTerrain(JObject s, FP scale)
            {
                if (!(s["terrain"] is JObject mods)) return;
                foreach (var m in mods.Properties())
                {
                    if (!(m.Value is JObject v)) continue;
                    terrain.TryGetValue(m.Name, out var t);
                    t.Attack += Num(v["attack"]) * scale;
                    t.Defence += Num(v["defence"]) * scale;
                    t.Movement += Num(v["movement"]) * scale;
                    terrain[m.Name] = t;
                }
            }
            int lineCount = 0;
            foreach (string name in regiments)
                if (subUnits[name] is JObject s) { st.CombatWidth += Val(s, "combat_width"); lineCount++; }
            if (lineCount > 0)
            {
                FP share = FP.One / lineCount;
                foreach (string name in regiments) if (subUnits[name] is JObject s) AddTerrain(s, share);
            }
            foreach (string name in support.Concat(regimental))
                if (subUnits[name] is JObject s) AddTerrain(s, FP.One);
            st.Terrain = new Dictionary<string, Hoi4TerrainModifier>(terrain);
            st.Parts = parts.ToArray();
            st.SpeedKmh = speed == FP.MaxValue ? FP.FromInt(4) : speed;
            if (st.Count > 0)
            {
                FP n = FP.FromInt(st.Count);
                st.MaxOrganisation /= n; st.Recovery /= n; st.Reliability /= n; st.Initiative /= n; st.Hardness /= n;
                st.Armor = P3 * armorMax + P7 * armorSum / n;
                st.Piercing = P4 * pierceMax + P6 * pierceSum / n;
            }
            return st;
        }
    }
}
