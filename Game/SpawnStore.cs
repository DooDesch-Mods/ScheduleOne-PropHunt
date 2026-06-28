using System.Collections.Generic;
using System.IO;

namespace PropHunt.Game
{
    /// <summary>
    /// Authored interior spawn points per safehouse property code. File format (one entry per line):
    /// "code|x|y|z|yaw". Load order mirrors <see cref="Disguise.PropCatalog"/>'s curation: a dev's
    /// UserData/PropHunt/spawns.txt overrides the embedded shipped PropHunt.Assets.spawns.txt (the
    /// source of truth for all players); absent both -> empty (the safehouse teleport falls back to the
    /// InteriorSpawnPoint ring). Authored in-game with the phspawn editor, then baked into the project.
    /// </summary>
    internal static class SpawnStore
    {
        internal readonly struct SpawnPoint
        {
            internal readonly UnityEngine.Vector3 Pos;
            internal readonly float Yaw;
            internal SpawnPoint(UnityEngine.Vector3 pos, float yaw) { Pos = pos; Yaw = yaw; }
        }

        private static readonly Dictionary<string, List<SpawnPoint>> _spawns = new Dictionary<string, List<SpawnPoint>>();
        private static bool _loaded;

        private static string SpawnsPath =>
            Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "PropHunt", "spawns.txt");

        // ---- public API ----

        internal static bool HasSpawns(string code)
        {
            if (!_loaded) Load();
            return !string.IsNullOrEmpty(code) && _spawns.TryGetValue(code, out var l) && l.Count > 0;
        }

        internal static List<SpawnPoint> GetSpawns(string code)
        {
            if (!_loaded) Load();
            return _spawns.TryGetValue(code, out var l) ? l : null;
        }

        internal static void Add(string code, UnityEngine.Vector3 pos, float yaw)
        {
            if (!_loaded) Load();
            if (!_spawns.TryGetValue(code, out var l)) { l = new List<SpawnPoint>(); _spawns[code] = l; }
            l.Add(new SpawnPoint(pos, yaw));
        }

        internal static void ClearCode(string code)
        {
            if (!_loaded) Load();
            _spawns.Remove(code);
        }

        internal static void Save()
        {
            try
            {
                string path = SpawnsPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine("# PropHunt safehouse spawn points. Format: code|x|y|z|yaw");
                sb.AppendLine("# Authored with the phspawn in-game editor. Copy to Assets/spawns.txt to bake in.");
                foreach (var kv in _spawns)
                    foreach (var sp in kv.Value)
                        sb.Append(kv.Key).Append('|')
                          .Append(sp.Pos.x.ToString("F3", ci)).Append('|')
                          .Append(sp.Pos.y.ToString("F3", ci)).Append('|')
                          .Append(sp.Pos.z.ToString("F3", ci)).Append('|')
                          .Append(sp.Yaw.ToString("F1", ci)).Append('\n');
                File.WriteAllText(path, sb.ToString());
                Core.Log.Msg($"[PropHunt] spawns saved -> {path}");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] SpawnStore.Save failed: " + e.Message); }
        }

        internal static void Reload() { _loaded = false; _spawns.Clear(); Load(); }

        // ---- persistence (mirrors PropCatalog.LoadCuration / ReadShippedCuration) ----

        private static void Load()
        {
            _loaded = true;
            _spawns.Clear();
            try
            {
                string path = SpawnsPath;
                string[] lines; string source;
                if (File.Exists(path)) { lines = File.ReadAllLines(path); source = "user"; }
                else { lines = ReadShipped(); source = "shipped"; }
                if (lines == null) { Core.LogDebug("[PropHunt] spawns: no file found - InteriorSpawnPoint ring fallback active."); return; }
                Parse(lines);
                int total = 0; foreach (var l in _spawns.Values) total += l.Count;
                Core.LogDebug($"[PropHunt] spawns loaded ({source}): {_spawns.Count} properties, {total} points.");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] SpawnStore.Load failed: " + e.Message); }
        }

        private static void Parse(string[] lines)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var ns = System.Globalization.NumberStyles.Float;
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var p = line.Split('|');
                if (p.Length < 5) continue;
                string code = p[0].Trim();
                if (string.IsNullOrEmpty(code)) continue;
                if (!float.TryParse(p[1], ns, ci, out float x)) continue;
                if (!float.TryParse(p[2], ns, ci, out float y)) continue;
                if (!float.TryParse(p[3], ns, ci, out float z)) continue;
                if (!float.TryParse(p[4], ns, ci, out float yaw)) continue;
                if (!_spawns.TryGetValue(code, out var l)) { l = new List<SpawnPoint>(); _spawns[code] = l; }
                l.Add(new SpawnPoint(new UnityEngine.Vector3(x, y, z), yaw));
            }
        }

        // Mirror of PropCatalog.ReadShippedCuration.
        private static string[] ReadShipped()
        {
            try
            {
                var asm = System.Reflection.Assembly.GetExecutingAssembly();
                using (var s = asm.GetManifestResourceStream("PropHunt.Assets.spawns.txt"))
                {
                    if (s == null) return null;
                    using (var r = new StreamReader(s))
                        return r.ReadToEnd().Split('\n');
                }
            }
            catch { return null; }
        }
    }
}
