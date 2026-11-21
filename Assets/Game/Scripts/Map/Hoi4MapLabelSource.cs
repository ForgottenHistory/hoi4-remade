using System.Collections.Generic;
using System.IO;
using Core.Loaders;
using Core.Modding;
using Core.Systems;
using Map.Rendering;
using Map.Rendering.Labels;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace Hoi4.Map
{
    /// <summary>
    /// GAME: HOI4 map labels. Names come from the engine default (localised country tag); capitals
    /// (the province whose territory piece gets the main label) from the "capitals" block of
    /// map/hoi4_regions.json5, written by Tools/hoi4_convert.py from HOI4's history/countries.
    /// </summary>
    public sealed class Hoi4MapLabelSource : DefaultMapLabelSource
    {
        private const string RegionsFile = "map/hoi4_regions.json5";

        private readonly Dictionary<string, ushort> capitals = new Dictionary<string, ushort>();
        private bool loaded;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            MapRendererRegistry.LabelSource = new Hoi4MapLabelSource();
        }

        /// <summary>Occupied land counts towards the occupier's label, like its colour.</summary>
        public override ushort GetLabelHolder(ushort province, ushort owner, Core.GameState gameState) =>
            Hoi4.Units.Hoi4Occupation.ControllerOf(gameState, province);

        public override ushort GetCapitalProvince(ushort countryId, CountrySystem countries)
        {
            EnsureLoaded();
            string tag = countries?.GetCountryTag(countryId);
            return tag != null && capitals.TryGetValue(tag, out ushort province) ? province : (ushort)0;
        }

        private void EnsureLoaded()
        {
            if (loaded || !DataFileResolver.IsInitialized) return;
            loaded = true;

            string path = DataFileResolver.Resolve(RegionsFile);
            if (!File.Exists(path)) return;
            try
            {
                JObject root = Json5Loader.LoadJson5File(path);
                if (root["capitals"] is JObject caps)
                    foreach (var p in caps.Properties())
                        capitals[p.Name] = (ushort)(int)p.Value;
                ArchonLogger.Log($"Hoi4MapLabelSource: {capitals.Count} capitals", "map_initialization");
            }
            catch (System.Exception e)
            {
                ArchonLogger.LogError($"Hoi4MapLabelSource: failed to read {path}: {e.Message}", "map_initialization");
            }
        }
    }
}
