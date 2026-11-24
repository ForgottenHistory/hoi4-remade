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
        // Each slot has a strip on the left for the no-orders mark (mapicons.gui no_orders sits at x -10) and one on top
        // for the low supply crate (mapicons.gui low_supply sits at y -4)
        const int CounterW = 66, CounterH = 27, BodyW = 68, LeftMargin = 12, TopMargin = 4, SlotW = BodyW + LeftMargin,
                  SlotH = 29 + TopMargin;
        const int AtlasWidth = 2048;
        const float StackSpacingPx = 70f;
        const float BaseYPx = -CounterH * 1.5f; // the lowest counter is centred on the province, not standing on it
        const float RowSpacingPx = CounterH; // different templates of a country pile up flush, one counter above the other

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
            public readonly List<ushort> Units = new List<ushort>();
            public bool Selected;
            public float X0, X1; // horizontal extent in counter pixels relative to the anchor
            public ushort Province, Country;
            public string Tag;
            public int Count;
            public float Org, MaxOrg, Str, MaxStr;
            public readonly Dictionary<string, int> Types = new Dictionary<string, int>();
            public Vector3 Anchor;
            public int IndexInProvince, StacksInProvince; // column among the countries in the province
            public string Template;
            public int GroupCount; // divisions of the template in the province, also when opened into single counters (keeps the pile's order)
            public int Battle; // battle bubble: 0 not the player's fight, 1 the player is losing, 2 winning
            public int ArmyId; // the army of its first division, 0 when unassigned: colours the strip behind the action icon
            public int Action; // 0 idle, 1 moving, 2 defending a battle, 3 attacking
            public bool NoOrders; // a division of an army that follows no battle plan and no order: red exclamation mark
            public Color32 Color; // the country's map colour, the counter's background
            public int Row; // templates of one country stack upwards
            public bool IsBattle; // a battle marker on a border, not a division counter (Count = attacker %)
            public float LowestSupply = 1f; // the player's divisions: the lowest supply ratio among them
            public int Supply; // the low supply crate: 0 none, 1 yellow, 2 orange, 3 red
        }

        GameState gameState;
        float worldPerMapPixel = 0.1f;
        ProvinceCenterLookup centers;
        IDisposable[] subscriptions;
        bool dirty;
        float lastRebuild = -1f;
        const float MinRebuildInterval = 0.1f; // hourly ticks at high speed would otherwise rebuild every frame
        // Composed counters by content: a counter that did not change is copied, not re-composed
        readonly Dictionary<CounterKey, Color32[]> composed = new Dictionary<CounterKey, Color32[]>();
        int atlasH; // atlas.height, kept here: Over() runs per pixel and the Texture getter is a native call

        /// <summary>What a composed counter image depends on (a struct key: building a string per counter per
        /// rebuild cost more than the lookup it served).</summary>
        readonly struct CounterKey : IEquatable<CounterKey>
        {
            readonly bool battle, selected, noOrders;
            readonly string tag, type;
            readonly int rgb, action, army, count, org, str, supply;

            public CounterKey(bool battle, string tag, int rgb, string type, int action, int army, int count,
                              int org, int str, bool selected, bool noOrders, int supply)
            {
                this.battle = battle; this.tag = tag; this.rgb = rgb; this.type = type; this.action = action;
                this.army = army; this.count = count; this.org = org; this.str = str;
                this.selected = selected; this.noOrders = noOrders; this.supply = supply;
            }

            public bool Equals(CounterKey o) =>
                battle == o.battle && selected == o.selected && noOrders == o.noOrders && rgb == o.rgb &&
                action == o.action && army == o.army && count == o.count && org == o.org && str == o.str && supply == o.supply &&
                string.Equals(tag, o.tag) && string.Equals(type, o.type);

            public override bool Equals(object obj) => obj is CounterKey k && Equals(k);

            public override int GetHashCode() =>
                HashCode.Combine(HashCode.Combine(battle, selected, noOrders, rgb, action, army, count),
                                 org, str, supply, tag, type);
        }

        Sprite actionMove, actionDefend, actionAttack; // optional: the arrow, shield and double chevron on a counter's right edge
        Sprite noOrders; // optional: GFX_mapicon_unit_no_orders
        readonly Sprite[] supplyCrates = new Sprite[3]; // optional: GFX_unitcounter_supply_status_strip (yellow, orange, red)
        Sprite plate, selectedFrame, ideologyStrip, barOrg, barStr, digits;
        List<Stack> stacks = new List<Stack>();
        Texture2D heightmap;
        float heightScale = 10f;
        Vector4 mapBounds;

        /// <summary>Selected engine unit IDs (owned by the selection controller); counters holding any are framed.</summary>
        public ISet<ushort> Selection { get; set; }

        public bool IsReady => material != null;

        // Map placement shared with other GAME map overlays (move arrows)
        public Vector4 MapWorldBounds => mapBounds;
        public Texture2D Heightmap => heightmap;
        public float HeightScale => heightScale;
        public float WorldPerMapPixel => worldPerMapPixel;

        /// <summary>Re-compose counters (selection, organisation or strength changed).</summary>
        public void MarkDirty() => dirty = true;
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
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
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
            mapBounds = new Vector4(bounds.min.x, bounds.min.z, bounds.size.x, bounds.size.z);
            material.SetVector(BoundsId, mapBounds);
            heightmap = engine.TextureManager.HeightmapTexture;
            worldPerMapPixel = bounds.size.x / Mathf.Max(engine.TextureManager.MapWidth, 1);
            if (engine.TextureManager.HeightmapTexture != null)
                material.SetTexture(HeightmapId, engine.TextureManager.HeightmapTexture);
            var mapMaterial = mapRenderer.sharedMaterial;
            heightScale = mapMaterial != null && mapMaterial.HasProperty("_HeightScale") ? mapMaterial.GetFloat("_HeightScale") : 10f;
            material.SetFloat(HeightScaleId, heightScale);

            mesh = new Mesh { name = "Hoi4UnitCounters", indexFormat = IndexFormat.UInt32 };

            subscriptions = new[]
            {
                gameState.EventBus.Subscribe<UnitCreatedEvent>(_ => dirty = true),
                gameState.EventBus.Subscribe<UnitDestroyedEvent>(_ => dirty = true),
                gameState.EventBus.Subscribe<UnitMovedEvent>(_ => dirty = true),
            };
            dirty = true;
        }

        /// <summary>Not drawn and not pickable (the battle plan delete tool hides them, like HOI4).</summary>
        public bool Hidden;

        void LateUpdate()
        {
            if (material == null) return;
            if (dirty && Time.unscaledTime - lastRebuild >= MinRebuildInterval)
            {
                dirty = false;
                lastRebuild = Time.unscaledTime;
                Rebuild();
            }

            material.SetVector(ParamsId, new Vector4(pixelScale, fadeHiddenZoom, Mathf.Max(fadeVisibleZoom, fadeHiddenZoom + 0.01f), liftAboveTerrain));
            material.SetVector(SizeId, new Vector4(Mathf.Max(fullSizeZoom, 0.01f), Mathf.Clamp01(minSizeFraction), worldPerMapPixel, 0f));
            if (mesh.vertexCount > 0 && !Hidden)
                Graphics.DrawMesh(mesh, Matrix4x4.identity, material, 0);
        }

        // --- Picking and map positions ----------------------------------------------------------

        /// <summary>Terrain surface height at a world position (same formula as the map shader).</summary>
        public float TerrainHeight(Vector3 world)
        {
            if (heightmap == null || !heightmap.isReadable) return 0f;
            float u = (world.x - mapBounds.x) / mapBounds.z;
            float v = (world.z - mapBounds.y) / mapBounds.w;
            return (heightmap.GetPixelBilinear(u, 1f - v).r - 0.5f) * heightScale;
        }

        /// <summary>Province centre on the terrain surface (where its counters stand).</summary>
        public bool TryGetProvincePoint(ushort province, out Vector3 world)
        {
            world = default;
            if (centers == null || !centers.TryGetProvinceCenter(province, out world)) return false;
            world.y = TerrainHeight(world) + liftAboveTerrain;
            return true;
        }

        /// <summary>
        /// The units of the counter under a screen position (topmost first), mirroring the
        /// Hoi4/UnitCounter vertex shader's size and placement. False when none or counters are hidden.
        /// </summary>
        public bool TryPick(Vector2 screen, out IReadOnlyList<ushort> units)
        {
            units = null;
            var cam = Camera.main;
            if (cam == null || stacks.Count == 0 || Hidden) return false;
            float p11 = Mathf.Abs(cam.projectionMatrix[1, 1]);
            // Drawn north to south, so the last hit is the one on top
            for (int i = stacks.Count - 1; i >= 0; i--)
            {
                if (stacks[i].IsBattle) continue;
                if (ScreenRect(stacks[i], cam, p11, out var rect, out _) && rect.Contains(screen))
                {
                    units = stacks[i].Units;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Units of the given country (0 = any) whose counter overlaps a screen rectangle, or whose
        /// province centre lies inside it when counters are hidden at this zoom (box selection).
        /// </summary>
        public List<ushort> UnitsInScreenRect(Rect box, ushort country)
        {
            var result = new List<ushort>();
            var cam = Camera.main;
            if (cam == null) return result;
            float p11 = Mathf.Abs(cam.projectionMatrix[1, 1]);
            foreach (var s in stacks)
            {
                if (s.IsBattle) continue;
                if (country != 0 && s.Country != country) continue;
                bool visible = ScreenRect(s, cam, p11, out var rect, out var anchor);
                bool hit = visible ? rect.Overlaps(box) : anchor.z > 0f && box.Contains(new Vector2(anchor.x, anchor.y));
                if (hit) result.AddRange(s.Units);
            }
            return result;
        }

        /// <summary>
        /// The counters' zoom rules at a world point, for other things written on the map (plan labels): false
        /// where counters are hidden; scale = the counters' size factor (1 at full size, minSizeFraction zoomed
        /// out), alpha = their fade-in.
        /// </summary>
        public bool ZoomRules(Vector3 world, Camera cam, out float scale, out float alpha)
        {
            scale = 1f; alpha = 1f;
            var screen = cam.WorldToScreenPoint(world);
            if (screen.z <= 0f) return false;
            float p11 = Mathf.Abs(cam.projectionMatrix[1, 1]);
            float worldPerScreenPx = 2f * Mathf.Max(screen.z, 1e-3f) / (p11 * Screen.height);
            float zoom = worldPerMapPixel / worldPerScreenPx;
            if (zoom < fadeHiddenZoom) return false;
            scale = Mathf.Clamp(zoom / Mathf.Max(fullSizeZoom, 0.01f), Mathf.Clamp01(minSizeFraction), 1f);
            alpha = Mathf.InverseLerp(fadeHiddenZoom, Mathf.Max(fadeVisibleZoom, fadeHiddenZoom + 0.01f), zoom);
            return true;
        }

        /// <summary>
        /// A counter's screen rectangle (pixels, origin bottom-left), mirroring the Hoi4/UnitCounter
        /// vertex shader's size and placement. False when it is behind the camera or hidden at this zoom.
        /// </summary>
        bool ScreenRect(Stack s, Camera cam, float p11, out Rect rect, out Vector3 anchorScreen)
        {
            rect = default;
            var anchor = s.Anchor;
            anchor.y = TerrainHeight(anchor) + liftAboveTerrain;
            anchorScreen = cam.WorldToScreenPoint(anchor);
            if (anchorScreen.z <= 0f) return false;
            float worldPerScreenPx = 2f * Mathf.Max(anchorScreen.z, 1e-3f) / (p11 * Screen.height);
            float zoom = worldPerMapPixel / worldPerScreenPx;
            if (zoom < fadeHiddenZoom) return false; // counters are hidden at this zoom
            float scale = pixelScale * Mathf.Clamp(zoom / Mathf.Max(fullSizeZoom, 0.01f), Mathf.Clamp01(minSizeFraction), 1f);
            rect = new Rect(anchorScreen.x + s.X0 * scale, anchorScreen.y + (BaseYPx + s.Row * RowSpacingPx + SlotH - TopMargin - CounterH - 1) * scale,
                CounterW * scale, (CounterH + 1) * scale);
            return true;
        }

        // --- Sprites -------------------------------------------------------------------------

        bool LoadSprites()
        {
            plate = LoadSprite("gfx/counters/plate.png");
            selectedFrame = LoadSprite("gfx/counters/selected.png");
            ideologyStrip = LoadSprite("gfx/counters/ideology.png");
            barOrg = LoadSprite("gfx/counters/bar_org.png");
            barStr = LoadSprite("gfx/counters/bar_str.png");
            digits = LoadSprite("gfx/counters/digits.png");
            actionMove = LoadSprite("gfx/counters/action_move.png");
            actionDefend = LoadSprite("gfx/counters/action_defend.png");
            actionAttack = LoadSprite("gfx/counters/action_attack.png");
            noOrders = LoadSprite("gfx/counters/no_orders.png");
            for (int i = 0; i < supplyCrates.Length; i++) supplyCrates[i] = LoadSprite($"gfx/counters/supply_{i + 1}.png");
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
            var byKey = new Dictionary<(uint, string), Stack>();
            var units = gameState.Units;
            // A counter of several divisions with any of them selected opens up into one counter per
            // division (stacked like different templates), so single divisions can be picked out.
            var defended = new HashSet<ushort>();
            foreach (var battle in Hoi4Combat.Battles) defended.Add(battle.To);
            var queue = units.MovementQueue;
            var viewerState = StarterKit.Initializer.Instance?.PlayerState;
            ushort viewer = viewerState != null && viewerState.HasPlayerCountry ? viewerState.PlayerCountryId : (ushort)0;
            var groupSize = new Dictionary<(uint, string), int>();
            var groupSelected = new HashSet<(uint, string)>();
            foreach (ushort id in units.GetAllUnits())
            {
                var u = units.GetUnit(id);
                var d = Hoi4Divisions.Instance != null ? Hoi4Divisions.Instance.Get(id) : null;
                var gk = (((uint)u.provinceID << 16) | u.countryID, d?.MainType ?? "infantry"); // grouped by icon, not template
                groupSize[gk] = groupSize.TryGetValue(gk, out int gn) ? gn + 1 : 1;
                if (Selection != null && Selection.Contains(id)) groupSelected.Add(gk);
            }
            foreach (ushort id in units.GetAllUnits())
            {
                var u = units.GetUnit(id);
                var d = Hoi4Divisions.Instance != null ? Hoi4Divisions.Instance.Get(id) : null;
                string template = d?.MainType ?? "infantry"; // the counter shows the icon, so divisions with the same icon share one
                var groupKey = (((uint)u.provinceID << 16) | u.countryID, template);
                if (groupSize[groupKey] > 1 && groupSelected.Contains(groupKey)) template += "#" + id.ToString("D5");
                var key = (((uint)u.provinceID << 16) | u.countryID, template);
                if (!byKey.TryGetValue(key, out var s))
                {
                    byKey[key] = s = new Stack
                    {
                        Template = template,
                        GroupCount = groupSize[groupKey],
                        Color = gameState.Countries.GetCountryColor(u.countryID),
                        Province = u.provinceID,
                        Country = u.countryID,
                        Tag = gameState.Countries.GetCountryTag(u.countryID),
                    };
                }
                s.Count++;
                int action = Hoi4Combat.Instance != null && Hoi4Combat.Instance.IsAttacking(id) ? 3
                    : defended.Contains(u.provinceID) ? 2
                    : queue != null && queue.IsUnitMoving(id) ? 1 : 0;
                if (action > s.Action) s.Action = action;
                var armyOf = Hoi4Armies.Instance?.ArmyOf(id);
                if (armyOf != null && armyOf.Owner != Hoi4Armies.Player && !Hoi4BattlePlans.ShowAIPlans) armyOf = null; // the AI's armies: not shown
                if (s.ArmyId == 0) s.ArmyId = armyOf?.Id ?? 0;
                if (u.countryID == viewer) s.LowestSupply = Mathf.Min(s.LowestSupply, Hoi4.Supply.Hoi4Supply.Ratio(id).ToFloat());
                // Like HOI4: in an army but on no battle plan, and not following a manual order
                var plans = Hoi4BattlePlans.Instance;
                if (plans != null && armyOf != null && plans.PlanOf(id) == null && !plans.HasManualOrder(id))
                    s.NoOrders = true;
                s.Units.Add(id);
                if (Selection != null && Selection.Contains(id)) s.Selected = true;
                string type = d?.MainType ?? "infantry";
                s.Types[type] = s.Types.TryGetValue(type, out int n) ? n + 1 : 1;
                if (d != null)
                {
                    s.Org += d.Organisation.ToFloat(); s.MaxOrg += d.MaxOrganisation.ToFloat();
                    s.Str += d.Strength.ToFloat(); s.MaxStr += d.MaxStrength.ToFloat();
                }
            }

            var result = new List<Stack>();
            var perProvince = new Dictionary<ushort, List<Stack>>();
            foreach (var s in byKey.Values)
            {
                // HOI4's low supply crate on the player's counters: from LOW_SUPPLY (0.99) yellow, under
                // ALERT_LOW_SUPPLY_LEVEL (0.5) orange, under ALERT_VERY_LOW_SUPPLY_LEVEL (0.2) red
                s.Supply = s.LowestSupply >= 0.99f ? 0 : s.LowestSupply >= 0.5f ? 1 : s.LowestSupply >= 0.2f ? 2 : 3;
                if (!centers.TryGetProvinceCenter(s.Province, out s.Anchor)) continue;
                if (!perProvince.TryGetValue(s.Province, out var list))
                    perProvince[s.Province] = list = new List<Stack>();
                list.Add(s);
                result.Add(s);
            }
            foreach (var list in perProvince.Values)
            {
                // Countries side by side, each country's templates piled up (biggest at the bottom)
                list.Sort((a, b) => a.Country != b.Country ? a.Country.CompareTo(b.Country)
                    : b.GroupCount != a.GroupCount ? b.GroupCount.CompareTo(a.GroupCount) : string.CompareOrdinal(a.Template, b.Template));
                int column = -1, row = 0;
                ushort last = 0;
                var columns = new List<ushort>();
                foreach (var s in list) if (columns.Count == 0 || columns[columns.Count - 1] != s.Country) columns.Add(s.Country);
                foreach (var s in list)
                {
                    if (column < 0 || s.Country != last) { column++; row = 0; last = s.Country; }
                    s.IndexInProvince = column;
                    s.StacksInProvince = columns.Count;
                    s.Row = row++;
                }
            }
            foreach (var b in Hoi4Combat.Battles)
            {
                if (!centers.TryGetProvinceCenter(b.From, out var p0) || !centers.TryGetProvinceCenter(b.To, out var p1)) continue;
                // From the player's side: defending when the player holds the attacked province, attacking when
                // the player holds the attackers' province; other countries' fights stay red
                var playerState = StarterKit.Initializer.Instance?.PlayerState;
                ushort player = playerState != null && playerState.HasPlayerCountry ? playerState.PlayerCountryId : (ushort)0;
                int share = b.AttackerPercent, side = 0;
                if (player != 0 && Hoi4Occupation.ControllerOf(gameState, b.To) == player) { share = 100 - share; side = share >= 50 ? 2 : 1; }
                else if (player != 0 && Hoi4Occupation.ControllerOf(gameState, b.From) == player) side = share >= 50 ? 2 : 1;
                result.Add(new Stack { IsBattle = true, Count = share, Battle = side, Anchor = (p0 + p1) * 0.5f, StacksInProvince = 1 });
            }
            // North first so southern counters draw on top (they are nearer the camera)
            result.Sort((a, b) => b.Anchor.z.CompareTo(a.Anchor.z));
            return result;
        }

        // --- Build ---------------------------------------------------------------------------

        void Rebuild()
        {
            stacks = CollectStacks();
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
            atlasH = atlasHeight;

            var verts = new Vector3[stacks.Count * 4];
            var uvs = new Vector2[stacks.Count * 4];
            var offsets = new Vector2[stacks.Count * 4];
            var tris = new int[stacks.Count * 6];
            for (int i = 0; i < stacks.Count; i++)
            {
                var s = stacks[i];
                int sx = (i % cols) * SlotW, sy = (i / cols) * SlotH; // slot top-left, row 0 = top
                ComposeCached(s, sx, sy); // the counter itself starts LeftMargin into the slot

                // Atlas uv (texture rows are bottom-up)
                float u0 = (float)sx / AtlasWidth, u1 = (float)(sx + SlotW) / AtlasWidth;
                float v1 = 1f - (float)sy / atlasHeight, v0 = 1f - (float)(sy + SlotH) / atlasHeight;

                // Offsets in counter pixels from the anchor (x right, y up), stacks side by side
                float shift = (s.IndexInProvince - (s.StacksInProvince - 1) * 0.5f) * StackSpacingPx;
                float x0 = -BodyW * 0.5f - LeftMargin + shift, x1 = BodyW * 0.5f + shift;
                s.X0 = -BodyW * 0.5f + shift; s.X1 = x1; // picking: the counter, not the mark strip
                float y0 = BaseYPx + s.Row * RowSpacingPx, y1 = y0 + SlotH; // bottom edge on the anchor (shader stands it up)

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

            atlas.SetPixelData(atlasPixels, 0);
            atlas.Apply(false);

            mesh.Clear();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.uv2 = offsets;
            mesh.triangles = tris;
            // Vertices sit at the anchors; the shader moves them in screen space, so never cull
            mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1e6f);

            if (GameSettings.Instance?.ShouldLog(LogLevel.Debug) ?? false) // every rebuild: several a second in a war
                ArchonLogger.Log($"Hoi4UnitCounters: {stacks.Count} counters, atlas {AtlasWidth}x{atlasHeight}", "game_units");
        }

        CounterKey Signature(Stack s)
        {
            if (s.IsBattle) return new CounterKey(true, null, 0, null, s.Battle, 0, s.Count, 0, 0, false, false, 0);
            string type = "infantry";
            int best = -1;
            foreach (var kv in s.Types)
                if (kv.Value > best) { best = kv.Value; type = kv.Key; }
            int org = Mathf.RoundToInt(barOrg.Width * Mathf.Clamp01(s.MaxOrg > 0 ? s.Org / s.MaxOrg : 1f));
            int str = Mathf.RoundToInt(barStr.Width * Mathf.Clamp01(s.MaxStr > 0 ? s.Str / s.MaxStr : 1f));
            return new CounterKey(false, s.Tag, s.Color.r << 16 | s.Color.g << 8 | s.Color.b, type, s.Action, s.ArmyId,
                                  s.Count, org, str, s.Selected, s.NoOrders, s.Supply);
        }

        void ComposeCached(Stack s, int ox, int oy)
        {
            var key = Signature(s);
            int h = atlasH;
            if (composed.TryGetValue(key, out var block))
            {
                for (int y = 0; y < SlotH; y++)
                    Array.Copy(block, y * SlotW, atlasPixels, (h - 1 - (oy + y)) * AtlasWidth + ox, SlotW);
                return;
            }
            if (s.IsBattle) ComposeBattle(s, ox + LeftMargin, oy + TopMargin);
            else ComposeCounter(s, ox + LeftMargin, oy + TopMargin);
            if (composed.Count > 5000) composed.Clear();
            block = new Color32[SlotW * SlotH];
            for (int y = 0; y < SlotH; y++)
                Array.Copy(atlasPixels, (h - 1 - (oy + y)) * AtlasWidth + ox, block, y * SlotW, SlotW);
            composed[key] = block;
        }

        void ComposeCounter(Stack s, int ox, int oy)
        {
            if (s.IsBattle) { ComposeBattle(s, ox, oy); return; }
            // mapicons.gui no_orders: left of the plate (x -10, y 8), in the slot's margin strip
            if (s.NoOrders && noOrders != null) Blit(noOrders, 0, 0, noOrders.Width, noOrders.Height, ox - 10, oy + 8);
            // Layout from HOI4 interface/mapicons.gui (unit_counter)
            BlitPlate(s.Color, ox, oy);
            // Dark box behind the icon, flag and bars (the count box on the right is part of the plate)
            DarkBox(ox + 1, oy + 1, 32, 24);
            if (s.Selected && selectedFrame != null)
                Blit(selectedFrame, 0, 0, selectedFrame.Width, selectedFrame.Height, ox, oy);

            var flag = s.Tag != null ? Flag(s.Tag) : null;
            if (flag != null) Blit(flag, 0, 0, flag.Width, flag.Height, ox + 5, oy + 15);

            string type = "infantry";
            int best = -1;
            foreach (var kv in s.Types)
                if (kv.Value > best) { best = kv.Value; type = kv.Key; }
            var icon = Icon(type) ?? Icon("infantry");
            if (icon != null) Blit(icon, 0, 0, icon.Width, icon.Height, ox + 3, oy + 2);

            if (s.Action >= 2 && s.ArmyId != 0)
            {
                // The strip shows which army the division belongs to, not what it is doing
                var army = Hoi4Armies.ColorOf(s.ArmyId);
                var fill = (Color32)army;
                var edgeCol = (Color32)(army * 0.4f);
                for (int sy = 1; sy < CounterH - 2; sy++)
                    for (int sx = 57; sx < CounterW - 1; sx++)
                    {
                        bool edge = sx == 57 || sy == 1 || sy == CounterH - 3 || sx == CounterW - 2;
                        float shade = 1f - 0.3f * (sy / (float)CounterH);
                        Over(ox + sx, oy + sy, edge ? edgeCol
                            : new Color32((byte)(fill.r * shade), (byte)(fill.g * shade), (byte)(fill.b * shade), 255));
                    }
            }
            var action = s.Action == 3 ? actionAttack : s.Action == 2 ? actionDefend : s.Action == 1 ? actionMove : null;
            if (action != null) BlitScaled(action, 9, ox + 57, oy + 9); // fits the strip right of the count box

            // mapicons.gui low_supply: a crate over the top edge (x 24, y -4, scale 0.75), into the slot's top strip
            var crate = s.Supply > 0 ? supplyCrates[s.Supply - 1] : null;

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
            if (crate != null) BlitScaled(crate, Mathf.RoundToInt(crate.Width * 0.75f), ox + 24, oy - 4);
        }

        // HOI4's battle burst: a red disc with the attackers' chance in it, standing on the border
        void ComposeBattle(Stack s, int ox, int oy)
        {
            int cx = ox + BodyW / 2, cy = oy + CounterH / 2 + 1;
            const int r = 12;
            for (int y = -r; y <= r; y++)
                for (int x = -r; x <= r; x++)
                {
                    float d = Mathf.Sqrt(x * x + y * y);
                    if (d > r + 0.5f) continue;
                    Over(cx + x, cy + y, s.Battle == 2
                        ? (d > r - 2f ? new Color32(24, 100, 28, 255) : new Color32(52, 170, 58, 255))
                        : (d > r - 2f ? new Color32(120, 18, 16, 255) : new Color32(205, 42, 36, 255)));
                }
            string text = s.Count.ToString();
            const int advance = 7;
            int textW = (text.Length - 1) * advance + digitW;
            int tx = cx - textW / 2 + 1, ty = cy - digitH / 2 + 1; // +1: the disc's centre is one pixel right and below the glyph cell's
            // Dark box behind the number, like the count box of a division counter
            bool win = s.Battle == 2;
            int boxW = Mathf.Min(textW + 2, 19) | 1, boxH = digitH; // odd width so it centres on the disc; stays inside it
            DarkBox(cx - boxW / 2, ty, boxW, boxH,
                win ? new Color32(10, 38, 12, 255) : new Color32(46, 8, 7, 255),
                win ? new Color32(4, 20, 6, 255) : new Color32(24, 4, 4, 255));
            foreach (char c in text)
            {
                Blit(digits, (c - '0') * digitW, 0, digitW, digitH, tx, ty, new Color32(255, 245, 235, 255));
                tx += advance;
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

        /// <summary>
        /// The plate in the country's colour: the plate's grey shading modulates the colour (keeping its
        /// hue, unlike a per-channel multiply that clips), and its dark parts become a dark shade of it.
        /// </summary>
        void BlitPlate(Color32 col, int dx, int dy)
        {
            for (int y = 0; y < CounterH && y < plate.Height; y++)
                for (int x = 0; x < CounterW && x < plate.Width; x++)
                {
                    var c = plate.Pixels[y * plate.Width + x];
                    if (c.a == 0) continue;
                    float lum = (c.r + c.g + c.b) / 3f;
                    float k = lum / 135f; // the plate's average grey (~105) maps to ~80 % of the plain colour
                    float r = col.r * k, g = col.g * k, b = col.b * k;
                    float m = Mathf.Max(r, Mathf.Max(g, b));
                    if (m > 255f) { float f = 255f / m; r *= f; g *= f; b *= f; }
                    if (lum < 80f) { r = col.r * 0.22f; g = col.g * 0.22f; b = col.b * 0.22f; } // count box and outline: a dark shade of the colour
                    Over(dx + x, dy + y, new Color32((byte)r, (byte)g, (byte)b, c.a));
                }
        }

        // Box-filtered downscale of a square sprite to size x size pixels
        void BlitScaled(Sprite src, int size, int dx, int dy)
        {
            float step = (float)src.Width / size;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float r = 0, g = 0, b = 0, a = 0; int n = 0;
                    for (int sy = (int)(y * step); sy < Mathf.Min(src.Height, Mathf.CeilToInt((y + 1) * step)); sy++)
                        for (int sx = (int)(x * step); sx < Mathf.Min(src.Width, Mathf.CeilToInt((x + 1) * step)); sx++)
                        {
                            var c = src.Pixels[sy * src.Width + sx];
                            r += c.r * c.a; g += c.g * c.a; b += c.b * c.a; a += c.a; n++;
                        }
                    if (n == 0 || a <= 0f) continue;
                    Over(dx + x, dy + y, new Color32((byte)(r / a), (byte)(g / a), (byte)(b / a), (byte)(a / n)));
                }
        }

        void DarkBox(int x0, int y0, int w, int h, Color32? fillColor = null, Color32? edgeColor = null)
        {
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    bool edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                    bool corner = (x == 0 || x == w - 1) && (y == 0 || y == h - 1);
                    if (corner) continue;
                    Over(x0 + x, y0 + y, edge ? (edgeColor ?? new Color32(30, 28, 26, 255)) : (fillColor ?? new Color32(64, 61, 57, 255)));
                }
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
            int h = atlasH;
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
