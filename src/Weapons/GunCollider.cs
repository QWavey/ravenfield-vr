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
        private static readonly List<Collider> PlayerColliders = new List<Collider>();
        private static int _playerCollidersFrame = -1;

        /// <summary>Makes <paramref name="c"/> pass through every collider of the local player's body.</summary>
        public static void IgnorePlayer(Collider c)
        {
            if (c == null)
            {
                return;
            }
            foreach (Collider other in CollectPlayerColliders())
            {
                Ignore(c, other);
            }
        }

        /// <summary>The same for a set of colliders (a model's shape pieces): the player's body is walked once, not per piece.</summary>
        // Hitboxes (8), ragdolls (10), seated hitboxes (16) and the soldiers' movement colliders (17).
        internal const int BodyMask = (1 << 8) | (1 << 10) | (1 << 16) | (1 << 17);
        private static readonly Collider[] BodyScratch = new Collider[64];

        /// <summary>
        /// Something lying in the world stops colliding with the bodies around it (ragdolls, soldiers walking past), so
        /// it stays where it fell; <paramref name="done"/> remembers the bodies already ignored.
        /// </summary>
        public static void IgnoreBodiesNear(IList<Collider> colliders, Vector3 at, float radius, HashSet<Collider> done)
        {
            int n = Physics.OverlapSphereNonAlloc(at, radius, BodyScratch, BodyMask, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                Collider body = BodyScratch[i];
                if (body == null || !done.Add(body))
                {
                    continue;
                }
                for (int j = 0; j < colliders.Count; j++)
                {
                    Collider c = colliders[j];
                    if (c != null && c.enabled)
                    {
                        Physics.IgnoreCollision(c, body, true);
                    }
                }
            }
            if (done.Count > 512)
            {
                done.RemoveWhere(c => c == null);
            }
        }

        public static void IgnorePlayer(IList<Collider> colliders)
        {
            List<Collider> player = CollectPlayerColliders();
            foreach (Collider c in colliders)
            {
                if (c == null)
                {
                    continue;
                }
                foreach (Collider other in player)
                {
                    Ignore(c, other);
                }
            }
        }

        /// <summary>The player's controller, hitbox and ragdoll colliders, gathered once per frame.</summary>
        private static List<Collider> CollectPlayerColliders()
        {
            if (_playerCollidersFrame == Time.frameCount)
            {
                return PlayerColliders;
            }
            _playerCollidersFrame = Time.frameCount;
            PlayerColliders.Clear();
            FpsActorController controller = LocalPlayer.controller;
            if (controller != null)
            {
                AddAll(controller.gameObject);
            }
            Actor actor = LocalPlayer.actor;
            if (actor != null)
            {
                AddAll(actor.gameObject);
                ActiveRaggy ragdoll = actor.ragdoll;
                if (ragdoll != null)
                {
                    if (ragdoll.animatedColliders != null)
                    {
                        foreach (Collider other in ragdoll.animatedColliders)
                        {
                            if (other != null)
                            {
                                PlayerColliders.Add(other);
                            }
                        }
                    }
                    if (ragdoll.ragdollObject != null)
                    {
                        AddAll(ragdoll.ragdollObject);
                    }
                }
            }
            return PlayerColliders;
        }

        private static void AddAll(GameObject root)
        {
            root.GetComponentsInChildren(true, Scratch);
            PlayerColliders.AddRange(Scratch);
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
    /// around. A handful of boxes fitted to the weapon's own meshes (<see cref="ShapeFit"/>: stock, receiver, grip,
    /// handguard, barrel, sight; arms and magazine left out), in the gun's aim frame and clamped to a sane envelope around
    /// the grip, so things touch the gun where it is instead of an invisible crate around it. It sits on the prop layer and
    /// ignores the player's body, so it never blocks a bullet or the player's movement. It is a light dynamic body chasing
    /// the gun by velocity, not a kinematic one: it pushes a magazine but cannot shove a car or a ragdoll with infinite
    /// force, and if something heavy holds it back it jumps back to the gun. Props that start inside it (a magazine
    /// leaving the well) pass through until clear.
    /// </summary>
    internal sealed class GunCollider
    {
        public static GunCollider Current { get; private set; }

        private readonly WeaponRig _rig;
        private readonly GameObject _go;
        private readonly Rigidbody _body;
        private readonly GunProxyBody _driver;
        private readonly List<Collider> _boxes;
        private readonly Quaternion _frame;
        private readonly Vector3 _origin;
        private readonly List<Collider> _passing = new List<Collider>();
        private bool _placed;
        private bool _enabled = true;

        private GunCollider(WeaponRig rig, int layer, Quaternion frame, Vector3 origin, ShapeFit.Piece[] boxes)
        {
            _rig = rig;
            _frame = frame;
            _origin = origin;
            _go = new GameObject("RavenfieldVR Gun Collider") { layer = layer };
            Object.DontDestroyOnLoad(_go);
            _boxes = ShapeFit.AddColliders(_go, boxes);
            _pieces = boxes;
            _body = _go.AddComponent<Rigidbody>();
            _body.mass = 2f;
            _body.useGravity = false;
            _body.drag = 0f;
            _body.angularDrag = 0f;
            _body.maxAngularVelocity = 50f;
            _body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            _driver = _go.AddComponent<GunProxyBody>();
            _driver.Body = _body;
            PropPhysics.IgnorePlayer(_boxes);
        }

        /// <summary>How many convex pieces make up the held gun's shape (self-test and logs).</summary>
        public int BoxCount => _boxes.Count;

        /// <summary>Self-test: how the shape matched the model before <see cref="UseShape"/> replaced it.</summary>
        public string ShapeBefore { get; internal set; }
        public float ShapeBeforeExcess { get; internal set; } = -1f;
        public float ShapeBeforeCovered { get; internal set; } = -1f;
        /// <summary>Self-test: a rendered-model shape that was measured and not kept (it matched the model worse).</summary>
        public string ShapeRejected { get; internal set; }
        /// <summary>Self-test: what the report's before-comparison cost (it is not made in play).</summary>
        public float ReportMatchMs { get; private set; } = -1f;

        /// <summary>This stand-in is the one of <paramref name="rig"/>'s weapon.</summary>
        internal bool Holds(WeaponRig rig) => _rig == rig;

        /// <summary>The pieces the stand-in is made of now.</summary>
        public ShapeFit.Piece[] Pieces => _pieces;
        private ShapeFit.Piece[] _pieces;

        /// <summary>
        /// Replaces the stand-in's pieces (a weapon whose meshes could only be measured by their bounding boxes gets the
        /// shape fitted to its rendered surface once that is measured).
        /// </summary>
        public void UseShape(ShapeFit.Piece[] pieces, GunVolume volume)
        {
            if (pieces == null || pieces.Length == 0 || _go == null)
            {
                return;
            }
            float excess = -1f, covered = -1f;
            // The before/after comparison is for the self-test report only: in play it would be a hitch on drawing.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            ShapeBefore = volume != null && Core.AutoTest.Requested ? Match(volume, out excess, out covered) : null;
            ReportMatchMs = ShapeBefore != null ? (float)watch.Elapsed.TotalMilliseconds : -1f;
            ShapeBeforeExcess = excess;
            ShapeBeforeCovered = covered;
            foreach (Collider c in _boxes)
            {
                if (c != null)
                {
                    Object.Destroy(c);
                }
            }
            _boxes.Clear();
            ShapeFit.Bake(pieces);
            _pieces = pieces;
            _boxes.AddRange(ShapeFit.AddColliders(_go, pieces));
            PropPhysics.IgnorePlayer(_boxes);
            foreach (Collider c in _passing)
            {
                foreach (Collider b in _boxes)
                {
                    Physics.IgnoreCollision(c, b, true);
                }
            }
            foreach (Collider b in _boxes)
            {
                b.enabled = _enabled;
            }
        }

        /// <summary>
        /// Self-test: of the stand-in's inside, the share outside the visible model ("excess"), and of the model's surface,
        /// the share within 5 mm of the stand-in ("covered").
        /// </summary>
        public string Match(GunVolume volume) => Match(volume, out _, out _);

        public string Match(GunVolume volume, out float excessPercent, out float coveredPercent) => Match(volume, 7, 400, out excessPercent, out coveredPercent);

        /// <summary>
        /// <paramref name="grid"/>³ samples per piece and about <paramref name="surfaceSamples"/> points of the model's
        /// surface: fine for the self-test, coarse for the one-off choice made in play (a draw must not hitch).
        /// </summary>
        public string Match(GunVolume volume, int grid, int surfaceSamples, out float excessPercent, out float coveredPercent) =>
            MatchShape(_boxes, _go.transform, volume, grid, surfaceSamples, out excessPercent, out coveredPercent);

        /// <summary>
        /// How well <paramref name="colliders"/> (in the frame of <paramref name="t"/>, the frame the volume was measured
        /// in) match the visible model: share of their inside outside the model, share of the model's surface covered.
        /// </summary>
        internal static string MatchShape(IList<Collider> colliders, Transform t, GunVolume volume, int grid, int surfaceSamples, out float excessPercent, out float coveredPercent)
        {
            IList<Collider> _boxes = colliders;
            int inside = 0, excess = 0;
            foreach (Collider c in _boxes)
            {
                if (c == null || !c.enabled)
                {
                    continue;
                }
                Bounds b = c.bounds;
                int N = grid;
                for (int x = 0; x < N; x++)
                {
                    for (int y = 0; y < N; y++)
                    {
                        for (int z = 0; z < N; z++)
                        {
                            Vector3 p = b.min + Vector3.Scale(b.size, new Vector3((x + 0.5f) / N, (y + 0.5f) / N, (z + 0.5f) / N));
                            if ((c.ClosestPoint(p) - p).sqrMagnitude > 1e-10f)
                            {
                                continue;
                            }
                            inside++;
                            if (!volume.InsideModelLocal(t.InverseTransformPoint(p)))
                            {
                                excess++;
                            }
                        }
                    }
                }
            }
            List<Vector3> surface = volume.SurfacePoints(6);
            int covered = 0, tested = 0;
            for (int i = 0; i < surface.Count; i += Mathf.Max(1, surface.Count / surfaceSamples))
            {
                Vector3 p = t.TransformPoint(surface[i]);
                tested++;
                foreach (Collider c in _boxes)
                {
                    if (c != null && c.enabled && (c.ClosestPoint(p) - p).sqrMagnitude <= 0.005f * 0.005f)
                    {
                        covered++;
                        break;
                    }
                }
            }
            excessPercent = inside > 0 ? 100f * excess / inside : 0f;
            coveredPercent = tested > 0 ? 100f * covered / tested : 0f;
            return $"{_boxes.Count} pieces, {excessPercent:0}% of it outside the model, {coveredPercent:0}% of the model covered";
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
            ShapeFit.Piece[] boxes = Fit(rig, frame, origin);
            Current?.Destroy();
            Current = new GunCollider(rig, layer, frame, origin, boxes);
            return Current;
        }

        /// <summary>The weapon's shape in the collider frame (origin at the grip, z along the barrel).</summary>
        /// <summary>
        /// The box (in the collider frame) a held weapon's parts can sensibly be in: a stray shadow plane or a far-off lens
        /// flare quad outside it is not part of what you hold.
        /// </summary>
        internal static void Envelope(WeaponRigData d, Quaternion frame, Vector3 origin, out Vector3 envMin, out Vector3 envMax)
        {
            Quaternion inv = Quaternion.Inverse(frame);
            float muzzle = (inv * (d.MuzzlePoint - origin)).z;
            float halfWidth = d.IsPistol ? 0.035f : 0.07f;
            envMin = new Vector3(-halfWidth, -0.22f, d.IsPistol ? -0.07f : -0.45f);
            envMax = new Vector3(halfWidth, 0.16f, Mathf.Max(muzzle, 0.1f) + 0.03f);
            if (d.Action == ActionType.Melee || d.Action == ActionType.Throwable || d.Action == ActionType.NoAmmo)
            {
                // Swords, tools and kits are not built along a barrel: a blade held upright must keep its full length.
                envMin = Vector3.one * -1.8f;
                envMax = Vector3.one * 1.8f;
            }
        }

        internal static ShapeFit.Piece[] Fit(WeaponRig rig, Quaternion frame, Vector3 origin)
        {
            WeaponRigData d = rig.Data;
            Envelope(d, frame, origin, out Vector3 envMin, out Vector3 envMax);

            Matrix4x4 worldFromFrame = Matrix4x4.TRS(rig.ToWorld(origin), rig.GunBody.rotation * frame, Vector3.one);
            ShapeFit.Piece[] fitted = ShapeFit.Fit("held:" + d.Key, VisibleRenderers(rig), worldFromFrame.inverse);
            var kept = new List<ShapeFit.Piece>(fitted.Length);
            foreach (ShapeFit.Piece p in fitted)
            {
                // Pieces outside a sane envelope around the gun (a stray shadow plane, a far-off lens flare quad) are not
                // part of what you hold.
                Bounds b = p.Bounds;
                if (b.max.x < envMin.x || b.max.y < envMin.y || b.max.z < envMin.z || b.min.x > envMax.x || b.min.y > envMax.y || b.min.z > envMax.z)
                {
                    continue;
                }
                kept.Add(p);
            }
            if (kept.Count == 0)
            {
                // No measurable mesh: a slim box from just behind the grip to the muzzle.
                var min = new Vector3(-0.025f, -0.1f, d.IsPistol ? -0.04f : -0.25f);
                var max = new Vector3(0.025f, 0.07f, envMax.z);
                kept.Add(ShapeFit.BoxPiece((min + max) * 0.5f, max - min));
            }
            return kept.ToArray();
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

        /// <summary>The weapon's own visible meshes: no arms, no magazine (it is a prop of its own), nothing the mod added.</summary>
        internal static List<Renderer> VisibleRenderers(WeaponRig rig)
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
            var list = new List<Renderer>();
            foreach (Renderer r in rig.Weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (r.enabled && !skip.Contains(r) && (r is MeshRenderer || r is SkinnedMeshRenderer) && !AddedByMod(r.transform, rig.Weapon.transform))
                {
                    list.Add(r);
                }
            }
            return list;
        }

        /// <summary>The renderer belongs to something the mod attached to the weapon (its objects are all named "RavenfieldVR ...").</summary>
        internal static bool AddedByMod(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
            {
                if (t.name.StartsWith("RavenfieldVR", System.StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Calls <paramref name="visit"/> with the gun-space corners of each visible weapon mesh; false if there were none.</summary>
        private static bool VisitVisibleCorners(WeaponRig rig, System.Action<Vector3> visit)
        {
            bool any = false;
            foreach (Renderer r in VisibleRenderers(rig))
            {
                Bounds local;
                Transform space;
                if (r is SkinnedMeshRenderer smr)
                {
                    local = smr.localBounds;
                    space = smr.rootBone != null ? smr.rootBone : smr.transform;
                }
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                    {
                        continue;
                    }
                    local = mf.sharedMesh.bounds;
                    space = r.transform;
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
            if (_enabled == seated)
            {
                _enabled = !seated;
                foreach (Collider b in _boxes)
                {
                    b.enabled = _enabled;
                }
                _body.detectCollisions = _enabled;
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
                else if (!Touches(c))
                {
                    foreach (Collider b in _boxes)
                    {
                        Physics.IgnoreCollision(c, b, false);
                    }
                    _passing.RemoveAt(i);
                }
            }
        }

        private bool Touches(Collider c)
        {
            Bounds cb = c.bounds;
            foreach (Collider b in _boxes)
            {
                if (b != null && b.enabled && cb.Intersects(b.bounds))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A prop just became physical: if it starts inside the gun it passes through until clear.</summary>
        public void OnPropDropped(Collider c)
        {
            if (c == null || _boxes.Count == 0 || !Touches(c))
            {
                return;
            }
            foreach (Collider b in _boxes)
            {
                Physics.IgnoreCollision(c, b, true);
            }
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
