using System.Collections;
using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Research
{
    /// <summary>One research slot. Technology is null while the slot is empty.</summary>
    public sealed class ResearchSlot
    {
        public string Technology;
        /// <summary>Days of research saved while the slot sat empty (at most SavedMaxDays), spent on the next pick.</summary>
        public FP Saved;
    }

    /// <summary>
    /// GAME: HOI4 research slots (wiki "Research", NDefines.NCountry / NTechnology).
    /// - Slots: set_research_slots in the country's 1936 history, else BASE_RESEARCH_SLOTS (2), from
    ///   common/hoi4_country_economy.json5. Checked against real HOI4: Germany 4.
    /// - Research speed: research_speed_factor summed over the 1936 ideas and technologies (Germany +1%).
    /// - An empty slot saves a day of research each day, up to BASE_RESEARCH_POINTS_SAVED (30). The saved days go to
    ///   the next technology picked for that slot.
    /// - Not modelled yet: technologies, picking one, progress, the experimental facilities and limited bonuses.
    /// </summary>
    public sealed class Hoi4Research : MonoBehaviour
    {
        const string EconomyFile = "common/hoi4_country_economy.json5";
        public const int BaseSlots = 2;                              // NCountry BASE_RESEARCH_SLOTS
        public static readonly FP SavedMaxDays = FP.FromInt(30);     // NTechnology BASE_RESEARCH_POINTS_SAVED

        public static Hoi4Research Instance { get; private set; }
        public bool IsReady { get; private set; }

        sealed class CountryResearch
        {
            public readonly List<ResearchSlot> Slots = new List<ResearchSlot>();
            public FP Speed;
        }

        GameState gameState;
        readonly SortedDictionary<ushort, CountryResearch> countries = new SortedDictionary<ushort, CountryResearch>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Research");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Research>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || !DataFileResolver.IsInitialized
                   // the countries must exist before their tags resolve
                   || Hoi4.Units.Hoi4Divisions.Instance == null || !Hoi4.Units.Hoi4Divisions.Instance.IsLoaded)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;

            string path = DataFileResolver.Resolve(EconomyFile);
            if (File.Exists(path))
                foreach (var p in Json5Loader.LoadJson5File(path).Properties())
                {
                    ushort country = gameState.Countries.GetCountryIdFromTag(p.Name);
                    if (country == 0 || !(p.Value is JObject o)) continue;
                    int slots = o["research_slots"] != null ? (int)o["research_slots"] : BaseSlots;
                    var c = Create(country, slots);
                    c.Speed = o["research_speed"] != null ? FP.FromDouble((double)o["research_speed"]) : FP.Zero;
                }
            else
                ArchonLogger.LogWarning($"Hoi4Research: no {EconomyFile} (run Tools/hoi4_convert.py); every country has {BaseSlots} slots", "game_research");
            IsReady = true;
            ArchonLogger.Log($"Hoi4Research: {countries.Count} countries", "game_research");
        }

        CountryResearch Create(ushort country, int slots)
        {
            var c = new CountryResearch();
            for (int i = 0; i < Mathf.Max(0, slots); i++) c.Slots.Add(new ResearchSlot());
            countries[country] = c;
            return c;
        }

        // countries missing from the economy file have no research state; queries read them as empty (a query must
        // never add simulation state: the UI asks on one client only)
        static readonly CountryResearch None = new CountryResearch();

        CountryResearch Of(ushort country) => countries.TryGetValue(country, out var c) ? c : None;

        // ---- queries ------------------------------------------------------------------------------------

        public IReadOnlyList<ResearchSlot> Slots(ushort country) => Of(country).Slots;

        /// <summary>Research speed bonus (0.01 = +1%).</summary>
        public FP Speed(ushort country) => Of(country).Speed;

        public int EmptySlots(ushort country)
        {
            int n = 0;
            foreach (var s in Of(country).Slots) if (s.Technology == null) n++;
            return n;
        }

        // ---- daily --------------------------------------------------------------------------------------

        /// <summary>Daily, from Hoi4Simulation: every empty slot saves a day of research, up to SavedMaxDays.</summary>
        public void SimulateDay()
        {
            if (!IsReady) return;
            foreach (var c in countries.Values)
                foreach (var s in c.Slots)
                    if (s.Technology == null && s.Saved < SavedMaxDays)
                        s.Saved = FP.Min(SavedMaxDays, s.Saved + FP.One);
        }
    }
}
