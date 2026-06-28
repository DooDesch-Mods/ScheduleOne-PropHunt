using System;
using System.Reflection;
using UnityEngine;
using S1API.AssetBundles;

namespace PropHunt.PlayArea
{
    /// <summary>
    /// Loads the precompiled border-wall shader from the embedded AssetBundle (PropHunt.Assets.Bundles.propborder)
    /// and creates materials. Mirrors Disguise/OutlineShader.cs exactly (same S1API AssetLoader native-ICall path,
    /// same LoadAllAssets-then-name-fallback, same crash-safe try/catch). If the bundle is absent (not built yet),
    /// TryCreateMaterial returns null and PlayAreaBorder simply shows no wall.
    /// </summary>
    internal static class BorderShader
    {
        private const string ResourceName = "PropHunt.Assets.Bundles.propborder";

        private static bool _tried;
        private static Shader _shader;

        internal static Material TryCreateMaterial(Color wallColor, Color rimColor)
        {
            var sh = GetShader();
            if (sh == null) return null;
            try
            {
                var mat = new Material(sh);
                TrySetColor(mat, "_WallColor", wallColor);
                TrySetColor(mat, "_RimColor", rimColor);
                mat.renderQueue = 3010;
                return mat;
            }
            catch { return null; }
        }

        private static void TrySetColor(Material m, string name, Color c) { try { m.SetColor(name, c); } catch { } }

        private static Shader GetShader()
        {
            if (_tried) return _shader;
            _tried = true;
            try
            {
                var bundle = AssetLoader.GetAssetBundleFromStream(ResourceName, Assembly.GetExecutingAssembly());
                if (bundle == null) { Core.Log?.Warning("[PropHunt] border shader: bundle null - not built yet?"); return null; }

                Shader[] shaders = bundle.LoadAllAssets<Shader>();
                _shader = (shaders != null && shaders.Length > 0) ? shaders[0] : null;
                if (_shader == null)
                {
                    _shader = bundle.LoadAsset<Shader>("Border");
                    if (_shader == null) _shader = bundle.LoadAsset<Shader>("PropHunt/Border");
                }

                if (_shader != null) Core.LogDebug($"[PropHunt] border shader loaded: {_shader.name} isSupported={_shader.isSupported}");
                else Core.Log?.Warning("[PropHunt] border shader: not found in bundle - wall will be invisible");
            }
            catch (Exception e) { Core.Log?.Warning("[PropHunt] border shader load failed: " + e.Message); _shader = null; }
            return _shader;
        }
    }
}
