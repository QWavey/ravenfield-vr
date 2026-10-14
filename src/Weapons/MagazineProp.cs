using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    public enum PropKind { Magazine, Shell, Rocket }

    /// <summary>
    /// A magazine, shell or rocket you can hold, drop, catch and insert. Uses a copy of the weapon's own
    /// magazine meshes when available (skinned parts are baked), otherwise a sized procedural stand-in.
    /// Its local space matches the weapon's magazine space, so seating it is a straight pose match.
    /// </summary>
    public sealed class MagazineProp
    {
        private const int HeldLayer = 18;
        private const int WorldLayer = 0;
        private static readonly List<MagazineProp> Alive = new List<MagazineProp>();

        /// <summary>Every magazine, shell and rocket prop that exists (held, palmed, sliding or lying around).</summary>
        public static IReadOnlyList<MagazineProp> All => Alive;
        private static Material _metal, _brass, _olive;

        public readonly GameObject Go;
        public readonly PropKind Kind;
        public readonly string WeaponKey;
        public int Rounds;
        /// <summary>
        /// The ammo the rounds came from. A dropped magazine keeps its rounds (pick it up and seat it to use them);
        /// if it despawns first they go back to this reserve, so leaving a half-full magazine behind costs nothing.
        /// </summary>
        public AmmoState Owner;
        public VRHand HeldBy { get; private set; }

        /// <summary>Insertion end (top of a magazine, nose of a shell/rocket) in prop space.</summary>
        public readonly Vector3 TipLocal;
        /// <summary>Unit vector from the prop's base toward its tip, in prop space.</summary>
        public readonly Vector3 InsertDirLocal;
        /// <summary>Unit vector pointing like the weapon barrel, in prop space.</summary>
        public readonly Vector3 FrontLocal;
        /// <summary>Bottom end (base plate of a magazine, rim of a shell) in prop space.</summary>
        public readonly Vector3 BaseLocal;
        /// <summary>Half the magazine's thickness across the hand (side to side), for holding a second one beside it.</summary>
        private readonly float _sideHalf;
        /// <summary>Half the magazine's largest size across its insertion axis (front to back or side to side): how big its base plate is.</summary>
        public readonly float CrossHalf;
        private readonly Rigidbody _body;
        /// <summary>The magazine's own shape (convex pieces of its mesh: a curved or drum magazine is not a crate).</summary>
        private Collider[] _colliders;
        private readonly HashSet<Collider> _ignoredBodies = new HashSet<Collider>();
        private float _nextBodyCheck;
        /// <summary>The colliders came from the meshes' boxes (unreadable meshes) and are replaced once the model is measured.</summary>
        private bool _boxShaped;
        /// <summary>Per magazine type: the shape fitted to the rendered model (for every later magazine of the type).</summary>
        private static readonly Dictionary<string, ShapeFit.Piece[]> RenderedShapes = new Dictionary<string, ShapeFit.Piece[]>();
        /// <summary>The pieces' bounds in prop space, for distances while the colliders are off (Unity reports an empty box then).</summary>
        private Bounds[] _pieceBounds;
        private float _dropTime = -1f;

        /// <summary>Locked onto the weapon's insertion axis (sliding in or out of the well).</summary>
        public bool OnRail;
        /// <summary>How far along the rail (0 = at the well entry, RailLength = seated).</summary>
        public float RailDepth;
        /// <summary>Insertion travel: how far the prop slides before it seats.</summary>
        public readonly float RailLength;
        public float LastTick;

        private MagazineProp(GameObject go, PropKind kind, string key, int rounds, Bounds bounds, Vector3 insertDir, Vector3 front, bool machineGun, string fitKey)
        {
            Go = go;
            Kind = kind;
            WeaponKey = key;
            Rounds = rounds;
            InsertDirLocal = insertDir.normalized;
            FrontLocal = Vector3.ProjectOnPlane(front, InsertDirLocal).sqrMagnitude > 1e-4f
                ? Vector3.ProjectOnPlane(front, InsertDirLocal).normalized
                : Vector3.Cross(InsertDirLocal, Vector3.right).normalized;
            float extent = Mathf.Abs(Vector3.Dot(bounds.extents, Abs(InsertDirLocal)));
            TipLocal = bounds.center + InsertDirLocal * extent;
            BaseLocal = bounds.center - InsertDirLocal * extent;
            Vector3 sideLocal = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
            _sideHalf = Mathf.Clamp(Mathf.Abs(Vector3.Dot(bounds.extents, Abs(sideLocal))), 0.006f, 0.04f);
            CrossHalf = Mathf.Clamp(Mathf.Max(Mathf.Abs(Vector3.Dot(bounds.extents, Abs(FrontLocal))), Mathf.Abs(Vector3.Dot(bounds.extents, Abs(sideLocal)))), 0.006f, 0.1f);
            switch (kind)
            {
                case PropKind.Shell:
                    RailLength = Mathf.Clamp(extent * 1.4f, 0.025f, 0.06f);
                    break;
                case PropKind.Rocket:
                    RailLength = Mathf.Clamp(extent * 1.0f, 0.12f, 0.5f);
                    break;
                default:
                    // Magazines slide in for roughly half their length before they latch; a machine gun's box or
                    // drum hooks on with a short, heavy slide along its rails.
                    RailLength = machineGun ? Mathf.Clamp(extent * 0.35f, 0.03f, 0.06f) : Mathf.Clamp(extent * 0.9f, 0.03f, 0.09f);
                    break;
            }

            Renderer[] shown = go.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer r in shown)
            {
                _boxShaped |= !ShapeFit.Readable(r);
            }
            ShapeFit.Piece[] shape;
            if (_boxShaped && kind == PropKind.Magazine && RenderedShapes.TryGetValue(ShapeKey, out ShapeFit.Piece[] rendered))
            {
                shape = rendered; // this type was measured already: its real shape from the start
                _boxShaped = false;
            }
            else
            {
                shape = fitKey != null ? ShapeFit.Fit(fitKey, shown, go.transform.worldToLocalMatrix, 0.015f, 8) : new ShapeFit.Piece[0];
            }
            if (shape.Length == 0)
            {
                shape = new[] { ShapeFit.BoxPiece(bounds.center, Vector3.Max(bounds.size, new Vector3(0.01f, 0.01f, 0.01f))) };
                _boxShaped = true;
            }
            _colliders = ShapeFit.AddColliders(go, shape).ToArray();
            _pieceBounds = new Bounds[shape.Length];
            for (int i = 0; i < shape.Length; i++)
            {
                _pieceBounds[i] = shape[i].Bounds;
            }
            _body = go.AddComponent<Rigidbody>();
            _body.mass = kind == PropKind.Rocket ? 2f : machineGun && kind == PropKind.Magazine ? 1.5f : 0.4f;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.isKinematic = true;
            SetCollidersEnabled(false);
            Alive.Add(this);
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>Creates a prop for <paramref name="rig"/>'s ammunition at the magazine's seated pose.</summary>
        public static MagazineProp Create(WeaponRig rig, PropKind kind, int rounds)
        {
            WeaponRigData d = rig.Data;
            Vector3 insertGun = -d.MagEjectAxis;
            Quaternion seatInv = Quaternion.Inverse(d.MagSeat.rotation);
            Vector3 insertLocal = seatInv * insertGun;
            Vector3 frontLocal = seatInv * d.AimAxis;

            GameObject go = null;
            Bounds bounds = d.MagBounds;
            string fitKey = null;
            if (kind == PropKind.Magazine && rig.Magazine != null)
            {
                go = CopyVisuals(rig);
                fitKey = go != null ? "mag:" + d.Key : null;
                if (go != null)
                {
                    bounds = RenderedBounds(go, d.MagBounds, d.Key);
                }
            }
            if (go == null)
            {
                go = new GameObject("RavenfieldVR " + kind);
                bounds = BuildProcedural(go, kind, insertLocal, frontLocal, d.MagBounds);
            }
            go.name = "RavenfieldVR " + kind;
            SetLayer(go, HeldLayer);
            var prop = new MagazineProp(go, kind, d.Key, rounds, bounds, insertLocal, frontLocal, d.IsMachineGun, fitKey);
            RigidPose seat = rig.GunBodyPose.Multiply(d.MagSeat);
            seat.ApplyTo(go.transform);
            Cull();
            return prop;
        }

        /// <summary>Visual-only copy of what the weapon feeds from, in magazine space (for the pouch display).</summary>
        internal static GameObject BuildDisplay(WeaponRig rig, PropKind kind, out Bounds bounds, out Vector3 insertLocal, out Vector3 frontLocal)
        {
            WeaponRigData d = rig.Data;
            Quaternion seatInv = Quaternion.Inverse(d.MagSeat.rotation);
            insertLocal = (seatInv * -d.MagEjectAxis).normalized;
            frontLocal = seatInv * d.AimAxis;
            GameObject go = null;
            bounds = d.MagBounds;
            if (kind == PropKind.Magazine && rig.Magazine != null)
            {
                go = CopyVisuals(rig);
                if (go != null)
                {
                    bounds = RenderedBounds(go, d.MagBounds, d.Key);
                }
            }
            if (go == null)
            {
                go = new GameObject("RavenfieldVR Pouch " + kind);
                bounds = BuildProcedural(go, kind, insertLocal, frontLocal, d.MagBounds);
            }
            go.name = "RavenfieldVR Pouch " + kind;
            SetLayer(go, HeldLayer);
            foreach (Collider c in go.GetComponentsInChildren<Collider>(true))
            {
                Object.Destroy(c);
            }
            return go;
        }

        internal static int DisplayLayer => HeldLayer;

        /// <summary>
        /// The bounds of what a copy actually shows, in its root's space. The analysis measures the magazine part alone at
        /// its idle pose; the copy also carries the pieces that travel with it (rounds, a follower) and skinned triangles
        /// baked from the gun's mesh, and a magazine held or displayed by the analysis box sat off its real middle (pistol
        /// magazines hung low in the pouch and in the hand). A copy measuring far from the analysis keeps the analysis.
        /// </summary>
        private static Bounds RenderedBounds(GameObject root, Bounds analysed, string key)
        {
            Matrix4x4 toRoot = root.transform.worldToLocalMatrix;
            bool any = false;
            var b = new Bounds();
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                    Vector3 p = m.MultiplyPoint3x4(corner);
                    if (!any)
                    {
                        b = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(p);
                    }
                }
            }
            float size = b.size.magnitude, expected = analysed.size.magnitude;
            if (!any || size < 0.02f || size > 1.5f || expected > 0.02f && (size > expected * 3f || size < expected / 3f))
            {
                Log.InfoOnce("magbounds:" + key, $"Magazine copy of {key} measures {size * 100f:0.0} cm against {expected * 100f:0.0} cm analysed; the analysed size is used.");
                return analysed;
            }
            if ((b.center - analysed.center).magnitude > 0.01f || Mathf.Abs(size - expected) > 0.01f)
            {
                Log.InfoOnce("magbounds:" + key, $"Magazine copy of {key}: shown bounds centre {b.center * 100f} cm, size {size * 100f:0.0} cm (analysed {analysed.center * 100f} cm, {expected * 100f:0.0} cm).");
            }
            return b;
        }

        /// <summary>
        /// Copies the magazine's look: its own meshes, the pieces that travel with it, and for skinned guns the
        /// triangles of the gun mesh that the magazine bones drive. Everything is expressed in magazine space.
        /// </summary>
        private static GameObject CopyVisuals(WeaponRig rig)
        {
            // While the magazine is out of the gun its parts are scaled to nothing: copy them at their real size.
            Vector3[] hidden = rig.ShowMagazineForCopy();
            try
            {
                return CopyVisualsShown(rig);
            }
            finally
            {
                rig.RestoreMagazineScales(hidden);
            }
        }

        private static GameObject CopyVisualsShown(WeaponRig rig)
        {
            Transform source = rig.Magazine;
            var root = new GameObject("mag");
            bool any = false;
            Quaternion inv = Quaternion.Inverse(source.rotation);
            var magBones = new HashSet<Transform>();
            foreach (Transform part in rig.MagazineParts)
            {
                foreach (Transform t in part.GetComponentsInChildren<Transform>(true))
                {
                    magBones.Add(t);
                }
            }

            // Only what shows on the gun: a rig's hidden spare magazine (the one its reload animation brings in) must not
            // turn up in your hand next to the real one.
            bool anyShown = false;
            foreach (Transform part in rig.MagazineParts)
            {
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                {
                    anyShown |= r.enabled && r.gameObject.activeInHierarchy && !(r is ParticleSystemRenderer);
                }
            }
            foreach (Transform part in rig.MagazineParts)
            {
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                {
                    if (anyShown && (!r.enabled || !r.gameObject.activeInHierarchy))
                    {
                        continue;
                    }
                    Mesh mesh = null;
                    if (r is MeshRenderer)
                    {
                        MeshFilter mf = r.GetComponent<MeshFilter>();
                        mesh = mf != null ? mf.sharedMesh : null;
                    }
                    else if (r is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                    {
                        mesh = new Mesh { name = smr.sharedMesh.name + " (baked)" };
                        smr.BakeMesh(mesh);
                    }
                    if (mesh == null)
                    {
                        continue;
                    }
                    AddPart(root.transform, r.name, mesh, r.sharedMaterials, inv * (r.transform.position - source.position), inv * r.transform.rotation,
                        r is SkinnedMeshRenderer ? Vector3.one : r.transform.lossyScale);
                    any = true;
                }
            }

            // Skinned guns: the magazine is a bone; cut its triangles out of the gun mesh.
            if (!any && rig.Data.MagazineIsSkinned)
            {
                foreach (SkinnedMeshRenderer smr in rig.Weapon.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Mesh cut = ExtractBoneTriangles(smr, magBones);
                    if (cut != null)
                    {
                        AddPart(root.transform, smr.name + " (magazine)", cut, smr.sharedMaterials,
                            inv * (smr.transform.position - source.position), inv * smr.transform.rotation, Vector3.one);
                        any = true;
                    }
                }
            }

            if (!any)
            {
                Object.Destroy(root);
                return null;
            }
            return root;
        }

        private static void AddPart(Transform root, string name, Mesh mesh, Material[] materials, Vector3 localPos, Quaternion localRot, Vector3 scale)
        {
            var part = new GameObject(name);
            part.transform.SetParent(root, false);
            part.transform.localPosition = localPos;
            part.transform.localRotation = localRot;
            part.transform.localScale = scale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = part.AddComponent<MeshRenderer>();
            mr.sharedMaterials = Util.MaterialFix.Fix(materials);
            mr.shadowCastingMode = ShadowCastingMode.On;
        }

        /// <summary>Triangles whose vertices are driven mostly by the given bones, baked in the renderer's space.</summary>
        private static Mesh ExtractBoneTriangles(SkinnedMeshRenderer smr, HashSet<Transform> bones)
        {
            Mesh shared = smr.sharedMesh;
            if (shared == null || !shared.isReadable || smr.bones == null)
            {
                return null;
            }
            Transform[] smrBones = smr.bones;
            var boneIsMag = new bool[smrBones.Length];
            bool anyBone = false;
            for (int i = 0; i < smrBones.Length; i++)
            {
                boneIsMag[i] = smrBones[i] != null && bones.Contains(smrBones[i]);
                anyBone |= boneIsMag[i];
            }
            if (!anyBone)
            {
                return null;
            }
            BoneWeight[] weights = shared.boneWeights;
            if (weights == null || weights.Length != shared.vertexCount)
            {
                return null;
            }
            var baked = new Mesh();
            smr.BakeMesh(baked);
            Vector3[] verts = baked.vertices;
            Vector3[] normals = baked.normals;
            Vector2[] uv = shared.uv;
            var map = new Dictionary<int, int>();
            var newVerts = new List<Vector3>();
            var newNormals = new List<Vector3>();
            var newUv = new List<Vector2>();
            var subMeshes = new List<int[]>();
            for (int sub = 0; sub < shared.subMeshCount; sub++)
            {
                int[] tris = shared.GetTriangles(sub);
                var keep = new List<int>();
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    if (!DrivenBy(weights[tris[t]], boneIsMag) || !DrivenBy(weights[tris[t + 1]], boneIsMag) || !DrivenBy(weights[tris[t + 2]], boneIsMag))
                    {
                        continue;
                    }
                    for (int k = 0; k < 3; k++)
                    {
                        int source = tris[t + k];
                        if (!map.TryGetValue(source, out int idx))
                        {
                            idx = newVerts.Count;
                            map[source] = idx;
                            newVerts.Add(verts[source]);
                            newNormals.Add(normals != null && normals.Length > source ? normals[source] : Vector3.up);
                            newUv.Add(uv != null && uv.Length > source ? uv[source] : Vector2.zero);
                        }
                        keep.Add(idx);
                    }
                }
                subMeshes.Add(keep.ToArray());
            }
            Object.Destroy(baked);
            if (newVerts.Count == 0)
            {
                return null;
            }
            var result = new Mesh { name = shared.name + " (magazine)" };
            result.indexFormat = newVerts.Count > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16;
            result.SetVertices(newVerts);
            result.SetNormals(newNormals);
            result.SetUVs(0, newUv);
            result.subMeshCount = subMeshes.Count;
            for (int sub = 0; sub < subMeshes.Count; sub++)
            {
                result.SetTriangles(subMeshes[sub], sub);
            }
            result.RecalculateBounds();
            return result;
        }

        private static bool DrivenBy(BoneWeight w, bool[] boneIsMag)
        {
            float share = 0f;
            if (w.boneIndex0 < boneIsMag.Length && boneIsMag[w.boneIndex0]) share += w.weight0;
            if (w.boneIndex1 < boneIsMag.Length && boneIsMag[w.boneIndex1]) share += w.weight1;
            if (w.boneIndex2 < boneIsMag.Length && boneIsMag[w.boneIndex2]) share += w.weight2;
            if (w.boneIndex3 < boneIsMag.Length && boneIsMag[w.boneIndex3]) share += w.weight3;
            return share >= 0.5f;
        }

        private static Bounds BuildProcedural(GameObject go, PropKind kind, Vector3 insertLocal, Vector3 frontLocal, Bounds magBounds)
        {
            EnsureMaterials();
            Vector3 size;
            Material mat;
            PrimitiveType shape;
            switch (kind)
            {
                case PropKind.Shell:
                    size = new Vector3(0.019f, 0.065f, 0.019f);
                    mat = _brass;
                    shape = PrimitiveType.Capsule;
                    break;
                case PropKind.Rocket:
                    size = new Vector3(0.085f, 0.75f, 0.085f);
                    mat = _olive;
                    shape = PrimitiveType.Capsule;
                    break;
                default:
                    size = magBounds.size.sqrMagnitude > 1e-6f ? magBounds.size : new Vector3(0.025f, 0.12f, 0.06f);
                    mat = _metal;
                    shape = PrimitiveType.Cube;
                    break;
            }
            GameObject tmp = GameObject.CreatePrimitive(shape);
            Object.DestroyImmediate(tmp.GetComponent<Collider>());
            tmp.transform.SetParent(go.transform, false);
            // Primitive's long axis (Y) follows the insertion direction; Z follows the barrel.
            Quaternion orient = Quaternion.LookRotation(frontLocal, insertLocal);
            tmp.transform.localRotation = orient;
            tmp.transform.localPosition = kind == PropKind.Magazine ? magBounds.center : Vector3.zero;
            tmp.transform.localScale = kind == PropKind.Magazine ? new Vector3(size.x, size.y, size.z) : new Vector3(size.x, size.y * 0.5f, size.z);
            tmp.GetComponent<Renderer>().sharedMaterial = mat;
            Vector3 worldishSize = orient * new Vector3(size.x, size.y, size.z);
            return new Bounds(tmp.transform.localPosition, Abs(worldishSize));
        }

        private static void EnsureMaterials()
        {
            if (_metal != null)
            {
                return;
            }
            Shader s = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            _metal = new Material(s) { color = new Color(0.12f, 0.12f, 0.13f) };
            _brass = new Material(s) { color = new Color(0.72f, 0.55f, 0.22f) };
            _olive = new Material(s) { color = new Color(0.28f, 0.31f, 0.2f) };
            if (_metal.HasProperty("_Metallic"))
            {
                _metal.SetFloat("_Metallic", 0.6f);
                _brass.SetFloat("_Metallic", 0.9f);
            }
        }

        private static void SetLayer(GameObject go, int layer)
        {
            foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer;
            }
        }

        // ---- holding ------------------------------------------------------------------------------

        // A grabbed prop eases into the hand over a few frames instead of jumping there.
        private RigidPose _attachFrom;
        private bool _fresh = true;
        private float _attachAt = -1f;
        private const float AttachBlend = 0.09f;

        public void Attach(VRHand hand)
        {
            // A prop made for this grab (from the pouch, pulled from the well) starts where it belongs; one picked up,
            // caught or moved from the other fingers eases in from where it was.
            if (Go != null && !_fresh)
            {
                _attachFrom = RigidPose.Of(Go.transform);
                _attachAt = Time.unscaledTime;
            }
            _fresh = false;
            HeldBy = hand;
            hand.Role = HandRole.Magazine;
            hand.Held = this;
            _body.isKinematic = true;
            SetCollidersEnabled(false);
            _dropTime = -1f;
            OnRail = false;
            SetLayer(Go, HeldLayer);
        }

        /// <summary>
        /// World pose the prop takes in the hand: the fist closed around its lower body (a third of the way up from
        /// the base plate, as you hold a magazine to seat it), tip up-forward, front forward.
        /// </summary>
        public RigidPose HoldPose(VRHand hand)
        {
            RigidPose palm = hand.Palm;
            Vector3 insertTarget = (palm.Up * 0.75f + palm.Forward * 0.65f).normalized;
            Quaternion rot = Quaternion.LookRotation(palm.Forward, insertTarget) * Quaternion.Inverse(Quaternion.LookRotation(FrontLocal, InsertDirLocal));
            float gripAt = Kind == PropKind.Magazine ? 0.33f : 0.5f; // shells and rockets are pinched in the middle
            Vector3 held = Vector3.Lerp(BaseLocal, TipLocal, gripAt);
            Vector3 pos = palm.position - rot * held;
            return new RigidPose(pos, rot);
        }

        /// <summary>
        /// Held as a second magazine in a hand that already holds one (H3VR-style palming): no hand role of its own, no
        /// physics; <see cref="PalmedPose"/> places it beside the first.
        /// </summary>
        public void AttachPalmed(VRHand hand)
        {
            _fresh = false;
            HeldBy = hand;
            _body.isKinematic = true;
            SetCollidersEnabled(false);
            _dropTime = -1f;
            OnRail = false;
            SetLayer(Go, HeldLayer);
        }

        /// <summary>
        /// The palmed magazine's pose: parallel to the one the hand holds, beside it on the finger side of the fist and
        /// a little lower, the way a second magazine is clamped between the fingers.
        /// </summary>
        public RigidPose PalmedPose(VRHand hand, MagazineProp primary)
        {
            RigidPose p = HoldPose(hand);
            Vector3 towardFingers = -hand.Palm.Right * hand.Mirror; // the palm side of the fist
            float spacing = _sideHalf + (primary != null ? primary._sideHalf : _sideHalf) + 0.004f;
            Vector3 insert = p.TransformDirection(InsertDirLocal);
            return new RigidPose(p.position + towardFingers * spacing - insert * 0.025f, p.rotation);
        }

        private GunVolume _volume;
        private bool _volumeTried;
        private static readonly System.Func<Vector3, bool>[] HandSolids = new System.Func<Vector3, bool>[2];
        private static readonly VRHand[] HandSolidOwners = new VRHand[2];

        /// <summary>Self-test: this magazine's measured solid (null until measured).</summary>
        internal GunVolume Volume => _volume;

        private void EnsureVolume()
        {
            if (_volume != null && !_volume.Failed || _volumeTried && _volume == null || Go == null)
            {
                return;
            }
            _volumeTried = true;
            var renderers = new List<Renderer>();
            foreach (Renderer r in Go.GetComponentsInChildren<Renderer>(false))
            {
                if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                {
                    renderers.Add(r);
                }
            }
            // Box-like: the three axis views see it whole. With unreadable meshes its physics shape comes from it too.
            _volume = GunVolume.Measure(ShapeKey, renderers, Go.transform, 3, 0.015f, 8);
        }

        /// <summary>
        /// The fingers of the hand holding magazines close on them: the solid of every magazine that hand holds (the one
        /// in the fist and a palmed second one), each measured once per magazine type from its own model.
        /// </summary>
        private void ShapeHand()
        {
            VRHand hand = HeldBy;
            if (hand == null || hand.Model == null || Kind != PropKind.Magazine)
            {
                return;
            }
            foreach (MagazineProp m in Alive)
            {
                if (m.HeldBy == hand && m.Kind == PropKind.Magazine)
                {
                    m.EnsureVolume(); // the palmed second magazine too: the fingers must not go through it either
                }
            }
            if (_volume == null || !_volume.Ready)
            {
                return; // until it is measured the fingers keep their usual grip instead of closing on nothing
            }
            int side = (int)hand.Side;
            if (HandSolids[side] == null || !ReferenceEquals(HandSolidOwners[side], hand))
            {
                // A new rig (VR restarted) has new hands: the closure must test this one.
                HandSolidOwners[side] = hand;
                HandSolids[side] = p => HeldSolid(hand, p);
            }
            hand.Model.Solid = HandSolids[side];
            hand.Model.SolidAnchor = Go.transform;
            hand.Model.SolidVersion = HeldShapesOf(hand).Count;
        }

        /// <summary>One held magazine's shape at its pose this frame (magazines of one type share a volume).</summary>
        private struct HeldShape
        {
            public GunVolume Volume;
            public Vector3 Position;
            public Quaternion Inverse;
        }

        /// <summary>Per hand: the measured magazines it holds this frame, gathered once per frame (not per tested point).</summary>
        private static readonly List<HeldShape>[] HeldShapes = { new List<HeldShape>(2), new List<HeldShape>(2) };
        private static readonly int[] HeldShapesFrame = { -1, -1 };

        private static List<HeldShape> HeldShapesOf(VRHand hand)
        {
            int side = (int)hand.Side;
            List<HeldShape> list = HeldShapes[side];
            if (HeldShapesFrame[side] == Time.frameCount)
            {
                return list;
            }
            HeldShapesFrame[side] = Time.frameCount;
            list.Clear();
            foreach (MagazineProp m in Alive)
            {
                if (m.HeldBy == hand && m._volume != null && m._volume.Ready && m.Go != null)
                {
                    Transform t = m.Go.transform;
                    list.Add(new HeldShape { Volume = m._volume, Position = t.position, Inverse = Quaternion.Inverse(t.rotation) });
                }
            }
            return list;
        }

        private static bool HeldSolid(VRHand hand, Vector3 p)
        {
            List<HeldShape> held = HeldShapesOf(hand);
            for (int i = 0; i < held.Count; i++)
            {
                if (held[i].Volume.SolidAt(p, held[i].Position, held[i].Inverse))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Self-test: how much of this hand is buried in the magazines it holds.</summary>
        internal static string HandClipping(VRHand hand) => HandClipping(hand, out _, out _, out _);

        internal static string HandClipping(VRHand hand, out int buried, out int samples, out int touching)
        {
            buried = samples = touching = -1;
            if (hand.Model == null)
            {
                return "not measured";
            }
            bool measured = false;
            foreach (MagazineProp m in Alive)
            {
                measured |= m.HeldBy == hand && m._volume != null && m._volume.Ready;
            }
            if (!measured)
            {
                return "not measured (magazine shape not ready)";
            }
            hand.Model.Measure(p => InsideHeld(hand, p), p => HeldSolid(hand, p), out buried, out samples, out touching);
            return $"{buried}/{samples} points inside the magazines, {touching}/4 fingers on them";
        }

        private static bool InsideHeld(VRHand hand, Vector3 p)
        {
            List<HeldShape> held = HeldShapesOf(hand);
            for (int i = 0; i < held.Count; i++)
            {
                if (held[i].Volume.InsideModelAt(p, held[i].Position, held[i].Inverse))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A hand let go of its magazines: it no longer closes on them.</summary>
        private static void ReleaseHand(VRHand hand)
        {
            if (hand != null && hand.Model != null && hand.Model.Solid == HandSolids[(int)hand.Side])
            {
                hand.Model.Solid = null;
            }
        }

        private string ShapeKey => "prop:" + WeaponKey + ":" + Kind;

        /// <summary>
        /// A magazine whose meshes could not be read had box colliders: once its model is measured they become the
        /// shape fitted to what is drawn (and every later magazine of the type starts with it).
        /// </summary>
        private void TakeRenderedShape()
        {
            if (!_boxShaped || _volume == null || !_volume.Ready || _volume.HullPieces == null || _volume.HullPieces.Length == 0 || Go == null)
            {
                return;
            }
            _boxShaped = false;
            ShapeFit.Piece[] pieces = _volume.HullPieces;
            RenderedShapes[ShapeKey] = pieces;
            bool on = _colliders.Length > 0 && _colliders[0] != null && _colliders[0].enabled;
            foreach (Collider c in _colliders)
            {
                if (c != null)
                {
                    UnityEngine.Object.Destroy(c);
                }
            }
            ShapeFit.Bake(pieces);
            _colliders = ShapeFit.AddColliders(Go, pieces).ToArray();
            _pieceBounds = new Bounds[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                _pieceBounds[i] = pieces[i].Bounds;
            }
            SetCollidersEnabled(on);
            if (on)
            {
                PropPhysics.IgnorePlayer(_colliders);
            }
        }

        /// <summary>Self-test: how this magazine's colliders match its drawn model (null until it is measured).</summary>
        internal string ShapeMatch(out float excess, out float covered)
        {
            excess = covered = -1f;
            if (_volume == null || !_volume.Ready || Go == null || _colliders.Length == 0 || !_colliders[0].enabled)
            {
                return null;
            }
            return GunCollider.MatchShape(_colliders, Go.transform, _volume, 6, 300, out excess, out covered);
        }

        /// <summary>Self-test: the colliders were still the meshes' boxes when matched.</summary>
        internal bool BoxShaped => _boxShaped;

        public void FollowHand()
        {
            ShapeHand();
            TakeRenderedShape();
            if (HeldBy != null && !OnRail)
            {
                RigidPose target = HoldPose(HeldBy);
                float t = _attachAt >= 0f ? (Time.unscaledTime - _attachAt) / AttachBlend : 1f;
                if (t < 1f)
                {
                    float k = t * t * (3f - 2f * t);
                    target = new RigidPose(Vector3.Lerp(_attachFrom.position, target.position, k), Quaternion.Slerp(_attachFrom.rotation, target.rotation, k));
                }
                else
                {
                    _attachAt = -1f;
                }
                target.ApplyTo(Go.transform);
            }
        }

        /// <summary>World pose at the current rail depth, given the seated pose of the magazine/shell/rocket.</summary>
        public RigidPose RailPose(RigidPose seat)
        {
            Vector3 insert = seat.TransformDirection(InsertDirLocal);
            return new RigidPose(seat.position - insert * (RailLength - RailDepth), seat.rotation);
        }

        public Vector3 TipWorld => Go.transform.TransformPoint(TipLocal);
        public Vector3 InsertDirWorld => Go.transform.TransformDirection(InsertDirLocal);

        public void Drop(Vector3 velocity, Vector3 angularVelocity)
        {
            if (HeldBy != null)
            {
                if (ReferenceEquals(HeldBy.Held, this))
                {
                    HeldBy.Held = null;
                    HeldBy.Role = HandRole.Free;
                }
                ReleaseHand(HeldBy);
                HeldBy = null;
            }
            OnRail = false;
            int layer = PropPhysics.Layer;
            SetLayer(Go, layer >= 0 ? layer : WorldLayer);
            SetCollidersEnabled(true);
            _body.isKinematic = false;
            _body.velocity = velocity;
            _body.angularVelocity = angularVelocity;
            _dropTime = Time.time;
            PropPhysics.IgnorePlayer(_colliders);
            // Bodies around it from the first physics step on (not from the next clean-up pass): a magazine falling next to
            // a soldier or into a ragdoll is not shoved away.
            PropPhysics.IgnoreBodiesNear(_colliders, Go.transform.position, 1f, _ignoredBodies);
            foreach (Collider c in _colliders)
            {
                GunCollider.Current?.OnPropDropped(c);
            }
        }

        private void SetCollidersEnabled(bool on)
        {
            foreach (Collider c in _colliders)
            {
                c.enabled = on;
            }
        }

        public bool IsDropped => _dropTime >= 0f;

        /// <summary>Seconds since it was let go (-1 while held).</summary>
        internal float DroppedFor => _dropTime >= 0f ? Time.time - _dropTime : -1f;
        internal Collider[] Colliders => _colliders;
        internal Rigidbody Body => _body;

        /// <summary>
        /// Distance from a point to the prop's own shape. Its transform pivot can sit far from the mesh (many mods
        /// pivot a magazine at the gun's origin), so grabbing is always judged against the box around the mesh.
        /// </summary>
        public float DistanceTo(Vector3 point)
        {
            if (Go == null || _colliders.Length == 0)
            {
                return float.MaxValue;
            }
            float best = float.MaxValue;
            if (_colliders[0].enabled && Go.activeInHierarchy)
            {
                foreach (Collider c in _colliders)
                {
                    best = Mathf.Min(best, Vector3.Distance(c.ClosestPoint(point), point));
                }
                return best;
            }
            // Held or sliding in the well the colliders are off and report no bounds at all: measure against the pieces'
            // boxes in the prop's own space (it is never scaled).
            Vector3 local = Go.transform.InverseTransformPoint(point);
            foreach (Bounds b in _pieceBounds)
            {
                best = Mathf.Min(best, Mathf.Sqrt(b.SqrDistance(local)));
            }
            return best;
        }

        /// <summary>Removed by the clean-up: rounds still inside go back to the reserve they came from.</summary>
        public void Despawn()
        {
            if (Owner != null && Rounds > 0 && VRConfig.ReturnRoundsOnMagDrop.Value)
            {
                Owner.ReturnSpareCapped(Rounds);
            }
            Rounds = 0;
            Destroy();
        }

        public void Destroy()
        {
            if (HeldBy != null && ReferenceEquals(HeldBy.Held, this))
            {
                HeldBy.Held = null;
                HeldBy.Role = HandRole.Free;
            }
            ReleaseHand(HeldBy);
            HeldBy = null;
            Alive.Remove(this);
            if (Go != null)
            {
                Object.Destroy(Go);
            }
        }

        /// <summary>
        /// Nearest dropped prop within <paramref name="radius"/>, measured to its collider (a long magazine can be taken
        /// by either end); only props of <paramref name="weaponKey"/> unless it is null.
        /// </summary>
        public static MagazineProp NearestDropped(Vector3 point, float radius, string weaponKey)
        {
            MagazineProp best = null;
            float bestDist = radius;
            foreach (MagazineProp p in Alive)
            {
                if (!p.IsDropped || p.Go == null || weaponKey != null && p.WeaponKey != weaponKey)
                {
                    continue;
                }
                float d = p.DistanceTo(point);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = p;
                }
            }
            return best;
        }

        /// <summary>Removes props past their lifetime, beyond the cap, or fallen out of the world.</summary>
        public static void Cull()
        {
            float lifetime = VRConfig.MagPropLifetime.Value;
            int max = Mathf.Max(1, VRConfig.MaxMagProps.Value);
            int dropped = 0;
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                MagazineProp p = Alive[i];
                if (p.Go == null)
                {
                    Alive.RemoveAt(i);
                    continue;
                }
                if (!p.IsDropped)
                {
                    continue;
                }
                dropped++;
                bool expired = Time.time - p._dropTime > lifetime || p.Go.transform.position.y < -500f || dropped > max;
                if (expired)
                {
                    p.Despawn();
                    continue;
                }
                // Lying in the world, bodies do not push it about (a magazine on the ground stays where it fell).
                if (Time.time >= p._nextBodyCheck)
                {
                    p._nextBodyCheck = Time.time + (Time.time - p._dropTime < 0.5f ? 0f : 0.25f);
                    PropPhysics.IgnoreBodiesNear(p._colliders, p.Go.transform.position, 1f, p._ignoredBodies);
                }
            }
        }

        public static void DestroyAll()
        {
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                Alive[i].Destroy();
            }
            Alive.Clear();
        }
    }
}
