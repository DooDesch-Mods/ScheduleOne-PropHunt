using System;
using System.Collections.Generic;
using System.Linq;

namespace PropHunt.Game
{
    /// <summary>
    /// The pure, engine-agnostic host-authoritative round logic. Operates only on <see cref="GameState"/> +
    /// <see cref="RoundSettings"/> + an injected unix clock + a member-id list - NO Unity/IL2CPP/networking
    /// dependencies. This is what makes PropHunt's round flow unit-testable headlessly. The
    /// <see cref="GameModeController"/> is a thin adapter that feeds these methods, then does the engine I/O
    /// (state push, geometry checks, local visual effects). Every mutator returns whether it changed state,
    /// so the controller only re-publishes the snapshot when something actually changed.
    /// </summary>
    internal static class RoundLogic
    {
        /// <summary>Add freshly-joined members (as spectators) + drop those who left. Returns true if changed.</summary>
        internal static bool SyncRoster(GameState s, IEnumerable<ulong> memberIds)
        {
            bool changed = false;
            var present = new HashSet<ulong>();
            if (memberIds != null)
                foreach (var id in memberIds)
                {
                    if (id == 0) continue;
                    present.Add(id);
                    if (!s.Players.ContainsKey(id)) { s.GetOrAdd(id).Role = PlayerRole.Spectator; changed = true; }
                }
            var gone = s.Players.Keys.Where(k => !present.Contains(k)).ToList();
            foreach (var k in gone) { s.Players.Remove(k); changed = true; }
            return changed;
        }

        internal static int AliveHiders(GameState s) =>
            s.Players.Values.Count(p => p.Role == PlayerRole.Hider && !p.Eliminated);

        internal static int CountRole(GameState s, PlayerRole r) =>
            s.Players.Values.Count(p => p.Role == r);

        /// <summary>Assign 1 hunter per <c>PlayersPerHunter</c> players (min 1, always leave >=1 hider when possible),
        /// rotating who hunts every <c>RoundsBeforeSwap</c> rounds. Deterministic (sorted by id) across host+clients.</summary>
        internal static void AssignRoles(GameState s, RoundSettings set, int round)
        {
            var ids = s.Players.Keys.ToList();
            ids.Sort();
            int count = ids.Count;
            if (count == 0) return;
            int hunters = Math.Max(1, count / Math.Max(1, set.PlayersPerHunter));
            if (hunters >= count) hunters = Math.Max(1, count - 1);
            int rotation = (round - 1) / Math.Max(1, set.RoundsBeforeSwap);

            var hunterSet = new HashSet<ulong>();
            for (int i = 0; i < hunters; i++) hunterSet.Add(ids[(rotation + i) % count]);

            foreach (var id in ids)
            {
                var p = s.GetOrAdd(id);
                p.Role = hunterSet.Contains(id) ? PlayerRole.Hunter : PlayerRole.Hider;
                p.PropId = -1; p.Locked = false; p.Eliminated = false; p.Hits = 0; p.MaxHits = 1; p.Changes = 0;
                p.PropYaw = 0f; p.DecoysUsed = 0; p.ConcussUsed = 0;
                p.CatchesMade = 0; p.HitsDealt = 0; p.DecoyBaits = 0; p.StunsLanded = 0; p.SurvivedSeconds = 0;   // per-round stats (SessScore persists)
            }
        }

        internal static void EnterHiding(GameState s, RoundSettings set, long now)
        {
            foreach (var p in s.Players.Values)
                if (p.Role == PlayerRole.Hider) { p.PropId = -1; p.Locked = false; p.Eliminated = false; p.Hits = 0; p.MaxHits = 1; p.Changes = 0; p.PropYaw = 0f; p.DecoysUsed = 0; p.ConcussUsed = 0; }
            if (set.RemoveDecoysBetweenRounds) s.Decoys.Clear();   // host setting (default on); false = decoys carry over
            s.Winner = -1;
            s.Phase = RoundPhase.Hiding;
            s.PhaseEndsAtUnix = now + Math.Max(1, set.HideSeconds);
        }

        internal static void EnterHunting(GameState s, RoundSettings set, long now)
        {
            s.Phase = RoundPhase.Hunting;
            s.PhaseEndsAtUnix = now + Math.Max(1, set.HuntSeconds);
            s.HuntStartUnix = now;   // baseline for hider survival-time stats
        }

        internal static void EndRound(GameState s, RoundSettings set, long now, bool winnerHunters)
        {
            // finalize survival time for hiders still alive (caught hiders got theirs at catch time), then roll each
            // player's round performance into their cumulative session score (the leaderboard).
            foreach (var p in s.Players.Values)
                if (p.Role == PlayerRole.Hider && !p.Eliminated && s.HuntStartUnix > 0)
                    p.SurvivedSeconds = (int)Math.Max(0, now - s.HuntStartUnix);
            foreach (var p in s.Players.Values)
                p.SessScore += RoundScore(p, winnerHunters);
            s.Phase = RoundPhase.RoundEnd;
            s.Winner = winnerHunters ? 0 : 1;
            s.PhaseEndsAtUnix = now + Math.Max(1, set.RoundEndSeconds);
        }

        /// <summary>This round's score for one player: catches + hits + decoy baits + stuns + survival time, plus a
        /// bonus for being on the winning side (and not caught). Pure + deterministic so it is unit-testable.</summary>
        internal static int RoundScore(PlayerState p, bool huntersWon)
        {
            int s = p.CatchesMade * 10 + p.HitsDealt + p.DecoyBaits * 5 + p.StunsLanded * 5 + p.SurvivedSeconds / 5;
            bool onWinningSide = huntersWon ? p.Role == PlayerRole.Hunter : p.Role == PlayerRole.Hider;
            if (onWinningSide && !p.Eliminated) s += 15;
            return s;
        }

        /// <summary>Begin a fresh match. Like every later round it starts in the SAFEHOUSE lobby (host picks the
        /// map + presses start, which opens the doors into the round) - the first Safehouse -> Hiding transition
        /// bumps the round counter to 1 and assigns roles. Falls back to a direct round 1 if no safehouse is
        /// available (the controller pre-fills <see cref="GameState.SafehouseCode"/> before this runs).</summary>
        internal static void BeginMatch(GameState s, RoundSettings set, long now, IEnumerable<ulong> memberIds)
        {
            SyncRoster(s, memberIds);
            if (!string.IsNullOrEmpty(s.SafehouseCode))
            {
                s.RoundNumber = 0;        // first Safehouse -> Hiding bumps it to 1
                EnterSafehouse(s, now);
            }
            else
            {
                s.RoundNumber = 1;
                AssignRoles(s, set, s.RoundNumber);
                EnterHiding(s, set, now);
            }
        }

        /// <summary>At RoundEnd expiry: Single -> MatchEnd; Continuous -> the Safehouse inter-round lobby (if the
        /// host pre-selected a property into <see cref="GameState.SafehouseCode"/>), else straight to the next round
        /// (fallback when no property is available). Always returns true (state changed).</summary>
        internal static bool AfterRoundEnd(GameState s, RoundSettings set, long now)
        {
            // clear the round's dropped decoys at round end (default) so they don't linger through the safehouse
            // lobby / into the next round; the host can turn this off to let decoys persist across rounds.
            if (set.RemoveDecoysBetweenRounds) s.Decoys.Clear();
            if (set.Structure == RoundStructure.Single) { s.Phase = RoundPhase.MatchEnd; return true; }
            if (!string.IsNullOrEmpty(s.SafehouseCode)) { EnterSafehouse(s, now); return true; }
            s.RoundNumber++;
            AssignRoles(s, set, s.RoundNumber);
            EnterHiding(s, set, now);
            return true;
        }

        /// <summary>Host: park players in the Safehouse inter-round lobby. The property code was chosen by the
        /// controller (size-based) and stored in <see cref="GameState.SafehouseCode"/> before this runs. No
        /// auto-timer: the host advances out manually via <see cref="ConfirmSafehouseReady"/>.</summary>
        internal static void EnterSafehouse(GameState s, long now)
        {
            s.Phase = RoundPhase.Safehouse;
            s.SafehouseReady = false;
            s.PhaseEndsAtUnix = 0;
            s.Winner = -1;
        }

        /// <summary>Host confirmed the next-round settings - open the doors after a short broadcast window so every
        /// client has the "ready" state before the round starts.</summary>
        internal static void ConfirmSafehouseReady(GameState s, long now)
        {
            s.SafehouseReady = true;
            s.PhaseEndsAtUnix = now + 3;
        }

        /// <summary>Advance the round machine one tick. Returns true if the state changed (re-publish).</summary>
        internal static bool TickHost(GameState s, RoundSettings set, long now)
        {
            switch (s.Phase)
            {
                case RoundPhase.Hiding:
                    if (now >= s.PhaseEndsAtUnix) { EnterHunting(s, set, now); return true; }
                    break;
                case RoundPhase.Hunting:
                    if (AliveHiders(s) == 0) { EndRound(s, set, now, true); return true; }
                    if (now >= s.PhaseEndsAtUnix) { EndRound(s, set, now, false); return true; }
                    break;
                case RoundPhase.RoundEnd:
                    if (now >= s.PhaseEndsAtUnix) { AfterRoundEnd(s, set, now); return true; }
                    break;
                case RoundPhase.Safehouse:
                    // advance only once the host confirmed (SafehouseReady) and the short broadcast window elapsed
                    if (s.SafehouseReady && s.PhaseEndsAtUnix > 0 && now >= s.PhaseEndsAtUnix)
                    {
                        s.RoundNumber++;
                        AssignRoles(s, set, s.RoundNumber);
                        EnterHiding(s, set, now);
                        return true;
                    }
                    break;
            }
            return false;
        }

        /// <summary>True in the phases where a hider may pick/change their disguise (hiding AND hunting - hiders
        /// can re-blend on the move during the hunt).</summary>
        private static bool CanDisguise(RoundPhase phase) => phase == RoundPhase.Hiding || phase == RoundPhase.Hunting;

        /// <summary>Hider chose/changed a prop. <paramref name="maxHits"/> is the prop's size-based HP (host computes
        /// it from the catalog). Each change RESETS HP and counts against <paramref name="maxChanges"/> (0 = unlimited).
        /// Returns true if applied (false if out of changes or wrong role/phase).</summary>
        internal static bool ApplySelectProp(GameState s, ulong sender, int propId, int maxHits, int maxChanges)
        {
            if (!CanDisguise(s.Phase)) return false;
            if (!s.Players.TryGetValue(sender, out var p) || p.Role != PlayerRole.Hider || p.Eliminated) return false;
            if (maxChanges > 0 && p.Changes >= maxChanges) return false;   // no prop changes left this round
            p.PropId = propId;
            p.MaxHits = Math.Max(1, maxHits);
            p.Hits = 0;            // a fresh prop = full HP
            p.DecoysUsed = 0;      // ...and fresh decoy + concussion charges (CoD-style: refill on prop change)
            p.ConcussUsed = 0;
            p.Changes++;
            return true;
        }

        internal static bool ApplyLock(GameState s, ulong sender, bool locked)
        {
            if (!CanDisguise(s.Phase)) return false;
            if (!s.Players.TryGetValue(sender, out var p) || p.Role != PlayerRole.Hider || p.Eliminated) return false;
            if (locked && p.PropId < 0) return false;
            p.Locked = locked;
            return true;
        }

        /// <summary>Register a hunter hit on a hider. The hider needs <c>HitsToCatch</c> hits before being caught
        /// (props "have health", CoD-PropHunt style) - the first hits just count up. On the catching hit the
        /// caught-behaviour mutation + win check run. Geometry is the caller's job. Returns true if state changed.</summary>
        internal static bool ApplyCatch(GameState s, RoundSettings set, ulong hunter, ulong victim, long now)
        {
            if (s.Phase != RoundPhase.Hunting) return false;
            if (!s.Players.TryGetValue(hunter, out var h) || h.Role != PlayerRole.Hunter || h.Eliminated) return false;
            if (!s.Players.TryGetValue(victim, out var v) || v.Role != PlayerRole.Hider || v.Eliminated) return false;
            v.Hits++;
            h.HitsDealt++;
            if (v.Hits < Math.Max(1, v.MaxHits)) return true;   // a hit landed - not caught yet (bigger prop = more HP)
            h.CatchesMade++;
            if (s.HuntStartUnix > 0) v.SurvivedSeconds = (int)Math.Max(0, now - s.HuntStartUnix);
            if (set.Caught == CaughtBehavior.Infection)
            {
                // mirror the full per-round reset AssignRoles does, so no stale per-prop state (MaxHits, prop
                // changes, yaw, decoy/concussion charges) rides along when a caught hider turns hunter mid-round.
                v.Role = PlayerRole.Hunter;
                v.PropId = -1; v.Locked = false; v.Eliminated = false; v.Hits = 0; v.MaxHits = 1; v.Changes = 0;
                v.PropYaw = 0f; v.DecoysUsed = 0; v.ConcussUsed = 0;
            }
            else { v.Eliminated = true; }
            if (AliveHiders(s) == 0) EndRound(s, set, now, true);
            return true;
        }

        /// <summary>Hider set the manual facing (yaw, degrees) of their current prop ([F]+mouse).</summary>
        internal static bool ApplyRotate(GameState s, ulong sender, float yaw)
        {
            if (!CanDisguise(s.Phase)) return false;
            if (!s.Players.TryGetValue(sender, out var p) || p.Role != PlayerRole.Hider || p.Eliminated || p.PropId < 0) return false;
            p.PropYaw = yaw;
            return true;
        }

        /// <summary>Hider dropped a decoy of their current prop at the given world spot ([Q]). Honoured up to
        /// <c>MaxDecoys</c> per round. <paramref name="maxHits"/> is the same size-based HP as the hider's own
        /// prop (so a hunter needs the same number of hits to destroy the decoy as to catch the real hider).
        /// Returns true if added.</summary>
        internal static bool ApplyDropDecoy(GameState s, RoundSettings set, ulong sender, float x, float y, float z, float yaw, int maxHits)
        {
            if (s.Phase != RoundPhase.Hiding && s.Phase != RoundPhase.Hunting) return false;
            if (!s.Players.TryGetValue(sender, out var p) || p.Role != PlayerRole.Hider || p.Eliminated || p.PropId < 0) return false;
            if (set.MaxDecoys > 0 && p.DecoysUsed >= set.MaxDecoys) return false;
            s.Decoys.Add(new DecoyState { X = x, Y = y, Z = z, Yaw = yaw, PropId = p.PropId, MaxHits = Math.Max(1, maxHits), OwnerSteamId = sender });
            p.DecoysUsed++;
            return true;
        }

        /// <summary>Host: a hunter hit a decoy. Only valid during Hunting; bounds-checked; a destroyed decoy
        /// ignores further hits. Returns true if state changed (Hits incremented or Destroyed newly set).</summary>
        internal static bool ApplyHitDecoy(GameState s, int decoyIndex)
        {
            if (s.Phase != RoundPhase.Hunting) return false;
            if (decoyIndex < 0 || decoyIndex >= s.Decoys.Count) return false;
            var d = s.Decoys[decoyIndex];
            if (d.Destroyed) return false;
            d.Hits++;
            // credit the decoy's owner with a "bait" - a hunter wasted a shot on their fake
            if (d.OwnerSteamId != 0 && s.Players.TryGetValue(d.OwnerSteamId, out var owner) && owner.Role == PlayerRole.Hider)
                owner.DecoyBaits++;
            if (d.Hits >= Math.Max(1, d.MaxHits))
                d.Destroyed = true;
            return true;
        }

        /// <summary>Hider triggered a concussion ([G]). Validates the charge limit; the host applies the actual
        /// stun to nearby hunters (engine I/O). Returns true if a charge was spent.</summary>
        internal static bool ApplyConcuss(GameState s, RoundSettings set, ulong sender)
        {
            if (s.Phase != RoundPhase.Hunting) return false;
            if (!s.Players.TryGetValue(sender, out var p) || p.Role != PlayerRole.Hider || p.Eliminated) return false;
            if (set.ConcussCharges > 0 && p.ConcussUsed >= set.ConcussCharges) return false;
            p.ConcussUsed++;
            return true;
        }

        /// <summary>True once the victim has been caught (eliminated or converted) rather than merely hit.</summary>
        internal static bool IsCaught(GameState s, ulong victim) =>
            s.Players.TryGetValue(victim, out var v) && (v.Eliminated || v.Role != PlayerRole.Hider);

        /// <summary>Eliminate a player who left the play area (caller validated geometry). Returns true if applied.</summary>
        internal static bool ApplyOutOfBounds(GameState s, RoundSettings set, ulong id, long now)
        {
            if (s.Phase != RoundPhase.Hiding && s.Phase != RoundPhase.Hunting) return false;
            if (!s.Players.TryGetValue(id, out var p) || p.Eliminated) return false;
            if (p.Role != PlayerRole.Hider && p.Role != PlayerRole.Hunter) return false;
            p.Eliminated = true;
            if (s.Phase == RoundPhase.Hunting && AliveHiders(s) == 0) EndRound(s, set, now, true);
            return true;
        }
    }
}
