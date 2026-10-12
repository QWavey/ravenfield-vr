using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The weapon currently in the dominant hand. Keeps the game's animated weapon object but re-anchors it
    /// every frame (after animation) so its gun body sits in the hand; adds two-handed steering, recoil,
    /// sight-to-eye aiming, arm-mesh hiding and the slide / magazine visual overrides used by reloading.
    /// </summary>
    public sealed class HeldWeapon
    {
        public readonly Weapon Weapon;
        public readonly WeaponRig Rig;
        public readonly AmmoState Ammo;
        private readonly WeaponSystem _system;
        public readonly ReloadInteractions Reload;
        private readonly VRScope _scope;
        private readonly AmmoCounter _counter;

        // Two-handed grip.
        public bool TwoHanded { get; private set; }
        private float _twoHandBlend;

        // Recoil spring (gun-frame offsets).
        private Vector3 _recoilPos, _recoilPosVel;
        private Vector2 _recoilRot, _recoilRotVel;

        // Visual overrides.
        public float SlideOffset;
        private Vector3 _magOriginalScale = Vector3.one;
        private bool _magHidden;

        public bool Aiming { get; private set; }

        public RigidPose GripWorld { get; private set; }

        /// <summary>The weapon that currently fires: the parent, or an active under-barrel/sub weapon.</summary>
        public Weapon ActiveWeapon { get; private set; }
        public WeaponRig ActiveRig { get; private set; }
        public AmmoState ActiveAmmo { get; private set; }

        public HeldWeapon(WeaponSystem system, Weapon weapon, WeaponRig rig, AmmoState ammo)
        {
            _system = system;
            Weapon = weapon;
            Rig = rig;
            Ammo = ammo;
            ActiveWeapon = weapon;
            ActiveRig = rig;
            ActiveAmmo = ammo;
            Reload = new ReloadInteractions(system, this);
            if (rig.Magazine != null)
            {
                _magOriginalScale = rig.Magazine.localScale;
            }
            if (VRConfig.VRScopes.Value && VRScope.Wanted(weapon, rig))
            {
                _scope = new VRScope(system.Rig, rig);
            }
            _counter = new AmmoCounter(rig);
        }

        public bool IsPistolGrip => Rig.Data.IsPistol;

        // ---------------------------------------------------------------------------------------------

        public void Update()
        {
            VRHand main = _system.Rig.Dominant;
            main.Role = HandRole.Weapon;
            Weapon active = Weapon.GetActiveSubWeapon() ?? Weapon;
            if (active != ActiveWeapon)
            {
                Reload.Release();
                ActiveWeapon = active;
                ActiveRig = active == Weapon ? Rig : _system.RigFor(active);
                ActiveAmmo = active == Weapon ? Ammo : _system.StateFor(active, create: true);
            }
            UpdateAim();
            Reload.Update();
        }

        private void UpdateAim()
        {
            Transform head = _system.Rig.Head;
            WeaponRigData d = Rig.Data;
            Vector3 eyeL = head.position - head.right * 0.032f;
            Vector3 eyeR = head.position + head.right * 0.032f;
            bool aim;
            if (d.HasEyePoint)
            {
                Vector3 eye = Rig.ToWorld(d.EyePoint);
                float dist = Mathf.Min(Vector3.Distance(eyeL, eye), Vector3.Distance(eyeR, eye));
                float angle = Vector3.Angle(head.forward, Rig.DirToWorld(d.EyeDirection));
                aim = Aiming ? dist < 0.11f && angle < 35f : dist < 0.075f && angle < 25f;
            }
            else
            {
                Vector3 muzzle = Rig.ToWorld(d.MuzzlePoint);
                Vector3 axis = Rig.AimWorld;
                float best = float.MaxValue;
                foreach (Vector3 e in new[] { eyeL, eyeR })
                {
                    Vector3 rel = e - muzzle;
                    float behind = -Vector3.Dot(rel, axis);
                    if (behind < 0.1f || behind > 0.8f)
                    {
                        continue;
                    }
                    best = Mathf.Min(best, Vector3.ProjectOnPlane(rel, axis).magnitude);
                }
                float angle = Vector3.Angle(head.forward, axis);
                aim = Aiming ? best < 0.09f && angle < 35f : best < 0.06f && angle < 25f;
            }
            Aiming = aim;
        }

        public void FlashCounter(string message) => _counter.Flash(message);

        /// <summary>Called by the off-hand grab logic.</summary>
        public void SetTwoHanded(bool on) => TwoHanded = on;

        public void OnShot()
        {
            Weapon.Configuration c = Weapon.configuration;
            float strength = VRConfig.RecoilStrength.Value;
            if (TwoHanded)
            {
                strength *= 1f - VRConfig.TwoHandRecoilReduction.Value;
            }
            float kick = Mathf.Clamp(c.kickback, 0f, 10f);
            float random = Mathf.Clamp(c.randomKick, 0f, 5f);
            // Critically damped spring peak = v0 / (omega * e), omega ~ 16: ~0.44 m/s per kick unit gives ~1 cm back,
            // ~80 deg/s per kick unit gives ~1.8 degrees of muzzle climb per shot.
            _recoilPosVel += new Vector3(0f, 0.06f * kick, -0.44f * kick) * strength;
            _recoilRotVel += new Vector2(-(80f * kick + Random.Range(0f, 30f * random)), Random.Range(-20f, 20f) * random) * strength;

            float amp = Mathf.Clamp01(0.35f + kick * 0.15f);
            _system.Rig.Dominant.Haptic(0.03f + kick * 0.01f, 180f, amp);
            if (TwoHanded)
            {
                _system.Rig.Off.Haptic(0.03f, 160f, amp * 0.7f);
            }
            Reload.OnShot();
        }

        private void StepRecoil(float dt)
        {
            // Critically damped springs: snappy kick, quick settle.
            const float stiffness = 260f, damping = 2f * 16.1f;
            _recoilPosVel += (-stiffness * _recoilPos - damping * _recoilPosVel) * dt;
            _recoilPos += _recoilPosVel * dt;
            _recoilRotVel += (-stiffness * _recoilRot - damping * _recoilRotVel) * dt;
            _recoilRot += _recoilRotVel * dt;
            _recoilPos = Vector3.ClampMagnitude(_recoilPos, 0.12f);
            _recoilRot = Vector2.ClampMagnitude(_recoilRot, 25f);
        }

        // ---------------------------------------------------------------------------------------------

        public void BeforeRender()
        {
            VRHand main = _system.Rig.Dominant;
            VRHand off = _system.Rig.Off;
            if (!main.Tracked)
            {
                return;
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            StepRecoil(dt);
            _twoHandBlend = Mathf.MoveTowards(_twoHandBlend, TwoHanded && off.Tracked ? 1f : 0f, dt * 10f);

            WeaponRigData d = Rig.Data;
            RigidPose hand = main.GunFrame;
            var gripInGun = new RigidPose(d.GripPoint, Quaternion.LookRotation(d.AimAxis, d.Up));
            Quaternion gunRot = (hand.rotation * Quaternion.Inverse(gripInGun.rotation));

            if (_twoHandBlend > 0f && d.HasSupportPoint && !d.IsPistol)
            {
                Vector3 dirGun = (d.SupportPoint - d.GripPoint).normalized;
                Vector3 dirWorld = off.Palm.position - hand.position;
                if (dirWorld.sqrMagnitude > 0.0025f)
                {
                    Quaternion gunFrameBase = Quaternion.LookRotation(dirGun, d.Up);
                    Quaternion worldFrame = OpenVRMath.SafeLookRotation(dirWorld.normalized, hand.Up);
                    Quaternion twoHand = worldFrame * Quaternion.Inverse(gunFrameBase);
                    gunRot = Quaternion.Slerp(gunRot, twoHand, _twoHandBlend);
                }
            }

            // Recoil rotates the gun about its grip and pushes it back along the barrel.
            Quaternion recoil = Quaternion.Euler(_recoilRot.x, _recoilRot.y, 0f);
            Quaternion aimFrame = gunRot * gripInGun.rotation;
            aimFrame = aimFrame * recoil;
            gunRot = aimFrame * Quaternion.Inverse(gripInGun.rotation);
            Vector3 gripWorld = hand.position + aimFrame * _recoilPos;
            var desiredGunBody = new RigidPose(gripWorld - gunRot * d.GripPoint, gunRot);
            GripWorld = new RigidPose(gripWorld, aimFrame);

            // Anchor: put the root where it has to be for the (animated) gun body to land on desiredGunBody.
            Transform root = Weapon.transform;
            RigidPose rootNow = RigidPose.Of(root);
            RigidPose gunInRoot = rootNow.Inverse().Multiply(RigidPose.Of(Rig.GunBody));
            desiredGunBody.Multiply(gunInRoot.Inverse()).ApplyTo(root);

            ApplyPartOverrides();
            HideArms();

            if (TwoHanded)
            {
                off.Model.OverridePose(new RigidPose(Rig.ToWorld(d.SupportPoint), Rig.GunBody.rotation * Quaternion.LookRotation(d.AimAxis, d.Up)));
            }
            else if (off.Role == HandRole.Foregrip && d.IsPistol)
            {
                off.Model.OverridePose(new RigidPose(Rig.ToWorld(d.SupportPoint), Rig.GunBody.rotation * Quaternion.LookRotation(d.AimAxis, d.Up)));
            }

            Reload.BeforeRender();
            _scope?.BeforeRender(Aiming);
            _counter.BeforeRender(ActiveAmmo);
            DrawDebugZones();
        }

        private GameObject[] _debug;

        /// <summary>Debug.ShowGrabZones: spheres on the grip, foregrip, rack point, magazine well and sight eye point.</summary>
        private void DrawDebugZones()
        {
            if (!VRConfig.ShowGrabZones.Value)
            {
                DestroyDebug();
                return;
            }
            WeaponRigData d = Rig.Data;
            WeaponRigData a = ActiveRig.Data;
            Vector3[] points =
            {
                Rig.ToWorld(d.GripPoint),
                Rig.ToWorld(d.SupportPoint),
                ActiveRig.ToWorld(a.RackPoint + a.SlideTravel.normalized * SlideOffset),
                ActiveRig.ToWorld(a.MagSeat.position),
                d.HasEyePoint ? Rig.ToWorld(d.EyePoint) : Rig.ToWorld(d.MuzzlePoint)
            };
            if (_debug == null)
            {
                Color[] colors =
                {
                    new Color(0.9f, 0.9f, 0.9f, 0.35f), new Color(0.3f, 0.8f, 0.4f, 0.35f), new Color(0.95f, 0.6f, 0.2f, 0.4f),
                    new Color(0.3f, 0.6f, 1f, 0.4f), new Color(1f, 0.3f, 0.3f, 0.35f)
                };
                _debug = new GameObject[points.Length];
                for (int i = 0; i < points.Length; i++)
                {
                    _debug[i] = BodyZones.DebugSphere(colors[i], i == 2 || i == 1 ? VRConfig.GrabRadius.Value : 0.02f);
                }
            }
            for (int i = 0; i < points.Length; i++)
            {
                _debug[i].transform.position = points[i];
            }
        }

        private void DestroyDebug()
        {
            if (_debug == null)
            {
                return;
            }
            foreach (GameObject g in _debug)
            {
                Object.Destroy(g);
            }
            _debug = null;
        }

        private void ApplyPartOverrides()
        {
            WeaponRigData d = Rig.Data;
            Transform slide = Rig.Slide;
            if (slide != null && SlideOffset > 0.0005f)
            {
                float travel = d.SlideTravel.magnitude;
                Vector3 worldTravel = Rig.DirToWorld(d.SlideTravel) * (SlideOffset / Mathf.Max(travel, 1e-4f));
                Transform parent = slide.parent;
                Vector3 localTravel = parent != null ? parent.InverseTransformVector(worldTravel) : worldTravel;
                slide.localPosition = d.SlideRestLocal + localTravel;
            }

            Transform mag = Rig.Magazine;
            AmmoState ammo = ActiveAmmo ?? Ammo;
            if (mag != null && ammo != null && ammo.UsesDetachableMag)
            {
                bool hide = !ammo.MagInserted;
                if (hide)
                {
                    // Scaling hides both mesh-renderer and skinned magazines.
                    mag.localScale = Vector3.one * 1e-4f;
                    _magHidden = true;
                }
                else if (_magHidden)
                {
                    mag.localScale = _magOriginalScale;
                    _magHidden = false;
                }
            }
        }

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

        /// <summary>Undo every visual change before the weapon is holstered, dropped or VR stops.</summary>
        public void Release()
        {
            Reload.Release();
            if (Rig.Magazine != null && _magHidden)
            {
                Rig.Magazine.localScale = _magOriginalScale;
                _magHidden = false;
            }
            if (Rig.Slide != null)
            {
                Rig.Slide.localPosition = Rig.Data.SlideRestLocal;
            }
            foreach (Renderer r in Rig.ArmRenderers)
            {
                if (r != null && !Weapon.isHidden)
                {
                    r.enabled = true;
                }
            }
            _scope?.Destroy();
            _counter.Destroy();
            DestroyDebug();
            TwoHanded = false;
            VRHand main = _system.Rig.Dominant;
            if (main.Role == HandRole.Weapon)
            {
                main.Role = HandRole.Free;
            }
            VRHand off = _system.Rig.Off;
            if (off.Role == HandRole.Foregrip || off.Role == HandRole.Slide)
            {
                off.Role = HandRole.Free;
                off.Held = null;
            }
        }
    }
}
