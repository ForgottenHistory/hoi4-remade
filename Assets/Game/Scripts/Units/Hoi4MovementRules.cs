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
    /// </summary>
    public sealed class Hoi4MovementRules : IMovementCostCalculator
    {
        private readonly GameState gameState;
        private readonly TerrainMovementCostCalculator terrain;

        public Hoi4MovementRules(GameState gameState)
        {
            this.gameState = gameState;
            if (gameState.Registries?.Terrains != null && gameState.Registries.Terrains.Count > 0)
                terrain = new TerrainMovementCostCalculator(gameState.Provinces, gameState.Registries.Terrains);
        }

        public bool CanTraverse(ushort province, PathContext context)
        {
            if (Hoi4Regions.IsWater(province) || (terrain != null && terrain.IsProvinceWater(province)))
                return false;
            if (Hoi4Regions.IsImpassable(province))
                return false;
            return HasAccess(context.UnitOwnerCountryId, gameState.Provinces.GetProvinceOwner(province));
        }

        /// <summary>May units of `country` enter land owned by `owner`?</summary>
        public bool HasAccess(ushort country, ushort owner)
        {
            if (country == 0) return true; // no owner given: terrain rules only
            if (owner == 0) return false;  // unowned land
            if (owner == country) return true;
            var diplomacy = gameState.Diplomacy;
            if (diplomacy == null) return false;
            return diplomacy.AreAllied(country, owner) ||
                   diplomacy.IsAtWar(country, owner) ||
                   diplomacy.HasMilitaryAccess(owner, country);
        }

        public FixedPoint64 GetMovementCost(ushort from, ushort to, PathContext context) =>
            terrain != null ? terrain.GetMovementCost(from, to, context) : FixedPoint64.One;

        public FixedPoint64 GetHeuristic(ushort from, ushort goal) => FixedPoint64.Zero;
    }
}
