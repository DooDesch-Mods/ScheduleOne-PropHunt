using UnityEngine;
using PropHunt.Game;
using PropHunt.Disguise;

namespace PropHunt.Catch
{
    /// <summary>
    /// LOCAL hunter tooling during the Hunting phase: aim + click to catch.
    ///
    /// Shot resolution (Feature 3):
    ///   1. Thin pre-ray (radius 0) finds anything in the line of sight up to TagRange.
    ///   2. If a decoy is hit (GameObject name walks up to something starting with "ph_decoy_"), the
    ///      trailing index is parsed and forwarded to GameModeController.RequestHitDecoy (step 4 stub).
    ///   3. If a player is hit by the pre-ray, look up that victim's current prop size and compute a
    ///      SphereCast radius proportional to it (big prop = generous; tiny prop = tight). Then fire the
    ///      SphereCast; if it resolves the same victim, send the ClaimTag intent.
    ///   4. Props are render-only (no collider), so both rays pass through the disguise clone and resolve
    ///      to the hider's real capsule behind it.
    ///
    /// Host re-validates geometry (distance + lateral offset gated by prop size) before accepting any tag.
    ///
    /// Camera confirmed: PlayerCamera.Instance.Camera (Camera field, il2cpp L1857); forward via Camera.transform.forward.
    /// PlayerRegistry.IdForPlayer confirmed as existing API used throughout the codebase.
    /// </summary>
    internal sealed class CatchController
    {
        private readonly GameModeController _ctl;
        internal CatchController(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            if (_ctl.Phase != RoundPhase.Hunting || _ctl.LocalRole != PlayerRole.Hunter) return;
            try
            {
                if (!Input.GetMouseButtonDown(0)) return;
                var lp = Player.Local;
                if (lp != null && lp.IsTased) return;   // stunned: can't catch while tased

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam == null || cam.Camera == null) return;
                var t = cam.Camera.transform;
                // The catch ray reaches as far as the weapon's projectile would - there is NO short "catch range".
                // Hunters fire projectile guns: if your aim is on the prop when you fire, the hit counts, at any
                // distance across the play area. Difficulty stays in the PROP SIZE (small prop = small target).
                const float maxRange = 150f;

                // ONE generous sphere sweep along the aim. A thin ray needed pixel-perfect aim (decoys felt
                // impossible to hit). The sweep widens the acceptance so you only need to be roughly on target;
                // the disguise/decoy now carries a prop-sized trigger collider, and the host re-validates a
                // player tag with its own prop-size lateral gate, so this stays fair.
                const float sweepR = 0.35f;
                var hits = Physics.SphereCastAll(t.position, sweepR, t.forward, maxRange);
                if (hits == null || hits.Length == 0) return;
                System.Array.Sort(hits, (System.Comparison<RaycastHit>)((a, b) => a.distance.CompareTo(b.distance)));

                // nearest relevant hit wins: a decoy, or a disguised player hit THROUGH THEIR PROP HITBOX.
                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    if (IsDecoy(h.transform, out int decoyIdx))
                    {
                        Core.LogDebug($"[PropHunt] hit decoy idx={decoyIdx}");
                        _ctl.RequestHitDecoy(decoyIdx);
                        return;
                    }
                    Player victim = ResolvePlayer(h);
                    if (victim == null) continue;
                    ulong victimId = PlayerRegistry.IdForPlayer(victim);
                    if (victimId == 0 || victimId == _ctl.LocalId) continue;   // self / invalid - keep scanning

                    // A DISGUISED player counts as hit ONLY when the shot lands on their prop hitbox (ph_prop_*),
                    // never on their movement capsule / body - so a small prop is a small target and you cannot
                    // shoot "over" the cone at the tall capsule behind it. An undisguised player (no prop yet) is
                    // still catchable normally, so never picking a prop is not an exploit.
                    bool disguised = _ctl.PropIdOf(victimId) >= 0;
                    bool viaProp = IsDisguiseHitbox(h.transform);
                    if (disguised && !viaProp) continue;

                    // feedback ON THE PROP (not blood on the player): show the game's impact puff where the shot
                    // landed on the prop, so the hunter sees the hit registered. The on-player blood is suppressed
                    // by ImpactFxSuppressPatch.
                    if (viaProp) SpawnPropHitFx(h.point);

                    _ctl.RequestClaimTag(victimId);
                    Core.LogDebug($"[PropHunt] claim tag on {victimId} (disguised={disguised}, propSize={PropCatalog.SizeOf(_ctl.PropIdOf(victimId)):F2})");
                    return;
                }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] catch tick failed: " + e.Message); }
        }

        // ---- helpers ----

        /// <summary>Show the game's impact particle at a world point (on the prop), so a hit registers visibly for
        /// the hunter without spawning blood on the hidden player.</summary>
        private static void SpawnPropHitFx(Vector3 point)
        {
            if (point == Vector3.zero) return;
            try
            {
                var fx = Singleton<Il2CppScheduleOne.FX.FXManager>.Instance;
                if (fx == null) return;
                var prefab = fx.PunchParticlePrefab;
                if (prefab != null) fx.PlayParticles(prefab, point, Quaternion.identity);
            }
            catch { }
        }

        /// <summary>True if the hit transform (or an ancestor) is a disguise prop hitbox ("ph_prop_*"). Only these
        /// count as a catchable hit on a player - the player's movement capsule / body never does.</summary>
        private static bool IsDisguiseHitbox(Transform hit)
        {
            if (hit == null) return false;
            try
            {
                Transform t = hit;
                for (int depth = 0; depth < 6 && t != null; depth++)
                {
                    string n = t.gameObject.name;
                    if (n != null && n.StartsWith("ph_prop_")) return true;
                    t = t.parent;
                }
            }
            catch { }
            return false;
        }

        /// <summary>True if the hit transform (or an ancestor) is a "ph_decoy_&lt;idx&gt;" decoy; outputs the index.</summary>
        private static bool IsDecoy(Transform hit, out int idx)
        {
            idx = -1;
            if (hit == null) return false;
            try
            {
                Transform t = hit;
                for (int depth = 0; depth < 6 && t != null; depth++)
                {
                    string n = t.gameObject.name;
                    if (n != null && n.StartsWith("ph_decoy_") && int.TryParse(n.Substring("ph_decoy_".Length), out idx))
                        return true;
                    t = t.parent;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Resolve a RaycastHit to a Player component via the collider or transform hierarchy.</summary>
        private static Player ResolvePlayer(RaycastHit hit)
        {
            Player p = null;
            if (hit.collider != null) p = hit.collider.GetComponentInParent<Player>();
            if (p == null && hit.transform != null) p = hit.transform.GetComponentInParent<Player>();
            return p;
        }
    }
}
