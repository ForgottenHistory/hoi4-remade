using System;
using System.Collections;
using System.Collections.Generic;
using Core;
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
        public Vector3 LabelWorld, LabelAhead;      // text anchor on the line and a point a little further along it
    }

    /// <summary>
    /// GAME: battle plans, HOI4's way of ordering a front. Only offensive lines for now. The tool (button in
    /// Hoi4BattlePlanPanel) follows the cursor: hovering a country shows its border with the player as a green
    /// dashed line and stripes the player's provinces along it; a left click places the line in the colour
    /// of the army of the selected divisions, which get assigned to it unless they already follow another
    /// plan. The divisions do not act on the line yet.
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
                if (!planOf.ContainsKey(u)) { plan.Units.Add(u); planOf[u] = plan; }
            ComputeLabelAnchor(plan);
            plans.Add(plan);
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
            if (!Active) { PruneDeadUnits(); return; }
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

        float lastPrune;
        void PruneDeadUnits()
        {
            if (Time.unscaledTime - lastPrune < 1f || plans.Count == 0) return;
            lastPrune = Time.unscaledTime;
            var units = gameState.Units;
            bool changed = false;
            foreach (var plan in plans)
                plan.Units.RemoveWhere(u =>
                {
                    if (units.HasUnit(u)) return false;
                    planOf.Remove(u);
                    changed = true;
                    return true;
                });
            if (changed) Changed?.Invoke();
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

            var arcs = new List<BorderArc>();
            foreach (var arc in allArcs)
            {
                ushort a = Hoi4Occupation.ControllerOf(gameState, arc.ProvinceA);
                ushort b = Hoi4Occupation.ControllerOf(gameState, arc.ProvinceB);
                if (a == player && b == country) previewProvinces.Add(arc.ProvinceA);
                else if (b == player && a == country) previewProvinces.Add(arc.ProvinceB);
                else continue;
                arcs.Add(arc);
            }
            if (arcs.Count == 0) { previewProvinces.Clear(); return; }
            hoverCountry = country;
            previewArcs = arcs;
            previewChains = BuildChains(arcs, country);
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
