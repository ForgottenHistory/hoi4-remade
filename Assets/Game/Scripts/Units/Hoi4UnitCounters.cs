using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Core.Units;
using Map.Utils;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Rendering;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4 on-map division counters. One counter per country per province (a stack), laid
    /// out like HOI4's interface/mapicons.gui unit_counter: grey plate, type icon of the stack's most
    /// common division type, small flag, organisation and strength bars, division count in the
    /// dark box and the ruling ideology in the box corner.
    ///
    /// Counters are composed on the CPU into one atlas from the sprites under gfx/counters/
    /// (Tools/hoi4_convert.py) and drawn as one mesh by Hoi4/UnitCounter: a screen-space size
    /// above the terrain height, on top of the map; they shrink as the camera zooms out and fade
    /// out where province borders do.
    /// Rebuilt when units are created, destroyed or move. Replaces the StarterKit unit badges.
    /// </summary>
    public sealed class Hoi4UnitCounters : MonoBehaviour
    {
        // Counter canvas: HOI4 plate is 66x27; slots get a transparent margin against bilinear bleed
        const int CounterW = 66, CounterH = 27, SlotW = 68, SlotH = 29;
        const int AtlasWidth = 2048;
        const float StackSpacingPx = 70f;

        // Zoom is measured like the border fades: screen pixels per map (province bitmap) pixel
        [Tooltip("Screen pixels per counter pixel at full size")]
        public float pixelScale = 1.5f;
        [Tooltip("Zoom (screen px per map px) at which counters reach full size; zoomed further out they shrink")]
        public float fullSizeZoom = 12f;
        [Tooltip("Smallest size, as a fraction of full size")]
        public float minSizeFraction = 0.45f;
        [Tooltip("Zoom (screen px per map px) below which counters are hidden (province borders: 3)")]
        public float fadeHiddenZoom = 3f;
        [Tooltip("Zoom (screen px per map px) above which counters are fully visible (province borders: 5)")]
        public float fadeVisibleZoom = 5f;
        [Tooltip("Counter centre height above the terrain, in world units")]
        public float liftAboveTerrain = 0f;
        [Tooltip("Plate brightness (HOI4's plate is grey; 0 = black)")]
        [Range(0f, 1f)] public float plateBrightness = 0.22f;

        static readonly int AtlasId = Shader.PropertyToID("_MainTex");
        static readonly int HeightmapId = Shader.PropertyToID("_HeightmapTexture");
        static readonly int HeightScaleId = Shader.PropertyToID("_HeightScale");
        static readonly int BoundsId = Shader.PropertyToID("_MapWorldBounds");
        static readonly int ParamsId = Shader.PropertyToID("_CounterParams");
        static readonly int SizeId = Shader.PropertyToID("_CounterSize");

        public static Hoi4UnitCounters Instance { get; private set; }

        sealed class Sprite
        {
            public Color32[] Pixels; // row 0 = top
            public int Width, Height;
        }

        sealed class Stack
        {
            public ushort Province, Country;
            public string Tag;
            public int Count;
            public float Org, MaxOrg, Str, MaxStr;
            public readonly Dictionary<string, int> Types = new Dictionary<string, int>();
            public Vector3 Anchor;
            public int IndexInProvince, StacksInProvince;
        }

        GameState gameState;
        float worldPerMapPixel = 0.1f;
        ProvinceCenterLookup centers;
        IDisposable[] subscriptions;
        bool dirty;

        Sprite plate, ideologyStrip, barOrg, barStr, digits;
        int digitW = 9, digitH = 14;
        readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();
        readonly Dictionary<string, Sprite> flags = new Dictionary<string, Sprite>();
        readonly Dictionary<string, int> ideologyFrame = new Dictionary<string, int>();
        readonly Dictionary<string, string> countryIdeology = new Dictionary<string, string>();

        Material material;
        Mesh mesh;
        Texture2D atlas;
        Color32[] atlasPixels;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (Instance != null) return;
            var go = new GameObject("Hoi4UnitCounters");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4UnitCounters>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (subscriptions != null)
                foreach (var s in subscriptions) s?.Dispose();
            if (material != null) Destroy(material);
            if (mesh != null) Destroy(mesh);
            if (atlas != null) Destroy(atlas);
        }

        IEnumerator Start()
        {
            while (Hoi4Divisions.Instance == null || !Hoi4Divisions.Instance.IsLoaded)
                yield return null;

            var engine = Engine.ArchonEngine.Instance;
            gameState = engine != null ? engine.GameState : null;
            if (gameState?.Units == null || engine.TextureManager == null || engine.MapMeshRenderer == null)
            {
                ArchonLogger.LogWarning("Hoi4UnitCounters: engine map or unit system missing", "game_units");
                yield break;
            }

            var shader = Shader.Find("Hoi4/UnitCounter");
            if (shader == null)
            {
                ArchonLogger.LogError("Hoi4UnitCounters: shader Hoi4/UnitCounter not found", "game_units");
                yield break;
            }
            if (!LoadSprites())
                yield break;

            // HOI4 counters replace the StarterKit's generic unit badges
            var badges = FindFirstObjectByType<StarterKit.UnitVisualization>();
            if (badges != null) badges.enabled = false;

            var mapRenderer = engine.MapMeshRenderer;
            centers = new ProvinceCenterLookup();
            centers.Initialize(engine.ProvinceMapping, mapRenderer.transform,
                engine.TextureManager.MapWidth, engine.TextureManager.MapHeight, engine.TextureManager);

            material = new Material(shader) { name = "Hoi4UnitCounters" };
            var bounds = mapRenderer.bounds;
            material.SetVector(BoundsId, new Vector4(bounds.min.x, bounds.min.z, bounds.size.x, bounds.size.z));
            worldPerMapPixel = bounds.size.x / Mathf.Max(engine.TextureManager.MapWidth, 1);
            if (engine.TextureManager.HeightmapTexture != null)
                material.SetTexture(HeightmapId, engine.TextureManager.HeightmapTexture);
            var mapMaterial = mapRenderer.sharedMaterial;
            material.SetFloat(HeightScaleId, mapMaterial != null && mapMaterial.HasProperty("_HeightScale")
                ? mapMaterial.GetFloat("_HeightScale") : 10f);

            mesh = new Mesh { name = "Hoi4UnitCounters", indexFormat = IndexFormat.UInt32 };

            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<UnitCreatedEvent>(_ => dirty = true),
                gameState.EventBus.Subscribe<UnitDestroyedEvent>(_ => dirty = true),
                gameState.EventBus.Subscribe<UnitMovedEvent>(_ => dirty = true),
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

            material.SetVector(ParamsId, new Vector4(pixelScale, fadeHiddenZoom, Mathf.Max(fadeVisibleZoom, fadeHiddenZoom + 0.01f), liftAboveTerrain));
            material.SetVector(SizeId, new Vector4(Mathf.Max(fullSizeZoom, 0.01f), Mathf.Clamp01(minSizeFraction), worldPerMapPixel, 0f));
            if (mesh.vertexCount > 0)
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);
        }

        // --- Sprites -------------------------------------------------------------------------

        bool LoadSprites()
        {
            plate = LoadSprite("gfx/counters/plate.png");
            ideologyStrip = LoadSprite("gfx/counters/ideology.png");
            barOrg = LoadSprite("gfx/counters/bar_org.png");
            barStr = LoadSprite("gfx/counters/bar_str.png");
            digits = LoadSprite("gfx/counters/digits.png");
            if (plate == null || ideologyStrip == null || barOrg == null || barStr == null || digits == null)
            {
                ArchonLogger.LogWarning("Hoi4UnitCounters: counter sprites missing under gfx/counters (rerun Tools/hoi4_convert.py)", "game_units");
                return false;
            }

            string meta = DataFileResolver.Resolve("gfx/counters/counters.json5");
            if (File.Exists(meta))
            {
                var root = Json5Loader.LoadJson5File(meta);
                if (root["ideology_frames"] is JArray frames)
                    for (int i = 0; i < frames.Count; i++)
                        ideologyFrame[(string)frames[i]] = i;
                if (root["digit_cell"] is JArray cell && cell.Count == 2)
                {
                    digitW = (int)cell[0];
                    digitH = (int)cell[1];
                }
                if (root["countries"] is JObject countries)
                    foreach (var p in countries.Properties())
                        countryIdeology[p.Name] = (string)p.Value;
            }
            return true;
        }

        Sprite Icon(string type)
        {
            if (!icons.TryGetValue(type, out var s))
                icons[type] = s = LoadSprite($"gfx/counters/icons/{type}.png");
            return s;
        }

        Sprite Flag(string tag)
        {
            if (!flags.TryGetValue(tag, out var s))
                flags[tag] = s = LoadSprite($"gfx/counters/flags/{tag}.png");
            return s;
        }

        static Sprite LoadSprite(string relative)
        {
            string path = DataFileResolver.Resolve(relative);
            if (!File.Exists(path)) return null;
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!tex.LoadImage(File.ReadAllBytes(path))) return null;
                var raw = tex.GetPixels32(); // row 0 = bottom
                int w = tex.width, h = tex.height;
                var top = new Color32[raw.Length];
                for (int y = 0; y < h; y++)
                    Array.Copy(raw, (h - 1 - y) * w, top, y * w, w);
                return new Sprite { Pixels = top, Width = w, Height = h };
            }
            finally
            {
                Destroy(tex);
            }
        }

        // --- Stacks --------------------------------------------------------------------------

        List<Stack> CollectStacks()
        {
            var byKey = new Dictionary<uint, Stack>();
            var units = gameState.Units;
            foreach (ushort id in units.GetAllUnits())
            {
                var u = units.GetUnit(id);
                uint key = ((uint)u.provinceID << 16) | u.countryID;
                if (!byKey.TryGetValue(key, out var s))
                {
                    byKey[key] = s = new Stack
                    {
                        Province = u.provinceID,
                        Country = u.countryID,
                        Tag = gameState.Countries.GetCountryTag(u.countryID),
                    };
                }
                s.Count++;
                var d = Hoi4Divisions.Instance != null ? Hoi4Divisions.Instance.Get(id) : null;
                string type = d?.MainType ?? "infantry";
                s.Types[type] = s.Types.TryGetValue(type, out int n) ? n + 1 : 1;
                if (d != null)
                {
                    s.Org += d.Organisation; s.MaxOrg += d.MaxOrganisation;
                    s.Str += d.Strength; s.MaxStr += d.MaxStrength;
                }
            }

            var stacks = new List<Stack>();
            var perProvince = new Dictionary<ushort, List<Stack>>();
            foreach (var s in byKey.Values)
            {
                if (!centers.TryGetProvinceCenter(s.Province, out s.Anchor)) continue;
                if (!perProvince.TryGetValue(s.Province, out var list))
                    perProvince[s.Province] = list = new List<Stack>();
                list.Add(s);
                stacks.Add(s);
            }
            foreach (var list in perProvince.Values)
            {
                list.Sort((a, b) => b.Count.CompareTo(a.Count));
                for (int i = 0; i < list.Count; i++)
                {
                    list[i].IndexInProvince = i;
                    list[i].StacksInProvince = list.Count;
                }
            }
            // North first so southern counters draw on top (they are nearer the camera)
            stacks.Sort((a, b) => b.Anchor.z.CompareTo(a.Anchor.z));
            return stacks;
        }

        // --- Build ---------------------------------------------------------------------------

        void Rebuild()
        {
            var stacks = CollectStacks();
            int cols = AtlasWidth / SlotW;
            int rows = Mathf.Max(1, (stacks.Count + cols - 1) / cols);
            int atlasHeight = Mathf.NextPowerOfTwo(rows * SlotH);
            if (atlas == null || atlas.height != atlasHeight)
            {
                if (atlas != null) Destroy(atlas);
                atlas = new Texture2D(AtlasWidth, atlasHeight, TextureFormat.RGBA32, false)
                {
                    name = "Hoi4UnitCounterAtlas",
                    filterMode = FilterMode.Bilinear,
                    wrapMode = TextureWrapMode.Clamp,
                };
                atlasPixels = new Color32[AtlasWidth * atlasHeight];
                material.SetTexture(AtlasId, atlas);
            }
            else
            {
                Array.Clear(atlasPixels, 0, atlasPixels.Length);
            }

            var verts = new Vector3[stacks.Count * 4];
            var uvs = new Vector2[stacks.Count * 4];
            var offsets = new Vector2[stacks.Count * 4];
            var tris = new int[stacks.Count * 6];
            for (int i = 0; i < stacks.Count; i++)
            {
                var s = stacks[i];
                int sx = (i % cols) * SlotW, sy = (i / cols) * SlotH; // slot top-left, row 0 = top
                ComposeCounter(s, sx, sy);

                // Atlas uv (texture rows are bottom-up)
                float u0 = (float)sx / AtlasWidth, u1 = (float)(sx + SlotW) / AtlasWidth;
                float v1 = 1f - (float)sy / atlasHeight, v0 = 1f - (float)(sy + SlotH) / atlasHeight;

                // Offsets in counter pixels from the anchor (x right, y up), stacks side by side
                float shift = (s.IndexInProvince - (s.StacksInProvince - 1) * 0.5f) * StackSpacingPx;
                float x0 = -SlotW * 0.5f + shift, x1 = SlotW * 0.5f + shift;
                float y0 = 0f, y1 = SlotH; // bottom edge on the anchor (shader stands it up)

                int v = i * 4;
                for (int k = 0; k < 4; k++) verts[v + k] = s.Anchor;
                uvs[v] = new Vector2(u0, v0); offsets[v] = new Vector2(x0, y0);
                uvs[v + 1] = new Vector2(u0, v1); offsets[v + 1] = new Vector2(x0, y1);
                uvs[v + 2] = new Vector2(u1, v1); offsets[v + 2] = new Vector2(x1, y1);
                uvs[v + 3] = new Vector2(u1, v0); offsets[v + 3] = new Vector2(x1, y0);
                int t = i * 6;
                tris[t] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;
            }

            atlas.SetPixels32(atlasPixels);
            atlas.Apply(false);

            mesh.Clear();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.uv2 = offsets;
            mesh.triangles = tris;
            // Vertices sit at the anchors; the shader moves them in screen space, so never cull
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);

            ArchonLogger.Log($"Hoi4UnitCounters: {stacks.Count} counters, atlas {AtlasWidth}x{atlasHeight}", "game_units");
        }

        void ComposeCounter(Stack s, int ox, int oy)
        {
            // Layout from HOI4 interface/mapicons.gui (unit_counter)
            byte pb = (byte)Mathf.RoundToInt(Mathf.Clamp01(plateBrightness) * 255f);
            Blit(plate, 0, 0, CounterW, CounterH, ox, oy, new Color32(pb, pb, pb, 255));
            string ideology = countryIdeology.TryGetValue(s.Tag ?? "", out var ide) ? ide : "neutrality";
            int frame = ideologyFrame.TryGetValue(ideology, out int f) ? f : 3;
            int ideoW = ideologyStrip.Width / Mathf.Max(ideologyFrame.Count, 1);
            Blit(ideologyStrip, frame * ideoW, 0, ideoW, ideologyStrip.Height, ox + 37, oy);

            var flag = s.Tag != null ? Flag(s.Tag) : null;
            if (flag != null) Blit(flag, 0, 0, flag.Width, flag.Height, ox + 5, oy + 15);

            string type = "infantry";
            int best = -1;
            foreach (var kv in s.Types)
                if (kv.Value > best) { best = kv.Value; type = kv.Key; }
            var icon = Icon(type) ?? Icon("infantry");
            if (icon != null) Blit(icon, 0, 0, icon.Width, icon.Height, ox + 3, oy + 2);

            Bar(barOrg, s.MaxOrg > 0 ? s.Org / s.MaxOrg : 1f, ox + 16, oy + 16);
            Bar(barStr, s.MaxStr > 0 ? s.Str / s.MaxStr : 1f, ox + 16, oy + 19);

            // Division count, centred in the dark box (centre x 46, y 13)
            string text = s.Count.ToString();
            const int advance = 7;
            int textW = (text.Length - 1) * advance + digitW;
            int x = ox + 46 - textW / 2, y = oy + 13 - digitH / 2;
            var tint = new Color32(235, 235, 225, 255);
            foreach (char c in text)
            {
                Blit(digits, (c - '0') * digitW, 0, digitW, digitH, x, y, tint);
                x += advance;
            }
        }

        void Bar(Sprite bar, float fill, int x, int y)
        {
            int w = bar.Width;
            Fill(x, y, w, bar.Height, new Color32(20, 20, 20, 200));
            int full = Mathf.RoundToInt(w * Mathf.Clamp01(fill));
            if (full > 0) Blit(bar, 0, 0, full, bar.Height, x, y);
        }

        void Fill(int dx, int dy, int w, int h, Color32 c)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Over(dx + x, dy + y, c);
        }

        void Blit(Sprite src, int sx, int sy, int w, int h, int dx, int dy, Color32? tint = null)
        {
            for (int y = 0; y < h; y++)
            {
                if (sy + y >= src.Height) break;
                for (int x = 0; x < w; x++)
                {
                    if (sx + x >= src.Width) break;
                    var c = src.Pixels[(sy + y) * src.Width + sx + x];
                    if (c.a == 0) continue;
                    if (tint.HasValue)
                    {
                        var t = tint.Value;
                        c = new Color32((byte)(c.r * t.r / 255), (byte)(c.g * t.g / 255), (byte)(c.b * t.b / 255), c.a);
                    }
                    Over(dx + x, dy + y, c);
                }
            }
        }

        // Straight-alpha "over" at atlas pixel (x, y), y = 0 at the top
        void Over(int x, int y, Color32 c)
        {
            int h = atlas.height;
            if (x < 0 || x >= AtlasWidth || y < 0 || y >= h) return;
            int i = (h - 1 - y) * AtlasWidth + x;
            var d = atlasPixels[i];
            float a = c.a / 255f, da = d.a / 255f;
            float oa = a + da * (1f - a);
            if (oa <= 0f) return;
            float k = da * (1f - a);
            atlasPixels[i] = new Color32(
                (byte)((c.r * a + d.r * k) / oa),
                (byte)((c.g * a + d.g * k) / oa),
                (byte)((c.b * a + d.b * k) / oa),
                (byte)(oa * 255f + 0.5f));
        }
    }
}
