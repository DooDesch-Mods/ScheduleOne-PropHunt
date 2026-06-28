using HarmonyLib;
using Il2CppScheduleOne.FX;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// Suppress the on-player blood / impact puff from a hunter's gun during a round. The hit FX is spawned by
    /// FXManager.CreateImpactFX(impact, target) purely because the target is a Player; it is LOCAL to the shooter
    /// and not networked. A hider is caught via the prop hitbox (a host-validated tag), NOT by bullet damage, so
    /// blood on the player is misleading feedback - CatchController shows a hit effect at the PROP instead. Muzzle
    /// flash, fire sound and the bullet trail are separate calls and are left untouched. NPC hits keep their FX.
    /// (The networked avatar blood-mist is already gated off by the PlayerHealth.TakeDamage prefix.)
    /// </summary>
    [HarmonyPatch(typeof(FXManager), nameof(FXManager.CreateImpactFX))]
    internal static class ImpactFxSuppressPatch
    {
        private static bool Prefix(Il2CppScheduleOne.Combat.IDamageable target)
        {
            try
            {
                var ctl = GameModeController.Active;
                if (ctl == null || !ctl.RoundActive) return true;          // normal FX outside a round
                if (target != null && target.TryCast<Player>() != null) return false;   // skip the on-player blood
            }
            catch { }
            return true;
        }
    }
}
