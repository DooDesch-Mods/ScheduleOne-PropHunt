using UnityEngine;

namespace PropHunt.PlayArea
{
    /// <summary>
    /// Builds an open-ended, double-sided cylinder mesh at runtime (no asset) for the play-area boundary wall.
    /// The cylinder spans from <see cref="Depth"/> metres BELOW the play-area centre to <see cref="Height"/> metres
    /// ABOVE it, so it bounds terrain that sits below the centre as well as above it (the play area is a horizontal
    /// radius at any altitude). UV.x = 0..1 around the ring (drives the Border shader's horizontal scroll; the seam
    /// at s=0 and s=Segments shares a position but uses uv.x 0 vs 1 so the bands don't tear). UV.y is LINEAR 0..1 from
    /// the bottom ring to the top ring.
    ///
    /// IMPORTANT - the Border shader fades the wall IN from uv.y=0 (smoothstep(0, _GroundFade, uv.y)), i.e. uv.y=0 is
    /// fully transparent. So uv.y=0 MUST sit at the very bottom (deep underground), not at the centre/ground: that way
    /// the transparent fade-in band is buried, and the wall renders solid through every ground height around the ring.
    /// (An earlier version anchored uv.y=0 at the centre, which left the wall transparent below the centre and made it
    /// appear to float above lower ground.) Normals point outward.
    /// </summary>
    internal static class BorderMesh
    {
        internal const int Segments = 96;
        internal const float Height = 200f;   // metres above the play-area centre
        internal const float Depth = 200f;    // metres below the play-area centre (covers any lower terrain)

        internal static void Build(MeshFilter mf, float radius, float height)
        {
            if (mf == null) return;
            const int rings = 2;
            int vCount = (Segments + 1) * rings;

            var verts = new Vector3[vCount];
            var uvs = new Vector2[vCount];
            var normals = new Vector3[vCount];

            for (int s = 0; s <= Segments; s++)
            {
                float t = s / (float)Segments;
                float ang = t * Mathf.PI * 2f;
                float cos = Mathf.Cos(ang), sin = Mathf.Sin(ang);
                var outward = new Vector3(cos, 0f, sin);

                int b = s * rings, top = s * rings + 1;
                verts[b] = new Vector3(cos * radius, -Depth, sin * radius);   // bottom ring, deep underground
                uvs[b] = new Vector2(t, 0f);                                  // uv.y=0 (transparent fade-in) is buried
                normals[b] = outward;

                verts[top] = new Vector3(cos * radius, height, sin * radius);
                uvs[top] = new Vector2(t, 1f);
                normals[top] = outward;
            }

            var tris = new int[Segments * (rings - 1) * 6];
            int ti = 0;
            for (int s = 0; s < Segments; s++)
            {
                int bl = s * rings, br = (s + 1) * rings, tl = s * rings + 1, tr = (s + 1) * rings + 1;
                tris[ti++] = bl; tris[ti++] = tl; tris[ti++] = tr;
                tris[ti++] = bl; tris[ti++] = tr; tris[ti++] = br;
            }

            var mesh = mf.sharedMesh;
            if (mesh == null) { mesh = new Mesh { name = "ph_border_cylinder" }; mf.sharedMesh = mesh; }
            mesh.Clear();
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = tris;
            mesh.RecalculateBounds();
        }
    }
}
