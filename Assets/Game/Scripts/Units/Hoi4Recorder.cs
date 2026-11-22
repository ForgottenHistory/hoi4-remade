using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Core;
using Core.Recording;
using Hoi4.Map;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME (debug): records play sessions with the engine's SessionRecorder so the game can be read
    /// afterwards (Tools/recorder, report.py first). Policy only: when to record (in the editor, once the
    /// player has a country), where (Recordings/ at the project root, gitignored), and the HOI4 tables.
    /// The engine adds meta.json, countries, commands, log and notes (SessionRecorder.Note, called by the
    /// AI, plans and combat); ProvinceControlSource adds control.tsv. The HOI4 tables:
    ///   divisions.tsv    a row whenever a division's province, destination, plan, slot, army or state changes;
    ///                    a full snapshot ("snap") at the start and every snapshotDays; "gone" when it dies
    ///   plans.tsv        a row whenever a plan's figures change: divisions, slotted, holding, marching, away
    ///                    (idle but not on its slot: should not last), unslotted; "removed" at the end
    ///   plan_fronts.tsv  the provinces of each front of a plan, in line order, whenever they change
    ///   battles.tsv      every battle every hour: who fights, who is within the width, the bar, organisation
    /// Records after everything else in the hour (Hoi4Simulation) and only reads.
    /// </summary>
    public sealed class Hoi4Recorder : MonoBehaviour
    {
        [Tooltip("Record play sessions in the editor (Recordings/ at the project root)")] public bool record = true;
        [Tooltip("Recordings to keep; older ones are deleted when a new one starts")] public int keep = 12;
        [Tooltip("Full division snapshot every this many game days")] public int snapshotDays = 7;

        public static Hoi4Recorder Instance { get; private set; }

        SessionRecorder session;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            if (!Application.isEditor) return;
            var go = new GameObject("Hoi4Recorder");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Recorder>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            session?.Stop();
            session = null;
            if (Instance == this) Instance = null;
        }

        void OnApplicationQuit() => session?.Stop();

        static ushort Player
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        IEnumerator Start()
        {
            if (!record) yield break;
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Time == null
                   || StarterKit.Initializer.Instance == null || !StarterKit.Initializer.Instance.IsInitialized)
                yield return null;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            // Once the player has a country (or time runs without one: an observer game)
            while (Player == 0 && gameState.Time.CurrentTick == 0) yield return null;

            string tag = Player != 0 ? gameState.Countries.GetCountryTag(Player) : "OBS";
            var options = new RecordingOptions
            {
                Root = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Recordings"),
                Name = $"{DateTime.Now:yyyyMMdd_HHmmss}_{tag}",
                Keep = keep,
            };
            options.Meta["player"] = Player != 0 ? tag : "";
            options.Meta["player_id"] = Player.ToString();
            session = SessionRecorder.Start(gameState, options);
            if (session == null) yield break;
            session.Add(new BattlesSource());
            session.Add(new DivisionsSource(snapshotDays));
            session.Add(new PlansSource());
            session.Add(new ProvinceControlSource(p => !Hoi4Regions.IsWater(p)));
            session.Flush();
        }

        /// <summary>From Hoi4Simulation at the end of every game hour.</summary>
        public void AfterHour() => session?.Record();

        // --- HOI4 tables ------------------------------------------------------------------------------

        sealed class BattlesSource : IRecordingSource
        {
            RecordingTable table;

            public void Begin(SessionRecorder r) =>
                table = r.Table("battles", "to\tfrom\tpercent\tattackers\tdefenders\tattacker_front\tdefender_front\tattacker_org\tdefender_org" +
                                           "\tterrain\twidth\tdirections\triver\tdefender_entrenchment");

            public void Record(SessionRecorder r)
            {
                var gs = Engine.ArchonEngine.Instance?.GameState;
                foreach (var b in Hoi4Combat.Battles)
                {
                    if (b.Attackers == null) continue;
                    table.Row($"{b.To}\t{b.From}\t{b.AttackerPercent}\t{SessionRecorder.Join(b.Attackers)}\t{SessionRecorder.Join(b.Defenders)}" +
                              $"\t{SessionRecorder.Join(b.AttackerFront)}\t{SessionRecorder.Join(b.DefenderFront)}\t{OrgSum(b.Attackers)}\t{OrgSum(b.Defenders)}" +
                              $"\t{Hoi4.Map.Hoi4CombatTerrain.TerrainKey(gs, b.To)}\t{b.Width}\t{b.Directions}\t{(b.River == 2 ? "large" : b.River == 1 ? "small" : "")}" +
                              $"\t{MeanEntrenchment(b.DefenderFront)}");
                }
            }

            static string MeanEntrenchment(List<ushort> ids)
            {
                float sum = 0; int n = 0;
                foreach (ushort id in ids)
                {
                    var d = Hoi4Divisions.Instance?.Get(id);
                    if (d == null) continue;
                    sum += d.Entrenchment.ToFloat(); n++;
                }
                return n > 0 ? $"{sum / n:0.#}" : "";
            }

            static string OrgSum(List<ushort> ids)
            {
                float org = 0, max = 0;
                foreach (ushort id in ids)
                {
                    var d = Hoi4Divisions.Instance?.Get(id);
                    if (d == null) continue;
                    org += d.Organisation.ToFloat();
                    max += d.MaxOrganisation.ToFloat();
                }
                return $"{org:0}/{max:0}";
            }
        }

        sealed class DivisionsSource : IRecordingSource
        {
            readonly int snapshotDays;
            readonly Dictionary<ushort, string> last = new Dictionary<ushort, string>();
            readonly Dictionary<ushort, (ushort, ushort, ushort, int, ushort, int, string)> lastValues =
                new Dictionary<ushort, (ushort, ushort, ushort, int, ushort, int, string)>(); // compared hourly, no text
            RecordingTable table;
            ulong nextSnapshot;

            public DivisionsSource(int snapshotDays) => this.snapshotDays = Math.Max(1, snapshotDays);

            public void Begin(SessionRecorder r)
            {
                table = r.Table("divisions", "why\tid\ttag\tprovince\tdest\tplan\tslot\tarmy\tstate\torg\tstr");
                nextSnapshot = r.Tick;
                Record(r); // the starting state
            }

            static ushort Destination(GameState gs, ushort id) => gs.Units.MovementQueue.GetFinalDestination(id);

            public void Record(SessionRecorder r)
            {
                var gs = r.GameState;
                var units = gs.Units;
                var divisions = Hoi4Divisions.Instance;
                var plans = Hoi4BattlePlans.Instance;
                var armies = Hoi4Armies.Instance;
                bool snapshot = r.Tick >= nextSnapshot;
                if (snapshot) nextSnapshot = r.Tick + (ulong)(snapshotDays * 24);

                var attacking = new HashSet<ushort>();
                var defending = new HashSet<ushort>();
                foreach (var b in Hoi4Combat.Battles)
                {
                    if (b.Attackers != null) attacking.UnionWith(b.Attackers);
                    if (b.Defenders != null) defending.UnionWith(b.Defenders);
                }

                var ids = units.GetAllUnits();
                ids.Sort();
                var alive = new HashSet<ushort>();
                foreach (ushort id in ids)
                {
                    alive.Add(id);
                    var u = units.GetUnit(id);
                    ushort dest = Destination(gs, id);
                    var plan = plans?.PlanOf(id);
                    ushort slot = plan != null && plan.Slot.TryGetValue(id, out ushort s) ? s : (ushort)0;
                    int army = armies?.ArmyOf(id)?.Id ?? 0;
                    string state =
                        Hoi4Combat.IsRetreating(id) ? "retreat" :
                        attacking.Contains(id) ? "attack" :
                        defending.Contains(id) ? "defend" :
                        plans != null && plans.IsBorrowed(id) ? "ai_attack" :
                        plans != null && plans.HasManualOrder(id) ? (dest != 0 ? "manual_march" : "manual") :
                        dest != 0 ? "march" : "idle";
                    var values = (u.countryID, u.provinceID, dest, plan?.Id ?? 0, slot, army, state);
                    bool changed = !lastValues.TryGetValue(id, out var before) || !before.Equals(values);
                    if (!changed && !snapshot) continue;
                    lastValues[id] = values;
                    string key = $"{r.Tag(u.countryID)}\t{u.provinceID}\t{dest}\t{plan?.Id ?? 0}\t{slot}\t{army}\t{state}";
                    last[id] = key;
                    var d = divisions?.Get(id);
                    string org = d != null && d.MaxOrganisation.ToFloat() > 0 ? ((int)(100 * d.Organisation.ToFloat() / d.MaxOrganisation.ToFloat())).ToString() : "";
                    string str = d != null && d.MaxStrength.ToFloat() > 0 ? ((int)(100 * d.Strength.ToFloat() / d.MaxStrength.ToFloat())).ToString() : "";
                    table.Row($"{(snapshot ? "snap" : "change")}\t{id}\t{key}\t{org}\t{str}");
                }
                var gone = new List<ushort>();
                foreach (ushort id in last.Keys) if (!alive.Contains(id)) gone.Add(id);
                gone.Sort();
                foreach (ushort id in gone)
                {
                    table.Row($"gone\t{id}\t{last[id]}\t\t");
                    last.Remove(id);
                    lastValues.Remove(id);
                }
            }
        }

        sealed class PlansSource : IRecordingSource
        {
            readonly Dictionary<int, string> last = new Dictionary<int, string>();
            readonly Dictionary<int, object> lastFronts = new Dictionary<int, object>(); // the fronts list: replaced when the line moves
            RecordingTable plansTable, frontsTable;

            public void Begin(SessionRecorder r)
            {
                plansTable = r.Table("plans", "plan\towner\tai\tcountry\tarmy\tdivisions\tslotted\tholding\tmarching\taway\tunslotted\tfronts");
                frontsTable = r.Table("plan_fronts", "plan\tfront\tcount\tprovinces");
                Record(r);
            }

            public void Record(SessionRecorder r)
            {
                var plans = Hoi4BattlePlans.Instance;
                if (plans == null) return;
                var units = r.GameState.Units;
                var queue = units.MovementQueue;
                var alive = new HashSet<int>();
                foreach (var plan in plans.Plans)
                {
                    alive.Add(plan.Id);
                    int slotted = 0, holding = 0, marching = 0, away = 0, unslotted = 0;
                    foreach (ushort u in plan.Units)
                    {
                        if (!units.HasUnit(u)) continue;
                        if (!plan.Slot.TryGetValue(u, out ushort slot)) { unslotted++; continue; }
                        slotted++;
                        if (units.GetUnit(u).provinceID == slot) holding++;
                        else if (queue.IsUnitMoving(u)) marching++;
                        else away++;
                    }
                    string fronts = "none";
                    if (plan.Fronts != null && plan.Fronts.Count > 0)
                    {
                        var parts = new List<string>();
                        foreach (var f in plan.Fronts) parts.Add(f.Count.ToString());
                        fronts = string.Join("+", parts);
                    }
                    string key = $"{r.Tag(plan.Owner)}\t{(plan.IsAI ? 1 : 0)}\t{r.Tag(plan.Country)}\t{plan.ArmyId}\t{plan.Units.Count}\t{slotted}\t{holding}\t{marching}\t{away}\t{unslotted}\t{fronts}";
                    if (!last.TryGetValue(plan.Id, out string before) || before != key)
                    {
                        last[plan.Id] = key;
                        plansTable.Row($"{plan.Id}\t{key}");
                    }

                    if (plan.Fronts == null) continue;
                    if (lastFronts.TryGetValue(plan.Id, out object beforeF) && ReferenceEquals(beforeF, plan.Fronts)) continue;
                    lastFronts[plan.Id] = plan.Fronts;
                    for (int i = 0; i < plan.Fronts.Count; i++)
                        frontsTable.Row($"{plan.Id}\t{i}\t{plan.Fronts[i].Count}\t{string.Join(",", plan.Fronts[i])}");
                }
                var removed = new List<int>();
                foreach (int id in last.Keys) if (!alive.Contains(id)) removed.Add(id);
                removed.Sort();
                foreach (int id in removed)
                {
                    plansTable.Row($"{id}\tremoved\t\t\t\t\t\t\t\t\t\t");
                    last.Remove(id);
                    lastFronts.Remove(id);
                }
            }
        }
    }
}
