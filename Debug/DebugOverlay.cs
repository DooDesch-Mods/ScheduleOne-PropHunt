#if DEBUG
using System.Collections.Generic;
using UnityEngine;
using PropHunt.Game;
using PropHunt.Disguise;
using PropHunt.Patches;
using PropHunt.View;
using PropHunt.Net;

namespace PropHunt.Debug
{
    /// <summary>
    /// DEBUG-only visual diagnostics overlay (toggle: F3, or the phdebug console command). Dumps the live
    /// PropHunt state - session, settings, net, the local player, the prop catalog, every player, decoys, and
    /// the interaction-suppression state - so problems (invisible disguise, blocked doors, wrong role, broken
    /// catalog) show their cause at a glance. Compiled out of Release. Reads only; never mutates game state.
    /// </summary>
    internal static class DebugOverlay
    {
        internal static bool Visible;
        private static float _lastToggle = -999f;

        // styling
        private static GUIStyle _style;
        private static Texture2D _bg;

        /// <summary>Toggle from the phdebug console command. Debounced because Console.SubmitCommand fires twice.</summary>
        internal static void ToggleFromConsole()
        {
            float now = Time.time;
            if (now - _lastToggle < 0.4f) return;
            _lastToggle = now;
            Visible = !Visible;
            Core.Log.Msg($"[PropHunt] debug overlay {(Visible ? "ON" : "OFF")} (F3 toggles).");
        }

        /// <summary>Called every frame from Core.OnUpdate (DEBUG). Hotkey toggle.</summary>
        internal static void Tick()
        {
            if (Input.GetKeyDown(KeyCode.F3)) Visible = !Visible;
        }

        internal static void DrawGui()
        {
            if (!Visible) return;
            EnsureStyle();
            var lines = new List<(string text, Color col)>();
            try { Build(lines); }
            catch (System.Exception e) { lines.Add(("overlay build failed: " + e.Message, Color.red)); }

            const float w = 440f, pad = 8f, lh = 15f;
            float h = pad * 2 + lines.Count * lh;
            var box = new Rect(6f, 6f, w, h);
            GUI.DrawTexture(box, _bg);
            for (int i = 0; i < lines.Count; i++)
            {
                _style.normal.textColor = lines[i].col;
                GUI.Label(new Rect(box.x + pad, box.y + pad + i * lh, w - pad * 2, lh + 2), lines[i].text, _style);
            }
        }

        // ---- content ----
        private static readonly Color H = new Color(1f, 0.85f, 0.3f);     // section header
        private static readonly Color K = new Color(0.7f, 0.85f, 1f);     // normal line
        private static readonly Color OK = new Color(0.6f, 1f, 0.6f);
        private static readonly Color BAD = new Color(1f, 0.5f, 0.4f);

        private static void Build(List<(string, Color)> L)
        {
            void Hdr(string s) => L.Add(("-- " + s + " --", H));
            void Row(string s) => L.Add((s, K));
            void Flag(string s, bool good) => L.Add((s, good ? OK : BAD));

            var ctl = Core.Session;
            L.Add(("PropHunt DEBUG  (F3 / phdebug to toggle)", Color.white));

            // SESSION
            Hdr("SESSION");
            if (ctl == null) { Flag("no active session", false); }
            else
            {
                Row($"host={ctl.IsHost}  phase={ctl.Phase}  round={ctl.State.RoundNumber}  left={ctl.SecondsLeft}s");
                Row($"roundActive={ctl.RoundActive}  winner={WinnerStr(ctl.State.Winner)}  cfgByForm={ctl.ConfiguredByHostForm}");

                // SETTINGS
                var s = ctl.Settings;
                if (s != null)
                {
                    Hdr("SETTINGS");
                    Row($"caught={s.Caught}  struct={s.Structure}  hide={s.HideSeconds}s  hunt={s.HuntSeconds}s");
                    Row($"pph={s.PlayersPerHunter}  hp/m={s.HitsToCatch}  maxChanges={s.MaxPropChanges}  decoys={s.MaxDecoys}  conc={s.ConcussCharges}");
                    Row($"area={(int)s.PlayAreaRadius}m  ff={(s.FriendlyFire ? "on" : "off")}  weapon={(string.IsNullOrEmpty(s.HunterWeapon) ? "none" : s.HunterWeapon)}");
                }
            }

            // NET
            Hdr("NET");
            Row($"ready={PropHuntNet.Ready}  inLobby={PropHuntNet.InLobby}  isHost={PropHuntNet.IsHost}  members={SafeMembers()}");
            Row($"localId={Short(PropHuntNet.LocalSteamId)}");

            // LOCAL PLAYER
            if (ctl != null)
            {
                Hdr("LOCAL");
                Row($"role={ctl.LocalRole}  prop={ctl.LocalPropId}{NameSuffix(ctl.LocalPropName)}  locked={ctl.LocalLocked}  3rdPerson={ctl.ThirdPersonOn}");
                Row($"hits={ctl.LocalHits}/{ctl.LocalMaxHits}  changes={ctl.LocalChanges}  decoysUsed={ctl.LocalDecoysUsed}  concUsed={ctl.LocalConcussUsed}  yaw={ctl.LocalPropYaw:F0}");
                Row($"look={(ctl.LookTargetName ?? "<none>")} (id {ctl.LookTargetId})  aimBecomable={ctl.LocalAimingBecomable}");
                if (ctl.LocalOutside) Flag($"OUTSIDE PLAY AREA  grace={ctl.OobGrace:F1}s", false);
                AppendController(L);
            }

            // CATALOG
            Hdr("CATALOG");
            int sh = ctl != null ? ctl.State.CatalogHash : 0;
            bool hashOk = ctl == null || sh == PropCatalog.Hash;
            L.Add(($"count={PropCatalog.Count}  hash={PropCatalog.Hash}  stateHash={sh}  match={hashOk}", hashOk ? OK : BAD));
            L.Add(($"curated={PropCatalog.Curated}  kept={PropCatalog.KeepCount()}", PropCatalog.Curated ? OK : BAD));
            if (!PropCatalog.Curated) Flag("heuristic mode: NO allowlist -> every prop offered (run phcurate)", false);

            // PLAYERS
            if (ctl != null && ctl.State != null)
            {
                Vector3 lpos = LocalPos();
                Hdr($"PLAYERS ({ctl.State.Players.Count})");
                foreach (var kv in ctl.State.Players)
                {
                    var p = kv.Value;
                    string pn = p.PropId >= 0 ? (PropCatalog.ById(p.PropId)?.Name ?? "?") : "-";
                    float dist = Dist(lpos, p.SteamId);
                    string ds = dist >= 0f ? $"{dist:F1}m" : "?";
                    var col = p.Eliminated ? BAD : K;
                    L.Add(($"  {Short(p.SteamId)} {p.Role} prop={p.PropId}({pn}) hp={p.Hits}/{p.MaxHits} elim={p.Eliminated} d={ds}", col));
                }

                // DECOYS
                Hdr($"DECOYS ({ctl.State.Decoys.Count})");
                for (int i = 0; i < ctl.State.Decoys.Count; i++)
                {
                    var d = ctl.State.Decoys[i];
                    string dn = PropCatalog.ById(d.PropId)?.Name ?? "?";
                    L.Add(($"  [{i}] prop={d.PropId}({dn}) hp={d.Hits}/{d.MaxHits} destroyed={d.Destroyed}", d.Destroyed ? BAD : K));
                }

                // INTERACTION (door diagnosis)
                Hdr("INTERACTION");
                bool suppressed = ctl.RoundActive && ctl.LocalAimingBecomable;
                L.Add(($"vanilla-suppressed={suppressed}  ({(suppressed ? "E becomes prop" : "doors/pickups work")})", suppressed ? K : OK));
            }
        }

        private static void AppendController(List<(string, Color)> L)
        {
            try
            {
                var pm = PlayerSingleton<PlayerMovement>.Instance;
                if (pm == null) { L.Add(("CC: <no PlayerMovement>", BAD)); return; }
                var cc = pm.Controller;
                float th = PropCollisionState.TargetHeight;
                if (cc != null)
                {
                    var c = cc.center;
                    L.Add(($"CC h={cc.height:F2} center=({c.x:F2},{c.y:F2},{c.z:F2}) r={cc.radius:F2}  crouch={pm.IsCrouched}  targetH={th:F2}", K));
                }
                else L.Add(($"CC: <null>  crouch={pm.IsCrouched}  targetH={th:F2}", BAD));
            }
            catch (System.Exception e) { L.Add(("CC read failed: " + e.Message, BAD)); }
        }

        private static Vector3 LocalPos()
        {
            try { var lp = Player.Local; if (lp != null) return lp.transform.position; } catch { }
            return Vector3.zero;
        }

        private static float Dist(Vector3 from, ulong steamId)
        {
            try
            {
                if (steamId == PropHuntNet.LocalSteamId) return 0f;
                var p = PlayerRegistry.Get(steamId);
                if (p != null && p.transform != null) return Vector3.Distance(from, p.transform.position);
            }
            catch { }
            return -1f;
        }

        private static int SafeMembers() { try { return PropHuntNet.MemberCount(); } catch { return -1; } }
        private static string WinnerStr(int w) => w == 0 ? "hunters" : w == 1 ? "hiders" : "-";
        private static string NameSuffix(string n) => string.IsNullOrEmpty(n) ? "(none)" : $"('{n}')";
        private static string Short(ulong id)
        {
            string s = id.ToString();
            return s.Length <= 5 ? s : ".." + s.Substring(s.Length - 5);
        }

        private static void EnsureStyle()
        {
            if (_style == null)
            {
                _style = new GUIStyle { fontSize = 12, fontStyle = FontStyle.Bold, richText = false };
                _style.normal.textColor = Color.white;
            }
            if (_bg == null)
            {
                _bg = new Texture2D(1, 1);
                _bg.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.72f));
                _bg.Apply();
                UnityEngine.Object.DontDestroyOnLoad(_bg);
            }
        }
    }
}
#endif
