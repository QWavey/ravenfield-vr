using System.Collections;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Manual reloading with both hands. Magazines, shells and rockets ride a rail into the gun: near the well they
    /// lock onto the insertion axis and slide with your hand; only full depth seats them (with the mod's mag-in
    /// sound). Released magazines slide out of the well before they fall. Slides, charging handles, bolts and
    /// pumps follow your hand along their real travel. Hands are never pinned to the weapon: the parts follow the
    /// hand, not the other way round.
    /// </summary>
    public sealed class ReloadInteractions
    {
        private const float EjectSeconds = 0.09f;

        private readonly WeaponSystem _sys;
        private readonly HeldWeapon _held;

        private bool _racking;
        /// <summary>Where along the stroke the hand was last frame (for the tug the pull gives the gun).</summary>
        private float _tugStroke;
        private Vector3 _rackStartGun;
        private float _rackBase;
        private bool _rackedBack;
        private bool _returning;
        private float _lastTick;

        private MagazineProp _ejecting;
        private float _ejectTime;

        /// <summary>
        /// The magazine taken out during a tactical reload, held in the same hand as the fresh one (H3VR palming): mag
        /// release with a fresh magazine at the well drops the old one into that hand instead of onto the floor.
        /// </summary>
        private MagazineProp _palmed;

        // Mag kick diagnostics and the swept test (the new magazine's tip passing the old one's base between frames).
        private float _kickPrevAlong = float.NaN;
        private float _kickLogAt;

        private float _openedAt;
        private bool _coverHeld;
        private float _coverGrabAngle;
        private float _coverGrabOpenness;

        public ReloadInteractions(WeaponSystem sys, HeldWeapon held)
        {
            _sys = sys;
            _held = held;
        }

        /// <summary>Reload targets follow the firing weapon (parent or sub weapon).</summary>
        private WeaponRig Rig => _held.ActiveRig;
        private WeaponRigData Data => _held.ActiveRig.Data;
        private AmmoState Ammo => _held.ActiveAmmo;

        /// <summary>The foregrip always belongs to the main weapon model.</summary>
        private WeaponRig GripRig => _held.Rig;
        private WeaponRigData GripData => _held.Rig.Data;

        private bool Manual => VRConfig.Reload.Value == ReloadMode.Manual && Ammo != null && Ammo.Participates;
        /// <summary>Break-action or revolver: opens to load, no slide.</summary>
        private bool Breaks => Data.Action == ActionType.BreakOpen;
        private float Travel => Mathf.Max(0.005f, Data.SlideTravel.magnitude);
        private Vector3 TravelDir => Data.SlideTravel.normalized;

        public void Update()
        {
            VRHand dom = _sys.Rig.Dominant;
            VRHand off = _sys.Rig.Off;

            if (VRInput.GetPressed(DigitalAction.MagRelease, dom.Side) && !Manual)
            {
                Log.InfoOnce("magrel-off:" + Data.Key, $"Magazine release on {Data.Key}: manual reloading is not active for it (mode {VRConfig.Reload.Value}, action {Data.Action}).");
            }
            if (Manual)
            {
                if (VRInput.GetPressed(DigitalAction.MagRelease, dom.Side))
                {
                    VRInput.Consume(DigitalAction.MagRelease, dom.Side);
                    if (!Breaks && !(Ammo.UsesDetachableMag && Ammo.MagInserted && !Ammo.BoxLocked))
                    {
                        Log.InfoOnce("magrel-no:" + Data.Key, $"Magazine release on {Data.Key} did nothing: detachable {Ammo.UsesDetachableMag}, inserted {Ammo.MagInserted}, box locked {Ammo.BoxLocked}, action {Data.Action}.");
                    }
                    if (Breaks)
                    {
                        SetBreakOpen(!Ammo.Open);
                    }
                    else if (Ammo.BoxLocked)
                    {
                        _held.FlashCounter("OPEN COVER"); // the belt is in the feed: the box will not come off
                        dom.Haptic(0.08f, 40f, 0.4f);
                    }
                    else if (Ammo.UsesDetachableMag && Ammo.MagInserted)
                    {
                        if (CanPalm(off))
                        {
                            TacticalEject(off);
                        }
                        else
                        {
                            EjectMagazine();
                        }
                    }
                }
                // Snap it shut with a flick of the wrist, like the real thing.
                if (Breaks && Ammo.Open && Time.unscaledTime - _openedAt > 0.35f && dom.AngularVelocity.magnitude > 9f)
                {
                    SetBreakOpen(false);
                }
                if (VRInput.GetPressed(DigitalAction.SlideRelease, dom.Side) && Ammo.SlideLocked)
                {
                    VRInput.Consume(DigitalAction.SlideRelease, dom.Side);
                    ReleaseSlide();
                }
            }

            if (!off.Tracked)
            {
                return;
            }
            // Grenades: the pin (ThrowableHandler) and loose props (WeaponSystem) take the off hand's grab. A grip closed a
            // moment before the hand reaches the bolt or magazine still takes it (nothing else has claimed the press).
            if (off.GrabPressed)
            {
                _pressPalm = off.Palm.position; // where this squeeze began (the pouch only takes squeezes made at it)
            }
            if ((off.GrabPressed || off.GrabReaching) && off.Role == HandRole.Free && Data.Action != ActionType.Throwable)
            {
                TryGrab(off);
            }
            switch (off.Role)
            {
                case HandRole.Foregrip:
                    if (!off.GrabHeld || Vector3.Distance(off.Palm.position, GripRig.ToWorld(GripData.SupportPoint)) > 0.35f)
                    {
                        _held.SetTwoHanded(false);
                        off.Role = HandRole.Free;
                    }
                    break;
                case HandRole.Slide:
                    UpdateRack(off);
                    break;
                case HandRole.Magazine:
                    UpdateHeldProp(off);
                    break;
                case HandRole.Hinge:
                    UpdateCover(off);
                    break;
                case HandRole.Belt:
                    UpdateBelt(off);
                    break;
                case HandRole.Optic:
                    UpdateOptic(off);
                    break;
            }
        }

        // ---- grabbing ------------------------------------------------------------------------------

        private Vector3 _pressPalm = Vector3.one * 1e4f;
        private float _seatedAt = -10f;

        private void TryGrab(VRHand off)
        {
            Vector3 palm = off.Palm.position;
            float radius = VRConfig.GrabRadius.Value;
            int choice = 0; // 1 catch, 2 rack, 3 pouch, 4 foregrip, 5 feed cover, 6 seated magazine, 7 belt, 8 optic
            float best = float.MaxValue;
            // This weapon's own magazines first, then anything else lying within reach, whatever this gun's reload mode
            // (picking something up is not a reload). A magazine you are reaching for wins over the pouch and the
            // gun's parts: bending down to pick one up brings the pouch zone (it follows your head) down with you.
            MagazineProp caught = _ejecting != null && _ejecting.DistanceTo(palm) < 0.13f
                ? _ejecting
                : MagazineProp.NearestDropped(palm, 0.13f, Data.Key) ?? MagazineProp.NearestDropped(palm, 0.13f, null);
            if (caught != null)
            {
                Consider(1, caught.DistanceTo(palm) / 0.13f * 0.4f, ref choice, ref best);
            }
            if (Manual && Ammo.UsesChamber)
            {
                float rackRadius = radius * 1.3f;
                float d = RackZoneDistance(palm);
                if (d < rackRadius)
                {
                    // Slight preference: racking is the most precise target.
                    Consider(2, d / rackRadius * 0.85f, ref choice, ref best);
                }
            }
            if (Manual && Ammo.UsesDetachableMag && Ammo.MagInserted && _ejecting == null && !Ammo.BoxLocked)
            {
                float d = Vector3.Distance(palm, SeatedMagBaseWorld);
                if (d < radius * 1.1f)
                {
                    Consider(6, d / (radius * 1.1f), ref choice, ref best);
                }
            }
            if (Manual && Data.Hinge == HingeKind.FeedCover)
            {
                float d = Vector3.Distance(palm, CoverGrabWorld);
                if (d < radius)
                {
                    Consider(5, d / radius * 0.9f, ref choice, ref best);
                }
            }
            if (Manual && VRConfig.BeltByHand.Value && Ammo.HasCover && Ammo.CoverOpen && Ammo.MagInserted && !Ammo.BeltLaid)
            {
                float d = Vector3.Distance(palm, BeltStartWorld);
                if (d < radius)
                {
                    Consider(7, d / radius * 0.8f, ref choice, ref best); // the belt end is the obvious next step
                }
            }
            if (HasSwitchableSight)
            {
                float d = OpticDistance(palm);
                if (d < 0.06f)
                {
                    Consider(8, d / 0.06f * 0.9f, ref choice, ref best);
                }
            }
            // The pouch only answers a squeeze made at it: a squeeze that found nothing elsewhere must not pull a magazine
            // out when the hand passes the hip a moment later. Parts of the gun win when about as close.
            if (Manual && _sys.Zones.InPouch(palm)
                && (off.GrabPressed || Vector3.Distance(_pressPalm, _sys.Zones.Pouch) < BodyZones.PouchRadius + 0.05f))
            {
                // A pistol held low beside the hip puts its slide and its cupping zone within reach of the pouch: a
                // squeeze in the pouch takes a magazine unless a part of the gun is clearly closer.
                Consider(3, 0.05f + 0.6f * Vector3.Distance(palm, _sys.Zones.Pouch) / BodyZones.PouchRadius, ref choice, ref best);
            }
            if (GripData.HasSupportPoint || GripData.IsPistol)
            {
                float d = ForegripDistance(palm);
                float r = GripData.IsPistol ? 0.12f : radius * 1.4f;
                if (d < r)
                {
                    Consider(4, d / r, ref choice, ref best);
                }
            }

            switch (choice)
            {
                case 1:
                    if (caught == _ejecting)
                    {
                        _ejecting = null;
                    }
                    caught.Attach(off);
                    off.Haptic(0.03f, 150f, 0.5f);
                    break;
                case 2:
                    BeginRack(off);
                    break;
                case 3:
                    TakeFromPouch(off);
                    break;
                case 5:
                    BeginCover(off);
                    break;
                case 6:
                    PullSeatedMagazine(off);
                    break;
                case 7:
                    BeginBelt(off);
                    break;
                case 8:
                    BeginOptic(off);
                    break;
                case 4:
                    off.Role = HandRole.Foregrip;
                    _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value && !GripData.IsPistol);
                    off.Haptic(0.02f, 120f, 0.35f);
                    break;
                default:
                    return;
            }
            VRInput.Consume(DigitalAction.Grab, off.Side);
        }

        private static void Consider(int id, float score, ref int choice, ref float best)
        {
            if (score < best)
            {
                best = score;
                choice = id;
            }
        }

        private float ForegripDistance(Vector3 palm)
        {
            WeaponRig rig = GripRig;
            WeaponRigData d = GripData;
            if (d.IsPistol)
            {
                return Vector3.Distance(palm, rig.ToWorld(d.SupportPoint));
            }
            // Anywhere along the handguard, from just ahead of the grip to the muzzle.
            Vector3 a = rig.ToWorld(d.GripPoint + d.AimAxis * 0.08f);
            Vector3 b = rig.ToWorld(d.MuzzlePoint);
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(palm - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            float lineDist = Vector3.Distance(palm, a + ab * t);
            return Mathf.Min(lineDist, Vector3.Distance(palm, rig.ToWorld(d.SupportPoint)));
        }

        // ---- magazine release ----------------------------------------------------------------------

        private void EjectMagazine()
        {
            if (_ejecting != null)
            {
                FinishEject();
            }
            // The rounds stay in the dropped magazine: pick it up again to use them (they return to the reserve if it
            // despawns first).
            int rounds = Ammo.EjectMagazine();
            _ejecting = MagazineProp.Create(Rig, PropKind.Magazine, rounds);
            Log.InfoOnce("eject:" + Data.Key, $"Magazine ejected from {Data.Key}: {rounds} rounds, {Rig.MagazineParts.Length} part(s) hidden on the gun, pull axis {Data.MagEjectAxis:F2}, rail {_ejecting.RailLength * 100f:0.0} cm.");
            _ejecting.Owner = Ammo;
            _ejecting.RailDepth = _ejecting.RailLength;
            _ejectTime = 0f;
            _held.Sounds.Play(MechSound.MagOut, _ejecting.Go.transform.position);
            _sys.Rig.Dominant.Haptic(0.05f, 90f, 0.6f);
        }

        /// <summary>The off hand holds a fresh magazine near the well and has room for one more.</summary>
        private bool CanPalm(VRHand off)
        {
            if (!VRConfig.MagazinePalming.Value || !off.Tracked || off.Role != HandRole.Magazine || _palmed != null || _ejecting != null)
            {
                return false;
            }
            return off.Held is MagazineProp fresh && fresh.Kind == PropKind.Magazine && fresh.Go != null
                   && fresh.DistanceTo(SeatedMagBaseWorld) < 0.2f;
        }

        /// <summary>Tactical reload: the released magazine goes into the off hand, beside the fresh one.</summary>
        private void TacticalEject(VRHand off)
        {
            int rounds = Ammo.EjectMagazine();
            MagazineProp prop = MagazineProp.Create(Rig, PropKind.Magazine, rounds);
            prop.Owner = Ammo;
            prop.AttachPalmed(off);
            _palmed = prop;
            _held.Sounds.Play(MechSound.MagOut, SeatPose.position);
            _sys.Rig.Dominant.Haptic(0.05f, 90f, 0.6f);
            off.Haptic(0.04f, 120f, 0.55f);
        }

        /// <summary>The fresh magazine is gone from the hand (seated or dropped): the palmed one becomes the one it holds.</summary>
        private void PromotePalmed(VRHand off)
        {
            MagazineProp palmed = _palmed;
            _palmed = null;
            if (palmed == null || palmed.Go == null)
            {
                return;
            }
            if (off.Held == null && off.GrabHeld)
            {
                palmed.Attach(off);
                palmed.FollowHand();
            }
            else
            {
                palmed.Drop(off.ThrowVelocity(), off.AngularVelocity);
            }
        }

        /// <summary>The ejected magazine leaves the well along its axis, then gravity takes it.</summary>
        private void UpdateEject(float dt)
        {
            if (_ejecting == null)
            {
                return;
            }
            if (_ejecting.Go == null || _ejecting.HeldBy != null)
            {
                _ejecting = null;
                return;
            }
            _ejectTime += dt;
            float u = Mathf.Clamp01(_ejectTime / EjectSeconds);
            _ejecting.RailDepth = _ejecting.RailLength * (1f - u * u);
            _ejecting.RailPose(SeatPose).ApplyTo(_ejecting.Go.transform);
            if (u >= 1f)
            {
                FinishEject();
            }
        }

        private void FinishEject()
        {
            MagazineProp prop = _ejecting;
            _ejecting = null;
            if (prop == null || prop.Go == null)
            {
                return;
            }
            // The weapon may already be gone (replaced or destroyed by the game): the prop's own axis then.
            Vector3 insert = Rig.GunBody != null ? SeatPose.TransformDirection(prop.InsertDirLocal) : prop.InsertDirWorld;
            prop.Drop(_sys.Rig.Dominant.Velocity - insert * 1.1f + Vector3.down * 0.25f, Random.insideUnitSphere * 1.5f);
        }

        /// <summary>Where magazines, shells and rockets go in; chambers on a break-open barrel or crane swing out with it.</summary>
        private RigidPose SeatPose
        {
            get
            {
                RigidPose seat = Data.MagSeat;
                if (Data.SeatOnHinge && _held.HingeOpenness > 0.001f)
                {
                    seat = HingeRotate(seat, _held.HingeOpenness);
                }
                return Rig.GunBodyPose.Multiply(seat);
            }
        }

        /// <summary>A gun-space pose carried along by the hinge opened <paramref name="openness"/> of the way.</summary>
        private RigidPose HingeRotate(RigidPose pose, float openness)
        {
            Quaternion r = Quaternion.AngleAxis(Data.HingeOpenAngle * openness, Data.HingeAxis);
            return new RigidPose(Data.HingePivot + r * (pose.position - Data.HingePivot), r * pose.rotation);
        }

        /// <summary>Base plate of the seated magazine, where a hand takes hold to pull it.</summary>
        private Vector3 SeatedMagBaseWorld
        {
            get
            {
                Quaternion seatInv = Quaternion.Inverse(Data.MagSeat.rotation);
                Vector3 insertLocal = (seatInv * -Data.MagEjectAxis).normalized;
                Bounds b = Data.MagBounds;
                float extent = Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(insertLocal.x), Mathf.Abs(insertLocal.y), Mathf.Abs(insertLocal.z))));
                return SeatPose.TransformPoint(b.center - insertLocal * extent);
            }
        }

        /// <summary>The off hand takes the seated magazine: it unlatches and slides out along the well with the hand.</summary>
        private void PullSeatedMagazine(VRHand off)
        {
            int rounds = Ammo.EjectMagazine();
            MagazineProp prop = MagazineProp.Create(Rig, PropKind.Magazine, rounds);
            prop.Owner = Ammo;
            prop.Attach(off);
            prop.OnRail = true;
            prop.RailDepth = prop.RailLength;
            prop.LastTick = prop.RailDepth;
            prop.RailPose(SeatPose).ApplyTo(prop.Go.transform);
            _held.Sounds.Play(MechSound.MagOut, SeatPose.position);
            _held.Nudge(-SeatPose.TransformDirection(prop.InsertDirLocal) * 0.08f); // the latch gives with a tug
            off.Haptic(0.03f, 120f, 0.5f);
        }

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
                if (Ammo.MagRounds < Ammo.Capacity)
                {
                    PlayCasingEject(); // the fired cases come out, live rounds stay
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
            if (!Ammo.CoverOpen && openness > 0.85f)
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
            if (!off.GrabHeld || Vector3.Distance(off.Palm.position, CoverGrabWorld) > 0.25f)
            {
                EndCover(off);
            }
        }

        private void EndCover(VRHand off)
        {
            _coverHeld = false;
            if (off.Role == HandRole.Hinge)
            {
                off.Role = HandRole.Free;
            }
            // Let go part way: it falls open or drops shut.
            if (Ammo.CoverOpen && _held.HingeOpenness < 0.4f)
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

        /// <summary>The weapon has more than one sight to choose from: the game's sight modes or a mod's magnifier key.</summary>
        private bool HasSwitchableSight
        {
            get
            {
                Weapon w = _held.Weapon;
                return w != null && (w.HasAnyAltSightModes() || Input.ScriptKeys.Find(w, Input.ScriptKeyPurpose.Magnifier) != null);
            }
        }

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

        /// <summary>Where the belt leaves the seated box: the top of the box, on its centre line.</summary>
        private Vector3 BeltStartWorld
        {
            get
            {
                Quaternion seatInv = Quaternion.Inverse(Data.MagSeat.rotation);
                Vector3 insertLocal = (seatInv * -Data.MagEjectAxis).normalized;
                Bounds b = Data.MagBounds;
                float extent = Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(insertLocal.x), Mathf.Abs(insertLocal.y), Mathf.Abs(insertLocal.z))));
                return SeatPose.TransformPoint(b.center + insertLocal * extent);
            }
        }

        /// <summary>The feed tray: under the closed cover, between its hinge and its latch.</summary>
        private Vector3 FeedTrayWorld => Rig.ToWorld(Vector3.Lerp(Data.HingePivot, Data.HingeGrabPoint, 0.5f) - Data.Up * 0.012f);

        private void BeginBelt(VRHand off)
        {
            _beltHeld = true;
            off.Role = HandRole.Belt;
            if (_belt == null)
            {
                _belt = new BeltStrip();
            }
            _belt.Show(true);
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
            if (!off.GrabHeld || Vector3.Distance(off.Palm.position, BeltStartWorld) > 0.45f)
            {
                EndBelt(off); // let go: it falls back into the box
                return;
            }
            if (Vector3.Distance(off.Palm.position, FeedTrayWorld) < 0.045f)
            {
                CompleteBelt(off);
            }
        }

        private void CompleteBelt(VRHand off)
        {
            if (Ammo.LayBelt())
            {
                _held.Sounds.Play(MechSound.ShellIn, FeedTrayWorld);
                off.Haptic(0.04f, 120f, 0.6f);
                _sys.Rig.Dominant.Haptic(0.03f, 120f, 0.4f);
            }
            EndBelt(off);
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

        // ---- pouch -----------------------------------------------------------------------------------

        private void TakeFromPouch(VRHand off)
        {
            PropKind kind;
            int rounds;
            if (Ammo.UsesDetachableMag)
            {
                kind = PropKind.Magazine;
                rounds = Ammo.RoundsForFreshMagazine();
            }
            else if (Data.Action == ActionType.SingleShot)
            {
                kind = PropKind.Rocket;
                rounds = Ammo.MagRounds == 0 && (Ammo.InfiniteSpare || Ammo.SpareAmmo > 0) ? 1 : 0;
            }
            else
            {
                kind = PropKind.Shell;
                rounds = Data.Action == ActionType.ShellFed || Breaks
                    ? Ammo.ShellChunk()
                    : Mathf.Min(Ammo.Capacity - Ammo.MagRounds, Ammo.InfiniteSpare ? int.MaxValue : Ammo.SpareAmmo);
            }
            if (rounds <= 0)
            {
                off.Haptic(0.15f, 35f, 0.7f);
                _sys.FlashMessage(Ammo.SpareAmmo <= 0 && !Ammo.InfiniteSpare ? "NO AMMO" : "FULL");
                return;
            }
            Ammo.TakeSpare(rounds);
            MagazineProp prop = MagazineProp.Create(Rig, kind, rounds);
            prop.Owner = Ammo;
            prop.Attach(off);
            prop.FollowHand();
            _sys.Sounds.Play(MechSound.Pouch, off.Palm.position, 0.6f);
            off.Haptic(0.03f, 140f, 0.45f);
        }

        // ---- held props: free, then on the rail --------------------------------------------------------

        private bool AcceptsNow(MagazineProp prop)
        {
            if (prop.WeaponKey != Data.Key || !Manual)
            {
                return false;
            }
            switch (prop.Kind)
            {
                case PropKind.Magazine:
                    return Ammo.UsesDetachableMag && !Ammo.MagInserted;
                case PropKind.Rocket:
                    return Ammo.MagRounds == 0;
                default:
                    return Ammo.MagRounds < Ammo.Capacity && (!Breaks || Ammo.Open);
            }
        }

        private void UpdateHeldProp(VRHand off)
        {
            var prop = off.Held as MagazineProp;
            if (prop == null)
            {
                off.Role = HandRole.Free;
                if (_palmed != null)
                {
                    PromotePalmed(off);
                }
                return;
            }
            if (!off.GrabHeld)
            {
                ReleaseProp(off, prop);
                return;
            }
            if (TryMagKick(off, prop))
            {
                return;
            }
            if (!AcceptsNow(prop))
            {
                prop.OnRail = false;
                if (_pullingIn == prop)
                {
                    _pullingIn = null;
                }
                return;
            }

            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(prop.InsertDirLocal);
            Vector3 entryTip = seat.TransformPoint(prop.TipLocal) - insert * prop.RailLength;
            RigidPose hold = prop.HoldPose(off);
            Vector3 holdTip = hold.TransformPoint(prop.TipLocal);
            Vector3 rel = holdTip - entryTip;
            float along = Vector3.Dot(rel, insert);
            float lateral = Vector3.ProjectOnPlane(rel, insert).magnitude;

            if (!prop.OnRail)
            {
                float angle = Vector3.Angle(hold.TransformDirection(prop.InsertDirLocal), insert);
                bool lined = lateral < 0.035f && along > -0.03f && along < prop.RailLength && angle < 35f;
                if (lined)
                {
                    prop.OnRail = true;
                    prop.RailDepth = Mathf.Clamp(along, 0f, prop.RailLength);
                    prop.LastTick = prop.RailDepth;
                    off.Haptic(0.015f, 180f, 0.3f);
                }
                return;
            }

            if (_pullingIn == prop)
            {
                // Started straight into a well inside the grip: the rail takes it home (see below), whatever the hand does.
                prop.RailDepth = Mathf.MoveTowards(prop.RailDepth, prop.RailLength, 0.9f * Mathf.Min(Time.unscaledDeltaTime, 0.05f));
                if (prop.RailDepth >= prop.RailLength * 0.999f)
                {
                    _pullingIn = null;
                    SeatProp(off, prop);
                }
                return;
            }
            // On the rail: the hand pushes the prop along the axis only.
            if (along < -0.03f || lateral > 0.07f)
            {
                prop.OnRail = false;
                return;
            }
            prop.RailDepth = Mathf.Clamp(along, 0f, prop.RailLength);
            if (Mathf.Abs(prop.RailDepth - prop.LastTick) > 0.01f)
            {
                prop.LastTick = prop.RailDepth;
                off.Haptic(0.006f, 200f, 0.15f);
            }
            if (prop.RailDepth >= prop.RailLength * 0.96f)
            {
                SeatProp(off, prop);
            }
            else if (prop.Kind == PropKind.Magazine && WellInGrip && prop.RailDepth >= prop.RailLength * 0.45f)
            {
                // A pistol's well is in its grip, where the gun hand is: the two controllers meet before the magazine is all
                // the way in. Half way in and straight, it is drawn home the rest of the way.
                _pullingIn = prop;
                off.Haptic(0.012f, 160f, 0.35f);
            }
        }

        /// <summary>A magazine that is being drawn the last part of the way into a well inside the grip.</summary>
        private MagazineProp _pullingIn;

        /// <summary>The magazine well is inside the gun hand's grip (pistols, magazine-in-grip designs).</summary>
        private bool WellInGrip
        {
            get
            {
                WeaponRigData d = Data;
                Vector3 magCentre = d.MagSeat.TransformPoint(d.MagBounds.center);
                return d.IsPistol || Mathf.Abs(Vector3.Dot(magCentre - d.GripPoint, d.AimAxis)) < 0.05f;
            }
        }

        /// <summary>
        /// Tactical reload with one hand: driving a fresh magazine into the base of the seated one knocks the old
        /// one out of the well; the new one then slides in on the rail. Any magazine of the gun does it (an empty one
        /// too), struck fast or pushed slowly and firmly past the old one's base, held straight or tilted; the window is
        /// as big as the magazine. A magazine moving past beside the gun, clear of the base, does nothing.
        /// </summary>
        private bool TryMagKick(VRHand off, MagazineProp prop)
        {
            if (prop.Kind != PropKind.Magazine || prop.WeaponKey != Data.Key || !Manual || !Ammo.UsesDetachableMag || !Ammo.MagInserted
                || _ejecting != null || Ammo.BoxLocked)
            {
                _kickPrevAlong = float.NaN;
                _kickPushSince = -1f;
                return false;
            }
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(prop.InsertDirLocal);
            float length = Vector3.Dot(prop.TipLocal - prop.BaseLocal, prop.InsertDirLocal);
            Vector3 oldBase = seat.TransformPoint(prop.TipLocal) - insert * length;
            RigidPose hold = prop.HoldPose(off);
            Vector3 rel = hold.TransformPoint(prop.TipLocal) - oldBase;
            float along = Vector3.Dot(rel, insert);
            float lateral = Vector3.ProjectOnPlane(rel, insert).magnitude;
            float angle = Vector3.Angle(hold.TransformDirection(prop.InsertDirLocal), insert);
            // The speed that counts is the new magazine's speed relative to the gun: driving it up into the old one
            // and bringing the gun down onto it are the same strike.
            // Averaged over the last moment (raw controller speed is noisy around these thresholds).
            float speed = Vector3.Dot(off.ThrowVelocity() - _sys.Rig.Dominant.ThrowVelocity(), insert);
            // Sized by the magazine: a strike lands anywhere on the old base plate (as wide as the magazine) with any part
            // of the fresh one's top; a slow push has to be on the base itself.
            float strikeReach = Mathf.Clamp(prop.CrossHalf * 2f + 0.025f, 0.06f, 0.13f);
            float pushReach = Mathf.Clamp(prop.CrossHalf + 0.025f, 0.035f, 0.08f);
            float below = Mathf.Clamp(length * 0.25f, 0.04f, 0.08f);
            float into = Mathf.Clamp(length * 0.45f, 0.06f, 0.12f);
            bool near = along > -0.12f && along < into + 0.04f && lateral < strikeReach + 0.04f;
            if (near && Time.unscaledTime - _kickLogAt > 0.3f)
            {
                // Numbers for tuning in the headset: which condition holds a kick back.
                _kickLogAt = Time.unscaledTime;
                Log.Info($"Mag kick test {Data.Key}: along {along * 100f:0.0} cm, lateral {lateral * 100f:0.0} cm (reach {strikeReach * 100f:0.0}), angle {angle:0} deg, speed {speed:0.00} m/s, kickable {Data.MagKickable}, rounds {prop.Rounds}");
            }
            // Passing the old magazine's base between two frames counts even when no single frame was inside the window.
            bool crossed = !float.IsNaN(_kickPrevAlong) && _kickPrevAlong < 0f && along >= 0f;
            _kickPrevAlong = near ? along : float.NaN;
            if (!Data.MagKickable)
            {
                return false; // deep wells (pistol grips, KRISS Vector) leave nothing to hit
            }
            if (lateral > strikeReach || angle > MaxKickAngle)
            {
                _kickPushSince = -1f;
                return false;
            }
            if (Time.unscaledTime - _seatedAt < 0.4f)
            {
                return false; // the push that just seated a magazine must not knock it straight back out with the next one
            }
            bool inWindow = along >= -below && along <= into;
            if (inWindow && !_kickTicked)
            {
                _kickTicked = true;
                off.Haptic(0.01f, 60f, 0.25f); // you are on the old magazine's base
            }
            else if (!inWindow)
            {
                _kickTicked = false;
            }
            // A slow, firm push: the fresh magazine's top pressed past the old one's base (where the old one would have to
            // give way), held there a moment or pushed on, and not being pulled back out.
            bool pressing = lateral <= pushReach && along >= 0.012f && along <= into && speed > -0.05f;
            if (!pressing)
            {
                _kickPushSince = -1f;
            }
            else if (_kickPushSince < 0f)
            {
                _kickPushSince = Time.unscaledTime;
            }
            bool push = pressing && (along >= 0.03f || Time.unscaledTime - _kickPushSince >= 0.12f);
            bool strike = inWindow && speed >= 0.2f || crossed && speed >= 0.08f;
            if (!strike && !push)
            {
                return false;
            }
            Log.Info($"Mag kick on {Data.Key} ({(strike ? "strike" : "push")}): along {along * 100f:0.0} cm, lateral {lateral * 100f:0.0} cm, angle {angle:0} deg, speed {speed:0.00} m/s");
            _kickPrevAlong = float.NaN;
            _kickPushSince = -1f;
            EjectMagazine();
            off.Haptic(0.04f, 80f, 0.8f);
            return true;
        }

        /// <summary>How far (degrees) a fresh magazine may be tilted off the well's axis and still knock the old one out.</summary>
        private const float MaxKickAngle = 75f;

        private bool _kickTicked;
        /// <summary>When the fresh magazine started pressing past the old one's base (-1: not pressing).</summary>
        private float _kickPushSince = -1f;

        private void SeatProp(VRHand off, MagazineProp prop)
        {
            _seatedAt = Time.unscaledTime;
            Vector3 at = SeatPose.position;
            switch (prop.Kind)
            {
                case PropKind.Magazine:
                    Ammo.InsertMagazine(prop.Rounds);
                    _held.Sounds.Play(MechSound.MagIn, at);
                    _held.Nudge(SeatPose.TransformDirection(prop.InsertDirLocal) * 0.14f); // seated with a push into the gun
                    break;
                case PropKind.Shell:
                {
                    int accepted = Ammo.LoadRounds(prop.Rounds);
                    Ammo.ReturnSpare(prop.Rounds - accepted);
                    _held.Sounds.Play(MechSound.ShellIn, at);
                    break;
                }
                case PropKind.Rocket:
                    Ammo.LoadRounds(1);
                    _held.Sounds.Play(MechSound.MagIn, at);
                    _held.Nudge(SeatPose.TransformDirection(prop.InsertDirLocal) * 0.1f);
                    break;
            }
            prop.Destroy();
            off.Haptic(0.05f, 110f, 0.8f);
            _sys.Rig.Dominant.Haptic(0.05f, 110f, 0.6f);
            if (_palmed != null)
            {
                PromotePalmed(off); // the old magazine stays in the hand
            }
        }

        /// <summary>Let go at the pouch, gently: stowed. Flicked away from the hip it flies.</summary>
        private bool Stows(VRHand off) => _sys.Zones.InPouch(off.Palm.position) && off.ThrowVelocity().magnitude < 1.2f;

        private void ReleaseProp(VRHand off, MagazineProp prop)
        {
            if (_pullingIn == prop)
            {
                // Let go while it was being drawn in: it finishes going in rather than falling out of the grip.
                _pullingIn = null;
                if (AcceptsNow(prop))
                {
                    SeatProp(off, prop);
                    return;
                }
            }
            if (_palmed != null)
            {
                // Letting go drops (or stows) everything the hand holds.
                MagazineProp palmed = _palmed;
                _palmed = null;
                if (Stows(off))
                {
                    AmmoState back = palmed.WeaponKey == Data.Key ? Ammo : palmed.Owner;
                    back?.ReturnSpare(palmed.Rounds);
                    palmed.Rounds = 0;
                    palmed.Destroy();
                }
                else
                {
                    palmed.Drop(off.ThrowVelocity(), off.AngularVelocity);
                }
            }
            if (Stows(off))
            {
                // Stowed: the rounds go back to this weapon's reserve, or to the weapon the magazine belongs to.
                AmmoState into = prop.WeaponKey == Data.Key ? Ammo : prop.Owner;
                into?.ReturnSpare(prop.Rounds);
                prop.Rounds = 0;
                prop.Destroy();
                _sys.Sounds.Play(MechSound.Pouch, off.Palm.position, 0.5f);
                off.Haptic(0.02f, 120f, 0.3f);
                return;
            }
            if (prop.Owner == null && prop.WeaponKey == Data.Key)
            {
                prop.Owner = Ammo;
            }
            if (prop.OnRail)
            {
                // Let go half way in: it slides back out of the well.
                Vector3 insert = SeatPose.TransformDirection(prop.InsertDirLocal);
                prop.Drop(_sys.Rig.Dominant.Velocity - insert * 0.6f, Vector3.zero);
                return;
            }
            prop.Drop(off.ThrowVelocity(), off.AngularVelocity);
        }

        // ---- slide / bolt / pump -----------------------------------------------------------------------

        /// <summary>
        /// The stroke the hand works: a separate charging handle's own travel (AR style: the bolt carrier follows it,
        /// whatever its own travel), else the slide or bolt's.
        /// </summary>
        private bool HandleStroke => Data.HandlePath != null && Rig.Handle != null && Data.HandleTravel.sqrMagnitude > 1e-4f;
        private Vector3 StrokeDir => HandleStroke ? Data.HandleTravel.normalized : TravelDir;
        private float StrokeLength => HandleStroke ? Mathf.Clamp(Data.HandleTravel.magnitude, 0.03f, 0.14f) : Travel;
        /// <summary>How far the action is open, 0 closed .. 1 fully back.</summary>
        private float Fraction => Mathf.Clamp01(_held.SlideOffset / Travel);

        /// <summary>Where the hand takes the slide, handle or bolt, moving with it.</summary>
        private Vector3 RackPointWorld => Rig.ToWorld(RackGun + StrokeDir * (StrokeLength * Fraction));

        /// <summary>The rack point (gun space); for the held weapon's own rig it sits on the drawn model's surface.</summary>
        private Vector3 RackGun => ReferenceEquals(Rig, _held.Rig) ? _held.RackPointGun : Data.RackPoint;

        /// <summary>
        /// Distance to the part you rack: a slide can be taken anywhere along its rear half, a bolt or handle around
        /// its knob.
        /// </summary>
        private float RackZoneDistance(Vector3 palm)
        {
            Vector3 a = RackPointWorld;
            float along = Data.IsPistol && !HandleStroke ? 0.05f : HandleStroke ? 0.012f : 0.03f;
            Vector3 b = a + Rig.AimWorld * along;
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(palm - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return Vector3.Distance(palm, a + ab * t);
        }

        /// <summary>Bolt, pump and lever actions move only by hand: let go half way and they stay there.</summary>
        private bool HandDriven => Data.CyclesByHand;

        /// <summary>A pump at the handguard: holding it is also the support grip, so both hands aim while pumping.</summary>
        private bool IsPump => Data.CyclesByHand && GripData.HasSupportPoint && !GripData.IsPistol
                               && Vector3.Distance(Data.RackPoint, GripData.SupportPoint) < 0.15f;

        private void BeginRack(VRHand off)
        {
            _racking = true;
            _returning = false;
            _rackStartGun = Rig.ToGun(off.Palm.position);
            _rackBase = Ammo.SlideLocked ? 1f : Fraction;
            _rackedBack = Ammo.SlideLocked || Ammo.BoltOpen && Fraction >= 0.8f;
            _lastTick = _rackBase * StrokeLength;
            _tugStroke = _lastTick;
            off.Role = HandRole.Slide;
            off.Haptic(0.02f, 160f, 0.35f);
            if (IsPump)
            {
                _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
            }
        }

        private void UpdateRack(VRHand off)
        {
            if (!_racking)
            {
                off.Role = HandRole.Free;
                return;
            }
            Vector3 handGun = Rig.ToGun(off.Palm.position);
            // The hand's pull along the handle's (or slide's) own stroke; the slide or bolt carrier moves the same share
            // of its travel.
            float pull = Vector3.Dot(handGun - _rackStartGun, StrokeDir);
            float fraction = Mathf.Clamp01(_rackBase + pull / StrokeLength);
            _held.SlideOffset = fraction * Travel;

            float stroke = fraction * StrokeLength;
            // The pull tugs the gun toward the hand (and a push home toward the muzzle), a few millimetres, as in Boneworks.
            float moved = stroke - _tugStroke;
            _tugStroke = stroke;
            if (Mathf.Abs(moved) > 1e-5f)
            {
                _held.Nudge(Rig.DirToWorld(StrokeDir) * Mathf.Clamp(moved * 2.5f, -0.05f, 0.05f));
            }
            if (Mathf.Abs(stroke - _lastTick) > 0.01f)
            {
                _lastTick = stroke;
                off.Haptic(0.006f, 220f, 0.15f);
            }
            if (!_rackedBack && fraction >= 0.8f)
            {
                _rackedBack = true;
                bool ejected = Ammo.RackBack();
                _held.Sounds.Play(MechSound.SlideBack, RackPointWorld);
                off.Haptic(0.03f, 140f, 0.6f);
                if (ejected)
                {
                    PlayCasingEject();
                }
                if (HandDriven)
                {
                    Ammo.BoltOpen = true;
                }
            }
            else if (HandDriven && _rackedBack && fraction <= 0.1f)
            {
                // Pushed home by hand: it strips and chambers a round as it closes.
                _rackedBack = false;
                Ammo.BoltOpen = false;
                Ammo.RackForward();
                _held.Nudge(Rig.AimWorld * 0.1f, 10f); // the bolt or pump closing jolts the gun
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld);
                off.Haptic(0.04f, 120f, 0.6f);
                _sys.Rig.Dominant.Haptic(0.03f, 120f, 0.4f);
            }

            float drift = Vector3.ProjectOnPlane(handGun - _rackStartGun, StrokeDir).magnitude;
            if (!off.GrabHeld || drift > 0.25f)
            {
                EndRack(off, chamber: true);
            }
        }

        private void EndRack(VRHand off, bool chamber)
        {
            _racking = false;
            off.Role = HandRole.Free;
            if (IsPump)
            {
                _held.SetTwoHanded(false);
            }
            if (HandDriven)
            {
                // Stays where the hand left it; it only closes when pushed forward.
                Ammo.BoltOpen = _held.SlideOffset > Travel * 0.08f;
                _returning = false;
                return;
            }
            if (!chamber)
            {
                _returning = true;
                return;
            }
            if (_rackedBack)
            {
                bool chambered = Ammo.RackForward();
                if (!chambered && Data.LockOnEmpty && Ammo.UsesDetachableMag && Ammo.MagInserted && Ammo.MagRounds == 0)
                {
                    Ammo.SlideLocked = true; // empty magazine catches the slide
                }
                if (!Ammo.SlideLocked)
                {
                    _held.Sounds.Play(MechSound.SlideForward, RackPointWorld);
                    off.Haptic(0.04f, 120f, 0.6f);
                    _sys.Rig.Dominant.Haptic(0.04f, 120f, 0.5f);
                }
            }
            _rackedBack = false;
            _returning = !Ammo.SlideLocked;
        }

        private void ReleaseSlide()
        {
            if (Ammo.RackForward())
            {
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld);
            }
            _returning = true;
            _sys.Rig.Dominant.Haptic(0.04f, 120f, 0.6f);
        }

        private void PlayCasingEject()
        {
            ParticleSystem[] casings = _held.ActiveWeapon.configuration.casingParticles;
            if (casings != null && casings.Length > 0 && casings[0] != null)
            {
                casings[0].Play(false);
            }
        }

        public void OnShot()
        {
            if (Ammo != null && Ammo.SlideLocked)
            {
                _held.Sounds.Play(MechSound.SlideBack, RackPointWorld, 0.4f);
            }
        }

        // ---- per-frame visuals -----------------------------------------------------------------------

        public void BeforeRender()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (Ammo != null && Ammo.SlideLocked && !_racking)
            {
                _held.SlideOffset = Travel;
                _returning = false;
            }
            else if (Ammo != null && Ammo.BoltOpen && !_racking)
            {
                // A manual action left open stays open (back to open if the weapon was just drawn again).
                if (_held.SlideOffset <= 0f)
                {
                    _held.SlideOffset = Travel;
                }
            }
            else if (_returning)
            {
                // Spring-driven slides slam home in a few hundredths of a second, jolting the gun forward and down.
                _held.SlideOffset = Mathf.MoveTowards(_held.SlideOffset, 0f, 6f * dt);
                if (_held.SlideOffset <= 0f)
                {
                    _returning = false;
                    _held.Nudge(Rig.AimWorld * 0.22f, 25f);
                }
            }
            else if (!_racking)
            {
                _held.SlideOffset = 0f;
            }

            // Break-open barrels drop open under their weight and snap shut; a released cover settles.
            if (!_coverHeld && Data.Hinge != HingeKind.None)
            {
                float target = Breaks ? (Ammo.Open ? 1f : 0f) : Data.Hinge == HingeKind.FeedCover && Ammo.CoverOpen ? 1f : 0f;
                float speed = target > _held.HingeOpenness ? 5f : 12f;
                _held.HingeOpenness = Mathf.MoveTowards(_held.HingeOpenness, target, speed * dt);
            }

            UpdateEject(dt);
            VRHand off = _sys.Rig.Off;
            UpdateGraspAnchor(off);
            if (_beltHeld && _belt != null && Rig.GunBody != null)
            {
                _belt.Place(BeltStartWorld, off.Palm.position + off.Palm.Forward * 0.02f, Rig.AimWorld);
            }
            if (_palmed != null)
            {
                if (_palmed.Go == null)
                {
                    _palmed = null;
                }
                else
                {
                    _palmed.PalmedPose(off, off.Held as MagazineProp).ApplyTo(_palmed.Go.transform);
                }
            }
            if (off.Held is MagazineProp held)
            {
                if (held.OnRail)
                {
                    held.RailPose(SeatPose).ApplyTo(held.Go.transform);
                }
                else
                {
                    held.FollowHand();
                }
            }
        }

        /// <summary>
        /// The off hand's glove settles onto what it works: thumb and index on the bolt or handle being racked or on a
        /// feed cover's latch, the fist around the handguard. The tracked hand still decides where it is.
        /// </summary>
        private void UpdateGraspAnchor(VRHand off)
        {
            if (off.Model == null)
            {
                return;
            }
            Vector3? anchor = null;
            bool pinch = false;
            if (Rig.GunBody != null)
            {
                switch (off.Role)
                {
                    case HandRole.Slide:
                        if (_racking)
                        {
                            anchor = IsPump ? HandguardPoint(off.Palm.position) : RackPointWorld;
                            pinch = !IsPump;
                        }
                        break;
                    case HandRole.Hinge:
                        if (_coverHeld)
                        {
                            anchor = CoverGrabWorld;
                            pinch = true;
                        }
                        break;
                    case HandRole.Optic:
                        if (_opticHeld)
                        {
                            anchor = OpticGrabWorld;
                            pinch = true;
                        }
                        break;
                    case HandRole.Foregrip:
                        if (!GripData.IsPistol)
                        {
                            anchor = HandguardPoint(off.Palm.position);
                        }
                        break;
                }
            }
            off.Model.GraspAnchor = anchor;
            off.Model.GraspPinch = pinch;
        }

        /// <summary>The point of the handguard (just under the bore, between the grip and the muzzle) nearest to <paramref name="point"/>.</summary>
        private Vector3 HandguardPoint(Vector3 point)
        {
            WeaponRigData d = GripData;
            Vector3 a = GripRig.ToWorld(d.GripPoint + d.AimAxis * 0.1f - d.Up * 0.012f);
            Vector3 b = GripRig.ToWorld(d.MuzzlePoint - d.Up * 0.012f);
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            // Under the bore: the handguard's centre line, a hand's half-width below the sight line.
            return a + ab * t;
        }

        /// <summary>
        /// Autotest: runs this weapon's whole manual reload through the real code paths without hand tracking —
        /// open / eject, take from the pouch, seat, pull a seated magazine by hand and seat it again, feed cover,
        /// rack — and reports what happened.
        /// </summary>
        internal IEnumerator SelfCheck(System.Action<string> report)
        {
            if (!Manual)
            {
                report("manual reload not active for this weapon");
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            if (off.Held != null)
            {
                yield break;
            }
            // The simulated hand keeps its grip closed while it handles magazines, as a player's would.
            VRInput.SimulatedGrab[(int)off.Side] = true;
            try
            {
                foreach (object step in SelfCheckSteps(report))
                {
                    yield return step;
                }
            }
            finally
            {
                VRInput.SimulatedGrab[(int)off.Side] = false;
            }
        }

        private IEnumerable SelfCheckSteps(System.Action<string> report)
        {
            VRHand off = _sys.Rig.Off;
            if (Breaks)
            {
                SetBreakOpen(true);
                yield return new UnityEngine.WaitForSecondsRealtime(0.2f);
                TakeFromPouch(off);
                if (off.Held is MagazineProp shells && AcceptsNow(shells))
                {
                    SeatProp(off, shells);
                }
                SetBreakOpen(false);
                report($"break-open: loaded to {Ammo.MagRounds}/{Ammo.Capacity}");
            }
            else if (Ammo.UsesDetachableMag)
            {
                if (Data.Hinge == HingeKind.FeedCover)
                {
                    Ammo.SetCoverOpen(true);
                    _held.HingeOpenness = 1f;
                }
                if (Ammo.MagInserted)
                {
                    int before = Ammo.MagRounds;
                    EjectMagazine();
                    MagazineProp ejected = _ejecting;
                    yield return new UnityEngine.WaitForSecondsRealtime(0.3f);
                    // It must be findable where it fell, by the same search a reaching hand uses.
                    bool grabbable = ejected != null && ejected.Go != null && ejected.IsDropped
                                     && MagazineProp.NearestDropped(ejected.Go.transform.position, 0.13f, Data.Key) == ejected;
                    if (!grabbable)
                    {
                        report("DROPPED MAGAZINE NOT GRABBABLE");
                    }
                    else if (off.Held == null)
                    {
                        ejected.Attach(off);
                        ejected.FollowHand();
                        if (AcceptsNow(ejected))
                        {
                            SeatProp(off, ejected);
                            report(Ammo.MagRounds == before ? $"dropped magazine picked up and seated, {before} rounds kept" : $"PICKED-UP MAGAZINE LOST ROUNDS ({Ammo.MagRounds}/{before})");
                        }
                    }
                    if (Ammo.MagInserted)
                    {
                        EjectMagazine();
                        yield return new UnityEngine.WaitForSecondsRealtime(0.2f);
                    }
                }
                TakeFromPouch(off);
                if (off.Held is MagazineProp mag && AcceptsNow(mag))
                {
                    SeatProp(off, mag);
                }
                if (Ammo.MagInserted)
                {
                    PullSeatedMagazine(off);
                    yield return null;
                    if (off.Held is MagazineProp pulled && AcceptsNow(pulled))
                    {
                        SeatProp(off, pulled);
                    }
                }
                if (Data.Hinge == HingeKind.FeedCover)
                {
                    if (VRConfig.BeltByHand.Value && Ammo.MagInserted && !Ammo.BeltLaid)
                    {
                        BeginBelt(off);
                        yield return null;
                        CompleteBelt(off);
                    }
                    Ammo.SetCoverOpen(false);
                }
                report($"magazine: {(Ammo.MagInserted ? "seated" : "NOT SEATED")}, {Ammo.MagRounds} rounds{(Ammo.HasCover ? Ammo.BeltLaid ? ", belt laid" : ", BELT NOT LAID" : "")}");
            }
            else if (Data.Action == ActionType.ShellFed || Data.Action == ActionType.SingleShot)
            {
                TakeFromPouch(off);
                if (off.Held is MagazineProp shell && AcceptsNow(shell))
                {
                    SeatProp(off, shell);
                }
                report($"loaded to {Ammo.MagRounds}/{Ammo.Capacity}");
            }
            if (off.Held is MagazineProp leftover)
            {
                Ammo.ReturnSpare(leftover.Rounds);
                leftover.Destroy();
            }
            if (Ammo.UsesChamber)
            {
                Ammo.RackBack();
                _held.Sounds.Play(MechSound.SlideBack, RackPointWorld);
                Ammo.BoltOpen = false;
                Ammo.RackForward();
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld);
                report($"racked: {(Ammo.Chambered ? "chambered" : "NOTHING CHAMBERED")}");
            }
            yield return null;
        }

        /// <summary>
        /// Autotest: H3VR-style tactical reload through the real code. A fresh magazine from the pouch in the off hand,
        /// the release pressed (the old one goes into the same hand), the fresh one seated, the old one still held, then
        /// both let go. Pictures at each step; rounds must add up.
        /// </summary>
        internal IEnumerator TacticalSelfCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesDetachableMag || off.Held != null || Ammo.BoxLocked)
            {
                yield break;
            }
            if (!Ammo.MagInserted)
            {
                TakeFromPouch(off);
                if (off.Held is MagazineProp first && AcceptsNow(first))
                {
                    SeatProp(off, first);
                }
            }
            int before = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0) + (Ammo.InfiniteSpare ? 0 : Ammo.SpareAmmo);
            // The simulated hand keeps its grip closed for the whole check, as a player holding the magazines would.
            VRInput.SimulatedGrab[(int)off.Side] = true;
            TakeFromPouch(off);
            if (!(off.Held is MagazineProp fresh))
            {
                VRInput.SimulatedGrab[(int)off.Side] = false;
                report("tactical reload: no fresh magazine from the pouch");
                yield break;
            }
            MagReadout.TestTarget = fresh;
            yield return new WaitForSecondsRealtime(0.4f); // the readout fades in
            yield return new WaitForEndOfFrame();
            shot("magazine-in-hand");
            bool palmable = CanPalm(off);
            TacticalEject(off);
            MagazineProp old = _palmed;
            yield return new WaitForEndOfFrame();
            shot("tactical-two-magazines");
            if (AcceptsNow(fresh))
            {
                SeatProp(off, fresh);
            }
            bool kept = old != null && ReferenceEquals(off.Held, old);
            yield return new WaitForEndOfFrame();
            shot("tactical-old-magazine-kept");
            int inHand = off.Held is MagazineProp held ? held.Rounds : 0;
            int after = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0) + (Ammo.InfiniteSpare ? 0 : Ammo.SpareAmmo) + inHand;
            MagReadout.TestTarget = null;
            if (off.Held is MagazineProp leftover)
            {
                Ammo.ReturnSpare(leftover.Rounds);
                leftover.Rounds = 0;
                leftover.Destroy();
            }
            VRInput.SimulatedGrab[(int)off.Side] = false;
            report($"tactical reload: {(palmable ? "fresh magazine at the well" : "fresh magazine away from the well (simulated hand)")}, old magazine {(kept ? "kept in the hand" : "NOT KEPT")}, rounds {(Ammo.InfiniteSpare || after == before ? "add up" : $"DO NOT ADD UP ({before} -> {after}: magazine {Ammo.MagRounds}, chamber {(Ammo.Chambered ? 1 : 0)}, spare {Ammo.SpareAmmo}, in hand {inHand}; weapon ammo {Ammo.Weapon.ammo})")}");
        }

        private int TotalRounds(int inHand) => Ammo.MagRounds + (Ammo.Chambered ? 1 : 0) + (Ammo.InfiniteSpare ? 0 : Ammo.SpareAmmo) + inHand;

        /// <summary>
        /// Autotest: moves the simulated off hand so the magazine it holds stands lined up below the well, its tip
        /// <paramref name="below"/> under the seated magazine's base (sideways by <paramref name="aside"/>), moving at
        /// <paramref name="speed"/> into the well.
        /// </summary>
        private void PlaceFreshMag(VRHand off, MagazineProp fresh, RigidPose holdInRaw, float below, float aside, float speed, float tilt = 0f)
        {
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(fresh.InsertDirLocal);
            Vector3 side = seat.TransformDirection(Vector3.Cross(fresh.InsertDirLocal, fresh.FrontLocal).normalized);
            float length = Vector3.Dot(fresh.TipLocal - fresh.BaseLocal, fresh.InsertDirLocal);
            var hold = new RigidPose(seat.position - insert * (length + below) + side * aside, seat.rotation);
            if (tilt != 0f)
            {
                // Tipped forward or back about its own top, which stays where the straight magazine's would be.
                Vector3 tip = hold.TransformPoint(fresh.TipLocal);
                Quaternion q = Quaternion.AngleAxis(tilt, side);
                hold = new RigidPose(tip + q * (hold.position - tip), q * hold.rotation);
            }
            VRHand.Scripted[(int)off.Side] = hold.Multiply(holdInRaw.Inverse());
            VRHand.ScriptedVelocity[(int)off.Side] = insert * speed;
        }

        /// <summary>Autotest: one way of driving a fresh magazine at the seated one.</summary>
        private sealed class KickPass
        {
            public string Name;
            public float Speed;
            public float Tilt;
            public float Aside;
            public bool Knocks = true;
        }

        /// <summary>
        /// The mag kick must work struck fast, pushed slowly and tilted, and must not fire for a magazine that only passes
        /// beside the gun.
        /// </summary>
        private static readonly KickPass[] KickPasses =
        {
            new KickPass { Name = "fast strike", Speed = 0.9f },
            new KickPass { Name = "slow push", Speed = 0.08f },
            new KickPass { Name = "tilted 45 deg", Speed = 0.6f, Tilt = 45f },
            new KickPass { Name = "passing beside the gun", Speed = 0.9f, Aside = 0.2f, Knocks = false }
        };

        /// <summary>
        /// Autotest: the two H3VR reloads through the real hand path, the simulated off hand moved by script. Mag kick: a
        /// fresh magazine driven up into the seated one knocks it out and slides in after it. Tactical: the fresh
        /// magazine waits under the well while the gun hand presses the release; the old one lands in the same hand,
        /// the fresh one goes in.
        /// </summary>
        internal IEnumerator H3vrReloadSelfCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            VRHand dom = _sys.Rig.Dominant;
            if (!Manual || !Ammo.UsesDetachableMag || Ammo.BoxLocked || off.Held != null || off.Role != HandRole.Free)
            {
                report("h3vr reloads: skipped (no detachable magazine or the hand is busy)");
                yield break;
            }
            VRInput.SimulatedGrab[(int)off.Side] = true;
            try
            {
                for (int pass = 0; pass <= KickPasses.Length; pass++)
                {
                    bool kick = pass < KickPasses.Length;
                    KickPass how = kick ? KickPasses[pass] : null;
                    if (kick && !Data.MagKickable)
                    {
                        if (pass == 0)
                        {
                            report("mag kick: this magazine sits too deep in the gun to be knocked out (not kickable)");
                        }
                        continue;
                    }
                    if (!Ammo.MagInserted)
                    {
                        TakeFromPouch(off);
                        if (off.Held is MagazineProp seatFirst && AcceptsNow(seatFirst))
                        {
                            SeatProp(off, seatFirst);
                        }
                    }
                    int oldRounds = Ammo.MagRounds;
                    // Counted without the pouch (a mutator may refill it at any moment): what is loaded, plus the fresh one.
                    int before = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0);
                    TakeFromPouch(off);
                    if (!(off.Held is MagazineProp fresh))
                    {
                        report((kick ? "mag kick" : "tactical reload (h3vr)") + ": NO FRESH MAGAZINE from the pouch");
                        continue;
                    }
                    int freshRounds = fresh.Rounds;
                    before += freshRounds;
                    yield return null;
                    RigidPose holdInRaw = off.Raw.Inverse().Multiply(fresh.HoldPose(off));
                    MagazineProp old = null;
                    bool knocked = false, palmed = false;
                    // Magazines lying around before this pass (earlier passes' knocked-out ones) are not this pass's.
                    var lyingBefore = new System.Collections.Generic.HashSet<MagazineProp>();
                    foreach (MagazineProp m in MagazineProp.All)
                    {
                        if (m.IsDropped)
                        {
                            lyingBefore.Add(m);
                        }
                    }
                    if (kick)
                    {
                        // From 8 cm below, up into (or past) the old magazine at this pass's speed and angle.
                        _seatedAt = -10f; // the seat just above is the test's set-up, not a push the kick must wait out
                        for (float below = 0.08f; below > -0.06f; below -= how.Speed * Mathf.Max(Time.unscaledDeltaTime, 0.005f))
                        {
                            PlaceFreshMag(off, fresh, holdInRaw, below, how.Aside, how.Speed, how.Tilt);
                            yield return null;
                            if (!Ammo.MagInserted)
                            {
                                knocked = true;
                                break;
                            }
                        }
                        yield return new WaitForEndOfFrame();
                        shot(pass == 0 ? "mag-kick" : "mag-kick-" + pass);
                        if (!how.Knocks)
                        {
                            // Only passing by: nothing may have come out. The fresh magazine goes back into the pouch.
                            VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                            report($"mag kick, {how.Name}: {(knocked ? "old magazine NOT LEFT IN THE GUN (knocked out by a magazine that missed its base)" : "nothing knocked out")}");
                            if (off.Held is MagazineProp passed)
                            {
                                Ammo.ReturnSpare(passed.Rounds);
                                passed.Rounds = 0;
                                passed.Destroy();
                            }
                            VRHand.Scripted[(int)off.Side] = null;
                            yield return null;
                            continue;
                        }
                    }
                    else
                    {
                        // Lined up 5 cm under the well, a little aside so it does not strike the old one; the gun hand
                        // presses the release.
                        PlaceFreshMag(off, fresh, holdInRaw, 0.05f, 0.03f, 0f);
                        yield return null;
                        yield return null;
                        VRInput.SimulatePress(DigitalAction.MagRelease, dom.Side);
                        yield return null;
                        yield return null;
                        old = _palmed;
                        palmed = old != null && ReferenceEquals(old.HeldBy, off);
                        knocked = !Ammo.MagInserted;
                        yield return new WaitForEndOfFrame();
                        shot("h3vr-tactical-two-magazines");
                    }
                    // Then up the well until it seats.
                    float length = Vector3.Dot(fresh.TipLocal - fresh.BaseLocal, fresh.InsertDirLocal);
                    float start = kick ? -0.02f : 0.04f;
                    for (float below = start; below > -length - 0.03f && fresh.Go != null && ReferenceEquals(off.Held, fresh); below -= 0.6f * Mathf.Max(Time.unscaledDeltaTime, 0.005f))
                    {
                        PlaceFreshMag(off, fresh, holdInRaw, below, 0f, 0.6f);
                        yield return null;
                    }
                    VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                    yield return null;
                    bool seated = Ammo.MagInserted && Ammo.MagRounds == freshRounds;
                    int inHand = off.Held is MagazineProp h ? h.Rounds : 0;
                    int dropped = 0;
                    if (kick)
                    {
                        foreach (MagazineProp m in MagazineProp.All)
                        {
                            if (m.IsDropped && m.WeaponKey == Data.Key && m.Rounds == oldRounds && !lyingBefore.Contains(m))
                            {
                                dropped = m.Rounds;
                                break;
                            }
                        }
                    }
                    int after = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0) + inHand + dropped;
                    knocked |= kick && dropped > 0; // the kick can land a frame after the strike (the hand pose lags a frame)
                    yield return new WaitForEndOfFrame();
                    shot(kick ? (pass == 0 ? "mag-kick-seated" : $"mag-kick-{pass}-seated") : "h3vr-tactical-seated");
                    string sum = after == before ? "rounds add up" : $"ROUNDS DO NOT ADD UP ({before} -> {after}: magazine {Ammo.MagRounds}, chamber {(Ammo.Chambered ? 1 : 0)}, in hand {inHand}, dropped {dropped})";
                    if (kick)
                    {
                        Core.AutoTest.Metric($"mag kick ({how.Name}): knocked out (0/1)", knocked ? 1 : 0);
                        report($"mag kick, {how.Name}: old magazine {(knocked ? $"knocked out ({oldRounds} rounds)" : "NOT KNOCKED OUT")}, fresh one {(seated ? $"seated ({freshRounds} rounds)" : "NOT SEATED")}, {sum}");
                    }
                    else
                    {
                        report($"tactical reload (h3vr): old magazine {(palmed ? $"in the same hand as the fresh one ({oldRounds} rounds)" : knocked ? "released but NOT IN THE HAND" : "NOT RELEASED")}, fresh one {(seated ? $"seated ({freshRounds} rounds)" : "NOT SEATED")}, then {(inHand > 0 ? $"the old one held ({inHand} rounds)" : "nothing in the hand")}, {sum}");
                    }
                    // Clean up: whatever is left in the hand back into the pouch.
                    if (off.Held is MagazineProp leftover)
                    {
                        Ammo.ReturnSpare(leftover.Rounds);
                        leftover.Rounds = 0;
                        leftover.Destroy();
                    }
                    VRHand.Scripted[(int)off.Side] = null;
                    yield return null;
                }
            }
            finally
            {
                VRHand.Scripted[(int)off.Side] = null;
                VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                VRInput.SimulatedGrab[(int)off.Side] = false;
            }
        }

        /// <summary>
        /// Autotest: the simulated off hand is put on the rack point (slide, bolt, charging handle or pump) and takes it the
        /// way a grab there does; pictured and measured on the part at rest and pulled half way, then let go.
        /// </summary>
        internal IEnumerator RackGripSelfCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesChamber || off.Role != HandRole.Free || off.Held != null || Rig.GunBody == null)
            {
                yield break;
            }
            int side = (int)off.Side;
            RigidPose raw = off.Raw;
            Vector3 palmFromRaw = raw.InverseTransformPoint(off.Palm.position);
            RigidPose At(Vector3 palm) => new RigidPose(palm - raw.rotation * palmFromRaw, raw.rotation);
            VRInput.SimulatedGrab[side] = true;
            try
            {
                VRHand.Scripted[side] = At(RackPointWorld);
                yield return null;
                yield return null;
                BeginRack(off);
                yield return new WaitForSecondsRealtime(0.5f); // the part is measured, the hand closes on it
                yield return new WaitForEndOfFrame();
                shot("rack-grip");
                string atRest = _held.PartClipping(off, out int restBuried, out int restTouching);
                // Half way back along the stroke.
                Vector3 start = RackPointWorld;
                Vector3 pull = Rig.DirToWorld(StrokeDir) * (StrokeLength * 0.5f);
                _held.ResetKick();
                for (int i = 1; i <= 10; i++)
                {
                    VRHand.Scripted[side] = At(start + pull * (i / 10f));
                    yield return null;
                }
                float tug = _held.PeakShift;
                yield return new WaitForSecondsRealtime(0.2f);
                yield return new WaitForEndOfFrame();
                shot("rack-grip-pulled");
                string pulled = _held.PartClipping(off, out int pulledBuried, out _);
                // All the way back, then let go: a spring-driven slide or bolt slams home and jolts the gun.
                for (int i = 11; i <= 20; i++)
                {
                    VRHand.Scripted[side] = At(start + pull * (i / 10f));
                    yield return null;
                }
                _held.ResetKick();
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.35f);
                float jolt = _held.PeakShift;
                Core.AutoTest.Metric("racking: mm the gun moves with the pull", tug * 1000f);
                if (!HandDriven)
                {
                    Core.AutoTest.Metric("racking: mm the gun moves when the slide goes home", jolt * 1000f);
                }
                Core.AutoTest.Metric("rack grip: points inside the part", restBuried);
                Core.AutoTest.Metric("rack grip: fingers on the part (of 4)", restTouching);
                Core.AutoTest.Metric("rack grip pulled half way: points inside the part", pulledBuried);
                string moves = _held.VisibleHandle;
                Core.AutoTest.Metric("rack grip: something visible moves with the pull (0/1)", moves.StartsWith("NOTHING", System.StringComparison.Ordinal) ? 0 : 1);
                report($"rack grip ({(IsPump ? "pump" : HandleStroke ? "charging handle" : "slide/bolt")}, {Data.SlideMethod}): {atRest}; pulled half way: {pulled}; moving with the hand: {moves}; "
                       + $"the gun moved {tug * 1000f:0.0} mm with the pull{(HandDriven ? " (hand-driven: stays open)" : $", {jolt * 1000f:0.0} mm when it went home")}"); 
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            yield return new WaitForSecondsRealtime(0.4f); // the grip opens: the part is let go and returns
        }

        /// <summary>
        /// Autotest: the off hand takes the handguard (as a grab there would) for a picture of both hands on the gun, then
        /// lets go. Reports whether the glove took the weapon's own support pose.
        /// </summary>
        internal IEnumerator TwoHandSelfCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            if (off.Role != HandRole.Free || !GripData.HasSupportPoint)
            {
                yield break;
            }
            VRInput.SimulatedGrab[(int)off.Side] = true;
            off.Role = HandRole.Foregrip;
            _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
            yield return new WaitForSecondsRealtime(0.3f);
            yield return new WaitForEndOfFrame();
            shot("two-handed");
            bool snapped = off.Model != null && off.Model.SnapPose.HasValue;
            HandModel gunHand = _sys.Rig.Dominant.Model;
            if (GripData.IsPistol && off.Model != null && gunHand != null)
            {
                // Cupping a pistol: how much of the support hand is inside the gun hand, closing on it and (as before) not.
                off.Model.Measure(p => gunHand.NearSkin(p, 0f), p => false, out int inside, out _, out _);
                HeldWeapon.LegacySupportSolid = true;
                yield return new WaitForSecondsRealtime(0.35f);
                off.Model.Measure(p => gunHand.NearSkin(p, 0f), p => false, out int before, out _, out _);
                HeldWeapon.LegacySupportSolid = false;
                Core.AutoTest.Metric("support hand on a pistol: points inside the gun hand", inside);
                Core.AutoTest.Metric("support hand on a pistol (closing on the gun only): points inside the gun hand", before);
                report($"support hand round the gun hand: {inside}/72 points inside it (closing on the gun only: {before})");
                yield return new WaitForSecondsRealtime(0.2f);
            }
            VRInput.SimulatedGrab[(int)off.Side] = false;
            yield return null; // the grip opens: the foregrip is let go
            report($"two hands: support hand {(snapped ? "on the weapon's own handguard pose" : "on the tracked hand (no support pose)")}");
        }

        /// <summary>Autotest: one full manual cycle (back and home), as a hand on the bolt or pump would do.</summary>
        internal void CycleForTest()
        {
            if (Ammo == null || !Ammo.UsesChamber)
            {
                return;
            }
            Ammo.BoltOpen = false;
            Ammo.RackBack();
            Ammo.RackForward();
        }

        /// <summary>Weapon is going away: finish what is in progress so no ammo is lost or duplicated.</summary>
        public void Release()
        {
            VRHand off = _sys.Rig.Off;
            _pullingIn = null;
            if (_ejecting != null)
            {
                FinishEject();
            }
            if (_palmed != null)
            {
                _palmed.Drop(off.ThrowVelocity(), off.AngularVelocity);
                _palmed = null;
            }
            if (_racking)
            {
                EndRack(off, chamber: _rackedBack);
            }
            if (_coverHeld)
            {
                EndCover(off);
            }
            if (_beltHeld)
            {
                EndBelt(off);
            }
            if (_opticHeld)
            {
                EndOptic(off);
            }
            _belt?.Destroy();
            _belt = null;
            if (off.Model != null)
            {
                off.Model.GraspAnchor = null;
            }
            if (off.Held is MagazineProp prop)
            {
                // Switching weapons with a magazine in hand: keep holding it (it can be seated once this weapon is
                // drawn again, stowed, or dropped).
                prop.OnRail = false;
                if (prop.Owner == null && prop.WeaponKey == Data.Key)
                {
                    prop.Owner = Ammo;
                }
            }
            _held.SlideOffset = 0f;
        }
    }
}
