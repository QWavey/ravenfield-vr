using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The loose end of a machine-gun belt while it is being laid: rounds side by side, pointing down the barrel,
    /// running from the mouth of the box to the hand that pulls it, sagging a little in between. One mesh (two materials):
    /// two draw calls for the whole strip, not two per round.
    /// </summary>
    internal sealed class BeltStrip
    {
        private const int MaxRounds = 36; // enough for the whole length the belt comes out of the box (38 cm held, at 1.15 cm)
        private const float Pitch = 0.0115f; // link spacing of a rifle-calibre belt

        private static Mesh _round;
        private static Material _brass, _link;
        // The single round the strip is made of: corners, normals and the two parts' triangles.
        private static Vector3[] _roundVerts, _roundNormals;
        private static int[] _roundTris, _linkTris;

        private readonly GameObject _root;
        private readonly Mesh _mesh;
        private readonly Vector3[] _verts, _normals;
        // The last placement, so a still belt (hanging or laid) is not rebuilt and re-uploaded every frame.
        private Vector3 _lastFrom, _lastTo, _lastBarrel;
        private int _lastRounds = -1, _lastCount;
        private bool _lastFromMouth;
        private bool _built;

        public BeltStrip()
        {
            EnsureAssets();
            _root = new GameObject("RavenfieldVR Belt") { layer = MagazineProp.DisplayLayer };
            int n = _roundVerts.Length;
            _verts = new Vector3[n * MaxRounds];
            _normals = new Vector3[n * MaxRounds];
            var round = new int[_roundTris.Length * MaxRounds];
            var link = new int[_linkTris.Length * MaxRounds];
            for (int i = 0; i < MaxRounds; i++)
            {
                for (int k = 0; k < _roundTris.Length; k++)
                {
                    round[i * _roundTris.Length + k] = _roundTris[k] + i * n;
                }
                for (int k = 0; k < _linkTris.Length; k++)
                {
                    link[i * _linkTris.Length + k] = _linkTris[k] + i * n;
                }
            }
            _mesh = new Mesh { name = "RavenfieldVR belt strip" };
            _mesh.MarkDynamic();
            _mesh.vertices = _verts;
            _mesh.normals = _normals;
            _mesh.subMeshCount = 2;
            _mesh.SetTriangles(round, 0);
            _mesh.SetTriangles(link, 1);
            // A box big enough for the whole belt around the box mouth (its reach, about 0.38 m, plus a generous margin so it
            // is never culled early), set once: the rounds are placed in this local frame, so a per-frame bounds recompute
            // would only re-derive the same reach.
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 1.2f);
            _root.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = _root.AddComponent<MeshRenderer>();
            mr.sharedMaterials = new[] { _brass, _link };
            mr.shadowCastingMode = ShadowCastingMode.Off;
            _root.SetActive(false);
        }

        public void Show(bool visible)
        {
            if (_root != null && _root.activeSelf != visible)
            {
                _root.SetActive(visible);
            }
        }

        /// <summary>
        /// Lays the rounds along a slightly sagging curve from <paramref name="from"/> to <paramref name="to"/>, at most
        /// <paramref name="rounds"/> of them (what is left in the box): a short belt ends short of the box, or with
        /// <paramref name="fromMouth"/> (hanging or laid, no hand at its end) starts at the box and ends short of the far end.
        /// A round that would lie in <paramref name="keepOutBox"/> (in <paramref name="keepOut"/>'s frame: the seated ammunition
        /// box) is pushed out through its nearest face (<see cref="KeptOut"/>): the belt lies over the box, not through it. With
        /// <paramref name="via"/> the strip bends toward it on the way (a quadratic curve: out over the box's side, then down).
        /// </summary>
        public void Place(Vector3 from, Vector3 to, Vector3 barrel, int rounds = MaxRounds, bool fromMouth = false,
            Runtime.RigidPose? keepOut = null, Bounds keepOutBox = default, Vector3? via = null)
        {
            Vector3 bend = via.HasValue ? via.Value - from : Vector3.zero;
            // A belt whose shape has not changed keeps its last geometry: it is built round the box mouth, so a gun carried
            // along (walking, or held still) only moves it there. Within a millimetre and half a degree: compared exactly, any
            // move of the gun in the hand rebuilt and re-uploaded the whole strip every frame (255.P1.6).
            if (_built && rounds == _lastRounds && fromMouth == _lastFromMouth && ((to - from) - (_lastTo - _lastFrom)).sqrMagnitude < 1e-6f
                && (bend - _lastBend).sqrMagnitude < 1e-6f
                && Vector3.Dot(barrel.normalized, _lastBarrel.normalized) > 0.99996f)
            {
                if (!Same(from, _lastFrom))
                {
                    _root.transform.position = from;
                    // Where it now is, its built shape carried along: left at the build's place, a gun back at exactly that
                    // place left the strip where it last was (scout, 2026-10-22).
                    _lastTo += from - _lastFrom;
                    _lastFrom = from;
                }
                return;
            }
            _built = true;
            _lastFrom = from;
            _lastTo = to;
            _lastBarrel = barrel;
            _lastRounds = rounds;
            _lastFromMouth = fromMouth;
            _lastBend = bend;
            Vector3 span = to - from;
            float length = span.magnitude;
            int count = Mathf.Min(Mathf.Clamp(Mathf.CeilToInt(length / Pitch), 2, MaxRounds), Mathf.Max(0, rounds));
            Vector3 sag = Vector3.down * Mathf.Min(0.04f, length * 0.12f);
            Vector3 forward = Vector3.ProjectOnPlane(barrel, span).sqrMagnitude > 1e-6f ? Vector3.ProjectOnPlane(barrel, span).normalized : barrel;
            Quaternion rot = Quaternion.LookRotation(forward, Vector3.Cross(forward, span.sqrMagnitude > 1e-8f ? span.normalized : Vector3.up));
            // Built round the box mouth (small numbers anywhere on the map), in world directions.
            _root.transform.SetPositionAndRotation(from, Quaternion.identity);
            int n = _roundVerts.Length;
            for (int i = 0; i < count; i++)
            {
                int b = i * n;
                float t = RoundAt(i, count, length, fromMouth);
                Vector3 p = (via.HasValue ? 2f * (1f - t) * t * bend + t * t * span : span * t) + sag * (4f * t * (1f - t));
                if (keepOut.HasValue)
                {
                    p = KeptOut(from + p, keepOut.Value, keepOutBox) - from;
                }
                for (int k = 0; k < n; k++)
                {
                    _verts[b + k] = rot * _roundVerts[k] + p;
                    _normals[b + k] = rot * _roundNormals[k];
                }
            }
            // Fold to a point only the rounds that were on the belt last time and are now past its end (the rest are already
            // zeroed, from the constructor or an earlier, longer belt).
            for (int i = count; i < _lastCount; i++)
            {
                int b = i * n;
                for (int k = 0; k < n; k++)
                {
                    _verts[b + k] = Vector3.zero;
                }
            }
            _lastCount = count;
            _mesh.vertices = _verts;
            _mesh.normals = _normals;
        }

        private static bool Same(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

        /// <summary>The bend the strip was last built with (from its start to its curve's control point; zero for a straight strip).</summary>
        private Vector3 _lastBend;

        /// <summary>
        /// Whether the straight line from <paramref name="a"/> to <paramref name="b"/> passes through <paramref name="box"/> (in
        /// <paramref name="frame"/>, grown by 8 mm as <see cref="KeptOut"/> keeps rounds out of it): a slab test.
        /// </summary>
        internal static bool Crosses(Vector3 a, Vector3 b, Runtime.RigidPose frame, Bounds box)
        {
            Vector3 p = frame.InverseTransformPoint(a) - box.center, d = frame.InverseTransformPoint(b) - box.center - p;
            Vector3 ext = box.extents + Vector3.one * 0.008f;
            float enter = 0f, leave = 1f;
            for (int k = 0; k < 3; k++)
            {
                if (Mathf.Abs(d[k]) < 1e-7f)
                {
                    if (Mathf.Abs(p[k]) >= ext[k])
                    {
                        return false;
                    }
                    continue;
                }
                float t1 = (-ext[k] - p[k]) / d[k], t2 = (ext[k] - p[k]) / d[k];
                enter = Mathf.Max(enter, Mathf.Min(t1, t2));
                leave = Mathf.Min(leave, Mathf.Max(t1, t2));
                if (enter > leave)
                {
                    return false;
                }
            }
            return leave - enter > 1e-4f;
        }

        /// <summary>
        /// <paramref name="world"/> moved out of <paramref name="box"/> (in <paramref name="frame"/>, grown by 8 mm, half a round and a
        /// little) through the face it is nearest; as it was when outside. Pure: the offline tests check a point inside comes out
        /// on the nearest face and one outside stays.
        /// </summary>
        internal static Vector3 KeptOut(Vector3 world, Runtime.RigidPose frame, Bounds box)
        {
            Vector3 d = frame.InverseTransformPoint(world) - box.center;
            Vector3 ext = box.extents + Vector3.one * 0.008f;
            float inX = ext.x - Mathf.Abs(d.x), inY = ext.y - Mathf.Abs(d.y), inZ = ext.z - Mathf.Abs(d.z);
            if (inX <= 0f || inY <= 0f || inZ <= 0f)
            {
                return world;
            }
            if (inX <= inY && inX <= inZ)
            {
                d.x = (d.x < 0f ? -1f : 1f) * ext.x;
            }
            else if (inY <= inZ)
            {
                d.y = (d.y < 0f ? -1f : 1f) * ext.y;
            }
            else
            {
                d.z = (d.z < 0f ? -1f : 1f) * ext.z;
            }
            return frame.TransformPoint(box.center + d);
        }

        /// <summary>
        /// Where round <paramref name="i"/> of <paramref name="count"/> sits along a strip <paramref name="length"/> long (0: the box
        /// mouth, 1: the far end), a pitch apart: counted from the hand back toward the box, so the end in the hand is always a
        /// whole round, or from the box out when nothing holds the end (<paramref name="fromMouth"/>: a short belt hanging or laid
        /// starts at the mouth, not in the air).
        /// </summary>
        internal static float RoundAt(int i, int count, float length, bool fromMouth)
        {
            float along = i / (float)Mathf.Max(1, count - 1) * Mathf.Min(1f, (count - 1) * Pitch / Mathf.Max(length, 1e-4f));
            return fromMouth ? along : 1f - along;
        }

        /// <summary>Self-test: the belt round's side faces drawn from outside (see <see cref="Casings.CountFacingOut"/>).</summary>
        internal static void CountFacingOut(ref int outward, ref int total)
        {
            EnsureAssets();
            Casings.CountFacingOut(_round, ref outward, ref total);
        }

        public void Destroy()
        {
            if (_root != null)
            {
                Object.Destroy(_root);
            }
            if (_mesh != null)
            {
                Object.Destroy(_mesh);
            }
        }

        private static void EnsureAssets()
        {
            if (_round != null)
            {
                return;
            }
            Shader s = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            _brass = new Material(s) { name = "RavenfieldVR belt brass", color = new Color(0.74f, 0.57f, 0.24f) };
            _link = new Material(s) { name = "RavenfieldVR belt link", color = new Color(0.16f, 0.16f, 0.17f) };
            if (_brass.HasProperty("_Metallic"))
            {
                _brass.SetFloat("_Metallic", 0.9f);
                _brass.SetFloat("_Glossiness", 0.55f);
                _link.SetFloat("_Metallic", 0.6f);
                _link.SetFloat("_Glossiness", 0.35f);
            }
            _round = BuildRound();
            _roundVerts = _round.vertices;
            _roundNormals = _round.normals;
            _roundTris = _round.GetTriangles(0);
            _linkTris = _round.GetTriangles(1);
        }

        /// <summary>A 7.62-ish cartridge along +Z (case and pointed bullet, submesh 0) and its link clip (submesh 1).</summary>
        private static Mesh BuildRound()
        {
            const int sides = 10;
            // Profile along Z: (z, radius) from the case head to the bullet tip.
            float[] z = { -0.026f, -0.026f, 0.010f, 0.016f, 0.020f, 0.030f, 0.036f };
            float[] r = { 0f, 0.0060f, 0.0060f, 0.0040f, 0.0040f, 0.0030f, 0f };
            int rings = z.Length;
            var verts = new System.Collections.Generic.List<Vector3>();
            var normals = new System.Collections.Generic.List<Vector3>();
            for (int i = 0; i < rings; i++)
            {
                for (int k = 0; k <= sides; k++)
                {
                    float a = k * Mathf.PI * 2f / sides;
                    var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    verts.Add(new Vector3(dir.x * r[i], dir.y * r[i], z[i]));
                    normals.Add(r[i] > 0f ? dir : new Vector3(0f, 0f, i == 0 ? -1f : 1f));
                }
            }
            var round = new System.Collections.Generic.List<int>();
            for (int i = 0; i < rings - 1; i++)
            {
                for (int k = 0; k < sides; k++)
                {
                    int a = i * (sides + 1) + k, b = (i + 1) * (sides + 1) + k;
                    // Clockwise seen from outside (Unity's front face), so the outside is drawn.
                    round.Add(a); round.Add(a + 1); round.Add(b);
                    round.Add(b); round.Add(a + 1); round.Add(b + 1);
                }
            }
            // Link: a flat band round the case, open toward the next round.
            int linkStart = verts.Count;
            float lr = 0.0064f, half = 0.004f;
            for (int k = 0; k <= sides; k++)
            {
                float a = k * Mathf.PI * 2f / sides;
                var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                verts.Add(dir * lr + new Vector3(0f, 0f, -0.012f - half));
                normals.Add(dir);
                verts.Add(dir * lr + new Vector3(0f, 0f, -0.012f + half));
                normals.Add(dir);
            }
            var link = new System.Collections.Generic.List<int>();
            for (int k = 0; k < sides; k++)
            {
                int a = linkStart + k * 2;
                link.Add(a); link.Add(a + 2); link.Add(a + 1);
                link.Add(a + 1); link.Add(a + 2); link.Add(a + 3);
            }
            var mesh = new Mesh { name = "RavenfieldVR belt round" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(round, 0);
            mesh.SetTriangles(link, 1);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
