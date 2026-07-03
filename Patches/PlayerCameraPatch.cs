using HarmonyLib;
using UnityEngine;
using Il2CppScheduleOne.PlayerScripts;
using PropHunt.View;

namespace PropHunt.Patches
{
    /// <summary>
    /// Third-person camera for PropHunt: the game re-centres the camera on the player's head every frame in
    /// <c>PlayerCamera.LateUpdate</c>, so we postfix it and, when <see cref="ThirdPersonView.Active"/>, pull the
    /// camera back along the view direction (clamped by a wall raycast so it doesn't clip through geometry) and
    /// lift it a little. This lets a disguised hider actually see their prop. Only acts during a PropHunt round
    /// (the flag is off otherwise) - normal play is untouched.
    /// </summary>
    [HarmonyPatch(typeof(PlayerCamera), "LateUpdate")]
    internal static class PlayerCameraThirdPersonPatch
    {
        private static float _tpResolved;   // smoothed rendered third-person distance (snap-in on walls, ease-out otherwise)

        private static void Postfix(PlayerCamera __instance)
        {
            // A downed player's body-cam owns the camera: aim it at their own ragdoll (position is set by the game's
            // PlayerCamera.OverrideTransform; we only re-aim here, after LateUpdate, so the aim tracks the settling body).
            if (BodyCam.Active) { BodyCam.Track(__instance); return; }
            if (!SpectatorCam.Active && !ThirdPersonView.Active) { _tpResolved = 0f; return; }
            try
            {
                var cam = __instance.Camera;
                if (cam == null) return;
                var t = cam.transform;

                // Spectator follow-cam takes priority: place the camera behind the followed player and look at
                // them (mouse-look is locked while spectating, so we set the rotation ourselves).
                if (SpectatorCam.Active && SpectatorCam.Target != null)
                {
                    Vector3 aim = SpectatorCam.Target.position + Vector3.up * 1.2f;   // upper body
                    Vector3 fwd = SpectatorCam.Target.forward;
                    if (fwd.sqrMagnitude < 0.0001f) fwd = Vector3.forward;
                    Vector3 camPos = aim - fwd * SpectatorCam.Distance + Vector3.up * SpectatorCam.Height;
                    Vector3 d = camPos - aim;
                    float dl = d.magnitude;
                    if (dl > 0.01f && Physics.Raycast(aim, d / dl, out var wall, dl))
                        camPos = wall.point - (d / dl) * 0.2f;   // pull in so we don't sit inside a wall
                    t.position = camPos;
                    var look = aim - camPos;
                    if (look.sqrMagnitude > 0.0001f) t.rotation = Quaternion.LookRotation(look.normalized, Vector3.up);
                    return;
                }

                if (!ThirdPersonView.Active) return;
                Vector3 eye = t.position;
                Vector3 back = -t.forward;
                float wallDist = ThirdPersonView.Distance;
                if (Physics.Raycast(eye, back, out var hit, wallDist + 0.3f))
                    wallDist = Mathf.Max(0.3f, hit.distance - 0.3f);   // don't push through a wall behind the player
                // Snap IN the instant a wall appears (never render a frame through geometry) but EASE back out when it
                // clears or the player zooms out, so the pull-back never pops. (Distance is already smoothed upstream.)
                if (_tpResolved <= 0f) _tpResolved = wallDist;
                float lambda = wallDist < _tpResolved ? 40f : 8f;
                _tpResolved = Mathf.Lerp(_tpResolved, wallDist, 1f - Mathf.Exp(-lambda * Time.deltaTime));
                t.position = eye + back * _tpResolved + Vector3.up * ThirdPersonView.Height;
                // rotation is left as the game set it, so mouse-look still aims the view forward
            }
            catch { }
        }
    }
}
