using System.IO;
using Core;
using Core.Commands;

namespace Hoi4.Construction
{
    /// <summary>Queue Count levels of a building in a state, at the end of the country's construction queue.</summary>
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

    /// <summary>Take an item off the construction queue; its progress is lost.</summary>
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
