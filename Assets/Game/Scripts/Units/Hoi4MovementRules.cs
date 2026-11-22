using System.Collections.Generic;
using Core;
using Core.Data;
using Core.Systems;
using Hoi4.Map;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4 land movement rules for pathfinding (installed as the engine's default cost
    /// calculator by Hoi4UnitSelection).
    ///
    /// A land division may enter a province when it is not water, not in an impassable state, and
    /// its owner is the division's own country, an ally, a country at war with it, or one that grants
    /// it military access. Neutral countries block (HOI4 needs access or war to cross a border).
    /// Cost = the destination terrain's movement cost (map/terrain.json5), so paths prefer plains
    /// over mountains and marshes.
    ///
    /// Cost: A* calls CanTraverse and GetMovementCost for every province it looks at, so they are array reads.
    /// Water, impassable and terrain cost never change in HOI4 and are read once. Owners are a snapshot
    /// rebuilt when ProvinceSystem.OwnershipVersion changes; access per (country, owner) is cached until
    /// DiplomacySystem.RelationsVersion or the ownership changes. Same answers as reading them each time.
    /// </summary>
    public sealed class Hoi4MovementRules : IMovementCostCalculator
    {
        private readonly GameState gameState;
        private readonly TerrainMovementCostCalculator terrain;

        // Static per province (by id): not enterable whatever the owner (water, impassable), terrain cost
        private bool[] blocked;
        private FixedPoint64[] cost;
        // Owner per province, as of ownershipVersion
        private ushort[] owner;
        private int ownershipVersion = -1;
        // HasAccess(country, owner) by (country << 16 | owner), as of relationsVersion
        private readonly Dictionary<uint, bool> access = new Dictionary<uint, bool>();
        private int relationsVersion = -1;

        public Hoi4MovementRules(GameState gameState)
        {
            this.gameState = gameState;
            if (gameState.Registries?.Terrains != null && gameState.Registries.Terrains.Count > 0)
                terrain = new TerrainMovementCostCalculator(gameState.Provinces, gameState.Registries.Terrains);
        }

        public bool CanTraverse(ushort province, PathContext context)
        {
            EnsureStatic();
            if (province >= blocked.Length || blocked[province])
                return false;
            if (context.UnitOwnerCountryId == 0) return true; // no owner given: terrain rules only
            return HasAccess(context.UnitOwnerCountryId, OwnerOf(province));
        }

        /// <summary>May units of `country` enter land owned by `owner`?</summary>
        public bool HasAccess(ushort country, ushort owner)
        {
            if (country == 0) return true; // no owner given: terrain rules only
            if (owner == 0) return false;  // unowned land
            if (owner == country) return true;
            var diplomacy = gameState.Diplomacy;
            if (diplomacy == null) return false;
            if (relationsVersion != diplomacy.RelationsVersion)
            {
                access.Clear();
                relationsVersion = diplomacy.RelationsVersion;
            }
            uint key = (uint)country << 16 | owner;
            if (access.TryGetValue(key, out bool ok)) return ok;
            ok = diplomacy.AreAllied(country, owner) ||
                 diplomacy.IsAtWar(country, owner) ||
                 diplomacy.HasMilitaryAccess(owner, country);
            access[key] = ok;
            return ok;
        }

        public FixedPoint64 GetMovementCost(ushort from, ushort to, PathContext context)
        {
            EnsureStatic();
            return to < cost.Length ? cost[to] : FixedPoint64.One;
        }

        public FixedPoint64 GetHeuristic(ushort from, ushort goal) => FixedPoint64.Zero;

        ushort OwnerOf(ushort province)
        {
            var provinces = gameState.Provinces;
            if (owner == null || ownershipVersion != provinces.OwnershipVersion)
            {
                Hoi4Occupation.Snapshot(gameState, blocked.Length, out _, out owner); // shared: read only
                ownershipVersion = provinces.OwnershipVersion;
            }
            return province < owner.Length ? owner[province] : (ushort)0;
        }

        void EnsureStatic()
        {
            if (blocked != null) return;
            var data = gameState.Provinces.GetNativeData();
            int size = 1;
            for (int i = 0; i < data.activeProvinceIds.Length; i++)
                size = System.Math.Max(size, data.activeProvinceIds[i] + 1);
            blocked = new bool[size];
            cost = new FixedPoint64[size];
            for (int i = 0; i < data.activeProvinceIds.Length; i++)
            {
                ushort p = data.activeProvinceIds[i];
                blocked[p] = Hoi4Regions.IsWater(p) || (terrain != null && terrain.IsProvinceWater(p)) || Hoi4Regions.IsImpassable(p);
                cost[p] = terrain != null ? terrain.GetMovementCost(0, p, default) : FixedPoint64.One;
            }
            blocked[0] = true;
        }
    }
}
