using System;
using System.Collections.Generic;
using UnityEngine;
using PropHunt.Game;
using PropHunt.Net;

namespace PropHunt.Taunt
{
    /// <summary>
    /// HOST-authoritative GLOBAL WHISTLE (CoD WWII style): every TauntIntervalSeconds during the Hunting phase
    /// every live hider is forced to emit their taunt sound, ONE AFTER ANOTHER (staggered by WhistleStaggerSeconds)
    /// so hunters hear a sweep of distinct, spatially-separated reveals. The whistle runs off a unix deadline (like
    /// the phase timer) so a "whistle in Ns" countdown is displayable. Each hider's reveal broadcasts a TauntMessage
    /// (IsWhistle = true -> clients play it at reduced volume via <see cref="GameModeController.NotifyTaunt"/>).
    /// Manual taunts ([1]) are a separate, full-volume path and do not touch this timer.
    /// </summary>
    internal sealed class TauntController
    {
        private readonly GameModeController _ctl;

        // unix timestamp of the next whistle (0 = not armed; armed on the first Hunting tick)
        private long _nextWhistleUnix;

        // pending staggered reveals: each fires when Time.time >= at (sub-second spacing the unix clock can't give)
        private readonly List<StaggerEntry> _staggerQueue = new List<StaggerEntry>();

        private struct StaggerEntry { public float At; public ulong Id; public string Sound; }

        internal TauntController(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            if (!_ctl.IsHost) return;

            // 1) drain any due staggered reveals first (sweep the props one by one)
            float now = Time.time;
            for (int i = _staggerQueue.Count - 1; i >= 0; i--)
            {
                if (now < _staggerQueue[i].At) continue;
                var e = _staggerQueue[i];
                _staggerQueue.RemoveAt(i);
                // only reveal hiders still live (a hider caught between scheduling and firing is skipped)
                if (!IsLiveHider(e.Id)) continue;
                try { PropHuntNet.Client?.BroadcastMessage(new TauntMessage { SteamId = e.Id, Sound = e.Sound, IsWhistle = true }); } catch { }
                _ctl.NotifyTaunt(e.Id, e.Sound, isWhistle: true);   // host also "hears" it (Broadcast doesn't self-send)
            }

            if (_ctl.Phase != RoundPhase.Hunting) { _nextWhistleUnix = 0; _staggerQueue.Clear(); return; }

            int interval = _ctl.Settings.TauntIntervalSeconds;
            if (interval <= 0) return;

            long unixNow = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            // arm on the first Hunting tick
            if (_nextWhistleUnix <= 0) { _nextWhistleUnix = unixNow + interval; return; }
            if (unixNow < _nextWhistleUnix) return;

            // advance the deadline BEFORE building the queue so a slow frame can't double-fire
            _nextWhistleUnix = unixNow + interval;

            // build the sweep: one staggered reveal per live hider
            float stagger = Mathf.Max(0f, _ctl.Settings.WhistleStaggerSeconds);
            float offset = 0f;
            int count = 0;
            foreach (var ps in _ctl.State.Players.Values)
            {
                if (ps.Role != PlayerRole.Hider || ps.Eliminated) continue;
                _staggerQueue.Add(new StaggerEntry { At = now + offset, Id = ps.SteamId, Sound = TauntSounds.PickDefault() });
                offset += stagger;
                count++;
            }
            Core.LogDebug($"[PropHunt] whistle fired: {count} hider(s), stagger={stagger}s, next in {interval}s.");
        }

        private bool IsLiveHider(ulong id) =>
            _ctl.State.Players.TryGetValue(id, out var p) && p.Role == PlayerRole.Hider && !p.Eliminated;
    }
}
