using System;
using System.Reflection;
using UnityEngine;
using S1API.AssetBundles;

namespace PropHunt.Disguise
{
    /// <summary>
    /// Loads the precompiled fresnel-rim highlight shader from the one embedded AssetBundle
    /// (<c>PropHunt.Assets.Bundles.propoutline</c>) and hands out fresh highlight materials.
    /// The bundle is built once in a 2022.3 editor (see Assets/Shaders/PropOutline.shader); IL2CPP
    /// cannot compile ShaderLab at runtime, so a precompiled bundle is the only path.
    ///
    /// IL2CPP loading note: the managed <c>AssetBundle.LoadFromMemory</c> AND <c>LoadAllAssets</c>
    /// wrappers are stripped from the game binary ("Method unstripping failed"), so we go through
    /// S1API's <see cref="AssetLoader"/>, whose Il2CppAssetBundle reaches both the load and the asset
    /// extraction via native ICalls - the only path that actually works here. Everything is cached +
    /// crash-safe: if the bundle is absent or any step throws, <see cref="TryCreateMaterial"/> returns
    /// null and the caller keeps its glow overlay fallback.
    /// </summary>
    internal static class OutlineShader
    {
        // Resource name = "<RootNamespace>.<folder path with dots>.<file>" (see Inkorporated.ExamplePack).
        private const string ResourceName = "PropHunt.Assets.Bundles.propoutline";
        private const string ShaderName = "PropHunt/PropOutline";

        private static bool _tried;
        private static Shader _shader;

        /// <summary>A new highlight material, or null when the shader bundle is unavailable.</summary>
        internal static Material TryCreateMaterial(Color color, float width)
            => TryCreateMaterial(color, width, color, rimPower: 2.6f, rimIntensity: 2.4f,
                                 pulseSpeed: 2.2f, pulseAmount: 0.30f, fillAlpha: 0.16f);

        /// <summary>
        /// Full-control overload exposing every tunable of the highlight shader. All values are clamped to
        /// the shader's Property ranges by Unity; safe defaults live in the shader, so any subset that
        /// fails to set still looks right.
        /// </summary>
        /// <param name="color">Core/highlight color (drives body tint + alpha master).</param>
        /// <param name="widthPixels">Unused (kept for API compatibility with older call sites).</param>
        /// <param name="rimColor">Color at the silhouette edge of the fresnel rim.</param>
        /// <param name="rimPower">Fresnel falloff exponent (higher = tighter, sharper rim).</param>
        /// <param name="rimIntensity">Rim brightness/alpha multiplier.</param>
        /// <param name="pulseSpeed">Time-pulse frequency.</param>
        /// <param name="pulseAmount">Time-pulse depth (0 = steady).</param>
        /// <param name="fillAlpha">Body fill alpha (the constant tint across the prop's near surface).</param>
        internal static Material TryCreateMaterial(
            Color color, float widthPixels, Color rimColor,
            float rimPower, float rimIntensity, float pulseSpeed, float pulseAmount, float fillAlpha)
        {
            var sh = GetShader();
            if (sh == null) return null;
            try
            {
                var mat = new Material(sh);
                // Each set is independently guarded: a property the shader does not expose must not abort
                // the rest. The shader's own Property defaults cover anything missing.
                TrySetColor(mat, "_OutlineColor", color);
                TrySetColor(mat, "_RimColor", rimColor);
                TrySetFloat(mat, "_RimPower", rimPower);
                TrySetFloat(mat, "_RimIntensity", rimIntensity);
                TrySetFloat(mat, "_PulseSpeed", pulseSpeed);
                TrySetFloat(mat, "_PulseAmount", pulseAmount);
                TrySetFloat(mat, "_CoreAlpha", fillAlpha);   // body-fill alpha used by the current shader
                mat.renderQueue = 3100;
                return mat;
            }
            catch { return null; }
        }

        private static void TrySetColor(Material m, string name, Color c) { try { m.SetColor(name, c); } catch { } }
        private static void TrySetFloat(Material m, string name, float f) { try { m.SetFloat(name, f); } catch { } }

        private static Shader GetShader()
        {
            if (_tried) return _shader;
            _tried = true;
            try
            {
                // S1API's loader uses native ICalls for BOTH the LoadFromMemory and the LoadAllAssets that
                // the managed wrappers strip out in this IL2CPP build.
                var bundle = AssetLoader.GetAssetBundleFromStream(ResourceName, Assembly.GetExecutingAssembly());
                if (bundle == null)
                {
                    Core.Log?.Warning("[PropHunt] outline shader: bundle is null after load - resource name mismatch?");
                    return null;
                }
                Core.LogDebug($"[PropHunt] outline shader: bundle loaded ok");

                Shader[] shaders = bundle.LoadAllAssets<Shader>();
                Core.LogDebug($"[PropHunt] outline shader: LoadAllAssets<Shader> count={(shaders != null ? shaders.Length.ToString() : "null")}");

                _shader = (shaders != null && shaders.Length > 0) ? shaders[0] : null;

                if (_shader != null)
                {
                    Core.LogDebug($"[PropHunt] outline shader loaded: {_shader.name} isSupported={_shader.isSupported}");
                }
                else
                {
                    // Bundle loaded but no shader asset found - try by name as fallback.
                    Core.Log?.Warning("[PropHunt] outline shader: LoadAllAssets returned empty - trying LoadAsset by name");
                    _shader = bundle.LoadAsset<Shader>("PropOutline");
                    if (_shader == null) _shader = bundle.LoadAsset<Shader>("PropHunt/PropOutline");
                    if (_shader != null)
                        Core.LogDebug($"[PropHunt] outline shader loaded via name fallback: {_shader.name} isSupported={_shader.isSupported}");
                    else
                        Core.Log?.Warning("[PropHunt] outline shader: not found by LoadAllAssets or LoadAsset - using glow fallback");
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[PropHunt] outline shader load failed (using glow fallback): " + e.Message);
                _shader = null;
            }
            return _shader;
        }
    }
}
