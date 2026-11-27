using System.Collections;
using System.Collections.Generic;
using System.IO;
using Core;
using Core.Modding;
using Hoi4.Scripts;
using Scripting;
using UnityEngine;

namespace Hoi4.Focus
{
    /// <summary>One national focus (a focus { ... } of common/focus/*.lua).</summary>
    public sealed class FocusDef
    {
        public int Index;                // in Hoi4Focus.Focuses: the same on every machine (files and focuses load in a fixed order)
        public string Id;                // industrial_effort
        public string Icon;              // GFX_goal_generic_production
        public int Tree = -1;            // the tree it was written in; -1: a shared or joint focus (trees list those they use)
        public int X, Y;                 // grid position in the tree, with relative_to worked in
        public int Days;                 // cost x FOCUS_POINT_DAYS (7)
        public int[][] Prerequisites = System.Array.Empty<int[]>();  // every group needs one of its focuses done
        public int[] Exclusive = System.Array.Empty<int>();          // mutually exclusive: none of these may be done
        public string[] Filters = System.Array.Empty<string>();
        public ScriptFunctionRef Available, Bypass, AllowBranch;     // triggers of the country (not valid: none, holds)
        public ScriptFunctionRef Reward, OnSelect, OnBypass;         // effects
        public ScriptFunctionRef Ai;                                 // the AI's weight, when it is a function
        public double AiBase = 1;                                    // and when it is a number (1 when the focus has none)

        // as written, until every file is loaded
        internal int RawX, RawY;
        internal string RelativeTo;
        internal string[][] PrerequisiteIds;
        internal string[] ExclusiveIds;
        internal bool Placed;
    }

    /// <summary>A focus tree (a focus_tree { ... } and the focuses after it, plus the shared focuses it lists).</summary>
    public sealed class FocusTree
    {
        public int Index;
        public string Id;                                       // generic_focus
        public bool Default;                                    // the tree of a country no other tree wants
        public readonly List<int> Focuses = new List<int>();    // its own, then the shared ones it uses
        public ScriptFunctionRef Weight;                        // of a country: the tree with the highest is the country's
        public double WeightBase;
        internal string[] SharedIds = System.Array.Empty<string>();
        internal readonly Dictionary<string, int> ById = new Dictionary<string, int>();
    }

    /// <summary>
    /// GAME: HOI4 national focuses (NDefines.NFocus). The trees are Lua scripts (common/focus/*.lua, converted from
    /// common/national_focus by Tools/hoi4_focus.py) run in the game's script world (Hoi4Script): a file's
    /// focus_tree { ... } and focus { ... } calls register here, and each focus keeps its Lua functions.
    /// - Tree: a country's tree is the one whose weight is highest for it (its own tree weighs 10 for its tag,
    ///   the generic tree 1 for everyone). Variants of a tree for different DLC both load; has_dlc is always true,
    ///   so the newest wins as HOI4's weights have it. Chosen once, at the start.
    /// - A focus takes cost x FOCUS_POINT_DAYS (7) days, one day of progress a day. A country works on one at a
    ///   time; with none, it saves up to MAX_SAVED_FOCUS_PROGRESS (10) days, spent at once on the next focus.
    /// - It can be started when it isn't done, every prerequisite group has one focus done, no mutually exclusive
    ///   focus is done, and its allow_branch and available triggers hold. Picking another focus while one runs
    ///   drops the running one's progress.
    /// - Starting runs on_select. A focus whose bypass trigger holds when it is picked is done at once, with
    ///   on_bypass and no reward; a bypass that asks something the game lacks never fires (Hoi4Script.TestSure).
    ///   Finishing runs the reward.
    /// - Scripts call what the game has (Hoi4Script.Country.cs, Hoi4Script.State.cs); a trigger or effect the game
    ///   lacks is reported once and does nothing (a trigger: false). So a focus whose `available` asks something
    ///   the game doesn't know stays unavailable, and a reward does the parts the game has.
    /// - Not modelled yet: cancel triggers and cancel_if_invalid, available_if_capitulated, continue_if_invalid,
    ///   joint focuses (loaded, in no tree), focus speed modifiers, tree changes in play (civil wars, releases).
    /// Changes come only through SelectFocusCommand and the daily tick (Hoi4Simulation).
    /// </summary>
    public sealed class Hoi4Focus : MonoBehaviour
    {
        const string FocusDir = "common/focus";
        const string Channel = "game_focus";
        public const int PointDays = 7;          // NFocus.FOCUS_POINT_DAYS
        public const int MaxSavedDays = 10;      // NFocus.MAX_SAVED_FOCUS_PROGRESS
        const int FilesPerFrame = 4;             // loading is spread over frames (80 files, about 12 MB of Lua)

        public static Hoi4Focus Instance { get; private set; }
        public bool IsReady { get; private set; }

        /// <summary>A country's focus changed: started, finished, dropped.</summary>
        public event System.Action<ushort> Changed;
        /// <summary>A country finished a focus (country, focus index; bypassed ones too).</summary>
        public event System.Action<ushort, int> Completed;

        sealed class CountryFocus
        {
            public int Tree = -1;
            public readonly HashSet<int> Done = new HashSet<int>();
            public int Current = -1;
            public int Progress;     // days on Current
            public int Saved;        // days saved with no focus, up to MaxSavedDays
        }

        GameState gameState;
        readonly List<FocusDef> focuses = new List<FocusDef>();
        readonly List<FocusTree> trees = new List<FocusTree>();
        readonly Dictionary<string, int> shared = new Dictionary<string, int>();     // shared and joint focuses by id
        readonly SortedDictionary<ushort, CountryFocus> countries = new SortedDictionary<ushort, CountryFocus>();
        static readonly CountryFocus None = new CountryFocus();
        FocusTree loadingTree;       // the tree focus { } calls go to while a file runs

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Focus");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Focus>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        IEnumerator Start()
        {
            // scripts ask about research, ideas, buildings and factories: those systems first
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || !DataFileResolver.IsInitialized
                   || Hoi4.Research.Hoi4Research.Instance == null || !Hoi4.Research.Hoi4Research.Instance.IsReady
                   || Hoi4.Construction.Hoi4Construction.Instance == null || !Hoi4.Construction.Hoi4Construction.Instance.IsReady)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            while (!Hoi4Script.Initialize(gameState)) yield return null;

            var engine = Hoi4Script.Engine;
            engine.SetFunction("focus_tree", (ref ScriptCall call) => AddTree(call.Table(0)));
            engine.SetFunction("focus", (ref ScriptCall call) => AddFocus(call.Table(0), loadingTree));
            engine.SetFunction("shared_focus", (ref ScriptCall call) => AddFocus(call.Table(0), null));
            engine.SetFunction("joint_focus", (ref ScriptCall call) => AddFocus(call.Table(0), null));

            string dir = DataFileResolver.Resolve(FocusDir);
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.lua") : System.Array.Empty<string>();
            System.Array.Sort(files, System.StringComparer.Ordinal);     // the load order numbers the focuses
            if (files.Length == 0)
                ArchonLogger.LogWarning($"Hoi4Focus: no {FocusDir}/*.lua (run Tools/hoi4_convert.py); no focus trees", Channel);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int failed = 0;
            for (int i = 0; i < files.Length; i++)
            {
                loadingTree = null;
                if (!engine.RunFile(files[i])) failed++;
                if (i % FilesPerFrame == FilesPerFrame - 1) yield return null;
            }
            loadingTree = null;
            Resolve();
            AssignTrees();
            IsReady = true;
            ArchonLogger.Log($"Hoi4Focus: {trees.Count} trees, {focuses.Count} focuses from {files.Length} files"
                             + (failed > 0 ? $" ({failed} failed)" : "") + $", {countries.Count} countries, "
                             + $"{watch.ElapsedMilliseconds} ms of loading", Channel);
        }

        // ---- loading -------------------------------------------------------------------------------------

        void AddTree(ScriptTable t)
        {
            var tree = new FocusTree { Index = trees.Count, Id = t.String("id", ""), Default = t.Bool("default") };
            var weight = t["weight"];
            if (weight.Kind == ScriptValueKind.Function) tree.Weight = weight.Function;
            else tree.WeightBase = weight.Number(tree.Default ? 1 : 0);
            tree.SharedIds = Strings(t.Table("shared"));
            trees.Add(tree);
            loadingTree = tree;
        }

        void AddFocus(ScriptTable t, FocusTree tree)
        {
            string id = t.String("id");
            if (!t.IsValid || string.IsNullOrEmpty(id)) return;
            var f = new FocusDef
            {
                Index = focuses.Count, Id = id, Icon = t.String("icon", ""), Tree = tree != null ? tree.Index : -1,
                RawX = (int)t.Number("x"), RawY = (int)t.Number("y"), RelativeTo = t.String("relative_to"),
                Days = Mathf.Max(1, Mathf.RoundToInt((float)(t.Number("cost", 10) * PointDays))),
                Filters = Strings(t.Table("filters")), ExclusiveIds = Strings(t.Table("mutually_exclusive")),
                Available = t.Function("available"), Bypass = t.Function("bypass"), AllowBranch = t.Function("allow_branch"),
                Reward = t.Function("reward"), OnSelect = t.Function("on_select"), OnBypass = t.Function("on_bypass"),
            };
            var groups = t.Table("prerequisites");
            f.PrerequisiteIds = new string[groups.Length][];
            for (int i = 0; i < groups.Length; i++) f.PrerequisiteIds[i] = Strings(groups[i + 1].Table);
            var ai = t["ai"];
            if (ai.Kind == ScriptValueKind.Function) f.Ai = ai.Function;
            else f.AiBase = ai.Number(1);
            focuses.Add(f);
            if (tree != null)
            {
                tree.Focuses.Add(f.Index);
                tree.ById[id] = f.Index;
            }
            else shared[id] = f.Index;
        }

        static string[] Strings(ScriptTable list)
        {
            if (!list.IsValid || list.Length == 0) return System.Array.Empty<string>();
            var a = new string[list.Length];
            for (int i = 0; i < a.Length; i++) a[i] = list[i + 1].String ?? "";
            return a;
        }

        /// <summary>After every file: the shared focuses each tree uses, ids to indices, positions.</summary>
        void Resolve()
        {
            foreach (var tree in trees)
            {
                // the shared focuses it lists, and every shared focus that follows from one already in the tree
                foreach (string id in tree.SharedIds)
                    if (shared.TryGetValue(id, out int s) && !tree.ById.ContainsKey(id)) { tree.ById[id] = s; tree.Focuses.Add(s); }
                if (tree.SharedIds.Length == 0) continue;
                bool grew = true;
                while (grew)
                {
                    grew = false;
                    foreach (var kv in shared)
                    {
                        if (tree.ById.ContainsKey(kv.Key)) continue;
                        bool follows = false;
                        foreach (var group in focuses[kv.Value].PrerequisiteIds)
                            foreach (string p in group)
                                if (tree.ById.TryGetValue(p, out int at) && focuses[at].Tree < 0) follows = true;
                        if (!follows) continue;
                        tree.ById[kv.Key] = kv.Value;
                        tree.Focuses.Add(kv.Value);
                        grew = true;
                    }
                }
                tree.Focuses.Sort(tree.Focuses.Count - SharedCount(tree), SharedCount(tree), null);  // the added ones in index order
            }

            // ids to indices: a tree's own focus first, then a shared one (focus ids repeat between tree variants)
            int unknown = 0;
            foreach (var f in focuses)
            {
                var own = f.Tree >= 0 ? trees[f.Tree].ById : null;
                int Find(string id) => id != null && own != null && own.TryGetValue(id, out int i) ? i
                                       : id != null && shared.TryGetValue(id, out int s) ? s : -1;
                var groups = new List<int[]>();
                foreach (var group in f.PrerequisiteIds)
                {
                    var g = new List<int>();
                    foreach (string id in group) { int i = Find(id); if (i >= 0) g.Add(i); else unknown++; }
                    if (g.Count > 0) groups.Add(g.ToArray());
                }
                f.Prerequisites = groups.ToArray();
                var ex = new List<int>();
                foreach (string id in f.ExclusiveIds) { int i = Find(id); if (i >= 0) ex.Add(i); else unknown++; }
                f.Exclusive = ex.ToArray();
            }
            // mutually exclusive is often written on one side only: both exclude each other
            foreach (var f in focuses)
                foreach (int x in f.Exclusive)
                    if (System.Array.IndexOf(focuses[x].Exclusive, f.Index) < 0)
                    {
                        var list = new List<int>(focuses[x].Exclusive) { f.Index };
                        focuses[x].Exclusive = list.ToArray();
                    }
            foreach (var f in focuses) Place(f, 0);
            foreach (var f in focuses) { f.PrerequisiteIds = null; f.ExclusiveIds = null; }
            if (unknown > 0)
                ArchonLogger.LogWarning($"Hoi4Focus: {unknown} prerequisite or exclusive ids name no focus of their tree", Channel);
        }

        int SharedCount(FocusTree tree)
        {
            int n = 0;
            foreach (int f in tree.Focuses) if (focuses[f].Tree < 0) n++;
            return n;
        }

        /// <summary>A focus's grid position: its own x, y added to the focus it is relative to.</summary>
        void Place(FocusDef f, int depth)
        {
            if (f.Placed) return;
            f.Placed = true;
            f.X = f.RawX;
            f.Y = f.RawY;
            if (f.RelativeTo == null || depth > 64) return;
            int at = f.Tree >= 0 && trees[f.Tree].ById.TryGetValue(f.RelativeTo, out int i) ? i
                     : shared.TryGetValue(f.RelativeTo, out int s) ? s : -1;
            if (at < 0) return;
            Place(focuses[at], depth + 1);
            f.X += focuses[at].X;
            f.Y += focuses[at].Y;
        }

        /// <summary>Each country its tree: the highest weight, the default tree when none weighs anything.</summary>
        void AssignTrees()
        {
            if (trees.Count == 0) return;
            int fallback = -1;
            foreach (var tree in trees) if (tree.Default && fallback < 0) fallback = tree.Index;
            foreach (ushort country in Hoi4.Research.Hoi4Research.Instance.Countries)
            {
                int best = fallback;
                double most = 0;
                foreach (var tree in trees)
                {
                    double w = tree.Weight.IsValid ? Hoi4Script.Number(tree.Weight, country) : tree.WeightBase;
                    if (w > most || (w == most && w > 0 && tree.Index > best)) { most = w; best = tree.Index; } // a tie: the later file
                }
                if (best >= 0) countries[country] = new CountryFocus { Tree = best };
            }
        }

        // ---- queries ------------------------------------------------------------------------------------

        public IReadOnlyList<FocusDef> Focuses => focuses;
        public IReadOnlyList<FocusTree> Trees => trees;

        /// <summary>Countries with a focus tree, by id.</summary>
        public IEnumerable<ushort> Countries => countries.Keys;

        CountryFocus Of(ushort country) => countries.TryGetValue(country, out var c) ? c : None;

        public FocusDef Focus(int index) => index >= 0 && index < focuses.Count ? focuses[index] : null;

        /// <summary>The country's tree, null without one.</summary>
        public FocusTree Tree(ushort country)
        {
            int t = Of(country).Tree;
            return t >= 0 ? trees[t] : null;
        }

        /// <summary>The focus the country works on, -1 for none.</summary>
        public int Current(ushort country) => Of(country).Current;

        /// <summary>Days of progress on the current focus.</summary>
        public int Progress(ushort country) => Of(country).Progress;

        /// <summary>Days saved while no focus runs (up to MaxSavedDays).</summary>
        public int Saved(ushort country) => Of(country).Saved;

        public bool IsDone(ushort country, int focus) => Of(country).Done.Contains(focus);

        public int DoneCount(ushort country) => Of(country).Done.Count;

        /// <summary>Days until the current focus finishes (0 with none).</summary>
        public int DaysLeft(ushort country)
        {
            var c = Of(country);
            return c.Current >= 0 ? Mathf.Max(0, focuses[c.Current].Days - c.Progress) : 0;
        }

        /// <summary>Whether the tree lets the focus start: in the country's tree, not done, not the running one,
        /// prerequisites done, nothing exclusive done. No script runs.</summary>
        public bool Unlocked(ushort country, int focus)
        {
            var c = Of(country);
            var f = Focus(focus);
            if (f == null || c.Tree < 0 || c.Current == focus || c.Done.Contains(focus)) return false;
            if (f.Tree != c.Tree && !(f.Tree < 0 && trees[c.Tree].ById.TryGetValue(f.Id, out int at) && at == focus)) return false;
            foreach (var group in f.Prerequisites)
            {
                bool any = false;
                foreach (int p in group) if (c.Done.Contains(p)) { any = true; break; }
                if (!any) return false;
            }
            foreach (int x in f.Exclusive) if (c.Done.Contains(x)) return false;
            return true;
        }

        /// <summary>Whether the country may start the focus now: Unlocked, and its allow_branch and available
        /// triggers hold (these run the focus's Lua).</summary>
        public bool CanSelect(ushort country, int focus)
        {
            if (!IsReady || !Unlocked(country, focus)) return false;
            var f = focuses[focus];
            return Hoi4Script.Test(f.AllowBranch, country) && Hoi4Script.Test(f.Available, country);
        }

        // ---- commands and the day -------------------------------------------------------------------------

        /// <summary>Start a focus (SelectFocusCommand). The one running before loses its progress; saved days go
        /// to the new one at once. A focus whose bypass holds is done here and now.</summary>
        public void Select(ushort country, int focus)
        {
            if (!CanSelect(country, focus)) return;
            var c = countries[country];
            var f = focuses[focus];
            if (Hoi4Script.TestSure(f.Bypass, country))
            {
                c.Done.Add(focus);
                Core.Recording.SessionRecorder.Note("focus", "bypass", country, 0, 0, 0, f.Id);
                Hoi4Script.RunEffect(f.OnBypass, country, focus);
                Completed?.Invoke(country, focus);
                Changed?.Invoke(country);
                return;
            }
            c.Current = focus;
            c.Progress = c.Saved;
            c.Saved = 0;
            Core.Recording.SessionRecorder.Note("focus", "start", country, 0, 0, 0, $"{f.Id} ({f.Days} days, {c.Progress} saved)");
            Hoi4Script.RunEffect(f.OnSelect, country, focus);
            if (c.Progress >= f.Days) Finish(country, c);
            Changed?.Invoke(country);
        }

        /// <summary>Daily, from Hoi4Simulation: a day of progress on each country's focus (in country order), a
        /// saved day for those with none.</summary>
        public void SimulateDay()
        {
            if (!IsReady) return;
            List<ushort> finishing = null;
            foreach (var kv in countries)
            {
                var c = kv.Value;
                if (c.Current < 0) { if (c.Saved < MaxSavedDays) c.Saved++; continue; }
                if (++c.Progress >= focuses[c.Current].Days) (finishing ??= new List<ushort>()).Add(kv.Key);
            }
            if (finishing == null) return;
            foreach (ushort country in finishing)      // after the loop: a reward may change other countries
            {
                var c = countries[country];
                if (c.Current < 0 || c.Progress < focuses[c.Current].Days) continue;
                Finish(country, c);
                Changed?.Invoke(country);
            }
        }

        void Finish(ushort country, CountryFocus c)
        {
            int focus = c.Current;
            var f = focuses[focus];
            c.Current = -1;
            c.Progress = 0;
            c.Done.Add(focus);
            Core.Recording.SessionRecorder.Note("focus", "done", country, 0, 0, 0, f.Id);
            if (!Hoi4Script.RunEffect(f.Reward, country, focus))
                Core.Recording.SessionRecorder.Note("focus", "error", country, 0, 0, 0, $"{f.Id}: {Hoi4Script.Engine.LastError}");
            Completed?.Invoke(country, focus);
        }
    }
}
