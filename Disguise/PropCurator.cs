#if DEBUG
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Object = UnityEngine.Object;   // disambiguate Object.Destroy/FindObjectOfType from System.Object

namespace PropHunt.Disguise
{
    /// <summary>
    /// DEBUG-only in-game curation tool (toggled by the <c>phcurate</c> console command). Steps through EVERY
    /// reviewable mesh in the loaded world one at a time, showing it as a rotating, lit preview floating in front
    /// of the camera, and lets the user mark each Keep / Skip. Locks the world to daytime so props are clearly
    /// lit, and surfaces per-prop info + material warnings (untextured / transparent / hollow-back / submesh gap)
    /// so broken-looking props are easy to spot. Decisions persist to UserData/PropHunt/prop_curation.txt which
    /// <see cref="PropCatalog"/> then uses as a becomable-prop allowlist. No PropHunt session required.
    ///
    /// Keys (while active): [Y] keep, [N] skip (both auto-advance), Left/Right prev/next, PgUp/PgDn jump 10.
    /// </summary>
    internal static class PropCurator
    {
        private static bool _active;
        private static List<PropEntry> _candidates;
        private static int _index;
        private static GameObject _anchor;     // positioned in front of the camera, spun for a turntable view
        private static GameObject _meshGo;      // child holding the previewed mesh, centred on the anchor
        private static GameObject _lightGo;     // a fill light following the camera so the prop is always lit
        private static Material _mat;           // owned copy of the source material (never touch the original)
        private static float _spin;
        private static float _lastToggle = -999f;
        private static bool _warnedNoCam;

        // Captured Property content-culling state, restored on phcurate exit. While curating we un-cull every
        // property interior so the distance-deactivated props (the bulk of the world's furniture/decor, hidden by
        // SetContentCulled at >50m) are active + renderer-enabled and thus reviewable AND previewable from a single
        // spot. The cull path is purely local (SetActive / MeshRenderer.enabled), so this is MP-safe per client.
        private struct PropCullState { internal Il2CppScheduleOne.Property.Property Prop; internal bool WasEnabled; internal bool WasCulled; }
        private static readonly List<PropCullState> _cullState = new List<PropCullState>();

        // cached per-prop info (recomputed on index change, displayed by the HUD)
        private static string _infoObj = "";
        private static string _infoStats = "";
        private static string _infoFlags = "";
        private static bool _infoFlagsBad;

        private const float PreviewDistance = 2.2f;
        private const float FitSize = 1.4f;     // normalise every prop to ~this many metres so all are viewable
        private const float SpinSpeed = 35f;    // deg/sec turntable

        internal static bool Active => _active;

        private enum CurateFilter { All, Unreviewed, Kept }

        /// <summary>phcurate: review EVERY candidate mesh.</summary>
        internal static void Toggle() => Enter(CurateFilter.All);

        /// <summary>phcurateu: review ONLY the still-UNREVIEWED candidates (no keep/skip decision yet) - so you can
        /// finish off newly-appeared props without scrolling past the hundreds already decided.</summary>
        internal static void ToggleUnreviewed() => Enter(CurateFilter.Unreviewed);

        /// <summary>phcuratekeep: re-review ONLY the currently-KEPT candidates (the ones in the becomable pool) - to
        /// weed out auto-seeded keeps you never verified. Press [N] on a bad one to drop it from the pool.</summary>
        internal static void ToggleKept() => Enter(CurateFilter.Kept);

        private static void Enter(CurateFilter filter)
        {
            // The game's Console.SubmitCommand fires the command TWICE per entry; without this guard a single
            // "phcurate" would toggle on then immediately off again. Ignore a 2nd toggle within a short window.
            float now = Time.time;
            if (now - _lastToggle < 0.4f) return;
            _lastToggle = now;

            if (_active) { Exit(); return; }
            try
            {
                UncullAllProperties();   // force every distance-culled interior active so its props can be reviewed
                PropCatalog.LoadCuration();
                var all = PropCatalog.EnumerateAllCandidates();
                if (filter == CurateFilter.All) _candidates = all;
                else
                {
                    var filtered = new List<PropEntry>();
                    for (int i = 0; i < all.Count; i++)
                    {
                        bool match = filter == CurateFilter.Unreviewed ? IsUnreviewed(all[i]) : IsKept(all[i]);
                        if (match) filtered.Add(all[i]);
                    }
                    _candidates = filtered;
                }

                if (_candidates == null || _candidates.Count == 0)
                {
                    string why = filter == CurateFilter.Unreviewed ? "no UNREVIEWED candidates - everything in this world is already reviewed."
                               : filter == CurateFilter.Kept       ? "no KEPT candidates in this world yet (nothing to re-review)."
                               :                                      "no candidate meshes in this world (load a world first).";
                    Core.Log.Warning("[PropHunt] phcurate: " + why);
                    return;
                }
                _index = 0;
                BuildPreview();
                Game.RoundEnvironment.LockTimeOfDay(1200);   // bright daylight so props are clearly visible
                _active = true;
                string tag = filter == CurateFilter.Unreviewed ? " (UNREVIEWED only)" : filter == CurateFilter.Kept ? " (KEPT only)" : "";
                Core.Log.Msg($"[PropHunt] prop curation ON{tag}: {_candidates.Count} meshes. " +
                             "[Y]=keep  [N]=skip  Left/Right=prev/next  PgUp/PgDn=+-10  phcurate=save+exit.");
                ShowCurrent();
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phcurate enter failed: " + e.Message); Exit(); }
        }

        /// <summary>True when a candidate is currently KEPT (becomable). For a LOD prop, kept = ANY of its LOD meshes
        /// is kept (that is what puts it in the pool).</summary>
        private static bool IsKept(PropEntry e)
        {
            if (e == null) return false;
            if (e.SourceLodGroup != null)
            {
                var keys = PropCatalog.LodMeshKeys(e.SourceLodGroup);
                if (keys.Count == 0) return PropCatalog.DecisionOf(e.Key) == true;
                for (int i = 0; i < keys.Count; i++) if (PropCatalog.DecisionOf(keys[i]) == true) return true;
                return false;
            }
            return PropCatalog.DecisionOf(e.Key) == true;
        }

        /// <summary>True when a candidate has no keep/skip decision yet. For a LOD prop, unreviewed = NONE of its
        /// LOD meshes has a decision (any decided LOD key means the prop was already reviewed).</summary>
        private static bool IsUnreviewed(PropEntry e)
        {
            if (e == null) return false;
            if (e.SourceLodGroup != null)
            {
                var keys = PropCatalog.LodMeshKeys(e.SourceLodGroup);
                if (keys.Count == 0) return PropCatalog.DecisionOf(e.Key) == null;
                for (int i = 0; i < keys.Count; i++) if (PropCatalog.DecisionOf(keys[i]) != null) return false;
                return true;
            }
            return PropCatalog.DecisionOf(e.Key) == null;
        }

        private static void Exit()
        {
            bool was = _active;
            _active = false;
            try { CuratePreview.Clear(); } catch { }   // stop the on-player preview on all clients
            try { PropCatalog.SaveCuration(); } catch { }
            try { RestorePropertyCulling(); } catch { }   // resume normal distance culling of interiors
            try { Game.RoundEnvironment.RestoreTimeProgression(); } catch { }
            if (_meshGo != null) { try { Object.Destroy(_meshGo); } catch { } }
            if (_lightGo != null) { try { Object.Destroy(_lightGo); } catch { } }
            if (_anchor != null) { try { Object.Destroy(_anchor); } catch { } }
            if (_mat != null) { try { Object.Destroy(_mat); } catch { } }
            _meshGo = _anchor = _lightGo = null; _mat = null; _candidates = null;
            if (was) Core.Log.Msg($"[PropHunt] prop curation OFF: saved ({PropCatalog.KeepCount()} kept). Rebuild the catalog (new round) to apply.");
        }

        internal static void Tick()
        {
            if (!_active) return;
            try { HandleKeys(); UpdatePreview(); }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phcurate tick failed: " + e.Message); Exit(); }
        }

        /// <summary>Un-cull every <see cref="Il2CppScheduleOne.Property.Property"/> so its distance-deactivated
        /// interior props become active + renderer-enabled and thus reviewable/previewable from one spot. The
        /// previous per-property state is captured for <see cref="RestorePropertyCulling"/>. Local-only (SetActive /
        /// MeshRenderer.enabled - the same op the game runs when you walk up to a property), so MP-safe per client.</summary>
        private static void UncullAllProperties()
        {
            _cullState.Clear();
            try
            {
                var props = Resources.FindObjectsOfTypeAll<Il2CppScheduleOne.Property.Property>();
                if (props == null) return;
                int n = 0;
                for (int i = 0; i < props.Length; i++)
                {
                    var p = props[i];
                    if (p == null) continue;
                    try
                    {
                        _cullState.Add(new PropCullState { Prop = p, WasEnabled = p.ContentCullingEnabled, WasCulled = p.IsContentCulled });
                        p.ContentCullingEnabled = false;   // stop it re-culling itself while we move around
                        p.SetContentCulled(false);         // activate the interior NOW
                        n++;
                    }
                    catch { }
                }
                Core.LogDebug($"[PropHunt] phcurate: un-culled {n} properties (interiors forced active for review).");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phcurate un-cull failed: " + e.Message); }
        }

        /// <summary>Restore each property to the content-culling state captured by <see cref="UncullAllProperties"/>,
        /// so normal distance culling resumes when the curator exits.</summary>
        private static void RestorePropertyCulling()
        {
            try
            {
                for (int i = 0; i < _cullState.Count; i++)
                {
                    var s = _cullState[i];
                    if (s.Prop == null) continue;
                    try { s.Prop.ContentCullingEnabled = s.WasEnabled; s.Prop.SetContentCulled(s.WasCulled); } catch { }
                }
            }
            catch { }
            _cullState.Clear();
        }

        private static void HandleKeys()
        {
            if (Input.GetKeyDown(KeyCode.Y)) { Decide(true); return; }
            if (Input.GetKeyDown(KeyCode.N)) { Decide(false); return; }
            if (Input.GetKeyDown(KeyCode.RightArrow)) { Move(1); return; }
            if (Input.GetKeyDown(KeyCode.LeftArrow)) { Move(-1); return; }
            if (Input.GetKeyDown(KeyCode.PageDown)) { Move(10); return; }
            if (Input.GetKeyDown(KeyCode.PageUp)) { Move(-10); return; }
        }

        private static void Decide(bool keep)
        {
            var e = Current; if (e == null) return;
            // For a LOD prop, record the decision for EVERY LOD MeshKey so the picker resolves whichever LOD
            // the world prop is showing (at any distance) to the same approved prop.
            if (e.SourceLodGroup != null)
            {
                var keys = PropCatalog.LodMeshKeys(e.SourceLodGroup);
                if (keys.Count == 0) PropCatalog.SetDecision(e.Key, keep);
                else for (int i = 0; i < keys.Count; i++) PropCatalog.SetDecision(keys[i], keep);
            }
            else PropCatalog.SetDecision(e.Key, keep);   // persists immediately
            Move(1);                                      // auto-advance for fast clicking
        }

        private static void Move(int delta)
        {
            if (_candidates == null || _candidates.Count == 0) return;
            int n = _candidates.Count;
            _index = ((_index + delta) % n + n) % n;
            RefreshPreviewMesh();
            ShowCurrent();
        }

        private static PropEntry Current =>
            (_candidates != null && _index >= 0 && _index < _candidates.Count) ? _candidates[_index] : null;

        private static void BuildPreview()
        {
            _anchor = new GameObject("ph_curate_anchor");
            Object.DontDestroyOnLoad(_anchor);

            // a fill light (kept off the spinning anchor) so the camera-facing side of every prop is lit
            _lightGo = new GameObject("ph_curate_light");
            Object.DontDestroyOnLoad(_lightGo);
            var light = _lightGo.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = 18f;
            light.intensity = 2.2f;
            light.color = Color.white;

            RefreshPreviewMesh();
        }

        private static void RefreshPreviewMesh()
        {
            var e = Current; if (e == null || _anchor == null) { AnalyzeCurrent(); return; }
            if (_meshGo != null) { try { Object.Destroy(_meshGo); } catch { } _meshGo = null; }

            // Build the REAL prop clone: full LODGroup hierarchy when present (so the preview shows actual LODs),
            // else a single mesh - identical to the in-game disguise.
            _meshGo = PropClone.Build(e, "ph_curate_mesh");
            if (_meshGo == null) { AnalyzeCurrent(); return; }
            _meshGo.transform.SetParent(_anchor.transform, false);

            // normalise to ~FitSize and centre on the spinning anchor
            float size; Vector3 center;
            if (e.SourceLodGroup != null) { size = e.SourceLodGroup.size; center = e.SourceLodGroup.localReferencePoint; }
            else
            {
                var b = (e.Source != null && e.Source.sharedMesh != null) ? e.Source.sharedMesh.bounds : new Bounds();
                size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                center = b.center;
            }
            float fit = size > 0.001f ? (FitSize / size) : 1f;
            _meshGo.transform.localScale = Vector3.one * fit;
            _meshGo.transform.localPosition = -center * fit;
            _meshGo.transform.localRotation = Quaternion.identity;

            AnalyzeCurrent();
        }

        private static void UpdatePreview()
        {
            if (_anchor == null) return;
            var cam = Camera.main;
            if (cam == null) cam = Object.FindObjectOfType<Camera>();
            if (cam == null)
            {
                if (!_warnedNoCam) { _warnedNoCam = true; Core.Log.Warning("[PropHunt] phcurate: no camera found - preview cannot be placed."); }
                return;
            }
            var t = cam.transform;
            _anchor.transform.position = t.position + t.forward * PreviewDistance - t.up * 0.1f;
            _spin += Time.deltaTime * SpinSpeed;
            _anchor.transform.rotation = Quaternion.Euler(0f, _spin, 0f);
            if (_lightGo != null) _lightGo.transform.position = t.position + t.up * 0.6f;   // light the front from the viewer side
        }

        /// <summary>Compute the cached per-prop info + material warnings shown by the HUD. Detects sides that
        /// would look broken on a disguise: missing textures, transparent (see-through) materials, single-sided
        /// (hollow from behind) meshes, and submesh/material mismatches (unrendered gaps).</summary>
        private static void AnalyzeCurrent()
        {
            _infoObj = _infoStats = _infoFlags = ""; _infoFlagsBad = false;
            var e = Current; if (e == null) return;
            var mesh = e.Source != null ? e.Source.sharedMesh : null;
            var rend = e.SourceRenderer;
            string objName = (e.Source != null && e.Source.gameObject != null) ? e.Source.gameObject.name : "?";
            int verts = mesh != null ? mesh.vertexCount : 0;
            int subs = mesh != null ? mesh.subMeshCount : 0;
            Vector3 ws = rend != null ? rend.bounds.size : Vector3.zero;

            int mats = 0, texCount = 0;
            bool transp = false, hollow = false, gap = false;
            if (rend != null)
            {
                var sm = rend.sharedMaterials;
                mats = sm != null ? sm.Length : 0;
                for (int i = 0; i < mats; i++)
                {
                    var m = sm[i];
                    if (m == null) { gap = true; continue; }
                    if (MaterialHasTexture(m)) texCount++;
                    try { if (m.renderQueue >= 3000) transp = true; } catch { }
                    try { if (m.HasProperty("_Cull") && (int)m.GetFloat("_Cull") == 2) hollow = true; } catch { }
                }
                if (subs > mats) gap = true;   // submeshes without a material render as gaps
            }

            _infoObj = $"obj '{objName}'    mesh '{e.Name}'";
            _infoStats = $"{ws.x:F2} x {ws.y:F2} x {ws.z:F2} m    verts {verts}    submesh {subs}    mat {mats}";

            var f = new StringBuilder();
            if (mats > 0 && texCount == 0) { f.Append("NO TEXTURE  "); _infoFlagsBad = true; }
            else if (texCount < mats) { f.Append($"some untextured ({texCount}/{mats})  "); _infoFlagsBad = true; }
            else f.Append("textured  ");
            if (transp) { f.Append("TRANSPARENT  "); _infoFlagsBad = true; }
            if (hollow) { f.Append("hollow-back(1-sided)  "); }
            if (gap) { f.Append("SUBMESH GAP  "); _infoFlagsBad = true; }
            _infoFlags = f.ToString().Trim();
        }

        /// <summary>True if the material binds a texture to ANY of its shader's texture slots. Enumerates the
        /// shader's real texture properties (robust to custom Schedule I shaders that don't use _BaseMap/_MainTex),
        /// with a mainTexture fast-path and a common-name fallback. Everything try/caught for IL2CPP safety.</summary>
        private static bool MaterialHasTexture(Material m)
        {
            if (m == null) return false;
            try { if (m.mainTexture != null) return true; } catch { }
            try
            {
                var sh = m.shader;
                if (sh != null)
                {
                    int count = sh.GetPropertyCount();
                    for (int i = 0; i < count; i++)
                    {
                        if (sh.GetPropertyType(i) == UnityEngine.Rendering.ShaderPropertyType.Texture)
                        {
                            string pn = sh.GetPropertyName(i);
                            if (!string.IsNullOrEmpty(pn))
                                try { if (m.GetTexture(pn) != null) return true; } catch { }
                        }
                    }
                }
            }
            catch { }
            string[] common = { "_BaseMap", "_MainTex", "_BaseColorMap", "_Albedo", "_AlbedoMap", "_DiffuseMap", "_Diffuse" };
            for (int i = 0; i < common.Length; i++)
                try { if (m.GetTexture(common[i]) != null) return true; } catch { }
            return false;
        }

        private static void ShowCurrent()
        {
            var e = Current; if (e == null) return;
            Core.LogDebug($"[PropHunt] curate [{_index + 1}/{_candidates.Count}] '{e.Name}' ({e.Key}) -> {DecisionText(e.Key)}  ({_infoFlags})");
            try { CuratePreview.Set(e.Key); } catch { }   // live on-player preview on the OTHER clients
        }

        private static string DecisionText(string key)
        {
            var d = PropCatalog.DecisionOf(key);
            return d == true ? "KEEP" : d == false ? "SKIP" : "unreviewed";
        }

        internal static void DrawGui()
        {
            if (!_active) return;
            var e = Current; if (e == null) return;
            string dec = DecisionText(e.Key);
            Color decCol = dec == "KEEP" ? Color.green : dec == "SKIP" ? new Color(1f, 0.45f, 0.45f) : Color.gray;

            const float w = 640f, h = 158f;
            var box = new Rect((Screen.width - w) / 2f, 12f, w, h);
            GUI.Box(box, "PropHunt - Prop Curation");
            Line(box, 24f, $"[{_index + 1}/{_candidates.Count}]    {dec}    (kept: {PropCatalog.KeepCount()})", 18, decCol);
            Line(box, 50f, _infoObj, 15, Color.white);
            Line(box, 72f, _infoStats, 14, new Color(0.8f, 0.9f, 1f));
            Line(box, 94f, _infoFlags, 14, _infoFlagsBad ? new Color(1f, 0.55f, 0.3f) : new Color(0.6f, 1f, 0.6f));
            Line(box, 122f, "[Y] keep    [N] skip    [<- / ->] prev/next    [PgUp/PgDn] +-10    (phcurate = save+exit)", 13, Color.cyan);
        }

        private static void Line(Rect box, float dy, string text, int size, Color col)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = size,
                fontStyle = FontStyle.Bold
            };
            style.normal.textColor = col;
            GUI.Label(new Rect(box.x, box.y + dy, box.width, size + 8), text, style);
        }
    }
}
#endif
