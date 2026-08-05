using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Events;
using S1API.UI;
using DooDesch.UI;
using SideHustle;
using PropHunt.Game;
using PropHunt.Config;
using PropHunt.Disguise;

namespace PropHunt.Phone
{
    /// <summary>
    /// Builds the four tab bodies of the PropHunt phone app (Match / Players / Settings / Stats) into a content
    /// container. Everything reads live from <see cref="GameModeController.Active"/>; the host gets the action
    /// controls, every non-host client gets the identical layout with controls replaced by read-only labels.
    /// Host actions call existing <see cref="GameModeController"/> methods directly (no new netcode - clients are
    /// read-only and the synced state propagates back). Each tab fully rebuilds when the app's state signature
    /// changes (see <see cref="PropHuntPhoneApp"/>), so these builders just render the current snapshot.
    /// </summary>
    internal static class PhoneScreens
    {
        internal const int TabMatch = 0, TabProps = 1, TabPlayers = 2, TabSettings = 3, TabStats = 4;
        internal static readonly string[] TabLabels = { "Match", "Props", "Players", "Settings", "Stats" };

        /// <summary>Fill <paramref name="body"/> (assumed empty) with the active tab for the current state.</summary>
        internal static void Build(Transform body, GameModeController ctl, int tab, bool isHost, Transform dialogRoot)
        {
            var list = Components.ScrollList(body, out var scroll, spacing: 8f);
            SmoothScroll.Attach(scroll);   // smooth wheel glide for every phone list (driven by PropHuntPhoneApp.Tick)
            switch (tab)
            {
                case TabProps: BuildProps(list); break;
                case TabPlayers: BuildPlayers(list, ctl, isHost); break;
                case TabSettings: BuildSettings(list, ctl, isHost); break;
                case TabStats: BuildStats(list, ctl); break;
                default: BuildMatch(list, ctl, isHost, dialogRoot); break;
            }
            try { Interactions.PolishButtons(body); } catch { }
        }

        // ---------------------------------------------------------------- Match tab (the only one with controls) --
        private static void BuildMatch(Transform list, GameModeController ctl, bool isHost, Transform dialogRoot)
        {
            var phase = ctl.Phase;
            // In the Lobby the synced roster isn't populated yet, so use the LIVE lobby member count for the
            // count display + the start gate; once a match is running State.Players is the accurate roster.
            int players = phase == RoundPhase.Lobby ? ctl.LobbyMemberCount : ctl.State.Players.Count;

            switch (phase)
            {
                case RoundPhase.Lobby:
                    Section(list, "Lobby");
                    Label(list, $"Players in the lobby: {players}", Theme.Body, Theme.TextMuted);
                    if (isHost)
                    {
                        bool ready = players >= 2;
                        Button(list, ready ? "START MATCH" : "START MATCH  -  need 2+ players", Theme.Accent, ready,
                            56f, () => ctl.BeginMatch());
                        Label(list, "Set the rules in the Settings tab, then start. Everyone in the lobby joins automatically.",
                            Theme.Caption, Theme.TextMuted);
                    }
                    else Label(list, "Waiting for the host to start the match...", Theme.H3, Theme.TextPrimary,
                        FontStyle.Bold, TextAnchor.MiddleCenter);
                    break;

                case RoundPhase.Safehouse:
                    Section(list, $"Between rounds  -  next: round {ctl.State.RoundNumber + 1}");
                    Label(list, $"Map: {ctl.SafehouseName(ctl.State.SafehouseCode)}", Theme.H3, Theme.TextPrimary, FontStyle.Bold);
                    if (isHost)
                    {
                        Label(list, $"{ctl.SafehouseOptionCount} maps fit {players} players", Theme.Caption, Theme.TextMuted);
                        MapSwitchRow(list, ctl);
                        Button(list, "START NEXT ROUND", Theme.Accent, true, 56f, () => ctl.BeginNextRound());
                        bool auto = ctl.Settings.AutoStartNextRound;
                        Button(list, $"Auto-start next round: {(auto ? "ON" : "OFF")}", auto ? Theme.Success : Theme.SurfaceInput, true, 44f,
                            () => ctl.SetSetting("autostart", auto ? "0" : "1"));
                        Label(list, "Tweak the rules in the Settings tab - changes apply to this next round.",
                            Theme.Caption, Theme.TextMuted);
                    }
                    else Label(list, $"The host is setting up round {ctl.State.RoundNumber + 1}...", Theme.Body, Theme.TextMuted,
                        FontStyle.Normal, TextAnchor.MiddleCenter);
                    if (ctl.State.SafehouseReady) Label(list, "Doors opening - get ready!", Theme.H3, Theme.SuccessText, FontStyle.Bold, TextAnchor.MiddleCenter);
                    break;

                case RoundPhase.Hiding:
                case RoundPhase.Hunting:
                    Section(list, $"Round {ctl.State.RoundNumber}  -  {phase}");
                    Label(list, ctl.SecondsLeft > 0 ? $"Time left: {ctl.SecondsLeft}s" : "In progress", Theme.H3, Theme.TextPrimary, FontStyle.Bold);
                    Label(list, $"Hiders left: {ctl.AliveHiderCount}", Theme.Body, Theme.TextMuted);
                    if (isHost)
                    {
                        // Autostart belongs here as well as on the setup screen: deciding to stop after this round is
                        // something a host works out DURING it, and the setup screen has already gone by then.
                        bool autoNow = ctl.Settings.AutoStartNextRound;
                        Button(list, $"Auto-start next round: {(autoNow ? "ON" : "OFF")}",
                               autoNow ? Theme.Success : Theme.SurfaceInput, true, 44f,
                               () => ctl.SetSetting("autostart", autoNow ? "0" : "1"));
                        EndRoundButton(list, ctl, dialogRoot);
                    }
                    else Label(list, "The host runs the match. Track it here live.", Theme.Caption, Theme.TextMuted, FontStyle.Normal, TextAnchor.MiddleCenter);
                    break;

                case RoundPhase.RoundEnd:
                    WinnerHeadline(list, ctl);
                    Label(list, ctl.SecondsLeft > 0 ? $"Next round in {ctl.SecondsLeft}s..." : "Loading next round...",
                        Theme.Body, Theme.TextMuted, FontStyle.Normal, TextAnchor.MiddleCenter);
                    Label(list, "Full results are on the Stats tab.", Theme.Caption, Theme.TextMuted, FontStyle.Normal, TextAnchor.MiddleCenter);
                    break;

                case RoundPhase.MatchEnd:
                    Section(list, "Match over");
                    WinnerHeadline(list, ctl);
                    if (isHost) ReturnToHubButton(list, ctl, dialogRoot, "Return everyone to the Side Hustle hub?");
                    else Label(list, "Returning to the hub...", Theme.Body, Theme.TextMuted, FontStyle.Normal, TextAnchor.MiddleCenter);
                    break;
            }
        }

        private static void MapSwitchRow(Transform list, GameModeController ctl)
        {
            var row = RowGO(list, 40f);
            var (prevGO, prevBtn, _p) = UIFactory.ButtonWithLabel("prevmap", "< Prev map", row.transform, Theme.Button, 0, 36);
            var ple = prevGO.AddComponent<LayoutElement>(); ple.flexibleWidth = 1; ple.minHeight = 36;
            var (nextGO, nextBtn, _n) = UIFactory.ButtonWithLabel("nextmap", "Next map >", row.transform, Theme.Button, 0, 36);
            var nle = nextGO.AddComponent<LayoutElement>(); nle.flexibleWidth = 1; nle.minHeight = 36;
            prevBtn.onClick.AddListener((UnityAction)(() => { try { ctl.SwitchSafehouse(-1); } catch { } }));
            nextBtn.onClick.AddListener((UnityAction)(() => { try { ctl.SwitchSafehouse(1); } catch { } }));
        }

        /// <summary>Host, mid-round: stop this round now. The one thing a host needs here that they cannot do from the
        /// pause menu - leaving to the hub they can, which is why that button is not on this screen any more.</summary>
        private static void EndRoundButton(Transform list, GameModeController ctl, Transform dialogRoot)
        {
            Button(list, "End round", Theme.Danger, true, 48f, () =>
                Components.ConfirmDialog(dialogRoot, "End round",
                    "End this round now? It is scored as a hunter win, and you go back to the round setup where you can pick a different map.",
                    "End round", () => ctl.RequestEndRound()));
        }

        private static void ReturnToHubButton(Transform list, GameModeController ctl, Transform dialogRoot, string message)
        {
            Button(list, "Return to hub", Theme.Danger, true, 48f, () =>
                Components.ConfirmDialog(dialogRoot, "Return to hub", message, "Return to hub", () => ctl.RequestReturnToHub()));
        }

        private static void WinnerHeadline(Transform list, GameModeController ctl)
        {
            int w = ctl.State.Winner;
            string head = w == 0 ? "HUNTERS WIN" : w == 1 ? "HIDERS WIN" : "ROUND OVER";
            Color c = w == 0 ? Theme.DangerText : w == 1 ? Theme.SuccessText : Theme.TextPrimary;
            Label(list, head, Theme.H2, c, FontStyle.Bold, TextAnchor.MiddleCenter);
        }

        // ---------------------------------------------------------------- Players tab (live roster; host can kick) -----
        private static void BuildPlayers(Transform list, GameModeController ctl, bool isHost)
        {
            try { PlayerRegistry.Refresh(); } catch { }
            var st = ctl.State;
            var phase = ctl.Phase;
            ulong me = ctl.LocalId;

            var players = new List<PlayerState>(st.Players.Values);
            players.Sort((a, b) => RoleRank(a).CompareTo(RoleRank(b)));

            Section(list, $"Players ({players.Count})");
            if (players.Count == 0) { Label(list, "No players yet.", Theme.Body, Theme.TextMuted); return; }

            foreach (var p in players)
            {
                var row = RowGO(list, 30f);
                bool isMe = p.SteamId == me;
                string name = NameOf(p) + (isMe ? "  (you)" : "");
                // Name column: flexible but CONTENT-INDEPENDENT width (preferredWidth 0) - so a longer name doesn't
                // grow the column and shove the role chip sideways. With a fixed chip + fixed detail column, the chip
                // now sits at an identical x on every row. The name wraps/clips instead of pushing the chip.
                var nameT = Col(row.transform, name, isMe ? Theme.AccentBorder : Theme.TextPrimary, 0, 1f, TextAnchor.MiddleLeft,
                    isMe ? FontStyle.Bold : FontStyle.Normal);
                nameT.horizontalOverflow = HorizontalWrapMode.Wrap; nameT.verticalOverflow = VerticalWrapMode.Truncate;
                var nle = nameT.gameObject.GetComponent<LayoutElement>(); if (nle != null) { nle.minWidth = 0; nle.preferredWidth = 0; }

                RoleChip(p, out string chip, out Color fill, out Color txt);
                ChipCol(row.transform, chip, fill, txt, 78f);

                Col(row.transform, DetailFor(ctl, p, phase, isMe), Theme.TextMuted, isHost && !isMe ? 96f : 140f, 0f, TextAnchor.MiddleRight, FontStyle.Normal, Theme.Caption);

                // The host may remove a player from the session (host-authoritative; the framework runs the actual kick).
                if (isHost && !isMe && p.SteamId != 0)
                {
                    ulong target = p.SteamId;
                    KickCol(row.transform, () => ctl.KickPlayer(target));
                }
            }

            // The roster never reveals a living hider's prop or position to others (that would let hunters cheat
            // through the phone); only your own disguise is shown to you. Full per-player details appear at round end.
        }

        // What to show in a player row's right-hand detail column without leaking a living hider's disguise.
        private static string DetailFor(GameModeController ctl, PlayerState p, RoundPhase phase, bool isMe)
        {
            bool inRound = phase == RoundPhase.Hiding || phase == RoundPhase.Hunting;
            if (p.Role == PlayerRole.Hunter) return $"{p.CatchesMade} catches";
            if (p.Role == PlayerRole.Hider)
            {
                if (p.Eliminated) return "caught";
                if (isMe && inRound)
                {
                    string prop = ctl.LocalPropName;
                    string hp = $"{Mathf.Max(0, p.MaxHits - p.Hits)}/{p.MaxHits} HP";
                    return string.IsNullOrEmpty(prop) ? hp : $"{prop}  -  {hp}";
                }
                return inRound ? "hidden" : "hider";
            }
            return p.Eliminated ? "spectating" : "waiting";
        }

        // ---------------------------------------------------------------- Settings tab (host edits between rounds) -
        private static void BuildSettings(Transform list, GameModeController ctl, bool isHost)
        {
            // The host can tune settings in ANY phase (including mid-hunt). It is safe: the app excludes the host's own
            // settings from its refresh fingerprint so a live edit never resets the control, and most keys are consumed
            // at round boundaries (a few - tag range / taunt / concuss - apply live), so "next round" is the honest note.
            bool editable = isHost;

            if (!isHost) Label(list, "VIEW ONLY  -  the host runs the match.", Theme.Body, Theme.WarningText, FontStyle.Bold);
            else Label(list, "Changes apply to the next round.", Theme.Caption, Theme.TextMuted);

            var cur = ctl.Settings.ToValues();
            string lastCat = null;
            foreach (var s in PropHuntSettingsSpec.Build())
            {
                if (!string.IsNullOrEmpty(s.Category) && s.Category != lastCat) { Section(list, s.Category); lastCat = s.Category; }
                string val = cur.TryGetValue(s.Key, out var cv) ? cv : s.Default;
                if (editable) BuildEditableRow(list, ctl, s, val);
                else BuildReadonlyRow(list, s, val);
            }
        }

        private static void BuildEditableRow(Transform list, GameModeController ctl, SettingDescriptor s, string val)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            switch (s.Type)
            {
                case SettingType.Slider:
                {
                    Components.FormRow(list, s.Label, s.Hint, out var slot);
                    float v0 = ParseFloat(val, s.Min);
                    float step = s.Step > 0f ? s.Step : (s.WholeNumbers ? 1f : 0f);
                    var lbl = UIFactory.Text("val", Fmt(v0, s.WholeNumbers, s.Unit), slot, Theme.Label, TextAnchor.MiddleRight);
                    lbl.color = Theme.TextPrimary; lbl.raycastTarget = false;
                    var vrt = lbl.rectTransform; vrt.anchorMin = new Vector2(1, 0); vrt.anchorMax = new Vector2(1, 1); vrt.pivot = new Vector2(1, 0.5f); vrt.sizeDelta = new Vector2(54, 0); vrt.anchoredPosition = new Vector2(-2, 0);
                    var slider = Components.Slider(slot, s.Min, s.Max, v0, v =>
                    {
                        lbl.text = Fmt(v, s.WholeNumbers, s.Unit);
                        ctl.SetSetting(s.Key, s.WholeNumbers ? Mathf.RoundToInt(v).ToString(ci) : v.ToString("0.##", ci));
                    }, step);
                    var srt = slider.GetComponent<RectTransform>();
                    srt.anchorMin = new Vector2(0, 0.5f); srt.anchorMax = new Vector2(1, 0.5f); srt.pivot = new Vector2(0.5f, 0.5f);
                    srt.offsetMin = new Vector2(0, -4); srt.offsetMax = new Vector2(-58, 4);
                    break;
                }
                case SettingType.Toggle:
                {
                    Components.FormRow(list, s.Label, s.Hint, out var slot);
                    bool on = val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
                    var tg = Components.Toggle(slot, on, v => ctl.SetSetting(s.Key, v ? "1" : "0"));
                    var trt = tg.GetComponent<RectTransform>(); trt.anchorMin = new Vector2(1, 0.5f); trt.anchorMax = new Vector2(1, 0.5f); trt.pivot = new Vector2(1, 0.5f); trt.anchoredPosition = new Vector2(-2, 0);
                    break;
                }
                case SettingType.Segmented:
                {
                    string[] opts = s.Options ?? new[] { "Off", "On" };
                    string[] vals = s.Values ?? opts;
                    int active = Math.Max(0, Array.IndexOf(vals, val));
                    Components.FormRow(list, s.Label, s.Hint, out var slot, stacked: opts.Length > 2);
                    var seg = Components.Segmented(slot, opts, active, i => ctl.SetSetting(s.Key, i >= 0 && i < vals.Length ? vals[i] : opts[i]), out _);
                    Fill(seg);
                    break;
                }
                case SettingType.Dropdown:
                {
                    string[] opts = s.Options ?? new[] { "Off", "On" };
                    string[] vals = s.Values ?? opts;
                    int idx = Math.Max(0, Array.IndexOf(vals, val));
                    Components.FormRow(list, s.Label, s.Hint, out var slot);
                    var holder = new GameObject("dropdown"); holder.transform.SetParent(slot, false); holder.AddComponent<RectTransform>();
                    Fill(holder);
                    var hl = holder.AddComponent<HorizontalLayoutGroup>();
                    hl.spacing = 6; hl.childControlWidth = true; hl.childControlHeight = true; hl.childForceExpandWidth = false; hl.childForceExpandHeight = true; hl.childAlignment = TextAnchor.MiddleRight;
                    var (prevGO, prevBtn, _dp) = UIFactory.ButtonWithLabel("prev", "<", holder.transform, Theme.Button, 36, 30);
                    var ple = prevGO.AddComponent<LayoutElement>(); ple.minWidth = 36; ple.preferredWidth = 36; ple.flexibleWidth = 0;
                    var dl = UIFactory.Text("val", opts[idx], holder.transform, Theme.Label, TextAnchor.MiddleCenter, FontStyle.Bold);
                    dl.color = Theme.TextPrimary; dl.raycastTarget = false;
                    var lle = dl.gameObject.AddComponent<LayoutElement>(); lle.minWidth = 100; lle.preferredWidth = 150; lle.flexibleWidth = 0;
                    var (nextGO, nextBtn, _dn) = UIFactory.ButtonWithLabel("next", ">", holder.transform, Theme.Button, 36, 30);
                    var nle = nextGO.AddComponent<LayoutElement>(); nle.minWidth = 36; nle.preferredWidth = 36; nle.flexibleWidth = 0;
                    Action<int> set = i =>
                    {
                        idx = ((i % opts.Length) + opts.Length) % opts.Length;
                        dl.text = opts[idx];
                        ctl.SetSetting(s.Key, idx < vals.Length ? vals[idx] : opts[idx]);
                    };
                    prevBtn.onClick.AddListener((UnityAction)(() => set(idx - 1)));
                    nextBtn.onClick.AddListener((UnityAction)(() => set(idx + 1)));
                    break;
                }
                case SettingType.Text:
                {
                    Components.FormRow(list, s.Label, s.Hint, out var slot, stacked: true);
                    var input = Components.TextInput(slot, val ?? "", t => ctl.SetSetting(s.Key, t), null, 64);
                    Fill(input.gameObject, 6f);
                    break;
                }
            }
        }

        private static void BuildReadonlyRow(Transform list, SettingDescriptor s, string val)
        {
            Components.FormRow(list, s.Label, s.Hint, out var slot);
            var lbl = UIFactory.Text("val", DisplayValue(s, val), slot, Theme.Label, TextAnchor.MiddleRight, FontStyle.Bold);
            lbl.color = Theme.TextPrimary; lbl.raycastTarget = false;
            var rt = lbl.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = new Vector2(0, 0); rt.offsetMax = new Vector2(-4, 0);
        }

        // Human-readable form of a stored value for the read-only rows.
        private static string DisplayValue(SettingDescriptor s, string val)
        {
            switch (s.Type)
            {
                case SettingType.Toggle:
                    return (val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase)) ? "On" : "Off";
                case SettingType.Slider:
                    return Fmt(ParseFloat(val, s.Min), s.WholeNumbers, s.Unit);
                case SettingType.Segmented:
                case SettingType.Dropdown:
                {
                    var vals = s.Values ?? s.Options;
                    var opts = s.Options ?? s.Values;
                    if (vals != null && opts != null) { int i = Array.IndexOf(vals, val); if (i >= 0 && i < opts.Length) return opts[i]; }
                    return string.IsNullOrEmpty(val) ? "None" : val;
                }
                default: return string.IsNullOrEmpty(val) ? "-" : val;
            }
        }

        // ---------------------------------------------------------------- Stats tab (live session leaderboard) ----
        private static void BuildStats(Transform list, GameModeController ctl)
        {
            try { PlayerRegistry.Refresh(); } catch { }
            var players = new List<PlayerState>(ctl.State.Players.Values);
            players.Sort((a, b) => b.SessScore.CompareTo(a.SessScore));

            var awards = BuildAwards(players);
            if (awards.Count > 0)
            {
                Section(list, "Round awards");
                foreach (var a in awards) Label(list, a, Theme.Body, Theme.WarningText, FontStyle.Bold);
            }

            Section(list, "Leaderboard");
            if (players.Count == 0) { Label(list, "No players yet.", Theme.Body, Theme.TextMuted); return; }

            var head = RowGO(list, 24f);
            Col(head.transform, "Player", Theme.TextMuted, 0, 1f, TextAnchor.MiddleLeft, FontStyle.Bold, Theme.Caption);
            Col(head.transform, "Catches", Theme.TextMuted, 64f, 0f, TextAnchor.MiddleCenter, FontStyle.Bold, Theme.Caption);
            Col(head.transform, "Survived", Theme.TextMuted, 70f, 0f, TextAnchor.MiddleCenter, FontStyle.Bold, Theme.Caption);
            Col(head.transform, "Score", Theme.TextMuted, 56f, 0f, TextAnchor.MiddleRight, FontStyle.Bold, Theme.Caption);

            int rank = 1;
            foreach (var p in players)
            {
                var row = RowGO(list, 28f);
                Col(row.transform, $"{rank}. {NameOf(p)}", Theme.TextPrimary, 0, 1f, TextAnchor.MiddleLeft);
                Col(row.transform, p.CatchesMade.ToString(), Theme.TextMuted, 64f, 0f, TextAnchor.MiddleCenter);
                Col(row.transform, p.SurvivedSeconds + "s", Theme.TextMuted, 70f, 0f, TextAnchor.MiddleCenter);
                Col(row.transform, p.SessScore.ToString(), Theme.AccentBorder, 56f, 0f, TextAnchor.MiddleRight, FontStyle.Bold);
                rank++;
            }
        }

        // ---------------------------------------------------------------- Props tab ------------------------------
        /// <summary>
        /// Every prop anyone can turn into, so players can plan in the lobby instead of discovering the pool by
        /// aiming at things mid-round.
        ///
        /// Two things shape how this is rendered. Prop names are NOT unique - the catalog keys entries by a content
        /// signature and several distinct meshes legitimately share a display name - so identical names are grouped
        /// and counted rather than listed five times in a row. And the pool is intersected with the HOST's, because
        /// a client that scanned a mesh the host never published cannot actually become it.
        /// </summary>
        /// <summary>
        /// The dressing room: try props on before the match starts.
        ///
        /// This tab used to be a LIST of prop names, which answered a question nobody has. Knowing that the pool
        /// contains four crates tells a player nothing about hiding as one - how short you get, what you can see over,
        /// whether the thing reads as scenery. So the tab hands them the actual prop instead.
        ///
        /// Only in the lobby. Once a round is running, prop changes are the round's own rules ([2], with a budget), and
        /// a second way in from the phone would quietly bypass them.
        /// </summary>
        private static void BuildProps(Transform list)
        {
            var ctl = GameModeController.Active;
            if (ctl != null && ctl.State != null && ctl.State.Phase == RoundPhase.Lobby)
            {
                BuildPropTryout(list);
                return;
            }

            Section(list, "Props");
            Label(list, "Try props on in the lobby, before the match starts. During a round, [2] changes your prop.",
                  Theme.Body, Theme.TextMuted);
#if DEBUG
            BuildPropCatalogList(list);
#endif
        }

        private static void BuildPropTryout(Transform list)
        {
            int becomable = 0;
            try { becomable = PropCatalog.BecomableCount(); } catch { }

            Section(list, "Try a prop");
            if (becomable == 0)
            {
                Label(list, "No props loaded here yet. Walk around a little and open this again - the game streams the " +
                            "world in as you move.", Theme.Body, Theme.TextMuted);
                return;
            }

            bool wearing = Disguise.PropPreview.Active;
            string name = wearing ? PrettyPropNameById(Disguise.PropPreview.PropId) : null;

            Label(list, wearing ? $"You are a {name}." : $"{becomable} props to try on right here.",
                  Theme.H3, wearing ? Theme.SuccessText : Theme.TextPrimary, FontStyle.Bold, TextAnchor.MiddleCenter);

            Button(list, wearing ? "Try another one" : "Become a random prop", Theme.Success, true, 48f, () =>
            {
                if (!Disguise.PropPreview.Roll())
                    Core.Log.Msg("[PropHunt] no becomable prop available to try on yet.");
            });

            if (wearing)
                Button(list, "Back to normal", Theme.Button, true, 40f, () => Disguise.PropPreview.Clear());

            Label(list, "[2] for another prop, hold [F] and move the mouse to turn it. Everyone sees you, and it ends " +
                        "when the match starts. Decoys, concussions and taunts are round-only.",
                  Theme.Caption, Theme.TextMuted);
        }

        /// <summary>A prop's tidied name from its catalog id.</summary>
        private static string PrettyPropNameById(int id)
        {
            try
            {
                var e = PropCatalog.ById(id);
                return e != null ? PrettyPropName(e) : "prop";
            }
            catch { return "prop"; }
        }

#if DEBUG
        private static void BuildPropCatalogList(Transform list)
        {
            List<PropEntry> entries;
            try { entries = PropCatalog.Entries(); }
            catch (Exception e)
            {
                Section(list, "Props");
                Label(list, "Could not read the prop catalog: " + e.Message, Theme.Body, Theme.TextMuted);
                return;
            }

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            int total = 0;
            foreach (var e in entries)
            {
                if (e == null || !PropCatalog.IsBecomable(e.Id)) continue;
                string name = PrettyPropName(e);
                counts.TryGetValue(name, out int n);
                counts[name] = n + 1;
                total++;
            }

            Section(list, $"Props you can become ({total})");
            if (total == 0)
            {
                Label(list, "The catalog is still building. It finishes when the first round starts - open this again then.",
                    Theme.Body, Theme.TextMuted);
                return;
            }

            Label(list, "More appear once a round starts: interiors stream in with the map, so the lobby only knows what is loaded now.",
                Theme.Caption, Theme.TextMuted);

            var names = new List<string>(counts.Keys);
            names.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                var row = RowGO(list, 26f);
                Col(row.transform, n, Theme.TextPrimary, 0, 1f, TextAnchor.MiddleLeft);
                int c = counts[n];
                Col(row.transform, c > 1 ? "x" + c : "", Theme.TextMuted, 44f, 0f, TextAnchor.MiddleRight);
            }
        }
#endif

        /// <summary>A catalog entry's display name, cleaned up for a player-facing list. Mesh names carry authoring
        /// noise (SM_ prefixes, LOD suffixes, underscores) and the composite entries are keyed by a namespaced key
        /// rather than a mesh, so those are read from the key instead.</summary>
        private static string PrettyPropName(PropEntry e)
        {
            string raw = e.Name ?? "";
            string key = e.Key ?? "";
            int colon = key.IndexOf(':');
            if (colon > 0 && colon < key.Length - 1) raw = key.Substring(colon + 1);   // "reg:GoldenToilet" -> "GoldenToilet"

            if (raw.StartsWith("SM_", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(3);
            int lod = raw.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (lod > 0) raw = raw.Substring(0, lod);
            raw = raw.Replace('_', ' ').Trim();
            if (raw.Length == 0) return "Prop";

            // "GoldenToilet" -> "Golden Toilet": the registry names are PascalCase and unreadable as one word.
            var sb = new System.Text.StringBuilder(raw.Length + 4);
            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];
                if (i > 0 && char.IsUpper(c) && !char.IsUpper(raw[i - 1]) && raw[i - 1] != ' ') sb.Append(' ');
                sb.Append(c);
            }
            raw = sb.ToString();
            return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
        }

        private static List<string> BuildAwards(List<PlayerState> players)
        {
            var list = new List<string>();
            AddAward(list, players, "Top Hunter", p => p.CatchesMade, (p, v) => $"{NameOf(p)} - {v} catches");
            AddAward(list, players, "Survivor", p => p.Role == PlayerRole.Hider ? p.SurvivedSeconds : 0, (p, v) => $"{NameOf(p)} - {v}s alive");
            AddAward(list, players, "Trickster", p => p.DecoyBaits, (p, v) => $"{NameOf(p)} - {v} decoy baits");
            AddAward(list, players, "Shocker", p => p.StunsLanded, (p, v) => $"{NameOf(p)} - {v} stuns");
            return list;
        }

        private static void AddAward(List<string> list, List<PlayerState> players, string title, Func<PlayerState, int> sel, Func<PlayerState, int, string> fmt)
        {
            PlayerState best = null; int bv = 0;
            foreach (var p in players) { int v = sel(p); if (v > bv) { bv = v; best = p; } }
            if (best != null && bv > 0) list.Add($"{title}: {fmt(best, bv)}");
        }

        // ---------------------------------------------------------------- shared row/label helpers ----------------
        private static void Section(Transform list, string title) => Components.SectionHeader(list, title);

        private static Text Label(Transform list, string text, int size, Color color, FontStyle style = FontStyle.Normal, TextAnchor anchor = TextAnchor.UpperLeft)
        {
            var t = UIFactory.Text("label", text, list, size, anchor, style);
            t.color = color; t.raycastTarget = false;
            return t;
        }

        private static Button Button(Transform list, string label, Color bg, bool enabled, float height, System.Action onClick)
        {
            var (go, btn, txt) = UIFactory.ButtonWithLabel("btn", label, list, enabled ? bg : Theme.Button, 0, height);
            var le = go.AddComponent<LayoutElement>(); le.minHeight = height; le.preferredHeight = height; le.flexibleWidth = 1;
            var img = go.GetComponent<Image>(); if (img != null) { img.sprite = Theme.RoundedSprite(); img.type = Image.Type.Sliced; }
            if (txt != null) { txt.color = enabled ? Theme.TextPrimary : Theme.TextDisabled; txt.fontSize = Theme.H3; }
            btn.interactable = enabled;
            if (enabled && onClick != null) btn.onClick.AddListener((UnityAction)(() => { try { onClick(); } catch { } }));
            return btn;
        }

        private static GameObject RowGO(Transform list, float height)
        {
            var go = new GameObject("row"); go.transform.SetParent(list, false); go.AddComponent<RectTransform>();
            var le = go.AddComponent<LayoutElement>(); le.minHeight = height; le.preferredHeight = height; le.flexibleWidth = 1;
            var h = go.AddComponent<HorizontalLayoutGroup>();
            h.spacing = 8; h.childControlWidth = true; h.childControlHeight = true; h.childForceExpandWidth = false; h.childForceExpandHeight = true; h.childAlignment = TextAnchor.MiddleLeft;
            return go;
        }

        private static Text Col(Transform row, string text, Color color, float width, float flex, TextAnchor anchor, FontStyle style = FontStyle.Normal, int size = Theme.Body)
        {
            var t = UIFactory.Text("c", text, row, size, anchor, style);
            t.color = color; t.raycastTarget = false;
            t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Truncate;
            var le = t.gameObject.AddComponent<LayoutElement>();
            if (width > 0f) { le.minWidth = width; le.preferredWidth = width; }
            le.flexibleWidth = flex;
            return t;
        }

        private static void ChipCol(Transform row, string text, Color fill, Color textColor, float width)
        {
            var p = UIFactory.Panel("chip", row, fill);
            var img = p.GetComponent<Image>(); if (img != null) { img.sprite = Theme.RoundedSprite(); img.type = Image.Type.Sliced; img.raycastTarget = false; }
            var le = p.AddComponent<LayoutElement>(); le.minWidth = width; le.preferredWidth = width; le.flexibleWidth = 0;
            var t = UIFactory.Text("t", text, p.transform, Theme.Caption, TextAnchor.MiddleCenter, FontStyle.Bold);
            t.color = textColor; t.raycastTarget = false;
            var rt = t.rectTransform; rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        }

        // A compact fixed-width "Kick" button that sits inside a player row (host-only).
        private static void KickCol(Transform row, System.Action onClick)
        {
            var (go, btn, txt) = UIFactory.ButtonWithLabel("kick", "Kick", row, Theme.DangerSubtle, 0, 26f);
            var le = go.AddComponent<LayoutElement>();
            le.minWidth = 52f; le.preferredWidth = 52f; le.flexibleWidth = 0; le.minHeight = 26f; le.preferredHeight = 26f;
            var img = go.GetComponent<Image>(); if (img != null) { img.sprite = Theme.RoundedSprite(); img.type = Image.Type.Sliced; }
            if (txt != null) { txt.color = Theme.DangerText; txt.fontSize = Theme.Caption; }
            if (onClick != null) btn.onClick.AddListener((UnityAction)(() => { try { onClick(); } catch { } }));
        }

        private static void RoleChip(PlayerState p, out string text, out Color fill, out Color txt)
        {
            if (p.Role == PlayerRole.Hunter) { text = "HUNTER"; fill = Theme.DangerSubtle; txt = Theme.DangerText; }
            else if (p.Role == PlayerRole.Hider)
            {
                if (p.Eliminated) { text = "CAUGHT"; fill = Theme.WarningSubtle; txt = Theme.WarningText; }
                else { text = "HIDER"; fill = Theme.SuccessSubtle; txt = Theme.SuccessText; }
            }
            else { text = "SPEC"; fill = Theme.Button; txt = Theme.TextMuted; }
        }

        // Sort order for the roster: hunters first, then living hiders, then caught/spectators.
        private static int RoleRank(PlayerState p)
        {
            if (p.Role == PlayerRole.Hunter) return 0;
            if (p.Role == PlayerRole.Hider) return p.Eliminated ? 2 : 1;
            return 3;
        }

        private static string NameOf(PlayerState p)
        {
            try { var gp = PlayerRegistry.Get(p.SteamId); if (gp != null) { var n = gp.PlayerName; if (!string.IsNullOrEmpty(n)) return n; } }
            catch { }
            return "Player " + (p.SteamId % 10000);
        }

        private static void Fill(GameObject go, float vInset = 0f)
        {
            var rt = go.GetComponent<RectTransform>(); if (rt == null) return;
            rt.anchorMin = new Vector2(0, 0); rt.anchorMax = new Vector2(1, 1);
            rt.offsetMin = new Vector2(0, vInset); rt.offsetMax = new Vector2(0, -vInset);
        }

        private static string Fmt(float v, bool whole, string unit)
        {
            string n = whole ? Mathf.RoundToInt(v).ToString() : v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(unit) ? n : n + " " + unit;
        }

        private static float ParseFloat(string s, float fallback) =>
            float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
