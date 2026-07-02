using System;
using System.Collections.Generic;
using Il2CppScheduleOne.UI.Phone;   // JournalApp (the rest - Player/PlayerSingleton/Singleton/HUD - are global usings)
using S1API.Quests;
using S1API.Quests.Constants;
using PropHunt.Game;

namespace PropHunt.Quests
{
    /// <summary>
    /// Drives the PropHunt journal quest as a live set of role-aware objectives, using the quest system fully:
    ///  - Several objectives can be active AT ONCE (e.g. a client sees "Open the app to track" + "Wait for round 2").
    ///  - Each objective is a real entry that gets a visible CHECKMARK (Active -> Completed) when achieved, or is
    ///    FAILED (Active -> Failed) when lost - so the round objective resolves to ✓ or ✗ by the round outcome.
    ///  - HOST gets control objectives ("Start the match" / "Start round N"); CLIENTS get the matching "Wait for ..."
    ///    objective; everyone gets the role objective during a round (Hider: survive; Hunter: catch the props).
    /// All entries live under the one persistent "PropHunt" quest (allow-listed), kept alive by an invisible Inactive
    /// anchor entry so completing/failing objectives never auto-ends it.
    /// </summary>
    internal static class GuideQuest
    {
        internal const string Title = "PropHunt";

        private static PropHuntGuideQuest _quest;
        private static readonly Dictionary<string, QuestEntry> _active = new Dictionary<string, QuestEntry>();
        private static readonly Dictionary<string, string> _titles = new Dictionary<string, string>();
        private static readonly List<string> _toRemove = new List<string>();
        private static bool _appOpened;

        internal static void Tick()
        {
            var ctl = GameModeController.Active;
            if (ctl == null) return;
            if (!QuestUiReady()) return;

            var desired = Desired(ctl);

            if (_quest == null)
            {
                if (desired.Count == 0) return;   // nothing to show yet
                try { _quest = QuestManager.CreateQuest<PropHuntGuideQuest>() as PropHuntGuideQuest; }
                catch (Exception e) { Core.Log.Warning("[PropHunt] guide quest create failed: " + e.Message); return; }
                if (_quest == null) return;
            }

            // resolve + remove objectives that are no longer desired (✓ achieved, ✗ lost)
            _toRemove.Clear();
            foreach (var kv in _active) if (!desired.ContainsKey(kv.Key)) _toRemove.Add(kv.Key);
            foreach (var key in _toRemove)
            {
                var entry = _active[key];
                bool won = Resolve(key, ctl);
                try
                {
                    string outcome = ResolveTitle(key, won);
                    if (outcome != null) entry.Title = outcome;
                    if (won) entry.Complete();                       // Active -> Completed: visible ✓
                    else entry.SetState(QuestState.Failed);          // Active -> Failed: visible ✗
                }
                catch { }
                _active.Remove(key);
                _titles.Remove(key);
            }

            // add newly-desired objectives (Active so the row shows + can later check off) + refresh changed titles
            foreach (var kv in desired)
            {
                if (!_active.ContainsKey(kv.Key))
                {
                    try { _active[kv.Key] = _quest.AddObjective(kv.Value); _titles[kv.Key] = kv.Value; }
                    catch (Exception e) { Core.Log.Warning("[PropHunt] guide quest add failed: " + e.Message); }
                }
                else if (_titles.TryGetValue(kv.Key, out var t) && t != kv.Value)
                {
                    try { _active[kv.Key].Title = kv.Value; } catch { }
                    _titles[kv.Key] = kv.Value;
                }
            }
        }

        // The set of objectives that should be ACTIVE right now, keyed so each persists until its phase/condition ends.
        private static Dictionary<string, string> Desired(GameModeController ctl)
        {
            var d = new Dictionary<string, string>();
            bool host = ctl.IsHost;
            bool spec = ctl.LocalSpectating;
            var role = ctl.LocalRole;

            if (!_appOpened)
                d["open"] = host ? "Open the PropHunt app to run the match." : "Open the PropHunt app to track the match.";

            switch (ctl.Phase)
            {
                case RoundPhase.Lobby:
                    if (host) d["start_match"] = "Start the match in the PropHunt app.";
                    else d["wait_match"] = "Wait for the host to start the match.";
                    break;
                case RoundPhase.Safehouse:
                    int n = ctl.State.RoundNumber + 1;   // the round about to start
                    if (host) d["start_round"] = $"Start round {n} in the PropHunt app.";
                    else d["wait_round"] = $"Wait for round {n} to begin.";
                    break;
                case RoundPhase.Hiding:
                    if (!spec)
                        d["prep"] = role == PlayerRole.Hunter
                            ? "Get ready - you're blinded until the hunt begins."
                            : "Disguise as a prop and find a hiding spot.";
                    break;
                case RoundPhase.Hunting:
                    if (!spec)
                    {
                        if (role == PlayerRole.Hunter) d["round_hunt"] = "Find and catch every hidden prop.";
                        else if (role == PlayerRole.Hider) d["round_hide"] = "Stay hidden and survive until the hunt ends.";
                    }
                    break;
            }
            return d;
        }

        // True = complete the objective (✓), false = fail it (✗). Outcome objectives resolve by the round winner
        // (State.Winner: 0 = hunters, 1 = hiders; -1 while ongoing -> a mid-round removal, e.g. a caught hider, fails).
        private static bool Resolve(string key, GameModeController ctl)
        {
            switch (key)
            {
                case "round_hide": return ctl.State.Winner == 1;   // survived to a hiders' win
                case "round_hunt": return ctl.State.Winner == 0;   // hunters caught everyone
                default: return true;                              // open / wait / start / prep -> achieved
            }
        }

        // Optional outcome text shown on the entry as it resolves (only the round objectives get a verdict).
        private static string ResolveTitle(string key, bool won)
        {
            switch (key)
            {
                case "round_hide": return won ? "You survived the round!" : "You were caught.";
                case "round_hunt": return won ? "Caught every prop!" : "The hiders survived.";
                default: return null;   // keep the existing title
            }
        }

        /// <summary>The player opened the PropHunt app -> the "open the app" objective completes next tick.</summary>
        internal static void OnAppOpened() { _appOpened = true; }

        /// <summary>Session teardown: cancel + reset for the next session.</summary>
        internal static void Stop()
        {
            try { _quest?.Cancel(); } catch { }
            _quest = null;
            _active.Clear();
            _titles.Clear();
            _toRemove.Clear();
            _appOpened = false;
        }

        // The journal + HUD singletons the game's quest init touches (SetupJournalEntry / SetupHUDUI) only come up
        // after the local player's phone hierarchy spawns; creating a quest before that NREs.
        private static bool QuestUiReady()
        {
            try
            {
                return Player.Local != null
                    && PlayerSingleton<JournalApp>.InstanceExists
                    && Singleton<HUD>.InstanceExists;
            }
            catch { return false; }
        }
    }
}
