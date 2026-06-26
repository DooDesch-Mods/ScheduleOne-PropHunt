using UnityEngine;
using Il2CppScheduleOne.PlayerScripts;
using PropHunt.Game;

namespace PropHunt.View
{
    /// <summary>
    /// LOCAL third-person toggle (key V) during a PropHunt round, so a hider can see their own disguise. Flips
    /// <see cref="ThirdPersonView.Active"/> (the camera postfix does the actual offset) and hides the
    /// first-person viewmodel arms while in third person. Off by default; aiming (catch/pick raycasts) comes
    /// from the camera, so it is most precise in first person - this is mainly an inspection view.
    /// </summary>
    internal sealed class ThirdPersonController
    {
        private readonly GameModeController _ctl;
        private bool _on;
        private bool? _appliedArmsVisible;
        private ViewmodelAvatar _viewmodel;

        internal ThirdPersonController(GameModeController ctl) { _ctl = ctl; }

        internal bool IsOn => _on && _ctl.RoundActive;

        /// <summary>Reset to first person (called on a role flip so a new hunter is not stuck pulled-back); the
        /// next Tick applies it. The player can re-toggle with V.</summary>
        internal void ForceOff() => _on = false;

        internal void Tick()
        {
            try
            {
                if (Input.GetKeyDown(KeyCode.V) && _ctl.RoundActive) _on = !_on;
                bool active = _on && _ctl.RoundActive;
                ThirdPersonView.Active = active;
                SetArmsVisible(!active);
                // Show your OWN body to your camera only in third person AND when not disguised (a disguised hider
                // should see their prop, not their body). First person / disguised -> hidden (no floating head).
                // Re-applied EVERY frame (not change-tracked) because the game resets local visibility each frame.
                SetOwnBodyVisible(active && _ctl.LocalPropId < 0);
            }
            catch { }
        }

        private void SetOwnBodyVisible(bool visible)
        {
            try
            {
                var lp = Player.Local;
                if (lp == null) return;
                lp.SetVisibleToLocalPlayer(visible);
                if (visible) { try { lp.SetThirdPersonMeshesVisibility(true); } catch { } }   // ensure the body meshes are on
            }
            catch { }
        }

        private void SetArmsVisible(bool visible)
        {
            if (_appliedArmsVisible == visible) return;
            _appliedArmsVisible = visible;
            try
            {
                if (_viewmodel == null) _viewmodel = UnityEngine.Object.FindObjectOfType<ViewmodelAvatar>();
                if (_viewmodel != null) _viewmodel.SetVisibility(visible);
            }
            catch { }
        }

        internal void Dispose()
        {
            _on = false;
            ThirdPersonView.Active = false;
            SetArmsVisible(true);
            SetOwnBodyVisible(false);   // back to standard first person (don't see your own body)
        }
    }
}
