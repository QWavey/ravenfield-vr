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
            bool paused = GameManager.IsPaused();
            if (_wasPaused && !paused)
            {
                // Back from a pause: re-anchor the motion baselines so a hand that moved while paused does not jump a kick or
                // a rack on the first frame back (the cockpit re-anchors its wheel and stick the same way, 2026-10-21).
                _kickPrevAlong = float.NaN;
                _kickPushSince = -1f;
                _kickTicked = false;
                _supportRest = -1f;
                if (_racking)
                {
                    _rackBase = Travel > 1e-5f ? Mathf.Clamp01(_held.SlideOffset / Travel) : _rackBase;
                    _rackStartGun = Rig.ToGun(off.Palm.position);
                    _tugStroke = _rackBase * StrokeLength;
                    _lastTick = _tugStroke;
                }
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
        private RigidPose SeatPose => ChamberSeat(_seatChamber);

        /// <summary>
        /// The seat of chamber <paramref name="chamber"/> (a break action's; the magazine's own is chamber 0, and every other gun
        /// has only that one): the magazine's seat moved to where that chamber's shell sits, swung open with the barrels.
        /// </summary>
        private RigidPose ChamberSeat(int chamber)
        {
            RigidPose seat = Data.MagSeat;
            if (chamber > 0 && chamber < Data.ChamberShellRest.Count)
            {
                seat = new RigidPose(seat.position + Data.ChamberShellRest[chamber].position - Data.ChamberShellRest[0].position, seat.rotation);
            }
            if (Data.SeatOnHinge && _held.HingeOpenness > 0.001f)
            {
                seat = HingeRotate(seat, _held.HingeOpenness);
            }
            return Rig.GunBodyPose.Multiply(seat);
        }

        /// <summary>Which chamber the shell in the hand goes into (0 but on a break action of two or more).</summary>
        private int _seatChamber;

        /// <summary>The empty chamber nearest <paramref name="at"/> (world), or 0 when none is empty or the gun has one.</summary>
        private int NearestEmptyChamber(Vector3 at)
        {
            bool[] full = _held.ChamberFull;
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < Data.ChamberShellRest.Count && i < full.Length; i++)
            {
                float distance = (ChamberSeat(i).position - at).sqrMagnitude;
                if (!full[i] && distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }
            return best;
        }

        /// <summary>A gun-space pose carried along by the hinge opened <paramref name="openness"/> of the way.</summary>
        private RigidPose HingeRotate(RigidPose pose, float openness) => Data.HingeRotated(pose, openness);

        /// <summary>Base plate of the seated magazine, where a hand takes hold to pull it.</summary>
        private Vector3 SeatedMagBaseWorld
        {
            get
            {
                Quaternion seatInv = Runtime.RigidPose.Conjugate(Data.MagSeat.rotation);
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
        /// <summary>The lift's flags were taken from the ammunition's state since the gun was drawn (see the belt's update).</summary>
        private bool _beltStateTaken;
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
            if (prop.Go == null)
            {
                prop.Destroy(); // gone with a scene change: frees the hand (Drop threw on the destroyed object; prop physics hunt, 2026-10-21)
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

            if (!prop.OnRail && prop.Kind == PropKind.Shell && Data.ChamberShellRest.Count > 1 && prop.Go != null)
            {
                // A double barrel's shell goes into the empty chamber it is held by (both took it into the first one).
                _seatChamber = NearestEmptyChamber(prop.Go.transform.TransformPoint(prop.TipLocal));
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
                    _railInSpeed = 0f;
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
                _railInSpeed = 0f;
                return;
            }
            prop.PrevDepth = prop.RailDepth;
            prop.RailDepth = Mathf.Clamp(along, prop.Kind == PropKind.Rocket ? -RocketLineAhead : 0f, prop.RailLength);
            if (prop.Kind == PropKind.Magazine)
            {
                // How hard it is driven in, against the gun (the mag kick's measure: both hands share the body's motion).
                _railInSpeed = Mathf.Max(_railInSpeed, Vector3.Dot(off.BodyThrowVelocity() - _sys.Rig.Dominant.BodyThrowVelocity(),
                                                                  SeatPose.TransformDirection(prop.InsertDirLocal)));
            }
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
        /// <summary>How far off a racked part's stroke line the hand may drift before it lets go of the part.</summary>
        private const float RackDrift = 0.35f;
        /// <summary>
        /// How far past either end of its stroke the hand may pull a racked part before it lets go (the part stops there; the
        /// real hand does not): Boneworks' grips break the same way once the hand has desynced from them.
        /// </summary>
        private const float RackOvershoot = 0.3f;
        /// <summary>How far the glove of a hand working a part stays on it: to the rack's let-go, drift and overshoot together.</summary>
        private const float HeldPartReach = 0.46f;
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

        /// <summary>The fastest the magazine on the rail has been driven in so far (m/s, against the gun), for the slap release at its seat.</summary>
        private float _railInSpeed;
        /// <summary>A magazine driven home at least this fast (m/s) releases a locked slide (<see cref="VRConfig.MagSlapReleasesSlide"/>).</summary>
        private const float SlapSpeed = 0.9f;

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
                    else if (Ammo.SlideLocked)
                    {
                        // Slammed home, the locked slide drops and chambers the first round (Boneworks' Gun.isSlideReleaseOnCollision);
                        // a gentle push leaves it locked for the release or a rack. The speed is logged for tuning in the headset.
                        bool slam = VRConfig.MagSlapReleasesSlide.Value && _railInSpeed >= SlapSpeed && Ammo.FeedReady && Ammo.MagRounds > 0;
                        Log.Info($"{Data.Key}: magazine seated at {_railInSpeed:0.00} m/s into the locked gun ({(slam ? "slammed: the slide drops" : "the slide stays locked")}).");
                        if (slam)
                        {
                            ReleaseSlide();
                            _held.Nudge(Rig.AimWorld * 0.12f, 12f);
                            off.Haptic(0.06f, 100f, 0.9f);
                        }
                    }
                    _railInSpeed = 0f;
                    break;
                case PropKind.Shell:
                {
                    if (_seatChamber < _held.ChamberFull.Length)
                    {
                        _held.ChamberFull[_seatChamber] = true; // drawn in the chamber it went into, not the first empty one
                    }
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
                if (palmed.Go == null)
                {
                    palmed.Destroy(); // gone with a scene change: Drop throws on the destroyed object (as the held prop is guarded above)
                }
                else if (Stows(off))
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
            // Past the stroke's ends the part stops and the hand pulls on alone: far enough on, it has let go.
            float unclamped = _rackBase + pull / StrokeLength;
            float past = Mathf.Max(0f, Mathf.Max(unclamped - 1f, -unclamped)) * StrokeLength;
            if (!off.GrabHeld || drift > RackDrift || past > RackOvershoot)
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
            Quaternion gunRot = hand.rotation * Runtime.RigidPose.Conjugate(d.HasGripRotation ? d.GripRotation : Quaternion.LookRotation(d.AimAxis, d.Up));
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

        /// <summary>Weapon is going away: finish what is in progress so no ammo is lost or duplicated.</summary>
        public void Release()
        {
            VRHand off = _sys.Rig.Off;
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
                if (prop.Owner == null && prop.WeaponKey == Data.Key)
                {
                    prop.Owner = Ammo;
                }
            }
            _held.SlideOffset = 0f;
        }
    }
}
