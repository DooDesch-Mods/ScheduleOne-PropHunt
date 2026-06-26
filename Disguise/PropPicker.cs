using UnityEngine;
using PropHunt.Game;

namespace PropHunt.Disguise
{
    /// <summary>
    /// LOCAL hider tooling during the Hiding/Hunting phases: look at a world object and become it. Each frame
    /// it raycasts from the player camera, takes the nearest BECOMABLE hit and latches it briefly, exposing its
    /// name for the HUD ("[E] become &lt;name&gt;"). [E] selects (re-selectable any time); [F] freezes/unfreezes the
    /// current prop's world rotation (so you can look around without the prop spinning). Sends intents through
    /// the controller (host validates). TODO(testing): rebindable keys.
    /// </summary>
    internal sealed class PropPicker
    {
        private readonly GameModeController _ctl;
        private float _holdUntil;
        private const float HoldTime = 0.4f;   // latch the last valid target so a key press still lands
        private bool _rotating;
        private float _yaw;
        private float _nextYawSend;
        private const float RotateSpeed = 5f;   // degrees per mouse-X unit while holding [F]
#if DEBUG
        private int _lastLoggedId = -2;
#endif
        internal int CurrentTargetId { get; private set; } = -1;
        internal string CurrentTargetName { get; private set; }

        internal PropPicker(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            // hiders can pick (and re-pick) a prop during hiding AND hunting
            bool canPick = (_ctl.Phase == RoundPhase.Hiding || _ctl.Phase == RoundPhase.Hunting) && _ctl.LocalRole == PlayerRole.Hider;
            if (!canPick)
            {
                CurrentTargetId = -1; CurrentTargetName = null;
                if (_rotating) StopRotating();
                return;
            }
            try
            {
                UpdateTarget();
#if DEBUG
                if (CurrentTargetId != _lastLoggedId)
                {
                    _lastLoggedId = CurrentTargetId;
                    Core.LogDebug(CurrentTargetId >= 0
                        ? $"[PropHunt] crosshair -> '{CurrentTargetName}' (id {CurrentTargetId})"
                        : "[PropHunt] crosshair -> <nothing becomable>");
                }
#endif
                if (Input.GetKeyDown(KeyCode.E) && CurrentTargetId >= 0)
                {
                    _ctl.RequestSelectProp(CurrentTargetId);
                    Core.LogDebug($"[PropHunt] selected prop {CurrentTargetId} ({CurrentTargetName}).");
                }
                // [2] become a random prop (no aiming needed)
                if (Input.GetKeyDown(KeyCode.Alpha2)) { _ctl.RequestSelectRandomProp(); Core.LogDebug("[PropHunt] random prop requested ([2])."); }
                // [Q] drop a decoy of the current prop;  [G] concussion grenade (stun nearby hunters)
                if (Input.GetKeyDown(KeyCode.Q) && _ctl.LocalPropId >= 0) { _ctl.RequestDropDecoy(); Core.LogDebug("[PropHunt] decoy requested ([Q])."); }
                if (Input.GetKeyDown(KeyCode.G)) { _ctl.RequestConcuss(); Core.LogDebug("[PropHunt] concussion requested ([G])."); }
                // [F] held + mouse = rotate the prop's facing (camera locked while rotating)
                HandleRotate();
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] picker tick failed: " + e.Message); }
        }

        /// <summary>Hold [F] + move the mouse to rotate the prop's facing. The camera is locked while holding so
        /// the mouse only turns the prop; the yaw applies locally each frame and syncs to the host throttled.</summary>
        private void HandleRotate()
        {
            bool holding = Input.GetKey(KeyCode.F) && _ctl.LocalPropId >= 0;
            if (holding)
            {
                if (!_rotating) { _rotating = true; _yaw = _ctl.LocalPropYaw; SetCanLook(false); _nextYawSend = Time.time; }
                float dx = Input.GetAxis("Mouse X");
                if (Mathf.Abs(dx) > 0.0001f) { _yaw += dx * RotateSpeed; _ctl.SetLocalYaw(_yaw); }
                if (Time.time >= _nextYawSend) { _nextYawSend = Time.time + 0.15f; _ctl.RequestRotate(_yaw); }
            }
            else if (_rotating) StopRotating();
        }

        private void StopRotating()
        {
            _rotating = false;
            SetCanLook(true);
            _ctl.RequestRotate(_yaw);   // push the final facing
        }

        private static void SetCanLook(bool can)
        {
            try { var cam = PlayerSingleton<PlayerCamera>.Instance; if (cam != null) cam.SetCanLook(can); } catch { }
        }

        private void UpdateTarget()
        {
            var cam = PlayerSingleton<PlayerCamera>.Instance;
            if (cam == null || cam.Camera == null) { CurrentTargetId = -1; CurrentTargetName = null; return; }
            var t = cam.Camera.transform;

            int foundId = -1; string foundName = null;
            // look at EVERY hit along the ray and take the nearest BECOMABLE one (junk colliders in front no
            // longer mask the prop behind them, which was making the crosshair flicker on/off)
            var hits = Physics.RaycastAll(t.position, t.forward, 8f);
            if (hits != null && hits.Length > 0)
            {
                System.Array.Sort(hits, (System.Comparison<RaycastHit>)((a, b) => a.distance.CompareTo(b.distance)));
                for (int i = 0; i < hits.Length; i++)
                {
                    var hit = hits[i];
                    MeshFilter mf = null;
                    if (hit.collider != null)
                    {
                        mf = hit.collider.GetComponentInParent<MeshFilter>();
                        if (mf == null) mf = hit.collider.GetComponentInChildren<MeshFilter>();
                    }
                    if (mf == null && hit.transform != null) mf = hit.transform.GetComponentInChildren<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    int id = PropCatalog.IdForMesh(mf.sharedMesh);
                    if (id >= 0) { foundId = id; foundName = PropCatalog.ById(id)?.Name; break; }
                }
            }

            if (foundId >= 0) { CurrentTargetId = foundId; CurrentTargetName = foundName; _holdUntil = Time.time + HoldTime; }
            else if (Time.time >= _holdUntil) { CurrentTargetId = -1; CurrentTargetName = null; }
            // else: keep the latched target a moment longer so [E] lands even between flickering frames
        }
    }
}
