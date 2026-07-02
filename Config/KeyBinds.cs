using UnityEngine;

namespace PropHunt.Config
{
    /// <summary>
    /// Single source of truth for PropHunt's key bindings. Centralised so the in-game role card and the
    /// controls overlay describe EXACTLY what the controllers read (no drift between help text and behaviour),
    /// and so a future rebind layer has one place to change. Keys are fixed for now (documented, not rebindable).
    /// </summary>
    internal static class KeyBinds
    {
        // hider tooling
        internal const KeyCode Become       = KeyCode.E;            // become the prop you look at
        internal const KeyCode RandomProp   = KeyCode.Alpha2;       // become a random prop
        internal const KeyCode Rotate       = KeyCode.F;            // held + mouse: rotate the prop's facing
        internal const KeyCode SlowWalk     = KeyCode.LeftControl;  // held: slow-walk
        internal const KeyCode Decoy        = KeyCode.Q;            // drop a decoy
        internal const KeyCode Concussion   = KeyCode.G;            // stun nearby hunters
        internal const KeyCode Taunt        = KeyCode.Alpha1;       // tap: taunt, hold: taunt wheel
        internal const KeyCode HighlightToggle = KeyCode.B;         // toggle the becomable-prop markers (off = blend in)

        // shared
        internal const KeyCode ThirdPerson  = KeyCode.V;            // hider: toggle 3rd person
        internal const KeyCode Catch        = KeyCode.Mouse0;       // hunter: shoot / catch
        internal const KeyCode Help         = KeyCode.H;            // toggle the controls overlay

        // spectator (caught players)
        internal const KeyCode SpectatorToggle = KeyCode.Alpha4;    // switch follow-cam <-> freecam
        internal const KeyCode SpectatorNext   = KeyCode.Mouse0;    // cycle to the next player

        /// <summary>Short human label for a key, for the role card / controls overlay (so the help text is
        /// derived from the actual bind above, never a hand-typed copy that can drift).</summary>
        internal static string Name(KeyCode k)
        {
            switch (k)
            {
                case KeyCode.Alpha1: return "1";
                case KeyCode.Alpha2: return "2";
                case KeyCode.Alpha3: return "3";
                case KeyCode.Alpha4: return "4";
                case KeyCode.LeftControl: return "Ctrl";
                case KeyCode.Mouse0: return "Left click";
                case KeyCode.Mouse1: return "Right click";
                default: return k.ToString();
            }
        }
    }
}
