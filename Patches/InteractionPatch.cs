using HarmonyLib;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Dragging;
using PropHunt.Game;

namespace PropHunt.Patches
{
    internal static class RoundInteractionGate
    {
        internal static bool RoundActive { get { var c = GameModeController.Active; return c != null && c.RoundActive; } }
    }

    /// <summary>
    /// Hide EVERY interaction prompt ("E to pick up" / "E to open" / ...) during a round, so a real pickupable prop
    /// and a disguised hider look identical - a missing prompt used to give the hider away (a real prop showed the
    /// prompt, a hider did not). ShowMessage is the single point that pushes the prompt to the InteractionCanvas,
    /// which resets every frame, so skipping ShowMessage simply shows nothing - no state cleanup needed. Interaction
    /// itself does NOT depend on ShowMessage, so doors still open (silently) on E.
    /// </summary>
    [HarmonyPatch(typeof(InteractableObject), "ShowMessage")]
    internal static class HideInteractionPromptPatch
    {
        private static bool Prefix() => !RoundInteractionGate.RoundActive;   // false -> no prompt while a round is active
    }

    /// <summary>
    /// During a round, block PICKUP interactions so a prop can never be picked up (by either role) - while leaving
    /// doors and every other interactable working. We skip CheckInteraction only when the hovered object is a
    /// pickup/draggable; a hider's "[E] become" is handled separately by PropPicker (a direct key read), so it
    /// still works. Outside a round this is a no-op.
    /// </summary>
    [HarmonyPatch(typeof(InteractionManager), "CheckInteraction")]
    internal static class BlockPickupInteractionPatch
    {
        private static bool Prefix(InteractionManager __instance)
        {
            try
            {
                if (!RoundInteractionGate.RoundActive) return true;
                var hovered = __instance.HoveredInteractableObject;
                if (hovered == null) return true;
                if (IsPickup(hovered)) return false;   // block picking up a prop; doors / others still interact
            }
            catch { }
            return true;
        }

        /// <summary>True if the interactable belongs to a pickup or draggable world item (vs a door, switch, etc.).</summary>
        private static bool IsPickup(InteractableObject io)
        {
            try
            {
                if (io.GetComponentInParent<ItemPickup>() != null) return true;          // covers NetworkedItemPickup/CashPickup (subclasses)
                if (io.GetComponentInParent<NetworkedItemPickup>() != null) return true;
                if (io.GetComponentInParent<CashPickup>() != null) return true;
                if (io.GetComponentInParent<Draggable>() != null) return true;
            }
            catch { }
            return false;
        }
    }
}
