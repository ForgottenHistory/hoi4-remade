using System.Collections.Generic;
using Core;
using Core.Data;
using Newtonsoft.Json.Linq;

namespace Hoi4.Research
{
    /// <summary>
    /// GAME: HOI4's research AI (prototype Tools/prototypes/research/research_ai.py: the AI's real picks in two recorded
    /// games land in this score's 75% band 78-79% of the time, a random tech's chance being 3-4%).
    /// Every PickCycleDays (28; each country on its own day of the cycle, country id mod 28, spreading the start's 850
    /// picks to about 30 a day), every country but the player's fills its empty slots. Waiting costs nothing: an empty
    /// slot saves up to 30 days (BASE_RESEARCH_POINTS_SAVED) and the pick spends them at once, so a cycle under 30 days
    /// loses no research. Score of each tech it can start:
    /// - ai_will_do (common/technologies): the base, then each modifier whose trigger holds, in order (factor multiplies,
    ///   add adds). Triggers it knows: AND, OR, NOT, date, tag, original_tag, is_major, has_war, has_tech, factory counts,
    ///   has_dlc (every DLC owned), always, is_ai. A modifier with any other trigger is left out.
    /// - x (1 + value / 100) for each active AI strategy weighting it (common/ai_strategy research_weight_factor: allowed
    ///   and enable hold, abort doesn't); a research_tech strategy's tech is picked first.
    /// - Ahead of time: none past MAX_AHEAD_RESEARCH_PENALTY (3, the 2 x years ahead penalty), else
    ///   / (1 + RESEARCH_AHEAD_OF_TIME_FACTOR 4 x the penalty).
    /// - Length: x RESEARCH_BASE_DAYS (60) / (60 + days at speed 1, penalty included): shorter techs first.
    /// The pick: at random among the techs scoring at least RESEARCH_WEIGHT_TRUNCATION_THRESHOLD (0.75) of the best.
    /// Majors (is_major): the 7 countries with the most factories, plus any with more than 0.7 x their average.
    /// Picks go through SetResearchCommand like the player's. Checked over the first year: 4.65 techs per AI country by
    /// 15 February 1937, the real AI 5.06 (vanilla_ger_1936).
    /// Cost: the triggers are compiled once into typed nodes (no JSON or string work per score), and a country's
    /// strategies are evaluated once per pick day, not per slot.
    /// </summary>
    public static class Hoi4ResearchAI
    {
        const int PickCycleDays = 28;        // under BASE_RESEARCH_POINTS_SAVED (30): a slot never waits past what it saves
        const double Truncation = 0.75;      // NAI RESEARCH_WEIGHT_TRUNCATION_THRESHOLD
        const double AheadFactor = 4.0;      // NAI RESEARCH_AHEAD_OF_TIME_FACTOR
        const double MaxAheadPenalty = 3.0;  // NAI MAX_AHEAD_RESEARCH_PENALTY
        const double BaseDays = 60.0;        // NAI RESEARCH_BASE_DAYS
        const int MinMajors = 7;             // NCountry MIN_MAJOR_COUNTRIES
        const double MajorRatio = 0.7;       // NCountry ADDITIONAL_MAJOR_COUNTRIES_IC_RATIO

        sealed class Ctx
        {
            public ushort Country;
            public string Tag;
            public bool Major, AtWar;
            public int Civ, Mil, Nav;
            public int Date;             // yyyymmdd
            public Hoi4Research Research;
        }

        // ---- compiled triggers ------------------------------------------------------------------------------

        enum Kind : byte { And, Or, Not, Date, Tag, Major, War, HasTech, Const, Civ, Mil, Nav, Factories, Unknown }
        enum Cmp : byte { Eq, Gt, Lt, Ge, Le, Ne }

        sealed class Node
        {
            public Kind Kind;
            public Cmp Cmp;
            public bool Yes;             // Const value; Major/War: the "yes" side
            public int Value;            // Date yyyymmdd, HasTech index (-1: unknown tech, false)
            public double Number;        // factory counts
            public string Tag;
            public Node[] Kids;
        }

        sealed class Mod
        {
            public Node If;
            public bool HasFactor, HasAdd;
            public double Factor, Add;
        }

        sealed class TechAi
        {
            public double Base = 1;
            public Mod[] Mods = System.Array.Empty<Mod>();
        }

        sealed class Strategy
        {
            public Node Allowed, Enable, Abort; // Abort null when empty
            public (int tech, double factor)[] Weights;
            public int[] Force;
        }

        static Hoi4Research compiledFor;
        static TechAi[] techAi;
        static Strategy[] strategies;

        static readonly HashSet<ushort> majors = new HashSet<ushort>();
        static readonly Dictionary<ushort, int> naval = new Dictionary<ushort, int>();
        static int countedDay = -1;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            compiledFor = null;
            techAi = null;
            strategies = null;
            majors.Clear();
            naval.Clear();
            countedDay = -1;
        }

        // ---- triggers for other AI systems (Hoi4TankDesigns) ------------------------------------------------

        /// <summary>A trigger block compiled once (the same triggers the research AI knows).</summary>
        public sealed class Trigger { internal object Root; }

        /// <summary>Compile a trigger block ([[key, op, value], ...], the converter's pdx_json); null lines hold.</summary>
        public static Trigger CompileTrigger(JArray lines)
        {
            var research = Hoi4Research.Instance;
            return research == null ? null : new Trigger { Root = Lines(research, lines, Kind.And) };
        }

        /// <summary>The trigger for a country: 1 true, 0 false, -1 can't tell (a trigger it doesn't know). Factory
        /// counts and majors as the research AI counts them (weekly).</summary>
        public static int Evaluate(Trigger t, ushort country, int day)
        {
            var research = Hoi4Research.Instance;
            if (t == null) return 1;
            if (research == null) return -1;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            if (countedDay < 0 || day - countedDay >= 7) CountFactories(gameState, research, day);
            var now = gameState.Time.GetCurrentGameTime();
            var construction = Hoi4.Construction.Hoi4Construction.Instance;
            var ctx = new Ctx
            {
                Country = country, Tag = gameState.Countries.GetCountryTag(country), Major = majors.Contains(country),
                AtWar = gameState.Diplomacy != null && gameState.Diplomacy.IsAtWar(country),
                Civ = construction != null ? construction.Civilian(country) : 0,
                Mil = construction != null ? construction.Military(country) : 0,
                Nav = naval.TryGetValue(country, out int nav) ? nav : 0,
                Date = now.Year * 10000 + now.Month * 100 + now.Day, Research = research,
            };
            return Eval(ctx, (Node)t.Root);
        }

        /// <summary>A weight block ({base, modifiers: [{factor | add, if}]}, the converter's tech_ai) for a country:
        /// the base, then each modifier whose trigger holds.</summary>
        public static double Weight(JObject block, ushort country, int day, Dictionary<JToken, Trigger> cache)
        {
            if (block == null) return 1;
            double w = block["base"] != null ? (double)block["base"] : 1;
            if (block["modifiers"] is JArray ms)
                foreach (var m in ms)
                {
                    if (!(m is JObject mo)) continue;
                    if (!cache.TryGetValue(mo, out var t)) cache[mo] = t = CompileTrigger(mo["if"] as JArray);
                    if (Evaluate(t, country, day) != 1) continue;
                    if (mo["factor"] != null) w *= (double)mo["factor"];
                    if (mo["add"] != null) w += (double)mo["add"];
                }
            return w;
        }

        /// <summary>Daily, on the host only (Hoi4Simulation); each country on its day of the cycle.</summary>
        public static void SimulateDay(int day)
        {
            var research = Hoi4Research.Instance;
            if (research == null || !research.IsReady || research.Technologies.Count == 0) return;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            ushort player = PlayerCountry;
            List<ushort> picking = null;
            foreach (ushort c in research.Countries)
                if (c != player && c % PickCycleDays == day % PickCycleDays && research.EmptySlots(c) > 0)
                    (picking ??= new List<ushort>()).Add(c);
            if (picking == null) return;
            if (compiledFor != research) Compile(research);
            if (countedDay < 0 || day - countedDay >= 7) CountFactories(gameState, research, day);

            var now = gameState.Time.GetCurrentGameTime();
            var construction = Hoi4.Construction.Hoi4Construction.Instance;
            var weights = new Dictionary<int, double>();
            var force = new List<int>();
            var scores = new List<(int tech, double score)>();
            var band = new List<int>();
            foreach (ushort country in picking)
            {
                var ctx = new Ctx
                {
                    Country = country, Tag = gameState.Countries.GetCountryTag(country), Major = majors.Contains(country),
                    AtWar = gameState.Diplomacy != null && gameState.Diplomacy.IsAtWar(country),
                    Civ = construction != null ? construction.Civilian(country) : 0,
                    Mil = construction != null ? construction.Military(country) : 0,
                    Nav = naval.TryGetValue(country, out int nav) ? nav : 0,
                    Date = now.Year * 10000 + now.Month * 100 + now.Day, Research = research,
                };
                weights.Clear();
                force.Clear();
                Strategies(ctx, weights, force);
                var slots = research.Slots(country);
                for (int i = 0; i < slots.Count; i++)
                {
                    if (!slots[i].Empty) continue;
                    int tech = Pick(ctx, weights, force, scores, band, day * 31 + i);
                    if (tech < 0) break; // nothing left to research
                    gameState.TryExecuteCommand(new SetResearchCommand { CountryId = country, Slot = i, Tech = tech }, out _);
                }
            }
        }

        static ushort PlayerCountry
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        /// <summary>Civilian + military (Hoi4Construction) + naval factories per country, and the majors from them.</summary>
        static void CountFactories(GameState gameState, Hoi4Research research, int day)
        {
            countedDay = day;
            naval.Clear();
            if (Hoi4.Construction.Hoi4Buildings.Available)
                foreach (var kv in Hoi4.Construction.Hoi4Buildings.Users(gameState))
                {
                    int n = Hoi4.Construction.Hoi4Buildings.Level(kv.Key, "dockyard");
                    if (n > 0) { naval.TryGetValue(kv.Value, out int had); naval[kv.Value] = had + n; }
                }
            var construction = Hoi4.Construction.Hoi4Construction.Instance;
            var totals = new List<(int n, ushort c)>();
            foreach (ushort c in research.Countries)
            {
                int n = (construction != null ? construction.Civilian(c) + construction.Military(c) : 0)
                        + (naval.TryGetValue(c, out int nav) ? nav : 0);
                if (n > 0) totals.Add((n, c));
            }
            totals.Sort((a, b) => b.n != a.n ? b.n.CompareTo(a.n) : a.c.CompareTo(b.c));
            majors.Clear();
            double sum = 0;
            int top = System.Math.Min(MinMajors, totals.Count);
            for (int i = 0; i < top; i++) { majors.Add(totals[i].c); sum += totals[i].n; }
            double bar = top > 0 ? MajorRatio * sum / top : double.MaxValue;
            foreach (var t in totals) if (t.n > bar) majors.Add(t.c);
        }

        // ---- the pick --------------------------------------------------------------------------------------

        static int Pick(Ctx ctx, Dictionary<int, double> weights, List<int> force, List<(int tech, double score)> scores,
                        List<int> band, int seed)
        {
            var research = ctx.Research;
            foreach (int f in force)
                if (research.CanResearch(ctx.Country, f)) return f;

            scores.Clear();
            double best = 0;
            int bestTech = -1;
            var techs = research.Technologies;
            for (int i = 0; i < techs.Count; i++)
            {
                if (!research.CanResearch(ctx.Country, i)) continue;
                double s = Score(ctx, techs[i], weights);
                if (s <= 0) continue;
                scores.Add((i, s));
                if (s > best) { best = s; bestTech = i; }
            }
            if (scores.Count == 0) return -1;
            band.Clear();
            foreach (var (tech, s) in scores) if (s >= Truncation * best) band.Add(tech);
            var rng = new DeterministicRandom((uint)(ctx.Country * 7919 + seed + 1));
            int pick = band[rng.NextInt(band.Count)];
            Core.Recording.SessionRecorder.Note("research_ai", "pick", ctx.Country, 0, 0, 0,
                $"{techs[pick].Id} of {band.Count} in band, {scores.Count} open, best {techs[bestTech].Id} {best:0.##}");
            return pick;
        }

        static double Score(Ctx ctx, TechDef t, Dictionary<int, double> weights)
        {
            double w = AiWillDo(ctx, techAi[t.Index]);
            if (weights.TryGetValue(t.Index, out double f)) w *= f;
            if (w <= 0) return 0;
            double penalty = 2.0 * ctx.Research.YearsAhead(t.Index).ToDouble();
            if (penalty > MaxAheadPenalty) return 0;
            w /= 1 + AheadFactor * penalty;
            double days = t.Cost.ToDouble() * Hoi4Research.BaseTechCost.ToDouble() * (1 + penalty);
            return w * BaseDays / (BaseDays + days);
        }

        static double AiWillDo(Ctx ctx, TechAi ai)
        {
            double w = ai.Base;
            foreach (var m in ai.Mods)
            {
                if (Eval(ctx, m.If) != 1) continue;
                if (m.HasFactor) w *= m.Factor;
                if (m.HasAdd) w += m.Add;
            }
            return w;
        }

        static void Strategies(Ctx ctx, Dictionary<int, double> weights, List<int> force)
        {
            foreach (var s in strategies)
            {
                if (Eval(ctx, s.Allowed) != 1 || Eval(ctx, s.Enable) != 1) continue;
                if (s.Abort != null && Eval(ctx, s.Abort) == 1) continue;
                foreach (var (tech, factor) in s.Weights)
                    weights[tech] = (weights.TryGetValue(tech, out double had) ? had : 1) * factor;
                force.AddRange(s.Force);
            }
        }

        // ---- evaluation: 1 true, 0 false, -1 can't tell ----------------------------------------------------

        static int Eval(Ctx ctx, Node n)
        {
            switch (n.Kind)
            {
                case Kind.And:
                {
                    int r = 1;
                    foreach (var k in n.Kids)
                    {
                        int e = Eval(ctx, k);
                        if (e == 0) return 0;
                        if (e < 0) r = -1;
                    }
                    return r;
                }
                case Kind.Or:
                {
                    int r = 0;
                    foreach (var k in n.Kids)
                    {
                        int e = Eval(ctx, k);
                        if (e == 1) return 1;
                        if (e < 0) r = -1;
                    }
                    return r;
                }
                case Kind.Not:
                {
                    int e = Eval(ctx, n.Kids[0]);
                    return e < 0 ? -1 : 1 - e;
                }
                case Kind.Date: return Compare(ctx.Date, n.Cmp, n.Value);
                case Kind.Tag: return ctx.Tag == n.Tag ? 1 : 0;
                case Kind.Major: return ctx.Major == n.Yes ? 1 : 0;
                case Kind.War: return ctx.AtWar == n.Yes ? 1 : 0;
                case Kind.HasTech: return n.Value >= 0 && ctx.Research.Known(ctx.Country, n.Value) ? 1 : 0;
                case Kind.Const: return n.Yes ? 1 : 0;
                case Kind.Civ: return Compare(ctx.Civ, n.Cmp, n.Number);
                case Kind.Mil: return Compare(ctx.Mil, n.Cmp, n.Number);
                case Kind.Nav: return Compare(ctx.Nav, n.Cmp, n.Number);
                case Kind.Factories: return Compare(ctx.Civ + ctx.Mil + ctx.Nav, n.Cmp, n.Number);
                default: return -1;
            }
        }

        static int Compare(double a, Cmp op, double b)
        {
            bool r;
            switch (op)
            {
                case Cmp.Gt: r = a > b; break;
                case Cmp.Lt: r = a < b; break;
                case Cmp.Ge: r = a >= b; break;
                case Cmp.Le: r = a <= b; break;
                case Cmp.Ne: r = a != b; break;
                default: r = a == b; break;
            }
            return r ? 1 : 0;
        }

        // ---- compiling ----------------------------------------------------------------------------------------

        static void Compile(Hoi4Research research)
        {
            compiledFor = research;
            var techs = research.Technologies;
            techAi = new TechAi[techs.Count];
            for (int i = 0; i < techs.Count; i++)
            {
                var ai = new TechAi();
                var o = techs[i].Ai;
                if (o != null)
                {
                    if (o["base"] != null) ai.Base = (double)o["base"];
                    var mods = new List<Mod>();
                    if (o["modifiers"] is JArray ms)
                        foreach (var m in ms)
                        {
                            if (!(m is JObject mo)) continue;
                            mods.Add(new Mod
                            {
                                If = Lines(research, mo["if"] as JArray, Kind.And),
                                HasFactor = mo["factor"] != null, Factor = mo["factor"] != null ? (double)mo["factor"] : 1,
                                HasAdd = mo["add"] != null, Add = mo["add"] != null ? (double)mo["add"] : 0,
                            });
                        }
                    ai.Mods = mods.ToArray();
                }
                techAi[i] = ai;
            }
            var list = new List<Strategy>();
            foreach (var s in research.AiStrategies)
            {
                var weights = new List<(int, double)>();
                if (s["weights"] is JObject ws)
                    foreach (var p in ws.Properties())
                        if (research.Tech(p.Name) is TechDef t) weights.Add((t.Index, 1 + (double)p.Value / 100));
                var force = new List<int>();
                if (s["force"] is JArray fs)
                    foreach (var f in fs)
                        if (research.Tech((string)f) is TechDef t) force.Add(t.Index);
                var abort = s["abort"] as JArray;
                list.Add(new Strategy
                {
                    Allowed = Lines(research, s["allowed"] as JArray, Kind.And),
                    Enable = Lines(research, s["enable"] as JArray, Kind.And),
                    Abort = abort != null && abort.Count > 0 ? Lines(research, abort, Kind.And) : null,
                    Weights = weights.ToArray(), Force = force.ToArray(),
                });
            }
            strategies = list.ToArray();
        }

        /// <summary>A block of trigger lines as one AND / OR node (an empty AND holds).</summary>
        static Node Lines(Hoi4Research research, JArray lines, Kind kind)
        {
            var kids = new List<Node>();
            if (lines != null)
                foreach (var l in lines)
                    kids.Add(l is JArray a && a.Count >= 3 ? Line(research, (string)a[0], (string)a[1], a[2]) : new Node { Kind = Kind.Unknown });
            return new Node { Kind = kind, Kids = kids.ToArray() };
        }

        static Node Line(Hoi4Research research, string key, string op, JToken v)
        {
            switch (key)
            {
                case "and": return Lines(research, v as JArray, Kind.And);
                case "or": return Lines(research, v as JArray, Kind.Or);
                case "not": return new Node { Kind = Kind.Not, Kids = new[] { Lines(research, v as JArray, Kind.And) } };
                case "date": return new Node { Kind = Kind.Date, Cmp = ToCmp(op), Value = PdxDate((string)v) };
                case "tag":
                case "original_tag": return new Node { Kind = Kind.Tag, Tag = (string)v };
                case "is_major": return new Node { Kind = Kind.Major, Yes = (string)v == "yes" };
                case "has_war": return new Node { Kind = Kind.War, Yes = (string)v == "yes" };
                case "has_tech": return new Node { Kind = Kind.HasTech, Value = research.Tech((string)v)?.Index ?? -1 };
                case "has_dlc": return new Node { Kind = Kind.Const, Yes = true };   // every DLC owned
                case "always": return new Node { Kind = Kind.Const, Yes = (string)v == "yes" };
                case "is_ai": return new Node { Kind = Kind.Const, Yes = (string)v == "yes" }; // the AI is asking
                case "num_of_military_factories": return Count(Kind.Mil, op, v);
                case "num_of_civilian_factories": return Count(Kind.Civ, op, v);
                case "num_of_naval_factories": return Count(Kind.Nav, op, v);
                case "num_of_factories": return Count(Kind.Factories, op, v);
                default: return new Node { Kind = Kind.Unknown };
            }
        }

        static Node Count(Kind kind, string op, JToken v) =>
            double.TryParse((string)v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double x)
                ? new Node { Kind = kind, Cmp = ToCmp(op), Number = x } : new Node { Kind = Kind.Unknown };

        static Cmp ToCmp(string op)
        {
            switch (op)
            {
                case ">": return Cmp.Gt;
                case "<": return Cmp.Lt;
                case ">=": return Cmp.Ge;
                case "<=": return Cmp.Le;
                case "!=": return Cmp.Ne;
                default: return Cmp.Eq;
            }
        }

        /// <summary>"1937.5.1" -> 19370501.</summary>
        static int PdxDate(string s)
        {
            var p = (s ?? "").Split('.');
            int y = p.Length > 0 && int.TryParse(p[0], out int a) ? a : 0;
            int m = p.Length > 1 && int.TryParse(p[1], out int b) ? b : 1;
            int d = p.Length > 2 && int.TryParse(p[2], out int c) ? c : 1;
            return y * 10000 + m * 100 + d;
        }
    }
}
