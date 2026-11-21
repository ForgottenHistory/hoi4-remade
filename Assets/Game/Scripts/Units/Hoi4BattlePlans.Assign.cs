using System.Collections.Generic;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the Battle Plans window's assign and unassign buttons (HOI4 btn_assign_units / btn_unassign_units).
    /// - Assign tool: click a front line and the selected divisions join it. A line of another army takes them into
    ///   that army (they leave theirs). Clicking an offensive assigns them to it: they join the line it starts
    ///   from and are kept on the offensive's front when the line spreads its divisions. Off again with Escape, right click, the button, or after a line is clicked.
    /// - Unassign: the selected divisions leave their front lines at once and stop. They stay off every line (the
    ///   hourly AutoAssign skips them) until assigned again or selected when a new line is drawn.
    /// </summary>
    public sealed partial class Hoi4BattlePlans
    {
        // Player divisions taken off their lines with Unassign: AutoAssign leaves them alone
        readonly HashSet<ushort> unassigned = new HashSet<ushort>();
        // Divisions assigned to an offensive: kept on the front it belongs to when their line spreads its divisions
        // (Distribute). Ignored once the offensive is gone or the division is on another army's line.
        readonly Dictionary<ushort, int> pinnedOffensive = new Dictionary<ushort, int>();

        /// <summary>The front of the plan a division is pinned to (its offensive's front), -1 none.</summary>
        int PinnedFront(Hoi4BattlePlan plan, ushort unit)
        {
            if (plan.IsAI || plan.Fronts == null || !pinnedOffensive.TryGetValue(unit, out int oid)) return -1;
            var o = GetOffensive(oid);
            if (o == null || o.ArmyId != plan.ArmyId) return -1;
            // The plan's front sharing the most provinces with the offensive's (fronts of lines against other
            // countries can share a corner province); ties: the lower front
            var overlap = new int[plan.Fronts.Count];
            foreach (ushort p in o.FrontSet)
                if (plan.FrontIndex.TryGetValue(p, out int f)) overlap[f]++;
            int best = -1;
            for (int f = 0; f < overlap.Length; f++)
                if (overlap[f] > 0 && (best < 0 || overlap[f] > overlap[best])) best = f;
            return best;
        }

        public bool AssignActive { get; private set; }

        /// <summary>The player's own divisions among the selected ones, sorted (assign / unassign act on these).</summary>
        public List<ushort> OwnSelection()
        {
            var result = new List<ushort>();
            var units = gameState?.Units;
            ushort player = Player;
            if (units == null || player == 0 || Hoi4UnitSelection.Instance == null) return result;
            foreach (ushort id in Hoi4UnitSelection.Instance.Selected)
                if (units.HasUnit(id) && units.GetUnit(id).countryID == player) result.Add(id);
            result.Sort();
            return result;
        }

        public bool HasOwnSelection => OwnSelection().Count > 0;

        /// <summary>At least one selected division is in an army (the Battle Plans window shows only then).</summary>
        public bool HasArmyMemberSelected
        {
            get
            {
                var armies = Hoi4Armies.Instance;
                if (armies == null) return false;
                foreach (ushort u in OwnSelection()) if (armies.ArmyOf(u) != null) return true;
                return false;
            }
        }

        public void BeginAssign()
        {
            if (!ready || !HasOwnSelection) return;
            if (Active) Cancel();
            if (OffensiveActive) CancelOffensive();
            CancelDelete();
            AssignActive = true;
            dirtyMesh = true;
            Changed?.Invoke();
        }

        public void CancelAssign()
        {
            if (!AssignActive) return;
            AssignActive = false;
            dirtyMesh = true;
            Changed?.Invoke();
        }

        /// <summary>The assign tool's click: the selected divisions join the front line under the cursor.</summary>
        bool OnAssignClick(int button)
        {
            if (button == 1) { CancelAssign(); return true; }
            if (button != 0) return true;
            var selected = OwnSelection();
            var target = hoverOffensive != null ? LineOf(hoverOffensive) : hoverPlan;
            if (target != null && !target.IsAI && target.Owner == Player && selected.Count > 0)
            {
                gameState.TryExecuteCommand(new AssignToPlanCommand { CountryId = Player, PlanId = target.Id,
                    OffensiveId = hoverOffensive?.Id ?? 0, UnitIds = selected }, out _);
                CancelAssign();
            }
            return true;
        }

        /// <summary>The front line an offensive starts from: its army's line holding the offensive's front (an
        /// offensive's divisions are that line's divisions slotted on it, AssignOffensives).</summary>
        Hoi4BattlePlan LineOf(Hoi4Offensive o)
        {
            // The army's line against the country holding the target, sharing the most provinces with the
            // offensive's front (lines against two countries can share a corner province); ties: the earlier line
            ushort enemy = o.Country != 0 ? o.Country : Hoi4Occupation.ControllerOf(gameState, o.Target);
            Hoi4BattlePlan best = null;
            int bestScore = 0;
            foreach (var plan in plans)
            {
                if (plan.IsAI || plan.ArmyId != o.ArmyId) continue;
                int overlap = 0;
                foreach (ushort p in o.FrontSet)
                    if (plan.FrontIndex.ContainsKey(p)) overlap++;
                if (overlap == 0) continue;
                int score = overlap + (plan.Country == enemy ? 1 << 20 : 0);
                if (score > bestScore) { best = plan; bestScore = score; }
            }
            return best;
        }

        /// <summary>AssignToPlanCommand: the divisions join the plan, and its army when they are in another one. With an
        /// offensive they go to the front it belongs to (and so are its divisions).</summary>
        public void AssignToPlan(int planId, List<ushort> unitIds, int offensiveId = 0)
        {
            var plan = GetPlan(planId);
            var armies = Hoi4Armies.Instance;
            if (plan == null || plan.IsAI || armies == null) return;
            var army = armies.Get(plan.ArmyId);
            if (army == null) return;
            var joining = new List<ushort>();
            foreach (ushort u in unitIds)
                if (gameState.Units.HasUnit(u)) joining.Add(u);
            if (joining.Count == 0) return;
            armies.Add(army, joining); // leaves its old army (no-op for divisions already in it)

            var touched = new SortedDictionary<int, Hoi4BattlePlan> { [plan.Id] = plan };
            foreach (ushort u in joining)
            {
                manualOrders.Remove(u);
                unassigned.Remove(u);
                if (offensiveId != 0) pinnedOffensive[u] = offensiveId; else pinnedOffensive.Remove(u);
                if (planOf.TryGetValue(u, out var old))
                {
                    if (old == plan) continue;
                    old.Units.Remove(u);
                    old.Slot.Remove(u);
                    touched[old.Id] = old;
                }
                plan.Units.Add(u);
                planOf[u] = plan;
            }
            foreach (var p in touched.Values) Distribute(p);
            AssignOffensives(); // the offensive's division count shows at once, also while paused
            Core.Recording.SessionRecorder.Note("plans", "assigned", plan.Owner, 0, 0, plan.Country,
                $"plan {plan.Id} army {plan.ArmyId}: {joining.Count} divisions assigned, {plan.Units.Count} on the line");
            dirtyMesh = true;
            Changed?.Invoke();
        }

        /// <summary>UnassignFromPlanCommand: the divisions leave their lines, stop, and stay off lines.</summary>
        public void UnassignFromPlans(List<ushort> unitIds)
        {
            var queue = gameState.Units.MovementQueue;
            var touched = new SortedDictionary<int, Hoi4BattlePlan>();
            int count = 0;
            foreach (ushort u in unitIds)
            {
                if (!gameState.Units.HasUnit(u)) continue;
                manualOrders.Remove(u);
                unassigned.Add(u);
                pinnedOffensive.Remove(u);
                if (planOf.TryGetValue(u, out var plan))
                {
                    if (plan.IsAI) continue;
                    planOf.Remove(u);
                    plan.Units.Remove(u);
                    plan.Slot.Remove(u);
                    touched[plan.Id] = plan;
                    count++;
                }
                if (!Hoi4Combat.IsRetreating(u)) queue.CancelMovement(u); // a retreat goes on
            }
            foreach (var p in touched.Values) Distribute(p);
            AssignOffensives();
            if (count > 0)
                Core.Recording.SessionRecorder.Note("plans", "unassigned", Player, 0, 0, 0, $"{count} divisions off their lines");
            dirtyMesh = true;
            Changed?.Invoke();
        }

        /// <summary>A division the player took off its line with Unassign (AutoAssign skips it).</summary>
        public bool IsUnassigned(ushort unitId) => unassigned.Contains(unitId);
    }
}
