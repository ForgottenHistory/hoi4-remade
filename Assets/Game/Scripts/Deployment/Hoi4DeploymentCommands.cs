using System.IO;
using Core;
using Core.Commands;

namespace Hoi4.Deployment
{
    /// <summary>A new deployment order: Amount divisions of the template, deploying to the province (0: the capital).</summary>
    public sealed class CreateDeploymentOrderCommand : BaseCommand
    {
        public ushort CountryId;
        public string Template;
        public int Amount = 1;
        public ushort Province;

        ushort Target(Hoi4Deployment d) => Province != 0 ? Province : d.DefaultProvince(CountryId);

        public override bool Validate(GameState gameState)
        {
            var d = Hoi4Deployment.Instance;
            return d != null && Amount >= 1 && Amount <= Hoi4Deployment.MaxAmount && d.CanTrain(CountryId, Template)
                   && d.CanDeployTo(CountryId, Target(d));
        }

        public override void Execute(GameState gameState)
        {
            var d = Hoi4Deployment.Instance;
            d.Create(CountryId, Template, Amount, Target(d));
        }

        public override void Undo(GameState gameState) { }
        public override void Serialize(BinaryWriter w) { w.Write(CountryId); w.Write(Template ?? ""); w.Write(Amount); w.Write(Province); }
        public override void Deserialize(BinaryReader r) { CountryId = r.ReadUInt16(); Template = r.ReadString(); Amount = r.ReadInt32(); Province = r.ReadUInt16(); }
    }

    /// <summary>More (Amount &gt; 0) or fewer (Amount &lt; 0) divisions on an order; none left removes the order.</summary>
    public sealed class ChangeDeploymentAmountCommand : BaseCommand
    {
        public ushort CountryId;
        public int OrderId;
        public int Amount;

        public override bool Validate(GameState gameState)
        {
            var o = Hoi4Deployment.Instance?.GetOrder(CountryId, OrderId);
            return o != null && Amount != 0 && o.Lines.Count + Amount <= Hoi4Deployment.MaxAmount;
        }

        public override void Execute(GameState gameState)
        {
            if (Amount > 0) Hoi4Deployment.Instance.AddLines(CountryId, OrderId, Amount);
            else Hoi4Deployment.Instance.RemoveLines(CountryId, OrderId, -Amount);
        }

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

    /// <summary>Where an order's divisions deploy: a land province the country owns and controls.</summary>
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
}
