using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Physics shapes taken from a model's own geometry, so the model itself is what collides and what a hand touches:
    /// a gun becomes its stock, receiver, grip, handguard, barrel and sight, each wrapped by a convex hull of its own
    /// vertices, instead of a crate around everything. Moving bodies in PhysX can only use convex shapes, so the model is
    /// cut into pieces first: slices along its longest axis, each slice split where it has a gap (a barrel above a
    /// bipod, a sight on its mount), neighbouring slices with the same cross-section joined. Each piece keeps the
    /// outermost vertices of every slice in 16 directions, and the hull of those follows the real surface (a round
    /// barrel, an angled grip). Readable meshes give their vertices and skinned meshes are baked; a mesh whose data the
    /// game does not let us read joins as its own outline box.
    /// </summary>
    internal static class ShapeFit
    {
        /// <summary>One convex piece of a model, in the space the fit was made in.</summary>
        public sealed class Piece
        {
            public Vector3[] Points;
            public Bounds Bounds;
            private Mesh _mesh;

            /// <summary>Hull source mesh (shared by every copy of the model).</summary>
            public Mesh Mesh
            {
                get
                {
                    if (_mesh == null)
                    {
                        _mesh = new Mesh { name = "RavenfieldVR hull" };
                        _mesh.vertices = Points;
                        // PhysX builds the hull from the vertices; the triangles only make the mesh valid.
                        var tris = new int[Mathf.Max(0, Points.Length - 2) * 3];
                        for (int i = 0; i + 2 < Points.Length; i++)
                        {
                            tris[i * 3] = 0;
                            tris[i * 3 + 1] = i + 1;
                            tris[i * 3 + 2] = i + 2;
                        }
                        _mesh.triangles = tris;
                        _mesh.RecalculateBounds();
                    }
                    return _mesh;
                }
            }
        }

        private const int MaxPointsPerMesh = 30000;
        private const float Cell = 0.01f;
        private const int Directions = 16;

        private static readonly Dictionary<string, Piece[]> Cache = new Dictionary<string, Piece[]>();
        private static readonly List<Vector3> Points = new List<Vector3>(8192);
        private static readonly Vector2[] Dirs = MakeDirs();

        private static Vector2[] MakeDirs()
        {
            var d = new Vector2[Directions];
            for (int i = 0; i < Directions; i++)
            {
                float a = i * Mathf.PI * 2f / Directions;
                d[i] = new Vector2(Mathf.Cos(a), Mathf.Sin(a));
            }
            return d;
        }

        /// <summary>
        /// Convex pieces in the space <paramref name="frameFromWorld"/> maps into, fitted to <paramref name="renderers"/>
        /// (cached under <paramref name="key"/> when given). Empty when there is nothing measurable.
        /// </summary>
        public static Piece[] Fit(string key, IEnumerable<Renderer> renderers, Matrix4x4 frameFromWorld, float slice = 0.03f, int maxPieces = 16)
        {
            if (key != null && Cache.TryGetValue(key, out Piece[] cached))
            {
                return cached;
            }
            Piece[] pieces;
            try
            {
                Points.Clear();
                foreach (Renderer r in renderers)
                {
                    AddPoints(r, frameFromWorld);
                }
                pieces = Points.Count >= 8 ? FitPoints(Points, slice, maxPieces) : new Piece[0];
            }
            catch (Exception e)
            {
                Log.WarnOnce("shapefit:" + key, $"Could not fit a physics shape for {key}: {e.Message}");
                pieces = new Piece[0];
            }
            finally
            {
                Points.Clear();
            }
            if (key != null)
            {
                Cache[key] = pieces;
            }
            return pieces;
        }

        /// <summary>How a renderer's shape can be measured: its vertices, or only its bounds (for the self-test).</summary>
        public static bool Readable(Renderer r)
        {
            if (r is SkinnedMeshRenderer smr)
            {
                return smr.sharedMesh != null && smr.sharedMesh.isReadable;
            }
            MeshFilter mf = r != null ? r.GetComponent<MeshFilter>() : null;
            return mf != null && mf.sharedMesh != null && mf.sharedMesh.isReadable;
        }

        // ---- points --------------------------------------------------------------------------------------

        private static void AddPoints(Renderer r, Matrix4x4 frameFromWorld)
        {
            if (r == null || !r.enabled || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
            {
                return;
            }
            if (r is SkinnedMeshRenderer smr)
            {
                if (smr.sharedMesh == null)
                {
                    return;
                }
                Mesh baked = null;
                try
                {
                    if (!smr.sharedMesh.isReadable)
                    {
                        throw new InvalidOperationException("mesh data not readable");
                    }
                    baked = new Mesh();
                    smr.BakeMesh(baked);
                    if (baked.vertexCount == 0)
                    {
                        throw new InvalidOperationException("nothing baked");
                    }
                    // Baked vertices are in the renderer's space with its scale already applied.
                    Matrix4x4 m = frameFromWorld * Matrix4x4.TRS(smr.transform.position, smr.transform.rotation, Vector3.one);
                    AddVertices(baked.vertices, m);
                }
                catch (Exception)
                {
                    Transform space = smr.rootBone != null ? smr.rootBone : smr.transform;
                    AddBox(smr.localBounds, frameFromWorld * space.localToWorldMatrix);
                }
                finally
                {
                    if (baked != null)
                    {
                        UnityEngine.Object.Destroy(baked);
                    }
                }
                return;
            }
            MeshFilter mf = r.GetComponent<MeshFilter>();
            Mesh mesh = mf != null ? mf.sharedMesh : null;
            if (mesh == null)
            {
                return;
            }
            Matrix4x4 local = frameFromWorld * r.transform.localToWorldMatrix;
            if (mesh.isReadable)
            {
                AddVertices(mesh.vertices, local);
            }
            else
            {
                AddBox(mesh.bounds, local);
            }
        }

        private static void AddVertices(Vector3[] verts, Matrix4x4 m)
        {
            int step = Mathf.Max(1, verts.Length / MaxPointsPerMesh);
            for (int i = 0; i < verts.Length; i += step)
            {
                Points.Add(m.MultiplyPoint3x4(verts[i]));
            }
        }

        /// <summary>A box as lines along its edges, dense enough that every slice it crosses sees its corners.</summary>
        private static void AddBox(Bounds b, Matrix4x4 m)
        {
            Vector3 size = m.MultiplyVector(b.size);
            int n = Mathf.Clamp(Mathf.CeilToInt(size.magnitude / 0.015f), 2, 32);
            Vector3 e = b.extents;
            for (int i = 0; i <= 1; i++)
            {
                for (int j = 0; j <= 1; j++)
                {
                    float si = i == 0 ? -1f : 1f, sj = j == 0 ? -1f : 1f;
                    for (int k = 0; k <= n; k++)
                    {
                        float t = Mathf.Lerp(-1f, 1f, (float)k / n);
                        Points.Add(m.MultiplyPoint3x4(b.center + new Vector3(t * e.x, si * e.y, sj * e.z)));
                        Points.Add(m.MultiplyPoint3x4(b.center + new Vector3(si * e.x, t * e.y, sj * e.z)));
                        Points.Add(m.MultiplyPoint3x4(b.center + new Vector3(si * e.x, sj * e.y, t * e.z)));
                    }
                }
            }
        }

        // ---- fitting -------------------------------------------------------------------------------------

        /// <summary>One gap-free run of cells in one slice, and the piece it ended up in.</summary>
        private struct Segment
        {
            public int C0, C1;
            public float Y0, Y1, X0, X1;
            public int Piece;
        }

        private sealed class Group
        {
            public float Y0, Y1, X0, X1;
            public int LastBin;
            public readonly List<Vector3> Hull = new List<Vector3>();
        }

        private static Piece[] FitPoints(List<Vector3> points, float slice, int maxPieces)
        {
            // Slice along the longest axis ("z"); split each slice along the next longest ("y").
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (Vector3 p in points)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            Vector3 ext = max - min;
            int za = ext.x >= ext.y && ext.x >= ext.z ? 0 : ext.y >= ext.z ? 1 : 2;
            int ya = za == 0 ? (ext.y >= ext.z ? 1 : 2) : za == 1 ? (ext.x >= ext.z ? 0 : 2) : (ext.x >= ext.y ? 0 : 1);
            int xa = 3 - za - ya;

            Piece[] result = null;
            for (int attempt = 0; attempt < 4 && result == null; attempt++)
            {
                List<Group> groups = Slices(points, min, max, xa, ya, za, slice * (1f + attempt), 0.012f * (1f + attempt));
                if (groups.Count <= maxPieces || attempt == 3)
                {
                    var list = new List<Piece>(groups.Count);
                    foreach (Group g in groups)
                    {
                        if (g.Hull.Count < 4)
                        {
                            continue;
                        }
                        var b = new Bounds(g.Hull[0], Vector3.zero);
                        foreach (Vector3 p in g.Hull)
                        {
                            b.Encapsulate(p);
                        }
                        // A flat piece (a decal, one face of a plate) still needs some thickness to be a solid.
                        if (b.size.x < 0.004f || b.size.y < 0.004f || b.size.z < 0.004f)
                        {
                            Vector3 grow = Vector3.Max(Vector3.zero, new Vector3(0.004f, 0.004f, 0.004f) - b.size) * 0.5f;
                            int n = g.Hull.Count;
                            for (int i = 0; i < n; i++)
                            {
                                g.Hull.Add(g.Hull[i] + grow);
                                g.Hull[i] -= grow;
                            }
                            b.Expand(grow * 2f);
                        }
                        list.Add(new Piece { Points = Dedupe(g.Hull), Bounds = b });
                    }
                    result = list.ToArray();
                }
            }
            return result ?? new Piece[0];
        }

        private static Vector3[] Dedupe(List<Vector3> pts)
        {
            var seen = new HashSet<Vector3Int>();
            var keep = new List<Vector3>(pts.Count);
            foreach (Vector3 p in pts)
            {
                if (seen.Add(new Vector3Int(Mathf.RoundToInt(p.x * 2000f), Mathf.RoundToInt(p.y * 2000f), Mathf.RoundToInt(p.z * 2000f))))
                {
                    keep.Add(p);
                }
            }
            return keep.ToArray();
        }

        private static List<Group> Slices(List<Vector3> points, Vector3 min, Vector3 max, int xa, int ya, int za, float slice, float tol)
        {
            int bins = Mathf.Clamp(Mathf.CeilToInt((max[za] - min[za]) / slice), 1, 80);
            int cells = Mathf.Clamp(Mathf.CeilToInt((max[ya] - min[ya]) / Cell) + 1, 1, 200);
            float zSpan = Mathf.Max(max[za] - min[za], 1e-5f);
            var used = new bool[bins, cells];
            var cellMinX = new float[bins, cells];
            var cellMaxX = new float[bins, cells];
            foreach (Vector3 p in points)
            {
                int b = Mathf.Min(bins - 1, (int)((p[za] - min[za]) / zSpan * bins));
                int c = Mathf.Min(cells - 1, (int)((p[ya] - min[ya]) / Cell));
                if (!used[b, c])
                {
                    used[b, c] = true;
                    cellMinX[b, c] = cellMaxX[b, c] = p[xa];
                }
                else
                {
                    cellMinX[b, c] = Mathf.Min(cellMinX[b, c], p[xa]);
                    cellMaxX[b, c] = Mathf.Max(cellMaxX[b, c], p[xa]);
                }
            }

            // Segments per slice, each joined to the piece of a matching segment in the slice before.
            var groups = new List<Group>();
            var segOfCell = new int[bins, cells];
            var segments = new List<Segment>();
            const int gapCells = 2; // a gap of 2 cm or more splits a slice
            for (int b = 0; b < bins; b++)
            {
                for (int c = 0; c < cells; c++)
                {
                    segOfCell[b, c] = -1;
                }
                int start = segments.Count;
                int cc = 0;
                while (cc < cells)
                {
                    if (!used[b, cc])
                    {
                        cc++;
                        continue;
                    }
                    var seg = new Segment { C0 = cc, X0 = cellMinX[b, cc], X1 = cellMaxX[b, cc] };
                    int last = cc;
                    for (int k = cc + 1; k < cells && k - last <= gapCells; k++)
                    {
                        if (used[b, k])
                        {
                            last = k;
                            seg.X0 = Mathf.Min(seg.X0, cellMinX[b, k]);
                            seg.X1 = Mathf.Max(seg.X1, cellMaxX[b, k]);
                        }
                    }
                    seg.C1 = last;
                    seg.Y0 = min[ya] + cc * Cell;
                    seg.Y1 = min[ya] + (last + 1) * Cell;
                    seg.Piece = -1;
                    foreach (Group g in groups)
                    {
                        if (g.LastBin == b - 1 && Mathf.Abs(g.Y0 - seg.Y0) < tol && Mathf.Abs(g.Y1 - seg.Y1) < tol
                            && Mathf.Abs(g.X0 - seg.X0) < tol && Mathf.Abs(g.X1 - seg.X1) < tol)
                        {
                            seg.Piece = groups.IndexOf(g);
                            break;
                        }
                    }
                    if (seg.Piece < 0)
                    {
                        seg.Piece = groups.Count;
                        groups.Add(new Group { Y0 = seg.Y0, Y1 = seg.Y1, X0 = seg.X0, X1 = seg.X1, LastBin = b });
                    }
                    else
                    {
                        Group g = groups[seg.Piece];
                        g.Y0 = Mathf.Min(g.Y0, seg.Y0);
                        g.Y1 = Mathf.Max(g.Y1, seg.Y1);
                        g.X0 = Mathf.Min(g.X0, seg.X0);
                        g.X1 = Mathf.Max(g.X1, seg.X1);
                        g.LastBin = b;
                    }
                    for (int k = seg.C0; k <= seg.C1; k++)
                    {
                        segOfCell[b, k] = segments.Count;
                    }
                    segments.Add(seg);
                    cc = last + 1;
                }
                // A piece already continued by one segment of this slice cannot take a second one.
                for (int i = start; i < segments.Count; i++)
                {
                    groups[segments[i].Piece].LastBin = b;
                }
            }

            // The outermost points of every slice segment, in 16 directions across the slice, plus its two ends.
            var best = new float[segments.Count, Directions + 2];
            var bestPoint = new Vector3[segments.Count, Directions + 2];
            for (int s = 0; s < segments.Count; s++)
            {
                for (int d = 0; d < Directions + 2; d++)
                {
                    best[s, d] = float.MinValue;
                }
            }
            foreach (Vector3 p in points)
            {
                int b = Mathf.Min(bins - 1, (int)((p[za] - min[za]) / zSpan * bins));
                int c = Mathf.Min(cells - 1, (int)((p[ya] - min[ya]) / Cell));
                int s = segOfCell[b, c];
                if (s < 0)
                {
                    continue;
                }
                for (int d = 0; d < Directions; d++)
                {
                    float v = p[xa] * Dirs[d].x + p[ya] * Dirs[d].y;
                    if (v > best[s, d])
                    {
                        best[s, d] = v;
                        bestPoint[s, d] = p;
                    }
                }
                if (-p[za] > best[s, Directions])
                {
                    best[s, Directions] = -p[za];
                    bestPoint[s, Directions] = p;
                }
                if (p[za] > best[s, Directions + 1])
                {
                    best[s, Directions + 1] = p[za];
                    bestPoint[s, Directions + 1] = p;
                }
            }
            for (int s = 0; s < segments.Count; s++)
            {
                List<Vector3> hull = groups[segments[s].Piece].Hull;
                for (int d = 0; d < Directions + 2; d++)
                {
                    if (best[s, d] > float.MinValue)
                    {
                        hull.Add(bestPoint[s, d]);
                    }
                }
            }
            return groups;
        }

        /// <summary>
        /// One convex MeshCollider per piece on <paramref name="go"/> (pieces in its local space). Pieces too small or too
        /// flat for a hull get a box instead.
        /// </summary>
        public static List<Collider> AddColliders(GameObject go, Piece[] pieces)
        {
            var list = new List<Collider>(pieces.Length);
            foreach (Piece p in pieces)
            {
                Collider c = null;
                if (p.Points.Length >= 4)
                {
                    try
                    {
                        var mc = go.AddComponent<MeshCollider>();
                        mc.convex = true;
                        mc.sharedMesh = p.Mesh;
                        c = mc;
                    }
                    catch (Exception)
                    {
                        c = null;
                    }
                }
                if (c == null)
                {
                    var box = go.AddComponent<BoxCollider>();
                    box.center = p.Bounds.center;
                    box.size = Vector3.Max(p.Bounds.size, new Vector3(0.004f, 0.004f, 0.004f));
                    c = box;
                }
                list.Add(c);
            }
            return list;
        }

        /// <summary>A single box piece (fallback shapes).</summary>
        public static Piece BoxPiece(Vector3 center, Vector3 size)
        {
            Vector3 e = size * 0.5f;
            var pts = new Vector3[8];
            for (int i = 0; i < 8; i++)
            {
                pts[i] = center + new Vector3((i & 1) == 0 ? -e.x : e.x, (i & 2) == 0 ? -e.y : e.y, (i & 4) == 0 ? -e.z : e.z);
            }
            return new Piece { Points = pts, Bounds = new Bounds(center, size) };
        }

        public static void ClearCache() => Cache.Clear();
    }
}
