using MelonLoader;
using UnityEngine;

namespace PropHunt.Config
{
    /// <summary>
    /// MelonPreferences wrapper. The category identifier is prefixed with the mod name
    /// ("PropHunt_...") so it is auto-detected by the "Mod Manager &amp; Phone App" settings UI (Prowiler).
    /// </summary>
    internal static class PropHuntPreferences
    {
        private const string CategoryId = "PropHunt_01_Main";

        private static MelonPreferences_Category _category;
        private static MelonPreferences_Entry<bool> _enabled;
        private static MelonPreferences_Entry<int> _hideSeconds;
        private static MelonPreferences_Entry<int> _huntSeconds;
        private static MelonPreferences_Entry<int> _roundEndSeconds;
        private static MelonPreferences_Entry<int> _playersPerHunter;
        private static MelonPreferences_Entry<int> _roundsBeforeSwap;
        private static MelonPreferences_Entry<float> _tagRange;
        private static MelonPreferences_Entry<int> _tauntIntervalSeconds;
        private static MelonPreferences_Entry<float> _playAreaRadius;
#if DEBUG
        private static MelonPreferences_Entry<bool> _startRoundDebug;
        private static MelonPreferences_Entry<bool> _netPingDebug;
        private static MelonPreferences_Entry<bool> _hideBodiesDebug;
#endif

        internal static void Initialize()
        {
            if (_category != null)
            {
                return;
            }

            _category = MelonPreferences.CreateCategory(CategoryId, "PropHunt");
            _enabled = CreateEntry("Enabled", true, "Enabled", "Enable the PropHunt gamemode.");
            _hideSeconds = CreateEntry("HideSeconds", 30, "Hiding phase (seconds)",
                "How long hiders have to find a prop and hide while hunters are frozen.");
            _huntSeconds = CreateEntry("HuntSeconds", 300, "Hunting phase (seconds)",
                "How long hunters have to find every hider before the hiders win.");
            _roundEndSeconds = CreateEntry("RoundEndSeconds", 8, "Round-end display (seconds)",
                "How long the result is shown before the next round begins.");
            _playersPerHunter = CreateEntry("PlayersPerHunter", 5, "Players per hunter",
                "Roughly one hunter is assigned for this many players (min one hunter). e.g. 5 = 1 hunter at 2-5 players, 2 at 6-10.");
            _roundsBeforeSwap = CreateEntry("RoundsBeforeSwap", 1, "Rounds before role swap",
                "How many rounds to play before rotating who hunts (round-robin).");
            _tagRange = CreateEntry("TagRange", 4f, "Catch range (metres)",
                "How close a hunter must be, looking at a hider, to catch them.");
            _tauntIntervalSeconds = CreateEntry("TauntIntervalSeconds", 30, "Taunt interval (seconds)",
                "During hunting, every hider is forced to emit a reveal sound this often. 0 disables taunts.");
            _playAreaRadius = CreateEntry("PlayAreaRadius", 75f, "Play-area radius (metres)",
                "Radius of the round's play area around the host's position. Leaving it warns, then eliminates.");
#if DEBUG
            _startRoundDebug = CreateEntry("StartRoundDebug", false, "Start round (debug, one-shot)",
                "Toggle ON (as host, in a co-op session) to force-start a PropHunt round now. Auto-resets to OFF.");
            _netPingDebug = CreateEntry("NetPingDebug", false, "Net ping (debug, one-shot)",
                "Toggle ON in a co-op session to send a PropHunt network ping and watch the other machine's log. Auto-resets.");
            _hideBodiesDebug = CreateEntry("HideBodiesDebug", false, "Probe: hide remote bodies (debug, one-shot)",
                "Toggle in a co-op session to hide/show every OTHER player's body locally (disguise probe). Auto-resets.");
#endif
        }

        private static MelonPreferences_Entry<T> CreateEntry<T>(string identifier, T defaultValue, string displayName, string description = null)
        {
            return _category.CreateEntry(identifier, defaultValue, displayName, description);
        }

        internal static bool Enabled => _enabled?.Value ?? true;
        internal static int HideSeconds => Mathf.Max(1, _hideSeconds?.Value ?? 30);
        internal static int HuntSeconds => Mathf.Max(1, _huntSeconds?.Value ?? 300);
        internal static int RoundEndSeconds => Mathf.Max(1, _roundEndSeconds?.Value ?? 8);
        internal static int PlayersPerHunter => Mathf.Max(1, _playersPerHunter?.Value ?? 5);
        internal static int RoundsBeforeSwap => Mathf.Max(1, _roundsBeforeSwap?.Value ?? 1);
        internal static float TagRange => Mathf.Max(0.5f, _tagRange?.Value ?? 4f);
        internal static int TauntIntervalSeconds => Mathf.Max(0, _tauntIntervalSeconds?.Value ?? 30);
        internal static float PlayAreaRadius => Mathf.Max(5f, _playAreaRadius?.Value ?? 75f);

#if DEBUG
        /// <summary>One-shot: true once if the debug "Start round" toggle is on, then resets it (in-memory, to avoid a save -> OnPreferencesSaved recursion).</summary>
        internal static bool ConsumeStartRoundRequest()
        {
            if (_startRoundDebug != null && _startRoundDebug.Value)
            {
                _startRoundDebug.Value = false;
                return true;
            }
            return false;
        }

        /// <summary>One-shot: true once if the debug "Net ping" toggle is on, then resets it.</summary>
        internal static bool ConsumeNetPingRequest()
        {
            if (_netPingDebug != null && _netPingDebug.Value)
            {
                _netPingDebug.Value = false;
                return true;
            }
            return false;
        }

        /// <summary>One-shot: true once if the debug "Hide remote bodies" toggle is on, then resets it.</summary>
        internal static bool ConsumeHideBodiesRequest()
        {
            if (_hideBodiesDebug != null && _hideBodiesDebug.Value)
            {
                _hideBodiesDebug.Value = false;
                return true;
            }
            return false;
        }
#endif
    }
}
