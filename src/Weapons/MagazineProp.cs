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
        public VRHand HeldBy { get; private set; }

        /// <summary>Insertion end (top of a magazine, nose of a shell/rocket) in prop space.</summary>
        public readonly Vector3 TipLocal;
        /// <summary>Unit vector from the prop's base toward its tip, in prop space.</summary>
        public readonly Vector3 InsertDirLocal;
        /// <summary>Unit vector pointing like the weapon barrel, in prop space.</summary>
        public readonly Vector3 FrontLocal;
        private readonly Vector3 _baseLocal;
        private readonly Rigidbody _body;
        private readonly Collider _collider;
        private float _dropTime = -1f;

        public bool Seating;
        public RigidPose SeatFrom;
        public float SeatProgress;

        private MagazineProp(GameObject go, PropKind kind, string key, int rounds, Bounds bounds, Vector3 insertDir, Vector3 front)
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
            _baseLocal = bounds.center - InsertDirLocal * extent;

            var box = go.AddComponent<BoxCollider>();
            box.center = bounds.center;
            box.size = Vector3.Max(bounds.size, new Vector3(0.01f, 0.01f, 0.01f));
            _collider = box;
            _body = go.AddComponent<Rigidbody>();
            _body.mass = kind == PropKind.Rocket ? 2f : 0.4f;
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
                go = CopyVisuals(rig.Magazine);
            }
            if (go == null)
            {
                go = new GameObject("RavenfieldVR " + kind);
                bounds = BuildProcedural(go, kind, insertLocal, frontLocal, d.MagBounds);
            }
            go.name = "RavenfieldVR " + kind;
            SetLayer(go, HeldLayer);
            var prop = new MagazineProp(go, kind, d.Key, rounds, bounds, insertLocal, frontLocal);
            RigidPose seat = rig.GunBodyPose.Multiply(d.MagSeat);
            seat.ApplyTo(go.transform);
            Cull();
            return prop;
        }

        private static GameObject CopyVisuals(Transform source)
        {
            var root = new GameObject("mag");
            bool any = false;
            Quaternion inv = Quaternion.Inverse(source.rotation);
            foreach (Renderer r in source.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = null;
                Material[] mats = r.sharedMaterials;
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
                var part = new GameObject(r.name);
                part.transform.SetParent(root.transform, false);
                part.transform.localPosition = inv * (r.transform.position - source.position);
                part.transform.localRotation = inv * r.transform.rotation;
                // Baked skinned meshes are already in world scale; static meshes keep their transform's scale.
                part.transform.localScale = r is SkinnedMeshRenderer ? Vector3.one : r.transform.lossyScale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = part.AddComponent<MeshRenderer>();
                mr.sharedMaterials = mats;
                mr.shadowCastingMode = ShadowCastingMode.On;
                any = true;
            }
            if (!any)
            {
                Object.Destroy(root);
                return null;
            }
            return root;
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
            SetLayer(Go, HeldLayer);
        }

        /// <summary>World pose the prop takes in the hand: base in the palm, tip up-forward, front forward.</summary>
        public RigidPose HoldPose(VRHand hand)
        {
            RigidPose palm = hand.Palm;
            Vector3 insertTarget = (palm.Up * 0.75f + palm.Forward * 0.65f).normalized;
            Quaternion rot = Quaternion.LookRotation(palm.Forward, insertTarget) * Quaternion.Inverse(Quaternion.LookRotation(FrontLocal, InsertDirLocal));
            Vector3 pos = palm.position + palm.Up * 0.01f - rot * _baseLocal;
            return new RigidPose(pos, rot);
        }

        public void FollowHand()
        {
            if (HeldBy != null && !Seating)
            {
                HoldPose(HeldBy).ApplyTo(Go.transform);
            }
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
            Seating = false;
            SetLayer(Go, WorldLayer);
            _collider.enabled = true;
            _body.isKinematic = false;
            _body.velocity = velocity;
            _body.angularVelocity = angularVelocity;
            _dropTime = Time.time;
            IgnorePlayerCollision();
        }

        private void IgnorePlayerCollision()
        {
            FpsActorController player = LocalPlayer.controller;
            if (player == null)
            {
                return;
            }
            foreach (Collider c in player.GetComponentsInChildren<Collider>())
            {
                if (c != null && c.enabled)
                {
                    Physics.IgnoreCollision(_collider, c, true);
                }
            }
        }

        public bool IsDropped => _dropTime >= 0f;

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

        /// <summary>Nearest dropped prop of the given weapon within <paramref name="radius"/>.</summary>
        public static MagazineProp NearestDropped(Vector3 point, float radius, string weaponKey)
        {
            MagazineProp best = null;
            float bestDist = radius;
            foreach (MagazineProp p in Alive)
            {
                if (!p.IsDropped || p.Go == null || p.WeaponKey != weaponKey)
                {
                    continue;
                }
                float d = Vector3.Distance(p.Go.transform.position, point);
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
                    p.Destroy();
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
