using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Core;
using Core.Modding;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Production
{
    /// <summary>
    /// GAME: every buildable land equipment research can unlock (common/hoi4_equipment.json5, converter
    /// write_equipment_catalog): archetype, parent, year, build cost, combat stats, names and pictures, and which
    /// technologies unlock what. Also HOI4's production efficiency retention when a line changes equipment.
    /// Tanks are designs (No Step Back): each country's history and default designs come from the file ("GER:Panzer
    /// II": chassis type, family, parent chassis, modules, upgrades), the AI's new ones are registered at runtime
    /// (Hoi4TankDesigns); the fixed tank designs (light_tank_equipment_N) are Legacy, only names and pictures.
    /// </summary>
    public static class Hoi4EquipmentCatalog
    {
        const string File_ = "common/hoi4_equipment.json5";
        const string PictureDir = "gfx/interface/production/equipment/";

        public sealed class Entry
        {
            public string Name;            // infantry_equipment_2
            public string Archetype;       // infantry_equipment
            public string Parent;          // infantry_equipment_1 (null: none)
            public int Year;
            public FP Cost;                // build_cost_ic
            public string General, ArchetypeName, Sprite;
            public readonly Dictionary<string, FP> Stats = new Dictionary<string, FP>();
            public int Order;              // tank designs: creation order (a later design of the same chassis is newer)
            public string Chassis, Family, ChassisParent; // tank designs: light_tank_chassis_2, its base tier, its parent
            public bool Legacy, Obsolete;  // a fixed tank design (no longer built) / a history design marked obsolete
            public string LegacyOf;        // tank designs: the fixed design of their chassis (names, pictures)
            public string Owner;           // tank designs: the country that made it
            public Hoi4TankDesigns.Design Design; // tank designs: type, modules, upgrades
            public bool IsDesign => Chassis != null;
            // the combat stats a division takes from it (Hoi4Divisions.RefreshStats)
            public FP SoftAttack, HardAttack, Defense, Breakthrough, Hardness, ArmorValue, ApAttack, Reliability;
            public bool HasReliability;

            /// <summary>Fill the combat fields from Stats.</summary>
            public Entry Resolve()
            {
                FP S(string k) => Stats.TryGetValue(k, out var v) ? v : FP.Zero;
                SoftAttack = S("soft_attack"); HardAttack = S("hard_attack"); Defense = S("defense");
                Breakthrough = S("breakthrough"); Hardness = S("hardness"); ArmorValue = S("armor_value");
                ApAttack = S("ap_attack"); Reliability = S("reliability");
                HasReliability = Stats.ContainsKey("reliability");
                return this;
            }

            /// <summary>An entry of the stats in a JSON object ({"soft_attack": 3, ...}).</summary>
            public static Entry FromStats(string name, string archetype, JObject stats)
            {
                var e = new Entry { Name = name, Archetype = archetype, General = name };
                if (stats != null) foreach (var s in stats.Properties()) e.Stats[s.Name] = FP.FromDouble((double)s.Value);
                return e.Resolve();
            }
        }

        static readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        static readonly Dictionary<string, string[]> unlocks = new Dictionary<string, string[]>();
        static readonly Dictionary<string, Dictionary<string, string>> names = new Dictionary<string, Dictionary<string, string>>();
        static readonly Dictionary<string, Dictionary<string, string>> sprites = new Dictionary<string, Dictionary<string, string>>();

        public static bool IsLoaded { get; private set; }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            entries.Clear(); unlocks.Clear(); names.Clear(); sprites.Clear();
            IsLoaded = false;
        }

        /// <summary>Load once (divisions and production both need it).</summary>
        public static void EnsureLoaded()
        {
            if (!IsLoaded) Load();
        }

        public static void Load()
        {
            Reset();
            string path = DataFileResolver.Resolve(File_);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4EquipmentCatalog: no {File_} (run Tools/hoi4_convert.py); research unlocks no equipment", "game_production");
                return;
            }
            string text = File.ReadAllText(path);
            var doc = JObject.Parse(text.Substring(text.IndexOf('{'))); // after the comment line
            if (doc["equipment"] is JObject eq)
                foreach (var p in eq.Properties())
                {
                    var o = (JObject)p.Value;
                    var e = new Entry
                    {
                        Name = p.Name, Archetype = (string)o["archetype"], Parent = (string)o["parent"],
                        Year = (int?)o["year"] ?? 0, Cost = FP.FromDouble((double?)o["cost"] ?? 0),
                        General = (string)o["general"] ?? p.Name, ArchetypeName = (string)o["archetype_name"],
                        Sprite = (string)o["sprite"], Order = (int?)o["order"] ?? 0,
                        Chassis = (string)o["chassis"], Family = (string)o["family"], ChassisParent = (string)o["chassis_parent"],
                        Legacy = (bool?)o["legacy"] ?? false, Obsolete = (bool?)o["obsolete"] ?? false, LegacyOf = (string)o["legacy_of"],
                    };
                    if (o["design"] is JObject d)
                    {
                        e.Design = Hoi4TankDesigns.Design.FromJson(d);
                        e.Owner = p.Name.Contains(":") ? p.Name.Substring(0, p.Name.IndexOf(':')) : null;
                    }
                    if (o["stats"] is JObject st) foreach (var s in st.Properties()) e.Stats[s.Name] = FP.FromDouble((double)s.Value);
                    entries[p.Name] = e.Resolve();
                }
            if (doc["unlocks"] is JObject un)
                foreach (var p in un.Properties())
                {
                    var list = new List<string>();
                    foreach (var v in (JArray)p.Value) list.Add((string)v);
                    unlocks[p.Name] = list.ToArray();
                }
            void PerCountry(JToken t, Dictionary<string, Dictionary<string, string>> into)
            {
                if (!(t is JObject o)) return;
                foreach (var c in o.Properties())
                {
                    var own = new Dictionary<string, string>();
                    foreach (var x in ((JObject)c.Value).Properties()) own[x.Name] = (string)x.Value;
                    into[c.Name] = own;
                }
            }
            PerCountry(doc["names"], names);
            PerCountry(doc["sprites"], sprites);
            IsLoaded = true;
            ArchonLogger.Log($"Hoi4EquipmentCatalog: {entries.Count} equipment, {unlocks.Count} unlocking technologies", "game_production");
        }

        public static Entry Get(string equipment) => equipment != null && entries.TryGetValue(equipment, out var e) ? e : null;

        static int registered = 100000; // runtime designs come after every file entry

        /// <summary>A design made during the game (Hoi4TankDesigns): it gets the next order, so it is newer than any
        /// other design of its chassis.</summary>
        public static void Register(Entry e)
        {
            e.Order = ++registered;
            entries[e.Name] = e;
        }

        /// <summary>The archetype's oldest design (earliest year, then cheapest), null when it has none.</summary>
        public static Entry Oldest(string archetype)
        {
            Entry best = null;
            foreach (var e in entries.Values)
                if (e.Archetype == archetype && !e.Legacy && (best == null || e.Year < best.Year || (e.Year == best.Year
                        && (e.Cost < best.Cost || (e.Cost == best.Cost && string.CompareOrdinal(e.Name, best.Name) < 0)))))
                    best = e;
            return best;
        }

        /// <summary>The equipment a technology makes buildable (empty: none).</summary>
        public static IReadOnlyList<string> Unlocks(string technology) =>
            technology != null && unlocks.TryGetValue(technology, out var u) ? u : System.Array.Empty<string>();

        /// <summary>Newer: later year, then a later design, then dearer (the converter's start rule).</summary>
        public static bool IsNewer(string candidate, string than)
        {
            var a = Get(candidate);
            var b = Get(than);
            if (a == null) return false;
            if (b == null) return true;
            return Compare(a, b) > 0;
        }

        /// <summary>Order of age: year, then creation order (designs), then cost, then name (the same on every machine).
        /// Positive when A is newer.</summary>
        public static int Compare(Entry a, Entry b)
        {
            if (a.Year != b.Year) return a.Year.CompareTo(b.Year);
            if (a.Order != b.Order) return a.Order.CompareTo(b.Order);
            if (a.Cost != b.Cost) return a.Cost > b.Cost ? 1 : -1;
            return string.CompareOrdinal(b.Name, a.Name);
        }

        /// <summary>The country's design name ("MAS-36"), else the general one.</summary>
        public static string Name(string tag, string equipment) =>
            tag != null && names.TryGetValue(tag, out var own) && own.TryGetValue(equipment, out var n) ? n : Get(equipment)?.General ?? equipment;

        /// <summary>The production picture's sprite name (the country's own first), null when none.</summary>
        public static string SpriteName(string tag, string equipment) =>
            equipment == null ? null
            : tag != null && sprites.TryGetValue(tag, out var own) && own.TryGetValue(equipment, out var x) ? x : Get(equipment)?.Sprite;

        /// <summary>The production picture's data-directory path (the country's own first).</summary>
        public static string Picture(string tag, string equipment)
        {
            string s = tag != null && sprites.TryGetValue(tag, out var own) && own.TryGetValue(equipment, out var x) ? x : Get(equipment)?.Sprite;
            return string.IsNullOrEmpty(s) ? null : PictureDir + s + ".png";
        }

        // ---- retention ----------------------------------------------------------------------------------

        static readonly FP Variant = FP.FromFraction(90, 100);     // ..._VARIANT_CHANGE_FACTOR: another design, same chassis
        static readonly FP Family = FP.FromFraction(70, 100);      // ..._FAMILY_CHANGE_FACTOR: same chassis, other model
        static readonly FP ParentChild = FP.FromFraction(30, 100); // ..._PARENT_CHANGE_FACTOR: direct up/downgrade
        static readonly FP SameArchetype = FP.FromFraction(20, 100); // ..._ARCHETYPE_CHANGE_FACTOR: indirect
        static readonly FP Other = FP.FromFraction(10, 100);       // any other change (wiki)
        static readonly Regex TankModel = new Regex(@"^(\w+?_tank)_(?:\w+_)?equipment_(\d+)$");

        /// <summary>Share of a line's efficiency kept when it switches from one equipment to another, before the
        /// retention bonus (HOI4 BASE_FACTORY_EFFICIENCY_*_CHANGE_FACTOR, wiki "Production efficiency retention").</summary>
        public static FP BaseRetention(string from, string to)
        {
            if (from == to) return FP.One;
            var a = Get(from);
            var b = Get(to);
            if (a == null || b == null) return Other;
            if (a.IsDesign && b.IsDesign) // tank designs: by chassis
            {
                if (a.Chassis == b.Chassis) return Variant;
                if (a.Family == b.Family) return Family;                 // light tank I -> light tank destroyer I
                if (a.Archetype == b.Archetype)
                    return a.ChassisParent == b.Chassis || b.ChassisParent == a.Chassis ? ParentChild : SameArchetype;
                return Other;
            }
            if (a.Archetype == b.Archetype)
                return a.Parent == to || b.Parent == from ? ParentChild : SameArchetype;
            // tanks: another model of the same chassis (light tank I -> light tank destroyer I)
            var ma = TankModel.Match(from);
            var mb = TankModel.Match(to);
            if (ma.Success && mb.Success && ma.Groups[1].Value == mb.Groups[1].Value && ma.Groups[2].Value == mb.Groups[2].Value)
                return Family;
            return Other;
        }

        /// <summary>Retention with the country's bonus (line_change_production_efficiency_factor): RET + (1 - RET) x
        /// bonus, at most 1. Measured on a real game: a 30% switch with +20% kept 44%, floored at the start
        /// efficiency (Tools/reference: production line switches in the daily country blocks).</summary>
        public static FP Retention(string from, string to, FP bonus)
        {
            FP r = BaseRetention(from, to);
            if (r == Variant) return r; // another variant always loses 10% (wiki): the retention bonus doesn't apply
            return FP.Min(FP.One, r + (FP.One - r) * FP.Max(FP.Zero, bonus));
        }
    }
}
