using System.Collections;
using System.Collections.Generic;
using Hoi4.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the Battle Plans window (HOI4's battleplantools_window). It shows while a division of an army is
    /// selected; the front-line button turns the front line tool of Hoi4BattlePlans on, the offence button
    /// (enabled once the army has a front line) the offensive tool, the exercise button at the far left starts or
    /// stops the exercise of the selected divisions' whole armies (Hoi4Training; shift + click: until they are trained; K and shift + K;
    /// its cog turns while any of them exercises), the fallback button the fallback line tool
    /// (drag a line across own or allied land for the divisions to hold), the area defence button its tool and
    /// the Defense window above the bar (what to guard in the clicked states; the kinds without data are
    /// disabled), the bin the delete tool (click a line or an
    /// offensive to remove it; shift + right click removes every order of the selected divisions' armies at once). Also writes the text on each placed line and objective line: "N Divs, army name", bent
    /// along the line like HOI4 (one element per letter). Like HOI4's, the text is sized in map units, so it
    /// grows and shrinks with the line it is on (labelMapPx, within labelMinPx..labelMaxPx on screen); it hides
    /// with zoom by the unit counters' rules (Hoi4UnitCounters.ZoomRules).
    /// View of the UI Presenter pattern: structure BattlePlanPanel.uxml, look BattlePlanStyle.uss + Hoi4Theme.uss.
    /// </summary>
    public sealed class Hoi4BattlePlanPanel : MonoBehaviour
    {
        const string Dir = "gfx/interface/battleplans/";

        UIDocument document;
        VisualElement window, labels, front, offence, fallback, area, defense, delete, assign, unassign;
        Label defenseTitle;
        VisualElement exercise, exerciseIcon, exerciseAnim, exerciseMax;
        int exerciseFrame = -2; // the animation frame shown, -1 the still icon
        // The Defense window's toggles in HOI4's order: sprite name, guard flag
        static readonly (string name, int flag)[] Guards =
        {
            ("vp", Hoi4BattlePlans.GuardVictoryPoints), ("port", Hoi4BattlePlans.GuardPorts), ("coastline", Hoi4BattlePlans.GuardCoast),
            ("airbase", Hoi4BattlePlans.GuardAirbases), ("borders", Hoi4BattlePlans.GuardBorders), ("fort", Hoi4BattlePlans.GuardForts),
            ("rail", Hoi4BattlePlans.GuardHubs), ("facility", Hoi4BattlePlans.GuardFacilities),
        };
        readonly VisualElement[] guardButtons = new VisualElement[Guards.Length];
        int shownGuard = -1, shownMin = -1;
        bool areaActive;
        readonly List<Label> labelPool = new List<Label>();   // one per letter
        readonly Dictionary<char, float> charWidth = new Dictionary<char, float>(); // at BaseFontPx, panel px
        Label probe;                                            // measures letters
        const float BaseFontPx = 15f;                           // BattlePlanStyle.uss .bp-line-label: letters are measured at this size
        [Tooltip("Line text height in map pixels (HOI4: about 1.3, a little over half a fallback line's X)")] public float labelMapPx = 1.3f;
        [Tooltip("Line text height on screen, smallest and largest (panel px)")] public float labelMinPx = 13f, labelMaxPx = 34f;
        [Tooltip("Dark outline around the letters, of the text height")] public float labelOutline = 0.09f;
        readonly List<Vector2> linePts = new List<Vector2>();
        readonly List<float> lineRun = new List<float>();
        bool frontActive, offenceActive, fallbackActive, deleteActive, assignActive, offenceEnabled = true, ownEnabled = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4BattlePlanPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4BattlePlanPanel>();
        }

        IEnumerator Start()
        {
            while (Hoi4BattlePlans.Instance == null || Hoi4UnitSelection.Instance == null)
                yield return null;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/BattlePlanPanel");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/BattlePlanStyle");
            if (tree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4BattlePlanPanel: Resources/Hoi4UI/BattlePlanPanel.uxml or its stylesheets are missing, panel disabled", "game_units");
                yield break;
            }

            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4BattlePlanPanel: no UIDocument panel settings in the scene, panel disabled", "game_units");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 44;
            yield return null;

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            var content = tree.Instantiate();
            labels = content.Q("bp-labels");
            window = content.Q("bp-window");
            Hoi4Sprites.Apply(window.Q("bp-bg"), Dir + "bg.png");
            offence = window.Q("bp-offence");
            Hoi4Sprites.Apply(offence, Dir + "offence_0.png");
            offence.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                var plans = Hoi4BattlePlans.Instance;
                if (plans.OffensiveActive) plans.CancelOffensive(); else plans.BeginOffensive();
                e.StopPropagation();
            });
            front = window.Q("bp-front");
            delete = window.Q("bp-delete");
            Hoi4Sprites.Apply(front, Dir + "defence_0.png");
            Hoi4Sprites.Apply(delete, Dir + "delete_0.png");
            front.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                var plans = Hoi4BattlePlans.Instance;
                if (plans.Active) plans.Cancel(); else plans.BeginDrawing();
                e.StopPropagation();
            });
            fallback = window.Q("bp-fallback");
            Hoi4Sprites.Apply(fallback, Dir + "fallback_0.png");
            fallback.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                var plans = Hoi4BattlePlans.Instance;
                if (plans.FallbackActive) plans.CancelFallback(); else plans.BeginFallback();
                e.StopPropagation();
            });
            exercise = window.Q("bp-exercise");
            exerciseIcon = exercise.Q("bp-exercise-icon");
            exerciseAnim = exercise.Q("bp-exercise-anim");
            exerciseMax = exercise.Q("bp-exercise-max");
            Hoi4Sprites.Apply(exerciseIcon, Dir + "exercise_icon.png");
            Hoi4Sprites.Apply(exerciseMax, Dir + "exercise_stop_at_max.png");
            exerciseMax.style.display = DisplayStyle.None;
            exercise.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 0) ToggleExercise(e.shiftKey); // the right button does nothing
                e.StopPropagation();
            });
            area = window.Q("bp-area");
            Hoi4Sprites.Apply(area, Dir + "area_0.png");
            area.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                var plans = Hoi4BattlePlans.Instance;
                if (plans.AreaActive) plans.CancelArea(); else plans.BeginArea();
                e.StopPropagation();
            });
            defense = window.Q("bp-defense");
            defense.style.display = DisplayStyle.None;
            Hoi4Sprites.Apply(defense.Q("bp-defense-bg"), Dir + "areadef_bg.png");
            defenseTitle = defense.Q<Label>("bp-defense-title");
            for (int i = 0; i < Guards.Length; i++)
            {
                int flag = Guards[i].flag;
                var b = guardButtons[i] = defense.Q("bp-guard-" + Guards[i].name);
                b.style.left = i * 40;
                if ((flag & Hoi4BattlePlans.GuardAvailable) == 0) { b.AddToClassList("bp-disabled"); b.pickingMode = PickingMode.Ignore; }
                b.RegisterCallback<PointerDownEvent>(e =>
                {
                    if (e.button != 0) return;
                    Hoi4BattlePlans.Instance.ToggleAreaGuard(flag);
                    e.StopPropagation();
                });
            }
            delete.RegisterCallback<PointerDownEvent>(e =>
            {
                var plans = Hoi4BattlePlans.Instance;
                if (e.button == 1 && e.shiftKey) plans.DeleteSelectedPlans();   // HOI4: every order of the selected armies, at once
                else if (e.button == 0) { if (plans.DeleteActive) plans.CancelDelete(); else plans.BeginDelete(); }
                else return;
                e.StopPropagation();
            });
            assign = window.Q("bp-assign");
            unassign = window.Q("bp-unassign");
            Hoi4Sprites.Apply(assign, Dir + "assign_0.png");
            Hoi4Sprites.Apply(unassign, Dir + "unassign_0.png");
            assign.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                var plans = Hoi4BattlePlans.Instance;
                if (plans.AssignActive) plans.CancelAssign(); else plans.BeginAssign();
                e.StopPropagation();
            });
            unassign.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                var plans = Hoi4BattlePlans.Instance;
                var selected = plans.OwnSelection();
                if (selected.Count > 0 && !plans.SelectionAreaDefended && Engine.ArchonEngine.Instance?.GameState != null)
                    Engine.ArchonEngine.Instance.GameState.TryExecuteCommand(
                        new UnassignFromPlanCommand { CountryId = selectedCountry(selected), UnitIds = selected }, out _);
                e.StopPropagation();
            });
            root.Add(labels);
            root.Add(window);
        }

        /// <summary>An exercise is the whole army's, like HOI4: every division of the armies the selected own
        /// divisions belong to, sorted.</summary>
        static List<ushort> ExerciseUnits(Hoi4BattlePlans plans)
        {
            var result = new List<ushort>();
            var armies = Hoi4Armies.Instance;
            if (armies == null) return result;
            var seen = new HashSet<int>();
            foreach (ushort u in plans.OwnSelection())
            {
                var army = armies.ArmyOf(u);
                if (army != null && seen.Add(army.Id)) result.AddRange(army.Units);
            }
            result.Sort();
            return result;
        }

        /// <summary>The selected divisions' armies start an exercise (untilTrained: it ends by itself once they are
        /// trained), or stop when any of their divisions is on one (with shift, already trained ones don't start).</summary>
        void ToggleExercise(bool untilTrained)
        {
            var plans = Hoi4BattlePlans.Instance;
            var gs = Engine.ArchonEngine.Instance?.GameState;
            var selected = ExerciseUnits(plans);
            if (gs == null || selected.Count == 0) return;
            bool any = false;
            foreach (ushort u in selected) any |= Hoi4Training.IsTraining(u);
            gs.TryExecuteCommand(new SetTrainingCommand { CountryId = selectedCountry(selected), UnitIds = selected, On = !any, UntilTrained = untilTrained }, out _);
        }

        void OnDestroy() { Hoi4UiHover.Remove(this); Hoi4UiHover.Remove(Guards); }

        static ushort selectedCountry(List<ushort> units)
        {
            var gs = Engine.ArchonEngine.Instance.GameState;
            return gs.Units.HasUnit(units[0]) ? gs.Units.GetUnit(units[0]).countryID : (ushort)0;
        }

        void Update()
        {
            if (window == null) return;
            var plans = Hoi4BattlePlans.Instance;
            bool own = plans.HasOwnSelection;
            bool show = plans.Active || plans.OffensiveActive || plans.FallbackActive || plans.AreaActive || plans.DeleteActive || plans.AssignActive || plans.HasArmyMemberSelected;
            window.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            Hoi4UiHover.Report(this, window);
            // The Defense window sticks out above the bar: reported on its own, so a click on it isn't a click on the map
            Hoi4UiHover.Report(Guards, show && plans.AreaActive ? defense : null);
            if (show && frontActive != plans.Active)
            {
                frontActive = plans.Active;
                Hoi4Sprites.Apply(front, Dir + (frontActive ? "defence_1.png" : "defence_0.png"));
            }
            if (show && fallbackActive != plans.FallbackActive)
            {
                fallbackActive = plans.FallbackActive;
                Hoi4Sprites.Apply(fallback, Dir + (fallbackActive ? "fallback_1.png" : "fallback_0.png"));
            }
            if (show)
            {
                // The exercise button: while a selected division exercises, HOI4's turning cog
                // (GFX_exercise_icon_anim_strip_big: 20 frames at 7 fps, real seconds: presentation); the mark
                // shows when one stops by itself once trained
                bool any = false, untilTrained = false;
                foreach (ushort u in ExerciseUnits(plans)) { any |= Hoi4Training.IsTraining(u); untilTrained |= Hoi4Training.StopsWhenTrained(u); }
                int frame = any ? Mathf.FloorToInt(Time.unscaledTime * 7f) % 20 : -1;
                if (frame != exerciseFrame)
                {
                    exerciseFrame = frame;
                    exerciseIcon.style.display = any ? DisplayStyle.None : DisplayStyle.Flex;
                    exerciseAnim.style.display = any ? DisplayStyle.Flex : DisplayStyle.None;
                    if (any) Hoi4Sprites.Apply(exerciseAnim, Dir + $"exercise_anim_{frame}.png");
                }
                exerciseMax.style.display = untilTrained ? DisplayStyle.Flex : DisplayStyle.None;
                if (global::Utils.KeyboardFocus.KeyDown(KeyCode.K))
                    ToggleExercise(Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
            }
            if (show && areaActive != plans.AreaActive)
            {
                areaActive = plans.AreaActive;
                Hoi4Sprites.Apply(area, Dir + (areaActive ? "area_1.png" : "area_0.png"));
                defense.style.display = areaActive ? DisplayStyle.Flex : DisplayStyle.None;
                shownGuard = shownMin = -1;
            }
            if (show && areaActive)
            {
                int guard = plans.AreaGuard, min = plans.AreaMinDivisions;
                if (guard != shownGuard)
                {
                    shownGuard = guard;
                    // the sprites' frame 0 is the chosen look (framed), frame 1 the plain one
                    for (int i = 0; i < Guards.Length; i++)
                        Hoi4Sprites.Apply(guardButtons[i], Dir + "garr_" + Guards[i].name + ((guard & Guards[i].flag) != 0 ? "_0.png" : "_1.png"));
                }
                if (min != shownMin)
                {
                    shownMin = min;
                    defenseTitle.text = $"Defense (Min. divisions: <color=#FFD24A>{min}</color>)";
                }
            }
            if (show && assignActive != plans.AssignActive)
            {
                assignActive = plans.AssignActive;
                Hoi4Sprites.Apply(assign, Dir + (assignActive ? "assign_1.png" : "assign_0.png"));
            }
            // Assign and unassign: greyed out without an own division selected, and for an army on area defence
            bool canAssign = show && own && !plans.SelectionAreaDefended;
            if (show && canAssign != ownEnabled)
            {
                ownEnabled = canAssign;
                assign.EnableInClassList("bp-disabled", !canAssign);
                unassign.EnableInClassList("bp-disabled", !canAssign);
            }
            if (show && deleteActive != plans.DeleteActive)
            {
                deleteActive = plans.DeleteActive;
                Hoi4Sprites.Apply(delete, Dir + (deleteActive ? "delete_1.png" : "delete_0.png"));
            }
            if (show && offenceActive != plans.OffensiveActive)
            {
                offenceActive = plans.OffensiveActive;
                Hoi4Sprites.Apply(offence, Dir + (offenceActive ? "offence_1.png" : "offence_0.png"));
            }
            bool canOffend = show && (plans.OffensiveActive || plans.CanPlanOffensive);
            if (canOffend != offenceEnabled)
            {
                offenceEnabled = canOffend;
                offence.EnableInClassList("bp-disabled", !canOffend);
            }
            UpdateLabels(plans);
        }

        void UpdateLabels(Hoi4BattlePlans plans)
        {
            var cam = Camera.main;
            var armies = Hoi4Armies.Instance;
            var counters = Hoi4UnitCounters.Instance;
            int used = 0;
            skip = default;
            if (cam != null && counters != null)
            {
                foreach (var plan in plans.Plans)
                {
                    if (plan.IsAI || plan.Labels.Count == 0) continue;
                    var counts = plan.DivisionsPerFront();
                    string army = ArmyName(armies, plan.ArmyId);
                    foreach (var label in plan.Labels)
                    {
                        int n = label.Front < counts.Length ? counts[label.Front] : 0;
                        PlaceAlong(Text(n, army), label.Path, label.Anchor, cam, counters, ref used);
                    }
                }
                foreach (var o in plans.Offensives)
                    if (o.Objective != null)
                        PlaceAlong(Text(plans.OffensiveDivisions(o), ArmyName(armies, o.ArmyId)), o.LabelPath, o.LabelAnchor, cam, counters, ref used);
            }
            for (int i = used; i < labelPool.Count; i++) labelPool[i].style.display = DisplayStyle.None;
            // Diagnostics: lines exist but no text was written (logged now and then, with why)
            if (used == 0 && skip.Total > 0 && Time.unscaledTime > nextSkipLog)
            {
                nextSkipLog = Time.unscaledTime + 5f;
                ArchonLogger.LogDebug($"Hoi4BattlePlanPanel: {skip.Total} line labels, none written: {skip.NoPath} without a path, " +
                                 $"{skip.Zoom} hidden by zoom, {skip.NoPanel} no panel, {skip.Behind} behind the camera, {skip.Bad} bad positions", "game_units");
            }
        }

        struct SkipCounts { public int Total, NoPath, Zoom, NoPanel, Behind, Bad; }
        SkipCounts skip;
        float nextSkipLog;

        static string Text(int divisions, string army) => divisions + (divisions == 1 ? " Div, " : " Divs, ") + army;

        /// <summary>
        /// Write the text centred on path[anchor], one letter at a time along the line as it appears on screen
        /// (HOI4 bends the text with the line), each letter turned to the line's direction there. Reads left to
        /// right: the path is walked backwards when it runs leftwards at the centre. Hidden where the unit
        /// counters are, and sized like them.
        /// </summary>
        void PlaceAlong(string text, List<Vector3> path, int anchor, Camera cam, Hoi4UnitCounters counters, ref int used)
        {
            skip.Total++;
            if (path == null || path.Count < 2 || anchor < 0 || anchor >= path.Count) { skip.NoPath++; return; }
            if (!counters.ZoomRules(path[anchor], cam, out _, out float alpha) || alpha <= 0.01f) { skip.Zoom++; return; }
            var panel = labels.panel;
            if (panel == null) { skip.NoPanel++; return; }
            // The text's height: labelMapPx of the map as it is on screen at the label (it scales with the line)
            var at0 = cam.WorldToScreenPoint(path[anchor]);
            float worldPerScreenPx = 2f * Mathf.Max(at0.z, 1e-3f) / (Mathf.Abs(cam.projectionMatrix[1, 1]) * Screen.height);
            float panelPerScreenPx = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(1f, 0f)).x - RuntimePanelUtils.ScreenToPanel(panel, Vector2.zero).x;
            float font = Mathf.Clamp(labelMapPx * counters.WorldPerMapPixel / worldPerScreenPx * panelPerScreenPx, labelMinPx, labelMaxPx);
            float scale = font / BaseFontPx;

            // The line on screen (panel px), cumulative length
            linePts.Clear(); lineRun.Clear();
            int anchorAt = -1;
            for (int i = 0; i < path.Count; i++)
            {
                var s = cam.WorldToScreenPoint(path[i]);
                if (s.z <= 0f) { if (i < anchor) { linePts.Clear(); continue; } break; } // behind the camera: cut there
                if (i == anchor) anchorAt = linePts.Count;
                linePts.Add(RuntimePanelUtils.ScreenToPanel(panel, new Vector2(s.x, Screen.height - s.y)));
            }
            if (anchorAt < 0 || linePts.Count < 2) { skip.Behind++; return; }
            int m = Mathf.Clamp(anchorAt, 1, linePts.Count - 1);
            if (linePts[m].x - linePts[m - 1].x < 0f) // runs leftwards: read it from the other end
            {
                linePts.Reverse();
                anchorAt = linePts.Count - 1 - anchorAt;
            }
            float run = 0f;
            lineRun.Add(0f);
            for (int i = 1; i < linePts.Count; i++) { run += Vector2.Distance(linePts[i - 1], linePts[i]); lineRun.Add(run); }

            float width = 0f;
            foreach (char c in text) width += CharWidth(c) * scale;
            float at = lineRun[anchorAt] - width * 0.5f;
            if (float.IsNaN(at) || float.IsInfinity(at)) { skip.Bad++; return; }
            var color = new Color(1f, 1f, 1f, alpha);
            foreach (char c in text)
            {
                float w = CharWidth(c) * scale;
                if (c != ' ')
                {
                    PointAt(at + w * 0.5f, out var pos, out var dir);
                    var l = LabelAt(used++);
                    l.text = c.ToString();
                    l.style.fontSize = font;
                    l.style.unityTextOutlineWidth = font * labelOutline;
                    l.style.color = color;
                    l.style.left = pos.x;
                    l.style.top = pos.y;
                    l.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
                    l.style.rotate = new Rotate(new Angle(Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg, AngleUnit.Degree));
                    l.style.display = DisplayStyle.Flex;
                }
                at += w;
            }
        }

        /// <summary>Point and direction at a distance along the screen line (straight on past its ends).</summary>
        void PointAt(float s, out Vector2 pos, out Vector2 dir)
        {
            int n = linePts.Count;
            int i = 1;
            while (i < n - 1 && lineRun[i] < s) i++;
            var a = linePts[i - 1];
            var b = linePts[i];
            float seg = lineRun[i] - lineRun[i - 1];
            dir = seg > 1e-4f ? (b - a) / seg : Vector2.right;
            pos = a + dir * (s - lineRun[i - 1]);
        }

        /// <summary>A letter's advance at BaseFontPx, measured once with the label style.</summary>
        float CharWidth(char c)
        {
            if (charWidth.TryGetValue(c, out float w)) return w;
            if (probe == null)
            {
                probe = new Label { pickingMode = PickingMode.Ignore };
                probe.AddToClassList("bp-line-label");
                probe.style.visibility = Visibility.Hidden;
                labels.Add(probe);
            }
            var size = probe.MeasureTextSize(c == ' ' ? "\u00a0" : c.ToString(), 0f, VisualElement.MeasureMode.Undefined, 0f, VisualElement.MeasureMode.Undefined);
            w = size.x;
            if (float.IsNaN(w) || float.IsInfinity(w) || w <= 0.01f) return BaseFontPx * 0.55f; // style not resolved yet: guess, measure again next time
            charWidth[c] = w;
            return w;
        }

        static string ArmyName(Hoi4Armies armies, int id)
        {
            if (armies == null) return "-";
            foreach (var a in armies.All)
                if (a.Id == id) return a.Name;
            return "-";
        }

        Label LabelAt(int index)
        {
            while (labelPool.Count <= index)
            {
                var l = new Label { pickingMode = PickingMode.Ignore };
                l.AddToClassList("bp-line-label");
                labels.Add(l);
                labelPool.Add(l);
            }
            return labelPool[index];
        }
    }
}
