using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Units;
using Hoi4.Map;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4 land battles, reduced. Like HOI4, a battle is fought across a border, not inside a
    /// province: a division ordered into a province that holds divisions of a country at war with it
    /// is held in its own province (the engine's ArrivalBlocker) and attacks from there. Every game
    /// hour the attackers of a target province fight its defenders, following the HOI4 wiki's "Land battle"
    /// page: attacks vs defences give hits, hits take
    /// organisation and strength; divisions beyond the combat width wait in reserve. A defender at zero
    /// organisation retreats to a friendly neighbouring province (destroyed when there is none); an
    /// attacker at zero organisation drops its order. Once the target is empty the attackers walk in.
    /// Left out: terrain, rivers, entrenchment, forts, planning, generals, supply. Occupation is Hoi4Occupation.
    /// All maths is FixedPoint64 and every loop runs in id order, so a battle ends the same on every machine.
    /// </summary>
    public sealed class Hoi4Combat : MonoBehaviour
    {
        // Tuning: HOI4 keeps organisation in thousands internally; ours is the displayed value, and
        // these scales make an even infantry fight last a few days.
        static readonly FP OrgScale = FP.FromInt(10);
        static readonly FP HpScale = FP.FromInt(3);
        static readonly FP RecoveryPerHour = FP.FromFraction(1, 100); // of max organisation, out of battle
        static readonly FP Width = FP.FromInt(80);                    // base combat width of a province
        static readonly FP OrgPerHit = FP.FromFraction(53, 1000);     // wiki: 0.053 org per hit, times the die
        static readonly FP HpPerHit = FP.FromFraction(9, 100);        // wiki: 0.06 hp per hit, times a 1.5 die
        static readonly FP ArmouredDie = FP.FromFraction(7, 2), Die = FP.FromFraction(5, 2);
        static readonly FP HitsWhileDefended = FP.FromFraction(1, 10), HitsBeyond = FP.FromFraction(4, 10);
        static readonly FP MinRate = FP.FromFraction(1, 10000);
        const int RetreatDays = 1;

        public sealed class Battle
        {
            public ushort From, To;       // the attackers' main province and the one attacked
            public int AttackerPercent;   // attackers' share of the two sides' remaining organisation
            public List<ushort> Attackers, Defenders; // everyone in it, front line and reserve (recorder)
            public List<ushort> AttackerFront, DefenderFront; // the ones within the combat width
        }

        public static Hoi4Combat Instance { get; private set; }

        /// <summary>Battles going this hour (counters draw a marker on the border between From and To).</summary>
        public static readonly List<Battle> Battles = new List<Battle>();

        GameState gameState;
        readonly Dictionary<ushort, List<ushort>> byProvince = new Dictionary<ushort, List<ushort>>();
        readonly Dictionary<ushort, List<ushort>> attackersOf = new Dictionary<ushort, List<ushort>>();
        readonly HashSet<ushort> fighting = new HashSet<ushort>();
        readonly List<ushort> destroyed = new List<ushort>();
        int lastBattleCount;
        readonly HashSet<ushort> retreating = new HashSet<ushort>(); // broken divisions marching to safety
        System.IDisposable arrivedSub;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Combat");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Combat>();
        }

        void Awake() => Instance = this;

        /// <summary>A broken division marching to safety (out of the fight; don't give it other orders).</summary>
        public static bool IsRetreating(ushort unitId) => Instance != null && Instance.retreating.Contains(unitId);

        IEnumerator Start()
        {
            while (Hoi4Divisions.Instance == null || !Hoi4Divisions.Instance.IsLoaded
                   || Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Time == null
                   || Engine.ArchonEngine.Instance.GameState.Units == null
                   || Engine.ArchonEngine.Instance.GameState.Units.MovementQueue == null)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            gameState.Units.MovementQueue.TicksPerDay = 24; // HOI4 divisions advance every hour
            gameState.Units.MovementQueue.ArrivalBlocker = EnemyHolds;
            arrivedSub = gameState.EventBus.Subscribe<UnitMovementCompletedEvent>(e => OnArrived(e.UnitID, e.DestinationProvinceID));
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (gameState != null)
            {
                if (gameState.Units?.MovementQueue != null && gameState.Units.MovementQueue.ArrivalBlocker == (System.Func<ushort, ushort, bool>)EnemyHolds)
                    gameState.Units.MovementQueue.ArrivalBlocker = null;
            }
            arrivedSub?.Dispose();
            Battles.Clear();
        }

        /// <summary>Engine arrival gate: a division may not enter a province held by a country at war with it.</summary>
        /// <summary>
        /// True while enemies in this province are attacking a province of this country: it can't be
        /// attacked back, the way HOI4 doesn't let you attack the province you are being attacked from.
        /// </summary>
        public static bool IsAttackedFrom(Core.GameState gameState, ushort country, ushort province)
        {
            foreach (var b in Battles)
                if (b.From == province && Hoi4Occupation.ControllerOf(gameState, b.To) == country) return true;
            return false;
        }

        /// <summary>True while this division's move is an attack: its target province holds an enemy.</summary>
        public bool IsAttacking(ushort unitId)
        {
            if (gameState == null || retreating.Contains(unitId)) return false;
            return gameState.Units.MovementQueue.TryGetMovementState(unitId, out var st) && EnemyHolds(unitId, st.destinationProvinceID);
        }

        bool EnemyHolds(ushort unitId, ushort province)
        {
            var diplomacy = gameState.Diplomacy;
            if (diplomacy == null || !gameState.Units.HasUnit(unitId) || retreating.Contains(unitId)) return false;
            ushort country = gameState.Units.GetUnit(unitId).countryID;
            foreach (ushort other in gameState.Units.GetUnitsInProvince(province))
            {
                if (retreating.Contains(other)) continue; // already leaving
                ushort c = gameState.Units.GetUnit(other).countryID;
                if (c != country && diplomacy.IsAtWar(country, c)) return true;
            }
            return false;
        }

        /// <summary>One hour of fighting; called by Hoi4Simulation.</summary>
        public void SimulateHour()
        {
            if (gameState == null) return;
            var diplomacy = gameState.Diplomacy;
            var divisions = Hoi4Divisions.Instance;
            if (diplomacy == null || divisions == null) return;
            var units = gameState.Units;

            foreach (var l in byProvince.Values) l.Clear();
            retreating.RemoveWhere(id => !units.MovementQueue.IsUnitMoving(id));
            foreach (ushort id in units.GetAllUnits())
            {
                if (retreating.Contains(id)) continue; // retreating divisions take no part
                ushort p = units.GetUnit(id).provinceID;
                if (!byProvince.TryGetValue(p, out var list)) byProvince[p] = list = new List<ushort>();
                list.Add(id);
            }

            // Attackers: every moving division whose next province holds an enemy (the attack starts with the move, and the gate holds it at the border if the march ends first), grouped by the province they attack
            foreach (var l in attackersOf.Values) l.Clear();
            foreach (ushort id in new List<ushort>(units.MovementQueue.GetMovingUnits()))
            {
                if (divisions.Get(id) == null || retreating.Contains(id)) continue;
                if (!units.MovementQueue.TryGetMovementState(id, out var st) || !EnemyHolds(id, st.destinationProvinceID)) continue;
                if (!attackersOf.TryGetValue(st.destinationProvinceID, out var list))
                    attackersOf[st.destinationProvinceID] = list = new List<ushort>();
                list.Add(id);
            }

            Battles.Clear();
            fighting.Clear();
            destroyed.Clear();
            // Battles in province order: a division can take part in one and be destroyed in another
            var targets = new List<ushort>();
            foreach (var kv in attackersOf) if (kv.Value.Count > 0) targets.Add(kv.Key);
            targets.Sort();
            foreach (ushort target in targets)
            {
                attackersOf[target].Sort();
                Fight(target, attackersOf[target], diplomacy, divisions);
            }

            // Divisions out of contact slowly regain organisation
            foreach (var d in divisions.All.Values)
                if (!fighting.Contains(d.UnitId) && !retreating.Contains(d.UnitId) && d.Organisation < d.MaxOrganisation)
                    d.Organisation = FP.Min(d.MaxOrganisation, d.Organisation + d.MaxOrganisation * RecoveryPerHour);

            foreach (ushort id in destroyed)
                Destroy(id, divisions);

            if (destroyed.Count > 0 || Battles.Count > 0 || lastBattleCount > 0)
                Hoi4UnitCounters.Instance?.MarkDirty(); // bars move every hour in battle
            Hoi4MoveArrows.Instance?.MarkDirty();
            lastBattleCount = Battles.Count;
        }

        /// <summary>A retreating division that arrives where an enemy stands is overrun: gone at once.</summary>
        void OnArrived(ushort unitId, ushort province)
        {
            if (!retreating.Remove(unitId) || Hoi4Divisions.Instance == null) return;
            if (!EnemyPresent(unitId, province)) return;
            Core.Recording.SessionRecorder.Note("combat", "overrun", gameState.Units.GetUnit(unitId).countryID, unitId, province, 0, "retreated into an enemy");
            Destroy(unitId, Hoi4Divisions.Instance);
        }

        bool EnemyPresent(ushort unitId, ushort province)
        {
            var diplomacy = gameState.Diplomacy;
            if (diplomacy == null || !gameState.Units.HasUnit(unitId)) return false;
            ushort country = gameState.Units.GetUnit(unitId).countryID;
            foreach (ushort other in gameState.Units.GetUnitsInProvince(province))
            {
                if (other == unitId || retreating.Contains(other)) continue;
                ushort c = gameState.Units.GetUnit(other).countryID;
                if (c != country && diplomacy.IsAtWar(country, c)) return true;
            }
            return false;
        }

        void Destroy(ushort id, Hoi4Divisions divisions)
        {
            if (!gameState.Units.HasUnit(id)) return;
            var u = gameState.Units.GetUnit(id);
            Core.Recording.SessionRecorder.Note("combat", "destroyed", u.countryID, id, u.provinceID, 0, "");
            divisions.Remove(id);
            gameState.Units.DisbandUnit(id, DestructionReason.Combat);
        }

        void Fight(ushort target, List<ushort> attackerIds, Core.Diplomacy.DiplomacySystem diplomacy, Hoi4Divisions divisions)
        {
            var units = gameState.Units;
            var attackers = new List<Hoi4Division>();
            var attackerCountries = new HashSet<ushort>();
            var origins = new Dictionary<ushort, int>();
            foreach (ushort id in attackerIds)
            {
                var d = divisions.Get(id);
                var u = units.GetUnit(id);
                if (d.Organisation <= FP.Zero) { units.MovementQueue.CancelMovement(id); continue; } // broken: drops its order
                attackers.Add(d);
                attackerCountries.Add(u.countryID);
                origins[u.provinceID] = origins.TryGetValue(u.provinceID, out int n) ? n + 1 : 1;
            }
            if (attackers.Count == 0) return;

            // Defenders: everyone in the target at war with an attacker; the broken ones retreat first
            var defending = new List<Hoi4Division>();
            if (byProvince.TryGetValue(target, out var present))
                foreach (ushort id in Sorted(present))
                {
                    var d = divisions.Get(id);
                    if (d == null) continue;
                    ushort c = units.GetUnit(id).countryID;
                    bool enemy = false;
                    foreach (ushort a in attackerCountries) if (a != c && diplomacy.IsAtWar(a, c)) { enemy = true; break; }
                    if (!enemy) continue;
                    if (d.Organisation <= FP.Zero) { Retreat(d, target, attackerCountries, diplomacy, divisions); continue; }
                    defending.Add(d);
                }
            if (defending.Count == 0) return; // nobody left: the gate opens at the next daily tick

            foreach (var d in attackers) fighting.Add(d.UnitId);
            foreach (var d in defending) fighting.Add(d.UnitId);

            ushort from = 0; int most = 0;
            foreach (var kv in origins) if (kv.Value > most || (kv.Value == most && kv.Key < from)) { most = kv.Value; from = kv.Key; }

            var aFront = LineUp(attackers, d => d.SoftAttack * Frac(d));
            var dFront = LineUp(defending, d => d.Defense * Frac(d));

            Sums(aFront, out FP aSoft, out FP aHard, out FP aDef, out FP aBrk, out FP aHardness, out FP aArmor, out FP aPierce);
            Sums(dFront, out FP dSoft, out FP dHard, out FP dDef, out FP dBrk, out FP dHardness, out FP dArmor, out FP dPierce);
            FP aPierceFactor = PierceFactor(aPierce, dArmor), dPierceFactor = PierceFactor(dPierce, aArmor);
            FP aDie = aArmor > dPierce ? ArmouredDie : Die, dDie = dArmor > aPierce ? ArmouredDie : Die;

            // Attacks per hour, then hits: 10% while defences last, 40% beyond
            FP aAttacks = (aSoft * (FP.One - dHardness) + aHard * dHardness) / 10;
            FP dAttacks = (dSoft * (FP.One - aHardness) + dHard * aHardness) / 10;
            FP onD = Hits(aAttacks, dDef / 10) * aPierceFactor;
            FP onA = Hits(dAttacks, aBrk / 10) * dPierceFactor;

            Hurt(dFront, onD * OrgPerHit * dDie * OrgScale, onD * HpPerHit * HpScale);
            Hurt(aFront, onA * OrgPerHit * aDie * OrgScale, onA * HpPerHit * HpScale);

            FP aOrg = FP.Zero, aMax = FP.Zero, dOrg = FP.Zero, dMax = FP.Zero;
            foreach (var d in attackers) { aOrg += d.Organisation; aMax += d.MaxOrganisation; }
            foreach (var d in defending) { dOrg += d.Organisation; dMax += d.MaxOrganisation; }
            FP af = aMax > FP.Zero ? aOrg / aMax : FP.Zero, df = dMax > FP.Zero ? dOrg / dMax : FP.Zero;
            Battles.Add(new Battle
            {
                From = from,
                To = target,
                AttackerPercent = af + df > FP.Zero ? FP.Round(FP.Hundred * af / (af + df)).ToInt() : 50,
                Attackers = attackers.ConvertAll(d => d.UnitId),
                Defenders = defending.ConvertAll(d => d.UnitId),
                AttackerFront = aFront.ConvertAll(d => d.UnitId),
                DefenderFront = dFront.ConvertAll(d => d.UnitId),
            });

            // Broken attackers drop their order; broken defenders retreat now
            foreach (var d in attackers)
                if (d.Organisation <= FP.Zero && d.Strength > FP.Zero) units.MovementQueue.CancelMovement(d.UnitId);
            foreach (var d in defending)
                if (d.Organisation <= FP.Zero && d.Strength > FP.Zero) Retreat(d, target, attackerCountries, diplomacy, divisions);
        }

        /// <summary>Move a broken division to a friendly neighbouring province free of enemies; destroyed when there is none.</summary>
        void Retreat(Hoi4Division d, ushort from, HashSet<ushort> enemies, Core.Diplomacy.DiplomacySystem diplomacy, Hoi4Divisions divisions)
        {
            var units = gameState.Units;
            if (!units.HasUnit(d.UnitId)) return;
            ushort country = units.GetUnit(d.UnitId).countryID;
            ushort best = 0;
            bool bestOwn = false;
            var neighbours = gameState.Adjacencies.GetNeighborsWhere(from, p =>
            {
                if (Hoi4Regions.IsWater(p) || Hoi4Regions.IsImpassable(p)) return false;
                ushort owner = Hoi4Occupation.ControllerOf(gameState, p); // occupied ground is not friendly
                if (owner != country && !diplomacy.AreAllied(country, owner)) return false;
                if (byProvince.TryGetValue(p, out var list))
                    foreach (ushort other in list)
                    {
                        ushort c = units.GetUnit(other).countryID;
                        if (c != country && diplomacy.IsAtWar(country, c)) return false;
                    }
                return true;
            });
            neighbours.Sort(); // the engine's adjacency map has no fixed order
            foreach (ushort p in neighbours)
            {
                bool own = Hoi4Occupation.ControllerOf(gameState, p) == country;
                if (best == 0 || (own && !bestOwn)) { best = p; bestOwn = own; }
            }

            if (best == 0)
            {
                Core.Recording.SessionRecorder.Note("combat", "trapped", country, d.UnitId, from, 0, "no friendly neighbour to retreat to: destroyed");
                destroyed.Add(d.UnitId);
                return;
            }
            Core.Recording.SessionRecorder.Note("combat", "retreat", country, d.UnitId, from, best,
                $"org {d.Organisation.ToFloat():0.#}/{d.MaxOrganisation.ToFloat():0.#} str {d.Strength.ToFloat():0.#}/{d.MaxStrength.ToFloat():0.#}");
            // Retreating is a march like any other (a day), and the division is out of the fight meanwhile
            units.MovementQueue.CancelMovement(d.UnitId);
            retreating.Add(d.UnitId);
            units.MovementQueue.StartMovement(d.UnitId, best, RetreatDays);
            if (byProvince.TryGetValue(from, out var old)) old.Remove(d.UnitId);
        }

        /// <summary>
        /// Rough odds of an attack, from the same hit formula as the battle: how fast each side wears down the
        /// other's organisation. Above 1 the attackers should break the defenders first. Ignores combat width,
        /// armour and reserves; infinite when nobody defends.
        /// </summary>
        public static FP Odds(List<Hoi4Division> attackers, List<Hoi4Division> defenders)
        {
            if (defenders.Count == 0) return FP.MaxValue;
            if (attackers.Count == 0) return FP.Zero;
            Sums(attackers, out FP aSoft, out FP aHard, out _, out FP aBrk, out _, out _, out _);
            Sums(defenders, out FP dSoft, out FP dHard, out FP dDef, out _, out _, out _, out _);
            FP aOrg = FP.Zero, dOrg = FP.Zero;
            foreach (var d in attackers) aOrg += d.Organisation;
            foreach (var d in defenders) dOrg += d.Organisation;
            FP aRate = Hits(aSoft + aHard, dDef) / FP.Max(dOrg, FP.One);  // share of the defenders' org lost per hour
            FP dRate = Hits(dSoft + dHard, aBrk) / FP.Max(aOrg, FP.One);  // share of the attackers' org lost per hour
            return aRate / FP.Max(dRate, MinRate);
        }

        static List<ushort> Sorted(List<ushort> ids)
        {
            var copy = new List<ushort>(ids);
            copy.Sort();
            return copy;
        }

        static FP Frac(Hoi4Division d) => d.MaxStrength > FP.Zero ? FP.Clamp(d.Strength / d.MaxStrength, FP.Zero, FP.One) : FP.One;

        /// <summary>Divisions that fit the combat width, best first (ties: lower id); broken ones (no organisation) stay out.</summary>
        static List<Hoi4Division> LineUp(List<Hoi4Division> members, System.Func<Hoi4Division, FP> key)
        {
            var live = new List<Hoi4Division>();
            foreach (var d in members) if (d.Organisation > FP.Zero) live.Add(d);
            live.Sort((x, y) =>
            {
                int c = key(y).CompareTo(key(x));
                return c != 0 ? c : x.UnitId.CompareTo(y.UnitId);
            });
            var front = new List<Hoi4Division>();
            FP used = FP.Zero;
            foreach (var d in live)
            {
                if (front.Count > 0 && used + d.CombatWidth > Width) continue; // in reserve
                front.Add(d);
                used += d.CombatWidth;
            }
            return front;
        }

        static readonly FP P3 = FP.FromFraction(3, 10), P7 = FP.FromFraction(7, 10);

        static void Sums(List<Hoi4Division> front, out FP soft, out FP hard, out FP def, out FP brk,
            out FP hardness, out FP armor, out FP pierce)
        {
            soft = hard = def = brk = hardness = pierce = FP.Zero;
            FP armorMax = FP.Zero, armorSum = FP.Zero;
            foreach (var d in front)
            {
                FP hp = Frac(d); // damage output shrinks with strength
                soft += d.SoftAttack * hp; hard += d.HardAttack * hp;
                def += d.Defense * hp; brk += d.Breakthrough * hp;
                hardness += d.Hardness; pierce += d.Piercing;
                armorMax = FP.Max(armorMax, d.Armor); armorSum += d.Armor;
            }
            int n = System.Math.Max(1, front.Count);
            hardness /= n; pierce /= n;
            armor = P3 * armorMax + P7 * armorSum / n;
        }

        static FP Hits(FP attacks, FP defences) =>
            attacks <= defences ? HitsWhileDefended * attacks : HitsWhileDefended * defences + HitsBeyond * (attacks - defences);

        static readonly FP Pierce80 = FP.FromFraction(8, 10), Pierce65 = FP.FromFraction(65, 100);

        static FP PierceFactor(FP piercing, FP armor)
        {
            if (armor <= FP.Zero || piercing >= armor) return FP.One;
            // piercing / armor >= 0.75, >= 0.5, without dividing
            return piercing * 4 >= armor * 3 ? Pierce80 : piercing * 2 >= armor ? Pierce65 : FP.Half;
        }

        /// <summary>Spread a side's damage evenly over its front divisions.</summary>
        void Hurt(List<Hoi4Division> front, FP org, FP hp)
        {
            if (front.Count == 0) return;
            FP orgEach = org / front.Count, hpEach = hp / front.Count;
            foreach (var d in front)
            {
                d.Organisation = FP.Max(FP.Zero, d.Organisation - orgEach);
                d.Strength = FP.Max(FP.Zero, d.Strength - hpEach);
                if (d.Strength <= FP.Zero) destroyed.Add(d.UnitId);
            }
        }
    }
}
