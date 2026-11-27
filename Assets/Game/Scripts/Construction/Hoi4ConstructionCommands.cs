using System.IO;
using Core;
using Core.Commands;

namespace Hoi4.Construction
{
    /// <summary>Queue Count levels of a building in a state: onto the country's order for it there, else a new order at
    /// the end of the construction queue.</summary>
    public sealed class QueueConstructionCommand : BaseCommand
    {
        public ushort CountryId;
        public int State;
        public string Building;
        public int Count = 1;

        public override bool Validate(GameState gameState) =>
            Count >= 1 && Count <= Hoi4Construction.MaxQueue && Hoi4Construction.Instance != null
            && Hoi4Construction.Instance.CanQueue(CountryId, State, Building);

        public override void Execute(GameState gameState)
        {
            for (int i = 0; i < Count; i++)
                if (Hoi4Construction.Instance.Enqueue(CountryId, State, Building) == 0) break;
        }

        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(State); w.Write(Building ?? ""); w.Write(Count); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); State = r.ReadInt32(); Building = r.ReadString(); Count = r.ReadInt32(); }
    }

    /// <summary>Queue Count levels of a province building (naval base, supply hub) in a province.</summary>
    public sealed class QueueProvinceConstructionCommand : BaseCommand
    {
        public ushort CountryId;
        public ushort Province;
        public string Building;
        public int Count = 1;

        public override bool Validate(GameState gameState) =>
            Count >= 1 && Count <= Hoi4Construction.MaxQueue && Hoi4Construction.Instance != null
            && Hoi4Construction.Instance.CanQueueProvince(CountryId, Province, Building);

        public override void Execute(GameState gameState)
        {
            for (int i = 0; i < Count; i++)
                if (Hoi4Construction.Instance.EnqueueProvince(CountryId, Province, Building) == 0) break;
        }

        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Province); w.Write(Building ?? ""); w.Write(Count); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); Province = r.ReadUInt16(); Building = r.ReadString(); Count = r.ReadInt32(); }
    }

    /// <summary>Queue a railway along a path of bordering provinces the country controls (Hoi4Construction.RailTarget
    /// decides its level).</summary>
    public sealed class QueueRailwayCommand : BaseCommand
    {
        public ushort CountryId;
        public System.Collections.Generic.List<ushort> Path;

        public override bool Validate(GameState gameState) =>
            Hoi4Construction.Instance != null && Hoi4Construction.Instance.RailTarget(CountryId, Path) > 0;

        public override void Execute(GameState gameState) => Hoi4Construction.Instance.EnqueueRail(CountryId, Path);

        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId);
            w.Write((ushort)(Path?.Count ?? 0));
            if (Path != null) foreach (ushort p in Path) w.Write(p);
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16();
            int n = r.ReadUInt16();
            Path = new System.Collections.Generic.List<ushort>(n);
            for (int i = 0; i < n; i++) Path.Add(r.ReadUInt16());
        }
    }

    /// <summary>Set the level a railway order builds its links to; an order left with nothing to build is removed.</summary>
    public sealed class SetRailOrderLevelCommand : BaseCommand
    {
        public ushort CountryId;
        public int ItemId;
        public int Level;

        public override bool Validate(GameState gameState)
        {
            var item = Hoi4Construction.Instance?.Item(CountryId, ItemId);
            return item != null && item.Path != null && Level >= 0 && Level <= Hoi4.Supply.Hoi4Railways.MaxLevel && Level != item.RailLevel;
        }

        public override void Execute(GameState gameState) => Hoi4Construction.Instance.SetRailOrderLevel(CountryId, ItemId, Level);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(ItemId); w.Write(Level); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); ItemId = r.ReadInt32(); Level = r.ReadInt32(); }
    }

    /// <summary>Set how many levels a construction order builds (the +/- on the order).</summary>
    public sealed class SetConstructionCountCommand : BaseCommand
    {
        public ushort CountryId;
        public int ItemId;
        public int Count;

        public override bool Validate(GameState gameState) =>
            Count >= 1 && Count <= Hoi4Construction.MaxQueue && Hoi4Construction.Instance?.Item(CountryId, ItemId) != null;

        public override void Execute(GameState gameState) => Hoi4Construction.Instance.SetCount(CountryId, ItemId, Count);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(ItemId); w.Write(Count); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); ItemId = r.ReadInt32(); Count = r.ReadInt32(); }
    }

    /// <summary>Take an order off the construction queue; its progress is lost.</summary>
    public sealed class RemoveConstructionCommand : BaseCommand
    {
        public ushort CountryId;
        public int ItemId;

        public override bool Validate(GameState gameState) => Hoi4Construction.Instance?.Item(CountryId, ItemId) != null;
        public override void Execute(GameState gameState) => Hoi4Construction.Instance.Remove(CountryId, ItemId);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(ItemId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); ItemId = r.ReadInt32(); }
    }

    /// <summary>Move a construction item in the queue (Position 0 = top). Higher items get civilian factories first.</summary>
    public sealed class MoveConstructionCommand : BaseCommand
    {
        public ushort CountryId;
        public int ItemId;
        public int Position;

        public override bool Validate(GameState gameState) =>
            Position >= 0 && Hoi4Construction.Instance?.Item(CountryId, ItemId) != null;

        public override void Execute(GameState gameState) => Hoi4Construction.Instance.Move(CountryId, ItemId, Position);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(ItemId); w.Write(Position); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); ItemId = r.ReadInt32(); Position = r.ReadInt32(); }
    }
}
