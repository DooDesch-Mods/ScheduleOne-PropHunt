using System;
using MelonLoader;
using PropHunt.Config;

[assembly: MelonInfo(typeof(PropHunt.Core), "PropHunt", "0.1.0", "DooDesch", null)]
[assembly: MelonGame("TVGS", "Schedule I")]

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
            HarmonyInstance.PatchAll();

            Log.Msg($"PropHunt initialized. Enabled={PropHuntPreferences.Enabled}");
        }

        /// <summary>
        /// Initialize networking early (the Menu scene) so SteamNetworkLib's global LobbyEnter_t callback is
        /// live BEFORE the player joins a co-op lobby - letting it auto-attach to the game's own Steam lobby.
        /// </summary>
        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
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
            // Pumps Steam callbacks + processes incoming PropHunt P2P. No-op until networking is ready.
#if SNITCH
            using (Snitch.Api.Profiler.Sample("PropHunt.Net")) Net.PropHuntNet.Tick();
#else
            Net.PropHuntNet.Tick();
#endif
        }

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
