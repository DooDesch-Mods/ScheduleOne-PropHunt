using UnityEngine;
using Object = UnityEngine.Object;

namespace PropHunt.Disguise
{
    /// <summary>
    /// Builds a render-only clone of a catalog prop. When the source prop has a <see cref="LODGroup"/>, the WHOLE
    /// LOD hierarchy is Instantiated - Unity re-wires the LODGroup's per-level renderer arrays to the clones - so
    /// the disguise carries the SAME LODs as the real prop and switches at the same distances (hunters can't
    /// distance-test the hider by watching LOD pops). Props without a LODGroup get a single mesh + materials clone.
    ///
    /// CRITICAL - why the Instantiate runs under an INACTIVE holder: Instantiate deep-copies the prop's real
    /// MonoBehaviours (storage/interactable/FishNet) and would run their Awake() during the copy - some (e.g.
    /// StorageEntityInteractable) NRE and hang the host, and their colliders/interactables would block the player's
    /// own interactions. A clone instantiated under an inactive parent is inactive-in-hierarchy, so NO Awake fires;
    /// we DestroyImmediate every script/collider while it is still inactive, then detach + activate. With the
    /// scripts already gone, activation runs no game logic - only the visual LODGroup/MeshRenderers remain. The real
    /// world prop is never modified.
    /// </summary>
    internal static class PropClone
    {
        internal static GameObject Build(PropEntry e, string name)
        {
            if (e == null) return null;
            try
            {
                if (e.CloneWholeRoot && e.SourceRoot != null) return BuildSubtree(e, name);
                return e.SourceLodGroup != null ? BuildLod(e, name) : BuildSingle(e, name);
            }
            catch (System.Exception ex) { Core.LogDebug("[PropHunt] PropClone.Build failed: " + ex.Message); return null; }
        }

        /// <summary>Clone an ENTIRE composite prefab subtree (a whole vehicle / buildable with all its parts + any
        /// nested LODGroups). Same inactive-holder + Strip discipline as <see cref="BuildLod"/> so no game script,
        /// FishNet behaviour, WheelCollider or Rigidbody ever runs - only the visual meshes/LODGroups survive. Every
        /// nested LODGroup is rebounded so its LOD switching keeps working. Falls back to LOD/single on failure.</summary>
        private static GameObject BuildSubtree(PropEntry e, string name)
        {
            var src = e.SourceRoot;
            if (src == null) return e.SourceLodGroup != null ? BuildLod(e, name) : BuildSingle(e, name);

            GameObject holder = null;
            try
            {
                holder = new GameObject("ph_holder");
                holder.SetActive(false);                          // clone stays inactive -> no Awake on the copied scripts
                var go = Object.Instantiate(src, holder.transform);
                go.name = name;
                Strip(go);                                        // remove scripts/colliders/Rigidbody/FishNet BEFORE activation
                go.transform.SetParent(null, false);
                go.SetActive(true);
                EnableAllVisuals(go);
                return go;
            }
            catch (System.Exception ex)
            {
                Core.LogDebug("[PropHunt] BuildSubtree failed: " + ex.Message);
                return e.SourceLodGroup != null ? BuildLod(e, name) : BuildSingle(e, name);
            }
            finally
            {
                if (holder != null) { try { Object.Destroy(holder); } catch { } }
            }
        }

        /// <summary>World AABB from the clone's MESH ASSET bounds (every MeshFilter's sharedMesh.bounds corners,
        /// taken through the CURRENT transform matrices). Used for per-frame re-centring: unlike
        /// <see cref="TryGetWorldBounds"/> (Renderer.bounds) it is never a frame stale (Renderer.bounds is refreshed
        /// during render culling, AFTER LateUpdate) and is LOD-active-independent (it does not include zeroed bounds
        /// from disabled LOD renderers) - both of which otherwise left the prop off-centre / trailing. Same basis as
        /// the hitbox (<see cref="TryGetLocalBounds"/>), so the visible prop and its hitbox stay aligned.</summary>
        internal static bool TryGetWorldMeshBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            if (go == null) return false;
            var mfs = go.GetComponentsInChildren<MeshFilter>(true);
            if (mfs == null) return false;
            bool any = false;
            for (int i = 0; i < mfs.Length; i++)
            {
                var mf = mfs[i];
                if (mf == null || mf.sharedMesh == null) continue;
                if (PropCatalog.IsJunkMeshName(mf.sharedMesh.name)) continue;   // skip trigger/collider/proxy meshes
                // ONLY the currently-VISIBLE meshes: a mesh with no MeshRenderer (collision/culling proxy) or whose
                // renderer is disabled (an inactive LOD level) must be skipped - those carry huge or stale bounds
                // that blew the union up to hundreds of metres and flung the prop off-screen.
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled || !mr.gameObject.activeInHierarchy) continue;
                var mt = mf.transform;
                var mb = mf.sharedMesh.bounds;
                Vector3 c = mb.center, e = mb.extents;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 w = mt.TransformPoint(c + new Vector3(sx * e.x, sy * e.y, sz * e.z));
                            if (!any) { bounds = new Bounds(w, Vector3.zero); any = true; }
                            else bounds.Encapsulate(w);
                        }
            }
            return any;
        }

        /// <summary>World AABB of a built clone: the root MeshRenderer for a single-mesh clone, else the union of
        /// the LOD clone's child renderers. False if it has no renderers.</summary>
        internal static bool TryGetWorldBounds(GameObject go, out Bounds bounds)
        {
            bounds = default;
            if (go == null) return false;
            var self = go.GetComponent<MeshRenderer>();
            if (self != null) { bounds = self.bounds; return true; }
            var rends = go.GetComponentsInChildren<Renderer>(true);
            if (rends == null || rends.Length == 0) return false;
            bool any = false;
            for (int i = 0; i < rends.Length; i++)
            {
                if (rends[i] == null) continue;
                if (!any) { bounds = rends[i].bounds; any = true; }
                else bounds.Encapsulate(rends[i].bounds);
            }
            return any;
        }

        /// <summary>Add a single TRIGGER BoxCollider sized to the clone's bounds, so a hunter's catch ray resolves
        /// a disguised player by hitting the PROP volume (big prop = big hitbox, small = tight). Trigger = never
        /// blocks movement; rays hit triggers by default. Bounds are computed in the clone's LOCAL space so the box
        /// stays ORIENTED WITH the prop - using a world-AABB here produces a twisted box on a rotated/flat prop.</summary>
        internal static BoxCollider AddTriggerHitbox(GameObject go)
        {
            if (go == null) return null;
            try
            {
                if (!TryGetPropLocalBounds(go, out Bounds lb)) return null;
                var bc = go.AddComponent<BoxCollider>();
                bc.isTrigger = true;
                bc.center = lb.center;   // already in the clone root's local space
                bc.size = lb.size;
                return bc;
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] AddTriggerHitbox failed: " + e.Message); return null; }
        }

        /// <summary>Bounds of the clone's meshes expressed in the ROOT's local space (every mesh corner taken to
        /// world, then back into root-local). A BoxCollider built from this is oriented along the prop's own axes,
        /// so a rotated or flat prop gets a matching box instead of a twisted world-AABB one.</summary>
        internal static bool TryGetLocalBounds(GameObject root, out Bounds local)
        {
            local = default;
            if (root == null) return false;
            var rt = root.transform;
            var mfs = root.GetComponentsInChildren<MeshFilter>(true);
            if (mfs == null) return false;
            bool any = false;
            for (int i = 0; i < mfs.Length; i++)
            {
                var mf = mfs[i];
                if (mf == null || mf.sharedMesh == null) continue;
                if (PropCatalog.IsJunkMeshName(mf.sharedMesh.name)) continue;   // skip trigger/collider/proxy meshes
                // only currently-visible meshes (skip non-rendered proxy meshes + inactive LOD levels, whose mesh
                // bounds otherwise blow the box up to hundreds of metres)
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || !mr.enabled || !mr.gameObject.activeInHierarchy) continue;
                var mt = mf.transform;
                var mb = mf.sharedMesh.bounds;   // local to the mesh's own transform
                Vector3 c = mb.center, e = mb.extents;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 world = mt.TransformPoint(c + new Vector3(sx * e.x, sy * e.y, sz * e.z));
                            Vector3 rl = rt.InverseTransformPoint(world);
                            if (!any) { local = new Bounds(rl, Vector3.zero); any = true; }
                            else local.Encapsulate(rl);
                        }
            }
            return any;
        }

        /// <summary>World AABB of a fixed ROOT-LOCAL <paramref name="local"/> box under <paramref name="t"/>'s current
        /// pose (its 8 corners through t.TransformPoint). Lets the disguise positioner reuse a bounds captured ONCE at
        /// build time (when correct) instead of re-querying renderer/mesh bounds every frame (which a LOD hierarchy
        /// reports as a huge, fluctuating box once it evaluates).</summary>
        internal static Bounds LocalToWorldBounds(Transform t, Bounds local)
        {
            Vector3 c = local.center, e = local.extents;
            Bounds w = default; bool any = false;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 p = t.TransformPoint(c + new Vector3(sx * e.x, sy * e.y, sz * e.z));
                        if (!any) { w = new Bounds(p, Vector3.zero); any = true; }
                        else w.Encapsulate(p);
                    }
            return w;
        }

        /// <summary>Root-local bounds of JUST THE PROP - the meshes that are actually part of the LODGroup's LOD
        /// levels (via <c>GetLODs()</c>), not whatever else got cloned alongside. A prop's LODGroup hierarchy can
        /// contain an unrelated sibling mesh (huge/far) that <see cref="TryGetLocalBounds"/> would wrongly include,
        /// blowing the box up to hundreds of metres. Falls back to the all-visible-mesh bounds for non-LOD clones.</summary>
        internal static bool TryGetPropLocalBounds(GameObject root, out Bounds local)
        {
            local = default;
            if (root == null) return false;
            var rt = root.transform;
            bool any = false;

            // Mirror TryGetWorldBounds' ROOT short-circuit: if the root itself carries the (enabled) mesh, use ONLY
            // it. A prop's cloned hierarchy can contain an unrelated FAR sibling mesh that DOES have an enabled
            // renderer (so it survives every "visible mesh" filter) - including it blew the box up to ~269m and
            // flung the prop. Renderer.bounds short-circuits on the root for exactly this reason; we match it.
            var rootMr = root.GetComponent<MeshRenderer>();
            var rootMf = root.GetComponent<MeshFilter>();
            if (rootMr != null && rootMr.enabled && rootMr.gameObject.activeInHierarchy && rootMf != null && rootMf.sharedMesh != null)
            {
                EncapsulateMeshLocal(rootMf, rt, ref local, ref any);
                if (any) return true;
            }

            // True multi-LOD-on-children clone (no root mesh): only the currently-VISIBLE child renderers (the
            // active LOD), skipping disabled/stale LOD levels and non-rendered proxy meshes.
            var mrs = root.GetComponentsInChildren<MeshRenderer>(true);
            if (mrs != null)
                for (int i = 0; i < mrs.Length; i++)
                {
                    var mr = mrs[i];
                    if (mr == null || !mr.enabled || !mr.gameObject.activeInHierarchy) continue;
                    var mf = mr.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    EncapsulateMeshLocal(mf, rt, ref local, ref any);
                }
            return any;
        }

        /// <summary>Root-local bounds of JUST THIS PROP'S source mesh, computed from the SOURCE asset (never the
        /// cloned hierarchy). Maps the source mesh's local bounds into the source root's local space (= the clone
        /// root's local space, since the clone replicates the source hierarchy 1:1). This uses only the one mesh,
        /// so an unrelated sibling mesh in the prop's prefab can never pollute it (the bug that gave a 269m box).</summary>
        internal static bool TryGetSourceLocalBounds(PropEntry e, out Bounds local)
        {
            local = default;
            if (e == null || e.Source == null) return false;
            var mesh = e.Source.sharedMesh;
            if (mesh == null) return false;
            var rootT = (e.SourceLodGroup != null) ? e.SourceLodGroup.transform : e.Source.transform;
            if (rootT == null) return false;
            try
            {
                Matrix4x4 m = rootT.worldToLocalMatrix * e.Source.transform.localToWorldMatrix;   // mesh-local -> root-local
                var mb = mesh.bounds;
                Vector3 c = mb.center, ex = mb.extents;
                bool any = false;
                for (int sx = -1; sx <= 1; sx += 2)
                    for (int sy = -1; sy <= 1; sy += 2)
                        for (int sz = -1; sz <= 1; sz += 2)
                        {
                            Vector3 p = m.MultiplyPoint3x4(c + new Vector3(sx * ex.x, sy * ex.y, sz * ex.z));
                            if (!any) { local = new Bounds(p, Vector3.zero); any = true; }
                            else local.Encapsulate(p);
                        }
                return any;
            }
            catch { return false; }
        }

        /// <summary>Root-local bounds of the WHOLE prop (all its meshes), computed from the SOURCE assets, but
        /// distance-culled around the LODGroup's reference point so an unrelated FAR sibling mesh in the prefab is
        /// excluded. This fixes multi-mesh props (e.g. Cash-for-Trash = panel + body + chute): TryGetSourceLocalBounds
        /// bounds only the ONE matched mesh, so the rest hung through the floor; this bounds every part of the prop
        /// while still ignoring the stray 100m+ sibling. Falls back to the single matched mesh.</summary>
        internal static bool TryGetPropBoundsFromSource(PropEntry e, out Bounds local)
        {
            local = default;
            if (e == null || e.Source == null || e.Source.sharedMesh == null) return false;
            // Whole composite (buildable/vehicle/world object): bound EVERY visible part in the SOURCE-ROOT's local
            // space, not just the single representative mesh. The rep mesh is often OFFSET from the prop root (e.g. a
            // TV bench's tabletop or a wall-mounted TV), and TryGetSourceLocalBounds anchors its box in the REP mesh's
            // OWN space - applied at the clone root, that mis-placed min.y and floated the prop off the ground. This
            // unions all parts anchored at the root, so grounding uses the true lowest visible point (the legs).
            if (e.CloneWholeRoot && e.SourceRoot != null && TryGetWholeRootBoundsFromSource(e, out local)) return true;
            if (e.SourceLodGroup == null) return TryGetSourceLocalBounds(e, out local);   // single-mesh prop
            try
            {
                var lg = e.SourceLodGroup;
                var rootT = lg.transform;
                Vector3 refP = lg.localReferencePoint;
                float size = lg.size; if (size < 0.01f) size = 4f;
                float cull = Mathf.Max(size * 2f + 2f, 5f);   // a mesh beyond this from the LOD reference point is an unrelated sibling
                float cull2 = cull * cull;
                var mfs = lg.GetComponentsInChildren<MeshFilter>(true);
                bool any = false;
                if (mfs != null)
                    for (int i = 0; i < mfs.Length; i++)
                    {
                        var mf = mfs[i];
                        if (mf == null || mf.sharedMesh == null) continue;
                        if (mf.GetComponent<MeshRenderer>() == null) continue;                 // skip non-rendered proxies
                        if (PropCatalog.IsJunkMeshName(mf.sharedMesh.name)) continue;
                        Vector3 cl = rootT.InverseTransformPoint(mf.transform.TransformPoint(mf.sharedMesh.bounds.center));
                        if ((cl - refP).sqrMagnitude > cull2) continue;                        // far sibling -> exclude
                        EncapsulateMeshLocal(mf, rootT, ref local, ref any);
                    }
                if (any) return true;
            }
            catch (System.Exception ex) { Core.LogDebug("[PropHunt] prop bounds (source) failed: " + ex.Message); }
            return TryGetSourceLocalBounds(e, out local);
        }

        /// <summary>Root-local bounds of a WHOLE-ROOT composite (buildable/vehicle/world object) from its SOURCE
        /// meshes, expressed in the SourceRoot's own local space (= the clone root's space, since the clone
        /// replicates the root 1:1). Unions EVERY visible source mesh so a multi-part item grounds on its true
        /// lowest point (a bench's legs, not just the offset tabletop that <see cref="TryGetSourceLocalBounds"/>
        /// bounded from the rep mesh's own frame). Excludes collider/junk proxies, the placement-visualization nodes
        /// (grid FootprintTile/TileAppearance + the ActivateDuringBuild build-arrow - still in the SOURCE even though
        /// the clone strips them, and their ground-level footprint would drag min.y below the real base), and any
        /// stray mesh far from the prop body (the 269m-box guard, referenced off the representative mesh).</summary>
        internal static bool TryGetWholeRootBoundsFromSource(PropEntry e, out Bounds local)
        {
            local = default;
            if (e == null || e.SourceRoot == null) return false;
            try
            {
                var rootT = e.SourceRoot.transform;
                var mfs = e.SourceRoot.GetComponentsInChildren<MeshFilter>(true);
                if (mfs == null) return false;

                // reference + cull radius from the representative (biggest) mesh: a real part can't be more than a
                // few prop-sizes from it, but an unrelated far sibling in the prefab can - exclude those.
                bool haveRef = false; Vector3 refP = Vector3.zero; float cull2 = 0f;
                if (e.Source != null && e.Source.sharedMesh != null)
                {
                    refP = rootT.InverseTransformPoint(e.Source.transform.TransformPoint(e.Source.sharedMesh.bounds.center));
                    var ext = e.Source.sharedMesh.bounds.extents;
                    float repSize = Mathf.Max(ext.x, Mathf.Max(ext.y, ext.z)) * 2f;
                    float cull = Mathf.Max(repSize * 3f + 2f, 6f);
                    cull2 = cull * cull; haveRef = true;
                }

                bool any = false;
                for (int i = 0; i < mfs.Length; i++)
                {
                    var mf = mfs[i];
                    if (mf == null || mf.sharedMesh == null) continue;
                    var mr = mf.GetComponent<MeshRenderer>();
                    if (mr == null || mr.sharedMaterial == null) continue;              // non-visual proxy
                    if (PropCatalog.IsJunkMeshName(mf.sharedMesh.name)) continue;
                    if (IsPlacementVizMesh(mf)) continue;                               // grid tiles / build arrow
                    Vector3 cl = rootT.InverseTransformPoint(mf.transform.TransformPoint(mf.sharedMesh.bounds.center));
                    if (haveRef && (cl - refP).sqrMagnitude > cull2) continue;          // stray far mesh
                    EncapsulateMeshLocal(mf, rootT, ref local, ref any);
                }
                return any;
            }
            catch (System.Exception ex) { Core.LogDebug("[PropHunt] whole-root bounds failed: " + ex.Message); return false; }
        }

        /// <summary>True if a source MeshFilter belongs to a placement-only visualization (the grid FootprintTile/
        /// TileAppearance squares or an ActivateDuringBuild build-arrow/indicator) - present in the buildable's
        /// SOURCE prefab even though <see cref="Strip"/> removes them from the clone, so bounds must skip them too.
        /// Matched by component (not by "Indicator" name) so a vehicle's always-on turn-signal meshes are kept.</summary>
        private static bool IsPlacementVizMesh(MeshFilter mf)
        {
            try
            {
                if (mf.GetComponentInParent<Il2CppScheduleOne.Tiles.FootprintTile>() != null) return true;
                if (mf.GetComponentInParent<Il2CppScheduleOne.Tiles.TileAppearance>() != null) return true;
                if (mf.GetComponentInParent<Il2CppScheduleOne.Building.ActivateDuringBuild>() != null) return true;
                if (mf.GetComponentInParent<Il2CppScheduleOne.Building.OverrideGhostMaterial>() != null) return true;   // build-only "yellow box" overlay - keeps the jukebox from floating (its low mesh dragged min.y down)
            }
            catch { }
            return false;
        }

        /// <summary>Add a trigger hitbox from a pre-computed ROOT-LOCAL bounds (so the hitbox matches the disguise's
        /// positioning bounds exactly).</summary>
        internal static BoxCollider AddTriggerHitbox(GameObject go, Bounds localBounds)
        {
            if (go == null) return null;
            try
            {
                var bc = go.AddComponent<BoxCollider>();
                bc.isTrigger = true;
                bc.center = localBounds.center;
                bc.size = localBounds.size;
                return bc;
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] AddTriggerHitbox(bounds) failed: " + e.Message); return null; }
        }

        private static void EncapsulateMeshLocal(MeshFilter mf, Transform rt, ref Bounds local, ref bool any)
        {
            var mt = mf.transform;
            var mb = mf.sharedMesh.bounds;
            Vector3 c = mb.center, e = mb.extents;
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sy = -1; sy <= 1; sy += 2)
                    for (int sz = -1; sz <= 1; sz += 2)
                    {
                        Vector3 rl = rt.InverseTransformPoint(mt.TransformPoint(c + new Vector3(sx * e.x, sy * e.y, sz * e.z)));
                        if (!any) { local = new Bounds(rl, Vector3.zero); any = true; }
                        else local.Encapsulate(rl);
                    }
        }

        /// <summary>
        /// Clone the full LODGroup hierarchy (true 1:1 LOD switching) without ever running the prop's game scripts:
        /// Instantiate under an inactive holder so no Awake fires, strip scripts/colliders while inactive, then
        /// detach + activate. Falls back to a single mesh if anything goes wrong.
        /// </summary>
        private static GameObject BuildLod(PropEntry e, string name)
        {
            var src = e.SourceLodGroup != null ? e.SourceLodGroup.gameObject : null;
            if (src == null) return BuildSingle(e, name);

            GameObject holder = null;
            try
            {
                holder = new GameObject("ph_holder");
                holder.SetActive(false);                          // clone instantiated under this stays inactive -> no Awake
                var go = Object.Instantiate(src, holder.transform);
                go.name = name;
                Strip(go);                                        // DestroyImmediate scripts/colliders BEFORE activation
                go.transform.SetParent(null, false);              // detach into the world (the caller re-parents to the player)
                go.SetActive(true);
                EnableAllVisuals(go);
                return go;
            }
            catch (System.Exception ex)
            {
                Core.LogDebug("[PropHunt] BuildLod failed: " + ex.Message);
                return BuildSingle(e, name);
            }
            finally
            {
                if (holder != null) { try { Object.Destroy(holder); } catch { } }
            }
        }

        /// <summary>Make every visual in a freshly-built clone show, even if the SOURCE was distance-culled
        /// (SetActive(false)) when it was cloned - which yielded a blank preview for far world objects/interiors.
        /// Activates all descendant GameObjects and enables MeshRenderers NOT managed by a LODGroup (LODGroup
        /// renderers are left to the group, which enables the correct LOD at the clone's distance), then rebounds
        /// each LODGroup. Only ever adds visibility - the disguise/preview never renders less than the real prop.</summary>
        private static void EnableAllVisuals(GameObject go)
        {
            try
            {
                var trs = go.GetComponentsInChildren<Transform>(true);
                if (trs != null) for (int i = 0; i < trs.Length; i++)
                { try { if (trs[i] != null && !trs[i].gameObject.activeSelf) trs[i].gameObject.SetActive(true); } catch { } }

                // Regenerate any kept text meshes (a sign's runtime-set label): the clone was built inactive, so TMP
                // never generated its mesh; now that the objects are active, force it so the text shows on the disguise.
                var tmps = go.GetComponentsInChildren<Il2CppTMPro.TMP_Text>(true);
                if (tmps != null) for (int i = 0; i < tmps.Length; i++)
                { try { if (tmps[i] != null) tmps[i].ForceMeshUpdate(false, false); } catch { } }

                // Deliberately do NOT blanket-enable disabled renderers. A renderer left DISABLED in the source is an
                // intentional PROXY - an interaction/collision-visualiser box (the "Cube" on an ATM / mailbox / vehicle);
                // force-enabling those spawned a stray white box over the prop. A distance-CULLED VISUAL instead keeps
                // renderer.enabled=true and only had its GameObject SetActive(false) - the sweep above already restores
                // it. So SetActive is enough for real visuals, and proxies correctly stay hidden.

                // Force each LODGroup to its highest-detail level. A clone captured while its source was far/culled
                // otherwise has no guaranteed path back to visible (RecalculateBounds alone never re-enables a
                // renderer), which was leaving LOD props blank. LOD0 is the visual (never a proxy), so this shows the
                // prop without the white-box side-effect. Guaranteed-visible beats matching the real prop's LOD-pop.
                var lgs = go.GetComponentsInChildren<LODGroup>(true);
                if (lgs != null) for (int i = 0; i < lgs.Length; i++)
                {
                    var lg = lgs[i];
                    if (lg == null) continue;
                    try { lg.RecalculateBounds(); lg.ForceLOD(0); } catch { }
                }
            }
            catch { }
        }

        private static GameObject BuildSingle(PropEntry e, string name)
        {
            if (e.Source == null || e.Source.sharedMesh == null) return null;
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = e.Source.sharedMesh;
            var mr = go.AddComponent<MeshRenderer>();
            if (e.SourceRenderer != null)
            {
                mr.sharedMaterials = e.SourceRenderer.sharedMaterials;
                // match the real prop's shadow behaviour - a shadowless disguise is an instant tell.
                mr.shadowCastingMode = e.SourceRenderer.shadowCastingMode;
                mr.receiveShadows = e.SourceRenderer.receiveShadows;
            }
            return go;
        }

        /// <summary>
        /// Strip everything that isn't pure visuals from an instantiated prop clone, using DestroyImmediate so the
        /// removal happens BEFORE the clone is activated (a deferred Destroy would leave the scripts alive long
        /// enough for their Awake/OnEnable to fire on activation). Safe here: the clone is inactive and detached.
        /// LODGroup / MeshFilter / MeshRenderer / Transform are NOT MonoBehaviours, so they are kept.
        /// </summary>
        private static void Strip(GameObject go)
        {
            try { go.isStatic = false; } catch { }

            // A buildable prop carries the game's PLACEMENT-ONLY visuals, which must never show on a disguise:
            //   (a) FootprintTile/TileAppearance nodes = the white grid squares. Vanilla "hides" them by
            //       MeshRenderer.enabled=false while leaving the GameObjects ACTIVE, so they survive the
            //       MonoBehaviour sweep below and EnableAllVisuals would re-show them.
            //   (b) ActivateDuringBuild-marked nodes = the blue front/direction arrow + L/R indicators. Vanilla
            //       shows these with GameObject.SetActive(true) during placement and SetActive(false) on a placed
            //       item; our EnableAllVisuals SetActive-sweep re-activates them, so the arrow reappears.
            // Destroy both node families up front (before the sweep + before activation). The ActivateDuringBuild
            // marker is exact - a vehicle's always-on turn-signal "Indicator" meshes do NOT carry it, so they are
            // untouched (which is why we match the component, never the "Indicator" name). No-op for props that
            // carry neither (most scene props, vehicles, world objects).
            try
            {
                var fts = go.GetComponentsInChildren<Il2CppScheduleOne.Tiles.FootprintTile>(true);
                if (fts != null) for (int i = 0; i < fts.Length; i++)
                { try { if (fts[i] != null) Object.DestroyImmediate(fts[i].gameObject); } catch { } }
                // belt-and-suspenders: kill any TileAppearance node not nested under a FootprintTile we just removed.
                var tas = go.GetComponentsInChildren<Il2CppScheduleOne.Tiles.TileAppearance>(true);
                if (tas != null) for (int i = 0; i < tas.Length; i++)
                { try { if (tas[i] != null) Object.DestroyImmediate(tas[i].gameObject); } catch { } }
                // the build-only direction arrow / indicators (marker-only MonoBehaviour, no fields).
                var adb = go.GetComponentsInChildren<Il2CppScheduleOne.Building.ActivateDuringBuild>(true);
                if (adb != null) for (int i = 0; i < adb.Length; i++)
                { try { if (adb[i] != null) Object.DestroyImmediate(adb[i].gameObject); } catch { } }
                // (c) build-only coloured placement overlays (the "yellow box" on e.g. MixingStationMk2): a mesh flagged
                //     OverrideGhostMaterial keeps its authored material during placement (BuildManager skips recolouring
                //     it) and is INACTIVE in the placed prefab, so the EnableAllVisuals activation sweep would un-hide it.
                var ogm = go.GetComponentsInChildren<Il2CppScheduleOne.Building.OverrideGhostMaterial>(true);
                if (ogm != null) for (int i = 0; i < ogm.Length; i++)
                { try { if (ogm[i] != null) Object.DestroyImmediate(ogm[i].gameObject); } catch { } }
            }
            catch { }

            var ts = go.GetComponentsInChildren<Transform>(true);
            if (ts != null) for (int i = 0; i < ts.Length; i++)
            {
                if (ts[i] == null) continue;
                try { ts[i].gameObject.isStatic = false; } catch { }
                // a world prop may carry one of OUR highlight shells (ph_) as a child; Instantiate deep-copies it
                // into the clone, making the disguise glow for the hider. Remove any such injected child.
                var n = ts[i].name;
                if (ts[i] != go.transform && !string.IsNullOrEmpty(n) && n.StartsWith("ph_"))
                { try { Object.DestroyImmediate(ts[i].gameObject); } catch { } }
            }

            var cols = go.GetComponentsInChildren<Collider>(true);
            if (cols != null) for (int i = 0; i < cols.Length; i++) { try { if (cols[i] != null) Object.DestroyImmediate(cols[i]); } catch { } }
            var rbs = go.GetComponentsInChildren<Rigidbody>(true);
            if (rbs != null) for (int i = 0; i < rbs.Length; i++) { try { if (rbs[i] != null) Object.DestroyImmediate(rbs[i]); } catch { } }
            var auds = go.GetComponentsInChildren<AudioSource>(true);
            if (auds != null) for (int i = 0; i < auds.Length; i++) { try { if (auds[i] != null) Object.DestroyImmediate(auds[i]); } catch { } }
            var lts = go.GetComponentsInChildren<Light>(true);
            if (lts != null) for (int i = 0; i < lts.Length; i++) { try { if (lts[i] != null) Object.DestroyImmediate(lts[i]); } catch { } }
            // every game script + FishNet NetworkBehaviour (all derive from MonoBehaviour). Destroy each while the
            // clone is inactive so its Awake never runs.
            var mbs = go.GetComponentsInChildren<MonoBehaviour>(true);
            if (mbs != null) for (int i = 0; i < mbs.Length; i++)
            {
                try
                {
                    var mb = mbs[i];
                    if (mb == null) continue;
                    // Keep text renderers (a prop's runtime-set label, e.g. the roadsign's "Curfew tonight ..."): TMP is
                    // a MonoBehaviour, so the blanket sweep would destroy it. It carries no networking / game-logic Awake,
                    // and the clone activates it afterwards so the text mesh regenerates. See EnableAllVisuals.
                    if (mb.TryCast<Il2CppTMPro.TMP_Text>() != null) continue;
                    Object.DestroyImmediate(mb);
                }
                catch { }
            }
        }
    }
}
