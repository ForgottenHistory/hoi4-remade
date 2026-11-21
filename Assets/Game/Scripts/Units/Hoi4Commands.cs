using System.Collections.Generic;
using System.IO;
using Core;
using Core.Commands;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: the HOI4 commands. Every change a player or the AI decides goes through one of these
    /// (gameState.TryExecuteCommand), so it is validated, logged and can be sent over the network or
    /// replayed. What the simulation does by itself on the hourly tick (combat, occupation, plans
    /// keeping their divisions in place) is not a command: it follows deterministically from these.
    /// </summary>
    static class Hoi4CommandIo
    {
        public static void WriteIds(BinaryWriter w, List<ushort> ids)
        {
            w.Write((ushort)(ids?.Count ?? 0));
            if (ids != null) foreach (ushort id in ids) w.Write(id);
        }

        public static List<ushort> ReadIds(BinaryReader r)
        {
            int n = r.ReadUInt16();
            var ids = new List<ushort>(n);
            for (int i = 0; i < n; i++) ids.Add(r.ReadUInt16());
            return ids;
        }

        /// <summary>All the divisions exist and belong to the country.</summary>
        public static bool Owned(GameState gameState, ushort country, List<ushort> ids)
        {
            if (country == 0 || ids == null || ids.Count == 0) return false;
            var units = gameState.Units;
            foreach (ushort id in ids)
                if (!units.HasUnit(id) || units.GetUnit(id).countryID != country) return false;
            return true;
        }
    }

    /// <summary>Stop divisions where they stand. With ReleasePlan (the player's H key or a manual order)
    /// they also leave their battle plan, so the plan doesn't send them back.</summary>
    public sealed class HaltUnitsCommand : BaseCommand
    {
        public ushort CountryId;
        public List<ushort> UnitIds;
        public bool ReleasePlan;

        public override bool Validate(GameState gameState) => Hoi4CommandIo.Owned(gameState, CountryId, UnitIds);

        public override void Execute(GameState gameState)
        {
            var queue = gameState.Units.MovementQueue;
            foreach (ushort id in UnitIds)
            {
                if (ReleasePlan) Hoi4BattlePlans.Instance?.Unassign(id);
                queue.CancelMovement(id);
            }
            Hoi4UnitCounters.Instance?.MarkDirty();
        }

        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId);
            w.Write(ReleasePlan);
            Hoi4CommandIo.WriteIds(w, UnitIds);
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16();
            ReleasePlan = r.ReadBoolean();
            UnitIds = Hoi4CommandIo.ReadIds(r);
        }
    }

    /// <summary>Take divisions off their battle plan without stopping them (sent before a manual move order).</summary>
    public sealed class ReleaseFromPlanCommand : BaseCommand
    {
        public ushort CountryId;
        public List<ushort> UnitIds;

        public override bool Validate(GameState gameState) =>
            Hoi4BattlePlans.Instance != null && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds);

        public override void Execute(GameState gameState)
        {
            foreach (ushort id in UnitIds) Hoi4BattlePlans.Instance.Unassign(id);
        }

        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); Hoi4CommandIo.WriteIds(w, UnitIds); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); UnitIds = Hoi4CommandIo.ReadIds(r); }
    }

    /// <summary>The AI takes a division off its front line to attack (it rejoins when the attack is over).</summary>
    public sealed class BorrowForAttackCommand : BaseCommand
    {
        public ushort CountryId;
        public ushort UnitId;

        public override bool Validate(GameState gameState) =>
            Hoi4BattlePlans.Instance != null && Hoi4CommandIo.Owned(gameState, CountryId, new List<ushort> { UnitId });

        public override void Execute(GameState gameState) => Hoi4BattlePlans.Instance.Borrow(UnitId);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(UnitId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); UnitId = r.ReadUInt16(); }
    }

    /// <summary>A new army from the country's divisions (taken out of any army they were in).</summary>
    public sealed class CreateArmyCommand : BaseCommand
    {
        public ushort CountryId;
        public List<ushort> UnitIds;

        public override bool Validate(GameState gameState) =>
            Hoi4Armies.Instance != null && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds);

        public override void Execute(GameState gameState) => Hoi4Armies.Instance.Create(UnitIds);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); Hoi4CommandIo.WriteIds(w, UnitIds); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); UnitIds = Hoi4CommandIo.ReadIds(r); }
    }

    /// <summary>Move divisions into an existing army.</summary>
    public sealed class AddToArmyCommand : BaseCommand
    {
        public ushort CountryId;
        public int ArmyId;
        public List<ushort> UnitIds;

        public override bool Validate(GameState gameState) =>
            Hoi4Armies.Instance?.Get(ArmyId) != null && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds);

        public override void Execute(GameState gameState) => Hoi4Armies.Instance.Add(Hoi4Armies.Instance.Get(ArmyId), UnitIds);
        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId);
            w.Write(ArmyId);
            Hoi4CommandIo.WriteIds(w, UnitIds);
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16();
            ArmyId = r.ReadInt32();
            UnitIds = Hoi4CommandIo.ReadIds(r);
        }
    }

    public sealed class DisbandArmyCommand : BaseCommand
    {
        public ushort CountryId;
        public int ArmyId;

        public override bool Validate(GameState gameState)
        {
            var army = Hoi4Armies.Instance?.Get(ArmyId);
            return army != null && army.Units.Count > 0 && Hoi4CommandIo.Owned(gameState, CountryId, new List<ushort> { army.Units[0] });
        }

        public override void Execute(GameState gameState) => Hoi4Armies.Instance.Disband(Hoi4Armies.Instance.Get(ArmyId));
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(ArmyId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); ArmyId = r.ReadInt32(); }
    }

    /// <summary>The player's front line along the border with the target country, held by these divisions
    /// of the army (those already on another plan stay there).</summary>
    public sealed class PlaceBattlePlanCommand : BaseCommand
    {
        public ushort CountryId;
        public ushort TargetCountry;
        public int ArmyId;
        public List<ushort> UnitIds;

        public override bool Validate(GameState gameState) =>
            Hoi4BattlePlans.Instance != null && Hoi4Armies.Instance?.Get(ArmyId) != null
            && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds)
            && Hoi4BattlePlans.Instance.HasBorder(CountryId, TargetCountry);

        public override void Execute(GameState gameState) =>
            Hoi4BattlePlans.Instance.CreatePlan(CountryId, ArmyId, TargetCountry, UnitIds);

        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId);
            w.Write(TargetCountry);
            w.Write(ArmyId);
            Hoi4CommandIo.WriteIds(w, UnitIds);
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16();
            TargetCountry = r.ReadUInt16();
            ArmyId = r.ReadInt32();
            UnitIds = Hoi4CommandIo.ReadIds(r);
        }
    }

    public sealed class DeleteBattlePlanCommand : BaseCommand
    {
        public ushort CountryId;
        public int PlanId;

        public override bool Validate(GameState gameState)
        {
            var plan = Hoi4BattlePlans.Instance?.GetPlan(PlanId);
            return plan != null && !plan.IsAI && plan.Owner == CountryId;
        }

        public override void Execute(GameState gameState) => Hoi4BattlePlans.Instance.DeletePlan(PlanId);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(PlanId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); PlanId = r.ReadInt32(); }
    }
}
