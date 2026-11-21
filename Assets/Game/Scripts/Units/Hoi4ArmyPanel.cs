using System.Collections;
using System.Collections.Generic;
using Hoi4.UI;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the army cards at the bottom of the screen (HOI4's leader group row, without field
    /// marshals). One card per army of the player's: left-click selects its divisions, right-click adds the
    /// selected divisions to it, the small cross disbands it. The plus card after them makes a new army
    /// from the selected divisions, and is grey while none of the player's are selected. An army with a front
    /// line gets HOI4's battle plan stop / play buttons above its card (ExecuteBattlePlanCommand); while its
    /// plans run, chevrons scroll over the play button.
    /// View of the UI Presenter pattern: structure is Resources/Hoi4UI/ArmyPanel.uxml, look is
    /// ArmyPanelStyle.uss + Hoi4Theme.uss; the cards themselves are built here, one per army.
    /// </summary>
    public sealed class Hoi4ArmyPanel : MonoBehaviour
    {
        const string Dir = "gfx/interface/armies/";

        UIDocument document;
        VisualElement bar, list, add;
        bool dirty = true;
        readonly List<VisualElement> anims = new List<VisualElement>(); // play-button chevrons of running armies
        int animFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4ArmyPanel");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4ArmyPanel>();
        }

        IEnumerator Start()
        {
            while (Hoi4UnitSelection.Instance == null || Hoi4Armies.Instance == null || Hoi4BattlePlans.Instance == null)
                yield return null;

            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/ArmyPanel");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/ArmyPanelStyle");
            if (tree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4ArmyPanel: Resources/Hoi4UI/ArmyPanel.uxml or its stylesheets are missing, panel disabled", "game_units");
                yield break;
            }

            // Borrow the panel settings (theme, scaling) of the scene's UI
            PanelSettings settings = null;
            foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
            if (settings == null)
            {
                ArchonLogger.LogWarning("Hoi4ArmyPanel: no UIDocument panel settings in the scene, panel disabled", "game_units");
                yield break;
            }
            document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 45;
            yield return null; // let the document create its root

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);

            bar = tree.Instantiate().Q("army-bar");
            list = bar.Q("army-list");
            add = bar.Q("add-army");
            add.RegisterCallback<PointerDownEvent>(OnAddPressed);
            Hoi4Sprites.Apply(bar.Q("add-frame"), Dir + "frame_0.png");
            root.Add(bar);

            Hoi4UnitSelection.Instance.Changed += OnChanged;
            Hoi4Armies.Instance.Changed += OnChanged;
            Hoi4BattlePlans.Instance.Changed += OnChanged;
        }

        void OnChanged() => dirty = true;

        void OnDestroy()
        {
            if (Hoi4UnitSelection.Instance != null) Hoi4UnitSelection.Instance.Changed -= OnChanged;
            if (Hoi4Armies.Instance != null) Hoi4Armies.Instance.Changed -= OnChanged;
            if (Hoi4BattlePlans.Instance != null) Hoi4BattlePlans.Instance.Changed -= OnChanged;
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

        /// <summary>The selected divisions that belong to the player (others can be inspected but not ordered).</summary>
        List<ushort> SelectedOwn(ushort player)
        {
            var result = new List<ushort>();
            var units = Engine.ArchonEngine.Instance?.GameState?.Units;
            if (units == null) return result;
            foreach (ushort id in Hoi4UnitSelection.Instance.Selected)
                if (units.HasUnit(id) && units.GetUnit(id).countryID == player) result.Add(id);
            return result;
        }

        void Update()
        {
            if (bar == null) return;
            ushort player = Player;
            bar.style.display = player == 0 ? DisplayStyle.None : DisplayStyle.Flex;
            Hoi4UiHover.Report(this, bar);
            if (player == 0) return;
            if (dirty)
            {
                dirty = false;
                Refresh(player);
            }
            // HOI4 GFX_active_battleplan_anim: 12 frames at 24 fps
            int frame = Mathf.FloorToInt(Time.unscaledTime * 24f) % 12;
            if (frame != animFrame && anims.Count > 0)
            {
                animFrame = frame;
                foreach (var a in anims) Hoi4Sprites.Apply(a, Dir + $"exec_anim_{frame}.png");
            }
        }

        void Refresh(ushort player)
        {
            var selection = Hoi4UnitSelection.Instance;
            var armies = Hoi4Armies.Instance;

            list.Clear();
            anims.Clear();
            animFrame = -1;
            foreach (var army in armies.All)
            {
                bool selected = army.Units.Count > 0;
                foreach (ushort u in army.Units)
                    if (!selection.IsSelected(u)) selected = false;
                list.Add(Card(army, selected));
            }

            bool canAdd = SelectedOwn(player).Count > 0;
            Hoi4Sprites.Apply(add, Dir + (canAdd ? "add.png" : "add_grey.png"));
            add.EnableInClassList("hoi4-button", canAdd); // a grey plus doesn't react to the pointer
        }

        VisualElement Card(Hoi4Army army, bool selected)
        {
            // The card's outer box includes the strip for the play / stop buttons (so the hover check that keeps
            // clicks off the map covers them); the body is the HOI4 card below it
            var outer = new VisualElement { pickingMode = PickingMode.Ignore };
            outer.AddToClassList("army-card");
            var plans = Hoi4BattlePlans.Instance;
            if (plans != null && plans.HasFrontLine(army.Id)) AddExecuteButtons(outer, army, plans.IsExecuting(army.Id));

            var card = new VisualElement { pickingMode = PickingMode.Position };
            card.AddToClassList("ac-body");
            outer.Add(card);

            var portrait = new VisualElement { pickingMode = PickingMode.Ignore };
            portrait.AddToClassList("ac-portrait");
            Hoi4Sprites.Apply(portrait, Dir + "portrait.png");
            card.Add(portrait);

            var frame = new VisualElement { pickingMode = PickingMode.Ignore };
            frame.AddToClassList("ac-frame");
            Hoi4Sprites.Apply(frame, Dir + (selected ? "frame_1.png" : "frame_0.png"));
            card.Add(frame);

            var count = new Label(army.Units.Count.ToString()) { pickingMode = PickingMode.Ignore };
            count.AddToClassList("ac-count");
            card.Add(count);

            var disband = new VisualElement { pickingMode = PickingMode.Position };
            disband.AddToClassList("ac-disband");
            disband.AddToClassList("hoi4-button");
            Hoi4Sprites.Apply(disband, Dir + "disband.png");
            disband.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button != 0) return;
                Engine.ArchonEngine.Instance.GameState.TryExecuteCommand(new DisbandArmyCommand { CountryId = Player, ArmyId = army.Id }, out _);
                e.StopPropagation();
            });
            card.Add(disband);

            card.RegisterCallback<PointerDownEvent>(e =>
            {
                if (e.button == 0)
                {
                    Hoi4UnitSelection.Instance.Select(army.Units); // the whole army
                }
                else if (e.button == 1)
                {
                    var own = SelectedOwn(Player);
                    if (own.Count > 0)
                        Engine.ArchonEngine.Instance.GameState.TryExecuteCommand(new AddToArmyCommand { CountryId = Player, ArmyId = army.Id, UnitIds = new System.Collections.Generic.List<ushort>(own) }, out _);
                }
                e.StopPropagation();
            });
            return outer;
        }

        /// <summary>leadergroups.gui btn_stop / btn_execute on their background. Idle: the play button's not-running
        /// frame (1). Running: its plan-in-motion frame (0) over btn_execute_anim, the animated background (12
        /// frames, 24 fps). plan_evaluation sits on the play button: HOI4 shows whether the general is confident in
        /// the plan (! / : / tick); always the tick here.</summary>
        void AddExecuteButtons(VisualElement outer, Hoi4Army army, bool running)
        {
            var bg = new VisualElement { pickingMode = PickingMode.Ignore };
            bg.AddToClassList("ac-exec-bg");
            Hoi4Sprites.Apply(bg, Dir + "exec_bg.png");
            outer.Add(bg);

            var stop = new VisualElement { pickingMode = PickingMode.Position };
            stop.AddToClassList("ac-stop");
            stop.AddToClassList("hoi4-button");
            Hoi4Sprites.Apply(stop, Dir + (running ? "exec_stop_1.png" : "exec_stop_0.png"));
            stop.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) Run(army, false); e.StopPropagation(); });
            outer.Add(stop);

            if (running)
            {
                var anim = new VisualElement { pickingMode = PickingMode.Ignore }; // under the arrow
                anim.AddToClassList("ac-play-anim");
                outer.Add(anim);
                anims.Add(anim);
            }

            var play = new VisualElement { pickingMode = PickingMode.Position };
            play.AddToClassList("ac-play");
            play.AddToClassList("hoi4-button");
            Hoi4Sprites.Apply(play, Dir + (running ? "exec_play_0.png" : "exec_play_1.png"));
            play.RegisterCallback<PointerDownEvent>(e => { if (e.button == 0) Run(army, true); e.StopPropagation(); });
            outer.Add(play);

            var grade = new VisualElement { pickingMode = PickingMode.Ignore };
            grade.AddToClassList("ac-plan-value");
            Hoi4Sprites.Apply(grade, Dir + "plan_value_3.png");
            outer.Add(grade);
        }

        static void Run(Hoi4Army army, bool run) =>
            Engine.ArchonEngine.Instance.GameState.TryExecuteCommand(new ExecuteBattlePlanCommand { CountryId = Player, ArmyId = army.Id, Run = run }, out _);

        void OnAddPressed(PointerDownEvent e)
        {
            if (e.button != 0) return;
            var own = SelectedOwn(Player);
            if (own.Count == 0) return;
            Engine.ArchonEngine.Instance.GameState.TryExecuteCommand(new CreateArmyCommand { CountryId = Player, UnitIds = new System.Collections.Generic.List<ushort>(own) }, out _);
            e.StopPropagation();
        }
    }
}
