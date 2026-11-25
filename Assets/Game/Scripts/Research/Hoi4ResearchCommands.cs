using System.IO;
using Core;
using Core.Commands;

namespace Hoi4.Research
{
    /// <summary>Put a technology in a research slot (Tech -1 empties the slot). The tech in the slot before keeps its
    /// progress; a pick in an empty slot spends the slot's saved days at once.</summary>
    public sealed class SetResearchCommand : BaseCommand
    {
        public ushort CountryId;
        public int Slot;
        public int Tech;

        public override bool Validate(GameState gameState) =>
            CountryId != 0 && Hoi4Research.Instance != null && Hoi4Research.Instance.CanSet(CountryId, Slot, Tech);

        public override void Execute(GameState gameState) => Hoi4Research.Instance.SetSlot(CountryId, Slot, Tech);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Slot); w.Write(Tech); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); Slot = r.ReadInt32(); Tech = r.ReadInt32(); }
    }
}
