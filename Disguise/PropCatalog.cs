using System.Collections.Generic;
using UnityEngine;

namespace PropHunt.Disguise
{
    /// <summary>One disguise prop: the source mesh + materials to clone onto a hider, plus a stable id.</summary>
    internal sealed class PropEntry
    {
        internal int Id;
        internal string Name;
        internal MeshFilter Source;
        internal MeshRenderer SourceRenderer;
    }

    /// <summary>
    /// Deterministic catalog of hide-able props. Scans the loaded world for hide-able-sized meshes and dedupes
    /// by mesh name. Each prop's id is a <b>stable hash of its mesh name</b> (NOT a positional index), so the
    /// same prop resolves to the same id on every client even if their catalogs differ slightly (NPCs/dropped
    /// items present at scan time vary between instances) - a single missing/extra mesh no longer shifts every
    /// id. The <see cref="Hash"/> over the sorted names is still exchanged as a soft handshake. Must be (re)built
    /// once the world is fully loaded; falls back to primitives if the scan yields nothing.
    /// </summary>
    internal static class PropCatalog
    {
        // props that look broken as a disguise (wrong pivot, missing submeshes, or not a sensible prop)
        private static readonly string[] _badNames = { "ReverseLight", "Case" };
        private static readonly List<PropEntry> _entries = new List<PropEntry>();
        private static readonly Dictionary<int, PropEntry> _byId = new Dictionary<int, PropEntry>();
        private static int _hash;

        internal static int Hash => _hash;
        internal static int Count => _entries.Count;
        internal static PropEntry ById(int id) => _byId.TryGetValue(id, out var e) ? e : null;

        internal static void BuildIfNeeded()
        {
            if (_entries.Count == 0) Build();
        }

        internal static void Build()
        {
            _entries.Clear();
            _byId.Clear();
            try
            {
                var seen = new HashSet<string>();
                var tmp = new List<PropEntry>();
                var filters = UnityEngine.Object.FindObjectsOfType<MeshFilter>();
                if (filters != null)
                {
                    for (int i = 0; i < filters.Length; i++)
                    {
                        var mf = filters[i];
                        if (mf == null) continue;
                        var mesh = mf.sharedMesh;
                        if (mesh == null) continue;
                        string name = mesh.name;
                        if (!IsUsablePropName(name)) continue;
                        if (mesh.vertexCount < 8) continue;          // skip degenerate/placeholder meshes
                        if (!seen.Add(name)) continue;
                        var rend = mf.GetComponent<MeshRenderer>();
                        if (rend == null || !rend.enabled) continue;
                        if (rend.sharedMaterial == null) continue;   // no material -> would render invisible
                        Vector3 b = rend.bounds.size;
                        float maxd = Mathf.Max(b.x, Mathf.Max(b.y, b.z));
                        float mind = Mathf.Min(b.x, Mathf.Min(b.y, b.z));
                        if (maxd < 0.3f || maxd > 4f) continue;     // only hide-able-sized props
                        if (mind < 0.08f) continue;                  // skip flat planes/decals (e.g. a 0-height "Cover")
                        tmp.Add(new PropEntry { Name = name, Source = mf, SourceRenderer = rend });
                    }
                }
                tmp.Sort((a, c) => string.CompareOrdinal(a.Name, c.Name));   // deterministic collision-resolution order
                for (int i = 0; i < tmp.Count; i++)
                {
                    int id = StableHash(tmp[i].Name);
                    if (_byId.ContainsKey(id)) continue;        // hash collision (rare) - keep the first by sort order (deterministic)
                    tmp[i].Id = id;
                    _byId[id] = tmp[i];
                    _entries.Add(tmp[i]);
                }
                if (_entries.Count == 0) AddPrimitiveFallback();
            }
            catch (System.Exception e)
            {
                Core.Log.Warning("[PropHunt] catalog build failed: " + e.Message);
                if (_entries.Count == 0) AddPrimitiveFallback();
            }
            ComputeHash();
            Core.Log.Msg($"[PropHunt] prop catalog: {_entries.Count} props (hash {_hash}).");
        }

        /// <summary>Reject meshes that make absurd disguises: our own clones, Unity static-batch chunks
        /// ("Combined Mesh (root: scene) N" = whole-scene fragments) and collision-only meshes ("*Collider*").</summary>
        private static bool IsUsablePropName(string n)
        {
            if (string.IsNullOrEmpty(n) || n.StartsWith("ph_")) return false;
            if (n.IndexOf("Combined Mesh", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (n.IndexOf("Collider", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            // specific buggy props (wrong pivot / missing submeshes / not a real prop)
            for (int i = 0; i < _badNames.Length; i++)
                if (n.IndexOf(_badNames[i], System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return true;
        }

        private static void AddPrimitiveFallback()
        {
            AddPrimitive(PrimitiveType.Cube, "ph_cube");
            AddPrimitive(PrimitiveType.Cylinder, "ph_cylinder");
            AddPrimitive(PrimitiveType.Sphere, "ph_sphere");
        }

        private static void AddPrimitive(PrimitiveType t, string name)
        {
            try
            {
                var go = GameObject.CreatePrimitive(t);
                var mf = go.GetComponent<MeshFilter>();
                var mr = go.GetComponent<MeshRenderer>();
                go.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(go);     // keep the mesh/material refs alive for the session
                int id = StableHash(name);
                var e = new PropEntry { Id = id, Name = name, Source = mf, SourceRenderer = mr };
                _byId[id] = e;
                _entries.Add(e);
            }
            catch { }
        }

        /// <summary>FNV-1a 32-bit over the chars - identical on every machine (unlike string.GetHashCode, which
        /// is process-randomised on .NET Core), so prop ids line up across clients.</summary>
        private static int StableHash(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return (int)(h & 0x7FFFFFFFu);   // always non-negative: keeps -1 a clean "no prop" sentinel and
                                                  // makes every `id >= 0` check valid ([2] random + [Q] decoy gate)
            }
        }

        private static void ComputeHash()
        {
            unchecked
            {
                int h = 17;
                foreach (var e in _entries) h = h * 31 + StableHash(e.Name);
                _hash = h;
            }
        }

        /// <summary>Largest world dimension of a prop (metres), used to scale its disguise HP. 0 if unknown.</summary>
        internal static float SizeOf(int id)
        {
            var e = ById(id);
            if (e == null || e.SourceRenderer == null) return 0f;
            var sz = e.SourceRenderer.bounds.size;
            return Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z));
        }

        /// <summary>A random catalog id (for the [2] "next random prop" control), avoiding <paramref name="exclude"/>
        /// so the prop visibly changes. -1 if the catalog is empty.</summary>
        internal static int RandomId(int exclude = -1)
        {
            if (_entries.Count == 0) return -1;
            if (_entries.Count == 1) return _entries[0].Id;
            for (int tries = 0; tries < 8; tries++)
            {
                int id = _entries[UnityEngine.Random.Range(0, _entries.Count)].Id;
                if (id != exclude) return id;
            }
            return _entries[UnityEngine.Random.Range(0, _entries.Count)].Id;
        }

        /// <summary>Map a looked-at mesh to its catalog id, or -1 if it isn't a hide-able prop.</summary>
        internal static int IdForMesh(Mesh m)
        {
            if (m == null || string.IsNullOrEmpty(m.name)) return -1;
            int id = StableHash(m.name);
            return (_byId.TryGetValue(id, out var e) && e.Name == m.name) ? id : -1;
        }

        internal static void Reset() { _entries.Clear(); _byId.Clear(); _hash = 0; }
    }
}
