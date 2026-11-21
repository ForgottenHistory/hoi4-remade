using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Systems;
using Hoi4.Map;
using StarterKit.Commands;
using Unity.Collections;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the simplest war AI. Every game day each AI country (any country but the player's) at war
    /// picks its strongest enemy that borders it (strength = the enemy's total division strength) and
    /// spreads its idle divisions evenly over that enemy's border provinces, ordering each to attack.
    /// Divisions that are moving, worn down (organisation or strength), or defending a province under
    /// attack are left alone. A division only attacks where the estimated odds (Hoi4Combat.Odds, with the
    /// divisions already marching there) are good; now and then it takes an uneven fight, never a hopeless
    /// one. Every hour, attacks that go badly are called off before the attackers are ground down.
    /// Orders go through the same QueueUnitMovementCommand as the player's, so combat, retreat and
    /// occupation apply unchanged. Holding the line is the front lines' job (Hoi4BattlePlans makes one per
    /// enemy border): an attacker is borrowed off its line and rejoins it when the attack is over.
    /// </summary>
    public sealed class Hoi4EnemyAI : MonoBehaviour
    {
        const float MinOrganisation = 0.7f;   // of max: tired divisions rest before attacking again
        const float MinStrength = 0.5f;       // of max: battered divisions don't attack at all
        const float GoodOdds = 1.0f;          // attack when the attackers should break the defenders first
        const float BoldOdds = 0.6f;          // uneven fights: taken sometimes (BoldChance), never below this
        const float BoldChance = 0.25f;
        const float BreakOffOrganisation = 0.3f; // of max: an attacker this worn down breaks off
        const float BreakOffStrength = 0.35f;
        const int BreakOffPercent = 25;       // or when its side holds less than this share of the battle's organisation

        GameState gameState;
        ushort[] provinceIds;
        readonly List<ushort> idle = new List<ushort>();
        readonly Dictionary<ushort, int> load = new Dictionary<ushort, int>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            var go = new GameObject("Hoi4EnemyAI");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4EnemyAI>();
        }

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
            gameState.Time.OnDailyTick += OnDay;
            gameState.Time.OnHourlyTick += OnHour;
        }

        void OnDestroy()
        {
            if (gameState?.Time != null)
            {
                gameState.Time.OnDailyTick -= OnDay;
                gameState.Time.OnHourlyTick -= OnHour;
            }
        }

        /// <summary>Call off attacks that go badly: worn-down attackers, or a battle that is clearly lost.</summary>
        void OnHour(int hour)
        {
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
                bool worn = d.Organisation < d.MaxOrganisation * BreakOffOrganisation || d.Strength < d.MaxStrength * BreakOffStrength;
                if (worn || battle.AttackerPercent < BreakOffPercent) breakOff.Add(id);
            }
            foreach (ushort id in breakOff) queue.CancelMovement(id); // not while iterating the moving units
        }

        /// <summary>Deterministic "dice" per country, province and day (same game, same choices).</summary>
        static float Roll(ushort country, ushort province, int day)
        {
            uint h = (uint)country * 73856093u ^ (uint)province * 19349663u ^ (uint)day * 83492791u;
            h ^= h >> 13; h *= 0x5bd1e995u; h ^= h >> 15;
            return (h & 0xFFFF) / 65535f;
        }

        static ushort PlayerCountry
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        void OnDay(int day)
        {
            ushort player = PlayerCountry;
            var diplomacy = gameState.Diplomacy;
            var countries = new HashSet<ushort>();
            foreach (ushort id in gameState.Units.GetAllUnits())
                countries.Add(gameState.Units.GetUnit(id).countryID);
            foreach (ushort country in countries)
                if (country != player && country != 0 && diplomacy.IsAtWar(country))
                    Attack(country);
        }

        float Strength(ushort country)
        {
            float total = 0f;
            foreach (ushort id in gameState.Units.GetCountryUnits(country))
            {
                var d = Hoi4Divisions.Instance.Get(id);
                if (d != null) total += d.Strength;
            }
            return total;
        }

        void Attack(ushort country)
        {
            var enemies = new HashSet<ushort>(gameState.Diplomacy.GetEnemies(country));
            if (enemies.Count == 0) return;

            // Border provinces per enemy: land the enemy controls next to land we control
            var fronts = new Dictionary<ushort, List<ushort>>();
            foreach (ushort p in provinceIds)
            {
                ushort holder = Hoi4Occupation.ControllerOf(gameState, p);
                if (!enemies.Contains(holder) || Hoi4Regions.IsWater(p) || Hoi4Regions.IsImpassable(p)) continue;
                bool border = false;
                foreach (ushort n in gameState.Adjacencies.GetNeighborsWhere(p, q => !Hoi4Regions.IsWater(q)))
                    if (Hoi4Occupation.ControllerOf(gameState, n) == country) { border = true; break; }
                if (!border || Hoi4Combat.IsAttackedFrom(gameState, country, p)) continue;
                if (!fronts.TryGetValue(holder, out var list)) fronts[holder] = list = new List<ushort>();
                list.Add(p);
            }
            if (fronts.Count == 0) return;

            ushort target = 0;
            float best = -1f;
            foreach (var kv in fronts)
            {
                float s = Strength(kv.Key);
                if (s > best) { best = s; target = kv.Key; }
            }
            var targets = fronts[target];

            // Idle divisions: not marching, rested, and not the defenders of a province under attack
            idle.Clear();
            foreach (ushort id in gameState.Units.GetCountryUnits(country))
            {
                var d = Hoi4Divisions.Instance.Get(id);
                if (d == null || d.Organisation < d.MaxOrganisation * MinOrganisation || d.Strength < d.MaxStrength * MinStrength) continue;
                if (gameState.Units.MovementQueue.IsUnitMoving(id)) continue;
                ushort at = gameState.Units.GetUnit(id).provinceID;
                bool defending = false;
                foreach (var b in Hoi4Combat.Battles) if (b.To == at) { defending = true; break; }
                if (!defending) idle.Add(id);
            }
            if (idle.Count == 0) return;

            // Divisions already marching at this enemy count towards the spread and the odds
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
            foreach (ushort id in idle)
            {
                // Least-loaded targets first; among the first few reachable ones the nearest wins a tie
                var unit = gameState.Units.GetUnit(id);
                List<ushort> bestPath = null;
                ushort bestTarget = 0;
                int bestLoad = int.MaxValue;
                var order = new List<ushort>(targets);
                order.Sort((a, b) => load[a].CompareTo(load[b]));
                int tried = 0;
                var self = Hoi4Divisions.Instance.Get(id);
                foreach (ushort t in order)
                {
                    if (tried >= 4 || (bestPath != null && load[t] > bestLoad)) break;
                    // Odds with this division joining the ones already on the way
                    var side = new List<Hoi4Division>(attackersOf[t]) { self };
                    float odds = Hoi4Combat.Odds(side, defendersOf[t]);
                    if (odds < GoodOdds && (odds < BoldOdds || Roll(country, t, today) >= BoldChance)) continue;
                    tried++;
                    var path = gameState.Pathfinding.FindPath(unit.provinceID, t, country);
                    if (path == null || path.Count < 2) continue;
                    if (bestPath == null || load[t] < bestLoad || (load[t] == bestLoad && path.Count < bestPath.Count))
                    {
                        bestPath = path;
                        bestTarget = t;
                        bestLoad = load[t];
                    }
                }
                if (bestPath == null) continue;

                var type = unitTypes?.GetUnitType(unit.unitTypeID);
                var command = new QueueUnitMovementCommand
                {
                    UnitId = id,
                    Path = new List<ushort>(bestPath),
                    MovementDays = type?.Speed ?? 3,
                    CountryId = country,
                };
                Hoi4BattlePlans.Instance?.Borrow(id); // off its front line while it attacks
                if (gameState.TryExecuteCommand(command, out _))
                {
                    load[bestTarget]++;
                    attackersOf[bestTarget].Add(self);
                }
            }
        }
    }
}
