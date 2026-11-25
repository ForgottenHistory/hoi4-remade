using System.Collections.Generic;
using System.IO;
using Core;
using Core.Modding;
using Hoi4.Research;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Production
{
    /// <summary>
    /// GAME: HOI4 tank designs (No Step Back; common/hoi4_tank_designs.json5, converter write_tank_designs; prototype
    /// Tools/prototypes/tanks/tank_designs.py, whose rules this ports):
    /// - A design is a chassis type (a tier of a role: light_tank_chassis_2, light_tank_destroyer_chassis_1), a module
    ///   per slot and upgrade levels. Stats: the type's, plus each module's add_stats x (1 + the chassis'
    ///   module_add_stats_multiplier) and each upgrade's add_stats x level, all x (1 + the modules' multiply_stats +
    ///   the upgrades' own stats x level). Matches the wiki's chassis table and, in the reference workspace, the real
    ///   designer's division attacks.
    /// - Slots in the chassis' order (turrets first: they widen the gun slot); anti-air guns and rockets not on a
    ///   plain tank; a role design needs a module that allows its role (a tank destroyer an anti-tank gun).
    /// - What a country may use: its technologies' chassis (enable_equipments) and modules (enable_equipment_modules);
    ///   upgrade levels up to 4, more with the engine / armour technologies.
    /// - The AI (host only, every country but the player's: the player keeps its history designs until there is a
    ///   designer screen): HOI4's own design groups (common/ai_equipment: generic and GER, SOV, ENG, USA, FRA, ITA, JAP,
    ///   HUN), per role the enabled design of the highest priority it can build, then match value. It adopts one
    ///   (CreateTankDesignCommand) when the role has no design, only a default one (no gun), an older chassis, or its
    ///   own earlier design that research has changed; a history design stays until a newer chassis (the Soviet BT-7
    ///   until its file's newer light tank has its guns). Production then builds it for new lines and the production
    ///   AI switches its lines (90% for another design of the same chassis). Each country is looked at when a
    ///   technology of its unlocks a chassis or module, and every 30 days (war, factories). No army experience.
    /// Countries and designs go in id and file order: the same on every machine.
    /// </summary>
    public static class Hoi4TankDesigns
    {
        const string File_ = "common/hoi4_tank_designs.json5";
        const int CycleDays = 30;

        /// <summary>A design's make-up.</summary>
        public sealed class Design
        {
            public string Type;
            public readonly List<(string slot, string module)> Modules = new List<(string, string)>();
            public readonly List<(string upgrade, int level)> Upgrades = new List<(string, int)>();

            public static Design FromJson(JObject o)
            {
                var d = new Design { Type = (string)o["type"] };
                if (o["modules"] is JObject m) foreach (var p in m.Properties()) d.Modules.Add((p.Name, (string)p.Value));
                if (o["upgrades"] is JObject u) foreach (var p in u.Properties()) d.Upgrades.Add((p.Name, (int)p.Value));
                return d;
            }

            public bool Same(Design o)
            {
                if (o == null || o.Type != Type || o.Modules.Count != Modules.Count || o.Upgrades.Count != Upgrades.Count) return false;
                var mine = new Dictionary<string, string>();
                foreach (var (s, m) in Modules) mine[s] = m;
                foreach (var (s, m) in o.Modules) if (!mine.TryGetValue(s, out var x) || x != m) return false;
                var ups = new Dictionary<string, int>();
                foreach (var (u, l) in Upgrades) ups[u] = l;
                foreach (var (u, l) in o.Upgrades) if (!ups.TryGetValue(u, out int x) || x != l) return false;
                return true;
            }

            /// <summary>Same chassis and modules as O, and some upgrade lower than O's: a design not worth a retool
            /// (HOI4's AI specs add armour levels at war: without this every peace would remake every role).</summary>
            public bool OnlyLowerUpgrades(Design o)
            {
                if (o == null || o.Type != Type || o.Modules.Count != Modules.Count) return false;
                var mine = new Dictionary<string, string>();
                foreach (var (s, m) in Modules) mine[s] = m;
                foreach (var (s, m) in o.Modules) if (!mine.TryGetValue(s, out var x) || x != m) return false;
                var ups = new Dictionary<string, int>();
                foreach (var (u, l) in Upgrades) ups[u] = l;
                foreach (var (u, l) in o.Upgrades) if ((ups.TryGetValue(u, out int x) ? x : 0) < l) return true;
                return false;
            }

            public string Describe()
            {
                var sb = new System.Text.StringBuilder(Type);
                foreach (var (_, m) in Modules) sb.Append(' ').Append(m);
                foreach (var (u, l) in Upgrades) sb.Append(' ').Append(u.Replace("tank_nsb_", "").Replace("_upgrade", "")).Append('=').Append(l);
                return sb.ToString();
            }
        }

        sealed class TypeDef
        {
            public string Name, Archetype, Parent, Family, Legacy;
            public int Year;
            public Dictionary<string, FP> Stats, Boost;
            public HashSet<string> Roles;
        }

        sealed class ModuleDef
        {
            public string Name, Category, ForbidExact, Parent;
            public string[] Forbid;  // roles it may not go on
            public Dictionary<string, FP> Add, Mult;
            public Dictionary<string, string[]> Widen;
            public Dictionary<string, string> ForbidExactCategory;
            public string[] Allows;
            public int Depth; // its parent chain's length: the newest of a category has the longest
        }

        sealed class UpgradeDef
        {
            public Dictionary<string, FP> Add, Mult;
            public int Max;
            public List<(int level, Hoi4ResearchAI.Trigger need)> Levels;
        }

        sealed class AiDesign
        {
            public string Name, Type;
            public JObject Priority;
            public Hoi4ResearchAI.Trigger Enable;
            public double Match;
            public List<(string slot, string op, JToken want)> Modules;
            public List<(string upgrade, JObject spec)> Upgrades;
            public string[] Allowed;
        }

        sealed class AiGroup
        {
            public string Name;
            public string[] Roles;
            public HashSet<string> Blocked, Available;
            public JObject Priority;
            public List<AiDesign> Designs;
        }

        static readonly Dictionary<string, TypeDef> types = new Dictionary<string, TypeDef>();
        static readonly Dictionary<string, List<(string slot, bool required, string[] cats)>> slots =
            new Dictionary<string, List<(string, bool, string[])>>();
        static readonly Dictionary<string, ModuleDef> modules = new Dictionary<string, ModuleDef>();
        static readonly List<ModuleDef> moduleOrder = new List<ModuleDef>();
        static readonly Dictionary<string, UpgradeDef> upgrades = new Dictionary<string, UpgradeDef>();
        static readonly Dictionary<string, string[]> techModules = new Dictionary<string, string[]>();
        static readonly List<AiGroup> groups = new List<AiGroup>();
        static readonly Dictionary<JToken, Hoi4ResearchAI.Trigger> triggerCache = new Dictionary<JToken, Hoi4ResearchAI.Trigger>();
        static readonly HashSet<ushort> due = new HashSet<ushort>();      // a technology changed what it may use
        static readonly Dictionary<ushort, int> designCount = new Dictionary<ushort, int>();
        static bool loaded, compiled, subscribed, started;

        public static bool IsLoaded => loaded;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            types.Clear(); slots.Clear(); modules.Clear(); moduleOrder.Clear(); upgrades.Clear(); techModules.Clear();
            groups.Clear(); triggerCache.Clear(); due.Clear(); designCount.Clear();
            loaded = compiled = subscribed = started = false;
        }

        static FP Num(JToken t) => t != null && t.Type != JTokenType.Null ? FP.FromDouble((double)t) : FP.Zero;

        static Dictionary<string, FP> Stats(JToken t)
        {
            var d = new Dictionary<string, FP>();
            if (t is JObject o) foreach (var p in o.Properties()) d[p.Name] = Num(p.Value);
            return d;
        }

        static string[] Strings(JToken t)
        {
            var l = new List<string>();
            if (t is JArray a) foreach (var x in a) l.Add((string)x);
            return l.ToArray();
        }

        /// <summary>Load once (Hoi4Production.Start). Bad data logs an error and leaves no designer; it never
        /// stops production starting.</summary>
        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            try { Load(); }
            catch (System.Exception ex)
            {
                ArchonLogger.LogError($"Hoi4TankDesigns: {File_} failed to load, no tank designer: {ex}", "game_production");
                types.Clear(); groups.Clear();
            }
        }

        static void Load()
        {
            string path = DataFileResolver.Resolve(File_);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4TankDesigns: no {File_} (run Tools/hoi4_convert.py); no tank designer", "game_production");
                return;
            }
            string text = File.ReadAllText(path);
            var doc = JObject.Parse(text.Substring(text.IndexOf('{')));
            if (doc["types"] is JObject ts)
                foreach (var p in ts.Properties())
                {
                    var o = (JObject)p.Value;
                    types[p.Name] = new TypeDef
                    {
                        Name = p.Name, Archetype = (string)o["archetype"], Parent = (string)o["parent"], Family = (string)o["family"],
                        Legacy = (string)o["legacy"], Year = (int?)o["year"] ?? 0, Stats = Stats(o["stats"]), Boost = Stats(o["boost"]),
                        Roles = new HashSet<string>(Strings(o["roles"])),
                    };
                }
            if (doc["archetypes"] is JObject archs)
                foreach (var p in archs.Properties())
                {
                    var list = new List<(string, bool, string[])>();
                    if (p.Value["slots"] is JArray ss)
                        foreach (var s in ss) list.Add(((string)s[0], (bool)s[1], Strings(s[2])));
                    slots[p.Name] = list;
                }
            if (doc["modules"] is JObject ms)
                foreach (var p in ms.Properties())
                {
                    var o = (JObject)p.Value;
                    var m = new ModuleDef
                    {
                        Name = p.Name, Category = (string)o["category"], Forbid = Strings(o["forbid"]), ForbidExact = (string)o["forbid_exact"],
                        Parent = (string)o["parent"], Add = Stats(o["add"]), Mult = Stats(o["mult"]), Allows = Strings(o["allows"]),
                        Widen = new Dictionary<string, string[]>(), ForbidExactCategory = new Dictionary<string, string>(),
                    };
                    if (o["widen"] is JObject w) foreach (var x in w.Properties()) m.Widen[x.Name] = Strings(x.Value);
                    if (o["forbid_exact_category"] is JObject f) foreach (var x in f.Properties()) m.ForbidExactCategory[x.Name] = (string)x.Value;
                    modules[p.Name] = m;
                    moduleOrder.Add(m);
                }
            foreach (var m in moduleOrder)
                for (var x = m; x?.Parent != null && m.Depth < 16; x = modules.TryGetValue(x.Parent, out var px) ? px : null) m.Depth++;
            if (doc["upgrades"] is JObject us)
                foreach (var p in us.Properties())
                    upgrades[p.Name] = new UpgradeDef { Add = Stats(p.Value["add"]), Mult = Stats(p.Value["mult"]), Max = (int?)p.Value["max"] ?? 20 };
            if (doc["tech_modules"] is JObject tm)
                foreach (var p in tm.Properties()) techModules[p.Name] = Strings(p.Value);
            if (doc["ai"] is JArray ai)
                foreach (var g in ai)
                {
                    var group = new AiGroup
                    {
                        Name = (string)g["group"], Roles = Strings(g["roles"]), Blocked = new HashSet<string>(Strings(g["blocked"])),
                        Available = new HashSet<string>(Strings(g["available"])), Priority = g["priority"] as JObject,
                        Designs = new List<AiDesign>(),
                    };
                    foreach (var d in (JArray)g["designs"])
                    {
                        var ad = new AiDesign
                        {
                            Name = (string)d["name"], Type = (string)d["type"], Priority = d["priority"] as JObject,
                            Match = (double?)d["match"] ?? 0, Allowed = Strings(d["allowed"]),
                            Modules = new List<(string, string, JToken)>(), Upgrades = new List<(string, JObject)>(),
                        };
                        ad.Enable = null; // compiled with the research AI (Compile)
                        if (d["modules"] is JArray mm) foreach (var x in mm) ad.Modules.Add(((string)x[0], (string)x[1], x[2]));
                        if (d["upgrades"] is JObject uu) foreach (var x in uu.Properties()) ad.Upgrades.Add((x.Name, x.Value as JObject));
                        group.Designs.Add(ad);
                        enables[ad] = d["enable"] as JArray;
                    }
                    groups.Add(group);
                }
            upgradeLevels = doc["upgrades"] as JObject;
            ArchonLogger.Log($"Hoi4TankDesigns: {types.Count} chassis types, {modules.Count} modules, {groups.Count} AI design groups", "game_production");
        }

        static readonly Dictionary<AiDesign, JArray> enables = new Dictionary<AiDesign, JArray>();
        static JObject upgradeLevels;

        /// <summary>Triggers compile against research's technologies, once it is ready.</summary>
        static void Compile()
        {
            compiled = true;
            foreach (var kv in enables) kv.Key.Enable = kv.Value != null ? Hoi4ResearchAI.CompileTrigger(kv.Value) : null;
            if (upgradeLevels != null)
                foreach (var p in upgradeLevels.Properties())
                {
                    var levels = new List<(int, Hoi4ResearchAI.Trigger)>();
                    if (p.Value["levels"] is JArray ls)
                        foreach (var l in ls) levels.Add(((int)l[0], Hoi4ResearchAI.CompileTrigger(l[1] as JArray)));
                    upgrades[p.Name].Levels = levels;
                }
        }

        // ---- one design ----------------------------------------------------------------------------------

        /// <summary>A design's stats (soft_attack, hard_attack, ap_attack, defense, breakthrough, armor_value,
        /// hardness, maximum_speed, reliability, build_cost_ic, ...); null for an unknown chassis type.</summary>
        public static Dictionary<string, FP> StatsOf(Design d)
        {
            if (d == null || !types.TryGetValue(d.Type, out var t)) return null;
            var st = new Dictionary<string, FP>(t.Stats);
            var mult = new Dictionary<string, FP>();
            void Add(Dictionary<string, FP> into, string k, FP v) => into[k] = (into.TryGetValue(k, out var x) ? x : FP.Zero) + v;
            foreach (var (_, name) in d.Modules)
            {
                if (!modules.TryGetValue(name, out var m)) continue;
                foreach (var kv in m.Add) Add(st, kv.Key, kv.Value * (FP.One + (t.Boost.TryGetValue(kv.Key, out var b) ? b : FP.Zero)));
                foreach (var kv in m.Mult) Add(mult, kv.Key, kv.Value);
            }
            foreach (var (name, level) in d.Upgrades)
            {
                if (!upgrades.TryGetValue(name, out var u)) continue;
                FP lv = FP.FromInt(level);
                foreach (var kv in u.Add) Add(st, kv.Key, kv.Value * lv);
                foreach (var kv in u.Mult) Add(mult, kv.Key, kv.Value * lv);
            }
            var outp = new Dictionary<string, FP>();
            foreach (var kv in st) outp[kv.Key] = kv.Value * (FP.One + (mult.TryGetValue(kv.Key, out var m2) ? m2 : FP.Zero));
            return outp;
        }

        static bool Fits(TypeDef t, string slot, ModuleDef m, Dictionary<string, string> chosen)
        {
            if (!slots.TryGetValue(t.Archetype, out var ss)) return false;
            string[] cats = null;
            foreach (var s in ss) if (s.slot == slot) { cats = s.cats; break; }
            if (cats == null) return false;
            bool allowed = System.Array.IndexOf(cats, m.Category) >= 0;
            foreach (var kv in chosen) // a turret widens the gun slot
            {
                if (allowed) break;
                if (kv.Key == slot || kv.Value == null || !modules.TryGetValue(kv.Value, out var other)) continue;
                if (other.Widen.TryGetValue(slot, out var w) && System.Array.IndexOf(w, m.Category) >= 0) allowed = true;
            }
            if (!allowed) return false;
            foreach (string f in m.Forbid) if (t.Roles.Contains(f)) return false;
            if (m.ForbidExact != null && t.Roles.Count == 1 && t.Roles.Contains(m.ForbidExact)) return false; // AA guns, rockets
            foreach (var kv in chosen) // gun categories some turrets bar on a plain tank
            {
                if (kv.Value == null || !modules.TryGetValue(kv.Value, out var other)) continue;
                if (other.ForbidExactCategory.TryGetValue(m.Category, out var role) && t.Roles.Count == 1 && t.Roles.Contains(role)) return false;
                if (m.ForbidExactCategory.TryGetValue(other.Category, out var role2) && t.Roles.Count == 1 && t.Roles.Contains(role2)) return false;
            }
            return true;
        }

        static bool HasRoleModule(TypeDef t, Dictionary<string, string> chosen)
        {
            bool needs = false;
            foreach (var r in t.Roles) if (r != "armor") needs = true;
            if (!needs) return true;
            foreach (var kv in chosen)
                if (kv.Value != null && modules.TryGetValue(kv.Value, out var m))
                    foreach (var a in m.Allows) if (a != "armor" && t.Roles.Contains(a)) return true;
            return false;
        }

        // ---- what a country may use --------------------------------------------------------------------

        static void Unlocked(ushort country, HashSet<string> outTypes, HashSet<string> outModules)
        {
            var research = Hoi4Research.Instance;
            foreach (var t in research.Technologies)
            {
                if (!research.Known(country, t.Index)) continue;
                foreach (string e in t.Equipment) if (types.ContainsKey(e)) outTypes.Add(e);
                if (techModules.TryGetValue(t.Id, out var ms)) foreach (string m in ms) outModules.Add(m);
            }
        }

        static int UpgradeCap(string name, ushort country, int day)
        {
            if (!upgrades.TryGetValue(name, out var u)) return 0;
            if (u.Levels != null)
                foreach (var (level, need) in u.Levels) // levels from an entry up need its technology
                    if (Hoi4ResearchAI.Evaluate(need, country, day) != 1) return System.Math.Min(u.Max, level - 1);
            return u.Max;
        }

        /// <summary>The design an AI design spec makes for the country, or null when it can't.</summary>
        static Design Build(AiDesign spec, ushort country, int day, HashSet<string> haveTypes, HashSet<string> haveModules)
        {
            string typeName = spec.Type;
            if (slots.ContainsKey(typeName)) // an archetype: its newest unlocked tier
            {
                TypeDef best = null;
                foreach (string n in haveTypes)
                    if (types[n].Archetype == typeName && (best == null || types[n].Year > best.Year
                            || (types[n].Year == best.Year && string.CompareOrdinal(n, best.Name) > 0))) best = types[n];
                typeName = best?.Name;
            }
            if (typeName == null || !haveTypes.Contains(typeName) || !types.TryGetValue(typeName, out var t)) return null;
            var ss = slots[t.Archetype];
            var chosen = new Dictionary<string, string>();
            var wants = new Dictionary<string, (string op, JToken want)>();
            foreach (var (slot, op, want) in spec.Modules) wants[slot] = (op, want);
            foreach (var (slot, required, _) in ss) // the chassis' order: turrets first
            {
                if (!wants.TryGetValue(slot, out var w)) continue;
                if (w.want.Type == JTokenType.String && (string)w.want == "empty" && w.op == "=") { chosen[slot] = null; continue; }
                var options = new List<string>();
                if (w.want is JObject wo)
                {
                    if (wo["any_of"] is JArray any) foreach (var x in any) options.Add((string)x);
                    else if (wo["module"] != null) options.Add((string)wo["module"]);
                }
                else options.Add((string)w.want);
                string pick = null;
                foreach (string o in options)
                {
                    if (modules.TryGetValue(o, out var m))
                    {
                        if (haveModules.Contains(o) && Fits(t, slot, m, chosen)) pick = o;
                    }
                    else // a category: its newest available module that fits
                    {
                        ModuleDef best = null;
                        foreach (var c in moduleOrder)
                            if (c.Category == o && haveModules.Contains(c.Name) && Fits(t, slot, c, chosen) && (best == null || c.Depth > best.Depth))
                                best = c;
                        pick = best?.Name;
                    }
                    if (pick != null) break;
                }
                if (pick == null)
                {
                    if (required) return null;
                    continue;
                }
                chosen[slot] = pick;
            }
            foreach (string name in spec.Allowed) // what's left, in the list's order
            {
                if (!haveModules.Contains(name) || !modules.TryGetValue(name, out var m)) continue;
                foreach (var (slot, _, _) in ss)
                    if (!chosen.ContainsKey(slot) && Fits(t, slot, m, chosen)) { chosen[slot] = name; break; }
            }
            foreach (var (slot, required, _) in ss)
                if (required && (!chosen.TryGetValue(slot, out var c) || c == null)) return null;
            if (!HasRoleModule(t, chosen)) return null;
            var d = new Design { Type = typeName };
            foreach (var (slot, _, _) in ss)
                if (chosen.TryGetValue(slot, out var c) && c != null) d.Modules.Add((slot, c));
            foreach (var (name, upSpec) in spec.Upgrades)
            {
                double level = (double?)upSpec?["base"] ?? 0;
                if (upSpec?["modifiers"] is JArray mods)
                    foreach (var mo in mods)
                    {
                        if (!triggerCache.TryGetValue(mo, out var trig)) triggerCache[mo] = trig = Hoi4ResearchAI.CompileTrigger(mo["if"] as JArray);
                        if (Hoi4ResearchAI.Evaluate(trig, country, day) == 1) level += (double?)mo["add"] ?? 0;
                    }
                int lv = System.Math.Min((int)level, UpgradeCap(name, country, day));
                if (lv > 0) d.Upgrades.Add((name, lv));
            }
            return d;
        }

        // ---- the AI --------------------------------------------------------------------------------------

        /// <summary>Daily, host only (Hoi4Simulation): each country but the player's when one of its technologies
        /// unlocked a chassis or module, and every 30 days.</summary>
        public static void SimulateDay(int day, ushort player)
        {
            var research = Hoi4Research.Instance;
            var production = Hoi4Production.Instance;
            if (!loaded || types.Count == 0 || research == null || !research.IsReady || production == null || !production.IsReady) return;
            if (!compiled) Compile();
            if (!subscribed) { research.Researched += OnResearched; subscribed = true; }
            var gameState = Engine.ArchonEngine.Instance.GameState;
            var countries = new List<ushort>(production.Countries); // countries on the map (research lists every tag)
            countries.Sort();
            foreach (ushort c in countries)
            {
                if (c == player) continue;
                if (!started || due.Contains(c) || c % CycleDays == day % CycleDays) DesignFor(gameState, c, day);
            }
            due.Clear();
            started = true;
        }

        static void OnResearched(ushort country, int tech)
        {
            var t = Hoi4Research.Instance?.Tech(tech);
            if (t == null) return;
            bool matters = techModules.ContainsKey(t.Id);
            foreach (string e in t.Equipment) matters |= types.ContainsKey(e);
            if (matters) due.Add(country);
        }

        static void DesignFor(GameState gameState, ushort country, int day)
        {
            var haveTypes = new HashSet<string>();
            var haveModules = new HashSet<string>();
            Unlocked(country, haveTypes, haveModules);
            if (haveTypes.Count == 0) return;
            string tag = gameState.Countries.GetCountryTag(country);
            var best = new Dictionary<string, (double priority, double match, Design d, string name)>();
            var roleOrder = new List<string>();
            foreach (var g in groups)
            {
                if (g.Blocked.Contains(tag) || (g.Available.Count > 0 && !g.Available.Contains(tag))) continue;
                double gp = Hoi4ResearchAI.Weight(g.Priority, country, day, triggerCache);
                if (gp <= 0) continue;
                foreach (var spec in g.Designs)
                {
                    if (spec.Enable != null && Hoi4ResearchAI.Evaluate(spec.Enable, country, day) != 1) continue;
                    double p = gp * Hoi4ResearchAI.Weight(spec.Priority, country, day, triggerCache);
                    if (p <= 0) continue;
                    var d = Build(spec, country, day, haveTypes, haveModules);
                    if (d == null) continue;
                    foreach (string role in g.Roles)
                    {
                        if (!best.ContainsKey(role)) roleOrder.Add(role);
                        if (!best.TryGetValue(role, out var b) || p > b.priority || (p == b.priority && spec.Match > b.match))
                            best[role] = (p, spec.Match, d, spec.Name);
                    }
                }
            }
            foreach (string role in roleOrder)
            {
                var (_, _, d, name) = best[role];
                var t = types[d.Type];
                int a = System.Array.IndexOf(Hoi4.Units.Hoi4Divisions.Archetypes, t.Archetype);
                if (a < 0) continue; // an archetype no sub-unit uses
                Hoi4Production.Instance.TryGetBuildable(country, a, out var now);
                var cur = Hoi4EquipmentCatalog.Get(now.Equipment);
                if (cur != null && cur.IsDesign)
                {
                    if (cur.Design != null && (cur.Design.Same(d) || d.OnlyLowerUpgrades(cur.Design))) continue;
                    bool isDefault = cur.Name == cur.Owner + ":" + cur.Chassis;   // the archetype's default modules, no gun
                    bool ours = cur.Order > 100000 && cur.Owner == tag;          // an earlier AI design of its own
                    if (!isDefault && !ours && t.Year <= cur.Year) continue;      // a history design stays until a newer chassis
                    if (ours && t.Year < cur.Year) continue;
                }
                gameState.TryExecuteCommand(new CreateTankDesignCommand
                {
                    CountryId = country, Archetype = a, Spec = name, Type = d.Type,
                    Modules = Pack(d.Modules), Upgrades = PackUpgrades(d.Upgrades),
                }, out _);
            }
        }

        static string Pack(List<(string slot, string module)> l)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (s, m) in l) sb.Append(s).Append('=').Append(m).Append(';');
            return sb.ToString();
        }

        static string PackUpgrades(List<(string upgrade, int level)> l)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (u, v) in l) sb.Append(u).Append('=').Append(v).Append(';');
            return sb.ToString();
        }

        /// <summary>A design from the command's packed modules and upgrades.</summary>
        public static Design Unpack(string type, string mods, string ups)
        {
            var d = new Design { Type = type };
            foreach (var part in (mods ?? "").Split(';'))
            {
                int i = part.IndexOf('=');
                if (i > 0) d.Modules.Add((part.Substring(0, i), part.Substring(i + 1)));
            }
            foreach (var part in (ups ?? "").Split(';'))
            {
                int i = part.IndexOf('=');
                if (i > 0 && int.TryParse(part.Substring(i + 1), out int v)) d.Upgrades.Add((part.Substring(0, i), v));
            }
            return d;
        }

        public static bool IsKnownType(string type) => type != null && types.ContainsKey(type);

        /// <summary>Make the design the country's current one of the archetype (CreateTankDesignCommand): a catalogue
        /// entry "TAG:Light Tank II 3" (the fixed design of its chassis names it and gives its picture) for new
        /// lines; the production AI switches its lines to it.</summary>
        public static void Create(GameState gameState, ushort country, int archetype, string spec, Design d)
        {
            var t = types[d.Type];
            var st = StatsOf(d);
            string tag = gameState.Countries.GetCountryTag(country);
            designCount.TryGetValue(country, out int n);
            designCount[country] = ++n;
            var legacy = Hoi4EquipmentCatalog.Get(t.Legacy);
            string general = legacy?.General ?? d.Type;
            var e = new Hoi4EquipmentCatalog.Entry
            {
                Name = $"{tag}:{general} {n}", Archetype = t.Archetype, Year = t.Year,
                Cost = st.TryGetValue("build_cost_ic", out var cost) ? cost : FP.One,
                General = $"{general} ({n})", ArchetypeName = legacy?.ArchetypeName, Sprite = Hoi4EquipmentCatalog.SpriteName(tag, t.Legacy) ?? legacy?.Sprite,
                Chassis = d.Type, Family = t.Family, ChassisParent = t.Parent, LegacyOf = t.Legacy, Owner = tag, Design = d,
            };
            foreach (string k in new[] { "soft_attack", "hard_attack", "defense", "breakthrough", "hardness", "armor_value", "ap_attack", "air_attack", "reliability", "maximum_speed" })
                e.Stats[k] = st.TryGetValue(k, out var v) ? v : FP.Zero;
            e.Resolve();
            Hoi4EquipmentCatalog.Register(e);
            Hoi4Production.Instance.SetBuildable(country, archetype, e.Name, $"tank design {spec}: {d.Describe()}");
            Core.Recording.SessionRecorder.Note("tank_design", "new", country, 0, 0, 0,
                $"{e.Name} ({spec}): {d.Describe()} | soft {e.SoftAttack.ToFloat():0.0} hard {e.HardAttack.ToFloat():0.0} " +
                $"ap {e.ApAttack.ToFloat():0.0} armor {e.ArmorValue.ToFloat():0.0} bt {e.Breakthrough.ToFloat():0.0} cost {e.Cost.ToFloat():0.00}");
        }
    }

    /// <summary>The country adopts a tank design for an archetype (the AI designer, Hoi4TankDesigns): it becomes
    /// what new lines of the archetype build.</summary>
    public sealed class CreateTankDesignCommand : Core.Commands.BaseCommand
    {
        public ushort CountryId;
        public int Archetype;
        public string Spec, Type, Modules, Upgrades;

        public override bool Validate(GameState gameState) =>
            CountryId != 0 && Hoi4Production.Instance != null && Hoi4TankDesigns.IsKnownType(Type)
            && Archetype >= 0 && Archetype < Hoi4.Units.Hoi4Divisions.Archetypes.Length;

        public override void Execute(GameState gameState) =>
            Hoi4TankDesigns.Create(gameState, CountryId, Archetype, Spec, Hoi4TankDesigns.Unpack(Type, Modules, Upgrades));

        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId); w.Write(Archetype); w.Write(Spec ?? ""); w.Write(Type ?? ""); w.Write(Modules ?? ""); w.Write(Upgrades ?? "");
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16(); Archetype = r.ReadInt32(); Spec = r.ReadString(); Type = r.ReadString(); Modules = r.ReadString(); Upgrades = r.ReadString();
        }
    }
}
