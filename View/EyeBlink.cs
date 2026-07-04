using System;
using DooDesch.Transition;
using MelonLoader;

namespace PropHunt.View
{
    /// <summary>
    /// PropHunt's two eyelid effects, now thin wrappers over the shared <see cref="DooDesch.Transition"/> primitive
    /// (which consolidates the try/finally guarantee, availability fallback and eyelid hand-back that used to live
    /// here). Both use the vanilla eyelids - the player's OWN eyes, never a hard black screen:
    /// <list type="bullet">
    /// <item><see cref="Teleport"/> - a one-shot BLINK that hides a teleport: eyes fade shut, the move runs while
    /// black, eyes fade open at the new spot.</item>
    /// <item><see cref="Blind"/>/<see cref="Unblind"/> - a sustained "eyes shut" HOLD for the hunter's hide-phase
    /// blindfold.</item>
    /// </list>
    /// All LOCAL + cosmetic (multiplayer-safe: each client blinks its own screen). The one-shot blink respects an
    /// active blindfold via <c>KeepClosedAfter</c>, so it can never open a blindfolded hunter's eyes.
    /// </summary>
    internal static class EyeBlink
    {
        private const string BlinkKey = "PropHunt.Blink";

        private static bool _blindHeld;   // sustained blindfold: the eyes are held shut

        /// <summary>True while a one-shot teleport blink is animating (callers use it to avoid stacking blinks).</summary>
        internal static bool Running => ScreenTransition.IsRunning(BlinkKey);

        // one-shot blink: eyes shut fast, open a touch slower ("arriving")
        private const float CloseSeconds = 0.5f;
        private const float OpenSeconds = 0.65f;
        // blindfold fades (short, at the phase edges)
        private const float BlindCloseSeconds = 0.4f;
        private const float BlindOpenSeconds = 0.45f;

        // ---------------- one-shot teleport blink ----------------

        /// <summary>Run <paramref name="onShut"/> (the teleport) behind an eye-blink. The primitive runs it
        /// immediately with NO blink when the eyelid overlay isn't ready or another blink is in flight, so the
        /// move is never lost.</summary>
        internal static void Teleport(Action onShut)
        {
            if (onShut == null) return;
            ScreenTransition.Play(new TransitionRequest
            {
                Mechanism = VeilMode.Eyelids,        // the player's own eyes, not a hard black screen
                CloseSeconds = CloseSeconds,
                OpenSeconds = OpenSeconds,
                DuringBlack = onShut,                // move while the screen is fully black
                KeepClosedAfter = () => _blindHeld,  // a blindfold may have taken over during the move -> stay shut
                Key = BlinkKey,
            });
        }

        // ---------------- sustained blindfold (hunter hide phase) ----------------

        /// <summary>Close the local player's eyes and HOLD them shut (the hunter's hide-phase blindfold). Idempotent;
        /// pair with <see cref="Unblind"/>.</summary>
        internal static void Blind()
        {
            if (_blindHeld) return;
            _blindHeld = true;
            if (!Veil.EyelidsAvailable) return;
            try { MelonCoroutines.Start(Veil.FadeEyelids(0f, BlindCloseSeconds, releaseToGame: false)); } catch { }
        }

        /// <summary>Open the local player's eyes after a blindfold and hand the eyelids back to the game.</summary>
        internal static void Unblind()
        {
            if (!_blindHeld) return;
            _blindHeld = false;
            if (!Veil.EyelidsAvailable) return;
            try { MelonCoroutines.Start(Veil.FadeEyelids(1f, BlindOpenSeconds, releaseToGame: true)); }
            catch { Veil.SetEyelids(1f); Veil.ReleaseEyelids(); }
        }

        // ---------------- session hygiene ----------------

        /// <summary>Clear any leaked state (a blink/blindfold left mid-flight by an abnormal teardown) and hand the
        /// eyelids back to the game fully open. Called on session start + teardown so a stuck blindfold can never
        /// carry into a fresh session.</summary>
        internal static void ResetState()
        {
            _blindHeld = false;
            ScreenTransition.ForceReset(BlinkKey);   // clears the guard, eyes open + AutoUpdate on, input restored
        }
    }
}
