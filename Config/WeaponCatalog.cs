using System;
using System.Collections.Generic;
using System.Text;
using Il2CppScheduleOne;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Equipping;

namespace PropHunt.Config
{
    /// <summary>
    /// Builds the host-form hunter-weapon list from the game's item Registry. Weapon item IDs live in the game's
    /// asset bundles (not in source), and the Registry is only populated in a gameplay scene - NOT at the menu where
    /// the host form is built. So on the first gameplay scene we enumerate every ranged/melee weapon once and cache
    /// the (id, name) list to preferences; the menu dropdown reads that cache (with a small built-in fallback until
    /// it is primed). The Golden M1911 is excluded on purpose (reserved for an easter egg).
    /// </summary>
    internal static class WeaponCatalog
    {
        /// <summary>PropHunt's default hunter weapon - the pump shotgun.</summary>
        internal const string DefaultWeaponId = "pumpshotgun";

        /// <summary>Cached (id, display-name) weapon pairs, in registry order. Empty until primed in a gameplay scene.</summary>
        internal static List<KeyValuePair<string, string>> Weapons()
        {
            var list = new List<KeyValuePair<string, string>>();
            var cache = PropHuntPreferences.WeaponCache;
            if (string.IsNullOrEmpty(cache)) return list;
            foreach (var line in cache.Split('\n'))
            {
                if (line.Length == 0) continue;
                int bar = line.IndexOf('|');
                if (bar <= 0) continue;
                list.Add(new KeyValuePair<string, string>(line.Substring(0, bar), line.Substring(bar + 1)));
            }
            return list;
        }

        /// <summary>Enumerate the live item Registry for ranged/melee weapons and cache them (excluding the Golden
        /// M1911). Safe on any gameplay scene; no-op until the Registry is populated. Returns true if the cache changed.</summary>
        internal static bool RefreshFromRegistry()
        {
            try
            {
                var reg = PersistentSingleton<Registry>.Instance;
                if (reg == null) return false;
                var all = reg.GetAllItems();
                if (all == null || all.Count == 0) return false;
                var seen = new HashSet<string>();
                var sb = new StringBuilder();
                for (int i = 0; i < all.Count; i++)
                {
                    var def = all[i];
                    if (def == null) continue;
                    Equippable eq = null;
                    try { eq = def.Equippable; } catch { }
                    if (eq == null) continue;
                    bool weapon = false;
                    try { weapon = eq.TryCast<Equippable_RangedWeapon>() != null || eq.TryCast<Equippable_MeleeWeapon>() != null; } catch { }
                    if (!weapon) continue;
                    string id = null, name = null;
                    try { id = def.ID; name = def.Name; } catch { }
                    if (string.IsNullOrEmpty(id)) continue;
                    if (id.IndexOf("golden", StringComparison.OrdinalIgnoreCase) >= 0) continue;   // reserved for an easter egg
                    if (!seen.Add(id)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(id).Append('|').Append(string.IsNullOrEmpty(name) ? id : name);
                }
                string cache = sb.ToString();
                if (string.IsNullOrEmpty(cache) || cache == PropHuntPreferences.WeaponCache) return false;
                PropHuntPreferences.SaveWeaponCache(cache);
                Core.Log.Msg($"[PropHunt] hunter-weapon catalog cached ({seen.Count} weapons): {cache.Replace('\n', ' ')}");
                return true;
            }
            catch (Exception e) { Core.Log.Warning("[PropHunt] weapon enumeration failed: " + e.Message); return false; }
        }
    }
}
