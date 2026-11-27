using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Systems;
using Hoi4.Map;
using StarterKit.Commands;
using Unity.Collections;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the simplest war AI. Every game day each AI country (any country but the player's) at war
    /// attacks the border provinces of every enemy that borders it (the strongest one, by total division
    /// strength, is named in the notes) target by target, the main effort's first (Hoi4BattlePlans.MainEffort:
    /// along the cheapest route on to the enemy's victory points, the only front that takes uneven fights;
    /// the others take good odds only): each target gathers the free divisions next to it (on the line: a
    /// battle plan's way, the line attacks across itself), until the estimated odds (Hoi4Combat.Odds, with the divisions already
    /// marching there) are good, and the group attacks together; a group that falls short of good odds takes
    /// the uneven fight now and then, never a hopeless one, and otherwise stays free for the next target.
    /// Divisions that are moving, worn down (organisation or strength), or defending a province under
    /// attack are left alone. No more divisions go at a province than its combat width holds (the rest would
    /// wait in reserve).
    /// Encircled divisions (in a piece of our land cut off from the one with the capital, surrounded by enemies:
    /// no coast, no neutral or allied neighbour) don't take part in the
    /// line's attacks: each pocket breaks out across its border province nearest the main body, at any odds but
    /// hopeless ones (BoldOdds), and otherwise holds.
    /// Every hour, attacks that go badly are called off before the attackers are ground down.
    /// Orders go through the same QueueUnitMovementCommand as the player's, so combat, retreat and
    /// occupation apply unchanged. Holding the line is the front lines' job (Hoi4BattlePlans makes one per
    /// enemy border): an attacker is borrowed off its line and rejoins it when the attack is over.
    /// Cost: each country decides at its own hour of the day (id mod 24), so the work is spread over the day
    /// rather than all on the day tick; attackers are next to their target, so there are no path searches.
    /// </summary>
    public sealed class Hoi4EnemyAI : MonoBehaviour
    {
        static readonly FP MinOrganisation = FP.FromFraction(7, 10);     // of max: tired divisions rest before attacking again
        static readonly FP MinStrength = FP.Half;                        // of max: battered divisions don't attack at all
        static readonly FP GoodOdds = FP.One;                            // attack when the attackers should break the defenders first
        static readonly FP BoldOdds = FP.FromFraction(6, 10);            // uneven fights: taken sometimes (BoldChance), never below this
        static readonly FP StrongOdds = FP.FromInt(2);                   // a holding front attacks only this sure (or an empty province)
        const int BoldChance = 16384;                                    // out of 65536 (a quarter)
        static readonly FP BreakOffOrganisation = FP.FromFraction(3, 10); // of max: an attacker this worn down breaks off
        static readonly FP BreakOffStrength = FP.FromFraction(35, 100);
        const int BreakOffPercent = 25;       // or when its side holds less than this share of the battle's organisation
        // Attackers come only from the line: divisions in a province next to the target (a battle plan's way: the
        // line attacks across itself and moves forward as a whole). Gathering from 3-5 hops away pulled holders
        // off the line, left holes and stray groups deep in enemy land, and walked them back afterwards.
        const int Reach = 1;
        const int ReserveDepth = 3;           // an attack short of good odds gathers up to this many times the width (reserves)

        public static Hoi4EnemyAI Instance { get; private set; }

        GameState gameState;
        ushort[] provinceIds;
        readonly List<ushort> idle = new List<ushort>();
        readonly Dictionary<ushort, int> load = new Dictionary<ushort, int>();
        ushort[] landProvinces;                                                    // sorted, passable land
        readonly HashSet<ushort> coastal = new HashSet<ushort>();                  // land next to water
        readonly Dictionary<ushort, ushort[]> landNeighbours = new Dictionary<ushort, ushort[]>(); // sorted

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4EnemyAI");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4EnemyAI>();
        }

        void Awake() => Instance = this;

        IEnumerator Start()
        {
            while (Hoi4Divisions.Instance == null || !Hoi4Divisions.Instance.IsLoaded
                   || Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Time == null
                   || Engine.ArchonEngine.Instance.GameState.Units == null
                   || Engine.ArchonEngine.Instance.GameState.Diplomacy == null
                   || Engine.ArchonEngine.Instance.GameState.Adjacencies == null
                   || Engine.ArchonEngine.Instance.GameState.Pathfinding == null
                   || !Engine.ArchonEngine.Instance.GameState.Pathfinding.IsInitialized)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            var ids = gameState.Provinces.GetAllProvinceIds(Allocator.Temp);
            provinceIds = ids.ToArray();
            ids.Dispose();
            System.Array.Sort(provinceIds);
            var land = new List<ushort>();
            foreach (ushort p in provinceIds)
            {
                if (p == 0 || Hoi4Regions.IsWater(p)) continue;
                var n = new List<ushort>();
                foreach (ushort q in gameState.Adjacencies.GetNeighborsWhere(p, q => !Hoi4Regions.IsWater(q))) n.Add(q);
                n.Sort();
                landNeighbours[p] = n.ToArray();
                foreach (ushort q in gameState.Adjacencies.GetNeighborsWhere(p, q => Hoi4Regions.IsWater(q))) { coastal.Add(p); break; }
                if (!Hoi4Regions.IsImpassable(p)) land.Add(p);
            }
            landProvinces = land.ToArray();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>Call off attacks that go badly: worn-down attackers, or a battle that is clearly lost.
        /// Called every hour by Hoi4Simulation, after combat.</summary>
        public void SimulateHour()
        {
            if (gameState == null) return;
            ushort player = PlayerCountry;
            var units = gameState.Units;
            var queue = units.MovementQueue;
            var divisions = Hoi4Divisions.Instance;
            if (Hoi4Combat.Battles.Count == 0) return;
            var battleAt = new Dictionary<ushort, Hoi4Combat.Battle>();
            foreach (var battle in Hoi4Combat.Battles) battleAt[battle.To] = battle;
            var breakOff = new List<ushort>();
            foreach (ushort id in queue.GetMovingUnits())
            {
                // An attacker: an AI division held at the border, its march aimed at a province in battle
                if (!queue.TryGetMovementState(id, out var st) || !battleAt.TryGetValue(st.destinationProvinceID, out var battle)) continue;
                ushort owner = units.GetUnit(id).countryID;
                if (owner == player) continue;
                // Not a reinforcement marching into its own side's province
                if (!gameState.Diplomacy.IsAtWar(owner, Hoi4Occupation.ControllerOf(gameState, st.destinationProvinceID))) continue;
                var d = divisions.Get(id);
                if (d == null) continue;
                bool worn = d.Organisation < d.MaxOrganisation * BreakOffOrganisation || d.Strength < d.FullStrength * BreakOffStrength;
                if (worn || battle.AttackerPercent < BreakOffPercent)
                {
                    breakOff.Add(id);
                    Core.Recording.SessionRecorder.Note("ai", "break_off", owner, id, st.destinationProvinceID, 0,
                        (worn ? "worn" : "losing") + $" battle {battle.AttackerPercent}% org {d.Organisation.ToFloat():0.#}/{d.MaxOrganisation.ToFloat():0.#} str {d.Strength.ToFloat():0.#}/{d.MaxStrength.ToFloat():0.#}");
                }
            }
            // Not while iterating the moving units; one command per country
            breakOff.Sort();
            var byCountry = new SortedDictionary<ushort, List<ushort>>();
            foreach (ushort id in breakOff)
            {
                ushort c = units.GetUnit(id).countryID;
                if (!byCountry.TryGetValue(c, out var list)) byCountry[c] = list = new List<ushort>();
                list.Add(id);
            }
            foreach (var kv in byCountry)
                gameState.TryExecuteCommand(new HaltUnitsCommand { CountryId = kv.Key, UnitIds = kv.Value });
        }

        /// <summary>Deterministic "dice" per country, province and day (same game, same choices): 0..65535.</summary>
        static int Roll(ushort country, ushort province, int day)
        {
            uint h = (uint)country * 73856093u ^ (uint)province * 19349663u ^ (uint)day * 83492791u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return (int)(h & 0xFFFF);
        }

        static ushort PlayerCountry
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        /// <summary>New attacks, once a day per country at its own hour (country id mod 24); called every hour by
        /// Hoi4Simulation with the hour of the day. Countries in id order.</summary>
        public void DecideAttacks(int hourOfDay)
        {
            if (gameState == null) return;
            ushort player = PlayerCountry;
            var diplomacy = gameState.Diplomacy;
            var countries = new SortedSet<ushort>();
            foreach (ushort id in gameState.Units.GetAllUnits())
                countries.Add(gameState.Units.GetUnit(id).countryID);
            ushort[] ctrl = null; // land controllers, read once for every country deciding this hour
            foreach (ushort country in countries)
                if (country % 24 == hourOfDay && country != player && country != 0 && diplomacy.IsAtWar(country))
                    Attack(country, ctrl ??= Controllers());
        }

        /// <summary>Controller of every land province, impassable too (by province id). Orders given while deciding
        /// don't change control (that happens when a division arrives), so one snapshot serves the whole hour.</summary>
        ushort[] Controllers()
        {
            Hoi4Occupation.Snapshot(gameState, provinceIds.Length == 0 ? 1 : provinceIds[provinceIds.Length - 1] + 1, out var ctrl, out _);
            return ctrl; // shared: read only
        }

        int OwnNeighbours(ushort p, ushort[] ctrl, ushort country)
        {
            int n = 0;
            if (landNeighbours.TryGetValue(p, out var ns))
                foreach (ushort q in ns) if (q < ctrl.Length && ctrl[q] == country) n++;
            return n;
        }

        bool Adjacent(ushort a, ushort b) =>
            landNeighbours.TryGetValue(a, out var n) && System.Array.BinarySearch(n, b) >= 0;

        static string OddsText(FP odds) => odds == FP.MaxValue ? "undefended" : odds.ToFloat().ToString("0.00");

        FP Strength(ushort country)
        {
            FP total = FP.Zero;
            foreach (ushort id in gameState.Units.GetCountryUnits(country))
            {
                var d = Hoi4Divisions.Instance.Get(id);
                if (d != null) total += d.Strength;
            }
            return total;
        }

        void Attack(ushort country, ushort[] ctrl)
        {
            var enemies = new HashSet<ushort>(gameState.Diplomacy.GetEnemies(country));
            if (enemies.Count == 0) return;

            // Border provinces per enemy: land the enemy controls next to land we control
            var fronts = new Dictionary<ushort, List<ushort>>();
            foreach (ushort p in landProvinces)
            {
                ushort holder = ctrl[p];
                if (holder == 0 || holder == country || !enemies.Contains(holder)) continue;
                bool border = false;
                foreach (ushort n in landNeighbours[p])
                    if (ctrl[n] == country) { border = true; break; }
                if (!border || Hoi4Combat.IsAttackedFrom(gameState, country, p)) continue;
                if (!fronts.TryGetValue(holder, out var list)) fronts[holder] = list = new List<ushort>();
                list.Add(p);
            }
            if (fronts.Count == 0) { Core.Recording.SessionRecorder.Note("ai", "no_front", country, 0, 0, 0, "at war but borders no enemy land it can attack"); return; }

            ushort target = 0;
            FP best = FP.NegativeOne;
            foreach (var kv in fronts)
            {
                FP s = Strength(kv.Key);
                if (s > best || (s == best && kv.Key < target)) { best = s; target = kv.Key; } // ties: lowest id
            }
            // Every bordered enemy's land is a target (the strongest one is only named in the note): a weaker
            // second enemy left out would keep its land for as long as the main war lasts
            var targets = new List<ushort>();
            foreach (var kv in fronts) targets.AddRange(kv.Value);
            Core.Recording.SessionRecorder.Note("ai", "target", country, 0, 0, target, $"{targets.Count} border provinces; enemies bordered {fronts.Count} (main {fronts[target].Count})");

            // Idle divisions: not marching, rested, and not the defenders of a province under attack
            idle.Clear();
            var own = gameState.Units.GetCountryUnits(country);
            own.Sort();
            int tired = 0, moving = 0, defendingCount = 0;
            var attacked = new HashSet<ushort>();
            foreach (var b in Hoi4Combat.Battles) attacked.Add(b.To);
            foreach (ushort id in own)
            {
                var d = Hoi4Divisions.Instance.Get(id);
                if (d == null) continue;
                if (d.Organisation < d.MaxOrganisation * MinOrganisation || d.Strength < d.FullStrength * MinStrength) { tired++; continue; }
                if (gameState.Units.MovementQueue.IsUnitMoving(id)) { moving++; continue; }
                ushort at = gameState.Units.GetUnit(id).provinceID;
                if (!attacked.Contains(at)) idle.Add(id);
                else defendingCount++;
            }
            Core.Recording.SessionRecorder.Note("ai", "pool", country, 0, 0, target, $"divisions {own.Count}: free {idle.Count}, tired {tired}, marching {moving}, defending {defendingCount}");
            if (idle.Count == 0) return;

            // Divisions already marching at this enemy count towards the width and the odds
            load.Clear();
            var attackersOf = new Dictionary<ushort, List<Hoi4Division>>();
            var defendersOf = new Dictionary<ushort, List<Hoi4Division>>();
            foreach (ushort t in targets)
            {
                load[t] = 0;
                attackersOf[t] = new List<Hoi4Division>();
                var defenders = new List<Hoi4Division>();
                foreach (ushort e in gameState.Units.GetUnitsInProvince(t))
                {
                    var ed = Hoi4Divisions.Instance.Get(e);
                    if (ed != null && enemies.Contains(gameState.Units.GetUnit(e).countryID)) defenders.Add(ed);
                }
                defendersOf[t] = defenders;
            }
            foreach (ushort id in gameState.Units.GetCountryUnits(country))
                if (gameState.Units.MovementQueue.TryGetMovementState(id, out var st) && load.ContainsKey(st.destinationProvinceID))
                {
                    load[st.destinationProvinceID]++;
                    var md = Hoi4Divisions.Instance.Get(id);
                    if (md != null) attackersOf[st.destinationProvinceID].Add(md);
                }
            int today = (int)(gameState.Time.CurrentTick / 24); // day count since the start (CurrentTick is hours)
            var unitTypes = StarterKit.Initializer.Instance?.UnitSystem;

            // Free divisions by province (idle is in id order), to gather each target's attackers around it; the
            // encircled ones apart (they break out instead, below)
            var pocketOf = Pockets(country, ctrl, out var breakouts);
            var freeAt = new Dictionary<ushort, List<ushort>>();
            var pocketFreeAt = new Dictionary<ushort, List<ushort>>();
            foreach (ushort id in idle)
            {
                ushort at = gameState.Units.GetUnit(id).provinceID;
                var into = pocketOf.ContainsKey(at) ? pocketFreeAt : freeAt;
                if (!into.TryGetValue(at, out var list)) into[at] = list = new List<ushort>();
                list.Add(id);
            }
            foreach (ushort b in breakouts)
                if (!load.ContainsKey(b)) // not a border province of an enemy (already in targets otherwise)
                {
                    load[b] = 0;
                    attackersOf[b] = new List<Hoi4Division>();
                    var defenders = new List<Hoi4Division>();
                    foreach (ushort e in gameState.Units.GetUnitsInProvince(b))
                    {
                        var ed = Hoi4Divisions.Instance.Get(e);
                        if (ed != null && enemies.Contains(gameState.Units.GetUnit(e).countryID)) defenders.Add(ed);
                    }
                    defendersOf[b] = defenders;
                }
            var launched = new HashSet<ushort>();

            // Target first: least-loaded, least-defended targets first. Each gathers the free divisions around
            // it, nearest first, until the odds are good (or the width is full); if the whole group falls short
            // the target is turned down once for all of them, and they stay free for the next target. (Deciding
            // per division made every free division test the odds at every border province: 55 x 27 in a day.)
            // The main effort's targets first, the cheapest route on to the enemy's victory points first
            // (Hoi4BattlePlans.TryRouteCost: terrain, defenders, rivers): an axis, not whatever gap is least held;
            // then the other fronts' targets as before
            var plansAI = Hoi4BattlePlans.Instance;
            ushort main = plansAI != null ? plansAI.MainEffort(country) : (ushort)0;
            FP AxisRank(ushort p) => main != 0 && plansAI.TryRouteCost(main, p, out FP c) ? c : FP.MaxValue;
            var order = new List<ushort>(targets);
            foreach (ushort b in breakouts) if (!order.Contains(b)) order.Add(b);
            order.Sort((a, b) =>
            {
                bool ba = breakouts.Contains(a), bb = breakouts.Contains(b);
                if (ba != bb) return ba ? -1 : 1; // breakouts first

                bool ma = ctrl[a] == main, mb = ctrl[b] == main;
                if (ma != mb) return ma ? -1 : 1;
                if (ma && AxisRank(a) != AxisRank(b)) return AxisRank(a).CompareTo(AxisRank(b));
                return load[a] != load[b] ? load[a].CompareTo(load[b])
                    : defendersOf[a].Count != defendersOf[b].Count ? defendersOf[a].Count.CompareTo(defendersOf[b].Count)
                    : a.CompareTo(b);
            });
            int oddsChecks = 0, turnedDown = 0, fullTargets = 0, noPath = 0, attacks = 0;
            FP bestRejected = FP.Zero;
            foreach (ushort t in order)
            {
                // Attacking only where the country's war AI attacks (the main effort, with the divisions for it);
                // elsewhere it holds and only flattens bulges at crushing odds (below)
                bool onMain = plansAI != null && plansAI.IsAttackFront(country, t);
                bool breakout = breakouts.Contains(t);
                var candidates = Nearby(t, ctrl, enemies, breakout ? pocketFreeAt : freeAt, launched, Reach);
                if (candidates.Count == 0) continue;
                FP width = Hoi4.Map.Hoi4CombatTerrain.Width(gameState, t);
                FP committed = FP.Zero;
                foreach (var a in attackersOf[t]) committed += a.CombatWidth;
                // The battle there is already as wide as the ground allows: more would only wait in reserve
                var side = new List<Hoi4Division>(attackersOf[t]);
                var group = new List<(ushort id, Hoi4Division d, List<ushort> path)>();
                FP odds = FP.Zero;
                bool full = false;
                foreach (var (id, hops) in candidates)
                {
                    var d = Hoi4Divisions.Instance.Get(id);
                    // the front line, then reserves behind it while the odds aren't good yet (they step in as
                    // the front breaks: a narrow front is ground down rather than never attacked)
                    if (side.Count > 0 && committed + d.CombatWidth > width * ReserveDepth) { full = true; continue; }
                    var unit = gameState.Units.GetUnit(id);
                    if (hops != 1) { noPath++; continue; }
                    var path = new List<ushort> { unit.provinceID, t }; // straight across the line
                    side.Add(d);
                    committed += d.CombatWidth;
                    group.Add((id, d, path));
                    oddsChecks++;
                    odds = Hoi4Combat.Odds(side, defendersOf[t], t);
                    if (odds >= GoodOdds) break;
                }
                if (group.Count == 0) { if (full) fullTargets++; continue; }
                // Short of good odds: now and then an uneven fight, never a hopeless one
                bool bold = odds < GoodOdds;
                // Holding fronts: only a bulge into our line (the target borders at least two of our provinces) at
                // crushing odds (StrongOdds); a front that takes every good chance never settles. A province nobody
                // defends is walked into wherever it lies: the bulge rule left empty land untaken where it touched
                // our line with one province only (the foot of Italy, the rest of Greece)
                bool undefended = defendersOf[t].Count == 0;
                if (!onMain && !breakout && !undefended && (odds < StrongOdds || OwnNeighbours(t, ctrl, country) < 2))
                {
                    turnedDown++;
                    if (odds > bestRejected) bestRejected = odds;
                    continue;
                }
                // Uneven fights only on the main effort. A breakout takes any fight that isn't hopeless: holding a
                // pocket only waits for its end
                if (bold && (breakout ? odds < BoldOdds
                                      : !onMain && main != 0 || odds < BoldOdds || Roll(country, t, today) >= BoldChance))
                {
                    turnedDown++;
                    if (odds > bestRejected) bestRejected = odds;
                    continue;
                }
                for (int i = 0; i < group.Count; i++)
                {
                    var (id, d, path) = group[i];
                    var unit = gameState.Units.GetUnit(id);
                    Core.Recording.SessionRecorder.Note("ai", breakout ? "breakout" : bold ? "attack_bold" : "attack", country, id, unit.provinceID, t,
                        $"odds {OddsText(odds)} with {attackersOf[t].Count} already going, {defendersOf[t].Count} defending, " +
                        $"path {path.Count - 1} hops, group of {group.Count}");
                    var type = unitTypes?.GetUnitType(unit.unitTypeID);
                    var command = new QueueUnitMovementCommand
                    {
                        UnitId = id,
                        Path = new List<ushort>(path),
                        MovementDays = type?.Speed ?? 3,
                        CountryId = country,
                    };
                    if (Hoi4BattlePlans.Instance != null) // off its front line while it attacks
                        gameState.TryExecuteCommand(new BorrowForAttackCommand { CountryId = country, UnitId = id });
                    if (gameState.TryExecuteCommand(command, out _))
                    {
                        load[t]++;
                        attackersOf[t].Add(d);
                        launched.Add(id);
                        attacks++;
                    }
                }
            }
            int holds = idle.Count - launched.Count;
            if (holds > 0) // one note per country (one per division made the recording heavy in big wars)
                Core.Recording.SessionRecorder.Note("ai", "hold", country, 0, 0, target,
                    $"{holds} of {idle.Count} free divisions hold: {turnedDown} targets turned down on odds (best {OddsText(bestRejected)}), " +
                    $"{fullTargets} at full width, {pocketOf.Count} provinces encircled, {breakouts.Count} breakout targets; " +
                    $"{oddsChecks} odds checks for {targets.Count} targets");
        }

        /// <summary>
        /// Encirclement: the country's land (it controls) in connected pieces; the piece with its capital (else
        /// the largest, ties the lowest province) is the main body, every other piece surrounded by enemies (no
        /// coast, no neutral or allied neighbour) a pocket. Returns each
        /// pocket province's piece; breakouts: per pocket, the enemy province next to it nearest the main body
        /// by land hops through any passable land (ties the lowest id), or none when nothing leads there (an
        /// island). Pockets without an enemy next to them (a colony) aren't returned.
        /// </summary>
        Dictionary<ushort, int> Pockets(ushort country, ushort[] ctrl, out HashSet<ushort> breakouts)
        {
            var pocketOf = new Dictionary<ushort, int>();
            breakouts = new HashSet<ushort>();
            var diplomacy = gameState.Diplomacy;
            bool Own(ushort p) => p < ctrl.Length && ctrl[p] == country && !Hoi4Regions.IsImpassable(p);
            var piece = new Dictionary<ushort, int>();
            var pieces = new List<List<ushort>>();
            foreach (ushort s in landProvinces)
            {
                if (!Own(s) || piece.ContainsKey(s)) continue;
                var list = new List<ushort> { s };
                piece[s] = pieces.Count;
                for (int i = 0; i < list.Count; i++)
                    foreach (ushort n in landNeighbours[list[i]])
                        if (Own(n) && !piece.ContainsKey(n)) { piece[n] = pieces.Count; list.Add(n); }
                pieces.Add(list);
            }
            if (pieces.Count < 2) return pocketOf;
            ushort capital = Hoi4Regions.Capital(gameState.Countries.GetCountryTag(country));
            int main = piece.TryGetValue(capital, out int cp) ? cp : -1;
            if (main < 0)
                for (int i = 0; i < pieces.Count; i++)
                    if (main < 0 || pieces[i].Count > pieces[main].Count) main = i;

            // Pockets: pieces surrounded by enemies. A piece on the coast (supplied by sea, like held land across
            // the sea from the main body) or next to land of a country we aren't at war with isn't encircled, and
            // its divisions attack with the line as usual
            var pocketPieces = new List<int>();
            for (int i = 0; i < pieces.Count; i++)
            {
                if (i == main) continue;
                bool contact = false, open = false;
                foreach (ushort p in pieces[i])
                {
                    if (coastal.Contains(p)) { open = true; break; }
                    foreach (ushort n in landNeighbours[p])
                    {
                        if (Hoi4Regions.IsImpassable(n)) continue;
                        ushort c = n < ctrl.Length ? ctrl[n] : (ushort)0;
                        if (c == 0 || c == country) continue;
                        if (diplomacy.IsAtWar(country, c)) contact = true;
                        else { open = true; break; }
                    }
                    if (open) break;
                }
                if (!contact || open) continue;
                pocketPieces.Add(i);
                foreach (ushort p in pieces[i]) pocketOf[p] = i;
            }
            if (pocketPieces.Count == 0) return pocketOf;

            // Hops to the main body through any passable land
            var dist = new Dictionary<ushort, int>();
            var queue = new List<ushort>(pieces[main]);
            queue.Sort();
            foreach (ushort p in queue) dist[p] = 0;
            for (int i = 0; i < queue.Count; i++)
            {
                ushort p = queue[i];
                int d = dist[p];
                foreach (ushort n in landNeighbours[p])
                    if (!Hoi4Regions.IsImpassable(n) && !dist.ContainsKey(n)) { dist[n] = d + 1; queue.Add(n); }
            }
            foreach (int i in pocketPieces)
            {
                ushort best = 0;
                int bestD = int.MaxValue;
                foreach (ushort p in pieces[i])
                    foreach (ushort n in landNeighbours[p])
                    {
                        ushort c = n < ctrl.Length ? ctrl[n] : (ushort)0;
                        if (c == 0 || c == country || !diplomacy.IsAtWar(country, c) || Hoi4Regions.IsImpassable(n)) continue;
                        if (!dist.TryGetValue(n, out int d)) continue;
                        if (d < bestD || (d == bestD && n < best)) { best = n; bestD = d; }
                    }
                if (best != 0) breakouts.Add(best);
            }
            return pocketOf;
        }

        /// <summary>
        /// Free (not yet launched) divisions within reach (land hops) of a target, nearest first (hops, then
        /// province id, then division id). The search goes through land no enemy holds (own, allied, neutral
        /// with access is left to the path search), so it gathers the divisions behind our side of the border.
        /// </summary>
        List<(ushort id, int hops)> Nearby(ushort target, ushort[] ctrl, HashSet<ushort> enemies,
                                            Dictionary<ushort, List<ushort>> freeAt, HashSet<ushort> launched, int reach)
        {
            var result = new List<(ushort, int)>();
            var seen = new HashSet<ushort> { target };
            var ring = new List<ushort> { target };
            for (int hop = 1; hop <= reach && ring.Count > 0; hop++)
            {
                var next = new List<ushort>();
                foreach (ushort p in ring)
                {
                    if (!landNeighbours.TryGetValue(p, out var ns)) continue;
                    foreach (ushort n in ns)
                    {
                        ushort c = n < ctrl.Length ? ctrl[n] : (ushort)0;
                        if (c != 0 && !enemies.Contains(c) && seen.Add(n)) next.Add(n);
                    }
                }
                next.Sort();
                foreach (ushort p in next)
                    if (freeAt.TryGetValue(p, out var ids))
                        foreach (ushort id in ids)
                            if (!launched.Contains(id)) result.Add((id, hop));
                ring = next;
            }
            return result;
        }
    }
}
