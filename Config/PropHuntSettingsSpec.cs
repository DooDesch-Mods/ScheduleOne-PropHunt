using System.Globalization;
using SideHustle;

namespace PropHunt.Config
{
    /// <summary>
    /// Maps PropHunt's <see cref="PropHunt.Game.RoundSettings"/> to the Side Hustle host-form controls (one control
    /// per field). Each descriptor's Key matches the RoundSettings short key, so the form's emitted config blob is a
    /// RoundSettings blob the controller can parse directly. Defaults are seeded from the saved host preferences.
    ///
    /// The settings are ordered + tagged by Category so the host form renders them under section headers
    /// (Round / Roles &amp; Combat / Props / World); the framework's built-in lobby rows render under a "Lobby" header.
    /// </summary>
    internal static class PropHuntSettingsSpec
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        internal static SettingDescriptor[] Build()
        {
            var w = WeaponChoices();
            return new[]
            {
                // --- Round ---
                Segmented("Round", "round", "Round mode",         "Continuous = auto-swap rounds; Single = one round, then the hub.", new[] { "Continuous", "Single" }, PropHuntPreferences.RoundStructureRaw),
                IntSlider("Round", "swap",  "Rounds before swap", "Rounds to play before roles rotate.",                   1,  10,  1, null, PropHuntPreferences.RoundsBeforeSwap),
                IntSlider("Round", "hide",  "Hiding time",        "Seconds hiders get before hunters are released.",       5, 120,  5, "s", PropHuntPreferences.HideSeconds),
                IntSlider("Round", "hunt",  "Hunting time",       "Seconds hunters have to find every hider.",            30, 900, 15, "s", PropHuntPreferences.HuntSeconds),
                IntSlider("Round", "end",   "Results screen",     "Seconds the round-end scoreboard shows before the safehouse.", 5, 60, 1, "s", PropHuntPreferences.RoundEndSeconds),
                IntSlider("Round", "taunt", "Taunt interval",     "How often hiders are forced to make a sound (0 = off).", 0, 120,  5, "s", PropHuntPreferences.TauntIntervalSeconds),
                Segmented("Round", "caught","On catch",           "Spectator = sit out till round end; Infection = become a hunter.", new[] { "Spectator", "Infection" }, PropHuntPreferences.CaughtBehaviorRaw),

                // --- Roles & Combat ---
                IntSlider("Roles & Combat", "pph",    "Hunter ratio",  "1 hunter per N players, rounded up.",            2, 10, 1, null, PropHuntPreferences.PlayersPerHunter),
                Dropdown ("Roles & Combat", "weapon", "Hunter weapon", "Weapon each hunter gets when the hunt starts.",
                          w.opts, w.vals,                                                                    PropHuntPreferences.HunterWeapon),
                Toggle   ("Roles & Combat", "ff",     "Friendly fire", "Hunters can knock each other down (ragdoll, never kill).", PropHuntPreferences.FriendlyFire),
                IntSlider("Roles & Combat", "hhp",    "Friendly hits to down", "Friendly-fire hits a hunter takes before being knocked down (their HP).", 1, 10, 1, null, PropHuntPreferences.HunterHitsToDown),
                IntSlider("Roles & Combat", "downb",  "Knockdown time",   "Seconds a hunter is ragdolled when first knocked down.",  1, 20, 1, "s", (int)PropHuntPreferences.HunterDownBaseSeconds),
                IntSlider("Roles & Combat", "downx",  "Max knockdown time", "Cap on ragdoll time; each extra hit while down extends toward this.", 1, 30, 1, "s", (int)PropHuntPreferences.HunterDownMaxSeconds),

                // --- Props ---
                IntSlider("Props", "hits",    "Prop toughness",   "Bigger props are naturally tankier at higher values (HP per metre).", 1, 10, 1, null, PropHuntPreferences.HitsToCatch),
                IntSlider("Props", "chg",     "Max prop changes", "Re-picks per round; each resets HP (0 = unlimited).",   0, 20, 1, null, PropHuntPreferences.MaxPropChanges),
                Toggle   ("Props", "freechg", "Unlimited changes while hiding", "Prop changes during the hiding phase are unlimited; the limit applies only once the hunt starts.", PropHuntPreferences.FreeChangesInHiding),
                Toggle   ("Props", "rnd",     "Random prop [2]",  "Let hiders press [2] to become a random prop.",         PropHuntPreferences.AllowRandomChange),
                IntSlider("Props", "decoy",   "Decoys per prop",  "Decoys [Q] a hider may drop per prop (0 = off).",       0, 10, 1, null, PropHuntPreferences.MaxDecoys),
                IntSlider("Props", "conc",    "Concussions per prop", "Concussions [G] a hider may use per prop (0 = off).", 0, 10, 1, null, PropHuntPreferences.ConcussCharges),
                IntSlider("Props", "concr",   "Concussion radius","Hunters within this of a concussion get stunned.",      2, 20, 1, "m", (int)PropHuntPreferences.ConcussRadius),
                IntSlider("Props", "stun",    "Concussion stun time", "Seconds a concussion knocks nearby hunters down (short stun).", 1, 10, 1, "s", (int)PropHuntPreferences.ConcussStunSeconds),
                Toggle   ("Props", "rmdecoy", "Clear decoys between rounds", "Remove dropped decoys at round end (off = they carry over).", PropHuntPreferences.RemoveDecoysBetweenRounds),

                // --- World ---
                IntSlider("World", "area",  "Play-area radius",   "Radius of the round's play area around the safehouse.", 50, 200, 5, "m", (int)PropHuntPreferences.PlayAreaRadius),
                IntSlider("World", "time",  "Time of day",        "World time during a round (HHMM; 1200 = noon).",        0, 2300, 100, null, PropHuntPreferences.TimeOfDay),
                Toggle   ("World", "freeze","Lock time of day",   "Lock the world clock during a round. Off = start at the set time, then let it run.", PropHuntPreferences.FreezeTime),
                Toggle   ("World", "autostart","Auto-start next round","Automatically start the next round after a short safehouse pause. Off = start each round manually. Toggle live in the phone app.", PropHuntPreferences.AutoStartNextRound),
            };
        }

        private static SettingDescriptor IntSlider(string cat, string key, string label, string hint, float min, float max, float step, string unit, int def) =>
            new SettingDescriptor { Category = cat, Key = key, Label = label, Hint = hint, Type = SettingType.Slider, Min = min, Max = max, Step = step, WholeNumbers = true, Unit = unit, Default = def.ToString(CI) };

        private static SettingDescriptor Segmented(string cat, string key, string label, string hint, string[] options, string def) =>
            new SettingDescriptor { Category = cat, Key = key, Label = label, Hint = hint, Type = SettingType.Segmented, Options = options, Values = options, Default = def };

        private static SettingDescriptor Toggle(string cat, string key, string label, string hint, bool def) =>
            new SettingDescriptor { Category = cat, Key = key, Label = label, Hint = hint, Type = SettingType.Toggle, Default = def ? "1" : "0" };

        private static SettingDescriptor Dropdown(string cat, string key, string label, string hint, string[] options, string[] values, string def) =>
            new SettingDescriptor { Category = cat, Key = key, Label = label, Hint = hint, Type = SettingType.Dropdown, Options = options, Values = values, Default = def ?? "" };

        /// <summary>The weapon dropdown choices: "None" plus every weapon discovered from the game's item registry
        /// (cached by <see cref="WeaponCatalog"/>); falls back to a small built-in set until the cache is primed in
        /// a gameplay session.</summary>
        private static (string[] opts, string[] vals) WeaponChoices()
        {
            var opts = new System.Collections.Generic.List<string> { "None" };
            var vals = new System.Collections.Generic.List<string> { "" };
            var weapons = WeaponCatalog.Weapons();
            if (weapons.Count > 0)
                foreach (var wkv in weapons) { opts.Add(wkv.Value); vals.Add(wkv.Key); }
            else
            {
                // The current weapon set (Golden M1911 excluded on purpose); the live registry cache supersedes this
                // and picks up any future weapons automatically once a gameplay scene has been entered.
                opts.AddRange(new[] { "Pump Shotgun", "M1911", "Revolver", "Machete", "Baseball Bat", "Frying Pan" });
                vals.AddRange(new[] { "pumpshotgun", "m1911", "revolver", "machete", "baseballbat", "fryingpan" });
            }
            return (opts.ToArray(), vals.ToArray());
        }
    }
}
