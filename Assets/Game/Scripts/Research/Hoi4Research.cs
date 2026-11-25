using System.Collections;
using System.Collections.Generic;
using System.IO;
using Core;
using Core.Data;
using Core.Loaders;
using Core.Modding;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Research
{
    /// <summary>One technology of the tech tree (common/hoi4_technologies.json5).</summary>
    public sealed class TechDef
    {
        public int Index;
        public string Id;            // infantry_weapons1
        public string Name;          // Infantry Equipment I
        public FP Cost;              // research_cost (x BaseTechCost = research points)
        public int Year;             // start_year: ahead of 1 January of it costs time
        public string Folder;
        public float X, Y;           // grid position in the folder
        /// <summary>Every folder it shows in, with its grid position there (motorised_infantry: infantry and support).</summary>
        public readonly List<(string folder, float x, float y)> Places = new List<(string, float, float)>();
        public readonly List<int> Children = new List<int>();    // leads_to
        public string EquipmentName;                              // the tree card's short name ("Basic Eq.")
        public Dictionary<string, string> EquipmentNames;         // per country tag ("ALZ 13"), null when none
        public readonly List<int> Parents = new List<int>();     // techs that lead here: any one is enough
        public readonly List<FP> ParentCoeff = new List<FP>();    // the path's research_cost_coeff, per parent
        public readonly List<int> Requires = new List<int>();    // dependencies: every one needed
        public readonly List<int> Excludes = new List<int>();    // xor
        public readonly List<string> Equipment = new List<string>();
        public readonly List<string> SubUnits = new List<string>();
        public readonly Dictionary<string, int> Buildings = new Dictionary<string, int>();
        public readonly Dictionary<string, FP> Modifiers = new Dictionary<string, FP>();
        public JObject Ai;           // ai_will_do: base and modifiers with their triggers (Hoi4ResearchAI)
    }

    /// <summary>One research slot. Technology is -1 while the slot is empty. Its tech's progress is SyncProgress at the
    /// end of SyncDay plus each later day's rate; FinishDay is the day that reaches the cost (Hoi4Research books it).</summary>
    public sealed class ResearchSlot
    {
        public int Technology = -1;
        public int EmptySince;       // day it emptied (saved days: min(30, days since))
        public int SyncDay;          // day SyncProgress was taken, after that day's research
        public FP SyncProgress;
        public int FinishDay;
        public bool Empty => Technology < 0;
    }

    /// <summary>
    /// GAME: HOI4 research (NDefines.NCountry / NTechnology; prototype Tools/prototypes/research/research_cost.py, checked
    /// against a real 1936 German game's daily save blocks).
    /// - Technologies: the tech tree folders' technologies (common/hoi4_technologies.json5, every DLC assumed owned), each
    ///   country's 1936 ones from its history.
    /// - Slots: set_research_slots in the 1936 history, else BASE_RESEARCH_SLOTS (2). Research speed: research_speed_factor
    ///   of the country's ideas and technologies, from the modifier system (Hoi4Modifiers; Germany +1% at the start);
    ///   a change rebooks its slots.
    /// - A tech needs research_cost x BASE_TECH_COST (110) points, x the research_cost_coeff of the path from a parent
    ///   it has (the cheapest). It can be researched once any one of the techs leading to it is known (a tech nothing
    ///   leads to: always), every dependency is known, nothing it excludes (xor) is known or in a slot, and no other
    ///   slot has it.
    /// - A slot gains each day max(MIN_RESEARCH_SPEED 0.1, 1 + research speed) / (1 + 2 x years ahead), years ahead
    ///   counted from today to 1 January of the tech's start_year (BASE_YEAR_AHEAD_PENALTY_FACTOR 2), so research ahead
    ///   of time speeds up as the year nears. The real game matches this within a day's reading.
    /// - Progress stays with the tech when it leaves its slot (HOI4 keeps research_points), and the tech goes on from it.
    /// - An empty slot saves a day of research each day, up to BASE_RESEARCH_POINTS_SAVED (30). Picking a tech spends
    ///   them at once at the slot's rate (HOI4: 3 saved days + the first day = 4 x the day's points).
    /// - Cost: nothing runs per slot per day. A day's rate depends only on the country's speed and the date, so when a
    ///   tech goes into a slot (or the speed changes) its finish day is found by summing the rates ahead, and booked;
    ///   a day only finishes the slots booked for it. Progress is summed on demand (the UI's one country), saved days
    ///   are the days since the slot emptied. Same sums, same order: the same results as adding the rate every day.
    ///   A speed change counts from the next day.
    /// - Not modelled yet: what a technology unlocks (equipment, sub-units, buildings) and its other modifiers, limited
    ///   bonuses, design teams, research AI, doctrines, experimental facilities, technology sharing.
    /// Changes come only through the commands (Hoi4ResearchCommands).
    /// </summary>
    public sealed class Hoi4Research : MonoBehaviour
    {
        const string EconomyFile = "common/hoi4_country_economy.json5";
        const string TechFile = "common/hoi4_technologies.json5";
        public const int BaseSlots = 2;                                     // NCountry BASE_RESEARCH_SLOTS
        public static readonly FP SavedMaxDays = FP.FromInt(30);            // NTechnology BASE_RESEARCH_POINTS_SAVED
        public static readonly FP BaseTechCost = FP.FromInt(110);           // NTechnology BASE_TECH_COST
        public static readonly FP YearAheadFactor = FP.FromInt(2);          // NTechnology BASE_YEAR_AHEAD_PENALTY_FACTOR
        public static readonly FP MinSpeed = FP.FromFraction(1, 10);        // NTechnology MIN_RESEARCH_SPEED

        public static Hoi4Research Instance { get; private set; }
        public bool IsReady { get; private set; }

        /// <summary>Raised after a slot changed or a tech finished, with the country (UI refresh).</summary>
        public event System.Action<ushort> Changed;
        /// <summary>Raised when a country finishes a technology (country, tech index).</summary>
        public event System.Action<ushort, int> Researched;

        sealed class CountryResearch
        {
            public readonly List<ResearchSlot> Slots = new List<ResearchSlot>();
            public FP Speed;
            public bool[] Known;
            public readonly Dictionary<int, FP> Progress = new Dictionary<int, FP>(); // started, not finished
        }

        GameState gameState;
        readonly List<TechDef> techs = new List<TechDef>();
        readonly Dictionary<string, int> techIndex = new Dictionary<string, int>();
        readonly SortedDictionary<ushort, CountryResearch> countries = new SortedDictionary<ushort, CountryResearch>();
        readonly List<JObject> strategies = new List<JObject>();
        // day number (year x 365 + day of the year) -> slots finishing then, as country << 8 | slot
        readonly Dictionary<int, List<int>> finishing = new Dictionary<int, List<int>>();
        readonly Dictionary<string, List<(int tech, int level)>> buildingGates = new Dictionary<string, List<(int, int)>>(); // building -> techs enabling it, up to a level
        int today;                         // the last simulated day (the start date before the first)
        const int MaxResearchDays = 36500; // a booking past this never finishes (speed floor 0.1: 5500 days at most)

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
            Hoi4.Modifiers.Hoi4Modifiers.Changed -= OnModifiersChanged;
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || !DataFileResolver.IsInitialized
                   // the countries must exist before their tags resolve
                   || Hoi4.Units.Hoi4Divisions.Instance == null || !Hoi4.Units.Hoi4Divisions.Instance.IsLoaded)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            today = DayNumber();

            var start = LoadTechnologies();
            // the modifier system: ideas now, each country's known technologies below (their keys get ids too)
            var keys = new HashSet<string>();
            foreach (var t in techs) foreach (var k in t.Modifiers.Keys) keys.Add(k);
            Hoi4.Modifiers.Hoi4Modifiers.Initialize(gameState, keys);
            Hoi4.Modifiers.Hoi4Modifiers.Changed += OnModifiersChanged;
            string path = DataFileResolver.Resolve(EconomyFile);
            if (File.Exists(path))
                foreach (var p in Json5Loader.LoadJson5File(path).Properties())
                {
                    ushort country = gameState.Countries.GetCountryIdFromTag(p.Name);
                    if (country == 0 || !(p.Value is JObject o)) continue;
                    int slots = o["research_slots"] != null ? (int)o["research_slots"] : BaseSlots;
                    var c = Create(country, slots);
                    if (start.TryGetValue(p.Name, out var known))
                        foreach (int t in known)
                        {
                            c.Known[t] = true;
                            Hoi4.Modifiers.Hoi4Modifiers.AddTechnology(country, t, techs[t].Modifiers, quiet: true);
                        }
                    c.Speed = Hoi4.Modifiers.Hoi4Modifiers.Sum(country, Hoi4.Modifiers.Hoi4Modifiers.ResearchSpeed);
                }
            else
                ArchonLogger.LogWarning($"Hoi4Research: no {EconomyFile} (run Tools/hoi4_convert.py); no country researches", "game_research");
            // with ideas and technologies in: stability (from their stability_factor) and landmarks
            Hoi4.Modifiers.Hoi4Stability.Initialize(gameState);
            IsReady = true;
            ArchonLogger.Log($"Hoi4Research: {techs.Count} technologies, {countries.Count} countries", "game_research");
        }

        /// <summary>Technologies from TechFile; returns each country's 1936 techs by tag.</summary>
        Dictionary<string, List<int>> LoadTechnologies()
        {
            var start = new Dictionary<string, List<int>>();
            string path = DataFileResolver.Resolve(TechFile);
            if (!File.Exists(path))
            {
                ArchonLogger.LogWarning($"Hoi4Research: no {TechFile} (run Tools/hoi4_convert.py); nothing to research", "game_research");
                return start;
            }
            var root = Json5Loader.LoadJson5File(path);
            if (!(root["technologies"] is JObject all)) return start;
            foreach (var p in all.Properties())
            {
                techIndex[p.Name] = techs.Count;
                techs.Add(new TechDef { Index = techs.Count, Id = p.Name });
            }
            foreach (var p in all.Properties())
            {
                var t = techs[techIndex[p.Name]];
                var o = (JObject)p.Value;
                t.Name = (string)o["name"] ?? p.Name;
                t.Cost = Num(o["cost"]);
                t.Year = o["year"] != null ? (int)o["year"] : 0;
                t.Folder = (string)o["folder"];
                t.X = o["x"] != null ? (float)o["x"] : 0f;
                t.Y = o["y"] != null ? (float)o["y"] : 0f;
                if (o["leads_to"] is JArray leads)
                    foreach (var l in leads)
                        if (l is JArray pair && techIndex.TryGetValue((string)pair[0], out int child))
                        {
                            techs[child].Parents.Add(t.Index);
                            t.Children.Add(child);
                            techs[child].ParentCoeff.Add(Num(pair[1]));
                        }
                Names(o["requires"], t.Requires);
                Names(o["xor"], t.Excludes);
                if (o["equipment"] is JArray eq) foreach (var e in eq) t.Equipment.Add((string)e);
                if (o["subunits"] is JArray su) foreach (var s in su) t.SubUnits.Add((string)s);
                if (o["buildings"] is JObject b) foreach (var x in b.Properties()) t.Buildings[x.Name] = (int)x.Value;
                if (o["modifiers"] is JObject m) foreach (var x in m.Properties()) t.Modifiers[x.Name] = Num(x.Value);
                foreach (var gate in t.Buildings)
                {
                    if (!buildingGates.TryGetValue(gate.Key, out var gates)) buildingGates[gate.Key] = gates = new List<(int, int)>();
                    gates.Add((t.Index, gate.Value));
                }
                t.Ai = o["ai"] as JObject;
                if (o["places"] is JArray places)
                    foreach (var pl in places)
                        if (pl is JArray a && a.Count >= 3) t.Places.Add(((string)a[0], (float)a[1], (float)a[2]));
                if (t.Places.Count == 0 && t.Folder != null) t.Places.Add((t.Folder, t.X, t.Y));
                t.EquipmentName = (string)o["equipment_name"];
                if (o["equipment_names"] is JObject en)
                {
                    t.EquipmentNames = new Dictionary<string, string>();
                    foreach (var x in en.Properties()) t.EquipmentNames[x.Name] = (string)x.Value;
                }
            }
            if (root["ai_strategies"] is JArray strats)
                foreach (var x in strats) if (x is JObject so) strategies.Add(so);
            // xor is listed on one side only: both exclude each other
            foreach (var t in techs)
                foreach (int x in t.Excludes.ToArray())
                    if (!techs[x].Excludes.Contains(t.Index)) techs[x].Excludes.Add(t.Index);
            if (root["countries"] is JObject cs)
                foreach (var c in cs.Properties())
                {
                    var list = new List<int>();
                    Names(c.Value, list);
                    start[c.Name] = list;
                }
            return start;
        }

        void Names(JToken token, List<int> into)
        {
            if (token is JArray a)
                foreach (var n in a)
                    if (techIndex.TryGetValue((string)n, out int i)) into.Add(i);
        }

        static FP Num(JToken t) => t != null && t.Type != JTokenType.Null ? FP.FromDouble((double)t) : FP.Zero;

        CountryResearch Create(ushort country, int slots)
        {
            var c = new CountryResearch { Known = new bool[techs.Count] };
            for (int i = 0; i < Mathf.Max(0, slots); i++) c.Slots.Add(new ResearchSlot { EmptySince = today });
            countries[country] = c;
            return c;
        }

        // countries missing from the economy file have no research state; queries read them as empty (a query must
        // never add simulation state: the UI asks on one client only)
        static readonly CountryResearch None = new CountryResearch { Known = System.Array.Empty<bool>() };

        CountryResearch Of(ushort country) => countries.TryGetValue(country, out var c) ? c : None;

        // ---- queries ------------------------------------------------------------------------------------

        public IReadOnlyList<TechDef> Technologies => techs;

        /// <summary>AI strategies weighting technologies (common/ai_strategy, research_weight_factor / research_tech).</summary>
        public IReadOnlyList<JObject> AiStrategies => strategies;

        /// <summary>Countries with research (the economy file's).</summary>
        public IEnumerable<ushort> Countries => countries.Keys;

        public TechDef Tech(int index) => index >= 0 && index < techs.Count ? techs[index] : null;

        public TechDef Tech(string id) => id != null && techIndex.TryGetValue(id, out int i) ? techs[i] : null;

        public IReadOnlyList<ResearchSlot> Slots(ushort country) => Of(country).Slots;

        /// <summary>Research speed bonus (0.01 = +1%).</summary>
        public FP Speed(ushort country) => Of(country).Speed;

        public bool Known(ushort country, int tech)
        {
            var k = Of(country).Known;
            return tech >= 0 && tech < k.Length && k[tech];
        }

        public int EmptySlots(ushort country)
        {
            int n = 0;
            foreach (var s in Of(country).Slots) if (s.Empty) n++;
            return n;
        }

        /// <summary>Research points a tech needs: research_cost x 110 x the cheapest path from a parent the country has.</summary>
        public FP Cost(ushort country, int tech)
        {
            var t = Tech(tech);
            if (t == null) return FP.Zero;
            FP coeff = FP.One;
            bool any = false;
            for (int i = 0; i < t.Parents.Count; i++)
                if (Known(country, t.Parents[i]) && (!any || t.ParentCoeff[i] < coeff)) { coeff = t.ParentCoeff[i]; any = true; }
            return t.Cost * BaseTechCost * coeff;
        }

        /// <summary>Research points the tech has so far (kept when it leaves its slot; its cost once known).</summary>
        public FP Progress(ushort country, int tech)
        {
            if (Known(country, tech)) return Cost(country, tech);
            var c = Of(country);
            int slot = SlotOf(country, tech);
            if (slot >= 0) return ProgressNow(c, c.Slots[slot]);
            return c.Progress.TryGetValue(tech, out var p) ? p : FP.Zero;
        }

        /// <summary>Research days a slot has saved while empty (0 while it researches).</summary>
        public FP Saved(ushort country, int slot)
        {
            var slots = Of(country).Slots;
            if (slot < 0 || slot >= slots.Count || !slots[slot].Empty) return FP.Zero;
            return FP.Min(SavedMaxDays, FP.FromInt(System.Math.Max(0, today - slots[slot].EmptySince)));
        }

        /// <summary>Slot index researching the tech, or -1.</summary>
        public int SlotOf(ushort country, int tech)
        {
            var slots = Of(country).Slots;
            for (int i = 0; i < slots.Count; i++) if (slots[i].Technology == tech) return i;
            return -1;
        }

        /// <summary>Whether the country may start the tech: not known, not in a slot, any parent known (or none), every
        /// dependency known, nothing it excludes known or being researched (it would be dropped when that finishes).</summary>
        public bool CanResearch(ushort country, int tech)
        {
            var t = Tech(tech);
            if (t == null || !countries.ContainsKey(country) || Known(country, tech) || SlotOf(country, tech) >= 0) return false;
            if (t.Parents.Count > 0)
            {
                bool any = false;
                foreach (int p in t.Parents) if (Known(country, p)) { any = true; break; }
                if (!any) return false;
            }
            foreach (int r in t.Requires) if (!Known(country, r)) return false;
            foreach (int x in t.Excludes) if (Known(country, x) || SlotOf(country, x) >= 0) return false;
            return true;
        }

        /// <summary>Today's day number: year x 365 + day of the year (the calendar has no leap years, like HOI4).</summary>
        int DayNumber()
        {
            var now = gameState.Time.GetCurrentGameTime();
            return now.Year * CalendarConstants.DAYS_PER_YEAR + CalendarConstants.DAYS_BEFORE_MONTH[now.Month] + now.Day - 1;
        }

        /// <summary>Years before 1 January of the tech's start_year on a day (0 once there).</summary>
        static FP YearsAhead(TechDef t, int day)
        {
            int days = t.Year > 0 ? t.Year * CalendarConstants.DAYS_PER_YEAR - day : 0;
            return days > 0 ? FP.FromFraction(days, CalendarConstants.DAYS_PER_YEAR) : FP.Zero;
        }

        /// <summary>Years before 1 January of the tech's start_year, from today (0 once there).</summary>
        public FP YearsAhead(int tech) => Tech(tech) is TechDef t ? YearsAhead(t, today) : FP.Zero;

        static FP Rate(FP speed, TechDef t, int day) =>
            FP.Max(MinSpeed, FP.One + speed) / (FP.One + YearAheadFactor * YearsAhead(t, day));

        /// <summary>Research points a slot of the country gains a day on the tech today.</summary>
        public FP DailyRate(ushort country, int tech) => Tech(tech) is TechDef t ? Rate(Speed(country), t, today) : FP.Zero;

        /// <summary>Days until the tech finishes: its booked day while in a slot, else at today's rate (an upper
        /// bound: the rate rises as the start year nears).</summary>
        public int DaysLeft(ushort country, int tech)
        {
            var c = Of(country);
            int slot = SlotOf(country, tech);
            if (slot >= 0) return System.Math.Max(0, c.Slots[slot].FinishDay - today);
            FP rate = DailyRate(country, tech);
            if (rate <= FP.Zero) return int.MaxValue;
            FP left = Cost(country, tech) - Progress(country, tech);
            return left <= FP.Zero ? 0 : FP.Ceiling(left / rate).ToInt();
        }

        /// <summary>A slot's progress after today's research: its sync point plus each day's rate since.</summary>
        FP ProgressNow(CountryResearch c, ResearchSlot s)
        {
            FP p = s.SyncProgress;
            var t = techs[s.Technology];
            for (int d = s.SyncDay + 1; d <= today; d++) p += Rate(c.Speed, t, d);
            return p;
        }

        // ---- commands -----------------------------------------------------------------------------------

        public bool CanSet(ushort country, int slot, int tech)
        {
            var c = Of(country);
            if (slot < 0 || slot >= c.Slots.Count) return false;
            return tech < 0 ? !c.Slots[slot].Empty : CanResearch(country, tech);
        }

        /// <summary>Put a tech in a slot (tech -1 empties it). The tech in it before keeps its progress; a pick in an
        /// empty slot gets the saved days at once, at the slot's rate.</summary>
        public void SetSlot(ushort country, int slot, int tech)
        {
            if (!CanSet(country, slot, tech)) return;
            var c = countries[country];
            var s = c.Slots[slot];
            FP saved = Saved(country, slot);
            if (!s.Empty) c.Progress[s.Technology] = ProgressNow(c, s); // the tech leaving keeps its progress
            s.Technology = tech;
            if (tech < 0)
            {
                s.EmptySince = today;
                Changed?.Invoke(country);
                return;
            }
            c.Progress.TryGetValue(tech, out var p);
            c.Progress.Remove(tech);
            s.SyncDay = today;
            s.SyncProgress = p + saved * Rate(c.Speed, techs[tech], today); // the saved days, at once
            Core.Recording.SessionRecorder.Note("research", "start", country, 0, 0, 0, $"{techs[tech].Id} in slot {slot + 1}");
            if (s.SyncProgress >= Cost(country, tech)) Finish(country, c, slot); // the saved days may finish it
            else Book(country, c, slot);
            Changed?.Invoke(country);
        }

        // ---- daily --------------------------------------------------------------------------------------

        /// <summary>The finish day of a slot's tech: its progress plus each coming day's rate, until the cost.</summary>
        void Book(ushort country, CountryResearch c, int slot)
        {
            var s = c.Slots[slot];
            var t = techs[s.Technology];
            FP cost = Cost(country, s.Technology), p = s.SyncProgress;
            int d = s.SyncDay;
            while (p < cost && d - s.SyncDay < MaxResearchDays) p += Rate(c.Speed, t, ++d);
            s.FinishDay = d;
            if (!finishing.TryGetValue(d, out var list)) finishing[d] = list = new List<int>();
            list.Add(country << 8 | slot);
        }

        /// <summary>A country's modifiers changed: research_speed_factor may have.</summary>
        void OnModifiersChanged(ushort country)
        {
            if (!countries.TryGetValue(country, out var c)) return;
            FP speed = Hoi4.Modifiers.Hoi4Modifiers.Sum(country, Hoi4.Modifiers.Hoi4Modifiers.ResearchSpeed);
            if (speed != c.Speed) SetSpeed(country, c, speed);
        }

        /// <summary>Most levels of a building the country's technologies allow: int.MaxValue when no technology gates
        /// it, 0 while none of its technologies is researched (HOI4 greys it out), else the highest level unlocked
        /// (radar 1..6 by technology).</summary>
        public int BuildingLevel(ushort country, string building)
        {
            if (!buildingGates.TryGetValue(building, out var gates)) return int.MaxValue;
            int best = 0;
            foreach (var (tech, level) in gates) if (Known(country, tech) && level > best) best = level;
            return best;
        }

        /// <summary>The country's research speed changed (a tech or, later, a law): its slots keep the progress made
        /// so far and are booked again at the new speed, which counts from the next day.</summary>
        void SetSpeed(ushort country, CountryResearch c, FP speed)
        {
            for (int i = 0; i < c.Slots.Count; i++)
            {
                var s = c.Slots[i];
                if (s.Empty) continue;
                s.SyncProgress = ProgressNow(c, s);
                s.SyncDay = today;
            }
            c.Speed = speed;
            for (int i = 0; i < c.Slots.Count; i++) if (!c.Slots[i].Empty) Book(country, c, i);
        }

        /// <summary>Daily, from Hoi4Simulation: the slots booked to finish today finish (in country, slot order).
        /// Nothing else runs: progress and saved days follow from the day numbers.</summary>
        public void SimulateDay()
        {
            if (!IsReady) return;
            today = DayNumber();
            if (!finishing.TryGetValue(today, out var list)) return;
            finishing.Remove(today);
            list.Sort();
            int last = -1;
            foreach (int key in list)
            {
                if (key == last) continue;
                last = key;
                ushort country = (ushort)(key >> 8);
                int slot = key & 0xFF;
                if (!countries.TryGetValue(country, out var c) || slot >= c.Slots.Count) continue;
                var s = c.Slots[slot];
                if (s.Empty || s.FinishDay != today) continue; // moved or booked again since
                if (ProgressNow(c, s) < Cost(country, s.Technology)) { Book(country, c, slot); continue; } // cost rose
                Finish(country, c, slot);
                Changed?.Invoke(country);
            }
        }

        void Finish(ushort country, CountryResearch c, int slot)
        {
            var s = c.Slots[slot];
            int tech = s.Technology;
            c.Progress.Remove(tech);
            c.Known[tech] = true;
            s.Technology = -1;
            s.EmptySince = today;
            // another slot can't hold it (CanResearch), but a tech this one excludes may sit in one: drop it
            foreach (var other in c.Slots)
                if (!other.Empty && techs[tech].Excludes.Contains(other.Technology))
                {
                    other.Technology = -1;
                    other.EmptySince = today;
                }
            // its modifiers join the country's (a research speed change comes back through OnModifiersChanged)
            Hoi4.Modifiers.Hoi4Modifiers.AddTechnology(country, tech, techs[tech].Modifiers);
            Core.Recording.SessionRecorder.Note("research", "done", country, 0, 0, 0, techs[tech].Id);
            Researched?.Invoke(country, tech);
        }
    }
}
