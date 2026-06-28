#if DEBUG
using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace PropHunt.Debug
{
    /// <summary>
    /// DEBUG-only in-game SOUND AUDITION tool (toggle: phsounds console command). Collects every loaded AudioClip,
    /// lets you scroll through them and HEAR each one (2D playback, audible anywhere), and mark favourites (logged
    /// so you can tell us which clip to use for the taunt). Like the prop curator, but for audio.
    ///
    /// Keys while active: [Space] replay, [Left]/[Right] prev/next (auto-plays), [PgUp]/[PgDn] +-10, [F] favourite.
    /// </summary>
    internal static class SoundBrowser
    {
        private static bool _active;
        private static List<AudioClip> _clips;
        private static int _index;
        private static GameObject _go;
        private static AudioSource _src;
        private static float _lastToggle = -999f;
        private static readonly List<string> _favorites = new List<string>();
        private static GUIStyle _style;

        internal static bool Active => _active;

        /// <summary>Toggle from the phsounds console command (debounced: SubmitCommand fires twice).</summary>
        internal static void Toggle()
        {
            float now = Time.time;
            if (now - _lastToggle < 0.4f) return;
            _lastToggle = now;
            if (_active) { Exit(); return; }

            Collect();
            if (_clips == null || _clips.Count == 0)
            {
                Core.Log.Warning("[PropHunt] phsounds: no AudioClips loaded yet - load a world (and let NPCs spawn) first.");
                return;
            }
            _index = 0;
            _active = true;
            EnsurePlayer();
            Core.Log.Msg($"[PropHunt] sound browser ON: {_clips.Count} clips.  [Space] replay  [<- / ->] prev/next  [PgUp/PgDn] +-10  [F] favourite  (phsounds = exit)");
            Play();
        }

        private static void Exit()
        {
            _active = false;
            if (_go != null) { try { Object.Destroy(_go); } catch { } _go = null; _src = null; }
            if (_favorites.Count > 0) Core.Log.Msg("[PropHunt] sound favourites: " + string.Join(", ", _favorites));
        }

        private static void Collect()
        {
            _clips = new List<AudioClip>();
            try
            {
                var all = Resources.FindObjectsOfTypeAll<AudioClip>();
                var seen = new HashSet<string>();
                if (all != null)
                    for (int i = 0; i < all.Length; i++)
                    {
                        var c = all[i];
                        if (c != null && !string.IsNullOrEmpty(c.name) && seen.Add(c.name)) _clips.Add(c);
                    }
                _clips.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] phsounds collect failed: " + e.Message); }
        }

        private static void EnsurePlayer()
        {
            if (_go != null) return;
            _go = new GameObject("ph_sound_audition");
            Object.DontDestroyOnLoad(_go);
            _src = _go.AddComponent<AudioSource>();
            _src.spatialBlend = 0f;   // 2D: audible regardless of position
            _src.volume = 1f;
            _src.loop = false;
        }

        private static AudioClip Current => (_clips != null && _index >= 0 && _index < _clips.Count) ? _clips[_index] : null;

        private static void Play()
        {
            var c = Current;
            if (c == null || _src == null) return;
            try { _src.Stop(); _src.clip = c; _src.Play(); }
            catch (Exception e) { Core.LogDebug("[PropHunt] sound play failed: " + e.Message); }
        }

        private static void Move(int d)
        {
            if (_clips == null || _clips.Count == 0) return;
            int n = _clips.Count;
            _index = ((_index + d) % n + n) % n;
            Play();
        }

        internal static void Tick()
        {
            if (!_active) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.RightArrow)) Move(1);
                else if (Input.GetKeyDown(KeyCode.LeftArrow)) Move(-1);
                else if (Input.GetKeyDown(KeyCode.PageDown)) Move(10);
                else if (Input.GetKeyDown(KeyCode.PageUp)) Move(-10);
                else if (Input.GetKeyDown(KeyCode.Space)) Play();
                else if (Input.GetKeyDown(KeyCode.F))
                {
                    var c = Current;
                    if (c != null && !_favorites.Contains(c.name))
                    {
                        _favorites.Add(c.name);
                        PropHunt.Taunt.TauntSounds.AddFavorite(c.name);   // persist for the manual-taunt wheel
                        Core.Log.Msg($"[PropHunt] sound favourite saved: {c.name}");
                    }
                }
            }
            catch { }
        }

        internal static void DrawGui()
        {
            if (!_active || _clips == null) return;
            EnsureStyle();
            const float w = 660f, lh = 20f;
            int window = 7;
            float h = 70f + window * lh;
            var box = new Rect((Screen.width - w) / 2f, 12f, w, h);
            GUI.Box(box, "PropHunt - Sound Browser");

            Line(box, 26f, $"[{_index + 1}/{_clips.Count}]    favourites: {_favorites.Count}", 16, Color.yellow);
            int half = window / 2;
            for (int k = -half; k <= half; k++)
            {
                int i = _index + k;
                if (i < 0 || i >= _clips.Count) continue;
                bool cur = k == 0;
                Line(box, 52f + (k + half) * lh, (cur ? "> " : "   ") + _clips[i].name, cur ? 16 : 13,
                     cur ? Color.green : new Color(0.8f, 0.85f, 1f));
            }
            Line(box, 56f + window * lh, "[Space] replay   [<- / ->] prev/next   [PgUp/PgDn] +-10   [F] favourite   (phsounds = exit)", 12, Color.cyan);
        }

        private static void Line(Rect box, float dy, string text, int size, Color col)
        {
            _style.fontSize = size;
            _style.normal.textColor = col;
            GUI.Label(new Rect(box.x + 14f, box.y + dy, box.width - 28f, size + 8f), text, _style);
        }

        private static void EnsureStyle()
        {
            if (_style == null) _style = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold, richText = false };
        }
    }
}
#endif
