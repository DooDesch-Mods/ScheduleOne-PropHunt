namespace PropHunt.View
{
    /// <summary>
    /// Shared third-person state, read by the <see cref="PropHunt.Patches.PlayerCameraThirdPersonPatch"/> camera
    /// postfix and driven by <see cref="ThirdPersonController"/>. A flag + offsets only (no engine refs) so the
    /// Harmony patch can read it without pulling in the controller. <see cref="Distance"/> is the smoothed, zoom-applied
    /// distance the camera actually renders; the controller derives it from the per-prop auto-framed
    /// <see cref="BaseDistance"/> times the player's mouse-wheel <see cref="ZoomMultiplier"/>.
    /// </summary>
    internal static class ThirdPersonView
    {
        internal static bool Active;
        internal static float Distance = 2.8f;   // smoothed, zoom-applied distance the camera pulls back to
        internal static float Height = 0.4f;     // extra world-up lift so the body sits in frame

        // --- mouse-wheel zoom on top of the per-prop auto-framed distance ---
        internal static float BaseDistance = 2.8f;   // auto-framed distance for the current prop (before the user's zoom)
        internal static float ZoomMultiplier = 1f;   // player scroll zoom (1 = the auto default; >1 = further out)

        internal const float ZoomStepPercent = 0.10f;   // multiplicative step per scroll notch (10%)
        internal const float ZoomMultiplierMin = 0.55f;  // relative-to-BaseDistance clamp (in)
        internal const float ZoomMultiplierMax = 2.5f;    // relative-to-BaseDistance clamp (out)
        internal const float GlobalMinDistance = 0.5f;    // absolute floor so a tiny prop never clips into the head
        internal const float GlobalMaxDistance = 16f;     // absolute ceiling for "zoom out more" (matches vanilla CameraOrbit ~15m; BaseDistance auto-frame still caps at 9)
    }
}
