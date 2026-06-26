using UnityEngine;
using PropHunt.Game;
using PropHunt.Net;

namespace PropHunt.Taunt
{
    /// <summary>
    /// HOST schedules forced reveal taunts during the Hunting phase: every TauntIntervalSeconds it broadcasts
    /// a TauntMessage for each live hider and fires the local cue. Clients receive it via the P2P handler and
    /// call <see cref="GameModeController.NotifyTaunt"/>, which flashes the HUD. TODO(testing): replace the
    /// HUD flash with a positional reveal sound at the hider (embedded-WAV pattern from RVRepairVan) + a prop
    /// wobble, so the taunt is actually audible/visible in-world.
    /// </summary>
    internal sealed class TauntController
    {
        private readonly GameModeController _ctl;
        private float _nextTaunt;

        internal TauntController(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            if (!_ctl.IsHost) return;
            if (_ctl.Phase != RoundPhase.Hunting) { _nextTaunt = 0f; return; }
            int interval = _ctl.Settings.TauntIntervalSeconds;
            if (interval <= 0) return;
            if (_nextTaunt <= 0f) { _nextTaunt = Time.time + interval; return; }
            if (Time.time < _nextTaunt) return;
            _nextTaunt = Time.time + interval;

            foreach (var ps in _ctl.State.Players.Values)
            {
                if (ps.Role != PlayerRole.Hider || ps.Eliminated) continue;
                try { PropHuntNet.Client?.BroadcastMessage(new TauntMessage { SteamId = ps.SteamId }); } catch { }
                _ctl.NotifyTaunt(ps.SteamId);   // the host also "hears" it (BroadcastMessage doesn't self-send)
            }
            Core.LogDebug("[PropHunt] taunt fired.");
        }
    }
}
