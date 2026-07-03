using System;
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.DevUtilities;

namespace PropHunt.Music
{
    /// <summary>
    /// ONE continuous game music track for the whole session, reused from the game's <see cref="MusicManager"/> (no
    /// custom assets). The track is enabled ONCE and never stopped between rounds, so it plays CONTINUOUSLY and never
    /// restarts on a phase change. For the hunt we do NOT stop it - we smoothly FADE the game's Music volume bus down,
    /// then fade it back up at round end, so it resumes SEAMLESSLY. Crucially the fade runs strictly between the
    /// PLAYER'S OWN music volume and 0 - never above it: the player's level is read fresh whenever we begin a duck
    /// from a settled non-hunt state (so a change they make between rounds is respected), and outside a hunt we don't
    /// touch the bus at all. If they have music at 50%, we only ever move between 50% and 0%. The fade is driven
    /// per-frame by <see cref="Tick"/> via <c>AudioManager.SetVolume</c>, which only sets a runtime field + re-applies
    /// the mixer (no disk write, no persistent subscriber). The hiders' whistles play on the FX bus, so ducking Music
    /// never masks them. Purely local per client; the player's volume is always restored on teardown.
    /// </summary>
    internal static class RoundMusicController
    {
        private static string _active;
        private static bool _muted;              // intent: are we ducking the Music bus for a hunt?
        private static float _userVolume = -1f;  // the PLAYER'S own music volume, read while fully unducked (-1 = never touched the bus)
        private static float _target = -1f;      // the volume we're currently fading the Music bus toward
        private static bool _fading;

        /// <summary>Seconds for a full fade of the music bus (down at the hunt / back up at round end).</summary>
        private const float FadeSeconds = 1.6f;

        /// <summary>The round track we keep enabled ("" = none). The hunt FADES the bus rather than stopping this, so
        /// it stays "active" (playing, silent) through the hunt.</summary>
        internal static string Active => _active ?? "";

        /// <summary>Non-hunt phase: keep the continuous track enabled and, if we ducked for a hunt, fade the Music bus
        /// back UP to the player's own volume (never higher). When not ducked we leave the bus exactly where the player
        /// set it - so their live volume (incl. a change between rounds) is untouched outside a hunt.</summary>
        internal static void Play(string trackName)
        {
            if (string.IsNullOrEmpty(trackName)) return;
            try
            {
                var mm = PersistentSingleton<MusicManager>.Instance;
                if (mm == null) return;
                if (!string.IsNullOrEmpty(_active) && _active != trackName) { try { mm.StopTrack(_active); } catch { } }
                mm.SetTrackEnabled(trackName, true);   // enable (or re-enable if the clip auto-ended); no-op if already playing
                _active = trackName;
                if (_muted)                            // coming out of a hunt -> glide back up to the player's volume
                {
                    _muted = false;
                    if (_userVolume >= 0f) FadeTo(_userVolume);
                }
                LogState("after Play " + trackName);
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] music play failed: " + e.Message); }
        }

        /// <summary>Hunt begins: smoothly FADE the Music volume bus to 0 (whistles are on the FX bus, unaffected)
        /// WITHOUT stopping the track - it keeps playing silently so it resumes seamlessly at round end. Reads the
        /// player's CURRENT music volume first (only when fully settled, so it's their real level, not a half-faded
        /// one), which is the ceiling we fade down from and back up to.</summary>
        internal static void MuteForHunt()
        {
            if (!_muted && !_fading)   // settled at the player's volume -> capture it fresh (picks up between-round changes)
            {
                float v = ReadUserVolume();
                if (v >= 0f) _userVolume = v;
            }
            if (_userVolume < 0f) return;   // couldn't read the player's own volume -> leave the game's music alone rather than risk sticking it silent
            _muted = true;
            FadeTo(0f);
            LogState("after MuteForHunt");
        }

        /// <summary>Per-frame music-bus fade toward the current target (call every frame). Cheap no-op when not
        /// fading. <c>SetVolume</c> only sets a runtime field + re-applies the mixer, so per-frame lerping is safe.</summary>
        internal static void Tick(float dt)
        {
            if (!_fading) return;
            try
            {
                var am = Singleton<AudioManager>.Instance;
                if (am == null) { _fading = false; return; }
                float cur = am.GetVolume(EAudioType.Music, false);
                float span = _userVolume > 0.0001f ? _userVolume : 1f;   // scale the rate to the player's level -> constant ~FadeSeconds fade
                float next = UnityEngine.Mathf.MoveTowards(cur, _target, span / FadeSeconds * dt);
                am.SetVolume(EAudioType.Music, next);
                if (UnityEngine.Mathf.Approximately(next, _target)) _fading = false;
            }
            catch { _fading = false; }
        }

        /// <summary>Read the player's own music volume (raw per-type, round-trips with SetVolume). -1 if unavailable.</summary>
        private static float ReadUserVolume()
        {
            try { var am = Singleton<AudioManager>.Instance; if (am != null) return am.GetVolume(EAudioType.Music, false); }
            catch { }
            return -1f;
        }

        private static void FadeTo(float target)
        {
            _target = target;
            _fading = true;
        }

        /// <summary>Teardown: instantly restore the player's music bus (if we ever ducked it) + stop our track, handing
        /// music fully back to the game. Called on session dispose so a ducked bus never leaks out of PropHunt.</summary>
        internal static void Stop()
        {
            _fading = false;
            try
            {
                var am = Singleton<AudioManager>.Instance;
                if (am != null && _userVolume >= 0f) am.SetVolume(EAudioType.Music, _userVolume);   // hand the player's own level back
            }
            catch { }
            try
            {
                var mm = PersistentSingleton<MusicManager>.Instance;
                if (mm != null && !string.IsNullOrEmpty(_active))
                {
                    mm.SetTrackEnabled(_active, false);
                    mm.StopTrack(_active);
                }
            }
            catch { }
            _active = null;
            _muted = false;
            _userVolume = -1f;
            _target = -1f;
            LogState("after Stop");
        }

        /// <summary>DEBUG diagnostic: the fade/duck state + player volume + live Music bus volume + every
        /// ENABLED/PLAYING MusicTrack. Compiled out of Release. Called after every Play/Mute/Stop.</summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void LogState(string ctx)
        {
            try
            {
                float mv = -1f; try { var am = Singleton<AudioManager>.Instance; if (am != null) mv = am.GetVolume(EAudioType.Music, false); } catch { }
                var tracks = UnityEngine.Resources.FindObjectsOfTypeAll<MusicTrack>();
                var sb = new System.Text.StringBuilder();
                if (tracks != null)
                    foreach (var t in tracks)
                        try { if (t != null && (t.Enabled || t.IsPlaying)) sb.Append($"  [{t.TrackName} prio={t.Priority} en={(t.Enabled ? 1 : 0)} play={(t.IsPlaying ? 1 : 0)}]"); } catch { }
                Core.Log.Msg($"[PropHunt] music state ({ctx}): active='{_active}' muted={_muted} fading={_fading} user={_userVolume:F2} target={_target:F2} musicVol={mv:F2}{(sb.Length > 0 ? sb.ToString() : "  (none enabled/playing)")}");
            }
            catch { }
        }

#if DEBUG
        /// <summary>Console 'phmusic': list every loaded MusicTrack name so we can pick a fitting round track for the
        /// MusicTrack pref.</summary>
        internal static void DumpTracks()
        {
            try
            {
                var tracks = UnityEngine.Resources.FindObjectsOfTypeAll<MusicTrack>();
                int n = tracks != null ? tracks.Length : 0;
                Core.Log.Msg($"[PropHunt] phmusic: {n} MusicTrack(s) loaded:");
                if (tracks != null)
                    for (int i = 0; i < tracks.Length; i++)
                    { try { Core.Log.Msg($"[PropHunt]   '{tracks[i].TrackName}'  (playing={tracks[i].IsPlaying}, prio={tracks[i].Priority})"); } catch { } }
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] phmusic failed: " + e.Message); }
        }
#endif
    }
}
