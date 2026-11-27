using System.Collections.Generic;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4 army, navy and air experience, per country: a stored number each, 0 to MAX_*_EXPERIENCE 500
    /// (NDefines.NCountry). Focus rewards add to it (add_army_experience ...) and scripts can ask for it.
    /// Nothing earns it in play and nothing spends it yet (division templates, equipment designs, doctrines).
    /// Part of the simulation: Add is called from commands and the daily tick only.
    /// </summary>
    public static class Hoi4Experience
    {
        public enum Kind { Army, Navy, Air }

        public static readonly FP Max = FP.FromInt(500);

        static readonly Dictionary<(ushort, Kind), FP> stored = new Dictionary<(ushort, Kind), FP>();

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => stored.Clear();

        public static FP Of(ushort country, Kind kind) => stored.TryGetValue((country, kind), out FP v) ? v : FP.Zero;

        public static void Add(ushort country, Kind kind, FP amount)
        {
            if (country != 0) stored[(country, kind)] = FP.Clamp(Of(country, kind) + amount, FP.Zero, Max);
        }
    }
}
