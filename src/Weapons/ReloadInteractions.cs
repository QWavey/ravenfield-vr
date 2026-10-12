using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Manual reloading with both hands: magazine release, pulling a fresh magazine / shell / rocket from the
    /// pouch, seating or loading it, catching dropped magazines, racking the slide / charging handle / bolt /
    /// pump along its real travel, slide-lock release, plus the foregrip grab for two-handed aiming.
    /// </summary>
    public sealed class ReloadInteractions
    {
        private readonly WeaponSystem _sys;
        private readonly HeldWeapon _held;

        private bool _racking;
        private Vector3 _rackStartGun;
        private float _rackBase;
        private bool _rackedBack;
        private bool _returning;
        private float _lastTick;

        private MagazineProp _seating;

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
                    if (Ammo.UsesDetachableMag && Ammo.MagInserted)
                    {
                        EjectMagazine();
                    }
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
            if (off.GrabPressed && off.Role == HandRole.Free)
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
            }
        }

        // ---- grabbing ------------------------------------------------------------------------------

        private void TryGrab(VRHand off)
        {
            Vector3 palm = off.Palm.position;
            float radius = VRConfig.GrabRadius.Value;
            int choice = 0; // 1 catch, 2 rack, 3 pouch, 4 foregrip
            float best = float.MaxValue;
            MagazineProp caught = MagazineProp.NearestDropped(palm, 0.13f, Data.Key);
            if (caught != null && Manual)
            {
                Consider(1, Vector3.Distance(caught.Go.transform.position, palm) / 0.13f, ref choice, ref best);
            }
            if (Manual && (Ammo.UsesChamber))
            {
                float d = Vector3.Distance(palm, RackPointWorld);
                if (d < radius)
                {
                    // Slight preference: racking is the most precise target.
                    Consider(2, d / radius * 0.85f, ref choice, ref best);
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
                    caught.Attach(off);
                    off.Haptic(0.03f, 150f, 0.5f);
                    break;
                case 2:
                    BeginRack(off);
                    break;
                case 3:
                    TakeFromPouch(off);
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

        // ---- magazine release / pouch / props ------------------------------------------------------

        private void EjectMagazine()
        {
            int rounds = Ammo.EjectMagazine();
            int propRounds = rounds;
            if (VRConfig.ReturnRoundsOnMagDrop.Value)
            {
                Ammo.ReturnSpare(rounds);
                propRounds = 0;
            }
            MagazineProp prop = MagazineProp.Create(Rig, PropKind.Magazine, propRounds);
            VRHand dom = _sys.Rig.Dominant;
            Vector3 eject = Rig.DirToWorld(Data.MagEjectAxis);
            prop.Drop(dom.Velocity + eject * 0.9f + Vector3.down * 0.2f, Random.insideUnitSphere * 2f);
            _sys.Sounds.Play(MechSound.MagOut, prop.Go.transform.position);
            dom.Haptic(0.05f, 90f, 0.6f);
        }

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
                rounds = Data.Action == ActionType.ShellFed
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
            prop.Attach(off);
            prop.FollowHand();
            _sys.Sounds.Play(MechSound.Pouch, off.Palm.position, 0.6f);
            off.Haptic(0.03f, 140f, 0.45f);
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
            if (prop.WeaponKey != Data.Key || prop.Seating || !Manual)
            {
                return;
            }
            Vector3 seatPoint = Rig.ToWorld(Data.MagSeat.position);
            switch (prop.Kind)
            {
                case PropKind.Magazine:
                {
                    if (Ammo.MagInserted || !Ammo.UsesDetachableMag)
                    {
                        return;
                    }
                    RigidPose seat = Rig.GunBodyPose.Multiply(Data.MagSeat);
                    Vector3 tipSeated = seat.TransformPoint(prop.TipLocal);
                    Vector3 insertSeated = seat.TransformDirection(prop.InsertDirLocal);
                    if (Vector3.Distance(prop.TipWorld, tipSeated) < 0.075f && Vector3.Angle(prop.InsertDirWorld, insertSeated) < 50f)
                    {
                        BeginSeat(off, prop);
                    }
                    break;
                }
                case PropKind.Shell:
                    if (Vector3.Distance(prop.TipWorld, seatPoint) < 0.085f || Vector3.Distance(prop.Go.transform.position, seatPoint) < 0.07f)
                    {
                        int accepted = Ammo.LoadRounds(prop.Rounds);
                        Ammo.ReturnSpare(prop.Rounds - accepted);
                        prop.Destroy();
                        _sys.Sounds.Play(MechSound.ShellIn, seatPoint);
                        off.Haptic(0.03f, 160f, 0.5f);
                        _sys.Rig.Dominant.Haptic(0.02f, 160f, 0.3f);
                    }
                    break;
                case PropKind.Rocket:
                    if (Ammo.MagRounds == 0 && (Vector3.Distance(prop.TipWorld, seatPoint) < 0.16f || Vector3.Distance(prop.Go.transform.position, seatPoint) < 0.2f))
                    {
                        Ammo.LoadRounds(1);
                        prop.Destroy();
                        _sys.Sounds.Play(MechSound.MagIn, seatPoint);
                        off.Haptic(0.06f, 90f, 0.7f);
                        _sys.Rig.Dominant.Haptic(0.05f, 90f, 0.5f);
                    }
                    break;
            }
        }

        private void BeginSeat(VRHand off, MagazineProp prop)
        {
            prop.Seating = true;
            prop.SeatFrom = RigidPose.Of(prop.Go.transform);
            prop.SeatProgress = 0f;
            off.Held = null;
            off.Role = HandRole.Free;
            _seating = prop;
            off.Haptic(0.02f, 200f, 0.4f);
        }

        private void FinishSeat()
        {
            MagazineProp prop = _seating;
            _seating = null;
            if (prop == null)
            {
                return;
            }
            Ammo.InsertMagazine(prop.Rounds);
            Vector3 at = prop.Go.transform.position;
            prop.Destroy();
            _sys.Sounds.Play(MechSound.MagIn, at);
            _sys.Rig.Off.Haptic(0.05f, 110f, 0.8f);
            _sys.Rig.Dominant.Haptic(0.05f, 110f, 0.6f);
        }

        private void ReleaseProp(VRHand off, MagazineProp prop)
        {
            if (_sys.Zones.InPouch(off.Palm.position))
            {
                if (prop.WeaponKey == Data.Key)
                {
                    Ammo?.ReturnSpare(prop.Rounds);
                }
                prop.Destroy();
                _sys.Sounds.Play(MechSound.Pouch, off.Palm.position, 0.5f);
                off.Haptic(0.02f, 120f, 0.3f);
                return;
            }
            if (VRConfig.ReturnRoundsOnMagDrop.Value && prop.WeaponKey == Data.Key)
            {
                Ammo?.ReturnSpare(prop.Rounds);
                prop.Rounds = 0;
            }
            prop.Drop(off.ThrowVelocity(), off.AngularVelocity);
        }

        // ---- slide / bolt / pump -----------------------------------------------------------------------

        private Vector3 RackPointWorld => Rig.ToWorld(Data.RackPoint + TravelDir * _held.SlideOffset);

        private void BeginRack(VRHand off)
        {
            _racking = true;
            _returning = false;
            _rackStartGun = Rig.ToGun(off.Palm.position);
            _rackBase = Ammo.SlideLocked ? Travel : _held.SlideOffset;
            _rackedBack = Ammo.SlideLocked;
            _lastTick = _rackBase;
            off.Role = HandRole.Slide;
            off.Haptic(0.02f, 160f, 0.35f);
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
                _sys.Sounds.Play(MechSound.SlideBack, RackPointWorld);
                off.Haptic(0.03f, 140f, 0.6f);
                if (ejected)
                {
                    PlayCasingEject();
                }
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
                    _sys.Sounds.Play(MechSound.SlideForward, RackPointWorld);
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
                _sys.Sounds.Play(MechSound.SlideForward, RackPointWorld);
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
                _sys.Sounds.Play(MechSound.SlideBack, RackPointWorld, 0.4f);
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
            else if (_returning)
            {
                // Slides slam home in a few hundredths of a second.
                _held.SlideOffset = Mathf.MoveTowards(_held.SlideOffset, 0f, 2.5f * dt);
                if (_held.SlideOffset <= 0f)
                {
                    _returning = false;
                }
            }
            else if (!_racking)
            {
                _held.SlideOffset = 0f;
            }

            VRHand off = _sys.Rig.Off;
            if (_racking)
            {
                off.Model.OverridePose(new RigidPose(RackPointWorld, Rig.GunBody.rotation * Quaternion.LookRotation(Data.AimAxis, Data.Up)));
            }
            if (off.Held is MagazineProp held)
            {
                held.FollowHand();
            }
            if (_seating != null)
            {
                _seating.SeatProgress += dt / 0.07f;
                RigidPose seat = Rig.GunBodyPose.Multiply(Data.MagSeat);
                float t = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_seating.SeatProgress));
                RigidPose.Lerp(_seating.SeatFrom, seat, t).ApplyTo(_seating.Go.transform);
                if (_seating.SeatProgress >= 1f)
                {
                    FinishSeat();
                }
            }
        }

        /// <summary>Weapon is going away: finish what is in progress so no ammo is lost or duplicated.</summary>
        public void Release()
        {
            VRHand off = _sys.Rig.Off;
            if (_seating != null)
            {
                FinishSeat();
            }
            if (_racking)
            {
                EndRack(off, chamber: _rackedBack);
            }
            if (off.Held is MagazineProp prop)
            {
                if (prop.WeaponKey == Data.Key)
                {
                    Ammo?.ReturnSpare(prop.Rounds);
                }
                prop.Destroy();
            }
            _held.SlideOffset = 0f;
        }
    }
}
