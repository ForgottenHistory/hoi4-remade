using System.Collections.Generic;
using Core;
using Hoi4.Map;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: divisions on someone else's land go home. HOI4 moves units that lose the right to stand somewhere
    /// (a war ends, access ends) to the nearest friendly territory. Without this armies were stranded: their march
    /// home needs a path through land they may not enter (Japan's garrison in Manchukuo, which stood there from
    /// the start without access since subjects aren't modelled; Italy's in Germany). The peace conference's
    /// SendHome covers land that changed hands in a peace; this covers the rest, every day.
    ///
    /// Daily, from Hoi4Simulation, for every country's divisions (the player's too): a division standing still or
    /// marching in land controlled by another country it isn't at war with, whose owner gives it no access
    /// (Hoi4MovementRules.HasAccess: allied, at war or military access), not at sea, is placed in the nearest
    /// province its country controls, counted in land hops through any passable land (ties: the lower
    /// province id). Its march is cancelled. A country with no land left leaves its divisions where they are.
    /// Deterministic: units and rings in id order. Noted (system "return_home").
    /// </summary>
    public static class Hoi4ReturnHome
    {
        const int MaxHops = 200;

        static int[] off;
        static ushort[] nbr;
        static Hoi4MovementRules rules;
        static GameState rulesState;

        [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { off = null; nbr = null; rules = null; rulesState = null; }

        public static void SimulateDay(GameState gameState)
        {
            if (gameState?.Units == null || gameState.Adjacencies == null || gameState.Diplomacy == null) return;
            if (off == null) gameState.Adjacencies.GetCompactGraph(out off, out nbr);
            var units = gameState.Units;
            var diplomacy = gameState.Diplomacy;
            if (rules == null || rulesState != gameState) { rules = new Hoi4MovementRules(gameState); rulesState = gameState; }
            Hoi4Occupation.Snapshot(gameState, off.Length - 1, out var ctrl, out var owner);
            var all = units.GetAllUnits();
            all.Sort();
            var moved = new Dictionary<ushort, List<string>>(); // country -> moves
            foreach (ushort u in all)
            {
                if (!units.HasUnit(u)) continue;
                var unit = units.GetUnit(u);
                ushort country = unit.countryID, p = unit.provinceID;
                if (country == 0 || p >= ctrl.Length || Hoi4Regions.IsWater(p)) continue;
                ushort holder = ctrl[p];
                if (holder == 0 || holder == country || diplomacy.IsAtWar(country, holder)) continue;
                if (p < owner.Length && rules.HasAccess(country, owner[p])) continue; // allowed there (ally, access)
                ushort home = NearestOwn(p, country, ctrl);
                if (home == 0) continue;
                if (units.MovementQueue.IsUnitMoving(u)) units.MovementQueue.CancelMovement(u);
                units.MoveUnit(u, home);
                if (!moved.TryGetValue(country, out var l)) moved[country] = l = new List<string>();
                l.Add($"{u} {p}->{home}");
            }
            foreach (var kv in moved)
                Core.Recording.SessionRecorder.Note("return_home", "moved", kv.Key, 0, 0, 0,
                    $"{kv.Value.Count} divisions on the land of a country not at war with them sent home: {string.Join(", ", kv.Value)}");
        }

        static ushort NearestOwn(ushort start, ushort country, ushort[] ctrl)
        {
            var seen = new HashSet<ushort> { start };
            var ring = new List<ushort> { start };
            for (int hop = 0; hop < MaxHops && ring.Count > 0; hop++)
            {
                var next = new List<ushort>();
                foreach (ushort p in ring)
                    for (int i = off[p]; i < off[p + 1]; i++)
                    {
                        ushort n = nbr[i];
                        if (Hoi4Regions.IsWater(n) || Hoi4Regions.IsImpassable(n) || !seen.Add(n)) continue;
                        next.Add(n);
                    }
                next.Sort();
                foreach (ushort n in next) if (n < ctrl.Length && ctrl[n] == country) return n;
                ring = next;
            }
            return 0;
        }
    }
}
