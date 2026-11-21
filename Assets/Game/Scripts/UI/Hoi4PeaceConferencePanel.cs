using System.Collections;
using System.Collections.Generic;
using Core;
using Core.Localization;
using Hoi4.Map;
using Hoi4.Units;
using Hoi4.War;
using UnityEngine;
using UnityEngine.UIElements;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: the peace conference screen (basic HOI4 version), shown when a country the player is at war with
    /// capitulates (Hoi4War pauses the game). Left: Make Demands, Take State only: the loser's states with what
    /// each costs the player now (cheaper next to states already demanded), Select All, Reset. Right: the victors
    /// with war participation and score, and the player's demands. On the map a cost card sits on each state and
    /// a click demands it or takes the demand back. Confirm and Exit sends PeaceDealCommand; the AI victors then
    /// make their demands and the deal is carried out (Hoi4PeaceConference). Unit counters are hidden meanwhile.
    /// View of the UI Presenter pattern: PeaceConference.uxml + PeaceConferenceStyle.uss + Hoi4Theme.uss.
    /// </summary>
    public sealed class Hoi4PeaceConferencePanel : MonoBehaviour
    {
        const string FlagDir = "gfx/interface/topbar/flags/";
        static readonly Color DemandOutline = new Color(0.45f, 0.65f, 1f, 1f);

        public static Hoi4PeaceConferencePanel Instance { get; private set; }

        GameState gameState;
        UIDocument document;
        VisualElement root, cards;
        ScrollView statesList, victorsList;
        Label title, score, loserName;
        VisualElement loserFlag;

        Hoi4PeaceConference conference;
        ushort player;
        readonly List<int> demands = new List<int>();   // the player's, in the order made
        readonly Dictionary<int, VisualElement> cardOf = new Dictionary<int, VisualElement>();
        readonly Dictionary<int, Vector3> cardWorld = new Dictionary<int, Vector3>();
        // The rest of the UI is hidden while the conference is open (HOI4: the conference is the whole screen)
        readonly List<(VisualElement root, StyleEnum<DisplayStyle> was)> hidden = new List<(VisualElement, StyleEnum<DisplayStyle>)>();

        public bool IsOpen => conference != null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4PeaceConferencePanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4PeaceConferencePanel>();
        }

        void Awake() => Instance = this;

        IEnumerator Start()
        {
            while (Hoi4War.Instance == null || !Hoi4War.Instance.IsReady || Engine.ArchonEngine.Instance == null)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/PeaceConference");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/PeaceConferenceStyle");
            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (tree == null || theme == null || style == null || settings == null)
            {
                ArchonLogger.LogWarning("Hoi4PeaceConferencePanel: PeaceConference.uxml, its stylesheets or panel settings missing; conferences are decided by the AI", "game_ui");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 70; // above the other HOI4 panels
            yield return null;

            var docRoot = document.rootVisualElement;
            docRoot.pickingMode = PickingMode.Ignore;
            docRoot.styleSheets.Add(theme);
            docRoot.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(docRoot);
            root = tree.Instantiate().Q("pc-root");
            Hoi4Sprites.Bind(root);
            cards = root.Q("pc-cards");
            statesList = root.Q<ScrollView>("pc-states");
            victorsList = root.Q<ScrollView>("pc-victors");
            title = root.Q<Label>("pc-title");
            score = root.Q<Label>("pc-score");
            loserName = root.Q<Label>("pc-loser-name");
            loserFlag = root.Q("pc-loser-flag");
            root.Q("pc-reset").RegisterCallback<ClickEvent>(_ => { demands.Clear(); Refresh(); });
            root.Q("pc-confirm").RegisterCallback<ClickEvent>(_ => Confirm());
            root.style.display = DisplayStyle.None;
            docRoot.Add(root);

            Hoi4War.Instance.ConferenceOpened += Open;
            Hoi4War.Instance.ConferenceClosed += Close;
            if (Hoi4War.Instance.Conference != null) Open(Hoi4War.Instance.Conference);
        }

        void OnDestroy()
        {
            if (Hoi4War.Instance != null)
            {
                Hoi4War.Instance.ConferenceOpened -= Open;
                Hoi4War.Instance.ConferenceClosed -= Close;
            }
            Hoi4UiHover.Remove(this);
            if (Instance == this) Instance = null;
        }

        string Tag(ushort c) => gameState.Countries.GetCountryTag(c);

        static string CountryName(string tag)
        {
            string name = LocalizationManager.Get(tag);
            return string.IsNullOrEmpty(name) || name == tag ? tag : name;
        }

        static string StateName(int state)
        {
            string key = $"STATE_{state}";
            string name = LocalizationManager.Get(key);
            return string.IsNullOrEmpty(name) || name == key ? $"State {state}" : name;
        }

        void Open(Hoi4PeaceConference c)
        {
            if (root == null) return;
            conference = c;
            player = StarterKit.Initializer.Instance?.PlayerState?.PlayerCountryId ?? 0;
            demands.Clear();
            string loser = Tag(c.Loser);
            title.text = $"TREATY OF {StateName(Hoi4Regions.State(Hoi4Regions.Capital(loser))).ToUpperInvariant()}";
            loserName.text = CountryName(loser);
            Hoi4Sprites.Apply(loserFlag, FlagDir + loser + ".png");
            BuildCards();
            if (Hoi4UnitCounters.Instance != null) Hoi4UnitCounters.Instance.Hidden = true;
            HideOtherUi();
            root.style.display = DisplayStyle.Flex;
            Refresh();
        }

        void Close()
        {
            conference = null;
            demands.Clear();
            if (root != null) root.style.display = DisplayStyle.None;
            cards?.Clear();
            cardOf.Clear();
            cardWorld.Clear();
            Engine.ArchonEngine.Instance?.ProvinceHighlighter?.ClearProvinceSet();
            if (Hoi4UnitCounters.Instance != null) Hoi4UnitCounters.Instance.Hidden = false;
            foreach (var (r, was) in hidden) r.style.display = was;
            hidden.Clear();
            Hoi4UiHover.Remove(this);
        }

        /// <summary>Every other UI document's root hidden (top bar, panels, army cards, plan window, labels).</summary>
        void HideOtherUi()
        {
            hidden.Clear();
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                if (doc == document || doc.rootVisualElement == null) continue;
                var r = doc.rootVisualElement;
                hidden.Add((r, r.style.display));
                r.style.display = DisplayStyle.None;
            }
            Hoi4UnitSelection.Instance?.Clear(); // no orders from a hidden selection
        }

        // --- State of the demands -------------------------------------------------------------------------

        FP PlayerScore => conference?.WinnerOf(player)?.Score ?? FP.Zero;
        FP Spent => conference.Spent(player, demands);
        FP CostNow(int s) => conference.Cost(s, player, demands);

        void Toggle(int state)
        {
            if (conference == null || !conference.States.Contains(state)) return;
            if (demands.Remove(state)) { Refresh(); return; }
            if (CostNow(state) > PlayerScore - Spent) return; // can't afford it
            demands.Add(state);
            Refresh();
        }

        /// <summary>Every state it can still afford, best value for the cost first (cheaper ones next to earlier ones).</summary>
        void SelectAll()
        {
            while (true)
            {
                FP left = PlayerScore - Spent;
                int best = -1; FP bestRatio = FP.Zero;
                foreach (int s in conference.States)
                {
                    if (demands.Contains(s)) continue;
                    FP cost = CostNow(s);
                    if (cost > left || cost <= FP.Zero) continue;
                    FP ratio = Hoi4PeaceConference.Value(s) / cost;
                    if (best < 0 || ratio > bestRatio) { best = s; bestRatio = ratio; }
                }
                if (best < 0) break;
                demands.Add(best);
            }
            Refresh();
        }

        void Confirm()
        {
            if (conference == null) return;
            var cmd = new PeaceDealCommand { CountryId = player, Loser = conference.Loser, States = new List<int>(demands) };
            if (!gameState.TryExecuteCommand(cmd, out _))
                ArchonLogger.LogWarning("Hoi4PeaceConferencePanel: the peace deal was refused", "game_ui");
        }

        // --- Drawing --------------------------------------------------------------------------------------

        void Refresh()
        {
            if (conference == null) return;
            FP total = PlayerScore, spent = Spent, left = total - spent;
            score.text = $"Score: {left.ToInt()} / {total.ToInt()}";

            statesList.Clear();
            var order = new List<int>(conference.States);
            order.Sort((a, b) => CostNow(b).CompareTo(CostNow(a)));
            FP all = FP.Zero;
            foreach (int s in order) if (!demands.Contains(s)) all += CostNow(s);
            var selectAll = Row($"{all.ToInt()}", "Select All", "");
            selectAll.RegisterCallback<ClickEvent>(_ => SelectAll());
            statesList.Add(selectAll);
            foreach (int s in order)
            {
                bool mine = demands.Contains(s);
                FP cost = mine ? conference.Cost(s, player, demands.FindAll(x => x != s)) : CostNow(s);
                string look = mine ? "demanded" : cost > left ? "unaffordable" : "";
                int state = s;
                var row = Row(cost.ToInt().ToString(), StateName(s), look);
                row.RegisterCallback<ClickEvent>(_ => Toggle(state));
                statesList.Add(row);
                if (cardOf.TryGetValue(s, out var card))
                {
                    card.Q<Label>().text = cost.ToInt().ToString();
                    card.EnableInClassList("demanded", mine);
                    card.EnableInClassList("unaffordable", look == "unaffordable");
                }
            }

            victorsList.Clear();
            foreach (var w in conference.Winners)
            {
                bool isPlayer = w.Country == player;
                var tile = new VisualElement();
                tile.AddToClassList("pc-victor");
                tile.EnableInClassList("player", isPlayer);
                var head = new VisualElement();
                head.AddToClassList("pc-victor-head");
                var flag = new VisualElement();
                flag.AddToClassList("pc-flag");
                Hoi4Sprites.Apply(flag, FlagDir + Tag(w.Country) + ".png");
                head.Add(flag);
                head.Add(Text(CountryName(Tag(w.Country)), "pc-victor-name"));
                FP have = isPlayer ? left : w.Score;
                head.Add(Text($"{(w.Participation * 100).ToInt()}% participation   score {have.ToInt()}", "pc-victor-stats"));
                tile.Add(head);
                if (isPlayer)
                    foreach (int s in demands)
                    {
                        var l = new Label(StateName(s));
                        l.AddToClassList("pc-victor-demand");
                        tile.Add(l);
                    }
                victorsList.Add(tile);
            }

            var set = new HashSet<ushort>();
            foreach (int s in demands) foreach (ushort p in Hoi4Regions.ProvincesOf(s)) set.Add(p);
            var highlighter = Engine.ArchonEngine.Instance?.ProvinceHighlighter;
            if (set.Count > 0) highlighter?.HighlightProvinceSet(set.Contains, DemandOutline);
            else highlighter?.ClearProvinceSet();
        }

        static VisualElement Row(string cost, string name, string look)
        {
            var row = new VisualElement();
            row.AddToClassList("pc-row");
            if (!string.IsNullOrEmpty(look)) row.AddToClassList(look);
            var c = new Label(cost);
            c.AddToClassList("pc-row-cost");
            var n = new Label(name);
            n.AddToClassList("pc-row-name");
            row.Add(c);
            row.Add(n);
            return row;
        }

        static Label Text(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList("hoi4-label");
            l.AddToClassList(cls);
            return l;
        }

        /// <summary>A cost card per state, at its most valuable victory point (else its first province).</summary>
        void BuildCards()
        {
            cards.Clear();
            cardOf.Clear();
            cardWorld.Clear();
            var counters = Hoi4UnitCounters.Instance;
            if (counters == null) return;
            foreach (int s in conference.States)
            {
                var info = Hoi4StateData.Get(s);
                ushort at = 0; int best = -1;
                if (info != null)
                    foreach (var kv in info.VictoryPoints) if (kv.Value > best) { best = kv.Value; at = (ushort)kv.Key; }
                if (at == 0)
                    foreach (ushort p in Hoi4Regions.ProvincesOf(s)) if (!Hoi4Regions.IsWater(p)) { at = p; break; }
                if (at == 0 || !counters.TryGetProvincePoint(at, out var world)) continue;
                var card = new VisualElement();
                card.AddToClassList("pc-card");
                card.pickingMode = PickingMode.Ignore;
                var l = new Label();
                l.AddToClassList("hoi4-label");
                l.AddToClassList("center");
                l.AddToClassList("pc-card-text");
                l.pickingMode = PickingMode.Ignore;
                card.Add(l);
                cards.Add(card);
                cardOf[s] = card;
                cardWorld[s] = world;
            }
        }

        void LateUpdate()
        {
            if (conference == null || cards == null || cards.panel == null) return;
            Hoi4UiHover.Report(this, root.Q("pc-left"));
            var cam = Camera.main;
            if (cam == null) return;
            foreach (var kv in cardOf)
            {
                var sp = cam.WorldToScreenPoint(cardWorld[kv.Key]);
                if (sp.z <= 0f) { kv.Value.style.display = DisplayStyle.None; continue; }
                var pp = RuntimePanelUtils.ScreenToPanel(cards.panel, new Vector2(sp.x, Screen.height - sp.y));
                kv.Value.style.display = DisplayStyle.Flex;
                kv.Value.style.left = pp.x;
                kv.Value.style.top = pp.y;
            }
        }

        /// <summary>From Hoi4UnitSelection's click interceptor: while the conference is open every map click is
        /// ours; a left click on one of the loser's states demands it or takes the demand back.</summary>
        public bool OnClick(int button, Vector2 screen)
        {
            if (conference == null) return false;
            if (button != 0) return true;
            var cam = Camera.main;
            var selector = Engine.ArchonEngine.Instance?.ProvinceSelector;
            if (cam == null || selector == null) return true;
            ushort p = selector.GetProvinceAtScreenPosition(screen, cam);
            if (p != 0) Toggle(Hoi4Regions.State(p));
            return true;
        }
    }
}
