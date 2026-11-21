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
    /// - Left-drag on the map: box-select your own divisions (Shift adds). The engine's
    ///   ProvinceSelector fires left clicks on release here, and not after a drag, so dragging a box
    ///   doesn't also click the province where it started.
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
        Hoi4MovementRules movementRules;

        // Box selection
        public Color boxBorder = new Color(1f, 0.93f, 0.7f, 0.9f);
        public Color boxFill = new Color(1f, 0.93f, 0.7f, 0.08f);
        bool boxActive;
        Vector2 boxStart;
        IDisposable[] subscriptions;

        /// <summary>Fires when the selection changes or a selected division moves or gets orders.</summary>
        public event Action Changed;

        public IReadOnlyList<ushort> Selected => ordered;

        /// <summary>
        /// The divisions the panel lists: the selection, widened to the whole army when the selection sits
        /// inside one. Splitting an army's selection (S, select-half) keeps the army listed, with the
        /// deselected divisions dark; unassigned divisions drop out of the panel once deselected.
        /// </summary>
        public IReadOnlyList<ushort> Context => context;
        readonly List<ushort> context = new List<ushort>();
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
            if (selector != null) selector.LeftClickOnRelease = false;
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

            // HOI4 movement rules (water, impassable states, access) as the pathfinding default.
            // After StarterKit's init, which installs its own terrain calculator.
            while (StarterKit.Initializer.Instance != null && !StarterKit.Initializer.Instance.IsInitialized)
                yield return null;
            if (gameState.Pathfinding != null)
                gameState.Pathfinding.SetDefaultCostCalculator(movementRules = new Hoi4MovementRules(gameState));

            interceptor = OnClick;
            selector.AddClickInterceptor(interceptor);
            // No province hover highlight under counters
            pointerBlocker = p => Hoi4UnitCounters.Instance != null && Hoi4UnitCounters.Instance.TryPick(p, out _);
            selector.AddPointerBlocker(pointerBlocker);
            // HOI4: a click acts on release, a left-drag draws a selection box instead
            selector.LeftClickOnRelease = true;

            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<UnitDestroyedEvent>(e => { context.Remove(e.UnitID); if (selection.Remove(e.UnitID)) { ordered.Remove(e.UnitID); OnSelectionChanged(true); } else Changed?.Invoke(); }),
                gameState.EventBus.Subscribe<UnitMovedEvent>(e => { if (selection.Contains(e.UnitID)) Changed?.Invoke(); }),
                gameState.EventBus.Subscribe<HourlyTickEvent>(_ => { if (selection.Count > 0) Changed?.Invoke(); }),
            };
            ArchonLogger.Log("Hoi4UnitSelection: ready", "game_units");
        }

        void Update()
        {
            if (gameState == null) return;
            if (Input.GetKeyDown(KeyCode.Escape) && selection.Count > 0)
                Clear();
            if (Input.GetKeyDown(KeyCode.S) && !Input.GetKey(KeyCode.LeftControl) && !Input.GetKey(KeyCode.RightControl))
                Hoi4DivisionPresenter.SelectHalf(this);
            if (Input.GetKeyDown(KeyCode.H)) HaltSelected();
            UpdateBox();
        }

        /// <summary>H: the selected own divisions stop where they stand, whether marching or attacking.</summary>
        void HaltSelected()
        {
            var p = StarterKit.Initializer.Instance?.PlayerState;
            if (p == null || !p.HasPlayerCountry) return;
            foreach (ushort id in new List<ushort>(selection))
                if (gameState.Units.HasUnit(id) && gameState.Units.GetUnit(id).countryID == p.PlayerCountryId)
                    gameState.Units.MovementQueue.CancelMovement(id);
            Hoi4UnitCounters.Instance?.MarkDirty();
        }

        // --- Box selection ---------------------------------------------------------------------

        void UpdateBox()
        {
            if (selector == null) return;
            if (selector.IsLeftDragging && !boxActive)
            {
                boxActive = true;
                boxStart = selector.LeftPressPosition;
            }
            if (!boxActive) return;
            if (Input.GetMouseButton(0) && !Input.GetMouseButtonUp(0)) return;

            boxActive = false;
            var box = BoxRect(boxStart, Input.mousePosition);
            var player = StarterKit.Initializer.Instance?.PlayerState;
            ushort country = player != null && player.HasPlayerCountry ? player.PlayerCountryId : (ushort)0;
            if (country == 0 || Hoi4UnitCounters.Instance == null) return;

            // HOI4 box-selects only your own divisions
            var units = Hoi4UnitCounters.Instance.UnitsInScreenRect(box, country);
            bool additive = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            if (!additive) ClearSilently();
            foreach (var u in units)
                if (selection.Add(u)) ordered.Add(u);
            OnSelectionChanged();
            if (units.Count > 0) selector.ClearSelection();
        }

        static Rect BoxRect(Vector2 a, Vector2 b) =>
            Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));

        void OnGUI()
        {
            if (!boxActive || Event.current.type != EventType.Repaint) return;
            var r = BoxRect(boxStart, Input.mousePosition);
            var gui = new Rect(r.xMin, Screen.height - r.yMax, r.width, r.height); // GUI: origin top-left
            var tex = Texture2D.whiteTexture;
            var old = GUI.color;
            GUI.color = boxFill;
            GUI.DrawTexture(gui, tex);
            GUI.color = boxBorder;
            GUI.DrawTexture(new Rect(gui.xMin, gui.yMin, gui.width, 1), tex);
            GUI.DrawTexture(new Rect(gui.xMin, gui.yMax - 1, gui.width, 1), tex);
            GUI.DrawTexture(new Rect(gui.xMin, gui.yMin, 1, gui.height), tex);
            GUI.DrawTexture(new Rect(gui.xMax - 1, gui.yMin, 1, gui.height), tex);
            GUI.color = old;
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

            if (Hoi4Combat.IsAttackedFrom(gameState, country, target))
            {
                ArchonLogger.Log($"Hoi4UnitSelection: province {target} is attacking us, it can't be attacked back", "game_units");
                return 0;
            }
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
            string why = "";
            if (noPath > 0 && movementRules != null)
            {
                ushort owner = gameState.Provinces.GetProvinceOwner(target);
                why = Hoi4.Map.Hoi4Regions.IsWater(target) ? " (sea)"
                    : Hoi4.Map.Hoi4Regions.IsImpassable(target) ? " (impassable)"
                    : !movementRules.HasAccess(country, owner) ? " (no military access)"
                    : " (unreachable)";
            }
            ArchonLogger.Log($"Hoi4UnitSelection: {issued} divisions ordered to province {target}" + (noPath > 0 ? $", {noPath} without a path{why}" : ""), "game_units");
            Hoi4UnitCounters.Instance?.MarkDirty(); // the counters show a move arrow now
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

        /// <summary>Change the selection to a subset of the listed divisions, keeping the panel's context.</summary>
        public void SelectSubset(IEnumerable<ushort> units)
        {
            ClearSilently();
            foreach (var u in units)
                if (selection.Add(u)) ordered.Add(u);
            OnSelectionChanged(true);
        }

        /// <summary>Select exactly these units (e.g. from the division panel).</summary>
        public void Select(IEnumerable<ushort> units)
        {
            ClearSilently();
            foreach (var u in units)
                if (selection.Add(u)) ordered.Add(u);
            OnSelectionChanged();
        }

        void RebuildContext()
        {
            context.Clear();
            if (ordered.Count == 0) return;
            var army = Hoi4Armies.Instance?.ArmyOf(ordered[0]);
            foreach (ushort u in ordered)
                if (Hoi4Armies.Instance?.ArmyOf(u) != army) { army = null; break; }
            context.AddRange(army != null ? army.Units : ordered);
        }

        void ClearSilently()
        {
            selection.Clear();
            ordered.Clear();
        }

        void OnSelectionChanged(bool _ = false)
        {
            RebuildContext(); // an army stays listed whole; unassigned divisions are listed only while selected
            Hoi4UnitCounters.Instance?.MarkDirty();
            Changed?.Invoke();
        }
    }
}
