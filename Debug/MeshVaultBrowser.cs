#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PropHunt.Debug
{
    /// <summary>
    /// DEBUG-only browser for the MeshVault mod's prop database (toggle: phmesh console command). Iterates every
    /// MeshVault mesh id and spawns it as a rotating turntable preview in front of the camera (like phcurate), so
    /// the dev can judge whether MeshVault's props are higher quality / useful for PropHunt.
    ///
    /// MeshVault is a separate IL2CPP mod; we reach it purely by REFLECTION (no build dependency), so this no-ops
    /// gracefully with a clear log line if MeshVault is not installed/loaded. API (from S1-MeshVault docs):
    ///   MeshVault.MeshVaultAPI.ListMeshes() -> string[]
    ///   MeshVault.MeshVaultAPI.Spawn(string id, Vector3 pos, Quaternion rot, Transform parent=null,
    ///                                string namePrefix="MeshVault", string[] matOverrides=null, Color?[] colOverrides=null) -> GameObject
    ///
    /// Keys while active: [Left]/[Right] prev/next, [PgUp]/[PgDn] +-10. phmesh = exit.
    /// </summary>
    internal static class MeshVaultBrowser
    {
        private static bool _active;
        private static float _lastToggle = -999f;
        private static List<string> _ids;
        private static int _index;
        private static GameObject _anchor;   // spun turntable parent in front of the camera
        private static GameObject _current;  // the spawned MeshVault prop
        private static float _spin;
        private static GUIStyle _style;

        private static Type _apiType;
        private static MethodInfo _spawn;
        private static MethodInfo _listMeshes;

        private const float PreviewDistance = 2.4f;
        private const float SpinSpeed = 35f;

        internal static bool Active => _active;

        internal static void Toggle()
        {
            float now = Time.time;
            if (now - _lastToggle < 0.4f) return;   // SubmitCommand fires twice
            _lastToggle = now;
            if (_active) { Exit(); return; }

            if (!ResolveApi())
            {
                Core.Log.Warning("[PropHunt] phmesh: MeshVault not found. Install the REAL MeshVault.Il2Cpp.dll " +
                                 "(github.com/hdlmrell/S1-MeshVault releases) into Mods - the file currently there is an XML config, not the DLL.");
                return;
            }
            if (!LoadIds() || _ids.Count == 0) { Core.Log.Warning("[PropHunt] phmesh: MeshVault returned no mesh ids (DB not built? load a save first)."); return; }

            _index = 0;
            _active = true;
            BuildAnchor();
            SpawnCurrent();
            Core.Log.Msg($"[PropHunt] MeshVault browser ON: {_ids.Count} meshes.  [<- / ->] prev/next  [PgUp/PgDn] +-10  (phmesh = exit)");
        }

        private static void Exit()
        {
            _active = false;
            if (_current != null) { try { Object.Destroy(_current); } catch { } _current = null; }
            if (_anchor != null) { try { Object.Destroy(_anchor); } catch { } _anchor = null; }
        }

        // ---- reflection into MeshVault ----
        private static bool ResolveApi()
        {
            if (_apiType != null && _spawn != null) return true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var nm = asm.GetName().Name;
                    if (nm == null || nm.IndexOf("MeshVault", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    Type t = asm.GetType("MeshVault.MeshVaultAPI") ?? FindTypeByName(asm, "MeshVaultAPI");
                    if (t == null) continue;
                    var spawn = FindMethod(t, "Spawn");
                    if (spawn == null) continue;
                    _apiType = t;
                    _spawn = spawn;
                    _listMeshes = FindMethod(t, "ListMeshes");
                    Core.LogDebug($"[PropHunt] phmesh: resolved {t.FullName} in {nm}");
                    return true;
                }
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] phmesh resolve failed: " + e.Message); }
            return false;
        }

        private static Type FindTypeByName(Assembly asm, string shortName)
        {
            try { foreach (var t in asm.GetTypes()) if (t.Name == shortName) return t; } catch { }
            return null;
        }

        private static MethodInfo FindMethod(Type t, string name)
        {
            try
            {
                MethodInfo best = null;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (m.Name == name && (best == null || m.GetParameters().Length > best.GetParameters().Length)) best = m;
                return best;
            }
            catch { return null; }
        }

        private static bool LoadIds()
        {
            _ids = new List<string>();
            try
            {
                if (_listMeshes == null) return false;
                object res = _listMeshes.Invoke(null, null);
                if (res is IEnumerable en)
                    foreach (var o in en) { if (o != null) _ids.Add(o.ToString()); }
                _ids.Sort(StringComparer.OrdinalIgnoreCase);
                return _ids.Count > 0;
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] phmesh ListMeshes failed: " + e.Message); return false; }
        }

        // ---- preview ----
        private static void BuildAnchor()
        {
            if (_anchor != null) return;
            _anchor = new GameObject("ph_meshvault_anchor");
            Object.DontDestroyOnLoad(_anchor);
        }

        private static string Current => (_ids != null && _index >= 0 && _index < _ids.Count) ? _ids[_index] : null;

        private static void SpawnCurrent()
        {
            if (_current != null) { try { Object.Destroy(_current); } catch { } _current = null; }
            string id = Current;
            if (id == null || _anchor == null || _spawn == null) return;
            try
            {
                var ps = _spawn.GetParameters();
                var args = new object[ps.Length];
                args[0] = id;
                if (ps.Length > 1) args[1] = Vector3.zero;
                if (ps.Length > 2) args[2] = Quaternion.identity;
                for (int k = 3; k < ps.Length; k++) args[k] = ps[k].HasDefaultValue ? ps[k].DefaultValue : null;

                var go = _spawn.Invoke(null, args) as GameObject;
                if (go == null) { Core.LogDebug($"[PropHunt] phmesh: Spawn returned null for '{id}'"); return; }

                // strip colliders/rigidbodies so the preview is inert
                try { foreach (var c in go.GetComponentsInChildren<Collider>(true)) if (c != null) Object.Destroy(c); } catch { }
                try { foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) if (rb != null) Object.Destroy(rb); } catch { }

                go.transform.SetParent(_anchor.transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                // normalise to ~1.4m, then recentre on the anchor using WORLD positions. The old code used the
                // world-space bounds centre directly as a LOCAL offset (-b.center*fit); that only worked for the
                // first spawn (anchor still at the origin). Once UpdateAnchor moves the anchor in front of the
                // camera, a world centre of e.g. (120,5,340) used as a local offset flings the prop far off-screen
                // - hence "first visible, the rest invisible".
                if (TryBounds(go, out var b))
                {
                    float size = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                    float fit = size > 0.001f ? 1.4f / size : 1f;
                    go.transform.localScale = Vector3.one * fit;
                }
                if (TryBounds(go, out var b2))
                    go.transform.position += _anchor.transform.position - b2.center;
                _current = go;
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] phmesh spawn failed: " + e.Message); }
        }

        private static bool TryBounds(GameObject go, out Bounds b)
        {
            b = default;
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends == null || rends.Length == 0) return false;
            bool any = false;
            for (int i = 0; i < rends.Length; i++)
            {
                if (rends[i] == null) continue;
                if (!any) { b = rends[i].bounds; any = true; } else b.Encapsulate(rends[i].bounds);
            }
            // convert world bounds to local-ish centre (anchor at origin); good enough for centring a fresh spawn
            return any;
        }

        private static void Move(int d)
        {
            if (_ids == null || _ids.Count == 0) return;
            int n = _ids.Count;
            _index = ((_index + d) % n + n) % n;
            SpawnCurrent();
        }

        internal static void Tick()
        {
            if (!_active) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.RightArrow)) Move(1);
                else if (Input.GetKeyDown(KeyCode.LeftArrow)) Move(-1);
                else if (Input.GetKeyDown(KeyCode.PageDown)) Move(10);
                else if (Input.GetKeyDown(KeyCode.PageUp)) Move(-10);
                UpdateAnchor();
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] phmesh tick failed: " + e.Message); }
        }

        private static void UpdateAnchor()
        {
            if (_anchor == null) return;
            var cam = Camera.main;
            if (cam == null) cam = Object.FindObjectOfType<Camera>();
            if (cam == null) return;
            var t = cam.transform;
            _anchor.transform.position = t.position + t.forward * PreviewDistance - t.up * 0.1f;
            _spin += Time.deltaTime * SpinSpeed;
            _anchor.transform.rotation = Quaternion.Euler(0f, _spin, 0f);
        }

        internal static void DrawGui()
        {
            if (!_active || _ids == null) return;
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 16, fontStyle = FontStyle.Bold };
            const float w = 660f, h = 70f;
            var box = new Rect((Screen.width - w) / 2f, 12f, w, h);
            GUI.Box(box, "PropHunt - MeshVault Browser");
            _style.normal.textColor = Color.green;
            GUI.Label(new Rect(box.x, box.y + 26f, w, 22f), $"[{_index + 1}/{_ids.Count}]  {Current}", _style);
            _style.normal.textColor = Color.cyan;
            var small = new GUIStyle(_style) { fontSize = 12 };
            GUI.Label(new Rect(box.x, box.y + 48f, w, 18f), "[<- / ->] prev/next    [PgUp/PgDn] +-10    (phmesh = exit)", small);
        }
    }
}
#endif
