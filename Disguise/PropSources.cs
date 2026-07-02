using System.Collections.Generic;
using UnityEngine;
using Il2CppScheduleOne;                         // Registry
using Il2CppScheduleOne.ItemFramework;           // ItemDefinition, BuildableItemDefinition
using Il2CppScheduleOne.Vehicles;                // VehicleManager, LandVehicle

namespace PropHunt.Disguise
{
    /// <summary>
    /// Alternative WHOLE-OBJECT prop sources that read the game's own clean databases instead of scraping the live
    /// scene mesh-by-mesh, yielding ONE PropEntry per logical object (each sets <see cref="PropEntry.CloneWholeRoot"/>
    /// so <see cref="PropClone"/> clones the ENTIRE prefab/object subtree):
    ///   - BUILDABLES: every <see cref="BuildableItemDefinition"/> in <see cref="Registry"/> -> its <c>BuiltItem</c>
    ///     prefab (furniture/fixtures/equipment). Keyed "reg:&lt;defName&gt;". Deterministic (prefab DB, host==client).
    ///   - VEHICLES: every <see cref="VehicleManager"/> prefab (one per car type). Keyed "veh:&lt;VehicleCode&gt;". Deterministic.
    ///   - WORLD: interactive scene objects (ATM/vending/dumpster/...) grouped by their root marker component and
    ///     deduped by mesh CONTENT hash. Keyed "world:&lt;hash&gt;". Enumerated via FindObjectsOfTypeAll (all instances,
    ///     not culling-dependent) so the set is identical host/client for the same loaded scene.
    /// Keys live in their own "reg:"/"veh:"/"world:" namespaces so they share the one curation file without colliding
    /// with scene "name|verts|bounds" keys. The enumeration ships (Release) - <see cref="PropCatalog"/> ingests the
    /// APPROVED ones into the becomable catalog; the diagnostic probes at the bottom stay DEBUG-only.
    /// </summary>
    internal static class PropSources
    {
        internal const string BuildablePrefix = "reg:";
        internal const string VehiclePrefix = "veh:";

        /// <summary>One whole-object PropEntry per buildable definition (Registry), cloning the full BuiltItem prefab.</summary>
        internal static List<PropEntry> EnumerateBuildables()
        {
            var list = new List<PropEntry>();
            try
            {
                var reg = Registry.Instance;
                if (reg == null) { Core.Log.Warning("[PropHunt] EnumerateBuildables: Registry.Instance null - load a world first."); return list; }
                var items = reg.GetAllItems();
                if (items == null) return list;
                var seen = new HashSet<string>();
                for (int i = 0; i < items.Count; i++)
                {
                    var def = items[i];
                    if (def == null) continue;
                    var b = def.TryCast<BuildableItemDefinition>();
                    if (b == null) continue;
                    var built = b.BuiltItem;
                    if (built == null || built.gameObject == null) continue;
                    string key = BuildablePrefix + def.name;
                    if (!seen.Add(key)) continue;
                    var entry = MakeWholeObjectEntry(key, def.name, built.gameObject);
                    if (entry != null) list.Add(entry);
                }
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] EnumerateBuildables failed: " + e.Message); }
            list.Sort((a, c) => string.CompareOrdinal(a.Name ?? "", c.Name ?? ""));
            return list;
        }

        /// <summary>One whole-object PropEntry per vehicle TYPE (VehicleManager.VehiclePrefabs), cloning the full car.</summary>
        internal static List<PropEntry> EnumerateVehicles()
        {
            var list = new List<PropEntry>();
            try
            {
                var vm = VehicleManager.Instance;
                if (vm == null) { Core.Log.Warning("[PropHunt] EnumerateVehicles: VehicleManager.Instance null - load a world first."); return list; }
                var prefabs = vm.VehiclePrefabs;
                if (prefabs == null) return list;
                var seen = new HashSet<string>();
                for (int i = 0; i < prefabs.Count; i++)
                {
                    var v = prefabs[i];
                    if (v == null || v.gameObject == null) continue;
                    string code = null;
                    try { code = v.VehicleCode; } catch { }
                    if (string.IsNullOrEmpty(code)) code = v.gameObject.name;
                    string key = VehiclePrefix + code;
                    if (!seen.Add(key)) continue;
                    // clone the whole LandVehicle.gameObject (NOT vehicleModel - wheels live outside it)
                    var entry = MakeWholeObjectEntry(key, code, v.gameObject);
                    if (entry != null) list.Add(entry);
                }
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] EnumerateVehicles failed: " + e.Message); }
            list.Sort((a, c) => string.CompareOrdinal(a.Name ?? "", c.Name ?? ""));
            return list;
        }

        /// <summary>Wrap a whole prefab as one PropEntry: pick the highest-vertex renderable mesh as the representative
        /// (for the info/preview-scale display), but flag CloneWholeRoot so PropClone clones the ENTIRE subtree.</summary>
        private static PropEntry MakeWholeObjectEntry(string key, string name, GameObject root)
        {
            if (root == null) return null;
            MeshFilter rep = null; MeshRenderer repR = null; int bestVerts = -1; bool hasBatched = false;
            try
            {
                var mfs = root.GetComponentsInChildren<MeshFilter>(true);
                if (mfs != null)
                    for (int i = 0; i < mfs.Length; i++)
                    {
                        var mf = mfs[i];
                        if (mf == null || mf.sharedMesh == null) continue;
                        var mn = mf.sharedMesh.name;
                        // Unity STATIC BATCHING replaces an object's real mesh with a scene-wide "Combined Mesh"
                        // (world-space, contains many objects) and disables the original renderer. Detect it: such
                        // an object's visual is unusable as a single prop.
                        if (!string.IsNullOrEmpty(mn) && mn.IndexOf("Combined Mesh", System.StringComparison.OrdinalIgnoreCase) >= 0) hasBatched = true;
                        if (PropCatalog.IsJunkMeshName(mn)) continue;
                        var mr = mf.GetComponent<MeshRenderer>();
                        if (mr == null || mr.sharedMaterial == null) continue;
                        int v = mf.sharedMesh.vertexCount;
                        if (v > bestVerts) { bestVerts = v; rep = mf; repR = mr; }
                    }
            }
            catch { }
            if (rep == null) return null;   // no renderable mesh+material -> nothing to preview/clone
            // Static-batched object: its real visual was merged into a scene "Combined Mesh" (dropped as junk),
            // leaving only tiny per-object proxy meshes (e.g. the ATM = 24-vert cubes). It CANNOT be cloned as a
            // clean prop, so skip it rather than offer a broken box. Genuine low-poly props have no Combined Mesh.
            if (hasBatched && bestVerts < 40)
            {
                Core.LogDebug($"[PropHunt] world skip (static-batched, only {bestVerts}-vert proxies): '{name}'");
                return null;
            }
            return new PropEntry
            {
                Key = key, Name = name, Source = rep, SourceRenderer = repR,
                SourceLodGroup = null, SourceRoot = root, CloneWholeRoot = true
            };
        }

        internal const string WorldPrefix = "world:";

        /// <summary>One whole-object PropEntry per INTERACTIVE/FUNCTIONAL world object (ATM, vending machine, dumpster,
        /// storage, ...). These are NOT in a prefab database, but each carries a ScheduleOne "root" component at its
        /// object root (observed live: ATM/VendingMachine have PhysicsDamageable; dumpster has NetworkedInteractable-
        /// Toggleable; boxes have StorageEntity). We enumerate those components directly (cheap - a few hundred, vs
        /// tens of thousands of meshes) and clone the whole object subtree. Deduped by mesh CONTENT so the many ATMs
        /// collapse to one entry. Pure static decor (no functional component) has no reliable marker and is NOT here -
        /// single-LODGroup decor is already one entry in the scene list (phcuratekeep).</summary>
        internal static List<PropEntry> EnumerateWorldObjects()
        {
            var map = new Dictionary<string, PropEntry>();
            try
            {
                AddRootsOfType<Il2CppScheduleOne.Combat.PhysicsDamageable>(map);
                AddRootsOfType<Il2CppScheduleOne.Interaction.NetworkedInteractableToggleable>(map);
                AddRootsOfType<Il2CppScheduleOne.Storage.StorageEntity>(map);
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] EnumerateWorldObjects failed: " + e.Message); }
            var list = new List<PropEntry>(map.Values);

            // Secondary dedup by REPRESENTATIVE MESH NAME (only for specific, non-generic names). Static batching
            // leaves different instances of the same object in different states - one with its body intact, one with
            // the body merged into a scene "Combined Mesh" so only e.g. a lid survives. They differ in content (so the
            // hash dedup keeps both) but share a specific mesh name ("Dumpster"). Collapse those to the RICHEST
            // (highest-vertex) instance, replacing the broken partial with the complete object. Generic mesh names
            // (Cube/Body/Lid/...) are NOT merged - too ambiguous - so distinct objects are never wrongly joined.
            var byMesh = new Dictionary<string, PropEntry>();
            var passthrough = new List<PropEntry>();
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                string mn = (e.Source != null && e.Source.sharedMesh != null) ? e.Source.sharedMesh.name : null;
                if (string.IsNullOrEmpty(mn) || IsGenericMeshName(mn)) { passthrough.Add(e); continue; }
                if (!byMesh.TryGetValue(mn, out var cur) || RepVerts(e) > RepVerts(cur)) byMesh[mn] = e;
            }
            var result = new List<PropEntry>(passthrough);
            result.AddRange(byMesh.Values);
            result.Sort((a, c) => string.CompareOrdinal(a.Name ?? "", c.Name ?? ""));
            return result;
        }

        private static int RepVerts(PropEntry e) =>
            (e != null && e.Source != null && e.Source.sharedMesh != null) ? e.Source.sharedMesh.vertexCount : 0;

        /// <summary>Generic mesh names that many unrelated objects share - never merge by these (would join distinct
        /// props). Specific asset names (e.g. "Dumpster", "Basketball") ARE safe to merge across batching states.</summary>
        private static bool IsGenericMeshName(string n)
        {
            if (string.IsNullOrEmpty(n)) return true;
            var s = n.ToLowerInvariant();
            return s == "cube" || s == "body" || s == "lid" || s == "base" || s == "quad" || s == "plane"
                || s == "sphere" || s == "cylinder" || s == "capsule" || s == "mesh" || s == "cover"
                || s == "top" || s == "bottom" || s == "panel" || s == "default" || s == "object";
        }

        private static void AddRootsOfType<T>(Dictionary<string, PropEntry> map) where T : Component
        {
            try
            {
                var comps = Resources.FindObjectsOfTypeAll<T>();
                if (comps == null) return;
                for (int i = 0; i < comps.Length; i++)
                {
                    var c = comps[i];
                    if (c == null || c.gameObject == null) continue;
                    TryAddRoot(c.gameObject, map);
                }
            }
            catch { }
        }

        /// <summary>True if a GameObject carries one of the world-object root markers.</summary>
        private static bool HasWorldMarker(GameObject g)
        {
            if (g == null) return false;
            try
            {
                return g.GetComponent<Il2CppScheduleOne.Combat.PhysicsDamageable>() != null
                    || g.GetComponent<Il2CppScheduleOne.Interaction.NetworkedInteractableToggleable>() != null
                    || g.GetComponent<Il2CppScheduleOne.Storage.StorageEntity>() != null;
            }
            catch { return false; }
        }

        /// <summary>Resolve the OUTERMOST marker-bearing ancestor as the object root. Objects can carry a marker on
        /// both the root AND a child (e.g. a chair with a damageable body + an interactable part); without this, the
        /// child marker produces a second, partial entry (looks the same but a bit smaller). Walking up to the
        /// topmost marker collapses those to the one whole object.</summary>
        private static GameObject TopMarkerRoot(GameObject go)
        {
            var top = go;
            try
            {
                var t = go.transform.parent;
                while (t != null) { if (HasWorldMarker(t.gameObject)) top = t.gameObject; t = t.parent; }
            }
            catch { }
            return top;
        }

        private static void TryAddRoot(GameObject markerGo, Dictionary<string, PropEntry> map)
        {
            try
            {
                if (markerGo == null || !markerGo.scene.IsValid()) return;        // real world instance, not a prefab template (checked early)
                var root = TopMarkerRoot(markerGo);                               // collapse nested markers to the whole object
                if (root == null) return;
                // objects handled by the other lists / never a prop:
                if (root.GetComponentInParent<Il2CppScheduleOne.Vehicles.LandVehicle>() != null) return;
                if (root.GetComponentInParent<Il2CppScheduleOne.EntityFramework.BuildableItem>() != null) return;
                if (root.GetComponentInParent<Il2CppScheduleOne.AvatarFramework.Avatar>() != null) return;
                string typeKey = RootTypeKey(root);
                if (typeKey == null) return;                                       // no renderable mesh -> can't preview/clone
                string full = WorldPrefix + typeKey;
                if (map.TryGetValue(full, out var existing))
                {
                    // same object type already captured; prefer an ACTIVE instance as the clone template (a culled /
                    // inactive one is likelier to clone blank), which also makes the pick less order-dependent.
                    bool newActive = root.activeInHierarchy;
                    bool oldActive = existing != null && existing.SourceRoot != null && existing.SourceRoot.activeInHierarchy;
                    if (!(newActive && !oldActive)) return;                        // keep existing unless the new one is the better (active) template
                }
                var entry = MakeWholeObjectEntry(full, CleanName(root.name), root);
                if (entry != null) map[full] = entry;
            }
            catch { }
        }

        /// <summary>Stable per-TYPE key for a whole world object: the object's (cleaned) name plus a hash over the
        /// SORTED SET of all its renderable child MeshKeys. Using the whole mesh set - not just the lex-min mesh -
        /// avoids collisions between different objects that happen to share a generic first mesh ("Body"/"Cube"),
        /// which was dropping objects (e.g. the ATM) during dedup. Every instance of the same object has the same
        /// name + mesh set, so all its copies collapse to one entry; two different objects get distinct keys.</summary>
        private static string RootTypeKey(GameObject root)
        {
            var keys = new List<string>();
            try
            {
                var mfs = root.GetComponentsInChildren<MeshFilter>(true);
                if (mfs != null)
                    for (int i = 0; i < mfs.Length; i++)
                    {
                        var m = mfs[i] != null ? mfs[i].sharedMesh : null;
                        if (m == null || PropCatalog.IsJunkMeshName(m.name)) continue;
                        if (mfs[i].GetComponent<MeshRenderer>() == null) continue;
                        string k = PropCatalog.MeshKey(m);
                        if (k != null) keys.Add(k);
                    }
            }
            catch { }
            if (keys.Count == 0) return null;
            keys.Sort(System.StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            string prev = null;
            for (int i = 0; i < keys.Count; i++) { if (keys[i] != prev) { sb.Append(keys[i]).Append(';'); prev = keys[i]; } }
            // HASH ONLY - do NOT include the object's name. The hash over the whole mesh set IS the content identity;
            // adding the name split content-identical objects into duplicates (e.g. ~20 dead-drop/region markers that
            // share one mesh but have distinct location names - "Behind bank", "Albert's stash", ...). Hash-only
            // collapses those to one entry while keeping genuinely different objects (own mesh set) distinct.
            return Fnv(sb.ToString());
        }

        /// <summary>Strip a Unity instance suffix (" (3)") + trim, so "Dumpster (2)" and "dumpster" read as the object.</summary>
        private static string CleanName(string n)
        {
            if (string.IsNullOrEmpty(n)) return "?";
            int p = n.LastIndexOf(" (");
            if (p > 0 && n.EndsWith(")")) n = n.Substring(0, p);
            return n.Trim();
        }

        /// <summary>FNV-1a 32-bit hex (machine-independent - unlike string.GetHashCode).</summary>
        private static string Fnv(string s)
        {
            unchecked
            {
                uint h = 2166136261u;
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619u; }
                return h.ToString("x8");
            }
        }

        /// <summary>Resolve a LOOKED-AT mesh to its COMPOSITE catalog key (veh:/reg:/world:), or null if the object
        /// isn't a composite. Lets [E] look-at become a vehicle / buildable / world object (not just [2]-random) by
        /// mirroring the exact key each Enumerate* method assigns, so it maps to the SAME catalog entry. Walks the
        /// object's components (not its mesh), so it works even when the visual is static-batched.</summary>
        internal static string CompositeKeyFor(MeshFilter mf)
        {
            if (mf == null) return null;
            try
            {
                // vehicle (matches EnumerateVehicles: veh:<VehicleCode>)
                var lv = mf.GetComponentInParent<LandVehicle>();
                if (lv != null)
                {
                    string code = null; try { code = lv.VehicleCode; } catch { }
                    if (string.IsNullOrEmpty(code)) code = lv.gameObject.name;
                    return VehiclePrefix + code;
                }
                // buildable (matches EnumerateBuildables: reg:<definition name>)
                var bi = mf.GetComponentInParent<Il2CppScheduleOne.EntityFramework.BuildableItem>();
                if (bi != null)
                {
                    string dn = null;
                    try { var inst = bi.ItemInstance; var def = inst != null ? inst.Definition : null; dn = def != null ? def.name : null; } catch { }
                    if (!string.IsNullOrEmpty(dn)) return BuildablePrefix + dn;
                }
                // world marker (matches EnumerateWorldObjects: world:<RootTypeKey of the topmost marker root>)
                GameObject markerGo = null;
                var pd = mf.GetComponentInParent<Il2CppScheduleOne.Combat.PhysicsDamageable>();
                if (pd != null) markerGo = pd.gameObject;
                if (markerGo == null) { var nt = mf.GetComponentInParent<Il2CppScheduleOne.Interaction.NetworkedInteractableToggleable>(); if (nt != null) markerGo = nt.gameObject; }
                if (markerGo == null) { var se = mf.GetComponentInParent<Il2CppScheduleOne.Storage.StorageEntity>(); if (se != null) markerGo = se.gameObject; }
                if (markerGo != null)
                {
                    string tk = RootTypeKey(TopMarkerRoot(markerGo));
                    if (!string.IsNullOrEmpty(tk)) return WorldPrefix + tk;
                }
            }
            catch { }
            return null;
        }

#if DEBUG
        // ---- DEBUG-only diagnostics (console probes); compiled out of Release. The enumeration above ships. ----

        /// <summary>phdumpobj &lt;nameSubstring&gt;: dump a world object's child mesh tree (go/mesh/verts/renderer/LOD/junk)
        /// so we can see WHY something (e.g. the ATM) clones as a proxy box vs the detailed visual - grounds the P1 fix.</summary>
        internal static void DumpObject(string nameQuery)
        {
            if (string.IsNullOrEmpty(nameQuery)) { Core.Log.Warning("[PropHunt] usage: phdumpobj <name-substring>"); return; }
            try
            {
                GameObject match = null, sub = null;
                var all = Resources.FindObjectsOfTypeAll<Transform>();
                if (all != null)
                    for (int i = 0; i < all.Length; i++)
                    {
                        var t = all[i];
                        if (t == null || t.gameObject == null || !t.gameObject.scene.IsValid() || t.name == null) continue;
                        // prefer an EXACT (case-insensitive) name match; only fall back to the first substring hit
                        // (so "ATM" doesn't resolve to "heATMapRegion"). Also require a renderable mesh in the subtree.
                        if (t.gameObject.GetComponentInChildren<MeshRenderer>(true) == null) continue;
                        if (string.Equals(t.name, nameQuery, System.StringComparison.OrdinalIgnoreCase)) { match = t.gameObject; break; }
                        if (sub == null && t.name.IndexOf(nameQuery, System.StringComparison.OrdinalIgnoreCase) >= 0) sub = t.gameObject;
                    }
                if (match == null) match = sub;
                if (match == null) { Core.Log.Warning($"[PropHunt] phdumpobj: no scene object matching '{nameQuery}'"); return; }
                Core.Log.Msg($"[PropHunt] phdumpobj '{match.name}' active={match.activeInHierarchy} markerRoot='{TopMarkerRoot(match).name}':");
                var mfs = match.GetComponentsInChildren<MeshFilter>(true);
                int shown = 0;
                if (mfs != null)
                    for (int i = 0; i < mfs.Length && shown < 40; i++)
                    {
                        var mf = mfs[i]; if (mf == null) continue;
                        var mesh = mf.sharedMesh;
                        var mr = mf.GetComponent<MeshRenderer>();
                        bool junk = mesh != null && PropCatalog.IsJunkMeshName(mesh.name);
                        bool lod = mf.GetComponentInParent<LODGroup>() != null;
                        Core.Log.Msg($"[PropHunt]   MF go='{mf.gameObject.name}' mesh='{(mesh != null ? mesh.name : "null")}' verts={(mesh != null ? mesh.vertexCount : 0)} " +
                                     $"mr={(mr != null)} mrEnabled={(mr != null && mr.enabled)} active={mf.gameObject.activeInHierarchy} lod={lod} junk={junk}");
                        shown++;
                    }
                var smrs = match.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                if (smrs != null)
                    for (int i = 0; i < smrs.Length; i++)
                    {
                        var s = smrs[i]; if (s == null) continue;
                        Core.Log.Msg($"[PropHunt]   SMR go='{s.gameObject.name}' mesh='{(s.sharedMesh != null ? s.sharedMesh.name : "null")}' " +
                                     $"verts={(s.sharedMesh != null ? s.sharedMesh.vertexCount : 0)} enabled={s.enabled} active={s.gameObject.activeInHierarchy}");
                    }
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phdumpobj failed: " + e.Message); }
        }

        /// <summary>phfindmesh &lt;nameSubstring&gt;: scan ALL loaded Mesh ASSETS (Resources.FindObjectsOfTypeAll&lt;Mesh&gt;) for
        /// un-batched originals whose name matches - the gating probe for static-batch recovery. If a real per-object
        /// mesh (e.g. 'Dumpster'/'Body_LOD0', hundreds+ verts, NOT 'Combined Mesh') is resident, we can clone it by
        /// reference to recover the visual of a static-batched object.</summary>
        internal static void FindMeshAssets(string nameQuery)
        {
            if (string.IsNullOrEmpty(nameQuery)) { Core.Log.Warning("[PropHunt] usage: phfindmesh <name-substring>"); return; }
            try
            {
                var meshes = Resources.FindObjectsOfTypeAll<Mesh>();
                int total = 0, shown = 0;
                if (meshes != null)
                    for (int i = 0; i < meshes.Length; i++)
                    {
                        var m = meshes[i];
                        if (m == null) continue;
                        var mn = m.name;
                        if (string.IsNullOrEmpty(mn)) continue;
                        if (mn.IndexOf("Combined Mesh", System.StringComparison.OrdinalIgnoreCase) >= 0) continue;   // skip the scene batch itself
                        if (PropCatalog.IsJunkMeshName(mn)) continue;
                        if (mn.IndexOf(nameQuery, System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (m.vertexCount < 40) continue;                                                          // skip proxy cubes
                        total++;
                        if (shown < 40)
                        {
                            var b = m.bounds.size;
                            bool rd = false; try { rd = m.isReadable; } catch { }
                            Core.Log.Msg($"[PropHunt]   MESH '{mn}' verts={m.vertexCount} bounds={b.x:F1}x{b.y:F1}x{b.z:F1} readable={rd}");
                            shown++;
                        }
                    }
                Core.Log.Msg($"[PropHunt] phfindmesh '{nameQuery}': {total} candidate un-batched mesh asset(s) resident.");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phfindmesh failed: " + e.Message); }
        }

        /// <summary>phmeshpool: measure the AVAILABLE clean-prop pool. Scans every MeshFilter (incl. inactive +
        /// prefab-template) that has a renderable, non-junk, non-Combined mesh WITH a material, and reports how many
        /// distinct props (by content key) exist in the SCENE vs on PREFAB-TEMPLATES (which the curator currently
        /// excludes via scene.IsValid). Answers "how many more props can we offer if we bypass static batching".</summary>
        internal static void MeshPool()
        {
            try
            {
                var sceneKeys = new HashSet<string>(System.StringComparer.Ordinal);
                var templateKeys = new HashSet<string>(System.StringComparer.Ordinal);
                int batchedSkipped = 0;
                var filters = Resources.FindObjectsOfTypeAll<MeshFilter>();
                if (filters != null)
                    for (int i = 0; i < filters.Length; i++)
                    {
                        var mf = filters[i];
                        if (mf == null || mf.sharedMesh == null) continue;
                        var mn = mf.sharedMesh.name;
                        if (!string.IsNullOrEmpty(mn) && mn.IndexOf("Combined Mesh", System.StringComparison.OrdinalIgnoreCase) >= 0) { batchedSkipped++; continue; }
                        if (PropCatalog.IsJunkMeshName(mn)) continue;
                        if (mf.sharedMesh.vertexCount < 40) continue;
                        var mr = mf.GetComponent<MeshRenderer>();
                        if (mr == null || mr.sharedMaterial == null) continue;
                        string key = PropCatalog.MeshKey(mf.sharedMesh);
                        if (key == null) continue;
                        if (mf.gameObject.scene.IsValid()) sceneKeys.Add(key);
                        else templateKeys.Add(key);   // prefab/template - currently EXCLUDED by the curator
                    }
                var union = new HashSet<string>(sceneKeys, System.StringComparer.Ordinal);
                union.UnionWith(templateKeys);
                int templateOnly = 0;
                foreach (var k in templateKeys) if (!sceneKeys.Contains(k)) templateOnly++;
                Core.Log.Msg($"[PropHunt] phmeshpool: distinct clean props (mesh+material, verts>=40, non-batched) - " +
                             $"SCENE={sceneKeys.Count}  TEMPLATE={templateKeys.Count} (template-ONLY, currently excluded={templateOnly})  UNION={union.Count}  | skipped {batchedSkipped} batched MeshFilters.");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phmeshpool failed: " + e.Message); }
        }

        /// <summary>phprobesources: log the runtime shape of both databases + answer the open UNCERTAINs
        /// (do prefabs carry a LODGroup, is VehiclePrefabs populated, is MeshesToCull non-empty) before relying on them.</summary>
        internal static void Probe()
        {
            try
            {
                var reg = Registry.Instance;
                Core.Log.Msg($"[PropHunt] probe: Registry.Instance = {(reg != null ? "OK" : "NULL")}");
                if (reg != null)
                {
                    var items = reg.GetAllItems();
                    int total = items != null ? items.Count : 0;
                    int build = 0, withMesh = 0, withLod = 0, withCull = 0, withBox = 0, shown = 0;
                    if (items != null)
                        for (int i = 0; i < items.Count; i++)
                        {
                            var b = items[i] != null ? items[i].TryCast<BuildableItemDefinition>() : null;
                            if (b == null) continue;
                            build++;
                            var built = b.BuiltItem;
                            if (built == null || built.gameObject == null) continue;
                            var root = built.gameObject;
                            bool mesh = root.GetComponentInChildren<MeshRenderer>(true) != null;
                            bool lod = root.GetComponentInChildren<LODGroup>(true) != null;
                            int cull = 0; try { var mc = built.MeshesToCull; cull = mc != null ? mc.Count : 0; } catch { }
                            bool box = false; try { box = built.BoundingCollider != null; } catch { }
                            if (mesh) withMesh++; if (lod) withLod++; if (cull > 0) withCull++; if (box) withBox++;
                            if (shown++ < 10) Core.Log.Msg($"[PropHunt]   build '{items[i].name}' mesh={mesh} lod={lod} meshesToCull={cull} box={box}");
                        }
                    Core.Log.Msg($"[PropHunt] probe BUILDABLES: {total} items, {build} buildable | withMesh={withMesh} withLOD={withLod} withMeshesToCull={withCull} withBoundingBox={withBox}");
                }

                var vm = VehicleManager.Instance;
                Core.Log.Msg($"[PropHunt] probe: VehicleManager.Instance = {(vm != null ? "OK" : "NULL")}");
                if (vm != null)
                {
                    var p = vm.VehiclePrefabs;
                    int n = p != null ? p.Count : 0, vlod = 0, vbox = 0;
                    if (p != null)
                        for (int i = 0; i < p.Count; i++)
                        {
                            var v = p[i];
                            if (v == null || v.gameObject == null) continue;
                            bool lod = v.gameObject.GetComponentInChildren<LODGroup>(true) != null;
                            if (lod) vlod++;
                            bool box = false; try { box = v.boundingBox != null; } catch { }
                            if (box) vbox++;
                            string code = null; try { code = v.VehicleCode; } catch { }
                            Core.Log.Msg($"[PropHunt]   veh '{(string.IsNullOrEmpty(code) ? v.gameObject.name : code)}' lod={lod} box={box}");
                        }
                    Core.Log.Msg($"[PropHunt] probe VEHICLES: {n} prefabs | withLOD={vlod} withBoundingBox={vbox}");
                }

                var world = EnumerateWorldObjects();
                Core.Log.Msg($"[PropHunt] probe WORLD OBJECTS: {world.Count} distinct interactive/functional objects (deduped by content).");
                for (int i = 0; i < world.Count && i < 20; i++)
                    Core.Log.Msg($"[PropHunt]   world '{world[i].Name}' ({world[i].Key})");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] probe failed: " + e.Message); }
        }
#endif
    }
}
