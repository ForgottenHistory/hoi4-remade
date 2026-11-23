using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Map
{
    /// <summary>
    /// GAME: what the ground does to a land battle, from the converted HOI4 data (Tools/hoi4_convert.py).
    /// Per terrain (the GAME fields of map/terrain.json5, from HOI4 common/terrain/00_terrain.txt): the battle's
    /// combat width, the width each extra attack direction adds, and the attacker's attack and breakthrough
    /// modifier. Per pair of neighbouring provinces (map/hoi4_river_crossings.json5): the river between them,
    /// small or large, which costs an attack across it 30% or 60% (HOI4 defines RIVER_CROSSING_PENALTY and
    /// RIVER_CROSSING_PENALTY_LARGE).
    /// Simulation data: the numbers are parsed as doubles and converted to FixedPoint64 once, so they are the
    /// same on every machine. Loaded on first use after the engine's terrain registry is up. Without the
    /// files everything is plains-like: width 70 (+35), no penalty, no rivers.
    /// </summary>
    public static class Hoi4CombatTerrain
    {
        const string TerrainFile = "map/terrain.json5";
        const string RiversFile = "map/hoi4_river_crossings.json5";

        public static readonly FP AmphibiousPenalty = FP.FromFraction(-5, 10); // AMPHIBIOUS_LANDING_PENALTY
        public static readonly FP SmallRiverPenalty = FP.FromFraction(-3, 10);
        public static readonly FP LargeRiverPenalty = FP.FromFraction(-6, 10);

        sealed class Terrain { public FP Width, SupportWidth, Attack, MovementCost; public string Key; }

        static readonly Terrain Fallback = new Terrain { Width = FP.FromInt(70), SupportWidth = FP.FromInt(35), Attack = FP.Zero, MovementCost = FP.One, Key = "plains" };
        static readonly Dictionary<ushort, Terrain> byTerrainId = new Dictionary<ushort, Terrain>();
        static readonly Dictionary<uint, byte> rivers = new Dictionary<uint, byte>();
        static bool loaded;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetCache()
        {
            loaded = false;
            byTerrainId.Clear();
            rivers.Clear();
        }

        /// <summary>Base combat width of a battle in this province (HOI4 terrain combat_width).</summary>
        public static FP Width(GameState gameState, ushort province) => Get(gameState, province).Width;

        /// <summary>Width added per attack direction beyond the first (HOI4 combat_support_width).</summary>
        public static FP SupportWidth(GameState gameState, ushort province) => Get(gameState, province).SupportWidth;

        /// <summary>The attacker's attack and breakthrough modifier in this province, e.g. -0.5 in mountains.</summary>
        public static FP AttackModifier(GameState gameState, ushort province) => Get(gameState, province).Attack;

        /// <summary>HOI4 terrain movement_cost: how much longer crossing the province takes (forest 1.5, mountain 2).</summary>
        public static FP MovementCost(GameState gameState, ushort province) => Get(gameState, province).MovementCost;

        /// <summary>The province's terrain key (plains, forest...), for notes and the recorder.</summary>
        public static string TerrainKey(GameState gameState, ushort province) => Get(gameState, province).Key;

        /// <summary>River between two neighbouring provinces: 0 none, 1 small, 2 large.</summary>
        public static int River(GameState gameState, ushort a, ushort b)
        {
            Load(gameState);
            return rivers.TryGetValue(PairKey(a, b), out byte size) ? size : 0;
        }

        /// <summary>The attacker's modifier for attacking across the border from one province into another.</summary>
        public static FP RiverModifier(GameState gameState, ushort from, ushort to)
        {
            int r = River(gameState, from, to);
            return r == 2 ? LargeRiverPenalty : r == 1 ? SmallRiverPenalty : FP.Zero;
        }

        /// <summary>Whether attacking from one province into the other crosses water: a strait crossing (the
        /// engine's adjacency overrides, type "sea", from HOI4 map/adjacencies.csv). HOI4 fights it as an amphibious
        /// attack ("Naval Penalty").</summary>
        public static bool IsStrait(GameState gameState, ushort from, ushort to) =>
            gameState?.Adjacencies != null && gameState.Adjacencies.TryGetLink(from, to, out var link) && link.Type == "sea";

        /// <summary>An attacking division's modifier over a strait: the amphibious penalty (-50%) plus the
        /// division's own amphibious attack (marines +0.5, tanks -0.4...), added as HOI4's one "Naval Penalty" line.
        /// Zero when the attack doesn't cross water.</summary>
        public static FP StraitModifier(GameState gameState, ushort from, ushort to, FP amphibiousAttack) =>
            IsStrait(gameState, from, to) ? AmphibiousPenalty + amphibiousAttack : FP.Zero;

        /// <summary>An attacking division's modifier for a river between the provinces: the crossing penalty plus
        /// its own river attack (engineers soften it). Zero without a river.</summary>
        public static FP RiverModifier(GameState gameState, ushort from, ushort to, FP riverAttack) =>
            River(gameState, from, to) > 0 ? RiverModifier(gameState, from, to) + riverAttack : FP.Zero;

        static uint PairKey(ushort a, ushort b) => a < b ? ((uint)a << 16) | b : ((uint)b << 16) | a;

        static Terrain Get(GameState gameState, ushort province)
        {
            Load(gameState);
            if (gameState?.ProvinceQueries == null) return Fallback;
            // Province terrain is the 0-based TerrainId (terrain.json5 order), see Hoi4StatePresenter.TerrainKey
            return byTerrainId.TryGetValue(gameState.ProvinceQueries.GetTerrain(province), out var t) ? t : Fallback;
        }

        static void Load(GameState gameState)
        {
            if (loaded || !DataFileResolver.IsInitialized || gameState?.Registries?.Terrains == null
                || gameState.Registries.Terrains.Count == 0) return;
            loaded = true;

            try
            {
                string path = DataFileResolver.Resolve(TerrainFile);
                var categories = File.Exists(path) ? Json5Loader.LoadJson5File(path)["categories"] as JObject : null;
                foreach (var t in gameState.Registries.Terrains.GetAll())
                {
                    var o = categories?[t.Key] as JObject;
                    byTerrainId[t.TerrainId] = new Terrain
                    {
                        Key = t.Key,
                        Width = Num(o?["combat_width"], Fallback.Width),
                        SupportWidth = Num(o?["combat_support_width"], Fallback.SupportWidth),
                        Attack = Num(o?["attack"], FP.Zero),
                        MovementCost = Num(o?["movement_cost"], FP.One),
                    };
                }

                path = DataFileResolver.Resolve(RiversFile);
                if (File.Exists(path) && Json5Loader.LoadJson5File(path)["crossings"] is JArray crossings)
                    foreach (var c in crossings)
                        rivers[PairKey((ushort)(int)c[0], (ushort)(int)c[1])] = (byte)(int)c[2];
                else
                    ArchonLogger.LogWarning($"Hoi4CombatTerrain: no {RiversFile}; battles ignore rivers (rerun Tools/hoi4_convert.py)", "game_units");

                ArchonLogger.Log($"Hoi4CombatTerrain: {byTerrainId.Count} terrains, {rivers.Count} river crossings", "game_units");
            }
            catch (System.Exception e)
            {
                ArchonLogger.LogError($"Hoi4CombatTerrain: failed to load: {e.Message}", "game_units");
            }
        }

        static FP Num(JToken t, FP fallback) => t != null && t.Type != JTokenType.Null ? FP.FromDouble((double)t) : fallback;
    }
}
