using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Newtonsoft.Json.Linq;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Modifiers
{
    /// <summary>
    /// GAME: HOI4 party politics, per country: the ruling ideology and each ideology's popularity (a share; the
    /// four add up to 1), from the 1936 history (common/hoi4_politics.json5). A country the file lacks is fully
    /// non-aligned.
    /// - AddPopularity moves one ideology's share (add_popularity) and takes it from, or gives it to, the others
    ///   in proportion to their shares.
    /// - Drift: each day an ideology gains its [ideology]_drift (summed over the country's ideas, Hoi4Modifiers),
    ///   in percent points a day as HOI4 shows it ("daily fascism support +0.02%").
    /// - SetRuling changes the government (set_politics).
    /// - Not modelled: elections, coups and civil wars at high support, the ideologies' own rules and modifiers,
    ///   sub-ideologies and leaders.
    /// Part of the simulation: changes come from the daily tick and from script effects.
    /// </summary>
    public static class Hoi4Politics
    {
        const string PoliticsFile = "common/hoi4_politics.json5";
        public static readonly string[] Ideologies = { "democratic", "communism", "fascism", "neutrality" };
        const int Neutrality = 3;
        static readonly FP Hundred = FP.FromInt(100);

        sealed class Country
        {
            public int Ruling = Neutrality;
            public readonly FP[] Share = { FP.Zero, FP.Zero, FP.Zero, FP.One };
        }

        static readonly Dictionary<ushort, Country> countries = new Dictionary<ushort, Country>();
        static readonly Country None = new Country();
        static ushort[] driftIds;
        static GameState loadedFor;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            countries.Clear();
            driftIds = null;
            loadedFor = null;
        }

        public static bool IsReady => loadedFor != null;

        /// <summary>Load the start politics. From Hoi4Simulation once the countries exist.</summary>
        public static void Initialize(GameState gameState)
        {
            if (loadedFor == gameState || gameState == null || !DataFileResolver.IsInitialized) return;
            Reset();
            loadedFor = gameState;
            string path = DataFileResolver.Resolve(PoliticsFile);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4Politics: no {PoliticsFile} (run Tools/hoi4_convert.py); every country is non-aligned", "game_economy");
                return;
            }
            foreach (var p in Json5Loader.LoadJson5File(path).Properties())
            {
                ushort id = gameState.Countries.GetCountryIdFromTag(p.Name);
                if (id == 0 || !(p.Value is JObject o)) continue;
                var c = new Country { Ruling = System.Math.Max(0, Index((string)o["ruling"])) };
                if (o["popularity"] is JObject pop)
                    for (int i = 0; i < Ideologies.Length; i++)
                        c.Share[i] = pop[Ideologies[i]] != null ? FP.FromDouble((double)pop[Ideologies[i]]) : FP.Zero;
                countries[id] = c;
            }
            ArchonLogger.Log($"Hoi4Politics: {countries.Count} countries", "game_economy");
        }

        /// <summary>An ideology's index in Ideologies, -1 when it is none of them.</summary>
        public static int Index(string ideology) => ideology == null ? -1 : System.Array.IndexOf(Ideologies, ideology);

        static Country Of(ushort country) => countries.TryGetValue(country, out var c) ? c : None;

        /// <summary>The ruling ideology (neutrality for a country without politics).</summary>
        public static string Ruling(ushort country) => Ideologies[Of(country).Ruling];

        /// <summary>An ideology's popularity, 0..1 (0 for a name that is no ideology).</summary>
        public static FP Popularity(ushort country, string ideology)
        {
            int i = Index(ideology);
            return i < 0 ? FP.Zero : Of(country).Share[i];
        }

        public static void SetRuling(ushort country, string ideology)
        {
            int i = Index(ideology);
            if (i < 0 || country == 0) return;
            var c = Mutable(country);
            if (c.Ruling == i) return;
            c.Ruling = i;
            Core.Recording.SessionRecorder.Note("politics", "government", country, 0, 0, 0, ideology);
        }

        /// <summary>Move an ideology's popularity by amount (a share: 0.1 = ten points), within 0..1. The others
        /// give or take the difference in proportion to their shares (evenly when they have none).</summary>
        public static void AddPopularity(ushort country, string ideology, FP amount)
        {
            int i = Index(ideology);
            if (i < 0 || country == 0 || amount == FP.Zero) return;
            var c = Mutable(country);
            FP now = FP.Clamp(c.Share[i] + amount, FP.Zero, FP.One);
            FP others = FP.One - c.Share[i];            // what the other three hold now
            FP left = FP.One - now;                     // and what they will hold
            for (int k = 0; k < c.Share.Length; k++)
            {
                if (k == i) continue;
                c.Share[k] = others > FP.Zero ? c.Share[k] * left / others : left / FP.FromInt(c.Share.Length - 1);
            }
            c.Share[i] = now;
        }

        static Country Mutable(ushort country)
        {
            if (!countries.TryGetValue(country, out var c)) countries[country] = c = new Country();
            return c;
        }

        /// <summary>Daily, from Hoi4Simulation: each country's ideologies drift by their [ideology]_drift.</summary>
        public static void SimulateDay(IEnumerable<ushort> all)
        {
            if (!IsReady || !Hoi4Modifiers.IsReady) return;
            if (driftIds == null)
            {
                driftIds = new ushort[Ideologies.Length];
                for (int i = 0; i < Ideologies.Length; i++) driftIds[i] = Hoi4Modifiers.Id(Ideologies[i] + "_drift");
            }
            foreach (ushort country in all)
                for (int i = 0; i < Ideologies.Length; i++)
                {
                    FP drift = Hoi4Modifiers.Sum(country, driftIds[i]);
                    if (drift != FP.Zero) AddPopularity(country, Ideologies[i], drift / Hundred);
                }
        }
    }
}
