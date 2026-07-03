using System;

namespace PropHunt.View
{
    /// <summary>
    /// Local third-person "you are DOWN" camera for a ragdolled player (friendly-fire KO / concussion): it detaches
    /// the <see cref="PlayerCamera"/> to a fixed point behind + above the body and re-aims it at the ragdoll's centre
    /// bone every frame, so the downed player watches their own body on the ground and knows they're out. Mirrors the
    /// Faded mod's bodycam - <c>PlayerCamera.OverrideTransform</c> for the pulled-out position (built-in ease +
    /// canLook off) plus a per-frame <c>Transform.LookAt(Avatar.CenterPointTransform)</c> for the aim, restored via
    /// <c>StopTransformOverride(reenableCameraLook: true)</c> (never leaves the camera stuck). Purely local + cosmetic
    /// and only ever driven for <see cref="Player.Local"/>; it rides on top of PropHunt's existing networked
    /// <c>SendPassOut</c> ragdoll (this class only moves the local camera, not the body).
    /// </summary>
    internal static class BodyCam
    {
        private static bool _active;

        /// <summary>True while the downed body-cam owns the local camera. The camera postfix aims it and the
        /// third-person toggle stands down while this is set.</summary>
        internal static bool Active => _active;

        private const float PullBack = 3.6f;    // metres behind the body
        private const float Lift = 2.1f;         // metres above the body
        private const float StartLerp = 0.5f;    // ease the camera out (snappy - you should see yourself drop right away)
        private const float StopLerp = 0.4f;     // ease back to first person (no black cover here, so lerp - don't snap)

        /// <summary>Pull the local camera out to third person and show the player their own body (call on the downed
        /// rising edge, AFTER the ragdoll starts).</summary>
        internal static void Start()
        {
            if (_active) return;
            try
            {
                var cam = PlayerSingleton<PlayerCamera>.Instance;
                var lp = Player.Local;
                if (cam == null || lp == null) return;
                UnityEngine.Vector3 body = BodyPoint(lp);
                UnityEngine.Vector3 back = -lp.transform.forward;
                UnityEngine.Vector3 pull = body + back * PullBack + UnityEngine.Vector3.up * Lift;
                UnityEngine.Quaternion look = UnityEngine.Quaternion.LookRotation((body - pull).normalized, UnityEngine.Vector3.up);
                lp.SetVisibleToLocalPlayer(true);              // un-hide our own avatar from our own camera
                cam.blockNextStopTransformOverride = false;    // guarantee our Stop() isn't swallowed by a stray earlier value
                cam.OverrideTransform(pull, look, StartLerp, false);   // detach + world-space ease-out; also sets canLook = false
                _active = true;
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] bodycam start failed: " + e.Message); }
        }

        /// <summary>Keep the pulled-out camera aimed at the settling ragdoll (called every frame from the camera
        /// LateUpdate postfix while active).</summary>
        internal static void Track(PlayerCamera cam)
        {
            if (!_active || cam == null) return;
            try
            {
                var lp = Player.Local;
                if (lp == null) return;
                cam.transform.LookAt(BodyPoint(lp));           // Unity LookAt on the camera transform (NOT PlayerCamera.LookAt, which spins the player root)
                lp.SetVisibleToLocalPlayer(true);              // the game resets local visibility each frame - re-assert it
            }
            catch { }
        }

        /// <summary>Ease the camera back to first person and re-hide the own body (call on recovery / teardown). Safe
        /// to call when not active.</summary>
        internal static void Stop()
        {
            if (!_active) return;
            _active = false;
            try
            {
                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam != null) cam.StopTransformOverride(StopLerp, true, true);   // reenableCameraLook MUST be true, or the camera stays locked
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] bodycam stop failed: " + e.Message); }
            try { Player.Local?.SetVisibleToLocalPlayer(false); } catch { }
        }

        // The ragdoll's centre (MiddleSpine) bone, so the aim follows the body as it settles; falls back to just above
        // the capsule if the avatar isn't resolvable.
        private static UnityEngine.Vector3 BodyPoint(Player lp)
        {
            try { if (lp.Avatar != null && lp.Avatar.CenterPointTransform != null) return lp.Avatar.CenterPointTransform.position; } catch { }
            return lp.transform.position + UnityEngine.Vector3.up;
        }
    }
}
