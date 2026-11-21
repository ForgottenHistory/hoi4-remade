using System;
using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Systems;

using Hoi4.Map;
using Map.Rendering.Border;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hoi4.Units
{
    /// <summary>One battle plan: a front line along the border of its owner against a country. The player's
    /// plans belong to an army; an AI country's plans (IsAI) hold all its divisions that aren't attacking.</summary>
    public sealed class Hoi4BattlePlan
    {
        public int Id;
        public ushort Owner;                        // the country whose divisions hold the line
        public bool IsAI;                           // made by the AI: not drawn, no army
        public int ArmyId;                          // the player's army (0 for AI plans)
        public ushort Country;                      // the country the line is drawn against
        // Presentation only (the player's plans): the traced border the line is drawn along
        public List<BorderArc> Arcs;                // the border arcs the line follows
        public List<List<Vector3>> Chains;          // the arcs joined into continuous polylines, enemy side on the left of travel
        public readonly HashSet<ushort> Units = new HashSet<ushort>();
        public List<ushort> FrontProvinces;         // own provinces along the line, in line order (all fronts)
        public List<List<ushort>> Fronts;           // the same per separate stretch of the line (a pocket is its own front); simulation, from adjacency
        // The stretch of the border this line holds (its own front provinces): player lines cover only the stretch
        // they were drawn on, like HOI4, so a border split by a third country (Luxembourg between Belgium and
        // Alsace) is two lines. Follows the border as it moves. Null: the whole border (AI plans).
        public HashSet<ushort> Section;
        // A line dragged over part of a stretch (right-drag): the stretch's other front provinces, which it keeps out
        // as the border moves (new front provinces next to it join, these never do). Null: it holds whole stretches.
        public HashSet<ushort> Outside;
        public readonly Dictionary<ushort, ushort> Slot = new Dictionary<ushort, ushort>(); // unit -> the front province it holds
        public readonly Dictionary<(ushort, int), bool> Reach = new Dictionary<(ushort, int), bool>(); // (province, front) reachable by land; cleared with the fronts
        public readonly Dictionary<ushort, int> FrontIndex = new Dictionary<ushort, int>(); // front province -> its front
        // Presentation: one label per front (a pocket gets its own), on that front's longest drawn stretch,
        // and the front each drawn stretch (Chains[i]) belongs to
        public readonly List<FrontLabel> Labels = new List<FrontLabel>();
        public readonly List<int> ChainFront = new List<int>();

        /// <summary>Divisions slotted on each front (an attack from the line still counts for its front).</summary>
        public int[] DivisionsPerFront()
        {
            var counts = new int[Fronts?.Count ?? 0];
            foreach (var kv in Slot)
                if (FrontIndex.TryGetValue(kv.Value, out int f) && f < counts.Length) counts[f]++;
            return counts;
        }
    }

    /// <summary>Text anchor on a front's line and a point a little further along it (the text's direction).</summary>
    public struct FrontLabel
    {
        public int Front;
        public Vector3 World, Ahead;
        public List<Vector3> Path;  // the line around the label (world, on the terrain): the text bends along it
        public int Anchor;          // index in Path of the label's centre
    }

    /// <summary>
    /// GAME: battle plans, HOI4's way of ordering a front. Only offensive lines for now. The tool (button in
    /// Hoi4BattlePlanPanel) follows the cursor: hovering a country shows its border with the player as a green
    /// dashed line and stripes the player's provinces along it; a left click places the line in the colour
    /// of the army of the selected divisions, which get assigned to it unless they already follow another
    /// plan. The assigned divisions spread evenly over the own provinces along the line and hold them
    /// (Distribute); a manual move or halt takes a division off its plan. Every other division of an army
    /// with a plan joins one of the army's plans by itself (AutoAssign), and leaves it if it leaves the army. When provinces change hands the
    /// lines are traced again along the new border (RefreshPlans), so they follow the front as it moves;
    /// a plan whose country no longer borders its owner is removed.
    /// AI countries get the same front lines without drawing them (EnsureAIPlans): one per enemy they
    /// border, holding all their divisions. Hoi4EnemyAI decides attacks itself and borrows a division
    /// off its line for an attack (Borrow); when the attack is over the division rejoins the line.
    /// Simulation and presentation are separate: which provinces form a front and in what order comes from
    /// the province adjacency graph with integer maths only (FrontsAgainst), so it is the same on every
    /// client and doesn't depend on the renderer. The drawn line is presentation: ribbons built from the
    /// engine's traced border arcs (IBorderArcSource), drawn by Hoi4/PlanLine, for the player's plans only.
    /// The province stripes come from a province-indexed texture (_Hoi4PlanTex) read in Hoi4MapModes.hlsl.
    /// Offensive plans (arrows to a target province) are in Hoi4BattlePlans.Offensives.cs.
    /// </summary>
    public sealed partial class Hoi4BattlePlans : MonoBehaviour
    {
        public static Hoi4BattlePlans Instance { get; private set; }

        const int TexWidth = 256;
        static readonly int PlanTexId = Shader.PropertyToID("_Hoi4PlanTex");
        static readonly int PlanActiveId = Shader.PropertyToID("_Hoi4PlanActive");
        static readonly int HeightmapId = Shader.PropertyToID("_HeightmapTexture");
        static readonly int HeightScaleId = Shader.PropertyToID("_HeightScale");
        static readonly int BoundsId = Shader.PropertyToID("_MapWorldBounds");

        [Tooltip("Placed front line width in map pixels (HOI4: about 1/6 of a European province)")] public float lineWidthPx = 1.7f;
        [Tooltip("Width of the unconfirmed (dashed) line relative to the placed one")] public float previewWidthScale = 0.75f;
        public Color previewColor = new Color(0.36f, 0.92f, 0.32f, 1f);
        [Tooltip("A plan under the cursor: its front line, or an offensive's arrow and objective line, turn this colour (HOI4)")]
        public Color hoverColor = new Color(0.36f, 0.92f, 0.32f, 1f);
        [Tooltip("Hovering: lines thinner than this on screen are still picked within it (screen px)")]
        public float hoverMinPx = 5f;

        GameState gameState;
        Material material;
        Mesh mesh;
        Texture2D planTex;
        Color32[] planPixels;
        bool dirtyMesh, dirtyTex;
        bool ready;

        List<BorderArc> allArcs;
        float mapHeightPx;
        Vector4 bounds;
        float worldPerPxX, worldPerPxZ;

        readonly List<Hoi4BattlePlan> plans = new List<Hoi4BattlePlan>();
        readonly Dictionary<ushort, Hoi4BattlePlan> planOf = new Dictionary<ushort, Hoi4BattlePlan>();
        int nextId = 1;
        // On a manual order: unit -> the plan it left (0: none). Off its plan until the order is done (it stops),
        // then back to that plan, like HOI4
        readonly Dictionary<ushort, int> manualOrders = new Dictionary<ushort, int>();
        IDisposable[] subscriptions;
        bool borderDirty;
        readonly HashSet<ushort> dirtyCountries = new HashSet<ushort>();   // controllers that gained or lost land since the last refresh
        readonly HashSet<ushort> borrowed = new HashSet<ushort>();         // AI divisions off their line while they attack
        readonly HashSet<Hoi4BattlePlan> dirtyPlans = new HashSet<Hoi4BattlePlan>(); // lost a division: spread again this hour
        bool simReady;                                                     // adjacency loaded: plans work even when drawing them fails
        ushort[] landProvinces;                                            // sorted
        ushort[][] landNeighbours;                                         // per province id: land neighbours, sorted
        ushort[] ctrl;                                                     // controller per province id, snapshot during a refresh
        readonly Dictionary<ushort, HashSet<ushort>> enemyLand = new Dictionary<ushort, HashSet<ushort>>(); // per country, this hour

        // Hover preview
        ushort hoverProvince, hoverCountry;
        // Right-drag along a border (front line tool): the line from where the press was to the cursor
        bool lineDrag;
        ushort lineDragStart, lineDragCountry;
        Vector3 lineDragStartWorld;
        Vector2 lineDragPress;               // screen: a release without moving is a plain right click (cancel)
        List<BorderArc> previewArcs;
        List<List<Vector3>> previewChains;
        readonly HashSet<ushort> previewProvinces = new HashSet<ushort>();

        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Color> colors = new List<Color>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<Vector2> styles = new List<Vector2>();
        readonly List<Vector2> centers = new List<Vector2>();
        readonly List<int> tris = new List<int>();

        /// <summary>True while the draw tool is on.</summary>
        public bool Active { get; private set; }
        public IReadOnlyList<Hoi4BattlePlan> Plans => plans;
        public event Action Changed;

        public Hoi4BattlePlan PlanOf(ushort unitId) => planOf.TryGetValue(unitId, out var p) ? p : null;
        public bool IsBorrowed(ushort unitId) => borrowed.Contains(unitId);         // an AI division off its line, attacking
        public bool HasManualOrder(ushort unitId) => manualOrders.ContainsKey(unitId); // a player division following a manual order

        public Hoi4BattlePlan GetPlan(int planId)
        {
            foreach (var p in plans) if (p.Id == planId) return p;
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            if (Instance != null) return;
            var go = new GameObject("Hoi4BattlePlans");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4BattlePlans>();
        }

        void Awake()
        {
            Instance = this;
            Changed += () => Hoi4UnitCounters.Instance?.MarkDirty(); // the no-orders mark follows plan and army membership
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Shader.SetGlobalFloat(PlanActiveId, 0f);
            if (subscriptions != null)
                foreach (var s in subscriptions) s?.Dispose();
            if (material != null) Destroy(material);
            if (mesh != null) Destroy(mesh);
            if (planTex != null) Destroy(planTex);
            DestroyOffensiveDrawing();
        }

        IEnumerator Start()
        {
            // Simulation: needs only the game state and the province graph
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Adjacencies == null
                   || Engine.ArchonEngine.Instance.GameState.Pathfinding == null
                   || !Engine.ArchonEngine.Instance.GameState.Pathfinding.IsInitialized)
                yield return null;
            var engine = Engine.ArchonEngine.Instance;
            gameState = engine.GameState;
            BuildLandGraph();
            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<ProvinceControllerChangedEvent>(e => MarkBorder(e.OldController, e.NewController)),
                gameState.EventBus.Subscribe<ProvinceOwnershipChangedEvent>(e => MarkBorder(e.OldOwner, e.NewOwner)),
            };
            borderDirty = true; // first hour: the AI draws its lines for the wars already going
            simReady = true;

            // Presentation: the tool and the drawn lines
            while (Hoi4UnitSelection.Instance == null || Hoi4UnitCounters.Instance == null || !Hoi4UnitCounters.Instance.IsReady)
                yield return null;

            var shader = Shader.Find("Hoi4/PlanLine");
            if (shader == null)
            {
                ArchonLogger.LogWarning("Hoi4BattlePlans: Hoi4/PlanLine shader missing, battle plans disabled", "game_units");
                yield break;
            }
            var counters = Hoi4UnitCounters.Instance;
            bounds = counters.MapWorldBounds;
            mapHeightPx = engine.TextureManager.MapHeight;
            worldPerPxX = counters.WorldPerMapPixel;
            worldPerPxZ = bounds.w / Mathf.Max(mapHeightPx, 1f);

            material = new Material(shader) { name = "Hoi4PlanLine" };
            var mask = LoadTexture("gfx/maparrows/front_mask.png");
            var pattern = LoadTexture("gfx/maparrows/front_pattern.png");
            if (mask == null || pattern == null)
            {
                ArchonLogger.LogWarning("Hoi4BattlePlans: gfx/maparrows/front_mask.png missing (rerun Tools/hoi4_convert.py), battle plans disabled", "game_units");
                yield break;
            }
            var pmask = LoadTexture("gfx/maparrows/preview_mask.png");
            var ppattern = LoadTexture("gfx/maparrows/preview_pattern.png");
            if (pmask != null && ppattern != null)
            {
                material.SetTexture("_PreviewMaskTex", pmask);
                material.SetTexture("_PreviewPatternTex", ppattern);
            }
            material.SetTexture("_MaskTex", mask);
            material.SetTexture("_PatternTex", pattern);
            var umask = LoadTexture("gfx/maparrows/front_unassigned_mask.png");
            material.SetTexture("_UnassignedMaskTex", umask != null ? umask : mask); // older data: drawn like a held front
            material.SetVector(BoundsId, bounds);
            if (counters.Heightmap != null) material.SetTexture(HeightmapId, counters.Heightmap);
            material.SetFloat(HeightScaleId, counters.HeightScale);
            mesh = new Mesh { name = "Hoi4BattlePlans", indexFormat = IndexFormat.UInt32 };

            planTex = new Texture2D(TexWidth, TexWidth, TextureFormat.RGBA32, false, linear: true)
            {
                filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Hoi4PlanTex"
            };
            planPixels = new Color32[TexWidth * TexWidth];
            planTex.SetPixelData(planPixels, 0);
            planTex.Apply(false);
            Shader.SetGlobalTexture(PlanTexId, planTex);
            Shader.SetGlobalFloat(PlanActiveId, 0f);
            SetUpOffensiveDrawing(counters);
            ready = true;
            foreach (var plan in plans) if (!plan.IsAI) TraceLine(plan); // placed before drawing was ready
            dirtyMesh = true;
        }

        /// <summary>Land provinces and their land neighbours, sorted (the engine's adjacency map has no fixed order).</summary>
        void BuildLandGraph()
        {
            var ids = gameState.Provinces.GetAllProvinceIds(Unity.Collections.Allocator.Temp);
            var land = new List<ushort>(ids.Length);
            int max = 0;
            foreach (ushort id in ids)
            {
                max = Mathf.Max(max, id);
                if (id != 0 && !Hoi4Regions.IsWater(id)) land.Add(id);
            }
            ids.Dispose();
            land.Sort();
            landProvinces = land.ToArray();
            landNeighbours = new ushort[max + 1][];
            var buffer = new List<ushort>();
            foreach (ushort p in landProvinces)
            {
                buffer.Clear();
                var n = gameState.Adjacencies.GetNeighbors(p, Unity.Collections.Allocator.Temp);
                foreach (ushort q in n) if (q != 0 && q <= max && !Hoi4Regions.IsWater(q)) buffer.Add(q);
                n.Dispose();
                buffer.Sort();
                landNeighbours[p] = buffer.ToArray();
            }
        }

        ushort Ctrl(ushort province) => ctrl != null ? ctrl[province] : Hoi4Occupation.ControllerOf(gameState, province);

        ushort[] Neighbours(ushort province) =>
            province < landNeighbours.Length && landNeighbours[province] != null ? landNeighbours[province] : Array.Empty<ushort>();

        void MarkBorder(ushort a, ushort b)
        {
            borderDirty = true;
            if (a != 0) dirtyCountries.Add(a);
            if (b != 0) dirtyCountries.Add(b);
        }

        static Texture2D LoadTexture(string relative)
        {
            string path = Core.Modding.DataFileResolver.Resolve(relative);
            if (!System.IO.File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear, name = System.IO.Path.GetFileNameWithoutExtension(path) };
            if (tex.LoadImage(System.IO.File.ReadAllBytes(path), true)) return tex;
            Destroy(tex);
            return null;
        }

        static ushort Player
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        // --- Tool --------------------------------------------------------------------------------

        /// <summary>The own divisions that are selected and belong to an army: the ones a plan is made for.</summary>
        public bool HasArmySelection => ArmySelection(out _) != null;

        List<ushort> ArmySelection(out Hoi4Army army)
        {
            army = null;
            var armies = Hoi4Armies.Instance;
            var units = gameState?.Units;
            ushort player = Player;
            if (armies == null || units == null || player == 0) return null;
            List<ushort> result = null;
            foreach (ushort id in Hoi4UnitSelection.Instance.Selected)
            {
                if (!units.HasUnit(id) || units.GetUnit(id).countryID != player) continue;
                var a = armies.ArmyOf(id);
                if (a == null || (army != null && a != army)) continue;
                army = a;
                (result ??= new List<ushort>()).Add(id);
            }
            return result;
        }

        public void BeginDrawing()
        {
            if (!ready || !HasArmySelection) return;
            if (OffensiveActive) CancelOffensive();
            Active = true;
            lineDrag = false;
            hoverProvince = 0;
            SetPreview(0);
            Changed?.Invoke();
        }

        public void Cancel()
        {
            if (!Active) return;
            Active = false;
            lineDrag = false;
            SetPreview(0);
            Changed?.Invoke();
        }

        /// <summary>Remove the plans the selected divisions follow.</summary>
        public void DeleteSelectedPlans()
        {
            // Offensives first: the bin removes those of the selected divisions; with none, their front lines
            var offensiveIds = new SortedSet<int>();
            foreach (ushort id in Hoi4UnitSelection.Instance.Selected)
            {
                var o = OffensiveOf(id);
                if (o != null) offensiveIds.Add(o.Id);
            }
            if (offensiveIds.Count > 0)
            {
                foreach (int oid in offensiveIds)
                    gameState.TryExecuteCommand(new DeleteOffensiveCommand { CountryId = Player, OffensiveId = oid }, out _);
                return;
            }
            var ids = new SortedSet<int>();
            foreach (ushort id in Hoi4UnitSelection.Instance.Selected)
            {
                var plan = PlanOf(id);
                if (plan != null && !plan.IsAI) ids.Add(plan.Id);
            }
            foreach (int planId in ids)
                gameState.TryExecuteCommand(new DeleteBattlePlanCommand { CountryId = Player, PlanId = planId }, out _);
        }

        /// <summary>DeleteBattlePlanCommand.</summary>
        public void DeletePlan(int planId)
        {
            var plan = GetPlan(planId);
            if (plan == null) return;
            RemovePlan(plan);
            dirtyMesh = true;
            Changed?.Invoke();
        }

        void RemovePlan(Hoi4BattlePlan plan)
        {
            Core.Recording.SessionRecorder.Note("plans", "removed", plan.Owner, 0, 0, plan.Country, $"plan {plan.Id}, {plan.Units.Count} divisions released");
            plans.Remove(plan);
            dirtyPlans.Remove(plan);
            foreach (ushort u in plan.Units) planOf.Remove(u);
            plan.Units.Clear();
            plan.Slot.Clear();
        }

        /// <summary>Selection click interceptor while the tool is on. Always consumes the click.</summary>
        public bool OnClick(int button, Vector2 screen)
        {
            if (OffensiveActive) return OnOffensiveClick(button);
            if (!Active) return false;
            if (button == 0 && previewArcs != null && previewArcs.Count > 0) Place();
            else if (button == 1 && previewArcs != null && previewArcs.Count > 0 && !lineDrag)
            {
                // Right press on a border: drag the line along it (placed on release)
                lineDrag = true;
                lineDragCountry = hoverCountry;
                lineDragStart = hoverProvince;
                lineDragPress = Input.mousePosition;
                var cam = Camera.main;
                lineDragStartWorld = cam != null && TryCursorWorld(cam, out var w) ? w : Vector3.zero;
                SetPreview(hoverProvince);
            }
            else if (button == 1 && !lineDrag) Cancel();
            return true;
        }

        void Place()
        {
            ushort end = lineDrag ? (hoverProvince != 0 ? hoverProvince : lineDragStart) : (ushort)0;
            ushort start = lineDrag ? lineDragStart : hoverProvince;
            lineDrag = false;
            var selected = ArmySelection(out var army);
            if (selected == null) { Cancel(); return; }
            selected.Sort();
            ushort target = hoverCountry;
            Active = false;
            SetPreview(0);
            // Province picks the stretch of the border; with End (dragged) the line covers only start to end
            gameState.TryExecuteCommand(new PlaceBattlePlanCommand { CountryId = Player, TargetCountry = target, Province = start, End = end, ArmyId = army.Id, UnitIds = selected }, out _);
            dirtyMesh = true;
            Changed?.Invoke();
        }

        /// <summary>True when the owner controls land next to land the country controls.</summary>
        public bool HasBorder(ushort owner, ushort country) => simReady && country != 0 && country != owner && FrontsAgainst(owner, country).Count > 0;

        /// <summary>PlaceBattlePlanCommand: the owner's front line against the country, traced from the
        /// current border, held by these divisions (those already on another plan stay there).</summary>
        public Hoi4BattlePlan CreatePlan(ushort owner, int armyId, ushort country, List<ushort> unitIds, ushort province = 0, ushort end = 0)
        {
            if (!simReady) return null;
            var plan = new Hoi4BattlePlan { Id = nextId++, Owner = owner, ArmyId = armyId, Country = country };
            var all = FrontsAgainst(owner, country);
            plan.Section = end != 0 ? SectionBetween(all, province, end, out plan.Outside) : StretchAt(all, province);
            var fronts = FrontsOf(plan, all);
            if (fronts.Count == 0) return null;
            SetFronts(plan, fronts);
            foreach (ushort u in unitIds)
            {
                manualOrders.Remove(u);
                if (!planOf.ContainsKey(u)) { plan.Units.Add(u); planOf[u] = plan; }
            }
            TraceLine(plan);
            plans.Add(plan);
            Core.Recording.SessionRecorder.Note("plans", "created", owner, 0, 0, country, $"plan {plan.Id} army {armyId}: {plan.Units.Count} of {unitIds.Count} selected divisions, fronts {FrontSizes(plan)}");
            Distribute(plan);
            dirtyMesh = true;
            Changed?.Invoke();
            return plan;
        }

        // --- Hover -------------------------------------------------------------------------------

        void Update()
        {
            if (!ready) return;
            UpdateHover();
            if (dirtyMesh) { dirtyMesh = false; Rebuild(); }
            if (OffensiveActive) { UpdateOffensiveTool(); return; }
            if (!Active) return;
            if (Input.GetKeyDown(KeyCode.Escape)) { Cancel(); return; }
            if (!HasArmySelection) { Cancel(); return; }

            ushort province = 0;
            var cam = Camera.main;
            var selector = Engine.ArchonEngine.Instance.ProvinceSelector;
            if (cam != null && selector != null && !global::Map.CameraControllers.BaseCameraController.ScrollCaptured)
                province = selector.GetProvinceAtScreenPosition(Input.mousePosition, cam);
            if (province != hoverProvince)
            {
                hoverProvince = province;
                SetPreview(province);
            }
            if (lineDrag)
            {
                if (!Input.GetMouseButton(1))
                {
                    if (((Vector2)Input.mousePosition - lineDragPress).sqrMagnitude < 36f) Cancel();
                    else if (previewArcs != null && previewArcs.Count > 0) Place();
                    else { lineDrag = false; SetPreview(hoverProvince); }
                    return;
                }
                dirtyMesh = true; // the end ring follows the cursor
            }
        }

        /// <summary>
        /// Game time, not frames, drives the plans so they behave the same at any speed: every hour the lines
        /// follow border changes from that hour (several provinces often flip at once), divisions that died
        /// or left the army drop off, new army divisions join, and idle divisions return to their spots.
        /// </summary>
        public void SimulateHour()
        {
            if (!simReady) return;
            enemyLand.Clear();
            bool refreshed = borderDirty;
            if (borderDirty) RefreshPlans();
            PruneOffensives(refreshed);
            PruneExecuting();
            if (plans.Count == 0) return;
            var units = gameState.Units;
            var queue = units.MovementQueue;
            var armies = Hoi4Armies.Instance;
            var touched = new HashSet<Hoi4BattlePlan>(dirtyPlans);
            dirtyPlans.Clear();
            ReturnFromManualOrders(touched);
            // An AI attack is over once the division stops (won, broke off, or retreated): back to the line
            borrowed.RemoveWhere(u => !units.HasUnit(u) || !queue.IsUnitMoving(u));
            foreach (var plan in plans)
            {
                int removed = plan.Units.RemoveWhere(u =>
                {
                    bool keep = units.HasUnit(u) && (plan.IsAI
                        ? units.GetUnit(u).countryID == plan.Owner && !borrowed.Contains(u)        // dead, captured or attacking
                        : armies != null && armies.ArmyOf(u)?.Id == plan.ArmyId);                   // dead, or left the army
                    if (keep) return false;
                    planOf.Remove(u);
                    plan.Slot.Remove(u);
                    return true;
                });
                if (removed > 0) touched.Add(plan);
            }
            AutoAssign(touched);
            AutoAssignAI(touched);
            bool playerTouched = false;
            foreach (var plan in plans)
            {
                if (touched.Contains(plan)) { Distribute(plan); playerTouched |= !plan.IsAI; }
                else Hold(plan);
            }
            AssignOffensives();
            Advance();
            if (playerTouched) Changed?.Invoke();
        }

        /// <summary>Daily, from Hoi4Simulation: wars declared or ended change which AI lines exist, so all
        /// lines are checked on the next hour.</summary>
        public void SimulateDay()
        {
            if (!simReady) return;
            borderDirty = true;
            UpdatePlanning();
        }

        /// <summary>Hoi4EnemyAI takes a division off its line to attack; it rejoins when the attack is over.</summary>
        public void Borrow(ushort unitId)
        {
            borrowed.Add(unitId);
            if (!planOf.TryGetValue(unitId, out var plan)) return;
            planOf.Remove(unitId);
            plan.Units.Remove(unitId);
            plan.Slot.Remove(unitId);
            dirtyPlans.Add(plan); // the rest close the gap on the next hour
        }

        /// <summary>
        /// A manual order lasts until the division stops: arrived, halted, or its attack over (a retreat still
        /// counts as marching). Then it goes back to the plan it left, if that plan is still its army's;
        /// otherwise AutoAssign gives it its army's least loaded plan, or it stays put when the army has none.
        /// </summary>
        void ReturnFromManualOrders(HashSet<Hoi4BattlePlan> touched)
        {
            if (manualOrders.Count == 0) return;
            var units = gameState.Units;
            var queue = units.MovementQueue;
            var armies = Hoi4Armies.Instance;
            var done = new List<ushort>();
            foreach (var kv in manualOrders)
            {
                ushort u = kv.Key;
                if (!units.HasUnit(u) || armies == null || armies.ArmyOf(u) == null) { done.Add(u); continue; } // gone, or left the army
                if (queue.IsUnitMoving(u) || Hoi4Combat.IsRetreating(u)) continue;                          // still on its order
                done.Add(u);
            }
            done.Sort();
            foreach (ushort u in done)
            {
                int planId = manualOrders[u];
                manualOrders.Remove(u);
                if (!units.HasUnit(u) || planOf.ContainsKey(u)) continue;
                var plan = planId != 0 ? GetPlan(planId) : null;
                var army = armies?.ArmyOf(u);
                if (plan == null || plan.IsAI || army == null || plan.ArmyId != army.Id) continue; // AutoAssign decides
                plan.Units.Add(u);
                planOf[u] = plan;
                touched.Add(plan);
                Core.Recording.SessionRecorder.Note("plans", "rejoined", plan.Owner, u, units.GetUnit(u).provinceID, 0, $"plan {plan.Id}: manual order done");
            }
        }

        /// <summary>An AI country's divisions that hold no line (new, back from an attack, or the line moved
        /// on) join its line with the fewest divisions per front province. Marching ones (retreating, say)
        /// wait until they stop.</summary>
        void AutoAssignAI(HashSet<Hoi4BattlePlan> touched)
        {
            var byOwner = new SortedDictionary<ushort, List<Hoi4BattlePlan>>();
            foreach (var plan in plans)
                if (plan.IsAI)
                {
                    if (!byOwner.TryGetValue(plan.Owner, out var list)) byOwner[plan.Owner] = list = new List<Hoi4BattlePlan>();
                    list.Add(plan);
                }
            var units = gameState.Units;
            var queue = units.MovementQueue;
            foreach (var kv in byOwner)
            {
                var ids = units.GetCountryUnits(kv.Key);
                ids.Sort();
                foreach (ushort u in ids)
                {
                    if (planOf.ContainsKey(u) || borrowed.Contains(u) || queue.IsUnitMoving(u)) continue;
                    Hoi4BattlePlan best = null;
                    foreach (var plan in kv.Value)
                        if (best == null || LessLoaded(plan, best)) best = plan;
                    if (best == null) break;
                    best.Units.Add(u);
                    planOf[u] = best;
                    touched.Add(best);
                }
            }
        }

        /// <summary>Divisions of an army with a plan that follow no plan (new to the army, or never selected
        /// when the line was drawn) join the army's plan with the fewest divisions per front province.</summary>
        bool AutoAssign(HashSet<Hoi4BattlePlan> touched)
        {
            var armies = Hoi4Armies.Instance;
            if (armies == null || plans.Count == 0) return false;
            bool any = false;
            foreach (var army in armies.All)
                foreach (ushort u in army.Units)
                {
                    if (planOf.ContainsKey(u) || manualOrders.ContainsKey(u)) continue;
                    Hoi4BattlePlan best = null;
                    foreach (var plan in plans)
                        if (!plan.IsAI && plan.ArmyId == army.Id && (best == null || LessLoaded(plan, best))) best = plan;
                    if (best == null) break; // this army has no plan
                    best.Units.Add(u);
                    planOf[u] = best;
                    touched.Add(best);
                    any = true;
                }
            return any;
        }

        /// <summary>Fewer divisions per front province than b (integer cross-multiplication; ties keep b, the earlier plan).</summary>
        static bool LessLoaded(Hoi4BattlePlan a, Hoi4BattlePlan b)
        {
            long pa = Math.Max(a.FrontProvinces?.Count ?? 1, 1), pb = Math.Max(b.FrontProvinces?.Count ?? 1, 1);
            return a.Units.Count * pb < b.Units.Count * pa;
        }

        bool EnsureArcs()
        {
            if (allArcs != null) return true;
            var renderer = Engine.ArchonEngine.Instance.BorderDispatcher?.GetActiveBorderRenderer() as IBorderArcSource;
            var arcs = renderer?.Arcs;
            if (arcs == null || arcs.Count == 0) return false;
            allArcs = new List<BorderArc>(arcs);
            return true;
        }

        void SetPreview(ushort province)
        {
            previewProvinces.Clear();
            previewArcs = null;
            previewChains = null;
            hoverCountry = 0;
            if (province != 0 && EnsureArcs()) FindBorder(province);
            dirtyTex = dirtyMesh = true;
            RebuildTexture();
        }

        /// <summary>The country to draw against: the hovered province's controller, or for an own province the
        /// foreign country holding most of its neighbouring arcs.</summary>
        void FindBorder(ushort province)
        {
            ushort player = Player;
            if (lineDrag)
            {
                if (!simReady) return;
                var all = FrontsAgainst(player, lineDragCountry);
                ushort end = province != 0 && !Hoi4Regions.IsWater(province) ? province : lineDragStart;
                var between = SectionBetween(all, lineDragStart, end, out _);
                if (between == null) return;
                var dragArcs = BorderWith(player, lineDragCountry, previewProvinces, between);
                if (dragArcs.Count == 0) { previewProvinces.Clear(); return; }
                hoverCountry = lineDragCountry;
                previewArcs = dragArcs;
                previewChains = BuildChains(dragArcs, lineDragCountry);
                return;
            }
            if (player == 0 || Hoi4Regions.IsWater(province)) return;
            ushort country = Hoi4Occupation.ControllerOf(gameState, province);
            if (country == player)
            {
                var votes = new Dictionary<ushort, int>();
                int best = 0;
                foreach (var arc in allArcs)
                {
                    ushort other = arc.ProvinceA == province ? arc.ProvinceB : arc.ProvinceB == province ? arc.ProvinceA : (ushort)0;
                    if (other == 0 || Hoi4Regions.IsWater(other)) continue;
                    ushort c = Hoi4Occupation.ControllerOf(gameState, other);
                    if (c == 0 || c == player) continue;
                    votes.TryGetValue(c, out int n);
                    votes[c] = ++n;
                    if (n > best) { best = n; country = c; }
                }
                if (best == 0) return;
            }
            if (country == 0) return;

            // Only the stretch of the border nearest the cursor (what Place will create)
            var section = simReady ? StretchAt(FrontsAgainst(player, country), province) : null;
            var arcs = BorderWith(player, country, previewProvinces, section);
            if (arcs.Count == 0) { previewProvinces.Clear(); return; }
            hoverCountry = country;
            previewArcs = arcs;
            previewChains = BuildChains(arcs, country);
        }

        /// <summary>Presentation: the traced arcs between provinces the owner controls and provinces the country
        /// controls (the drawn line); the owner's provinces along them go into ownSide (the preview stripes).</summary>
        List<BorderArc> BorderWith(ushort owner, ushort country, HashSet<ushort> ownSide, HashSet<ushort> section = null)
        {
            var arcs = new List<BorderArc>();
            if (owner == 0 || !EnsureArcs()) return arcs;
            for (int i = 0; i < allArcs.Count; i++)
            {
                var arc = allArcs[i];
                ushort a = Hoi4Occupation.ControllerOf(gameState, arc.ProvinceA);
                ushort b = Hoi4Occupation.ControllerOf(gameState, arc.ProvinceB);
                ushort own;
                if (a == owner && b == country) own = arc.ProvinceA;
                else if (b == owner && a == country) own = arc.ProvinceB;
                else continue;
                if (!InSection(section, own)) continue; // another stretch of the border
                ownSide.Add(own);
                arcs.Add(arc);
            }
            return arcs;
        }

        /// <summary>Every AI country at war gets a front line against each enemy it borders by land; lines
        /// against countries it is no longer at war with, or no longer borders, go. Countries without
        /// divisions get none.</summary>
        void EnsureAIPlans()
        {
            ushort player = Player;
            var diplomacy = gameState.Diplomacy;
            if (diplomacy == null) return;
            var hasUnits = new Dictionary<ushort, bool>();
            bool HasUnits(ushort c)
            {
                if (!hasUnits.TryGetValue(c, out bool h)) hasUnits[c] = h = gameState.Units.GetCountryUnitCount(c) > 0;
                return h;
            }
            var atWar = new Dictionary<(ushort, ushort), bool>();
            var pairs = new SortedSet<(ushort owner, ushort enemy)>();
            foreach (ushort p in landProvinces)
            {
                ushort a = Ctrl(p);
                if (a == 0) continue;
                foreach (ushort q in Neighbours(p))
                {
                    if (q < p) continue; // each border once
                    ushort b = Ctrl(q);
                    if (b == 0 || b == a) continue;
                    var key = a < b ? (a, b) : (b, a);
                    if (!atWar.TryGetValue(key, out bool war)) atWar[key] = war = diplomacy.IsAtWar(a, b);
                    if (!war) continue;
                    if (a != player && HasUnits(a)) pairs.Add((a, b));
                    if (b != player && HasUnits(b)) pairs.Add((b, a));
                }
            }
            var existing = new HashSet<(ushort, ushort)>();
            foreach (var plan in new List<Hoi4BattlePlan>(plans))
            {
                if (!plan.IsAI) continue;
                if (!pairs.Contains((plan.Owner, plan.Country))) { RemovePlan(plan); continue; } // peace, or no border left
                existing.Add((plan.Owner, plan.Country));
            }
            foreach (var (owner, enemy) in pairs)
            {
                if (existing.Contains((owner, enemy))) continue;
                var fronts = FrontsAgainst(owner, enemy);
                if (fronts.Count == 0) continue; // only impassable land along it
                var plan = new Hoi4BattlePlan { Id = nextId++, Owner = owner, IsAI = true, Country = enemy };
                SetFronts(plan, fronts);
                plans.Add(plan); // divisions join on this hour's AutoAssignAI
                Core.Recording.SessionRecorder.Note("plans", "created", owner, 0, 0, enemy, $"plan {plan.Id} (AI): fronts {FrontSizes(plan)}");
            }
        }

        /// <summary>
        /// Simulation: the owner's provinces facing the country, as separate fronts each in order along the
        /// line. A front province is owner land (not impassable) next to land the country controls. Two of
        /// them belong to the same front when they are neighbours or face the same enemy province (that
        /// bridges a gap such as a lake on our side, but only within one connected piece of our land), and
        /// face the same connected piece of the enemy's land; so an own pocket and an enemy pocket are each a
        /// front of their own. The order
        /// along a front runs from one end (the farthest province from the lowest id) by hop distance.
        /// Integer graph maths only, in id order, so every client gets the same fronts.
        /// </summary>
        List<List<ushort>> FrontsAgainst(ushort owner, ushort country)
        {
            var result = new List<List<ushort>>();
            if (owner == 0 || country == 0 || owner == country) return result;
            var front = new List<ushort>();
            foreach (ushort p in landProvinces)
            {
                if (Ctrl(p) != owner || Hoi4Regions.IsImpassable(p)) continue;
                foreach (ushort q in Neighbours(p))
                    if (Ctrl(q) == country) { front.Add(p); break; }
            }
            if (front.Count == 0) return result;

            // The enemy's land in connected pieces: a pocket of it inside our land is a front of its own, like
            // HOI4, even where the ring around it touches the main line. Each front province faces the smallest
            // piece it borders (ties: the lower piece id), so the pocket gets the corner provinces.
            var pieceSize = new List<int>();
            var piece = EnemyPieces(front, country, pieceSize);
            var faces = new int[front.Count];
            for (int i = 0; i < front.Count; i++) faces[i] = PieceFaced(front[i], piece, pieceSize);

            // Our own land in connected pieces (walkable: not impassable). Facing the same enemy province only
            // bridges a gap within one piece (a lake on our side); an encircled pocket of ours is its own front.
            var own = new Dictionary<ushort, int>();
            int ownPieces = 0;
            foreach (ushort p in front)
            {
                if (own.ContainsKey(p)) continue;
                int id = ownPieces++;
                var queue = new List<ushort> { p };
                own[p] = id;
                for (int k = 0; k < queue.Count; k++)
                    foreach (ushort r in Neighbours(queue[k]))
                        if (!own.ContainsKey(r) && Ctrl(r) == owner && !Hoi4Regions.IsImpassable(r)) { own[r] = id; queue.Add(r); }
            }

            // Links (within one enemy piece): own neighbours, and own provinces facing the same enemy province
            var index = new Dictionary<ushort, int>(front.Count);
            for (int i = 0; i < front.Count; i++) index[front[i]] = i;
            var links = new List<int>[front.Count];
            for (int i = 0; i < front.Count; i++) links[i] = new List<int>();
            var facing = new SortedDictionary<ushort, List<int>>();
            for (int i = 0; i < front.Count; i++)
                foreach (ushort q in Neighbours(front[i]))
                {
                    if (index.TryGetValue(q, out int j)) { if (j > i && faces[i] == faces[j]) { links[i].Add(j); links[j].Add(i); } }
                    else if (Ctrl(q) == country)
                    {
                        if (!facing.TryGetValue(q, out var list)) facing[q] = list = new List<int>();
                        list.Add(i);
                    }
                }
            foreach (var list in facing.Values)
                for (int a = 0; a < list.Count; a++)
                    for (int b = a + 1; b < list.Count; b++)
                        if (faces[list[a]] == faces[list[b]] && own[front[list[a]]] == own[front[list[b]]]
                            && !links[list[a]].Contains(list[b])) { links[list[a]].Add(list[b]); links[list[b]].Add(list[a]); }
            foreach (var l in links) l.Sort();

            // Components in order of their lowest province id (front is sorted, so the first unvisited)
            var dist = new int[front.Count];
            var seen = new bool[front.Count];
            for (int s = 0; s < front.Count; s++)
            {
                if (seen[s]) continue;
                var members = Bfs(s, links, dist);
                foreach (int m in members) seen[m] = true;
                int end = Farthest(members, dist);            // one end of the front
                Bfs(end, links, dist);
                members.Sort((x, y) => dist[x] != dist[y] ? dist[x].CompareTo(dist[y]) : x.CompareTo(y));
                var ordered = new List<ushort>(members.Count);
                foreach (int m in members) ordered.Add(front[m]);
                result.Add(ordered);
            }
            return result;
        }

        /// <summary>The fronts grouped into stretches of border: fronts whose provinces touch (a pocket at the main
        /// line) are one stretch; a third country in between (Luxembourg) splits them. Front indices, in order of the
        /// lowest front index.</summary>
        List<List<int>> Stretches(List<List<ushort>> fronts)
        {
            var frontOf = new Dictionary<ushort, int>();
            for (int f = 0; f < fronts.Count; f++) foreach (ushort p in fronts[f]) frontOf[p] = f;
            var root = new int[fronts.Count];
            for (int f = 0; f < root.Length; f++) root[f] = f;
            int Find(int x) { while (root[x] != x) x = root[x] = root[root[x]]; return x; }
            for (int f = 0; f < fronts.Count; f++)
                foreach (ushort p in fronts[f])
                    foreach (ushort n in Neighbours(p))
                        if (frontOf.TryGetValue(n, out int g))
                        {
                            int a = Find(f), b = Find(g);
                            if (a != b) root[Math.Max(a, b)] = Math.Min(a, b);
                        }
            var groups = new SortedDictionary<int, List<int>>();
            for (int f = 0; f < fronts.Count; f++)
            {
                int r = Find(f);
                if (!groups.TryGetValue(r, out var list)) groups[r] = list = new List<int>();
                list.Add(f);
            }
            return new List<List<int>>(groups.Values);
        }

        /// <summary>The stretch nearest the province (hops over land, ties: the lower province id), as its front
        /// provinces; null (the whole border) when no province is given or none is reachable.</summary>
        HashSet<ushort> StretchAt(List<List<ushort>> fronts, ushort province)
        {
            if (province == 0 || fronts.Count == 0) return null;
            var stretches = Stretches(fronts);
            var stretchOf = new Dictionary<ushort, int>();
            for (int s = 0; s < stretches.Count; s++)
                foreach (int f in stretches[s]) foreach (ushort p in fronts[f]) stretchOf[p] = s;
            ushort best = Nearest(province, stretchOf.ContainsKey);
            if (best == 0) return null;
            var set = new HashSet<ushort>();
            foreach (int f in stretches[stretchOf[best]]) set.UnionWith(fronts[f]);
            return set;
        }

        /// <summary>The province matching the test nearest the start (hops over land, ties: the lower id), 0 none.</summary>
        ushort Nearest(ushort start, Func<ushort, bool> match)
        {
            if (start == 0) return 0;
            var seen = new HashSet<ushort> { start };
            var level = new List<ushort> { start };
            while (level.Count > 0)
            {
                ushort best = 0;
                foreach (ushort p in level) if (match(p) && (best == 0 || p < best)) best = p;
                if (best != 0) return best;
                var next = new List<ushort>();
                foreach (ushort p in level)
                    foreach (ushort n in Neighbours(p))
                        if (seen.Add(n)) next.Add(n);
                level = next;
            }
            return 0;
        }

        /// <summary>
        /// A dragged line: the front provinces between the one nearest the start and the one nearest the end, on the
        /// stretch the start is on (an end beyond the stretch stops at its nearest province). Between = on a shortest
        /// walk along the front provinces (neighbours in the stretch), so a bulge the walk passes is kept. Where the
        /// walk is broken (a lake on our side) the whole stretch. Outside gets the stretch's other front provinces.
        /// </summary>
        HashSet<ushort> SectionBetween(List<List<ushort>> fronts, ushort start, ushort end, out HashSet<ushort> outside)
        {
            outside = null;
            var stretch = StretchAt(fronts, start);
            if (stretch == null) return null;
            ushort a = Nearest(start, stretch.Contains), b = Nearest(end, stretch.Contains);
            var da = WalkDistances(a, stretch);
            var db = WalkDistances(b, stretch);
            var section = new HashSet<ushort>();
            if (da.TryGetValue(b, out int length))
                foreach (var kv in da)
                    if (db.TryGetValue(kv.Key, out int d) && kv.Value + d <= length) section.Add(kv.Key);
            if (section.Count == 0) return stretch;
            outside = new HashSet<ushort>(stretch);
            outside.ExceptWith(section);
            return section;
        }

        /// <summary>Hops from the start to every province of the set reachable through neighbours within it.</summary>
        Dictionary<ushort, int> WalkDistances(ushort start, HashSet<ushort> within)
        {
            var dist = new Dictionary<ushort, int> { [start] = 0 };
            var queue = new Queue<ushort>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                ushort p = queue.Dequeue();
                foreach (ushort n in Neighbours(p))
                    if (within.Contains(n) && !dist.ContainsKey(n)) { dist[n] = dist[p] + 1; queue.Enqueue(n); }
            }
            return dist;
        }

        /// <summary>The fronts a plan holds: all of them for a whole-border plan, else the stretches that keep or
        /// touch its last provinces (the border moved by a province or two; two stretches that join become one).
        /// Updates the plan's section to them.</summary>
        List<List<ushort>> FrontsOf(Hoi4BattlePlan plan, List<List<ushort>> fronts)
        {
            if (plan.Section == null) return fronts;
            if (plan.Outside != null) return PartialFrontsOf(plan, fronts);
            var kept = new List<List<ushort>>();
            foreach (var stretch in Stretches(fronts))
            {
                bool touches = false;
                foreach (int f in stretch)
                {
                    foreach (ushort p in fronts[f])
                    {
                        if (plan.Section.Contains(p)) { touches = true; break; }
                        foreach (ushort n in Neighbours(p)) if (plan.Section.Contains(n)) { touches = true; break; }
                        if (touches) break;
                    }
                    if (touches) break;
                }
                if (touches) foreach (int f in stretch) kept.Add(fronts[f]);
            }
            if (kept.Count > 0)
            {
                plan.Section = new HashSet<ushort>();
                foreach (var f in kept) plan.Section.UnionWith(f);
            }
            return kept;
        }

        /// <summary>A dragged line follows the border without creeping along it: it keeps its front provinces that are
        /// still front provinces, and takes new ones next to them (land won ahead, or the line behind land lost)
        /// unless they were outside it before. Each front keeps its order, filtered.</summary>
        List<List<ushort>> PartialFrontsOf(Hoi4BattlePlan plan, List<List<ushort>> fronts)
        {
            var kept = new List<List<ushort>>();
            var section = new HashSet<ushort>();
            var outside = new HashSet<ushort>();
            foreach (var stretch in Stretches(fronts))
            {
                var mine = new List<List<ushort>>();
                foreach (int f in stretch)
                {
                    var list = new List<ushort>();
                    foreach (ushort p in fronts[f])
                    {
                        bool take = plan.Section.Contains(p);
                        if (!take && !plan.Outside.Contains(p))
                            foreach (ushort n in Neighbours(p)) if (plan.Section.Contains(n)) { take = true; break; }
                        if (take) list.Add(p);
                    }
                    if (list.Count > 0) mine.Add(list);
                }
                if (mine.Count == 0) continue;
                kept.AddRange(mine);
                foreach (var l in mine) section.UnionWith(l);
                foreach (int f in stretch) foreach (ushort p in fronts[f]) outside.Add(p);
            }
            if (kept.Count > 0)
            {
                outside.ExceptWith(section);
                plan.Section = section;
                plan.Outside = outside;
            }
            return kept;
        }

        /// <summary>An own province's arcs belong to the stretch when it is a front province of it, or an impassable
        /// province next to one (impassable land is never a front province but its border is still drawn).</summary>
        bool InSection(HashSet<ushort> section, ushort own)
        {
            if (section == null || section.Contains(own)) return true;
            if (!Hoi4Regions.IsImpassable(own)) return false;
            foreach (ushort n in Neighbours(own)) if (section.Contains(n)) return true;
            return false;
        }

        /// <summary>The country's land next to the given provinces, split into connected pieces (id per province,
        /// in the order the provinces list them; sizes filled per id).</summary>
        Dictionary<ushort, int> EnemyPieces(IEnumerable<ushort> along, ushort country, List<int> sizes)
        {
            var piece = new Dictionary<ushort, int>();
            foreach (ushort p in along)
                foreach (ushort q in Neighbours(p))
                {
                    if (Ctrl(q) != country || piece.ContainsKey(q)) continue;
                    int id = sizes.Count;
                    var queue = new List<ushort> { q };
                    piece[q] = id;
                    for (int k = 0; k < queue.Count; k++)
                        foreach (ushort r in Neighbours(queue[k]))
                            if (Ctrl(r) == country && !piece.ContainsKey(r)) { piece[r] = id; queue.Add(r); }
                    sizes.Add(queue.Count);
                }
            return piece;
        }

        /// <summary>The enemy piece a front province belongs with: the smallest it borders (ties: lower id), -1 none.</summary>
        int PieceFaced(ushort province, Dictionary<ushort, int> piece, List<int> sizes)
        {
            int best = -1;
            foreach (ushort q in Neighbours(province))
            {
                if (!piece.TryGetValue(q, out int id)) continue;
                if (best < 0 || sizes[id] < sizes[best] || (sizes[id] == sizes[best] && id < best)) best = id;
            }
            return best;
        }

        /// <summary>Breadth-first from start over the links: fills dist (hops) and returns the members reached.</summary>
        static List<int> Bfs(int start, List<int>[] links, int[] dist)
        {
            var members = new List<int> { start };
            var queue = new Queue<int>();
            var visited = new HashSet<int> { start };
            dist[start] = 0;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int c = queue.Dequeue();
                foreach (int n in links[c])
                    if (visited.Add(n))
                    {
                        dist[n] = dist[c] + 1;
                        members.Add(n);
                        queue.Enqueue(n);
                    }
            }
            return members;
        }

        static int Farthest(List<int> members, int[] dist)
        {
            int best = members[0];
            foreach (int m in members)
                if (dist[m] > dist[best] || (dist[m] == dist[best] && m < best)) best = m;
            return best;
        }

        static bool SameFronts(List<List<ushort>> a, List<List<ushort>> b)
        {
            if (b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].Count != b[i].Count) return false;
                for (int k = 0; k < a[i].Count; k++) if (a[i][k] != b[i][k]) return false;
            }
            return true;
        }

        static string FrontSizes(Hoi4BattlePlan plan)
        {
            if (plan.Fronts == null || plan.Fronts.Count == 0) return "none";
            var parts = new List<string>(plan.Fronts.Count);
            foreach (var f in plan.Fronts) parts.Add(f.Count.ToString());
            return string.Join("+", parts);
        }

        void SetFronts(Hoi4BattlePlan plan, List<List<ushort>> fronts)
        {
            plan.Fronts = fronts;
            plan.FrontProvinces = new List<ushort>();
            plan.FrontIndex.Clear();
            for (int f = 0; f < fronts.Count; f++)
            {
                plan.FrontProvinces.AddRange(fronts[f]);
                foreach (ushort p in fronts[f]) plan.FrontIndex[p] = f;
            }
            plan.Reach.Clear();
        }

        /// <summary>Presentation: the drawn line and its label from the traced border (player plans only).
        /// Empty when the border renderer exposes no arcs; the plan works the same without it.</summary>
        void TraceLine(Hoi4BattlePlan plan)
        {
            if (!ready) { plan.Arcs = new List<BorderArc>(); plan.Chains = new List<List<Vector3>>(); return; }
            plan.Arcs = BorderWith(plan.Owner, plan.Country, new HashSet<ushort>(), plan.Section);
            plan.Chains = new List<List<Vector3>>();
            plan.ChainFront.Clear();
            // Each front is drawn from its own arcs (the own-side province decides), so a pocket that touches the
            // main line at a corner stays a line of its own, with its own label and look, like HOI4
            // A corner province belongs to one front (the pocket's) but its arc towards the other enemy piece is
            // drawn with the front that faces that piece
            var sizes = new List<int>();
            var piece = EnemyPieces(plan.FrontProvinces ?? new List<ushort>(), plan.Country, sizes);
            var frontPiece = new int[plan.Fronts?.Count ?? 0];
            for (int f = 0; f < frontPiece.Length; f++)
                frontPiece[f] = plan.Fronts[f].Count > 0 ? PieceFaced(plan.Fronts[f][0], piece, sizes) : -1;
            var byFront = new SortedDictionary<int, List<BorderArc>>();
            foreach (var arc in plan.Arcs)
            {
                bool aOwn = Hoi4Occupation.ControllerOf(gameState, arc.ProvinceA) == plan.Owner;
                ushort own = aOwn ? arc.ProvinceA : arc.ProvinceB, enemy = aOwn ? arc.ProvinceB : arc.ProvinceA;
                int f = plan.FrontIndex.TryGetValue(own, out int fi) ? fi : -1; // -1: impassable, not part of a front
                if (f >= 0 && piece.TryGetValue(enemy, out int ep) && frontPiece[f] != ep)
                {
                    // the front facing that piece next to this enemy province (lowest province id first)
                    int g = -1;
                    foreach (ushort n in Neighbours(enemy))
                        if (plan.FrontIndex.TryGetValue(n, out int fn) && frontPiece[fn] == ep) { g = fn; break; }
                    if (g >= 0) f = g;
                }
                if (!byFront.TryGetValue(f, out var list)) byFront[f] = list = new List<BorderArc>();
                list.Add(arc);
            }
            foreach (var kv in byFront)
                foreach (var chain in BuildChains(kv.Value, plan.Country))
                {
                    plan.Chains.Add(chain);
                    plan.ChainFront.Add(kv.Key);
                }
            ComputeLabelAnchor(plan);
        }

        /// <summary>Provinces changed hands: every line follows the new border with its country and its
        /// divisions spread over the new front (those still on a front province stay put).</summary>
        void RefreshPlans()
        {
            borderDirty = false;
            // One snapshot of every land province's controller, shared by all plans this hour
            ctrl = new ushort[landNeighbours.Length];
            foreach (ushort p in landProvinces) ctrl[p] = Hoi4Occupation.ControllerOf(gameState, p);
            var changedCountries = new HashSet<ushort>(dirtyCountries);
            dirtyCountries.Clear();
            bool any = false;
            foreach (var plan in new List<Hoi4BattlePlan>(plans))
            {
                // Only a country that gained or lost land can move this line (all of them on the daily pass)
                if (changedCountries.Count > 0 && !changedCountries.Contains(plan.Owner) && !changedCountries.Contains(plan.Country)) continue;
                var fronts = FrontsOf(plan, FrontsAgainst(plan.Owner, plan.Country));
                if (fronts.Count == 0) { RemovePlan(plan); any |= !plan.IsAI; continue; } // nothing left to hold against
                if (SameFronts(fronts, plan.Fronts)) continue;                        // a change somewhere else on the map
                string before = FrontSizes(plan);
                SetFronts(plan, fronts);
                Core.Recording.SessionRecorder.Note("plans", "line_moved", plan.Owner, 0, 0, plan.Country, $"plan {plan.Id}: fronts {before} -> {FrontSizes(plan)}");
                if (!plan.IsAI) { TraceLine(plan); any = true; }
                Distribute(plan);
            }
            EnsureAIPlans();
            ctrl = null; // outside a refresh, read live controllers
            if (Active) SetPreview(hoverProvince);
            if (any)
            {
                dirtyMesh = true;
                Changed?.Invoke();
            }
        }

        void RebuildTexture()
        {
            if (!dirtyTex) return;
            dirtyTex = false;
            Array.Clear(planPixels, 0, planPixels.Length);
            bool show = Active && previewArcs != null;
            if (OffensiveActive)
            {
                PaintOffensiveTargets();
                show = offensiveTargets.Count > 0;
            }
            else if (show)
            {
                var green = (Color32)previewColor;
                foreach (ushort p in previewProvinces)
                    planPixels[p] = new Color32(green.r, green.g, green.b, 128);
                if (hoverProvince != 0 && hoverProvince < planPixels.Length)
                    planPixels[hoverProvince] = new Color32(green.r, green.g, green.b, 255);
            }
            planTex.SetPixelData(planPixels, 0);
            planTex.Apply(false);
            Shader.SetGlobalFloat(PlanActiveId, show ? 1f : 0f);
        }

        // --- Mesh --------------------------------------------------------------------------------

        void LateUpdate()
        {
            if (!ready) return;
            if (dirtyMesh) { dirtyMesh = false; Rebuild(); }
            if (mesh.vertexCount > 0)
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);
            DrawOffensiveArrows();
        }

        Vector3 PixelToWorld(Vector2 px) =>
            new Vector3(bounds.x + px.x * worldPerPxX, 0f, bounds.y + (mapHeightPx - px.y) * worldPerPxZ);

        // --- Hover (presentation): the plan under the cursor is drawn green ----------------------------

        Hoi4BattlePlan hoverPlan;
        int hoverFront = -1;
        Hoi4Offensive hoverOffensive;

        /// <summary>
        /// The plan part under the cursor, like HOI4: an offensive (its arrow or its objective line; both light up)
        /// before a front line (the stretch under the cursor). Picked within the drawn width, at least hoverMinPx
        /// on screen. Not while a tool is on or the cursor is over a panel.
        /// </summary>
        void UpdateHover()
        {
            Hoi4BattlePlan plan = null;
            int front = -1;
            Hoi4Offensive offensive = null;
            var cam = Camera.main;
            var selector = Engine.ArchonEngine.Instance.ProvinceSelector;
            if (!Active && !OffensiveActive && cam != null && selector != null
                && !global::Map.CameraControllers.BaseCameraController.ScrollCaptured
                && selector.TryGetMapPointAtScreenPosition(Input.mousePosition, cam, out var world, out _))
            {
                var q = new Vector2(world.x, world.z);
                float perScreenPx = 2f * Vector3.Distance(cam.transform.position, world) * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(Screen.height, 1);
                float minR = hoverMinPx * perScreenPx;
                float best = float.MaxValue;
                foreach (var o in offensives)
                {
                    float r = Mathf.Max(offensiveRibbonPx * worldPerPxX * 0.5f, minR);
                    float d = Mathf.Min(PolylineDistance(o.HitLine, q), PolylineDistance(o.Objective, q));
                    float ro = Mathf.Max(objectiveWidthPx * worldPerPxX * 0.75f, minR);
                    if ((d <= r || PolylineDistance(o.Objective, q) <= ro) && d < best) { best = d; offensive = o; }
                }
                if (offensive == null)
                {
                    float r = Mathf.Max(lineWidthPx * worldPerPxX * 0.75f, minR);
                    foreach (var p in plans)
                    {
                        if (p.IsAI || p.Chains == null) continue;
                        for (int c = 0; c < p.Chains.Count; c++)
                        {
                            float d = PolylineDistance(p.Chains[c], q);
                            if (d <= r && d < best) { best = d; plan = p; front = c < p.ChainFront.Count ? p.ChainFront[c] : -1; }
                        }
                    }
                }
            }
            if (plan == hoverPlan && front == hoverFront && offensive == hoverOffensive) return;
            hoverPlan = plan; hoverFront = front; hoverOffensive = offensive;
            dirtyMesh = true;
        }

        /// <summary>Distance in the map plane (world xz) from a point to a polyline; MaxValue when there is none.</summary>
        static float PolylineDistance(List<Vector3> pts, Vector2 q)
        {
            if (pts == null || pts.Count == 0) return float.MaxValue;
            float best = float.MaxValue;
            var a = new Vector2(pts[0].x, pts[0].z);
            if (pts.Count == 1) return Vector2.Distance(a, q);
            for (int i = 1; i < pts.Count; i++)
            {
                var b = new Vector2(pts[i].x, pts[i].z);
                var ab = b - a;
                float t = ab.sqrMagnitude > 1e-12f ? Mathf.Clamp01(Vector2.Dot(q - a, ab) / ab.sqrMagnitude) : 0f;
                float d = (a + ab * t - q).sqrMagnitude;
                if (d < best) best = d;
                a = b;
            }
            return Mathf.Sqrt(best);
        }

        void Rebuild()
        {
            verts.Clear(); colors.Clear(); uvs.Clear(); styles.Clear(); centers.Clear(); tris.Clear();
            if (Active && previewChains != null)
            {
                if (lineDrag) AddLineDragPreview();
                else
                    foreach (var chain in previewChains)
                        AddRibbon(chain, previewColor, 0f);
            }
            foreach (var plan in plans)
            {
                if (plan.IsAI) continue; // AI lines are not drawn
                var color = Hoi4Armies.ColorOf(plan.ArmyId, 0.95f);
                // A front nobody is slotted on: HOI4's unassigned line (separate teeth)
                var counts = plan.DivisionsPerFront();
                for (int c = 0; c < plan.Chains.Count; c++)
                {
                    int f = c < plan.ChainFront.Count ? plan.ChainFront[c] : -1;
                    bool held = f < 0 || f >= counts.Length || counts[f] > 0;
                    bool hot = plan == hoverPlan && f == hoverFront;
                    AddRibbon(plan.Chains[c], hot ? hoverColor : color, held ? 1f : 5f);
                }
            }
            RebuildOffensives();

            mesh.Clear();
            if (verts.Count == 0) return;
            mesh.SetVertices(verts);
            mesh.SetColors(colors);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, styles);
            mesh.SetUVs(2, centers);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f); // heights come from the shader
        }

        // --- Holding the line ----------------------------------------------------------------------

        /// <summary>Take a division off its plan (a manual order overrides the plan).</summary>
        public void Unassign(ushort unitId)
        {
            if (planOf.TryGetValue(unitId, out var p) && p.IsAI) return; // the player's orders, the player's plans
            if (!manualOrders.ContainsKey(unitId)) manualOrders[unitId] = p?.Id ?? 0; // a new order keeps the plan it first left
            if (!planOf.TryGetValue(unitId, out var plan)) return;
            planOf.Remove(unitId);
            plan.Units.Remove(unitId);
            plan.Slot.Remove(unitId);
            Distribute(plan);
            Changed?.Invoke();
        }

        /// <summary>
        /// Spread the plan's divisions evenly over its front provinces, like HOI4: with fewer divisions than
        /// provinces they take evenly spaced provinces along the line, with more every province gets its
        /// share and the rest are spaced out the same way. Each slot then goes to the nearest free division
        /// (greedy on distance), and divisions not already there march.
        /// </summary>
        void Distribute(Hoi4BattlePlan plan)
        {
            if (!plan.IsAI) dirtyMesh = true; // a front may have gained or lost all its divisions (unassigned look)
            plan.Slot.Clear();
            var fronts = plan.Fronts;
            if (fronts == null || fronts.Count == 0 || plan.Units.Count == 0) return;
            var units = gameState.Units;
            var ids = new List<ushort>();
            foreach (ushort u in plan.Units) if (units.HasUnit(u)) ids.Add(u);
            ids.Sort(); // deterministic order
            if (ids.Count == 0) return;

            // Which fronts each division can reach by land (a colonial front across the sea can't be walked to)
            var reachCache = plan.Reach;
            bool Reaches(ushort u, int f)
            {
                var unit = units.GetUnit(u);
                var key = (unit.provinceID, f);
                if (reachCache.TryGetValue(key, out bool ok)) return ok;
                ok = fronts[f].Contains(unit.provinceID);
                var pathfinding = gameState.Pathfinding;
                if (!ok && pathfinding != null && pathfinding.IsInitialized)
                {
                    var path = FriendlyPath(unit.provinceID, fronts[f][0], unit.countryID).Path;
                    ok = path != null && path.Count >= 2;
                }
                reachCache[key] = ok;
                return ok;
            }
            var reachers = new int[fronts.Count];
            foreach (ushort u in ids)
                for (int f = 0; f < fronts.Count; f++)
                    if (Reaches(u, f)) reachers[f]++;

            // Divisions per front: every reachable front (an encircled pocket too) gets one while there are
            // enough, the rest go by front length (D'Hondt: the next one to the front with the most provinces
            // per division). A front never gets more divisions than can reach it.
            var alloc = new int[fronts.Count];
            var order = new List<int>();
            for (int i = 0; i < fronts.Count; i++) if (reachers[i] > 0) order.Add(i);
            order.Sort((x, y) => fronts[y].Count != fronts[x].Count ? fronts[y].Count.CompareTo(fronts[x].Count) : x.CompareTo(y));
            int left = ids.Count;
            foreach (int i in order) { if (left == 0) break; alloc[i] = 1; left--; }
            while (left > 0)
            {
                int best = -1;
                foreach (int i in order)
                {
                    if (alloc[i] >= reachers[i]) continue;
                    // Most provinces per division after one more: count_i/(alloc_i+1) > count_best/(alloc_best+1)
                    if (best < 0 || (long)fronts[i].Count * (alloc[best] + 1) > (long)fronts[best].Count * (alloc[i] + 1)) best = i;
                }
                if (best < 0) break;
                alloc[best]++;
                left--;
            }

            // Within a front: evenly spaced provinces along it, every province its share when there are more
            var slots = new List<(ushort province, int front)>(ids.Count);
            for (int f = 0; f < fronts.Count; f++)
            {
                var front = fronts[f];
                int m = alloc[f], p = front.Count;
                if (m == 0 || p == 0) continue;
                int each = m / p, extra = m % p;
                for (int i = 0; i < p; i++)
                    for (int k = 0; k < each; k++) slots.Add((front[i], f));
                for (int k = 0; k < extra; k++)
                    slots.Add((front[Math.Min((2 * k + 1) * p / (2 * extra), p - 1)], f)); // (k + 0.5) * p / extra
            }

            // Each slot to the nearest free division that can reach it (greedy on land hops)
            var slotProvinces = new HashSet<ushort>();
            foreach (var s in slots) slotProvinces.Add(s.province);
            var hopsFrom = new Dictionary<ushort, Dictionary<ushort, int>>();
            var pairs = new List<(int d, ushort unit, int slot)>(ids.Count * slots.Count);
            foreach (ushort u in ids)
            {
                // A division already marching counts from where it is going, so a redistribution keeps it on course
                ushort heading = HeadingTo(u);
                ushort at = heading != 0 ? heading : units.GetUnit(u).provinceID;
                if (!hopsFrom.TryGetValue(at, out var hops)) hopsFrom[at] = hops = Hops(at, slotProvinces);
                for (int s = 0; s < slots.Count; s++)
                {
                    if (!Reaches(u, slots[s].front)) continue;
                    pairs.Add((hops.TryGetValue(slots[s].province, out int d) ? d : int.MaxValue, u, s));
                }
            }
            pairs.Sort((x, y) => x.d != y.d ? x.d.CompareTo(y.d) : x.unit != y.unit ? x.unit.CompareTo(y.unit) : x.slot.CompareTo(y.slot));
            var taken = new bool[slots.Count];
            foreach (var (d, u, s) in pairs)
            {
                if (taken[s] || plan.Slot.ContainsKey(u)) continue;
                taken[s] = true;
                plan.Slot[u] = slots[s].province;
            }
            // Left over (the fronts they can reach are full): double up on the nearest slot they can reach
            foreach (var (d, u, s) in pairs)
                if (!plan.Slot.ContainsKey(u)) plan.Slot[u] = slots[s].province;
            if (Core.Recording.SessionRecorder.IsRecording)
            {
                var parts = new List<string>(fronts.Count);
                for (int f = 0; f < fronts.Count; f++) parts.Add($"{fronts[f].Count}p:{alloc[f]}/{reachers[f]}");
                Core.Recording.SessionRecorder.Note("plans", "distribute", plan.Owner, 0, 0, plan.Country,
                    $"plan {plan.Id}: {ids.Count} divisions, {plan.Slot.Count} slotted; per front provinces:allotted/can_reach {string.Join(" ", parts)}");
            }
            foreach (ushort u in ids)
                if (!plan.IsAI && !plan.Slot.ContainsKey(u))
                {
                    var unit = units.GetUnit(u);
                    if (fronts.Count > 0 && fronts[0].Count > 0) OrderMove(u, unit, fronts[0][0], dryRun: true); // logs why there is no path
                    // Once per division and province: plans spread their divisions again every time the line moves
                    if (!noPathWarned.Add(((uint)u << 16 | unit.provinceID, 0))) continue;
                    ArchonLogger.LogWarning($"Hoi4BattlePlans: plan {plan.Id}: unit {u} in province {unit.provinceID} can reach none of the {fronts.Count} fronts", "game_units");
                    Core.Recording.SessionRecorder.Note("plans", "unreachable", plan.Owner, u, unit.provinceID, 0, $"plan {plan.Id}: can reach none of the {fronts.Count} fronts");
                }
            Hold(plan, force: true);
        }

        /// <summary>Land hops from a province to each of the targets (breadth-first, stops when all are found
        /// or after MaxHops). Missing = farther than that.</summary>
        Dictionary<ushort, int> Hops(ushort from, HashSet<ushort> targets)
        {
            const int MaxHops = 64;
            var found = new Dictionary<ushort, int>();
            var dist = new Dictionary<ushort, int> { [from] = 0 };
            var queue = new Queue<ushort>();
            queue.Enqueue(from);
            if (targets.Contains(from)) found[from] = 0;
            while (queue.Count > 0 && found.Count < targets.Count)
            {
                ushort c = queue.Dequeue();
                int d = dist[c];
                if (d >= MaxHops) break;
                foreach (ushort n in Neighbours(c))
                {
                    if (dist.ContainsKey(n)) continue;
                    dist[n] = d + 1;
                    if (targets.Contains(n)) found[n] = d + 1;
                    queue.Enqueue(n);
                }
            }
            return found;
        }

        /// <summary>Send every division of the plan that is idle and away from its province back to it.</summary>
        void Hold(Hoi4BattlePlan plan, bool force = false)
        {
            var units = gameState.Units;
            var queue = units.MovementQueue;
            int ordered = 0, holding = 0, marching = 0, noPath = 0;
            foreach (var kv in plan.Slot)
            {
                if (!units.HasUnit(kv.Key)) continue;
                var unit = units.GetUnit(kv.Key);
                if (IsPlanAttack(kv.Key, unit)) { marching++; continue; } // attacking from the line: leave it be
                bool moving = queue.IsUnitMoving(kv.Key);
                if (moving && Hoi4Combat.IsRetreating(kv.Key)) { marching++; continue; } // falling back: back to the line once it stops
                if (unit.provinceID == kv.Value)
                {
                    if (force && moving) queue.CancelMovement(kv.Key);
                    holding++;
                    continue;
                }
                // Re-ordering restarts the current hop, so leave a march to the right place alone
                if (moving && (!force || HeadingTo(kv.Key) == kv.Value)) { marching++; continue; }
                if (OrderMove(kv.Key, unit, kv.Value, log: !plan.IsAI)) ordered++;
                else noPath++;
            }
            if (ordered > 0) Hoi4UnitCounters.Instance?.MarkDirty();
            if (!plan.IsAI && (force || ordered > 0))
                ArchonLogger.Log($"Hoi4BattlePlans: plan {plan.Id} ({plan.Units.Count} divisions, {plan.FrontProvinces?.Count ?? 0} front provinces in {plan.Fronts?.Count ?? 0} fronts): " +
                                 $"{holding} holding, {marching} marching, {ordered} ordered, {noPath} without a path", "game_units");
        }

        /// <summary>Final province of the division's current march, 0 when it isn't moving.</summary>
        ushort HeadingTo(ushort unitId)
        {
            var queue = gameState.Units.MovementQueue;
            if (!queue.IsUnitMoving(unitId)) return 0;
            var path = queue.GetFullPath(unitId);
            if (path != null && path.Count > 0) return path[path.Count - 1];
            return queue.TryGetMovementState(unitId, out var s) ? s.destinationProvinceID : (ushort)0;
        }

        readonly HashSet<(uint, ushort)> noPathWarned = new HashSet<(uint, ushort)>();

        bool OrderMove(ushort unitId, Core.Units.UnitState unit, ushort target, bool dryRun = false, bool log = true)
        {
            var pathfinding = gameState.Pathfinding;
            if (pathfinding == null || !pathfinding.IsInitialized) return false;
            var result = FriendlyPath(unit.provinceID, target, unit.countryID);
            var path = result.Path;
            if (path == null || path.Count < 2)
            {
                if (!dryRun) Core.Recording.SessionRecorder.Note("plans", "no_path", unit.countryID, unitId, unit.provinceID, target, $"{result.Status}, {result.NodesExplored} explored");
                // Diagnostics: why a front division can't march (owner of both ends, search status). Once per
                // division, start and target: a front out of reach (across the sea) would otherwise warn every hour.
                if (!log || !noPathWarned.Add(((uint)unitId << 16 | unit.provinceID, target))) return false;
                var provinces = gameState.Provinces;
                ArchonLogger.LogWarning($"Hoi4BattlePlans: no path for unit {unitId} (type {unit.unitTypeID}, country {unit.countryID}) " +
                    $"from {unit.provinceID} (owner {provinces.GetProvinceOwner(unit.provinceID)}, water {Hoi4Regions.IsWater(unit.provinceID)}) " +
                    $"to {target} (owner {provinces.GetProvinceOwner(target)}, controller {Hoi4Occupation.ControllerOf(gameState, target)}): " +
                    $"{result.Status}, {result.NodesExplored} explored", "game_units");
                return false;
            }
            if (dryRun) return true;
            // Not a command: holding the line is simulation that every client runs the same way (lockstep).
            // Only decisions (placing a plan, a manual order, an AI attack) are commands; sending these
            // moves as commands too would apply them twice on the other clients and in a replay.
            var type = StarterKit.Initializer.Instance?.UnitSystem?.GetUnitType(unit.unitTypeID);
            var queue = gameState.Units.MovementQueue;
            if (queue.IsUnitMoving(unitId)) queue.CancelMovement(unitId); // a new march replaces the old one
            queue.StartMovement(unitId, path[1], type?.Speed ?? 3, new List<ushort>(path));
            return true;
        }

        /// <summary>
        /// A path that never enters land held by a country at war with this one. Holding the line is not
        /// attacking: a march through an enemy province is an attack there (Hoi4Combat), so a plan that
        /// routed a division to its spot that way had it attack every hour, at any organisation.
        /// </summary>
        Core.Systems.PathResult FriendlyPath(ushort from, ushort to, ushort country)
        {
            var options = Core.Systems.PathOptions.Default;
            options.Context = Core.Systems.PathContext.Create(country, 0);
            options.ForbiddenProvinces = EnemyLand(country);
            return gameState.Pathfinding.FindPathWithOptions(from, to, options);
        }

        /// <summary>Land controlled by countries at war with this one, once per hour per country.</summary>
        HashSet<ushort> EnemyLand(ushort country)
        {
            if (enemyLand.TryGetValue(country, out var set)) return set;
            set = new HashSet<ushort>();
            var diplomacy = gameState.Diplomacy;
            var war = new Dictionary<ushort, bool>();
            foreach (ushort p in landProvinces)
            {
                ushort c = Hoi4Occupation.ControllerOf(gameState, p);
                if (c == 0 || c == country) continue;
                if (!war.TryGetValue(c, out bool w)) war[c] = w = diplomacy != null && diplomacy.IsAtWar(country, c);
                if (w) set.Add(p);
            }
            enemyLand[country] = set;
            return set;
        }

        // --- Line geometry (prototype: Tools/prototypes/battleplan/battleplan_line.py) ---------------
        // Map pixel space (x right, y down) until the final conversion to world.

        static Vector2Int EndKey(Vector2 p) => new Vector2Int(Mathf.RoundToInt(p.x * 2f), Mathf.RoundToInt(p.y * 2f));

        /// <summary>The border between the player and the country as smooth polylines in world space, each
        /// running with the enemy on its left: arcs chained by shared endpoints regardless of direction,
        /// oriented by sampling the province ids on both sides, then resampled and relaxed.</summary>
        List<List<Vector3>> BuildChains(List<BorderArc> arcs, ushort country)
        {
            var byEnd = new Dictionary<Vector2Int, List<int>>();
            void AddEnd(Vector2Int k, int i)
            {
                if (!byEnd.TryGetValue(k, out var l)) byEnd[k] = l = new List<int>(2);
                l.Add(i);
            }
            for (int i = 0; i < arcs.Count; i++)
            {
                if (arcs[i].Points.Length < 2) continue;
                AddEnd(EndKey(arcs[i].Points[0]), i);
                AddEnd(EndKey(arcs[i].Points[arcs[i].Points.Length - 1]), i);
            }
            bool IsEnd(Vector2Int k) => byEnd.TryGetValue(k, out var l) && l.Count == 1;

            var used = new bool[arcs.Count];
            var result = new List<List<Vector3>>();
            // Open chains first (from an end used by one arc), then whatever is left (loops)
            for (int pass = 0; pass < 2; pass++)
                for (int s = 0; s < arcs.Count; s++)
                {
                    var sp = arcs[s].Points;
                    if (used[s] || sp.Length < 2) continue;
                    bool startsAtEnd = IsEnd(EndKey(sp[0])), endsAtEnd = IsEnd(EndKey(sp[sp.Length - 1]));
                    if (pass == 0 && !startsAtEnd && !endsAtEnd) continue;
                    used[s] = true;
                    var pts = new List<Vector2>(sp);
                    if (!startsAtEnd && endsAtEnd) pts.Reverse();
                    var enemy = new HashSet<ushort>();
                    var own = new HashSet<ushort>();
                    void Sides(BorderArc arc)
                    {
                        bool aEnemy = Hoi4Occupation.ControllerOf(gameState, arc.ProvinceA) == country;
                        enemy.Add(aEnemy ? arc.ProvinceA : arc.ProvinceB);
                        own.Add(aEnemy ? arc.ProvinceB : arc.ProvinceA);
                    }
                    Sides(arcs[s]);
                    for (int dir = 0; dir < 2; dir++) // grow from the end, then from the start
                    {
                        while (true)
                        {
                            var end = EndKey(pts[pts.Count - 1]);
                            int next = -1;
                            if (byEnd.TryGetValue(end, out var cands))
                                foreach (int c in cands) if (!used[c]) { next = c; break; }
                            if (next < 0) break;
                            used[next] = true;
                            Sides(arcs[next]);
                            var np = arcs[next].Points;
                            if (EndKey(np[0]) == end) for (int k = 1; k < np.Length; k++) pts.Add(np[k]);
                            else for (int k = np.Length - 2; k >= 0; k--) pts.Add(np[k]);
                        }
                        pts.Reverse();
                    }
                    if (!EnemyOnLeft(pts, enemy, own)) pts.Reverse();
                    var smooth = Relax(pts, 0.25f, 14);
                    var world = new List<Vector3>(smooth.Count);
                    foreach (var p in smooth) world.Add(PixelToWorld(p));
                    result.Add(world);
                }
            return result;
        }

        /// <summary>Every segment samples the province ids a little to each side; the enemy on the left
        /// (map space, y down: (t.y, -t.x)) votes yes.</summary>
        bool EnemyOnLeft(List<Vector2> pts, HashSet<ushort> enemy, HashSet<ushort> own)
        {
            var tm = Engine.ArchonEngine.Instance.TextureManager;
            int h = tm.MapHeight;
            int votes = 0;
            for (int m = 0; m < pts.Count - 1; m++)
            {
                var t = pts[m + 1] - pts[m];
                float len = t.magnitude;
                if (len < 1e-6f) continue;
                var n = new Vector2(t.y, -t.x) / len;
                var mid = (pts[m] + pts[m + 1]) * 0.5f;
                for (int di = 0; di < 3; di++)
                {
                    float d = di == 0 ? 0.7f : di == 1 ? 1.5f : 2.5f;
                    for (int sgn = 1; sgn >= -1; sgn -= 2)
                    {
                        var q = mid + n * (d * sgn);
                        // The lookup takes y from the bottom of the map
                        ushort id = tm.GetProvinceID(Mathf.FloorToInt(q.x), h - 1 - Mathf.FloorToInt(q.y));
                        if (enemy.Contains(id)) votes += sgn;
                        else if (own.Contains(id)) votes -= sgn;
                    }
                }
            }
            return votes >= 0;
        }

        /// <summary>Resample evenly, then relax (endpoints fixed): the border corners become gentle bends.</summary>
        static List<Vector2> Relax(List<Vector2> pts, float spacing, int passes)
        {
            float total = 0f;
            for (int i = 1; i < pts.Count; i++) total += Vector2.Distance(pts[i - 1], pts[i]);
            int n = Mathf.Max(Mathf.RoundToInt(total / spacing), 1);
            var res = new List<Vector2>(n + 1);
            float step = total / n;
            int seg = 0;
            float segStart = 0f;
            for (int k = 0; k <= n; k++)
            {
                float at = Mathf.Min(k * step, total);
                while (seg < pts.Count - 2 && segStart + Vector2.Distance(pts[seg], pts[seg + 1]) < at)
                {
                    segStart += Vector2.Distance(pts[seg], pts[seg + 1]);
                    seg++;
                }
                float sl = Vector2.Distance(pts[seg], pts[seg + 1]);
                res.Add(Vector2.Lerp(pts[seg], pts[seg + 1], sl > 1e-6f ? Mathf.Clamp01((at - segStart) / sl) : 0f));
            }
            if (res.Count < 3) return res;
            var tmp = new Vector2[res.Count];
            for (int p = 0; p < passes; p++)
            {
                tmp[0] = res[0];
                tmp[res.Count - 1] = res[res.Count - 1];
                for (int i = 1; i < res.Count - 1; i++) tmp[i] = (res[i - 1] + res[i] * 2f + res[i + 1]) * 0.25f;
                for (int i = 0; i < res.Count; i++) res[i] = tmp[i];
            }
            return res;
        }

        /// <summary>Ribbon along a chain: uv.x = distance in ribbon widths, uv.y = 0 own side .. 1 enemy side (left of travel).
        /// widthPx > 0 overrides the style's width.</summary>
        void AddRibbon(List<Vector3> pts, Color color, float style, float widthPx = -1f)
        {
            if (pts.Count < 2) return;
            float width = widthPx > 0f ? widthPx * worldPerPxX
                : style > 1.5f ? objectiveWidthPx * worldPerPxX
                : lineWidthPx * worldPerPxX * (style < 0.5f ? previewWidthScale : 1f);
            float half = width * 0.5f;
            float dist = 0f;
            int first = verts.Count;
            for (int i = 0; i < pts.Count; i++)
            {
                var t = pts[Mathf.Min(i + 1, pts.Count - 1)] - pts[Mathf.Max(i - 1, 0)];
                t.y = 0f;
                t = t.sqrMagnitude > 1e-14f ? t.normalized : Vector3.right;
                // World z points north (map y flipped), so the map-space left (t.y, -t.x) is (-t.z, t.x) in world xz
                var left = new Vector3(-t.z, 0f, t.x);
                if (i > 0) dist += Vector3.Distance(pts[i - 1], pts[i]);
                var p = pts[i];
                var center = new Vector2(p.x, p.z);
                for (int side = 0; side < 2; side++)
                {
                    verts.Add(p + left * (side == 0 ? -half : half));
                    colors.Add(color);
                    uvs.Add(new Vector2(dist / width, side));
                    styles.Add(new Vector2(style, 0f));
                    centers.Add(center);
                }
            }
            for (int i = 0; i < pts.Count - 1; i++)
            {
                int a = first + i * 2;
                tris.Add(a); tris.Add(a + 1); tris.Add(a + 2);
                tris.Add(a + 1); tris.Add(a + 3); tris.Add(a + 2);
            }
        }

        /// <summary>Anchor and direction for the plan's text: the middle of its longest arc.</summary>
        [Tooltip("Text along a line: how much of the line around the label is kept to bend the text along (map px each way)")]
        public float labelPathPx = 40f;

        /// <summary>The part of a line within labelPathPx (along it) of the point at distance 'at', with that point
        /// inserted (anchor) and every point lifted onto the terrain. Presentation.</summary>
        internal List<Vector3> LabelPath(List<Vector3> pts, float at, out int anchor)
        {
            var counters = Hoi4UnitCounters.Instance;
            float window = labelPathPx * worldPerPxX;
            var path = new List<Vector3>();
            anchor = 0;
            float run = 0f;
            bool placed = false;
            for (int i = 0; i < pts.Count; i++)
            {
                if (i > 0)
                {
                    float seg = Vector3.Distance(pts[i - 1], pts[i]);
                    if (!placed && run + seg >= at)
                    {
                        float f = seg > 1e-6f ? (at - run) / seg : 0f;
                        anchor = path.Count;
                        path.Add(Vector3.Lerp(pts[i - 1], pts[i], f));
                        placed = true;
                    }
                    run += seg;
                }
                if (Mathf.Abs(run - at) <= window) path.Add(pts[i]);
            }
            if (!placed) { anchor = path.Count; path.Add(pts[pts.Count - 1]); }
            if (counters != null)
                for (int i = 0; i < path.Count; i++)
                {
                    var p = path[i];
                    p.y = counters.TerrainHeight(p);
                    path[i] = p;
                }
            return path;
        }

        /// <summary>
        /// One label per front, like HOI4 (an encircled pocket is its own front with its own count): each drawn
        /// stretch belongs to the front it was traced for (ChainFront, TraceLine), and a front's label sits
        /// halfway along its longest stretch.
        /// </summary>
        void ComputeLabelAnchor(Hoi4BattlePlan plan)
        {
            plan.Labels.Clear();
            var counters = Hoi4UnitCounters.Instance;
            if (counters == null || plan.Chains == null) return;
            var longest = new SortedDictionary<int, (List<Vector3> pts, float len)>();
            for (int c = 0; c < plan.Chains.Count && c < plan.ChainFront.Count; c++)
            {
                var pts = plan.Chains[c];
                int front = plan.ChainFront[c];
                if (front < 0 || pts.Count < 2) continue;
                float len = 0f;
                for (int i = 1; i < pts.Count; i++) len += Vector3.Distance(pts[i - 1], pts[i]);
                if (!longest.TryGetValue(front, out var was) || len > was.len) longest[front] = (pts, len);
            }

            foreach (var kv in longest)
            {
                var pts = kv.Value.pts;
                float half = kv.Value.len * 0.5f, run = 0f;
                var label = new FrontLabel { Front = kv.Key, World = pts[0], Ahead = pts[1] };
                for (int i = 1; i < pts.Count; i++)
                {
                    float seg = Vector3.Distance(pts[i - 1], pts[i]);
                    if (run + seg >= half)
                    {
                        float f = seg > 1e-6f ? (half - run) / seg : 0f;
                        label.World = Vector3.Lerp(pts[i - 1], pts[i], f);
                        label.Ahead = pts[i];
                        if ((label.Ahead - label.World).sqrMagnitude < 1e-8f) label.Ahead = pts[Mathf.Min(i + 1, pts.Count - 1)];
                        break;
                    }
                    run += seg;
                }
                label.World.y = counters.TerrainHeight(label.World);
                label.Ahead.y = counters.TerrainHeight(label.Ahead);
                label.Path = LabelPath(pts, half, out label.Anchor);
                plan.Labels.Add(label);
            }
        }
    }
}
