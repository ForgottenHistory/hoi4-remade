using System.Collections.Generic;
using Hoi4.Research;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: what a country's researched technologies add to one sub-unit (HOI4 technologies' unit blocks:
    /// `artillery_brigade = { soft_attack = 0.1 }`, `category_all_infantry = { ... }`, terrain blocks inside them).
    /// A block names the sub-unit or one of its categories (Hoi4Divisions.SubUnit "categories"); every block of every
    /// researched technology that fits counts, summed.
    /// - Soft and hard attack, defence, breakthrough, piercing, armour: fractions added to the sub-unit's own factor
    ///   on its equipment (battalion attack = equipment x (1 + own + technologies), as the real game's division
    ///   designer adds them: the reference workspace's hoi4rec/templates.py matches a 1939 Panzer-Division's soft and
    ///   hard attack exactly this way).
    /// - Organisation: added to the sub-unit's max_organisation (paratroopers II, +5).
    /// - Terrain: attack, defence and movement added to the sub-unit's terrain modifiers.
    /// Not used yet: entrenchment, recon, initiative, reliability and supply factors, speed, width, HP.
    /// Cached per country and sub-unit; a researched technology with blocks clears its country (Hoi4Divisions
    /// refreshes its divisions then).
    /// </summary>
    public static class Hoi4UnitBonuses
    {
        public sealed class Bonus
        {
            public FP SoftAttack, HardAttack, Defense, Breakthrough, Piercing, Armor, Organisation;
            public Dictionary<string, Hoi4TerrainModifier> Terrain; // null: none
            public bool Any;
        }

        static readonly Bonus None = new Bonus();
        static readonly Dictionary<ushort, Dictionary<string, Bonus>> cache = new Dictionary<ushort, Dictionary<string, Bonus>>();

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => cache.Clear();

        /// <summary>A country's technologies changed: its bonuses are worked out again on next use.</summary>
        public static void Clear(ushort country) => cache.Remove(country);

        public static void ClearAll() => cache.Clear();

        /// <summary>The country's bonuses for a sub-unit (an empty Bonus when none, or before research is ready).</summary>
        public static Bonus For(ushort country, string subUnit)
        {
            var research = Hoi4Research.Instance;
            if (subUnit == null || country == 0 || research == null || !research.IsReady) return None;
            if (!cache.TryGetValue(country, out var own)) cache[country] = own = new Dictionary<string, Bonus>();
            if (own.TryGetValue(subUnit, out var b)) return b;
            return own[subUnit] = Compute(research, country, subUnit);
        }

        static Bonus Compute(Hoi4Research research, ushort country, string subUnit)
        {
            var names = new HashSet<string> { subUnit };
            if (Hoi4Divisions.Instance?.SubUnit(subUnit)?["categories"] is JArray cats)
                foreach (var c in cats) names.Add((string)c);
            var b = new Bonus();
            foreach (var t in research.Technologies)
            {
                if (t.Bonuses == null || !research.Known(country, t.Index)) continue;
                foreach (var block in t.Bonuses.Properties())
                {
                    if (!names.Contains(block.Name) || !(block.Value is JObject s)) continue;
                    b.SoftAttack += Num(s["soft_attack"]); b.HardAttack += Num(s["hard_attack"]);
                    b.Defense += Num(s["defense"]); b.Breakthrough += Num(s["breakthrough"]);
                    b.Piercing += Num(s["ap_attack"]); b.Armor += Num(s["armor_value"]);
                    b.Organisation += Num(s["max_organisation"]);
                    if (s["terrain"] is JObject terrain)
                        foreach (var tp in terrain.Properties())
                        {
                            if (!(tp.Value is JObject v)) continue;
                            if (b.Terrain == null) b.Terrain = new Dictionary<string, Hoi4TerrainModifier>();
                            b.Terrain.TryGetValue(tp.Name, out var m);
                            m.Attack += Num(v["attack"]); m.Defence += Num(v["defence"]); m.Movement += Num(v["movement"]);
                            b.Terrain[tp.Name] = m;
                        }
                    b.Any = true;
                }
            }
            return b.Any ? b : None;
        }

        static FP Num(JToken t) => t != null && t.Type != JTokenType.Null && t.Type != JTokenType.Object ? FP.FromDouble((double)t) : FP.Zero;
    }
}
