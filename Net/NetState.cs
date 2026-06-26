namespace PropHunt.Net
{
    /// <summary>
    /// SteamNetworkLib state keys. PropHunt syncs its entire host-authoritative snapshot as ONE string
    /// (see PropHunt.Game.GameState) under a single HostSyncVar - host writes, all read, late-joiners get
    /// the current value for free. Discrete one-shot actions ride the typed P2P messages in Messages.cs.
    /// </summary>
    internal static class NetKeys
    {
        /// <summary>The single host-authoritative GameState blob (HostSyncVar, host writes / all read).</summary>
        internal const string State = "ph_state";
    }
}
