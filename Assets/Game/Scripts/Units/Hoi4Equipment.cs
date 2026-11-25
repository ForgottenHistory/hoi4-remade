using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Core;
using Core.Loaders;
using Core.Modding;
using Newtonsoft.Json.Linq;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Units
{
    /// <summary>
    /// GAME: HOI4 equipment stockpiles and equipment reinforcement (production is Hoi4.Production.Hoi4Production,
    /// which adds what its lines make to these stockpiles first each day).
    /// - Stockpiles: one engine ResourceSystem resource per equipment archetype the divisions need (id
    ///   StockResourceBase + index into Hoi4Divisions.Archetypes), so they are saved with the game. They start at the
    ///   country history's add_equipment_to_stockpile, which is almost nothing (Germany has none: HOI4's stockpile is a
    ///   pass-through, production goes straight to the divisions).
    /// - Daily delivery: field divisions (reinforcing) and started training lines (Hoi4.Deployment) each want up to 10%
    ///   of their need of an archetype. Highest priority first (the country's reinforcement priority for the field,
    ///   each order's for its lines; all Normal by default), and within it field divisions by their template's priority
    ///   (elite, normal, reserve; lines count as normal); within one tier everyone gets the same share of what they
    ///   want when the stockpile is short (HOI4 splits equipment between reinforcing and training this way).
    ///   Hoi4Manpower then raises strength to what equipment and men allow.
    /// - Versions: each stockpile and each division's equipment keeps which equipment versions it is made of
    ///   (Hoi4EquipmentMix; the amounts above stay the totals). The 1936 divisions and stockpiles hold their country's
    ///   start version (Hoi4Divisions.StartVersion), production adds its line's version, deliveries hand out the
    ///   newest first. Then upgrades (HOI4 "upgrading fielded units"): a division with all it needs of an archetype
    ///   but older versions than the stockpile's newest swaps up to 10% of its need a day, the old equipment going
    ///   back to the stockpile, in the same priority order. A division's stats follow what it holds
    ///   (Hoi4Divisions.ApplyEquipmentStats). Returns to the stockpile (disbanding, cancelled training, a smaller
    ///   template) go through Return with their versions.
    /// Countries, lines and divisions all go in id or file order: the same on every machine.
    /// </summary>
    public sealed class Hoi4Equipment : MonoBehaviour
    {
        const string OobFile = "history/units/oob_1936.json5";

        /// <summary>Engine resource id of the first archetype's stockpile (manpower is 1).</summary>
        public const ushort StockResourceBase = 100;

        static readonly FP DeliveryChunk = FP.FromFraction(1, 10);          // of a division's need, a day
        const int ReportEveryDays = 7;

        public static Hoi4Equipment Instance { get; private set; }

        GameState gameState;
        readonly List<ushort> countries = new List<ushort>(); // with a stockpile, in id order
        readonly Dictionary<ushort, FP[]> made = new Dictionary<ushort, FP[]>(), delivered = new Dictionary<ushort, FP[]>(),
            upgraded = new Dictionary<ushort, FP[]>();
        readonly Dictionary<ushort, Hoi4EquipmentMix[]> stockVersions = new Dictionary<ushort, Hoi4EquipmentMix[]>();
        readonly List<(Hoi4.Production.Hoi4EquipmentCatalog.Entry entry, FP amount)> pieces =
            new List<(Hoi4.Production.Hoi4EquipmentCatalog.Entry, FP)>();
        readonly List<(Hoi4Division division, int priority)> upgradeDue = new List<(Hoi4Division, int)>();
        readonly List<(Hoi4.Production.Hoi4EquipmentCatalog.Entry entry, FP amount)> upgradeBack =
            new List<(Hoi4.Production.Hoi4EquipmentCatalog.Entry, FP)>();
        int day;

        public bool IsReady { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Spawn()
        {
            if (!Hoi4.Hoi4Scenes.Active) return; // not in other scenes (the engine's StarterKit scene)
            var go = new GameObject("Hoi4Equipment");
            DontDestroyOnLoad(go);
            go.AddComponent<Hoi4Equipment>();
        }

        void Awake() => Instance = this;

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        /// <summary>The engine's ResourceSystem, initialised by the game when nothing else has (it holds manpower and
        /// the equipment stockpiles).</summary>
        public static void EnsureResources(GameState gameState)
        {
            if (!gameState.Resources.IsInitialized)
                gameState.Resources.Initialize(4096, gameState.EventBus);
        }

        /// <summary>The divisions of each country, in id order.</summary>
        public static Dictionary<ushort, List<Hoi4Division>> DivisionsByCountry(GameState gameState, Hoi4Divisions divisions)
        {
            var byCountry = new Dictionary<ushort, List<Hoi4Division>>();
            foreach (var d in divisions.All.Values)
            {
                if (!gameState.Units.HasUnit(d.UnitId)) continue;
                ushort c = gameState.Units.GetUnit(d.UnitId).countryID;
                if (!byCountry.TryGetValue(c, out var list)) byCountry[c] = list = new List<Hoi4Division>();
                list.Add(d);
            }
            foreach (var list in byCountry.Values) list.Sort((a, b) => a.UnitId.CompareTo(b.UnitId));
            return byCountry;
        }

        /// <summary>The country's stockpile of an archetype (index into Hoi4Divisions.Archetypes).</summary>
        public FP Stock(ushort country, int archetype) =>
            IsReady ? gameState.Resources.GetResource(country, (ushort)(StockResourceBase + archetype)) : FP.Zero;

        /// <summary>Which versions the country's stockpile of an archetype is made of (made on first use).</summary>
        public Hoi4EquipmentMix StockMix(ushort country, int archetype)
        {
            if (!stockVersions.TryGetValue(country, out var m))
                stockVersions[country] = m = new Hoi4EquipmentMix[Hoi4Divisions.Archetypes.Length];
            return m[archetype] ?? (m[archetype] = new Hoi4EquipmentMix());
        }

        /// <summary>Production: AMOUNT of EQUIPMENT (a version, "infantry_equipment_1") into the stockpile.</summary>
        public void AddStock(ushort country, int archetype, string equipment, FP amount)
        {
            if (!IsReady || amount <= FP.Zero || archetype < 0 || archetype >= Hoi4Divisions.Archetypes.Length) return;
            ushort id = (ushort)(StockResourceBase + archetype);
            FP before = gameState.Resources.GetResource(country, id);
            StockMix(country, archetype).Add(Hoi4.Production.Hoi4EquipmentCatalog.Get(equipment), amount, before);
            gameState.Resources.SetResource(country, id, before + amount);
        }

        /// <summary>AMOUNT of a division's equipment of an archetype back to the country's stockpile, in the division's
        /// mix of versions (its shares stay: the caller lowers its Have).</summary>
        public static void Return(GameState gameState, ushort country, Hoi4Division d, int archetype, FP amount)
        {
            if (amount <= FP.Zero) return;
            var resources = gameState.Resources;
            ushort id = (ushort)(StockResourceBase + archetype);
            FP before = resources.GetResource(country, id);
            var mix = d.Versions != null && archetype < d.Versions.Length ? d.Versions[archetype] : null;
            var self = Instance;
            if (self != null && self.IsReady && mix != null && mix.Count > 0)
            {
                var stock = self.StockMix(country, archetype);
                FP total = before;
                for (int i = 0; i < mix.Count; i++)
                {
                    FP part = mix.Share(i) * amount;
                    stock.Add(mix.Entry(i), part, total);
                    total += part;
                }
            }
            resources.SetResource(country, id, before + amount);
        }

        /// <summary>Production adds what a line made today (for the weekly note).</summary>
        public void CountMade(ushort country, int archetype, FP amount)
        {
            if (made.TryGetValue(country, out var m) && archetype >= 0 && archetype < m.Length) m[archetype] += amount;
        }

        IEnumerator Start()
        {
            while (Hoi4Divisions.Instance == null || !Hoi4Divisions.Instance.IsLoaded
                   || Engine.ArchonEngine.Instance == null || Engine.ArchonEngine.Instance.GameState == null
                   || Engine.ArchonEngine.Instance.GameState.Resources == null || !DataFileResolver.IsInitialized)
                yield return null;
            gameState = Engine.ArchonEngine.Instance.GameState;
            var archetypes = Hoi4Divisions.Archetypes;
            var index = new Dictionary<string, int>();
            for (int i = 0; i < archetypes.Length; i++) index[archetypes[i]] = i;

            var resources = gameState.Resources;
            EnsureResources(gameState);
            for (int i = 0; i < archetypes.Length; i++)
            {
                ushort id = (ushort)(StockResourceBase + i);
                if (!resources.IsResourceRegistered(id))
                    resources.RegisterResource(id, new Core.Resources.ResourceDefinition
                    {
                        id = archetypes[i], displayName = archetypes[i], icon = archetypes[i], startingAmount = 0, minValue = 0, maxValue = 0,
                    });
            }

            string path = DataFileResolver.Resolve(OobFile);
            var data = File.Exists(path) ? Json5Loader.LoadJson5File(path)["countries"] as JObject : null;
            if (data == null)
            {
                ArchonLogger.LogWarning($"Hoi4Equipment: no {OobFile}; no stockpiles", "game_units");
                yield break;
            }

            resources.BeginBatch();
            foreach (var p in data.Properties())
            {
                ushort country = gameState.Countries.GetCountryIdFromTag(p.Name);
                if (country == 0) continue;
                countries.Add(country);
                made[country] = new FP[archetypes.Length];
                delivered[country] = new FP[archetypes.Length];
                upgraded[country] = new FP[archetypes.Length];
                if (p.Value["stockpile"] is JObject stock)
                    foreach (var s in stock.Properties())
                        if (index.TryGetValue(s.Name, out int a))
                        {
                            resources.SetResource(country, (ushort)(StockResourceBase + a), Num(s.Value));
                            StockMix(country, a).Set(Hoi4Divisions.StartVersion(p.Name, a)); // its start version
                        }
            }
            resources.EndBatch();
            countries.Sort();
            IsReady = true;
            ArchonLogger.Log($"Hoi4Equipment: {archetypes.Length} archetypes, stockpiles for {countries.Count} countries", "game_units");
        }

        /// <summary>Daily, from Hoi4Simulation after Hoi4Production and before Hoi4Manpower: deliveries from the stockpiles.</summary>
        public void SimulateDay()
        {
            if (!IsReady || Hoi4Divisions.Instance == null) return;
            var resources = gameState.Resources;
            int n = Hoi4Divisions.Archetypes.Length;
            var byCountry = DivisionsByCountry(gameState, Hoi4Divisions.Instance);
            var stock = new FP[n];
            var deployment = Hoi4.Deployment.Hoi4Deployment.Instance;
            var consumers = new List<(Hoi4Division division, int priority)>();
            day++;

            resources.BeginBatch();
            foreach (ushort country in countries)
            {
                for (int a = 0; a < n; a++) stock[a] = resources.GetResource(country, (ushort)(StockResourceBase + a));
                consumers.Clear();
                // tier = equipment priority x 3 + template priority (0..8, served from the top)
                int fieldPriority = deployment != null ? deployment.ReinforcementPriority(country) : Hoi4.Deployment.Hoi4Deployment.Normal;
                var divisions = Hoi4Divisions.Instance;
                if (byCountry.TryGetValue(country, out var list))
                    foreach (var d in list) consumers.Add((d, fieldPriority * 3 + divisions.TemplatePriority(d.Tag, d.Template)));
                int lines = consumers.Count;
                deployment?.StartedLines(country, consumers);
                for (int i = lines; i < consumers.Count; i++)
                    consumers[i] = (consumers[i].division, consumers[i].priority * 3 + Hoi4Divisions.Normal);
                stockVersions.TryGetValue(country, out var mixes);
                if (consumers.Count > 0)
                    for (int a = 0; a < n; a++)
                    {
                        var mix = mixes?[a];
                        for (int tier = 8; tier >= 0 && stock[a] > FP.Zero; tier--)
                            delivered[country][a] += Share(consumers, tier, a, ref stock[a], mix);
                        if (mix != null && mix.Count > 0 && stock[a] > FP.Zero)
                            upgraded[country][a] += Upgrade(consumers, a, ref stock[a], mix);
                    }
                for (int a = 0; a < n; a++) resources.SetResource(country, (ushort)(StockResourceBase + a), stock[a]);
                foreach (var (d, _) in consumers)
                    if (d.VersionsChanged) Hoi4Divisions.ApplyEquipmentStats(d);
            }
            resources.EndBatch();

            if (day % ReportEveryDays == 0) Report();
        }

        /// <summary>Equipment of one archetype to everyone of one priority: what each wants (up to 10% of its need a
        /// day), all of it when the stockpile covers the total, else the same fraction of it for each. Returns what
        /// was given.</summary>
        FP Share(List<(Hoi4Division division, int priority)> consumers, int priority, int a, ref FP stock, Hoi4EquipmentMix mix)
        {
            FP wanted = FP.Zero;
            foreach (var (d, p) in consumers)
                if (p == priority) wanted += Want(d, a);
            if (wanted <= FP.Zero) return FP.Zero;
            FP fraction = stock >= wanted ? FP.One : stock / wanted;
            FP given = FP.Zero;
            foreach (var (d, p) in consumers)
            {
                if (p != priority) continue;
                FP give = FP.Min(Want(d, a) * fraction, stock - given);
                if (give <= FP.Zero) continue;
                Hand(d, a, give, stock - given, mix, true);
                given += give;
            }
            stock -= given;
            return given;
        }

        /// <summary>AMOUNT of the stockpile (STOCK before it, its versions MIX) into the division, newest versions first
        /// (oldest first: OLDEST).</summary>
        void Hand(Hoi4Division d, int a, FP amount, FP stock, Hoi4EquipmentMix mix, bool newest)
        {
            pieces.Clear();
            mix?.Take(amount, stock, newest, pieces);
            FP have = d.Have[a];
            if (pieces.Count > 0)
            {
                var dm = d.Mix(a);
                foreach (var (e, x) in pieces)
                {
                    d.VersionsChanged |= dm.Add(e, x, have);
                    have += x;
                }
            }
            d.Have[a] += amount;
        }

        /// <summary>Divisions with all they need of an archetype but holding versions older than the stockpile's newest
        /// swap up to 10% of their need a day for it, highest priority first; the old equipment goes back to the
        /// stockpile. Returns the amount swapped.</summary>
        FP Upgrade(List<(Hoi4Division division, int priority)> consumers, int a, ref FP stock, Hoi4EquipmentMix mix)
        {
            var newest = mix.Newest;
            FP available = mix.ShareOf(newest) * stock, swapped = FP.Zero;
            var back = upgradeBack;
            // one pass for who could upgrade (full, holding something older than the newest): mostly nobody, once
            // the army is through an upgrade
            var due = upgradeDue;
            due.Clear();
            int top = -1, bottom = int.MaxValue;
            foreach (var c in consumers)
            {
                var d = c.division;
                if (d.Need == null || d.Need[a] <= FP.Zero || d.Have[a] < d.Need[a]) continue;
                var dm = d.Versions != null && a < d.Versions.Length ? d.Versions[a] : null;
                if (dm == null || dm.Count == 0 || !Hoi4EquipmentMix.Before(newest, dm.Oldest)) continue;
                due.Add(c);
                top = System.Math.Max(top, c.priority);
                bottom = System.Math.Min(bottom, c.priority);
            }
            for (int tier = top; tier >= bottom && available > FP.Zero; tier--)
                foreach (var (d, p) in due)
                {
                    if (p != tier || available <= FP.Zero) continue;
                    var dm = d.Versions[a];
                    FP older = dm.OlderThan(newest, d.Have[a]);
                    if (older <= FP.Zero) continue;
                    FP x = FP.Min(FP.Min(d.Need[a] * DeliveryChunk, older), available);
                    back.Clear();
                    dm.Take(x, d.Have[a], false, back);   // its oldest out
                    dm.Add(newest, x, d.Have[a] - x);     // the newest in
                    d.VersionsChanged = true;
                    mix.Take(x, stock, true, null);       // the newest out of the stockpile
                    FP total = stock - x;
                    foreach (var (e, y) in back) { mix.Add(e, y, total); total += y; } // the old in
                    available -= x;
                    swapped += x;
                }
            return swapped;
        }

        static FP Want(Hoi4Division d, int a) =>
            d.Need == null || d.Have[a] >= d.Need[a] ? FP.Zero : FP.Min(d.Need[a] * DeliveryChunk, d.Need[a] - d.Have[a]);

        /// <summary>A note per country with lines or deliveries in the last week: made, delivered, stockpile
        /// ("made infantry_equipment=378 ... | delivered ... | stock ...", Tools/recorder/report.py reads it).</summary>
        void Report()
        {
            var names = Hoi4Divisions.Archetypes;
            foreach (ushort country in countries)
            {
                FP[] m = made[country], dl = delivered[country];
                bool any = false;
                for (int a = 0; a < names.Length; a++) any |= m[a] > FP.Zero || dl[a] > FP.Zero;
                if (!any) continue;
                var sb = new StringBuilder("made");
                for (int a = 0; a < names.Length; a++) if (m[a] > FP.Zero) sb.Append($" {names[a]}={m[a].ToInt()}");
                sb.Append(" | delivered");
                for (int a = 0; a < names.Length; a++) if (dl[a] > FP.Zero) sb.Append($" {names[a]}={dl[a].ToInt()}");
                sb.Append(" | stock");
                for (int a = 0; a < names.Length; a++)
                {
                    FP s = Stock(country, a);
                    if (s > FP.Zero) sb.Append($" {names[a]}={s.ToInt()}");
                }
                Core.Recording.SessionRecorder.Note("equipment", "week", country, 0, 0, 0, sb.ToString());
                System.Array.Clear(m, 0, m.Length);
                System.Array.Clear(dl, 0, dl.Length);
            }
            ReportVersions();
        }

        /// <summary>A note per country that upgraded or holds more than one version of an archetype: what it upgraded
        /// this week and the versions its divisions hold ("upgraded infantry_equipment=120 | field
        /// infantry_equipment: infantry_equipment_1=8100 infantry_equipment_2=900; ...").</summary>
        void ReportVersions()
        {
            var names = Hoi4Divisions.Archetypes;
            var byCountry = DivisionsByCountry(gameState, Hoi4Divisions.Instance);
            foreach (ushort country in countries)
            {
                FP[] up = upgraded[country];
                var field = new SortedDictionary<string, FP>[names.Length];
                bool mixed = false;
                if (byCountry.TryGetValue(country, out var list))
                    foreach (var d in list)
                    {
                        if (d.Versions == null) continue;
                        for (int a = 0; a < d.Versions.Length; a++)
                        {
                            var mx = d.Versions[a];
                            if (mx == null || d.Have[a] <= FP.Zero) continue;
                            for (int i = 0; i < mx.Count; i++)
                            {
                                var f = field[a] ?? (field[a] = new SortedDictionary<string, FP>(System.StringComparer.Ordinal));
                                f.TryGetValue(mx.Entry(i).Name, out var have);
                                f[mx.Entry(i).Name] = have + mx.Share(i) * d.Have[a];
                            }
                        }
                    }
                for (int a = 0; a < names.Length; a++) mixed |= field[a] != null && field[a].Count > 1;
                bool any = mixed;
                for (int a = 0; a < names.Length; a++) any |= up[a] > FP.Zero;
                if (!any) continue;
                var sb = new StringBuilder("upgraded");
                for (int a = 0; a < names.Length; a++) if (up[a] > FP.Zero) sb.Append($" {names[a]}={up[a].ToInt()}");
                sb.Append(" | field");
                bool first = true;
                for (int a = 0; a < names.Length; a++)
                {
                    if (field[a] == null || (field[a].Count < 2 && up[a] <= FP.Zero)) continue;
                    sb.Append(first ? " " : "; ").Append(names[a]).Append(':');
                    first = false;
                    foreach (var kv in field[a]) sb.Append($" {kv.Key}={kv.Value.ToInt()}");
                }
                Core.Recording.SessionRecorder.Note("equipment", "versions", country, 0, 0, 0, sb.ToString());
                System.Array.Clear(up, 0, up.Length);
            }
        }

        static FP Num(JToken t) => t != null && t.Type != JTokenType.Null ? FP.FromDouble((double)t) : FP.Zero;
    }
}
