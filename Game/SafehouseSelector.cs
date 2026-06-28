using System.Collections.Generic;
using Il2CppScheduleOne.Property;

namespace PropHunt.Game
{
    /// <summary>
    /// Safehouse selection from the live vanilla property registry. Real codes + grid areas (measured via the
    /// phsafehouse probe): rv 32, laundromat 44, carwash 55, postoffice 60, tacoticklers 90, motelroom 120,
    /// storageunit 190, sweatshop 216, seweroffice 256, bungalow 432, barn 1264, dockswarehouse 1624, manor 1904.
    ///
    /// Two surfaces:
    ///  - <see cref="SelectForPlayerCount"/> = the AUTO default: a curated, size-ordered list of real living spaces
    ///    (no RV / no laundering-front businesses), scaled by lobby size (2 -> motel, 12 -> barn, 16 -> manor).
    ///  - <see cref="AvailableForPlayerCount"/> = what the host may MANUALLY switch to: EVERY loaded usable property
    ///    (incl. the RV + businesses) whose <see cref="Capacity"/> is at least the player count, smallest -> largest.
    ///    So 2 players see all maps (incl. the RV); 10 players only see maps big enough for 10.
    /// Capacity is grid-area / <see cref="AreaPerPlayer"/> (RV -> 2, motel -> ~9), min 2.
    /// </summary>
    internal static class SafehouseSelector
    {
        // Curated AUTO-default codes, SMALLEST interior -> LARGEST. Excludes the RV (tiny) + laundering businesses.
        private static readonly string[] Tiers =
        {
            "motelroom", "storageunit", "sweatshop", "seweroffice", "bungalow", "barn", "dockswarehouse", "manor",
        };

        private const int MaxLobby = 16;       // scale the auto-default across the tiers up to this
        private const float AreaPerPlayer = 14f;   // grid tiles a player "needs" -> per-map capacity

        internal static int Capacity(int area)
        {
            int c = UnityEngine.Mathf.RoundToInt(area / AreaPerPlayer);
            return c < 2 ? 2 : c;
        }

        /// <summary>AUTO default safehouse code for the lobby size. "" = nothing usable loaded.</summary>
        internal static string SelectForPlayerCount(int playerCount)
        {
            var ordered = OrderedDefaults();
            if (ordered.Count == 0) return "";
            int pc = playerCount < 2 ? 2 : (playerCount > MaxLobby ? MaxLobby : playerCount);
            int idx = UnityEngine.Mathf.RoundToInt((pc - 2) / (float)(MaxLobby - 2) * (ordered.Count - 1));
            if (idx < 0) idx = 0;
            if (idx > ordered.Count - 1) idx = ordered.Count - 1;
            return ordered[idx];
        }

        /// <summary>Every loaded usable property big enough for the player count, smallest -> largest (manual switch).</summary>
        internal static List<string> AvailableForPlayerCount(int playerCount)
        {
            int pc = playerCount < 2 ? 2 : playerCount;
            var usable = GatherUsable();
            var fits = new List<KeyValuePair<string, int>>();
            foreach (var kv in usable)
                if (Capacity(kv.Value) >= pc) fits.Add(kv);
            if (fits.Count == 0) fits.AddRange(usable);   // lobby bigger than every map -> offer them all
            fits.Sort((a, b) => a.Value.CompareTo(b.Value));
            var codes = new List<string>();
            foreach (var kv in fits) codes.Add(kv.Key);
            return codes;
        }

        /// <summary>True if the coded property is loaded and big enough for the player count (keeps the host's pick
        /// across rounds unless the lobby outgrows it).</summary>
        internal static bool Fits(string code, int playerCount)
        {
            if (string.IsNullOrEmpty(code)) return false;
            var usable = GatherUsable();
            return usable.TryGetValue(code, out var area) && Capacity(area) >= (playerCount < 2 ? 2 : playerCount);
        }

        // The curated default tiers that are actually loaded; falls back to all usable ranked by area.
        private static List<string> OrderedDefaults()
        {
            var usable = GatherUsable();
            var ordered = new List<string>();
            for (int i = 0; i < Tiers.Length; i++)
                if (usable.ContainsKey(Tiers[i])) ordered.Add(Tiers[i]);
            if (ordered.Count > 0) return ordered;
            var rest = new List<KeyValuePair<string, int>>(usable);
            rest.Sort((a, b) => a.Value.CompareTo(b.Value));
            foreach (var kv in rest) ordered.Add(kv.Key);
            return ordered;
        }

        // code -> grid area, for every loaded property we can teleport into (has a spawn point). No blocklist:
        // the manual switch may pick any of them; only the auto-default filters down to the curated Tiers.
        private static Dictionary<string, int> GatherUsable()
        {
            var map = new Dictionary<string, int>();
            try
            {
                var props = Property.Properties;
                if (props == null) return map;
                for (int i = 0; i < props.Count; i++)
                {
                    var p = props[i];
                    if (p == null) continue;
                    if (p.InteriorSpawnPoint == null && p.SpawnPoint == null) continue;
                    string code = p.PropertyCode;
                    if (string.IsNullOrEmpty(code)) continue;
                    map[code] = GridArea(p);
                }
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] safehouse gather failed: " + e.Message); }
            return map;
        }

        internal static int GridArea(Property p)
        {
            int area = 0;
            try
            {
                var grids = p.Grids;
                if (grids != null)
                    for (int i = 0; i < grids.Count; i++)
                    {
                        var g = grids[i];
                        if (g != null) area += g.Width * g.Height;
                    }
            }
            catch { }
            return area;
        }

#if DEBUG
        /// <summary>phsafehouse: dump every loaded property (codes/sizes/spawn/capacity are runtime data) + the picks.</summary>
        internal static void DumpProperties()
        {
            try
            {
                var props = Property.Properties;
                if (props == null) { Core.Log.Msg("[PropHunt] phsafehouse: Property.Properties is null (scene not loaded?)."); return; }
                Core.Log.Msg($"[PropHunt] phsafehouse: {props.Count} loaded properties (sorted by grid area):");
                var rows = new List<(string code, string name, int area)>();
                for (int i = 0; i < props.Count; i++)
                {
                    var p = props[i];
                    if (p == null) continue;
                    rows.Add((p.PropertyCode, p.PropertyName, GridArea(p)));
                }
                rows.Sort((a, b) => a.area.CompareTo(b.area));
                foreach (var r in rows)
                    Core.Log.Msg($"[PropHunt]   area={r.area,5}  cap={Capacity(r.area),3}  code='{r.code}'  name='{r.name}'");
                for (int pc = 2; pc <= 16; pc += 2)
                    Core.Log.Msg($"[PropHunt]   -> {pc} players: default='{SelectForPlayerCount(pc)}'  options=[{string.Join(", ", AvailableForPlayerCount(pc).ToArray())}]");
            }
            catch (System.Exception e) { Core.Log.Warning("[PropHunt] phsafehouse dump failed: " + e.Message); }
        }
#endif
    }
}
