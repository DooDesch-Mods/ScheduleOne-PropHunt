using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using S1API.UI;
using DooDesch.UI;
using PropHunt.Game;

namespace PropHunt.UI.Hud
{
    /// <summary>
    /// The round-end results card: winner headline + subtitle, a row of award cards, and a per-player leaderboard
    /// with row striping, role chips, right-aligned stats and the local player highlighted. Shown only in RoundEnd;
    /// the content is rebuilt only when a compact signature changes (not every frame).
    /// </summary>
    internal sealed class Scoreboard
    {
        private const float CardWidth = 720f;
        private const float Inset = 30f;
        private const float ContentW = CardWidth - Inset * 2f;   // 660
        private const float RowH = 30f;

        // leaderboard columns (x within the content area; numeric columns are right-aligned to x+w)
        private const float CRank = 0f, WRank = 36f;
        private const float CName = 44f, WName = 220f;
        private const float CRole = 270f, WRole = 96f;
        private const float CCatch = 374f, WCatch = 86f;
        private const float CSurv = 466f, WSurv = 92f;
        private const float CScore = 564f, WScore = 76f;   // ends at 640 within the 660 content -> ~20px right margin

        private readonly GameObject _card;
        private readonly GameObject _content;
        private bool _active;
        private string _sig;

        internal Scoreboard(Transform parent)
        {
            _card = HudWidgets.Pill(parent, "hud_scoreboard", Theme.WithAlpha(Theme.BgDeep, 0.94f));
            HudWidgets.Outline(_card, Theme.HairlineStrong);
            HudWidgets.Place(_card, 0.5f, 0.5f, 0f, 0f, CardWidth, 480f);
            _content = UIFactory.Panel("content", _card.transform, Theme.Clear);
            _content.GetComponent<Image>().raycastTarget = false;
            HudWidgets.Stretch(_content.GetComponent<RectTransform>(), Inset, 26f, Inset, 26f);
            _card.SetActive(false);
        }

        internal void Apply(GameModeController ctl)
        {
            if (ctl.Phase != RoundPhase.RoundEnd)
            {
                if (_active) { _active = false; _card.SetActive(false); }
                return;
            }
            var st = ctl.State;
            string sig = $"{st.Winner}|{st.RoundNumber}|{st.Players.Count}";
            if (!_active) { _active = true; _card.SetActive(true); }
            if (sig != _sig) { _sig = sig; Rebuild(st, ctl.LocalId); }
        }

        private void Rebuild(GameState st, ulong localId)
        {
            UIFactory.ClearChildren(_content.transform);
            try { PlayerRegistry.Refresh(); } catch { }

            var players = new List<PlayerState>(st.Players.Values);
            players.Sort((a, b) => b.SessScore.CompareTo(a.SessScore));
            var awards = BuildAwards(players);

            float y = 0f;

            // --- headline + subtitle ---
            string headline = st.Winner == 0 ? "HUNTERS WIN" : st.Winner == 1 ? "HIDERS WIN" : "ROUND OVER";
            Color hc = st.Winner == 0 ? Theme.DangerText : st.Winner == 1 ? Theme.SuccessText : Theme.TextPrimary;
            var head = HudWidgets.Label(_content.transform, "headline", Theme.H1, hc, TextAnchor.UpperCenter, FontStyle.Bold);
            FullRow(head, y, 38f); y += 42f;
            var sub = HudWidgets.Label(_content.transform, "sub", Theme.Caption, Theme.TextMuted, TextAnchor.UpperCenter, FontStyle.Bold);
            sub.text = $"ROUND {st.RoundNumber}   -   {players.Count} PLAYERS";
            FullRow(sub, y, 16f); y += 26f;

            // --- award cards (horizontal row) ---
            if (awards.Count > 0)
            {
                const float gap = 10f, cardH = 58f;
                float cw = (ContentW - gap * (awards.Count - 1)) / awards.Count;
                for (int i = 0; i < awards.Count; i++)
                {
                    var a = awards[i];
                    var c = HudWidgets.Pill(_content.transform, "award", Theme.WithAlpha(Theme.WarningSubtle, 0.55f));
                    HudWidgets.Outline(c, Theme.WithAlpha(Theme.Warning, 0.4f));
                    TopLeft(c, i * (cw + gap), y, cw, cardH);
                    Cell(c.transform, "t", a.Title.ToUpperInvariant(), Theme.WarningText, Theme.Caption, TextAnchor.UpperCenter, FontStyle.Bold, 8f, 6f, cw - 16f, 14f);
                    Cell(c.transform, "n", a.Name, Theme.TextPrimary, Theme.Label, TextAnchor.UpperCenter, FontStyle.Bold, 8f, 22f, cw - 16f, 20f);
                    Cell(c.transform, "m", a.Metric, Theme.TextMuted, Theme.Caption, TextAnchor.UpperCenter, FontStyle.Normal, 8f, 40f, cw - 16f, 14f);
                }
                y += cardH + 14f;
            }

            // --- leaderboard header ---
            var hbar = HudWidgets.Pill(_content.transform, "lb_head", Theme.WithAlpha(Theme.BgElevated, 0.6f));
            TopLeft(hbar, 0f, y, ContentW, RowH);
            HeaderCell("Player", CName, WName, TextAnchor.MiddleLeft, y);
            HeaderCell("Role", CRole, WRole, TextAnchor.MiddleLeft, y);
            HeaderCell("Catches", CCatch, WCatch, TextAnchor.MiddleRight, y);
            HeaderCell("Survived", CSurv, WSurv, TextAnchor.MiddleRight, y);
            HeaderCell("Score", CScore, WScore, TextAnchor.MiddleRight, y);
            y += RowH + 4f;

            // --- leaderboard rows ---
            int rank = 1;
            foreach (var p in players)
            {
                bool isLocal = p.SteamId == localId;
                var bg = HudWidgets.Pill(_content.transform, "row", isLocal ? Theme.AccentSubtle : Theme.WithAlpha(Theme.BgElevated, (rank % 2 == 0) ? 0.32f : 0.16f));
                if (isLocal) HudWidgets.Outline(bg, Theme.WithAlpha(Theme.AccentBorder, 0.7f));
                TopLeft(bg, 0f, y, ContentW, RowH - 2f);

                Color nameColor = isLocal ? Theme.TextPrimary : Theme.TextPrimary;
                Cell(_content.transform, "rank", rank.ToString(), Theme.TextMuted, Theme.Body, TextAnchor.MiddleCenter, FontStyle.Bold, CRank, y, WRank, RowH);
                Cell(_content.transform, "name", NameOf(p) + (isLocal ? "  (you)" : ""), nameColor, Theme.Body, TextAnchor.MiddleLeft, isLocal ? FontStyle.Bold : FontStyle.Normal, CName, y, WName, RowH);
                RoleChip(p, CRole, y);
                Cell(_content.transform, "catch", p.CatchesMade.ToString(), Theme.TextMuted, Theme.Body, TextAnchor.MiddleRight, FontStyle.Normal, CCatch, y, WCatch, RowH);
                Cell(_content.transform, "surv", p.SurvivedSeconds + "s", Theme.TextMuted, Theme.Body, TextAnchor.MiddleRight, FontStyle.Normal, CSurv, y, WSurv, RowH);
                Cell(_content.transform, "score", p.SessScore.ToString(), Theme.TextPrimary, Theme.Body, TextAnchor.MiddleRight, FontStyle.Bold, CScore, y, WScore, RowH);
                y += RowH; rank++;
            }

            // size the card to its content
            float h = y + 26f + 26f;
            var rt = _card.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(CardWidth, Mathf.Min(h, Screen.height - 40f));
        }

        // ---- cell / chip / layout helpers ----
        private void Cell(Transform parent, string name, string text, Color color, int size, TextAnchor anchor, FontStyle style, float x, float y, float w, float h)
        {
            var t = HudWidgets.Label(parent, name, size, color, anchor, style);
            t.text = text;
            var rt = t.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
        }

        private void HeaderCell(string text, float x, float w, TextAnchor anchor, float y)
            => Cell(_content.transform, "h", text, Theme.InfoText, Theme.Caption, anchor, FontStyle.Bold, x, y, w, RowH);

        private void RoleChip(PlayerState p, float x, float y)
        {
            string txt = RoleShort(p);
            Color col = RoleColor(p);
            var chip = HudWidgets.Pill(_content.transform, "rolechip", Theme.WithAlpha(col, 0.18f));
            // chip sits centered vertically in the row, left-aligned in the role column
            var crt = chip.GetComponent<RectTransform>();
            crt.anchorMin = new Vector2(0f, 1f); crt.anchorMax = new Vector2(0f, 1f); crt.pivot = new Vector2(0f, 1f);
            crt.anchoredPosition = new Vector2(x, -(y + 4f)); crt.sizeDelta = new Vector2(WRole - 8f, RowH - 10f);
            var t = HudWidgets.Label(chip.transform, "txt", Theme.Caption, col, TextAnchor.MiddleCenter, FontStyle.Bold);
            t.text = txt;
            HudWidgets.Stretch(t.rectTransform, 4f, 0f, 4f, 0f);
        }

        private static void TopLeft(GameObject go, float x, float y, float w, float h)
        {
            var rt = go.GetComponent<RectTransform>();
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y); rt.sizeDelta = new Vector2(w, h);
        }

        private void FullRow(Text t, float y, float h)
        {
            var rt = t.rectTransform;
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(0f, 0f); rt.offsetMax = new Vector2(0f, 0f);
            rt.anchoredPosition = new Vector2(0f, -y); rt.sizeDelta = new Vector2(0f, h);
        }

        // ---- award / name / role logic ----
        private struct Award { public string Title, Name, Metric; }

        private static List<Award> BuildAwards(List<PlayerState> players)
        {
            var list = new List<Award>();
            AddAward(list, players, "Top Hunter", p => p.CatchesMade, v => $"{v} catches");
            AddAward(list, players, "Survivor", p => p.Role == PlayerRole.Hider ? p.SurvivedSeconds : 0, v => $"{v}s alive");
            AddAward(list, players, "Trickster", p => p.DecoyBaits, v => $"{v} decoy baits");
            AddAward(list, players, "Shocker", p => p.StunsLanded, v => $"{v} stuns");
            return list;
        }

        private static void AddAward(List<Award> list, List<PlayerState> players, string title, Func<PlayerState, int> sel, Func<int, string> fmt)
        {
            PlayerState best = null; int bv = 0;
            foreach (var p in players) { int v = sel(p); if (v > bv) { bv = v; best = p; } }
            if (best != null && bv > 0) list.Add(new Award { Title = title, Name = NameOf(best), Metric = fmt(bv) });
        }

        private static string NameOf(PlayerState p)
        {
            try { var gp = PlayerRegistry.Get(p.SteamId); if (gp != null) { var n = gp.PlayerName; if (!string.IsNullOrEmpty(n)) return n; } }
            catch { }
            return "Player " + (p.SteamId % 10000);
        }

        private static string RoleShort(PlayerState p)
        {
            if (p.Role == PlayerRole.Hunter) return "Hunter";
            if (p.Role == PlayerRole.Hider) return p.Eliminated ? "Caught" : "Hider";
            return "Spectator";
        }

        private static Color RoleColor(PlayerState p)
        {
            if (p.Role == PlayerRole.Hunter) return Theme.DangerText;
            if (p.Role == PlayerRole.Hider) return p.Eliminated ? Theme.TextMuted : Theme.SuccessText;
            return Theme.InfoText;
        }
    }
}
