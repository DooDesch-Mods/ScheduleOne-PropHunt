using UnityEngine;
using UnityEngine.UI;
using S1API.UI;
using DooDesch.UI;

namespace PropHunt.UI.Hud
{
    /// <summary>
    /// PropHunt-specific HUD widgets (HP bar, crosshair hitmarker) plus thin forwarders to the shared
    /// <see cref="DooDesch.UI.HudPrimitives"/> (label / pill / outline / anchoring). The generic primitives and the
    /// generic <see cref="DooDesch.UI.FlashLabel"/> now live in DooDesch.UI (shared via SideHustle.dll); these
    /// forwarders keep the existing <c>HudWidgets.Label/Pill/Place/...</c> call sites unchanged. Everything is
    /// DISPLAY-ONLY: raycastTarget is always off so the HUD can never intercept the catch click or the worldspace phone.
    /// </summary>
    internal static class HudWidgets
    {
        internal static Text Label(Transform parent, string name, int size, Color color,
            TextAnchor anchor = TextAnchor.MiddleCenter, FontStyle style = FontStyle.Normal)
            => HudPrimitives.Label(parent, name, size, color, anchor, style);

        internal static GameObject Pill(Transform parent, string name, Color color)
            => HudPrimitives.Pill(parent, name, color);

        internal static void Outline(GameObject go, Color color) => HudPrimitives.Outline(go, color);

        internal static void Place(Component c, float ax, float ay, float x, float y, float w, float h)
            => HudPrimitives.Place(c, ax, ay, x, y, w, h);

        internal static void Place(GameObject go, float ax, float ay, float x, float y, float w, float h)
            => HudPrimitives.Place(go, ax, ay, x, y, w, h);

        internal static void Stretch(RectTransform rt, float l, float t, float r, float b)
            => HudPrimitives.Stretch(rt, l, t, r, b);
    }

    /// <summary>A depleting HP bar: rounded track + horizontal fill that drains as the hider takes hits, colored
    /// green -> amber -> red, with a "{remaining} / {max} HP" caption. Shows REMAINING (counts DOWN).</summary>
    internal sealed class HpBar
    {
        private readonly GameObject _root;
        private readonly Image _fill;
        private readonly Text _label;
        private float _lastFill = -1f;
        private string _lastText;
        private Color _lastColor;

        internal GameObject Root => _root;

        internal HpBar(Transform parent)
        {
            _root = HudWidgets.Pill(parent, "hud_hp_track", Theme.WithAlpha(Theme.BgDeep, 0.8f));
            HudWidgets.Outline(_root, Theme.HairlineStrong);

            var fillGO = HudWidgets.Pill(_root.transform, "hud_hp_fill", Theme.Success);
            _fill = fillGO.GetComponent<Image>();
            _fill.type = Image.Type.Filled;
            _fill.fillMethod = Image.FillMethod.Horizontal;
            _fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            _fill.fillAmount = 1f;
            HudWidgets.Stretch(_fill.rectTransform, 2f, 2f, 2f, 2f);

            _label = HudWidgets.Label(_root.transform, "hud_hp_txt", Theme.Caption, Theme.TextPrimary, TextAnchor.MiddleCenter, FontStyle.Bold);
            HudWidgets.Stretch(_label.rectTransform, 0f, 0f, 0f, 0f);
        }

        internal void Set(int remaining, int max)
        {
            float f = max > 0 ? Mathf.Clamp01((float)remaining / max) : 0f;
            if (!Mathf.Approximately(f, _lastFill)) { _lastFill = f; _fill.fillAmount = f; }
            Color c = f > 0.5f ? Theme.Success : f > 0.25f ? Theme.Warning : Theme.Danger;
            if (c != _lastColor) { _lastColor = c; _fill.color = c; }
            string txt = $"{remaining} / {max} HP";
            if (txt != _lastText) { _lastText = txt; _label.text = txt; }
        }
    }

    /// <summary>A brief crosshair hitmarker: four short diagonal ticks around screen centre that flash for a moment
    /// then fade, confirming a shot connected. The SAME marker is used for every hit (decoy, disguised or bare
    /// player) - it is a pure local "you hit something" cue on the shooter's screen, no netcode.</summary>
    internal sealed class Hitmarker
    {
        private const float Dur = 0.22f;

        private readonly GameObject _root;
        private readonly CanvasGroup _cg;
        private float _start;

        internal GameObject Root => _root;

        internal Hitmarker(Transform parent)
        {
            _root = HudWidgets.Pill(parent, "hud_hitmarker", Theme.Clear);   // invisible container centred on the reticle
            _cg = _root.AddComponent<CanvasGroup>();
            _cg.blocksRaycasts = false; _cg.interactable = false;
            HudWidgets.Place(_root, 0.5f, 0.5f, 0f, 0f, 40f, 40f);
            // four ticks at the diagonals (top-left, top-right, bottom-left, bottom-right)
            MakeTick(-11f, 11f, -45f); MakeTick(11f, 11f, 45f); MakeTick(-11f, -11f, 45f); MakeTick(11f, -11f, -45f);
            _root.SetActive(false);
        }

        private void MakeTick(float x, float y, float rot)
        {
            var t = HudWidgets.Pill(_root.transform, "tick", Color.white);
            HudWidgets.Place(t, 0.5f, 0.5f, x, y, 11f, 3f);
            t.GetComponent<RectTransform>().localEulerAngles = new Vector3(0f, 0f, rot);
        }

        /// <summary>(Re)start the hitmarker flash at now = Time.unscaledTime.</summary>
        internal void Trigger(float now) { _start = now; if (!_root.activeSelf) _root.SetActive(true); }

        /// <summary>Advance the fade; auto-hides when the window elapses.</summary>
        internal void Tick(float now)
        {
            if (!_root.activeSelf) return;
            float age = now - _start;
            if (age >= Dur) { _root.SetActive(false); return; }
            _cg.alpha = Mathf.Clamp01(1f - age / Dur);
        }
    }
}
