using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Units;
using Hoi4.Map;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4 land battles, reduced. Like HOI4, a battle is fought across a border, not inside a
    /// province: a division ordered into a province that holds divisions of a country at war with it
    /// is held in its own province (the engine's ArrivalBlocker) and attacks from there. Every game
    /// hour the attackers of a target province fight its defenders, following the wiki's "Land battle"
    /// (as ported in the reference workspace's battle.py): attacks vs defences give hits, hits take
    /// organisation and strength; divisions beyond the combat width wait in reserve. A defender at zero
    /// organisation retreats to a friendly neighbouring province (destroyed when there is none); an
    /// attacker at zero organisation drops its order. Once the target is empty the attackers walk in.
    /// Left out: terrain, rivers, entrenchment, forts, planning, generals, supply. Occupation is Hoi4Occupation.
    /// </summary>
    public sealed class Hoi4Combat : MonoBehaviour
    {
        // Tuning: HOI4 keeps organisation in thousands internally; ours is the displayed value, and
        // these scales make an even infantry fight last a few days.
        const float OrgScale = 10f;
        const float HpScale = 3f;
        const float RecoveryPerHour = 0.01f; // of max organisation, out of battle
        const float Width = 80f;             // base combat width of a province
        const int RetreatDays = 1;

        public sealed class Battle
        {
            public ushort From, To;       // the attackers' main province and the one attacked
            public int AttackerPercent;   // attackers' share of the two sides' remaining organisation
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
            var go = new GameObject("Hoi4Combat");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Combat>();
        }

        void Awake() => Instance = this;

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
            gameState.Time.OnHourlyTick += OnHour;
            arrivedSub = gameState.EventBus.Subscribe<UnitMovementCompletedEvent>(e => OnArrived(e.UnitID, e.DestinationProvinceID));
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (gameState != null)
            {
                if (gameState.Time != null) gameState.Time.OnHourlyTick -= OnHour;
                if (gameState.Units?.MovementQueue != null && gameState.Units.MovementQueue.ArrivalBlocker == (System.Func<ushort, ushort, bool>)EnemyHolds)
                    gameState.Units.MovementQueue.ArrivalBlocker = null;
            }
            arrivedSub?.Dispose();
            Battles.Clear();
        }

        /// <summary>Engine arrival gate: a division may not enter a province held by a country at war with it.</summary>
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

        void OnHour(int hour)
        {
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
            foreach (var kv in attackersOf)
                if (kv.Value.Count > 0) Fight(kv.Key, kv.Value, diplomacy, divisions);

            // Divisions out of contact slowly regain organisation
            foreach (var d in divisions.All.Values)
                if (!fighting.Contains(d.UnitId) && !retreating.Contains(d.UnitId) && d.Organisation < d.MaxOrganisation)
                    d.Organisation = Mathf.Min(d.MaxOrganisation, d.Organisation + d.MaxOrganisation * RecoveryPerHour);

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
            if (EnemyPresent(unitId, province)) Destroy(unitId, Hoi4Divisions.Instance);
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
                if (d.Organisation <= 0f) { units.MovementQueue.CancelMovement(id); continue; } // broken: drops its order
                attackers.Add(d);
                attackerCountries.Add(u.countryID);
                origins[u.provinceID] = origins.TryGetValue(u.provinceID, out int n) ? n + 1 : 1;
            }
            if (attackers.Count == 0) return;

            // Defenders: everyone in the target at war with an attacker; the broken ones retreat first
            var defending = new List<Hoi4Division>();
            if (byProvince.TryGetValue(target, out var present))
                foreach (ushort id in new List<ushort>(present))
                {
                    var d = divisions.Get(id);
                    if (d == null) continue;
                    ushort c = units.GetUnit(id).countryID;
                    bool enemy = false;
                    foreach (ushort a in attackerCountries) if (a != c && diplomacy.IsAtWar(a, c)) { enemy = true; break; }
                    if (!enemy) continue;
                    if (d.Organisation <= 0f) { Retreat(d, target, attackerCountries, diplomacy, divisions); continue; }
                    defending.Add(d);
                }
            if (defending.Count == 0) return; // nobody left: the gate opens at the next daily tick

            foreach (var d in attackers) fighting.Add(d.UnitId);
            foreach (var d in defending) fighting.Add(d.UnitId);

            ushort from = 0; int most = 0;
            foreach (var kv in origins) if (kv.Value > most) { most = kv.Value; from = kv.Key; }

            var aFront = LineUp(attackers, d => d.SoftAttack * Frac(d));
            var dFront = LineUp(defending, d => d.Defense * Frac(d));

            Sums(aFront, out float aSoft, out float aHard, out float aDef, out float aBrk, out float aHardness, out float aArmor, out float aPierce);
            Sums(dFront, out float dSoft, out float dHard, out float dDef, out float dBrk, out float dHardness, out float dArmor, out float dPierce);
            float aPierceFactor = PierceFactor(aPierce, dArmor), dPierceFactor = PierceFactor(dPierce, aArmor);
            float aDie = aArmor > dPierce ? 3.5f : 2.5f, dDie = dArmor > aPierce ? 3.5f : 2.5f;

            // Attacks per hour, then hits: 10% while defences last, 40% beyond
            float aAttacks = (aSoft * (1f - dHardness) + aHard * dHardness) / 10f;
            float dAttacks = (dSoft * (1f - aHardness) + dHard * aHardness) / 10f;
            float onD = Hits(aAttacks, dDef / 10f) * aPierceFactor;
            float onA = Hits(dAttacks, aBrk / 10f) * dPierceFactor;

            Hurt(dFront, onD * 0.053f * dDie * OrgScale, onD * 0.06f * 1.5f * HpScale);
            Hurt(aFront, onA * 0.053f * aDie * OrgScale, onA * 0.06f * 1.5f * HpScale);

            float aOrg = 0, aMax = 0, dOrg = 0, dMax = 0;
            foreach (var d in attackers) { aOrg += d.Organisation; aMax += d.MaxOrganisation; }
            foreach (var d in defending) { dOrg += d.Organisation; dMax += d.MaxOrganisation; }
            float af = aMax > 0 ? aOrg / aMax : 0f, df = dMax > 0 ? dOrg / dMax : 0f;
            Battles.Add(new Battle
            {
                From = from,
                To = target,
                AttackerPercent = af + df > 0f ? Mathf.RoundToInt(100f * af / (af + df)) : 50,
            });

            // Broken attackers drop their order; broken defenders retreat now
            foreach (var d in attackers)
                if (d.Organisation <= 0f && d.Strength > 0f) units.MovementQueue.CancelMovement(d.UnitId);
            foreach (var d in defending)
                if (d.Organisation <= 0f && d.Strength > 0f) Retreat(d, target, attackerCountries, diplomacy, divisions);
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
            foreach (ushort p in neighbours)
            {
                bool own = Hoi4Occupation.ControllerOf(gameState, p) == country;
                if (best == 0 || (own && !bestOwn)) { best = p; bestOwn = own; }
            }

            if (best == 0) { destroyed.Add(d.UnitId); return; }
            // Retreating is a march like any other (a day), and the division is out of the fight meanwhile
            units.MovementQueue.CancelMovement(d.UnitId);
            retreating.Add(d.UnitId);
            units.MovementQueue.StartMovement(d.UnitId, best, RetreatDays);
            if (byProvince.TryGetValue(from, out var old)) old.Remove(d.UnitId);
        }

        static float Frac(Hoi4Division d) => d.MaxStrength > 0 ? Mathf.Clamp01(d.Strength / d.MaxStrength) : 1f;

        /// <summary>Divisions that fit the combat width, best first; broken ones (no organisation) stay out.</summary>
        static List<Hoi4Division> LineUp(List<Hoi4Division> members, System.Func<Hoi4Division, float> key)
        {
            var live = new List<Hoi4Division>();
            foreach (var d in members) if (d.Organisation > 0f) live.Add(d);
            live.Sort((x, y) => key(y).CompareTo(key(x)));
            var front = new List<Hoi4Division>();
            float used = 0f;
            foreach (var d in live)
            {
                if (front.Count > 0 && used + d.CombatWidth > Width) continue; // in reserve
                front.Add(d);
                used += d.CombatWidth;
            }
            return front;
        }

        static void Sums(List<Hoi4Division> front, out float soft, out float hard, out float def, out float brk,
            out float hardness, out float armor, out float pierce)
        {
            soft = hard = def = brk = hardness = pierce = 0f;
            float armorMax = 0f, armorSum = 0f;
            foreach (var d in front)
            {
                float hp = Frac(d); // damage output shrinks with strength
                soft += d.SoftAttack * hp; hard += d.HardAttack * hp;
                def += d.Defense * hp; brk += d.Breakthrough * hp;
                hardness += d.Hardness; pierce += d.Piercing;
                armorMax = Mathf.Max(armorMax, d.Armor); armorSum += d.Armor;
            }
            int n = Mathf.Max(1, front.Count);
            hardness /= n; pierce /= n;
            armor = 0.3f * armorMax + 0.7f * armorSum / n;
        }

        static float Hits(float attacks, float defences) =>
            attacks <= defences ? 0.1f * attacks : 0.1f * defences + 0.4f * (attacks - defences);

        static float PierceFactor(float piercing, float armor)
        {
            if (armor <= 0f || piercing >= armor) return 1f;
            float r = piercing / armor;
            return r >= 0.75f ? 0.8f : r >= 0.5f ? 0.65f : 0.5f;
        }

        /// <summary>Spread a side's damage evenly over its front divisions.</summary>
        void Hurt(List<Hoi4Division> front, float org, float hp)
        {
            if (front.Count == 0) return;
            float orgEach = org / front.Count, hpEach = hp / front.Count;
            foreach (var d in front)
            {
                d.Organisation = Mathf.Max(0f, d.Organisation - orgEach);
                d.Strength = Mathf.Max(0f, d.Strength - hpEach);
                if (d.Strength <= 0f) destroyed.Add(d.UnitId);
            }
        }
    }
}
