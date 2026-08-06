using PropHunt.Game;

namespace PropHunt.Disguise
{
    /// <summary>
    /// The lobby dressing room: wear props before the match starts, to find out what hiding as one actually feels like -
    /// how short you get, what you can see over, whether the thing reads as scenery when you turn it.
    ///
    /// It goes through the SAME machinery as a real disguise: the host approves the prop, the choice is synced so
    /// everyone sees each other, the body is hidden, the camera swings to third person, [F] turns the prop. The first
    /// version of this was local-only and that was the wrong call - a dressing room nobody else can see is a different
    /// feature, and it left a second rendering path to keep in step with the real one.
    ///
    /// What it deliberately does NOT include: decoys, concussions and the whistle. Those are round mechanics with
    /// budgets and cooldowns attached; handing them out in the lobby would mean the round starts with them already
    /// spent, or with the rules quietly different for whoever was fiddling beforehand.
    /// </summary>
    internal static class PropPreview
    {
        /// <summary>Whether the local player is wearing a lobby prop, according to the synced state.</summary>
        internal static bool Active
        {
            get
            {
                var ctl = GameModeController.Active;
                if (ctl == null || ctl.State == null || ctl.State.Phase != RoundPhase.Lobby) return false;
                return ctl.LocalLobbyProp >= 0;
            }
        }

        /// <summary>The prop the local player is wearing in the lobby, or -1.</summary>
        internal static int PropId
        {
            get
            {
                var ctl = GameModeController.Active;
                return ctl != null ? ctl.LocalLobbyProp : -1;
            }
        }

        /// <summary>Ask for a different random prop. Returns false when this client's pool is still empty, which is the
        /// honest answer in a fresh lobby rather than a silent no-op.</summary>
        internal static bool Roll()
        {
            var ctl = GameModeController.Active;
            if (ctl == null) return false;
            try
            {
                int next = PropCatalog.RandomBecomableId(exclude: PropId);
                if (next < 0) return false;
                ctl.RequestSelectProp(next);
                return true;
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] lobby prop roll failed: " + e.Message); return false; }
        }

        /// <summary>Back to being a person.</summary>
        internal static void Clear()
        {
            var ctl = GameModeController.Active;
            if (ctl == null || !Active) return;
            try { ctl.RequestSelectProp(-1); }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] lobby prop clear failed: " + e.Message); }
        }
    }
}
