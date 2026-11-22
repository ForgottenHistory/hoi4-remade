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
    /// off a line. Orders go through the same commands as the player's.
    /// </summary>
    public static class Hoi4ProductionAI
    {
        static readonly FP Reserve = FP.FromFraction(1, 10);

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
                int free = production.Free(country);
                if (free <= 0) continue;
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

                var weight = new Dictionary<int, FP>();
                var assigned = new Dictionary<int, int>();
                foreach (int a in archetypes)
                {
                    production.TryGetBuildable(country, a, out var b);
                    FP shortfall = FP.Max(FP.Zero, lack[a] - equipment.Stock(country, a));
                    weight[a] = (shortfall + need[a] * Reserve) * b.Cost;
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
