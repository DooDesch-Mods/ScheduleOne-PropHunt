using UnityEngine;

namespace PropHunt.View
{
    /// <summary>
    /// Shared follow-cam state, read by <see cref="PropHunt.Patches.PlayerCameraThirdPersonPatch"/> and driven by
    /// <see cref="SpectatorController"/>. When <see cref="Active"/>, the camera postfix places the local camera
    /// behind <see cref="Target"/> and looks at it. A flag + a target transform only (no controller ref).
    /// </summary>
    internal static class SpectatorCam
    {
        internal static bool Active;
        internal static Transform Target;        // the player being followed
        internal static float Distance = 3.5f;   // how far behind the target
        internal static float Height = 1.0f;     // world-up lift
    }
}
