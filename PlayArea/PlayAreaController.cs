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
        // Three seconds, not ten. The wall is visible and the beeps start immediately, so the window only has to be
        // long enough to turn around - ten was long enough to cross a street and come back, which made the boundary
        // a suggestion.
        private const float GraceSeconds = 3f;

        /// <summary>
        /// How much of the prop the water has to cover before it counts as hiding in the water.
        ///
        /// A fixed depth was wrong, and wrong in a way that showed up as "the prop is clearly in the water and nothing
        /// happens": the question is never how deep the water is, it is whether the water hides the thing you are
        /// pretending to be. 40cm swallows a sign whole and barely wets a vending machine. So the threshold is a
        /// fraction of the PROP's own height, measured from its base at the player's feet.
        /// </summary>
        private const float DeepWaterPropFraction = 0.35f;

        /// <summary>Floor and ceiling for that fraction. The floor keeps a puddle from catching a tiny prop the moment
        /// it gets its feet wet; the ceiling means even a tall prop eventually counts, rather than letting someone
        /// stand chest-deep in a lake as a lamppost.</summary>
        private const float MinWaterDepth = 0.2f;
        private const float MaxWaterDepth = 0.8f;

        /// <summary>The most of a prop that may be under water before it counts as hidden. This caps the FLOOR above:
        /// a bottle is about 25cm, so a 20cm floor let it stand 80% submerged and legal - which is exactly the hiding
        /// place the rule exists to close. No prop may ever be allowed to sit deeper than this share of itself.</summary>
        private const float SubmergedFraction = 0.6f;
        private static readonly string[] OobClips = { "beep", "alarm", "warning", "alert" };
        private readonly GameModeController _ctl;
        private float _outsideSince = -1f;
        private float _nextBeep;

        internal bool LocalOutside { get; private set; }
        /// <summary>True when the warning is about WATER rather than the area edge, so the HUD can say which.</summary>
        internal bool LocalWater { get; private set; }
        internal float GraceLeft { get; private set; }

        internal PlayAreaController(GameModeController ctl) { _ctl = ctl; }

        /// <summary>How deep the water may be here before it counts, for the prop this player is currently wearing.
        /// Undisguised (a hunter, or a hider before picking) falls back to the middle of the range - there is no prop
        /// to measure, and the point of the rule is the prop.</summary>
        private float DeepWaterLimit()
        {
            float propH = 0f;
            try
            {
                int id = _ctl.LocalPropId;
                if (id >= 0) propH = Disguise.PropCatalog.HeightOf(id);
            }
            catch { }
            if (propH <= 0f) return (MinWaterDepth + MaxWaterDepth) * 0.5f;
            // The ceiling is the smaller of the absolute cap and what this prop can hide behind, and the floor may not
            // climb above it - otherwise the floor itself becomes the hiding place for anything shorter than it.
            float ceiling = Mathf.Min(MaxWaterDepth, propH * SubmergedFraction);
            return Mathf.Clamp(propH * DeepWaterPropFraction, Mathf.Min(MinWaterDepth, ceiling), ceiling);
        }

        internal void Tick()
        {
            LocalOutside = false;
            LocalWater = false;
            GraceLeft = 0f;
            var s = _ctl.State;
            if (s == null || s.AreaRadius <= 0f) { _outsideSince = -1f; return; }
            if (_ctl.Phase != RoundPhase.Hiding && _ctl.Phase != RoundPhase.Hunting) { _outsideSince = -1f; return; }
            var role = _ctl.LocalRole;
            if (role != PlayerRole.Hider && role != PlayerRole.Hunter) { _outsideSince = -1f; return; }
            // Someone already out of the round is a spectator and may roam freely. A caught hider still reads as
            // PlayerRole.Hider under the Spectator caught-behaviour, so the role check above does NOT cover them -
            // without this they got the warning beeps and the full-screen banner while spectating, and kept sending
            // reports the host silently threw away.
            if (_ctl.LocalEliminated || _ctl.LocalSpectating) { _outsideSince = -1f; return; }
            try
            {
                var lp = Player.Local;
                if (lp == null) return;
                var p = lp.transform.position;
                float dx = p.x - s.AreaX, dz = p.z - s.AreaZ;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);
                // Deep water counts as leaving the area. A disguise sits with its BASE at the player's feet, so water
                // that reaches up the prop hides it - and a hider could simply wait out the round down there. Wading
                // stays legal; the line is drawn against the prop's own height rather than a fixed depth.
                bool inDeepWater = WaterProbe.DepthOverFeet(lp, out _) > DeepWaterLimit()
                                   && !WaterProbe.HasRoofAbove(lp);
                if (dist > s.AreaRadius || inDeepWater)
                {
                    LocalWater = inDeepWater;
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
                            _ctl.ReportOutOfBounds(LocalWater);
                        _outsideSince = Time.time;   // reset to avoid spamming
                    }
                }
                else { _outsideSince = -1f; }
            }
            catch (System.Exception e) { Core.LogDebug("playarea tick failed: " + e.Message); }
        }
    }
}
