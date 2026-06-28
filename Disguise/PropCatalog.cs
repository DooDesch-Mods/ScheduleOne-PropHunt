using System.Collections.Generic;
using System.IO;
using Il2CppScheduleOne.AvatarFramework;
using UnityEngine;

namespace PropHunt.Disguise
{
    /// <summary>One disguise prop: the source mesh + materials to clone onto a hider, plus a stable id.</summary>
    internal sealed class PropEntry
    {
        internal int Id;
        /// <summary>Human-readable display name (mesh.name). NOT used for identity; multiple different meshes may share it.</summary>
        internal string Name;
        /// <summary>Content-signature key used for identity + curation. Non-LOD: MeshKey = "name|verts|wFxhFxdF".
        /// LOD props: lex-min MeshKey across LOD levels (one key per prop type, same for all instances).</summary>
        internal string Key;
        internal MeshFilter Source;
        internal MeshRenderer SourceRenderer;
        // non-null when this prop's mesh sits under a LODGroup -> the disguise clones the whole LOD hierarchy
        // (1:1 LOD switching) instead of a single mesh.
        internal LODGroup SourceLodGroup;
        // the GameObject to clone: the LODGroup root when present, else the single mesh's GameObject.
        internal GameObject SourceRoot;
    }

    /// <summary>
    /// Deterministic catalog of hide-able props. Scans the loaded world for becomable meshes and dedupes by a
    /// content signature so the hider becomes EXACTLY the mesh they look at. Each prop's id is StableHash(Key).
    /// Disabled renderers (statically-batched / RuntimeMeshCombiner-merged originals) are skipped so they never
    /// appear as becomable - the same set is excluded on every client.
    ///
    /// Identity: MeshKey(m) = "name|vertexCount|wFxhFxdF" - only values baked into the mesh asset, so two
    /// different meshes that share a name produce different keys, and the same mesh resolves identically on
    /// host + client + every instance.
    ///
    /// Curation: when a curation file exists (authored in-game with the phcurate tool) it is the AUTHORITY -
    /// a prop is becomable iff explicitly approved ("allowlist"). Until then the heuristic name/size filter applies.
    /// </summary>
    internal static class PropCatalog
    {
        // props that look broken as a disguise (used only by the pre-curation heuristic fallback; checked on Name)
        private static readonly string[] _badNames = { "ReverseLight", "Case" };
        private static readonly List<PropEntry> _entries = new List<PropEntry>();
        private static readonly Dictionary<int, PropEntry> _byId = new Dictionary<int, PropEntry>();
        private static int _hash;

        // curation decisions keyed by entry Key: true = keep (becomable), false = skip. Absent = unreviewed.
        private static readonly Dictionary<string, bool> _curation = new Dictionary<string, bool>();
        private static bool _curationLoaded;

        internal static int Hash => _hash;
        internal static int Count => _entries.Count;
        /// <summary>True when an allowlist is in force (a curation file with decisions was loaded); false = heuristic mode.</summary>
        internal static bool Curated => _curation.Count > 0;
        internal static PropEntry ById(int id) => _byId.TryGetValue(id, out var e) ? e : null;

        /// <summary>Content-signature key for a mesh. Combines name, vertex count, and quantised bounds so two
        /// different meshes that share a name (e.g. "Base", "Cube") produce different keys. All components are
        /// baked into the asset - identical on host and client and across all instances of the same prop type.</summary>
        internal static string MeshKey(Mesh m)
        {
            if (m == null) return null;
            var s = m.bounds.size;
            return $"{m.name}|{m.vertexCount}|{s.x:F1}x{s.y:F1}x{s.z:F1}";
        }

        /// <summary>Structural non-props that must NEVER be becomable regardless of curation: our own clones,
        /// collision proxies ("*Collider*") and whole-scene static-batch chunks ("Combined Mesh ...").</summary>
        internal static bool IsJunkMeshName(string n)
        {
            if (string.IsNullOrEmpty(n)) return true;
            if (n.StartsWith("ph_")) return true;
            if (n.IndexOf("Combined Mesh", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.IndexOf("Collider", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        internal static void BuildIfNeeded()
        {
            if (_entries.Count == 0) Build();
        }

        /// <summary>
        /// Build the catalog: one scan of every MeshFilter, deduped by content key. A LOD prop's mesh resolves to
        /// its LODGroup root (so the disguise clones the whole LOD hierarchy); a non-LOD mesh is its own single
        /// prop. Disabled renderers (batched/merged originals) are skipped unconditionally so the set is the same
        /// on every client. The hider becomes exactly the mesh they look at - no over-grouping.
        /// </summary>
        internal static void Build()
        {
            _entries.Clear();
            _byId.Clear();
            if (!_curationLoaded) LoadCuration();
            bool curated = _curation.Count > 0;   // any decision made -> allowlist mode
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int scanned = 0, survivors = 0;
            try
            {
                var seen = new HashSet<string>();   // dedup by content key
                var tmp = new List<PropEntry>();
                var filters = UnityEngine.Object.FindObjectsOfType<MeshFilter>();
                scanned = filters != null ? filters.Length : 0;
                // On the HOST the whole city is loaded (tens of thousands of MeshFilters); each generic
                // GetComponentInParent<T> is an expensive IL2CPP interop hierarchy walk. Running TWO of them
                // (Avatar + LODGroup) per mesh froze StartAsHost. So the order below is deliberate: every CHEAP
                // reject (name/vertex/material, content-key dedup, allowlist/heuristic) runs FIRST, and only the
                // few hundred SURVIVORS pay for the hierarchy walks.
                Core.LogDebug($"[PropHunt] catalog: scanning {scanned} mesh filters (curated={curated})...");
                if (filters != null)
                {
                    for (int i = 0; i < filters.Length; i++)
                    {
                        var mf = filters[i];
                        if (mf == null) continue;
                        var mesh = mf.sharedMesh;
                        if (mesh == null) continue;
                        string name = mesh.name;
                        if (IsJunkMeshName(name)) continue;
                        if (mesh.vertexCount < 8) continue;
                        var rend = mf.GetComponent<MeshRenderer>();
                        // require a material (needed to clone the disguise). The renderer's ENABLED flag is NOT
                        // checked: static batching / the MTAssets RuntimeMeshCombiner disable the original
                        // renderers at a timing that differs between host and client, so keying on it produced
                        // DIFFERENT catalogs per machine (same prop id -> different mesh). The MeshFilter set is
                        // the same everywhere, so ignoring enabled makes the catalog deterministic. The merged
                        // "Combined Mesh" chunks are still excluded by IsJunkMeshName.
                        if (rend == null || rend.sharedMaterial == null) continue;
                        string key = MeshKey(mesh);
                        if (key == null || !seen.Add(key)) continue;   // one entry per distinct mesh

                        if (curated)
                        {
                            // allowlist: only meshes the curator explicitly approved (a cheap dictionary lookup -
                            // rejects ~all of the world's meshes before any hierarchy walk).
                            if (!(_curation.TryGetValue(key, out var keep) && keep)) continue;
                        }
                        else
                        {
                            // pre-curation heuristic: name blocklist + hide-able size band. Size from the mesh
                            // asset bounds * world scale (deterministic + valid even when the renderer is disabled,
                            // unlike rend.bounds which can be stale on a batched-away renderer).
                            if (!IsUsablePropName(name)) continue;
                            Vector3 b = Vector3.Scale(mesh.bounds.size, mf.transform.lossyScale);
                            float maxd = Mathf.Max(b.x, Mathf.Max(b.y, b.z));
                            float mind = Mathf.Min(b.x, Mathf.Min(b.y, b.z));
                            if (maxd < 0.3f || maxd > 4f) continue;   // only hide-able-sized props
                            if (mind < 0.08f) continue;               // skip flat planes/decals
                        }

                        // survivors only (a few hundred) -> the expensive hierarchy walks: reject NPC/player
                        // avatar body parts + doors (kept interactable), then resolve the LOD root for cloning.
                        if (IsCharacterMesh(mf) || IsDoorMesh(mf)) continue;
                        survivors++;
                        var lg = mf.GetComponentInParent<LODGroup>();
                        tmp.Add(new PropEntry
                        {
                            Name = name, Key = key, Source = mf, SourceRenderer = rend,
                            SourceLodGroup = lg,
                            SourceRoot = lg != null ? lg.gameObject : mf.gameObject
                        });
                    }
                }
                tmp.Sort((a, c) => string.CompareOrdinal(a.Key, c.Key));   // deterministic collision-resolution order
                for (int i = 0; i < tmp.Count; i++)
                {
                    int id = StableHash(tmp[i].Key);
                    if (_byId.ContainsKey(id)) continue;   // hash collision (rare) - keep the first by sort order
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
            sw.Stop();
            int lodCount = 0;
            foreach (var e in _entries) if (e.SourceLodGroup != null) lodCount++;
            Core.Log.Msg($"[PropHunt] prop catalog: {_entries.Count} props ({lodCount} with LODGroup, hash {_hash}){(curated ? " [curated allowlist]" : "")} " +
                         $"- scanned {scanned} filters, {survivors} survivors, {sw.ElapsedMilliseconds}ms.");
        }

        /// <summary>
        /// ALL reviewable candidate props in the loaded world (for the phcurate tool): one scan, deduped per
        /// distinct mesh (LOD props deduped per type via LodTypeKey so each LOD prop shows once). No curation or
        /// heuristic size/name filter; structural guards (junk/character/vertex/material) still apply.
        /// </summary>
        internal static List<PropEntry> EnumerateAllCandidates()
        {
            var seen = new HashSet<string>();
            var seenLodType = new HashSet<string>();
            var list = new List<PropEntry>();
            try
            {
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
                        if (IsJunkMeshName(name)) continue;
                        if (mesh.vertexCount < 8) continue;
                        var rend = mf.GetComponent<MeshRenderer>();
                        // enabled flag deliberately NOT checked - see Build(): keying on it makes the candidate
                        // set timing-dependent (batching) and thus different per machine.
                        if (rend == null || rend.sharedMaterial == null) continue;
                        if (IsCharacterMesh(mf) || IsDoorMesh(mf)) continue;   // never offer doors as disguises

                        var lg = mf.GetComponentInParent<LODGroup>();
                        string entryKey;
                        if (lg != null)
                        {
                            entryKey = LodTypeKey(lg);                      // one entry per LOD prop type
                            if (entryKey == null || !seenLodType.Add(entryKey)) continue;
                        }
                        else
                        {
                            entryKey = MeshKey(mesh);
                            if (entryKey == null || !seen.Add(entryKey)) continue;
                        }
                        list.Add(new PropEntry
                        {
                            Id = StableHash(entryKey), Key = entryKey, Name = name,
                            Source = mf, SourceRenderer = rend,
                            SourceLodGroup = lg,
                            SourceRoot = lg != null ? lg.gameObject : mf.gameObject
                        });
                    }
                }
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] EnumerateAllCandidates failed: " + e.Message); }
            list.Sort((a, c) => string.CompareOrdinal(a.Key, c.Key));
            return list;
        }

        /// <summary>
        /// Stable per-TYPE key for a LODGroup: the lexicographically-smallest MeshKey across all LOD levels.
        /// Every instance of the same prop prefab shares the same mesh assets, so this key is identical for all
        /// copies of that prop in the world while remaining distinct from other prop types.
        /// </summary>
        private static string LodTypeKey(LODGroup lg)
        {
            string best = null;
            try
            {
                var mfs = lg.GetComponentsInChildren<MeshFilter>(true);
                if (mfs != null)
                    for (int i = 0; i < mfs.Length; i++)
                    {
                        var m = mfs[i] != null ? mfs[i].sharedMesh : null;
                        if (m != null && !IsJunkMeshName(m.name) && mfs[i].GetComponent<MeshRenderer>() != null)
                        {
                            string k = MeshKey(m);
                            if (k != null && (best == null || string.CompareOrdinal(k, best) < 0))
                                best = k;
                        }
                    }
            }
            catch { }
            return best ?? (lg.gameObject != null ? lg.gameObject.name : "lod_unknown");
        }

        /// <summary>
        /// Returns true when <paramref name="mf"/> is part of an NPC or player avatar (body parts, hair, etc.).
        /// Uses GetComponentInParent(Avatar): the Avatar MonoBehaviour is the root of every character visual
        /// hierarchy. World props never have an Avatar ancestor. Covers LOD avatar meshes too (the Avatar is above the LODGroup).
        /// </summary>
        private static bool IsCharacterMesh(MeshFilter mf)
        {
            try { return mf.GetComponentInParent<Avatar>() != null; }
            catch { return false; }
        }

        /// <summary>True when the mesh belongs to a door (any mesh under a DoorController). Doors are NEVER becomable:
        /// a hider aiming at one would suppress its open-interaction, so we exclude them so doors stay usable. The
        /// DoorController component is present identically on host + client, so the exclusion is deterministic.</summary>
        private static bool IsDoorMesh(MeshFilter mf)
        {
            try { return mf.GetComponentInParent<Il2CppScheduleOne.Doors.DoorController>() != null; }
            catch { return false; }
        }

#if DEBUG
        /// <summary>
        /// Logs the full candidate list with per-entry details. Called by the phcuratelist console command.
        /// Output per entry: index, name (display), key, LODGroup yes/no, world size, vertex count, material count, decision.
        /// </summary>
        internal static void DumpCandidates()
        {
            var candidates = EnumerateAllCandidates();
            if (!_curationLoaded) LoadCuration();
            int lodCount = 0;
            for (int i = 0; i < candidates.Count; i++) if (candidates[i].SourceLodGroup != null) lodCount++;
            Core.Log.Msg($"[PropHunt] phcuratelist: {candidates.Count} candidates ({lodCount} with LODGroup)");
            for (int i = 0; i < candidates.Count; i++)
            {
                var e = candidates[i];
                string lodTag = e.SourceLodGroup != null ? "LOD" : "   ";
                float sz = 0f;
                int verts = 0, mats = 0;
                try
                {
                    if (e.SourceLodGroup != null) { sz = e.SourceLodGroup.size; }
                    else if (e.SourceRenderer != null) { var b = e.SourceRenderer.bounds.size; sz = Mathf.Max(b.x, Mathf.Max(b.y, b.z)); }
                    if (e.Source != null && e.Source.sharedMesh != null) verts = e.Source.sharedMesh.vertexCount;
                    if (e.SourceRenderer != null && e.SourceRenderer.sharedMaterials != null) mats = e.SourceRenderer.sharedMaterials.Length;
                }
                catch { }
                var dec = DecisionOf(e.Key);
                string decTag = dec == true ? "KEEP" : dec == false ? "SKIP" : "----";
                Core.Log.Msg($"[PropHunt]  {i + 1,4}  {lodTag}  {sz,5:F2}m  v{verts,6}  m{mats}  [{decTag}]  {e.Name}  ({e.Key})");
            }
        }
#endif

        // ---- curation persistence (UserData/PropHunt/prop_curation.txt; lines "key=1" keep / "key=0" skip) ----
        // The key format contains '|' and 'x' but never '=', so splitting on the LAST '=' is safe.

        private static string CurationPath =>
            Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "PropHunt", "prop_curation.txt");

        internal static void LoadCuration()
        {
            _curationLoaded = true;
            _curation.Clear();
            try
            {
                string path = CurationPath;
                string[] lines; string source;
                if (File.Exists(path)) { lines = File.ReadAllLines(path); source = "user"; }
                else { lines = ReadShippedCuration(); source = "shipped"; }   // default allowlist baked into the mod
                if (lines == null) { Core.LogDebug("[PropHunt] curation: no user file and no shipped allowlist - heuristic mode."); return; }
                ParseCurationLines(lines);
                Core.LogDebug($"[PropHunt] curation loaded ({source}): {_curation.Count} decisions, {KeepCount()} kept.");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] LoadCuration failed: " + e.Message); }
        }

        private static void ParseCurationLines(string[] lines)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                int eq = line.LastIndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (key.Length > 0) _curation[key] = (v == "1");
            }
        }

        /// <summary>The curated allowlist shipped inside the mod DLL (PropHunt.Assets.prop_curation.txt). This is
        /// the source of truth used on a fresh install; a UserData file (dev curation) overrides it. Null if absent.</summary>
        private static string[] ReadShippedCuration()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("PropHunt.Assets.prop_curation.txt"))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s))
                        return r.ReadToEnd().Split('\n');
                }
            }
            catch { return null; }
        }

        internal static void SaveCuration()
        {
            try
            {
                string path = CurationPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# PropHunt becomable-prop curation. key=1 keep, key=0 skip. Edit in-game with the phcurate console command.");
                sb.AppendLine("# key = meshName|vertexCount|wFxhFxdF (content signature; LOD props use the lex-min key across LODs).");
                foreach (var kv in _curation) sb.Append(kv.Key).Append('=').Append(kv.Value ? '1' : '0').Append('\n');
                File.WriteAllText(path, sb.ToString());
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] SaveCuration failed: " + e.Message); }
        }

        /// <summary>Record a keep/skip decision for an entry Key and persist immediately.</summary>
        internal static void SetDecision(string key, bool keep)
        {
            if (string.IsNullOrEmpty(key)) return;
            if (!_curationLoaded) LoadCuration();
            _curation[key] = keep;
            SaveCuration();
        }

        /// <summary>Current decision for an entry Key: true = keep, false = skip, null = unreviewed.</summary>
        internal static bool? DecisionOf(string key)
        {
            if (!_curationLoaded) LoadCuration();
            return _curation.TryGetValue(key, out var v) ? v : (bool?)null;
        }

        /// <summary>How many props are currently approved (kept).</summary>
        internal static int KeepCount()
        {
            int n = 0;
            foreach (var kv in _curation) if (kv.Value) n++;
            return n;
        }

#if DEBUG
        /// <summary>
        /// DEV seed for the becomable-prop allowlist: marks every candidate the heuristic would accept as KEEP and
        /// everything else as SKIP, then saves. Turns the heuristic baseline (~223 props) into a curation file the
        /// dev only has to REFINE (skip the few bad ones) instead of reviewing all from scratch. Console:
        /// phcurateseed. The finished file ships with the mod as the source-of-truth allowlist.
        /// </summary>
        internal static int SeedCurationFromHeuristic()
        {
            if (!_curationLoaded) LoadCuration();
            _curation.Clear();
            var all = EnumerateAllCandidates();
            for (int i = 0; i < all.Count; i++)
            {
                var e = all[i];
                bool keep = HeuristicKeep(e);
                // LOD props: record EVERY LOD MeshKey (Build checks each LOD mesh's key), like the curator does.
                if (e.SourceLodGroup != null)
                {
                    var keys = LodMeshKeys(e.SourceLodGroup);
                    if (keys.Count == 0) _curation[e.Key] = keep;
                    else for (int k = 0; k < keys.Count; k++) _curation[keys[k]] = keep;
                }
                else _curation[e.Key] = keep;
            }
            SaveCuration();
            return KeepCount();
        }

        /// <summary>The pre-curation heuristic verdict for a candidate (matches Build's heuristic branch).</summary>
        private static bool HeuristicKeep(PropEntry e)
        {
            if (e == null || e.Source == null) return false;
            var mesh = e.Source.sharedMesh;
            if (mesh == null) return false;
            if (!IsUsablePropName(e.Name)) return false;
            Vector3 b = Vector3.Scale(mesh.bounds.size, e.Source.transform.lossyScale);
            float maxd = Mathf.Max(b.x, Mathf.Max(b.y, b.z));
            float mind = Mathf.Min(b.x, Mathf.Min(b.y, b.z));
            if (maxd < 0.3f || maxd > 4f) return false;
            if (mind < 0.08f) return false;
            return true;
        }
#endif

        /// <summary>Reject meshes that make absurd disguises (pre-curation heuristic only; checks display Name).</summary>
        private static bool IsUsablePropName(string n)
        {
            if (string.IsNullOrEmpty(n) || n.StartsWith("ph_")) return false;
            if (n.IndexOf("Combined Mesh", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (n.IndexOf("Collider", System.StringComparison.OrdinalIgnoreCase) >= 0) return false;
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
                string key = MeshKey(mf != null ? mf.sharedMesh : null) ?? name;
                int id = StableHash(key);
                var e = new PropEntry { Id = id, Key = key, Name = name, Source = mf, SourceRenderer = mr };
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
                return (int)(h & 0x7FFFFFFFu);   // always non-negative: keeps -1 a clean "no prop" sentinel
            }
        }

        private static void ComputeHash()
        {
            unchecked
            {
                int h = 17;
                foreach (var e in _entries) h = h * 31 + StableHash(e.Key);
                _hash = h;
            }
        }

        /// <summary>Largest world dimension of a prop (metres), used to scale its disguise HP / collision / hitbox. 0 if unknown.</summary>
        internal static float SizeOf(int id)
        {
            var e = ById(id);
            if (e == null) return 0f;
            if (e.SourceLodGroup != null) { try { float s = e.SourceLodGroup.size; if (s > 0f) return s; } catch { } }
            if (e.SourceRenderer == null) return 0f;
            var sz = e.SourceRenderer.bounds.size;
            return Mathf.Max(sz.x, Mathf.Max(sz.y, sz.z));
        }

        /// <summary>All distinct LOD MeshKeys under a LODGroup (LOD0/LOD1/LOD2...). Used by the curator so a
        /// keep/skip decision on a LOD prop covers every LOD mesh - the picker resolves whichever LOD the world
        /// prop is showing at the hider's distance to the same approved prop.</summary>
        internal static List<string> LodMeshKeys(LODGroup lg)
        {
            var keys = new List<string>();
            if (lg == null) return keys;
            try
            {
                var mfs = lg.GetComponentsInChildren<MeshFilter>(true);
                if (mfs != null)
                    for (int i = 0; i < mfs.Length; i++)
                    {
                        var m = mfs[i] != null ? mfs[i].sharedMesh : null;
                        if (m == null || IsJunkMeshName(m.name)) continue;                 // skip collider/junk children
                        if (mfs[i].GetComponent<MeshRenderer>() == null) continue;          // only renderable LOD meshes
                        string k = MeshKey(m);
                        if (k != null && !keys.Contains(k)) keys.Add(k);
                    }
            }
            catch { }
            return keys;
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

        /// <summary>Map a looked-at mesh to its catalog id, or -1 if it isn't a becomable prop. Direct content-key
        /// match (the hider becomes exactly the mesh under the crosshair); a LOD prop's individual LOD meshes are
        /// separate catalog entries that all clone the same LODGroup root, so any LOD distance resolves correctly.</summary>
        internal static int IdForMesh(Mesh m)
        {
            if (m == null) return -1;
            string key = MeshKey(m);
            if (key == null) return -1;
            int id = StableHash(key);
            return (_byId.TryGetValue(id, out var e) && e.Key == key) ? id : -1;
        }

        internal static void Reset() { _entries.Clear(); _byId.Clear(); _hash = 0; }
    }
}
