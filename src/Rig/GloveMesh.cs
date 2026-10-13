using System.Collections.Generic;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Builds the glove as one skinned mesh: a lofted palm and back of the hand (rounded cross-sections from the cuff to
    /// the knuckles) and a continuous tube per finger whose joint rings are shared between the two bones they connect,
    /// so the glove bends without seams. One renderer and one material for the whole hand (the primitive version needed
    /// eighteen), and a fabric weave normal map for a matte, stitched look up close.
    /// </summary>
    internal static class GloveMesh
    {
        private const int Around = 10;
        private const float Roundness = 2.6f; // superellipse exponent: 2 = ellipse, higher = squarer

        private static Texture2D _weave;

        private sealed class Builder
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector3> N = new List<Vector3>();
            public readonly List<Vector2> UV = new List<Vector2>();
            public readonly List<BoneWeight> W = new List<BoneWeight>();
            public readonly List<int> T = new List<int>();

            /// <summary>A ring of <see cref="Around"/>+1 vertices (the last repeats the first for the texture seam).</summary>
            public int Ring(Vector3 centre, Vector3 xAxis, Vector3 yAxis, float a, float b, float v, BoneWeight w, Vector3 bias)
            {
                int start = V.Count;
                for (int i = 0; i <= Around; i++)
                {
                    float t = (float)i / Around * Mathf.PI * 2f;
                    float c = Mathf.Cos(t), s = Mathf.Sin(t);
                    float px = a * Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), 2f / Roundness);
                    float py = b * Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), 2f / Roundness);
                    float nx = Mathf.Sign(c) * Mathf.Pow(Mathf.Abs(c), 2f - 2f / Roundness) / Mathf.Max(a, 1e-4f);
                    float ny = Mathf.Sign(s) * Mathf.Pow(Mathf.Abs(s), 2f - 2f / Roundness) / Mathf.Max(b, 1e-4f);
                    V.Add(centre + xAxis * px + yAxis * py);
                    N.Add((xAxis * nx + yAxis * ny).normalized * 0.85f + bias * 0.15f);
                    UV.Add(new Vector2((float)i / Around * 2f, v));
                    W.Add(w);
                }
                return start;
            }

            /// <summary>
            /// Quads between two rings, facing outward for rings that run counter-clockwise (x toward y) and follow each
            /// other along +z of their frame; <paramref name="flip"/> turns them for rings running the other way.
            /// </summary>
            public void Bridge(int r0, int r1, bool flip)
            {
                for (int i = 0; i < Around; i++)
                {
                    int a = r0 + i, b = r0 + i + 1, c = r1 + i, d = r1 + i + 1;
                    if (flip)
                    {
                        T.Add(a); T.Add(c); T.Add(b);
                        T.Add(b); T.Add(c); T.Add(d);
                    }
                    else
                    {
                        T.Add(a); T.Add(b); T.Add(c);
                        T.Add(b); T.Add(d); T.Add(c);
                    }
                }
            }

            /// <summary>Closes a ring with a single centre vertex: facing +z of the ring's frame, or -z when <paramref name="flip"/>.</summary>
            public void Cap(int ring, Vector3 tip, Vector3 normal, float v, BoneWeight w, bool flip)
            {
                int centre = V.Count;
                V.Add(tip);
                N.Add(normal);
                UV.Add(new Vector2(0.5f, v));
                W.Add(w);
                for (int i = 0; i < Around; i++)
                {
                    if (flip)
                    {
                        T.Add(ring + i); T.Add(centre); T.Add(ring + i + 1);
                    }
                    else
                    {
                        T.Add(ring + i); T.Add(ring + i + 1); T.Add(centre);
                    }
                }
            }
        }

        private static BoneWeight One(int bone) => new BoneWeight { boneIndex0 = bone, weight0 = 1f };

        private static BoneWeight Two(int a, int b, float wb) =>
            new BoneWeight { boneIndex0 = a, weight0 = 1f - wb, boneIndex1 = b, weight1 = wb };

        /// <summary>
        /// The glove in <paramref name="root"/>'s space, skinned to bone 0 (<paramref name="root"/>, the palm) and bones
        /// 1 + finger * 3 + segment (<paramref name="joints"/>). <paramref name="mirror"/> is -1 for a left hand.
        /// </summary>
        public static Mesh Build(Transform root, Transform[][] joints, float[][] lengths, float[] radii, float mirror)
        {
            var g = new Builder();

            // Palm and back of the hand, from the cuff to the knuckles: (z, half width across the knuckles, half
            // thickness from palm to back, x offset of the centre toward the back of the hand).
            float[,] palm =
            {
                { -0.096f, 0.027f, 0.022f, 0.003f },
                { -0.078f, 0.026f, 0.020f, 0.003f },
                { -0.058f, 0.025f, 0.016f, 0.003f }, // wrist
                { -0.036f, 0.032f, 0.0135f, 0.002f },
                { -0.010f, 0.038f, 0.0135f, 0.0015f },
                { 0.018f, 0.040f, 0.0125f, 0.001f },
                { 0.040f, 0.039f, 0.011f, 0.001f }, // knuckles
                { 0.052f, 0.034f, 0.0085f, 0.000f }
            };
            int prev = -1;
            int first = -1;
            Vector3 xAxis = Vector3.right, yAxis = Vector3.up;
            for (int i = 0; i < palm.GetLength(0); i++)
            {
                var centre = new Vector3(palm[i, 3] * mirror, 0f, palm[i, 0]);
                // The ring's local X is the hand's thickness, Y its width (the superellipse makes a flat, rounded box).
                int ring = g.Ring(centre, xAxis, yAxis, palm[i, 2], palm[i, 1], i * 0.6f, One(0), Vector3.zero);
                if (prev >= 0)
                {
                    g.Bridge(prev, ring, false);
                }
                else
                {
                    first = ring;
                }
                prev = ring;
            }
            g.Cap(first, new Vector3(0.004f * mirror, 0f, -0.108f), Vector3.back, 0f, One(0), true);
            g.Cap(prev, new Vector3(0f, 0f, 0.056f), Vector3.forward, 5f, One(0), false);

            // Fingers: a tube from the knuckle (inside the palm) through every joint to a rounded tip.
            for (int f = 0; f < 5; f++)
            {
                prev = -1;
                float v = 0f;
                for (int s = 0; s < 3; s++)
                {
                    Transform j = joints[f][s];
                    int bone = 1 + f * 3 + s;
                    Vector3 o = root.InverseTransformPoint(j.position);
                    Vector3 fwd = root.InverseTransformDirection(j.forward);
                    Vector3 jx = root.InverseTransformDirection(j.right);
                    Vector3 jy = root.InverseTransformDirection(j.up);
                    float r = radii[f] * (1f - 0.12f * s);
                    float rNext = radii[f] * (1f - 0.12f * Mathf.Min(s + 1, 2));
                    float len = lengths[f][s];
                    // Knuckles sit a little proud; a finger is slightly flatter on its pad side.
                    float bulge = s == 0 ? 1.12f : 1.04f;
                    if (s == 0)
                    {
                        // The finger grows out of the palm: a root ring inside the hand on the palm bone, so a curled
                        // finger stays joined to the knuckle instead of swinging free of it.
                        prev = g.Ring(o - fwd * 0.024f, jx, jy, r * 1.05f, r * 1.1f, v - 0.5f, One(0), Vector3.zero);
                    }
                    // Joint ring shared with the bone before (the palm at the knuckle): no seam where the finger bends.
                    BoneWeight atJoint = Two(s == 0 ? 0 : bone - 1, bone, 0.5f);
                    int r0 = g.Ring(o, jx, jy, r * bulge * 0.92f, r * bulge, v, atJoint, Vector3.zero);
                    g.Bridge(prev, r0, false);
                    int r1 = g.Ring(o + fwd * (len * 0.5f), jx, jy, Mathf.Lerp(r, rNext, 0.5f) * 0.9f, Mathf.Lerp(r, rNext, 0.5f) * 0.97f, v + 0.5f, One(bone), Vector3.zero);
                    g.Bridge(r0, r1, false);
                    prev = r1;
                    v += 1f;
                    if (s == 2)
                    {
                        // Rounded fingertip.
                        int bone2 = bone;
                        int r2 = g.Ring(o + fwd * len, jx, jy, rNext * 0.88f, rNext * 0.94f, v, One(bone2), fwd);
                        g.Bridge(prev, r2, false);
                        int r3 = g.Ring(o + fwd * (len + rNext * 0.55f), jx, jy, rNext * 0.68f, rNext * 0.72f, v + 0.3f, One(bone2), fwd);
                        g.Bridge(r2, r3, false);
                        g.Cap(r3, o + fwd * (len + rNext * 0.95f), fwd, v + 0.5f, One(bone2), false);
                    }
                }
            }

            var mesh = new Mesh { name = "RavenfieldVR Glove" };
            mesh.SetVertices(g.V);
            mesh.SetNormals(g.N);
            mesh.SetUVs(0, g.UV);
            mesh.SetTriangles(g.T, 0);
            mesh.boneWeights = g.W.ToArray();
            mesh.RecalculateTangents();
            mesh.RecalculateBounds();
            return mesh;
        }

        /// <summary>A small fabric weave as a tangent-space normal map (RGB normal, alpha 1, linear).</summary>
        public static Texture2D Weave()
        {
            if (_weave != null)
            {
                return _weave;
            }
            const int size = 64;
            var heights = new float[size, size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Over-under threads: two crossing sine bands, alternating which lies on top every cell.
                    float u = x * Mathf.PI * 2f / 8f, w = y * Mathf.PI * 2f / 8f;
                    bool over = ((x / 4) + (y / 4)) % 2 == 0;
                    float warp = Mathf.Abs(Mathf.Sin(u)), weft = Mathf.Abs(Mathf.Sin(w));
                    heights[x, y] = over ? warp * 0.7f + weft * 0.3f : weft * 0.7f + warp * 0.3f;
                }
            }
            _weave = new Texture2D(size, size, TextureFormat.RGBA32, true, true) { name = "RavenfieldVR Glove Weave", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Trilinear, anisoLevel = 4 };
            var pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = heights[(x + 1) % size, y] - heights[(x + size - 1) % size, y];
                    float dy = heights[x, (y + 1) % size] - heights[x, (y + size - 1) % size];
                    Vector3 n = new Vector3(-dx * 1.4f, -dy * 1.4f, 1f).normalized;
                    pixels[y * size + x] = new Color(n.x * 0.5f + 0.5f, n.y * 0.5f + 0.5f, n.z * 0.5f + 0.5f, 1f);
                }
            }
            _weave.SetPixels(pixels);
            _weave.Apply(true, true);
            return _weave;
        }
    }
}
