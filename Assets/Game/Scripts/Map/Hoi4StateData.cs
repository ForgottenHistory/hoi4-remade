using System.Collections.Generic;
using System.IO;
using Core.Loaders;
using Core.Modding;
using Newtonsoft.Json.Linq;

namespace Hoi4.Map
{
    /// <summary>
    /// GAME: HOI4 state details for the state panel (owner, cores, manpower, resources, victory points and
    /// buildings at the start date) plus building definitions and state categories, from
    /// map/hoi4_state_data.json5 (written by Tools/hoi4_convert.py). Loaded on first use.
    /// </summary>
    public static class Hoi4StateData
    {
        public sealed class BuildingDef
        {
            public int Frame;        // 1-based icon strip frame
            public bool Coastal;
            public bool Shared;      // uses the state's shared building slots
            public int StateMax;     // 0 when not a state building
            public int ProvinceMax;  // 0 when not a province building
            public double Cost;      // construction: base_cost of a level
            public double CostPerLevel; // + this x the level being built (naval bases, forts)
            public bool InfraSpeed;  // the state's infrastructure speeds its construction up
            public string Name = "";
            public string Section = ""; // construction screen section: state, shared, province, facility ("" never offered)
        }

        public sealed class StateInfo
        {
            public string Category = "";
            public int Manpower;
            public string Owner = "";
            public List<string> Cores = new List<string>();
            public Dictionary<string, int> Resources = new Dictionary<string, int>();
            public Dictionary<int, int> VictoryPoints = new Dictionary<int, int>();
            public Dictionary<string, int> Buildings = new Dictionary<string, int>();
            public Dictionary<int, Dictionary<string, int>> Provincial = new Dictionary<int, Dictionary<string, int>>();
            public double Value;           // HOI4 state value at the start (peace conference costs and score)
            public bool HasCenter;
            public int CenterX, CenterY;   // centre on the map, px (y down): peace conference influence distances
        }

        /// <summary>Order of GFX_resources_strip frames (common/resources icon_frame 1..7).</summary>
        public static readonly string[] ResourceOrder = { "oil", "aluminium", "rubber", "tungsten", "steel", "chromium", "coal" };

        static readonly Dictionary<string, BuildingDef> defs = new Dictionary<string, BuildingDef>();
        static readonly List<BuildingDef> defOrder = new List<BuildingDef>();
        static readonly Dictionary<string, int> categories = new Dictionary<string, int>();
        static readonly Dictionary<int, StateInfo> states = new Dictionary<int, StateInfo>();
        static bool loaded;
        static bool available;

        public static bool Available => EnsureLoaded();

        public static StateInfo Get(int state) => EnsureLoaded() && states.TryGetValue(state, out var s) ? s : null;
        public static BuildingDef Def(string building) => EnsureLoaded() && defs.TryGetValue(building, out var d) ? d : null;

        /// <summary>Every building definition in HOI4's file order (the construction screen's button order).</summary>
        public static IReadOnlyList<BuildingDef> Defs => EnsureLoaded() ? defOrder : (IReadOnlyList<BuildingDef>)System.Array.Empty<BuildingDef>();
        public static int Slots(string category) => EnsureLoaded() && categories.TryGetValue(category ?? "", out int n) ? n : 0;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            defs.Clear(); defOrder.Clear(); categories.Clear(); states.Clear();
            loaded = false; available = false;
        }

        static bool EnsureLoaded()
        {
            if (loaded) return available;
            if (!DataFileResolver.IsInitialized) return false;
            loaded = true;

            string path = DataFileResolver.Resolve("map/hoi4_state_data.json5");
            if (!File.Exists(path)) return available = false;
            try
            {
                JObject root = Json5Loader.LoadJson5File(path);
                foreach (var p in (root["buildings"] as JObject).Properties())
                {
                    var def = defs[p.Name] = new BuildingDef
                    {
                        Name = p.Name,
                        Section = (string)p.Value["section"] ?? "",
                        Frame = (int?)p.Value["frame"] ?? 0,
                        Coastal = (bool?)p.Value["coastal"] ?? false,
                        Shared = (bool?)p.Value["shared"] ?? false,
                        StateMax = (int?)p.Value["state_max"] ?? 0,
                        ProvinceMax = (int?)p.Value["province_max"] ?? 0,
                        Cost = (double?)p.Value["cost"] ?? 0.0,
                        CostPerLevel = (double?)p.Value["cost_per_level"] ?? 0.0,
                        InfraSpeed = (bool?)p.Value["infra_speed"] ?? false,
                    };
                    defOrder.Add(def);
                }
                foreach (var p in (root["categories"] as JObject).Properties())
                    categories[p.Name] = (int)p.Value;
                foreach (var p in (root["states"] as JObject).Properties())
                {
                    var j = p.Value;
                    var s = new StateInfo
                    {
                        Category = (string)j["category"] ?? "",
                        Manpower = (int?)j["manpower"] ?? 0,
                        Owner = (string)j["owner"] ?? "",
                        Value = (double?)j["value"] ?? 0.0,
                    };
                    if (j["center"] is JArray centre && centre.Count == 2)
                    {
                        s.HasCenter = true;
                        s.CenterX = (int)centre[0];
                        s.CenterY = (int)centre[1];
                    }
                    if (j["cores"] is JArray cores) foreach (var c in cores) s.Cores.Add((string)c);
                    Fill(j["resources"] as JObject, s.Resources);
                    if (j["vps"] is JObject vps) foreach (var v in vps.Properties()) s.VictoryPoints[int.Parse(v.Name)] = (int)v.Value;
                    Fill(j["buildings"] as JObject, s.Buildings);
                    if (j["provincial"] is JObject prov)
                        foreach (var pp in prov.Properties())
                        {
                            var d = new Dictionary<string, int>();
                            Fill(pp.Value as JObject, d);
                            s.Provincial[int.Parse(pp.Name)] = d;
                        }
                    states[int.Parse(p.Name)] = s;
                }
                ArchonLogger.Log($"Hoi4StateData: {states.Count} states, {defs.Count} building types", "map_initialization");
                return available = true;
            }
            catch (System.Exception e)
            {
                ArchonLogger.LogError($"Hoi4StateData: failed to read {path}: {e.Message}", "map_initialization");
                return available = false;
            }
        }

        static void Fill(JObject src, Dictionary<string, int> target)
        {
            if (src == null) return;
            foreach (var p in src.Properties()) target[p.Name] = (int)p.Value;
        }
    }
}
