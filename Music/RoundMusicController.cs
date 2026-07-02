using System;
using Il2CppScheduleOne.Audio;

namespace PropHunt.Music
{
    /// <summary>
    /// Plays a fitting EXISTING game music track during a round by reusing the game's <see cref="MusicManager"/>
    /// (no custom assets). Purely local per client - driven by the synced round phase, so every client starts/stops
    /// its own music on its own phase edge; no netcode. Always restores the game's normal music when a round ends /
    /// the session tears down. Track NAMES are configured in prefs (discovered live via the DEBUG 'phmusic' dump).
    /// </summary>
    internal static class RoundMusicController
    {
        private static string _active;

        /// <summary>Enable a named game track (disabling any track we previously enabled). No-op for an empty name.</summary>
        internal static void Play(string trackName)
        {
            if (string.IsNullOrEmpty(trackName)) return;
            try
            {
                var mm = PersistentSingleton<MusicManager>.Instance;
                if (mm == null) return;
                if (!string.IsNullOrEmpty(_active) && _active != trackName) { try { mm.StopTrack(_active); } catch { } }
                mm.SetTrackEnabled(trackName, true);
                _active = trackName;
                Core.LogDebug($"[PropHunt] music -> '{trackName}'");
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] music play failed: " + e.Message); }
        }

        /// <summary>Fade out + stop whatever round track we enabled, handing music back to the game. Disables the track
        /// FIRST (so MusicManager.UpdateTracks won't immediately re-select + re-play it) then Stop()s it, which makes
        /// MusicTrack.Update ramp its volume down over the track's FadeOutTime (~2s) before silencing the source - i.e.
        /// a smooth fade, not a hard cut.</summary>
        internal static void Stop()
        {
            try
            {
                var mm = PersistentSingleton<MusicManager>.Instance;
                if (mm != null && !string.IsNullOrEmpty(_active))
                {
                    mm.SetTrackEnabled(_active, false);   // Disable -> not re-selected by UpdateTracks
                    mm.StopTrack(_active);                // Stop -> MusicTrack.Update fades the volume out, then stops the source
                }
            }
            catch { }
            _active = null;
        }

#if DEBUG
        /// <summary>Console 'phmusic': list every loaded MusicTrack name so we can pick fitting round tracks to wire
        /// into the HidingMusicTrack / HuntingMusicTrack prefs.</summary>
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
