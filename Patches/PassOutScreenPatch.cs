using HarmonyLib;
using Il2CppScheduleOne.UI;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// A PropHunt knockdown ragdolls a hunter via <c>Player.SendPassOut()</c>, whose owner-side logic also opens the
    /// vanilla <see cref="PassOutScreen"/> - a full faint sequence that fades the eyes shut and, after ~3s,
    /// TELEPORTS the player to a random recovery point (a hospital bed), drains 50-500 cash and drives its OWN
    /// <c>SendPassOutRecovery()</c>. That is entirely wrong for a brief tactical knockdown: it would eject the hunter
    /// from the play area and fight PropHunt's own timed recovery. Skip the pass-out screen while a PropHunt round is
    /// active - PropHunt drives the ragdoll + timed stand-up itself, and re-enables player control on recovery
    /// (see GameModeController.DriveLocalRagdoll). Outside a round this is inert, so vanilla passing out is untouched.
    /// </summary>
    [HarmonyPatch(typeof(PassOutScreen), nameof(PassOutScreen.Open))]
    internal static class PassOutScreenGatePatch
    {
        private static bool Prefix()
        {
            var ctl = GameModeController.Active;
            return !(ctl != null && ctl.RoundActive);   // false -> skip Open() during a PropHunt round
        }
    }
}
