using System.Collections.Generic;
using UnityEngine.SceneManagement;

namespace Hoi4
{
    /// <summary>
    /// GAME: the scenes that run the HOI4 game layer. The game's scripts start themselves with
    /// RuntimeInitializeOnLoadMethod (no scene setup needed), and Unity runs those for every scene,
    /// the engine's own StarterKit scene included. Each of them returns early unless Active.
    /// Add a scene here to run HOI4 in it.
    /// </summary>
    public static class Hoi4Scenes
    {
        static readonly HashSet<string> Names = new HashSet<string> { "Hoi4Test" };

        /// <summary>True when a loaded scene is a HOI4 scene (also valid in BeforeSceneLoad: the scene is loaded, not yet awake).</summary>
        public static bool Active
        {
            get
            {
                for (int i = 0; i < SceneManager.sceneCount; i++)
                    if (Names.Contains(SceneManager.GetSceneAt(i).name)) return true;
                return false;
            }
        }
    }
}
