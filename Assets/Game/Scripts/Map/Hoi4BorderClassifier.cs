using Map.Rendering;
using Map.Rendering.Border;
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
    /// Region data comes from Hoi4Regions. Falls back to the engine default classifier when the
    /// regions file is missing.
    /// </summary>
    public sealed class Hoi4BorderClassifier : IBorderClassifier
    {
        private readonly DefaultBorderClassifier fallback = new DefaultBorderClassifier();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            MapRendererRegistry.BorderClassifier = new Hoi4BorderClassifier();
        }

        public string Classify(BorderArc arc, ProvinceSystemType provinces)
        {
            if (!Hoi4Regions.Available)
                return fallback.Classify(arc, provinces);

            ushort a = arc.ProvinceA, b = arc.ProvinceB;
            if (a == 0 || b == 0)
                return null;

            bool waterA = Hoi4Regions.IsWater(a), waterB = Hoi4Regions.IsWater(b);
            if (waterA && waterB)
                return Hoi4Regions.Region(a) != Hoi4Regions.Region(b) ? "sea_region" : "sea";
            if (waterA || waterB)
                return "coast";

            if (provinces.GetProvinceState(a).ownerID != provinces.GetProvinceState(b).ownerID)
                return "country";

            int stateA = Hoi4Regions.State(a), stateB = Hoi4Regions.State(b);
            if (Hoi4Regions.IsImpassableState(stateA) != Hoi4Regions.IsImpassableState(stateB))
                return "impassable";
            return stateA != stateB ? "state" : "province";
        }
    }
}
