using System.Collections.Generic;
using Core;

namespace Hoi4.Construction
{
    /// <summary>
    /// GAME: the simplest construction AI. Once a week (each country on its own weekday, (country id + 3) mod 7, so
    /// not the production AI's day) every country but the player's keeps its queue long enough for all its available
    /// civilian factories (15 per item, plus one spare). What it builds: civilian factories until 1938 (HOI4's usual
    /// build-up), military factories from then on, or while at war. Where: the state with a free shared slot and the
    /// best infrastructure (it speeds construction up), then the most free slots, then the lowest id. With no shared
    /// slot left anywhere it raises infrastructure where it is lowest. Orders go through the same commands as the player's.
    /// </summary>
    public static class Hoi4ConstructionAI
    {
        const int MilitaryFromYear = 1938;

        /// <summary>Daily, on the host only (Hoi4Simulation). `day` counts game days.</summary>
        public static void SimulateDay(int day)
        {
            var construction = Hoi4Construction.Instance;
            if (construction == null || !construction.IsReady) return;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            ushort player = PlayerCountry;
            int year = gameState.Time.GetCurrentGameTime().Year;

            // states each country may build in, in state id order
            Dictionary<ushort, List<int>> statesOf = null;
            foreach (var econ in Countries(construction))
            {
                ushort country = econ;
                if (country == player || (country + 3) % 7 != day % 7) continue;
                int available = construction.Available(country);
                if (available <= 0) continue;
                int wanted = (available + Hoi4Construction.MaxFactoriesPerItem - 1) / Hoi4Construction.MaxFactoriesPerItem + 1;
                int queued = construction.Queue(country).Count;
                if (queued >= wanted) continue;
                if (statesOf == null)
                {
                    statesOf = new Dictionary<ushort, List<int>>();
                    foreach (var kv in Hoi4Buildings.Users(gameState))
                    {
                        if (!statesOf.TryGetValue(kv.Value, out var l)) statesOf[kv.Value] = l = new List<int>();
                        l.Add(kv.Key);
                    }
                }
                if (!statesOf.TryGetValue(country, out var states)) continue;
                bool atWar = gameState.Diplomacy != null && gameState.Diplomacy.IsAtWar(country);
                string factory = year >= MilitaryFromYear || atWar ? "arms_factory" : "industrial_complex";

                for (int n = queued; n < wanted; n++)
                {
                    int state = BestFactoryState(construction, country, states, factory);
                    string building = factory;
                    if (state == 0)
                    {
                        state = LowestInfrastructure(construction, country, states);
                        building = "infrastructure";
                    }
                    if (state == 0) break;
                    if (!gameState.TryExecuteCommand(new QueueConstructionCommand { CountryId = country, State = state, Building = building }, out _))
                        break;
                    Core.Recording.SessionRecorder.Note("construction", "ai_queue", country, 0, 0, 0,
                        $"{building} in state {state} (free civilian factories {available}, queue {n + 1}/{wanted})");
                }
            }
        }

        static int BestFactoryState(Hoi4Construction construction, ushort country, List<int> states, string building)
        {
            int best = 0, bestInfra = -1, bestFree = 0;
            foreach (int s in states)
            {
                int free = construction.FreeSharedSlots(s);
                if (free <= 0 || !construction.CanQueue(country, s, building)) continue;
                int infra = Hoi4Buildings.Level(s, "infrastructure");
                if (infra > bestInfra || (infra == bestInfra && free > bestFree))
                {
                    best = s; bestInfra = infra; bestFree = free;
                }
            }
            return best;
        }

        static int LowestInfrastructure(Hoi4Construction construction, ushort country, List<int> states)
        {
            int best = 0, bestInfra = int.MaxValue;
            foreach (int s in states)
            {
                if (!construction.CanQueue(country, s, "infrastructure")) continue;
                int infra = Hoi4Buildings.Level(s, "infrastructure");
                if (infra < bestInfra) { best = s; bestInfra = infra; }
            }
            return best;
        }

        /// <summary>Countries with civilian factories, in id order.</summary>
        static List<ushort> Countries(Hoi4Construction construction)
        {
            var gameState = Engine.ArchonEngine.Instance.GameState;
            var set = new SortedSet<ushort>();
            foreach (var kv in Hoi4Buildings.Users(gameState)) set.Add(kv.Value);
            var list = new List<ushort>();
            foreach (ushort c in set) if (construction.Civilian(c) > 0) list.Add(c);
            return list;
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
