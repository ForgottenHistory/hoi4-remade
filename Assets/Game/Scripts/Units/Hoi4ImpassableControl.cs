using System.Collections.Generic;
using System.IO;
using Core;
using Core.Modding;
using Hoi4.Map;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: who controls impassable land (HOI4 deserts, mountains, ice no division may enter). Nobody can walk in
    /// to take it, so, as in HOI4, it goes to whoever controls most of its border: daily, each impassable state
    /// whose border (its provinces' map/hoi4_impassable_borders.csv, pixel edges with each neighbour outside the
    /// state, from Tools/hoi4_convert.py; a neighbouring impassable state counts for whoever controls it, so the
    /// Sahara follows the country holding most of the desert, not a few oases) is mostly held by one country at
    /// war with its controller passes to that
    /// country, all its provinces (inner ones touch no passable land; ties: the lower country id). Land of a country not at war with its controller goes back to its owner
    /// as usual (Hoi4Occupation's liberation rule). Runs on every machine (deterministic: sorted data).
    /// </summary>
    public static class Hoi4ImpassableControl
    {
        const string BordersFile = "map/hoi4_impassable_borders.csv";

        // impassable state -> (passable neighbour, border length) of all its provinces, sorted by state
        static readonly SortedDictionary<int, List<(ushort neighbour, int length)>> borders = new SortedDictionary<int, List<(ushort, int)>>();
        static bool loaded;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetCache()
        {
            loaded = false;
            borders.Clear();
        }

        /// <summary>Daily, from Hoi4War (before capitulations).</summary>
        public static void SimulateDay(GameState gameState)
        {
            Load();
            if (borders.Count == 0 || gameState?.Provinces == null) return;
            var provinces = gameState.Provinces;
            var diplomacy = gameState.Diplomacy;
            var held = new Dictionary<ushort, int>();
            foreach (var kv in borders)
            {
                var stateProvinces = Hoi4Regions.ProvincesOf(kv.Key);
                if (stateProvinces.Count == 0) continue;
                ushort p = stateProvinces[0];
                ushort owner = provinces.GetProvinceOwner(p);
                if (owner == 0) continue;
                ushort current = Hoi4Occupation.ControllerOf(gameState, p);
                held.Clear();
                foreach (var (n, length) in kv.Value)
                {
                    ushort c = Hoi4Occupation.ControllerOf(gameState, n);
                    if (c != 0) held[c] = (held.TryGetValue(c, out int l) ? l : 0) + length;
                }
                ushort top = 0;
                int best = 0;
                foreach (var h in held)
                    if (h.Value > best || (h.Value == best && h.Key < top)) { best = h.Value; top = h.Key; }
                if (top == 0 || top == current) continue;
                ushort to;
                if (diplomacy.IsAtWar(top, current)) to = !diplomacy.IsAtWar(top, owner) ? owner : top; // a third country liberates
                else if (current != owner && !diplomacy.IsAtWar(current, owner) ) to = owner;           // an occupation that outlived its war
                else continue;
                if (to == current) continue;
                foreach (ushort sp in stateProvinces)
                    if (provinces.GetProvinceOwner(sp) == owner) provinces.SetProvinceController(sp, to);
                Core.Recording.SessionRecorder.Note("occupation", "impassable", to, 0, p, current,
                    $"impassable state {kv.Key} to {gameState.Countries.GetCountryTag(to)} (holds {best} of its border, most of anyone)");
            }
        }

        static void Load()
        {
            if (loaded || !DataFileResolver.IsInitialized) return;
            loaded = true;
            string path = DataFileResolver.Resolve(BordersFile);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4ImpassableControl: no {BordersFile}; impassable land keeps its controller (rerun Tools/hoi4_convert.py)", "game_units");
                return;
            }
            foreach (string line in File.ReadLines(path))
            {
                string[] f = line.Split(';');
                if (f.Length < 3 || !ushort.TryParse(f[0], out ushort p) || !ushort.TryParse(f[1], out ushort n) || !int.TryParse(f[2], out int l)) continue;
                int s = Hoi4Regions.State(p);
                if (s <= 0 || Hoi4Regions.State(n) == s) continue; // the state's own inner borders don't count
                if (!borders.TryGetValue(s, out var list)) borders[s] = list = new List<(ushort, int)>();
                list.Add((n, l));
            }
            foreach (var list in borders.Values) list.Sort((a, b) => a.Item1 != b.Item1 ? a.Item1.CompareTo(b.Item1) : a.Item2.CompareTo(b.Item2));
            ArchonLogger.Log($"Hoi4ImpassableControl: {borders.Count} impassable states", "game_units");
        }
    }
}
