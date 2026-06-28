using UnityEngine;
using PropHunt.Game;

namespace PropHunt.PlayArea
{
    /// <summary>
    /// LOCAL visible boundary: a tall translucent cylinder (ground to ~200m) centered on the play area, shown
    /// during the round. Occluded by buildings/terrain (Border.shader uses ZTest LessEqual), fades in as the local
    /// player approaches the edge (_Proximity), and erupts from the ground (vertical gradient). Enforcement (warn ->
    /// eliminate/teleport) lives in PlayAreaController; this is render-only.
    ///
    /// If the propborder shader bundle is not built yet, BorderShader.TryCreateMaterial returns null and the wall is
    /// simply invisible (the mod runs fine) - build the bundle to see it.
    /// </summary>
    internal sealed class PlayAreaBorder
    {
        private readonly GameModeController _ctl;
        private GameObject _root;
        private MeshFilter _mf;
        private MeshRenderer _mr;
        private Material _mat;
        private string _builtSig;

        // proximity thresholds (horizontal metres from the wall edge)
        private const float ProximityFullNear = 5f;
        private const float ProximityFadeOut = 45f;

        internal PlayAreaBorder(GameModeController ctl) { _ctl = ctl; }

        internal void Tick()
        {
            try
            {
                var s = _ctl.State;
                bool show = s != null && s.AreaRadius > 0f &&
                            (_ctl.Phase == RoundPhase.Hiding || _ctl.Phase == RoundPhase.Hunting || _ctl.Phase == RoundPhase.RoundEnd);
                if (!show) { if (_root != null) _root.SetActive(false); return; }

                string sig = $"{s.AreaX:F1}|{s.AreaY:F1}|{s.AreaZ:F1}|{s.AreaRadius:F1}";
                if (_root == null || _builtSig != sig) Build(s.AreaX, s.AreaY, s.AreaZ, s.AreaRadius, sig);
                if (_root != null) _root.SetActive(true);
                UpdateProximity(s);
            }
            catch (System.Exception e) { Core.LogDebug("[PropHunt] border tick failed: " + e.Message); }
        }

        // horizontal distance to the wall -> _Proximity (0 = at the wall, 1 = far inside); ignores Y for stability.
        private void UpdateProximity(GameState s)
        {
            if (_mat == null) return;
            try
            {
                var lp = Player.Local;
                if (lp == null) return;
                var pos = lp.transform.position;
                float dx = pos.x - s.AreaX, dz = pos.z - s.AreaZ;
                float distToWall = s.AreaRadius - Mathf.Sqrt(dx * dx + dz * dz);
                float prox = Mathf.Clamp01(Mathf.InverseLerp(ProximityFullNear, ProximityFadeOut, distToWall));
                _mat.SetFloat("_Proximity", prox);
            }
            catch { }
        }

        private void Build(float cx, float cy, float cz, float radius, string sig)
        {
            Destroy();
            EnsureMat();
            _root = new GameObject("ph_border_wall");
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.transform.position = new Vector3(cx, cy, cz);
            _mf = _root.AddComponent<MeshFilter>();
            _mr = _root.AddComponent<MeshRenderer>();
            _mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _mr.receiveShadows = false;
            if (_mat != null) _mr.sharedMaterial = _mat;
            // no shader bundle yet -> hide the mesh entirely rather than show Unity's magenta "missing material".
            _mr.enabled = _mat != null;
            BorderMesh.Build(_mf, radius, BorderMesh.Height);
            _builtSig = sig;
            Core.LogDebug($"[PropHunt] border wall built at ({cx:F0},{cz:F0}) r={radius:F0} mat={(_mat != null)}");
        }

        private void EnsureMat()
        {
            if (_mat != null) return;
            _mat = BorderShader.TryCreateMaterial(new Color(1f, 0.25f, 0.10f, 0.45f), new Color(1f, 0.55f, 0.20f, 1f));
            if (_mat != null) UnityEngine.Object.DontDestroyOnLoad(_mat);
            else Core.Log?.Warning("[PropHunt] border shader bundle missing - play-area wall is invisible until 'propborder' is built.");
        }

        private void Destroy()
        {
            if (_root != null) { try { UnityEngine.Object.Destroy(_root); } catch { } _root = null; }
            _mf = null; _mr = null;
        }

        internal void Dispose() { Destroy(); _builtSig = null; }
    }
}
