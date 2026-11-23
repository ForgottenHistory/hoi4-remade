using System.Collections.Generic;
using System.Linq;
using System.Text;
using Core;
using Hoi4.Production;
using Hoi4.Units;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Hoi4.Deployment
{
    /// <summary>
    /// GAME: the AI's army plan (prototype: Tools/prototypes/ai_templates/design_templates.py). Each AI country keeps
    /// one division template per role, improved in place (no copies, no decommissioning as HOI4's AI does):
    ///   defence      - defence infantry: holds the line cheaply (defense, organisation, HP); engineers and support
    ///                  artillery always (the engineers' terrain and entrenchment bonuses aren't in the designer sums)
    ///   offence      - offensive infantry: soft attack and breakthrough, artillery heavy (4+ military factories)
    ///   mobile       - motorized with trucks (5+ military factories), cavalry without (and then no vehicles in it)
    ///   breakthrough - tanks with motorized infantry (8+ military factories)
    /// Infantry roles take nothing that needs vehicles; tank divisions nothing slower than their tanks.
    ///
    /// A review every ReviewDays (each country on its own day): sort the templates into roles (first time: the
    /// infantry template holding the most frontage is defence, the fastest mobile one mobile, the one with most tanks
    /// breakthrough; missing roles get a new template), design each role by hill climbing over single edits (add,
    /// remove or swap a battalion, add or remove a support or regimental company) scored from the designer's sums per
    /// combat width (diminishing returns, minus equipment IC and manpower, an organisation floor per role), and plan
    /// production: factories split by the IC the recruitment mix (Mix) needs per archetype. An archetype under half a
    /// factory drops out and the roles are designed again without it, so designs and plan agree. A role's template is
    /// saved (SaveTemplateCommand) when the design scores ImproveToSave better; the rest of the country's templates train
    /// no more (their AI orders are cancelled) and are deleted once no division uses them.
    /// Hoi4DeploymentAI trains by the plan's role shares, Hoi4ProductionAI aims its factories at the plan's split.
    ///
    /// Scores are doubles: the AI decides on the host only and its decisions reach other clients as commands.
    /// </summary>
    public static class Hoi4ArmyAI
    {
        public const int Defence = 0, Offence = 1, Mobile = 2, Breakthrough = 3, RoleCount = 4;
        public static readonly string[] RoleNames = { "defence", "offence", "mobile", "breakthrough" };
        // shares of the army (divisions); a missing role's goes to defence. After a player's Germany of 1939: five
        // infantry armies of 24, 10-12 motorized and 4-6 tank divisions (88% infantry, 8% mobile, 4% tanks)
        static readonly double[] Mix = { 0.70, 0.18, 0.08, 0.04 };
        static readonly string[] NewNames = { "Infantry Division", "Assault Infantry Division", "Motorized Division", "Armored Division" };
        const int OffenceFactories = 4, TruckFactories = 5, TankFactories = 8;
        const int ReviewDays = 90;
        const double ImproveToSave = 1.05, ImproveStep = 1.01;
        const int Columns = Hoi4TemplateDesign.Columns, Rows = Hoi4TemplateDesign.OpenRows, SupportRows = Hoi4TemplateDesign.SupportRows;
        const int RegimentalNeeds = Hoi4TemplateDesign.RegimentalBattalions;

        sealed class RoleRule
        {
            public string[] Groups;
            public int MinWidth, MaxWidth;
            public double OrgMin, Cost;
            public (string stat, double weight)[] Weights;
            public string[] Required = new string[0];
        }

        static readonly RoleRule[] Rules =
        {
            new RoleRule { Groups = new[] { "infantry", "combat_support" }, MinWidth = 16, MaxWidth = 20, OrgMin = 50, Cost = 1.0,
                Weights = new[] { ("defense", 1.0), ("org", 1.2), ("hp", 0.8), ("soft", 0.3), ("breakthrough", 0.1) },
                Required = new[] { "engineer", "artillery" } },
            new RoleRule { Groups = new[] { "infantry", "combat_support" }, MinWidth = 18, MaxWidth = 22, OrgMin = 35, Cost = 0.5,
                Weights = new[] { ("soft", 1.2), ("breakthrough", 0.8), ("hard", 0.3), ("defense", 0.4), ("org", 0.6), ("hp", 0.3) } },
            new RoleRule { Groups = new[] { "mobile", "mobile_combat_support" }, MinWidth = 16, MaxWidth = 22, OrgMin = 35, Cost = 0.5,
                Weights = new[] { ("soft", 0.8), ("breakthrough", 0.6), ("defense", 0.6), ("org", 0.8), ("hp", 0.3) } },
            new RoleRule { Groups = new[] { "armor", "mobile", "armor_combat_support", "mobile_combat_support" }, MinWidth = 18, MaxWidth = 30,
                OrgMin = 20, Cost = 0.3,
                Weights = new[] { ("breakthrough", 1.0), ("soft", 0.8), ("hard", 0.4), ("hardness", 0.8), ("armor", 0.4), ("org", 0.4), ("hp", 0.2) } },
        };

        // battalions kept out of the general roles: special forces and odd ones
        static readonly HashSet<string> Special = new HashSet<string>
        {
            "mountaineers", "marine", "paratrooper", "ranger_battalion", "marine_commando", "penal_battalion", "militia",
            "irregular_infantry", "bicycle_battalion", "camelry", "bus", "armored_car", "land_cruiser", "amphibious_armor",
            "amphibious_light_armor", "amphibious_medium_armor", "amphibious_heavy_armor", "amphibious_mechanized",
            "super_heavy_armor", "super_heavy_tank_destroyer_brigade", "super_heavy_sp_artillery_brigade",
            "super_heavy_sp_anti_air_brigade", "railway_gun", "super_heavy_artillery", "self_propelled_super_heavy_artillery",
        };
        static readonly string[] Vehicles = { "motorized", "mechanized", "tank_chassis", "armored_car", "armor" };

        /// <summary>A country's plan: its role templates (null: no such role), recruitment shares and factories per
        /// archetype (Hoi4Divisions.Archetypes).</summary>
        public sealed class Plan
        {
            public readonly string[] Templates = new string[RoleCount];
            public readonly double[] Shares = new double[RoleCount];
            public int[] Factories = new int[0];
            public int Military;
        }

        static readonly Dictionary<ushort, Plan> plans = new Dictionary<ushort, Plan>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics() { plans.Clear(); subs.Clear(); }

        /// <summary>The country's plan, null before its first review.</summary>
        public static Plan PlanOf(ushort country) => plans.TryGetValue(country, out var p) ? p : null;

        /// <summary>Daily, on the host only (Hoi4Simulation): the countries whose review day it is.</summary>
        public static void SimulateDay(int day)
        {
            var deployment = Hoi4Deployment.Instance;
            var production = Hoi4Production.Instance;
            var divisions = Hoi4Divisions.Instance;
            if (deployment == null || !deployment.IsReady || production == null || !production.IsReady || divisions == null
                || Hoi4Equipment.Instance == null || Hoi4Divisions.Archetypes.Length == 0) return;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            ushort player = PlayerCountry;
            foreach (ushort country in production.Countries.OrderBy(c => c))
            {
                // the first review in the first week (each country on its weekday), then every ReviewDays
                bool due = plans.ContainsKey(country) ? (day + country * 7) % ReviewDays == 0 : (day + country) % 7 == 0;
                if (country == player || !due) continue;
                string tag = deployment.Tag(country);
                if (tag == null || !divisions.Templates(tag).Any()) continue;
                Review(gameState, country, tag);
            }
        }

        // ---- sub-unit data ---------------------------------------------------------------------------------------

        sealed class Sub
        {
            public string Name, Group;
            public double Width, Hp, Org, Defense, Soft, Hard, Breakthrough, Hardness, Armor, Speed;
            public bool AffectsSpeed, Divisional, Regimental, Vehicle;
            public int Manpower;
            public string[] BattalionGroups;
            public (int archetype, double amount)[] Need;
        }

        static readonly Dictionary<string, Sub> subs = new Dictionary<string, Sub>();

        static Sub SubOf(string name)
        {
            if (name == null) return null;
            if (subs.TryGetValue(name, out var s)) return s;
            var j = Hoi4Divisions.Instance.SubUnit(name);
            if (j == null) { subs[name] = null; return null; }
            double D(string k) => (double?)j[k] ?? 0;
            var need = new List<(int, double)>();
            bool vehicle = false;
            if (j["need"] is JObject n)
                foreach (var p in n.Properties())
                {
                    int a = System.Array.IndexOf(Hoi4Divisions.Archetypes, p.Name);
                    if (a >= 0) need.Add((a, (double)p.Value));
                    if (Vehicles.Any(v => p.Name.Contains(v))) vehicle = true;
                }
            s = new Sub
            {
                Name = name, Group = (string)j["group"] ?? "infantry",
                Width = D("combat_width"), Hp = D("max_strength"), Org = D("max_organisation"), Defense = D("defense"),
                Soft = D("soft_attack"), Hard = D("hard_attack"), Breakthrough = D("breakthrough"), Hardness = D("hardness"),
                Armor = D("armor"), Speed = D("speed"), AffectsSpeed = (bool?)j["affects_speed"] ?? true,
                Divisional = (bool?)j["divisional"] ?? true, Regimental = (bool?)j["regimental"] ?? false,
                Manpower = (int?)j["manpower"] ?? 0, Vehicle = vehicle,
                BattalionGroups = (j["battalion_groups"] as JArray)?.Select(x => (string)x).ToArray() ?? new string[0],
                Need = need.ToArray(),
            };
            subs[name] = s;
            return s;
        }

        // ---- designs ---------------------------------------------------------------------------------------------

        /// <summary>A layout as the search edits it: brigades (group, battalions), divisional companies, a regimental
        /// company per brigade.</summary>
        sealed class Layout
        {
            public readonly List<(string group, List<string> battalions)> Columns = new List<(string, List<string>)>();
            public readonly List<string> Support = new List<string>();
            public readonly SortedDictionary<int, string> Regimental = new SortedDictionary<int, string>();

            public Layout Copy()
            {
                var c = new Layout();
                foreach (var (g, b) in Columns) c.Columns.Add((g, new List<string>(b)));
                c.Support.AddRange(Support);
                foreach (var p in Regimental) c.Regimental[p.Key] = p.Value;
                return c;
            }

            public IEnumerable<string> All()
            {
                foreach (var (_, b) in Columns) foreach (var x in b) yield return x;
                foreach (var x in Support) yield return x;
                foreach (var x in Regimental.Values) yield return x;
            }

            public string Key()
            {
                var sb = new StringBuilder();
                foreach (var (g, b) in Columns) { sb.Append(g).Append(':'); foreach (var x in b.OrderBy(x => x, System.StringComparer.Ordinal)) sb.Append(x).Append(','); sb.Append('|'); }
                sb.Append('#');
                foreach (var x in Support.OrderBy(x => x, System.StringComparer.Ordinal)) sb.Append(x).Append(',');
                sb.Append('#');
                foreach (var p in Regimental) sb.Append(p.Key).Append('=').Append(p.Value).Append(',');
                return sb.ToString();
            }

            public static Layout Of(Hoi4TemplateDesign d)
            {
                var l = new Layout();
                foreach (var x in d.Regiments.Select(s => s.X).Distinct().OrderBy(x => x))
                {
                    var b = d.Regiments.Where(s => s.X == x).OrderBy(s => s.Y).Select(s => s.Type).ToList();
                    l.Columns.Add((SubOf(b[0])?.Group ?? "infantry", b));
                }
                l.Support.AddRange(d.Support.OrderBy(s => s.Y).Select(s => s.Type));
                var cols = d.Regiments.Select(s => s.X).Distinct().OrderBy(x => x).ToList();
                foreach (var s in d.Regimental)
                {
                    int i = cols.IndexOf(s.X);
                    if (i >= 0) l.Regimental[i] = s.Type;
                }
                return l;
            }

            public Hoi4TemplateDesign ToDesign(string name, int priority)
            {
                var d = new Hoi4TemplateDesign { Name = name, Priority = priority };
                for (int x = 0; x < Columns.Count; x++)
                    for (int y = 0; y < Columns[x].battalions.Count; y++)
                        d.Regiments.Add(new Hoi4TemplateDesign.Slot(Columns[x].battalions[y], x, y));
                for (int y = 0; y < Support.Count; y++) d.Support.Add(new Hoi4TemplateDesign.Slot(Support[y], 0, y));
                foreach (var p in Regimental) d.Regimental.Add(new Hoi4TemplateDesign.Slot(p.Value, p.Key, 0));
                return d;
            }
        }

        struct Sums
        {
            public double Width, Hp, Org, Defense, Soft, Hard, Breakthrough, Hardness, Armor, Speed, Ic;
            public int Manpower;
        }

        static Sums Sum(Layout l, double[] cost)
        {
            var s = new Sums { Speed = double.MaxValue };
            double armorMax = 0, armorSum = 0;
            int n = 0;
            foreach (var (_, b) in l.Columns) foreach (var x in b) { var u = SubOf(x); if (u != null) s.Width += u.Width; }
            int regimentalFrom = l.Columns.Sum(c => c.battalions.Count) + l.Support.Count, i = 0;
            foreach (var x in l.All())
            {
                var u = SubOf(x);
                i++;
                if (u == null) continue;
                n++;
                s.Hp += u.Hp; s.Org += u.Org; s.Defense += u.Defense; s.Soft += u.Soft; s.Hard += u.Hard;
                s.Breakthrough += u.Breakthrough; s.Hardness += u.Hardness; s.Manpower += u.Manpower;
                armorMax = System.Math.Max(armorMax, u.Armor); armorSum += u.Armor;
                if (i <= regimentalFrom && u.AffectsSpeed) s.Speed = System.Math.Min(s.Speed, u.Speed);
                foreach (var (a, amount) in u.Need) s.Ic += amount * cost[a];
            }
            if (n > 0) { s.Org /= n; s.Hardness /= n; s.Armor = 0.3 * armorMax + 0.7 * armorSum / n; }
            if (s.Speed == double.MaxValue) s.Speed = 4;
            return s;
        }

        static double Stat(Sums s, string k)
        {
            switch (k)
            {
                case "defense": return s.Defense / s.Width;
                case "hp": return s.Hp / s.Width;
                case "soft": return s.Soft / s.Width;
                case "hard": return s.Hard / s.Width;
                case "breakthrough": return s.Breakthrough / s.Width;
                case "org": return s.Org;
                case "hardness": return s.Hardness;
                case "armor": return s.Armor;
                default: return 0;
            }
        }

        /// <summary>The role's score of a layout (null over its width), relative to a plain 9-infantry division.</summary>
        static double? Score(Layout l, RoleRule rule, Sums reference, double[] cost)
        {
            var s = Sum(l, cost);
            if (s.Width <= 0 || s.Width > rule.MaxWidth) return null;
            double value = 0;
            foreach (var (k, w) in rule.Weights)
            {
                double v = Stat(s, k), r = Stat(reference, k);
                double ratio = r > 0 ? v / r : v / 10;
                value += w * (2 * System.Math.Sqrt(System.Math.Max(0, ratio)) - 1); // diminishing returns
            }
            value -= rule.Cost * 0.5 * (s.Ic / s.Width) / (reference.Ic / reference.Width);
            value -= 0.3 * (s.Manpower / s.Width) / (reference.Manpower / reference.Width);
            if (s.Width < rule.MinWidth) value *= s.Width / rule.MinWidth;
            if (s.Org < rule.OrgMin) value -= 4 * (rule.OrgMin - s.Org) / rule.OrgMin;
            return value;
        }

        sealed class Options
        {
            public Dictionary<string, List<string>> Battalions = new Dictionary<string, List<string>>();
            public List<string> Divisional = new List<string>();
            public List<string> Regimental = new List<string>();
            public string[] Required = new string[0];
        }

        static IEnumerable<Layout> Edits(Layout d, Options o)
        {
            for (int i = 0; i < d.Columns.Count; i++)
            {
                var (g, bs) = d.Columns[i];
                o.Battalions.TryGetValue(g, out var same);
                if (bs.Count < Rows && same != null)
                    foreach (var b in same) { var n = d.Copy(); n.Columns[i].battalions.Add(b); yield return n; }
                for (int j = 0; j < bs.Count; j++)
                {
                    var n = d.Copy();
                    n.Columns[i].battalions.RemoveAt(j);
                    if (n.Columns[i].battalions.Count == 0)
                    {
                        n.Columns.RemoveAt(i);
                        var moved = new SortedDictionary<int, string>();
                        foreach (var p in n.Regimental) if (p.Key != i) moved[p.Key < i ? p.Key : p.Key - 1] = p.Value;
                        n.Regimental.Clear();
                        foreach (var p in moved) n.Regimental[p.Key] = p.Value;
                    }
                    else if (n.Columns[i].battalions.Count < RegimentalNeeds) n.Regimental.Remove(i);
                    yield return n;
                    if (same != null)
                        foreach (var b in same)
                            if (b != bs[j]) { var m = d.Copy(); m.Columns[i].battalions[j] = b; yield return m; }
                }
            }
            if (d.Columns.Count < Columns)
                foreach (var p in o.Battalions)
                    foreach (var b in p.Value) { var n = d.Copy(); n.Columns.Add((p.Key, new List<string> { b })); yield return n; }
            foreach (var s in o.Divisional)
            {
                if (o.Required.Contains(s)) continue;
                if (d.Support.Contains(s)) { var n = d.Copy(); n.Support.Remove(s); yield return n; }
                else if (d.Support.Count < SupportRows) { var n = d.Copy(); n.Support.Add(s); yield return n; }
            }
            for (int i = 0; i < d.Columns.Count; i++)
            {
                if (d.Columns[i].battalions.Count < RegimentalNeeds) continue;
                if (d.Regimental.ContainsKey(i)) { var n = d.Copy(); n.Regimental.Remove(i); yield return n; }
                foreach (var r in o.Regimental)
                    if (SubOf(r).BattalionGroups.Contains(d.Columns[i].group) && (!d.Regimental.TryGetValue(i, out var cur) || cur != r))
                    { var n = d.Copy(); n.Regimental[i] = r; yield return n; }
            }
        }

        static (Layout layout, double score) Search(Layout start, RoleRule rule, Sums reference, double[] cost, Options o)
        {
            var best = start;
            double bestScore = Score(start, rule, reference, cost) ?? -1e9;
            var seen = new HashSet<string> { start.Key() };
            for (int step = 0; step < 100; step++)
            {
                Layout pick = null;
                double pickScore = bestScore > 0 ? bestScore * ImproveStep : bestScore + 1e-6;
                foreach (var n in Edits(best, o))
                {
                    if (!seen.Add(n.Key())) continue;
                    var s = Score(n, rule, reference, cost);
                    if (s.HasValue && s.Value > pickScore) { pick = n; pickScore = s.Value; }
                }
                if (pick == null) break;
                best = pick;
                bestScore = pickScore;
            }
            return (best, bestScore);
        }

        // ---- review ----------------------------------------------------------------------------------------------

        sealed class RoleDesign
        {
            public Layout Layout, Current;
            public double Score, CurrentScore;
            public string Template;   // the role's template (null: a new one)
            public Sums Sums;
        }

        static void Review(GameState gameState, ushort country, string tag)
        {
            var divisions = Hoi4Divisions.Instance;
            var production = Hoi4Production.Instance;
            var equipment = Hoi4Equipment.Instance;
            var deployment = Hoi4Deployment.Instance;
            int archetypes = Hoi4Divisions.Archetypes.Length;

            var cost = new double[archetypes];
            var buildable = new HashSet<int>(production.BuildableArchetypes(country));
            for (int a = 0; a < archetypes; a++)
                cost[a] = production.TryGetBuildable(country, a, out var b) ? b.Cost.ToFloat() : 5.0;
            var stocked = new HashSet<int>();
            for (int a = 0; a < archetypes; a++) if (equipment.Stock(country, a) > Core.Data.FixedPoint64.Zero) stocked.Add(a);
            int military = production.Factories(country);

            var excluded = new HashSet<int>();
            RoleDesign[] designs = null;
            int[] split = null;
            double[] shares = null;
            for (int pass = 0; pass < 3; pass++)
            {
                designs = DesignRoles(country, tag, military, buildable, excluded, cost, PlanOf(country));
                shares = Shares(designs);
                split = Split(designs, BuildMix(country, tag, designs, shares), military, cost, archetypes);
                var zero = Enumerable.Range(0, archetypes).Where(a => split[a] == 0 && Used(designs, a) && !stocked.Contains(a)).ToList();
                if (zero.Count == 0) break;
                foreach (int a in zero) excluded.Add(a);
            }

            var plan = new Plan { Factories = split, Military = military };
            var taken = new HashSet<string>(divisions.Templates(tag));
            for (int r = 0; r < RoleCount; r++)
            {
                var d = designs[r];
                plan.Shares[r] = shares[r];
                if (d == null) continue;
                bool missing = d.Current != null && Rules[r].Required.Any(s => d.Layout.Support.Contains(s) && !d.Current.Support.Contains(s));
                bool save = d.Template == null || d.CurrentScore <= 0 || missing || d.Score > d.CurrentScore * ImproveToSave;
                string fresh = r == Mobile && d.Layout.Columns.Any(c => c.battalions.Contains("cavalry")) ? "Cavalry Division" : NewNames[r];
                string name = d.Template ?? Unique(fresh, taken);
                taken.Add(name);
                if (save && (d.Template == null || d.Layout.Key() != d.Current?.Key()))
                {
                    int priority = d.Template != null ? divisions.TemplatePriority(tag, d.Template) : Hoi4Divisions.Normal;
                    var design = d.Layout.ToDesign(name, priority);
                    if (divisions.CanSave(tag, d.Template, design, out string why)
                        && gameState.TryExecuteCommand(new SaveTemplateCommand { CountryId = country, Original = d.Template ?? "", Design = design }, out _))
                        Core.Recording.SessionRecorder.Note("army", "design", country, 0, 0, 0,
                            $"{RoleNames[r]} {name}: {Text(d.Layout)} (score {d.CurrentScore:0.00} -> {d.Score:0.00})");
                    else if (d.Template == null)
                    {
                        Core.Recording.SessionRecorder.Note("army", "design_failed", country, 0, 0, 0, $"{RoleNames[r]} {name}: {why}");
                        plan.Shares[Defence] += plan.Shares[r];
                        plan.Shares[r] = 0;
                        continue;
                    }
                }
                plan.Templates[r] = divisions.HasTemplate(tag, name) ? name : d.Template;
            }
            plans[country] = plan;

            // the rest train no more and go once empty
            var roleTemplates = new HashSet<string>(plan.Templates.Where(t => t != null));
            foreach (var order in deployment.Orders(country).ToList())
                if (!roleTemplates.Contains(order.Template))
                    gameState.TryExecuteCommand(new RemoveDeploymentOrderCommand { CountryId = country, OrderId = order.Id }, out _);
            foreach (string t in divisions.Templates(tag).ToList())
                if (!roleTemplates.Contains(t) && !deployment.TemplateInUse(country, t))
                    gameState.TryExecuteCommand(new DeleteTemplateCommand { CountryId = country, Template = t }, out _);

            Core.Recording.SessionRecorder.Note("army", "plan", country, 0, 0, 0,
                string.Join(", ", Enumerable.Range(0, RoleCount).Where(r => plan.Templates[r] != null)
                    .Select(r => $"{RoleNames[r]} {plan.Shares[r]:0%} {plan.Templates[r]}"))
                + " | factories " + string.Join(" ", Enumerable.Range(0, archetypes).Where(a => split[a] > 0)
                    .Select(a => $"{Hoi4Divisions.Archetypes[a]} {split[a]}"))
                + (excluded.Count > 0 ? " | left out " + string.Join(" ", excluded.Select(a => Hoi4Divisions.Archetypes[a])) : ""));
        }

        /// <summary>What recruitment will train next, by role: each role's gap to its share of the army (field and
        /// training, as Hoi4DeploymentAI counts it) after about a year of new divisions; the factory split follows it,
        /// so a country whose 1936 army already has its tanks doesn't pile up tanks. The shares when no role is short.</summary>
        static double[] BuildMix(ushort country, string tag, RoleDesign[] designs, double[] shares)
        {
            var divisions = Hoi4Divisions.Instance;
            var count = new double[RoleCount];
            double total = 0;
            int RoleOf(string template, string mainType)
            {
                for (int r = 0; r < RoleCount; r++) if (designs[r]?.Template != null && designs[r].Template == template) return r;
                string g = divisions.SubUnitGroup(mainType);
                return g == "armor" ? Breakthrough : g == "mobile" ? Mobile : Defence;
            }
            foreach (var d in divisions.All.Values)
                if (d.Tag == tag) { count[RoleOf(d.Template, d.MainType)]++; total++; }
            foreach (var o in Hoi4Deployment.Instance.Orders(country))
            {
                int r = RoleOf(o.Template, divisions.TemplateMain(tag, o.Template));
                count[r] += o.Lines.Count;
                total += o.Lines.Count;
            }
            double coming = System.Math.Max(10, total * 0.25);
            var mix = new double[RoleCount];
            double sum = 0;
            for (int r = 0; r < RoleCount; r++)
            {
                if (designs[r] == null) continue;
                mix[r] = System.Math.Max(0, shares[r] * (total + coming) - count[r]);
                sum += mix[r];
            }
            if (sum <= 0) return shares;
            for (int r = 0; r < RoleCount; r++) mix[r] /= sum;
            return mix;
        }

        static bool Used(RoleDesign[] designs, int archetype) =>
            designs.Any(d => d != null && d.Layout.All().Any(x => SubOf(x)?.Need.Any(n => n.archetype == archetype) ?? false));

        static string Unique(string name, HashSet<string> taken)
        {
            if (!taken.Contains(name)) return name;
            for (int i = 2; ; i++) if (!taken.Contains($"{name} {i}")) return $"{name} {i}";
        }

        static string Text(Layout l) =>
            string.Join(" | ", l.Columns.Select((c, i) => string.Join("+", c.battalions.GroupBy(x => x).Select(g => $"{g.Count()}x{g.Key}"))
                + (l.Regimental.TryGetValue(i, out var r) ? $" [{r}]" : "")))
            + (l.Support.Count > 0 ? " ; " + string.Join(", ", l.Support) : "");

        static double[] Shares(RoleDesign[] designs)
        {
            var s = new double[RoleCount];
            double lacking = 0;
            for (int r = 0; r < RoleCount; r++) { if (designs[r] != null) s[r] = Mix[r]; else lacking += Mix[r]; }
            s[Defence] += lacking;
            return s;
        }

        /// <summary>Factories by the IC each archetype's share of the mix takes: one for every archetype worth half a
        /// factory (largest first), the rest by largest remainder.</summary>
        static int[] Split(RoleDesign[] designs, double[] shares, int military, double[] cost, int archetypes)
        {
            var ic = new double[archetypes];
            for (int r = 0; r < RoleCount; r++)
            {
                if (designs[r] == null) continue;
                foreach (var x in designs[r].Layout.All())
                    foreach (var (a, amount) in SubOf(x)?.Need ?? new (int, double)[0])
                        ic[a] += shares[r] * amount * cost[a];
            }
            double total = ic.Sum();
            var split = new int[archetypes];
            if (total <= 0 || military <= 0) return split;
            var raw = ic.Select(v => military * v / total).ToArray();
            int assigned = 0;
            foreach (int a in Enumerable.Range(0, archetypes).OrderByDescending(a => raw[a]))
                if (raw[a] >= 0.5 && assigned < military) { split[a] = 1; assigned++; }
            while (assigned < military)
            {
                int a = Enumerable.Range(0, archetypes).OrderByDescending(x => raw[x] - split[x]).First();
                split[a]++;
                assigned++;
            }
            return split;
        }

        static RoleDesign[] DesignRoles(ushort country, string tag, int military, HashSet<int> buildable, HashSet<int> excluded,
                                        double[] cost, Plan previous)
        {
            var divisions = Hoi4Divisions.Instance;
            int Archetype(string n) => System.Array.IndexOf(Hoi4Divisions.Archetypes, n);
            int trucksA = Archetype("motorized_equipment");
            // a role the last plan had keeps going until 2 factories under its threshold (no flapping at the edge)
            int Slack(int role) => previous?.Templates[role] != null ? 2 : 0;
            bool trucks = military >= TruckFactories - Slack(Mobile) && buildable.Contains(trucksA) && !excluded.Contains(trucksA);
            int chassis = new[] { "light_tank_chassis", "medium_tank_chassis" }.Select(Archetype).Where(a => a >= 0 && buildable.Contains(a) && !excluded.Contains(a)).DefaultIfEmpty(-1).First();
            bool tanks = military >= TankFactories - Slack(Breakthrough) && chassis >= 0 && trucks;

            // what the country may use: its designer sub-units, no special forces, nothing the plan leaves out;
            // armour only of a chassis it makes, vehicles only with trucks
            var usable = divisions.SubUnitsOf(tag).Where(n =>
            {
                var s = SubOf(n);
                if (s == null || Special.Contains(n) || s.Need.Any(x => excluded.Contains(x.archetype))) return false;
                if (s.Group == "armor" && !(tanks && s.Need.Any(x => x.archetype == chassis))) return false;
                if (s.Need.Any(x => x.archetype == trucksA) && !trucks) return false;
                return true;
            }).ToList();
            string mobileMain = trucks && usable.Contains("motorized") ? "motorized" : usable.Contains("cavalry") ? "cavalry" : null;

            var reference = Reference(cost);
            var templates = divisions.Templates(tag).ToList();
            var field = new Dictionary<string, int>();
            foreach (var d in divisions.All.Values) if (d.Tag == tag) field[d.Template] = (field.TryGetValue(d.Template, out int c) ? c : 0) + 1;

            var result = new RoleDesign[RoleCount];
            var claimed = new HashSet<string>();
            for (int r = 0; r < RoleCount; r++)
            {
                var rule = Rules[r];
                if (r == Offence && military < OffenceFactories - Slack(Offence)) continue;
                if (r == Breakthrough && !tanks) continue;
                if (r == Mobile && mobileMain == null) continue;

                var o = new Options { Required = rule.Required };
                foreach (var n in usable)
                {
                    var s = SubOf(n);
                    if (!rule.Groups.Contains(s.Group)) continue;
                    if ((r == Defence || r == Offence || (r == Mobile && mobileMain == "cavalry")) && s.Vehicle) continue;
                    if (s.Group == "mobile" && r == Mobile && n != mobileMain) continue;
                    if (!o.Battalions.TryGetValue(s.Group, out var list)) o.Battalions[s.Group] = list = new List<string>();
                    list.Add(n);
                }
                if (r == Breakthrough)
                {
                    double slowest = o.Battalions.TryGetValue("armor", out var armor) ? armor.Min(b => SubOf(b).Speed) : 4;
                    foreach (var g in o.Battalions.Keys.ToList()) o.Battalions[g] = o.Battalions[g].Where(b => SubOf(b).Speed >= slowest).ToList();
                }
                if (r == Mobile && mobileMain == "motorized")
                {
                    double speed = SubOf("motorized").Speed;
                    foreach (var g in o.Battalions.Keys.ToList()) o.Battalions[g] = o.Battalions[g].Where(b => SubOf(b).Speed >= speed).ToList();
                }
                foreach (var g in o.Battalions.Keys.ToList()) if (o.Battalions[g].Count == 0) o.Battalions.Remove(g);
                bool noVehicles = r == Defence || r == Offence || (r == Mobile && mobileMain == "cavalry");
                foreach (var n in usable)
                {
                    var s = SubOf(n);
                    if (s.Group != "support" || (noVehicles && s.Vehicle)) continue;
                    if (s.Divisional) o.Divisional.Add(n);
                    if (s.Regimental && s.BattalionGroups.Length > 0) o.Regimental.Add(n);
                }
                if (r == Breakthrough && (!o.Battalions.ContainsKey("armor") || !o.Battalions.ContainsKey("mobile"))) continue; // tanks need their infantry

                // the role's template: kept from the last review while it still fits, else found among the templates
                string template = previous?.Templates[r];
                if (template != null && (!templates.Contains(template) || claimed.Contains(template) || !Fits(divisions.Design(tag, template), r, mobileMain)))
                    template = null;
                if (template == null) template = Classify(tag, templates, field, claimed, r, mobileMain);
                Layout current = template != null ? Layout.Of(divisions.Design(tag, template)) : null;
                if (template != null) claimed.Add(template);

                // the search starts from the role's template (only what the role may use), offence from defence's
                Layout start = current?.Copy();
                if (start == null && r == Offence && result[Defence] != null) start = result[Defence].Layout.Copy();
                if (start == null)
                {
                    string main = r == Mobile ? mobileMain : r == Breakthrough ? o.Battalions["armor"][0] : "infantry";
                    if (!usable.Contains(main)) continue;
                    start = new Layout();
                    start.Columns.Add((SubOf(main).Group, new List<string> { main, main }));
                }
                for (int i = start.Columns.Count - 1; i >= 0; i--)
                {
                    var (g, bs) = start.Columns[i];
                    bs.RemoveAll(b => !o.Battalions.TryGetValue(g, out var l) || !l.Contains(b));
                    if (bs.Count == 0) start.Columns.RemoveAt(i);
                }
                start.Support.RemoveAll(s => !o.Divisional.Contains(s));
                start.Regimental.Clear();
                if (start.Columns.Count == 0) continue;
                foreach (var s in rule.Required)
                    if (o.Divisional.Contains(s) && !start.Support.Contains(s) && start.Support.Count < SupportRows) start.Support.Add(s);

                var (best, score) = Search(start, rule, reference, cost, o);
                result[r] = new RoleDesign
                {
                    Layout = best, Score = score, Template = template, Current = current,
                    CurrentScore = current != null ? Score(current, rule, reference, cost) ?? 0 : 0,
                };
            }
            return result;
        }

        static Sums Reference(double[] cost)
        {
            var l = new Layout();
            for (int i = 0; i < 3; i++) l.Columns.Add(("infantry", new List<string> { "infantry", "infantry", "infantry" }));
            var s = Sum(l, cost);
            if (s.Width <= 0) s.Width = 18;
            if (s.Ic <= 0) s.Ic = 1;
            if (s.Manpower <= 0) s.Manpower = 1;
            return s;
        }

        /// <summary>Whether a template still suits the role (a cavalry mobile template doesn't once trucks come).</summary>
        static bool Fits(Hoi4TemplateDesign d, int role, string mobileMain)
        {
            if (d == null || d.Regiments.Count == 0) return false;
            var groups = new HashSet<string>(d.Regiments.Select(s => SubOf(s.Type)?.Group));
            switch (role)
            {
                case Defence: case Offence:
                    return groups.IsSubsetOf(new[] { "infantry", "combat_support" }) && !d.Regiments.Any(s => Special.Contains(s.Type));
                case Mobile: return !groups.Contains("armor") && d.Regiments.Any(s => s.Type == mobileMain);
                default: return groups.Contains("armor");
            }
        }

        /// <summary>The template a role starts from: defence the infantry one holding the most frontage (divisions x
        /// width), mobile the one of its main battalion with most divisions, breakthrough the one with most tanks.
        /// Offence starts new (from defence's design).</summary>
        static string Classify(string tag, List<string> templates, Dictionary<string, int> field, HashSet<string> claimed, int role, string mobileMain)
        {
            if (role == Offence) return null;
            var divisions = Hoi4Divisions.Instance;
            string best = null;
            double bestKey = -1;
            foreach (string t in templates)
            {
                if (claimed.Contains(t)) continue;
                var d = divisions.Design(tag, t);
                if (!Fits(d, role, mobileMain)) continue;
                int used = field.TryGetValue(t, out int n) ? n : 0;
                double width = d.Regiments.Sum(s => SubOf(s.Type)?.Width ?? 0);
                double key = role == Defence ? used * width + width * 0.001
                    : role == Mobile ? used + width * 0.001
                    : d.Regiments.Count(s => SubOf(s.Type)?.Group == "armor") * 1000 + used;
                if (key > bestKey) { bestKey = key; best = t; }
            }
            return best;
        }

        static ushort PlayerCountry
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }
    }
}
