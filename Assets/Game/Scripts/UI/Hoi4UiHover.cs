using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: lets several panels share the engine camera's ScrollCaptured flag. Each panel reports every
    /// frame whether the pointer is over it; the camera ignores the mouse wheel while any panel says yes.
    /// </summary>
    public static class Hoi4UiHover
    {
        static readonly Dictionary<object, bool> over = new Dictionary<object, bool>();

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() => over.Clear();

        /// <summary>Report whether the pointer is over a visible panel element (call once per frame).</summary>
        public static void Report(object owner, VisualElement panel)
        {
            bool hit = false;
            if (panel != null && panel.panel != null && panel.resolvedStyle.display != DisplayStyle.None)
            {
                Vector2 mp = Input.mousePosition;
                mp.y = Screen.height - mp.y;
                hit = panel.worldBound.Contains(RuntimePanelUtils.ScreenToPanel(panel.panel, mp));
            }
            over[owner] = hit;
            bool any = false;
            foreach (var v in over.Values) any |= v;
            global::Map.CameraControllers.BaseCameraController.ScrollCaptured = any;
        }

        public static void Remove(object owner)
        {
            over.Remove(owner);
            bool any = false;
            foreach (var v in over.Values) any |= v;
            global::Map.CameraControllers.BaseCameraController.ScrollCaptured = any;
        }
    }
}
