using HarmonyLib;
using PropHunt.Game;
// Not a `using Il2CppScheduleOne.UI.Phone;` - that would make the bare name `Phone` ambiguous with PropHunt's own
// Phone namespace (CS0118), so the patch target is spelled out below.
using VanillaPhone = Il2CppScheduleOne.UI.Phone.Phone;

namespace PropHunt.Patches
{
    /// <summary>
    /// During a PropHunt round, [F] is the "rotate prop" key - it must not also toggle the game's flashlight
    /// (F is the vanilla flashlight bind). Since 0.4.6f11 the flashlight lives on the phone: <c>Phone</c> polls its own
    /// <c>InputActionReference</c> every frame and calls <c>Phone.ToggleFlashlight</c>. Cancel that call while a round
    /// is active. No-op outside a round.
    /// </summary>
    [HarmonyPatch(typeof(VanillaPhone), "ToggleFlashlight")]
    internal static class FlashlightSuppressPatch
    {
        private static bool Prefix()
        {
            var ctl = GameModeController.Active;
            return !(ctl != null && ctl.RoundActive);   // false (skip) during a round
        }
    }
}
