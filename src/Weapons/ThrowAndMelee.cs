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
            if (_armed && VRInput.GetReleased(DigitalAction.Fire, hand.Side))
            {
                _armed = false;
                Vector3 v = hand.ThrowVelocity() * VRConfig.ThrowVelocityMultiplier.Value;
                if (v.magnitude > 1.1f && Throw(weapon, hand, v))
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
                Vector3 v = hand.ThrowVelocity() * VRConfig.ThrowVelocityMultiplier.Value;
                if (v.magnitude < body.magnitude * 0.7f)
                {
                    v = body;
                }
                if (body.magnitude > 1.6f)
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

        private static bool GameContextBlocksThrow() => Util.GameContext.GameMenuOpen || VRGameInput.PanelOpen && VRGameInput.PointerOverUI;

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
                p.velocity = velocity;
                p.transform.rotation = Quaternion.LookRotation(dir);
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
        private static readonly Regex ArmName = new Regex(@"hand|arm(?!ature|or)|wrist|finger|palm|thumb|elbow|shoulder|clavicle", RegexOptions.IgnoreCase);
        private static readonly Regex NotAGrenade = new Regex(@"bag|kit|c4|charge|mine|claymore|beacon|molotov|cocktail|knife|axe|tomahawk", RegexOptions.IgnoreCase);

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

        private static Transform FindModPin(Transform root)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
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
            VRHand dom = _sys.Rig.Dominant;
            if (!_pinPulled)
            {
                Vector3 pin = PinWorld;
                if (!_holdingPin && off.Tracked && off.Role == HandRole.Free && off.GrabPressed
                    && Vector3.Distance(off.Palm.position, pin) < PinGrabRadius)
                {
                    VRInput.Consume(DigitalAction.Grab, off.Side);
                    _holdingPin = true;
                    off.Role = HandRole.Pin;
                    off.Haptic(0.015f, 160f, 0.3f);
                }
                if (_holdingPin)
                {
                    if (!off.GrabHeld)
                    {
                        _holdingPin = false; // let go before it came out
                        off.Role = HandRole.Free;
                    }
                    else if (Vector3.Distance(off.Palm.position, pin) > PinPullDistance)
                    {
                        PullPin(off);
                    }
                }
                if (!_pinPulled && VRInput.GetPressed(DigitalAction.SlideRelease, dom.Side))
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

        /// <summary>Death or a new map: the grenades drawn next are fresh ones, pins in.</summary>
        public void ForgetPulledPins() => _pulledPutAway.Clear();

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
            WeaponRig rig = held.Rig;
            Vector3 basePoint = rig.ToWorld(rig.Data.GripPoint + _bladeDir * Mathf.Min(0.08f, _bladeLength * 0.3f));
            Vector3 tip = rig.ToWorld(rig.Data.GripPoint + _bladeDir * _bladeLength);
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            Transform body = _sys.Rig.Root;
            if (_hasLast)
            {
                // The swing is the blade's speed against the player's own body: running (about 6 m/s) or turning with the
                // stick with a knife held still counted as swinging it (2.6 m/s needed) and cut whatever you ran into.
                float speed = (tip - body.TransformPoint(_lastTipInRig)).magnitude / dt;
                float cooldown = Mathf.Max(0.28f, melee.configuration.cooldown * 0.8f);
                bool carried = (tip - _lastTip).sqrMagnitude > MaxSweep * MaxSweep; // the hit test would span the whole jump
                if (!carried && speed > VRConfig.MeleeSwingSpeed.Value && Time.time - _lastHit > cooldown && !Util.GameContext.GameMenuOpen)
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
            bool disabled = user.DisableHitboxColliders();
            bool hitSomething = false;
            try
            {
                // Two casts: along the tip's path and along the middle of the blade.
                Vector3 midFrom = Vector3.Lerp(fromBase, fromTip, 0.5f);
                Vector3 midTo = Vector3.Lerp(toBase, toTip, 0.5f);
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

        private static bool Cast(MeleeWeapon melee, Ray ray, float distance, float radius, Vector3 direction)
        {
            melee.SphereCast(ray, radius, distance, 70913, out RaycastHit hitWall, out MeleeWeapon.RaycastHitHitbox hitEnemy,
                out MeleeWeapon.RaycastHitHitbox hitAlly, out RaycastHit hitVehicle);
            float blocking = Mathf.Min(hitWall.distance, hitVehicle.distance);
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
        }
    }
}
