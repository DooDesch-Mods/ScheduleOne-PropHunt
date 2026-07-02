#if DEBUG
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;
using PropHunt.Game;
using PropHunt.Net;

namespace PropHunt.Disguise
{
    /// <summary>
    /// DEBUG-only LIVE on-player preview for the prop curator: while one client steps through props with phcurate,
    /// every OTHER player wears the currently-previewed prop, so the curator sees how it looks on a real player.
    /// The curator broadcasts the prop's content key; each client renders that prop on the OTHER players it sees
    /// (hiding their body) using the same source-mesh bounds + grounding as the real disguise. Purely local +
    /// cosmetic per client; needs no PropHunt round, just a shared lobby.
    /// </summary>
    internal static class CuratePreview
    {
        private static bool _active;
        private static PropEntry _entry;
        private static readonly Dictionary<ulong, GameObject> _clones = new Dictionary<ulong, GameObject>();
        private static readonly Dictionary<ulong, Bounds> _lb = new Dictionary<ulong, Bounds>();
        private static readonly Dictionary<ulong, Quaternion> _rot = new Dictionary<ulong, Quaternion>();
        private static bool _handlerReg;

        /// <summary>Curator: broadcast the prop being previewed (null/empty = stop). Also applied locally so the
        /// curator itself sees the prop on the other players.</summary>
        internal static void Set(string key)
        {
            try { PropHuntNet.Client?.BroadcastMessage(new CuratePreviewMessage { Key = key ?? "" }); } catch { }
            Apply(key);
        }

        internal static void Tick()
        {
            EnsureHandler();
            if (!_active || _entry == null) return;
            try
            {
                PlayerRegistry.Refresh();
                ulong localId = PropHuntNet.LocalSteamId;
                var list = Player.PlayerList;
                var present = new HashSet<ulong>();
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var p = list[i];
                        if (p == null) continue;
                        ulong id = PlayerRegistry.IdForPlayer(p);
                        if (id == 0 || id == localId) continue;   // render on the OTHER players only
                        present.Add(id);
                        Ensure(id, p);
                        Position(id, p);
                    }
                List<ulong> gone = null;
                foreach (var kid in _clones.Keys) if (!present.Contains(kid)) (gone ??= new List<ulong>()).Add(kid);
                if (gone != null) foreach (var kid in gone) Remove(kid);
            }
            catch { }
        }

        /// <summary>Stop + restore everything (called on phcurate exit + on the clear message).</summary>
        internal static void Clear() => Apply(null);

        private static void Apply(string key)
        {
            ClearClones();
            if (string.IsNullOrEmpty(key))
            {
                _entry = null; _active = false;
                try { PropCatalog.ClearKeyIndex(); } catch { }   // session ended (on this client too) - drop the cache
                return;
            }
            _entry = Resolve(key);
            _active = _entry != null;
        }

        private static void Ensure(ulong id, Player p)
        {
            if (_clones.ContainsKey(id)) return;
            var go = PropClone.Build(_entry, "ph_preview_" + id);
            if (go == null) return;
            go.transform.SetParent(null, false);
            go.transform.localScale = _entry.SourceRoot.transform.lossyScale;
            _rot[id] = _entry.SourceRoot.transform.rotation;
            go.transform.rotation = _rot[id];
            if (PropClone.TryGetPropBoundsFromSource(_entry, out var lb)) _lb[id] = lb;
            try { p.SetThirdPersonMeshesVisibility(false); p.SetVisibleToLocalPlayer(false); } catch { }
            _clones[id] = go;
        }

        private static void Position(ulong id, Player p)
        {
            if (!_clones.TryGetValue(id, out var go) || go == null) return;
            go.transform.rotation = _rot.TryGetValue(id, out var r) ? r : Quaternion.identity;
            if (!_lb.TryGetValue(id, out var lb)) return;
            var wb = PropClone.LocalToWorldBounds(go.transform, lb);
            var a = p.transform.position;
            float feetY = RoundEnvironment.FeetY(p);
            go.transform.position += new Vector3(a.x - wb.center.x, feetY - wb.min.y, a.z - wb.center.z);
        }

        private static void Remove(ulong id)
        {
            if (_clones.TryGetValue(id, out var go) && go != null) { try { Object.Destroy(go); } catch { } }
            _clones.Remove(id); _lb.Remove(id); _rot.Remove(id);
            var p = PlayerRegistry.Get(id);
            if (p != null) try { p.SetThirdPersonMeshesVisibility(true); p.SetVisibleToLocalPlayer(true); } catch { }
        }

        private static void ClearClones()
        {
            if (_clones.Count == 0) return;
            var ids = new List<ulong>(_clones.Keys);
            foreach (var id in ids) Remove(id);
        }

        private static PropEntry Resolve(string key)
        {
            // Use the curator's CACHED key index (per-mesh + per-LOD keys), built once per session. The old code
            // rebuilt a full EnumerateAllCandidates scan on every cache miss - and since keep-keys are per-LOD keys
            // that the LOD-folded candidate list never contains, that miss-rebuild fired on EVERY navigation move
            // (the extreme stepping lag). The cached index is built lazily here for non-curator clients.
            try
            {
                var idx = PropCatalog.KeyIndex();
                return (idx != null && idx.TryGetValue(key, out var pe)) ? pe : null;
            }
            catch { return null; }
        }

        private static void EnsureHandler()
        {
            if (_handlerReg) return;
            var c = PropHuntNet.Client;
            if (c == null) return;
            try { c.RegisterMessageHandler<CuratePreviewMessage>((m, s) => Apply(m.Key)); _handlerReg = true; }
            catch { }
        }
    }
}
#endif
