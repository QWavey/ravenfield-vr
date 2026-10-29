using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The weapon's own first-person arms (its gloves, sleeves and authored finger poses) driven by the VR hands:
    /// both arms are re-rooted at shoulders on the player's body; the gun hand keeps its authored grip on the weapon;
    /// the off hand takes the authored foregrip pose while it holds the gun and follows the controller otherwise (settled
    /// onto a bolt, handle or cover it works, as the VR glove is), easing between the two. Two-bone IK per arm, elbow
    /// bent toward a natural pole; an out-of-reach target slides the shoulder toward it.
    /// </summary>
    internal sealed class ArmIK
    {
        private sealed class Arm
        {
            public ArmChain Chain;
            public Transform Root, Upper, Lower, Hand;
            public bool Valid => Root != null && Upper != null && Lower != null && Hand != null;
        }

        private readonly WeaponSystem _sys;
        private readonly HeldWeapon _held;
        private readonly Arm _right, _left;
        /// <summary>The eased shift of the off hand onto the part it works (see <see cref="HandModel.GraspAnchor"/>).</summary>
        private Vector3 _anchorOffset;

        /// <summary>How far the off arm has gone over to the weapon's support grip (0: the controller, 1: the grip).</summary>
        private float _onGunBlend = -1f;

        /// <summary>The support hand's fingers as its reload holds the magazine (MagHoldPose), and how far the hand has gone onto it.</summary>
        private readonly Transform[] _magFingers;
        private readonly Quaternion[] _magFingerLocal;
        private float _onMagBlend;
        private RigidPose _lastOnMag;

        /// <summary>Self-test: after the last solve, how far (m) each hand bone ended from its target, and whether a shoulder had to slide.</summary>
        internal float GunHandError { get; private set; } = -1f;
        internal float OffHandError { get; private set; } = -1f;
        internal bool Stretched { get; private set; }

        /// <summary>Self-test: the off hand is all the way onto the magazine it holds, as the gun's reload holds it.</summary>
        internal bool OnMagazine => _onMagBlend >= 0.999f;

        /// <summary>
        /// The weapon's left hand's fingers, thumb to little finger, up to three joints each (by name, as the analysis finds them);
        /// a missing finger is null. Posed from the controller while the hand is off the gun (<see cref="PoseFreeFingers"/>).
        /// </summary>
        private readonly Transform[][] _fingers;
        /// <summary>Each joint's pose from the animation (the support grip), and what this class wrote to it last frame.</summary>
        private readonly Quaternion[][] _fingerBase, _fingerWritten;
        private bool _fingersWritten;

        public bool Active => _right.Valid || _left.Valid;

        public ArmIK(WeaponSystem sys, HeldWeapon held)
        {
            _sys = sys;
            _held = held;
            Transform root = held.Weapon.transform;
            // The model's right arm is the gun arm (its mesh is not mirrored for left-handed play).
            _right = Resolve(root, held.Rig.Data.ArmRight);
            _left = Resolve(root, held.Rig.Data.ArmLeft);
            if (_left.Valid)
            {
                _fingers = FingerChains(_left.Hand);
                _fingerBase = new Quaternion[5][];
                _fingerWritten = new Quaternion[5][];
                for (int f = 0; f < 5; f++)
                {
                    int n = _fingers[f] != null ? _fingers[f].Length : 0;
                    _fingerBase[f] = new Quaternion[n];
                    _fingerWritten[f] = new Quaternion[n];
                }
            }
            MagHoldPose hold = held.Rig.Data.MagHold;
            if (hold != null && _left.Valid && hold.FingerPaths != null)
            {
                _magFingers = new Transform[hold.FingerPaths.Length];
                _magFingerLocal = hold.FingerLocal;
                for (int i = 0; i < _magFingers.Length; i++)
                {
                    _magFingers[i] = WeaponRig.Resolve(_left.Hand, hold.FingerPaths[i]);
                }
            }
        }

        /// <summary>Each finger's joints under the hand, thumb to little finger (the analysis's names and walk); null where none is found.</summary>
        private static Transform[][] FingerChains(Transform hand)
        {
            var chains = new Transform[5][];
            Transform[] under = hand.GetComponentsInChildren<Transform>(true);
            for (int f = 0; f < 5; f++)
            {
                Transform first = null;
                foreach (Transform t in under)
                {
                    if (t != hand && WeaponAnalyzer.FingerPatterns[f].IsMatch(t.name) && (t.parent == hand || !WeaponAnalyzer.FingerPatterns[f].IsMatch(t.parent.name)))
                    {
                        first = t;
                        break;
                    }
                }
                var joints = new System.Collections.Generic.List<Transform>();
                for (Transform j = first; j != null && joints.Count < 3;)
                {
                    joints.Add(j);
                    Transform next = null;
                    foreach (Transform c in j)
                    {
                        if (WeaponAnalyzer.FingerPatterns[f].IsMatch(c.name) || j.childCount == 1)
                        {
                            next = c;
                            break;
                        }
                    }
                    j = next;
                }
                // Two joints at least, or one with a child to aim it by: a bone's direction is to the next one.
                chains[f] = joints.Count >= 2 || joints.Count == 1 && joints[0].childCount > 0 ? joints.ToArray() : null;
            }
            return chains;
        }

        /// <summary>How far each joint bends in a full fist (degrees): the fingers' knuckle, middle and end joints, the thumb's.</summary>
        private static readonly float[] FingerFist = { 80f, 95f, 70f }, ThumbFist = { 0f, 45f, 45f };
        /// <summary>A hand at rest is never ruler-straight (the VR glove's rest curls, thumb to little finger).</summary>
        private static readonly float[] RestCurl = { 0.1f, 0.12f, 0.16f, 0.2f, 0.24f };

        /// <summary>
        /// The weapon's left hand off the gun: its fingers follow the controller as the VR glove's do (the trigger the index, the
        /// grip the others, or the controller's own finger tracking), instead of keeping the animation's grip of this gun's
        /// handguard in the air, a pose of its own for every weapon (user, 2026-10-29: "the hand pose of the left hand is used
        /// for the hand on the gun ... before i even touched the weapon and its custom for each weapon so my hand looks weird").
        /// Each finger bends in its own plane (the animation's curl gives it, else across the palm), each joint to its share of
        /// a fist, and the result eases into the animation's grip as the hand takes the gun (<paramref name="onGun"/>). The
        /// animation's pose is read fresh whenever the animator wrote one, so nothing compounds frame to frame.
        /// </summary>
        private void PoseFreeFingers(VRHand off, float onGun)
        {
            if (_fingers == null)
            {
                return;
            }
            Input.Hand side = off.Side;
            bool skeleton = Input.VRInput.HasSkeleton(side);
            float grip = Input.VRInput.GetGrip(side), trigger = Input.VRInput.GetTrigger(side);
            ArmChain c = _left.Chain;
            Vector3 fingerDir = _left.Hand.rotation * c.FingerDir, palm = -(_left.Hand.rotation * c.BackDir);
            var dir = new Vector3[3];
            var baseAngle = new float[3];
            for (int f = 0; f < 5; f++)
            {
                Transform[] j = _fingers[f];
                if (j == null)
                {
                    continue;
                }
                for (int s = 0; s < j.Length; s++)
                {
                    Quaternion now = j[s].localRotation;
                    if (!_fingersWritten || Quaternion.Angle(now, _fingerWritten[f][s]) > 0.05f)
                    {
                        _fingerBase[f][s] = now; // the animator's pose this frame (or the first one seen)
                    }
                    j[s].localRotation = _fingerBase[f][s];
                }
                if (onGun < 0.999f)
                {
                    float curl = skeleton ? Input.VRInput.GetCurl(side, f) : f == 0 ? grip * 0.6f : f == 1 ? trigger : grip;
                    curl = Mathf.Clamp01(Mathf.Max(curl, RestCurl[f]));
                    for (int s = 0; s < j.Length; s++)
                    {
                        Vector3 to = s + 1 < j.Length ? j[s + 1].position : j[s].childCount > 0 ? j[s].GetChild(0).position : j[s].position + (s > 0 ? dir[s - 1] : fingerDir) * 0.02f;
                        dir[s] = to - j[s].position;
                    }
                    // The bend plane: the animation's own curl, else across the palm; turned so a positive angle closes the finger.
                    Vector3 axis = j.Length >= 2 ? Vector3.Cross(dir[0], dir[1]) : Vector3.zero;
                    if (axis.sqrMagnitude < 1e-10f || Vector3.Angle(dir[0], dir[1]) < 8f)
                    {
                        axis = Vector3.Cross(dir[0], palm);
                    }
                    if (axis.sqrMagnitude < 1e-10f)
                    {
                        continue;
                    }
                    axis.Normalize();
                    if (Vector3.Dot(Vector3.Cross(axis, dir[0]), palm) < 0f && f != 0)
                    {
                        axis = -axis;
                    }
                    float[] fist = f == 0 ? ThumbFist : FingerFist;
                    for (int s = 0; s < j.Length; s++)
                    {
                        Vector3 incoming = s > 0 ? dir[s - 1] : Vector3.ProjectOnPlane(fingerDir, axis);
                        baseAngle[s] = Vector3.SignedAngle(Vector3.ProjectOnPlane(incoming, axis), Vector3.ProjectOnPlane(dir[s], axis), axis);
                    }
                    // Each joint by its difference from the animation's (a turn about the shared axis keeps the joints after it as
                    // they are relative to it). The thumb's root stays where the animation has it (it does not bend in that plane).
                    for (int s = 0; s < j.Length; s++)
                    {
                        if (f == 0 && s == 0)
                        {
                            continue;
                        }
                        j[s].rotation = Quaternion.AngleAxis(curl * fist[Mathf.Min(s, 2)] - baseAngle[s], axis) * j[s].rotation;
                    }
                    for (int s = 0; s < j.Length; s++)
                    {
                        j[s].localRotation = Quaternion.Slerp(j[s].localRotation, _fingerBase[f][s], onGun);
                    }
                }
                for (int s = 0; s < j.Length; s++)
                {
                    _fingerWritten[f][s] = j[s].localRotation;
                }
            }
            _fingersWritten = true;
        }

        private static Arm Resolve(Transform root, ArmChain chain)
        {
            var arm = new Arm { Chain = chain };
            if (chain != null)
            {
                arm.Root = WeaponRig.Resolve(root, chain.Root);
                arm.Upper = WeaponRig.Resolve(root, chain.Upper);
                arm.Lower = WeaponRig.Resolve(root, chain.Lower);
                arm.Hand = WeaponRig.Resolve(root, chain.Hand);
            }
            return arm;
        }

        /// <summary>After the weapon is placed and its parts posed, before rendering.</summary>
        public void Solve()
        {
            BodyZones zones = _sys.Zones;
            Vector3 neck = _sys.Rig.Head.position - Vector3.up * 0.12f - zones.TorsoForward * 0.08f;
            float side = Input.VRInput.GunHand == Input.Hand.Left ? -1f : 1f; // the gun arm's shoulder is on the gun hand's side
            Vector3 gunShoulder = neck - Vector3.up * 0.06f + zones.TorsoRight * (0.17f * side);
            Vector3 offShoulder = neck - Vector3.up * 0.06f - zones.TorsoRight * (0.17f * side);
            Vector3 down = Vector3.down;

            Stretched = false;
            if (_right.Valid)
            {
                // Gun hand: exactly where its animation holds the grip.
                RigidPose target = RigidPose.Of(_right.Hand);
                Vector3 pole = gunShoulder + down * 0.6f + zones.TorsoRight * (0.35f * side) - zones.TorsoForward * 0.2f;
                SolveArm(_right, gunShoulder, target, pole);
                GunHandError = Vector3.Distance(_right.Hand.position, target.position);
            }
            if (_left.Valid)
            {
                VRHand off = _sys.Rig.Off;
                // A magazine of this gun in the hand (its reload's hold: MagHoldPose) is not the gun: a two-handed mark left set must
                // not send the hand to the handguard while it holds the magazine.
                MagHoldPose hold = _held.Rig.Data.MagHold;
                MagazineProp mag = off.Role == HandRole.Magazine ? off.Held as MagazineProp : null;
                bool onMag = hold != null && mag != null && mag.Go != null && mag.Kind == PropKind.Magazine && mag.WeaponKey == _held.Rig.Data.Key;
                bool onGun = !onMag && (off.Role == HandRole.Foregrip || _held.TwoHanded || !off.Tracked);
                RigidPose grip = RigidPose.Of(_left.Hand); // the authored support grip
                // Free: the grasp follows the controller palm, settled onto a bolt, handle or cover it works.
                ArmChain c = _left.Chain;
                // Turned as the VR glove is (its tracked frame, the glove rotation setting included): fingers along its forward,
                // the back of the hand along its side (+x on the right hand, -x on the left: HandModel's palm faces the other
                // way). The back of the hand went along the palm frame's up, the top of the fist, and the weapon's own hand
                // was a quarter turn off whenever it was not on the gun (user, 2026-10-29: "the hand is turned wrong").
                RigidPose tracked = default, placed = default;
                if (off.Model != null)
                {
                    off.Model.PlacedFromTracked(out tracked, out placed);
                }
                Quaternion glove = off.Model != null ? tracked.rotation : off.Palm.rotation;
                Quaternion rot = Quaternion.LookRotation(glove * Vector3.forward, glove * new Vector3(off.Mirror, 0f, 0f))
                                 * Runtime.RigidPose.Conjugate(Quaternion.LookRotation(c.FingerDir, c.BackDir));
                // PalmPoint is in metres already (measured on the unscaled idle pose): scaled by the hand bone's scale again, a
                // rig built at 1/100 put its hand a palm's length off the controller (night bug hunt, 229).
                Vector3 palmPoint = rot * c.PalmPoint;
                Vector3 at = off.PalmCentre;
                if (off.Model != null)
                {
                    // Where the glove is placed off the tracked hand (holding a magazine on the well's line, kept out of the
                    // gun, carried with a heavy gun's lag): the arm goes there too; it held the hand where the hidden glove
                    // was not (bug hunt 251.7).
                    if (off.Role == HandRole.Magazine && off.Model.SnapPose.HasValue)
                    {
                        // The glove pinned on a magazine it pushed home (ReloadInteractions' seated hold): the arm goes where the glove
                        // is drawn, not to the controller (round-3 scout, 2026-10-28).
                        placed = off.Model.SnapPose.Value;
                        tracked = off.Model.TrackedGlovePose;
                    }
                    Quaternion turn = placed.rotation * Runtime.RigidPose.Conjugate(tracked.rotation);
                    at = placed.position + turn * (at - tracked.position);
                    rot = turn * rot;
                    palmPoint = rot * c.PalmPoint;
                }
                // Settled onto the part it works the way the VR glove is: eased (it jumped up to 5 cm when racking began
                // and ended).
                bool anchored = off.Model != null && off.Model.GraspAnchor.HasValue;
                // The point the glove grips with goes onto the part (the fist hollow, or the pinch on a bolt handle), not the
                // palm centre: the arm sat 4 cm off what the glove held.
                // This frame's grip point (the glove's is placed after the arm), eased with the glove's own time step. As far
                // as the glove goes onto a part it works (a racked bolt or handle holds the hand all the way, Boneworks style):
                // held at 5 cm the arm stayed with the real hand while the glove was on the handle.
                float reach = off.Role == HandRole.Foregrip ? 0.05f : off.Model.GraspReach;
                Vector3 anchorWant = anchored ? Vector3.ClampMagnitude(off.Model.GraspAnchor.Value - off.Model.GraspPointNow(), reach) : Vector3.zero;
                _anchorOffset = Vector3.Lerp(_anchorOffset, anchorWant, 1f - Mathf.Exp(-(anchored ? 30f : 18f) * Time.unscaledDeltaTime));
                at += _anchorOffset;
                var free = new RigidPose(at - palmPoint, rot);
                // A magazine of this gun in the hand: the hand on it as the gun's own reload holds it, its fingers too (the user,
                // 2026-10-29: the weapon's hand sat off the magazine with the handguard's grip). Eased on and off.
                if (onMag)
                {
                    _lastOnMag = RigidPose.Of(mag.Go.transform).Multiply(hold.HandInMag);
                }
                _onMagBlend = Mathf.MoveTowards(_onMagBlend, onMag ? 1f : 0f, Time.unscaledDeltaTime * 10f);
                float m = _onMagBlend * _onMagBlend * (3f - 2f * _onMagBlend);
                if (m > 0f)
                {
                    free = new RigidPose(Vector3.Lerp(free.position, _lastOnMag.position, m), Quaternion.Slerp(free.rotation, _lastOnMag.rotation, m));
                }
                // Taking hold of the handguard or letting go eases over a tenth of a second instead of snapping.
                float want = onGun ? 1f : 0f;
                _onGunBlend = _onGunBlend < 0f ? want : Mathf.MoveTowards(_onGunBlend, want, Time.unscaledDeltaTime * 10f);
                float e = _onGunBlend * _onGunBlend * (3f - 2f * _onGunBlend); // eased in and out
                RigidPose target = new RigidPose(Vector3.Lerp(free.position, grip.position, e), Quaternion.Slerp(free.rotation, grip.rotation, e));
                Vector3 pole = offShoulder + down * 0.6f - zones.TorsoRight * (0.35f * side) - zones.TorsoForward * 0.2f;
                SolveArm(_left, offShoulder, target, pole);
                OffHandError = Vector3.Distance(_left.Hand.position, target.position);
                PoseFreeFingers(off, e);
                if (m > 0f && _magFingers != null)
                {
                    for (int i = 0; i < _magFingers.Length; i++)
                    {
                        if (_magFingers[i] != null)
                        {
                            _magFingers[i].localRotation = Quaternion.Slerp(_magFingers[i].localRotation, _magFingerLocal[i], m);
                        }
                    }
                }
            }
        }

        private void SolveArm(Arm arm, Vector3 shoulder, RigidPose target, Vector3 pole)
        {
            float a = Vector3.Distance(arm.Upper.position, arm.Lower.position);
            float b = Vector3.Distance(arm.Lower.position, arm.Hand.position);
            if (a < 1e-3f || b < 1e-3f)
            {
                return;
            }
            // Re-root: move the arm's own root so the shoulder joint lands on the body's shoulder.
            Vector3 toTarget = target.position - shoulder;
            float reach = (a + b) * 0.995f;
            if (toTarget.magnitude > reach)
            {
                shoulder = target.position - toTarget.normalized * reach; // stretch: the shoulder follows the hand
                Stretched = true;
            }
            arm.Root.position += shoulder - arm.Upper.position;

            Vector3 s = arm.Upper.position;
            Vector3 t = target.position;
            float d = Mathf.Clamp(Vector3.Distance(s, t), Mathf.Abs(a - b) + 1e-3f, reach);
            Vector3 dir = (t - s).sqrMagnitude > 1e-8f ? (t - s).normalized : Vector3.forward;
            float x = (a * a - b * b + d * d) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, a * a - x * x));
            Vector3 bend = Vector3.ProjectOnPlane(pole - s, dir);
            bend = bend.sqrMagnitude > 1e-6f ? bend.normalized : Vector3.ProjectOnPlane(Vector3.down, dir).normalized;
            Vector3 elbow = s + dir * x + bend * h;

            arm.Upper.rotation = Quaternion.FromToRotation(arm.Lower.position - s, elbow - s) * arm.Upper.rotation;
            arm.Lower.rotation = Quaternion.FromToRotation(arm.Hand.position - arm.Lower.position, t - arm.Lower.position) * arm.Lower.rotation;
            arm.Hand.rotation = target.rotation;
        }
    }
}
