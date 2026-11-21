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
    /// Divisions that are moving, low on organisation, or defending a province under attack are left
    /// alone. Orders go through the same QueueUnitMovementCommand as the player's, so combat, retreat
    /// and occupation apply unchanged. No defence, reserves, front planning or retreat decisions.
    /// </summary>
    public sealed class Hoi4EnemyAI : MonoBehaviour
    {
        const float MinOrganisation = 0.5f; // of max: exhausted divisions stay put

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
        }

        void OnDestroy()
        {
            if (gameState?.Time != null) gameState.Time.OnDailyTick -= OnDay;
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
                if (d == null || d.Organisation < d.MaxOrganisation * MinOrganisation) continue;
                if (gameState.Units.MovementQueue.IsUnitMoving(id)) continue;
                ushort at = gameState.Units.GetUnit(id).provinceID;
                bool defending = false;
                foreach (var b in Hoi4Combat.Battles) if (b.To == at) { defending = true; break; }
                if (!defending) idle.Add(id);
            }
            if (idle.Count == 0) return;

            // Divisions already marching at this enemy count towards the spread
            load.Clear();
            foreach (ushort t in targets) load[t] = 0;
            foreach (ushort id in gameState.Units.GetCountryUnits(country))
                if (gameState.Units.MovementQueue.TryGetMovementState(id, out var st) && load.ContainsKey(st.destinationProvinceID))
                    load[st.destinationProvinceID]++;

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
                foreach (ushort t in order)
                {
                    if (tried++ >= 4 || (bestPath != null && load[t] > bestLoad)) break;
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
                if (gameState.TryExecuteCommand(command, out _)) load[bestTarget]++;
            }
        }
    }
}
