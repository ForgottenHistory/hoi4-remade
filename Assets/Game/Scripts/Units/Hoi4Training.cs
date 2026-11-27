using System.Collections.Generic;
using Core;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: army exercises, HOI4's way (the Battle Plans window's btn_exercise; NDefines.NMilitary TRAINING_*).
    /// A division on exercise gains UNIT_EXPERIENCE_PER_TRAINING_DAY (0.15%) experience a day, while it stands
    /// still, up to the start of level TRAINING_MAX_LEVEL (2, Regular: 30%). Meanwhile its organisation is held at
    /// TRAINING_ORG (20% of its maximum; it drops there the moment the exercise starts and recovers once it ends)
    /// and it suffers TRAINING_ATTRITION (5%) on top of supply's and the weather's (Hoi4Supply). Below
    /// TRAINING_MIN_STRENGTH (10%) it gains nothing until reinforced. It cannot be moved: the player's move orders
    /// pass it by and a march it was ordered on by hand stops. Its battle plan still moves it: a division first
    /// marches to its place on the plan (Hoi4BattlePlans.Hold) and exercises once there; the plan doesn't send it
    /// to attack. A battle ends its exercise.
    /// Started with "until trained" (shift + click), a division's exercise ends by itself at the experience cap;
    /// otherwise it goes on until stopped. Orders are SetTrainingCommand; the daily step is simulation
    /// (id-ordered, fixed point). Not here: the country's army experience from exercises (there is none yet) and
    /// the equipment an exercise wears out beyond attrition.
    /// </summary>
    public static class Hoi4Training
    {
        static readonly FP ExperiencePerDay = FP.FromFraction(15, 10000);   // UNIT_EXPERIENCE_PER_TRAINING_DAY
        const int MaxLevel = 2;                                             // TRAINING_MAX_LEVEL
        static readonly FP OrgShare = FP.FromFraction(2, 10);               // TRAINING_ORG
        static readonly FP ExtraAttrition = FP.FromFraction(5, 100);        // TRAINING_ATTRITION
        static readonly FP MinStrength = FP.FromFraction(1, 10);            // TRAINING_MIN_STRENGTH

        // division -> its exercise ends by itself at the experience cap
        static readonly Dictionary<ushort, bool> training = new Dictionary<ushort, bool>();

        /// <summary>Bumped when a division starts or stops (presentation: buttons, counters).</summary>
        public static int Version { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { training.Clear(); Version = 0; }

        public static bool IsTraining(ushort unitId) => training.ContainsKey(unitId);
        /// <summary>On exercise, to end by itself once trained.</summary>
        public static bool StopsWhenTrained(ushort unitId) => training.TryGetValue(unitId, out bool until) && until;

        /// <summary>The experience an exercise can take a division to: the start of level TRAINING_MAX_LEVEL.</summary>
        public static FP ExperienceCap => Hoi4Division.LevelStart(MaxLevel);
        public static string MaxLevelName => Hoi4Division.LevelNames[MaxLevel];

        /// <summary>The share of its maximum organisation a division may have: TRAINING_ORG on exercise, else all.</summary>
        public static FP OrganisationCap(ushort unitId) => training.ContainsKey(unitId) ? OrgShare : FP.One;

        /// <summary>The attrition an exercise adds (TRAINING_ATTRITION), 0 off exercise.</summary>
        public static FP Attrition(ushort unitId) => training.ContainsKey(unitId) ? ExtraAttrition : FP.Zero;

        /// <summary>SetTrainingCommand: start the divisions' exercise (those in a battle don't) or stop it.</summary>
        public static void Set(GameState gameState, List<ushort> unitIds, bool on, bool untilTrained)
        {
            var divisions = Hoi4Divisions.Instance;
            var units = gameState.Units;
            ushort country = 0;
            int changed = 0;
            foreach (ushort u in unitIds)
            {
                if (!units.HasUnit(u)) continue;
                country = units.GetUnit(u).countryID;
                if (!on) { if (training.Remove(u)) changed++; continue; }
                if (Hoi4Combat.IsFighting(u) || Hoi4Combat.IsRetreating(u)) continue;
                var d = divisions?.Get(u);
                if (d == null) continue;
                if (untilTrained && d.Experience >= ExperienceCap) continue; // nothing left to train for
                if (!training.TryGetValue(u, out bool was) || was != untilTrained) changed++;
                training[u] = untilTrained;
                // A march by hand stops; one to its place on a battle plan goes on (it exercises once there)
                var plans = Hoi4BattlePlans.Instance;
                if (plans == null || plans.PlanOf(u) == null || plans.HasManualOrder(u)) units.MovementQueue.CancelMovement(u);
                d.Organisation = FP.Min(d.Organisation, d.MaxOrganisation * OrgShare); // at once, like HOI4
            }
            if (changed == 0) return;
            Version++;
            Core.Recording.SessionRecorder.Note("training", on ? "start" : "stop", country, 0, 0, 0,
                $"{changed} of {unitIds.Count} divisions" + (on && untilTrained ? ", until trained" : ""));
            Hoi4UnitCounters.Instance?.MarkDirty();
        }

        /// <summary>Daily: experience for the divisions on exercise; those gone, in a battle or (until trained) at
        /// the cap leave it.</summary>
        public static void SimulateDay(GameState gameState, Hoi4Divisions divisions)
        {
            if (training.Count == 0 || divisions == null) return;
            var ids = new List<ushort>(training.Keys);
            ids.Sort();
            FP cap = ExperienceCap;
            int ended = 0;
            foreach (ushort u in ids)
            {
                var d = divisions.Get(u);
                if (d == null || !gameState.Units.HasUnit(u)) { training.Remove(u); ended++; continue; }
                if (Hoi4Combat.IsFighting(u) || Hoi4Combat.IsRetreating(u))
                {
                    training.Remove(u);
                    ended++;
                    Core.Recording.SessionRecorder.Note("training", "interrupted", gameState.Units.GetUnit(u).countryID, u, gameState.Units.GetUnit(u).provinceID, 0, "in a battle");
                    continue;
                }
                bool marching = gameState.Units.MovementQueue.IsUnitMoving(u); // to its place on a plan: not exercising yet
                if (!marching && d.FullStrength > FP.Zero && d.Strength >= d.FullStrength * MinStrength && d.Experience < cap)
                    d.Experience = FP.Min(cap, d.Experience + ExperiencePerDay);
                if (d.Experience >= cap && training[u])
                {
                    training.Remove(u);
                    ended++;
                    Core.Recording.SessionRecorder.Note("training", "trained", gameState.Units.GetUnit(u).countryID, u, gameState.Units.GetUnit(u).provinceID, 0, $"experience {d.Experience.ToFloat():0.###}");
                }
            }
            if (ended == 0) return;
            Version++;
            Hoi4UnitCounters.Instance?.MarkDirty();
        }
    }
}
