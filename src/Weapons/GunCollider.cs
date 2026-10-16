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

        /// <summary>
        /// A layer the first-person controller's ground sphere cast (FirstPersonController.FixedUpdate: mask -12945153) and
        /// the game's bullets leave out, nothing in the level uses: where the held gun's stand-in waits while the player moves.
        /// </summary>
        public const int HiddenFromPlayerLayer = 22;

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
        private static Collider[] _bodyScratch = new Collider[64];

        /// <summary>
        /// Something lying in the world stops colliding with the bodies around it (ragdolls, soldiers walking past), so
        /// it stays where it fell; <paramref name="done"/> remembers the bodies already ignored.
        /// </summary>
        private static readonly System.Predicate<Collider> IsGoneCollider = c => c == null;

        public static void IgnoreBodiesNear(IList<Collider> colliders, Vector3 at, float radius, HashSet<Collider> done)
        {
            // A full buffer means more bodies than it holds (a squad around a soldier who fell: about 20 colliders each),
            // and the same first ones come back every time: the rest kept shoving the drop. Grown until all fit.
            int n;
            while ((n = Physics.OverlapSphereNonAlloc(at, radius, _bodyScratch, BodyMask, QueryTriggerInteraction.Ignore)) == _bodyScratch.Length
                   && _bodyScratch.Length < 1024)
            {
                _bodyScratch = new Collider[_bodyScratch.Length * 2];
            }
            for (int i = 0; i < n; i++)
            {
                Collider body = _bodyScratch[i];
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
            // Dead bodies forgotten when it grows past 512 (pruned on every call, it was
            // rescanned each time once a lot of bodies were alive): at most once every 64 frames.
            if (done.Count > 512 && (Time.frameCount & 63) == 0)
            {
                done.RemoveWhere(IsGoneCollider);
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

        /// <summary>
        /// Changes whenever the player's colliders may have been switched off and on (a respawn, a vehicle or ladder, falling
        /// over): Unity forgets an ignored collision pair then, so everything that ignores the player does it again.
        /// </summary>
        public static int PlayerGeneration { get; private set; }
        private static Actor _trackedActor;
        private static int _trackedState = -1;
        private static float _bumpAgainAt = -1f;

        /// <summary>Per frame: notices when the player's colliders were (or are about to be) switched off and on.</summary>
        public static void TrackPlayer()
        {
            Actor actor = LocalPlayer.actor;
            FpsActorController controller = LocalPlayer.controller;
            int state = actor == null ? 0
                : 1 | (actor.dead ? 2 : 0) | (actor.IsSeated() ? 4 : 0) | (actor.IsOnLadder() ? 8 : 0) | (actor.fallenOver ? 16 : 0)
                  | (controller != null && controller.controller != null && controller.controller.enabled ? 32 : 0);
            if (!ReferenceEquals(actor, _trackedActor) || state != _trackedState)
            {
                _trackedActor = actor;
                _trackedState = state;
                PlayerGeneration++;
                _bumpAgainAt = Time.unscaledTime + 0.3f; // colliders the game switches back on a moment later
            }
            else if (_bumpAgainAt > 0f && Time.unscaledTime >= _bumpAgainAt)
            {
                _bumpAgainAt = -1f;
                PlayerGeneration++;
            }
        }

        /// <summary>One of the local player's own colliders (body, controller, hitboxes, ragdoll).</summary>
        public static bool IsPlayerCollider(Collider c)
        {
            if (c == null)
            {
                return false;
            }
            CollectPlayerColliders();
            return PlayerColliderSet.Contains(c);
        }

        private static readonly HashSet<Collider> PlayerColliderSet = new HashSet<Collider>();
        private static int _playerCollidersGeneration = -1;
        private static float _playerCollidersUntil;

        /// <summary>
        /// The player's controller, hitbox and ragdoll colliders: gathered again when the player changed (respawn, seat, ladder)
        /// and at most once a second otherwise (it walks the player's whole hierarchy; it was done every frame).
        /// </summary>
        private static List<Collider> CollectPlayerColliders()
        {
            if (_playerCollidersFrame == Time.frameCount
                || _playerCollidersGeneration == PlayerGeneration && Time.unscaledTime < _playerCollidersUntil)
            {
                return PlayerColliders;
            }
            _playerCollidersFrame = Time.frameCount;
            _playerCollidersGeneration = PlayerGeneration;
            _playerCollidersUntil = Time.unscaledTime + 1f;
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
            PlayerColliderSet.Clear();
            foreach (Collider c in PlayerColliders)
            {
                if (c != null)
                {
                    PlayerColliderSet.Add(c);
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
            _layer = layer;
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
            _body.interpolation = RigidbodyInterpolation.Interpolate; // drawn where it is when the world holds the gun back
            _driver = _go.AddComponent<GunProxyBody>();
            _driver.Body = _body;
            PropPhysics.IgnorePlayer(_boxes);
            _playerGeneration = PropPhysics.PlayerGeneration;
        }

        private int _playerGeneration;
        private readonly int _layer;
        /// <summary>Where the hand wants the gun body this frame (set before rendering); the stand-in chases it.</summary>
        private RigidPose? _handPose;

        /// <summary>
        /// While the game moves the player (the first-person controller's physics step, any move of its character controller),
        /// the held gun's stand-in is out of the player's way: on a layer the controller's ground sphere cast leaves out, and a
        /// trigger. The cast took a gun held down by the feet for a moving platform (it carried the player along with the hand)
        /// and for a steep slope (the player slid), and a character controller pushes itself out of whatever it overlaps
        /// without honouring ignored pairs: 9-12 cm in a second in the autotest. Solid again before the physics step runs.
        /// </summary>
        internal static void HideFromPlayer(bool hide)
        {
            GunCollider g = Current;
            if (g == null || g._go == null || !g._enabled)
            {
                _hiddenDepth = 0;
                return;
            }
            if (hide ? _hiddenDepth++ > 0 : --_hiddenDepth > 0)
            {
                return; // nested (a move inside the controller's step): only the outermost call switches
            }
            _hiddenDepth = Mathf.Max(0, _hiddenDepth);
            g._go.layer = hide ? PropPhysics.HiddenFromPlayerLayer : g._layer;
            foreach (Collider b in g._boxes)
            {
                if (b != null && b.isTrigger != hide)
                {
                    b.isTrigger = hide;
                }
            }
        }

        private static int _hiddenDepth;

        /// <summary>The gun-body pose the hand wants (not where the gun is drawn, which may be held back by the world).</summary>
        public void SetHandPose(RigidPose gunBody) => _handPose = gunBody;

        /// <summary>
        /// The world holds the stand-in back (it touches something solid that does not give way): the gun-body pose it is
        /// stopped at, to draw the gun there.
        /// </summary>
        public bool TryBlockedPose(out RigidPose gunBody)
        {
            gunBody = default;
            if (!_enabled || !_placed || _go == null || !_driver.TouchingWorld)
            {
                return false;
            }
            Transform t = _go.transform;
            Quaternion rot = t.rotation * Quaternion.Inverse(_frame);
            gunBody = new RigidPose(t.position - rot * _origin, rot);
            return true;
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
            _restIgnored.Clear(); // the new pieces start colliding with everything
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
            _nextRestCheck = 0f; // re-ignore the resting props at once with the new boxes, not up to 0.1 s later (they were shoved)
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
            // Only meshes centred in the envelope go into the fit: a far-off one (parked 25 m away) stretched the span the
            // slices are cut over, and every held gun came out in 8 cm slices.
            Matrix4x4 frameFromWorld = worldFromFrame.inverse;
            List<Renderer> renderers = VisibleRenderers(rig);
            List<Renderer> near = renderers.FindAll(r =>
            {
                Vector3 c = frameFromWorld.MultiplyPoint3x4(r.bounds.center);
                return c.x > envMin.x - 0.05f && c.y > envMin.y - 0.05f && c.z > envMin.z - 0.05f
                       && c.x < envMax.x + 0.05f && c.y < envMax.y + 0.05f && c.z < envMax.z + 0.05f;
            });
            ShapeFit.Piece[] fitted = ShapeFit.Fit("held:" + d.Key, near.Count > 0 ? near : renderers, frameFromWorld);
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
            var solid = new List<Renderer>();
            foreach (Renderer r in rig.Weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (r.enabled && !skip.Contains(r) && (r is MeshRenderer || r is SkinnedMeshRenderer) && !AddedByMod(r.transform, rig.Weapon.transform))
                {
                    list.Add(r);
                    if (!SeeThrough(r))
                    {
                        solid.Add(r);
                    }
                }
            }
            // Without a sight's reticle quad or glow (an M4 holo sight's additive quad is 4 m across): magazines bounced off
            // an invisible plane and the hand closed on air above the receiver. All of them for a gun drawn see-through.
            return solid.Count > 0 ? solid : list;
        }

        /// <summary>A reticle, glow or glass: transparent, additive, a HUD or particle shader, or larger than a gun could be.</summary>
        internal static bool SeeThrough(Renderer r)
        {
            Material m = r.sharedMaterial;
            if (m == null)
            {
                return false;
            }
            string shader = m.shader != null ? m.shader.name : "";
            // The size test on the mesh's own bounds at its scale, not the world box: a long launcher tube (about 1.5 m, and its
            // world box larger again when tilted across the axes) was dropped from the hands' solid and the stand-in by a
            // 1.5 m limit on the world box. The 4 m holo quad this test was for is additive and caught by the shader above.
            return m.renderQueue > 2500 || shader.IndexOf("Additive", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || shader.IndexOf("HUD", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || shader.IndexOf("Particle", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || MeshSize(r).sqrMagnitude > 3.5f * 3.5f;
        }

        /// <summary>The renderer's mesh size in metres (its local bounds at its scale): the same in any pose.</summary>
        private static Vector3 MeshSize(Renderer r)
        {
            Mesh mesh = r is SkinnedMeshRenderer skinned ? skinned.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null)
            {
                return r.bounds.size;
            }
            Vector3 s = r.transform.lossyScale;
            return Vector3.Scale(mesh.bounds.size, new Vector3(Mathf.Abs(s.x), Mathf.Abs(s.y), Mathf.Abs(s.z)));
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
            if (_go != null && _go.layer != _layer)
            {
                // A move of the player threw before its postfix put the stand-in back (Harmony skips postfixes then). Here,
                // outside any move, it is solid again: never a ghost the gun passes through walls and props with.
                _hiddenDepth = 0;
                _go.layer = _layer;
                foreach (Collider b in _boxes)
                {
                    if (b != null)
                    {
                        b.isTrigger = false;
                    }
                }
            }
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
                // Switching the pieces off made Unity forget every ignored pair: resting props are judged again from scratch
                // (they were still marked ignored and got shoved), and nothing is passing through any more.
                _restIgnored.Clear();
                _passing.Clear();
            }
            if (seated)
            {
                return;
            }
            PropPhysics.TrackPlayer();
            if (_playerGeneration != PropPhysics.PlayerGeneration)
            {
                // The player's colliders were switched off and on: Unity forgot the ignored pairs, and the stand-in pressed
                // against the player's own controller (which pushes itself out of whatever overlaps it: a long gun held
                // down against the ground pushed the player around).
                _playerGeneration = PropPhysics.PlayerGeneration;
                PropPhysics.IgnorePlayer(_boxes);
            }
            Vector3 pos = _handPose.HasValue ? _handPose.Value.TransformPoint(_origin) : _rig.ToWorld(_origin);
            Quaternion rot = (_handPose.HasValue ? _handPose.Value.rotation : _rig.GunBody.rotation) * _frame;
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
                    bool resting = c.attachedRigidbody != null && _restIgnored.TryGetValue(c.attachedRigidbody, out bool r) && r;
                    if (!resting)
                    {
                        foreach (Collider b in _boxes)
                        {
                            Physics.IgnoreCollision(c, b, false);
                        }
                    }
                    _passing.RemoveAt(i);
                }
            }
            UpdateRestingProps();
        }

        /// <summary>Loose things in the world and whether the stand-in currently ignores them because they lie still.</summary>
        private readonly Dictionary<Rigidbody, bool> _restIgnored = new Dictionary<Rigidbody, bool>();
        private readonly List<Rigidbody> _restScratch = new List<Rigidbody>();
        private float _nextRestCheck;

        /// <summary>
        /// Weapons and magazines lying still on the ground are not shoved by the gun in your hand: reaching down with the gun
        /// held low (or the stand-in jumping back to the gun) pushed them away, so things you wanted to pick up floated off.
        /// Something falling or flying still bounces off the gun; one lying still is ignored until it moves again.
        /// </summary>
        private void UpdateRestingProps()
        {
            if (Time.unscaledTime < _nextRestCheck)
            {
                return;
            }
            _nextRestCheck = Time.unscaledTime + 0.1f;
            WeaponDrops drops = WeaponDrops.Instance;
            if (drops != null)
            {
                IReadOnlyList<DroppedWeapon> all = drops.All;
                for (int i = 0; i < all.Count; i++)
                {
                    DroppedWeapon d = all[i];
                    if (d.Go != null)
                    {
                        SetRestIgnored(d.Body, d.Colliders, Time.time - d.DropTime);
                    }
                }
            }
            IReadOnlyList<MagazineProp> mags = MagazineProp.All;
            for (int i = 0; i < mags.Count; i++)
            {
                MagazineProp m = mags[i];
                if (m.IsDropped && m.Go != null)
                {
                    SetRestIgnored(m.Body, m.Colliders, m.DroppedFor);
                }
            }
            if (_restIgnored.Count > 64)
            {
                _restScratch.Clear();
                foreach (KeyValuePair<Rigidbody, bool> kv in _restIgnored)
                {
                    if (kv.Key == null)
                    {
                        _restScratch.Add(kv.Key);
                    }
                }
                foreach (Rigidbody gone in _restScratch)
                {
                    _restIgnored.Remove(gone);
                }
            }
        }

        private void SetRestIgnored(Rigidbody body, IList<Collider> colliders, float lyingFor)
        {
            if (body == null || colliders == null || body.isKinematic)
            {
                return;
            }
            bool known = _restIgnored.TryGetValue(body, out bool ignored);
            float speedSq = body.velocity.sqrMagnitude;
            // Settled for a moment and slow; once ignored, only a real push or fall (above 0.8 m/s) makes it collide again.
            bool want = ignored ? speedSq < 0.8f * 0.8f : lyingFor > 0.75f && (body.IsSleeping() || speedSq < 0.25f * 0.25f && body.angularVelocity.sqrMagnitude < 1f);
            if (known && want == ignored)
            {
                return;
            }
            _restIgnored[body] = want;
            if (!known && !want)
            {
                return; // never ignored: nothing to undo
            }
            foreach (Collider c in colliders)
            {
                if (c == null || !c.enabled || !want && _passing.Contains(c))
                {
                    continue;
                }
                foreach (Collider b in _boxes)
                {
                    if (b != null && b.enabled)
                    {
                        Physics.IgnoreCollision(c, b, want);
                    }
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
            if (c != null && c.attachedRigidbody != null)
            {
                // Picked up, its colliders were switched off and Unity forgot its ignored pairs with the gun: a magazine
                // that lay still before was still marked ignored, so one set down gently was shoved by the gun again.
                _restIgnored.Remove(c.attachedRigidbody);
            }
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
        private const float SnapDistanceSq = 0.45f * 0.45f; // held back 45 cm (the hand pushed through a wall): jump back to it
        private const float MaxSpeed = 25f;

        public Rigidbody Body;
        public Vector3 TargetPos;
        public Quaternion TargetRot = Quaternion.identity;
        public bool HasTarget;

        /// <summary>Touching something solid that does not give way (the level, a vehicle, a body) in the last moment.</summary>
        public bool TouchingWorld => Time.time < _worldContactUntil;
        private float _worldContactUntil;
        /// <summary>A moving body this heavy (a vehicle) holds the gun back like the level; lighter ones are pushed.</summary>
        private const float HeavyMass = 30f;

        private void OnCollisionEnter(Collision c) => Touch(c);
        private void OnCollisionStay(Collision c) => Touch(c);

        private void Touch(Collision c)
        {
            Rigidbody other = c.rigidbody;
            if (other != null && !other.isKinematic && other.mass < HeavyMass)
            {
                return; // loose props and ragdoll limbs are pushed: they do not hold the gun back
            }
            if (PropPhysics.IsPlayerCollider(c.collider))
            {
                return; // never the player's own body
            }
            // In game time, a few physics steps long: in the game's slow motion the steps come further apart in real time,
            // and a real-time window flickered between held back and free.
            _worldContactUntil = Time.time + Mathf.Max(0.12f, Time.fixedDeltaTime * 3f);
        }

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

    /// <summary>The player's own physics step never meets the held gun's stand-in (see <see cref="GunCollider.HideFromPlayer"/>).</summary>
    [HarmonyLib.HarmonyPatch(typeof(UnityStandardAssets.Characters.FirstPerson.FirstPersonController), "FixedUpdate")]
    internal static class PlayerStepPastGunPatch
    {
        private static void Prefix(UnityStandardAssets.Characters.FirstPerson.FirstPersonController __instance, out bool __state)
        {
            FpsActorController player = LocalPlayer.controller;
            __state = Core.VRManager.Active && player != null && ReferenceEquals(player.controller, __instance);
            if (__state)
            {
                GunCollider.HideFromPlayer(true);
            }
        }

        private static void Postfix(bool __state)
        {
            if (__state)
            {
                GunCollider.HideFromPlayer(false);
            }
        }
    }

    /// <summary>Any other move of the player's character controller (real steps in the room) past the held gun's stand-in.</summary>
    [HarmonyLib.HarmonyPatch(typeof(CharacterController), nameof(CharacterController.Move))]
    internal static class PlayerMovePastGunPatch
    {
        private static void Prefix(CharacterController __instance, out bool __state)
        {
            FpsActorController player = LocalPlayer.controller;
            __state = Core.VRManager.Active && GunCollider.Current != null && player != null && player.controller != null
                      && player.controller.gameObject == __instance.gameObject;
            if (__state)
            {
                GunCollider.HideFromPlayer(true);
            }
        }

        private static void Postfix(bool __state)
        {
            if (__state)
            {
                GunCollider.HideFromPlayer(false);
            }
        }
    }
}
