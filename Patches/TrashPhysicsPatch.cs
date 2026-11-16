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
    ///    <see cref="TrashFreeze"/> freezes every TrashItem on the round edge and every one spawned during it.
    /// Trash pickup is the TrashItemDraggable interaction (untouched), so the trash grabber still collects it; colliders
    /// stay (the grabber ray + catch hitbox need them). No-op outside a round.
    /// </summary>
    [HarmonyPatch(typeof(PhysicsDamageable), "ReceiveImpact")]
    internal static class BlockPhysicsImpactPatch
    {
        private static bool Prefix() => !RoundInteractionGate.RoundActive;   // false -> no impact force while a round is active
    }

    /// <summary>Trash that spawns during a round (thrown out by NPCs, dropped from a grabber) is frozen on arrival.</summary>
    [HarmonyPatch(typeof(TrashItem), nameof(TrashItem.Initialize))]
    internal static class FreezeSpawnedTrashPatch
    {
        private static void Postfix(TrashItem __instance)
        {
            if (TrashFreeze.Frozen) TrashFreeze.Freeze(__instance);
        }
    }

    internal static class TrashFreeze
    {
        private static readonly List<Rigidbody> _frozen = new List<Rigidbody>();
        internal static bool Frozen { get; private set; }

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
