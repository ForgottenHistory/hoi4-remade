using System.Collections;
using System.Collections.Generic;
using Hoi4.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the Battle Plans window (HOI4's battleplantools_window, offensive lines only). It shows while a
    /// division of an army is selected; the front-line button turns the draw tool of Hoi4BattlePlans on, the
    /// bin removes the plans of the selected divisions. Also writes the text on each placed line:
    /// "N Divs, army name", along the line's direction.
    /// View of the UI Presenter pattern: structure BattlePlanPanel.uxml, look BattlePlanStyle.uss + Hoi4Theme.uss.
    /// </summary>
    public sealed class Hoi4BattlePlanPanel : MonoBehaviour
    {
        const string Dir = "gfx/interface/battleplans/";

        UIDocument document;
        VisualElement window, labels, front, delete;
        readonly List<Label> labelPool = new List<Label>();
        bool frontActive;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
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
            Hoi4Sprites.Apply(window.Q("bp-offence"), Dir + "offence_0.png");
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
            delete.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                Hoi4BattlePlans.Instance.DeleteSelectedPlans();
                e.StopPropagation();
            });
            root.Add(labels);
            root.Add(window);
        }

        void OnDestroy() => Hoi4UiHover.Remove(this);

        void Update()
        {
            if (window == null) return;
            var plans = Hoi4BattlePlans.Instance;
            bool show = plans.Active || plans.HasArmySelection;
            window.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            Hoi4UiHover.Report(this, window);
            if (show && frontActive != plans.Active)
            {
                frontActive = plans.Active;
                Hoi4Sprites.Apply(front, Dir + (frontActive ? "defence_1.png" : "defence_0.png"));
            }
            UpdateLabels(plans);
        }

        void UpdateLabels(Hoi4BattlePlans plans)
        {
            var cam = Camera.main;
            var armies = Hoi4Armies.Instance;
            int used = 0;
            if (cam != null)
                foreach (var plan in plans.Plans)
                {
                    var a = cam.WorldToScreenPoint(plan.LabelWorld);
                    var b = cam.WorldToScreenPoint(plan.LabelAhead);
                    if (a.z <= 0f) continue;
                    var label = LabelAt(used++);
                    label.text = plan.Units.Count + (plan.Units.Count == 1 ? " Div, " : " Divs, ") + ArmyName(armies, plan.ArmyId);
                    var panel = label.panel;
                    var pos = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(a.x, Screen.height - a.y));
                    var ahead = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(b.x, Screen.height - b.y));
                    float deg = Mathf.Atan2(ahead.y - pos.y, ahead.x - pos.x) * Mathf.Rad2Deg;
                    if (deg > 90f) deg -= 180f; else if (deg < -90f) deg += 180f; // keep the text upright
                    label.style.left = pos.x;
                    label.style.top = pos.y;
                    label.style.translate = new Translate(Length.Percent(-50), Length.Percent(-50));
                    label.style.rotate = new Rotate(new Angle(deg, AngleUnit.Degree));
                    label.style.display = DisplayStyle.Flex;
                }
            for (int i = used; i < labelPool.Count; i++) labelPool[i].style.display = DisplayStyle.None;
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
