using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Physics for loose props (dropped magazines, shells, rockets). They live on a layer the game's bullets and
    /// raycasts skip, so a magazine on the floor never stops a shot or confuses a ground check, and they never collide
    /// with the player's own body (controller, hitboxes, ragdoll), which would leave them resting on invisible colliders.
    /// </summary>
    internal static class PropPhysics
    {
        // Layers Projectile.HIT_MASK leaves out (Ignore Raycast first); level geometry is on layers 0 and 15.
        private static readonly int[] Candidates = { 2, 9, 11, 22, 24 };
        private const int LevelLayerA = 0, LevelLayerB = 15;

        /// <summary>
        /// The layer for loose props and the gun's stand-in: one bullets ignore that still collides with the level and
        /// with itself and that the VR camera draws. -1 when none qualifies (props then stay on Default).
        /// </summary>
        public static int Layer
        {
            get
            {
                Camera cam = VRRig.Instance != null ? VRRig.Instance.Camera : null;
                int drawn = cam != null ? cam.cullingMask : ~0;
                foreach (int l in Candidates)
                {
                    if ((drawn & (1 << l)) != 0 && !Physics.GetIgnoreLayerCollision(l, LevelLayerA) && !Physics.GetIgnoreLayerCollision(l, LevelLayerB)
                        && !Physics.GetIgnoreLayerCollision(l, l))
                    {
                        return l;
                    }
                }
                return -1;
            }
        }

        private static readonly List<Collider> Scratch = new List<Collider>();

        /// <summary>Makes <paramref name="c"/> pass through every collider of the local player's body.</summary>
        public static void IgnorePlayer(Collider c)
        {
            if (c == null)
            {
                return;
            }
            FpsActorController controller = LocalPlayer.controller;
            if (controller != null)
            {
                IgnoreAll(c, controller.gameObject);
            }
            Actor actor = LocalPlayer.actor;
            if (actor != null)
            {
                IgnoreAll(c, actor.gameObject);
                ActiveRaggy ragdoll = actor.ragdoll;
                if (ragdoll != null)
                {
                    if (ragdoll.animatedColliders != null)
                    {
                        foreach (Collider other in ragdoll.animatedColliders)
                        {
                            Ignore(c, other);
                        }
                    }
                    if (ragdoll.ragdollObject != null)
                    {
                        IgnoreAll(c, ragdoll.ragdollObject);
                    }
                }
            }
        }

        private static void IgnoreAll(Collider c, GameObject root)
        {
            root.GetComponentsInChildren(true, Scratch);
            foreach (Collider other in Scratch)
            {
                Ignore(c, other);
            }
            Scratch.Clear();
        }

        private static void Ignore(Collider a, Collider b)
        {
            // Unity refuses (with an error) unless both colliders are enabled and active; the inactive ragdoll
            // only appears once the player is dead.
            if (b != null && b != a && b.enabled && b.gameObject.activeInHierarchy && a.enabled && a.gameObject.activeInHierarchy)
            {
                Physics.IgnoreCollision(a, b, true);
            }
        }
    }

    /// <summary>
    /// A physical stand-in for the held gun, so loose magazines bounce off it and the gun can shove props lying
    /// around. One box in the gun's aim frame, fitted to the weapon's visible meshes (arms and magazine left out) and
    /// clamped to a sane envelope around the grip. It sits on the prop layer and ignores the player's body, so it never
    /// blocks a bullet or the player's movement. It is a light dynamic body chasing the gun by velocity, not a kinematic
    /// one: it pushes a magazine but cannot shove a car or a ragdoll with infinite force, and if something heavy holds
    /// it back it jumps back to the gun. Props that start inside it (a magazine leaving the well) pass through until clear.
    /// </summary>
    internal sealed class GunCollider
    {
        public static GunCollider Current { get; private set; }

        private readonly WeaponRig _rig;
        private readonly GameObject _go;
        private readonly Rigidbody _body;
        private readonly GunProxyBody _driver;
        private readonly BoxCollider _box;
        private readonly Quaternion _frame;
        private readonly Vector3 _origin;
        private readonly List<Collider> _passing = new List<Collider>();
        private bool _placed;

        private GunCollider(WeaponRig rig, int layer, Quaternion frame, Vector3 origin, Bounds box)
        {
            _rig = rig;
            _frame = frame;
            _origin = origin;
            _go = new GameObject("RavenfieldVR Gun Collider") { layer = layer };
            Object.DontDestroyOnLoad(_go);
            _box = _go.AddComponent<BoxCollider>();
            _box.center = box.center;
            _box.size = box.size;
            _body = _go.AddComponent<Rigidbody>();
            _body.mass = 2f;
            _body.useGravity = false;
            _body.drag = 0f;
            _body.angularDrag = 0f;
            _body.maxAngularVelocity = 50f;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _driver = _go.AddComponent<GunProxyBody>();
            _driver.Body = _body;
            PropPhysics.IgnorePlayer(_box);
        }

        public static GunCollider Create(WeaponRig rig)
        {
            int layer = PropPhysics.Layer;
            if (layer < 0 || rig.GunBody == null)
            {
                return null;
            }
            WeaponRigData d = rig.Data;
            Quaternion frame = OpenVRMath.SafeLookRotation(d.AimAxis, d.Up);
            Vector3 origin = d.GripPoint;
            Bounds box = Fit(rig, frame, origin);
            Current?.Destroy();
            Current = new GunCollider(rig, layer, frame, origin, box);
            return Current;
        }

        /// <summary>The weapon's visible extent in the box frame (origin at the grip, z along the barrel).</summary>
        private static Bounds Fit(WeaponRig rig, Quaternion frame, Vector3 origin)
        {
            WeaponRigData d = rig.Data;
            Quaternion inv = Quaternion.Inverse(frame);
            float muzzle = (inv * (d.MuzzlePoint - origin)).z;
            float halfWidth = d.IsPistol ? 0.03f : 0.06f;
            var envMin = new Vector3(-halfWidth, -0.22f, d.IsPistol ? -0.06f : -0.42f);
            var envMax = new Vector3(halfWidth, 0.15f, Mathf.Max(muzzle, 0.1f) + 0.02f);

            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            bool any = VisitVisibleCorners(rig, gun =>
            {
                Vector3 q = inv * (gun - origin);
                min = Vector3.Min(min, q);
                max = Vector3.Max(max, q);
            });
            if (!any)
            {
                // No measurable mesh: a slim box from just behind the grip to the muzzle.
                min = new Vector3(-0.025f, -0.1f, d.IsPistol ? -0.04f : -0.25f);
                max = new Vector3(0.025f, 0.07f, envMax.z);
            }
            min = Vector3.Max(min, envMin);
            max = Vector3.Min(max, envMax);
            Vector3 size = Vector3.Max(max - min, new Vector3(0.02f, 0.03f, 0.08f));
            return new Bounds((min + max) * 0.5f, size);
        }

        /// <summary>Axis-aligned bounds of the weapon's visible meshes in gun space (arms and magazine left out).</summary>
        internal static Bounds VisibleBounds(WeaponRig rig, out bool measured)
        {
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            measured = VisitVisibleCorners(rig, gun =>
            {
                min = Vector3.Min(min, gun);
                max = Vector3.Max(max, gun);
            });
            return measured ? new Bounds((min + max) * 0.5f, max - min) : new Bounds(rig.Data.GripPoint, Vector3.one * 0.05f);
        }

        /// <summary>Calls <paramref name="visit"/> with the gun-space corners of each visible weapon mesh; false if there were none.</summary>
        private static bool VisitVisibleCorners(WeaponRig rig, System.Action<Vector3> visit)
        {
            var skip = new HashSet<Renderer>(rig.ArmRenderers);
            foreach (Transform part in rig.MagazineParts)
            {
                if (part != null)
                {
                    foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                    {
                        skip.Add(r);
                    }
                }
            }
            bool any = false;
            foreach (Renderer r in rig.Weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || skip.Contains(r))
                {
                    continue;
                }
                Bounds local;
                Transform space;
                if (r is SkinnedMeshRenderer smr)
                {
                    local = smr.localBounds;
                    space = smr.rootBone != null ? smr.rootBone : smr.transform;
                }
                else if (r is MeshRenderer)
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                    {
                        continue;
                    }
                    local = mf.sharedMesh.bounds;
                    space = r.transform;
                }
                else
                {
                    continue; // particles, lines, trails
                }
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = local.center + Vector3.Scale(local.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    visit(rig.ToGun(space.TransformPoint(corner)));
                    any = true;
                }
            }
            return any;
        }

        /// <summary>Follows the gun through the physics engine so props it touches are pushed, not teleported through.</summary>
        public void Update()
        {
            if (_rig.GunBody == null)
            {
                return;
            }
            // Seated (a weapon drawn in a car), the stand-in would sit inside the vehicle's own colliders and shove it
            // every physics step; there are no loose props to bounce off in there anyway.
            Actor actor = LocalPlayer.actor;
            bool seated = actor != null && actor.IsSeated();
            if (_box.enabled == seated)
            {
                _box.enabled = !seated;
                _body.detectCollisions = !seated;
                _placed = false; // back on foot: jump to the gun instead of sweeping there
            }
            if (seated)
            {
                return;
            }
            Vector3 pos = _rig.ToWorld(_origin);
            Quaternion rot = _rig.GunBody.rotation * _frame;
            if (!_placed)
            {
                // First frame: start on the gun instead of sweeping there through the world.
                _body.position = pos;
                _body.rotation = rot;
                _go.transform.SetPositionAndRotation(pos, rot);
                _placed = true;
            }
            _driver.TargetPos = pos;
            _driver.TargetRot = rot;
            _driver.HasTarget = true;
            for (int i = _passing.Count - 1; i >= 0; i--)
            {
                Collider c = _passing[i];
                if (c == null)
                {
                    _passing.RemoveAt(i);
                }
                else if (!c.bounds.Intersects(_box.bounds))
                {
                    Physics.IgnoreCollision(c, _box, false);
                    _passing.RemoveAt(i);
                }
            }
        }

        /// <summary>A prop just became physical: if it starts inside the gun it passes through until clear.</summary>
        public void OnPropDropped(Collider c)
        {
            if (c == null || _box == null || !c.bounds.Intersects(_box.bounds))
            {
                return;
            }
            Physics.IgnoreCollision(c, _box, true);
            if (!_passing.Contains(c))
            {
                _passing.Add(c);
            }
        }

        public void Destroy()
        {
            if (_go != null)
            {
                Object.Destroy(_go);
            }
            _passing.Clear();
            if (Current == this)
            {
                Current = null;
            }
        }
    }

    /// <summary>Drives the gun's stand-in toward its target pose each physics step by setting its velocity.</summary>
    internal sealed class GunProxyBody : MonoBehaviour
    {
        private const float SnapDistanceSq = 0.09f; // held back 30 cm (wedged against a vehicle): jump back
        private const float MaxSpeed = 25f;

        public Rigidbody Body;
        public Vector3 TargetPos;
        public Quaternion TargetRot = Quaternion.identity;
        public bool HasTarget;

        private void FixedUpdate()
        {
            if (!HasTarget || Body == null)
            {
                return;
            }
            float dt = Time.fixedDeltaTime;
            Vector3 delta = TargetPos - Body.position;
            if (delta.sqrMagnitude > SnapDistanceSq)
            {
                Body.position = TargetPos;
                Body.rotation = TargetRot;
                Body.velocity = Vector3.zero;
                Body.angularVelocity = Vector3.zero;
                return;
            }
            Body.velocity = Vector3.ClampMagnitude(delta / dt, MaxSpeed);
            Quaternion dq = TargetRot * Quaternion.Inverse(Body.rotation);
            dq.ToAngleAxis(out float angle, out Vector3 axis);
            if (angle > 180f)
            {
                angle -= 360f;
            }
            Body.angularVelocity = Mathf.Abs(angle) > 0.01f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x)
                ? axis * (angle * Mathf.Deg2Rad / dt)
                : Vector3.zero;
        }
    }
}
