using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PropHunt.Game
{
    /// <summary>One player's synced per-round record (keyed by 64-bit Steam id).</summary>
    internal sealed class PlayerState
    {
        internal ulong SteamId;
        internal PlayerRole Role = PlayerRole.Unassigned;
        internal int PropId = -1;      // chosen prop catalog id, -1 = none
        internal bool Locked;          // hider committed to the prop
        internal bool Eliminated;      // caught (or out of bounds)
        internal int Hits;             // hunter hits taken on the current prop (caught at MaxHits)
        internal int MaxHits = 1;      // size-based HP of the current prop (bigger prop = more hits to catch)
        internal int Changes;          // how many times this hider has (re)picked a prop this round
        internal float PropYaw;        // manual prop facing (degrees), set by [F]+mouse; prop is world-fixed at this yaw
        internal int DecoysUsed;       // decoys dropped this round ([Q])
        internal int ConcussUsed;      // concussion grenades used this round ([G])

        // ---- per-round stats (reset each round; drive the round-end scoreboard + awards) ----
        internal int CatchesMade;      // hiders this player caught (hunter)
        internal int HitsDealt;        // hits this player landed on hiders (hunter)
        internal int DecoyBaits;       // hits hunters wasted on this player's decoys (hider)
        internal int StunsLanded;      // hunters this player's concussions stunned (hider)
        internal int SurvivedSeconds;  // seconds this hider survived the hunt (set when caught / at round end)

        // ---- session stats (NOT reset per round; the leaderboard) ----
        internal int SessScore;        // cumulative score across all rounds of this match

        internal PlayerState() { }
        internal PlayerState(ulong id) { SteamId = id; }
    }

    /// <summary>A decoy: a static, render-only copy of a prop left in the world to mislead hunters.</summary>
    internal sealed class DecoyState
    {
        internal float X, Y, Z, Yaw;
        internal int PropId;
        internal int Hits;
        internal int MaxHits;
        internal bool Destroyed;
        internal ulong OwnerSteamId;   // the hider who dropped it (credited with "decoy baits" when hunters shoot it)
    }

    /// <summary>
    /// The full host-authoritative snapshot, synced as ONE string via the SteamNetworkLib "ph_state"
    /// HostSyncVar. The host mutates it and pushes; every client parses it on change and renders. A late
    /// joiner reads the current lobby-data value for a free snapshot. Newline-delimited so the settings
    /// blob (which uses ';' and '=') and the per-player rows (which use '|') never collide.
    /// </summary>
    internal sealed class GameState
    {
        internal RoundPhase Phase = RoundPhase.Lobby;
        internal long PhaseEndsAtUnix;          // 0 = no timer; else absolute unix seconds
        internal int RoundNumber;
        internal int CatalogHash;               // prop-catalog handshake
        internal float AreaX, AreaY, AreaZ, AreaRadius; // play-area centre + radius (world units)
        internal int Winner = -1;               // -1 none, 0 hunters won, 1 hiders won (RoundEnd display)
        internal string SettingsBlob = "";      // RoundSettings.Serialize()
        internal string SafehouseCode = "";     // property code of the between-rounds safehouse ("" = none / not in Safehouse)
        internal bool SafehouseReady;           // host pressed "start next round" -> doors about to open
        internal int SafehouseSeed;             // host-rolled seed -> all clients shuffle the spawn points the same way
        internal long HuntStartUnix;            // unix time the current Hunting phase began (for survival-time stats)
        internal readonly Dictionary<ulong, PlayerState> Players = new Dictionary<ulong, PlayerState>();
        internal readonly List<DecoyState> Decoys = new List<DecoyState>();

        internal PlayerState GetOrAdd(ulong id)
        {
            if (!Players.TryGetValue(id, out var s)) { s = new PlayerState(id); Players[id] = s; }
            return s;
        }

        internal string Serialize()
        {
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append((int)Phase).Append('|').Append(PhaseEndsAtUnix.ToString(ci)).Append('|')
              .Append(RoundNumber.ToString(ci)).Append('|').Append(CatalogHash.ToString(ci)).Append('|')
              .Append(AreaX.ToString(ci)).Append('|').Append(AreaZ.ToString(ci)).Append('|')
              .Append(AreaRadius.ToString(ci)).Append('|').Append(Winner.ToString(ci))
              .Append('|').Append(AreaY.ToString(ci))
              .Append('|').Append(SafehouseCode ?? "").Append('|').Append(SafehouseReady ? '1' : '0')
              .Append('|').Append(SafehouseSeed.ToString(ci))
              .Append('|').Append(HuntStartUnix.ToString(ci));
            sb.Append('\n').Append(SettingsBlob ?? "");
            foreach (var p in Players.Values)
            {
                sb.Append('\n')
                  .Append(p.SteamId.ToString(ci)).Append('|')
                  .Append((int)p.Role).Append('|')
                  .Append(p.PropId.ToString(ci)).Append('|')
                  .Append(p.Locked ? '1' : '0').Append('|')
                  .Append(p.Eliminated ? '1' : '0').Append('|')
                  .Append(p.Hits.ToString(ci)).Append('|')
                  .Append(p.MaxHits.ToString(ci)).Append('|')
                  .Append(p.Changes.ToString(ci)).Append('|')
                  .Append(p.PropYaw.ToString(ci)).Append('|')
                  .Append(p.DecoysUsed.ToString(ci)).Append('|')
                  .Append(p.ConcussUsed.ToString(ci)).Append('|')
                  .Append(p.CatchesMade.ToString(ci)).Append('|')
                  .Append(p.HitsDealt.ToString(ci)).Append('|')
                  .Append(p.DecoyBaits.ToString(ci)).Append('|')
                  .Append(p.StunsLanded.ToString(ci)).Append('|')
                  .Append(p.SurvivedSeconds.ToString(ci)).Append('|')
                  .Append(p.SessScore.ToString(ci));
            }
            foreach (var d in Decoys)
            {
                sb.Append('\n').Append("D|")
                  .Append(d.X.ToString(ci)).Append('|').Append(d.Y.ToString(ci)).Append('|').Append(d.Z.ToString(ci)).Append('|')
                  .Append(d.Yaw.ToString(ci)).Append('|').Append(d.PropId.ToString(ci)).Append('|')
                  .Append(d.Hits.ToString(ci)).Append('|').Append(d.MaxHits.ToString(ci)).Append('|')
                  .Append(d.Destroyed ? '1' : '0').Append('|')
                  .Append(d.OwnerSteamId.ToString(ci));
            }
            return sb.ToString();
        }

        internal static GameState Parse(string blob)
        {
            var gs = new GameState();
            if (string.IsNullOrEmpty(blob)) return gs;
            var ci = CultureInfo.InvariantCulture;
            var lines = blob.Split('\n');
            if (lines.Length > 0)
            {
                var h = lines[0].Split('|');
                if (h.Length >= 8)
                {
                    gs.Phase = (RoundPhase)SafeInt(h[0]);
                    long.TryParse(h[1], NumberStyles.Integer, ci, out gs.PhaseEndsAtUnix);
                    gs.RoundNumber = SafeInt(h[2]);
                    gs.CatalogHash = SafeInt(h[3]);
                    gs.AreaX = SafeFloat(h[4]); gs.AreaZ = SafeFloat(h[5]); gs.AreaRadius = SafeFloat(h[6]);
                    gs.Winner = SafeInt(h[7]);
                    if (h.Length >= 9) gs.AreaY = SafeFloat(h[8]);
                    if (h.Length >= 10) gs.SafehouseCode = h[9] ?? "";
                    if (h.Length >= 11) gs.SafehouseReady = h[10] == "1";
                    if (h.Length >= 12) gs.SafehouseSeed = SafeInt(h[11]);
                    if (h.Length >= 13) long.TryParse(h[12], NumberStyles.Integer, ci, out gs.HuntStartUnix);
                }
            }
            if (lines.Length > 1) gs.SettingsBlob = lines[1];
            for (int i = 2; i < lines.Length; i++)
            {
                var f = lines[i].Split('|');
                if (f.Length >= 1 && f[0] == "D")    // decoy row
                {
                    if (f.Length >= 6)
                    {
                        var d = new DecoyState
                        {
                            X = SafeFloat(f[1]), Y = SafeFloat(f[2]), Z = SafeFloat(f[3]),
                            Yaw = SafeFloat(f[4]), PropId = SafeInt(f[5]),
                            // fields 6/7/8 only present in newer snapshots; older rows default to 0/0/false
                            Hits      = f.Length >= 7 ? SafeInt(f[6])   : 0,
                            MaxHits   = f.Length >= 8 ? SafeInt(f[7])   : 1,   // match the live >=1 invariant for old snapshots
                            Destroyed = f.Length >= 9 && f[8] == "1",
                            OwnerSteamId = f.Length >= 10 ? SafeULong(f[9]) : 0UL,
                        };
                        gs.Decoys.Add(d);
                    }
                    continue;
                }
                if (f.Length < 5) continue;
                if (!ulong.TryParse(f[0], NumberStyles.Integer, ci, out var id)) continue;
                var p = gs.GetOrAdd(id);
                p.Role = (PlayerRole)SafeInt(f[1]);
                p.PropId = SafeInt(f[2]);
                p.Locked = f[3] == "1";
                p.Eliminated = f[4] == "1";
                if (f.Length >= 6) p.Hits = SafeInt(f[5]);
                if (f.Length >= 7) p.MaxHits = SafeInt(f[6]);
                if (f.Length >= 8) p.Changes = SafeInt(f[7]);
                if (f.Length >= 9) p.PropYaw = SafeFloat(f[8]);
                if (f.Length >= 10) p.DecoysUsed = SafeInt(f[9]);
                if (f.Length >= 11) p.ConcussUsed = SafeInt(f[10]);
                if (f.Length >= 12) p.CatchesMade = SafeInt(f[11]);
                if (f.Length >= 13) p.HitsDealt = SafeInt(f[12]);
                if (f.Length >= 14) p.DecoyBaits = SafeInt(f[13]);
                if (f.Length >= 15) p.StunsLanded = SafeInt(f[14]);
                if (f.Length >= 16) p.SurvivedSeconds = SafeInt(f[15]);
                if (f.Length >= 17) p.SessScore = SafeInt(f[16]);
            }
            return gs;
        }

        private static int SafeInt(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        private static ulong SafeULong(string s) => ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0UL;
        private static float SafeFloat(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
    }
}
