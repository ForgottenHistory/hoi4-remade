using System.Collections.Generic;
using System.IO;
using Core;
using Core.Commands;

namespace Hoi4.War
{
    /// <summary>The player's demands at the peace conference (state, taker); confirming ends the conference
    /// (Hoi4War.ConcludeConference): the AI winners make theirs and the deal is carried out.</summary>
    public sealed class PeaceDealCommand : BaseCommand
    {
        public ushort CountryId;
        public ushort Loser;
        public List<int> States = new List<int>();

        public override bool Validate(GameState gameState)
        {
            var c = Hoi4War.Instance?.Conference;
            return c != null && c.Loser == Loser && c.WinnerOf(CountryId) != null;
        }

        public override void Execute(GameState gameState)
        {
            var demands = new List<(int, ushort)>();
            foreach (int s in States) demands.Add((s, CountryId));
            Hoi4War.Instance.ConcludeConference(Loser, demands);
        }

        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId);
            w.Write(Loser);
            w.Write((ushort)States.Count);
            foreach (int s in States) w.Write(s);
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16();
            Loser = r.ReadUInt16();
            int n = r.ReadUInt16();
            States = new List<int>(n);
            for (int i = 0; i < n; i++) States.Add(r.ReadInt32());
        }
    }
}
