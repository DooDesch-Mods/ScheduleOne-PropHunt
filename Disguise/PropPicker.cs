using UnityEngine;
using PropHunt.Game;
using PropHunt.Config;

namespace PropHunt.Disguise
{
    /// <summary>
    /// LOCAL hider tooling during the Hiding/Hunting phases: look at a world object and become it. Each frame
    /// it raycasts from the player camera, takes the nearest BECOMABLE hit and latches it briefly, exposing its
    /// name for the HUD ("[E] become &lt;name&gt;"). [E] selects (re-selectable any time); [F] freezes/unfreezes the
    /// current prop's world rotation (so you can look around without the prop spinning). Sends intents through
    /// the controller (host validates).
    /// </summary>
    internal sealed class PropPicker
    {
        private readonly GameModeController _ctl;
        private float _holdUntil;
        private float _lastRandomRoll;   // local echo of the host's prop-change cooldown
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
            // run the becomable-target raycast during the round for BOTH roles: it powers the world-interaction
            // suppression (a becomable prop must not be pickup-able by anyone) AND the hider's "[E] become" prompt.
            bool inRound = _ctl.Phase == RoundPhase.Hiding || _ctl.Phase == RoundPhase.Hunting;

            // The lobby dressing room gets the MOVEMENT half of the toolkit and nothing else: [2] for another prop and
            // [F] to turn it. Decoys, concussions and taunts stay out - they carry per-round budgets and cooldowns, and
            // spending them before the round exists would either start it short or make the rules differ per player.
            if (!inRound && _ctl.Phase == RoundPhase.Lobby && PropPreview.Active)
            {
                CurrentTargetId = -1; CurrentTargetName = null;
                try
                {
                    Patches.SlowWalk.Set(KeyBinds.Held(KeyBinds.SlowWalk));
                    if (KeyBinds.Down(KeyBinds.RandomProp)) PropPreview.Roll();
                    HandleRotate();
                }
                catch (System.Exception e) { Core.LogDebug("lobby prop tick failed: " + e.Message); }
                return;
            }

            if (!inRound)
            {
                CurrentTargetId = -1; CurrentTargetName = null;
                if (_rotating) StopRotating();
                Patches.SlowWalk.Restore();
                return;
            }
            try
            {
                UpdateTarget();

                // the become / decoy / rotate tooling itself is hider-only
                bool canPick = _ctl.LocalRole == PlayerRole.Hider;
                if (!canPick)
                {
                    if (_rotating) StopRotating();
                    Patches.SlowWalk.Restore();
                    TickGrabberProbe();
                    return;
                }

                // [Ctrl] held = slow-walk at half speed (replaces the blocked crouch); only while disguised
                Patches.SlowWalk.Set(_ctl.LocalPropId >= 0 && KeyBinds.Held(KeyBinds.SlowWalk));
                // [1] manual taunt + the hold-to-pick wheel is handled by TauntWheel (ticked by the controller).
#if DEBUG
                if (CurrentTargetId != _lastLoggedId)
                {
                    _lastLoggedId = CurrentTargetId;
                    Core.LogDebug(CurrentTargetId >= 0
                        ? $"crosshair -> '{CurrentTargetName}' (id {CurrentTargetId})"
                        : "crosshair -> <nothing becomable>");
                }
#endif
                if (KeyBinds.Down(KeyBinds.Become) && CurrentTargetId >= 0)
                {
                    _ctl.RequestSelectProp(CurrentTargetId);
                    Core.LogDebug($"selected prop {CurrentTargetId} ({CurrentTargetName}).");
                }
                // [2] become a random prop (no aiming needed) - only when the host allows it
                // Mirrors the host's cooldown locally so a held [2] does not fire a request per frame that the host
                // then throws away. The host decides; this only keeps the wire quiet.
                if (KeyBinds.Down(KeyBinds.RandomProp) && (_ctl.Settings == null || _ctl.Settings.AllowRandomChange)
                    && UnityEngine.Time.time - _lastRandomRoll >= Game.GameModeController.PropChangeCooldownSeconds)
                {
                    _lastRandomRoll = UnityEngine.Time.time;
                    _ctl.RequestSelectRandomProp();
                    Core.LogDebug("random prop requested ([2]).");
                }
                // [Q] drop a decoy of the current prop;  [G] concussion grenade (stun nearby hunters)
                if (KeyBinds.Down(KeyBinds.Decoy) && _ctl.LocalPropId >= 0) { _ctl.RequestDropDecoy(); Core.LogDebug("decoy requested ([Q])."); }
                if (KeyBinds.Down(KeyBinds.Concussion)) { _ctl.RequestConcuss(); Core.LogDebug("concussion requested ([G])."); }
                // [F] held + mouse = rotate the prop's facing (camera locked while rotating)
                HandleRotate();
                HandleLock();
            }
            catch (System.Exception e) { Core.LogDebug("picker tick failed: " + e.Message); }
        }

        /// <summary>
        /// Fire button: lock the prop where it stands, mid-air included. Press again to drop.
        ///
        /// The fire button because that is where every Prop Hunt player's hand already is - Call of Duty binds Lock to
        /// the same trigger - and because a disguised hider has no weapon, so nothing else wants it. Rotation is already
        /// independent of the camera here, so unlike CoD this is purely the gravity half of that ability.
        ///
        /// Ignored while a UI has the mouse. A click meant for the phone or a menu must not leave someone hanging in the
        /// air wondering what happened.
        /// </summary>
        private void HandleLock()
        {
            if (_ctl.WornPropId < 0) return;
            if (!Input.GetMouseButtonDown(0)) return;
            if (UiHasTheMouse()) return;
            _ctl.TogglePropLock();
        }

        /// <summary>True when a click belongs to the interface rather than the world.</summary>
        private static bool UiHasTheMouse()
        {
            try
            {
                if (Il2CppScheduleOne.GameInput.IsTyping) return true;
                var pause = Singleton<Il2CppScheduleOne.UI.PauseMenu>.Instance;
                if (pause != null && pause.IsPaused) return true;
                var phone = PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.Instance;
                if (phone != null && phone.IsOpen) return true;
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Hunter with the trash grabber: grabbing AT a disguised hider makes their prop whistle.
        ///
        /// A hunter who suspects a bin and clicks it currently gets nothing at all - the disguise clone is render-only
        /// (no colliders, deliberately, so it cannot be picked up), so the grabber has nothing to interact with and the
        /// click is indistinguishable from a mis-click on scenery. The whistle answers the actual question: that is a
        /// player. It costs the hider their position, which is the fair trade for having been correctly identified.
        ///
        /// The ray is aimed at the shootable hitbox (which DOES exist, under the player) rather than the clone, and it
        /// is kept to the grabber's own arm's length so this is never a ranged detector.
        /// </summary>
        private void TickGrabberProbe()
        {
            try
            {
                if (!Il2CppScheduleOne.Equipping.Equippable_TrashGrabber.IsEquipped) return;
                // [E], not the mouse. The grabber picks trash up through the INTERACTION system, so the key a hunter
                // actually presses at a suspicious bin is the interact key - the mouse button was simply the wrong one
                // and nothing ever fired. Reading it raw is fine: Unity input is not consumed by the interaction that
                // may also be handling this press.
                if (!KeyBinds.Down(KeyBinds.Become)) return;

                var cam = PlayerSingleton<PlayerCamera>.Instance;
                if (cam == null || cam.Camera == null) return;
                var t = cam.Camera.transform;

                // Match how the game itself finds something to interact with: a SPHERE cast from the camera over its
                // full interaction range, and EVERY hit considered - not the first. A thin ray over a guessed 3m was
                // the wrong shape and the wrong distance, so grabbing at a prop that plainly looked in reach did
                // nothing. Every hit matters because the prop's hitbox can sit behind the visible mesh's collider.
                var hits = Physics.SphereCastAll(t.position, ProbeRadius, t.forward, GrabberReach, ~0, QueryTriggerInteraction.Collide);
                if (hits == null) return;
                for (int i = 0; i < hits.Length; i++)
                {
                    ulong victim = HitboxOwner(hits[i].collider);
                    if (victim == 0) continue;
                    _ctl.RequestProbeProp(victim);
                    return;
                }
            }
            catch (System.Exception e) { Core.LogDebug("grabber probe failed: " + e.Message); }
        }

        /// <summary>
        /// The game's own interaction reach (InteractionManager.MaxInteractionRange, and the length of its casts), so
        /// grabbing at a prop works exactly as far as grabbing at a bin does.
        /// </summary>
        private const float GrabberReach = 4f;

        /// <summary>
        /// Wider than vanilla's 0.075 interaction probe, and deliberately so.
        ///
        /// That thin sphere is tuned for picking up a bag you can already see and want to hit precisely. Here the hunter
        /// is testing a suspicion about something the size of a crate, and the cost of being generous is one whistle -
        /// which only ever fires on something that really is a player. Being stingy costs the hunter the answer.
        /// </summary>
        private const float ProbeRadius = 0.35f;

        /// <summary>The steam id behind a disguise hitbox, or 0. The hitbox is named "ph_prop_&lt;steamId&gt;" and hangs
        /// under the wearer, which is what makes the owner readable from a raw collider hit.</summary>
        private static ulong HitboxOwner(Collider col)
        {
            try
            {
                if (col == null || col.gameObject == null) return 0;
                string n = col.gameObject.name;
                if (string.IsNullOrEmpty(n) || !n.StartsWith("ph_prop_")) return 0;
                return ulong.TryParse(n.Substring("ph_prop_".Length), out var id) ? id : 0;
            }
            catch { return 0; }
        }

        /// <summary>Hold [F] + move the mouse to rotate the prop's facing. The camera is locked while holding so
        /// the mouse only turns the prop; the yaw applies locally each frame and syncs to the host throttled.</summary>
        private void HandleRotate()
        {
            bool holding = KeyBinds.Held(KeyBinds.Rotate) && _ctl.WornPropId >= 0;
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
                    if (IsOurs(hit.transform)) continue;   // our own disguise/decoy clones are not becomable props
                    MeshFilter mf = null;
                    if (hit.collider != null)
                    {
                        mf = hit.collider.GetComponentInParent<MeshFilter>();
                        if (mf == null) mf = hit.collider.GetComponentInChildren<MeshFilter>();
                    }
                    if (mf == null && hit.transform != null) mf = hit.transform.GetComponentInChildren<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null) continue;
                    int id = PropCatalog.IdForMeshFilter(mf);
                    // Not becomable = the host cannot draw it, so becoming it would leave us looking like a player on
                    // their screen. Skip it and keep searching along the ray rather than offering a broken disguise.
                    if (id >= 0 && !PropCatalog.IsBecomable(id)) continue;
                    if (id >= 0) { foundId = id; foundName = PropCatalog.ById(id)?.Name; break; }
                }
            }

            if (foundId >= 0) { CurrentTargetId = foundId; CurrentTargetName = foundName; _holdUntil = Time.time + HoldTime; }
            else if (Time.time >= _holdUntil) { CurrentTargetId = -1; CurrentTargetName = null; }
            // else: keep the latched target a moment longer so [E] lands even between flickering frames
        }

        /// <summary>True if the transform belongs to one of OUR runtime clones (disguise "ph_prop_*" or decoy
        /// "ph_decoy_*"); these use real prop meshes but must never count as becomable world props.</summary>
        private static bool IsOurs(Transform t)
        {
            for (int d = 0; d < 8 && t != null; d++)
            {
                var n = t.name;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("ph_")) return true;
                t = t.parent;
            }
            return false;
        }
    }
}
