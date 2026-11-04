using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// A second weapon in the free hand beside the game's active one (Tasks 252 p, step two of 152): taken from its holster
    /// by the free hand while a gun is held, posed in that hand, fired with that hand's own trigger, put back in its holster
    /// when the hand lets go. The game keeps the gun in the other hand as its one active weapon; this one is drawn and fired
    /// through the weapon itself (Unholster, Fire, StopFire), which the game allows for any drawn weapon of the player.
    /// Pairs: one each of primary, secondary and launcher (never two primaries). In this first step it is not reloaded by
    /// hand (both hands are full): empty, its trigger reloads it from the reserve over its own reload time.
    /// </summary>
    internal sealed class SecondWeapon
    {
        public readonly Weapon Weapon;
        public readonly WeaponRig Rig;
        /// <summary>This gun's data for its sounds (GameSounds: its own, or those of its kind).</summary>
        private WeaponRigData SoundData => Rig != null ? Rig.Data : null;
        public readonly AmmoState Ammo;
        public readonly VRHand Hand;
        private readonly WeaponSystem _sys;
        /// <summary>Its class (pistol, rifle, launcher...), as the gun hand would read it: its grip and its kick.</summary>
        private readonly WeaponClass _class;
        private bool _firing;
        private Vector3 _kickPos, _kickPosVel;
        private Vector2 _kickRot, _kickRotVel;
        private float _reloadDoneAt = -1f;
        private int _reloadRounds;

        /// <summary>Autotest: its trigger held without a hand on it.</summary>
        internal static bool TestTrigger;
        /// <summary>Autotest: shots it fired.</summary>
        internal int Shots { get; private set; }

        /// <summary>What kind of weapon this is for pairing: 0 primary, 1 secondary, 2 launcher (large gear), 3 grenade; -1 other.</summary>
        internal static int Kind(Weapon w)
        {
            if (w == null)
            {
                return -1;
            }
            if (w is ThrowableWeapon)
            {
                return 3;
            }
            switch (w.slot)
            {
                case 0: return 0;
                case 1: return 1;
                case 3: return 2;
                default: return -1;
            }
        }

        /// <summary>
        /// The two may be held at once (user, 2026-10-16): primary + secondary, primary + launcher, secondary + launcher, and a
        /// grenade with any of them; never two of a kind.
        /// </summary>
        internal static bool CanPair(Weapon main, Weapon second)
        {
            int a = Kind(main), b = Kind(second);
            return a >= 0 && b >= 0 && a != b && second != main && !(second is MeleeWeapon);
        }

        /// <summary>A grenade (or another throwable) in the free hand: thrown, not fired.</summary>
        internal bool IsThrowable => Weapon is ThrowableWeapon;

        /// <summary>Its pin pulled (the hand's trigger squeezed: the other hand holds the gun and cannot pull it).</summary>
        internal bool Armed { get; private set; }

        /// <summary>A throwable with a pin to pull (a grenade, with pins on): only that one makes the pull's sound and drops a pin.</summary>
        private readonly bool _usesPin;
        /// <summary>
        /// Its own pin part, hidden while the pin is out as the grenade hand hides it (it stayed in place in the free hand, only the
        /// sound said it was pulled; round-5 scout), and the scale it is given back.
        /// </summary>
        private readonly Transform _modPin;
        private readonly Vector3 _modPinScale;

        /// <summary>Done with: thrown and nothing left to throw. The weapon system lets it go.</summary>
        internal bool Done { get; private set; }

        /// <summary>Autotest: live projectiles of its last throw.</summary>
        internal int LastLiveCount { get; private set; }

        private bool _triggerWas;
        private float _lastUpdateAt = -1f;
        private bool _throwBlocked; // input was blocked (a pause, or a panel under the laser) last frame
        private readonly List<Projectile> _spawned = new List<Projectile>();
        /// <summary>The weapon's own right hand (its first-person arms), whose grip pose the glove takes, as the gun hand's does.</summary>
        private readonly ModHand _gripHand;

        public SecondWeapon(WeaponSystem sys, Weapon weapon, WeaponRig rig, AmmoState ammo, VRHand hand)
        {
            _sys = sys;
            Weapon = weapon;
            Rig = rig;
            Ammo = ammo;
            Hand = hand;
            if (!weapon.gameObject.activeSelf)
            {
                weapon.gameObject.SetActive(true);
            }
            weapon.Unholster();
            hand.Role = weapon is ThrowableWeapon ? HandRole.Throwable : HandRole.Weapon;
            if (rig != null && rig.Data.Action == ActionType.Throwable)
            {
                // Held by its middle, in the fist (only the gun hand's draw did this: a grenade first taken in the free hand hung
                // from where its throw animation's palm was; second-weapon hunt, 2026-10-21).
                HeldWeapon.CentreGrenadeGrip(rig, weapon);
            }
            _class = Rig != null ? HeldWeapon.ClassOf(Rig, weapon) : WeaponClass.Rifle;
            if (hand.Model != null && Rig != null)
            {
                // Its own grip pose, as the gun hand's: unset, every second weapon took the rifle grip, a pistol's thumb wrapped over
                // its slide for the whole hold (hand hunt, 2026-10-20).
                hand.Model.WeaponIsPistol = Rig.Data.IsPistol;
                hand.Model.WeaponClass = _class;
                hand.Model.WeaponGripCurls = Rig.Data.GripCurls;
                hand.Model.WeaponGripThumbBase = Rig.Data.GripThumbBase;
                hand.Model.WeaponGripThumbCurl = Rig.Data.GripThumbCurl;
            }
            _gripHand = Rig != null && !(weapon is ThrowableWeapon) ? ModHand.Find(weapon.transform, Rig.Data.ArmRight) : null;
            // A grenade put away with its pin out is drawn with it out (it came back a dud; review, 2026-10-20).
            Armed = weapon is ThrowableWeapon pulled && sys.Throwables != null && sys.Throwables.TakePulled(pulled);
            _usesPin = weapon is ThrowableWeapon pinned && ThrowableHandler.HasPin(pinned);
            if (_usesPin)
            {
                _modPin = ThrowableHandler.FindModPin(weapon.transform);
                if (_modPin != null)
                {
                    _modPinScale = _modPin.localScale;
                    if (Armed)
                    {
                        _modPin.localScale = Vector3.one * 1e-4f; // (drawn with its pin out, as the grenade hand's SetupPin does: it showed one)
                    }
                }
            }
            Log.Info($"Second weapon: {weapon.name} in the {hand.Side} hand beside {(sys.Held != null && sys.Held.Weapon != null ? sys.Held.Weapon.name : "nothing")}.");
        }

        /// <summary>Its trigger (the hand's own, which is the game's Use on the free hand), and a timed reload when it is empty.</summary>
        public void Update()
        {
            VRGameInput.SupportTriggerOwned = true; // the free hand's trigger fires this weapon, it does not enter vehicles
            VRGameInput.SupportTriggerLightsOnly = false;
            if (Weapon == null)
            {
                return;
            }
            // The squeeze that started a reload is not a shot when the reload ends: held through it, the gun fired the moment it
            // was loaded (an automatic one kept firing). Read through the reload, so a let-go during it is seen (second-weapon hunt).
            bool pressed = VRInput.GetHeld(DigitalAction.Use, Hand.Side) || TestTrigger;
            if (!pressed)
            {
                _awaitRelease = false;
            }
            if (_reloadDoneAt >= 0f)
            {
                if (Time.time >= _reloadDoneAt)
                {
                    FinishReload();
                }
                return;
            }
            bool blocked = VRGameInput.GunInputBlocked;
            bool held = pressed && !blocked && !_awaitRelease;
            if (IsThrowable)
            {
                // Back after a gap (a tracking gap: the free hand is not worked while untracked; or the first update after it was
                // taken): resynced to the trigger as after a block, so a trigger let go out of view is no throw when the hand is seen
                // again, and one already down as the grenade is taken does not pull its pin (round 11).
                if (Time.unscaledTime - _lastUpdateAt > 0.1f)
                {
                    _triggerWas = held;
                }
                _lastUpdateAt = Time.unscaledTime;
                if (_throwBlocked && !blocked)
                {
                    // Back from a block (the match paused with no menu, or a panel opened under the laser): the pin stays pulled,
                    // but a trigger let go while blocked is not a throw. Resync to the trigger now, so the first frame back does not
                    // read the release as a throw (the main hand disarms instead; here Armed also marks the pin out, so it stays).
                    _triggerWas = held;
                }
                _throwBlocked = blocked;
                // A grenade with its pin out keeps the trigger as it was while input is blocked (a panel opened under the laser,
                // or the match paused): blocked, it read as let go, and a moving hand threw it (review, 2026-10-20; pause, 2026-10-21).
                UpdateThrowable(blocked && Armed ? _triggerWas : held);
                return;
            }
            if (Ammo != null && Ammo.Participates && Ammo.HasCover && Ammo.MagInserted && !Ammo.FeedReady && !_firing && _reloadDoneAt < 0f)
            {
                // A belt-fed gun taken into the free hand with its cover open or its belt not laid (left so by a reload in the gun
                // hand): this hand cannot work the cover, and with rounds in the box it never reloads, so it could neither fire nor
                // be fixed until moved back. Shut and laid for it, as its own reload does (gameplay workflow, 2026-10-24).
                Ammo.CoverOpen = false;
                Ammo.BeltLaid = true;
            }
            CycleForTheHand();
            // In either reload mode: the game reloads only its active weapon, so with Assisted reloading an empty second weapon
            // stayed empty for as long as it was held (second-weapon hunt, 2026-10-20 night). This reload is the hand's own.
            if (held && !_firing && Ammo != null && Ammo.Participates
                && !Ammo.Chambered && Ammo.MagRounds == 0 && (Ammo.InfiniteSpare || Ammo.SpareAmmo > 0))
            {
                StartReload();
                return;
            }
            if (held)
            {
                Vector3 aim = Rig.GunBody != null ? Rig.AimWorld : Weapon.transform.forward;
                Weapon.Fire(aim, true);
                _firing = true;
            }
            else if (_firing)
            {
                Weapon.StopFire();
                _firing = false;
            }
        }

        /// <summary>
        /// A grenade in the free hand (252 p): the trigger squeezed pulls its pin (the other hand holds the gun); let go of the
        /// trigger in a throwing motion, it is thrown. The grip let go in a throwing motion throws it too (<see cref="LetGo"/>).
        /// </summary>
        private void UpdateThrowable(bool trigger)
        {
            if (trigger && !_triggerWas && !Armed)
            {
                Armed = true; // (a molotov, C4 or bag too: the trigger's release throws it)
                if (_usesPin)
                {
                    PullPin();
                }
            }
            if (!trigger && _triggerWas && Armed)
            {
                Vector3 v = ThrowableHandler.Released(Hand);
                if (v.magnitude > 1.1f)
                {
                    Throw(v);
                }
            }
            _triggerWas = trigger;
        }

        /// <summary>
        /// The pin out of a grenade: its sound and buzz, its own pin part gone from the grenade and a copy of it falling away from the
        /// hand (the other hand holds the gun: nothing keeps it), or a plain ring for a grenade with no pin part of its own. Only
        /// pinned grenades: a molotov, C4 or a bag made the pull's sound and buzz too (round-5 scout).
        /// </summary>
        private void PullPin()
        {
            _sys.Sounds.Play(MechSound.PinPull, Hand.Palm.position, 0.8f);
            Hand.Haptic(0.03f, 120f, 0.5f);
            GameObject ring;
            if (_modPin != null)
            {
                ring = PinRing.CreateFrom(_modPin, ThrowableHandler.PartCenter(_modPin));
                _modPin.localScale = Vector3.one * 1e-4f;
            }
            else
            {
                ring = PinRing.Create();
                RigidPose palm = Hand.Palm;
                ring.transform.SetPositionAndRotation(palm.position + palm.Forward * 0.035f, palm.rotation);
            }
            PinRing.Drop(ring, Hand.Velocity);
        }

        /// <summary>The grenade's own pin part shown again (the next one in hand, or the grenade leaving the hand).</summary>
        private void RestorePin()
        {
            if (_modPin != null)
            {
                _modPin.localScale = _modPinScale;
            }
        }

        /// <summary>
        /// The grip let go: in a throwing motion against the body it is thrown (a dud with its pin in), else it goes back to
        /// its holster. Then it is put away either way.
        /// </summary>
        public void LetGo()
        {
            if (IsThrowable && Weapon != null && !Done && !Util.GameContext.GameMenuOpen && !VRGameInput.GunInputBlocked)
            {
                // Not while input is blocked (the match paused with no menu): opening the grip there threw the grenade, as the
                // main hand's grip-throw is already gated (ThrowAndMelee). Paused, GameMenuOpen is false, so it needs this too.
                Vector3 body = Hand.BodyThrowVelocity() * VRConfig.ThrowVelocityMultiplier.Value;
                Vector3 v = ThrowableHandler.Released(Hand);
                if (v.magnitude < body.magnitude * 0.7f)
                {
                    v = body;
                }
                if (body.magnitude > 1.6f)
                {
                    Throw(v);
                }
            }
            if (Armed && !Done && Weapon is ThrowableWeapon kept)
            {
                _sys.Throwables?.PutAwayPulled(kept); // its pin stays out
            }
            PutAway();
        }

        /// <summary>Autotest: thrown now with this velocity.</summary>
        internal void TestThrow(Vector3 velocity) => Throw(velocity);

        /// <summary>
        /// Out of the hand through the weapon's own Shoot (ammo, cooldown and resupply stay the game's), from the palm along the
        /// throw, the projectile given the hand's velocity: as the grenade hand's throw (ThrowableHandler.Throw). With its pin
        /// in, an inert dud.
        /// </summary>
        private void Throw(Vector3 velocity)
        {
            var weapon = (ThrowableWeapon)Weapon;
            LastLiveCount = 0;
            weapon.isPreparingToThrowProjectile = false;
            weapon.hasFiredSingleRoundThisTrigger = false;
            if (!weapon.CanFire() || !weapon.unholstered)
            {
                Hand.Haptic(0.08f, 40f, 0.4f);
                return;
            }
            // Only what has a pin to pull (a grenade, with pins on): a molotov, C4 or a bag thrown from the free hand landed inert,
            // as the grenade hand's _usesPin already knew (throw hunt, 2026-10-20).
            if (!Armed && ThrowableHandler.HasPin(weapon))
            {
                // As the grenade hand's dud: one fewer in the pouch (picked up again, it goes back), the next one ready.
                GrenadeDud.Spawn(Rig, weapon, Hand.Palm.position, Hand.Palm.rotation, velocity, Hand.AngularVelocity);
                weapon.ammo = weapon.ammo > 0 ? weapon.ammo - 1 : weapon.ammo; // (-1, without end, stays so: the game checks that too)
                weapon.InstantlyReload();
                Hand.Haptic(0.03f, 70f, 0.4f);
                Done = true;
                return;
            }
            Transform muzzle = weapon.CurrentMuzzle();
            Vector3 savedPos = muzzle.localPosition;
            Quaternion savedRot = muzzle.localRotation;
            Vector3 dir = velocity.sqrMagnitude > 1e-6f ? velocity.normalized : Hand.Palm.Forward; // (let go dead still: no zero look rotation)
            muzzle.SetPositionAndRotation(Hand.Palm.position + dir * 0.08f, Quaternion.LookRotation(dir));
            _spawned.Clear();
            weapon.onSpawnProjectiles.AddListener(OnSpawned);
            try
            {
                weapon.Shoot(Vector3.zero, true);
            }
            finally
            {
                weapon.onSpawnProjectiles.RemoveListener(OnSpawned);
                muzzle.localPosition = savedPos;
                muzzle.localRotation = savedRot;
            }
            foreach (Projectile p in _spawned)
            {
                if (p != null)
                {
                    p.velocity = velocity;
                    p.transform.rotation = Quaternion.LookRotation(dir);
                    LastLiveCount++;
                }
            }
            _spawned.Clear();
            if (LastLiveCount == 0)
            {
                return; // nothing left the hand (a script took the throw): still armed, still in the hand
            }
            Armed = false; // the next one comes with its pin in
            RestorePin();
            if (_usesPin)
            {
                _sys.Sounds.Play(MechSound.Spoon, Hand.Palm.position, 0.8f); // (a pinned grenade's only, as in the gun hand: no spoon on a molotov)
            }
            Hand.Haptic(0.04f, 90f, 0.6f);
            Log.Info($"Second weapon: {weapon.name} thrown from the {Hand.Side} hand ({velocity.magnitude:0.0} m/s).");
            // The hand is empty after a throw, as with the grenade hand (the next one waits in its holster).
            Done = true;
        }

        private void OnSpawned(Projectile[] projectiles)
        {
            if (projectiles != null)
            {
                _spawned.AddRange(projectiles);
            }
        }

        private float _cycleReadyAt;
        private bool _animatedCycle; // the coming stroke follows a shot whose fire animation cycled the action itself

        /// <summary>
        /// A pump or bolt gun (or any gun with nothing chambered) in the free hand: the other hand holds the main gun and
        /// cannot work its action, so it is worked for it, the spent case out and the next round in, a moment after each shot
        /// (a pump shotgun taken as a second weapon never fired: nothing was chambered and nobody could pump it; dump-1017b).
        /// </summary>
        private void CycleForTheHand()
        {
            if (Ammo == null || !Ammo.Participates || !Ammo.UsesChamber || Ammo.Chambered || Ammo.MagRounds <= 0 || _firing || Time.time < _cycleReadyAt)
            {
                return;
            }
            Ammo.BoltOpen = false;
            Ammo.RackBack(); // (the spent case is not thrown: Casings throws from the main gun's port)
            bool animated = _animatedCycle;
            _animatedCycle = false;
            if (Ammo.RackForward())
            {
                if (!animated)
                {
                    // (After a shot the fire animation already worked and sounded it: both played, a double stroke; second-weapon
                    // hunt, 2026-10-20 night.)
                    _sys.Sounds.Play(MechSound.SlideBack, Hand.Palm.position, 0.6f, 0f, SoundData);
                    _sys.Sounds.Play(MechSound.SlideForward, Hand.Palm.position, 0.6f, 0.12f, SoundData); // back, then home
                }
                Hand.Haptic(0.03f, 120f, 0.4f);
            }
        }

        /// <summary>
        /// The rounds of a reload under way, for when the weapon leaves the hand in a swap (the gun hand took up a weapon of its slot):
        /// the rounds it took for it, which go with the weapon put down. Settled after the weapon was destroyed, they went to
        /// the destroyed weapon's reserve and were lost (scout, 2026-10-22). 0 when none is under way.
        /// </summary>
        internal int ReloadRounds => _reloadDoneAt >= 0f ? _reloadRounds : 0;

        /// <summary>The reload under way given up, its rounds handed over (see <see cref="ReloadRounds"/>).</summary>
        internal int TakeReloadRounds()
        {
            if (_reloadDoneAt < 0f)
            {
                return 0;
            }
            _reloadDoneAt = -1f;
            return _reloadRounds;
        }

        /// <summary>A reload was started by a squeeze still held: no shot until the trigger is let go once.</summary>
        private bool _awaitRelease;

        private void StartReload()
        {
            _awaitRelease = true;
            int rounds = Ammo.RoundsForFreshMagazine();
            if (rounds <= 0)
            {
                return;
            }
            _reloadRounds = rounds;
            Ammo.TakeSpare(rounds);
            if (Ammo.UsesDetachableMag && Ammo.MagInserted)
            {
                Ammo.EjectMagazine();
            }
            float time = Weapon.configuration != null ? Mathf.Clamp(Weapon.configuration.reloadTime, 0.8f, 4f) : 2f;
            _reloadDoneAt = Time.time + time;
            _sys.Sounds.Play(MechSound.MagOut, Hand.Palm.position, 0.8f, 0f, SoundData);
            Hand.Haptic(0.04f, 120f, 0.5f);
        }

        private void FinishReload()
        {
            _reloadDoneAt = -1f;
            if (Ammo.UsesDetachableMag)
            {
                Ammo.InsertMagazine(_reloadRounds);
            }
            else
            {
                int accepted = Ammo.LoadRounds(_reloadRounds);
                Ammo.ReturnSpare(_reloadRounds - accepted);
            }
            if (Ammo.HasCover)
            {
                // The hand's own reload is the whole reload, the belt laid under the shut cover too: with belt laying by hand a new
                // box left it unlaid, which nothing lays for this hand, and a machine gun in the free hand stayed dry for good
                // after its first reload (its magazine not empty, no reload again; scout, 2026-10-22).
                Ammo.CoverOpen = false;
                Ammo.BeltLaid = Ammo.MagInserted;
            }
            Ammo.SlideLocked = false;
            Ammo.RackForward();
            _sys.Sounds.Play(MechSound.MagIn, Hand.Palm.position, 0.8f, 0f, SoundData);
            _sys.Sounds.Play(MechSound.SlideForward, Hand.Palm.position, 0.8f, 0.2f, SoundData); // the magazine in, then the slide home
            Hand.Haptic(0.05f, 110f, 0.7f);
        }

        /// <summary>A shot of this weapon: it kicks in its hand (a short spring, as the main gun's) and the hand feels it.</summary>
        public void OnShot()
        {
            Shots++;
            if (IsThrowable)
            {
                return; // a throw: no kick in the hand
            }
            // Worked for the hand a moment after the shot (a pump's or bolt's stroke).
            _cycleReadyAt = Time.time + (Rig.Data.CyclesByHand ? 0.45f : 0f);
            // The game's own fire animation is not cancelled for this hand (WeaponSystem.OnPlayerShot): on a pump or bolt gun it
            // cycles the action on the shot, with its own sounds, so the stroke that follows only moves the rounds.
            _animatedCycle = Rig.Data.CyclesByHand && Weapon.animator != null && Weapon.animator.isActiveAndEnabled;
            Weapon.Configuration c = Weapon.configuration;
            // (By its class as the gun hand reads it: a launcher or machine gun classed as a rifle by its handgun flag lost their kick
            // floors in this hand; round-2 scout, 2026-10-27.)
            float kick = HeldWeapon.ShotKick(c, Rig.Data, _class, out float climbShare);
            float strength = VRConfig.RecoilStrength.Value * (Rig.Data.Handgun ? 1.15f : 1.6f); // held in one hand
            _kickPosVel += new Vector3(0f, 0.1f * kick, -0.9f * kick * Mathf.Max(0.3f, 2f - climbShare)) * strength;
            _kickRotVel += new Vector2(-(180f * kick * climbShare), Random.Range(-25f, 25f)) * strength;
            Hand.Haptic(0.03f + kick * 0.01f, 180f, Mathf.Clamp01(0.35f + kick * 0.15f));
            Hand.Model?.TriggerShot();
        }

        /// <summary>Posed in its hand, the hand's grip on the weapon's grip, with its kick.</summary>
        public void BeforeRender()
        {
            if (Weapon == null || Rig.GunBody == null || !Hand.HasPose)
            {
                return;
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            const float stiffness = 150f;
            float damping = 2f * Mathf.Sqrt(stiffness);
            _kickPosVel += (-stiffness * _kickPos - damping * _kickPosVel) * dt;
            _kickPos = Vector3.ClampMagnitude(_kickPos + _kickPosVel * dt, 0.12f);
            _kickRotVel += (-stiffness * _kickRot - damping * _kickRotVel) * dt;
            _kickRot = Vector2.ClampMagnitude(_kickRot + _kickRotVel * dt, 25f);

            if (Armed && _modPin != null)
            {
                _modPin.localScale = Vector3.one * 1e-4f; // (its animator writes the pin part every frame: kept hidden while out)
            }
            WeaponRigData d = Rig.Data;
            RigidPose hand = HeldWeapon.HoldFrame(Hand, d);
            Quaternion gripInGun = d.HasGripRotation ? d.GripRotation : Quaternion.LookRotation(d.AimAxis, d.Up);
            Quaternion aimFrame = hand.rotation * Quaternion.Euler(_kickRot.x, _kickRot.y, 0f);
            Quaternion gunRot = aimFrame * Runtime.RigidPose.Conjugate(gripInGun);
            Vector3 gripWorld = hand.position + aimFrame * _kickPos;
            var gunBody = new RigidPose(gripWorld - gunRot * d.GripPoint, gunRot);
            Transform root = Weapon.transform;
            RigidPose gunInRoot = RigidPose.Of(root).Inverse().Multiply(RigidPose.Of(Rig.GunBody));
            gunBody.Multiply(gunInRoot.Inverse()).ApplyTo(root);
            if (Hand.Model != null)
            {
                // The glove goes with the kick: the gun left the fist for a moment after every shot (second-weapon hunt,
                // 2026-10-21), as the gun hand's glove goes with its gun's lag.
                Hand.Model.FollowOffset = aimFrame * _kickPos;
                Hand.Model.FollowFrame = Time.frameCount;
                // On the weapon's own grip pose, as the gun hand's glove: left on the tracked hand, it grasped 4.3 cm off the grip
                // the weapon is held by (measured; user, 2026-10-21: "the 2nd object sometimes isnt in the hand and floats").
                Hand.Model.SnapPose = _gripHand != null && _gripHand.TryPose(Weapon.transform, out RigidPose grip)
                                      && (grip.position - Rig.ToWorld(d.GripPoint)).sqrMagnitude < 0.06f * 0.06f
                    ? Hand.IsLeft ? HeldWeapon.MirrorAcrossGun(Rig, grip) : grip // the weapon's hand is a right hand
                    : (RigidPose?)null;
            }
            if (Weapon.isHidden)
            {
                Weapon.Show(); // the game hid it as a weapon not in use: it is in the hand
            }
            GateScopeCamera();
            if (IsThrowable)
            {
                HeldWeapon.CentreGrenadeGrip(Rig, Weapon); // once shown (hidden when taken, its middle could not be measured yet)
            }
            HideArms(); // after Show(), which draws the weapon's own arms again
            // Its parked spare magazines and rounds are not drawn in the hand, as the held gun's are not (Task 268: a spare
            // waited beside it in the free hand, floating).
            Rig.HideSpareParts();
            UpdateGlove();
        }

        /// <summary>
        /// The weapon's own first-person arms (the game's model, both of its hands on the gun) are hidden while it is in the free
        /// hand, as the drawn gun hides its own (<see cref="HeldWeapon"/>'s HideArms). Taking it out unholsters it, which shows
        /// them (Weapon.Show), and nothing hid them again: the user saw the two hands of the normal Ravenfield model beside the
        /// gun (Task 267, 2026-10-18). This weapon has no arm IK to put them on the controllers.
        /// </summary>
        private void HideArms()
        {
            foreach (Renderer r in Rig.ArmRenderers)
            {
                if (r != null && r.enabled)
                {
                    r.enabled = false;
                }
            }
        }

        private GunVolume _volume;
        private System.Func<Vector3, bool> _solid;
        private Vector3 _solidAt;
        private Quaternion _solidInverse = Quaternion.identity;
        private readonly float _heldSince = Time.unscaledTime;
        private float _volumeRetryAt;
        private int _volumeTries;

        /// <summary>
        /// The hand closes on this weapon's own measured shape, as the gun hand on its gun (252 p: the glove was the tracked
        /// fist, closed on nothing). Measured a moment after it is taken (shared per weapon type), posed with the weapon.
        /// </summary>
        private void UpdateGlove()
        {
            HandModel model = Hand.Model;
            if (model == null || Rig.GunBody == null)
            {
                return;
            }
            Quaternion frame = OpenVRMath.SafeLookRotation(Rig.Data.AimAxis, Rig.Data.Up);
            if (_volume != null && (_volume.Failed || _volume.Stalled))
            {
                // (Stalled too, as the gun hand's: a read-back that never came back left the glove closing on nothing.)
                _volume = null; // measured again a second later
                _volumeRetryAt = Time.unscaledTime + 1f;
            }
            // Only while it is drawn (measured from the first frame now, a hidden weapon would give an empty shape to its whole type).
            if (_volume == null && _volumeTries < 3 && Time.unscaledTime >= _volumeRetryAt && Time.unscaledTime - _heldSince > 0.25f && !Weapon.isHidden)
            {
                _volumeTries++;
                _volume = GunVolume.For(Rig, frame, Rig.Data.GripPoint, posedByHand: true);
                if (_volume == null)
                {
                    _volumeRetryAt = Time.unscaledTime + 1f;
                }
            }
            if (_volume == null || !_volume.Ready)
            {
                return;
            }
            // At its own pose: the volume is shared by every gun of the model (GunVolume.For caches by key), and with both hands on
            // the same model the main gun's pose, set after this one, was the one the free hand's fingers closed on.
            _solidAt = Rig.ToWorld(Rig.Data.GripPoint);
            _solidInverse = Quaternion.Inverse(Rig.GunBody.rotation * frame);
            _volume.SetPose(_solidAt, Rig.GunBody.rotation * frame);
            model.Solid = _solid ?? (_solid = p => _volume != null && _volume.Ready && _volume.SolidAt(p, _solidAt, _solidInverse));
            model.SolidAnchor = Rig.GunBody;
            model.SolidVersion = 0;
        }

        /// <summary>Autotest: what the hand holding it closes on (its role, its solid, the measurement's state).</summary>
        internal string GloveState =>
            $"{Hand.Role}, closing on {(Hand.Model == null ? "no glove" : Hand.Model.Solid == null ? "nothing" : Hand.Model.Solid == _solid ? "its shape" : "another shape")}, "
            + $"shape {(_volume == null ? $"none ({_volumeTries} tries)" : _volume.Ready ? "ready" : _volume.Failed ? "failed" : "measuring")}";

        /// <summary>Back into its holster (let go, or the main gun went away).</summary>
        public void PutAway()
        {
            if (Weapon == null)
            {
                // Destroyed under the hand: the game replaced it (the gun hand picked up a weapon of its slot, which drops and
                // destroys this one at the end of that frame; a loadout change). The hand is freed all the same: its role came
                // back only through HealHandRoles, and its shape to close on stayed the destroyed weapon's, so the next grip
                // that set no shape of its own closed the fingers on an invisible gun (bug hunt, 2026-10-19).
                if (_reloadDoneAt >= 0f)
                {
                    // A reload under way gives back what it took (review, 2026-10-20): to a reserve of its kind still about. Into its
                    // own, the destroyed weapon's, they were lost with it (round-2 scout, 2026-10-27; the swap hands them over first).
                    _sys.ReturnRoundsOfKind(Rig?.Data?.Key, _reloadRounds);
                    _reloadDoneAt = -1f;
                }
                FreeHand();
                return;
            }
            if (_firing)
            {
                Weapon.StopFire();
                _firing = false;
            }
            if (_reloadDoneAt >= 0f)
            {
                FinishReload(); // a reload under way is finished, not lost
            }
            Rig.ShowSpareParts(); // its parked ammunition back as the rig was made, before it is put away (Task 268)
            KeepPulledPin();
            Weapon.Holster();
            if (!Weapon.isHidden)
            {
                // Undone as HeldWeapon.Release undoes it: its own arms come back with it, for whichever hand takes it next.
                foreach (Renderer r in Rig.ArmRenderers)
                {
                    if (r != null)
                    {
                        r.enabled = true;
                    }
                }
            }
            FreeHand();
            _sys.Sounds.Play(MechSound.Holster, Hand.Palm.position, 0.6f);
            Hand.Haptic(0.04f, 100f, 0.5f);
            Log.Info($"Second weapon: {Weapon.name} back in its holster.");
        }

        /// <summary>The weapon became the gun hand's (the game drew it): this hand lets go of it, nothing is holstered.</summary>
        public void HandOver()
        {
            if (Weapon != null && _firing)
            {
                Weapon.StopFire();
                _firing = false;
            }
            if (Weapon != null && _reloadDoneAt >= 0f)
            {
                FinishReload();
            }
            KeepPulledPin();
            FreeHand();
        }

        /// <summary>
        /// A grenade with its pin out leaving this hand unthrown keeps it out, for whichever hand takes it next: only the let-go
        /// recorded it, and handed over (its number key) or put away by a seat, death or lost tracking it came back with its pin
        /// in and was thrown as a dud (throw hunt, 2026-10-20).
        /// </summary>
        private void KeepPulledPin()
        {
            if (Armed && !Done && Weapon is ThrowableWeapon kept)
            {
                _sys.Throwables?.PutAwayPulled(kept);
            }
        }

        /// <summary>
        /// Its own scope camera (a game render-texture camera) draws only while the eye is at the scope (the head within 30 cm of it:
        /// the free hand's weapon has no lens state of its own), and without shadows unless the scope shadows setting asks: drawn
        /// every frame with its shadow cascades, as the gun hand's was (HeldWeapon.GateScopeCamera; round-3 scout, 2026-10-28).
        /// Away from the eye the lens keeps its last picture.
        /// </summary>
        private void GateScopeCamera()
        {
            Camera cam = Weapon != null ? Weapon.renderTextureCamera : null;
            if (cam == null || _sys.Rig.Head == null)
            {
                return;
            }
            bool atEye = (cam.transform.position - _sys.Rig.Head.position).sqrMagnitude < 0.3f * 0.3f;
            if (cam.enabled != atEye)
            {
                if (!ReferenceEquals(_scopeCameraGated, cam))
                {
                    _scopeCameraWas = cam.enabled;
                }
                cam.enabled = atEye;
                _scopeCameraGated = cam;
            }
            if (atEye)
            {
                VRScope.LensDrawnAt = Time.unscaledTime; // (the game's own scope picture: magnified too, everything drawn; TinyObjects)
            }
            if (_scopeNoShadows == null || _scopeNoShadows.gameObject != cam.gameObject)
            {
                _scopeNoShadows = cam.GetComponent<VRScope.ScopeNoShadows>() ?? cam.gameObject.AddComponent<VRScope.ScopeNoShadows>();
            }
            bool noShadows = !VRConfig.ScopeShadows.Value;
            if (_scopeNoShadows.enabled != noShadows)
            {
                _scopeNoShadows.enabled = noShadows;
            }
        }

        private Camera _scopeCameraGated;
        private bool _scopeCameraWas;
        private VRScope.ScopeNoShadows _scopeNoShadows;

        /// <summary>The hand empty again: free to take things, closing on nothing.</summary>
        private void FreeHand()
        {
            // Its pin part as the model has it: a grenade put away with its pin out is hidden again by whichever hand draws it
            // (TakePulled, the grenade hand's SetupPin), and one handed over must not give its next holder a hidden scale to keep.
            RestorePin();
            // Its scope camera as the game had it (the gate switched it off away from the eye).
            if (_scopeCameraGated != null)
            {
                _scopeCameraGated.enabled = _scopeCameraWas; // (as the held gun's: forced on, one the game had off drew the scene from the holster)
                _scopeCameraGated = null;
            }
            if (_scopeNoShadows != null)
            {
                _scopeNoShadows.enabled = false;
            }
            if (Hand.Role == HandRole.Weapon || Hand.Role == HandRole.Throwable)
            {
                Hand.Role = HandRole.Free;
            }
            if (Hand.Model != null && _solid != null && Hand.Model.Solid == _solid)
            {
                Hand.Model.Solid = null; // nothing left in the hand to close on
            }
            if (Hand.Model != null)
            {
                Hand.Model.SnapPose = null; // its grip pose goes with it whatever the hand does next (rewritten each frame by a holder)
            }
            if (Hand.Model != null && (Hand.Role == HandRole.Free))
            {
                Hand.Model.WeaponIsPistol = false;
                Hand.Model.WeaponClass = WeaponClass.None;
                Hand.Model.WeaponGripCurls = null;
                Hand.Model.WeaponGripThumbBase = -1f;
                Hand.Model.WeaponGripThumbCurl = -1f;
            }
        }
    }
}
