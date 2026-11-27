using Hoi4.Construction;
using Hoi4.Map;
using Scripting;

namespace Hoi4.Scripts
{
    /// <summary>
    /// GAME: what scripts can ask of and do to a state (the methods of `s`). See Hoi4Script for the conventions.
    /// Triggers: is_state (its id), is_coastal, is_impassable, is_owned_by, is_controlled_by, is_fully_controlled_by
    /// and is_owned_and_controlled_by (owner and controller), is_capital (of its owner), is_in_home_area (joined to
    /// its owner's capital by land the owner owns), has_state_category, is_core_of (a core at the start);
    /// is_demilitarized_zone and is_claimed_by say no (the game has neither), building_level(building, op, value),
    /// free_building_slots(building, op, value, include_locked): the levels of the building the state can still
    /// take, counting what is built and queued (include_locked is not told apart: slots are not locked behind
    /// technologies here), has_flag.
    /// Scopes: owner(), controller() (the "no country" object when none).
    /// Effects: add_building(building, levels) builds at once, up to what the state can hold (a province building,
    /// which needs a province, is left out); add_building_slots(n) adds shared slots; set_flag, clear_flag.
    /// </summary>
    public static partial class Hoi4Script
    {
        static readonly System.Collections.Generic.Dictionary<int, System.Collections.Generic.HashSet<string>> stateFlags =
            new System.Collections.Generic.Dictionary<int, System.Collections.Generic.HashSet<string>>();

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStateFlags() => stateFlags.Clear();

        static void StateBindings(ScriptType t)
        {
            t.SetMethod("is_state", (ref ScriptCall call) => call.Return(call.Self<StateScope>().Id == (int)call.Number(1, -1)));
            t.SetMethod("is_coastal", (ref ScriptCall call) =>
            {
                var info = Hoi4StateData.Get(call.Self<StateScope>().Id);
                call.Return((info != null && info.Coastal) == call.Bool(1));
            });
            t.SetMethod("is_impassable", (ref ScriptCall call) =>
                call.Return(Hoi4Regions.IsImpassableState(call.Self<StateScope>().Id) == call.Bool(1)));
            t.SetMethod("is_owned_by", (ref ScriptCall call) =>
            {
                ushort who = CountryArg(ref call, 1).Id;
                call.Return(who != 0 && StateOwner(call.Self<StateScope>().Id) == who);
            });
            t.SetMethod("is_controlled_by", (ref ScriptCall call) =>
            {
                ushort who = CountryArg(ref call, 1).Id;
                call.Return(who != 0 && StateController(call.Self<StateScope>().Id) == who);
            });
            t.SetMethod("is_fully_controlled_by", (ref ScriptCall call) =>
            {
                ushort who = CountryArg(ref call, 1).Id;
                int id = call.Self<StateScope>().Id;
                call.Return(who != 0 && StateOwner(id) == who && StateController(id) == who);
            });
            t.SetMethod("is_core_of", (ref ScriptCall call) =>
            {
                var info = Hoi4StateData.Get(call.Self<StateScope>().Id);
                string tag = CountryArg(ref call, 1).Tag;
                call.Return(info != null && !string.IsNullOrEmpty(tag) && info.Cores.Contains(tag));
            });
            t.SetMethod("building_level", (ref ScriptCall call) =>
                call.Return(Compare(ref call, 2, Hoi4Buildings.Level(call.Self<StateScope>().Id, call.String(1)))));
            t.SetMethod("free_building_slots", (ref ScriptCall call) =>
            {
                var construction = Hoi4Construction.Instance;
                int free = construction != null ? construction.Room(call.Self<StateScope>().Id, call.String(1)) : 0;
                call.Return(Compare(ref call, 2, free));
            });
            t.SetMethod("has_flag", (ref ScriptCall call) =>
            {
                string flag = call.String(1) ?? call.Table(1).String("flag");
                call.Return(flag != null && stateFlags.TryGetValue(call.Self<StateScope>().Id, out var set) && set.Contains(flag));
            });

            t.SetMethod("is_owned_and_controlled_by", (ref ScriptCall call) =>
            {
                ushort who = CountryArg(ref call, 1).Id;
                int id = call.Self<StateScope>().Id;
                call.Return(who != 0 && StateOwner(id) == who && StateController(id) == who);
            });
            t.SetMethod("is_capital", (ref ScriptCall call) =>
            {
                // the capital of the country that owns it
                int id = call.Self<StateScope>().Id;
                ushort capital = Hoi4Regions.Capital(Country(StateOwner(id)).Tag);
                call.Return((capital != 0 && Hoi4Regions.State(capital) == id) == call.Bool(1));
            });
            t.SetMethod("is_in_home_area", (ref ScriptCall call) =>
            {
                int id = call.Self<StateScope>().Id;
                call.Return(InHomeArea(StateOwner(id), id) == call.Bool(1));
            });
            t.SetMethod("has_state_category", (ref ScriptCall call) =>
            {
                var info = Hoi4StateData.Get(call.Self<StateScope>().Id);
                call.Return(info != null && info.Category == call.String(1));
            });
            t.SetMethod("is_demilitarized_zone", (ref ScriptCall call) => call.Return(!call.Bool(1)));    // none in the game
            t.SetMethod("is_claimed_by", (ref ScriptCall call) => call.Return(false));                    // no claims in the game

            t.SetMethod("owner", (ref ScriptCall call) => call.Return(Country(StateOwner(call.Self<StateScope>().Id))));
            t.SetMethod("controller", (ref ScriptCall call) => call.Return(Country(StateController(call.Self<StateScope>().Id))));

            t.SetMethod("add_building", (ref ScriptCall call) =>
            {
                int state = call.Self<StateScope>().Id;
                string building = call.String(1);
                var construction = Hoi4Construction.Instance;
                var def = Hoi4StateData.Def(building);
                if (construction == null || def == null || Hoi4Construction.IsProvinceBuilding(building) || def.StateMax <= 0) return;
                int room = construction.MaxLevel(state, building) - Hoi4Buildings.Level(state, building);
                int levels = System.Math.Min((int)call.Number(2, 1), room);
                if (levels <= 0) return;
                Hoi4Buildings.Add(state, building, levels);
                Core.Recording.SessionRecorder.Note("script", "building", StateOwner(state), 0, 0, 0,
                    $"{levels} {building} in state {state} (now {Hoi4Buildings.Level(state, building)})");
            });
            t.SetMethod("add_building_slots", (ref ScriptCall call) =>
                Hoi4Buildings.AddSlots(call.Self<StateScope>().Id, (int)call.Number(1)));
            t.SetMethod("set_flag", (ref ScriptCall call) =>
            {
                int id = call.Self<StateScope>().Id;
                string flag = call.String(1) ?? call.Table(1).String("flag");
                if (flag == null) return;
                if (!stateFlags.TryGetValue(id, out var set)) stateFlags[id] = set = new System.Collections.Generic.HashSet<string>();
                set.Add(flag);
            });
            t.SetMethod("clear_flag", (ref ScriptCall call) =>
            {
                if (stateFlags.TryGetValue(call.Self<StateScope>().Id, out var set) && call.String(1) != null) set.Remove(call.String(1));
            });
        }
    }
}
