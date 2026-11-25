using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Core.Modifiers;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Modifiers
{
    /// <summary>
    /// GAME: HOI4 stability and landmarks, as country modifier sources (Hoi4Modifiers.SetSource).
    /// - Stability: the country's set_stability (common/hoi4_country_stats.json5) + the sum of its stability_factor
    ///   (ideas, landmarks), clamped to 0..1. Germany: 0.70 + 0.05 (Reichstag) = 0.75.
    /// - Above 50% stability_good_modifier applies scaled by (stability - 0.5) / 0.5, below 50% stability_bad_modifier
    ///   by (0.5 - stability) / 0.5 (common/hoi4_static_modifiers.json5): factory output +20% / -50% at the extremes,
    ///   consumer goods factor -20% at 100%. Measured on a real German game: output moves 0.4 per point of
    ///   stability (Tools/reference/hoi4_production_modifiers.py), and day one's consumer goods (15 of 63) need the
    ///   full -20% x scale beside Germany's own +10% (a dynamic modifier not modelled here).
    /// - Landmarks (common/hoi4_landmarks.json5): a province building whose country modifiers go to the province's
    ///   controller while it is in the landmark's list (any controller when the list is empty). Rechecked when
    ///   province control changes.
    /// - Not modelled: stability changes from events, focuses and decisions, war support's stability penalty in war,
    ///   political power.
    /// </summary>
    public static class Hoi4Stability
    {
        const string StatsFile = "common/hoi4_country_stats.json5";
        const string StaticFile = "common/hoi4_static_modifiers.json5";
        const string LandmarksFile = "common/hoi4_landmarks.json5";
        const uint GoodSource = 1, BadSource = 2;   // Custom sources
        static readonly FP Neutral = FP.Half;       // no stability modifier at 50%

        sealed class Landmark
        {
            public ushort Province;
            public string Building;
            public HashSet<ushort> Controllers;      // null: any controller
            public List<KeyValuePair<string, FP>> Modifiers;
            public ushort Holder;
        }

        static readonly Dictionary<ushort, FP> baseStability = new Dictionary<ushort, FP>();
        static readonly Dictionary<ushort, FP> appliedScale = new Dictionary<ushort, FP>();
        static readonly List<Landmark> landmarks = new List<Landmark>();
        static List<KeyValuePair<string, FP>> good = new List<KeyValuePair<string, FP>>();
        static List<KeyValuePair<string, FP>> bad = new List<KeyValuePair<string, FP>>();
        static GameState gameState;
        static int controlVersion = int.MinValue;
        static bool refreshing;

        public static bool IsReady { get; private set; }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            Hoi4Modifiers.Changed -= OnChanged;
            baseStability.Clear();
            appliedScale.Clear();
            landmarks.Clear();
            good = new List<KeyValuePair<string, FP>>();
            bad = new List<KeyValuePair<string, FP>>();
            gameState = null;
            controlVersion = int.MinValue;
            refreshing = false;
            IsReady = false;
        }

        /// <summary>After Hoi4Modifiers has the ideas and technologies (Hoi4Research's start): base stability,
        /// landmarks to their controllers, every country's stability modifier.</summary>
        public static void Initialize(GameState state)
        {
            Reset();
            gameState = state;
            var countries = state.Countries;

            string path = DataFileResolver.Resolve(StatsFile);
            if (File.Exists(path))
                foreach (var p in Json5Loader.LoadJson5File(path).Properties())
                {
                    ushort c = countries.GetCountryIdFromTag(p.Name);
                    if (c != 0 && p.Value["stability"] != null) baseStability[c] = FP.FromDouble((double)p.Value["stability"]);
                }
            path = DataFileResolver.Resolve(StaticFile);
            if (File.Exists(path))
            {
                var o = Json5Loader.LoadJson5File(path);
                good = Modifiers(o["stability_good_modifier"]);
                bad = Modifiers(o["stability_bad_modifier"]);
            }
            else ArchonLogger.LogWarning($"Hoi4Stability: no {StaticFile} (run Tools/hoi4_convert.py); stability has no effect", "game_economy");

            path = DataFileResolver.Resolve(LandmarksFile);
            string text = File.Exists(path) ? File.ReadAllText(path) : null;
            int open = text != null ? text.IndexOf('[') : -1; // a list after the comment line
            if (open >= 0 && JToken.Parse(text.Substring(open)) is JArray list)
                foreach (var l in list)
                {
                    HashSet<ushort> ctrl = null;
                    if (l["controllers"] is JArray tags && tags.Count > 0)
                    {
                        ctrl = new HashSet<ushort>();
                        foreach (var t in tags)
                        {
                            ushort c = countries.GetCountryIdFromTag((string)t);
                            if (c != 0) ctrl.Add(c);
                        }
                    }
                    landmarks.Add(new Landmark
                    {
                        Province = (ushort)(int)l["province"], Building = (string)l["building"], Controllers = ctrl,
                        Modifiers = Modifiers(l["modifiers"]),
                    });
                }

            Hoi4Modifiers.Changed += OnChanged;
            UpdateLandmarks(notify: false);
            var all = new SortedSet<ushort>(baseStability.Keys);
            foreach (var l in landmarks) if (l.Holder != 0) all.Add(l.Holder);
            foreach (ushort c in all) Refresh(c, notify: false);
            IsReady = true;
            ArchonLogger.Log($"Hoi4Stability: {baseStability.Count} countries, {landmarks.Count} landmarks", "game_economy");
        }

        static List<KeyValuePair<string, FP>> Modifiers(JToken o)
        {
            var list = new List<KeyValuePair<string, FP>>();
            if (o is JObject m)
                foreach (var p in m.Properties()) list.Add(new KeyValuePair<string, FP>(p.Name, FP.FromDouble((double)p.Value)));
            return list;
        }

        /// <summary>The country's stability, 0..1 (50% for a country without history).</summary>
        public static FP Stability(ushort country)
        {
            FP b = baseStability.TryGetValue(country, out FP v) ? v : Neutral;
            return FP.Clamp(b + Hoi4Modifiers.Sum(country, Hoi4Modifiers.StabilityFactor), FP.Zero, FP.One);
        }

        /// <summary>Daily, from Hoi4Simulation: landmarks follow province control.</summary>
        public static void SimulateDay()
        {
            if (!IsReady || gameState == null || gameState.Provinces.ControlVersion == controlVersion) return;
            UpdateLandmarks(notify: true);
        }

        static void UpdateLandmarks(bool notify)
        {
            controlVersion = gameState.Provinces.ControlVersion;
            for (int i = 0; i < landmarks.Count; i++)
            {
                var l = landmarks[i];
                ushort ctrl = gameState.Provinces.GetProvinceController(l.Province);
                ushort holder = ctrl != 0 && (l.Controllers == null || l.Controllers.Contains(ctrl)) ? ctrl : (ushort)0;
                if (holder == l.Holder) continue;
                ushort old = l.Holder;
                l.Holder = holder;
                if (old != 0 && Hoi4Modifiers.SetSource(old, ModifierSource.SourceType.Building, (uint)i, null) && notify)
                    Hoi4Modifiers.NotifyChanged(old);      // OnChanged refreshes its stability
                if (holder != 0 && Hoi4Modifiers.SetSource(holder, ModifierSource.SourceType.Building, (uint)i, l.Modifiers) && notify)
                    Hoi4Modifiers.NotifyChanged(holder);
            }
        }

        static void OnChanged(ushort country)
        {
            if (!refreshing) Refresh(country, notify: true);
        }

        /// <summary>Stability's scaled static modifier, reapplied when the scale moved.</summary>
        static void Refresh(ushort country, bool notify)
        {
            FP s = Stability(country);
            FP scale = (s - Neutral) / Neutral;   // -1..1: bad below 50%, good above
            if (appliedScale.TryGetValue(country, out FP was) && was == scale) return;
            appliedScale[country] = scale;
            refreshing = true;
            bool changed = Hoi4Modifiers.SetSource(country, ModifierSource.SourceType.Custom, GoodSource,
                                                   scale > FP.Zero ? Scaled(good, scale) : null);
            changed |= Hoi4Modifiers.SetSource(country, ModifierSource.SourceType.Custom, BadSource,
                                               scale < FP.Zero ? Scaled(bad, -scale) : null);
            refreshing = false;
            if (changed && notify) Hoi4Modifiers.NotifyChanged(country);
        }

        static IEnumerable<KeyValuePair<string, FP>> Scaled(List<KeyValuePair<string, FP>> mods, FP k)
        {
            foreach (var kv in mods) yield return new KeyValuePair<string, FP>(kv.Key, kv.Value * k);
        }
    }
}
