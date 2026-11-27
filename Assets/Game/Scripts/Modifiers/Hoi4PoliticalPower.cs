using System.Collections.Generic;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Modifiers
{
    /// <summary>
    /// GAME: HOI4 political power, per country.
    /// - A day's gain: (BASE_POLITICAL_POWER_INCREASE 2 + political_power_gain) x (1 + political_power_factor), both
    ///   keys summed over the country's ideas and technologies (Hoi4Modifiers).
    /// - Kept between POLITICAL_POWER_LOWER_CAP -500 and POLITICAL_POWER_UPPER_CAP 2000 (NDefines.NCountry).
    /// - Every country starts at 0 (the history files' own add_political_power is not read).
    /// - Nothing spends it yet (laws, advisors, decisions): focus rewards add to it.
    /// Part of the simulation: Add and SimulateDay are called from the daily tick and from commands only.
    /// </summary>
    public static class Hoi4PoliticalPower
    {
        public static readonly FP BaseGain = FP.FromInt(2);       // NDefines.NCountry.BASE_POLITICAL_POWER_INCREASE
        public static readonly FP LowerCap = FP.FromInt(-500);    // POLITICAL_POWER_LOWER_CAP
        public static readonly FP UpperCap = FP.FromInt(2000);    // POLITICAL_POWER_UPPER_CAP

        static readonly Dictionary<ushort, FP> power = new Dictionary<ushort, FP>();

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => power.Clear();

        public static FP Of(ushort country) => power.TryGetValue(country, out FP p) ? p : FP.Zero;

        /// <summary>Political power the country gains a day.</summary>
        public static FP DailyGain(ushort country) =>
            (BaseGain + Hoi4Modifiers.Sum(country, Hoi4Modifiers.PoliticalPowerGain))
            * (FP.One + Hoi4Modifiers.Sum(country, Hoi4Modifiers.PoliticalPowerFactor));

        public static void Add(ushort country, FP amount)
        {
            if (country == 0) return;
            power[country] = FP.Clamp(Of(country) + amount, LowerCap, UpperCap);
        }

        /// <summary>Daily, from Hoi4Simulation: every country (in the given order) gains its day's political power.</summary>
        public static void SimulateDay(IEnumerable<ushort> countries)
        {
            if (!Hoi4Modifiers.IsReady) return;
            foreach (ushort c in countries) Add(c, DailyGain(c));
        }
    }
}
