using System;
using System.Collections.Generic;
using PropHunt.Catch;
using PropHunt.Config;
using PropHunt.Disguise;
using PropHunt.Net;
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
        private GameState _state = new GameState();
        private HostSyncVar<string> _stateVar;
        private DisguiseController _disguise;
        private DecoyController _decoy;
        private PropPicker _picker;
        private PropHighlighter _highlighter;
        private PropHunt.View.ThirdPersonController _thirdPerson;
        private CatchController _catch;
        private PlayAreaController _playArea;
        private TauntController _taunt;
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
        internal bool LocalOutside => _playArea != null && _playArea.LocalOutside;
        internal float OobGrace => _playArea != null ? _playArea.GraceLeft : 0f;
        internal float LastTauntTime => _lastTauntTime;
        internal string LookTargetName => _picker != null ? _picker.CurrentTargetName : null;
        internal bool ThirdPersonOn => _thirdPerson != null && _thirdPerson.IsOn;
        internal bool RoundActive => _state.Phase == RoundPhase.Hiding || _state.Phase == RoundPhase.Hunting || _state.Phase == RoundPhase.RoundEnd;

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

        /// <summary>Local reveal cue when a taunt fires (host direct; clients via the P2P handler).</summary>
        internal void NotifyTaunt(ulong steamId)
        {
            _lastTauntTime = Time.time;   // TODO(testing): positional reveal sound at the hider + prop wobble
        }

        /// <summary>Debug: dump the prop pipeline state - catalog size/hash, crosshair target, highlight count,
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
                            if (mf != null && mf.sharedMesh != null && PropCatalog.IdForMesh(mf.sharedMesh) >= 0) near++;
                        }
                    Core.Log.Msg($"[PropHunt] props: {near} becomable object(s) within 22m ({scanned} colliders scanned).");
                }
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] DumpPropDebug scan failed: " + e.Message); }
        }

        // ---- lifecycle ----

        internal void StartAsHost()
        {
            Active = this;
            _settings = BuildSettings();
            EnsureHandlers();
            EnsureStateVar();
            PropCatalog.BuildIfNeeded();
            _disguise = new DisguiseController();
            _decoy = new DecoyController();
            _picker = new PropPicker(this);
            _highlighter = new PropHighlighter(this);
            _thirdPerson = new PropHunt.View.ThirdPersonController(this);
            _catch = new CatchController(this);
            _playArea = new PlayAreaController(this);
            _taunt = new TauntController(this);
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
            _playArea = new PlayAreaController(this);
            _taunt = new TauntController(this);
            try { var cur = _stateVar?.Value; if (!string.IsNullOrEmpty(cur)) ApplyStateString(cur); } catch { }
            Core.Log.Msg("[PropHunt] client session started; waiting for host state.");
        }

        /// <summary>Host: begin the match (host setup screen "START MATCH" or the phstart debug command).</summary>
        internal void BeginMatch()
        {
            if (!_isHost) { Core.Log.Warning("[PropHunt] BeginMatch ignored - not host."); return; }
            if (_matchStarted && _state.Phase != RoundPhase.Lobby) { Core.Log.Msg("[PropHunt] match already running."); return; }
            _matchStarted = true;
            _state.SettingsBlob = _settings.Serialize();
            _state.CatalogHash = PropCatalog.Hash;
            SetPlayArea();
            RoundLogic.BeginMatch(_state, _settings, NowUnix(), GetMemberIds());
            PushState();
            RoundEnvironment.ApplyHostWorld(_settings);   // lock time of day + freeze; police suppressed each tick
            Core.Log.Msg($"[PropHunt] match begun. {_settings}");
        }

        internal void Tick(float dt)
        {
            if (_disposed) return;
            if (_stateVar == null) { EnsureStateVar(); if (_stateVar == null) return; }
            if (!_handlersRegistered) EnsureHandlers();

            if (_isHost && _matchStarted)
            {
                bool changed = RoundLogic.SyncRoster(_state, GetMemberIds());
                if (RoundLogic.TickHost(_state, _settings, NowUnix())) changed = true;
                if (changed) PushState();
            }

            if (_state.Phase != _loggedPhase)
            {
                _loggedPhase = _state.Phase;
                Core.Log.Msg($"[PropHunt] phase -> {_state.Phase} (round {_state.RoundNumber}, you={LocalRole}, {SecondsLeft}s, " +
                             $"hunters={RoundLogic.CountRole(_state, PlayerRole.Hunter)}, hiders={AliveHiderCount}, winner={_state.Winner})");

                if (_state.Phase == RoundPhase.Hiding)
                {
                    // Rebuild the catalog now the world is fully loaded (the client's session-start build can
                    // run BEFORE the scene finishes loading -> a near-empty catalog + hash mismatch). Both sides
                    // rebuild at the same lifecycle point -> matching deterministic ids/hash.
                    PropCatalog.Build();
                    if (_isHost && _state.CatalogHash != PropCatalog.Hash) { _state.CatalogHash = PropCatalog.Hash; PushState(); }
                    RoundEnvironment.TeleportLocalInto(_state.AreaX, _state.AreaY, _state.AreaZ, LocalId);   // gather everyone into the area
                    if (LocalRole == PlayerRole.Hider) DumpPropDebug();   // auto-report the prop pipeline (no need to run phprops)
                }
                // arming/disarming the local hunter is role-driven, not phase-edge driven -> ApplyLocalEffects
            }

            // keep the world day-locked + police off + the local player crime-free during a round
            bool roundActive = _state.Phase == RoundPhase.Hiding || _state.Phase == RoundPhase.Hunting || _state.Phase == RoundPhase.RoundEnd;
            if (roundActive) RoundEnvironment.ClearLocalCrime();
            if (_isHost && roundActive) RoundEnvironment.SuppressPolice();

#if DEBUG
            // live grounding-mode tuner: [4] previous, [5] next (hider hotbar is disabled, so the number keys are free)
            if (RoundActive)
            {
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Alpha5)) { RoundEnvironment.GroundMode = (RoundEnvironment.GroundMode + 1) % RoundEnvironment.GroundModeCount; Core.Log.Msg($"[PropHunt] ground mode -> {RoundEnvironment.GroundModeName}"); }
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Alpha4)) { RoundEnvironment.GroundMode = (RoundEnvironment.GroundMode + RoundEnvironment.GroundModeCount - 1) % RoundEnvironment.GroundModeCount; Core.Log.Msg($"[PropHunt] ground mode -> {RoundEnvironment.GroundModeName}"); }
                // [6]/[7] fine-tune the "fixed" feet drop (lower / raise the prop) live
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Alpha6)) { RoundEnvironment.FixedFeetDrop = UnityEngine.Mathf.Clamp(RoundEnvironment.FixedFeetDrop + 0.02f, 0.5f, 1.5f); Core.Log.Msg($"[PropHunt] feet drop -> {RoundEnvironment.FixedFeetDrop:F2} (prop lower)"); }
                if (UnityEngine.Input.GetKeyDown(UnityEngine.KeyCode.Alpha7)) { RoundEnvironment.FixedFeetDrop = UnityEngine.Mathf.Clamp(RoundEnvironment.FixedFeetDrop - 0.02f, 0.5f, 1.5f); Core.Log.Msg($"[PropHunt] feet drop -> {RoundEnvironment.FixedFeetDrop:F2} (prop higher)"); }
            }
#endif

            ApplyLocalEffects();
            _picker?.Tick();
            _highlighter?.Tick();
            _thirdPerson?.Tick();
            _catch?.Tick();
            _playArea?.Tick();
            _taunt?.Tick();
            _disguise?.Apply(_state);
            _decoy?.Apply(_state);

            int lpid = LocalPropId;
            if (lpid != _lastLocalProp)
            {
                _lastLocalProp = lpid;
                var lid = LocalId;   // realign the optimistic local yaw to the synced value on a prop/round change
                _localYaw = (lid != 0 && _state.Players.TryGetValue(lid, out var lp)) ? lp.PropYaw : 0f;
                Core.LogDebug($"[PropHunt] local disguise PropId -> {lpid} ({LocalPropName ?? "none"})");
            }

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
            if (_isHost) RoundEnvironment.RestoreWorld();
            RestoreLocalEffects();
            try { _disguise?.Dispose(); } catch { }
            try { _decoy?.Dispose(); } catch { }
            try { _highlighter?.Dispose(); } catch { }
            try { _thirdPerson?.Dispose(); } catch { }
            _disguise = null;
            _decoy = null;
            _picker = null;
            _highlighter = null;
            _thirdPerson = null;
            _catch = null;
            _playArea = null;
            _taunt = null;
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
            ApplyLocalEffects();
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
                c.RegisterMessageHandler<ClaimTagMessage>((m, s) => Active?.HandleClaimTag(s.m_SteamID, m.VictimSteamId));
                c.RegisterMessageHandler<OutOfBoundsMessage>((m, s) => Active?.HandleOutOfBounds(s.m_SteamID));
                c.RegisterMessageHandler<TauntMessage>((m, s) => Active?.NotifyTaunt(m.SteamId));
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

        internal void RequestClaimTag(ulong victimSteamId)
        {
            if (_isHost) HandleClaimTag(LocalId, victimSteamId);
            else SendToHost(new ClaimTagMessage { VictimSteamId = victimSteamId });
        }

        internal void ReportOutOfBounds()
        {
            if (_isHost) HandleOutOfBounds(LocalId);
            else SendToHost(new OutOfBoundsMessage());
        }

        // ---- host-authoritative handlers (validate I/O, delegate the decision to RoundLogic) ----

        private void HandleSelectProp(ulong sender, int propId)
        {
            if (!_isHost) return;
            int maxHits = ComputeMaxHits(propId);
            bool ok = RoundLogic.ApplySelectProp(_state, sender, propId, maxHits, _settings.MaxPropChanges);
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
            if (RoundLogic.ApplyDropDecoy(_state, _settings, sender, x, y, z, yaw))
            {
                Core.Log.Msg($"[PropHunt] {sender} dropped a decoy ({_state.Decoys.Count} total).");
                PushState();
            }
            else
            {
                _state.Players.TryGetValue(sender, out var sp);
                Core.Log.Msg($"[PropHunt] decoy from {sender} rejected (phase={_state.Phase}, used={(sp != null ? sp.DecoysUsed : -1)}/{_settings.MaxDecoys}, propId={(sp != null ? sp.PropId : -99)})");
            }
        }

        private void HandleConcuss(ulong sender, float x, float y, float z)
        {
            if (!_isHost) return;
            if (RoundLogic.ApplyConcuss(_state, _settings, sender))
            {
                ApplyConcussionEffect(new UnityEngine.Vector3(x, y, z), sender);
                PushState();
            }
            else
            {
                _state.Players.TryGetValue(sender, out var cp);
                Core.Log.Msg($"[PropHunt] concussion from {sender} rejected (phase={_state.Phase}, used={(cp != null ? cp.ConcussUsed : -1)}/{_settings.ConcussCharges})");
            }
        }

        /// <summary>Host engine I/O: taze every hunter within the concussion radius of the given centre.</summary>
        private void ApplyConcussionEffect(UnityEngine.Vector3 center, ulong hiderId)
        {
            try
            {
                PlayerRegistry.Refresh();
                float r = _settings.ConcussRadius;
                int hit = 0;
                var list = Player.PlayerList;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var pl = list[i];
                    if (pl == null) continue;
                    if (RoleOf(PlayerRegistry.IdForPlayer(pl)) != PlayerRole.Hunter) continue;
                    if (UnityEngine.Vector3.Distance(center, pl.transform.position) > r) continue;
                    try { pl.Taze(); hit++; } catch { }
                }
                Core.Log.Msg($"[PropHunt] concussion by {hiderId} - tazed {hit} hunter(s) within {r}m.");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] concussion effect failed: " + e.Message); }
        }

        private void HandleClaimTag(ulong hunter, ulong victim)
        {
            if (!_isHost || _state.Phase != RoundPhase.Hunting) return;
            // host-side geometry re-validation - never trust the client's ray
            PlayerRegistry.Refresh();
            var hp = PlayerRegistry.Get(hunter);
            var vp = PlayerRegistry.Get(victim);
            if (hp != null && vp != null)
            {
                float dist = UnityEngine.Vector3.Distance(hp.transform.position, vp.transform.position);
                if (dist > _settings.TagRange + 2f) { Core.LogDebug($"[PropHunt] tag rejected: dist {dist:F1} > {_settings.TagRange}"); return; }
            }
            if (RoundLogic.ApplyCatch(_state, _settings, hunter, victim, NowUnix()))
            {
                if (RoundLogic.IsCaught(_state, victim)) Core.Log.Msg($"[PropHunt] {hunter} CAUGHT {victim} ({_settings.Caught}).");
                else Core.Log.Msg($"[PropHunt] {hunter} hit {victim} ({_state.Players[victim].Hits}/{_settings.HitsToCatch}).");
                PushState();
            }
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

        // ---- engine helpers ----

        private void RequestReturnToHub()
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
            _state.AreaRadius = _settings.PlayAreaRadius;
        }

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
            if (phase == RoundPhase.Lobby || phase == RoundPhase.MatchEnd) { frozen = false; blind = false; }

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
