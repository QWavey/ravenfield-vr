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
        private readonly Rigidbody _body;
        private readonly Collider _collider;
        private float _dropTime = -1f;

        /// <summary>Locked onto the weapon's insertion axis (sliding in or out of the well).</summary>
        public bool OnRail;
        /// <summary>How far along the rail (0 = at the well entry, RailLength = seated).</summary>
        public float RailDepth;
        /// <summary>Insertion travel: how far the prop slides before it seats.</summary>
        public readonly float RailLength;
        public float LastTick;

        private MagazineProp(GameObject go, PropKind kind, string key, int rounds, Bounds bounds, Vector3 insertDir, Vector3 front, bool machineGun)
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

            var box = go.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = Vector3.Max(bounds.size, new Vector3(0.01f, 0.01f, 0.01f));
            _collider = box;
            _body = go.AddComponent<Rigidbody>();
            _body.mass = kind == PropKind.Rocket ? 2f : machineGun && kind == PropKind.Magazine ? 1.5f : 0.4f;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _body.isKinematic = true;
            _collider.enabled = false;
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
            if (kind == PropKind.Magazine && rig.Magazine != null)
            {
                go = CopyVisuals(rig);
            }
            if (go == null)
            {
                go = new GameObject("RavenfieldVR " + kind);
                bounds = BuildProcedural(go, kind, insertLocal, frontLocal, d.MagBounds);
            }
            go.name = "RavenfieldVR " + kind;
            SetLayer(go, HeldLayer);
            var prop = new MagazineProp(go, kind, d.Key, rounds, bounds, insertLocal, frontLocal, d.IsMachineGun);
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
        /// Copies the magazine's look: its own meshes, the pieces that travel with it, and for skinned guns the
        /// triangles of the gun mesh that the magazine bones drive. Everything is expressed in magazine space.
        /// </summary>
        private static GameObject CopyVisuals(WeaponRig rig)
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

            foreach (Transform part in rig.MagazineParts)
            {
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                {
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

        public void Attach(VRHand hand)
        {
            HeldBy = hand;
            hand.Role = HandRole.Magazine;
            hand.Held = this;
            _body.isKinematic = true;
            _collider.enabled = false;
            _dropTime = -1f;
            OnRail = false;
            SetLayer(Go, HeldLayer);
        }

        /// <summary>World pose the prop takes in the hand: base in the palm, tip up-forward, front forward.</summary>
        public RigidPose HoldPose(VRHand hand)
        {
            RigidPose palm = hand.Palm;
            Vector3 insertTarget = (palm.Up * 0.75f + palm.Forward * 0.65f).normalized;
            Quaternion rot = Quaternion.LookRotation(palm.Forward, insertTarget) * Quaternion.Inverse(Quaternion.LookRotation(FrontLocal, InsertDirLocal));
            Vector3 pos = palm.position + palm.Up * 0.01f - rot * BaseLocal;
            return new RigidPose(pos, rot);
        }

        public void FollowHand()
        {
            if (HeldBy != null && !OnRail)
            {
                HoldPose(HeldBy).ApplyTo(Go.transform);
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
                HeldBy = null;
            }
            OnRail = false;
            int layer = PropPhysics.Layer;
            SetLayer(Go, layer >= 0 ? layer : WorldLayer);
            _collider.enabled = true;
            _body.isKinematic = false;
            _body.velocity = velocity;
            _body.angularVelocity = angularVelocity;
            _dropTime = Time.time;
            PropPhysics.IgnorePlayer(_collider);
            GunCollider.Current?.OnPropDropped(_collider);
        }

        public bool IsDropped => _dropTime >= 0f;

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
                float d = Vector3.Distance(p._collider.ClosestPoint(point), point);
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
