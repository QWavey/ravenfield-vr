using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // ReloadInteractions, part: the parts a reload opens and lays: break-open barrels and cylinders, the feed cover,
    // switchable sights, the belt. The class, grabbing and the frame's visuals: ReloadInteractions.cs; the self-checks:
    // ReloadInteractions.SelfChecks.cs.
    public sealed partial class ReloadInteractions
    {
        // ---- break-open (break-action barrels, revolver cylinder) ----------------------------------

        private void SetBreakOpen(bool open)
        {
            if (Ammo.Open == open)
            {
                return;
            }
            Ammo.SetOpen(open);
            _openedAt = Time.unscaledTime;
            VRHand dom = _sys.Rig.Dominant;
            if (open)
            {
                _held.Sounds.Play(MechSound.SlideBack, SeatPose.position);
                // The cases fired since it was last opened fall out (opened again, nothing more falls). A break-action shotgun's
                // live shells come out with them, back into the reserve: they stayed in, and the gun could not be loaded one
                // by one (user, 2026-10-22). A revolver's live rounds stay in its cylinder.
                int spent = Ammo.TakeSpentCases();
                int live = Data.Hinge == HingeKind.BreakAction ? Ammo.TakeLiveShells() : 0;
                if (spent > 0)
                {
                    Casings.Drop(_held, spent, SeatPose.position);
                }
                if (live > 0)
                {
                    Casings.Drop(_held, live, SeatPose.position, live: true);
                }
                dom.Haptic(0.04f, 90f, 0.6f);
            }
            else
            {
                _held.Sounds.Play(MechSound.SlideForward, SeatPose.position);
                _held.Nudge(Rig.UpWorld * 0.08f, -8f); // snapped shut with a flick
                dom.Haptic(0.05f, 110f, 0.75f);
            }
        }

        // ---- feed cover ------------------------------------------------------------------------------

        private Vector3 CoverGrabWorld => Rig.ToWorld(HingeRotate(new RigidPose(Data.HingeGrabPoint, Quaternion.identity), _held.HingeOpenness).position);

        /// <summary>
        /// Distance to where a feed cover is taken: along its rear half, from its latch forward toward the hinge (a point at
        /// the latch alone missed a hand on the cover's top a little further forward: the QJB's covers "could not be
        /// opened"; user, 2026-10-16). Barrels are taken at their one point.
        /// </summary>
        private float CoverZoneDistance(Vector3 palm)
        {
            Vector3 a = CoverGrabWorld;
            if (Data.Hinge != HingeKind.FeedCover)
            {
                return Vector3.Distance(palm, a);
            }
            float toHinge = Vector3.Dot(Data.HingePivot - Data.HingeGrabPoint, Data.AimAxis);
            Vector3 forward = Data.HingeGrabPoint + Data.AimAxis * (toHinge * 0.6f);
            Vector3 b = Rig.ToWorld(HingeRotate(new RigidPose(forward, Quaternion.identity), _held.HingeOpenness).position);
            Vector3 ab = b - a;
            float t = ab.sqrMagnitude > 1e-8f ? Mathf.Clamp01(Vector3.Dot(palm - a, ab) / ab.sqrMagnitude) : 0f;
            return Vector3.Distance(palm, a + ab * t);
        }

        /// <summary>How far a feed cover must be lifted (share of its swing) to stay open: past half way it falls open.</summary>
        private const float CoverStaysOpen = 0.5f;

        private float CoverAngle(Vector3 palmWorld)
        {
            Vector3 axis = Data.HingeAxis;
            Vector3 rest = Vector3.ProjectOnPlane(Data.HingeGrabPoint - Data.HingePivot, axis);
            Vector3 hand = Vector3.ProjectOnPlane(Rig.ToGun(palmWorld) - Data.HingePivot, axis);
            return Vector3.SignedAngle(rest, hand, axis);
        }

        private void BeginCover(VRHand off)
        {
            _coverHeld = true;
            _coverGrabAngle = CoverAngle(off.Palm.position);
            _coverGrabOpenness = _held.HingeOpenness;
            off.Role = HandRole.Hinge;
            off.Haptic(0.02f, 150f, 0.4f);
        }

        /// <summary>The cover follows the hand around its hinge: lift to open, push down to close over the belt.</summary>
        private void UpdateCover(VRHand off)
        {
            if (!_coverHeld)
            {
                off.Role = HandRole.Free;
                return;
            }
            float delta = Mathf.DeltaAngle(_coverGrabAngle, CoverAngle(off.Palm.position));
            float openness = Mathf.Clamp01(_coverGrabOpenness + delta / Mathf.Max(10f, Data.HingeOpenAngle));
            _held.HingeOpenness = openness;
            if (Breaks)
            {
                // Barrels: pushed down open (the spent cases drop), lifted shut.
                if (!Ammo.Open && openness > 0.8f)
                {
                    SetBreakOpen(true);
                }
                else if (Ammo.Open && openness < 0.06f)
                {
                    SetBreakOpen(false);
                }
                if (!off.GrabHeld || Vector3.Distance(off.Palm.position, CoverGrabWorld) > 0.35f)
                {
                    EndCover(off);
                }
                return;
            }
            // Past half way it is open and falls the rest of the way when let go: at 85% of a swing of up to 110 degrees the
            // hand had to sweep 94 degrees round the hinge, and the QJB's covers seemed not to open at all (user, 2026-10-16).
            if (!Ammo.CoverOpen && openness > CoverStaysOpen)
            {
                Ammo.SetCoverOpen(true);
                _held.Sounds.Play(MechSound.SlideBack, CoverGrabWorld);
                off.Haptic(0.03f, 120f, 0.5f);
            }
            else if (Ammo.CoverOpen && openness < 0.06f)
            {
                Ammo.SetCoverOpen(false);
                _held.Nudge(-Rig.UpWorld * 0.06f, 6f); // the cover pressed shut
                _held.Sounds.Play(MechSound.SlideForward, CoverGrabWorld);
                off.Haptic(0.05f, 110f, 0.75f);
                _sys.Rig.Dominant.Haptic(0.04f, 110f, 0.5f);
            }
            if (!off.GrabHeld || CoverZoneDistance(off.Palm.position) > 0.35f)
            {
                EndCover(off);
            }
        }

        private float _coverLetGoAt = -10f;

        private void EndCover(VRHand off)
        {
            _coverHeld = false;
            _coverLetGoAt = Time.unscaledTime;
            if (off.Role == HandRole.Hinge)
            {
                off.Role = HandRole.Free;
            }
            // Let go part way: it falls open or drops shut.
            if (Breaks)
            {
                return; // barrels settle where they were left: open if opened, shut otherwise
            }
            if (Ammo.CoverOpen && _held.HingeOpenness < 0.35f)
            {
                Ammo.SetCoverOpen(false);
                _held.Sounds.Play(MechSound.SlideForward, CoverGrabWorld);
            }
        }

        // ---- switchable sights (a flip magnifier behind a red dot, offset sights) ------------------------

        private bool _opticHeld;
        private bool _opticSwitched;
        private float _opticGrabTime;
        private Vector3 _opticGrabGun;
        private bool _offLostTracking; // the off hand was out of tracking (the hold kept); its first tracked frame re-bases holds

        /// <summary>The weapon has more than one sight to choose from: the game's sight modes or a mod's magnifier key.</summary>
        private bool HasSwitchableSight
        {
            get
            {
                Weapon w = _held.Weapon;
                if (w == null)
                {
                    return false;
                }
                // The script keys asked twice a second, as the support keys are: ScriptKeys.Find makes and sorts a list, and a
                // grab that took nothing asked it every frame of its quarter-second reach (gameplay workflow, 2026-10-24); the
                // magnifier grip's test asks it too (review, 2026-10-24).
                // A magnifier the free hand flips by its own grip (MagnifierFlip) has one switch: that grip. This optic grab
                // would also flip it on a quick tap at release, which the magnifier grip must never do. Read live: its part
                // shows and hides as the lens flips (cached, a stale answer let a release tap the key; review, 2026-10-24).
                if (_held.MagnifierByHand(w))
                {
                    return false;
                }
                if (!ReferenceEquals(_magnifierKeyFor, w) || Time.unscaledTime - _magnifierKeyAt > 0.5f)
                {
                    _magnifierKeyFor = w;
                    _magnifierKeyAt = Time.unscaledTime;
                    _magnifierKey = Input.ScriptKeys.Find(w, Input.ScriptKeyPurpose.Magnifier) != null;
                }
                return w.HasAnyAltSightModes() || _magnifierKey;
            }
        }

        private Weapon _magnifierKeyFor;
        private float _magnifierKeyAt = -10f;
        private bool _magnifierKey;

        /// <summary>
        /// The optics sit on the sight line over the gun itself: from just behind the grip to a little short of the muzzle
        /// (on a pistol the eye point is at arm's length behind the gun, so a zone measured from the eye would float in
        /// the air in front of your face).
        /// </summary>
        private void OpticSegment(out Vector3 a, out Vector3 b)
        {
            WeaponRigData d = GripData;
            Vector3 eye = d.HasEyePoint ? d.EyePoint : d.GripPoint + d.Up * 0.07f;
            Vector3 dir = d.HasEyePoint ? d.EyeDirection : d.AimAxis;
            float grip = Vector3.Dot(d.GripPoint - eye, dir);
            float muzzle = Vector3.Dot(d.MuzzlePoint - eye, dir);
            float start = grip - 0.05f;
            float end = Mathf.Max(start + 0.04f, Mathf.Min(grip + 0.3f, muzzle - 0.04f));
            a = GripRig.ToWorld(eye + dir * start);
            b = GripRig.ToWorld(eye + dir * end);
        }

        private float OpticDistance(Vector3 palm)
        {
            OpticSegment(out Vector3 a, out Vector3 b);
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(palm - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return Vector3.Distance(palm, a + ab * t);
        }

        private Vector3 OpticGrabWorld
        {
            get
            {
                OpticSegment(out Vector3 a, out Vector3 b);
                return Vector3.Lerp(a, b, 0.4f);
            }
        }

        private void BeginOptic(VRHand off)
        {
            _opticHeld = true;
            _opticSwitched = false;
            _opticGrabTime = Time.unscaledTime;
            _opticGrabGun = GripRig.ToGun(off.Palm.position);
            off.Role = HandRole.Optic;
            off.Haptic(0.015f, 180f, 0.3f);
        }

        /// <summary>
        /// Swinging the optic aside (a flip mount turned out or in) switches the sight once per grab; a quick tap on it
        /// does the same. The game's sight mode then moves the model and the aim line follows.
        /// </summary>
        private void UpdateOptic(VRHand off)
        {
            if (!_opticHeld)
            {
                off.Role = HandRole.Free;
                return;
            }
            Vector3 moved = GripRig.ToGun(off.Palm.position) - _opticGrabGun;
            float sideways = Vector3.ProjectOnPlane(moved, GripData.AimAxis).magnitude;
            if (!_opticSwitched && sideways > 0.035f)
            {
                _opticSwitched = true;
                SwitchSight(off);
            }
            if (!off.GrabHeld)
            {
                if (!_opticSwitched && Time.unscaledTime - _opticGrabTime < 0.45f && moved.magnitude < 0.03f)
                {
                    SwitchSight(off); // a tap
                }
                EndOptic(off);
            }
            else if (moved.magnitude > 0.2f)
            {
                EndOptic(off);
            }
        }

        private void EndOptic(VRHand off)
        {
            _opticHeld = false;
            if (off.Role == HandRole.Optic)
            {
                off.Role = HandRole.Free;
            }
        }

        private void SwitchSight(VRHand off)
        {
            Weapon w = _held.Weapon;
            Input.ScriptKey key = Input.ScriptKeys.Find(w, Input.ScriptKeyPurpose.Magnifier);
            if (key != null)
            {
                Input.ScriptKeys.Tap(key.Key, key.Script);
            }
            else if (!w.NextSightMode())
            {
                return;
            }
            _held.Sounds.Play(MechSound.DryFire, OpticGrabWorld, 0.5f);
            off.Haptic(0.03f, 160f, 0.55f);
            SightLine now = _held.CurrentSight;
            if (now != null && now.Name.Length > 0)
            {
                _held.FlashCounter(now.Name.ToUpperInvariant());
            }
        }

        // ---- belt (machine guns with a feed cover) ------------------------------------------------------

        private bool _beltHeld;
        private BeltStrip _belt;
        /// <summary>The belt slipped from the hand (pulled out to its length): it hangs from the box mouth until taken again.</summary>
        private bool _beltHanging;
        /// <summary>The belt was laid by hand while the cover was open: it shows until the cover is lifted again (274).</summary>
        private bool _beltLaidThisLift;
        /// <summary>The lift's flags were taken from the ammunition's state since the gun was drawn (see the belt's update).</summary>
        private bool _beltStateTaken;
        /// <summary>The cover was open at the last look, so a lift is seen as the moment it goes up.</summary>
        private bool _beltCoverWasOpen;
        /// <summary>How far the belt end comes out of the box (about 28 rounds).</summary>
        private const float BeltReach = 0.32f;

        /// <summary>
        /// How far from the box mouth the belt stays in the hand, and the held strip is drawn to: the same length, so the strip
        /// always reaches the fingers until it slips (it was drawn to 32 cm and held to 38 cm: for the last 6 cm the hand held
        /// nothing visible and the strip's end hung in the air; sweep round 2).
        /// </summary>
        private const float BeltHeldReach = BeltReach + 0.06f;

        /// <summary>Where the held strip's end was (in the gun's frame) when it was laid, eased onto the tray from there.</summary>
        private Vector3 _beltLaidFrom;
        /// <summary>When the belt was laid (Time.time), for that ease; -1 for none.</summary>
        private float _beltLaidAt = -1f;

        /// <summary>
        /// Where the end of a belt hanging from the box mouth is: out over the box's side (the gun's side the mouth is on), then
        /// down beside it. Straight down from the mouth on the box's top, the belt hung through the box itself, each round half in
        /// its wall, and a hand reaching for it took the box (user, 2026-11-03: "Magbelts from MGs are INSIDE the mag ... its not
        /// grabbable").
        /// </summary>
        private Vector3 BeltHangEnd => BeltSideExit + Vector3.down * (BeltReach * 0.65f) + Rig.AimWorld * 0.03f;

        /// <summary>
        /// Out over the box's side from the belt's mouth, at the mouth's height: where a belt hanging, or held below the box, turns
        /// down (the curve's control point, and the hanging belt's grab line goes through it).
        /// </summary>
        private Vector3 BeltSideExit
        {
            get
            {
                Vector3 mouth = BeltStartWorld;
                Bounds b = Data.MagBounds;
                // Across the gun, toward the side of the box the mouth is on (the left where it is in the middle: most feed trays).
                Vector3 across = Rig.DirToWorld(Vector3.Cross(Data.AimAxis, Data.Up));
                Vector3 local = SeatPose.InverseTransformPoint(mouth) - b.center;
                Vector3 acrossLocal = SeatPose.InverseTransformDirection(across);
                if (Vector3.Dot(local, acrossLocal) < -0.005f)
                {
                    across = -across;
                    acrossLocal = -acrossLocal;
                }
                // Past the box's side along it (its half width that way, less where the mouth already is) and 3 cm more.
                float half = Mathf.Abs(acrossLocal.x) * b.extents.x + Mathf.Abs(acrossLocal.y) * b.extents.y + Mathf.Abs(acrossLocal.z) * b.extents.z;
                float clear = Mathf.Max(0f, half - Vector3.Dot(local, acrossLocal)) + 0.03f;
                return mouth + across * clear;
            }
        }

        /// <summary>How far outside the box's face the belt starts and its rounds are kept (a round is 12 mm across).</summary>
        private const float BeltClear = 0.008f;

        /// <summary>
        /// Where the belt leaves the seated box: its feed mouth, the point of the insertion face nearest the feed tray (the belt
        /// runs from there onto the tray). Before, it was b.center + insert * extent: the point on the box's centre line at the
        /// insertion depth, which for a box inserted at an angle lies off the box. On the MG3 (measured in the export) it was
        /// 2.9 cm past the box's back face and 9 cm from the gun-side edge of its top (274: "the belt comes out of the box in
        /// the middle and not the box opening").
        /// </summary>
        private Vector3 BeltStartWorld
        {
            get
            {
                Quaternion seatInv = Runtime.RigidPose.Conjugate(Data.MagSeat.rotation);
                Vector3 insertLocal = (seatInv * -Data.MagEjectAxis).normalized;
                Bounds b = Data.MagBounds;
                // The insertion face is the one the box is pushed through: the axis its insert direction has most of. Only that axis
                // is pinned to the box's face; the others run free across it, clamped to the box, so the belt starts at the point of
                // that face nearest the tray (taken in the seat's rest frame, where the box's bounds are). Pinning every axis the
                // insert direction has at all put the MK46's mouth at the far end of its 58 cm box (a 2-degree slant on y), 48 cm
                // from the tray (274).
                Vector3 tray = Data.MagSeat.InverseTransformPoint(Rig.ToGun(FeedTrayWorld));
                int lead = 0;
                for (int axis = 1; axis < 3; axis++)
                {
                    if (Mathf.Abs(insertLocal[axis]) > Mathf.Abs(insertLocal[lead]))
                    {
                        lead = axis;
                    }
                }
                // Just outside that face (on it, the first round sat half inside the box's wall).
                Vector3 mouth = b.center;
                for (int axis = 0; axis < 3; axis++)
                {
                    mouth[axis] = axis == lead
                        ? b.center[axis] + Mathf.Sign(insertLocal[axis]) * (b.extents[axis] + BeltClear)
                        : Mathf.Clamp(tray[axis], b.center[axis] - b.extents[axis], b.center[axis] + b.extents[axis]);
                }
                return SeatPose.TransformPoint(mouth);
            }
        }

        /// <summary>The feed tray: the model's own belt's feed where it has one (<see cref="HeldWeapon.BeltFeedGun"/>), else under the closed cover, between its hinge and its latch.</summary>
        private Vector3 FeedTrayWorld => _held.BeltFeedGun.HasValue && ReferenceEquals(Rig, _held.Rig) ? Rig.ToWorld(_held.BeltFeedGun.Value)
            : Rig.ToWorld(Vector3.Lerp(Data.HingePivot, Data.HingeGrabPoint, 0.5f) - Data.Up * 0.012f);

        private void BeginBelt(VRHand off)
        {
            _beltHeld = true;
            _beltHanging = false;
            off.Role = HandRole.Belt;
            if (_belt == null)
            {
                _belt = new BeltStrip();
            }
            _belt.Show(true);
            Log.InfoOnce("beltmouth:" + Data.Key, $"{Data.Key}: the belt leaves its box at the feed mouth, gun space (cm) {(Rig.ToGun(BeltStartWorld) * 100f).ToString("0.0")}.");
            _held.Sounds.Play(MechSound.ShellIn, BeltStartWorld, 0.5f);
            off.Haptic(0.02f, 160f, 0.35f);
        }

        /// <summary>The belt end follows the hand; brought onto the feed tray it drops into place.</summary>
        private void UpdateBelt(VRHand off)
        {
            if (!_beltHeld || !Ammo.MagInserted || !Ammo.CoverOpen || Ammo.BeltLaid)
            {
                EndBelt(off);
                return;
            }
            if (!off.GrabHeld)
            {
                // Let go short of the tray: it hangs from the box, still there to take (it vanished; 255.I3-1).
                LetBeltHang(off);
                return;
            }
            if (Vector3.Distance(off.Palm.position + off.Palm.Forward * 0.02f, BeltStartWorld) > BeltHeldReach)
            {
                // Pulled out to its length: it slips from the fingers and hangs from the box, still there to take again.
                LetBeltHang(off);
                off.Haptic(0.03f, 90f, 0.4f);
                return;
            }
            // The belt's end (just ahead of the fingers) onto the feed tray, taken from 7 cm (the palm within 4.5 cm of one
            // point was fiddly with the gun in the other hand); a tick as the end comes near, so the tray is found by feel.
            float toTray = Vector3.Distance(off.Palm.position + off.Palm.Forward * 0.02f, FeedTrayWorld);
            if (toTray < 0.07f)
            {
                CompleteBelt(off);
            }
            else if (toTray < 0.12f && !_beltNearTicked)
            {
                _beltNearTicked = true;
                off.Haptic(0.015f, 200f, 0.3f);
            }
            else if (toTray > 0.16f)
            {
                _beltNearTicked = false;
            }
        }

        private bool _beltNearTicked;

        private void CompleteBelt(VRHand off)
        {
            if (Ammo.LayBelt())
            {
                _beltLaidThisLift = true;
                // The end eases onto the tray from where it was drawn (taken from 7 cm, it jumped there in one frame).
                _beltLaidFrom = Rig.ToGun(BeltStartWorld + Vector3.ClampMagnitude(off.Palm.position + off.Palm.Forward * 0.02f - BeltStartWorld, BeltHeldReach));
                _beltLaidAt = Time.unscaledTime;
                _held.Sounds.Play(MechSound.ShellIn, FeedTrayWorld);
                off.Haptic(0.04f, 120f, 0.6f);
                _sys.Rig.Dominant.Haptic(0.03f, 120f, 0.4f);
            }
            EndBelt(off);
        }

        /// <summary>The belt slips from the hand and hangs from the box, still there to take again.</summary>
        private void LetBeltHang(VRHand off)
        {
            EndBelt(off);
            _beltHanging = true;
            _belt?.Show(true);
        }

        private void EndBelt(VRHand off)
        {
            _beltHeld = false;
            if (off.Role == HandRole.Belt)
            {
                off.Role = HandRole.Free;
            }
            _belt?.Show(false);
        }

        /// <summary>
        /// The belt strip: from the box mouth to the hand that pulls it (never longer than the belt comes out), hanging from
        /// the box after it slipped from the hand, or lying on the feed tray once laid on a gun without a belt in its model.
        /// </summary>
        private void UpdateBeltStrip(VRHand off)
        {
            // A belt still held goes with its box: out, the cover shut or the belt laid, it is let go here whatever the hand's role
            // says. Only UpdateBelt let it go, and that runs only while the hand's role is Belt, so a belt held when the box came
            // out (the role since reset, or the hand not tracked) stayed drawn after its box (274: "the old belt is still there").
            if (_beltHeld && (!Ammo.MagInserted || !Ammo.HasCover || !Ammo.CoverOpen || Ammo.BeltLaid))
            {
                Log.InfoOnce("beltheld-dropped:" + Data.Key, $"{Data.Key}: the belt held by the hand was let go with its box (box in {Ammo.MagInserted}, cover open {Ammo.CoverOpen}, belt laid {Ammo.BeltLaid}).");
                EndBelt(off);
            }
            if (!_beltStateTaken)
            {
                // Drawn again (or a sub weapon switched in place): the lift as the state has it. Lifting the cover takes a laid belt
                // off, so a belt laid with the cover open was laid in this lift. Starting from a shut cover, the open one read as
                // just lifted and a belt laid before the gun was holstered vanished until the cover was shut (bug hunt, 2026-10-22).
                _beltStateTaken = true;
                _beltCoverWasOpen = Ammo.CoverOpen;
                _beltLaidThisLift = Ammo.CoverOpen && Ammo.BeltLaid && Ammo.MagInserted;
            }
            // Lifting the cover takes a belt laid on the tray off it with its box: the old belt stayed when the cover was opened
            // (274, the user). A belt laid by hand with the cover open is shown until the cover is lifted again.
            if (Ammo.CoverOpen && !_beltCoverWasOpen)
            {
                _beltLaidThisLift = false;
            }
            _beltCoverWasOpen = Ammo.CoverOpen;
            if (!Ammo.MagInserted || !Ammo.HasCover || Ammo.BeltLaid || !VRConfig.BeltByHand.Value)
            {
                _beltHanging = false; // (with belts by hand off too: one hanging stayed drawn for good)
            }
            else if (!_beltHeld)
            {
                // A box in with its belt still to lay: its belt hangs from its mouth, to be taken and laid with the cover open. Drawn only
                // after a slip, the belt was nowhere to be seen and the hand reload stopped at LAY BELT (255.B6-1); and drawn only under an
                // open cover, a box put in with the cover shut showed no belt at all (user, 2026-11-03: "magbelt is sometimes dissapearing
                // when inserted"). It hangs from the box's mouth out over its side, clear of the shut cover.
                _belt = _belt ?? new BeltStrip();
                _beltHanging = true;
            }
            // A laid strip shows under the shut cover, and with the cover open only if it was laid in this lift; the box going
            // clears it, so a box put back is never drawn with the old belt (274). Read from the state, so a belt laid at the
            // spawn, or still laid when the gun is drawn again, shows too; for hand-laid belts only (without BeltByHand the belt
            // was never shown on the tray), and a gun whose model has its own belt shows that instead. A box fired empty has no belt left.
            bool laidOnTray = Ammo.BeltLaid && Ammo.MagInserted && Ammo.MagRounds > 0 && Ammo.HasCover && VRConfig.BeltByHand.Value && _held.BeltPartCount == 0
                              && (!Ammo.CoverOpen || _beltLaidThisLift);
            bool show = _beltHeld || _beltHanging || laidOnTray;
            if (laidOnTray && _belt == null)
            {
                Log.InfoOnce("belt-tray:" + Data.Key, $"{Data.Key}: the belt laid on the feed tray shows from the gun's state (spawned or drawn again with it laid).");
                _belt = new BeltStrip();
            }
            if (_belt == null)
            {
                return;
            }
            // Hidden even with no gun body to place it on: a strip left drawn with its box gone stayed in the air (274).
            _belt.Show(show);
            if (!show || Rig.GunBody == null)
            {
                return;
            }
            Vector3 from = BeltStartWorld;
            Vector3 to;
            if (_beltHeld)
            {
                to = off.Palm.position + off.Palm.Forward * 0.02f;
                to = from + Vector3.ClampMagnitude(to - from, BeltHeldReach);
            }
            else if (_beltHanging)
            {
                to = BeltHangEnd;
            }
            else
            {
                to = FeedTrayWorld;
                float eased = _beltLaidAt < 0f ? 1f : (Time.unscaledTime - _beltLaidAt) / 0.12f;
                if (eased < 1f)
                {
                    to = Vector3.Lerp(Rig.ToWorld(_beltLaidFrom), to, Mathf.SmoothStep(0f, 1f, eased));
                }
            }
            // A nearly empty box has only a few rounds of belt: held, they end in the hand; hanging or laid, they start at the
            // box mouth (counted from the hand, they began in the air, 22 cm off the box for ten rounds; sweep round 2).
            // Over the box's side when it hangs, or when the hand holds it where the straight line runs through the box (below it).
            Vector3? via = _beltHanging || _beltHeld && BeltStrip.Crosses(from, to, SeatPose, Data.MagBounds) ? BeltSideExit : (Vector3?)null;
            _belt.Place(from, to, Rig.AimWorld, Ammo.MagRounds, fromMouth: !_beltHeld, keepOut: SeatPose, keepOutBox: Data.MagBounds, via: via);
        }
    }
}
