using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using UnityEngine;
using MelonLoader.Utils;
using Il2CppScheduleOne.Audio;
using Il2CppScheduleOne.DevUtilities;

namespace PropHunt.Taunt
{
    /// <summary>
    /// Resolves and plays taunt sounds. Clips are looked up by name from the loaded AudioClips (the same set the
    /// phsounds browser shows). Playback is a fire-and-forget 3D AudioSource with a long linear rolloff so hunters
    /// hear direction + proximity across the whole play area (CoD-style). Default auto-taunts = Cork Pop / fart1-9
    /// (+ a rare toilet flush); manual taunts come from the player's favourites file (authored via phsounds [F]).
    /// </summary>
    internal static class TauntSounds
    {
        private static Dictionary<string, AudioClip> _cache;
        private static float _cacheBuiltAt = -999f;
        private static List<string> _defaultPool;
        private static string _flushName;

        private const string FlushClip = "foley-toilet-flush-without-tank-refill-238004";
        private const float FlushChance = 0.001f;   // 0.1% rare easter egg

        private static string FavPath => Path.Combine(MelonEnvironment.UserDataDirectory, "PropHunt", "taunt_favorites.txt");

        // ---- clip cache ----
        private static void BuildCache()
        {
            // rebuild at most every few seconds (clips load in as NPCs/scenes spawn)
            if (_cache != null && Time.time - _cacheBuiltAt < 5f) return;
            _cache = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var all = Resources.FindObjectsOfTypeAll<AudioClip>();
                if (all != null)
                    for (int i = 0; i < all.Length; i++)
                    {
                        var c = all[i];
                        if (c != null && !string.IsNullOrEmpty(c.name) && !_cache.ContainsKey(c.name)) _cache[c.name] = c;
                    }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] taunt cache failed: " + e.Message); }
            _cacheBuiltAt = Time.time;
        }

        private static AudioClip GetClip(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (_cache == null || !_cache.ContainsKey(name)) BuildCache();
            return (_cache != null && _cache.TryGetValue(name, out var c)) ? c : null;
        }

        // ---- playback ----
        // Volume tuning: the clip plays AT the hider, so the hider's own taunt is point-blank (within minDistance =
        // full volume) - that is why it felt deafening. We scale the FX-mixer volume down so a taunt is clearly
        // audible but not overwhelming, and a global-whistle sweep (many sources in quick succession) a touch lower
        // still. The 3D linear rolloff keeps direction/distance cues; a tighter maxDistance makes far props quieter.
        private const float ManualVolumeScale = 0.6f;    // single taunt ([1] / caught reveal)
        private const float WhistleVolumeScale = 0.45f;  // global whistle sweep entry (stacks in time)

        /// <summary>Play a named taunt clip at a world position (3D, linear rolloff). Full (manual) volume.</summary>
        internal static void Play(string clipName, Vector3 pos) => PlayInternal(clipName, pos, WhistleVolume: false);

        /// <summary>Play a global-whistle reveal: same as <see cref="Play"/> but at reduced volume (sweep stacking).</summary>
        internal static void PlayWhistle(string clipName, Vector3 pos) => PlayInternal(clipName, pos, WhistleVolume: true);

        private static void PlayInternal(string clipName, Vector3 pos, bool WhistleVolume)
        {
            var clip = GetClip(clipName);
            if (clip == null) { Core.LogDebug($"[PropHunt] taunt clip not found: '{clipName}'"); return; }
            PlayClip(clip, pos, WhistleVolume ? WhistleVolumeScale : ManualVolumeScale);
        }

        /// <summary>Action-feedback SFX (catch / stun / decoy pop): play the first clip whose name matches one of
        /// the candidate names (exact first, then substring) at a world position. Clip names vary across game
        /// builds, so this is best-effort - if nothing matches it is a silent no-op (the on-screen flash still
        /// carries the feedback).</summary>
        internal static void PlayFx(string[] candidates, Vector3 pos, float volumeScale = 0.7f)
        {
            var clip = ResolveAny(candidates);
            if (clip == null) { Core.LogDebug("[PropHunt] fx clip not found: " + string.Join("/", candidates ?? Array.Empty<string>())); return; }
            PlayClip(clip, pos, volumeScale);
        }

        private static AudioClip ResolveAny(string[] names)
        {
            if (names == null) return null;
            BuildCache();
            if (_cache == null) return null;
            foreach (var n in names) if (!string.IsNullOrEmpty(n) && _cache.TryGetValue(n, out var c) && c != null) return c;
            foreach (var n in names)
            {
                if (string.IsNullOrEmpty(n)) continue;
                foreach (var kv in _cache)
                    if (kv.Value != null && kv.Key.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
            }
            return null;
        }

        private static void PlayClip(AudioClip clip, Vector3 pos, float volumeScale)
        {
            if (clip == null) return;
            try
            {
                var go = new GameObject("ph_taunt");
                go.transform.position = pos;
                var src = go.AddComponent<AudioSource>();
                src.clip = clip;
                src.spatialBlend = 1f;                            // 3D: gives hunters direction + distance
                // Logarithmic (not Linear): volume roughly halves each time the distance from the prop doubles past
                // minDistance, so a close prop is clearly loud while one on the FAR side of the zone is barely
                // audible - a Linear rolloff stayed too loud end-to-end. ~6m full -> ~12m half -> ~50m faint.
                src.rolloffMode = AudioRolloffMode.Logarithmic;
                src.minDistance = 6f;                             // full-volume core
                src.maxDistance = 140f;                           // hard cutoff beyond the play-area diameter
                src.dopplerLevel = 0f;
                // respect the game's audio settings: route through the main mixer (master/game volume slider) and
                // scale by the player's FX volume - so it is NOT blasting at full volume independent of settings.
                float fx = 0.7f;
                try
                {
                    var am = PersistentSingleton<AudioManager>.Instance;
                    if (am != null) { src.outputAudioMixerGroup = am.MainGameMixer; fx = am.GetVolume(EAudioType.FX, true); }
                }
                catch { }
                src.volume = fx * volumeScale;
                src.Play();
                UnityEngine.Object.Destroy(go, clip.length + 0.2f);
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] taunt play failed: " + e.Message); }
        }

        // ---- default auto pool ----
        private static void BuildDefaults()
        {
            BuildCache();
            _defaultPool = new List<string>();
            _flushName = null;
            if (_cache == null) return;
            foreach (var kv in _cache)
            {
                string n = kv.Key;
                if (n.IndexOf("cork pop", StringComparison.OrdinalIgnoreCase) >= 0) _defaultPool.Add(n);
                else if (Regex.IsMatch(n, @"^fart\s*\d+$", RegexOptions.IgnoreCase)) _defaultPool.Add(n);
                else if (string.Equals(n, FlushClip, StringComparison.OrdinalIgnoreCase)) _flushName = n;
            }
        }

        /// <summary>The default-taunt clip names (Cork Pop / fart1-9). Used as the radial-wheel fallback.</summary>
        internal static List<string> DefaultNames()
        {
            if (_defaultPool == null) BuildDefaults();
            return new List<string>(_defaultPool ?? new List<string>());
        }

        /// <summary>Options for the manual taunt wheel: the player's favourites, or the defaults if none are set.</summary>
        internal static List<string> WheelOptions()
        {
            var fav = ManualFavorites();
            return fav.Count > 0 ? fav : DefaultNames();
        }

        /// <summary>A default auto-taunt clip name: Cork Pop / fart1-9, with a 0.1% chance of the toilet flush.</summary>
        internal static string PickDefault()
        {
            if (_defaultPool == null || _defaultPool.Count == 0) BuildDefaults();
            if (_flushName != null && UnityEngine.Random.value < FlushChance) return _flushName;
            if (_defaultPool != null && _defaultPool.Count > 0) return _defaultPool[UnityEngine.Random.Range(0, _defaultPool.Count)];
            return null;
        }

        // ---- manual favourites (authored via phsounds [F]) ----
        internal static List<string> ManualFavorites()
        {
            var list = new List<string>();
            try
            {
                if (File.Exists(FavPath))
                    foreach (var raw in File.ReadAllLines(FavPath))
                    {
                        var s = raw.Trim();
                        if (s.Length > 0 && !s.StartsWith("#") && !list.Contains(s)) list.Add(s);
                    }
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] taunt favourites read failed: " + e.Message); }
            return list;
        }

        /// <summary>Append a clip name to the manual-taunt favourites file (no-op if already present).</summary>
        internal static void AddFavorite(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                var list = ManualFavorites();
                if (list.Contains(name)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(FavPath));
                File.AppendAllText(FavPath, name + "\n");
            }
            catch (Exception e) { Core.LogDebug("[PropHunt] taunt favourite write failed: " + e.Message); }
        }
    }
}
