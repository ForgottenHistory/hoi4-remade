using System;
using System.Collections.Generic;
using System.IO;
using Core;
using Core.Loaders;
using Core.Modding;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using TextFontAsset = UnityEngine.TextCore.Text.FontAsset;

namespace Hoi4.UI
{
    /// <summary>
    /// GAME: shared loader for the converted HOI4 interface sprites (PNG files in the data
    /// directory, made by Tools/hoi4_convert.py) and the UI font. Textures are cached for the session.
    /// </summary>
    public static class Hoi4UiAssets
    {
        const string FontFile = "gfx/fonts/Oswald.ttf";

        static readonly Dictionary<string, Texture2D> textures = new Dictionary<string, Texture2D>();
        static TextFontAsset font;
        static bool fontTried;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void Reset()
        {
            textures.Clear();
            font = null;
            fontTried = false;
        }

        /// <summary>A PNG under the data directory (e.g. "gfx/interface/unitpanel/header.png"), or null when missing.
        /// optional: a file that may well not exist (a country's own tech picture), so no warning when it doesn't.</summary>
        public static Texture2D Texture(string relativePath, bool optional = false)
        {
            if (textures.TryGetValue(relativePath, out var t) && t != null) return t;
            t = null;
            string path = DataFileResolver.Resolve(relativePath);
            if (File.Exists(path))
            {
                t = new Texture2D(2, 2, TextureFormat.RGBA32, false)
                {
                    name = "Hoi4Ui_" + relativePath, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
                };
                if (!t.LoadImage(File.ReadAllBytes(path))) { UnityEngine.Object.Destroy(t); t = null; }
            }
            else if (!optional) ArchonLogger.LogWarning($"Hoi4UiAssets: missing {path} (run Tools/hoi4_convert.py)", "game_ui");
            textures[relativePath] = t;
            return t;
        }

        /// <summary>The UI font (Oswald, like the map labels) as an SDF font asset, or null to use the default font.</summary>
        public static TextFontAsset Font
        {
            get
            {
                if (font != null || fontTried) return font;
                fontTried = true;
                string path = DataFileResolver.Resolve(FontFile);
                if (!File.Exists(path)) return null;
                try { font = TextFontAsset.CreateFontAsset(path, 0, 64, 6, GlyphRenderMode.SDFAA, 1024, 1024); }
                catch (Exception ex) { ArchonLogger.LogWarning($"Hoi4UiAssets: could not load {path} ({ex.Message})", "game_ui"); }
                return font;
            }
        }
    }
}
