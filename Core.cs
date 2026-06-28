using System;
using MelonLoader;
using PropHunt.Config;

[assembly: MelonInfo(typeof(PropHunt.Core), "PropHunt", "0.1.0", "DooDesch", null)]
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
                SideHustle.API.Register(new SideHustle.GamemodeDescriptor
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
                    SkipIntro = true,
                    ForceNewGame = true,
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
                });
                Log.Msg("[PropHunt] registered with Side Hustle.");
            }
            catch (Exception e)
            {
                Log.Warning("[PropHunt] Side Hustle not available; cannot register as a gamemode: " + e.Message);
            }
        }

        private static SideHustle.LaunchContext _session;
        private static Game.GameModeController _controller;
        private static bool _patched;              // gameplay Harmony patches applied (lazily, on the first gameplay scene)

        /// <summary>The live PropHunt session controller, or null when not in a session.</summary>
        internal static Game.GameModeController Session => _controller;

        private static void OnHostMultiplayer(SideHustle.LaunchContext ctx)
        {
            _session = ctx;
            Log.Msg($"[PropHunt] hosting via Side Hustle (lobby {ctx.LobbyId}, {ctx.PlayerCount} player(s)).");
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
                try { HarmonyInstance.PatchAll(); Log.Msg("[PropHunt] PatchAll completed."); }
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

            // Gameplay scene loaded. The gamemode controller, role assignment, and prop catalog are wired
            // here as those systems land (Phase 1).
            LogDebug("[PropHunt] Main scene loaded.");
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
#if DEBUG
            Disguise.PropCurator.Tick();   // prop curation tool (toggled by the phcurate console command)
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
            if (_controller != null) { UI.PropHuntHud.Draw(_controller); _controller.DrawGui(); }
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
        /// <summary>Fired when preferences are saved (incl. via the Mod Manager &amp; Phone App UI). Phase 0 debug probes.</summary>
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
