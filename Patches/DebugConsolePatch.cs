#if DEBUG
using System;
using HarmonyLib;

namespace PropHunt.Patches
{
    /// <summary>
    /// DEBUG-only console commands so PropHunt can be driven headlessly via the schedule1 MCP
    /// (run_console_command) in the local MP test harness, without the in-game settings UI:
    ///   phping    - broadcast a PHUNT_PING to all lobby members (watch the OTHER instance's log for "[Net] &lt;- PHUNT_PING")
    ///   phhide    - toggle every remote player's third-person body locally (disguise probe)
    ///   phstatus  - log networking status (Ready / InLobby / IsHost / member count)
    /// Mirrors RVRepairVan's DebugConsolePatch. Compiled out of Release.
    ///
    /// KNOWN LIMITATION: these commands don't currently fire. The real console call path
    /// (ConsoleUI.Submit / S1API ConsoleHelper) goes through SubmitCommand(string), and in this IL2CPP build
    /// the managed prefix that actually runs is the string one, not this List overload. The family fix is to
    /// patch BOTH overloads at OnInitializeMelon (as Litterally/Siesta/Snitch/Hotline do), but PropHunt
    /// deliberately DEFERS its PatchAll() to the first Main scene (the menu-crash fix). Patching the string
    /// overload that LATE - after the method has already been JIT-compiled/invoked - was observed to throw a
    /// HarmonyException "IL Compile Error" that aborts this mod's whole PatchAll(). The proper fix is to
    /// register the console prefixes EARLY (at init, separate from the deferred gameplay PatchAll), which is a
    /// larger change than this stays-broken stopgap. Left on List&lt;string&gt; until then.
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
                    case "phcurate":  // toggle the becomable-prop curation tool (step through every mesh, Keep/Skip)
                        Disguise.PropCurator.Toggle();
                        return false;
                    case "phcurateu":  // toggle curation over ONLY the still-unreviewed candidates
                        Disguise.PropCurator.ToggleUnreviewed();
                        return false;
                    case "phcuratekeep":  // re-review ONLY the currently-kept candidates (prune auto-seeded keeps)
                        Disguise.PropCurator.ToggleKept();
                        return false;
                    case "phcurateskip":  // review the SKIPPED scene props to RESCUE good ones the seed rejected
                        Disguise.PropCurator.ToggleSkipped();
                        return false;
                    case "phcuratebuild":  // review the BUILDABLES list (Registry whole-object prefabs: furniture/fixtures)
                        Disguise.PropCurator.ToggleBuildables();
                        return false;
                    case "phcuratevehicles":  // review the VEHICLES list (VehicleManager whole-car prefabs)
                        Disguise.PropCurator.ToggleVehicles();
                        return false;
                    case "phcurateworld":  // review the WORLD-OBJECTS list (ATM/vending/dumpster/storage as whole objects)
                        Disguise.PropCurator.ToggleWorld();
                        return false;
                    case "phcuratepool":  // review the EXACT becomable pool (the real runtime catalog: [2]-random + [E]-lookat source)
                        Disguise.PropCurator.ToggleCatalog();
                        return false;
                    case "phprobesources":  // log the Registry/VehicleManager runtime shape + LODGroup/MeshesToCull/boundingBox checks
                        Disguise.PropSources.Probe();
                        return false;
                    case "phdumpobj":  // dump a named world object's child mesh tree (diagnose proxy vs visual grouping)
                        Disguise.PropSources.DumpObject(args.Count > 1 ? args[1] : null);
                        return false;
                    case "phfindmesh":  // scan loaded Mesh ASSETS for un-batched originals matching a name (static-batch recovery probe)
                        Disguise.PropSources.FindMeshAssets(args.Count > 1 ? args[1] : null);
                        return false;
                    case "phmeshpool":  // measure the available clean-prop pool (scene vs prefab-template, bypassing batching)
                        Disguise.PropSources.MeshPool();
                        return false;
                    case "phdebug":   // toggle the visual diagnostics overlay (also F3)
                        PropHunt.Debug.DebugOverlay.ToggleFromConsole();
                        return false;
                    case "phcuratelist":  // dump the full candidate list to the log (index, name, LOD, size, verts, mats, decision)
                        Disguise.PropCatalog.DumpCandidates();
                        return false;
                    case "phcurateseed":  // seed the allowlist from the heuristic (Keep heuristic-accepted, Skip the rest) to refine
                    {
                        int kept = Disguise.PropCatalog.SeedCurationFromHeuristic();
                        Core.Log.Msg($"[PropHunt] phcurateseed: allowlist seeded - {kept} kept (heuristic baseline), rest skipped. " +
                                     "Run phcurate to refine, then start a new round to rebuild the catalog.");
                        return false;
                    }
                    case "phsounds":   // toggle the in-game sound browser: scroll + HEAR every loaded clip, pick a taunt
                        PropHunt.Debug.SoundBrowser.Toggle();
                        return false;
                    case "phmusic":    // dump every loaded MusicTrack name (to pick fitting Hiding/Hunting round tracks)
                        PropHunt.Music.RoundMusicController.DumpTracks();
                        return false;
                    case "phmesh":     // toggle the MeshVault prop browser (turntable iterate, needs MeshVault installed)
                        PropHunt.Debug.MeshVaultBrowser.Toggle();
                        return false;
                    case "phsafehouse":  // dump every loaded property (code/name/size/spawn) to calibrate the safehouse selector
                        Game.SafehouseSelector.DumpProperties();
                        return false;
                    case "phspawn":      // toggle the in-game safehouse spawn-point authoring editor (doors forced open)
                        PropHunt.Debug.SpawnEditor.Toggle();
                        return false;
                    case "phnextround":  // host: from the Safehouse lobby, start the next round (stand-in for the UI button)
                        if (Core.Session != null) Core.Session.BeginNextRound();
                        else Core.Log.Warning("[PropHunt] phnextround: no active session.");
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
