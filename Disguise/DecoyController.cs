using System.Collections.Generic;
using UnityEngine;
using PropHunt.Game;

namespace PropHunt.Disguise
{
    /// <summary>
    /// LOCAL on every client: renders the host-synced decoys (static, render-only copies of a prop left behind
    /// to mislead hunters). Same crash-safe clone-mesh approach as <see cref="DisguiseController"/>. The decoy
    /// list is append-only within a round and cleared on a new round, so a simple count check decides when to
    /// rebuild. Decoys have no collider, so the hunter's catch ray passes through them (they tag nothing).
    /// </summary>
    internal sealed class DecoyController
    {
        private readonly List<GameObject> _decoys = new List<GameObject>();
        private int _builtCount = -1;

        internal void Apply(GameState state)
        {
            if (state == null) return;
            try
            {
                if (state.Decoys.Count == _builtCount) return;   // rebuild only when the set changes
                Clear();
                foreach (var d in state.Decoys)
                {
                    var e = PropCatalog.ById(d.PropId);
                    if (e == null || e.Source == null || e.Source.sharedMesh == null) continue;
                    var go = new GameObject("ph_decoy");
                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = e.Source.sharedMesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    if (e.SourceRenderer != null) mr.sharedMaterials = e.SourceRenderer.sharedMaterials;
                    go.transform.localScale = e.Source.transform.lossyScale;
                    go.transform.rotation = Quaternion.Euler(0f, d.Yaw, 0f) * e.Source.transform.rotation;
                    // place by world bounds: centred on the drop spot (xz), base at the stored ground y
                    go.transform.position = new Vector3(d.X, d.Y, d.Z);
                    Bounds wb = mr.bounds;
                    go.transform.position += new Vector3(d.X - wb.center.x, d.Y - wb.min.y, d.Z - wb.center.z);
                    _decoys.Add(go);
                }
                _builtCount = state.Decoys.Count;
                Core.LogDebug($"[PropHunt] decoys: rendering {_decoys.Count}.");
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] decoy apply failed: " + e.Message); }
        }

        private void Clear()
        {
            foreach (var g in _decoys) if (g != null) { try { UnityEngine.Object.Destroy(g); } catch { } }
            _decoys.Clear();
        }

        internal void Dispose() { try { Clear(); } catch { } _builtCount = -1; }
    }
}
