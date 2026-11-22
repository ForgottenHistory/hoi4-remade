using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Core;
using Core.Loaders;
using Core.Modding;
using Hoi4.Map;
using Hoi4.Units;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Production
{
    /// <summary>One HOI4 production line: one equipment, a requested number of military factories, an
    /// efficiency per factory slot, and the IC put into the unit being made.</summary>
    public sealed class ProductionLine
    {
        public int Id;                 // unique within the country
        public int Archetype;          // index into Hoi4Divisions.Archetypes
        public string Equipment;       // HOI4 equipment name (infantry_equipment_1)
        public FP Cost;                // build_cost_ic of one unit
        public int Requested;          // factories the line asks for
        public int Active;             // factories it got today (priority order, up to the country's total)
        public readonly List<FP> Efficiency = new List<FP>(); // per factory slot, kept for slots < Active
        public FP Progress;            // IC toward the next unit
        public FP StartEfficiency;     // a 1936 line's starting efficiency (zero for lines made in the game)
        public FP OutputToday;         // IC made today (all active factories)
        public int MadeToday;          // whole units made today

        public FP AverageEfficiency
        {
            get
            {
                if (Active == 0 || Efficiency.Count == 0) return Efficiency.Count > 0 ? Efficiency[0] : Hoi4Production.BaseEfficiency;
                FP sum = FP.Zero;
                int n = System.Math.Min(Active, Efficiency.Count);
                for (int i = 0; i < n; i++) sum += Efficiency[i];
                return sum / n;
            }
        }
    }

    /// <summary>
    /// GAME: HOI4 production (wiki "Production", NDefines.NProduction).
    /// - Military factories: a state's arms_factory buildings (Hoi4Buildings: the 1936 ones plus what construction
    ///   built) count for its owner (the country owning most of its provinces) while the owner controls more than
    ///   half of them. Recounted when ownership, control or the buildings change.
    /// - Lines: per country, in priority order. Each day the lines take factories top-down (Requested, at most
    ///   MAX_MIL_FACTORIES_PER_LINE 150) until the country's factories run out; a lost factory comes off the bottom
    ///   and its slot loses its efficiency, a newly added one starts at BASE_FACTORY_START_EFFICIENCY_FACTOR 10%.
    /// - Output: per active factory POWERED_FACTORY_SPEED_MIL 4.5 IC x its efficiency a day. Whole units come out
    ///   once the line's progress reaches the equipment's build_cost_ic; the rest carries over.
    /// - First day: until the first game day has passed, factories move freely between the 1936 lines: one added to
    ///   such a line starts at the line's starting efficiency and one taken off costs nothing (HOI4 lets the player
    ///   rearrange the start lines). A line made that day starts at the base like any other new line.
    /// - Efficiency grows per active factory by 0.001 x cap^2 / current a day, up to
    ///   BASE_FACTORY_MAX_EFFICIENCY_FACTOR 50% (500 days from 0 to the cap).
    /// - Not modelled: strategic resources and trade, factory output modifiers (technology, laws, stability), equipment
    ///   variants and switching a line's equipment (retention), licences, naval and air production.
    /// Changes come only through the commands (Hoi4ProductionCommands); the AI (Hoi4ProductionAI) uses the same ones.
    /// Countries, lines and states go in id or list order: the same on every machine.
    /// </summary>
    public sealed class Hoi4Production : MonoBehaviour
    {
        const string OobFile = "history/units/oob_1936.json5";

        public static readonly FP FactoryOutput = FP.FromFraction(9, 2);          // POWERED_FACTORY_SPEED_MIL
        public static readonly FP BaseEfficiency = FP.FromFraction(1, 10);        // BASE_FACTORY_START_EFFICIENCY_FACTOR
        public static readonly FP MaxEfficiency = FP.Half;                        // BASE_FACTORY_MAX_EFFICIENCY_FACTOR
        static readonly FP GrowthFactor = FP.FromFraction(1, 1000);
        public const int MaxFactoriesPerLine = 150;                               // MAX_MIL_FACTORIES_PER_LINE
        public const int MaxLines = 64;

        public static Hoi4Production Instance { get; private set; }

        /// <summary>What a country can build of an archetype: the equipment, its cost, and for the production screen
        /// its design name (FRA "MAS-36"), general name ("Infantry Equipment I") and picture (a data-directory path).</summary>
        public struct Buildable { public string Equipment; public FP Cost; public string Name, General, Picture; }

        sealed class CountryProduction
        {
            public readonly List<ProductionLine> Lines = new List<ProductionLine>();
            public readonly Dictionary<int, Buildable> Buildable = new Dictionary<int, Buildable>();
            public int NextLineId = 1;
            public int Factories;
        }

        GameState gameState;
        readonly SortedDictionary<ushort, CountryProduction> countries = new SortedDictionary<ushort, CountryProduction>();
        readonly Dictionary<ushort, int> factoryCount = new Dictionary<ushort, int>();
        int countedUsers = int.MinValue, countedBuildings = -1;
        int day;
        const int ReportEveryDays = 7;

        public bool IsReady { get; private set; }

        /// <summary>Raised after a command changed a country's lines (UI refresh).</summary>
        public event System.Action<ushort> LinesChanged;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Production");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Production>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ---- queries ------------------------------------------------------------------------------------

        /// <summary>Countries with production data (an OOB), in id order.</summary>
        public IEnumerable<ushort> Countries => countries.Keys;

        /// <summary>The country's lines in priority order (empty when it has none). Read only.</summary>
        public IReadOnlyList<ProductionLine> Lines(ushort country) =>
            countries.TryGetValue(country, out var c) ? c.Lines : (IReadOnlyList<ProductionLine>)System.Array.Empty<ProductionLine>();

        public ProductionLine Line(ushort country, int lineId)
        {
            if (!countries.TryGetValue(country, out var c)) return null;
            foreach (var l in c.Lines) if (l.Id == lineId) return l;
            return null;
        }

        /// <summary>Military factories the country has (owned and controlled states).</summary>
        public int Factories(ushort country)
        {
            if (IsReady) CountFactories();
            return factoryCount.TryGetValue(country, out int n) ? n : 0;
        }

        /// <summary>Factories its lines ask for, and those they actually got today.</summary>
        public int Requested(ushort country)
        {
            int n = 0;
            foreach (var l in Lines(country)) n += l.Requested;
            return n;
        }

        /// <summary>Factories no line asks for.</summary>
        public int Free(ushort country) => System.Math.Max(0, Factories(country) - Requested(country));

        readonly Dictionary<int, string> archetypeNames = new Dictionary<int, string>();

        /// <summary>An archetype's display name (HOI4: "Infantry Equipment", "Truck"), else its id made readable.</summary>
        public string ArchetypeName(int archetype)
        {
            if (archetypeNames.TryGetValue(archetype, out var n)) return n;
            var ids = Hoi4.Units.Hoi4Divisions.Archetypes;
            string id = archetype >= 0 && archetype < ids.Length ? ids[archetype] : "?";
            return id.Length > 0 ? char.ToUpper(id[0]) + id.Substring(1).Replace('_', ' ') : id;
        }

        public bool TryGetBuildable(ushort country, int archetype, out Buildable b)
        {
            b = default;
            return countries.TryGetValue(country, out var c) && c.Buildable.TryGetValue(archetype, out b);
        }

        /// <summary>Archetypes the country can build, in index order.</summary>
        public List<int> BuildableArchetypes(ushort country)
        {
            var list = new List<int>();
            if (countries.TryGetValue(country, out var c)) list.AddRange(c.Buildable.Keys);
            list.Sort();
            return list;
        }

        /// <summary>Units of an archetype the country's lines make a day at today's factories and efficiency.</summary>
        public FP PerDay(ushort country, int archetype)
        {
            FP sum = FP.Zero;
            foreach (var l in Lines(country))
                if (l.Archetype == archetype && l.Cost > FP.Zero) sum += LineOutput(l) / l.Cost;
            return sum;
        }

        /// <summary>IC a line makes a day with its active factories.</summary>
        public static FP LineOutput(ProductionLine l)
        {
            FP sum = FP.Zero;
            int n = System.Math.Min(l.Active, l.Efficiency.Count);
            for (int i = 0; i < n; i++) sum += l.Efficiency[i];
            return sum * FactoryOutput;
        }

        // ---- loading ------------------------------------------------------------------------------------

        IEnumerator Start()
        {
            while (Hoi4Divisions.Instance == null || !Hoi4Divisions.Instance.IsLoaded
                   || Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Provinces == null || !DataFileResolver.IsInitialized
                   || Hoi4Equipment.Instance == null || !Hoi4Equipment.Instance.IsReady
                   || !Hoi4StateData.Available || !Hoi4Regions.Available) // factories come from the states
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            var archetypes = Hoi4Divisions.Archetypes;
            var index = new Dictionary<string, int>();
            for (int i = 0; i < archetypes.Length; i++) index[archetypes[i]] = i;

            string path = DataFileResolver.Resolve(OobFile);
            var data = File.Exists(path) ? Json5Loader.LoadJson5File(path)["countries"] as JObject : null;
            if (data == null)
            {
                ArchonLogger.LogWarning($"Hoi4Production: no {OobFile}; no production", "game_production");
                yield break;
            }

            int lineCount = 0;
            foreach (var p in data.Properties())
            {
                ushort country = gameState.Countries.GetCountryIdFromTag(p.Name);
                if (country == 0) continue;
                var cp = new CountryProduction();
                if (p.Value["equipment"] is JObject eq)
                    foreach (var e in eq.Properties())
                    {
                        FP cost = Num(e.Value["cost"]);
                        string sprite = (string)e.Value["sprite"];
                        if (index.TryGetValue(e.Name, out int named) && e.Value["archetype_name"] != null)
                            archetypeNames[named] = (string)e.Value["archetype_name"];
                        if (index.TryGetValue(e.Name, out int a) && cost > FP.Zero)
                            cp.Buildable[a] = new Buildable
                            {
                                Equipment = (string)e.Value["equipment"], Cost = cost,
                                Name = (string)e.Value["name"] ?? e.Name, General = (string)e.Value["general"] ?? e.Name,
                                Picture = string.IsNullOrEmpty(sprite) ? null : $"gfx/interface/production/equipment/{sprite}.png",
                            };
                    }
                if (p.Value["production"] is JArray list)
                    foreach (var l in list)
                    {
                        if (!index.TryGetValue((string)l["archetype"] ?? "", out int a) || cp.Lines.Count >= MaxLines) continue;
                        FP cost = Num(l["cost"]);
                        if (cost <= FP.Zero) continue;
                        int factories = System.Math.Max(1, System.Math.Min(MaxFactoriesPerLine, (int)l["factories"]));
                        // histories start some lines at 100%: the game caps them (research raises the cap)
                        FP eff = FP.Clamp(Num(l["efficiency"]), BaseEfficiency, MaxEfficiency);
                        var line = new ProductionLine
                        {
                            Id = cp.NextLineId++, Archetype = a, Equipment = (string)l["equipment"], Cost = cost,
                            Requested = factories,
                        };
                        for (int i = 0; i < factories; i++) line.Efficiency.Add(eff);
                        line.StartEfficiency = eff;
                        cp.Lines.Add(line);
                        if (!cp.Buildable.ContainsKey(a))
                            cp.Buildable[a] = new Buildable { Equipment = line.Equipment, Cost = cost, Name = line.Equipment, General = line.Equipment };
                        lineCount++;
                    }
                countries[country] = cp;
            }
            IsReady = true;
            CountFactories();
            AllocateFactories();
            int total = 0;
            foreach (int n in factoryCount.Values) total += n;
            ArchonLogger.Log($"Hoi4Production: {lineCount} lines in {countries.Count} countries, {total} military factories", "game_production");
        }

        // ---- factories ----------------------------------------------------------------------------------

        /// <summary>Military factories per country from the states, when ownership or control changed.</summary>
        void CountFactories()
        {
            if (!Construction.Hoi4Buildings.Available) return; // not loaded yet: count later, never cache an empty count
            int users = Construction.Hoi4Buildings.UsersVersion(gameState);
            if (countedUsers == users && countedBuildings == Construction.Hoi4Buildings.Version) return;
            countedUsers = users;
            countedBuildings = Construction.Hoi4Buildings.Version;
            factoryCount.Clear();
            foreach (var kv in Construction.Hoi4Buildings.Users(gameState))
            {
                int mil = Construction.Hoi4Buildings.Level(kv.Key, "arms_factory");
                if (mil <= 0) continue;
                factoryCount.TryGetValue(kv.Value, out int have);
                factoryCount[kv.Value] = have + mil;
            }
        }

        /// <summary>Lines take factories top-down (Active). Each line keeps an efficiency slot per requested factory:
        /// a slot that loses its factory (it was active, now the lines above took it or the country lost it) drops to
        /// the base efficiency, a newly requested slot starts there, a request lowered drops slots off the end. Slots
        /// waiting for a factory keep their value, so a line that has not had its factories yet (the 1936 lines before
        /// the first count) keeps its starting efficiency.</summary>
        void AllocateFactories()
        {
            CountFactories();
            foreach (var kv in countries)
            {
                factoryCount.TryGetValue(kv.Key, out int left);
                foreach (var l in kv.Value.Lines)
                {
                    int was = l.Active;
                    int slots = System.Math.Min(l.Requested, MaxFactoriesPerLine);
                    l.Active = System.Math.Max(0, System.Math.Min(slots, left));
                    left -= l.Active;
                    if (l.Efficiency.Count > slots) l.Efficiency.RemoveRange(slots, l.Efficiency.Count - slots);
                    // first day: a 1936 line's new or freed slots stay at its starting efficiency
                    FP fresh = day == 0 && l.StartEfficiency > FP.Zero ? l.StartEfficiency : BaseEfficiency;
                    while (l.Efficiency.Count < slots) l.Efficiency.Add(fresh);
                    for (int i = l.Active; i < was && i < l.Efficiency.Count; i++) l.Efficiency[i] = fresh;
                }
            }
        }

        // ---- daily --------------------------------------------------------------------------------------

        /// <summary>Daily, from Hoi4Simulation before Hoi4Equipment's deliveries: factories to lines, output into
        /// the stockpiles, efficiency growth.</summary>
        public void SimulateDay()
        {
            if (!IsReady) return;
            AllocateFactories();
            var resources = gameState.Resources;
            var equipment = Hoi4Equipment.Instance;
            resources.BeginBatch();
            foreach (var kv in countries)
                foreach (var l in kv.Value.Lines)
                {
                    l.OutputToday = LineOutput(l);
                    l.MadeToday = 0;
                    if (l.Active == 0) continue;
                    l.Progress += l.OutputToday;
                    if (l.Progress >= l.Cost)
                    {
                        int units = (l.Progress / l.Cost).ToInt();
                        l.Progress -= l.Cost * units;
                        l.MadeToday = units;
                        FP made = FP.FromInt(units);
                        resources.AddResource(kv.Key, (ushort)(Hoi4Equipment.StockResourceBase + l.Archetype), made);
                        equipment?.CountMade(kv.Key, l.Archetype, made);
                    }
                    for (int i = 0; i < l.Active; i++)
                    {
                        FP e = l.Efficiency[i];
                        if (e >= MaxEfficiency) continue;
                        l.Efficiency[i] = FP.Min(MaxEfficiency, e + GrowthFactor * MaxEfficiency * MaxEfficiency / FP.Max(e, BaseEfficiency));
                    }
                }
            resources.EndBatch();
            if (++day % ReportEveryDays == 0) Report();
        }

        /// <summary>Weekly note per country with factories: "factories 28 free 11 | infantry_equipment 10/10 50% 26.4ic ...".</summary>
        void Report()
        {
            if (!IsReady) return;
            var names = Hoi4Divisions.Archetypes;
            foreach (var kv in countries)
            {
                int f = Factories(kv.Key);
                if (f == 0 && kv.Value.Lines.Count == 0) continue;
                var sb = new StringBuilder($"factories {f} free {Free(kv.Key)}");
                foreach (var l in kv.Value.Lines)
                    sb.Append($" | {names[l.Archetype]} {l.Active}/{l.Requested} {FP.Round(l.AverageEfficiency * 100).ToInt()}% {LineOutput(l).ToFloat():0.0}ic");
                Core.Recording.SessionRecorder.Note("production", "week", kv.Key, 0, 0, 0, sb.ToString());
            }
        }

        // ---- changes (commands only) ---------------------------------------------------------------------

        public bool CanAddLine(ushort country, int archetype) =>
            IsReady && countries.TryGetValue(country, out var c) && c.Buildable.ContainsKey(archetype) && c.Lines.Count < MaxLines;

        /// <summary>A new line at the bottom of the queue. Returns its id (0 when not possible).</summary>
        public int AddLine(ushort country, int archetype, int factories)
        {
            if (!CanAddLine(country, archetype)) return 0;
            var c = countries[country];
            var b = c.Buildable[archetype];
            var line = new ProductionLine
            {
                Id = c.NextLineId++, Archetype = archetype, Equipment = b.Equipment, Cost = b.Cost,
                Requested = System.Math.Max(0, System.Math.Min(MaxFactoriesPerLine, factories)),
            };
            c.Lines.Add(line);
            AllocateFactories();
            LinesChanged?.Invoke(country);
            return line.Id;
        }

        public bool SetFactories(ushort country, int lineId, int factories)
        {
            var l = Line(country, lineId);
            if (l == null) return false;
            l.Requested = System.Math.Max(0, System.Math.Min(MaxFactoriesPerLine, factories));
            AllocateFactories();
            LinesChanged?.Invoke(country);
            return true;
        }

        public bool RemoveLine(ushort country, int lineId)
        {
            if (!countries.TryGetValue(country, out var c)) return false;
            int i = c.Lines.FindIndex(l => l.Id == lineId);
            if (i < 0) return false;
            c.Lines.RemoveAt(i);
            AllocateFactories();
            LinesChanged?.Invoke(country);
            return true;
        }

        /// <summary>Move a line to a position in the priority queue (0 = top).</summary>
        public bool MoveLine(ushort country, int lineId, int position)
        {
            if (!countries.TryGetValue(country, out var c)) return false;
            int i = c.Lines.FindIndex(l => l.Id == lineId);
            if (i < 0) return false;
            var line = c.Lines[i];
            c.Lines.RemoveAt(i);
            c.Lines.Insert(System.Math.Max(0, System.Math.Min(c.Lines.Count, position)), line);
            AllocateFactories();
            LinesChanged?.Invoke(country);
            return true;
        }

        static FP Num(JToken t) => t != null && t.Type != JTokenType.Null ? FP.FromDouble((double)t) : FP.Zero;
    }
}
