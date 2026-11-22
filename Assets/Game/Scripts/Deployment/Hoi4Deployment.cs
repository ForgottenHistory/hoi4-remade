using System.Collections;
using System.Collections.Generic;
using Core;
using Hoi4.Units;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Deployment
{
    /// <summary>
    /// GAME: HOI4 training and deployment (recruit & deploy). Each country has deployment orders: a division template,
    /// a province to deploy to (the capital by default) and a priority (low, normal, high). An order holds one
    /// training line per division asked for. Daily, per country in id order, lines by priority (high first), then order,
    /// then line:
    /// - Manpower: a line takes the template's whole manpower from the pool when it starts (HOI4 does too: a German
    ///   1936 line holds its 9,600 men from day one). It waits while the pool is short, or while the men in training
    ///   would go over max(100,000, 75% of the men in the field) (MIN_POSSIBLE_TRAINING_MANPOWER,
    ///   MIN_FIELD_TO_TRAINING_MANPOWER_RATIO).
    /// - Equipment: started lines get up to 10% of their need of each archetype a day from the stockpile, like field
    ///   divisions, but after them (Hoi4Equipment delivers to the field first each day).
    /// - Training: +1 day a day (BASE_DEPLOYMENT_TRAINING) of the template's training time (its slowest sub-unit's
    ///   training_time), never above the line's equipment level: in HOI4 a line's max_training follows its equipment
    ///   (105 of 910 rifles: at most 0.11 trained), so training waits for the factories.
    /// - Deploying: a fully trained line deploys on its own; the player may deploy one early from 20% training
    ///   (MIN_DIVISION_DEPLOYMENT_TRAINING) with the equipment it has. It appears at the order's province, or the
    ///   capital when the country no longer holds that province; with neither it waits.
    /// - Fewer divisions or a cancelled order: the lines' men go back to the pool and their equipment to the stockpile.
    /// Orders are changed only through commands (Hoi4DeploymentCommands), so every client runs the same days.
    /// </summary>
    public sealed class Hoi4Deployment : MonoBehaviour
    {
        public const int MaxAmount = 99;
        public const int Low = 0, Normal = 1, High = 2;

        static readonly FP DeliveryChunk = FP.FromFraction(1, 10);       // of a line's need, a day (as field divisions)
        static readonly FP MinDeployTraining = FP.FromFraction(2, 10);   // MIN_DIVISION_DEPLOYMENT_TRAINING
        static readonly FP MinTrainingManpower = FP.FromInt(100000);     // MIN_POSSIBLE_TRAINING_MANPOWER
        static readonly FP FieldToTraining = FP.FromFraction(75, 100);   // MIN_FIELD_TO_TRAINING_MANPOWER_RATIO
        const int ReportEveryDays = 7;

        public sealed class Line
        {
            public int Id;
            public Hoi4Division Division;   // not on the map yet: UnitId 0, its men and equipment are the line's
            public FP Training;             // 0..1
            public bool Started => Division.Men >= FP.FromInt(Division.Manpower);
        }

        public sealed class Order
        {
            public int Id;
            public string Template;
            public ushort Province;
            public int Priority = Normal;
            public int TrainingDays;
            public readonly List<Line> Lines = new List<Line>();
        }

        sealed class Country
        {
            public readonly List<Order> Orders = new List<Order>();
            public int NextOrder = 1, NextLine = 1;
        }

        public static Hoi4Deployment Instance { get; private set; }

        GameState gameState;
        readonly SortedDictionary<ushort, Country> countries = new SortedDictionary<ushort, Country>();
        int day;

        public bool IsReady { get; private set; }

        /// <summary>Raised after a command or a day changed a country's orders (UI refresh).</summary>
        public event System.Action<ushort> Changed;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Deployment");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Deployment>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        IEnumerator Start()
        {
            while (Hoi4Divisions.Instance == null || !Hoi4Divisions.Instance.IsLoaded
                   || Hoi4Manpower.Instance == null || !Hoi4Manpower.Instance.IsReady
                   || Hoi4Equipment.Instance == null || !Hoi4Equipment.Instance.IsReady)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            IsReady = true;
        }

        // ---- queries ------------------------------------------------------------------------------------

        static readonly List<Order> None = new List<Order>();

        public IReadOnlyList<Order> Orders(ushort country) => countries.TryGetValue(country, out var c) ? c.Orders : None;

        public Order GetOrder(ushort country, int orderId)
        {
            foreach (var o in Orders(country)) if (o.Id == orderId) return o;
            return null;
        }

        public string Tag(ushort country) => gameState?.Countries.GetCountryTag(country);

        public bool CanTrain(ushort country, string template) =>
            IsReady && country != 0 && Hoi4Divisions.Instance.HasTemplate(Tag(country), template)
            && Hoi4Divisions.Instance.TrainingDays(Tag(country), template) > 0;

        /// <summary>Whether the country may deploy to the province: it owns and controls it, on land.</summary>
        public bool CanDeployTo(ushort country, ushort province) =>
            IsReady && province != 0 && gameState.Provinces.HasProvince(province) && !Hoi4.Map.Hoi4Regions.IsWater(province)
            && gameState.Provinces.GetProvinceOwner(province) == country && gameState.Provinces.GetProvinceController(province) == country;

        /// <summary>Where a country's new orders deploy: its capital (HOI4's default).</summary>
        public ushort DefaultProvince(ushort country) => Hoi4.Map.Hoi4Regions.Capital(Tag(country));

        /// <summary>Men in training (lines that have started).</summary>
        public FP MenInTraining(ushort country)
        {
            FP men = FP.Zero;
            foreach (var o in Orders(country))
                foreach (var l in o.Lines) men += l.Division.Men;
            return men;
        }

        /// <summary>Most men the country may have in training: max(100,000, 75% of the men in the field).</summary>
        public FP TrainingCap(ushort country, List<Hoi4Division> field)
        {
            FP men = FP.Zero;
            if (field != null) foreach (var d in field) men += FP.FromInt(d.Manpower);
            return FP.Max(MinTrainingManpower, men * FieldToTraining);
        }

        // ---- changes (commands only) --------------------------------------------------------------------

        /// <summary>A new order of `amount` divisions of the template, deploying to the province. Returns its id.</summary>
        public int Create(ushort country, string template, int amount, ushort province)
        {
            if (!CanTrain(country, template)) return 0;
            if (!countries.TryGetValue(country, out var c)) countries[country] = c = new Country();
            var order = new Order
            {
                Id = c.NextOrder++,
                Template = template,
                Province = province,
                TrainingDays = Hoi4Divisions.Instance.TrainingDays(Tag(country), template),
            };
            c.Orders.Add(order);
            AddLines(country, order.Id, amount);
            Core.Recording.SessionRecorder.Note("deployment", "order", country, 0, province, 0,
                $"order {order.Id}: {amount} x {template} ({order.TrainingDays} days), deploy to {province}");
            return order.Id;
        }

        /// <summary>More divisions on an order (each a new line at its end).</summary>
        public void AddLines(ushort country, int orderId, int amount)
        {
            var order = GetOrder(country, orderId);
            if (order == null || !countries.TryGetValue(country, out var c)) return;
            string tag = Tag(country);
            for (int i = 0; i < amount && order.Lines.Count < MaxAmount; i++)
            {
                var division = Hoi4Divisions.Instance.NewDivision(tag, order.Template);
                if (division == null) break;
                division.Men = FP.Zero; // taken from the pool when the line starts
                order.Lines.Add(new Line { Id = c.NextLine++, Division = division });
            }
            Changed?.Invoke(country);
        }

        /// <summary>Fewer divisions: the last lines go (the least trained, newest first), their men and equipment back.</summary>
        public void RemoveLines(ushort country, int orderId, int amount)
        {
            var order = GetOrder(country, orderId);
            if (order == null) return;
            for (int i = 0; i < amount && order.Lines.Count > 0; i++)
            {
                Refund(country, order.Lines[order.Lines.Count - 1]);
                order.Lines.RemoveAt(order.Lines.Count - 1);
            }
            if (order.Lines.Count == 0) countries[country].Orders.Remove(order);
            Changed?.Invoke(country);
        }

        public void RemoveOrder(ushort country, int orderId)
        {
            var order = GetOrder(country, orderId);
            if (order == null) return;
            foreach (var l in order.Lines) Refund(country, l);
            countries[country].Orders.Remove(order);
            Core.Recording.SessionRecorder.Note("deployment", "cancel", country, 0, order.Province, 0,
                $"order {order.Id}: {order.Lines.Count} x {order.Template} cancelled");
            Changed?.Invoke(country);
        }

        public void SetPriority(ushort country, int orderId, int priority)
        {
            var order = GetOrder(country, orderId);
            if (order == null) return;
            order.Priority = Mathf.Clamp(priority, Low, High);
            Changed?.Invoke(country);
        }

        public void SetProvince(ushort country, int orderId, ushort province)
        {
            var order = GetOrder(country, orderId);
            if (order == null) return;
            order.Province = province;
            Changed?.Invoke(country);
        }

        /// <summary>Whether a line may deploy now: started and at least 20% trained.</summary>
        public bool CanDeployEarly(ushort country, int orderId, int lineId)
        {
            var line = FindLine(GetOrder(country, orderId), lineId);
            return line != null && line.Started && line.Training >= MinDeployTraining;
        }

        public void DeployEarly(ushort country, int orderId, int lineId)
        {
            var order = GetOrder(country, orderId);
            var line = FindLine(order, lineId);
            if (line != null && Deploy(country, order, line)) Changed?.Invoke(country);
        }

        static Line FindLine(Order order, int lineId)
        {
            if (order == null) return null;
            foreach (var l in order.Lines) if (l.Id == lineId) return l;
            return null;
        }

        void Refund(ushort country, Line line)
        {
            var resources = gameState.Resources;
            var d = line.Division;
            if (d.Men > FP.Zero)
                resources.SetResource(country, Hoi4Manpower.ResourceId, resources.GetResource(country, Hoi4Manpower.ResourceId) + d.Men);
            for (int a = 0; a < d.Have.Length; a++)
                if (d.Have[a] > FP.Zero)
                {
                    ushort id = (ushort)(Hoi4Equipment.StockResourceBase + a);
                    resources.SetResource(country, id, resources.GetResource(country, id) + d.Have[a]);
                }
            d.Men = FP.Zero;
            System.Array.Clear(d.Have, 0, d.Have.Length);
        }

        /// <summary>The line's division onto the map; false when the country holds neither the order's province
        /// nor its capital.</summary>
        bool Deploy(ushort country, Order order, Line line)
        {
            ushort province = CanDeployTo(country, order.Province) ? order.Province
                : CanDeployTo(country, DefaultProvince(country)) ? DefaultProvince(country) : (ushort)0;
            if (province == 0) return false;
            var d = line.Division;
            d.Strength = d.FullStrength;
            d.Organisation = d.MaxOrganisation;
            ushort unitId = Hoi4Divisions.Instance.Deploy(d, province, country);
            if (unitId == 0) return false;
            order.Lines.Remove(line);
            if (order.Lines.Count == 0) countries[country].Orders.Remove(order);
            Core.Recording.SessionRecorder.Note("deployment", "deployed", country, unitId, province, 0,
                $"{d.Name} ({order.Template}) trained {line.Training.ToFloat():0.00}, equipment {d.EquipmentLevel.ToFloat():0.00}");
            Hoi4UnitCounters.Instance?.MarkDirty();
            return true;
        }

        // ---- daily --------------------------------------------------------------------------------------

        /// <summary>Daily, from Hoi4Simulation after Hoi4Equipment (the field is equipped first) and before Hoi4Manpower:
        /// lines start, get equipment, train, and the trained ones deploy.</summary>
        public void SimulateDay()
        {
            if (!IsReady || countries.Count == 0) return;
            var resources = gameState.Resources;
            var byCountry = Hoi4Equipment.DivisionsByCountry(gameState, Hoi4Divisions.Instance);
            int n = Hoi4Divisions.Archetypes.Length;
            var stock = new FP[n];
            var ready = new List<(Order, Line)>();
            day++;

            resources.BeginBatch();
            foreach (var kv in countries)
            {
                ushort country = kv.Key;
                var c = kv.Value;
                if (c.Orders.Count == 0) continue;
                byCountry.TryGetValue(country, out var field);
                FP pool = resources.GetResource(country, Hoi4Manpower.ResourceId);
                FP cap = TrainingCap(country, field), training = MenInTraining(country);
                for (int a = 0; a < n; a++) stock[a] = resources.GetResource(country, (ushort)(Hoi4Equipment.StockResourceBase + a));
                bool changed = false;

                foreach (var order in ByPriority(c.Orders))
                {
                    FP perDay = FP.FromFraction(1, System.Math.Max(1, order.TrainingDays));
                    foreach (var line in order.Lines)
                    {
                        var d = line.Division;
                        if (!line.Started)
                        {
                            FP need = FP.FromInt(d.Manpower) - d.Men;
                            if (pool < need || training + need > cap) continue; // waits for manpower
                            d.Men += need;
                            pool -= need;
                            training += need;
                            changed = true;
                        }
                        for (int a = 0; a < n; a++)
                        {
                            if (stock[a] <= FP.Zero || d.Have[a] >= d.Need[a]) continue;
                            FP give = FP.Min(stock[a], FP.Min(d.Need[a] * DeliveryChunk, d.Need[a] - d.Have[a]));
                            d.Have[a] += give;
                            stock[a] -= give;
                        }
                        line.Training = FP.Min(line.Training + perDay, d.EquipmentLevel);
                        if (line.Training >= FP.One) ready.Add((order, line));
                    }
                }
                resources.SetResource(country, Hoi4Manpower.ResourceId, pool);
                for (int a = 0; a < n; a++) resources.SetResource(country, (ushort)(Hoi4Equipment.StockResourceBase + a), stock[a]);

                foreach (var (order, line) in ready) changed |= Deploy(country, order, line);
                ready.Clear();
                if (changed) Changed?.Invoke(country);
                if (day % ReportEveryDays == 0) Report(country, c, pool, cap);
            }
            resources.EndBatch();
        }

        /// <summary>High priority first, then order of creation (stable).</summary>
        static List<Order> ByPriority(List<Order> orders)
        {
            var list = new List<Order>(orders);
            list.Sort((a, b) => a.Priority != b.Priority ? b.Priority.CompareTo(a.Priority) : a.Id.CompareTo(b.Id));
            return list;
        }

        /// <summary>Weekly note per country with lines: how many, started, mean training and equipment, waiting
        /// for manpower ("lines 12 started 8 training 0.41 equipment 0.45 | waiting 4 | pool 120000 cap 150000").</summary>
        void Report(ushort country, Country c, FP pool, FP cap)
        {
            int lines = 0, started = 0;
            FP training = FP.Zero, equipment = FP.Zero;
            foreach (var o in c.Orders)
                foreach (var l in o.Lines)
                {
                    lines++;
                    if (!l.Started) continue;
                    started++;
                    training += l.Training;
                    equipment += l.Division.EquipmentLevel;
                }
            if (lines == 0) return;
            float mean(FP v) => started > 0 ? (v / started).ToFloat() : 0f;
            Core.Recording.SessionRecorder.Note("deployment", "week", country, 0, 0, 0,
                $"lines {lines} started {started} training {mean(training):0.00} equipment {mean(equipment):0.00} | " +
                $"waiting {lines - started} | pool {pool.ToInt()} cap {cap.ToInt()}");
        }
    }
}
