using System.Collections.Generic;
using Core;
using Hoi4.Map;
using Hoi4.Units;

namespace Hoi4.Construction
{
    /// <summary>
    /// GAME: the state buildings as they are now: levels per state and building, starting from the 1936 history
    /// (Hoi4StateData) and raised by construction. Also who a state's buildings work for: its owner (the country owning
    /// most of its provinces) while that owner controls more than half of them (HOI4: the state's controller).
    /// Production counts military factories from here, construction civilian factories and free slots.
    /// Changes only through Add, from the deterministic construction tick; Version counts them for caches.
    /// </summary>
    public static class Hoi4Buildings
    {
        public const int MaxSharedSlots = 25; // NDefines.NBuildings.MAX_SHARED_SLOTS

        static readonly Dictionary<int, Dictionary<string, int>> levels = new Dictionary<int, Dictionary<string, int>>();
        static readonly Dictionary<int, int> addedSlots = new Dictionary<int, int>();   // shared slots added in play (focus rewards)
        static bool loaded;

        // state -> country its buildings work for (0: none), refreshed when ownership or control changes
        static readonly Dictionary<int, ushort> user = new Dictionary<int, ushort>();
        static GameState userState;
        static int userOwnership = -1, userControl = -1;

        /// <summary>Raised by every change (build finished), for caches.</summary>
        public static int Version { get; private set; }

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            levels.Clear(); addedSlots.Clear(); loaded = false; Version = 0;
            user.Clear(); userState = null; userOwnership = userControl = -1;
        }

        public static bool Available => EnsureLoaded();

        static bool EnsureLoaded()
        {
            if (loaded) return true;
            if (!Hoi4StateData.Available || !Hoi4Regions.Available) return false;
            foreach (int state in Hoi4Regions.States)
            {
                var info = Hoi4StateData.Get(state);
                levels[state] = info != null ? new Dictionary<string, int>(info.Buildings) : new Dictionary<string, int>();
            }
            loaded = true;
            return true;
        }

        public static int Level(int state, string building) =>
            EnsureLoaded() && levels.TryGetValue(state, out var b) && b.TryGetValue(building, out int n) ? n : 0;

        /// <summary>The state's building levels (read only).</summary>
        public static IReadOnlyDictionary<string, int> Of(int state) =>
            EnsureLoaded() && levels.TryGetValue(state, out var b) ? b : (IReadOnlyDictionary<string, int>)new Dictionary<string, int>();

        /// <summary>Add levels (a construction finished).</summary>
        public static void Add(int state, string building, int n)
        {
            if (!EnsureLoaded()) return;
            if (!levels.TryGetValue(state, out var b)) levels[state] = b = new Dictionary<string, int>();
            b.TryGetValue(building, out int have);
            b[building] = have + n;
            Version++;
        }

        /// <summary>Shared slots the state has (its category's plus extra ones from its history and those added in
        /// play, at most 25) and those its shared buildings use.</summary>
        public static int SharedSlots(int state)
        {
            var info = Hoi4StateData.Get(state);
            if (info == null) return 0;
            addedSlots.TryGetValue(state, out int added);
            return System.Math.Min(MaxSharedSlots, Hoi4StateData.Slots(info.Category) + info.ExtraSlots + added);
        }

        /// <summary>More shared slots for the state (add_extra_state_shared_building_slots).</summary>
        public static void AddSlots(int state, int n)
        {
            if (n == 0 || !EnsureLoaded() || Hoi4StateData.Get(state) == null) return;
            addedSlots.TryGetValue(state, out int added);
            addedSlots[state] = added + n;
            Version++;
        }

        public static int SharedUsed(int state)
        {
            int used = 0;
            foreach (var kv in Of(state))
            {
                var def = Hoi4StateData.Def(kv.Key);
                if (def != null && def.Shared) used += kv.Value;
            }
            return used;
        }

        /// <summary>The country the state's buildings work for (0 when none: unowned, or its owner doesn't control
        /// more than half of it).</summary>
        public static ushort UserOf(GameState gameState, int state)
        {
            Refresh(gameState);
            return user.TryGetValue(state, out ushort c) ? c : (ushort)0;
        }

        /// <summary>Every state with a user, in state id order.</summary>
        public static IEnumerable<KeyValuePair<int, ushort>> Users(GameState gameState)
        {
            Refresh(gameState);
            foreach (int state in Hoi4Regions.States)
                if (user.TryGetValue(state, out ushort c) && c != 0) yield return new KeyValuePair<int, ushort>(state, c);
        }

        /// <summary>Ownership or control version this lookup was made at, for callers caching on top of it.</summary>
        public static int UsersVersion(GameState gameState)
        {
            Refresh(gameState);
            return userOwnership * 31 + userControl;
        }

        static void Refresh(GameState gameState)
        {
            if (!EnsureLoaded()) return;
            var provinces = gameState.Provinces;
            if (userState == gameState && userOwnership == provinces.OwnershipVersion && userControl == provinces.ControlVersion) return;
            userState = gameState;
            userOwnership = provinces.OwnershipVersion;
            userControl = provinces.ControlVersion;
            user.Clear();
            Hoi4Occupation.Snapshot(gameState, 0, out var ctrl, out var owner);
            var tally = new Dictionary<ushort, int>();
            foreach (int state in Hoi4Regions.States)
            {
                var provs = Hoi4Regions.ProvincesOf(state);
                if (provs.Count == 0) continue;
                tally.Clear();
                foreach (ushort p in provs)
                {
                    if (p >= owner.Length || owner[p] == 0) continue;
                    tally.TryGetValue(owner[p], out int n);
                    tally[owner[p]] = n + 1;
                }
                ushort stateOwner = 0;
                int best = 0;
                foreach (var kv in tally)
                    if (kv.Value > best || (kv.Value == best && kv.Key < stateOwner)) { stateOwner = kv.Key; best = kv.Value; }
                if (stateOwner == 0) continue;
                int controlled = 0;
                foreach (ushort p in provs) if (p < ctrl.Length && ctrl[p] == stateOwner) controlled++;
                if (controlled * 2 > provs.Count) user[state] = stateOwner;
            }
        }
    }
}
