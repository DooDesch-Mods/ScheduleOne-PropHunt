#if DEBUG
using UnityEngine;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using PropHunt.Disguise;
using PropHunt.Game;

namespace PropHunt.Debug
{
    /// <summary>
    /// DEBUG-only measurements for the two things that cannot be judged by looking at the screen: how big the local
    /// hider's body actually is compared to their prop. Prints one block to the log; nothing here changes state.
    /// The play-area ring has its own report - phmapring in <see cref="SoloHarness"/>.
    /// </summary>
    internal static class SizeProbe
    {
        /// <summary>phscale - the numbers behind "is the collider the size of the prop".</summary>
        internal static void DumpScale()
        {
            var log = Core.Log;
            try
            {
                var ctl = GameModeController.Active;
                if (ctl == null) { log.Msg("[PropHunt] phscale: no active session."); return; }

                int propId = ctl.LocalPropId;
                var player = Player.Local;
                log.Msg("[PropHunt] ---- phscale ----");
                log.Msg($"  role={ctl.LocalRole} phase={ctl.State?.Phase} propId={propId} ({ctl.LocalPropName ?? "none"})");
                log.Msg($"  setting PropSizeCollision={(ctl.Settings == null ? "(no settings)" : ctl.Settings.PropSizeCollision.ToString())}");

                if (player == null) { log.Msg("  Player.Local is null."); return; }
                log.Msg($"  player.Scale={player.Scale:F3}  transform.localScale={player.transform.localScale}");

                var pm = PlayerSingleton<PlayerMovement>.Instance;
                if (pm != null && pm.Controller != null)
                {
                    var cc = pm.Controller;
                    var s = cc.transform.lossyScale;
                    log.Msg($"  capsule local h={cc.height:F3} r={cc.radius:F3}   lossyScale={s}");
                    log.Msg($"  capsule WORLD h={cc.height * Mathf.Abs(s.y):F3} w={cc.radius * 2f * Mathf.Abs(s.x):F3}");
                    log.Msg($"  capsule transform == player transform: {(cc.transform == player.transform ? "YES" : "NO - scaling the player does NOT scale the capsule")}");
                }
                else log.Msg("  PlayerMovement/Controller not available.");

                if (propId >= 0)
                {
                    var e = PropCatalog.ById(propId);
                    if (e == null) log.Msg($"  catalog entry for {propId} is null.");
                    else if (PropClone.TryGetPropBoundsFromSource(e, out var lb))
                    {
                        Vector3 ls = e.SourceRoot != null ? e.SourceRoot.transform.lossyScale : Vector3.one;
                        Vector3 world = new Vector3(Mathf.Abs(lb.size.x * ls.x), Mathf.Abs(lb.size.y * ls.y), Mathf.Abs(lb.size.z * ls.z));
                        log.Msg($"  prop '{e.Name}' local bounds={lb.size}  sourceLossyScale={ls}");
                        log.Msg($"  prop WORLD size={world}  -> scale={PropScale.ForBounds(lb, ls):F3} width={PropScale.WidthFor(lb, ls):F3}m");
                        log.Msg($"  catalog SizeOf={PropCatalog.SizeOf(propId):F3}  PropCollisionState.TargetHeight={Patches.PropCollisionState.TargetHeight:F3}");
                    }
                    else log.Msg($"  no source bounds for '{e.Name}'.");
                }

                // The two boxes the F3 overlay draws, measured straight off the live colliders instead of recomputed.
                // This is the comparison that settles it: everything above is what the code MEANT to build, this is
                // what is actually in the scene, and a mismatch between the two is its own bug.
                if (TryGetPropBox(player, out var boxWorld, out string boxName))
                {
                    var pm2 = PlayerSingleton<PlayerMovement>.Instance;
                    var cc2 = pm2 != null ? pm2.Controller : null;
                    if (cc2 != null)
                    {
                        var s2 = cc2.transform.lossyScale;
                        var body = new Vector3(cc2.radius * 2f * Mathf.Abs(s2.x), cc2.height * Mathf.Abs(s2.y), cc2.radius * 2f * Mathf.Abs(s2.z));
                        log.Msg($"  BLUE  prop box '{boxName}' world = {Fmt(boxWorld)}");
                        log.Msg($"  GREEN body capsule    world = {Fmt(body)}");
                        log.Msg($"  delta (body - prop)         = {Fmt(body - boxWorld)}" +
                                $"   [height should be ~0; width is capped by the prop's NARROWER side]");
                    }
                }
                else log.Msg("  no ph_prop_ hitbox on the local player (not disguised, or the hitbox failed to build).");
            }
            catch (System.Exception ex) { log.Warning("[PropHunt] phscale failed: " + ex); }
        }

        /// <summary>The disguise's shootable box (the cyan one in the F3 overlay), in world metres. It hangs under the
        /// player, so the player's own children are the whole search space.</summary>
        internal static bool TryGetPropBox(Player player, out Vector3 worldSize, out string name)
        {
            worldSize = Vector3.zero; name = null;
            if (player == null) return false;
            try
            {
                var boxes = player.GetComponentsInChildren<BoxCollider>(true);
                if (boxes == null) return false;
                for (int i = 0; i < boxes.Length; i++)
                {
                    var b = boxes[i];
                    if (b == null || b.gameObject == null) continue;
                    string n = b.gameObject.name;
                    if (string.IsNullOrEmpty(n) || !n.StartsWith("ph_prop_")) continue;
                    var s = b.transform.lossyScale;
                    worldSize = new Vector3(Mathf.Abs(b.size.x * s.x), Mathf.Abs(b.size.y * s.y), Mathf.Abs(b.size.z * s.z));
                    name = n;
                    return true;
                }
            }
            catch { }
            return false;
        }

        private static string Fmt(Vector3 v) => $"({v.x:F2}, {v.y:F2}, {v.z:F2})";
    }
}
#endif
