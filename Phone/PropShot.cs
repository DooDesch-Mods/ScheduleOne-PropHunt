using System;
using PropHunt.Disguise;

namespace PropHunt.Phone
{
    /// <summary>
    /// Photographs a becomable prop so the phone app can show what you are, rather than spelling it.
    ///
    /// The rig is Yoink's (<c>Yoink/Item/IconRenderer.cs</c>), which is the right one to copy for this: everything
    /// happens far below the world on layer 31, with a camera and two lights that see only that layer, so nothing
    /// is visible to the player for the frame it exists. The subject is a real <see cref="PropClone"/> - the same
    /// stripped, script-free copy a disguise wears - so the picture is the prop, not an approximation of it.
    ///
    /// The game's own <c>IconGenerator</c> is not usable here. Its camera pose is authored and absolute, framed for
    /// a packaged product held at a fixed spot; pointing it at props that range from a traffic cone to a van puts
    /// most of them off frame (BreedToSeed/Catalog/ModelIcon.cs records two attempts at exactly that: first a blank
    /// square, then a three-pixel strip).
    /// </summary>
    internal static class PropShot
    {
        /// <summary>Large enough for the 96px dressing-room tile with a mip chain under it, small enough that one
        /// readback is not felt. The page never draws it above 96 css px.</summary>
        private const int Size = 192;

        /// <summary>The last user layer. The camera and both lights are masked to it and nothing else uses it.</summary>
        private const int Layer = 31;

        private static readonly Vector3 Studio = new Vector3(0f, -9000f, 0f);

        /// <summary>
        /// A three-quarter view from slightly above - the angle the game draws its own buildable icons at, and the
        /// one where a box reads as a box rather than as a rectangle.
        /// </summary>
        private static readonly Quaternion View = Quaternion.Euler(20f, 35f, 0f);

        /// <summary>PNG bytes for one prop, or null when it could not be photographed.</summary>
        internal static byte[] Render(int propId)
        {
            PropEntry entry;
            try { entry = PropCatalog.ById(propId); }
            catch { return null; }

            if (entry == null) return null;

            GameObject subject = null;
            GameObject rig = null;
            RenderTexture rt = null;
            RenderTexture previous = RenderTexture.active;

            try
            {
                subject = PropClone.Build(entry, "ph_shot_" + propId);
                if (subject == null) return null;

                subject.transform.position = Studio;
                subject.transform.rotation = Quaternion.identity;
                subject.SetActive(true);
                SetLayer(subject.transform, Layer);
                ForceNearestLod(subject);

                if (!TryBounds(subject.transform, out Bounds b)) return null;

                rig = new GameObject("ph_shot_rig");
                rig.transform.position = Studio;

                Vector3 dir = View * Vector3.forward;

                var camObj = new GameObject("ph_shot_cam");
                camObj.transform.SetParent(rig.transform, false);
                camObj.transform.position = b.center - dir * (b.extents.magnitude * 4f + 1f);
                camObj.transform.rotation = View;

                Camera cam = camObj.AddComponent<Camera>();
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0f, 0f, 0f, 0f);    // transparent: the page puts its own panel behind it
                cam.cullingMask = 1 << Layer;
                cam.orthographic = true;
                cam.orthographicSize = ProjectedRadius(b, View) * 1.14f;
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = b.extents.magnitude * 8f + 20f;
                cam.enabled = false;                                 // rendered by hand, never by the frame loop
                cam.allowHDR = false;
                cam.allowMSAA = false;

                // Props use the game's lit shaders, so with no light they photograph black. A key from the camera's
                // shoulder for form, a dim fill from behind so the shadowed faces still read.
                AddLight(rig.transform, View * Quaternion.Euler(25f, -30f, 0f), 1.35f);
                AddLight(rig.transform, View * Quaternion.Euler(-15f, 140f, 0f), 0.55f);

                rt = new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 4 };
                rt.Create();
                cam.targetTexture = rt;
                cam.Render();

                RenderTexture.active = rt;
                var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
                {
                    hideFlags = HideFlags.HideAndDontSave,
                };
                tex.ReadPixels(new Rect(0f, 0f, Size, Size), 0, 0, false);
                tex.Apply();

                byte[] png = ImageConversion.EncodeToPNG(tex);
                UnityEngine.Object.Destroy(tex);

                return png != null && png.Length > 0 ? png : null;
            }
            catch (Exception e)
            {
                Core.LogDebug("[PropHunt] prop shot failed for " + propId + ": " + e.Message);
                return null;
            }
            finally
            {
                RenderTexture.active = previous;
                try { if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); } } catch { }
                try { if (subject != null) UnityEngine.Object.Destroy(subject); } catch { }
                try { if (rig != null) UnityEngine.Object.Destroy(rig); } catch { }
            }
        }

        /// <summary>
        /// Pin every LODGroup to its closest level. The clone keeps the original's LOD hierarchy, and a camera one
        /// metre away is still "far" by the group's own screen-height maths at this resolution - left alone, half
        /// the props photograph as their crudest mesh or as nothing at all.
        /// </summary>
        private static void ForceNearestLod(GameObject root)
        {
            foreach (LODGroup g in root.GetComponentsInChildren<LODGroup>(true))
            {
                if (g == null) continue;
                try { g.ForceLOD(0); } catch { }
            }

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                r.enabled = true;
                r.gameObject.SetActive(true);
            }
        }

        private static void AddLight(Transform parent, Quaternion rotation, float intensity)
        {
            var go = new GameObject("ph_shot_light");
            go.transform.SetParent(parent, false);
            go.transform.rotation = rotation;

            Light l = go.AddComponent<Light>();
            l.type = LightType.Directional;
            l.intensity = intensity;
            l.cullingMask = 1 << Layer;      // never touches anything the player can see
            l.shadows = LightShadows.None;
        }

        /// <summary>
        /// How much of the frame the prop needs, measured along the camera's own axes.
        ///
        /// The bounding sphere is what a first attempt reaches for, but it sizes for the longest diagonal and
        /// leaves a long thin prop - a ladder, a pallet - floating in a mostly empty tile. Projecting the box onto
        /// the camera's right and up axes fits what is actually seen, which matters here more than anywhere: the
        /// catalog runs from a traffic cone to a van.
        /// </summary>
        private static float ProjectedRadius(Bounds b, Quaternion look)
        {
            Vector3 e = b.extents;
            Vector3 right = look * Vector3.right;
            Vector3 up = look * Vector3.up;

            float halfW = Mathf.Abs(right.x) * e.x + Mathf.Abs(right.y) * e.y + Mathf.Abs(right.z) * e.z;
            float halfH = Mathf.Abs(up.x) * e.x + Mathf.Abs(up.y) * e.y + Mathf.Abs(up.z) * e.z;
            return Mathf.Max(0.01f, Mathf.Max(halfW, halfH));
        }

        private static bool TryBounds(Transform root, out Bounds bounds)
        {
            bounds = default;
            bool any = false;

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!any) { bounds = r.bounds; any = true; }
                else bounds.Encapsulate(r.bounds);
            }

            return any;
        }

        private static void SetLayer(Transform t, int layer)
        {
            if (t == null) return;
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayer(t.GetChild(i), layer);
        }
    }
}
