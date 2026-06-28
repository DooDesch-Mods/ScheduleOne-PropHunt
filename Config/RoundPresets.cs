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
        internal static SettingPreset[] Build() => new[]
        {
            P("Classic Hunt",
              "The classic. Hiders disguise as props and hide, hunters seek. Caught hiders are out and spectate.",
              "hide=30;hunt=300;end=8;pph=5;swap=1;tag=4;taunt=30;area=75;hits=2;chg=5;decoy=4;conc=1;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=m1911;ff=1"),

            P("Infection",
              "The classic with escalation: caught hiders turn into hunters. The longer it runs, the deadlier it gets for the remaining props.",
              "hide=30;hunt=360;end=8;pph=7;swap=1;tag=4;taunt=35;area=75;hits=2;chg=4;decoy=3;conc=1;concr=7;caught=Infection;round=Continuous;time=1200;weapon=m1911;ff=1"),

            P("Side Hustle Party",
              "Casual chaos with lots of hider tools. Great for bigger groups and fun, less competitive lobbies.",
              "hide=45;hunt=420;end=8;pph=5;swap=1;tag=4;taunt=40;area=90;hits=2;chg=8;decoy=6;conc=2;concr=8;caught=Spectator;round=Continuous;time=1200;weapon=m1911;ff=1"),

            P("Ranked Rules",
              "Competitive, low randomness. Fewer decoys and utility, cleaner rounds, no friendly fire. Good for fair, fixed groups.",
              "hide=30;hunt=300;end=8;pph=5;swap=1;tag=4;taunt=30;area=65;hits=2;chg=2;decoy=1;conc=0;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=m1911;ff=0"),

            P("Panic Room",
              "Tiny zone, short rounds, fast action. Ideal for \"just one more quick round\".",
              "hide=15;hunt=150;end=5;pph=4;swap=1;tag=4;taunt=20;area=40;hits=2;chg=2;decoy=2;conc=1;concr=6;caught=Spectator;round=Continuous;time=1200;weapon=m1911;ff=1"),

            P("Deep Cover",
              "Long, calmer rounds. Hiders must stay convincing for longer; hunters watch more than they shoot.",
              "hide=60;hunt=600;end=8;pph=5;swap=1;tag=4;taunt=50;area=100;hits=2;chg=3;decoy=3;conc=1;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=m1911;ff=1"),

            P("Last Prop Standing",
              "Endgame-focused. Standard PropHunt tuned so the closing minutes feel like a finale.",
              "hide=30;hunt=360;end=8;pph=5;swap=1;tag=4;taunt=30;area=75;hits=2;chg=4;decoy=3;conc=1;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=m1911;ff=1"),

            P("Blend In (Experimental)",
              "A Schedule I twist where hiders mimic NPCs, not just props. Plays as Classic for now - NPC disguise is coming soon.",
              "hide=45;hunt=360;end=8;pph=5;swap=1;tag=4;taunt=35;area=80;hits=2;chg=5;decoy=2;conc=1;concr=7;caught=Spectator;round=Continuous;time=1200;weapon=m1911;ff=1"),

            P("Closing Time (Experimental)",
              "The play area shrinks over time, squeezing hiders out of safe spots. The boundary stays fixed for now - shrinking is coming soon.",
              "hide=30;hunt=360;end=8;pph=5;swap=1;tag=4;taunt=30;area=100;hits=2;chg=4;decoy=3;conc=1;concr=7;caught=Spectator;round=Continuous;time=1800;weapon=m1911;ff=1"),
        };

        /// <summary>Parse a compact "k=v;k=v" spec into a preset's value map (keeps the preset table readable).</summary>
        private static SettingPreset P(string name, string hint, string spec)
        {
            var values = new Dictionary<string, string>();
            foreach (var part in spec.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                values[part.Substring(0, eq).Trim()] = part.Substring(eq + 1).Trim();
            }
            return new SettingPreset { Name = name, Hint = hint, Values = values };
        }
    }
}
