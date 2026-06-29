using System.Collections.Generic;
using PropHunt.Net;

namespace PropHunt.Game
{
    /// <summary>
    /// Best-effort SteamId64 &lt;-&gt; game Player map. Matches SteamNetworkLib member DisplayName to the game
    /// Player.PlayerName (the Steam persona). Persona names are not guaranteed unique, so a name shared by two
    /// players is treated as ambiguous and left unresolved rather than mapped to the wrong player. Rebuilt on
    /// demand (cheap; PlayerList is small).
    /// </summary>
    internal static class PlayerRegistry
    {
        private static readonly Dictionary<ulong, Player> _byId = new Dictionary<ulong, Player>();
        private static readonly Dictionary<string, ulong> _idByName = new Dictionary<string, ulong>();
        private static readonly HashSet<string> _ambiguous = new HashSet<string>();   // names shared by 2+ players -> never resolved

        internal static void Refresh()
        {
            _byId.Clear();
            _idByName.Clear();
            _ambiguous.Clear();
            try
            {
                // Steam personas are NOT unique (defaults, copies, empty names). A shared name would otherwise
                // silently resolve two SteamIds to one Player and mis-route host-authoritative catch/stun/disguise
                // to the wrong person, so any name seen on two members - or on two game Players - is marked
                // ambiguous and left unresolved (id 0), which every caller already skips.
                var members = PropHuntNet.Client?.GetLobbyMembers();
                if (members != null)
                    foreach (var m in members)
                    {
                        var name = m.DisplayName;
                        if (string.IsNullOrEmpty(name)) continue;
                        if (_idByName.TryGetValue(name, out var existing) && existing != m.SteamId64) { _ambiguous.Add(name); continue; }
                        _idByName[name] = m.SteamId64;
                    }

                var list = Player.PlayerList;
                if (list != null)
                {
                    var seen = new HashSet<string>();
                    for (int i = 0; i < list.Count; i++)
                    {
                        var p = list[i]; if (p == null) continue;
                        string n = null; try { n = p.PlayerName; } catch { }
                        if (!string.IsNullOrEmpty(n) && !seen.Add(n)) _ambiguous.Add(n);   // same persona on two Players
                    }
                    for (int i = 0; i < list.Count; i++)
                    {
                        var p = list[i]; if (p == null) continue;
                        string n = null; try { n = p.PlayerName; } catch { }
                        if (!string.IsNullOrEmpty(n) && !_ambiguous.Contains(n) && _idByName.TryGetValue(n, out var id)) _byId[id] = p;
                    }
                }
            }
            catch { }
        }

        internal static Player Get(ulong id)
        {
            return (_byId.TryGetValue(id, out var p) && p != null) ? p : null;
        }

        /// <summary>Resolve a hit game Player back to its SteamId64 (via PlayerName). 0 if unknown, or if the name
        /// is shared by another player (ambiguous) so a persona collision never mis-identifies a player.</summary>
        internal static ulong IdForPlayer(Player player)
        {
            if (player == null) return 0UL;
            try
            {
                string n = player.PlayerName;
                if (!string.IsNullOrEmpty(n) && !_ambiguous.Contains(n) && _idByName.TryGetValue(n, out var id)) return id;
            }
            catch { }
            return 0UL;
        }
    }
}
