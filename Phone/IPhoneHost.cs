using System.Collections.Generic;

namespace PropHunt.Phone
{
    /// <summary>
    /// Everything the phone app is allowed to know and do, as plain data and plain strings.
    ///
    /// This exists so <see cref="PhoneBackend"/> - the half that decides what the page sees - can be compiled and
    /// tested without Unity, IL2CPP or a running match. <see cref="GameHost"/> is the only implementation that
    /// touches the game; the tests supply their own.
    ///
    /// Two rules this interface enforces by shape rather than by discipline:
    ///
    ///  - A living hider's prop never appears on it. <see cref="RosterEntry.PropId"/> is filled for the local
    ///    player and for anyone already caught, and is -1 otherwise, so the prop of someone still hiding is not
    ///    merely hidden by the page - it never crosses the bridge. The page cannot leak what it was never sent.
    ///  - Every command returns a string, and a refusal is a value rather than an exception. The host checks live
    ///    on <c>GameModeController</c> anyway; this layer only reports what happened.
    /// </summary>
    internal interface IPhoneHost
    {
        /// <summary>False when there is no PropHunt session at all - the app then shows how to start one.</summary>
        bool Available { get; }

        bool IsHost { get; }

        /// <summary>Round phase name: Lobby, Hiding, Hunting, RoundEnd, Safehouse, MatchEnd.</summary>
        string Phase { get; }

        int RoundNumber { get; }

        /// <summary>-1 none, 0 hunters, 1 hiders.</summary>
        int Winner { get; }

        /// <summary>Wall clock in HOST time. The page derives every countdown from this and the deadlines below,
        /// so a client whose own clock disagrees still shows the host's numbers.</summary>
        long Now { get; }

        /// <summary>Absolute host-time unix second the current phase ends, or 0 when it has no clock.</summary>
        long PhaseEndsAt { get; }

        /// <summary>How many seconds the current phase was given, so the page can draw how much is used up.</summary>
        int PhaseLength { get; }

        /// <summary>One composed countdown across RoundEnd -> Safehouse -> doors, or -1 when unpredictable.</summary>
        int SecondsUntilNextRound { get; }

        /// <summary>Seconds to the next forced whistle, or -1 when none is pending.</summary>
        int SecondsToWhistle { get; }

        /// <summary>Seconds to the next forced prop reshuffle, or -1 when off.</summary>
        int SecondsToPropRotation { get; }

        int AliveHiders { get; }

        /// <summary>Live Steam lobby size. The synced roster is empty before the match starts, so the Lobby
        /// screen and the start gate need this instead.</summary>
        int LobbyMembers { get; }

        LocalView Me { get; }

        IReadOnlyList<RosterEntry> Roster { get; }

        IReadOnlyList<SettingView> Settings { get; }

        /// <summary>Preset names the host may apply live. Empty for clients.</summary>
        IReadOnlyList<string> Presets { get; }

        /// <summary>The preset the live settings still match, or "" when a value has been tweaked away from all
        /// of them. Nothing records which one was applied, so this is answered by comparing values.</summary>
        string ActivePreset { get; }

        SafehouseView Safehouse { get; }

        IReadOnlyList<AwardView> Awards { get; }

        /// <summary>How many props exist to try on right here, for the lobby dressing room's empty state.</summary>
        int BecomablePropCount { get; }

        /// <summary>The <c>s1://</c> key of a ready picture for this prop, or null while none has been rendered.
        /// Asking also queues the render, so the page gets it on a later snapshot.</summary>
        string PropImage(int propId);

        /// <summary>The <c>s1://</c> key of a ready portrait for this player, or null. Queues on ask, same as above.</summary>
        string PlayerImage(ulong steamId);

        // ---- commands ----
        string BeginMatch();
        string BeginNextRound();
        string EndRound();
        string ReturnToHub();
        string SwitchSafehouse(int delta);
        string SetSetting(string key, string value);
        string ApplyPreset(string name);
        string Kick(ulong steamId);
        string RollProp();
        string ClearProp();
    }

    /// <summary>The local player's own state - the left rail's whole job.</summary>
    internal sealed class LocalView
    {
        internal ulong Id;
        internal string Role = "Unassigned";
        internal bool Eliminated;
        internal bool Spectating;

        internal int Hp;
        internal int MaxHp = 1;
        internal int HunterHp;
        internal int HunterMaxHp = 1;

        internal int PropId = -1;
        internal string PropName;
        internal bool Locked;

        internal int Changes;
        internal int MaxChanges;          // 0 = unlimited
        internal bool FreeChangesNow;     // changes during Hiding do not count

        internal int DecoysUsed;
        internal int MaxDecoys;
        internal int ConcussUsed;
        internal int MaxConcuss;

        internal bool Downed;
        internal int DownedSecondsLeft;

        internal bool Outside;
        internal bool InWater;
        internal int OobGrace;
    }

    /// <summary>One row of the roster. See the interface docs for why <see cref="PropId"/> is often -1.</summary>
    internal sealed class RosterEntry
    {
        internal ulong Id;
        internal string Name = "";
        internal string Role = "Unassigned";
        internal bool Eliminated;
        internal bool Self;

        internal int Catches;
        internal int SurvivedSeconds;
        internal int Score;
        internal int HitsDealt;
        internal int DecoyBaits;
        internal int StunsLanded;
        internal int DecoysSmashed;
        internal int Taunts;

        /// <summary>Only meaningful for the local player.</summary>
        internal int Hp;
        internal int MaxHp = 1;

        /// <summary>-1 unless this is the local player or they are already out of the round.</summary>
        internal int PropId = -1;
        internal string PropName;
    }

    /// <summary>One host-configurable rule, already merged with its current value.</summary>
    internal sealed class SettingView
    {
        internal string Key = "";
        internal string Label = "";
        internal string Hint = "";
        internal string Category = "";

        /// <summary>number | toggle | segmented | choice | text</summary>
        internal string Type = "number";

        internal string Unit = "";
        internal string Value = "";
        internal string Default = "";

        internal double Min;
        internal double Max;
        internal double Step = 1;
        internal bool WholeNumbers = true;

        /// <summary>Display labels for segmented / choice. Empty otherwise.</summary>
        internal string[] Options = System.Array.Empty<string>();

        /// <summary>Wire values matching <see cref="Options"/> one for one.</summary>
        internal string[] Values = System.Array.Empty<string>();
    }

    internal sealed class SafehouseView
    {
        internal string Name = "";
        internal string Code = "";
        internal int OptionCount;
        internal bool Ready;
    }

    internal sealed class AwardView
    {
        internal string Label = "";
        internal string Name = "";
        internal string Value = "";
    }
}
