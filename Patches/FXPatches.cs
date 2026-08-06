using HarmonyLib;
using Il2CppScheduleOne.FX;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// The game's own bullet decides who was hit; this reads that decision instead of re-deriving it.
    ///
    /// Equippable_RangedWeapon.Fire resolves a shot with its own SphereCastAll and, per damaged target, calls
    /// SendImpact + FXManager.CreateImpactFX(impact, target). That call is the only place the victim, the hit point
    /// and the shooter arrive together, and it runs on the SHOOTER's client - which is exactly where a catch should
    /// be judged, because the shooter is the one who saw the prop. A hider's catch hitbox now hangs under their
    /// Player (see DisguiseController.EnsureHitbox), so the game attributes a shot on the prop to that hider.
    ///
    /// The FX itself is left alone: the impact point sits on the prop's surface, so the game draws its hit effect
    /// exactly where the bullet visibly landed. Real bullet DAMAGE never applies - Side Hustle's gamemode hygiene
    /// prefixes PlayerHealth.TakeDamage for the whole session - so a hider loses prop HP, never health.
    /// </summary>
    [HarmonyPatch(typeof(FXManager), nameof(FXManager.CreateImpactFX))]
    internal static class BulletHitReadPatch
    {
        private static void Postfix(Il2CppScheduleOne.Combat.Impact impact, Il2CppScheduleOne.Combat.IDamageable target)
        {
            try
            {
                var ctl = GameModeController.Active;
                if (ctl == null || !ctl.RoundActive || ctl.LocalRole != PlayerRole.Hunter) return;
                if (target == null || impact == null) return;
                // Melee and punches come through here too; only gunfire should resolve a catch this way.
                if (impact.ImpactType != Il2CppScheduleOne.Combat.EImpactType.Bullet) return;

                // A managed `as`/`is` cast never sees an Il2Cpp type - go through the GameObject.
                var victim = target.gameObject != null ? target.gameObject.GetComponent<Player>() : null;
                if (victim == null) return;

                ctl.OnVanillaBulletHitPlayer(victim, impact.HitPoint);
            }
            catch { }
        }
    }
}
