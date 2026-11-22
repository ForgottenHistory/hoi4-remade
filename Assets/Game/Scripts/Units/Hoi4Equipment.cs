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
        readonly Dictionary<ushort, FP[]> made = new Dictionary<ushort, FP[]>(), delivered = new Dictionary<ushort, FP[]>();
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
                if (p.Value["stockpile"] is JObject stock)
                    foreach (var s in stock.Properties())
                        if (index.TryGetValue(s.Name, out int a))
                            resources.SetResource(country, (ushort)(StockResourceBase + a), Num(s.Value));
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
                if (consumers.Count > 0)
                    for (int a = 0; a < n; a++)
                        for (int tier = 8; tier >= 0 && stock[a] > FP.Zero; tier--)
                            delivered[country][a] += Share(consumers, tier, a, ref stock[a]);
                for (int a = 0; a < n; a++) resources.SetResource(country, (ushort)(StockResourceBase + a), stock[a]);
            }
            resources.EndBatch();

            if (day % ReportEveryDays == 0) Report();
        }

        /// <summary>Equipment of one archetype to everyone of one priority: what each wants (up to 10% of its need a
        /// day), all of it when the stockpile covers the total, else the same fraction of it for each. Returns what
        /// was given.</summary>
        static FP Share(List<(Hoi4Division division, int priority)> consumers, int priority, int a, ref FP stock)
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
                d.Have[a] += give;
                given += give;
            }
            stock -= given;
            return given;
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
        }

        static FP Num(JToken t) => t != null && t.Type != JTokenType.Null ? FP.FromDouble((double)t) : FP.Zero;
    }
}
