using UnityEngine;

namespace PropHunt.PlayArea
{
    /// <summary>
    /// "Is the local player standing in water, and how deep?"
    ///
    /// Schedule I has NO swimming state - no IsSwimming, no buoyancy, no height clamp anywhere in the player code.
    /// In shallow water the player simply walks along the bottom. The one thing vanilla does model is a Unity layer
    /// called "Water", which it mixes into its own footstep ground detection and classifies through a MaterialTag
    /// (MaterialType == Water). So the surface has colliders on that layer, and a downward ray against it gives both
    /// the answer and the surface height in one shot.
    ///
    /// Why this matters for PropHunt: a disguise is placed with its BASE at the player's feet, so a prop shorter than
    /// the water is deep sits entirely below the surface - invisible, and effectively uncatchable. Deep water is
    /// therefore treated exactly like leaving the play area.
    ///
    /// Everything here is client-local (PlayerMovement is a local-only singleton and vanilla's own water handling is
    /// client-side), so each machine measures for itself.
    /// </summary>
    internal static class WaterProbe
    {
        private const float RayStartHeight = 3f;    // start ABOVE the player: a ray starting inside a convex collider reports no hit
        private const float RayLength = 8f;
        /// <summary>How far up to look for a roof. Long enough to clear a tall tunnel, short enough that the sky never
        /// answers - open water has nothing above it at any distance.</summary>
        private const float RoofRayLength = 25f;

        private static int _mask = -1;              // 0 = no "Water" layer in this project -> feature disabled
        private static bool _maskResolved;

        /// <summary>The "Water" layer mask, or 0 when the project has no such layer.</summary>
        internal static int Mask
        {
            get
            {
                if (_maskResolved) return _mask;
                _maskResolved = true;
                try
                {
                    int layer = LayerMask.NameToLayer("Water");
                    // NameToLayer returns -1 when the layer does not exist, and `1 << -1` is `1 << 31` in C# - that
                    // would silently probe a completely unrelated layer, so bail out instead.
                    _mask = layer >= 0 ? 1 << layer : 0;
                }
                catch { _mask = 0; }
                return _mask;
            }
        }

        /// <summary>Feet height of a player, read from the LIVE character controller rather than a constant: the
        /// controller shrinks while crouched, so a fixed drop is wrong exactly when someone ducks.</summary>
        internal static float FeetY(Player p)
        {
            float y = p.transform.position.y;
            try
            {
                var mv = PlayerSingleton<PlayerMovement>.Instance;
                var cc = mv != null ? mv.Controller : null;
                // Read the capsule's WORLD bottom. height is in the controller's own space and the player is scaled to
                // their prop now, so subtracting it raw put the feet of a 0.25-scale hider 0.69m underground - and the
                // depth measured from there cleared the threshold in ankle-deep water, which eliminated them.
                if (cc != null && cc.enabled) return cc.bounds.min.y;
                if (cc != null) return y - cc.height * 0.5f * Mathf.Abs(cc.transform.lossyScale.y);
            }
            catch { }
            return y - 0.925f;   // half the default 1.85m controller
        }

        /// <summary>
        /// How deep the local player is standing in water, in metres above their feet. 0 = dry (or no water layer in
        /// this build). Wading is a small positive number; a submerged player is a large one.
        /// </summary>
        internal static float DepthOverFeet(Player p, out float surfaceY)
        {
            surfaceY = 0f;
            if (p == null || Mask == 0) return 0f;
            try
            {
                Vector3 from = p.transform.position + Vector3.up * RayStartHeight;
                // Trigger colliders MUST be included - vanilla's own footstep probe uses QueryTriggerInteraction
                // .Collide against this same mask, so the water volumes are plausibly triggers.
                if (!Physics.Raycast(from, Vector3.down, out var hit, RayLength, Mask, QueryTriggerInteraction.Collide))
                    return 0f;
                surfaceY = hit.point.y;
                float feet = FeetY(p);
                float depth = surfaceY - feet;
                return depth > 0f ? depth : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>
        /// Is there solid geometry over the player's head?
        ///
        /// This is what separates a lake from the sewers, which is a distinction the rule needs and depth cannot make:
        /// the sewer channels are water on the same layer, so a hider walking through them was told to get out of the
        /// water and then thrown out of the round - the route was simply impassable. There is no sewer zone or flag in
        /// the game to ask, and a height cut-off would be a guess about level geometry. A roof is a fact about the
        /// place, and every sewer stretch has one where open water never does.
        ///
        /// Everything except the water layer counts as roof, so the surface itself cannot be mistaken for one.
        /// </summary>
        internal static bool HasRoofAbove(Player p)
        {
            if (p == null) return false;
            try
            {
                int mask = Mask == 0 ? ~0 : ~Mask;
                Vector3 from = p.transform.position + Vector3.up * 0.2f;
                return Physics.Raycast(from, Vector3.up, RoofRayLength, mask, QueryTriggerInteraction.Ignore);
            }
            catch { return false; }   // never let a failed probe be the thing that traps someone in the sewers
        }

#if DEBUG
        /// <summary>phwater: report what the probe actually sees here - the layer index, whether anything was hit,
        /// the surface height and the depth over the local player's feet. This is how the layer setup gets confirmed
        /// in the real scene instead of being assumed.</summary>
        internal static void Dump()
        {
            try
            {
                int layer = LayerMask.NameToLayer("Water");
                var p = Player.Local;
                if (p == null) { Core.Log.Warning("phwater: no local player."); return; }

                float depth = DepthOverFeet(p, out float surface);
                var pos = p.transform.position;
                Core.Log.Msg($"phwater: layer \"Water\"={layer} mask={Mask} pos=({pos.x:F1},{pos.y:F1},{pos.z:F1}) " +
                             $"feetY={FeetY(p):F2} surfaceY={surface:F2} depthOverFeet={depth:F2}m");

                if (Mask == 0) { Core.Log.Warning("phwater: no \"Water\" layer in this build - water handling stays off."); return; }

                Vector3 from = pos + Vector3.up * RayStartHeight;
                if (!Physics.Raycast(from, Vector3.down, out var hit, RayLength, Mask, QueryTriggerInteraction.Collide))
                {
                    Core.Log.Msg("phwater: no water collider under this spot (stand in/next to water and repeat).");
                    return;
                }
                string tag = "none";
                try
                {
                    var mt = hit.collider.GetComponentInParent<Il2CppScheduleOne.Materials.MaterialTag>();
                    if (mt != null) tag = mt.MaterialType.ToString();
                }
                catch { }
                Core.Log.Msg($"phwater: hit \"{hit.collider.gameObject.name}\" layer={hit.collider.gameObject.layer} " +
                             $"isTrigger={hit.collider.isTrigger} materialTag={tag} at y={hit.point.y:F2}");
            }
            catch (System.Exception e) { Core.Log.Warning("phwater failed: " + e.Message); }
        }
#endif
    }
}
