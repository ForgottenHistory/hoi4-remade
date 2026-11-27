using System.IO;
using Core;
using Core.Commands;

namespace Hoi4.Focus
{
    /// <summary>Start a national focus (Focus: its index in Hoi4Focus.Focuses). The focus's scripts run here, inside
    /// the command: on_select, or on_bypass when the focus is bypassed.</summary>
    public sealed class SelectFocusCommand : BaseCommand
    {
        public ushort CountryId;
        public int Focus;

        public override bool Validate(GameState gameState) =>
            CountryId != 0 && Hoi4Focus.Instance != null && Hoi4Focus.Instance.CanSelect(CountryId, Focus);

        public override void Execute(GameState gameState) => Hoi4Focus.Instance.Select(CountryId, Focus);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Focus); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); Focus = r.ReadInt32(); }
    }
}
