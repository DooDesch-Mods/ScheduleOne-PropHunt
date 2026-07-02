using HarmonyLib;
using Il2CppScheduleOne.PlayerScripts;
using PropHunt.Game;

namespace PropHunt.Patches
{
    /// <summary>
    /// Team-based map visibility. The phone map (M) draws a marker (a <see cref="Il2CppScheduleOne.Map.POI"/>) per
    /// co-op player. During a PropHunt round we hide markers the local player must not see so the map can't be used
    /// to cheat: a HUNTER never sees HIDER markers; a HIDER always sees HUNTERs, and sees other HIDERs only outside
    /// Infection mode (in Infection hiders can't see hiders either); a SPECTATOR sees everyone (default). Purely
    /// client-local rendering (no netcode), so filtering on the local client's own markers is enough.
    ///
    /// Patched on <c>POI.Update</c> (runs each frame the marker exists) so it re-evaluates LIVE as roles change
    /// (a caught hider, a between-round swap). Default is VISIBLE, so outside a round - or for any non-player POI
    /// (shops/quests) - markers are never touched, and a marker we hid is restored the moment it should show again.
    /// </summary>
    [HarmonyPatch(typeof(Il2CppScheduleOne.Map.POI), nameof(Il2CppScheduleOne.Map.POI.Update))]
    internal static class MapVisibilityPatch
    {
        private static void Postfix(Il2CppScheduleOne.Map.POI __instance)
        {
            try
            {
                var ui = __instance.UI;
                if (ui == null) return;
                var owner = OwnerOf(__instance);
                if (owner == null) return;   // not a player's marker (shop/quest/etc.) -> never touch it

                bool visible = true;   // default visible; only a live round + the rule can hide a marker
                var ctl = GameModeController.Active;
                if (ctl != null && ctl.RoundActive)
                {
                    ulong id = PlayerRegistry.IdForPlayer(owner);
                    if (id != 0UL && id != ctl.LocalId)
                        visible = CanLocalSeeOnMap(ctl.LocalRole, ctl.RoleOf(id), ctl.Settings);
                }
                var go = ui.gameObject;
                if (go.activeSelf != visible) go.SetActive(visible);
            }
            catch { }
        }

        /// <summary>The player whose map marker this POI is, or null if it isn't a player POI. PlayerList is tiny.</summary>
        private static Player OwnerOf(Il2CppScheduleOne.Map.POI poi)
        {
            try
            {
                var list = Player.PlayerList;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var p = list[i];
                        if (p != null && p.PoI == poi) return p;
                    }
            }
            catch { }
            return null;
        }

        /// <summary>Whether the LOCAL player may see the TARGET player's marker. One small table, gamemode-aware.</summary>
        private static bool CanLocalSeeOnMap(PlayerRole local, PlayerRole target, RoundSettings set)
        {
            if (local == PlayerRole.Spectator) return true;                    // spectators see everyone (default)
            if (local == PlayerRole.Hunter) return target != PlayerRole.Hider; // hunters never see hiders
            if (local == PlayerRole.Hider)
            {
                if (target == PlayerRole.Hunter) return true;                  // hiders always see hunters
                if (target == PlayerRole.Hider)
                    return set == null || set.Caught != CaughtBehavior.Infection;  // Infection: hiders don't see hiders
                return true;
            }
            return true;
        }
    }
}
