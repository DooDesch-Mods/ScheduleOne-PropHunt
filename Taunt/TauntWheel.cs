using System.Collections.Generic;
using UnityEngine;
using PropHunt.Game;
using PropHunt.Config;

namespace PropHunt.Taunt
{
    /// <summary>
    /// LOCAL hider taunt control on [1]:
    ///   - TAP [1]   -> play the currently-set taunt (defaults to "Default" = a random standard taunt).
    ///   - HOLD [1]  -> open a radial wheel; push the MOUSE toward an entry to highlight it; RELEASE to SET it.
    /// Entry 0 is always "Default" (top) so you can return to the standard random-taunt mode. The rest are your
    /// favourites (phsounds [F]); if you have none, the default taunt clips are shown so the wheel is never empty.
    ///
    /// Selection uses the mouse DELTA (Input.GetAxis Mouse X/Y), not the cursor position: during gameplay the
    /// cursor is FPS-locked to the centre, so mousePosition never moves - accumulating the delta into an aim vector
    /// is what actually lets the mouse pick a slice. The camera look is locked while the wheel is open.
    /// </summary>
    internal sealed class TauntWheel
    {
        private readonly GameModeController _ctl;
        private List<string> _labels = new List<string>();   // display text; [0] = "Default"
        private List<string> _sounds = new List<string>();   // parallel; [0] = null (default mode)
        private string _selectedSound;                        // current tap sound (null = default mode)
        private string _selectedLabel = "Default";
        private int _highlight;
        private bool _open;
        private float _downAt = -1f;
        private Vector2 _aim;
        private const float HoldThreshold = 0.22f;
        private const float AimSpeed = 9f;
        private const float MaxAim = 100f;
        private const float DeadZone = 22f;

        internal TauntWheel(GameModeController ctl) { _ctl = ctl; }

        internal bool MenuOpen => _open;

        internal void Tick()
        {
            bool canTaunt = (_ctl.Phase == RoundPhase.Hiding || _ctl.Phase == RoundPhase.Hunting) && _ctl.LocalRole == PlayerRole.Hider;
            if (!canTaunt) { if (_open) Close(false); _downAt = -1f; return; }

            try
            {
                if (Input.GetKeyDown(KeyBinds.Taunt)) _downAt = Time.time;

                if (!_open && _downAt >= 0f && Input.GetKey(KeyBinds.Taunt) && Time.time - _downAt >= HoldThreshold)
                    Open();

                if (_open) UpdateAim();

                if (Input.GetKeyUp(KeyBinds.Taunt))
                {
                    if (_open) Close(true);                                       // commit highlighted
                    else if (_downAt >= 0f) _ctl.RequestManualTaunt(_selectedSound);   // tap -> play selected (null = default)
                    _downAt = -1f;
                }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] taunt wheel tick failed: " + e.Message); }
        }

        private void Open()
        {
            // [0] = Default, then favourites (or the default clips if no favourites)
            _labels = new List<string> { "Default" };
            _sounds = new List<string> { null };
            foreach (var s in Taunt.TauntSounds.WheelOptions()) { _labels.Add(s); _sounds.Add(s); }

            _aim = Vector2.zero;
            _highlight = Mathf.Max(0, _sounds.IndexOf(_selectedSound));
            _open = true;
            SetCanLook(false);
            Core.LogDebug($"[PropHunt] taunt wheel open: {_labels.Count} options.");
        }

        private void Close(bool commit)
        {
            if (commit && _highlight >= 0 && _highlight < _sounds.Count)
            {
                _selectedSound = _sounds[_highlight];
                _selectedLabel = _labels[_highlight];
                Core.LogDebug($"[PropHunt] taunt set: {_selectedLabel}");
            }
            _open = false;
            SetCanLook(true);
        }

        private void UpdateAim()
        {
            int n = _labels.Count;
            if (n == 0) return;
            // accumulate mouse movement into an aim direction (cursor is locked, so position doesn't move)
            _aim += new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")) * AimSpeed;
            _aim = Vector2.ClampMagnitude(_aim, MaxAim);
            if (_aim.magnitude < DeadZone) return;   // not pushed far enough yet -> keep current highlight
            float ang = Mathf.Atan2(_aim.x, _aim.y) * Mathf.Rad2Deg;   // 0 = up (Default), clockwise
            if (ang < 0f) ang += 360f;
            _highlight = Mathf.Clamp(Mathf.RoundToInt(ang / 360f * n) % n, 0, n - 1);
        }

        private static void SetCanLook(bool can)
        {
            try { var cam = PlayerSingleton<PlayerCamera>.Instance; if (cam != null) cam.SetCanLook(can); } catch { }
        }

        internal void DrawGui()
        {
            if (!_open || _labels.Count == 0) return;
            int n = _labels.Count;
            float cx = Screen.width * 0.5f, cy = Screen.height * 0.5f;
            // radius scales with entry count so chips never overlap (arc spacing stays > chip width).
            float r = Mathf.Max(180f, n * 24f);
            const float chipW = 116f, chipH = 26f, hotW = 140f, hotH = 32f;

            var label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = false };
            for (int i = 0; i < n; i++)
            {
                float rad = (i / (float)n) * Mathf.PI * 2f;     // 0 = up, clockwise
                float x = cx + Mathf.Sin(rad) * r;
                float y = cy - Mathf.Cos(rad) * r;
                bool hot = i == _highlight;
                float w = hot ? hotW : chipW, h = hot ? hotH : chipH;

                var prev = GUI.color;
                GUI.color = hot ? new Color(0.20f, 0.85f, 0.40f, 0.96f) : new Color(0.05f, 0.06f, 0.09f, 0.78f);
                GUI.Box(new Rect(x - w / 2f, y - h / 2f, w, h), GUIContent.none);
                GUI.color = prev;

                label.fontSize = hot ? 14 : 12;
                label.normal.textColor = hot ? Color.black : new Color(0.82f, 0.88f, 1f);
                GUI.Label(new Rect(x - w / 2f, y - h / 2f, w, h), Short(_labels[i]), label);
            }

            // centre: current highlight + hint
            var c = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, fontSize = 18 };
            c.normal.textColor = new Color(0.30f, 1f, 0.5f);
            GUI.Label(new Rect(cx - 220f, cy - 14f, 440f, 24f), Short(_labels[_highlight]), c);
            var hint = new GUIStyle(c) { fontSize = 12 };
            hint.normal.textColor = new Color(0.6f, 0.85f, 1f);
            GUI.Label(new Rect(cx - 220f, cy + 12f, 440f, 18f), "move mouse - release [1] to set", hint);
        }

        private static string Short(string s) => string.IsNullOrEmpty(s) ? "?" : (s.Length > 22 ? s.Substring(0, 22) : s);

        internal void Dispose() { if (_open) Close(false); }
    }
}
