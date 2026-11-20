using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Core.Systems;
using Core.Units;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4 move arrows for the selected divisions that are moving, using HOI4's move_thick
    /// arrow (gfx/maparrows/, Tools/hoi4_convert.py; prototype Tools/prototypes/arrows).
    ///
    /// Each distinct remaining route becomes a ribbon: it starts at the stack's province (the part
    /// already travelled toward the next province fills in a lighter colour), runs through the province
    /// centres on a Catmull-Rom curve, tapers from body_width[0] to body_width[1] map pixels and
    /// ends in the head. One mesh, drawn by Hoi4/MoveArrow (draped on the terrain, below the
    /// counters). Rebuilt when the selection, movement or day changes.
    /// </summary>
    public sealed class Hoi4MoveArrows : MonoBehaviour
    {
        public static Hoi4MoveArrows Instance { get; private set; }

        [Tooltip("HOI4's amber move arrow")]
        public Color arrowColor = new Color(0.78f, 0.56f, 0.16f, 1f);
        [Tooltip("Colour of the part already travelled toward the next province")]
        public Color progressColor = new Color(1f, 0.95f, 0.78f, 1f);
        [Tooltip("Fraction of the arrow faded in at the root")]
        [Range(0f, 0.5f)] public float rootFade = 0.04f;
        [Tooltip("Arrow height above the terrain, world units")]
        public float lift = 0.05f;
        [Tooltip("Arrows shorter than this (map pixels) are scaled down, like HOI4's global_scale")]
        public float fullSizeLength = 200f;
        [Tooltip("Smallest arrow scale for short moves")]
        public float minScale = 0.5f;
        [Tooltip("Even body width in map pixels (as HOI4 shows it); 0 = the move_thick taper from body_width[0] to body_width[1]")]
        public float evenBodyWidth = 9f;
        [Range(0f, 1f)] public float stripeStrength = 0.08f;

        // move_thick defaults; overridden by gfx/maparrows/move_arrow.json5
        float bodyWidthStart = 20.5f, bodyWidthEnd = 6f, headLength = 19.5f;
        float bodyUvScale = 0.4f, fadeIn = 0.2f, animSpeed = 0.1f;

        static readonly int MaskId = Shader.PropertyToID("_MaskTex");
        static readonly int PatternId = Shader.PropertyToID("_PatternTex");
        static readonly int HeightmapId = Shader.PropertyToID("_HeightmapTexture");
        static readonly int HeightScaleId = Shader.PropertyToID("_HeightScale");
        static readonly int BoundsId = Shader.PropertyToID("_MapWorldBounds");
        static readonly int ColorId = Shader.PropertyToID("_ArrowColor");
        static readonly int ParamsId = Shader.PropertyToID("_ArrowParams");
        static readonly int StripeId = Shader.PropertyToID("_StripeStrength");
        static readonly int ProgressColorId = Shader.PropertyToID("_ProgressColor");

        GameState gameState;
        Material material;
        Mesh mesh;
        Texture2D maskTex, patternTex;
        IDisposable[] subscriptions;
        bool dirty;

        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<Vector2> infos = new List<Vector2>();
        readonly List<Vector2> fills = new List<Vector2>();
        readonly List<Vector2> centers = new List<Vector2>(); // centre-line xz: both edges take its terrain height
        readonly List<int> tris = new List<int>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (Instance != null) return;
            var go = new GameObject("Hoi4MoveArrows");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4MoveArrows>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (subscriptions != null)
                foreach (var s in subscriptions) s?.Dispose();
            if (material != null) Destroy(material);
            if (mesh != null) Destroy(mesh);
            if (maskTex != null) Destroy(maskTex);
            if (patternTex != null) Destroy(patternTex);
        }

        IEnumerator Start()
        {
            while (Hoi4UnitSelection.Instance == null || Hoi4UnitCounters.Instance == null || !Hoi4UnitCounters.Instance.IsReady)
                yield return null;

            var shader = Shader.Find("Hoi4/MoveArrow");
            maskTex = LoadTexture("gfx/maparrows/move_mask.png");
            patternTex = LoadTexture("gfx/maparrows/move_pattern.png");
            if (shader == null || maskTex == null || patternTex == null)
            {
                ArchonLogger.LogWarning("Hoi4MoveArrows: shader or gfx/maparrows textures missing (rerun Tools/hoi4_convert.py), no move arrows", "game_units");
                yield break;
            }
            LoadParams();

            gameState = Engine.ArchonEngine.Instance.GameState;
            var counters = Hoi4UnitCounters.Instance;
            material = new Material(shader) { name = "Hoi4MoveArrows" };
            material.SetTexture(MaskId, maskTex);
            material.SetTexture(PatternId, patternTex);
            material.SetVector(BoundsId, counters.MapWorldBounds);
            if (counters.Heightmap != null) material.SetTexture(HeightmapId, counters.Heightmap);
            material.SetFloat(HeightScaleId, counters.HeightScale);
            mesh = new Mesh { name = "Hoi4MoveArrows", indexFormat = IndexFormat.UInt32 };

            Hoi4UnitSelection.Instance.Changed += () => dirty = true;
            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<UnitMovementStartedEvent>(_ => dirty = true),
                gameState.EventBus.Subscribe<UnitMovementCompletedEvent>(_ => dirty = true),
                gameState.EventBus.Subscribe<UnitMovementCancelledEvent>(_ => dirty = true),
                gameState.EventBus.Subscribe<DailyTickEvent>(_ => dirty = true),
            };
            dirty = true;
        }

        void LateUpdate()
        {
            if (material == null) return;
            if (dirty)
            {
                dirty = false;
                Rebuild();
            }
            material.SetColor(ColorId, arrowColor);
            material.SetColor(ProgressColorId, progressColor);
            material.SetVector(ParamsId, new Vector4(bodyUvScale, animSpeed, rootFade, lift));
            material.SetFloat(StripeId, stripeStrength);
            if (mesh.vertexCount > 0)
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);
        }

        void LoadParams()
        {
            string path = DataFileResolver.Resolve("gfx/maparrows/move_arrow.json5");
            if (!File.Exists(path)) return;
            try
            {
                var root = Json5Loader.LoadJson5File(path);
                if (root["body_width"] is Newtonsoft.Json.Linq.JArray w && w.Count == 2)
                {
                    bodyWidthStart = (float)w[0];
                    bodyWidthEnd = (float)w[1];
                }
                headLength = root["head_length"] != null ? (float)root["head_length"] : headLength;
                bodyUvScale = root["body_uv_scale"] != null ? (float)root["body_uv_scale"] : bodyUvScale;
                fadeIn = root["fade_in"] != null ? (float)root["fade_in"] : fadeIn;
                animSpeed = root["texture_anim_speed"] != null ? (float)root["texture_anim_speed"] : animSpeed;
            }
            catch (Exception e)
            {
                ArchonLogger.LogWarning($"Hoi4MoveArrows: bad move_arrow.json5 ({e.Message}), using move_thick defaults", "game_units");
            }
        }

        static Texture2D LoadTexture(string relative)
        {
            string path = DataFileResolver.Resolve(relative);
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = Path.GetFileNameWithoutExtension(path),
            };
            if (tex.LoadImage(File.ReadAllBytes(path), true)) return tex;
            Destroy(tex);
            return null;
        }

        // --- Build ---------------------------------------------------------------------------

        void Rebuild()
        {
            verts.Clear(); uvs.Clear(); infos.Clear(); fills.Clear(); centers.Clear(); tris.Clear();
            var selection = Hoi4UnitSelection.Instance;
            var counters = Hoi4UnitCounters.Instance;
            var queue = gameState.Units.MovementQueue;
            var seen = new HashSet<string>();
            var points = new List<Vector3>();

            foreach (ushort id in selection.Selected)
            {
                if (!queue.TryGetMovementState(id, out var state)) continue;
                var route = queue.GetFullPath(id); // current province, next stop, remaining waypoints
                if (route.Count < 2) continue;
                float progress = Mathf.Clamp01(state.GetProgress());
                string key = string.Join(",", route) + "@" + progress.ToString("0.00");
                if (!seen.Add(key)) continue; // same stack, same orders: one arrow

                points.Clear();
                for (int i = 0; i < route.Count; i++)
                    if (counters.TryGetProvincePoint(route[i], out var p))
                        points.Add(new Vector3(p.x, 0f, p.z));
                if (points.Count < 2) continue;
                // Like HOI4: the arrow starts at the division's province and the stretch already
                // travelled toward the next province fills in
                AddArrow(points, counters.WorldPerMapPixel, progress);
            }

            mesh.Clear();
            if (verts.Count == 0) return;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, infos);
            mesh.SetUVs(2, fills);
            mesh.SetUVs(3, centers);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f); // heights come from the shader
        }

        void AddArrow(List<Vector3> controls, float worldPerPx, float progress)
        {
            var curve = CatmullRom(controls, worldPerPx * 2f, out int firstLegEnd, out int lastLegStart);
            if (curve.Count < 2) return;
            var cum = new float[curve.Count];
            for (int i = 1; i < curve.Count; i++)
                cum[i] = cum[i - 1] + Vector3.Distance(curve[i - 1], curve[i]);
            float length = cum[curve.Count - 1];
            if (length <= 1e-4f) return;

            float scale = Mathf.Clamp(length / worldPerPx / Mathf.Max(fullSizeLength, 1f), Mathf.Clamp01(minScale), 1f);
            float startPx = evenBodyWidth > 0f ? evenBodyWidth : bodyWidthStart;
            float endPx = evenBodyWidth > 0f ? evenBodyWidth : bodyWidthEnd;
            float w0 = startPx * scale * worldPerPx, w1 = endPx * scale * worldPerPx;
            // The head stays on the final leg (last province to the destination): a head reaching
            // back past a corner would cut across it
            float lastLeg = length - cum[Mathf.Clamp(lastLegStart, 0, curve.Count - 1)];
            float head = Mathf.Min(headLength * scale * worldPerPx, Mathf.Min(length * 0.6f, lastLeg * 0.85f));
            float headWidth = w1 / Mathf.Max(bodyUvScale, 0.05f);
            float bodyLength = length - head;
            // Travelled part: progress along the first leg (current province -> next province)
            fill = cum[Mathf.Clamp(firstLegEnd, 0, curve.Count - 1)] * progress / length;
            // Body texture tile length keeps the texture aspect (half width : body height)
            float tile = w0 * 0.5f * 2f / Mathf.Max(bodyUvScale, 0.05f);

            // Straight head, like HOI4's: bent along a curve its inner side squeezes and the barbs
            // come out lopsided. It runs along the chord of the curve's last `head` length and ends
            // at the destination; the body is re-curved to end at the head's start with its end
            // tangent aimed at the tip, so it bends smoothly into the head.
            // Prototype: Tools/prototypes/arrows/arrow_mesh.py
            float window = Mathf.Max(w0, w1);
            var tip = curve[curve.Count - 1];
            var chordStart = PointAt(curve, cum, bodyLength, out _);
            var headDir = tip - chordStart;
            headDir.y = 0f;
            headDir = headDir.sqrMagnitude > 1e-10f ? headDir.normalized : Vector3.forward;
            var headStart = tip - headDir * head;

            var bodyControls = new List<Vector3>(controls);
            bodyControls[bodyControls.Count - 1] = headStart;
            if (bodyControls.Count >= 2 && (bodyControls[bodyControls.Count - 1] - bodyControls[bodyControls.Count - 2]).sqrMagnitude < 1e-8f)
                bodyControls.RemoveAt(bodyControls.Count - 2);
            var bodyCurve = bodyControls.Count >= 2
                ? CatmullRom(bodyControls, worldPerPx * 2f, out _, out _, tip)
                : new List<Vector3> { headStart };
            var bodyCum = new float[bodyCurve.Count];
            for (int i = 1; i < bodyCurve.Count; i++)
                bodyCum[i] = bodyCum[i - 1] + Vector3.Distance(bodyCurve[i - 1], bodyCurve[i]);
            float bodyLen = bodyCum[bodyCurve.Count - 1];
            float total = Mathf.Max(bodyLen + head, 1e-4f);

            for (int i = 0; i < bodyCurve.Count; i++)
            {
                // Side directions from points a body width apart: a turn tighter than the ribbon
                // is wide doesn't fold its inner edge over itself. The last one matches the head.
                var dir = i == bodyCurve.Count - 1 ? headDir : DirectionAt(bodyCurve, bodyCum, bodyCum[i], window);
                float t = bodyLen > 0 ? bodyCum[i] / bodyLen : 1f;
                float half = Mathf.Lerp(w0, w1, t) * 0.5f;
                AddPair(bodyCurve[i], dir, half, bodyCum[i] / tile, 0f, bodyCum[i] / total, i > 0);
            }

            // Head cross-sections as dense as the body's, so it follows the terrain the same way
            int headSteps = Mathf.Clamp(Mathf.CeilToInt(head / Mathf.Max(worldPerPx * 2f, 1e-4f)), 4, 32);
            for (int k = 0; k <= headSteps; k++)
            {
                float f = k / (float)headSteps;
                AddPair(headStart + headDir * (head * f), headDir, headWidth * 0.5f, f, 1f, (bodyLen + f * head) / total, k > 0);
            }
        }

        float fill; // travelled fraction of the arrow being built

        void AddPair(Vector3 p, Vector3 dir, float half, float u, float isHead, float along, bool connect)
        {
            var side = new Vector3(-dir.z, 0f, dir.x) * half;
            int v = verts.Count;
            verts.Add(p - side); uvs.Add(new Vector2(u, 0f)); infos.Add(new Vector2(isHead, along)); fills.Add(new Vector2(fill, 0f));
            verts.Add(p + side); uvs.Add(new Vector2(u, 1f)); infos.Add(new Vector2(isHead, along)); fills.Add(new Vector2(fill, 0f));
            centers.Add(new Vector2(p.x, p.z)); centers.Add(new Vector2(p.x, p.z));
            if (!connect) return;
            tris.Add(v - 2); tris.Add(v); tris.Add(v - 1);
            tris.Add(v - 1); tris.Add(v); tris.Add(v + 1);
        }

        static Vector3 DirectionAt(List<Vector3> c, float[] cum, float s, float window)
        {
            float length = cum[c.Count - 1];
            var a = PointAt(c, cum, Mathf.Max(s - window * 0.5f, 0f), out _);
            var b = PointAt(c, cum, Mathf.Min(s + window * 0.5f, length), out var fallback);
            var d = b - a;
            d.y = 0f;
            return d.sqrMagnitude > 1e-10f ? d.normalized : fallback;
        }

        static Vector3 PointAt(List<Vector3> c, float[] cum, float s, out Vector3 dir)
        {
            for (int i = 1; i < c.Count; i++)
            {
                if (cum[i] >= s || i == c.Count - 1)
                {
                    float seg = cum[i] - cum[i - 1];
                    float t = seg > 1e-6f ? Mathf.Clamp01((s - cum[i - 1]) / seg) : 1f;
                    var d = c[i] - c[i - 1];
                    dir = d.sqrMagnitude > 1e-10f ? d.normalized : Vector3.forward;
                    return Vector3.Lerp(c[i - 1], c[i], t);
                }
            }
            dir = Vector3.forward;
            return c[c.Count - 1];
        }

        /// <summary>
        /// Centripetal Catmull-Rom (alpha 0.5) through the points: unlike the uniform kind it
        /// never loops or forms cusps at sharp corners. firstLegEnd = index of pts[1] in the
        /// result, lastLegStart = index of pts[n-2]. endPhantom, if given, steers the end tangent
        /// (default: the last leg continued straight).
        /// </summary>
        static List<Vector3> CatmullRom(List<Vector3> pts, float step, out int firstLegEnd, out int lastLegStart,
            Vector3? endPhantom = null)
        {
            var result = new List<Vector3>();
            firstLegEnd = 0;
            lastLegStart = 0;
            int n = pts.Count;
            for (int i = 0; i < n - 1; i++)
            {
                Vector3 p1 = pts[i], p2 = pts[i + 1];
                // Phantom end points continue the line, so the ends don't curl
                Vector3 p0 = i > 0 ? pts[i - 1] : p1 + (p1 - p2);
                Vector3 p3 = i + 2 < n ? pts[i + 2] : endPhantom ?? p2 + (p2 - p1);
                float t0 = 0f;
                float t1 = t0 + Knot(p0, p1);
                float t2 = t1 + Knot(p1, p2);
                float t3 = t2 + Knot(p2, p3);

                if (i == n - 2) lastLegStart = result.Count;
                int steps = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(p1, p2) / Mathf.Max(step, 1e-4f)), 1, 256);
                for (int k = 0; k < steps; k++)
                {
                    float t = Mathf.Lerp(t1, t2, k / (float)steps);
                    var a1 = Blend(p0, p1, t0, t1, t);
                    var a2 = Blend(p1, p2, t1, t2, t);
                    var a3 = Blend(p2, p3, t2, t3, t);
                    var b1 = Blend(a1, a2, t0, t2, t);
                    var b2 = Blend(a2, a3, t1, t3, t);
                    result.Add(Blend(b1, b2, t1, t2, t));
                }
                if (i == 0) firstLegEnd = result.Count;
            }
            result.Add(pts[n - 1]);
            return result;
        }

        static float Knot(Vector3 a, Vector3 b) => Mathf.Max(Mathf.Sqrt(Vector3.Distance(a, b)), 1e-4f);

        static Vector3 Blend(Vector3 a, Vector3 b, float ta, float tb, float t)
        {
            float d = tb - ta;
            return d > 1e-6f ? a * ((tb - t) / d) + b * ((t - ta) / d) : a;
        }
    }
}
