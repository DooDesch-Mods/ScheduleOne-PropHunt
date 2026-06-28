#if DEBUG
using System.Collections.Generic;
using UnityEngine;
using PropHunt.Disguise;
using Object = UnityEngine.Object;

namespace PropHunt.Debug
{
    /// <summary>
    /// DEBUG-only in-world collision visualizer, shown only while the F3 overlay (<see cref="DebugOverlay"/>) is on.
    /// Renders translucent boxes for: each decoy's catch collider (red), each disguise prop hitbox (cyan), and every
    /// player's CharacterController capsule (green) - so it is obvious why a shot hits or misses (e.g. the visible
    /// prop vs the actual capsule). Pure visuals: the boxes have no colliders and never affect gameplay. Reads only.
    /// </summary>
    internal static class DebugViz
    {
        private static readonly List<GameObject> _pool = new List<GameObject>();
        private static readonly List<BoxCollider> _decoyCols = new List<BoxCollider>();
        private static readonly List<BoxCollider> _propCols = new List<BoxCollider>();
        private static readonly List<CharacterController> _ccs = new List<CharacterController>();
        private static float _refreshAt;
        private static Material _matDecoy, _matProp, _matCap;

        internal static void Tick()
        {
            if (!DebugOverlay.Visible) { HideAll(); return; }
            try
            {
                if (Time.time >= _refreshAt) { _refreshAt = Time.time + 0.4f; RefreshTargets(); }
                int n = 0;
                n = PlaceColliders(_decoyCols, Mat(ref _matDecoy, new Color(1f, 0.3f, 0.2f, 0.32f)), n);
                n = PlaceColliders(_propCols, Mat(ref _matProp, new Color(0.25f, 0.8f, 1f, 0.28f)), n);
                n = PlaceControllers(_ccs, Mat(ref _matCap, new Color(0.4f, 1f, 0.45f, 0.30f)), n);
                for (int i = n; i < _pool.Count; i++) if (_pool[i] != null) _pool[i].SetActive(false);
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] DebugViz failed: " + e.Message); }
        }

        private static void RefreshTargets()
        {
            _decoyCols.Clear(); _propCols.Clear(); _ccs.Clear();
            var boxes = Object.FindObjectsOfType<BoxCollider>();
            if (boxes != null)
                for (int i = 0; i < boxes.Length; i++)
                {
                    var b = boxes[i]; if (b == null) continue;
                    string n = b.gameObject.name;
                    if (string.IsNullOrEmpty(n)) continue;
                    if (n.StartsWith("ph_decoy_")) _decoyCols.Add(b);
                    else if (n.StartsWith("ph_prop_")) _propCols.Add(b);
                }
            var ccs = Object.FindObjectsOfType<CharacterController>();
            if (ccs != null) for (int i = 0; i < ccs.Length; i++) if (ccs[i] != null) _ccs.Add(ccs[i]);
        }

        private static int PlaceColliders(List<BoxCollider> cols, Material mat, int idx)
        {
            for (int i = 0; i < cols.Count; i++)
            {
                var c = cols[i]; if (c == null) continue;
                var tr = c.transform;
                Vector3 center = tr.TransformPoint(c.center);
                Vector3 size = Vector3.Scale(c.size, AbsScale(tr.lossyScale));
                Place(idx++, mat, center, tr.rotation, size);
            }
            return idx;
        }

        private static int PlaceControllers(List<CharacterController> ccs, Material mat, int idx)
        {
            for (int i = 0; i < ccs.Count; i++)
            {
                var c = ccs[i]; if (c == null) continue;
                var tr = c.transform;
                Vector3 center = tr.TransformPoint(c.center);
                var s = AbsScale(tr.lossyScale);
                Vector3 size = new Vector3(c.radius * 2f * s.x, c.height * s.y, c.radius * 2f * s.z);
                Place(idx++, mat, center, tr.rotation, size);
            }
            return idx;
        }

        private static void Place(int idx, Material mat, Vector3 center, Quaternion rot, Vector3 size)
        {
            var go = Get(idx);
            go.SetActive(true);
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null && mr.sharedMaterial != mat) mr.sharedMaterial = mat;
            go.transform.position = center;
            go.transform.rotation = rot;
            go.transform.localScale = size;   // unit cube -> world size
        }

        private static GameObject Get(int idx)
        {
            while (idx >= _pool.Count)
            {
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "ph_viz";
                var col = cube.GetComponent<Collider>();   // never let a viz box affect physics / rays
                if (col != null) { col.enabled = false; Object.Destroy(col); }
                var mr = cube.GetComponent<MeshRenderer>();
                if (mr != null)
                {
                    // no shadow (the cast shadow was the only thing visible before) and don't receive shadows
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }
                Object.DontDestroyOnLoad(cube);
                _pool.Add(cube);
            }
            return _pool[idx];
        }

        private static Vector3 AbsScale(Vector3 s) => new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z));

        private static void HideAll()
        {
            for (int i = 0; i < _pool.Count; i++) if (_pool[i] != null) _pool[i].SetActive(false);
        }

        /// <summary>Lazily build a viz material in the given colour. Uses the bundled outline shader (proven to
        /// render in this URP game, ZTest Always so boxes show through geometry); falls back to an opaque tinted
        /// material if the shader bundle is missing. A runtime URP-Lit transparent switch was unreliable (the
        /// transparent variant gets stripped -> the box cast a shadow but never drew).</summary>
        private static Material Mat(ref Material slot, Color c)
        {
            if (slot != null) return slot;
            Material m = null;
            try
            {
                m = OutlineShader.TryCreateMaterial(c, 1f, c, rimPower: 2.2f, rimIntensity: 2.2f,
                                                    pulseSpeed: 0f, pulseAmount: 0f, fillAlpha: 0.22f);
                if (m == null) m = OpaqueFallback(c);   // shader bundle absent
                if (m != null) Object.DontDestroyOnLoad(m);
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] viz material failed: " + e.Message); }
            slot = m;
            return slot;
        }

        private static Material OpaqueFallback(Color c)
        {
            try
            {
                var tmp = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var src = tmp.GetComponent<MeshRenderer>().sharedMaterial;
                var m = new Material(src);
                Object.Destroy(tmp);
                m.SetColor("_BaseColor", c);
                m.SetColor("_Color", c);
                return m;
            }
            catch { return null; }
        }
    }
}
#endif
