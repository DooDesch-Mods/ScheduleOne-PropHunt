using PropHunt.Disguise;

namespace PropHunt.Game
{
    /// <summary>
    /// HOST-authoritative FORCED PROP ROTATION: every PropRotationSeconds during the hunt, every live hider is put
    /// into a NEW random prop from the host's pool. A hider who found one perfect spot can no longer sit in it for
    /// the whole round - the prop under them changes, so the spot stops fitting and they have to move.
    ///
    /// It runs on the same grid as the whistle (<see cref="RoundLogic.NextGridMark"/>) so the two never drift apart
    /// and a hider can anticipate both. The rotation is a FREE change: it resets prop HP and refills decoy and
    /// concussion charges like any prop change, but it does NOT spend the hider's own change budget - being moved by
    /// the host must never cost a player something they did not choose to use.
    /// </summary>
    internal sealed class PropRotationController
    {
        private readonly GameModeController _ctl;

        // unix timestamp (host time) of the next rotation; 0 = not armed
        private long _nextUnix;

        internal PropRotationController(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            if (!_ctl.IsHost) return;
            if (_ctl.Phase != RoundPhase.Hunting) { _nextUnix = 0; return; }

            // The round's frozen interval, not the live setting - see GameState.RotationSeconds.
            int interval = _ctl.State.RotationSeconds;
            if (interval <= 0) { _nextUnix = 0; return; }

            long now = _ctl.NowUnix();
            // Mark 0 is the start of the hunt, which is when everyone JUST picked their spot - rotating there would
            // be pointless, so the first rotation is one full interval in.
            if (_nextUnix <= 0) { _nextUnix = RoundLogic.NextGridMark(_ctl.HuntStartUnix, interval, _ctl.HuntStartUnix); return; }
            if (now < _nextUnix) return;
            _nextUnix = RoundLogic.NextGridMark(_ctl.HuntStartUnix, interval, now);

            int rotated = 0;
            foreach (var ps in _ctl.State.Players.Values)
            {
                if (ps.Role != PlayerRole.Hider || ps.Eliminated) continue;
                int next = PropCatalog.RandomId(ps.PropId);   // excludes the current one, so the change is visible
                if (next < 0) continue;
                if (!RoundLogic.ApplySelectProp(_ctl.State, ps.SteamId, next, _ctl.MaxHitsFor(next),
                                                _ctl.Settings.MaxPropChanges, freeChange: true)) continue;
                // Only the prop changes. PlayerState.Locked is the [F] yaw freeze, not a commitment to a prop -
                // touching it here would silently take the hider's facing control away.
                rotated++;
            }

            if (rotated <= 0) return;
            _ctl.PublishState();
            _ctl.AnnounceRotation();
            // Msg, not LogDebug: this yanks a prop out of someone's hands, and Release builds drop LogDebug - so when a
            // player reported the countdown missing there was no way to tell whether rotation had run at all.
            Core.Log.Msg($"[PropHunt] prop rotation: {rotated} hider(s) reshuffled, next in {interval}s.");
        }
    }
}
