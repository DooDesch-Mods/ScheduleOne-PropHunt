using UnityEngine;
using PropHunt.Game;
using PropHunt.Config;

namespace PropHunt.UI
{
    /// <summary>
    /// First-round friction killer. Shows a short ROLE CARD when the local player enters Hiding (role, objective,
    /// win condition + the keys for that role), auto-dismissed after a few seconds or on the first input, plus a
    /// persistent "[H] Controls" hint that toggles a full controls overlay (Hider / Hunter / Spectator). Every key
    /// label comes from <see cref="KeyBinds"/> so the help text can never drift from what the controllers read.
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

        internal void DrawGui(bool suppressCard = false)
        {
            try
            {
                if (_helpOpen) DrawHelp();
                else if (_cardActive && !suppressCard) DrawRoleCard();   // hidden while the taunt wheel is open

                if (_ctl.RoundActive && !_helpOpen)
                    Hint($"[{KeyBinds.Name(KeyBinds.Help)}] Controls", Screen.height - 26f);
            }
            catch { }
        }

        private void DrawRoleCard()
        {
            var role = _ctl.LocalRole;
            string title, goal; string[] keys; Color titleColor;
            if (role == PlayerRole.Hunter)
            {
                title = "YOU ARE A HUNTER";
                titleColor = new Color(1f, 0.45f, 0.4f);
                goal = "Find and shoot the disguised props before the hunt timer runs out.";
                keys = new[]
                {
                    $"[{KeyBinds.Name(KeyBinds.Catch)}]  shoot / catch   (big props take more hits)",
                    "You are blinded until the hunt begins.",
                };
            }
            else
            {
                title = "YOU ARE A HIDER";
                titleColor = new Color(0.3f, 1f, 0.5f);
                goal = "Disguise as a prop and survive until the hunt timer runs out.";
                keys = new[]
                {
                    $"[{KeyBinds.Name(KeyBinds.Become)}] become what you look at     [{KeyBinds.Name(KeyBinds.RandomProp)}] random prop",
                    $"[{KeyBinds.Name(KeyBinds.Rotate)}]+mouse rotate     [{KeyBinds.Name(KeyBinds.SlowWalk)}] slow-walk",
                    $"[{KeyBinds.Name(KeyBinds.Decoy)}] decoy     [{KeyBinds.Name(KeyBinds.Concussion)}] stun     [{KeyBinds.Name(KeyBinds.Taunt)}] taunt (hold = wheel)",
                    $"[{KeyBinds.Name(KeyBinds.ThirdPerson)}] third-person",
                };
            }

            float w = 540f, h = 44f + 26f + keys.Length * 22f + 28f;
            var box = new Rect((Screen.width - w) / 2f, Screen.height * 0.15f, w, h);
            var prev = GUI.color; GUI.color = new Color(0.05f, 0.06f, 0.09f, 0.86f);
            GUI.Box(box, GUIContent.none); GUI.color = prev;
            float y = box.y + 12f;
            Line(box, ref y, title, 22, titleColor, FontStyle.Bold);
            Line(box, ref y, goal, 15, Color.white, FontStyle.Normal);
            y += 6f;
            foreach (var k in keys) Line(box, ref y, k, 15, new Color(0.72f, 0.88f, 1f), FontStyle.Normal);
            y += 4f;
            Line(box, ref y, $"[{KeyBinds.Name(KeyBinds.Help)}] controls anytime   -   leave the safehouse when you are ready", 12, new Color(0.7f, 0.7f, 0.78f), FontStyle.Italic);
        }

        private void DrawHelp()
        {
            string[] hider =
            {
                $"[{KeyBinds.Name(KeyBinds.Become)}] become looked-at prop",
                $"[{KeyBinds.Name(KeyBinds.RandomProp)}] random prop",
                $"[{KeyBinds.Name(KeyBinds.Rotate)}]+mouse rotate facing",
                $"[{KeyBinds.Name(KeyBinds.SlowWalk)}] slow-walk",
                $"[{KeyBinds.Name(KeyBinds.Decoy)}] drop decoy",
                $"[{KeyBinds.Name(KeyBinds.Concussion)}] concussion (stun hunters)",
                $"[{KeyBinds.Name(KeyBinds.Taunt)}] taunt (hold = wheel)",
                $"[{KeyBinds.Name(KeyBinds.ThirdPerson)}] third-person view",
            };
            string[] hunter =
            {
                $"[{KeyBinds.Name(KeyBinds.Catch)}] shoot / catch props",
                "Props have HP - big props take more hits.",
                "Blinded during the hide phase.",
            };
            string[] spec =
            {
                $"[{KeyBinds.Name(KeyBinds.SpectatorToggle)}] follow-cam <-> freecam",
                $"[{KeyBinds.Name(KeyBinds.SpectatorNext)}] next player (follow-cam)",
            };

            float w = 560f, h = 392f;
            var box = new Rect((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);
            GUI.Box(box, "PropHunt - Controls");
            GUILayout.BeginArea(new Rect(box.x + 18f, box.y + 30f, w - 36f, h - 44f));
            Section("HIDER", hider);
            Section("HUNTER", hunter);
            Section("SPECTATOR (when caught)", spec);
            GUILayout.Space(8f);
            GUILayout.Label($"[{KeyBinds.Name(KeyBinds.Help)}] close", _muted.Value);
            GUILayout.EndArea();
        }

        private void Section(string head, string[] lines)
        {
            GUILayout.Space(6f);
            GUILayout.Label(head, _head.Value);
            foreach (var l in lines) GUILayout.Label("   " + l, _body.Value);
        }

        // ---- styles (lazy; GUI.skin is only valid inside OnGUI) ----
        private readonly System.Lazy<GUIStyle> _head = new System.Lazy<GUIStyle>(() =>
            new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, fontSize = 15, normal = { textColor = new Color(0.3f, 1f, 0.5f) } });
        private readonly System.Lazy<GUIStyle> _body = new System.Lazy<GUIStyle>(() =>
            new GUIStyle(GUI.skin.label) { fontSize = 14, normal = { textColor = new Color(0.82f, 0.88f, 1f) } });
        private readonly System.Lazy<GUIStyle> _muted = new System.Lazy<GUIStyle>(() =>
            new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Italic, fontSize = 12, normal = { textColor = new Color(0.7f, 0.7f, 0.78f) } });

        private static void Line(Rect box, ref float y, string text, int size, Color color, FontStyle style)
        {
            var s = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = size, fontStyle = style, wordWrap = true };
            s.normal.textColor = color;
            float h = size + 8f;
            GUI.Label(new Rect(box.x + 12f, y, box.width - 24f, h), text, s);
            y += h;
        }

        private static void Hint(string text, float y)
        {
            var s = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 13, fontStyle = FontStyle.Bold };
            s.normal.textColor = new Color(0.75f, 0.85f, 1f, 0.85f);
            GUI.Label(new Rect(0, y, Screen.width, 22f), text, s);
        }
    }
}
