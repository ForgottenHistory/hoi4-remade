using System.Collections.Generic;
using Hoi4.Production;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: which equipment versions make up an amount of one archetype (a division's Have[a], a country's
    /// stockpile), as shares of it, newest first (Hoi4EquipmentCatalog.IsNewer). The amount itself lives elsewhere and
    /// stays authoritative: losses, attrition and anything else that scales it leave the shares alone (HOI4 loses
    /// every version alike), only additions and withdrawals of known versions move them. Empty: no version known (the
    /// division's start version or the archetype's 1936 stats count, Hoi4Divisions.RefreshStats).
    /// </summary>
    public sealed class Hoi4EquipmentMix
    {
        struct Item { public Hoi4EquipmentCatalog.Entry Entry; public FP Share; }

        static readonly FP Dust = FP.FromFraction(1, 100000); // a share below it is gone (fixed-point remainders)

        readonly List<Item> items = new List<Item>(2);
        static FP[] scratch = new FP[8];

        public int Count => items.Count;
        public Hoi4EquipmentCatalog.Entry Entry(int i) => items[i].Entry;
        public FP Share(int i) => items[i].Share;
        public Hoi4EquipmentCatalog.Entry Newest => items.Count > 0 ? items[0].Entry : null;
        public Hoi4EquipmentCatalog.Entry Oldest => items.Count > 0 ? items[items.Count - 1].Entry : null;

        public void Clear() => items.Clear();

        /// <summary>Only this version.</summary>
        public void Set(Hoi4EquipmentCatalog.Entry entry)
        {
            items.Clear();
            if (entry != null) items.Add(new Item { Entry = entry, Share = FP.One });
        }

        /// <summary>The share of one version (0 when it isn't held).</summary>
        public FP ShareOf(Hoi4EquipmentCatalog.Entry entry)
        {
            foreach (var it in items) if (it.Entry == entry) return it.Share;
            return FP.Zero;
        }

        /// <summary>AMOUNT of ENTRY joins an amount of TOTAL (before the addition). An unknown version (null) dilutes
        /// nothing: it counts as the versions already there. Returns true when the shares changed.</summary>
        public bool Add(Hoi4EquipmentCatalog.Entry entry, FP amount, FP total)
        {
            if (entry == null || amount <= FP.Zero) return false;
            if (total <= FP.Zero || items.Count == 0)
            {
                bool same = items.Count == 1 && items[0].Entry == entry;
                Set(entry);
                return !same;
            }
            if (items.Count == 1 && items[0].Entry == entry) return false;
            FP after = total + amount;
            FP keep = total / after;
            int at = -1;
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                it.Share *= keep;
                if (it.Entry == entry) { it.Share += amount / after; at = i; }
                items[i] = it;
            }
            if (at < 0)
            {
                int pos = 0;
                while (pos < items.Count && Before(items[pos].Entry, entry)) pos++;
                items.Insert(pos, new Item { Entry = entry, Share = amount / after });
            }
            Normalise();
            return true;
        }

        /// <summary>Take AMOUNT out of an amount of TOTAL (before the withdrawal), newest versions first (or oldest
        /// first), adding what came of each version to INTO. With no versions known, nothing is added (the amount is
        /// of unknown versions). Returns true when the shares changed.</summary>
        public bool Take(FP amount, FP total, bool newestFirst, List<(Hoi4EquipmentCatalog.Entry entry, FP amount)> into)
        {
            if (amount <= FP.Zero || total <= FP.Zero || items.Count == 0) return false;
            amount = FP.Min(amount, total);
            FP left = amount;
            if (scratch.Length < items.Count) scratch = new FP[items.Count * 2];
            var amounts = scratch; // the simulation runs on one thread
            for (int i = 0; i < items.Count; i++) amounts[i] = items[i].Share * total;
            for (int k = 0; k < items.Count && left > FP.Zero; k++)
            {
                int i = newestFirst ? k : items.Count - 1 - k;
                FP t = FP.Min(left, amounts[i]);
                if (k == items.Count - 1) t = left; // the last version takes the fixed-point remainder
                if (t <= FP.Zero) continue;
                amounts[i] -= t;
                left -= t;
                into?.Add((items[i].Entry, t));
            }
            if (items.Count == 1) return false;
            FP after = total - amount;
            if (after <= FP.Zero) return false; // all gone: the shares no longer matter, the next addition resets them
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                it.Share = FP.Max(FP.Zero, amounts[i]) / after;
                items[i] = it;
            }
            Normalise();
            return true;
        }

        /// <summary>The amount of the versions older than ENTRY in an amount of TOTAL.</summary>
        public FP OlderThan(Hoi4EquipmentCatalog.Entry entry, FP total)
        {
            FP s = FP.Zero;
            foreach (var it in items) if (Before(entry, it.Entry)) s += it.Share;
            return s * total;
        }

        /// <summary>Newest first (Hoi4EquipmentCatalog.Compare: year, a later design, cost, name).</summary>
        public static bool Before(Hoi4EquipmentCatalog.Entry a, Hoi4EquipmentCatalog.Entry b)
        {
            if (a == b || a == null) return false;
            if (b == null) return true;
            return Hoi4EquipmentCatalog.Compare(a, b) > 0;
        }

        /// <summary>Drop dust shares and give the newest the remainder, so the shares add up to exactly 1.</summary>
        void Normalise()
        {
            for (int i = items.Count - 1; i >= 0 && items.Count > 1; i--)
                if (items[i].Share < Dust) items.RemoveAt(i);
            FP rest = FP.One;
            for (int i = 1; i < items.Count; i++) rest -= items[i].Share;
            var first = items[0];
            first.Share = rest;
            items[0] = first;
        }
    }
}
