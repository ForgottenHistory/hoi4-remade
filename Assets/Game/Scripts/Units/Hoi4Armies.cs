using System;
using System.Collections;
using System.Collections.Generic;
using Core.Units;
using UnityEngine;

namespace Hoi4.Units
{
    /// <summary>One army: a named group of divisions of one country.</summary>
    public sealed class Hoi4Army
    {
        public int Id;
        public ushort Owner;                // the country whose divisions these are
        public string Name;
        public readonly List<ushort> Units = new List<ushort>();
    }

    /// <summary>
    /// GAME: armies, HOI4's grouping of divisions. There is no size limit; a general's limit (debuffs past it) is not modelled yet. A division belongs to at most one army.
    /// The player's armies are created from the selection and can take more divisions later; an AI country's
    /// are made by its war AI (Hoi4BattlePlans.AICountry, Docs/ai-layers.md) and may be empty while it fills
    /// them. A player army that loses its last division disappears. The bottom-of-screen cards are
    /// Hoi4ArmyPanel (the player's armies only). Changed fires for the player's armies only: AI armies change
    /// daily and the UI shouldn't rebuild for them.
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

        /// <summary>The player's country (0: none, e.g. observing).</summary>
        public static ushort Player
        {
            get
            {
                var p = StarterKit.Initializer.Instance?.PlayerState;
                return p != null && p.HasPlayerCountry ? p.PlayerCountryId : (ushort)0;
            }
        }

        /// <summary>The country's armies (in id order).</summary>
        public List<Hoi4Army> Of(ushort owner)
        {
            var list = new List<Hoi4Army>();
            foreach (var a in armies) if (a.Owner == owner) list.Add(a);
            return list;
        }

        void Notify(Hoi4Army army)
        {
            if (army == null || army.Owner == Player) Changed?.Invoke();
        }

        /// <summary>The army's colour: a fixed hue per army number (golden-ratio spacing keeps neighbours apart).</summary>
        public static Color ColorOf(int armyId, float value = 0.5f) => Color.HSVToRGB((armyId * 0.382f) % 1f, 0.75f, value);

        public Hoi4Army ArmyOf(ushort unitId) => armyOf.TryGetValue(unitId, out var a) ? a : null;

        public Hoi4Army Get(int armyId)
        {
            foreach (var a in armies) if (a.Id == armyId) return a;
            return null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            if (Instance != null) return;
            var go = new GameObject("Hoi4Armies");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Armies>();
        }

        void Awake()
        {
            Instance = this;
            Changed += () => Hoi4UnitCounters.Instance?.MarkDirty(); // the no-orders mark follows plan and army membership
        }

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

        /// <summary>A new army of the country from these divisions (taken out of any army they were in). Null when
        /// none, unless allowEmpty (the AI makes its armies first and fills them over the days).</summary>
        public Hoi4Army Create(ushort owner, IEnumerable<ushort> units, bool allowEmpty = false)
        {
            var army = new Hoi4Army { Id = nextId++, Owner = owner };
            // numbered per country, like HOI4: the lowest number none of its armies has
            var used = new HashSet<string>();
            foreach (var a in armies) if (a.Owner == owner) used.Add(a.Name);
            int n = 1;
            while (used.Contains(Ordinal(n) + " Army")) n++;
            army.Name = Ordinal(n) + " Army";
            int added = 0;
            foreach (ushort u in units)
            {
                Detach(u);
                army.Units.Add(u);
                armyOf[u] = army;
                added++;
            }
            if (added == 0 && !allowEmpty) return null;
            armies.Add(army);
            Prune();
            Notify(army);
            return army;
        }

        /// <summary>Take divisions out of their armies (the AI's reserve: no army).</summary>
        public void Release(IEnumerable<ushort> units)
        {
            bool player = false;
            foreach (ushort u in units)
            {
                if (armyOf.TryGetValue(u, out var a) && a.Owner == Player) player = true;
                Detach(u);
            }
            Prune();
            if (player) Changed?.Invoke();
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
                Notify(army);
            }
            return added;
        }

        public void Disband(Hoi4Army army)
        {
            if (!armies.Remove(army)) return;
            foreach (ushort u in army.Units) armyOf.Remove(u);
            army.Units.Clear();
            Notify(army);
        }

        void Remove(ushort unitId)
        {
            if (!armyOf.TryGetValue(unitId, out var army)) return;
            Detach(unitId);
            Prune();
            Notify(army);
        }

        void Detach(ushort unitId)
        {
            if (!armyOf.TryGetValue(unitId, out var army)) return;
            army.Units.Remove(unitId);
            armyOf.Remove(unitId);
        }

        // An empty player army disappears; the AI keeps its armies while their theatre needs them (it disbands them)
        void Prune() => armies.RemoveAll(a => a.Units.Count == 0 && !IsAI(a));

        static bool IsAI(Hoi4Army a) => a.Owner != Player;

        static string Ordinal(int n)
        {
            int mod100 = n % 100;
            string suffix = mod100 >= 11 && mod100 <= 13 ? "th" : (n % 10) == 1 ? "st" : (n % 10) == 2 ? "nd" : (n % 10) == 3 ? "rd" : "th";
            return n + suffix;
        }
    }
}
