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
        private static MelonPreferences_Entry<int> _hitsToCatch;
        private static MelonPreferences_Entry<int> _maxPropChanges;
        private static MelonPreferences_Entry<int> _maxDecoys;
        private static MelonPreferences_Entry<int> _concussCharges;
        private static MelonPreferences_Entry<float> _concussRadius;
        private static MelonPreferences_Entry<int> _tauntIntervalSeconds;
        private static MelonPreferences_Entry<float> _playAreaRadius;
        private static MelonPreferences_Entry<string> _caughtBehavior;
        private static MelonPreferences_Entry<string> _roundStructure;
        private static MelonPreferences_Entry<int> _timeOfDay;
        private static MelonPreferences_Entry<string> _hunterWeapon;
        private static MelonPreferences_Entry<bool> _friendlyFire;
        private static MelonPreferences_Entry<int> _hunterHitsToDown;
        private static MelonPreferences_Entry<float> _hunterDownBaseSeconds;
        private static MelonPreferences_Entry<float> _hunterDownMaxSeconds;
        private static MelonPreferences_Entry<float> _concussStunSeconds;
        private static MelonPreferences_Entry<bool> _removeDecoysBetweenRounds;
        private static MelonPreferences_Entry<bool> _allowRandomChange;
        private static MelonPreferences_Entry<bool> _freeChangesInHiding;
        private static MelonPreferences_Entry<bool> _freezeTime;
        private static MelonPreferences_Entry<bool> _autoStartNextRound;
        private static MelonPreferences_Entry<string> _musicTrack;
        private static MelonPreferences_Entry<string> _customBlob;
        private static MelonPreferences_Entry<string> _customBase;
        private static MelonPreferences_Entry<string> _weaponCache;
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
            _roundEndSeconds = CreateEntry("RoundEndSeconds", 15, "Scoreboard time (seconds)",
                "How long the round-end scoreboard is shown before the safehouse (5-60).");
            _playersPerHunter = CreateEntry("PlayersPerHunter", 5, "Players per hunter",
                "Roughly one hunter is assigned for this many players (min one hunter). e.g. 5 = 1 hunter at 2-5 players, 2 at 6-10.");
            _roundsBeforeSwap = CreateEntry("RoundsBeforeSwap", 1, "Rounds before role swap",
                "How many rounds to play before rotating who hunts (round-robin).");
            _tagRange = CreateEntry("TagRange", 4f, "Catch range (metres)",
                "How close a hunter must be, looking at a hider, to catch them.");
            _hitsToCatch = CreateEntry("HitsToCatch", 2, "Prop HP per metre",
                "Disguise HP scales with prop size: a prop needs round(largest dimension * this) hits to catch (clamped 1-25). Bigger props tank far more shots.");
            _maxPropChanges = CreateEntry("MaxPropChanges", 5, "Max prop changes per round",
                "How many times a hider may (re)pick a prop each round. Each change resets their HP. 0 = unlimited.");
            _maxDecoys = CreateEntry("MaxDecoys", 4, "Decoys per prop",
                "How many decoys ([Q]) a hider may drop per prop (refills when they change prop) - static copies to mislead hunters. 0 = disabled.");
            _concussCharges = CreateEntry("ConcussCharges", 1, "Concussions per prop",
                "How many concussions ([G]) a hider may use per prop (refills when they change prop, like CoD Prop Hunt) - stuns nearby hunters. 0 = disabled.");
            _concussRadius = CreateEntry("ConcussRadius", 7f, "Concussion radius (metres)",
                "Hunters within this distance of the hider when they trigger a concussion get stunned.");
            _tauntIntervalSeconds = CreateEntry("TauntIntervalSeconds", 30, "Taunt interval (seconds)",
                "During hunting, every hider is forced to emit a reveal sound this often. 0 disables taunts.");
            _playAreaRadius = CreateEntry("PlayAreaRadius", 75f, "Play-area radius (metres)",
                "Radius of the round's play area around the host's position. Leaving it warns, then eliminates.");
            _caughtBehavior = CreateEntry("CaughtBehavior", "Spectator", "Caught behavior (default)",
                "Default for the host setup screen. Spectator = a caught hider sits out till the round ends. Infection = a caught hider becomes a hunter.");
            _roundStructure = CreateEntry("RoundStructure", "Continuous", "Round structure (default)",
                "Default for the host setup screen. Continuous = auto-start the next round with swapped roles. Single = one round, then back to the hub.");
            _timeOfDay = CreateEntry("TimeOfDay", 1200, "Time of day (HHMM)",
                "World time locked during a round (progression frozen). 1200 = noon/day, 0100 = night.");
            _hunterWeapon = CreateEntry("HunterWeapon", "pumpshotgun", "Hunter weapon",
                "Item id given to each hunter at hunt start (e.g. pumpshotgun, m1911, machete). Empty = none.");
            _friendlyFire = CreateEntry("FriendlyFire", true, "Friendly fire (hunters)",
                "Whether hunters can knock each other down with friendly fire (ragdoll, never kill).");
            _hunterHitsToDown = CreateEntry("HunterHitsToDown", 3, "Friendly hits to down",
                "Friendly-fire hits a hunter takes before being knocked down (their HP).");
            _hunterDownBaseSeconds = CreateEntry("HunterDownBaseSeconds", 3f, "Knockdown time (seconds)",
                "How long a hunter is ragdolled when first knocked down.");
            _hunterDownMaxSeconds = CreateEntry("HunterDownMaxSeconds", 10f, "Max knockdown time (seconds)",
                "Cap on ragdoll time; each extra hit while down extends toward this.");
            _concussStunSeconds = CreateEntry("ConcussStunSeconds", 2f, "Concussion stun time (seconds)",
                "How long a concussion ([G]) knocks nearby hunters down (a short stun).");
            _removeDecoysBetweenRounds = CreateEntry("RemoveDecoysBetweenRounds", true, "Clear decoys between rounds",
                "Remove every dropped decoy at the end of a round. Off = decoys carry over into the next round.");
            _allowRandomChange = CreateEntry("AllowRandomChange", true, "Allow random prop ([2])",
                "Let hiders press [2] to become a random prop.");
            _freeChangesInHiding = CreateEntry("FreeChangesInHiding", true, "Free changes while hiding",
                "Prop changes during the hiding phase are unlimited; the Max-prop-changes limit applies only during the hunt.");
            _freezeTime = CreateEntry("FreezeTime", true, "Freeze time of day",
                "Lock the world clock during a round. Off = set the time at round start, then let it progress.");
            _autoStartNextRound = CreateEntry("AutoStartNextRound", true, "Auto-start next round",
                "After a round, automatically start the next one after a short safehouse pause. Off = the host starts each round manually. Can be toggled live in the phone app.");
            _musicTrack = CreateEntry("MusicTrack", "Sneak Ambience", "Round music track",
                "The single game music track PropHunt plays continuously through every phase EXCEPT the hunt (lobby, hiding, round-end, safehouse). It fades out when the hunt starts (so hunters hear the whistles) and resumes at round end - it never restarts between phases. Empty = no music. Use the DEBUG 'phmusic' console command to list available track names.");
            _customBlob = CreateEntry("CustomPresetBlob", "", "Custom preset (saved)",
                "Internal: the last hosted custom settings, offered as a Custom preset next time. Managed automatically.");
            _customBase = CreateEntry("CustomPresetBase", "", "Custom preset base mode",
                "Internal: which base mode the saved Custom preset was derived from. Managed automatically.");
            _weaponCache = CreateEntry("WeaponCache", "", "Hunter weapon list (cached)",
                "Internal: the available weapons discovered from the game's item registry, used to fill the host-form dropdown. Managed automatically.");
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
            // is_hidden: true keeps the entry PERSISTED and code-readable (the Side Hustle host form + our phone app
            // still read/write these values) but hides it from generic MelonPreferences UIs - notably the Mod Manager
            // & Phone App settings list - because PropHunt manages its own settings via the host form. Standard
            // MelonLoader flag; any settings-scanner that respects is_hidden will omit these.
            return _category.CreateEntry(identifier, defaultValue, displayName, description, is_hidden: true);
        }

        internal static bool Enabled => _enabled?.Value ?? true;
        internal static int HideSeconds => Mathf.Max(1, _hideSeconds?.Value ?? 30);
        internal static int HuntSeconds => Mathf.Max(1, _huntSeconds?.Value ?? 300);
        internal static int RoundEndSeconds => Mathf.Clamp(_roundEndSeconds?.Value ?? 15, 5, 60);
        internal static int PlayersPerHunter => Mathf.Max(1, _playersPerHunter?.Value ?? 5);
        internal static int RoundsBeforeSwap => Mathf.Max(1, _roundsBeforeSwap?.Value ?? 1);
        internal static float TagRange => Mathf.Max(0.5f, _tagRange?.Value ?? 4f);
        internal static int HitsToCatch => Mathf.Max(1, _hitsToCatch?.Value ?? 2);
        internal static int MaxPropChanges => Mathf.Max(0, _maxPropChanges?.Value ?? 5);
        internal static int MaxDecoys => Mathf.Max(0, _maxDecoys?.Value ?? 4);
        internal static int ConcussCharges => Mathf.Max(0, _concussCharges?.Value ?? 1);
        internal static float ConcussRadius => Mathf.Max(1f, _concussRadius?.Value ?? 7f);
        internal static int TauntIntervalSeconds => Mathf.Max(0, _tauntIntervalSeconds?.Value ?? 30);
        internal static float PlayAreaRadius => Mathf.Max(5f, _playAreaRadius?.Value ?? 75f);
        internal static string CaughtBehaviorRaw => _caughtBehavior?.Value ?? "Spectator";
        internal static string RoundStructureRaw => _roundStructure?.Value ?? "Continuous";
        internal static int TimeOfDay => _timeOfDay?.Value ?? 1200;
        internal static string HunterWeapon => _hunterWeapon?.Value ?? "pumpshotgun";
        internal static bool FriendlyFire => _friendlyFire?.Value ?? true;
        internal static int HunterHitsToDown => Mathf.Max(1, _hunterHitsToDown?.Value ?? 3);
        internal static float HunterDownBaseSeconds => Mathf.Max(1f, _hunterDownBaseSeconds?.Value ?? 3f);
        internal static float HunterDownMaxSeconds => Mathf.Max(HunterDownBaseSeconds, _hunterDownMaxSeconds?.Value ?? 10f);
        internal static float ConcussStunSeconds => Mathf.Max(1f, _concussStunSeconds?.Value ?? 2f);
        internal static bool RemoveDecoysBetweenRounds => _removeDecoysBetweenRounds?.Value ?? true;
        internal static bool AllowRandomChange => _allowRandomChange?.Value ?? true;
        internal static bool FreeChangesInHiding => _freeChangesInHiding?.Value ?? true;
        internal static bool FreezeTime => _freezeTime?.Value ?? true;
        internal static bool AutoStartNextRound => _autoStartNextRound?.Value ?? true;
        internal static string MusicTrack => _musicTrack?.Value ?? "";
        internal static string CustomBlob => _customBlob?.Value ?? "";
        internal static string CustomBase => _customBase?.Value ?? "";
        internal static string WeaponCache => _weaponCache?.Value ?? "";

        /// <summary>Persist the weapon list discovered from the item registry (newline-separated "id|name" pairs).</summary>
        internal static void SaveWeaponCache(string cache)
        {
            if (_weaponCache == null) return;
            _weaponCache.Value = cache ?? "";
            try { MelonPreferences.Save(); } catch { }
        }

        /// <summary>Persist the last hosted custom settings so they are offered as a "Custom - {base}" preset next time.</summary>
        internal static void SaveCustomPreset(string blob, string baseMode)
        {
            if (_customBlob == null || _customBase == null) return;
            _customBlob.Value = blob ?? "";
            _customBase.Value = baseMode ?? "";
            try { MelonPreferences.Save(); } catch { }
        }

        /// <summary>Build the default RoundSettings (host side) from these preferences.</summary>
        internal static PropHunt.Game.RoundSettings BuildRoundSettings()
        {
            return new PropHunt.Game.RoundSettings
            {
                HideSeconds = HideSeconds,
                HuntSeconds = HuntSeconds,
                RoundEndSeconds = RoundEndSeconds,
                PlayersPerHunter = PlayersPerHunter,
                RoundsBeforeSwap = RoundsBeforeSwap,
                TagRange = TagRange,
                HitsToCatch = HitsToCatch,
                MaxPropChanges = MaxPropChanges,
                MaxDecoys = MaxDecoys,
                ConcussCharges = ConcussCharges,
                ConcussRadius = ConcussRadius,
                TauntIntervalSeconds = TauntIntervalSeconds,
                PlayAreaRadius = PlayAreaRadius,
                Caught = PropHunt.Game.RoundSettings.ParseCaught(CaughtBehaviorRaw),
                Structure = PropHunt.Game.RoundSettings.ParseStructure(RoundStructureRaw),
                TimeOfDay = TimeOfDay,
                HunterWeapon = HunterWeapon,
                FriendlyFire = FriendlyFire,
                HunterHitsToDown = HunterHitsToDown,
                HunterDownBaseSeconds = HunterDownBaseSeconds,
                HunterDownMaxSeconds = HunterDownMaxSeconds,
                ConcussStunSeconds = ConcussStunSeconds,
                RemoveDecoysBetweenRounds = RemoveDecoysBetweenRounds,
                AllowRandomChange = AllowRandomChange,
                FreeChangesInHiding = FreeChangesInHiding,
                FreezeTime = FreezeTime,
                AutoStartNextRound = AutoStartNextRound
            };
        }

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
