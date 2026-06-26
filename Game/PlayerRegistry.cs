using System.Collections.Generic;
using PropHunt.Net;

namespace PropHunt.Game
{
    /// <summary>
    /// Best-effort SteamId64 &lt;-&gt; game Player map. Matches SteamNetworkLib member DisplayName to the game
    /// Player.PlayerName (the Steam persona). TODO(testing): if personas can collide, harden via the
    /// FishySteamworks connection -&gt; steamid path. Rebuilt on demand (cheap; PlayerList is small).
    /// </summary>
    internal static class PlayerRegistry
    {
        private static readonly Dictionary<ulong, Player> _byId = new Dictionary<ulong, Player>();
        private static readonly Dictionary<string, ulong> _idByName = new Dictionary<string, ulong>();

        internal static void Refresh()
        {
            _byId.Clear();
            _idByName.Clear();
            try
            {
                var members = PropHuntNet.Client?.GetLobbyMembers();
                if (members != null)
                    foreach (var m in members)
                        if (!string.IsNullOrEmpty(m.DisplayName)) _idByName[m.DisplayName] = m.SteamId64;

                var list = Player.PlayerList;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var p = list[i];
                        if (p == null) continue;
                        string n = null;
                        try { n = p.PlayerName; } catch { }
                        if (!string.IsNullOrEmpty(n) && _idByName.TryGetValue(n, out var id)) _byId[id] = p;
                    }
            }
            catch { }
        }

        internal static Player Get(ulong id)
        {
            return (_byId.TryGetValue(id, out var p) && p != null) ? p : null;
        }

        /// <summary>Resolve a hit game Player back to its SteamId64 (via PlayerName). 0 if unknown.</summary>
        internal static ulong IdForPlayer(Player player)
        {
            if (player == null) return 0UL;
            try
            {
                string n = player.PlayerName;
                if (!string.IsNullOrEmpty(n) && _idByName.TryGetValue(n, out var id)) return id;
            }
            catch { }
            return 0UL;
        }
    }
}
