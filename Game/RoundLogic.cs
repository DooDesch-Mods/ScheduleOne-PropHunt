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
        /// <summary>Safehouse pause (seconds) before an auto-started next round confirms - lets players read the
        /// scoreboard + regroup. Only used when <see cref="RoundSettings.AutoStartNextRound"/> is on.</summary>
        private const int AutoStartPauseSeconds = 10;

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
                p.DecoysSmashed = 0; p.Taunts = 0;
                p.HunterHits = 0; p.HunterMaxHits = Math.Max(1, set.HunterHitsToDown); p.Downed = false; p.DownedUntilUnix = 0; p.KnockX = 0f; p.KnockZ = 0f;   // hunter friendly-fire HP + knockdown
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
            int s = p.CatchesMade * 10 + p.HitsDealt + p.DecoyBaits * 5 + p.StunsLanded * 5 + p.SurvivedSeconds / 5
                    + p.DecoysSmashed * 3   // hunter: destroying a hider's decoy
                    + p.Taunts * 2;         // hider: scored taunts (host rate-limits to 1/15s)
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
            // Clear every disguise for the inter-round lobby: a hider's prop persisted from the last round into the
            // Safehouse (PropId was only reset in EnterHiding), so players still appeared/were-labelled as their old
            // prop ("You are now: ...") while waiting. Drop the prop state here; EnterHiding re-initialises it for the
            // next round. Role/Eliminated are left to EnterHiding / the role swap.
            foreach (var p in s.Players.Values)
            {
                p.PropId = -1; p.Locked = false; p.Hits = 0; p.MaxHits = 1; p.PropYaw = 0f;
            }
        }

        /// <summary>Host confirmed the next-round settings - open the doors after a short broadcast window so every
        /// client has the "ready" state before the round starts.</summary>
        internal static void ConfirmSafehouseReady(GameState s, long now)
        {
            s.SafehouseReady = true;
            s.PhaseEndsAtUnix = now + 3;
        }

        /// <summary>Advance the round machine one tick. Returns true if the state changed (re-publish). Also stands
        /// knocked-down hunters back up when their ragdoll timer elapses - that runs in EVERY phase so a hunter downed
        /// just as the round ends still recovers instead of staying ragdolled into RoundEnd.</summary>
        internal static bool TickHost(GameState s, RoundSettings set, long now)
        {
            bool recovered = RecoverDowned(s, now);
            bool advanced = TickPhase(s, set, now);
            return recovered || advanced;
        }

        /// <summary>Clear the knocked-down flag on any hunter whose ragdoll timer has elapsed (or who is no longer a
        /// live hunter). Resets their friendly-fire HP so they stand back up at full. Returns true if anything changed;
        /// the controller then sends the recovered player's owner a "stand up" message.</summary>
        private static bool RecoverDowned(GameState s, long now)
        {
            bool changed = false;
            foreach (var p in s.Players.Values)
            {
                if (!p.Downed) continue;
                if (now >= p.DownedUntilUnix || p.Role != PlayerRole.Hunter || p.Eliminated)
                {
                    p.Downed = false; p.HunterHits = 0; p.DownedUntilUnix = 0; p.KnockX = 0f; p.KnockZ = 0f;
                    changed = true;
                }
            }
            return changed;
        }

        /// <summary>Register a friendly-fire hit from one hunter on another. Requires FriendlyFire on, the Hunting
        /// phase, both to be live hunters, and shooter != victim. The victim takes an FF "hit"; at HunterHitsToDown
        /// hits they are knocked DOWN (ragdoll) for <see cref="RoundSettings.HunterDownBaseSeconds"/>, and each
        /// follow-up hit while down EXTENDS the ragdoll by a step toward <see cref="RoundSettings.HunterDownMaxSeconds"/>
        /// (the deadline is anchored to the first knockdown, never re-set to 'now', so a team can't pin someone
        /// forever). Never eliminates - a hunter always stands back up. Returns true if the hit changed state (so the
        /// caller re-publishes); <paramref name="newlyDowned"/> is set true only on the hit that caused the knockdown
        /// (so the caller drives the ragdoll + blood/FX exactly once).</summary>
        internal static bool ApplyHitHunter(GameState s, RoundSettings set, ulong shooter, ulong victim, long now, out bool newlyDowned)
        {
            newlyDowned = false;
            if (s.Phase != RoundPhase.Hunting || !set.FriendlyFire || shooter == victim) return false;
            if (!s.Players.TryGetValue(shooter, out var h) || h.Role != PlayerRole.Hunter || h.Eliminated) return false;
            if (!s.Players.TryGetValue(victim, out var v) || v.Role != PlayerRole.Hunter || v.Eliminated) return false;

            // NOTE: friendly-fire hits deliberately do NOT credit HitsDealt (which feeds RoundScore) - otherwise
            // shooting teammates would farm points. FF is a tactical hindrance, never a scoring action.
            int toDown = Math.Max(1, set.HunterHitsToDown);
            int baseS = Math.Max(1, (int)Math.Round(set.HunterDownBaseSeconds));
            int maxS = Math.Max(baseS, (int)Math.Round(set.HunterDownMaxSeconds));
            const int StepSeconds = 2;                       // extra ragdoll time per follow-up hit while already down
            int maxExtra = Math.Max(0, (maxS - baseS) / StepSeconds);   // integer floor => base + maxExtra*step <= max (no overshoot)

            v.HunterMaxHits = toDown;   // keep in sync with the live setting for the HUD
            if (!v.Downed)
            {
                v.HunterHits++;
                if (v.HunterHits < toDown) return true;       // chipped, not down yet (state changed -> HUD HP bar)
                v.Downed = true;
                v.DownedUntilUnix = now + baseS;
                newlyDowned = true;                           // caller ragdolls + blood/FX
                return true;
            }
            // already down: extend toward the cap, anchored to the original knockdown deadline (add, never re-anchor).
            int extra = v.HunterHits - toDown;
            if (extra >= maxExtra) return false;              // at the cap -> ignore (no re-anchor => no permanent pin)
            v.HunterHits++; v.DownedUntilUnix += StepSeconds;
            return true;
        }

        /// <summary>Knock a hunter DOWN for a fixed short time (a concussion "stun"), reusing the same ragdoll state as
        /// friendly fire. Never shortens an existing (possibly longer friendly-fire) knockdown - takes the later
        /// deadline. Returns true if the hunter's downed state changed/extended. Host validates the role/phase.</summary>
        internal static bool ApplyConcussDown(GameState s, ulong hunterId, int seconds, long now)
        {
            if (s.Phase != RoundPhase.Hunting) return false;
            if (!s.Players.TryGetValue(hunterId, out var v) || v.Role != PlayerRole.Hunter || v.Eliminated) return false;
            long until = now + Math.Max(1, seconds);
            if (v.Downed && v.DownedUntilUnix >= until) return false;   // already down at least this long
            v.Downed = true;
            v.DownedUntilUnix = Math.Max(v.DownedUntilUnix, until);
            return true;
        }

        /// <summary>Advance the phase state machine one tick. Returns true if the phase state changed (re-publish).</summary>
        private static bool TickPhase(GameState s, RoundSettings set, long now)
        {
            switch (s.Phase)
            {
                case RoundPhase.Hiding:
                    // Phase==Hiding always begins with >=1 hunter (AssignRoles guarantees it); if that drops to 0 the
                    // last hunter disconnected mid-round -> end the round, hiders win (don't leave it running forever).
                    if (CountRole(s, PlayerRole.Hunter) == 0) { EndRound(s, set, now, false); return true; }
                    // every hider left during the hide window -> end the dead round now instead of waiting out the timer.
                    if (AliveHiders(s) == 0) { EndRound(s, set, now, true); return true; }
                    if (now >= s.PhaseEndsAtUnix) { EnterHunting(s, set, now); return true; }
                    break;
                case RoundPhase.Hunting:
                    if (CountRole(s, PlayerRole.Hunter) == 0) { EndRound(s, set, now, false); return true; }   // last hunter left -> hiders win
                    if (AliveHiders(s) == 0) { EndRound(s, set, now, true); return true; }
                    if (now >= s.PhaseEndsAtUnix) { EndRound(s, set, now, false); return true; }
                    break;
                case RoundPhase.RoundEnd:
                    if (now >= s.PhaseEndsAtUnix) { AfterRoundEnd(s, set, now); return true; }
                    break;
                case RoundPhase.Safehouse:
                    // Auto-start the NEXT round after a short pause when enabled (default). Gated on RoundNumber >= 1
                    // so the INITIAL pre-match lobby (round 0) still waits for the host to set up + press start. The
                    // host can start early (manual ConfirmSafehouseReady), or toggle auto off to fall back to manual.
                    if (set.AutoStartNextRound && !s.SafehouseReady && s.RoundNumber >= 1)
                    {
                        if (s.PhaseEndsAtUnix <= 0) s.PhaseEndsAtUnix = now + AutoStartPauseSeconds;   // begin the countdown
                        else if (now >= s.PhaseEndsAtUnix) { ConfirmSafehouseReady(s, now); return true; }
                    }
                    // advance once ready (host confirmed OR auto) and the short broadcast window elapsed
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
        internal static bool ApplySelectProp(GameState s, ulong sender, int propId, int maxHits, int maxChanges, bool freeChange = false)
        {
            if (!CanDisguise(s.Phase)) return false;
            if (!s.Players.TryGetValue(sender, out var p) || p.Role != PlayerRole.Hider || p.Eliminated) return false;
            // A free change (hiding phase, when the host allows unlimited pre-hunt changes) bypasses the limit and
            // does NOT count against the hunt-phase budget; a normal change is limited and increments the counter.
            if (!freeChange && maxChanges > 0 && p.Changes >= maxChanges) return false;
            p.PropId = propId;
            p.MaxHits = Math.Max(1, maxHits);
            p.Hits = 0;            // a fresh prop = full HP
            p.DecoysUsed = 0;      // ...and fresh decoy + concussion charges (CoD-style: refill on prop change)
            p.ConcussUsed = 0;
            if (!freeChange) p.Changes++;
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
                v.HunterHits = 0; v.HunterMaxHits = Math.Max(1, set.HunterHitsToDown); v.Downed = false; v.DownedUntilUnix = 0; v.KnockX = 0f; v.KnockZ = 0f;
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
            // Only hiders are eliminated for leaving the area; hunters are teleported back by PlayAreaController and
            // never eliminated - an eliminated hunter would be a broken half-state (still armed, but unable to catch).
            if (p.Role != PlayerRole.Hider) return false;
            p.Eliminated = true;
            if (s.Phase == RoundPhase.Hunting && AliveHiders(s) == 0) EndRound(s, set, now, true);
            return true;
        }
    }
}
