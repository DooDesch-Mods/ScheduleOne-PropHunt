using System.Collections.Generic;
using UnityEngine;
using Il2CppScheduleOne.AvatarFramework;
using PropHunt.Game;

namespace PropHunt.Disguise
{
    /// <summary>
    /// LOCAL on every client: render each disguised player as their synced prop. Hides the player's
    /// third-person body and parents a render-only clone of the prop mesh to their transform; the prop then
    /// rides the game's already-replicated player transform, so no transform networking is needed. Driven
    /// from the synced <see cref="GameState"/> each tick; idempotent (only rebuilds when a player's prop id
    /// changes).
    /// </summary>
    internal sealed class DisguiseController
    {
        private readonly Dictionary<ulong, GameObject> _props = new Dictionary<ulong, GameObject>();
        private readonly Dictionary<ulong, int> _appliedPropId = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, Quaternion> _sourceRot = new Dictionary<ulong, Quaternion>();   // each prop's source world orientation
        private readonly Dictionary<ulong, CharacterController> _cc = new Dictionary<ulong, CharacterController>();   // cached collision capsule per player (the live anchor)
        private readonly Dictionary<ulong, float> _yaw = new Dictionary<ulong, float>();   // smoothed render yaw (remotes lerp toward the synced value)
        private readonly Dictionary<ulong, Bounds> _localBounds = new Dictionary<ulong, Bounds>();   // clone's local bounds captured ONCE at build (stable; avoids per-frame LOD bounds garbage)
        // live hiders are rendered NAKED (underwear-only) underneath their prop: when undisguised that bare body
        // shows, when disguised it is hidden behind the prop. We cache each one's original appearance to restore.
        private readonly Dictionary<ulong, AvatarSettings> _nakedOriginal = new Dictionary<ulong, AvatarSettings>();
        private readonly HashSet<ulong> _naked = new HashSet<ulong>();
        private readonly HashSet<ulong> _warnedUnresolved = new HashSet<ulong>();   // log "can't resolve" once per player, not per frame
        private const float HiderScale = 0.7f;   // an undisguised hider's HUMAN model renders at 70% so hunters can tell them from a full-size hunter
        private bool _warnedHashMismatch;

        internal void Apply(GameState state)
        {
            if (state == null) return;
            try
            {
                // A prop-catalog hash mismatch (host vs local) is the surviving "disguises may differ" check; the
                // broader "everyone must be on the same build" version check now lives in Side Hustle at the join layer.
                if (!_warnedHashMismatch && state.CatalogHash != 0 && state.CatalogHash != PropCatalog.Hash)
                {
                    _warnedHashMismatch = true;
                    Core.Log.Warning($"[PropHunt] prop-catalog hash mismatch (host {state.CatalogHash} vs local {PropCatalog.Hash}) - disguises may differ.");
                }

                PlayerRegistry.Refresh();
                ulong localId = Net.PropHuntNet.LocalSteamId;
                var localPlayer = Player.Local;
                // disguises only exist DURING a round (hiding + hunting). In the safehouse lobby / round-end /
                // match-end, every hider is rendered NORMALLY (full body, no prop) - otherwise a hider's body stays
                // hidden behind a (removed) prop and they are invisible to others in the safehouse.
                bool inRound = state.Phase == RoundPhase.Hiding || state.Phase == RoundPhase.Hunting;

                foreach (var ps in state.Players.Values)
                {
                    // the LOCAL player is resolved via the reliable Player.Local, NOT the best-effort name map
                    var player = (ps.SteamId == localId && localPlayer != null) ? localPlayer : PlayerRegistry.Get(ps.SteamId);
                    bool liveHider = ps.Role == PlayerRole.Hider && !ps.Eliminated && inRound;
                    bool disguised = liveHider && ps.PropId >= 0;
                    bool caughtOut = ps.Role == PlayerRole.Hider && ps.Eliminated && inRound;   // Spectator-caught: sit out invisibly (Infection makes them a Hunter, not this)
                    if (player == null)
                    {
                        // Log ONCE per unresolved player, not every frame. A crashed/disconnected client left as a
                        // hider in the synced roster (the host still lists them until the lobby drops them) otherwise
                        // floods the log every frame - observed at 360k lines, which also stalls the host via log I/O.
                        if (disguised && _warnedUnresolved.Add(ps.SteamId))
                            Core.Log.Warning($"[PropHunt] disguise: no game Player resolved for {ps.SteamId} (prop {ps.PropId}) - cannot render. " +
                                             "PlayerCode not yet replicated for this peer? (retries each tick)");
                        continue;
                    }
                    _warnedUnresolved.Remove(ps.SteamId);   // resolved now -> a future drop warns once again

                    // a caught hider (Spectator mode) sits out INVISIBLY on every client - not as a normal walking
                    // character (the old else-path restored their clothed body). Round-end (inRound=false) re-shows them.
                    if (caughtOut)
                    {
                        RemoveProp(ps.SteamId, player);
                        SetHiderScale(player, 1f);
                        SetBodyVisible(player, false, ps.SteamId == localId);
                        continue;
                    }

                    // naked FIRST (rebuilds the avatar -> body visible), then the prop hides the body if disguised
                    if (liveHider) EnsureNaked(ps.SteamId, player);
                    else RestoreAppearance(ps.SteamId, player);
                    if (disguised) EnsureProp(ps.SteamId, player, ps.PropId);
                    else RemoveProp(ps.SteamId, player);
                    // an undisguised hider's HUMAN body renders at 70% (so hunters can tell them from a full hunter);
                    // the disguised body is hidden anyway. Applied AFTER the avatar (re)build so a rebuild can't reset it.
                    SetHiderScale(player, liveHider ? HiderScale : 1f);
                }

                // tidy disguises + restore appearance for players gone from the snapshot
                List<ulong> stale = null;
                foreach (var id in _props.Keys)
                    if (!state.Players.TryGetValue(id, out var p) || p.PropId < 0 || p.Eliminated || p.Role != PlayerRole.Hider)
                        (stale ??= new List<ulong>()).Add(id);
                if (stale != null) foreach (var id in stale) RemoveProp(id, PlayerRegistry.Get(id));

                List<ulong> staleNaked = null;
                foreach (var id in _naked)
                    if (!state.Players.TryGetValue(id, out var p) || p.Role != PlayerRole.Hider || p.Eliminated)
                        (staleNaked ??= new List<ulong>()).Add(id);
                if (staleNaked != null) foreach (var id in staleNaked) RestoreAppearance(id, id == localId ? localPlayer : PlayerRegistry.Get(id));
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] disguise apply failed: " + e.Message); }
        }

        private void EnsureProp(ulong id, Player player, int propId)
        {
            if (_appliedPropId.TryGetValue(id, out var cur) && cur == propId && _props.ContainsKey(id)) return;
            RemoveProp(id, player);
            var e = PropCatalog.ById(propId);
            if (e == null || e.SourceRoot == null)
            {
                Core.LogDebug($"[PropHunt] disguise: prop id {propId} not renderable in local catalog (entry={(e == null ? "null" : e.Name)}) - count {PropCatalog.Count}");
                return;
            }
            try
            {
                bool isLocal = id == Net.PropHuntNet.LocalSteamId;
                SetBodyVisible(player, false, isLocal);

                // clone the REAL prop: the full LODGroup hierarchy when present (1:1 LOD switching), else a single
                // mesh. The clone is render-only (no colliders/scripts/interactables - see PropClone).
                var go = PropClone.Build(e, "ph_prop_" + id);
                if (go == null)
                {
                    Core.LogDebug($"[PropHunt] disguise: clone build returned null for '{e.Name}'");
                    SetBodyVisible(player, true, isLocal);
                    return;
                }

                // IMPORTANT: do NOT parent the clone to the player. UpdatePropTransform re-positions it every
                // LateUpdate; parenting it as WELL meant the player's movement was applied twice (the parent moved
                // it, then the recenter moved it again), so the prop mirrored across the player and slid at a
                // multiple of the player's speed. Decoys are world-space for the same reason and sit correctly.
                go.transform.SetParent(null, false);   // world space (BuildLod may leave it under a holder)
                // real-world size (player scale is ~1) so a vending machine stays its size
                go.transform.localScale = e.SourceRoot.transform.lossyScale;
                // base = the source prop's world orientation so sideways-authored meshes (e.g. the barrel) stand
                // correctly; UpdatePropTransform applies the hider's chosen yaw on top + re-centres/grounds it.
                _sourceRot[id] = e.SourceRoot.transform.rotation;
                go.transform.rotation = _sourceRot[id];
                try { go.transform.position = player.transform.position; } catch { }   // rough initial spot; LateApply re-centres this frame

                // prop-sized catch hitbox: a trigger collider matching the prop volume, so a hunter who shoots
                // the visible prop catches the hider (big prop = big hitbox). The player capsule stays as a
                // backstop. Trigger = never blocks movement. Local + remote alike (the clone sits under the player).
                // Bounds for BOTH positioning and the hitbox come from the SOURCE mesh asset (mapped into the
                // clone's local space), never the cloned hierarchy - so an unrelated sibling mesh in the prop's
                // prefab cannot pollute them (the 269m box that flung the prop). Captured once; transformed by the
                // live pose each frame.
                if (PropClone.TryGetPropBoundsFromSource(e, out var lb0))
                {
                    _localBounds[id] = lb0;
                    PropClone.AddTriggerHitbox(go, lb0);
                }
                else
                {
                    PropClone.AddTriggerHitbox(go);   // fallback to the clone-hierarchy box
                    if (PropClone.TryGetPropLocalBounds(go, out var lbF)) _localBounds[id] = lbF;
                }

                _props[id] = go;
                _appliedPropId[id] = propId;
                Core.LogDebug($"[PropHunt] disguise: applied '{e.Name}' to {id} (LOD={e.SourceLodGroup != null})");
            }
            catch (System.Exception ex) { Core.LogDebug("[PropHunt] EnsureProp failed: " + ex.Message); }
        }

        /// <summary>Runs in LATE update (after the player has moved/rotated this frame) so re-centring the props
        /// has no one-frame lag. The local player's facing uses <paramref name="localYaw"/> (responsive); remote
        /// players use their synced PropYaw.</summary>
        internal void LateApply(GameState state, ulong localId, float localYaw)
        {
            if (state == null) return;
            try
            {
                var localPlayer = Player.Local;
                foreach (var ps in state.Players.Values)
                {
                    if (ps.PropId < 0 || ps.Eliminated || ps.Role != PlayerRole.Hider) continue;
                    if (!_props.ContainsKey(ps.SteamId)) continue;
                    bool isLocal = ps.SteamId == localId;
                    var player = (isLocal && localPlayer != null) ? localPlayer : PlayerRegistry.Get(ps.SteamId);
                    UpdatePropTransform(ps.SteamId, player, isLocal ? localYaw : ps.PropYaw, isLocal);
                }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] disguise late-apply failed: " + e.Message); }
        }

        /// <summary>Per-frame transform upkeep for a disguise. ROTATION: world-fixed at the source orientation +
        /// the hider's chosen yaw - it does NOT follow the camera, so looking around never spins the prop ([F]+
        /// mouse changes the yaw). POSITION: re-centred EVERY frame so the mesh's bounding-box centre sits on the
        /// player (xz) with its base on the actual ground under them (y, via a downward raycast). Recomputing each
        /// frame is what stops the prop ORBITING/drifting off to the side when the player turns.</summary>
        private void UpdatePropTransform(ulong id, Player player, float yaw, bool isLocal)
        {
            if (player == null) return;
            if (!_props.TryGetValue(id, out var go) || go == null) return;

            Quaternion baseRot = _sourceRot.TryGetValue(id, out var sr) ? sr : Quaternion.identity;
            // Remote players' yaw arrives throttled over the network, so snapping to it looks jerky to a hunter -
            // smooth toward the synced target. The local owner uses its own yaw directly (responsive).
            float appliedYaw;
            if (isLocal) { appliedYaw = yaw; _yaw[id] = yaw; }
            else
            {
                float cur = _yaw.TryGetValue(id, out var y) ? y : yaw;
                appliedYaw = Mathf.LerpAngle(cur, yaw, Mathf.Clamp01(Time.deltaTime * 12f));
                _yaw[id] = appliedYaw;
            }
            go.transform.rotation = Quaternion.Euler(0f, appliedYaw, 0f) * baseRot;

            // World AABB from the LOCAL bounds captured ONCE at build (stable), transformed by the prop's current
            // pose. Re-querying renderer/mesh bounds every frame gave a bogus, huge, fluctuating box for LOD props
            // (the LOD hierarchy reports ~68-269m once it evaluates) which flung the prop off-screen.
            if (!_localBounds.TryGetValue(id, out var lb))
            {
                if (!PropClone.TryGetPropLocalBounds(go, out lb)) return;
                _localBounds[id] = lb;
            }
            Bounds wb = PropClone.LocalToWorldBounds(go.transform, lb);

            // FEET (y): always the tuned FeetY grounding (GroundMode "fixed" = a dialed-in drop below the root).
            // The CharacterController's capsule BOTTOM sits ABOVE the visual feet, so anchoring y to it made the
            // prop float - FeetY is what grounds the decoys + was the earlier ground fix, so it is used for everyone.
            // XZ (centre): for the LOCAL owner use the live capsule centre (it tracks client-predicted movement, so
            // the prop does not trail the player); for REMOTE players the capsule is not live (its driver
            // PlayerMovement is a local-only singleton), so use the replicated transform position.
            float feetY = RoundEnvironment.FeetY(player);
            var cc = isLocal ? ResolveController(id, player) : null;
            Vector3 anchorXZ = (cc != null) ? cc.bounds.center : player.transform.position;

            go.transform.position += new Vector3(anchorXZ.x - wb.center.x, feetY - wb.min.y, anchorXZ.z - wb.center.z);
        }

        /// <summary>The player's CharacterController, cached per id (re-resolved if it goes away). Its world bounds
        /// are the live collision position - the reliable anchor for centring the LOCAL owner's disguise.</summary>
        private CharacterController ResolveController(ulong id, Player player)
        {
            if (_cc.TryGetValue(id, out var cc) && cc != null) return cc;
            _cc.Remove(id);
            try { cc = player.GetComponentInChildren<CharacterController>(); } catch { cc = null; }
            if (cc != null) _cc[id] = cc;
            return cc;
        }

        private void RemoveProp(ulong id, Player player)
        {
            if (_props.TryGetValue(id, out var go) && go != null) { try { UnityEngine.Object.Destroy(go); } catch { } }
            _props.Remove(id);
            _appliedPropId.Remove(id);
            _sourceRot.Remove(id);
            _cc.Remove(id);
            _yaw.Remove(id);
            _localBounds.Remove(id);
            if (player != null) SetBodyVisible(player, true, id == Net.PropHuntNet.LocalSteamId);
        }

        /// <summary>Render a live hider's avatar as underwear-only (the game's own "naked" render). Applied ONCE
        /// per hider (rebuilding the avatar is what shows the body); EnsureProp then hides it behind the prop when
        /// disguised. The original appearance is cached BEFORE the swap (LoadNakedSettings reassigns CurrentSettings)
        /// so it can be restored. Local-only per client - appearance does not auto-replicate, which is why this runs
        /// for every player in the per-client Apply loop.</summary>
        private void EnsureNaked(ulong id, Player player)
        {
            if (_naked.Contains(id)) return;
            try
            {
                var av = player != null ? player.Avatar : null;
                if (av == null) return;
                var orig = av.CurrentSettings;
                if (orig == null) return;   // avatar not ready yet - retry next tick
                _nakedOriginal[id] = orig;
                av.LoadNakedSettings(orig, 19);   // 19 = the game's underwear cutoff (CharacterCustomizationUI)
                _naked.Add(id);
                Core.LogDebug($"[PropHunt] appearance: {id} -> naked");
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] EnsureNaked failed: " + e.Message); }
        }

        /// <summary>Restore a player's original (clothed) appearance if we made them naked.</summary>
        private void RestoreAppearance(ulong id, Player player)
        {
            if (!_naked.Contains(id)) return;
            try
            {
                var av = player != null ? player.Avatar : null;
                if (av != null && _nakedOriginal.TryGetValue(id, out var orig) && orig != null)
                    av.LoadAvatarSettings(orig);
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] RestoreAppearance failed: " + e.Message); }
            _naked.Remove(id);
            _nakedOriginal.Remove(id);
        }

        // Scale a player's VISUAL avatar (NOT the CharacterController - movement/grounding stay full-size). Reads the
        // live transform each tick so an avatar rebuild (naked swap) can't leave it at the wrong scale. Local per client.
        private static void SetHiderScale(Player player, float scale)
        {
            try
            {
                var av = player != null ? player.Avatar : null;
                if (av == null || av.transform == null) return;
                var target = Vector3.one * scale;
                if (av.transform.localScale != target) av.transform.localScale = target;
            }
            catch { }
        }

        private static void SetBodyVisible(Player player, bool visible, bool isLocal)
        {
            try { player.SetThirdPersonMeshesVisibility(visible); } catch { }
            // SetVisibleToLocalPlayer controls whether THIS player's body is drawn to the LOCAL camera.
            //  - remote player: mirror `visible` so a disguised remote shows its prop, not its body.
            //  - local player: NOT touched here - ThirdPersonController owns it (show own body only in 3rd
            //    person when undisguised; never in 1st person; never when disguised).
            if (!isLocal) { try { player.SetVisibleToLocalPlayer(visible); } catch { } }
        }

        internal void Dispose()
        {
            foreach (var kv in _props) if (kv.Value != null) { try { UnityEngine.Object.Destroy(kv.Value); } catch { } }
            _props.Clear();
            _appliedPropId.Clear();
            _sourceRot.Clear();
            _cc.Clear();
            _yaw.Clear();
            _localBounds.Clear();

            // restore every naked hider's original appearance
            if (_naked.Count > 0)
            {
                ulong localId = Net.PropHuntNet.LocalSteamId;
                var ids = new List<ulong>(_naked);
                foreach (var id in ids)
                    RestoreAppearance(id, id == localId ? Player.Local : PlayerRegistry.Get(id));
            }
            _naked.Clear();
            _nakedOriginal.Clear();

            try
            {
                var list = Player.PlayerList;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var p = list[i];
                        if (p != null) { SetBodyVisible(p, true, p == Player.Local); SetHiderScale(p, 1f); }
                    }
            }
            catch { }
        }
    }
}
