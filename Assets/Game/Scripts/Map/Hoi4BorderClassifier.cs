using System.Collections.Generic;
using System.IO;
using Core.Loaders;
using Core.Modding;
using Map.Rendering;
using Map.Rendering.Border;
using Newtonsoft.Json.Linq;
using UnityEngine;
using ProvinceSystemType = Core.Systems.ProvinceSystem;

namespace Hoi4.Map
{
    /// <summary>
    /// GAME: HOI4 border classes, using the states and strategic regions from
    /// map/hoi4_regions.json5 (written by Tools/hoi4_convert.py).
    ///
    ///   sea / sea_region  water-water, same / different strategic region
    ///   coast             water-land (HOI4 draws no coast line)
    ///   country           different owners
    ///   impassable        edge of an impassable state
    ///   state / province  same owner, different / same state
    ///
    /// Falls back to the engine default classifier when the regions file is missing.
    /// </summary>
    public sealed class Hoi4BorderClassifier : IBorderClassifier
    {
        private const string RegionsFile = "map/hoi4_regions.json5";

        private readonly DefaultBorderClassifier fallback = new DefaultBorderClassifier();
        private readonly Dictionary<ushort, int> provinceState = new Dictionary<ushort, int>();
        private readonly Dictionary<ushort, int> provinceRegion = new Dictionary<ushort, int>();
        private readonly HashSet<ushort> water = new HashSet<ushort>();
        private readonly HashSet<int> impassableStates = new HashSet<int>();
        private bool loaded;
        private bool available;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            MapRendererRegistry.BorderClassifier = new Hoi4BorderClassifier();
        }

        public string Classify(BorderArc arc, ProvinceSystemType provinces)
        {
            if (!EnsureLoaded())
                return fallback.Classify(arc, provinces);

            ushort a = arc.ProvinceA, b = arc.ProvinceB;
            if (a == 0 || b == 0)
                return null;

            bool waterA = water.Contains(a), waterB = water.Contains(b);
            if (waterA && waterB)
                return Region(a) != Region(b) ? "sea_region" : "sea";
            if (waterA || waterB)
                return "coast";

            if (provinces.GetProvinceState(a).ownerID != provinces.GetProvinceState(b).ownerID)
                return "country";

            int stateA = State(a), stateB = State(b);
            if (impassableStates.Contains(stateA) != impassableStates.Contains(stateB))
                return "impassable";
            return stateA != stateB ? "state" : "province";
        }

        private int State(ushort p) => provinceState.TryGetValue(p, out int s) ? s : -1;
        private int Region(ushort p) => provinceRegion.TryGetValue(p, out int r) ? r : -1;

        private bool EnsureLoaded()
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

                ArchonLogger.Log($"Hoi4BorderClassifier: {provinceState.Count} provinces in states, " +
                                 $"{provinceRegion.Count} in strategic regions, {water.Count} water", "map_initialization");
                return available = true;
            }
            catch (System.Exception e)
            {
                ArchonLogger.LogError($"Hoi4BorderClassifier: failed to read {path}: {e.Message}", "map_initialization");
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
