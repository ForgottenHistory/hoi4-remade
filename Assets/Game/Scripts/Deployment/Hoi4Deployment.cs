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
    /// a province to deploy to (none at first: HOI4's red "No location set", they deploy at the capital then; the
    /// player picks a state its land connects to the capital, never overseas), a priority (low, normal, high) and a series amount. An order
    /// holds training lines (Add Unit adds one), each training one division at a time: when it deploys the line starts
    /// the next, until it has made the order's amount of them (0 = infinite, HOI4's default: it keeps training; amount
    /// 1: the line goes after its first division). Daily, per country in id order, lines by priority (high first), then the order's place
    /// in the list, then line:
    /// - Manpower: a line takes the template's whole manpower from the pool when it starts (HOI4 does too: a German
    ///   1936 line holds its 9,600 men from day one). It waits while the pool is short, or while the men in training
    ///   would go over max(100,000, 75% of the men in the field) (MIN_POSSIBLE_TRAINING_MANPOWER,
    ///   MIN_FIELD_TO_TRAINING_MANPOWER_RATIO).
    /// - Equipment: Hoi4Equipment hands it out each day to field divisions and started lines together, by priority
    ///   (the order's for its lines, the country's reinforcement priority for the field): higher first, an equal share
    ///   of what is needed within one priority, as HOI4 splits equipment between reinforcing and training.
    /// - Training: +1 day a day (BASE_DEPLOYMENT_TRAINING) of the template's training time (its slowest sub-unit's
    ///   training_time), never above the line's equipment level: in HOI4 a line's max_training follows its equipment
    ///   (105 of 910 rifles: at most 0.11 trained), so training waits for the factories.
    /// - Deploying: a fully trained line deploys on its own; the player may deploy one early from 20% training
    ///   (MIN_DIVISION_DEPLOYMENT_TRAINING) with the equipment it has. It appears at the order's province while that
    ///   is still a valid location, else at the capital; Its experience is its training of the way to
    ///   level 1 (DEPLOY_TRAINING_MAX_LEVEL): fully trained it is Trained, early it is Green. It appears at the order's province, or the
    ///   with neither it waits.
    /// - A cancelled line or order: the men in training go back to the pool and their equipment to the stockpile.
    /// Orders are changed only through commands (Hoi4DeploymentCommands), so every client runs the same days.
    /// </summary>
    public sealed class Hoi4Deployment : MonoBehaviour
    {
        public const int MaxAmount = 99;
        public const int Low = 0, Normal = 1, High = 2;

        static readonly FP MinDeployTraining = FP.FromFraction(2, 10);   // MIN_DIVISION_DEPLOYMENT_TRAINING
        static readonly FP MinTrainingManpower = FP.FromInt(100000);     // MIN_POSSIBLE_TRAINING_MANPOWER
        static readonly FP FieldToTraining = FP.FromFraction(75, 100);   // MIN_FIELD_TO_TRAINING_MANPOWER_RATIO
        const int ReportEveryDays = 7;

        public sealed class Line
        {
            public int Id;
            public Hoi4Division Division;   // not on the map yet: UnitId 0, its men and equipment are the line's
            public FP Training;             // 0..1
            public int Series = 1;          // which division of the order's amount this is (1, 2, ...)
            public bool Started => Division.Men >= FP.FromInt(Division.Manpower);
        }

        public sealed class Order
        {
            public int Id;
            public string Template;
            public ushort Province;
            public int Priority = Normal;
            public int TrainingDays;
            public int Amount;              // divisions each line trains in turn; 0 = infinite (keeps training)
            public readonly List<Line> Lines = new List<Line>();
        }

        sealed class Country
        {
            public readonly List<Order> Orders = new List<Order>();
            public int NextOrder = 1, NextLine = 1;
            public int Reinforcement = Normal; // equipment priority of the divisions in the field
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

        /// <summary>Equipment priority of the country's divisions in the field (reinforcing), Normal by default.</summary>
        public int ReinforcementPriority(ushort country) => countries.TryGetValue(country, out var c) ? c.Reinforcement : Normal;

        public void SetReinforcementPriority(ushort country, int priority)
        {
            if (!countries.TryGetValue(country, out var c)) countries[country] = c = new Country();
            c.Reinforcement = Mathf.Clamp(priority, Low, High);
            Changed?.Invoke(country);
        }

        /// <summary>The started lines of a country with their order's priority, in the order they are served
        /// (Hoi4Equipment's daily delivery).</summary>
        public void StartedLines(ushort country, List<(Hoi4Division division, int priority)> into)
        {
            if (!countries.TryGetValue(country, out var c)) return;
            foreach (var o in ByPriority(c.Orders))
                foreach (var l in o.Lines)
                    if (l.Started) into.Add((l.Division, o.Priority));
        }

        public bool CanTrain(ushort country, string template) =>
            IsReady && country != 0 && Hoi4Divisions.Instance.HasTemplate(Tag(country), template)
            && Hoi4Divisions.Instance.TrainingDays(Tag(country), template) > 0;

        /// <summary>A template was saved in the division designer: orders follow a rename, and divisions in training
        /// take the new layout (what they no longer need goes back, as for divisions in the field).</summary>
        public void TemplateSaved(ushort country, string original, string name)
        {
            if (original == null || !countries.TryGetValue(country, out var c)) return;
            var divisions = Hoi4.Units.Hoi4Divisions.Instance;
            foreach (var order in c.Orders)
            {
                if (order.Template != original) continue;
                order.Template = name;
                foreach (var line in order.Lines) divisions.Restructure(gameState, country, line.Division, name);
            }
            Changed?.Invoke(country);
        }

        /// <summary>Whether divisions of the country use the template, in the field or in training (HOI4 then won't
        /// delete it).</summary>
        public bool TemplateInUse(ushort country, string template)
        {
            string tag = Tag(country);
            foreach (var d in Hoi4.Units.Hoi4Divisions.Instance.All.Values)
                if (d.Tag == tag && d.Template == template) return true;
            if (countries.TryGetValue(country, out var c))
                foreach (var order in c.Orders)
                    if (order.Template == template) return true;
            return false;
        }

        /// <summary>Whether the country may deploy to the province: it owns and controls it, on land, in a state its own
        /// land connects to the capital (HOI4: no deploying overseas).</summary>
        public bool CanDeployTo(ushort country, ushort province) =>
            Held(country, province) && DeployableStates(country).Contains(Hoi4.Map.Hoi4Regions.State(province));

        bool Held(ushort country, ushort province) =>
            IsReady && province != 0 && gameState.Provinces.HasProvince(province) && !Hoi4.Map.Hoi4Regions.IsWater(province)
            && gameState.Provinces.GetProvinceOwner(province) == country && gameState.Provinces.GetProvinceController(province) == country;

        /// <summary>States the country may deploy to: those with a province it owns and controls, reached from its capital
        /// over land it controls. Empty while it doesn't hold its capital.</summary>
        public HashSet<int> DeployableStates(ushort country)
        {
            var states = new HashSet<int>();
            ushort capital = DefaultProvince(country);
            if (!Held(country, capital)) return states;
            var provinces = gameState.Provinces;
            var seen = new HashSet<ushort> { capital };
            var queue = new Queue<ushort>();
            queue.Enqueue(capital);
            while (queue.Count > 0)
            {
                ushort p = queue.Dequeue();
                if (provinces.GetProvinceOwner(p) == country) states.Add(Hoi4.Map.Hoi4Regions.State(p));
                var neighbours = gameState.Adjacencies.GetNeighbors(p, Unity.Collections.Allocator.Temp);
                foreach (ushort q in neighbours)
                    if (q != 0 && !seen.Contains(q) && !Hoi4.Map.Hoi4Regions.IsWater(q) && provinces.GetProvinceController(q) == country)
                    {
                        seen.Add(q);
                        queue.Enqueue(q);
                    }
                neighbours.Dispose();
            }
            states.Remove(0);
            return states;
        }

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

        /// <summary>A new order of the template with `lines` training lines, deploying to the province, training
        /// without end (amount infinite) until told otherwise. Returns its id.</summary>
        public int Create(ushort country, string template, int lines, ushort province)
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
            AddLines(country, order.Id, lines);
            Core.Recording.SessionRecorder.Note("deployment", "order", country, 0, province, 0,
                $"order {order.Id}: {lines} x {template} ({order.TrainingDays} days), deploy to {province}");
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

        /// <summary>Cancel one line: its men and equipment go back; the order goes with its last line.</summary>
        public void RemoveLine(ushort country, int orderId, int lineId)
        {
            var order = GetOrder(country, orderId);
            var line = FindLine(order, lineId);
            if (line == null) return;
            Refund(country, line);
            order.Lines.Remove(line);
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

        /// <summary>How many divisions each line trains (0 = infinite). Lines already past it finish their division
        /// and go.</summary>
        public void SetAmount(ushort country, int orderId, int amount)
        {
            var order = GetOrder(country, orderId);
            if (order == null) return;
            order.Amount = Mathf.Clamp(amount, 0, MaxAmount);
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

        /// <summary>An order up (delta &lt; 0) or down the list: among orders of the same priority the earlier one gets
        /// manpower and equipment first.</summary>
        public void MoveOrder(ushort country, int orderId, int delta)
        {
            if (!countries.TryGetValue(country, out var c)) return;
            int at = c.Orders.FindIndex(o => o.Id == orderId);
            if (at < 0) return;
            int to = Mathf.Clamp(at + delta, 0, c.Orders.Count - 1);
            if (to == at) return;
            var order = c.Orders[at];
            c.Orders.RemoveAt(at);
            c.Orders.Insert(to, order);
            Changed?.Invoke(country);
        }

        /// <summary>Experience a division deploys with: its training of the way to level 1, trained
        /// (DEPLOY_TRAINING_MAX_LEVEL: training goes no higher), so fully trained it is Trained, deployed early Green.</summary>
        public static FP DeployExperience(FP training) =>
            Hoi4Division.LevelStart(1) * FP.Clamp(training, FP.Zero, FP.One);

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

        void Refund(ushort country, Line line) => Refund(gameState, country, line.Division);

        /// <summary>A division's men back to the manpower pool and its equipment to the stockpile (cancelled training,
        /// disbanding); it is left with neither.</summary>
        public static void Refund(GameState gameState, ushort country, Hoi4.Units.Hoi4Division d)
        {
            var resources = gameState.Resources;
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
            ushort province = order.Province != 0 && CanDeployTo(country, order.Province) ? order.Province
                : Held(country, DefaultProvince(country)) ? DefaultProvince(country) : (ushort)0;
            if (province == 0) return false;
            var d = line.Division;
            d.Strength = d.FullStrength;
            d.Organisation = d.MaxOrganisation;
            d.Experience = DeployExperience(line.Training);
            ushort unitId = Hoi4Divisions.Instance.Deploy(d, province, country);
            if (unitId == 0) return false;
            var next = order.Amount == 0 || line.Series < order.Amount ? Hoi4Divisions.Instance.NewDivision(Tag(country), order.Template) : null;
            if (next != null)
            {
                // the line trains the next division of the series: new men, new equipment, from no training
                next.Men = FP.Zero;
                line.Division = next;
                line.Training = FP.Zero;
                line.Series++;
            }
            else
            {
                order.Lines.Remove(line);
                if (order.Lines.Count == 0) countries[country].Orders.Remove(order);
            }
            Core.Recording.SessionRecorder.Note("deployment", "deployed", country, unitId, province, 0,
                $"{d.Name} ({order.Template}) trained {line.Training.ToFloat():0.00}, equipment {d.EquipmentLevel.ToFloat():0.00}");
            Hoi4UnitCounters.Instance?.MarkDirty();
            return true;
        }

        // ---- daily --------------------------------------------------------------------------------------

        /// <summary>Daily, from Hoi4Simulation after Hoi4Equipment (which equips the field and the started lines) and
        /// before Hoi4Manpower: lines start, train, and the trained ones deploy.</summary>
        public void SimulateDay()
        {
            if (!IsReady || countries.Count == 0) return;
            var resources = gameState.Resources;
            var byCountry = Hoi4Equipment.DivisionsByCountry(gameState, Hoi4Divisions.Instance);
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
                        line.Training = FP.Min(line.Training + perDay, d.EquipmentLevel);
                        if (line.Training >= FP.One) ready.Add((order, line));
                    }
                }
                resources.SetResource(country, Hoi4Manpower.ResourceId, pool);

                foreach (var (order, line) in ready) changed |= Deploy(country, order, line);
                ready.Clear();
                if (changed) Changed?.Invoke(country);
                if (day % ReportEveryDays == 0) Report(country, c, pool, cap);
            }
            resources.EndBatch();
        }

        /// <summary>High priority first, then the order of the list (the player can move orders up and down).</summary>
        static List<Order> ByPriority(List<Order> orders)
        {
            var list = new List<Order>(orders.Count);
            for (int p = High; p >= Low; p--)
                foreach (var o in orders) if (o.Priority == p) list.Add(o);
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
