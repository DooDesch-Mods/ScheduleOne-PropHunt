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
    /// changes). TODO(testing): if SetThirdPersonMeshesVisibility only affects the local view, fall back to
    /// toggling Avatar BodyMeshes/FaceMesh/ShapeKeyMeshes renderers; tune prop offset/scale.
    /// </summary>
    internal sealed class DisguiseController
    {
        private readonly Dictionary<ulong, GameObject> _props = new Dictionary<ulong, GameObject>();
        private readonly Dictionary<ulong, int> _appliedPropId = new Dictionary<ulong, int>();
        private readonly Dictionary<ulong, Quaternion> _sourceRot = new Dictionary<ulong, Quaternion>();   // each prop's source world orientation
        // live hiders are rendered NAKED (underwear-only) underneath their prop: when undisguised that bare body
        // shows, when disguised it is hidden behind the prop. We cache each one's original appearance to restore.
        private readonly Dictionary<ulong, AvatarSettings> _nakedOriginal = new Dictionary<ulong, AvatarSettings>();
        private readonly HashSet<ulong> _naked = new HashSet<ulong>();
        private readonly HashSet<ulong> _warnedUnresolved = new HashSet<ulong>();   // log "can't resolve" once per player, not per frame
        private bool _warnedHashMismatch;

        internal void Apply(GameState state)
        {
            if (state == null) return;
            try
            {
                if (!_warnedHashMismatch && state.CatalogHash != 0 && state.CatalogHash != PropCatalog.Hash)
                {
                    _warnedHashMismatch = true;
                    Core.Log.Warning($"[PropHunt] prop-catalog hash mismatch (host {state.CatalogHash} vs local {PropCatalog.Hash}) - disguises may differ.");
                }

                PlayerRegistry.Refresh();
                ulong localId = Net.PropHuntNet.LocalSteamId;
                var localPlayer = Player.Local;

                foreach (var ps in state.Players.Values)
                {
                    // the LOCAL player is resolved via the reliable Player.Local, NOT the best-effort name map
                    var player = (ps.SteamId == localId && localPlayer != null) ? localPlayer : PlayerRegistry.Get(ps.SteamId);
                    bool liveHider = ps.Role == PlayerRole.Hider && !ps.Eliminated;
                    bool disguised = liveHider && ps.PropId >= 0;
                    if (player == null)
                    {
                        // Log ONCE per unresolved player, not every frame. A crashed/disconnected client left as a
                        // hider in the synced roster (the host still lists them until the lobby drops them) otherwise
                        // floods the log every frame - observed at 360k lines, which also stalls the host via log I/O.
                        if (disguised && _warnedUnresolved.Add(ps.SteamId))
                            Core.LogDebug($"[PropHunt] disguise: no game Player resolved for {ps.SteamId} (prop {ps.PropId}) - cannot render");
                        continue;
                    }
                    _warnedUnresolved.Remove(ps.SteamId);   // resolved now -> a future drop warns once again
                    // naked FIRST (rebuilds the avatar -> body visible), then the prop hides the body if disguised
                    if (liveHider) EnsureNaked(ps.SteamId, player);
                    else RestoreAppearance(ps.SteamId, player);
                    if (disguised) EnsureProp(ps.SteamId, player, ps.PropId);
                    else RemoveProp(ps.SteamId, player);
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

                go.transform.SetParent(player.transform, false);
                // real-world size (player scale is ~1) so a vending machine stays its size
                go.transform.localScale = e.SourceRoot.transform.lossyScale;
                // base = the source prop's world orientation so sideways-authored meshes (e.g. the barrel) stand
                // correctly; UpdatePropTransform applies the hider's chosen yaw on top + re-centres/grounds it.
                _sourceRot[id] = e.SourceRoot.transform.rotation;
                go.transform.rotation = _sourceRot[id];

                // prop-sized catch hitbox: a trigger collider matching the prop volume, so a hunter who shoots
                // the visible prop catches the hider (big prop = big hitbox). The player capsule stays as a
                // backstop. Trigger = never blocks movement. Local + remote alike (the clone sits under the player).
                PropClone.AddTriggerHitbox(go);

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
                    UpdatePropTransform(ps.SteamId, player, isLocal ? localYaw : ps.PropYaw);
                }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] disguise late-apply failed: " + e.Message); }
        }

        /// <summary>Per-frame transform upkeep for a disguise. ROTATION: world-fixed at the source orientation +
        /// the hider's chosen yaw - it does NOT follow the camera, so looking around never spins the prop ([F]+
        /// mouse changes the yaw). POSITION: re-centred EVERY frame so the mesh's bounding-box centre sits on the
        /// player (xz) with its base on the actual ground under them (y, via a downward raycast). Recomputing each
        /// frame is what stops the prop ORBITING/drifting off to the side when the player turns.</summary>
        private void UpdatePropTransform(ulong id, Player player, float yaw)
        {
            if (player == null) return;
            if (!_props.TryGetValue(id, out var go) || go == null) return;

            Quaternion baseRot = _sourceRot.TryGetValue(id, out var sr) ? sr : Quaternion.identity;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * baseRot;

            // world AABB: a single-mesh clone has its own MeshRenderer; a LOD clone has none on the root (the
            // renderers sit on child LOD objects), so union the child renderers' bounds.
            if (!PropClone.TryGetWorldBounds(go, out Bounds wb)) return;

            // feet = the player capsule's BOTTOM (stays at the ground regardless of crouch/sprint/stand). A
            // downward raycast proved unreliable (it missed and fell back to a waist-relative guess -> the prop
            // floated/changed with stance). The CharacterController/collider bottom is stance-independent.
            Vector3 pp = player.transform.position;
            float feetY = RoundEnvironment.FeetY(player);            // capsule bottom; look-/stance-independent

            go.transform.position += new Vector3(pp.x - wb.center.x, feetY - wb.min.y, pp.z - wb.center.z);
        }

        private void RemoveProp(ulong id, Player player)
        {
            if (_props.TryGetValue(id, out var go) && go != null) { try { UnityEngine.Object.Destroy(go); } catch { } }
            _props.Remove(id);
            _appliedPropId.Remove(id);
            _sourceRot.Remove(id);
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
                        if (p != null) { SetBodyVisible(p, true, p == Player.Local); }
                    }
            }
            catch { }
        }
    }
}
