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
    /// pumps follow your hand along their real travel: the parts follow the hand. The glove grips the part it works (a
    /// bolt, a handle, a cover's latch, an optic: GraspAnchor) or, a support hand, the handguard or the gun's own
    /// support point.
    /// </summary>
    public sealed partial class ReloadInteractions
    {
        private const float EjectSeconds = 0.09f;

        private readonly WeaponSystem _sys;
        private readonly HeldWeapon _held;

        /// <summary>The match was paused (no menu: the dashboard, the headset off) last frame, so the reload froze; on the first
        /// frame back the motion baselines are re-anchored before anything advances.</summary>
        private bool _wasPaused;

        private bool _racking;
        /// <summary>This stroke's slide is held with the palm on it (else pinched).</summary>
        private bool _rackPalm;
        /// <summary>Where along the stroke the hand was last frame (for the tug the pull gives the gun).</summary>
        private float _tugStroke;
        /// <summary>The slide's speed home under its spring (m/s), from where it was let go (see StepParts).</summary>
        private float _returnSpeed;
        /// <summary>The spring's pull on a released slide (m/s per s), its top speed, and the speed whose landing jolts the gun fully.</summary>
        private const float ReturnAccel = 250f, ReturnTop = 8f, ReturnSlam = 5f;
        /// <summary>Against the back stop this stroke (its knock felt once).</summary>
        private bool _atBackStop;
        /// <summary>The slide was caught back last frame, and a frame has been seen (no click for a gun drawn already caught back).</summary>
        private bool _wasLocked, _lockSeen;
        /// <summary>The caught slide was let go short of the back: it runs back onto its catch instead of jumping there.</summary>
        private bool _toCatch;
        /// <summary>The frame of the last shot (OnShot).</summary>
        private int _shotFrame = -10;
        private Vector3 _rackStartGun;
        /// <summary>Where the hand took the pump (gun space, the pump forward): the hand stays there as the pump moves.</summary>
        private Vector3 _pumpGraspRestGun;
        private float _rackBase;
        private bool _rackedBack;
        /// <summary>The hand has moved the stroke's part since it took it (a slide taken on its catch and let go with nothing pulled stays on it).</summary>
        private bool _rackMoved;
        /// <summary>This stroke went a quarter of the way back or more (shut again short of the line, it still closes with a click).</summary>
        private bool _strokeOut;
        /// <summary>This stroke is a pump worked by the support hand's trigger: it ends when the trigger is let go.</summary>
        private bool _rackByTrigger;

        /// <summary>The under-barrel launcher's sub weapon index (-1 none, -2 not looked for yet).</summary>
        private int _launcherSub = -2;
        /// <summary>The sub weapon that was active before the support hand's trigger switched to the launcher (null: not firing it).</summary>
        private int? _launcherFrom;
        private static readonly System.Func<MagazineProp, bool> NotEmpty = m => m.Rounds > 0;

        /// <summary>
        /// Autotest: why the off hand cannot run a check now (null: it can). Written as a skip line: a check that returned
        /// silently read the same as one that passed (255.I6-4).
        /// </summary>
        private string OffHandBusy(VRHand off) =>
            off.Role != HandRole.Free ? $"the off hand is busy ({off.Role})"
            : off.Held != null ? "the off hand holds something"
            : Rig.GunBody == null ? "no gun body"
            : null;

        /// <summary>
        /// The sub weapon to go back to from the under-barrel launcher (null: none). Set when the support hand lets go of the
        /// launcher: it stays selected while it is empty or reloading (LauncherReturn).
        /// </summary>
        private int? _launcherReturn;

        /// <summary>
        /// Back from the under-barrel launcher to what the gun fired before. Let go empty or reloading, the launcher stays
        /// selected, so it can be loaded into by hand or by the game's reload (user, 2026-10-18 evening: "as soon as I let go
        /// of the shoot button it goes away, can't even reload it then"). The gun goes back once the launcher is loaded, or at
        /// once when the gun's trigger is pulled (rifleWanted). Never mid-reload: switched then, the game drops the reload
        /// (HolsterSubWeapon) and its end lands on the rifle (255.B2-1). Left alone when the player switched it meanwhile.
        /// </summary>
        private void LauncherReturn(bool rifleWanted = false)
        {
            if (!_launcherReturn.HasValue)
            {
                return;
            }
            Weapon gun = _held.Weapon;
            if (gun == null || _launcherSub < 0 || gun.activeSubWeaponIndex != _launcherSub)
            {
                _launcherReturn = null;
                return;
            }
            // Waits on the loaded state, not the reserve: the last round taken from the pouch leaves the reserve empty while it is
            // still in the hand, and the launcher has to stay selected for it to be seated.
            Weapon sub = gun.GetActiveSubWeapon();
            if (sub.reloading || !rifleWanted && !sub.HasLoadedAmmo())
            {
                return;
            }
            gun.EquipSubWeapon(_launcherReturn.Value);
            _launcherReturn = null;
        }

        /// <summary>Lights and lasers per weapon type: 0 off, 1 laser, 2 light, 3 both (as the support hand's trigger left them).</summary>
        private static readonly Dictionary<string, int> LightModes = new Dictionary<string, int>();

        private int _lightMode
        {
            // Kept per weapon type, not per draw: drawn again, the cycle restarted at "off" while the gun's light was still
            // on, and stayed out of step with it (255.B2-5).
            get => Data.Key != null && LightModes.TryGetValue(Data.Key, out int m) ? m : 0;
            set
            {
                if (Data.Key != null)
                {
                    LightModes[Data.Key] = value;
                }
            }
        }

        /// <summary>The weapon's laser and light keys, looked up twice a second (each lookup sorts a new list; 255.P1.3).</summary>
        private Input.ScriptKey _laserKey, _lightKey;
        private float _supportKeysAt = -10f;

        private void RefreshSupportKeys()
        {
            if (Time.unscaledTime - _supportKeysAt < 0.5f)
            {
                return;
            }
            _supportKeysAt = Time.unscaledTime;
            _laserKey = Input.ScriptKeys.Find(_held.Weapon, Input.ScriptKeyPurpose.Laser);
            _lightKey = Input.ScriptKeys.Find(_held.Weapon, Input.ScriptKeyPurpose.Light);
        }

        /// <summary>
        /// The support hand's trigger, the hand on the gun (user, 2026-10-16): on an under-barrel launcher it fires the
        /// launcher (switched to while the trigger is held; let go, the gun goes back to the rifle once the launcher is loaded,
        /// and the launcher stays selected while it is empty, so it can be loaded: the HK416-D's); elsewhere on the gun it
        /// steps through the lights and lasers the gun's scripts offer: laser, light, both, off (a gun with one key taps it).
        /// </summary>
        private void SupportTrigger(VRHand off)
        {
            Weapon w = _held.Weapon;
            int launcher = UnderBarrelLauncher(w);
            if (_launcherFrom.HasValue)
            {
                if (VRInput.GetHeld(DigitalAction.Use, off.Side) && off.GrabHeld && w.activeSubWeaponIndex == launcher)
                {
                    VRGameInput.SupportFire = true;
                    return;
                }
                // Let go: the gun goes back to what it was firing before (after the shot has gone: the trigger was held a
                // frame at least) once the launcher is loaded again (LauncherReturn; empty, it stays selected).
                VRGameInput.SupportFire = false;
                _launcherReturn = _launcherFrom;
                _launcherFrom = null;
                LauncherReturn();
                if (_launcherReturn.HasValue)
                {
                    Log.InfoOnce("ubgl-park:" + GripData.Key, $"{w.name}: its under-barrel launcher stays selected while it is empty or reloading, so it can be loaded; the gun goes back once it is loaded or its trigger is pulled.");
                }
                return;
            }
            if (!VRInput.GetPressed(DigitalAction.Use, off.Side) || VRGameInput.GunInputBlocked)
            {
                return;
            }
            if (launcher >= 0 && OnLauncher(w.alternativeWeapons[launcher], off.Palm.position))
            {
                VRInput.Consume(DigitalAction.Use, off.Side);
                // Pressed again while the launcher is parked (empty or reloading): the gun goes back to what it fired before the
                // first press, not to the launcher it is on. Pressed while the launcher is already the gun's selection (cycled to
                // it), the return is the rifle (-1): the launcher cannot be its own return, or the gun's trigger would never switch back.
                int returnTo = _launcherReturn ?? w.activeSubWeaponIndex;
                _launcherFrom = returnTo == launcher ? -1 : returnTo;
                _launcherReturn = null;
                if (w.activeSubWeaponIndex != launcher)
                {
                    w.EquipSubWeapon(launcher);
                }
                VRGameInput.SupportFire = true;
                off.Haptic(0.02f, 140f, 0.3f);
                return;
            }
            RefreshSupportKeys();
            Input.ScriptKey laser = _laserKey;
            Input.ScriptKey light = _lightKey;
            if (laser == null && light == null)
            {
                return;
            }
            VRInput.Consume(DigitalAction.Use, off.Side);
            if (laser != null && light != null && laser.Key != light.Key)
            {
                // Off -> laser -> light -> both -> off: the laser every step, the light every other.
                Input.ScriptKeys.Tap(laser.Key, laser.Script);
                if (_lightMode == 1 || _lightMode == 3)
                {
                    Input.ScriptKeys.Tap(light.Key, light.Script);
                }
                _lightMode = (_lightMode + 1) % 4;
            }
            else if (laser != null && light != null && laser.Script != light.Script)
            {
                Input.ScriptKeys.Tap(laser.Key); // one key, two scripts: a tap for one left the other's light dark for good
            }
            else
            {
                Input.ScriptKey only = laser ?? light; // one key: the gun's own script steps its modes
                Input.ScriptKeys.Tap(only.Key, only.Script);
            }
            off.Haptic(0.015f, 200f, 0.25f);
            _held.Sounds.Play(MechSound.Selector, off.Palm.position, 0.3f); // a small click of the switch
        }

        /// <summary>The weapon's under-barrel launcher (a sub weapon firing an exploding round, a few at most), or -1.</summary>
        private int UnderBarrelLauncher(Weapon w)
        {
            if (_launcherSub != -2)
            {
                return _launcherSub;
            }
            _launcherSub = -1;
            for (int i = 0; w.alternativeWeapons != null && i < w.alternativeWeapons.Count; i++)
            {
                Weapon sub = w.alternativeWeapons[i];
                if (sub == null || sub.configuration == null || sub.configuration.projectilePrefab == null || sub.configuration.ammo < 1 || sub.configuration.ammo > 6)
                {
                    continue;
                }
                Projectile p = sub.configuration.projectilePrefab.GetComponent<Projectile>();
                if (p is ExplodingProjectile || p is GrenadeProjectile)
                {
                    _launcherSub = i;
                    Log.InfoOnce("ubgl:" + Data.Key, $"{w.name}: its under-barrel launcher '{sub.name}' is fired by the support hand's trigger.");
                    break;
                }
            }
            return _launcherSub;
        }

        /// <summary>The palm is on the launcher: up to 30 cm behind its muzzle along the barrel, within 12 cm of its line.</summary>
        private bool OnLauncher(Weapon sub, Vector3 palm)
        {
            Transform m = sub.configuration.muzzles != null && sub.configuration.muzzles.Length > 0 ? sub.configuration.muzzles[0] : null;
            if (m == null)
            {
                return true; // nowhere to measure from: the support hand anywhere on the gun
            }
            Vector3 aim = Rig.AimWorld;
            Vector3 rel = palm - m.position;
            float back = -Vector3.Dot(rel, aim);
            return back > -0.05f && back < 0.3f && (rel + aim * back).magnitude < 0.12f;
        }

        /// <summary>
        /// The support hand on a pump squeezes its trigger: it works the pump (only then; held without the trigger the pump
        /// is the support grip and stays where it is). Within 20 cm of the pump's grasp: a short pump far down a long tube.
        /// </summary>
        private bool PumpByTrigger(VRHand off)
        {
            return IsPump && Manual && Ammo.UsesChamber && off.GrabHeld && VRInput.GetPressed(DigitalAction.Use, off.Side)
                   && !VRGameInput.GunInputBlocked && Vector3.Distance(off.Palm.position, RackPointWorld) < (Data.Action == ActionType.ShellFed ? 0.25f : 0.2f);
        }
        /// <summary>This stroke took the handle home while only its bolt was locked back (an M4 on an empty magazine).</summary>
        private bool _rackFromBoltLock;

        /// <summary>Where the support hand took the pump in this hold (the gun's frame), for <see cref="PumpCue"/>; tracked or not.</summary>
        private Vector3 _pumpHoldFrom;
        private bool _pumpHoldTracked, _pumpCueGiven;

        /// <summary>
        /// A pump pulled back with the grip alone (3 cm along its stroke, the trigger up) says once per hold that the trigger
        /// works it, with a tick: it is the user's rule (only with the trigger, 2026-10-16), and the pull moved nothing and said
        /// nothing, so the pump read as stuck (Tasks 181, "the pump sometimes does not move at all"; sweep round 2).
        /// </summary>
        private void PumpCue(VRHand off)
        {
            if (!IsPump || !Manual || !Ammo.UsesChamber || !off.GrabHeld)
            {
                _pumpHoldTracked = false;
                return;
            }
            Vector3 palm = Rig.ToGun(off.Palm.position);
            if (!_pumpHoldTracked)
            {
                _pumpHoldTracked = true;
                _pumpCueGiven = false;
                _pumpHoldFrom = palm;
                return;
            }
            if (!_pumpCueGiven && !VRInput.GetHeld(DigitalAction.Use, off.Side) && Vector3.Dot(palm - _pumpHoldFrom, StrokeDir) > 0.03f)
            {
                _pumpCueGiven = true;
                _sys.FlashMessage("TRIGGER TO PUMP");
                off.Haptic(0.02f, 90f, 0.3f);
            }
        }
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

        /// <summary>The magazine release only unlatches this gun's magazine (AkMagazinesStayIn: a curved magazine on a rifle).</summary>
        private bool StaysInOnRelease => VRConfig.AkMagazinesStayIn.Value && Data.MagCurved && !Data.IsPistol && !Data.IsMachineGun && !Data.EnBloc;

        public void Update()
        {
            VRHand dom = _sys.Rig.Dominant;
            VRHand off = _sys.Rig.Off;
            if (Manual && Ammo != null && Ammo.UsesDetachableMag && !GameManager.IsPaused() && !VRGameInput.GunInputBlocked) // (nor under a menu)
            {
                UpdateDetent(off, dom);
            }

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
                else if (Ammo.UsesDetachableMag && Ammo.MagInserted && Ammo.MagRounds >= Mathf.Max(1, Ammo.Capacity - 1)
                         && !MagazineProp.KeepsLoadedLook(Data.Key) && _held.SettledLoaded)
                {
                    // (Only once the gun has settled loaded: see HeldWeapon.SettledLoaded.)
                    // Its magazine as it sits full (one in the chamber at most): a full one from the pouch is drawn with it. With one
                    // round left the copy showed what that magazine's model shows then: an en-bloc clip whose rounds go as they are
                    // fired (the M1 GARRET's) was copied with none in it, and every clip from the pouch looked empty (user,
                    // 2026-11-03: "you take an empty mag from your pouch and not a full one").
                    MagazineProp.KeepLoadedMagazine(Rig);
                }
                else if (Ammo.UsesDetachableMag && Ammo.MagInserted && Ammo.MagRounds == 0 && !Ammo.Chambered && !Data.EnBloc
                         && !MagazineProp.KeepsEmptyLook(Data.Key) && _held.SettledEmpty)
                {
                    // And as it sits shot dry: an empty magazine (from the pouch with magazines loaded by hand) is drawn with it, not as
                    // the full one the gun may hold when it is taken. (Not an en-bloc clip: it leaves the gun as it empties.)
                    MagazineProp.KeepEmptyMagazine(Rig);
                }
                if (VRInput.GetPressed(DigitalAction.MagRelease, dom.Side) && !VRGameInput.GunInputBlocked)
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
                    else if (Ammo.UsesDetachableMag && Ammo.MagInserted && StaysInOnRelease)
                    {
                        // An AK-style magazine does not drop free: the release only unlatches it, and the hand rocks it out
                        // (AkMagazinesStayIn, idea 10). Heard and felt as the latch lets go.
                        GameSounds.PlayPart(Data, MechSound.MagOut, SeatPose.position, 0f, 0.2f, 0.35f);
                        dom.Haptic(0.02f, 140f, 0.4f);
                    }
                    else if (Ammo.UsesDetachableMag && Ammo.MagInserted && Data.EnBloc)
                    {
                        ThrowEmptyClip(); // (an M1's clip latch: the clip jumps out of the top with its rounds, it does not slide down a rail)
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
                // Snap it shut with a flick of the wrist, like the real thing. (Not in a pause, the dashboard or a menu: the match is
                // frozen, as the release buttons are gated; round 11.)
                // (Nor with a shell on its way in: the action snapped shut on it and it jumped back to the hand.)
                if (Breaks && Ammo.Open && !_coverHeld && !VRGameInput.GunInputBlocked && Time.unscaledTime - _openedAt > 0.35f && dom.OwnAngularVelocity.magnitude > 9f
                    && !(off.Held is MagazineProp seating && seating.OnRail))
                {
                    SetBreakOpen(false);
                }
                // A machine gun's feed cover slapped shut with the open hand, as it is closed in a hurry: a quick downward hit
                // on it (it had to be taken and pushed down all the way).
                // (Over the cover's rear half, as the grab; a hit of 1.5 m/s, not any brisk move down; not in the half second
                // after the cover was let go: a hand dropping away from it shut it again; 255.B2-4, B6-4.)
                if (Ammo.HasCover && Ammo.CoverOpen && !_coverHeld && off.Tracked && off.Role == HandRole.Free && !off.GrabHeld
                    && Time.unscaledTime - _coverLetGoAt > 0.5f && CoverZoneDistance(off.Palm.position) < 0.1f
                    && !VRGameInput.GunInputBlocked && Vector3.Dot(off.BodyRelativeVelocity - dom.BodyRelativeVelocity, -Rig.UpWorld) > 1.5f)
                {
                    Ammo.SetCoverOpen(false);
                    _held.Nudge(-Rig.UpWorld * 0.08f, 8f);
                    _held.Sounds.Play(MechSound.CoverShut, MechSound.SlideForward, CoverGrabWorld);
                    off.Haptic(0.05f, 110f, 0.8f);
                    dom.Haptic(0.04f, 110f, 0.5f);
                }
                if (VRInput.GetPressed(DigitalAction.SlideRelease, dom.Side) && Ammo.SlideLocked && !_racking && !VRGameInput.GunInputBlocked)
                {
                    VRInput.Consume(DigitalAction.SlideRelease, dom.Side);
                    // The thumb on the slide or bolt release: the gun hand's on a pistol, the support hand's on the gun otherwise.
                    VRHand presses = !Data.IsPistol && off.Role == HandRole.Foregrip ? off : dom;
                    presses.Model?.ThumbPress();
                    ReleaseSlide();
                }
                else if (VRConfig.TriggerReleasesSlide.Value && Ammo.SlideLocked && !_racking && VRInput.GetPressed(DigitalAction.Fire, dom.Side)
                         && !VRGameInput.GunInputBlocked && Ammo.FeedReady && Ammo.MagRounds > 0)
                {
                    // The trigger on a gun locked open sends the slide or bolt forward, as the slide release would: it chambers a
                    // round from a loaded magazine. With none to chamber, the slide stays back (AmmoState.RackForward), so this
                    // branch is skipped and the pull is a dry one: the game's dry-fire click (WeaponCanFirePatch, 264). That pull
                    // does not also fire.
                    ReleaseSlide();
                    Ammo.HoldFireUntilTriggerUp = true;
                }
                if (Ammo.HoldFireUntilTriggerUp && !VRInput.GetHeld(DigitalAction.Fire, dom.Side))
                {
                    Ammo.HoldFireUntilTriggerUp = false;
                }
            }

            // Parked on the under-barrel launcher (its trigger let go, the launcher empty or reloading) and the gun's trigger pulled:
            // the rifle is wanted. The pull does not fire the launcher meanwhile (SuppressFire), and the rifle fires on the held
            // trigger as soon as it is the active weapon (user, 2026-10-18 evening). Not in a menu, nor with the laser on a panel.
            bool gunTriggerWanted = VRInput.GetHeld(DigitalAction.Fire, dom.Side) && !VRGameInput.GunInputBlocked;
            if (gunTriggerWanted && !_launcherReturn.HasValue && !_launcherFrom.HasValue && VRInput.GetPressed(DigitalAction.Fire, dom.Side))
            {
                // Drawn again after a holster with the launcher still selected and empty: the parking went with the held weapon it
                // was on, so the rifle is the return here. Without it the gun's trigger only dry-clicked on the empty launcher. Only a
                // fresh pull: a launcher emptied by a shot made on a held trigger does not switch away mid-hold.
                Weapon gunWeapon = _held.Weapon;
                int launcher = UnderBarrelLauncher(gunWeapon);
                Weapon sub = launcher >= 0 && gunWeapon.activeSubWeaponIndex == launcher ? gunWeapon.GetActiveSubWeapon() : null;
                if (sub != null && !sub.reloading && !sub.HasLoadedAmmo())
                {
                    _launcherReturn = -1;
                    Log.InfoOnce("ubgl-redrawn:" + GripData.Key, $"{gunWeapon.name}: its under-barrel launcher is still selected and empty when the gun's trigger is pulled: the gun goes back to the rifle.");
                }
            }
            if (gunTriggerWanted && _launcherReturn.HasValue)
            {
                LauncherReturn(rifleWanted: true);
                if (_launcherReturn.HasValue)
                {
                    VRGameInput.SuppressFire = true; // still on the launcher (reloading): its switch waits, the pull does not fire it
                }
            }

            if (!off.Tracked)
            {
                _offLostTracking = true;
                if (_coverHeld)
                {
                    // The hand is gone (tracking lost, or a test hand handed back): the cover is let go where it is. Held on,
                    // it stayed frozen part way open and its latch out of reach (check-1017h: the mg3's and MK46's covers
                    // at 59% after the cover check, the machine gun reload's squeeze at the latch took nothing).
                    EndCover(off);
                }
                if (_beltHeld)
                {
                    LetBeltHang(off); // the belt too: held on, it stayed drawn to where the lost hand was (review, 2026-10-24)
                }
                return;
            }
            if (_offLostTracking)
            {
                // Back in tracking with the grip still closed: an optic hold measures its slide again from where the hand is now. Its
                // jump back from the lost pose read as a sideways slide and switched the sight with no press (grab-zone hunt,
                // 2026-10-21).
                _offLostTracking = false;
                // A rack too: its stroke was measured from where the hand began it, and the hand came back somewhere else (the lost
                // pose rides with the body). The first tracked frame read the jump as a full pull, ejected the chambered round and
                // chambered the next (round-2 scout, 2026-10-27).
                ReanchorMotion(off);
            }
            bool paused = GameManager.IsPaused();
            if (_wasPaused && !paused)
            {
                // Back from a pause: re-anchor the motion baselines so a hand that moved while paused does not jump a kick or
                // a rack on the first frame back (the cockpit re-anchors its wheel and stick the same way, 2026-10-21).
                ReanchorMotion(off);
            }
            _wasPaused = paused;
            // Grenades: the pin (ThrowableHandler) and loose props (WeaponSystem) take the off hand's grab. A grip closed a
            // moment before the hand reaches the bolt or magazine still takes it (nothing else has claimed the press).
            if (off.GrabPressed)
            {
                _pressPalm = off.Palm.position; // where this squeeze began (the pouch only takes squeezes made at it)
            }
            // Not with the pause menu up: a squeeze there pulled the magazine or a clip out of the paused gun (sound hunt, 2026-10-20).
            // Nor paused with no menu (the dashboard, the headset off; input hunt, 2026-10-21).
            if ((off.GrabPressed || off.GrabReaching) && off.Role == HandRole.Free && Data.Action != ActionType.Throwable && !Util.GameContext.GameMenuOpen
                && !paused)
            {
                TryGrab(off);
            }
            if (paused)
            {
                // Paused: no reload interaction advances (no seat, kick, stow, cover, belt, optic, rack or support let-go). The
                // hand keeps its role and prop and carries on from here on resume (scout finding, 2026-10-21: an in-progress
                // reload still seated and kicked while paused because only the grab above was gated, not the switch below).
                return;
            }
            if (_launcherFrom.HasValue && off.Role != HandRole.Foregrip)
            {
                // The support hand left the launcher with its trigger still held: let go here, so the gun goes back to what it fired
                // before once the launcher is loaded (empty, it stays selected).
                _launcherReturn = _launcherFrom;
                _launcherFrom = null;
            }
            LauncherReturn();
            if (off.Role != HandRole.Foregrip)
            {
                _supportRest = -1f; // the next hold measures its own grab
            }
            if (off.Role != HandRole.Foregrip)
            {
                _pumpHoldTracked = false; // (a hold of the pump measures its pull from its own grab)
            }
            switch (off.Role)
            {
                case HandRole.Foregrip:
                {
                    RefreshSupportKeys();
                    bool works = IsPump && Manual || UnderBarrelLauncher(_held.Weapon) >= 0; // the trigger works the gun itself
                    VRGameInput.SupportTriggerOwned = works || _laserKey != null || _lightKey != null;
                    VRGameInput.SupportTriggerLightsOnly = !works && VRGameInput.SupportTriggerOwned;
                    if (PumpByTrigger(off))
                    {
                        // The trigger squeezed on the pump: the hand takes the pump and works it for as long as it is held.
                        BeginRack(off);
                        _rackByTrigger = true;
                        break;
                    }
                    PumpCue(off);
                    if (Manual && Ammo.UsesChamber && !IsPump && !GripData.IsPistol)
                    {
                        // Slid back along the gun onto the charging handle with the grip still closed: the hand takes the handle. It
                        // stayed on the handguard (it lets go only at 60 cm of stretch) and the rack never started, a rack being taken
                        // by a squeeze and this squeeze the handguard's (user, 2026-10-20 night: "i cannot pull back the bolt thingy
                        // again"; rack trace). As a new grab decides: well within the handle's zone, and the handle clearly nearer
                        // than the handguard line (half its distance). Measured on the gun as the gun hand alone holds it (see
                        // PalmOnLiveGun): steered, the handle swung away from the palm coming for it.
                        Vector3 palmNow = _held.TwoHanded ? PalmOnLiveGun(off.Palm.position) : off.Palm.position;
                        float dRack = RackZoneDistance(palmNow);
                        if (dRack < VRConfig.GrabRadius.Value && dRack < 0.5f * ForegripDistance(palmNow))
                        {
                            _held.SetTwoHanded(false);
                            BeginRack(off);
                            break;
                        }
                    }
                    SupportTrigger(off);
                    // Pulled away with the grip still closed, the support hand stays on (Boneworks style): its glove keeps to
                    // the handguard, a buzz grows with the stretch, and it lets go only well past an arm's slack (60 cm), with
                    // a clear knock. At 35 cm it slipped off in a hard pull.
                    // Measured on the gun as the gun hand alone holds it (UnsteeredGunPoint): steered (TwoHanded), the bore turns onto
                    // the off palm, so the live gun moved with every pull and the stretch never grew (task 206; 2026-10-19 finding).
                    // A pistol's cup is measured where it is (it is not steered). A support held on its own point (a rear grip behind
                    // the trigger grip, a magazine's or a launcher's grip) is measured from that point, from where the hold took it:
                    // the handguard line runs forward from the grip, so a palm on a rear grip read as stretched at rest and the buzz
                    // ran on every hold (task 276). On the handguard, the distance to the line, not to a point, so a hand slid along
                    // it does not stretch it (a long handguard taken far from its support point let go on the frame it was taken;
                    // task 133).
                    float stretch;
                    if (GripData.IsPistol)
                    {
                        stretch = Vector3.Distance(off.Palm.position, GripRig.ToWorld(GripData.SupportPoint));
                    }
                    else if (SupportAtOwnPoint)
                    {
                        float atPoint = Vector3.Distance(off.Palm.position, UnsteeredGunPoint(GripData.SupportPoint));
                        if (_supportRest < 0f)
                        {
                            _supportRest = atPoint;
                        }
                        stretch = Mathf.Max(0f, atPoint - _supportRest);
                    }
                    else if (_held.TwoHanded)
                    {
                        // The gun as the gun hand alone holds it is off the steered one by how the wrist sits on a two-handed aim
                        // (10-25 deg: 7-17 cm at 40 cm, over the 15 cm buzz), so its distance counts from a rest that follows a slow
                        // change (the wrist settling into the aim) but not a pull: down at once, up 5 cm a second. The live line
                        // still counts too: a hand pulled off the end of the handguard.
                        float unsteered = Vector3.Distance(off.Palm.position, UnsteeredHandguardPoint(off.Palm.position));
                        _supportRest = _supportRest < 0f ? unsteered : Mathf.Min(unsteered, _supportRest + 0.05f * Time.unscaledDeltaTime);
                        stretch = Mathf.Max(Vector3.Distance(off.Palm.position, HandguardPoint(off.Palm.position)), unsteered - _supportRest);
                    }
                    else
                    {
                        stretch = Vector3.Distance(off.Palm.position, HandguardPoint(off.Palm.position));
                    }
                    // Away from where its glove holds the gun: the glove stays where it gripped (task 324), and a pull along the
                    // barrel measured nothing against the handguard's line, so the hand could slide off down the gun with the
                    // glove left behind and never let go (hold review, 2026-10-20 night).
                    if (off.Model != null && off.Model.SnapPose.HasValue)
                    {
                        stretch = Mathf.Max(stretch, Vector3.Distance(off.Model.TrackedGlovePose.position, off.Model.SnapPose.Value.position));
                    }
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
                    if (_rackByTrigger)
                    {
                        VRGameInput.SupportTriggerOwned = true; // the pump's trigger is the gun's while it works it
                        VRGameInput.SupportTriggerLightsOnly = false;
                    }
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

        private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return Vector3.Distance(p, a + ab * t);
        }

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
            // (In the pouch an empty one is passed over in the search itself: dropped beside a full one, it hid the full one
            // and the pouch spent spare rounds; 255.I1-5.)
            // (By the pouch, not only in it: an empty one lying just outside its 17 cm, the M1 GARRET's clip thrown out by the hip,
            // was the nearest thing to a squeeze at the pouch's edge and was handed over; user, 2026-11-03: "you take an empty mag
            // from your pouch and not a full one". The one just thrown out too, caught there.)
            bool byPouch = Vector3.Distance(palm, _sys.Zones.Pouch) < BodyZones.PouchRadius + 0.10f;
            System.Func<MagazineProp, bool> accept = byPouch ? NotEmpty : null;
            MagazineProp caught = _ejecting != null && _ejecting.DistanceTo(palm) < 0.13f
                ? _ejecting
                : MagazineProp.NearestDropped(palm, 0.13f, Data.Key, accept) ?? MagazineProp.NearestDropped(palm, 0.13f, null, accept);
            if (caught != null && caught.Rounds == 0 && byPouch)
            {
                // Reaching into the pouch takes a full one from it, not the empty magazine just dropped by the hip: the
                // emptied M1 GARRET clip lay there and was handed back (user, 2026-10-16: "I grab an empty mag from the pouch").
                caught = null;
            }
            if (caught != null)
            {
                Consider(1, caught.DistanceTo(palm) / 0.13f * 0.4f, ref choice, ref best);
            }
            if (Manual && Ammo.UsesChamber && !IsPump) // a pump is worked by the support hand's trigger (PumpByTrigger)
            {
                float rackRadius = radius * 1.3f;
                float d = RackZoneDistance(palm);
                if (d < rackRadius)
                {
                    // Slight preference: racking is the most precise target.
                    Consider(2, d / rackRadius * 0.85f, ref choice, ref best);
                }
            }
            // (Not an en-bloc clip with the operating rod's handle in reach: the M1 GARRET's clip sits 5 cm from it, and a hand on the
            // handle took the clip instead, so the rod could not be pulled back; user, 2026-11-03: "the m1garand ... the thing isnt
            // able to be pulled back". A clip is not pulled out of an M1 by hand: the rod is worked and it pings out empty.)
            // (2026-11-10: not by hand at all. Taken out of the full gun it left the bolt shut on a round with no clip in, and the
            // clip in the hand was refused there: "the m1 garand isnt reloadable". The rod or the release throws it out.)
            if (Manual && Ammo.UsesDetachableMag && Ammo.MagInserted && _ejecting == null && !Ammo.BoxLocked && !Data.EnBloc)
            {
                float d = Vector3.Distance(palm, SeatedMagBaseWorld);
                if (d < radius * 1.1f)
                {
                    Consider(6, d / (radius * 1.1f), ref choice, ref best);
                }
            }
            // (A revolver's crane too, swung out: pushed shut by hand. It was only B or the flick, and a hand on it did nothing. Shut, the cylinder is
            // where the hand cups the gun: it is not opened so.)
            if (Manual && (Data.Hinge == HingeKind.FeedCover || Data.Hinge == HingeKind.BreakAction && Breaks
                           || Data.Hinge == HingeKind.Cylinder && Breaks && Ammo.Open && Data.HingePath != null))
            {
                float d = CoverZoneDistance(palm);
                if (d < radius)
                {
                    // Weighed as the rack is: at 0.9 a charging handle under the cover's edge won where both were in reach.
                    // With the box in and its belt still to lay, the belt is the next step: the cover weighs full, so a belt
                    // end about as near wins (the cover's latch was taken instead; 255.I3-8).
                    bool beltNext = VRConfig.BeltByHand.Value && Ammo.BeltToLay;
                    Consider(5, d / radius * (beltNext ? 1f : 0.65f), ref choice, ref best);
                }
            }
            if (Manual && VRConfig.BeltByHand.Value && Ammo.HasCover && Ammo.CoverOpen && Ammo.MagInserted && !Ammo.BeltLaid && Ammo.MagRounds > 0)
            {
                // At the box mouth, or anywhere along the belt hanging from it (it slipped from the hand).
                float d = Vector3.Distance(palm, BeltStartWorld);
                if (_beltHanging)
                {
                    // Along the belt as it hangs: out over the box's side, then down beside it (a straight line from the mouth to its
                    // end ran through the box, where a hand reaching for the belt found the box first).
                    Vector3 side = BeltSideExit;
                    d = Mathf.Min(d, Mathf.Min(DistanceToSegment(palm, BeltStartWorld, side), DistanceToSegment(palm, side, BeltEndDrawn)));
                }
                if (d < radius)
                {
                    Consider(7, d / radius * 0.8f, ref choice, ref best); // the belt end is the obvious next step
                }
            }
            if (HasSwitchableSight)
            {
                float d = OpticDistance(palm);
                // Not where the charging handle is about as near: the sight line runs back over an AR's T (its zone starts 5 cm
                // behind the grip), and a palm a few centimetres under the T took the sight switch, not the handle (rack trace,
                // 2026-10-20 night). At the optic itself the sight is clearly nearer and still taken.
                float dRackHere = Manual && Ammo.UsesChamber && !IsPump ? RackZoneDistance(palm) : float.MaxValue;
                // Nor where the handguard is about as near: the sight line runs 30 cm on over it, and a support hand taking the
                // handguard from above (palm 4 cm over the bore, the sight line at 6.5) switched the sight instead of holding the gun
                // (grab-zone hunt, 2026-10-21).
                float dForeHere = !GripData.IsPistol ? ForegripDistance(palm) : float.MaxValue;
                if (d < 0.06f && !(dRackHere < radius * 1.3f && dRackHere < d + 0.03f) && !(dForeHere < radius * 1.4f && dForeHere < d + 0.03f))
                {
                    Consider(8, d / 0.06f * 0.9f, ref choice, ref best);
                }
            }
            // The pouch only answers a squeeze made at it: a squeeze that found nothing elsewhere must not pull a magazine
            // out when the hand passes the hip a moment later. Parts of the gun win when about as close.
            // (Not when the seated magazine is within reach: the pouch won, spent a magazine's rounds from the reserve and
            // the fresh one could not go in; 255.I1-1.)
            if (Manual && _sys.Zones.InPouch(palm) && !(caught != null && Ours(caught)) // (a magazine of this gun in reach wins, as said above: by the scores a pouch 5 cm off took a squeeze 8 cm from it)
                && !(Ammo.UsesDetachableMag && Ammo.MagInserted && Vector3.Distance(palm, SeatedMagBaseWorld) < radius * 1.1f)
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
                // A palm within the charging handle's zone is the handle's unless it is clearly nearer the handguard (half the rack
                // distance): the CZ Bren 2's knob sits at the front of the receiver where the handguard line starts, and about 6 cm
                // off the knob's tip toward the support point the handguard won (Bren hunt, 2026-10-19, task 302).
                float dRack = !GripData.IsPistol && Manual && Ammo.UsesChamber && !IsPump ? RackZoneDistance(palm) : float.MaxValue;
                bool handleHolds = dRack < radius * 1.3f;
                if (d < r && (!handleHolds || d < 0.5f * dRack))
                {
                    Consider(4, d / r, ref choice, ref best);
                }
            }

            // A pump gun's support hand on the pump holds the gun there; it works the pump only while its trigger is held
            // (see the foregrip below). Taken as the pump at once, a hand resting on it racked it with every move (user,
            // 2026-10-16: "you rack it the whole time because you're holding it").

            if (choice == 0 && off.GrabPressed && Core.AutoTest.Requested)
            {
                // Autotest: a squeeze that took nothing, with how far each zone was (mg3 and MK46 ET: the first squeeze at the
                // cover's latch was not taken in the machine gun reload check, the cover check's at the same spot was).
                Log.Info($"Grab on {Data.Key} took nothing (radius {radius * 100f:0.0} cm): "
                         + $"{(Data.Hinge != HingeKind.None ? $"cover {CoverZoneDistance(palm) * 100f:0.0} cm, " : "")}"
                         + $"{(Manual && Ammo.UsesChamber ? $"rack {RackZoneDistance(palm) * 100f:0.0} cm, " : "")}"
                         + $"{(Ammo.UsesDetachableMag && Ammo.MagInserted ? $"seated magazine {Vector3.Distance(palm, SeatedMagBaseWorld) * 100f:0.0} cm, " : "")}"
                         + $"manual {Manual}, cover {(Ammo.HasCover ? Ammo.CoverOpen ? "open" : "shut" : "none")}, openness {_held.HingeOpenness:0.00}");
            }

            if (choice == 0 && off.GrabPressed && HandLoading.TakeLying(off))
            {
                // Loading by hand: a case or round lying within reach, with a gun drawn too (only a hand no gun manages took them, and
                // the user asked for them grabbable). HandLoading has it from here.
                VRInput.Consume(DigitalAction.Grab, off.Side);
                return;
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
                    TakeHandguard(off);
                    Log.InfoOnce("support-stretch:" + GripData.Key, $"{GripData.Key}: the support hand's stretch is measured from {(SupportAtOwnPoint ? "its own support point (a pistol's cup, a rear grip, a magazine's or a launcher's grip)" : "the handguard line")}.");
                    if (GripData.IsPistol && !GripData.HasSupportPoint)
                    {
                        // Its cup is only estimated (no left-hand bone): the support glove settles onto it (UpdateGraspAnchor).
                        Log.InfoOnce("pistol-cup-glove:" + GripData.Key, $"{GripData.Key}: no left-hand bone: the support glove settles onto the grip's cup, a few centimetres.");
                    }
                    break;
                default:
                    return;
            }
            VRInput.Consume(DigitalAction.Grab, off.Side);
        }

        private void TakeHandguard(VRHand off)
        {
            off.Role = HandRole.Foregrip;
            // The rest is taken again for this hold: the reset above runs only on the frames the hand is not holding, so a
            // hold taken on the frame after the last one let go kept the old rest.
            _supportRest = -1f;
            _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value && !GripData.IsPistol);
            off.Haptic(0.02f, 120f, 0.35f);
            if (Data.Cues.ContainsKey(MechSound.HandGrab))
            {
                _held.Sounds.Play(MechSound.HandGrab, off.Palm.position, 0.6f); // (the hand on the handguard, where the mod ships its sound)
            }
        }

        /// <summary>
        /// A hand still closed on the handguard as the gun is taken up again in the other (a gun held where one hand held it, taken
        /// back by its grip: WeaponSystem's regrip): it keeps its hold there instead of closing on nothing until squeezed again. True
        /// when it took it.
        /// </summary>
        internal bool KeepHandguard(VRHand off)
        {
            if (!off.Tracked || !off.GrabHeld || off.Role != HandRole.Free || off.Held != null || !(GripData.HasSupportPoint || GripData.IsPistol)
                || ForegripDistance(off.Palm.position) > (GripData.IsPistol ? 0.12f : VRConfig.GrabRadius.Value * 1.4f))
            {
                return false;
            }
            TakeHandguard(off);
            return true;
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

        // ---- per-frame visuals -----------------------------------------------------------------------

        /// <summary>
        /// The slide's and the hinge's offsets for this frame (locked open, a bolt left open, the spring slamming it home, a
        /// hinge settling). Run before the drawn parts are posed: written after them, each change showed a frame late
        /// (255.B7-2).
        /// </summary>
        internal void StepParts()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            if (!_returning)
            {
                _returnSpeed = 0f; // (each return from a standing start)
            }
            // Caught back on an empty magazine (the last round fired, or racked with nothing to feed): a light click and a tap in the
            // gun hand, the end of its own slide-back sound (user, 2026-11-03, idea 6: told empty before looking).
            bool locked = Ammo != null && Ammo.SlideLocked;
            if (locked && !_wasLocked && _lockSeen)
            {
                if (Time.frameCount - _shotFrame > 2)
                {
                    GameSounds.PlayPart(Data, MechSound.SlideBack, RackPointWorld, 0.7f, 0.3f, 0.4f, 1.12f); // (after a shot OnShot plays its own)
                }
                _sys.Rig.Dominant.Haptic(0.025f, 180f, 0.45f);
            }
            _wasLocked = locked;
            _lockSeen = true;
            if (!_racking && BoltLifts)
            {
                // Not in the hand: up while the bolt is open, down once it is shut (let go at home it falls into its notch).
                _held.BoltTurn = Ammo != null && Ammo.BoltOpen ? 1f : 0f;
            }
            if (Ammo != null && Ammo.SlideLocked && !_racking)
            {
                if (_toCatch && _held.SlideOffset < Travel)
                {
                    _held.SlideOffset = Mathf.MoveTowards(_held.SlideOffset, Travel, Mathf.Max(Travel, 0.02f) * dt / 0.05f); // (its whole travel in a twentieth of a second)
                }
                else
                {
                    _toCatch = false;
                    _held.SlideOffset = Travel;
                }
                _returning = false;
            }
            else if (Ammo != null && Ammo.BoltOpen && !_racking)
            {
                // A manual action left open stays open (back to open if the weapon was just drawn again), as far as it was left: drawn fully
                // open, a bolt left half way jumped back, and the first touch threw out the round it never reached the line for.
                if (_held.SlideOffset <= 0f)
                {
                    _held.SlideOffset = Travel * (Ammo.BoltLeftAt > 0f ? Ammo.BoltLeftAt : 1f);
                }
            }
            else if (_returning)
            {
                // Spring-driven slides slam home in a few hundredths of a second, jolting the gun forward and down: from where it was
                // let go it speeds up all the way home (it went at a steady 6 m/s), and the jolt is as hard as it got fast, so one
                // let go from the back slams and one ridden nearly home closes gently (user, 2026-11-03, idea 3).
                _returnSpeed = Mathf.Min(_returnSpeed + ReturnAccel * dt, ReturnTop);
                _held.SlideOffset = Mathf.MoveTowards(_held.SlideOffset, 0f, _returnSpeed * dt);
                if (_held.SlideOffset <= 0f)
                {
                    _returning = false;
                    _held.Nudge(Rig.AimWorld * (0.22f * Mathf.Clamp01(_returnSpeed / ReturnSlam)), 25f);
                    _returnSpeed = 0f;
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
        }

        public void BeforeRender()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            UpdateEject(dt);
            DrawAlmostSeated();
            // Press check: eased back part way (not far enough to throw it out), the chambered round shows in the port.
            float open = Ammo != null && Ammo.UsesChamber ? Fraction : 0f;
            Casings.ShowChamber(_held, open > 0.12f && open < 0.8f && (Ammo.Chambered || Ammo.SpentInChamber), Ammo != null && Ammo.Chambered);
            VRHand off = _sys.Rig.Off;
            UpdateGraspAnchor(off);
            UpdateBeltStrip(off);
            PoseSeatedHold(off);
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
            // The palmed one after the held one: it goes with the first's keep-out of this frame (posed before it, it took the last
            // frame's and parted from it by that frame's change while the first was pressed on the gun; round-6 scout).
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
                    float into = _palmedAt >= 0f ? Mathf.Clamp01((Time.unscaledTime - _palmedAt) / 0.12f) : 1f; // (at 0.18 s it trailed a moving hand by 5 cm per m/s)
                    if (into < 1f)
                    {
                        float e = into * into * (3f - 2f * into);
                        RigidPose from = MagazineProp.FromRigSpace(_palmedFrom);
                        palmedPose = new RigidPose(Vector3.Lerp(from.position, palmedPose.position, e), Quaternion.Slerp(from.rotation, palmedPose.rotation, e));
                    }
                    else
                    {
                        _palmedAt = -1f;
                    }
                    // It stays out of the gun on its own, easing out of the well too (it went through the gun meanwhile;
                    // 255.I1-2); the glove follows it only once it is in the hand and the fresh one is not on its rail.
                    palmedPose = _palmed.KeptOffGun(palmedPose, moveHand: into >= 1f && !(off.Held is MagazineProp onRail && onRail.OnRail));
                    palmedPose.ApplyTo(_palmed.Go.transform);
                }
            }
            KeepGloveOutAndRemember(off);
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
                            // Held on its own point (a rear grip, a magazine's or a launcher's grip): the glove stays on it, where the
                            // weapon's own support pose puts the hand, and does not slide onto the barrel line when the snap is lost
                            // (the PPSh's drum; 2026-10-19 finding, task 277).
                            anchor = SupportAtOwnPoint ? GripRig.ToWorld(GripData.SupportPoint) : HandguardPoint(off.Palm.position);
                        }
                        else if (!GripData.HasSupportPoint)
                        {
                            // A pistol with no left-hand bone: its cup is only estimated, and the glove settles a few centimetres onto
                            // it instead of following the real hand freely (GraspReach stays 5 cm for a pistol).
                            anchor = GripRig.ToWorld(GripData.SupportPoint);
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
            // The support hand keeps to the handguard however far the stretch (see the foregrip's let-go); a hand working a
            // part (a bolt, handle, slide or pump being racked, a feed cover or an optic held) stays on it for as far as it
            // holds it, as Boneworks' grip joint keeps the hand on the handle until the hand desyncs; the others settle a few
            // centimetres at most. At 5 cm a hand drifting while racking left the glove off the handle, and a hand shoved into
            // the gun drew it up to 30 cm inside (user, 2026-10-23: "hands glitch through the gun with enough force").
            bool workedPart = anchor.HasValue && (off.Role == HandRole.Slide || off.Role == HandRole.Hinge || off.Role == HandRole.Optic);
            off.Model.GraspReach = off.Role == HandRole.Foregrip && !GripData.IsPistol ? SupportLetGo : workedPart ? HeldPartReach : 0.05f;
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
        /// An empty hand does not pass through the held gun or the hand holding it (Tasks 207): its glove is drawn out of
        /// them the shortest way (away from the bore first). Only the drawing: the tracked hand, and what it can grab, stay
        /// where they are. Its fingers still move freely.
        /// </summary>
        private void KeepEmptyHandOut(VRHand off)
        {
            if (!off.Tracked || off.Held != null || off.Model == null || Rig.GunBody == null
                || (off.Palm.position - _held.GripWorld.position).sqrMagnitude > 0.7f * 0.7f)
            {
                _freeHandClearSet = false; // away from the gun: its last clear place no longer says where it came from
                return;
            }
            if (!PrepareKeepOut())
            {
                return;
            }
            // Its last clear place is only good while the hand stayed empty: kept from before it held something (a magazine,
            // a bolt), it sent the way-out search back toward a place it had long left (review, 2026-10-23).
            if (Time.frameCount - _freeHandFrame > 1)
            {
                _freeHandClearSet = false;
            }
            _freeHandFrame = Time.frameCount;
            // By contact first (Boneworks' physics hand: stopped where it meets the gun, slid along it), from where the glove was
            // drawn last frame; the shortest way out of where the hand is now only when there is no such place.
            if (SweepGlove(off, Vector3.zero))
            {
                if (off.Model.KeepOut.sqrMagnitude < 1e-6f)
                {
                    _freeHandClearGun = Rig.ToGun(off.Palm.position);
                    _freeHandClearSet = true;
                }
                return;
            }
            Vector3 bore = Rig.ToWorld(Data.MuzzlePoint);
            Vector3 away = Vector3.ProjectOnPlane(off.Palm.position - bore, Rig.AimWorld);
            // Where the hand last was clear of the gun, in the gun's own space (it moves with the gun): pushed out that way.
            Vector3 cameFrom = _freeHandClearSet ? Rig.ToWorld(_freeHandClearGun) - off.Palm.position : Vector3.zero;
            off.Model.KeepOut = off.Model.ShortestWayOut(_keepOutBlocked, 0.1f, away, cameFrom);
            off.Model.KeepOutFrame = Time.frameCount;
            if (off.Model.KeepOut.sqrMagnitude < 1e-6f)
            {
                _freeHandClearGun = Rig.ToGun(off.Palm.position);
                _freeHandClearSet = true;
            }
        }

        /// <summary>The free hand's palm where its glove was last clear of the gun (gun space), for the way back out.</summary>
        private Vector3 _freeHandClearGun;
        private bool _freeHandClearSet;
        /// <summary>The last frame the hand was kept out of the gun as an empty hand.</summary>
        private int _freeHandFrame = -10;

        private void KeepOutOfGun(VRHand off, MagazineProp prop)
        {
            if (!PrepareKeepOut())
            {
                return;
            }
            Vector3 insert = SeatPose.TransformDirection(prop.InsertDirLocal);
            float length = Vector3.Distance(prop.BaseLocal, prop.TipLocal);
            float back = off.Model.ClearanceAlong(insert, _keepOutBlocked, Mathf.Min(0.12f, length));
            off.Model.KeepOut = -insert * back;
            off.Model.KeepOutFrame = Time.frameCount;
            _seatKeepOutFrame = Time.frameCount;
        }

        /// <summary>What the glove is kept out of: the held gun's solid and the gun hand's skin. False when there is neither.</summary>
        private bool PrepareKeepOut()
        {
            _keepOutGun = _held.HandsSolid;
            _keepOutHand = _sys.Rig.Dominant != null ? _sys.Rig.Dominant.Model : null;
            if (_keepOutGun == null && _keepOutHand == null)
            {
                return false;
            }
            if (_keepOutBlocked == null)
            {
                _keepOutBlocked = p => _keepOutGun != null && _keepOutGun(p) || _keepOutHand != null && _keepOutHand.NearSkin(p, GunVolume.Grow);
            }
            return true;
        }

        /// <summary>The frame the seating magazine's keep-out (back along its line, <see cref="KeepOutOfGun"/>) last set the glove's.</summary>
        private int _seatKeepOutFrame = -10;

        /// <summary>Where the off glove's root was drawn last frame, in the gun's space (it moves with the gun), and in which frame.</summary>
        private Vector3 _offGloveDrawnGun;
        private int _offGloveDrawnFrame = -10;

        // Held at the gun more than this from where the real hand is, with that hand clear of it (it went round or through to the
        // far side), the glove is let go to the hand: Boneworks' stuck hand is put back on the tracked one the same way.
        private const float GloveRelease = 0.15f;
        private static readonly Vector3[] GloveSlideAxes = new Vector3[3];

        /// <summary>
        /// The off glove kept out of the gun by contact (<see cref="RavenfieldVR.Rig.HandModel.SweptKeepOut"/>), aimed at the tracked hand plus
        /// <paramref name="aim"/>, swept from where it was drawn last frame. False when it was not drawn by the gun last frame
        /// or that place cannot be freed nearby: the caller keeps its own keep-out.
        /// </summary>
        private bool SweepGlove(VRHand off, Vector3 aim)
        {
            if (Time.frameCount - _offGloveDrawnFrame != 1 || off.Model == null)
            {
                return false;
            }
            Vector3 up = Rig.UpWorld, aimDir = Rig.AimWorld;
            GloveSlideAxes[0] = aimDir;
            GloveSlideAxes[1] = up;
            GloveSlideAxes[2] = Vector3.Cross(up, aimDir).normalized;
            Vector3? shift = off.Model.SweptKeepOut(_keepOutBlocked, Rig.ToWorld(_offGloveDrawnGun), aim, GloveSlideAxes, GloveRelease);
            if (!shift.HasValue)
            {
                return false;
            }
            off.Model.KeepOut = shift.Value;
            off.Model.KeepOutFrame = Time.frameCount;
            off.Model.KeepOutSweptFrame = Time.frameCount;
            return true;
        }

        /// <summary>
        /// A hand holding a magazine off the well's line is kept out of the gun by contact too, aimed where it holds the magazine
        /// (the magazine keeps itself out; the glove went in on its own: after a double-magazine reload the old magazine, promoted
        /// in the hand still up at the well, eased in from beside it and its glove slid into the gun; user, 2026-10-23). Then the
        /// glove's place this frame is kept for the next sweep. Last in <see cref="BeforeRender"/>: after every keep-out is set.
        /// </summary>
        private void KeepGloveOutAndRemember(VRHand off)
        {
            if (off.Model == null || !off.Tracked || Rig.GunBody == null
                || (off.Palm.position - _held.GripWorld.position).sqrMagnitude > 0.7f * 0.7f)
            {
                _offGloveDrawnFrame = -10;
                return;
            }
            if (off.Role == HandRole.Magazine && off.Held is MagazineProp held && !held.OnRail && _seatKeepOutFrame != Time.frameCount
                && PrepareKeepOut())
            {
                SweepGlove(off, off.Model.KeepOutFrame == Time.frameCount ? off.Model.KeepOut : Vector3.zero);
            }
            off.Model.PlacedFromTracked(out _, out RigidPose placed);
            _offGloveDrawnGun = Rig.ToGun(placed.position + off.Model.GraspOffsetNext()); // where Apply draws it this frame
            _offGloveDrawnFrame = Time.frameCount;
        }

        /// <summary>The point of the handguard (just under the bore, between the grip and the muzzle) nearest to <paramref name="point"/>.</summary>
        internal Vector3 HandguardPoint(Vector3 point)
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
        /// A point of the gun (gun space) where the gun hand alone holds it, without HeldWeapon's two-hand turn: the turn moves the
        /// live gun onto the off palm, so a stretch measured on the live gun never grew (task 206). The grip sits on the gun hand's
        /// frame (<see cref="VRHand.GunFrame"/>), turned as HeldWeapon turns it before the steer.
        /// </summary>
        private Vector3 UnsteeredGunPoint(Vector3 gunPoint)
        {
            WeaponRigData d = GripData;
            RigidPose hand = HeldWeapon.HoldFrame(_sys.Rig.Dominant, d);
            return hand.position + UnsteeredRotation(hand, d) * (gunPoint - d.GripPoint);
        }

        private static Quaternion UnsteeredRotation(RigidPose hand, WeaponRigData d) =>
            hand.rotation * Runtime.RigidPose.Conjugate(d.HasGripRotation ? d.GripRotation : Quaternion.LookRotation(d.AimAxis, d.Up));

        /// <summary>
        /// A world point (the off palm) put on the live gun where it lies on the gun as the gun hand alone holds it (the inverse of
        /// <see cref="UnsteeredGunPoint"/>): measured against the live, steered gun, a support palm slid back toward the charging
        /// handle chased it, the two-hand turn swinging the gun onto the palm, and stayed 8-11 cm off (2026-10-25: the
        /// JOLT-20-S and the PATRIOT GL never took the handle, in every autotest).
        /// </summary>
        private Vector3 PalmOnLiveGun(Vector3 palm)
        {
            WeaponRigData d = GripData;
            RigidPose hand = HeldWeapon.HoldFrame(_sys.Rig.Dominant, d);
            return GripRig.ToWorld(d.GripPoint + Quaternion.Inverse(UnsteeredRotation(hand, d)) * (palm - hand.position));
        }

        /// <summary>The handguard point of the gun as the gun hand alone holds it: <see cref="HandguardPoint"/> without the two-hand turn.</summary>
        private Vector3 UnsteeredHandguardPoint(Vector3 point)
        {
            WeaponRigData d = GripData;
            Vector3 a = UnsteeredGunPoint(d.GripPoint + d.AimAxis * 0.1f - d.Up * 0.012f);
            Vector3 b = UnsteeredGunPoint(d.MuzzlePoint - d.Up * 0.012f);
            Vector3 ab = b - a;
            float t = Mathf.Clamp01(Vector3.Dot(point - a, ab) / Mathf.Max(ab.sqrMagnitude, 1e-6f));
            return a + ab * t;
        }

        /// <summary>
        /// A support grip behind the trigger grip: a shoulder-fired launcher's wooden grip (the RPG-7). The analyzer keeps such a
        /// grip only for a single-shot launcher (WeaponAnalyzer, task 276); elsewhere a support point behind the grip is a pistol's.
        /// </summary>
        private bool SupportBehindGrip => GripData.HasSupportPoint && !GripData.IsPistol
                                          && Vector3.Dot(GripData.SupportPoint - GripData.GripPoint, GripData.AimAxis) < 0.07f;

        /// <summary>
        /// The support hand's point is the gun's own, not a place along the handguard: a pistol's cup, a rear grip, or a magazine's
        /// or a launcher's grip (HeldWeapon.SupportHeldInPlace, task 277). Its stretch and its glove are taken from that point.
        /// </summary>
        private bool SupportAtOwnPoint => GripData.IsPistol || SupportBehindGrip || _held.SupportHeldInPlace();

        /// <summary>The off hand's motion measured again from where it is now (a kick's push, a rack's stroke): after a jump in its pose.</summary>
        private void ReanchorMotion(VRHand off)
        {
            if (_opticHeld)
            {
                _opticGrabGun = GripRig.ToGun(off.Palm.position);
                _opticSwitched = true; // and no switch from this hold any more: the hand was not moving it
            }
            if (_coverHeld)
            {
                // (Back from a pause: the hand moved while it was up opened or shut the cover on the first frame; round-2 scout.)
                _coverGrabAngle = CoverAngle(off.Palm.position);
                _coverGrabOpenness = _held.HingeOpenness;
            }
            _kickPrevAlong = float.NaN;
            _kickPushSince = -1f;
            _kickTicked = false;
            _supportRest = -1f;
            if (_racking)
            {
                _rackBase = Travel > 1e-5f ? Mathf.Clamp01(_held.SlideOffset / Travel) : _rackBase;
                _rackStartGun = HandInGun(off);
                _tugStroke = _rackBase * StrokeLength;
                _lastTick = _tugStroke;
                if (BoltLifts)
                {
                    // (The turn's own start too: measured from before the pause, the handle came up with no hand turning it.)
                    _liftFromGun = Vector3.ProjectOnPlane(_rackStartGun - Data.MuzzlePoint, Data.AimAxis);
                    _liftFrom = _held.BoltTurn;
                }
            }
        }

        /// <summary>Weapon is going away: finish what is in progress so no ammo is lost or duplicated.</summary>
        public void Release(bool keepSeatedHold = false)
        {
            DropAlmostSeated();
            VRHand off = _sys.Rig.Off;
            if (!keepSeatedHold)
            {
                EndSeatedHold(off); // (the gun going away: the hand on its seated magazine is free)
            }
            if (off.Model != null)
            {
                // First: whatever throws below (HeldWeapon catches it), the off glove is not left pulled toward an anchor on a gun
                // that is gone (review, 2026-10-20). Nor left pinching: the grip point stayed the pinch's for the diagnostics.
                off.Model.GraspAnchor = null;
                off.Model.GraspPinch = false;
            }
            _pullingIn = null;
            // The mag-kick trackers reset too: an in-place sub-weapon switch keeps this instance, so a crossing left over from
            // before the switch could knock the next magazine out on the first frame back (reload lifecycle scout, 2026-10-21).
            _kickPrevAlong = float.NaN;
            _kickPushSince = -1f;
            _kickTicked = false;
            // And the glove's: the last drawn place and the last clear place were in the other sub-weapon's space (review).
            _offGloveDrawnFrame = -10;
            _freeHandClearSet = false;
            Casings.ShowChamber(_held, false, false);
            if (_ejecting != null)
            {
                FinishEject();
            }
            MagazineProp palmed = _palmed;
            _palmed = null;
            if (off.Model != null)
            {
                off.Model.Palming = false;
            }
            if (palmed != null && palmed.Go != null)
            {
                // (Not one a scene change already destroyed: dropping it threw and skipped everything below; bug hunt 251.6.)
                palmed.Drop(off.ThrowVelocity(), off.AngularVelocity);
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
            _beltLaidThisLift = false;
            _beltCoverWasOpen = false;
            _beltStateTaken = false;
            if (off.Model != null)
            {
                off.Model.GraspAnchor = null;
                off.Model.GraspPinch = false;
                off.Model.Palming = false; // the palmed magazine was dropped above; a kept one is held alone
            }
            if (off.Held is MagazineProp prop)
            {
                // Switching weapons with a magazine in hand: keep holding it (it can be seated once this weapon is
                // drawn again, stowed, or dropped).
                prop.OnRail = false;
                if (prop.Owner == null && Ours(prop))
                {
                    prop.Owner = Ammo;
                }
            }
            _held.SlideOffset = 0f;
            _returning = false; // (a rack ended here: the switch to a sub weapon gave the gun now in hand the old one's slam)
        }
    }
}
