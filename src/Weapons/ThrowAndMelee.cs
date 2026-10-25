using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Grenades and other throwables leave the hand with the hand's real velocity when the trigger is released.
    /// The game's throw animation is bypassed: the projectile is spawned through the weapon's own Shoot()
    /// (so ammo, cooldown, emotes and resupply logic stay intact) and its velocity is replaced afterwards.
    /// Fused grenades have a pin: pull it with the other hand (or press A), and nothing happens while you keep holding
    /// the grenade; the fuse starts when it leaves your hand, like a spoon flying off. Thrown with the pin still in it
    /// is a dud: it bounces and lies there, and picking it up puts the grenade back in your pouch.
    /// </summary>
    public sealed class ThrowableHandler
    {
        private const float PinGrabRadius = 0.07f;
        private const float PinPullDistance = 0.055f;

        private readonly WeaponSystem _sys;
        private bool _armed;
        private readonly List<Projectile> _spawned = new List<Projectile>();
        private ThrowableWeapon _listening;

        // Pin state for the grenade in hand.
        private ThrowableWeapon _pinFor;
        private WeaponRig _pinRig;
        private bool _usesPin;
        private bool _pinPulled;
        private bool _holdingPin;
        private Vector3 _pinGrabAt;
        private Transform _modPin;
        private Vector3 _modPinScale;
        private Vector3 _pinLocal;
        private Vector3 _pinAxisLocal;
        private GameObject _ring;
        private VRHand _ringHand;
        /// <summary>Grenades put away with their pin pulled: drawn again, the pin is still out (it does not grow back).</summary>
        private readonly HashSet<ThrowableWeapon> _pulledPutAway = new HashSet<ThrowableWeapon>();

        /// <summary>Live projectiles the last throw spawned (the autotest checks a pinned throw spawns none).</summary>
        internal int LastLiveCount { get; private set; }

        /// <summary>Time of the last throw made by letting go of the grip (grip-to-hold), so letting go is not also a holster.</summary>
        internal float GripThrowAt { get; private set; } = -10f;

        public ThrowableHandler(WeaponSystem sys)
        {
            _sys = sys;
        }

        public void Update(HeldWeapon held)
        {
            var weapon = held.Weapon as ThrowableWeapon;
            if (weapon == null)
            {
                _armed = false;
                return;
            }
            if (_pinFor != weapon)
            {
                SetupPin(held, weapon);
            }
            VRGameInput.SuppressFire = true;
            VRHand hand = _sys.Rig.Dominant;
            hand.Role = HandRole.Throwable;
            if (_usesPin)
            {
                UpdatePin();
            }
            if (VRInput.GetPressed(DigitalAction.Fire, hand.Side) && !GameContextBlocksThrow())
            {
                _armed = true;
                hand.Haptic(0.02f, 100f, 0.3f);
            }
            if (_armed && GameContextBlocksThrow())
            {
                _armed = false; // armed, then a menu opened: letting go in the menu must not throw it (85-agent review)
            }
            if (_armed && VRInput.GetReleased(DigitalAction.Fire, hand.Side))
            {
                _armed = false;
                Vector3 v = ThrowableHandler.Released(hand);
                if ((v.magnitude > 1.1f || CanStrap(hand)) && Throw(weapon, hand, v))
                {
                    GripThrowAt = Time.unscaledTime; // the grip let go just after is not a second throw
                }
            }
            // Holding by the grip: opening the hand in a throwing motion lets the grenade go, as in other VR games (a slow
            // release puts it back on the chest instead).
            if (VRConfig.GripToHoldWeapon.Value && hand.GrabReleased && !GameContextBlocksThrow() && Time.unscaledTime - GripThrowAt > 0.5f)
            {
                // A throwing motion against the body (opening the hand while running is not a throw); the grenade then
                // leaves with the hand's full speed, running included. Backing away as you throw, that speed could cancel
                // out: the grenade then fell at your feet with its pin out, so it never leaves slower than the swing itself.
                Vector3 body = hand.BodyThrowVelocity() * VRConfig.ThrowVelocityMultiplier.Value;
                Vector3 v = ThrowableHandler.Released(hand);
                if (v.magnitude < body.magnitude * 0.7f)
                {
                    v = body;
                }
                if (body.magnitude > 1.6f || CanStrap(hand))
                {
                    _armed = false;
                    if (Throw(weapon, hand, v))
                    {
                        // The hand that let go is empty, as in Boneworks: the next grenade waits in its holster. It stayed
                        // "held" in the open hand before (the let-go was spent on the throw), and could not be thrown or
                        // put away without gripping again. A throw the game refused is an ordinary let-go (it goes back).
                        GripThrowAt = Time.unscaledTime;
                        _sys.PutAwayAfterThrow();
                    }
                }
            }
        }

        private static bool GameContextBlocksThrow() => VRGameInput.GunInputBlocked; // a menu, the laser on a panel, or the match paused

        /// <summary>
        /// A live grenade (its pin out, or one without a pin) let go on a body the other hand holds is strapped to it, however
        /// slowly it is let go (a slow let-go elsewhere puts it back on the chest): <see cref="StrappedGrenade"/>.
        /// </summary>
        private bool CanStrap(VRHand hand) => (!_usesPin || _pinPulled) && hand.BodyRelativeVelocity.magnitude < StrapMaxSpeed
                                              && BodyGrab.StrapBone(hand.Palm.position, BodyGrab.StrapReach) != null;

        /// <summary>A let-go slower than this beside a held body straps the grenade to it; faster is a throw (m/s).</summary>
        private const float StrapMaxSpeed = 2.5f;

        /// <returns>Something left the hand (a live grenade or a dud).</returns>
        private bool Throw(ThrowableWeapon weapon, VRHand hand, Vector3 velocity)
        {
            LastLiveCount = 0;
            // The flat game's throw animation states never apply to a throw by hand (the game's own throw, started by a
            // stray fire input, waits for an animation event that may never come and blocked every throw after it).
            weapon.isPreparingToThrowProjectile = false;
            weapon.hasFiredSingleRoundThisTrigger = false;
            if (!weapon.CanFire() || !weapon.unholstered)
            {
                hand.Haptic(0.08f, 40f, 0.4f);
                return false;
            }
            if (_usesPin && !_pinPulled)
            {
                ThrowDud(weapon, hand, velocity);
                return true;
            }
            if (_listening != weapon)
            {
                weapon.onSpawnProjectiles.AddListener(OnSpawned);
                _listening = weapon;
            }
            Transform muzzle = weapon.CurrentMuzzle();
            Vector3 savedPos = muzzle.localPosition;
            Quaternion savedRot = muzzle.localRotation;
            Vector3 dir = velocity.normalized;
            muzzle.SetPositionAndRotation(hand.Palm.position + dir * 0.08f, Quaternion.LookRotation(dir));
            // Let go on a held body: strapped to its nearest bone, not thrown (user, 2026-10-24). Only a let-go, not a throw that
            // starts beside the body the other hand holds: slow against the player's own body, as CanStrap (the world velocity
            // counted walking, so a shield carried along at a walk never took a grenade).
            Actor carrier = null;
            Rigidbody strapTo = hand.BodyRelativeVelocity.magnitude < StrapMaxSpeed ? BodyGrab.StrapBone(hand.Palm.position, BodyGrab.StrapReach, out carrier) : null;
            _spawned.Clear();
            try
            {
                weapon.Shoot(Vector3.zero, true);
            }
            finally
            {
                muzzle.localPosition = savedPos;
                muzzle.localRotation = savedRot;
            }
            foreach (Projectile p in _spawned)
            {
                if (p == null)
                {
                    continue;
                }
                if (strapTo != null && p is GrenadeProjectile)
                {
                    StrappedGrenade.Strap(p, strapTo, carrier, hand.Palm.position);
                }
                else
                {
                    p.velocity = velocity;
                    p.transform.rotation = Quaternion.LookRotation(dir);
                }
                LastLiveCount++;
            }
            _spawned.Clear();
            if (_usesPin && LastLiveCount > 0)
            {
                _sys.Sounds.Play(MechSound.Spoon, hand.Palm.position, 0.8f);
            }
            hand.Haptic(0.04f, 90f, 0.6f);
            if (LastLiveCount > 0)
            {
                ResetPin(); // the next grenade comes out of the pouch with its pin in
            }
            // (Nothing thrown, a script took the throw: the grenade is still in the hand, its pin still pulled. Put back,
            // the next throw of that live grenade came out a dud.)
            return LastLiveCount > 0;
        }

        /// <summary>Pin still in: the grenade leaves the hand as an inert, physical object.</summary>
        private void ThrowDud(ThrowableWeapon weapon, VRHand hand, Vector3 velocity)
        {
            GrenadeDud.Spawn(_pinRig, weapon, hand.Palm.position, hand.Palm.rotation, velocity, hand.AngularVelocity);
            weapon.ammo = Mathf.Max(0, weapon.ammo - 1);
            weapon.InstantlyReload(); // the next one from the pouch, as after a real throw
            weapon.user?.EmoteToss();
            hand.Haptic(0.03f, 70f, 0.4f);
            ResetPin();
        }

        private void OnSpawned(Projectile[] projectiles)
        {
            if (projectiles != null)
            {
                _spawned.AddRange(projectiles);
            }
        }

        // ---- the pin -----------------------------------------------------------------------------------

        private static readonly Regex PinName = new Regex(@"\b(pin|ring|pullring|pinring|pull ring|safety pin)\b");
        // Not a stick grenade's "Handle" or an "Arming" lever: a real pin under one was taken for a finger bone. (GrenadeDud's arm-bone
        // test uses it too.)
        internal static readonly Regex ArmName = new Regex(@"hand(?!le|guard|rail)|arm(?!ature|or|our|ing|ed)|wrist|finger|palm|thumb|elbow|shoulder|clavicle", RegexOptions.IgnoreCase);
        private static readonly Regex NotAGrenade = new Regex(@"bag|kit|c4|charge|mine|claymore|beacon|molotov|cocktail|knife|axe|tomahawk", RegexOptions.IgnoreCase);

        /// <summary>
        /// The velocity a throwable leaves the hand with: the hand's own swing (against the body) times the throw multiplier, and
        /// the body's own motion (walking, a vehicle) as it is. The whole world velocity was multiplied: a grenade let go from a
        /// car at 60 m/s left at about 84 along the car's heading (throw hunt, 2026-10-20).
        /// </summary>
        internal static Vector3 Released(VRHand hand) =>
            hand.ThrowVelocity() + hand.BodyThrowVelocity() * (VRConfig.ThrowVelocityMultiplier.Value - 1f);

        /// <summary>Fused grenades get a pin; bags, kits, charges and other throwables do not.</summary>
        internal static bool HasPin(ThrowableWeapon weapon)
        {
            if (!VRConfig.GrenadePins.Value)
            {
                return false;
            }
            GameObject projectile = weapon.configuration.projectilePrefab;
            return projectile != null && projectile.GetComponent<GrenadeProjectile>() != null && !NotAGrenade.IsMatch(weapon.name);
        }

        private void SetupPin(HeldWeapon held, ThrowableWeapon weapon)
        {
            ResetVisuals();
            _pinFor = weapon;
            _pinRig = held.Rig;
            _usesPin = HasPin(weapon);
            _pinPulled = false;
            _holdingPin = false;
            _modPin = null;
            _pulledPutAway.RemoveWhere(w => w == null);
            bool alreadyPulled = _pulledPutAway.Remove(weapon);
            if (!_usesPin)
            {
                return;
            }
            WeaponRigData d = held.Rig.Data;
            _modPin = FindModPin(weapon.transform);
            if (_modPin != null)
            {
                _modPinScale = _modPin.localScale;
                _pinLocal = held.Rig.ToGun(PartCenter(_modPin));
            }
            else
            {
                // No pin part: a ring at the top of the grenade, on its side.
                Bounds b = GunCollider.VisibleBounds(held.Rig, out bool measured);
                float up = measured ? Mathf.Abs(Vector3.Dot(b.extents, Abs(d.Up))) : 0.05f;
                Vector3 side = Vector3.Cross(d.Up, d.AimAxis).normalized;
                _pinLocal = (measured ? b.center : d.GripPoint) + d.Up * (up * 0.85f) + side * 0.018f;
                if (!alreadyPulled)
                {
                    _ring = PinRing.Create();
                }
            }
            _pinAxisLocal = Vector3.Cross(d.Up, d.AimAxis).normalized;
            if (alreadyPulled)
            {
                // Put away with the pin out: it is still out (pulling it again for a fresh pin was a free reset).
                _pinPulled = true;
                if (_modPin != null)
                {
                    _modPin.localScale = Vector3.one * 1e-4f;
                }
            }
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        internal static Transform FindModPin(Transform root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t == root)
                {
                    continue; // a grenade named "Ring Grenade" was its own pin: pulling it hid the whole grenade
                }
                string name = Regex.Replace(Regex.Replace(t.name, "(?<=[a-z])(?=[A-Z])", " "), "[^A-Za-z]+", " ").ToLowerInvariant();
                if (!PinName.IsMatch(name))
                {
                    continue;
                }
                // Finger bones ("ring_01_l") hang under the hand: anything below an arm bone is not a pin.
                bool underArm = false;
                for (Transform p = t; p != null && p != root; p = p.parent)
                {
                    if (ArmName.IsMatch(p.name))
                    {
                        underArm = true;
                        break;
                    }
                }
                if (!underArm)
                {
                    return t;
                }
            }
            return null;
        }

        private static Vector3 PartCenter(Transform t)
        {
            Renderer r = t.GetComponentInChildren<Renderer>(true);
            return r != null && !(r is SkinnedMeshRenderer) ? r.bounds.center : t.position;
        }

        private Vector3 PinWorld => _pinRig != null && _pinRig.GunBody != null
            ? (_modPin != null && !_pinPulled ? PartCenter(_modPin) : _pinRig.ToWorld(_pinLocal))
            : Vector3.zero;

        private void UpdatePin()
        {
            VRHand off = _sys.Rig.Off;
            if (GameManager.IsPaused())
            {
                if (_holdingPin)
                {
                    // A pause (the dashboard, the headset off) ends the pin grab, as MagnifierFlip ends its hold: a hand moved
                    // while paused must not pull the pin on the first frame back (pause audit, 2026-10-21).
                    _holdingPin = false;
                    off.Role = HandRole.Free;
                }
                return; // the pin is not pulled in a paused match (input hunt, 2026-10-21)
            }
            VRHand dom = _sys.Rig.Dominant;
            if (!_pinPulled)
            {
                // (Where the pin is, last: only on the frame the other hand grips. A modelled pin's centre searches its renderer.)
                if (!_holdingPin && off.Tracked && off.Role == HandRole.Free && off.GrabPressed && _pinRig != null && _pinRig.GunBody != null
                    && Vector3.Distance(off.Palm.position, PinWorld) < PinGrabRadius)
                {
                    VRInput.Consume(DigitalAction.Grab, off.Side);
                    _holdingPin = true;
                    // The pull is measured from where the grip closed, not the pin, and against the grenade (its own frame): in the
                    // world, walking, turning or a snap turn with the grip on the ring moved the palm 5.5 cm and pulled the pin with
                    // no tug (pause audit's pin findings, bug hunt 2026-10-22).
                    _pinGrabAt = _pinRig.ToGun(off.Palm.position);
                    off.Role = HandRole.Pin;
                    off.Haptic(0.015f, 160f, 0.3f);
                }
                if (_holdingPin)
                {
                    if (!off.GrabHeld || !off.Tracked || _pinRig == null || _pinRig.GunBody == null)
                    {
                        // Let go before it came out; or the hand lost tracking (its pose stays where it last was: a jump back
                        // read as a tug), or the grenade went with its body.
                        _holdingPin = false;
                        off.Role = HandRole.Free;
                    }
                    else if (Vector3.Distance(_pinRig.ToGun(off.Palm.position), _pinGrabAt) > PinPullDistance)
                    {
                        // An actual tug of the hand, so a grip that closed 5.5-7 cm from the pin does not pop it the same frame.
                        PullPin(off);
                    }
                }
                if (!_pinPulled && VRInput.GetPressed(DigitalAction.SlideRelease, dom.Side) && !GameContextBlocksThrow())
                {
                    VRInput.Consume(DigitalAction.SlideRelease, dom.Side);
                    PullPin(null); // one-handed: the thumb flicks the ring off
                }
            }
            if (_ringHand != null && (!_ringHand.GrabHeld || !_ringHand.Tracked))
            {
                DropRing(_ringHand.ThrowVelocity());
            }
        }

        /// <summary>Pin out: the grenade is ready, the fuse waits for the throw.</summary>
        internal void PullPin(VRHand into)
        {
            if (!_usesPin || _pinPulled)
            {
                return;
            }
            Vector3 at = PinWorld;
            _pinPulled = true;
            _holdingPin = false;
            if (_modPin != null)
            {
                // The piece that comes away is the grenade's own pin; the one on the grenade disappears with it.
                _ring = PinRing.CreateFrom(_modPin, at);
                _modPin.localScale = Vector3.one * 1e-4f;
            }
            if (_ring == null)
            {
                _ring = PinRing.Create();
            }
            _ring.transform.position = at;
            _sys.Sounds.Play(MechSound.PinPull, at, 0.7f);
            _sys.Rig.Dominant.Haptic(0.03f, 140f, 0.45f);
            if (into != null)
            {
                into.Role = HandRole.Pin;
                into.Haptic(0.04f, 180f, 0.6f);
                _ringHand = into;
            }
            else
            {
                DropRing(_sys.Rig.Dominant.Velocity + _sys.Rig.Dominant.Palm.Up * 1.2f);
            }
        }

        private void DropRing(Vector3 velocity)
        {
            if (_ringHand != null && _ringHand.Role == HandRole.Pin)
            {
                _ringHand.Role = HandRole.Free;
            }
            _ringHand = null;
            if (_ring == null)
            {
                return;
            }
            PinRing.Drop(_ring, velocity);
            _ring = null;
        }

        /// <summary>Called from BeforeRender: the ring sits in the fuse, or in the hand that pulled it.</summary>
        public void BeforeRender()
        {
            // The grenade's animator writes the pin part's transform every frame: keep it hidden once pulled.
            if (_modPin != null && _pinPulled)
            {
                _modPin.localScale = Vector3.one * 1e-4f;
            }
            if (_ring == null || _pinRig == null || _pinRig.GunBody == null)
            {
                return;
            }
            if (_ringHand != null)
            {
                RigidPose palm = _ringHand.Palm;
                _ring.transform.SetPositionAndRotation(palm.position + palm.Forward * 0.035f, palm.rotation);
            }
            else if (!_pinPulled)
            {
                _ring.transform.SetPositionAndRotation(_pinRig.ToWorld(_pinLocal),
                    _pinRig.GunBody.rotation * OpenVRMath.SafeLookRotation(_pinAxisLocal, _pinRig.Data.AimAxis));
            }
        }

        /// <summary>A fresh grenade in hand: pin back in.</summary>
        private void ResetPin()
        {
            if (_ringHand != null)
            {
                DropRing(_ringHand.Velocity);
            }
            else if (_ring != null && _pinPulled)
            {
                DropRing(Vector3.zero);
            }
            _pinPulled = false;
            _holdingPin = false;
            if (_modPin != null)
            {
                _modPin.localScale = _modPinScale;
            }
            else if (_usesPin && _ring == null)
            {
                _ring = PinRing.Create();
            }
            VRHand off = _sys.Rig.Off;
            if (off.Role == HandRole.Pin && _ringHand == null)
            {
                off.Role = HandRole.Free;
            }
        }

        private void ResetVisuals()
        {
            if (_ringHand != null)
            {
                DropRing(_ringHand.Velocity);
            }
            if (_ring != null)
            {
                Object.Destroy(_ring);
                _ring = null;
            }
            if (_modPin != null)
            {
                _modPin.localScale = _modPinScale;
            }
            VRHand off = _sys.Rig.Off;
            if (off.Role == HandRole.Pin)
            {
                off.Role = HandRole.Free;
            }
            _holdingPin = false;
        }

        /// <summary>
        /// Autotest: a throw with the pin in must not spawn a live grenade (a dud lands instead and picking it up refunds
        /// it); with the pin pulled the same throw must spawn one.
        /// </summary>
        internal IEnumerator SelfCheck(HeldWeapon held, System.Action<string> report)
        {
            var weapon = held.Weapon as ThrowableWeapon;
            if (weapon == null)
            {
                yield break;
            }
            if (_pinFor != weapon)
            {
                SetupPin(held, weapon);
            }
            if (!_usesPin)
            {
                report("throwable without a pin");
                yield break;
            }
            float until = Time.realtimeSinceStartup + 3f;
            while ((!weapon.unholstered || !weapon.CanFire()) && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
            if (!weapon.unholstered || !weapon.CanFire())
            {
                // The game will not let it be thrown now (a call-in on cooldown, none left): nothing to test.
                report("pin check skipped: the game does not let this one be thrown right now");
                yield break;
            }
            VRHand hand = _sys.Rig.Dominant;
            Transform head = _sys.Rig.Head;
            Vector3 lob = (Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized + Vector3.up).normalized * 16f;
            int before = weapon.ammo + weapon.spareAmmo;
            int duds = GrenadeDud.Count;
            Throw(weapon, hand, lob);
            bool dud = GrenadeDud.Count == duds + 1 && LastLiveCount == 0;
            bool refunded = GrenadeDud.RefundLast(weapon.user) && weapon.ammo + weapon.spareAmmo == before;
            report($"pin in: {(dud ? "dud, no live grenade" : "LIVE GRENADE WITH THE PIN IN")}, picked up again: {(refunded ? "refunded" : "NOT REFUNDED")}");
            until = Time.realtimeSinceStartup + 3f;
            while (!weapon.CanFire() && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
            if (!weapon.CanFire())
            {
                report("pin pulled: skipped (the game does not let another one be thrown yet)");
                yield break;
            }
            PullPin(null);
            Throw(weapon, hand, lob);
            report($"pin pulled: {(LastLiveCount > 0 ? "live grenade thrown" : "NO GRENADE THROWN")}");
        }

        /// <summary>
        /// Autotest: the grenade in the hand let go where the hand is, its pin pulled first (the strap check: let go on a held
        /// body, it must be strapped to it). Returns how many live grenades left the hand.
        /// </summary>
        internal int TestLetGoLive(HeldWeapon held)
        {
            var weapon = held != null ? held.Weapon as ThrowableWeapon : null;
            if (weapon == null)
            {
                return 0;
            }
            if (_pinFor != weapon)
            {
                SetupPin(held, weapon);
            }
            if (_usesPin && !_pinPulled)
            {
                PullPin(null);
            }
            return Throw(weapon, _sys.Rig.Dominant, Vector3.zero) ? LastLiveCount : 0;
        }

        /// <summary>Death or a new map: the grenades drawn next are fresh ones, pins in.</summary>
        public void ForgetPulledPins() => _pulledPutAway.Clear();

        /// <summary>The free hand put a grenade away with its pin out: drawn again (either hand), it is still pulled.</summary>
        internal void PutAwayPulled(ThrowableWeapon weapon)
        {
            if (weapon != null)
            {
                _pulledPutAway.Add(weapon);
            }
        }

        /// <summary>Whether this grenade was put away with its pin out (the mark is taken: it is in a hand again).</summary>
        internal bool TakePulled(ThrowableWeapon weapon) => weapon != null && _pulledPutAway.Remove(weapon);

        public void Release()
        {
            _armed = false;
            if (_listening != null)
            {
                _listening.onSpawnProjectiles.RemoveListener(OnSpawned);
                _listening = null;
            }
            // Put away with the pin out: it stays out, and is still out when the grenade is drawn again.
            if (_pinPulled && _usesPin && _pinFor != null)
            {
                _pulledPutAway.Add(_pinFor);
            }
            ResetVisuals();
            _pinFor = null;
            _pinRig = null;
            _pinPulled = false;
            VRGameInput.SuppressFire = false;
        }
    }

    /// <summary>
    /// Physical melee: a fast enough sweep of the blade sphere-casts along the path the blade travelled this
    /// frame and applies the weapon's own damage, sounds and forces to whatever it cut through.
    /// </summary>
    public sealed class MeleeHandler
    {
        private readonly WeaponSystem _sys;
        private Vector3 _lastTip, _lastBase;
        /// <summary>The last tip in the rig's own frame: where it would be now had it only moved with the player.</summary>
        private Vector3 _lastTipInRig;
        private bool _hasLast;
        /// <summary>A blade that moved this far in one frame was carried (a teleport, a ladder, a seat), not swung.</summary>
        private const float MaxSweep = 1.5f;
        /// <summary>The game's own melee mask (MeleeWeapon.ShootMeleeRay): the hitbox layers, the default layer and layer 12.</summary>
        private const int GameMeleeMask = 70913;
        /// <summary>The game's first-person layer (the drawn weapon and arms), never a wall a blow stops at.</summary>
        private const int FirstPersonLayer = 18;
        /// <summary>No fist, gun or held thing hits below this (m/s), whatever the knife's own setting: a walk is no blow.</summary>
        private const float MinBlowSpeed = 2.2f;

        /// <summary>
        /// How fast a fist, a swung gun or a held thing must move against the body to hit (and the speed over which a hand
        /// reaching for a soldier punches instead of grabbing): the knife's setting, at least <see cref="MinBlowSpeed"/>.
        /// </summary>
        internal static float BlowSpeed => Mathf.Max(MinBlowSpeed, VRConfig.MeleeSwingSpeed.Value);
        private float _lastHit = -10f;
        private float _bladeLength = -1f;
        private Vector3 _bladeDir = Vector3.forward;
        private Weapon _weapon;

        public MeleeHandler(WeaponSystem sys)
        {
            _sys = sys;
        }

        /// <summary>Samples the blade after placement; hits are resolved here (render time) so the sweep matches what you saw.</summary>
        public void BeforeRender(HeldWeapon held)
        {
            var melee = held.Weapon as MeleeWeapon;
            if (melee == null)
            {
                _hasLast = false;
                return;
            }
            if (_weapon != melee)
            {
                _weapon = melee;
                MeasureBlade(held.Rig, out _bladeDir, out _bladeLength);
                _hasLast = false;
            }
            if (!_sys.Rig.Dominant.Tracked)
            {
                // Tracking lost: the frame it comes back the hand's jump to where it is is no swing (up to 1.4 m in a frame read
                // as a fast one and cut along the jump; throw hunt, 2026-10-20). That frame only takes the pose.
                _hasLast = false;
                return;
            }
            WeaponRig rig = held.Rig;
            Vector3 basePoint = rig.ToWorld(rig.Data.GripPoint + _bladeDir * Mathf.Min(0.08f, _bladeLength * 0.3f));
            Vector3 tip = rig.ToWorld(rig.Data.GripPoint + _bladeDir * _bladeLength);
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            Transform body = _sys.Rig.Root;
            if (_hasLast && !NoFrameTime)
            {
                // The swing is the blade's speed against the player's own body: running (about 6 m/s) or turning with the
                // stick with a knife held still counted as swinging it (2.6 m/s needed) and cut whatever you ran into.
                float speed = (tip - body.TransformPoint(_lastTipInRig)).magnitude / dt;
                float cooldown = Mathf.Max(0.28f, melee.configuration.cooldown * 0.8f);
                bool carried = (tip - _lastTip).sqrMagnitude > MaxSweep * MaxSweep; // the hit test would span the whole jump
                if (!carried && speed > VRConfig.MeleeSwingSpeed.Value && Time.time - _lastHit > cooldown && !VRGameInput.GunInputBlocked)
                {
                    if (Swing(melee, _lastTip, tip, _lastBase, basePoint))
                    {
                        _lastHit = Time.time;
                    }
                }
            }
            _lastTip = tip;
            _lastTipInRig = body.InverseTransformPoint(tip);
            _lastBase = basePoint;
            _hasLast = true;
        }

        /// <summary>
        /// The blade (or head) of a melee weapon, from the grip to the farthest point of its own shape. Swords, axes and
        /// wrenches are rarely built with the blade along the "aim" axis of the rig: a sabre held blade-up would otherwise
        /// test its hits at a point floating in front of the hand.
        /// </summary>
        internal static void MeasureBlade(WeaponRig rig, out Vector3 dirGun, out float length)
        {
            WeaponRigData d = rig.Data;
            Quaternion frame = OpenVRMath.SafeLookRotation(d.AimAxis, d.Up);
            dirGun = d.AimAxis;
            length = 0.25f;
            float best = 0f;
            foreach (ShapeFit.Piece piece in GunCollider.Fit(rig, frame, d.GripPoint))
            {
                foreach (Vector3 p in piece.Points)
                {
                    float dist = p.magnitude;
                    if (dist > best)
                    {
                        best = dist;
                        dirGun = frame * (p / Mathf.Max(dist, 1e-5f));
                    }
                }
            }
            if (best > 0.05f)
            {
                length = Mathf.Clamp(best, 0.12f, 1.8f);
            }
        }

        private bool Swing(MeleeWeapon melee, Vector3 fromTip, Vector3 toTip, Vector3 fromBase, Vector3 toBase)
        {
            Actor user = melee.user;
            if (user == null)
            {
                return false;
            }
            Vector3 sweep = toTip - fromTip;
            Vector3 dir = sweep.normalized;
            float radius = Mathf.Clamp(melee.radius * 0.5f, 0.06f, 0.3f);
            Vector3 midFrom = Vector3.Lerp(fromBase, fromTip, 0.5f);
            Vector3 midTo = Vector3.Lerp(toBase, toTip, 0.5f);
            // Nothing but the player in the blade's way (most fast frames of a swing through the air): no cast. The game's cast
            // needs the player's own hitboxes switched off around it, and switching colliders off and on is paid in the physics
            // scene: it ran on every fast frame of a swing that hit nothing (review, 2026-10-24).
            if (!PathHasTarget(user, new Ray(fromTip, dir), sweep.magnitude + 0.05f, radius)
                && !PathHasTarget(user, new Ray(midFrom, (midTo - midFrom).normalized), (midTo - midFrom).magnitude + 0.05f, radius))
            {
                return false;
            }
            bool disabled = user.DisableHitboxColliders();
            bool hitSomething = false;
            try
            {
                // Two casts: along the tip's path and along the middle of the blade.
                hitSomething = Cast(melee, new Ray(fromTip, dir), sweep.magnitude + 0.05f, radius, dir)
                               || Cast(melee, new Ray(midFrom, (midTo - midFrom).normalized), (midTo - midFrom).magnitude + 0.05f, radius, dir);
            }
            finally
            {
                if (disabled)
                {
                    user.EnableHitboxColliders();
                }
            }
            if (hitSomething)
            {
                _sys.Rig.Dominant.Haptic(0.08f, 70f, 1f);
                user.PlaySwingAnimation();
            }
            return hitSomething;
        }

        private static readonly RaycastHit[] PathHits = new RaycastHit[16];

        /// <summary>
        /// Whether a blade's path (as <see cref="Cast"/> sweeps it, the game's own back-off included) touches anything but the
        /// player: another soldier, a wall, a vehicle, a loose body. The player's own hitboxes and body are left out by owner.
        /// </summary>
        private static bool PathHasTarget(Actor user, Ray ray, float distance, float radius)
        {
            float back = radius * 2f;
            int n = Physics.SphereCastNonAlloc(new Ray(ray.origin - ray.direction * back, ray.direction), radius, PathHits, distance + back, GameMeleeMask);
            for (int i = 0; i < n; i++)
            {
                Collider c = PathHits[i].collider;
                if (c == null || PropPhysics.IsPlayerCollider(c) || c.gameObject.layer == FirstPersonLayer)
                {
                    continue;
                }
                if (Hitbox.IsHitboxLayer(c.gameObject.layer) && BodyGrab.HurtableOf(c) == user)
                {
                    continue;
                }
                return true;
            }
            return n >= PathHits.Length; // (a full buffer: cast to be sure)
        }

        private static bool Cast(MeleeWeapon melee, Ray ray, float distance, float radius, Vector3 direction)
        {
            melee.SphereCast(ray, radius, distance, GameMeleeMask, out RaycastHit hitWall, out MeleeWeapon.RaycastHitHitbox hitEnemy,
                out MeleeWeapon.RaycastHitHitbox hitAlly, out RaycastHit hitVehicle);
            float blocking = Mathf.Min(hitWall.collider == null || BlocksBlow(hitWall.collider) ? hitWall.distance : float.MaxValue, hitVehicle.distance);
            if (hitEnemy.hitInfo.collider != null && hitEnemy.hitInfo.distance < blocking)
            {
                melee.DamageHitbox(direction, hitEnemy);
                return true;
            }
            if (hitAlly.hitInfo.collider != null && hitAlly.hitInfo.distance < blocking)
            {
                melee.DamageHitbox(direction, hitAlly);
                return true;
            }
            if (hitVehicle.collider != null)
            {
                melee.HitVehicle(direction, hitVehicle);
                return true;
            }
            if (hitWall.collider != null)
            {
                Rigidbody body = hitWall.collider.attachedRigidbody;
                Destructible destructible = hitWall.collider.GetComponentInParent<Destructible>();
                if (body != null)
                {
                    melee.HitRigidbody(direction, body, hitWall);
                }
                if (destructible != null)
                {
                    melee.HitDestructible(direction, hitWall, destructible);
                }
                return body != null || destructible != null;
            }
            return false;
        }

        public void Release()
        {
            _hasLast = false;
            _weapon = null;
            _bashHas = false;
        }

        // ---- a gun swung into someone ---------------------------------------------------------------

        /// <summary>
        /// A frame with no time (reported as 0): no swing is measured on it, only the samples refreshed. Its speed divided by the
        /// floored dt read a 0.3 mm change as 3 m/s, a swing (throw/melee scout, 2026-10-23).
        /// </summary>
        private static bool NoFrameTime => Time.unscaledDeltaTime < 1e-4f;

        /// <summary>Points along the gun from its butt (0) to its muzzle (last), each swept from the last frame (world and body space).</summary>
        private const int MaxBashPoints = 12;
        private readonly Vector3[] _bashAt = new Vector3[MaxBashPoints], _bashInRig = new Vector3[MaxBashPoints];
        private int _bashCount;
        private bool _bashHas;
        private Weapon _bashWeapon;
        private string _bashName; // the weapon's name, read once: Object.name makes a new string each read, per point per fast frame
        private float _lastBash = -10f;
        private static readonly RaycastHit[] BashHits = new RaycastHit[16];

        /// <summary>
        /// A gun (not a melee weapon) swung into an enemy hits them, with its muzzle end or its butt: a muzzle strike or a
        /// butt stroke (user, 2026-10-16: "hitting enemies with weapons doesn't do damage"). Its speed against the player's
        /// own body, as a knife's (walking or turning is no swing); the harder and the heavier, the more damage. Once every
        /// half second at most.
        /// </summary>
        public void Bash(HeldWeapon held)
        {
            Weapon w = held.Weapon;
            Actor user = w != null ? w.user : null;
            WeaponRig rig = held.Rig;
            if (user == null || rig.GunBody == null || held.Rig.Data.Action == ActionType.Throwable)
            {
                _bashHas = false;
                return;
            }
            if (_bashWeapon != w)
            {
                _bashWeapon = w;
                _bashName = w.name;
                _bashHas = false;
            }
            if (!_sys.Rig.Dominant.Tracked)
            {
                _bashHas = false; // as the blade: the jump back into tracking is no bash
                return;
            }
            WeaponRigData d = rig.Data;
            Vector3 muzzle = rig.ToWorld(d.MuzzlePoint);
            // The butt: the stock's end on a long gun (where the drawn model ends: a rifle's stock runs 30 to 35 cm behind the grip,
            // and a butt stroke with its last 10 cm hit nothing at a fixed 20), the grip's heel on a handgun.
            Vector3 butt = rig.ToWorld(held.StockButt ?? (d.Handgun ? d.GripPoint - d.Up * 0.04f : d.GripPoint - d.AimAxis * 0.2f));
            Transform body = _sys.Rig.Root;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            // Points at most 10 cm apart (4 to 12), from the butt to the muzzle: four on a rifle were 27 cm apart, and a head or an arm
            // passing between their 12 cm sweeps took no hit (user, 2026-10-24: "I cannot punch them with parts of my weapons").
            int count = Mathf.Clamp(Mathf.CeilToInt(Vector3.Distance(butt, muzzle) / 0.1f) + 1, 4, MaxBashPoints);
            if (count != _bashCount)
            {
                _bashCount = count;
                _bashHas = false; // (the samples were of another spacing)
            }
            // Points along the gun, from its butt to its muzzle, each swept on its own: with the muzzle's tip and the butt alone,
            // a swing that landed with the middle of the gun (a handguard, a magazine, a receiver) hit nothing, and a pistol punch
            // needed its muzzle over 3.1 m/s (user, 2026-10-19 night: "when i punch things with objects such as weapons they dont
            // take damage", task 306). The knife's speed now (MeleeSwingSpeed, 2.6 m/s): a swing, not a walk or a turn.
            // (Not with the laser on one of the mod's panels either, as the trigger: review, 2026-10-20.)
            if (_bashHas && !NoFrameTime && Time.time - _lastBash > 0.5f && Time.time - _lastBlow > 0.5f && !VRGameInput.GunInputBlocked)
            {
                bool carried = (muzzle - _bashAt[count - 1]).sqrMagnitude > MaxSweep * MaxSweep;
                float need = BlowSpeed;
                int near = -1; // a soldier anywhere the gun swept this frame: asked once, at its first fast point
                for (int k = count - 1; k >= 0 && !carried; k--)
                {
                    Vector3 at = Vector3.Lerp(butt, muzzle, k / (float)(count - 1));
                    float speed = (at - body.TransformPoint(_bashInRig[k])).magnitude / dt;
                    if (speed <= need)
                    {
                        continue;
                    }
                    if (near < 0)
                    {
                        // One overlap round the whole gun, as wide as its farthest point moved (a straight gun's points move
                        // least to most from one end to the other), instead of up to 12 sweeps through the air on every fast
                        // frame of a swing that meets nobody (review, 2026-10-24).
                        float moved = Mathf.Max((muzzle - _bashAt[count - 1]).magnitude, (butt - _bashAt[0]).magnitude);
                        near = SoldierNear(user, butt, muzzle, moved + BashRadius * 3f) ? 1 : 0;
                    }
                    if (near == 0)
                    {
                        break;
                    }
                    if (BashCast(w, user, _bashAt[k], at, speed, d.Handgun ? 0.6f : d.IsMachineGun ? 1.4f : 1f, _bashName, d.Key))
                    {
                        _lastBash = Time.time;
                        _sys.Rig.Dominant.Haptic(0.1f, 60f, 1f);
                        if (held.TwoHanded)
                        {
                            _sys.Rig.Off.Haptic(0.08f, 60f, 0.8f);
                        }
                        held.Nudge(-(at - _bashAt[k]).normalized * 0.6f, 20f); // the blow jars the gun back
                        break;
                    }
                }
            }
            for (int k = 0; k < count; k++)
            {
                Vector3 at = Vector3.Lerp(butt, muzzle, k / (float)(count - 1));
                _bashAt[k] = at;
                _bashInRig[k] = body.InverseTransformPoint(at);
            }
            _bashHas = true;
        }

        // ---- a magazine or a grenade swung into someone --------------------------------------------------

        private readonly Vector3[] _blowAt = new Vector3[2], _blowInRig = new Vector3[2];
        private readonly object[] _blowHeld = new object[2];
        private float _lastBlow = -10f;
        private readonly float[] _lastBlowBy = { -10f, -10f };

        /// <summary>When each hand last landed a blow (game time): a fist that just hit does not then take hold of whom it hit (BodyGrab).</summary>
        internal static readonly float[] LastHandBlow = { -10f, -10f };
        // What each held thing is called in the log and its once-key, made once (built every fast frame, hit or not): PropKind's
        // order, then the grenade, then an empty fist.
        private static readonly string[] BlowLabels = { "a held magazine", "a held shell", "a held rocket", "a held grenade", "a fist" };
        private static readonly string[] BlowKeys = { "blow:magazine", "blow:shell", "blow:rocket", "blow:grenade", "blow:fist" };
        private const int GrenadeBlow = 3, FistBlow = 4;
        private static readonly object[] Fists = { new object(), new object() }; // what an empty fist "holds", one a hand

        /// <summary>
        /// What a hand holds besides a gun, swung into an enemy, hits them lightly: a magazine, shell or rocket in the hand, or
        /// a grenade held to throw ("hitting things with held objects does no damage": only guns and blades hit; user,
        /// 2026-10-19 night, task 306). The palm's path, swept as a gun's points are, at the same speed against the player's
        /// own body (a swing, not a walk or a turn); once every half second at most. The damage is credited to the gun in use.
        /// An empty hand closed into a fist punches the same way, every 0.3 s a hand, credited to the player alone.
        /// </summary>
        public void HandBlows(HeldWeapon held)
        {
            Actor user = Util.GameContext.PlayerActor;
            Weapon source = held != null && held.Weapon != null ? held.Weapon : user != null ? user.activeWeapon : null;
            // (No weapon needed for a fist: the damage is the player's own then.)
            bool canFist = user != null && !user.dead && Util.GameContext.PlayerOnFoot && !VRGameInput.GunInputBlocked;
            bool can = canFist && source != null;
            Transform body = _sys.Rig.Root;
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            for (int i = 0; i < 2; i++)
            {
                VRHand h = _sys.Rig.Hands[i];
                // An empty hand closed into a fist punches (user, 2026-10-24: "I cannot punch them with my hands if they are fists";
                // Boneworks' hands are physical, and their impacts are blunt attacks): a free hand with its grip closed.
                bool fist = canFist && h != null && h.Tracked && h.Role == HandRole.Free && h.Held == null && VRInput.GetGrip(h.Side) > 0.6f;
                object what = fist ? Fists[i]
                    : !can || h == null || !h.Tracked ? null
                    : h.Held is MagazineProp prop && prop.Go != null ? prop
                    : h == _sys.Rig.Dominant && held != null && held.Weapon != null && held.Rig.Data.Action == ActionType.Throwable ? (object)held.Weapon
                    : null;
                if (what == null)
                {
                    _blowHeld[i] = null;
                    continue;
                }
                Vector3 at = fist ? h.PalmCentre : h.Palm.position; // a fist strikes with its knuckles, round the fist's hollow
                if (RecordNotes && fist)
                {
                    float measured = (at - body.TransformPoint(_blowInRig[i])).magnitude / dt;
                    FistNote = $"same fist {ReferenceEquals(what, _blowHeld[i])}, {Time.time - _lastBlowBy[i]:0.00} s since its blow, "
                               + $"{Time.time - _lastBash:0.00} s since a bash, moved {(at - _blowAt[i]).magnitude * 100f:0} cm at {measured:0.0} m/s";
                }
                // Each hand on its own clock (a left-right combination), a fist sooner again than a swung magazine.
                if (ReferenceEquals(what, _blowHeld[i]) && !NoFrameTime && Time.time - _lastBlowBy[i] > (fist ? 0.3f : 0.5f) && Time.time - _lastBash > 0.5f
                    && (at - _blowAt[i]).sqrMagnitude < MaxSweep * MaxSweep)
                {
                    float speed = (at - body.TransformPoint(_blowInRig[i])).magnitude / dt;
                    if (speed > BlowSpeed)
                    {
                        int kind = fist ? FistBlow : what is MagazineProp m ? (int)m.Kind : GrenadeBlow;
                        float weight = fist ? 0.4f : what is MagazineProp ? 0.45f : 0.55f;
                        if (BashCast(fist ? null : source, user, _blowAt[i], at, speed, weight, BlowLabels[kind], BlowKeys[kind]))
                        {
                            _lastBlow = Time.time;
                            _lastBlowBy[i] = Time.time;
                            LastHandBlow[i] = Time.time;
                            h.Haptic(0.08f, 60f, 0.9f);
                        }
                    }
                }
                _blowHeld[i] = what;
                _blowAt[i] = at;
                _blowInRig[i] = body.InverseTransformPoint(at);
            }
        }

        // weight: a pistol 0.6 (about a knife's light blow), a rifle 1, a machine gun 1.4; a held magazine or grenade less.
        /// <summary>Autotest: notes on the last fist frame and the last sweep, kept while set (why a blow struck or not).</summary>
        internal static bool RecordNotes;
        internal static string FistNote = "", CastNote = "";

        /// <summary>The radius of a blow's sweep (a gun's points, a fist, a held thing).</summary>
        private const float BashRadius = 0.06f;
        private static readonly Collider[] NearBuffer = new Collider[32];

        /// <summary>
        /// Whether a soldier a blow could hurt (alive, of another team, not the player) has a collider within a capsule round a
        /// line: none, and no sweep along that line can hit one.
        /// </summary>
        private static bool SoldierNear(Actor user, Vector3 lineFrom, Vector3 lineTo, float radius)
        {
            int n = Physics.OverlapCapsuleNonAlloc(lineFrom, lineTo, radius, NearBuffer, Hitbox.ALL_HITBOX_LAYER_MASK, QueryTriggerInteraction.Collide);
            if (n >= NearBuffer.Length)
            {
                return true; // (a full buffer: sweep to be sure)
            }
            for (int i = 0; i < n; i++)
            {
                Collider c = NearBuffer[i];
                Hurtable h = c != null ? BodyGrab.HurtableOf(c) : null;
                if (h != null && h != user && !(h is Actor soldier && soldier.dead) && h.team != user.team)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether something a blow meets on its way stops it: a wall, a vehicle, a loose thing. Never the player's own body, hands
        /// or what they hold (the held gun's stand-in, a magazine in a hand), and never a soldier's own gear: a mod's soldier with
        /// colliders on his helmet, vest or pack, or a gun with colliders of its own, took no blow at all, fist, gun or knife
        /// (user, 2026-10-25: "I cannot punch enemies with things or my fists"; not one blow in the session's log).
        /// </summary>
        internal static bool BlocksBlow(Collider c)
        {
            if (c == null || c.gameObject.layer == FirstPersonLayer || PropPhysics.IsPlayerCollider(c))
            {
                return false;
            }
            Rigidbody body = c.attachedRigidbody;
            if (body != null && body.GetComponent<GunProxyBody>() != null)
            {
                return false;
            }
            if (c.GetComponentInParent<Actor>() != null)
            {
                return false; // (a soldier's: the soldier is what it hits)
            }
            VRRig rig = VRRig.Instance;
            if (rig != null)
            {
                foreach (VRHand h in rig.Hands)
                {
                    if (h != null && h.Held is MagazineProp prop && prop.Go != null && c.transform.IsChildOf(prop.Go.transform))
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        /// <summary>A collider as the log names it: its object, kind, layer and the root it is under.</summary>
        private static string Describe(Collider c) => c == null ? "nothing"
            : $"{c.name} ({c.GetType().Name}, layer {c.gameObject.layer}, under {c.transform.root.name}{(c.attachedRigidbody != null ? c.attachedRigidbody.isKinematic ? ", kinematic" : ", dynamic" : "")})";

        private static bool BashCast(Weapon w, Actor user, Vector3 from, Vector3 to, float speed, float weight, string label, string key)
        {
            Vector3 sweep = to - from;
            float length = sweep.magnitude;
            if (length < 1e-4f)
            {
                return false;
            }
            Vector3 dir = sweep / length;
            // The player's own hitboxes are skipped by their owner below, not switched off and on every fast frame (255.P7-1).
            try
            {
                const float radius = BashRadius;
                int n = Physics.SphereCastNonAlloc(new Ray(from - dir * radius, dir), radius, BashHits, length + radius * 2f, GameMeleeMask);
                Hurtable best = null;
                RaycastHit bestHit = default;
                float bestDistance = float.MaxValue, wall = float.MaxValue;
                Collider wallHit = null;
                for (int i = 0; i < n; i++)
                {
                    RaycastHit h = BashHits[i];
                    if (h.collider == null)
                    {
                        continue;
                    }
                    if (!Hitbox.IsHitboxLayer(h.collider.gameObject.layer))
                    {
                        // A wall or vehicle in the way stops the blow, as a knife's (an enemy behind a wall was hit; 255.B3-2).
                        if (h.distance < wall && BlocksBlow(h.collider))
                        {
                            wall = h.distance;
                            wallHit = h.collider;
                        }
                        continue;
                    }
                    if (h.distance >= bestDistance)
                    {
                        continue;
                    }
                    Hurtable target = BodyGrab.HurtableOf(h.collider);
                    if (target == null || target == user || target is Actor a && a.dead || target.team == user.team)
                    {
                        continue;
                    }
                    best = target;
                    bestHit = h;
                    bestDistance = h.distance;
                }
                if (RecordNotes)
                {
                    CastNote = $"{n} hit(s) over {length * 100f:0} cm, nearest soldier {(best != null ? best.name + $" at {bestDistance * 100f:0} cm" : "none")}, "
                               + $"nearest wall {(wallHit != null ? $"{Describe(wallHit)} at {wall * 100f:0} cm" : "none")}";
                }
                if (best != null && bestDistance > wall && !Log.Logged("blow-walled"))
                {
                    // (Once a session, for the log: what stopped a blow that was on its way to a soldier.)
                    Log.InfoOnce("blow-walled", $"{label}: a blow at {best.name} was stopped by {Describe(wallHit)} {(bestDistance - wall) * 100f:0} cm before him.");
                }
                if (best == null || bestDistance > wall)
                {
                    return false;
                }
                var info = new DamageInfo(DamageInfo.DamageSourceType.Melee, user, w)
                {
                    healthDamage = Mathf.Clamp(speed * 9f * weight, 20f, 90f),
                    balanceDamage = Mathf.Clamp(speed * 30f * weight, 60f, 250f),
                    point = bestHit.point.sqrMagnitude > 0f ? bestHit.point : to,
                    direction = dir,
                    impactForce = dir * (150f * weight * Mathf.Min(speed, 8f))
                };
                best.Damage(info);
                if (!Log.Logged("bash:" + key))
                {
                    Log.InfoOnce("bash:" + key, $"{label}: swung into {best.name} at {speed:0.0} m/s, {info.healthDamage:0} damage.");
                }
                return true;
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("gun bash", e);
                return false;
            }
        }
    }
}
