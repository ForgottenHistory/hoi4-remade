using System.Collections;
using Map.MapModes;
using UnityEngine;

namespace Hoi4.Map
{
    /// <summary>
    /// GAME: map mode hotkeys, like HOI4's: F1 the political map (the default), F4 supply (Hoi4SupplyMapMode,
    /// registered here). Keys stand aside while the player types (KeyboardFocus).
    /// </summary>
    public sealed class Hoi4MapModeKeys : MonoBehaviour
    {
        MapModeManager mapModes;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4MapModeKeys");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4MapModeKeys>();
        }

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.MapModeManager == null
                   || !Engine.ArchonEngine.Instance.MapModeManager.IsInitialized)
                yield return null;
            var manager = Engine.ArchonEngine.Instance.MapModeManager;
            if (Hoi4.Supply.Hoi4SupplyMapMode.Register(manager) == null)
            {
                ArchonLogger.LogWarning("Hoi4MapModeKeys: could not register the supply map mode", "game_ui");
                yield break;
            }
            mapModes = manager;
        }

        void Update()
        {
            if (mapModes == null) return;
            if (Utils.KeyboardFocus.KeyDown(KeyCode.F1)) mapModes.SetMapMode(MapMode.Political);
            else if (Utils.KeyboardFocus.KeyDown(KeyCode.F4)) mapModes.SetMapMode(MapMode.Supply);
        }
    }
}
