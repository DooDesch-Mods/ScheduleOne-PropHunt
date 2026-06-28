using UnityEngine;
using PropHunt.Game;

namespace PropHunt.UI
{
    /// <summary>
    /// Functional in-round HUD + host pre-round setup screen, drawn via IMGUI from Core.OnGUI. Shows role,
    /// phase, timer, hiders-remaining, action hints, taunt flash, out-of-bounds warning, and the round-end
    /// banner. On the host in Lobby it draws the setup screen (configure the match, then "START MATCH").
    /// TODO(testing): re-skin with the DooDesch.UI design system (Banner/Toast/Slider/Segmented).
    /// </summary>
    internal static class PropHuntHud
    {
        internal static void Draw(GameModeController ctl)
        {
            if (ctl == null) return;
            var phase = ctl.Phase;

            string status = "PropHunt - " + phase;
            if (phase == RoundPhase.Hiding || phase == RoundPhase.Hunting)
            {
                status += $"   |   {ctl.SecondsLeft}s   |   Hiders left: {ctl.AliveHiderCount}   |   You: {ctl.LocalRole}";
                // global whistle countdown - shown to everyone (hiders need to know when the next reveal hits)
                int ws = ctl.SecondsToWhistle;
                if (ws >= 0) status += $"   |   whistle in {ws}s";
            }
            Center(status, 8, 22, Color.white);

            string view = ctl.ThirdPersonOn ? "[V] 1st-person" : "[V] 3rd-person";
            string hint = null;
            bool hiderHit = false;
            if (ctl.LocalRole == PlayerRole.Hider && (phase == RoundPhase.Hiding || phase == RoundPhase.Hunting))
            {
                if (phase == RoundPhase.Hunting && ctl.LocalHits > 0)
                {
                    hiderHit = true;
                    hint = $"Hit! ({ctl.LocalHits}/{ctl.LocalMaxHits} HP) - keep moving!     {view}";
                }
                else
                {
                    string tn = ctl.LookTargetName;
                    string become = tn != null ? $"[E] become {tn}" : "Look at a highlighted object to become it";
                    string lead = phase == RoundPhase.Hunting ? "Stay hidden!  " : "";
                    string rot = ctl.LocalPropId >= 0 ? "  [F]+mouse rotate" : "";
                    hint = $"{lead}{become}  [2] random{rot}   {view}";
                }
            }
            else if (phase == RoundPhase.Hiding && ctl.LocalRole == PlayerRole.Hunter) hint = "Get ready... (blinded until the hunt begins)";
            // hunters are first-person only - no [V] hint for them
            else if (phase == RoundPhase.Hunting && ctl.LocalRole == PlayerRole.Hunter) hint = "Find the props - [Left click] to catch (big props take more hits)";
            if (hint != null) Center(hint, 34, 18, hiderHit ? Color.red : Color.cyan);

            // crosshair-anchored "become" prompt - the top hint is easy to miss while aiming at the centre,
            // so mirror the game's own interaction prompt location (just under the crosshair).
            if (ctl.LocalRole == PlayerRole.Hider && (phase == RoundPhase.Hiding || phase == RoundPhase.Hunting))
            {
                string tn = ctl.LookTargetName;
                if (tn != null) Center($"[E] become {tn}", Screen.height / 2 + 28, 22, Color.cyan);
            }

            // explicit "you transformed" confirmation + abilities, independent of whether the camera sees the prop
            if (ctl.LocalRole == PlayerRole.Hider && ctl.LocalPropId >= 0)
            {
                var s = ctl.Settings;
                string pn = ctl.LocalPropName ?? "a prop";
                string chg = s.MaxPropChanges > 0 ? $"{Mathf.Max(0, s.MaxPropChanges - ctl.LocalChanges)} changes left" : "unlimited changes";
                Center($"You are now: {pn}  ({ctl.LocalMaxHits} HP, {chg})", 56, 18, Color.green);

                string decoy = s.MaxDecoys > 0 ? $"[Q] decoy ({Mathf.Max(0, s.MaxDecoys - ctl.LocalDecoysUsed)} left)" : "";
                string conc = s.ConcussCharges > 0 ? $"[G] concussion ({Mathf.Max(0, s.ConcussCharges - ctl.LocalConcussUsed)} left)" : "";
                string ab = (decoy + "   " + conc).Trim();
                if (ab.Length > 0) Center(ab, 78, 16, Color.cyan);
            }

            if (Time.time - ctl.LastTauntTime < 1.5f) Center("! TAUNT !", 60, 24, Color.yellow);

            if (ctl.LocalOutside) Center($"RETURN TO THE PLAY AREA!  {Mathf.CeilToInt(ctl.OobGrace)}s", 90, 26, Color.red);

            if (phase == RoundPhase.RoundEnd)
            {
                string w = ctl.State.Winner == 0 ? "HUNTERS WIN" : ctl.State.Winner == 1 ? "HIDERS WIN" : "ROUND OVER";
                Center(w, Screen.height / 2 - 24, 40, Color.white);
            }

            if (phase == RoundPhase.Lobby)
            {
                if (ctl.IsHost) DrawHostSetup(ctl);
                else Center("Waiting for the host to start the match...", Screen.height / 2, 22, Color.white);
            }

            if (phase == RoundPhase.Safehouse)
            {
                if (ctl.IsHost) DrawSafehouseSetup(ctl);
                else Center("Safehouse - waiting for the host to start the next round...", Screen.height / 2, 22, Color.white);
                if (ctl.State.SafehouseReady) Center("Doors opening - get ready!", Screen.height / 2 + 70, 20, Color.green);
            }
        }

        // Between-rounds lobby setup, shown to the host while everyone waits in the safehouse interior. Mirrors
        // DrawHostSetup: a read-only summary when configured via the Side Hustle form, a full inline editor otherwise.
        private static void DrawSafehouseSetup(GameModeController ctl)
        {
            var s = ctl.Settings;
            bool fromForm = ctl.ConfiguredByHostForm;
            float w = 380, h = fromForm ? 280 : 450;
            var box = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            GUI.Box(box, $"PropHunt - Between Rounds (next: round {ctl.State.RoundNumber + 1})");
            GUILayout.BeginArea(new Rect(box.x + 14, box.y + 28, w - 28, h - 42));
            GUILayout.Label("Players in lobby: " + ctl.State.Players.Count);
            GUILayout.Space(4);

            // safehouse / map switch (only maps big enough for the lobby are offered)
            GUILayout.Label($"Map: {ctl.SafehouseName(ctl.State.SafehouseCode)}   ({ctl.SafehouseOptionCount} fit {ctl.State.Players.Count} players)");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("< Prev")) ctl.SwitchSafehouse(-1);
            if (GUILayout.Button("Switch map >")) ctl.SwitchSafehouse(1);
            GUILayout.EndHorizontal();
            GUILayout.Space(6);

            if (fromForm)
            {
                GUILayout.Label($"Hide {s.HideSeconds}s   .   Hunt {s.HuntSeconds}s");
                GUILayout.Label($"1 hunter / {s.PlayersPerHunter} players   .   Prop HP/m {s.HitsToCatch}");
                GUILayout.Label($"Caught: {s.Caught}   .   Rounds: {s.Structure}");
            }
            else
            {
                if (GUILayout.Button("Caught: " + s.Caught + "   (toggle)"))
                    s.Caught = s.Caught == CaughtBehavior.Spectator ? CaughtBehavior.Infection : CaughtBehavior.Spectator;
                s.HideSeconds = IntRow("Hide seconds", s.HideSeconds, 5);
                s.HuntSeconds = IntRow("Hunt seconds", s.HuntSeconds, 15);
                s.PlayersPerHunter = Mathf.Max(1, IntRow("Players / hunter", s.PlayersPerHunter, 1));
                s.HitsToCatch = Mathf.Max(1, IntRow("Prop HP per metre", s.HitsToCatch, 1));
                s.MaxDecoys = Mathf.Max(0, IntRow("Decoys / round", s.MaxDecoys, 1));
                s.ConcussCharges = Mathf.Max(0, IntRow("Concussions / round", s.ConcussCharges, 1));
            }

            GUILayout.Space(6);
            if (GUILayout.Button("START NEXT ROUND", GUILayout.Height(30))) ctl.BeginNextRound();
            GUILayout.EndArea();
        }

        private static void DrawHostSetup(GameModeController ctl)
        {
            var s = ctl.Settings;
            bool fromForm = ctl.ConfiguredByHostForm;
            float w = 380, h = fromForm ? 240 : 420;
            var box = new Rect((Screen.width - w) / 2, (Screen.height - h) / 2, w, h);
            GUI.Box(box, "PropHunt - Lobby");
            GUILayout.BeginArea(new Rect(box.x + 14, box.y + 28, w - 28, h - 42));
            GUILayout.Label("Players in lobby: " + ctl.State.Players.Count);
            GUILayout.Space(4);

            if (fromForm)
            {
                // The match was configured on the Side Hustle host form - show a read-only summary, not editors.
                GUILayout.Label($"Hide {s.HideSeconds}s   ·   Hunt {s.HuntSeconds}s");
                GUILayout.Label($"1 hunter / {s.PlayersPerHunter} players   ·   Prop HP/m {s.HitsToCatch}");
                GUILayout.Label($"Caught: {s.Caught}   ·   Rounds: {s.Structure}");
                GUILayout.Label($"Decoys {s.MaxDecoys}   ·   Concussions {s.ConcussCharges}   ·   Play {(int)s.PlayAreaRadius}m");
                GUILayout.Label($"Friendly fire: {(s.FriendlyFire ? "on" : "off")}   ·   Weapon: {(string.IsNullOrEmpty(s.HunterWeapon) ? "none" : s.HunterWeapon)}");
            }
            else
            {
                // Standalone / native co-op (no host form): edit the match inline.
                if (GUILayout.Button("Caught: " + s.Caught + "   (toggle)"))
                    s.Caught = s.Caught == CaughtBehavior.Spectator ? CaughtBehavior.Infection : CaughtBehavior.Spectator;
                if (GUILayout.Button("Rounds: " + s.Structure + "   (toggle)"))
                    s.Structure = s.Structure == RoundStructure.Continuous ? RoundStructure.Single : RoundStructure.Continuous;
                s.HideSeconds = IntRow("Hide seconds", s.HideSeconds, 5);
                s.HuntSeconds = IntRow("Hunt seconds", s.HuntSeconds, 15);
                s.PlayersPerHunter = Mathf.Max(1, IntRow("Players / hunter", s.PlayersPerHunter, 1));
                s.HitsToCatch = Mathf.Max(1, IntRow("Prop HP per metre", s.HitsToCatch, 1));
                s.MaxPropChanges = Mathf.Max(0, IntRow("Max prop changes (0=inf)", s.MaxPropChanges, 1));
                s.MaxDecoys = Mathf.Max(0, IntRow("Decoys / round", s.MaxDecoys, 1));
                s.ConcussCharges = Mathf.Max(0, IntRow("Concussions / round", s.ConcussCharges, 1));
                s.PlayAreaRadius = Mathf.Max(5, IntRow("Play radius (m)", (int)s.PlayAreaRadius, 5));
            }

            GUILayout.Space(6);
            if (GUILayout.Button("START MATCH", GUILayout.Height(30))) ctl.BeginMatch();
            GUILayout.EndArea();
        }

        private static int IntRow(string label, int val, int step)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label + ": " + val);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("-", GUILayout.Width(28))) val -= step;
            if (GUILayout.Button("+", GUILayout.Width(28))) val += step;
            GUILayout.EndHorizontal();
            return val;
        }

        private static void Center(string text, float y, int size, Color color)
        {
            var style = new GUIStyle(GUI.skin.label)
            {
                alignment = TextAnchor.UpperCenter,
                fontSize = size,
                fontStyle = FontStyle.Bold
            };
            style.normal.textColor = color;
            GUI.Label(new Rect(0, y, Screen.width, size + 10), text, style);
        }
    }
}
