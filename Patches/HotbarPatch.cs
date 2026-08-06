using HarmonyLib;
using Il2CppScheduleOne.PlayerScripts;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// Stops the vanilla hotbar from reacting at all while the local player is a disguised hider, so the number keys
    /// belong to PropHunt ([2] changes prop) and no item can be held on a prop.
    ///
    /// Clearing <c>HotbarEnabled</c>/<c>EquippingEnabled</c> once is not enough, which is why this patch exists: every
    /// scene-state transition re-applies equipping from the state's own properties
    /// (<c>StateProperties.Apply</c> -> <c>SetEquippingEnabled(to.Equipping == Enabled)</c>), so opening the phone,
    /// entering a vehicle or any menu silently hands the hotbar back mid-round. Owning the input method itself cannot
    /// be undone that way.
    ///
    /// <c>UpdateHotbarSelection</c> is the single entry point for all of it - number keys, the holster action, the
    /// scroll wheel and the gamepad inventory buttons - so one prefix covers every route to equipping something.
    /// </summary>
    [HarmonyPatch(typeof(PlayerInventory), "UpdateHotbarSelection")]
    internal static class HotbarSelectionBlockPrefix
    {
        private static bool Prefix()
        {
            // Disguised, NOT "an active hider in a round". Those are different states and the difference is a bug: in the
            // lobby dressing room nobody has a role yet, so the role-and-phase test said no and [2] went to inventory
            // slot 2 instead of rolling a new prop. Wearing a prop is the thing that makes a hotbar wrong.
            try { return !HotbarSuppression.Disguised; }
            catch { return true; }   // never let this patch be the reason a player cannot use their inventory
        }
    }

    /// <summary>Whether the local player should have no hotbar right now: an active hider carries no equipment, and
    /// their number keys drive the disguise instead. Hunters need theirs (the weapon), so they are untouched.</summary>
    internal static class HotbarSuppression
    {
        internal static bool Active
        {
            get
            {
                var ctl = GameModeController.Active;
                if (ctl == null) return false;
                if (ctl.LocalRole != PlayerRole.Hider) return false;
                var phase = ctl.State?.Phase ?? RoundPhase.Lobby;
                return phase == RoundPhase.Hiding || phase == RoundPhase.Hunting;
            }
        }

        /// <summary>Whether the local player is currently WEARING a prop - the state in which player-shaped controls
        /// stop making sense. Covers the lobby dressing room as well as a live round.</summary>
        internal static bool Disguised
        {
            get
            {
                var ctl = GameModeController.Active;
                if (ctl == null) return false;
                if (Disguise.PropPreview.Active) return true;
                return Active && ctl.LocalPropId >= 0;
            }
        }
    }

    /// <summary>
    /// A prop does not carry a torch. The phone's flashlight is bound to [F], which PropHunt also uses to turn a prop,
    /// so a hider aiming their disguise switched a light on inside it - a crate with a beam coming out of it is not
    /// hiding, and it was visible to every other player.
    ///
    /// TWO patch points, because one was not enough and the failure was instructive: patching only the phone's private
    /// <c>ToggleFlashlight</c> changed nothing observable - the light came on for everybody and could not be switched
    /// off again, while the owner saw it flicker. That is the signature of a target that got inlined: the patch is
    /// applied, the method is never called, and the only thing still running was our own per-frame flag clearing,
    /// fighting a toggle that had already gone out over the network.
    ///
    /// <c>Player.SetFlashlightOn_Server</c> is where the state actually leaves this machine, and it is public, so it
    /// survives as a real method. Blocking it is what makes the light stay off for everyone else.
    /// </summary>
    // A SECOND prefix used to sit on this same method (Patches/FlashlightPatch.cs) and cancelled the toggle for the
    // whole round, not just while disguised - so a HUNTER could not switch their torch on either. Two prefixes on one
    // method both run and the stricter one wins, which is why the narrower rule here never got a say. Removed; hunters
    // carry a light, hiders do not.
    [HarmonyPatch(typeof(Il2CppScheduleOne.UI.Phone.Phone), "ToggleFlashlight")]
    internal static class FlashlightTogglePrefix
    {
        private static bool Prefix()
        {
            try { return !HotbarSuppression.Disguised; }
            catch { return true; }
        }
    }

    [HarmonyPatch(typeof(Il2CppScheduleOne.PlayerScripts.Player), nameof(Il2CppScheduleOne.PlayerScripts.Player.SetFlashlightOn_Server))]
    internal static class FlashlightNetworkPrefix
    {
        private static bool Prefix(Il2CppScheduleOne.PlayerScripts.Player __instance, bool on)
        {
            try
            {
                if (!on) return true;                      // turning it OFF is always allowed
                if (!HotbarSuppression.Disguised) return true;
                // Only the local player's own request is ours to refuse; a relayed state from someone else is not.
                var local = Il2CppScheduleOne.PlayerScripts.Player.Local;
                if (local == null || __instance == null) return true;
                return local.GetInstanceID() != __instance.GetInstanceID();
            }
            catch { return true; }
        }
    }
}
