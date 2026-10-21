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
            else
            {
                Input.ScriptKey only = laser ?? light; // one key: the gun's own script steps its modes
                Input.ScriptKeys.Tap(only.Key, only.Script);
            }
            off.Haptic(0.015f, 200f, 0.25f);
            _held.Sounds.Play(MechSound.SlideForward, off.Palm.position, 0.15f); // a small click of the switch
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
                if (Breaks && Ammo.Open && !_coverHeld && Time.unscaledTime - _openedAt > 0.35f && dom.OwnAngularVelocity.magnitude > 9f)
                {
                    SetBreakOpen(false);
                }
                // A machine gun's feed cover slapped shut with the open hand, as it is closed in a hurry: a quick downward hit
                // on it (it had to be taken and pushed down all the way).
                // (Over the cover's rear half, as the grab; a hit of 1.5 m/s, not any brisk move down; not in the half second
                // after the cover was let go: a hand dropping away from it shut it again; 255.B2-4, B6-4.)
                if (Ammo.HasCover && Ammo.CoverOpen && !_coverHeld && off.Tracked && off.Role == HandRole.Free && !off.GrabHeld
                    && Time.unscaledTime - _coverLetGoAt > 0.5f && CoverZoneDistance(off.Palm.position) < 0.1f
                    && Vector3.Dot(off.BodyRelativeVelocity - dom.BodyRelativeVelocity, -Rig.UpWorld) > 1.5f)
                {
                    Ammo.SetCoverOpen(false);
                    _held.Nudge(-Rig.UpWorld * 0.08f, 8f);
                    _held.Sounds.Play(MechSound.SlideForward, CoverGrabWorld);
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
                return;
            }
            if (_offLostTracking)
            {
                // Back in tracking with the grip still closed: an optic hold measures its slide again from where the hand is now. Its
                // jump back from the lost pose read as a sideways slide and switched the sight with no press (grab-zone hunt,
                // 2026-10-21).
                _offLostTracking = false;
                if (_opticHeld)
                {
                    _opticGrabGun = GripRig.ToGun(off.Palm.position);
                    _opticSwitched = true; // and no switch from this hold any more: the hand was not moving it
                }
            }
            // Grenades: the pin (ThrowableHandler) and loose props (WeaponSystem) take the off hand's grab. A grip closed a
            // moment before the hand reaches the bolt or magazine still takes it (nothing else has claimed the press).
            if (off.GrabPressed)
            {
                _pressPalm = off.Palm.position; // where this squeeze began (the pouch only takes squeezes made at it)
            }
            // Not with the pause menu up: a squeeze there pulled the magazine or a clip out of the paused gun (sound hunt, 2026-10-20).
            if ((off.GrabPressed || off.GrabReaching) && off.Role == HandRole.Free && Data.Action != ActionType.Throwable && !Util.GameContext.GameMenuOpen)
            {
                TryGrab(off);
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
                    if (Manual && Ammo.UsesChamber && !IsPump && !GripData.IsPistol)
                    {
                        // Slid back along the gun onto the charging handle with the grip still closed: the hand takes the handle. It
                        // stayed on the handguard (it lets go only at 60 cm of stretch) and the rack never started, a rack being taken
                        // by a squeeze and this squeeze the handguard's (user, 2026-10-20 night: "i cannot pull back the bolt thingy
                        // again"; rack trace). As a new grab decides: well within the handle's zone, and the handle clearly nearer
                        // than the handguard line (half its distance).
                        Vector3 palmNow = off.Palm.position;
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
            System.Func<MagazineProp, bool> accept = _sys.Zones.InPouch(palm) ? NotEmpty : null;
            MagazineProp caught = _ejecting != null && _ejecting.DistanceTo(palm) < 0.13f
                ? _ejecting
                : MagazineProp.NearestDropped(palm, 0.13f, Data.Key, accept) ?? MagazineProp.NearestDropped(palm, 0.13f, null, accept);
            if (caught != null && caught != _ejecting && caught.Rounds == 0 && _sys.Zones.InPouch(palm))
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
                float d = CoverZoneDistance(palm);
                if (d < radius)
                {
                    // Weighed as the rack is: at 0.9 a charging handle under the cover's edge won where both were in reach.
                    // With the box in and its belt still to lay, the belt is the next step: the cover weighs full, so a belt
                    // end about as near wins (the cover's latch was taken instead; 255.I3-8).
                    bool beltNext = VRConfig.BeltByHand.Value && Ammo.HasCover && Ammo.CoverOpen && Ammo.MagInserted && !Ammo.BeltLaid;
                    Consider(5, d / radius * (beltNext ? 1f : 0.65f), ref choice, ref best);
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
            if (Manual && _sys.Zones.InPouch(palm)
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
                    // The rest is taken again for this hold: the reset above runs only on the frames the hand is not holding, so a
                    // hold taken on the frame after the last one let go kept the old rest.
                    _supportRest = -1f;
                    _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value && !GripData.IsPistol);
                    off.Haptic(0.02f, 120f, 0.35f);
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
            _ejecting.NotFresh();
            Log.InfoOnce("eject:" + Data.Key, $"Magazine ejected from {Data.Key}: {rounds} rounds, {Rig.MagazineParts.Length} part(s) hidden on the gun, pull axis {Data.MagEjectAxis:F2}, rail {_ejecting.RailLength * 100f:0.0} cm.");
            _ejecting.Owner = Ammo;
            if (WellInGrip)
            {
                GripWellRail(_ejecting); // it drops clear of the grip, not let go half way up it (task 270)
            }
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
            if (WellInGrip)
            {
                // Out along the whole grip, as it goes in (task 270): on the short rail it came off with half of it still up
                // the grip, free there, where the keep-out now opens the well only to a magazine within 12 degrees.
                GripWellRail(prop);
            }
            prop.OnRail = true;
            prop.RailDepth = prop.RailLength;
            prop.LastTick = prop.RailDepth;
            // The hand took it wherever it grabbed (the grab zone is large): the magazine follows the hand's pull from
            // here. Taken from the palm's depth, a grip a little up the magazine read as pushed home and seated it again
            // on the same frame: pulling a magazine out by hand did nothing.
            prop.RailHandOffset = HandAlongRail(off, prop, out float lateral0) - prop.RailLength;
            // And wherever across it: the grab zone is large, so the hand is often several centimetres off the well's line.
            // Measured from no offset, a hand 7 cm off knocked it off the rail on the first frame and it jumped to the hand
            // through the receiver (user, 2026-10-16: "it always needs to exit how it entered").
            prop.RailLateral0 = lateral0;
            prop.FromSeat = true;
            prop.MovingIn = false;
            prop.TurnDepth = prop.RailDepth;
            prop.PrevDepth = prop.RailDepth;
            // An en-bloc clip under a bolt shut on a round is not taken back, but it still leaves along its line (check-1017h:
            // the M1's clip was knocked off its rail on the first frame and jumped to the hand through the receiver).
            prop.RailOutOnly = !AcceptsNow(prop);
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

        /// <summary>
        /// Autotest: the simulated off hand takes a feed cover at its latch and lifts it round its hinge, as a hand would, to
        /// 60% of its swing; reports whether it opened and stays open when let go, then shuts it again by hand.
        /// </summary>
        /// <summary>
        /// Autotest: a machine gun's whole reload by hand, through the hand's own grabs: every step is a squeeze where a
        /// player's hand would be, nothing is called directly (user, 2026-10-16: "reloading is sooo buggy and doesnt even
        /// work manually"). The cover opened at its latch, the box pulled out along its line, a fresh one taken from the
        /// pouch and seated along its line, the belt taken at the box's mouth and laid on the tray, the cover pushed shut;
        /// then it must be ready to fire. Each step says what took the squeeze when it went wrong.
        /// </summary>
        internal IEnumerator MgHandReloadSelfCheck(System.Action<string> report)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || Ammo == null || !Ammo.HasCover || Data.Hinge != HingeKind.FeedCover || !Ammo.UsesDetachableMag)
            {
                yield break;
            }
            string busy = OffHandBusy(off) ?? (Ammo.CoverOpen ? "the cover is open" : !Ammo.MagInserted ? "no box in"
                : !Ammo.InfiniteSpare && Ammo.SpareAmmo <= 0 ? "no spare box" : null);
            if (busy != null)
            {
                report($"machine gun reload by hand: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            Quaternion palmDown = Quaternion.LookRotation(Rig.AimWorld, -Rig.UpWorld);
            void PalmTo(Vector3 p)
            {
                VRHand.Scripted[side] = new RigidPose(p, palmDown).Multiply(rawFromPalm);
                VRHand.ScriptedVelocity[side] = Vector3.zero;
            }
            Vector3 CoverLatchAt(float openness) => Rig.ToWorld(HingeRotate(new RigidPose(Data.HingeGrabPoint + Data.Up * 0.02f, Quaternion.identity), openness).position);
            var steps = new List<string>();
            bool ok = false;
            // The cover settled shut first (the check before can leave it swinging down).
            if (_coverHeld)
            {
                EndCover(off);
            }
            for (float until = Time.unscaledTime + 1f; _held.HingeOpenness > 0.01f && Time.unscaledTime < until;)
            {
                yield return null;
            }
            try
            {
                // 1. The cover: the hand at its latch, a squeeze, lifted.
                PalmTo(CoverLatchAt(0f));
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                yield return null;
                yield return null;
                bool tookCover = off.Role == HandRole.Hinge;
                string coverTaker = $"{off.Role}, the palm {CoverZoneDistance(off.Palm.position) * 100f:0.0} cm from the latch, the cover {_held.HingeOpenness:0.00} open";
                for (int i = 1; i <= 15; i++)
                {
                    PalmTo(CoverLatchAt(0.8f * i / 15f));
                    yield return null;
                }
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.4f);
                bool opened = Ammo.CoverOpen;
                steps.Add(tookCover ? opened ? "cover opened at its latch" : "cover taken but DID NOT OPEN" : $"COVER NOT TAKEN at its latch (the squeeze: {coverTaker})");
                if (!opened)
                {
                    yield break;
                }

                // 2. The box out: a squeeze on it, pulled out along its line, let go away from the gun.
                Vector3 outW = Rig.DirToWorld(Data.MagEjectAxis).normalized;
                Vector3 boxAt = SeatedMagBaseWorld;
                PalmTo(boxAt);
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                yield return null;
                yield return null;
                bool tookBox = off.Held is MagazineProp;
                HandRole boxTaker = off.Role;
                for (int i = 1; i <= 15; i++)
                {
                    PalmTo(boxAt + outW * (0.35f * i / 15f));
                    yield return null;
                }
                bool boxOut = !Ammo.MagInserted;
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.3f);
                steps.Add(tookBox ? boxOut ? "box pulled out" : "box taken but NOT OUT of the gun" : $"BOX NOT TAKEN (the squeeze: {boxTaker})");
                if (Ammo.MagInserted)
                {
                    yield break;
                }

                // 3. A fresh box: a squeeze in the pouch.
                PalmTo(_sys.Zones.Pouch);
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                for (int i = 0; i < 6 && off.Held == null; i++)
                {
                    yield return null;
                }
                MagazineProp box = off.Held as MagazineProp;
                steps.Add(box != null && box.Rounds > 0 ? $"fresh box from the pouch ({box.Rounds} rounds)" : $"NO FRESH BOX from the pouch (the squeeze: {off.Role})");
                if (box == null || box.Rounds <= 0)
                {
                    yield break;
                }

                // 4. Seated along its line, pushed in as a hand does.
                RigidPose holdInRaw = off.Raw.Inverse().Multiply(box.HoldPose(off));
                float length = Vector3.Dot(box.TipLocal - box.BaseLocal, box.InsertDirLocal);
                for (float below = 0.06f; below > -length - 0.03f && ReferenceEquals(off.Held, box) && !Ammo.MagInserted;
                     below -= 0.3f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
                {
                    PlaceFreshMag(off, box, holdInRaw, below, 0f, 0.3f);
                    yield return null;
                }
                for (int wait = 0; wait < 30 && !Ammo.MagInserted; wait++)
                {
                    yield return null;
                }
                bool seated = Ammo.MagInserted;
                VRInput.SimulatedGrab[side] = false;
                VRHand.ScriptedVelocity[side] = Vector3.zero;
                yield return null;
                steps.Add(seated ? "fresh box seated along its line" : "FRESH BOX NOT SEATED");
                if (!seated)
                {
                    yield break;
                }

                // 5. The belt: taken at the box's mouth, brought onto the tray.
                if (VRConfig.BeltByHand.Value && !Ammo.BeltLaid)
                {
                    Vector3 beltAt = BeltStartWorld;
                    PalmTo(beltAt);
                    yield return null;
                    yield return null;
                    VRInput.SimulateGrabPress(off.Side);
                    yield return null;
                    yield return null;
                    bool tookBelt = off.Role == HandRole.Belt;
                    HandRole beltTaker = off.Role;
                    for (int i = 1; i <= 20 && !Ammo.BeltLaid; i++)
                    {
                        PalmTo(Vector3.Lerp(beltAt, FeedTrayWorld, i / 20f));
                        yield return null;
                    }
                    bool laid = Ammo.BeltLaid;
                    VRInput.SimulatedGrab[side] = false;
                    yield return null;
                    steps.Add(tookBelt ? laid ? "belt taken at the box and laid on the tray" : "belt taken but NOT LAID on the tray" : $"BELT NOT TAKEN at the box (the squeeze: {beltTaker})");
                    if (!laid)
                    {
                        yield break;
                    }
                }

                // 6. The cover pushed shut.
                PalmTo(CoverLatchAt(_held.HingeOpenness));
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                yield return null;
                yield return null;
                bool tookShut = off.Role == HandRole.Hinge;
                HandRole shutTaker = off.Role;
                float from = _held.HingeOpenness;
                for (int i = 1; i <= 15; i++)
                {
                    PalmTo(CoverLatchAt(Mathf.Lerp(from, 0f, i / 15f)));
                    yield return null;
                }
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.3f);
                bool shut = !Ammo.CoverOpen;
                steps.Add(tookShut ? shut ? "cover pushed shut" : "cover taken but NOT SHUT" : $"COVER NOT TAKEN to shut it (the squeeze: {shutTaker})");
                ok = shut;
            }
            finally
            {
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
                VRHand.ScriptedVelocity[side] = Vector3.zero;
                if (off.Held is MagazineProp leftover)
                {
                    Ammo.ReturnSpare(leftover.Rounds);
                    leftover.Rounds = 0;
                    leftover.Destroy();
                }
                report($"machine gun reload by hand: {string.Join(", ", steps)}{(ok ? Ammo.FeedReady ? "; ready to fire" : "; NOT READY TO FIRE" : "")}");
                // Left as a player would leave it: box in, belt laid, cover shut (a failed step is put right here, also after
                // a step that gave up, for the checks after this one).
                if (!Ammo.MagInserted)
                {
                    if (!Ammo.CoverOpen)
                    {
                        Ammo.SetCoverOpen(true);
                    }
                    Ammo.InsertMagazine(Ammo.RoundsForFreshMagazine());
                }
                if (Ammo.CoverOpen && !Ammo.BeltLaid)
                {
                    Ammo.LayBelt();
                }
                if (Ammo.CoverOpen)
                {
                    Ammo.SetCoverOpen(false);
                }
            }
            yield return new WaitForSecondsRealtime(0.2f);
        }

        internal IEnumerator CoverSelfCheck(System.Action<string> report)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || Data.Hinge != HingeKind.FeedCover)
            {
                yield break;
            }
            string busy = OffHandBusy(off) ?? (Ammo.CoverOpen ? "the cover is already open" : null);
            if (busy != null)
            {
                report($"feed cover by hand: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            Quaternion palmDown = Quaternion.LookRotation(Rig.AimWorld, -Rig.UpWorld);
            RigidPose At(float openness) => new RigidPose(
                Rig.ToWorld(HingeRotate(new RigidPose(Data.HingeGrabPoint + Data.Up * 0.02f, Quaternion.identity), openness).position), palmDown).Multiply(rawFromPalm);
            bool opened = false, stayed = false, shut = false;
            try
            {
                // The hand onto the cover first, then the grip closes there (closed before it got there, the grab was taken
                // wherever the hand was and the cover "could not be taken").
                VRHand.Scripted[side] = At(0f);
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side); // a press: a grip only held was never taken (the cover check read NOT TAKEN)
                yield return null;
                yield return null; // the grab is taken (the press)
                bool took = off.Role == HandRole.Hinge;
                for (int i = 1; i <= 15; i++)
                {
                    VRHand.Scripted[side] = At(0.6f * i / 15f);
                    yield return null;
                }
                opened = Ammo.CoverOpen;
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.4f);
                stayed = Ammo.CoverOpen && _held.HingeOpenness > 0.9f;
                VRHand.Scripted[side] = At(_held.HingeOpenness);
                yield return null;
                VRInput.SimulateGrabPress(off.Side); // a press: a grip only held was never taken (the cover check read NOT TAKEN)
                yield return null;
                yield return null;
                for (int i = 1; i <= 15; i++)
                {
                    VRHand.Scripted[side] = At(Mathf.Lerp(1f, 0f, i / 15f));
                    yield return null;
                }
                shut = !Ammo.CoverOpen;
                // Let go where it is before the hand is handed back (see the tracking note at EndCover's caller).
                VRInput.SimulatedGrab[side] = false;
                yield return null;
                yield return null;
                report($"feed cover by hand: {(took ? "taken at its latch" : "NOT TAKEN at its latch")}, lifted to 60%: {(opened ? "open" : "DID NOT OPEN")}, let go: {(stayed ? "falls open" : "DID NOT STAY OPEN")}, pushed down: {(shut ? "shut" : "DID NOT SHUT")}");
            }
            finally
            {
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            yield return new WaitForSecondsRealtime(0.3f);
            if (Ammo.CoverOpen)
            {
                Ammo.SetCoverOpen(false); // left as it was found
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
                // A magnifier the free hand flips by its own grip (MagnifierFlip) has one switch: that grip. This optic grab
                // would also flip it on a quick tap at release, which the magnifier grip must never do.
                if (w == null || _held.MagnifierByHand(w))
                {
                    return false;
                }
                return w.HasAnyAltSightModes() || Input.ScriptKeys.Find(w, Input.ScriptKeyPurpose.Magnifier) != null;
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
        /// <summary>The belt was laid by hand while the cover was open: it shows until the cover is lifted again (274).</summary>
        private bool _beltLaidThisLift;
        /// <summary>The cover was open at the last look, so a lift is seen as the moment it goes up.</summary>
        private bool _beltCoverWasOpen;
        /// <summary>How far the belt end comes out of the box (about 28 rounds).</summary>
        private const float BeltReach = 0.32f;

        /// <summary>Where the end of a belt hanging from the box mouth is.</summary>
        private Vector3 BeltHangEnd => BeltStartWorld + Vector3.down * (BeltReach * 0.65f) + Rig.AimWorld * 0.03f;

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
                Quaternion seatInv = Quaternion.Inverse(Data.MagSeat.rotation);
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
                Vector3 mouth = b.center;
                for (int axis = 0; axis < 3; axis++)
                {
                    mouth[axis] = axis == lead
                        ? b.center[axis] + Mathf.Sign(insertLocal[axis]) * b.extents[axis]
                        : Mathf.Clamp(tray[axis], b.center[axis] - b.extents[axis], b.center[axis] + b.extents[axis]);
                }
                return SeatPose.TransformPoint(mouth);
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
                EndBelt(off);
                _beltHanging = true;
                _belt?.Show(true);
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
                _beltLaidThisLift = true;
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
            // A belt still held goes with its box: out, the cover shut or the belt laid, it is let go here whatever the hand's role
            // says. Only UpdateBelt let it go, and that runs only while the hand's role is Belt, so a belt held when the box came
            // out (the role since reset, or the hand not tracked) stayed drawn after its box (274: "the old belt is still there").
            if (_beltHeld && (!Ammo.MagInserted || !Ammo.HasCover || !Ammo.CoverOpen || Ammo.BeltLaid))
            {
                Log.InfoOnce("beltheld-dropped:" + Data.Key, $"{Data.Key}: the belt held by the hand was let go with its box (box in {Ammo.MagInserted}, cover open {Ammo.CoverOpen}, belt laid {Ammo.BeltLaid}).");
                EndBelt(off);
            }
            // Lifting the cover takes a belt laid on the tray off it with its box: the old belt stayed when the cover was opened
            // (274, the user). A belt laid by hand with the cover open is shown until the cover is lifted again.
            if (Ammo.CoverOpen && !_beltCoverWasOpen)
            {
                _beltLaidThisLift = false;
            }
            _beltCoverWasOpen = Ammo.CoverOpen;
            if (!Ammo.MagInserted || !Ammo.HasCover)
            {
                _beltHanging = false;
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
                if (!_beltHeld && VRConfig.BeltByHand.Value)
                {
                    // A fresh box under an open cover: its belt hangs from it, to be taken and laid. Drawn only after a slip,
                    // the belt was nowhere to be seen and the hand reload stopped at LAY BELT (255.B6-1).
                    _belt = _belt ?? new BeltStrip();
                    _beltHanging = true;
                }
            }
            // A laid strip shows under the shut cover, and with the cover open only if it was laid in this lift; the box going
            // clears it, so a box put back is never drawn with the old belt (274). Read from the state, so a belt laid at the
            // spawn, or still laid when the gun is drawn again, shows too; for hand-laid belts only (without BeltByHand the belt
            // was never shown on the tray), and a gun whose model has its own belt shows that instead.
            bool laidOnTray = Ammo.BeltLaid && Ammo.MagInserted && Ammo.HasCover && VRConfig.BeltByHand.Value && _held.BeltPartCount == 0
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
                    : Mathf.Min(Ammo.TubeRoom, Ammo.InfiniteSpare ? int.MaxValue : Ammo.SpareAmmo);
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
                    // An en-bloc clip goes in only through the open action (locked back, or nothing in the chamber).
                    return Ammo.UsesDetachableMag && !Ammo.MagInserted && (!Data.EnBloc || Ammo.SlideLocked || !Ammo.Chambered);
                case PropKind.Rocket:
                    return Ammo.MagRounds == 0;
                default:
                    return Ammo.TubeRoom > 0 && (!Breaks || Ammo.Open);
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
            if (prop.LostByHand)
            {
                // The hand went 45 cm on past the gun it could not pass through: it let go, the magazine falls where it was.
                prop.LostByHand = false;
                prop.Drop(Vector3.zero, Vector3.zero); // out of the hand (Drop frees it)
                off.Haptic(0.03f, 90f, 0.4f);
                return;
            }
            if (TryMagKick(off, prop))
            {
                return;
            }
            bool refused = !AcceptsNow(prop);
            if (refused && _pullingIn == prop)
            {
                _pullingIn = null;
            }
            if (refused && !(prop.OnRail && prop.RailOutOnly))
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
                    off.Haptic(0.04f, 70f, 0.55f); // felt only: no knock sound against the gun (user, 2026-10-16)
                }
                return;
            }

            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(prop.InsertDirLocal);
            RigidPose hold = prop.HoldPose(off);
            if (!prop.OnRail && prop.Kind == PropKind.Magazine && WellInGrip)
            {
                GripWellRail(prop);
            }
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
                // Taken loosely only at the well's mouth, where a magazine goes in (its tip within 2 cm of it). Higher up, off
                // the line, the magazine is beside the gun: taken there, it slid sideways onto its line through the grip's (or
                // the receiver's) wall in the 0.08 s switch, and sat in the well (user, 2026-10-19, headset: "the mag goes
                // through the handle", the 1911s most; task 270). Deeper than the mouth only on the line already (within the
                // well's play: the keep-out lets it no further) and nearly straight.
                // Judged where the magazine is drawn, not where the hand is: held back at the mouth by the gun with the hand
                // pushing on (a fast push passes the mouth's 2 cm in a frame), it is still at the mouth.
                bool magazine = prop.Kind == PropKind.Magazine;
                float drawnLateral = lateral;
                float drawnAlong = magazine && prop.Go != null ? PoseAlongRail(prop, RigidPose.Of(prop.Go.transform), out drawnLateral) : along;
                bool atMouth = !magazine || drawnAlong < MouthReach + 0.005f;
                float lateralMax = rocket ? 0.06f : !atMouth ? MagazineProp.WellPlay * 0.75f : grip ? 0.045f : 0.035f;
                float angleMax = !atMouth ? MagazineProp.InWellTurn : grip || rocket ? 40f : 35f;
                bool lined = (magazine ? drawnLateral : lateral) < lateralMax && along > (rocket ? -0.03f : -0.005f) && (rocket ? along < RocketTakenIn : (magazine ? drawnAlong : along) < prop.RailLength)
                             && angle < angleMax;
                if (rocket && !lined && LooselyLined(prop, hold, seat, insert, out float looseDepth))
                {
                    // A rocket held like a stick (along the fist, either end first) at the launcher's mouth: onto the line
                    // as it is, and from there it follows the hand along the tube. It had to be held with its tail out of
                    // the top of the fist within 40 degrees of the tube, and scoped RPGs "could not be reloaded" (Tasks 231).
                    // Where the hand has it: the line reaches out in front of the mouth. Taken onto the mouth itself, a rocket
                    // taken 10 cm out jumped 10 cm and stayed ahead of the hand, loaded at 80% of the push (rocket film).
                    lined = true;
                    prop.RailHandOffset = 0f;
                    prop.RailLateral0 = lateral;
                }
                if (lined)
                {
                    prop.OnRail = true;
                    prop.RailDepth = Mathf.Clamp(along, rocket ? -RocketLineAhead : 0f, prop.RailLength);
                    if (magazine)
                    {
                        // On at the depth where it is drawn, the hand's way in counted from there: held back at the mouth it starts
                        // at the mouth; taken off the line there, at the mouth itself, so it moves onto the line under the well and
                        // not through its lowest centimetre (task 270).
                        prop.RailDepth = drawnLateral > MagazineProp.WellPlay * 0.75f ? 0f : Mathf.Clamp(drawnAlong, 0f, prop.RailLength);
                        prop.RailHandOffset = along - prop.RailDepth;
                    }
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
            // A magazine still in its well is held by the well the same way: it slides out along it, never sideways out of it.
            bool inWell = prop.Kind == PropKind.Magazine && prop.RailDepth > 0.01f;
            float offLine = prop.Kind == PropKind.Rocket ? -RocketLineAhead - 0.03f : -0.03f;
            if (along < offLine || lateral > (inTube ? 0.35f : inWell ? 0.2f : 0.07f) + prop.RailLateral0)
            {
                prop.OnRail = false;
                prop.RailOutOnly = false;
                prop.RailHandOffset = 0f;
                prop.RailLateral0 = 0f;
                prop.FromSeat = false;
                return;
            }
            prop.PrevDepth = prop.RailDepth;
            prop.RailDepth = Mathf.Clamp(along, prop.Kind == PropKind.Rocket ? -RocketLineAhead : 0f, prop.RailLength);
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
            if (refused)
            {
                // Out only: pushed back it stops short of its seat (the gun will not take it now).
                prop.RailDepth = Mathf.Min(prop.RailDepth, prop.RailLength * 0.9f);
                return;
            }
            prop.RailOutOnly = false;
            // (98.5%, not 96: the prop gave way 3.6-5 mm short of the seat and the gun's magazine popped up there; review, 2026-10-20.)
            if (prop.RailDepth >= prop.RailLength * 0.985f)
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

        /// <summary>How far in front of the mouth a rocket's line reaches (where it is taken onto it, loosely held).</summary>
        private const float RocketLineAhead = 0.12f;

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
        private float HandAlongRail(VRHand off, MagazineProp prop, out float lateral) => PoseAlongRail(prop, prop.HoldPose(off), out lateral);

        /// <summary>How deep the gun's well in its grip is from the side (its seated magazine inside the gun), -1 until measured.</summary>
        private float _gripWellDepth = -1f;

        /// <summary>
        /// A magazine going into a well in the grip runs on its rail from the grip's bottom: its rail was at most 9 cm, and on
        /// the 1911s (12.6 cm in the grip seated) it began 3.6 cm up inside the grip, where the magazine was still free, turned
        /// up to 40 degrees and 4 mm off its line, and showed through the grip's walls on the way in (user, 2026-10-19: "the
        /// mag goes through the handle", the 1911s most; task 270). Measured once per hold, from the side, as the autotest does.
        /// </summary>
        private void GripWellRail(MagazineProp prop)
        {
            float length = Vector3.Dot(prop.TipLocal - prop.BaseLocal, prop.InsertDirLocal);
            if (_gripWellDepth < 0f && _held.HandsSolid != null)
            {
                RigidPose seated = SeatPose;
                float depth = 0f;
                for (float s = 0f; s <= length; s += 0.002f)
                {
                    if (!_held.InsideGunFromSide(seated.TransformPoint(prop.TipLocal - prop.InsertDirLocal * s)))
                    {
                        break;
                    }
                    depth = s;
                }
                if (depth > 0.01f) // else the gun's side view is not ready yet: measured again next frame
                {
                    _gripWellDepth = depth;
                    Log.InfoOnce("gripwell:" + Data.Key, $"{Data.Key}: the well in the grip is {depth * 100f:0.0} cm deep from the side; a magazine's rail starts at its bottom ({prop.RailLength * 100f:0.0} cm before).");
                }
            }
            if (_gripWellDepth > prop.RailLength)
            {
                prop.RailFromGripBottom(Mathf.Min(_gripWellDepth + 0.005f, length));
            }
        }

        /// <summary>How far along its rail a prop at <paramref name="pose"/> is (0: its tip at the entry), and how far off it.</summary>
        private float PoseAlongRail(MagazineProp prop, RigidPose pose, out float lateral)
        {
            RigidPose seat = SeatPose;
            Vector3 insert = seat.TransformDirection(prop.InsertDirLocal);
            Vector3 entryTip = seat.TransformPoint(prop.TipLocal) - insert * prop.RailLength;
            Vector3 rel = pose.TransformPoint(prop.TipLocal) - entryTip;
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

        /// <summary>
        /// How far into its well (from the entry: its tip at the mouth) a magazine off its line can still be taken onto it. Past
        /// that it is beside the gun, not under its well (task 270).
        /// </summary>
        internal const float MouthReach = 0.015f;

        /// <summary>How far the support hand may be pulled from the handguard with the grip closed before it lets go.</summary>
        private const float SupportLetGo = 0.6f;
        private float _nextStretchBuzz;
        /// <summary>
        /// The support hand's distance from its own point (see <see cref="SupportAtOwnPoint"/>) where the hold took it: the stretch
        /// counts from there, so a palm a few centimetres off the point on the grab is not a stretch. -1 until the hold is measured.
        /// </summary>
        private float _supportRest = -1f;

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
            // Against the body: both hands share the rig's motion, but taken at each hand a turn moved them apart by its spin
            // times their gap (0.2-0.4 m/s at a smooth turn), around these thresholds (user, 2026-10-20: the mag kick worse).
            float speed = Vector3.Dot(off.BodyThrowVelocity() - _sys.Rig.Dominant.BodyThrowVelocity(), insert);
            // Sized by the magazine: a strike lands anywhere on the old base plate (as wide as the magazine) with any part
            // of the fresh one's top; a slow push has to be on the base itself.
            float strikeReach = Mathf.Clamp(prop.CrossHalf * 2f + 0.025f, 0.06f, 0.13f);
            float pushReach = Mathf.Clamp(prop.CrossHalf + 0.025f, 0.035f, 0.08f);
            float below = Mathf.Clamp(length * 0.25f, 0.04f, 0.08f);
            float into = Mathf.Clamp(length * 0.45f, 0.06f, 0.12f);
            bool near = along > -0.12f && along < into + 0.04f && lateral < strikeReach + 0.04f;
            if (near && Data.MagKickable && Time.unscaledTime - _kickLogAt > 0.3f)
            {
                // Numbers for tuning in the headset: which condition holds a kick back (debug logging only: three lines a
                // second flooded the log while a magazine was near the well; 255.P2-5).
                _kickLogAt = Time.unscaledTime;
                Log.Debug($"Mag kick test {Data.Key}: along {along * 100f:0.0} cm, lateral {lateral * 100f:0.0} cm (reach {strikeReach * 100f:0.0}), angle {angle:0} deg, speed {speed:0.00} m/s, kickable {Data.MagKickable}, rounds {prop.Rounds}");
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
            // Crossing at 8 cm/s; compared a little under it (a push at exactly 0.08 m/s was a coin flip: 252 ae).
            bool strike = inWindow && speed >= 0.2f || crossed && speed >= 0.075f;
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
        /// of the inside of the gun or through the fresh one coming up), and flies clear forward, out in front of the gun
        /// with the strike's push and a little sideways away from the gun hand's side, turning end over end with its top
        /// leading. It used to drop straight down, spinning about the barrel (user, 2026-10-18: "drops straight down instead
        /// of flipping out forward", task 260 a). (It was made at its seat, slid straight out along the well through the fresh
        /// one, and dropped from inside the gun.) The release button still drops it straight out.
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
            Vector3 push = Vector3.ClampMagnitude(off.BodyThrowVelocity() - _sys.Rig.Dominant.BodyThrowVelocity(), 2.5f);
            // Forward along the barrel with the strike and the gun hand's motion; out of the well, a little away from the gun
            // hand and only a touch down. (Sideways and down alone, it fell straight down.)
            Vector3 velocity = _sys.Rig.Dominant.Velocity + push * 0.5f + aim * Random.Range(1.4f, 1.9f) + side * 0.6f - insert * 0.6f + Vector3.down * 0.2f;
            // End over end with the top (the insertion end) leading forward: the spin is about the axis across the gun, insert
            // x aim, which carries the top along the barrel. A spin about the barrel itself only rolled it.
            Vector3 tumble = Vector3.Cross(insert, aim).normalized * Random.Range(6f, 9f) + Random.insideUnitSphere * 1.5f;
            old.Drop(velocity, tumble);
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
                    if (Data.EnBloc && prop.Rounds > 0 && (Ammo.SlideLocked || !Ammo.Chambered))
                    {
                        // The clip pressed home releases the bolt: it slams forward on its own and chambers the first round.
                        ReleaseSlide();
                        _held.Nudge(Rig.AimWorld * 0.12f, 12f);
                        off.Haptic(0.06f, 100f, 0.9f);
                    }
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
            _kickPrevAlong = float.NaN; // the next magazine's kick starts afresh (a stale crossing fired a kick; review, 2026-10-20)
            _kickPushSince = -1f;
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
        private Vector3 RackPointWorld => Rig.ToWorld(RackGun + StrokeDir * (StrokeLength * ((_held.BoltLockedAlone || _held.KnobLockedAlone || _held.HandleLockedAlone) && !_racking ? 0f : Fraction)));

        /// <summary>The rack point (gun space); for the held weapon's own rig it sits on the drawn model's surface.</summary>
        private Vector3 RackGun => ReferenceEquals(Rig, _held.Rig) ? _held.RackPointGun : Data.RackPoint;

        /// <summary>
        /// Distance to the part you rack: a slide can be taken anywhere along its rear half, a bolt or handle around
        /// its knob.
        /// </summary>
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
            _rackedBack = Ammo.SlideLocked || Ammo.BoltOpen && !Ammo.SpentInChamber && (Fraction >= 0.8f || !Ammo.Chambered);
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

        // ---- per-frame visuals -----------------------------------------------------------------------

        /// <summary>
        /// The slide's and the hinge's offsets for this frame (locked open, a bolt left open, the spring slamming it home, a
        /// hinge settling). Run before the drawn parts are posed: written after them, each change showed a frame late
        /// (255.B7-2).
        /// </summary>
        internal void StepParts()
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
        }

        public void BeforeRender()
        {
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
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
                    // It stays out of the gun on its own, easing out of the well too (it went through the gun meanwhile;
                    // 255.I1-2); the glove follows it only once it is in the hand and the fresh one is not on its rail.
                    palmedPose = _palmed.KeptOffGun(palmedPose, moveHand: into >= 1f && !(off.Held is MagazineProp onRail && onRail.OnRail));
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
                _freeHandClearSet = false; // away from the gun: its last clear place no longer says where it came from
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
            RigidPose hand = _sys.Rig.Dominant.GunFrame;
            Quaternion gunRot = hand.rotation * Quaternion.Inverse(d.HasGripRotation ? d.GripRotation : Quaternion.LookRotation(d.AimAxis, d.Up));
            return hand.position + gunRot * (gunPoint - d.GripPoint);
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
                // Not fired in the film (a lock-on launcher with nothing to lock on): unloaded, as if fired, rather than
                // skipped (HYDRA, SCALPEL and AT-1K were never loaded by hand).
                Ammo.ReturnSpare(Ammo.MagRounds);
                Ammo.MagRounds = 0;
                yield return null;
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
            Vector3 seatedTail = seatedPose.TransformPoint(rocket.TipLocal); // inside the tube, behind its mouth: where the probe crosses it
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
                RigidPose above = palmHolding(seatedTail + upW * 0.35f, rightW, aimW);
                yield return Move(s => new RigidPose(Vector3.Lerp(start.position, above.position, s), Quaternion.Slerp(start.rotation, above.rotation, s)), 0.5f, "across the tube");
                watchSide = true;
                yield return Move(s => palmHolding(seatedTail + upW * (0.35f * (1f - s)), rightW, aimW), 0.6f, "down across the tube");
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
            LogCopyPieces(prop);
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
                                // Compared with the seated magazine at the same place in the gun (the point slid down the line by
                                // the part still out), not the same point of the magazine seated, which sits up in the frame: the
                                // grip's bottom (a cutout, a flared well) showed more of any magazine there, and a clean line was
                                // flagged as going in beside the well on the 1911s and the Glock (task 270; offline, the line was clean).
                                insideSeated += _held.InsideGunFromSide(seated.TransformPoint(local - prop.InsertDirLocal * (prop.RailLength - prop.RailDepth))) ? 1 : 0;
                            }
                        }
                    }
                    float outside = total > 0 ? 1f - inside / (float)total : 0f;
                    float outsideSeated = total > 0 ? 1f - insideSeated / (float)total : 0f;
                    Core.AutoTest.Metric("magazine (deep): share of what has gone in that shows beside the gun's sides", outside * 100f);
                    sb.Append($", {inDepth * 100f:0.0} cm in past the well's mouth ({inSeated * 100f:0.0} cm seated): {outside * 100f:0}% of it shows beside the gun's sides "
                              + $"(the seated magazine there: {outsideSeated * 100f:0}%){(inDepth > 0.025f && outside > 0.25f && outside > outsideSeated + 0.15f ? " (GOES IN BESIDE THE WELL)" : "")}");
                }
                else
                {
                    sb.Append($", not past the well's mouth yet ({inSeated * 100f:0.0} cm of it goes in)");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// Once per weapon: each piece the magazine in the hand is drawn with, its size and how far its middle sits to the side
        /// of the bore's plane when seated, against the gun's own magazine pieces (the 1911's copy showed over the outside of
        /// its grip panel while the gun's own magazine did not; its analysed box was 7 x 6 cm across).
        /// </summary>
        private void LogCopyPieces(MagazineProp prop)
        {
            if (prop.Go == null || !_copyLogged.Add(Data.Key))
            {
                return;
            }
            Vector3 aimW = Rig.AimWorld, rightW = Vector3.Cross(Rig.UpWorld, aimW).normalized;
            Vector3 bore = Rig.ToWorld(Data.MuzzlePoint);
            var sb = new System.Text.StringBuilder($"Magazine pieces of {Data.Key} (sideways from the bore's plane, cm):");
            foreach (Renderer r in prop.Go.GetComponentsInChildren<Renderer>(false))
            {
                Bounds b = r.bounds;
                sb.Append($" copy '{r.name}' {Vector3.Dot(b.center - bore, rightW) * 100f:0.0} ({b.size.magnitude * 100f:0} cm);");
            }
            foreach (Transform part in Rig.MagazineParts)
            {
                foreach (Renderer r in part != null ? part.GetComponentsInChildren<Renderer>(true) : new Renderer[0])
                {
                    Bounds b = r.bounds;
                    sb.Append($" gun '{r.name}' {(r.enabled && r.gameObject.activeInHierarchy ? "" : "hidden ")}{Vector3.Dot(b.center - bore, rightW) * 100f:0.0} ({b.size.magnitude * 100f:0} cm);");
                }
            }
            Log.Info(sb.ToString());
        }

        private readonly HashSet<string> _copyLogged = new HashSet<string>();

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
                // Still in the hand (the real grip held it): back into the pouch. One let go unseated has fallen, as in play.
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
                // "capacity minus loaded" cases once, out of nothing). Shut first: left open (break actions stay open),
                // opening again did nothing and the check read "0 cases" as a pass (255.I6-8).
                SetBreakOpen(false);
                yield return new UnityEngine.WaitForSecondsRealtime(0.1f);
                bool fired = Ammo.MagRounds > 0;
                if (fired)
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
                report($"break-open: opened after a shot, {first} case(s) fell out{(fired && first == 0 ? " (NO CASE FELL OUT OF THE FIRED ONE)" : fired ? "" : " (nothing loaded to fire)")}; opened again, {again}{(again > 0 ? " (NOT ONLY ONCE: cases out of nothing)" : "")}");
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
                if (Data.EnBloc)
                {
                    // An en-bloc rifle takes a clip only through the open action: as after its last round, the bolt locked
                    // back over an empty chamber (with a round chambered and the bolt shut the clip cannot go in).
                    Ammo.Chambered = false;
                    Ammo.SlideLocked = true;
                    Ammo.Sync();
                }
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
                            // (An en-bloc clip pressed home sends the bolt forward, which chambers its first round: counted.)
                            int kept = Ammo.MagRounds + (Data.EnBloc && Ammo.Chambered ? 1 : 0);
                            report(kept == before ? $"dropped magazine picked up and seated, {before} rounds kept" : $"PICKED-UP MAGAZINE LOST ROUNDS ({kept}/{before})");
                        }
                    }
                    if (Ammo.MagInserted)
                    {
                        EjectMagazine();
                        yield return new UnityEngine.WaitForSecondsRealtime(0.2f);
                    }
                }
                if (Data.EnBloc)
                {
                    // Open again for the pouch's clip (the seated one sent the bolt home on a round, and a clip cannot go in
                    // then: "NOT SEATED, 0 rounds").
                    Ammo.Chambered = false;
                    Ammo.SlideLocked = true;
                    Ammo.Sync();
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
                    if (Data.EnBloc)
                    {
                        // The clip seated sent the bolt home on a round: open again for it to go back in.
                        Ammo.Chambered = false;
                        Ammo.SlideLocked = true;
                        Ammo.Sync();
                    }
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
                        report($"belt laid: {(_held.BeltPartCount > 0 ? $"the model's own belt ({_held.BeltPartCount} part(s)) shows" : Ammo.BeltLaid ? "the strip stays on the feed tray" : "NOTHING SHOWS on the tray")}");
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
                // Drawn at a size that shows: the part's own scale said nothing on a model scaled up from centimetres (a
                // magazine shrunk to 1/10000 still had a scale of 1/100 there, and the m4 pack's M4A1s read "STILL SHOWS"
                // with nothing in the well in the picture).
                if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer) && r.transform.lossyScale.sqrMagnitude >= 1e-6f
                    && r.bounds.size.sqrMagnitude >= 0.002f * 0.002f)
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
            if (Manual && Ammo.BoxLocked && Ammo.HasCover)
            {
                // A machine gun's box is locked under its shut cover: opened first, as a player does, so the box is taken out,
                // held and seated in the pictures too (none of the belt-fed guns had one; Tasks 252 n).
                Ammo.SetCoverOpen(true);
                _held.HingeOpenness = 1f;
                yield return new WaitForSecondsRealtime(0.2f);
                report("magazine (deep): feed cover opened first (the box was locked under it)");
            }
            if (!Manual || !Ammo.UsesDetachableMag || Ammo.BoxLocked || off.Held != null || off.Role != HandRole.Free || Rig.GunBody == null)
            {
                report("magazine (deep): skipped (no detachable magazine, or the hand is busy)");
                yield break;
            }
            if (Data.EnBloc && !(Ammo.SlideLocked || !Ammo.Chambered))
            {
                // An en-bloc clip goes in only through the open action: as after its last round, the bolt locked back over an
                // empty chamber (with the bolt shut on a round the fresh clip was refused: "NOT ON THE RAIL", "NOT SEATED").
                Ammo.Chambered = false;
                Ammo.SlideLocked = true;
                Ammo.Sync();
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
            if (Data.EnBloc)
            {
                // The fresh clip goes in through the open action (seated, the first one sent the bolt home on a round).
                Ammo.Chambered = false;
                Ammo.SlideLocked = true;
                Ammo.Sync();
            }
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
            string skinnedLeft = _held.SkinnedInMagazineSpace();
            if (skinnedLeft.Length > 0)
            {
                lines.Add($"while out, the gun's skinned meshes in the magazine's place: {skinnedLeft}");
            }
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

                RigidPose holdInRaw = off.Raw.Inverse().Multiply(fresh.HoldPose(off));
                float length = Vector3.Dot(fresh.TipLocal - fresh.BaseLocal, fresh.InsertDirLocal);
                if (WellInGrip && _held.HandsSolid != null)
                {
                    // 2b. Into the grip from the side and turned (task 270; user, 2026-10-19: "the mag goes through the handle", the
                    // 1911s most): it must not go into the grip through its walls.
                    yield return SideEntry(off, fresh, holdInRaw, length, lines, sideShot);
                }

                // 3. Lined up under the well, half way in on the rail, home.
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
            if (Ammo.HasCover && Ammo.CoverOpen)
            {
                // Finished as a player finishes a belt reload: the belt on the tray, the cover shut. Left open with the belt not
                // laid, every machine gun's fire test after this check read LAY BELT and did not fire (weapons-1017).
                if (Ammo.MagInserted && !Ammo.BeltLaid)
                {
                    Ammo.LayBelt();
                }
                Ammo.SetCoverOpen(false);
            }
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
                // The deep check before it takes fresh magazines from the pouch: the MG3's spare boxes ran out there, and this
                // check reported NO MAGAZINE in every run. Topped up to the weapon's own spare, as the kick check does.
                Ammo.SpareAmmo = Mathf.Max(Ammo.SpareAmmo, Ammo.Weapon.configuration.spareAmmo);
                TakeFromPouch(off);
                if (!(off.Held is MagazineProp mag))
                {
                    report($"magazine keep-out: NO MAGAZINE from the pouch (spare {Ammo.SpareAmmo}, a fresh one would hold {Ammo.RoundsForFreshMagazine()})");
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
                // What the keep-out works with (252 ae reviews: measured before the keep-out is changed): the magazine's box,
                // the well's opening, where the seat lies from the sweep's centre, how far the centre is from the grip
                // (the keep-out is off past 0.7 m).
                Vector3 seatOff = seatGun - centre;
                lines.Add($"probe: magazine {mag.KeepOutBox}, well opening {Data.MagBounds.size * 100f:F1} cm, insert {Rig.ToGun(SeatPose.position + SeatPose.rotation * mag.InsertDirLocal) - seatGun:F2}, "
                          + $"seat {Vector3.Dot(seatOff, right) * 100f:0.0} right {Vector3.Dot(seatOff, up) * 100f:0.0} up {Vector3.Dot(seatOff, -aim) * 100f:0.0} back of the sweep's centre, "
                          + $"centre {Vector3.Distance(Rig.ToWorld(centre), _held.GripWorld.position) * 100f:0} cm from the grip");
                foreach (var (name, dirGun, speed) in passes)
                {
                    int frames = 0, maxPoints = 0, probeFramesOut = 0, probeMaxOut = 0, probeMaxIn = 0, probePoints = 0;
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
                        mag.ProbeInside(_held, out probePoints, out int pIn, out int pOut);
                        if (pOut > 0)
                        {
                            probeFramesOut++;
                        }
                        probeMaxOut = Mathf.Max(probeMaxOut, pOut);
                        probeMaxIn = Mathf.Max(probeMaxIn, pIn);
                        if (Mathf.Abs(travel - 0.25f) < speed * 0.012f)
                        {
                            yield return new WaitForEndOfFrame();
                            shot($"keepout-{name.Replace(' ', '-')}");
                        }
                        travel += speed * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.03f);
                    }
                    float endSide = mag.Go != null ? Vector3.Dot(Rig.ToGun(mag.Go.transform.position) - centre, dirGun) : 0f;
                    Core.AutoTest.Metric($"magazine keep-out ({name}): frames with the magazine drawn inside the gun", frames);
                    lines.Add($"{name}: {frames} frame(s) drawn inside the gun (at most {maxPoints}/{MagazineProp.MagRows * 9} points), ended {endSide * 100f:0.0} cm {(endSide > 0.02f ? "PAST THE GUN (WENT THROUGH)" : "on its side")}"
                              + $" [probe of {probePoints} points: {probeFramesOut} frame(s) with points in the solid outside the well's opening (at most {probeMaxOut}), at most {probeMaxIn} in the solid inside the opening]");
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
        /// Autotest (task 270): the fresh magazine (the gun empty) (a) held level with the grip at half its rail, 5 cm to the
        /// side, and brought onto its line from there slowly: it must stay out of the grip, kept out at its wall and not taken
        /// onto the rail beside it (taken there, it slid in through the wall); (b) turned 25 degrees and pushed up into the
        /// well from below: taken at the mouth, with none of it through the grip's walls on the way. Then back out under the
        /// well for the straight reload that follows.
        /// </summary>
        private IEnumerator SideEntry(VRHand off, MagazineProp fresh, RigidPose holdInRaw, float length, System.Collections.Generic.List<string> lines,
                                      System.Action<string, Vector3, float> sideShot)
        {
            float halfway = fresh.RailLength - length - fresh.RailLength * 0.5f; // "below" for the tip at half the rail
            int framesIn = 0, maxIn = 0, maxOut = 0;
            bool takenBeside = false, shotBeside = false;
            for (float aside = 0.05f; aside >= 0f && ReferenceEquals(off.Held, fresh) && !Ammo.MagInserted; aside -= 0.2f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
            {
                PlaceFreshMag(off, fresh, holdInRaw, halfway, aside, 0f);
                VRHand.ScriptedVelocity[(int)off.Side] = Vector3.zero;
                yield return null;
                if (fresh.Go == null)
                {
                    break;
                }
                PoseAlongRail(fresh, RigidPose.Of(fresh.Go.transform), out float drawnLateral);
                takenBeside |= fresh.OnRail && drawnLateral > MagazineProp.WellPlay;
                int inside = fresh.PointsInsideGun(_held);
                if (inside > 0)
                {
                    framesIn++;
                    maxIn = Mathf.Max(maxIn, inside);
                }
                fresh.ProbeInside(_held, out _, out _, out int outWell);
                maxOut = Mathf.Max(maxOut, outWell);
                if (!shotBeside && aside < 0.004f)
                {
                    shotBeside = true;
                    yield return new WaitForEndOfFrame();
                    sideShot("mag-side-entry", fresh.Go.transform.TransformPoint(Vector3.Lerp(fresh.TipLocal, fresh.BaseLocal, 0.5f)), 0.3f);
                }
            }
            Core.AutoTest.Metric("magazine side entry: points through the grip's walls (probe, at most)", maxOut);
            lines.Add($"side entry (beside the grip at half depth, onto the line from 5 cm): {(takenBeside ? "TAKEN ONTO THE RAIL BESIDE THE GRIP" : fresh.OnRail ? "taken on the line" : "kept out at the grip's wall")}, "
                      + $"{framesIn} frame(s) drawn inside (at most {maxIn}/45 points), at most {maxOut} probe points through the walls{(maxOut > 6 ? " (THROUGH THE GRIP)" : "")}");
            // Out sideways, down under the well, back onto the line below it.
            for (int k = 0; k < 3; k++)
            {
                float[] asides = { 0.06f, 0.06f, 0f };
                float[] belows = { halfway, 0.06f, 0.06f };
                PlaceFreshMag(off, fresh, holdInRaw, belows[k], asides[k], 0f);
                yield return null;
                yield return null;
            }
            if (Ammo.MagInserted || !ReferenceEquals(off.Held, fresh))
            {
                yield break;
            }
            // (b) Turned 25 degrees, pushed up from under the well until the rail takes it (or 4 cm past the mouth).
            int tiltOut = 0;
            bool taken = false;
            float takenDepth = -1f;
            float mouthBelow = fresh.RailLength - length; // "below" with the tip at the rail's entry
            for (float below = 0.04f; below > mouthBelow - 0.04f && ReferenceEquals(off.Held, fresh) && !Ammo.MagInserted; below -= 0.2f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.02f))
            {
                PlaceFreshMag(off, fresh, holdInRaw, below, 0f, 0.2f, 25f);
                yield return null;
                if (fresh.Go == null)
                {
                    break;
                }
                if (!fresh.OnRail)
                {
                    fresh.ProbeInside(_held, out _, out _, out int outWell);
                    tiltOut = Mathf.Max(tiltOut, outWell);
                }
                if (fresh.OnRail)
                {
                    taken = true;
                    takenDepth = fresh.RailDepth;
                    break;
                }
            }
            lines.Add($"turned 25 deg, up from below: {(taken ? $"taken at the mouth ({takenDepth * 100f:0.0} cm in)" : "NOT TAKEN")}, at most {tiltOut} probe points through the walls before{(tiltOut > 6 ? " (THROUGH THE GRIP)" : "")}");
            // Back down out of the well for the straight reload.
            for (int k = 0; k < 3; k++)
            {
                PlaceFreshMag(off, fresh, holdInRaw, 0.08f, 0f, -0.3f);
                yield return null;
            }
        }

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
        /// Autotest: the scripted off hand's pose for a palm at a point on the rack part (slide, bolt, charging handle or
        /// pump), held as a player holds it. Shared by the rack checks.
        /// </summary>
        private System.Func<Vector3, RigidPose> RackHandPose(VRHand off)
        {
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            // The hand as a player holds the part (the controller's fixed test pose put the fist sideways beside it, and the
            // picture review saw the hand off the part on 207 guns): the palm against the part's outer side; on a part on top,
            // the fingers across it toward the far side, on a part on the side, up and over it.
            Vector3 aimW = Rig.AimWorld, upW = Rig.DirToWorld(Data.Up).normalized, rightW = Vector3.Cross(upW, aimW).normalized;
            Vector3 boreAt = Rig.ToWorld(Data.MuzzlePoint);
            // A handle clearly off to the side (an AK's bolt handle, a side charging handle) is taken from that side; any
            // other part over the top (a pistol slide, a bolt whose rack point lay a few millimetres under the bore put the
            // hand under the gun, at the trigger guard: P320, the PKM family).
            // From the side the part faces, once it is clearly off the bore (1.5 cm): the PKM's lever 2.3 cm to the right at
            // the bore's height was taken over the top, the glove 2.5 cm above it (check-1017f); a knob up and to the side
            // (M1014) is taken from that diagonal.
            Vector3 offBore = Vector3.ProjectOnPlane(RackPointWorld - boreAt, aimW);
            Vector3 outward = offBore.magnitude > 0.015f ? offBore.normalized : upW;
            Vector3 intoPart = -outward;
            Vector3 fingers = Vector3.Dot(outward, upW) > 0.7f ? (off.IsLeft ? rightW : -rightW) : upW;
            Vector3 palmX = -intoPart * off.Mirror;
            fingers = Vector3.ProjectOnPlane(fingers, palmX);
            fingers = fingers.sqrMagnitude > 1e-6f ? fingers.normalized : aimW;
            Quaternion palmRot = Quaternion.LookRotation(fingers, Vector3.Cross(fingers, palmX));
            return palm => new RigidPose(palm - intoPart * 0.02f, palmRot).Multiply(rawFromPalm);
        }

        /// <summary>
        /// Autotest: the simulated off hand is put on the rack point (slide, bolt, charging handle or pump) and takes it the
        /// way a grab there does; pictured and measured on the part at rest and pulled half way, then let go.
        /// </summary>
        internal IEnumerator RackGripSelfCheck(System.Action<string> report, System.Action<string> shot)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesChamber)
            {
                yield break;
            }
            string busy = OffHandBusy(off);
            if (busy != null)
            {
                report($"rack grip: skipped ({busy})");
                yield break;
            }
            // The rack point is put on the model's surface once the gun is measured (HeldWeapon.SnapRackPoint): waited
            // for, or the hand was put at the analysis's point and the pictures showed it where the point had been.
            for (float until = Time.unscaledTime + 2f; _held.HandsSolid == null && Time.unscaledTime < until;)
            {
                yield return null;
            }
            int side = (int)off.Side;
            System.Func<Vector3, RigidPose> At = RackHandPose(off);
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
                // Which parts moved, by name, how far, and how far from the rack point (252 ae reviews: the next run must
                // say which part moved, not only that something did).
                var movers = new List<(string name, float moved, float fromPoint)>();
                var movingRenderers = new List<Renderer>();
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
                        float fromPoint = kv.Key is Renderer rr ? Mathf.Sqrt(rr.bounds.SqrDistance(RackPointWorld))
                            : kv.Key is Transform bb ? Vector3.Distance(bb.position, RackPointWorld) : float.MaxValue;
                        movers.Add(((kv.Key is Renderer ? "" : "bone ") + kv.Key.name, moved, fromPoint));
                        if (kv.Key is Renderer mr)
                        {
                            movingRenderers.Add(mr);
                        }
                    }
                }
                movers.Sort((a, b) => b.moved.CompareTo(a.moved));
                var moverText = new List<string>();
                for (int i = 0; i < movers.Count && i < 5; i++)
                {
                    moverText.Add($"'{movers[i].name}' {movers[i].moved * 1000f:0} mm ({movers[i].fromPoint * 100f:0.0} cm from the rack point)");
                }
                if (movers.Count > 5)
                {
                    moverText.Add($"{movers.Count - 5} more");
                }
                // Whether the moving part is seen at all from outside the gun (a carrier in the receiver moves, unseen).
                string seenText = "";
                if (movingRenderers.Count > 0)
                {
                    var gunRenderers = new List<Renderer>();
                    var arms = new HashSet<Renderer>(Rig.ArmRenderers);
                    foreach (Renderer r in _held.Weapon.GetComponentsInChildren<Renderer>(false))
                    {
                        if (r.enabled && !arms.Contains(r) && (r is MeshRenderer || r is SkinnedMeshRenderer))
                        {
                            gunRenderers.Add(r);
                        }
                    }
                    float share = GunVolume.VisibleShare(gunRenderers, movingRenderers, Rig.AimWorld, Rig.DirToWorld(Data.Up), out string views);
                    if (share >= 0f)
                    {
                        Core.AutoTest.Metric("rack: % of the moving parts seen from outside the gun at half pull", share * 100f);
                        seenText = $"; seen from outside: {share * 100f:0}% of the moving parts ({views}){(share < 0.05f ? " (HIDDEN IN THE GUN)" : "")}";
                    }
                }
                Core.AutoTest.Metric("rack: mm the drawn gun moves at half pull (most)", most * 1000f);
                string drawnMoves = movedCount == 0
                    ? $"NOTHING DRAWN MOVES (most {most * 1000f:0} mm for a {halfStroke * 1000f:0} mm half pull)"
                    : $"{movedCount} drawn part(s) move, most {most * 1000f:0} mm; the nearest is {nearestMoving * 100f:0.0} cm from the hand"
                      + (nearestMoving > 0.03f ? " (THE PART UNDER THE HAND DOES NOT MOVE)" : "")
                      + $" [{string.Join(", ", moverText.ToArray())}]{seenText}";
                Vector3 rackGun = Rig.ToGun(RackPointWorld) - Data.MuzzlePoint;
                Vector3 rightGun = Vector3.Cross(Data.Up, Data.AimAxis).normalized;
                drawnMoves += $"; rack point back {Vector3.Dot(rackGun, -Data.AimAxis) * 100f:0.0} up {Vector3.Dot(rackGun, Data.Up) * 100f:0.0} right {Vector3.Dot(rackGun, rightGun) * 100f:0.0} cm from the muzzle";
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
        /// Autotest: the support hand holding the handguard slides back along the gun onto the charging handle with its grip still
        /// closed, and must take the handle (Tasks 322: it stayed on the handguard, which lets go only at 60 cm, and the rack, which
        /// needs a fresh squeeze, never started: "i cannot pull back the bolt thingy").
        /// </summary>
        internal IEnumerator SupportSlideSelfCheck(System.Action<string> report)
        {
            VRHand off = _sys.Rig.Off;
            if (!Manual || !Ammo.UsesChamber || IsPump || GripData.IsPistol || !GripData.HasSupportPoint)
            {
                yield break;
            }
            string busy = OffHandBusy(off);
            if (busy != null)
            {
                report($"support hand slid onto the charging handle: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            System.Func<Vector3, RigidPose> At = RackHandPose(off);
            Vector3 from = GripRig.ToWorld(GripData.SupportPoint);
            bool took = false;
            string role = "";
            VRInput.SimulatedGrab[side] = true;
            try
            {
                VRHand.Scripted[side] = At(from);
                yield return null;
                yield return null;
                off.Role = HandRole.Foregrip;
                _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
                yield return null;
                for (int i = 1; i <= 20 && !took; i++)
                {
                    VRHand.Scripted[side] = At(Vector3.Lerp(from, RackPointWorld, i / 20f));
                    yield return null;
                    took = off.Role == HandRole.Slide;
                }
                role = off.Role.ToString();
            }
            finally
            {
                if (_racking)
                {
                    EndRack(off, chamber: false);
                }
                if (off.Role == HandRole.Foregrip)
                {
                    _held.SetTwoHanded(false);
                    off.Role = HandRole.Free;
                }
                VRHand.Scripted[side] = null;
                VRInput.SimulatedGrab[side] = false;
            }
            Core.AutoTest.Metric("support hand slid onto the charging handle: taken (1) or not (0)", took ? 1 : 0);
            report($"support hand slid back onto the charging handle, grip held: {(took ? "took the handle" : $"DID NOT TAKE IT (the hand: {role})")}");
            yield return new WaitForSecondsRealtime(0.3f);
            if (HandDriven && Ammo.BoltOpen)
            {
                // A bolt left open by the take is closed again, as the rack-grip check does (the shots after it met an open bolt).
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
            if (!GripData.HasSupportPoint)
            {
                yield break;
            }
            if (off.Role != HandRole.Free)
            {
                report($"two hands: skipped (the off hand is busy: {off.Role})");
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

        /// <summary>Autotest: the gun is worked by a pump the support hand's trigger takes (<see cref="PumpTriggerSelfCheck"/>).</summary>
        internal bool WorksByPump => Ammo != null && Manual && Ammo.UsesChamber && IsPump;

        /// <summary>
        /// Autotest: the pump worked by the support hand's trigger, as the player does it (user, 2026-10-16: "only rackable
        /// if the trigger finger on the rack was pressed and held"; 255.I6-2): the support hand holds the pump, the trigger
        /// squeezed takes it, a full stroke back and home cycles a round, the trigger let go leaves the hand holding the gun
        /// by the pump again. Also: the grip alone (no trigger) does not work the pump.
        /// </summary>
        internal IEnumerator PumpTriggerSelfCheck(System.Action<string> report)
        {
            VRHand off = _sys.Rig.Off;
            if (!WorksByPump || off.Role != HandRole.Free || off.Held != null || Rig.GunBody == null || _racking)
            {
                yield break;
            }
            int side = (int)off.Side;
            bool chambered = Ammo.Chambered, spent = Ammo.SpentInChamber;
            int rounds = Ammo.MagRounds;
            System.Func<Vector3, RigidPose> at = RackHandPose(off);
            Vector3 start = RackPointWorld;
            Vector3 pull = Rig.DirToWorld(StrokeDir) * StrokeLength;
            bool held = false, taken = false, backOnGrip = false, freeAfter = false;
            float gripOnly = 0f, back = 0f, drawnMost = 0f;
            int cycledFrom = -1;
            try
            {
                // The support hand on the pump (a grab there is the foregrip, as for any handguard).
                VRHand.Scripted[side] = at(start);
                yield return null;
                yield return null;
                VRInput.SimulatedGrab[side] = true;
                off.Role = HandRole.Foregrip;
                _held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
                yield return new WaitForSecondsRealtime(0.3f);
                held = off.Role == HandRole.Foregrip;
                // The grip alone, pulled back: the pump must not move (it was worked by any grab).
                float before = _held.SlideOffset;
                for (int i = 1; i <= 8; i++)
                {
                    VRHand.Scripted[side] = at(start + pull * (0.5f * i / 8f));
                    yield return null;
                }
                gripOnly = Mathf.Abs(_held.SlideOffset - before);
                VRHand.Scripted[side] = at(start);
                yield return new WaitForSecondsRealtime(0.2f);
                // Make the chamber's state known: a live round in it, the tube with rounds behind it.
                Ammo.Chambered = true;
                Ammo.SpentInChamber = false;
                if (Ammo.MagRounds == 0)
                {
                    Ammo.MagRounds = Mathf.Max(1, Mathf.Min(2, Ammo.Capacity));
                }
                cycledFrom = Ammo.MagRounds;
                // The trigger squeezed: the hand takes the pump.
                VRInput.SimulatedUse[side] = true;
                VRInput.SimulatePress(DigitalAction.Use, off.Side);
                yield return null;
                yield return null;
                taken = _racking && _rackByTrigger && off.Role == HandRole.Slide;
                var drawnHome = _held.DrawnLayout();
                // All the way back, then home.
                for (int i = 1; i <= 12; i++)
                {
                    VRHand.Scripted[side] = at(start + pull * (i / 12f));
                    yield return null;
                }
                back = Travel > 1e-4f ? _held.SlideOffset / Travel : 0f;
                // What the eye sees move (the stroke above is the mod's number): 76 EAGLE's pump block moved 15 mm in the
                // grip-only rack check while the stroke read 80% (252 ae).
                yield return new WaitForEndOfFrame();
                foreach (var kv in drawnHome)
                {
                    if (_held.DrawnLayout().TryGetValue(kv.Key, out Vector3 now))
                    {
                        drawnMost = Mathf.Max(drawnMost, Vector3.Distance(kv.Value, now));
                    }
                }
                for (int i = 11; i >= 0; i--)
                {
                    VRHand.Scripted[side] = at(start + pull * (i / 12f));
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.1f);
                // The trigger let go: the hand holds the gun by the pump again.
                VRInput.SimulatedUse[side] = false;
                yield return null;
                yield return null;
                backOnGrip = !_racking && off.Role == HandRole.Foregrip;
                VRInput.SimulatedGrab[side] = false;
                yield return new WaitForSecondsRealtime(0.3f);
                freeAfter = off.Role == HandRole.Free;
            }
            finally
            {
                VRInput.SimulatedUse[side] = false;
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
                if (_racking)
                {
                    EndRack(off, chamber: false);
                }
                if (off.Role != HandRole.Free)
                {
                    _held.SetTwoHanded(false);
                    off.Role = HandRole.Free;
                }
            }
            // A full stroke ejects the round and feeds the next: one round fewer in the tube, one chambered.
            bool cycled = cycledFrom > 0 && Ammo.Chambered && Ammo.MagRounds == cycledFrom - 1;
            Core.AutoTest.Metric("pump by trigger: share of the stroke at the full pull", back);
            Core.AutoTest.Metric("pump by trigger: mm the drawn pump moves at the full pull", drawnMost * 1000f);
            report($"pump by trigger: {(held ? "held by the pump" : "NOT HELD BY THE PUMP")}, the grip alone moved it {gripOnly * 1000f:0} mm{(gripOnly > 0.005f ? " (THE GRIP ALONE WORKS THE PUMP)" : "")}; "
                   + $"{(taken ? "the trigger took the pump" : "THE TRIGGER DID NOT TAKE THE PUMP")}, full pull {back * 100f:0}% of the stroke{(taken && back < 0.8f ? " (NOT ALL THE WAY BACK)" : "")}"
                   + $" (the drawn gun moved {drawnMost * 1000f:0} mm{(taken && drawnMost < Travel * 0.5f ? ": THE PUMP IS NOT SEEN MOVING" : "")}), "
                   + $"{(cycled ? "cycled a round" : taken ? "DID NOT CYCLE A ROUND" : "not cycled")}; "
                   + $"trigger let go: {(backOnGrip ? "the hand holds the pump again" : "NOT BACK ON THE PUMP")}, grip let go: {(freeAfter ? "free" : "NOT FREE")}");
            // Back as it was.
            _rackedBack = false;
            _held.SlideOffset = 0f;
            Ammo.BoltOpen = false;
            Ammo.Chambered = chambered;
            Ammo.SpentInChamber = spent;
            Ammo.MagRounds = rounds;
            Ammo.Sync();
        }

        /// <summary>
        /// Autotest: an en-bloc rifle as the player loads and fires it (255.I6-5): with the bolt shut on a round a clip is
        /// refused; through the open action a full clip goes in, the bolt slams home on its own and chambers; the last round
        /// fired throws the empty clip and leaves the bolt locked open, ready for the next clip.
        /// </summary>
        internal IEnumerator EnBlocSelfCheck(System.Action<string> report)
        {
            if (!Manual || Ammo == null || !Data.EnBloc || !Ammo.UsesDetachableMag)
            {
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            string busy = OffHandBusy(off);
            if (busy != null)
            {
                report($"en-bloc: skipped ({busy})");
                yield break;
            }
            bool chambered = Ammo.Chambered, inserted = Ammo.MagInserted, locked = Ammo.SlideLocked;
            int rounds = Ammo.MagRounds;
            if (Ammo.MagInserted)
            {
                Ammo.EjectMagazine();
            }
            // The bolt shut on a round, no clip: a clip cannot go in.
            Ammo.Chambered = true;
            Ammo.SlideLocked = false;
            Ammo.Sync();
            MagazineProp clip = MagazineProp.Create(Rig, PropKind.Magazine, Ammo.Capacity);
            clip.Owner = Ammo;
            clip.Attach(off);
            clip.FollowHand();
            bool refusedShut = !AcceptsNow(clip);
            // As after the last round: the bolt locked back over an empty chamber. The clip goes in and releases the bolt.
            Ammo.Chambered = false;
            Ammo.SlideLocked = true;
            Ammo.Sync();
            yield return new WaitForSecondsRealtime(0.2f);
            bool accepted = AcceptsNow(clip);
            if (accepted)
            {
                SeatProp(off, clip);
            }
            else
            {
                clip.Destroy();
            }
            yield return new WaitForSecondsRealtime(0.4f);
            bool seated = Ammo.MagInserted, released = !Ammo.SlideLocked, fed = Ammo.Chambered;
            // Fired to the last round: the empty clip flies out, the bolt stays open.
            int shots = 0;
            while (Ammo.Chambered && shots < Ammo.Capacity + 2)
            {
                Ammo.OnShot();
                OnShot();
                shots++;
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.3f);
            bool thrown = !Ammo.MagInserted, lockedOpen = Ammo.SlideLocked;
            report($"en-bloc: bolt shut on a round: {(refusedShut ? "clip refused" : "CLIP TAKEN WITH THE BOLT SHUT")}; through the open action: "
                   + $"{(accepted && seated ? "clip in" : "CLIP NOT SEATED")}, {(released ? "bolt home on its own" : "BOLT NOT RELEASED")}, {(fed ? "chambered" : "NOTHING CHAMBERED")}; "
                   + $"{shots} shot(s): {(thrown ? "empty clip thrown out" : "EMPTY CLIP NOT THROWN OUT")}, {(lockedOpen ? "bolt locked open" : "BOLT NOT LOCKED OPEN")}{(_pingSource != null ? $", ping '{_pingSource.clip.name}'" : "")}");
            // Back as it was.
            Ammo.SlideLocked = locked;
            Ammo.Chambered = chambered;
            Ammo.MagInserted = inserted;
            Ammo.MagRounds = rounds;
            Ammo.Sync();
        }

        /// <summary>
        /// Autotest: an emptied magazine dropped at the hip is not handed back by a squeeze in the pouch (user, 2026-10-16: "I
        /// grab an empty mag from the pouch"; 255.I6-6): the squeeze goes through the hand's own grab (TryGrab) and must
        /// bring a full one.
        /// </summary>
        internal IEnumerator EmptyAtPouchSelfCheck(System.Action<string> report)
        {
            if (!Manual || Ammo == null || !Ammo.UsesDetachableMag || !Ammo.MagInserted || Ammo.BoxLocked)
            {
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            string busy = OffHandBusy(off) ?? (_ejecting != null ? "a magazine is coming out" : !Ammo.InfiniteSpare && Ammo.SpareAmmo <= 0 ? "no spare rounds" : null);
            if (busy != null)
            {
                report($"empty magazine at the pouch: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            bool chambered = Ammo.Chambered;
            int rounds = Ammo.MagRounds, spare = Ammo.SpareAmmo;
            Ammo.MagRounds = 0;
            EjectMagazine();
            MagazineProp empty = _ejecting;
            FinishEject();
            Vector3 pouch = _sys.Zones.Pouch;
            if (empty != null && empty.Go != null)
            {
                // Lying in the pouch zone, held still there for the squeeze.
                empty.Go.transform.position = pouch + Vector3.down * 0.03f;
                if (empty.Body != null)
                {
                    empty.Body.velocity = Vector3.zero;
                    empty.Body.isKinematic = true;
                }
            }
            RigidPose rawFromPalm = off.Palm.Inverse().Multiply(off.Raw);
            MagazineProp got = null;
            try
            {
                VRHand.Scripted[side] = new RigidPose(pouch, off.Palm.rotation).Multiply(rawFromPalm);
                yield return null;
                yield return null;
                VRInput.SimulateGrabPress(off.Side);
                for (int i = 0; i < 6 && off.Held == null; i++)
                {
                    yield return null;
                }
                got = off.Held as MagazineProp;
            }
            finally
            {
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
            }
            bool emptyBack = got != null && got == empty;
            report($"empty magazine at the pouch: {(empty == null ? "NO MAGAZINE CAME OUT" : emptyBack ? "THE EMPTY MAGAZINE WAS HANDED BACK" : got != null && got.Rounds > 0 ? $"a full one from the pouch ({got.Rounds} rounds)" : got != null ? "AN EMPTY MAGAZINE FROM THE POUCH" : "nothing taken (the pouch did not answer the squeeze)")}");
            // Back as it was.
            if (got != null && got != empty)
            {
                Ammo.ReturnSpare(got.Rounds);
                got.Rounds = 0;
                got.Destroy();
            }
            if (empty != null)
            {
                empty.Rounds = 0;
                empty.Destroy();
            }
            Ammo.MagInserted = true;
            Ammo.MagRounds = rounds;
            Ammo.Chambered = chambered;
            Ammo.Sync();
            if (!Ammo.InfiniteSpare && Ammo.SpareAmmo < spare)
            {
                Ammo.ReturnSpare(spare - Ammo.SpareAmmo); // the reserve as before the check
            }
        }

        /// <summary>
        /// Autotest: the seated magazine taken 7 cm off the well's line and pulled (255.I6-7): it must stay on its rail and
        /// slide out along the well, not jump to the hand through the receiver (the hand's offset is kept from the grab:
        /// RailLateral0). The reload film only pulls 1.5 cm aside.
        /// </summary>
        internal IEnumerator OffLinePullSelfCheck(System.Action<string> report)
        {
            if (!Manual || Ammo == null || !Ammo.UsesDetachableMag || !Ammo.MagInserted || Ammo.BoxLocked || Rig.GunBody == null)
            {
                yield break;
            }
            VRHand off = _sys.Rig.Off;
            string busy = OffHandBusy(off) ?? (_ejecting != null ? "a magazine is coming out" : null);
            if (busy != null)
            {
                report($"magazine pulled 7 cm off the line: skipped ({busy})");
                yield break;
            }
            int side = (int)off.Side;
            int rounds = Ammo.MagRounds;
            RigidPose palm0 = off.Palm, rawFromPalm = palm0.Inverse().Multiply(off.Raw);
            MagazineProp probe = MagazineProp.Create(Rig, PropKind.Magazine, 0);
            probe.RailDepth = probe.RailLength;
            RigidPose holding = probe.RailPose(SeatPose).Multiply(probe.HoldPose(off).Inverse().Multiply(palm0));
            float railLength = probe.RailLength;
            Vector3 insert = SeatPose.TransformDirection(probe.InsertDirLocal);
            probe.Destroy();
            Vector3 aside = Vector3.Cross(insert, Rig.AimWorld).normalized;
            Vector3 hand = holding.position + aside * 0.07f;
            bool firstFrame = false, halfWay = false;
            try
            {
                VRHand.Scripted[side] = new RigidPose(hand, holding.rotation).Multiply(rawFromPalm);
                yield return null;
                yield return null;
                VRInput.SimulatedGrab[side] = true;
                PullSeatedMagazine(off);
                yield return null;
                firstFrame = off.Held is MagazineProp m1 && m1.OnRail;
                for (int i = 1; i <= 8; i++)
                {
                    VRHand.Scripted[side] = new RigidPose(hand - insert * (railLength * 0.5f * i / 8f), holding.rotation).Multiply(rawFromPalm);
                    yield return null;
                }
                halfWay = off.Held is MagazineProp m2 && m2.OnRail;
                // Pushed home again.
                for (int i = 7; i >= -1; i--)
                {
                    VRHand.Scripted[side] = new RigidPose(hand - insert * (railLength * 0.5f * i / 8f), holding.rotation).Multiply(rawFromPalm);
                    yield return null;
                }
            }
            finally
            {
                VRInput.SimulatedGrab[side] = false;
                VRHand.Scripted[side] = null;
            }
            yield return null;
            if (off.Held is MagazineProp left)
            {
                if (AcceptsNow(left))
                {
                    SeatProp(off, left);
                }
                else
                {
                    Ammo.ReturnSpare(left.Rounds);
                    left.Rounds = 0;
                    left.Destroy();
                }
            }
            if (!Ammo.MagInserted)
            {
                Ammo.MagInserted = true;
                Ammo.MagRounds = rounds;
                Ammo.Sync();
            }
            report($"magazine pulled 7 cm off the well's line: {(firstFrame ? "on its rail on the first frame" : "NOT ON THE RAIL ON THE FIRST FRAME")}, "
                   + $"{(halfWay ? "still on it half way out" : "NOT ON THE RAIL HALF WAY OUT")}");
        }

        /// <summary>
        /// Autotest: an empty reload as the player does it (Tasks 252 c/d/i, 254): the last round fired leaves the slide or
        /// bolt locked back; a fresh magazine goes in and the drawn slide must stay back (it ran forward on its own on the
        /// mwii-g17); released, it must come home (it stayed back until the first shot on the FNX-45 and many rifles); then a
        /// hand rack must visibly move it (a broken block once reset it every frame). Measured on the drawn model, in gun space.
        /// </summary>
        internal IEnumerator EmptyReloadSelfCheck(System.Action<string> report)
        {
            if (!Manual || Ammo == null || !Ammo.UsesDetachableMag || !Ammo.UsesChamber || !Data.LockOnEmpty || Data.EnBloc || Ammo.HasCover)
            {
                yield break; // (a belt-fed gun's fresh box comes with its belt unlaid: it could not fire after this check)
            }
            if (_racking || Rig.GunBody == null || !ReferenceEquals(_held.ActiveRig, _held.Rig))
            {
                report($"empty reload: skipped ({(_racking ? "a rack under way" : Rig.GunBody == null ? "no gun body" : "a sub weapon is active")})");
                yield break;
            }
            bool chambered = Ammo.Chambered, inserted = Ammo.MagInserted, locked = Ammo.SlideLocked;
            int rounds = Ammo.MagRounds;
            float Moved(Dictionary<Object, Vector3> a, Dictionary<Object, Vector3> b)
            {
                float most = 0f;
                foreach (var kv in a)
                {
                    if (b.TryGetValue(kv.Key, out Vector3 at))
                    {
                        most = Mathf.Max(most, Vector3.Distance(kv.Value, at));
                    }
                }
                return most;
            }
            // Which drawn parts are away from home, by name, and how far back (+) or forward (-) along the bore (ARS MP9: a
            // part 44 mm off after the release, not named; 252 ae reviews: named before anything is changed).
            string Away(Dictionary<Object, Vector3> a, Dictionary<Object, Vector3> b)
            {
                var away = new List<(string name, float moved, float back)>();
                foreach (var kv in a)
                {
                    if (b.TryGetValue(kv.Key, out Vector3 at) && Vector3.Distance(kv.Value, at) >= 0.003f)
                    {
                        away.Add(((kv.Key is Renderer ? "" : "bone ") + kv.Key.name, Vector3.Distance(kv.Value, at), Vector3.Dot(at - kv.Value, -Data.AimAxis)));
                    }
                }
                away.Sort((x, y) => y.moved.CompareTo(x.moved));
                var parts = new List<string>();
                for (int i = 0; i < away.Count && i < 3; i++)
                {
                    parts.Add($"'{away[i].name}' {away[i].moved * 1000f:0} mm ({away[i].back * 1000f:0} mm back)");
                }
                return parts.Count == 0 ? "nothing" : string.Join(", ", parts.ToArray()) + (away.Count > 3 ? $" and {away.Count - 3} more" : "");
            }
            yield return new WaitForSecondsRealtime(0.3f);
            var home = _held.DrawnLayout();
            string homeState = _held.AnimatorStateText;
            // The last round fired: nothing chambered, the magazine empty, the slide caught back.
            Ammo.Chambered = false;
            Ammo.MagRounds = 0;
            Ammo.SlideLocked = true;
            Ammo.Sync();
            yield return new WaitForSecondsRealtime(0.5f);
            var lockedBack = _held.DrawnLayout();
            float lockTravel = Moved(home, lockedBack);
            // Locked with a charging handle of its own drawn home (an M4's T): the grab point must be on it, not where the carrier
            // took it (it sat 7.5 cm behind the T; user, 2026-10-20: "some m4a1 racks got unrackable again").
            if (_held.HandleLockedAlone && Rig.Handle != null)
            {
                Vector3 grab = RackPointWorld;
                float gap = float.MaxValue;
                foreach (Renderer r in Rig.Handle.GetComponentsInChildren<Renderer>())
                {
                    gap = Mathf.Min(gap, Mathf.Sqrt(r.bounds.SqrDistance(grab)));
                }
                if (gap < float.MaxValue)
                {
                    Core.AutoTest.Metric("locked empty: cm from the grab point to the drawn charging handle", gap * 100f);
                    string handleLine = $"locked empty: the grab point {gap * 100f:0.0} cm from the drawn charging handle";
                    if (gap > 0.03f)
                    {
                        handleLine += " (THE HANDLE IS OUT OF THE GRAB)";
                    }
                    report(handleLine);
                }
            }
            // A fresh magazine in.
            Ammo.InsertMagazine(Ammo.Capacity);
            yield return new WaitForSecondsRealtime(0.6f);
            float ranForward = Moved(lockedBack, _held.DrawnLayout());
            bool stillLocked = Ammo.SlideLocked;
            // Released (the slide release, as the A button does).
            ReleaseSlide();
            yield return new WaitForSecondsRealtime(0.8f);
            var released = _held.DrawnLayout();
            float leftBack = Moved(home, released);
            string releasedAway = Away(home, released), releasedState = _held.AnimatorStateText;
            bool fed = Ammo.Chambered;
            // A hand rack: the off hand takes the part and pulls it half way back; the drawn part must follow. Through the
            // hand's own path (BeginRack, UpdateRack, EndRack): the offset set directly left a reset in that path unseen
            // (255.I6-1).
            float racked = -1f;
            VRHand off = _sys.Rig.Off;
            if (off.Role == HandRole.Free && off.Held == null)
            {
                int side = (int)off.Side;
                System.Func<Vector3, RigidPose> at = RackHandPose(off);
                Vector3 start = RackPointWorld;
                Vector3 pull = Rig.DirToWorld(StrokeDir) * (StrokeLength * 0.5f);
                VRInput.SimulatedGrab[side] = true;
                try
                {
                    VRHand.Scripted[side] = at(start);
                    yield return null;
                    yield return null;
                    BeginRack(off);
                    yield return new WaitForSecondsRealtime(0.2f);
                    for (int i = 1; i <= 10; i++)
                    {
                        VRHand.Scripted[side] = at(start + pull * (i / 10f));
                        yield return null;
                    }
                    yield return new WaitForSecondsRealtime(0.1f);
                    yield return new WaitForEndOfFrame();
                    racked = _racking ? Moved(home, _held.DrawnLayout()) : -2f;
                    VRInput.SimulatedGrab[side] = false;
                    yield return new WaitForSecondsRealtime(0.5f); // let go: it goes home
                }
                finally
                {
                    VRHand.Scripted[side] = null;
                    VRInput.SimulatedGrab[side] = false;
                }
                if (_racking)
                {
                    EndRack(off, chamber: false);
                }
            }
            Core.AutoTest.Metric("empty reload: mm the drawn slide ran forward on a fresh magazine", ranForward * 1000f);
            Core.AutoTest.Metric("empty reload: mm the drawn slide stayed back after the release", leftBack * 1000f);
            if (racked >= 0f)
            {
                Core.AutoTest.Metric("empty reload: mm the drawn slide moved at half a hand rack", racked * 1000f);
            }
            float tol = Mathf.Max(0.003f, lockTravel * 0.25f);
            string verdict = lockTravel < 0.003f ? "THE DRAWN SLIDE DOES NOT SHOW THE LOCK (nothing moved when locked)"
                : ranForward > tol ? $"THE SLIDE RAN FORWARD ON ITS OWN when the magazine went in ({ranForward * 1000f:0} mm)"
                : leftBack > tol ? $"THE SLIDE STAYED BACK after the release ({leftBack * 1000f:0} mm from home: {releasedAway}; at home {homeState}, released {releasedState})"
                : "stayed back with the fresh magazine, home when released";
            report($"empty reload: locked back {lockTravel * 1000f:0} mm, {(stillLocked ? "still locked" : "NOT LOCKED")} with a fresh magazine, {verdict}, {(fed ? "chambered" : "NOTHING CHAMBERED")}; "
                   + (racked == -1f ? "hand rack not checked (the off hand was busy)"
                       : racked == -2f ? "THE HAND LET GO OF THE RACK during the pull"
                       : $"half a hand rack moves the drawn gun {racked * 1000f:0} mm{(racked < Mathf.Max(0.002f, Travel * 0.15f) ? " (THE HAND RACK MOVES NOTHING)" : "")}"));
            // Back as it was.
            Ammo.SlideLocked = locked;
            Ammo.Chambered = chambered;
            Ammo.MagInserted = inserted;
            Ammo.MagRounds = rounds;
            Ammo.Sync();
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
            if (off.Model != null)
            {
                // First: whatever throws below (HeldWeapon catches it), the off glove is not left pulled toward an anchor on a gun
                // that is gone (review, 2026-10-20).
                off.Model.GraspAnchor = null;
            }
            _pullingIn = null;
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
