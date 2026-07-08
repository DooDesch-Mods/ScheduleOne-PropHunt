using HarmonyLib;
using Il2CppScheduleOne.Trash;
using Il2CppScheduleOne.Combat;

namespace PropHunt.Patches
{
    /// <summary>
    /// Keep movable world objects STATIC during a round so a real (pushable) trash can / mailbox can't be told apart
    /// from a static hider-prop by shooting or bumping it. Two prefixes, both gated on RoundInteractionGate.RoundActive
    /// (client-local - isKinematic and the impact force are local):
    ///  - PhysicsDamageable.ReceiveImpact: skip the AddForce, so shooting a physics object never shoves it (covers
    ///    trash, mailboxes and any other PhysicsDamageable decor).
    ///  - TrashItem.SetPhysicsActive: never let trash un-freeze (drag/impact) - force it kinematic instead.
    /// Trash pickup is the Draggable interaction (untouched), so the trash grabber still collects it; colliders stay
    /// (the grabber ray + catch hitbox need them). No-op outside a round.
    /// </summary>
    [HarmonyPatch(typeof(PhysicsDamageable), "ReceiveImpact")]
    internal static class BlockPhysicsImpactPatch
    {
        private static bool Prefix() => !RoundInteractionGate.RoundActive;   // false -> no impact force while a round is active
    }

    [HarmonyPatch(typeof(TrashItem), "SetPhysicsActive")]
    internal static class FreezeTrashPatch
    {
        private static bool Prefix(TrashItem __instance, bool active)
        {
            if (!RoundInteractionGate.RoundActive) return true;
            if (active)   // something tries to un-freeze the trash (drag/impact) -> keep it static
            {
                try { var rb = __instance.GetComponent<UnityEngine.Rigidbody>(); if (rb != null) rb.isKinematic = true; } catch { }
                return false;
            }
            return true;   // freezing (active=false) proceeds normally
        }
    }
}
