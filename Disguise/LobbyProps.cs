using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace PropHunt.Disguise
{
    /// <summary>
    /// Reading and writing the lobby's prop assignments, the flat "steamId:propId:yaw" list carried on
    /// <see cref="Game.GameState.LobbyProps"/>.
    ///
    /// Kept as text rather than a dictionary on the state because the state travels as one blob and every other
    /// per-player field already does the same - one format to reason about, and an older build that does not know this
    /// field simply ignores it instead of failing to parse the whole snapshot.
    /// </summary>
    internal static class LobbyPropCodec
    {
        internal struct Worn
        {
            internal int PropId;
            internal float Yaw;
        }

        internal static Dictionary<ulong, Worn> Parse(string blob)
        {
            var map = new Dictionary<ulong, Worn>();
            if (string.IsNullOrEmpty(blob)) return map;
            var ci = CultureInfo.InvariantCulture;
            foreach (var part in blob.Split(','))
            {
                if (string.IsNullOrEmpty(part)) continue;
                var f = part.Split(':');
                if (f.Length < 2) continue;
                if (!ulong.TryParse(f[0], NumberStyles.Integer, ci, out var id)) continue;
                if (!int.TryParse(f[1], NumberStyles.Integer, ci, out var prop)) continue;
                float yaw = 0f;
                if (f.Length >= 3) float.TryParse(f[2], NumberStyles.Float, ci, out yaw);
                map[id] = new Worn { PropId = prop, Yaw = yaw };
            }
            return map;
        }

        internal static string Serialize(Dictionary<ulong, Worn> map)
        {
            if (map == null || map.Count == 0) return "";
            var ci = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            foreach (var kv in map)
            {
                if (kv.Value.PropId < 0) continue;   // "not wearing one" is an absent entry, never a -1 row
                if (sb.Length > 0) sb.Append(',');
                sb.Append(kv.Key.ToString(ci)).Append(':')
                  .Append(kv.Value.PropId.ToString(ci)).Append(':')
                  .Append(kv.Value.Yaw.ToString("F1", ci));
            }
            return sb.ToString();
        }
    }
}
