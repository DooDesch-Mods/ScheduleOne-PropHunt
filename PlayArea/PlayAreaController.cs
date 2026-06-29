using UnityEngine;
using PropHunt.Game;
using PropHunt.Taunt;

namespace PropHunt.PlayArea
{
    /// <summary>
    /// LOCAL play-area enforcement. Each client checks its own player's distance from the synced area centre;
    /// once outside for a grace period it reports out-of-bounds to the host, which re-validates and eliminates.
    /// The HUD reads <see cref="LocalOutside"/>/<see cref="GraceLeft"/> for the warning. Centre+radius are set
    /// by the host at round start (host position).
    /// </summary>
    internal sealed class PlayAreaController
    {
        private const float GraceSeconds = 10f;
        private static readonly string[] OobClips = { "beep", "alarm", "warning", "alert" };
        private readonly GameModeController _ctl;
        private float _outsideSince = -1f;
        private float _nextBeep;

        internal bool LocalOutside { get; private set; }
        internal float GraceLeft { get; private set; }

        internal PlayAreaController(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            LocalOutside = false;
            GraceLeft = 0f;
            var s = _ctl.State;
            if (s == null || s.AreaRadius <= 0f) { _outsideSince = -1f; return; }
            if (_ctl.Phase != RoundPhase.Hiding && _ctl.Phase != RoundPhase.Hunting) { _outsideSince = -1f; return; }
            var role = _ctl.LocalRole;
            if (role != PlayerRole.Hider && role != PlayerRole.Hunter) { _outsideSince = -1f; return; }
            try
            {
                var lp = Player.Local;
                if (lp == null) return;
                var p = lp.transform.position;
                float dx = p.x - s.AreaX, dz = p.z - s.AreaZ;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                if (dist > s.AreaRadius)
                {
                    LocalOutside = true;
                    if (_outsideSince < 0f) _outsideSince = Time.time;
                    GraceLeft = Mathf.Max(0f, GraceSeconds - (Time.time - _outsideSince));
                    // audible warning while outside - beeps faster as the grace window runs out
                    if (Time.time >= _nextBeep)
                    {
                        _nextBeep = Time.time + Mathf.Lerp(0.3f, 1f, Mathf.Clamp01(GraceLeft / GraceSeconds));
                        try { TauntSounds.PlayFx(OobClips, p, 0.6f); } catch { }
                    }
                    if (GraceLeft <= 0f)
                    {
                        // hunters who glitch out are teleported back to the area centre; hiders are eliminated.
                        if (role == PlayerRole.Hunter)
                            RoundEnvironment.TeleportLocalInto(s.AreaX, s.AreaY, s.AreaZ, _ctl.LocalId);
                        else
                            _ctl.ReportOutOfBounds();
                        _outsideSince = Time.time;   // reset to avoid spamming
                    }
                }
                else { _outsideSince = -1f; }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] playarea tick failed: " + e.Message); }
        }
    }
}
