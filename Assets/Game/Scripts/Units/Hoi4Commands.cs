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
                if (gameState.Units.HasUnit(id) && Hoi4.Map.Hoi4Regions.IsWater(gameState.Units.GetUnit(id).provinceID))
                    SailToNearestPort(gameState, id); // a division can't wait at sea
            }
            Hoi4UnitCounters.Instance?.MarkDirty();
        }

        /// <summary>Daily, from Hoi4Simulation: every division standing still in a sea province sails to the nearest
        /// port its side holds. A division can't wait at sea, and its order can end there without a halt command (an
        /// army released it, its plan went, the war ended). Units in id order.</summary>
        public static void RescueStranded(GameState gameState)
        {
            if (gameState?.Units == null || !Hoi4.Supply.Hoi4Ports.Available) return;
            var ids = new List<ushort>(gameState.Units.GetAllUnits());
            ids.Sort();
            foreach (ushort id in ids)
                if (gameState.Units.HasUnit(id) && Hoi4.Map.Hoi4Regions.IsWater(gameState.Units.GetUnit(id).provinceID)
                    && !gameState.Units.MovementQueue.IsUnitMoving(id))
                    SailToNearestPort(gameState, id);
        }

        /// <summary>A division halted at sea (in a naval transfer) makes for the nearest port its side holds: the
        /// one with the shortest way by sea from where it is (Hoi4Ports.SailingDistanceFrom), ties to the lower
        /// province. With no such port on these waters it stays where it is.</summary>
        static void SailToNearestPort(GameState gameState, ushort id)
        {
            var unit = gameState.Units.GetUnit(id);
            var rules = new Hoi4MovementRules(gameState);
            ushort best = 0;
            long bestDist = 0;
            foreach (ushort port in Hoi4.Supply.Hoi4Ports.Ports) // sorted
            {
                if (!rules.CanUsePort(unit.countryID, port)) continue;
                long d = Hoi4.Supply.Hoi4Ports.SailingDistanceFrom(gameState, unit.provinceID, port);
                if (d < 0 || (best != 0 && d >= bestDist)) continue;
                best = port; bestDist = d;
            }
            if (best == 0 || gameState.Pathfinding == null) return;
            var path = gameState.Pathfinding.FindPath(unit.provinceID, best, unit.countryID);
            if (path == null || path.Count < 2) return;
            gameState.Units.MovementQueue.StartMovement(id, path[1], 1, path);
            Core.Recording.SessionRecorder.Note("naval", "halt_to_port", unit.countryID, id, unit.provinceID, best, "stopped at sea: sails to the nearest port");
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

        public override void Execute(GameState gameState) => Hoi4Armies.Instance.Create(CountryId, UnitIds);
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
            Hoi4Armies.Instance?.Get(ArmyId)?.Owner == CountryId && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds);

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

    /// <summary>Disband divisions (HOI4's delete button in the selected-units list): their equipment goes back to
    /// the stockpile, their men to the manpower pool.</summary>
    public sealed class DisbandDivisionsCommand : BaseCommand
    {
        public ushort CountryId;
        public List<ushort> UnitIds;

        public override bool Validate(GameState gameState) =>
            Hoi4Divisions.Instance != null && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds);

        public override void Execute(GameState gameState)
        {
            var divisions = Hoi4Divisions.Instance;
            foreach (ushort id in UnitIds)
            {
                if (!gameState.Units.HasUnit(id)) continue;
                var d = divisions.Get(id);
                if (d != null) Hoi4.Deployment.Hoi4Deployment.Refund(gameState, CountryId, d);
                Core.Recording.SessionRecorder.Note("units", "disbanded", CountryId, id, gameState.Units.GetUnit(id).provinceID, 0, d?.Name ?? "");
                divisions.Remove(id);
                gameState.Units.DisbandUnit(id, Core.Units.DestructionReason.Disbanded);
            }
        }

        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); Hoi4CommandIo.WriteIds(w, UnitIds); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); UnitIds = Hoi4CommandIo.ReadIds(r); }
    }

    public sealed class DisbandArmyCommand : BaseCommand
    {
        public ushort CountryId;
        public int ArmyId;

        public override bool Validate(GameState gameState)
        {
            var army = Hoi4Armies.Instance?.Get(ArmyId);
            return army != null && army.Owner == CountryId && army.Units.Count > 0 && Hoi4CommandIo.Owned(gameState, CountryId, new List<ushort> { army.Units[0] });
        }

        public override void Execute(GameState gameState) => Hoi4Armies.Instance.Disband(Hoi4Armies.Instance.Get(ArmyId));
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(ArmyId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); ArmyId = r.ReadInt32(); }
    }

    /// <summary>The player's front line along the stretch of border with the target country nearest Province, held by these divisions
    /// of the army (those already on another plan stay there).</summary>
    public sealed class PlaceBattlePlanCommand : BaseCommand
    {
        public ushort CountryId;
        public ushort TargetCountry;
        public ushort Province;      // where it was drawn: the line holds the stretch of border nearest it
        public ushort End;           // dragged: the line covers the stretch only from Province to End (0: all of it)
        public int ArmyId;
        public List<ushort> UnitIds;

        public override bool Validate(GameState gameState) =>
            Hoi4BattlePlans.Instance != null && Hoi4Armies.Instance?.Get(ArmyId)?.Owner == CountryId
            && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds)
            && Hoi4BattlePlans.Instance.HasBorder(CountryId, TargetCountry);

        public override void Execute(GameState gameState) =>
            Hoi4BattlePlans.Instance.CreatePlan(CountryId, ArmyId, TargetCountry, UnitIds, Province, End);

        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId);
            w.Write(TargetCountry);
            w.Write(Province);
            w.Write(End);
            w.Write(ArmyId);
            Hoi4CommandIo.WriteIds(w, UnitIds);
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16();
            TargetCountry = r.ReadUInt16();
            Province = r.ReadUInt16();
            End = r.ReadUInt16();
            ArmyId = r.ReadInt32();
            UnitIds = Hoi4CommandIo.ReadIds(r);
        }
    }

    /// <summary>The player's fallback line through these provinces (land the army can stand on, in the order they
    /// were dragged over), held by these divisions of the army (those already on another plan stay there).</summary>
    public sealed class PlaceFallbackLineCommand : BaseCommand
    {
        public ushort CountryId;
        public int ArmyId;
        public List<ushort> Provinces;
        public List<ushort> UnitIds;

        public override bool Validate(GameState gameState) =>
            Hoi4BattlePlans.Instance != null && Hoi4Armies.Instance?.Get(ArmyId)?.Owner == CountryId
            && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds)
            && Hoi4BattlePlans.Instance.AreFallbackProvinces(CountryId, Provinces);

        public override void Execute(GameState gameState) =>
            Hoi4BattlePlans.Instance.CreateFallbackLine(CountryId, ArmyId, Provinces, UnitIds);

        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId);
            w.Write(ArmyId);
            Hoi4CommandIo.WriteIds(w, Provinces);
            Hoi4CommandIo.WriteIds(w, UnitIds);
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16();
            ArmyId = r.ReadInt32();
            Provinces = Hoi4CommandIo.ReadIds(r);
            UnitIds = Hoi4CommandIo.ReadIds(r);
        }
    }

    /// <summary>Divisions join a front line (and its army, leaving theirs).</summary>
    public sealed class AssignToPlanCommand : BaseCommand
    {
        public ushort CountryId;
        public int PlanId;
        public int OffensiveId;      // assigned to an offensive: kept on its front (0: the line as a whole)
        public List<ushort> UnitIds;

        public override bool Validate(GameState gameState)
        {
            var plan = Hoi4BattlePlans.Instance?.GetPlan(PlanId);
            return plan != null && !plan.IsAI && plan.Owner == CountryId && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds);
        }

        public override void Execute(GameState gameState) => Hoi4BattlePlans.Instance.AssignToPlan(PlanId, UnitIds, OffensiveId);
        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId);
            w.Write(PlanId);
            w.Write(OffensiveId);
            Hoi4CommandIo.WriteIds(w, UnitIds);
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16();
            PlanId = r.ReadInt32();
            OffensiveId = r.ReadInt32();
            UnitIds = Hoi4CommandIo.ReadIds(r);
        }
    }

    /// <summary>Divisions leave their front lines, stop, and stay off lines until assigned again.</summary>
    public sealed class UnassignFromPlanCommand : BaseCommand
    {
        public ushort CountryId;
        public List<ushort> UnitIds;

        public override bool Validate(GameState gameState) =>
            Hoi4BattlePlans.Instance != null && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds);

        public override void Execute(GameState gameState) => Hoi4BattlePlans.Instance.UnassignFromPlans(UnitIds);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); Hoi4CommandIo.WriteIds(w, UnitIds); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); UnitIds = Hoi4CommandIo.ReadIds(r); }
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

    /// <summary>An offensive plan of an army with a front line: provinces to take (held by countries the army's
    /// front lines face, in the order they were dragged over), with these divisions of that army assigned to it.</summary>
    public sealed class PlaceOffensiveCommand : BaseCommand
    {
        public ushort CountryId;
        public int ArmyId;
        public List<ushort> Targets;
        public List<ushort> UnitIds;
        public ushort StartProvince; // a province of the front the arrow starts from (Tab while drawing); 0 = any facing the targets

        public override bool Validate(GameState gameState) =>
            Hoi4BattlePlans.Instance != null && Hoi4Armies.Instance?.Get(ArmyId)?.Owner == CountryId
            && Hoi4CommandIo.Owned(gameState, CountryId, UnitIds)
            && Hoi4BattlePlans.Instance.AreOffensiveTargets(CountryId, ArmyId, Targets);

        public override void Execute(GameState gameState) =>
            Hoi4BattlePlans.Instance.CreateOffensive(CountryId, ArmyId, Targets, UnitIds, StartProvince);

        public override void Undo(GameState gameState) { }

        public override void Serialize(BinaryWriter w)
        {
            w.Write(CountryId);
            w.Write(ArmyId);
            Hoi4CommandIo.WriteIds(w, Targets);
            Hoi4CommandIo.WriteIds(w, UnitIds);
            w.Write(StartProvince);
        }

        public override void Deserialize(BinaryReader r)
        {
            CountryId = r.ReadUInt16();
            ArmyId = r.ReadInt32();
            Targets = Hoi4CommandIo.ReadIds(r);
            UnitIds = Hoi4CommandIo.ReadIds(r);
            StartProvince = r.ReadUInt16();
        }
    }

    public sealed class DeleteOffensiveCommand : BaseCommand
    {
        public ushort CountryId;
        public int OffensiveId;

        public override bool Validate(GameState gameState)
        {
            var o = Hoi4BattlePlans.Instance?.GetOffensive(OffensiveId);
            return o != null && o.Owner == CountryId;
        }

        public override void Execute(GameState gameState) => Hoi4BattlePlans.Instance.DeleteOffensive(OffensiveId);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(OffensiveId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); OffensiveId = r.ReadInt32(); }
    }

    /// <summary>Run or stop an army's battle plans (HOI4 COrderExecuteCommand: 1 execute, 0 stop): the play and
    /// stop buttons above its card. Only for an army with a front line.</summary>
    public sealed class ExecuteBattlePlanCommand : BaseCommand
    {
        public ushort CountryId;
        public int ArmyId;
        public bool Run;                // true execute, false stop

        public override bool Validate(GameState gameState)
        {
            var army = Hoi4Armies.Instance?.Get(ArmyId);
            return army != null && army.Owner == CountryId && army.Units.Count > 0 && Hoi4CommandIo.Owned(gameState, CountryId, new List<ushort> { army.Units[0] })
                && Hoi4BattlePlans.Instance != null && (!Run || Hoi4BattlePlans.Instance.HasFrontLine(ArmyId));
        }

        public override void Execute(GameState gameState) => Hoi4BattlePlans.Instance.SetExecuting(CountryId, ArmyId, Run);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(ArmyId); w.Write(Run); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); ArmyId = r.ReadInt32(); Run = r.ReadBoolean(); }
    }
}
