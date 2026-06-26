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
        internal int RoundEndSeconds = 8;
        internal int PlayersPerHunter = 5;
        internal int RoundsBeforeSwap = 1;
        internal float TagRange = 4f;
        internal int TauntIntervalSeconds = 30;
        internal float PlayAreaRadius = 75f;
        internal int HitsToCatch = 2;           // prop HP per metre of size: MaxHits = round(maxDimension * this), clamped
        internal int MaxPropChanges = 5;        // how many times a hider may (re)pick a prop per round (0 = unlimited); each change resets HP
        internal int MaxDecoys = 4;             // decoys ([Q]) a hider may drop PER PROP (refills on prop change; 0 = none)
        internal int ConcussCharges = 1;        // concussions ([G]) a hider may use PER PROP (refills on prop change, CoD-style; 0 = none)
        internal float ConcussRadius = 7f;      // metres: hunters within this of the hider get tazed/stunned
        internal CaughtBehavior Caught = CaughtBehavior.Spectator;
        internal RoundStructure Structure = RoundStructure.Continuous;
        internal int TimeOfDay = 1200;          // HHMM; world locked here during a round (1200 = noon/day, 0100 = night)
        internal string HunterWeapon = "m1911"; // item id given to each hunter at hunt start ("" = none)
        internal bool FriendlyFire = true;      // hunters can damage each other (enforcement = testing-phase)

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
                "area=" + PlayAreaRadius.ToString(ci),
                "hits=" + HitsToCatch.ToString(ci),
                "chg=" + MaxPropChanges.ToString(ci),
                "decoy=" + MaxDecoys.ToString(ci),
                "conc=" + ConcussCharges.ToString(ci),
                "concr=" + ConcussRadius.ToString(ci),
                "caught=" + Caught,
                "round=" + Structure,
                "time=" + TimeOfDay.ToString(ci),
                "weapon=" + (HunterWeapon ?? ""),
                "ff=" + (FriendlyFire ? "1" : "0")
            });
        }

        /// <summary>Parse a blob produced by <see cref="Serialize"/>. Returns defaults for anything missing/garbled.</summary>
        internal static RoundSettings Parse(string blob) => Parse(blob, new RoundSettings());

        /// <summary>Parse a blob over the given defaults (e.g. the host's saved preferences) - missing/garbled keys keep their default.</summary>
        internal static RoundSettings Parse(string blob, RoundSettings defaults)
        {
            var s = defaults ?? new RoundSettings();
            if (string.IsNullOrEmpty(blob)) return s;
            var ci = CultureInfo.InvariantCulture;
            foreach (var part in blob.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq);
                string v = part.Substring(eq + 1);
                switch (k)
                {
                    case "hide": if (int.TryParse(v, NumberStyles.Integer, ci, out var hi)) s.HideSeconds = hi; break;
                    case "hunt": if (int.TryParse(v, NumberStyles.Integer, ci, out var hu)) s.HuntSeconds = hu; break;
                    case "end": if (int.TryParse(v, NumberStyles.Integer, ci, out var en)) s.RoundEndSeconds = en; break;
                    case "pph": if (int.TryParse(v, NumberStyles.Integer, ci, out var pph)) s.PlayersPerHunter = pph; break;
                    case "swap": if (int.TryParse(v, NumberStyles.Integer, ci, out var sw)) s.RoundsBeforeSwap = sw; break;
                    case "tag": if (float.TryParse(v, NumberStyles.Float, ci, out var tg)) s.TagRange = tg; break;
                    case "taunt": if (int.TryParse(v, NumberStyles.Integer, ci, out var ta)) s.TauntIntervalSeconds = ta; break;
                    case "area": if (float.TryParse(v, NumberStyles.Float, ci, out var ar)) s.PlayAreaRadius = ar; break;
                    case "hits": if (int.TryParse(v, NumberStyles.Integer, ci, out var ht)) s.HitsToCatch = ht; break;
                    case "chg": if (int.TryParse(v, NumberStyles.Integer, ci, out var cg)) s.MaxPropChanges = cg; break;
                    case "decoy": if (int.TryParse(v, NumberStyles.Integer, ci, out var dc)) s.MaxDecoys = dc; break;
                    case "conc": if (int.TryParse(v, NumberStyles.Integer, ci, out var cc)) s.ConcussCharges = cc; break;
                    case "concr": if (float.TryParse(v, NumberStyles.Float, ci, out var cr)) s.ConcussRadius = cr; break;
                    case "caught": s.Caught = ParseCaught(v); break;
                    case "round": s.Structure = ParseStructure(v); break;
                    case "time": if (int.TryParse(v, NumberStyles.Integer, ci, out var tm)) s.TimeOfDay = tm; break;
                    case "weapon": s.HunterWeapon = v; break;
                    case "ff": s.FriendlyFire = v == "1"; break;
                }
            }
            return s;
        }

        public override string ToString() =>
            $"hide={HideSeconds}s hunt={HuntSeconds}s pph={PlayersPerHunter} caught={Caught} round={Structure} tag={TagRange} hits={HitsToCatch} area={PlayAreaRadius}";
    }
}
