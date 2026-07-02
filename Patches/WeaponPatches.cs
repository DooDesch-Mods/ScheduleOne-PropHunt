using HarmonyLib;
using Il2CppScheduleOne.Equipping;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Storage;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// ScheduleOne.Equipping.Equippable_RangedWeapon behaviour this patch relies on:
    ///  - Fire() has no tased/stun guard, so a concussed hunter could still shoot -> block it.
    ///  - Reload() plays the full reload animation (trigger + ReloadStartTime wait) and refills the clip
    ///    FROM a magazine item that GetMagazine() finds in a hotbar slot (ID == Magazine.ID). The hunter is
    ///    never given one, so after the first clip IsReloadReady() returns false and they are stuck dry.
    ///    Rather than bypass Reload() (which skips the animation), we make GetMagazine() always succeed with
    ///    an inexhaustible magazine: the native reload then runs unmodified - real animation, real refill -
    ///    and never runs out.
    /// All gated to the local PropHunt hunter during a round; no effect on normal play.
    /// </summary>
    internal static class WeaponPatchGate
    {
        internal static bool HunterInRound()
        {
            var ctl = GameModeController.Active;
            return ctl != null && ctl.RoundActive && ctl.LocalRole == PlayerRole.Hunter;
        }
    }

    /// <summary>Block firing while the local hunter is KNOCKED DOWN (friendly-fire KO or concussion) - and, as a belt,
    /// while the vanilla IsTased flag is set. The knockdown auto-clears from the host clock.</summary>
    [HarmonyPatch(typeof(Equippable_RangedWeapon), nameof(Equippable_RangedWeapon.Fire))]
    internal static class TasedNoFirePatch
    {
        private static bool Prefix()
        {
            var ctl = GameModeController.Active;
            if (ctl == null || !ctl.RoundActive) return true;
            if (ctl.LocalDowned) return false;   // knocked down -> can't fire
            var p = Player.Local;
            return !(p != null && p.IsTased);    // false -> skip Fire() while tased (belt-and-braces)
        }
    }

    /// <summary>
    /// Register a PropHunt catch from a REAL fired shot. The postfix runs only after the hunter's gun actually
    /// fires (ammo + aim + cooldown gated by the game), so spamming left-click without firing can no longer
    /// "hit" anything, and the catch resolves from where the weapon truly shot. The actual decoy/prop resolution
    /// (camera sphere-sweep) lives in CatchController.ResolveShot.
    /// </summary>
    [HarmonyPatch(typeof(Equippable_RangedWeapon), nameof(Equippable_RangedWeapon.Fire))]
    internal static class HunterRangedCatchPatch
    {
        private static void Postfix()
        {
            if (!WeaponPatchGate.HunterInRound()) return;
            try { GameModeController.Active?.OnLocalHunterFired(150f); } catch { }   // guns reach across the play area
            // NOTE: we deliberately do NOT top up the clip here - the clip must still deplete so the hunter has to
            // RELOAD (a balancing gate that prevents continuous fire). Infinite RESERVE (so reload never runs dry) is
            // handled by HunterInfiniteMagazinePatch on GetMagazine, for both the pistol AND the pump shotgun.
        }
    }

    /// <summary>Melee equivalent: a real melee strike (short reach) resolves a catch the same way.</summary>
    [HarmonyPatch(typeof(Equippable_MeleeWeapon), "ExecuteHit")]
    internal static class HunterMeleeCatchPatch
    {
        private static void Postfix()
        {
            if (!WeaponPatchGate.HunterInRound()) return;
            try { GameModeController.Active?.OnLocalHunterFired(4f); } catch { }
        }
    }

    /// <summary>
    /// The hunter's reload is self-sufficient: GetMagazine() always hands back an inexhaustible magazine, so
    /// the native Reload() runs its full animation + clip-refill and the hunter never runs out - no magazine
    /// item needed in the hotbar. The throwaway magazine is built from GetDefaultInstance (the same proven
    /// factory used to arm the hunter) and kept off the inventory entirely; re-pinning its value before every
    /// reload keeps it from ever depleting (so the native path never spawns reload trash or drops quantity).
    /// </summary>
    [HarmonyPatch(typeof(Equippable_RangedWeapon), nameof(Equippable_RangedWeapon.GetMagazine))]
    internal static class HunterInfiniteMagazinePatch
    {
        private static void Postfix(Equippable_RangedWeapon __instance, ref bool __result, ref StorableItemInstance mag)
        {
            if (!WeaponPatchGate.HunterInRound()) return;
            int reserve = (__instance.MagazineSize > 0 ? __instance.MagazineSize : 7) * 5;

            // a real magazine already in the hotbar -> keep its rounds topped so it never depletes
            if (__result && mag != null)
            {
                try { var ii = mag.TryCast<IntegerItemInstance>(); if (ii != null) ii.SetValue(reserve); } catch { }
                return;
            }

            // otherwise hand back an inexhaustible throwaway (never added to the inventory), rebuilt fresh from the
            // weapon's OWN Magazine def each call so it is always full regardless of the ammo item TYPE:
            //  - pistol magazine = IntegerItemInstance (rounds live in a Value) -> the Magazine reload reads/top its
            //    Value, so we SetValue(reserve);
            //  - pump-shotgun shells = a plain quantity stack -> the Incremental reload consumes mag.ChangeQuantity(-1)
            //    per shell, so GetDefaultInstance(reserve) seeds a high Quantity that covers the whole reload.
            // The previous code only handled IntegerItemInstance: for the shotgun the TryCast was null, GetMagazine
            // kept returning false, the incremental reload never started, and the shotgun ran dry after one clip.
            try
            {
                var def = __instance.Magazine;
                if (def == null) return;
                var inst = def.GetDefaultInstance(reserve);
                if (inst == null) return;
                var ii = inst.TryCast<IntegerItemInstance>();
                if (ii != null) ii.SetValue(reserve);
                var sii = inst.TryCast<StorableItemInstance>();
                if (sii == null) return;
                mag = sii;
                __result = true;
            }
            catch { }
        }
    }
}
