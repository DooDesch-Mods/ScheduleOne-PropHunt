namespace PropHunt.Compat
{
    /// <summary>
    /// Shows or hides a player's avatar to the LOCAL camera.
    ///
    /// Game 0.4.7 folded <c>Player.SetVisibleToLocalPlayer</c> into <c>Player.SetThirdPersonMeshesVisibility</c>:
    /// the one call now also moves the whole avatar between the "Player" and "Invisible" layers
    /// (<c>Player.ApplyThirdPersonMeshVisibility</c>). That is a recursive layer walk plus an event, so callers that
    /// re-assert visibility every frame go through here, which only calls into the game when the flag or the
    /// avatar's layer differs from the wanted state. The layer is part of the check because
    /// <c>Player.SetRagdolled</c> rewrites the owner's avatar layer without touching the flag.
    /// </summary>
    internal static class BodyVisibility
    {
        private static int _playerLayer = -2;
        private static int _invisibleLayer = -2;

        internal static void Set(Player player, bool visible)
        {
            if (player == null) return;
            try
            {
                if (_playerLayer == -2) { _playerLayer = LayerMask.NameToLayer("Player"); _invisibleLayer = LayerMask.NameToLayer("Invisible"); }
                int wantLayer = visible ? _playerLayer : _invisibleLayer;
                var av = player.Avatar;
                bool layerOk = av == null || av.gameObject.layer == wantLayer;
                if (player.ThirdPersonMeshesVisibleToLocalPlayer == visible && layerOk) return;
                player.SetThirdPersonMeshesVisibility(visible);
            }
            catch (System.Exception e) { Core.LogDebug("body visibility failed: " + e.Message); }
        }
    }
}
