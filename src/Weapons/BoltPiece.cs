using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// A visible, moving charging handle for guns whose bolt is part of the receiver mesh (no separate part moves
    /// when the action is worked). Readable meshes: the geometry around the handle point is cut out of the
    /// receiver into its own piece, so nothing changes at rest and pulling it back opens the port like the real
    /// thing. Unreadable meshes: a small handle in the gun's own material is added at the point where the mod's
    /// hand racks in its reload animation.
    /// </summary>
    internal sealed class BoltPiece
    {
        private struct Piece
        {
            public Transform T;
            public MeshFilter Source;
            public Mesh Original;
        }

        private struct CarveResult
        {
            public Mesh Remaining;
            public Mesh Cut;
            public int[] Submeshes;
        }

        // Carving is done once per mesh and handle point, then shared by every copy of the weapon.
        private static readonly Dictionary<string, CarveResult> CarveCache = new Dictionary<string, CarveResult>();

        private readonly List<Piece> _pieces = new List<Piece>();
        public bool Carved { get; private set; }

        /// <summary>
        /// Self-test: pieces that are really drawn now (alive, switched on, with a mesh and an enabled renderer). A handle
        /// once counted as made while its pieces had gone with an object destroyed at the end of the frame.
        /// </summary>
        internal int ShownPieces
        {
            get
            {
                int n = 0;
                foreach (Piece p in _pieces)
                {
                    if (p.T == null || !p.T.gameObject.activeInHierarchy)
                    {
                        continue;
                    }
                    MeshFilter mf = p.T.GetComponent<MeshFilter>();
                    MeshRenderer mr = p.T.GetComponent<MeshRenderer>();
                    if (mf != null && mf.sharedMesh != null && mf.sharedMesh.vertexCount > 0 && mr != null && mr.enabled)
                    {
                        n++;
                    }
                }
                return n;
            }
        }

        public static BoltPiece Create(WeaponRig rig) => Create(rig, rig.Data.RackPoint);

        /// <param name="rackGun">Where the handle is (gun-body space).</param>
        public static BoltPiece Create(WeaponRig rig, Vector3 rackGun)
        {
            var piece = new BoltPiece();
            try
            {
                Build(piece, rig, rackGun);
            }
            catch
            {
                piece.Destroy(); // what was cut so far goes back into the receiver: a failure half way left a hole in it
                throw;
            }
            return piece;
        }

        private static void Build(BoltPiece piece, WeaponRig rig, Vector3 rackGun)
        {
            WeaponRigData d = rig.Data;
            float radius = d.IsPistol ? 0.022f : 0.03f;
            Vector3 rackWorld = rig.ToWorld(rackGun);
            var skip = new HashSet<Renderer>(rig.ArmRenderers);
            foreach (MeshFilter mf in rig.Weapon.GetComponentsInChildren<MeshFilter>(false))
            {
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || !mr.enabled || skip.Contains(mr) || IsMagazine(rig, mf.transform)
                    || rig.Slide != null && mf.transform.IsChildOf(rig.Slide) || rig.Handle != null && mf.transform.IsChildOf(rig.Handle))
                {
                    continue; // the parts that move by themselves already (a carrier inside, a handle) are not cut again
                }
                if (mf.name.StartsWith("RavenfieldVR ", System.StringComparison.Ordinal))
                {
                    // The mod's own pieces: a handle cut or added just before (destroyed only at the end of the frame) was
                    // cut again, under itself, and went with it; the new handle was then missing.
                    continue;
                }
                if (!mf.sharedMesh.isReadable)
                {
                    continue;
                }
                Vector3 center = mf.transform.InverseTransformPoint(rackWorld);
                float scale = Mathf.Max(1e-5f, Mathf.Abs(mf.transform.lossyScale.x)); // a mirrored mesh (negative x) took the whole receiver
                float r = radius / scale;
                if ((mf.sharedMesh.bounds.ClosestPoint(center) - center).magnitude > r)
                {
                    continue;
                }
                string key = mf.sharedMesh.GetInstanceID() + "@" + center.ToString("F4") + "/" + r.ToString("F4");
                if (!CarveCache.TryGetValue(key, out CarveResult result))
                {
                    result = Carve(mf.sharedMesh, center, r);
                    CarveCache[key] = result;
                }
                if (result.Cut == null)
                {
                    continue;
                }
                var go = new GameObject("RavenfieldVR Bolt Piece");
                go.layer = mf.gameObject.layer;
                go.transform.SetParent(mf.transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = result.Cut;
                MeshRenderer pr = go.AddComponent<MeshRenderer>();
                Material[] mats = mr.sharedMaterials;
                var used = new Material[result.Submeshes.Length];
                for (int i = 0; i < used.Length; i++)
                {
                    used[i] = mats.Length > 0 ? mats[Mathf.Min(result.Submeshes[i], mats.Length - 1)] : null;
                }
                pr.sharedMaterials = used;
                pr.shadowCastingMode = mr.shadowCastingMode;
                piece._pieces.Add(new Piece { T = go.transform, Source = mf, Original = mf.sharedMesh });
                mf.sharedMesh = result.Remaining;
                piece.Carved = true;
            }
            if (!piece.Carved)
            {
                piece.AddKnob(rig, rackGun);
            }
        }

        /// <summary>1 / v keeping its sign (a mirrored parent), never dividing by almost nothing.</summary>
        private static float Inverse(float v) => (v < 0f ? -1f : 1f) / Mathf.Max(1e-5f, Mathf.Abs(v));

        private static bool IsMagazine(WeaponRig rig, Transform t)
        {
            foreach (Transform m in rig.MagazineParts)
            {
                if (m != null && t.IsChildOf(m))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Splits off the triangles around <paramref name="center"/> (mesh space).</summary>
        private static CarveResult Carve(Mesh mesh, Vector3 center, float r)
        {
            Vector3[] verts = mesh.vertices;
            var keepSub = new List<int[]>();
            var cutSub = new List<List<int>>();
            var cutSubIndex = new List<int>();
            bool any = false;
            float r2 = r * r, loose2 = r2 * 2.6f;
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                if (mesh.GetTopology(sm) != MeshTopology.Triangles)
                {
                    keepSub.Add(mesh.GetIndices(sm));
                    continue;
                }
                int[] tris = mesh.GetTriangles(sm);
                var keep = new List<int>(tris.Length);
                var cut = new List<int>();
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vector3 a = verts[tris[i]], b = verts[tris[i + 1]], c = verts[tris[i + 2]];
                    Vector3 centroid = (a + b + c) / 3f;
                    bool inside = (centroid - center).sqrMagnitude < r2
                                  && (a - center).sqrMagnitude < loose2 && (b - center).sqrMagnitude < loose2 && (c - center).sqrMagnitude < loose2;
                    (inside ? cut : keep).Add(tris[i]);
                    (inside ? cut : keep).Add(tris[i + 1]);
                    (inside ? cut : keep).Add(tris[i + 2]);
                }
                keepSub.Add(keep.ToArray());
                if (cut.Count > 0)
                {
                    cutSub.Add(cut);
                    cutSubIndex.Add(sm);
                    any = true;
                }
            }
            if (!any)
            {
                return new CarveResult();
            }

            Mesh remaining = Object.Instantiate(mesh);
            remaining.name = mesh.name + " (bolt cut)";
            for (int sm = 0; sm < keepSub.Count; sm++)
            {
                remaining.SetIndices(keepSub[sm], mesh.GetTopology(sm), sm, false);
            }

            // The cut piece gets only the vertices it uses.
            var map = new Dictionary<int, int>();
            var pv = new List<Vector3>();
            Vector3[] normals = mesh.normals;
            Vector4[] tangents = mesh.tangents;
            Vector2[] uv = mesh.uv;
            var pn = new List<Vector3>();
            var pt = new List<Vector4>();
            var pu = new List<Vector2>();
            var subTris = new List<int[]>();
            foreach (List<int> tris in cutSub)
            {
                var local = new int[tris.Count];
                for (int i = 0; i < tris.Count; i++)
                {
                    int v = tris[i];
                    if (!map.TryGetValue(v, out int nv))
                    {
                        nv = pv.Count;
                        map[v] = nv;
                        pv.Add(verts[v]);
                        if (normals.Length == verts.Length) pn.Add(normals[v]);
                        if (tangents.Length == verts.Length) pt.Add(tangents[v]);
                        if (uv.Length == verts.Length) pu.Add(uv[v]);
                    }
                    local[i] = nv;
                }
                subTris.Add(local);
            }
            var cutMesh = new Mesh { name = mesh.name + " (bolt piece)" };
            cutMesh.SetVertices(pv);
            if (pn.Count == pv.Count) cutMesh.SetNormals(pn);
            if (pt.Count == pv.Count) cutMesh.SetTangents(pt);
            if (pu.Count == pv.Count) cutMesh.SetUVs(0, pu);
            cutMesh.subMeshCount = subTris.Count;
            for (int i = 0; i < subTris.Count; i++)
            {
                cutMesh.SetTriangles(subTris[i], i);
            }
            cutMesh.RecalculateBounds();
            return new CarveResult { Remaining = remaining, Cut = cutMesh, Submeshes = cutSubIndex.ToArray() };
        }

        /// <summary>A small handle in the gun's own material, standing off the receiver at the rack point.</summary>
        private void AddKnob(WeaponRig rig, Vector3 rackGun)
        {
            WeaponRigData d = rig.Data;
            Material mat = null;
            float best = 0f;
            Vector3 gunAt = rig.GunBody.position;
            foreach (Renderer r in rig.Weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (r is ParticleSystemRenderer || !r.enabled || r.sharedMaterial == null || System.Array.IndexOf(rig.ArmRenderers, r) >= 0)
                {
                    continue;
                }
                // The gun's own opaque metal: not a sight's reticle (a 4 m additive quad was the biggest renderer of an M4's
                // holo sight, and the handle came out a red square), glass, or anything hanging far off the gun.
                Material m = r.sharedMaterial;
                string shader = m.shader != null ? m.shader.name : "";
                if (m.renderQueue >= 2450 || shader.IndexOf("Additive", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || shader.IndexOf("HUD", System.StringComparison.OrdinalIgnoreCase) >= 0 || shader.IndexOf("Particle", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || (r.bounds.center - gunAt).sqrMagnitude > 0.5f * 0.5f || r.bounds.size.sqrMagnitude > 1f)
                {
                    continue;
                }
                float size = r.bounds.size.sqrMagnitude;
                if (size > best)
                {
                    best = size;
                    mat = r.sharedMaterial;
                }
            }
            if (mat == null)
            {
                // Nothing opaque to borrow: plain dark gun metal.
                mat = _fallback ?? (_fallback = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse")) { name = "RavenfieldVR handle", color = new Color(0.12f, 0.12f, 0.13f) });
            }
            var go = new GameObject("RavenfieldVR Bolt Handle");
            go.transform.SetParent(rig.GunBody, false);
            // Out from the bore toward the side the hand reaches from.
            Vector3 fromBore = Vector3.ProjectOnPlane(rackGun - d.MuzzlePoint, d.AimAxis);
            Vector3 outward = fromBore.sqrMagnitude > 1e-6f ? fromBore.normalized : d.Up;
            _knobBase = rackGun + outward * 0.004f;
            _knobRotation = Quaternion.LookRotation(d.AimAxis, outward);
            go.AddComponent<MeshFilter>().sharedMesh = KnobMesh(d.IsPistol);
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            _pieces.Add(new Piece { T = go.transform });
            SetOffset(rig, Vector3.zero);
        }

        private static Material _fallback;
        private Vector3 _knobBase;
        private Quaternion _knobRotation = Quaternion.identity;

        private static Mesh _knob, _knobPistol;

        private static Mesh KnobMesh(bool pistol)
        {
            ref Mesh cached = ref pistol ? ref _knobPistol : ref _knob;
            if (cached != null)
            {
                return cached;
            }
            // Bevelled block: length along the bore, height away from the receiver.
            float l = pistol ? 0.014f : 0.02f, w = pistol ? 0.008f : 0.011f, h = pistol ? 0.007f : 0.012f;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            Vector3[] v = cube.vertices;
            for (int i = 0; i < v.Length; i++)
            {
                v[i] = new Vector3(v[i].x * w, (v[i].y + 0.5f) * h, v[i].z * l);
            }
            cached = new Mesh { name = "RavenfieldVR knob" };
            cached.vertices = v;
            cached.triangles = cube.triangles;
            cached.uv = cube.uv;
            cached.RecalculateNormals();
            cached.RecalculateBounds();
            return cached;
        }

        /// <summary>Moves the piece back along the bolt travel (gun space vector, already scaled).</summary>
        public void SetOffset(WeaponRig rig, Vector3 travelGun)
        {
            Vector3 world = rig.DirToWorld(travelGun);
            foreach (Piece p in _pieces)
            {
                if (p.T == null)
                {
                    continue;
                }
                if (p.Source != null)
                {
                    p.T.localPosition = p.Source.transform.InverseTransformVector(world);
                }
                else
                {
                    // World pose and unit scale every frame: gun-body space is in metres whatever the bone's scale.
                    p.T.SetPositionAndRotation(rig.ToWorld(_knobBase + travelGun), rig.GunBody.rotation * _knobRotation);
                    Vector3 parentScale = p.T.parent != null ? p.T.parent.lossyScale : Vector3.one;
                    p.T.localScale = new Vector3(Inverse(parentScale.x), Inverse(parentScale.y), Inverse(parentScale.z));
                }
            }
        }

        public void Destroy()
        {
            foreach (Piece p in _pieces)
            {
                if (p.Source != null && p.Original != null)
                {
                    p.Source.sharedMesh = p.Original;
                }
                if (p.T != null)
                {
                    Object.Destroy(p.T.gameObject);
                }
            }
            _pieces.Clear();
        }
    }
}
