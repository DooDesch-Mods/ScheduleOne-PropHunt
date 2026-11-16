#if DEBUG
namespace PropHunt.Disguise
{
    /// <summary>
    /// The phnames test command: runs every catalog prop through <see cref="PropNames"/> and logs one summary line
    /// ("names: total=N readable=R fallback=F raw=0") plus one line per prop that still shows mesh noise or falls
    /// back to "this prop". The walk in tests/phone.py fails on raw &gt; 0.
    /// </summary>
    internal static class PropNamesReport
    {
        internal static void Run()
        {
            var entries = PropCatalog.Entries();
            int readable = 0, fallback = 0, raw = 0;
            foreach (var e in entries)
            {
                string shown = PropNames.Display(e);
                if (shown == null) { fallback++; Core.Log.Msg($"names: fallback id={e.Id} mesh='{e.Name}' key='{e.Key}'"); continue; }
                if (PropNames.LooksRaw(shown)) { raw++; Core.Log.Msg($"names: RAW id={e.Id} shown='{shown}' mesh='{e.Name}' key='{e.Key}'"); continue; }
                readable++;
            }
            Core.Log.Msg($"names: total={entries.Count} readable={readable} fallback={fallback} raw={raw}");
        }

        /// <summary>
        /// The prompt name of a catalog prop whose mesh name is noisy (a LOD tag, a ".003", a bare "Body"), so a
        /// forced crosshair target shows the cleaner at work; the first prop when none is noisy.
        /// </summary>
        internal static string SampleName()
        {
            var entries = PropCatalog.Entries();
            if (entries.Count == 0) return PropNames.Fallback;
            foreach (var e in entries)
                if (e.Name != null && PropNames.LooksRaw(e.Name.Replace('_', ' ')) || (e.Name ?? "").Contains("_LOD"))
                    return PropNames.DisplayOrFallback(e);
            return PropNames.DisplayOrFallback(entries[0]);
        }
    }
}
#endif
