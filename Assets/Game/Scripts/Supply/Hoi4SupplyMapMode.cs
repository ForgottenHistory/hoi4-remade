using System.Collections.Generic;
using Core;
using Core.Queries;
using Hoi4.Map;
using Hoi4.Units;
using Map.MapModes;
using Map.Rendering;
using UnityEngine;
using FP = Core.Data.FixedPoint64;

namespace Hoi4.Supply
{
    /// <summary>
    /// GAME (presentation): HOI4's supply map mode (F4). Colours, from common/defines/00_graphics.lua:
    ///
    ///   - Each province the player or an ally controls shows its supply (hub supply + its state's pool, what a
    ///     division standing there draws on) as a share of SUPPLY_MAP_MODE_BEST_FLOW_DISPLAY (12) on the
    ///     SUPPLY_MAP_MODE_REACH_COLOR gradient: purple at none, dark blue, blue, light blue, teal, light green at 12+.
    ///   - Where the player's divisions in it average under SUPPLY_STATUS_DISPLAY_THRESHOLD (0.9) supply, it shows
    ///     their status instead (SUPPLY_MAP_MODE_STATUS_COLOR): red at none, orange at 0.7, mustard at 0.9.
    ///   - Everywhere else plain terrain.
    ///
    /// A palette mode (shader mode 6): one colour per province in the engine's province palette, alpha 0 for terrain.
    /// The shader (Hoi4MapModes.hlsl, Hoi4SupplyColor) smooths the colours across provinces like HOI4's heatmap but
    /// stops them at the province's own edge. While active, Hoi4RailRenderer draws the railways as HOI4's white
    /// supply-mode map arrows and Hoi4SeaRoutes the sea supply routes between ports as white convoy ships. Recoloured once a game day while shown (and on activation).
    /// Prototype: Tools/prototypes/supply/supply_mapmode.py.
    /// </summary>
    public sealed class Hoi4SupplyMapMode : BaseMapModeHandler
    {
        public const int ShaderId = 6;

        // SUPPLY_MAP_MODE_REACH_COLOR / _STATUS_COLOR: (position, r, g, b)
        static readonly float[,] Reach =
        {
            { 0.00f, 0.60f, 0.00f, 0.40f }, { 0.02f, 0.20f, 0.17f, 0.52f }, { 0.12f, 0.04f, 0.17f, 0.60f },
            { 0.20f, 0.13f, 0.36f, 0.65f }, { 0.40f, 0.11f, 0.56f, 0.75f }, { 0.60f, 0.25f, 0.71f, 0.76f },
            { 0.80f, 0.47f, 0.80f, 0.73f }, { 1.00f, 0.60f, 0.82f, 0.60f },
        };
        static readonly float[,] Status =
        {
            { 0.0f, 0.90f, 0.00f, 0.00f }, { 0.7f, 0.98f, 0.40f, 0.10f }, { 1.0f, 0.80f, 0.64f, 0.20f },
        };
        const float BestFlow = 12f;          // SUPPLY_MAP_MODE_BEST_FLOW_DISPLAY
        const float StatusThreshold = 0.9f;  // SUPPLY_STATUS_DISPLAY_THRESHOLD

        /// <summary>True while the supply map mode is shown (Hoi4RailRenderer swaps the railways).</summary>
        public static bool Active { get; private set; }

        static Hoi4SupplyMapMode instance;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset() { Active = false; instance = null; }

        /// <summary>From Hoi4Simulation right after the day's supply: redraw at the end of this frame instead of at
        /// the scheduler's next 2-second check (the map lagged the counters by up to 2 s).</summary>
        public static void Refresh()
        {
            if (!Active || instance == null) return;
            Engine.ArchonEngine.Instance?.MapModeManager?.RequestDeferredUpdate(instance);
        }

        public override MapMode Mode => MapMode.Supply;
        public override string Name => "Supply";
        public override int ShaderModeID => ShaderId;
        public override UpdateFrequency GetUpdateFrequency() => UpdateFrequency.Daily; // checked every 2 s, redrawn on a new day

        int paletteIndex = -1;
        int shownDay = -1;
        ushort shownViewer;
        readonly Dictionary<int, Color32> colours = new Dictionary<int, Color32>();

        /// <summary>Register with the engine's map mode manager. Null when it is not ready.</summary>
        public static Hoi4SupplyMapMode Register(MapModeManager manager)
        {
            var handler = new Hoi4SupplyMapMode();
            handler.paletteIndex = manager.RegisterCustomMapMode(ShaderId);
            if (handler.paletteIndex < 0) return null;
            manager.RegisterHandler(MapMode.Supply, handler);
            instance = handler;
            return handler;
        }

        public override void OnActivate(Material mapMaterial, MapModeDataTextures dataTextures)
        {
            SetShaderMode(mapMaterial, ShaderModeID);
            Active = true;
            shownDay = -1; // recolour now
            LogActivation("Supply map mode");
        }

        public override void OnDeactivate(Material mapMaterial)
        {
            Active = false;
            LogDeactivation();
        }

        public override void UpdateTextures(MapModeDataTextures dataTextures, ProvinceQueries provinceQueries,
            CountryQueries countryQueries, ProvinceMapping provinceMapping, object gameProvinceSystem = null)
        {
            var engine = Engine.ArchonEngine.Instance;
            var init = StarterKit.Initializer.Instance;
            if (engine == null || engine.MapModeManager == null || init?.PlayerState == null || !init.PlayerState.HasPlayerCountry)
                return;
            var gs = engine.GameState;
            ushort viewer = init.PlayerState.PlayerCountryId;
            int version = Hoi4Supply.DayVersion;
            if (version == shownDay && viewer == shownViewer) return;
            shownDay = version;
            shownViewer = viewer;

            // the viewer's divisions per province: their average supply ratio
            var ratioSum = new Dictionary<ushort, float>();
            var ratioCount = new Dictionary<ushort, int>();
            foreach (ushort u in gs.Units.GetCountryUnits(viewer))
            {
                if (!gs.Units.HasUnit(u)) continue;
                ushort p = gs.Units.GetUnit(u).provinceID;
                ratioSum[p] = (ratioSum.TryGetValue(p, out float s) ? s : 0f) + Hoi4Supply.Ratio(u).ToFloat();
                ratioCount[p] = (ratioCount.TryGetValue(p, out int n) ? n : 0) + 1;
            }

            var diplomacy = gs.Diplomacy;
            var friendly = new Dictionary<ushort, bool>();
            colours.Clear();
            foreach (int state in Hoi4Regions.States)
                foreach (ushort p in Hoi4Regions.ProvincesOf(state))
                {
                    ushort c = Hoi4Occupation.ControllerOf(gs, p);
                    if (!friendly.TryGetValue(c, out bool ok))
                        friendly[c] = ok = c != 0 && (c == viewer || (diplomacy != null && diplomacy.AreAllied(viewer, c)));
                    if (!ok || Hoi4Regions.IsImpassable(p) || Hoi4Regions.IsWater(p)) { colours[p] = new Color32(0, 0, 0, 0); continue; }
                    if (ratioCount.TryGetValue(p, out int n) && ratioSum[p] / n < StatusThreshold)
                        colours[p] = Gradient(Status, ratioSum[p] / n);
                    else
                        colours[p] = Gradient(Reach, Supply(gs, p).ToFloat() / BestFlow);
                }
            engine.MapModeManager.UpdateProvinceColors(paletteIndex, colours);
        }

        /// <summary>The hub in the province, like HOI4's hub tooltips: the capital hub's supply cap and what it is
        /// made of; a supply hub's or naval base's connection from the capital (its throughput, the rail bottleneck
        /// level or the port that ships it); and the supply demanded of it today. Empty when there is no hub.</summary>
        static string HubText(ushort province)
        {
            if (!Hoi4Railways.TryGetHub(province, out var hub)) return "";
            int port = Hoi4Ports.Level(province);
            string demanded = $"\nDemanded supplies: {Hoi4Supply.Drawn(province).ToFloat():0.0}";
            if (hub.IsCapital)
            {
                Hoi4Railways.CapitalSupplyParts(hub.Country, out FP b, out FP c, out FP m, out FP d);
                return $"\nCapital hub. Supply cap: {hub.Capacity.ToFloat():0.0}"
                       + $"\n  From base: {b.ToFloat():0.0}\n  From civilian factories: {c.ToFloat():0.0}"
                       + $"\n  From military factories: {m.ToFloat():0.0}\n  From dockyards: {d.ToFloat():0.0}" + demanded;
            }
            string name = port > 0 ? $"Naval base, level {port}" : "Supply hub";
            if (!hub.Connected) return $"\n{name}: not connected to the capital";
            string how = hub.BySea ? $"by sea from the port in province {hub.Source}"
                : hub.Source != 0 ? $"rail level {hub.Level} from the port in province {hub.Source}"
                : $"rail level {hub.Level}";
            return $"\n{name}." + demanded + $"\nSupply connection from capital: {hub.Capacity.ToFloat():0.0} ({how})";
        }

        static FP Supply(GameState gs, ushort p) => Hoi4Supply.HubSupply(p) + Hoi4Supply.StateSupply(gs, p);

        static Color32 Gradient(float[,] stops, float t)
        {
            t = Mathf.Clamp01(t);
            int last = stops.GetLength(0) - 1;
            for (int i = 0; i < last; i++)
            {
                if (t > stops[i + 1, 0]) continue;
                float k = (t - stops[i, 0]) / (stops[i + 1, 0] - stops[i, 0]);
                return new Color(Mathf.Lerp(stops[i, 1], stops[i + 1, 1], k), Mathf.Lerp(stops[i, 2], stops[i + 1, 2], k),
                                 Mathf.Lerp(stops[i, 3], stops[i + 1, 3], k), 1f);
            }
            return new Color(stops[last, 1], stops[last, 2], stops[last, 3], 1f);
        }

        public override string GetProvinceTooltip(ushort provinceId, ProvinceQueries provinceQueries, CountryQueries countryQueries)
        {
            var gs = Engine.ArchonEngine.Instance?.GameState;
            if (gs == null) return "";
            string text = $"Supply {Supply(gs, provinceId).ToFloat():0.0} (hubs {Hoi4Supply.HubSupply(provinceId).ToFloat():0.0}, " +
                          $"state {Hoi4Supply.StateSupply(gs, provinceId).ToFloat():0.0})";
            text += HubText(provinceId);
            if (!Hoi4.Weather.Hoi4Weather.Available) return text;
            var parts = new List<string> { $"{Hoi4.Weather.Hoi4Weather.Temperature(provinceId).ToFloat():0} C" };
            var kind = Hoi4.Weather.Hoi4Weather.KindAt(provinceId);
            if (kind != Hoi4.Weather.Hoi4Weather.Kind.None) parts.Add(kind.ToString().ToLowerInvariant());
            int snowLevel = Hoi4.Weather.Hoi4Weather.GroundSnow(provinceId);
            if (snowLevel > 0) parts.Add(snowLevel == 2 ? "deep snow" : "snow");
            if (Hoi4.Weather.Hoi4Weather.IsMud(provinceId)) parts.Add("mud");
            int cut = Mathf.RoundToInt((1f - Hoi4.Weather.Hoi4Weather.SupplyFactor(provinceId).ToFloat()) * 100f);
            if (cut > 0) parts.Add($"hub supply -{cut}%");
            return text + "\n" + string.Join(", ", parts);
        }
    }
}
