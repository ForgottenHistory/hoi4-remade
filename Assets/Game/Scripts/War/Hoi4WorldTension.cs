using System.Collections.Generic;
using Core;
using Core.Diplomacy;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.War
{
    /// <summary>
    /// GAME: HOI4 world tension (NDefines.NDiplomacy TENSION_*; the wiki's World tension page).
    /// - Tension is the sum of everything that raised or lowered it, each entry with the country that caused it.
    ///   Shown as 0..100%; the sum may pass 100, which then cushions later reductions.
    /// - Each entry decays towards 0 by TENSION_DECAY_DAILY (0.005) a day, but not while its country is at war.
    /// - A declaration of war adds TENSION_NO_CB_WAR (14: the game's wars have no war goals yet), scaled by the
    ///   defender's size and by time:
    ///   - size: the defender's factories over those of the second largest country (BASE_TENSION_MAJOR_COUNTRY_INDEX
    ///     1), between 0.05 and 1. HOI4 scales by production capacity too; its exact formula is not known here.
    ///   - time: 1 less TENSION_TIME_SCALE_MONTHLY_FACTOR (0.005) for every month since January 1936, at least
    ///     TENSION_TIME_SCALE_MIN (0.25).
    /// - Peace takes back TENSION_PEACE_FACTOR (0.8) of what the declaration added, less at high tension: x (1 -
    ///   TENSION_PEACE_FACTOR_THREAT_FACTOR 0.6 x tension). The define's wording allows other readings.
    /// - Scripts add to it (add_threat, add_named_threat) and ask for it (threat, has_added_tension_amount).
    /// - Effect: war support of every country + WAR_SUPPORT_TENSION_IMPACT (0.4) x tension (Hoi4Stability.WarSupport).
    /// - Not modelled: tension from annexing states, guarantees, factions, volunteers, justifying war goals; the
    ///   ideologies' tension limits on diplomatic actions.
    /// Part of the simulation: it changes on the daily tick, on wars and peace, and in script effects.
    /// </summary>
    public static class Hoi4WorldTension
    {
        public static readonly FP DecayDaily = FP.FromFraction(5, 1000);      // TENSION_DECAY_DAILY
        public static readonly FP NoCbWar = FP.FromInt(14);                    // TENSION_NO_CB_WAR
        public static readonly FP PeaceFactor = FP.FromFraction(8, 10);        // TENSION_PEACE_FACTOR
        public static readonly FP PeaceThreatFactor = FP.FromFraction(6, 10);  // TENSION_PEACE_FACTOR_THREAT_FACTOR
        public static readonly FP TimeScaleMonthly = FP.FromFraction(5, 1000); // TENSION_TIME_SCALE_MONTHLY_FACTOR
        public static readonly FP TimeScaleMin = FP.FromFraction(25, 100);     // TENSION_TIME_SCALE_MIN
        public static readonly FP WarSupportImpact = FP.FromFraction(4, 10);   // WAR_SUPPORT_TENSION_IMPACT
        static readonly FP Hundred = FP.FromInt(100);
        static readonly FP MinSize = FP.FromFraction(5, 100);

        sealed class Entry
        {
            public ushort Country;
            public FP Amount;
        }

        static readonly List<Entry> entries = new List<Entry>();
        static readonly Dictionary<(ushort, ushort), FP> warTension = new Dictionary<(ushort, ushort), FP>();   // (lower id, higher id) -> what the declaration added
        static GameState gameState;
        static System.IDisposable warSub, peaceSub;
        static FP total;

        /// <summary>Counts every change, for the UI.</summary>
        public static int Version { get; private set; }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            warSub?.Dispose(); peaceSub?.Dispose();
            warSub = peaceSub = null;
            entries.Clear(); warTension.Clear();
            gameState = null;
            total = FP.Zero;
            Version = 0;
        }

        /// <summary>Start listening for wars and peace. From Hoi4Simulation once the game state exists.</summary>
        public static void Initialize(GameState state)
        {
            if (gameState == state || state == null) return;
            Reset();
            gameState = state;
            warSub = state.EventBus.Subscribe<DiplomacyWarDeclaredEvent>(e => OnWar(e.attackerID, e.defenderID));
            peaceSub = state.EventBus.Subscribe<DiplomacyPeaceMadeEvent>(e => OnPeace(e.country1, e.country2));
        }

        /// <summary>World tension in percent, 0..100.</summary>
        public static FP Percent => FP.Clamp(total, FP.Zero, Hundred);

        /// <summary>World tension as a share, 0..1 (what scripts compare `threat` with).</summary>
        public static FP Share => Percent / Hundred;

        /// <summary>The tension a country has caused and that still stands, in percent points.</summary>
        public static FP GeneratedBy(ushort country)
        {
            FP sum = FP.Zero;
            foreach (var e in entries) if (e.Country == country) sum += e.Amount;
            return sum;
        }

        /// <summary>Add (or, negative, take off) tension in percent points, caused by a country (0: nobody).</summary>
        public static void Add(ushort country, FP amount, string why)
        {
            if (amount == FP.Zero) return;
            entries.Add(new Entry { Country = country, Amount = amount });
            Sum();
            Core.Recording.SessionRecorder.Note("tension", amount > FP.Zero ? "up" : "down", country, 0, 0, 0,
                $"{amount.ToDouble():+0.##;-0.##} ({why}), now {Percent.ToDouble():0.#}%");
        }

        static void Sum()
        {
            total = FP.Zero;
            foreach (var e in entries) total += e.Amount;
            Version++;
        }

        /// <summary>Daily, from Hoi4Simulation: every entry of a country at peace decays towards 0.</summary>
        public static void SimulateDay()
        {
            if (gameState == null || entries.Count == 0) return;
            var diplomacy = gameState.Diplomacy;
            bool changed = false;
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                var e = entries[i];
                if (e.Country != 0 && diplomacy != null && diplomacy.IsAtWar(e.Country)) continue;
                if (e.Amount > DecayDaily) e.Amount -= DecayDaily;
                else if (e.Amount < -DecayDaily) e.Amount += DecayDaily;
                else entries.RemoveAt(i);
                changed = true;
            }
            if (changed) Sum();
        }

        static void OnWar(ushort attacker, ushort defender)
        {
            FP amount = NoCbWar * SizeScale(defender) * TimeScale();
            warTension[Key(attacker, defender)] = amount;
            Add(attacker, amount, $"war on {gameState.Countries.GetCountryTag(defender)}");
        }

        static void OnPeace(ushort a, ushort b)
        {
            if (!warTension.TryGetValue(Key(a, b), out FP amount)) return;
            warTension.Remove(Key(a, b));
            FP back = amount * PeaceFactor * FP.Max(FP.Zero, FP.One - PeaceThreatFactor * Share);
            Add(0, -back, $"peace between {gameState.Countries.GetCountryTag(a)} and {gameState.Countries.GetCountryTag(b)}");
        }

        static (ushort, ushort) Key(ushort a, ushort b) => a < b ? (a, b) : (b, a);

        /// <summary>The country's factories over those of the second largest country, 0.05..1.</summary>
        static FP SizeScale(ushort country)
        {
            var construction = Hoi4.Construction.Hoi4Construction.Instance;
            var research = Hoi4.Research.Hoi4Research.Instance;
            if (construction == null || research == null) return FP.One;
            int first = 0, second = 0;
            foreach (ushort c in research.Countries)
            {
                int n = construction.Civilian(c) + construction.Military(c);
                if (n > first) { second = first; first = n; }
                else if (n > second) second = n;
            }
            if (second <= 0) return FP.One;
            int own = construction.Civilian(country) + construction.Military(country);
            return FP.Clamp(FP.FromFraction(own, second), MinSize, FP.One);
        }

        /// <summary>Tension from actions shrinks with time: 1 - 0.005 a month since January 1936, at least 0.25.</summary>
        static FP TimeScale()
        {
            var now = gameState.Time.GetCurrentGameTime();
            int months = System.Math.Max(0, (now.Year - 1936) * 12 + now.Month - 1);
            return FP.Max(TimeScaleMin, FP.One - TimeScaleMonthly * FP.FromInt(months));
        }
    }
}
