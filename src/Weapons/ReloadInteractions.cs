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
        /// <summary>This stroke's slide is held with the palm on it (else pinched).</summary>
        private bool _rackPalm;
        /// <summary>Where along the stroke the hand was last frame (for the tug the pull gives the gun).</summary>
        private float _tugStroke;
        private Vector3 _rackStartGun;
        /// <summary>Where the hand took the pump (gun space, the pump forward): the hand stays there as the pump moves.</summary>
        private Vector3 _pumpGraspRestGun;
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

        private bool Manual => VRConfig.ReloadNow == ReloadMode.Manual && Ammo != null && Ammo.Participates;
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
                Log.InfoOnce("magrel-off:" + Data.Key, $"Magazine release on {Data.Key}: manual reloading is not active for it (mode {VRConfig.ReloadNow}, action {Data.Action}).");
            }
            if (Manual)
            {
                if (Data.Action == ActionType.SingleShot && !Ammo.UsesDetachableMag && Ammo.MagRounds > 0)
                {
                    MagazineProp.KeepLoadedLook(Rig); // its rocket as it sits loaded: the one from the pouch is drawn with it
                }
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
                        if (Data.IsPistol)
                        {
                            dom.Model?.ThumbPress(); // the gun hand's thumb on the magazine release
                        }
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
                if (Breaks && Ammo.Open && !_coverHeld && Time.unscaledTime - _openedAt > 0.35f && dom.AngularVelocity.magnitude > 9f)
                {
                    SetBreakOpen(false);
                }
                // A machine gun's feed cover slapped shut with the open hand, as it is closed in a hurry: a quick downward hit
                // on it (it had to be taken and pushed down all the way).
                if (Ammo.HasCover && Ammo.CoverOpen && !_coverHeld && off.Tracked && off.Role == HandRole.Free && !off.GrabHeld
                    && Vector3.Distance(off.Palm.position, CoverGrabWorld) < 0.1f
                    && Vector3.Dot(off.Velocity - dom.Velocity, -Rig.UpWorld) > 0.8f)
                {
                    Ammo.SetCoverOpen(false);
                    _held.Nudge(-Rig.UpWorld * 0.08f, 8f);
                    _held.Sounds.Play(MechSound.SlideForward, CoverGrabWorld);
                    off.Haptic(0.05f, 110f, 0.8f);
                    dom.Haptic(0.04f, 110f, 0.5f);
                }
                if (VRInput.GetPressed(DigitalAction.SlideRelease, dom.Side) && Ammo.SlideLocked && !_racking)
                {
                    VRInput.Consume(DigitalAction.SlideRelease, dom.Side);
                    // The thumb on the slide or bolt release: the gun hand's on a pistol, the support hand's on the gun otherwise.
                    VRHand presses = !Data.IsPistol && off.Role == HandRole.Foregrip ? off : dom;
                    presses.Model?.ThumbPress();
                    ReleaseSlide();
                }
                else if (VRConfig.TriggerReleasesSlide.Value && Ammo.SlideLocked && !_racking && VRInput.GetPressed(DigitalAction.Fire, dom.Side)
                         && !Util.GameContext.GameMenuOpen)
                {
                    // The trigger on a gun locked open sends the slide or bolt forward, as the slide release would: it
                    // chambers a round from a fresh magazine (or closes on an empty one). That pull does not also fire.
                    ReleaseSlide();
                    Ammo.HoldFireUntilTriggerUp = true;
                }
                if (Ammo.HoldFireUntilTriggerUp && !VRInput.GetHeld(DigitalAction.Fire, dom.Side))
                {
                    Ammo.HoldFireUntilTriggerUp = false;
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
                {
                    // Pulled away with the grip still closed, the support hand stays on (Boneworks style): its glove keeps to
                    // the handguard, a buzz grows with the stretch, and it lets go only well past an arm's slack (60 cm), with
                    // a clear knock. At 35 cm it slipped off in a hard pull.
                    // Measured from where on the handguard the hand is (a long handguard taken far from its support point
                    // let go on the frame it was taken).
                    float stretch = GripData.IsPistol
                        ? Vector3.Distance(off.Palm.position, GripRig.ToWorld(GripData.SupportPoint))
                        : Vector3.Distance(off.Palm.position, HandguardPoint(off.Palm.position));
                    if (!off.GrabHeld || stretch > SupportLetGo)
                    {
                        if (off.GrabHeld)
                        {
                            off.Haptic(0.08f, 60f, 1f);
                        }
                        _held.SetTwoHanded(false);
                        off.Role = HandRole.Free;
                    }
                    else if (stretch > 0.15f && Time.unscaledTime >= _nextStretchBuzz)
                    {
                        float k = Mathf.InverseLerp(0.15f, SupportLetGo, stretch);
                        off.Haptic(0.02f, 80f + 120f * k, 0.15f + 0.6f * k);
                        _nextStretchBuzz = Time.unscaledTime + Mathf.Lerp(0.15f, 0.05f, k);
                    }
                    break;
                }
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
            if (Manual && (Data.Hinge == HingeKind.FeedCover || Data.Hinge == HingeKind.BreakAction && Breaks))
            {
                float d = Vector3.Distance(palm, CoverGrabWorld);
                if (d < radius)
                {
                    Consider(5, d / radius * 0.9f, ref choice, ref best);
                }
            }
            if (Manual && VRConfig.BeltByHand.Value && Ammo.HasCover && Ammo.CoverOpen && Ammo.MagInserted && !Ammo.BeltLaid)
            {
                // At the box mouth, or anywhere along the belt hanging from it (it slipped from the hand).
                float d = Vector3.Distance(palm, BeltStartWorld);
                if (_beltHanging)
                {
                    Vector3 a = BeltStartWorld, b = BeltHangEnd, ab = b - a;
                    float t = Mathf.Clamp01(Vector3.Dot(palm - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
                    d = Mathf.Min(d, Vector3.Distance(palm, a + ab * t));
                }
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
                && (off.GrabPressed || Vector3.Distance(_pressPalm, _sys.Zones.Pouch) < BodyZones.PouchRadius + 0.05f)
                && !_sys.Zones.HolsterNearerThanPouch(palm, _held.Weapon.user != null ? _held.Weapon.user.activeWeaponSlot : -1))
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

            // A pump gun's support hand is on the pump: a grab there works the pump (the gun is held two-handed while it
            // does). Taken as the foregrip instead, pulling it back did nothing ("the pump does not move at all").
            if (choice == 4 && IsPump && Manual && Ammo.UsesChamber && Vector3.Distance(palm, RackPointWorld) < 0.15f)
            {
                choice = 2;
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
            // This gun's kind of magazine only: one picked up from another weapon was taken as the fresh one.
            return off.Held is MagazineProp fresh && fresh.Kind == PropKind.Magazine && fresh.Go != null && fresh.WeaponKey == Data.Key
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
            // From the well into the hand over a moment (it was beside the fresh one the next frame).
            _palmedFrom = prop.Go != null ? RigidPose.Of(prop.Go.transform) : default;
            _palmedAt = prop.Go != null ? Time.unscaledTime : -1f;
            _held.Sounds.Play(MechSound.MagOut, SeatPose.position);
            _sys.Rig.Dominant.Haptic(0.05f, 90f, 0.6f);
            off.Haptic(0.04f, 120f, 0.55f);
        }

        private float _refusedAt = -10f;
        private RigidPose _palmedFrom;
        private float _palmedAt = -1f;

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
            // The hand took it wherever it grabbed (the grab zone is large): the magazine follows the hand's pull from
            // here. Taken from the palm's depth, a grip a little up the magazine read as pushed home and seated it again
            // on the same frame: pulling a magazine out by hand did nothing.
            prop.RailHandOffset = HandAlongRail(off, prop, out _) - prop.RailLength;
            prop.FromSeat = true;
            prop.MovingIn = false;
            prop.TurnDepth = prop.RailDepth;
            prop.PrevDepth = prop.RailDepth;
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
                // The cases fired since it was last opened fall out, live rounds stay (opened again, nothing more falls).
                int spent = Ammo.TakeSpentCases();
                if (spent > 0)
                {
                    Casings.Drop(_held, spent, SeatPose.position);
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
            if (!off.GrabHeld || Vector3.Distance(off.Palm.position, CoverGrabWorld) > 0.35f)
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
            if (Breaks)
            {
                return; // barrels settle where they were left: open if opened, shut otherwise
            }
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
        /// <summary>The belt slipped from the hand (pulled out to its length): it hangs from the box mouth until taken again.</summary>
        private bool _beltHanging;
        /// <summary>The belt was laid by hand on a gun that has no belt of its own in its model: the strip stays on the tray.</summary>
        private bool _beltShownLaid;
        /// <summary>How far the belt end comes out of the box (about 28 rounds).</summary>
        private const float BeltReach = 0.32f;

        /// <summary>Where the end of a belt hanging from the box mouth is.</summary>
        private Vector3 BeltHangEnd => BeltStartWorld + Vector3.down * (BeltReach * 0.65f) + Rig.AimWorld * 0.03f;

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
            _beltHanging = false;
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
            if (!off.GrabHeld)
            {
                EndBelt(off); // let go: it falls back into the box
                return;
            }
            if (Vector3.Distance(off.Palm.position, BeltStartWorld) > BeltReach + 0.06f)
            {
                // Pulled out to its length: it slips from the fingers and hangs from the box, still there to take again.
                EndBelt(off);
                _beltHanging = true;
                _belt?.Show(true);
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
                // On a gun whose model has no belt of its own, the laid strip stays to be seen on the tray.
                _beltShownLaid = _held.BeltPartCount == 0;
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

        /// <summary>
        /// The belt strip: from the box mouth to the hand that pulls it (never longer than the belt comes out), hanging from
        /// the box after it slipped from the hand, or lying on the feed tray once laid on a gun without a belt in its model.
        /// </summary>
        private void UpdateBeltStrip(VRHand off)
        {
            if (!Ammo.MagInserted || !Ammo.HasCover)
            {
                _beltHanging = false;
                _beltShownLaid = false;
            }
            else if (!Ammo.CoverOpen)
            {
                _beltHanging = false; // shut over it: a belt left hanging from the box no longer shows under the cover
            }
            else if (Ammo.BeltLaid)
            {
                _beltHanging = false;
            }
            else
            {
                _beltShownLaid = false;
            }
            bool show = _beltHeld || _beltHanging || _beltShownLaid;
            if (_belt == null || Rig.GunBody == null)
            {
                return;
            }
            _belt.Show(show);
            if (!show)
            {
                return;
            }
            Vector3 from = BeltStartWorld;
            Vector3 to;
            if (_beltHeld)
            {
                to = off.Palm.position + off.Palm.Forward * 0.02f;
                to = from + Vector3.ClampMagnitude(to - from, BeltReach);
            }
            else if (_beltHanging)
            {
                to = BeltHangEnd;
            }
            else
            {
                to = FeedTrayWorld;
            }
            _belt.Place(from, to, Rig.AimWorld, Ammo.MagRounds); // a nearly empty box has only a few rounds of belt
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
                if (kind == PropKind.Rocket)
                {
                    Log.InfoOnce("rocketnone:" + Data.Key, $"No rocket from the pouch for {Data.Key}: {(Ammo.MagRounds > 0 ? "it is still loaded" : "no rockets left")} (spare {Ammo.SpareAmmo}).");
                }
                off.Haptic(0.15f, 35f, 0.7f);
                _sys.FlashMessage(Ammo.SpareAmmo <= 0 && !Ammo.InfiniteSpare ? "NO AMMO" : "FULL");
                return;
            }
            Ammo.TakeSpare(rounds);
            MagazineProp prop = MagazineProp.Create(Rig, kind, rounds);
            prop.Owner = Ammo;
            prop.Attach(off);
            prop.FollowHand();
            if (kind == PropKind.Rocket && prop.Go != null)
            {
                // What the hand holds, once per launcher (a rocket in the hand was reported invisible).
                int drawn = 0;
                float size = 0f;
                foreach (Renderer r in prop.Go.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.enabled && !r.forceRenderingOff)
                    {
                        drawn++;
                        size = Mathf.Max(size, r.bounds.size.magnitude);
                    }
                }
                Log.InfoOnce("rocketheld:" + Data.Key, $"Rocket from the pouch for {Data.Key}: {drawn} drawn renderer(s), {size * 100f:0} cm across, "
                                                       + $"{Vector3.Distance(prop.Go.transform.position, off.Palm.position) * 100f:0} cm from the palm, layer {prop.Go.layer}.");
            }
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
                // Pushed into a well that will not take it (a magazine still seated, a full tube, a loaded launcher): a short
                // blocked knock, at most once a second, instead of nothing at all (85-agent review).
                float refusedAlong = HandAlongRail(off, prop, out float refusedLateral);
                if (refusedLateral < 0.05f && refusedAlong > -0.02f && refusedAlong < prop.RailLength && Time.unscaledTime > _refusedAt + 1f)
                {
                    _refusedAt = Time.unscaledTime;
                    off.Haptic(0.04f, 70f, 0.55f);
                    _sys.Sounds.Play(MechSound.MagLip, SeatPose.position, 0.35f);
                }
                return;
            }

            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(prop.InsertDirLocal);
            RigidPose hold = prop.HoldPose(off);
            float along = HandAlongRail(off, prop, out float lateral);

            if (!prop.OnRail)
            {
                prop.RailHandOffset = 0f;
                prop.RailLateral0 = 0f;
                prop.FromSeat = false;
                float angle = Vector3.Angle(hold.TransformDirection(prop.InsertDirLocal), insert);
                // A pistol's well is in the gun hand's grip: the controllers meet before the magazine is quite lined up, so
                // it is taken a little more loosely there.
                bool grip = prop.Kind == PropKind.Magazine && WellInGrip;
                // A rocket is lined up at the launcher's mouth, an arm's length out on a shouldered launcher: taken more
                // loosely too (it was close to impossible to start).
                // Only at the tube's mouth: a rocket goes in at the front, its tail first (user, 2026-10-16); held beside the
                // tube with its tail level with the middle, it was taken into the tube from the side.
                bool rocket = prop.Kind == PropKind.Rocket;
                bool lined = lateral < (grip ? 0.045f : rocket ? 0.06f : 0.035f) && along > -0.03f && along < (rocket ? RocketTakenIn : prop.RailLength)
                             && angle < (grip || rocket ? 40f : 35f);
                if (rocket && !lined && LooselyLined(prop, hold, seat, insert, out float looseDepth))
                {
                    // A rocket held like a stick (along the fist, either end first) at the launcher's mouth: onto the line
                    // as it is, and from there it follows the hand along the tube. It had to be held with its tail out of
                    // the top of the fist within 40 degrees of the tube, and scoped RPGs "could not be reloaded" (Tasks 231).
                    lined = true;
                    prop.RailHandOffset = along - looseDepth;
                    prop.RailLateral0 = lateral;
                    along = looseDepth;
                }
                if (lined)
                {
                    prop.OnRail = true;
                    prop.RailDepth = Mathf.Clamp(along, 0f, prop.RailLength);
                    prop.LastTick = prop.RailDepth;
                    prop.MovingIn = false;
                    prop.TurnDepth = prop.RailDepth;
                    off.Haptic(0.015f, 180f, 0.3f);
                    if (prop.Kind == PropKind.Magazine)
                    {
                        _sys.Sounds.Play(MechSound.MagLip, SeatPose.position, 0.5f); // onto the well's lip
                    }
                }
                return;
            }

            if (_pullingIn == prop)
            {
                // Started straight into a well inside the grip: the rail takes it home (see below), whatever the hand does.
                // A damped spring (about a third of a second, at most 0.6 m/s), not a constant 0.9 m/s: the hand turning
                // from holding it into pushing it home (PushPalm) was over in a tenth of a second and never seen.
                prop.RailDepth = Mathf.SmoothDamp(prop.RailDepth, prop.RailLength, ref prop.DepthVel, 0.09f, 0.6f, Mathf.Min(Time.unscaledDeltaTime, 0.05f));
                if (prop.RailDepth >= prop.RailLength - 0.001f)
                {
                    _pullingIn = null;
                    SeatProp(off, prop);
                }
                return;
            }
            // On the rail: the hand pushes the prop along the axis only (from where it took it, when pulled out).
            along -= prop.RailHandOffset;
            // A rocket in the tube is held by the tube: it comes out only the way it went in, back out of the mouth. The hand
            // off its line leaves it on the line (it came off sideways through the tube's wall) until the hand is 35 cm off.
            bool inTube = prop.Kind == PropKind.Rocket && prop.RailDepth > 0.03f;
            if (along < -0.03f || lateral > (inTube ? 0.35f : 0.07f) + prop.RailLateral0)
            {
                prop.OnRail = false;
                prop.RailHandOffset = 0f;
                prop.RailLateral0 = 0f;
                prop.FromSeat = false;
                return;
            }
            prop.PrevDepth = prop.RailDepth;
            prop.RailDepth = Mathf.Clamp(along, 0f, prop.RailLength);
            // Which way it is going, with 3 mm of play: a slow push still turns the hand into the push and a slow pull lets
            // go of it (a millimetre a frame was a speed: slower than 9 cm/s neither ever happened; bug hunt 2026-10-16).
            if (prop.MovingIn ? prop.RailDepth > prop.TurnDepth : prop.RailDepth < prop.TurnDepth)
            {
                prop.TurnDepth = prop.RailDepth; // still going the same way: the turning point goes with it
            }
            else if (Mathf.Abs(prop.RailDepth - prop.TurnDepth) > 0.003f)
            {
                prop.MovingIn = !prop.MovingIn;
                prop.TurnDepth = prop.RailDepth;
            }
            if (prop.FromSeat && prop.RailDepth < prop.RailLength - 0.015f)
            {
                prop.FromSeat = false; // out a little: pushed back now, it seats again
            }
            bool goingIn = prop.RailDepth > prop.PrevDepth + 1e-4f;
            if (Mathf.Abs(prop.RailDepth - prop.LastTick) > 0.01f)
            {
                prop.LastTick = prop.RailDepth;
                off.Haptic(0.006f, 200f, 0.15f);
            }
            if (prop.FromSeat)
            {
                return; // just pulled out of its seat: neither seated again nor drawn home until it comes out a little
            }
            if (prop.RailDepth >= prop.RailLength * 0.96f)
            {
                SeatProp(off, prop);
            }
            else if (goingIn && prop.Kind == PropKind.Magazine && WellInGrip && prop.RailDepth >= prop.RailLength * 0.25f)
            {
                // A pistol's well is in its grip, where the gun hand is: the two controllers meet before the magazine is all
                // the way in. A quarter of the way in and straight, it is drawn home the rest of the way. A rocket is not:
                // it slides into the tube with the hand the whole way, the hand never letting go of it (user, 2026-10-16).
                _pullingIn = prop;
                // On from the hand's speed, but no faster than the spring's own 0.6 m/s (a 2 m/s push seated it in a frame or
                // two; bug hunt 2026-10-16).
                prop.DepthVel = Mathf.Clamp((prop.RailDepth - prop.PrevDepth) / Mathf.Max(Time.unscaledDeltaTime, 1e-3f), 0f, 0.6f);
                off.Haptic(0.012f, 160f, 0.35f);
            }
        }

        /// <summary>How far past the tube's mouth a rocket's tail may be when it is taken onto the line (it goes in at the front).</summary>
        private const float RocketTakenIn = 0.08f;

        /// <summary>
        /// A rocket at the launcher's mouth, loosely held: its tail (the end that goes in) within 12 cm of the tube's line
        /// at the muzzle, the rocket pointing into the tube within 60 degrees. Tail first only, as an RPG is loaded and as the
        /// rocket is drawn on the line: taken by its warhead end it was drawn a rocket's length away from the hand, or
        /// turned over (85-agent review). At the mouth only, no deeper than <see cref="RocketTakenIn"/>: held beside the tube
        /// it was taken in from the side. <paramref name="depth"/>: how far in its tail already is.
        /// </summary>
        private static bool LooselyLined(MagazineProp prop, RigidPose hold, RigidPose seat, Vector3 insert, out float depth)
        {
            Vector3 mouth = seat.TransformPoint(prop.TipLocal) - insert * prop.RailLength;
            Vector3 tail = hold.TransformPoint(prop.TipLocal), nose = hold.TransformPoint(prop.BaseLocal);
            Vector3 rel = tail - mouth;
            float along = Vector3.Dot(rel, insert);
            depth = Mathf.Max(0f, along);
            float lateral = Vector3.ProjectOnPlane(rel, insert).magnitude;
            float angle = Vector3.Angle(tail - nose, insert);
            return lateral < 0.12f && along > -0.12f && along < RocketTakenIn && angle < 60f;
        }

        /// <summary>
        /// How far along the well's rail the held prop's tip is (from the rail's mouth; the rail's length = seated), and how
        /// far off the rail's line.
        /// </summary>
        private float HandAlongRail(VRHand off, MagazineProp prop, out float lateral)
        {
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(prop.InsertDirLocal);
            Vector3 entryTip = seat.TransformPoint(prop.TipLocal) - insert * prop.RailLength;
            Vector3 rel = prop.HoldPose(off).TransformPoint(prop.TipLocal) - entryTip;
            lateral = Vector3.ProjectOnPlane(rel, insert).magnitude;
            return Vector3.Dot(rel, insert);
        }

        /// <summary>The held magazine is under its well and roughly lined up (6 cm aside, 40 degrees): about to go in.</summary>
        private bool LinedUpUnderWell(VRHand off, MagazineProp prop)
        {
            if (prop.Kind != PropKind.Magazine || !AcceptsNow(prop))
            {
                return false;
            }
            float along = HandAlongRail(off, prop, out float lateral);
            float angle = Vector3.Angle(prop.HoldPose(off).TransformDirection(prop.InsertDirLocal), SeatPose.TransformDirection(prop.InsertDirLocal));
            return lateral < 0.06f && angle < 40f && along > -0.06f && along < prop.RailLength;
        }

        /// <summary>How far the support hand may be pulled from the handguard with the grip closed before it lets go.</summary>
        private const float SupportLetGo = 0.6f;
        private float _nextStretchBuzz;

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
            KickOut(off, prop);
            off.Haptic(0.04f, 80f, 0.8f);
            return true;
        }

        /// <summary>
        /// Knocked out by a fresh magazine: the old one comes off at the well's mouth, set aside by its own width (never out
        /// of the inside of the gun or through the fresh one coming up), and flies clear with the strike's push, a sideways
        /// kick away from the gun hand's side, downward, spinning. (It was made at its seat, slid straight out along the
        /// well through the fresh one, and dropped from inside the gun.) The release button still drops it straight out.
        /// </summary>
        private void KickOut(VRHand off, MagazineProp fresh)
        {
            if (_ejecting != null)
            {
                FinishEject();
            }
            int rounds = Ammo.EjectMagazine();
            MagazineProp old = MagazineProp.Create(Rig, PropKind.Magazine, rounds);
            old.Owner = Ammo;
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(old.InsertDirLocal);
            Vector3 up = Rig.UpWorld, aim = Rig.AimWorld;
            Vector3 gunRight = Vector3.Cross(up, aim).normalized;
            // Away from the gun hand: to the left of a right hand's gun, to the right of a left hand's.
            Vector3 side = _sys.Rig.Dominant.IsLeft ? gunRight : -gunRight;
            float width = Mathf.Max(0.02f, old.CrossHalf * 2f);
            old.RailDepth = old.RailLength * 0.15f; // at the mouth
            RigidPose mouth = old.RailPose(seat);
            mouth = new RigidPose(mouth.position + side * width - insert * 0.01f, mouth.rotation);
            mouth.ApplyTo(old.Go.transform);
            Vector3 push = Vector3.ClampMagnitude(off.ThrowVelocity() - _sys.Rig.Dominant.ThrowVelocity(), 2.5f);
            Vector3 velocity = _sys.Rig.Dominant.Velocity + push * 0.5f + side * 1.3f - insert * 0.6f + Vector3.down * 0.5f;
            old.Drop(velocity, aim * Random.Range(4f, 7f) * (Random.value < 0.5f ? -1f : 1f) + Random.insideUnitSphere * 2f);
            _held.Sounds.Play(MechSound.MagOut, SeatPose.position);
            _sys.Sounds.Play(MechSound.MagKnock, mouth.position, 0.9f);
            _held.Nudge(-insert * 0.06f);
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
        // Gentle against the body: running, the hand's world speed is the running speed, and a magazine put back while
        // moving was thrown off the hip instead of stowed.
        private bool Stows(VRHand off) => _sys.Zones.InPouch(off.Palm.position) && off.BodyThrowVelocity().magnitude < 1.2f;

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
                    _sys.ReturnRounds(palmed, palmed.Rounds, palmed.WeaponKey == Data.Key ? Ammo : null);
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
                _sys.ReturnRounds(prop, prop.Rounds, prop.WeaponKey == Data.Key ? Ammo : null);
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
        // The handle's own travel, not clamped: the drawn handle moves all of it, and the hand clamped to 14 cm let go
        // of a 20 cm handle at the end of its stroke.
        private float StrokeLength => HandleStroke ? Data.HandleTravel.magnitude : Travel;
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
        /// <summary>A hand is working the slide, bolt or handle right now.</summary>
        internal bool IsRacking => _racking;

        private float RackZoneDistance(Vector3 palm)
        {
            Vector3 a = RackPointWorld;
            float along = Data.IsPistol && !HandleStroke ? 0.05f : HandleStroke ? 0.012f : 0.03f;
            // A charging handle at the back of the receiver is taken by its T behind the rack point, not inside the gun.
            Vector3 b = a + Rig.AimWorld * (Data.CarveHandle && !Data.IsPistol ? -0.025f : along);
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(palm - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return Vector3.Distance(palm, a + ab * t);
        }

        /// <summary>Bolt, pump and lever actions move only by hand: let go half way and they stay there.</summary>
        private bool HandDriven => Data.CyclesByHand;

        /// <summary>A pump at the handguard: holding it is also the support grip, so both hands aim while pumping.</summary>
        private bool IsPump => Data.CyclesByHand && GripData.HasSupportPoint && !GripData.IsPistol
                               && Vector3.Distance(Rig.ToWorld(Data.RackPoint), GripRig.ToWorld(GripData.SupportPoint)) < 0.15f;

        private void BeginRack(VRHand off)
        {
            _racking = true;
            _returning = false;
            _rackStartGun = Rig.ToGun(off.Palm.position);
            // How the hand takes a pistol's slide, for the whole stroke: the palm on it (over the top or on its side, the
            // fingers round it) when the palm faces the slide across it, else pinched (a slingshot from behind).
            Vector3 palmNormal = off.Palm.rotation * new Vector3(-off.Mirror, 0f, 0f);
            _rackPalm = Data.IsPistol && !HandleStroke && !IsPump && Mathf.Abs(Vector3.Dot(palmNormal, Rig.AimWorld)) < 0.6f
                        && Vector3.Dot(palmNormal, RackPointWorld - off.Palm.position) > -0.01f;
            _rackBase = Ammo.SlideLocked ? 1f : Fraction;
            // An open bolt with an empty chamber (the case already out) feeds when pushed home, however far it was left open:
            // pulled back, half closed and let go, it closed later without feeding and the gun said RACK.
            _rackedBack = Ammo.SlideLocked || Ammo.BoltOpen && (Fraction >= 0.8f || !Ammo.Chambered && !Ammo.SpentInChamber);
            _lastTick = _rackBase * StrokeLength;
            _tugStroke = _lastTick;
            off.Role = HandRole.Slide;
            off.Haptic(0.02f, 160f, 0.35f);
            if (IsPump)
            {
                _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
                _pumpGraspRestGun = Rig.ToGun(HandguardPoint(off.Palm.position)) - Data.SlideTravel.normalized * _held.SlideOffset;
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
                bool spent = Ammo.SpentInChamber;
                bool ejected = Ammo.RackBack();
                _held.Sounds.Play(MechSound.SlideBack, RackPointWorld);
                off.Haptic(0.03f, 140f, 0.6f);
                if (ejected || spent)
                {
                    Casings.Eject(_held, live: ejected); // a live round flies out, or the fired case a bolt or pump held
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
            if (!off.GrabHeld || drift > 0.35f)
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
                // Stays where the hand left it; it only closes when pushed forward. The same line as the stroke's own
                // closing (a tenth of the travel): let go between the two, a chambered pump counted as open and clicked.
                Ammo.BoltOpen = _held.SlideOffset > Travel * 0.1f;
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
            // Press check: eased back part way (not far enough to throw it out), the chambered round shows in the port.
            float open = Ammo != null && Ammo.UsesChamber ? Fraction : 0f;
            Casings.ShowChamber(_held, open > 0.12f && open < 0.8f && (Ammo.Chambered || Ammo.SpentInChamber), Ammo != null && Ammo.Chambered);
            VRHand off = _sys.Rig.Off;
            UpdateGraspAnchor(off);
            UpdateBeltStrip(off);
            if (_palmed != null)
            {
                if (_palmed.Go == null)
                {
                    _palmed = null;
                }
                else
                {
                    RigidPose palmedPose = _palmed.PalmedPose(off, off.Held as MagazineProp);
                    if (off.Held is MagazineProp first)
                    {
                        palmedPose = new RigidPose(palmedPose.position + first.KeptOut, palmedPose.rotation); // with the first one, out of the gun
                    }
                    float into = _palmedAt >= 0f ? Mathf.Clamp01((Time.unscaledTime - _palmedAt) / 0.18f) : 1f;
                    if (into < 1f)
                    {
                        float e = into * into * (3f - 2f * into);
                        palmedPose = new RigidPose(Vector3.Lerp(_palmedFrom.position, palmedPose.position, e), Quaternion.Slerp(_palmedFrom.rotation, palmedPose.rotation, e));
                    }
                    else
                    {
                        _palmedAt = -1f;
                    }
                    palmedPose.ApplyTo(_palmed.Go.transform);
                }
            }
            if (off.Held is MagazineProp held)
            {
                if (held.OnRail)
                {
                    held.ShowOnRail(held.RailPose(SeatPose));
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
            // Which pose the off hand takes: the part it works, two magazines in one hand.
            off.Model.WorkedPart = IsPump ? WorkedPart.Pump : HandleStroke ? WorkedPart.ChargingHandle : Data.IsPistol ? WorkedPart.Slide : WorkedPart.Bolt;
            off.Model.Palming = _palmed != null;
            if (Rig.GunBody != null)
            {
                switch (off.Role)
                {
                    case HandRole.Slide:
                        if (_racking)
                        {
                            Vector3? palmOn = _rackPalm && off.Model.Solid != null
                                ? PalmOnPart(off.Model.Solid, RackPointWorld, off.Palm.rotation * new Vector3(-off.Mirror, 0f, 0f))
                                : null;
                            // On a pump the hand rides on it, from where it took hold: it slid on along the handguard
                            // past the pump's travel before.
                            anchor = IsPump ? Rig.ToWorld(_pumpGraspRestGun + Data.SlideTravel.normalized * _held.SlideOffset) : palmOn ?? RackPointWorld;
                            pinch = !IsPump && !palmOn.HasValue;
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
                    case HandRole.Free:
                        KeepEmptyHandOut(off);
                        break;
                    case HandRole.Magazine:
                        // On the rail, and already while it is lined up under the well: a pistol's rail is only the last
                        // part of the way, and the hand was in the grip before the keep-out began.
                        if (off.Held is MagazineProp seating && (seating.OnRail || LinedUpUnderWell(off, seating)))
                        {
                            KeepOutOfGun(off, seating);
                        }
                        break;
                }
            }
            off.Model.GraspAnchor = anchor;
            // The support hand keeps to the handguard however far the stretch (see the foregrip's let-go); the others settle
            // a few centimetres at most.
            off.Model.GraspReach = off.Role == HandRole.Foregrip && !GripData.IsPistol ? SupportLetGo : 0.05f;
            off.Model.GraspPinch = pinch;
        }

        /// <summary>
        /// Where the palm lies on the part for a palm grip: on the line through the rack point along the palm's normal
        /// (<paramref name="normal"/>, out of the palm), the part's surface on the palm's side (its measured solid, grown by
        /// a finger's thickness, so the palm's skin rests on it). The pinch put the point between thumb and index there,
        /// which held the palm 3 cm off a slide taken over the top, only the fingertips on it (tools/hands/grip_check.py).
        /// Null when the line misses the part.
        /// </summary>
        private static Vector3? PalmOnPart(System.Func<Vector3, bool> solid, Vector3 rack, Vector3 normal)
        {
            const float Step = 0.002f;
            float inside = float.NaN;
            for (int i = 0; i <= 50 && float.IsNaN(inside); i++)
            {
                if (solid(rack + normal * (i * Step)))
                {
                    inside = i * Step;
                }
                else if (i > 0 && solid(rack - normal * (i * Step)))
                {
                    inside = -i * Step;
                }
            }
            if (float.IsNaN(inside))
            {
                return null;
            }
            for (float t = inside; t >= inside - 0.12f; t -= Step)
            {
                if (!solid(rack + normal * t))
                {
                    return rack + normal * t;
                }
            }
            return null;
        }

        private System.Func<Vector3, bool> _keepOutGun, _keepOutBlocked;
        private HandModel _keepOutHand;

        /// <summary>
        /// A magazine sliding into the well slides through the hand that holds it: the glove stops where it would go into
        /// the gun (the well and the receiver round it, a pistol's grip and the gun hand on it) and the magazine goes on
        /// through the fingers, so the hand ends on its lower end. The glove followed it into the gun before (a pistol
        /// magazine, held by most of its length, took the hand up into the grip).
        /// </summary>
        /// <summary>
        /// An empty hand does not pass through the held gun or the hand holding it (Tasks 207): its glove is drawn out of
        /// them the shortest way (away from the bore first). Only the drawing: the tracked hand, and what it can grab, stay
        /// where they are. Its fingers still move freely.
        /// </summary>
        private void KeepEmptyHandOut(VRHand off)
        {
            if (!off.Tracked || off.Held != null || off.Model == null || Rig.GunBody == null
                || (off.Palm.position - _held.GripWorld.position).sqrMagnitude > 0.7f * 0.7f)
            {
                return;
            }
            _keepOutGun = _held.HandsSolid;
            _keepOutHand = _sys.Rig.Dominant != null ? _sys.Rig.Dominant.Model : null;
            if (_keepOutGun == null && _keepOutHand == null)
            {
                return;
            }
            if (_keepOutBlocked == null)
            {
                _keepOutBlocked = p => _keepOutGun != null && _keepOutGun(p) || _keepOutHand != null && _keepOutHand.NearSkin(p, GunVolume.Grow);
            }
            Vector3 bore = Rig.ToWorld(Data.MuzzlePoint);
            Vector3 away = Vector3.ProjectOnPlane(off.Palm.position - bore, Rig.AimWorld);
            off.Model.KeepOut = off.Model.ShortestWayOut(_keepOutBlocked, 0.1f, away);
            off.Model.KeepOutFrame = Time.frameCount;
        }

        private void KeepOutOfGun(VRHand off, MagazineProp prop)
        {
            _keepOutGun = _held.HandsSolid;
            _keepOutHand = _sys.Rig.Dominant != null ? _sys.Rig.Dominant.Model : null;
            if (_keepOutGun == null && _keepOutHand == null)
            {
                return;
            }
            if (_keepOutBlocked == null)
            {
                _keepOutBlocked = p => _keepOutGun != null && _keepOutGun(p) || _keepOutHand != null && _keepOutHand.NearSkin(p, GunVolume.Grow);
            }
            Vector3 insert = SeatPose.TransformDirection(prop.InsertDirLocal);
            float length = Vector3.Distance(prop.BaseLocal, prop.TipLocal);
            float back = off.Model.ClearanceAlong(insert, _keepOutBlocked, Mathf.Min(0.12f, length));
            off.Model.KeepOut = -insert * back;
            off.Model.KeepOutFrame = Time.frameCount;
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
        /// Autotest film of a launcher's reload by hand: a rocket from the pouch, carried to the launcher's mouth, lined up
        /// 25 cm in front of it and pushed in, the way a player does. Reports whether the rocket in the hand is drawn, whether
        /// it went onto the line and whether it loaded (Tasks 231: scoped RPGs could not be reloaded, the rocket invisible).
        /// </summary>
        /// <summary>Autotest (-rfvr-deep): the launcher emptied and reloaded by hand, as the rocket film does, without filming.</summary>
        internal IEnumerator RocketCheck(System.Action<string> report)
        {
            if (Ammo.MagRounds > 0 && Manual)
            {
                Ammo.ReturnSpare(Ammo.MagRounds); // as if fired: the rocket goes back to the pouch
                Ammo.MagRounds = 0;
                yield return null;
            }
            yield return RocketFilm(report, _ => { });
        }

        private IEnumerator RocketFilm(System.Action<string> report, System.Action<string> frame)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("rocket film: skipped (manual reloading off, or the hand is busy)");
                yield break;
            }
            if (Ammo.MagRounds > 0)
            {
                report("rocket film: skipped (still loaded)");
                yield break;
            }
            int side = (int)off.Side;
            // The launcher's measured shape is what the rocket is kept out of: up to 6 s for it (measured a moment after the
            // launcher is held at rest), or the side probes below measure nothing.
            for (float waited = 0f; _held.HandsSolid == null && waited < 6f; waited += Time.unscaledDeltaTime)
            {
                yield return null;
            }
            bool solidReady = _held.HandsSolid != null;
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            VRInput.SimulatedGrab[side] = true;
            TakeFromPouch(off);
            if (!(off.Held is MagazineProp rocket) || rocket.Go == null)
            {
                VRInput.SimulatedGrab[side] = false;
                report("rocket film: NO ROCKET FROM THE POUCH");
                yield break;
            }
            float drawnSize = 0f;
            foreach (Renderer r in rocket.Go.GetComponentsInChildren<Renderer>(false))
            {
                if (r.enabled && !r.forceRenderingOff)
                {
                    drawnSize = Mathf.Max(drawnSize, r.bounds.size.magnitude);
                }
            }
            // The rocket in the hand is the launcher's own: its meshes are those the launcher shows loaded (an RPG-7V
            // variant's held all five warheads, the scoped one a made stand-in; user, 2026-10-16).
            string model = RocketModel(rocket);
            // The palm that holds the rocket lined up at the mouth (depth 0 on the line), as the hand holds it.
            float depth0 = rocket.RailDepth;
            rocket.RailDepth = 0f;
            RigidPose entry = rocket.RailPose(SeatPose);
            rocket.RailDepth = depth0;
            RigidPose palmAtEntry = entry.Multiply(rocket.HoldPose(off).Inverse().Multiply(off.Palm));
            Vector3 insert = SeatPose.TransformDirection(rocket.InsertDirLocal);
            // As a real hand holds it: 45 degrees off the tube's line, turned about the rocket's tail at the mouth (the
            // strict line-up wanted 40 degrees at most; the loose one takes it).
            Vector3 tailAtMouth = entry.TransformPoint(rocket.TipLocal);
            Quaternion tilt = Quaternion.AngleAxis(45f, Vector3.Cross(insert, Vector3.up).sqrMagnitude > 1e-4f ? Vector3.Cross(insert, Vector3.up).normalized : Vector3.right);
            palmAtEntry = new RigidPose(tailAtMouth + tilt * (palmAtEntry.position - tailAtMouth), tilt * palmAtEntry.rotation);
            float rail = rocket.RailLength;
            System.Func<float, RigidPose> along = d => new RigidPose(palmAtEntry.position + insert * d, palmAtEntry.rotation);
            // The palm that holds the rocket at a pose of its own (the hand holds it as HoldPose puts it).
            RigidPose holdToPalm = rocket.HoldPose(off).Inverse().Multiply(off.Palm);
            RigidPose seatedPose = SeatPose;
            Vector3 aimW = Rig.AimWorld, upW = Rig.UpWorld, rightW = Vector3.Cross(upW, aimW).normalized;
            Vector3 midLocal = (rocket.TipLocal + rocket.BaseLocal) * 0.5f;
            Quaternion toLocalFrame = Quaternion.Inverse(Quaternion.LookRotation(rocket.InsertDirLocal, rocket.FrontLocal));
            System.Func<Vector3, Vector3, Vector3, RigidPose> palmHolding = (mid, axisW, frontW) =>
            {
                Quaternion r = Quaternion.LookRotation(axisW, frontW) * toLocalFrame;
                return new RigidPose(mid - r * midLocal, r).Multiply(holdToPalm);
            };
            Vector3 tubeMid = seatedPose.TransformPoint(rocket.TipLocal); // the seated tail: inside the tube, behind its mouth
            RigidPose start = off.Palm;
            bool watchBack = false, linedSeen = false;
            // Into the launcher's side (beside the tube, then across it from above): how much of the drawn rocket is inside the
            // launcher, and whether it was taken in from there (user, 2026-10-16: it went through the launcher).
            bool watchSide = false, fromSide = false, pushing = false, loadedWhilePushing = false;
            int sideInside = 0, sideSamples = 0;
            float lead = 0f, handAtLoad = -1f;
            float linedAhead = float.NaN;
            Vector3 aimG = Data.AimAxis.normalized;
            float tubeFrontG = MagazineProp.TubeFront(Rig, aimG, out float tfG) ? tfG : Vector3.Dot(Data.MuzzlePoint, aimG);
            bool fromBehind = false;
            void Watch()
            {
                if (!(off.Held is MagazineProp held))
                {
                    return;
                }
                if (watchBack && (held.OnRail || Ammo.MagRounds > 0))
                {
                    fromBehind = true; // every frame of the probe at the back, not only its end
                }
                if (watchSide)
                {
                    fromSide |= held.OnRail || Ammo.MagRounds > 0;
                    int inside = 0;
                    const int k = 24;
                    for (int i = 0; i <= k; i++)
                    {
                        inside += _held.InsideDrawnGun(held.Go.transform.TransformPoint(Vector3.Lerp(held.BaseLocal, held.TipLocal, i / (float)k))) ? 1 : 0;
                    }
                    sideInside = Mathf.Max(sideInside, inside);
                    sideSamples = k + 1;
                }
                if (pushing && held.OnRail)
                {
                    // The rocket goes in with the hand: never ahead of where the hand has pushed it.
                    float hand = Mathf.Clamp(HandAlongRail(off, held, out _) - held.RailHandOffset, 0f, held.RailLength);
                    lead = Mathf.Max(lead, held.RailDepth - hand);
                }
                if (!watchBack && !linedSeen && held.OnRail)
                {
                    // Where the hand's rocket tail was when it went onto the line, against the tube's front end.
                    linedSeen = true;
                    linedAhead = Vector3.Dot(Rig.ToGun(held.HoldPose(off).TransformPoint(held.TipLocal)), aimG) - tubeFrontG;
                }
            }
            IEnumerator Move(System.Func<float, RigidPose> pose, float seconds, string note)
            {
                float t0 = Time.unscaledTime;
                for (float k = 0f; k < 1f; k = (Time.unscaledTime - t0) / seconds)
                {
                    float s = k * k * (3f - 2f * k);
                    VRHand.Scripted[side] = pose(s).Multiply(rawFromPalm);
                    yield return new WaitForEndOfFrame();
                    Watch();
                    frame(note);
                    yield return null;
                }
                VRHand.Scripted[side] = pose(1f).Multiply(rawFromPalm);
            }
            bool onLine = false, loaded = false;
            // Where the rocket is lined up, against the tube's front end: an RPG is loaded from the front (user, 2026-10-16).
            Vector3 aimGun = Data.AimAxis.normalized;
            float tubeFront = MagazineProp.TubeFront(Rig, aimGun, out float tf) ? tf : Vector3.Dot(Data.MuzzlePoint, aimGun);
            float mouthAhead = Vector3.Dot(Rig.ToGun(tailAtMouth), aimGun) - tubeFront;
            try
            {
                // At the back first: the rocket's tail held 40 cm behind where it sits seated must not be taken.
                RigidPose behind = along(rail + 0.4f);
                watchBack = true;
                yield return Move(s => new RigidPose(Vector3.Lerp(start.position, behind.position, s), Quaternion.Slerp(start.rotation, behind.rotation, s)), 0.6f, "at the back");
                yield return Move(s => along(rail + 0.4f - 0.1f * s), 0.3f, "at the back");
                watchBack = false;
                if (off.Held is MagazineProp offRail)
                {
                    offRail.OnRail = false;
                }
                // Beside the tube, lying along it as it sits seated, pushed sideways into it; then held across the tube and
                // brought down onto it from above.
                start = off.Palm;
                RigidPose besideOut = seatedPose.Multiply(new RigidPose(Quaternion.Inverse(seatedPose.rotation) * (rightW * 0.3f), Quaternion.identity)).Multiply(holdToPalm);
                yield return Move(s => new RigidPose(Vector3.Lerp(start.position, besideOut.position, s), Quaternion.Slerp(start.rotation, besideOut.rotation, s)), 0.6f, "beside the tube");
                watchSide = true;
                yield return Move(s =>
                {
                    RigidPose p = seatedPose.Multiply(new RigidPose(Quaternion.Inverse(seatedPose.rotation) * (rightW * (0.3f * (1f - s))), Quaternion.identity));
                    return p.Multiply(holdToPalm);
                }, 0.7f, "into the tube's side");
                watchSide = false;
                if (off.Held is MagazineProp offRail2)
                {
                    offRail2.OnRail = false;
                }
                start = off.Palm;
                RigidPose above = palmHolding(tubeMid + upW * 0.35f, rightW, aimW);
                yield return Move(s => new RigidPose(Vector3.Lerp(start.position, above.position, s), Quaternion.Slerp(start.rotation, above.rotation, s)), 0.5f, "across the tube");
                watchSide = true;
                yield return Move(s => palmHolding(tubeMid + upW * (0.35f * (1f - s)), rightW, aimW), 0.6f, "down across the tube");
                watchSide = false;
                if (off.Held is MagazineProp offRail3)
                {
                    offRail3.OnRail = false;
                }
                start = off.Palm;
                RigidPose before = along(-0.25f);
                yield return Move(s => new RigidPose(Vector3.Lerp(start.position, before.position, s), Quaternion.Slerp(start.rotation, before.rotation, s)), 0.8f, "to the launcher");
                yield return Move(s => along(-0.25f + 0.25f * s), 0.45f, "lining up");
                onLine = off.Held is MagazineProp lined && lined.OnRail;
                pushing = true;
                float pushT0 = Time.unscaledTime;
                for (float k = 0f; k < 1f && !loadedWhilePushing; k = (Time.unscaledTime - pushT0) / 1.0f)
                {
                    float s = k * k * (3f - 2f * k);
                    VRHand.Scripted[side] = along(rail * s + 0.01f).Multiply(rawFromPalm);
                    yield return new WaitForEndOfFrame();
                    Watch();
                    frame("pushing in");
                    yield return null;
                    if (Ammo.MagRounds > 0)
                    {
                        loadedWhilePushing = true; // loaded with the grip still closed: where the hand was then
                        handAtLoad = s;
                    }
                }
                pushing = false;
                if (!loadedWhilePushing)
                {
                    yield return Move(s => along(rail + 0.01f), 0.2f, "pushing in");
                    loadedWhilePushing = Ammo.MagRounds > 0;
                    handAtLoad = loadedWhilePushing ? 1f : -1f;
                }
                onLine |= Ammo.MagRounds > 0;
                loaded = Ammo.MagRounds > 0;
                yield return Move(s => along(rail + 0.01f), 0.25f, "loaded, still held");
                VRInput.SimulatedGrab[side] = false;
                yield return Move(s => along(rail - 0.15f * s), 0.35f, "hand away");
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            if (off.Held is MagazineProp left)
            {
                Ammo.ReturnSpare(left.Rounds);
                left.Rounds = 0;
                left.Destroy();
            }
            string atHand = float.IsNaN(linedAhead) ? "" : $" (the hand's rocket {Mathf.Abs(linedAhead) * 100f:0} cm {(linedAhead >= 0f ? "in front of" : "inside")} it as it went on)";
            report($"rocket film: rocket in the hand {(drawnSize > 0.12f ? $"drawn ({drawnSize * 100f:0} cm)" : drawnSize > 0f ? $"TOO SMALL ({drawnSize * 100f:0} cm)" : "NOT DRAWN")}, {model}, "
                   + $"lined up {Mathf.Abs(mouthAhead) * 100f:0} cm {(mouthAhead >= 0f ? "in front of" : "inside")} the tube's front{(mouthAhead < -0.05f ? " (NOT AT THE MUZZLE)" : "")}{atHand}, "
                   + $"{(fromBehind ? "TAKEN FROM THE BACK" : "not taken at the back")}, "
                   + $"pushed into the launcher's side{(solidReady ? "" : " (NO SOLID: the launcher was never measured)")}: {sideInside}/{sideSamples} points of it inside the launcher{(sideInside > 2 ? " (PASSES THROUGH THE LAUNCHER)" : "")}"
                   + $"{(fromSide ? ", TAKEN IN FROM THE SIDE" : ", not taken in from the side")}, "
                   + $"{(onLine ? "went onto the launcher's line" : "NEVER LINED UP at the mouth")}, "
                   + $"slid in {(lead > 0.03f ? $"AHEAD OF THE HAND by up to {lead * 100f:0} cm" : $"with the hand (at most {lead * 100f:0.0} cm ahead)")}, "
                   + $"{(loaded ? loadedWhilePushing ? $"loaded with the hand on it ({handAtLoad * 100f:0}% of the push)" : "loaded" : "NOT LOADED")}");
            Core.AutoTest.Metric("rocket: points of a rocket pushed into the launcher's side that are inside it", sideInside);
            Core.AutoTest.Metric("rocket: how far ahead of the hand it slid in (cm)", lead * 100f);
        }

        /// <summary>
        /// A magazine on its way into the well, measured: how far the hand has turned into the push on its base plate, how
        /// much of the free glove is inside the gun hand, and how much of what has gone in lies outside the gun's own shape
        /// (a magazine going up beside the grip instead of into it: drawn over the grip's outside on the 1911, GLOCK 43X,
        /// P320s; user, 2026-10-16; the line check could not see it: it measured the magazine against its own rail).
        /// </summary>
        private string SeatingState(VRHand off, MagazineProp prop)
        {
            var sb = new System.Text.StringBuilder();
            float frac = prop.OnRail ? prop.RailDepth / Mathf.Max(prop.RailLength, 1e-4f) : 0f;
            sb.Append($"rail {frac * 100f:0}%, push {(off.Model != null ? off.Model.PushWeight : 0f):0.00}");
            HandModel gunHand = _sys.Rig.Dominant != null ? _sys.Rig.Dominant.Model : null;
            if (off.Model != null && gunHand != null)
            {
                off.Model.Measure(p => gunHand.NearSkin(p, 0f), p => false, out int inHand, out int samples, out _);
                if (inHand >= 0)
                {
                    sb.Append($", glove in the gun hand {inHand}/{samples}{(inHand > 4 ? " (GLOVES OVERLAP)" : "")}");
                    Core.AutoTest.Metric("magazine (deep): free glove points inside the gun hand while seating", inHand);
                }
            }
            if (prop.Go != null && prop.OnRail && prop.RailDepth > 0.01f)
            {
                // Which way it comes in: its line tipped out of the gun's side plane comes up beside the grip, not into it.
                Vector3 insertW = SeatPose.TransformDirection(prop.InsertDirLocal).normalized;
                Vector3 rightW = Vector3.Cross(Rig.UpWorld, Rig.AimWorld).normalized;
                float tilt = Mathf.Asin(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(insertW, rightW)))) * Mathf.Rad2Deg;
                sb.Append($", its line {tilt:0} deg out of the gun's side plane{(tilt > 8f ? " (COMES IN FROM THE SIDE)" : "")}");
                // What has gone in: the part past the well's mouth. Where the mouth is comes from the magazine seated (how
                // far down from its top it is still inside the gun from the side); the part of it that is in now should be
                // inside the gun's drawn shape. Counted as all the rail had gone in, a rifle magazine's top still under the
                // well read as beside it (100% on rifles whose magazines went in straight).
                RigidPose seated = SeatPose;
                float length = Vector3.Dot(prop.TipLocal - prop.BaseLocal, prop.InsertDirLocal);
                float inSeated = 0f;
                for (float s = 0f; s <= length; s += 0.002f)
                {
                    if (!_held.InsideGunFromSide(seated.TransformPoint(prop.TipLocal - prop.InsertDirLocal * s)))
                    {
                        break;
                    }
                    inSeated = s;
                }
                float inDepth = inSeated - (prop.RailLength - prop.RailDepth);
                if (inSeated < 0.005f)
                {
                    sb.Append(", seated it is not inside the gun from the side (SIDE TEST OR SEAT WRONG)");
                }
                else if (inDepth > 0.005f)
                {
                    Transform t = prop.Go.transform;
                    Vector3 side = Vector3.Cross(prop.InsertDirLocal, prop.FrontLocal).normalized;
                    Bounds box = MagBoxLocal(prop);
                    int inside = 0, total = 0, insideSeated = 0;
                    for (int a = 0; a < 5; a++)
                    {
                        for (int f = -1; f <= 1; f++)
                        {
                            for (int s = -1; s <= 1; s++)
                            {
                                Vector3 local = prop.TipLocal - prop.InsertDirLocal * (inDepth * (0.1f + 0.8f * a / 4f))
                                                + prop.FrontLocal * (f * Mathf.Abs(Vector3.Dot(box.extents, Abs(prop.FrontLocal))) * 0.6f)
                                                + side * (s * Mathf.Abs(Vector3.Dot(box.extents, Abs(side))) * 0.6f);
                                total++;
                                inside += _held.InsideGunFromSide(t.TransformPoint(local)) ? 1 : 0;
                                insideSeated += _held.InsideGunFromSide(seated.TransformPoint(local)) ? 1 : 0;
                            }
                        }
                    }
                    float outside = total > 0 ? 1f - inside / (float)total : 0f;
                    float outsideSeated = total > 0 ? 1f - insideSeated / (float)total : 0f;
                    Core.AutoTest.Metric("magazine (deep): share of what has gone in that shows beside the gun's sides", outside * 100f);
                    sb.Append($", {inDepth * 100f:0.0} cm in past the well's mouth ({inSeated * 100f:0.0} cm seated): {outside * 100f:0}% of it shows beside the gun's sides "
                              + $"(the same points seated: {outsideSeated * 100f:0}%){(outside > 0.25f && outside > outsideSeated + 0.15f ? " (GOES IN BESIDE THE WELL)" : "")}");
                }
                else
                {
                    sb.Append($", not past the well's mouth yet ({inSeated * 100f:0.0} cm of it goes in)");
                }
            }
            return sb.ToString();
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        private static Bounds MagBoxLocal(MagazineProp prop)
        {
            Vector3 mid = (prop.TipLocal + prop.BaseLocal) * 0.5f;
            Vector3 size = new Vector3(0.03f, 0.03f, 0.03f);
            if (prop.Go != null)
            {
                Matrix4x4 toRoot = prop.Go.transform.worldToLocalMatrix;
                bool any = false;
                var b = new Bounds(mid, Vector3.zero);
                foreach (MeshFilter mf in prop.Go.GetComponentsInChildren<MeshFilter>())
                {
                    if (mf.sharedMesh == null)
                    {
                        continue;
                    }
                    Bounds mb = mf.sharedMesh.bounds;
                    Matrix4x4 m = toRoot * mf.transform.localToWorldMatrix;
                    for (int i = 0; i < 8; i++)
                    {
                        Vector3 c = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f));
                        b.Encapsulate(m.MultiplyPoint3x4(c));
                        any = true;
                    }
                }
                if (any)
                {
                    return b;
                }
            }
            return new Bounds(mid, size);
        }

        /// <summary>
        /// Whether the rocket in the hand is drawn with the launcher's own meshes (those it shows loaded): "the launcher's own
        /// rocket", or NOT THE LAUNCHER'S ROCKET with what it is instead.
        /// </summary>
        private string RocketModel(MagazineProp rocket)
        {
            var own = new HashSet<string>();
            IEnumerable<Renderer> loaded = Rig.MagShownLoaded != null && Rig.MagShownLoaded.Count > 0
                ? (IEnumerable<Renderer>)Rig.MagShownLoaded
                : Rig.MagazineParts.Where(p => p != null).SelectMany(p => p.GetComponentsInChildren<Renderer>(true));
            foreach (Renderer r in loaded)
            {
                MeshFilter mf = r != null ? r.GetComponent<MeshFilter>() : null;
                string mesh = mf != null && mf.sharedMesh != null ? mf.sharedMesh.name : r is SkinnedMeshRenderer smr && smr.sharedMesh != null ? smr.sharedMesh.name : null;
                if (mesh != null)
                {
                    own.Add(mesh);
                }
            }
            string ownIs = "the launcher's own rocket";
            if (own.Count == 0)
            {
                // A missile tube shows nothing loaded: its rocket is the projectile it fires.
                GameObject projectile = Rig.Weapon != null && Rig.Weapon.configuration != null ? Rig.Weapon.configuration.projectilePrefab : null;
                if (projectile != null)
                {
                    foreach (MeshFilter mf in projectile.GetComponentsInChildren<MeshFilter>(true))
                    {
                        if (mf.sharedMesh != null)
                        {
                            own.Add(mf.sharedMesh.name);
                        }
                    }
                    ownIs = "the launcher's own projectile";
                }
            }
            var held = new List<string>();
            foreach (MeshFilter mf in rocket.Go.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null)
                {
                    held.Add(mf.sharedMesh.name.Replace(" (baked)", ""));
                }
            }
            if (held.Count == 0)
            {
                return "NOT THE LAUNCHER'S ROCKET (nothing drawn)";
            }
            bool match = held.Any(own.Contains);
            bool extra = held.Any(h => !own.Contains(h) && !h.StartsWith("Capsule", System.StringComparison.Ordinal));
            if (match && !extra)
            {
                return $"{ownIs} ({string.Join(", ", held.Distinct().ToArray())})";
            }
            return match ? $"NOT THE LAUNCHER'S ROCKET alone (also {string.Join(", ", held.Where(h => !own.Contains(h)).Distinct().ToArray())})"
                         : $"NOT THE LAUNCHER'S ROCKET ({string.Join(", ", held.Distinct().ToArray())}; it shows {string.Join(", ", own.ToArray())})";
        }

        /// <summary>
        /// Autotest film (-rfvr-flashfilm): the off hand pulls the seated magazine out of the well by hand, holds it, and
        /// pushes it back in, the way a player does: the hand a little turned and off the well's line, as a real hand is.
        /// <paramref name="frame"/> is called on every frame (it films the close-up).
        /// </summary>
        internal IEnumerator ReloadFilm(System.Action<string> report, System.Action<string> frame)
        {
            if (Data.Action == ActionType.SingleShot && !Ammo.UsesDetachableMag)
            {
                yield return RocketFilm(report, frame);
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesDetachableMag || Ammo.BoxLocked || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("reload film: skipped (no detachable magazine by hand, or the hand is busy)");
                yield break;
            }
            if (!Ammo.MagInserted)
            {
                TakeFromPouch(off);
                if (off.Held is MagazineProp m0 && AcceptsNow(m0))
                {
                    SeatProp(off, m0);
                }
                yield return null;
            }
            if (!Ammo.MagInserted || off.Held != null)
            {
                report("reload film: no magazine seated");
                yield break;
            }
            int side = (int)off.Side;
            RigidPose palm0 = off.Palm, raw0 = off.Raw;
            RigidPose rawFromPalm = palm0.Inverse().Multiply(raw0);
            // Where a palm holds this magazine seated (as the hand holds one), from a stand-in made like the pulled one.
            MagazineProp probe = MagazineProp.Create(Rig, PropKind.Magazine, 0);
            probe.RailDepth = probe.RailLength;
            RigidPose seated = probe.RailPose(SeatPose);
            RigidPose holding = seated.Multiply(probe.HoldPose(off).Inverse().Multiply(palm0));
            float railLength = probe.RailLength;
            Vector3 insert = SeatPose.TransformDirection(probe.InsertDirLocal);
            probe.Destroy();
            // A real hand: 18 degrees turned about the well's line and 1.5 cm aside.
            Vector3 aside = Vector3.Cross(insert, Rig.AimWorld).normalized;
            RigidPose hand = new RigidPose(holding.position + aside * 0.015f, Quaternion.AngleAxis(18f, insert) * holding.rotation);
            // Out of the gun, as a real hand is: a seated pistol magazine is held inside the grip (only its base shows), and
            // the hand pushing it home stays under the grip.
            // The drawn gun only (not the seated magazine's own solid: a rifle's hand round its magazine was pushed 20 cm
            // under it, the magazine held there never lined up again).
            for (int k = 0; k < 25 && _held.InsideDrawnGun(hand.position); k++)
            {
                hand = new RigidPose(hand.position - insert * 0.01f, hand.rotation);
            }
            float outBy = railLength + 0.12f;
            System.Func<float, RigidPose> along = d => new RigidPose(hand.position - insert * d, hand.rotation);
            IEnumerator Move(System.Func<float, RigidPose> pose, float seconds, string note)
            {
                float t0 = Time.unscaledTime;
                for (float k = 0f; k < 1f; k = (Time.unscaledTime - t0) / seconds)
                {
                    float s = k * k * (3f - 2f * k);
                    VRHand.Scripted[side] = pose(s).Multiply(rawFromPalm);
                    yield return new WaitForEndOfFrame();
                    frame(note);
                    yield return null;
                }
                VRHand.Scripted[side] = pose(1f).Multiply(rawFromPalm);
            }
            bool pulled = false, seatedAgain = false;
            try
            {
                yield return Move(s => along(0.12f * (1f - s)), 0.45f, "reaching");
                VRInput.SimulatedGrab[side] = true;
                if (off.Held == null && Ammo.MagInserted)
                {
                    PullSeatedMagazine(off); // what a squeeze at the magazine's base does (the simulated grab is held, not pressed)
                }
                yield return Move(s => along(0f), 0.15f, "grab");
                pulled = off.Held is MagazineProp;
                yield return Move(s => along(outBy * s), 0.8f, "pulling out");
                yield return Move(s => along(outBy), 0.4f, "held");
                yield return Move(s => along(outBy * (1f - s)), 0.8f, "pushing in");
                yield return Move(s => along(-0.005f), 0.3f, "seated");
                seatedAgain = Ammo.MagInserted;
                VRInput.SimulatedGrab[side] = false;
                yield return Move(s => along(0.12f * s), 0.35f, "let go");
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            if (off.Held is MagazineProp left)
            {
                // Not seated again: back into the pouch.
                Ammo.ReturnSpare(left.Rounds);
                left.Rounds = 0;
                left.Destroy();
            }
            report($"reload film: {(pulled ? "pulled out by hand" : "NOT PULLED OUT (the grab did not take it)")}, {(seatedAgain ? "seated again" : "NOT SEATED AGAIN")}");
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
                // A shot, then opened twice: the fired case falls out the first time only (every opening dropped
                // "capacity minus loaded" cases once, out of nothing).
                if (Ammo.MagRounds > 0)
                {
                    Ammo.OnShot();
                }
                int cases = Casings.Spawned;
                SetBreakOpen(true);
                int first = Casings.Spawned - cases;
                SetBreakOpen(false);
                yield return new UnityEngine.WaitForSecondsRealtime(0.2f);
                cases = Casings.Spawned;
                SetBreakOpen(true);
                int again = Casings.Spawned - cases;
                report($"break-open: opened after a shot, {first} case(s) fell out; opened again, {again}{(again > 0 ? " (NOT ONLY ONCE: cases out of nothing)" : "")}");
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
                    yield return null;
                    // Taken out of its seat by hand, it stays in the hand (it went straight back in on the grab frame).
                    report(!Ammo.MagInserted && off.Held is MagazineProp ? "pulled out by hand: stays in the hand" : "pulled out by hand: NOT KEPT IN THE HAND (seated again at once)");
                    if (off.Held is MagazineProp pulled && AcceptsNow(pulled))
                    {
                        SeatProp(off, pulled);
                    }
                }
                if (Data.Hinge == HingeKind.FeedCover)
                {
                    if (VRConfig.BeltByHand.Value && Ammo.MagInserted && !Ammo.BeltLaid)
                    {
                        // Pulled far past its length the belt slips from the hand and hangs from the box (it used to vanish).
                        BeginBelt(off);
                        RigidPose raw = off.Raw;
                        Vector3 palmFromRaw = raw.InverseTransformPoint(off.Palm.position);
                        Vector3 far = BeltStartWorld + Vector3.down * 0.25f - Rig.AimWorld * 0.55f;
                        VRHand.Scripted[(int)off.Side] = new RigidPose(far - raw.rotation * palmFromRaw, raw.rotation);
                        yield return null;
                        yield return null;
                        bool hanging = _beltHanging && !_beltHeld;
                        VRHand.Scripted[(int)off.Side] = null;
                        yield return null;
                        report($"belt pulled too far: {(hanging ? "slipped from the hand and hangs from the box" : "NOT HANGING from the box")}");
                        BeginBelt(off);
                        yield return null;
                        CompleteBelt(off);
                        report($"belt laid: {(_held.BeltPartCount > 0 ? $"the model's own belt ({_held.BeltPartCount} part(s)) shows" : _beltShownLaid ? "the strip stays on the feed tray" : "NOTHING SHOWS on the tray")}");
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
        /// The world bounds of what is drawn under <paramref name="t"/> (enabled meshes not scaled away); false if nothing.
        /// Pieces more than a metre from the middle of the rest are left out: a BRN 180 parks spent rounds 25 m away, which
        /// put its magazine's middle 12 m off and the autotest's close-up camera in a wall.
        /// </summary>
        internal static bool DrawnBounds(Transform t, out Bounds bounds)
        {
            bounds = default;
            if (t == null)
            {
                return false;
            }
            var parts = new System.Collections.Generic.List<Bounds>();
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(false))
            {
                if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer) && r.transform.lossyScale.sqrMagnitude >= 1e-6f)
                {
                    parts.Add(r.bounds);
                }
            }
            if (parts.Count == 0)
            {
                return false;
            }
            float Median(int axis)
            {
                var v = new float[parts.Count];
                for (int i = 0; i < v.Length; i++)
                {
                    v[i] = parts[i].center[axis];
                }
                System.Array.Sort(v);
                return v[v.Length / 2];
            }
            var middle = new Vector3(Median(0), Median(1), Median(2));
            if (parts.Count <= 2)
            {
                // Two pieces have no middle: the one nearest the part's own origin is the part.
                middle = parts[0].center;
                foreach (Bounds b in parts)
                {
                    if ((b.center - t.position).sqrMagnitude < (middle - t.position).sqrMagnitude)
                    {
                        middle = b.center;
                    }
                }
            }
            bool any = false;
            foreach (Bounds b in parts)
            {
                if ((b.center - middle).sqrMagnitude > 1f)
                {
                    continue;
                }
                if (!any)
                {
                    bounds = b;
                    any = true;
                }
                else
                {
                    bounds.Encapsulate(b);
                }
            }
            return any;
        }

        /// <summary>
        /// World box of the magazine as analysed, at its seat: for a magazine drawn by the gun's skinned mesh (a bone of it,
        /// nothing of its own to measure) or one the mod makes (no magazine part).
        /// </summary>
        private Bounds AnalysedMagWorld()
        {
            RigidPose seat = SeatPose;
            Bounds mb = Data.MagBounds;
            var b = new Bounds(seat.TransformPoint(mb.center), Vector3.zero);
            for (int i = 0; i < 8; i++)
            {
                b.Encapsulate(seat.TransformPoint(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1f : 1f, (i & 2) == 0 ? -1f : 1f, (i & 4) == 0 ? -1f : 1f))));
            }
            return b;
        }

        /// <summary>
        /// Autotest (-rfvr-deep): one magazine all the way round, with pictures from the gun's side and numbers. Dropped: does
        /// the copy that leaves the well start where the gun's own magazine was, and is the gun's own one hidden? Taken from
        /// the pouch: is it in the hand or floating beside it? Slid in: does it travel up the magwell or in front of it?
        /// Seated: does the gun's magazine show where it was?
        /// </summary>
        internal IEnumerator MagazineDeepCheck(System.Action<string> report, System.Action<string, Vector3, float> sideShot)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesDetachableMag || Ammo.BoxLocked || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("magazine (deep): skipped (no detachable magazine, or the hand is busy)");
                yield break;
            }
            if (!Ammo.MagInserted)
            {
                TakeFromPouch(off);
                if (off.Held is MagazineProp first && AcceptsNow(first))
                {
                    SeatProp(off, first);
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.5f); // the gun settles in the hand (the check before may have moved it)
            var lines = new System.Collections.Generic.List<string>();
            // A magazine drawn by the gun's skinned mesh (a bone of it) or made by the mod (no magazine part) has nothing of
            // its own to measure: its analysed box at the seat stands in, and the pictures are still taken (ARS guns had none).
            bool ownDrawn = DrawnBounds(Rig.Magazine, out Bounds own);
            if (!ownDrawn)
            {
                own = AnalysedMagWorld();
                lines.Add(Rig.Magazine != null ? "the gun's magazine is drawn by its skinned mesh: its analysed box measured" : "no magazine part (a made one): its analysed box measured");
            }
            // Measured in gun space: the gun may move in the hand during the check.
            Vector3 ownGun = Rig.ToGun(own.center);
            Vector3 outGun = Data.MagEjectAxis.normalized;
            Vector3 outWorld = Rig.DirToWorld(outGun);
            // The free hand out of the side camera's way for the first pictures (it stood right in front of it).
            RigidPose offRaw = off.Raw;
            VRHand.Scripted[(int)off.Side] = new RigidPose(offRaw.position + Vector3.down * 0.6f - Rig.AimWorld * 0.3f, offRaw.rotation);
            yield return null;
            yield return null;
            yield return new WaitForEndOfFrame();
            sideShot("mag-1-seated", Rig.ToWorld(ownGun), 0.32f);

            // 1. Dropped.
            EjectMagazine();
            MagazineProp leaving = _ejecting;
            yield return null;
            yield return new WaitForEndOfFrame();
            if (leaving != null && leaving.Go != null && DrawnBounds(leaving.Go.transform, out Bounds copy))
            {
                Vector3 delta = Rig.ToGun(copy.center) - ownGun;
                float across = Vector3.ProjectOnPlane(delta, outGun).magnitude;
                float size = copy.size.magnitude / Mathf.Max(own.size.magnitude, 1e-4f);
                Core.AutoTest.Metric("magazine (deep): cm the dropped copy starts across the pull from the gun's magazine", across * 100f);
                lines.Add($"dropped copy {across * 100f:0.0} cm across the pull from the gun's magazine, {Vector3.Dot(delta, outGun) * 100f:0.0} cm out, size x{size:0.00}"
                          + (ownDrawn && (across > 0.015f || size > 1.5f || size < 0.67f) ? " (THE DROPPED MAGAZINE IS NOT THE ONE IN THE GUN)" : ""));
            }
            else
            {
                lines.Add("dropped copy: DRAWS NOTHING");
            }
            bool ownShows = ownDrawn && DrawnBounds(Rig.Magazine, out _);
            if (ownShows)
            {
                lines.Add("the gun's own magazine STILL SHOWS after the drop");
            }
            sideShot("mag-2-leaving", Rig.ToWorld(ownGun), 0.32f);
            yield return new WaitForSecondsRealtime(0.6f);
            yield return new WaitForEndOfFrame();
            sideShot("mag-3-out", Rig.ToWorld(ownGun), 0.32f);
            VRHand.Scripted[(int)off.Side] = null;

            // 2. A fresh one from the pouch, in the hand.
            VRInput.SimulatedGrab[(int)off.Side] = true;
            try
            {
                TakeFromPouch(off);
                if (!(off.Held is MagazineProp fresh))
                {
                    lines.Add("NO FRESH MAGAZINE from the pouch");
                    report("magazine (deep): " + string.Join("; ", lines));
                    yield break;
                }
                // The fingers close and the palm settles over about a fifth of a second (two frames after the grab the
                // picture showed an open hand with the magazine above its knuckles on 91 guns).
                yield return new WaitForSecondsRealtime(0.35f);
                yield return new WaitForEndOfFrame();
                if (DrawnBounds(fresh.Go.transform, out Bounds inHand))
                {
                    float gap = Mathf.Sqrt(inHand.SqrDistance(off.PalmCentre));
                    Core.AutoTest.Metric("magazine (deep): cm from the palm to the magazine in the hand", gap * 100f);
                    lines.Add($"in the hand: {gap * 100f:0.0} cm from the palm{(gap > 0.04f ? " (FLOATS BESIDE THE HAND)" : "")}");
                }
                // How the fingers hold it (the review saw an open hand with the magazine on its knuckles on 91 guns).
                string held = MagazineProp.HandClipping(off, out _, out _, out int fingersOn);
                if (fingersOn >= 0)
                {
                    Core.AutoTest.Metric("magazine (deep): fingers on the magazine in the hand (of 4)", fingersOn);
                }
                lines.Add($"held: {held}; {fresh.SizeText}; {(off.Model != null ? off.Model.CurlsText : "no hand model")}{(fingersOn == 0 ? " (NO FINGER ON THE MAGAZINE)" : "")}");
                sideShot("mag-4-in-hand", off.PalmCentre, 0.32f);

                // 3. Lined up under the well, half way in on the rail, home.
                RigidPose holdInRaw = off.Raw.Inverse().Multiply(fresh.HoldPose(off));
                float length = Vector3.Dot(fresh.TipLocal - fresh.BaseLocal, fresh.InsertDirLocal);
                PlaceFreshMag(off, fresh, holdInRaw, 0.03f, 0f, 0.1f);
                yield return null;
                yield return null;
                yield return new WaitForEndOfFrame();
                // It waits a magazine's length under the well: framed from further back, on its top (a shot at the well
                // showed only the tip of it at the frame's bottom edge).
                Vector3 freshTip = fresh.Go != null ? fresh.Go.transform.TransformPoint(fresh.TipLocal) : Rig.ToWorld(ownGun + outGun * 0.05f);
                sideShot("mag-5-lined-up", freshTip - outWorld * 0.07f, 0.5f);
                // In until it is half way along its rail (a long magazine only meets the rail near the well).
                float halfIn = 0.03f;
                bool shot30 = false, shot60 = false, shot85 = false;
                for (float below = 0.03f; below > -length - 0.03f && ReferenceEquals(off.Held, fresh) && !Ammo.MagInserted
                                          && !(fresh.OnRail && fresh.RailDepth >= fresh.RailLength * 0.5f); below -= 0.3f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
                {
                    PlaceFreshMag(off, fresh, holdInRaw, below, 0f, 0.3f);
                    halfIn = below;
                    yield return null;
                    if (!shot30 && fresh.OnRail && fresh.RailDepth >= fresh.RailLength * 0.3f)
                    {
                        shot30 = true;
                        yield return new WaitForEndOfFrame();
                        lines.Add("30% in: " + SeatingState(off, fresh));
                        sideShot("mag-6a-30-in", fresh.Go.transform.TransformPoint(fresh.TipLocal), 0.22f);
                    }
                }
                yield return new WaitForEndOfFrame();
                if (fresh.Go != null && !Ammo.MagInserted && DrawnBounds(fresh.Go.transform, out Bounds half))
                {
                    // Where the gun's own magazine would be, slid out by what is left of the rail.
                    Vector3 halfGun = Rig.ToGun(half.center);
                    float left = fresh.OnRail ? fresh.RailLength - fresh.RailDepth : Vector3.Dot(halfGun - ownGun, outGun);
                    Vector3 expected = ownGun + outGun * left;
                    float off1 = Vector3.ProjectOnPlane(halfGun - expected, outGun).magnitude;
                    Core.AutoTest.Metric("magazine (deep): cm off the magwell line half way in", off1 * 100f);
                    lines.Add($"half way in ({(fresh.OnRail ? "on the rail" : "NOT ON THE RAIL")}): {off1 * 100f:0.0} cm off the magwell line{(ownDrawn && off1 > 0.012f ? " (SLIDES IN BESIDE THE WELL)" : "")}");
                }
                lines.Add("half in: " + SeatingState(off, fresh));
                sideShot("mag-6-half-in", Rig.ToWorld(ownGun), 0.32f);
                for (float below = halfIn; below > -length - 0.03f && ReferenceEquals(off.Held, fresh) && fresh.Go != null && !Ammo.MagInserted; below -= 0.3f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
                {
                    PlaceFreshMag(off, fresh, holdInRaw, below, 0f, 0.3f);
                    yield return null;
                    if (fresh.Go == null || Ammo.MagInserted)
                    {
                        break;
                    }
                    float frac = fresh.OnRail ? fresh.RailDepth / Mathf.Max(fresh.RailLength, 1e-4f) : 0f;
                    if (!shot60 && frac >= 0.6f || !shot85 && frac >= 0.85f)
                    {
                        string which = !shot60 && frac < 0.85f ? "60" : "85";
                        shot60 = true;
                        shot85 |= which == "85";
                        yield return new WaitForEndOfFrame();
                        if (fresh.Go == null)
                        {
                            break;
                        }
                        lines.Add(which + "% in: " + SeatingState(off, fresh));
                        // Between the base plate and the gun hand's grip, wide enough for the gun: centred on the base plate the
                        // pictures showed the free hand alone and nothing of where the magazine went.
                        Vector3 basePlate = fresh.Go.transform.TransformPoint(fresh.BaseLocal);
                        sideShot($"mag-6{(which == "60" ? "b" : "c")}-{which}-in", Vector3.Lerp(basePlate, Rig.ToWorld(Data.GripPoint), 0.5f), 0.32f);
                    }
                }
                for (int wait = 0; wait < 30 && !Ammo.MagInserted; wait++)
                {
                    yield return null;
                }
                yield return new WaitForEndOfFrame();
                bool seated = Ammo.MagInserted;
                string back = !ownDrawn ? "(not measured: nothing of its own)"
                    : DrawnBounds(Rig.Magazine, out Bounds again) ? $"{(Rig.ToGun(again.center) - ownGun).magnitude * 100f:0.0} cm from where it was" : "NOT SHOWN";
                lines.Add(seated ? $"seated: the gun's magazine shows {back}" : "NOT SEATED");
                sideShot("mag-7-seated-again", Rig.ToWorld(ownGun), 0.32f);
            }
            finally
            {
                VRHand.Scripted[(int)off.Side] = null;
                VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                VRInput.SimulatedGrab[(int)off.Side] = false;
                if (off.Held is MagazineProp leftover)
                {
                    Ammo.ReturnSpare(leftover.Rounds);
                    leftover.Rounds = 0;
                    leftover.Destroy();
                }
            }
            report("magazine (deep): " + string.Join("; ", lines));
            yield return new WaitForSecondsRealtime(0.2f);
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
            // Counted without the reserve: the game tops it up near a supply crate at any moment (the test stands at the
            // spawn's crate; an M1's "40 -> 48" was one refill of 8). What was loaded, plus the fresh magazine.
            int before = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0);
            // The simulated hand keeps its grip closed for the whole check, as a player holding the magazines would.
            VRInput.SimulatedGrab[(int)off.Side] = true;
            TakeFromPouch(off);
            if (off.Held is MagazineProp taken)
            {
                before += taken.Rounds;
            }
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
            int after = Ammo.MagRounds + (Ammo.Chambered ? 1 : 0) + inHand;
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

        /// <summary>
        /// Autotest (task 165): a magazine in the hand swept through the receiver above its well, sideways and from above,
        /// slowly and fast (2.5 m/s, a hard shove). Where it is drawn it must stay out of the gun's body the whole way
        /// (only the magwell is open to it), and end on the side it came from.
        /// </summary>
        internal IEnumerator MagazineKeepOutCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesDetachableMag || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("magazine keep-out: skipped (no detachable magazine, or the hand is busy)");
                yield break;
            }
            float until = Time.unscaledTime + 4f;
            while (_held.HandsSolid == null && Time.unscaledTime < until)
            {
                yield return null;
            }
            if (_held.HandsSolid == null)
            {
                report("magazine keep-out: the gun's body was not measured: not checked");
                yield break;
            }
            int side = (int)off.Side;
            VRInput.SimulatedGrab[side] = true;
            var lines = new System.Collections.Generic.List<string>();
            try
            {
                TakeFromPouch(off);
                if (!(off.Held is MagazineProp mag))
                {
                    report("magazine keep-out: NO MAGAZINE from the pouch");
                    yield break;
                }
                yield return null;
                yield return null;
                RigidPose holdInRaw = off.Raw.Inverse().Multiply(mag.HoldPose(off));
                Vector3 aim = Data.AimAxis.normalized, up = Data.Up.normalized, right = Vector3.Cross(up, aim).normalized;
                Vector3 seatGun = Rig.ToGun(SeatPose.position);
                // In the receiver over the well: the bore's line, a little ahead of the magazine's middle.
                Vector3 centre = Data.MuzzlePoint + aim * (Vector3.Dot(seatGun - Data.MuzzlePoint, aim) + 0.03f);
                var passes = new[] { ("sideways slowly", right, 0.5f), ("sideways hard", right, 2.5f), ("from above hard", -up, 2.5f) };
                int worstFrames = 0, worstPoints = 0;
                foreach (var (name, dirGun, speed) in passes)
                {
                    int frames = 0, maxPoints = 0;
                    float travel = 0f;
                    while (travel <= 0.5f && ReferenceEquals(off.Held, mag) && mag.Go != null && !mag.OnRail)
                    {
                        Vector3 at = Rig.ToWorld(centre + dirGun * (travel - 0.25f));
                        RigidPose hold = new RigidPose(at, SeatPose.rotation);
                        VRHand.Scripted[side] = hold.Multiply(holdInRaw.Inverse());
                        VRHand.ScriptedVelocity[side] = Rig.DirToWorld(dirGun) * speed;
                        yield return null;
                        int inside = mag.PointsInsideGun(_held);
                        if (inside > 0)
                        {
                            frames++;
                            maxPoints = Mathf.Max(maxPoints, inside);
                        }
                        if (Mathf.Abs(travel - 0.25f) < speed * 0.012f)
                        {
                            yield return new WaitForEndOfFrame();
                            shot($"keepout-{name.Replace(' ', '-')}");
                        }
                        travel += speed * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.03f);
                    }
                    float endSide = mag.Go != null ? Vector3.Dot(Rig.ToGun(mag.Go.transform.position) - centre, dirGun) : 0f;
                    Core.AutoTest.Metric($"magazine keep-out ({name}): frames with the magazine drawn inside the gun", frames);
                    lines.Add($"{name}: {frames} frame(s) drawn inside the gun (at most {maxPoints}/25 points), ended {endSide * 100f:0.0} cm {(endSide > 0.02f ? "PAST THE GUN (WENT THROUGH)" : "on its side")}");
                    worstFrames = Mathf.Max(worstFrames, frames);
                    worstPoints = Mathf.Max(worstPoints, maxPoints);
                    // Back out the way it came before the next pass.
                    VRHand.Scripted[side] = new RigidPose(Rig.ToWorld(centre - dirGun * 0.3f), SeatPose.rotation).Multiply(holdInRaw.Inverse());
                    yield return null;
                    yield return null;
                }
                report("magazine keep-out: " + string.Join("; ", lines));
                if (worstFrames > 2 || worstPoints > 3)
                {
                    report("magazine keep-out: THE MAGAZINE WENT INTO THE GUN");
                }
                if (off.Held is MagazineProp leftover)
                {
                    Ammo.ReturnSpare(leftover.Rounds);
                    leftover.Rounds = 0;
                    leftover.Destroy();
                }
            }
            finally
            {
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
                VRHand.ScriptedVelocity[side] = Vector3.zero;
            }
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
                    // Each pass starts with a full reserve: a gun with a few spare magazines (a sniper's four) ran the pouch dry
                    // by the fourth pass, the knocked-out magazines lying on the ground with their rounds.
                    if (!Ammo.InfiniteSpare && Ammo.Weapon.configuration.spareAmmo > 0)
                    {
                        Ammo.SpareAmmo = Mathf.Max(Ammo.SpareAmmo, Ammo.Weapon.configuration.spareAmmo);
                    }
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
                        // Stepped by at most 20 ms of travel a frame: right after a picture the first frame is long, and the
                        // hand jumped straight past the well (a player's hand moves continuously).
                        for (float below = 0.08f; below > -0.06f; below -= how.Speed * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
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
                    for (float below = start; below > -length - 0.03f && fresh.Go != null && ReferenceEquals(off.Held, fresh); below -= 0.6f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
                    {
                        PlaceFreshMag(off, fresh, holdInRaw, below, 0f, 0.6f);
                        yield return null;
                    }
                    VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                    yield return null;
                    // The knocked-out magazine slides out of the well for a moment before it falls (and counts as dropped).
                    for (int wait = 0; wait < 30 && _ejecting != null; wait++)
                    {
                        yield return null;
                    }
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
            // The rack point is put on the model's surface once the gun is measured (HeldWeapon.SnapRackPoint): waited
            // for, or the hand was put at the analysis's point and the pictures showed it where the point had been.
            for (float until = Time.unscaledTime + 2f; _held.HandsSolid == null && Time.unscaledTime < until;)
            {
                yield return null;
            }
            int side = (int)off.Side;
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            // The hand as a player holds the part (the controller's fixed test pose put the fist sideways beside it, and the
            // picture review saw the hand off the part on 207 guns): the palm against the part's outer side; on a part on top,
            // the fingers across it toward the far side, on a part on the side, up and over it.
            Vector3 aimW = Rig.AimWorld, upW = Rig.DirToWorld(Data.Up).normalized, rightW = Vector3.Cross(upW, aimW).normalized;
            Vector3 boreAt = Rig.ToWorld(Data.MuzzlePoint);
            // A handle clearly off to the side (an AK's bolt handle, a side charging handle) is taken from that side; any
            // other part over the top (a pistol slide, a bolt whose rack point lay a few millimetres under the bore put the
            // hand under the gun, at the trigger guard: P320, the PKM family).
            Vector3 offBore = Vector3.ProjectOnPlane(RackPointWorld - boreAt, aimW);
            Vector3 sideways = offBore - upW * Vector3.Dot(offBore, upW);
            Vector3 outward = sideways.magnitude > 0.025f ? sideways.normalized : upW;
            Vector3 intoPart = -outward;
            Vector3 fingers = Vector3.Dot(outward, upW) > 0.7f ? (off.IsLeft ? rightW : -rightW) : upW;
            Vector3 palmX = -intoPart * off.Mirror;
            fingers = Vector3.ProjectOnPlane(fingers, palmX);
            fingers = fingers.sqrMagnitude > 1e-6f ? fingers.normalized : aimW;
            Quaternion palmRot = Quaternion.LookRotation(fingers, Vector3.Cross(fingers, palmX));
            RigidPose At(Vector3 palm) => new RigidPose(palm - intoPart * 0.02f, palmRot).Multiply(rawFromPalm);
            VRInput.SimulatedGrab[side] = true;
            try
            {
                VRHand.Scripted[side] = At(RackPointWorld);
                yield return null;
                yield return null;
                BeginRack(off);
                yield return new WaitForSecondsRealtime(0.5f); // the part is measured, the hand closes on it
                // The part's shape is measured from pictures over a few frames: waited for (up to 2 s more), or the hand
                // was measured against nothing ("the part's shape is not ready").
                for (float until = Time.realtimeSinceStartup + 2f; !_held.PartShapeReady && Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                }
                yield return new WaitForEndOfFrame();
                shot("rack-grip");
                string atRest = _held.PartClipping(off, out int restBuried, out int restTouching);
                var layoutRest = _held.DrawnLayout();
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
                // What visibly moved with the half pull (in gun space), and whether the part under the hand is among it.
                var layoutPulled = _held.DrawnLayout();
                float most = 0f, nearestMoving = float.MaxValue, halfStroke = StrokeLength * 0.5f;
                int movedCount = 0;
                foreach (var kv in layoutRest)
                {
                    if (!layoutPulled.TryGetValue(kv.Key, out Vector3 now))
                    {
                        continue;
                    }
                    float moved = Vector3.Distance(kv.Value, now);
                    most = Mathf.Max(most, moved);
                    if (moved >= Mathf.Max(0.004f, halfStroke * 0.4f))
                    {
                        // How far it is from the hand now (to a mesh's surface, a bone's point): a part that came back
                        // with the hand is still right there.
                        movedCount++;
                        // From the hand itself (the rack point lies on the part by definition: every gun read 0.0 cm).
                        Vector3 handAt = off.Model != null ? off.Model.GraspPointWorld : RackPointWorld;
                        float gap = kv.Key is Renderer r ? Mathf.Sqrt(r.bounds.SqrDistance(handAt))
                            : kv.Key is Transform b ? Vector3.Distance(b.position, handAt) : float.MaxValue;
                        nearestMoving = Mathf.Min(nearestMoving, gap);
                    }
                }
                Core.AutoTest.Metric("rack: mm the drawn gun moves at half pull (most)", most * 1000f);
                string drawnMoves = movedCount == 0
                    ? $"NOTHING DRAWN MOVES (most {most * 1000f:0} mm for a {halfStroke * 1000f:0} mm half pull)"
                    : $"{movedCount} drawn part(s) move, most {most * 1000f:0} mm; the nearest is {nearestMoving * 100f:0.0} cm from the hand"
                      + (nearestMoving > 0.03f ? " (THE PART UNDER THE HAND DOES NOT MOVE)" : "");
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
                report($"rack grip ({(IsPump ? "pump" : HandleStroke ? "charging handle" : "slide/bolt")}, {Data.SlideMethod}): {atRest}; pulled half way: {pulled}; moving with the hand: {moves}; drawn: {drawnMoves}; "
                       + $"the gun moved {tug * 1000f:0.0} mm with the pull{(HandDriven ? " (hand-driven: stays open)" : $", {jolt * 1000f:0.0} mm when it went home")}"); 
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            yield return new WaitForSecondsRealtime(0.4f); // the grip opens: the part is let go and returns
            if (HandDriven && Ammo.BoltOpen)
            {
                // A bolt or pump stays open where the hand left it: closed again as a hand pushing it home would, or the
                // shots after this check met an open bolt (an MRAD "did not fire").
                _rackedBack = false;
                _held.SlideOffset = 0f;
                Ammo.BoltOpen = false;
                Ammo.RackForward();
            }
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
            bool spent = Ammo.SpentInChamber;
            bool live = Ammo.RackBack();
            if (spent || live)
            {
                Casings.Eject(_held, live);
            }
            Ammo.RackForward();
        }

        /// <summary>Weapon is going away: finish what is in progress so no ammo is lost or duplicated.</summary>
        public void Release()
        {
            VRHand off = _sys.Rig.Off;
            _pullingIn = null;
            Casings.ShowChamber(_held, false, false);
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
            _beltHanging = false;
            _beltShownLaid = false;
            if (off.Model != null)
            {
                off.Model.GraspAnchor = null;
                off.Model.Palming = false; // the palmed magazine was dropped above; a kept one is held alone
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
