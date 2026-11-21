using System.Collections.Generic;
using System.IO;
using Core.Loaders;
using Core.Modding;
using Newtonsoft.Json.Linq;

namespace Hoi4.Map
{
    /// <summary>
    /// GAME: HOI4 states, strategic regions, impassable states and water provinces from
    /// map/hoi4_regions.json5 (written by Tools/hoi4_convert.py). Loaded once, on first use after
    /// DataFileResolver is ready. Available is false when the file is missing or unreadable.
    /// </summary>
    public static class Hoi4Regions
    {
        private const string RegionsFile = "map/hoi4_regions.json5";

        private static readonly Dictionary<ushort, int> provinceState = new Dictionary<ushort, int>();
        private static readonly Dictionary<ushort, int> provinceRegion = new Dictionary<ushort, int>();
        private static readonly HashSet<ushort> water = new HashSet<ushort>();
        private static readonly HashSet<int> impassableStates = new HashSet<int>();
        private static readonly Dictionary<int, List<ushort>> stateProvinces = new Dictionary<int, List<ushort>>();
        private static readonly Dictionary<string, ushort> capitals = new Dictionary<string, ushort>();
        private static readonly List<int> stateIds = new List<int>();
        private static bool loaded;
        private static bool available;

        /// <summary>True once the regions file has been read successfully (loads it on first call).</summary>
        public static bool Available => EnsureLoaded();

        /// <summary>HOI4 state of a province, or -1.</summary>
        public static int State(ushort province) =>
            EnsureLoaded() && provinceState.TryGetValue(province, out int s) ? s : -1;

        /// <summary>Strategic region of a province, or -1.</summary>
        public static int Region(ushort province) =>
            EnsureLoaded() && provinceRegion.TryGetValue(province, out int r) ? r : -1;

        public static bool IsWater(ushort province) => EnsureLoaded() && water.Contains(province);

        public static bool IsImpassableState(int state) => EnsureLoaded() && impassableStates.Contains(state);

        /// <summary>Province lies in an impassable state (HOI4 mountains, deserts, ice no unit may enter).</summary>
        public static bool IsImpassable(ushort province) => IsImpassableState(State(province));

        /// <summary>The provinces of a state, sorted (empty when unknown).</summary>
        public static IReadOnlyList<ushort> ProvincesOf(int state) =>
            EnsureLoaded() && stateProvinces.TryGetValue(state, out var l) ? l : (IReadOnlyList<ushort>)System.Array.Empty<ushort>();

        /// <summary>Every state id, sorted.</summary>
        public static IReadOnlyList<int> States => EnsureLoaded() ? stateIds : (IReadOnlyList<int>)System.Array.Empty<int>();

        /// <summary>A country's capital province (HOI4 history), 0 when unknown.</summary>
        public static ushort Capital(string tag) =>
            EnsureLoaded() && tag != null && capitals.TryGetValue(tag, out ushort p) ? p : (ushort)0;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            provinceState.Clear(); provinceRegion.Clear(); water.Clear(); impassableStates.Clear();
            stateProvinces.Clear(); capitals.Clear(); stateIds.Clear();
            loaded = false; available = false;
        }

        private static bool EnsureLoaded()
        {
            if (loaded)
                return available;
            if (!DataFileResolver.IsInitialized)
                return false; // try again on the next call
            loaded = true;

            string path = DataFileResolver.Resolve(RegionsFile);
            if (!File.Exists(path))
                return available = false;

            try
            {
                JObject root = Json5Loader.LoadJson5File(path);
                ReadGroups(root["states"] as JObject, provinceState);
                ReadGroups(root["strategic_regions"] as JObject, provinceRegion);
                if (root["impassable_states"] is JArray imp)
                    foreach (var v in imp) impassableStates.Add((int)v);
                if (root["water_provinces"] is JArray wat)
                    foreach (var v in wat) water.Add((ushort)(int)v);
                foreach (var kv in provinceState)
                {
                    if (!stateProvinces.TryGetValue(kv.Value, out var list)) stateProvinces[kv.Value] = list = new List<ushort>();
                    list.Add(kv.Key);
                }
                foreach (var list in stateProvinces.Values) list.Sort();
                stateIds.AddRange(stateProvinces.Keys);
                stateIds.Sort();
                if (root["capitals"] is JObject caps)
                    foreach (var c in caps.Properties()) capitals[c.Name] = (ushort)(int)c.Value;

                ArchonLogger.Log($"Hoi4Regions: {provinceState.Count} provinces in states, {provinceRegion.Count} in " +
                                 $"strategic regions, {impassableStates.Count} impassable states, {water.Count} water", "map_initialization");
                return available = true;
            }
            catch (System.Exception e)
            {
                ArchonLogger.LogError($"Hoi4Regions: failed to read {path}: {e.Message}", "map_initialization");
                return available = false;
            }
        }

        private static void ReadGroups(JObject groups, Dictionary<ushort, int> target)
        {
            if (groups == null) return;
            foreach (var group in groups.Properties())
            {
                int id = int.Parse(group.Name);
                foreach (var p in (JArray)group.Value)
                    target[(ushort)(int)p] = id;
            }
        }
    }
}
