using System;
using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Systems;
using Core.Units;
using StarterKit.Commands;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4-style division selection and move orders.
    /// - Left-click a counter: select its divisions (Shift adds/removes). Any country's counters can
    ///   be selected to inspect them; only the player's divisions take orders.
    /// - Left-click elsewhere on the map: clear the unit selection (the province click goes on).
    /// - Right-click a province with own divisions selected: path and move there.
    /// - Escape: clear the selection.
    /// Clicks reach this through the engine ProvinceSelector's click interceptors, so a counter
    /// click doesn't also select the province under it; a pointer blocker stops the province hover
    /// highlight under counters. Move arrows for the selection are drawn by Hoi4MoveArrows.
    /// </summary>
    public sealed class Hoi4UnitSelection : MonoBehaviour
    {
        public static Hoi4UnitSelection Instance { get; private set; }

        readonly HashSet<ushort> selection = new HashSet<ushort>();
        readonly List<ushort> ordered = new List<ushort>(); // selection in click order, for the panel

        GameState gameState;
        global::Map.Interaction.ProvinceSelector selector;
        Func<int, Vector2, bool> interceptor;
        Func<Vector2, bool> pointerBlocker;
        IDisposable[] subscriptions;

        /// <summary>Fires when the selection changes or a selected division moves or gets orders.</summary>
        public event Action Changed;

        public IReadOnlyList<ushort> Selected => ordered;
        public bool IsSelected(ushort unitId) => selection.Contains(unitId);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (Instance != null) return;
            var go = new GameObject("Hoi4UnitSelection");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4UnitSelection>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (selector != null && interceptor != null) selector.RemoveClickInterceptor(interceptor);
            if (selector != null && pointerBlocker != null) selector.RemovePointerBlocker(pointerBlocker);
            if (subscriptions != null)
                foreach (var s in subscriptions) s?.Dispose();
        }

        IEnumerator Start()
        {
            while (Hoi4UnitCounters.Instance == null || !Hoi4UnitCounters.Instance.IsReady)
                yield return null;

            var engine = Engine.ArchonEngine.Instance;
            gameState = engine.GameState;
            selector = engine.ProvinceSelector;
            if (selector == null)
            {
                ArchonLogger.LogWarning("Hoi4UnitSelection: no ProvinceSelector, unit selection disabled", "game_units");
                yield break;
            }
            Hoi4UnitCounters.Instance.Selection = selection;
            interceptor = OnClick;
            selector.AddClickInterceptor(interceptor);
            // No province hover highlight under counters
            pointerBlocker = p => Hoi4UnitCounters.Instance != null && Hoi4UnitCounters.Instance.TryPick(p, out _);
            selector.AddPointerBlocker(pointerBlocker);

            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<UnitDestroyedEvent>(e => { if (selection.Remove(e.UnitID)) { ordered.Remove(e.UnitID); OnSelectionChanged(); } }),
                gameState.EventBus.Subscribe<UnitMovedEvent>(e => { if (selection.Contains(e.UnitID)) Changed?.Invoke(); }),
                gameState.EventBus.Subscribe<DailyTickEvent>(_ => { if (selection.Count > 0) Changed?.Invoke(); }),
            };
            ArchonLogger.Log("Hoi4UnitSelection: ready", "game_units");
        }

        void Update()
        {
            if (gameState == null) return;
            if (Input.GetKeyDown(KeyCode.Escape) && selection.Count > 0)
                Clear();
        }

        // --- Input ---------------------------------------------------------------------------

        bool OnClick(int button, Vector2 screen)
        {
            var counters = Hoi4UnitCounters.Instance;
            if (button == 0)
            {
                if (counters != null && counters.TryPick(screen, out var units))
                {
                    bool additive = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
                    if (!additive) ClearSilently();
                    bool allSelected = true;
                    foreach (var u in units) allSelected &= selection.Contains(u);
                    foreach (var u in units)
                    {
                        if (additive && allSelected) { selection.Remove(u); ordered.Remove(u); }
                        else if (selection.Add(u)) ordered.Add(u);
                    }
                    OnSelectionChanged();
                    selector.ClearSelection(); // selecting divisions closes the province selection, like HOI4
                    return true; // don't select the province under the counter
                }
                if (selection.Count > 0) Clear();
                return false;
            }

            if (button == 1 && selection.Count > 0)
            {
                var cam = Camera.main;
                ushort target = cam != null ? selector.GetProvinceAtScreenPosition(screen, cam) : (ushort)0;
                if (target != 0) IssueMove(target);
                return true; // with divisions selected, right-click is a unit order, not a province action
            }
            return false;
        }

        int IssueMove(ushort target)
        {
            var player = StarterKit.Initializer.Instance?.PlayerState;
            var unitTypes = StarterKit.Initializer.Instance?.UnitSystem;
            if (player == null || !player.HasPlayerCountry || gameState.Pathfinding == null || !gameState.Pathfinding.IsInitialized)
                return 0;
            ushort country = player.PlayerCountryId;

            var paths = new Dictionary<ushort, List<ushort>>(); // per start province
            int issued = 0, noPath = 0;
            foreach (ushort id in ordered)
            {
                var unit = gameState.Units.GetUnit(id);
                if (unit.unitCount == 0 || unit.countryID != country) continue;
                if (unit.provinceID == target)
                {
                    if (gameState.Units.MovementQueue.IsUnitMoving(id))
                        gameState.Units.MovementQueue.CancelMovement(id); // ordered back to where it is
                    continue;
                }
                if (!paths.TryGetValue(unit.provinceID, out var path))
                    paths[unit.provinceID] = path = gameState.Pathfinding.FindPath(unit.provinceID, target, country);
                if (path == null || path.Count < 2) { noPath++; continue; }

                var type = unitTypes?.GetUnitType(unit.unitTypeID);
                var command = new QueueUnitMovementCommand
                {
                    UnitId = id,
                    Path = new List<ushort>(path),
                    MovementDays = type?.Speed ?? 3,
                    CountryId = country,
                };
                if (gameState.TryExecuteCommand(command, out string message)) issued++;
                else ArchonLogger.LogWarning($"Hoi4UnitSelection: move order for unit {id} failed: {message}", "game_units");
            }
            ArchonLogger.Log($"Hoi4UnitSelection: {issued} divisions ordered to province {target}" + (noPath > 0 ? $", {noPath} without a path" : ""), "game_units");
            Changed?.Invoke();
            return issued;
        }

        // --- Selection -----------------------------------------------------------------------

        public void Clear()
        {
            if (selection.Count == 0) return;
            ClearSilently();
            OnSelectionChanged();
        }

        /// <summary>Select exactly these units (e.g. from the division panel).</summary>
        public void Select(IEnumerable<ushort> units)
        {
            ClearSilently();
            foreach (var u in units)
                if (selection.Add(u)) ordered.Add(u);
            OnSelectionChanged();
        }

        void ClearSilently()
        {
            selection.Clear();
            ordered.Clear();
        }

        void OnSelectionChanged()
        {
            Hoi4UnitCounters.Instance?.MarkDirty();
            Changed?.Invoke();
        }
    }
}
