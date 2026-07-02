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
    /// live in RoundLogic (headlessly unit-tested); here they are wired to I/O.
    /// </summary>
    internal sealed class GameModeController
    {
        internal static GameModeController Active { get; private set; }
        private static bool _handlersRegistered;

        private readonly SideHustle.LaunchContext _ctx;
        private readonly bool _isHost;
        private RoundSettings _settings = new RoundSettings();
        private bool _settingsDirty;        // host edited a setting via the phone -> re-publish to clients (throttled)
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
        internal float OobGrace => _playArea != null ? _playArea.GraceLeft : 0f;
        internal float LastTauntTime => _lastTauntTime;
        /// <summary>Seconds until the next global whistle, or -1 if none is pending (not Hunting / taunts off / no
        /// further whistle before the hunt ends). Computed from the SYNCED phase timer + interval, so the host AND
        /// every client show the same countdown - hiders need to know when the next forced reveal is coming.</summary>
        internal int SecondsToWhistle
        {
            get
            {
                if (_state.Phase != RoundPhase.Hunting) return -1;
                int interval = _settings.TauntIntervalSeconds;
                if (interval <= 0) return -1;
                long now = NowUnix();
                long huntStart = _state.PhaseEndsAtUnix - _settings.HuntSeconds;   // when Hunting began (host arms the whistle here)
                long elapsed = now - huntStart; if (elapsed < 0) elapsed = 0;
                long next = huntStart + interval * ((elapsed / interval) + 1);
                if (next >= _state.PhaseEndsAtUnix) return -1;   // no further whistle before the hunt ends
                return (int)Math.Max(0L, next - now);
            }
        }
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
            get { int pid = LocalPropId; return pid >= 0 ? PropHunt.Disguise.PropCatalog.ById(pid)?.Name : null; }
        }
        /// <summary>Whether the local player's prop rotation is frozen ([F] toggles it).</summary>
        internal bool LocalLocked
        {
            get { var id = LocalId; return id != 0 && _state.Players.TryGetValue(id, out var p) && p.Locked; }
        }

        internal int SecondsLeft =>
            _state.PhaseEndsAtUnix <= 0 ? 0 : (int)Math.Max(0, _state.PhaseEndsAtUnix - NowUnix());

        private static long NowUnix() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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
            // DEBUG-ONLY: the hunter's hit/catch flash reveals which props are real hiders - a cheat in real play -
            // so it ships only in Debug for testing. The victim's own status flash (below) is legitimate and stays.
            if (me == hunterId) SetFx(caught ? "CATCH!" : "HIT", new Color(0.3f, 1f, 0.5f));
#endif
            if (me == victimId) SetFx(caught ? "CAUGHT!" : "HIT!", Color.red);
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
            _disguise = new DisguiseController();
            _decoy = new DecoyController();
            _picker = new PropPicker(this);
            _highlighter = new PropHighlighter(this);
            _thirdPerson = new PropHunt.View.ThirdPersonController(this);
            _catch = new CatchController(this);
            _passthrough = new PropPassthrough(this);
            _playArea = new PlayAreaController(this);
            _border = new PlayAreaBorder(this);
            _taunt = new TauntController(this);
            _tauntWheel = new Taunt.TauntWheel(this);
            _onboarding = new UI.Onboarding(this);
            _spectator = new PropHunt.View.SpectatorController(this);
            _state = new GameState { Phase = RoundPhase.Lobby, SettingsBlob = _settings.Serialize(), CatalogHash = PropCatalog.Hash };
            RoundLogic.SyncRoster(_state, GetMemberIds());
            PushState();
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
            _disguise = new DisguiseController();
            _decoy = new DecoyController();
            _picker = new PropPicker(this);
            _highlighter = new PropHighlighter(this);
            _thirdPerson = new PropHunt.View.ThirdPersonController(this);
            _catch = new CatchController(this);
            _passthrough = new PropPassthrough(this);
            _playArea = new PlayAreaController(this);
            _border = new PlayAreaBorder(this);
            _taunt = new TauntController(this);
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
                    RoundEnvironment.TeleportLocalTo(sp.Pos + UnityEngine.Vector3.up * 1f);
                    try { var lp = Player.Local; if (lp != null) lp.transform.rotation = UnityEngine.Quaternion.Euler(0f, sp.Yaw, 0f); } catch { }
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

            if (_isHost && _matchStarted)
            {
                bool changed = RoundLogic.SyncRoster(_state, GetMemberIds());
                // pre-select the safehouse (size-based) BEFORE TickHost may transition RoundEnd -> Safehouse,
                // so the pure RoundLogic can read the chosen code without any engine/Property dependency.
                if (_state.Phase == RoundPhase.RoundEnd && _settings.Structure == RoundStructure.Continuous && !SafehouseSelector.Fits(_state.SafehouseCode, _state.Players.Count))
                    _state.SafehouseCode = SafehouseSelector.SelectForPlayerCount(_state.Players.Count);
                if (RoundLogic.TickHost(_state, _settings, NowUnix())) changed = true;
                if (changed) PushState();
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
                    // Coming from the safehouse, players are ALREADY inside it (= the play-area centre) and its doors
                    // just opened, so they walk out into the surrounding map - NO teleport. Otherwise (round 1) gather
                    // everyone at the area centre.
                    if (prevPhase != RoundPhase.Safehouse)
                        RoundEnvironment.TeleportLocalInto(_state.AreaX, _state.AreaY, _state.AreaZ, LocalId);
#if DEBUG
                    if (LocalRole == PlayerRole.Hider) DumpPropDebug();
#endif
                    // round-start music (reused game track); local per client, driven by the synced phase edge.
                    PropHunt.Music.RoundMusicController.Play(PropHunt.Config.PropHuntPreferences.HidingMusicTrack);
                }
                if (_state.Phase == RoundPhase.Hunting)
                    // Hunt begins (hunters unblinded): FADE the music out so hunters can hear the hiders' whistles
                    // precisely. Stop() ramps the hiding track down over its native fade-out; the hunt stays quiet.
                    PropHunt.Music.RoundMusicController.Stop();
                // back in the safehouse / between rounds -> reset everyone to first person (a pulled-back
                // third-person view from the last round must not carry into the lobby) + stop the round music.
                if (_state.Phase == RoundPhase.Safehouse || _state.Phase == RoundPhase.RoundEnd || _state.Phase == RoundPhase.MatchEnd)
                {
                    _thirdPerson?.ForceOff();
                    PropHunt.Music.RoundMusicController.Stop();
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
            _disguise?.Apply(_state);
            _decoy?.Apply(_state);
            DriveLocalRagdoll();   // ragdoll/stand-up the local player when the synced Downed flag flips (FF-KO / concussion)

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
            try { _tauntWheel?.Dispose(); } catch { }
            try { PropHunt.Music.RoundMusicController.Stop(); } catch { }   // hand music back to the game
            try { if (_localDownedApplied) { _localDownedApplied = false; Player.Local?.SendPassOutRecovery(); Player.Activate(); } } catch { }   // stand up + restore control if we tore down mid-ragdoll (clear flag first so it resets even if an RPC throws)
            try { Quests.GuideQuest.Stop(); } catch { }   // remove the local guidance quest on session teardown
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
                c.RegisterMessageHandler<HitHunterMessage>((m, s) => Active?.HandleHitHunter(s.m_SteamID, m.VictimSteamId, new Vector3(m.DirX, m.DirY, m.DirZ)));
                c.RegisterMessageHandler<OutOfBoundsMessage>((m, s) => Active?.HandleOutOfBounds(s.m_SteamID));
                c.RegisterMessageHandler<TauntMessage>((m, s) => Active?.NotifyTaunt(m.SteamId, m.Sound, m.IsWhistle));
                c.RegisterMessageHandler<ManualTauntMessage>((m, s) => Active?.HandleManualTaunt(s.m_SteamID, m.Sound));
                c.RegisterMessageHandler<DecoyHitMessage>((m, s) => Active?.HandleDecoyHit(s.m_SteamID, m.DecoyIndex));
                c.RegisterMessageHandler<CatchFxMessage>((m, s) => Active?.NotifyCatchFx(m.HunterId, m.VictimId, m.Caught, new Vector3(m.X, m.Y, m.Z)));
                c.RegisterMessageHandler<StunFxMessage>((m, s) => Active?.NotifyStunFx(m.ThrowerId, new Vector3(m.X, m.Y, m.Z)));
                c.RegisterMessageHandler<DecoyFxMessage>((m, s) => Active?.NotifyDecoyFx(m.HunterId, new Vector3(m.X, m.Y, m.Z)));
                c.RegisterMessageHandler<SafehouseDoorLockMessage>((m, s) => Active?.HandleSafehouseDoorLock(m.PropertyCode, m.Locked));
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
        private bool _localDownedApplied;   // edge-detect the synced Downed flag so we ragdoll/recover the owner exactly once

        /// <summary>Drive the native ragdoll on the LOCAL (owner) player from the synced Downed flag. The host owns the
        /// Downed state (friendly-fire KO or concussion), but the native <c>PassOut</c> RPC is owner-gated + ExcludeOwner,
        /// so only the downed player's OWN client can trigger the limp that replicates to everyone. We therefore watch
        /// our own Downed edge here: rising -> SendPassOut (ragdoll), falling -> SendPassOutRecovery (stand up in place).</summary>
        private void DriveLocalRagdoll()
        {
            bool downed = LocalDowned;
            if (downed == _localDownedApplied) return;
            _localDownedApplied = downed;
            try
            {
                var lp = Player.Local;
                if (lp == null) return;
                if (downed)
                {
                    lp.SendPassOut();   // networked ragdoll (replicates to everyone). Its owner-side ExitAll disables
                                        // look/move/inventory during the knockdown - desirable while down.
                    SetFx("KNOCKED DOWN", new Color(0.95f, 0.55f, 0.2f));
                }
                else
                {
                    lp.SendPassOutRecovery();   // un-ragdoll everywhere
                    // PassOutRecovery does NOT re-enable control - vanilla relies on PassOutScreen.Close() -> Activate(),
                    // which we suppress (PassOutScreenGatePatch). So restore control ourselves: Player.Activate() is the
                    // exact inverse of the ExitAll that SendPassOut ran (canLook + CanMove + inventory + crosshair +
                    // LockMouse). Without this the camera stays locked ("canLook=false") after standing up.
                    Player.Activate();
                }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] ragdoll drive failed: " + e.Message); }
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

        /// <summary>Host: apply a single setting edit from the phone Settings tab + flag it for re-publish so clients
        /// see the change live (Tick pushes the new SettingsBlob, throttled). No-op for non-hosts (clients can't edit).</summary>
        internal void SetSetting(string key, string value)
        {
            if (!_isHost) return;
            _settings.ApplyKeyValue(key, value);
            _settingsDirty = true;
        }

        internal void ReportOutOfBounds()
        {
            if (_isHost) HandleOutOfBounds(LocalId);
            else SendToHost(new OutOfBoundsMessage());
        }

        // host-only: last unix time each hider was AWARDED a taunt point, to cap taunt scoring at once per 15s
        // (the taunt sound/cue still plays every time - only the score is rate-limited).
        private readonly System.Collections.Generic.Dictionary<ulong, long> _lastTauntScoreUnix = new System.Collections.Generic.Dictionary<ulong, long>();

        /// <summary>Local player asks to taunt ([1]) with a chosen sound; the host broadcasts the reveal cue.</summary>
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
            int maxHits = ComputeMaxHits(propId);
            bool freeChange = _settings.FreeChangesInHiding && _state.Phase == RoundPhase.Hiding;
            bool ok = RoundLogic.ApplySelectProp(_state, sender, propId, maxHits, _settings.MaxPropChanges, freeChange);
            _state.Players.TryGetValue(sender, out var sp);
            Core.LogDebug($"[PropHunt] host: select from {sender} prop {propId} hp {maxHits} -> {(ok ? "ACCEPTED" : "rejected")}" +
                          (sp != null ? $" (role={sp.Role} elim={sp.Eliminated} changes={sp.Changes}/{_settings.MaxPropChanges})" : " (sender NOT in roster)"));
            if (ok) PushState();
        }

        /// <summary>Size-based prop HP: bigger props take many more hits to catch (round(maxDim * HitsPerMetre), clamped).</summary>
        private int ComputeMaxHits(int propId)
        {
            float maxDim = PropHunt.Disguise.PropCatalog.SizeOf(propId);
            int hp = UnityEngine.Mathf.RoundToInt(maxDim * UnityEngine.Mathf.Max(1, _settings.HitsToCatch));
            return UnityEngine.Mathf.Clamp(hp, 1, 25);
        }

        private void HandleLock(ulong sender, bool locked)
        {
            if (!_isHost) return;
            if (RoundLogic.ApplyLock(_state, sender, locked)) PushState();
        }

        private void HandleRotate(ulong sender, float yaw)
        {
            if (!_isHost) return;
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
            // host-side geometry re-validation - never trust the client's ray
            PlayerRegistry.Refresh();
            var hp = PlayerRegistry.Get(hunter);
            var vp = PlayerRegistry.Get(victim);
            if (hp != null && vp != null)
            {
                // No distance gate - hunters fire projectile weapons, so a hit counts at any range. The host still
                // re-validates the AIM via the lateral-offset gate below (the shot must actually be on the prop).
                // lateral-offset gate: a hider behind a large prop should be catchable from a wider angle
                // than a tiny prop. maxLateral is scaled to the victim's prop size with a generous tolerance
                // (+0.5m) to account for camera-vs-body-forward divergence.
                float victimPropSize = PropHunt.Disguise.PropCatalog.SizeOf(PropIdOf(victim));
                // Validate against the client's CAMERA AIM (the direction the shot was actually fired), NOT the body
                // forward. While a player stands still the body facing diverges from where they look, so a body-forward
                // cone grows with distance and falsely rejects long-range hits (moving toward the target aligned the
                // body, which is why it only worked up close). aimDir is the camera forward the client sent; fall back
                // to the body forward only for an old client that sent none.
                var hunterFwd = (aimDir.sqrMagnitude > 0.01f) ? aimDir.normalized : hp.transform.forward;
                var delta = vp.transform.position - hp.transform.position;
                float along = UnityEngine.Vector3.Dot(delta, hunterFwd);
                var lateral = delta - hunterFwd * along;
                float lateralDist = lateral.magnitude;
                // Allowed lateral offset SCALES with distance (a cone, not a fixed radius): a bigger prop is catchable
                // from a wider angle. prop-size base + ~14% of the forward distance keeps it bounded.
                float maxLateral = UnityEngine.Mathf.Clamp(victimPropSize * 0.4f, 0.15f, 2.0f) + 0.75f + UnityEngine.Mathf.Max(0f, along) * 0.14f;
                if (lateralDist > maxLateral)
                {
                    Core.LogDebug($"[PropHunt] tag rejected: lateral {lateralDist:F2}m > {maxLateral:F2}m (propSize={victimPropSize:F2})");
                    return;
                }
            }
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

        /// <summary>Host: validate + apply a friendly-fire hit from one hunter on another. Re-validates the aim with a
        /// lateral-offset cone (like <see cref="HandleClaimTag"/>, but a human-body base since hunters wear no prop),
        /// then routes through <see cref="RoundLogic.ApplyHitHunter"/>. Plays blood on every accepted hit and, on the
        /// knockdown hit, a stun cue; the ragdoll itself is driven on the victim's own client from the synced Downed
        /// flag (see <see cref="DriveLocalRagdoll"/>). Never trusts the client's ray.</summary>
        private void HandleHitHunter(ulong shooter, ulong victim, Vector3 aimDir)
        {
            if (!_isHost || _state.Phase != RoundPhase.Hunting || !_settings.FriendlyFire) return;
            PlayerRegistry.Refresh();
            var hp = PlayerRegistry.Get(shooter);
            var vp = PlayerRegistry.Get(victim);
            // Require BOTH players resolved/online so the geometry gate always runs - otherwise a client could target a
            // just-disconnected hunter (still in GameState until the next SyncRoster) and skip the aim validation.
            if (hp == null || vp == null)
            {
                Core.LogDebug($"[PropHunt] FF hit rejected: shooter/victim not resolved (hp={hp != null}, vp={vp != null}).");
                return;
            }
            // validate against the client's CAMERA AIM (the shot direction), not the body facing (long-range fix - see HandleClaimTag).
            var hunterFwd = (aimDir.sqrMagnitude > 0.01f) ? aimDir.normalized : hp.transform.forward;
            var delta = vp.transform.position - hp.transform.position;
            float along = UnityEngine.Vector3.Dot(delta, hunterFwd);
            var lateral = delta - hunterFwd * along;
            float maxLateral = 0.9f + UnityEngine.Mathf.Max(0f, along) * 0.14f;   // human-body base + a widening cone with range
            if (lateral.magnitude > maxLateral)
            {
                Core.LogDebug($"[PropHunt] FF hit rejected: lateral {lateral.magnitude:F2}m > {maxLateral:F2}m");
                return;
            }
            if (RoundLogic.ApplyHitHunter(_state, _settings, shooter, victim, NowUnix(), out bool newlyDowned))
            {
                var vpos = vp.transform.position;
                // blood spurt on the hit hunter (pure feedback; the gun does no real damage - see DisableVanillaPlayerDeath).
                try { vp?.Health?.PlayBloodMist(); } catch { }
                if (newlyDowned)
                {
                    SetKnockback(victim, vp.transform.position - hp.transform.position);   // ragdoll away from the shooter
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
        /// by <see cref="Patches.PassOutKnockbackPatch"/> on every client so the body falls in the hit direction instead
        /// of the vanilla always-forward faint. <paramref name="dir"/> is a world vector attacker-&gt;victim (y ignored).</summary>
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

        private void HandleOutOfBounds(ulong sender)
        {
            if (!_isHost) return;
            PlayerRegistry.Refresh();
            var gp = PlayerRegistry.Get(sender);
            if (gp != null && _state.AreaRadius > 0f)
            {
                var pos = gp.transform.position;
                float dx = pos.x - _state.AreaX, dz = pos.z - _state.AreaZ;
                if (UnityEngine.Mathf.Sqrt(dx * dx + dz * dz) <= _state.AreaRadius + 3f) return;   // not actually outside
            }
            if (RoundLogic.ApplyOutOfBounds(_state, _settings, sender, NowUnix()))
            {
                Core.Log.Msg($"[PropHunt] {sender} eliminated (left the play area).");
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

        /// <summary>Host: leave the gamemode and return to the Side Hustle hub (phone "Return to hub" button + MatchEnd auto-return).</summary>
        internal void RequestReturnToHub()
        {
            if (_returnRequested) return;
            _returnRequested = true;
            try { _ctx?.ReturnToHub(); } catch (Exception e) { Core.Log.Warning("[PropHunt] ReturnToHub failed: " + e.Message); }
        }

        private List<ulong> GetMemberIds()
        {
            var list = new List<ulong>();
            try
            {
                var ms = PropHuntNet.Client?.GetLobbyMembers();
                if (ms != null) foreach (var m in ms) if (m.SteamId64 != 0) list.Add(m.SteamId64);
            }
            catch { }
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
            _state.AreaRadius = Mathf.Max(_settings.PlayAreaRadius, MinPlayAreaRadius);
        }

        /// <summary>Hard floor for the play-area radius. A too-small area (a friend hosted with 40m, centred on a
        /// safehouse whose interior spawn sits near one edge) strands the hider outside the wall within seconds and
        /// eliminates them. 50m is the smallest that reliably contains the smallest safehouse + its immediate yard.</summary>
        private const float MinPlayAreaRadius = 50f;

        // ---- local effects (freeze + blind hunters during hiding), applied only on change ----

        private void ApplyLocalEffects()
        {
            var role = LocalRole;
            var phase = _state.Phase;
            // change-gate on (phase, role): every effect below depends only on those two, so a same-phase role
            // flip (Infection: Hider->Hunter mid-Hunting) still re-runs this. A future per-PLAYER effect must NOT
            // rely on this key - it would silently fail to re-apply when only that one player's data changed.
            int key = (int)phase * 16 + (int)role;
            if (key == _lastEffectKey) return;
            _lastEffectKey = key;

            bool frozen = phase == RoundPhase.Hiding && role == PlayerRole.Hunter;
            bool blind = frozen;
            if (phase == RoundPhase.Lobby || phase == RoundPhase.MatchEnd || phase == RoundPhase.Safehouse) { frozen = false; blind = false; }

            // a disguised hider has no equipment - disable the hotbar so number keys (incl. [2] = change prop)
            // aren't eaten by the game and no item is held on the prop. Hunters keep it (they need the weapon).
            bool hotbar = !(role == PlayerRole.Hider && (phase == RoundPhase.Hiding || phase == RoundPhase.Hunting));

            SetFrozen(frozen);
            SetBlind(blind);
            SetHotbar(hotbar);

            // arm/disarm is role-driven (the single authority): a hunter holds exactly one weapon during the hunt;
            // anyone who stops being a hunter (Continuous swap, or any non-hunter) is stripped, so the gun never
            // persists or stacks across rounds. Both calls are idempotent (GetAmountOfItem guards).
            if (role == PlayerRole.Hunter && phase == RoundPhase.Hunting) RoundEnvironment.GiveWeapon(_settings.HunterWeapon);
            else if (role != PlayerRole.Hunter) RoundEnvironment.RemoveWeapon(_settings.HunterWeapon);

            // a new hunter starts in first person (the catch/fire raycast comes from the camera); they can still
            // toggle 3rd person with V. Without this, a hider caught into a hunter stays stuck in the pulled-back view.
            if (role == PlayerRole.Hunter) _thirdPerson?.ForceOff();
        }

        private void RestoreLocalEffects()
        {
            _lastEffectKey = int.MinValue;
            SetFrozen(false);
            SetBlind(false);
            SetHotbar(true);
            try { var cam = PlayerSingleton<PlayerCamera>.Instance; if (cam != null) cam.SetCanLook(true); } catch { }   // never leave the camera locked
        }

        private void SetHotbar(bool enabled)
        {
            if (enabled == _appliedHotbar) return;
            _appliedHotbar = enabled;
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv != null) { inv.HotbarEnabled = enabled; inv.SetEquippingEnabled(enabled); }
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

        private void SetBlind(bool blind)
        {
            if (blind == _appliedBlind) return;
            _appliedBlind = blind;
            try
            {
                var ov = Singleton<BlackOverlay>.Instance;
                if (ov != null) { if (blind) ov.Open(0.25f); else ov.Close(0.25f); }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] SetBlind failed: " + e.Message); }
        }
    }
}
