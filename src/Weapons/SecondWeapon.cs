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
        public readonly AmmoState Ammo;
        public readonly VRHand Hand;
        private readonly WeaponSystem _sys;
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

        /// <summary>Done with: thrown and nothing left to throw. The weapon system lets it go.</summary>
        internal bool Done { get; private set; }

        /// <summary>Autotest: live projectiles of its last throw.</summary>
        internal int LastLiveCount { get; private set; }

        private bool _triggerWas;
        private readonly List<Projectile> _spawned = new List<Projectile>();

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
            // A grenade put away with its pin out is drawn with it out (it came back a dud; review, 2026-10-20).
            Armed = weapon is ThrowableWeapon pulled && sys.Throwables != null && sys.Throwables.TakePulled(pulled);
            Log.Info($"Second weapon: {weapon.name} in the {hand.Side} hand beside {(sys.Held != null && sys.Held.Weapon != null ? sys.Held.Weapon.name : "nothing")}.");
        }

        /// <summary>Its trigger (the hand's own, which is the game's Use on the free hand), and a timed reload when it is empty.</summary>
        public void Update()
        {
            VRGameInput.SupportTriggerOwned = true; // the free hand's trigger fires this weapon, it does not enter vehicles
            if (Weapon == null)
            {
                return;
            }
            if (_reloadDoneAt >= 0f)
            {
                if (Time.time >= _reloadDoneAt)
                {
                    FinishReload();
                }
                return;
            }
            bool held = (VRInput.GetHeld(DigitalAction.Use, Hand.Side) || TestTrigger) && !VRGameInput.GunInputBlocked;
            if (IsThrowable)
            {
                // A grenade with its pin out keeps the trigger as it was while input is blocked (a panel opened under the laser):
                // blocked, it read as let go, and a moving hand threw it (review, 2026-10-20).
                UpdateThrowable(VRGameInput.GunInputBlocked && Armed ? _triggerWas : held);
                return;
            }
            CycleForTheHand();
            if (held && !_firing && Ammo != null && Ammo.Participates && VRConfig.ReloadNow == ReloadMode.Manual
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
                Armed = true;
                _sys.Sounds.Play(MechSound.PinPull, Hand.Palm.position, 0.8f);
                Hand.Haptic(0.03f, 120f, 0.5f);
            }
            if (!trigger && _triggerWas && Armed)
            {
                Vector3 v = Hand.ThrowVelocity() * VRConfig.ThrowVelocityMultiplier.Value;
                if (v.magnitude > 1.1f)
                {
                    Throw(v);
                }
            }
            _triggerWas = trigger;
        }

        /// <summary>
        /// The grip let go: in a throwing motion against the body it is thrown (a dud with its pin in), else it goes back to
        /// its holster. Then it is put away either way.
        /// </summary>
        public void LetGo()
        {
            if (IsThrowable && Weapon != null && !Done && !Util.GameContext.GameMenuOpen)
            {
                Vector3 body = Hand.BodyThrowVelocity() * VRConfig.ThrowVelocityMultiplier.Value;
                Vector3 v = Hand.ThrowVelocity() * VRConfig.ThrowVelocityMultiplier.Value;
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
            if (!Armed)
            {
                // As the grenade hand's dud: one fewer in the pouch (picked up again, it goes back), the next one ready.
                GrenadeDud.Spawn(Rig, weapon, Hand.Palm.position, Hand.Palm.rotation, velocity, Hand.AngularVelocity);
                weapon.ammo = Mathf.Max(0, weapon.ammo - 1);
                weapon.InstantlyReload();
                Hand.Haptic(0.03f, 70f, 0.4f);
                Done = true;
                return;
            }
            Transform muzzle = weapon.CurrentMuzzle();
            Vector3 savedPos = muzzle.localPosition;
            Quaternion savedRot = muzzle.localRotation;
            Vector3 dir = velocity.normalized;
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
            _sys.Sounds.Play(MechSound.Spoon, Hand.Palm.position, 0.8f);
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
            if (Ammo.RackForward())
            {
                _sys.Sounds.Play(MechSound.SlideBack, Hand.Palm.position, 0.6f);
                _sys.Sounds.Play(MechSound.SlideForward, Hand.Palm.position, 0.6f);
                Hand.Haptic(0.03f, 120f, 0.4f);
            }
        }

        private void StartReload()
        {
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
            _sys.Sounds.Play(MechSound.MagOut, Hand.Palm.position, 0.8f);
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
            Ammo.SlideLocked = false;
            Ammo.RackForward();
            _sys.Sounds.Play(MechSound.MagIn, Hand.Palm.position, 0.8f);
            _sys.Sounds.Play(MechSound.SlideForward, Hand.Palm.position, 0.8f);
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
            Weapon.Configuration c = Weapon.configuration;
            float kick = HeldWeapon.ShotKick(c, Rig.Data, Rig.Data.Handgun ? WeaponClass.Pistol : WeaponClass.Rifle, out float climbShare);
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

            WeaponRigData d = Rig.Data;
            RigidPose hand = Hand.GunFrame;
            Quaternion gripInGun = d.HasGripRotation ? d.GripRotation : Quaternion.LookRotation(d.AimAxis, d.Up);
            Quaternion aimFrame = hand.rotation * Quaternion.Euler(_kickRot.x, _kickRot.y, 0f);
            Quaternion gunRot = aimFrame * Quaternion.Inverse(gripInGun);
            Vector3 gripWorld = hand.position + aimFrame * _kickPos;
            var gunBody = new RigidPose(gripWorld - gunRot * d.GripPoint, gunRot);
            Transform root = Weapon.transform;
            RigidPose gunInRoot = RigidPose.Of(root).Inverse().Multiply(RigidPose.Of(Rig.GunBody));
            gunBody.Multiply(gunInRoot.Inverse()).ApplyTo(root);
            if (Weapon.isHidden)
            {
                Weapon.Show(); // the game hid it as a weapon not in use: it is in the hand
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
            if (_volume != null && _volume.Failed)
            {
                _volume = null; // measured again a second later
                _volumeRetryAt = Time.unscaledTime + 1f;
            }
            if (_volume == null && _volumeTries < 3 && Time.unscaledTime >= _volumeRetryAt && Time.unscaledTime - _heldSince > 0.25f)
            {
                _volumeTries++;
                _volume = GunVolume.For(Rig, frame, Rig.Data.GripPoint);
                if (_volume == null)
                {
                    _volumeRetryAt = Time.unscaledTime + 1f;
                }
            }
            if (_volume == null || !_volume.Ready)
            {
                return;
            }
            _volume.SetPose(Rig.ToWorld(Rig.Data.GripPoint), Rig.GunBody.rotation * frame);
            model.Solid = _solid ?? (_solid = p => _volume != null && _volume.Ready && _volume.Solid(p));
            model.SolidAnchor = Rig.GunBody;
            model.SolidVersion = 0;
        }

        /// <summary>Back into its holster (let go, or the main gun went away).</summary>
        public void PutAway()
        {
            if (Weapon == null)
            {
                // Destroyed under the hand: the game replaced it (the gun hand picked up a weapon of its slot, which drops and
                // destroys this one at the end of that frame; a loadout change). The hand is freed all the same: its role came
                // back only through HealHandRoles, and its shape to close on stayed the destroyed weapon's, so the next grip
                // that set no shape of its own closed the fingers on an invisible gun (bug hunt, 2026-10-19).
                if (_reloadDoneAt >= 0f && Ammo != null)
                {
                    Ammo.ReturnSpare(_reloadRounds); // a reload under way gives back what it took (review, 2026-10-20)
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
            FreeHand();
        }

        /// <summary>The hand empty again: free to take things, closing on nothing.</summary>
        private void FreeHand()
        {
            if (Hand.Role == HandRole.Weapon || Hand.Role == HandRole.Throwable)
            {
                Hand.Role = HandRole.Free;
            }
            if (Hand.Model != null && _solid != null && Hand.Model.Solid == _solid)
            {
                Hand.Model.Solid = null; // nothing left in the hand to close on
            }
        }
    }
}
