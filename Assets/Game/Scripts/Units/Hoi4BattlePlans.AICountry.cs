using System;
using System.Collections.Generic;
using Hoi4.Map;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the war AI's country layer, and the strategic layer's front lines (Docs/ai-layers.md; prototype
    /// Tools/prototypes/ai_armies/theatres.py). Daily per AI country at its own hour (country id mod 24):
    /// - Theatres: its front provinces (passable land next to passable land of a country it is at war with),
    ///   joined through land adjacency or through facing the same enemy province, across enemies, never across
    ///   landmasses (Landmasses: each shares its own divisions, with its own main effort).
    /// - Per theatre: what it takes, enough to fill the line (one per front province) and to match the enemy
    ///   divisions facing it (within FacingHops) times HoldMin..HoldMax by the danger to the homeland (HomeCosts:
    ///   the enemy's cheapest route from there to our core victory points), whichever is more (the main effort
    ///   AttackRatio times them), and a score (times 1 + danger) from the cheapest route on to an enemy's victory points (RouteCosts:
    ///   (0.25 + surrender progress) x 10 / (1 + cost); no route: 0, the theatre only holds). The main effort is
    ///   the best score, kept CommitDays unless another scores ScoreToSwitch times better (it follows its armies).
    /// - Budget: each theatre its need, the spare shared in proportion to the needs; short of divisions, each the
    ///   same fraction of its need. Capped by supply (SupplyCap: what the theatre's front provinces can feed, as
    ///   HOI4's AI_FRONT_DIVISIONS_PER_SUPPLY_POINT, at least one per province); what a capped theatre can't take
    ///   goes to the others on the landmass with room, the rest is reserve.
    /// - Reserve: daily, reserve divisions starving where they stand walk to the nearest of our provinces whose hub
    ///   supply has room for them, off the front (RestReserves).
    /// - Posture: the main effort attacks while its armies have AttackRatio times the enemy facing it; the rest hold.
    /// - Armies: ceil(budget / ArmySize) per theatre, each holding a contiguous section of its front. Sections
    ///   carry over from the day before by nearness (each front province takes the army of the nearest earlier
    ///   front province within CarryHops), then the smallest army merges away or the largest splits until the
    ///   count fits; each army keeps one connected stretch. Armies merged away or left without a theatre go to a
    ///   pool a theatre short of armies takes from (the nearest, within TransferKm: whole armies move between
    ///   theatres); the rest are disbanded and their divisions go to reserve.
    /// - Lines: one plan per army and enemy its section faces, the player's kind with a section (Section, and
    ///   Outside the rest of that border, so the hourly refresh follows the border without creeping into the
    ///   neighbouring army's stretch).
    /// - Divisions: an army over its budget lets the ones furthest from its section go to reserve (they stop
    ///   where they are); an army under it takes the nearest reserve divisions it can walk to (same walk label,
    ///   MaxTransferHops). New divisions are reserve until an army takes them.
    /// Decisions are noted (system "ai_country").
    /// </summary>
    public sealed partial class Hoi4BattlePlans
    {
        static readonly Unity.Profiling.ProfilerMarker LandmassesMarker = new Unity.Profiling.ProfilerMarker("Hoi4.ai_country.landmasses");
        static readonly Unity.Profiling.ProfilerMarker HomeCostsMarker = new Unity.Profiling.ProfilerMarker("Hoi4.ai_country.home_costs");
        static readonly Unity.Profiling.ProfilerMarker RouteCostsMarker = new Unity.Profiling.ProfilerMarker("Hoi4.ai_country.route_costs");
        static readonly Unity.Profiling.ProfilerMarker FacingMarker = new Unity.Profiling.ProfilerMarker("Hoi4.ai_country.facing");
        static readonly Unity.Profiling.ProfilerMarker ArmyLinesMarker = new Unity.Profiling.ProfilerMarker("Hoi4.ai_country.army_lines");
        static readonly Unity.Profiling.ProfilerMarker MembershipMarker = new Unity.Profiling.ProfilerMarker("Hoi4.ai_country.membership");

        const int PlanDays = 7;         // the country layer plans weekly (and early when the picture changes, RunCountry)
        const int CommitDays = 60;
        const int FacingHops = 2;
        const int ArmySize = 24;        // HOI4's army limit
        const int FollowHops = 8;       // between plans, a front province goes to the army of a section this close (else: plan)
        const int CarryHops = 4;        // a section carries over to front provinces this close to it
        const int MergeHops = 4;        // stretches of front this close (land hops) are one theatre ...
        const int MinTheatre = 4;       // ... and one shorter than this provinces joins one up to ...
        const int SliverHops = 8;       // ... this far: a theatre re-cut every day re-cut its armies
        const int MaxTransferHops = 20; // a division joins an army only this close to its section (walkable land)
        static readonly FP TransferKm = FP.FromInt(1500); // a whole army takes over another theatre only this close
        static readonly FP ObjectiveBase = FP.FromInt(8);   // a route to a victory point starts at 8 / (1 + VP / 5): bigger ones pull harder
        static readonly FP ScoreScale = FP.FromInt(10);
        static readonly FP HomeScale = FP.FromInt(10);     // danger 0.5 at a route home costing 10 (about 10 plains provinces)
        static readonly FP HoldMin = FP.FromFraction(1, 2); // a front's need: the enemy facing it times HoldMin (far from home) ...
        static readonly FP HoldMax = FP.FromFraction(3, 2); // ... up to HoldMax (at our core victory points)
        static readonly FP AttackRatio = FP.FromFraction(3, 2); // the main effort wants this many per enemy division facing it, and attacks only with them
        static readonly FP ScoreToSwitch = FP.FromFraction(3, 2);
        static readonly FP ProgressBase = FP.FromFraction(1, 4);
        // supply: a theatre takes at most what its front provinces feed (HOI4 AI_FRONT_DIVISIONS_PER_SUPPLY_POINT 1.0,
        // AI_FRONT_MINIMUM_UNITS_PER_PROVINCE_FOR_SUPPLY_CALCULATIONS 1), counting each division's own need
        static readonly FP DivisionsPerSupplyPoint = FP.One;
        const int MinPerProvinceForSupply = 1;
        static readonly FP RestBelow = FP.FromFraction(6, 10); // a reserve division getting less than this of its need moves
        const int RestHops = 30;                              // ... to a fed province this close (own land)
        const int RestPerDay = 24;                            // ... at most this many orders a country a day

        sealed class Theatre
        {
            public List<ushort> Provinces;                    // sorted
            public List<ushort> Order;                        // along the front, from one end
            public Dictionary<ushort, List<ushort>> Links;    // neighbours along the front (sorted)
            public SortedSet<ushort> Enemies = new SortedSet<ushort>();
            public int Facing, Need, Budget, Label, SupplyCap;
            public FP Supply;                                  // hub supply of its front provinces plus their states' pools
            public FP HomeCost = FP.MaxValue, Danger;          // the enemy's cheapest route from here to our core VPs; HomeScale / (HomeScale + it)
            public FP Cost = FP.MaxValue, Score;
            public ushort BestEnemy;
            public bool Overseas, Main, Attacking;            // Overseas: not on the landmass with the capital
            public Dictionary<ushort, int> ArmyOf;            // province -> army id
            public List<int> Armies = new List<int>();        // in order along the front
        }

        sealed class AIState
        {
            public ushort MainEnemy;
            public readonly HashSet<int> MainArmies = new HashSet<int>();                // the armies of the main efforts
            public readonly HashSet<int> AttackArmies = new HashSet<int>();              // armies whose theatre attacks (posture)
            public readonly Dictionary<int, int> MainSince = new Dictionary<int, int>(); // army -> day its theatre became the main effort
            public Dictionary<ushort, int> Section = new Dictionary<ushort, int>(); // yesterday's front province -> army
            public readonly Dictionary<int, ushort> Anchor = new Dictionary<int, ushort>(); // army -> middle province of its section
            public readonly Dictionary<int, int> Budget = new Dictionary<int, int>();
            // The last plan (PlanCountry), which the daily follow-up (FollowCountry) works from
            public int LastPlan;
            public List<Theatre> Theatres = new List<Theatre>();
            public readonly Dictionary<int, int> ArmyTheatre = new Dictionary<int, int>(); // army -> index in Theatres
            public SortedSet<ushort> Enemies = new SortedSet<ushort>();                    // the enemies its front faced
            public int FrontSize;
        }

        readonly Dictionary<ushort, AIState> aiStates = new Dictionary<ushort, AIState>();
        // Per enemy, today: each province of its land -> the cost of the cheapest route from there to one of the
        // victory points of its main body (RouteCosts)
        readonly Dictionary<ushort, Dictionary<ushort, FP>> routes = new Dictionary<ushort, Dictionary<ushort, FP>>();
        int routesDay = -1;

        int Today => (int)(gameState.Time.CurrentTick / 24);

#if UNITY_EDITOR
        // Debug switch remembered per machine (EditorPrefs): menu HOI4 > Show AI Plans draws every AI army's front
        // lines and offensives in its colour, and the AI's army colours on its counters
        const string ShowAIPref = "Hoi4.Plans.ShowAI";
        const string ShowAIMenu = "HOI4/Show AI Plans";

        [UnityEditor.MenuItem(ShowAIMenu)]
        static void ToggleShowAI()
        {
            bool on = !UnityEditor.EditorPrefs.GetBool(ShowAIPref, false);
            UnityEditor.EditorPrefs.SetBool(ShowAIPref, on);
            SetShowAIPlans(on);
        }

        [UnityEditor.MenuItem(ShowAIMenu, true)]
        static bool ToggleShowAIValidate()
        {
            UnityEditor.Menu.SetChecked(ShowAIMenu, UnityEditor.EditorPrefs.GetBool(ShowAIPref, false));
            return true;
        }

        static bool ShowAIPreference => UnityEditor.EditorPrefs.GetBool(ShowAIPref, false);
#else
        static bool ShowAIPreference => false;
#endif

        /// <summary>The cost of the cheapest route from an enemy province on to that enemy's victory points (its
        /// main body's), today; false when none leads there (a pocket, a colony, or not its land).</summary>
        public bool TryRouteCost(ushort enemy, ushort province, out FP cost)
        {
            cost = FP.MaxValue;
            return routes.TryGetValue(enemy, out var r) && r.TryGetValue(province, out cost);
        }

        /// <summary>The enemy an AI country's main effort is against (0: none).</summary>
        public ushort MainEffort(ushort country) => aiStates.TryGetValue(country, out var s) ? s.MainEnemy : (ushort)0;

        /// <summary>The province is next to a section of an army whose theatre attacks (the main effort, with
        /// enough divisions for it): Hoi4EnemyAI attacks there; elsewhere the country holds.</summary>
        public bool IsAttackFront(ushort country, ushort province)
        {
            if (!aiStates.TryGetValue(country, out var s) || s.AttackArmies.Count == 0) return false;
            foreach (ushort n in Neighbours(province))
                if (s.Section.TryGetValue(n, out int a) && s.AttackArmies.Contains(a)) return true;
            return false;
        }

        /// <summary>The divisions the AI gives an army (-1: not an AI army).</summary>
        public int ArmyBudget(ushort owner, int armyId) =>
            aiStates.TryGetValue(owner, out var s) && s.Budget.TryGetValue(armyId, out int b) ? b : -1;

        /// <summary>Hourly: the AI countries whose hour it is (id mod 24) run their country layer, in id order.</summary>
        void UpdateAI(int hour)
        {
            if (routesDay < 0 || Today - routesDay >= PlanDays) { routes.Clear(); routesDay = Today; } // as long as a plan
            var armies = Hoi4Armies.Instance;
            var diplomacy = gameState.Diplomacy;
            if (armies == null || diplomacy == null) return;
            ushort player = Player;
            var countries = new SortedSet<ushort>();
            foreach (ushort id in gameState.Units.GetAllUnits())
            {
                ushort c = gameState.Units.GetUnit(id).countryID;
                if (c % 24 == hour && c != player && c != 0) countries.Add(c);
            }
            foreach (var a in armies.All) if (a.Owner % 24 == hour && a.Owner != player && a.Owner != 0) countries.Add(a.Owner);
            if (countries.Count == 0) return;
            BeginFrontsMemo();
            foreach (ushort c in countries)
            {
                if (diplomacy.IsAtWar(c)) RunCountry(c);
                else if (aiStates.ContainsKey(c)) StandDown(c);
            }
            EndFrontsMemo();
        }

        /// <summary>At peace: the country's armies are disbanded (their lines go with them), its divisions reserve.</summary>
        void StandDown(ushort country)
        {
            var armies = Hoi4Armies.Instance;
            foreach (var a in armies.Of(country)) DisbandAIArmy(a);
            aiStates.Remove(country);
            Core.Recording.SessionRecorder.Note("ai_country", "stand_down", country, 0, 0, 0, "at peace: armies disbanded");
        }

        void DisbandAIArmy(Hoi4Army army)
        {
            foreach (var plan in new List<Hoi4BattlePlan>(plans))
                if (plan.ArmyId == army.Id) { RemovePlan(plan); if (IsDrawn(plan)) simMeshDirty = true; }
            foreach (var o in new List<Hoi4Offensive>(offensives))
                if (o.ArmyId == army.Id) RemoveOffensive(o, "army disbanded");
            executing.Remove(army.Id);
            Hoi4Armies.Instance.Disband(army);
        }

        /// <summary>
        /// Daily per country at war: the country layer plans (PlanCountry: theatres, scores, main effort, budgets,
        /// armies) weekly, or early when the picture changed: no plan yet, other enemies, the front a quarter longer
        /// or shorter, an army gone, or more than a tenth of the front (at least 3 provinces) beyond the reach of
        /// every army's section. Otherwise the strategic layer follows the front (FollowCountry). Planning daily cost
        /// ~16 ms a frame in a world war.
        /// </summary>
        void RunCountry(ushort country)
        {
            SnapshotHour();
            var diplomacy = gameState.Diplomacy;
            var armies = Hoi4Armies.Instance;
            var front = new List<ushort>();
            var enemies = new SortedSet<ushort>();
            foreach (ushort p in landProvinces)
            {
                if (hourCtrl[p] != country || Hoi4Regions.IsImpassable(p)) continue;
                bool isFront = false;
                foreach (ushort n in Neighbours(p))
                {
                    ushort e = hourCtrl[n];
                    if (Hoi4Regions.IsImpassable(n) || e == 0 || e == country || !diplomacy.IsAtWar(country, e)) continue;
                    isFront = true;
                    enemies.Add(e);
                }
                if (isFront) front.Add(p);
            }
            if (front.Count == 0) { StandDown(country); return; }
            string reason = null;
            if (!aiStates.TryGetValue(country, out var st)) reason = "first plan";
            else if (Today - st.LastPlan >= PlanDays) reason = "weekly";
            else if (!enemies.SetEquals(st.Enemies)) reason = "other enemies";
            else if (Math.Abs(front.Count - st.FrontSize) * 4 > st.FrontSize) reason = $"front {st.FrontSize} -> {front.Count} provinces";
            else foreach (int a in st.Budget.Keys) if (armies.Get(a) == null) { reason = "an army is gone"; break; }
            Dictionary<ushort, int> near = null;
            if (reason == null)
            {
                near = CarrySections(st, FollowHops);
                int uncovered = 0;
                foreach (ushort p in front) if (!near.TryGetValue(p, out int a) || !st.ArmyTheatre.ContainsKey(a)) uncovered++;
                if (uncovered > Math.Max(2, front.Count / 10)) reason = $"{uncovered} front provinces beyond every army's section";
            }
            if (reason != null) PlanCountry(country, reason);
            else FollowCountry(country, st, front, near);
            RestReserves(country);
        }

        /// <summary>
        /// Reserve divisions (no army) getting less than RestBelow of their supply need, standing still and not
        /// fighting, walk to the nearest of our provinces (by land hops through our own land, within RestHops) that
        /// isn't on the front and whose hub supply has room for one more: hub supply minus the need of our divisions
        /// there or marching there. At most RestPerDay orders a day. Deterministic: units and rings in id order.
        /// </summary>
        void RestReserves(ushort country)
        {
            var armies = Hoi4Armies.Instance;
            var divs = Hoi4Divisions.Instance;
            if (armies == null || divs == null) return;
            var units = gameState.Units;
            var queue = units.MovementQueue;
            var diplomacy = gameState.Diplomacy;
            var own = units.GetCountryUnits(country);
            own.Sort();
            // load: the need of our divisions in each province or marching to it
            var load = new Dictionary<ushort, FP>();
            var hungry = new List<ushort>();
            foreach (ushort u in own)
            {
                if (!units.HasUnit(u)) continue;
                var dv = divs.Get(u);
                if (dv == null) continue;
                ushort at = queue.IsUnitMoving(u) ? HeadingTo(u) : units.GetUnit(u).provinceID;
                load[at] = (load.TryGetValue(at, out FP l) ? l : FP.Zero) + dv.SupplyUse;
                if (armies.ArmyOf(u) != null || queue.IsUnitMoving(u) || Hoi4Combat.IsFighting(u) || Hoi4Combat.IsRetreating(u)
                    || borrowed.Contains(u) || Hoi4Regions.IsWater(units.GetUnit(u).provinceID)) continue;
                if (Hoi4.Supply.Hoi4Supply.TargetRatio(u) < RestBelow) hungry.Add(u);
            }
            if (hungry.Count == 0) return;
            bool Front(ushort p)
            {
                foreach (ushort n in Neighbours(p))
                {
                    ushort c = hourCtrl[n];
                    if (c != 0 && c != country && !Hoi4Regions.IsImpassable(n) && diplomacy.IsAtWar(country, c)) return true;
                }
                return false;
            }
            int orders = 0;
            var moved = new List<string>();
            foreach (ushort u in hungry)
            {
                if (orders >= RestPerDay) break;
                var unit = units.GetUnit(u);
                FP need = divs.Get(u).SupplyUse;
                ushort start = unit.provinceID, dest = 0;
                var seen = new HashSet<ushort> { start };
                var ring = new List<ushort> { start };
                for (int hop = 0; hop < RestHops && ring.Count > 0 && dest == 0; hop++)
                {
                    var next = new List<ushort>();
                    foreach (ushort p in ring)
                        foreach (ushort n in Neighbours(p))
                            if (hourCtrl[n] == country && !Hoi4Regions.IsImpassable(n) && seen.Add(n)) next.Add(n);
                    next.Sort();
                    foreach (ushort p in next)
                    {
                        FP room = Hoi4.Supply.Hoi4Supply.HubSupply(p) - (load.TryGetValue(p, out FP l) ? l : FP.Zero);
                        if (room >= need && !Front(p)) { dest = p; break; }
                    }
                    ring = next;
                }
                if (dest == 0 || !OrderMove(u, unit, dest, log: false)) continue;
                load[start] = (load.TryGetValue(start, out FP ls) ? ls : FP.Zero) - need;
                load[dest] = (load.TryGetValue(dest, out FP ld) ? ld : FP.Zero) + need;
                orders++;
                moved.Add($"{u} {start}->{dest}");
            }
            if (moved.Count > 0)
                Core.Recording.SessionRecorder.Note("ai_country", "rest", country, 0, 0, 0,
                    $"{moved.Count} reserve divisions short of supply to fed provinces: {string.Join(", ", moved)}");
        }

        /// <summary>Each province within reach land hops of the last sections -> the army of the nearest
        /// (ties: the lower army id, then the lower province id).</summary>
        Dictionary<ushort, int> CarrySections(AIState st, int reach)
        {
            var near = new Dictionary<ushort, int>();
            if (st.Section.Count == 0) return near;
            var q = new List<ushort>(st.Section.Keys);
            q.Sort((a, b) => st.Section[a] != st.Section[b] ? st.Section[a].CompareTo(st.Section[b]) : a.CompareTo(b));
            var dist = new Dictionary<ushort, int>();
            foreach (ushort p in q) { dist[p] = 0; near[p] = st.Section[p]; }
            for (int i = 0; i < q.Count; i++)
            {
                ushort x = q[i];
                if (dist[x] >= reach) continue;
                foreach (ushort y in Neighbours(x))
                    if (!dist.ContainsKey(y) && !Hoi4Regions.IsImpassable(y)) { dist[y] = dist[x] + 1; near[y] = near[x]; q.Add(y); }
            }
            return near;
        }

        /// <summary>
        /// The strategic layer between plans: each front province takes the army of the nearest province of the
        /// last sections (CarrySections), into that army's theatre; then the army lines, divisions and posture
        /// follow, with the plan's theatres and budgets.
        /// </summary>
        void FollowCountry(ushort country, AIState st, List<ushort> front, Dictionary<ushort, int> near)
        {
            var diplomacy = gameState.Diplomacy;
            // the attack AI ranks its targets by the main enemy's route costs (TryRouteCost): cached for a plan's
            // lifetime, and computed here when the cache was cleared since this country last planned
            if (st.MainEnemy != 0) RouteCosts(st.MainEnemy);
            foreach (var th in st.Theatres) { th.ArmyOf = new Dictionary<ushort, int>(); th.Enemies.Clear(); }
            var section = new Dictionary<ushort, int>();
            foreach (ushort p in front)
            {
                if (!near.TryGetValue(p, out int a) || !st.ArmyTheatre.TryGetValue(a, out int ti)) continue;
                section[p] = a;
                var th = st.Theatres[ti];
                th.ArmyOf[p] = a;
                foreach (ushort n in Neighbours(p))
                {
                    ushort e = hourCtrl[n];
                    if (!Hoi4Regions.IsImpassable(n) && e != 0 && e != country && diplomacy.IsAtWar(country, e)) th.Enemies.Add(e);
                }
            }
            // Sections as before: nothing for the army lines to do (the hourly refresh follows the border within
            // each section), which is most days on a front that holds
            bool same = section.Count == st.Section.Count;
            if (same) foreach (var kv in section) if (!st.Section.TryGetValue(kv.Key, out int was) || was != kv.Value) { same = false; break; }
            st.Section = section;
            // an army whose anchor left its section: the middle of its new one (by id)
            var byArmy = new SortedDictionary<int, List<ushort>>();
            foreach (var kv in section)
            {
                if (!byArmy.TryGetValue(kv.Value, out var l)) byArmy[kv.Value] = l = new List<ushort>();
                l.Add(kv.Key);
            }
            foreach (var kv in byArmy)
                if (!st.Anchor.TryGetValue(kv.Key, out ushort anchor) || !section.TryGetValue(anchor, out int at) || at != kv.Key)
                {
                    kv.Value.Sort();
                    st.Anchor[kv.Key] = kv.Value[kv.Value.Count / 2];
                }
            int[] labels;
            using (LandmassesMarker.Auto()) labels = Landmasses(country);
            var own = gameState.Units.GetCountryUnits(country);
            own.Sort();
            if (!same) using (ArmyLinesMarker.Auto()) UpdateArmyLines(country, st.Theatres);
            using (MembershipMarker.Auto()) UpdateMembership(country, st, st.Theatres, own, labels);
            UpdatePosture(st, st.Theatres);
        }

        /// <summary>Posture: the main effort attacks while its armies have AttackRatio times the enemy divisions
        /// facing it; every other theatre (and a main effort short of that) holds (Hoi4EnemyAI: only flattening
        /// bulges at crushing odds).</summary>
        void UpdatePosture(AIState st, List<Theatre> theatres)
        {
            var armies = Hoi4Armies.Instance;
            st.AttackArmies.Clear();
            foreach (var th in theatres)
            {
                int have = 0;
                foreach (int a in th.Armies) have += armies.Get(a)?.Units.Count ?? 0;
                th.Attacking = th.Main && FP.FromInt(have) >= FP.FromInt(th.Facing) * AttackRatio;
                if (th.Attacking) foreach (int a in th.Armies) st.AttackArmies.Add(a);
            }
        }

        void PlanCountry(ushort country, string reason)
        {
            SnapshotHour();
            var armies = Hoi4Armies.Instance;
            var diplomacy = gameState.Diplomacy;
            if (!aiStates.TryGetValue(country, out var st)) aiStates[country] = st = new AIState();
            int today = Today;
            bool IsEnemy(ushort c) => c != 0 && c != country && diplomacy.IsAtWar(country, c);
            bool Land(ushort p) => !Hoi4Regions.IsImpassable(p);
            ushort C(ushort p) => hourCtrl[p];

            // --- Theatres -------------------------------------------------------------------------------
            var front = new List<ushort>();
            foreach (ushort p in landProvinces)
            {
                if (C(p) != country || !Land(p)) continue;
                foreach (ushort n in Neighbours(p))
                    if (Land(n) && IsEnemy(C(n))) { front.Add(p); break; }
            }
            if (front.Count == 0) { StandDown(country); return; }
            var frontSet = new HashSet<ushort>(front);
            var root = new Dictionary<ushort, ushort>();
            foreach (ushort p in front) root[p] = p;
            ushort Find(ushort x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
            void Union(ushort a, ushort b) { a = Find(a); b = Find(b); if (a != b) root[Math.Max(a, b)] = Math.Min(a, b); }
            var faced = new SortedDictionary<ushort, List<ushort>>(); // enemy province -> our front provinces facing it
            foreach (ushort p in front)
                foreach (ushort n in Neighbours(p))
                {
                    if (frontSet.Contains(n)) Union(p, n);
                    else if (Land(n) && IsEnemy(C(n)))
                    {
                        if (!faced.TryGetValue(n, out var l)) faced[n] = l = new List<ushort>();
                        l.Add(p);
                    }
                }
            // Facing the same enemy province joins fronts only on the same landmass: a cut-off piece facing the
            // main front's enemy took the main front onto its landmass (its label came from whichever province
            // was first) and starved it of divisions
            int[] labels;
            Dictionary<ushort, FP> homeCost;
            using (LandmassesMarker.Auto()) labels = Landmasses(country);
            using (HomeCostsMarker.Auto()) homeCost = HomeCosts(country);
            foreach (var l in faced.Values)
                for (int i = 1; i < l.Count; i++) if (labels[l[i]] == labels[l[0]]) Union(l[0], l[i]);
            var groups = Groups(front, Find);
            // Stretches of front within MergeHops land hops of each other are one theatre (a front cut by a lake,
            // a neutral or a bulge isn't two), and a sliver (under MinTheatre provinces: a bit of enemy land cut
            // off) joins one up to SliverHops away instead of getting an army of its own. Same landmass only. The
            // two closest provinces are linked so the walk along the front goes through
            var bridges = new List<(ushort a, ushort b)>();
            foreach (var g in new List<List<ushort>>(groups.Values))
            {
                int reach = g.Count < MinTheatre ? SliverHops : MergeHops;
                var origin = new Dictionary<ushort, ushort>();
                var hops = new Dictionary<ushort, int>();
                var q = new List<ushort>(g);
                foreach (ushort s in g) { origin[s] = s; hops[s] = 0; }
                ushort mine0 = Find(g[0]), found = 0, from = 0;
                for (int i = 0; i < q.Count && found == 0; i++)
                {
                    ushort x = q[i];
                    if (hops[x] >= reach) continue;
                    foreach (ushort y in Neighbours(x))
                    {
                        if (hops.ContainsKey(y) || !Land(y)) continue;
                        if (frontSet.Contains(y) && Find(y) != mine0 && labels[y] == labels[g[0]]) { found = y; from = origin[x]; break; }
                        hops[y] = hops[x] + 1; origin[y] = origin[x]; q.Add(y);
                    }
                }
                if (found == 0) continue;
                Union(mine0, found);
                bridges.Add((from, found));
            }
            if (bridges.Count > 0) groups = Groups(front, Find);

            ushort capital = Hoi4.Map.Hoi4Regions.Capital(Tag(country));
            int home = capital != 0 && capital < labels.Length ? labels[capital] : -1;
            if (home < 0)
            {
                var count = new Dictionary<int, int>();
                foreach (ushort p in landProvinces) if (C(p) == country && labels[p] >= 0) count[labels[p]] = count.TryGetValue(labels[p], out int n) ? n + 1 : 1;
                int best = -1;
                foreach (var kv in count) if (best < 0 || kv.Value > count[best] || (kv.Value == count[best] && kv.Key < best)) best = kv.Key;
                home = best;
            }
            var own = gameState.Units.GetCountryUnits(country);
            own.Sort();
            var divisionsByLabel = new Dictionary<int, int>();
            foreach (ushort u in own)
            {
                int l = labels[gameState.Units.GetUnit(u).provinceID];
                divisionsByLabel[l] = divisionsByLabel.TryGetValue(l, out int n) ? n + 1 : 1;
            }

            FP needSum = FP.Zero;
            int needCount = 0;
            var divs = Hoi4Divisions.Instance;
            foreach (ushort u in own)
            {
                var dv = divs?.Get(u);
                if (dv == null) continue;
                needSum += FP.Max(FP.FromFraction(5, 100), dv.SupplyUse);
                needCount++;
            }
            FP avgNeed = needCount > 0 ? needSum / needCount : FP.FromFraction(7, 10);

            var war = Hoi4.War.Hoi4War.Instance;
            var theatres = new List<Theatre>();
            foreach (var g in groups.Values)
            {
                g.Sort();
                var t = new Theatre { Provinces = g, Links = new Dictionary<ushort, List<ushort>>() };
                var gs = new HashSet<ushort>(g);
                foreach (ushort p in g) t.Links[p] = new List<ushort>();
                foreach (ushort p in g) foreach (ushort n in Neighbours(p)) if (gs.Contains(n)) t.Links[p].Add(n);
                foreach (var l in faced.Values)
                {
                    var here = l.FindAll(gs.Contains);
                    foreach (ushort a in here) foreach (ushort b in here) if (a != b && !t.Links[a].Contains(b)) t.Links[a].Add(b);
                }
                foreach (var (a, b) in bridges)
                    if (gs.Contains(a) && gs.Contains(b) && !t.Links[a].Contains(b)) { t.Links[a].Add(b); t.Links[b].Add(a); }
                foreach (var l in t.Links.Values) l.Sort();
                var d0 = WalkAlong(t, g[0]);
                ushort end = g[0];
                foreach (var kv in d0) if (kv.Value > d0[end] || (kv.Value == d0[end] && kv.Key < end)) end = kv.Key;
                var d = WalkAlong(t, end);
                t.Order = new List<ushort>(g);
                t.Order.Sort((a, b) => { int da = d.TryGetValue(a, out int x) ? x : int.MaxValue, db = d.TryGetValue(b, out int y) ? y : int.MaxValue; return da != db ? da.CompareTo(db) : a.CompareTo(b); });
                foreach (ushort p in g) foreach (ushort n in Neighbours(p)) if (Land(n) && IsEnemy(C(n))) t.Enemies.Add(C(n));
                t.Label = labels[g[0]];
                t.Overseas = t.Label != home;
                using (FacingMarker.Auto()) t.Facing = FacingDivisions(g, t.Enemies);
                // How many it takes: enough to fill the line (one per front province) and to match the enemy
                // divisions facing it, whichever is more (the main effort: AttackRatio times them, below)
                // Weighted by the danger to the homeland: an enemy here with a cheap route to our core victory
                // points (HomeCosts) is matched up to HoldMax times, one with a long walk through non-core land
                // only HoldMin times (Finland -> Sweden -> Denmark -> Germany costs little to lose)
                foreach (ushort p in g) if (homeCost.TryGetValue(p, out FP hc) && hc < t.HomeCost) t.HomeCost = hc;
                t.Danger = t.HomeCost == FP.MaxValue ? FP.Zero : HomeScale / (HomeScale + t.HomeCost);
                t.Need = Math.Max(1, Math.Max(g.Count, FP.Ceiling(FP.FromInt(t.Facing) * (HoldMin + (HoldMax - HoldMin) * t.Danger)).ToInt()));
                foreach (ushort p in g)
                    foreach (ushort q in Neighbours(p))
                    {
                        ushort e = C(q);
                        if (!Land(q) || !IsEnemy(e) || !RouteCosts(e).TryGetValue(q, out FP c)) continue;
                        c += FP.FromInt(Hoi4.Map.Hoi4CombatTerrain.River(gameState, p, q));
                        if (c < t.Cost || (c == t.Cost && e < t.BestEnemy)) { t.Cost = c; t.BestEnemy = e; }
                    }
                if (t.BestEnemy == 0) t.BestEnemy = t.Enemies.Min;
                FP progress = war != null ? war.SurrenderProgress(t.BestEnemy) : FP.Zero;
                // a front near home is the better place to attack too: it pushes the enemy away from our cores
                t.Score = t.Cost == FP.MaxValue ? FP.Zero : (ProgressBase + progress) * ScoreScale / (FP.One + t.Cost) * (FP.One + t.Danger);
                // Supply: the hub supply of its front provinces and the pools of their states (each once), fed to
                // divisions of the country's average need (Hoi4Supply); never under one per front province
                var poolStates = new HashSet<int>();
                t.Supply = FP.Zero;
                foreach (ushort p in g)
                {
                    t.Supply += Hoi4.Supply.Hoi4Supply.HubSupply(p);
                    if (poolStates.Add(Hoi4Regions.State(p))) t.Supply += Hoi4.Supply.Hoi4Supply.StateSupply(gameState, p);
                }
                t.SupplyCap = Math.Max(g.Count * MinPerProvinceForSupply, (t.Supply * DivisionsPerSupplyPoint / avgNeed).ToInt());
                theatres.Add(t);
            }

            // --- Carry yesterday's sections by nearness; whose armies are where --------------------------
            var near = CarrySections(st, CarryHops);
            var mine = new Dictionary<int, int>(); // army -> theatre holding most of its carried provinces
            {
                var votes = new Dictionary<(int army, int theatre), int>();
                for (int i = 0; i < theatres.Count; i++)
                    foreach (ushort p in theatres[i].Provinces)
                        if (near.TryGetValue(p, out int a)) votes[(a, i)] = votes.TryGetValue((a, i), out int n) ? n + 1 : 1;
                var bestVotes = new Dictionary<int, int>();
                foreach (var kv in votes)
                {
                    var (a, i) = kv.Key;
                    if (!mine.ContainsKey(a) || kv.Value > bestVotes[a] || (kv.Value == bestVotes[a] && i < mine[a])) { mine[a] = i; bestVotes[a] = kv.Value; }
                }
            }
            // An army that no longer exists (disbanded elsewhere, or a player's command) carries nothing
            var alive = new HashSet<int>();
            foreach (var a in armies.Of(country)) alive.Add(a.Id);
            foreach (int a in new List<int>(mine.Keys)) if (!alive.Contains(a)) mine.Remove(a);

            // --- Main effort and budgets, per landmass ----------------------------------------------------
            // Divisions can't cross the sea (no naval transfer), so each walkable landmass (walk label) shares
            // its own divisions among its own theatres, with its own main effort: land held across the sea is
            // budgeted from the divisions there, and an encircled pocket is a small landmass of its own
            var byLabel = new SortedDictionary<int, List<int>>();
            for (int i = 0; i < theatres.Count; i++)
            {
                if (!byLabel.TryGetValue(theatres[i].Label, out var l)) byLabel[theatres[i].Label] = l = new List<int>();
                l.Add(i);
            }
            int reserve = 0;
            var mainSince = new Dictionary<int, int>(); // main theatre -> since
            ushort homeMain = 0, biggestMain = 0;
            int biggest = -1;
            foreach (var kv in byLabel)
            {
                var open = kv.Value;
                int total = divisionsByLabel.TryGetValue(kv.Key, out int on) ? on : 0;
                int best = open[0];
                foreach (int i in open) if (theatres[i].Score > theatres[best].Score) best = i;
                // the old main effort here: the theatre holding most of the main armies
                int current = -1, currentVotes = 0, since = today;
                foreach (int i in open)
                {
                    int v = 0, s = int.MaxValue;
                    foreach (int a in st.MainArmies)
                        if (mine.TryGetValue(a, out int ti) && ti == i) { v++; if (st.MainSince.TryGetValue(a, out int ms)) s = Math.Min(s, ms); }
                    if (v > currentVotes) { currentVotes = v; current = i; since = s == int.MaxValue ? today : s; }
                }
                int main = best;
                if (current >= 0 && current != best
                    && (today - since < CommitDays || theatres[best].Score < theatres[current].Score * ScoreToSwitch)) main = current;
                if (main != current)
                {
                    var parts = new List<string>();
                    foreach (int i in open)
                        parts.Add($"{TheatreName(theatres[i])} {theatres[i].Provinces.Count}p vs {theatres[i].Facing} div, route " +
                                  (theatres[i].Cost == FP.MaxValue ? "none" : theatres[i].Cost.ToFloat().ToString("0.0")) + $", score {theatres[i].Score.ToFloat():0.00}");
                    Core.Recording.SessionRecorder.Note("ai_country", "main_effort", country, 0, theatres[main].Order[theatres[main].Order.Count / 2], theatres[main].BestEnemy,
                        $"main effort {TheatreName(theatres[main])}{(theatres[main].Overseas ? " (overseas)" : "")} ({(current < 0 ? "first choice" : $"better opening after {today - since} days")}), {total} divisions on its landmass: {string.Join("; ", parts)}");
                    since = today;
                }
                theatres[main].Main = true;
                mainSince[main] = since;
                if (!theatres[main].Overseas) homeMain = theatres[main].BestEnemy;
                if (total > biggest) { biggest = total; biggestMain = theatres[main].BestEnemy; }

                // Targets: each theatre its need, the main effort enough to attack (AttackRatio times the enemy
                // facing it). Enough divisions: each its target and the spare shared in proportion to the targets
                // (no reserve, no cap). Short: each the same fraction of its target
                theatres[main].Need = Math.Max(theatres[main].Need, FP.Ceiling(FP.FromInt(theatres[main].Facing) * AttackRatio).ToInt());
                long sumNeed = 0;
                foreach (int i in open) sumNeed += theatres[i].Need;
                int given = 0;
                var rem = new List<(long r, int i)>();
                foreach (int i in open)
                {
                    long share = (long)theatres[i].Need * total;
                    theatres[i].Budget = (int)(share / Math.Max(1, sumNeed));
                    given += theatres[i].Budget;
                    rem.Add((share % Math.Max(1, sumNeed), i));
                }
                rem.Sort((x, y) => x.r != y.r ? y.r.CompareTo(x.r) : (x.i == main ? -1 : y.i == main ? 1 : x.i.CompareTo(y.i)));
                for (int k = 0; k < total - given && k < rem.Count; k++) theatres[rem[k].i].Budget++;

                // Supply caps: a theatre over what its front feeds gives the rest to the others with room (in
                // proportion to their needs, the main effort first on ties); what nobody can feed is reserve
                for (int round = 0; round < open.Count; round++)
                {
                    int spare = 0;
                    long roomNeed = 0;
                    foreach (int i in open)
                    {
                        if (theatres[i].Budget > theatres[i].SupplyCap) { spare += theatres[i].Budget - theatres[i].SupplyCap; theatres[i].Budget = theatres[i].SupplyCap; }
                        else if (theatres[i].Budget < theatres[i].SupplyCap) roomNeed += theatres[i].Need;
                    }
                    if (spare == 0) break;
                    if (roomNeed == 0) { reserve += spare; break; }
                    int handed = 0;
                    var rem2 = new List<(long r, int i)>();
                    foreach (int i in open)
                    {
                        if (theatres[i].Budget >= theatres[i].SupplyCap) continue;
                        long share = (long)theatres[i].Need * spare;
                        int add = (int)(share / roomNeed);
                        theatres[i].Budget += add;
                        handed += add;
                        rem2.Add((share % roomNeed, i));
                    }
                    rem2.Sort((x, y) => x.r != y.r ? y.r.CompareTo(x.r) : (x.i == main ? -1 : y.i == main ? 1 : x.i.CompareTo(y.i)));
                    for (int k = 0; k < spare - handed && k < rem2.Count; k++) theatres[rem2[k].i].Budget++;
                }
            }
            st.MainEnemy = homeMain != 0 ? homeMain : biggestMain;

            // --- Armies per theatre --------------------------------------------------------------------
            var pool = new SortedSet<int>();
            foreach (int a in alive) if (!mine.ContainsKey(a)) pool.Add(a);
            var wants = new int[theatres.Count];
            for (int i = 0; i < theatres.Count; i++)
            {
                var t = theatres[i];
                // at most one army per front province: each holds a stretch of at least one (a short front with
                // many divisions has bigger armies, not armies sharing a province)
                int n = t.Budget > 0 ? Math.Min(t.Order.Count, Math.Max(1, (t.Budget + ArmySize - 1) / ArmySize)) : 0;
                wants[i] = n;
                var keep = new Dictionary<ushort, int>();
                if (n > 0)
                    foreach (ushort p in t.Order)
                        if (near.TryGetValue(p, out int a) && mine.TryGetValue(a, out int ti) && ti == i) keep[p] = a;
                t.ArmyOf = keep.Count > 0 ? Fill(t, keep) : new Dictionary<ushort, int>();
                while (t.ArmyOf.Count > 0 && Distinct(t.ArmyOf) > n)
                {
                    int drop = Smallest(t.ArmyOf);
                    pool.Add(drop);
                    var rest = new Dictionary<ushort, int>();
                    foreach (var kv in t.ArmyOf) if (kv.Value != drop) rest[kv.Key] = kv.Value;
                    t.ArmyOf = Fill(t, rest);
                }
                if (n == 0) foreach (var kv in mine) if (kv.Value == i) pool.Add(kv.Key);
            }
            for (int i = 0; i < theatres.Count; i++)
            {
                var t = theatres[i];
                int n = wants[i];
                if (n == 0) continue;
                var pos = new Dictionary<ushort, int>();
                for (int k = 0; k < t.Order.Count; k++) pos[t.Order[k]] = k;
                ushort middle = t.Order[t.Order.Count / 2];
                int Take()
                {
                    int bestA = -1;
                    FP bestKm = FP.MaxValue;
                    foreach (int a in pool)
                    {
                        if (!st.Anchor.TryGetValue(a, out ushort anchor) || !Hoi4Movement.TryDistanceKm(anchor, middle, out FP km) || km > TransferKm) continue;
                        if (km < bestKm) { bestKm = km; bestA = a; }
                    }
                    if (bestA >= 0) { pool.Remove(bestA); return bestA; }
                    var army = armies.Create(country, Array.Empty<ushort>(), allowEmpty: true);
                    return army.Id;
                }
                if (t.ArmyOf.Count == 0)
                {
                    var got = new int[n];
                    for (int k = 0; k < n; k++) got[k] = Take();
                    for (int k = 0; k < n; k++)
                        for (int j = t.Order.Count * k / n; j < t.Order.Count * (k + 1) / n; j++) t.ArmyOf[t.Order[j]] = got[k];
                }
                while (Distinct(t.ArmyOf) < n)
                {
                    int big = Largest(t.ArmyOf);
                    var ps = new List<ushort>();
                    foreach (var kv in t.ArmyOf) if (kv.Value == big) ps.Add(kv.Key);
                    if (ps.Count < 2) break; // can't split a one-province stretch (the cap above keeps this from happening)
                    ps.Sort((a, b) => pos[a].CompareTo(pos[b]));
                    int fresh = Take();
                    for (int k = ps.Count / 2; k < ps.Count; k++) t.ArmyOf[ps[k]] = fresh;
                }
                // one connected stretch per army: its largest run stays, islands go to the army around them
                var stay = new Dictionary<ushort, int>();
                var ids = new SortedSet<int>(t.ArmyOf.Values);
                foreach (int a in ids)
                {
                    var set = new HashSet<ushort>();
                    foreach (var kv in t.ArmyOf) if (kv.Value == a) set.Add(kv.Key);
                    List<ushort> bestRun = null;
                    var seen = new HashSet<ushort>();
                    var sorted = new List<ushort>(set);
                    sorted.Sort();
                    foreach (ushort s in sorted)
                    {
                        if (!seen.Add(s)) continue;
                        var run = new List<ushort> { s };
                        for (int k = 0; k < run.Count; k++)
                            foreach (ushort y in t.Links[run[k]])
                                if (set.Contains(y) && seen.Add(y)) run.Add(y);
                        if (bestRun == null || run.Count > bestRun.Count) bestRun = run;
                    }
                    foreach (ushort p in bestRun) stay[p] = a;
                }
                t.ArmyOf = Fill(t, stay);
                var first = new Dictionary<int, int>();
                foreach (var kv in t.ArmyOf) if (!first.TryGetValue(kv.Value, out int f) || pos[kv.Key] < f) first[kv.Value] = pos[kv.Key];
                t.Armies = new List<int>(first.Keys);
                t.Armies.Sort((a, b) => first[a].CompareTo(first[b]));
            }
            foreach (int a in pool)
            {
                var army = armies.Get(a);
                if (army != null)
                {
                    Core.Recording.SessionRecorder.Note("ai_country", "disband", country, 0, 0, 0, $"{army.Name} (id {a}): no theatre needs it in reach, {army.Units.Count} divisions to reserve");
                    DisbandAIArmy(army);
                }
            }

            // --- Budgets per army (by section size, largest remainder), sections, anchors, main armies ---
            st.Budget.Clear();
            st.Anchor.Clear();
            st.Section = new Dictionary<ushort, int>();
            st.MainArmies.Clear();
            st.MainSince.Clear();
            for (int ti = 0; ti < theatres.Count; ti++)
            {
                var t = theatres[ti];
                if (t.Armies.Count == 0) continue;
                var size = new Dictionary<int, int>();
                foreach (var kv in t.ArmyOf) { size[kv.Value] = size.TryGetValue(kv.Value, out int n) ? n + 1 : 1; st.Section[kv.Key] = kv.Value; }
                int given = 0;
                var rem = new List<(long r, int a)>();
                foreach (int a in t.Armies)
                {
                    long share = (long)t.Budget * size[a];
                    st.Budget[a] = (int)(share / t.Provinces.Count);
                    given += st.Budget[a];
                    rem.Add((share % t.Provinces.Count, a));
                }
                rem.Sort((x, y) => x.r != y.r ? y.r.CompareTo(x.r) : x.a.CompareTo(y.a));
                for (int k = 0; k < t.Budget - given && k < rem.Count; k++) st.Budget[rem[k].a]++;
                foreach (int a in t.Armies)
                {
                    var ps = new List<ushort>();
                    foreach (ushort p in t.Order) if (t.ArmyOf[p] == a) ps.Add(p);
                    st.Anchor[a] = ps[ps.Count / 2];
                    if (t.Main) { st.MainArmies.Add(a); st.MainSince[a] = mainSince[ti]; }
                }
            }

            using (ArmyLinesMarker.Auto()) UpdateArmyLines(country, theatres);
            using (MembershipMarker.Auto()) UpdateMembership(country, st, theatres, own, labels);

            UpdatePosture(st, theatres);

            // what the daily follow-up works from until the next plan
            st.LastPlan = today;
            st.Theatres = theatres;
            st.ArmyTheatre.Clear();
            for (int ti = 0; ti < theatres.Count; ti++) foreach (int a in theatres[ti].Armies) st.ArmyTheatre[a] = ti;
            st.Enemies = new SortedSet<ushort>();
            foreach (var t in theatres) st.Enemies.UnionWith(t.Enemies);
            st.FrontSize = front.Count;

            var summary = new List<string>();
            foreach (var t in theatres)
            {
                var parts = new List<string>();
                foreach (int a in t.Armies)
                {
                    var army = armies.Get(a);
                    int n = 0;
                    foreach (var kv in t.ArmyOf) if (kv.Value == a) n++;
                    parts.Add($"{army?.Name ?? a.ToString()} {army?.Units.Count ?? 0}/{st.Budget[a]} on {n}p");
                }
                summary.Add($"{TheatreName(t)}{(t.Main ? " MAIN" : "")}{(t.Attacking ? " ATTACK" : "")}{(t.Overseas ? " OVERSEAS" : "")} {t.Provinces.Count}p danger {t.Danger.ToFloat():0.00} need {t.Need} supply {t.Supply.ToFloat():0.0} (cap {t.SupplyCap}) budget {t.Budget}: {string.Join(", ", parts)}");
            }
            Core.Recording.SessionRecorder.Note("ai_country", "theatres", country, 0, 0, st.MainEnemy,
                $"plan ({reason}): {own.Count} divisions, reserve target {reserve}; {string.Join(" | ", summary)}");
        }

        /// <summary>
        /// The country's landmasses: connected pieces of passable land it may walk on (its own, allies', access)
        /// that no enemy controls and that hold some of its own land, by province id; -1 elsewhere. Unlike WalkLabels an enemy division standing in our
        /// land doesn't split it (that flickered fronts between "landmasses" hour to hour). Its divisions can reach
        /// each other's landmass only by sea, which isn't modelled: budgets are per landmass.
        /// </summary>
        int[] Landmasses(ushort country)
        {
            movementRules ??= new Hoi4MovementRules(gameState);
            SnapshotHour();
            var diplomacy = gameState.Diplomacy;
            var label = new int[landNeighbours.Length];
            System.Array.Fill(label, -1);
            // Only land reachable from the country's own: every caller looks at its fronts, sections and the
            // divisions that can walk to them (labelling the whole map for every country was 3 ms a frame).
            // Walkable is decided per province as the fill reaches it
            var ok = new Dictionary<(ushort owner, ushort ctrl), bool>();
            bool Walk(ushort p)
            {
                if (Hoi4Regions.IsImpassable(p)) return false;
                ushort c = hourCtrl[p], o = hourOwner[p];
                if (c == country) return true;
                if (!ok.TryGetValue((o, c), out bool w))
                    ok[(o, c)] = w = c != 0 && !diplomacy.IsAtWar(country, c) && movementRules.HasAccess(country, o);
                return w;
            }
            int next = 0;
            var queue = new List<ushort>();
            foreach (ushort s in landProvinces)
            {
                if (hourCtrl[s] != country || label[s] >= 0 || !Walk(s)) continue;
                int id = next++;
                queue.Clear();
                queue.Add(s);
                label[s] = id;
                for (int k = 0; k < queue.Count; k++)
                    foreach (ushort r in Neighbours(queue[k]))
                        if (label[r] < 0 && Walk(r)) { label[r] = id; queue.Add(r); }
            }
            return label;
        }

        static SortedDictionary<ushort, List<ushort>> Groups(List<ushort> front, Func<ushort, ushort> find)
        {
            var groups = new SortedDictionary<ushort, List<ushort>>();
            foreach (ushort p in front)
            {
                ushort r = find(p);
                if (!groups.TryGetValue(r, out var g)) groups[r] = g = new List<ushort>();
                g.Add(p);
            }
            return groups;
        }

        string TheatreName(Theatre t)
        {
            var tags = new List<string>();
            foreach (ushort e in t.Enemies) tags.Add(Tag(e));
            return $"vs {string.Join("+", tags)} @{t.Order[t.Order.Count / 2]}";
        }

        /// <summary>Hops along a theatre's front (its links) from a start.</summary>
        static Dictionary<ushort, int> WalkAlong(Theatre t, ushort start)
        {
            var d = new Dictionary<ushort, int> { [start] = 0 };
            var q = new List<ushort> { start };
            for (int i = 0; i < q.Count; i++)
                foreach (ushort y in t.Links[q[i]])
                    if (!d.ContainsKey(y)) { d[y] = d[q[i]] + 1; q.Add(y); }
            return d;
        }

        /// <summary>The theatre's provinces without an army join the nearest army along the front (ties: the lower
        /// army id, then province id); unlinked ones (shouldn't happen) the lowest army.</summary>
        static Dictionary<ushort, int> Fill(Theatre t, Dictionary<ushort, int> army)
        {
            var q = new List<ushort>(army.Keys);
            q.Sort((a, b) => army[a] != army[b] ? army[a].CompareTo(army[b]) : a.CompareTo(b));
            for (int i = 0; i < q.Count; i++)
                foreach (ushort y in t.Links[q[i]])
                    if (!army.ContainsKey(y)) { army[y] = army[q[i]]; q.Add(y); }
            if (army.Count > 0)
            {
                int low = int.MaxValue;
                foreach (int v in army.Values) low = Math.Min(low, v);
                foreach (ushort p in t.Order) if (!army.ContainsKey(p)) army[p] = low;
            }
            return army;
        }

        static int Distinct(Dictionary<ushort, int> army) => new HashSet<int>(army.Values).Count;

        static Dictionary<int, int> Sizes(Dictionary<ushort, int> army)
        {
            var size = new Dictionary<int, int>();
            foreach (int v in army.Values) size[v] = size.TryGetValue(v, out int n) ? n + 1 : 1;
            return size;
        }

        /// <summary>The army with the fewest provinces (ties: the higher id).</summary>
        static int Smallest(Dictionary<ushort, int> army)
        {
            int best = -1, n = int.MaxValue;
            foreach (var kv in Sizes(army)) if (kv.Value < n || (kv.Value == n && kv.Key > best)) { best = kv.Key; n = kv.Value; }
            return best;
        }

        /// <summary>The army with the most provinces (ties: the lower id).</summary>
        static int Largest(Dictionary<ushort, int> army)
        {
            int best = -1, n = -1;
            foreach (var kv in Sizes(army)) if (kv.Value > n || (kv.Value == n && kv.Key < best)) { best = kv.Key; n = kv.Value; }
            return best;
        }

        /// <summary>
        /// Strategic layer, lines: one plan per army and enemy its section faces, holding that part of the border
        /// (Section; Outside the rest of the border with that enemy). Created, moved or removed to match the
        /// sections; a plan whose fronts changed spreads its divisions again on the next hour.
        /// </summary>
        void UpdateArmyLines(ushort country, List<Theatre> theatres)
        {
            var want = new Dictionary<(int army, ushort enemy), HashSet<ushort>>();
            foreach (var t in theatres)
                foreach (var kv in t.ArmyOf)
                    foreach (ushort n in Neighbours(kv.Key))
                    {
                        ushort e = hourCtrl[n];
                        if (!t.Enemies.Contains(e) || Hoi4Regions.IsImpassable(n)) continue;
                        if (!want.TryGetValue((kv.Value, e), out var set)) want[(kv.Value, e)] = set = new HashSet<ushort>();
                        set.Add(kv.Key);
                    }
            var border = new Dictionary<ushort, List<List<ushort>>>();
            List<List<ushort>> Border(ushort e)
            {
                if (!border.TryGetValue(e, out var f)) border[e] = f = FrontsAgainst(country, e);
                return f;
            }
            var have = new Dictionary<(int, ushort), Hoi4BattlePlan>();
            foreach (var plan in new List<Hoi4BattlePlan>(plans))
            {
                if (!plan.IsAI || plan.Owner != country) continue;
                if (!want.ContainsKey((plan.ArmyId, plan.Country)) || have.ContainsKey((plan.ArmyId, plan.Country)))
                {
                    RemovePlan(plan);
                    if (IsDrawn(plan)) simMeshDirty = true;
                    continue;
                }
                have[(plan.ArmyId, plan.Country)] = plan;
            }
            var keys = new List<(int army, ushort enemy)>(want.Keys);
            keys.Sort();
            foreach (var key in keys)
            {
                var section = want[key];
                var all = Border(key.enemy);
                var outside = new HashSet<ushort>();
                foreach (var f in all) foreach (ushort p in f) if (!section.Contains(p)) outside.Add(p);
                bool fresh = !have.TryGetValue(key, out var plan);
                if (fresh) plan = new Hoi4BattlePlan { Id = nextId++, Owner = country, IsAI = true, ArmyId = key.army, Country = key.enemy };
                plan.Section = new HashSet<ushort>(section);
                plan.Outside = outside;
                var fronts = FrontsOf(plan, all);
                if (fronts.Count == 0) { if (!fresh) RemovePlan(plan); continue; }
                if (!fresh && SameFronts(fronts, plan.Fronts)) continue;
                SetFronts(plan, fronts);
                if (fresh)
                {
                    plans.Add(plan);
                    Core.Recording.SessionRecorder.Note("plans", "created", country, 0, 0, key.enemy, $"plan {plan.Id} (AI) army {key.army}: fronts {FrontSizes(plan)}");
                }
                if (IsDrawn(plan)) { plan.TraceDirty = true; simMeshDirty = true; }
                dirtyPlans.Add(plan);
            }
        }

        /// <summary>
        /// Divisions to armies: an army over its budget lets the ones furthest from its section go to reserve
        /// (no army; a march to its old line stops); then each army under its budget, most short first, takes the
        /// nearest reserve divisions it can walk to (same walk label as its section, within MaxTransferHops land
        /// hops; nearest ring first, ties the lower id). Divisions fighting, retreating or attacking stay.
        /// </summary>
        void UpdateMembership(ushort country, AIState st, List<Theatre> theatres, List<ushort> own, int[] labels)
        {
            var armies = Hoi4Armies.Instance;
            var units = gameState.Units;
            var queue = units.MovementQueue;
            bool Busy(ushort u) => Hoi4Combat.IsFighting(u) || Hoi4Combat.IsRetreating(u) || borrowed.Contains(u);
            // Per theatre, not per army: an army's budget is its share of the theatre's by section length, and a
            // section a province longer or shorter swung it by several divisions, released by one army and taken
            // back by its neighbour on the same line the next day. Divisions go to reserve only when the whole
            // theatre is clearly over (more than a quarter, at least 2), from the armies most over their share,
            // the ones furthest from their section first; joins below fill a theatre only up to its budget
            var released = new List<ushort>();
            var theatreOf = new Dictionary<int, int>(); // army -> its theatre's index
            var room = new int[theatres.Count];
            for (int ti = 0; ti < theatres.Count; ti++)
            {
                var th = theatres[ti];
                int have = 0;
                foreach (int a in th.Armies) { have += armies.Get(a)?.Units.Count ?? 0; theatreOf[a] = ti; }
                room[ti] = th.Budget - have;
                int over = have - th.Budget;
                if (over <= Math.Max(2, th.Budget / 4)) continue;
                var byExcess = new List<(int excess, int id)>();
                foreach (int a in th.Armies)
                {
                    var army = armies.Get(a);
                    if (army != null && st.Budget.TryGetValue(a, out int b) && army.Units.Count > b) byExcess.Add((army.Units.Count - b, a));
                }
                byExcess.Sort((x, y) => x.excess != y.excess ? y.excess.CompareTo(x.excess) : x.id.CompareTo(y.id));
                foreach (var (excess, a) in byExcess)
                {
                    if (over <= 0) break;
                    var army = armies.Get(a);
                    ushort anchor = st.Anchor[a];
                    var far = new List<(long km, ushort u)>();
                    foreach (ushort u in army.Units)
                    {
                        if (!units.HasUnit(u) || Busy(u)) continue;
                        long km = Hoi4Movement.TryDistanceKm(units.GetUnit(u).provinceID, anchor, out FP d) ? d.RawValue : long.MaxValue;
                        far.Add((km, u));
                    }
                    far.Sort((x, y) => x.km != y.km ? y.km.CompareTo(x.km) : y.u.CompareTo(x.u));
                    int n = Math.Min(excess, over);
                    for (int i = 0; i < far.Count && i < n; i++) { released.Add(far[i].u); over--; room[ti]++; }
                }
            }
            if (released.Count > 0)
            {
                foreach (ushort u in released) if (queue.IsUnitMoving(u)) queue.CancelMovement(u);
                armies.Release(released);
                Core.Recording.SessionRecorder.Note("ai_country", "reserve", country, 0, 0, 0, $"{released.Count} divisions over their theatre's budget to reserve: {string.Join(" ", released)}");
            }

            // reserve divisions by province
            var reserveAt = new Dictionary<ushort, List<ushort>>();
            int reserveCount = 0;
            foreach (ushort u in own)
            {
                if (!units.HasUnit(u) || armies.ArmyOf(u) != null || Busy(u)) continue;
                ushort p = units.GetUnit(u).provinceID;
                if (!reserveAt.TryGetValue(p, out var l)) reserveAt[p] = l = new List<ushort>();
                l.Add(u);
                reserveCount++;
            }
            if (reserveCount == 0) return;
            var needy = new List<(int gap, int id)>();
            foreach (var army in armies.Of(country))
                if (st.Budget.TryGetValue(army.Id, out int b) && b > army.Units.Count) needy.Add((b - army.Units.Count, army.Id));
            needy.Sort((a, b) => a.gap != b.gap ? b.gap.CompareTo(a.gap) : a.id.CompareTo(b.id));
            var log = new List<string>();
            // Near first (within MaxTransferHops), then, for a gap still open, the nearest reserves it can walk to
            // at any distance (a new war far from home has nobody near it)
            for (int pass = 0; pass < 2; pass++)
            foreach (var (_, id) in needy)
            {
                var army = armies.Get(id);
                int gap = st.Budget[id] - army.Units.Count;
                int ti = theatreOf.TryGetValue(id, out int tix) ? tix : -1;
                if (ti >= 0) gap = Math.Min(gap, room[ti]);
                if (gap <= 0) continue;
                int maxHops = pass == 0 ? MaxTransferHops : int.MaxValue;
                if (pass == 1 && army.Units.Count * 2 >= st.Budget[id]) continue; // far only when below half its budget
                var section = new List<ushort>();
                foreach (var kv in st.Section) if (kv.Value == id) section.Add(kv.Key);
                section.Sort();
                if (section.Count == 0) continue;
                int label = labels[section[0]];
                var take = new List<ushort>();
                var seen = new HashSet<ushort>(section);
                var ring = new List<ushort>(section);
                for (int hop = 0; hop <= maxHops && ring.Count > 0 && take.Count < gap; hop++)
                {
                    foreach (ushort p in ring)
                    {
                        if (!reserveAt.TryGetValue(p, out var here)) continue;
                        while (here.Count > 0 && take.Count < gap) { take.Add(here[0]); here.RemoveAt(0); }
                        if (take.Count >= gap) break;
                    }
                    var next = new List<ushort>();
                    foreach (ushort p in ring)
                        foreach (ushort n in Neighbours(p))
                            if (labels[n] == label && seen.Add(n)) next.Add(n);
                    next.Sort();
                    ring = next;
                }
                if (take.Count == 0) continue;
                if (ti >= 0) room[ti] -= take.Count;
                armies.Add(army, take);
                log.Add($"{army.Name} +{take.Count}{(pass == 1 ? " (far)" : "")}");
            }
            if (log.Count > 0)
                Core.Recording.SessionRecorder.Note("ai_country", "join", country, 0, 0, 0, $"reserve to armies: {string.Join(", ", log)}");
        }

        /// <summary>
        /// The homeland's mirror of RouteCosts (prototype Tools/prototypes/ai_armies/defence.py): over land the
        /// country controls, the cheapest route an enemy would take from each province to its core victory points
        /// (in states it has a core on, still its own), each starting at ObjectiveBase / (1 + VP / 5); a step costs
        /// the terrain (movement cost / (1 + attack modifier)) plus 1 or 2 for a river. Our own divisions don't
        /// count: they are what is being placed. Once per run.
        /// </summary>
        Dictionary<ushort, FP> HomeCosts(ushort country)
        {
            var result = new Dictionary<ushort, FP>();
            var war = Hoi4.War.Hoi4War.Instance;
            string tag = Tag(country);
            bool Mine(ushort p) => p < hourCtrl.Length && hourCtrl[p] == country && !Hoi4.Map.Hoi4Regions.IsImpassable(p);
            var queue = new SortedSet<(long cost, ushort p)>();
            foreach (ushort p in landProvinces)
            {
                if (!Mine(p)) continue;
                int vp = war != null ? war.VictoryPoints(p) : 0;
                if (vp <= 0) continue;
                var s = Hoi4.Map.Hoi4StateData.Get(Hoi4.Map.Hoi4Regions.State(p));
                if (s == null || !s.Cores.Contains(tag)) continue;
                FP c = ObjectiveBase / (FP.One + FP.FromInt(vp) / 5) + TerrainStep(p);
                result[p] = c;
                queue.Add((c.RawValue, p));
            }
            while (queue.Count > 0)
            {
                var top = queue.Min;
                queue.Remove(top);
                ushort p = top.p;
                FP c = result[p];
                foreach (ushort n in Neighbours(p))
                {
                    if (!Mine(n)) continue;
                    FP nc = c + TerrainStep(n) + FP.FromInt(Hoi4.Map.Hoi4CombatTerrain.River(gameState, p, n));
                    if (result.TryGetValue(n, out FP old))
                    {
                        if (nc >= old) continue;
                        queue.Remove((old.RawValue, n));
                    }
                    result[n] = nc;
                    queue.Add((nc.RawValue, n));
                }
            }
            return result;
        }

        // Terrain never changes: each province's step cost, read once (home and route costs ask tens of thousands of times)
        FP[] terrainStep;
        bool[] terrainStepKnown;

        FP TerrainStep(ushort p)
        {
            if (terrainStep == null) { terrainStep = new FP[landNeighbours.Length]; terrainStepKnown = new bool[landNeighbours.Length]; }
            if (p < terrainStep.Length && terrainStepKnown[p]) return terrainStep[p];
            FP attack = Hoi4.Map.Hoi4CombatTerrain.AttackModifier(gameState, p);
            FP step = Hoi4.Map.Hoi4CombatTerrain.MovementCost(gameState, p) / FP.Max(FP.FromFraction(1, 10), FP.One + attack);
            if (p < terrainStep.Length) { terrainStep[p] = step; terrainStepKnown[p] = true; }
            return step;
        }

        /// <summary>
        /// Route costs on an enemy's land (after hoi4-custom-ai's planner): a cheapest-route search from the victory
        /// points of its main body (its largest connected piece of land, weighted by victory points and size;
        /// pockets and colonies hold no objectives), each starting at ObjectiveBase / (1 + VP / 5) so bigger
        /// points pull harder. Entering a province costs its terrain (movement cost / (1 + attack modifier):
        /// plains 1, forest 1.8, hills 2, marsh 3.3, mountain 4) times 1 + its defenders (divisions, by strength),
        /// plus 1 or 2 for a small or large river crossed. Once per enemy per day.
        /// </summary>
        Dictionary<ushort, FP> RouteCosts(ushort enemy)
        {
            if (routes.TryGetValue(enemy, out var known)) return known;
            using var _ = RouteCostsMarker.Auto();
            var result = new Dictionary<ushort, FP>();
            routes[enemy] = result;
            if (!LandByController().TryGetValue(enemy, out var landList)) return result;
            var land = new HashSet<ushort>();
            foreach (ushort p in landList) if (!Hoi4.Map.Hoi4Regions.IsImpassable(p)) land.Add(p);
            var war = Hoi4.War.Hoi4War.Instance;
            int Vp(ushort p) => war != null ? war.VictoryPoints(p) : 0;

            // main body
            var seen = new HashSet<ushort>();
            List<ushort> body = null;
            int bodyWeight = -1;
            var sorted = new List<ushort>(land);
            sorted.Sort();
            foreach (ushort s in sorted)
            {
                if (!seen.Add(s)) continue;
                var piece = new List<ushort> { s };
                for (int k = 0; k < piece.Count; k++)
                    foreach (ushort n in Neighbours(piece[k]))
                        if (land.Contains(n) && seen.Add(n)) piece.Add(n);
                int w = piece.Count;
                foreach (ushort p in piece) w += Vp(p);
                if (w > bodyWeight) { bodyWeight = w; body = piece; }
            }
            if (body == null) return result;

            // defenders per province (divisions, by strength)
            var foe = new Dictionary<ushort, FP>();
            var divisions = Hoi4Divisions.Instance;
            foreach (ushort p in land)
                foreach (ushort u in gameState.Units.GetUnitsInProvince(p))
                {
                    if (gameState.Units.GetUnit(u).countryID != enemy) continue;
                    var d = divisions?.Get(u);
                    FP share = d != null && d.MaxStrength > FP.Zero ? FP.Min(FP.One, d.Strength / d.MaxStrength) : FP.One;
                    foe[p] = (foe.TryGetValue(p, out FP f) ? f : FP.Zero) + share;
                }
            FP Step(ushort p) => TerrainStep(p) * (FP.One + (foe.TryGetValue(p, out FP f) ? f : FP.Zero));

            // cheapest routes from the objectives (Dijkstra; ties by province id)
            var queue = new SortedSet<(long cost, ushort p)>();
            foreach (ushort p in body)
            {
                int vp = Vp(p);
                if (vp <= 0) continue;
                FP c = ObjectiveBase / (FP.One + FP.FromInt(vp) / 5) + Step(p);
                result[p] = c;
                queue.Add((c.RawValue, p));
            }
            while (queue.Count > 0)
            {
                var top = queue.Min;
                queue.Remove(top);
                ushort p = top.p;
                FP c = result[p];
                foreach (ushort n in Neighbours(p))
                {
                    if (!land.Contains(n)) continue;
                    int river = Hoi4.Map.Hoi4CombatTerrain.River(gameState, p, n);
                    FP nc = c + Step(n) + FP.FromInt(river);
                    if (result.TryGetValue(n, out FP old))
                    {
                        if (nc >= old) continue;
                        queue.Remove((old.RawValue, n));
                    }
                    result[n] = nc;
                    queue.Add((nc.RawValue, n));
                }
            }
            return result;
        }

        /// <summary>Enemy divisions within FacingHops of the front provinces, on the enemies' side.</summary>
        int FacingDivisions(List<ushort> frontProvinces, SortedSet<ushort> enemies)
        {
            var seen = new HashSet<ushort>(frontProvinces);
            var ring = new List<ushort>();
            foreach (ushort p in frontProvinces)
                foreach (ushort n in Neighbours(p))
                    if (enemies.Contains(hourCtrl[n]) && seen.Add(n)) ring.Add(n);
            int count = 0;
            for (int hop = 1; hop <= FacingHops && ring.Count > 0; hop++)
            {
                var next = new List<ushort>();
                foreach (ushort p in ring)
                {
                    foreach (ushort u in gameState.Units.GetUnitsInProvince(p))
                        if (enemies.Contains(gameState.Units.GetUnit(u).countryID)) count++;
                    if (hop < FacingHops)
                        foreach (ushort n in Neighbours(p))
                            if (enemies.Contains(hourCtrl[n]) && seen.Add(n)) next.Add(n);
                }
                ring = next;
            }
            return count;
        }

        string Tag(ushort country) => gameState.Countries.GetCountryTag(country);
    }
}
