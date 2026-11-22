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
        // Map stripes (Hoi4MapModes.hlsl Hoi4PeaceOverlay; prototype Tools/prototypes/peace/peace_stripes.py)
        static readonly Color32 OnTable = new Color32(77, 242, 64, 158);   // green, a = band opacity 0.62
        static readonly Color32 Demanded = new Color32(77, 140, 255, 194); // blue, 0.76 (blue reads weaker over the land)
        const int TexWidth = 256;
        static readonly int PeaceTexId = Shader.PropertyToID("_Hoi4PeaceTex");
        static readonly int PeaceActiveId = Shader.PropertyToID("_Hoi4PeaceActive");
        static readonly int PeaceGlowId = Shader.PropertyToID("_Hoi4PeaceGlow");
        static readonly int HideOccupationId = Shader.PropertyToID("_Hoi4HideOccupation");
        Texture2D peaceTex;
        Color32[] peacePixels;

        public static Hoi4PeaceConferencePanel Instance { get; private set; }

        GameState gameState;
        UIDocument document;
        VisualElement root, cards;
        ScrollView statesList, victorsList;
        Label title, score, participation, spentLabel, loserName, loserStates, submitText;
        VisualElement loserFlag;
        readonly object hoverLeft = new object(), hoverRight = new object();

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
            participation = root.Q<Label>("pc-participation");
            spentLabel = root.Q<Label>("pc-spent");
            loserName = root.Q<Label>("pc-loser-name");
            loserStates = root.Q<Label>("pc-loser-states");
            loserFlag = root.Q("pc-loser-flag");
            submitText = root.Q<Label>("pc-submit-text");
            root.Q("pc-reset").RegisterCallback<ClickEvent>(_ => { demands.Clear(); Refresh(); });
            root.Q("pc-done").RegisterCallback<ClickEvent>(_ => Confirm());
            root.Q("pc-submit").RegisterCallback<ClickEvent>(_ => Confirm()); // one turn: submitting ends it
            root.style.display = DisplayStyle.None;
            docRoot.Add(root);

            Hoi4War.Instance.ConferenceOpened += Open;
            Hoi4War.Instance.ConferenceClosed += Close;
            if (Hoi4War.Instance.Conference != null) Open(Hoi4War.Instance.Conference);
        }

        // shader globals outlive play mode in the editor: a session stopped mid-conference would start with the stripes
        // on and occupation hidden
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetShaderGlobals()
        {
            Shader.SetGlobalFloat(PeaceActiveId, 0f);
            Shader.SetGlobalFloat(HideOccupationId, 0f);
        }

        void OnDestroy()
        {
            if (Hoi4War.Instance != null)
            {
                Hoi4War.Instance.ConferenceOpened -= Open;
                Hoi4War.Instance.ConferenceClosed -= Close;
            }
            Hoi4UiHover.Remove(hoverLeft);
            Hoi4UiHover.Remove(hoverRight);
            if (Instance == this) Instance = null;
        }

        string Tag(ushort c) => gameState.Countries.GetCountryTag(c);

        static string CountryName(string tag)
        {
            string name = LocalizationManager.Get(tag);
            return string.IsNullOrEmpty(name) || name == tag ? tag : name;
        }

        /// <summary>The loser's capital's province name (HOI4: "Treaty of Brussels"), else its state's name.</summary>
        static string CapitalName(string tag)
        {
            ushort capital = Hoi4Regions.Capital(tag);
            string key = $"PROV{capital}";
            string name = LocalizationManager.Get(key);
            return string.IsNullOrEmpty(name) || name == key ? StateName(Hoi4Regions.State(capital)) : name;
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
            title.text = $"TREATY OF {CapitalName(loser).ToUpperInvariant()}";
            loserName.text = CountryName(loser);
            loserStates.text = c.States.Count.ToString();
            Hoi4Sprites.Apply(loserFlag, FlagDir + loser + ".png");
            BuildCards();
            Shader.SetGlobalColor(PeaceGlowId, CountryColor(c.Loser));
            Shader.SetGlobalFloat(HideOccupationId, 1f);   // the loser in its own colour, as in HOI4
            Shader.SetGlobalFloat(PeaceActiveId, 1f);
            if (Hoi4UnitCounters.Instance != null) Hoi4UnitCounters.Instance.Hidden = true;
            if (Hoi4BattlePlans.Instance != null) Hoi4BattlePlans.Instance.Hidden = true;
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
            Shader.SetGlobalFloat(PeaceActiveId, 0f);
            Shader.SetGlobalFloat(HideOccupationId, 0f);
            if (Hoi4UnitCounters.Instance != null) Hoi4UnitCounters.Instance.Hidden = false;
            if (Hoi4BattlePlans.Instance != null) Hoi4BattlePlans.Instance.Hidden = false;
            foreach (var (r, was) in hidden) r.style.display = was;
            hidden.Clear();
            Hoi4UiHover.Remove(hoverLeft);
            Hoi4UiHover.Remove(hoverRight);
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
            score.text = $"Score: <color=#f7d45a>{left.ToInt()}</color>";
            var me = conference.WinnerOf(player);
            participation.text = me != null ? $"{(me.Participation * 100).ToInt()}%" : "";
            spentLabel.text = spent.ToInt().ToString();
            submitText.text = demands.Count > 0 ? $"Submit Demands   {spent.ToInt()}" : "Pass";

            statesList.Clear();
            var order = new List<int>(conference.States);
            order.Sort((a, b) => CostNow(b).CompareTo(CostNow(a)));
            FP all = FP.Zero;
            foreach (int s in order) if (!demands.Contains(s)) all += CostNow(s);
            var selectAll = Row(all.ToInt().ToString(), "Select All", "");
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
                var tile = new VisualElement { pickingMode = PickingMode.Ignore };
                tile.AddToClassList("pc-victor");
                tile.EnableInClassList("player", isPlayer);
                var head = Hoi4Sprites.Make("pc-victor-head");
                var frame = Hoi4Sprites.Make("pc-flag-frame", "pc-victor-flag-frame");
                var flag = new VisualElement { pickingMode = PickingMode.Ignore };
                flag.AddToClassList("pc-flag");
                Hoi4Sprites.Apply(flag, FlagDir + Tag(w.Country) + ".png");
                frame.Add(flag);
                head.Add(frame);
                head.Add(Text(CountryName(Tag(w.Country)), "pc-victor-name"));
                if (isPlayer) head.Add(Hoi4Sprites.Make("pc-victor-player"));
                head.Add(Hoi4Sprites.Make("pc-icon", "pc-icon-score", "pc-victor-score-icon"));
                head.Add(Text((isPlayer ? left : w.Score).ToInt().ToString(), "pc-victor-score"));
                head.Add(Hoi4Sprites.Make("pc-icon", "pc-icon-bids", "pc-victor-bids-icon"));
                head.Add(Text(isPlayer ? demands.Count.ToString() : "0", "pc-victor-bids"));
                head.Add(Hoi4Sprites.Make("pc-icon", "pc-icon-contested", "pc-victor-contested-icon"));
                head.Add(Text("0", "pc-victor-contested"));
                head.Add(Hoi4Sprites.Make("pc-icon", "pc-icon-participation", "pc-victor-part-icon"));
                head.Add(Text($"{(w.Participation * 100).ToInt()}%", "pc-victor-part"));
                tile.Add(head);
                if (isPlayer && demands.Count > 0)
                {
                    var list = new VisualElement { pickingMode = PickingMode.Ignore };
                    list.AddToClassList("pc-victor-demands");
                    var done = new List<int>();
                    foreach (int s in demands)
                    {
                        int state = s;
                        var row = Row(conference.Cost(s, player, done).ToInt().ToString(), StateName(s), "demanded");
                        row.RegisterCallback<ClickEvent>(_ => Toggle(state)); // click to take it back
                        list.Add(row);
                        done.Add(s);
                    }
                    tile.Add(list);
                }
                victorsList.Add(tile);
            }

            PaintStripes();
        }

        /// <summary>The loser's states into the province-indexed stripe texture: green on the table, blue demanded.</summary>
        void PaintStripes()
        {
            if (peaceTex == null)
            {
                peaceTex = new Texture2D(TexWidth, TexWidth, TextureFormat.RGBA32, false, linear: true)
                {
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Hoi4PeaceTex"
                };
                peacePixels = new Color32[TexWidth * TexWidth];
            }
            System.Array.Clear(peacePixels, 0, peacePixels.Length);
            foreach (int s in conference.States)
            {
                var colour = demands.Contains(s) ? Demanded : OnTable;
                foreach (ushort p in Hoi4Regions.ProvincesOf(s))
                    if (gameState.Provinces.GetProvinceOwner(p) == conference.Loser) peacePixels[p] = colour;
            }
            peaceTex.SetPixelData(peacePixels, 0);
            peaceTex.Apply(false);
            Shader.SetGlobalTexture(PeaceTexId, peaceTex);
        }

        /// <summary>The country's political map colour (its edge glow).</summary>
        Color CountryColor(ushort country)
        {
            var c = gameState.Countries.GetCountryColor(country);
            return new Color(c.r / 255f, c.g / 255f, c.b / 255f, 1f);
        }

        /// <summary>A conference_action_entry_bg row: score icon, cost, state name.</summary>
        static VisualElement Row(string cost, string name, string look)
        {
            var row = new VisualElement();
            row.AddToClassList("spr");
            row.AddToClassList("pc-row");
            if (!string.IsNullOrEmpty(look)) row.AddToClassList(look);
            Hoi4Sprites.Hook(row);
            row.Add(Hoi4Sprites.Make("pc-icon", "pc-icon-score", "pc-row-icon"));
            row.Add(Text(cost, "pc-row-cost"));
            row.Add(Text(name, "pc-row-name"));
            return row;
        }

        static Label Text(string text, string cls)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
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
                var card = Hoi4Sprites.Make("pc-card");
                card.Add(Hoi4Sprites.Make("pc-icon", "pc-icon-score", "pc-card-icon"));
                card.Add(Text("", "pc-card-text"));
                cards.Add(card);
                cardOf[s] = card;
                cardWorld[s] = world;
            }
        }

        void LateUpdate()
        {
            if (conference == null || cards == null || cards.panel == null) return;
            Hoi4UiHover.Report(hoverLeft, root.Q("pc-actions"));
            Hoi4UiHover.Report(hoverRight, root.Q("pc-right"));
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
