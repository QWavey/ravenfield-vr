using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// A visible, moving charging handle for guns whose bolt is part of the receiver mesh (no separate part moves
    /// when the action is worked). Readable meshes: the geometry around the handle point is cut out of the
    /// receiver into its own piece, so nothing changes at rest and pulling it back opens the port like the real
    /// thing. Unreadable meshes: a slim T latch in the gun's own material is added at the point where the mod's
    /// hand racks in its reload animation (never a block: see <see cref="LatchMesh"/>).
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

        /// <summary>
        /// A reload of the content makes the weapons again: their carves are made again too. Kept, every reload added the old
        /// receivers' carved copies for the rest of the session (lifecycle hunt, 2026-10-20); let go, the next scene load unloads
        /// the meshes nothing uses any more (a held weapon's own still in use is left alone).
        /// </summary>
        internal static void ClearCache() => CarveCache.Clear();

        // Reused by every carve (task 263, 2026-10-18): Mesh.vertices and the others copy the whole mesh into a new array on each
        // call, and a receiver's first carve made that copy on the main thread in the first draw. Each one is cleared or
        // overwritten before it is read, so nothing from an earlier carve is used.
        private static readonly List<Vector3> CarveVerts = new List<Vector3>();
        private static readonly List<Vector3> CarveNormals = new List<Vector3>();
        private static readonly List<Vector4> CarveTangents = new List<Vector4>();
        private static readonly List<Vector2> CarveUvs = new List<Vector2>();
        private static readonly List<int> CarveTris = new List<int>();
        private static int[] _carveKeep = new int[0], _carveCut = new int[0];

        private readonly List<Piece> _pieces = new List<Piece>();
        public bool Carved { get; private set; }

        /// <summary>Nothing cut and no knob added on purpose: the gun's own carrier, its handle with it, moves with the hand.</summary>
        internal bool CarrierOnly { get; private set; }

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

        /// <summary>Only a knob at <paramref name="rackGun"/>, nothing cut out: for a slide that moves hidden in the receiver.</summary>
        public static BoltPiece CreateKnob(WeaponRig rig, Vector3 rackGun)
        {
            var piece = new BoltPiece();
            try
            {
                piece.AddKnob(rig, rackGun);
            }
            catch
            {
                piece.Destroy();
                throw;
            }
            return piece;
        }

        /// <param name="rig">The gun the piece is cut out of.</param>
        /// <param name="rackGun">Where the handle is (gun-body space).</param>
        public static BoltPiece Create(WeaponRig rig, Vector3 rackGun)
        {
            // Timed on its own in a profiled run (the handle cut out of the receiver's mesh).
            if (!Core.PerfProbe.Enabled)
            {
                return CreateNow(rig, rackGun);
            }
            Core.PerfProbe.Begin("bolt piece cut");
            try
            {
                return CreateNow(rig, rackGun);
            }
            finally
            {
                Core.PerfProbe.End("bolt piece cut");
            }
        }

        private static BoltPiece CreateNow(WeaponRig rig, Vector3 rackGun)
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

        /// <summary>
        /// Warm-up: the receiver's cut worked out ahead (on the weapon's prefab, from the menu) and kept, so the first draw in a
        /// match finds it (it copied and walked the whole receiver mesh in that frame; 255.P6-5). Nothing is made or changed.
        /// </summary>
        internal static void Prewarm(WeaponRig rig) => Build(new BoltPiece(), rig, rig.Data.RackPoint, prewarm: true);

        private static void Build(BoltPiece piece, WeaponRig rig, Vector3 rackGun, bool prewarm = false)
        {
            WeaponRigData d = rig.Data;
            if (d.AddedHandle)
            {
                if (!prewarm)
                {
                    piece.AddKnob(rig, rackGun); // a handle of its own, nothing cut out of the receiver
                }
                return;
            }
            float radius = d.CarveRadius > 0f ? d.CarveRadius : d.Handgun ? 0.022f : 0.03f;
            Vector3 rackWorld = rig.ToWorld(rackGun);
            // Only from the handle's own mesh when it is known (the receiver round a side handle's slot stays whole).
            Transform only = d.CarveFromPath != null ? WeaponRig.Resolve(rig.Weapon.transform, d.CarveFromPath) : null;
            var skip = new HashSet<Renderer>(rig.ArmRenderers);
            int unreadable = 0, far = 0, nothing = 0, moving = 0;
            foreach (MeshFilter mf in rig.Weapon.GetComponentsInChildren<MeshFilter>(false))
            {
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (mf.sharedMesh == null || mr == null || !mr.enabled || skip.Contains(mr) || IsMagazine(rig, mf.transform)
                    || rig.Slide != null && mf.transform.IsChildOf(rig.Slide) || rig.Handle != null && mf.transform.IsChildOf(rig.Handle))
                {
                    moving += rig.Slide != null && mf.transform.IsChildOf(rig.Slide) || rig.Handle != null && mf.transform.IsChildOf(rig.Handle) ? 1 : 0;
                    continue; // the parts that move by themselves already (a carrier inside, a handle) are not cut again
                }
                if (only != null && mf.transform != only)
                {
                    continue;
                }
                if (mf.name.StartsWith("RavenfieldVR ", System.StringComparison.Ordinal))
                {
                    // The mod's own pieces: a handle cut or added just before (destroyed only at the end of the frame) was
                    // cut again, under itself, and went with it; the new handle was then missing.
                    continue;
                }
                if (!mf.sharedMesh.isReadable)
                {
                    unreadable++;
                    continue;
                }
                Vector3 center = mf.transform.InverseTransformPoint(rackWorld);
                float scale = Mathf.Max(1e-5f, Mathf.Abs(mf.transform.lossyScale.x)); // a mirrored mesh (negative x) took the whole receiver
                float r = radius / scale;
                if ((mf.sharedMesh.bounds.ClosestPoint(center) - center).magnitude > r)
                {
                    far++;
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
                    nothing++;
                    continue;
                }
                if (prewarm)
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
            if (prewarm)
            {
                return;
            }
            if (!piece.Carved && !d.AddedHandle && d.CarveHandle && d.SlidePath != null && rig.Slide != null)
            {
                // Nothing could be cut out (the receiver's mesh cannot be read), but the gun's own carrier moves with the
                // hand's stroke and its handle with it: no knob added. The knob was a second handle 3 cm off the carrier's own
                // that moved while the user watched the real one (AS Val, AK-24; Tasks 245).
                Log.InfoOnce("boltknob:" + d.Key, $"{d.Key}: nothing cut out at the rack point ({unreadable} mesh(es) unreadable, {far} too far, {nothing} with no triangles "
                                                  + $"there); its own carrier '{rig.Slide.name}' moves with the hand, no knob added.");
                piece.CarrierOnly = true;
                return;
            }
            if (!piece.Carved && d.SlidePath == null && d.HandlePath == null && d.SlideMethod != null && d.SlideMethod.Contains("(AR-pattern"))
            {
                // An AR whose bolt and T are baked into a receiver mesh the game will not let the mod read (all of the MWII
                // M4s'): the T is already drawn there, so an added knob was a second handle beside it (user, headset,
                // 2026-10-18 evening: "2 pull back things, remove the one!!!!!", all three MWII M4s). No knob: the hand racks at
                // the T, which cannot move apart from the receiver.
                Log.InfoOnce("boltknob:" + d.Key, $"{d.Key}: nothing cut out at the rack point ({unreadable} mesh(es) unreadable, {far} too far, {nothing} with no triangles "
                                                  + "there); its own T is part of the receiver's mesh: no knob added, the hand racks it there.");
                return;
            }
            if (!piece.Carved)
            {
                // Said once per weapon: the added knob is what moves, the model's own handle (if it has one) stays put.
                Log.InfoOnce("boltknob:" + d.Key, $"{d.Key}: nothing cut out at the rack point, a knob added instead ({unreadable} mesh(es) unreadable, "
                                                  + $"{far} too far, {nothing} with no triangles there, {moving} moving by themselves{(only != null ? ", only '" + only.name + "' asked" : "")}).");
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
            CarveVerts.Clear();
            mesh.GetVertices(CarveVerts);
            List<Vector3> verts = CarveVerts;
            var keepSub = new List<int[]>();
            var cutSub = new List<int[]>();
            var cutSubIndex = new List<int>();
            bool any = false;
            float r2 = r * r, loose2 = r2 * 2.6f;
            // A triangle is cut only when all three corners are within loose2 of the point (the test below needs that), so each
            // corner is measured once here and the centroid test runs only for triangles whose three corners all pass
            // (task 263, 2026-10-18).
            var near = new bool[verts.Count];
            for (int v = 0; v < near.Length; v++)
            {
                near[v] = (verts[v] - center).sqrMagnitude < loose2;
            }
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                if (mesh.GetTopology(sm) != MeshTopology.Triangles)
                {
                    keepSub.Add(mesh.GetIndices(sm));
                    continue;
                }
                CarveTris.Clear();
                mesh.GetTriangles(CarveTris, sm);
                int n = CarveTris.Count;
                if (_carveKeep.Length < n)
                {
                    _carveKeep = new int[n];
                    _carveCut = new int[n];
                }
                int keepN = 0, cutN = 0;
                for (int i = 0; i + 2 < n; i += 3)
                {
                    int ia = CarveTris[i], ib = CarveTris[i + 1], ic = CarveTris[i + 2];
                    bool inside = false;
                    if (near[ia] && near[ib] && near[ic])
                    {
                        Vector3 a = verts[ia], b = verts[ib], c = verts[ic];
                        Vector3 centroid = (a + b + c) / 3f;
                        inside = (centroid - center).sqrMagnitude < r2;
                    }
                    if (inside)
                    {
                        _carveCut[cutN++] = ia;
                        _carveCut[cutN++] = ib;
                        _carveCut[cutN++] = ic;
                    }
                    else
                    {
                        _carveKeep[keepN++] = ia;
                        _carveKeep[keepN++] = ib;
                        _carveKeep[keepN++] = ic;
                    }
                }
                var keep = new int[keepN];
                System.Array.Copy(_carveKeep, keep, keepN);
                keepSub.Add(keep);
                if (cutN > 0)
                {
                    var cut = new int[cutN];
                    System.Array.Copy(_carveCut, cut, cutN);
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

            // The cut piece gets only the vertices it uses. Read only now: a carve with no cut returned above and copied none of these.
            var map = new Dictionary<int, int>();
            var pv = new List<Vector3>();
            CarveNormals.Clear();
            mesh.GetNormals(CarveNormals);
            CarveTangents.Clear();
            mesh.GetTangents(CarveTangents);
            CarveUvs.Clear();
            mesh.GetUVs(0, CarveUvs);
            var pn = new List<Vector3>();
            var pt = new List<Vector4>();
            var pu = new List<Vector2>();
            var subTris = new List<int[]>();
            foreach (int[] tris in cutSub)
            {
                var local = new int[tris.Length];
                for (int i = 0; i < tris.Length; i++)
                {
                    int v = tris[i];
                    if (!map.TryGetValue(v, out int nv))
                    {
                        nv = pv.Count;
                        map[v] = nv;
                        pv.Add(verts[v]);
                        if (CarveNormals.Count == verts.Count) pn.Add(CarveNormals[v]);
                        if (CarveTangents.Count == verts.Count) pt.Add(CarveTangents[v]);
                        if (CarveUvs.Count == verts.Count) pu.Add(CarveUvs[v]);
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

        /// <summary>A slim T latch (<see cref="LatchMesh"/>) in the gun's own material, on the receiver's surface at the rack point.</summary>
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
            // On the surface, not standing off it (4 mm out looked like a part that did not belong; user, 2026-10-16).
            _knobBase = rackGun + outward * 0.001f;
            _knobRotation = Quaternion.LookRotation(d.AimAxis, outward);
            // The made handle is the slim T latch on every gun (task 299). It used to be a small cube on every gun that was not an
            // AR (hidden slides, guns with nothing that moves, a Bren 2's added handle), and the cube read as a block on the part
            // the hand racks (user, 2026-10-19: "some weapons still have a block on their pull rack"). An AR's T was already this shape
            // (user, 2026-10-16, mwii-m4holo: a cube there read as a black block).
            go.AddComponent<MeshFilter>().sharedMesh = LatchMesh(d.Handgun);
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            _pieces.Add(new Piece { T = go.transform });
            SetOffset(rig, Vector3.zero);
        }

        private static Material _fallback;
        private Vector3 _knobBase;
        private Quaternion _knobRotation = Quaternion.identity;

        private static Mesh _latch, _latchPistol;

        /// <summary>
        /// The slim T latch every made handle uses (task 299): a bar across the gun with a stem reaching forward from it, a few
        /// millimetres thick, standing on the surface at the rack point with the bar's back edge at that point (the place the
        /// AR charging handle's T always had). Rifles get the AR charging handle's size, pistols a smaller one. The size comes
        /// from the gun's class only, never from a part's bounds, so it cannot grow into a block the size of a whole part.
        /// </summary>
        private static Mesh LatchMesh(bool handgun)
        {
            ref Mesh cached = ref handgun ? ref _latchPistol : ref _latch;
            if (cached != null)
            {
                return cached;
            }
            // Bar: across the gun (x), along the bore (z), height off the surface (y). Stem: the same axes, starting 1 mm inside the
            // bar's forward end and reaching on forward. Rifle: bar 4.2 x 1.2 x 0.8 cm, stem 0.9 x 2.6 x 0.6 cm. Pistol: bar 2.6 x 0.9 x 0.5 cm,
            // stem 0.7 x 1.8 x 0.4 cm.
            float barAcross = handgun ? 0.026f : 0.042f, barLen = handgun ? 0.009f : 0.012f, barHigh = handgun ? 0.005f : 0.008f;
            float stemWide = handgun ? 0.007f : 0.009f, stemLen = handgun ? 0.018f : 0.026f, stemHigh = handgun ? 0.004f : 0.006f;
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Mesh cube = go.GetComponent<MeshFilter>().sharedMesh;
            Object.Destroy(go);
            Vector3[] cv = cube.vertices;
            int[] ct = cube.triangles;
            Vector2[] cu = cube.uv;
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var uvs = new List<Vector2>();
            // (centre z along the bore, width x across, length z, height y).
            void Box(float z, float wide, float len, float tall)
            {
                int start = verts.Count;
                foreach (Vector3 v in cv)
                {
                    verts.Add(new Vector3(v.x * wide, (v.y + 0.5f) * tall, z + v.z * len));
                }
                foreach (int t in ct)
                {
                    tris.Add(start + t);
                }
                uvs.AddRange(cu);
            }
            Box(barLen * 0.5f, barAcross, barLen, barHigh);
            Box(barLen - 0.001f + stemLen * 0.5f, stemWide, stemLen, stemHigh);
            cached = new Mesh { name = handgun ? "RavenfieldVR latch (pistol)" : "RavenfieldVR latch" };
            cached.SetVertices(verts);
            cached.SetTriangles(tris, 0);
            cached.SetUVs(0, uvs);
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
