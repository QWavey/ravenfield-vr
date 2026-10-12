using System.Collections.Generic;
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
    /// </summary>
    public sealed class ThrowableHandler
    {
        private readonly WeaponSystem _sys;
        private bool _armed;
        private readonly List<Projectile> _spawned = new List<Projectile>();
        private ThrowableWeapon _listening;

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
            VRGameInput.SuppressFire = true;
            VRHand hand = _sys.Rig.Dominant;
            hand.Role = HandRole.Throwable;
            if (VRInput.GetPressed(DigitalAction.Fire, hand.Side) && !GameContextBlocksThrow())
            {
                _armed = true;
                hand.Haptic(0.02f, 100f, 0.3f);
            }
            if (_armed && VRInput.GetReleased(DigitalAction.Fire, hand.Side))
            {
                _armed = false;
                Vector3 v = hand.ThrowVelocity() * VRConfig.ThrowVelocityMultiplier.Value;
                if (v.magnitude > 1.1f)
                {
                    Throw(weapon, hand, v);
                }
            }
        }

        private static bool GameContextBlocksThrow() => Util.GameContext.GameMenuOpen;

        private void Throw(ThrowableWeapon weapon, VRHand hand, Vector3 velocity)
        {
            if (!weapon.CanFire() || !weapon.unholstered)
            {
                hand.Haptic(0.08f, 40f, 0.4f);
                return;
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
            }
            _spawned.Clear();
            hand.Haptic(0.04f, 90f, 0.6f);
        }

        private void OnSpawned(Projectile[] projectiles)
        {
            if (projectiles != null)
            {
                _spawned.AddRange(projectiles);
            }
        }

        public void Release()
        {
            _armed = false;
            if (_listening != null)
            {
                _listening.onSpawnProjectiles.RemoveListener(OnSpawned);
                _listening = null;
            }
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
        private bool _hasLast;
        private float _lastHit = -10f;
        private float _bladeLength = -1f;
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
                _bladeLength = MeasureLength(held);
                _hasLast = false;
            }
            WeaponRig rig = held.Rig;
            Vector3 basePoint = rig.ToWorld(rig.Data.GripPoint + rig.Data.AimAxis * 0.06f);
            Vector3 tip = rig.ToWorld(rig.Data.GripPoint + rig.Data.AimAxis * _bladeLength);
            float dt = Mathf.Max(Time.unscaledDeltaTime, 1e-4f);
            if (_hasLast)
            {
                float speed = (tip - _lastTip).magnitude / dt;
                float cooldown = Mathf.Max(0.28f, melee.configuration.cooldown * 0.8f);
                if (speed > VRConfig.MeleeSwingSpeed.Value && Time.time - _lastHit > cooldown && !Util.GameContext.GameMenuOpen)
                {
                    if (Swing(melee, _lastTip, tip, _lastBase, basePoint))
                    {
                        _lastHit = Time.time;
                    }
                }
            }
            _lastTip = tip;
            _lastBase = basePoint;
            _hasLast = true;
        }

        private static float MeasureLength(HeldWeapon held)
        {
            WeaponRig rig = held.Rig;
            float max = 0.25f;
            foreach (MeshFilter mf in held.Weapon.GetComponentsInChildren<MeshFilter>())
            {
                if (mf.sharedMesh == null)
                {
                    continue;
                }
                Bounds b = mf.sharedMesh.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 gun = rig.ToGun(mf.transform.TransformPoint(c));
                    max = Mathf.Max(max, Vector3.Dot(gun - rig.Data.GripPoint, rig.Data.AimAxis));
                }
            }
            return Mathf.Clamp(max, 0.15f, 1.6f);
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
