#if SNITCH
using System;
using Snitch.Api;
using PropHunt.Game;      // GameModeController, PlayerRole, GameState
using PropHunt.Net;       // PropHuntNet
using PropHunt.Music;     // RoundMusicController

namespace PropHunt.Profiling
{
    /// <summary>
    /// DEBUG-only Snitch panel for PropHunt: live round/role/HP/net/music state, a few gauges, and host debug
    /// actions. The Snitch host auto-discovers <see cref="Register"/> on bind and ALSO forwards this panel into the
    /// Hotline overlay, so there is no direct Hotline dependency. Compiled only under SNITCH (Debug + EnableSnitch);
    /// the Release DLL contains zero Snitch types. Hot-path timing (PropHunt.Net / PropHunt.Round) is sampled in
    /// Core.OnUpdate. The actions double as a reliable in-game trigger for the phmusic/phprops dumps, since the
    /// deferred-PatchAll console prefix is unreliable (see DebugConsolePatch).
    /// </summary>
    internal static class SnitchProbe
    {
        public static void Register()
        {
            Panel p = Profiler.RegisterPanel("PropHunt", "PropHunt");

            // --- live status lines (polled on the main thread) ---
            p.Text(RoundLine);
            p.Text(LocalLine);
            p.Text(NetLine);
            p.Text(MusicLine);

            // --- gauges ---
            p.Counter("Members", () => PropHuntNet.MemberCount(), "players");
            p.Counter("Alive hiders", () => Core.Session?.AliveHiderCount ?? 0, "hiders");
            p.Counter("Seconds left", () => Core.Session?.SecondsLeft ?? 0, "s");

            // --- debug actions (host); usable from the Hotline overlay without the flaky console ---
            p.Action("Start match", () => Core.Session?.BeginMatch());
            p.Action("Next round", () => Core.Session?.BeginNextRound());
            p.Action("Dump props", () => Core.Session?.DumpPropDebug());
            p.Action("Dump music tracks", () => RoundMusicController.DumpTracks());

            p.Log();
        }

        private static string RoundLine()
        {
            var s = Core.Session;
            if (s == null) return "session: none (launch via Side Hustle -> PropHunt)";
            return $"phase={s.Phase}  round={s.State.RoundNumber}  you={s.LocalRole}  left={s.SecondsLeft}s  host={s.IsHost}  hiders={s.AliveHiderCount}";
        }

        private static string LocalLine()
        {
            var s = Core.Session;
            if (s == null) return "";
            if (s.LocalRole == PlayerRole.Hunter)
                return $"hunter: FF-HP={Math.Max(0, s.LocalHunterMaxHits - s.LocalHunterHits)}/{s.LocalHunterMaxHits}  downed={s.LocalDowned}";
            if (s.LocalRole == PlayerRole.Hider)
                return $"hider: prop={s.LocalPropName ?? "-"}  HP={Math.Max(0, s.LocalMaxHits - s.LocalHits)}/{s.LocalMaxHits}";
            return $"role={s.LocalRole}";
        }

        private static string NetLine() =>
            $"net: ready={PropHuntNet.Ready}  inLobby={PropHuntNet.InLobby}  isHost={PropHuntNet.IsHost}  members={PropHuntNet.MemberCount()}";

        // Surfaces the round-end music bug: the track WE enabled vs whether a higher-priority game track is masking it.
        private static string MusicLine() =>
            $"music: active='{RoundMusicController.Active}' (a higher-priority game track can still mask it - use Dump music tracks)";
    }
}
#endif
