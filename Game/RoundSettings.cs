using System;
using System.Globalization;

namespace PropHunt.Game
{
    /// <summary>
    /// The host-chosen match configuration. The host seeds it from <see cref="PropHuntPreferences"/> (the
    /// defaults), optionally tweaks it on the pre-round setup screen, then publishes the serialized form via
    /// the SteamNetworkLib <c>Settings</c> HostSyncVar so every client applies the identical rules. Plain
    /// data + a compact, version-tolerant string codec (unknown keys ignored, missing keys defaulted).
    /// </summary>
    internal sealed class RoundSettings
    {
        internal int HideSeconds = 30;
        internal int HuntSeconds = 300;
        internal int RoundEndSeconds = 15;
        internal int PlayersPerHunter = 5;
        internal int RoundsBeforeSwap = 1;
        internal float TagRange = 4f;
        internal int TauntIntervalSeconds = 30;
        internal float WhistleStaggerSeconds = 0.3f;   // gap between each hider's sound in the global whistle sweep (CoD WWII style)
        internal float PlayAreaRadius = 75f;
        internal int HitsToCatch = 2;           // prop HP per metre of size: MaxHits = round(maxDimension * this), capped by HiderMaxHp
        internal int HiderMaxHp = 4;            // cap on a hider's prop HP - the size-scaled value is clamped to this (host-configurable)
        internal int MaxPropChanges = 5;        // how many times a hider may (re)pick a prop per round (0 = unlimited); each change resets HP
        internal int MaxDecoys = 4;             // decoys ([Q]) a hider may drop PER PROP (refills on prop change; 0 = none)
        internal int ConcussCharges = 1;        // concussions ([G]) a hider may use PER PROP (refills on prop change, CoD-style; 0 = none)
        internal float ConcussRadius = 7f;      // metres: hunters within this of the hider get tazed/stunned
        internal CaughtBehavior Caught = CaughtBehavior.Spectator;
        internal RoundStructure Structure = RoundStructure.Continuous;
        internal int TimeOfDay = 1200;          // HHMM; world locked here during a round (1200 = noon/day, 0100 = night)
        internal string HunterWeapon = "pumpshotgun"; // item id given to each hunter at hunt start ("" = none)
        internal bool FriendlyFire = true;      // hunters can damage each other (friendly-fire hits knock a hunter DOWN, never kill)
        internal int HunterHitsToDown = 3;      // friendly-fire hits a hunter takes before being knocked down (their "HP"); 0/1 = one shot downs
        internal float HunterDownBaseSeconds = 3f;   // ragdoll time when first knocked down
        internal float HunterDownMaxSeconds = 10f;   // cap on ragdoll time (each extra hit while down extends toward this)
        internal float ConcussStunSeconds = 2f;      // ragdoll time a concussion ([G]) knocks nearby hunters down for (short stun)
        internal bool RemoveDecoysBetweenRounds = true;   // clear dropped decoys at round end (false = they persist into the next round)
        internal bool AllowRandomChange = true;           // hider may press [2] to become a random prop
        internal bool FreeChangesInHiding = true;         // prop changes during Hiding don't count toward MaxPropChanges (unlimited before the hunt)
        internal bool FreezeTime = true;                  // lock + freeze the time of day during a round (false = set it at round start, then let it run)
        internal bool AutoStartNextRound = true;          // auto-advance out of the between-rounds Safehouse after a short pause (host can toggle live in the app)

        internal static CaughtBehavior ParseCaught(string s) =>
            string.Equals(s, "Infection", StringComparison.OrdinalIgnoreCase) ? CaughtBehavior.Infection : CaughtBehavior.Spectator;

        internal static RoundStructure ParseStructure(string s) =>
            string.Equals(s, "Single", StringComparison.OrdinalIgnoreCase) ? RoundStructure.Single : RoundStructure.Continuous;

        /// <summary>Compact "k=v;..." blob for the Settings HostSyncVar.</summary>
        internal string Serialize()
        {
            var ci = CultureInfo.InvariantCulture;
            return string.Join(";", new[]
            {
                "hide=" + HideSeconds.ToString(ci),
                "hunt=" + HuntSeconds.ToString(ci),
                "end=" + RoundEndSeconds.ToString(ci),
                "pph=" + PlayersPerHunter.ToString(ci),
                "swap=" + RoundsBeforeSwap.ToString(ci),
                "tag=" + TagRange.ToString(ci),
                "taunt=" + TauntIntervalSeconds.ToString(ci),
                "wstag=" + WhistleStaggerSeconds.ToString(ci),
                "area=" + PlayAreaRadius.ToString(ci),
                "hits=" + HitsToCatch.ToString(ci),
                "hidermaxhp=" + HiderMaxHp.ToString(ci),
                "chg=" + MaxPropChanges.ToString(ci),
                "decoy=" + MaxDecoys.ToString(ci),
                "conc=" + ConcussCharges.ToString(ci),
                "concr=" + ConcussRadius.ToString(ci),
                "caught=" + Caught,
                "round=" + Structure,
                "time=" + TimeOfDay.ToString(ci),
                "weapon=" + (HunterWeapon ?? ""),
                "ff=" + (FriendlyFire ? "1" : "0"),
                "hhp=" + HunterHitsToDown.ToString(ci),
                "downb=" + HunterDownBaseSeconds.ToString(ci),
                "downx=" + HunterDownMaxSeconds.ToString(ci),
                "stun=" + ConcussStunSeconds.ToString(ci),
                "rmdecoy=" + (RemoveDecoysBetweenRounds ? "1" : "0"),
                "rnd=" + (AllowRandomChange ? "1" : "0"),
                "freechg=" + (FreeChangesInHiding ? "1" : "0"),
                "freeze=" + (FreezeTime ? "1" : "0"),
                "autostart=" + (AutoStartNextRound ? "1" : "0")
            });
        }

        /// <summary>Parse a blob produced by <see cref="Serialize"/>. Returns defaults for anything missing/garbled.</summary>
        internal static RoundSettings Parse(string blob) => Parse(blob, new RoundSettings());

        /// <summary>Parse a blob over the given defaults (e.g. the host's saved preferences) - missing/garbled keys keep their default.</summary>
        internal static RoundSettings Parse(string blob, RoundSettings defaults)
        {
            var s = defaults ?? new RoundSettings();
            if (string.IsNullOrEmpty(blob)) return s;
            foreach (var part in blob.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                s.ApplyKeyValue(part.Substring(0, eq), part.Substring(eq + 1));
            }
            return s;
        }

        /// <summary>
        /// Apply a single "key=value" pair (the same keys <see cref="Serialize"/> emits) onto this instance.
        /// Unknown keys and unparseable values are ignored so the field keeps its current value. Shared by
        /// <see cref="Parse(string, RoundSettings)"/> and the in-game phone Settings editor (per-control edits).
        /// </summary>
        internal void ApplyKeyValue(string k, string v)
        {
            var ci = CultureInfo.InvariantCulture;
            switch (k)
            {
                case "hide": if (int.TryParse(v, NumberStyles.Integer, ci, out var hi)) HideSeconds = hi; break;
                case "hunt": if (int.TryParse(v, NumberStyles.Integer, ci, out var hu)) HuntSeconds = hu; break;
                case "end": if (int.TryParse(v, NumberStyles.Integer, ci, out var en)) RoundEndSeconds = en; break;
                case "pph": if (int.TryParse(v, NumberStyles.Integer, ci, out var pph)) PlayersPerHunter = pph; break;
                case "swap": if (int.TryParse(v, NumberStyles.Integer, ci, out var sw)) RoundsBeforeSwap = sw; break;
                case "tag": if (float.TryParse(v, NumberStyles.Float, ci, out var tg)) TagRange = tg; break;
                case "taunt": if (int.TryParse(v, NumberStyles.Integer, ci, out var ta)) TauntIntervalSeconds = ta; break;
                case "wstag": if (float.TryParse(v, NumberStyles.Float, ci, out var wst)) WhistleStaggerSeconds = wst; break;
                case "area": if (float.TryParse(v, NumberStyles.Float, ci, out var ar)) PlayAreaRadius = ar; break;
                case "hits": if (int.TryParse(v, NumberStyles.Integer, ci, out var ht)) HitsToCatch = ht; break;
                case "hidermaxhp": if (int.TryParse(v, NumberStyles.Integer, ci, out var hmh)) HiderMaxHp = hmh; break;
                case "chg": if (int.TryParse(v, NumberStyles.Integer, ci, out var cg)) MaxPropChanges = cg; break;
                case "decoy": if (int.TryParse(v, NumberStyles.Integer, ci, out var dc)) MaxDecoys = dc; break;
                case "conc": if (int.TryParse(v, NumberStyles.Integer, ci, out var cc)) ConcussCharges = cc; break;
                case "concr": if (float.TryParse(v, NumberStyles.Float, ci, out var cr)) ConcussRadius = cr; break;
                case "caught": Caught = ParseCaught(v); break;
                case "round": Structure = ParseStructure(v); break;
                case "time": if (int.TryParse(v, NumberStyles.Integer, ci, out var tm)) TimeOfDay = tm; break;
                case "weapon": HunterWeapon = v; break;
                case "ff": FriendlyFire = v == "1"; break;
                case "hhp": if (int.TryParse(v, NumberStyles.Integer, ci, out var hhp)) HunterHitsToDown = hhp; break;
                case "downb": if (float.TryParse(v, NumberStyles.Float, ci, out var dnb)) HunterDownBaseSeconds = dnb; break;
                case "downx": if (float.TryParse(v, NumberStyles.Float, ci, out var dnx)) HunterDownMaxSeconds = dnx; break;
                case "stun": if (float.TryParse(v, NumberStyles.Float, ci, out var stn)) ConcussStunSeconds = stn; break;
                case "rmdecoy": RemoveDecoysBetweenRounds = v == "1"; break;
                case "rnd": AllowRandomChange = v == "1"; break;
                case "freechg": FreeChangesInHiding = v == "1"; break;
                case "freeze": FreezeTime = v == "1"; break;
                case "autostart": AutoStartNextRound = v == "1"; break;
            }
        }

        /// <summary>The current values as a key-&gt;value map (same keys as <see cref="Serialize"/>), for seeding UI controls.</summary>
        internal System.Collections.Generic.Dictionary<string, string> ToValues()
        {
            var d = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in Serialize().Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                d[part.Substring(0, eq)] = part.Substring(eq + 1);
            }
            return d;
        }

        public override string ToString() =>
            $"hide={HideSeconds}s hunt={HuntSeconds}s pph={PlayersPerHunter} caught={Caught} round={Structure} tag={TagRange} hits={HitsToCatch} area={PlayAreaRadius}";
    }
}
