using HarmonyLib;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Dragging;
using Il2CppScheduleOne.Equipping;
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
                // A hunter holding the trash grabber MUST be able to grab trash (it clears hiding spots). Trash pickup
                // runs through the trash's Draggable interaction (TrashItem.Interacted -> PickupTrash), which IsPickup
                // would otherwise block. Allow it when the grabber is equipped AND the hovered object is REAL trash.
                // A disguised hider is a prop clone, NOT a TrashItem, so this never grabs a hider (they're still caught
                // with the gun); and the prompt stays hidden below, so a missing prompt can't out a hider.
                if (Equippable_TrashGrabber.IsEquipped && IsTrash(hovered)) return true;
                if (IsPickup(hovered)) return false;   // block picking up a prop; doors / others still interact
            }
            catch { }
            return true;
        }

        /// <summary>True if the interactable belongs to a real trash item (grabbable by the trash grabber). A disguised
        /// hider is a prop clone, not a TrashItem, so this is false for them.</summary>
        private static bool IsTrash(InteractableObject io)
        {
            try { return io.GetComponentInParent<Il2CppScheduleOne.Trash.TrashItem>() != null; }
            catch { return false; }
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

    /// <summary>
    /// During a round, block the vanilla right-click-hold "pick up / destroy" of world buildables (mailboxes, trash
    /// cans, props, ...). It otherwise lets a player dismantle the very objects hiders blend in with - including
    /// removing props from the safehouse. The hider's own tooling is direct key reads (PropPicker), not this path.
    /// Trash pickup goes through the Draggable interaction (still allowed above), not this destroy loop. No-op outside a round.
    /// </summary>
    [HarmonyPatch(typeof(InteractionManager), "CheckRightClick")]
    internal static class BlockRightClickPickupPatch
    {
        private static bool Prefix() => !RoundInteractionGate.RoundActive;   // false -> skip the whole right-click destroy loop
    }
}
