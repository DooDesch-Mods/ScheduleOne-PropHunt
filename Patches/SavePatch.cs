using System;
using HarmonyLib;
using Il2CppScheduleOne.Persistence;

namespace PropHunt.Patches
{
    /// <summary>
    /// Block saving while a PropHunt round is active (Hiding/Hunting/RoundEnd AND the Safehouse lobby). The mod runs
    /// on a scratch co-op session and mid-round teleports players, locks property doors, swaps appearances + applies
    /// disguises; persisting that transient state to disk would corrupt the save. Saving works again once the round
    /// ends / the player is back in the hub (RoundActive == false). The Save button still appears but does nothing.
    /// </summary>
    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save), new Type[] { })]
    internal static class BlockSavePatch
    {
        private static bool Prefix()
        {
            if (Core.Session != null && Core.Session.RoundActive) { Core.LogDebug("[PropHunt] save blocked during round."); return false; }
            return true;
        }
    }

    [HarmonyPatch(typeof(SaveManager), nameof(SaveManager.Save), new Type[] { typeof(string) })]
    internal static class BlockSavePathPatch
    {
        private static bool Prefix()
        {
            if (Core.Session != null && Core.Session.RoundActive) { Core.LogDebug("[PropHunt] save (path) blocked during round."); return false; }
            return true;
        }
    }
}
