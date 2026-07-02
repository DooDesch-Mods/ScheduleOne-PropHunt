using PropHunt.Game;

namespace PropHunt.UI.Hud
{
    /// <summary>
    /// Owns the PropHunt in-game uGUI HUD. A single ScreenSpaceOverlay canvas is created lazily on the first frame a
    /// session is live (<see cref="GameModeController.Active"/> != null) and torn down when the session ends -
    /// including the MatchEnd auto-return, which this catches when Active goes null. Driven from Core.OnUpdate (the
    /// family's live-refresh pump - no injected MonoBehaviour). The HUD is DISPLAY-ONLY: it has no GraphicRaycaster
    /// and every graphic sets raycastTarget=false, so it can never intercept the catch click or the worldspace phone.
    /// </summary>
    internal static class HudController
    {
        private static HudRoot _root;

        /// <summary>Per-frame from Core.OnUpdate. Builds the canvas on the first session frame, refreshes it live,
        /// and tears it down once the session is gone.</summary>
        internal static void Tick()
        {
            try
            {
                var ctl = GameModeController.Active;
                // Only ever show the HUD inside the gameplay scene. If we're back at the menu - host left, quit, or a
                // client lost the host - even a stale session reference must not keep the canvas (and its bar) running.
                if (ctl == null || !InGameplayScene())
                {
                    if (_root != null) Teardown();   // session ended / left the gameplay scene
                    return;
                }
                if (_root == null) _root = new HudRoot();
                _root.Apply(ctl);
            }
            catch { }
        }

        private static bool InGameplayScene()
        {
            try { return UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "Main"; }
            catch { return true; }
        }

        /// <summary>Flash the crosshair hitmarker on the local shooter (a shot connected with a decoy or a player).
        /// No-op if the HUD isn't up yet. Called from CatchController the instant a hit is detected locally.</summary>
        internal static void ShowHitmarker() { try { _root?.ShowHitmarker(); } catch { } }

        /// <summary>Destroy the HUD canvas. Called from Core.OnExitToHub and whenever Tick sees no active session.</summary>
        internal static void Teardown()
        {
            try { _root?.Destroy(); } catch { }
            _root = null;
        }
    }
}
