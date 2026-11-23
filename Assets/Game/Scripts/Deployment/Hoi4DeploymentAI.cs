using System.Collections.Generic;
using Core;
using Hoi4.Units;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Deployment
{
    /// <summary>
    /// GAME: the recruitment AI. Once a week (each country on its own weekday, (country id + 5) mod 7, not the
    /// production or construction AI's day) every country but the player's trains what its manpower and equipment
    /// allow, keeping a bank of Bank (10%) of the deployed army's men and of its equipment use: a new line only when
    /// the pool, after what waiting lines will take, keeps that bank, and so does the stockpile of every archetype the
    /// template needs, after what the field and the lines in training still lack. So recruitment follows industry
    /// and population, and the production AI (which stocks up to a quarter of the army's use) feeds it.
    /// Which template: once Hoi4ArmyAI has planned the country, its role templates by the plan's shares of the army
    /// (field and training; the role furthest under its share first, the next one when that can't be afforded;
    /// divisions of other templates count by kind: tanks as breakthrough, mobile as mobile, the rest as defence),
    /// before that its most common template. Each AI line trains one division (amount 1), so every new division is
    /// chosen again. Deploys to the capital; at most MaxNewPerWeek new lines a week. Orders go through the same
    /// commands as the player's.
    /// </summary>
    public static class Hoi4DeploymentAI
    {
        static readonly FP Bank = FP.FromFraction(1, 10);
        const int MaxNewPerWeek = 10;

        /// <summary>Daily, on the host only (Hoi4Simulation). `day` counts game days.</summary>
        public static void SimulateDay(int day)
        {
            var deployment = Hoi4Deployment.Instance;
            var divisions = Hoi4Divisions.Instance;
            var manpower = Hoi4Manpower.Instance;
            var equipment = Hoi4Equipment.Instance;
            if (deployment == null || !deployment.IsReady || divisions == null || manpower == null || !manpower.IsReady
                || equipment == null || !equipment.IsReady) return;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            ushort player = PlayerCountry;
            int n = Hoi4Divisions.Archetypes.Length;

            Dictionary<ushort, List<Hoi4Division>> byCountry = null;
            foreach (ushort country in Countries(gameState, divisions))
            {
                if (country == player || (country + 5) % 7 != day % 7) continue;
                if (byCountry == null) byCountry = Hoi4Equipment.DivisionsByCountry(gameState, divisions);
                byCountry.TryGetValue(country, out var field);
                string tag = deployment.Tag(country);
                var plan = Hoi4ArmyAI.PlanOf(country);

                // what's left above the bank: men in the pool after the waiting lines, equipment in the stockpile
                // after what the field and the lines still lack
                FP fieldMen = FP.Zero;
                var fieldNeed = new FP[n];
                var spare = new FP[n];
                for (int a = 0; a < n; a++) spare[a] = equipment.Stock(country, a);
                if (field != null)
                    foreach (var d in field)
                    {
                        fieldMen += FP.FromInt(d.Manpower);
                        if (d.Need == null) continue;
                        for (int a = 0; a < n && a < d.Need.Length; a++)
                        {
                            fieldNeed[a] += d.Need[a];
                            if (d.Have[a] < d.Need[a]) spare[a] -= d.Need[a] - d.Have[a];
                        }
                    }
                FP men = manpower.Available(country);
                int lines = 0;
                foreach (var o in deployment.Orders(country))
                    foreach (var l in o.Lines)
                    {
                        lines++;
                        var d = l.Division;
                        if (!l.Started) men -= FP.FromInt(d.Manpower) - d.Men;
                        if (d.Need == null) continue;
                        for (int a = 0; a < n && a < d.Need.Length; a++)
                            if (d.Have[a] < d.Need[a]) spare[a] -= d.Need[a] - d.Have[a];
                    }
                men -= fieldMen * Bank;
                for (int a = 0; a < n; a++) spare[a] -= fieldNeed[a] * Bank;

                // the roles in the order recruitment wants them, and what each template takes
                var count = new int[Hoi4ArmyAI.RoleCount];
                int total = 0;
                if (plan != null)
                {
                    if (field != null)
                        foreach (var d in field)
                        {
                            int r = System.Array.IndexOf(plan.Templates, d.Template);
                            if (r < 0)
                            {
                                string g = divisions.SubUnitGroup(d.MainType);
                                r = g == "armor" ? Hoi4ArmyAI.Breakthrough : g == "mobile" ? Hoi4ArmyAI.Mobile : Hoi4ArmyAI.Defence;
                            }
                            count[r]++;
                            total++;
                        }
                    foreach (var o in deployment.Orders(country))
                    {
                        int r = System.Array.IndexOf(plan.Templates, o.Template);
                        if (r < 0) continue;
                        count[r] += o.Lines.Count;
                        total += o.Lines.Count;
                    }
                }
                string fallback = plan == null ? MostCommonTemplate(field, tag, divisions) : null;
                var cost = new Dictionary<string, Hoi4TemplateStats>();
                Hoi4TemplateStats Cost(string t)
                {
                    if (!cost.TryGetValue(t, out var st))
                    {
                        var design = divisions.Design(tag, t);
                        st = design != null ? divisions.Stats(design.RegimentTypes(), design.SupportTypes(), design.RegimentalTypes()) : default;
                        cost[t] = st;
                    }
                    return st;
                }
                bool Affordable(string t)
                {
                    var st = Cost(t);
                    if (st.Manpower <= 0 || men < FP.FromInt(st.Manpower)) return false;
                    if (st.Need != null)
                        for (int a = 0; a < n && a < st.Need.Length; a++)
                            if (st.Need[a] > FP.Zero && spare[a] < st.Need[a]) return false;
                    return true;
                }

                var picks = new Dictionary<string, int>();
                for (int k = 0; k < MaxNewPerWeek; k++)
                {
                    string pick = null;
                    int role = -1;
                    if (plan == null)
                    {
                        if (fallback != null && deployment.CanTrain(country, fallback) && Affordable(fallback)) pick = fallback;
                    }
                    else
                    {
                        // furthest under its share first; an unaffordable role gives way to the next
                        var order = new List<int>();
                        for (int r = 0; r < Hoi4ArmyAI.RoleCount; r++)
                            if (plan.Templates[r] != null && plan.Shares[r] > 0 && deployment.CanTrain(country, plan.Templates[r])) order.Add(r);
                        int tot = total + 1;
                        order.Sort((x, y) =>
                        {
                            int c = (plan.Shares[y] * tot - count[y]).CompareTo(plan.Shares[x] * tot - count[x]);
                            return c != 0 ? c : x.CompareTo(y);
                        });
                        foreach (int r in order)
                            if (Affordable(plan.Templates[r])) { pick = plan.Templates[r]; role = r; break; }
                    }
                    if (pick == null) break;
                    var st = Cost(pick);
                    men -= FP.FromInt(st.Manpower);
                    if (st.Need != null) for (int a = 0; a < n && a < st.Need.Length; a++) spare[a] -= st.Need[a];
                    if (role >= 0) { count[role]++; total++; }
                    picks[pick] = (picks.TryGetValue(pick, out int c2) ? c2 : 0) + 1;
                }

                foreach (var pick in picks)
                {
                    Hoi4Deployment.Order existing = null;
                    foreach (var o in deployment.Orders(country)) if (o.Template == pick.Key) { existing = o; break; }
                    bool ok = existing != null
                        ? gameState.TryExecuteCommand(new AddDeploymentLinesCommand { CountryId = country, OrderId = existing.Id, Lines = pick.Value }, out _)
                        : gameState.TryExecuteCommand(new CreateDeploymentOrderCommand { CountryId = country, Template = pick.Key, Lines = pick.Value }, out _);
                    if (!ok) continue;
                    foreach (var o in deployment.Orders(country))
                        if (o.Template == pick.Key && o.Amount != 1)
                            gameState.TryExecuteCommand(new SetDeploymentAmountCommand { CountryId = country, OrderId = o.Id, Amount = 1 }, out _);
                    Core.Recording.SessionRecorder.Note("deployment", "ai_train", country, 0, 0, 0,
                        $"{pick.Value} x {pick.Key} (lines {lines} before, pool {manpower.Available(country).ToInt()}, " +
                        $"bank {(fieldMen * Bank).ToInt()} men)");
                }
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
