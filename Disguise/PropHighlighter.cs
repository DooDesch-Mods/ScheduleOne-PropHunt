using System.Collections.Generic;
using UnityEngine;
using PropHunt.Game;

namespace PropHunt.Disguise
{
    /// <summary>
    /// LOCAL hider aid during the Hiding phase: paints a pulsing white shell on every becomable world object
    /// near the player, so a hider can see at a glance what the gamemode recognises as a prop (this doubles as
    /// a debug signal - the log/`phprops` report how many it found). Built on the same crash-safe clone-mesh
    /// pattern as <see cref="DisguiseController"/> (a render-only child mesh + an owned, tinted material copy) -
    /// the game's EPOOutline cannot be component-added at runtime in IL2CPP (it hard-crashes), so we do not use
    /// it. Best-effort "through walls" via ZTest Always + a high render queue when the shader supports it.
    /// Periodically rescans a cheap sphere around the player (capped); everything is try/caught and the whole
    /// aid self-disables on the first failure (the HUD look-at hint remains the fallback).
    /// </summary>
    internal sealed class PropHighlighter
    {
        private sealed class Hl { internal GameObject Go; internal Material Mat; internal bool Outline; }

        private readonly GameModeController _ctl;
        private readonly Dictionary<int, Hl> _shells = new Dictionary<int, Hl>();   // prop instanceID -> highlight shell
        private readonly HashSet<int> _inRange = new HashSet<int>();
        private readonly List<MeshFilter> _candidates = new List<MeshFilter>();
        private float _nextScan;
        private int _lastLoggedCount = -1;
        private bool _failed;
        private bool _active;

        private const float ScanRadius = 22f;
        private const float ScanInterval = 0.4f;
        private const int MaxHighlights = 40;

        internal PropHighlighter(GameModeController ctl) { _ctl = ctl; }

        internal int HighlightedCount => _shells.Count;

        internal void Tick()
        {
            if (_failed) return;
            // hiders see what they can become during hiding AND hunting (the shells are LOCAL-only)
            bool want = (_ctl.Phase == RoundPhase.Hiding || _ctl.Phase == RoundPhase.Hunting)
                        && _ctl.LocalRole == PlayerRole.Hider && PropCatalog.Count > 0;
            if (!want) { if (_active) { ClearAll(); _lastLoggedCount = -1; } _active = false; return; }
            _active = true;
            try
            {
                if (Time.time >= _nextScan) { _nextScan = Time.time + ScanInterval; Rescan(); }
                Pulse();
            }
            catch (System.Exception e) { Fail("highlighter tick failed: " + e.Message); }
        }

        /// <summary>True if this transform or any ancestor is one of our spawned clones (ph_prop_/ph_decoy/ph_hl),
        /// so the highlighter never paints a shell on a disguise/decoy/shell (which carry becomable prop meshes).</summary>
        private static bool IsOurClone(Transform t)
        {
            while (t != null)
            {
                var n = t.name;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("ph_")) return true;
                t = t.parent;
            }
            return false;
        }

        private void Rescan()
        {
            var lp = Player.Local;
            if (lp == null) return;
            Vector3 center = lp.transform.position;

            // collect every becomable prop in range, then keep the NEAREST N (stable frame-to-frame -> no flicker)
            _candidates.Clear();
            var hits = Physics.OverlapSphere(center, ScanRadius);
            var seen = new HashSet<int>();
            if (hits != null)
            {
                for (int i = 0; i < hits.Length; i++)
                {
                    var col = hits[i];
                    if (col == null) continue;
                    var mf = col.GetComponentInParent<MeshFilter>();
                    if (mf == null) mf = col.GetComponentInChildren<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    if (mf.GetComponent<MeshRenderer>() == null) continue;
                    if (mf.GetComponentInParent<Player>() != null) continue;    // a disguise riding a player, not a world prop
                    if (IsOurClone(mf.transform)) continue;                     // our own decoy/disguise/shell clones
                    if (PropCatalog.IdForMesh(mf.sharedMesh) < 0) continue;     // not a becomable prop
                    if (!seen.Add(mf.gameObject.GetInstanceID())) continue;
                    _candidates.Add(mf);
                }
            }
            _candidates.Sort((a, b) =>
                (a.transform.position - center).sqrMagnitude.CompareTo((b.transform.position - center).sqrMagnitude));

            _inRange.Clear();
            int n = Mathf.Min(_candidates.Count, MaxHighlights);
            for (int i = 0; i < n; i++)
            {
                var mf = _candidates[i];
                int gid = mf.gameObject.GetInstanceID();
                _inRange.Add(gid);
                if (!_shells.ContainsKey(gid)) AddShell(gid, mf, mf.GetComponent<MeshRenderer>());
            }

            // drop highlights for props no longer in the nearest set
            List<int> stale = null;
            foreach (var kv in _shells)
                if (!_inRange.Contains(kv.Key)) (stale ??= new List<int>()).Add(kv.Key);
            if (stale != null) foreach (var gid in stale) RemoveShell(gid);

            if (_shells.Count != _lastLoggedCount)
            {
                _lastLoggedCount = _shells.Count;
                Core.LogDebug($"[PropHunt] highlighting {_shells.Count} becomable prop(s) nearby ({_candidates.Count} in {ScanRadius}m).");
            }
        }

        private void AddShell(int gid, MeshFilter mf, MeshRenderer srcRend)
        {
            try
            {
                // Preferred: the premium "selectable prop" highlight from the shipped shader bundle - a
                // fresnel rim GLOW + a screen-space-constant outline + a faint through-walls ghost. It does
                // all geometry work in object space, so it can never drift off the pivot the way a scaled
                // halo did, and the outline width is in PIXELS so it stays crisp at any distance (no speckle).
                // _OutlineColor.a drives body-fill alpha (via _CoreAlpha) -> 0.55 gives a visible
                // translucent cyan tint over the prop body even in direct daylight.
                // _RimColor.a drives the silhouette rim alpha -> 0.90 = near-opaque bright rim.
                // rimIntensity: 3.5 means at the grazing edge the rim contribution is 3.5x the rim alpha
                // -> saturates to a clean solid rim. Alpha-blend (not additive) ensures it reads in sun.
                var core = new Color(0.10f, 0.85f, 1f, 0.55f);   // cyan body tint, 55% opaque
                var rim  = new Color(0.50f, 1.00f, 1f, 0.90f);    // bright cyan rim, 90% opaque
                var outline = OutlineShader.TryCreateMaterial(
                    core, widthPixels: 0f, rimColor: rim,
                    rimPower: 2.2f, rimIntensity: 3.5f, pulseSpeed: 2.2f, pulseAmount: 0.25f, fillAlpha: 0.20f);
                if (outline != null) { AddShellGo(gid, mf, outline, true); return; }

                // ponytail: fallback when the bundle is absent/unbuilt - the scale-1.0 glow-tint overlay
                // (no drift, just not a true outline). Zero regression before the bundle is built/shipped.
                Material mat = null;
                var src = srcRend.sharedMaterial;
                if (src != null) { try { mat = new Material(src); } catch { mat = null; } }
                if (mat == null) return;                       // can't make an owned, tintable copy -> skip safely

                // a translucent white GLOW (ignore the prop's own texture) - reads as a highlight, not a solid
                // blob "stuck in" the prop; drawn through walls + on top.
                try { mat.mainTexture = null; } catch { }
                try { mat.SetTexture("_BaseMap", null); } catch { }
                try { mat.SetTexture("_MainTex", null); } catch { }
                var glow = new Color(1f, 1f, 1f, 0.45f);
                mat.color = glow;
                try { mat.SetColor("_BaseColor", glow); } catch { }
                try { mat.EnableKeyword("_EMISSION"); mat.SetColor("_EmissionColor", Color.white); } catch { }
                try
                {
                    mat.SetFloat("_Surface", 1f); mat.SetFloat("_Blend", 0f);   // URP -> transparent
                    mat.SetOverrideTag("RenderType", "Transparent");
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.EnableKeyword("_ALPHABLEND_ON");                          // built-in
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");              // URP
                }
                catch { }
                try { mat.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always); } catch { }   // see-through-walls (if supported)
                mat.renderQueue = 3100;

                AddShellGo(gid, mf, mat, false);
            }
            catch (System.Exception e) { Fail("AddShell failed: " + e.Message); }
        }

        // Builds the render-only child mesh (identity transform = exact 1:1 overlay, scale 1.0 -> no pivot
        // drift) and registers the shell. Shared by the outline and the glow-fallback paths.
        private void AddShellGo(int gid, MeshFilter mf, Material mat, bool outline)
        {
            var go = new GameObject("ph_hl");
            go.transform.SetParent(mf.transform, false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            var cmf = go.AddComponent<MeshFilter>();
            cmf.sharedMesh = mf.sharedMesh;
            var cmr = go.AddComponent<MeshRenderer>();
            cmr.sharedMaterial = mat;
            cmr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            cmr.receiveShadows = false;

            _shells[gid] = new Hl { Go = go, Mat = mat, Outline = outline };
        }

        private void RemoveShell(int gid)
        {
            if (_shells.TryGetValue(gid, out var h))
            {
                if (h.Go != null) { try { UnityEngine.Object.Destroy(h.Go); } catch { } }
                if (h.Mat != null) { try { UnityEngine.Object.Destroy(h.Mat); } catch { } }
            }
            _shells.Remove(gid);
        }

        private void Pulse()
        {
            if (_shells.Count == 0) return;
            // The premium outline shader runs its OWN time-based pulse (fresnel rim + outline), so the
            // outline shells need no per-frame CPU drive - touching _OutlineColor here would only clobber
            // the cyan tint. The pulse remains for the GLOW-FALLBACK material (no internal animation).
            float a = 0.55f + 0.45f * Mathf.PingPong(Time.time * 1.6f, 1f);   // never fully fades -> steady, visible
            var emis = Color.white * a;
            foreach (var h in _shells.Values)
            {
                if (h.Mat == null || h.Outline) continue;
                try { h.Mat.SetColor("_EmissionColor", emis); }
                catch { }
            }
        }

        private void ClearAll()
        {
            var ids = new List<int>(_shells.Keys);
            foreach (var gid in ids) RemoveShell(gid);
            _shells.Clear();
            _inRange.Clear();
        }

        private void Fail(string why)
        {
            _failed = true;
            Core.Log.Warning("[PropHunt] " + why + " - prop highlight disabled (HUD look-at hint still works).");
            try { ClearAll(); } catch { }
        }

        internal void Dispose() { try { ClearAll(); } catch { } }
    }
}
