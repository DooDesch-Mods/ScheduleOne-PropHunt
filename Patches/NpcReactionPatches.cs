using HarmonyLib;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Noise;
using Il2CppScheduleOne.Vision;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// During a PropHunt round, NPCs must ignore hunter GUNFIRE - no panic/flee/cover, no civilian police-call, no
    /// police pursuit - so they keep their normal schedule and a hider can mimic them. Gunfire reaches NPCs through
    /// two independent dispatchers on <c>NPCAwareness</c>:
    ///   - <c>NoiseEvent</c>: a Gunshot/Explosion noise -> civilian panic/flee/call-police (+ police face shooter).
    ///   - <c>VisionEvent</c>: seeing <c>EVisualState.DischargingWeapon</c> -> police WANTED pursuit.
    /// We prefix both and skip ONLY the gunfire cases while a round is active; every other awareness (footsteps,
    /// drug deals, vandalism, etc.) is untouched, so NPC behaviour otherwise stays normal. The patch is purely
    /// subtractive (nothing is set, so nothing to replicate / roll back) and is applied on the host (authoritative
    /// AI) and clients (cosmetic notice) alike.
    /// </summary>
    internal static class NpcGunfireReactionGate
    {
        internal static bool Suppress()
        {
            var ctl = GameModeController.Active;
            return ctl != null && ctl.RoundActive;
        }
    }

    [HarmonyPatch(typeof(NPCAwareness), "NoiseEvent")]
    internal static class NpcNoiseEventPatch
    {
        private static bool Prefix(NoiseEvent nEvent)
        {
            try
            {
                if (!NpcGunfireReactionGate.Suppress() || nEvent == null) return true;
                if (nEvent.type == ENoiseType.Gunshot || nEvent.type == ENoiseType.Explosion) return false;
            }
            catch { }
            return true;
        }
    }

    [HarmonyPatch(typeof(NPCAwareness), "VisionEvent")]
    internal static class NpcVisionEventPatch
    {
        private static bool Prefix(VisionEventReceipt vEvent)
        {
            try
            {
                if (!NpcGunfireReactionGate.Suppress() || vEvent == null) return true;
                if (vEvent.State == EVisualState.DischargingWeapon || vEvent.State == EVisualState.Brandishing) return false;
            }
            catch { }
            return true;
        }
    }
}
