using System.Collections;
using Hoi4.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4's selected-units list (interface/unitview.gui army_division_list_view), top left under the
    /// top bar. View of the UI Presenter pattern: structure is Resources/Hoi4UI/DivisionPanel.uxml, look is
    /// DivisionPanelStyle.uss + Hoi4Theme.uss, display logic is Hoi4DivisionPresenter. Sprites are loaded at
    /// runtime from the data directory (Hoi4Sprites). Clicking an entry selects only that division.
    /// </summary>
    public sealed class Hoi4DivisionPanel : MonoBehaviour
    {
        UIDocument document;
        VisualElement panel;
        bool dirty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            var go = new GameObject("Hoi4DivisionPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4DivisionPanel>();
        }

        IEnumerator Start()
        {
            while (Hoi4UnitSelection.Instance == null)
                yield return null;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/DivisionPanel");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/DivisionPanelStyle");
            if (tree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4DivisionPanel: Resources/Hoi4UI/DivisionPanel.uxml / DivisionPanelStyle.uss or its stylesheets are missing, panel disabled", "game_units");
                yield break;
            }

            // Borrow the panel settings (theme, scaling) of the scene's UI
            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4DivisionPanel: no UIDocument panel settings in the scene, panel disabled", "game_units");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 50;
            yield return null; // let the document create its root

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            panel = tree.Instantiate().Q("division-panel");
            Hoi4Sprites.Bind(panel);
            Hoi4Sprites.Hook(panel); // the window frame is the root itself, which Bind skips
            panel.style.display = DisplayStyle.None;
            var selection = Hoi4UnitSelection.Instance;
            panel.Q("close").RegisterCallback<ClickEvent>(_ => selection.Clear());
            panel.Q("select-half").RegisterCallback<ClickEvent>(_ => Hoi4DivisionPresenter.SelectHalf(selection));
            root.Add(panel);

            selection.Changed += OnChanged;
            dirty = true;
        }

        void OnChanged() => dirty = true;

        void OnDestroy()
        {
            if (Hoi4UnitSelection.Instance != null) Hoi4UnitSelection.Instance.Changed -= OnChanged;
            Hoi4UiHover.Remove(this);
        }

        void Update()
        {
            if (panel == null) return;
            Hoi4UiHover.Report(this, panel); // the wheel scrolls the list, not the map zoom
            if (!dirty) return;
            dirty = false;
            var gameState = Engine.ArchonEngine.Instance != null ? Engine.ArchonEngine.Instance.GameState : null;
            bool shown = Hoi4DivisionPresenter.Show(panel, gameState, Hoi4UnitSelection.Instance, Hoi4Divisions.Instance);
            panel.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
