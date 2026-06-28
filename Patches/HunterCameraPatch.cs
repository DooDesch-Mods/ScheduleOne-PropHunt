using HarmonyLib;
using Il2CppScheduleOne.PlayerScripts;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// Hunters may not switch to third person. Schedule I has no persistent perspective toggle - the only
    /// third-person view is PlayerCamera.ViewAvatar(), invoked every frame from PlayerCamera.Update() while the
    /// ViewAvatar button is held. A Harmony prefix returning false skips ViewAvatar() for hunters, so the hold
    /// simply never enters avatar view (StopViewingAvatar() still runs normally as ViewingAvatar stays false).
    ///
    /// Hiders are intentionally left alone - seeing their own prop disguise in third person is part of the fun.
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), "ViewAvatar")]
    internal static class HunterNoThirdPersonPrefix
    {
        private static bool Prefix()
        {
            try
            {
                var ctl = GameModeController.Active;
                if (ctl == null) return true;                       // no active session -> normal behaviour
                if (ctl.LocalRole == PlayerRole.Hunter) return false; // hunters cannot view the avatar (third person)
                return true;
            }
            catch { return true; }
        }
    }
}
