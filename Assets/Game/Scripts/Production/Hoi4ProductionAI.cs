using System.Linq;
using System.Collections.Generic;
using Core;
using Hoi4.Units;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Production
{
    /// <summary>
    /// GAME: the simplest production AI. Once a week (each country on its own weekday, country id mod 7) every
    /// country but the player's puts its free military factories to work, one factory at a time on the archetype
    /// with the most need per factory already making it. Need, in IC: what its divisions lack beyond the stockpile,
    /// plus a 10% reserve of what they use in all (so a country at peace keeps building). It never takes factories
    /// off a line, except that with an army plan (Hoi4ArmyAI) the reserve follows the plan's factory split (none once
    /// the stockpile holds a quarter of the army's use), lines of archetypes outside the plan go once nothing lacks
    /// them, and up to two factories a week move from lines over the plan, or whose stockpile is past that ceiling
    /// (a country short of men piles up rifles), to archetypes under the plan or the planned one least stocked. What lacks counts
    /// the divisions in training as well as the field (a line can't finish without its equipment). Orders go through the same commands as the player's.
    /// </summary>
    public static class Hoi4ProductionAI
    {
        static readonly FP Reserve = FP.FromFraction(1, 10);
        static readonly FP StockCap = FP.FromFraction(1, 4); // with a plan: no reserve once the stockpile holds this share of the army's use
        const int MovesPerWeek = 2;                          // with a plan: factories moved a week towards its split

        /// <summary>Daily, on the host only (Hoi4Simulation).</summary>
        public static void SimulateDay(int day)
        {
            var production = Hoi4Production.Instance;
            var equipment = Hoi4Equipment.Instance;
            if (production == null || !production.IsReady || equipment == null || !equipment.IsReady
                || Hoi4Divisions.Instance == null) return;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            ushort player = PlayerCountry;
            Dictionary<ushort, List<Hoi4Division>> byCountry = null;
            int n = Hoi4Divisions.Archetypes.Length;

            int infantry = System.Array.IndexOf(Hoi4Divisions.Archetypes, "infantry_equipment");
            var ids = new List<ushort>();
            foreach (ushort c in production.Countries) if (c != player && c % 7 == day % 7) ids.Add(c);
            foreach (ushort country in ids)
            {
                // research unlocked newer equipment: lines switch to it (HOI4's AI upgrades its lines)
                foreach (var l in production.Lines(country).ToList())
                    if (production.CanSwitch(country, l.Id, l.Archetype))
                        gameState.TryExecuteCommand(new SwitchLineEquipmentCommand { CountryId = country, LineId = l.Id, Archetype = l.Archetype }, out _);
                int free = production.Free(country);
                if (free <= 0 && Hoi4.Deployment.Hoi4ArmyAI.PlanOf(country) == null) continue; // a plan may give lines up
                var archetypes = production.BuildableArchetypes(country);
                if (archetypes.Count == 0) continue;
                if (byCountry == null) byCountry = Hoi4Equipment.DivisionsByCountry(gameState, Hoi4Divisions.Instance);

                var need = new FP[n];
                var lack = new FP[n];
                if (byCountry.TryGetValue(country, out var divisions))
                    foreach (var d in divisions)
                    {
                        if (d.Need == null) continue;
                        for (int a = 0; a < n; a++)
                        {
                            need[a] += d.Need[a];
                            if (d.Have[a] < d.Need[a]) lack[a] += d.Need[a] - d.Have[a];
                        }
                    }
                // divisions in training lack equipment too (a line can't finish without it)
                var deployment = Hoi4.Deployment.Hoi4Deployment.Instance;
                if (deployment != null && deployment.IsReady)
                    foreach (var o in deployment.Orders(country))
                        foreach (var line in o.Lines)
                        {
                            var d = line.Division;
                            if (d?.Need == null) continue;
                            for (int a = 0; a < n && a < d.Need.Length; a++)
                                if (d.Have[a] < d.Need[a]) lack[a] += d.Need[a] - d.Have[a];
                        }

                // with an army plan (Hoi4ArmyAI): the reserve goes by the plan's factory split instead of by what the
                // field uses, and lines of archetypes the plan doesn't make and the field doesn't lack are given up
                var plan = Hoi4.Deployment.Hoi4ArmyAI.PlanOf(country);
                FP reserveIc = FP.Zero;
                if (plan != null && plan.Military > 0) // no military factories: nothing to rebalance
                {
                    foreach (int a in archetypes)
                        if (production.TryGetBuildable(country, a, out var rb)) reserveIc += need[a] * Reserve * rb.Cost;
                    foreach (var l in production.Lines(country).ToList())
                    {
                        int a = l.Archetype;
                        bool planned = a < plan.Factories.Length && plan.Factories[a] > 0;
                        if (!planned && lack[a] <= equipment.Stock(country, a)
                            && gameState.TryExecuteCommand(new RemoveProductionLineCommand { CountryId = country, LineId = l.Id }, out _))
                            Core.Recording.SessionRecorder.Note("production", "ai_drop", country, 0, 0, 0,
                                $"{Hoi4Divisions.Archetypes[a]} line given up (not in the army plan, nothing lacking)");
                    }
                    // move factories towards the plan's split: off lines making more than the plan wants, or whose
                    // stockpile is past the ceiling (a country short of men can't use more rifles), nothing lacking,
                    // to archetypes under the plan or, failing that, the planned one with the least stock for its use
                    var have = new int[n];
                    foreach (var l in production.Lines(country)) if (l.Archetype < n) have[l.Archetype] += l.Requested;
                    bool Full(int a) => equipment.Stock(country, a) >= need[a] * StockCap + FP.FromInt(100) && lack[a] <= equipment.Stock(country, a);
                    for (int move = 0; move < MovesPerWeek; move++)
                    {
                        if (production.Free(country) > move) break; // free factories already
                        int want = -1, gap = 0;
                        foreach (int a in archetypes)
                        {
                            int target = a < plan.Factories.Length ? plan.Factories[a] : 0;
                            int g = target - have[a];
                            if (g <= 0 && have[a] == 0 && lack[a] > equipment.Stock(country, a)) g = 1;
                            if (g > gap && !Full(a)) { gap = g; want = a; }
                        }
                        bool overCeiling = false;
                        if (want < 0)
                        {
                            want = LeastStocked(archetypes, plan, need, equipment, country, Full);
                            overCeiling = true;
                        }
                        if (want < 0) break;
                        ProductionLine donor = null;
                        FP surplus = FP.Zero;
                        foreach (var l in production.Lines(country))
                        {
                            int a = l.Archetype;
                            if (a == want || l.Requested <= 0 || lack[a] > equipment.Stock(country, a)) continue;
                            int over = have[a] - (a < plan.Factories.Length ? plan.Factories[a] : 0);
                            // over the plan counts first; past the ceiling counts by how far (only when nothing is under the plan)
                            FP s = !overCeiling && over > 0 ? FP.FromInt(1000 + over)
                                : Full(a) && need[a] > FP.Zero ? equipment.Stock(country, a) / need[a] : FP.Zero;
                            if (s > surplus) { surplus = s; donor = l; }
                        }
                        if (donor == null) break;
                        int da = donor.Archetype;
                        bool ok = donor.Requested > 1
                            ? gameState.TryExecuteCommand(new SetLineFactoriesCommand { CountryId = country, LineId = donor.Id, Factories = donor.Requested - 1 }, out _)
                            : gameState.TryExecuteCommand(new RemoveProductionLineCommand { CountryId = country, LineId = donor.Id }, out _);
                        if (!ok) break;
                        have[da]--;
                        Core.Recording.SessionRecorder.Note("production", "ai_move", country, 0, 0, 0,
                            $"1 factory off {Hoi4Divisions.Archetypes[da]} towards {Hoi4Divisions.Archetypes[want]} (army plan)");
                    }
                    free = production.Free(country);
                    if (free <= 0) continue;
                }

                var weight = new Dictionary<int, FP>();
                var assigned = new Dictionary<int, int>();
                foreach (int a in archetypes)
                {
                    production.TryGetBuildable(country, a, out var b);
                    FP shortfall = FP.Max(FP.Zero, lack[a] - equipment.Stock(country, a));
                    FP reserve = plan == null ? need[a] * Reserve * b.Cost
                        : a < plan.Factories.Length && plan.Military > 0 && equipment.Stock(country, a) < need[a] * StockCap + FP.FromInt(100)
                            ? reserveIc * FP.FromInt(plan.Factories[a]) / FP.FromInt(plan.Military) + FP.FromInt(plan.Factories[a])
                            : FP.Zero;
                    weight[a] = shortfall * b.Cost + reserve;
                    assigned[a] = 0;
                }
                foreach (var l in production.Lines(country))
                    if (assigned.ContainsKey(l.Archetype)) assigned[l.Archetype] += l.Requested;
                var start = new Dictionary<int, int>(assigned);

                for (int f = 0; f < free; f++)
                {
                    int pick = -1;
                    FP best = FP.Zero;
                    foreach (int a in archetypes)
                    {
                        FP score = weight[a] / (assigned[a] + 1);
                        if (score > best) { best = score; pick = a; }
                    }
                    if (pick < 0 && plan != null) pick = LeastStocked(archetypes, plan, need, equipment, country, a => false);
                    if (pick < 0) pick = archetypes.Contains(infantry) ? infantry : archetypes[0]; // nothing needed: infantry equipment
                    assigned[pick]++;
                }

                foreach (int a in archetypes)
                {
                    int more = assigned[a] - start[a];
                    if (more <= 0) continue;
                    ProductionLine line = null;
                    foreach (var l in production.Lines(country))
                        if (l.Archetype == a && l.Requested < Hoi4Production.MaxFactoriesPerLine) { line = l; break; }
                    if (line != null)
                    {
                        int before = line.Requested; // read before the command: it changes this line
                        int to = System.Math.Min(Hoi4Production.MaxFactoriesPerLine, before + more);
                        if (gameState.TryExecuteCommand(new SetLineFactoriesCommand { CountryId = country, LineId = line.Id, Factories = to }, out _))
                            more -= to - before;
                    }
                    if (more > 0)
                        gameState.TryExecuteCommand(new AddProductionLineCommand { CountryId = country, Archetype = a, Factories = System.Math.Min(more, Hoi4Production.MaxFactoriesPerLine) }, out _);
                    Core.Recording.SessionRecorder.Note("production", "ai_assign", country, 0, 0, 0,
                        $"+{assigned[a] - start[a]} factories to {Hoi4Divisions.Archetypes[a]} (need {weight[a].ToInt()} ic)");
                }
            }
        }

        /// <summary>The planned archetype with the least stockpile for the army's use (not full), -1 when none.</summary>
        static int LeastStocked(List<int> archetypes, Hoi4.Deployment.Hoi4ArmyAI.Plan plan, FP[] need, Hoi4Equipment equipment,
                                ushort country, System.Func<int, bool> full)
        {
            int best = -1;
            FP lowest = FP.MaxValue;
            foreach (int a in archetypes)
            {
                if (a >= plan.Factories.Length || plan.Factories[a] <= 0 || full(a)) continue;
                FP ratio = need[a] > FP.Zero ? equipment.Stock(country, a) / need[a] : equipment.Stock(country, a);
                if (ratio < lowest) { lowest = ratio; best = a; }
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
