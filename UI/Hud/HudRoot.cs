using UnityEngine;
using UnityEngine.UI;
using S1API.UI;
using DooDesch.UI;
using PropHunt.Game;
using PropHunt.Config;

namespace PropHunt.UI.Hud
{
    /// <summary>
    /// Builds the PropHunt HUD canvas (ScreenSpaceOverlay) and all its elements ONCE, then <see cref="Apply"/> updates
    /// them from the live <see cref="GameModeController"/> each frame. Text/colors/visibility are written only when a
    /// value actually changes (uGUI dirties the mesh on assignment, so blind per-frame writes would rebuild meshes).
    ///
    /// Display-only: no GraphicRaycaster, every graphic raycastTarget=false, so the HUD never intercepts the catch
    /// click or the worldspace phone. All anchored (no layout groups), so it lays out reliably.
    /// </summary>
    internal sealed class HudRoot
    {
        private readonly GameObject _go;
        private readonly Transform _host;

        // top-center stack
        private readonly GameObject _statusPill; private readonly Text _status;
        private readonly HpBar _hp;
        private readonly HpBar _hunterHp;   // friendly-fire "HP" for hunters (same slot as the hider bar; mutually exclusive by role)
        private readonly Text _hint;
        private readonly Text _transform;
        private readonly GameObject _abilityPill; private readonly Text _ability;
        // crosshair + alerts + flashes
        private readonly GameObject _becomePill; private readonly Text _become;
        private readonly FlashLabel _flash;
        private readonly Hitmarker _hitmarker;
        private readonly GameObject _specPill; private readonly Text _spec;
        private readonly GameObject _oobPill; private readonly Text _oob;
        private readonly GameObject _safehousePill; private readonly Text _safehouse;
        private readonly GameObject _downedPill; private readonly Text _downed;
        private readonly Scoreboard _scoreboard;
        // controls (role card + [H] overlay + bottom hint) - restyled from the old IMGUI Onboarding
        private readonly GameObject _cardPanel; private readonly Text _cardTitle; private readonly Text _cardBody;
        private readonly GameObject _helpPanel; private readonly Text _helpBody;
        private readonly Text _controlsHint;

        // change-gate caches
        private string _cStatus, _cHint, _cTransform, _cAbility, _cBecome, _cSpec, _cOob, _cDowned;
        private string _cCardTitle, _cCardBody, _cHelpBody, _cControlsHint; private bool _cCardHunter;
        private bool _aStatus, _aHp, _aHunterHp, _aHint, _aTransform, _aAbility, _aBecome, _aSpec, _aOob, _aSafehouse, _aDowned;
        private bool _aCard, _aHelp, _aControlsHint;

        internal HudRoot()
        {
            _go = new GameObject("PropHunt_Hud");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            var canvas = _go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;   // above world canvases, below the menu/phone overlays (30000+)

            var root = UIFactory.Panel("hud_root", _go.transform, Theme.Clear, fullAnchor: true);
            root.GetComponent<Image>().raycastTarget = false;
            _host = root.transform;

            // --- top-center status bar ---
            _statusPill = HudWidgets.Pill(_host, "hud_status", Theme.WithAlpha(Theme.BgDeep, 0.74f));
            HudWidgets.Outline(_statusPill, Theme.HairlineStrong);
            HudWidgets.Place(_statusPill, 0.5f, 1f, 0f, -12f, 760f, 36f);
            _status = HudWidgets.Label(_statusPill.transform, "txt", Theme.H3, Theme.TextPrimary, TextAnchor.MiddleCenter, FontStyle.Bold);
            HudWidgets.Stretch(_status.rectTransform, 12f, 0f, 12f, 0f);

            // --- HP bar (depleting; remaining count-down) ---
            _hp = new HpBar(_host);
            HudWidgets.Place(_hp.Root, 0.5f, 1f, 0f, -56f, 260f, 18f);

            // --- hunter friendly-fire HP bar (same slot; a player is never both a disguised hider and a hunter) ---
            _hunterHp = new HpBar(_host);
            HudWidgets.Place(_hunterHp.Root, 0.5f, 1f, 0f, -56f, 260f, 18f);

            // --- hint line (become / stay-hidden / hunter directions) ---
            _hint = HudWidgets.Label(_host, "hud_hint", Theme.Body, Theme.InfoText, TextAnchor.MiddleCenter);
            HudWidgets.Place(_hint, 0.5f, 1f, 0f, -84f, 1100f, 24f);

            // --- transform confirmation ---
            _transform = HudWidgets.Label(_host, "hud_transform", Theme.Body, Theme.SuccessText, TextAnchor.MiddleCenter, FontStyle.Bold);
            HudWidgets.Place(_transform, 0.5f, 1f, 0f, -112f, 800f, 22f);

            // --- abilities (decoy / concussion charges) ---
            _abilityPill = HudWidgets.Pill(_host, "hud_ability", Theme.WithAlpha(Theme.BgDeep, 0.7f));
            HudWidgets.Outline(_abilityPill, Theme.Hairline);
            HudWidgets.Place(_abilityPill, 0.5f, 1f, 0f, -140f, 440f, 26f);
            _ability = HudWidgets.Label(_abilityPill.transform, "txt", Theme.Body, Theme.InfoText, TextAnchor.MiddleCenter, FontStyle.Bold);
            HudWidgets.Stretch(_ability.rectTransform, 12f, 0f, 12f, 0f);

            // --- become prompt under the crosshair ---
            _becomePill = HudWidgets.Pill(_host, "hud_become", Theme.WithAlpha(Theme.BgDeep, 0.78f));
            HudWidgets.Place(_becomePill, 0.5f, 0.5f, 0f, -52f, 380f, 30f);
            _become = HudWidgets.Label(_becomePill.transform, "txt", Theme.H3, Theme.SuccessText, TextAnchor.MiddleCenter, FontStyle.Bold);
            HudWidgets.Stretch(_become.rectTransform, 10f, 0f, 10f, 0f);

            // --- action feedback flash (centered) ---
            _flash = new FlashLabel(_host, 34);
            HudWidgets.Place(_flash.Root, 0.5f, 0.5f, 0f, 84f, 700f, 48f);

            // --- crosshair hitmarker (triggered on a confirmed hit; manages its own visibility) ---
            _hitmarker = new Hitmarker(_host);

            // --- spectator banner (bottom) ---
            _specPill = HudWidgets.Pill(_host, "hud_spec", Theme.WithAlpha(Theme.BgDeep, 0.74f));
            HudWidgets.Outline(_specPill, Theme.Hairline);
            HudWidgets.Place(_specPill, 0.5f, 0f, 0f, 74f, 540f, 34f);
            _spec = HudWidgets.Label(_specPill.transform, "txt", Theme.Body, Theme.InfoText, TextAnchor.MiddleCenter, FontStyle.Bold);
            HudWidgets.Stretch(_spec.rectTransform, 14f, 0f, 14f, 0f);

            // --- out-of-bounds warning (urgent, top) ---
            _oobPill = HudWidgets.Pill(_host, "hud_oob", Theme.WithAlpha(Theme.DangerSubtle, 0.9f));
            HudWidgets.Outline(_oobPill, Theme.Danger);
            HudWidgets.Place(_oobPill, 0.5f, 1f, 0f, -188f, 600f, 44f);
            _oob = HudWidgets.Label(_oobPill.transform, "txt", Theme.H2, Theme.DangerText, TextAnchor.MiddleCenter, FontStyle.Bold);
            HudWidgets.Stretch(_oob.rectTransform, 12f, 0f, 12f, 0f);

            // --- "doors opening" safehouse alert (center) ---
            _safehousePill = HudWidgets.Pill(_host, "hud_safehouse", Theme.WithAlpha(Theme.SuccessSubtle, 0.9f));
            HudWidgets.Outline(_safehousePill, Theme.Success);
            HudWidgets.Place(_safehousePill, 0.5f, 0.5f, 0f, -110f, 440f, 36f);
            _safehouse = HudWidgets.Label(_safehousePill.transform, "txt", Theme.H3, Theme.SuccessText, TextAnchor.MiddleCenter, FontStyle.Bold);
            HudWidgets.Stretch(_safehouse.rectTransform, 12f, 0f, 12f, 0f);

            // --- knocked-down banner (center; friendly-fire KO or concussion) ---
            _downedPill = HudWidgets.Pill(_host, "hud_downed", Theme.WithAlpha(Theme.WarningSubtle, 0.92f));
            HudWidgets.Outline(_downedPill, Theme.Warning);
            HudWidgets.Place(_downedPill, 0.5f, 0.5f, 0f, -150f, 460f, 40f);
            _downed = HudWidgets.Label(_downedPill.transform, "txt", Theme.H3, Theme.WarningText, TextAnchor.MiddleCenter, FontStyle.Bold);
            HudWidgets.Stretch(_downed.rectTransform, 12f, 0f, 12f, 0f);

            // --- round-end scoreboard card ---
            _scoreboard = new Scoreboard(_host);

            // --- role card (upper-center, on first Hiding entry) ---
            _cardPanel = HudWidgets.Pill(_host, "hud_card", Theme.WithAlpha(Theme.BgDeep, 0.92f));
            HudWidgets.Outline(_cardPanel, Theme.HairlineStrong);
            HudWidgets.Place(_cardPanel, 0.5f, 0.5f, 0f, 120f, 600f, 220f);
            _cardTitle = HudWidgets.Label(_cardPanel.transform, "title", Theme.H2, Theme.SuccessText, TextAnchor.UpperCenter, FontStyle.Bold);
            HudWidgets.Place(_cardTitle, 0.5f, 1f, 0f, -16f, 560f, 30f);
            _cardBody = HudWidgets.Label(_cardPanel.transform, "body", Theme.Body, Theme.TextPrimary, TextAnchor.UpperCenter);
            _cardBody.lineSpacing = 1.25f;
            HudWidgets.Stretch(_cardBody.rectTransform, 22f, 56f, 22f, 16f);

            // --- [H] controls overlay (centered modal-style card) ---
            _helpPanel = HudWidgets.Pill(_host, "hud_help", Theme.WithAlpha(Theme.BgDeep, 0.95f));
            HudWidgets.Outline(_helpPanel, Theme.HairlineStrong);
            HudWidgets.Place(_helpPanel, 0.5f, 0.5f, 0f, 0f, 620f, 580f);   // tall enough for HIDER + HUNTER + SPECTATOR without crowding
            var helpTitle = HudWidgets.Label(_helpPanel.transform, "title", Theme.H2, Theme.TextPrimary, TextAnchor.UpperCenter, FontStyle.Bold);
            HudWidgets.Place(helpTitle, 0.5f, 1f, 0f, -16f, 580f, 28f);
            helpTitle.text = "PropHunt - Controls";
            _helpBody = HudWidgets.Label(_helpPanel.transform, "body", Theme.Body, Theme.TextPrimary, TextAnchor.UpperLeft);
            _helpBody.lineSpacing = 1.3f;
            _helpBody.supportRichText = true;
            HudWidgets.Stretch(_helpBody.rectTransform, 28f, 56f, 28f, 16f);

            // --- bottom "[H] Controls" hint ---
            _controlsHint = HudWidgets.Label(_host, "hud_controls_hint", Theme.Caption, Theme.WithAlpha(Theme.TextMuted, 0.9f), TextAnchor.LowerCenter, FontStyle.Bold);
            HudWidgets.Place(_controlsHint, 0.5f, 0f, 0f, 22f, 360f, 20f);

            // start hidden; Apply() turns things on per phase/role
            HideAll();

            // safety: one layout pass after build
            try { Canvas.ForceUpdateCanvases(); } catch { }
        }

        // Force every toggleable element INACTIVE at build, directly (not via the change-gated SetActive, which would
        // no-op because the caches already read false while the GameObjects are created active). This makes each
        // cache consistent with the real state, so Apply()'s gated SetActive(true) fires correctly afterwards.
        private void HideAll()
        {
            Hide(_statusPill, ref _aStatus);
            Hide(_hp.Root, ref _aHp);
            Hide(_hunterHp.Root, ref _aHunterHp);
            Hide(_downedPill, ref _aDowned);
            Hide(_hint.gameObject, ref _aHint);
            Hide(_transform.gameObject, ref _aTransform);
            Hide(_abilityPill, ref _aAbility);
            Hide(_becomePill, ref _aBecome);
            Hide(_specPill, ref _aSpec);
            Hide(_oobPill, ref _aOob);
            Hide(_safehousePill, ref _aSafehouse);
            Hide(_cardPanel, ref _aCard);
            Hide(_helpPanel, ref _aHelp);
            Hide(_controlsHint.gameObject, ref _aControlsHint);
        }

        private static void Hide(GameObject go, ref bool cache) { go.SetActive(false); cache = false; }

        internal void Apply(GameModeController ctl)
        {
            var phase = ctl.Phase;
            bool inRound = phase == RoundPhase.Hiding || phase == RoundPhase.Hunting;
            bool hider = ctl.LocalRole == PlayerRole.Hider && !ctl.LocalSpectating;
            bool disguised = hider && ctl.LocalPropId >= 0;

            // --- status bar (always in session) ---
            string status = "PropHunt  -  " + phase;
            if (inRound)
            {
                status += $"      {ctl.SecondsLeft}s      Hiders left: {ctl.AliveHiderCount}      You: {ctl.LocalRole}";
                int ws = ctl.SecondsToWhistle;
                if (ws >= 0) status += $"      whistle in {ws}s";
            }
            // Between rounds show ONE monotonic countdown to the next round instead of a per-phase timer that resets
            // three times: SecondsUntilNextRound rolls the RoundEnd scoreboard + auto Safehouse pause + doors window
            // into a single number that just ticks down. It sharpens to "starting in Xs" for the final doors window,
            // says "waiting for the host..." in a manual lobby, and "round over" when there's no next round (Single).
            else if (phase == RoundPhase.RoundEnd || phase == RoundPhase.Safehouse)
            {
                int nxt = ctl.SecondsUntilNextRound;
                if (phase == RoundPhase.Safehouse && ctl.State.SafehouseReady)
                    status += nxt > 0 ? $"      starting in {nxt}s" : "      starting...";
                else if (nxt > 0)
                    status += $"      next round in {nxt}s";
                else if (phase == RoundPhase.Safehouse)
                    status += "      waiting for the host...";
                else
                    status += "      round over";
            }
            SetText(_status, ref _cStatus, status);
            SetActive(_statusPill, ref _aStatus, true);

            // --- HP bar (only when disguised in Hunting) ---
            bool showHp = disguised && phase == RoundPhase.Hunting;
            if (showHp) _hp.Set(Mathf.Max(0, ctl.LocalMaxHits - ctl.LocalHits), Mathf.Max(1, ctl.LocalMaxHits));
            SetActive(_hp.Root, ref _aHp, showHp);

            // --- hunter friendly-fire HP bar (live hunter in Hunting, FF on, not currently knocked down) ---
            bool hunter = ctl.LocalRole == PlayerRole.Hunter && !ctl.LocalSpectating;
            bool ffOn = ctl.Settings != null && ctl.Settings.FriendlyFire;
            bool showHunterHp = hunter && phase == RoundPhase.Hunting && ffOn && !ctl.LocalDowned;
            if (showHunterHp) _hunterHp.Set(Mathf.Max(0, ctl.LocalHunterMaxHits - ctl.LocalHunterHits), Mathf.Max(1, ctl.LocalHunterMaxHits));
            SetActive(_hunterHp.Root, ref _aHunterHp, showHunterHp);

            // --- knocked-down banner (friendly-fire KO / concussion) ---
            bool downed = ctl.LocalDowned;
            if (downed) SetText(_downed, ref _cDowned, $"KNOCKED DOWN - {ctl.LocalDownedSecondsLeft}s");
            SetActive(_downedPill, ref _aDowned, downed);

            // --- hint line ---
            string view = ctl.ThirdPersonOn ? $"[{KeyBinds.Name(KeyBinds.ThirdPerson)}] 1st-person" : $"[{KeyBinds.Name(KeyBinds.ThirdPerson)}] 3rd-person";
            string hint = null;
            if (hider && inRound)
            {
                string tn = ctl.LookTargetName;
                string become = tn != null ? $"[{KeyBinds.Name(KeyBinds.Become)}] become {tn}" : "Look at a highlighted object to become it";
                string lead = phase == RoundPhase.Hunting ? "Stay hidden!   " : "";
                string rot = ctl.LocalPropId >= 0 ? $"   [{KeyBinds.Name(KeyBinds.Rotate)}]+mouse rotate" : "";
                string rnd = (ctl.Settings == null || ctl.Settings.AllowRandomChange) ? $"   [{KeyBinds.Name(KeyBinds.RandomProp)}] random" : "";
                hint = $"{lead}{become}{rnd}{rot}      {view}";
            }
            else if (phase == RoundPhase.Hiding && ctl.LocalRole == PlayerRole.Hunter) hint = "Get ready... (blinded until the hunt begins)";
            else if (phase == RoundPhase.Hunting && ctl.LocalRole == PlayerRole.Hunter) hint = $"Find the props  -  [{KeyBinds.Name(KeyBinds.Catch)}] to catch (big props take more hits)";
            SetText(_hint, ref _cHint, hint ?? "");
            SetActive(_hint.gameObject, ref _aHint, hint != null);

            // --- transform confirm + abilities (disguised hiders, only DURING a round - not in the Safehouse
            //     between rounds, where a stale prop id would otherwise leave "You are now: ..." on screen) ---
            if (disguised && inRound && ctl.Settings != null)
            {
                var s = ctl.Settings;
                string pn = ctl.LocalPropName ?? "a prop";
                // HP lives on the HpBar (shown during the hunt); the banner drops it and foregrounds the change counter.
                string chg = s.MaxPropChanges > 0 ? $"{Mathf.Max(0, s.MaxPropChanges - ctl.LocalChanges)} CHANGES LEFT" : "UNLIMITED CHANGES";
                SetText(_transform, ref _cTransform, $"You are now: {pn}   -   {chg}");
                SetActive(_transform.gameObject, ref _aTransform, true);

                string decoy = s.MaxDecoys > 0 ? $"[{KeyBinds.Name(KeyBinds.Decoy)}] Decoy ({Mathf.Max(0, s.MaxDecoys - ctl.LocalDecoysUsed)})" : "";
                string conc = s.ConcussCharges > 0 ? $"[{KeyBinds.Name(KeyBinds.Concussion)}] Stun ({Mathf.Max(0, s.ConcussCharges - ctl.LocalConcussUsed)})" : "";
                string ab = (decoy + "      " + conc).Trim();
                SetText(_ability, ref _cAbility, ab);
                SetActive(_abilityPill, ref _aAbility, ab.Length > 0);
            }
            else
            {
                SetActive(_transform.gameObject, ref _aTransform, false);
                SetActive(_abilityPill, ref _aAbility, false);
            }

            // --- become prompt under crosshair ---
            string becomeTn = (hider && inRound) ? ctl.LookTargetName : null;
            if (becomeTn != null) SetText(_become, ref _cBecome, $"[{KeyBinds.Name(KeyBinds.Become)}] become {becomeTn}");
            SetActive(_becomePill, ref _aBecome, becomeTn != null);

            // --- action feedback flash ---
            if (ctl.FxActive) _flash.Trigger(ctl.FxText, ctl.FxColor, Time.unscaledTime);
            _flash.Tick(Time.unscaledTime);
            _hitmarker.Tick(Time.unscaledTime);   // fades out an active hitmarker (triggered externally on a hit)

            // --- spectator banner ---
            string spec = ctl.SpectatorHudText;
            bool showSpec = !string.IsNullOrEmpty(spec);
            if (showSpec) SetText(_spec, ref _cSpec, spec);
            SetActive(_specPill, ref _aSpec, showSpec);

            // --- out-of-bounds ---
            bool oob = ctl.LocalOutside;
            if (oob) SetText(_oob, ref _cOob, $"RETURN TO THE PLAY AREA!   {Mathf.CeilToInt(ctl.OobGrace)}s");
            SetActive(_oobPill, ref _aOob, oob);

            // --- safehouse "doors opening" ---
            SetActive(_safehousePill, ref _aSafehouse, phase == RoundPhase.Safehouse && ctl.State.SafehouseReady);
            if (_aSafehouse && _safehouse.text != "Doors opening - get ready!") _safehouse.text = "Doors opening - get ready!";

            // --- controls: role card + [H] overlay + bottom hint (restyled Onboarding) ---
            ApplyControls(ctl);

            // --- round-end scoreboard ---
            _scoreboard.Apply(ctl);
        }

        private void ApplyControls(GameModeController ctl)
        {
            var ob = ctl.Onboarding;
            if (ob == null)
            {
                SetActive(_cardPanel, ref _aCard, false);
                SetActive(_helpPanel, ref _aHelp, false);
                SetActive(_controlsHint.gameObject, ref _aControlsHint, false);
                return;
            }

            // role card (hidden while the taunt wheel is open so they don't overlap)
            bool showCard = ob.CardActive && !ctl.TauntWheelOpen && !ob.HelpOpen;
            if (showCard)
            {
                SetText(_cardTitle, ref _cCardTitle, ob.CardTitle);
                if (_cCardHunter != ob.LocalIsHunter) { _cCardHunter = ob.LocalIsHunter; _cardTitle.color = ob.LocalIsHunter ? Theme.DangerText : Theme.SuccessText; }
                string body = ob.CardGoal + "\n\n" + string.Join("\n", ob.CardKeys) + "\n\n" + ob.CardFooter;
                SetText(_cardBody, ref _cCardBody, body);
            }
            SetActive(_cardPanel, ref _aCard, showCard);

            // [H] controls overlay
            if (ob.HelpOpen)
            {
                string help =
                    "<b>HIDER</b>\n   " + string.Join("\n   ", ob.HelpHider) +
                    "\n\n<b>HUNTER</b>\n   " + string.Join("\n   ", ob.HelpHunter) +
                    "\n\n<b>SPECTATOR (when caught)</b>\n   " + string.Join("\n   ", ob.HelpSpectator) +
                    "\n\n" + ob.ControlsHintText.Replace("Controls", "close");
                SetText(_helpBody, ref _cHelpBody, help);
            }
            SetActive(_helpPanel, ref _aHelp, ob.HelpOpen);

            // bottom "[H] Controls" hint
            bool showHint = ob.ShowControlsHint;
            if (showHint) SetText(_controlsHint, ref _cControlsHint, ob.ControlsHintText);
            SetActive(_controlsHint.gameObject, ref _aControlsHint, showHint);
        }

        // ---- change-gated setters ----
        private static void SetText(Text t, ref string cache, string val)
        {
            if (cache == val) return;
            cache = val; t.text = val;
        }

        private static void SetActive(GameObject go, ref bool cache, bool val)
        {
            if (cache == val) return;
            cache = val; go.SetActive(val);
        }

        /// <summary>Flash the crosshair hitmarker (a hunter's shot connected). Called from the catch resolver.</summary>
        internal void ShowHitmarker() { try { _hitmarker.Trigger(Time.unscaledTime); } catch { } }

        internal void Destroy()
        {
            if (_go != null) UnityEngine.Object.Destroy(_go);
        }
    }
}
