using UnityEngine;
using PropHunt.Game;
using PropHunt.Disguise;

namespace PropHunt.Catch
{
    /// <summary>
    /// LOCAL hunter tooling during the Hunting phase: aim + click to catch.
    ///
    /// Shot resolution (one sweep, nearest relevant hit wins):
    ///   1. A single SphereCastAll (radius 0.35) runs along the camera aim as far as the weapon reaches -
    ///      the weapon's own range for a gun, the host's TagRange for melee. Triggers are INCLUDED: the
    ///      disguise/decoy hitboxes are triggers so they never block movement.
    ///   2. Each hit is line-of-sight checked against the point on the sweep AXIS, then classified:
    ///      a decoy ("ph_decoy_&lt;i&gt;"), a disguised hider via their prop hitbox ("ph_prop_&lt;steamId&gt;"), or an
    ///      UNDISGUISED player via their capsule. The clone is not parented to the player, so a disguised
    ///      hider is resolved from the hitbox NAME, never from GetComponentInParent&lt;Player&gt;.
    ///   3. A disguised hider is catchable ONLY through the prop hitbox, so a small prop stays a small target.
    ///
    /// The host trusts the client's hit (it re-checks phase, roles and elimination, not geometry).
    ///
    /// Aim uses PlayerCamera.Instance.Camera; forward via Camera.transform.forward. Victims resolve to a stable
    /// id via PlayerRegistry.IdForPlayer.
    /// </summary>
    internal sealed class CatchController
    {
        private readonly GameModeController _ctl;
        internal CatchController(GameModeController ctl) { _ctl = ctl; }

        // Catches are driven by a REAL fired weapon shot (WeaponPatches postfix on Equippable_RangedWeapon.Fire /
        // Equippable_MeleeWeapon.ExecuteHit -> GameModeController.OnLocalHunterFired -> here), never by raw input.
        // That makes them ammo/aim/cooldown-gated like the gun itself, so spamming left-click without firing does
        // nothing, and the shot only lands where the weapon actually fired.
        internal void Tick() { }

        /// <summary>Resolve a fired shot into a decoy/prop hit. <paramref name="maxRange"/> is the weapon's reach
        /// (generous for guns, short for melee). Called from the weapon-fire Harmony postfix.</summary>
        internal void ResolveShot(float maxRange)
        {
            if (_ctl.Phase != RoundPhase.Hunting || _ctl.LocalRole != PlayerRole.Hunter) return;
            try
            {
                var lp = Player.Local;
                // A tased hunter's Fire() is skipped by the prefix, but Harmony still runs this postfix - so re-check.
                if (lp != null && lp.IsTased) return;
                if (_ctl.LocalDowned) return;   // knocked down (FF / concussion) -> can't shoot or catch

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam == null || cam.Camera == null) return;
                var t = cam.Camera.transform;
                // The catch ray reaches as far as the weapon does (passed in): generous for guns - if your aim is on
                // the prop when you fire, the hit counts across the play area - and short for melee. Difficulty stays
                // in the PROP SIZE (small prop = small target).

                // ONE generous sphere sweep along the aim. A thin ray needed pixel-perfect aim (decoys felt
                // impossible to hit). The sweep widens the acceptance so you only need to be roughly on target;
                // the disguise/decoy now carries a prop-sized trigger collider, and the host re-validates a
                // player tag with its own prop-size lateral gate, so this stays fair.
                const float sweepR = 0.35f;
                // MUST include trigger colliders: the disguise's prop hitbox (and decoys) are TRIGGERS so they never
                // block movement. The default SphereCastAll skips triggers, which is why a shot passed straight through
                // a hider's prop hitbox and never registered a catch. QueryTriggerInteraction.Collide hits them.
                var hits = Physics.SphereCastAll(t.position, sweepR, t.forward, maxRange, ~0, QueryTriggerInteraction.Collide);
                if (hits == null || hits.Length == 0) return;
                System.Array.Sort(hits, (System.Comparison<RaycastHit>)((a, b) => a.distance.CompareTo(b.distance)));

                // nearest relevant hit wins: a decoy, a DISGUISED hider (via their "ph_prop_<id>" hitbox), or an
                // UNDISGUISED player (via their capsule). The disguise clone is NOT parented to the player (it is
                // world-space, re-centred each frame), so a disguised hider is resolved from the hitbox NAME -
                // GetComponentInParent<Player> finds nothing on the clone, which is why catches never registered.
                for (int i = 0; i < hits.Length; i++)
                {
                    var h = hits[i];
                    if (!HasLineOfSight(t.position, t.forward, h, sweepR)) continue;   // a wall between shooter and this hit -> no through-wall hit
                    if (IsDecoy(h.transform, out int decoyIdx))
                    {
                        SpawnPropHitFx(h.point);   // immediate local hit feedback on the decoy (it reads as a real prop)
                        PropHunt.UI.Hud.HudController.ShowHitmarker();
                        Core.LogDebug($"[PropHunt] hit decoy idx={decoyIdx}");
                        _ctl.RequestHitDecoy(decoyIdx);
                        return;
                    }
                    if (IsDisguiseHitbox(h.transform, out ulong propVictim) && propVictim != 0 && propVictim != _ctl.LocalId)
                    {
                        // feedback ON THE PROP (the game's impact puff at the hit point); on-player blood is suppressed
                        // by ImpactFxSuppressPatch. The host re-validates before accepting.
                        SpawnPropHitFx(h.point);
                        PropHunt.UI.Hud.HudController.ShowHitmarker();
                        _ctl.RequestClaimTag(propVictim, t.forward);
                        Core.LogDebug($"[PropHunt] claim tag on {propVictim} via prop hitbox (size {PropCatalog.SizeOf(_ctl.PropIdOf(propVictim)):F2})");
                        return;
                    }

                    // An UNDISGUISED player (no prop yet) is still catchable via their capsule; a DISGUISED player is
                    // NEVER caught via the capsule (only the prop hitbox above), so a small prop stays a small target.
                    Player victim = ResolvePlayer(h);
                    if (victim == null) continue;
                    ulong victimId = PlayerRegistry.IdForPlayer(victim);
                    if (victimId == 0 || victimId == _ctl.LocalId) continue;
                    if (_ctl.PropIdOf(victimId) >= 0) continue;   // disguised -> handled via the prop hitbox only
                    // A shot on another HUNTER is FRIENDLY FIRE (knock down, never catch). When FF is off, teammates
                    // are not targets - pass through and keep scanning for a real target behind them.
                    if (_ctl.RoleOf(victimId) == PlayerRole.Hunter)
                    {
                        if (_ctl.Settings != null && _ctl.Settings.FriendlyFire)
                        {
                            PropHunt.UI.Hud.HudController.ShowHitmarker();
                            _ctl.RequestHitHunter(victimId, t.forward);
                            Core.LogDebug($"[PropHunt] friendly-fire hit on hunter {victimId}");
                            return;
                        }
                        continue;
                    }
                    PropHunt.UI.Hud.HudController.ShowHitmarker();
                    _ctl.RequestClaimTag(victimId, t.forward);
                    Core.LogDebug($"[PropHunt] claim tag on {victimId} via capsule (undisguised)");
                    return;
                }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] catch tick failed: " + e.Message); }
        }

        // ---- helpers ----

        // True if nothing SOLID sits between the shooter's eye and the hit (a wall blocks the shot). Triggers are
        // ignored (the prop hitboxes + decoys are triggers), so only real level geometry occludes; a blocker at ~the
        // target's distance is the target itself, not a wall. Fail-open on error so shots never silently die.
        //
        // The test runs against the point on the SWEEP AXIS at the hit's distance, NOT against hit.point:
        //  - hit.point is where the 0.35m sphere first touched, so for a prop standing against a wall it sits LATERALLY
        //    inside that wall - a line to it is blocked by the wall and the shot died even though the prop was in the open.
        //  - when the sphere already OVERLAPS a collider at the sweep origin (shooting from point-blank), Unity reports
        //    distance 0 and point (0,0,0); a line to the world origin is blocked by whatever happens to be there.
        // The axis point is by construction inside the volume the sphere just swept through, so it only reports real walls.
        private static bool HasLineOfSight(Vector3 from, Vector3 dir, RaycastHit hit, float sweepR)
        {
            try
            {
                // overlap at the muzzle: the target is already touching us, there is no room for a wall in between
                if (hit.distance <= 0.001f) return true;
                Vector3 to = from + dir * hit.distance;
                if (!Physics.Linecast(from, to, out var block, ~0, QueryTriggerInteraction.Ignore)) return true;   // clear line
                return block.distance >= hit.distance - (sweepR + 0.1f);   // blocker at/after the target = the target, not a wall
            }
            catch { return true; }
        }

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

        /// <summary>True if the hit transform (or an ancestor) is a disguise prop hitbox ("ph_prop_&lt;steamId&gt;");
        /// outputs the disguised player's steam id parsed from the name (the clone isn't parented to the player, so the
        /// name is how we map a hit prop back to its hider).</summary>
        private static bool IsDisguiseHitbox(Transform hit, out ulong victimId)
        {
            victimId = 0;
            if (hit == null) return false;
            try
            {
                Transform t = hit;
                for (int depth = 0; depth < 6 && t != null; depth++)
                {
                    string n = t.gameObject.name;
                    if (n != null && n.StartsWith("ph_prop_") && ulong.TryParse(n.Substring("ph_prop_".Length), out victimId)) return true;
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
