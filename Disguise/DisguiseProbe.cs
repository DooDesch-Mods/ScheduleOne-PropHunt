using System;

namespace PropHunt.Disguise
{
    /// <summary>
    /// Phase 0 probe (gate 3): toggle the visibility of every REMOTE player's third-person body locally,
    /// to confirm whether <c>Player.SetThirdPersonMeshesVisibility</c> hides a remote player's body for the
    /// local viewer - the basis of the disguise. If a remote player's body vanishes for you when toggled,
    /// the gate passes; if it only affects your own view, we fall back to toggling the Avatar's renderers.
    /// </summary>
    internal static class DisguiseProbe
    {
        private static bool _hidden;

        internal static void ToggleRemoteBodies()
        {
            _hidden = !_hidden;
            int n = 0;
            try
            {
                var list = Player.PlayerList;
                if (list == null) { Core.Log.Warning("[Probe] Player.PlayerList is null - are you in a game session?"); return; }
                for (int i = 0; i < list.Count; i++)
                {
                    Player p = list[i];
                    if (p == null || p.IsLocalPlayer) continue;
                    p.SetThirdPersonMeshesVisibility(!_hidden);
                    n++;
                }
                Core.Log.Msg($"[Probe] remote bodies {(_hidden ? "HIDDEN" : "shown")} on {n} remote player(s).");
            }
            catch (Exception e) { Core.Log.Warning("[Probe] failed: " + e.Message); }
        }
    }
}
