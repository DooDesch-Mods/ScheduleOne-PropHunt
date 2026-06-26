namespace PropHunt.View
{
    /// <summary>
    /// Shared third-person state, read by the <see cref="PropHunt.Patches.PlayerCameraThirdPersonPatch"/> camera
    /// postfix and driven by <see cref="ThirdPersonController"/>. A flag + offsets only (no engine refs) so the
    /// Harmony patch can read it without pulling in the controller.
    /// </summary>
    internal static class ThirdPersonView
    {
        internal static bool Active;
        internal static float Distance = 2.8f;   // how far the camera pulls back along the view direction
        internal static float Height = 0.4f;     // extra world-up lift so the body sits in frame
    }
}
