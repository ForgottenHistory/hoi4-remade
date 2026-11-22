using System.Collections.Generic;
using Core;
using Hoi4.Units;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Deployment
{
    /// <summary>
    /// GAME: the simplest recruitment AI. Once a week (each country on its own weekday, (country id + 5) mod 7, not the
    /// production or construction AI's day) every country but the player's keeps enough divisions in training for its
    /// military factories: one line per 4 military factories, at least one. It trains its most common template (its
    /// line infantry, as HOI4's AI mostly does in 1936), deploying to the capital, and only while its manpower pool
    /// would keep twice the template's men after the new lines start (a reserve for reinforcing the field). Orders go
    /// through the same commands as the player's.
    /// </summary>
    public static class Hoi4DeploymentAI
    {
        const int FactoriesPerLine = 4;
        const int MaxNewPerWeek = 5;

        /// <summary>Daily, on the host only (Hoi4Simulation). `day` counts game days.</summary>
        public static void SimulateDay(int day)
        {
            var deployment = Hoi4Deployment.Instance;
            var construction = Hoi4.Construction.Hoi4Construction.Instance;
            var divisions = Hoi4Divisions.Instance;
            var manpower = Hoi4Manpower.Instance;
            if (deployment == null || !deployment.IsReady || construction == null || !construction.IsReady
                || divisions == null || manpower == null || !manpower.IsReady) return;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            ushort player = PlayerCountry;

            Dictionary<ushort, List<Hoi4Division>> byCountry = null;
            foreach (ushort country in Countries(gameState, divisions))
            {
                if (country == player || (country + 5) % 7 != day % 7) continue;
                int wanted = System.Math.Max(1, construction.Military(country) / FactoriesPerLine);
                int lines = 0;
                foreach (var o in deployment.Orders(country)) lines += o.Lines.Count;
                if (lines >= wanted) continue;

                if (byCountry == null) byCountry = Hoi4Equipment.DivisionsByCountry(gameState, divisions);
                byCountry.TryGetValue(country, out var field);
                string template = MostCommonTemplate(field, deployment.Tag(country), divisions);
                if (template == null || !deployment.CanTrain(country, template)) continue;

                // men the waiting lines will take, then keep a reserve of two divisions' men in the pool
                int men = TemplateManpower(field, template);
                FP pool = manpower.Available(country);
                foreach (var o in deployment.Orders(country))
                    foreach (var l in o.Lines)
                        if (!l.Started) pool -= FP.FromInt(l.Division.Manpower);
                int affordable = men > 0 ? (int)((pool - FP.FromInt(2 * men)) / FP.FromInt(men)).ToInt() : 0;
                int add = System.Math.Min(System.Math.Min(wanted - lines, MaxNewPerWeek), affordable);
                if (add <= 0) continue;

                Hoi4Deployment.Order existing = null;
                foreach (var o in deployment.Orders(country)) if (o.Template == template) { existing = o; break; }
                bool ok = existing != null
                    ? gameState.TryExecuteCommand(new ChangeDeploymentAmountCommand { CountryId = country, OrderId = existing.Id, Amount = add }, out _)
                    : gameState.TryExecuteCommand(new CreateDeploymentOrderCommand { CountryId = country, Template = template, Amount = add }, out _);
                if (ok)
                    Core.Recording.SessionRecorder.Note("deployment", "ai_train", country, 0, 0, 0,
                        $"{add} x {template} (lines {lines + add}/{wanted}, military factories {construction.Military(country)}, pool {manpower.Available(country).ToInt()})");
            }
        }

        /// <summary>The template most of the country's field divisions use (ties: the first in the file).</summary>
        static string MostCommonTemplate(List<Hoi4Division> field, string tag, Hoi4Divisions divisions)
        {
            var count = new Dictionary<string, int>();
            if (field != null) foreach (var d in field) count[d.Template] = (count.TryGetValue(d.Template, out int n) ? n : 0) + 1;
            string best = null;
            int bestCount = -1;
            foreach (string t in divisions.Templates(tag))
            {
                int n = count.TryGetValue(t, out int v) ? v : 0;
                if (n > bestCount) { best = t; bestCount = n; }
            }
            return best;
        }

        static int TemplateManpower(List<Hoi4Division> field, string template)
        {
            if (field != null) foreach (var d in field) if (d.Template == template) return d.Manpower;
            return 0;
        }

        /// <summary>Countries with divisions, in id order.</summary>
        static List<ushort> Countries(GameState gameState, Hoi4Divisions divisions)
        {
            var set = new SortedSet<ushort>();
            foreach (var d in divisions.All.Values)
                if (gameState.Units.HasUnit(d.UnitId)) set.Add(gameState.Units.GetUnit(d.UnitId).countryID);
            return new List<ushort>(set);
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
