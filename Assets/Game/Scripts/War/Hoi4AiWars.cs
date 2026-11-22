using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Diplomacy;
using Hoi4.Map;
using Hoi4.Units;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.War
{
    /// <summary>
    /// GAME: AI war declarations, for now a stress test of the war AI (fronts, attacks, capitulation, AI peace
    /// conferences). Off by default; F8 toggles it. While on, every IntervalDays each AI country with an army, in
    /// id order, may declare war on a land neighbour: one it isn't allied with, not at war with, and clearly weaker
    /// than it (enemy division strength at most MaxStrengthRatio of its own); the weakest relative to it first.
    /// A country fights at most MaxWars wars, and at most MaxPerDay wars start per check, so the world catches
    /// fire gradually. The player's country is never attacked unless IncludePlayer is set (inspector).
    /// Declarations go through DeclareWarCommand like the player's; the decisions are noted in the recording
    /// (system "ai_war"). Host only (Hoi4Simulation.RunsAI).
    /// </summary>
    public sealed class Hoi4AiWars : MonoBehaviour
    {
        public static Hoi4AiWars Instance { get; private set; }

        [Tooltip("AI countries declare wars on weaker neighbours (F8 toggles)")] public bool stressTest;
        [Tooltip("The player's country can be attacked too")] public bool includePlayer;
        [Min(1)] public int intervalDays = 3;
        [Min(1)] public int maxWars = 1;
        [Min(1)] public int maxPerDay = 3;
        [Min(1)] public int minDivisions = 2;
        [Range(0.05f, 1f)] public float maxStrengthRatio = 0.7f;

        GameState gameState;
        int day;
        // land province -> neighbouring land provinces, sorted (the engine's adjacency has no fixed order)
        readonly Dictionary<ushort, ushort[]> landNeighbours = new Dictionary<ushort, ushort[]>();
        readonly List<ushort> landProvinces = new List<ushort>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4AiWars");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4AiWars>();
        }

        void Awake() => Instance = this;
        void OnDestroy() { if (Instance == this) Instance = null; }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Adjacencies == null || !Hoi4Regions.Available
                   || Hoi4Divisions.Instance == null || !Hoi4Divisions.Instance.IsLoaded)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            var ids = gameState.Provinces.GetAllProvinceIds(Unity.Collections.Allocator.Temp);
            var all = ids.ToArray();
            ids.Dispose();
            System.Array.Sort(all);
            foreach (ushort p in all)
            {
                if (p == 0 || Hoi4Regions.IsWater(p)) continue;
                var n = new List<ushort>();
                foreach (ushort q in gameState.Adjacencies.GetNeighborsWhere(p, x => !Hoi4Regions.IsWater(x))) n.Add(q);
                n.Sort();
                landProvinces.Add(p);
                landNeighbours[p] = n.ToArray();
            }
        }

        void Update()
        {
            if (!Input.GetKeyDown(KeyCode.F8)) return;
            stressTest = !stressTest;
            ArchonLogger.Log($"Hoi4AiWars: AI war stress test {(stressTest ? "ON" : "OFF")}", "game_war");
            Core.Recording.SessionRecorder.Note("ai_war", stressTest ? "stress_on" : "stress_off", 0, 0, 0, 0,
                $"every {intervalDays} days, max {maxWars} wars each, {maxPerDay} per check, ratio {maxStrengthRatio:0.00}");
        }

        static ushort Player => StarterKit.Initializer.Instance?.PlayerState?.PlayerCountryId ?? 0;

        /// <summary>Daily, from Hoi4Simulation (host only), after the war system.</summary>
        public void SimulateDay()
        {
            if (gameState == null || !stressTest) return;
            if (Hoi4War.Instance != null && Hoi4War.Instance.Conference != null) return; // the player is deciding a peace
            if (++day % intervalDays != 0) return;

            var diplomacy = gameState.Diplomacy;
            var strength = new Dictionary<ushort, FP>();
            var divisions = new Dictionary<ushort, int>();
            var units = gameState.Units;
            foreach (ushort id in units.GetAllUnits())
            {
                var d = Hoi4Divisions.Instance.Get(id);
                if (d == null) continue;
                ushort c = units.GetUnit(id).countryID;
                strength[c] = (strength.TryGetValue(c, out FP s) ? s : FP.Zero) + d.Strength;
                divisions[c] = (divisions.TryGetValue(c, out int n) ? n : 0) + 1;
            }

            // Land neighbours by owner
            // (one read of every owner, not two lookups per province and neighbour; each pair added once)
            var borders = new SortedDictionary<ushort, SortedSet<ushort>>();
            int size = 1;
            foreach (var kv in landNeighbours) size = System.Math.Max(size, kv.Key + 1);
            Hoi4Occupation.Snapshot(gameState, size, out _, out var owner); // shared: read only
            var pairs = new HashSet<uint>();
            foreach (ushort p in landProvinces)
            {
                ushort a = owner[p];
                if (a == 0) continue;
                foreach (ushort q in landNeighbours[p])
                {
                    ushort b = q < owner.Length ? owner[q] : (ushort)0;
                    if (b == 0 || b == a || !pairs.Add((uint)a << 16 | b)) continue;
                    if (!borders.TryGetValue(a, out var set)) borders[a] = set = new SortedSet<ushort>();
                    set.Add(b);
                }
            }

            ushort player = Player;
            FP maxRatio = FP.FromDouble(maxStrengthRatio);
            int declared = 0;
            int tick = (int)gameState.Time.CurrentTick;
            foreach (var kv in borders)
            {
                if (declared >= maxPerDay) break;
                ushort country = kv.Key;
                if (country == player) continue;
                if (!divisions.TryGetValue(country, out int own) || own < minDivisions) continue;
                if (diplomacy.GetEnemies(country).Count >= maxWars) continue;
                FP mine = strength[country];
                if (mine <= FP.Zero) continue;

                ushort target = 0;
                FP best = FP.MaxValue;
                foreach (ushort other in kv.Value)
                {
                    if (other == player && !includePlayer) continue;
                    if (diplomacy.IsAtWar(country, other) || diplomacy.AreAllied(country, other)
                        || diplomacy.HasNonAggressionPact(country, other)) continue;
                    FP ratio = (strength.TryGetValue(other, out FP s) ? s : FP.Zero) / mine;
                    if (ratio > maxRatio || ratio >= best) continue;
                    best = ratio;
                    target = other;
                }
                if (target == 0) continue;

                var cmd = new DeclareWarCommand { AttackerID = country, DefenderID = target };
                if (!gameState.TryExecuteCommand(cmd, out _)) continue;
                declared++;
                Core.Recording.SessionRecorder.Note("ai_war", "declare", country, 0, 0, target,
                    $"{gameState.Countries.GetCountryTag(country)} ({own} div, str {mine.ToInt()}) on " +
                    $"{gameState.Countries.GetCountryTag(target)} (ratio {best.ToFloat():0.00})");
                ArchonLogger.LogDebug($"Hoi4AiWars: {gameState.Countries.GetCountryTag(country)} declares war on {gameState.Countries.GetCountryTag(target)} (strength ratio {best.ToFloat():0.00}, tick {tick})", "game_war");
            }
        }
    }
}
