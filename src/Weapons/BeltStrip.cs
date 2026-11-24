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
        /// <summary>Half a round's length (BuildRound: 6.2 cm from head to tip).</summary>
        private const float RoundHalf = 0.031f;

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
        private Bounds _lastBox;
        private bool _lastHadBox;
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
        /// A held belt whose straight line runs through the box, or a hanging one whose curve does (it hangs toward the ground: with the
        /// gun tilted, down can be through the box), goes round it in one piece (<see cref="PathRound"/>): each round pushed out by
        /// itself, those before the box's middle left by one face and those after it by another, and the belt tore in two with the
        /// box between (user, 2026-11-03: "magbelt is getting split when reacting with the physics of the MG mag").
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
                && Vector3.Dot(barrel.normalized, _lastBarrel.normalized) > 0.99996f
                // (And the box it keeps out of: measured again as drawn, the belt kept its shape round the old one; belt scout.)
                && keepOut.HasValue == _lastHadBox && (keepOutBox.size - _lastBox.size).sqrMagnitude < 1e-6f && (keepOutBox.center - _lastBox.center).sqrMagnitude < 1e-6f)
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
            _lastHadBox = keepOut.HasValue;
            _lastBox = keepOutBox;
            _lastBarrel = barrel;
            _lastRounds = rounds;
            _lastFromMouth = fromMouth;
            _lastBend = bend;
            Vector3 span = to - from;
            float length = span.magnitude;
            int count = Mathf.Min(Mathf.Clamp(Mathf.CeilToInt(length / Pitch), 2, MaxRounds), Mathf.Max(0, rounds));
            Vector3 sag = Vector3.down * Mathf.Min(0.04f, length * 0.12f);
            Bounds plainBox = keepOutBox; // (the box itself, for the rounds' ends below)
            if (keepOut.HasValue)
            {
                keepOutBox = RoundBox(from, keepOutBox, keepOut.Value, barrel);
            }
            // (Going round from over a centimetre in and back from under half of one: at one depth for both, a gun held at that tilt
            // flipped between the two shapes.)
            bool around = keepOut.HasValue && (fromMouth ? via.HasValue && CurveInBox(from, bend, span, sag, keepOut.Value, keepOutBox, _around ? 0.004f : 0.01f)
                                                         : Crosses(from, to, keepOut.Value, keepOutBox));
            _around = around;
            int points = 0;
            float total = 0f, spacing = Pitch;
            if (around)
            {
                points = PathRound(from, KeptOut(to, keepOut.Value, keepOutBox), keepOut.Value, keepOutBox, WayRound);
                total = PathLength(WayRound, points);
                int needed = Mathf.Clamp(Mathf.CeilToInt(total / Pitch) + 1, 2, MaxRounds);
                count = Mathf.Min(needed, Mathf.Max(0, rounds));
                // Longer than the strip's rounds: spread over the whole way, a little stretched, so it stays in one piece.
                spacing = count == MaxRounds ? Mathf.Max(Pitch, total / (count - 1)) : Pitch;
            }
            // Across the belt where it hangs free, along the gun by the box, as the box holds them: square to the belt all the way, they
            // tipped with it as the gun was turned, and their 6 cm went through the box's wall with their middles clear of it (user,
            // 2026-11-04: "the magbelt still gltiches through the mag but only if the mg is turned a little").
            Vector3 gun = barrel.normalized;
            Vector3 free = Vector3.ProjectOnPlane(barrel, span).sqrMagnitude > 1e-6f ? Vector3.ProjectOnPlane(barrel, span).normalized : gun;
            Vector3 way = span.sqrMagnitude > 1e-8f ? span.normalized : Vector3.up;
            // Built round the box mouth (small numbers anywhere on the map), in world directions.
            _root.transform.SetPositionAndRotation(from, Quaternion.identity);
            int n = _roundVerts.Length;
            for (int i = 0; i < count; i++)
            {
                int b = i * n;
                Vector3 p;
                if (around)
                {
                    p = PointAlong(WayRound, points, fromMouth ? i * spacing : total - i * spacing) - from; // (held: from the hand back toward the box)
                }
                else
                {
                    float t = RoundAt(i, count, length, fromMouth);
                    p = (via.HasValue ? 2f * (1f - t) * t * bend + t * t * span : span * t) + sag * (4f * t * (1f - t));
                    if (keepOut.HasValue)
                    {
                        p = KeptOut(from + p, keepOut.Value, keepOutBox) - from;
                    }
                }
                Vector3 axis = keepOut.HasValue ? Vector3.Slerp(gun, free, Mathf.Clamp01(Clearance(from + p, keepOut.Value, keepOutBox) / 0.06f)) : free;
                if (keepOut.HasValue)
                {
                    // Its ends out of the box too, not only its middle: turned between the gun's line and the belt's, a round whose middle was
                    // kept just out of a face had its tip 3 cm long through it. Measured in the game with the gun rolled 50 deg: 16 of 17 hanging
                    // rounds with an end in the MG3 ACOG's box (user, 2026-11-08: "The MG bullets as a lane ... are still clipping through the
                    // mags ... discovered on MG32"). Pushed out by the deeper end, twice (one push can bring the other end in at a corner).
                    for (int pass = 0; pass < 2; pass++)
                    {
                        Vector3 tip = from + p + axis * RoundHalf, tail = from + p - axis * RoundHalf;
                        Vector3 outTip = KeptOut(tip, keepOut.Value, plainBox) - tip, outTail = KeptOut(tail, keepOut.Value, plainBox) - tail;
                        Vector3 push = outTip.sqrMagnitude >= outTail.sqrMagnitude ? outTip : outTail;
                        if (push.sqrMagnitude < 1e-8f)
                        {
                            break;
                        }
                        p += push;
                    }
                }
                Vector3 up = Vector3.Cross(axis, way);
                Quaternion rot = Quaternion.LookRotation(axis, up.sqrMagnitude > 1e-6f ? up : Vector3.Cross(axis, Vector3.right));
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

        /// <summary>
        /// Autotest: of the rounds drawn, how many have their middle in <paramref name="box"/> (in <paramref name="frame"/>, the box shrunk by
        /// <paramref name="depth"/>), and how many reach into it that deep with any corner (a round's ends through a wall). 0 when not shown.
        /// </summary>
        internal int RoundsIn(Runtime.RigidPose frame, Bounds box, float depth, out int drawn, out int reaching)
        {
            drawn = reaching = 0;
            if (_root == null || !_root.activeInHierarchy)
            {
                return 0;
            }
            Bounds inner = box;
            inner.extents = Vector3.Max(Vector3.zero, box.extents - Vector3.one * depth);
            Vector3 origin = _root.transform.position;
            int n = _roundVerts.Length, middles = 0;
            for (int i = 0; i < _lastCount; i++)
            {
                Vector3 mid = Vector3.zero;
                bool reaches = false;
                for (int k = 0; k < n; k++)
                {
                    Vector3 v = _verts[i * n + k];
                    mid += v;
                    reaches |= inner.Contains(frame.InverseTransformPoint(origin + v));
                }
                drawn++;
                middles += inner.Contains(frame.InverseTransformPoint(origin + mid / n)) ? 1 : 0;
                reaching += reaches ? 1 : 0;
            }
            return middles;
        }

        /// <summary>The belt went round the box when it was last laid out.</summary>
        private bool _around;

        /// <summary>
        /// The box as a round's middle has to stay out of it, the round lying along the gun: longer along <paramref name="barrel"/> by half a
        /// round each way, so its head and tip stay out too; the box as it is for a belt that starts inside that (a mouth on an end
        /// of the box). Pure: the offline tests check a round's ends.
        /// </summary>
        internal static Bounds RoundBox(Vector3 from, Bounds box, Runtime.RigidPose frame, Vector3 barrel)
        {
            Vector3 along = frame.InverseTransformPoint(frame.position + barrel.normalized);
            Bounds grown = box;
            grown.extents = box.extents + new Vector3(Mathf.Abs(along.x), Mathf.Abs(along.y), Mathf.Abs(along.z)) * RoundHalf;
            return (KeptOut(from, frame, grown) - from).sqrMagnitude > 1e-8f ? box : grown;
        }

        /// <summary>How far <paramref name="world"/> is outside <paramref name="box"/> (in <paramref name="frame"/>; 0 in it or within 1.5 cm of it).</summary>
        private static float Clearance(Vector3 world, Runtime.RigidPose frame, Bounds box)
        {
            Vector3 d = frame.InverseTransformPoint(world) - box.center;
            float x = Mathf.Max(0f, Mathf.Abs(d.x) - box.extents.x), y = Mathf.Max(0f, Mathf.Abs(d.y) - box.extents.y), z = Mathf.Max(0f, Mathf.Abs(d.z) - box.extents.z);
            return Mathf.Max(0f, Mathf.Sqrt(x * x + y * y + z * z) - 0.015f);
        }

        /// <summary>The bend the strip was last built with (from its start to its curve's control point; zero for a straight strip).</summary>
        private Vector3 _lastBend;

        /// <summary>
        /// Whether the straight line from <paramref name="a"/> to <paramref name="b"/> passes through <paramref name="box"/> (in
        /// <paramref name="frame"/>, grown by 8 mm as <see cref="KeptOut"/> keeps rounds out of it): a slab test.
        /// </summary>
        internal static bool Crosses(Vector3 a, Vector3 b, Runtime.RigidPose frame, Bounds box)
        {
            Vector3 p = frame.InverseTransformPoint(a) - box.center, d = frame.InverseTransformPoint(b) - box.center - p;
            return Blocked(p, d, box.extents + Vector3.one * 0.008f);
        }

        /// <summary>Whether a hanging belt's curve (as <see cref="Place"/> lays it) goes through the box, not only brushing it.</summary>
        private static bool CurveInBox(Vector3 from, Vector3 bend, Vector3 span, Vector3 sag, Runtime.RigidPose frame, Bounds box, float deep)
        {
            for (int i = 1; i <= 10; i++)
            {
                float t = i / 10f;
                Vector3 at = from + 2f * (1f - t) * t * bend + t * t * span + sag * (4f * t * (1f - t));
                if ((KeptOut(at, frame, box) - at).sqrMagnitude > deep * deep) // (deeper than this: less is the curve brushing the box, kept out in one piece)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>The way a belt takes round the box, and the search's nodes: start, end, the box's 8 corners and 12 edge middles.</summary>
        private static readonly Vector3[] WayRound = new Vector3[PathNodes];
        private const int PathNodes = 22;
        private static readonly Vector3[] Nodes = new Vector3[PathNodes];
        private static readonly float[] Cost = new float[PathNodes];
        private static readonly int[] Came = new int[PathNodes];
        private static readonly bool[] Done = new bool[PathNodes];

        /// <summary>
        /// The shortest way from <paramref name="from"/> to <paramref name="to"/> that stays out of <paramref name="box"/> (in
        /// <paramref name="frame"/>), over its edges, into <paramref name="path"/> (world, at least <see cref="PathNodes"/> long): a search
        /// over its corners and edge middles, 12 mm out from it (a round's half and a little), each hop clear of the box grown by 7 mm.
        /// The number of points; the straight line (2) when no way is found (an end inside the box). Pure: the offline tests check it.
        /// </summary>
        internal static int PathRound(Vector3 from, Vector3 to, Runtime.RigidPose frame, Bounds box, Vector3[] path)
        {
            Vector3 clear = box.extents + Vector3.one * 0.012f, solid = box.extents + Vector3.one * 0.007f;
            Nodes[0] = frame.InverseTransformPoint(from) - box.center;
            Nodes[1] = frame.InverseTransformPoint(to) - box.center;
            int n = 2;
            for (int x = -1; x <= 1; x++)
            {
                for (int y = -1; y <= 1; y++)
                {
                    for (int z = -1; z <= 1; z++)
                    {
                        if ((x == 0 ? 1 : 0) + (y == 0 ? 1 : 0) + (z == 0 ? 1 : 0) <= 1)
                        {
                            Nodes[n++] = new Vector3(x * clear.x, y * clear.y, z * clear.z);
                        }
                    }
                }
            }
            for (int i = 0; i < n; i++)
            {
                Cost[i] = float.MaxValue;
                Came[i] = -1;
                Done[i] = false;
            }
            Cost[0] = 0f;
            while (true)
            {
                int u = -1;
                float best = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (!Done[i] && Cost[i] < best)
                    {
                        best = Cost[i];
                        u = i;
                    }
                }
                if (u < 0 || u == 1)
                {
                    break;
                }
                Done[u] = true;
                for (int v = 0; v < n; v++)
                {
                    float c = Cost[u] + Vector3.Distance(Nodes[u], Nodes[v]);
                    if (!Done[v] && c < Cost[v] && !Blocked(Nodes[u], Nodes[v] - Nodes[u], solid))
                    {
                        Cost[v] = c;
                        Came[v] = u;
                    }
                }
            }
            if (Came[1] < 0)
            {
                path[0] = from;
                path[1] = to;
                return 2;
            }
            int points = 1;
            for (int i = 1; i != 0; i = Came[i])
            {
                points++;
            }
            int at = points - 1;
            for (int i = 1; at >= 0; i = Came[i], at--)
            {
                path[at] = frame.TransformPoint(Nodes[i] + box.center);
                if (i == 0)
                {
                    break;
                }
            }
            return points;
        }

        /// <summary>How long a way of <paramref name="points"/> points is.</summary>
        internal static float PathLength(Vector3[] path, int points)
        {
            float total = 0f;
            for (int i = 1; i < points; i++)
            {
                total += Vector3.Distance(path[i - 1], path[i]);
            }
            return total;
        }

        /// <summary>The point <paramref name="along"/> metres along a way of <paramref name="points"/> points (kept to its ends).</summary>
        internal static Vector3 PointAlong(Vector3[] path, int points, float along)
        {
            if (along <= 0f)
            {
                return path[0];
            }
            for (int i = 1; i < points; i++)
            {
                float hop = Vector3.Distance(path[i - 1], path[i]);
                if (along <= hop && hop > 1e-6f)
                {
                    return Vector3.Lerp(path[i - 1], path[i], along / hop);
                }
                along -= hop;
            }
            return path[points - 1];
        }

        /// <summary>Whether the line from <paramref name="p"/> along <paramref name="d"/> (to its end) passes through the box of half size <paramref name="ext"/> round the origin: a slab test.</summary>
        private static bool Blocked(Vector3 p, Vector3 d, Vector3 ext)
        {
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
