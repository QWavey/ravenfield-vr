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
        private const int MaxRounds = 32; // enough for the whole length the belt comes out of the box
        private const float Pitch = 0.0115f; // link spacing of a rifle-calibre belt

        private static Mesh _round;
        private static Material _brass, _link;
        // The single round the strip is made of: corners, normals and the two parts' triangles.
        private static Vector3[] _roundVerts, _roundNormals;
        private static int[] _roundTris, _linkTris;

        private readonly GameObject _root;
        private readonly Mesh _mesh;
        private readonly Vector3[] _verts, _normals;

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
        /// <paramref name="rounds"/> of them (what is left in the box): a short belt ends short of the box.
        /// </summary>
        public void Place(Vector3 from, Vector3 to, Vector3 barrel, int rounds = MaxRounds)
        {
            Vector3 span = to - from;
            float length = span.magnitude;
            int count = Mathf.Min(Mathf.Clamp(Mathf.CeilToInt(length / Pitch), 2, MaxRounds), Mathf.Max(0, rounds));
            Vector3 sag = Vector3.down * Mathf.Min(0.04f, length * 0.12f);
            Vector3 forward = Vector3.ProjectOnPlane(barrel, span).sqrMagnitude > 1e-6f ? Vector3.ProjectOnPlane(barrel, span).normalized : barrel;
            Quaternion rot = Quaternion.LookRotation(forward, Vector3.Cross(forward, span.sqrMagnitude > 1e-8f ? span.normalized : Vector3.up));
            // Built round the box mouth (small numbers anywhere on the map), in world directions.
            _root.transform.SetPositionAndRotation(from, Quaternion.identity);
            int n = _roundVerts.Length;
            for (int i = 0; i < MaxRounds; i++)
            {
                int b = i * n;
                if (i >= count)
                {
                    // Not on the belt: folded to a point, nothing drawn.
                    for (int k = 0; k < n; k++)
                    {
                        _verts[b + k] = Vector3.zero;
                    }
                    continue;
                }
                // Count from the hand back toward the box, so the end in the hand is always a whole round.
                float t = 1f - i / (float)Mathf.Max(1, count - 1) * Mathf.Min(1f, (count - 1) * Pitch / Mathf.Max(length, 1e-4f));
                Vector3 p = span * t + sag * (4f * t * (1f - t));
                for (int k = 0; k < n; k++)
                {
                    _verts[b + k] = rot * _roundVerts[k] + p;
                    _normals[b + k] = rot * _roundNormals[k];
                }
            }
            _mesh.vertices = _verts;
            _mesh.normals = _normals;
            _mesh.RecalculateBounds();
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
