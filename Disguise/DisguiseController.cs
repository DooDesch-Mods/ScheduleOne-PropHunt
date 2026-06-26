using System.Collections.Generic;
using UnityEngine;
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
                    bool disguised = ps.PropId >= 0 && !ps.Eliminated && ps.Role == PlayerRole.Hider;
                    if (player == null)
                    {
                        if (disguised) Core.LogDebug($"[PropHunt] disguise: no game Player resolved for {ps.SteamId} (prop {ps.PropId}) - cannot render");
                        continue;
                    }
                    if (disguised) EnsureProp(ps.SteamId, player, ps.PropId);
                    else RemoveProp(ps.SteamId, player);
                }

                // tidy disguises for players gone from the snapshot
                List<ulong> stale = null;
                foreach (var id in _props.Keys)
                    if (!state.Players.TryGetValue(id, out var p) || p.PropId < 0 || p.Eliminated || p.Role != PlayerRole.Hider)
                        (stale ??= new List<ulong>()).Add(id);
                if (stale != null) foreach (var id in stale) RemoveProp(id, PlayerRegistry.Get(id));
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] disguise apply failed: " + e.Message); }
        }

        private void EnsureProp(ulong id, Player player, int propId)
        {
            if (_appliedPropId.TryGetValue(id, out var cur) && cur == propId && _props.ContainsKey(id)) return;
            RemoveProp(id, player);
            var e = PropCatalog.ById(propId);
            if (e == null || e.Source == null || e.Source.sharedMesh == null)
            {
                Core.LogDebug($"[PropHunt] disguise: prop id {propId} not renderable in local catalog (entry={(e == null ? "null" : e.Name)}) - count {PropCatalog.Count}");
                return;
            }
            try
            {
                bool isLocal = id == Net.PropHuntNet.LocalSteamId;
                SetBodyVisible(player, false, isLocal);
                var mesh = e.Source.sharedMesh;
                var go = new GameObject("ph_prop_" + id);
                var mf = go.AddComponent<MeshFilter>();
                mf.sharedMesh = mesh;
                var mr = go.AddComponent<MeshRenderer>();
                if (e.SourceRenderer != null) mr.sharedMaterials = e.SourceRenderer.sharedMaterials;

                go.transform.SetParent(player.transform, false);
                // real-world size (player scale is ~1) so a vending machine stays its size
                go.transform.localScale = e.Source.transform.lossyScale;
                // base = the source prop's world orientation so sideways-authored meshes (e.g. the barrel) stand
                // correctly; UpdatePropTransform applies the hider's chosen yaw on top + re-centres/grounds it.
                _sourceRot[id] = e.Source.transform.rotation;
                go.transform.rotation = _sourceRot[id];

                _props[id] = go;
                _appliedPropId[id] = propId;

                int matN = (e.SourceRenderer != null && e.SourceRenderer.sharedMaterials != null) ? e.SourceRenderer.sharedMaterials.Length : 0;
                string shader = "?";
                try { var m0 = e.SourceRenderer != null ? e.SourceRenderer.sharedMaterial : null; shader = m0 == null ? "NULL" : (m0.shader != null ? m0.shader.name : "no-shader"); } catch { }
                Core.LogDebug($"[PropHunt] disguise: applied '{e.Name}' to {id} (worldSize {mr.bounds.size}, verts {mesh.vertexCount}, mats {matN}, shader '{shader}', srcEnabled {(e.SourceRenderer != null && e.SourceRenderer.enabled)})");
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
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) return;

            Quaternion baseRot = _sourceRot.TryGetValue(id, out var sr) ? sr : Quaternion.identity;
            go.transform.rotation = Quaternion.Euler(0f, yaw, 0f) * baseRot;

            // feet = the player capsule's BOTTOM (stays at the ground regardless of crouch/sprint/stand). A
            // downward raycast proved unreliable (it missed and fell back to a waist-relative guess -> the prop
            // floated/changed with stance). The CharacterController/collider bottom is stance-independent.
            Vector3 pp = player.transform.position;
            float feetY = RoundEnvironment.FeetY(player);            // capsule bottom; look-/stance-independent

            Bounds wb = mr.bounds;                                   // world AABB at the current rotation/position
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
