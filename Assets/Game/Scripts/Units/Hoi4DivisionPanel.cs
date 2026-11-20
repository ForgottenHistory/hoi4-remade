using System.Collections;
using Core.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: bottom-left panel listing the selected divisions, like HOI4's army panel: name,
    /// template, where it is or where it's going, organisation and strength bars. Clicking a card
    /// selects only that division. Uses the scene's existing UI Toolkit panel settings.
    /// </summary>
    public sealed class Hoi4DivisionPanel : MonoBehaviour
    {
        const int MaxCards = 60;

        static readonly Color Background = new Color(0.07f, 0.07f, 0.07f, 0.93f);
        static readonly Color Border = new Color(0.55f, 0.48f, 0.3f, 1f);
        static readonly Color CardBackground = new Color(0.13f, 0.13f, 0.12f, 1f);
        static readonly Color CardOwn = new Color(0.14f, 0.17f, 0.12f, 1f);
        static readonly Color TextMain = new Color(0.93f, 0.91f, 0.85f, 1f);
        static readonly Color TextDim = new Color(0.65f, 0.63f, 0.58f, 1f);
        static readonly Color OrgColor = new Color(0.38f, 0.78f, 0.36f, 1f);
        static readonly Color StrColor = new Color(0.86f, 0.66f, 0.32f, 1f);
        static readonly Color BarBack = new Color(0.2f, 0.2f, 0.2f, 1f);

        UIDocument document;
        VisualElement panel;
        Label header;
        ScrollView list;
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
            Build();
            Hoi4UnitSelection.Instance.Changed += () => dirty = true;
            dirty = true;
        }

        void Build()
        {
            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;

            panel = new VisualElement { name = "hoi4-division-panel" };
            var st = panel.style;
            st.position = Position.Absolute;
            st.left = 12; st.bottom = 12;
            st.width = 330;
            st.maxHeight = Length.Percent(60);
            st.backgroundColor = Background;
            st.borderTopWidth = st.borderBottomWidth = st.borderLeftWidth = st.borderRightWidth = 1;
            st.borderTopColor = st.borderBottomColor = st.borderLeftColor = st.borderRightColor = Border;
            st.borderTopLeftRadius = st.borderTopRightRadius = st.borderBottomLeftRadius = st.borderBottomRightRadius = 4;
            st.paddingTop = st.paddingBottom = st.paddingLeft = st.paddingRight = 6;
            st.display = DisplayStyle.None;

            header = new Label();
            header.style.color = TextMain;
            header.style.unityFontStyleAndWeight = FontStyle.Bold;
            header.style.fontSize = 14;
            header.style.marginBottom = 4;
            panel.Add(header);

            list = new ScrollView(ScrollViewMode.Vertical);
            list.style.flexShrink = 1;
            panel.Add(list);
            root.Add(panel);
        }

        void Update()
        {
            if (!dirty || panel == null) return;
            dirty = false;
            Refresh();
        }

        void Refresh()
        {
            var selection = Hoi4UnitSelection.Instance;
            var divisions = Hoi4Divisions.Instance;
            var gameState = Engine.ArchonEngine.Instance?.GameState;
            if (selection == null || divisions == null || gameState == null || selection.Selected.Count == 0)
            {
                panel.style.display = DisplayStyle.None;
                return;
            }
            panel.style.display = DisplayStyle.Flex;

            var selected = selection.Selected;
            if (selected.Count == 1)
                header.text = divisions.Get(selected[0])?.Name ?? "Division";
            else
                header.text = $"{selected.Count} divisions";

            ushort player = StarterKit.Initializer.Instance?.PlayerState?.PlayerCountryId ?? 0;
            list.Clear();
            int shown = 0;
            foreach (ushort id in selected)
            {
                if (shown++ >= MaxCards)
                {
                    var more = new Label($"+{selected.Count - MaxCards} more");
                    more.style.color = TextDim;
                    list.Add(more);
                    break;
                }
                var d = divisions.Get(id);
                var unit = gameState.Units.GetUnit(id);
                list.Add(Card(id, d, unit, unit.countryID == player, gameState));
            }
        }

        VisualElement Card(ushort id, Hoi4Division d, Core.Units.UnitState unit, bool own, Core.GameState gameState)
        {
            var card = new VisualElement();
            var st = card.style;
            st.backgroundColor = own ? CardOwn : CardBackground;
            st.marginBottom = 3;
            st.paddingTop = st.paddingBottom = 4;
            st.paddingLeft = st.paddingRight = 6;
            st.borderTopLeftRadius = st.borderTopRightRadius = st.borderBottomLeftRadius = st.borderBottomRightRadius = 3;

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.justifyContent = Justify.SpaceBetween;
            var name = new Label(d?.Name ?? $"Unit {id}");
            name.style.color = TextMain;
            name.style.unityFontStyleAndWeight = FontStyle.Bold;
            name.style.fontSize = 12;
            name.style.flexShrink = 1;
            top.Add(name);
            var tag = new Label(d?.Tag ?? "");
            tag.style.color = TextDim;
            tag.style.fontSize = 11;
            top.Add(tag);
            card.Add(top);

            var template = new Label(d != null ? $"{d.Template} · {d.CombatWidth:0} width · {d.SpeedKmh:0.#} km/h" : "");
            template.style.color = TextDim;
            template.style.fontSize = 11;
            card.Add(template);

            var status = new Label(Status(id, unit, gameState));
            status.style.color = TextDim;
            status.style.fontSize = 11;
            status.style.marginBottom = 3;
            card.Add(status);

            card.Add(Bar(d != null && d.MaxOrganisation > 0 ? d.Organisation / d.MaxOrganisation : 1f, OrgColor));
            card.Add(Bar(d != null && d.MaxStrength > 0 ? d.Strength / d.MaxStrength : 1f, StrColor));

            card.RegisterCallback<ClickEvent>(_ => Hoi4UnitSelection.Instance?.Select(new[] { id }));
            return card;
        }

        static string Status(ushort id, Core.Units.UnitState unit, Core.GameState gameState)
        {
            var queue = gameState.Units.MovementQueue;
            if (queue != null && queue.TryGetMovementState(id, out var state))
            {
                var path = queue.GetFullPath(id);
                ushort goal = path.Count > 0 ? path[path.Count - 1] : state.destinationProvinceID;
                int hops = Mathf.Max(path.Count - 1, 1);
                return $"Moving to {ProvinceName(goal)} · {hops} province{(hops == 1 ? "" : "s")} left, next in {state.daysRemaining}d";
            }
            return $"In {ProvinceName(unit.provinceID)}";
        }

        static string ProvinceName(ushort province)
        {
            string key = $"PROV{province}";
            string name = LocalizationManager.Get(key);
            return string.IsNullOrEmpty(name) || name == key ? $"province {province}" : name;
        }

        static VisualElement Bar(float fill, Color color)
        {
            var back = new VisualElement();
            back.style.height = 4;
            back.style.marginTop = 1;
            back.style.backgroundColor = BarBack;
            var front = new VisualElement();
            front.style.height = Length.Percent(100);
            front.style.width = Length.Percent(Mathf.Clamp01(fill) * 100f);
            front.style.backgroundColor = color;
            back.Add(front);
            return back;
        }
    }
}
