using System;
using System.Collections.Generic;
using System.Globalization;
using PropHunt.Config;
using PropHunt.Disguise;
using PropHunt.Game;
using SideHustle;

namespace PropHunt.Phone
{
    /// <summary>
    /// The only place the phone app touches the game. Reads <see cref="GameModeController.Active"/> and hands
    /// <see cref="PhoneBackend"/> plain data.
    ///
    /// Nothing here re-checks host authority. Every command below lands on a controller method that already
    /// returns early for a client (BeginMatch, BeginNextRound, SwitchSafehouse, SetSetting, KickPlayer,
    /// RequestEndRound), so a client that somehow sent one gets a no-op rather than a second opinion. The app
    /// hides those controls as a courtesy, not as the check.
    /// </summary>
    internal sealed class GameHost : IPhoneHost
    {
        private static GameModeController Ctl => GameModeController.Active;

        /// <summary>
        /// The preset the host last applied from the phone, which is what the change marks are read against.
        ///
        /// It cannot be derived from the settings: the moment one value is tweaked they match no preset at all,
        /// and that is exactly the moment the marks matter most. Deriving it was the first attempt and it left
        /// every mark pointing at the saved host preference - invisible, and different on every machine.
        ///
        /// Static because it belongs to the session rather than to this object, and forgotten when the session
        /// ends. A round started from the Side Hustle host form leaves it empty, and the exact-match answer below
        /// stands in until someone picks one here.
        /// </summary>
        private static string _appliedPreset = "";

        internal static void ForgetPreset() => _appliedPreset = "";

        public bool Available => Ctl != null;

        public bool IsHost => Ctl?.IsHost ?? false;

        public string Phase => (Ctl?.Phase ?? RoundPhase.Lobby).ToString();

        public int RoundNumber => Ctl?.State?.RoundNumber ?? 0;

        public int Winner => Ctl?.State?.Winner ?? -1;

        public long Now => Ctl?.NowUnix() ?? 0;

        public long PhaseEndsAt => Ctl?.State?.PhaseEndsAtUnix ?? 0;

        /// <summary>
        /// How long the running phase was given. The page draws a depletion bar from this and the deadline, and
        /// there is no synced field for it - a phase's length is just the setting it was started from.
        /// </summary>
        public int PhaseLength
        {
            get
            {
                var c = Ctl;
                if (c == null) return 0;
                switch (c.Phase)
                {
                    case RoundPhase.Hiding: return Math.Max(1, c.Settings.HideSeconds);
                    case RoundPhase.Hunting: return Math.Max(1, c.Settings.HuntSeconds);
                    case RoundPhase.RoundEnd: return Math.Max(1, c.Settings.RoundEndSeconds);
                    default: return 0;
                }
            }
        }

        public int SecondsUntilNextRound => Ctl?.SecondsUntilNextRound ?? -1;

        public int SecondsToWhistle => Ctl?.SecondsToWhistle ?? -1;

        public int SecondsToPropRotation => Ctl?.SecondsToPropRotation ?? -1;

        public int AliveHiders => Ctl?.AliveHiderCount ?? 0;

        public int LobbyMembers => Ctl?.LobbyMemberCount ?? 0;

        public int BecomablePropCount
        {
            get { try { return PropCatalog.BecomableCount(); } catch { return 0; } }
        }

        public string PropImage(int propId) => PhoneImages.Prop(propId);

        public string PlayerImage(ulong steamId) => PhoneImages.Player(steamId);

        // ---- local player ----

        public LocalView Me
        {
            get
            {
                var v = new LocalView();
                var c = Ctl;
                if (c == null) return v;

                v.Id = c.LocalId;
                v.Role = c.LocalRole.ToString();
                v.Eliminated = c.LocalEliminated;
                v.Spectating = c.LocalSpectating;

                v.Hp = c.LocalHits;
                v.MaxHp = Math.Max(1, c.LocalMaxHits);
                v.HunterHp = c.LocalHunterHits;
                v.HunterMaxHp = Math.Max(1, c.LocalHunterMaxHits);

                v.PropId = c.WornPropId;
                v.PropName = v.PropId >= 0 ? PrettyPropName(SafeEntry(v.PropId)) : null;
                v.Locked = c.LocalLocked;

                v.Changes = c.LocalChanges;
                v.MaxChanges = c.Settings.MaxPropChanges;
                v.FreeChangesNow = c.Settings.FreeChangesInHiding && c.Phase == RoundPhase.Hiding;

                v.DecoysUsed = c.LocalDecoysUsed;
                v.MaxDecoys = c.Settings.MaxDecoys;
                v.ConcussUsed = c.LocalConcussUsed;
                v.MaxConcuss = c.Settings.ConcussCharges;

                v.Downed = c.LocalDowned;
                v.DownedSecondsLeft = c.LocalDownedSecondsLeft;

                v.Outside = c.LocalOutside;
                v.InWater = c.LocalInWater;
                v.OobGrace = (int)Math.Ceiling(Math.Max(0f, c.OobGrace));

                return v;
            }
        }

        // ---- roster ----

        public IReadOnlyList<RosterEntry> Roster
        {
            get
            {
                var rows = new List<RosterEntry>();
                var c = Ctl;
                if (c?.State?.Players == null) return rows;

                try { PlayerRegistry.Refresh(); } catch { }

                ulong local = c.LocalId;
                var states = new List<PlayerState>(c.State.Players.Values);
                states.Sort((a, b) =>
                {
                    int r = RoleRank(a).CompareTo(RoleRank(b));
                    return r != 0 ? r : b.SessScore.CompareTo(a.SessScore);
                });

                foreach (PlayerState p in states)
                {
                    bool self = p.SteamId == local && local != 0;
                    var row = new RosterEntry
                    {
                        Id = p.SteamId,
                        Name = NameOf(p.SteamId),
                        Role = p.Role.ToString(),
                        Eliminated = p.Eliminated,
                        Self = self,
                        Friend = !self && IsSteamFriend(p.SteamId),
                        Catches = p.CatchesMade,
                        SurvivedSeconds = p.SurvivedSeconds,
                        Score = p.SessScore,
                        HitsDealt = p.HitsDealt,
                        DecoyBaits = p.DecoyBaits,
                        StunsLanded = p.StunsLanded,
                        DecoysSmashed = p.DecoysSmashed,
                        Taunts = p.Taunts,
                        Hp = p.Hits,
                        MaxHp = Math.Max(1, p.MaxHits),
                    };

                    // The one rule the whole roster exists under: a hider who is still in the round has no prop on
                    // this list. Not hidden by the page - absent from the data, so nothing downstream can leak it.
                    // Yourself you may see, and someone already caught is no longer a secret worth keeping.
                    if (self || p.Eliminated)
                    {
                        int prop = self ? c.WornPropId : p.PropId;
                        if (prop >= 0)
                        {
                            row.PropId = prop;
                            row.PropName = PrettyPropName(SafeEntry(prop));
                        }
                    }

                    rows.Add(row);
                }

                return rows;
            }
        }

        /// <summary>
        /// Whether a player in the lobby is on the local Steam friends list.
        ///
        /// Cached for the process: the roster is rebuilt on every phone poll, and a friendship does not change during
        /// a round. A Steam call that is unavailable (no overlay, no Steam) answers "not a friend" - a missing badge
        /// is a non-event, a wrong one is a claim about who someone is.
        /// </summary>
        private static readonly Dictionary<ulong, bool> _friendCache = new Dictionary<ulong, bool>();

        private static bool IsSteamFriend(ulong steamId)
        {
            if (steamId == 0UL) return false;
            if (_friendCache.TryGetValue(steamId, out bool known)) return known;
            bool friend = false;
            try
            {
                friend = Il2CppSteamworks.SteamFriends.GetFriendRelationship(new Il2CppSteamworks.CSteamID(steamId))
                         == Il2CppSteamworks.EFriendRelationship.k_EFriendRelationshipFriend;
            }
            catch (Exception e) { Core.Log.Warning("could not read the Steam friend relationship: " + e.Message); }
            _friendCache[steamId] = friend;
            return friend;
        }

        // ---- rules ----

        public IReadOnlyList<SettingView> Settings
        {
            get
            {
                var views = new List<SettingView>();
                var c = Ctl;
                if (c == null) return views;

                Dictionary<string, string> values;
                try { values = c.Settings.ToValues(); }
                catch { return views; }

                SettingDescriptor[] spec;
                try { spec = PropHuntSettingsSpec.Build(); }
                catch { return views; }

                // Marks are measured against a preset rather than against the saved host preference, and a saved
                // "Custom - X" is measured against X - see BaselineFor and BaselinePresetFor.
                SettingPreset live = null;
                try
                {
                    // The one the host picked, if they picked one - it stays the reference after a tweak, which is
                    // when a mark is worth anything. Otherwise fall back to whichever preset the values still
                    // match exactly, so a session launched from the host form is not left without a baseline.
                    string against = _appliedPreset.Length > 0 ? _appliedPreset : ActivePreset;

                    if (against.Length > 0)
                    {
                        SettingPreset[] all = RoundPresets.Build();
                        foreach (SettingPreset p in all)
                            if (string.Equals(p.Name, against, StringComparison.Ordinal))
                            {
                                live = BaselinePresetFor(p, all);
                                break;
                            }
                    }
                }
                catch { }

                foreach (SettingDescriptor d in spec)
                {
                    values.TryGetValue(d.Key, out string value);
                    views.Add(new SettingView
                    {
                        Key = d.Key,
                        Label = d.Label,
                        Hint = d.Hint,
                        Category = d.Category,
                        Type = TypeName(d.Type),
                        Unit = d.Unit,
                        Value = value ?? d.Default,
                        Default = BaselineFor(d, live),
                        Min = d.Min,
                        Max = d.Max,
                        Step = d.Step <= 0 ? 1 : d.Step,
                        WholeNumbers = d.WholeNumbers,
                        Options = d.Options ?? Array.Empty<string>(),
                        Values = d.Values ?? d.Options ?? Array.Empty<string>(),
                    });
                }

                return views;
            }
        }

        private static string TypeName(SettingType t)
        {
            switch (t)
            {
                case SettingType.Toggle: return "toggle";
                case SettingType.Segmented: return "segmented";
                case SettingType.Dropdown: return "choice";
                case SettingType.Text: return "text";
                default: return "number";
            }
        }

        /// <summary>Preset names, host only - a client applying one would be a silent no-op and an empty list says
        /// so honestly.</summary>
        public IReadOnlyList<string> Presets
        {
            get
            {
                var names = new List<string>();
                if (!IsHost) return names;
                try { foreach (SettingPreset p in RoundPresets.Build()) names.Add(p.Name); }
                catch { }
                return names;
            }
        }

        /// <summary>
        /// The preset the live settings still match, or "" when they match none.
        ///
        /// Nothing records which preset was applied - `SetSetting` writes values, not a choice - so this asks the
        /// only question that can be answered from the state that exists: does every value this preset names still
        /// hold? Tweak one of them and the answer becomes no, which is exactly when the app should stop claiming
        /// a preset is selected.
        /// </summary>
        public string ActivePreset
        {
            get
            {
                var c = Ctl;
                if (c == null) return "";

                try
                {
                    Dictionary<string, string> live = c.Settings.ToValues();

                    foreach (SettingPreset p in RoundPresets.Build())
                    {
                        if (p.Values == null || p.Values.Count == 0) continue;
                        if (MatchesEveryValue(p, live)) return p.Name;
                    }
                }
                catch { }

                return "";
            }
        }

        /// <summary>What the change marks are read against. See <see cref="_appliedPreset"/>.</summary>
        public string BaselinePreset => _appliedPreset.Length > 0 ? _appliedPreset : ActivePreset;

        /// <summary>Every value the preset names, compared as text the way both sides were written.</summary>
        private static bool MatchesEveryValue(SettingPreset preset, Dictionary<string, string> live)
        {
            foreach (KeyValuePair<string, string> want in preset.Values)
            {
                if (!live.TryGetValue(want.Key, out string have)) return false;
                if (!string.Equals(have, want.Value, StringComparison.OrdinalIgnoreCase)) return false;
            }

            return true;
        }

        /// <summary>
        /// What a setting is measured against for the "changed" mark.
        ///
        /// The descriptor's own Default is the saved host preference, which nobody can see and which drifts with
        /// every session someone hosted. Measuring against a PRESET is the thing a player can reason about: the
        /// mark then means "this is not what Classic Hunt says", and it clears again when the value goes back.
        ///
        /// A saved "Custom - X" is measured against X, not against itself. It is X with someone's tweaks - that is
        /// what its name says and what its Mode records - so measuring it against its own values would mark
        /// nothing, which is exactly the question it exists to answer.
        /// </summary>
        private static string BaselineFor(SettingDescriptor d, SettingPreset baseline)
        {
            if (baseline?.Values != null && baseline.Values.TryGetValue(d.Key, out string fromPreset)) return fromPreset;
            return d.Default;
        }

        /// <summary>
        /// The preset a given preset's changes should be read against: the one its <c>Mode</c> names when that is a
        /// different preset - "Custom - Classic Hunt" carries Mode "Classic Hunt" - and otherwise itself.
        /// </summary>
        private static SettingPreset BaselinePresetFor(SettingPreset active, SettingPreset[] all)
        {
            if (active == null) return null;
            if (string.IsNullOrEmpty(active.Mode) || string.Equals(active.Mode, active.Name, StringComparison.Ordinal))
                return active;

            foreach (SettingPreset p in all)
                if (string.Equals(p.Name, active.Mode, StringComparison.Ordinal)) return p;

            return active;   // its base is not on the list any more - better its own values than the hidden default
        }

        // ---- safehouse ----

        public SafehouseView Safehouse
        {
            get
            {
                var v = new SafehouseView();
                var c = Ctl;
                if (c?.State == null) return v;

                v.Code = c.State.SafehouseCode ?? "";
                v.Ready = c.State.SafehouseReady;
                try { v.Name = c.SafehouseName(v.Code) ?? v.Code; } catch { v.Name = v.Code; }
                try { v.OptionCount = c.SafehouseOptionCount; } catch { }
                return v;
            }
        }

        // ---- awards ----

        public IReadOnlyList<AwardView> Awards
        {
            get
            {
                var list = new List<AwardView>();
                var c = Ctl;
                if (c?.State?.Players == null) return list;

                var players = new List<PlayerState>(c.State.Players.Values);
                Award(list, players, "Top Hunter", p => p.CatchesMade, "catches");
                Award(list, players, "Survivor", p => p.Role == PlayerRole.Hider ? p.SurvivedSeconds : 0, "s alive");
                Award(list, players, "Trickster", p => p.DecoyBaits, "decoy baits");
                Award(list, players, "Shocker", p => p.StunsLanded, "stuns");
                return list;
            }
        }

        private void Award(List<AwardView> list, List<PlayerState> players, string label, Func<PlayerState, int> pick, string unit)
        {
            PlayerState best = null;
            int bestValue = 0;
            foreach (PlayerState p in players)
            {
                int v = pick(p);
                if (v > bestValue) { bestValue = v; best = p; }
            }

            if (best == null || bestValue <= 0) return;
            list.Add(new AwardView
            {
                Label = label,
                Name = NameOf(best.SteamId),
                Value = bestValue.ToString(CultureInfo.InvariantCulture) + " " + unit,
            });
        }

        // ---- commands ----

        public string BeginMatch() => Run(c => c.BeginMatch());

        public string BeginNextRound() => Run(c => c.BeginNextRound());

        public string EndRound() => Run(c => c.RequestEndRound());

        public string ReturnToHub() => Run(c => c.RequestReturnToHub());

        public string SwitchSafehouse(int delta) => Run(c => c.SwitchSafehouse(delta >= 0 ? 1 : -1));

        public string SetSetting(string key, string value) => Run(c => c.SetSetting(key, value));

        public string Kick(ulong steamId) => steamId == 0 ? "error" : Run(c => c.KickPlayer(steamId));

        public string RollProp() => Run(_ => PropPreview.Roll());

        public string ClearProp() => Run(_ => PropPreview.Clear());

        /// <summary>
        /// Apply every value of a named preset. This is the one thing the pre-lobby host form could do and the
        /// phone could not, which is why a host had to relaunch the whole session to switch to Panic Room.
        /// Values land through the same SetSetting the individual rows use, so they take effect at the next
        /// round boundary exactly as a hand edit would.
        /// </summary>
        public string ApplyPreset(string name)
        {
            var c = Ctl;
            if (c == null || !c.IsHost || string.IsNullOrEmpty(name)) return "error";

            try
            {
                foreach (SettingPreset p in RoundPresets.Build())
                {
                    if (!string.Equals(p.Name, name, StringComparison.Ordinal)) continue;
                    if (p.Values == null) return "error";

                    foreach (KeyValuePair<string, string> kv in p.Values) c.SetSetting(kv.Key, kv.Value);
                    _appliedPreset = p.Name;
                    return "ok";
                }
            }
            catch (Exception e) { Core.LogDebug("preset failed: " + e.Message); }

            return "error";
        }

        private static string Run(Action<GameModeController> action)
        {
            var c = Ctl;
            if (c == null) return "error";

            try { action(c); return "ok"; }
            catch (Exception e) { Core.LogDebug("phone command failed: " + e.Message); return "error"; }
        }

        // ---- naming ----

        private static PropEntry SafeEntry(int propId)
        {
            try { return PropCatalog.ById(propId); } catch { return null; }
        }

        private static string NameOf(ulong steamId)
        {
            try
            {
                var gp = PlayerRegistry.Get(steamId);
                string n = gp?.PlayerName;
                if (!string.IsNullOrEmpty(n)) return n;
            }
            catch { }
            return "Player " + (steamId % 10000);
        }

        /// <summary>Sort order for the roster: hunters, then living hiders, then caught, then spectators.</summary>
        private static int RoleRank(PlayerState p)
        {
            if (p.Role == PlayerRole.Hunter) return 0;
            if (p.Role == PlayerRole.Hider) return p.Eliminated ? 2 : 1;
            return 3;
        }

        /// <summary>
        /// A catalog entry's display name, cleaned up for a player-facing list. Mesh names carry authoring noise
        /// (SM_ prefixes, LOD suffixes, underscores) and the composite entries are keyed by a namespaced key
        /// rather than a mesh, so those are read from the key instead.
        /// </summary>
        internal static string PrettyPropName(PropEntry e)
        {
            if (e == null) return "Prop";

            string raw = e.Name ?? "";
            string key = e.Key ?? "";
            int colon = key.IndexOf(':');

            // "reg:GoldenToilet" -> "GoldenToilet". But world objects are keyed by their mesh CONTENT HASH
            // ("world:6e6a7880" - see PropSources), and taking that suffix put an internal id where a name goes:
            // a player stood in the lobby wearing something called 6e6a7880. Only take the suffix when it reads
            // like a name; otherwise the mesh's own name is the better guess.
            if (colon > 0 && colon < key.Length - 1)
            {
                string suffix = key.Substring(colon + 1);
                if (!LooksLikeAnId(suffix)) raw = suffix;
            }

            if (raw.StartsWith("SM_", StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(3);
            int lod = raw.IndexOf("_LOD", StringComparison.OrdinalIgnoreCase);
            if (lod > 0) raw = raw.Substring(0, lod);
            raw = raw.Replace('_', ' ').Trim();
            if (raw.Length == 0) return "Prop";

            // "GoldenToilet" -> "Golden Toilet": the registry names are PascalCase and unreadable as one word.
            var sb = new System.Text.StringBuilder(raw.Length + 4);
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (i > 0 && char.IsUpper(ch) && !char.IsUpper(raw[i - 1]) && raw[i - 1] != ' ') sb.Append(' ');
                sb.Append(ch);
            }

            raw = sb.ToString();
            if (LooksLikeAnId(raw)) return "Unnamed prop";

            return char.ToUpperInvariant(raw[0]) + raw.Substring(1);
        }

        /// <summary>
        /// Whether a string is an internal identifier rather than something to show a player: a bare run of hex
        /// digits, which is what a content hash looks like. Six characters is the shortest hash the catalog emits
        /// and long enough that a real word ("decade", "facade") is not mistaken for one - both of those carry
        /// letters outside a-f.
        /// </summary>
        private static bool LooksLikeAnId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 6) return false;

            foreach (char c in s)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }

            return true;
        }
    }
}
