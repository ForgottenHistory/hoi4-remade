using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Hoi4.Map;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Weather
{
    /// <summary>
    /// GAME: HOI4 weather, simulated daily (from Hoi4Simulation, before supply). Data: each strategic region's weather
    /// periods (map/hoi4_weather.json5, from map/strategicregions); rules: common/weather.txt and the wiki's Weather page.
    /// Prototype: Tools/prototypes/weather/weather.py (the 1936 seasons it gives: Pomerania snowed in by February,
    /// spring mud from the thaw, Ukraine's spring and autumn mud, northern France's wet winters).
    ///
    ///   - Weather event per strategic region (none, light rain, heavy rain, snow, blizzard, sandstorm): picked by the
    ///     month's period weights, lasting a `duration` (random in [min, max], the smaller of two rolls). OURS: one roll
    ///     per region (HOI4 drifts weather systems across regions).
    ///   - Temperature per province, inside the period's range plus the terrain bias (mountain -7, hills -3, forest -1,
    ///     urban +3, jungle +5): each day up to 2 degrees of noise and half the way to the region's target, at most 6,
    ///     then 30% toward its land neighbours (temperature_variation, max_temperature_change,
    ///     temperature_neighbor_smoothing). OURS: the region target drifts at random in the range, a fifth back to the
    ///     middle a day.
    ///   - Snow: snowing (blizzard) at 1 degree or colder, a province gains 1 (5) a day, full from -4 down, scaled by
    ///     terrain (mountain +80%, urban -40%, desert none), up to 80. 20-40 is ground snow, 40+ deep snow. Not snowing,
    ///     from 0 degrees it melts, 0.1% (0 C) to 90% (12 C) a day, into collected water. Warm, a snowfall is rain.
    ///   - Water: rain gives 0.5 (heavy 1.8) a day, times the period's mud factor and the terrain (plains +50%, marsh
    ///     +90%, mountain -30%), less with infrastructure (-80% at level 5, INFRASTRUCTURE_MUD_EFFECT), up to 200. From
    ///     2 degrees it dries 0.05% (2 C) to 40% (38 C) a day, a quarter of that on rainy days (OURS: weather.txt dries
    ///     only dry days, which left Scotland, Norway and the Baltic in mud all summer); dry days under 5 it is gone.
    ///     Mud from 15 water, until under 13.5 (OURS, from chance_increase mud { 15 0.10 }); frozen at -1 or colder.
    ///
    /// Effects (common/modifiers/00_static_modifiers.txt), summed per province: SupplyFactor (hub supply into it),
    /// ConsumptionFactor (divisions' supply use), SpeedFactor, AttackFactor, Attrition (counted only at war).
    /// Deterministic: fixed point, provinces and regions in id order, a hash of (day, region or province, use) for
    /// every random number (no stored random state).
    /// </summary>
    public static class Hoi4Weather
    {
        const string WeatherFile = "map/hoi4_weather.json5";
        public enum Kind : byte { None, RainLight, RainHeavy, Snow, Blizzard, Sandstorm }
        static readonly int[,] Duration = { { 5, 12 }, { 5, 20 }, { 2, 15 }, { 5, 20 }, { 2, 15 }, { 2, 10 } };

        static readonly FP SnowMax = FP.FromInt(80), SnowMedium = FP.FromInt(20), SnowHigh = FP.FromInt(40);
        static readonly FP WaterMax = FP.FromInt(200), WaterMin = FP.FromInt(5);
        static readonly FP MudAt = FP.FromInt(15), MudKeep = FP.FromFraction(135, 10), MudFrozen = FP.FromInt(-1);
        static readonly FP RainLight = FP.Half, RainHeavy = FP.FromFraction(18, 10);
        static readonly FP SnowFall = FP.One, BlizzardFall = FP.FromInt(5);
        static readonly FP MaxChange = FP.FromInt(6), Variation = FP.FromInt(2), TargetStep = FP.FromInt(3);
        static readonly FP NeighbourSmoothing = FP.FromFraction(3, 10);
        static readonly FP InfraMud = FP.FromFraction(8, 10);
        static readonly FP RainDrain = FP.FromFraction(1, 4);
        const int MaxInfra = 5;
        const int WarmupDays = 30; // December before the start (HOI4 runs init_run_passes before the game)

        static bool loaded, available;
        // regions
        static int[] regionIds;                          // index -> strategic region id
        static List<int[]>[] periods;                    // index -> rows from the data file
        static Kind[] kind;
        static int[] left;
        static FP[] target;
        // provinces (by id)
        static int size;
        static int[] regionOf;                           // -1 = no weather (water, unknown)
        static FP[] temp, next, snow, water, bias, snowMod, mudMod;
        static bool[] mud, hasTemp;
        static int[] off;
        static ushort[] nbr;
        static int dayCount;

        public static bool Available => loaded && available;
        /// <summary>Bumps every simulated day (presentation redraws when it changes).</summary>
        public static int Version { get; private set; }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            loaded = available = false;
            regionIds = null; periods = null; kind = null; left = null; target = null;
            regionOf = null; temp = next = snow = water = bias = snowMod = mudMod = null; mud = hasTemp = null;
            off = null; nbr = null; dayCount = 0; Version = 0;
        }

        // --- queries -------------------------------------------------------------------------------------

        static bool Has(ushort p) => available && p < size && regionOf[p] >= 0 && hasTemp[p];

        public static Kind KindAt(ushort p) => Has(p) ? kind[regionOf[p]] : Kind.None;
        public static FP Temperature(ushort p) => Has(p) ? temp[p] : FP.FromInt(15);
        public static FP Snow(ushort p) => Has(p) ? snow[p] : FP.Zero;
        public static FP Water(ushort p) => Has(p) ? water[p] : FP.Zero;
        /// <summary>Mud that isn't frozen.</summary>
        public static bool IsMud(ushort p) => Has(p) && mud[p] && temp[p] > MudFrozen;
        /// <summary>0 none, 1 ground snow (20-40), 2 deep snow (40+).</summary>
        public static int GroundSnow(ushort p) => !Has(p) ? 0 : snow[p] >= SnowHigh ? 2 : snow[p] >= SnowMedium ? 1 : 0;
        /// <summary>-2 extremely cold (-12 and under), -1 very cold (to -4), 0, 1 very hot (20 to 28), 2 extremely hot.</summary>
        public static int TemperatureBand(ushort p)
        {
            if (!Has(p)) return 0;
            FP t = temp[p];
            return t <= FP.FromInt(-12) ? -2 : t <= FP.FromInt(-4) ? -1 : t >= FP.FromInt(28) ? 2 : t >= FP.FromInt(20) ? 1 : 0;
        }

        /// <summary>Hub supply flowing into the province: 1 + supply_factor of the region's weather, the ground and the
        /// temperature (snow -10%, blizzard -25%, heavy rain -5%, sandstorm -15%; ground snow -20%, deep -40%, mud
        /// -15%; extremely cold -15%, very hot -5%), at least 0.</summary>
        public static FP SupplyFactor(ushort p)
        {
            if (!Has(p)) return FP.One;
            int s = 0; // percent
            switch (kind[regionOf[p]])
            {
                case Kind.RainHeavy: s -= 5; break;
                case Kind.Snow: s -= 10; break;
                case Kind.Blizzard: s -= 25; break;
                case Kind.Sandstorm: s -= 15; break;
            }
            int g = GroundSnow(p);
            s -= g == 2 ? 40 : g == 1 ? 20 : 0;
            if (IsMud(p)) s -= 15;
            int band = TemperatureBand(p);
            if (band == -2) s -= 15;
            if (band == 1) s -= 5;
            return FP.Max(FP.Zero, FP.One + FP.FromFraction(s, 100));
        }

        /// <summary>A division's supply use there: 1 + supply_consumption_factor (heavy rain +10%, blizzard +15%,
        /// extremely hot +50%).</summary>
        public static FP ConsumptionFactor(ushort p)
        {
            if (!Has(p)) return FP.One;
            int s = 0;
            var k = kind[regionOf[p]];
            if (k == Kind.RainHeavy) s += 10;
            if (k == Kind.Blizzard) s += 15;
            if (TemperatureBand(p) == 2) s += 50;
            return FP.One + FP.FromFraction(s, 100);
        }

        /// <summary>Movement: 1 + army_speed_factor (ground snow -30%, deep -70%, mud -50%), at least 0.2.</summary>
        public static FP SpeedFactor(ushort p)
        {
            if (!Has(p)) return FP.One;
            int s = 0, g = GroundSnow(p);
            s -= g == 2 ? 70 : g == 1 ? 30 : 0;
            if (IsMud(p)) s -= 50;
            return FP.Max(FP.FromFraction(2, 10), FP.One + FP.FromFraction(s, 100));
        }

        /// <summary>Attack of the divisions fighting there: 1 + army_attack_factor (ground snow -20%, deep -40%, mud -40%,
        /// extremely cold -20%, very cold -10%, extremely hot -10%), at least 0.2.</summary>
        public static FP AttackFactor(ushort p)
        {
            if (!Has(p)) return FP.One;
            int s = 0, g = GroundSnow(p);
            s -= g == 2 ? 40 : g == 1 ? 20 : 0;
            if (IsMud(p)) s -= 40;
            int band = TemperatureBand(p);
            s -= band == -2 ? 20 : band == -1 ? 10 : band == 2 ? 10 : 0;
            return FP.Max(FP.FromFraction(2, 10), FP.One + FP.FromFraction(s, 100));
        }

        /// <summary>Attrition from the weather (HOI4 counts it only when the controller is at war): mud 0.7, winter
        /// attrition 0.4 extremely cold / 0.25 very cold, heat 0.1 very hot / 0.2 extremely hot.</summary>
        public static FP Attrition(ushort p)
        {
            if (!Has(p)) return FP.Zero;
            int s = IsMud(p) ? 70 : 0;
            switch (TemperatureBand(p))
            {
                case -2: s += 40; break;
                case -1: s += 25; break;
                case 1: s += 10; break;
                case 2: s += 20; break;
            }
            return FP.FromFraction(s, 100);
        }

        // --- simulation ----------------------------------------------------------------------------------

        public static void SimulateDay(GameState gameState)
        {
            if (gameState == null || !EnsureLoaded(gameState)) return;
            var time = gameState.Time;
            if (dayCount == 0)
                for (int d = 1; d <= WarmupDays; d++) Step(gameState, 12, d);
            Step(gameState, time.CurrentMonth, time.CurrentDay);
            Version++;
        }

        static void Step(GameState gs, int month, int day)
        {
            dayCount++;
            // regions: event and temperature target
            var current = new int[regionIds.Length][];
            for (int r = 0; r < regionIds.Length; r++)
            {
                var per = PeriodFor(periods[r], month, day);
                current[r] = per;
                if (per == null) continue;
                FP lo = FP.FromFraction(per[4], 10), hi = FP.FromFraction(per[5], 10), mid = (lo + hi) / 2;
                target[r] = FP.Clamp(target[r] + Uniform(1, regionIds[r], -TargetStep, TargetStep) + (mid - target[r]) / 5, lo, hi);
                if (left[r] <= 0)
                {
                    int total = 0;
                    for (int k = 0; k < 6; k++) total += per[6 + k];
                    int roll = total > 0 ? (int)(Rand(2, regionIds[r]) % (ulong)total) : 0;
                    int pick = 0;
                    for (int k = 0; k < 6; k++)
                    {
                        roll -= per[6 + k];
                        if (roll < 0) { pick = k; break; }
                    }
                    kind[r] = (Kind)pick;
                    int a = RandInt(3, regionIds[r], Duration[pick, 0], Duration[pick, 1]);
                    int b = RandInt(4, regionIds[r], Duration[pick, 0], Duration[pick, 1]);
                    left[r] = System.Math.Min(a, b);
                }
                left[r]--;
            }
            // provinces: temperature, then smoothing toward the neighbours
            for (int p = 1; p < size; p++)
            {
                int r = regionOf[p];
                if (r < 0 || current[r] == null) continue;
                var per = current[r];
                FP lo = FP.FromFraction(per[4], 10) + bias[p], hi = FP.FromFraction(per[5], 10) + bias[p];
                FP goal = target[r] + bias[p];
                FP t = hasTemp[p] ? temp[p] : goal;
                FP change = FP.Clamp(Uniform(5, p, -Variation, Variation) + (goal - t) / 2, -MaxChange, MaxChange);
                next[p] = FP.Clamp(t + change, lo, hi);
            }
            for (int p = 1; p < size; p++)
            {
                int r = regionOf[p];
                if (r < 0 || current[r] == null) continue;
                FP sum = FP.Zero;
                int n = 0;
                for (int i = off[p]; i < off[p + 1]; i++)
                {
                    int q = nbr[i];
                    if (regionOf[q] < 0 || current[regionOf[q]] == null) continue;
                    sum += next[q];
                    n++;
                }
                temp[p] = n > 0 ? next[p] + (sum / n - next[p]) * NeighbourSmoothing : next[p];
                hasTemp[p] = true;
            }
            // provinces: snow, water, mud
            for (int p = 1; p < size; p++)
            {
                int r = regionOf[p];
                if (r < 0 || current[r] == null) continue;
                FP t = temp[p];
                var k = kind[r];
                bool snowing = (k == Kind.Snow || k == Kind.Blizzard) && t <= FP.One;
                bool raining = k == Kind.RainLight || k == Kind.RainHeavy || ((k == Kind.Snow || k == Kind.Blizzard) && !snowing);
                if (snowing)
                {
                    FP rate = FP.Clamp((FP.One - t) / 5, FP.Zero, FP.One); // full from -4
                    snow[p] = FP.Min(SnowMax, snow[p] + (k == Kind.Blizzard ? BlizzardFall : SnowFall) * rate * snowMod[p]);
                }
                else if (snow[p] > FP.Zero && t >= FP.Zero)
                {
                    FP melt = snow[p] * Lerp(t, FP.Zero, FP.FromInt(12), FP.FromFraction(1, 1000), FP.FromFraction(9, 10));
                    snow[p] -= melt;
                    water[p] += melt;
                }
                if (raining)
                {
                    int state = Hoi4Regions.State((ushort)p);
                    int infra = state > 0 ? System.Math.Min(MaxInfra, Hoi4.Construction.Hoi4Buildings.Level(state, "infrastructure")) : 0;
                    FP gain = (k == Kind.RainHeavy || k == Kind.Blizzard) ? RainHeavy : RainLight;
                    water[p] += gain * FP.FromFraction(current[r][12], 1000) * mudMod[p] * (FP.One - InfraMud * infra / MaxInfra);
                }
                if (t >= FP.FromInt(2)) // rainy days drain at a quarter of the rate (OURS: else cool wet summers stay mud)
                    water[p] -= water[p] * Lerp(t, FP.FromInt(2), FP.FromInt(38), FP.FromFraction(5, 10000), FP.FromFraction(4, 10))
                                * (raining ? RainDrain : FP.One);
                water[p] = FP.Min(WaterMax, water[p]);
                if (!raining && water[p] < WaterMin) water[p] = FP.Zero;
                if (mud[p]) { if (water[p] < MudKeep) mud[p] = false; }
                else if (water[p] >= MudAt) mud[p] = true;
            }
        }

        static int[] PeriodFor(List<int[]> list, int month, int day)
        {
            if (list == null || list.Count == 0) return null;
            int key = month * 100 + day;
            foreach (var per in list)
                if (key >= per[0] * 100 + per[1] && key <= per[2] * 100 + per[3]) return per;
            return list[0];
        }

        static FP Lerp(FP x, FP x0, FP x1, FP y0, FP y1) => y0 + (y1 - y0) * FP.Clamp((x - x0) / (x1 - x0), FP.Zero, FP.One);

        // --- deterministic random numbers ----------------------------------------------------------------

        static ulong Rand(int use, int key)
        {
            ulong x = ((ulong)(uint)dayCount << 32) ^ ((ulong)(uint)key << 4) ^ (ulong)use ^ 0x9E3779B97F4A7C15UL;
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            return x ^ (x >> 31);
        }

        static FP Uniform(int use, int key, FP lo, FP hi) =>
            lo + (hi - lo) * FP.FromFraction((long)(Rand(use, key) % 1000000UL), 1000000);

        static int RandInt(int use, int key, int lo, int hi) => lo + (int)(Rand(use, key) % (ulong)(hi - lo + 1));

        // --- loading -------------------------------------------------------------------------------------

        static bool EnsureLoaded(GameState gameState)
        {
            if (loaded) return available;
            if (!DataFileResolver.IsInitialized || !Hoi4Regions.Available || gameState.Adjacencies == null) return false;
            loaded = true;
            string path = DataFileResolver.Resolve(WeatherFile);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning("Hoi4Weather: map/hoi4_weather.json5 missing (rerun Tools/hoi4_convert.py), no weather", "game_units");
                return available = false;
            }
            var byRegion = new SortedDictionary<int, List<int[]>>();
            try
            {
                JObject root = Json5Loader.LoadJson5File(path);
                if (root["regions"] is JObject regions)
                    foreach (var prop in regions.Properties())
                    {
                        var list = new List<int[]>();
                        foreach (var row in (JArray)prop.Value)
                        {
                            var a = (JArray)row;
                            var v = new int[a.Count];
                            for (int i = 0; i < a.Count; i++) v[i] = (int)a[i];
                            if (v.Length >= 13) list.Add(v);
                        }
                        byRegion[int.Parse(prop.Name)] = list;
                    }
            }
            catch (System.Exception e)
            {
                ArchonLogger.LogWarning($"Hoi4Weather: could not read {path}: {e.Message}", "game_units");
                return available = false;
            }

            regionIds = new int[byRegion.Count];
            periods = new List<int[]>[byRegion.Count];
            var index = new Dictionary<int, int>();
            int ri = 0;
            foreach (var kv in byRegion) { regionIds[ri] = kv.Key; periods[ri] = kv.Value; index[kv.Key] = ri; ri++; }
            kind = new Kind[ri];
            left = new int[ri];
            target = new FP[ri];
            for (int r = 0; r < ri; r++)
            {
                var first = periods[r].Count > 0 ? periods[r][0] : null;
                target[r] = first != null ? FP.FromFraction(first[4] + first[5], 20) : FP.Zero;
            }

            gameState.Adjacencies.GetCompactGraph(out off, out nbr);
            size = off.Length - 1;
            regionOf = new int[size];
            temp = new FP[size]; next = new FP[size]; snow = new FP[size]; water = new FP[size];
            bias = new FP[size]; snowMod = new FP[size]; mudMod = new FP[size];
            mud = new bool[size]; hasTemp = new bool[size];
            int land = 0;
            for (int p = 0; p < size; p++)
            {
                regionOf[p] = -1;
                if (p == 0 || Hoi4Regions.IsWater((ushort)p)) continue;
                int region = Hoi4Regions.Region((ushort)p);
                if (!index.TryGetValue(region, out int r)) continue;
                regionOf[p] = r;
                land++;
                string terrain = Hoi4CombatTerrain.TerrainKey(gameState, (ushort)p) ?? "";
                // weather.txt terrain_modifiers: temperature in degrees, snow and mud as chance increases
                bias[p] = FP.FromInt(terrain == "mountain" ? -7 : terrain == "hills" ? -3 : terrain == "forest" ? -1
                                     : terrain == "urban" ? 3 : terrain == "jungle" ? 5 : 0);
                snowMod[p] = FP.FromFraction(terrain == "mountain" ? 180 : terrain == "urban" ? 60 : terrain == "desert" ? 0 : 100, 100);
                mudMod[p] = FP.FromFraction(terrain == "mountain" ? 70 : terrain == "plains" ? 150 : terrain == "marsh" ? 190
                                            : terrain == "desert" ? 110 : terrain == "jungle" ? 120 : 100, 100);
            }
            ArchonLogger.Log($"Hoi4Weather: {ri} strategic regions, weather in {land} land provinces", "game_units");
            return available = land > 0;
        }
    }
}
