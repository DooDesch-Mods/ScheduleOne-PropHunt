using System;
using System.Collections.Generic;
using Il2CppScheduleOne.GameTime;
using Il2CppScheduleOne.Police;
using Il2CppScheduleOne.ItemFramework;
using Reg = Il2CppScheduleOne.Registry;

namespace PropHunt.Game
{
    /// <summary>
    /// Engine-side round environment: host world setup (lock time of day + freeze, keep police ignoring
    /// players) and per-client local effects (clear own crime so no arrests, teleport into the play area,
    /// arm the local hunter). Every interop call is try/caught + best-effort so a single failure never
    /// crashes the round. TODO(testing): tune the spread ring, weapon set, and friendly-fire enforcement.
    /// </summary>
    internal static class RoundEnvironment
    {
        private static readonly HashSet<int> _suppressedOfficers = new HashSet<int>();

        /// <summary>Host: lock the world to the configured time of day and freeze its progression.</summary>
        internal static void ApplyHostWorld(RoundSettings s)
        {
            try
            {
                var tm = NetworkSingleton<TimeManager>.Instance;
                if (tm != null) { tm.SetTimeAndSync(s.TimeOfDay); tm.SetTimeSpeedMultiplier(0f); }
                Core.Log.Msg($"[PropHunt] world: time locked to {s.TimeOfDay}, progression frozen.");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] ApplyHostWorld failed: " + e.Message); }
        }

        /// <summary>Host: resume normal time when the session ends.</summary>
        internal static void RestoreWorld()
        {
            try { var tm = NetworkSingleton<TimeManager>.Instance; if (tm != null) tm.SetTimeSpeedMultiplier(1f); } catch { }
            _suppressedOfficers.Clear();
        }

        /// <summary>Dev/curation: lock the world to a bright time of day (HHMM) and freeze progression so props
        /// are clearly lit for review. Host-authoritative; no-op/safe off-host.</summary>
        internal static void LockTimeOfDay(int hhmm)
        {
            try { var tm = NetworkSingleton<TimeManager>.Instance; if (tm != null) { tm.SetTimeAndSync(hhmm); tm.SetTimeSpeedMultiplier(0f); } }
            catch (Exception e) { Core.LogDebug("[PropHunt] LockTimeOfDay failed: " + e.Message); }
        }

        /// <summary>Resume normal time progression (pair with <see cref="LockTimeOfDay"/>).</summary>
        internal static void RestoreTimeProgression()
        {
            try { var tm = NetworkSingleton<TimeManager>.Instance; if (tm != null) tm.SetTimeSpeedMultiplier(1f); }
            catch (Exception e) { Core.LogDebug("[PropHunt] RestoreTimeProgression failed: " + e.Message); }
        }

        /// <summary>Host: make police ignore players (applied once per officer; cheap to call each tick).</summary>
        internal static void SuppressPolice()
        {
            try
            {
                var officers = PoliceOfficer.Officers;
                if (officers == null) return;
                for (int i = 0; i < officers.Count; i++)
                {
                    var o = officers[i];
                    if (o == null) continue;
                    int id = o.GetInstanceID();
                    if (_suppressedOfficers.Contains(id)) continue;
                    o.SetIgnorePlayers(true);
                    _suppressedOfficers.Add(id);
                }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] SuppressPolice failed: " + e.Message); }
        }

        /// <summary>All clients: keep the LOCAL player crime-free so NPCs/police never engage or arrest.</summary>
        internal static void ClearLocalCrime()
        {
            try
            {
                var p = Player.Local;
                var cd = p != null ? p.CrimeData : null;
                if (cd != null) { cd.ClearCrimes(); cd.SetPursuitLevel(PlayerCrimeData.EPursuitLevel.None); }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] ClearLocalCrime failed: " + e.Message); }
        }

        /// <summary>Local: teleport the local player into the play area, spread on a small deterministic ring.</summary>
        internal static void TeleportLocalInto(float x, float y, float z, ulong steamId)
        {
            float ang = (steamId % 360UL) * UnityEngine.Mathf.Deg2Rad;
            float r = 3f + (steamId % 4UL);
            TeleportLocalTo(new UnityEngine.Vector3(x + UnityEngine.Mathf.Cos(ang) * r, y, z + UnityEngine.Mathf.Sin(ang) * r));
        }

        /// <summary>Local: teleport the local player to a world position via the game's PlayerTeleporter (the proven
        /// path that keeps the CharacterController/network position consistent), with a transform fallback.</summary>
        internal static void TeleportLocalTo(UnityEngine.Vector3 pos)
        {
            try
            {
                var p = Player.Local;
                if (p == null) return;
                var tp = p.GetComponent<PlayerTeleporter>();
                if (tp != null)
                {
                    var go = new UnityEngine.GameObject("ph_tp");
                    go.transform.position = pos;
                    tp.Teleport(go.transform);
                    UnityEngine.Object.Destroy(go);
                }
                else
                {
                    p.transform.position = pos;   // fallback if the teleporter component isn't present
                }
                Core.LogDebug($"[PropHunt] teleported local player to ({pos.x:F0},{pos.y:F0},{pos.z:F0}).");
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] teleport failed: " + e.Message); }
        }

        // Live-switchable feet-Y modes ([4]/[5] cycle) so we can find the one that handles jump/stairs AND
        // stays consistent across host/client. The default (Follow) tracks the player's replicated position +
        // the owner's capsule offset, so it rides jumps/stairs and matches on every client.
        internal static int GroundMode = 3;   // default: "fixed" - flat offset below the capsule centre. Identical
                                               // on host+client (uses only the replicated position) and the
                                               // capsule bottom sits above the visual feet, so the other modes float.
        internal const int GroundModeCount = 4;
        internal static float FixedFeetDrop = 0.97f;   // metres below the player root to the feet (dialed in live via [6]/[7])
        internal static string GroundModeName =>
            GroundMode == 1 ? "capsule" : GroundMode == 2 ? "floor-ray" : GroundMode == 3 ? "fixed" : "follow";
        private static float _localFeetOffset = -1.0f;

        /// <summary>World-Y of a player's feet, per the current <see cref="GroundMode"/>.</summary>
        internal static float FeetY(Player p)
        {
            if (p == null) return 0f;
            UnityEngine.Vector3 pos = p.transform.position;

            // keep the capsule offset (feet relative to the root) fresh from the LOCAL player's own capsule -
            // reliable, and the same shape for every player. Used by the "follow" mode for all players.
            try
            {
                var lp = Player.Local;
                if (lp != null) { var lcc = lp.GetComponentInChildren<UnityEngine.CharacterController>(); if (lcc != null) _localFeetOffset = lcc.bounds.min.y - lp.transform.position.y; }
            }
            catch { }

            switch (GroundMode)
            {
                case 1:   // capsule: this player's own CharacterController bottom (tracks everything; unreliable for remotes on the host)
                    try { var cc = p.GetComponentInChildren<UnityEngine.CharacterController>(); if (cc != null) return cc.bounds.min.y; } catch { }
                    return pos.y + _localFeetOffset;
                case 2:   // floor-ray: raycast down to the floor, skipping the player (pins to ground - bad for jumps)
                    return FloorRaycastY(pos);
                case 3:   // fixed: a flat, tuned offset below the capsule centre (consistent host+client)
                    return pos.y - FixedFeetDrop;
                default:  // follow (0): replicated position + the owner's capsule offset -> rides jumps/stairs, consistent
                    return pos.y + _localFeetOffset;
            }
        }

        private static float FloorRaycastY(UnityEngine.Vector3 pos)
        {
            try
            {
                var hits = UnityEngine.Physics.RaycastAll(pos + UnityEngine.Vector3.up * 0.3f, UnityEngine.Vector3.down, 5f);
                if (hits != null && hits.Length > 0)
                {
                    System.Array.Sort(hits, (System.Comparison<UnityEngine.RaycastHit>)((a, b) => a.distance.CompareTo(b.distance)));
                    for (int i = 0; i < hits.Length; i++)
                    {
                        var c = hits[i].collider;
                        if (c == null) continue;
                        if (c.GetComponentInParent<Player>() != null) continue;
                        return hits[i].point.y;
                    }
                }
            }
            catch { }
            return pos.y - 1.0f;
        }

        /// <summary>Local: give the local hunter a weapon (self-discovering via the item registry).</summary>
        internal static void GiveWeapon(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv == null) return;
                if (inv.GetAmountOfItem(id) > 0) return;   // already armed - AddItemToInventory is additive, never stack a 2nd weapon
                ItemDefinition def = null;
                try { def = Reg.GetItem(id); } catch { }
                if (def == null) { Core.Log.Warning($"[PropHunt] weapon '{id}' not in the item registry."); return; }
                var inst = def.GetDefaultInstance(1);
                if (inst != null && inv.CanItemFitInInventory(inst, 1))
                {
                    // normal magazine; the weapon-reload patches refill the clip on reload (no magazine item
                    // needed) so the hunter reloads normally but never runs out.
                    inv.AddItemToInventory(inst);
                    Core.Log.Msg($"[PropHunt] armed hunter with '{id}'.");
                }
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] GiveWeapon failed: " + e.Message); }
        }

        /// <summary>Local: strip the hunter weapon from the local inventory and holster it cleanly. RemoveAmountOfItem
        /// routes through ChangeQuantity(-n) -> ClearStoredInstance at zero, which runs HotbarSlot.Unequip() and
        /// destroys the live viewmodel, so a player who stops being a hunter keeps no gun and no ghost weapon.</summary>
        internal static void RemoveWeapon(string id)
        {
            if (string.IsNullOrEmpty(id)) return;
            try
            {
                var inv = PlayerSingleton<PlayerInventory>.Instance;
                if (inv == null) return;
                if (inv.GetAmountOfItem(id) == 0) return;   // nothing to strip (idempotent)
                inv.RemoveAmountOfItem(id, 99u);
                Core.Log.Msg($"[PropHunt] disarmed former hunter ('{id}').");
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] RemoveWeapon failed: " + e.Message); }
        }
    }
}
