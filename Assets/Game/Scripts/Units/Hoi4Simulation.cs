using System.Collections;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the one place the HOI4 simulation runs on game time, in a fixed order. Each system used to
    /// subscribe to the hourly tick on its own, so the order depended on which finished loading first.
    /// Every hour: combat (battles, retreats, organisation), then battle plans (lines follow the border,
    /// divisions take their spots), then the AI calling off bad attacks. Every day, after that day's last
    /// hour: plans re-check which AI lines exist, then the AI picks new attacks.
    /// Occupation is not here: it follows unit arrivals, which the engine's movement queue raises.
    /// A system that hasn't finished loading skips the tick.
    /// </summary>
    public sealed class Hoi4Simulation : MonoBehaviour
    {
        Core.Systems.TimeManager time;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Simulation");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Simulation>();
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Time == null)
                yield return null;
            time = Engine.ArchonEngine.Instance.GameState.Time;
            time.OnHourlyTick += OnHour;
            time.OnDailyTick += OnDay;
        }

        void OnDestroy()
        {
            if (time == null) return;
            time.OnHourlyTick -= OnHour;
            time.OnDailyTick -= OnDay;
        }

        void OnHour(int hour)
        {
            Hoi4Combat.Instance?.SimulateHour();
            Hoi4BattlePlans.Instance?.SimulateHour();
            if (RunsAI) Hoi4EnemyAI.Instance?.SimulateHour();
            Hoi4Recorder.Instance?.AfterHour(); // reads only, after everything this hour did
        }

        void OnDay(int day)
        {
            Hoi4Combat.Instance?.SimulateDay(); // entrenchment
            Hoi4BattlePlans.Instance?.SimulateDay();
            if (RunsAI) Hoi4EnemyAI.Instance?.SimulateDay();
        }

        /// <summary>Archon's lockstep model: the AI decides on the host only and its decisions reach the other
        /// clients as commands. Everything else here is deterministic simulation that every client runs.</summary>
        static bool RunsAI => Engine.ArchonEngine.Instance?.GameState?.CommandProcessor?.IsAuthoritative ?? true;
    }
}
