#if DEBUG
using System;
using UnityEngine;
using Il2CppScheduleOne.Combat;
using Il2CppScheduleOne.DevUtilities;

namespace PropHunt.Debug
{
    /// <summary>
    /// phcombat: read the values the vanilla shot pipeline actually runs on. Every one of them is Inspector or
    /// project data rather than code, so it cannot be answered by reading the decompiled source - and the whole
    /// "let vanilla resolve the hit" design stands or falls on them:
    ///
    ///   - CombatManager.RangedWeaponLayerMask decides which colliders a bullet can even see. A hitbox on a layer
    ///     outside it is invisible to gunfire no matter how it is built.
    ///   - Physics.queriesHitTriggers decides whether a trigger collider is returned by the cast at all.
    ///   - The player's own layer is the safest home for our hitbox: vanilla demonstrably shoots players, so that
    ///     layer is in the mask by definition.
    /// </summary>
    internal static class CombatProbe
    {
        internal static void Dump()
        {
            try
            {
                Core.Log.Msg($"phcombat: Physics.queriesHitTriggers={Physics.queriesHitTriggers}");

                var cm = NetworkSingleton<CombatManager>.Instance;
                if (cm == null) Core.Log.Warning("phcombat: CombatManager not spawned yet.");
                else
                {
                    int ranged = cm.RangedWeaponLayerMask.value;
                    int melee = cm.MeleeLayerMask.value;
                    Core.Log.Msg($"phcombat: RangedWeaponLayerMask={ranged} -> {DescribeMask(ranged)}");
                    Core.Log.Msg($"phcombat: MeleeLayerMask={melee} -> {DescribeMask(melee)}");
                }

                var lp = Player.Local;
                if (lp == null) { Core.Log.Warning("phcombat: no local player."); return; }

                int rootLayer = lp.gameObject.layer;
                Core.Log.Msg($"phcombat: local Player root layer={rootLayer} \"{LayerMask.LayerToName(rootLayer)}\"");

                var cap = lp.CapCol;
                if (cap == null) Core.Log.Warning("phcombat: Player.CapCol is null.");
                else
                {
                    int l = cap.gameObject.layer;
                    Core.Log.Msg($"phcombat: CapCol layer={l} \"{LayerMask.LayerToName(l)}\" " +
                                 $"isTrigger={cap.isTrigger} enabled={cap.enabled} " +
                                 $"onObject=\"{cap.gameObject.name}\" damageableFound={(cap.GetComponentInParent<Player>() != null)}");
                }

                // Every REMOTE player too: their capsule is what a bullet hits today, and vanilla marks it a trigger.
                var all = Player.PlayerList;
                if (all != null)
                {
                    for (int i = 0; i < all.Count; i++)
                    {
                        var p = all[i];
                        if (p == null || p.Equals(lp)) continue;
                        var c = p.CapCol;
                        Core.Log.Msg($"phcombat: remote \"{p.PlayerName}\" rootLayer={p.gameObject.layer} " +
                                     (c == null ? "CapCol=null" : $"capLayer={c.gameObject.layer} isTrigger={c.isTrigger} enabled={c.enabled}"));
                    }
                }

                // The catch hitbox: the whole "let vanilla resolve the shot" design rests on this collider having a
                // Player above it (Fire() BREAKS its hit loop when GetComponentInParent<IDamageable>() is null) and
                // sitting on a layer the weapon mask contains. Both are asserted here rather than assumed.
                foreach (var box in UnityEngine.Object.FindObjectsOfType<BoxCollider>())
                {
                    if (box == null || box.gameObject == null) continue;
                    string n = box.gameObject.name;
                    if (n == null || !n.StartsWith("ph_prop_")) continue;

                    var owner = box.GetComponentInParent<Player>();
                    int layer = box.gameObject.layer;
                    bool inRangedMask = cm != null && (cm.RangedWeaponLayerMask.value & (1 << layer)) != 0;
                    Core.Log.Msg($"phcombat: hitbox \"{n}\" layer={layer} \"{LayerMask.LayerToName(layer)}\" " +
                                 $"inRangedMask={inRangedMask} isTrigger={box.isTrigger} " +
                                 $"parent=\"{(box.transform.parent == null ? "NONE" : box.transform.parent.name)}\" " +
                                 $"resolvesToPlayer={(owner != null ? owner.PlayerName : "NULL - bullets would STOP here")} " +
                                 $"size=({box.size.x:F2}x{box.size.y:F2}x{box.size.z:F2}) worldScale={box.transform.lossyScale.x:F2}");
                }

                // "Solid props" needs a layer that is BOTH in the weapon mask and collides with what actually moves
                // a player - their CharacterController, which is not necessarily on the same object as the capsule.
                var ccLocal = lp.GetComponentInChildren<CharacterController>();
                int ccLayer = ccLocal != null ? ccLocal.gameObject.layer : -1;
                Core.Log.Msg($"phcombat: CharacterController layer={ccLayer} " +
                             $"\"{(ccLayer >= 0 ? LayerMask.LayerToName(ccLayer) : "none")}\" " +
                             $"detectCollisions={(ccLocal != null ? ccLocal.detectCollisions.ToString() : "n/a")} " +
                             $"stepOffset={(ccLocal != null ? ccLocal.stepOffset.ToString("F2") : "n/a")} " +
                             $"height={(ccLocal != null ? ccLocal.height.ToString("F2") : "n/a")} " +
                             $"radius={(ccLocal != null ? ccLocal.radius.ToString("F2") : "n/a")}");

                if (ccLayer >= 0 && cm != null)
                {
                    var usable = new System.Text.StringBuilder();
                    for (int i = 0; i < 32; i++)
                    {
                        bool inMask = (cm.RangedWeaponLayerMask.value & (1 << i)) != 0;
                        if (!inMask) continue;
                        if (Physics.GetIgnoreLayerCollision(i, ccLayer)) continue;   // would not block a player
                        if (usable.Length > 0) usable.Append(", ");
                        usable.Append(i).Append(':').Append(LayerMask.LayerToName(i));
                    }
                    Core.Log.Msg($"phcombat: layers that are BOTH shootable AND block a player: " +
                                 (usable.Length == 0 ? "(NONE - a solid prop is impossible without a physics-matrix change)" : usable.ToString()));
                }

                int pl = LayerMask.NameToLayer("Player");
                if (pl >= 0)
                    Core.Log.Msg($"phcombat: Player-vs-Player ignored={Physics.GetIgnoreLayerCollision(pl, pl)}, " +
                                 $"Player-vs-Default ignored={Physics.GetIgnoreLayerCollision(pl, 0)}");

                // Does the "pass a bullet through me" tag the shot loop checks even exist in this build?
                try
                {
                    var probe = new GameObject("ph_tagprobe");
                    bool tagExists;
                    try { tagExists = probe.CompareTag("CombatIgnore"); tagExists = true; }
                    catch { tagExists = false; }
                    UnityEngine.Object.DestroyImmediate(probe);
                    Core.Log.Msg($"phcombat: tag \"CombatIgnore\" defined={tagExists}");
                }
                catch { }
            }
            catch (Exception e) { Core.Log.Error("phcombat THREW - " + e); }
        }

        private static string DescribeMask(int mask)
        {
            if (mask == 0) return "(empty)";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < 32; i++)
            {
                if ((mask & (1 << i)) == 0) continue;
                string n = LayerMask.LayerToName(i);
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(i).Append(':').Append(string.IsNullOrEmpty(n) ? "<unnamed>" : n);
            }
            return sb.ToString();
        }
    }
}
#endif
