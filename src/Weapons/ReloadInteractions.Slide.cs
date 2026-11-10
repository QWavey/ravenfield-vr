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
        private Vector3 RackPointWorld => Rig.ToWorld(TurnedWithBolt(RackGun + StrokeDir * (StrokeLength * ((_held.BoltLockedAlone || _held.KnobLockedAlone || _held.HandleLockedAlone) && !_racking ? 0f : Fraction))));

        /// <summary>
        /// A point of the bolt (gun space) turned about the bore as a bolt action's lifted handle is drawn (HeldWeapon.ApplyBoltTurn): the
        /// hand on the handle, and the grab, stay on it as it comes up (left where it was, the hand held the air 5 cm off a handle lifted 60 degrees).
        /// </summary>
        private Vector3 TurnedWithBolt(Vector3 gun)
        {
            float turn = BoltLifts ? _held.BoltTurn : 0f;
            if (turn <= 1e-3f)
            {
                return gun;
            }
            return Data.MuzzlePoint + Quaternion.AngleAxis(turn * BoltLiftAngle * BoltLiftSign, Data.AimAxis) * (gun - Data.MuzzlePoint);
        }

        /// <summary>
        /// The hand on the slide in gun space, against the gun as the gun hand carries it: it trails a swing by its weight (up to 8 cm,
        /// HeldWeapon.InertiaLag), and read against the gun as drawn a swing moved the bolt by the lag, as it moved a magazine in its well.
        /// </summary>
        private Vector3 HandInGun(VRHand off) => Rig.ToGun(off.Palm.position + _held.InertiaLag);

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

        // ---- a bolt action's lift (idea 9) --------------------------------------------------------------------------------

        /// <summary>How far a bolt action's handle turns up about the bore before the bolt slides (degrees).</summary>
        internal const float BoltLiftAngle = 60f;

        private int _boltLifts = -1;
        private float _boltLiftSign = 1f;

        /// <summary>
        /// A bolt action whose handle is turned up before it is pulled back (user, 2026-11-03, idea 9): worked by hand, not a pump, a long
        /// gun with a bolt or handle part of its own, its rack point out to the side of the bore (2 to 12 cm, more to the side than up
        /// or down: a lever below the gun is not one). Any other gun is pulled straight back as before.
        /// </summary>
        internal bool BoltLifts
        {
            get
            {
                if (_boltLifts < 0)
                {
                    _boltLifts = 0;
                    if (Data.Action == ActionType.Manual && Data.CyclesByHand && !IsPump && !Data.Handgun && (Rig.Slide != null || Rig.Handle != null))
                    {
                        Vector3 off = Vector3.ProjectOnPlane(Data.RackPoint - Data.MuzzlePoint, Data.AimAxis);
                        float up = Vector3.Dot(off, Data.Up);
                        float side = Mathf.Sqrt(Mathf.Max(0f, off.sqrMagnitude - up * up));
                        if (side >= 0.02f && off.magnitude <= 0.12f && Mathf.Abs(up) < side)
                        {
                            _boltLifts = 1;
                            // The turn that takes the handle up (about the bore, toward the gun's up).
                            _boltLiftSign = Vector3.Dot(Data.AimAxis, Vector3.Cross(off, Data.Up)) >= 0f ? 1f : -1f;
                            Log.InfoOnce("boltlift:" + Data.Key, $"{Data.Key}: its bolt handle ({side * 100f:0.0} cm out to the side of the bore) is turned up before the bolt slides back.");
                        }
                    }
                }
                return _boltLifts == 1;
            }
        }

        /// <summary>The way a lifted bolt turns about the bore's axis (the gun's aim axis): +1 or -1.</summary>
        internal float BoltLiftSign => BoltLifts ? _boltLiftSign : 1f;

        /// <summary>Turning the handle up (it is not pulled until it is up), or down at home after the bolt was pushed shut.</summary>
        private bool _lifting, _turningDown;
        /// <summary>Where the hand was about the bore when that turn began (gun space, off the bore's axis), and the turn then (0..1).</summary>
        private Vector3 _liftFromGun;
        private float _liftFrom;

        /// <summary>The hand's turn about the bore since <see cref="_liftFromGun"/>, in shares of the lift (up positive).</summary>
        private float HandTurn(Vector3 handGun)
        {
            Vector3 now = Vector3.ProjectOnPlane(handGun - Data.MuzzlePoint, Data.AimAxis);
            if (now.sqrMagnitude < 1e-6f || _liftFromGun.sqrMagnitude < 1e-6f)
            {
                return 0f;
            }
            return Vector3.SignedAngle(_liftFromGun, now, Data.AimAxis * _boltLiftSign) / BoltLiftAngle;
        }

        /// <summary>
        /// The handle's turn while the hand holds it: up from home before the pull (the bolt does not slide until it is up), and down at
        /// home after it was pushed shut. True while the turn holds the stroke (nothing else of the rack this frame).
        /// </summary>
        private bool UpdateBoltTurn(VRHand off, Vector3 handGun)
        {
            if (!BoltLifts)
            {
                return false;
            }
            if (_lifting)
            {
                // Either way about the bore: at home the handle can only go up, and the way up is worked out from the model, never
                // seen in a game; turned the other way it would never have lifted and the rifle could not be worked.
                float turn = Mathf.Clamp01(_liftFrom + Mathf.Abs(HandTurn(handGun)));
                // Pulled straight back 2.5 cm without the turn: taken as lifted on the way (a VR hand pulls before it turns, and a
                // bolt that stayed shut under a pull read as broken).
                if (Vector3.Dot(handGun - _rackStartGun, StrokeDir) > 0.025f)
                {
                    turn = 1f;
                }
                if (Mathf.Abs(turn - _held.BoltTurn) > 0.15f)
                {
                    off.Haptic(0.006f, 200f, 0.15f);
                }
                _held.BoltTurn = Mathf.Max(_held.BoltTurn * 0.5f, turn); // (eased: no jump back on a twitch)
                _held.SlideOffset = 0f;
                if (turn >= 0.9f)
                {
                    // Up: unlocked, it slides from here (the pull measured from where the hand is now).
                    _held.BoltTurn = 1f;
                    _lifting = false;
                    _rackStartGun = handGun;
                    _rackBase = 0f;
                    GameSounds.PlayPart(Data, MechSound.SlideBack, RackPointWorld, 0f, 0.2f, 0.35f);
                    off.Haptic(0.02f, 150f, 0.45f);
                }
                return _lifting;
            }
            if (_turningDown)
            {
                float turn = Mathf.Clamp01(1f - Mathf.Abs(HandTurn(handGun))); // (either way, as the lift: at home it can only go down)
                _held.BoltTurn = Mathf.Min(_held.BoltTurn, Mathf.Max(turn, 0f));
                if (turn <= 0.12f)
                {
                    // Down: locked shut.
                    _held.BoltTurn = 0f;
                    _turningDown = false;
                    GameSounds.PlayPart(Data, MechSound.SlideForward, RackPointWorld, 0.7f, 0.3f, 0.3f);
                    off.Haptic(0.02f, 130f, 0.45f);
                }
                return false;
            }
            return false;
        }

        /// <summary>A pump at the handguard: holding it is also the support grip, so both hands aim while pumping.</summary>
        // A shotgun's pump up to 22 cm from the support point (one analysed as a charging handle is taken at its back end:
        // the R870 CQC's and the ARS M500's could not be worked from the pump; user, 2026-10-16).
        private bool IsPump => Data.CyclesByHand && GripData.HasSupportPoint && !GripData.IsPistol
                               && Vector3.Distance(Rig.ToWorld(Data.RackPoint), GripRig.ToWorld(GripData.SupportPoint)) < (Data.Action == ActionType.ShellFed ? 0.22f : 0.15f);

        private void BeginRack(VRHand off)
        {
            _returning = false;
            _rackStartGun = HandInGun(off);
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
            _rackMoved = false;
            _strokeOut = false;
            // A bolt action shut at home is turned up first; an open one is up already.
            _turningDown = false;
            _lifting = BoltLifts && !Ammo.BoltOpen && _held.SlideOffset <= Travel * 0.02f;
            if (BoltLifts)
            {
                _liftFromGun = Vector3.ProjectOnPlane(_rackStartGun - Data.MuzzlePoint, Data.AimAxis);
                _liftFrom = _lifting ? _held.BoltTurn : 1f;
                _held.BoltTurn = _liftFrom;
            }
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
            Vector3 handGun = HandInGun(off);
            if (UpdateBoltTurn(off, handGun))
            {
                // Still turning the handle up: no pull yet. Let go, it stays as it is (it falls back down: StepParts).
                if (!off.GrabHeld)
                {
                    EndRack(off, chamber: true);
                }
                return;
            }
            // The hand's pull along the handle's (or slide's) own stroke; the slide or bolt carrier moves the same share
            // of its travel.
            float pull = Vector3.Dot(handGun - _rackStartGun, StrokeDir);
            _rackMoved |= Mathf.Abs(pull) > 0.006f;
            float fraction = Mathf.Clamp01(_rackBase + pull / StrokeLength);
            _strokeOut |= fraction > 0.25f;
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
                if (Data.EnBloc && Ammo.UsesDetachableMag && Ammo.MagInserted)
                {
                    // The rod pulled back throws the clip out with what is left in it, and the bolt stays open for the next one
                    // (user, 2026-11-10: "it should jump out when pulling back or reloading").
                    ThrowEmptyClip();
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
                _strokeOut = false; // (else the short-stroke click below played a second home sound and buzz a frame later; scout, 2026-11-10)
                Ammo.BoltOpen = false;
                Ammo.RackForward();
                _held.Nudge(Rig.AimWorld * (0.1f * hard), 10f); // the bolt or pump closing jolts the gun
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld, hard);
                off.Haptic(0.04f, 120f, 0.3f + 0.3f * hard);
                _sys.Rig.Dominant.Haptic(0.03f, 120f, 0.4f * hard);
                if (BoltLifts)
                {
                    // Shut, the handle still up: turned down by the hand from here (or by itself when let go).
                    _turningDown = true;
                    _liftFromGun = Vector3.ProjectOnPlane(handGun - Data.MuzzlePoint, Data.AimAxis);
                }
            }
            else if (!HandDriven && _rackedBack && _strokeOut && !_rackFromBoltLock && fraction <= 0.1f
                     && !(Data.LockOnEmpty && Ammo.UsesChamber && !(Ammo.FeedReady && Ammo.MagRounds > 0)))
            {
                // A spring-driven slide ridden all the way home by the hand (not let go at the back): it chambers and shuts as it
                // arrives, as softly or hard as it was brought there. It did nothing until the hand let go, the gun said RACK
                // meanwhile, and a second pull without letting go was silent (scout, 2026-11-10). Not where it would only catch
                // again on an empty magazine: that stays for the let-go.
                float closing = Mathf.Abs(moved) / Mathf.Max(Time.unscaledDeltaTime, 1e-3f);
                float hard = Mathf.Clamp01(0.2f + closing / 1.5f);
                _rackedBack = false;
                _strokeOut = false;
                Ammo.RackForward();
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld, hard);
                off.Haptic(0.03f, 120f, 0.25f + 0.3f * hard);
                _sys.Rig.Dominant.Haptic(0.03f, 120f, 0.35f * hard);
            }
            else if (HandDriven && !_rackedBack && _strokeOut && fraction <= 0.1f)
            {
                // Pushed home short of the line, its case or round still in: it shuts with a click, not in silence. Nothing is fed.
                _strokeOut = false;
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld, 0.35f);
                off.Haptic(0.02f, 120f, 0.3f);
                if (BoltLifts)
                {
                    _turningDown = true;
                    _liftFromGun = Vector3.ProjectOnPlane(handGun - Data.MuzzlePoint, Data.AimAxis);
                }
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
                Ammo.BoltLeftAt = Ammo.BoltOpen ? Fraction : 0f;
                _returning = false;
                if (_turningDown)
                {
                    // Let go before it was turned down: it falls into its notch, and says so.
                    _turningDown = false;
                    GameSounds.PlayPart(Data, MechSound.SlideForward, RackPointWorld, 0.7f, 0.3f, 0.3f);
                }
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
            if (Ammo.SlideLocked && !_rackMoved)
            {
                // Taken on its catch and let go with nothing pulled (a hand on the gun, a squeeze that meant something else): it stays on the
                // catch. It slammed home and chambered with nothing moved.
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
            else if (Fraction > 0.15f)
            {
                // A short pull let go: the slide still runs home, and clicks (it went in silence). No round is moved.
                _held.Sounds.Play(MechSound.SlideForward, RackPointWorld, Mathf.Lerp(0.2f, 0.6f, Fraction));
            }
            _rackedBack = false;
            _returning = !Ammo.SlideLocked;
            // Let go short of the back with the slide still caught: it runs back onto its catch (it jumped there in one frame; scout, 2026-11-10).
            _toCatch = Ammo.SlideLocked && Fraction < 0.97f;
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
            _shotFrame = Time.frameCount;
            if (Manual && Ammo != null && Ammo.MagLoose && Ammo.MagInserted && Ammo.UsesDetachableMag)
            {
                // Not latched (let go short of its click): the shot shakes it out of the well (UnderSeatedMagazineFalls, idea 8).
                Ammo.MagLoose = false;
                EjectMagazine();
            }
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
            if (rounds == 0)
            {
                Log.InfoOnce("enbloc-empty:" + Data.Key, $"{Data.Key}: the last round fired, the empty clip thrown out, the bolt {(Ammo.SlideLocked ? "locked back" : "NOT LOCKED")}.");
            }
            else
            {
                Log.InfoOnce("enbloc-out:" + Data.Key, $"{Data.Key}: the clip thrown out by hand (the rod or the release) with {rounds} round(s) in it.");
            }
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
