#if DEBUG
using System;
using HarmonyLib;

namespace PropHunt.Patches
{
    /// <summary>
    /// DEBUG-only console commands so the Phase-0 gates can be driven headlessly via the schedule1 MCP
    /// (run_console_command) in the local MP test harness, without the in-game settings UI:
    ///   phping    - broadcast a PHUNT_PING to all lobby members (watch the OTHER instance's log for "[Net] &lt;- PHUNT_PING")
    ///   phhide    - toggle every remote player's third-person body locally (disguise probe, gate 3)
    ///   phstatus  - log networking status (Ready / InLobby / IsHost / member count)
    /// Mirrors RVRepairVan's DebugConsolePatch. Compiled out of Release.
    /// </summary>
    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.SubmitCommand), new Type[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
    internal static class DebugConsolePatch
    {
        private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
        {
            try
            {
                if (args == null || args.Count == 0) return true;   // not ours - let the game handle it
                switch (args[0].ToLower())
                {
                    case "phping":
                        Net.PropHuntNet.SendPing();
                        return false;
                    case "phhide":
                        Disguise.DisguiseProbe.ToggleRemoteBodies();
                        return false;
                    case "phstatus":
                        Core.Log.Msg($"[PropHunt] status: Ready={Net.PropHuntNet.Ready} InLobby={Net.PropHuntNet.InLobby} " +
                                     $"IsHost={Net.PropHuntNet.IsHost} members={Net.PropHuntNet.MemberCount()}");
                        return false;
                    case "phhost":    // test: spin up a PropHunt session as host in the current co-op world
                        Core.DebugStartSession(true);
                        return false;
                    case "phjoin":    // test: spin up a PropHunt session as client
                        Core.DebugStartSession(false);
                        return false;
                    case "phstop":    // test: tear down the PropHunt session
                        Core.DebugStopSession();
                        return false;
                    case "phstart":   // host: begin a PropHunt match (stand-in for the setup-screen "Start")
                        if (Core.Session != null) Core.Session.BeginMatch();
                        else Core.Log.Warning("[PropHunt] phstart: no active session (run phhost first, or launch via Side Hustle).");
                        return false;
                    case "phprops":   // dump the prop pipeline: catalog size/hash, crosshair target, nearby becomable count
                        if (Core.Session != null) Core.Session.DumpPropDebug();
                        else Core.Log.Warning("[PropHunt] phprops: no active session.");
                        return false;
                    case "phstate":   // dump the local view of the round state
                        if (Core.Session != null)
                        {
                            var s = Core.Session;
                            Core.Log.Msg($"[PropHunt] state: phase={s.Phase} role={s.LocalRole} secondsLeft={s.SecondsLeft} " +
                                         $"players={s.State.Players.Count} isHost={s.IsHost}");
                        }
                        else Core.Log.Warning("[PropHunt] phstate: no active session.");
                        return false;
                    default:
                        return true;   // not one of ours
                }
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] console cmd failed: " + e.Message); return false; }
        }
    }
}
#endif
