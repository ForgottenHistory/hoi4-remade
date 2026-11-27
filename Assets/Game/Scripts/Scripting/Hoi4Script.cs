using System.Collections.Generic;
using Core;
using Core.Data;
using Hoi4.Map;
using Scripting;

namespace Hoi4.Scripts
{
    /// <summary>A country as scripts hold it (`c` in a focus's functions). One object per country, made at the start.</summary>
    public sealed class CountryScope : ScriptObject
    {
        public readonly ushort Id;      // 0: a tag the game has no country for (every question about it is false)
        public readonly string Tag;
        public CountryScope(ScriptType type, ushort id, string tag) : base(type) { Id = id; Tag = tag; }
    }

    /// <summary>A state as scripts hold it. One object per state.</summary>
    public sealed class StateScope : ScriptObject
    {
        public readonly int Id;
        public StateScope(ScriptType type, int id) : base(type) { Id = id; }
    }

    /// <summary>
    /// GAME: the HOI4 script world: one Lua engine (Scripting.ScriptEngine) with the objects and functions HOI4's
    /// converted scripts use. Content systems (Hoi4Focus; later events, decisions) add their own registering
    /// functions (focus { ... }) and run their files in it.
    /// - Scopes: country and state objects whose methods are HOI4's triggers and effects (c:has_war(),
    ///   s:add_building("arms_factory", 1)): Hoi4Script.Country.cs and Hoi4Script.State.cs. Names are HOI4's, with
    ///   the translator's few renames (Tools/hoi4_focus.py).
    /// - Globals: has_start_date, global flags (has_global_flag, set_global_flag, clear_global_flag), has_game_rule
    ///   (always false: there are none), country("GER"), state(64), date("<", "1939.1.1"), countries(), states() and their any_, all_ and
    ///   random_ forms with a filter, country_exists, has_dlc (always true),
    ///   is_historical_focus_on (always true), and the combinators of shown triggers in Lua (Prelude): all, any,
    ///   none, when, tooltip, hidden, tooltip_only.
    /// - What the game doesn't have yet is simply not defined: the engine reports each such name once and it
    ///   returns false (Engine.Missing is the list still owed; each is a script / missing note in the recording). A trigger about something the game doesn't model
    ///   at all (subjects) is defined and says no. A tag or state id the game lacks gives a "no country" or
    ///   "no state" object, about which everything is false, and a missing list loops zero times: one gap does
    ///   not stop the rest of a script.
    /// - Conventions of the methods: a yes/no trigger takes an optional false (c:has_war(false): "is not at war");
    ///   a number trigger takes the operator and the value (c:num_of_factories(">", 50)), a bare value meaning
    ///   equal; a country argument is a tag or a country object.
    /// - Effects change game state directly, so call an effect function only from the simulation (a command, the
    ///   daily tick): RunEffect. Triggers only read: Test and Number are safe from the UI.
    /// - State owners: a state's owner is the country owning most of its provinces, its controller the one
    ///   controlling most (ties: the lower id). Cached until ownership or control changes.
    /// </summary>
    public static partial class Hoi4Script
    {
        const string Channel = "game_script";

        // The combinators the translator uses where every line is shown to the player. All arguments are already
        // evaluated when these run.
        const string Prelude = @"
function all(...)
  for i = 1, select('#', ...) do if not select(i, ...) then return false end end
  return true
end
function any(...)
  for i = 1, select('#', ...) do if select(i, ...) then return true end end
  return false
end
function none(...)
  for i = 1, select('#', ...) do if select(i, ...) then return false end end
  return true
end
function when(limit, yes, no)
  if limit then return yes end
  if no == nil then return true end
  return no
end
function tooltip(key, value)
  if value == nil then return true end
  return value
end
function hidden(value)
  if type(value) == 'function' then return value() end
  return value
end
function tooltip_only(effects) end
-- a list the game doesn't provide yet reads as false: looping over it does nothing, the rest of the effect still runs
local lua_ipairs = ipairs
function ipairs(list)
  if type(list) ~= 'table' then return function() end end
  return lua_ipairs(list)
end
";

        public static ScriptEngine Engine { get; private set; }

        /// <summary>The country and state types, for content systems that add methods about their own state
        /// (Hoi4Focus: has_completed_focus).</summary>
        public static ScriptType CountryType => countryType;
        public static ScriptType StateType => stateType;
        public static bool IsReady { get; private set; }

        static GameState gameState;
        static ScriptType countryType, stateType;
        static readonly Dictionary<ushort, CountryScope> countries = new Dictionary<ushort, CountryScope>();
        static readonly Dictionary<string, CountryScope> countriesByTag = new Dictionary<string, CountryScope>();
        static readonly Dictionary<int, StateScope> states = new Dictionary<int, StateScope>();
        static readonly Dictionary<ushort, HashSet<string>> flags = new Dictionary<ushort, HashSet<string>>();
        static readonly HashSet<string> globalFlags = new HashSet<string>();
        const int StartDate = 19360101;
        static CountryScope noCountry;
        static StateScope noState;

        // who owns and controls each state, and each country's states, as of ownedAt / controlledAt
        static readonly Dictionary<int, ushort> stateOwner = new Dictionary<int, ushort>();
        static readonly Dictionary<int, ushort> stateController = new Dictionary<int, ushort>();
        static readonly Dictionary<ushort, List<StateScope>> ownedStates = new Dictionary<ushort, List<StateScope>>();
        static readonly Dictionary<ushort, List<StateScope>> controlledStates = new Dictionary<ushort, List<StateScope>>();
        static readonly Dictionary<ushort, List<CountryScope>> neighbours = new Dictionary<ushort, List<CountryScope>>();
        static readonly List<CountryScope> existing = new List<CountryScope>();    // countries owning a province, by id
        static readonly List<StateScope> noStates = new List<StateScope>();
        static readonly List<CountryScope> noCountries = new List<CountryScope>();
        static int ownedAt = -1, controlledAt = -1;
        static bool neighboursFresh;

        // random picks of one effect run: seeded by RunEffect, so every machine picks the same
        static uint randomSeed;
        static uint randomCalls;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            Engine?.Dispose();
            Engine = null;
            IsReady = false;
            gameState = null;
            countryType = stateType = null;
            countries.Clear(); countriesByTag.Clear(); states.Clear(); flags.Clear(); globalFlags.Clear();
            homeArea.Clear(); homeAreaAt = -1;
            noCountry = null;
            noState = null;
            allStates = null;
            stateOwner.Clear(); stateController.Clear(); ownedStates.Clear(); controlledStates.Clear();
            neighbours.Clear(); existing.Clear();
            ownedAt = controlledAt = -1;
            neighboursFresh = false;
            randomSeed = randomCalls = 0;
        }

        /// <summary>Make the engine, the scopes and the bindings. Needs the countries, the regions and the state data.</summary>
        public static bool Initialize(GameState state)
        {
            if (IsReady) return true;
            if (state == null || !Hoi4Regions.Available || !Hoi4StateData.Available) return false;
            Hoi4.Modifiers.Hoi4Politics.Initialize(state);   // has_government and the popularity triggers read it
            Reset();
            gameState = state;
            Engine = new ScriptEngine(stubMissingGlobals: true, logChannel: Channel);
            // HOI4's scripts use far more than the game has: each missing name goes to the recording's notes
            // (script / missing), not to the log
            Engine.LogMissing = false;
            Engine.MissingMember += (type, member) =>
                Core.Recording.SessionRecorder.Note("script", "missing", 0, 0, 0, 0, type == null ? member : $"{type}:{member}");
            countryType = Engine.DefineType("country");
            stateType = Engine.DefineType("state");

            noCountry = new CountryScope(countryType, 0, "");
            noState = new StateScope(stateType, -1);
            var ids = state.Countries.GetAllCountryIds(Unity.Collections.Allocator.Temp);
            foreach (ushort id in ids)
            {
                string tag = state.Countries.GetCountryTag(id);
                if (id == 0 || string.IsNullOrEmpty(tag)) continue;
                var scope = new CountryScope(countryType, id, tag);
                countries[id] = scope;
                countriesByTag[tag] = scope;
            }
            ids.Dispose();
            foreach (int id in Hoi4Regions.States) states[id] = new StateScope(stateType, id);

            Engine.SetFunction("country", (ref ScriptCall call) => call.Return(Country(call.String(0))));
            Engine.SetFunction("state", (ref ScriptCall call) => call.Return(StateOrNone((int)call.Number(0, -1))));
            Engine.SetFunction("date", (ref ScriptCall call) =>
            {
                var now = gameState.Time.GetCurrentGameTime();
                call.Return(Compare(call.String(0), now.Year * 10000 + now.Month * 100 + now.Day, DateNumber(call.String(1))));
            });
            Engine.SetFunction("has_dlc", (ref ScriptCall call) => call.Return(true));                  // every DLC is assumed owned, as in the converter
            Engine.SetFunction("is_historical_focus_on", (ref ScriptCall call) => call.Return(true));   // the AI plays historically
            Engine.SetFunction("random_state", (ref ScriptCall call) => call.Return(Pick(ref call, AllStates(), call.Function(0))));
            // every country that exists and every state: the lists every_country / every_state loop over, and
            // their any_ / all_ / random_ forms
            Engine.SetFunction("countries", (ref ScriptCall call) => call.ReturnList(Existing()));
            Engine.SetFunction("states", (ref ScriptCall call) => call.ReturnList(AllStates()));
            Engine.SetFunction("any_country", (ref ScriptCall call) => call.Return(Any(ref call, Existing(), call.Function(0))));
            Engine.SetFunction("all_country", (ref ScriptCall call) => call.Return(Any(ref call, Existing(), call.Function(0), all: true)));
            Engine.SetFunction("random_country", (ref ScriptCall call) => call.Return(Pick(ref call, Existing(), call.Function(0))));
            Engine.SetFunction("any_state", (ref ScriptCall call) => call.Return(Any(ref call, AllStates(), call.Function(0))));
            Engine.SetFunction("all_state", (ref ScriptCall call) => call.Return(Any(ref call, AllStates(), call.Function(0), all: true)));
            Engine.SetFunction("country_exists", (ref ScriptCall call) => call.Return(Exists(CountryArg(ref call, 0).Id)));
            // world tension: threat(">", 0.5) compares the share 0..1; add_threat and add_named_threat add percent
            // points caused by nobody in particular (a country's own add_threat is in Hoi4Script.Country.cs)
            Engine.SetFunction("threat", (ref ScriptCall call) => call.Return(Compare(ref call, 0, Hoi4.War.Hoi4WorldTension.Share.ToDouble())));
            // the game starts on 1 January 1936, always
            Engine.SetFunction("has_start_date", (ref ScriptCall call) => call.Return(Compare(call.String(0), StartDate, DateNumber(call.String(1)))));
            Engine.SetFunction("has_game_rule", (ref ScriptCall call) => call.Return(false));   // no game rules: none is set to anything
            Engine.SetFunction("has_global_flag", (ref ScriptCall call) =>
            {
                string flag = call.String(0) ?? call.Table(0).String("flag");
                call.Return(flag != null && globalFlags.Contains(flag));
            });
            Engine.SetFunction("set_global_flag", (ref ScriptCall call) =>
            {
                string flag = call.String(0) ?? call.Table(0).String("flag");
                if (flag != null) globalFlags.Add(flag);
            });
            Engine.SetFunction("clear_global_flag", (ref ScriptCall call) => { if (call.String(0) != null) globalFlags.Remove(call.String(0)); });
            CountryBindings(countryType);
            StateBindings(stateType);
            if (!Engine.Run(Prelude, "prelude.lua"))
                ArchonLogger.LogError($"Hoi4Script: the prelude failed: {Engine.LastError}", Channel);
            IsReady = true;
            ArchonLogger.Log($"Hoi4Script: {countries.Count} countries, {states.Count} states, "
                             + $"{countryType.MethodNames.Count} country and {stateType.MethodNames.Count} state methods", Channel);
            return true;
        }

        // ---- scopes --------------------------------------------------------------------------------------

        /// <summary>The country's object (the "no country" object for an id the game doesn't have: never null).</summary>
        public static CountryScope Country(ushort id) => countries.TryGetValue(id, out var c) ? c : noCountry;

        public static CountryScope Country(string tag) => tag != null && countriesByTag.TryGetValue(tag, out var c) ? c : noCountry;

        /// <summary>The state's object, null for an id that is no state.</summary>
        public static StateScope State(int id) => states.TryGetValue(id, out var s) ? s : null;

        /// <summary>For scripts: the state's object, or the "no state" object (every question about it is false), so
        /// a script naming a state the map lacks goes on.</summary>
        static StateScope StateOrNone(int id) => states.TryGetValue(id, out var s) ? s : noState;

        // ---- calling -------------------------------------------------------------------------------------

        /// <summary>A trigger function for a country. A function that isn't there holds; one that fails doesn't.</summary>
        public static bool Test(ScriptFunctionRef trigger, ushort country) =>
            !trigger.IsValid || (IsReady && Engine.CallBool(trigger, Country(country), onError: false));

        /// <summary>A trigger function that must hold for certain: false when it is not there, when it fails, and
        /// when it called anything the game lacks (a missing trigger answers false, so `not` or none(...) around
        /// it would say yes about something the game cannot tell). For triggers whose yes does something (bypass).</summary>
        public static bool TestSure(ScriptFunctionRef trigger, ushort country)
        {
            if (!trigger.IsValid || !IsReady) return false;
            long before = Engine.MissingCalls;
            return Engine.CallBool(trigger, Country(country), onError: false) && Engine.MissingCalls == before;
        }

        /// <summary>A function returning a number for a country (an AI weight).</summary>
        public static double Number(ScriptFunctionRef function, ushort country, double onError = 0) =>
            IsReady && function.IsValid ? Engine.CallNumber(function, Country(country), onError) : onError;

        /// <summary>Run an effect function for a country. From the simulation only (a command, the daily tick):
        /// it changes game state. seed makes its random picks the same on every machine (and different between
        /// effects): pass something that names this run, like the focus's index.</summary>
        public static bool RunEffect(ScriptFunctionRef effect, ushort country, int seed)
        {
            if (!effect.IsValid) return true;
            if (!IsReady) return false;
            randomSeed = (uint)(gameState.Time.CurrentTick * 2654435761UL) ^ (uint)(country * 7919 + seed * 31 + 1);
            randomCalls = 0;
            return Engine.Call(effect, Country(country), out _);
        }

        // ---- helpers for the bindings ----------------------------------------------------------------------

        /// <summary>have (op) value: the operator is one of &lt; &gt; &lt;= &gt;= != and anything else means equal.</summary>
        static bool Compare(string op, double have, double value)
        {
            switch (op)
            {
                case "<": return have < value;
                case ">": return have > value;
                case "<=": return have <= value;
                case ">=": return have >= value;
                case "!=": return have != value;
                default: return have == value;
            }
        }

        /// <summary>A number trigger's arguments from index at: (operator, value), or a bare value (equal).</summary>
        static bool Compare(ref ScriptCall call, int at, double have)
        {
            string op = call.String(at);
            return op != null ? Compare(op, have, call.Number(at + 1)) : Compare("=", have, call.Number(at));
        }

        /// <summary>"1939.1.1" as 19390101 (0 when it is no date).</summary>
        static int DateNumber(string date)
        {
            if (date == null) return 0;
            var parts = date.Split('.');
            if (parts.Length < 3 || !int.TryParse(parts[0], out int y) || !int.TryParse(parts[1], out int m) || !int.TryParse(parts[2], out int d)) return 0;
            return y * 10000 + m * 100 + d;
        }

        /// <summary>A country argument: a country object or a tag. The "no country" object when it is neither.</summary>
        static CountryScope CountryArg(ref ScriptCall call, int index)
        {
            var value = call[index];
            if (value.Kind == ScriptValueKind.Object) return value.Object<CountryScope>() ?? noCountry;
            return Country(value.String);
        }

        /// <summary>Whether any (or, with all, every) item passes the script's filter function.</summary>
        static bool Any<T>(ref ScriptCall call, List<T> items, ScriptFunctionRef filter, bool all = false) where T : ScriptObject
        {
            foreach (var item in items)
            {
                bool pass = !filter.IsValid || call.Engine.CallBool(filter, item);
                if (pass != all) return pass;
            }
            return all;
        }

        /// <summary>One of the items passing the script's filter function, picked by the effect run's random
        /// sequence (RunEffect); null when none passes.</summary>
        static T Pick<T>(ref ScriptCall call, List<T> items, ScriptFunctionRef filter) where T : ScriptObject
        {
            var passing = new List<T>();
            foreach (var item in items)
                if (!filter.IsValid || call.Engine.CallBool(filter, item)) passing.Add(item);
            if (passing.Count == 0) return null;
            var rng = new DeterministicRandom(randomSeed + (++randomCalls) * 40503u + 1u);
            return passing[rng.NextInt(passing.Count)];
        }

        static HashSet<string> Flags(ushort country)
        {
            if (!flags.TryGetValue(country, out var set)) flags[country] = set = new HashSet<string>();
            return set;
        }

        // ---- states' owners and controllers ----------------------------------------------------------------

        static void RefreshStates()
        {
            var provinces = gameState.Provinces;
            if (ownedAt == provinces.OwnershipVersion && controlledAt == provinces.ControlVersion) return;
            ownedAt = provinces.OwnershipVersion;
            controlledAt = provinces.ControlVersion;
            neighboursFresh = false;
            stateOwner.Clear(); stateController.Clear(); ownedStates.Clear(); controlledStates.Clear(); existing.Clear();
            Hoi4.Units.Hoi4Occupation.Snapshot(gameState, 0, out var ctrl, out var owner);
            var tally = new Dictionary<ushort, int>();
            var exists = new SortedSet<ushort>();
            foreach (int state in Hoi4Regions.States)      // in id order: the lists below are sorted by state
            {
                var provs = Hoi4Regions.ProvincesOf(state);
                var scope = states[state];
                ushort o = Most(provs, owner, tally), c = Most(provs, ctrl, tally);
                if (o != 0) { stateOwner[state] = o; ListOf(ownedStates, o).Add(scope); }
                if (c != 0) { stateController[state] = c; ListOf(controlledStates, c).Add(scope); }
                foreach (ushort p in provs) if (p < owner.Length && owner[p] != 0) exists.Add(owner[p]);
            }
            foreach (ushort c in exists) if (countries.TryGetValue(c, out var scope)) existing.Add(scope);
        }

        static ushort Most(IReadOnlyList<ushort> provs, ushort[] of, Dictionary<ushort, int> tally)
        {
            tally.Clear();
            foreach (ushort p in provs)
            {
                if (p >= of.Length || of[p] == 0) continue;
                tally.TryGetValue(of[p], out int n);
                tally[of[p]] = n + 1;
            }
            ushort best = 0;
            int most = 0;
            foreach (var kv in tally)
                if (kv.Value > most || (kv.Value == most && kv.Key < best)) { best = kv.Key; most = kv.Value; }
            return best;
        }

        static List<T> ListOf<T>(Dictionary<ushort, List<T>> lists, ushort country)
        {
            if (!lists.TryGetValue(country, out var list)) lists[country] = list = new List<T>();
            return list;
        }

        public static ushort StateOwner(int state) { RefreshStates(); return stateOwner.TryGetValue(state, out ushort c) ? c : (ushort)0; }

        public static ushort StateController(int state) { RefreshStates(); return stateController.TryGetValue(state, out ushort c) ? c : (ushort)0; }

        static List<StateScope> OwnedStates(ushort country) { RefreshStates(); return ownedStates.TryGetValue(country, out var l) ? l : noStates; }

        static List<StateScope> ControlledStates(ushort country) { RefreshStates(); return controlledStates.TryGetValue(country, out var l) ? l : noStates; }

        // per country the provinces joined to its capital by land it owns, as of homeAreaAt (ownership version)
        static readonly Dictionary<ushort, HashSet<ushort>> homeArea = new Dictionary<ushort, HashSet<ushort>>();
        static int homeAreaAt = -1;

        /// <summary>Whether the state is in the country's home area: one of its provinces is joined to the country's
        /// capital by land the country owns (HOI4's is_in_home_area, without its continent rule).</summary>
        static bool InHomeArea(ushort country, int state)
        {
            if (country == 0) return false;
            if (homeAreaAt != gameState.Provinces.OwnershipVersion) { homeArea.Clear(); homeAreaAt = gameState.Provinces.OwnershipVersion; }
            if (!homeArea.TryGetValue(country, out var area))
            {
                homeArea[country] = area = new HashSet<ushort>();
                ushort capital = Hoi4Regions.Capital(Country(country).Tag);
                Hoi4.Units.Hoi4Occupation.Snapshot(gameState, 0, out _, out var owner);
                gameState.Adjacencies.GetCompactGraph(out int[] offsets, out ushort[] links);
                if (capital != 0 && capital < owner.Length && owner[capital] == country)
                {
                    var queue = new Queue<ushort>();
                    area.Add(capital);
                    queue.Enqueue(capital);
                    while (queue.Count > 0)
                    {
                        ushort p = queue.Dequeue();
                        if (p + 1 >= offsets.Length) continue;
                        for (int i = offsets[p]; i < offsets[p + 1]; i++)
                        {
                            ushort q = links[i];
                            if (q < owner.Length && owner[q] == country && !Hoi4Regions.IsWater(q) && area.Add(q)) queue.Enqueue(q);
                        }
                    }
                }
            }
            foreach (ushort p in Hoi4Regions.ProvincesOf(state)) if (area.Contains(p)) return true;
            return false;
        }

        /// <summary>Whether the country controls every province of the state.</summary>
        static bool FullyControls(ushort country, int state)
        {
            var provs = Hoi4Regions.ProvincesOf(state);
            if (country == 0 || provs.Count == 0) return false;
            Hoi4.Units.Hoi4Occupation.Snapshot(gameState, 0, out var ctrl, out _);
            foreach (ushort p in provs) if (p >= ctrl.Length || ctrl[p] != country) return false;
            return true;
        }

        /// <summary>A state argument: a state object or a state id; -1 when it is neither.</summary>
        static int StateArg(ref ScriptCall call, int index)
        {
            var value = call[index];
            if (value.Kind == ScriptValueKind.Object) return value.Object<StateScope>()?.Id ?? -1;
            return (int)value.Number(-1);
        }

        static List<StateScope> allStates;

        /// <summary>Every state, by id.</summary>
        static List<StateScope> AllStates()
        {
            if (allStates == null || allStates.Count != states.Count)
            {
                allStates = new List<StateScope>(states.Count);
                foreach (int id in Hoi4Regions.States) allStates.Add(states[id]);
            }
            return allStates;
        }

        /// <summary>Whether the country owns a state (most of one's provinces).</summary>
        public static bool Exists(ushort country) => OwnedStates(country).Count > 0;

        /// <summary>Countries owning a province, by id.</summary>
        static List<CountryScope> Existing() { RefreshStates(); return existing; }

        /// <summary>Countries owning a province next to one of the country's (land or sea link), by id.</summary>
        static List<CountryScope> Neighbours(ushort country)
        {
            RefreshStates();
            if (!neighboursFresh)
            {
                neighboursFresh = true;
                neighbours.Clear();
                Hoi4.Units.Hoi4Occupation.Snapshot(gameState, 0, out _, out var owner);
                gameState.Adjacencies.GetCompactGraph(out int[] offsets, out ushort[] links);
                var pairs = new SortedSet<(ushort, ushort)>();
                for (int p = 0; p + 1 < offsets.Length && p < owner.Length; p++)
                {
                    ushort a = owner[p];
                    if (a == 0) continue;
                    for (int i = offsets[p]; i < offsets[p + 1]; i++)
                    {
                        ushort q = links[i];
                        ushort b = q < owner.Length ? owner[q] : (ushort)0;
                        if (b != 0 && b != a) pairs.Add((a, b));
                    }
                }
                foreach (var (a, b) in pairs)
                    if (countries.TryGetValue(b, out var scope)) ListOf(neighbours, a).Add(scope);
            }
            return neighbours.TryGetValue(country, out var l) ? l : noCountries;
        }
    }
}
