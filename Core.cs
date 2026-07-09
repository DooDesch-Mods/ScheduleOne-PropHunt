using System;
using MelonLoader;
using PropHunt.Config;

[assembly: MelonInfo(typeof(PropHunt.Core), "PropHunt", "1.0.1", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-PropHunt")]
[assembly: MelonGame("TVGS", "Schedule I")]
[assembly: MelonOptionalDependencies("SideHustle")]   // PropHunt is launched from the Side Hustle gamemode hub

namespace PropHunt
{
    /// <summary>
    /// MelonLoader entry point for the PropHunt gamemode. Mirrors the sibling RVRepairVan mod:
    /// initialize preferences, patch Harmony, log status; per-scene setup runs on the "Main" scene.
    /// </summary>
    public sealed class Core : MelonMod
    {
        public static Core Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        /// <summary>Debug-only trace log - compiled out of Release builds so the release log stays clean.</summary>
        [System.Diagnostics.Conditional("DEBUG")]
        public static void LogDebug(string msg) { Log?.Msg(msg); }

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            PropHuntPreferences.Initialize();
            // Gameplay patches are applied lazily on the first gameplay scene (see OnSceneWasInitialized "Main"),
            // NOT here at load. Patching the game's gameplay methods while the Side Hustle hub builds its menu UI
            // intermittently hard-crashes the game, and these patches only do anything during an in-game round anyway.

            RegisterWithSideHustle();

            Log.Msg($"PropHunt initialized. Enabled={PropHuntPreferences.Enabled}");
        }

        /// <summary>
        /// Register PropHunt as a multiplayer + world gamemode in the Side Hustle hub. Wrapped in try/catch so
        /// PropHunt still loads cleanly if Side Hustle is not installed (it is an optional dependency). Registration
        /// is load-order independent. The launch callbacks bootstrap networking and the session; the full round
        /// machine (roles/disguise/catch) lands with the gameplay phase.
        /// </summary>
        private static void RegisterWithSideHustle()
        {
            try
            {
                _descriptor = new SideHustle.GamemodeDescriptor
                {
                    Id = "doodesch.prophunt",
                    DisplayName = "PropHunt",
                    Description = "Hiders disguise as props; hunters find them before the timer runs out.",
                    Author = "DooDesch",
                    Support = SideHustle.GamemodeSupport.Multiplayer,
                    Surface = SideHustle.GamemodeSurface.World,
                    OnHostMultiplayer = OnHostMultiplayer,
                    OnJoinMultiplayer = OnJoinMultiplayer,
                    OnExitToHub = OnExitToHub,
                    HostSettings = Config.PropHuntSettingsSpec.Build(),   // the round settings, shown + chosen on the host form
                    Presets = Config.RoundPresets.Build(),                // one-click presets (cascade into the form; still tweakable)
                    // Gamemode hygiene: a fresh world auto-starts tutorial/dealer quests + the intro cutscene, which
                    // create phone prompts, overlays + NPC waypoints that fight a PropHunt round and can drop players
                    // into a scripted RV sequence instead of the map. Opt out of all three (Side Hustle applies them).
                    BlockVanillaQuests = true,
                    AllowedQuestTitles = new[] { Quests.GuideQuest.Title },   // our own guide quest is exempt from the block
                    SkipIntro = true,
                    ForceNewGame = true,
                    // World hygiene Side Hustle enforces for the whole session (replaces PropHunt's own patches):
                    //  - no saving (scratch world; teleports/locked doors/disguises would corrupt a save),
                    //  - NPCs ignore hunter gunfire (so a hider can mimic them without derailing the schedule),
                    //  - no vanilla player death (elimination is ONLY the host-validated catch; vanilla death +
                    //    medical-centre respawn would eject the player out of the play area).
                    BlockSaveDuringSession = true,
                    SuppressNpcCombatReactions = true,
                    DisableVanillaPlayerDeath = true,
                    // Default the host form to "Required mods only" so everyone plays on an identical, clean set
                    // (fewer "we're all on different mods" desyncs). The host can still switch back before starting.
                    DefaultRequiredModsOnly = true,
                    // Keep PropHunt's session clean: disable unrelated gameplay/world mods that could interfere,
                    // but allow a few harmless ones to stay. PropHunt's own mod, S1API and the Side Hustle hub are
                    // always kept (essentials); SteamNetworkLib is a UserLib that is never disabled; and BiggerLobbies
                    // is kept automatically whenever it is loaded because PropHunt is a multiplayer gamemode - so none
                    // of those need to be listed (and listing them could falsely block the launch).
                    Policy = new SideHustle.ModPolicy
                    {
                        // Allowed to remain loaded - cosmetic or utility mods with no effect on the round.
                        AllowedMods = new[]
                        {
                            "Snitch",        // performance profiler - useful for tuning, no gameplay effect
                            "Inkorporated",  // custom tattoos (cosmetic)
                            "Inkubator"      // tattoo editor (cosmetic; only matched if the player has it installed)
                        }
                        // RequiredMods intentionally left empty: PropHunt's hard dependencies (S1API,
                        // SteamNetworkLib) are always present, and BiggerLobbies is optional - it raises the lobby
                        // cap when installed but PropHunt runs without it. Nothing here forces or blocks a mod.
                    }
                };
                SideHustle.API.Register(_descriptor);
                Log.Msg("[PropHunt] registered with Side Hustle.");
            }
            catch (Exception e)
            {
                Log.Warning("[PropHunt] Side Hustle not available; cannot register as a gamemode: " + e.Message);
            }
        }

        private static SideHustle.LaunchContext _session;
        private static SideHustle.GamemodeDescriptor _descriptor;   // our registered descriptor (its Presets are refreshed after a host)
        private static Game.GameModeController _controller;
        private static bool _patched;              // gameplay Harmony patches applied (lazily, on the first gameplay scene)

        /// <summary>The live PropHunt session controller, or null when not in a session.</summary>
        internal static Game.GameModeController Session => _controller;

        private static void OnHostMultiplayer(SideHustle.LaunchContext ctx)
        {
            _session = ctx;
            Log.Msg($"[PropHunt] hosting via Side Hustle (lobby {ctx.LobbyId}, {ctx.PlayerCount} player(s)).");
            // If the host tweaked a preset (mode == "Custom - <base>"), persist these settings as the Custom preset
            // so the form pre-selects them next time + refresh our preset list for any re-host this session.
            try
            {
                var mp = ctx.Multiplayer;
                if (mp != null && !string.IsNullOrEmpty(mp.Mode) && mp.Mode.StartsWith("Custom - ") && !string.IsNullOrEmpty(mp.ConfigBlob))
                {
                    PropHuntPreferences.SaveCustomPreset(mp.ConfigBlob, mp.Mode.Substring("Custom - ".Length));
                    if (_descriptor != null) _descriptor.Presets = Config.RoundPresets.Build();
                }
            }
            catch (Exception e) { Log.Warning("[PropHunt] save custom preset failed: " + e.Message); }
            // SteamNetworkLib auto-attaches to the game's Steam lobby (idempotent).
            Net.PropHuntNet.Initialize();
            _controller?.Dispose();
            _controller = new Game.GameModeController(ctx, isHost: true);
            _controller.StartAsHost();
        }

        private static void OnJoinMultiplayer(SideHustle.LaunchContext ctx)
        {
            _session = ctx;
            Log.Msg($"[PropHunt] joined via Side Hustle (lobby {ctx.LobbyId}, host {ctx.HostName ?? "?"}).");
            Net.PropHuntNet.Initialize();
            _controller?.Dispose();
            _controller = new Game.GameModeController(ctx, isHost: false);
            _controller.StartAsClient();
        }

        private static void OnExitToHub(SideHustle.LaunchContext ctx)
        {
            Log.Msg("[PropHunt] session ended; tearing down.");
            UI.Hud.HudController.Teardown();   // destroy the uGUI HUD canvas with the session
            _controller?.Dispose();
            _controller = null;
            _session = null;
        }

        /// <summary>
        /// Apply the gameplay patches lazily on the first gameplay scene (the crash fix - never at the menu, where
        /// patching gameplay methods alongside the Side Hustle hub's menu UI intermittently hard-crashes the game),
        /// and initialize networking early (menu + gameplay) so SteamNetworkLib's global lobby callback is live BEFORE
        /// the player enters any co-op lobby - via the Side Hustle browser OR the game's own co-op. If it inits only
        /// after the lobby is entered it misses the enter event and never attaches. (Net-init was never the crash
        /// cause - the patches were - so it stays early.)
        /// </summary>
        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            if (sceneName == "Main" && !_patched)
            {
                _patched = true;
                // Idempotency belt: the scratch-world boot can initialize "Main" twice, and PatchAll-ing twice applies
                // every postfix twice (it doubled the per-shot catch hit). Skip if Harmony already has our patches.
                bool already = false;
                try { foreach (var _ in HarmonyInstance.GetPatchedMethods()) { already = true; break; } } catch { }
                try
                {
                    if (already) Log.Msg("[PropHunt] gameplay patches already applied; skipping PatchAll.");
                    else { HarmonyInstance.PatchAll(); Log.Msg("[PropHunt] PatchAll completed."); }
                }
                catch (Exception e) { Log.Error("[PropHunt] PatchAll FAILED (later patches skipped): " + e); }
                try
                {
                    int n = 0;
                    foreach (var m in HarmonyInstance.GetPatchedMethods()) { n++; LogDebug($"[PropHunt] patched: {m.DeclaringType?.Name}.{m.Name}"); }
                    Log.Msg($"[PropHunt] patched method count: {n}");
                }
                catch { }
            }

            if (sceneName == "Menu" || sceneName == "Main")
            {
                Net.PropHuntNet.Initialize();
            }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            if (sceneName != "Main")
            {
                return;
            }

            // Gameplay scene loaded. The session controller, role assignment, and prop catalog are wired up
            // when a PropHunt session starts.
            LogDebug("[PropHunt] Main scene loaded.");

            // The item registry is only populated in a gameplay scene (not at the menu where the host form is built),
            // so discover the available weapons here + cache them for the host-form weapon dropdown. Rebuild the
            // descriptor's settings so the next time the host form opens it offers the full weapon list.
            try
            {
                if (Config.WeaponCatalog.RefreshFromRegistry() && _descriptor != null)
                    _descriptor.HostSettings = Config.PropHuntSettingsSpec.Build();
            }
            catch (Exception e) { Log.Warning("[PropHunt] weapon catalog refresh failed: " + e.Message); }
        }

        /// <summary>
        /// Leaving the gameplay scene ("Main" -> menu) ends the session, however it happened: the clean Side Hustle
        /// "return to hub" (OnExitToHub) OR the host quitting via the game's own pause menu / a client losing the host.
        /// Tear the HUD + session down here too so nothing lingers into the main menu (e.g. the status bar kept
        /// running). Idempotent: if OnExitToHub already disposed, _controller is null and this is a no-op.
        /// </summary>
        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            if (sceneName != "Main") return;
            try { UI.Hud.HudController.Teardown(); } catch { }
            if (_controller != null)
            {
                Log.Msg("[PropHunt] left the gameplay scene; tearing down the session.");
                try { _controller.Dispose(); } catch { }
                _controller = null;
                _session = null;
            }
        }

        public override void OnUpdate()
        {
#if DEBUG
            // Test harness only: launched into a plain co-op world (no Side Hustle menu), a non-host client
            // auto-joins a PropHunt session so the gamemode can be exercised headlessly. Release excludes this.
            if (_controller == null && Net.PropHuntNet.Ready && Net.PropHuntNet.InLobby && !Net.PropHuntNet.IsHost)
                DebugStartSession(false);
#endif
            // Pumps Steam callbacks + processes incoming PropHunt P2P. No-op until networking is ready.
#if SNITCH
            using (Snitch.Api.Profiler.Sample("PropHunt.Net")) Net.PropHuntNet.Tick();
            using (Snitch.Api.Profiler.Sample("PropHunt.Round")) _controller?.Tick(UnityEngine.Time.deltaTime);
#else
            Net.PropHuntNet.Tick();
            _controller?.Tick(UnityEngine.Time.deltaTime);
#endif
            Phone.PropHuntPhoneApp.Instance?.Tick();   // refresh the in-game phone app while it is open
            UI.Hud.HudController.Tick();   // build/refresh/teardown the in-game uGUI HUD with the session
#if DEBUG
            Disguise.PropCurator.Tick();   // prop curation tool (toggled by the phcurate console command)
            Disguise.CuratePreview.Tick();   // live on-player preview of the curated prop on other clients
            PropHunt.Debug.DebugOverlay.Tick();   // F3 toggles the visual diagnostics overlay
            PropHunt.Debug.DebugViz.Tick();        // in-world collision boxes (only while the overlay is on)
            PropHunt.Debug.SoundBrowser.Tick();    // sound audition browser (toggled by the phsounds command)
            PropHunt.Debug.MeshVaultBrowser.Tick();   // MeshVault prop browser (toggled by the phmesh command)
            PropHunt.Debug.SpawnEditor.Tick();     // safehouse spawn-point authoring (toggled by the phspawn command)
#endif
        }

        public override void OnLateUpdate()
        {
            // disguise prop transform upkeep runs AFTER the player has moved/rotated this frame, so a locked
            // prop doesn't lag-wiggle when looking around.
            _controller?.LateTick();
        }

        public override void OnGUI()
        {
            // The gameplay HUD is now a uGUI overlay (UI.Hud.HudController, ticked from OnUpdate). The only IMGUI left
            // is the radial taunt wheel, drawn here via the controller.
            if (_controller != null) _controller.DrawGui();
#if DEBUG
            Disguise.PropCurator.DrawGui();
            PropHunt.Debug.DebugOverlay.DrawGui();
            PropHunt.Debug.SoundBrowser.DrawGui();
            PropHunt.Debug.MeshVaultBrowser.DrawGui();
            PropHunt.Debug.SpawnEditor.DrawGui();
#endif
        }

#if DEBUG
        /// <summary>Test-only: spin up a PropHunt session in the current co-op world (bypasses the Side Hustle
        /// menu so the harness can drive it via console). ctx is null - ReturnToHub is null-guarded.</summary>
        internal static void DebugStartSession(bool isHost)
        {
            try
            {
                Net.PropHuntNet.Initialize();
                _controller?.Dispose();
                _controller = new Game.GameModeController(null, isHost);
                if (isHost) _controller.StartAsHost(); else _controller.StartAsClient();
                Log.Msg($"[PropHunt] DEBUG session started (isHost={isHost}).");
            }
            catch (Exception e) { Log.Warning("[PropHunt] DebugStartSession failed: " + e.Message); }
        }

        internal static void DebugStopSession()
        {
            _controller?.Dispose();
            _controller = null;
            Log.Msg("[PropHunt] DEBUG session stopped.");
        }
#endif

#if DEBUG
        /// <summary>Fired when preferences are saved (incl. via the Mod Manager &amp; Phone App UI). DEBUG-only probes.</summary>
        public override void OnPreferencesSaved()
        {
            try
            {
                if (PropHuntPreferences.ConsumeNetPingRequest())
                {
                    Net.PropHuntNet.Initialize();
                    Log.Msg($"[Debug] Net ping. Ready={Net.PropHuntNet.Ready} InLobby={Net.PropHuntNet.InLobby} IsHost={Net.PropHuntNet.IsHost}");
                    Net.PropHuntNet.SendPing();
                }

                if (PropHuntPreferences.ConsumeHideBodiesRequest())
                {
                    Log.Msg("[Debug] Disguise probe: toggling remote player body visibility.");
                    Disguise.DisguiseProbe.ToggleRemoteBodies();
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Prefs] OnPreferencesSaved failed: " + e.Message);
            }
        }
#endif
    }
}
