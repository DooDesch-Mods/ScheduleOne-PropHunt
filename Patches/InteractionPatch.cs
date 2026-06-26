using HarmonyLib;
using Il2CppScheduleOne.Interaction;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// While a PropHunt round is active, the local player's world interaction is suppressed: no "E to pick up /
    /// open" prompt and E does nothing in-game. This frees E for "become this prop" and matches the expectation
    /// that becomable objects are not pickup/openable during a round. Off outside a round (normal play untouched).
    /// </summary>
    [HarmonyPatch(typeof(InteractionManager))]
    internal static class InteractionSuppressPatch
    {
        private static bool Suppress()
        {
            var ctl = GameModeController.Active;
            return ctl != null && ctl.RoundActive;
        }

        [HarmonyPrefix]
        [HarmonyPatch(nameof(InteractionManager.CheckHover))]
        private static bool BlockHover() => !Suppress();   // false -> skip the vanilla scan (no prompt)

        [HarmonyPrefix]
        [HarmonyPatch(nameof(InteractionManager.CheckInteraction))]
        private static bool BlockInteraction() => !Suppress();   // false -> E does nothing in-game
    }
}
