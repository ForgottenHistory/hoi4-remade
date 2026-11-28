using System.IO;
using Core;
using Core.Commands;

namespace Hoi4.Production
{
    /// <summary>A new production line at the bottom of the country's queue, building the newest equipment of the
    /// archetype it has, or Equipment when set (an outdated one the country knows), asking for Factories military
    /// factories.</summary>
    public sealed class AddProductionLineCommand : BaseCommand
    {
        public ushort CountryId;
        public int Archetype;
        public int Factories;
        public string Equipment; // null: what the country builds of the archetype now

        public override bool Validate(GameState gameState) =>
            CountryId != 0 && Factories >= 0 && Hoi4Production.Instance != null
            && (Equipment != null ? Hoi4Production.Instance.CanAddLine(CountryId, Equipment)
                                  : Hoi4Production.Instance.CanAddLine(CountryId, Archetype));

        public override void Execute(GameState gameState) => Hoi4Production.Instance.AddLine(CountryId, Archetype, Factories, Equipment);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Archetype); w.Write(Factories); w.Write(Equipment ?? ""); }
        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16(); Archetype = r.ReadInt32(); Factories = r.ReadInt32();
            Equipment = r.ReadString();
            if (Equipment.Length == 0) Equipment = null;
        }
    }

    /// <summary>Mark an equipment the country knows outdated, or current again (the production screen's round
    /// button on an equipment).</summary>
    public sealed class SetEquipmentObsoleteCommand : BaseCommand
    {
        public ushort CountryId;
        public string Equipment;
        public bool Obsolete;

        public override bool Validate(GameState gameState) =>
            CountryId != 0 && Hoi4Production.Instance != null && Hoi4Production.Instance.ArchetypeOf(CountryId, Equipment) >= 0;

        public override void Execute(GameState gameState) => Hoi4Production.Instance.SetObsolete(CountryId, Equipment, Obsolete);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Equipment ?? ""); w.Write(Obsolete); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); Equipment = r.ReadString(); Obsolete = r.ReadBoolean(); }
    }

    /// <summary>How many military factories a line asks for (0 to 150). It gets them if the lines above it
    /// leave enough.</summary>
    public sealed class SetLineFactoriesCommand : BaseCommand
    {
        public ushort CountryId;
        public int LineId;
        public int Factories;

        public override bool Validate(GameState gameState) =>
            Factories >= 0 && Factories <= Hoi4Production.MaxFactoriesPerLine
            && Hoi4Production.Instance?.Line(CountryId, LineId) != null;

        public override void Execute(GameState gameState) => Hoi4Production.Instance.SetFactories(CountryId, LineId, Factories);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(LineId); w.Write(Factories); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); LineId = r.ReadInt32(); Factories = r.ReadInt32(); }
    }

    /// <summary>Delete a production line; the unit in progress is lost and its factories go free.</summary>
    public sealed class RemoveProductionLineCommand : BaseCommand
    {
        public ushort CountryId;
        public int LineId;

        public override bool Validate(GameState gameState) => Hoi4Production.Instance?.Line(CountryId, LineId) != null;
        public override void Execute(GameState gameState) => Hoi4Production.Instance.RemoveLine(CountryId, LineId);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(LineId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); LineId = r.ReadInt32(); }
    }

    /// <summary>Switch a line to the country's current equipment of an archetype; it keeps part of its efficiency
    /// (Hoi4Production.SwitchLine).</summary>
    public sealed class SwitchLineEquipmentCommand : BaseCommand
    {
        public ushort CountryId;
        public int LineId;
        public int Archetype;

        public override bool Validate(GameState gameState) =>
            Hoi4Production.Instance != null && Hoi4Production.Instance.CanSwitch(CountryId, LineId, Archetype);

        public override void Execute(GameState gameState) => Hoi4Production.Instance.SwitchLine(CountryId, LineId, Archetype);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(LineId); w.Write(Archetype); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); LineId = r.ReadInt32(); Archetype = r.ReadInt32(); }
    }

    /// <summary>Move a line in the priority queue (Position 0 = top). Higher lines get factories first.</summary>
    public sealed class MoveProductionLineCommand : BaseCommand
    {
        public ushort CountryId;
        public int LineId;
        public int Position;

        public override bool Validate(GameState gameState) =>
            Position >= 0 && Hoi4Production.Instance?.Line(CountryId, LineId) != null;

        public override void Execute(GameState gameState) => Hoi4Production.Instance.MoveLine(CountryId, LineId, Position);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(LineId); w.Write(Position); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); LineId = r.ReadInt32(); Position = r.ReadInt32(); }
    }
}
