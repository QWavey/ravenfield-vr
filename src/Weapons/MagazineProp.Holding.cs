using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Weapons
{
    // MagazineProp, part: held in a hand: the grip and palming, the hand's shape on it, the rail into the well and kept
    // off the gun.
    // The class, its shape and section, dropping and lifetime: MagazineProp.cs.
    public sealed partial class MagazineProp
    {
        // ---- holding ------------------------------------------------------------------------------

        // A grabbed prop eases into the hand over a few frames instead of jumping there.
        private RigidPose _attachFrom;
        private bool _attachInRig;
        private bool _fresh = true;

        /// <summary>An ejected magazine on its way out: caught, it eases into the hand like any other (not as a pouch's fresh one).</summary>
        internal void NotFresh() => _fresh = false;
        private float _attachAt = -1f;
        private const float AttachBlend = 0.09f;
        /// <summary>This grab's own time to the hand (a magazine fetched from metres away flies a quarter second, not 0.09 s).</summary>
        private float _attachTime = AttachBlend;
        /// <summary>The holding hand was tracked at the last drawing.</summary>
        private bool _followTracked = true;

        /// <summary>Taken from afar (VRConfig.MagazineReach): to the hand over a time that grows with the way, 0.15 to 0.35 s.</summary>
        internal void AttachFromAfar(VRHand hand)
        {
            float far = Go != null && hand != null ? Vector3.Distance(Go.transform.position, hand.Palm.position) : 0f;
            Attach(hand);
            _attachTime = Mathf.Clamp(0.12f + far * 0.08f, 0.15f, 0.35f);
        }

        public void Attach(VRHand hand)
        {
            // A prop made for this grab (from the pouch, pulled from the well) starts where it belongs; one picked up,
            // caught or moved from the other fingers eases in from where it was.
            if (Go != null && !_fresh)
            {
                // From another hand's fingers, the rail or the gun: against the rig, as the player carries it; off the ground or out
                // of the air (its body free): in the world, where it was.
                _attachInRig = _body != null && _body.isKinematic;
                RigidPose at = RigidPose.Of(Go.transform);
                _attachFrom = _attachInRig ? ToRigSpace(at) : at;
                _attachAt = Time.unscaledTime;
            }
            _attachTime = AttachBlend;
            _fresh = false;
            HeldBy = hand;
            _keptOut = Vector3.zero; // a fresh grip: no offset left over from the last time it met the gun
            _touchingGun = false; // nor a touch: let go pressed on the gun, the next grab's first touch was felt as a scrape
            LostByHand = false; // (a stale flag let go of the next grip at once; 255.B3-1)
            _drawnFor = null;
            hand.Role = HandRole.Magazine;
            hand.Held = this;
            PropPhysics.MakeKinematic(_body);
            SetCollidersEnabled(false);
            _dropTime = -1f;
            OnRail = false;
            // No switch left over from a release off the rail: re-grabbed within its 0.3 s, the prop jumped back to where it was
            // let go and went round the well again (bug hunt, 2026-10-20 night). The grab eases in from _attachFrom.
            _switchAt = -1f;
            RailOutOnly = false;
            SetLayer(Go, HeldLayer);
        }

        /// <summary>
        /// World pose the prop takes in the hand: the fist closed around its lower body (just up from the base plate, as you
        /// hold a magazine to seat it), tip up-forward out between the thumb and the index, front forward. A pistol magazine
        /// the same way, its base plate in the fist (the user's picture; it was held index-point, the index along its front).
        /// </summary>
        public RigidPose HoldPose(VRHand hand)
        {
            RigidPose palm = hand.Palm;
            if (Kind == PropKind.Shell && hand.Model != null)
            {
                return ShellPinchPose(hand, palm);
            }
            Vector3 insertTarget = (palm.Up * 0.75f + palm.Forward * 0.65f).normalized;
            Quaternion rot = Quaternion.LookRotation(palm.Forward, insertTarget) * Runtime.RigidPose.Conjugate(Quaternion.LookRotation(FrontLocal, InsertDirLocal));
            // A magazine by its lower end (15% up from the base plate): held a third of the way up, its side ran past the
            // pinky, which lay flat along it and stuck out straight (the "pinky bug"); now the pinky curls toward the base
            // plate in line with the others (tools/hands/grip_check.py). Shells are pinched in the middle. A rocket is held
            // by its front part, 6 cm out past what goes into the tube: the hand pushes it in up to the mouth and stays out
            // (it slides in with the hand now, and by its middle the glove went 11 cm into an RPG-7's tube).
            float length = Vector3.Distance(BaseLocal, TipLocal);
            float gripAt = Kind == PropKind.Magazine ? Mathf.Lerp(0.15f, 0.12f, PistolBlend)
                           : Kind == PropKind.Rocket && length > 0.05f ? Mathf.Clamp(1f - (RailLength + 0.06f) / length, 0.12f, 0.5f) : 0.5f;
            Vector3 held = Vector3.Lerp(BaseLocal, TipLocal, gripAt);
            Vector3 pos = palm.position - rot * held;
            // Resting against the palm, not half inside it. The grip point (the fist hollow) lies at the palm's skin, so a
            // prop centred on it was half in the hand, and the glove, settling onto it, left the real hand: 1.8 cm for a
            // rifle magazine, 2.0 for an AK's, the full 4 cm for a machine gun's box (tools/hands/grip_check.py). Out along
            // the palm's normal by the palm's skin over the part it covers, plus its own half thickness.
            float zHold = VRHand.FistHollow.z, yHold = VRHand.FistHollow.y;
            float rest = Rig.HandModel.PalmSurfaceFromGrip(zHold - _frontHalf, zHold + _frontHalf, yHold - gripAt * length, yHold + (1f - gripAt) * length) + _sideHalf;
            pos += palm.rotation * new Vector3(-hand.Mirror, 0f, 0f) * rest;
            if (Kind == PropKind.Magazine)
            {
                // A centimetre toward the fingers: further back its back edge lay in the root of the thumb, which went
                // through it (rifle and AK magazines, grip_check.py); here the thumb wraps it and nothing is buried. A
                // machine gun's box or drum (as wide as the box pose takes) three: its 8 cm side buried the thumb 14 mm
                // at one (10 of 72 hand points inside it; at three none, every finger and the thumb on it).
                pos += palm.rotation * Vector3.forward * Mathf.Lerp(0.01f, 0.03f, BoxBlend);
            }
            return new RigidPose(pos, rot);
        }

        // Shell pinch (tools/hands/grip_check.py, "shell (pinch)"): along the fingers, tipped 22 degrees toward the palm's
        // heel, the pinch point (between the thumb and index tips) 2.5 cm up from the rim.
        private static readonly Vector3 ShellPinchAxis = new Vector3(0f, -0.3714f, 0.9285f);
        private const float ShellPinchFromRim = 0.025f;

        /// <summary>
        /// A shell the way it is pushed into a tube: pinched near its rim between the thumb and the index and middle
        /// fingers, its front out past the fingertips. Held at its middle it lay in a closed fist.
        /// </summary>
        private RigidPose ShellPinchPose(VRHand hand, RigidPose palm)
        {
            Vector3 axis = palm.rotation * ShellPinchAxis;
            Vector3 front = Vector3.ProjectOnPlane(palm.Up, axis);
            front = front.sqrMagnitude > 1e-6f ? front.normalized : palm.Forward;
            Quaternion rot = Quaternion.LookRotation(front, axis) * Runtime.RigidPose.Conjugate(Quaternion.LookRotation(FrontLocal, InsertDirLocal));
            Vector3 pinch = palm.TransformPoint(hand.Model.PinchInPalm);
            return new RigidPose(pinch - axis * ShellPinchFromRim - rot * BaseLocal, rot);
        }

        /// <summary>
        /// Held as a second magazine in a hand that already holds one (H3VR-style palming): no hand role of its own, no
        /// physics; <see cref="PalmedPose"/> places it beside the first.
        /// </summary>
        public void AttachPalmed(VRHand hand)
        {
            _fresh = false;
            HeldBy = hand;
            _touchingGun = false; // (as Attach)
            PropPhysics.MakeKinematic(_body);
            SetCollidersEnabled(false);
            _dropTime = -1f;
            OnRail = false;
            _switchAt = -1f; // (as Attach)
            RailOutOnly = false;
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
            // Both rest on the palm in HoldPose: this one goes the first one's whole thickness further out, and 4 mm. Its
            // thickness that way, both its width and its depth (a pistol magazine is held in the fist by its base too, Tasks 240).
            MagazineProp first = primary ?? this;
            Vector3 frontW = p.rotation * FrontLocal, sideW = p.rotation * Vector3.Cross(InsertDirLocal, FrontLocal);
            float half = Mathf.Abs(Vector3.Dot(towardFingers, sideW)) * first._sideHalf + Mathf.Abs(Vector3.Dot(towardFingers, frontW)) * first._frontHalf;
            float spacing = 2f * half + 0.004f;
            Vector3 insert = p.TransformDirection(InsertDirLocal);
            return new RigidPose(p.position + towardFingers * spacing - insert * 0.025f, p.rotation);
        }

        private GunVolume _volume;
        private bool _volumeTried;
        private static readonly System.Func<Vector3, bool>[] HandSolids = new System.Func<Vector3, bool>[2];
        private static readonly VRHand[] HandSolidOwners = new VRHand[2];

        private float _volumeRetryAt;

        /// <summary>Self-test: this magazine's measured solid (null until measured).</summary>
        internal GunVolume Volume => _volume;

        private void EnsureVolume()
        {
            // (Stalled as the gun's own: a read-back that never came back left the magazine with no hand shape for its life.)
            if (_volume != null && !_volume.Failed && !_volume.Stalled || Go == null)
            {
                return;
            }
            if (_volumeTried && Time.unscaledTime < _volumeRetryAt)
            {
                // A read-back that failed, or no volume at all (its build threw): measured again once a second, not every
                // frame, and not never (a null on the first try left the magazine without a hand shape for good; bug hunt).
                return;
            }
            _volumeRetryAt = Time.unscaledTime + 1f;
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
                    m.MeasureSectionFromVolume();
                    m.EaseSection();
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
            // The shapes' count and where each sits against the anchor (to the millimetre): a palmed magazine easing from the well
            // into the hand (a tactical reload) moved with the hand still, and the fingers kept the grip solved where it had been
            // until the hand moved (round 11).
            List<HeldShape> shapes = HeldShapesOf(hand);
            int version = shapes.Count;
            foreach (HeldShape s in shapes)
            {
                Vector3 at = Go.transform.InverseTransformPoint(s.Position) * 1000f;
                version = unchecked(version * 31 + Mathf.RoundToInt(at.x) * 73856093 + Mathf.RoundToInt(at.y) * 19349663 + Mathf.RoundToInt(at.z) * 83492791);
            }
            hand.Model.SolidVersion = version;
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
                    list.Add(new HeldShape { Volume = m._volume, Position = t.position, Inverse = Runtime.RigidPose.Conjugate(t.rotation) });
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

        /// <summary>
        /// Let go of while it sits in the well short of its latch (ReloadInteractions' magazine detent): out of the hand, still on its
        /// rail, held by the gun (which draws it), not dropped and not cleared away.
        /// </summary>
        internal void LetGoOnRail()
        {
            VRHand hand = HeldBy;
            if (hand != null && ReferenceEquals(hand.Held, this))
            {
                hand.Held = null;
                hand.Role = HandRole.Free;
            }
            ReleaseHand(hand, this);
            HeldBy = null;
            LostByHand = false;
            PropPhysics.MakeKinematic(_body);
            OnRail = true;
        }

        /// <summary>
        /// <paramref name="leaving"/> left the hand: once no other magazine is in it, the hand no longer closes on them. With a
        /// palmed one still between the fingers (the fresh one seated or dropped), clearing the shared solid let the fingers
        /// close through it until it was set again (bug hunt, 2026-10-23).
        /// </summary>
        private static void ReleaseHand(VRHand hand, MagazineProp leaving)
        {
            if (hand == null || hand.Model == null || hand.Model.Solid != HandSolids[(int)hand.Side])
            {
                return;
            }
            foreach (MagazineProp m in Alive)
            {
                if (!ReferenceEquals(m, leaving) && m.HeldBy == hand)
                {
                    return;
                }
            }
            hand.Model.Solid = null;
        }

        /// <summary>A made stand-in's shape is its own (a cube): under the type's key the fingers closed on that cube round the real magazine for the session.</summary>
        private string ShapeKey => "prop:" + WeaponKey + ":" + Kind + (OwnCopy ? "" : ":made");

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
            FreeBoxMesh();
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

        /// <summary>
        /// On the well's line (pulled out or pushed in): drawn there, and the hand's glove moved to hold it there as it holds
        /// one in the free hand (<see cref="Rig.HandModel.HeldPalm"/>), the fingers closing on it where it is.
        /// </summary>
        public void ShowOnRail(RigidPose railPose)
        {
            ShapeHand();
            TakeRenderedShape();
            ShowAt(railPose);
            // Off the line again it goes from here toward the hand (from where it was drawn before it went on, a rocket
            // pulled out of the tube's mouth went back through the launcher to its side).
            HeldWeapon gunNow = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            if (gunNow != null && gunNow.Rig.GunBody != null)
            {
                _drawnInGun = gunNow.Rig.ToGun(railPose.position);
                _drawnFor = gunNow;
            }
            if (HeldBy != null && HeldBy.Model != null)
            {
                // The palm such that this magazine, at its pose on the line, sits in the hand as HoldPose puts it.
                RigidPose palm = HeldBy.Palm;
                RigidPose hold = HoldPose(HeldBy);
                RigidPose heldPalm = RigidPose.Of(Go.transform).Multiply(hold.Inverse().Multiply(palm));
                // A pistol magazine going in: as it slides up the grip the hand lets go of it and turns into a push on its
                // base plate, the thumb coming away (the user's pictures: more satisfying than a fist all the way in).
                float push = PistolSized && !FromSeat && MovingIn && RailLength > 1e-4f ? Mathf.InverseLerp(0.2f, 0.75f, RailDepth / RailLength) : 0f;
                if (Kind == PropKind.Rocket && GoesAllTheWayIn && !FromSeat && MovingIn)
                {
                    // A missile that goes all the way into its tube: the hand cannot follow it in, so for the last of it the
                    // hand turns into a push on its nose, as on a pistol magazine's base plate.
                    push = Mathf.InverseLerp(0.45f, 0.8f, RailDepth / RailLength);
                }
                if (push > 0f)
                {
                    push = push * push * (3f - 2f * push);
                    RigidPose pushPalm = PushPalm(HeldBy);
                    heldPalm = new RigidPose(Vector3.Lerp(heldPalm.position, pushPalm.position, push), Quaternion.Slerp(heldPalm.rotation, pushPalm.rotation, push));
                    HeldBy.Model.PushBlend = push;
                    HeldBy.Model.PushBlendFrame = Time.frameCount;
                }
                HeldBy.Model.HeldPalm = heldPalm;
                HeldBy.Model.HeldPalmFrame = Time.frameCount;
                HeldShapesFrame[(int)HeldBy.Side] = -1; // moved: the fingers' solid is measured again from where it is now
            }
        }

        /// <summary>A rocket whose whole length goes into the tube (within 10 cm): nothing of it is left for the hand at the end.</summary>
        internal bool GoesAllTheWayIn => Kind == PropKind.Rocket && RailLength > Vector3.Dot(TipLocal - BaseLocal, InsertDirLocal) - 0.1f;

        /// <summary>
        /// The palm pushing this magazine's base plate home: the palm flat under it, facing up the magazine, its heel on the
        /// base plate and the fingers forward toward the muzzle, under the gun hand's.
        /// </summary>
        private RigidPose PushPalm(VRHand hand)
        {
            Transform t = Go.transform;
            Vector3 up = t.TransformDirection(InsertDirLocal).normalized;
            Vector3 front = Vector3.ProjectOnPlane(t.TransformDirection(FrontLocal), up).normalized;
            Vector3 right = -hand.Mirror * up; // the palm's normal (-Mirror * right) points up the magazine
            Vector3 handUp = Vector3.Cross(front, right);
            Quaternion rot = Quaternion.LookRotation(front, handUp);
            // The palm's skin about 1.7 cm out from its centre; the heel (2.5 cm toward the wrist) under the base plate.
            Vector3 basePlate = t.TransformPoint(BaseLocal);
            return new RigidPose(basePlate - up * 0.017f + front * 0.025f, rot);
        }

        public void FollowHand()
        {
            ShapeHand();
            TakeRenderedShape();
            if (HeldBy != null && !OnRail)
            {
                RigidPose target = HoldPose(HeldBy);
                // The hand's tracking back after a gap: from where the magazine hung to where the hand is now over a tenth of a
                // second. It jumped the hand's whole way in the gap (5 to 40 cm) in one frame (scout, 2026-11-11).
                bool tracked = HeldBy.Tracked;
                if (tracked && !_followTracked && Go != null && _attachAt < 0f && (Go.transform.position - target.position).sqrMagnitude > 0.03f * 0.03f)
                {
                    _attachInRig = true;
                    _attachFrom = ToRigSpace(RigidPose.Of(Go.transform));
                    _attachAt = Time.unscaledTime;
                    _attachTime = 0.1f;
                }
                _followTracked = tracked;
                float t = _attachAt >= 0f ? (Time.unscaledTime - _attachAt) / Mathf.Max(0.01f, _attachTime) : 1f;
                if (t < 1f)
                {
                    float k = t * t * (3f - 2f * t);
                    RigidPose from = _attachInRig ? FromRigSpace(_attachFrom) : _attachFrom;
                    target = new RigidPose(Vector3.Lerp(from.position, target.position, k), Quaternion.Slerp(from.rotation, target.rotation, k));
                }
                else
                {
                    _attachAt = -1f;
                }
                ShowAt(KeepOffGun(target));
            }
            if (HeldBy != null)
            {
                HeldShapesFrame[(int)HeldBy.Side] = -1; // moved: the fingers' solid is measured again from where it is now
            }
        }

        /// <summary>The hand went on far past the gun this magazine is kept out of: it is let go of (ReloadInteractions).</summary>
        internal bool LostByHand;

        /// <summary>How far the magazine in the hand is drawn away from the hand to stay out of the gun now.</summary>
        private Vector3 _keptOut;
        /// <summary>The frame the keep-out last ran (its pacing needs the frame before).</summary>
        private int _easeFrame = -10;
        /// <summary>How far the keep-out's sweep took the magazine the last time it ran (metres in that frame).</summary>
        private float _easeTravel;
        /// <summary>The magazine's way toward the hand was cut short by the pace the last time (it goes on from where it is drawn,
        /// and the held-still shortcut is not taken).</summary>
        private bool _keptShort;
        /// <summary>
        /// How far a magazine behind its hand may go in a frame (metres): the player's own way that frame, and on top at most
        /// 8 mm more than it went the frame before (<paramref name="lastTravel"/>, without the player's way), never more than
        /// four tenths of what is left to the hand or the hand's own way that frame, whichever is more, and 4 mm at least. A rule of
        /// its own so it is run outside the game too (the offline tests).
        /// </summary>
        internal static float PaceBudget(float lastTravel, float behind, float handWay, float rigWay) =>
            rigWay + Mathf.Max(0.004f, Mathf.Min(lastTravel + 0.008f, Mathf.Max(Mathf.Max(0f, behind - rigWay) * 0.4f, handWay + 0.004f))); // (the hand's way and 4 mm: with its way alone it stayed 3 cm behind a moving hand for as long as that moved; four tenths: by half, the last steps of 10 cm went 26 then 13 mm; offline test)

        /// <summary>The hand's place the last time the keep-out ran.</summary>
        private Vector3 _easeTarget;
        /// <summary>The rig's place the last time the keep-out's sweep ran.</summary>
        private Vector3 _easeRoot;
        // On the rail nothing keeps it out of the gun: the last keep-out eases away with the switch onto the line (the palmed
        // magazine beside it kept the stale offset for as long as this one sat on the rail; bug hunt 251.3).
        // On the rail it eases to nothing over the switch and stays nothing after it: once the switch was over, the stale offset came
        // back and a palmed magazine jumped by it when the fresh one seated (review, 2026-10-20).
        internal Vector3 KeptOut => !OnRail ? _keptOut : _switchAt >= 0f ? _keptOut * (1f - Mathf.Clamp01((Time.unscaledTime - _switchAt) / SwitchBlend)) : Vector3.zero;
        /// <summary>Rows of points along a magazine for the keep-out (five, 4-5.5 cm apart on a rifle magazine, let a plate of
        /// the receiver between them: the dense probe found up to 300 points of a PATRIOT magazine in the gun; check-1017f).</summary>
        internal const int MagRows = 9;
        // Nine a row: the middle, the four edge middles and the four corners. The edges alone (a "+" a row) left a magazine's
        // corners unsampled: pushed sideways, its leading face had one point a row, and the JOLT-20-S's and PATRIOT GL's went on
        // through the gun with up to 70 of the test's dense probe points in its body (autotest of 2026-10-21).
        // (Fifteen, five across the depth, were tried for the HK416s and changed nothing: their "went through" was the test's
        // magazine starting on the far side of the gun, resting against it; task 328 d.)
        private const int RowPoints = 9;
        private static readonly float[] RowFront = { -1f, 0f, 1f };
        private static readonly float[] RowSide = { -1f, 0f, 1f };

        /// <summary>One row of the keep-out's points round <paramref name="c"/> (out to <paramref name="front"/> and <paramref name="side"/>), from index <paramref name="n"/>.</summary>
        private static int FillRow(Vector3[] points, int n, Vector3 c, Vector3 front, Vector3 side)
        {
            foreach (float u in RowFront)
            {
                foreach (float v in RowSide)
                {
                    points[n++] = c + front * u + side * v;
                }
            }
            return n;
        }

        /// <summary>Self-test: how many points the keep-out tests on this magazine.</summary>
        internal int KeepOutPoints => KeepOutRows * RowPoints;
        // How far out a long gun's magazine is sampled (of its half-widths; a pistol's to its edges). At 0.8 the outer band went
        // through thin parts of the gun unseen: the GREASER's and JOLT-20-S's magazines put up to 114 of the test's dense probe
        // points in the gun's body, sideways (autotest, 2026-10-21).
        private const float KeepOutEdge = 0.95f;
        // Rows at most 2 cm apart on a long magazine (up to 16): nine over the GREASER's 28.6 cm stood 3.6 cm apart, and a thin
        // part of the gun passed between them sideways (autotest, 2026-10-21).
        private const int MaxMagRows = 16;
        private static readonly Vector3[] Samples = new Vector3[MaxMagRows * RowPoints];

        /// <summary>The keep-out's rows along this magazine: nine, more on a long one (2 cm apart at most).</summary>
        internal int KeepOutRows => Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(BaseLocal, TipLocal) / 0.02f) + 1, MagRows, MaxMagRows);

        /// <summary>
        /// The well lets a magazine in only on its own line: within the rail's reach of it sideways (4.5 cm) and turned less
        /// than 40 degrees from it. Off the line, the well's opening is solid like the rest of the gun: a PKM's box swept
        /// sideways 13 cm over its seat passed through the receiver, all its points inside the opening's box (check-1017f).
        /// </summary>
        private bool OnWellLine(RigidPose seat, Vector3 at, Quaternion rot)
        {
            Vector3 local = seat.InverseTransformPoint(at);
            // Aside by its farthest end, not its pivot: turned 10 degrees past the mouth, a 1911 magazine's tip stood 1.1 cm off the
            // line while its pivot read 0, the well stayed open and the tip went through the grip's wall (review, 2026-10-20).
            Vector3 scale = Go != null ? Go.transform.lossyScale : Vector3.one;
            Vector3 tip = Vector3.Scale(TipLocal, scale), bottom = Vector3.Scale(BaseLocal, scale);
            float aside = Mathf.Max(Vector3.ProjectOnPlane(local, InsertDirLocal).magnitude,
                Mathf.Max(Vector3.ProjectOnPlane(seat.InverseTransformPoint(at + rot * tip) - tip, InsertDirLocal).magnitude,
                          Vector3.ProjectOnPlane(seat.InverseTransformPoint(at + rot * bottom) - bottom, InsertDirLocal).magnitude));
            // Past the mouth the well is open only to a magazine nearly straight on its line and within its play: turned in it,
            // its corners went out through a pistol grip's walls, and 4.5 cm aside, the half of it over the seated magazine
            // was let in and its far side stood in the grip's wall (user, 2026-10-19, the 1911s; task 270; check-1019k: 23-102
            // points in the wall pushed in from the side). At the mouth it may come in turned and aside, as the rail takes it
            // on there.
            float depth = Vector3.Dot(local, InsertDirLocal) + RailLength; // 0: its tip at the well's mouth, RailLength: seated
            bool past = depth > ReloadInteractions.MouthReach;
            if (aside > (past ? WellPlay * 0.75f : 0.045f))
            {
                return false;
            }
            return Quaternion.Angle(rot, seat.rotation) < (past ? InWellTurn : 40f);
        }

        /// <summary>How far a magazine may be turned off its line inside its well, past the mouth (task 270).</summary>
        internal const float InWellTurn = 12f;
        /// <summary>How much bigger than the magazine its well is left open (the whole size: half of it a side).</summary>
        internal const float WellPlay = 0.008f;
        private static readonly Vector3[] RocketSamples = new Vector3[5 * 24];
        private static readonly Vector3[] SlideAxes = new Vector3[3];
        private static readonly Vector3[] WayOut = new Vector3[7];
        /// <summary>Where the magazine was last drawn, in the space of the gun it was kept out of (it moves with that gun).</summary>
        private Vector3 _drawnInGun;
        private HeldWeapon _drawnFor;
        private Vector3 _keptTargetInGun;
        private Quaternion _keptRotInGun = Quaternion.identity;
        private System.Func<Vector3, bool> _keptSolid;
        private bool _keptMagShown;

        /// <summary>
        /// The magazine held beside the one in the hand (palmed): kept out of the gun the same way, on its own (it borrowed
        /// the first one's keep-out only, and went through the gun wherever the first one was clear; user, 2026-10-16).
        /// With the first one on its rail the glove is left to that one (<paramref name="moveHand"/> false).
        /// </summary>
        internal RigidPose KeptOffGun(RigidPose target, bool moveHand)
        {
            _keepHand = moveHand;
            try
            {
                return KeepOffGun(target);
            }
            finally
            {
                _keepHand = true;
            }
        }

        private bool _keepHand = true;

        /// <summary>The rig whose well a held prop goes into: the active sub weapon's when it has a body, else the gun's own.</summary>
        private static WeaponRig WellRig(HeldWeapon gun) => gun.ActiveRig != null && gun.ActiveRig.GunBody != null ? gun.ActiveRig : gun.Rig;

        /// <summary>
        /// A magazine in the hand does not pass through the gun. It goes from where it was drawn toward the hand, stops
        /// where it meets the gun's body and slides along it (the glove holding it stays with it); its own magwell stays
        /// open to it (the rail takes it from there, and a fresh one pushed into the seated one kicks it out: those go by
        /// the hand). Only checking where the hand is now let a hard or fast push through: a magazine past the middle of
        /// the receiver came out the far side, and one buried deeper than the search reached went straight through.
        /// A rocket the same against its launcher (user, 2026-10-16: it went through the tube): it goes in only at the
        /// tube's mouth, on its line, where it is no longer kept out.
        /// </summary>
        private RigidPose KeepOffGun(RigidPose target)
        {
            HeldWeapon gun = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            System.Func<Vector3, bool> solid = gun != null ? gun.HandsSolid : null;
            Vector3 drawn = target.position;
            bool rocket = Kind == PropKind.Rocket;
            if (solid != null && HeldBy != null && (Kind == PropKind.Magazine || rocket) && gun.Rig.GunBody != null
                && (target.position - gun.GripWorld.position).sqrMagnitude < (rocket ? 1.6f * 1.6f : 0.7f * 0.7f))
            {
                WeaponRigData d = gun.Rig.Data;
                // The well of the weapon in use: a sub weapon's magazine or rocket goes into its own well, and was kept out
                // of it as the parent's body (255.B3-3).
                WeaponRig wellRig = WellRig(gun);
                RigidPose seat = wellRig.GunBodyPose.Multiply(wellRig.Data.MagSeat);
                Bounds well = wellRig.Data.MagBounds;
                // Open only on the well's own line, 4 mm round the magazine: at 1.5 cm a side a magazine held off the line went
                // into a pistol's grip through its 4 mm wall and showed through its sides (user, 2026-10-16, many pistols).
                well.Expand(WellPlay);
                // Points over the magazine: five along it, each at its middle and four edges. A rocket every 6 cm along it
                // (a launcher's tube is a few centimetres thick: five points over a metre of rocket would pass it between
                // them), half way out to its edges (its box is the warhead's: the thin tail would stop short of the tube).
                Vector3 side = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
                Vector3[] points = rocket ? RocketSamples : Samples;
                int along = rocket ? Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(BaseLocal, TipLocal) / 0.06f) + 1, 5, RocketSamples.Length / 5) : KeepOutRows;
                // A pistol magazine to its edges: its box is its shape, and at 80% its outer 1.5-3 mm went into a 3-5 mm grip wall
                // unseen (task 270). Others at 80%: a rifle magazine's box is taken where the hand holds it, and a curved one's
                // edges along its length would meet the receiver before the magazine does.
                float edge = rocket ? 0.5f : _pistolGun ? 1f : KeepOutEdge;
                int n = 0;
                for (int a = 0; a < along; a++)
                {
                    Vector3 c = Vector3.Lerp(BaseLocal, TipLocal, a / (float)(along - 1));
                    if (rocket)
                    {
                        points[n++] = c;
                        points[n++] = c + FrontLocal * (_frontHalf * edge);
                        points[n++] = c - FrontLocal * (_frontHalf * edge);
                        points[n++] = c + side * (_sideHalf * edge);
                        points[n++] = c - side * (_sideHalf * edge);
                    }
                    else
                    {
                        n = FillRow(points, n, c, FrontLocal * (_frontHalf * edge), side * (_sideHalf * edge));
                    }
                }
                int count = n;
                Quaternion rot = target.rotation;
                // Turned once here (the buffer is refilled every call): turned at every point of every 5 mm step, a magazine
                // swept against the gun made thousands of Quaternion * Vector3 calls a frame for the same rotation.
                for (int i = 0; i < count; i++)
                {
                    points[i] = rot * points[i];
                }
                bool BlockedAt(Vector3 at)
                {
                    // A rocket has no well open to it from the side: where it sits seated is the tube's front itself, and
                    // left open, a rocket held across the launcher went straight through the tube there.
                    bool open = !rocket && OnWellLine(seat, at, rot);
                    for (int i = 0; i < count; i++)
                    {
                        Vector3 p = at + points[i];
                        if (!solid(p))
                        {
                            continue;
                        }
                        // The well opens to the gun's body only: a magazine seated in it stays solid, so a fresh one stops at its
                        // base, where a firm push or strike knocks the seated one out, instead of passing through it (user,
                        // 2026-10-18, task 260 c).
                        if (open && Runtime.BoundsMath.Contains(well, seat.InverseTransformPoint(p)) && !gun.SeatedMagSolid(p))
                        {
                            continue;
                        }
                        return true;
                    }
                    return false;
                }
                // From where it is toward where it is going, in steps of half a centimetre: the last free step.
                Vector3 Advance(Vector3 from, Vector3 delta)
                {
                    int steps = Mathf.Clamp(Mathf.CeilToInt(delta.magnitude / 0.005f), 1, 80);
                    Vector3 last = from;
                    for (int i = 1; i <= steps; i++)
                    {
                        Vector3 p = from + delta * ((float)i / steps);
                        if (BlockedAt(p))
                        {
                            return last;
                        }
                        last = p;
                    }
                    return last;
                }

                Vector3 from = ReferenceEquals(_drawnFor, gun) ? gun.Rig.ToWorld(_drawnInGun) : target.position;
                // Held against the gun with the hand still against it (within a millimetre and half a degree, in gun space):
                // last frame's place, without the search's up to a few thousand solid tests a frame (255.P2-3). Only for a
                // blocked one: a free one is drawn exactly at the hand.
                Vector3 targetInGun = gun.Rig.ToGun(target.position);
                Quaternion rotInGun = Runtime.RigidPose.Conjugate(gun.Rig.GunBody.rotation) * target.rotation;
                if (ReferenceEquals(_drawnFor, gun) && ReferenceEquals(_keptSolid, solid) && _keptMagShown == gun.MagSolidShown && _keptOut.sqrMagnitude > 1e-6f && !_keptShort
                    && (targetInGun - _keptTargetInGun).sqrMagnitude < 1e-6f && Quaternion.Angle(rotInGun, _keptRotInGun) < 0.5f)
                {
                    drawn = from;
                }
                else
                {
                    _keptTargetInGun = targetInGun;
                    _keptRotInGun = rotInGun;
                    _keptSolid = solid;
                    _keptMagShown = gun.MagSolidShown;
                    if (BlockedAt(from))
                    {
                        // Already in the gun (taken out of the pouch inside it, the gun swung onto it): out the shortest way,
                        // from the bore outward first, then up, down and to either side.
                        Vector3 bore = gun.Rig.ToWorld(d.MuzzlePoint);
                        Vector3 away = Vector3.ProjectOnPlane(from - bore, gun.Rig.AimWorld);
                        Vector3 up = gun.Rig.UpWorld, right = Vector3.Cross(up, gun.Rig.AimWorld).normalized;
                        Vector3[] dirs = WayOut; // filled in place: no array made each frame the magazine starts in the gun
                        dirs[0] = away.sqrMagnitude > 1e-6f ? away.normalized : -up;
                        dirs[1] = -up;
                        dirs[2] = up;
                        dirs[3] = right;
                        dirs[4] = -right;
                        dirs[5] = -gun.Rig.AimWorld;
                        dirs[6] = gun.Rig.AimWorld;
                        float best = float.MaxValue;
                        Vector3 freed = target.position;
                        foreach (Vector3 dir in dirs)
                        {
                            for (float step = 0.005f; step <= 0.25f && step < best; step += 0.005f)
                            {
                                if (!BlockedAt(from + dir * step))
                                {
                                    best = step;
                                    freed = from + dir * step;
                                    break;
                                }
                            }
                        }
                        from = freed;
                    }
                    // Its way toward the hand at a pace, not all of it in one frame: held out of the gun while the hand went on, it
                    // slid the whole free way to the hand on the frame the way opened (up to 45 cm; "parts of the hand or mags are
                    // glitching around (this happens randomly with force or smth)", user, 2026-11-11; 90 mm in the smoothness
                    // check, and 27 mm still with the offset's length paced after the search: the search itself is what runs
                    // ahead). Behind the hand, a frame's way is at most 8 mm more than the frame before went (it gathers from
                    // standing; with the hand's own way allowed on top it still started with that in one step: 87 -> 60 mm,
                    // measured), and never more than half of what is left, 4 mm at least. At the hand (within 3 mm the frame
                    // before) it follows freely, not held back at all. 10 cm behind closes in about eight frames.
                    bool ran = _easeFrame == Time.frameCount - 1;
                    float behind = (target.position - from).magnitude;
                    // The player's own way that frame (walking, running, a turn's jump) is never part of the pace: the gun's frame the
                    // sweep starts from is a frame behind the rig, and paced with it a magazine taken while running trailed 4 to
                    // 6 cm (autotest, 2026-11-11).
                    Transform rigRoot = VRRig.Instance != null ? VRRig.Instance.Root : null;
                    Vector3 rootAt = rigRoot != null ? rigRoot.position : Vector3.zero;
                    float rigWay = ran ? (rootAt - _easeRoot).magnitude : 0f;
                    _easeRoot = rootAt;
                    float handWay = Mathf.Max(0f, (target.position - _easeTarget).magnitude - rigWay);
                    float budget = !ran || _attachAt >= 0f || _keptOut.sqrMagnitude < 0.003f * 0.003f // (nor while it eases into the hand: that ease is its pace)
                        ? float.MaxValue
                        : PaceBudget(_easeTravel, behind, handWay, rigWay);
                    // (Or the hand's own way that frame, where that is more than the half: slid along the gun beside a moving hand
                    // it keeps up with it.)
                    Vector3 swept = from;
                    _keptShort = false;
                    Vector3 Paced(Vector3 start, Vector3 delta)
                    {
                        float len = delta.magnitude;
                        if (len > budget)
                        {
                            _keptShort = true;
                            if (budget <= 1e-5f)
                            {
                                return start;
                            }
                            delta *= budget / len;
                        }
                        Vector3 reached = Advance(start, delta);
                        budget -= (reached - start).magnitude;
                        return reached;
                    }
                    drawn = Paced(from, target.position - from);
                    Vector3 left = target.position - drawn;
                    if (left.sqrMagnitude > 1e-6f)
                    {
                        // Met the gun: what is left of the move slides along it, one of the gun's axes at a time.
                        Vector3 upW = gun.Rig.UpWorld, aimW = gun.Rig.AimWorld, rightW = Vector3.Cross(upW, aimW).normalized;
                        SlideAxes[0] = aimW;
                        SlideAxes[1] = upW;
                        SlideAxes[2] = rightW;
                        foreach (Vector3 axis in SlideAxes)
                        {
                            Vector3 part = axis * Vector3.Dot(target.position - drawn, axis);
                            if (part.sqrMagnitude > 1e-8f)
                            {
                                drawn = Paced(drawn, part);
                            }
                        }
                    }
                    // The hand far through or past the gun: the magazine stays outside it, the glove with it (it jumped through the
                    // gun to the hand 30 cm on; user, 2026-10-16). Pulled more than 45 cm from it, the hand lets go of it.
                    _easeTravel = Mathf.Max(0f, (drawn - swept).magnitude - rigWay);
                    LostByHand = (drawn - target.position).sqrMagnitude > 0.45f * 0.45f;
                    _drawnInGun = gun.Rig.ToGun(drawn);
                    _drawnFor = gun;
                }
            }
            else
            {
                _drawnFor = null;
                LostByHand = false;
            }
            Vector3 offset = drawn - target.position;
            _easeFrame = Time.frameCount;
            _easeTarget = target.position;
            _keptOut = offset;
            ContactSounds(gun, drawn, _keptOut.sqrMagnitude > 1e-6f);
            if (_keptOut.sqrMagnitude < 1e-8f)
            {
                return target;
            }
            if (_keepHand && HeldBy != null && HeldBy.Model != null)
            {
                Rig.HandModel model = HeldBy.Model;
                bool setThisFrame = model.KeepOutFrame == Time.frameCount;
                // Both shifts keep the glove out (backed out of the well, and to where the magazine is drawn): added, not the
                // longer one kept, which dropped the magazine's sideways offset (255.I2-3).
                model.KeepOut = setThisFrame ? model.KeepOut + _keptOut : _keptOut;
                model.KeepOutFrame = Time.frameCount;
            }
            return new RigidPose(drawn, target.rotation);
        }

        private static readonly Vector3[] RowBuffer = new Vector3[RowPoints];

        /// <summary>
        /// Autotest: how many of the keep-out's check points of the magazine are inside <paramref name="gun"/>'s body where it is
        /// drawn now (its magwell does not count, a magazine seated in it does: task 260 c). -1 when the gun's solid is not measured.
        /// </summary>
        internal int PointsInsideGun(HeldWeapon gun)
        {
            System.Func<Vector3, bool> solid = gun != null ? gun.HandsSolid : null;
            if (solid == null || Go == null || gun.Rig.GunBody == null)
            {
                return -1;
            }
            WeaponRig wellRig = WellRig(gun);
            RigidPose seat = wellRig.GunBodyPose.Multiply(wellRig.Data.MagSeat);
            Bounds well = wellRig.Data.MagBounds;
            well.Expand(WellPlay);
            Vector3 side = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
            Transform t = Go.transform;
            bool open = OnWellLine(seat, t.position, t.rotation);
            int inside = 0;
            int rows = KeepOutRows;
            for (int a = 0; a < rows; a++)
            {
                Vector3 c = Vector3.Lerp(BaseLocal, TipLocal, a / (float)(rows - 1));
                float edge = _pistolGun ? 1f : KeepOutEdge;
                FillRow(RowBuffer, 0, c, FrontLocal * (_frontHalf * edge), side * (_sideHalf * edge));
                foreach (Vector3 local in RowBuffer)
                {
                    Vector3 p = t.TransformPoint(local);
                    // The same rule as KeepOffGun: the well is open to the gun's body only, not to a magazine seated in it.
                    if (solid(p) && !(open && Runtime.BoundsMath.Contains(well, seat.InverseTransformPoint(p)) && !gun.SeatedMagSolid(p)))
                    {
                        inside++;
                    }
                }
            }
            return inside;
        }

        /// <summary>Self-test: the middle of the last probe's points in the gun's body outside the well's opening (world; zero when none).</summary>
        internal Vector3 ProbeOutCentre { get; private set; }

        /// <summary>
        /// Self-test: the magazine as drawn now, sampled every 5 mm over its whole box (the keep-out tests 25 points): how
        /// many lie in the gun's solid inside the well's opening (let through by design, not a seated magazine) and how many
        /// outside it. A sideways pass that crosses the receiver through the opening, or between the keep-out's rows, shows
        /// up here (252 ae).
        /// </summary>
        internal void ProbeInside(HeldWeapon gun, out int points, out int inWell, out int outWell)
        {
            points = inWell = outWell = 0;
            ProbeOutCentre = Vector3.zero;
            Vector3 outSum = Vector3.zero;
            System.Func<Vector3, bool> solid = gun != null ? gun.HandsSolid : null;
            if (solid == null || Go == null || gun.Rig.GunBody == null)
            {
                return;
            }
            WeaponRig wellRig = WellRig(gun);
            RigidPose seat = wellRig.GunBodyPose.Multiply(wellRig.Data.MagSeat);
            Bounds well = wellRig.Data.MagBounds;
            well.Expand(WellPlay);
            Vector3 side = Vector3.Cross(InsertDirLocal, FrontLocal).normalized;
            Transform t = Go.transform;
            bool open = OnWellLine(seat, t.position, t.rotation);
            int na = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(BaseLocal, TipLocal) / 0.005f) + 1, 2, 80);
            int nf = Mathf.Clamp(Mathf.CeilToInt(2f * _frontHalf / 0.005f) + 1, 2, 20);
            int ns = Mathf.Clamp(Mathf.CeilToInt(2f * _sideHalf / 0.005f) + 1, 2, 20);
            for (int a = 0; a < na; a++)
            {
                Vector3 c = Vector3.Lerp(BaseLocal, TipLocal, a / (float)(na - 1));
                for (int f = 0; f < nf; f++)
                {
                    for (int s = 0; s < ns; s++)
                    {
                        Vector3 p = t.TransformPoint(c + FrontLocal * (_frontHalf * (2f * f / (nf - 1) - 1f)) + side * (_sideHalf * (2f * s / (ns - 1) - 1f)));
                        points++;
                        if (!solid(p))
                        {
                            continue;
                        }
                        // A magazine seated in the well is not let through (task 260 c): its points are outside the opening.
                        if (open && Runtime.BoundsMath.Contains(well, seat.InverseTransformPoint(p)) && !gun.SeatedMagSolid(p))
                        {
                            inWell++;
                        }
                        else
                        {
                            outWell++;
                            outSum += p;
                        }
                    }
                }
            }
            if (outWell > 0)
            {
                ProbeOutCentre = outSum / outWell;
            }
        }

        /// <summary>Self-test: the keep-out's own box of the magazine (front x side x length, cm).</summary>
        internal string KeepOutBox => $"{_frontHalf * 200f:0.0} x {_sideHalf * 200f:0.0} x {Vector3.Distance(BaseLocal, TipLocal) * 100f:0.0} cm";

        private bool _touchingGun;
        private Vector3 _lastScrapeAt;

        /// <summary>Its tip within 4.5 cm of the well's mouth (where it goes in): against the edge of the well, not the gun's side.</summary>
        private bool AtWellMouth(HeldWeapon gun)
        {
            WeaponRig rig = gun.Rig;
            if (Kind != PropKind.Magazine || Go == null || rig == null || rig.GunBody == null)
            {
                return false;
            }
            RigidPose seat = rig.GunBodyPose.Multiply(rig.Data.MagSeat);
            Vector3 mouth = seat.TransformPoint(TipLocal) - seat.TransformDirection(InsertDirLocal) * RailLength;
            return (TipWorld - mouth).sqrMagnitude < 0.045f * 0.045f;
        }
        // Each magazine's own: shared, the palmed one's knock silenced the fresh one's in the same frame (255.I1-6).
        private float _lastContactSound = -1f;

        /// <summary>
        /// A held magazine meeting the gun buzzes the hand, harder the faster it came in, and more lightly while it scrapes
        /// along the gun. Felt only: no knock or scrape sound (user, 2026-10-16, 252 r). At most four buzzes a second (a
        /// magazine resting against the gun does not buzz the hand over and over).
        /// </summary>
        private void ContactSounds(HeldWeapon gun, Vector3 drawn, bool touching)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            if (sys == null || HeldBy == null || gun == null)
            {
                _touchingGun = false;
                return;
            }
            float now = Time.unscaledTime;
            if (touching && now - _lastContactSound >= 0.25f)
            {
                VRHand dom = sys.Rig.Dominant;
                float speed = (HeldBy.BodyRelativeVelocity - (dom != null ? dom.BodyRelativeVelocity : Vector3.zero)).magnitude; // (against the body: a turn is no motion between the hands)
                // Felt, not heard: the knock and scrape sounds of a magazine meeting the gun were too goofy (user, 2026-10-16).
                if (!_touchingGun)
                {
                    float loud = Mathf.Clamp01(0.2f + speed / 1.5f);
                    HeldBy.Haptic(0.02f + 0.03f * loud, 150f, 0.3f + 0.5f * loud);
                    dom?.Haptic(0.015f, 150f, 0.2f * loud);
                    _lastContactSound = now;
                    _lastScrapeAt = drawn;
                }
                else if ((drawn - _lastScrapeAt).sqrMagnitude > 0.015f * 0.015f && speed > 0.1f)
                {
                    HeldBy.Haptic(0.012f, 220f, 0.15f);
                    _lastContactSound = now;
                    _lastScrapeAt = drawn;
                    if (AtWellMouth(gun))
                    {
                        // Ground along the well's edge, looking for the way in: heard too, quietly, as the gun's own magazine going in
                        // starts (a slice of its game sound, a little lower; user, 2026-11-03, idea 1). Only there: elsewhere on the
                        // gun a touch stays felt only (user, 2026-10-16).
                        HeldBy.Haptic(0.02f, 240f, 0.25f);
                        GameSounds.PlayPart(gun.Rig.Data, MechSound.MagIn, TipWorld, 0f, 0.3f, 0.12f + 0.18f * Mathf.Clamp01(speed), Random.Range(0.86f, 0.94f));
                    }
                }
            }
            _touchingGun = touching;
        }
    }
}
