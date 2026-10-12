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
        private Vector3 _rackStartGun;
        private float _rackBase;
        private bool _rackedBack;
        private bool _returning;
        private float _lastTick;

        private MagazineProp _ejecting;
        private float _ejectTime;

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

            if (Manual)
            {
                if (VRInput.GetPressed(DigitalAction.MagRelease, dom.Side))
                {
                    VRInput.Consume(DigitalAction.MagRelease, dom.Side);
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
                        EjectMagazine();
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
            // Grenades: the pin (ThrowableHandler) and loose props (WeaponSystem) take the off hand's grab.
            if (off.GrabPressed && off.Role == HandRole.Free && Data.Action != ActionType.Throwable)
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
            }
        }

        // ---- grabbing ------------------------------------------------------------------------------

        private void TryGrab(VRHand off)
        {
            Vector3 palm = off.Palm.position;
            float radius = VRConfig.GrabRadius.Value;
            int choice = 0; // 1 catch, 2 rack, 3 pouch, 4 foregrip, 5 feed cover, 6 seated magazine
            float best = float.MaxValue;
            MagazineProp caught = null;
            if (Manual)
            {
                // This weapon's own magazines first, then anything else lying within reach.
                caught = _ejecting != null && Vector3.Distance(_ejecting.Go.transform.position, palm) < 0.13f
                    ? _ejecting
                    : MagazineProp.NearestDropped(palm, 0.13f, Data.Key) ?? MagazineProp.NearestDropped(palm, 0.13f, null);
                if (caught != null)
                {
                    Consider(1, Vector3.Distance(caught.Go.transform.position, palm) / 0.13f, ref choice, ref best);
                }
            }
            if (Manual && Ammo.UsesChamber)
            {
                float d = Vector3.Distance(palm, RackPointWorld);
                if (d < radius)
                {
                    // Slight preference: racking is the most precise target.
                    Consider(2, d / radius * 0.85f, ref choice, ref best);
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
            if (Manual && _sys.Zones.InPouch(palm))
            {
                Consider(3, Vector3.Distance(palm, _sys.Zones.Pouch) / BodyZones.PouchRadius, ref choice, ref best);
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
            _ejecting.Owner = Ammo;
            _ejecting.RailDepth = _ejecting.RailLength;
            _ejectTime = 0f;
            _held.Sounds.Play(MechSound.MagOut, _ejecting.Go.transform.position);
            _sys.Rig.Dominant.Haptic(0.05f, 90f, 0.6f);
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
        }

        /// <summary>
        /// Tactical reload with one hand: driving a fresh magazine into the base of the seated one knocks the old
        /// one out of the well; the new one then slides in on the rail.
        /// </summary>
        private bool TryMagKick(VRHand off, MagazineProp prop)
        {
            if (prop.Kind != PropKind.Magazine || prop.WeaponKey != Data.Key || !Manual || !Ammo.UsesDetachableMag || !Ammo.MagInserted
                || _ejecting != null || prop.Rounds <= 0 || !Data.MagKickable || Ammo.BoxLocked)
            {
                return false; // deep wells (pistol grips, KRISS Vector) leave nothing to hit
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
            float speed = Vector3.Dot(off.Velocity - _sys.Rig.Dominant.Velocity, insert);
            if (along < -0.03f || along > 0.06f || lateral > 0.055f || angle > 50f || speed < 0.45f)
            {
                return false;
            }
            EjectMagazine();
            off.Haptic(0.04f, 80f, 0.8f);
            return true;
        }

        private void SeatProp(VRHand off, MagazineProp prop)
        {
            Vector3 at = SeatPose.position;
            switch (prop.Kind)
            {
                case PropKind.Magazine:
                    Ammo.InsertMagazine(prop.Rounds);
                    _held.Sounds.Play(MechSound.MagIn, at);
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
                    break;
            }
            prop.Destroy();
            off.Haptic(0.05f, 110f, 0.8f);
            _sys.Rig.Dominant.Haptic(0.05f, 110f, 0.6f);
        }

        private void ReleaseProp(VRHand off, MagazineProp prop)
        {
            if (_sys.Zones.InPouch(off.Palm.position))
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

        private Vector3 RackPointWorld => Rig.ToWorld(Data.RackPoint + TravelDir * _held.SlideOffset);

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
            _rackBase = Ammo.SlideLocked ? Travel : _held.SlideOffset;
            _rackedBack = Ammo.SlideLocked || Ammo.BoltOpen && _held.SlideOffset >= Travel * 0.85f;
            _lastTick = _rackBase;
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
            float pull = Vector3.Dot(handGun - _rackStartGun, TravelDir);
            float offset = Mathf.Clamp(_rackBase + pull, 0f, Travel);
            _held.SlideOffset = offset;

            if (Mathf.Abs(offset - _lastTick) > 0.01f)
            {
                _lastTick = offset;
                off.Haptic(0.006f, 220f, 0.15f);
            }
            if (!_rackedBack && offset >= Travel * 0.85f)
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
            else if (HandDriven && _rackedBack && offset <= Travel * 0.08f)
            {
                // Pushed home by hand: it strips and chambers a round as it closes.
                _rackedBack = false;
                Ammo.BoltOpen = false;
                Ammo.RackForward();
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld);
                off.Haptic(0.04f, 120f, 0.6f);
                _sys.Rig.Dominant.Haptic(0.03f, 120f, 0.4f);
            }

            float drift = Vector3.ProjectOnPlane(handGun - _rackStartGun, TravelDir).magnitude;
            if (!off.GrabHeld || drift > 0.2f)
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
                // Spring-driven slides slam home in a few hundredths of a second.
                _held.SlideOffset = Mathf.MoveTowards(_held.SlideOffset, 0f, 6f * dt);
                if (_held.SlideOffset <= 0f)
                {
                    _returning = false;
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
            if (_beltHeld && _belt != null && Rig.GunBody != null)
            {
                _belt.Place(BeltStartWorld, off.Palm.position + off.Palm.Forward * 0.02f, Rig.AimWorld);
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
            if (_ejecting != null)
            {
                FinishEject();
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
            _belt?.Destroy();
            _belt = null;
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
