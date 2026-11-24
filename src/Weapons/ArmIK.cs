using System.Collections.Generic;
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
        private bool _saidMagHoldOff;
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
        /// <summary>How long the off hand has had no tracking (seconds).</summary>
        private float _offLostFor;

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
        /// <summary>The gun hand's fingers: only its index moves, with the trigger (<see cref="ArmFingers.Pull"/>).</summary>
        private readonly ArmFingers _gunFingers;

        public bool Active => _right.Valid || _left.Valid;

        /// <summary>
        /// The showcase's body: the bones of the weapon's own gun arm or off arm (false when the weapon draws no such arm). The body's
        /// upper arm goes to its elbow (the lower bone): its sleeve can end there (a pistol's arms are forearms only).
        /// </summary>
        internal bool Chain(bool gunArm, out Transform root, out Transform upper, out Transform lower, out Transform hand)
        {
            Arm a = gunArm ? _right : _left;
            root = a.Root;
            upper = a.Upper;
            lower = a.Lower;
            hand = a.Hand;
            return a.Valid;
        }

        /// <summary>Self-test: the off hand's fingers' mean bend toward its palm (degrees; -1 with none found).</summary>
        internal float OffFingerBend => _fingers != null && _left.Hand != null ? _fingers.MeanBend(-(_left.Hand.rotation * _left.Chain.BackDir)) : -1f;

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
            if (_right.Valid)
            {
                _gunFingers = new ArmFingers(_right.Hand);
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
        private RigidPose _solvedRight, _solvedLeft;
        private int _solvedFrame = -10;

        public void Solve()
        {
            SolveArms();
            // The hands as solved, kept for a copy taken before the next solve (a release at the top of the next BeforeRender read the
            // animator's own pose there, and the empty hands' off arm swept from the authored grip, up to 30 cm away; hands scout).
            if (_right.Valid && _left.Valid)
            {
                _solvedRight = RigidPose.Of(_right.Hand);
                _solvedLeft = RigidPose.Of(_left.Hand);
                _solvedFrame = Time.frameCount;
            }
        }

        private void SolveArms()
        {
            BodyZones zones = _sys.Zones;
            Vector3 neck = _sys.Rig.Head.position - Vector3.up * 0.12f - zones.TorsoForward * 0.08f;
            float side = Input.VRInput.GunHand == Input.Hand.Left ? -1f : 1f; // the gun arm's shoulder is on the gun hand's side
            Vector3 gunShoulder = neck - Vector3.up * 0.06f + zones.TorsoRight * (0.17f * side);
            Vector3 offShoulder = neck - Vector3.up * 0.06f - zones.TorsoRight * (0.17f * side);
            Vector3 down = Vector3.down;

            Stretched = false;
            bool whereTaken = _sys.HeldWhereTaken(_held.Weapon);
            if (whereTaken && !_keptTaken)
            {
                // The pass itself: the support arm's hand where it held the gun is kept, in the gun's frame.
                _keptArmInGun = _supportOnGun ? _lastSupportInGun : (RigidPose?)null;
            }
            _keptTaken = whereTaken;
            if (!whereTaken)
            {
                _keptArmInGun = null;
            }
            if (_right.Valid && _left.Valid && (whereTaken || Input.VRInput.GunHandSwapped))
            {
                SolveWhereTaken(neck, zones, down);
                return;
            }
            if (_right.Valid)
            {
                // Gun hand: exactly where its animation holds the grip (steadied: a frame's blink of it, or a pose off the grip altogether).
                RigidPose animated = RigidPose.Of(_right.Hand);
                bool onGrip = (animated.position - _held.Rig.ToWorld(_held.Rig.Data.GripPoint)).sqrMagnitude < 0.08f * 0.08f;
                RigidPose target = _gunGrip.SteadyEased(animated, _held.Rig.GunBodyPose, onGrip, GameReloading, Time.frameCount, Time.unscaledTime);
                SayHeldOff("gun hand", animated, target, ref _saidGunOff);
                Vector3 pole = gunShoulder + down * 0.6f + zones.TorsoRight * (0.35f * side) - zones.TorsoForward * 0.2f;
                SolveArm(_right, gunShoulder, target, pole);
                GunHandError = Vector3.Distance(_right.Hand.position, target.position);
                _gunFingers?.Pull(_right.Hand, _right.Chain, 1, Input.VRInput.GetTrigger(_sys.Rig.Dominant.Side));
            }
            if (_left.Valid)
            {
                VRHand off = _sys.Rig.Off;
                // A magazine of this gun in the hand (its reload's hold: MagHoldPose) is not the gun: a two-handed mark left set must
                // not send the hand to the handguard while it holds the magazine.
                MagHoldPose hold = _held.Rig.Data.MagHold;
                MagazineProp mag = off.Role == HandRole.Magazine ? off.Held as MagazineProp : null;
                // A magazine pushed home with the hand still on it (ReloadInteractions' seated hold): the hand stays on it as the reload
                // holds it, on the gun's own magazine now. Dropped there, the hand went to the glove's grip each time a magazine
                // seated, a jump of the hand on the magazine (user, 2026-11-03: "the hand positions on some mags are sometimes bugged").
                Transform seated = off.Role == HandRole.Magazine && ReferenceEquals(off.Held, ReloadInteractions.SeatedHold)
                                   && ReferenceEquals(_held.ActiveRig, _held.Rig) ? _held.Rig.Magazine : null;
                // (A made stand-in is not the part the hold was taken on: the hand would sit off it, turned as the seat is.)
                bool onMag = hold != null && (mag != null && mag.Go != null && mag.Kind == PropKind.Magazine && (mag.WeaponKey == _held.Rig.Data.Key || _held.Rig.Data.TakesMagazineOf(mag.Data)) && mag.OwnCopy
                                              || seated != null);
                // (Not tracked for a tenth of a second, not for one frame: a single lost frame started the hand toward the
                // handguard, 7% of the way, and back over the next six: a twitch of the arm at every dropout; scout, 2026-11-11.)
                _offLostFor = off.Tracked ? 0f : _offLostFor + Mathf.Min(Time.unscaledDeltaTime, 0.05f);
                bool onGun = !onMag && (off.Role == HandRole.Foregrip || _held.TwoHanded || _offLostFor > 0.1f);
                // The authored support grip, steadied (the same: its bone went 26 to 31 cm off the support point on the HK416-E/S and the
                // G BRN180 EO S, by the log of 2026-11-03).
                RigidPose animatedGrip = RigidPose.Of(_left.Hand);
                WeaponRigData data = _held.Rig.Data;
                bool onSupport = !data.HasSupportPoint || (animatedGrip.position - _held.Rig.ToWorld(data.SupportPoint)).sqrMagnitude < 0.14f * 0.14f;
                RigidPose grip = _supportGrip.SteadyEased(animatedGrip, _held.Rig.GunBodyPose, onSupport, GameReloading, Time.frameCount, Time.unscaledTime);
                // On a pump the support grip is the pump: it goes back with it. The weapon's hand stayed on the pump's place at rest
                // while the pump was worked (the user, 2026-11-14: "the hands (weapon hands) still dont move with the shotgun rack").
                if (data.CyclesByHand && data.HasSupportPoint && _held.SlideOffset > 0.0005f && data.SlideTravel.sqrMagnitude > 1e-8f
                    && Vector3.Distance(data.RackPoint, data.SupportPoint) < 0.22f && (off.Role == HandRole.Slide || off.Role == HandRole.Foregrip))
                {
                    grip = new RigidPose(grip.position + _held.Rig.DirToWorld(data.SlideTravel.normalized) * _held.SlideOffset, grip.rotation);
                }
                SayHeldOff("support hand", animatedGrip, grip, ref _saidSupportOff);
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
                if (mag != null && mag.Go != null && !onMag && !mag.OnRail)
                {
                    // A magazine its reload does not hold (no hold of the reload for this gun, the G17's and BRN180's, or a made stand-in):
                    // the tracked hand moved out of the gun as the magazine is, by the same amount in the same frame. From the glove's
                    // place, which eases after a magazine kept out of the gun (at up to 4 m/s), the weapon's hand slid after the magazine
                    // as it met the gun (user, 2026-11-03: "mags are sometimes trying to keep up with the speed of the weapons arms").
                    at += mag.KeptOut;
                }
                else if (off.Model != null)
                {
                    // Where the glove is placed off the tracked hand (holding a magazine on the well's line, kept out of the
                    // gun, carried with a heavy gun's lag): the arm goes there too; it held the hand where the hidden glove
                    // was not (bug hunt 251.7).
                    // (And a second gun in this hand: its glove sits on that gun's own grip pose, 4 cm off the tracked hand; the arm stayed on the
                    // tracked hand, the gun off its fingers with the weapon's arms drawn; arms scout, 2026-11-08.)
                    if (off.Role == HandRole.Magazine || off.Role == HandRole.Body || off.Role == HandRole.Free || off.Role == HandRole.Weapon)
                    {
                        // The glove pinned on a magazine it pushed home (ReloadInteractions' seated hold) or on a soldier it grabbed, the gun in
                        // the other hand: the arm goes where the glove is drawn, not to the controller (round-3 scout, 2026-10-28), eased in and
                        // out over the glove's 80 ms (taken at once it popped ahead of the hand, and back on the let-go; arms scout, 2026-11-06).
                        placed = off.Model.WithSnap(placed);
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
                // (Every ease here by a frame of 50 ms at most: on a slow frame, a load or a collection, the tenth-of-a-second
                // eases were over in that one frame and the arm jumped to the grip or off it; scout, 2026-11-11.)
                float easeDt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
                _anchorOffset = Vector3.Lerp(_anchorOffset, anchorWant, 1f - Mathf.Exp(-(anchored ? 30f : 18f) * easeDt));
                at += _anchorOffset;
                var free = new RigidPose(at - palmPoint, rot);
                // A magazine of this gun in the hand: the hand on it as the gun's own reload holds it, its fingers too (the user,
                // 2026-10-29: the weapon's hand sat off the magazine with the handguard's grip). Eased on and off.
                if (onMag)
                {
                    RigidPose onIt = RigidPose.Of(mag != null && mag.Go != null ? mag.Go.transform : seated).Multiply(hold.HandInMag);
                    if ((onIt.position - free.position).sqrMagnitude > 0.025f * 0.025f) // (seated too: the hand went through the 92g on its seated magazine; user)
                    {
                        // The reload's hold puts the hand off where the glove holds this magazine (the 92g's, its copy 18 cm across, the
                        // hold 3 cm off its part): the arm stays with the glove's hand, which holds it right (the user, 2026-11-14: "the 92g
                        // mag gets held weirdly and not in the hand, floats in the air a little", with the weapon's arms only).
                        if (!_saidMagHoldOff)
                        {
                            _saidMagHoldOff = true; // (once: the line's strings were made every frame the hand held it)
                            Log.InfoOnce("maghold-off:" + _held.Rig.Data.Key, $"{_held.Rig.Data.Key}: its reload's magazine hold is {(onIt.position - free.position).magnitude * 100f:0.0} cm off the glove's hold: the weapon's hand stays with the glove.");
                        }
                        onMag = false;
                    }
                    else
                    {
                        _lastOnMag = onIt;
                    }
                }
                _onMagBlend = Mathf.MoveTowards(_onMagBlend, onMag ? 1f : 0f, easeDt * 10f);
                float m = _onMagBlend * _onMagBlend * (3f - 2f * _onMagBlend);
                if (m > 0f)
                {
                    free = new RigidPose(Vector3.Lerp(free.position, _lastOnMag.position, m), Quaternion.Slerp(free.rotation, _lastOnMag.rotation, m));
                }
                // Taking hold of the handguard or letting go eases over a tenth of a second instead of snapping.
                float want = onGun ? 1f : 0f;
                _onGunBlend = _onGunBlend < 0f ? want : Mathf.MoveTowards(_onGunBlend, want, easeDt * 10f);
                float e = _onGunBlend * _onGunBlend * (3f - 2f * _onGunBlend); // eased in and out
                RigidPose target = new RigidPose(Vector3.Lerp(free.position, grip.position, e), Quaternion.Slerp(free.rotation, grip.rotation, e));
                // (Where the support arm's hand is on the gun, for a pass: see _keptArmInGun.)
                _supportOnGun = e > 0.5f;
                _keptLeftHanded = side < 0f;
                if (_supportOnGun)
                {
                    _lastSupportInGun = _held.Rig.GunBodyPose.Inverse().Multiply(target);
                }
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

        /// <summary>
        /// A gun passed to the other hand and held where that hand took it (WeaponSystem.HeldWhereTaken), or in the hand that is not the
        /// player's gun hand however it got there (drawn with it, say; the glove is mirrored onto the grip then): each arm stays on its own
        /// side, the model's left arm on the left controller and its right arm on the right, the one holding the gun where it holds it,
        /// the other free, both as the glove is placed (<see cref="FreeTarget"/>), the fingers from their controllers. As the gun arm, the
        /// model's right arm went from the left shoulder to the gun's own grip and its left arm to the right controller: the hands
        /// swapped over (user, 2026-11-03: "my hand turns and the left hand becomes the right hand then and it looks weird on weapons
        /// arms"). Taken by its grip again, the gun's right arm holds it as before.
        /// </summary>
        private void SolveWhereTaken(Vector3 neck, BodyZones zones, Vector3 down)
        {
            for (int i = 0; i < 2; i++)
            {
                bool leftSide = i == 0;
                Arm arm = leftSide ? _left : _right;
                VRHand hand = _sys.Rig.Hands[0].Side == (leftSide ? Input.Hand.Left : Input.Hand.Right) ? _sys.Rig.Hands[0] : _sys.Rig.Hands[1];
                float s = leftSide ? -1f : 1f;
                Vector3 shoulder = neck - Vector3.up * 0.06f + zones.TorsoRight * (0.17f * s);
                Vector3 pole = shoulder + down * 0.6f + zones.TorsoRight * (0.35f * s) - zones.TorsoForward * 0.2f;
                // (A magazine in it kept out of the gun: moved with it in the same frame, as in Solve.)
                MagazineProp mag = hand.Role == HandRole.Magazine ? hand.Held as MagazineProp : null;
                RigidPose target = FreeTarget(hand, arm.Chain, mag != null && mag.Go != null && !mag.OnRail ? mag.KeptOut : (Vector3?)null);
                // (Left-handed the support arm is the model's left arm on the right controller; there the right controller's arm keeps the gun.
                // Its bone is a right hand's, so only the hand's place is kept, turned as its glove is.)
                Arm keeper = _keptLeftHanded ? _right : _left;
                if (_keptArmInGun.HasValue && ReferenceEquals(arm, keeper) && hand.Role == HandRole.Weapon && _held.Rig.GunBody != null)
                {
                    // The support arm keeping the gun the trigger hand let go of: its hand stays where it held the gun (the animation's support
                    // grip), not moved onto the glove's pinned place, which is not where the weapon's arm had it (the user, 2026-11-24, weapon
                    // arms: "the hand that is not on the trigger, when let go the trigger finger/arm, the hand changes positions").
                    RigidPose kept = _held.Rig.GunBodyPose.Multiply(_keptArmInGun.Value);
                    target = _keptLeftHanded ? new RigidPose(kept.position, target.rotation) : kept;
                }
                SolveArm(arm, shoulder, target, pole);
                float error = Vector3.Distance(arm.Hand.position, target.position);
                if (hand == _sys.Rig.Dominant)
                {
                    GunHandError = error;
                }
                else
                {
                    OffHandError = error;
                }
                ArmFingers fingers = leftSide ? _fingers : _gunFingers;
                fingers?.Pose(hand, arm.Hand, arm.Chain, 0f);
            }
            _onGunBlend = 0f; // (taken by its grip again, the support hand eases onto the handguard from where it is)
            _onMagBlend = 0f;
        }

        /// <summary>Once per gun and hand: the gun's animation had the hand bone away from where it was held (the proof of a blink, for the log).</summary>
        private void SayHeldOff(string which, RigidPose animated, RigidPose held, ref bool said)
        {
            float off = said ? 0f : Vector3.Distance(animated.position, held.position);
            if (off > 0.03f)
            {
                said = true; // (once a hold: no string made each frame of it)
                Log.InfoOnce("armblink:" + which + _held.Rig.Data.Key, $"{_held.Rig.Data.Key}: the {which} held on the gun, its bone {off * 100f:0} cm away in the animation ({_held.AnimatorStateText}).");
            }
        }

        private bool _saidGunOff, _saidSupportOff;
        /// <summary>The support arm's hand on the gun at the last frame it held it (gun body frame), whether it did, and the pass's copy.</summary>
        private RigidPose _lastSupportInGun;
        private bool _supportOnGun, _keptTaken, _keptLeftHanded;
        private RigidPose? _keptArmInGun;

        /// <summary>The game's own reload is playing (the assisted reload): the hands go where its animation takes them, off the gun too.</summary>
        private bool GameReloading => _held.ActiveWeapon != null && _held.ActiveWeapon.reloading;

        /// <summary>The gun hand's and the support hand's poses as the gun's animation holds them, each steadied (<see cref="SteadyGrip"/>).</summary>
        private readonly SteadyGrip _gunGrip = new SteadyGrip(), _supportGrip = new SteadyGrip();

        /// <summary>
        /// A hand bone's pose as the gun's animation leaves it this frame, steadied against the gun: a jump of more than 3 cm or 20 degrees
        /// is held off for up to four frames and taken only if it lasts; and once a pose on the gun has been seen, one off it altogether
        /// (<c>onGun</c> false: as far off as the glove's own rule, HeldWeapon.SnapHands) is held off for as long as it lasts. Between two of
        /// the game's own animations (a shot's and the idle after it) a hand bone was left in another pose, up to 31 cm off the gun, and
        /// the weapon's arm went there and back (user, 2026-11-03: "the left arm moves away after shooting full auto or semi ... for a
        /// short milisecond but teleports back"). A gun whose bones never sit on its points keeps them as they are, and with
        /// <c>pass</c> (the game's own reload playing) every pose is taken as it comes.
        /// </summary>
        private sealed class SteadyGrip
        {
            private RigidPose _inGun;
            private bool _known, _seenOnGun;
            private int _odd;

            /// <summary>The steadied pose as it is decided, with no ease (the rule alone; the offline tests' entry).</summary>
            public RigidPose Steady(RigidPose now, RigidPose gun, bool onGun, bool pass)
            {
                return Decide(now, gun, onGun, pass, out RigidPose inGun) ? gun.Multiply(inGun) : now;
            }

            /// <summary>The steadied pose, what it gives out gone through over a tenth of a second where it changes at once
            /// (<paramref name="frame"/> and <paramref name="time"/>: Time.frameCount and Time.unscaledTime, handed in so the rule
            /// runs outside the game too: called from in here, the offline tests stopped at Unity's native clock, 2026-11-11).</summary>
            public RigidPose SteadyEased(RigidPose now, RigidPose gun, bool onGun, bool pass, int frame, float time)
            {
                if (!Decide(now, gun, onGun, pass, out RigidPose inGun))
                {
                    _hasOut = false;
                    _easeAt = -1f;
                    return now;
                }
                return Eased(inGun, gun, frame, time);
            }

            /// <summary>The pose to hold to in the gun's space; false with <paramref name="pass"/> (taken as it comes).</summary>
            private bool Decide(RigidPose now, RigidPose gun, bool onGun, bool pass, out RigidPose held)
            {
                held = default;
                if (pass)
                {
                    _known = false; // (the reload over, the next pose is taken as the one to hold to)
                    _odd = 0;
                    return false;
                }
                RigidPose inGun = gun.Inverse().Multiply(now);
                if (_seenOnGun && !onGun)
                {
                    held = _inGun;
                    return true;
                }
                if (_known && (Vector3.Distance(inGun.position, _inGun.position) > 0.03f || Quaternion.Angle(inGun.rotation, _inGun.rotation) > 20f) && ++_odd <= 4)
                {
                    held = _inGun;
                    return true;
                }
                _inGun = inGun;
                _known = true;
                _seenOnGun |= onGun;
                _odd = 0;
                held = inGun;
                return true;
            }

            // What it gives out never jumps: a pose held for four frames and then taken over, or held off the grip and let back
            // onto it, moved the hand by the whole difference in one frame (up to 31 cm by the comment above; scout, 2026-11-11:
            // "frozen for 4 frames, then teleports"). A change of more than 1.5 cm or 10 degrees from the frame before is gone
            // through over a tenth of a second, in the gun's own space.
            private RigidPose _out, _easeFrom;
            private bool _hasOut;
            private int _outFrame;
            private float _easeAt = -1f;

            private RigidPose Eased(RigidPose inGun, RigidPose gun, int frame, float time)
            {
                RigidPose r = inGun;
                if (_hasOut && frame - _outFrame <= 2)
                {
                    if (_easeAt < 0f && (Vector3.Distance(r.position, _out.position) > 0.015f || Quaternion.Angle(r.rotation, _out.rotation) > 10f))
                    {
                        _easeFrom = _out;
                        _easeAt = time;
                    }
                    if (_easeAt >= 0f)
                    {
                        float t = (time - _easeAt) / 0.1f;
                        if (t >= 1f)
                        {
                            _easeAt = -1f;
                        }
                        else
                        {
                            float k = t * t * (3f - 2f * t);
                            // (No turn between the two: none worked out. Slerp is one of Unity's native calls, and the rule runs outside the game too.)
                            bool turned = Mathf.Abs(Quaternion.Dot(_easeFrom.rotation, r.rotation)) < 0.999999f;
                            r = new RigidPose(Vector3.Lerp(_easeFrom.position, r.position, k), turned ? Quaternion.Slerp(_easeFrom.rotation, r.rotation, k) : r.rotation);
                        }
                    }
                }
                else
                {
                    _easeAt = -1f;
                }
                _out = r;
                _outFrame = frame;
                _hasOut = true;
                return gun.Multiply(r);
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
        /// With <paramref name="keptOut"/>: the tracked hand moved by it, not where the glove has eased to.
        /// </summary>
        internal static RigidPose FreeTarget(VRHand hand, ArmChain c, Vector3? keptOut = null)
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
            if (keptOut.HasValue)
            {
                at += keptOut.Value;
            }
            else if (hand.Model != null)
            {
                if (hand.Role == HandRole.Body || hand.Role == HandRole.Weapon)
                {
                    placed = hand.Model.WithSnap(placed); // (on a soldier it grabbed, a passed gun in the other hand; a second gun on its own grip)
                }
                Quaternion turn = placed.rotation * RigidPose.Conjugate(tracked.rotation);
                at = placed.position + turn * (at - tracked.position);
                rot = turn * rot;
            }
            return new RigidPose(at - rot * c.PalmPoint, rot);
        }

        // The side an elbow bends to turns no faster than 600 degrees a second. It is the pole's direction seen across the line from
        // shoulder to hand: where the hand's direction passes the pole's (down, a little out and back: a hand hanging at the hip, where
        // the magazine pouch is), that side swings half a turn within a few degrees of the hand's way, and the elbow whipped 35 cm
        // across in one frame ("parts of the hand ... glitching around", user 2026-11-11; scout). Kept against the rig, so a turn
        // of the player is not held back.
        /// <summary>Where the two hands of the gun's arms are drawn now (the gun hand's, the off hand's), for the empty-hand arms to start from.</summary>
        internal bool HandsNow(out RigidPose gunHand, out RigidPose offHand)
        {
            if (Time.frameCount - _solvedFrame <= 1)
            {
                gunHand = _solvedRight;
                offHand = _solvedLeft;
                return true;
            }
            gunHand = _right.Valid ? RigidPose.Of(_right.Hand) : default;
            offHand = _left.Valid ? RigidPose.Of(_left.Hand) : default;
            return _right.Valid && _left.Valid;
        }

        private sealed class BendMemory
        {
            public Vector3 InRig;
            public int Frame;
        }

        private static readonly Dictionary<Transform, BendMemory> Bends = new Dictionary<Transform, BendMemory>();
        private static readonly List<Transform> BendGone = new List<Transform>();

        private static Vector3 SteadyBend(Transform arm, Vector3 bend, Vector3 dir)
        {
            Transform rig = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Root : null;
            if (arm == null || rig == null)
            {
                return bend;
            }
            if (!Bends.TryGetValue(arm, out BendMemory memory))
            {
                if (Bends.Count > 24)
                {
                    // (Arms of guns put away long ago: only those gone. Cleared whole, the arm solved just before lost its memory, and its
                    // elbow was free for a frame; arms scout.)
                    BendGone.Clear();
                    foreach (Transform t in Bends.Keys)
                    {
                        if (t == null)
                        {
                            BendGone.Add(t);
                        }
                    }
                    foreach (Transform t in BendGone)
                    {
                        Bends.Remove(t);
                    }
                    if (Bends.Count > 48)
                    {
                        Bends.Clear(); // (many alive at once, a rare case: as before)
                    }
                }
                Bends[arm] = memory = new BendMemory { Frame = -10 };
            }
            if (Time.frameCount - memory.Frame <= 2)
            {
                Vector3 before = Vector3.ProjectOnPlane(rig.TransformDirection(memory.InRig), dir);
                if (before.sqrMagnitude > 1e-4f)
                {
                    before.Normalize();
                    float most = 600f * Mathf.Min(Time.unscaledDeltaTime, 0.05f);
                    if (Vector3.Angle(before, bend) > most)
                    {
                        // (Turned about the arm's own line: two opposite directions have no shortest turn of their own.)
                        float signed = Vector3.SignedAngle(before, bend, dir);
                        Vector3 paced = Quaternion.AngleAxis(Mathf.Sign(signed) * most, dir) * before;
                        paced = Vector3.ProjectOnPlane(paced, dir);
                        if (paced.sqrMagnitude > 1e-6f)
                        {
                            bend = paced.normalized;
                        }
                    }
                }
            }
            memory.InRig = rig.InverseTransformDirection(bend);
            memory.Frame = Time.frameCount;
            return bend;
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
            bend = SteadyBend(upper, bend, dir);
            Vector3 elbow = s + dir * x + bend * h;

            upper.rotation = Quaternion.FromToRotation(lower.position - s, elbow - s) * upper.rotation;
            lower.rotation = Quaternion.FromToRotation(hand.position - lower.position, t - lower.position) * lower.rotation;
            hand.rotation = target.rotation;
            return stretched;
        }
    }
}
