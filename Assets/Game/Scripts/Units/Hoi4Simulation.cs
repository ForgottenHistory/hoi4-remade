using System.Collections;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the one place the HOI4 simulation runs on game time, in a fixed order. Each system used to
    /// subscribe to the hourly tick on its own, so the order depended on which finished loading first.
    /// Every hour: combat (battles, retreats, organisation), then battle plans (lines follow the border,
    /// divisions take their spots), then the AI calling off bad attacks. Every day, after that day's last
    /// hour: plans re-check which AI lines exist. The AI picks new attacks hourly, each country at its own hour.
    /// Occupation is not here: it follows unit arrivals, which the engine's movement queue raises.
    /// A system that hasn't finished loading skips the tick.
    /// Timing (perf.tsv in the recording, one row per day): milliseconds each system spent that day (hourly
    /// ones summed over the day's hours), the frame times since the last row, and how many wars and divisions
    /// there are, plus the worst single hour of the hourly systems. Measuring only: it changes nothing in the simulation.
    /// </summary>
    public sealed class Hoi4Simulation : MonoBehaviour
    {
        Core.Systems.TimeManager time;

        // Timing, per day: [0] combat hourly, [1] plans hourly, [2] AI hourly (break-offs and that hour's
        // countries' attacks), [3] recorder, [4] combat daily, [5] equipment, [6] manpower, [7] plans daily,
        // [8] war, [9] AI wars, [10] production, [11] AI production, [12] construction, [13] AI construction, [14] deployment, [15] AI deployment, [16] AI army, [17] railways, [18] supply, [19] weather, [20] research, [21] AI research, [22] AI tank designs, [23] focuses, political power, party popularity and world tension, [24] AI focus picks; then the worst single hour of [0], [1], [2], and the plans' daily line refresh
        static readonly string[] PerfNames =
            { "combat_h", "plans_h", "ai_h", "recorder", "combat_d", "equipment", "manpower", "plans_d", "war", "ai_wars", "production", "ai_production", "construction", "ai_construction", "deployment", "ai_deployment", "ai_army", "railways", "supply", "weather", "research", "ai_research", "ai_tanks", "focus", "ai_focus" };
        static readonly string[] MaxNames = { "combat_h_max", "plans_h_max", "ai_h_max", "plans_refresh", "plans_distribute", "plans_ai" };
        readonly double[] perf = new double[PerfNames.Length];
        // managed memory each step allocated that day, bytes (alloc.tsv): what feeds the garbage collector
        readonly long[] alloc = new long[PerfNames.Length];
        // One profiler marker per step, named like the perf.tsv columns: they show in a normal (not deep) capture
        static readonly Unity.Profiling.ProfilerMarker[] Markers = System.Array.ConvertAll(PerfNames, n => new Unity.Profiling.ProfilerMarker("Hoi4." + n));
        readonly double[] perfMax = new double[3];
        readonly System.Diagnostics.Stopwatch watch = new System.Diagnostics.Stopwatch();
        double frameSum, frameMax;
        int frames;

        void Measure(int slot, System.Action step)
        {
            // heap growth over the step (Unity's Mono doesn't count allocated bytes per thread: that call returns 0).
            // A collection during the step shrinks the heap: such a step counts as nothing, so the sums run a little low
            long before = System.GC.GetTotalMemory(false);
            watch.Restart();
            using (Markers[slot].Auto()) step();
            double ms = watch.Elapsed.TotalMilliseconds;
            long grown = System.GC.GetTotalMemory(false) - before;
            if (grown > 0) alloc[slot] += grown;
            perf[slot] += ms;
            if (slot < perfMax.Length && ms > perfMax[slot]) perfMax[slot] = ms;
        }

        void Update()
        {
            double ms = Time.unscaledDeltaTime * 1000.0;
            frameSum += ms;
            if (ms > frameMax) frameMax = ms;
            frames++;
        }

        void WritePerf()
        {
            var recorder = Core.Recording.SessionRecorder.Current;
            if (recorder != null && !recorder.Stopped)
            {
                var gs = Engine.ArchonEngine.Instance.GameState;
                int wars = gs.Diplomacy?.GetAllWars().Count ?? 0;
                int divisions = Hoi4Divisions.Instance != null ? Hoi4Divisions.Instance.All.Count : 0;
                double total = 0;
                var sb = new System.Text.StringBuilder();
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                foreach (double v in perf) { total += v; sb.Append(v.ToString("0.00", inv)).Append('\t'); }
                foreach (double v in perfMax) sb.Append(v.ToString("0.00", inv)).Append('\t');
                double refresh = Hoi4BattlePlans.Instance != null ? Hoi4BattlePlans.Instance.TakeRefreshMs() : 0;
                sb.Append(refresh.ToString("0.00", inv)).Append('\t');
                double distribute = Hoi4BattlePlans.Instance != null ? Hoi4BattlePlans.Instance.TakeDistributeMs() : 0;
                sb.Append(distribute.ToString("0.00", inv)).Append('\t');
                double ai = Hoi4BattlePlans.Instance != null ? Hoi4BattlePlans.Instance.TakeAIMs() : 0;
                sb.Append(ai.ToString("0.00", inv)).Append('\t');
                sb.Append(total.ToString("0.00", inv)).Append('\t');
                sb.Append(frames).Append('\t');
                sb.Append((frames > 0 ? frameSum / frames : 0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(frameMax.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)).Append('\t');
                sb.Append(wars).Append('\t').Append(divisions);
                recorder.Table("perf", string.Join("\t", PerfNames) + "\t" + string.Join("\t", MaxNames) + "\tsim_ms\tframes\tframe_ms_avg\tframe_ms_max\twars\tdivisions").Row(sb.ToString());
            }
            if (recorder != null && !recorder.Stopped)
            {
                // alloc.tsv, one row per day: kilobytes of managed memory each system allocated (same columns as perf.tsv's systems)
                var sb = new System.Text.StringBuilder();
                long total = 0;
                foreach (long b in alloc) { total += b; sb.Append(b / 1024).Append('	'); }
                sb.Append(total / 1024);
                recorder.Table("alloc", string.Join("	", PerfNames) + "	total_kb").Row(sb.ToString());
            }
            System.Array.Clear(alloc, 0, alloc.Length);
            System.Array.Clear(perf, 0, perf.Length);
            System.Array.Clear(perfMax, 0, perfMax.Length);
            frameSum = frameMax = 0;
            frames = 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Simulation");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Simulation>();
        }

        // The engine's and the game's assemblies, compiled in the background while the game loads (Mono compiles a
        // method on its first call: the first day used to pay about 90 ms for that in one frame)
        static readonly string[] WarmAssemblies = { "Assembly-CSharp", "Core", "Engine", "MapAssembly", "Scripting", "StarterKit", "Utils" };

        static void WarmUpJit()
        {
            var found = new System.Collections.Generic.List<System.Reflection.Assembly>();
            var loaded = System.AppDomain.CurrentDomain.GetAssemblies();
            foreach (string name in WarmAssemblies)
                foreach (var a in loaded)
                    if (a.GetName().Name == name) { found.Add(a); break; }
            Core.Common.JitWarmup.Start(found.ToArray());
        }

        /// <summary>Everything the load-time preparation reads has loaded.</summary>
        static bool AllLoaded(Engine.ArchonEngine engine) =>
            engine.IsInitialized && engine.GameState != null
            && Hoi4Divisions.Instance != null && Hoi4Divisions.Instance.IsLoaded
            && Hoi4.Modifiers.Hoi4Modifiers.IsReady
            && Hoi4.Research.Hoi4Research.Instance != null && Hoi4.Research.Hoi4Research.Instance.IsReady
            && Hoi4.Production.Hoi4Production.Instance != null && Hoi4.Production.Hoi4Production.Instance.IsReady
            && Hoi4.Construction.Hoi4Construction.Instance != null && Hoi4.Construction.Hoi4Construction.Instance.IsReady;

        IEnumerator Start()
        {
            WarmUpJit();
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Time == null)
                yield return null;
            time = Engine.ArchonEngine.Instance.GameState.Time;
            time.OnHourlyTick += OnHour;
            time.OnDailyTick += OnDay;
            // One-time work the first day would otherwise pay for in one frame, done while the game loads. None of
            // it decides anything or touches a division: the same results, earlier. Each step is still done by its
            // own SimulateDay if a day comes first.
            while (true)
            {
                var engine = Engine.ArchonEngine.Instance;
                if (engine == null) yield break; // the scene went away
                if (AllLoaded(engine) && Hoi4.Weather.Hoi4Weather.Prepare(engine.GameState)) // the December before the start
                {
                    yield return null; // one step a frame
                    Hoi4.Supply.Hoi4Supply.Prepare(engine.GameState); // hub connections and province supply
                    yield return null;
                    Hoi4.Research.Hoi4ResearchAI.Prepare(); // the AI's tech weights
                    Hoi4.Production.Hoi4TankDesigns.Prepare(); // and its tank design triggers
                    break;
                }
                yield return null;
            }
            while (Core.Common.JitWarmup.IsRunning) yield return null;
            if (Core.Common.JitWarmup.Supported)
                ArchonLogger.Log($"Hoi4Simulation: {Core.Common.JitWarmup.MethodCount} methods compiled ahead in "
                                 + $"{Core.Common.JitWarmup.ElapsedMs:0} ms (background)", "game_units");
        }

        void OnDestroy()
        {
            if (time == null) return;
            time.OnHourlyTick -= OnHour;
            time.OnDailyTick -= OnDay;
        }

        void OnHour(int hour)
        {
            Measure(0, () => Hoi4Combat.Instance?.SimulateHour());
            Measure(1, () => Hoi4BattlePlans.Instance?.SimulateHour());
            if (RunsAI) Measure(2, () =>
            {
                Hoi4EnemyAI.Instance?.SimulateHour();
                // new attacks: each AI country at its own hour of the day (spreads the work over the day)
                var gs = Engine.ArchonEngine.Instance.GameState;
                Hoi4EnemyAI.Instance?.DecideAttacks((int)(gs.Time.CurrentTick % 24));
            });
            if (RunsAI) DailyAI();
            Measure(3, () => Hoi4Recorder.Instance?.AfterHour()); // reads only, after everything this hour did
        }

        /// <summary>The AI's once-a-day reviews, each at its own hour instead of all in the day's first frame (that
        /// frame also runs every daily system). The order of a day is unchanged; a review a few hours later is
        /// the same review. Their time is in the next day's perf row.</summary>
        void DailyAI()
        {
            ulong tick = Engine.ArchonEngine.Instance.GameState.Time.CurrentTick;
            int day = (int)(tick / 24);
            switch ((int)(tick % 24))
            {
                case 3: Measure(16, () => Hoi4.Deployment.Hoi4ArmyAI.SimulateDay(day)); break; // army plans: role templates, mix, factory split
                case 6: Measure(11, () => Hoi4.Production.Hoi4ProductionAI.SimulateDay(day)); break; // factories to lines, each country weekly
                case 9: Measure(13, () => Hoi4.Construction.Hoi4ConstructionAI.SimulateDay(day)); break; // construction queue, each country weekly
                case 12: Measure(21, () => Hoi4.Research.Hoi4ResearchAI.SimulateDay(day)); break; // fills empty research slots
                case 15: Measure(22, () => Hoi4.Production.Hoi4TankDesigns.SimulateDay(day, PlayerCountry)); break; // tank designs (the player keeps its own)
                case 18: Measure(15, () => Hoi4.Deployment.Hoi4DeploymentAI.SimulateDay(day)); break; // training orders, each country weekly
                case 21: Measure(24, () => Hoi4.Focus.Hoi4FocusAI.SimulateDay(day)); break; // national focus picks, each country weekly
            }
        }

        void OnDay(int day)
        {
            Measure(19, () => Hoi4.Weather.Hoi4Weather.SimulateDay(Engine.ArchonEngine.Instance.GameState)); // weather, snow and mud: before supply reads them
            Measure(17, () => Hoi4.Supply.Hoi4Railways.SimulateDay(Engine.ArchonEngine.Instance.GameState)); // rail conversion, hub connections to the capital
            Hoi4Occupation.SettleStanding(Engine.ArchonEngine.Instance.GameState); // a division left standing on enemy land takes it
            Hoi4ReturnHome.SimulateDay(Engine.ArchonEngine.Instance.GameState); // divisions on a non-enemy's land go home (cheap: no column)
            HaltUnitsCommand.RescueStranded(Engine.ArchonEngine.Instance.GameState); // divisions left standing at sea sail to the nearest port
            Measure(18, () => Hoi4.Supply.Hoi4Supply.SimulateDay(Engine.ArchonEngine.Instance.GameState, Hoi4Divisions.Instance)); // province supply, division supply ratios
            Hoi4UnitCounters.Instance?.MarkDirtyNextFrame(); // the low supply crates follow the day's ratios (drawn a frame later)
            Hoi4.Supply.Hoi4SupplyMapMode.Refresh(); // and the supply map mode, at once
            Measure(4, () => Hoi4Combat.Instance?.SimulateDay()); // entrenchment
            Hoi4Training.SimulateDay(Engine.ArchonEngine.Instance.GameState, Hoi4Divisions.Instance); // exercises: experience (cheap: only the divisions on one)
            Hoi4.Modifiers.Hoi4Stability.SimulateDay(); // landmarks to their province's controller (cheap: only on control changes)
            Measure(12, () => Hoi4.Construction.Hoi4Construction.Instance?.SimulateDay()); // civilian factories build; new factories count from here
            Measure(10, () => Hoi4.Production.Hoi4Production.Instance?.SimulateDay()); // factories to lines, output into the stockpiles
            Measure(5, () => Hoi4Equipment.Instance?.SimulateDay()); // equipment deliveries from the stockpiles
            Measure(20, () => Hoi4.Research.Hoi4Research.Instance?.SimulateDay()); // empty research slots save a day
            Measure(23, () =>
            {
                var research = Hoi4.Research.Hoi4Research.Instance;
                var gs = Engine.ArchonEngine.Instance.GameState;
                Hoi4.War.Hoi4WorldTension.Initialize(gs); // listens for wars and peace from here on
                Hoi4.War.Hoi4WorldTension.SimulateDay(); // tension from countries at peace decays
                if (research != null && research.IsReady)
                {
                    Hoi4.Modifiers.Hoi4PoliticalPower.SimulateDay(research.Countries); // the day's political power
                    Hoi4.Modifiers.Hoi4Politics.Initialize(gs);
                    Hoi4.Modifiers.Hoi4Politics.SimulateDay(research.Countries); // party popularity drift
                }
                Hoi4.Focus.Hoi4Focus.Instance?.SimulateDay(); // a day on each country's focus; finished ones run their reward script
            });
            Measure(14, () => Hoi4.Deployment.Hoi4Deployment.Instance?.SimulateDay()); // training lines: men, the equipment the field left, training, deploying
            Measure(6, () => Hoi4Manpower.Instance?.SimulateDay()); // manpower growth, men delivered, strength follows
            Measure(7, () => Hoi4BattlePlans.Instance?.SimulateDay());
            Measure(8, () => Hoi4.War.Hoi4War.Instance?.SimulateDay()); // surrender progress and capitulation (may open the peace conference)
            if (RunsAI) Measure(9, () => Hoi4.War.Hoi4AiWars.Instance?.SimulateDay()); // AI war declarations (stress test, F8)
            WritePerf();
        }

        /// <summary>Archon's lockstep model: the AI decides on the host only and its decisions reach the other
        /// clients as commands. Everything else here is deterministic simulation that every client runs.</summary>
        static ushort PlayerCountry
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        static bool RunsAI => Engine.ArchonEngine.Instance?.GameState?.CommandProcessor?.IsAuthoritative ?? true;
    }
}
