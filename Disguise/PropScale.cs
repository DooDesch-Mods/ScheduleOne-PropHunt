using System.Collections.Generic;
using UnityEngine;

namespace PropHunt.Disguise
{
    /// <summary>
    /// Shrinks a disguised hider to the physical size of their prop, so a small prop really does fit where a
    /// person does not - under a shelf, into a corner, through a gap.
    ///
    /// HEIGHT goes through <c>Player.SetScale</c>, the game's own API (the Shrinking drug effect uses it at 0.8x).
    /// That matters: SetScale writes the player's transform scale, and Unity scales the CharacterController capsule
    /// with it, so the capsule's LOCAL height stays 1.85 and <c>PlayerMovement.UpdatePlayerHeight</c> - which
    /// rewrites that height every single frame - computes a delta of zero and never fights us.
    ///
    /// Writing <c>Controller.height</c> directly is the trap PropHunt fell into once before: vanilla restores 1.85
    /// the next frame and compensates with <c>Controller.Move(up * delta * 0.5f)</c>, which at a 0.5m target height
    /// lifts the player 0.67m PER FRAME and then drops them through the world. That is why this class exists at all.
    ///
    /// WIDTH is set separately, on <c>Controller.radius</c>. A uniform scale ties width to height at the player's own
    /// 0.7:1.85 proportions, so a wide low crate got a narrow body and a tall thin post got a fat one. Radius is safe
    /// to write where height is not: nothing in the game ever assigns it, so it stays put once set.
    ///
    /// Player.Scale is not a network variable, but nothing needs sending - every client already knows every
    /// hider's prop id from the synced state and computes the same numbers from the same catalog.
    /// </summary>
    internal static class PropScale
    {
        /// <summary>Vanilla's own capsule (PlayerMovement.DefaultCharacterControllerHeight and the prefab radius).</summary>
        private const float PlayerHeight = 1.85f;

        /// <summary>
        /// The smallest body a player can have. Purely a movement floor: below it Unity's CharacterController stops
        /// behaving (its skin width and slope handling scale with the radius) and the camera ends up in the floor.
        ///
        /// It used to be 0.35, which was high enough to swallow most of the catalog: every prop under 0.65m tall
        /// clamped to the same number, so a sign, a shrub and a crate all gave an identical capsule and the size
        /// looked fixed. A prop below this floor is still DRAWN at its real size, and its shootable box is always the
        /// prop's exact size - only the body inside it stops shrinking.
        ///
        /// 0.25 gives a 0.46m body. Going lower is one number, but it is not free: stepOffset scales with the body, so
        /// at some point a kerb or a single stair stops being climbable and a hider is stuck on the pavement. That
        /// threshold has not been measured, so this stays at a value close to the old, known-playable one.
        /// </summary>
        private const float MinScale = 0.25f;

        /// <summary>Narrowest body PhysX still moves reliably. A thinner capsule jitters against walls and can tunnel
        /// through thin geometry, so a sign-thin prop stops here rather than at its true 3cm.</summary>
        private const float MinWorldWidth = 0.2f;

        /// <summary>Each player's untouched capsule radius, so restoring never has to assume the prefab value.
        /// Keyed by instance id: every interop cast hands back a fresh wrapper, so the objects themselves are
        /// useless as dictionary keys.</summary>
        private static readonly Dictionary<int, float> _baseRadius = new Dictionary<int, float>();

        /// <summary>
        /// The scale a player wearing this prop should have. 1 = unchanged (the prop is player-sized or bigger).
        ///
        /// HEIGHT decides, because it is what the uniform scale controls: the capsule ends up as tall as the prop and
        /// never taller than a normal player. A hunter shooting at a knee-high box hits a knee-high volume, and the box
        /// does not stand in a doorway with an invisible person-shaped body around it. Width is handled by
        /// <see cref="WidthFor"/> instead of being folded in here.
        /// </summary>
        internal static float ForBounds(Bounds localBounds, Vector3 lossyScale)
        {
            float h = Mathf.Abs(localBounds.size.y * lossyScale.y);
            if (h <= 0f) return 1f;
            return Mathf.Clamp(h / PlayerHeight, MinScale, 1f);
        }

        /// <summary>
        /// How wide the body should be, in metres: the NARROWER of the two horizontal dimensions.
        ///
        /// A CharacterController's cross-section is a circle - Unity has no elliptical or box-shaped controller, and the
        /// capsule sits on the player's own transform, so it cannot be squashed on one axis either. One number has to
        /// stand in for both, and which one is a real choice:
        ///
        ///   - the WIDER one circumscribes the footprint, so the mesh never pushes into a wall, but a 3cm sign then
        ///     carries a 50cm body and cannot go anywhere a sign obviously fits;
        ///   - the NARROWER one is inscribed in the footprint, so the body is never bigger than the prop on EITHER
        ///     axis and a thin prop is genuinely thin. Its long axis can overlap a wall by the difference.
        ///
        /// Inscribed wins because it is the property players act on: a thin prop has to fit into thin gaps, and that is
        /// the whole reason to pick one. The overlap it allows is cosmetic, and only on the long axis. What a bullet
        /// hits is unaffected either way - that is a box built from these same bounds, exact on both axes.
        /// </summary>
        internal static float WidthFor(Bounds localBounds, Vector3 lossyScale)
        {
            float x = Mathf.Abs(localBounds.size.x * lossyScale.x);
            float z = Mathf.Abs(localBounds.size.z * lossyScale.z);
            float w = Mathf.Min(x, z);
            if (w <= 0f) return PlayerWidth;
            return Mathf.Clamp(w, MinWorldWidth, PlayerWidth);
        }

        /// <summary>Vanilla's capsule diameter - the cap on how wide a disguise may make someone.</summary>
        private const float PlayerWidth = 0.7f;

        /// <summary>
        /// Size a player to their prop: height via the uniform scale, width via the capsule radius.
        ///
        /// Re-checking before writing is not paranoia, it is what makes this safe to run every tick: three vanilla
        /// paths overwrite the player's scale behind our back - mounting a skateboard forces it to one, finishing a
        /// drug effect calls SetScale(1), and SetScale's lerp overload runs a coroutine that keeps writing for its
        /// whole duration. Comparing first is cheap enough to win those races every frame.
        /// </summary>
        internal static void Apply(Player player, float scale, float worldWidth)
        {
            if (player == null) return;
            try
            {
                if (Mathf.Abs(player.Scale - scale) > 0.005f)
                    player.SetScale(scale);   // instant overload; the lerp one would keep writing over us
                ApplyWidth(player, scale, worldWidth);
            }
            catch (System.Exception e) { Core.LogDebug("prop scale failed: " + e.Message); }
        }

        /// <summary>
        /// Set the capsule radius so the body is <paramref name="worldWidth"/> across, whatever the uniform scale is
        /// doing to it.
        ///
        /// The radius is stored in the controller's own space and multiplied by the transform scale, so the target has
        /// to be divided back out. It is also bounded by the capsule's height: a capsule cannot be wider than it is
        /// tall - past that Unity has nothing but a sphere to work with - so a very low, very wide prop ends up as
        /// wide as it is tall rather than its full footprint.
        /// </summary>
        private static void ApplyWidth(Player player, float scale, float worldWidth)
        {
            if (scale <= 0.001f) return;
            var cc = ResolveController(player);
            if (cc == null) return;

            int key = player.GetInstanceID();
            if (!_baseRadius.ContainsKey(key)) _baseRadius[key] = cc.radius;

            float want = (worldWidth * 0.5f) / scale;
            want = Mathf.Min(want, cc.height * 0.5f - 0.02f);
            want = Mathf.Max(want, 0.02f);
            if (Mathf.Abs(cc.radius - want) > 0.005f) cc.radius = want;
        }

        /// <summary>Back to normal size, capsule radius included. Safe to call on a player who was never scaled.</summary>
        internal static void Restore(Player player)
        {
            if (player == null) return;
            try
            {
                if (Mathf.Abs(player.Scale - 1f) > 0.005f) player.SetScale(1f);

                var cc = ResolveController(player);
                if (cc == null) return;
                int key = player.GetInstanceID();
                if (_baseRadius.TryGetValue(key, out var r))
                {
                    if (Mathf.Abs(cc.radius - r) > 0.005f) cc.radius = r;
                    _baseRadius.Remove(key);
                }
            }
            catch (System.Exception e) { Core.LogDebug("prop scale restore failed: " + e.Message); }
        }

        private static CharacterController ResolveController(Player player)
        {
            try { return player.GetComponentInChildren<CharacterController>(); }
            catch { return null; }
        }
    }
}
