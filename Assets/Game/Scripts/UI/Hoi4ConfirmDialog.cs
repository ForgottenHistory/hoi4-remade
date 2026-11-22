using System;
using System.Collections;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: HOI4's confirmation popup (interface/popupwindow.gui default_confirmation_popup), centred over everything:
    /// a title, a scrolling rich text and Cancel / OK. Used for disbanding divisions and deleting templates. OK (Enter)
    /// runs the action, Cancel (Escape) drops it. Structure is Resources/Hoi4UI/ConfirmDialog.uxml, look is
    /// ConfirmDialogStyle.uss + Hoi4Theme.uss.
    /// </summary>
    public sealed class Hoi4ConfirmDialog : MonoBehaviour
    {
        public static Hoi4ConfirmDialog Instance { get; private set; }

        /// <summary>The dialog is up, or closed this frame (its Escape / Enter shouldn't also reach other screens).</summary>
        public static bool IsOpen => Instance != null && (Instance.open || Instance.closedFrame == Time.frameCount);

        VisualElement dialog;
        Label title, text;
        ScrollView scroll;
        Action onOk;
        bool open;
        int closedFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4ConfirmDialog");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4ConfirmDialog>();
        }

        void Awake() => Instance = this;

        IEnumerator Start()
        {
            var tree = Resources.Load<VisualTreeAsset>("Hoi4UI/ConfirmDialog");
            var theme = Resources.Load<StyleSheet>("Hoi4UI/Hoi4Theme");
            var style = Resources.Load<StyleSheet>("Hoi4UI/ConfirmDialogStyle");
            if (tree == null || theme == null || style == null)
            {
                ArchonLogger.LogWarning("Hoi4ConfirmDialog: Resources/Hoi4UI/ConfirmDialog.uxml / ConfirmDialogStyle.uss missing, dialog disabled", "game_ui");
                yield break;
            }
            PanelSettings settings = null;
            while (settings == null)
            {
                foreach (var doc in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
                    if (doc.panelSettings != null) { settings = doc.panelSettings; break; }
                if (settings == null) yield return null;
            }
            var document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = settings;
            document.sortingOrder = 200; // over every screen
            yield return null;

            var root = document.rootVisualElement;
            root.pickingMode = PickingMode.Ignore;
            root.styleSheets.Add(theme);
            root.styleSheets.Add(style);
            Hoi4Sprites.ApplyFont(root);
            dialog = tree.Instantiate().Q("confirm-dialog");
            Hoi4Sprites.Bind(dialog);
            Hoi4Sprites.Hook(dialog);
            root.Add(dialog);
            title = dialog.Q<Label>("cd-title");
            scroll = dialog.Q<ScrollView>("cd-scroll");
            text = dialog.Q<Label>("cd-text");
            dialog.Q("cd-cancel").RegisterCallback<ClickEvent>(_ => Close());
            dialog.Q("cd-ok").RegisterCallback<ClickEvent>(_ => Accept());
            dialog.style.display = DisplayStyle.None;
        }

        /// <summary>Ask: the title (shown in capitals), the text (rich text), what OK does.</summary>
        public void Show(string titleText, string body, Action ok)
        {
            if (dialog == null) return;
            title.text = titleText.ToUpperInvariant();
            text.text = body;
            scroll.scrollOffset = Vector2.zero;
            onOk = ok;
            open = true;
            dialog.style.display = DisplayStyle.Flex;
        }

        public void Close()
        {
            if (open) closedFrame = Time.frameCount;
            open = false;
            onOk = null;
            if (dialog != null) dialog.style.display = DisplayStyle.None;
        }

        void Accept()
        {
            var ok = onOk;
            Close();
            ok?.Invoke();
        }

        void Update()
        {
            if (dialog == null) return;
            Hoi4UiHover.Report(this, open ? dialog : null); // the wheel scrolls the text, not the map
            if (!open) return;
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
            else if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter)) Accept();
        }

        void OnDestroy()
        {
            Hoi4UiHover.Remove(this);
            if (Instance == this) Instance = null;
        }
    }
}
