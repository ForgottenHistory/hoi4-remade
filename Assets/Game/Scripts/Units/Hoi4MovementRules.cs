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
    /// Naval transfer: a division may also go by sea between ports (Hoi4Ports). It goes to sea only from a province
    /// with a naval base that its country or an ally controls, into that base's sea province; it sails any water
    /// from there; it lands only at such a port, from the port's sea province (an enemy's port can't be landed at:
    /// no naval invasions). There is no navy yet, so the sea is open and nothing needs convoys. Going to sea costs
    /// EmbarkCost and every sea province SeaCost, far more than land provinces (HOI4 weighs sea distance by
    /// NAVAL_TRANSFER_BASE_NAVAL_DIST_MULT 20 and adds 100 when it picks a route): a division sails only where
    /// walking is impossible or a very long way round, and a search over land looks only a few provinces out to sea.
    ///
    /// Cost: A* calls CanTraverse and GetMovementCost for every province it looks at, so they are array reads.
    /// Water, impassable and terrain cost never change in HOI4 and are read once. Owners are a snapshot
    /// rebuilt when ProvinceSystem.OwnershipVersion changes; access per (country, owner) is cached until
    /// DiplomacySystem.RelationsVersion or the ownership changes, and folded per country into one passable array
    /// (Passable) under the same versions. Same answers as reading them each time.
    /// </summary>
    public sealed class Hoi4MovementRules : IMovementCostCalculator
    {
        private readonly GameState gameState;
        private readonly TerrainMovementCostCalculator terrain;

        // Static per province (by id): not enterable whatever the owner (water, impassable), terrain cost
        private bool[] blocked;
        private bool[] water;
        static readonly FixedPoint64 EmbarkCost = FixedPoint64.FromInt(40);
        static readonly FixedPoint64 SeaCost = FixedPoint64.FromInt(10);
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
            if (province >= blocked.Length)
                return false;
            if (water[province]) // the sea: whether this step may be taken is the edge's question (GetMovementCost)
                return context.UnitOwnerCountryId != 0 && Hoi4.Supply.Hoi4Ports.Available;
            if (blocked[province])
                return false;
            ushort country = context.UnitOwnerCountryId;
            if (country == 0) return true; // no owner given: terrain rules only
            return Passable(country)[province];
        }

        // Per country: may it enter each province (terrain, impassable, the owner's war/alliance/access), as of the
        // ownership and relations versions. A* asks for every province it looks at (hundreds of thousands of times
        // an hour in a world war): one array read instead of version checks, an owner read and an access lookup
        readonly Dictionary<ushort, (int ownership, int relations, bool[] ok)> passable = new Dictionary<ushort, (int, int, bool[])>();
        ushort lastCountry;
        bool[] lastPassable;
        int lastOwnership = -1, lastRelations = -1;

        bool[] Passable(ushort country)
        {
            int ownership = gameState.Provinces.OwnershipVersion;
            int relations = gameState.Diplomacy?.RelationsVersion ?? 0;
            if (country == lastCountry && lastPassable != null && ownership == lastOwnership && relations == lastRelations) return lastPassable;
            if (!passable.TryGetValue(country, out var entry) || entry.ownership != ownership || entry.relations != relations)
            {
                var ok = entry.ok ?? new bool[blocked.Length];
                for (int p = 0; p < ok.Length; p++) ok[p] = !blocked[p] && HasAccess(country, OwnerOf((ushort)p));
                passable[country] = entry = (ownership, relations, ok);
            }
            lastCountry = country;
            lastPassable = entry.ok;
            lastOwnership = ownership;
            lastRelations = relations;
            return entry.ok;
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
            if (to >= cost.Length) return FixedPoint64.One;
            bool seaFrom = from < water.Length && water[from], seaTo = water[to];
            if (!seaFrom && !seaTo) return cost[to];
            if (seaFrom && seaTo) return SeaCost;
            // between land and sea: only through a port of the division's side, to or from the port's own sea province
            ushort port = seaFrom ? to : from, sea = seaFrom ? from : to;
            if (!CanUsePort(context.UnitOwnerCountryId, port) || Hoi4.Supply.Hoi4Ports.Sea(port) != sea)
                return FixedPoint64.MaxValue;
            return seaTo ? EmbarkCost : cost[to];
        }

        /// <summary>May the country's divisions go to sea from, or land at, this province: it has a naval base, held
        /// by the country or an ally.</summary>
        public bool CanUsePort(ushort country, ushort province)
        {
            if (country == 0 || Hoi4.Supply.Hoi4Ports.Level(province) <= 0) return false;
            ushort holder = Hoi4Occupation.ControllerOf(gameState, province);
            return holder == country || (holder != 0 && gameState.Diplomacy != null && gameState.Diplomacy.AreAllied(country, holder));
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
            water = new bool[size];
            cost = new FixedPoint64[size];
            for (int i = 0; i < data.activeProvinceIds.Length; i++)
            {
                ushort p = data.activeProvinceIds[i];
                water[p] = Hoi4Regions.IsWater(p);
                blocked[p] = water[p] || (terrain != null && terrain.IsProvinceWater(p)) || Hoi4Regions.IsImpassable(p);
                cost[p] = terrain != null ? terrain.GetMovementCost(0, p, default) : FixedPoint64.One;
            }
            blocked[0] = true;
        }
    }
}
