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
            /// <summary>PhysX could not make a hull of it once: it is a box from then on.</summary>
            public bool CookFailed;
            /// <summary>Its hull is cooked (the pieces are kept per weapon type: cooked again on every draw before; 255.P6-1).</summary>
            public bool Baked;
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

        private const int MaxPointsPerMesh = 8000;
        private static readonly List<Vector3> VertexBuffer = new List<Vector3>(8192);
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

        /// <summary>The pieces fitted earlier under <paramref name="key"/>, if any.</summary>
        internal static bool TryCached(string key, out Piece[] pieces) => Cache.TryGetValue(key, out pieces);

        /// <summary>Keeps <paramref name="pieces"/> under <paramref name="key"/> (fitted elsewhere, from <see cref="Gather"/>'s points).</summary>
        internal static void Store(string key, Piece[] pieces) => Cache[key] = pieces;

        /// <summary>Forgets the pieces kept under <paramref name="key"/> (autotest).</summary>
        internal static void Forget(string key) => Cache.Remove(key);

        /// <summary>
        /// The points <see cref="Fit"/> would fit, in the space <paramref name="frameFromWorld"/> maps into, copied out for a fit on a
        /// worker thread (<see cref="FitCloud"/>). Main thread: it reads the meshes.
        /// </summary>
        public static List<Vector3> Gather(IEnumerable<Renderer> renderers, Matrix4x4 frameFromWorld)
        {
            try
            {
                Points.Clear();
                foreach (Renderer r in renderers)
                {
                    AddPoints(r, frameFromWorld);
                }
                return new List<Vector3>(Points);
            }
            finally
            {
                Points.Clear();
            }
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
            // Timed on its own in a profiled run (where a long call in the weapons' steps comes from; 2026-10-21).
            if (!Core.PerfProbe.Enabled)
            {
                return FitNow(key, renderers, frameFromWorld, slice, maxPieces);
            }
            Core.PerfProbe.Begin("shape fitted");
            try
            {
                return FitNow(key, renderers, frameFromWorld, slice, maxPieces);
            }
            finally
            {
                Core.PerfProbe.End("shape fitted");
            }
        }

        private static Piece[] FitNow(string key, IEnumerable<Renderer> renderers, Matrix4x4 frameFromWorld, float slice, int maxPieces)
        {
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

        /// <summary>
        /// Convex pieces fitted to a point cloud (already in the fit's frame). Pure arithmetic: safe on a worker thread.
        /// </summary>
        public static Piece[] FitCloud(List<Vector3> points, float slice = 0.03f, int maxPieces = 16) =>
            points.Count >= 8 ? FitPoints(points, slice, maxPieces) : new Piece[0];

        /// <summary>
        /// Cooks the hulls now (PhysX keeps the result in each mesh, and every collider made from it later reuses it), so
        /// creating the colliders in a match costs no cooking. Pieces PhysX cannot cook are marked and become boxes.
        /// </summary>
        public static void Bake(Piece[] pieces)
        {
            foreach (Piece p in pieces)
            {
                if (p.Points.Length < 4 || p.CookFailed || p.Baked)
                {
                    continue;
                }
                try
                {
                    Physics.BakeMesh(p.Mesh.GetInstanceID(), true);
                    p.Baked = true;
                }
                catch (Exception)
                {
                    p.CookFailed = true;
                }
            }
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
                    baked.GetVertices(VertexBuffer);
                    AddVertices(VertexBuffer, m);
                    VertexBuffer.Clear();
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
                // Into one reused buffer: Mesh.vertices would allocate a fresh copy of every vertex.
                mesh.GetVertices(VertexBuffer);
                AddVertices(VertexBuffer, local);
                VertexBuffer.Clear();
            }
            else
            {
                AddBox(mesh.bounds, local);
            }
        }

        private static void AddVertices(List<Vector3> verts, Matrix4x4 m)
        {
            int step = Mathf.Max(1, verts.Count / MaxPointsPerMesh);
            for (int i = 0; i < verts.Count; i += step)
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
                        Vector3[] pts = Solid(Thin(Dedupe(g.Hull), 180));
                        if (pts.Length > 255)
                        {
                            // A thin piece is doubled out along its thin axes (Solid): 180 points became up to 360, past the 255 a
                            // convex hull may have, and its cooking failed, a box instead (gun-volume scout, 2026-10-24).
                            pts = Thin(pts, 250);
                        }
                        var b = new Bounds(pts[0], Vector3.zero);
                        foreach (Vector3 p in pts)
                        {
                            b.Encapsulate(p);
                        }
                        list.Add(new Piece { Points = pts, Bounds = b });
                    }
                    result = list.ToArray();
                }
            }
            return result ?? new Piece[0];
        }

        /// <summary>
        /// At most <paramref name="max"/> of the points, chosen so that their hull is, to a fraction of a millimetre, the hull
        /// of all of them: for each of <paramref name="max"/> directions spread evenly over the sphere, the point farthest
        /// along it. Taking every n-th point lost the outermost ones (the top end of each slice, where the stride fell), so a
        /// piece's hull stopped short of the model: a magazine's hitbox sat low and did not cover the whole magazine (user,
        /// 2026-10-18, task 260 b).
        /// </summary>
        private static Vector3[] Thin(Vector3[] pts, int max)
        {
            if (pts.Length <= max)
            {
                return pts;
            }
            var keep = new List<Vector3>(max);
            var taken = new bool[pts.Length];
            Vector3[] dirs = ThinDirections(max);
            for (int i = 0; i < max; i++)
            {
                Vector3 dir = dirs[i];
                int best = 0;
                float bestDot = float.NegativeInfinity;
                for (int k = 0; k < pts.Length; k++)
                {
                    // Vector3.Dot's own sum, written out (the call per point per direction was most of a thinning).
                    Vector3 p = pts[k];
                    float dot = p.x * dir.x + p.y * dir.y + p.z * dir.z;
                    if (dot > bestDot)
                    {
                        bestDot = dot;
                        best = k;
                    }
                }
                if (!taken[best])
                {
                    taken[best] = true;
                    keep.Add(pts[best]);
                }
            }
            return keep.ToArray();
        }

        private static Vector3[] _thinDirs, _thinDirsBefore;

        /// <summary>
        /// <paramref name="max"/> directions on a Fibonacci sphere, evenly spread, one per point to keep: made once for each count
        /// in use (they were made again, with a square root, a sine and a cosine each, for every piece). Two counts are: 180 for
        /// every piece and 250 for a doubled one thinned again, so the last two are kept (one kept, each doubled piece made both
        /// again; review, 2026-10-24). Fits also run on worker threads: two that make them at once make the same directions, and
        /// each array is published whole.
        /// </summary>
        private static Vector3[] ThinDirections(int max)
        {
            Vector3[] dirs = _thinDirs, before = _thinDirsBefore;
            if (dirs != null && dirs.Length == max)
            {
                return dirs;
            }
            if (before != null && before.Length == max)
            {
                return before;
            }
            dirs = new Vector3[max];
            for (int i = 0; i < max; i++)
            {
                float y = 1f - 2f * (i + 0.5f) / max;
                float r = Mathf.Sqrt(Mathf.Max(0f, 1f - y * y));
                float a = i * 2.39996323f;
                dirs[i] = new Vector3(r * Mathf.Cos(a), y, r * Mathf.Sin(a));
            }
            _thinDirsBefore = _thinDirs;
            _thinDirs = dirs;
            return dirs;
        }

        private const float MinThickness = 0.004f;

        /// <summary>
        /// A point set that spans a real volume. Points lying (almost) on a plane or a line, in any orientation, make PhysX's
        /// convex cooking fail, and a collider without a shape reports every point as touching it. The set is measured along
        /// its principal axes and doubled out along each one that is thinner than a few millimetres.
        /// </summary>
        private static Vector3[] Solid(Vector3[] pts)
        {
            Vector3 mean = Vector3.zero;
            foreach (Vector3 p in pts)
            {
                mean += p;
            }
            mean /= Mathf.Max(1, pts.Length);
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (Vector3 p in pts)
            {
                Vector3 d = p - mean;
                xx += d.x * d.x; xy += d.x * d.y; xz += d.x * d.z;
                yy += d.y * d.y; yz += d.y * d.z; zz += d.z * d.z;
            }
            Vector3[] axes = PrincipalAxes(xx, xy, xz, yy, yz, zz);
            var result = new List<Vector3>(pts);
            foreach (Vector3 axis in axes)
            {
                float min = float.MaxValue, max = float.MinValue;
                foreach (Vector3 p in result)
                {
                    float v = Vector3.Dot(p - mean, axis);
                    min = Mathf.Min(min, v);
                    max = Mathf.Max(max, v);
                }
                if (max - min >= MinThickness)
                {
                    continue;
                }
                Vector3 grow = axis * (MinThickness - (max - min)) * 0.5f;
                int n = result.Count;
                for (int i = 0; i < n; i++)
                {
                    result.Add(result[i] + grow);
                    result[i] -= grow;
                }
            }
            return result.ToArray();
        }

        /// <summary>Eigenvectors of a symmetric 3x3 covariance (Jacobi rotations), as unit vectors.</summary>
        private static Vector3[] PrincipalAxes(float xx, float xy, float xz, float yy, float yz, float zz)
        {
            var a = new double[3, 3] { { xx, xy, xz }, { xy, yy, yz }, { xz, yz, zz } };
            var v = new double[3, 3] { { 1, 0, 0 }, { 0, 1, 0 }, { 0, 0, 1 } };
            for (int sweep = 0; sweep < 12; sweep++)
            {
                for (int p = 0; p < 2; p++)
                {
                    for (int q = p + 1; q < 3; q++)
                    {
                        if (Math.Abs(a[p, q]) < 1e-12)
                        {
                            continue;
                        }
                        double theta = (a[q, q] - a[p, p]) / (2 * a[p, q]);
                        double t = Math.Sign(theta) / (Math.Abs(theta) + Math.Sqrt(theta * theta + 1));
                        if (theta == 0)
                        {
                            t = 1;
                        }
                        double c = 1 / Math.Sqrt(t * t + 1), s = t * c;
                        for (int k = 0; k < 3; k++)
                        {
                            double akp = a[k, p], akq = a[k, q];
                            a[k, p] = c * akp - s * akq;
                            a[k, q] = s * akp + c * akq;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double apk = a[p, k], aqk = a[q, k];
                            a[p, k] = c * apk - s * aqk;
                            a[q, k] = s * apk + c * aqk;
                        }
                        for (int k = 0; k < 3; k++)
                        {
                            double vkp = v[k, p], vkq = v[k, q];
                            v[k, p] = c * vkp - s * vkq;
                            v[k, q] = s * vkp + c * vkq;
                        }
                    }
                }
            }
            var axes = new Vector3[3];
            for (int i = 0; i < 3; i++)
            {
                var axis = new Vector3((float)v[0, i], (float)v[1, i], (float)v[2, i]);
                axes[i] = axis.sqrMagnitude > 1e-8f ? axis.normalized : (i == 0 ? Vector3.right : i == 1 ? Vector3.up : Vector3.forward);
            }
            return axes;
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
            float minZ = min[za], minY = min[ya];
            // Each point's three axes and its slice cell, read once: the Vector3 indexer (a switch) and the [,] arrays (a call per
            // access on Mono) were most of a fit's time, and a fit runs on the main thread when a weapon is let go (the same
            // arithmetic, so the same pieces: tools/tests/ShapeFitBench.cs checks every point's bits).
            int n = points.Count;
            float[] px = new float[n], py = new float[n], pz = new float[n];
            int[] cellOf = new int[n];
            for (int i = 0; i < n; i++)
            {
                Vector3 p = points[i];
                px[i] = p[xa];
                py[i] = p[ya];
                pz[i] = p[za];
                int b = Mathf.Min(bins - 1, (int)((pz[i] - minZ) / zSpan * bins));
                int c = Mathf.Min(cells - 1, (int)((py[i] - minY) / Cell));
                cellOf[i] = b * cells + c;
            }
            var used = new bool[bins * cells];
            var cellMinX = new float[bins * cells];
            var cellMaxX = new float[bins * cells];
            for (int i = 0; i < n; i++)
            {
                int k = cellOf[i];
                if (!used[k])
                {
                    used[k] = true;
                    cellMinX[k] = cellMaxX[k] = px[i];
                }
                else
                {
                    cellMinX[k] = Mathf.Min(cellMinX[k], px[i]);
                    cellMaxX[k] = Mathf.Max(cellMaxX[k], px[i]);
                }
            }

            // Segments per slice, each joined to the piece of a matching segment in the slice before.
            var groups = new List<Group>();
            var segOfCell = new int[bins * cells];
            var segments = new List<Segment>();
            const int gapCells = 2; // a gap of 2 cm or more splits a slice
            for (int b = 0; b < bins; b++)
            {
                int row = b * cells;
                for (int c = 0; c < cells; c++)
                {
                    segOfCell[row + c] = -1;
                }
                int start = segments.Count;
                int cc = 0;
                while (cc < cells)
                {
                    if (!used[row + cc])
                    {
                        cc++;
                        continue;
                    }
                    var seg = new Segment { C0 = cc, X0 = cellMinX[row + cc], X1 = cellMaxX[row + cc] };
                    int last = cc;
                    for (int k = cc + 1; k < cells && k - last <= gapCells; k++)
                    {
                        if (used[row + k])
                        {
                            last = k;
                            seg.X0 = Mathf.Min(seg.X0, cellMinX[row + k]);
                            seg.X1 = Mathf.Max(seg.X1, cellMaxX[row + k]);
                        }
                    }
                    seg.C1 = last;
                    seg.Y0 = min[ya] + cc * Cell;
                    seg.Y1 = min[ya] + (last + 1) * Cell;
                    seg.Piece = -1;
                    for (int gi = 0; gi < groups.Count; gi++)
                    {
                        Group g = groups[gi];
                        if (g.LastBin == b - 1 && Mathf.Abs(g.Y0 - seg.Y0) < tol && Mathf.Abs(g.Y1 - seg.Y1) < tol
                            && Mathf.Abs(g.X0 - seg.X0) < tol && Mathf.Abs(g.X1 - seg.X1) < tol)
                        {
                            seg.Piece = gi;
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
                        segOfCell[row + k] = segments.Count;
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

            // The outermost points of every slice segment, in 16 directions across the slice, plus its two ends, and how far
            // along the slice its points reach.
            const int slots = Directions + 2;
            var best = new float[segments.Count * slots];
            var bestPoint = new int[segments.Count * slots];
            var zLo = new float[segments.Count];
            var zHi = new float[segments.Count];
            for (int s = 0; s < segments.Count; s++)
            {
                for (int d = 0; d < slots; d++)
                {
                    best[s * slots + d] = float.MinValue;
                }
                zLo[s] = float.MaxValue;
                zHi[s] = float.MinValue;
            }
            for (int i = 0; i < n; i++)
            {
                int s = segOfCell[cellOf[i]];
                if (s < 0)
                {
                    continue;
                }
                float x = px[i], y = py[i], z = pz[i];
                zLo[s] = Mathf.Min(zLo[s], z);
                zHi[s] = Mathf.Max(zHi[s], z);
                int at = s * slots;
                for (int d = 0; d < Directions; d++)
                {
                    float v = x * Dirs[d].x + y * Dirs[d].y;
                    if (v > best[at + d])
                    {
                        best[at + d] = v;
                        bestPoint[at + d] = i;
                    }
                }
                if (-z > best[at + Directions])
                {
                    best[at + Directions] = -z;
                    bestPoint[at + Directions] = i;
                }
                if (z > best[at + Directions + 1])
                {
                    best[at + Directions + 1] = z;
                    bestPoint[at + Directions + 1] = i;
                }
            }
            // A piece's first and last slices (its segments come in slice order, one a slice).
            var firstSeg = new int[groups.Count];
            var lastSeg = new int[groups.Count];
            for (int g = 0; g < groups.Count; g++)
            {
                firstSeg[g] = -1;
            }
            for (int s = 0; s < segments.Count; s++)
            {
                int g = segments[s].Piece;
                if (firstSeg[g] < 0)
                {
                    firstSeg[g] = s;
                }
                lastSeg[g] = s;
            }
            for (int s = 0; s < segments.Count; s++)
            {
                List<Vector3> hull = groups[segments[s].Piece].Hull;
                int at = s * slots;
                for (int d = 0; d < slots; d++)
                {
                    if (best[at + d] > float.MinValue)
                    {
                        hull.Add(points[bestPoint[at + d]]);
                    }
                }
                // At a piece's two ends, each outermost point of its end slice also at that slice's outer end: the piece's hull
                // then keeps its outline out to its ends. The outermost points lie wherever they fell along their slice, so the
                // hull narrowed toward a piece's ends: a gun-like cloud had 148 of 2600 points outside every piece, a
                // receiver's end face up to 13 mm out (offline check, 2026-10-22). Between its slices a piece's hull holds the
                // outline already (it spans one slice's outermost points to the next one's).
                bool atStart = s == firstSeg[segments[s].Piece], atEnd = s == lastSeg[segments[s].Piece];
                if ((atStart || atEnd) && zLo[s] <= zHi[s])
                {
                    for (int d = 0; d < Directions; d++)
                    {
                        if (best[at + d] > float.MinValue)
                        {
                            Vector3 q = points[bestPoint[at + d]];
                            if (atStart)
                            {
                                q[za] = zLo[s];
                                hull.Add(q);
                            }
                            if (atEnd)
                            {
                                q[za] = zHi[s];
                                hull.Add(q);
                            }
                        }
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
                if (p.Points.Length >= 4 && !p.CookFailed)
                {
                    try
                    {
                        var mc = go.AddComponent<MeshCollider>();
                        mc.convex = true;
                        mc.sharedMesh = p.Mesh;
                        // Cooking failed (PhysX logs it): the collider has no shape and would touch everything. Its box
                        // instead, and never try that piece again.
                        if (mc.sharedMesh == null || mc.bounds.size.sqrMagnitude < 1e-10f && go.activeInHierarchy && mc.enabled)
                        {
                            p.CookFailed = true;
                            UnityEngine.Object.DestroyImmediate(mc);
                        }
                        else
                        {
                            c = mc;
                        }
                    }
                    catch (Exception)
                    {
                        p.CookFailed = true;
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
