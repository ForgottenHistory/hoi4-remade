using Core.Localization;
using Map.Rendering;
using Map.Rendering.Labels;
using UnityEngine;

namespace Hoi4.Map
{
    /// <summary>
    /// GAME: HOI4 states as the engine's region labels (state names on the map, like HOI4's construction map mode).
    /// Region = state id (Hoi4Regions, from map/hoi4_regions.json5), text = the localised STATE_n name. Shown while
    /// the construction screen places a building (MapRegionLabels.Shown); they fade in at the zoom where unit
    /// counters appear (regionLabels in the visual style).
    /// </summary>
    public sealed class Hoi4StateLabelSource : IMapRegionLabelSource
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            MapRendererRegistry.RegionLabelSource = new Hoi4StateLabelSource();
        }

        public ushort GetRegion(ushort province)
        {
            if (!Hoi4Regions.Available || Hoi4Regions.IsWater(province)) return 0;
            int state = Hoi4Regions.State(province);
            return state > 0 && state <= ushort.MaxValue ? (ushort)state : (ushort)0;
        }

        public string GetRegionText(ushort region)
        {
            string key = $"STATE_{region}";
            string name = LocalizationManager.IsInitialized ? LocalizationManager.Get(key) : null;
            return string.IsNullOrEmpty(name) || name == key ? null : name;
        }
    }
}
