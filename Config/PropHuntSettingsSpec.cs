using System.Globalization;
using SideHustle;

namespace PropHunt.Config
{
    /// <summary>
    /// Maps PropHunt's <see cref="PropHunt.Game.RoundSettings"/> to the Side Hustle host-form controls (one control
    /// per field). Each descriptor's Key matches the RoundSettings short key, so the form's emitted config blob is a
    /// RoundSettings blob the controller can parse directly. Defaults are seeded from the saved host preferences.
    /// </summary>
    internal static class PropHuntSettingsSpec
    {
        private static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        internal static SettingDescriptor[] Build()
        {
            return new[]
            {
                IntSlider("hide",  "Hiding phase",        "Seconds hiders get before hunters are released.",       5, 120,  5, "s", PropHuntPreferences.HideSeconds),
                IntSlider("hunt",  "Hunting phase",       "Seconds hunters have to find every hider.",            30, 900, 15, "s", PropHuntPreferences.HuntSeconds),
                IntSlider("end",   "Scoreboard time",     "Seconds the round-end scoreboard shows before the safehouse.", 5, 60, 1, "s", PropHuntPreferences.RoundEndSeconds),
                IntSlider("pph",   "Players per hunter",  "Roughly one hunter is assigned per this many players.", 2,  10,  1, null, PropHuntPreferences.PlayersPerHunter),
                IntSlider("swap",  "Rounds before swap",  "Rounds to play before roles rotate.",                   1,  10,  1, null, PropHuntPreferences.RoundsBeforeSwap),
                IntSlider("taunt", "Taunt interval",      "How often hiders are forced to make a sound (0 = off).", 0, 120,  5, "s", PropHuntPreferences.TauntIntervalSeconds),
                IntSlider("area",  "Play-area radius",    "Radius of the round's play area around the host.",     20, 200,  5, "m", (int)PropHuntPreferences.PlayAreaRadius),
                IntSlider("hits",  "Prop HP per metre",   "Disguise HP scales with prop size by this (clamped).",  1,  10,  1, null, PropHuntPreferences.HitsToCatch),
                IntSlider("chg",   "Max prop changes",    "Re-picks per round; each resets HP (0 = unlimited).",   0,  20,  1, null, PropHuntPreferences.MaxPropChanges),
                IntSlider("decoy", "Decoys per prop",     "Decoys [Q] a hider may drop per prop (0 = off).",       0,  10,  1, null, PropHuntPreferences.MaxDecoys),
                IntSlider("conc",  "Concussions per prop","Concussions [G] a hider may use per prop (0 = off).",   0,  10,  1, null, PropHuntPreferences.ConcussCharges),
                IntSlider("concr", "Concussion radius",   "Hunters within this of a concussion get stunned.",      2,  20,  1, "m", (int)PropHuntPreferences.ConcussRadius),
                Segmented("caught","When caught",         "Spectator = sit out till round end; Infection = become a hunter.", new[] { "Spectator", "Infection" }, PropHuntPreferences.CaughtBehaviorRaw),
                Segmented("round", "Round structure",     "Continuous = auto-swap rounds; Single = one round, then the hub.", new[] { "Continuous", "Single" }, PropHuntPreferences.RoundStructureRaw),
                IntSlider("time",  "Time of day",         "World time locked during a round (HHMM; 1200 = noon).", 0, 2300, 100, null, PropHuntPreferences.TimeOfDay),
                Text("weapon",     "Hunter weapon",       "Item id given to each hunter (e.g. m1911, revolver; empty = none).", PropHuntPreferences.HunterWeapon),
                Toggle("ff",       "Friendly fire",       "Hunters can damage each other.",                        PropHuntPreferences.FriendlyFire),
                Toggle("rmdecoy",  "Clear decoys between rounds", "Remove dropped decoys at round end (off = they carry over).", PropHuntPreferences.RemoveDecoysBetweenRounds),
            };
        }

        private static SettingDescriptor IntSlider(string key, string label, string hint, float min, float max, float step, string unit, int def) =>
            new SettingDescriptor { Key = key, Label = label, Hint = hint, Type = SettingType.Slider, Min = min, Max = max, Step = step, WholeNumbers = true, Unit = unit, Default = def.ToString(CI) };

        private static SettingDescriptor FloatSlider(string key, string label, string hint, float min, float max, float step, string unit, float def) =>
            new SettingDescriptor { Key = key, Label = label, Hint = hint, Type = SettingType.Slider, Min = min, Max = max, Step = step, WholeNumbers = false, Unit = unit, Default = def.ToString("0.##", CI) };

        private static SettingDescriptor Segmented(string key, string label, string hint, string[] options, string def) =>
            new SettingDescriptor { Key = key, Label = label, Hint = hint, Type = SettingType.Segmented, Options = options, Values = options, Default = def };

        private static SettingDescriptor Text(string key, string label, string hint, string def) =>
            new SettingDescriptor { Key = key, Label = label, Hint = hint, Type = SettingType.Text, Default = def ?? "" };

        private static SettingDescriptor Toggle(string key, string label, string hint, bool def) =>
            new SettingDescriptor { Key = key, Label = label, Hint = hint, Type = SettingType.Toggle, Default = def ? "1" : "0" };
    }
}
