using System;
using System.Collections;
using Core.Systems;
using Hoi4.Map;
using Hoi4.Units;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's state view (interface/countrystateview.gui), bottom left. Clicking a land province
    /// selects its state and shows it. This is the View of the UI Presenter pattern: structure is
    /// Resources/Hoi4UI/StatePanel.uxml, look is StatePanelStyle.uss + Hoi4Theme.uss, display logic is
    /// Hoi4StatePresenter. Sprites are loaded at runtime from the data directory (Hoi4Sprites).
    /// Hidden while divisions are selected (the unit panel takes the space).
    /// </summary>
    public sealed class Hoi4StatePanel : MonoBehaviour
    {
        UIDocument document;
        VisualElement panel;
        global::Map.Interaction.ProvinceSelector selector;
        Core.GameState gameState;
        IDisposable ownershipSub;
        ushort province;
        bool dirty;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4StatePanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4StatePanel>();
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.ProvinceSelector == null
                   || Engine.ArchonEngine.Instance.GameState == null)
                yield return null;
            selector = Engine.ArchonEngine.Instance.ProvinceSelector;
            gameState = Engine.ArchonEngine.Instance.GameState;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/StatePanel");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/StatePanelStyle");
            if (tree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4StatePanel: Resources/Hoi4UI/StatePanel.uxml or its stylesheets are missing, panel disabled", "game_ui");
                yield break;
            }

            // Borrow the panel settings (theme, scaling) of the scene's UI
            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4StatePanel: no UIDocument panel settings in the scene, panel disabled", "game_ui");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 40;
            yield return null; // let the document create its root

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            panel = tree.Instantiate().Q("state-panel");
            Hoi4Sprites.Bind(panel);
            panel.style.display = DisplayStyle.None;
            panel.Q("close").RegisterCallback<ClickEvent>(_ => Hide());
            root.Add(panel);

            selector.OnProvinceClicked += OnProvinceClicked;
            selector.OnSelectionCleared += Hide;
            ownershipSub = gameState.EventBus.Subscribe<ProvinceOwnershipChangedEvent>(e =>
            {
                if (province != 0 && Hoi4Regions.State(e.ProvinceId) == Hoi4Regions.State(province)) dirty = true;
            });
            while (Hoi4UnitSelection.Instance == null) yield return null;
            Hoi4UnitSelection.Instance.Changed += OnUnitsChanged;
        }

        void OnDestroy()
        {
            if (selector != null)
            {
                selector.OnProvinceClicked -= OnProvinceClicked;
                selector.OnSelectionCleared -= Hide;
            }
            ownershipSub?.Dispose();
            if (Hoi4UnitSelection.Instance != null) Hoi4UnitSelection.Instance.Changed -= OnUnitsChanged;
            Hoi4UiHover.Remove(this);
        }

        void OnUnitsChanged()
        {
            if (Hoi4UnitSelection.Instance.Selected.Count > 0) Hide();
        }

        // Lighter than the gold province selection, so the two outlines read apart
        static readonly Color StateOutline = new Color(1f, 0.93f, 0.74f, 1f);
        static readonly Color ProvinceOutline = new Color(1f, 0.84f, 0f, 0.5f);

        static global::Map.Interaction.ProvinceHighlighter Highlighter =>
            Engine.ArchonEngine.Instance != null ? Engine.ArchonEngine.Instance.ProvinceHighlighter : null;

        static bool HasCountry()
        {
            var init = StarterKit.Initializer.Instance;
            return init != null && init.PlayerState != null && init.PlayerState.HasPlayerCountry;
        }

        void Hide()
        {
            province = 0;
            Highlighter?.ClearProvinceSet();
            Highlighter?.ClearHighlight();
            if (panel != null) panel.style.display = DisplayStyle.None;
        }

        void OnProvinceClicked(ushort id)
        {
            if (id == 0 || panel == null || !HasCountry()) return;
            province = id;
            dirty = true;
        }

        bool wasDiplomacy;

        void Update()
        {
            if (panel == null) return;
            Hoi4UiHover.Report(this, panel);
            if (!HasCountry()) { if (province != 0) Hide(); return; }
            if (Input.GetKeyDown(KeyCode.Escape) && province != 0) { Hide(); return; }
            bool diplomacy = Hoi4DiplomacyPanel.IsOpen;
            if (diplomacy != wasDiplomacy) { wasDiplomacy = diplomacy; dirty = true; }
            if (diplomacy && panel.resolvedStyle.display != DisplayStyle.None) panel.style.display = DisplayStyle.None;
            if (!dirty) return;
            dirty = false;
            if (province != 0 && Hoi4StatePresenter.Show(panel, gameState, province))
            {
                panel.style.display = diplomacy ? DisplayStyle.None : DisplayStyle.Flex; // diplomacy mode: outline only, no panel
                int stateId = Hoi4Regions.State(province);
                Highlighter?.HighlightProvince(province, ProvinceOutline);
                Highlighter?.HighlightProvinceSet(Hoi4Regions.ProvincesOf(stateId), StateOutline);
            }
            else
                Hide();
        }
    }
}
