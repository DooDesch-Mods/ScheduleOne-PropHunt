using System.Collections.Generic;
using HarmonyLib;
using Il2CppScheduleOne.Trash;
using Il2CppScheduleOne.Combat;

namespace PropHunt.Patches
{
    /// <summary>
    /// Keep movable world objects STATIC during a round so a real (pushable) trash can / mailbox can't be told apart
    /// from a static hider-prop by shooting or bumping it. Client-local (isKinematic and the impact force are local):
    ///  - PhysicsDamageable.ReceiveImpact: skip the AddForce, so shooting a physics object never shoves it (covers
    ///    trash, mailboxes and any other PhysicsDamageable decor).
    ///  - Trash rigidbodies are kinematic for the whole round. Game 0.4.7 removed <c>TrashItem.SetPhysicsActive</c>
    ///    (trash used to fall asleep kinematic and wake on contact); trash is now always a live body, so
    ///    <see cref="TrashFreeze"/> freezes every TrashItem on the round edge and every one spawned during it once it
    ///    has landed.
    /// Trash pickup is the TrashItemDraggable interaction (untouched), so the trash grabber still collects it; colliders
    /// stay (the grabber ray + catch hitbox need them). No-op outside a round.
    /// </summary>
    [HarmonyPatch(typeof(PhysicsDamageable), "ReceiveImpact")]
    internal static class BlockPhysicsImpactPatch
    {
        private static bool Prefix() => !RoundInteractionGate.RoundActive;   // false -> no impact force while a round is active
    }

    /// <summary>Trash that spawns during a round (reload casings, grabber drops, an NPC emptying a can, station output)
    /// is frozen once it has landed. <c>Initialize</c> is where vanilla gives new trash its spawn velocity, so freezing
    /// here would pin it in mid-air at the spawn point; <see cref="TrashFreeze.FreezeWhenSettled"/> waits instead.</summary>
    [HarmonyPatch(typeof(TrashItem), nameof(TrashItem.Initialize))]
    internal static class FreezeSpawnedTrashPatch
    {
        private static void Postfix(TrashItem __instance)
        {
            if (!TrashFreeze.Frozen) return;
            try { MelonLoader.MelonCoroutines.Start(TrashFreeze.FreezeWhenSettled(__instance)); }
            catch (System.Exception e) { Core.Log.Warning("trash: settle watch for spawned trash failed to start: " + e.Message); }
        }
    }

    internal static class TrashFreeze
    {
        private static readonly List<Rigidbody> _frozen = new List<Rigidbody>();
        internal static bool Frozen { get; private set; }

        private const float SettleSpeed = 0.1f;       // m/s below which a body counts as resting
        private const int SettleSteps = 10;           // physics steps in a row it must rest (0.2 s): the top of a throw is slow for one step only
        private const float SettleTimeout = 8f;       // a body still rolling after this is frozen where it is
        private const float MaxRestGap = 0.3f;        // m between collider bottom and ground up to which a resting body counts as landed

        /// <summary>Wait until a freshly spawned trash body has rested for <see cref="SettleSteps"/> physics steps in a
        /// row, then freeze it. <c>Rigidbody.IsSleeping</c> is no signal here: it reports a thrown body asleep at the
        /// top of its arc. Stops without a freeze when the round ends first or the item is destroyed.</summary>
        internal static System.Collections.IEnumerator FreezeWhenSettled(TrashItem item)
        {
            Rigidbody rb = null;
            Vector3 start = Vector3.zero;
            string name = "?";
            try
            {
                rb = item != null ? item.GetComponent<Rigidbody>() : null;
                if (rb != null) { start = rb.position; name = item.name; }
            }
            catch (System.Exception e) { Core.Log.Warning("trash: settle watch could not read the spawned body: " + e.Message); }
            if (rb == null) { Core.LogDebug("trash: spawned trash has no rigidbody - nothing to freeze"); yield break; }

            float age = 0f;
            int resting = 0;
            string reason = "timeout";
            while (age < SettleTimeout)
            {
                yield return new WaitForFixedUpdate();
                age += Time.fixedDeltaTime;
                if (!Frozen) { Core.LogDebug($"trash: round ended before {name} settled - left live"); yield break; }
                bool gone, settled = false;
                try
                {
                    gone = rb == null || item == null;
                    if (!gone)
                    {
                        bool slow = rb.velocity.magnitude < SettleSpeed && rb.angularVelocity.magnitude < 1f;
                        resting = slow ? resting + 1 : 0;
                        if (resting >= SettleSteps)
                        {
                            // Slow but still in the air: PhysX put the body to sleep at the top of its arc. Wake it so
                            // it falls, and count again.
                            float gap = AboveGround(item);
                            if (gap > MaxRestGap)
                            {
                                rb.WakeUp();
                                resting = 0;
                                Core.LogDebug($"trash: {name} rests {gap:0.00} m above ground - woken to fall");
                            }
                            else { settled = true; reason = gap <= -1f ? "resting, no ground found" : "resting"; }
                        }
                    }
                }
                catch (System.Exception e) { Core.Log.Warning($"trash: settle watch of {name} failed: " + e.Message); yield break; }
                if (gone) { Core.LogDebug($"trash: {name} was removed before it settled"); yield break; }
                if (settled) break;
            }
            try
            {
                if (rb.isKinematic) { Core.LogDebug($"trash: {name} turned kinematic on its own - not ours to freeze"); yield break; }
                Freeze(item);
                Core.LogDebug($"trash: spawned {name} settled after {age:0.00}s ({reason}), fell {start.y - rb.position.y:0.00} m, " +
                              $"{AboveGround(item):0.00} m above ground - frozen");
            }
            catch (System.Exception e) { Core.Log.Warning($"trash: freezing settled {name} failed: " + e.Message); }
        }

        /// <summary>Gap between the bottom of the item's enabled colliders (its pivot when none is enabled) and the first
        /// solid surface below it, or -1 when there is none within 50 m.</summary>
        internal static float AboveGround(TrashItem item)
        {
            var pos = item.transform.position;
            Bounds b = new Bounds(pos, Vector3.zero);
            bool any = false;
            var cols = item.GetComponentsInChildren<Collider>();
            if (cols != null)
                foreach (var c in cols)
                {
                    if (c == null || !c.enabled || c.isTrigger) continue;
                    if (any) b.Encapsulate(c.bounds); else { b = c.bounds; any = true; }
                }
            if (!any) Core.LogDebug($"trash: {item.name} has no enabled collider - ground gap measured from its pivot");
            RaycastHit[] hits = Physics.RaycastAll(b.center + Vector3.up * 0.05f, Vector3.down, 50f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.transform.IsChildOf(item.transform)) continue;
                if (h.distance < best) best = h.distance;
            }
            return best == float.MaxValue ? -1f : b.min.y - (b.center.y + 0.05f - best);
        }

        /// <summary>Called every frame with the round flag; acts only on the edge.</summary>
        internal static void Tick(bool roundActive)
        {
            if (roundActive == Frozen) return;
            if (roundActive) FreezeAll();
            else ReleaseAll();
        }

        internal static void Freeze(TrashItem item)
        {
            try
            {
                var rb = item != null ? item.GetComponent<Rigidbody>() : null;
                if (rb == null || rb.isKinematic) return;   // already static (a vanilla or another mod's choice) - not ours to release
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                rb.isKinematic = true;
                _frozen.Add(rb);
            }
            catch (System.Exception e) { Core.Log.Warning("trash freeze failed: " + e.Message); }
        }

        private static void FreezeAll()
        {
            Frozen = true;
            int before = _frozen.Count;
            try
            {
                foreach (var item in UnityEngine.Object.FindObjectsOfType<TrashItem>()) Freeze(item);
            }
            catch (System.Exception e) { Core.Log.Warning("trash freeze scan failed: " + e.Message); }
            Core.Log.Msg($"trash: froze {_frozen.Count - before} trash bodies for the round.");
        }

        private static void ReleaseAll()
        {
            Frozen = false;
            int released = 0;
            foreach (var rb in _frozen)
            {
                try { if (rb != null) { rb.isKinematic = false; released++; } }
                catch (System.Exception e) { Core.Log.Warning("trash release failed: " + e.Message); }
            }
            _frozen.Clear();
            Core.Log.Msg($"trash: released {released} trash bodies.");
        }
    }
}
