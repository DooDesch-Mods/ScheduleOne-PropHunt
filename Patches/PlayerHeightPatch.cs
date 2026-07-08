using HarmonyLib;
using Il2CppScheduleOne.PlayerScripts;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// Holds the local hider's current prop height (the prop's largest world dimension, clamped 0.5-1.85m), written
    /// by GameModeController when the local prop changes. Read by <see cref="PropHunt.Disguise.PropPassthrough"/>,
    /// which lets a short-prop hider walk under low obstacles by Physics.IgnoreCollision (NOT by resizing the
    /// CharacterController - any resize breaks Unity's grounding and sinks/launches the player).
    /// </summary>
    internal static class PropCollisionState
    {
        /// <summary>The local hider's current prop height (clamped 0.5-1.85). 0 = not disguised / inactive.</summary>
        internal static float TargetHeight;
    }

    /// <summary>
    /// Local-player move-speed control for PropHunt, the single owner of the global StaticMoveSpeedMultiplier during a
    /// round. Composes TWO factors so they never clobber each other:
    ///   - a per-role BASE (<see cref="SetRoleFactor"/>): hiders move a bit slower than hunters (host-configurable),
    ///   - the CTRL crouch-walk (<see cref="Set"/>): a disguised hider halves speed instead of the blocked crouch.
    /// Only the local player's Move() reads StaticMoveSpeedMultiplier on this client, so this genuinely slows the
    /// local player (which then syncs to everyone) - it is not merely cosmetic. Always restored to vanilla when idle.
    /// </summary>
    internal static class SlowWalk
    {
        private const float CrouchFactor = 0.5f;   // half normal speed while crouch-walking (CTRL)
        private static float _roleFactor = 1f;     // per-role base: 1.0 = normal, <1.0 = slower (hiders)
        private static bool _slow;                 // CTRL crouch-walk held
        private static bool _applied;
        private static float _base = 1f;           // the vanilla multiplier we compose our factors onto

        /// <summary>The persistent per-role speed base (1.0 = normal; &lt;1.0 slows the local player). Set on role change.</summary>
        internal static void SetRoleFactor(float factor)
        {
            _roleFactor = factor <= 0f ? 1f : factor;
            Apply();
        }

        /// <summary>Crouch-walk toggle (CTRL), driven each frame by PropPicker while a disguised hider.</summary>
        internal static void Set(bool slow)
        {
            _slow = slow;
            Apply();
        }

        private static void Apply()
        {
            try
            {
                float mult = _roleFactor * (_slow ? CrouchFactor : 1f);
                if (mult > 0.999f && mult < 1.001f)   // nothing to apply -> back to vanilla
                {
                    if (_applied) { PlayerMovement.StaticMoveSpeedMultiplier = _base; _applied = false; }
                    return;
                }
                if (!_applied) { _base = PlayerMovement.StaticMoveSpeedMultiplier; _applied = true; }   // capture vanilla once
                PlayerMovement.StaticMoveSpeedMultiplier = _base * mult;
            }
            catch { _applied = false; }
        }

        /// <summary>Full reset to vanilla speed (crouch off + role factor cleared). Called when leaving the
        /// active-hider state and on session teardown.</summary>
        internal static void Restore()
        {
            _slow = false;
            _roleFactor = 1f;
            Apply();
        }
    }

    /// <summary>
    /// Blocks TryToggleCrouch while the local player is an active hider with a prop equipped (the hider is already
    /// scaled to the prop, and CTRL is repurposed as slow-walk - see <see cref="SlowWalk"/>). Returning false from a
    /// Harmony Prefix skips the original method.
    /// </summary>
    [HarmonyPatch(typeof(PlayerMovement), "TryToggleCrouch")]
    internal static class PlayerCrouchBlockPrefix
    {
        private static bool Prefix()
        {
            try
            {
                var ctl = GameModeController.Active;
                if (ctl == null) return true;
                if (ctl.LocalRole != PlayerRole.Hider) return true;   // hunters crouch freely
                if (ctl.LocalPropId < 0) return true;                 // not disguised yet
                return false;                                          // disguised hider -> block crouch
            }
            catch { return true; }
        }
    }
}
