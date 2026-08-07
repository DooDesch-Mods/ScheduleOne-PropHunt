#if DEBUG
using System.Collections.Generic;
using UnityEngine;

namespace PropHunt.Disguise
{
    /// <summary>
    /// phpropcheck: measure whether a prop's disguise will sit ON the ground or FLOAT above it, without anyone
    /// having to become it and look.
    ///
    /// A disguise is positioned by snapping the BASE of the bounds computed from the SOURCE asset
    /// (<see cref="PropClone.TryGetPropBoundsFromSource"/>) onto the player's feet, but what the player actually
    /// SEES is the built + stripped clone. Those two are computed by different code over different filters, so
    /// when they disagree the visible mesh ends up somewhere other than the ground - the "floating golden toilet"
    /// class of bug. This builds the real clone, measures both, and prints the gap plus the single lowest mesh
    /// that produced it, which is the thing that has to be excluded.
    ///
    /// Debug-only, console-driven (no hotkey), and it destroys everything it makes.
    /// </summary>
    internal static class PropGeometryCheck
    {
        /// <summary>Check every catalog entry whose name or key contains <paramref name="filter"/> (empty = all).
        /// Anything off the ground by more than a couple of centimetres is reported as FLOAT/SUNK.</summary>
        internal static void Run(string filter)
        {
            List<PropEntry> entries;
            try { entries = PropCatalog.CatalogSnapshot(); }
            catch (System.Exception e) { Core.Log.Warning("phpropcheck: catalog unavailable - " + e.Message); return; }

            int checkedCount = 0, bad = 0;
            foreach (var e in entries)
            {
                if (e == null) continue;
                if (!string.IsNullOrEmpty(filter) &&
                    (e.Name == null || e.Name.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0) &&
                    (e.Key == null || e.Key.IndexOf(filter, System.StringComparison.OrdinalIgnoreCase) < 0)) continue;

                checkedCount++;
                if (CheckOne(e)) bad++;
            }

            if (checkedCount == 0) { Core.Log.Warning($"phpropcheck: no catalog entry matches \"{filter}\"."); return; }
            Core.Log.Msg($"phpropcheck: {checkedCount} prop(s) checked, {bad} misplaced.");
        }

        /// <summary>True when this prop would visibly float or sink.</summary>
        private static bool CheckOne(PropEntry e)
        {
            GameObject clone = null;
            try
            {
                if (!PropClone.TryGetPropBoundsFromSource(e, out var placing))
                {
                    Core.Log.Msg($"phpropcheck  {e.Key}: no source bounds - the disguise falls back to clone bounds, nothing to compare.");
                    return false;
                }

                clone = PropClone.Build(e, "ph_geomcheck");
                if (clone == null) { Core.Log.Warning($"phpropcheck  {e.Key}: clone build FAILED (this prop cannot be worn)."); return true; }

                if (!PropClone.TryGetPropLocalBounds(clone, out var visible))
                {
                    Core.Log.Warning($"phpropcheck  {e.Key}: the built clone has no visible mesh at all.");
                    return true;
                }

                // The disguise puts placing.min.y on the player's feet, so the visible mesh ends up this far off:
                //   > 0 -> the visible mesh starts above the ground (floats), < 0 -> it is buried.
                float gap = visible.min.y - placing.min.y;
                bool misplaced = Mathf.Abs(gap) > 0.05f;

                string verdict = !misplaced ? "ok" : (gap > 0f ? $"FLOATS {gap:F2}m" : $"SUNK {(-gap):F2}m");
                Core.Log.Msg($"phpropcheck  {e.Key}: {verdict}  " +
                             $"placing.min.y={placing.min.y:F2} visible.min.y={visible.min.y:F2} " +
                             $"size={placing.size.x:F1}x{placing.size.y:F1}x{placing.size.z:F1}");

                if (misplaced) NameTheCulprit(e, placing);
                return misplaced;
            }
            catch (System.Exception ex) { Core.Log.Warning($"phpropcheck  {e.Key}: threw - {ex.Message}"); return true; }
            finally { if (clone != null) { try { UnityEngine.Object.DestroyImmediate(clone); } catch { } } }
        }

        /// <summary>Print the source meshes sitting at the very bottom of the placing bounds. When a prop floats,
        /// one of these is a build-only or otherwise invisible mesh that the bounds filter still counted.</summary>
        private static void NameTheCulprit(PropEntry e, Bounds placing)
        {
            try
            {
                if (e.SourceRoot == null) return;
                var rootT = e.SourceRoot.transform;
                var mfs = e.SourceRoot.GetComponentsInChildren<MeshFilter>(true);
                if (mfs == null) return;

                foreach (var mf in mfs)
                {
                    if (mf == null || mf.sharedMesh == null) continue;
                    var b = mf.sharedMesh.bounds;
                    var centre = rootT.InverseTransformPoint(mf.transform.TransformPoint(b.center));
                    float half = Mathf.Abs(rootT.InverseTransformVector(mf.transform.TransformVector(new Vector3(0f, b.extents.y, 0f))).y);
                    float minY = centre.y - half;
                    if (minY > placing.min.y + 0.05f) continue;   // not one of the lowest

                    var mr = mf.GetComponent<MeshRenderer>();
                    Core.Log.Msg($"phpropcheck    bottom mesh \"{mf.sharedMesh.name}\" on \"{mf.gameObject.name}\" " +
                                 $"minY={minY:F2} activeSelf={mf.gameObject.activeSelf} " +
                                 $"renderer={(mr == null ? "none" : (mr.enabled ? "on" : "off"))}");
                }
            }
            catch { }
        }
    }
}
#endif
