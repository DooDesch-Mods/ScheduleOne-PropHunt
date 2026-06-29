using System.Collections.Generic;
using UnityEngine;
using PropHunt.Game;
using PropHunt.Config;

namespace PropHunt.View
{
    /// <summary>
    /// Spectator camera for caught players (Spectator caught-behaviour) and late joiners waiting for the next round.
    /// Default = a FOLLOW-CAM behind a living player (cycle with [click]); [4] toggles to the game's built-in
    /// FREECAM and back. Purely local - the camera and PlayerMovement.CanMove are owner-side, not networked.
    /// Driven from the controller Tick via an Eliminated/Spectator watch, NOT ApplyLocalEffects (that change-gates
    /// on phase+role, which does not change when only Eliminated flips, so it would never re-fire here).
    /// </summary>
    internal sealed class SpectatorController
    {
        private readonly GameModeController _ctl;
        private bool _active;
        private bool _freecam;
        private int _cycle;
        private ulong _targetId;
        private float _nextRefresh;

        internal SpectatorController(GameModeController ctl) { _ctl = ctl; }

        internal bool Active => _active;
        internal string HudText { get; private set; }

        internal void Tick()
        {
            try
            {
                bool should = ShouldSpectate();
                if (should && !_active) Enter();
                else if (!should && _active) { Exit(); return; }
                if (!_active) { HudText = null; return; }

                if (Input.GetKeyDown(KeyBinds.SpectatorToggle)) ToggleMode();

                if (_freecam) { HudText = "FREECAM   [4] follow-cam"; return; }

                if (Time.time >= _nextRefresh) { _nextRefresh = Time.time + 1f; PlayerRegistry.Refresh(); }
                EnsureTarget();
                if (Input.GetKeyDown(KeyBinds.SpectatorNext)) CycleTarget();
                UpdateFollowCam();
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] spectator tick failed: " + e.Message); }
        }

        internal void ForceExit() { if (_active) Exit(); }

        private bool ShouldSpectate()
        {
            var phase = _ctl.Phase;
            if (phase != RoundPhase.Hiding && phase != RoundPhase.Hunting) return false;
            var ls = LocalState();
            if (ls == null) return false;
            if (ls.Role == PlayerRole.Spectator) return true;                 // late joiner / not yet assigned
            if (ls.Role == PlayerRole.Hider && ls.Eliminated) return true;    // caught in Spectator mode
            return false;
        }

        private void Enter()
        {
            _active = true;
            _freecam = false;
            _targetId = 0;
            SetCanMove(false);
            SetCanLook(false);
            PlayerRegistry.Refresh();
            EnsureTarget();
            SpectatorCam.Active = true;
            Core.LogDebug("[PropHunt] spectator: entered (follow-cam).");
        }

        private void Exit()
        {
            _active = false;
            SpectatorCam.Active = false;
            SpectatorCam.Target = null;
            if (_freecam) { SetFreeCam(false); _freecam = false; }
            SetCanLook(true);
            SetCanMove(true);
            HudText = null;
            Core.LogDebug("[PropHunt] spectator: exited.");
        }

        private void ToggleMode()
        {
            _freecam = !_freecam;
            if (_freecam)
            {
                SpectatorCam.Active = false;
                SetFreeCam(true);          // vanilla freecam freezes the body + handles fly input
            }
            else
            {
                SetFreeCam(false);
                SetCanMove(false);         // re-assert the freeze the follow-cam relies on
                SetCanLook(false);
                SpectatorCam.Active = true;
            }
        }

        private void EnsureTarget()
        {
            if (_targetId != 0 && IsAliveOther(_targetId)) return;
            var list = AliveOthers();
            _targetId = list.Count > 0 ? list[0] : 0;
            _cycle = 0;
        }

        private void CycleTarget()
        {
            var list = AliveOthers();
            if (list.Count == 0) { _targetId = 0; return; }
            _cycle = (_cycle + 1) % list.Count;
            _targetId = list[_cycle];
        }

        // alive players to spectate: hiders first (the interesting ones), then hunters; self excluded
        private List<ulong> AliveOthers()
        {
            var hiders = new List<ulong>();
            var hunters = new List<ulong>();
            ulong me = _ctl.LocalId;
            foreach (var kv in _ctl.State.Players)
            {
                var p = kv.Value;
                if (p.SteamId == me || p.Eliminated) continue;
                if (p.Role == PlayerRole.Hider) hiders.Add(p.SteamId);
                else if (p.Role == PlayerRole.Hunter) hunters.Add(p.SteamId);
            }
            hiders.Sort(); hunters.Sort();
            hiders.AddRange(hunters);
            return hiders;
        }

        private bool IsAliveOther(ulong id) =>
            id != _ctl.LocalId && _ctl.State.Players.TryGetValue(id, out var p) && !p.Eliminated &&
            (p.Role == PlayerRole.Hider || p.Role == PlayerRole.Hunter);

        private void UpdateFollowCam()
        {
            var gp = (_targetId != 0) ? PlayerRegistry.Get(_targetId) : null;
            bool lateJoin = LocalState()?.Role == PlayerRole.Spectator;
            string prefix = lateJoin ? "You join next round.  " : "Caught!  ";
            if (gp == null)
            {
                SpectatorCam.Target = null;
                HudText = prefix + "Spectating - waiting for a player...   [4] freecam";
                return;
            }
            SpectatorCam.Target = gp.transform;
            HudText = prefix + $"Spectating {SafeName(gp)}   [click] next   [4] freecam";
        }

        private PlayerState LocalState()
        {
            var id = _ctl.LocalId;
            return id != 0 && _ctl.State.Players.TryGetValue(id, out var p) ? p : null;
        }

        // ---- engine helpers (all owner-local, none networked) ----
        private static void SetCanMove(bool can) { try { var pm = PlayerSingleton<PlayerMovement>.Instance; if (pm != null) pm.CanMove = can; } catch { } }
        private static void SetCanLook(bool can) { try { var cam = PlayerSingleton<PlayerCamera>.Instance; if (cam != null) cam.SetCanLook(can); } catch { } }
        private static void SetFreeCam(bool on) { try { var cam = PlayerSingleton<PlayerCamera>.Instance; if (cam != null) cam.SetFreeCam(on, true); } catch { } }
        private static string SafeName(Player p) { try { var n = p.PlayerName; return string.IsNullOrEmpty(n) ? "a player" : n; } catch { return "a player"; } }
    }
}
