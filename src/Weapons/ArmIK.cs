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

        /// <summary>The weapon's left hand's fingers, posed from the controller while the hand is off the gun.</summary>
        private readonly ArmFingers _fingers;

        public bool Active => _right.Valid || _left.Valid;

        /// <summary>Self-test: the off hand's fingers' mean bend (degrees; -1 with none found).</summary>
        internal float OffFingerBend => _fingers != null ? _fingers.MeanBend() : -1f;

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
                _fingers = new ArmFingers(_left.Hand);
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
                // Off the gun the fingers follow the controller; on a magazine they keep the animation's pose as before, the reload's
                // hold on the joints it moves (the user: "the gun held and support hand held weapon positions need to stay (plus mag
                // being held)"; review, 2026-10-30: the joints the reload does not move took the controller's curl).
                _fingers?.Pose(off, _left.Hand, _left.Chain, Mathf.Max(e, m));
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
                _fingers?.Remember(); // (the magazine's turns are this frame's last write, not an animator's pose next frame)
            }
        }

        private void SolveArm(Arm arm, Vector3 shoulder, RigidPose target, Vector3 pole)
        {
            Stretched |= SolveArm(arm.Root, arm.Upper, arm.Lower, arm.Hand, shoulder, target, pole);
        }

        /// <summary>
        /// The off hand off the gun as the VR glove is placed: the glove's tracked frame turned onto the arm's hand bone (fingers
        /// along its forward, the back of the hand along its side), carried where the glove is drawn (kept out of a gun, a heavy
        /// gun's lag), the arm's palm point on the hand's palm. Also the empty-hand arms' (<see cref="EmptyArms"/>) for both hands.
        /// </summary>
        internal static RigidPose FreeTarget(VRHand hand, ArmChain c)
        {
            RigidPose tracked = default, placed = default;
            if (hand.Model != null)
            {
                hand.Model.PlacedFromTracked(out tracked, out placed);
            }
            Quaternion glove = hand.Model != null ? tracked.rotation : hand.Palm.rotation;
            Quaternion rot = Quaternion.LookRotation(glove * Vector3.forward, glove * new Vector3(hand.Mirror, 0f, 0f))
                             * RigidPose.Conjugate(Quaternion.LookRotation(c.FingerDir, c.BackDir));
            Vector3 at = hand.PalmCentre;
            if (hand.Model != null)
            {
                Quaternion turn = placed.rotation * RigidPose.Conjugate(tracked.rotation);
                at = placed.position + turn * (at - tracked.position);
                rot = turn * rot;
            }
            return new RigidPose(at - rot * c.PalmPoint, rot);
        }

        /// <summary>
        /// Two-bone IK: the arm's root moved so its shoulder joint lands on <paramref name="shoulder"/> (slid toward the hand when
        /// out of reach), the elbow bent toward <paramref name="pole"/>, the hand on <paramref name="target"/>. True when the
        /// shoulder had to slide.
        /// </summary>
        internal static bool SolveArm(Transform root, Transform upper, Transform lower, Transform hand, Vector3 shoulder, RigidPose target, Vector3 pole)
        {
            bool stretched = false;
            float a = Vector3.Distance(upper.position, lower.position);
            float b = Vector3.Distance(lower.position, hand.position);
            if (a < 1e-3f || b < 1e-3f)
            {
                return false;
            }
            // Re-root: move the arm's own root so the shoulder joint lands on the body's shoulder.
            Vector3 toTarget = target.position - shoulder;
            float reach = (a + b) * 0.995f;
            if (toTarget.magnitude > reach)
            {
                shoulder = target.position - toTarget.normalized * reach; // stretch: the shoulder follows the hand
                stretched = true;
            }
            root.position += shoulder - upper.position;

            Vector3 s = upper.position;
            Vector3 t = target.position;
            float d = Mathf.Clamp(Vector3.Distance(s, t), Mathf.Abs(a - b) + 1e-3f, reach);
            Vector3 dir = (t - s).sqrMagnitude > 1e-8f ? (t - s).normalized : Vector3.forward;
            float x = (a * a - b * b + d * d) / (2f * d);
            float h = Mathf.Sqrt(Mathf.Max(0f, a * a - x * x));
            Vector3 bend = Vector3.ProjectOnPlane(pole - s, dir);
            bend = bend.sqrMagnitude > 1e-6f ? bend.normalized : Vector3.ProjectOnPlane(Vector3.down, dir).normalized;
            Vector3 elbow = s + dir * x + bend * h;

            upper.rotation = Quaternion.FromToRotation(lower.position - s, elbow - s) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, t - lower.position) * lower.rotation;
            hand.rotation = target.rotation;
            return stretched;
        }
    }
}
