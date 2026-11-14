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
    public sealed partial class MagazineProp
    {
        private const int HeldLayer = 18;
        private const int WorldLayer = 0;
        private static readonly List<MagazineProp> Alive = new List<MagazineProp>();

        /// <summary>Every magazine, shell and rocket prop that exists (held, palmed, sliding or lying around).</summary>
        public static IReadOnlyList<MagazineProp> All => Alive;

        /// <summary>Whether <paramref name="body"/> is a loose magazine (dropped, lying or falling: not held, seated or carried).</summary>
        internal static bool IsLooseBody(Rigidbody body)
        {
            for (int i = 0; i < Alive.Count; i++)
            {
                MagazineProp m = Alive[i];
                if (m._body == body)
                {
                    return m.IsDropped && !body.isKinematic;
                }
            }
            return false;
        }
        private static Material _metal, _brass, _olive;

        public readonly GameObject Go;
        public readonly PropKind Kind;
        public readonly string WeaponKey;
        /// <summary>
        /// A copy of the gun's own part (its frame the part's: the reload's hold of it, MagHoldPose, fits it), not a made stand-in
        /// (built in the seat's frame, turned off the part's where the analysis lined the seat up).
        /// </summary>
        public readonly bool OwnCopy;
        /// <summary>Its weapon's analysis (its game sounds: MagazineClatter), when it was made from one.</summary>
        internal WeaponRigData Data;
        public int Rounds;
        /// <summary>
        /// The ammo the rounds came from. A dropped magazine keeps its rounds (pick it up and seat it to use them);
        /// if it despawns first they go back to this reserve, so leaving a half-full magazine behind costs nothing.
        /// </summary>
        public AmmoState Owner;
        /// <summary>Moving hulls it passes through, let go inside them (see <see cref="OutOfTheLevel"/>).</summary>
        private readonly List<Collider> _passedHulls = new List<Collider>();
        private float _hullCheckAt;

        /// <summary>
        /// A few times a second: a hull a magazine was let go inside meets it again once it is out of it. Only a new let-go gave it back, so one
        /// dropped in a seat that fell through the floor lay under the vehicle, which drove through it (physics scout, 2026-11-08).
        /// </summary>
        internal static void ReleasePassedHulls()
        {
            float now = Time.time;
            Actor seated = Util.GameContext.PlayerActor;
            Vehicle ride = seated != null && seated.IsSeated() && seated.seat != null ? seated.seat.vehicle : null;
            Footwell.Clear();
            foreach (MagazineProp p in Alive)
            {
                if (p._passedHulls.Count == 0 || now < p._hullCheckAt || p._colliders == null)
                {
                    continue;
                }
                p._hullCheckAt = now + 0.2f;
                // Let go inside the vehicle the player rides in: to the footwell, its rounds back in the reserve as a magazine put away.
                // A vehicle's hull is one convex shape with no floor of its own: the magazine went through it and lay on the road behind
                // (seat weapons scout, 2026-11-12).
                if (ride != null && p.IsDropped && p.HeldBy == null && now - p._dropTime > 0.3f)
                {
                    foreach (Collider hull in p._passedHulls)
                    {
                        if (hull != null && hull.transform.IsChildOf(ride.transform) && p.Go != null && hull.bounds.Contains(p.Go.transform.position))
                        {
                            Footwell.Add(p);
                            break;
                        }
                    }
                }
                for (int k = p._passedHulls.Count - 1; k >= 0; k--)
                {
                    Collider hull = p._passedHulls[k];
                    bool inside = false;
                    if (hull != null)
                    {
                        Transform ht = hull.transform;
                        foreach (Collider c in p._colliders)
                        {
                            if (c != null && c.enabled && Physics.ComputePenetration(c, c.transform.position, c.transform.rotation, hull, ht.position, ht.rotation, out _, out _))
                            {
                                inside = true;
                                break;
                            }
                        }
                    }
                    if (inside)
                    {
                        continue;
                    }
                    if (hull != null)
                    {
                        foreach (Collider c in p._colliders)
                        {
                            if (c != null)
                            {
                                Physics.IgnoreCollision(c, hull, false);
                            }
                        }
                    }
                    p._passedHulls.RemoveAt(k);
                }
            }
            foreach (MagazineProp p in Footwell)
            {
                if (p.Rounds > 0)
                {
                    WeaponSystem.Instance?.ReturnRounds(p, p.Rounds);
                }
                p.Destroy("footwell");
            }
            Footwell.Clear();
        }

        private static readonly List<MagazineProp> Footwell = new List<MagazineProp>();
        /// <summary>
        /// On the rail after being pulled out of its seat: where the hand was along the rail at the pull minus the
        /// magazine's depth then, so the magazine moves with the hand's pull from there (0 when it went onto the rail
        /// from outside).
        /// </summary>
        public float RailHandOffset;
        /// <summary>
        /// Pulled out of its seat and still on the rail: it is not seated again (nor drawn home into a pistol's grip) until
        /// it has come out a little. At the grab it sits at full depth, which counted as pushed home: it re-seated at once.
        /// </summary>
        public bool FromSeat;
        /// <summary>Pulled from its seat and tugged a few millimetres out: pushed home from there it seats (see <see cref="FromSeat"/>).</summary>
        public bool TuggedFromSeat;
        /// <summary>The rail depth last frame (whether it is going in or coming out).</summary>
        public float PrevDepth;
        /// <summary>
        /// It last moved into the well (set going in, cleared going out by more than a millimetre): the hand turns into a
        /// push only seating it, never pulling one out (it snapped into the push 1.5 cm out of a pistol's grip).
        /// </summary>
        public bool MovingIn;
        /// <summary>The depth it last turned at (the deepest while it goes in, the shallowest while it comes out).</summary>
        public float TurnDepth;
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
        private float _sideHalf;
        /// <summary>Half the magazine's size front to back (along the hand's pointing direction when held).</summary>
        private float _frontHalf;
        /// <summary>Half the magazine's largest size across its insertion axis (front to back or side to side): how big its base plate is.</summary>
        public float CrossHalf { get; private set; }
        /// <summary>A slim magazine (under 4.4 cm across, or a pistol's own under 8 cm): held by its base in the fist too, a little nearer the base plate and with the pistol pose (Tasks 240).</summary>
        public bool PistolSized => Kind == PropKind.Magazine && (CrossHalf < 0.022f || _pistolGun && CrossHalf < 0.04f);

        /// <summary>A pistol's magazine: held and seated as one even when its base plate makes it wider (AUTOMATICO 4.8 cm, FNX-45 6.5).</summary>
        private bool _pistolGun;
        /// <summary>
        /// A machine gun's box or drum: wider across than a hand closes round (over 11 cm), or thicker than any stick
        /// magazine (over 5 cm). Held in the box pose, further out along the fingers.
        /// </summary>
        public bool BoxSized => Kind == PropKind.Magazine && (CrossHalf > 0.055f || _sideHalf > 0.025f);

        /// <summary>
        /// How far it is held as a box (0 or 1), and as a pistol magazine, eased with its size while that eases in the hand: the hold's
        /// grip point and nudge took the flags, which switch in one frame where the easing size crosses their line, and the magazine
        /// jumped by 2 cm in the hand the ease is there to keep from (round-6 scout).
        /// </summary>
        internal float BoxBlend => _sectionEasing ? _boxNow : BoxSized ? 1f : 0f;
        internal float PistolBlend => _sectionEasing ? _pistolNow : PistolSized ? 1f : 0f;
        private float _boxFrom, _boxNow, _pistolFrom, _pistolNow;
        private bool _boxTo, _pistolTo;

        /// <summary>Self-test: its size across where the hand holds it, and how it is held.</summary>
        internal string SizeText => $"{_frontHalf * 200f:0.0} x {_sideHalf * 200f:0.0} cm across, held as {(BoxSized ? "a box" : PistolSized ? "a pistol magazine" : "a rifle magazine")}";
        private readonly Rigidbody _body;
        /// <summary>The magazine's own shape (convex pieces of its mesh: a curved or drum magazine is not a crate).</summary>
        private Collider[] _colliders;
        private readonly HashSet<Collider> _ignoredBodies = new HashSet<Collider>();
        private float _nextBodyCheck;
        private float _bodiesForgottenAt;
        private int _playerGeneration = -1;
        /// <summary>The colliders came from the meshes' boxes (unreadable meshes) and are replaced once the model is measured.</summary>
        private bool _boxShaped;
        /// <summary>The hull of the plain box a prop with no fitted shape is given: no cache holds it, so nothing else frees it.</summary>
        private Mesh _boxMesh;

        private void FreeBoxMesh()
        {
            if (_boxMesh != null)
            {
                UnityEngine.Object.Destroy(_boxMesh);
                _boxMesh = null;
            }
        }
        /// <summary>Per magazine type: the shape fitted to the rendered model (for every later magazine of the type).</summary>
        private static readonly Dictionary<string, ShapeFit.Piece[]> RenderedShapes = new Dictionary<string, ShapeFit.Piece[]>();
        /// <summary>The pieces' bounds in prop space, for distances while the colliders are off (Unity reports an empty box then).</summary>
        private Bounds[] _pieceBounds;
        private float _dropTime = -1f;
        /// <summary>How fast it was let go, and by which path (the flung-magazine log line; task 315).</summary>
        private float _dropSpeed, _dropAcross, _dropUp;
        private string _dropBy;
        private bool _flungLogged;
        private static int _flungLines;
        private static readonly Collider[] FlungNear = new Collider[8];

        /// <summary>Locked onto the weapon's insertion axis (sliding in or out of the well).</summary>
        public bool OnRail
        {
            get => _onRail;
            set
            {
                if (_onRail != value && Go != null)
                {
                    // Onto the rail or off it again: eased over a moment from where it is drawn. Lining up at the edge of the
                    // window it jumped up to 3.5 cm and 35 degrees in one frame (and back to the hand when pulled off).
                    _switchFrom = ToRigSpace(RigidPose.Of(Go.transform));
                    _switchAt = Time.unscaledTime;
                }
                _onRail = value;
            }
        }

        private bool _onRail;

        /// <summary>A hand holding more shells than one has just pushed one in: the next goes in only after the hand came back out
        /// towards the port's mouth (all of them went in on neighbouring frames, as one).</summary>
        public bool BackOutFirst;

        /// <summary>When the hand last took it off the well's line by going aside (Time.unscaledTime).</summary>
        public float LeftRailAt = -10f;
        /// <summary>Where it was drawn as it went onto or off the rail, against the rig (<see cref="ToRigSpace"/>).</summary>
        private RigidPose _switchFrom;

        /// <summary>
        /// A pose against the rig (the player's own space, carried by walking, a vehicle, a turn), for an ease that starts from a hand
        /// or the gun: kept in the world, its start stayed where the player had been and the magazine trailed behind while walking
        /// until the ease ended ("because of walking forward the mag goes a little bit backwards but corrects its path again"; user,
        /// 2026-10-28). Without a rig, the world.
        /// </summary>
        internal static RigidPose ToRigSpace(RigidPose world)
        {
            Transform root = VRRig.Instance != null ? VRRig.Instance.Root : null;
            return root != null ? RigidPose.Of(root).Inverse().Multiply(world) : world;
        }

        /// <summary>A pose kept by <see cref="ToRigSpace"/>, where it is in the world now.</summary>
        internal static RigidPose FromRigSpace(RigidPose local)
        {
            Transform root = VRRig.Instance != null ? VRRig.Instance.Root : null;
            return root != null ? RigidPose.Of(root).Multiply(local) : local;
        }
        private float _switchAt = -1f;
        private const float SwitchBlend = 0.08f;
        /// <summary>The switch (its <see cref="_switchAt"/> in milliseconds) <see cref="_switchOffset"/> was taken for, and how far
        /// it was drawn from where it goes on the switch's first frame, against where it goes.</summary>
        private int _offsetFor = -1;
        private RigidPose _switchOffset;

        // A magazine switched onto its line from aside at the well's mouth, or off it from inside the well, goes round the gun
        // (Routed): where the switch started and, taken off the line, the line it leaves (its mouth and direction), all against the
        // gun (it may move meanwhile: taken onto the line, against the rail's pose; off it, against the gun body), and how long.
        private int _routeFor = -1; // the switch (its _switchAt in milliseconds) the route was set up for; -1 none
        private bool _routed;
        private float _routeTime;
        private RigidPose _routeFrom, _routeLine; // the start, and (off the line) the line's mouth and turn, in the anchor's frame
        private Transform _routeGun; // off the line: the gun body the well is on (null: in the world)

        /// <summary>Draws the prop at <paramref name="target"/>, eased from where it was when it went onto or off the rail.</summary>
        public void ShowAt(RigidPose target)
        {
            if (Go == null)
            {
                return;
            }
            if (_switchAt >= 0f && Routed(ref target))
            {
                target.ApplyTo(Go.transform);
                return;
            }
            float t = _switchAt >= 0f ? (Time.unscaledTime - _switchAt) / SwitchBlend : 1f;
            if (t < 1f)
            {
                // What is eased away is how far it was from where it goes, kept against where it goes: eased from the fixed place
                // it was drawn at, it stood still on the switch's first frame while the hand went on (a hand at 0.75 m/s: 8 mm
                // a frame lost at once, and the same won back at the end; 26 mm in the smoothness check, 2026-11-11). It keeps
                // the hand's own motion through the ease this way, and walking's and the gun's too.
                int key = Mathf.RoundToInt(_switchAt * 1000f);
                if (_offsetFor != key)
                {
                    _offsetFor = key;
                    _switchOffset = target.Inverse().Multiply(FromRigSpace(_switchFrom));
                }
                float k = t * t * (3f - 2f * t);
                target = new RigidPose(target.position + target.rotation * (_switchOffset.position * (1f - k)),
                                       target.rotation * Quaternion.Slerp(_switchOffset.rotation, Quaternion.identity, k));
            }
            else
            {
                _switchAt = -1f;
            }
            target.ApplyTo(Go.transform);
        }

        /// <summary>
        /// A magazine switched onto its line from aside (up to 4.5 cm off it and 40 degrees turned, at the mouth of a pistol's
        /// grip), or off it while still in the well (the hand pulled 20 cm aside): eased straight between the two, it cut through
        /// the grip's or the magwell's wall in the switch, unchecked against the gun ("glitch through the handle a little but
        /// near", user, 2026-10-20; insertion hunt). It goes round under the well instead: along the line to below the mouth
        /// (beside the gun, or out of the well), turned there, across under the well, then along the line to where it goes (up
        /// the well, or to the hand). False when the switch is not such a one (eased as before).
        /// </summary>
        private bool Routed(ref RigidPose target)
        {
            if (Kind != PropKind.Magazine)
            {
                return false;
            }
            int key = Mathf.RoundToInt(_switchAt * 1000f);
            if (_routeFor != key)
            {
                _routeFor = key;
                RigidPose switchFrom = FromRigSpace(_switchFrom);
                Vector3 tip0 = switchFrom.position + switchFrom.rotation * TipLocal;
                Vector3 tipTarget = target.position + target.rotation * TipLocal;
                float far;
                if (_onRail)
                {
                    // Onto the line: the start kept against the rail's pose.
                    Vector3 lineIn = target.rotation * InsertDirLocal;
                    Vector3 mouthNow = tipTarget - lineIn * RailDepth;
                    _routeFrom = target.Inverse().Multiply(switchFrom);
                    far = Vector3.ProjectOnPlane(tip0 - mouthNow, lineIn).magnitude;
                    _routed = far > WellPlay * 0.75f;
                }
                else
                {
                    // Off the line from in the well: the line as it was (it starts on it, RailDepth in), against the gun body, so it
                    // stays on the well's axis while the gun moves (frozen in the world, it cut the mouth's wall; review, 2026-10-20).
                    _routeGun = _drawnFor != null && _drawnFor.Rig != null ? _drawnFor.Rig.GunBody : null;
                    RigidPose anchor = _routeGun != null ? RigidPose.Of(_routeGun) : RigidPose.Identity;
                    RigidPose inv = anchor.Inverse();
                    _routeFrom = inv.Multiply(switchFrom);
                    _routeLine = inv.Multiply(new RigidPose(tip0 - (switchFrom.rotation * InsertDirLocal) * RailDepth, switchFrom.rotation));
                    far = Vector3.Distance(tip0, tipTarget);
                    // A pistol's always, at the mouth too: it leaves the rail 3 cm past the mouth with its drawn depth held at 0, its top
                    // 5 mm under the grip, and the straight ease turned it the hand's 30 degrees on the way, its corners up through the
                    // grip's bottom ("yanking out the mag with force ... glitch through the weapon handle a little", user, 2026-11-03).
                    // The route drops it clear of the mouth before it turns. Any other only when it is still up in the well, or when the
                    // straight ease would lift a corner over the mouth while under it: routed every time, a magazine taken off its
                    // line went the long way round under the well and reached the hand a quarter second late, flying after it (user,
                    // 2026-11-03: "mags are sometimes trying to keep up ... flying in the air trying to correct their positions").
                    _routed = _pistolGun || RailDepth > 0.005f || StraightCutsMouth(switchFrom, target, tip0 - (switchFrom.rotation * InsertDirLocal) * RailDepth,
                                                                                         switchFrom.rotation * InsertDirLocal, TipLocal, InsertDirLocal, _frontHalf, _sideHalf);
                }
                // Longer for a longer way round: at a fixed 0.14 s, 20 cm aside went past in two frames (review, 2026-10-20). At most a
                // fifth of a second: the hand it ends on does not wait (up to 0.3 s, it trailed the hand by a quarter metre at 1 m/s).
                _routeTime = Mathf.Clamp(0.08f + far * 0.45f, 0.1f, 0.2f);
                if (!(_routeTime > 0f))
                {
                    _routeTime = 0.1f; // (a NaN distance passes the clamp, and the route would never end)
                }
            }
            if (!_routed)
            {
                return false;
            }
            float t = (Time.unscaledTime - _switchAt) / _routeTime;
            if (t >= 1f)
            {
                _switchAt = -1f;
                return false; // there: drawn where it goes as any other
            }
            // Down clear of the mouth, turned, across, then to where it goes: each leg over a share of the route, the next one
            // starting before the last ends (one after the other, a quarter each, the hand was reached only in the last quarter).
            // The turn begins with the drop over half done (it starts slowly: its corners stay under the mouth), the last leg once most
            // of the way across is.
            float Phase(int i)
            {
                float x = Mathf.Clamp01((t - PhaseStart[i]) / PhaseLength[i]);
                return x * x * (3f - 2f * x);
            }
            RigidPose from, lineAt;
            if (_onRail)
            {
                from = target.Multiply(_routeFrom);
                lineAt = new RigidPose(target.position + target.rotation * TipLocal - (target.rotation * InsertDirLocal) * RailDepth, target.rotation);
            }
            else
            {
                RigidPose anchor = _routeGun != null ? RigidPose.Of(_routeGun) : RigidPose.Identity;
                from = anchor.Multiply(_routeFrom);
                lineAt = anchor.Multiply(_routeLine);
            }
            Vector3 insert = lineAt.rotation * InsertDirLocal;
            Vector3 mouth = lineAt.position;
            Vector3 tipA = from.position + from.rotation * TipLocal;
            Quaternion rotA = from.rotation;
            Vector3 tipB = target.position + target.rotation * TipLocal;
            float dA = Vector3.Dot(tipA - mouth, insert), dB = Vector3.Dot(tipB - mouth, insert);
            Vector3 asideA = Vector3.ProjectOnPlane(tipA - mouth, insert), asideB = Vector3.ProjectOnPlane(tipB - mouth, insert);
            // Low enough under the mouth that its top face's highest corner clears it, turned as it starts, ends or halfway (a
            // slerp can lean further between), and by the face's corner, not its longer half (a square magazine turned about its
            // diagonal stood 5 mm over the mouth against a 3 mm margin; review, 2026-10-20).
            Quaternion mid = Quaternion.Slerp(rotA, target.rotation, 0.5f);
            float turned = Mathf.Max(Vector3.Angle(rotA * InsertDirLocal, insert),
                Mathf.Max(Vector3.Angle(target.rotation * InsertDirLocal, insert), Vector3.Angle(mid * InsertDirLocal, insert)));
            float corner = Mathf.Sqrt(_frontHalf * _frontHalf + _sideHalf * _sideHalf);
            float low = Mathf.Min(Mathf.Min(dA, dB), 0f) - Mathf.Sin(Mathf.Min(turned, 90f) * Mathf.Deg2Rad) * corner - 0.003f;
            float depth = Phase(3) > 0f ? Mathf.Lerp(low, dB, Phase(3)) : Mathf.Lerp(dA, low, Phase(0));
            Vector3 aside = Vector3.Lerp(asideA, asideB, Phase(2));
            Quaternion rot = Quaternion.Slerp(rotA, target.rotation, Phase(1));
            Vector3 tip = mouth + insert * depth + aside;
            target = new RigidPose(tip - rot * TipLocal, rot);
            return true;
        }
        /// <summary>The route's legs (down, turn, across, to the target): where each starts and how long it takes, in shares of the route.</summary>
        private static readonly float[] PhaseStart = { 0f, 0.2f, 0.4f, 0.72f }, PhaseLength = { 0.35f, 0.35f, 0.35f, 0.28f };

        /// <summary>
        /// Whether easing straight from <paramref name="from"/> (on the line, at the mouth) to <paramref name="to"/> lifts a corner of
        /// the magazine's top face over the mouth (<paramref name="mouth"/>, the line going in along <paramref name="insert"/>) while
        /// it is still under the well: the hand turned it more than it drew it down. Checked at a few points of the ease. Pure: the
        /// offline tests check it.
        /// </summary>
        internal static bool StraightCutsMouth(RigidPose from, RigidPose to, Vector3 mouth, Vector3 insert, Vector3 tipLocal, Vector3 insertLocal, float frontHalf, float sideHalf)
        {
            float corner = Mathf.Sqrt(frontHalf * frontHalf + sideHalf * sideHalf);
            float under = Mathf.Max(frontHalf, sideHalf) + 0.01f; // aside within this, it is under the well's opening
            for (int i = 1; i <= 4; i++)
            {
                float k = i / 5f;
                float d = Quaternion.Dot(from.rotation, to.rotation) < 0f ? -1f : 1f; // (a blend, not a slerp: a native call, and this is an estimate)
                Quaternion a = from.rotation, b = to.rotation;
                var blend = new Quaternion(a.x + (d * b.x - a.x) * k, a.y + (d * b.y - a.y) * k, a.z + (d * b.z - a.z) * k, a.w + (d * b.w - a.w) * k);
                float size = Mathf.Sqrt(Quaternion.Dot(blend, blend));
                var at = new RigidPose(Vector3.Lerp(from.position, to.position, k), new Quaternion(blend.x / size, blend.y / size, blend.z / size, blend.w / size));
                Vector3 tip = at.position + at.rotation * tipLocal;
                float depth = Vector3.Dot(tip - mouth, insert);
                float aside = Vector3.ProjectOnPlane(tip - mouth, insert).magnitude;
                float lift = Mathf.Sin(Mathf.Min(Vector3.Angle(at.rotation * insertLocal, insert), 90f) * Mathf.Deg2Rad) * corner;
                if (aside < under && depth + lift > 0.001f)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>How far along the rail (0 = at the well entry, RailLength = seated).</summary>
        public float RailDepth;
        /// <summary>How far off the line the held end was when it went onto the rail (a rocket taken loosely): the hand may keep it.</summary>
        public float RailLateral0;
        /// <summary>Speed along the rail while it is drawn home by itself (a damped spring, as Boneworks drives its insert).</summary>
        public float DepthVel;
        /// <summary>
        /// Pulled from its seat in a gun that will not take it back now (an en-bloc clip under a bolt shut on a round): it
        /// slides out along its rail all the same, and is not seated again from it.
        /// </summary>
        public bool RailOutOnly;
        /// <summary>Insertion travel: how far the prop slides before it seats.</summary>
        public float RailLength { get; private set; }

        /// <summary>A well in the grip deeper than the rail: the rail starts at the grip's bottom (ReloadInteractions, task 270).</summary>
        internal void RailFromGripBottom(float length) => RailLength = Mathf.Max(RailLength, length);
        public float LastTick;

        private MagazineProp(GameObject go, PropKind kind, string key, int rounds, Bounds bounds, Vector3 insertDir, Vector3 front, bool machineGun, string fitKey)
        {
            Go = go;
            Kind = kind;
            WeaponKey = key;
            OwnCopy = fitKey != null;
            Rounds = rounds;
            InsertDirLocal = insertDir.normalized;
            FrontLocal = Vector3.ProjectOnPlane(front, InsertDirLocal).sqrMagnitude > 1e-4f
                ? Vector3.ProjectOnPlane(front, InsertDirLocal).normalized
                : Vector3.Cross(InsertDirLocal, Vector3.right).normalized;
            float extent = Mathf.Abs(Vector3.Dot(bounds.extents, Abs(InsertDirLocal)));
            TipLocal = bounds.center + InsertDirLocal * extent;
            BaseLocal = bounds.center - InsertDirLocal * extent;
            Vector3 sideLocal = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
            float sideBox = Mathf.Abs(Vector3.Dot(bounds.extents, Abs(sideLocal))), frontBox = Mathf.Abs(Vector3.Dot(bounds.extents, Abs(FrontLocal)));
            _sectionKey = key + "|" + InsertDirLocal.ToString("F3");
            _boxHalf = new Vector2(frontBox, sideBox);
            // (Kept per weapon only for the gun's own copy: a made stand-in's result was kept for the weapon too, and when its copy
            // came out right later, its grip was never measured.)
            if (kind == PropKind.Magazine && GripSection(go, fitKey != null ? key : null, InsertDirLocal, FrontLocal, sideLocal, out float frontGrip, out float sideGrip))
            {
                // Where the hand holds it, not its box: the box of a raked or curved magazine counted part of its length
                // as width (a 25 cm magazine tipped 20 degrees, an AK's bend), and most rifle and pistol magazines were
                // taken for a machine gun's box and held out on the fingers, on the back of the hand (91 guns' pictures).
                frontBox = frontGrip;
                sideBox = sideGrip;
            }
            _sideHalf = Mathf.Clamp(sideBox, 0.006f, 0.04f);
            _frontHalf = Mathf.Clamp(frontBox, 0.006f, 0.1f);
            CrossHalf = Mathf.Clamp(Mathf.Max(frontBox, sideBox), 0.006f, 0.1f);
            switch (kind)
            {
                case PropKind.Shell:
                    RailLength = Mathf.Clamp(extent * 1.4f, 0.025f, 0.06f);
                    break;
                case PropKind.Rocket:
                    RailLength = Mathf.Clamp(extent * 0.6f, 0.1f, 0.3f); // started into the tube, it slides home itself
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
            if (_boxShaped && shape.Length == 1)
            {
                _boxMesh = shape[0].MadeMesh; // (its own, in no cache: freed with it)
            }
            _pieceBounds = new Bounds[shape.Length];
            for (int i = 0; i < shape.Length; i++)
            {
                _pieceBounds[i] = shape[i].Bounds;
            }
            if (shape.Length > 0)
            {
                // What the physics shape spans against what the copy draws, both in its own space (task 260 b: the hitbox
                // of many magazines sat low and covered only about half of them).
                Bounds span = _pieceBounds[0];
                foreach (Bounds pb in _pieceBounds)
                {
                    span.Encapsulate(pb);
                }
                Log.InfoOnce("magshape:" + key + ":" + kind, $"Magazine shape of {key} ({kind}): {shape.Length} piece(s) spanning {span.min * 100f:F1}..{span.max * 100f:F1} cm, "
                                                        + $"the copy drawn over {bounds.min * 100f:F1}..{bounds.max * 100f:F1} cm, {(_boxShaped ? "boxes" : "fitted to its vertices")}, "
                                                        + $"{shown.Length} renderer(s) ({System.Linq.Enumerable.Count(shown, r => r != null && r.enabled)} on).");
            }
            _body = go.AddComponent<Rigidbody>();
            if (kind == PropKind.Magazine)
            {
                go.AddComponent<MagazineClatter>().Prop = this;
            }
            _body.mass = kind == PropKind.Rocket ? 2f : machineGun && kind == PropKind.Magazine ? 1.5f : 0.4f;
            _body.interpolation = RigidbodyInterpolation.Interpolate;
            RestingBodies.Add(_body); // (no interpolation while it lies still)
            // (Made kinematic below: the swept kind, ContinuousDynamic, is set when it is let go, PropPhysics.MakeDynamic.)
            // A knocked-out magazine is given 6 to 9 rad/s end over end; Unity's default cap of 7 cut the faster half of that
            // (prop physics hunt, 2026-10-21).
            _body.maxAngularVelocity = 12f;
            // Pushed out of what it ends up in (a gun swung into it, a hand) gently, not at PhysX's 10 m/s.
            _body.maxDepenetrationVelocity = 2f;
            PropPhysics.MakeKinematic(_body);
            SetCollidersEnabled(false);
            Alive.Add(this);
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static readonly Dictionary<string, Vector2> GripSections = new Dictionary<string, Vector2>();
        private string _sectionKey;
        private Vector2 _boxHalf;
        private bool _sectionMeasured;

        /// <summary>
        /// A magazine whose meshes cannot be read had its grip thickness from its box (a raked or curved one too wide): once
        /// its shape is measured (rendered), the band where the hand holds it is measured on that, and kept for its type.
        /// </summary>
        private void MeasureSectionFromVolume()
        {
            if (_sectionMeasured || Kind != PropKind.Magazine || _volume == null || !_volume.Ready || Go == null)
            {
                return;
            }
            _sectionMeasured = true;
            if (GripSections.TryGetValue(_sectionKey, out Vector2 known) && known.x > 0f)
            {
                // From its vertices, or measured on an earlier one: this one takes it too (one made before that kept its box's
                // widths for good, and a palmed one never measured: a pistol magazine could be held as a box; bug hunt).
                ApplySection(known.x, known.y);
                return;
            }
            Vector3 insert = InsertDirLocal, front = FrontLocal, side = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
            float length = Vector3.Dot(TipLocal - BaseLocal, insert);
            if (length < 0.03f)
            {
                return;
            }
            Transform t = Go.transform;
            Quaternion inv = Runtime.RigidPose.Conjugate(t.rotation);
            float reachF = Mathf.Clamp(_boxHalf.x + 0.01f, 0.02f, 0.12f), reachS = Mathf.Clamp(_boxHalf.y + 0.01f, 0.02f, 0.06f);
            const float step = 0.004f;
            float fMin = float.MaxValue, fMax = float.MinValue, sMin = float.MaxValue, sMax = float.MinValue;
            int hits = 0;
            for (float u = 0.12f; u <= 0.3f; u += 0.03f)
            {
                Vector3 c = BaseLocal + insert * (length * u);
                for (float f = -reachF; f <= reachF; f += step)
                {
                    for (float s = -reachS; s <= reachS; s += step)
                    {
                        Vector3 local = c + front * f + side * s;
                        Vector3 frame = inv * (t.TransformPoint(local) - t.position);
                        if (!_volume.InsideModelLocal(frame))
                        {
                            continue;
                        }
                        hits++;
                        fMin = Mathf.Min(fMin, f);
                        fMax = Mathf.Max(fMax, f);
                        sMin = Mathf.Min(sMin, s);
                        sMax = Mathf.Max(sMax, s);
                    }
                }
            }
            if (hits < 8)
            {
                return;
            }
            float frontHalf = (fMax - fMin) * 0.5f + step * 0.5f, sideHalf = (sMax - sMin) * 0.5f + step * 0.5f;
            if (OwnCopy)
            {
                // (A made stand-in's cube kept for the type: every later magazine, its own copy too, was held by the box's widths as a box,
                // out on the fingers, until the content came again; props scout, 2026-11-06.)
                GripSections[_sectionKey] = new Vector2(frontHalf, sideHalf);
            }
            ApplySection(frontHalf, sideHalf);
        }

        private Vector3 _sectionGoal, _sectionFrom; // front half, side half, cross half
        private float _sectionAt;
        private int _sectionFrame = -1;
        private bool _sectionEasing;

        /// <summary>
        /// Takes the measured thickness where the hand holds it. In the hand it eases there over a tenth of a second: the
        /// measurement lands a few frames after the grab, and the magazine jumped in the palm (bug hunt 2026-10-16).
        /// </summary>
        private void ApplySection(float frontHalf, float sideHalf)
        {
            var goal = new Vector3(Mathf.Clamp(frontHalf, 0.006f, 0.1f), Mathf.Clamp(sideHalf, 0.006f, 0.04f), Mathf.Clamp(Mathf.Max(frontHalf, sideHalf), 0.006f, 0.1f));
            bool wasBox = BoxSized, wasPistol = PistolSized;
            if (HeldBy != null)
            {
                _boxFrom = _boxNow = BoxBlend; // (from where a running ease has it)
                _pistolFrom = _pistolNow = PistolBlend;
                _sectionFrom = new Vector3(_frontHalf, _sideHalf, CrossHalf);
                _sectionGoal = goal;
                _sectionAt = Time.unscaledTime;
                _sectionEasing = true;
            }
            else
            {
                _frontHalf = goal.x;
                _sideHalf = goal.y;
                CrossHalf = goal.z;
            }
            bool box = goal.z > 0.055f || goal.y > 0.025f, pistol = goal.z < 0.022f || _pistolGun && goal.z < 0.04f;
            _boxTo = box;
            _pistolTo = pistol;
            if (Kind == PropKind.Magazine && (wasBox != box || wasPistol != pistol))
            {
                Log.InfoOnce("magsection:" + WeaponKey, $"Magazine of {WeaponKey}: {goal.x * 200f:0.0} x {goal.y * 200f:0.0} cm where the hand holds it, held as "
                                                        + $"{(box ? "a box" : pistol ? "a pistol magazine" : "a rifle magazine")} (its box said {_boxHalf.x * 200f:0.0} x {_boxHalf.y * 200f:0.0} cm).");
            }
        }

        /// <summary>
        /// Once a frame (the hand's shape is updated once per magazine it holds), linear over 0.1 s; at once when it is not
        /// held (let go half way it kept the in-between widths until it was picked up again).
        /// </summary>
        private void EaseSection()
        {
            if (!_sectionEasing || _sectionFrame == Time.frameCount)
            {
                return;
            }
            _sectionFrame = Time.frameCount;
            float k = HeldBy != null ? Mathf.Clamp01((Time.unscaledTime - _sectionAt) / 0.1f) : 1f;
            Vector3 now = Vector3.Lerp(_sectionFrom, _sectionGoal, k);
            _frontHalf = now.x;
            _sideHalf = now.y;
            CrossHalf = now.z;
            _boxNow = Mathf.Lerp(_boxFrom, _boxTo ? 1f : 0f, k);
            _pistolNow = Mathf.Lerp(_pistolFrom, _pistolTo ? 1f : 0f, k);
            _sectionEasing = k < 1f;
        }

        /// <summary>
        /// The magazine's half thickness front to back and side to side where a hand holds it (a band 12-30% of its length
        /// up from the base plate), from its vertices, across its insertion axis. False when its meshes cannot be read (the
        /// box is used then). Kept per weapon.
        /// </summary>
        private static bool GripSection(GameObject go, string key, Vector3 insert, Vector3 front, Vector3 side, out float frontHalf, out float sideHalf)
        {
            frontHalf = sideHalf = 0f;
            string cacheKey = key + "|" + insert.ToString("F3");
            if (key != null && GripSections.TryGetValue(cacheKey, out Vector2 known))
            {
                frontHalf = known.x;
                sideHalf = known.y;
                return known.x > 0f;
            }
            var pts = new List<Vector3>();
            Matrix4x4 toRoot = go.transform.worldToLocalMatrix;
            foreach (MeshFilter mf in go.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable || m.vertexCount == 0)
                {
                    continue;
                }
                Matrix4x4 toProp = toRoot * mf.transform.localToWorldMatrix;
                Vector3[] v = m.vertices;
                int step = Mathf.Max(1, v.Length / 3000);
                for (int i = 0; i < v.Length; i += step)
                {
                    pts.Add(toProp.MultiplyPoint3x4(v[i]));
                }
            }
            bool ok = false;
            if (pts.Count >= 16)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                foreach (Vector3 p in pts)
                {
                    float t = Vector3.Dot(p, insert);
                    lo = Mathf.Min(lo, t);
                    hi = Mathf.Max(hi, t);
                }
                float length = hi - lo;
                if (length > 0.02f)
                {
                    float a = lo + length * 0.12f, b = lo + length * 0.3f;
                    float fMin = float.MaxValue, fMax = float.MinValue, sMin = float.MaxValue, sMax = float.MinValue;
                    int n = 0;
                    foreach (Vector3 p in pts)
                    {
                        float t = Vector3.Dot(p, insert);
                        if (t < a || t > b)
                        {
                            continue;
                        }
                        float f = Vector3.Dot(p, front), s = Vector3.Dot(p, side);
                        fMin = Mathf.Min(fMin, f);
                        fMax = Mathf.Max(fMax, f);
                        sMin = Mathf.Min(sMin, s);
                        sMax = Mathf.Max(sMax, s);
                        n++;
                    }
                    if (n >= 8)
                    {
                        frontHalf = (fMax - fMin) * 0.5f;
                        sideHalf = (sMax - sMin) * 0.5f;
                        ok = frontHalf > 0.003f && sideHalf > 0.003f;
                    }
                }
            }
            if (key != null)
            {
                GripSections[cacheKey] = ok ? new Vector2(frontHalf, sideHalf) : Vector2.zero;
            }
            return ok;
        }

        /// <summary>
        /// The way a prop goes in and its front (gun-body space), for the prop in the hand and the one shown on the pouch
        /// alike. A rocket goes into the launcher's muzzle, tail first, back along the bore (an RPG is always loaded from
        /// the front; user, 2026-10-16: the reload animation's first motion is not that on every launcher), and its front
        /// is the launcher's up (the bore, its old front, lay along its insert and left its roll in the hand to chance).
        /// </summary>
        private static void Axes(WeaponRigData d, PropKind kind, out Vector3 insertGun, out Vector3 frontGun)
        {
            insertGun = -d.MagEjectAxis;
            frontGun = d.AimAxis;
            if (kind == PropKind.Rocket)
            {
                // Along the bore (a slightly oblique animated axis kept a rail whose depth is measured along the bore), or
                // better along the rocket's own length as it sits loaded, which is the tube's axis: the muzzle's aim can be a
                // few degrees off the tube, and over a 50-70 cm rail the booster rode along the top of an RPG-7's tube instead
                // of going into it (the user's picture, 2026-10-16: "the same bug as with pistol mags").
                insertGun = -d.AimAxis.normalized;
                frontGun = d.Up;
                Bounds mb = d.MagBounds;
                Vector3 size = mb.size;
                if (size.sqrMagnitude > 1e-6f)
                {
                    Vector3 longLocal = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
                    float longest = Vector3.Dot(size, longLocal), next = Mathf.Max(Vector3.Dot(size, Vector3.one - longLocal) - Mathf.Min(size.x, Mathf.Min(size.y, size.z)), 1e-4f);
                    Vector3 longGun = (d.MagSeat.rotation * longLocal).normalized;
                    if (Vector3.Dot(longGun, insertGun) < 0f)
                    {
                        longGun = -longGun;
                    }
                    // A rocket clearly longer than wide, its length within 15 degrees of the bore: that is the line it goes in on.
                    if (longest > next * 3f && Vector3.Angle(longGun, insertGun) < 15f)
                    {
                        insertGun = longGun;
                        frontGun = Vector3.ProjectOnPlane(d.Up, longGun).normalized;
                    }
                }
            }
        }

        /// <summary>World pose at the current rail depth, given the seated pose of the magazine/shell/rocket.</summary>
        public RigidPose RailPose(RigidPose seat)
        {
            Vector3 insert = seat.TransformDirection(InsertDirLocal);
            var pose = new RigidPose(seat.position - insert * (RailLength - RailDepth), seat.rotation);
            if (RockAngle > 0f && RailLength > 1e-3f)
            {
                // Rocked in (user, 2026-11-03, idea 11): at the mouth it hangs from its front lip with its base swung forward, and turns
                // back about that lip as it goes in, straight by the last stretch before its seat. Turned about the front lip, its back
                // edge stays below the well's.
                float k = 1f - Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(RailDepth / (RailLength * 0.85f)));
                if (k > 1e-3f)
                {
                    Vector3 lip = pose.TransformPoint(TipLocal + FrontLocal * _frontHalf);
                    Quaternion rock = Quaternion.AngleAxis(-RockAngle * k, pose.rotation * Vector3.Cross(InsertDirLocal, FrontLocal));
                    pose = new RigidPose(lip + rock * (pose.position - lip), rock * pose.rotation);
                }
            }
            return pose;
        }

        /// <summary>How far a curved magazine hangs forward at the well's mouth as it rocks in (degrees; 0: straight in, as most).</summary>
        internal float RockAngle;

        public Vector3 TipWorld => Go.transform.TransformPoint(TipLocal);
        public Vector3 InsertDirWorld => Go.transform.TransformDirection(InsertDirLocal);

        public void Drop(Vector3 velocity, Vector3 angularVelocity, [System.Runtime.CompilerServices.CallerMemberName] string by = "")
        {
            if (Core.AutoTest.Requested && HeldBy != null && Rounds > 0)
            {
                // Autotest: which branch takes a loaded magazine out of a hand (RECON LRR's fresh magazine left the hand
                // with its 14 rounds during a slow push, and no branch could be named from the code; 252 ae).
                Log.Info($"Magazine ({Rounds} rounds) dropped from the {HeldBy.Side} hand by {by}{(LostByHand ? " (lost by the hand)" : "")}.");
            }
            // Let go by the hand at another PC's player's hand, with a gun it fits in his: his now (invention 3, 2026-11-14).
            if (HeldBy != null && !LostByHand && !OnRail && !RailOutOnly && Net.Multiplayer.Active && Net.NetHandover.TryGive(this))
            {
                Destroy("handed over");
                return;
            }
            LostByHand = false;
            _fresh = false; // caught or picked up again later, it eases into the hand (it jumped up to 13 cm in one frame; review, 2026-10-20)
            if (_sectionEasing)
            {
                _frontHalf = _sectionGoal.x;
                _sideHalf = _sectionGoal.y;
                CrossHalf = _sectionGoal.z;
                _sectionEasing = false;
            }
            if (HeldBy != null)
            {
                if (ReferenceEquals(HeldBy.Held, this))
                {
                    HeldBy.Held = null;
                    HeldBy.Role = HandRole.Free;
                }
                ReleaseHand(HeldBy, this);
                HeldBy = null;
            }
            OnRail = false;
            RailOutOnly = false;
            int layer = PropPhysics.Layer;
            SetLayer(Go, layer >= 0 ? layer : WorldLayer);
            // The pose written this frame (a knocked-out magazine at the well's mouth, the hand's last move) reaches PhysX first:
            // the stand-in's overlap test below read the old pose, missed the overlap, and the next step pushed it out of the gun
            // (review, 2026-10-20, task 315).
            Physics.SyncTransforms();
            SetCollidersEnabled(true);
            OutOfTheLevel();
            RealTimeFlight.Stop(_body); // (caught in flight and let go before a physics step: its old slow-motion scale went with it)
            PropPhysics.MakeDynamic(_body);
            _body.velocity = velocity;
            _body.angularVelocity = angularVelocity;
            // In slow motion it flies as in real time until it touches something (it hung by the hand: RealTimeFlight).
            velocity *= RealTimeFlight.Begin(_body);
            _dropTime = Time.time;
            _dropSpeed = velocity.magnitude;
            _dropAcross = new Vector2(velocity.x, velocity.z).magnitude;
            _dropUp = Mathf.Max(0f, velocity.y);
            _dropBy = by;
            _flungLogged = false;
            // The newest drop goes last: the cap on lying props counts from the end, so a magazine picked up and dropped
            // again counts as new (counted by when it was made, it vanished at the feet the moment it was let go).
            if (Alive.Remove(this))
            {
                Alive.Add(this);
            }
            PropPhysics.IgnorePlayer(_colliders);
            // Bodies around it from the first physics step on (not from the next clean-up pass): a magazine falling next to
            // a soldier or into a ragdoll is not shoved away. All of them again: held, its colliders were off, and Unity forgets
            // an ignored pair then, so the bodies remembered from the last drop were skipped and could shove it for up to 2 s
            // (prop physics hunt, 2026-10-21).
            _ignoredBodies.Clear();
            PropPhysics.IgnoreBodiesNear(_colliders, Go.transform.position, 1f, _ignoredBodies);
            // Met by the held gun like any loose thing (passing through only while it starts inside it, out of the well): ignored
            // however it moved, a magazine went through the gun ("mags still glitch through guns": user, 2026-10-28). The gun pushes
            // it no faster than it moves itself (GunProxyBody.Touch), which is what had magazines flying off it before.
            foreach (Collider c in _colliders)
            {
                GunCollider.Current?.OnPropDropped(c);
            }
        }

        private static readonly Collider[] LevelNear = new Collider[32]; // 16 was dropped in a cluttered spot (a vehicle cabin, a prop pile): walls past the first 16 went unseen and the magazine stayed in them

        /// <summary>
        /// Let go with part of it inside the floor, a wall or a vehicle (the hand pressed it a little into the ground): it is moved
        /// just out of them first, with no speed. Left inside, PhysX pushed it out at up to 10 m/s, a hop or a flight (review,
        /// 2026-10-20, task 318 a). Props, the player and bodies are not level: they are ignored or passed through elsewhere.
        /// </summary>
        private void OutOfTheLevel()
        {
            if (_colliders == null || _colliders.Length == 0 || Go == null)
            {
                return;
            }
            // Hulls it was let go inside before meet it again: left ignored, a magazine once dropped in a seat sank through that vehicle's floor for good.
            foreach (Collider hull in _passedHulls)
            {
                if (hull != null)
                {
                    foreach (Collider c in _colliders)
                    {
                        Physics.IgnoreCollision(c, hull, false);
                    }
                }
            }
            _passedHulls.Clear();
            Bounds b = _colliders[0].bounds;
            for (int i = 1; i < _colliders.Length; i++)
            {
                b.Encapsulate(_colliders[i].bounds);
            }
            int prop = PropPhysics.Layer;
            int mask = ~PropPhysics.BodyMask;
            int n = Physics.OverlapBoxNonAlloc(b.center, b.extents, LevelNear, Quaternion.identity, mask, QueryTriggerInteraction.Ignore);
            Vector3 push = Vector3.zero;
            for (int i = 0; i < n; i++)
            {
                Collider level = LevelNear[i];
                // Loose props lying here (another magazine, a dropped weapon, a casing) count too: dropped onto one it was pushed
                // out of it the same way (review, 2026-10-20, task 318 c). The held gun's stand-in does not: it lets props pass.
                bool looseProp = level != null && prop >= 0 && level.gameObject.layer == prop && !GunCollider.IsStandIn(level);
                if (NotAWall(level))
                {
                    continue; // its own pieces and the player's own body are not walls
                }
                if (level.attachedRigidbody != null && !level.attachedRigidbody.isKinematic && !looseProp)
                {
                    // A moving thing it starts inside (the vehicle the player sits in: a magazine dropped in a seat starts inside its
                    // hull) is not pushed out of (it moves, and from inside a hull there is no short way out): left to the physics
                    // step, it threw the magazine out at depenetration speed (flung-magazine hunt, 2026-10-20). It passes through
                    // that collider instead and falls.
                    foreach (Collider c in _colliders)
                    {
                        Transform ct = c.transform, lt = level.transform;
                        if (c.enabled && Physics.ComputePenetration(c, ct.position, ct.rotation, level, lt.position, lt.rotation, out _, out _))
                        {
                            Physics.IgnoreCollision(c, level, true);
                            _passedHulls.Add(level);
                        }
                    }
                    continue;
                }
                if (PlayersOwn(level))
                {
                    continue; // nor the player's own weapons (a knocked-out magazine starts at the well's mouth)
                }
                foreach (Collider c in _colliders)
                {
                    Transform ct = c.transform, lt = level.transform;
                    if (c.enabled && Physics.ComputePenetration(c, ct.position + push, ct.rotation, level, lt.position, lt.rotation, out Vector3 dir, out float dist))
                    {
                        push += dir * dist;
                    }
                }
            }
            bool deep = push.sqrMagnitude >= 0.25f * 0.25f;
            if (push.sqrMagnitude > 1e-8f && !deep)
            {
                Vector3 moved = push + push.normalized * 0.002f;
                Go.transform.position += moved;
                b.center += moved;
                Physics.SyncTransforms();
            }
            // Under the ground's surface (terrain and mesh ground are a surface, not a solid: the push above finds nothing in them,
            // and a magazine let go 15 or 40 cm under the ground fell through the world; buried-magazine check, 2026-10-21), or
            // deeper in a wall than the push goes: back toward the eyes, onto the surface in between.
            Transform head = WeaponSystem.Instance != null && WeaponSystem.Instance.Rig != null ? WeaponSystem.Instance.Rig.Head : null;
            if (head != null && (deep || PropPhysics.NothingUnder(b.center, _colliders) || PropPhysics.GroundBetween(head.position, b.center, _colliders)))
            {
                if (!PropPhysics.BackToward(Go.transform, _colliders, head.position, b, deep ? 1f : 0.8f) && !deep) // (at most a metre: an arm's length)
                {
                    PropPhysics.UpOnto(Go.transform, _colliders, b, 1f); // (the way from the eyes blocked near them: straight up onto the ground)
                }
            }
        }

        private bool NotAWall(Collider level) =>
            level == null || System.Array.IndexOf(_colliders, level) >= 0 || level.attachedRigidbody == _body || PropPhysics.IsPlayerCollider(level);

        private static bool PlayersOwn(Collider level)
        {
            Actor player = Util.GameContext.PlayerActor;
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            return player != null && level.transform.IsChildOf(player.transform)
                   || held != null && held.Weapon != null && level.transform.IsChildOf(held.Weapon.transform);
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
            if (Rounds > 0 && VRConfig.ReturnRoundsOnMagDrop.Value && WeaponSystem.Instance != null)
            {
                WeaponSystem.Instance.ReturnRounds(this, Rounds);
            }
            Rounds = 0;
            Destroy();
        }

        public void Destroy([System.Runtime.CompilerServices.CallerMemberName] string by = "")
        {
            if (Core.AutoTest.Requested && HeldBy != null && Rounds > 0)
            {
                Log.Info($"Magazine ({Rounds} rounds) in the {HeldBy.Side} hand removed by {by}.");
            }
            if (HeldBy != null && ReferenceEquals(HeldBy.Held, this))
            {
                HeldBy.Held = null;
                HeldBy.Role = HandRole.Free;
            }
            ReleaseHand(HeldBy, this);
            HeldBy = null;
            Alive.Remove(this);
            FreeBoxMesh();
            if (Go != null)
            {
                Object.Destroy(Go);
            }
        }

        /// <summary>
        /// Nearest dropped prop within <paramref name="radius"/>, measured to its collider (a long magazine can be taken
        /// by either end); only props of <paramref name="weaponKey"/> unless it is null, and only those <paramref name="accept"/>
        /// takes when given.
        /// </summary>
        public static MagazineProp NearestDropped(Vector3 point, float radius, string weaponKey, System.Func<MagazineProp, bool> accept = null)
        {
            MagazineProp best = null;
            float bestDist = radius;
            foreach (MagazineProp p in Alive)
            {
                if (!p.IsDropped || p.Go == null || weaponKey != null && p.WeaponKey != weaponKey || accept != null && !accept(p))
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

        /// <summary>
        /// The dropped prop a hand points at from afar: of this weapon, taken by <paramref name="accept"/>, within <paramref name="reach"/>
        /// metres of <paramref name="from"/> and <paramref name="degrees"/> of the pointing line; the one nearest the line.
        /// </summary>
        public static MagazineProp PointedDropped(Vector3 from, Vector3 direction, float reach, float degrees, string weaponKey, System.Func<MagazineProp, bool> accept)
        {
            MagazineProp best = null;
            float bestAngle = degrees;
            foreach (MagazineProp p in Alive)
            {
                if (!p.IsDropped || p.Go == null || weaponKey != null && p.WeaponKey != weaponKey || accept != null && !accept(p))
                {
                    continue;
                }
                Vector3 to = p.Go.transform.position - from;
                float d = to.magnitude;
                if (d > reach || d < 0.05f)
                {
                    continue;
                }
                float angle = Vector3.Angle(direction, to);
                if (angle < bestAngle)
                {
                    bestAngle = angle;
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
                    // Its object went with the scene: the hand holding it is freed too (it kept pointing at the dead prop,
                    // threw every frame and held nothing else again).
                    p.Destroy();
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
                p.LogIfFlung();
                // Lying in the world, bodies do not push it about (a magazine on the ground stays where it fell).
                if (Time.time >= p._nextBodyCheck)
                {
                    p._nextBodyCheck = Time.time + (Time.time - p._dropTime < 0.5f ? 0f : 0.25f);
                    if (Time.time - p._bodiesForgottenAt > 2f)
                    {
                        p._bodiesForgottenAt = Time.time; // bodies switched off and on collide again: ignore them afresh
                        p._ignoredBodies.Clear();
                    }
                    PropPhysics.IgnoreBodiesNear(p._colliders, p.Go.transform.position, 1f, p._ignoredBodies);
                    if (p._playerGeneration != PropPhysics.PlayerGeneration)
                    {
                        p._playerGeneration = PropPhysics.PlayerGeneration;
                        PropPhysics.IgnorePlayer(p._colliders);
                    }
                }
            }
        }

        /// <summary>
        /// A magazine flying off much faster than it was let go, in its first seconds on its own: pushed out of something it was
        /// let go inside (the gun's stand-in, a hand, a body), the "mags flying around" the user saw on pistols (2026-10-20, task
        /// 315). Logged once per drop, with what it was let go by and what is around it, at most 20 times a session.
        /// </summary>
        private void LogIfFlung()
        {
            if (_flungLogged || _flungLines >= 20 || _body == null || _body.isKinematic || Time.time - _dropTime > 3f)
            {
                return;
            }
            // Across or upward, not down: a magazine dropped off a roof falls fast and is not flung.
            Vector3 v = _body.velocity;
            float across = new Vector2(v.x, v.z).magnitude, up = v.y;
            if (!(across >= 6f && across >= _dropAcross + 3f || up >= 4f && up >= _dropUp + 3f))
            {
                return;
            }
            float speed = v.magnitude;
            _flungLogged = true;
            _flungLines++;
            Vector3 at = Go.transform.position;
            int n = Physics.OverlapSphereNonAlloc(at, 0.15f, FlungNear, ~0, QueryTriggerInteraction.Ignore);
            var near = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                Collider c = FlungNear[i];
                if (c == null || System.Array.IndexOf(_colliders, c) >= 0)
                {
                    continue;
                }
                near.Append(near.Length > 0 ? ", " : "").Append(c.name).Append(" (layer ").Append(c.gameObject.layer).Append(')');
            }
            Log.Info($"Magazine of {WeaponKey} flung: {speed:0.0} m/s {Time.time - _dropTime:0.00} s after it was let go at {_dropSpeed:0.0} m/s by {_dropBy}; "
                     + $"near it: {(near.Length > 0 ? near.ToString() : "nothing")}.");
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
