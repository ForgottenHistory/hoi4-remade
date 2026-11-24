using System.Collections.Generic;
using System.IO;
using Core;
using Core.Modding;
using Hoi4.Map;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: how long a division takes to move into the next province (the engine's UnitMovementQueue.LegTicks,
    /// ticks are hours). HOI4: the distance between the province centres (map px x MAP_SCALE_PIXEL_TO_KM 7.114)
    /// times the destination's terrain movement_cost, over the division's speed (its slowest battalion, km/h)
    /// with its modifiers, each a factor: the division's own movement modifier in the destination's terrain
    /// (light tanks -40% in forest), a river between the provinces (RIVER_CROSSING_SPEED_PENALTY -25% small, -50%
    /// large, plus its river movement modifier: engineers +25%) and the destination state's infrastructure (-5%
    /// per level below 5, INFRASTRUCTURE_MOVEMENT_SPEED_IMPACT) and, moving into enemy land below 30% organisation,
    /// down to -80% at none (LOW_ORG_FOR_ATTACK, ZERO_ORG_MOVEMENT_MODIFIER). Checked against daily division snapshots of a real
    /// 1936 game: infantry marching unopposed covers about 4 km of distance x cost an hour.
    /// At least an hour; at most MaxSpeedFactor slower than the base (modifiers never stop a division).
    /// Centres come from map/hoi4_province_centres.csv (Tools/hoi4_convert.py); without it, or for a unit that
    /// isn't a division, the caller's time stands.
    /// </summary>
    public static class Hoi4Movement
    {
        const string CentresFile = "map/hoi4_province_centres.csv";
        static readonly FP KmPerPixel = FP.FromFraction(7114, 1000);            // MAP_SCALE_PIXEL_TO_KM
        static readonly FP SmallRiver = FP.FromFraction(-25, 100);              // RIVER_CROSSING_SPEED_PENALTY
        static readonly FP LargeRiver = FP.FromFraction(-50, 100);              // RIVER_CROSSING_SPEED_PENALTY_LARGE
        static readonly FP InfrastructurePerLevel = FP.FromFraction(-5, 100);   // INFRASTRUCTURE_MOVEMENT_SPEED_IMPACT
        static readonly FP MinFactor = FP.FromFraction(1, 10);                  // speed never below 10% of the base
        static readonly FP LowOrgForAttack = FP.FromFraction(3, 10);            // LOW_ORG_FOR_ATTACK
        static readonly FP ZeroOrgMovement = FP.FromFraction(-8, 10);           // ZERO_ORG_MOVEMENT_MODIFIER

        static readonly Dictionary<ushort, (int x, int y)> centres = new Dictionary<ushort, (int, int)>();
        static bool loaded;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetCache()
        {
            loaded = false;
            centres.Clear();
        }

        /// <summary>Hours for the division to move from one province into the next; 0 when unknown.</summary>
        public static int LegHours(GameState gameState, ushort unitId, ushort from, ushort to)
        {
            var division = Hoi4Divisions.Instance?.Get(unitId);
            if (division == null || !TryDistanceKm(from, to, out FP km)) return 0;
            string terrain = Hoi4CombatTerrain.TerrainKey(gameState, to);
            FP speed = division.SpeedKmh > FP.Zero ? division.SpeedKmh : FP.FromInt(4);
            FP factor = FP.Max(MinFactor, FP.One + division.TerrainModifier(terrain).Movement);
            int river = Hoi4CombatTerrain.River(gameState, from, to);
            if (river > 0)
                factor *= FP.Max(MinFactor, FP.One + (river == 2 ? LargeRiver : SmallRiver) + division.TerrainModifier("river").Movement);
            int state = Hoi4Regions.State(to);
            if (state > 0)
            {
                int missing = Hoi4.Construction.Hoi4Construction.MaxInfrastructure - Hoi4.Construction.Hoi4Buildings.Level(state, "infrastructure");
                if (missing > 0) factor *= FP.Max(MinFactor, FP.One + InfrastructurePerLevel * missing);
            }
            // Into enemy land with little organisation left: slower, down to -80% at none (below 30%)
            ushort mover = gameState.Units.HasUnit(unitId) ? gameState.Units.GetUnit(unitId).countryID : (ushort)0;
            ushort holder = Hoi4Occupation.ControllerOf(gameState, to);
            if (mover != 0 && holder != 0 && holder != mover && gameState.Diplomacy.IsAtWar(mover, holder) && division.MaxOrganisation > FP.Zero)
            {
                FP share = division.Organisation / division.MaxOrganisation;
                if (share < LowOrgForAttack)
                    factor *= FP.Max(MinFactor, FP.One + ZeroOrgMovement * (FP.One - share / LowOrgForAttack));
            }
            factor *= Hoi4.Supply.Hoi4Supply.SpeedFactor(unitId); // low supply: down to -80%
            factor = FP.Max(MinFactor, factor);
            FP hours = km * Hoi4CombatTerrain.MovementCost(gameState, to) / (speed * factor);
            return System.Math.Max(1, FP.Ceiling(hours).ToInt());
        }

        /// <summary>Km between two provinces' centres.</summary>
        public static bool TryDistanceKm(ushort a, ushort b, out FP km)
        {
            Load();
            km = FP.Zero;
            if (!centres.TryGetValue(a, out var p) || !centres.TryGetValue(b, out var q)) return false;
            long dx = q.x - p.x, dy = q.y - p.y;
            km = FP.Sqrt(FP.FromInt((int)(dx * dx + dy * dy))) * KmPerPixel;
            return true;
        }

        static void Load()
        {
            if (loaded || !DataFileResolver.IsInitialized) return;
            loaded = true;
            string path = DataFileResolver.Resolve(CentresFile);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4Movement: no {CentresFile}; divisions move at the unit type's speed (rerun Tools/hoi4_convert.py)", "game_units");
                return;
            }
            foreach (string line in File.ReadLines(path))
            {
                string[] f = line.Split(';');
                if (f.Length < 3 || !ushort.TryParse(f[0], out ushort id) || !int.TryParse(f[1], out int x) || !int.TryParse(f[2], out int y)) continue;
                centres[id] = (x, y);
            }
            ArchonLogger.Log($"Hoi4Movement: {centres.Count} province centres", "game_units");
        }
    }
}
