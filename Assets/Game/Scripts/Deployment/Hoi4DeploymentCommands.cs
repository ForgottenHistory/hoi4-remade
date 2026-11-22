using System.IO;
using Core;
using Core.Commands;

namespace Hoi4.Deployment
{
    /// <summary>A new deployment order: Lines training lines of the template, deploying to the province (0: no
    /// location set, HOI4's default; they deploy at the capital), training without end until its amount is set.</summary>
    public sealed class CreateDeploymentOrderCommand : BaseCommand
    {
        public ushort CountryId;
        public string Template;
        public int Lines = 1;
        public ushort Province;

        public override bool Validate(GameState gameState)
        {
            var d = Hoi4Deployment.Instance;
            return d != null && Lines >= 1 && Lines <= Hoi4Deployment.MaxAmount && d.CanTrain(CountryId, Template)
                   && (Province == 0 || d.CanDeployTo(CountryId, Province));
        }

        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.Create(CountryId, Template, Lines, Province);

        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Template ?? ""); w.Write(Lines); w.Write(Province); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); Template = r.ReadString(); Lines = r.ReadInt32(); Province = r.ReadUInt16(); }
    }

    /// <summary>More training lines on an order (Add Unit), Lines of them.</summary>
    public sealed class AddDeploymentLinesCommand : BaseCommand
    {
        public ushort CountryId;
        public int OrderId;
        public int Lines = 1;

        public override bool Validate(GameState gameState)
        {
            var o = Hoi4Deployment.Instance?.GetOrder(CountryId, OrderId);
            return o != null && Lines >= 1 && o.Lines.Count + Lines <= Hoi4Deployment.MaxAmount;
        }

        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.AddLines(CountryId, OrderId, Lines);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(OrderId); w.Write(Lines); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); OrderId = r.ReadInt32(); Lines = r.ReadInt32(); }
    }

    /// <summary>How many divisions each of an order's lines trains in turn (the number between - and +): 0 infinite
    /// (HOI4's default, it keeps training), else 1..99.</summary>
    public sealed class SetDeploymentAmountCommand : BaseCommand
    {
        public ushort CountryId;
        public int OrderId;
        public int Amount;

        public override bool Validate(GameState gameState) =>
            Amount >= 0 && Amount <= Hoi4Deployment.MaxAmount && Hoi4Deployment.Instance?.GetOrder(CountryId, OrderId) != null;

        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.SetAmount(CountryId, OrderId, Amount);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(OrderId); w.Write(Amount); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); OrderId = r.ReadInt32(); Amount = r.ReadInt32(); }
    }

    /// <summary>Cancel an order: its men and equipment go back.</summary>
    public sealed class RemoveDeploymentOrderCommand : BaseCommand
    {
        public ushort CountryId;
        public int OrderId;

        public override bool Validate(GameState gameState) => Hoi4Deployment.Instance?.GetOrder(CountryId, OrderId) != null;
        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.RemoveOrder(CountryId, OrderId);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(OrderId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); OrderId = r.ReadInt32(); }
    }

    /// <summary>Priority of an order: 0 low, 1 normal, 2 high (who gets manpower and equipment first).</summary>
    public sealed class SetDeploymentPriorityCommand : BaseCommand
    {
        public ushort CountryId;
        public int OrderId;
        public int Priority;

        public override bool Validate(GameState gameState) =>
            Priority >= Hoi4Deployment.Low && Priority <= Hoi4Deployment.High
            && Hoi4Deployment.Instance?.GetOrder(CountryId, OrderId) != null;

        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.SetPriority(CountryId, OrderId, Priority);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(OrderId); w.Write(Priority); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); OrderId = r.ReadInt32(); Priority = r.ReadInt32(); }
    }

    /// <summary>Where an order's divisions deploy: a province in a state the country holds, connected by its land to the
    /// capital.</summary>
    public sealed class SetDeploymentLocationCommand : BaseCommand
    {
        public ushort CountryId;
        public int OrderId;
        public ushort Province;

        public override bool Validate(GameState gameState)
        {
            var d = Hoi4Deployment.Instance;
            return d?.GetOrder(CountryId, OrderId) != null && d.CanDeployTo(CountryId, Province);
        }

        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.SetProvince(CountryId, OrderId, Province);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(OrderId); w.Write(Province); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); OrderId = r.ReadInt32(); Province = r.ReadUInt16(); }
    }

    /// <summary>Deploy a line before it is fully trained (from 20% training, with the equipment it has).</summary>
    public sealed class DeployEarlyCommand : BaseCommand
    {
        public ushort CountryId;
        public int OrderId;
        public int LineId;

        public override bool Validate(GameState gameState) =>
            Hoi4Deployment.Instance != null && Hoi4Deployment.Instance.CanDeployEarly(CountryId, OrderId, LineId);

        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.DeployEarly(CountryId, OrderId, LineId);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(OrderId); w.Write(LineId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); OrderId = r.ReadInt32(); LineId = r.ReadInt32(); }
    }

    /// <summary>Move an order up (Delta &lt; 0) or down the list (first served among equal priorities).</summary>
    public sealed class MoveDeploymentOrderCommand : BaseCommand
    {
        public ushort CountryId;
        public int OrderId;
        public int Delta;

        public override bool Validate(GameState gameState) => Delta != 0 && Hoi4Deployment.Instance?.GetOrder(CountryId, OrderId) != null;
        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.MoveOrder(CountryId, OrderId, Delta);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(OrderId); w.Write(Delta); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); OrderId = r.ReadInt32(); Delta = r.ReadInt32(); }
    }

    /// <summary>Cancel one training line (its men and equipment go back).</summary>
    public sealed class RemoveDeploymentLineCommand : BaseCommand
    {
        public ushort CountryId;
        public int OrderId;
        public int LineId;

        public override bool Validate(GameState gameState)
        {
            var o = Hoi4Deployment.Instance?.GetOrder(CountryId, OrderId);
            return o != null && o.Lines.Exists(l => l.Id == LineId);
        }

        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.RemoveLine(CountryId, OrderId, LineId);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(OrderId); w.Write(LineId); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); OrderId = r.ReadInt32(); LineId = r.ReadInt32(); }
    }

    /// <summary>Equipment priority of the divisions in the field (reinforcing): 0 low, 1 normal, 2 high.</summary>
    public sealed class SetReinforcementPriorityCommand : BaseCommand
    {
        public ushort CountryId;
        public int Priority;

        public override bool Validate(GameState gameState) =>
            Hoi4Deployment.Instance != null && CountryId != 0 && Priority >= Hoi4Deployment.Low && Priority <= Hoi4Deployment.High;

        public override void Execute(GameState gameState) => Hoi4Deployment.Instance.SetReinforcementPriority(CountryId, Priority);
        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Priority); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); Priority = r.ReadInt32(); }
    }

    /// <summary>A division template's reinforcement priority: 0 reserve, 1 normal, 2 elite (HOI4 sets it in the division
    /// designer; here the template's chevrons on the recruit screen cycle it).</summary>
    /// <summary>Delete a division template no division uses, in the field or in training.</summary>
    public sealed class DeleteTemplateCommand : BaseCommand
    {
        public ushort CountryId;
        public string Template;

        public override bool Validate(GameState gameState)
        {
            var divisions = Hoi4.Units.Hoi4Divisions.Instance;
            var d = Hoi4Deployment.Instance;
            return divisions != null && d != null && d.IsReady
                   && divisions.HasTemplate(gameState.Countries.GetCountryTag(CountryId), Template)
                   && !d.TemplateInUse(CountryId, Template);
        }

        public override void Execute(GameState gameState)
        {
            Hoi4.Units.Hoi4Divisions.Instance.DeleteTemplate(gameState.Countries.GetCountryTag(CountryId), Template);
            Core.Recording.SessionRecorder.Note("deployment", "delete_template", CountryId, 0, 0, 0, Template);
        }

        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Template ?? ""); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); Template = r.ReadString(); }
    }

    public sealed class SetTemplatePriorityCommand : BaseCommand
    {
        public ushort CountryId;
        public string Template;
        public int Priority;

        public override bool Validate(GameState gameState) =>
            Priority >= Hoi4.Units.Hoi4Divisions.Reserve && Priority <= Hoi4.Units.Hoi4Divisions.Elite
            && Hoi4.Units.Hoi4Divisions.Instance != null
            && Hoi4.Units.Hoi4Divisions.Instance.HasTemplate(gameState.Countries.GetCountryTag(CountryId), Template);

        public override void Execute(GameState gameState) =>
            Hoi4.Units.Hoi4Divisions.Instance.SetTemplatePriority(gameState.Countries.GetCountryTag(CountryId), Template, Priority);

        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Template ?? ""); w.Write(Priority); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); Template = r.ReadString(); Priority = r.ReadInt32(); }
    }
}
