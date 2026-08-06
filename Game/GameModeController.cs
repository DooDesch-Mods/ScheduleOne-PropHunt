using System;
using System.Collections.Generic;
using PropHunt.Catch;
using PropHunt.Config;
using PropHunt.Disguise;
using PropHunt.Net;
using PropHunt.Patches;
using PropHunt.PlayArea;
using PropHunt.Taunt;
using SteamNetworkLib.Models;
using SteamNetworkLib.Sync;
#if IL2CPP
using Il2CppSteamworks;
#else
using Steamworks;
#endif

namespace PropHunt.Game
{
    /// <summary>
    /// One PropHunt session (created in the Side Hustle launch callback, disposed on teardown). The HOST owns
    /// the authoritative <see cref="GameState"/> and drives the round flow via the pure <see cref="RoundLogic"/>,
    /// then publishes the snapshot through a single HostSyncVar. CLIENTS apply the synced state and render.
    /// This class is the engine adapter: networking, the SteamId&lt;-&gt;Player map + catch geometry, local
    /// freeze/blind effects, and the disguise/catch/play-area/taunt sub-controllers. All durable decisions
    /// live in RoundLogic; here they are wired to I/O.
    /// </summary>
    internal sealed class GameModeController
    {
        internal static GameModeController Active { get; private set; }
        private static bool _handlersRegistered;

        private readonly SideHustle.LaunchContext _ctx;
        private readonly bool _isHost;
        private RoundSettings _settings = new RoundSettings();
        private bool _settingsDirty;        // host edited a setting via the phone -> re-publish to clients (throttled)
        private string _armedWeaponId;      // the hunter weapon id we actually granted locally (to strip the OLD one if the host swaps it mid-match)
        private float _lastSettingsPush;
        private GameState _state = new GameState();
        private HostSyncVar<string> _stateVar;
        private DisguiseController _disguise;
        private DecoyController _decoy;
        private PropPicker _picker;
        private PropHighlighter _highlighter;
        private PropHunt.View.ThirdPersonController _thirdPerson;
        private CatchController _catch;
        private PropPassthrough _passthrough;
        private PlayAreaController _playArea;
        private PlayAreaBorder _border;
        private TauntController _taunt;
        private PropRotationController _propRotation;
        private PlayArea.MapAreaRing _mapRing;
        private Taunt.TauntWheel _tauntWheel;
        private UI.Onboarding _onboarding;
        private PropHunt.View.SpectatorController _spectator;
        private float _lastTauntTime;
        private RoundPhase _loggedPhase = (RoundPhase)(-1);
        private bool _matchStarted;
        private bool _returnRequested;
        private bool _disposed;

        // local-effect change tracking (apply only on change so we don't fight the game)
        private bool _appliedFrozen;
        private bool _appliedBlind;
        private bool _appliedHotbar = true;
        private int _lastEffectKey = int.MinValue;
        private int _lastLocalProp = int.MinValue;
        private float _localYaw;   // optimistic local prop facing while [F]+mouse rotating (synced to others via the host)

        internal GameModeController(SideHustle.LaunchContext ctx, bool isHost)
        {
            _ctx = ctx;
            _isHost = isHost;
        }

        // ---- public surface (Core / sub-controllers / HUD) ----
        internal bool IsHost => _isHost;
        internal RoundSettings Settings => _settings;
        internal bool ConfiguredByHostForm => !string.IsNullOrEmpty(_ctx?.Multiplayer?.ConfigBlob);   // launched via the Side Hustle host form
        internal GameState State => _state;
        internal RoundPhase Phase => _state.Phase;
        internal ulong LocalId => PropHuntNet.LocalSteamId;
        internal int AliveHiderCount => RoundLogic.AliveHiders(_state);
        /// <summary>Live Steam-lobby member count (host + everyone joined). Use this in the Lobby - the synced
        /// <see cref="GameState"/> roster only fills once the match starts, so before that it would read just the host.</summary>
        internal int LobbyMemberCount { get { int n = GetMemberIds().Count; return n > 0 ? n : _state.Players.Count; } }
        internal bool LocalOutside => _playArea != null && _playArea.LocalOutside;
        /// <summary>True when <see cref="LocalOutside"/> is about deep water rather than the area edge.</summary>
        internal bool LocalInWater => _playArea != null && _playArea.LocalWater;
        internal float OobGrace => _playArea != null ? _playArea.GraceLeft : 0f;
        internal float LastTauntTime => _lastTauntTime;
        /// <summary>When the Hunting phase began, in host time. The zero mark of the whistle grid, and the single
        /// source both the countdown below and <see cref="Taunt.TauntController"/> derive their marks from - if they
        /// each computed it their own way they would drift apart, which is exactly the bug this replaced.</summary>
        internal long HuntStartUnix => RoundLogic.HuntStartUnix(_state, _settings, NowUnix());

        /// <summary>Seconds until the next global whistle, or -1 if none is pending (not Hunting / taunts off / no
        /// further whistle before the hunt ends). Computed from the SYNCED phase timer + interval in HOST time, so the
        /// host AND every client show the same countdown - hiders need to know when the next forced reveal is coming.
        /// Mark 0 is the start of the hunt itself, so the phase opens with a whistle.</summary>
        internal int SecondsToWhistle => RoundLogic.SecondsToWhistle(_state, _settings, NowUnix());

        /// <summary>Seconds until the host reshuffles props, or -1 when none is due. Derived from the synced hunt start
        /// and interval, so every client counts the same instant down without a field for it.</summary>
        internal int SecondsToPropRotation => RoundLogic.SecondsToPropRotation(_state, _settings, NowUnix());
        internal string LookTargetName => _picker != null ? _picker.CurrentTargetName : null;
        internal int LookTargetId => _picker != null ? _picker.CurrentTargetId : -1;
        /// <summary>True when the local player (EITHER role) is aiming at a becomable world prop. Vanilla world
        /// interaction is suppressed in that case so a prop can never be picked up during a round (and, for a
        /// hider, [E] becomes it). Aiming at a door / non-prop leaves this false, so doors still open for both.</summary>
        internal bool LocalAimingBecomable => _picker != null && _picker.CurrentTargetId >= 0;
        internal bool ThirdPersonOn => _thirdPerson != null && _thirdPerson.IsOn;
        internal bool LocalSpectating => _spectator != null && _spectator.Active;
        internal string SpectatorHudText => _spectator != null ? _spectator.HudText : null;
        /// <summary>The onboarding state (role card + [H] controls overlay) - read by the uGUI HUD, which renders it.</summary>
        internal UI.Onboarding Onboarding => _onboarding;
        /// <summary>True while the radial taunt wheel is open - the HUD suppresses the role card so they don't overlap.</summary>
        internal bool TauntWheelOpen => _tauntWheel != null && _tauntWheel.MenuOpen;
        internal bool RoundActive => _state.Phase == RoundPhase.Hiding || _state.Phase == RoundPhase.Hunting || _state.Phase == RoundPhase.RoundEnd || _state.Phase == RoundPhase.Safehouse;

        internal PlayerRole LocalRole => RoleOf(LocalId);

        /// <summary>The synced role of any player id (Unassigned if unknown). Used by gameplay patches.</summary>
        internal PlayerRole RoleOf(ulong id) =>
            (id != 0 && _state.Players.TryGetValue(id, out var p)) ? p.Role : PlayerRole.Unassigned;

        /// <summary>Hunter hits taken so far by the local hider this round (0 if not a live hider).</summary>
        internal int LocalHits
        {
            get { var id = LocalId; return (id != 0 && _state.Players.TryGetValue(id, out var p)) ? p.Hits : 0; }
        }
        /// <summary>Size-based HP of the local hider's current prop (hits needed to catch them).</summary>
        internal int LocalMaxHits
        {
            get { var id = LocalId; return (id != 0 && _state.Players.TryGetValue(id, out var p)) ? p.MaxHits : 1; }
        }

        /// <summary>Friendly-fire hits the local HUNTER has taken this "life" (knocked down at LocalHunterMaxHits).</summary>
        internal int LocalHunterHits
        {
            get { var id = LocalId; return (id != 0 && _state.Players.TryGetValue(id, out var p)) ? p.HunterHits : 0; }
        }
        /// <summary>Friendly-fire hits the local hunter can take before being knocked down (their "HP").</summary>
        internal int LocalHunterMaxHits
        {
            get { var id = LocalId; return (id != 0 && _state.Players.TryGetValue(id, out var p)) ? Math.Max(1, p.HunterMaxHits) : Math.Max(1, _settings.HunterHitsToDown); }
        }
        /// <summary>True while the local player is knocked down (ragdolled) by friendly fire or a concussion.</summary>
        internal bool LocalDowned
        {
            get { var id = LocalId; return id != 0 && _state.Players.TryGetValue(id, out var p) && p.Downed; }
        }
        /// <summary>True once the local player is out of the round. A caught hider KEEPS <see cref="PlayerRole.Hider"/>
        /// under the Spectator caught-behaviour (only Infection flips the role), so a role check alone never sees them -
        /// anything that must not apply to someone who is already out has to test this.</summary>
        internal bool LocalEliminated
        {
            get { var id = LocalId; return id != 0 && _state.Players.TryGetValue(id, out var p) && p.Eliminated; }
        }
        /// <summary>Whole seconds left on the local player's knockdown (0 if not downed).</summary>
        internal int LocalDownedSecondsLeft
        {
            get { var id = LocalId; return (id != 0 && _state.Players.TryGetValue(id, out var p) && p.Downed) ? (int)Math.Max(0, p.DownedUntilUnix - NowUnix()) : 0; }
        }
        /// <summary>Prop changes the local hider has used this round.</summary>
        internal int LocalChanges
        {
            get { var id = LocalId; return (id != 0 && _state.Players.TryGetValue(id, out var p)) ? p.Changes : 0; }
        }
        internal float LocalPropYaw => _localYaw;

        /// <summary>
        /// The prop the local player is wearing, wherever they are: the round's prop, or the lobby dressing room's.
        ///
        /// Ask THIS, not LocalPropId, anywhere the question is "am I currently a prop" - the camera pull-back, the
        /// own-body visibility, the [F] turn. LocalPropId is the round's roster field and reads -1 in the lobby, so every
        /// one of those quietly did nothing there while the disguise itself rendered perfectly.
        /// </summary>
        internal int WornPropId
        {
            get
            {
                int round = LocalPropId;
                return round >= 0 ? round : LocalLobbyProp;
            }
        }

        /// <summary>The prop the local player is wearing in the LOBBY dressing room, or -1. Read from the synced field
        /// rather than a local flag, so it is the same answer every other client has.</summary>
        internal int LocalLobbyProp
        {
            get
            {
                if (_state == null || _state.Phase != RoundPhase.Lobby) return -1;
                var id = LocalId;
                if (id == 0) return -1;
                var worn = Disguise.LobbyPropCodec.Parse(_state.LobbyProps);
                return worn.TryGetValue(id, out var w) ? w.PropId : -1;
            }
        }
        internal int LocalDecoysUsed
        {
            get { var id = LocalId; return (id != 0 && _state.Players.TryGetValue(id, out var p)) ? p.DecoysUsed : 0; }
        }
        internal int LocalConcussUsed
        {
            get { var id = LocalId; return (id != 0 && _state.Players.TryGetValue(id, out var p)) ? p.ConcussUsed : 0; }
        }

        /// <summary>The local player's currently-synced prop id (-1 = not disguised) + its catalog name (for HUD/debug).</summary>
        internal int LocalPropId
        {
            get { var id = LocalId; return (id != 0 && _state.Players.TryGetValue(id, out var p)) ? p.PropId : -1; }
        }

        /// <summary>The synced prop id for any player id (-1 if not disguised or unknown). Used by CatchController
        /// to scale the SphereCast radius to the victim's current prop size.</summary>
        internal int PropIdOf(ulong id)
            => (id != 0 && _state.Players.TryGetValue(id, out var p)) ? p.PropId : -1;
        internal string LocalPropName
        {
            get { int pid = WornPropId; return pid >= 0 ? PropHunt.Disguise.PropCatalog.ById(pid)?.Name : null; }
        }
        /// <summary>Whether the local player's prop rotation is frozen ([F] toggles it).</summary>
        internal bool LocalLocked
        {
            get { var id = LocalId; return id != 0 && _state.Players.TryGetValue(id, out var p) && p.Locked; }
        }

        internal int SecondsLeft =>
            _state.PhaseEndsAtUnix <= 0 ? 0 : (int)Math.Max(0, _state.PhaseEndsAtUnix - NowUnix());

        /// <summary>Seconds until the next round actually starts, composed across RoundEnd -> Safehouse -> doors so it
        /// ticks down as ONE countdown (see <see cref="RoundLogic.SecondsUntilNextRound"/>). -1 = no predictable next
        /// round (Single, or a manual Safehouse waiting on the host).</summary>
        internal int SecondsUntilNextRound => RoundLogic.SecondsUntilNextRound(_state, _settings, NowUnix());

        // ---- clock ----
        // Every timestamp in GameState is absolute unix time from the HOST's clock. Two Windows machines routinely
        // disagree by several seconds, so a client that read its own clock rendered every countdown wrong by that
        // much - the whistle "arrived early", the round timer was off. The host stamps HostNowUnix into each push;
        // the client subtracts its own clock from it and applies the difference everywhere. On the host the offset
        // is 0 by construction, so host behaviour is unchanged.
        private long _clockOffset;      // seconds to add to the local clock to get host time
        private bool _clockSynced;

#if DEBUG
        /// <summary>phclockskew: fake a local clock that runs N seconds off, so the offset correction is provable on
        /// ONE machine (two local instances otherwise share the same system clock and can never disagree).</summary>
        internal static long DebugClockSkew;
#endif

        private static long RawNowUnix()
        {
            long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
#if DEBUG
            t += DebugClockSkew;
#endif
            return t;
        }

        /// <summary>Wall clock in HOST time. Use this for anything compared against a GameState timestamp.</summary>
        internal long NowUnix() => RawNowUnix() + _clockOffset;

        /// <summary>Current offset in seconds (0 on the host / before the first push). Diagnostics only.</summary>
        internal long ClockOffset => _clockOffset;

        /// <summary>Re-learn the host-clock offset from a freshly received snapshot. Snaps on the first push (so the
        /// very first countdown is already right) and eases afterwards, so a single delayed packet cannot make the
        /// timer jump - the offset itself is near-constant, only the transport jitters.</summary>
        private void SyncClock(GameState fresh)
        {
            if (_isHost || fresh == null || fresh.HostNowUnix <= 0) return;   // old host without the field -> keep what we have
            long observed = fresh.HostNowUnix - RawNowUnix();

            // Take the HIGHEST reading, never the latest one.
            //
            // The state lives in lobby data, so what arrives is not always fresh: a joining client reads whatever
            // the host last published, which can be many seconds old, and a stamp from the past makes the clocks
            // look further apart than they are. Measured live at -29s between two instances on ONE machine, whose
            // clocks are by definition identical - taking that at face value would have skewed every countdown by
            // half a minute, worse than the bug this exists to fix.
            //
            // Staleness can only ever drag the reading DOWN (an older stamp is a smaller number), and nothing can
            // push it above the truth - a stamp cannot arrive before it was made. So the maximum across samples IS
            // the answer, and it never decays: a stale blob arrives constantly (every lobby-data read is one), so
            // anything that eases downward would simply be dragged back down by the next one. The estimate resets
            // with the session, which is the only point a clock could plausibly have changed underneath us.
            if (!_clockSynced)
            {
                _clockOffset = observed;
                _clockSynced = true;
                return;   // the first sample is the least trustworthy one - do not announce it
            }

            if (observed <= _clockOffset) return;

            long was = _clockOffset;
            _clockOffset = observed;
            if (System.Math.Abs(_clockOffset) >= 2 && was != _clockOffset)
                Core.Log.Msg($"[PropHunt] host clock is {_clockOffset:+#;-#;0}s from ours - correcting every timer by that.");
        }

        /// <summary>Local reveal cue when a taunt fires (host direct; clients via the P2P handler): flash the HUD
        /// and play the taunt sound at the hider's world position (3D, long range). Empty sound -> a default.</summary>
        internal void NotifyTaunt(ulong steamId, string sound, bool isWhistle = false)
        {
            _lastTauntTime = Time.time;
            try
            {
                Player gp = (steamId == LocalId) ? Player.Local : PlayerRegistry.Get(steamId);
                if (gp == null) return;
                string clip = string.IsNullOrEmpty(sound) ? Taunt.TauntSounds.PickDefault() : sound;
                if (isWhistle) Taunt.TauntSounds.PlayWhistle(clip, gp.transform.position);
                else Taunt.TauntSounds.Play(clip, gp.transform.position);
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] taunt sound failed: " + e.Message); }
        }

        // ---- action feedback (catch / stun / decoy pop): a 3D SFX + a brief screen flash so outcomes read
        // clearly. The host emits these where it validates the action; clients receive them via P2P. Best-effort
        // clip names (resolved against the game's audio library at runtime; silent if none match). ----
        private static readonly string[] HitClips   = { "bullet_impact", "impact", "flesh", "thud" };
        private static readonly string[] CatchClips = { "bullet_impact", "impact", "thud", "hit" };
        private static readonly string[] StunClips  = { "taze", "electric", "shock", "zap", "stun" };
        private static readonly string[] DecoyClips = { "glass", "shatter", "break", "pop" };

        private string _fxText;
        private Color _fxColor = Color.white;
        private float _fxUntil;
        private void SetFx(string text, Color color) { _fxText = text; _fxColor = color; _fxUntil = Time.time + 1.2f; }
        internal bool FxActive => Time.time < _fxUntil && !string.IsNullOrEmpty(_fxText);
        internal string FxText => _fxText;
        internal Color FxColor => _fxColor;

        private static void BroadcastFx(P2PMessage msg) { try { PropHuntNet.Client?.BroadcastMessage(msg); } catch { } }

        /// <summary>A hunter landed a hit/catch on a hider: play an impact at the victim; flash the hunter and the
        /// victim. Runs on every client (host calls it directly; clients via the P2P handler).</summary>
        internal void NotifyCatchFx(ulong hunterId, ulong victimId, bool caught, Vector3 pos)
        {
            try { Taunt.TauntSounds.PlayFx(caught ? CatchClips : HitClips, pos, 0.8f); } catch { }
            ulong me = LocalId;
#if DEBUG
            // DEBUG-ONLY: the on-screen HIT/CAUGHT text is test feedback (the hunter's flash also reveals which props
            // are real hiders), so it ships only in Debug. In Release the HP bar + scoreboard carry the real info.
            if (me == hunterId) SetFx(caught ? "CATCH!" : "HIT", new Color(0.3f, 1f, 0.5f));
            if (me == victimId) SetFx(caught ? "CAUGHT!" : "HIT!", Color.red);
#endif
        }

        /// <summary>A concussion went off: play a stun SFX at the centre; the thrower gets confirmation, a local
        /// hunter inside the blast radius flashes STUNNED.</summary>
        internal void NotifyStunFx(ulong throwerId, Vector3 pos)
        {
            try { Taunt.TauntSounds.PlayFx(StunClips, pos, 0.85f); } catch { }
            if (LocalId == throwerId) { SetFx("STUN!", Color.cyan); return; }
            if (LocalRole == PlayerRole.Hunter)
            {
                try { var lp = Player.Local; if (lp != null && Vector3.Distance(lp.transform.position, pos) <= _settings.ConcussRadius + 1f) SetFx("STUNNED!", new Color(1f, 0.4f, 1f)); }
                catch { }
            }
        }

        /// <summary>A decoy was revealed as fake: play a pop at its position; the hunter who shot it flashes DECOY.</summary>
        internal void NotifyDecoyFx(ulong hunterId, Vector3 pos)
        {
            try { Taunt.TauntSounds.PlayFx(DecoyClips, pos, 0.8f); } catch { }
#if DEBUG
            // DEBUG-ONLY: telling the hunter "that was a DECOY" reveals decoys (a cheat in real play); Debug-only.
            if (LocalId == hunterId) SetFx("DECOY!", Color.yellow);
#endif
        }

        /// <summary>IMGUI hook (called from Core.OnGUI): the taunt selection wheel.</summary>
        // Only the radial taunt wheel still draws via IMGUI (an input widget out of the HUD redesign's scope). The
        // role card + [H] controls overlay moved to the uGUI HUD (HudRoot reads Onboarding state + content).
        internal void DrawGui() { try { _tauntWheel?.DrawGui(); } catch { } }

#if DEBUG
        /// <summary>DEBUG-only: dump the prop pipeline state - catalog size/hash, crosshair target, highlight count,
        /// and a live count of becomable objects within reach of the local player.</summary>
        internal void DumpPropDebug()
        {
            Core.Log.Msg($"[PropHunt] props: catalog={PropCatalog.Count} hash={PropCatalog.Hash} stateHash={_state.CatalogHash} " +
                         $"phase={_state.Phase} role={LocalRole} highlighted={(_highlighter != null ? _highlighter.HighlightedCount : 0)}");
            // The pool is the usual reason a prop refuses to be taken, so say plainly whether one is in force and how
            // much of our catalog it leaves us.
            Core.Log.Msg(PropCatalog.HostPool == null
                ? "[PropHunt] props: no host pool (we are the host, or it has not arrived yet) - all of our props are becomable."
                : $"[PropHunt] props: host pool {PropCatalog.HostPool.Count} prop(s) -> {PropCatalog.BecomableCount()} of our {PropCatalog.Count} are becomable.");
            string tgt = _picker?.CurrentTargetName;
            Core.Log.Msg($"[PropHunt] props: crosshair -> {(tgt != null ? $"'{tgt}' (id {_picker.CurrentTargetId})" : "<nothing becomable>")}");
            try
            {
                var lp = Player.Local;
                if (lp != null)
                {
                    int near = 0, scanned = 0;
                    var hits = Physics.OverlapSphere(lp.transform.position, 22f);
                    if (hits != null)
                        for (int i = 0; i < hits.Length; i++)
                        {
                            var c = hits[i]; if (c == null) continue; scanned++;
                            var mf = c.GetComponentInParent<MeshFilter>();
                            if (mf != null && PropCatalog.IdForMeshFilter(mf) >= 0) near++;
                        }
                    Core.Log.Msg($"[PropHunt] props: {near} becomable object(s) within 22m ({scanned} colliders scanned).");
                }
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] DumpPropDebug scan failed: " + e.Message); }
        }
#endif

        // ---- lifecycle ----

        internal void StartAsHost()
        {
            Active = this;
            _settings = BuildSettings();
            EnsureHandlers();
            Core.LogDebug("[PropHunt] StartAsHost: creating state var...");
            EnsureStateVar();
            Core.LogDebug("[PropHunt] StartAsHost: building prop catalog...");
            PropCatalog.BuildIfNeeded();
            _disguise = new DisguiseController { LiveLocalYaw = () => _localYaw };
            _decoy = new DecoyController();
            _picker = new PropPicker(this);
            _highlighter = new PropHighlighter(this);
            PropHunt.View.EyeBlink.ResetState();   // clear any static blink/blindfold state leaked by an abnormal prior teardown
            PropHunt.View.BodyCam.Stop();          // and any leaked body-cam camera override
            _thirdPerson = new PropHunt.View.ThirdPersonController(this);
            _catch = new CatchController(this);
            _passthrough = new PropPassthrough(this);
            _playArea = new PlayAreaController(this);
            _border = new PlayAreaBorder(this);
            _taunt = new TauntController(this);
            _propRotation = new PropRotationController(this);
            _mapRing = new PlayArea.MapAreaRing(this);
            _tauntWheel = new Taunt.TauntWheel(this);
            _onboarding = new UI.Onboarding(this);
            _spectator = new PropHunt.View.SpectatorController(this);
            _state = new GameState { Phase = RoundPhase.Lobby, SettingsBlob = _settings.Serialize(), CatalogHash = PropCatalog.Hash };
            RoundLogic.SyncRoster(_state, GetMemberIds());
            PushState();
            BroadcastPropPool(force: true);   // tell joiners which props they may become before anyone can pick one
            Core.Log.Msg($"[PropHunt] host session started (Lobby). Settings: {_settings}");
        }

        // Host config: the Side Hustle host form sends the chosen round settings as the launch ConfigBlob (its
        // descriptor keys match RoundSettings' keys). Parse it over the saved-pref defaults; fall back to the prefs
        // when launched without a config (e.g. the standalone co-op test path).
        private RoundSettings BuildSettings()
        {
            var defaults = PropHuntPreferences.BuildRoundSettings();
            string blob = _ctx?.Multiplayer?.ConfigBlob;
            return string.IsNullOrEmpty(blob) ? defaults : RoundSettings.Parse(blob, defaults);
        }

        internal void StartAsClient()
        {
            Active = this;
            EnsureHandlers();
            EnsureStateVar();
            PropCatalog.BuildIfNeeded();
            _disguise = new DisguiseController { LiveLocalYaw = () => _localYaw };
            _decoy = new DecoyController();
            _picker = new PropPicker(this);
            _highlighter = new PropHighlighter(this);
            PropHunt.View.EyeBlink.ResetState();   // clear any static blink/blindfold state leaked by an abnormal prior teardown
            PropHunt.View.BodyCam.Stop();          // and any leaked body-cam camera override
            _thirdPerson = new PropHunt.View.ThirdPersonController(this);
            _catch = new CatchController(this);
            _passthrough = new PropPassthrough(this);
            _playArea = new PlayAreaController(this);
            _border = new PlayAreaBorder(this);
            _taunt = new TauntController(this);
            _propRotation = new PropRotationController(this);
            _mapRing = new PlayArea.MapAreaRing(this);
            _tauntWheel = new Taunt.TauntWheel(this);
            _onboarding = new UI.Onboarding(this);
            _spectator = new PropHunt.View.SpectatorController(this);
            try { var cur = _stateVar?.Value; if (!string.IsNullOrEmpty(cur)) ApplyStateString(cur); } catch { }
            Core.Log.Msg("[PropHunt] client session started; waiting for host state.");
        }

        /// <summary>Host: begin the match (host setup screen "START MATCH" or the phstart debug command).</summary>
        internal void BeginMatch()
        {
            if (!_isHost) { Core.Log.Warning("[PropHunt] BeginMatch ignored - not host."); return; }
            // A prop hunt needs at least one hunter AND one hider; with a single player a round would assign the
            // lone player as hunter, leave zero hiders, and end the instant it starts. Wait for a second player.
            if (GetMemberIds().Count < 2) { Core.Log.Msg("[PropHunt] need at least 2 players to start - waiting for more to join."); return; }
            if (_matchStarted && _state.Phase != RoundPhase.Lobby) { Core.Log.Msg("[PropHunt] match already running."); return; }
            _matchStarted = true;
            _state.SettingsBlob = _settings.Serialize();
            _state.CatalogHash = PropCatalog.Hash;
            SetPlayArea();   // radius + a host-position fallback centre
            // centre the first round on a size-appropriate safehouse too, so every round's map is "around a safehouse"
            // (round 1 spawns players at it with the doors at their default exit-only state - no lock phase).
            _state.SafehouseCode = SafehouseSelector.SelectForPlayerCount(GetMemberIds().Count);
            if (!string.IsNullOrEmpty(_state.SafehouseCode)) CenterPlayAreaOnSafehouse(_state.SafehouseCode);
            // Roll who hunts first, BEFORE roles are assigned. Without this the round-robin always starts at the lowest
            // steam id, so one unlucky player hunted the opening round of every match they ever hosted or joined.
            _state.RoleOffset = UnityEngine.Random.Range(0, 100000);
            RoundLogic.BeginMatch(_state, _settings, NowUnix(), GetMemberIds());
            PushState();
            RoundEnvironment.ApplyHostWorld(_settings);   // lock time of day + freeze; police suppressed each tick
            Core.Log.Msg($"[PropHunt] match begun. {_settings}");
        }

        /// <summary>Host: confirm next-round settings + open the safehouse, advancing Safehouse -> next round.
        /// Called from the between-rounds setup screen ("START NEXT ROUND") or the phnextround debug command.</summary>
        internal void BeginNextRound()
        {
            if (!_isHost || _state.Phase != RoundPhase.Safehouse) return;
            _state.SettingsBlob = _settings.Serialize();   // re-publish any settings the host changed in the lobby
            RoundLogic.ConfirmSafehouseReady(_state, NowUnix());
            PushState();
            Core.Log.Msg($"[PropHunt] host starting next round. {_settings}");
        }

        // ---- safehouse (between-rounds lobby; its surroundings are the play area) ----

        private string _appliedSafehouseCode = "";   // the safehouse we've locally entered/locked (tracks code changes)

        /// <summary>
        /// Reconcile local state with the synced safehouse each tick. On entering the Safehouse phase (or the host
        /// switching the map) it teleports the local player inside, locks the doors, and (host) re-centres the play
        /// area on that property so the round happens AROUND it. On leaving the phase it OPENS the doors so players
        /// walk straight out into the map - there is no teleport-away. Handles late-join + map switches uniformly.
        /// </summary>
        private void ApplySafehousePresence()
        {
            if (_state.Phase == RoundPhase.Safehouse)
            {
                if (_state.SafehouseCode == _appliedSafehouseCode) return;
                // host switched the map -> unlock the previous one first
                if (_isHost && !string.IsNullOrEmpty(_appliedSafehouseCode)) ApplyDoorAccess(_appliedSafehouseCode, false, swing: true);
                _appliedSafehouseCode = _state.SafehouseCode;
                if (string.IsNullOrEmpty(_appliedSafehouseCode)) return;
                if (_isHost)
                {
                    CenterPlayAreaOnSafehouse(_appliedSafehouseCode);   // the area around this safehouse is the next map
                    _state.SafehouseSeed = UnityEngine.Random.Range(1, int.MaxValue);   // fresh per entry -> random (but synced) spawn assignment
                    SetSafehouseDoorAccess(_appliedSafehouseCode, true);
                    PushState();   // publish the re-centred area + seed + (re)selected code to clients
                }
                else ApplyDoorAccess(_appliedSafehouseCode, true, swing: false);
                TeleportLocalToSafehouse(_appliedSafehouseCode);
                TurnOnSafehouseLights(_appliedSafehouseCode);
            }
            else if (!string.IsNullOrEmpty(_appliedSafehouseCode))
            {
                // round starting (Safehouse -> Hiding): open the doors so everyone spills out into the map.
                if (_isHost) SetSafehouseDoorAccess(_appliedSafehouseCode, false);
                else ApplyDoorAccess(_appliedSafehouseCode, false, swing: false);
                _appliedSafehouseCode = "";
            }
        }

        /// <summary>Host: centre the synced play area on the safehouse's spawn point (the map is the radius around it).</summary>
        private void CenterPlayAreaOnSafehouse(string code)
        {
            try
            {
                var prop = FindProperty(code);
                if (prop == null) return;
                var t = prop.InteriorSpawnPoint != null ? prop.InteriorSpawnPoint : prop.SpawnPoint;
                var pos = t != null ? t.position : prop.transform.position;
                _state.AreaX = pos.x; _state.AreaY = pos.y; _state.AreaZ = pos.z;
                _state.AreaRadius = Mathf.Max(_settings.PlayAreaRadius, MinPlayAreaRadius);   // enforce the floor at round start too
            }
            catch { }
        }

        /// <summary>Host: cycle the safehouse among the maps big enough for the current lobby (the "Switch map" button).</summary>
        internal void SwitchSafehouse(int dir)
        {
            if (!_isHost || _state.Phase != RoundPhase.Safehouse) return;
            var avail = SafehouseSelector.AvailableForPlayerCount(_state.Players.Count);
            if (avail.Count == 0) return;
            int cur = avail.IndexOf(_state.SafehouseCode);
            int next = cur < 0 ? 0 : (((cur + dir) % avail.Count) + avail.Count) % avail.Count;
            _state.SafehouseCode = avail[next];
            PushState();   // ApplySafehousePresence picks up the change next tick (re-teleport + re-lock + re-centre)
            Core.Log.Msg($"[PropHunt] host switched safehouse -> '{_state.SafehouseCode}' ({avail.Count} options for {_state.Players.Count}).");
        }

        /// <summary>Friendly display name of a property code (for the HUD), or the code if not resolvable.</summary>
        internal string SafehouseName(string code) { var p = FindProperty(code); return p != null ? p.PropertyName : (code ?? ""); }
        /// <summary>How many maps are big enough for the current lobby (shown next to the switch button).</summary>
        internal int SafehouseOptionCount => SafehouseSelector.AvailableForPlayerCount(_state.Players.Count).Count;

        private void TeleportLocalToSafehouse(string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            try
            {
                var prop = FindProperty(code);
                if (prop == null) { Core.Log.Warning($"[PropHunt] safehouse '{code}' not found in scene."); return; }

                // Authored points first: each player teleports to a DISTINCT baked-in interior spot. The index is
                // the local player's rank in the sorted lobby-member list, so host + every client independently
                // compute the same assignment (the teleport itself is a local owner-move - no server reconcile).
                if (SpawnStore.HasSpawns(code))
                {
                    var pts = SpawnStore.GetSpawns(code);
                    // rank from the SYNCED roster (every client parses the SAME GameState.Players), so all clients
                    // agree on each player's rank. Using the local GetMemberIds() desynced (a member not yet
                    // replicated on one client) and put two players on the same point.
                    var ids = new List<ulong>(_state.Players.Keys);
                    ids.Sort();
                    int rank = ids.IndexOf(LocalId);
                    if (rank < 0) rank = (int)(LocalId % (ulong)System.Math.Max(1, pts.Count));   // not in roster yet (late join)
                    // randomised but coordinated: every client shuffles the points with the host's synced seed, then
                    // indexes by rank - so positions are random (host isn't always point 1) yet distinct + agreed on.
                    int idx = ShuffledSpawnIndex(rank, pts.Count, _state.SafehouseSeed);
                    var sp = pts[idx];
                    RoundEnvironment.TeleportLocalTo(sp.Pos + UnityEngine.Vector3.up * 1f, sp.Yaw);   // face + move together, hidden by the blink
                    Core.Log.Msg($"[PropHunt] entered safehouse '{code}' (authored point {idx + 1}/{pts.Count}, rank {rank}, seed {_state.SafehouseSeed}).");
                    return;
                }

                // Fallback (no authored points yet): InteriorSpawnPoint + a tight ring (kept small so a motel room
                // doesn't push clients through its walls). Replaced per-property once the phspawn editor is used.
                var t = prop.InteriorSpawnPoint != null ? prop.InteriorSpawnPoint : prop.SpawnPoint;
                UnityEngine.Vector3 basePos = t != null ? t.position : prop.transform.position;
                ulong sid = LocalId;
                float ang = (sid % 360UL) * UnityEngine.Mathf.Deg2Rad;
                float r = 0.3f + (sid % 3UL) * 0.35f;   // 0.3 .. 1.0m
                RoundEnvironment.TeleportLocalTo(basePos + new UnityEngine.Vector3(UnityEngine.Mathf.Cos(ang) * r, 0f, UnityEngine.Mathf.Sin(ang) * r));
                Core.Log.Msg($"[PropHunt] entered safehouse '{code}' (ring fallback - no authored points).");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] TeleportLocalToSafehouse failed: " + e.Message); }
        }

        /// <summary>Turn ON all of the safehouse's lights when players spawn in (the interior should be lit during the
        /// lobby). Flips every wired light switch (the same path a player flipping it would take) AND forces any
        /// ToggleableLight under the interior on (catches fixtures not wired to a switch, e.g. the RV). Local + cosmetic,
        /// so run on every client. Time is frozen during a round (ApplyHostWorld) so a LightTimer won't turn them off.</summary>
        private static void TurnOnSafehouseLights(string code)
        {
            if (string.IsNullOrEmpty(code)) return;
            try
            {
                var prop = FindProperty(code);
                if (prop == null) return;
                int n = 0;
                var switches = prop.Switches;
                if (switches != null)
                    for (int i = 0; i < switches.Count; i++)
                    {
                        var sw = switches[i];
                        if (sw == null) continue;
                        try { sw.SwitchOn(); n++; } catch { }
                    }
                try
                {
                    var lights = prop.GetComponentsInChildren<Il2CppScheduleOne.Misc.ToggleableLight>(true);
                    if (lights != null)
                        for (int i = 0; i < lights.Length; i++)
                        { var l = lights[i]; if (l != null) { try { l.TurnOn(); } catch { } } }
                }
                catch { }
                Core.LogDebug($"[PropHunt] safehouse '{code}' lights ON ({n} switch(es)).");
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] TurnOnSafehouseLights failed: " + e.Message); }
        }

        /// <summary>Host: set the coded property's doors locked/open locally + broadcast to clients (PlayerAccess is
        /// not a SyncVar, so it must be pushed explicitly - see <see cref="SafehouseDoorLockMessage"/>).</summary>
        private void SetSafehouseDoorAccess(string code, bool locked)
        {
            ApplyDoorAccess(code, locked, swing: true);   // host (server) swings the door; the visual replicates
            BroadcastSafehouseDoorLock(code, locked);
        }

        /// <summary>Set every PropertyDoorController of the coded property to Locked+closed or Open. PlayerAccess is
        /// a LOCAL field (set on every client). The open/closed SWING is networked via SetIsOpen_Server, so only the
        /// host (FishNet server) swings it - the visual then replicates to clients. Idempotent.</summary>
        private const float SafehouseDoorRadius = 22f;   // non-property doors (RV/sewer/plain) within this of the spawn

        private static void ApplyDoorAccess(string code, bool locked, bool swing)
        {
            if (string.IsNullOrEmpty(code)) return;
            try
            {
                // resolve the property's interior spawn so we can also catch nearby NON-PropertyDoorController doors
                // (the RV's + the sewer office's doors are plain DoorController / SewerDoorController with no .Property
                // back-ref, so the property-code match alone left them open).
                var prop = FindProperty(code);
                UnityEngine.Vector3 center = UnityEngine.Vector3.zero; bool haveCenter = false;
                if (prop != null)
                {
                    var t = prop.InteriorSpawnPoint != null ? prop.InteriorSpawnPoint : prop.SpawnPoint;
                    center = t != null ? t.position : prop.transform.position; haveCenter = true;
                }

                int n = 0;
                var doors = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Doors.DoorController>();   // base -> all door types
                if (doors != null)
                    for (int i = 0; i < doors.Length; i++)
                    {
                        var d = doors[i];
                        if (d == null) continue;
                        // NEVER a sewer door. On the exterior side its access is key-gated
                        // (SewerDoorController.CanPlayerAccess): a door that is merely OPEN rather than UNLOCKED needs
                        // the Sewer Key once it has been closed, and PlayerAccess = Open does not get a say - the
                        // override returns false before it defers to the base. So swinging one shut during the
                        // safehouse phase barred the sewer for the whole session: anyone already down there was
                        // unreachable and nobody could follow them in. It is a route into a separate area, not a room
                        // of the safehouse, and the safehouse lock has no business touching it.
                        if (d.TryCast<Il2CppScheduleOne.Doors.SewerDoorController>() != null) continue;
                        bool belongs;
                        var pdc = d.TryCast<Il2CppScheduleOne.Building.Doors.PropertyDoorController>();
                        if (pdc != null)
                            belongs = pdc.Property != null && pdc.Property.PropertyCode == code;   // this property's doors, any distance
                        else
                            belongs = haveCenter && UnityEngine.Vector3.Distance(d.transform.position, center) <= SafehouseDoorRadius;   // RV/sewer/plain
                        if (!belongs) continue;
                        n++;
                        d.PlayerAccess = locked ? Il2CppScheduleOne.Doors.EDoorAccess.Locked : Il2CppScheduleOne.Doors.EDoorAccess.Open;
                        if (swing) { try { d.SetIsOpen_Server(!locked, Il2CppScheduleOne.Doors.EDoorSide.Interior, false); } catch { } }
                    }
                Core.LogDebug($"[PropHunt] safehouse '{code}' doors {(locked ? "LOCKED" : "OPENED")} ({n}, swing={swing}).");
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] ApplyDoorAccess failed: " + e.Message); }
        }

        private static Il2CppScheduleOne.Property.Property FindProperty(string code)
        {
            try
            {
                var props = Il2CppScheduleOne.Property.Property.Properties;
                if (props != null)
                    for (int i = 0; i < props.Count; i++)
                    {
                        var p = props[i];
                        if (p != null && p.PropertyCode == code) return p;
                    }
            }
            catch { }
            return null;
        }

        /// <summary>Deterministic seeded permutation of [0..count): every client shuffles the spawn points the same
        /// way from the host's synced seed, then indexes by the player's rank. Result is RANDOM (the host isn't
        /// always point 0) yet distinct + identical on all machines. Pure LCG Fisher-Yates (not runtime-RNG dependent).</summary>
        private static int ShuffledSpawnIndex(int rank, int count, int seed)
        {
            if (count <= 1) return 0;
            var perm = new int[count];
            for (int i = 0; i < count; i++) perm[i] = i;
            uint s = (uint)seed; if (s == 0) s = 1u;
            for (int i = count - 1; i > 0; i--)
            {
                s = s * 1664525u + 1013904223u;   // LCG step
                int j = (int)(s % (uint)(i + 1));
                int tmp = perm[i]; perm[i] = perm[j]; perm[j] = tmp;
            }
            return perm[((rank % count) + count) % count];
        }

        private void BroadcastSafehouseDoorLock(string code, bool locked)
        {
            if (!_isHost) return;
            try { PropHuntNet.Client?.BroadcastMessage(new SafehouseDoorLockMessage { PropertyCode = code, Locked = locked }); } catch { }
        }

        private int _sentPoolHash = int.MinValue;   // last pool we told clients about, so we only resend on a change

        /// <summary>Host: publish the props WE can render, which is the set hiders are allowed to pick from.
        ///
        /// Sent when the session starts and again whenever our own catalog changes - walking into a building streams
        /// its interior in, and those props become available to everyone rather than only to whoever is standing
        /// inside. Cheap to repeat: it only goes out when the content signature actually moved.</summary>
        private void BroadcastPropPool(bool force = false)
        {
            if (!_isHost) return;
            try
            {
                int h = PropCatalog.Hash;
                if (!force && h == _sentPoolHash) return;
                _sentPoolHash = h;
                var ids = PropCatalog.AllIds();
                PropHuntNet.Client?.BroadcastMessage(new PropPoolMessage { Ids = ids });
                Core.LogDebug($"[PropHunt] prop pool published: {ids.Count} prop(s), hash {h}.");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] publishing the prop pool failed: " + e.Message); }
        }

        /// <summary>Client: adopt the host's pool. From here a hider is only offered props the host can draw too, so
        /// a disguise can no longer turn into nothing on the host's screen.</summary>
        private void HandlePropPool(List<int> ids)
        {
            if (_isHost || ids == null) return;
            try
            {
                if (!PropCatalog.SetHostPool(new HashSet<int>(ids))) return;
                Core.Log.Msg($"[PropHunt] host prop pool: {ids.Count} prop(s); {PropCatalog.BecomableCount()} of our {PropCatalog.Count} are usable.");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] adopting the host prop pool failed: " + e.Message); }
        }

        /// <summary>Client handler: apply a door lock/open the host pushed.</summary>
        private void HandleSafehouseDoorLock(string code, bool locked)
        {
            if (_isHost) return;   // host already applied it directly
            ApplyDoorAccess(code, locked, swing: false);   // client sets the local access flag; swing replicates from host
        }

        internal void Tick(float dt)
        {
            if (_disposed) return;
            if (_stateVar == null) { EnsureStateVar(); if (_stateVar == null) return; }
            if (!_handlersRegistered) EnsureHandlers();

            // drive the music-bus cross-fade every frame (fade down at the hunt, back up otherwise); cheap no-op when idle.
            PropHunt.Music.RoundMusicController.Tick(dt);

            if (_isHost)
            {
                // Keep the roster live even BEFORE the match starts (in the pre-match Lobby) so the Stats/Players tabs
                // show every joiner, not just the host. The round machine (TickHost) still only runs once started.
                bool changed = RoundLogic.SyncRoster(_state, GetMemberIds());
                // A joiner has no pool until we send it one, and the send is a no-op unless the roster or our own
                // catalog actually moved - so this costs a hash compare on an ordinary tick.
                if (changed) BroadcastPropPool(force: true);
                if (_matchStarted)
                {
                    // pre-select the safehouse (size-based) BEFORE TickHost may transition RoundEnd -> Safehouse,
                    // so the pure RoundLogic can read the chosen code without any engine/Property dependency.
                    if (_state.Phase == RoundPhase.RoundEnd && _settings.Structure == RoundStructure.Continuous && !SafehouseSelector.Fits(_state.SafehouseCode, _state.Players.Count))
                        _state.SafehouseCode = SafehouseSelector.SelectForPlayerCount(_state.Players.Count);
                    if (RoundLogic.TickHost(_state, _settings, NowUnix())) changed = true;
                }
                if (changed)
                {
                    // carry any live host edit with every state push (incl. an auto round-transition), so a setting
                    // changed mid-round can never reach clients a round late.
                    _state.SettingsBlob = _settings.Serialize();
                    _settingsDirty = false;
                    PushState();
                }
            }

            // Host: re-publish phone-edited settings so clients see them live (throttled, runs in any phase incl. the
            // Lobby/Safehouse where the host edits between rounds).
            if (_isHost && _settingsDirty && Time.unscaledTime - _lastSettingsPush > 0.4f)
            {
                _settingsDirty = false;
                _lastSettingsPush = Time.unscaledTime;
                _state.SettingsBlob = _settings.Serialize();
                PushState();
            }

            if (_state.Phase != _loggedPhase)
            {
                var prevPhase = _loggedPhase;
                _loggedPhase = _state.Phase;
                Core.Log.Msg($"[PropHunt] phase -> {_state.Phase} (round {_state.RoundNumber}, you={LocalRole}, {SecondsLeft}s, " +
                             $"hunters={RoundLogic.CountRole(_state, PlayerRole.Hunter)}, hiders={AliveHiderCount}, winner={_state.Winner})");

                if (_state.Phase == RoundPhase.Hiding)
                {
                    // re-apply the world time at the start of every round, so each round begins at the configured
                    // time of day (and, with FreezeTime off, the clock then runs from there instead of staying locked).
                    if (_isHost) RoundEnvironment.ApplyHostWorld(_settings);
                    // Rebuild the catalog now the world is fully loaded (the client's session-start build can
                    // run BEFORE the scene finishes loading -> a near-empty catalog + hash mismatch). Both sides
                    // rebuild at the same lifecycle point -> matching deterministic ids/hash.
                    PropCatalog.Build();
                    if (_isHost && _state.CatalogHash != PropCatalog.Hash) { _state.CatalogHash = PropCatalog.Hash; PushState(); }
                    BroadcastPropPool();   // the rebuild may have picked up interiors that streamed in since
                    // Coming from the safehouse, players are ALREADY inside it (= the play-area centre) and its doors
                    // just opened, so they walk out into the surrounding map - NO teleport. Otherwise (round 1) gather
                    // everyone at the area centre.
                    if (prevPhase != RoundPhase.Safehouse)
                        RoundEnvironment.TeleportLocalInto(_state.AreaX, _state.AreaY, _state.AreaZ, LocalId);
#if DEBUG
                    if (LocalRole == PlayerRole.Hider) DumpPropDebug();
#endif
                }
                // back in the safehouse / between rounds -> reset everyone to first person (a pulled-back third-person
                // view from the last round must not carry into the lobby).
                if (_state.Phase == RoundPhase.Safehouse || _state.Phase == RoundPhase.RoundEnd || _state.Phase == RoundPhase.MatchEnd)
                    _thirdPerson?.ForceOff();
                // MUSIC (one continuous track): the SAME track stays enabled for the whole session and NEVER restarts
                // on a phase change. The hunt DUCKS the music volume bus to 0 (whistles are on the FX bus, unaffected)
                // instead of stopping the track, so it keeps playing silently and RESUMES SEAMLESSLY at round end
                // rather than restarting. Driven per client off the synced phase edge - no netcode. Skipped entirely
                // when no music track is configured (the game's own audio is left alone).
                var musicTrack = PropHunt.Config.PropHuntPreferences.MusicTrack;
                if (!string.IsNullOrEmpty(musicTrack))
                {
                    if (_state.Phase == RoundPhase.Hunting) PropHunt.Music.RoundMusicController.MuteForHunt();
                    else PropHunt.Music.RoundMusicController.Play(musicTrack);
                }
                // arming/disarming the local hunter is role-driven, not phase-edge driven -> ApplyLocalEffects
            }

            ApplySafehousePresence();   // teleport into / switch / out of the safehouse, lock/open doors, centre the area

            // keep the world day-locked + police off + the local player crime-free during a round (incl. the safehouse lobby)
            bool roundActive = RoundActive;
            if (roundActive) RoundEnvironment.ClearLocalCrime();
            if (_isHost && roundActive) RoundEnvironment.SuppressPolice();

            ApplyLocalEffects();
            _picker?.Tick();
            _highlighter?.Tick();
            _spectator?.Tick();
            _thirdPerson?.Tick();
            _catch?.Tick();
            _passthrough?.Tick();
            _border?.Tick();
            _tauntWheel?.Tick();
            _onboarding?.Tick();
            _playArea?.Tick();
            _taunt?.Tick();
            _propRotation?.Tick();   // host-only: reshuffle every hider's prop on the whistle grid (0 = off)
            _mapRing?.Tick();        // keep the play-area ring on the phone map in step with the synced area
            // Re-assert the hotbar flags: the game re-enables equipping on every scene-state change (phone, vehicle,
            // any menu), so a hider would get their hotbar UI back mid-round without this. Cheap - it compares first.
            if (Patches.HotbarSuppression.Disguised) SetHotbar(false);
            // ...and put the phone torch out. Blocking the toggle stops it being switched on, but someone who was
            // already holding a light when they became a prop would keep glowing inside it.
            if (Patches.HotbarSuppression.Disguised) DouseFlashlight();
            TickPropLock();   // a lock lives and dies with the prop it holds
            _disguise?.Apply(_state);
            _decoy?.Apply(_state);
            DriveRagdolls();   // ragdoll/stand-up every player whose synced Downed flag flipped (FF-KO / concussion)

            int lpid = LocalPropId;
            if (lpid != _lastLocalProp)
            {
                _lastLocalProp = lpid;
                var lid = LocalId;   // realign the optimistic local yaw to the synced value on a prop/round change
                _localYaw = (lid != 0 && _state.Players.TryGetValue(lid, out var lp)) ? lp.PropYaw : 0f;
                Core.LogDebug($"[PropHunt] local disguise PropId -> {lpid} ({LocalPropName ?? "none"})");
                UpdatePropCollisionHeight(lpid);
            }

            // Local guidance quest (journal/tracker): created once the phone UI is ready, completed when the player
            // opens the PropHunt app. Points the player at the app as the control/tracking surface.
            Quests.GuideQuest.Tick();

            if (_state.Phase == RoundPhase.MatchEnd) RequestReturnToHub();
        }

        /// <summary>LATE update (after the player has moved this frame): disguise prop transform upkeep, so the
        /// prop doesn't lag-wiggle when the player looks around. The local player's facing uses the optimistic
        /// local yaw for responsiveness; remote players use their synced yaw.</summary>
        internal void LateTick()
        {
            if (_disposed) return;
            try { _disguise?.LateApply(_state, LocalId, _localYaw); } catch { }
        }

        internal void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (Active == this) Active = null;
            PropCollisionState.TargetHeight = 0f;   // restore vanilla CharacterController height on teardown
            try { SlowWalk.Restore(); } catch { }   // restore normal move speed
            if (_isHost) RoundEnvironment.RestoreWorld();
            RestoreLocalEffects();
            try { _disguise?.Dispose(); } catch { }
            try { _decoy?.Dispose(); } catch { }
            try { _highlighter?.Dispose(); } catch { }
            try { _thirdPerson?.Dispose(); } catch { }
            try { _spectator?.ForceExit(); } catch { }   // restore camera + movement if caught/spectating on teardown
            try { _passthrough?.Dispose(); } catch { }   // restore any obstacle collisions we ignored
            try { _border?.Dispose(); } catch { }
            try { _mapRing?.Destroy(); } catch { }   // the ring lives on the phone map, which outlives the session
            try { _tauntWheel?.Dispose(); } catch { }
            try { PropHunt.Music.RoundMusicController.Stop(); } catch { }   // hand music back to the game
            // Stand everyone back up + restore control if we tore down mid-knockdown (clear the sets first, so the state
            // resets even if one of the bodies has already been destroyed).
            try
            {
                var stuck = new List<ulong>(_ragdolled); _ragdolled.Clear();
                foreach (var id in stuck) { var pl = PlayerRegistry.Get(id); if (pl != null) StandUp(pl); }
            }
            catch { }
            // UNCONDITIONALLY release the root and hand control back. Guarding this on _localDownedApplied left anyone
            // who quit while their prop was LOCKED with a disabled CharacterController for the rest of the game session -
            // the lock is a second, independent reason the root is frozen, and teardown has to clear both.
            try
            {
                bool wasHeld = _localDownedApplied || LocalPropLocked;
                _localDownedApplied = false;
                LocalPropLocked = false;
                ApplyRootFreeze();
                if (wasHeld) SetLocalControl(true);
            }
            catch { }
            try { PropHunt.View.BodyCam.Stop(); } catch { }                 // restore first person if we tore down mid-ragdoll body-cam
            try { PropHunt.View.EyeBlink.ResetState(); } catch { }          // clear any blink/blindfold static state + ensure the eyes are open
            try { Quests.GuideQuest.Stop(); } catch { }   // remove the local guidance quest on session teardown
            // A pool belongs to the host we were playing with; carrying it into the next session would silently
            // shrink what we may become there.
            try { PropCatalog.ClearHostPool(); } catch { }
            _disguise = null;
            _decoy = null;
            _picker = null;
            _highlighter = null;
            _thirdPerson = null;
            _catch = null;
            _passthrough = null;
            _playArea = null;
            _border = null;
            _taunt = null;
            _propRotation = null;
            _mapRing = null;
            _tauntWheel = null;
            _onboarding = null;
            _spectator = null;
            try { _stateVar?.Dispose(); } catch { }
            _stateVar = null;
            Core.Log.Msg("[PropHunt] session disposed.");
        }

        // ---- networking plumbing ----

        private void EnsureStateVar()
        {
            if (_stateVar != null) return;
            if (!PropHuntNet.Ready || PropHuntNet.Client == null) return;
            try
            {
                _stateVar = PropHuntNet.Client.CreateHostSyncVar<string>(NetKeys.State, "");
                _stateVar.OnValueChanged += OnStateVarChanged;
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] CreateHostSyncVar failed: " + e.Message); }
        }

        private void OnStateVarChanged(string oldV, string newV)
        {
            if (_isHost) return;   // host already holds the authoritative _state; ignore its own echo
            try { ApplyStateString(newV); } catch (Exception e) { Core.Log.Warning("[PropHunt] state apply failed: " + e.Message); }
        }

        private void ApplyStateString(string blob)
        {
            _state = GameState.Parse(blob);
            SyncClock(_state);   // before any effect reads a timer: every timestamp below is in host time
            if (!string.IsNullOrEmpty(_state.SettingsBlob)) _settings = RoundSettings.Parse(_state.SettingsBlob);
            Core.LogDebug($"[PropHunt] client recv state: phase={_state.Phase} hash={_state.CatalogHash} players={_state.Players.Count} - applying effects...");
            // NOTE: do NOT scan/lock doors here. ApplyStateString runs in the SteamNetworkLib state-var callback,
            // which fires once PER host push (several in quick succession when entering the safehouse). A
            // FindObjectsOfType<DoorController> city scan per push froze the client. Door locking (incl. late-join)
            // is handled once-per-code-change in the Tick-driven ApplySafehousePresence instead.
            ApplyLocalEffects();
            Core.LogDebug("[PropHunt] client recv state: effects applied.");
        }

        private void PushState()
        {
            if (!_isHost || _stateVar == null) return;
            _state.HostNowUnix = RawNowUnix();   // clients derive their clock offset from this
            try { _stateVar.Value = _state.Serialize(); } catch (Exception e) { Core.Log.Warning("[PropHunt] PushState failed: " + e.Message); }
        }

        private static void EnsureHandlers()
        {
            if (_handlersRegistered) return;
            var c = PropHuntNet.Client;
            if (c == null) return;
            try
            {
                c.RegisterMessageHandler<SelectPropMessage>((m, s) => Active?.HandleSelectProp(s.m_SteamID, m.PropId));
                c.RegisterMessageHandler<LockPropMessage>((m, s) => Active?.HandleLock(s.m_SteamID, m.Locked));
                c.RegisterMessageHandler<RotatePropMessage>((m, s) => Active?.HandleRotate(s.m_SteamID, m.Yaw));
                c.RegisterMessageHandler<DropDecoyMessage>((m, s) => Active?.HandleDropDecoy(s.m_SteamID, m.X, m.Y, m.Z, m.Yaw));
                c.RegisterMessageHandler<ConcussMessage>((m, s) => Active?.HandleConcuss(s.m_SteamID, m.X, m.Y, m.Z));
                c.RegisterMessageHandler<ClaimTagMessage>((m, s) => Active?.HandleClaimTag(s.m_SteamID, m.VictimSteamId, new Vector3(m.DirX, m.DirY, m.DirZ)));
                c.RegisterMessageHandler<ProbePropMessage>((m, s) => Active?.HandleProbeProp(s.m_SteamID, m.VictimSteamId));
                c.RegisterMessageHandler<HitHunterMessage>((m, s) => Active?.HandleHitHunter(s.m_SteamID, m.VictimSteamId, new Vector3(m.DirX, m.DirY, m.DirZ)));
                c.RegisterMessageHandler<OutOfBoundsMessage>((m, s) => Active?.HandleOutOfBounds(s.m_SteamID, m.Water));
                c.RegisterMessageHandler<TauntMessage>((m, s) => Active?.NotifyTaunt(m.SteamId, m.Sound, m.IsWhistle));
                c.RegisterMessageHandler<ManualTauntMessage>((m, s) => Active?.HandleManualTaunt(s.m_SteamID, m.Sound));
                c.RegisterMessageHandler<DecoyHitMessage>((m, s) => Active?.HandleDecoyHit(s.m_SteamID, m.DecoyIndex));
                c.RegisterMessageHandler<CatchFxMessage>((m, s) => Active?.NotifyCatchFx(m.HunterId, m.VictimId, m.Caught, new Vector3(m.X, m.Y, m.Z)));
                c.RegisterMessageHandler<StunFxMessage>((m, s) => Active?.NotifyStunFx(m.ThrowerId, new Vector3(m.X, m.Y, m.Z)));
                c.RegisterMessageHandler<DecoyFxMessage>((m, s) => Active?.NotifyDecoyFx(m.HunterId, new Vector3(m.X, m.Y, m.Z)));
                c.RegisterMessageHandler<SafehouseDoorLockMessage>((m, s) => Active?.HandleSafehouseDoorLock(m.PropertyCode, m.Locked));
                c.RegisterMessageHandler<PropPoolMessage>((m, s) => Active?.HandlePropPool(m.Ids));
                c.RegisterMessageHandler<PropRotationMessage>((m, s) => Active?.NotifyRotation());
                _handlersRegistered = true;
                Core.LogDebug("[PropHunt] P2P handlers registered.");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] handler registration failed: " + e.Message); }
        }

        private void SendToHost(P2PMessage msg)
        {
            try
            {
                var host = PropHuntNet.Client?.GetHostMember();
                if (host != null) _ = PropHuntNet.Client.SendMessageToPlayerAsync(host.SteamId, msg);
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] SendToHost failed: " + e.Message); }
        }

        // ---- intent request hooks (sub-controllers call these) ----

        internal void RequestSelectProp(int propId)
        {
            if (_isHost) HandleSelectProp(LocalId, propId);
            else SendToHost(new SelectPropMessage { PropId = propId });
        }

        /// <summary>[2]: become a random catalog prop (different from the current one, so it visibly changes).</summary>
        internal void RequestSelectRandomProp()
        {
            int id = PropHunt.Disguise.PropCatalog.RandomId(LocalPropId);
            if (id >= 0) RequestSelectProp(id);
        }

        /// <summary>Apply the local prop facing immediately (no network) - used each frame while rotating for smoothness.</summary>
        internal void SetLocalYaw(float yaw) => _localYaw = yaw;

        /// <summary>[F]+mouse: set the prop's manual facing (applied locally now, synced via the host - throttled by the caller).</summary>
        internal void RequestRotate(float yaw)
        {
            _localYaw = yaw;
            if (_isHost) HandleRotate(LocalId, yaw);
            else SendToHost(new RotatePropMessage { Yaw = yaw });
        }

        /// <summary>[Q]: drop a decoy of the current prop at the local player's spot (client-authoritative position).</summary>
        internal void RequestDropDecoy()
        {
            var lp = Player.Local;
            if (lp == null) return;
            var pos = lp.transform.position;
            float y = RoundEnvironment.FeetY(lp);
            if (_isHost) HandleDropDecoy(LocalId, pos.x, y, pos.z, _localYaw);
            else SendToHost(new DropDecoyMessage { X = pos.x, Y = y, Z = pos.z, Yaw = _localYaw });
        }

        /// <summary>[G]: set off a concussion grenade at the local player's position (stuns nearby hunters).</summary>
        internal void RequestConcuss()
        {
            var lp = Player.Local;
            if (lp == null) return;
            var pos = lp.transform.position;
            if (_isHost) HandleConcuss(LocalId, pos.x, pos.y, pos.z);
            else SendToHost(new ConcussMessage { X = pos.x, Y = pos.y, Z = pos.z });
        }

        internal void RequestLock(bool locked)
        {
            if (_isHost) HandleLock(LocalId, locked);
            else SendToHost(new LockPropMessage { Locked = locked });
        }

        internal void RequestClaimTag(ulong victimSteamId, Vector3 aimDir)
        {
            if (_isHost) HandleClaimTag(LocalId, victimSteamId, aimDir);
            else SendToHost(new ClaimTagMessage { VictimSteamId = victimSteamId, DirX = aimDir.x, DirY = aimDir.y, DirZ = aimDir.z });
        }

        /// <summary>A hunter's shot landed on another HUNTER (friendly fire). The host validates + knocks them down.</summary>
        internal void RequestHitHunter(ulong victimSteamId, Vector3 aimDir)
        {
            if (_isHost) HandleHitHunter(LocalId, victimSteamId, aimDir);
            else SendToHost(new HitHunterMessage { VictimSteamId = victimSteamId, DirX = aimDir.x, DirY = aimDir.y, DirZ = aimDir.z });
        }

        private int _lastShotFrame = -1;
        private bool _localDownedApplied;               // edge-detect our OWN Downed flag (drives camera + control lock)
        private readonly HashSet<ulong> _ragdolled = new HashSet<ulong>();   // who we currently show limp, so each edge fires once
        // Scratch buffers, reused every tick so the per-frame drive allocates nothing.
        private readonly List<ulong> _pendingRagdoll = new List<ulong>();
        private readonly List<ulong> _pendingStandUp = new List<ulong>();
        private readonly List<ulong> _stale = new List<ulong>();

        private const float KnockImpulse = 30f;         // matches the impulse vanilla puts on a falling body

        /// <summary>Ragdoll every player whose synced Downed flag says so, and stand the rest back up.
        ///
        /// The host owns the Downed state (friendly-fire KO or concussion) and syncs it, together with the knockback
        /// direction, to every client - so replication is already done and each client simply mirrors the flags onto the
        /// bodies it can see. That is why this needs no RPC: 0.4.6f11 deleted the whole pass-out system
        /// (<c>Player.PassOut</c>, <c>SendPassOut</c>, <c>PassOutScreen</c>, the energy meter), and the networked limp
        /// went with it. What survived is the death path's own recipe - <c>Player.SetRagdolled</c> plus a spine impulse -
        /// which is local, cheap, and also fixes the fall direction natively: vanilla always toppled a body forward, so
        /// we used to need a second patch to cancel that. Here the impulse simply IS the knockback direction.</summary>
        private void DriveRagdolls()
        {
            try
            {
                // Collect the edges first, act after. Ragdoll/StandUp touch the avatar and can spawn or despawn
                // objects, which must not happen while we are still walking the state dictionary.
                foreach (var kv in _state.Players)
                {
                    ulong id = kv.Key;
                    if (kv.Value.Downed == _ragdolled.Contains(id)) continue;   // no edge for this player
                    (kv.Value.Downed ? _pendingRagdoll : _pendingStandUp).Add(id);
                }

                foreach (ulong id in _pendingRagdoll)
                {
                    var pl = PlayerRegistry.Get(id);
                    if (pl == null || !_state.Players.TryGetValue(id, out var st)) continue;   // not spawned here yet - retry next tick
                    if (Ragdoll(pl, st.KnockX, st.KnockZ)) _ragdolled.Add(id);                 // only remember what actually took
                }
                foreach (ulong id in _pendingStandUp)
                {
                    var pl = PlayerRegistry.Get(id);
                    if (pl == null) continue;      // body out of reach - stay marked and stand it up when it returns
                    if (StandUp(pl)) _ragdolled.Remove(id);
                }
                _pendingRagdoll.Clear();
                _pendingStandUp.Clear();

                // A player who left the round while limp: stand the body back up if it is still here, so a disconnect
                // (or a state reset) cannot leave a corpse lying in the world for everyone else.
                if (_ragdolled.Count > 0)
                {
                    _stale.Clear();
                    foreach (ulong id in _ragdolled) if (!_state.Players.ContainsKey(id)) _stale.Add(id);
                    foreach (ulong id in _stale)
                    {
                        var pl = PlayerRegistry.Get(id);
                        // Keep the id until the body is either upright again or genuinely gone - dropping it while a
                        // limp body is still in the world would strand that body with nothing left to stand it up.
                        if (pl == null || StandUp(pl)) _ragdolled.Remove(id);
                    }
                }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] ragdoll drive failed: " + e.Message); }

            DriveLocalDownedView();
        }

        /// <summary>Drop a player limp and shove the body in the synced knockback direction (away from the attacker).
        /// Mirrors what vanilla does when a player dies (Player.OnDied): SetRagdolled + a spine impulse + a little
        /// random torque so the tumble does not look mechanical. A zero direction falls straight down.
        ///
        /// Returns false when the limp did not take. The caller only records the player as ragdolled on a true, so a
        /// transient failure is retried next tick instead of being remembered as done.</summary>
        private static bool Ragdoll(Player pl, float kx, float kz)
        {
            try
            {
                pl.SetRagdolled(true);
                var rb = pl.Avatar?.MiddleSpineRB;
                if (rb == null) return true;   // limp applied; only the shove is missing
                var dir = new Vector3(kx, 0f, kz);
                if (dir.sqrMagnitude > 0.0001f) rb.AddForce(dir.normalized * KnockImpulse, ForceMode.VelocityChange);
                rb.AddRelativeTorque(new Vector3(0f, UnityEngine.Random.Range(-1f, 1f), UnityEngine.Random.Range(-1f, 1f)) * 10f, ForceMode.VelocityChange);
                return true;
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] ragdoll failed: " + e.Message); return false; }
        }

        /// <summary>Stand a body back up. Returns false when it did not take, so the caller keeps the player marked
        /// and tries again rather than leaving them limp forever.</summary>
        private static bool StandUp(Player pl)
        {
            try { pl.SetRagdolled(false); return true; }
            catch (Exception e) { Core.LogDebug("[PropHunt] stand-up failed: " + e.Message); return false; }
        }

        /// <summary>The half of a knockdown that only applies to OUR player: freeze the character controller so it
        /// cannot drag the limp body around, take control away, and pull the camera out so you can see yourself drop.
        ///
        /// The edge is only latched once the local player actually exists. A knockdown can be synced to us while the
        /// scene is still coming up, and every effect below silently no-ops without a local player - latching first
        /// would swallow the edge for good and leave us running around while the body lies limp.</summary>
        private void DriveLocalDownedView()
        {
            bool downed = LocalDowned;
            if (downed == _localDownedApplied) return;
            if (Player.Local == null) return;   // not ready - keep the edge pending and try again next tick

            _localDownedApplied = downed;
            try
            {
                if (downed)
                {
                    SetLocalControl(false);
                    PropHunt.View.BodyCam.Start();   // third person, so you watch your own body drop and know you're down
                    ApplyRootFreeze();               // our CharacterController is still active and would drag the ragdoll forward
                    SetFx("KNOCKED DOWN", new Color(0.95f, 0.55f, 0.2f));
                }
                else
                {
                    ApplyRootFreeze();
                    SetLocalControl(true);
                    PropHunt.View.BodyCam.Stop();    // ease back to first person now the body is upright
                }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] downed view failed: " + e.Message); }
        }

        /// <summary>Take local control away and hand it back. This used to be <c>Player.Deactivate</c>/<c>Activate</c>,
        /// which 0.4.6f11 removed; these are the same four levers those static helpers pulled, so a knockdown still
        /// locks look, movement, inventory and the crosshair exactly as before. The mouse is deliberately left locked
        /// (we never free it) so the knockdown does not pop a cursor into the middle of a round.</summary>
        private static void SetLocalControl(bool enabled)
        {
            try { PlayerSingleton<PlayerCamera>.Instance?.SetCanLook(enabled); } catch { }
            try { var m = PlayerSingleton<PlayerMovement>.Instance; if (m != null) m.CanMove = enabled; } catch { }
            try { PlayerSingleton<PlayerInventory>.Instance?.SetInventoryEnabled(enabled); } catch { }
            try { Singleton<HUD>.Instance?.SetCrosshairVisible(enabled); } catch { }

            // Hand equipping back explicitly, because SetInventoryEnabled is ASYMMETRIC: it clears EquippingEnabled on
            // the way down (PlayerInventory.SetInventoryEnabled -> if (!enabled) SetEquippingEnabled(false)) and never
            // restores it on the way up. UpdateHotbarSelection needs both, so a hunter who had been stunned stood back
            // up unable to select any weapon for the rest of the round. Vanilla only ever gets away with this because a
            // scene-state change re-applies equipping, and nothing changes scene state mid-round.
            //
            // Not blindly true: a disguised hider must stay without a hotbar, which is the state this same flag enforces.
            if (!enabled) return;
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                bool want = !Patches.HotbarSuppression.Disguised;
                if (inv != null && inv.EquippingEnabled != want) inv.SetEquippingEnabled(want);
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] restore equipping failed: " + e.Message); }
        }

        /// <summary>The local hunter FIRED a real, ammo/aim/cooldown-gated shot (driven by the weapon-fire Harmony
        /// postfix, not by raw input). Resolve it into a decoy/prop hit using the weapon's reach. Guarded to ONE
        /// resolve per frame: a single shot is one frame, and the Fire postfix can run more than once per shot (e.g.
        /// the gameplay patches get applied twice across the scratch-world boot), which would otherwise double the hit.</summary>
        internal void OnLocalHunterFired(float maxRange)
        {
            if (Time.frameCount == _lastShotFrame) return;
            _lastShotFrame = Time.frameCount;
            try { _catch?.ResolveShot(maxRange); } catch { }
        }

        /// <summary>
        /// The GAME's own bullet resolved onto a player. This is the primary catch path: the shot was aimed, cast,
        /// spread and sorted by the game itself, against the same colliders the shooter can see, so a hunter who put
        /// their crosshair on a prop and pulled the trigger gets the hit the game already agreed to.
        ///
        /// It runs on the shooter's client from the impact-FX call, BEFORE the weapon-fire postfix does our own
        /// sweep - and it shares the one-resolve-per-frame guard, so the two never both count the same shot. The
        /// sweep is still needed for decoys, which are not players and which the game cannot attribute to anyone.
        /// </summary>
        internal void OnVanillaBulletHitPlayer(Player victim, Vector3 hitPoint)
        {
            if (victim == null || _state.Phase != RoundPhase.Hunting) return;
            if (Time.frameCount == _lastShotFrame) return;

            ulong victimId = PlayerRegistry.IdForPlayer(victim);
            if (victimId == 0 || victimId == LocalId) return;
            if (!_state.Players.TryGetValue(victimId, out var vp) || vp.Eliminated) return;

            _lastShotFrame = Time.frameCount;
            var aim = Vector3.forward;
            try { var cam = PlayerSingleton<PlayerCamera>.Instance; if (cam != null && cam.Camera != null) aim = cam.Camera.transform.forward; } catch { }

            if (vp.Role == PlayerRole.Hunter)
            {
                if (_settings == null || !_settings.FriendlyFire) return;   // teammates are not targets
                PropHunt.UI.Hud.HudController.ShowHitmarker();
                RequestHitHunter(victimId, aim);
                Core.LogDebug($"[PropHunt] vanilla bullet -> friendly fire on {victimId} at {hitPoint}");
                return;
            }

            PropHunt.UI.Hud.HudController.ShowHitmarker();
            RequestClaimTag(victimId, aim);
            Core.LogDebug($"[PropHunt] vanilla bullet -> claim tag on {victimId} at {hitPoint} (prop {PropIdOf(victimId)})");
        }

        /// <summary>Host: apply a single setting edit from the phone Settings tab + flag it for re-publish so clients
        /// see the change live (Tick pushes the new SettingsBlob, throttled). No-op for non-hosts (clients can't edit).</summary>
        internal void SetSetting(string key, string value)
        {
            if (!_isHost) return;
            _settings.ApplyKeyValue(key, value);
            _settingsDirty = true;
        }

        /// <summary><paramref name="water"/> distinguishes "standing in deep water" from "outside the area radius":
        /// the host re-checks the radius before accepting an area report, and a drowning player is normally inside it.</summary>
        internal void ReportOutOfBounds(bool water = false)
        {
            if (_isHost) HandleOutOfBounds(LocalId, water);
            else SendToHost(new OutOfBoundsMessage { Water = water });
        }

        /// <summary>Host: remove a player from the session by their Steam id, via the Side Hustle framework helper
        /// (host-authoritative FishNet kick). No-op for non-hosts and never kicks the host itself.</summary>
        internal void KickPlayer(ulong steamId)
        {
            if (!_isHost || steamId == 0 || steamId == LocalId) return;
            try { SideHustle.API.KickPlayer(steamId, "Kicked by host"); }
            catch (Exception e) { Core.LogDebug("[PropHunt] kick failed: " + e.Message); }
        }

        // host-only: last unix time each hider was AWARDED a taunt point, to cap taunt scoring at once per 15s
        // (the taunt sound/cue still plays every time - only the score is rate-limited).
        private readonly System.Collections.Generic.Dictionary<ulong, long> _lastTauntScoreUnix = new System.Collections.Generic.Dictionary<ulong, long>();

        /// <summary>Local player asks to taunt ([1]) with a chosen sound; the host broadcasts the reveal cue.</summary>
        /// <summary>A hunter poked a disguised hider with the trash grabber: make that hider whistle.</summary>
        internal void RequestProbeProp(ulong victimSteamId)
        {
            if (victimSteamId == 0) return;
            if (_isHost) HandleProbeProp(LocalId, victimSteamId);
            else SendToHost(new ProbePropMessage { VictimSteamId = victimSteamId });
        }

        /// <summary>
        /// Host: a hunter says they grabbed at a prop. Re-validated here rather than trusted, because a client that
        /// simply names a steam id could otherwise make anyone give themselves away on demand.
        ///
        /// A hunter who keeps grabbing at a prop within arm's length is MEANT to keep making it whistle - a hider who has
        /// been found should run, not stand there. The only limit is one whistle per clip length, so pressing faster
        /// than the sound plays cannot layer it into noise.
        /// </summary>
        private void HandleProbeProp(ulong hunter, ulong victim)
        {
            if (!_isHost) return;
            if (RoleOf(hunter) != PlayerRole.Hunter) return;
            if (_state.Phase != RoundPhase.Hunting) return;
            if (!_state.Players.TryGetValue(victim, out var vs)) return;
            if (vs.Role != PlayerRole.Hider || vs.Eliminated || vs.PropId < 0) return;

            // DISTANCE, checked here rather than trusted. The client only reports "I grabbed at this id", and a modified
            // one could name every hider in turn and make the whole lobby give itself away from across the map. The host
            // has both replicated positions, so this costs nothing and is the difference between a claim and a fact.
            try
            {
                var hp = PlayerRegistry.Get(hunter);
                var vp = PlayerRegistry.Get(victim);
                if (hp == null || vp == null) return;   // cannot verify -> do not act
                if (Vector3.Distance(hp.transform.position, vp.transform.position) > ProbeMaxDistance) return;
            }
            catch { return; }

            float now = Time.unscaledTime;
            // Per VICTIM so one whistle answers one grab, and per HUNTER so naming a different victim each time cannot
            // be used to walk around the first limit.
            if (_lastProbe.TryGetValue(victim, out var last) && now - last < ProbeWhistleCooldown) return;
            if (_lastProbeBy.TryGetValue(hunter, out var lastBy) && now - lastBy < ProbeWhistleCooldown) return;
            _lastProbe[victim] = now;
            _lastProbeBy[hunter] = now;

            string sound = Taunt.TauntSounds.PickDefault();   // the same clip the timed whistle uses
            try { PropHuntNet.Client?.BroadcastMessage(new TauntMessage { SteamId = victim, Sound = sound, IsWhistle = true }); } catch { }
            NotifyTaunt(victim, sound, isWhistle: true);   // the host hears it too (Broadcast does not self-send)
            Core.LogDebug($"[PropHunt] {hunter} grabbed at {victim}'s prop - forced a whistle.");
        }

        /// <summary>Roughly one whistle clip. Not a cooldown on being found - a hunter may keep the siren going as long
        /// as they stay in reach - just enough that two presses in the same breath do not play over each other.</summary>
        private const float ProbeWhistleCooldown = 0.6f;

        /// <summary>Generous against the grabber's own 4m reach - the two players' ROOTS are being compared while the
        /// grab was aimed at a prop that stands beside its wearer, so a hard 4m would reject honest grabs.</summary>
        private const float ProbeMaxDistance = 6f;

        private readonly Dictionary<ulong, float> _lastProbe = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, float> _lastProbeBy = new Dictionary<ulong, float>();

        internal void RequestManualTaunt(string sound)
        {
            if (_isHost) HandleManualTaunt(LocalId, sound);
            else SendToHost(new ManualTauntMessage { Sound = sound });
        }

        /// <summary>Host: a player manually taunted -> broadcast the reveal cue + sound to everyone (incl. self).</summary>
        private void HandleManualTaunt(ulong sender, string sound)
        {
            if (!_isHost) return;
            if (sender == 0 || !_state.Players.TryGetValue(sender, out var p) || p.Eliminated) return;
            // Resolve a default clip on the host so every machine plays the SAME sound. An empty sound would
            // otherwise make each receiver pick its own random default; the whistle path already resolves on host.
            if (string.IsNullOrEmpty(sound)) sound = Taunt.TauntSounds.PickDefault();
            try { PropHuntNet.Client?.BroadcastMessage(new TauntMessage { SteamId = sender, Sound = sound }); } catch { }
            NotifyTaunt(sender, sound);   // host also hears it (BroadcastMessage doesn't self-send)

            // Score the taunt for a live hider during a round, capped to once per 15s (RoundScore * 2). The taunt
            // itself always plays above; only the point is rate-limited so spamming [1] doesn't farm score.
            if (p.Role == PlayerRole.Hider && (_state.Phase == RoundPhase.Hiding || _state.Phase == RoundPhase.Hunting))
            {
                long now = NowUnix();
                if (!_lastTauntScoreUnix.TryGetValue(sender, out var last) || now - last >= 15)
                {
                    _lastTauntScoreUnix[sender] = now;
                    p.Taunts++;
                    PushState();   // sync the stat (rate-limited -> infrequent)
                }
            }
        }

        // ---- host-authoritative handlers (validate I/O, delegate the decision to RoundLogic) ----

        private void HandleSelectProp(ulong sender, int propId)
        {
            if (!_isHost) return;

            // In the lobby this is the dressing room, not a round move: there is no roster to write to and no change
            // budget to spend, so it goes to its own field and everybody sees it. Rejecting it here (which is what the
            // roster path does, correctly, for a player with no role) is why trying props on was invisible to others.
            if (_state.Phase == RoundPhase.Lobby)
            {
                var worn = Disguise.LobbyPropCodec.Parse(_state.LobbyProps);
                if (propId < 0) worn.Remove(sender);
                else
                {
                    if (PropCatalog.ById(propId) == null)
                    {
                        Core.Log.Warning($"[PropHunt] host: rejected lobby prop {propId} from {sender} - not in the host catalog.");
                        BroadcastPropPool(force: true);
                        return;
                    }
                    worn.TryGetValue(sender, out var prev);
                    worn[sender] = new Disguise.LobbyPropCodec.Worn { PropId = propId, Yaw = prev.Yaw };
                }
                _state.LobbyProps = Disguise.LobbyPropCodec.Serialize(worn);
                PushState();
                return;
            }

            // Last line of defence: a prop we cannot draw would leave the hider looking like a player to everyone
            // watching through us. Clients are already gated on the published pool, so this only catches a stale one.
            if (propId >= 0 && PropCatalog.ById(propId) == null)
            {
                Core.Log.Warning($"[PropHunt] host: rejected prop {propId} from {sender} - not in the host catalog.");
                BroadcastPropPool(force: true);   // their pool is out of date; hand them the current one
                return;
            }
            int maxHits = ComputeMaxHits(propId);
            bool freeChange = _settings.FreeChangesInHiding && _state.Phase == RoundPhase.Hiding;
            bool ok = RoundLogic.ApplySelectProp(_state, sender, propId, maxHits, _settings.MaxPropChanges, freeChange);
            _state.Players.TryGetValue(sender, out var sp);
            Core.LogDebug($"[PropHunt] host: select from {sender} prop {propId} hp {maxHits} -> {(ok ? "ACCEPTED" : "rejected")}" +
                          (sp != null ? $" (role={sp.Role} elim={sp.Eliminated} changes={sp.Changes}/{_settings.MaxPropChanges})" : " (sender NOT in roster)"));
            if (ok) PushState();
        }

        /// <summary>Prop HP for a catalog id, for host-side code outside this class (the forced prop rotation).</summary>
        internal int MaxHitsFor(int propId) => ComputeMaxHits(propId);

        /// <summary>Push the current host state to every client. Exposed for host-side sub-controllers that mutate
        /// the state themselves (the prop rotation).</summary>
        internal void PublishState() => PushState();

        /// <summary>Tell everyone their prop just changed under them, so a sudden new shape reads as the rotation
        /// setting rather than a glitch. Hiders see it as a fresh disguise; hunters as a cue that every prop moved.</summary>
        internal void AnnounceRotation()
        {
            try { PropHuntNet.Client?.BroadcastMessage(new PropRotationMessage()); } catch { }
            NotifyRotation();   // the host does not receive its own broadcast
        }

        /// <summary>Local cue for a forced prop rotation.</summary>
        internal void NotifyRotation() => SetFx("PROPS ROTATED", new Color(0.55f, 0.85f, 1f));

        /// <summary>Size-based prop HP: bigger props take many more hits to catch (round(maxDim * HitsPerMetre), clamped).</summary>
        private int ComputeMaxHits(int propId)
        {
            float maxDim = PropHunt.Disguise.PropCatalog.SizeOf(propId);
            int hp = UnityEngine.Mathf.RoundToInt(maxDim * UnityEngine.Mathf.Max(1, _settings.HitsToCatch));
            return UnityEngine.Mathf.Clamp(hp, 1, UnityEngine.Mathf.Max(1, _settings.HiderMaxHp));   // size-scaled HP, capped at the host's Max hider HP
        }

        private void HandleLock(ulong sender, bool locked)
        {
            if (!_isHost) return;
            if (RoundLogic.ApplyLock(_state, sender, locked)) PushState();
        }

        private void HandleRotate(ulong sender, float yaw)
        {
            if (!_isHost) return;
            if (_state.Phase == RoundPhase.Lobby)
            {
                // Turning a prop in the dressing room has to reach the others too, or a player lining their crate up
                // against a wall is the only one who sees it happen.
                var worn = Disguise.LobbyPropCodec.Parse(_state.LobbyProps);
                if (!worn.TryGetValue(sender, out var w)) return;
                w.Yaw = yaw;
                worn[sender] = w;
                _state.LobbyProps = Disguise.LobbyPropCodec.Serialize(worn);
                PushState();
                return;
            }
            if (RoundLogic.ApplyRotate(_state, sender, yaw)) PushState();
        }

        private void HandleDropDecoy(ulong sender, float x, float y, float z, float yaw)
        {
            if (!_isHost) return;
            // compute the same size-based HP the hider's own prop would have, so the decoy has identical durability
            _state.Players.TryGetValue(sender, out var sp);
            int maxHits = sp != null ? ComputeMaxHits(sp.PropId) : 1;
            if (RoundLogic.ApplyDropDecoy(_state, _settings, sender, x, y, z, yaw, maxHits))
            {
                Core.Log.Msg($"[PropHunt] {sender} dropped a decoy (hp={maxHits}, {_state.Decoys.Count} total).");
                PushState();
            }
            else
            {
                Core.Log.Msg($"[PropHunt] decoy from {sender} rejected (phase={_state.Phase}, used={(sp != null ? sp.DecoysUsed : -1)}/{_settings.MaxDecoys}, propId={(sp != null ? sp.PropId : -99)})");
            }
        }

        private void HandleConcuss(ulong sender, float x, float y, float z)
        {
            if (!_isHost) return;
            if (RoundLogic.ApplyConcuss(_state, _settings, sender))
            {
                var center = new Vector3(x, y, z);
                ApplyConcussionEffect(center, sender);
                BroadcastFx(new StunFxMessage { ThrowerId = sender, X = x, Y = y, Z = z });
                NotifyStunFx(sender, center);
                PushState();
            }
            else
            {
                _state.Players.TryGetValue(sender, out var cp);
                Core.Log.Msg($"[PropHunt] concussion from {sender} rejected (phase={_state.Phase}, used={(cp != null ? cp.ConcussUsed : -1)}/{_settings.ConcussCharges})");
            }
        }

        /// <summary>Host: knock down every hunter within the concussion radius of the given centre for a short stun.
        /// Uses the SAME ragdoll/Downed state as friendly fire (a stun is just a brief knockdown), so nearby hunters
        /// ragdoll via the synced Downed flag instead of the old fixed 2s taze. Credits the throwing hider's StunsLanded.</summary>
        private void ApplyConcussionEffect(UnityEngine.Vector3 center, ulong hiderId)
        {
            try
            {
                PlayerRegistry.Refresh();
                float r = _settings.ConcussRadius;
                int seconds = Math.Max(1, (int)Math.Round(_settings.ConcussStunSeconds));
                long now = NowUnix();
                int hit = 0;
                var list = Player.PlayerList;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var pl = list[i];
                    if (pl == null) continue;
                    ulong hid = PlayerRegistry.IdForPlayer(pl);
                    if (RoleOf(hid) != PlayerRole.Hunter) continue;
                    if (UnityEngine.Vector3.Distance(center, pl.transform.position) > r) continue;
                    if (RoundLogic.ApplyConcussDown(_state, hid, seconds, now)) { SetKnockback(hid, pl.transform.position - center); hit++; }   // synced Downed -> ragdoll AWAY from the blast on that hunter's client
                }
                if (hit > 0 && _state.Players.TryGetValue(hiderId, out var hs)) hs.StunsLanded += hit;   // credit the hider (synced by HandleConcuss' PushState)
                Core.Log.Msg($"[PropHunt] concussion by {hiderId} - knocked down {hit} hunter(s) within {r}m for {seconds}s.");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] concussion effect failed: " + e.Message); }
        }

        private void HandleClaimTag(ulong hunter, ulong victim, Vector3 aimDir)
        {
            if (!_isHost || _state.Phase != RoundPhase.Hunting) return;
            // TRUST THE CLIENT'S HIT. The client only sends this claim after its OWN SphereCast actually struck the
            // victim's prop hitbox / capsule (that is exactly what shows the hitmarker), so the client already did the
            // authoritative aim-hit detection. The host previously RE-derived the geometry from the victim's CAPSULE
            // position vs the aim and rejected on a lateral cone - but the disguise prop renders OFFSET from the capsule,
            // so at close range (standing on the prop) and with aim/body divergence at long range that cone
            // false-rejected clearly-landed shots (hitmarker shown, but no damage). We drop the geometry gate and just
            // apply the hit: RoundLogic.ApplyCatch still enforces phase==Hunting + that the victim is a live hider.
            // Casual co-op, so trusting the client's ray is the right trade-off; aimDir stays in the message for a
            // possible future host-side raycast check but no longer gates the hit.
            PlayerRegistry.Refresh();
            var hp = PlayerRegistry.Get(hunter);
            var vp = PlayerRegistry.Get(victim);
            if (RoundLogic.ApplyCatch(_state, _settings, hunter, victim, NowUnix()))
            {
                bool caught = RoundLogic.IsCaught(_state, victim);
                if (caught) Core.Log.Msg($"[PropHunt] {hunter} CAUGHT {victim} ({_settings.Caught}).");
                else Core.Log.Msg($"[PropHunt] {hunter} hit {victim} ({_state.Players[victim].Hits}/{_state.Players[victim].MaxHits}).");
                var vpos = vp != null ? vp.transform.position : (hp != null ? hp.transform.position : Vector3.zero);
                // Blood spurt on the hit hider - the gun no longer damages players (immunity), so the vanilla
                // death-blood is gone; play the blood mist here as pure "you hit a hider" feedback. Host-side +
                // ObserversRpc -> replicates to everyone, at the (hidden) avatar = the prop position. No damage.
                try { vp?.Health?.PlayBloodMist(); } catch { }
                BroadcastFx(new CatchFxMessage { HunterId = hunter, VictimId = victim, Caught = caught, X = vpos.x, Y = vpos.y, Z = vpos.z });
                NotifyCatchFx(hunter, victim, caught, vpos);
                PushState();
            }
        }

        /// <summary>Host: apply a friendly-fire hit from one hunter on another. Trusts the client's resolved hit (same
        /// reasoning as <see cref="HandleClaimTag"/> - no host geometry re-validation, which false-rejected landed
        /// shots). <see cref="RoundLogic.ApplyHitHunter"/> still gates FriendlyFire + that both are live hunters. Plays
        /// blood on every accepted hit and, on the knockdown hit, a stun cue; the ragdoll is driven on the victim's own
        /// client from the synced Downed flag (see <see cref="DriveLocalRagdoll"/>).</summary>
        private void HandleHitHunter(ulong shooter, ulong victim, Vector3 aimDir)
        {
            if (!_isHost || _state.Phase != RoundPhase.Hunting || !_settings.FriendlyFire) return;
            PlayerRegistry.Refresh();
            var hp = PlayerRegistry.Get(shooter);
            var vp = PlayerRegistry.Get(victim);
            if (RoundLogic.ApplyHitHunter(_state, _settings, shooter, victim, NowUnix(), out bool newlyDowned))
            {
                var vpos = vp != null ? vp.transform.position : (hp != null ? hp.transform.position : Vector3.zero);
                // blood spurt on the hit hunter (pure feedback; the gun does no real damage - see DisableVanillaPlayerDeath).
                try { vp?.Health?.PlayBloodMist(); } catch { }
                if (newlyDowned)
                {
                    if (hp != null && vp != null) SetKnockback(victim, vp.transform.position - hp.transform.position);   // ragdoll away from the shooter
                    // reuse the concussion "stun" cue (sound + STUNNED flash for nearby hunters); the ragdoll is driven
                    // on the victim's own client off the synced Downed flag.
                    BroadcastFx(new StunFxMessage { ThrowerId = shooter, X = vpos.x, Y = vpos.y, Z = vpos.z });
                    NotifyStunFx(shooter, vpos);
                    Core.Log.Msg($"[PropHunt] {shooter} knocked down hunter {victim} (friendly fire).");
                }
                PushState();
            }
        }

        /// <summary>Set the synced horizontal knockback direction for a player's ragdoll (away from the attacker), read
        /// by <see cref="DriveRagdolls"/> on every client so the body falls in the hit direction.
        /// <paramref name="dir"/> is a world vector attacker-&gt;victim (y ignored).</summary>
        internal void SetKnockback(ulong id, Vector3 dir)
        {
            if (!_state.Players.TryGetValue(id, out var p)) return;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f) { p.KnockX = 0f; p.KnockZ = 0f; return; }
            dir.Normalize();
            p.KnockX = dir.x; p.KnockZ = dir.z;
        }

        /// <summary>The synced knockback direction for a player id (used by the pass-out ragdoll patch). Returns false
        /// when unknown or unset (0,0) so the patch keeps the vanilla forward faint.</summary>
        internal bool TryGetKnock(ulong id, out float kx, out float kz)
        {
            kx = 0f; kz = 0f;
            if (id == 0 || !_state.Players.TryGetValue(id, out var p)) return false;
            kx = p.KnockX; kz = p.KnockZ;
            return kx != 0f || kz != 0f;
        }

        private void HandleOutOfBounds(ulong sender, bool water)
        {
            if (!_isHost) return;
            PlayerRegistry.Refresh();
            var gp = PlayerRegistry.Get(sender);
            // The radius re-check only applies to an AREA report. A drowning player is normally well inside the
            // radius, and water is a client-local measurement (vanilla has no swim state - it is a raycast against
            // the "Water" layer at the player's own position), so there is nothing for the host to re-derive.
            if (!water && gp != null && _state.AreaRadius > 0f)
            {
                var pos = gp.transform.position;
                float dx = pos.x - _state.AreaX, dz = pos.z - _state.AreaZ;
                if (UnityEngine.Mathf.Sqrt(dx * dx + dz * dz) <= _state.AreaRadius + 3f) return;   // not actually outside
            }
            if (RoundLogic.ApplyOutOfBounds(_state, _settings, sender, NowUnix()))
            {
                Core.Log.Msg($"[PropHunt] {sender} eliminated ({(water ? "went into deep water" : "left the play area")}).");
                PushState();
            }
        }

        // ---- prop collision / height helpers ----

        /// <summary>
        /// When the local hider's prop changes, update the CharacterController target height to the prop's
        /// largest world dimension (clamped to the default character height of 1.85m). On undisguise (lpid &lt; 0)
        /// the target is cleared so the vanilla height logic takes over again. When the hider is currently
        /// crouched while equipping a prop, force them upright so the scaled capsule doesn't start below ground.
        /// </summary>
        private void UpdatePropCollisionHeight(int lpid)
        {
            if (LocalRole != PlayerRole.Hider) { PropCollisionState.TargetHeight = 0f; return; }
            if (lpid < 0) { PropCollisionState.TargetHeight = 0f; return; }
            try
            {
                float size = PropHunt.Disguise.PropCatalog.SizeOf(lpid);
                // scale the raw world size to a sensible character height:
                // the prop's largest dimension is taken directly as the target height, then clamped to [0.5, 1.85].
                // 0.5m is a safe minimum (CharacterController breaks below ~0.3m); 1.85m is the vanilla default.
                PropCollisionState.TargetHeight = size > 0f ? UnityEngine.Mathf.Clamp(size, 0.5f, 1.85f) : 0f;

                // if already crouched when a prop is equipped, force standing so the shrunk capsule
                // doesn't clip below the floor on the first frame
                try
                {
                    var pm = PlayerSingleton<PlayerMovement>.Instance;
                    if (pm != null && pm.IsCrouched) pm.SetCrouched(false);
                }
                catch (System.Exception e) { Core.LogDebug("[PropHunt] force-uncrouch on prop equip failed: " + e.Message); }

                Core.LogDebug($"[PropHunt] prop collision height -> {PropCollisionState.TargetHeight:F2}m (propSize={size:F2}m, propId={lpid})");
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] UpdatePropCollisionHeight failed: " + e.Message); }
        }

        // ---- decoy hit ----

        /// <summary>
        /// Called by CatchController when the hunter's ray resolves a transform whose name starts with
        /// "ph_decoy_". If host, handle directly; otherwise send an intent to the host.
        /// </summary>
        internal void RequestHitDecoy(int idx)
        {
            if (_isHost) HandleDecoyHit(LocalId, idx);
            else SendToHost(new DecoyHitMessage { DecoyIndex = idx });
        }

        /// <summary>Host-only: validate and apply a hunter's hit on a decoy.</summary>
        private void HandleDecoyHit(ulong hunter, int decoyIndex)
        {
            if (!_isHost) return;
            if (_state.Phase != RoundPhase.Hunting) return;
            if (!_state.Players.TryGetValue(hunter, out var h) || h.Role != PlayerRole.Hunter) return;

            if (decoyIndex < 0 || decoyIndex >= _state.Decoys.Count) return;
            var d = _state.Decoys[decoyIndex];
            if (d.Destroyed) return;

            // No distance gate: a decoy is hit by the same long-range projectile path as a real prop (the client
            // already ray-hit the decoy's collider before sending this), so range is not re-checked.
            if (RoundLogic.ApplyHitDecoy(_state, decoyIndex))
            {
                var dAfter = _state.Decoys[decoyIndex];
                if (dAfter.Destroyed)
                {
                    h.DecoysSmashed++;   // hunter scores for clearing a fake (RoundScore * 3)
                    Core.Log.Msg($"[PropHunt] hunter {hunter} DESTROYED decoy {decoyIndex} (FAKE!) hits={dAfter.Hits}/{dAfter.MaxHits}.");
                    var dpos = new Vector3(dAfter.X, dAfter.Y, dAfter.Z);
                    BroadcastFx(new DecoyFxMessage { HunterId = hunter, X = dpos.x, Y = dpos.y, Z = dpos.z });
                    NotifyDecoyFx(hunter, dpos);
                }
                else
                    Core.Log.Msg($"[PropHunt] hunter {hunter} hit decoy {decoyIndex} ({dAfter.Hits}/{dAfter.MaxHits}).");
                PushState();
            }
        }

        // ---- engine helpers ----

        /// <summary>
        /// Host: stop the round that is running right now and go to the between-rounds screen.
        ///
        /// This is what a host actually wants mid-round - a different safehouse, changed settings, someone who needs a
        /// minute - and the only button there used to be threw everyone back to the hub, which a player can do from
        /// their own pause menu anyway. Ending the round scores it exactly like a natural timeout, so the leaderboard
        /// stays honest rather than gaining a special "abandoned" case.
        ///
        /// Hunters win, on the same rule as the clock running out: the hiders did not survive to the end, because there
        /// was no end to survive to. Turning autostart off first means the round lands in the setup screen and waits.
        /// </summary>
        internal void RequestEndRound()
        {
            if (!IsHost) return;
            if (_state.Phase != RoundPhase.Hiding && _state.Phase != RoundPhase.Hunting) return;
            try
            {
                RoundLogic.EndRound(_state, _settings, NowUnix(), winnerHunters: true);
                PublishState();
                Core.Log.Msg($"[PropHunt] host ended round {_state.RoundNumber} early.");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] end round failed: " + e.Message); }
        }

        /// <summary>Host: leave the gamemode and return to the Side Hustle hub (phone "Return to hub" button + MatchEnd auto-return).</summary>
        internal void RequestReturnToHub()
        {
            if (_returnRequested) return;
            _returnRequested = true;
            try { _ctx?.ReturnToHub(); } catch (Exception e) { Core.Log.Warning("[PropHunt] ReturnToHub failed: " + e.Message); }
        }

#if DEBUG
        /// <summary>A stand-in lobby member for solo testing (phsolo). Deliberately a LOW id so role assignment,
        /// which sorts by id and hunts with the lowest, makes IT the hunter and the real player the hider - the
        /// hider is the side with the disguise, the whistle and the prop rotation, so it is the side worth driving.
        /// It never resolves to a Player object, which every per-player path already tolerates.</summary>
        internal const ulong DebugStandInMember = 1UL;
        internal static bool DebugSoloMode;
#endif

        private List<ulong> GetMemberIds()
        {
            var list = new List<ulong>();
            try
            {
                var ms = PropHuntNet.Client?.GetLobbyMembers();
                if (ms != null) foreach (var m in ms) if (m.SteamId64 != 0) list.Add(m.SteamId64);
            }
            catch { }
#if DEBUG
            // Solo test harness: add the stand-in AND ourselves, because with no real lobby the member list is empty
            // and even the local player is missing. Everything downstream then runs its normal two-player path.
            if (DebugSoloMode)
            {
                if (!list.Contains(DebugStandInMember)) list.Add(DebugStandInMember);
                ulong me = LocalId;
                if (me != 0 && !list.Contains(me)) list.Add(me);
            }
#endif
            return list;
        }

        private void SetPlayArea()
        {
            try
            {
                var lp = Player.Local;
                if (lp != null) { var pos = lp.transform.position; _state.AreaX = pos.x; _state.AreaY = pos.y; _state.AreaZ = pos.z; }
            }
            catch { }
            // A host who never touched the slider gets a radius that fits the lobby, recomputed each round so people
            // joining mid-match widen it. Someone who set a number keeps it, whatever the count does.
            float want = _settings.PlayAreaRadius;
            if (Config.PropHuntPreferences.PlayAreaRadiusUntouched
                && Mathf.Approximately(want, Config.PropHuntPreferences.PlayAreaRadiusFactory))
            {
                int players = Mathf.Max(_state.Players.Count, LobbyMemberCount);
                want = DefaultAreaRadiusFor(Mathf.Max(2, players));
            }
            _state.AreaRadius = Mathf.Max(want, MinPlayAreaRadius);
        }

        /// <summary>
        /// The play-area radius to OFFER a host who has not touched the setting, scaled to how many people are in the
        /// lobby: 50m up to ten players, then 60m and five more for every further five.
        ///
        /// Only ever a default. A host who moved the slider keeps their number - detected by the stored preference still
        /// reading its own factory value, so "I deliberately set 75" and "I never looked" stay distinguishable.
        /// </summary>
        internal static float DefaultAreaRadiusFor(int players)
        {
            if (players <= 10) return 50f;
            return 60f + 5f * Mathf.Floor((players - 10) / 5f);
        }

        /// <summary>Hard floor for the play-area radius. A too-small area (a friend hosted with 40m, centred on a
        /// safehouse whose interior spawn sits near one edge) strands the hider outside the wall within seconds and
        /// eliminates them. 50m is the smallest that reliably contains the smallest safehouse + its immediate yard.</summary>
        private const float MinPlayAreaRadius = 50f;
        private const string TrashGrabberId = "trashgrabber";   // vanilla item id (DragManager checks ItemInstance.ID == "trashgrabber")

        // ---- local effects (freeze + blind hunters during hiding), applied only on change ----

        private void ApplyLocalEffects()
        {
            var role = LocalRole;
            var phase = _state.Phase;
            // change-gate on (phase, role): every effect below depends only on those two, so a same-phase role
            // flip (Infection: Hider->Hunter mid-Hunting) still re-runs this. A future per-PLAYER effect must NOT
            // rely on this key - it would silently fail to re-apply when only that one player's data changed.
            // Wearing a lobby prop is part of the key: it is per-player state, and the comment above is the warning
            // that per-player effects gated only on (phase, role) never re-apply. Without it the camera would not swing
            // to third person when someone puts a prop on, because nothing else about them changed.
            int key = (int)phase * 32 + (int)role * 2 + (LocalLobbyProp >= 0 ? 1 : 0);
            if (key == _lastEffectKey) return;
            _lastEffectKey = key;

            bool frozen = phase == RoundPhase.Hiding && role == PlayerRole.Hunter;
            bool blind = frozen;
            if (phase == RoundPhase.Lobby || phase == RoundPhase.MatchEnd || phase == RoundPhase.Safehouse) { frozen = false; blind = false; }

            // a disguised hider has no equipment - disable the hotbar so number keys (incl. [2] = change prop)
            // aren't eaten by the game and no item is held on the prop. Hunters keep it (they need the weapon).
            // The lobby dressing room counts too: someone wearing a prop there is in exactly the same position, and
            // leaving them a hotbar meant [2] equipped an item instead of rolling another prop.
            bool hotbar = !(role == PlayerRole.Hider && (phase == RoundPhase.Hiding || phase == RoundPhase.Hunting))
                          && LocalLobbyProp < 0;

            SetFrozen(frozen);
            SetBlind(blind);
            SetHotbar(hotbar);

            // Hiders move a touch slower than hunters (host-configurable, only during the active round). Client-local:
            // only the local player's Move() reads StaticMoveSpeedMultiplier, so this genuinely slows the local hider
            // and then syncs. Composed with the CTRL slow-walk by SlowWalk so the two never clobber each other.
            bool activeHider = role == PlayerRole.Hider && (phase == RoundPhase.Hiding || phase == RoundPhase.Hunting);
            Patches.SlowWalk.SetRoleFactor(activeHider ? UnityEngine.Mathf.Clamp(_settings.HiderSpeedPercent / 100f, 0.5f, 1f) : 1f);

            // arm/disarm is role-driven (the single authority): a hunter holds their weapon PLUS the trash grabber
            // during the hunt; anyone who stops being a hunter (Continuous swap, or any non-hunter) is stripped, so
            // nothing persists or stacks across rounds. All calls are idempotent (GetAmountOfItem guards).
            // The trash grabber lets hunters clear trash so hiders can't permanently hide as litter on the open street;
            // it's a separate tool (Equippable_TrashGrabber) so using it never triggers the weapon-fire catch ray.
            if (role == PlayerRole.Hunter && phase == RoundPhase.Hunting)
            {
                // if the host changed the weapon mid-match, strip the weapon we actually granted last round before
                // handing out the new one (RemoveWeapon strips by id, so it would otherwise leave the old gun behind).
                if (!string.IsNullOrEmpty(_armedWeaponId) && _armedWeaponId != _settings.HunterWeapon)
                    RoundEnvironment.RemoveWeapon(_armedWeaponId);
                RoundEnvironment.GiveWeapon(_settings.HunterWeapon);
                RoundEnvironment.GiveWeapon(TrashGrabberId);
                _armedWeaponId = _settings.HunterWeapon;
            }
            else if (role != PlayerRole.Hunter)
            {
                RoundEnvironment.RemoveWeapon(_settings.HunterWeapon);
                if (!string.IsNullOrEmpty(_armedWeaponId) && _armedWeaponId != _settings.HunterWeapon)
                    RoundEnvironment.RemoveWeapon(_armedWeaponId);
                RoundEnvironment.RemoveWeapon(TrashGrabberId);
                _armedWeaponId = null;
            }

            // a new hunter starts in first person (the catch/fire raycast comes from the camera); they can still
            // toggle 3rd person with V. Without this, a hider caught into a hunter stays stuck in the pulled-back view.
            // A hider defaults to third person at round start so they can see their disguise (V still toggles back).
            if (phase == RoundPhase.Lobby)
            {
                // Wearing a prop in the lobby means seeing it, same as in a round. Taking it off hands the view back
                // rather than leaving someone stuck in third person while they are a person again.
                if (LocalLobbyProp >= 0) _thirdPerson?.ForceOn(); else _thirdPerson?.ForceOff();
            }
            else if (role == PlayerRole.Hunter) _thirdPerson?.ForceOff();
            else if (role == PlayerRole.Hider && (phase == RoundPhase.Hiding || phase == RoundPhase.Hunting)) _thirdPerson?.ForceOn();
        }

        private void RestoreLocalEffects()
        {
            _lastEffectKey = int.MinValue;
            SetFrozen(false);
            SetBlind(false);
            SetHotbar(true);
            try { var cam = PlayerSingleton<PlayerCamera>.Instance; if (cam != null) cam.SetCanLook(true); } catch { }   // never leave the camera locked
        }

        /// <summary>
        /// Set the two flags that hide the hotbar UI and stop items being equipped.
        ///
        /// Compared against the LIVE values, not a cached bool. Every scene-state transition re-applies equipping from
        /// the state's own properties (StateProperties -> SetEquippingEnabled), so the game hands the hotbar back on
        /// its own while a round is running; a cached "already applied" would then never correct it again. The actual
        /// input block is <see cref="Patches.HotbarSelectionBlockPrefix"/> - these flags are what keeps the UI honest.
        /// </summary>
        /// <summary>
        /// Put the phone torch out for a player who was already holding one when they became a prop.
        ///
        /// Clearing the flag is not enough, and that was the bug: Phone.Update rebuilds only the LOCAL light from it
        /// (Phone.cs:127), while the state other players render arrived earlier over Player.SetFlashlightOn_Server. So
        /// the owner's light went dark, every other player still saw a glowing crate, and pressing [F] could not fix it -
        /// the local flag was already false, so the toggle tried to switch the light ON, which the round blocks.
        ///
        /// Vanilla's own toggle does three things (Phone.cs:136-143); this repeats all three for the off direction. The
        /// visibility attribute matters as much as the beam: left at its lit values a doused prop stays easier for NPCs
        /// to notice. Runs once per transition, because the flag it tests is what it clears.
        /// </summary>
        private static void DouseFlashlight()
        {
            try
            {
                var phone = PlayerSingleton<Il2CppScheduleOne.UI.Phone.Phone>.Instance;
                if (phone == null || !phone.FlashlightOn) return;
                phone.FlashlightOn = false;
                try
                {
                    var vis = phone.flashlightVisibility;
                    if (vis != null) { vis.pointsChange = 0f; vis.multiplier = 1f; }
                }
                catch (Exception e) { Core.LogDebug("[PropHunt] flashlight visibility reset failed: " + e.Message); }
                var local = Il2CppScheduleOne.PlayerScripts.Player.Local;
                if (local != null) local.SetFlashlightOn_Server(false);   // our network prefix always allows OFF
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] douse flashlight failed: " + e.Message); }
        }

        private void SetHotbar(bool enabled)
        {
            _appliedHotbar = enabled;
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv == null) return;
                if (inv.HotbarEnabled != enabled) inv.HotbarEnabled = enabled;
                if (inv.EquippingEnabled != enabled) inv.SetEquippingEnabled(enabled);
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] SetHotbar failed: " + e.Message); }
        }

        private void SetFrozen(bool frozen)
        {
            if (frozen == _appliedFrozen) return;
            _appliedFrozen = frozen;
            try { var pm = PlayerSingleton<PlayerMovement>.Instance; if (pm != null) pm.CanMove = !frozen; }
            catch (Exception e) { Core.LogDebug("[PropHunt] SetFrozen failed: " + e.Message); }
        }

        /// <summary>Freeze/unfreeze the LOCAL player root by toggling its CharacterController during a knockdown. The
        /// controller stays active while ragdolled and keeps sliding the root toward the player's facing, dragging the
        /// ragdoll anchor with it - which overrides the knockback impulse and makes the OWNER'S OWN body always topple
        /// forward (observers have no local controller, so they see the correct direction). With the controller off the
        /// root stays put and the spine impulse from <see cref="Ragdoll"/> decides the fall. Local; undone on recovery.</summary>
        private bool _rootFrozenByUs;   // only hand the controller back if WE were the ones who took it away

        /// <summary>
        /// The single owner of "is our root frozen". Two things want it - being knocked down and locking a prop in place -
        /// and they must not both call the raw freeze.
        ///
        /// <see cref="FreezeLocalRoot"/> remembers whether the disable was OURS by reading the controller's current
        /// state, so a second freeze while already frozen records "not ours" and the eventual unfreeze does nothing at
        /// all - a player left permanently unable to move. Routing both through one desired-state check makes that
        /// impossible rather than merely unlikely.
        /// </summary>
        private void ApplyRootFreeze()
        {
            bool want = _localDownedApplied || LocalPropLocked;
            if (want == _rootFreezeApplied) return;
            _rootFreezeApplied = want;
            FreezeLocalRoot(want);
        }

        private bool _rootFreezeApplied;

        /// <summary>Whether the local player has locked their prop in place (mid-air included). Toggled with the fire
        /// button, which a disguised hider has no other use for.</summary>
        internal bool LocalPropLocked { get; private set; }

        /// <summary>
        /// Toggle the prop lock. Refused unless a prop is actually being worn - locking a person in place is not a
        /// feature - and dropped automatically when the prop goes away, so nobody can be left hanging by a round change.
        /// </summary>
        internal void TogglePropLock()
        {
            if (WornPropId < 0) return;
            LocalPropLocked = !LocalPropLocked;
            ApplyRootFreeze();
            Core.LogDebug($"[PropHunt] prop lock {(LocalPropLocked ? "ON" : "off")}.");
        }

        /// <summary>Called every tick: a lock only survives while there is a prop to lock. A forced rotation, being
        /// caught, or the round ending all clear it without each of them having to remember to.</summary>
        private void TickPropLock()
        {
            if (LocalPropLocked && WornPropId < 0) { LocalPropLocked = false; Core.LogDebug("[PropHunt] prop lock cleared - no prop."); }
            ApplyRootFreeze();
        }

        private void FreezeLocalRoot(bool freeze)
        {
            try
            {
                var pm = PlayerSingleton<PlayerMovement>.Instance;
                if (pm == null || pm.Controller == null) return;

                if (freeze)
                {
                    // Someone else may already have it off - a skateboard disables the controller deliberately while
                    // mounted. Remember only the case where the disable is ours, so recovery cannot re-enable a
                    // controller the game wants off and drop the player off the board.
                    _rootFrozenByUs = pm.Controller.enabled;
                    pm.Controller.enabled = false;
                }
                else if (_rootFrozenByUs)
                {
                    _rootFrozenByUs = false;
                    pm.Controller.enabled = true;
                }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] FreezeLocalRoot failed: " + e.Message); }
        }

        private void SetBlind(bool blind)
        {
            if (blind == _appliedBlind) return;
            _appliedBlind = blind;
            // Close the hunter's EYES for the hide phase (and open them when the hunt begins) - feels far cleaner than
            // slapping a black screen over the view. EyeBlink drives the vanilla eyelid overlay locally.
            if (blind) PropHunt.View.EyeBlink.Blind();
            else PropHunt.View.EyeBlink.Unblind();
        }
    }
}
