using System.Collections.Generic;
using Hoi4.Map;
using Map.Rendering.Border;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>
    /// Fallback lines, HOI4's way (btn_order_draw_fallback, arrowType fallback_line): a line across land the army
    /// may stand on, away from any border, that its divisions march to and hold. With a division of an army
    /// selected the fallback button turns the tool on and stripes every province a line may cross yellow: the
    /// player's land and land it may walk on (allies), not held by an enemy. Pressing the right button on one and
    /// dragging picks the provinces the cursor crosses (green), with the offensive drag's dashed cursor trail and
    /// end rings, and the line itself drawn as it will be placed: a row of X through the picked provinces'
    /// centres, in the army's colour. Releasing places it; the selected divisions on no other line are assigned to it.
    /// A fallback line is a battle plan (Hoi4BattlePlan.Line set, no country): one front, its provinces in drag
    /// order, so the divisions spread over it, hold it and return to it like on a front line (Distribute, Hold),
    /// and the assign, unassign and delete tools work on it. It doesn't follow a border: it stays where it was
    /// drawn. Provinces of it an enemy takes are left out until they are held again; with none left the line is
    /// removed. Offensives can't start from it and it doesn't give the army play buttons.
    /// Look: Tools/prototypes/battleplan/fallback.py, against HOI4 screenshots.
    /// </summary>
    public sealed partial class Hoi4BattlePlans
    {
        [Header("Fallback lines (map pixels)")]
        [Tooltip("Fallback line width: the height of an X (HOI4: about 2.1 map px)")] public float fallbackWidthPx = 2.2f;
        [Tooltip("An X every this many line widths (HOI4: about 2.9 map px apart)")] public float fallbackTile = 1.4f;
        [Tooltip("How much the line through the province centres is smoothed (relax passes at 1 map px)")] public int fallbackSmooth = 60;

        /// <summary>True while the fallback line tool is on.</summary>
        public bool FallbackActive { get; private set; }

        // --- Tool ------------------------------------------------------------------------------------

        public void BeginFallback()
        {
            if (!ready || !HasArmySelection) return;
            if (Active) Cancel();
            if (OffensiveActive) CancelOffensive();
            CancelDelete();
            CancelAssign();
            FallbackActive = true;
            hoverProvince = 0;
            EndDrag();
            // The tool's valid provinces (the drag picks among them, like the offensive tool's)
            offensiveTargets.Clear();
            ushort player = Player;
            foreach (ushort p in landProvinces)
                if (CanHoldAt(player, p)) offensiveTargets.Add(p);
            dirtyTex = true;
            RebuildTexture();
            Changed?.Invoke();
        }

        public void CancelFallback()
        {
            if (!FallbackActive) return;
            FallbackActive = false;
            offensiveTargets.Clear();
            EndDrag();
            dirtyTex = true;
            RebuildTexture();
            Changed?.Invoke();
        }

        void UpdateFallbackTool()
        {
            if (global::Utils.KeyboardFocus.KeyDown(KeyCode.Escape)) { toolEscapeFrame = Time.frameCount; CancelFallback(); return; }
            if (!HasArmySelection) { CancelFallback(); return; }
            ushort province = 0;
            var cam = Camera.main;
            var selector = Engine.ArchonEngine.Instance.ProvinceSelector;
            if (cam != null && selector != null && !global::Map.CameraControllers.BaseCameraController.ScrollCaptured)
                province = selector.GetProvinceAtScreenPosition(Input.mousePosition, cam);
            if (province != hoverProvince)
            {
                hoverProvince = province;
                dirtyTex = true;
            }
            if (dragging)
            {
                if (!Input.GetMouseButton(1)) { FinishFallbackDrag(); return; }
                if (cam != null && TryCursorPixel(cam, out var px)) ExtendDrag(px);
            }
            RebuildTexture();
        }

        /// <summary>Right press on a valid province starts the drag (released, the line is placed). Left click cancels.</summary>
        bool OnFallbackClick(int button)
        {
            if (button == 1 && hoverProvince != 0 && offensiveTargets.Contains(hoverProvince) && !dragging)
            {
                dragging = true;
                dragTrail.Clear();
                dragPicked.Clear();
                dragPicked.Add(hoverProvince);
                var cam = Camera.main;
                if (cam != null && TryCursorPixel(cam, out var px)) dragTrail.Add(px);
                dirtyTex = dirtyMesh = true;
                return true;
            }
            if (button == 0) CancelFallback();
            return true;
        }

        void FinishFallbackDrag()
        {
            var line = new List<ushort>(dragPicked);
            var selected = ArmySelection(out var army);
            CancelFallback();
            if (selected == null || line.Count == 0) return;
            selected.Sort();
            gameState.TryExecuteCommand(new PlaceFallbackLineCommand { CountryId = Player, ArmyId = army.Id, Provinces = line, UnitIds = selected }, out _);
        }

        // --- Simulation --------------------------------------------------------------------------------

        /// <summary>A division of the country can stand in the province without a fight: passable land it has
        /// access to that no enemy holds or stands in (WalkLabels, this hour's).</summary>
        bool CanHoldAt(ushort country, ushort province)
        {
            if (country == 0 || province == 0 || !simReady) return false;
            var walk = WalkLabels(country);
            return province < walk.Length && walk[province] >= 0;
        }

        /// <summary>A fallback line may run through these provinces: each one holdable, none twice.</summary>
        public bool AreFallbackProvinces(ushort owner, List<ushort> provinces)
        {
            if (provinces == null || provinces.Count == 0) return false;
            var seen = new HashSet<ushort>();
            foreach (ushort p in provinces)
                if (!seen.Add(p) || !CanHoldAt(owner, p)) return false;
            return true;
        }

        /// <summary>The line's one front: its provinces that can be held now, in the order it was drawn.</summary>
        List<List<ushort>> FallbackFronts(Hoi4BattlePlan plan)
        {
            var front = new List<ushort>(plan.Line.Count);
            foreach (ushort p in plan.Line)
                if (CanHoldAt(plan.Owner, p)) front.Add(p);
            var fronts = new List<List<ushort>>();
            if (front.Count > 0) fronts.Add(front);
            return fronts;
        }

        /// <summary>PlaceFallbackLineCommand: the army's fallback line through the provinces (in drag order), held by
        /// these divisions (those already on another plan stay there, as with a front line).</summary>
        public Hoi4BattlePlan CreateFallbackLine(ushort owner, int armyId, List<ushort> provinces, List<ushort> unitIds)
        {
            if (!simReady || !AreFallbackProvinces(owner, provinces)) return null;
            var plan = new Hoi4BattlePlan { Id = nextId++, Owner = owner, ArmyId = armyId, Line = new List<ushort>(provinces) };
            SetFronts(plan, FallbackFronts(plan));
            foreach (ushort u in unitIds)
            {
                if (planOf.ContainsKey(u)) continue; // on another line: it stays there
                manualOrders.Remove(u);
                unassigned.Remove(u);
                pinnedOffensive.Remove(u);
                plan.Units.Add(u);
                planOf[u] = plan;
            }
            TraceLine(plan);
            plans.Add(plan);
            Core.Recording.SessionRecorder.Note("plans", "fallback", owner, 0, provinces[0], 0,
                $"plan {plan.Id} army {armyId}: {plan.Units.Count} of {unitIds.Count} selected divisions, provinces {string.Join(",", provinces)}");
            Distribute(plan);
            dirtyMesh = true;
            Changed?.Invoke();
            return plan;
        }

        // --- Drawing (presentation) ----------------------------------------------------------------------

        /// <summary>The line as drawn: through the provinces' centres in order, as one smooth curve like HOI4's
        /// (map px). Relaxed coarse, so the corner at every centre goes (it would skew the X there), then fine.
        /// One province: a stretch one X long.</summary>
        List<Vector2> FallbackSpine(List<ushort> provinces, Hoi4UnitCounters counters)
        {
            var c = new List<Vector2>(provinces.Count);
            foreach (ushort p in provinces)
                if (counters.TryGetProvincePoint(p, out var w)) c.Add(WorldToPixel(w));
            if (c.Count == 0) return null;
            if (c.Count == 1)
            {
                var half = new Vector2(fallbackWidthPx * fallbackTile * 0.5f, 0f);
                return new List<Vector2> { c[0] - half, c[0] + half };
            }
            return Relax(Relax(c, 1f, fallbackSmooth), 0.25f, 4);
        }

        /// <summary>Presentation: a fallback line's drawn line and its label. It never moves, so this runs once.</summary>
        void TraceFallback(Hoi4BattlePlan plan)
        {
            plan.Arcs = new List<BorderArc>();
            plan.Chains = new List<List<Vector3>>();
            plan.ChainFront.Clear();
            var counters = Hoi4UnitCounters.Instance;
            var spine = counters != null ? FallbackSpine(plan.Line, counters) : null;
            if (spine != null && spine.Count >= 2)
            {
                plan.Chains.Add(ToWorld(spine));
                plan.ChainFront.Add(0);
            }
            ComputeLabelAnchor(plan);
        }

        /// <summary>The row of X along the points: whole X only, so the tile is stretched a little to fit the
        /// length. held false: fallback_line's unassigned mask (broken X).</summary>
        void AddFallbackRibbon(List<Vector3> pts, Color color, bool held)
        {
            if (pts.Count < 2) return;
            float length = 0f;
            for (int i = 1; i < pts.Count; i++) length += Vector3.Distance(pts[i - 1], pts[i]);
            float tile = fallbackWidthPx * worldPerPxX * Mathf.Max(fallbackTile, 0.1f);
            int count = Mathf.Max(1, Mathf.RoundToInt(length / tile));
            AddRibbon(pts, color, held ? 7f : 8f, fallbackWidthPx, length / count);
        }

        /// <summary>While dragging a fallback line: the cursor trail dashed, the line as it will be placed and a ring
        /// at each end.</summary>
        void AddFallbackDragPreview()
        {
            var counters = Hoi4UnitCounters.Instance;
            if (!FallbackActive || !dragging || counters == null || ArmySelection(out var army) == null) return;
            var color = Hoi4Armies.ColorOf(army.Id, 0.95f);
            if (dragTrail.Count >= 2)
            {
                var trail = Relax(new List<Vector2>(dragTrail), 0.25f, 4);
                AddRibbon(ToWorld(trail), Color.Lerp(color, Color.white, 0.2f), 0f, dragTrailWidthPx);
            }
            var spine = FallbackSpine(dragPicked, counters);
            if (spine == null) return;
            AddFallbackRibbon(ToWorld(spine), color, true);
            if (dragPicked.Count < 2) return; // one province: no ends yet
            AddRing(spine[0]);
            AddRing(spine[spine.Count - 1]);
        }
    }
}
