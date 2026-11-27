using System.Collections.Generic;
using Hoi4.Construction;
using Hoi4.Map;
using Hoi4.Modifiers;
using Hoi4.Research;
using Scripting;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Scripts
{
    /// <summary>
    /// GAME: what scripts can ask of and do to a country (the methods of `c`). See Hoi4Script for the conventions.
    /// Triggers:
    /// - Who: is_tag, is_original_tag (same as is_tag: no country changes tag yet), exists, has_government (the
    ///   ruling ideology at the start: governments don't change yet).
    /// - What it has: has_idea, has_tech, has_flag, has_political_power, has_stability, num_of_factories (civilian
    ///   + military + dockyards) and each kind alone, num_of_controlled_states, amount_research_slots,
    ///   original_research_slots.
    /// - Diplomacy: has_war, has_war_with, is_in_faction (has an ally), is_in_faction_with; is_subject and
    ///   is_subject_of say no (the game has no subjects).
    /// - Its states and other countries, with a filter function: any_ / all_ owned_state, controlled_state,
    ///   neighbor_country, other_country, enemy_country, allied_country.
    /// Scopes and lists: capital() (a state), overlord() and faction_leader() (no country: neither exists yet), owned_states(), controlled_states(), neighbor_countries(),
    /// other_countries(), enemy_countries(), allied_countries(); random_owned_state, random_controlled_state,
    /// random_owned_controlled_state, random_neighbor_country, random_other_country (one passing the filter, or nil).
    /// Effects: add_political_power, add_idea, remove_idea, swap_idea, add_research_slot, set_flag, clear_flag.
    /// Not here yet (reported as missing when a script calls them): experience, tech bonuses, doctrine cost
    /// reductions, party popularity, stability and war support changes, rules, threat, events, and the rest of
    /// HOI4's vocabulary.
    /// </summary>
    public static partial class Hoi4Script
    {
        static void CountryBindings(ScriptType t)
        {
            // ---- who
            t.SetMethod("is_tag", (ref ScriptCall call) =>
            {
                ushort id = call.Self<CountryScope>().Id;
                call.Return(id != 0 && id == CountryArg(ref call, 1).Id);
            });
            t.SetMethod("is_original_tag", (ref ScriptCall call) =>
            {
                ushort id = call.Self<CountryScope>().Id;
                call.Return(id != 0 && id == CountryArg(ref call, 1).Id);
            });
            t.SetMethod("exists", (ref ScriptCall call) => call.Return(Existing().Contains(call.Self<CountryScope>()) == call.Bool(1)));
            t.SetMethod("has_government", (ref ScriptCall call) =>
                call.Return(Hoi4.War.Hoi4PeaceConference.Ideology(call.Self<CountryScope>().Tag) == call.String(1)));

            // ---- what it has
            t.SetMethod("has_idea", (ref ScriptCall call) => call.Return(Hoi4Modifiers.HasIdea(call.Self<CountryScope>().Id, call.String(1))));
            t.SetMethod("has_tech", (ref ScriptCall call) =>
            {
                var research = Hoi4Research.Instance;
                var tech = research != null ? research.Tech(call.String(1)) : null;
                call.Return(tech != null && research.Known(call.Self<CountryScope>().Id, tech.Index));
            });
            t.SetMethod("has_flag", (ref ScriptCall call) =>
            {
                // has_country_flag = name, or { flag = name ... } (the block's day counts are not kept)
                string flag = call.String(1) ?? call.Table(1).String("flag");
                call.Return(flag != null && flags.TryGetValue(call.Self<CountryScope>().Id, out var set) && set.Contains(flag));
            });
            t.SetMethod("has_political_power", (ref ScriptCall call) =>
                call.Return(Compare(ref call, 1, Hoi4PoliticalPower.Of(call.Self<CountryScope>().Id).ToDouble())));
            t.SetMethod("has_stability", (ref ScriptCall call) =>
                call.Return(Compare(ref call, 1, Hoi4Stability.Stability(call.Self<CountryScope>().Id).ToDouble())));
            t.SetMethod("num_of_factories", (ref ScriptCall call) =>
            {
                ushort id = call.Self<CountryScope>().Id;
                call.Return(Compare(ref call, 1, Civilian(id) + Military(id) + Dockyards(id)));
            });
            t.SetMethod("num_of_civilian_factories", (ref ScriptCall call) => call.Return(Compare(ref call, 1, Civilian(call.Self<CountryScope>().Id))));
            t.SetMethod("num_of_military_factories", (ref ScriptCall call) => call.Return(Compare(ref call, 1, Military(call.Self<CountryScope>().Id))));
            t.SetMethod("num_of_naval_factories", (ref ScriptCall call) => call.Return(Compare(ref call, 1, Dockyards(call.Self<CountryScope>().Id))));
            t.SetMethod("num_of_controlled_states", (ref ScriptCall call) =>
                call.Return(Compare(ref call, 1, ControlledStates(call.Self<CountryScope>().Id).Count)));
            t.SetMethod("amount_research_slots", (ref ScriptCall call) =>
                call.Return(Compare(ref call, 1, Hoi4Research.Instance != null ? Hoi4Research.Instance.Slots(call.Self<CountryScope>().Id).Count : 0)));
            t.SetMethod("original_research_slots", (ref ScriptCall call) =>
                call.Return(Compare(ref call, 1, Hoi4Research.Instance != null ? Hoi4Research.Instance.StartSlots(call.Self<CountryScope>().Id) : 0)));

            // ---- diplomacy
            t.SetMethod("has_war", (ref ScriptCall call) =>
            {
                var diplomacy = gameState.Diplomacy;
                call.Return((diplomacy != null && diplomacy.IsAtWar(call.Self<CountryScope>().Id)) == call.Bool(1));
            });
            t.SetMethod("has_war_with", (ref ScriptCall call) =>
            {
                var diplomacy = gameState.Diplomacy;
                ushort other = CountryArg(ref call, 1).Id;
                call.Return(diplomacy != null && other != 0 && diplomacy.IsAtWar(call.Self<CountryScope>().Id, other));
            });
            t.SetMethod("is_in_faction", (ref ScriptCall call) =>
            {
                var diplomacy = gameState.Diplomacy;
                call.Return((diplomacy != null && diplomacy.GetAllies(call.Self<CountryScope>().Id).Count > 0) == call.Bool(1));
            });
            t.SetMethod("is_in_faction_with", (ref ScriptCall call) =>
            {
                var diplomacy = gameState.Diplomacy;
                ushort id = call.Self<CountryScope>().Id, other = CountryArg(ref call, 1).Id;
                call.Return(id != 0 && other != 0 && (id == other || (diplomacy != null && diplomacy.AreAllied(id, other))));
            });
            t.SetMethod("is_subject", (ref ScriptCall call) => call.Return(!call.Bool(1)));     // no subjects in the game
            t.SetMethod("is_subject_of", (ref ScriptCall call) => call.Return(false));

            // ---- its states and other countries
            t.SetMethod("capital", (ref ScriptCall call) =>
                call.Return(StateOrNone(Hoi4Regions.State(Hoi4Regions.Capital(call.Self<CountryScope>().Tag)))));
            t.SetMethod("overlord", (ref ScriptCall call) => call.Return(noCountry));          // no subjects in the game
            t.SetMethod("faction_leader", (ref ScriptCall call) => call.Return(noCountry));    // alliances have no leader yet
            Iterators(t, "owned_state", id => OwnedStates(id));
            Iterators(t, "controlled_state", id => ControlledStates(id));
            Iterators(t, "owned_controlled_state", OwnedAndControlled);
            Iterators(t, "neighbor_country", id => Neighbours(id));
            Iterators(t, "other_country", Others);
            Iterators(t, "enemy_country", id => Related(id, enemies: true));
            Iterators(t, "allied_country", id => Related(id, enemies: false));

            // ---- effects
            t.SetMethod("add_political_power", (ref ScriptCall call) =>
                Hoi4PoliticalPower.Add(call.Self<CountryScope>().Id, FP.FromDouble(call.Number(1))));
            t.SetMethod("add_idea", (ref ScriptCall call) => Hoi4Modifiers.AddIdea(call.Self<CountryScope>().Id, call.String(1)));  // a timed idea's days are not kept: it stays
            t.SetMethod("remove_idea", (ref ScriptCall call) => Hoi4Modifiers.RemoveIdea(call.Self<CountryScope>().Id, call.String(1)));
            t.SetMethod("swap_idea", (ref ScriptCall call) =>
            {
                ushort id = call.Self<CountryScope>().Id;
                Hoi4Modifiers.RemoveIdea(id, call.String(1));
                Hoi4Modifiers.AddIdea(id, call.String(2));
            });
            t.SetMethod("add_research_slot", (ref ScriptCall call) =>
                Hoi4Research.Instance?.AddSlots(call.Self<CountryScope>().Id, (int)call.Number(1)));
            t.SetMethod("set_flag", (ref ScriptCall call) =>
            {
                ushort id = call.Self<CountryScope>().Id;
                string flag = call.String(1) ?? call.Table(1).String("flag");
                if (id != 0 && flag != null) Flags(id).Add(flag);
            });
            t.SetMethod("clear_flag", (ref ScriptCall call) =>
            {
                if (flags.TryGetValue(call.Self<CountryScope>().Id, out var set) && call.String(1) != null) set.Remove(call.String(1));
            });
        }

        /// <summary>The four forms of one HOI4 iterator over a country's states or countries: any_X(filter),
        /// all_X(filter), random_X(filter) and the list Xs() that every_X loops over.</summary>
        static void Iterators<T>(ScriptType t, string what, System.Func<ushort, List<T>> of) where T : ScriptObject
        {
            t.SetMethod("any_" + what, (ref ScriptCall call) =>
                call.Return(Any(ref call, of(call.Self<CountryScope>().Id), call.Function(1))));
            t.SetMethod("all_" + what, (ref ScriptCall call) =>
                call.Return(Any(ref call, of(call.Self<CountryScope>().Id), call.Function(1), all: true)));
            t.SetMethod("random_" + what, (ref ScriptCall call) =>
                call.Return(Pick(ref call, of(call.Self<CountryScope>().Id), call.Function(1))));
            string list = what.EndsWith("y") ? what.Substring(0, what.Length - 1) + "ies" : what + "s";
            t.SetMethod(list, (ref ScriptCall call) => call.ReturnList(of(call.Self<CountryScope>().Id)));
        }

        static int Civilian(ushort country) => Hoi4Construction.Instance != null ? Hoi4Construction.Instance.Civilian(country) : 0;

        static int Military(ushort country) => Hoi4Construction.Instance != null ? Hoi4Construction.Instance.Military(country) : 0;

        static int Dockyards(ushort country)
        {
            int n = 0;
            if (country != 0 && Hoi4Buildings.Available)
                foreach (var kv in Hoi4Buildings.Users(gameState))
                    if (kv.Value == country) n += Hoi4Buildings.Level(kv.Key, "dockyard");
            return n;
        }

        static List<StateScope> OwnedAndControlled(ushort country)
        {
            var list = new List<StateScope>();
            foreach (var s in OwnedStates(country)) if (StateController(s.Id) == country) list.Add(s);
            return list;
        }

        static List<CountryScope> Others(ushort country)
        {
            var list = new List<CountryScope>();
            foreach (var c in Existing()) if (c.Id != country) list.Add(c);
            return list;
        }

        static List<CountryScope> Related(ushort country, bool enemies)
        {
            var list = new List<CountryScope>();
            var diplomacy = gameState.Diplomacy;
            if (diplomacy == null || country == 0) return list;
            var ids = new List<ushort>(enemies ? diplomacy.GetEnemies(country) : diplomacy.GetAllies(country));
            ids.Sort();     // the engine's order is not promised: scripts loop in id order
            foreach (ushort id in ids) if (countries.TryGetValue(id, out var scope)) list.Add(scope);
            return list;
        }
    }
}
