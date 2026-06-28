namespace PropHunt.Game
{
    /// <summary>The round lifecycle. Host-authoritative; clients render the synced phase.</summary>
    internal enum RoundPhase
    {
        Lobby,      // in the Side Hustle session, host configuring on the setup screen / waiting
        Hiding,     // hiders pick + lock a prop while hunters are frozen + blinded
        Hunting,    // hunters search; taunts fire; catches happen
        RoundEnd,   // result shown briefly
        Safehouse,  // between-rounds lobby inside a property interior: doors locked, host reconfigures + starts next round
        MatchEnd    // whole match over -> return to the Side Hustle hub
    }

    /// <summary>A player's role for the current round.</summary>
    internal enum PlayerRole
    {
        Unassigned,
        Hider,
        Hunter,
        Spectator   // caught (Spectator mode) or a late-joiner waiting for the next round
    }

    /// <summary>Host-selectable: what happens to a hider when caught.</summary>
    internal enum CaughtBehavior
    {
        Spectator,  // classic: sit out until the round ends
        Infection   // caught hider becomes a hunter; the hunter team grows
    }

    /// <summary>Host-selectable: what happens when a round ends.</summary>
    internal enum RoundStructure
    {
        Continuous, // auto-start the next round with swapped roles until someone returns to the hub
        Single      // one round, then back to the Side Hustle hub
    }
}
