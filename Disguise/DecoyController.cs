using System.Collections.Generic;
using UnityEngine;
using PropHunt.Game;

namespace PropHunt.Disguise
{
    /// <summary>
    /// LOCAL on every client: renders the host-synced decoys (static, render-only copies of a prop left behind
    /// to mislead hunters). Same crash-safe clone-mesh approach as <see cref="DisguiseController"/>. A decoy
    /// gets a single trigger BoxCollider sized to its world bounds so a hunter's Raycast can hit it. Destroyed
    /// decoys are tombstoned as null entries so the list index always matches the state's Decoys index.
    /// </summary>
    internal sealed class DecoyController
    {
        // null entries are tombstones for destroyed or failed decoys - index mirrors state.Decoys index
        private readonly List<GameObject> _decoys = new List<GameObject>();
        // rebuild signature: count + one bit per decoy for Destroyed; changes when a decoy is destroyed
        private string _builtSig = null;

        internal void Apply(GameState state)
        {
            if (state == null) return;
            try
            {
                string sig = BuildSig(state);
                if (sig == _builtSig) return;
                // full rebuild: clear all and re-create from current state
                Clear();
                for (int i = 0; i < state.Decoys.Count; i++)
                {
                    var d = state.Decoys[i];
                    if (d.Destroyed)
                    {
                        _decoys.Add(null);   // tombstone: keep index stable, render nothing
                        continue;
                    }
                    var e = PropCatalog.ById(d.PropId);
                    if (e == null || e.SourceRoot == null) { _decoys.Add(null); continue; }
                    // name includes the decoy index so CatchController can identify which decoy was hit
                    var go = PropClone.Build(e, "ph_decoy_" + i);
                    if (go == null) { _decoys.Add(null); continue; }
                    go.transform.localScale = e.SourceRoot.transform.lossyScale;
                    go.transform.rotation = Quaternion.Euler(0f, d.Yaw, 0f) * e.SourceRoot.transform.rotation;
                    go.transform.position = new Vector3(d.X, d.Y, d.Z);
                    // place by SOURCE-mesh bounds (centred on the drop spot xz, base at the stored ground y), the
                    // same approach the disguise uses - so a prop whose prefab carries an unrelated sibling mesh
                    // does not get a bogus huge box that flings the decoy across the map.
                    if (PropClone.TryGetPropBoundsFromSource(e, out var lb))
                    {
                        Bounds wb = PropClone.LocalToWorldBounds(go.transform, lb);
                        go.transform.position += new Vector3(d.X - wb.center.x, d.Y - wb.min.y, d.Z - wb.center.z);
                        PropClone.AddTriggerHitbox(go, lb);   // trigger hitbox from the same bounds
                    }
                    else
                    {
                        if (PropClone.TryGetWorldBounds(go, out Bounds wb2))
                            go.transform.position += new Vector3(d.X - wb2.center.x, d.Y - wb2.min.y, d.Z - wb2.center.z);
                        PropClone.AddTriggerHitbox(go);
                    }

                    _decoys.Add(go);
                }
                _builtSig = sig;
                Core.LogDebug($"[PropHunt] decoys: rendering {ActiveCount()} of {_decoys.Count}.");
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] decoy apply failed: " + e.Message); }
        }

        /// <summary>Build a signature string that changes whenever the decoy count or any Destroyed flag changes.</summary>
        private static string BuildSig(GameState state)
        {
            if (state.Decoys.Count == 0) return "0:";
            var sb = new System.Text.StringBuilder(state.Decoys.Count * 2 + 4);
            sb.Append(state.Decoys.Count).Append(':');
            for (int i = 0; i < state.Decoys.Count; i++)
                sb.Append(state.Decoys[i].Destroyed ? '1' : '0');
            return sb.ToString();
        }

        private int ActiveCount()
        {
            int n = 0;
            for (int i = 0; i < _decoys.Count; i++) if (_decoys[i] != null) n++;
            return n;
        }

        private void Clear()
        {
            for (int i = 0; i < _decoys.Count; i++)
            {
                var g = _decoys[i];
                if (g != null) { try { UnityEngine.Object.Destroy(g); } catch { } }
            }
            _decoys.Clear();
        }

        internal void Dispose() { try { Clear(); } catch { } _builtSig = null; }
    }
}
