using System;
using System.Collections;
using MelonLoader;

namespace PropHunt.View
{
    /// <summary>
    /// Drives the vanilla <see cref="EyelidOverlay"/> (a client-local <see cref="Singleton{T}"/> UI, no networking)
    /// for two PropHunt effects, so both feel like the player's OWN eyes instead of a hard black screen:
    /// <list type="bullet">
    /// <item><see cref="Teleport"/> - a one-shot BLINK that hides a teleport: eyes fade shut, the move runs while
    /// black, eyes fade open at the new spot (mirrors the game's <c>PassOutScreen</c> faint + the Faded entry
    /// cinematic).</item>
    /// <item><see cref="Blind"/>/<see cref="Unblind"/> - a sustained "eyes shut" HOLD for the hunter's hide-phase
    /// blindfold: eyes close and stay shut until the hunt begins, then open.</item>
    /// </list>
    /// Both set <c>EyelidOverlay.AutoUpdate = false</c> while driving (so the vanilla tiredness system doesn't fight
    /// the lerp) and hand the eyelids back open afterwards; a try/finally guarantees the player is never left blind.
    /// All LOCAL + cosmetic. The two never run together in practice (a blindfolded hunter is frozen, so no teleport
    /// happens), but the one-shot blink still respects an active blindfold so it can never open a blindfolded hunter's
    /// eyes.
    /// </summary>
    internal static class EyeBlink
    {
        private static bool _running;     // a one-shot teleport blink is animating
        private static bool _blindHeld;   // sustained blindfold: the eyes are held shut

        /// <summary>True while a one-shot teleport blink is animating (callers use it to avoid stacking blinks).</summary>
        internal static bool Running => _running;

        // one-shot blink: eyes shut fast, open a touch slower ("arriving")
        private const float CloseSeconds = 0.5f;
        private const float OpenSeconds = 0.65f;
        // blindfold fades (short, at the phase edges)
        private const float BlindCloseSeconds = 0.4f;
        private const float BlindOpenSeconds = 0.45f;

        // ---------------- one-shot teleport blink ----------------

        /// <summary>Run <paramref name="onShut"/> (the teleport) behind an eye-blink. Runs it immediately with NO blink
        /// when the eyelid overlay isn't ready or another blink is in flight, so the move is never lost.</summary>
        internal static void Teleport(Action onShut)
        {
            if (onShut == null) return;
            if (_running || !Available()) { SafeInvoke(onShut); return; }
            try { MelonCoroutines.Start(RunBlink(onShut)); }
            catch (Exception e) { Core.LogDebug("[PropHunt] blink start failed: " + e.Message); SafeInvoke(onShut); }
        }

        private static IEnumerator RunBlink(Action onShut)
        {
            _running = true;
            var e = Overlay();
            if (e != null) { try { e.AutoUpdate = false; } catch { } }   // stop the vanilla tiredness system re-driving the eyelids
            try
            {
                float start = SafeCurrent(e, 1f);
                for (float t = 0f; t < CloseSeconds; t += Time.deltaTime) { SetOpen(e, Mathf.Lerp(start, 0f, EaseInOut(t / CloseSeconds))); yield return null; }
                SetOpen(e, 0f);

                SafeInvoke(onShut);            // move the player while the screen is fully black
                yield return null;             // let the move settle a couple of frames before opening
                yield return null;

                // stay shut if a blindfold took over during the move; otherwise open back up ("arriving")
                if (!_blindHeld)
                {
                    for (float t = 0f; t < OpenSeconds; t += Time.deltaTime) { SetOpen(e, EaseOut(t / OpenSeconds)); yield return null; }
                    SetOpen(e, 1f);
                }
            }
            finally
            {
                // Hand the eyelids back on every exit path: fully open normally, or held shut if a blindfold is active.
                if (e != null) { try { e.SetOpen(_blindHeld ? 0f : 1f); if (!_blindHeld) e.AutoUpdate = true; } catch { } }
                _running = false;
            }
        }

        // ---------------- sustained blindfold (hunter hide phase) ----------------

        /// <summary>Close the local player's eyes and HOLD them shut (the hunter's hide-phase blindfold). Idempotent;
        /// pair with <see cref="Unblind"/>.</summary>
        internal static void Blind()
        {
            if (_blindHeld) return;
            _blindHeld = true;
            if (!Available()) return;
            try { MelonCoroutines.Start(FadeHold(0f, BlindCloseSeconds, releaseToGame: false)); } catch { }
        }

        /// <summary>Open the local player's eyes after a blindfold and hand the eyelids back to the game.</summary>
        internal static void Unblind()
        {
            if (!_blindHeld) return;
            _blindHeld = false;
            var e = Overlay();
            if (e == null) return;
            try { MelonCoroutines.Start(FadeHold(1f, BlindOpenSeconds, releaseToGame: true)); }
            catch { try { e.SetOpen(1f); e.AutoUpdate = true; } catch { } }
        }

        private static IEnumerator FadeHold(float target, float dur, bool releaseToGame)
        {
            var e = Overlay();
            if (e == null) yield break;
            try { e.AutoUpdate = false; } catch { }
            float start = SafeCurrent(e, target);
            for (float t = 0f; t < dur; t += Time.deltaTime) { SetOpen(e, Mathf.Lerp(start, target, EaseInOut(t / dur))); yield return null; }
            SetOpen(e, target);
            if (releaseToGame) { try { e.AutoUpdate = true; } catch { } }   // eyes fully open -> hand the eyelids back to the game
        }

        // ---------------- session hygiene ----------------

        /// <summary>Clear any leaked static state (a blink/blindfold left mid-flight by an abnormal teardown) and hand
        /// the eyelids back to the game fully open. Called on session start + teardown so a stuck blindfold can never
        /// carry into a fresh session.</summary>
        internal static void ResetState()
        {
            _running = false;
            _blindHeld = false;
            var e = Overlay();
            if (e != null) { try { e.SetOpen(1f); e.AutoUpdate = true; } catch { } }
        }

        // ---------------- shared helpers ----------------

        private static bool Available()
        {
            try { return Singleton<EyelidOverlay>.InstanceExists && Player.Local != null; }
            catch { return false; }
        }

        private static EyelidOverlay Overlay()
        {
            try { return Singleton<EyelidOverlay>.InstanceExists ? Singleton<EyelidOverlay>.Instance : null; }
            catch { return null; }
        }

        private static float SafeCurrent(EyelidOverlay e, float fallback)
        {
            if (e == null) return fallback;
            try { return e.CurrentOpen; } catch { return fallback; }
        }

        private static void SetOpen(EyelidOverlay e, float v)
        {
            if (e == null) return;
            try { e.SetOpen(Mathf.Clamp01(v)); } catch { }
        }

        private static void SafeInvoke(Action a)
        {
            try { a?.Invoke(); } catch (Exception e) { Core.LogDebug("[PropHunt] blink action failed: " + e.Message); }
        }

        // smoothstep (accelerate then decelerate) for the close; decelerate-only for the open ("arriving").
        private static float EaseInOut(float x) { x = Mathf.Clamp01(x); return x * x * (3f - 2f * x); }
        private static float EaseOut(float x) { x = Mathf.Clamp01(x); return 1f - (1f - x) * (1f - x); }
    }
}
