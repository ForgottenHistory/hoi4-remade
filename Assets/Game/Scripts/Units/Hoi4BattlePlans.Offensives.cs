using System;
using System.Collections.Generic;
using Hoi4.Map;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hoi4.Units
{
    /// <summary>An offensive plan: an army's arrow from its front line to the provinces to take, with the
    /// divisions assigned to it. Assigned divisions keep holding their front line until the plan runs.</summary>
    public sealed class Hoi4Offensive
    {
        public int Id;
        public ushort Owner;
        public int ArmyId;
        public readonly List<ushort> Targets = new List<ushort>(); // the provinces to take, in the order they were dragged over
        public ushort Target;                       // the middle one: the arrow and the path go there
        public readonly HashSet<ushort> Units = new HashSet<ushort>();
        // Simulation: the army's front provinces this offensive starts from, the fronts facing the piece of the
        // enemy's land its target is in (not a pocket elsewhere). Refreshed when the borders move.
        public readonly HashSet<ushort> FrontSet = new HashSet<ushort>();
        // Presentation (rebuilt with the mesh). Path: HOI4 keeps an offensive line as province points, from an own
        // province on the front through the enemy's land to the target (the last); the arrow starts where it
        // crosses the front (HOI4 draws one bend, not the route). Its tie-breaks use the drawn province centres
        // (floats from a GPU readback), so the simulation must never read it: divisions advance by land hops
        // to the targets (Advance), and the fronts it starts from are FrontSet, from adjacency only.
        public List<ushort> Path;
        public List<Vector3> Objective;             // the line around the targets, world
        public Vector3 LabelWorld, LabelAhead;
        public List<Vector3> LabelPath;             // the objective line around the label (the text bends along it)
        public int LabelAnchor;
    }

    /// <summary>
    /// Offensive battle plans, HOI4's way: with a division of an army that has a front line selected, the
    /// offence button turns the tool on and stripes the enemy's provinces (the countries the army's front
    /// lines face) yellow. Pressing the right button on one of them and dragging picks the provinces the cursor
    /// crosses (green), with HOI4's dashed cursor trail (magnet_line) and dotted line through the picked
    /// provinces (virtual_line); releasing places the plan: an arrow from the front line into the middle of the
    /// targets and an objective line along their far side, in the army's colour. A click without dragging picks
    /// one province. The selected divisions of that army are assigned to it. They hold the front line; when the
    /// army's play button runs its plans they attack from it towards the targets (Advance).
    /// Look: Tools/prototypes/battleplan/offensive.py and drag.py, against HOI4 screenshots (arrowType
    /// move_thick for the arrow, offence_line for the objective line).
    /// </summary>
    public sealed partial class Hoi4BattlePlans
    {
        [Header("Offensive plans (map pixels)")]
        [Tooltip("Arrow body ribbon width at the root (the visible body is 0.75 of it; the head is ribbon / body_uv_scale wide)")] public float offensiveRibbonPx = 7.5f;
        [Tooltip("Ribbon width at the head / at the root")] public float offensiveTaper = 0.9f;
        [Tooltip("Arrow head length")] public float offensiveHeadLengthPx = 7f;
        [Tooltip("The arrow starts this far behind the front line")] public float offensiveRootBackPx = 4f;
        [Tooltip("HOI4's arrow bends once: it leaves the front square to it and turns to the target. The curve's control point is this far out along the front's normal (of the root-tip distance)")] public float offensiveBend = 0.5f;
        [Tooltip("The arrow body fades in (smoothstep) over this distance from its root")] public float offensiveFadePx = 7f;
        [Tooltip("Drop shadow blur radius")] public float offensiveShadowBlurPx = 1f;
        [Tooltip("Drop shadow offset (x right, y down)")] public Vector2 offensiveShadowPx = new Vector2(-1.8f, 0.4f);
        [Tooltip("Objective line width")] public float objectiveWidthPx = 2f;
        [Tooltip("Objective line: the target's outline pulled towards its centre by this factor")] public float objectiveScale = 0.75f;
        [Tooltip("The offensive tool's stripe colour")] public Color offensiveStripeColor = new Color(0.90f, 0.88f, 0.32f, 1f);
        [Tooltip("The province under the cursor while placing, and the provinces picked by a drag")] public Color offensiveHoverColor = new Color(0.36f, 0.92f, 0.32f, 1f);
        [Tooltip("Dragging: the dashed cursor trail's width (HOI4 magnet_line)")] public float dragTrailWidthPx = 0.55f;
        [Tooltip("Dragging: the dotted line through the picked provinces (HOI4 virtual_line)")] public float dragDotWidthPx = 1f;
        [Tooltip("Dragging: the ring at each end of the dotted line")] public float dragRingRadiusPx = 1.6f;
        public Color dragDotColor = new Color(0.85f, 0.8f, 0.7f, 1f);
        public Color dragRingColor = new Color(0.9f, 0.9f, 0.85f, 0.55f);

        // HOI4 arrowType move_thick: the body ribbon maps the middle body_uv_scale of the texture height (its band,
        // rows 0.35..0.65, is then 0.75 of the ribbon); the head mesh is ribbon / body_uv_scale wide and maps the
        // full height, so its barbs come out ~2.3x the visible body
        const float ArrowBodyUv = 0.4f;
        const float ArrowTexAspect = 2f;        // move_mask is 256 x 128
        const float ArrowTile = 0.45f;          // a body tile spans this much of the texture width

        readonly List<Hoi4Offensive> offensives = new List<Hoi4Offensive>();
        readonly Dictionary<ushort, Hoi4Offensive> offensiveOf = new Dictionary<ushort, Hoi4Offensive>();
        readonly HashSet<ushort> offensiveTargets = new HashSet<ushort>(); // the tool's valid provinces
        // Dragging (presentation): the cursor trail in map px and the provinces it crossed, in order
        bool dragging;
        readonly List<Vector2> dragTrail = new List<Vector2>();
        readonly List<ushort> dragPicked = new List<ushort>();
        int nextOffensiveId = 1;

        Material arrowMaterial;
        Mesh arrowMesh;
        readonly List<Vector3> aVerts = new List<Vector3>();
        readonly List<Color> aColors = new List<Color>();
        readonly List<Vector2> aUvs = new List<Vector2>();
        readonly List<Vector2> aInfos = new List<Vector2>();
        readonly List<Vector2> aKinds = new List<Vector2>();
        readonly List<Vector2> aCenters = new List<Vector2>();
        readonly List<Vector2> aBlurs = new List<Vector2>();   // texture uv per map px (u, v): the shadow blur's tap spacing
        readonly List<int> aTris = new List<int>();

        /// <summary>True while the offensive tool is on.</summary>
        public bool OffensiveActive { get; private set; }
        public IReadOnlyList<Hoi4Offensive> Offensives => offensives;
        public Hoi4Offensive OffensiveOf(ushort unitId) => offensiveOf.TryGetValue(unitId, out var o) ? o : null;

        public Hoi4Offensive GetOffensive(int id)
        {
            foreach (var o in offensives) if (o.Id == id) return o;
            return null;
        }

        // --- Set-up ----------------------------------------------------------------------------------

        void SetUpOffensiveDrawing(Hoi4UnitCounters counters)
        {
            var omask = LoadTexture("gfx/maparrows/offence_mask.png");
            var opattern = LoadTexture("gfx/maparrows/offence_pattern.png");
            if (omask != null && opattern != null)
            {
                material.SetTexture("_OffenceMaskTex", omask);
                material.SetTexture("_OffencePatternTex", opattern);
            }
            var dmask = LoadTexture("gfx/maparrows/dot_mask.png");
            var dpattern = LoadTexture("gfx/maparrows/dot_pattern.png");
            if (dmask != null && dpattern != null)
            {
                material.SetTexture("_DotMaskTex", dmask);
                material.SetTexture("_DotPatternTex", dpattern);
            }
            else ArchonLogger.LogWarning("Hoi4BattlePlans: gfx/maparrows/offence_mask.png missing (rerun Tools/hoi4_convert.py), objective lines drawn plain", "game_units");

            var shader = Shader.Find("Hoi4/PlanArrow");
            var amask = LoadTexture("gfx/maparrows/move_mask.png");
            var apattern = LoadTexture("gfx/maparrows/move_pattern.png");
            if (shader == null || amask == null || apattern == null)
            {
                ArchonLogger.LogWarning("Hoi4BattlePlans: Hoi4/PlanArrow shader or gfx/maparrows/move_mask.png missing, no offensive arrows", "game_units");
                return;
            }
            amask.wrapMode = apattern.wrapMode = TextureWrapMode.Clamp;
            arrowMaterial = new Material(shader) { name = "Hoi4PlanArrow" };
            arrowMaterial.SetTexture("_MaskTex", amask);
            arrowMaterial.SetTexture("_PatternTex", apattern);
            arrowMaterial.SetVector(BoundsId, bounds);
            if (counters.Heightmap != null) arrowMaterial.SetTexture(HeightmapId, counters.Heightmap);
            arrowMaterial.SetFloat(HeightScaleId, counters.HeightScale);
            arrowMaterial.SetVector("_ArrowParams", new Vector4(ArrowBodyUv, 0.8f, 1f, 0.07f));
            arrowMaterial.SetFloat("_ShadowBlur", offensiveShadowBlurPx);
            arrowMesh = new Mesh { name = "Hoi4OffensiveArrows", indexFormat = IndexFormat.UInt32 };
        }

        void DestroyOffensiveDrawing()
        {
            if (arrowMaterial != null) Destroy(arrowMaterial);
            if (arrowMesh != null) Destroy(arrowMesh);
        }

        // --- Tool ------------------------------------------------------------------------------------

        /// <summary>The selected army has a front line: the offensive tool can be used.</summary>
        public bool CanPlanOffensive
        {
            get
            {
                if (!ready || ArmySelection(out var army) == null) return false;
                foreach (var p in plans) if (!p.IsAI && p.ArmyId == army.Id) return true;
                return false;
            }
        }

        public void BeginOffensive()
        {
            if (!CanPlanOffensive) return;
            if (Active) Cancel();
            OffensiveActive = true;
            hoverProvince = 0;
            EndDrag();
            RefreshOffensiveTargets();
            Changed?.Invoke();
        }

        public void CancelOffensive()
        {
            if (!OffensiveActive) return;
            OffensiveActive = false;
            offensiveTargets.Clear();
            EndDrag();
            dirtyTex = true;
            RebuildTexture();
            Changed?.Invoke();
        }

        /// <summary>The provinces an offensive of the selected army can aim at: land held by the countries its
        /// front lines face.</summary>
        void RefreshOffensiveTargets()
        {
            offensiveTargets.Clear();
            if (ArmySelection(out var army) != null)
                foreach (ushort p in landProvinces)
                    if (IsOffensiveTarget(Player, army.Id, p)) offensiveTargets.Add(p);
            dirtyTex = true;
            RebuildTexture();
        }

        /// <summary>Land (not impassable) held by a country one of the army's front lines faces.</summary>
        public bool IsOffensiveTarget(ushort owner, int armyId, ushort province)
        {
            if (province == 0 || Hoi4Regions.IsWater(province) || Hoi4Regions.IsImpassable(province)) return false;
            ushort holder = Hoi4Occupation.ControllerOf(gameState, province);
            if (holder == 0 || holder == owner) return false;
            foreach (var p in plans)
                if (!p.IsAI && p.Owner == owner && p.ArmyId == armyId && p.Country == holder) return true;
            return false;
        }

        void UpdateOffensiveTool()
        {
            if (Input.GetKeyDown(KeyCode.Escape) || !CanPlanOffensive) { CancelOffensive(); return; }
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
                if (!Input.GetMouseButton(1)) { FinishDrag(); return; }
                if (cam != null && TryCursorPixel(cam, out var px)) ExtendDrag(px);
            }
            RebuildTexture();
        }

        /// <summary>The cursor on the map, in map px (y down). The engine's heightfield-corrected hit, the point
        /// province picking uses: a ray against the flat plane lands south of the cursor on raised terrain.</summary>
        bool TryCursorPixel(Camera cam, out Vector2 px)
        {
            px = default;
            var selector = Engine.ArchonEngine.Instance.ProvinceSelector;
            if (selector == null || !selector.TryGetMapPointAtScreenPosition(Input.mousePosition, cam, out _, out var uv)) return false;
            var tm = Engine.ArchonEngine.Instance.TextureManager;
            px = new Vector2(uv.x * tm.MapWidth, (1f - uv.y) * tm.MapHeight);
            return true;
        }

        /// <summary>Right press on a valid province starts a drag (released, the plan is placed). Left click cancels.</summary>
        bool OnOffensiveClick(int button)
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
            if (button == 0) CancelOffensive();
            return true;
        }

        /// <summary>The trail reaches the cursor; every valid province on the way (sampled every 0.25 map px, so a
        /// fast drag skips none) is picked in order.</summary>
        void ExtendDrag(Vector2 px)
        {
            if (dragTrail.Count > 0 && (px - dragTrail[dragTrail.Count - 1]).sqrMagnitude < 0.0625f) return;
            var from = dragTrail.Count > 0 ? dragTrail[dragTrail.Count - 1] : px;
            dragTrail.Add(px);
            var tm = Engine.ArchonEngine.Instance.TextureManager;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, px) / 0.25f));
            for (int s = 1; s <= steps; s++)
            {
                var q = Vector2.Lerp(from, px, s / (float)steps);
                int x = Mathf.FloorToInt(q.x), y = Mathf.FloorToInt(q.y);
                if (x < 0 || y < 0 || x >= tm.MapWidth || y >= tm.MapHeight) continue;
                ushort id = tm.GetProvinceID(x, tm.MapHeight - 1 - y); // the lookup takes y from the bottom
                if (id != 0 && offensiveTargets.Contains(id) && !dragPicked.Contains(id))
                {
                    dragPicked.Add(id);
                    dirtyTex = true;
                }
            }
            dirtyMesh = true;
        }

        void FinishDrag()
        {
            var targets = new List<ushort>(dragPicked);
            var selected = ArmySelection(out var army);
            CancelOffensive();
            if (selected == null || targets.Count == 0) return;
            selected.Sort();
            gameState.TryExecuteCommand(new PlaceOffensiveCommand { CountryId = Player, ArmyId = army.Id, Targets = targets, UnitIds = selected }, out _);
        }

        void EndDrag()
        {
            dragging = false;
            dragTrail.Clear();
            dragPicked.Clear();
            dirtyMesh = true;
        }

        void PaintOffensiveTargets()
        {
            var yellow = (Color32)offensiveStripeColor;
            var green = (Color32)offensiveHoverColor;
            foreach (ushort p in offensiveTargets)
                if (p < planPixels.Length) planPixels[p] = new Color32(yellow.r, yellow.g, yellow.b, 64);
            foreach (ushort p in dragPicked)
                if (p < planPixels.Length) planPixels[p] = new Color32(green.r, green.g, green.b, 96);
            if (!dragging && hoverProvince != 0 && offensiveTargets.Contains(hoverProvince) && hoverProvince < planPixels.Length)
                planPixels[hoverProvince] = new Color32(green.r, green.g, green.b, 96);
        }

        // --- Planning bonus ---------------------------------------------------------------------------

        static readonly Core.Data.FixedPoint64 PlanningGain = Core.Data.FixedPoint64.FromFraction(2, 100);  // HOI4 PLANNING_GAIN, per day
        static readonly Core.Data.FixedPoint64 PlanningDecay = Core.Data.FixedPoint64.FromFraction(1, 100); // HOI4 PLANNING_DECAY, per day
        static readonly Core.Data.FixedPoint64 ManualOrderDecay = Core.Data.FixedPoint64.FromFraction(3, 100); // HOI4 PLAYER_ORDER_PLANNING_DECAY, per day
        static readonly Core.Data.FixedPoint64 PlanningMax = Core.Data.FixedPoint64.FromFraction(3, 10);    // HOI4 PLANNING_MAX (techs can raise it)
        public static Core.Data.FixedPoint64 MaxPlanning => PlanningMax;

        /// <summary>
        /// Daily (HOI4 wiki "Battle plan", Planning bonus): a division at the starting position of an offensive
        /// battle plan, i.e. on a front province of its army's front line when the army has an offensive line,
        /// neither moving nor fighting, gains PLANNING_GAIN up to PLANNING_MAX. Otherwise it loses PLANNING_DECAY,
        /// or PLAYER_ORDER_PLANNING_DECAY while following a manual order. The bonus raises attack and
        /// breakthrough by the same percentage (Hoi4Combat).
        /// </summary>
        void UpdatePlanning()
        {
            var divisions = Hoi4Divisions.Instance;
            if (divisions == null) return;
            var units = gameState.Units;
            var queue = units.MovementQueue;
            var ids = new List<ushort>(divisions.All.Keys);
            ids.Sort();
            foreach (ushort u in ids)
            {
                var d = divisions.Get(u);
                if (d == null || !units.HasUnit(u)) continue;
                bool planning = !queue.IsUnitMoving(u) && !Hoi4Combat.IsFighting(u)
                    && planOf.TryGetValue(u, out var plan) && !plan.IsAI && plan.FrontProvinces != null
                    && ArmyHasOffensive(plan.ArmyId)
                    && plan.FrontProvinces.Contains(units.GetUnit(u).provinceID);
                d.Planning = planning
                    ? Core.Data.FixedPoint64.Min(PlanningMax, d.Planning + PlanningGain)
                    : Core.Data.FixedPoint64.Max(Core.Data.FixedPoint64.Zero, d.Planning - (manualOrders.ContainsKey(u) ? ManualOrderDecay : PlanningDecay));
            }
        }

        bool ArmyHasOffensive(int armyId)
        {
            foreach (var o in offensives) if (o.ArmyId == armyId) return true;
            return false;
        }

        // --- Executing (HOI4 COrderExecuteCommand: the army's play / stop buttons) ---------------------

        readonly SortedSet<int> executing = new SortedSet<int>(); // army ids whose plans run

        /// <summary>The army's plans run (its play button is on).</summary>
        public bool IsExecuting(int armyId) => executing.Contains(armyId);

        /// <summary>The army has a front line: its play / stop buttons show.</summary>
        public bool HasFrontLine(int armyId)
        {
            foreach (var p in plans) if (!p.IsAI && p.ArmyId == armyId) return true;
            return false;
        }

        /// <summary>ExecuteBattlePlanCommand.</summary>
        public void SetExecuting(ushort owner, int armyId, bool on)
        {
            if (on ? !executing.Add(armyId) : !executing.Remove(armyId)) return;
            Core.Recording.SessionRecorder.Note("plans", on ? "execute" : "stop", owner, 0, 0, 0, $"army {armyId}");
            DirtyArmy(armyId); // spread again: on stop, attacks under way are called back to the line
            Changed?.Invoke();
        }

        /// <summary>Hourly: an army without a front line has nothing to run, and one whose offensives are all done
        /// (every target taken) stops, like HOI4: the play button has to be pressed again for the next phase.</summary>
        void PruneExecuting()
        {
            var stop = new List<int>();
            foreach (int a in executing) if (!HasFrontLine(a) || !ArmyHasOffensive(a)) stop.Add(a);
            if (stop.Count == 0) return;
            foreach (int a in stop)
            {
                executing.Remove(a);
                Core.Recording.SessionRecorder.Note("plans", "stop", 0, 0, 0, 0, $"army {a}: {(HasFrontLine(a) ? "offensives done" : "no front line")}");
                DirtyArmy(a);
            }
            Changed?.Invoke();
        }

        /// <summary>The army's front line plans spread their divisions again on the next hour.</summary>
        void DirtyArmy(int armyId)
        {
            foreach (var p in plans) if (!p.IsAI && p.ArmyId == armyId) dirtyPlans.Add(p);
        }

        // --- Advancing (the plan runs) ----------------------------------------------------------------

        static readonly Core.Data.FixedPoint64 AttackOrganisation = Core.Data.FixedPoint64.Half;                 // of max: rests before attacking below this
        static readonly Core.Data.FixedPoint64 AttackStrength = Core.Data.FixedPoint64.FromFraction(35, 100);     // of max: too battered to attack
        static readonly Core.Data.FixedPoint64 BreakOffOrganisation = Core.Data.FixedPoint64.FromFraction(3, 10); // HOI4 LOW_ORG_FOR_ATTACK: breaks off below

        // How far an offensive may bulge sideways from the shortest way between the front and a target, in hops
        const int ZoneSlack = 2;

        /// <summary>A division on an offensive of an army whose plans run, following its plan (not a manual order).
        /// It keeps its slot on the front line and attacks from there (Advance).</summary>
        public bool IsAdvancing(ushort unitId) =>
            offensiveOf.TryGetValue(unitId, out var o) && executing.Contains(o.ArmyId)
            && planOf.TryGetValue(unitId, out var plan) && !plan.IsAI && !manualOrders.ContainsKey(unitId);

        /// <summary>An advancing division marching into enemy land: an attack the line must not call back (Hold).</summary>
        bool IsPlanAttack(ushort unitId, Core.Units.UnitState unit)
        {
            if (!IsAdvancing(unitId)) return false;
            ushort h = HeadingTo(unitId);
            return h != 0 && EnemyLand(unit.countryID).Contains(h);
        }

        /// <summary>
        /// Hourly, after the lines are held: a broad advance, HOI4's offensive line (not its spearhead). The
        /// offensive's divisions keep their slots on the front line, which stays manned; each idle one attacks
        /// across the line into a neighbouring enemy province of the offensive's zone (OffensiveZone: the land
        /// between the front and the targets). Of those it takes the one the fewest of the offensive's divisions
        /// already attack, so the push spreads over the zone's whole width; ties go to the one nearer the targets,
        /// then the lower id. Hoi4Combat holds it at the border until the defenders break or leave; an empty
        /// province is simply taken (Hoi4Occupation). Each capture moves the border, the front line is traced
        /// again along it and the divisions spread over the new line (RefreshPlans), so the front advances as a
        /// line towards the targets. A division attacks only with half its organisation and 35% strength (it holds
        /// until then) and breaks off below 30% organisation. When the last target falls the offensive ends and
        /// the army stops (PruneExecuting). Simulation, not commands: every client runs it the same way.
        /// </summary>
        void Advance()
        {
            if (executing.Count == 0 || offensives.Count == 0) return;
            var units = gameState.Units;
            var queue = units.MovementQueue;
            var divisions = Hoi4Divisions.Instance;
            bool ordered = false;
            foreach (var o in offensives)
            {
                if (!executing.Contains(o.ArmyId) || o.Units.Count == 0) continue;
                var enemy = EnemyLand(o.Owner);
                var zone = OffensiveZone(o, out var toTarget);
                if (zone.Count == 0) continue;
                var ids = new List<ushort>(o.Units);
                ids.Sort();
                var load = new Dictionary<ushort, int>();
                foreach (ushort u in ids)
                {
                    ushort h = HeadingTo(u);
                    if (h != 0 && zone.Contains(h)) load[h] = load.TryGetValue(h, out int n) ? n + 1 : 1;
                }
                foreach (ushort u in ids)
                {
                    if (!units.HasUnit(u) || !IsAdvancing(u) || Hoi4Combat.IsRetreating(u)) continue;
                    var d = divisions?.Get(u);
                    var unit = units.GetUnit(u);
                    if (queue.IsUnitMoving(u))
                    {
                        // Worn down in an attack: break it off; it holds its province and rests
                        if (d != null && Hoi4Combat.IsFighting(u) && d.Organisation < d.MaxOrganisation * BreakOffOrganisation
                            && enemy.Contains(HeadingTo(u)))
                        {
                            queue.CancelMovement(u);
                            Core.Recording.SessionRecorder.Note("plans", "break_off", o.Owner, u, unit.provinceID, 0,
                                $"offensive {o.Id}: org {d.Organisation.ToFloat():0.#}/{d.MaxOrganisation.ToFloat():0.#}");
                            ordered = true;
                        }
                        continue;
                    }
                    if (d != null && (d.Organisation < d.MaxOrganisation * AttackOrganisation || d.Strength < d.MaxStrength * AttackStrength)) continue;
                    if (!o.FrontSet.Contains(unit.provinceID)) continue; // on another front (a pocket): it holds there
                    ushort best = 0;
                    int bestLoad = int.MaxValue, bestDist = int.MaxValue;
                    foreach (ushort q in Neighbours(unit.provinceID))
                    {
                        if (!zone.Contains(q) || !enemy.Contains(q)) continue;
                        int l = load.TryGetValue(q, out int n) ? n : 0;
                        int dq = toTarget.TryGetValue(q, out int t) ? t : int.MaxValue;
                        if (l < bestLoad || (l == bestLoad && (dq < bestDist || (dq == bestDist && q < best))))
                        {
                            best = q; bestLoad = l; bestDist = dq;
                        }
                    }
                    if (best == 0 || !StepTo(u, unit, best)) continue;
                    load[best] = bestLoad + 1;
                    ordered = true;
                    Core.Recording.SessionRecorder.Note("plans", "advance_attack", o.Owner, u, unit.provinceID, best,
                        $"offensive {o.Id}: {bestDist} hops from a target, {bestLoad} already attacking it, zone {zone.Count}");
                }
            }
            if (ordered) Hoi4UnitCounters.Instance?.MarkDirty();
        }

        /// <summary>
        /// The provinces an offensive pushes through: land of the targets' holder that lies on or near a shortest
        /// way from the army's front line to one of the targets. A province q is in when, for some target t,
        /// hops(front, q) + hops(q, t) is at most the shortest front-to-t distance plus ZoneSlack (an ellipse
        /// between the line and the target in hops). Hops run through the holder's land only, from adjacency
        /// (deterministic). toTarget: hops from each zone province to its nearest target.
        /// </summary>
        HashSet<ushort> OffensiveZone(Hoi4Offensive o, out Dictionary<ushort, int> toTarget)
        {
            var zone = new HashSet<ushort>();
            toTarget = new Dictionary<ushort, int>();
            ushort holder = Ctrl(o.Target);
            if (holder == 0 || holder == o.Owner) return zone;
            bool Walk(ushort p) => Ctrl(p) == holder && !Hoi4Regions.IsImpassable(p);

            // Hops from the front: 1 for the holder's provinces across the army's lines against it
            var fromFront = new Dictionary<ushort, int>();
            var queue = new List<ushort>();
            var starts = new List<ushort>(o.FrontSet);
            starts.Sort();
            foreach (ushort fp in starts)
                foreach (ushort q in Neighbours(fp))
                    if (Walk(q) && fromFront.TryAdd(q, 1)) queue.Add(q);
            queue.Sort();
            Bfs(fromFront, queue, Walk);
            if (fromFront.Count == 0) return zone;

            var targets = new List<ushort>(o.Targets);
            targets.Sort();
            foreach (ushort t in targets)
            {
                if (!fromFront.TryGetValue(t, out int shortest)) continue; // not reachable from the line
                var fromTarget = new Dictionary<ushort, int> { [t] = 0 };
                Bfs(fromTarget, new List<ushort> { t }, Walk);
                foreach (var kv in fromTarget)
                {
                    if (!fromFront.TryGetValue(kv.Key, out int f) || f + kv.Value > shortest + ZoneSlack) continue;
                    zone.Add(kv.Key);
                    if (!toTarget.TryGetValue(kv.Key, out int was) || kv.Value < was) toTarget[kv.Key] = kv.Value;
                }
            }
            return zone;
        }

        /// <summary>Breadth-first hop counts from the seeded provinces through walkable land (seeds already in dist).</summary>
        void Bfs(Dictionary<ushort, int> dist, List<ushort> queue, Func<ushort, bool> walkable)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                ushort p = queue[i];
                int d = dist[p];
                foreach (ushort q in Neighbours(p))
                    if (!dist.ContainsKey(q) && walkable(q)) { dist[q] = d + 1; queue.Add(q); }
            }
        }

        /// <summary>One march to a neighbouring province (simulation, not a command; see OrderMove).</summary>
        bool StepTo(ushort unitId, Core.Units.UnitState unit, ushort to)
        {
            var type = StarterKit.Initializer.Instance?.UnitSystem?.GetUnitType(unit.unitTypeID);
            var queue = gameState.Units.MovementQueue;
            if (queue.IsUnitMoving(unitId)) return false;
            queue.StartMovement(unitId, to, type?.Speed ?? 3, new List<ushort> { unit.provinceID, to });
            return true;
        }

        // --- Plans (commands) -----------------------------------------------------------------------

        /// <summary>All the targets are valid for the army, none twice.</summary>
        public bool AreOffensiveTargets(ushort owner, int armyId, List<ushort> targets)
        {
            if (targets == null || targets.Count == 0) return false;
            var seen = new HashSet<ushort>();
            foreach (ushort p in targets)
                if (!seen.Add(p) || !IsOffensiveTarget(owner, armyId, p)) return false;
            return true;
        }

        /// <summary>PlaceOffensiveCommand: the army's offensive on the targets, with these divisions (taken off
        /// any other offensive; they keep holding their front line).</summary>
        public Hoi4Offensive CreateOffensive(ushort owner, int armyId, List<ushort> targets, List<ushort> unitIds)
        {
            if (!simReady || !AreOffensiveTargets(owner, armyId, targets)) return null;
            ushort target = targets[targets.Count / 2];
            var fronts = OffensiveFronts(owner, armyId, target);
            if (fronts.Count == 0) return null;
            var o = new Hoi4Offensive { Id = nextOffensiveId++, Owner = owner, ArmyId = armyId, Target = target };
            o.Targets.AddRange(targets);
            o.FrontSet.UnionWith(fronts);
            var armies = Hoi4Armies.Instance;
            foreach (ushort u in unitIds)
            {
                if (armies?.ArmyOf(u)?.Id != armyId) continue;
                if (offensiveOf.TryGetValue(u, out var old)) old.Units.Remove(u);
                o.Units.Add(u);
                offensiveOf[u] = o;
            }
            offensives.Add(o);
            offensives.RemoveAll(x => x != o && x.Units.Count == 0);
            DirtyArmy(armyId); // spread again (a division taken off another offensive may be mid-attack)
            Core.Recording.SessionRecorder.Note("plans", "offensive", owner, 0, 0, target, $"offensive {o.Id} army {armyId}: {o.Units.Count} divisions, targets {string.Join(",", targets)}");
            dirtyMesh = true;
            Changed?.Invoke();
            return o;
        }

        /// <summary>DeleteOffensiveCommand.</summary>
        public void DeleteOffensive(int id)
        {
            var o = GetOffensive(id);
            if (o == null) return;
            RemoveOffensive(o, "deleted");
            dirtyMesh = true;
            Changed?.Invoke();
        }

        void RemoveOffensive(Hoi4Offensive o, string why)
        {
            DirtyArmy(o.ArmyId); // its attacks under way are called back to the line
            offensives.Remove(o);
            foreach (ushort u in o.Units) if (offensiveOf.TryGetValue(u, out var x) && x == o) offensiveOf.Remove(u);
            o.Units.Clear();
            Core.Recording.SessionRecorder.Note("plans", "offensive_removed", o.Owner, 0, 0, o.Target, $"offensive {o.Id}: {why}");
        }

        /// <summary>Hourly: divisions that died or left the army drop off, targets taken (or no longer valid) drop
        /// off; an offensive goes when no target is left, nobody is assigned, or (checked when the borders moved)
        /// its target can no longer be reached from the army's front line.</summary>
        void PruneOffensives(bool bordersMoved)
        {
            if (offensives.Count == 0) return;
            var units = gameState.Units;
            var armies = Hoi4Armies.Instance;
            bool any = false;
            foreach (var o in new List<Hoi4Offensive>(offensives))
            {
                int dropped = o.Units.RemoveWhere(u =>
                {
                    if (units.HasUnit(u) && armies?.ArmyOf(u)?.Id == o.ArmyId) return false;
                    offensiveOf.Remove(u);
                    return true;
                });
                any |= dropped > 0;
                string why = null;
                int lost = o.Targets.RemoveAll(p => !IsOffensiveTarget(o.Owner, o.ArmyId, p));
                if (lost > 0)
                {
                    any = true;
                    if (o.Targets.Count > 0)
                    {
                        ushort middle = o.Targets[o.Targets.Count / 2];
                        if (middle != o.Target) { o.Target = middle; o.Path = null; }
                    }
                }
                if (o.Targets.Count == 0) why = "targets taken (or no front line against their holder)";
                else if (o.Units.Count == 0) why = "no divisions";
                else if (bordersMoved || lost > 0)
                {
                    var fronts = OffensiveFronts(o.Owner, o.ArmyId, o.Target);
                    if (fronts.Count == 0) why = "no route to the target";
                    else if (!o.FrontSet.SetEquals(fronts)) { o.FrontSet.Clear(); o.FrontSet.UnionWith(fronts); any = true; }
                }
                if (why == null) continue;
                RemoveOffensive(o, why);
                any = true;
            }
            if (!any) return;
            dirtyMesh = true;
            Changed?.Invoke();
        }

        /// <summary>
        /// Simulation: the army's front provinces an offensive on the target starts from, sorted: every front of
        /// its lines against the target's holder that faces the piece of the holder's land the target is in
        /// (FrontsAgainst splits fronts by enemy piece). Empty: no route to the target. Adjacency only, the
        /// same on every machine.
        /// </summary>
        List<ushort> OffensiveFronts(ushort owner, int armyId, ushort target)
        {
            var result = new List<ushort>();
            ushort holder = Ctrl(target);
            if (holder == 0 || holder == owner) return result;
            foreach (var plan in plans)
            {
                if (plan.IsAI || plan.Owner != owner || plan.ArmyId != armyId || plan.Country != holder || plan.Fronts == null) continue;
                var sizes = new List<int>();
                var piece = EnemyPieces(plan.FrontProvinces, holder, sizes);
                if (!piece.TryGetValue(target, out int tp)) continue; // the target's piece borders none of this line
                foreach (var front in plan.Fronts)
                    if (front.Count > 0 && PieceFaced(front[0], piece, sizes) == tp) result.AddRange(front);
            }
            result.Sort();
            return result;
        }

        /// <summary>Presentation: the offensive's divisions slotted on its fronts (its label's count).</summary>
        public int OffensiveDivisions(Hoi4Offensive o)
        {
            int n = 0;
            foreach (ushort u in o.Units)
                if (planOf.TryGetValue(u, out var plan) && plan.Slot.TryGetValue(u, out ushort slot) && o.FrontSet.Contains(slot)) n++;
            return n;
        }

        /// <summary>Presentation: the drawn path still starts in an own province and runs through the target's holder's land.</summary>
        bool PathHolds(Hoi4Offensive o)
        {
            if (o.Path == null || o.Path.Count < 2 || Ctrl(o.Path[0]) != o.Owner) return false;
            ushort holder = Ctrl(o.Target);
            for (int i = 1; i < o.Path.Count; i++)
                if (Ctrl(o.Path[i]) != holder) return false;
            return true;
        }

        /// <summary>
        /// Presentation: the offensive's province path for the arrow (prototype: offensive.py offensive_path). Breadth-first from the target
        /// through the land of its holder; the start is one of the army's front provinces against that country
        /// with the fewest steps (ties: nearest the target); each step goes to a neighbour one step closer, the one
        /// nearest the straight line from the start to the target. Steps and province ids decide; province
        /// centres (integer map px) only break ties.
        /// </summary>
        List<ushort> OffensivePath(Hoi4Offensive o)
        {
            ushort target = o.Target;
            ushort holder = Ctrl(target);
            if (holder == 0 || holder == o.Owner) return null;
            var dist = new Dictionary<ushort, int> { [target] = 0 };
            var queue = new List<ushort> { target };
            for (int qi = 0; qi < queue.Count; qi++)
            {
                ushort p = queue[qi];
                foreach (ushort q in Neighbours(p))
                    if (!dist.ContainsKey(q) && Ctrl(q) == holder) { dist[q] = dist[p] + 1; queue.Add(q); }
            }
            var goal = ProvinceCentre(target);
            ushort start = 0;
            int bestSteps = int.MaxValue;
            long bestD = long.MaxValue;
            var starts = new List<ushort>(o.FrontSet);
            starts.Sort();
            foreach (ushort fp in starts)
            {
                int steps = int.MaxValue;
                foreach (ushort q in Neighbours(fp))
                    if (dist.TryGetValue(q, out int d)) steps = Mathf.Min(steps, d + 1);
                if (steps == int.MaxValue) continue;
                long d2 = SqDist(ProvinceCentre(fp), goal);
                if (steps < bestSteps || (steps == bestSteps && (d2 < bestD || (d2 == bestD && fp < start))))
                {
                    bestSteps = steps; bestD = d2; start = fp;
                }
            }
            if (start == 0) return null;
            var path = new List<ushort> { start };
            var a = ProvinceCentre(start);
            long vx = goal.x - a.x, vy = goal.y - a.y;
            int here = bestSteps;
            while (path[path.Count - 1] != target && here > 0)
            {
                ushort best = 0;
                long bestOff = long.MaxValue;
                foreach (ushort q in Neighbours(path[path.Count - 1]))
                {
                    if (!dist.TryGetValue(q, out int d) || d != here - 1) continue;
                    var c = ProvinceCentre(q);
                    long off = Math.Abs(vx * (c.y - a.y) - vy * (c.x - a.x));
                    if (off < bestOff || (off == bestOff && q < best)) { bestOff = off; best = q; }
                }
                if (best == 0) return null;
                path.Add(best);
                here--;
            }
            return path[path.Count - 1] == target ? path : null;
        }

        /// <summary>A province's centre in integer map px (0,0 before the map is drawn: ties then go by id).</summary>
        Vector2Int ProvinceCentre(ushort province)
        {
            var counters = Hoi4UnitCounters.Instance;
            if (worldPerPxX <= 0f || counters == null || !counters.TryGetProvincePoint(province, out var w)) return Vector2Int.zero;
            return Vector2Int.RoundToInt(WorldToPixel(w));
        }

        static long SqDist(Vector2Int a, Vector2Int b)
        {
            long dx = a.x - b.x, dy = a.y - b.y;
            return dx * dx + dy * dy;
        }

        // --- Drawing ---------------------------------------------------------------------------------

        Vector2 WorldToPixel(Vector3 w) =>
            new Vector2((w.x - bounds.x) / worldPerPxX, mapHeightPx - (w.z - bounds.y) / worldPerPxZ);

        /// <summary>Objective lines (in the plan line mesh) and arrows (their own mesh).</summary>
        void RebuildOffensives()
        {
            aVerts.Clear(); aColors.Clear(); aUvs.Clear(); aInfos.Clear(); aKinds.Clear(); aCenters.Clear(); aBlurs.Clear(); aTris.Clear();
            var counters = Hoi4UnitCounters.Instance;
            AddDragPreview(counters);
            foreach (var o in offensives)
            {
                if (!PathHolds(o) || !o.FrontSet.Contains(o.Path[0])) o.Path = OffensivePath(o); // new, or the front moved
                if (counters == null || !counters.TryGetProvincePoint(o.Target, out var target)) continue;
                target.y = 0f;
                // Several targets: the arrow goes into the middle of the line through their centres
                var spine = o.Targets.Count > 1 ? TargetSpine(o.Targets, counters) : null;
                if (spine != null) target = PixelToWorld(spine[spine.Count / 2]);
                if (!ArrowControls(o, counters, target, out var root, out var bend)) continue;
                var back = bend - target; // where the arrow comes in from: the objective line stays open there
                back.y = 0f;
                if (back.sqrMagnitude < 1e-10f) continue;
                back.Normalize();
                var color = Hoi4Armies.ColorOf(o.ArmyId, 0.95f);
                var fill = Color.Lerp(color, Color.white, 0.35f);

                o.Objective = spine != null ? ObjectiveFarSide(o.Targets, spine, WorldToPixel(root)) : ObjectiveLine(o.Target, target, back);
                if (o.Objective != null && o.Objective.Count >= 2)
                {
                    AddRibbon(o.Objective, color, 2f);
                    int mid = o.Objective.Count / 2;
                    o.LabelWorld = o.Objective[mid];
                    o.LabelAhead = o.Objective[Mathf.Min(mid + 1, o.Objective.Count - 1)];
                    o.LabelWorld.y = counters.TerrainHeight(o.LabelWorld);
                    o.LabelAhead.y = counters.TerrainHeight(o.LabelAhead);
                    float len = 0f;
                    for (int k = 1; k < o.Objective.Count; k++) len += Vector3.Distance(o.Objective[k - 1], o.Objective[k]);
                    o.LabelPath = LabelPath(o.Objective, len * 0.5f, out o.LabelAnchor);
                }
                if (arrowMaterial == null) continue;
                var shadow = new Vector3(offensiveShadowPx.x * worldPerPxX, 0f, -offensiveShadowPx.y * worldPerPxZ);
                AddOffensiveArrow(root + shadow, bend + shadow, target + shadow, fill, true);
                AddOffensiveArrow(root, bend, target, fill, false);
            }
            if (arrowMesh == null) return;
            arrowMesh.Clear();
            if (aVerts.Count == 0) return;
            arrowMesh.SetVertices(aVerts);
            arrowMesh.SetColors(aColors);
            arrowMesh.SetUVs(0, aUvs);
            arrowMesh.SetUVs(1, aInfos);
            arrowMesh.SetUVs(2, aKinds);
            arrowMesh.SetUVs(3, aCenters);
            arrowMesh.SetUVs(4, aBlurs);
            arrowMesh.SetTriangles(aTris, 0);
            arrowMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);
        }

        /// <summary>While dragging: the cursor trail dashed, the dotted line through the picked provinces' centres
        /// and a ring at each end. Prototype: Tools/prototypes/battleplan/drag.py.</summary>
        void AddDragPreview(Hoi4UnitCounters counters)
        {
            if (!OffensiveActive || !dragging || counters == null || ArmySelection(out var army) == null) return;
            var color = Hoi4Armies.ColorOf(army.Id, 0.95f);
            if (dragTrail.Count >= 2)
            {
                var trail = Relax(new List<Vector2>(dragTrail), 0.25f, 4);
                AddRibbon(ToWorld(trail), Color.Lerp(color, Color.white, 0.2f), 0f, dragTrailWidthPx);
            }
            var spine = TargetSpine(dragPicked, counters);
            if (spine == null || spine.Count == 0) return;
            if (spine.Count >= 2) AddRibbon(ToWorld(spine), dragDotColor, 3f, dragDotWidthPx);
            AddRing(spine[0]);
            if (spine.Count >= 2) AddRing(spine[spine.Count - 1]);
        }

        void AddRing(Vector2 centre)
        {
            var circle = new List<Vector2>(41);
            for (int i = 0; i <= 40; i++)
            {
                float a = i / 40f * Mathf.PI * 2f;
                circle.Add(centre + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * dragRingRadiusPx);
            }
            AddRibbon(ToWorld(circle), dragRingColor, 4f, 0.7f);
        }

        List<Vector3> ToWorld(List<Vector2> px)
        {
            var w = new List<Vector3>(px.Count);
            foreach (var p in px) w.Add(PixelToWorld(p));
            return w;
        }

        /// <summary>The targets' centres in drag order, smoothed (map px): the dotted line while dragging, the line
        /// the objective line follows and the arrow aims at the middle of.</summary>
        List<Vector2> TargetSpine(List<ushort> targets, Hoi4UnitCounters counters)
        {
            var c = new List<Vector2>(targets.Count);
            foreach (ushort p in targets)
                if (counters.TryGetProvincePoint(p, out var w)) c.Add(WorldToPixel(w));
            if (c.Count <= 1) return c.Count == 1 ? c : null;
            return Relax(c, 0.25f, 40);
        }

        /// <summary>
        /// Several targets: the outline of all of them on the side of the spine away from the arrow's root, ordered
        /// along the spine, pulled towards it and relaxed; at the spine's ends it wraps a little around. Teeth point
        /// at the spine. Prototype: drag.py objective_far_side.
        /// </summary>
        List<Vector3> ObjectiveFarSide(List<ushort> targets, List<Vector2> sp, Vector2 rootPx)
        {
            if (!EnsureArcs() || sp.Count < 2) return null;
            var tset = new HashSet<ushort>(targets);
            int n = sp.Count;
            var dir = new Vector2[n];
            var nrm = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                var d = sp[Mathf.Min(i + 1, n - 1)] - sp[Mathf.Max(i - 1, 0)];
                dir[i] = d.sqrMagnitude > 1e-12f ? d.normalized : Vector2.right;
                nrm[i] = new Vector2(dir[i].y, -dir[i].x);
            }
            if (Vector2.Dot(rootPx - sp[n / 2], nrm[n / 2]) > 0f)
                for (int i = 0; i < n; i++) nrm[i] = -nrm[i];

            var kept = new List<(float key, Vector2 p)>();
            foreach (var arc in allArcs)
            {
                if (tset.Contains(arc.ProvinceA) == tset.Contains(arc.ProvinceB) || arc.Points.Length < 2) continue;
                foreach (var p in arc.Points)
                {
                    int bi = 0;
                    float bd = float.MaxValue;
                    for (int i = 0; i < n; i++)
                    {
                        float d2 = (sp[i] - p).sqrMagnitude;
                        if (d2 < bd) { bd = d2; bi = i; }
                    }
                    var r = p - sp[bi];
                    float along = Vector2.Dot(r, dir[bi]);
                    float side = Vector2.Dot(r, nrm[bi]);
                    bool end = (bi == 0 && along < 0f) || (bi == n - 1 && along > 0f);
                    if (!(side > 0f || (end && side > -0.5f * Mathf.Abs(along)))) continue;
                    float key = end
                        ? bi + Mathf.Sign(along) * (2f - Mathf.Clamp(side / (Mathf.Abs(along) + 1e-6f), -0.5f, 1.9f))
                        : bi + along / 0.25f * 0.01f;
                    kept.Add((key, sp[bi] + r * objectiveScale));
                }
            }
            if (kept.Count < 4) return null;
            kept.Sort((a, b) => a.key.CompareTo(b.key));
            var line = new List<Vector2>(kept.Count);
            foreach (var (_, p) in kept) line.Add(p);
            line = Relax(line, 0.25f, 60);
            if (line.Count > 10) line = line.GetRange(3, line.Count - 6); // the relaxed ends hook
            // Teeth point left of travel (map space (t.y, -t.x)): turn the line so they face the spine
            int m = line.Count / 2;
            var t = line[Mathf.Min(m + 1, line.Count - 1)] - line[Mathf.Max(m - 1, 0)];
            var left = new Vector2(t.y, -t.x);
            int si = 0;
            float sd = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                float d2 = (sp[i] - line[m]).sqrMagnitude;
                if (d2 < sd) { sd = d2; si = i; }
            }
            if (Vector2.Dot(left, sp[si] - line[m]) < 0f) line.Reverse();
            return ToWorld(line);
        }

        void DrawOffensiveArrows()
        {
            if (arrowMaterial != null && arrowMesh != null && arrowMesh.vertexCount > 0)
                Graphics.DrawMesh(arrowMesh, Matrix4x4.identity, arrowMaterial, 0);
        }

        /// <summary>HOI4's arrow bends once. The root: where the path crosses the drawn front line (the line's point
        /// nearest the middle of the path's first two provinces), pulled back into own land. It leaves along the
        /// line's normal there (towards the enemy) and curves to the target: bend is the quadratic's control
        /// point. Prototype: Tools/prototypes/battleplan/offensive.py (arrow_controls).</summary>
        bool ArrowControls(Hoi4Offensive o, Hoi4UnitCounters counters, Vector3 tip, out Vector3 root, out Vector3 bend)
        {
            root = bend = default;
            if (o.Path == null || o.Path.Count < 2
                || !counters.TryGetProvincePoint(o.Path[0], out var c0) || !counters.TryGetProvincePoint(o.Path[1], out var c1)) return false;
            c0.y = c1.y = 0f;
            var mid = (c0 + c1) * 0.5f;
            ushort holder = Hoi4Occupation.ControllerOf(gameState, o.Target);
            List<Vector3> bestChain = null;
            int bestIndex = 0;
            float bestD = float.MaxValue;
            foreach (var p in plans)
            {
                if (p.IsAI || p.ArmyId != o.ArmyId || p.Country != holder || p.Chains == null) continue;
                foreach (var chain in p.Chains)
                    for (int i = 0; i < chain.Count; i++)
                    {
                        var q = chain[i];
                        q.y = 0f;
                        float d = (q - mid).sqrMagnitude;
                        if (d < bestD) { bestD = d; bestChain = chain; bestIndex = i; }
                    }
            }
            Vector3 near, normal;
            if (bestChain != null)
            {
                near = bestChain[bestIndex];
                near.y = 0f;
                // Tangent over ~3 map px either side (the chains are resampled every 0.25 px)
                var tangent = bestChain[Mathf.Min(bestIndex + 12, bestChain.Count - 1)] - bestChain[Mathf.Max(bestIndex - 12, 0)];
                tangent.y = 0f;
                normal = new Vector3(-tangent.z, 0f, tangent.x);
            }
            else
            {
                near = mid;
                normal = c1 - c0;
            }
            if (normal.sqrMagnitude < 1e-12f) normal = c1 - c0;
            normal.y = 0f;
            if (normal.sqrMagnitude < 1e-12f) return false;
            normal.Normalize();
            if (Vector3.Dot(normal, c1 - near) < 0f) normal = -normal;
            root = near - normal * (offensiveRootBackPx * worldPerPxX);
            bend = root + normal * (Vector3.Distance(root, tip) * offensiveBend);
            return true;
        }

        /// <summary>The target's outline on the side facing away from the arrow, pulled towards its centre and
        /// relaxed: HOI4's horseshoe around the objective, teeth (left of travel) pointing at the target.</summary>
        List<Vector3> ObjectiveLine(ushort province, Vector3 centreWorld, Vector3 backWorld)
        {
            if (!EnsureArcs()) return null;
            var c = WorldToPixel(centreWorld);
            var toRoot = new Vector2(backWorld.x, -backWorld.z).normalized; // world z points north, map y down
            float rootAngle = Mathf.Atan2(toRoot.y, toRoot.x);
            var pts = new List<(float angle, Vector2 p)>();
            foreach (var arc in allArcs)
            {
                if (arc.ProvinceA != province && arc.ProvinceB != province) continue;
                foreach (var q in arc.Points)
                {
                    var r = q - c;
                    if (r.sqrMagnitude < 1e-6f) continue;
                    if (Vector2.Dot(r.normalized, toRoot) >= 0.3f) continue; // the side the arrow comes from stays open
                    float a = Mathf.Repeat(Mathf.Atan2(r.y, r.x) - rootAngle, Mathf.PI * 2f);
                    pts.Add((a, c + r * objectiveScale));
                }
            }
            if (pts.Count < 4) return null;
            pts.Sort((x, y) => x.angle.CompareTo(y.angle));
            var line = new List<Vector2>(pts.Count);
            foreach (var (_, p) in pts) line.Add(p);
            line = Relax(line, 0.25f, 60);
            if (line.Count > 10) line = line.GetRange(3, line.Count - 6); // the relaxed ends hook
            // Teeth point left of travel (map space (t.y, -t.x)): turn the line so that faces the centre
            int m = line.Count / 2;
            var t = line[Mathf.Min(m + 1, line.Count - 1)] - line[Mathf.Max(m - 1, 0)];
            var left = new Vector2(t.y, -t.x);
            if (Vector2.Dot(left, c - line[m]) < 0f) line.Reverse();
            var world = new List<Vector3>(line.Count);
            foreach (var p in line) world.Add(PixelToWorld(p));
            return world;
        }

        static Vector3 Quadratic(Vector3 a, Vector3 b, Vector3 c, float t) => (1 - t) * (1 - t) * a + 2 * (1 - t) * t * b + t * t * c;

        static Vector3 Cubic(Vector3 a, Vector3 b, Vector3 c, Vector3 d, float t)
        {
            float s = 1 - t;
            return s * s * s * a + 3 * s * s * t * b + 3 * s * t * t * c + t * t * t * d;
        }

        /// <summary>HOI4's offensive arrow (move_thick), one bend: a quadratic from the root towards bend and on to the
        /// tip; a straight head along the chord of its last stretch; the body a cubic from the root (still leaving
        /// towards bend) to the head's start, its end tangent the head's direction.
        /// Prototype: Tools/prototypes/battleplan/offensive.py (arrow_mesh).</summary>
        void AddOffensiveArrow(Vector3 root, Vector3 bend, Vector3 tip, Color color, bool shadow)
        {
            float px = worldPerPxX;
            root.y = bend.y = tip.y = 0f;
            const int N = 200;
            var curve = new Vector3[N];
            var cum = new float[N];
            for (int i = 0; i < N; i++)
            {
                curve[i] = Quadratic(root, bend, tip, i / (N - 1f));
                if (i > 0) cum[i] = cum[i - 1] + Vector3.Distance(curve[i - 1], curve[i]);
            }
            float length = cum[N - 1];
            if (length < px) return;
            float head = Mathf.Min(offensiveHeadLengthPx * px, length * 0.45f);
            int c0 = Array.BinarySearch(cum, length - head);
            if (c0 < 0) c0 = Mathf.Min(~c0, N - 1);
            var hd = tip - curve[c0];
            if (hd.sqrMagnitude < 1e-12f) return;
            hd.Normalize();
            var headStart = tip - hd * head;
            var k1 = root + (bend - root) * 0.66f;
            var k2 = headStart - hd * (Vector3.Distance(headStart, root) * 0.45f);

            const int B = 120;
            var body = new Vector3[B];
            var bcum = new float[B];
            for (int i = 0; i < B; i++)
            {
                body[i] = Cubic(root, k1, k2, headStart, i / (B - 1f));
                if (i > 0) bcum[i] = bcum[i - 1] + Vector3.Distance(body[i - 1], body[i]);
            }
            float blen = Mathf.Max(bcum[B - 1], 1e-5f);
            float ribbon0 = offensiveRibbonPx * px, ribbon1 = ribbon0 * offensiveTaper;
            float tile = ribbon0 * ArrowTile * ArrowTexAspect / ArrowBodyUv; // keeps the texture's aspect
            float kind = shadow ? 1f : 0f;
            float fade = Mathf.Max(offensiveFadePx * px, 1e-5f); // the shader fades the body in over info.y 0..1
            // The shadow is blurred in the shader: its mesh is wider and longer than the arrow's (same texture
            // mapping, uv past 0..1) so the blur isn't cut off at the mesh edge
            float pad = shadow ? offensiveShadowBlurPx * 2.5f * px : 0f;
            var bodyBlur = new Vector2(ArrowTile / (tile / px), ArrowBodyUv / (ribbon0 / px));
            for (int i = 0; i < B; i++)
            {
                Vector3 d;
                if (i == B - 1) d = hd;
                else
                {
                    d = body[Mathf.Min(i + 2, B - 1)] - body[Mathf.Max(i - 2, 0)];
                    d = d.sqrMagnitude > 1e-12f ? d.normalized : hd;
                }
                float f = bcum[i] / blen;
                AddArrowPair(body[i], d, Mathf.Lerp(ribbon0, ribbon1, f) * 0.5f, pad, bcum[i] / tile, 0f, bcum[i] / fade, kind, bodyBlur, color, i > 0);
            }
            float headWidth = ribbon1 / ArrowBodyUv;
            var headBlur = new Vector2((1f - 0.63f) / (head / px), 1f / (headWidth / px));
            // The shadow's head also reaches a little past the tip and back over the barbs
            float ext = pad / head;
            for (int k = 0; k <= 8; k++)
            {
                float f = Mathf.Lerp(-ext, 1f + ext, k / 8f);
                AddArrowPair(headStart + hd * (head * f), hd, headWidth * 0.5f, pad, f, 1f, 1f, kind, headBlur, color, k > 0);
            }
        }

        /// <summary>A cross-section: half width plus pad each side, uv.y 0..1 over the half width (past it on the pad).</summary>
        void AddArrowPair(Vector3 p, Vector3 dir, float half, float pad, float u, float isHead, float along, float kind, Vector2 blur, Color color, bool connect)
        {
            var side = new Vector3(-dir.z, 0f, dir.x);
            float vPad = half > 1e-7f ? pad / (2f * half) : 0f;
            int v = aVerts.Count;
            for (int s = 0; s < 2; s++)
            {
                aVerts.Add(p + side * ((s == 0 ? -1f : 1f) * (half + pad)));
                aColors.Add(color);
                aUvs.Add(new Vector2(u, s == 0 ? -vPad : 1f + vPad));
                aInfos.Add(new Vector2(isHead, along));
                aKinds.Add(new Vector2(kind, 0f));
                aCenters.Add(new Vector2(p.x, p.z));
                aBlurs.Add(blur);
            }
            if (!connect) return;
            aTris.Add(v - 2); aTris.Add(v); aTris.Add(v - 1);
            aTris.Add(v - 1); aTris.Add(v); aTris.Add(v + 1);
        }
    }
}
