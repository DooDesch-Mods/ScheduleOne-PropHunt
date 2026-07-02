using System;
using UnityEngine;
using UnityEngine.UI;
using S1API.PhoneApp;
using S1API.UI;
using DooDesch.UI;
using PropHunt.Game;

namespace PropHunt.Phone
{
    /// <summary>
    /// The in-game PropHunt phone app: the host's full control surface and a live, read-only tracker for everyone
    /// else. S1API auto-discovers + instantiates PhoneApp subclasses when the gameplay-scene phone home screen
    /// starts, so this needs only a parameterless ctor. The chrome (status header + Match/Players/Settings/Stats
    /// tab bar + content region) is built once in <see cref="OnCreatedUI"/>; <see cref="Tick"/> (driven each frame
    /// by Core.OnUpdate, only while the app is open) refreshes the header cheaply and rebuilds the active tab body
    /// when the synced state changes. All controls/data come from <see cref="GameModeController.Active"/>.
    /// </summary>
    public class PropHuntPhoneApp : PhoneApp
    {
        internal static PropHuntPhoneApp Instance { get; private set; }

        protected override string AppName => "PropHuntApp";
        protected override string AppTitle => "PropHunt";
        protected override string IconLabel => "PropHunt";
        // Optional Mods-folder icon file; only used if no embedded icon is found and S1API otherwise keeps the template.
        protected override string IconFileName => "PropHunt.png";

        // Branded icon: an embedded "Assets/phone_icon.png" if present (LogicalName PropHunt.Assets.phone_icon.png).
        // Until that art asset is added the stream is null -> S1API falls back to IconFileName / the template icon.
        private static Sprite _icon;
        private static bool _iconAbsent;   // set only if the embedded resource genuinely isn't there (stop retrying)
        protected override Sprite IconSprite
        {
            // Re-load on demand, not once: a scene unload (quit to menu -> re-host) can sweep the icon's texture (an
            // un-rooted asset across the IL2CPP boundary), leaving _icon a DESTROYED sprite. Unity's '== null' is true
            // for a destroyed object, so this rebuilds it on the next open instead of falling back to the template icon.
            get
            {
                if (_icon != null) return _icon;
                if (_iconAbsent) return null;
                _icon = LoadEmbeddedIcon();
                if (_icon == null) _iconAbsent = true;
                return _icon;
            }
        }

        private static Sprite LoadEmbeddedIcon()
        {
            try
            {
                using (var s = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("PropHunt.Assets.phone_icon.png"))
                {
                    if (s == null) return null;
                    var bytes = new byte[s.Length];
                    int read = 0;
                    while (read < bytes.Length) { int n = s.Read(bytes, read, bytes.Length - read); if (n <= 0) break; read += n; }
                    var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear };
                    tex.hideFlags = HideFlags.DontUnloadUnusedAsset;   // survive scene unloads so a re-host keeps the icon
                    if (tex.LoadImage(bytes))
                    {
                        var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
                        if (sp != null) sp.hideFlags = HideFlags.DontUnloadUnusedAsset;
                        return sp;
                    }
                    return null;
                }
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] phone icon load failed: " + e.Message); return null; }
        }

        private Text _title;
        private Text _status;
        private Text _badge;
        private GameObject _contentRegion;   // fixed region below the tab bar; the rebuilt body lives inside it
        private GameObject _body;            // current tab's content (recreated on rebuild to avoid flicker)
        private Transform _dialogRoot;       // full-screen root for modal dialogs (ConfirmDialog)
        private GameObject _container;       // the S1API app container, kept for the one-time active rebuild
        private bool _activated;             // have we rebuilt the UI once while the app was open + active?
        private Button[] _tabButtons;        // the tab bar buttons (recolored on tab switch)
        private int _tab = PhoneScreens.TabMatch;
        private string _lastSig;

        protected override void OnCreatedUI(GameObject container)
        {
            Instance = this;
            _container = container;
            try { BuildUI(container); }
            catch (Exception e) { Core.Log.Warning("[PropHunt] phone app build failed: " + e.Message); }
        }

        // Create a panel filling a vertical BAND of its parent (full width, y in [y0,y1]) with its offsets zeroed so it
        // fits the band EXACTLY. UIFactory.Panel(anchorMin,anchorMax) leaves the default sizeDelta=(100,100), which
        // overflowed each band by 50px on every side - the oversized opaque content panel then covered the tab labels.
        private static GameObject Band(string name, Transform parent, Color color, float y0, float y1)
        {
            var p = UIFactory.Panel(name, parent, color, new Vector2(0f, y0), new Vector2(1f, y1));
            var rt = p.GetComponent<RectTransform>();
            rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
            return p;
        }

        private void BuildUI(GameObject container)
        {
            // OPAQUE background filling the whole container edge-to-edge - matches the native apps' "Background" child
            // (measured: full 0..1) so nothing shows through the phone.
            var root = UIFactory.Panel("ph_root", container.transform, Theme.BgBase, fullAnchor: true);
            root.GetComponent<RectTransform>().localScale = Vector3.one;   // guard: a cloned-template scale must not carry over
            _dialogRoot = root.transform;

            // CONTENT safe-area inset INSIDE the full background (the background still covers the edges - no see-through).
            // The native apps reserve ~60px at the top + a few px on the sides for the phone chrome and never hug the
            // frame; a small uniform ~3% inset gives that breathing room without the oversized margin the 7% had.
            var safe = UIFactory.Panel("ph_safe", root.transform, Theme.Clear, fullAnchor: true);
            var safeRT = safe.GetComponent<RectTransform>();
            float cw = container.GetComponent<RectTransform>() != null ? container.GetComponent<RectTransform>().rect.width : 0f;
            if (cw > 1f) { float m = cw * 0.012f; safeRT.offsetMin = new Vector2(m, m); safeRT.offsetMax = new Vector2(-m, -m); }
            var host = safe.transform;

            // --- status header (always-updated phase / timer / role) ---
            var header = Band("ph_header", host, Theme.BgPanel, 0.9f, 1f);
            _title = UIFactory.Text("ph_title", "PropHunt", header.transform, Theme.H3, TextAnchor.MiddleLeft, FontStyle.Bold);
            _title.color = Theme.Accent; AnchorText(_title, new Vector2(0, 0), new Vector2(0.32f, 1), 12f);
            _status = UIFactory.Text("ph_status", "", header.transform, Theme.H3, TextAnchor.MiddleCenter, FontStyle.Bold);
            _status.color = Theme.TextPrimary; AnchorText(_status, new Vector2(0.32f, 0), new Vector2(0.74f, 1), 4f);
            _badge = UIFactory.Text("ph_badge", "", header.transform, Theme.Caption, TextAnchor.MiddleRight, FontStyle.Bold);
            _badge.color = Theme.TextMuted; AnchorText(_badge, new Vector2(0.74f, 0), new Vector2(1, 1), 12f);

            // --- tab bar (anchor-based buttons, NOT a layout group: layout groups don't size children while the app
            //     container is inactive at build time, which left the tab labels at 0x0 and invisible). ---
            var tabBar = Band("ph_tabs", host, Theme.BgBase, 0.82f, 0.9f);
            _tabButtons = BuildTabs(tabBar.transform);

            // --- content region (the tab body is rebuilt inside this) ---
            _contentRegion = Band("ph_content", host, Theme.BgBase, 0f, 0.82f);

            RebuildTab();
            RefreshHeader();

            // S1API builds OnCreatedUI while the container is INACTIVE, so uGUI Text meshes (tab labels, header) can be
            // generated at size 0 and never re-render. Force an immediate layout pass + mark every Text dirty so they
            // regenerate at their real size and actually appear.
            try
            {
                Canvas.ForceUpdateCanvases();
                LayoutRebuilder.ForceRebuildLayoutImmediate(root.GetComponent<RectTransform>());
                var texts = root.GetComponentsInChildren<Text>(true);
                if (texts != null) foreach (var tx in texts) if (tx != null) tx.SetAllDirty();
            }
            catch { }
        }

        /// <summary>Per-frame hook (Core.OnUpdate): refresh only while the app is actually open.</summary>
        internal void Tick()
        {
            try
            {
                if (!IsOpen()) return;
                // S1API builds OnCreatedUI while the app container is INACTIVE; uGUI Text meshes built then don't
                // always appear once the app is shown (the body looks fine only because it gets rebuilt live). Rebuild
                // the whole UI once now that the app is open + active, so the header text + tab labels actually render.
                if (!_activated)
                {
                    _activated = true;
                    if (_container != null) { try { UIFactory.ClearChildren(_container.transform); BuildUI(_container); } catch (Exception e) { Core.Log.Warning("[PropHunt] phone UI rebuild failed: " + e.Message); } }
                    // The player opened the app -> the "open the PropHunt app" guide quest is fulfilled.
                    try { PropHunt.Quests.GuideQuest.OnAppOpened(); } catch { }
                }
                RefreshHeader();
                string sig = Signature();
                if (sig != _lastSig) { _lastSig = sig; RebuildTab(); }
                SmoothScroll.Tick(PhoneCamera());   // glide the active list (pass the phone's worldspace render camera)
            }
            catch { }
        }

        private void SwitchTab(int tab)
        {
            if (tab == _tab) return;
            _tab = tab;
            RecolorTabs();
            _lastSig = null;   // force a rebuild for the new tab
            RebuildTab();
        }

        // The tab bar: one button per tab, sized by ANCHORS (each takes 1/n of the bar width) rather than a layout
        // group, so the labels get a real rect even though S1API builds the UI while the container is inactive.
        private Button[] BuildTabs(Transform tabBar)
        {
            int n = PhoneScreens.TabLabels.Length;
            var btns = new Button[n];
            for (int i = 0; i < n; i++)
            {
                int idx = i;
                var (go, btn, txt) = UIFactory.ButtonWithLabel("seg_" + i, PhoneScreens.TabLabels[i], tabBar, i == _tab ? Theme.Accent : Theme.Button, 0, 0);
                var rt = go.GetComponent<RectTransform>();
                rt.anchorMin = new Vector2((float)i / n, 0f); rt.anchorMax = new Vector2((float)(i + 1) / n, 1f);
                rt.offsetMin = new Vector2(i == 0 ? 0f : 3f, 4f); rt.offsetMax = new Vector2(i == n - 1 ? 0f : -3f, -4f);
                var img = go.GetComponent<Image>(); if (img != null) { img.sprite = Theme.RoundedSprite(); img.type = Image.Type.Sliced; }
                if (txt != null) { txt.fontSize = Theme.Label; txt.color = Theme.TextPrimary; txt.horizontalOverflow = HorizontalWrapMode.Overflow; }
                btn.onClick.AddListener((UnityEngine.Events.UnityAction)(() => SwitchTab(idx)));
                btns[i] = btn;
            }
            return btns;
        }

        private void RecolorTabs()
        {
            if (_tabButtons == null) return;
            for (int i = 0; i < _tabButtons.Length; i++)
            {
                if (_tabButtons[i] == null) continue;
                var img = _tabButtons[i].GetComponent<Image>(); if (img == null) img = _tabButtons[i].targetGraphic as Image;
                if (img != null) img.color = i == _tab ? Theme.Accent : Theme.Button;
            }
        }

        private void RefreshHeader()
        {
            if (_status == null) return;
            var ctl = GameModeController.Active;
            if (ctl == null) { _status.text = "No active match"; if (_badge != null) _badge.text = ""; return; }
            int secs = ctl.SecondsLeft;
            _status.text = secs > 0 ? $"{ctl.Phase}  -  {secs}s" : ctl.Phase.ToString();
            if (_badge != null) _badge.text = ctl.IsHost ? "HOST" : "VIEW ONLY";
            if (_badge != null) _badge.color = ctl.IsHost ? Theme.Accent : Theme.WarningText;
        }

        private void RebuildTab()
        {
            if (_contentRegion == null) return;
            SmoothScroll.Clear();   // forget the old tab's scroller; PhoneScreens re-attaches the new list's
            var prev = _body;
            _body = UIFactory.Panel("ph_body", _contentRegion.transform, Theme.BgBase, fullAnchor: true);

            var ctl = GameModeController.Active;
            if (ctl == null) BuildEmptyState(_body.transform);
            else { try { PhoneScreens.Build(_body.transform, ctl, _tab, ctl.IsHost, _dialogRoot); } catch (Exception e) { Core.Log.Warning("[PropHunt] phone tab build failed: " + e.Message); } }

            _lastSig = Signature();
            if (prev != null) UnityEngine.Object.Destroy(prev);
        }

        private static void BuildEmptyState(Transform parent)
        {
            var t = UIFactory.Text("empty", "No PropHunt match active.\n\nLaunch one from the main menu:\nSide Hustle  ->  PropHunt.",
                parent, Theme.Body, TextAnchor.MiddleCenter);
            t.color = Theme.TextMuted; t.raycastTarget = false;
            t.rectTransform.anchorMin = Vector2.zero; t.rectTransform.anchorMax = Vector2.one;
            t.rectTransform.offsetMin = new Vector2(16, 16); t.rectTransform.offsetMax = new Vector2(-16, -16);
        }

        // A compact fingerprint of everything the active tab renders; when it changes, the tab is rebuilt. The live
        // tabs (Players/Stats) fold in a 1-second bucket so they refresh continuously; Match/Settings rebuild only on
        // a real state change (so the host can edit a setting without the control resetting under their finger).
        private string Signature()
        {
            var ctl = GameModeController.Active;
            if (ctl == null) return "none|" + _tab;
            var st = ctl.State;
            string s = $"{_tab}|{st.Phase}|{st.RoundNumber}|{st.Winner}|{st.Players.Count}|{ctl.AliveHiderCount}|{ctl.IsHost}|{st.SafehouseCode}|{(st.SafehouseReady ? 1 : 0)}";
            if (_tab == PhoneScreens.TabPlayers || _tab == PhoneScreens.TabStats) s += "|" + Mathf.FloorToInt(Time.unscaledTime);
            // Client's (read-only) Settings tab: refresh when the host's synced settings change. NOT for the host -
            // their Settings tab is being edited and must not rebuild mid-edit (it would reset the control under them).
            if (_tab == PhoneScreens.TabSettings && !ctl.IsHost) s += "|" + (st.SettingsBlob ?? "");
            return s;
        }

        // The camera that renders the worldspace phone screen - used by SmoothScroll's hover test (a null camera
        // fails RectangleContainsScreenPoint for a worldspace canvas, which is why the list wouldn't wheel-scroll).
        private static Camera PhoneCamera()
        {
            try { var gm = Singleton<GameplayMenu>.Instance; return gm != null ? gm.OverlayCamera : null; }
            catch { return null; }
        }

        private static void AnchorText(Text t, Vector2 min, Vector2 max, float inset)
        {
            var rt = t.rectTransform;
            rt.anchorMin = min; rt.anchorMax = max; rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(inset, 0); rt.offsetMax = new Vector2(-inset, 0);
        }
    }
}
