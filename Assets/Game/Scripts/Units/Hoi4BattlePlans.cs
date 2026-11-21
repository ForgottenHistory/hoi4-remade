using System;
using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Systems;
using StarterKit.Commands;
using Hoi4.Map;
using Map.Rendering.Border;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hoi4.Units
{
    /// <summary>One battle plan: an offensive line along the border against a country, with the army that owns it.</summary>
    public sealed class Hoi4BattlePlan
    {
        public int Id;
        public int ArmyId;
        public ushort Country;                      // the country the line is drawn against
        public List<BorderArc> Arcs;                // the border arcs the line follows
        public List<List<Vector3>> Chains;          // the arcs joined into continuous polylines, enemy side on the left of travel
        public readonly HashSet<ushort> Units = new HashSet<ushort>();
        public List<ushort> FrontProvinces;         // own provinces along the line, in line order (all fronts)
        public List<List<ushort>> Fronts;           // the same per separate stretch of the line (a pocket is its own front)
        public readonly Dictionary<ushort, ushort> Slot = new Dictionary<ushort, ushort>(); // unit -> the front province it holds
        public Vector3 LabelWorld, LabelAhead;      // text anchor on the line and a point a little further along it
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
    /// a plan whose country no longer borders the player is removed.
    /// Lines are ribbons built from the engine's traced border arcs (IBorderArcSource), drawn by Hoi4/PlanLine.
    /// The province stripes come from a province-indexed texture (_Hoi4PlanTex) read in Hoi4MapModes.hlsl.
    /// </summary>
    public sealed class Hoi4BattlePlans : MonoBehaviour
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
        readonly HashSet<ushort> manualOrders = new HashSet<ushort>(); // taken off a plan by a manual order: not auto-assigned again
        IDisposable[] subscriptions;
        bool borderDirty;

        // Hover preview
        ushort hoverProvince, hoverCountry;
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

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (Instance != null) return;
            var go = new GameObject("Hoi4BattlePlans");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4BattlePlans>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Shader.SetGlobalFloat(PlanActiveId, 0f);
            if (subscriptions != null)
                foreach (var s in subscriptions) s?.Dispose();
            if (gameState?.Time != null) gameState.Time.OnHourlyTick -= OnHour;
            if (material != null) Destroy(material);
            if (mesh != null) Destroy(mesh);
            if (planTex != null) Destroy(planTex);
        }

        IEnumerator Start()
        {
            while (Hoi4UnitSelection.Instance == null || Hoi4UnitCounters.Instance == null || !Hoi4UnitCounters.Instance.IsReady)
                yield return null;
            var engine = Engine.ArchonEngine.Instance;
            gameState = engine.GameState;

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
            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<ProvinceControllerChangedEvent>(_ => borderDirty = true),
                gameState.EventBus.Subscribe<ProvinceOwnershipChangedEvent>(_ => borderDirty = true),
            };
            gameState.Time.OnHourlyTick += OnHour;
            ready = true;
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
            Active = true;
            hoverProvince = 0;
            SetPreview(0);
            Changed?.Invoke();
        }

        public void Cancel()
        {
            if (!Active) return;
            Active = false;
            SetPreview(0);
            Changed?.Invoke();
        }

        /// <summary>Remove the plans the selected divisions follow.</summary>
        public void DeleteSelectedPlans()
        {
            bool any = false;
            foreach (ushort id in Hoi4UnitSelection.Instance.Selected)
            {
                var plan = PlanOf(id);
                if (plan == null) continue;
                RemovePlan(plan);
                any = true;
            }
            if (any) { dirtyMesh = true; Changed?.Invoke(); }
        }

        void RemovePlan(Hoi4BattlePlan plan)
        {
            plans.Remove(plan);
            foreach (ushort u in plan.Units) planOf.Remove(u);
            plan.Units.Clear();
            plan.Slot.Clear();
        }

        /// <summary>Selection click interceptor while the tool is on. Always consumes the click.</summary>
        public bool OnClick(int button, Vector2 screen)
        {
            if (!Active) return false;
            if (button == 0 && previewArcs != null && previewArcs.Count > 0) Place();
            else if (button == 1) Cancel();
            return true;
        }

        void Place()
        {
            var selected = ArmySelection(out var army);
            if (selected == null) { Cancel(); return; }
            var plan = new Hoi4BattlePlan { Id = nextId++, ArmyId = army.Id, Country = hoverCountry, Arcs = previewArcs, Chains = previewChains };
            foreach (ushort u in selected)
            {
                manualOrders.Remove(u);
                if (!planOf.ContainsKey(u)) { plan.Units.Add(u); planOf[u] = plan; }
            }
            ComputeLabelAnchor(plan);
            SetFronts(plan, previewProvinces);
            plans.Add(plan);
            Distribute(plan);
            Active = false;
            SetPreview(0);
            dirtyMesh = true;
            Changed?.Invoke();
        }

        // --- Hover -------------------------------------------------------------------------------

        void Update()
        {
            if (!ready) return;
            if (dirtyMesh) { dirtyMesh = false; Rebuild(); }
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
        }

        /// <summary>
        /// Game time, not frames, drives the plans so they behave the same at any speed: every hour the lines
        /// follow border changes from that hour (several provinces often flip at once), divisions that died
        /// or left the army drop off, new army divisions join, and idle divisions return to their spots.
        /// </summary>
        void OnHour(int hour)
        {
            if (!ready) return;
            if (borderDirty) RefreshPlans();
            if (plans.Count == 0) return;
            var units = gameState.Units;
            var armies = Hoi4Armies.Instance;
            manualOrders.RemoveWhere(u => !units.HasUnit(u) || armies == null || armies.ArmyOf(u) == null);
            bool changed = false;
            var touched = new HashSet<Hoi4BattlePlan>();
            foreach (var plan in plans)
            {
                int removed = plan.Units.RemoveWhere(u =>
                {
                    // Dead, or moved to another army (or out of armies): off this plan
                    if (units.HasUnit(u) && armies != null && armies.ArmyOf(u)?.Id == plan.ArmyId) return false;
                    planOf.Remove(u);
                    plan.Slot.Remove(u);
                    return true;
                });
                if (removed > 0) touched.Add(plan);
            }
            if (AutoAssign(touched)) changed = true;
            foreach (var plan in plans)
                if (touched.Contains(plan)) Distribute(plan);
                else Hold(plan);
            if (changed || touched.Count > 0) Changed?.Invoke();
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
                    if (planOf.ContainsKey(u) || manualOrders.Contains(u)) continue;
                    Hoi4BattlePlan best = null;
                    float bestLoad = float.MaxValue;
                    foreach (var plan in plans)
                    {
                        if (plan.ArmyId != army.Id) continue;
                        float load = plan.Units.Count / (float)Mathf.Max(plan.FrontProvinces?.Count ?? 1, 1);
                        if (load < bestLoad) { bestLoad = load; best = plan; }
                    }
                    if (best == null) break; // this army has no plan
                    best.Units.Add(u);
                    planOf[u] = best;
                    touched.Add(best);
                    any = true;
                }
            return any;
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

            var arcs = BorderWith(country, previewProvinces);
            if (arcs.Count == 0) { previewProvinces.Clear(); return; }
            hoverCountry = country;
            previewArcs = arcs;
            previewChains = BuildChains(arcs, country);
        }

        /// <summary>The arcs between provinces the player controls and provinces the country controls; the
        /// player's provinces along them go into ownSide.</summary>
        List<BorderArc> BorderWith(ushort country, HashSet<ushort> ownSide)
        {
            ushort player = Player;
            var arcs = new List<BorderArc>();
            if (player == 0 || !EnsureArcs()) return arcs;
            foreach (var arc in allArcs)
            {
                ushort a = Hoi4Occupation.ControllerOf(gameState, arc.ProvinceA);
                ushort b = Hoi4Occupation.ControllerOf(gameState, arc.ProvinceB);
                if (a == player && b == country) ownSide.Add(arc.ProvinceA);
                else if (b == player && a == country) ownSide.Add(arc.ProvinceB);
                else continue;
                arcs.Add(arc);
            }
            return arcs;
        }

        /// <summary>Provinces changed hands: every line follows the new border with its country and its
        /// divisions spread over the new front (those still on a front province stay put).</summary>
        void RefreshPlans()
        {
            borderDirty = false;
            bool any = plans.Count > 0;
            foreach (var plan in new List<Hoi4BattlePlan>(plans))
            {
                var own = new HashSet<ushort>();
                var arcs = BorderWith(plan.Country, own);
                if (arcs.Count == 0) { RemovePlan(plan); continue; } // nothing left to hold against
                if (SameArcs(arcs, plan.Arcs)) continue;              // a change somewhere else on the map
                plan.Arcs = arcs;
                plan.Chains = BuildChains(arcs, plan.Country);
                SetFronts(plan, own);
                ComputeLabelAnchor(plan);
                Distribute(plan);
            }
            if (Active) SetPreview(hoverProvince);
            if (any)
            {
                dirtyMesh = true;
                Changed?.Invoke();
            }
        }

        static bool SameArcs(List<BorderArc> a, List<BorderArc> b)
        {
            if (b == null || a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) if (!ReferenceEquals(a[i], b[i])) return false; // same order: both walk allArcs
            return true;
        }

        void RebuildTexture()
        {
            if (!dirtyTex) return;
            dirtyTex = false;
            Array.Clear(planPixels, 0, planPixels.Length);
            bool show = Active && previewArcs != null;
            if (show)
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
        }

        Vector3 PixelToWorld(Vector2 px) =>
            new Vector3(bounds.x + px.x * worldPerPxX, 0f, bounds.y + (mapHeightPx - px.y) * worldPerPxZ);

        void Rebuild()
        {
            verts.Clear(); colors.Clear(); uvs.Clear(); styles.Clear(); centers.Clear(); tris.Clear();
            if (Active && previewChains != null)
                foreach (var chain in previewChains)
                    AddRibbon(chain, previewColor, 0f);
            foreach (var plan in plans)
            {
                var color = Hoi4Armies.ColorOf(plan.ArmyId, 0.95f);
                foreach (var chain in plan.Chains)
                    AddRibbon(chain, color, 1f);
            }

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
            manualOrders.Add(unitId);
            if (!planOf.TryGetValue(unitId, out var plan)) return;
            planOf.Remove(unitId);
            plan.Units.Remove(unitId);
            plan.Slot.Remove(unitId);
            Distribute(plan);
            Changed?.Invoke();
        }

        /// <summary>The own provinces along the line, grouped by the stretch of line (chain) they are closest to
        /// and sorted along it. An encirclement splits the border into several chains (the pocket is a loop),
        /// so each becomes its own front. Impassable provinces are left out.</summary>
        void SetFronts(Hoi4BattlePlan plan, IEnumerable<ushort> provinces)
        {
            var chains = plan.Chains;
            var keyed = new List<(int chain, float run, ushort p)>();
            var counters = Hoi4UnitCounters.Instance;
            foreach (ushort p in provinces)
            {
                if (Hoi4Regions.IsImpassable(p) || Hoi4Regions.IsWater(p)) continue;
                int chain = 0;
                float at = 0f;
                if (counters != null && counters.TryGetProvincePoint(p, out var c))
                {
                    float best = float.MaxValue;
                    for (int ci = 0; ci < chains.Count; ci++)
                    {
                        float run = 0f;
                        var pts = chains[ci];
                        for (int i = 0; i < pts.Count; i++)
                        {
                            if (i > 0) run += Vector3.Distance(pts[i - 1], pts[i]);
                            float dx = pts[i].x - c.x, dz = pts[i].z - c.z;
                            float d = dx * dx + dz * dz;
                            if (d < best) { best = d; chain = ci; at = run; }
                        }
                    }
                }
                keyed.Add((chain, at, p));
            }
            keyed.Sort((x, y) => x.chain != y.chain ? x.chain.CompareTo(y.chain) : x.run.CompareTo(y.run));
            plan.FrontProvinces = new List<ushort>(keyed.Count);
            plan.Fronts = new List<List<ushort>>();
            int last = -1;
            foreach (var k in keyed)
            {
                if (k.chain != last) { plan.Fronts.Add(new List<ushort>()); last = k.chain; }
                plan.Fronts[plan.Fronts.Count - 1].Add(k.p);
                plan.FrontProvinces.Add(k.p);
            }
        }

        /// <summary>
        /// Spread the plan's divisions evenly over its front provinces, like HOI4: with fewer divisions than
        /// provinces they take evenly spaced provinces along the line, with more every province gets its
        /// share and the rest are spaced out the same way. Each slot then goes to the nearest free division
        /// (greedy on distance), and divisions not already there march.
        /// </summary>
        void Distribute(Hoi4BattlePlan plan)
        {
            plan.Slot.Clear();
            var fronts = plan.Fronts;
            if (fronts == null || fronts.Count == 0 || plan.Units.Count == 0) return;
            var units = gameState.Units;
            var ids = new List<ushort>();
            foreach (ushort u in plan.Units) if (units.HasUnit(u)) ids.Add(u);
            ids.Sort(); // deterministic order
            if (ids.Count == 0) return;

            // Which fronts each division can reach by land (a colonial front across the sea can't be walked to)
            var reachCache = new Dictionary<(ushort, int), bool>();
            bool Reaches(ushort u, int f)
            {
                var unit = units.GetUnit(u);
                var key = (unit.provinceID, f);
                if (reachCache.TryGetValue(key, out bool ok)) return ok;
                ok = fronts[f].Contains(unit.provinceID);
                var pathfinding = gameState.Pathfinding;
                if (!ok && pathfinding != null && pathfinding.IsInitialized)
                {
                    var path = pathfinding.FindPath(unit.provinceID, fronts[f][0], unit.countryID);
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
                float bestScore = -1f;
                foreach (int i in order)
                {
                    if (alloc[i] >= reachers[i]) continue;
                    float score = fronts[i].Count / (float)(alloc[i] + 1);
                    if (score > bestScore) { bestScore = score; best = i; }
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
                    slots.Add((front[Mathf.Min((int)((k + 0.5f) * p / extra), p - 1)], f));
            }

            // Each slot to the nearest free division that can reach it (greedy on distance)
            var counters = Hoi4UnitCounters.Instance;
            var pairs = new List<(float d, ushort unit, int slot)>(ids.Count * slots.Count);
            foreach (ushort u in ids)
            {
                // A division already marching counts from where it is going, so a redistribution keeps it on course
                ushort heading = HeadingTo(u);
                ushort at = heading != 0 ? heading : units.GetUnit(u).provinceID;
                Vector3 from = default;
                bool hasFrom = counters != null && counters.TryGetProvincePoint(at, out from);
                for (int s = 0; s < slots.Count; s++)
                {
                    if (!Reaches(u, slots[s].front)) continue;
                    float d = 0f;
                    if (at != slots[s].province && hasFrom && counters.TryGetProvincePoint(slots[s].province, out var to))
                        d = (to - from).sqrMagnitude + 1e-6f;
                    else if (at != slots[s].province) d = 1e9f;
                    pairs.Add((d, u, s));
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
            foreach (ushort u in ids)
                if (!plan.Slot.ContainsKey(u))
                {
                    var unit = units.GetUnit(u);
                    if (fronts.Count > 0 && fronts[0].Count > 0) OrderMove(u, unit, fronts[0][0], dryRun: true); // logs why there is no path
                    ArchonLogger.LogWarning($"Hoi4BattlePlans: plan {plan.Id}: unit {u} in province {unit.provinceID} can reach none of the {fronts.Count} fronts", "game_units");
                }
            Hold(plan, force: true);
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
                bool moving = queue.IsUnitMoving(kv.Key);
                if (unit.provinceID == kv.Value)
                {
                    if (force && moving) queue.CancelMovement(kv.Key);
                    holding++;
                    continue;
                }
                // Re-ordering restarts the current hop, so leave a march to the right place alone
                if (moving && (!force || HeadingTo(kv.Key) == kv.Value)) { marching++; continue; }
                if (OrderMove(kv.Key, unit, kv.Value)) ordered++;
                else noPath++;
            }
            if (ordered > 0) Hoi4UnitCounters.Instance?.MarkDirty();
            if (force || ordered > 0)
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

        bool OrderMove(ushort unitId, Core.Units.UnitState unit, ushort target, bool dryRun = false)
        {
            var pathfinding = gameState.Pathfinding;
            if (pathfinding == null || !pathfinding.IsInitialized) return false;
            var options = Core.Systems.PathOptions.Default;
            options.Context = Core.Systems.PathContext.Create(unit.countryID, 0);
            var result = pathfinding.FindPathWithOptions(unit.provinceID, target, options);
            var path = result.Path;
            if (path == null || path.Count < 2)
            {
                // Diagnostics: why a front division can't march (owner of both ends, search status)
                var provinces = gameState.Provinces;
                ArchonLogger.LogWarning($"Hoi4BattlePlans: no path for unit {unitId} (type {unit.unitTypeID}, country {unit.countryID}) " +
                    $"from {unit.provinceID} (owner {provinces.GetProvinceOwner(unit.provinceID)}, water {Hoi4Regions.IsWater(unit.provinceID)}) " +
                    $"to {target} (owner {provinces.GetProvinceOwner(target)}, controller {Hoi4Occupation.ControllerOf(gameState, target)}): " +
                    $"{result.Status}, {result.NodesExplored} explored", "game_units");
                return false;
            }
            if (dryRun) return true;
            var type = StarterKit.Initializer.Instance?.UnitSystem?.GetUnitType(unit.unitTypeID);
            var command = new QueueUnitMovementCommand
            {
                UnitId = unitId,
                Path = new List<ushort>(path),
                MovementDays = type?.Speed ?? 3,
                CountryId = unit.countryID,
            };
            return gameState.TryExecuteCommand(command, out _);
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

        /// <summary>Ribbon along a chain: uv.x = distance in ribbon widths, uv.y = 0 own side .. 1 enemy side (left of travel).</summary>
        void AddRibbon(List<Vector3> pts, Color color, float style)
        {
            if (pts.Count < 2) return;
            float width = lineWidthPx * worldPerPxX * (style < 0.5f ? previewWidthScale : 1f);
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
        void ComputeLabelAnchor(Hoi4BattlePlan plan)
        {
            List<Vector3> bestPts = null;
            float bestLen = -1f;
            foreach (var pts in plan.Chains)
            {
                float len = 0f;
                for (int i = 1; i < pts.Count; i++) len += Vector3.Distance(pts[i - 1], pts[i]);
                if (len > bestLen) { bestLen = len; bestPts = pts; }
            }
            if (bestPts == null || bestPts.Count < 2) return;
            float half = bestLen * 0.5f, run = 0f;
            for (int i = 1; i < bestPts.Count; i++)
            {
                float seg = Vector3.Distance(bestPts[i - 1], bestPts[i]);
                if (run + seg >= half)
                {
                    float f = seg > 1e-6f ? (half - run) / seg : 0f;
                    plan.LabelWorld = Vector3.Lerp(bestPts[i - 1], bestPts[i], f);
                    plan.LabelAhead = bestPts[i];
                    if ((plan.LabelAhead - plan.LabelWorld).sqrMagnitude < 1e-8f) plan.LabelAhead = bestPts[Mathf.Min(i + 1, bestPts.Count - 1)];
                    break;
                }
                run += seg;
            }
            if (Hoi4UnitCounters.Instance != null)
            {
                plan.LabelWorld.y = Hoi4UnitCounters.Instance.TerrainHeight(plan.LabelWorld);
                plan.LabelAhead.y = Hoi4UnitCounters.Instance.TerrainHeight(plan.LabelAhead);
            }
        }
    }
}
