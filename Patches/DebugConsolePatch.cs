#if DEBUG
using System;
using HarmonyLib;
using UnityEngine;
using Il2CppScheduleOne.PlayerScripts;

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
                // The game submits each console entry twice in one frame; run a ph command once per frame and text.
                if (args[0].StartsWith("ph", StringComparison.OrdinalIgnoreCase))
                {
                    var sig = string.Join(" ", ToArray(args));
                    if (UnityEngine.Time.frameCount == _lastFrame && sig == _lastSig) return false;
                    _lastFrame = UnityEngine.Time.frameCount;
                    _lastSig = sig;
                }
                switch (args[0].ToLower())
                {
                    case "phknock":       // phknock [fix|legacy] [speed] [dx] [dz] [seconds] [free] [lift]: knock the local player down, log travel distance
                    {                     // free: 0 = as is, 1 = character controller off (as in a round), 2 = also the capsule off; lift: upward share of the shove
                        Game.GameModeController.DebugKnockLift = Arg(args, 7, -1f);
                        Game.GameModeController.DebugLegacyKnock = args.Count > 1 && args[1].ToLower() == "legacy";
                        Game.GameModeController.DebugKnockSpeed = Arg(args, 2, -1f);
                        Game.GameModeController.DebugKnockFree = (int)Arg(args, 6, 1f);
                        Game.GameModeController.DebugKnock(Arg(args, 3, 1f), Arg(args, 4, 0f), Arg(args, 5, 3f));
                        return false;
                    }
                    case "phfire":        // hunter: fire one catch shot at another player's prop hitbox (or body) - phfire [name]
                        DebugShots.Fire(args.Count > 1 ? args[1] : "");
                        return false;
                    case "phconcuss":     // hider: set off the concussion at the local player's position
                        if (Core.Session == null) { Core.Log.Warning("phconcuss: no active session."); return false; }
                        Core.Session.RequestConcuss();
                        Core.Log.Msg("phconcuss: requested.");
                        return false;
                    case "phprobe":       // per player: role, prop, clone, avatar layer, mesh visibility, ragdoll, downed
                        DebugShots.Probe();
                        return false;
                    case "phping":
                        Net.PropHuntNet.SendPing();
                        return false;
                    case "phhide":
                        Disguise.DisguiseProbe.ToggleRemoteBodies();
                        return false;
                    case "phstatus":
                        Core.Log.Msg($"status: Ready={Net.PropHuntNet.Ready} InLobby={Net.PropHuntNet.InLobby} " +
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
                        else Core.Log.Warning("phstart: no active session (run phhost first, or launch via Side Hustle).");
                        return false;
                    case "phprops":   // dump the prop pipeline: catalog size/hash, crosshair target, nearby becomable count
                        if (Core.Session != null) Core.Session.DumpPropDebug();
                        else Core.Log.Warning("phprops: no active session.");
                        return false;
                    case "phcurate":  // toggle the becomable-prop curation tool (step through every mesh, Keep/Skip)
                        Disguise.PropCurator.Toggle();
                        return false;
                    case "phcurateu":  // toggle curation over ONLY the still-unreviewed candidates
                        Disguise.PropCurator.ToggleUnreviewed();
                        return false;
                    case "phcuratekeep":  // re-review the kept candidates you can SEE here ("all" also lists the not-loaded ones)
                        Disguise.PropCurator.ToggleKept(args.Count > 1 &&
                            string.Equals(args[1], "all", StringComparison.OrdinalIgnoreCase));
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
                    case "phscale":   // dump prop world size vs the local player's capsule (is the body prop-sized?)
                        PropHunt.Debug.SizeProbe.DumpScale();
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
                        Core.Log.Msg($"phcurateseed: allowlist seeded - {kept} kept (heuristic baseline), rest skipped. " +
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
                        else Core.Log.Warning("phnextround: no active session.");
                        return false;
                    case "phstate":   // dump the local view of the round state
                        if (Core.Session != null)
                        {
                            var s = Core.Session;
                            Core.Log.Msg($"state: phase={s.Phase} role={s.LocalRole} secondsLeft={s.SecondsLeft} " +
                                         $"players={s.State.Players.Count} isHost={s.IsHost}");
                        }
                        else Core.Log.Warning("phstate: no active session.");
                        return false;
                    case "phclockskew":   // fake a local clock that is N seconds off, to prove the host-clock correction
                    {
                        // Two instances on one machine share a system clock, so a real skew can never happen here.
                        // This bends the LOCAL reading only; the offset learned from the host must cancel it exactly,
                        // leaving every countdown identical on both windows.
                        long skew = 0;
                        if (args.Count > 1) long.TryParse(args[1], out skew);
                        Game.GameModeController.DebugClockSkew = skew;
                        var sess = Core.Session;
                        Core.Log.Msg($"phclockskew: local clock bent by {skew}s. " +
                                     (sess != null
                                        ? $"offset={sess.ClockOffset}s secondsLeft={sess.SecondsLeft} whistleIn={sess.SecondsToWhistle}"
                                        : "no active session yet - the offset is learned from the host's next push."));
                        return false;
                    }
                    case "phcombat":      // read the layer mask / trigger settings the vanilla shot pipeline runs on
                        PropHunt.Debug.CombatProbe.Dump();
                        return false;
                    case "phsolo":        // run a match with one real player (a stand-in fills the hunter slot)
                        Game.SoloHarness.StartSolo();
                        return false;
                    case "phbecome":      // force the local player into a prop by name/key (empty = random)
                        Game.SoloHarness.Become(args.Count > 1 ? args[1] : "");
                        return false;
                    case "phbecomeall":   // build EVERY catalog prop as a clone and report the ones that break
                        Game.SoloHarness.BuildAll();
                        return false;
                    case "phphone":       // build every phone tab into a throwaway container and report throws
                        Game.SoloHarness.PhoneSelfTest();
                        return false;
                    case "phmapring":     // report the play-area ring on the phone map (existence + coordinates)
                        Game.SoloHarness.MapRingReport();
                        return false;
                    case "phpropcheck":   // measure which props would float/sink when worn (e.g. phpropcheck toilet)
                        Disguise.PropGeometryCheck.Run(args.Count > 1 ? args[1] : "");
                        return false;
                    case "phset":         // host: change one round setting live, by its blob key (e.g. phset proprot 20)
                    {
                        if (args.Count < 3) { Core.Log.Warning("phset: usage 'phset <key> <value>' (keys are the RoundSettings blob keys)."); return false; }
                        var sess = Core.Session;
                        if (sess == null) { Core.Log.Warning("phset: no active session."); return false; }
                        sess.SetSetting(args[1], args[2]);
                        Core.Log.Msg($"phset: {args[1]}={args[2]} -> {sess.Settings}");
                        return false;
                    }
                    case "phwater":       // what the water probe sees here (layer, collider, surface height, depth)
                        PropHunt.PlayArea.WaterProbe.Dump();
                        return false;
                    case "phclock":       // compare this machine's clock against the host's
                        if (Core.Session != null)
                        {
                            var s = Core.Session;
                            Core.Log.Msg($"clock: offset={s.ClockOffset}s hostNow={s.NowUnix()} " +
                                         $"secondsLeft={s.SecondsLeft} whistleIn={s.SecondsToWhistle} huntStart={s.HuntStartUnix}");
                        }
                        else Core.Log.Warning("phclock: no active session.");
                        return false;
                    default:
                        return true;   // not one of ours
                }
            }
            catch (Exception e) { Core.Log.Warning("console cmd failed: " + e.Message); return false; }
        }

        private static int _lastFrame = -1;
        private static string _lastSig;

        private static string[] ToArray(Il2CppSystem.Collections.Generic.List<string> args)
        {
            var a = new string[args.Count];
            for (int i = 0; i < a.Length; i++) a[i] = args[i];
            return a;
        }

        private static float Arg(Il2CppSystem.Collections.Generic.List<string> args, int i, float fallback) =>
            args.Count > i && float.TryParse(args[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    /// <summary>Scripted stand-ins for a hunter's aimed shot and a state dump, for the co-op test scripts.</summary>
    internal static class DebugShots
    {
        /// <summary>Aim the next catch shot at the named player (empty = the first other player) and resolve it
        /// through the normal catch sweep, so line of sight, hitbox and the host claim all run as for a real shot.</summary>
        internal static void Fire(string name)
        {
            var ctl = Core.Session;
            if (ctl == null) { Core.Log.Warning("phfire: no active session."); return; }
            var target = FindOther(name);
            if (target == null) { Core.Log.Warning($"phfire: no other player matching '{name}'."); return; }
            ulong id = Game.PlayerRegistry.IdForPlayer(target);
            Vector3 aim;
            string via;
            var hitbox = GameObject.Find("ph_prop_" + id);
            var col = hitbox != null ? hitbox.GetComponentInChildren<Collider>() : null;
            if (col != null) { aim = col.bounds.center; via = "prop hitbox"; }
            else { aim = target.Avatar != null ? target.Avatar.CenterPoint : target.transform.position + Vector3.up; via = "body"; }
            var lp = Player.Local;
            float dist = lp != null ? Vector3.Distance(lp.transform.position, aim) : -1f;
            Core.Log.Msg($"phfire: shot at {target.PlayerName} ({id}) via {via}, {dist:0.0} m, phase={ctl.Phase} role={ctl.LocalRole}.");
            Catch.CatchController.DebugAimPoint = aim;
            ctl.OnLocalHunterFired(150f);
            Catch.CatchController.DebugAimPoint = null;
        }

        internal static void Probe()
        {
            var ctl = Core.Session;
            var list = Player.PlayerList;
            if (list == null) { Core.Log.Warning("phprobe: no player list."); return; }
            Core.Log.Msg($"phprobe: {list.Count} players, phase={(ctl != null ? ctl.Phase.ToString() : "no session")}.");
            for (int i = 0; i < list.Count; i++)
            {
                var pl = list[i];
                if (pl == null) continue;
                ulong id = Game.PlayerRegistry.IdForPlayer(pl);
                Game.PlayerState st = null;
                if (ctl != null) ctl.State.Players.TryGetValue(id, out st);
                string layer = "?";
                bool ragdolled = false;
                try
                {
                    if (pl.Avatar != null) { layer = LayerMask.LayerToName(pl.Avatar.gameObject.layer); ragdolled = pl.Avatar.Ragdolled; }
                }
                catch (Exception e) { Core.Log.Warning("phprobe: avatar read failed: " + e.Message); }
                Core.Log.Msg($"phprobe: {pl.PlayerName} id={id} local={pl.IsOwner} role={(ctl != null ? ctl.RoleOf(id).ToString() : "-")} " +
                             $"prop={(st != null ? st.PropId : -99)} clone={GameObject.Find("ph_prop_" + id) != null} layer={layer} " +
                             $"meshesVisible={pl.ThirdPersonMeshesVisibleToLocalPlayer} ragdolled={ragdolled} " +
                             $"downed={(st != null && st.Downed)} eliminated={(st != null && st.Eliminated)} " +
                             $"pos=({pl.transform.position.x:0.0},{pl.transform.position.y:0.0},{pl.transform.position.z:0.0})");
            }
        }

        private static Player FindOther(string name)
        {
            var list = Player.PlayerList;
            if (list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var pl = list[i];
                if (pl == null || pl.IsOwner) continue;
                if (string.IsNullOrEmpty(name) || (pl.PlayerName != null && pl.PlayerName.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)) return pl;
            }
            return null;
        }
    }
}
#endif
