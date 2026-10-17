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
        /// The two may be held at once (user, 2026-10-16): primary + secondary, primary + launcher, secondary + launcher (and
        /// with grenades, in a later step); never two of a kind. The second, for now, is a gun or a launcher.
        /// </summary>
        internal static bool CanPair(Weapon main, Weapon second)
        {
            int a = Kind(main), b = Kind(second);
            return a >= 0 && b >= 0 && b != 3 && a != b && second != main && !(second is MeleeWeapon);
        }

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
            hand.Role = HandRole.Weapon;
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
            bool held = VRInput.GetHeld(DigitalAction.Use, Hand.Side) && !Util.GameContext.GameMenuOpen;
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
            Quaternion gripInGun = Quaternion.LookRotation(d.AimAxis, d.Up);
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
        }

        /// <summary>Back into its holster (let go, or the main gun went away).</summary>
        public void PutAway()
        {
            if (Weapon == null)
            {
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
            Weapon.Holster();
            if (Hand.Role == HandRole.Weapon)
            {
                Hand.Role = HandRole.Free;
            }
            _sys.Sounds.Play(MechSound.Holster, Hand.Palm.position, 0.6f);
            Hand.Haptic(0.04f, 100f, 0.5f);
            Log.Info($"Second weapon: {Weapon.name} back in its holster.");
        }
    }
}
