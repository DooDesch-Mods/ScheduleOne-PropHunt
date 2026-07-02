using System.Collections.Generic;

namespace PropHunt.Game
{
    /// <summary>
    /// SteamId64 &lt;-&gt; game Player map, keyed off the game's OWN replicated identity, not Steam persona names.
    /// Each Player carries <c>PlayerCode</c> - a SyncVar&lt;string&gt; (ReadPermission.Observers) into which the owning
    /// client writes <c>SteamUser.GetSteamID().m_SteamID</c> as a decimal string; it replicates to every peer. So a
    /// remote player resolves to its real SteamId64 even when Steam has not downloaded that peer's persona name
    /// (which is empty for non-friends on a real Steam session and was the reason the old name-matching left remotes
    /// unresolved -> no disguise / no catch). Rebuilt on demand (cheap; PlayerList is small).
    /// </summary>
    internal static class PlayerRegistry
    {
        private static readonly Dictionary<ulong, Player> _byId = new Dictionary<ulong, Player>();

        internal static void Refresh()
        {
            _byId.Clear();
            try
            {
                var list = Player.PlayerList;
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var p = list[i]; if (p == null) continue;
                    ulong id = SteamIdOf(p);
                    if (id != 0UL) _byId[id] = p;   // PlayerCode is a unique SteamId - no ambiguity to guard against
                }
            }
            catch { }
        }

        internal static Player Get(ulong id)
        {
            if (id == 0UL) return null;
            if (_byId.TryGetValue(id, out var p) && p != null) return p;
            // Lazy fallback: PlayerCode may have populated (name-sync RPC round-trip) AFTER the last Refresh, or a
            // caller may query without a fresh Refresh. Re-scan once so a just-arrived peer resolves immediately.
            try
            {
                var list = Player.PlayerList;
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var q = list[i];
                        if (q != null && SteamIdOf(q) == id) { _byId[id] = q; return q; }
                    }
            }
            catch { }
            return null;
        }

        /// <summary>Resolve a hit game Player back to its SteamId64 (via the replicated PlayerCode). 0 if the code has
        /// not populated yet (owner's name-sync RPC still in flight) or Steam is offline.</summary>
        internal static ulong IdForPlayer(Player player) => SteamIdOf(player);

        /// <summary>The owning user's SteamId64 read from the replicated <c>Player.PlayerCode</c> SyncVar. Machine- and
        /// name-independent: the same value on host and every client. 0 while still empty (pre-RPC) or "0" (offline).</summary>
        internal static ulong SteamIdOf(Player player)
        {
            if (player == null) return 0UL;
            try
            {
                string code = player.PlayerCode;
                if (!string.IsNullOrEmpty(code) && ulong.TryParse(code, out var id) && id != 0UL) return id;
            }
            catch { }
            return 0UL;
        }
    }
}
