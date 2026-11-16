using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PropHunt.Disguise
{
    /// <summary>
    /// The name a player reads for a catalog prop, in the HUD prompt, the "You are now" banner and the phone app.
    ///
    /// A catalog entry carries the mesh's own name, and mesh names are authoring noise: "Body_LOD0.003",
    /// "SM_TrashCan_01", "Cube". The cleaner strips the noise and refuses names that say nothing about the object
    /// ("Body", "Cube", "Mesh"); for a refused name it tries the registry key and the source object's parents, which
    /// usually carry the prefab name. When none of them reads as a name, <see cref="Display"/> answers null and the
    /// caller shows <see cref="Fallback"/>; the raw name is logged once per prop so the catalog can be curated.
    /// </summary>
    internal static class PropNames
    {
        /// <summary>What a prompt says for a prop without a readable name: "[E] become this prop".</summary>
        internal const string Fallback = "this prop";

        private static readonly Dictionary<int, string> _cache = new Dictionary<int, string>();
        private static readonly HashSet<int> _logged = new HashSet<int>();

        // Words that name a part of a model, a modelling primitive or a scene container, not an object a player sees.
        private static readonly HashSet<string> _generic = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "body", "cube", "mesh", "object", "default", "model", "plane", "cylinder", "sphere", "quad", "geo",
            "geometry", "group", "root", "base", "part", "parts", "visual", "visuals", "combined mesh", "main",
            "prop", "props", "item", "new", "unnamed", "untitled", "collider", "lod", "lods", "lod group", "meshes",
            "models", "environment", "world", "map", "static", "dynamic", "decor", "decoration", "misc", "exterior",
            "interior", "building", "buildings", "container", "holder", "pivot", "offset", "graphics", "render",
            "renderer", "art", "shell", "frame", "top", "bottom", "side", "left", "right", "front", "back",
        };

        private static readonly Regex _clone = new Regex(@"\s*\(Clone\)", RegexOptions.IgnoreCase);
        private static readonly Regex _instance = new Regex(@"\s*\(\d+\)\s*$");
        private static readonly Regex _dotNumber = new Regex(@"\.\d+$");
        private static readonly Regex _prefix = new Regex(@"^(SM|SK|S|P|M|GEO|MDL)_", RegexOptions.IgnoreCase);
        private static readonly Regex _lod = new Regex(@"(^|[_\s.\-])LOD\d*(?=$|[_\s.\-])", RegexOptions.IgnoreCase);
        private static readonly Regex _trailingNumber = new Regex(@"[_\s\-]+\d+$");
        private static readonly Regex _primitive = new Regex(@"^(poly\s?surface|p\s?cube|p\s?cylinder|p\s?plane|p\s?sphere|object|mesh|cube|body)\s*\d*$", RegexOptions.IgnoreCase);
        private static readonly Regex _spaces = new Regex(@"\s+");

        /// <summary>The readable name of a prop, or null when nothing about it reads as a name.</summary>
        internal static string Display(PropEntry e)
        {
            if (e == null) return null;
            if (_cache.TryGetValue(e.Id, out var cached)) return cached;

            string found = null;
            foreach (var candidate in Candidates(e))
            {
                found = Clean(candidate);
                if (found != null) break;
            }

            _cache[e.Id] = found;
            if (found == null && _logged.Add(e.Id))
                Core.Log.Msg($"prop {e.Id} has no readable name (mesh '{e.Name}', key '{e.Key}'); prompts call it '{Fallback}'.");
            return found;
        }

        /// <summary>The readable name, or <see cref="Fallback"/>; never null for a real entry.</summary>
        internal static string DisplayOrFallback(PropEntry e) => e == null ? null : (Display(e) ?? Fallback);

        /// <summary>Forgets the cached names; the catalog ids are rebuilt on a rescan.</summary>
        internal static void Reset() { _cache.Clear(); }

        // Where a name can come from, best first: a namespaced registry key ("reg:SmallTrashCan"), the mesh name,
        // then the source object and up to three of its parents, the LOD group and the clone root.
        private static IEnumerable<string> Candidates(PropEntry e)
        {
            string key = e.Key ?? "";
            int colon = key.IndexOf(':');
            if (colon > 0 && colon < key.Length - 1)
            {
                string suffix = key.Substring(colon + 1);
                if (!LooksLikeAnId(suffix)) yield return suffix;
            }

            yield return e.Name;

            var names = new List<string>();
            try
            {
                Transform t = e.Source != null ? e.Source.transform : null;
                for (int i = 0; i < 4 && t != null; i++) { names.Add(t.name); t = t.parent; }
                if (e.SourceLodGroup != null) names.Add(e.SourceLodGroup.gameObject.name);
                if (e.SourceRoot != null) names.Add(e.SourceRoot.name);
            }
            catch (Exception ex)
            {
                Core.Log.Warning($"prop {e.Id}: could not read the source object's names ({ex.Message}); only the mesh name is tried.");
            }
            foreach (var n in names) yield return n;
        }

        /// <summary>A raw object or mesh name cleaned for a player, or null when what is left is not a name.</summary>
        internal static string Clean(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            string s = _clone.Replace(raw.Trim(), "");
            s = _instance.Replace(s, "");
            while (_dotNumber.IsMatch(s)) s = _dotNumber.Replace(s, "");
            s = _prefix.Replace(s, "");
            s = _lod.Replace(s, "$1");
            s = _trailingNumber.Replace(s, "");
            s = s.Replace('_', ' ').Replace('-', ' ').Replace('.', ' ');

            // "GoldenToilet" -> "Golden Toilet": registry and prefab names are PascalCase.
            var sb = new StringBuilder(s.Length + 4);
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (i > 0 && char.IsUpper(ch) && char.IsLower(s[i - 1])) sb.Append(' ');
                sb.Append(ch);
            }
            s = _spaces.Replace(sb.ToString(), " ").Trim();

            // "Trash Can Prop" -> "Trash Can": every disguise is a prop, the word adds nothing.
            if (s.EndsWith(" Prop", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 5).TrimEnd();

            if (!Readable(s)) return null;
            return char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static bool Readable(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 3) return false;
            int letters = 0;
            foreach (char c in s) if (char.IsLetter(c)) letters++;
            if (letters < 3) return false;
            if (_generic.Contains(s) || _primitive.IsMatch(s)) return false;
            if (LooksLikeAnId(s.Replace(" ", ""))) return false;
            return true;
        }

        /// <summary>
        /// Whether a string is an internal identifier rather than something to show a player: a bare run of hex
        /// digits, which is what a content hash looks like. Six characters is the shortest hash the catalog emits
        /// and long enough that a real word ("decade", "facade") is not mistaken for one.
        /// </summary>
        internal static bool LooksLikeAnId(string s)
        {
            if (string.IsNullOrEmpty(s) || s.Length < 6) return false;
            foreach (char c in s)
            {
                bool hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether a shown name still carries mesh noise: a LOD tag, a ".003" suffix, or a bare modelling word.
        /// The test command reports every catalog entry this is true for; the answer must be none.
        /// </summary>
        internal static bool LooksRaw(string shown)
        {
            if (string.IsNullOrEmpty(shown)) return false;
            if (Regex.IsMatch(shown, @"LOD\d", RegexOptions.IgnoreCase)) return true;
            if (Regex.IsMatch(shown, @"\.\d{3}")) return true;
            if (shown.IndexOf('_') >= 0) return true;
            return _generic.Contains(shown) || _primitive.IsMatch(shown);
        }
    }
}
