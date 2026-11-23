using System.Collections.Generic;
using System.IO;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: a division template's layout as the division designer edits it: its name, battalions at their brigade grid
    /// slot (column X 0..4, row Y 0..4), support companies at their row, and reinforcement priority.
    /// HOI4 rules (defines): MAX_DIVISION_BRIGADE_WIDTH 5 columns, MAX_DIVISION_BRIGADE_HEIGHT 5 rows of which
    /// MIN_DIVISION_BRIGADE_HEIGHT 4 are open (a doctrine opens the fifth), MAX_DIVISION_SUPPORT_HEIGHT 5 support
    /// companies, one of each type; a brigade (column) holds one sub-unit group (infantry, mobile, armor...).
    /// Regimental support: one company under a brigade (MAX_REGIMENTAL_SUPPORT_WIDTH 5, _HEIGHT 1) once the brigade has
    /// REGIMENTAL_SUPPORT_REQUIRED_BATTALIONS 3 battalions, of a type that allows the brigade's group.
    /// </summary>
    public sealed class Hoi4TemplateDesign
    {
        public const int Columns = 5, Rows = 5, OpenRows = 4, SupportRows = 5, RegimentalBattalions = 3;

        public struct Slot
        {
            public string Type;
            public int X, Y;
            public Slot(string type, int x, int y) { Type = type; X = x; Y = y; }
        }

        public string Name = "";
        public readonly List<Slot> Regiments = new List<Slot>();
        public readonly List<Slot> Support = new List<Slot>(); // X is 0
        public readonly List<Slot> Regimental = new List<Slot>(); // Y is 0, X its brigade
        public int Priority = Hoi4Divisions.Normal;

        public Hoi4TemplateDesign Clone()
        {
            var c = new Hoi4TemplateDesign { Name = Name, Priority = Priority };
            c.Regiments.AddRange(Regiments);
            c.Support.AddRange(Support);
            c.Regimental.AddRange(Regimental);
            return c;
        }

        public string RegimentAt(int x, int y)
        {
            foreach (var s in Regiments) if (s.X == x && s.Y == y) return s.Type;
            return null;
        }

        public string SupportAt(int y)
        {
            foreach (var s in Support) if (s.Y == y) return s.Type;
            return null;
        }

        public string RegimentalAt(int x)
        {
            foreach (var s in Regimental) if (s.X == x) return s.Type;
            return null;
        }

        /// <summary>Put a regimental support company under brigade X (null removes it).</summary>
        public void SetRegimental(int x, string type)
        {
            Regimental.RemoveAll(s => s.X == x);
            if (type != null) Regimental.Add(new Slot(type, x, 0));
            Regimental.Sort((a, b) => a.X.CompareTo(b.X));
        }

        public int ColumnCount(int x)
        {
            int n = 0;
            foreach (var s in Regiments) if (s.X == x) n++;
            return n;
        }

        /// <summary>Put a battalion in a slot (null removes it); then close the gaps as HOI4 does: rows move up
        /// within a column, columns move left.</summary>
        public void SetRegiment(int x, int y, string type)
        {
            Regiments.RemoveAll(s => s.X == x && s.Y == y);
            if (type != null) Regiments.Add(new Slot(type, x, y));
            Compact();
        }

        public void SetSupport(int y, string type)
        {
            Support.RemoveAll(s => s.Y == y);
            if (type != null) Support.Add(new Slot(type, 0, y));
            Support.Sort((a, b) => a.Y.CompareTo(b.Y));
            for (int i = 0; i < Support.Count; i++) Support[i] = new Slot(Support[i].Type, 0, i);
        }

        void Compact()
        {
            Regiments.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            var moved = new Dictionary<int, int>(); // old column -> new
            int column = -1, lastX = int.MinValue, row = 0;
            for (int i = 0; i < Regiments.Count; i++)
            {
                var s = Regiments[i];
                if (s.X != lastX) { column++; lastX = s.X; row = 0; moved[s.X] = column; }
                Regiments[i] = new Slot(s.Type, column, row++);
            }
            for (int i = Regimental.Count - 1; i >= 0; i--)
            {
                var s = Regimental[i];
                if (moved.TryGetValue(s.X, out int x) && ColumnCount(x) >= RegimentalBattalions) Regimental[i] = new Slot(s.Type, x, 0);
                else Regimental.RemoveAt(i);
            }
            Regimental.Sort((a, b) => a.X.CompareTo(b.X));
        }

        /// <summary>Sub-unit names in template order: battalions by column then row, then the support companies.</summary>
        public List<string> RegimentTypes()
        {
            var list = new List<Slot>(Regiments);
            list.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            return list.ConvertAll(s => s.Type);
        }

        public List<string> SupportTypes()
        {
            var list = new List<Slot>(Support);
            list.Sort((a, b) => a.Y.CompareTo(b.Y));
            return list.ConvertAll(s => s.Type);
        }

        public List<string> RegimentalTypes()
        {
            var list = new List<Slot>(Regimental);
            list.Sort((a, b) => a.X.CompareTo(b.X));
            return list.ConvertAll(s => s.Type);
        }

        public void Write(BinaryWriter w)
        {
            w.Write(Name ?? "");
            w.Write(Priority);
            w.Write((byte)Regiments.Count);
            foreach (var s in Regiments) { w.Write(s.Type ?? ""); w.Write((byte)s.X); w.Write((byte)s.Y); }
            w.Write((byte)Support.Count);
            foreach (var s in Support) { w.Write(s.Type ?? ""); w.Write((byte)s.Y); }
            w.Write((byte)Regimental.Count);
            foreach (var s in Regimental) { w.Write(s.Type ?? ""); w.Write((byte)s.X); }
        }

        public static Hoi4TemplateDesign Read(BinaryReader r)
        {
            var d = new Hoi4TemplateDesign { Name = r.ReadString(), Priority = r.ReadInt32() };
            int n = r.ReadByte();
            for (int i = 0; i < n; i++) d.Regiments.Add(new Slot(r.ReadString(), r.ReadByte(), r.ReadByte()));
            n = r.ReadByte();
            for (int i = 0; i < n; i++) d.Support.Add(new Slot(r.ReadString(), 0, r.ReadByte()));
            n = r.ReadByte();
            for (int i = 0; i < n; i++) d.Regimental.Add(new Slot(r.ReadString(), r.ReadByte(), 0));
            return d;
        }
    }

    /// <summary>
    /// GAME: what a template adds up to (HOI4 division designer sums): HP, suppression, weight, supply use, attacks,
    /// defence, breakthrough, entrenchment, recon, manpower and equipment add up over every sub-unit; organisation,
    /// recovery, reliability, initiative and hardness are the mean over every sub-unit (support companies included);
    /// speed is the slowest of those that affect it (battalions and divisional companies without affects_speed = no;
    /// never regimental support); combat width counts battalions only; armour and piercing are 30% / 40% the best plus
    /// 70% / 60% the mean. Checked against HOI4's designer (FRA Division Coloniale).
    /// </summary>
    /// <summary>A division's or sub-unit's modifiers in one terrain (HOI4 `forest = { attack defence movement }`).</summary>
    public struct Hoi4TerrainModifier
    {
        public FP Attack, Defence, Movement;
    }

    public struct Hoi4TemplateStats
    {
        public FP MaxStrength, MaxOrganisation, Recovery, Recon, Suppression, Weight, SupplyUse, Reliability;
        public FP SoftAttack, HardAttack, AirAttack, Defense, Breakthrough, Armor, Piercing, Initiative, Entrenchment;
        public FP CombatWidth, Hardness, SpeedKmh;
        public Dictionary<string, Hoi4TerrainModifier> Terrain; // per terrain (and river, amphibious): see Hoi4Divisions.Sum
        public int Manpower, TrainingDays, Count;
        public FP[] Need;                         // per archetype (Hoi4Divisions.Archetypes)
        public Hoi4Division.Part[] Parts;         // per sub-unit: its strength share and archetypes
    }
}
