using System.Collections.Generic;
using Core;
using Core.Queries;
using Map.Rendering;
using Map.MapModes;
using Unity.Collections;
using UnityEngine;

namespace Hoi4.Map
{
    /// <summary>
    /// GAME: HOI4's diplomatic map mode, drawn over the terrain: your own country in green, allies in
    /// blue, countries you are at war with in red, everyone else plain terrain. Opened by the
    /// diplomacy panel and refreshed when a war starts or ends. A palette mode (shader mode 5): the
    /// colors live in the engine's province palette, alpha 0 shows terrain.
    /// </summary>
    public sealed class Hoi4DiplomaticMapMode : BaseMapModeHandler
    {
        public const int ShaderId = 5;

        static readonly Color32 Self = new Color32(46, 190, 52, 255);
        static readonly Color32 Ally = new Color32(70, 140, 220, 255);
        static readonly Color32 Enemy = new Color32(205, 50, 46, 255);
        static readonly Color32 None = new Color32(0, 0, 0, 0);

        public override MapMode Mode => MapMode.Diplomatic;
        public override string Name => "Diplomatic";
        public override int ShaderModeID => ShaderId;
        public override UpdateFrequency GetUpdateFrequency() => UpdateFrequency.PerConquest;

        /// <summary>The country whose diplomacy is shown; drawn green.</summary>
        public static ushort Target;

        static readonly int ActiveId = Shader.PropertyToID("_Hoi4DiplomacyActive");
        static readonly int ColorsId = Shader.PropertyToID("_Hoi4DiplomacyColors");
        const int PaletteWidth = 4096; // same as the engine country palette

        int paletteIndex = -1;
        Texture2D countryColors;
        readonly Color32[] pixels = new Color32[PaletteWidth];

        /// <summary>Register with the engine's map mode manager. False when it is not ready.</summary>
        public static Hoi4DiplomaticMapMode Register(MapModeManager manager)
        {
            var handler = new Hoi4DiplomaticMapMode();
            handler.paletteIndex = manager.RegisterCustomMapMode(ShaderId);
            if (handler.paletteIndex < 0) return null;
            manager.RegisterHandler(MapMode.Diplomatic, handler);
            return handler;
        }

        public override void OnActivate(Material mapMaterial, MapModeDataTextures dataTextures)
        {
            SetShaderMode(mapMaterial, ShaderModeID);
            Shader.SetGlobalFloat(ActiveId, 1f); // the shader colors by country, not by province palette
            LogActivation("Diplomatic map mode");
        }

        public override void OnDeactivate(Material mapMaterial)
        {
            Shader.SetGlobalFloat(ActiveId, 0f);
            LogDeactivation();
        }

        public override void UpdateTextures(MapModeDataTextures dataTextures, ProvinceQueries provinceQueries,
            CountryQueries countryQueries, ProvinceMapping provinceMapping, object gameProvinceSystem = null)
        {
            var engine = Engine.ArchonEngine.Instance;
            var manager = engine != null ? engine.MapModeManager : null;
            var init = StarterKit.Initializer.Instance;
            if (manager == null || init == null || init.PlayerState == null || !init.PlayerState.HasPlayerCountry)
                return;

            var diplomacy = engine.GameState.Diplomacy;

            // Country -> color, once per country
            var byCountry = new Dictionary<ushort, Color32>();
            Color32 For(ushort owner)
            {
                if (owner == 0) return None;
                if (byCountry.TryGetValue(owner, out var c)) return c;
                if (owner == Target) c = Self;
                else if (diplomacy != null && diplomacy.IsAtWar(Target, owner)) c = Enemy;
                else if (diplomacy != null && diplomacy.AreAllied(Target, owner)) c = Ally;
                else c = None;
                byCountry[owner] = c;
                return c;
            }

            // One pixel per country. The shader blends them with the political edge gradient.
            if (countryColors == null)
            {
                countryColors = new Texture2D(PaletteWidth, 1, TextureFormat.RGBA32, false, linear: true)
                {
                    filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp, name = "Hoi4DiplomacyColors"
                };
            }
            System.Array.Clear(pixels, 0, pixels.Length);
            using var ids = engine.GameState.Countries.GetAllCountryIds(Allocator.Temp);
            for (int i = 0; i < ids.Length; i++)
                if (ids[i] < PaletteWidth) pixels[ids[i]] = For(ids[i]);
            countryColors.SetPixels32(pixels);
            countryColors.Apply(false);
            Shader.SetGlobalTexture(ColorsId, countryColors);
        }

        public override string GetProvinceTooltip(ushort provinceId, ProvinceQueries provinceQueries, CountryQueries countryQueries)
        {
            ushort owner = provinceQueries.GetOwner(provinceId);
            return owner != 0 ? countryQueries.GetTag(owner) : "";
        }
    }
}
