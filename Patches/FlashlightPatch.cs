using HarmonyLib;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// During a PropHunt round, [F] is the "rotate prop" key - it must not also toggle the game's flashlight
    /// (F is the vanilla flashlight bind). The flashlight is driven by the input handler
    /// <c>GameInput.OnToggleFlashlight</c>; we cancel it while a round is active. No-op outside a round.
    /// </summary>
    [HarmonyPatch(typeof(Il2CppScheduleOne.GameInput), nameof(Il2CppScheduleOne.GameInput.OnToggleFlashlight))]
    internal static class FlashlightSuppressPatch
    {
        private static bool Prefix()
        {
            var ctl = GameModeController.Active;
            return !(ctl != null && ctl.RoundActive);   // false (skip) during a round
        }
    }
}
