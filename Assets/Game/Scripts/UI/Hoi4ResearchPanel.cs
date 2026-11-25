using System.Collections;
using System.Collections.Generic;
using Core;
using Hoi4.Research;
using UnityEngine;
using UnityEngine.UIElements;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's research screen (interface/countrytechnologyview.gui), left of the map, opened by the top bar's
    /// research button or W. Structure is Resources/Hoi4UI/ResearchPanel.uxml (+ ResearchSlot.uxml per slot), look is
    /// ResearchPanelStyle.uss + Hoi4Theme.uss. Data from Hoi4Research.
    /// - Research Slots tab: limited bonuses, research speed, one entry per slot. An empty slot glows (the two glow
    ///   frames blended, like HOI4) and its bar shows the research saved while idle, out of 30 days.
    /// - Experimental Facilities tab: a placeholder.
    /// Values refresh four times a real second (a presentation throttle, not game time).
    /// </summary>
    public sealed class Hoi4ResearchPanel : MonoBehaviour
    {
        const float RefreshSeconds = 0.25f;
        const float BarWidth = 377f;
        const string EmptyText = "Select a technology to research"; // UNUSED_SLOT

        public static Hoi4ResearchPanel Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance.open;

        UIDocument document;
        VisualElement panel, slotsPage, facilitiesPage, tabSlots, tabFacilities;
        ScrollView slotList;
        Label speed;
        VisualTreeAsset slotTree;
        readonly List<VisualElement> entries = new List<VisualElement>();
        bool open, facilities;
        float nextRefresh;
        ushort shownCountry;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4ResearchPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4ResearchPanel>();
        }

        void Awake() => Instance = this;

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null)
                yield return null;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/ResearchPanel");
            slotTree = Resources.Load<VisualTreeAsset>("Hoi4UI/ResearchSlot");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/ResearchPanelStyle");
            if (tree == null || slotTree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4ResearchPanel: Resources/Hoi4UI/ResearchPanel.uxml, ResearchSlot.uxml or the stylesheets are missing, panel disabled", "game_ui");
                yield break;
            }

            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4ResearchPanel: no UIDocument panel settings in the scene, panel disabled", "game_ui");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 45; // over the state and unit panels, under the top bar
            yield return null;

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            panel = tree.Instantiate().Q("research-panel");
            Hoi4Sprites.Bind(panel);
            root.Add(panel);
            slotsPage = panel.Q("rs-slots-page");
            facilitiesPage = panel.Q("rs-facilities-page");
            slotList = panel.Q<ScrollView>("rs-slots");
            speed = panel.Q<Label>("rs-speed");
            tabSlots = panel.Q("rs-tab-slots");
            tabFacilities = panel.Q("rs-tab-facilities");

            panel.Q("rs-close").RegisterCallback<ClickEvent>(_ => Close());
            tabSlots.RegisterCallback<ClickEvent>(_ => ShowTab(false));
            tabFacilities.RegisterCallback<ClickEvent>(_ => ShowTab(true));
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

        /// <summary>The top bar's research button.</summary>
        public void Toggle()
        {
            if (open) Close(); else Open();
        }

        void Open()
        {
            if (panel == null || Player == 0) return;
            // they share the left side
            if (Hoi4ProductionPanel.IsOpen) Hoi4ProductionPanel.Instance.Toggle();
            if (Hoi4ConstructionPanel.IsOpen) Hoi4ConstructionPanel.Instance.Close();
            if (Hoi4DeploymentPanel.IsOpen) Hoi4DeploymentPanel.Instance.Close();
            open = true;
            panel.style.display = DisplayStyle.Flex;
            shownCountry = 0; // rebuild the slots
            nextRefresh = 0f;
        }

        public void Close()
        {
            open = false;
            if (panel != null) panel.style.display = DisplayStyle.None;
        }

        void ShowTab(bool showFacilities)
        {
            facilities = showFacilities;
            tabSlots.EnableInClassList("selected", !facilities);
            tabFacilities.EnableInClassList("selected", facilities);
            slotsPage.EnableInClassList("rs-hidden", facilities);
            facilitiesPage.EnableInClassList("rs-hidden", !facilities);
        }

        // ---- update -----------------------------------------------------------------------------------

        void Update()
        {
            if (panel == null) return;
            var research = Hoi4Research.Instance;
            if (Player == 0 || research == null || !research.IsReady || Hoi4.War.Hoi4War.Instance?.Conference != null)
            {
                if (open) Close();
                return;
            }
            if (global::Utils.KeyboardFocus.KeyDown(KeyCode.W)) Toggle();
            if (open && global::Utils.KeyboardFocus.KeyDown(KeyCode.Escape)) Close();
            Hoi4UiHover.Report(this, open ? panel : null);
            if (!open) return;

            var slots = research.Slots(Player);
            if (shownCountry != Player || entries.Count != slots.Count) Rebuild(slots.Count);
            // HOI4 blends the two glow frames (buttonstate_blendframes, 1 fps): fade frame 1 in and out over frame 0
            // (real time, presentation only)
            float blend = 0.5f - 0.5f * Mathf.Cos(Time.unscaledTime * Mathf.PI);
            for (int i = 0; i < entries.Count; i++) entries[i].Q("rs-glow-b").style.opacity = blend;
            if (Time.unscaledTime < nextRefresh) return;
            nextRefresh = Time.unscaledTime + RefreshSeconds;
            Refresh(research, slots);
        }

        void Rebuild(int count)
        {
            shownCountry = Player;
            while (entries.Count < count)
            {
                var e = slotTree.Instantiate().Q(className: "rs-entry");
                Hoi4Sprites.Bind(e);
                entries.Add(e);
            }
            while (entries.Count > count) entries.RemoveAt(entries.Count - 1);
            slotList.Clear();
            foreach (var e in entries) slotList.Add(e);
            nextRefresh = 0f;
        }

        void Refresh(Hoi4Research research, IReadOnlyList<ResearchSlot> slots)
        {
            FP s = research.Speed(shownCountry);
            int pct = Mathf.RoundToInt(s.ToFloat() * 100f);
            speed.text = pct >= 0 ? $"+{pct}%" : $"{pct}%";
            speed.EnableInClassList("negative", pct < 0);

            for (int i = 0; i < entries.Count && i < slots.Count; i++)
            {
                var e = entries[i];
                var slot = slots[i];
                bool empty = slot.Technology == null;
                e.Q("rs-glow").style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
                e.Q("rs-glow-b").style.display = empty ? DisplayStyle.Flex : DisplayStyle.None;
                e.Q<Label>("rs-entry-title").text = empty ? EmptyText : slot.Technology;
                // empty: the research saved so far, out of the 30 days a slot can save
                float fill = empty ? Mathf.Clamp01((slot.Saved / Hoi4Research.SavedMaxDays).ToFloat()) : 0f;
                e.Q("rs-bar").style.width = Mathf.Round(BarWidth * fill);
            }
        }
    }
}
