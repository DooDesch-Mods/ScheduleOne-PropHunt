using System.Collections.Generic;
using UnityEngine;
using PropHunt.Game;

namespace PropHunt.Disguise
{
    /// <summary>
    /// LOCAL-only: lets a hider disguised as a SHORT prop walk under low overhead obstacles (guardrails, fences,
    /// overhangs) by <see cref="Physics.IgnoreCollision"/>-ing those obstacle colliders against the player's
    /// CharacterController + body capsule - WITHOUT resizing the controller.
    ///
    /// Why not shrink the capsule: any change to Controller.height/radius/center breaks Unity's
    /// Controller.isGrounded, which gates PlayerMovement's gravity reset; the gravity accumulator then drives the
    /// player straight through the floor in a few frames (and persisting a shrink makes the inlined
    /// UpdatePlayerHeight launch the player upward). IgnoreCollision leaves grounding completely intact: we never
    /// ignore the floor (filtered by height), so the player still stands and still collides with walls.
    ///
    /// Only obstacles whose BOTTOM sits at/above the prop's top are ignored (the prop fits in the gap under them),
    /// so floors and floor-anchored walls keep blocking. Rescans as the player moves; fully restored on undisguise,
    /// role change, or round end. Applied to the LOCAL player only (remote hider bodies are visuals; their
    /// CharacterController is authoritative on their own client).
    /// </summary>
    internal sealed class PropPassthrough
    {
        private readonly GameModeController _ctl;
        private readonly List<Collider> _ignored = new List<Collider>();
        private CharacterController _cc;
        private CapsuleCollider _capCol;
        private Vector3 _lastScanPos;
        private float _activeHeight;     // prop height the current ignore-set was built for (0 = inactive)
        private bool _active;

        private const float ScanRadius = 12f;
        private const float RescanMoveSqr = 4f;   // rescan after moving >2m

        internal PropPassthrough(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            try
            {
                float h = Patches.PropCollisionState.TargetHeight;   // prop height, clamped 0.5-1.85 (0 = none)
                bool want = _ctl.LocalRole == PlayerRole.Hider && _ctl.LocalPropId >= 0 && h > 0f && h < 1.8f;
                if (!want) { if (_active) Clear(); return; }

                EnsureRefs();
                if (_cc == null) return;
                var lp = Player.Local; if (lp == null) return;
                Vector3 pos = lp.transform.position;

                if (!_active || _activeHeight != h || (pos - _lastScanPos).sqrMagnitude > RescanMoveSqr)
                {
                    Clear();
                    Scan(pos, h);
                    _active = true;
                    _activeHeight = h;
                    _lastScanPos = pos;
                }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] passthrough tick failed: " + e.Message); }
        }

        private void EnsureRefs()
        {
            try
            {
                if (_cc == null) { var pm = PlayerSingleton<PlayerMovement>.Instance; if (pm != null) _cc = pm.Controller; }
                if (_capCol == null) { var lp = Player.Local; if (lp != null) _capCol = lp.CapCol; }
            }
            catch { }
        }

        private void Scan(Vector3 pos, float propHeight)
        {
            float feetY = pos.y;
            float propTop = feetY + propHeight;
            // DIAGNOSTIC: scan ALL layers so we can see what colliders are actually around the player (the guardrail
            // may not be on "Default", or its collider may reach the floor). Logs nearby non-floor colliders.
            var hits = Physics.OverlapSphere(pos, ScanRadius, ~0, QueryTriggerInteraction.Ignore);
            if (hits == null) return;
            int n = 0, total = 0, logged = 0;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < hits.Length; i++)
            {
                var col = hits[i];
                if (col == null || col.isTrigger) continue;
                total++;
                var b = col.bounds;
                // dump EVERY nearby collider (name/layer/bounds/distance) so we can see exactly what the rail is
                if (logged < 40)
                {
                    sb.Append($"\n   '{TrimName(col.name)}' L{col.gameObject.layer} y[{b.min.y:F2}..{b.max.y:F2}] d={Vector3.Distance(pos, b.ClosestPoint(pos)):F1}");
                    logged++;
                }
                // ignore obstacles whose bottom is at/above the prop top -> the prop fits in the gap under them.
                if (b.min.y < propTop - 0.15f) continue;
                if (b.min.y > feetY + 3f) continue;   // far overhead - already under it
                Ignore(col, true);
                _ignored.Add(col);
                n++;
            }
            Core.LogDebug($"[PropHunt] passthrough scan: total={total} ignored={n} feetY={feetY:F2} propTop={propTop:F2} nearby(above-feet):{sb}");
        }

        private static string TrimName(string n) => string.IsNullOrEmpty(n) ? "?" : (n.Length > 28 ? n.Substring(0, 28) : n);

        private void Ignore(Collider col, bool ignore)
        {
            // CharacterController inherits Collider, so it can be passed straight to Physics.IgnoreCollision.
            try { if (_cc != null) Physics.IgnoreCollision(_cc, col, ignore); } catch { }
            try { if (_capCol != null) Physics.IgnoreCollision(_capCol, col, ignore); } catch { }
        }

        private void Clear()
        {
            for (int i = 0; i < _ignored.Count; i++) { var c = _ignored[i]; if (c != null) Ignore(c, false); }
            _ignored.Clear();
            _active = false;
            _activeHeight = 0f;
        }

        internal void Dispose() { try { Clear(); } catch { } }
    }
}
