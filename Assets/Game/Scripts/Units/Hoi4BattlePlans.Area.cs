using System.Collections.Generic;
using Hoi4.Map;
using Map.Rendering.Border;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>An army's area defence order: the states it guards and what in them (Hoi4BattlePlans.Guard* flags).</summary>
    public sealed class Hoi4AreaDefense
    {
        public readonly SortedSet<int> States = new SortedSet<int>();
        public int Guard;
    }

    /// <summary>
    /// Area defence, HOI4's garrison order (btn_area_defense, battleplantools_area_defense_config_window): an army
    /// guards states instead of a line. With a division of an army selected the button turns the tool on and opens
    /// the Defense window (Hoi4BattlePlanPanel): what to guard in the chosen states, in HOI4's order victory points,
    /// naval bases, coastline, airbases, borders, forts, supply hubs and railway junctions, special project
    /// facilities; several at once. A left click on a state adds it to the order or takes it out (green stripes);
    /// every click is the order (SetAreaDefenseCommand), there is nothing to confirm, and taking the last state
    /// out removes it. "Min. divisions" is the number of provinces the order guards: one division each.
    /// With the tool off, the states are striped in the army's colour, dim, while a division of the army is selected.
    /// The order is a battle plan (Hoi4BattlePlan.Area set, no country, nothing drawn on the map): its fronts are
    /// the guarded provinces in order of importance (the windows' order; victory points by value), one front per
    /// piece of land the divisions can walk within, so Distribute, Hold, manual orders and unassign work as on a
    /// line. Fewer divisions than provinces take the most important ones. Every division of the army is on it:
    /// they can't be assigned to another line or unassigned. It is the army's only order, like HOI4:
    /// giving it removes the army's lines and offensives, and drawing a line removes it (the tools warn first).
    /// Not here yet: airbases and special project facilities (no province data), railway junctions (hubs only),
    /// and forts built after the start.
    /// </summary>
    public sealed partial class Hoi4BattlePlans
    {
        public const int GuardVictoryPoints = 1, GuardPorts = 2, GuardCoast = 4, GuardAirbases = 8, GuardBorders = 16,
            GuardForts = 32, GuardHubs = 64, GuardFacilities = 128;
        /// <summary>The guard kinds there is data for (the others' buttons are disabled).</summary>
        public const int GuardAvailable = GuardVictoryPoints | GuardPorts | GuardCoast | GuardBorders | GuardForts | GuardHubs;
        const int GuardDefault = GuardVictoryPoints | GuardPorts | GuardForts | GuardHubs;
        static readonly int[] GuardOrder = { GuardVictoryPoints, GuardPorts, GuardCoast, GuardBorders, GuardForts, GuardHubs };

        [Tooltip("The area defence tool's chosen states")] public Color areaPickedColor = new Color(0.25f, 0.9f, 0.25f, 1f);
        [Tooltip("An army's guarded states, shown while it is selected: its colour times this")] public float areaDim = 0.75f;

        /// <summary>True while the area defence tool is on.</summary>
        public bool AreaActive { get; private set; }
        int areaArmyId;          // the army the tool edits
        int areaPendingGuard = GuardDefault;   // the window's choice before the army has an order
        int areaHoverState = -1;
        Hoi4BattlePlan shownArea;              // presentation: the selected army's order, striped while no tool is on
        int shownAreaStamp = -1;
        int areaStamp;                         // bumped when an order changes (presentation)
        bool[] coastal;                        // by province id: next to water

        /// <summary>The army's area defence order, null when it has none.</summary>
        public Hoi4BattlePlan AreaOf(int armyId)
        {
            foreach (var p in plans) if (p.IsArea && p.ArmyId == armyId) return p;
            return null;
        }

        /// <summary>The division's army has an area defence order: it is on it with the whole army, and can't be
        /// assigned elsewhere or unassigned (HOI4 greys those buttons out).</summary>
        public bool IsAreaDefended(ushort unitId)
        {
            var army = Hoi4Armies.Instance?.ArmyOf(unitId);
            return army != null && AreaOf(army.Id) != null;
        }

        /// <summary>One of the player's selected divisions is in an army with an area defence order.</summary>
        public bool SelectionAreaDefended
        {
            get
            {
                foreach (ushort u in OwnSelection()) if (IsAreaDefended(u)) return true;
                return false;
            }
        }

        /// <summary>The army has an order other than area defence (a line or an offensive).</summary>
        bool HasOtherOrders(int armyId)
        {
            foreach (var p in plans) if (!p.IsArea && p.ArmyId == armyId) return true;
            return ArmyHasOffensive(armyId);
        }

        // --- Tool ------------------------------------------------------------------------------------

        public void BeginArea()
        {
            if (!ready || ArmySelection(out var army) == null) return;
            if (Active) Cancel();
            if (OffensiveActive) CancelOffensive();
            CancelFallback();
            CancelDelete();
            CancelAssign();
            AreaActive = true;
            areaArmyId = army.Id;
            areaHoverState = -1;
            hoverProvince = 0;
            if (HasOtherOrders(army.Id)) WarnOrdersReplaced();
            dirtyTex = true;
            RebuildTexture();
            Changed?.Invoke();
        }

        public void CancelArea()
        {
            if (!AreaActive) return;
            AreaActive = false;
            areaHoverState = -1;
            dirtyTex = true;
            RebuildTexture();
            Changed?.Invoke();
        }

        /// <summary>HOI4's AREA_DEFENSE_REPLACE_OTHER_DESC: shown when a tool is picked whose order would replace
        /// the army's standing ones. OK only closes it; the orders go when the new one is given.</summary>
        static void WarnOrdersReplaced() =>
            Hoi4.UI.Hoi4ConfirmDialog.Instance?.Notice("Warning",
                "If you draw an order with this tool, any other standing orders the group has will be removed.");

        /// <summary>A line tool was picked: warn when the selected army has an area defence order (it would go).</summary>
        void WarnIfAreaDefended()
        {
            if (ArmySelection(out var army) != null && AreaOf(army.Id) != null) WarnOrdersReplaced();
        }

        /// <summary>What the Defense window shows as guarded: the army's order's choice, or the one waiting for it.</summary>
        public int AreaGuard => AreaActive && AreaOf(areaArmyId) is Hoi4BattlePlan p ? p.Area.Guard : areaPendingGuard;

        /// <summary>The window's "Min. divisions": the provinces the tool's order guards.</summary>
        public int AreaMinDivisions => AreaActive && AreaOf(areaArmyId) is Hoi4BattlePlan p ? p.FrontProvinces?.Count ?? 0 : 0;

        /// <summary>The Defense window's buttons: guard this kind too, or no longer.</summary>
        public void ToggleAreaGuard(int flag)
        {
            if (!AreaActive || (flag & GuardAvailable) == 0) return;
            var plan = AreaOf(areaArmyId);
            if (plan == null) { areaPendingGuard ^= flag; Changed?.Invoke(); return; }
            gameState.TryExecuteCommand(new SetAreaDefenseCommand { CountryId = Player, ArmyId = areaArmyId,
                States = StateList(plan.Area.States), Guard = plan.Area.Guard ^ flag }, out _);
        }

        static List<ushort> StateList(IEnumerable<int> states)
        {
            var list = new List<ushort>();
            foreach (int s in states) list.Add((ushort)s);
            return list;
        }

        void UpdateAreaTool()
        {
            if (Hoi4.UI.Hoi4ConfirmDialog.IsOpen) return; // the warning is up: its Escape isn't the tool's
            if (global::Utils.KeyboardFocus.KeyDown(KeyCode.Escape)) { EscapeTool(); return; }
            if (ArmySelection(out var army) == null || army.Id != areaArmyId) { CancelArea(); return; }
            ushort province = 0;
            var cam = Camera.main;
            var selector = Engine.ArchonEngine.Instance.ProvinceSelector;
            if (cam != null && selector != null && !global::Map.CameraControllers.BaseCameraController.ScrollCaptured)
                province = selector.GetProvinceAtScreenPosition(Input.mousePosition, cam);
            hoverProvince = province;
            int state = province != 0 && !Hoi4Regions.IsWater(province) ? Hoi4Regions.State(province) : -1;
            if (state > 0 && !CanDefendState(Player, state)) state = -1;
            areaHoverState = state; // what a click toggles
        }

        /// <summary>Left click on a state adds it to the army's order or takes it out. Right click closes the tool.</summary>
        bool OnAreaClick(int button)
        {
            if (Hoi4.UI.Hoi4ConfirmDialog.IsOpen) return true;
            if (button == 1) { CancelArea(); return true; }
            if (button != 0 || areaHoverState <= 0) return true;
            var plan = AreaOf(areaArmyId);
            var states = plan != null ? new SortedSet<int>(plan.Area.States) : new SortedSet<int>();
            if (!states.Remove(areaHoverState)) states.Add(areaHoverState);
            gameState.TryExecuteCommand(new SetAreaDefenseCommand { CountryId = Player, ArmyId = areaArmyId,
                States = StateList(states), Guard = plan != null ? plan.Area.Guard : areaPendingGuard }, out _);
            return true;
        }

        /// <summary>The tool's stripes: the order's states green. The state under the cursor isn't marked (HOI4).</summary>
        void PaintAreaTool()
        {
            var green = (Color32)areaPickedColor;
            var plan = AreaOf(areaArmyId);
            if (plan == null) return;
            foreach (int s in plan.Area.States)
                foreach (ushort p in Hoi4Regions.ProvincesOf(s))
                    if (p < planPixels.Length) planPixels[p] = new Color32(green.r, green.g, green.b, 96);
        }

        /// <summary>No tool on: the selected army's guarded states in its colour, dim.</summary>
        void PaintShownArea()
        {
            var c = Hoi4Armies.ColorOf(shownArea.ArmyId, 1f) * areaDim;
            var dim = new Color32((byte)(Mathf.Clamp01(c.r) * 255f), (byte)(Mathf.Clamp01(c.g) * 255f), (byte)(Mathf.Clamp01(c.b) * 255f), 160);
            foreach (int s in shownArea.Area.States)
                foreach (ushort p in Hoi4Regions.ProvincesOf(s))
                    if (p < planPixels.Length) planPixels[p] = dim;
        }

        /// <summary>Every frame (presentation): which army's order is striped follows the selection.</summary>
        void UpdateShownArea()
        {
            Hoi4BattlePlan show = null;
            if (!ToolOn && ArmySelection(out var army) != null) show = AreaOf(army.Id);
            if (show == shownArea && shownAreaStamp == areaStamp) return;
            shownArea = show;
            shownAreaStamp = areaStamp;
            dirtyTex = true;
            RebuildTexture();
        }

        // --- Simulation --------------------------------------------------------------------------------

        /// <summary>The country can guard the state: it can stand in one of its provinces without a fight.</summary>
        public bool CanDefendState(ushort country, int state)
        {
            foreach (ushort p in Hoi4Regions.ProvincesOf(state))
                if (CanHoldAt(country, p)) return true;
            return false;
        }

        /// <summary>SetAreaDefenseCommand's check: known states, none twice, and those new to the army's order
        /// are ones the country can guard.</summary>
        public bool AreAreaStates(ushort owner, int armyId, List<ushort> states)
        {
            if (states == null) return false;
            var had = AreaOf(armyId)?.Area.States;
            var seen = new HashSet<ushort>();
            foreach (ushort s in states)
            {
                if (!seen.Add(s) || Hoi4Regions.ProvincesOf(s).Count == 0) return false;
                if ((had == null || !had.Contains(s)) && !CanDefendState(owner, s)) return false;
            }
            return true;
        }

        bool IsCoastal(ushort province)
        {
            if (coastal == null)
            {
                coastal = new bool[landNeighbours.Length];
                foreach (ushort p in landProvinces)
                {
                    var n = gameState.Adjacencies.GetNeighbors(p, Unity.Collections.Allocator.Temp);
                    foreach (ushort q in n) if (q != 0 && Hoi4Regions.IsWater(q)) { coastal[p] = true; break; }
                    n.Dispose();
                }
            }
            return province < coastal.Length && coastal[province];
        }

        /// <summary>The province is one of this kind in its state (the state's start data where there is nothing live).</summary>
        bool IsGuarded(int kind, ushort province, int state, ushort owner)
        {
            switch (kind)
            {
                case GuardVictoryPoints: return VictoryPoints(state, province) > 0;
                case GuardPorts: return Hoi4.Supply.Hoi4Ports.Level(province) > 0;
                case GuardCoast: return IsCoastal(province);
                case GuardBorders:
                    // next to land another country holds
                    foreach (ushort q in Neighbours(province))
                    {
                        ushort c = HourController(q);
                        if (c != 0 && c != HourController(province)) return true;
                    }
                    return false;
                case GuardForts:
                    var info = Hoi4StateData.Get(state);
                    return info != null && info.Provincial.TryGetValue(province, out var b)
                        && ((b.TryGetValue("bunker", out int f) && f > 0) || (b.TryGetValue("coastal_bunker", out int cf) && cf > 0));
                case GuardHubs: return Hoi4.Supply.Hoi4Railways.TryGetHub(province, out _);
                default: return false;
            }
        }

        static int VictoryPoints(int state, ushort province)
        {
            var info = Hoi4StateData.Get(state);
            return info != null && info.VictoryPoints.TryGetValue(province, out int v) ? v : 0;
        }

        /// <summary>
        /// The provinces the order guards, most important first: by kind in the window's order, victory points by
        /// value, then by state and province id. Only provinces a division can stand in now (CanHoldAt). Split
        /// into one front per piece of land the divisions can walk within (in order of each piece's first
        /// province), so a division is only sent where it can walk. Ids and integers only.
        /// </summary>
        List<List<ushort>> AreaFronts(Hoi4BattlePlan plan)
        {
            SnapshotHour();
            var walk = WalkLabels(plan.Owner);
            var taken = new HashSet<ushort>();
            var ordered = new List<ushort>();
            var batch = new List<ushort>();
            foreach (int kind in GuardOrder)
            {
                if ((plan.Area.Guard & kind) == 0) continue;
                batch.Clear();
                foreach (int s in plan.Area.States)
                    foreach (ushort p in Hoi4Regions.ProvincesOf(s))
                        if (!taken.Contains(p) && CanHoldAt(plan.Owner, p) && IsGuarded(kind, p, s, plan.Owner)) batch.Add(p);
                if (kind == GuardVictoryPoints)
                    batch.Sort((a, b) =>
                    {
                        int va = VictoryPoints(Hoi4Regions.State(a), a), vb = VictoryPoints(Hoi4Regions.State(b), b);
                        return va != vb ? vb.CompareTo(va) : a.CompareTo(b);
                    });
                else batch.Sort();
                foreach (ushort p in batch) if (taken.Add(p)) ordered.Add(p);
            }
            var fronts = new List<List<ushort>>();
            var frontOf = new Dictionary<int, int>();
            foreach (ushort p in ordered)
            {
                int piece = walk[p];
                if (!frontOf.TryGetValue(piece, out int f)) { frontOf[piece] = f = fronts.Count; fronts.Add(new List<ushort>()); }
                fronts[f].Add(p);
            }
            return fronts;
        }

        /// <summary>
        /// SetAreaDefenseCommand: the army guards these states (all of them: the order is replaced), with this
        /// choice of what to guard. No states: the order is removed. A new order takes every division of the army
        /// and removes the army's lines and offensives, like HOI4.
        /// </summary>
        public void SetAreaDefense(ushort owner, int armyId, List<ushort> states, int guard)
        {
            if (!simReady) return;
            var plan = AreaOf(armyId);
            areaStamp++;
            if (states.Count == 0)
            {
                if (plan == null) return;
                areaPendingGuard = plan.Area.Guard; // the window keeps the choice
                RemovePlan(plan);
                dirtyTex = dirtyMesh = true;
                if (ready) RebuildTexture();
                Changed?.Invoke();
                return;
            }
            if (plan == null)
            {
                foreach (var other in new List<Hoi4BattlePlan>(plans))
                    if (other.ArmyId == armyId) RemovePlan(other);
                foreach (var o in new List<Hoi4Offensive>(offensives))
                    if (o.ArmyId == armyId) RemoveOffensive(o, "the army got an area defence order");
                if (executing.Remove(armyId))
                    Core.Recording.SessionRecorder.Note("plans", "stop", owner, 0, 0, 0, $"army {armyId}: area defence");
                plan = new Hoi4BattlePlan { Id = nextId++, Owner = owner, ArmyId = armyId, Area = new Hoi4AreaDefense() };
                var army = Hoi4Armies.Instance?.Get(armyId);
                if (army != null)
                    foreach (ushort u in army.Units)
                    {
                        manualOrders.Remove(u);
                        unassigned.Remove(u);
                        pinnedOffensive.Remove(u);
                        plan.Units.Add(u);
                        planOf[u] = plan;
                    }
                TraceLine(plan);
                plans.Add(plan);
                AssignOffensives();
            }
            plan.Area.States.Clear();
            foreach (ushort s in states) plan.Area.States.Add(s);
            plan.Area.Guard = guard & GuardAvailable;
            SetFronts(plan, AreaFronts(plan));
            Core.Recording.SessionRecorder.Note("plans", "area_defence", owner, 0, 0, 0,
                $"plan {plan.Id} army {armyId}: states {string.Join(",", plan.Area.States)}, guard {plan.Area.Guard}, " +
                $"{plan.FrontProvinces.Count} provinces in {plan.Fronts.Count} pieces, {plan.Units.Count} divisions");
            Distribute(plan);
            dirtyTex = dirtyMesh = true;
            if (ready) RebuildTexture();
            Changed?.Invoke();
        }

        /// <summary>
        /// Distribute for an area defence order: who guards which province. Per front (a piece of land the
        /// divisions can walk within; a division belongs to the piece it stands in), with m divisions for p
        /// provinces in order of importance: fewer divisions than provinces guard the m most important, one
        /// each; with more, every province gets m / p and the m % p most important one more. No province gets
        /// more than that while another is short: a line's rules (keep the old slot, stay within a week's march,
        /// double up on the nearest) left 13 divisions on one coastal province and 20 others empty
        /// (Recordings/20261127_132337_FRA). A division keeps the province it guards, or stands in, while that
        /// one has room; the others go to the open places nearest first (km, then ids). Integers and ids only.
        /// </summary>
        void SlotArea(Hoi4BattlePlan plan, Dictionary<ushort, ushort> previous, List<ushort> ids)
        {
            var fronts = plan.Fronts;
            var units = gameState.Units;
            var walk = WalkLabels(plan.Owner);
            var frontOfPiece = new Dictionary<int, int>();
            for (int f = 0; f < fronts.Count; f++)
                if (fronts[f].Count > 0 && fronts[f][0] < walk.Length && walk[fronts[f][0]] >= 0) frontOfPiece[walk[fronts[f][0]]] = f;
            var members = new List<ushort>[fronts.Count];
            for (int f = 0; f < fronts.Count; f++) members[f] = new List<ushort>();
            var startOf = new Dictionary<ushort, ushort>();
            foreach (ushort u in ids) // sorted
            {
                // A division already marching counts from where it is going
                ushort heading = HeadingTo(u);
                ushort here = units.GetUnit(u).provinceID;
                startOf[u] = heading != 0 ? heading : here;
                int piece = here < walk.Length ? walk[here] : -1;
                if (piece >= 0 && frontOfPiece.TryGetValue(piece, out int f)) members[f].Add(u);
                // else: no guarded province it can walk to (another landmass, or standing on enemy land): it stays
            }
            int slotted = 0;
            for (int f = 0; f < fronts.Count; f++)
            {
                var front = fronts[f];
                var mine = members[f];
                int m = mine.Count, p = front.Count;
                if (m == 0 || p == 0) continue;
                var room = new int[p]; // places left per province
                var index = new Dictionary<ushort, int>(p);
                for (int i = 0; i < p; i++) { room[i] = m / p + (i < m % p ? 1 : 0); index[front[i]] = i; }
                // Stay put: the province it stands in, else the one it guarded
                var open = new List<ushort>();
                foreach (ushort u in mine)
                {
                    ushort here = units.GetUnit(u).provinceID;
                    int keep = -1;
                    if (!units.MovementQueue.IsUnitMoving(u) && index.TryGetValue(here, out int standing) && room[standing] > 0) keep = standing;
                    else if (previous.TryGetValue(u, out ushort had) && index.TryGetValue(had, out int guarded) && room[guarded] > 0) keep = guarded;
                    if (keep < 0) { open.Add(u); continue; }
                    room[keep]--;
                    plan.Slot[u] = front[keep];
                    slotted++;
                }
                if (open.Count == 0) continue;
                // The rest: every (division, province with room) pair, nearest first
                var pairs = new List<(FP km, ushort unit, int province)>(open.Count * p);
                foreach (ushort u in open)
                    for (int i = 0; i < p; i++)
                    {
                        if (room[i] == 0) continue;
                        if (!Hoi4Movement.TryDistanceKm(startOf[u], front[i], out var km)) km = FP.MaxValue;
                        pairs.Add((km, u, i));
                    }
                pairs.Sort((x, y) => x.km != y.km ? x.km.CompareTo(y.km) : x.unit != y.unit ? x.unit.CompareTo(y.unit) : x.province.CompareTo(y.province));
                foreach (var (_, u, i) in pairs)
                {
                    if (room[i] == 0 || plan.Slot.ContainsKey(u)) continue;
                    room[i]--;
                    plan.Slot[u] = front[i];
                    slotted++;
                }
            }
            if (Core.Recording.SessionRecorder.IsRecording)
            {
                var parts = new List<string>(fronts.Count);
                for (int f = 0; f < fronts.Count; f++) parts.Add($"{fronts[f].Count}p:{members[f].Count}");
                Core.Recording.SessionRecorder.Note("plans", "distribute", plan.Owner, 0, 0, 0,
                    $"plan {plan.Id} (area defence): {ids.Count} divisions, {slotted} slotted; per piece provinces:divisions {string.Join(" ", parts)}");
            }
        }

        /// <summary>A line is drawn for the army: its area defence order goes (HOI4: the army's only order).</summary>
        void ReplaceAreaDefence(int armyId)
        {
            var plan = AreaOf(armyId);
            if (plan == null) return;
            areaStamp++;
            RemovePlan(plan);
            dirtyTex = true;
        }

        /// <summary>Presentation: nothing of an area defence order is drawn as a line.</summary>
        void TraceArea(Hoi4BattlePlan plan)
        {
            plan.Arcs = new List<BorderArc>();
            plan.Chains = new List<List<Vector3>>();
            plan.ChainFront.Clear();
            plan.Labels.Clear();
        }
    }
}
