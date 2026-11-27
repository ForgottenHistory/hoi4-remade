using System.Collections.Generic;
using Core.Data;
using Hoi4.Scripts;

namespace Hoi4.Focus
{
    /// <summary>
    /// GAME: the AI's national focus pick. A country without a running focus picks among the focuses it can start,
    /// at random by weight: each focus's `ai` (ai_will_do: a number, or a Lua function of the country), 1 when it
    /// has none. A focus weighing 0 or less is never picked.
    /// - Each country looks once a week, on its own day (country id modulo 7): the days it waits are saved days
    ///   (Hoi4Focus keeps up to 10), so nothing is lost and no day does every country's work.
    /// - Runs on the host only (Hoi4Simulation); the pick reaches every machine as a SelectFocusCommand. The
    ///   random number is seeded by the country and the day.
    /// - A weight function that asks something the game doesn't have reads it as false, so `not` of it counts:
    ///   such weights can be off until the vocabulary is there.
    /// - Not modelled: ai_strategy's focus weights and plans, HOI4's own look-ahead to later focuses.
    /// </summary>
    public static class Hoi4FocusAI
    {
        const int CycleDays = 7;

        public static void SimulateDay(int day)
        {
            var focus = Hoi4Focus.Instance;
            if (focus == null || !focus.IsReady) return;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            var player = StarterKit.Initializer.Instance?.PlayerState;
            ushort human = player != null && player.HasPlayerCountry ? player.PlayerCountryId : (ushort)0;
            List<(int focus, double weight)> open = null;
            foreach (ushort country in focus.Countries)
            {
                if (country == human || country % CycleDays != day % CycleDays || focus.Current(country) >= 0) continue;
                if (!Hoi4Script.Exists(country)) continue;      // a country with no land takes no focus
                var tree = focus.Tree(country);
                if (tree == null) continue;
                open ??= new List<(int, double)>();
                open.Clear();
                double total = 0;
                foreach (int f in tree.Focuses)
                {
                    if (!focus.CanSelect(country, f)) continue;
                    var def = focus.Focus(f);
                    double w = def.Ai.IsValid ? Hoi4Script.Number(def.Ai, country, 1) : def.AiBase;
                    if (w <= 0) continue;
                    open.Add((f, w));
                    total += w;
                }
                if (open.Count == 0) continue;
                var rng = new DeterministicRandom((uint)(country * 7919 + day * 31 + 1));
                double roll = rng.NextInt(1 << 20) / (double)(1 << 20) * total;
                int pick = open[open.Count - 1].focus;
                foreach (var (f, w) in open)
                {
                    roll -= w;
                    if (roll < 0) { pick = f; break; }
                }
                Core.Recording.SessionRecorder.Note("focus_ai", "pick", country, 0, 0, 0,
                    $"{focus.Focus(pick).Id} of {open.Count} open, weights {total:0.##}");
                gameState.TryExecuteCommand(new SelectFocusCommand { CountryId = country, Focus = pick }, out _);
            }
        }
    }
}
