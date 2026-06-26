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
        private static void Postfix(PlayerCamera __instance)
        {
            if (!ThirdPersonView.Active) return;
            try
            {
                var cam = __instance.Camera;
                if (cam == null) return;
                var t = cam.transform;
                Vector3 eye = t.position;
                Vector3 back = -t.forward;

                float dist = ThirdPersonView.Distance;
                if (Physics.Raycast(eye, back, out var hit, dist + 0.3f))
                    dist = Mathf.Max(0.3f, hit.distance - 0.3f);   // don't push through a wall behind the player

                t.position = eye + back * dist + Vector3.up * ThirdPersonView.Height;
                // rotation is left as the game set it, so mouse-look still aims the view forward
            }
            catch { }
        }
    }
}
