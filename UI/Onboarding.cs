using UnityEngine;
using PropHunt.Game;
using PropHunt.Config;

namespace PropHunt.UI
{
    /// <summary>
    /// First-round friction killer. Decides WHEN to show a short ROLE CARD (on entering Hiding: role, objective,
    /// win condition + that role's keys, auto-dismissed after a few seconds or once you leave the safehouse) and a
    /// full controls overlay toggled by [H]. This class owns only the STATE + the content strings; the uGUI HUD
    /// (<see cref="Hud.HudRoot"/>) renders them. Every key label comes from <see cref="KeyBinds"/> so the help text
    /// can never drift from what the controllers read.
    /// </summary>
    internal sealed class Onboarding
    {
        private readonly GameModeController _ctl;
        private RoundPhase _lastPhase = RoundPhase.Lobby;
        private bool _cardActive;       // role card showing this round
        private bool _spawnCaptured;    // captured the round-start position yet?
        private Vector3 _spawnPos;      // where the local player started the round (safehouse)
        private float _cardShownAt;
        private bool _helpOpen;
        private const float CardMaxSeconds = 45f;   // safety cap so it never lingers forever if you stand still
        private const float LeaveRadius = 8f;       // dismissed once you walk this far from the safehouse spawn

        internal Onboarding(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            try
            {
                var phase = _ctl.Phase;
                if (phase == RoundPhase.Hiding && _lastPhase != RoundPhase.Hiding)
                {
                    var role = _ctl.LocalRole;
                    if (role == PlayerRole.Hider || role == PlayerRole.Hunter) { _cardActive = true; _spawnCaptured = false; _cardShownAt = Time.time; }
                }
                _lastPhase = phase;

                if (Input.GetKeyDown(KeyBinds.Help)) _helpOpen = !_helpOpen;
                if (_cardActive) UpdateCardDismiss();
            }
            catch { }
        }

        // The role card stays up until the player FIRST leaves the safehouse this round (walks LeaveRadius from the
        // round-start spot) - so there is time to actually read it - with a hard time cap as a safety net.
        private void UpdateCardDismiss()
        {
            if (Time.time - _cardShownAt > CardMaxSeconds) { _cardActive = false; return; }
            var phase = _ctl.Phase;
            if (phase != RoundPhase.Hiding && phase != RoundPhase.Hunting) { _cardActive = false; return; }   // round ended
            // Once the hunt begins the hide window is over: hunters are un-blinded and the "leave the safehouse"
            // guidance no longer applies, so clear the card for BOTH roles the moment Hunting starts. During Hiding
            // a hider keeps it until they first walk out of the safehouse (below).
            if (phase == RoundPhase.Hunting) { _cardActive = false; return; }
            // hiders keep it until they first walk out of the safehouse this round
            var lp = Player.Local;
            if (lp == null) return;
            var pos = lp.transform.position;
            if (!_spawnCaptured) { _spawnPos = pos; _spawnCaptured = true; return; }   // capture the round-start spot once the body exists
            float dx = pos.x - _spawnPos.x, dz = pos.z - _spawnPos.z;
            if (dx * dx + dz * dz > LeaveRadius * LeaveRadius) _cardActive = false;
        }

        internal void Reset() { _cardActive = false; _spawnCaptured = false; _helpOpen = false; _lastPhase = RoundPhase.Lobby; }

        // ---- state read by the HUD ----
        internal bool CardActive => _cardActive;
        internal bool HelpOpen => _helpOpen;
        internal bool ShowControlsHint => _ctl.RoundActive && !_helpOpen;
        internal bool LocalIsHunter => _ctl.LocalRole == PlayerRole.Hunter;

        // ---- role-card content (key labels via KeyBinds so they never drift) ----
        internal string CardTitle => LocalIsHunter ? "YOU ARE A HUNTER" : "YOU ARE A HIDER";

        internal string CardGoal => LocalIsHunter
            ? "Find and shoot the disguised props before the hunt timer runs out."
            : "Disguise as a prop and survive until the hunt timer runs out.";

        internal string[] CardKeys => LocalIsHunter
            ? new[]
            {
                $"[{KeyBinds.Name(KeyBinds.Catch)}]  shoot / catch   (big props take more hits)",
                "You are blinded until the hunt begins.",
            }
            : new[]
            {
                $"[{KeyBinds.Name(KeyBinds.Become)}] become what you look at      [{KeyBinds.Name(KeyBinds.RandomProp)}] random prop",
                $"[{KeyBinds.Name(KeyBinds.Rotate)}]+mouse rotate      [{KeyBinds.Name(KeyBinds.SlowWalk)}] slow-walk",
                $"[{KeyBinds.Name(KeyBinds.Decoy)}] decoy      [{KeyBinds.Name(KeyBinds.Concussion)}] stun      [{KeyBinds.Name(KeyBinds.Taunt)}] taunt (hold = wheel)",
                $"[{KeyBinds.Name(KeyBinds.ThirdPerson)}] third-person      [{KeyBinds.Name(KeyBinds.HighlightToggle)}] prop markers on/off",
            };

        internal string CardFooter => $"[{KeyBinds.Name(KeyBinds.Help)}] controls anytime   -   leave the safehouse when you are ready";

        internal string ControlsHintText => $"[{KeyBinds.Name(KeyBinds.Help)}] Controls";

        // ---- [H] controls-overlay content ----
        internal string[] HelpHider => new[]
        {
            $"[{KeyBinds.Name(KeyBinds.Become)}] become looked-at prop",
            $"[{KeyBinds.Name(KeyBinds.RandomProp)}] random prop",
            $"[{KeyBinds.Name(KeyBinds.Rotate)}]+mouse rotate facing",
            $"[{KeyBinds.Name(KeyBinds.SlowWalk)}] slow-walk",
            $"[{KeyBinds.Name(KeyBinds.Decoy)}] drop decoy",
            $"[{KeyBinds.Name(KeyBinds.Concussion)}] concussion (stun hunters)",
            $"[{KeyBinds.Name(KeyBinds.Taunt)}] taunt (hold = wheel)",
            $"[{KeyBinds.Name(KeyBinds.ThirdPerson)}] third-person view",
            $"[{KeyBinds.Name(KeyBinds.HighlightToggle)}] toggle becomable-prop markers (off = blend in)",
        };

        internal string[] HelpHunter => new[]
        {
            $"[{KeyBinds.Name(KeyBinds.Catch)}] shoot / catch props",
            "Props have HP - big props take more hits.",
            "Blinded during the hide phase.",
        };

        internal string[] HelpSpectator => new[]
        {
            $"[{KeyBinds.Name(KeyBinds.SpectatorToggle)}] follow-cam <-> freecam",
            $"[{KeyBinds.Name(KeyBinds.SpectatorNext)}] next player (follow-cam)",
        };
    }
}
