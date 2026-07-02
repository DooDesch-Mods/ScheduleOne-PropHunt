using HarmonyLib;
using UnityEngine;
using Il2CppScheduleOne.PlayerScripts;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// Vanilla pass-out always shoves the ragdoll along the player's FORWARD (RpcLogic___PassOut does
    /// <c>Avatar.MiddleSpineRB.AddForce(transform.forward * 30)</c>), so a knocked-down hunter ALWAYS topples forward
    /// no matter where the friendly-fire shot or concussion came from. During a PropHunt round we redirect it: after
    /// the native impulse, cancel the forward component and re-apply the same magnitude in the knockback direction the
    /// host synced for this player (away from the attacker). Because <c>Player.PassOut</c> runs on every client that
    /// shows the ragdoll (owner via the SendPassOut RunLocally chain + observers), the fall direction is consistent.
    /// If no knockback is set (0,0) we leave the vanilla forward faint untouched, and outside a round this is inert.
    /// </summary>
    [HarmonyPatch(typeof(Player), nameof(Player.PassOut))]
    internal static class PassOutKnockbackPatch
    {
        private const float Impulse = 30f;   // matches the native MiddleSpineRB.AddForce magnitude

        private static void Postfix(Player __instance)
        {
            try
            {
                var ctl = GameModeController.Active;
                if (ctl == null || !ctl.RoundActive || __instance == null) return;
                ulong id = PlayerRegistry.IdForPlayer(__instance);
                if (!ctl.TryGetKnock(id, out float kx, out float kz)) return;   // no direction -> keep the vanilla forward faint
                var rb = __instance.Avatar?.MiddleSpineRB;
                if (rb == null) return;
                // Both AddForce calls land in the same physics step as the native one, so velocity accumulates:
                // forward*30 (native) - forward*30 (cancel) + knockDir*30 = knockDir*30. The native random torque stays,
                // so the tumble still looks natural.
                rb.AddForce(-__instance.transform.forward * Impulse, ForceMode.VelocityChange);
                rb.AddForce(new Vector3(kx, 0f, kz).normalized * Impulse, ForceMode.VelocityChange);
            }
            catch { }
        }
    }
}
