using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Spent cases you can see land, as in Boneworks: a self-loading gun throws a brass case out of its ejection port with
    /// every shot, a bolt or pump throws the case (or the live round) out when it is worked, a revolver or a break-action
    /// drops its fired cases when opened. They tumble, bounce with a tink and lie on the ground for a while (40 at most,
    /// the oldest go first). Only for weapons that eject cases in the flat game (they have casing particles, which the
    /// held weapon no longer shows): an energy weapon stays clean.
    /// </summary>
    internal static class Casings
    {
        private const int Max = 40;
        private const float Lifetime = 25f;

        private enum Kind { Pistol, Rifle, Heavy, Shell }

        /// <summary>One case object, made once and used again (a shot used to build and later destroy six objects).</summary>
        internal sealed class Case
        {
            public GameObject Go;
            public float Born;
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public CapsuleCollider Collider;
            public Rigidbody Body;
            public CaseSound Sound;
            /// <summary>A live round (it can be loaded into a magazine by hand: HandLoading), not a fired case.</summary>
            public bool Live;
            /// <summary>In a hand (HandLoading): kinematic, its collider off, not put away for its age.</summary>
            public bool Held;
            /// <summary>The hand it is in while <see cref="Held"/>.</summary>
            public Rig.VRHand Holder;
            /// <summary>Whose a live round is (the weapon's analysis key): it goes into that weapon's magazines only; null for none.</summary>
            public string Key;
            /// <summary>The reserve a live round came out of: it goes back there when it is put away unused (null: lost, as one racked out).</summary>
            public AmmoState Owner;
        }

        private static readonly List<Case> Alive = new List<Case>();
        /// <summary>Cases put away (switched off), ready for the next shot.</summary>
        private static readonly List<Case> Idle = new List<Case>();
        private static Mesh[] _meshes, _liveMeshes;
        private static Material _brass, _copper, _hull;
        // The renderer copies the list it is given: one shared list per look, not a new one per shot.
        private static Material[] _caseMats, _liveMats, _shellMats;
        private static PhysicMaterial _bounce;

        private static Material[] MaterialsFor(Kind kind, bool live) => kind == Kind.Shell ? _shellMats : live ? _liveMats : _caseMats;

        /// <summary>
        /// A shotgun's own shell for the shells it throws out: the biggest plain mesh of its shell part (the hull, not its
        /// crimp or head), with its materials and its size in the world, and the turn from a made shell's length (z) onto its own.
        /// </summary>
        private sealed class OwnShell
        {
            public Mesh Mesh;
            public Material[] Materials;
            public Vector3 Scale;
            public Quaternion Turn;
            public int Axis;
        }

        /// <summary>Per weapon (null where it has no shell of its own to show).</summary>
        private static readonly Dictionary<string, OwnShell> OwnShells = new Dictionary<string, OwnShell>();

        /// <summary>
        /// The gun's own shell for its thrown shells, when its shell part (the reload's, or the one in its chamber) is a shell's
        /// size and drawn by a plain mesh: they were the mod's made 12-gauge shell on every shotgun (user, 2026-10-22: "shotguns
        /// are not displayed with their real shells"). Read at its real size: hidden while held, it is scaled to nothing.
        /// </summary>
        private static OwnShell OwnShellOf(HeldWeapon held, Kind kind)
        {
            WeaponRig rig = held.ActiveRig;
            if (kind != Kind.Shell || rig.Magazine == null || !WeaponAnalyzer.ShellSized(rig.Data.MagBounds))
            {
                return null;
            }
            string key = rig.Data.Key ?? "";
            if (OwnShells.TryGetValue(key, out OwnShell known) && (known == null || known.Mesh != null))
            {
                return known; // (made again when a content reload destroyed its mesh)
            }
            OwnShell own = null;
            MeshFilter best = null;
            float size = 0f;
            foreach (MeshFilter f in rig.Magazine.GetComponentsInChildren<MeshFilter>(true))
            {
                if (f.sharedMesh != null && f.GetComponent<MeshRenderer>() != null && f.sharedMesh.bounds.size.magnitude > size)
                {
                    size = f.sharedMesh.bounds.size.magnitude;
                    best = f;
                }
            }
            if (best != null)
            {
                Vector3[] hidden = rig.ShowMagazineForCopy();
                Vector3 lossy;
                try
                {
                    lossy = best.transform.lossyScale;
                }
                finally
                {
                    rig.RestoreMagazineScales(hidden);
                }
                Vector3 sized = Vector3.Scale(best.sharedMesh.bounds.size, new Vector3(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z)));
                int axis = sized.x >= sized.y && sized.x >= sized.z ? 0 : sized.y >= sized.z ? 1 : 2;
                if (sized[axis] > 0.03f && sized[axis] < 0.15f)
                {
                    Vector3 along = Vector3.zero;
                    along[axis] = 1f;
                    own = new OwnShell
                    {
                        Mesh = best.sharedMesh, Materials = best.GetComponent<MeshRenderer>().sharedMaterials, Scale = lossy,
                        Turn = Quaternion.FromToRotation(along, Vector3.forward), Axis = axis,
                    };
                }
            }
            OwnShells[key] = own;
            Log.InfoOnce("ownshell:" + key, own != null
                ? $"Shells thrown by {key}: its own ('{best.name}', {own.Mesh.bounds.size[own.Axis] * Mathf.Abs(own.Scale[own.Axis]) * 100f:0.0} cm long)."
                : $"Shells thrown by {key}: the made 12-gauge shell (its shell part has no plain mesh of a shell's size).");
            return own;
        }

        /// <summary>Whether the held gun throws its own shells (the autotest says so).</summary>
        internal static bool ThrowsOwnShells(HeldWeapon held) => held != null && held.ActiveWeapon != null && OwnShellOf(held, KindOf(held)) != null;

        /// <summary>The weapon throws cases in the flat game (and so in VR).</summary>
        public static bool Ejects(HeldWeapon held)
        {
            Weapon w = held?.ActiveWeapon;
            ParticleSystem[] p = w != null ? w.configuration.casingParticles : null;
            return p != null && p.Length > 0 && p[0] != null;
        }

        /// <summary>
        /// Throws one spent case (or a live round) out of the held weapon's ejection port. With magazines loaded by hand a live
        /// round racked out (and lost by the rack, LoseRoundOnRack) is that weapon's: picked up, it can be loaded again.
        /// </summary>
        public static void Eject(HeldWeapon held, bool live = false)
        {
            if (held == null || !Ejects(held) || held.ActiveRig.GunBody == null)
            {
                return;
            }
            WeaponRig rig = held.ActiveRig;
            WeaponRigData d = rig.Data;
            Transform port = held.ActiveWeapon.configuration.casingParticles[0].transform;
            Vector3 right = rig.DirToWorld(Vector3.Cross(d.Up, d.AimAxis).normalized);
            Vector3 up = rig.UpWorld, back = -rig.AimWorld;
            Vector3 hand = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Dominant.Velocity : Vector3.zero;
            Vector3 velocity = hand + right * Random.Range(1.8f, 2.6f) + up * Random.Range(0.8f, 1.5f) + back * Random.Range(0.1f, 0.5f);
            // The port is where the gun was drawn last frame (it is placed again before this frame renders): led by the hand's
            // motion over a frame, or on a swing the case came out a couple of centimetres behind the port (visual hunt, 2026-10-20).
            Vector3 lead = hand * Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            Kind kind = KindOf(held);
            Case c = Spawn(kind, live, port.position + lead + right * 0.01f, Quaternion.LookRotation(right, up), velocity,
                Random.insideUnitSphere * 25f + rig.DirToWorld(d.Up) * 10f, OwnShellOf(held, kind));
            if (live && HandLoading.Enabled && VRConfig.LoseRoundOnRack.Value && kind != Kind.Shell)
            {
                c.Key = d.Key;
            }
        }

        /// <summary>Fired cases falling out of an opened cylinder or break-action, straight down out of the chambers.</summary>
        public static void Drop(HeldWeapon held, int count, Vector3 chambers, bool live = false)
        {
            // Whatever its flat casing particles: an opened break action or cylinder always lets its fired cases out (the
            // vanilla DOUBLE-BARREL and SAWED-OFF have none, and their shells stayed in; user, 2026-10-16). The count was
            // already taken from the gun, so returning here lost them.
            if (held == null || held.ActiveWeapon == null)
            {
                return;
            }
            Kind kind = KindOf(held);
            OwnShell own = OwnShellOf(held, kind);
            Vector3 aim = held.ActiveRig.AimWorld;
            for (int i = 0; i < Mathf.Min(count, 8); i++)
            {
                Vector3 at = chambers + Random.insideUnitSphere * 0.012f;
                Spawn(kind, live, at, Quaternion.LookRotation(-aim, Vector3.up), Vector3.down * Random.Range(0.3f, 0.9f) - aim * 0.4f, Random.insideUnitSphere * 8f, own);
            }
        }

        private static Kind KindOf(HeldWeapon held)
        {
            WeaponRigData d = held.ActiveRig.Data;
            Weapon.Configuration c = held.ActiveWeapon.configuration;
            // A slug gun's are shells too, when its own shell part is one (the R870 [SLUG]'s and M1878 Slugs' were rifle cases).
            if ((c.projectilesPerShot > 1 || held.ActiveRig.Magazine != null && WeaponAnalyzer.ShellSized(d.AnalysedMagBounds)) // (not a copy's box)
                && (d.Action == ActionType.ShellFed || d.Action == ActionType.BreakOpen || d.Action == ActionType.Manual))
            {
                return Kind.Shell;
            }
            if (d.Handgun)
            {
                return Kind.Pistol;
            }
            float length = Mathf.Abs(Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis));
            return KindOf(d);
        }

        /// <summary>The round of a magazine-fed weapon by its analysis alone (no gun drawn: a magazine in a free hand).</summary>
        private static Kind KindOf(WeaponRigData d)
        {
            if (d == null)
            {
                return Kind.Rifle;
            }
            if (d.Handgun)
            {
                return Kind.Pistol;
            }
            float length = Mathf.Abs(Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis));
            return d.IsMachineGun || d.Action == ActionType.Manual && length > 0.55f ? Kind.Heavy : Kind.Rifle;
        }

        private static Case Spawn(Kind kind, bool live, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 spin, OwnShell own = null)
        {
            EnsureAssets();
            Cull();
            Case c;
            int oldest = Alive.FindIndex(a => !a.Held); // (never one in a hand)
            if (Alive.Count >= Max && oldest >= 0)
            {
                c = Alive[oldest]; // the oldest one lying around is taken
                Alive.RemoveAt(oldest);
                GiveBack(c);
            }
            else if (Idle.Count > 0)
            {
                c = Idle[Idle.Count - 1];
                Idle.RemoveAt(Idle.Count - 1);
            }
            else
            {
                c = Build();
            }
            GameObject go = c.Go;
            // Placed while switched off: switched on again it starts here, with no interpolation from where it lay.
            go.SetActive(false);
            go.name = live ? "RavenfieldVR Round" : "RavenfieldVR Case";
            int layer = PropPhysics.Layer;
            go.layer = layer >= 0 ? layer : 0; // bullets pass through it
            go.transform.SetPositionAndRotation(position, own != null ? rotation * own.Turn : rotation);
            go.transform.localScale = own != null ? own.Scale : Vector3.one; // (a pooled case may have been another gun's shell)
            Mesh mesh = own != null ? own.Mesh : live ? _liveMeshes[(int)kind] : _meshes[(int)kind];
            c.Filter.sharedMesh = mesh;
            c.Renderer.sharedMaterials = own != null ? own.Materials : MaterialsFor(kind, live);
            Bounds b = mesh.bounds;
            int axis = own != null ? own.Axis : 2;
            c.Collider.center = b.center;
            c.Collider.direction = axis;
            c.Collider.radius = own != null ? Mathf.Max(b.extents[(axis + 1) % 3], b.extents[(axis + 2) % 3]) : Mathf.Max(b.extents.x, 0.003f);
            c.Collider.height = b.size[axis];
            c.Body.mass = kind == Kind.Shell ? 0.03f : 0.012f;
            c.Body.isKinematic = false;
            c.Collider.enabled = true;
            c.Live = live;
            c.Held = false;
            c.Holder = null;
            c.Key = null;
            c.Owner = null;
            c.Body.interpolation = RigidbodyInterpolation.Interpolate; // (in flight smooth again: lying still it had it off, RestingBodies)
            c.Sound.Rearm();
            go.SetActive(true);
            c.Body.velocity = velocity;
            c.Body.angularVelocity = spin;
            // Switching the collider off and on made Unity forget its ignored pairs: the player and the gun again.
            PropPhysics.IgnorePlayer(c.Collider);
            GunCollider.Current?.OnPropDropped(c.Collider); // out of the port it passes through the gun
            c.Born = Time.time;
            Alive.Add(c);
            Spawned++;
            return c;
        }

        /// <summary>A live round put away unused goes back to the reserve it came out of (HandLoading: from the pouch, or out of a magazine).</summary>
        private static void GiveBack(Case c)
        {
            if (c.Live && c.Owner != null && c.Owner.Weapon != null)
            {
                c.Owner.ReturnSpareCapped(1);
            }
            c.Owner = null;
        }

        // ---- in the hand (HandLoading) ---------------------------------------------------------------------

        /// <summary>Where a held case or round sits against the palm (palm frame): its middle between the thumb and the fingers' first joints.</summary>
        private static readonly Vector3 HeldAt = new Vector3(0f, 0.012f, 0.05f);
        /// <summary>
        /// Where it sits with the weapon's arms drawn (palm frame, +y the thumb's side): standing on top of the hand, tip up and a little
        /// forward, as a round held up between thumb and finger. Straight out past the fingertips (as it was) it pointed away along the hand, a
        /// 1 cm end behind the fist from where the eye is, and was not seen (user, 2026-11-04: "Bullets are also still invisible").
        /// </summary>
        private static readonly Vector3 HeldOut = new Vector3(0f, 0.055f, 0.05f);
        private static readonly Quaternion HeldOutTurn = new Quaternion(-0.4964f, 0f, 0f, 0.8681f); // (its length, +z, up and forward: about the palm's x, 60 degrees)

        /// <summary>Whether a collider is one of the cases or rounds lying around (bullets push them).</summary>
        internal static bool IsCase(Collider c) => c != null && c.GetComponent<CaseSound>() != null;

        /// <summary>The nearest case or round lying within <paramref name="radius"/> of <paramref name="point"/>, taken into <paramref name="hand"/>; null for none.</summary>
        internal static Case Take(Rig.VRHand hand, Vector3 point, float radius)
        {
            Case best = null;
            float bestSq = radius * radius;
            foreach (Case c in Alive)
            {
                if (c.Held || c.Go == null || !c.Go.activeSelf)
                {
                    continue;
                }
                float d = (CentreOf(c) - point).sqrMagnitude;
                if (d < bestSq)
                {
                    bestSq = d;
                    best = c;
                }
            }
            if (best != null)
            {
                Hold(best, hand);
            }
            return best;
        }

        private static void Hold(Case c, Rig.VRHand hand)
        {
            c.Held = true;
            c.Holder = hand;
            c.Body.isKinematic = true;
            c.Collider.enabled = false;
            c.Go.layer = MagazineProp.DisplayLayer; // drawn as what else is held: the first-person layer the view always has
        }

        /// <summary>A live round of the weapon <paramref name="d"/> (key <paramref name="key"/>, out of <paramref name="owner"/>'s reserve) made in a hand.</summary>
        internal static Case RoundInHand(WeaponRigData d, string key, AmmoState owner, Rig.VRHand hand)
        {
            Case c = Spawn(KindOf(d), true, hand.Palm.position, hand.Palm.rotation, Vector3.zero, Vector3.zero);
            c.Key = key;
            c.Owner = owner;
            Hold(c, hand);
            Follow(c, hand);
            return c;
        }

        /// <summary>What a live round of the weapon <paramref name="d"/> is drawn with (the pouch shows one, loaded by hand).</summary>
        internal static void RoundLook(WeaponRigData d, out Mesh mesh, out Material[] materials)
        {
            EnsureAssets();
            Kind kind = KindOf(d);
            mesh = _liveMeshes[(int)kind];
            materials = MaterialsFor(kind, true);
        }

        /// <summary>A live round of the weapon <paramref name="d"/> let out of a magazine's top, flying.</summary>
        internal static Case RoundOut(WeaponRigData d, string key, AmmoState owner, Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            Case c = Spawn(KindOf(d), true, position, rotation, velocity, Random.insideUnitSphere * 6f);
            c.Key = key;
            c.Owner = owner;
            return c;
        }

        /// <summary>
        /// The pinch's line in the palm's frame: along the fingers, tipped 22 degrees toward the palm's heel (MagazineProp's shell
        /// pinch, from tools/hands/grip_check.py).
        /// </summary>
        private static readonly Vector3 PinchAxis = new Vector3(0f, -0.3714f, 0.9285f);

        /// <summary>
        /// A held case or round in the hand's fingers: its middle at the glove's pinch (between the thumb and the index tips), along
        /// the pinch's line, as the glove's shell pose holds a shell; without a glove a little out along the fingers.
        /// </summary>
        internal static void Follow(Case c, Rig.VRHand hand)
        {
            Quaternion rot = hand.Palm.rotation;
            Vector3 at = hand.Palm.position + rot * HeldAt;
            if (hand.Model != null && hand.Model.Suppressed)
            {
                // The weapon's arms drawn instead of the glove: its fist closes round the glove's pinch, and a round held there was not seen
                // (user, 2026-11-03: "the bullets are invisible"). Stood on top of the hand instead (HeldOut).
                at = hand.Palm.position + rot * HeldOut;
                rot *= HeldOutTurn;
            }
            else if (hand.Model != null)
            {
                Vector3 axis = rot * PinchAxis;
                Vector3 up = Vector3.ProjectOnPlane(hand.Palm.Up, axis);
                at = hand.Palm.TransformPoint(hand.Model.PinchInPalm);
                rot = Quaternion.LookRotation(axis, up.sqrMagnitude > 1e-6f ? up : hand.Palm.Up);
            }
            Transform t = c.Go.transform;
            t.SetPositionAndRotation(at - rot * Vector3.Scale(c.Collider.center, t.localScale), rot);
        }

        /// <summary>A held case or round let go: flying again with the hand's throw, put away for its age from now.</summary>
        internal static void LetGo(Case c, Vector3 velocity, Vector3 spin)
        {
            c.Held = false;
            c.Holder = null;
            int layer = PropPhysics.Layer;
            c.Go.layer = layer >= 0 ? layer : 0; // (bullets pass through it again)
            c.Collider.enabled = true;
            c.Body.isKinematic = false;
            c.Body.interpolation = RigidbodyInterpolation.Interpolate;
            c.Body.velocity = velocity;
            c.Body.angularVelocity = spin;
            PropPhysics.IgnorePlayer(c.Collider); // (switched off and on, the collider forgot its ignored pairs)
            c.Born = Time.time;
        }

        /// <summary>A round loaded into a magazine: put away, nothing back to the reserve (it is in the magazine now).</summary>
        internal static void LoadedAway(Case c)
        {
            c.Held = false;
            c.Holder = null;
            c.Owner = null;
            c.Body.isKinematic = false;
            c.Collider.enabled = true;
            c.Go.SetActive(false);
            Alive.Remove(c);
            Idle.Add(c);
        }

        /// <summary>Where a case or round's middle is.</summary>
        internal static Vector3 CentreOf(Case c) => c.Go.transform.TransformPoint(c.Collider.center);

        /// <summary>A new case object (made only until the pool holds <see cref="Max"/>).</summary>
        private static Case Build()
        {
            var go = new GameObject("RavenfieldVR Case");
            go.SetActive(false);
            var c = new Case { Go = go };
            c.Filter = go.AddComponent<MeshFilter>();
            c.Renderer = go.AddComponent<MeshRenderer>();
            c.Renderer.shadowCastingMode = ShadowCastingMode.Off;
            c.Collider = go.AddComponent<CapsuleCollider>();
            c.Collider.direction = 2;
            c.Collider.sharedMaterial = _bounce;
            c.Body = go.AddComponent<Rigidbody>();
            c.Body.drag = 0.1f;
            c.Body.angularDrag = 0.2f;
            c.Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            c.Body.interpolation = RigidbodyInterpolation.Interpolate;
            RestingBodies.Add(c.Body);
            c.Sound = go.AddComponent<CaseSound>();
            Built++;
            return c;
        }

        private static float _nextFallCheck;

        /// <summary>Puts away cases past their time (also called per frame by the weapon system).</summary>
        public static void Cull()
        {
            float now = Time.time;
            // The out-of-the-world fall-out is a transform read per live case: it only matters when a case drops below the
            // map, so it is tested twice a second rather than every frame. A case is then put away up to 0.5 s late (unseen).
            bool checkFall = now >= _nextFallCheck;
            if (checkFall)
            {
                _nextFallCheck = now + 0.5f;
            }
            for (int i = Alive.Count - 1; i >= 0; i--)
            {
                Case c = Alive[i];
                if (c.Go == null)
                {
                    Alive.RemoveAt(i); // gone with its scene
                    continue;
                }
                if (c.Held && (c.Holder == null || !ReferenceEquals(c.Holder.Held, c)))
                {
                    // Its hand holds something else now (a gun drawn into it, its role reset by the reload): it falls from where it
                    // was, rather than hanging there for good, out of the pool.
                    LetGo(c, Vector3.zero, Vector3.zero);
                }
                if (!c.Held && (now - c.Born > Lifetime || (checkFall && c.Go.transform.position.y < -500f)))
                {
                    GiveBack(c);
                    c.Go.SetActive(false);
                    Alive.RemoveAt(i);
                    Idle.Add(c);
                }

            }
        }

        private static GameObject _chamber;
        private static bool _chamberLive;
        private static OwnShell _chamberOwn;

        /// <summary>
        /// Press check: with the slide or bolt eased back part way, the round in the chamber (or a bolt gun's fired case)
        /// shows in the ejection port. Hidden otherwise.
        /// </summary>
        public static void ShowChamber(HeldWeapon held, bool show, bool live)
        {
            show &= held != null && Ejects(held) && held.ActiveRig.GunBody != null;
            if (!show)
            {
                if (_chamber != null && _chamber.activeSelf)
                {
                    _chamber.SetActive(false);
                }
                return;
            }
            EnsureAssets();
            Kind kind = KindOf(held);
            if (_chamber == null)
            {
                _chamber = new GameObject("RavenfieldVR Chambered Round") { layer = MagazineProp.DisplayLayer };
                Object.DontDestroyOnLoad(_chamber);
                _chamber.AddComponent<MeshFilter>();
                var mr = _chamber.AddComponent<MeshRenderer>();
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
            MeshFilter mf = _chamber.GetComponent<MeshFilter>();
            // The gun's own shell, as the ones it throws out (the port showed the made 12-gauge shell while the gun ejected its
            // own; props scout, 2026-10-24).
            OwnShell own = OwnShellOf(held, kind);
            Mesh mesh = own != null ? own.Mesh : live ? _liveMeshes[(int)kind] : _meshes[(int)kind];
            if (mf.sharedMesh != mesh || _chamberLive != live || !ReferenceEquals(_chamberOwn, own))
            {
                mf.sharedMesh = mesh;
                _chamberLive = live;
                _chamberOwn = own;
                _chamber.GetComponent<MeshRenderer>().sharedMaterials = own != null ? own.Materials : MaterialsFor(kind, live);
                _chamber.transform.localScale = own != null ? own.Scale : Vector3.one;
            }
            WeaponRig rig = held.ActiveRig;
            Transform port = held.ActiveWeapon.configuration.casingParticles[0].transform;
            Vector3 aim = rig.AimWorld;
            // In the port, head toward the shooter, the length of a case back from its mouth.
            Vector3 right = rig.DirToWorld(Vector3.Cross(rig.Data.Up, rig.Data.AimAxis).normalized);
            float length = own != null ? mesh.bounds.size[own.Axis] * own.Scale[own.Axis] : mesh.bounds.size.z;
            Quaternion look = Quaternion.LookRotation(aim, rig.UpWorld);
            _chamber.transform.SetPositionAndRotation(port.position - right * 0.004f - aim * length * 0.5f, own != null ? look * own.Turn : look);
            if (!_chamber.activeSelf)
            {
                _chamber.SetActive(true);
            }
        }

        /// <summary>Cases and rounds thrown since start (self-test).</summary>
        internal static int Spawned { get; private set; }

        /// <summary>How many cases lie around (self-test).</summary>
        internal static int Count => Alive.Count;

        /// <summary>
        /// Self-test: of a turned mesh's side faces (about +Z), how many face away from the axis, so they are drawn from
        /// outside (Unity draws a triangle whose corners run clockwise as seen; these were all wound the other way once).
        /// </summary>
        internal static void CountFacingOut(Mesh mesh, ref int outward, ref int total)
        {
            Vector3[] v = mesh.vertices;
            for (int sm = 0; sm < mesh.subMeshCount; sm++)
            {
                int[] t = mesh.GetTriangles(sm);
                for (int i = 0; i + 2 < t.Length; i += 3)
                {
                    Vector3 a = v[t[i]], b = v[t[i + 1]], c = v[t[i + 2]];
                    Vector3 n = Vector3.Cross(b - a, c - a);
                    Vector3 centre = (a + b + c) / 3f;
                    var radial = new Vector3(centre.x, centre.y, 0f);
                    if (n.sqrMagnitude < 1e-20f || radial.sqrMagnitude < 1e-12f || Mathf.Abs(n.z) > new Vector2(n.x, n.y).magnitude)
                    {
                        continue; // degenerate, on the axis, or an end face
                    }
                    total++;
                    if (Vector3.Dot(n, radial) > 0f)
                    {
                        outward++;
                    }
                }
            }
        }

        /// <summary>Self-test line: the cases', rounds' and shells' meshes and the belt's round are drawn from outside.</summary>
        internal static string MeshCheck()
        {
            EnsureAssets();
            int outward = 0, total = 0;
            for (int k = 0; k < _meshes.Length; k++)
            {
                CountFacingOut(_meshes[k], ref outward, ref total);
                if (_liveMeshes[k] != _meshes[k])
                {
                    CountFacingOut(_liveMeshes[k], ref outward, ref total);
                }
            }
            BeltStrip.CountFacingOut(ref outward, ref total);
            return $"{outward}/{total} side faces drawn from outside{(outward < total ? " (INSIDE OUT)" : "")}";
        }

        /// <summary>Case objects made since start (self-test: stays at the pool size however long you shoot).</summary>
        internal static int Built { get; private set; }

        public static void DestroyAll()
        {
            if (_chamber != null)
            {
                _chamber.SetActive(false);
            }
            foreach (Case c in Alive)
            {
                GiveBack(c); // (a live round out of a reserve still carried: back into it, as when it is put away for its age)
                if (c.Go != null)
                {
                    Object.Destroy(c.Go);
                }
            }
            foreach (Case c in Idle)
            {
                if (c.Go != null)
                {
                    Object.Destroy(c.Go);
                }
            }
            Alive.Clear();
            Idle.Clear();
        }

        private static void EnsureAssets()
        {
            if (_meshes != null)
            {
                return;
            }
            Shader s = Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse");
            _brass = new Material(s) { name = "RavenfieldVR case brass", color = new Color(0.78f, 0.6f, 0.27f) };
            _copper = new Material(s) { name = "RavenfieldVR bullet", color = new Color(0.66f, 0.38f, 0.22f) };
            _hull = new Material(s) { name = "RavenfieldVR shell hull", color = new Color(0.55f, 0.08f, 0.06f) };
            _caseMats = new[] { _brass };
            _liveMats = new[] { _brass, _copper };
            _shellMats = new[] { _hull, _brass };
            if (_brass.HasProperty("_Metallic"))
            {
                _brass.SetFloat("_Metallic", 0.9f);
                _brass.SetFloat("_Glossiness", 0.6f);
                _copper.SetFloat("_Metallic", 0.85f);
                _copper.SetFloat("_Glossiness", 0.5f);
                _hull.SetFloat("_Glossiness", 0.35f);
            }
            _bounce = new PhysicMaterial("RavenfieldVR case") { bounciness = 0.35f, dynamicFriction = 0.5f, staticFriction = 0.6f, bounceCombine = PhysicMaterialCombine.Maximum };
            // Profiles along +Z (z, radius), case head at 0: 9 mm, 5.56 mm, 7.62 mm, 12 gauge.
            float[][] z =
            {
                new[] { 0f, 0f, 0.019f, 0.019f },
                new[] { 0f, 0f, 0.036f, 0.039f, 0.045f, 0.045f },
                new[] { 0f, 0f, 0.041f, 0.044f, 0.051f, 0.051f },
                new[] { 0f, 0f, 0.07f, 0.07f }
            };
            float[][] r =
            {
                new[] { 0f, 0.0049f, 0.0048f, 0.0042f },
                new[] { 0f, 0.0048f, 0.0047f, 0.0032f, 0.0029f, 0.0025f },
                new[] { 0f, 0.0060f, 0.0058f, 0.0040f, 0.0036f, 0.0031f },
                new[] { 0f, 0.0105f, 0.0102f, 0.0094f }
            };
            float[] bullet = { 0.008f, 0.019f, 0.022f, 0f };
            _meshes = new Mesh[4];
            _liveMeshes = new Mesh[4];
            for (int k = 0; k < 4; k++)
            {
                _meshes[k] = Lathe(z[k], r[k], k == 3 ? 0.016f : 0f, 0f);
                _liveMeshes[k] = k == 3 ? _meshes[k] : Lathe(z[k], r[k], 0f, bullet[k]);
            }
        }

        /// <summary>
        /// A round to hold (from the pouch, loaded by hand) along +Z with its base at 0: a 12 gauge shell (red hull, brass
        /// head) or a live rifle round (brass case, copper bullet). Shared mesh and materials; the caller owns the object.
        /// </summary>
        internal static GameObject MakeRound(bool shotgunShell, Transform parent, out float length, out float radius)
        {
            EnsureAssets();
            Mesh mesh = _liveMeshes[shotgunShell ? 3 : 1];
            var go = new GameObject(shotgunShell ? "shell" : "round");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterials = shotgunShell ? _shellMats : _liveMats;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            length = mesh.bounds.max.z;
            radius = Mathf.Max(mesh.bounds.extents.x, mesh.bounds.extents.y);
            return go;
        }

        /// <summary>
        /// A turned case along +Z. Shells: the brass head (submesh 1) up to <paramref name="head"/>, the hull above (submesh
        /// 0). Live rounds: a bullet of <paramref name="bullet"/> length on the mouth (submesh 1).
        /// </summary>
        private static Mesh Lathe(float[] z, float[] r, float head, float bullet)
        {
            const int sides = 10;
            var verts = new List<Vector3>();
            var normals = new List<Vector3>();
            var main = new List<int>();
            var second = new List<int>();
            var zs = new List<float>(z);
            var rs = new List<float>(r);
            if (bullet > 0f)
            {
                float mouth = zs[zs.Count - 1], mr = rs[rs.Count - 1];
                zs.Add(mouth + bullet * 0.55f); rs.Add(mr * 0.95f);
                zs.Add(mouth + bullet); rs.Add(0f);
            }
            for (int i = 0; i < zs.Count; i++)
            {
                for (int k = 0; k <= sides; k++)
                {
                    float a = k * Mathf.PI * 2f / sides;
                    var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    verts.Add(new Vector3(dir.x * rs[i], dir.y * rs[i], zs[i]));
                    normals.Add(rs[i] > 0f ? dir : new Vector3(0f, 0f, i == 0 ? -1f : 1f));
                }
            }
            int caseRings = z.Length;
            for (int i = 0; i < zs.Count - 1; i++)
            {
                bool other = bullet > 0f ? i >= caseRings - 1 : head > 0f && zs[i + 1] <= head + 1e-4f;
                List<int> tris = other ? second : main;
                for (int k = 0; k < sides; k++)
                {
                    int a = i * (sides + 1) + k, b = (i + 1) * (sides + 1) + k;
                    // Clockwise seen from outside (Unity's front face), so the outside is drawn.
                    tris.Add(a); tris.Add(a + 1); tris.Add(b);
                    tris.Add(b); tris.Add(a + 1); tris.Add(b + 1);
                }
            }
            if (head > 0f)
            {
                // The shell's brass head band, a little proud of the hull.
                int start = verts.Count;
                for (int k = 0; k <= sides; k++)
                {
                    float a = k * Mathf.PI * 2f / sides;
                    var dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                    verts.Add(dir * (r[1] + 0.0004f));
                    normals.Add(dir);
                    verts.Add(dir * (r[1] + 0.0004f) + new Vector3(0f, 0f, head));
                    normals.Add(dir);
                }
                for (int k = 0; k < sides; k++)
                {
                    int a = start + k * 2;
                    second.Add(a); second.Add(a + 2); second.Add(a + 1);
                    second.Add(a + 1); second.Add(a + 2); second.Add(a + 3);
                }
            }
            var mesh = new Mesh { name = "RavenfieldVR case" };
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.subMeshCount = second.Count > 0 ? 2 : 1;
            mesh.SetTriangles(main, 0);
            if (second.Count > 0)
            {
                mesh.SetTriangles(second, 1);
            }
            mesh.RecalculateBounds();
            return mesh;
        }
    }

    /// <summary>
    /// A mesh made for one copy (a skinned magazine baked, its triangles cut out of a skinned gun), destroyed with the copy
    /// however it goes: every reload, pouch display and rejected copy left one behind in memory for the session.
    /// </summary>
    internal sealed class OwnedMesh : MonoBehaviour
    {
        public Mesh Mesh;

        private void OnDestroy()
        {
            if (Mesh != null)
            {
                Object.Destroy(Mesh);
            }
        }
    }

    /// <summary>
    /// A case's tink when it lands (twice at most, softer for a slow touch); on earth, grass or sand a dull tap instead (a
    /// bright metal ring on the ground of every map sounded wrong).
    /// </summary>
    internal sealed class CaseSound : MonoBehaviour
    {
        private int _tinks;

        /// <summary>Thrown again (the case object is used again): it may tink again.</summary>
        public void Rearm() => _tinks = 0; // not "Reset": Unity calls that one itself (in the editor)

        private void OnCollisionEnter(Collision c)
        {
            float speed = c.relativeVelocity.magnitude;
            if (_tinks >= 2 || speed < 0.6f || WeaponSystem.Instance == null)
            {
                return;
            }
            _tinks++;
            bool soft = IsSoft(c.collider);
            float volume = Mathf.Clamp01(speed / 4f) * (_tinks == 1 ? 0.55f : 0.3f) * (soft ? 0.6f : 1f);
            WeaponSystem.Instance.Sounds.Play(soft ? MechSound.CasingSoft : MechSound.Casing, transform.position, volume);
        }

        /// <summary>The ground of a map (terrain), or anything whose physics material is named like earth.</summary>
        internal static bool IsSoft(Collider c)
        {
            if (c == null)
            {
                return false;
            }
            if (c is TerrainCollider)
            {
                return true;
            }
            PhysicMaterial m = c.sharedMaterial;
            if (m == null)
            {
                return false;
            }
            string name = m.name;
            return name.IndexOf("dirt", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("grass", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("sand", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("mud", System.StringComparison.OrdinalIgnoreCase) >= 0
                   || name.IndexOf("soil", System.StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("earth", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
