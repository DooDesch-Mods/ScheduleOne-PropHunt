using HarmonyLib;
using Il2CppScheduleOne.UI;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// Block the pause-menu "I'm stuck" button while a PropHunt session is between rounds (Safehouse or the pre-match
    /// Lobby). The button warps the player to the nearest navmesh - i.e. straight OUT of the safehouse interior -
    /// which is an exploit there. Outside a PropHunt session, and during an active round, it works normally (a
    /// genuinely stuck player mid-round can still use it). We patch the button, not PlayerMovement.WarpToNavMesh,
    /// which is also the legit y&lt;-20 fall-recovery + NPC path.
    /// </summary>
    [HarmonyPatch(typeof(PauseMenu), "StuckButtonClicked")]
    internal static class BlockStuckButtonPatch
    {
        private static bool Prefix()
        {
            var c = GameModeController.Active;
            if (c == null) return true;   // not in a PropHunt session -> vanilla
            var p = c.Phase;
            return !(p == RoundPhase.Safehouse || p == RoundPhase.Lobby);   // false -> button does nothing between rounds
        }
    }
}
