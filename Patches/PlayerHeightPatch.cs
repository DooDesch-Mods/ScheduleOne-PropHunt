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
    /// Slow-walk for a disguised hider: while CTRL is held we halve the local player's move speed (instead of the
    /// vanilla crouch, which we block). Driven each frame by PropPicker; uses the global StaticMoveSpeedMultiplier
    /// (only the local player's movement is computed on this client). Always restored when not slow-walking.
    /// </summary>
    internal static class SlowWalk
    {
        private const float Factor = 0.5f;   // half normal speed
        private static bool _applied;
        private static float _orig = 1f;

        internal static void Set(bool slow)
        {
            try
            {
                if (slow && !_applied) { _orig = PlayerMovement.StaticMoveSpeedMultiplier; PlayerMovement.StaticMoveSpeedMultiplier = _orig * Factor; _applied = true; }
                else if (!slow && _applied) { PlayerMovement.StaticMoveSpeedMultiplier = _orig; _applied = false; }
            }
            catch { _applied = false; }
        }

        internal static void Restore() => Set(false);
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
