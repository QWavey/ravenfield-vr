using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Owns everything weapon-related in VR: which weapon is in the hand, the per-weapon ammo states, the
    /// reload / throw / melee handlers, holster switching, body zones and the procedural mechanical sounds.
    /// </summary>
    public sealed class WeaponSystem : IDisposable
    {
        public static WeaponSystem Instance { get; private set; }

        public readonly VRRig Rig;
        public readonly SoundVoices Sounds;
        public readonly BodyZones Zones;
        private readonly ThrowableHandler _throwables;
        internal ThrowableHandler Throwables => _throwables;
        private readonly MeleeHandler _melee;
        private readonly BodyGrab _bodyGrab; // a soldier held in an empty hand (dragged, swung, thrown)
        private readonly SoldierGuns _soldierGuns; // a hand on a soldier's gun (knocked aside, its magazine pulled, wrested)
        public readonly WeaponDrops Drops;
        private readonly PouchDisplay _pouch;

        /// <summary>Self-test: the pouch as shown (where its magazine stands).</summary>
        internal PouchDisplay Pouch => _pouch;
        private readonly HolsterDisplay _holsters;
        private readonly MagReadout _readout;
        private readonly Dictionary<Weapon, AmmoState> _ammo = new Dictionary<Weapon, AmmoState>();
        private readonly Dictionary<Weapon, WeaponRig> _rigs = new Dictionary<Weapon, WeaponRig>();

        public HeldWeapon Held { get; private set; }
        private float _lastDryFire;

        public WeaponSystem(VRRig rig)
        {
            Instance = this;
            Rig = rig;
            try
            {
                Sounds = new SoundVoices();
                try
                {
                    _lensFlash = new LensFlash(rig);
                }
                catch (System.Exception e)
                {
                    Log.Exception("Muzzle lens flash could not be set up; playing without it", e);
                }
                Zones = new BodyZones(rig);
                _throwables = new ThrowableHandler(this);
                _melee = new MeleeHandler(this);
                _bodyGrab = new BodyGrab(rig);
                _soldierGuns = new SoldierGuns(this, rig);
                Drops = new WeaponDrops(this);
                _pouch = new PouchDisplay(this);
                _holsters = new HolsterDisplay(this);
                _readout = new MagReadout(this);
                if (Core.AutoTest.Requested)
                {
                    Camera.onPreCull += CountFlatFrames;
                }
            }
            catch
            {
                // Built part way: what was made goes, and the instance with it. VRManager shuts VR down on the throw with no
                // weapon system to dispose (its field was never set), so the parts made so far stayed, and WeaponSystem.Instance
                // stayed this half-built one for the patches in the flat game (bug hunt, 2026-10-22).
                Log.Guard("half-built weapon system", Dispose);
                throw;
            }
        }

        /// <summary>
        /// Self-test: frames the headset drew with the player's weapon not yet handled by the mod (where the flat game
        /// holds it) or with the weapon's own arms showing (only the gloves should).
        /// </summary>
        internal static int UnhandledFrames, ArmFrames;

        private void CountFlatFrames(Camera cam)
        {
            if (cam != Rig.Camera)
            {
                return;
            }
            Weapon w = GameContext.HeldPersonalWeapon;
            if (w == null || GameContext.GameMenuOpen)
            {
                return;
            }
            if (Held == null || Held.Weapon != w)
            {
                if (!_attachFailed.Contains(w))
                {
                    UnhandledFrames++;
                }
                return;
            }
            if (Held.ArmsShownByMod)
            {
                return; // the weapon's own arms are the chosen hands
            }
            foreach (Renderer r in Held.Rig.ArmRenderers)
            {
                if (r != null && r.enabled && r.gameObject.activeInHierarchy)
                {
                    ArmFrames++;
                    return;
                }
            }
        }

        // ---------------------------------------------------------------------------------------------

        public void Update()
        {
            Zones.Update();
            MagazineProp.Cull();
            Casings.Cull();
            GrenadeDud.Cull();
            VRGameInput.SuppressFire = false;
            // Set again below while the support hand's trigger fires an under-barrel launcher, or is the gun's.
            _supportFireLast = VRGameInput.SupportFire;
            VRGameInput.SupportFire = false;
            VRGameInput.SupportTriggerOwned = false;
            VRGameInput.SupportTriggerLightsOnly = false;

            Weapon weapon = GameContext.HeldPersonalWeapon;
            if (Held != null && (weapon != Held.Weapon || Held.Weapon == null))
            {
                ReleaseHeld();
            }
            if (Held == null && weapon != null && weapon.gameObject.activeInHierarchy)
            {
                AttachHeld(weapon);
            }
            if (Held == null)
            {
                // Nothing taken up again (no weapon drawn, or its set-up failed): a gun held where a hand took it is not held so later
                // (the same gun drawn again much later would have come back off the hand by the old offset).
                _passHold = null;
                _passEaseAt = -1f;
                _regripSupport = null;
            }
            if (_regripSupport != null)
            {
                // The hand that held it where it took it keeps the handguard once the gun is taken up again by its grip.
                if (Held != null && Held.Weapon == _passWeapon && _regripSupport == Rig.Off)
                {
                    Held.Reload.KeepHandguard(_regripSupport);
                }
                _regripSupport = Held != null ? null : _regripSupport;
            }
            RegripPassedWeapon();
            HandLoading.Update(this); // (a magazine's trigger before the gun's: the gun hand's own is never taken)

            if (Held != null)
            {
                // Caught here: thrown, it skipped everything below every frame (the free hand's weapon, the drops, the holsters,
                // the grip to hold), and the log said so once (lifecycle hunt, 2026-10-21).
                try
                {
                    Held.Update();
                    if (Held != null && Held.Rig.Data.Action == ActionType.Throwable)
                    {
                        _throwables.Update(Held);
                    }
                    else if (Held != null && Held.Weapon is MeleeWeapon)
                    {
                        // A blade hits what it is swung through (MeleeHandler), not on the trigger: the game's own attack on
                        // the trigger struck along the head's facing, a hit without a swing, and with a swing a second hit.
                        VRGameInput.SuppressFire = true;
                    }
                    UpdateSubWeaponReloadTarget();
                    SayOverheated();
                }
                catch (Exception e)
                {
                    Log.ExceptionOnce("Held.Update", e);
                }
            }
            // Caught as the held gun's is: thrown (a game call in the holster or the hand's model gone under it), it skipped the hand
            // roles, the drops, the holsters and the body grabs every frame after it (round-2 scout, 2026-10-27).
            try
            {
                UpdateSecond();
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("UpdateSecond", e);
            }
            HealHandRoles();
            if (GameContext.PlayerActor == null || GameContext.PlayerActor.dead)
            {
                _throwables.ForgetPulledPins(); // a new life's grenades come with their pins in
                // A dead hand lets go. Releasing the gun keeps its magazine in the hand on purpose (a weapon switch), so one stayed in the
                // hand through the death screen into the next life, letting rounds out on the trigger (lifecycle scout, 2026-11-05).
                foreach (VRHand h in Rig.Hands)
                {
                    if (h.Held is MagazineProp prop && prop.Go != null)
                    {
                        prop.Drop(h.Velocity, Vector3.zero);
                    }
                    HandLoading.LetGoOfRound(h);
                }
            }
            // No weapon out at all (holstered, dropped, died): the buttons go back to the dominant hand's layout. A weapon drawn
            // with the other hand swapped them, and they stayed swapped after it was put away: the map and squad buttons answered
            // on the wrong hand (round 9 hunt). (Not while one is passed between hands: it stays the active weapon meanwhile.)
            if (VRInput.GunHandSwapped && Held == null && (GameContext.PlayerActor == null || GameContext.PlayerActor.activeWeapon == null))
            {
                VRInput.SetGunHand(VRInput.Dominant);
            }
            SyncDrawnSlot(GameContext.PlayerActor);
            MagazineProp.ReleasePassedHulls();
            UpdateLooseProps();
            Drops.Update();
            FireSelector.Update();
            UpdateDrivingWeapons();
            UpdateHolsters();
            UpdateGripToHold();
            // Last: every other grab (a gun, a magazine, a holster, a drop) has had the press first. A soldier's gun before the
            // soldier: a squeeze on it takes the gun or its magazine, not him.
            try
            {
                _soldierGuns?.Update();
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("SoldierGuns.Update", e);
            }
            try
            {
                _bodyGrab?.Update();
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("BodyGrab.Update", e);
            }
        }

        /// <summary>The second weapon in the free hand (null: none), beside the game's active one.</summary>
        internal SecondWeapon Second { get; private set; }

        private void UpdateSecond()
        {
            if (Second == null)
            {
                return;
            }
            // The free hand's trigger is the second weapon's while it holds one, on the frames below that return before its update
            // too (a tracking gap, a hand-over): else from the second untracked frame on, the held trigger was the game's Use.
            VRGameInput.SupportTriggerOwned = true;
            VRGameInput.SupportTriggerLightsOnly = false;
            Actor actor = GameContext.PlayerActor;
            if (Held != null && Second.Weapon != null && Second.Weapon == Held.Weapon)
            {
                // The game drew the other hand's weapon (its number key): it is the gun hand's now. Only this hand lets go: put
                // away as well, it was holstered under the gun hand and vanished from both (review, 2026-10-20).
                SecondWeapon handed = Second;
                Second = null; // (first: one that throws in its hand-over is not handed over again every frame)
                handed.HandOver();
                return;
            }
            // (And a pairing no longer allowed after a switch of the gun hand's weapon: a knife drawn beside a pistol, two grenades;
            // checked only as the free hand took it, the pair stayed; round 11.)
            bool gone = Second.Weapon == null || actor == null || actor.dead || actor.IsSeated() || Held == null || Held.Weapon == null
                        || System.Array.IndexOf(actor.weapons, Second.Weapon) < 0 || !SecondWeapon.CanPair(Held.Weapon, Second.Weapon);
            if (gone || Second.Done)
            {
                // The gun in the other hand went away (a seat, death), or a grenade thrown: back into its holster.
                SecondWeapon away = Second;
                Second = null;
                away.PutAway();
                return;
            }
            if (!Second.Hand.Tracked)
            {
                // A tracking gap (the hand behind the back, out of the sensors' view): kept for half a second, as the gun hand keeps
                // its gun, and not worked meanwhile. Put away on the first untracked frame, a glance away holstered it and it had to
                // be taken again (gameplay workflow, 2026-10-24). After that it goes back to its holster, not let go: a grenade is
                // not thrown on a lost hand's last motion.
                if (_secondUntrackedAt < 0f)
                {
                    _secondUntrackedAt = Time.unscaledTime;
                }
                if (Time.unscaledTime - _secondUntrackedAt > 0.5f)
                {
                    SecondWeapon away = Second;
                    Second = null;
                    away.PutAway();
                }
                return;
            }
            _secondUntrackedAt = -1f;
            // With Grip to hold off the free hand keeps its weapon as the gun hand does, until it is put back at its holster
            // (UpdateHolster); it went away the moment the grip opened (round 11).
            if (!Second.Hand.GrabHeld && VRConfig.GripToHoldWeapon.Value && !GameContext.GameMenuOpen && !GameManager.IsPaused()) // (as the gun hand's: a grip let go in a pause holstered it in the frozen match)
            {
                // Let go: back into its holster (a grenade let go in a throwing motion is thrown first).
                SecondWeapon let = Second;
                Second = null;
                let.LetGo();
                return;
            }
            Second.Update();
        }

        private float _secondUntrackedAt = -1f; // since when the second weapon's hand has had no tracking (-1: tracked)

        /// <summary>The free hand takes this holstered weapon as a second one beside the gun in the other hand.</summary>
        internal void BeginSecond(Weapon weapon, VRHand hand)
        {
            Second = new SecondWeapon(this, weapon, RigFor(weapon), StateFor(weapon, create: true), hand);
            _secondUntrackedAt = -1f;
            Sounds.Play(MechSound.Holster, hand.Palm.position, 0.7f);
            hand.Haptic(0.05f, 100f, 0.6f);
        }

        /// <summary>
        /// Only the dominant hand holds the drawn weapon or grenade. A hand still marked so without one stayed busy for
        /// good (no magazine, rack or support grip): the dominant side changed while a weapon was held (the left-handed
        /// setting, the self-test's left-handed pass) and the release freed the new dominant hand, not the one holding it.
        /// </summary>
        private void HealHandRoles()
        {
            foreach (VRHand h in Rig.Hands)
            {
                if (Second != null && h == Second.Hand)
                {
                    continue; // it holds the second weapon
                }
                if ((h.Role == HandRole.Weapon || h.Role == HandRole.Throwable) && (h != Rig.Dominant || Held == null))
                {
                    h.Role = HandRole.Free;
                }
                // On the handguard, a bolt, a hinge, a belt, an optic or a magnifier, with no weapon drawn: left over from a release that
                // did not finish (an exception part way), and the hand took nothing again.
                else if (Held == null && (h.Role == HandRole.Foregrip || h.Role == HandRole.Slide || h.Role == HandRole.Hinge
                                          || h.Role == HandRole.Belt || h.Role == HandRole.Optic || h.Role == HandRole.Magnifier))
                {
                    h.Role = HandRole.Free;
                    if (!(h.Held is MagazineProp))
                    {
                        h.Held = null;
                    }
                }
                // The seated hold left on a hand with no gun (a release cut short): nothing frees it otherwise.
                else if (Held == null && ReferenceEquals(h.Held, ReloadInteractions.SeatedHold))
                {
                    h.Role = HandRole.Free;
                    h.Held = null;
                }
            }
        }

        /// <summary>
        /// A magazine, shell or rocket let go of at the pouch by a hand no drawn gun manages (the gun put away, as when magazines are
        /// loaded by hand): stowed as the drawn gun's reload stows one (ReloadInteractions.Stows), its rounds into the reserve of the
        /// carried weapon it belongs to. One of a weapon not carried is dropped as before. Let go there with the gun away, every
        /// magazine fell to the ground.
        /// </summary>
        private bool StowAtPouch(VRHand h, MagazineProp prop)
        {
            if (!Zones.InPouch(h.Palm.position) || h.BodyThrowVelocity().magnitude >= 1.2f)
            {
                return false;
            }
            AmmoState owner = LiveOwner(prop);
            if (owner == null)
            {
                return false;
            }
            ReturnRounds(prop, prop.Rounds, owner);
            prop.Rounds = 0;
            prop.Destroy(); // (frees the hand)
            Sounds.Play(MechSound.Pouch, h.Palm.position, 0.5f);
            h.Haptic(0.02f, 120f, 0.3f);
            return true;
        }

        /// <summary>
        /// A free hand picks up any magazine, shell or rocket lying within reach, whatever weapon it belongs to (the
        /// held weapon's reload logic decides for the off hand while a weapon is drawn).
        /// </summary>
        private void UpdateLooseProps()
        {
            // (Nor dead: a dud beside a hand still squeezing was refunded to the dead soldier's weapon, with the pouch's click and buzz;
            // holsters scout. In a seat the free hand still picks things up, as it reloads a gun drawn there.)
            Actor player = GameContext.PlayerActor;
            if (GameContext.GameMenuOpen || GameManager.IsPaused() || player == null || player.dead) // (a dud refunded, a magazine taken in the paused match)
            {
                return;
            }
            // A drawn gun's reload logic owns the off hand; a grenade in hand leaves it free for picking things up.
            bool offManaged = Held != null && Held.Rig.Data.Action != ActionType.Throwable;
            foreach (VRHand h in Rig.Hands)
            {
                if (!h.Tracked || h.Role != HandRole.Free || !(h.GrabPressed || h.GrabReaching) || offManaged && h == Rig.Off)
                {
                    continue;
                }
                Vector3 palm = h.Palm.position;
                if (HandLoading.RoundFromPouch(this, h) || HandLoading.MagazineFromPouch(this, h, _lastHeldWeapon))
                {
                    VRInput.Consume(DigitalAction.Grab, h.Side);
                    continue;
                }
                // (By the pouch an empty one is passed over, as the gun's own reload does: a squeeze there wants a full one.)
                bool byPouch = Vector3.Distance(palm, Zones.Pouch) < BodyZones.PouchRadius + 0.10f;
                MagazineProp prop = MagazineProp.NearestDropped(palm, 0.13f, null, byPouch ? (System.Func<MagazineProp, bool>)(p => p.Rounds > 0) : null);
                GrenadeDud dud = GrenadeDud.Nearest(palm, 0.12f);
                if (dud != null && (prop == null || dud.Distance(palm) < prop.DistanceTo(palm)))
                {
                    VRInput.Consume(DigitalAction.Grab, h.Side);
                    if (dud.Refund(GameContext.PlayerActor))
                    {
                        Sounds.Play(MechSound.Pouch, palm, 0.6f);
                        h.Haptic(0.03f, 140f, 0.45f);
                    }
                    else
                    {
                        FlashMessage("FULL");
                        h.Haptic(0.12f, 35f, 0.6f);
                    }
                    continue;
                }
                if (prop == null)
                {
                    if (HandLoading.TakeLying(h)) // (loaded by hand: a case or round on the ground)
                    {
                        VRInput.Consume(DigitalAction.Grab, h.Side);
                    }
                    continue;
                }
                // (Only where the gun would be taken: in a seat or with the draw refused the squeeze was left to a pickup that never came.)
                Actor taker = GameContext.PlayerActor;
                if (Drops != null && GameContext.PlayerOnFoot && taker != null && taker.CanUnholsterWeapon() && Drops.GunNearer(palm, prop.DistanceTo(palm)))
                {
                    continue; // a dropped gun nearer the hand than this magazine: it took the magazine lying by it, the gun left on the ground (grip scout, 2026-11-06)
                }
                VRInput.Consume(DigitalAction.Grab, h.Side);
                prop.Attach(h);
                prop.FollowHand();
                h.Haptic(0.03f, 150f, 0.5f);
            }
        }

        public void LateUpdate()
        {
            List<Weapon> dead = null;
            foreach (KeyValuePair<Weapon, AmmoState> kv in _ammo)
            {
                if (kv.Key == null)
                {
                    (dead ??= new List<Weapon>()).Add(kv.Key);
                    continue;
                }
                if (kv.Key.gameObject.activeInHierarchy)
                {
                    kv.Value.Reconcile();
                }
            }
            if (dead != null)
            {
                foreach (Weapon w in dead)
                {
                    _ammo.Remove(w);
                    _rigs.Remove(w);
                }
            }
            // Rigs with no ammo state (a soldier's gun a hand took the magazine of, a sub-weapon looked at) were dropped only at the
            // scene change: their weapons, gone with their soldiers, stayed as keys for the whole match. Every few seconds.
            if (Time.unscaledTime >= _nextRigPrune)
            {
                _nextRigPrune = Time.unscaledTime + 5f;
                _goneRigs.Clear();
                foreach (Weapon w in _rigs.Keys)
                {
                    if (w == null)
                    {
                        _goneRigs.Add(w);
                    }
                }
                foreach (Weapon w in _goneRigs)
                {
                    _rigs.Remove(w);
                }
                _goneRigs.Clear();
            }
        }

        private float _nextRigPrune;
        private readonly List<Weapon> _goneRigs = new List<Weapon>();

        public void BeforeRender()
        {
            // A weapon drawn after this frame's Update (the game switches weapons in its own Update or LateUpdate) is
            // taken over now, before it is drawn: otherwise it showed for a frame where the flat game holds it, in the
            // weapon's own arms.
            Weapon now = GameContext.HeldPersonalWeapon;
            if (Held != null && (now != Held.Weapon || Held.Weapon == null))
            {
                ReleaseHeld();
            }
            if (Held == null && now != null && now.gameObject.activeInHierarchy)
            {
                bool timed = PerfProbe.Part("render: weapon taken over");
                AttachHeld(now);
                Held?.Update();
                if (timed)
                {
                    PerfProbe.End("render: weapon taken over");
                }
            }
            Zones.ComputeSpots(); // from this frame's head pose: what hangs on the body must not trail the head
            // Parts of this step timed on their own in a profiled run: a 300 ms call in it came with no part to name (2026-10-21).
            bool part = PerfProbe.Part("render: pouch and holsters");
            // Each caught on its own (as the empty arms below): thrown, the held gun, the magazines in the hands and the readout
            // were skipped for that frame, and every frame while it kept throwing (second-weapon hunt).
            try
            {
                _pouch.BeforeRender(Held != null && Held.Weapon != null ? Held : null, _lastFedWeapon);
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("Pouch.BeforeRender", e);
            }
            try
            {
                _holsters.BeforeRender();
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("Holsters.BeforeRender", e);
            }
            if (part)
            {
                PerfProbe.End("render: pouch and holsters");
            }
            // Weapon's arms on empty hands, before the held weapon's step: a gun drawn this frame hides the gloves again after it.
            try
            {
                _emptyArms.BeforeRender(this, VRConfig.HandsNow == HandsMode.WeaponArms && (Held == null || Held.Weapon == null) && GameContext.InGame);
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("EmptyArms.BeforeRender", e);
            }
            try
            {
                Second?.BeforeRender();
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("Second.BeforeRender", e);
            }
            if (Held != null && Held.Weapon != null)
            {
                part = PerfProbe.Part("render: held weapon");
                Held.BeforeRender();
                if (part)
                {
                    PerfProbe.End("render: held weapon");
                }
                if (Held.Rig.Data.Action == ActionType.Melee)
                {
                    _melee.BeforeRender(Held);
                }
                else
                {
                    _melee.Bash(Held); // a gun swung into someone hits them (user, 2026-10-16)
                }
            }
            // After the held grenade is placed this frame: the fuse ring set from its gun body before that sat where the grenade was
            // drawn the frame before (a frame behind it as it moved; round-5 scout).
            _throwables.BeforeRender();
            _melee.HandBlows(Held != null && Held.Weapon != null ? Held : null); // and a magazine or grenade in a hand (task 306)
            foreach (VRHand h in Rig.Hands)
            {
                if (h.Held is MagazineProp prop && (Held == null || h != Rig.Off))
                {
                    if (prop.Go == null)
                    {
                        prop.Destroy(); // gone with its scene: free the hand
                        continue;
                    }
                    prop.FollowHand();
                    if (!h.GrabHeld && !StowAtPouch(h, prop))
                    {
                        prop.Drop(h.ThrowVelocity(), h.AngularVelocity);
                    }
                }
            }
            HandLoading.BeforeRender(this); // a round in a hand, after the magazine it may go into has its pose
            _readout.BeforeRender(); // after every magazine has its pose for this frame
            _lensFlash?.BeforeRender(); // after the gun is placed: the flash sits at its muzzle
        }

        private readonly LensFlash _lensFlash;

        /// <summary>Weapon's arms with no weapon in the hand: the last held gun's arms on both hands.</summary>
        private readonly EmptyArms _emptyArms = new EmptyArms();

        /// <summary>Self-test: the empty-hand arms.</summary>
        internal EmptyArms EmptyHandArms => _emptyArms;

        // ---------------------------------------------------------------------------------------------

        // Weapons whose VR handling could not be set up: not retried every frame (the game keeps handling them).
        private readonly HashSet<Weapon> _attachFailed = new HashSet<Weapon>();

        private void AttachHeld(Weapon weapon)
        {
            if (_attachFailed.Contains(weapon))
            {
                return;
            }
            // (Timed on its own in a profiled run: a new weapon object, the drawn one after a respawn, builds its rig here.)
            bool timed = PerfProbe.Part("draw: rig");
            WeaponRig rig = RigFor(weapon);
            if (timed)
            {
                PerfProbe.End("draw: rig");
            }
            try
            {
                AmmoState state = StateFor(weapon, create: true);
                Held = new HeldWeapon(this, weapon, rig, state);
                if (Log.DebugOn)
                {
                    Log.Debug($"Holding {weapon.name} ({rig.Data.Action})"); // (built only when logged: every draw before)
                }
            }
            catch (Exception e)
            {
                Held = null;
                // The constructor may have created the physics stand-in before the step that threw: destroy it, or it stays
                // DontDestroyOnLoad at the world origin with Held null and nothing ever moves or clears it.
                if (GunCollider.Current != null && GunCollider.Current.Holds(rig))
                {
                    GunCollider.Current.Destroy();
                }
                _attachFailed.Add(weapon);
                _attachFailed.RemoveWhere(w => w == null);
                Log.Exception($"Setting up {weapon.name} for VR (it keeps its normal behaviour)", e);
            }
        }

        /// <summary>The box-fed gun last held in the hand (put away since, or dropped): HandLoading.MagazineFromPouch.</summary>
        private Weapon _lastHeldWeapon;
        /// <summary>The gun last held that feeds from the pouch, of any kind (put away since, or dropped): PouchDisplay.</summary>
        private Weapon _lastFedWeapon;

        private void ReleaseHeld()
        {
            _passHold = null;
            _passEaseAt = -1f;
            if (Held != null)
            {
                AmmoState ammo = Held.Ammo;
                if (ammo != null && ammo.Weapon != null && ammo.UsesDetachableMag && !ammo.Rig.IsMachineGun && !ammo.Rig.EnBloc)
                {
                    _lastHeldWeapon = Held.Weapon; // (its magazines from the pouch while it is away, loaded by hand; not a grenade thrown since)
                }
                if (ammo != null && ammo.Weapon != null && ammo.Participates && ammo.Rig.Action != ActionType.Throwable)
                {
                    _lastFedWeapon = Held.Weapon; // (the pouch goes on showing what it feeds from while it is away)
                }
                Log.Guard("EmptyArms.TakeFrom", () => _emptyArms.TakeFrom(Held)); // its arms for the empty hands, in this frame's pose
                Log.Guard("ReleaseHeld", () => Held.Release()); // a seat's drawn weapon with no held rig called it on nothing (lifecycle hunt)
            }
            _throwables.Release();
            _melee.Release();
            Held = null;
        }

        /// <summary>
        /// Under-barrel launchers and other sub weapons share the parent model but have their own ammo;
        /// the reload logic then targets the active sub weapon.
        /// </summary>
        private void UpdateSubWeaponReloadTarget()
        {
            Weapon active = Held.Weapon.GetActiveSubWeapon();
            if (active != null && active != Held.Weapon)
            {
                StateFor(active, create: true);
            }
        }

        public WeaponRig RigFor(Weapon weapon)
        {
            if (!_rigs.TryGetValue(weapon, out WeaponRig rig))
            {
                rig = new WeaponRig(weapon, WeaponAnalyzer.Get(weapon));
                _rigs[weapon] = rig;
            }
            return rig;
        }

        /// <summary>
        /// The reserve a magazine's rounds go back to: its own weapon while the player still carries it, else a weapon of
        /// the same kind the player carries. Null when neither: the weapon it came out of was let fall since (the game
        /// destroys a dropped weapon, and its reserve with it: a magazine kept in the other hand lost its rounds there).
        /// </summary>
        public AmmoState LiveOwner(MagazineProp prop)
        {
            AmmoState owner = prop.Owner;
            if (owner != null && owner.Weapon != null && owner.Weapon.UserIsPlayer())
            {
                return owner;
            }
            return LiveOwnerOfKind(prop.WeaponKey);
        }

        /// <summary>A weapon of the kind <paramref name="key"/> names (its analysis key) the player carries: its reserve, or null.</summary>
        private AmmoState LiveOwnerOfKind(string key)
        {
            Actor actor = GameContext.PlayerActor;
            if (actor == null || actor.weapons == null)
            {
                return null;
            }
            foreach (Weapon w in actor.weapons)
            {
                // (Its analysis's key, not a rig made for it: asked here, a rig was built and kept for every weapon only looked at.)
                if (w != null && w.UserIsPlayer() && WeaponAnalyzer.Get(w).Key == key)
                {
                    return StateFor(w, create: true);
                }
            }
            return null;
        }

        /// <summary>
        /// A magazine's rounds back into a reserve (up to its limit): <paramref name="into"/> when given, else the live owner,
        /// else the player's own dropped weapon of that kind (they come back with it).
        /// </summary>
        public void ReturnRounds(MagazineProp prop, int rounds, AmmoState into = null)
        {
            if (rounds <= 0)
            {
                return;
            }
            into = into ?? LiveOwner(prop);
            if (into != null)
            {
                into.ReturnSpareCapped(rounds);
                return;
            }
            Drops?.ReturnRoundsToOwnDrop(prop.WeaponKey, rounds);
        }

        /// <summary>
        /// Rounds of a weapon that is gone (destroyed in the hand) back into a reserve of its kind: a weapon of that kind the player
        /// carries, else the player's own dropped one. Its own reserve went with it.
        /// </summary>
        internal void ReturnRoundsOfKind(string key, int rounds)
        {
            if (rounds <= 0 || key == null)
            {
                return;
            }
            AmmoState into = LiveOwnerOfKind(key);
            if (into != null)
            {
                into.ReturnSpareCapped(rounds);
                return;
            }
            Drops?.ReturnRoundsToOwnDrop(key, rounds);
        }

        public AmmoState StateFor(Weapon weapon, bool create)
        {
            if (weapon == null)
            {
                return null;
            }
            if (_ammo.TryGetValue(weapon, out AmmoState s))
            {
                return s;
            }
            if (!create || !weapon.UserIsPlayer())
            {
                return null;
            }
            s = new AmmoState(weapon, RigFor(weapon).Data);
            _ammo[weapon] = s;
            return s;
        }

        /// <summary>
        /// The weapon is <paramref name="owner"/> or one of its fire-mode variants, by a live owner only: a destroyed owner reads
        /// as null to Unity, and a main gun's shot (its parent null) went to a second weapon destroyed in a swap (scout,
        /// 2026-10-22).
        /// </summary>
        private static bool Fires(Weapon weapon, Weapon owner) =>
            owner != null && (weapon == owner || !ReferenceEquals(weapon.parentWeapon, null) && weapon.parentWeapon == owner);

        public void OnPlayerShot(Weapon weapon)
        {
            AmmoState state = StateFor(weapon, create: true);
            state?.OnShot();
            FireSelector.OnShot(weapon);
            if (Second != null && Fires(weapon, Second.Weapon))
            {
                Second.OnShot();
                _lensFlash?.OnShot(Second.Rig, weapon); // its lens flash too (none came: bug hunt 2026-10-19, task 287)
                return;
            }
            bool held = Held != null && Fires(weapon, Held.Weapon);
            if (held && state != null && state.Rig.CyclesByHand && VRConfig.ReloadNow == ReloadMode.Manual)
            {
                // Bolt / pump / lever: the fire animation would cycle the action (and play its sounds) by itself.
                // Cancel it so the action only moves, and only sounds, when the player works it; keep the
                // mod's gunshot sounds that were tied to that animation.
                Animator anim = weapon.animator;
                if (anim != null && anim.isActiveAndEnabled)
                {
                    anim.ResetTrigger(Weapon.FIRE_PARAMETER_HASH);
                }
                Held.Sounds.PlayShotCues();
            }
            if (held)
            {
                Held.OnShot(weapon);
                Rig.Dominant.Model?.TriggerShot();
                _lensFlash?.OnShot(Held, weapon);
            }
        }

        /// <summary>
        /// A trigger pulled on a gun too hot to fire: the game's Fire returns before anything is asked, so the pull did nothing at all, and the
        /// flat game's heat bar is no part of VR.
        /// </summary>
        private void SayOverheated()
        {
            Weapon w = Held?.ActiveWeapon; // (an under-barrel gun's heat is its own)
            if (w != null && w.isOverheating && VRInput.GetPressed(DigitalAction.Fire, VRInput.GunHand) && !VRGameInput.GunInputBlocked)
            {
                FlashMessage("OVERHEATED");
                Log.InfoOnce("overheated:" + w.name, $"{w.name}: trigger pulled while overheated.");
                Rig.Dominant.Haptic(0.01f, 250f, 0.25f);
            }
        }

        /// <summary>The support hand's trigger fired the launcher last frame (for the dry-fire click's edge).</summary>
        private bool _supportFireLast;

        public void OnDryFire(Weapon weapon)
        {
            // The second weapon's own trigger (the free hand's Use): an empty pull there gave no click at all (second-weapon hunt,
            // 2026-10-20 night).
            if (Second != null && weapon != null && Fires(weapon, Second.Weapon)) // (its fire-mode variant too: it clicked in the gun hand)
            {
                if (VRInput.GetPressed(DigitalAction.Use, Second.Hand.Side) && Time.unscaledTime - _lastDryFire > 0.15f)
                {
                    _lastDryFire = Time.unscaledTime;
                    Sounds.Play(MechSound.DryFire, Second.Hand.Palm.position, 0.8f, 0f, Second.Rig?.Data); // (the gun's own click where it has one)
                    Second.Hand.Haptic(0.01f, 250f, 0.25f);
                    Second.Hand.Model?.TriggerShot();
                }
                return;
            }
            // CanFire is polled every frame while the trigger is held; click once per trigger pull.
            // (Or the support hand's trigger just pulled on an under-barrel launcher: it never clicked; 255.B5-4.)
            if ((VRInput.GetPressed(DigitalAction.Fire, VRInput.GunHand) || VRGameInput.SupportFire && !_supportFireLast) && Time.unscaledTime - _lastDryFire > 0.15f)
            {
                _lastDryFire = Time.unscaledTime;
                if (Held != null)
                {
                    Held.Sounds.Play(MechSound.DryFire, Rig.Dominant.Palm.position, 0.8f); // (the gun's own click where it has one)
                }
                else
                {
                    Sounds.Play(MechSound.DryFire, Rig.Dominant.Palm.position, 0.8f);
                }
                Rig.Dominant.Haptic(0.01f, 250f, 0.25f);
                // The trigger was pulled on an empty gun: the finger presses as on a shot (a controller that reports the
                // trigger only as a click showed no pull at all on the click).
                Rig.Dominant.Model?.TriggerShot();
            }
        }

        /// <summary>Autotest: the grenade pin and dud checks for the throwable in hand.</summary>
        internal System.Collections.IEnumerator ThrowableSelfCheck(HeldWeapon held, Action<string> report) => _throwables.SelfCheck(held, report);

        public void FlashMessage(string message)
        {
            Held?.FlashCounter(message);
        }

        /// <summary>
        /// Fire-mode button on foot. In order: the weapon's own fire-mode script (it switches with its own animation
        /// and sound when it sees its key), the mod's sub-weapons (the game cycles them), then our semi/auto
        /// selector for automatic weapons. Returns false to hand the press to the game.
        /// </summary>
        public bool HandleFireModePress(Weapon weapon)
        {
            LastPressWasSelector = false;
            if (weapon == null)
            {
                return false;
            }
            ScriptKey scripted = ScriptKeys.Find(weapon, ScriptKeyPurpose.FireMode);
            if (scripted != null)
            {
                ScriptKeys.Tap(scripted.Key, scripted.Script);
                Rig.Dominant.Haptic(0.02f, 200f, 0.5f);
                return true;
            }
            if (weapon.HasAnySubWeapons())
            {
                return false;
            }
            if (!VRConfig.FireSelector.Value)
            {
                return false; // the semi / auto selector is off: the game's own fire-mode bind
            }
            string mode = FireSelector.Toggle(weapon);
            if (mode == null)
            {
                FlashMessage("SEMI ONLY");
                Rig.Dominant.Haptic(0.02f, 60f, 0.3f);
                return false; // the game still gets the press: its own fire-mode uses (a Katana sheathes with it; 255.B5-2)
            }
            FlashMessage(mode);
            // The selector's own short click, not the hammer falling on an empty chamber (85-agent review). The gun's own where it has one.
            if (Held != null)
            {
                Held.Sounds.Play(MechSound.Selector, Rig.Dominant.Palm.position, 0.4f);
            }
            else
            {
                Sounds.Play(MechSound.Selector, Rig.Dominant.Palm.position, 0.4f);
            }
            Rig.Dominant.Haptic(0.02f, 200f, 0.5f);
            LastPressWasSelector = true;
            return true;
        }

        /// <summary>The last <see cref="HandleFireModePress"/> switched the mod's semi / auto selector (a switch that can be put back).</summary>
        internal bool LastPressWasSelector { get; private set; }

        /// <summary>
        /// Puts back a semi / auto switch a Touch A tap made at once, when that tap was the first of slow motion's double press.
        /// </summary>
        internal void UndoSelectorToggle(Weapon weapon)
        {
            string mode = weapon != null ? FireSelector.Toggle(weapon) : null;
            if (mode != null)
            {
                FlashMessage(mode);
            }
        }

        // ---------------------------------------------------------------------------------------------

        /// <summary>Driving a car: a holster grab draws that weapon (put it back to drive with the horn again).</summary>
        private void UpdateDrivingWeapons()
        {
            Actor actor = GameContext.PlayerActor;
            if (!DrivingWeapons.CanDraw(actor))
            {
                if (DrivingWeapons.Armed)
                {
                    // Still in the seat (the option turned off while driving): the drawn weapon is put away too. With only the flag
                    // cleared, the pistol stayed the active weapon nobody held, the trigger fired it instead of the horn, and no
                    // holster grab could put it away until the player got out (vehicle hunt, 2026-10-20).
                    if (actor != null && !actor.dead && actor.IsSeated() && actor.activeWeapon != null && !actor.activeWeapon.IsMountedWeapon())
                    {
                        DrivingWeapons.PutAway(actor);
                    }
                    else
                    {
                        DrivingWeapons.Reset();
                    }
                }
                return;
            }
            VRHand dom = Rig.Dominant;
            // A grenade drawn in the seat is put back the same way (the hand holding it is "Throwable").
            if (!dom.Tracked || !dom.GrabPressed || dom.Role != HandRole.Free && dom.Role != HandRole.Weapon && dom.Role != HandRole.Throwable
                || GameContext.GameMenuOpen || GameManager.IsPaused())
            {
                return;
            }
            // An empty hand reaching for the rim takes the wheel, not a holster it passes on the way (in some cars the rim sits
            // by the hip and belt holsters, and the press drew the pistol instead of turning the wheel). The rim test runs first in
            // the frame, so a press on the rim has already been taken; this catches the ones just short of it.
            const float wheelReachMargin = 0.06f;
            if (dom.Role == HandRole.Free && Rig.Cockpit.WheelReaches(dom.Palm.position, wheelReachMargin))
            {
                return;
            }
            int slot = Zones.HolsterAt(dom.Palm.position);
            if (slot < 0 || actor.weapons == null || slot >= actor.weapons.Length)
            {
                return;
            }
            Weapon weapon = actor.weapons[slot];
            if (weapon == null)
            {
                dom.Haptic(0.05f, 40f, 0.3f);
                return;
            }
            VRInput.Consume(DigitalAction.Grab, dom.Side);
            if (DrivingWeapons.Armed && actor.activeWeapon == weapon)
            {
                ReleaseHeld();
                DrivingWeapons.PutAway(actor);
                dom.Role = HandRole.Free;
            }
            else if (weapon is ToggleableItem item)
            {
                // A toggled item (night vision) is switched, not drawn into the hand (holster hunt). Itself: the game's SwitchWeapon goes by
                // the seat's weapons in a seat, so this slot's press drew the car's horn and the item never came on (holster scout, 2026-11-04).
                item.Toggle();
            }
            else
            {
                DrivingWeapons.Draw(actor, weapon);
            }
            Sounds.Play(MechSound.Holster, dom.Palm.position, 0.7f);
            dom.Haptic(0.05f, 100f, 0.6f);
        }

        /// <summary>The holster each hand is over (a tick when it arrives at one with a weapon in it).</summary>
        private readonly int[] _hoveredHolster = { -1, -1 };

        /// <summary>A hand is at this holster slot with something there to draw: its copy reacts (the second weapon's own copy is hidden).</summary>
        internal bool HolsterHovered(int slot) => slot >= 0 && (_hoveredHolster[0] == slot || _hoveredHolster[1] == slot);

        /// <summary>
        /// Either hand draws from the body: the weapon comes out in the hand that took it. The hand holding the drawn
        /// weapon puts it back at its own holster or swaps it for another; the free hand takes another weapon into itself
        /// (the drawn one goes to its holster).
        /// </summary>
        private void UpdateHolsters()
        {
            // Nor while the match is paused with no menu (the dashboard, the headset off): a grip on a holster drew or stowed a
            // weapon in the frozen match (vehicles hunt, 2026-10-21).
            if (!VRConfig.HolsterSwitching.Value || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen || GameManager.IsPaused())
            {
                // (And no hover kept: a copy reached as the pause began stayed eased out through it; review, 2026-10-30.)
                _hoveredHolster[0] = _hoveredHolster[1] = -1;
                return;
            }
            Actor actor = GameContext.PlayerActor;
            foreach (VRHand hand in Rig.Hands)
            {
                UpdateHolster(actor, hand);
            }
        }

        /// <summary>
        /// With Grip to hold off, the free hand's second weapon goes back at its own holster: a tick when the hand reaches it, a grip
        /// press there puts it away (as the gun hand puts its weapon back). Nowhere else does the free hand let it go then.
        /// </summary>
        private void PutBackSecond(Actor actor, VRHand hand, int i)
        {
            int own = actor != null && actor.weapons != null ? Array.IndexOf(actor.weapons, Second.Weapon) : -1;
            int slot = own >= 0 ? Zones.HolsterAt(hand.Palm.position, includeChest: Held == null || !Held.Aiming) : -1;
            bool atOwn = slot >= 0 && slot == own;
            if (atOwn && slot != _hoveredHolster[i])
            {
                hand.Haptic(0.012f, 220f, 0.35f);
            }
            _hoveredHolster[i] = atOwn ? slot : -1;
            if (!atOwn || !hand.GrabPressed)
            {
                return;
            }
            VRInput.Consume(DigitalAction.Grab, hand.Side);
            SecondWeapon away = Second;
            Second = null;
            away.PutAway(); // (its own click and buzz: a second pair here played the holster twice)
        }

        private void UpdateHolster(Actor actor, VRHand hand)
        {
            int i = (int)hand.Side;
            bool gunHand = hand == Rig.Dominant;
            // The gun hand may hold its weapon; the free hand only draws while it holds nothing (not the handguard, a bolt,
            // a magazine).
            bool canDraw = gunHand ? hand.Role != HandRole.Magazine : hand.Role == HandRole.Free && hand.Held == null;
            if (!gunHand && hand.Tracked && Second != null && Second.Hand == hand && !VRConfig.GripToHoldWeapon.Value)
            {
                PutBackSecond(actor, hand, i);
                return;
            }
            if (!hand.Tracked || !canDraw)
            {
                _hoveredHolster[i] = -1; // a hand holding a magazine has no room for a weapon
                return;
            }
            // The free hand passes over the drawn weapon's own holster (empty: the weapon is in the other hand) to the
            // nearest one with something in it; it found that spot first and grabbed nothing.
            int skip = !gunHand && actor != null && actor.activeWeapon != null && actor.weapons != null ? Array.IndexOf(actor.weapons, actor.activeWeapon) : -1;
            int slot = Zones.HolsterAt(hand.Palm.position, includeChest: Held == null || !Held.Aiming, skipSlot: skip);
            // A short tick when the hand reaches a holster with something in it: holsters are found by feel.
            bool usable = slot >= 0 && actor != null && actor.weapons != null && slot < actor.weapons.Length && actor.weapons[slot] != null
                          && (gunHand || actor.weapons[slot] != actor.activeWeapon) && !HolsterDisplay.Spent(actor.weapons[slot]);
            // The gun hand gets nothing at the second weapon's holster either: its copy is hidden (HolsterDisplay), so the spot
            // looks empty. A grab there drew that weapon, which put the held one away and then the second one too (2026-10-19).
            if (usable && gunHand && Second != null && actor.weapons[slot] == Second.Weapon)
            {
                usable = false;
            }
            if (usable && slot != _hoveredHolster[i])
            {
                hand.Haptic(0.012f, 220f, 0.35f);
            }
            _hoveredHolster[i] = usable ? slot : -1;
            if (!hand.GrabPressed || slot < 0)
            {
                return;
            }
            if (!usable)
            {
                if (gunHand)
                {
                    hand.Haptic(0.05f, 40f, 0.3f); // nothing there
                }
                return;
            }
            VRInput.Consume(DigitalAction.Grab, hand.Side);
            if (gunHand && actor.activeWeapon == actor.weapons[slot])
            {
                // Putting the weapon back where it lives: holster it and free the hand.
                HolsterToEmptyHand(actor);
                return;
            }
            // (Only where the game draws at all: in deep water nothing is, and the free hand's second weapon was drawn and fired
            // there; holster hunt, 2026-10-21.)
            // (The gun still drawn: the gun hand holstering it this same frame left Held set, and the pair was begun and put away again.)
            if (!gunHand && Second == null && Held != null && Held.Weapon != null && actor.activeWeapon == Held.Weapon && SecondWeapon.CanPair(Held.Weapon, actor.weapons[slot])
                && actor.CanUnholsterWeapon() && !actor.weapons[slot].IsToggleable())
            {
                // A second weapon of another kind (primary, secondary, launcher): held in this hand beside the drawn one
                // (user, 2026-10-16: taking a second weapon made the first disappear).
                BeginSecond(actor.weapons[slot], hand);
                return;
            }
            // What the game will not draw takes no hand: a toggled item (night-vision goggles) only toggles, and in deep water nothing
            // is drawn. Taken into the hand first, the gun hand moved over and the drawn rifle followed it (review, 2026-10-20).
            Weapon wanted = actor.weapons[slot];
            if (wanted != null && wanted.IsToggleable() || !actor.CanUnholsterWeapon())
            {
                actor.SwitchWeapon(slot);
                hand.Haptic(0.03f, 80f, 0.4f);
                return;
            }
            VRHand before = Rig.Dominant;
            TakeIntoHand(hand); // the free hand takes it: the drawn weapon goes to its holster
            actor.SwitchWeapon(slot);
            if (actor.activeWeapon != wanted && before != hand)
            {
                // The game did not draw it: the gun stays in the hand that held it (it was attached to the grabbing hand instead;
                // holster hunt, 2026-10-21).
                TakeIntoHand(before);
                hand.Haptic(0.05f, 40f, 0.3f);
                return;
            }
            Sounds.Play(MechSound.Holster, hand.Palm.position, 0.7f);
            hand.Haptic(0.05f, 100f, 0.6f);
        }

        /// <summary>A grenade thrown by letting go of the grip: the throwable goes back to its holster and the hand is empty.</summary>
        internal void PutAwayAfterThrow()
        {
            Actor actor = GameContext.PlayerActor;
            if (actor == null || actor.activeWeapon == null)
            {
                return;
            }
            if (actor.IsSeated())
            {
                // Drawn in a seat: put away as the seat's holster grab does (the horn back). It stayed in the open hand, and
                // the next let-go threw it again with its pin in (throw hunt, 2026-10-20). In a seat that allows personal weapons of
                // its own (no driving weapons drawn), holstered as on foot (review, 2026-10-20).
                if (actor.activeWeapon.IsMountedWeapon())
                {
                    return;
                }
                if (DrivingWeapons.Armed)
                {
                    DrivingWeapons.PutAway(actor);
                }
                else if (actor.seat != null && actor.seat.CanUsePersonalWeapons())
                {
                    actor.HolsterActiveWeapon();
                }
                else
                {
                    return;
                }
            }
            else
            {
                actor.HolsterActiveWeapon();
            }
            VRHand dom = Rig.Dominant;
            if (dom.Role == HandRole.Weapon || dom.Role == HandRole.Throwable)
            {
                dom.Role = HandRole.Free;
            }
        }

        private void HolsterToEmptyHand(Actor actor)
        {
            if (actor.activeWeapon == null || actor.IsSeated())
            {
                return;
            }
            actor.HolsterActiveWeapon();
            VRHand dom = Rig.Dominant;
            if (dom.Role == HandRole.Weapon || dom.Role == HandRole.Throwable)
            {
                dom.Role = HandRole.Free;
            }
            Sounds.Play(MechSound.Holster, dom.Palm.position, 0.7f);
            dom.Haptic(0.05f, 100f, 0.5f);
        }

        private float _gripLetGoAt = -1f;
        private bool _openedOnThrow;
        private float _gripGrace = 0.12f;
        private Vector3 _letGoVelocity, _letGoAngular, _letGoPalm;

        /// <summary>
        /// GripToHoldWeapon: letting go of the grip puts the weapon away and frees the hand. A loosened grip has a moment
        /// to close again before the weapon goes (a shift of the fingers must not drop it mid-fight).
        /// </summary>
        private void UpdateGripToHold()
        {
            // (Paused: a grip let go as the headset comes off does not drop or holster the gun.)
            if (!VRConfig.GripToHoldWeapon.Value || Held == null || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen || GameManager.IsPaused())
            {
                _gripLetGoAt = -1f;
                _openedOnThrow = false; // (the last grenade gone: left set, the next one drawn with an open hand went straight back)
                return;
            }
            VRHand dom = Rig.Dominant;
            if (!dom.Tracked)
            {
                _gripLetGoAt = -1f; // out of view: never drop on a tracking gap
                return;
            }
            if (Held.Rig.Data.Action == ActionType.Throwable && Time.unscaledTime - _throwables.GripThrowAt < 0.5f)
            {
                _gripLetGoAt = -1f; // that release was a throw
                _openedOnThrow |= dom.GrabReleased;
                return;
            }
            if (_openedOnThrow)
            {
                // Thrown by the trigger and the grip opened just after: the next grenade goes back, as the grip's own throw leaves the hand. Its
                // let-go was spent on the throw, and it stayed in an open palm.
                _openedOnThrow = false;
                if (!dom.GrabHeld && Held.Rig.Data.Action == ActionType.Throwable)
                {
                    PutAwayAfterThrow();
                    return;
                }
            }
            if (dom.GrabReleased)
            {
                _gripLetGoAt = Time.unscaledTime;
                // A longer moment with the other hand at work on the gun, as it was when the grip opened: a grip loosened for a tenth of a second
                // mid-reload dropped the gun, and the magazine on its way in with it.
                HandRole working = Rig.Off.Role;
                _gripGrace = working == HandRole.Magazine || working == HandRole.Slide || working == HandRole.Hinge || working == HandRole.Belt ? 0.3f : 0.12f;
                // The swing as the hand opens: 0.12 s later the hand has stopped and a throw would just drop.
                _letGoVelocity = dom.ThrowVelocity();
                _letGoAngular = dom.AngularVelocity;
                _letGoPalm = dom.Palm.position;
            }
            else if (dom.GrabHeld)
            {
                _gripLetGoAt = -1f;
            }
            if (_gripLetGoAt >= 0f && Time.unscaledTime - _gripLetGoAt > _gripGrace)
            {
                _gripLetGoAt = -1f;
                Actor actor = GameContext.PlayerActor;
                if (actor == null)
                {
                    return;
                }
                if (PassToOtherHand())
                {
                    return;
                }
                // Let go at your body: back in its holster. Anywhere else it falls, like any object you open your hand on.
                if (VRConfig.ReleaseDropsWeapon.Value && !Zones.AtBody(_letGoPalm) && Held.Rig.Data.Action != ActionType.Throwable)
                {
                    LetFall(actor, dom, _letGoVelocity, _letGoAngular);
                }
                else
                {
                    HolsterToEmptyHand(actor);
                }
            }
        }

        /// <summary>
        /// The next weapon the game draws goes into <paramref name="hand"/>: it becomes the gun hand (the weapon drawn now,
        /// in the other hand, is let go of here and holstered by the game's switch).
        /// </summary>
        internal void TakeIntoHand(VRHand hand)
        {
            if (hand == Rig.Dominant)
            {
                return;
            }
            if (Held != null)
            {
                ReleaseHeld();
            }
            VRHand old = Rig.Dominant;
            if (old.Role == HandRole.Weapon || old.Role == HandRole.Throwable)
            {
                old.Role = HandRole.Free;
            }
            VRInput.SetGunHand(hand.Side);
        }

        /// <summary>
        /// The gun hand let go while the other hand still holds the weapon (on its handguard or round the grip): the weapon
        /// stays in that hand, which becomes the gun hand and takes it by the grip. It used to go to its holster.
        /// </summary>
        private bool PassToOtherHand()
        {
            VRHand from = Rig.Dominant, to = Rig.Off;
            if (Held == null || Held.Rig.Data.Action == ActionType.Throwable || !to.Tracked || to.Role != HandRole.Foregrip || !to.GrabHeld)
            {
                return false;
            }
            // Where the gun is now, against the hand that keeps it: it stays there in that hand (user, 2026-11-03: "when you release
            // its still held there and doesnt snap to the trigger ... when you switch hands or release or anything to bring out a
            // pistol"). Taken over by the grip, it jumped by the hands' spacing along the gun.
            RigidPose kept = HeldWeapon.HoldFrame(to, Held.Rig.Data).Inverse().Multiply(HandPose(Held).Multiply(HeldWeapon.GripFrame(Held.Rig.Data)));
            Weapon passed = Held.Weapon;
            ReleaseHeld(); // the support grip and reload state go with it; the weapon is taken up again next frame
            from.Role = HandRole.Free;
            to.Role = HandRole.Free;
            to.Held = null;
            VRInput.SetGunHand(to.Side);
            _passHold = kept;
            _passWeapon = passed;
            to.Haptic(0.04f, 140f, 0.5f);
            Log.InfoOnce("passhand", $"Weapon passed to the {to.Side} hand, held where that hand holds it.");
            return true;
        }

        /// <summary>
        /// A weapon passed to the other hand is held where that hand took it (on the handguard, say), not by its grip: the gun hand's
        /// frame to the grip's then. Eased away over <see cref="PassEase"/> once a hand takes the grip again; cleared when the weapon
        /// is let go of, holstered or switched.
        /// </summary>
        private RigidPose? _passHold;
        private Weapon _passWeapon;
        /// <summary>The hand that held the gun where it took it, until the gun is taken up again by its grip (it keeps the handguard then).</summary>
        private VRHand _regripSupport;
        private float _passEaseAt = -1f;
        private const float PassEase = 0.2f;

        /// <summary>
        /// Where the held gun's grip is against its hand's grip frame (<see cref="HeldWeapon.HoldFrame"/>) while it is held where the
        /// hand took it, or null for the grip in the hand as usual. Eased to the grip over a fifth of a second after a regrip.
        /// </summary>
        internal RigidPose? PassHoldNow(Weapon weapon)
        {
            if (!_passHold.HasValue || weapon == null || weapon != _passWeapon)
            {
                return null;
            }
            if (_passEaseAt < 0f)
            {
                return _passHold;
            }
            float t = (Time.unscaledTime - _passEaseAt) / PassEase;
            if (t >= 1f)
            {
                _passHold = null;
                _passEaseAt = -1f;
                return null;
            }
            float e = t * t * (3f - 2f * t);
            RigidPose from = _passHold.Value;
            return new RigidPose(Vector3.Lerp(from.position, Vector3.zero, e), Quaternion.Slerp(from.rotation, Quaternion.identity, e));
        }

        /// <summary>Whether the held gun is held where a hand took it, not by its grip (<see cref="PassHoldNow"/>): the glove is not put on the grip.</summary>
        internal bool HeldWhereTaken(Weapon weapon) => _passHold.HasValue && _passEaseAt < 0f && weapon != null && weapon == _passWeapon;

        /// <summary>
        /// The free hand squeezing at the grip of a gun held where the other hand took it: the gun goes back into that hand by its
        /// grip, eased there from where it is (the other hand let go of it to it). Before the gun's own update, so the squeeze is
        /// not also taken as a grab of its handguard or a part.
        /// </summary>
        private void RegripPassedWeapon()
        {
            if (!_passHold.HasValue || _passEaseAt >= 0f || Held == null || Held.Weapon != _passWeapon || Held.Rig.Data.Action == ActionType.Throwable)
            {
                return;
            }
            VRHand free = Rig.Off, holding = Rig.Dominant;
            if (!free.Tracked || free.Role != HandRole.Free || !free.GrabPressed
                || Vector3.Distance(free.Palm.position, Held.Rig.ToWorld(Held.Rig.Data.GripPoint)) > RegripReach)
            {
                return;
            }
            WeaponRigData d = Held.Rig.Data;
            RigidPose kept = HeldWeapon.HoldFrame(free, d).Inverse().Multiply(HandPose(Held).Multiply(HeldWeapon.GripFrame(d)));
            Weapon weapon = Held.Weapon;
            VRInput.Consume(DigitalAction.Grab, free.Side);
            ReleaseHeld();
            holding.Role = HandRole.Free;
            holding.Held = null;
            free.Role = HandRole.Free;
            VRInput.SetGunHand(free.Side);
            _passHold = kept;
            _passWeapon = weapon;
            _passEaseAt = Time.unscaledTime;
            _regripSupport = holding;
            free.Haptic(0.04f, 140f, 0.5f);
            Log.InfoOnce("regrip", $"Weapon taken by its grip again in the {free.Side} hand.");
        }

        /// <summary>The gun body where the hand put it last frame (not where a wall held it), or as drawn before it was ever placed.</summary>
        private static RigidPose HandPose(HeldWeapon held) =>
            held.HandGunBody.rotation.x == 0f && held.HandGunBody.rotation.y == 0f && held.HandGunBody.rotation.z == 0f && held.HandGunBody.rotation.w == 0f
                ? held.Rig.GunBodyPose
                : held.HandGunBody;

        /// <summary>
        /// How near (m) the free hand's palm must be to the grip to take a gun held where the other hand took it back by it: on the grip
        /// itself (at 10 cm a squeeze for the magazine in front of an AR's grip took the grip instead).
        /// </summary>
        private const float RegripReach = 0.065f;

        /// <summary>Autotest: passes the held weapon to the other hand as letting go while it holds the handguard does.</summary>
        internal bool TestPassToOtherHand() => PassToOtherHand();

        /// <summary>Autotest: lets the held weapon fall as a released grip would; the slot it came from, or -1.</summary>
        internal int TestLetFall()
        {
            Actor actor = GameContext.PlayerActor;
            if (Held == null || actor == null || actor.weapons == null)
            {
                return -1;
            }
            int slot = Array.IndexOf(actor.weapons, Held.Weapon);
            LetFall(actor, Rig.Dominant, Rig.Dominant.ThrowVelocity(), Rig.Dominant.AngularVelocity);
            return slot;
        }

        /// <summary>The weapon leaves the hand as a physical object where it was held; it returns to its holster later.</summary>
        private void LetFall(Actor actor, VRHand dom, Vector3 velocity, Vector3 angularVelocity)
        {
            HeldWeapon held = Held;
            int slot = actor.weapons != null ? Array.IndexOf(actor.weapons, held.Weapon) : -1;
            if (slot < 0 || actor.IsSeated())
            {
                HolsterToEmptyHand(actor);
                return;
            }
            DroppedWeapon drop = Drops.DropHeld(held, slot, velocity, angularVelocity);
            if (drop == null)
            {
                HolsterToEmptyHand(actor);
                return;
            }
            ReleaseHeld();
            actor.HolsterActiveWeapon();
            actor.DropWeapon(slot);
            if (dom.Role == HandRole.Weapon || dom.Role == HandRole.Throwable)
            {
                dom.Role = HandRole.Free;
            }
            dom.Haptic(0.03f, 90f, 0.4f);
        }

        public void OnSceneChanged()
        {
            // Each on its own: one that threw skipped the rest, the held weapon's release and the ammo and rig clean-up included
            // (sweep, 2026-10-29).
            Log.Guard("Scene: driving weapons", DrivingWeapons.Reset); // (a new level begun in a seat with no gun kept the last one's armed flag)
            Log.Guard("Scene: body grab", () => _bodyGrab?.ReleaseAll());
            Log.Guard("Scene: body impacts", BodyImpacts.Clear);
            SoldierGuns.ForgetUnloaded();
            Log.Guard("Scene: soldier guns", SoldierGuns.Clear);
            Log.Guard("Scene: gun volumes", GunVolume.OnSceneLoaded);
            Log.Guard("Scene: hand bodies", HandBody.OnSceneLoaded);
            Log.Guard("Retired hull meshes", ShapeFit.ReleaseRetired); // (replaced content's, now that the old scene's drops are gone)
            if (Held != null)
            {
                ReleaseHeld();
            }
            if (Second != null)
            {
                // The other hand's weapon too, before the ammo states go: kept over a load, its state was orphaned and a second
                // one made for the same gun (second-weapon hunt, 2026-10-20 night).
                Log.Guard("Second.PutAway", Second.PutAway);
                Second = null;
            }
            MagazineProp.DestroyAll();
            GrenadeDud.DestroyAll();
            Casings.DestroyAll();
            Drops.Clear();
            FireSelector.Clear();
            _ammo.Clear();
            _rigs.Clear();
            _attachFailed.Clear(); // as the analysis's failures: tried again in the new scene (lifecycle hunt, 2026-10-20)
            WeaponAnalyzer.ForgetFailed(); // a weapon whose analysis threw is analysed again in the new scene (2026-10-19)
        }

        /// <summary>
        /// The game's slot number follows the gun really drawn: <c>Actor.EquipNewWeaponEntry</c> (a map's trigger, a mutator, a script) draws one without
        /// setting it, and everything that read the slot after that read the wrong one: the next-weapon button's order, the holster a gun goes back
        /// to, a pickup beside the gun in the hand (pickups scout, 2026-11-08). Only a gun of the player's own slots (a seat's gun is not one).
        /// </summary>
        private static void SyncDrawnSlot(Actor actor)
        {
            if (actor == null || actor.dead || actor.activeWeapon == null || actor.weapons == null)
            {
                return;
            }
            int s = System.Array.IndexOf(actor.weapons, actor.activeWeapon);
            if (s >= 0 && s != actor.activeWeaponSlot)
            {
                Log.InfoOnce("slotsync:" + actor.activeWeapon.name, $"The drawn {actor.activeWeapon.name} is in slot {s + 1}, the game had slot {actor.activeWeaponSlot + 1}: set (a gun drawn by a script or trigger).");
                actor.activeWeaponSlot = s;
            }
        }

        public void Dispose()
        {
            Camera.onPreCull -= CountFlatFrames;
            Log.Guard("HandBody.GiveLayerBack", HandBody.GiveLayerBack); // (its collision row as the map had it, for flat play after VR)
            if (Second != null)
            {
                Log.Guard("Second.PutAway", Second.PutAway); // the other hand's weapon back in its holster too (lifecycle hunt)
                Second = null;
            }
            if (Held != null)
            {
                Log.Guard("Weapons.ReleaseHeld", ReleaseHeld);
            }
            Log.Guard("Weapons.Dispose.Gloves", () => _emptyArms.ReturnGloves(this));
            Log.Guard("Weapons.Dispose.EmptyArms", _emptyArms.Destroy); // (DontDestroyOnLoad: it outlives the scenes)
            // Each step on its own, as VRManager's and the rig's: one that threw skipped the rest, the lens flash's muzzle
            // objects (DontDestroyOnLoad) and the scopes' spare textures among them (render-effects scout, 2026-10-23).
            // Each part only if it was made (a constructor that threw part way disposes what it built).
            Log.Guard("Weapons.Dispose.Props", () =>
            {
                MagazineProp.DestroyAll();
                GrenadeDud.DestroyAll();
                Casings.DestroyAll();
            });
            Log.Guard("Weapons.Dispose.Drops", () => Drops?.Dispose());
            Log.Guard("Weapons.Dispose.Pouch", () => _pouch?.Destroy());
            Log.Guard("Weapons.Dispose.LensFlash", () => _lensFlash?.Destroy());
            Log.Guard("Weapons.Dispose.Holsters", () => _holsters?.Destroy());
            Log.Guard("Weapons.Dispose.Readout", () => _readout?.Destroy());
            Log.Guard("Weapons.Dispose.FireSelector", FireSelector.Clear);
            Log.Guard("Weapons.Dispose.Zones", () => Zones?.Destroy());
            Log.Guard("Weapons.Dispose.Sounds", () => Sounds?.Dispose());
            Log.Guard("Weapons.Dispose.Scopes", VRScope.ReleaseSpares);
            Log.Guard("Weapons.Dispose.BodyGrab", () =>
            {
                _bodyGrab?.Destroy();
                BodyImpacts.Clear();
            });
            VRGameInput.SuppressFire = false;
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
