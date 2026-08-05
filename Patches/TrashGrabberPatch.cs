using HarmonyLib;
using Il2CppScheduleOne.Equipping;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// Give the hunter's trash grabber ten times its normal room.
    ///
    /// Hunters carry it so hiders cannot camp forever as a piece of litter on an open street. At vanilla's 20 units it
    /// fills after a handful of bags, and from then on the tool is dead weight for the rest of the round - a hunter who
    /// has to go and find a bin is not hunting. Emptying it is a chore the round has no room for.
    ///
    /// Only while a PropHunt round is running: outside one this is the game's own item with the game's own balance, and
    /// nothing here should leak into a normal save.
    /// </summary>
    [HarmonyPatch(typeof(Equippable_TrashGrabber), nameof(Equippable_TrashGrabber.GetCapacity))]
    internal static class TrashGrabberCapacityPatch
    {
        /// <summary>Vanilla's capacity, read out of GetCapacity's own arithmetic (20 - whatever is already inside).</summary>
        private const int VanillaCapacity = 20;
        private const int Multiplier = 10;

        /// <summary>
        /// A POSTFIX rather than a prefix, and that is deliberate: vanilla computes "room left" as
        /// <c>20 - GetTotalSize()</c>, so adding the extra allowance to whatever it returned needs no second call into
        /// the instance and cannot disagree with it about how full the thing is. Nine more vanilla-loads of room.
        /// </summary>
        private static void Postfix(ref int __result)
        {
            try
            {
                if (GameModeController.Active == null || !GameModeController.Active.RoundActive) return;
                __result += VanillaCapacity * (Multiplier - 1);
            }
            catch { }
        }
    }
}
