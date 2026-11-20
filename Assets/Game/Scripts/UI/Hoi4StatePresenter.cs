using System.Collections.Generic;
using System.Linq;
using Core;
using Core.Localization;
using Hoi4.Map;
using UnityEngine.UIElements;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: stateless presentation logic for the state panel. Reads game state and HOI4 state data and
    /// fills the elements of StatePanel.uxml (structure and look live in the UXML/USS, not here).
    /// </summary>
    public static class Hoi4StatePresenter
    {
        const string Dir = "gfx/interface/stateview/";
        const int SharedSlots = 25;
        static readonly string[] StateBuildings = { "infrastructure", "air_base", "anti_air_building", "radar_station" };

        /// <summary>Fill the panel for a province. False when it has no HOI4 state (water, unknown).</summary>
        public static bool Show(VisualElement panel, GameState gameState, ushort province)
        {
            int stateId = Hoi4Regions.State(province);
            var info = stateId >= 0 ? Hoi4StateData.Get(stateId) : null;
            if (info == null || Hoi4Regions.IsWater(province)) return false;

            string owner = OwnerTag(gameState, province, info);
            panel.Q<Label>("title").text = StateName(stateId).ToUpperInvariant();
            panel.Q<Label>("manpower").text = Compact(info.Manpower);

            SetFlag(panel.Q("owner-flag"), owner, false);
            var claims = panel.Q("claims");
            claims.Clear();
            foreach (string tag in info.Cores.Where(c => c != owner).Take(3))
            {
                var f = new VisualElement { pickingMode = PickingMode.Ignore };
                f.style.width = 47; f.style.height = 32; f.style.marginRight = 3;
                SetFlag(f, tag, true);
                claims.Add(f);
            }

            FillResources(panel.Q("resources"), info);
            FillStateBuildings(panel.Q("state-buildings"), info);
            FillSlots(panel.Q("slots"), panel.Q<Label>("slots-count"), info);

            string terrain = TerrainKey(gameState, province);
            string terrainName = LocalizationManager.Get(terrain);
            if (string.IsNullOrEmpty(terrainName) || terrainName == terrain) terrainName = terrain;
            panel.Q<Label>("province-header").text = $"Province: <color=#f7d45a>{terrainName}</color>";
            Hoi4Sprites.Apply(panel.Q("province-picture"), Dir + $"terrain/{terrain}.png");
            SetSmallFlag(panel.Q("province-flag"), owner);

            info.Provincial.TryGetValue(province, out var provBuildings);
            FillProvinceBuildings(panel.Q("province-buildings"), provBuildings);
            return true;
        }

        // --- Sections -------------------------------------------------------------------------

        static void FillResources(VisualElement grid, Hoi4StateData.StateInfo info)
        {
            grid.Clear();
            for (int i = 0; i < Hoi4StateData.ResourceOrder.Length; i++)
            {
                if (!info.Resources.TryGetValue(Hoi4StateData.ResourceOrder[i], out int amount) || amount <= 0) continue;
                var slot = new VisualElement { pickingMode = PickingMode.Ignore };
                slot.AddToClassList("resource");
                var icon = Hoi4Sprites.Make("resource-icon");
                Hoi4Sprites.Apply(icon, Dir + $"resources_{i}.png");
                slot.Add(icon);
                slot.Add(MakeLabel("resource-amount", "center", amount.ToString()));
                grid.Add(slot);
            }
        }

        static void FillStateBuildings(VisualElement row, Hoi4StateData.StateInfo info)
        {
            row.Clear();
            foreach (string b in StateBuildings)
            {
                var def = Hoi4StateData.Def(b);
                if (def == null) continue;
                info.Buildings.TryGetValue(b, out int level);
                row.Add(Entry(def, level, def.StateMax, "entry"));
            }
        }

        static void FillSlots(VisualElement grid, Label count, Hoi4StateData.StateInfo info)
        {
            grid.Clear();
            var shared = new List<Hoi4StateData.BuildingDef>();
            foreach (var kv in info.Buildings)
            {
                var def = Hoi4StateData.Def(kv.Key);
                if (def != null && def.Shared)
                    for (int n = 0; n < kv.Value; n++) shared.Add(def);
            }
            int unlocked = System.Math.Max(Hoi4StateData.Slots(info.Category), shared.Count);
            count.text = $"<color=#f7d45a>{unlocked}</color>/{SharedSlots} Unlocked Slots";
            for (int i = 0; i < SharedSlots; i++)
            {
                var slot = new VisualElement { pickingMode = PickingMode.Ignore };
                slot.AddToClassList("slot");
                if (i < shared.Count)
                {
                    slot.Add(Hoi4Sprites.Make("slot-bg"));
                    var icon = Hoi4Sprites.Make("slot-icon");
                    Hoi4Sprites.Apply(icon, Dir + $"buildings_{shared[i].Frame - 1}.png");
                    slot.Add(icon);
                }
                else
                {
                    slot.Add(Hoi4Sprites.Make("slot-icon", i < unlocked ? "slot-empty" : "slot-locked"));
                }
                grid.Add(slot);
            }
        }

        static void FillProvinceBuildings(VisualElement row, Dictionary<string, int> provBuildings)
        {
            row.Clear();
            var shown = new List<string> { "bunker" };
            if (provBuildings != null)
                foreach (var kv in provBuildings)
                    if (kv.Value > 0 && !shown.Contains(kv.Key) && Hoi4StateData.Def(kv.Key) != null) shown.Add(kv.Key);
            for (int i = 0; i < shown.Count && i < 7; i++)
            {
                var def = Hoi4StateData.Def(shown[i]);
                if (def == null) continue;
                int level = 0;
                provBuildings?.TryGetValue(shown[i], out level);
                row.Add(Entry(def, level, def.ProvinceMax, "province-entry"));
            }
        }

        // --- Pieces ---------------------------------------------------------------------------

        static VisualElement Entry(Hoi4StateData.BuildingDef def, int level, int max, string cls)
        {
            var entry = new VisualElement { pickingMode = PickingMode.Ignore };
            entry.AddToClassList(cls);
            entry.Add(Hoi4Sprites.Make("entry-bg"));
            var icon = Hoi4Sprites.Make("entry-icon");
            Hoi4Sprites.Apply(icon, Dir + $"buildings_{def.Frame - 1}.png");
            entry.Add(icon);
            entry.Add(MakeLabel("entry-level", "center", max > 0 ? $"{level}/{max}" : level.ToString()));
            return entry;
        }

        static Label MakeLabel(string cls, string align, string text)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            l.AddToClassList("hoi4-label");
            l.AddToClassList(align);
            l.AddToClassList(cls);
            return l;
        }

        /// <summary>Shield flag with HOI4's frame (52x38 thick, 47x32 thin) as children of a positioned host.</summary>
        static void SetFlag(VisualElement host, string tag, bool thin)
        {
            host.Clear();
            if (string.IsNullOrEmpty(tag)) return;
            float fw = thin ? 47 : 52, fh = thin ? 32 : 38;
            var flag = Hoi4Sprites.Make();
            Hoi4Sprites.Apply(flag, Dir + $"flags/{tag}.png");
            flag.style.left = (fw - 41) * 0.5f; flag.style.top = (fh - 26) * 0.5f;
            flag.style.width = 41; flag.style.height = 26;
            var frame = Hoi4Sprites.Make();
            Hoi4Sprites.Apply(frame, Dir + (thin ? "flag_frame_thin.png" : "flag_frame.png"));
            frame.style.left = 0; frame.style.top = 0; frame.style.width = fw; frame.style.height = fh;
            host.Add(flag);
            host.Add(frame);
        }

        /// <summary>The small flag beside the province header: frameless, half size.</summary>
        static void SetSmallFlag(VisualElement host, string tag)
        {
            host.Clear();
            if (string.IsNullOrEmpty(tag)) return;
            var flag = Hoi4Sprites.Make();
            Hoi4Sprites.Apply(flag, Dir + $"flags/{tag}.png");
            flag.style.left = 0; flag.style.top = 0; flag.style.width = 20; flag.style.height = 13;
            host.Add(flag);
        }

        static string OwnerTag(GameState gameState, ushort province, Hoi4StateData.StateInfo info)
        {
            ushort owner = gameState.ProvinceQueries.GetOwner(province);
            string tag = owner != 0 ? gameState.Countries.GetCountryTag(owner) : null;
            return string.IsNullOrEmpty(tag) ? info.Owner : tag;
        }

        static string TerrainKey(GameState gameState, ushort province)
        {
            // Province terrain is the 0-based TerrainId (terrain.json5 order); the registry reserves slot 0,
            // so Registry.Get(id) is off by one. Match on TerrainId instead.
            ushort id = gameState.ProvinceQueries.GetTerrain(province);
            foreach (var t in gameState.Registries.Terrains.GetAll())
                if (t.TerrainId == id) return t.Key;
            return "plains";
        }

        static string StateName(int stateId)
        {
            string key = $"STATE_{stateId}";
            string name = LocalizationManager.Get(key);
            return string.IsNullOrEmpty(name) || name == key ? $"State {stateId}" : name;
        }

        static string Compact(int n)
        {
            if (n >= 1_000_000) return (n / 1_000_000f).ToString("0.##") + "M";
            if (n >= 1_000) return (n / 1_000f).ToString("0.#") + "K";
            return n.ToString();
        }
    }
}
