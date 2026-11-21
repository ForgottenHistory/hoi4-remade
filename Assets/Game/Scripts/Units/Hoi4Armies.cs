using System;
using System.Collections;
using System.Collections.Generic;
using Core.Units;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>One army: a named group of divisions.</summary>
    public sealed class Hoi4Army
    {
        public int Id;
        public string Name;
        public readonly List<ushort> Units = new List<ushort>();
    }

    /// <summary>
    /// GAME: armies, HOI4's grouping of divisions. There is no size limit; a general's limit (debuffs past it) is not modelled yet. A division belongs to at most one army.
    /// Armies are created from the player's selection and can take more divisions later; an army that
    /// loses its last division disappears. The bottom-of-screen cards are Hoi4ArmyPanel.
    /// </summary>
    public sealed class Hoi4Armies : MonoBehaviour
    {
        public static Hoi4Armies Instance { get; private set; }

        readonly List<Hoi4Army> armies = new List<Hoi4Army>();
        readonly Dictionary<ushort, Hoi4Army> armyOf = new Dictionary<ushort, Hoi4Army>();
        int nextId = 1;
        IDisposable destroyedSub;

        /// <summary>Fires when armies are created, change or are disbanded.</summary>
        public event Action Changed;

        public IReadOnlyList<Hoi4Army> All => armies;

        /// <summary>The army's colour: a fixed hue per army number (golden-ratio spacing keeps neighbours apart).</summary>
        public static Color ColorOf(int armyId, float value = 0.5f) => Color.HSVToRGB((armyId * 0.382f) % 1f, 0.75f, value);

        public Hoi4Army ArmyOf(ushort unitId) => armyOf.TryGetValue(unitId, out var a) ? a : null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (Instance != null) return;
            var go = new GameObject("Hoi4Armies");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Armies>();
        }

        void Awake() => Instance = this;

        IEnumerator Start()
        {
            while (Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.EventBus == null)
                yield return null;
            destroyedSub = Engine.ArchonEngine.Instance.GameState.EventBus.Subscribe<UnitDestroyedEvent>(e => Remove(e.UnitID));
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
            destroyedSub?.Dispose();
        }

        /// <summary>A new army from these divisions (taken out of any army they were in). Null when none.</summary>
        public Hoi4Army Create(IEnumerable<ushort> units)
        {
            var army = new Hoi4Army { Id = nextId++ };
            army.Name = Ordinal(army.Id) + " Army";
            int added = 0;
            foreach (ushort u in units)
            {
                Detach(u);
                army.Units.Add(u);
                armyOf[u] = army;
                added++;
            }
            if (added == 0) return null;
            armies.Add(army);
            Prune();
            Changed?.Invoke();
            return army;
        }

        /// <summary>Move divisions into an army. Returns how many were added.</summary>
        public int Add(Hoi4Army army, IEnumerable<ushort> units)
        {
            int added = 0;
            foreach (ushort u in units)
            {
                if (armyOf.TryGetValue(u, out var current) && current == army) continue;
                Detach(u);
                army.Units.Add(u);
                armyOf[u] = army;
                added++;
            }
            if (added > 0)
            {
                Prune();
                Changed?.Invoke();
            }
            return added;
        }

        public void Disband(Hoi4Army army)
        {
            if (!armies.Remove(army)) return;
            foreach (ushort u in army.Units) armyOf.Remove(u);
            army.Units.Clear();
            Changed?.Invoke();
        }

        void Remove(ushort unitId)
        {
            if (!armyOf.ContainsKey(unitId)) return;
            Detach(unitId);
            Prune();
            Changed?.Invoke();
        }

        void Detach(ushort unitId)
        {
            if (!armyOf.TryGetValue(unitId, out var army)) return;
            army.Units.Remove(unitId);
            armyOf.Remove(unitId);
        }

        void Prune() => armies.RemoveAll(a => a.Units.Count == 0);

        static string Ordinal(int n)
        {
            int mod100 = n % 100;
            string suffix = mod100 >= 11 && mod100 <= 13 ? "th" : (n % 10) == 1 ? "st" : (n % 10) == 2 ? "nd" : (n % 10) == 3 ? "rd" : "th";
            return n + suffix;
        }
    }
}
