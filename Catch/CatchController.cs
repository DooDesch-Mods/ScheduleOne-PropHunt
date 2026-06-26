using UnityEngine;
using PropHunt.Game;

namespace PropHunt.Catch
{
    /// <summary>
    /// LOCAL hunter tooling during the Hunting phase: aim + click to catch. Raycasts from the player camera
    /// up to the configured tag range; if it resolves to another player, sends a ClaimTag intent (the host
    /// re-validates geometry before accepting). Props are render-only (no collider), so the ray passes
    /// through a disguise and resolves to the hider's real capsule behind it. TODO(testing): rebindable key,
    /// a hit/miss feedback effect, and using CombatManager.MeleeLayerMask instead of the all-layers ray.
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
                if (lp != null && lp.IsTased) return;   // concussed: can't catch while stunned
                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam == null || cam.Camera == null) return;
                var t = cam.Camera.transform;
                if (Physics.Raycast(t.position, t.forward, out var hit, _ctl.Settings.TagRange + 0.5f))
                {
                    Player victim = null;
                    if (hit.collider != null) victim = hit.collider.GetComponentInParent<Player>();
                    if (victim == null && hit.transform != null) victim = hit.transform.GetComponentInParent<Player>();
                    if (victim != null)
                    {
                        ulong id = PlayerRegistry.IdForPlayer(victim);
                        if (id != 0 && id != _ctl.LocalId) { _ctl.RequestClaimTag(id); Core.LogDebug("[PropHunt] claim tag on " + id); }
                    }
                }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] catch tick failed: " + e.Message); }
        }
    }
}
