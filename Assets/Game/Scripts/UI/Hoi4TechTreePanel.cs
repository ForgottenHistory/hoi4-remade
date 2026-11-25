using System.Collections;
using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using Hoi4.Research;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's tech tree screen (interface/countrytechtreeview.gui), full screen under the top bar. Opened by
    /// clicking a slot of the research screen; a tech picked here goes into that slot (SetResearchCommand, like the AI).
    /// Frame: Resources/Hoi4UI/TechTreePanel.uxml + TechTreePanelStyle.uss. Folder content from the converter's
    /// gfx/interface/techtree/techtree.json5, laid out as in the prototype (Tools/prototypes/research/render_techtree.py):
    /// - Techs sit on the gridbox of their first ancestor that has one (<root>_tree); position = gridbox + the tech's
    ///   folder x, y in slots (format LEFT: y across, x down; else x across, y down).
    /// - Techs unlocking equipment are large cards with the country's short design name, the rest small icons; the
    ///   picture is the country's own where it has one.
    /// - Background by state: researched, available, researching (9-frame chevron strip, 15 fps), unavailable.
    /// - Links run through slot centres: one slot along y, then along x, then along y; coloured by the child's state.
    /// Folders are built once per country and folder; states refresh four times a real second and the strip animates
    /// on real time (presentation throttles, not game time).
    /// </summary>
    public sealed class Hoi4TechTreePanel : MonoBehaviour
    {
        const string Dir = "gfx/interface/techtree/";
        const float RefreshSeconds = 0.25f;
        const float StripFps = 15f;            // GFX_technology_currently_researching_item_bg animation_rate_fps
        const string DefaultFolder = "infantry_folder";

        public static Hoi4TechTreePanel Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance.open;
        /// <summary>Frame the tree last closed on Escape (the research screen leaves that Escape alone).</summary>
        public static int EscapeFrame { get; private set; } = -1;

        sealed class Item
        {
            public int Tech;
            public VisualElement Root, Bg;
            public Dictionary<string, string[]> Frames;
            public string State;
            public int Frame = -1;
        }

        sealed class Line
        {
            public int Child;
            public readonly List<VisualElement> Pieces = new List<VisualElement>();
            public int Frame = -1;
        }

        sealed class Folder
        {
            public VisualElement Content;
            public float Width;                                   // the content's own width (techtree_stripes)
            public float Height;
            /// <summary>Elements anchored to the content's right edge (artillery_year_right) and their x there.</summary>
            public readonly List<(VisualElement e, float x)> RightAnchored = new List<(VisualElement, float)>();
            public readonly List<Item> Items = new List<Item>();
            public readonly List<Line> Lines = new List<Line>();
        }

        UIDocument document;
        VisualElement panel, tabs;
        ScrollView scroll;
        JObject layout;
        readonly Dictionary<string, Folder> built = new Dictionary<string, Folder>();
        readonly Dictionary<string, VisualElement> tabButtons = new Dictionary<string, VisualElement>();
        readonly List<string> folderOrder = new List<string>();
        string folder = DefaultFolder;
        ushort builtFor;
        int targetSlot;
        bool open;
        float nextRefresh;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4TechTreePanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4TechTreePanel>();
        }

        void Awake() => Instance = this;

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null || !DataFileResolver.IsInitialized)
                yield return null;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/TechTreePanel");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/TechTreePanelStyle");
            string path = DataFileResolver.Resolve(Dir + "techtree.json5");
            if (tree == null || theme == null || style == null || !File.Exists(path))
            {
                ArchonLogger.LogWarning("Hoi4TechTreePanel: TechTreePanel.uxml, its stylesheets or " + Dir +
                                        "techtree.json5 (run Tools/hoi4_convert.py) is missing, tech tree disabled", "game_ui");
                yield break;
            }
            layout = Json5Loader.LoadJson5File(path);

            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4TechTreePanel: no UIDocument panel settings in the scene, tech tree disabled", "game_ui");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 46; // over the research screen, under the top bar
            yield return null;

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            panel = tree.Instantiate().Q("techtree-panel");
            Hoi4Sprites.Bind(panel);
            root.Add(panel);
            tabs = panel.Q("tt-tabs");
            scroll = panel.Q<ScrollView>("tt-scroll");
            scroll.mouseWheelScrollSize = 40f; // scroll_wheel_factor
            Hoi4Scrollbar.Apply(scroll);
            scroll.contentViewport.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                if (built.TryGetValue(folder, out var shown)) Fit(shown);
            });
            panel.Q("tt-close").RegisterCallback<ClickEvent>(_ => Close());

            if (layout["folders"] is JObject folders)
                foreach (var f in folders.Properties())
                {
                    if (!(f.Value["tab"] is JObject tab)) continue;
                    string name = f.Name;
                    folderOrder.Add(name);
                    var b = new VisualElement { pickingMode = PickingMode.Position };
                    b.AddToClassList("tt-tab");
                    b.AddToClassList("hoi4-button");
                    b.style.left = (float)tab["x"];
                    b.style.top = (float)tab["y"];
                    b.RegisterCallback<ClickEvent>(_ => ShowFolder(name));
                    tabs.Add(b);
                    tabButtons[name] = b;
                }
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            Hoi4UiHover.Remove(this);
        }

        static ushort Player
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        // ---- open / close -----------------------------------------------------------------------------

        /// <summary>Open on a research slot: a pick goes into it. Shows its tech's folder, else the last one.</summary>
        public void Open(int slot)
        {
            var research = Hoi4Research.Instance;
            if (panel == null || Player == 0 || research == null || !research.IsReady) return;
            targetSlot = slot;
            var slots = research.Slots(Player);
            if (slot >= 0 && slot < slots.Count && !slots[slot].Empty && research.Tech(slots[slot].Technology)?.Folder is string f
                && tabButtons.ContainsKey(f))
                folder = f;
            open = true;
            panel.style.display = DisplayStyle.Flex;
            ShowFolder(folder);
        }

        public void Close()
        {
            open = false;
            if (panel != null) panel.style.display = DisplayStyle.None;
        }

        void ShowFolder(string name)
        {
            if (!tabButtons.ContainsKey(name)) name = folderOrder.Count > 0 ? folderOrder[0] : name;
            folder = name;
            foreach (var kv in tabButtons)
                Hoi4Sprites.Apply(kv.Value, Dir + Frame(layout["folders"][kv.Key]["tab"], kv.Key == folder ? 0 : 1));
            if (builtFor != Player) { foreach (var b in built.Values) b.Content.RemoveFromHierarchy(); built.Clear(); builtFor = Player; }
            if (!built.TryGetValue(folder, out var fo)) built[folder] = fo = Build(folder);
            scroll.Clear();
            scroll.Add(fo.Content);
            scroll.scrollOffset = Vector2.zero;
            Fit(fo);
            nextRefresh = 0f;
        }

        static string Frame(JToken sprite, int i)
        {
            var frames = sprite["frames"] as JArray;
            return frames != null && frames.Count > 0 ? (string)frames[Mathf.Clamp(i, 0, frames.Count - 1)] : (string)sprite["image"];
        }

        // ---- building a folder --------------------------------------------------------------------------

        Folder Build(string name)
        {
            var research = Hoi4Research.Instance;
            var gameState = Engine.ArchonEngine.Instance.GameState;
            string tag = gameState.Countries.GetCountryTag(Player);
            var fj = (JObject)layout["folders"][name];
            var items = layout["items"][name] as JObject;
            var fo = new Folder { Content = new VisualElement { pickingMode = PickingMode.Ignore } };
            var content = fo.Content;
            content.AddToClassList("tt-content");
            float width = (float)fj["width"], height = (float)fj["height"];

            foreach (var im in (JArray)fj["images"])
            {
                var e = new VisualElement { pickingMode = PickingMode.Ignore };
                e.AddToClassList("tt-abs");
                Place(e, (float)im["x"], (float)im["y"], (float)im["w"], (float)im["h"]);
                Hoi4Sprites.Apply(e, Dir + (string)im["image"]);
                if (RightAnchored(im)) fo.RightAnchored.Add((e, (float)im["x"]));
                content.Add(e);
            }
            foreach (var lab in (JArray)fj["labels"])
            {
                var l = new Label((string)lab["text"]) { pickingMode = PickingMode.Ignore };
                l.AddToClassList("hoi4-label");
                l.AddToClassList("tt-abs");
                l.AddToClassList((bool)lab["header"] ? "tt-year" : "tt-subtitle");
                l.style.left = (float)lab["x"];
                l.style.top = (float)lab["y"];
                content.Add(l);
                if (RightAnchored(lab)) fo.RightAnchored.Add((l, (float)lab["x"]));
                else width = Mathf.Max(width, (float)lab["x"] + 80f);
            }

            // place the folder's techs on their gridboxes
            var grids = new Dictionary<string, JObject>();
            foreach (JObject g in (JArray)fj["grids"]) grids[(string)g["root"]] = g;
            var techs = research.Technologies;
            var placed = new Dictionary<int, (float sx, float sy, JObject grid, float x, float y)>();
            var on = new Dictionary<int, (JObject grid, float x, float y)>();
            var leftmost = new Dictionary<JObject, float>();
            for (int i = 0; i < techs.Count; i++)
            {
                var t = techs[i];
                int at = t.Places.FindIndex(p => p.folder == name);
                if (at < 0) continue;
                var grid = GridOf(t, name, grids, techs);
                if (grid == null) continue;
                var (_, x, y) = t.Places[at];
                on[i] = (grid, x, y);
                // a gridbox (other than LEFT) starts its tree at its leftmost column: artillery's run from x -9 to 7
                if ((string)grid["format"] != "LEFT")
                    leftmost[grid] = Mathf.Min(leftmost.TryGetValue(grid, out float l) ? l : 0f, x);
            }
            foreach (var kv in on)
            {
                var (grid, x, y) = kv.Value;
                x -= leftmost.TryGetValue(grid, out float l) ? l : 0f;
                var (sx, sy) = SlotCorner(grid, x, y);
                placed[kv.Key] = (sx, sy, grid, x, y);
            }

            // links first, under the items
            foreach (var kv in placed)
            {
                var (_, _, grid, x, y) = kv.Value;
                foreach (int child in techs[kv.Key].Children)
                {
                    if (!placed.TryGetValue(child, out var c) || c.grid != grid) continue;
                    var line = new Line { Child = child };
                    var pts = new List<(float, float)> { (x, y) };
                    if (x != c.x && y != c.y)
                    {
                        float step = y + (c.y > y ? 1 : -1);
                        pts.Add((x, step));
                        pts.Add((c.x, step));
                    }
                    pts.Add((c.x, c.y));
                    for (int p = 1; p < pts.Count; p++)
                    {
                        if (pts[p - 1] == pts[p]) continue;
                        var a = SlotCentre(grid, pts[p - 1].Item1, pts[p - 1].Item2);
                        var b = SlotCentre(grid, pts[p].Item1, pts[p].Item2);
                        var e = new VisualElement { pickingMode = PickingMode.Ignore };
                        e.AddToClassList("tt-line");
                        bool across = Mathf.Approximately(a.y, b.y);
                        var piece = layout["lines"][across ? "line_left" : "line_up"];
                        float pw = (float)piece["w"], ph = (float)piece["h"];
                        if (across)
                            Place(e, Mathf.Min(a.x, b.x) - 8f, a.y - ph / 2f, Mathf.Abs(b.x - a.x) + 16f, ph);
                        else
                            Place(e, a.x - pw / 2f, Mathf.Min(a.y, b.y) - 8f, pw, Mathf.Abs(b.y - a.y) + 16f);
                        e.userData = across ? "line_left" : "line_up";
                        line.Pieces.Add(e);
                        content.Add(e);
                    }
                    fo.Lines.Add(line);
                }
            }

            foreach (var kv in placed)
            {
                var t = techs[kv.Key];
                var (sx, sy, _, _, _) = kv.Value;
                string size = (t.Equipment.Count > 0 || items["small"] == null) && items["large"] != null ? "large" : "small";
                var it = (JObject)items[size];
                var item = new Item { Tech = kv.Key, Frames = new Dictionary<string, string[]>() };
                foreach (var st in ((JObject)it["bg"]).Properties())
                {
                    var frames = (JArray)st.Value["frames"];
                    var list = new string[frames.Count];
                    for (int f = 0; f < list.Length; f++) list[f] = Dir + (string)frames[f];
                    item.Frames[st.Name] = list;
                }
                var root = new VisualElement { pickingMode = PickingMode.Position };
                root.AddToClassList("tt-item");
                float ix = sx + (float)it["x"], iy = sy + (float)it["y"];
                var bgInfo = it["bg"]["unavailable"];   // the background's own size (industry: a 72 x 72 icon in a 204 wide item)
                float bw = bgInfo != null ? (float)bgInfo["w"] : (float)it["w"], bh = bgInfo != null ? (float)bgInfo["h"] : (float)it["h"];
                Place(root, ix, iy, bw, bh);
                var bg = new VisualElement { pickingMode = PickingMode.Ignore };
                bg.AddToClassList("tt-item-bg");
                bg.style.width = bw;
                bg.style.height = bh;
                root.Add(bg);
                item.Root = root;
                item.Bg = bg;

                var tex = Hoi4UiAssets.Texture($"{Dir}icons/{tag}_{t.Id}.png") ?? Hoi4UiAssets.Texture($"{Dir}icons/{t.Id}.png");
                if (tex != null && it["icon"] is JObject ic)
                {
                    var icon = new VisualElement { pickingMode = PickingMode.Ignore };
                    icon.AddToClassList("tt-item-icon");
                    float ixo = (float)ic["x"], iyo = (float)ic["y"];
                    if ((bool)ic["center"]) { ixo -= tex.width / 2f; iyo -= tex.height / 2f; }
                    Place(icon, ixo, iyo, tex.width, tex.height);
                    icon.style.backgroundImage = new StyleBackground(tex);
                    root.Add(icon);
                }
                if (t.Equipment.Count > 0 && it["name"] is JObject nm)
                {
                    string text = t.EquipmentNames != null && tag != null && t.EquipmentNames.TryGetValue(tag, out string own) ? own
                        : t.EquipmentName ?? t.Name;
                    var l = new Label(text) { pickingMode = PickingMode.Ignore };
                    l.AddToClassList("hoi4-label");
                    l.AddToClassList("tt-item-name");
                    Place(l, (float)nm["x"] + 4f, (float)nm["y"] + 2f, (float)nm["w"], (float)nm["h"]);
                    root.Add(l);
                }
                int techIndex = kv.Key;
                root.RegisterCallback<ClickEvent>(_ => Pick(techIndex));
                fo.Items.Add(item);
                content.Add(root);
                width = Mathf.Max(width, ix + bw + 20f);
                height = Mathf.Max(height, iy + bh + 20f);
            }
            fo.Width = width;
            fo.Height = height;
            return fo;
        }

        /// <summary>Whether the converter found it anchored to the content's right edge (ax 1). Centre-anchored ones
        /// (ax 0.5, artillery_subtitles) stay where they are at the content's own width: there they sit over their
        /// trees, which don't move.</summary>
        static bool RightAnchored(JToken e) => e["ax"] != null && (float)e["ax"] >= 0.99f;

        /// <summary>The content is its own size or the visible folder, whichever is larger (min = 100%); right-anchored
        /// labels follow its right edge.</summary>
        void Fit(Folder fo)
        {
            float view = scroll.contentViewport.resolvedStyle.width;
            float w = float.IsNaN(view) ? fo.Width : Mathf.Max(fo.Width, view);
            fo.Content.style.width = w;
            fo.Content.style.height = fo.Height;
            foreach (var (e, x) in fo.RightAnchored) e.style.left = x + (w - fo.Width);
        }

        static void Place(VisualElement e, float x, float y, float w, float h)
        {
            e.style.left = x;
            e.style.top = y;
            e.style.width = w;
            e.style.height = h;
        }

        /// <summary>The gridbox of the tech's first ancestor (through leads_to, inside the folder) that has one.</summary>
        static JObject GridOf(TechDef t, string folder, Dictionary<string, JObject> grids, IReadOnlyList<TechDef> techs)
        {
            var seen = new HashSet<int>();
            var todo = new Queue<TechDef>();
            todo.Enqueue(t);
            while (todo.Count > 0)
            {
                var m = todo.Dequeue();
                if (!seen.Add(m.Index)) continue;
                if (grids.TryGetValue(m.Id, out var g)) return g;
                foreach (int p in m.Parents)
                    if (techs[p].Places.Exists(pl => pl.folder == folder)) todo.Enqueue(techs[p]);
            }
            return null;
        }

        static (float, float) SlotCorner(JObject g, float x, float y)
        {
            float gx = (float)g["x"], gy = (float)g["y"], w = (float)g["w"], h = (float)g["h"];
            return (string)g["format"] == "LEFT" ? (gx + y * w, gy + x * h) : (gx + x * w, gy + y * h);
        }

        static Vector2 SlotCentre(JObject g, float x, float y)
        {
            var (sx, sy) = SlotCorner(g, x, y);
            return new Vector2(sx + (float)g["w"] / 2f, sy + (float)g["h"] / 2f);
        }

        // ---- picking ------------------------------------------------------------------------------------

        void Pick(int tech)
        {
            var research = Hoi4Research.Instance;
            ushort player = Player;
            if (research == null || player == 0) return;
            int slot = targetSlot;
            var slots = research.Slots(player);
            if (slot < 0 || slot >= slots.Count)
            {
                slot = -1;
                for (int i = 0; i < slots.Count; i++) if (slots[i].Empty) { slot = i; break; }
            }
            if (slot < 0 || !research.CanSet(player, slot, tech)) return;
            Engine.ArchonEngine.Instance.GameState.TryExecuteCommand(
                new SetResearchCommand { CountryId = player, Slot = slot, Tech = tech }, out _);
            nextRefresh = 0f;
        }

        // ---- update -------------------------------------------------------------------------------------

        void Update()
        {
            if (panel == null) return;
            var research = Hoi4Research.Instance;
            if (Player == 0 || research == null || !research.IsReady || Hoi4.War.Hoi4War.Instance?.Conference != null)
            {
                if (open) Close();
                return;
            }
            if (open && global::Utils.KeyboardFocus.KeyDown(KeyCode.Escape))
            {
                EscapeFrame = Time.frameCount;
                Close();
            }
            Hoi4UiHover.Report(this, open ? panel : null);
            if (!open || !built.TryGetValue(folder, out var fo)) return;

            if (Time.unscaledTime >= nextRefresh)
            {
                nextRefresh = Time.unscaledTime + RefreshSeconds;
                Refresh(research, fo);
            }
            // the researching strip: 9 frames at 15 fps, real time (presentation only)
            int frame = Mathf.FloorToInt(Time.unscaledTime * StripFps);
            foreach (var item in fo.Items)
                if (item.Frames.TryGetValue(item.State ?? "", out var frames) && frames.Length > 1) SetFrame(item, frames, frame % frames.Length);
        }

        void Refresh(Hoi4Research research, Folder fo)
        {
            ushort player = Player;
            foreach (var item in fo.Items)
            {
                string state = State(research, player, item.Tech);
                item.Root.EnableInClassList("clickable", state == "available");
                if (state == item.State) continue;
                item.State = state;
                item.Frame = -1;
                var frames = item.Frames.TryGetValue(state, out var f) ? f : item.Frames["unavailable"];
                SetFrame(item, frames, 0);
            }
            foreach (var line in fo.Lines)
            {
                string state = State(research, player, line.Child);
                int frame = state == "researched" ? 3 : state == "unavailable" ? 1 : 2; // line frames: 1 grey, 2 tan, 3 green
                if (frame == line.Frame) continue;
                line.Frame = frame;
                foreach (var e in line.Pieces) Hoi4Sprites.Apply(e, Dir + Frame(layout["lines"][(string)e.userData], frame));
            }
        }

        static void SetFrame(Item item, string[] frames, int i)
        {
            if (item.Frame == i) return;
            item.Frame = i;
            Hoi4Sprites.Apply(item.Bg, frames[i]);
        }

        static string State(Hoi4Research research, ushort country, int tech)
        {
            if (research.Known(country, tech)) return "researched";
            if (research.SlotOf(country, tech) >= 0) return "currently_researching";
            return research.CanResearch(country, tech) ? "available" : "unavailable";
        }
    }
}
