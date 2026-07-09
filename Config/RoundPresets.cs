using System.Collections.Generic;
using SideHustle;

namespace PropHunt.Config
{
    /// <summary>
    /// One-click host presets for the Side Hustle host form. Each preset is a bundle of <see cref="RoundSettings"/>
    /// values keyed by the SAME short keys the <see cref="PropHuntSettingsSpec"/> descriptors use, so selecting one
    /// cascades straight into the form controls (the host can then still tweak every individual setting). Keys that
    /// have no control - e.g. not-yet-built experimental settings - are simply ignored.
    ///
    /// "Classic Hunt" is first, so the form opens on it (the default). The two experimental presets configure only
    /// their implemented fields today; their headline mechanic (NPC disguise / shrinking area) lands later.
    /// </summary>
    internal static class RoundPresets
    {
        internal static SettingPreset[] Build()
        {
            var basePresets = BuildBase();
#if !DEBUG
            // Experimental presets ("Blend In", "Closing Time") ship their headline mechanic (NPC disguise /
            // shrinking area) later, so keep them out of the public host form - nobody should be able to pick a
            // half-finished mode. They stay available in Debug builds for authoring and testing.
            basePresets = System.Array.FindAll(basePresets, p => !p.Experimental);
#endif
            var custom = BuildCustom();
            if (custom == null) return basePresets;
            var list = new List<SettingPreset>(basePresets.Length + 1) { custom };
            list.AddRange(basePresets);
            return list.ToArray();
        }

        /// <summary>The saved "Custom - {base}" preset from the host's last tweaked session (pre-selected), or null.</summary>
        private static SettingPreset BuildCustom()
        {
            string blob = PropHuntPreferences.CustomBlob;
            if (string.IsNullOrEmpty(blob)) return null;
            string baseMode = PropHuntPreferences.CustomBase;
            if (string.IsNullOrEmpty(baseMode)) baseMode = "Custom";
            // The config blob is the same escape-free k=v;k=v form RoundSettings uses (PropHunt values never
            // contain ';' or '='), so a plain split mirrors how the gamemode reads its own settings.
            var values = new Dictionary<string, string>();
            foreach (var part in blob.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                values[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            return new SettingPreset
            {
                Name = "Custom - " + baseMode,
                Hint = "Your last hosted settings (based on " + baseMode + ").",
                Mode = baseMode,
                Values = values,
                DefaultSelected = true,
            };
        }

        private static SettingPreset[] BuildBase() => new[]
        {
            P("Classic Hunt",
              "The classic. Hiders disguise as props and hide, hunters seek. Caught hiders are out and spectate.",
              "hide=30;hunt=300;end=15;pph=5;swap=1;tag=4;taunt=30;area=75;hits=2;chg=5;decoy=4;conc=1;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=pumpshotgun;ff=1",
              recommended: "Best for 4-10", minP: 3, maxP: 12),

            P("Infection",
              "The classic with escalation: caught hiders turn into hunters. The longer it runs, the deadlier it gets for the remaining props.",
              "hide=30;hunt=360;end=15;pph=7;swap=1;tag=4;taunt=35;area=75;hits=2;chg=4;decoy=3;conc=1;concr=7;caught=Infection;round=Continuous;time=1200;weapon=pumpshotgun;ff=1",
              recommended: "Great for 6-16", minP: 6, maxP: 20),

            P("Side Hustle Party",
              "Casual chaos with lots of hider tools. Great for bigger groups and fun, less competitive lobbies.",
              "hide=45;hunt=420;end=15;pph=5;swap=1;tag=4;taunt=40;area=90;hits=2;chg=8;decoy=6;conc=2;concr=8;caught=Spectator;round=Continuous;time=1200;weapon=pumpshotgun;ff=1",
              recommended: "Best for 8-16", minP: 7, maxP: 20),

            P("Ranked Rules",
              "Competitive, low randomness. Fewer decoys and utility, cleaner rounds, no friendly fire. Good for fair, fixed groups.",
              "hide=30;hunt=300;end=15;pph=5;swap=1;tag=4;taunt=30;area=65;hits=2;chg=2;decoy=1;conc=0;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=pumpshotgun;ff=0",
              recommended: "Best for 4-8", minP: 4, maxP: 8),

            P("Panic Room",
              "Tiny zone, short rounds, fast action. Ideal for \"just one more quick round\".",
              "hide=15;hunt=150;end=15;pph=4;swap=1;tag=4;taunt=20;area=40;hits=2;chg=2;decoy=2;conc=1;concr=6;caught=Spectator;round=Continuous;time=1200;weapon=pumpshotgun;ff=1",
              recommended: "Best for 2-5", minP: 2, maxP: 5),

            P("Deep Cover",
              "Long, calmer rounds. Hiders must stay convincing for longer; hunters watch more than they shoot.",
              "hide=60;hunt=600;end=15;pph=5;swap=1;tag=4;taunt=50;area=100;hits=2;chg=3;decoy=3;conc=1;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=pumpshotgun;ff=1",
              recommended: "Best for 4-10", minP: 4, maxP: 10),

            P("Last Prop Standing",
              "Endgame-focused. Standard PropHunt tuned so the closing minutes feel like a finale.",
              "hide=30;hunt=360;end=15;pph=5;swap=1;tag=4;taunt=30;area=75;hits=2;chg=4;decoy=3;conc=1;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=pumpshotgun;ff=1",
              recommended: "Best for 4-10", minP: 4, maxP: 12),

            P("Blend In",
              "A Schedule I twist where hiders mimic NPCs, not just props. Plays as Classic for now - NPC disguise is coming soon.",
              "hide=45;hunt=360;end=15;pph=5;swap=1;tag=4;taunt=35;area=80;hits=2;chg=5;decoy=2;conc=1;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=pumpshotgun;ff=1",
              recommended: "Best for 4-10", minP: 4, maxP: 12, experimental: true),

            P("Closing Time",
              "The play area shrinks over time, squeezing hiders out of safe spots. The boundary stays fixed for now - shrinking is coming soon.",
              "hide=30;hunt=360;end=15;pph=5;swap=1;tag=4;taunt=30;area=100;hits=2;chg=4;decoy=3;conc=1;concr=7;caught=Spectator;round=Continuous;time=1800;weapon=pumpshotgun;ff=1",
              recommended: "Best for 6-16", minP: 6, maxP: 20, experimental: true),
        };

        /// <summary>Parse a compact "k=v;k=v" spec into a preset's value map (keeps the preset table readable).</summary>
        private static SettingPreset P(string name, string hint, string spec,
                                       string recommended = null, int minP = 0, int maxP = 0, bool experimental = false)
        {
            var values = new Dictionary<string, string>();
            foreach (var part in spec.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                values[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            // newer settings default-on for every preset (these keys were added after the preset table was authored).
            if (!values.ContainsKey("rnd")) values["rnd"] = "1";
            if (!values.ContainsKey("freechg")) values["freechg"] = "1";
            if (!values.ContainsKey("freeze")) values["freeze"] = "1";
            return new SettingPreset
            {
                Name = name, Hint = hint, Values = values, Mode = name,
                Recommended = recommended, MinPlayers = minP, MaxPlayers = maxP, Experimental = experimental,
            };
        }
    }
}
