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
    // ReloadInteractions, part: the slide, bolt or pump worked by hand. The class, grabbing and the frame's visuals:
    // ReloadInteractions.cs; the self-checks: ReloadInteractions.SelfChecks.cs.
    public sealed partial class ReloadInteractions
    {
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
        private Vector3 RackPointWorld => Rig.ToWorld(RackGun + StrokeDir * (StrokeLength * ((_held.BoltLockedAlone || _held.KnobLockedAlone || _held.HandleLockedAlone) && !_racking ? 0f : Fraction)));

        /// <summary>The rack point (gun space); for the held weapon's own rig it sits on the drawn model's surface.</summary>
        private Vector3 RackGun => ReferenceEquals(Rig, _held.Rig) ? _held.RackPointGun : Data.RackPoint;

        /// <summary>A hand is working the slide, bolt or handle right now.</summary>
        internal bool IsRacking => _racking;

        /// <summary>The rack in progress started with the bolt locked back and its handle at home (an AR's, a CZ Bren 2's knob).</summary>
        internal bool RackFromLock => _racking && _rackFromBoltLock;

        /// <summary>A palm weighed against the rack zone as TryGrab weighs it (for MagnifierFlip): its score, or infinity out of reach.</summary>
        internal float RackScore(Vector3 palm)
        {
            if (!Manual || Ammo == null || !Ammo.UsesChamber || IsPump)
            {
                return float.PositiveInfinity;
            }
            float rackRadius = VRConfig.GrabRadius.Value * 1.3f;
            float d = RackZoneDistance(palm);
            return d < rackRadius ? d / rackRadius * 0.85f : float.PositiveInfinity;
        }

        /// <summary>
        /// Distance to the part you rack: a slide can be taken anywhere along its rear half, a bolt or handle around
        /// its knob.
        /// </summary>
        private float RackZoneDistance(Vector3 palm)
        {
            Vector3 a = RackPointWorld;
            float along = Data.IsPistol && !HandleStroke ? 0.05f : HandleStroke ? 0.012f : 0.03f;
            // A charging handle at the back of the receiver is taken by its T behind the rack point, not inside the gun.
            bool tBehind = !Data.IsPistol && (Data.CarveHandle || Data.SlideMethod != null && Data.SlideMethod.Contains("(AR-pattern: its own handle)"));
            Vector3 b = a + Rig.AimWorld * (tBehind ? -0.025f : along);
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(palm - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return Vector3.Distance(palm, a + ab * t);
        }

        /// <summary>Bolt, pump and lever actions move only by hand: let go half way and they stay there.</summary>
        private bool HandDriven => Data.CyclesByHand;

        /// <summary>A pump at the handguard: holding it is also the support grip, so both hands aim while pumping.</summary>
        // A shotgun's pump up to 22 cm from the support point (one analysed as a charging handle is taken at its back end:
        // the R870 CQC's and the ARS M500's could not be worked from the pump; user, 2026-10-16).
        private bool IsPump => Data.CyclesByHand && GripData.HasSupportPoint && !GripData.IsPistol
                               && Vector3.Distance(Rig.ToWorld(Data.RackPoint), GripRig.ToWorld(GripData.SupportPoint)) < (Data.Action == ActionType.ShellFed ? 0.22f : 0.15f);

        private void BeginRack(VRHand off)
        {
            _returning = false;
            _rackStartGun = Rig.ToGun(off.Palm.position);
            // How the hand takes a pistol's slide, for the whole stroke: the palm on it (over the top or on its side, the
            // fingers round it) when the palm faces the slide across it, else pinched (a slingshot from behind).
            Vector3 palmNormal = off.Palm.rotation * new Vector3(-off.Mirror, 0f, 0f);
            _rackPalm = Data.IsPistol && !HandleStroke && !IsPump && Mathf.Abs(Vector3.Dot(palmNormal, Rig.AimWorld)) < 0.6f
                        && Vector3.Dot(palmNormal, RackPointWorld - off.Palm.position) > -0.01f;
            // Racking from here on (set after the test above: while racking, RackPointWorld follows the slide's travel, and a
            // made knob locked alone is taken at home before; review, 2026-10-20).
            _racking = true;
            // Locked open with only the bolt back (an M4's handle home): the handle is taken where it is, at home.
            _rackFromBoltLock = _held.BoltLockedAlone || _held.KnobLockedAlone || _held.HandleLockedAlone; // (a made knob or an M4's T locked forward: taken at home too)
            _rackBase = _rackFromBoltLock ? 0f : Ammo.SlideLocked ? 1f : Fraction;
            // An open bolt with an empty chamber (the case already out) feeds when pushed home, however far it was left open:
            // pulled back, half closed and let go, it closed later without feeding and the gun said RACK.
            // (Not with a fired case still in: a bolt left part way open and put away came back drawn fully open, and the case was
            // never thrown out before the next round went in over it; reload hunt, 2026-10-20.)
            // (Nor with a live round still in: a pump or bolt pulled part way and put away is drawn fully open, its round not yet
            // thrown out, and counted as already back it never was; a pull past the line always empties the chamber (RackBack), so
            // open past it with a round in comes only from that, and the round goes out as the hand takes it; round-5 scout.)
            _rackedBack = Ammo.SlideLocked || Ammo.BoltOpen && !Ammo.SpentInChamber && !Ammo.Chambered;
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
            if (_rackByTrigger && off.GrabHeld && !VRInput.GetHeld(DigitalAction.Use, off.Side))
            {
                // The trigger let go: the pump stays where the hand left it and the hand holds the gun by the pump again.
                EndRack(off, chamber: true);
                off.Role = HandRole.Foregrip;
                _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
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
            // A spring-driven slide or handle pulls back harder the further it goes, as its spring is compressed: the tug and the
            // ticks in the hand grow from a third at the start to full at the back (user, 2026-11-03, idea 2). A bolt or pump moved by
            // hand has no spring: even all the way.
            float spring = HandDriven ? 1f : 0.35f + 0.65f * fraction;
            float moved = stroke - _tugStroke;
            _tugStroke = stroke;
            if (Mathf.Abs(moved) > 1e-5f)
            {
                _held.Nudge(Rig.DirToWorld(StrokeDir) * Mathf.Clamp(moved * 2.5f * spring, -0.05f, 0.05f));
            }
            if (Mathf.Abs(stroke - _lastTick) > 0.01f)
            {
                _lastTick = stroke;
                off.Haptic(0.006f, 230f - 60f * fraction * spring, 0.06f + 0.24f * spring);
            }
            // Against the back stop: a firm knock in the hand, once a stroke.
            if (fraction >= 0.985f && !_atBackStop)
            {
                _atBackStop = true;
                off.Haptic(0.03f, 90f, 0.75f);
                _held.Nudge(Rig.DirToWorld(StrokeDir) * 0.006f);
            }
            else if (fraction < 0.9f)
            {
                _atBackStop = false;
            }
            // An en-bloc rifle's rod at half its stroke: the M1 GARRET's 9 cm, pulled most of the way with the other hand on the
            // stock, came short of 80% and sprang home instead of staying back for a clip (user, 2026-11-03: "it doesnt stay back
            // when pulled back").
            if (!_rackedBack && fraction >= (Data.EnBloc ? 0.5f : 0.8f))
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
                // Pushed home by hand: it strips and chambers a round as it closes, as hard as it was pushed (slammed home it
                // clacks and jolts the gun; eased home it closes softly: user, 2026-11-03, idea 3).
                float closing = Mathf.Abs(moved) / Mathf.Max(Time.unscaledDeltaTime, 1e-3f);
                float hard = Mathf.Clamp01(0.25f + closing / 1.2f);
                _rackedBack = false;
                Ammo.BoltOpen = false;
                Ammo.RackForward();
                _held.Nudge(Rig.AimWorld * (0.1f * hard), 10f); // the bolt or pump closing jolts the gun
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld, hard);
                off.Haptic(0.04f, 120f, 0.3f + 0.3f * hard);
                _sys.Rig.Dominant.Haptic(0.03f, 120f, 0.4f * hard);
            }

            float drift = Vector3.ProjectOnPlane(handGun - _rackStartGun, StrokeDir).magnitude;
            // Past the stroke's ends the part stops and the hand pulls on alone: far enough on, it has let go. A pump lets go a
            // stroke past its stops (6 cm at least), and the hand holds the gun by the pump again: at the slides' 30 cm the glove
            // stayed stuck on the pump's stop while the hand went on for 30 cm more (the R870's stroke is 9 cm; Tasks 181, "the
            // hand on the pump slides much too far along it"; sweep round 2).
            float unclamped = _rackBase + pull / StrokeLength;
            float past = Mathf.Max(0f, Mathf.Max(unclamped - 1f, -unclamped)) * StrokeLength;
            float overshoot = IsPump ? Mathf.Max(0.06f, StrokeLength) : RackOvershoot;
            if (!off.GrabHeld || drift > RackDrift || past > overshoot)
            {
                bool backOnPump = IsPump && off.GrabHeld && drift <= RackDrift;
                EndRack(off, chamber: true);
                if (backOnPump)
                {
                    off.Role = HandRole.Foregrip;
                    _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
                }
            }
        }

        private void EndRack(VRHand off, bool chamber)
        {
            _racking = false;
            _rackByTrigger = false;
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
            bool fromBoltLock = _rackFromBoltLock;
            _rackFromBoltLock = false;
            if (fromBoltLock && Ammo.SlideLocked && Fraction < 0.8f)
            {
                // Let go short of the back: the bolt is still on its catch, and the handle springs home alone.
                _rackedBack = false;
                _returning = false;
                return;
            }
            if (_rackedBack)
            {
                // (With no round to chamber, a gun that locks open on empty stays caught back: AmmoState.RackForward, Tasks 264.)
                Ammo.RackForward();
                if (!Ammo.SlideLocked)
                {
                    // As loud as the run home it has left: let go from the back it slams, ridden most of the way home it only
                    // clicks shut (idea 3).
                    float slam = Mathf.Lerp(0.3f, 1f, Fraction);
                    _held.Sounds.Play(MechSound.SlideForward, RackPointWorld, slam);
                    off.Haptic(0.04f, 120f, 0.3f + 0.3f * slam);
                    _sys.Rig.Dominant.Haptic(0.04f, 120f, 0.5f * slam);
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
            if (Manual && Data.EnBloc && Ammo.MagInserted && Ammo.MagRounds == 0 && !Ammo.Chambered)
            {
                ThrowEmptyClip();
                return;
            }
            if (Ammo != null && Ammo.SlideLocked)
            {
                _held.Sounds.Play(MechSound.SlideBack, RackPointWorld, 0.4f);
            }
        }

        private AudioSource _pingSource;
        private bool _pingLooked;

        /// <summary>
        /// The last round of an en-bloc clip fired: the empty clip flies up out of the open action with the gun's own ping
        /// (the vanilla Garand's "Bank Audio Source" holds garand_clip), and the bolt stays open (AmmoState locked it).
        /// </summary>
        private void ThrowEmptyClip()
        {
            if (_ejecting != null)
            {
                FinishEject();
            }
            int rounds = Ammo.EjectMagazine();
            MagazineProp clip = MagazineProp.Create(Rig, PropKind.Magazine, rounds);
            clip.Owner = Ammo;
            Vector3 up = Rig.UpWorld, right = Rig.DirToWorld(Vector3.Cross(Data.Up, Data.AimAxis).normalized);
            clip.Drop(_sys.Rig.Dominant.Velocity + up * Random.Range(2.6f, 3.4f) + right * Random.Range(0.2f, 0.6f) - Rig.AimWorld * 0.4f,
                Random.insideUnitSphere * 12f + right * 15f);
            if (!_pingLooked)
            {
                _pingLooked = true;
                foreach (AudioSource a in _held.Weapon.GetComponentsInChildren<AudioSource>(true))
                {
                    if (a.clip != null && System.Text.RegularExpressions.Regex.IsMatch(a.clip.name, @"ping|_clip$|clip.?(eject|out|ping)|enbloc", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    {
                        _pingSource = a;
                        break;
                    }
                }
                Log.Info($"{Data.Key}: the empty clip's ping is {(_pingSource != null ? $"the gun's own '{_pingSource.clip.name}'" : "not found (a plain eject sound)")}.");
            }
            if (_pingSource != null)
            {
                _pingSource.PlayOneShot(_pingSource.clip);
            }
            else
            {
                _held.Sounds.Play(MechSound.MagOut, SeatPose.position);
            }
            _sys.Rig.Dominant.Haptic(0.04f, 160f, 0.5f);
        }
    }
}
