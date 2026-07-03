using UnityEngine;
using Il2CppScheduleOne.PlayerScripts;
using PropHunt.Game;
using PropHunt.Config;

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

        // hunters aim catch raycasts from the camera, so they are locked to first person; only hiders may use
        // the third-person inspection view (to see their own prop disguise).
        private bool Allowed => _ctl.RoundActive && _ctl.LocalRole != PlayerRole.Hunter && !_ctl.LocalSpectating;

        internal bool IsOn => _on && Allowed;

        /// <summary>Reset to first person (called on a role flip so a new hunter is not stuck pulled-back); the
        /// next Tick applies it. The player can re-toggle with V.</summary>
        internal void ForceOff() => _on = false;

        internal void Tick()
        {
            try
            {
                if (BodyCam.Active) return;   // a downed player's body-cam owns the camera + own-body visibility - stand down
                if (Input.GetKeyDown(KeyBinds.ThirdPerson) && Allowed) _on = !_on;
                bool active = _on && Allowed;
                ThirdPersonView.Active = active;
                if (active) UpdateCameraForProp();
                SetArmsVisible(!active);
                // Show your OWN body to your camera only in third person AND when not disguised (a disguised hider
                // should see their prop, not their body). First person / disguised -> hidden (no floating head).
                // Re-applied EVERY frame (not change-tracked) because the game resets local visibility each frame.
                SetOwnBodyVisible(active && _ctl.LocalPropId < 0);
            }
            catch { }
        }

        // Scale the third-person pull-back to the disguise prop's largest dimension so the player frames their prop:
        // a tiny prop pulls the camera in close, a big prop pulls it far back (instead of a fixed distance that's
        // too far for small props and clips into big ones). Undisguised falls back to the default offset.
        private void UpdateCameraForProp()
        {
            try
            {
                float size = _ctl.LocalPropId >= 0 ? PropHunt.Disguise.PropCatalog.SizeOf(_ctl.LocalPropId) : 0f;
                if (size > 0f)
                {
                    ThirdPersonView.Distance = Mathf.Clamp(size * 1.7f + 1.0f, 1.6f, 9f);
                    ThirdPersonView.Height = Mathf.Clamp(size * 0.4f, 0.2f, 2f);
                }
                else { ThirdPersonView.Distance = 2.8f; ThirdPersonView.Height = 0.4f; }
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
