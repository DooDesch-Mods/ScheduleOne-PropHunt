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

    /// <summary>Block firing while the local player is tased (concussion stun); the stun auto-clears after ~2s.</summary>
    [HarmonyPatch(typeof(Equippable_RangedWeapon), nameof(Equippable_RangedWeapon.Fire))]
    internal static class TasedNoFirePatch
    {
        private static bool Prefix()
        {
            var ctl = GameModeController.Active;
            if (ctl == null || !ctl.RoundActive) return true;
            var p = Player.Local;
            return !(p != null && p.IsTased);   // false -> skip Fire() while tased
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
        private static IntegerItemInstance _mag;
        private static string _magId;

        private static void Postfix(Equippable_RangedWeapon __instance, ref bool __result, ref StorableItemInstance mag)
        {
            if (!WeaponPatchGate.HunterInRound()) return;
            int reserve = (__instance.MagazineSize > 0 ? __instance.MagazineSize : 7) * 5;

            // a real magazine already in the hotbar -> just keep it topped so it never depletes
            if (__result && mag != null)
            {
                try { var ii = mag.TryCast<IntegerItemInstance>(); if (ii != null) ii.SetValue(reserve); } catch { }
                return;
            }

            // otherwise hand back an inexhaustible throwaway (never added to the inventory)
            try
            {
                var def = __instance.Magazine;
                if (def == null) return;
                if (_mag == null || _magId != def.ID)
                {
                    var inst = def.GetDefaultInstance(1);
                    _mag = inst != null ? inst.TryCast<IntegerItemInstance>() : null;
                    _magId = def.ID;
                }
                if (_mag == null) return;
                _mag.SetValue(reserve);
                mag = _mag;
                __result = true;
            }
            catch { }
        }
    }
}
