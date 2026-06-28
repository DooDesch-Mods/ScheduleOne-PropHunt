#if DEBUG
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace PropHunt.Debug
{
    /// <summary>
    /// DEBUG-only in-game spawn-point authoring tool (toggle: phspawn console command). Cycles every safehouse
    /// property ([,]/[.] or [PgUp]/[PgDn] -> teleports the dev INTO it), then stamps a spawn point at the dev's
    /// current position + facing ([Enter]/[Insert]). Undo ([Backspace]), clear the current property ([Delete]),
    /// save ([F5]); exit with phspawn again (also saves). While active, ALL property doors are forced OPEN so the
    /// dev can walk through interiors freely to place points. Output: UserData/PropHunt/spawns.txt -> bake into
    /// Assets/spawns.txt. No PropHunt session required.
    /// </summary>
    internal static class SpawnEditor
    {
        private static bool _active;
        private static float _lastToggle = -999f;
        private static List<string> _codes;   // all loaded safehouse codes, sorted by grid area
        private static int _index;
        private static GUIStyle _header, _body, _key;

        internal static bool Active => _active;

        internal static void Toggle()
        {
            float now = Time.time;
            if (now - _lastToggle < 0.4f) return;   // SubmitCommand fires twice (same guard as PropCurator)
            _lastToggle = now;

            if (_active) { Game.SpawnStore.Save(); Exit(); return; }
            try
            {
                _codes = Game.SafehouseSelector.AvailableForPlayerCount(2);   // 2 = min -> every loaded map
                if (_codes == null || _codes.Count == 0)
                {
                    Core.Log.Warning("[PropHunt] phspawn: no safehouse properties loaded (load a save first).");
                    return;
                }
                _index = 0;
                _active = true;
                TeleportToCurrent();
                OpenAllDoors();
                Core.Log.Msg($"[PropHunt] spawn editor ON: {_codes.Count} properties.  " +
                             "[,/.] cycle  [Enter] place  [Backspace] undo  [Delete] clear  [F5] save  (phspawn = exit)");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phspawn enter failed: " + e.Message); Exit(); }
        }

        private static void Exit()
        {
            bool was = _active;
            _active = false;
            _codes = null;
            if (was) Core.Log.Msg("[PropHunt] spawn editor OFF (saved).");
        }

        internal static void Tick()
        {
            if (!_active) return;
            try
            {
                if (Input.GetKeyDown(KeyCode.Period) || Input.GetKeyDown(KeyCode.PageDown)) { Move(1); return; }
                if (Input.GetKeyDown(KeyCode.Comma) || Input.GetKeyDown(KeyCode.PageUp)) { Move(-1); return; }
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Insert)) { PlaceSpawn(); return; }
                if (Input.GetKeyDown(KeyCode.Backspace)) { UndoLast(); return; }
                if (Input.GetKeyDown(KeyCode.Delete)) { ClearCurrent(); return; }
                if (Input.GetKeyDown(KeyCode.F5)) { Game.SpawnStore.Save(); return; }
                // keep doors open while authoring (in case any auto-closed since the last teleport)
                if (Time.frameCount % 120 == 0) OpenAllDoors();
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phspawn tick failed: " + e.Message); Exit(); }
        }

        private static void Move(int d)
        {
            int n = _codes.Count;
            _index = ((_index + d) % n + n) % n;
            TeleportToCurrent();
            OpenAllDoors();
        }

        private static void PlaceSpawn()
        {
            string code = CurrentCode();
            if (string.IsNullOrEmpty(code)) return;
            var lp = Player.Local;
            if (lp == null) { Core.Log.Warning("[PropHunt] phspawn: Player.Local is null."); return; }
            var pos = lp.transform.position;
            float yaw = lp.transform.eulerAngles.y;
            Game.SpawnStore.Add(code, pos, yaw);
            var pts = Game.SpawnStore.GetSpawns(code);
            Core.Log.Msg($"[PropHunt] phspawn: placed point {(pts != null ? pts.Count : 0)} for '{code}' at ({pos.x:F2},{pos.y:F2},{pos.z:F2}) yaw={yaw:F0}");
        }

        private static void UndoLast()
        {
            string code = CurrentCode();
            var pts = code != null ? Game.SpawnStore.GetSpawns(code) : null;
            if (pts == null || pts.Count == 0) { Core.Log.Msg($"[PropHunt] phspawn: nothing to undo for '{code}'."); return; }
            pts.RemoveAt(pts.Count - 1);
            Core.Log.Msg($"[PropHunt] phspawn: undid last point for '{code}' ({pts.Count} left).");
        }

        private static void ClearCurrent()
        {
            string code = CurrentCode();
            if (string.IsNullOrEmpty(code)) return;
            Game.SpawnStore.ClearCode(code);
            Core.Log.Msg($"[PropHunt] phspawn: cleared all points for '{code}'.");
        }

        private static void TeleportToCurrent()
        {
            string code = CurrentCode();
            if (string.IsNullOrEmpty(code)) return;
            try
            {
                var prop = FindProperty(code);
                if (prop == null) { Core.Log.Warning($"[PropHunt] phspawn: '{code}' not found in scene."); return; }
                var t = prop.InteriorSpawnPoint != null ? prop.InteriorSpawnPoint : prop.SpawnPoint;
                if (t == null) { Core.Log.Warning($"[PropHunt] phspawn: '{code}' has no spawn transform."); return; }
                Game.RoundEnvironment.TeleportLocalTo(t.position + Vector3.up * 1f);   // +1m: vanilla teleport offset
                Core.Log.Msg($"[PropHunt] phspawn: teleported into '{code}' ({prop.PropertyName}).");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phspawn teleport failed: " + e.Message); }
        }

        // Force every property door OPEN so the dev can move through interiors freely while authoring.
        private static void OpenAllDoors()
        {
            try
            {
                var doors = UnityEngine.Object.FindObjectsOfType<Il2CppScheduleOne.Building.Doors.PropertyDoorController>();
                if (doors == null) return;
                for (int i = 0; i < doors.Length; i++)
                {
                    var d = doors[i];
                    if (d == null) continue;
                    d.PlayerAccess = Il2CppScheduleOne.Doors.EDoorAccess.Open;
                    try { d.SetIsOpen_Server(true, Il2CppScheduleOne.Doors.EDoorSide.Interior, false); } catch { }
                }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] phspawn OpenAllDoors failed: " + e.Message); }
        }

        private static Il2CppScheduleOne.Property.Property FindProperty(string code)
        {
            try
            {
                var props = Il2CppScheduleOne.Property.Property.Properties;
                if (props != null)
                    for (int i = 0; i < props.Count; i++)
                    { var p = props[i]; if (p != null && p.PropertyCode == code) return p; }
            }
            catch { }
            return null;
        }

        private static string CurrentCode() => (_codes != null && _index >= 0 && _index < _codes.Count) ? _codes[_index] : null;
        private static string CurrentName() { var p = FindProperty(CurrentCode()); return p != null ? p.PropertyName : (CurrentCode() ?? ""); }

        internal static void DrawGui()
        {
            if (!_active) return;
            if (_header == null)
            {
                _header = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 17, fontStyle = FontStyle.Bold };
                _body = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 14 };
                _key = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 12 };
            }
            string code = CurrentCode() ?? "";
            var pts = code.Length > 0 ? Game.SpawnStore.GetSpawns(code) : null;
            int count = pts != null ? pts.Count : 0;

            const float w = 680f, h = 122f;
            var box = new Rect((Screen.width - w) / 2f, 12f, w, h);
            GUI.Box(box, "PropHunt - Spawn Editor (doors forced open)");

            _header.normal.textColor = Color.green;
            GUI.Label(new Rect(box.x, box.y + 24f, w, 22f), $"[{_index + 1}/{(_codes != null ? _codes.Count : 0)}]  '{code}'  ({CurrentName()})", _header);
            _body.normal.textColor = Color.white;
            GUI.Label(new Rect(box.x, box.y + 50f, w, 20f), $"Authored points for this property: {count}", _body);
            _body.normal.textColor = new Color(0.7f, 0.7f, 0.7f);
            GUI.Label(new Rect(box.x, box.y + 72f, w, 18f), Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "PropHunt", "spawns.txt"), _body);
            _key.normal.textColor = Color.cyan;
            GUI.Label(new Rect(box.x, box.y + 96f, w, 18f), "[,/.] cycle    [Enter] place    [Backspace] undo    [Delete] clear    [F5] save    (phspawn = exit)", _key);
        }
    }
}
#endif
