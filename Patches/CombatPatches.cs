using HarmonyLib;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.PlayerScripts.Health;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// During a PropHunt round, hunters carry a real weapon (for feel + sound + the catch ray on the same
    /// click), but a hider must NOT die to bullets - catching is the host-validated tag/hit count, not the
    /// gun's damage. So we cancel real player damage selectively:
    ///   - hiders/spectators: never take damage (they are "caught" via <see cref="RoundLogic.ApplyCatch"/>).
    ///   - hunters: take damage only when FriendlyFire is enabled.
    /// Outside a PropHunt round (no active session) this is a no-op - normal Schedule I damage is untouched.
    /// </summary>
    [HarmonyPatch(typeof(PlayerHealth), nameof(PlayerHealth.TakeDamage), new[] { typeof(float), typeof(bool), typeof(bool) })]
    internal static class PlayerDamagePatch
    {
        private static bool Prefix(PlayerHealth __instance)
        {
            try
            {
                var ctl = GameModeController.Active;
                if (ctl == null || !ctl.RoundActive) return true;   // not in a round -> normal damage

                Player player = __instance.GetComponentInParent<Player>();
                if (player == null) player = __instance.GetComponentInChildren<Player>();
                if (player == null) return true;

                ulong id = PlayerRegistry.IdForPlayer(player);
                var role = ctl.RoleOf(id);
                if (role == PlayerRole.Hider || role == PlayerRole.Spectator) return false;   // caught via tags, never killed
                if (role == PlayerRole.Hunter && !ctl.Settings.FriendlyFire) return false;     // FF off -> hunters invulnerable to each other
                return true;
            }
            catch { return true; }
        }
    }
}
