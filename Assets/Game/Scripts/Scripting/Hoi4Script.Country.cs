using System.Collections.Generic;
using Hoi4.Construction;
using Hoi4.Map;
using Hoi4.Modifiers;
using Hoi4.Research;
using Hoi4.Units;
using Scripting;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Scripts
{
    /// <summary>
    /// GAME: what scripts can ask of and do to a country (the methods of `c`). See Hoi4Script for the conventions.
    /// Triggers:
    /// - Who: is_tag, is_original_tag (same as is_tag: no country changes tag yet), exists, has_government (the
    ///   ruling ideology, Hoi4Politics), party popularity as democratic / communism / fascism / neutrality
    ///   (op, share).
    /// - World tension (Hoi4WorldTension): threat (op, share), has_added_tension_amount (op, percent points).
    /// - What it has: has_idea, has_tech, has_flag, has_political_power, has_stability, has_war_support,
    ///   has_army_experience (and navy, air), num_of_factories (civilian
    ///   + military + dockyards) and each kind alone, num_of_controlled_states, amount_research_slots,
    ///   original_research_slots.
    /// - Diplomacy: has_war, has_war_with, has_war_together_with (a shared enemy), has_war_with_major,
    ///   is_in_faction (has an ally), is_in_faction_with, is_ally_with, is_faction_leader (a stand-in: the member
    ///   with the most factories), is_neighbor_of, has_non_aggression_pact_with, has_guaranteed, is_guaranteed_by,
    ///   gives_military_access_to, has_military_access_to; is_subject, is_puppet, is_subject_of, is_puppet_of,
    ///   has_subject and num_subjects answer for a world without subjects.
    /// - Standing: is_major (as the research AI counts majors), is_ai, surrender_progress, has_capitulated
    ///   (surrender progress at 100%), has_manpower.
    /// - Land: owns_state, controls_state (most of its provinces), has_full_control_of_state (every province),
    ///   controls_province; core states through any_/all_/random_core_state and core_states().
    /// - Its states and other countries, with a filter function: any_ / all_ owned_state, controlled_state,
    ///   neighbor_country, other_country, enemy_country, allied_country.
    /// Scopes and lists: capital() (a state), overlord() and faction_leader() (no country: neither exists yet), owned_states(), controlled_states(), neighbor_countries(),
    /// other_countries(), enemy_countries(), allied_countries(); random_owned_state, random_controlled_state,
    /// random_owned_controlled_state, random_neighbor_country, random_other_country (one passing the filter, or nil).
    /// Effects: add_popularity, set_politics (the ruling party), add_threat, add_named_threat, add_political_power, add_stability, add_war_support, add_army_experience (and navy, air: stored
    /// numbers nothing spends yet), add_tech_bonus (Hoi4Research's research bonuses), add_idea, remove_idea,
    /// swap_idea, add_research_slot, set_flag, clear_flag.
    /// Hoi4Focus adds has_completed_focus, focus_progress and complete_national_focus.
    /// Not here yet (reported as missing when a script calls them): doctrine cost reductions, rules, events, and the rest of HOI4's vocabulary.
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
                call.Return(Hoi4Politics.Ruling(call.Self<CountryScope>().Id) == call.String(1)));
            // party popularity: c:fascism(">", 0.5)
            foreach (string ideology in Hoi4Politics.Ideologies)
            {
                string name = ideology;
                t.SetMethod(name, (ref ScriptCall call) =>
                    call.Return(Compare(ref call, 1, Hoi4Politics.Popularity(call.Self<CountryScope>().Id, name).ToDouble())));
            }
            // world tension: the share 0..1, and what this country has caused in percent points
            t.SetMethod("threat", (ref ScriptCall call) => call.Return(Compare(ref call, 1, Hoi4.War.Hoi4WorldTension.Share.ToDouble())));
            t.SetMethod("has_added_tension_amount", (ref ScriptCall call) =>
                call.Return(Compare(ref call, 1, Hoi4.War.Hoi4WorldTension.GeneratedBy(call.Self<CountryScope>().Id).ToDouble())));

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
            t.SetMethod("has_war_support", (ref ScriptCall call) =>
                call.Return(Compare(ref call, 1, Hoi4Stability.WarSupport(call.Self<CountryScope>().Id).ToDouble())));
            Experience(t, "army", Hoi4Experience.Kind.Army);
            Experience(t, "navy", Hoi4Experience.Kind.Navy);
            Experience(t, "air", Hoi4Experience.Kind.Air);
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
            t.SetMethod("is_puppet", (ref ScriptCall call) => call.Return(!call.Bool(1)));
            t.SetMethod("is_subject_of", (ref ScriptCall call) => call.Return(false));
            t.SetMethod("is_puppet_of", (ref ScriptCall call) => call.Return(false));
            t.SetMethod("has_subject", (ref ScriptCall call) => call.Return(false));
            t.SetMethod("num_subjects", (ref ScriptCall call) => call.Return(Compare(ref call, 1, 0)));
            t.SetMethod("is_ally_with", (ref ScriptCall call) =>
            {
                ushort id = call.Self<CountryScope>().Id, other = CountryArg(ref call, 1).Id;
                call.Return(id != 0 && other != 0 && id != other && gameState.Diplomacy != null && gameState.Diplomacy.AreAllied(id, other));
            });
            t.SetMethod("is_faction_leader", (ref ScriptCall call) => call.Return(FactionLeader(call.Self<CountryScope>().Id) == call.Bool(1)));
            t.SetMethod("is_neighbor_of", (ref ScriptCall call) =>
                call.Return(Neighbours(call.Self<CountryScope>().Id).Contains(CountryArg(ref call, 1))));
            t.SetMethod("has_non_aggression_pact_with", (ref ScriptCall call) => call.Return(Pact(ref call, (d, a, b) => d.HasNonAggressionPact(a, b))));
            t.SetMethod("has_guaranteed", (ref ScriptCall call) => call.Return(Pact(ref call, (d, a, b) => d.IsGuaranteeing(a, b))));
            t.SetMethod("is_guaranteed_by", (ref ScriptCall call) => call.Return(Pact(ref call, (d, a, b) => d.IsGuaranteeing(b, a))));
            t.SetMethod("gives_military_access_to", (ref ScriptCall call) => call.Return(Pact(ref call, (d, a, b) => d.HasMilitaryAccess(a, b))));
            t.SetMethod("has_military_access_to", (ref ScriptCall call) => call.Return(Pact(ref call, (d, a, b) => d.HasMilitaryAccess(b, a))));
            t.SetMethod("has_war_together_with", (ref ScriptCall call) =>
            {
                // both at war with the same country
                ushort other = CountryArg(ref call, 1).Id;
                bool together = false;
                if (other != 0 && gameState.Diplomacy != null)
                    foreach (ushort enemy in gameState.Diplomacy.GetEnemies(call.Self<CountryScope>().Id))
                        if (gameState.Diplomacy.IsAtWar(other, enemy)) { together = true; break; }
                call.Return(together);
            });
            t.SetMethod("has_war_with_major", (ref ScriptCall call) =>
            {
                bool any = false;
                if (gameState.Diplomacy != null)
                    foreach (ushort enemy in gameState.Diplomacy.GetEnemies(call.Self<CountryScope>().Id))
                        if (Hoi4ResearchAI.IsMajor(enemy)) { any = true; break; }
                call.Return(any == call.Bool(1));
            });
            t.SetMethod("is_major", (ref ScriptCall call) => call.Return(Hoi4ResearchAI.IsMajor(call.Self<CountryScope>().Id) == call.Bool(1)));
            t.SetMethod("is_ai", (ref ScriptCall call) =>
            {
                var player = StarterKit.Initializer.Instance?.PlayerState;
                bool human = player != null && player.HasPlayerCountry && player.PlayerCountryId == call.Self<CountryScope>().Id;
                call.Return(!human == call.Bool(1));
            });
            t.SetMethod("surrender_progress", (ref ScriptCall call) => call.Return(Compare(ref call, 1, Surrender(call.Self<CountryScope>().Id))));
            t.SetMethod("has_capitulated", (ref ScriptCall call) => call.Return((Surrender(call.Self<CountryScope>().Id) >= 1) == call.Bool(1)));
            t.SetMethod("has_manpower", (ref ScriptCall call) =>
            {
                var pools = Hoi4Manpower.Instance;
                call.Return(Compare(ref call, 1, pools != null && pools.IsReady ? pools.Available(call.Self<CountryScope>().Id).ToDouble() : 0));
            });

            // ---- land
            t.SetMethod("owns_state", (ref ScriptCall call) =>
            {
                ushort id = call.Self<CountryScope>().Id;
                call.Return(id != 0 && StateOwner(StateArg(ref call, 1)) == id);
            });
            t.SetMethod("controls_state", (ref ScriptCall call) =>
            {
                ushort id = call.Self<CountryScope>().Id;
                call.Return(id != 0 && StateController(StateArg(ref call, 1)) == id);
            });
            t.SetMethod("has_full_control_of_state", (ref ScriptCall call) =>
                call.Return(FullyControls(call.Self<CountryScope>().Id, StateArg(ref call, 1))));
            t.SetMethod("controls_province", (ref ScriptCall call) =>
            {
                ushort id = call.Self<CountryScope>().Id;
                int province = (int)call.Number(1, -1);
                Hoi4.Units.Hoi4Occupation.Snapshot(gameState, 0, out var ctrl, out _);
                call.Return(id != 0 && province > 0 && province < ctrl.Length && ctrl[province] == id);
            });
            Iterators(t, "core_state", Cores);

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
            t.SetMethod("add_popularity", (ref ScriptCall call) =>
                Hoi4Politics.AddPopularity(call.Self<CountryScope>().Id, call.String(1), FP.FromDouble(call.Number(2))));
            t.SetMethod("set_politics", (ref ScriptCall call) =>
                Hoi4Politics.SetRuling(call.Self<CountryScope>().Id, call.Table(1).String("ruling_party")));
            t.SetMethod("add_threat", (ref ScriptCall call) =>
                Hoi4.War.Hoi4WorldTension.Add(call.Self<CountryScope>().Id, FP.FromDouble(call.Number(1)), "script"));
            t.SetMethod("add_named_threat", (ref ScriptCall call) =>
            {
                var b = call.Table(1);
                Hoi4.War.Hoi4WorldTension.Add(call.Self<CountryScope>().Id, FP.FromDouble(b.Number("threat")), b.String("name", "script"));
            });
            t.SetMethod("add_stability", (ref ScriptCall call) =>
                Hoi4Stability.AddStability(call.Self<CountryScope>().Id, FP.FromDouble(call.Number(1))));
            t.SetMethod("add_war_support", (ref ScriptCall call) =>
                Hoi4Stability.AddWarSupport(call.Self<CountryScope>().Id, FP.FromDouble(call.Number(1))));
            t.SetMethod("add_tech_bonus", (ref ScriptCall call) =>
            {
                // { name, bonus, uses, ahead_reduction, categories = { ... }, technologies = { ... } }
                var b = call.Table(1);
                var research = Hoi4Research.Instance;
                if (!b.IsValid || research == null) return;
                research.AddBonus(call.Self<CountryScope>().Id, b.String("name"), FP.FromDouble(b.Number("bonus")),
                                  FP.FromDouble(b.Number("ahead_reduction")), (int)b.Number("uses", 1),
                                  Strings(b.Table("categories")), Strings(b.Table("technologies")));
            });
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

        /// <summary>add_X_experience(amount) and has_X_experience(op, value) for army, navy or air.</summary>
        static void Experience(ScriptType t, string name, Hoi4Experience.Kind kind)
        {
            t.SetMethod($"add_{name}_experience", (ref ScriptCall call) =>
                Hoi4Experience.Add(call.Self<CountryScope>().Id, kind, FP.FromDouble(call.Number(1))));
            t.SetMethod($"has_{name}_experience", (ref ScriptCall call) =>
                call.Return(Compare(ref call, 1, Hoi4Experience.Of(call.Self<CountryScope>().Id, kind).ToDouble())));
        }

        /// <summary>A Lua list of strings.</summary>
        static List<string> Strings(ScriptTable list)
        {
            var items = new List<string>();
            for (int i = 1; i <= list.Length; i++) if (list[i].String != null) items.Add(list[i].String);
            return items;
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

        /// <summary>A two-country diplomacy question about this country (a) and the argument (b).</summary>
        static bool Pact(ref ScriptCall call, System.Func<Core.Diplomacy.DiplomacySystem, ushort, ushort, bool> ask)
        {
            ushort id = call.Self<CountryScope>().Id, other = CountryArg(ref call, 1).Id;
            return id != 0 && other != 0 && gameState.Diplomacy != null && ask(gameState.Diplomacy, id, other);
        }

        /// <summary>Alliances have no leader. A stand-in: of a country and its allies (and theirs), the one with the
        /// most factories leads (the lowest id on a tie). A country with no ally leads nothing.</summary>
        static bool FactionLeader(ushort country)
        {
            var diplomacy = gameState.Diplomacy;
            if (diplomacy == null || country == 0) return false;
            var members = diplomacy.GetAlliesRecursive(country);
            members.Remove(country);
            if (members.Count == 0) return false;
            int own = Civilian(country) + Military(country);
            foreach (ushort m in members)
            {
                int theirs = Civilian(m) + Military(m);
                if (theirs > own || (theirs == own && m < country)) return false;
            }
            return true;
        }

        static double Surrender(ushort country)
        {
            var war = Hoi4.War.Hoi4War.Instance;
            return war != null && war.IsReady && country != 0 ? war.SurrenderProgress(country).ToDouble() : 0;
        }

        /// <summary>The states the country has a core on (at the start: cores don't change yet), by id.</summary>
        static List<StateScope> Cores(ushort country)
        {
            var list = new List<StateScope>();
            string tag = Country(country).Tag;
            if (string.IsNullOrEmpty(tag)) return list;
            foreach (var s in AllStates())
            {
                var info = Hoi4StateData.Get(s.Id);
                if (info != null && info.Cores.Contains(tag)) list.Add(s);
            }
            return list;
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
