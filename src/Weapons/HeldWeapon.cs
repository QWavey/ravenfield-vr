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

        /// <summary>The weapon's own sounds for manual actions (procedural fallback).</summary>
        public readonly WeaponSoundPlayer Sounds;

        // Two-handed grip.
        public bool TwoHanded { get; private set; }
        private float _twoHandBlend;

        // Recoil spring (gun-frame offsets).
        private Vector3 _recoilPos, _recoilPosVel;
        private Vector2 _recoilRot, _recoilRotVel;

        // Handling by weapon class: how much harder it kicks in one hand, how far it sags and how slowly it follows
        // the wrist when nothing supports the front.
        private readonly float _oneHandRecoil;
        private readonly float _sagDegrees;
        private readonly float _followRate;
        private Quaternion _carriedRot;
        private bool _carriedInit;

        /// <summary>
        /// The front of the gun is supported: steering with both hands, or the off hand on the handguard / cupping a
        /// pistol grip even when two-handed steering is switched off.
        /// </summary>
        public bool Supported => TwoHanded || _system.Rig.Off.Role == HandRole.Foregrip;

        // Visual overrides.
        public float SlideOffset;
        /// <summary>0 closed .. 1 fully open, for break-open barrels, a revolver crane or a feed cover.</summary>
        public float HingeOpenness;
        private Quaternion _hingeRestLocal;
        private bool _hingeOverridden;
        private Vector3[] _magOriginalScales;
        private bool _magHidden;
        private readonly BoltPiece _boltPiece;
        private readonly ArmIK _armIK;

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
            Sounds = new WeaponSoundPlayer(rig, system.Sounds);
            Reload = new ReloadInteractions(system, this);
            _magOriginalScales = rig.MagazinePartScales;
            if (VRConfig.VRScopes.Value && VRScope.Wanted(weapon, rig))
            {
                _scope = new VRScope(system.Rig, rig);
            }
            _counter = new AmmoCounter(rig);
            int repaired = Util.MaterialFix.ApplyAll(weapon.gameObject);
            if (repaired > 0)
            {
                Log.InfoOnce("matfix:" + rig.Data.Key, $"{weapon.name}: repaired {repaired} renderer(s) with shaders this graphics API cannot draw.");
            }
            _gunCollider = GunCollider.Create(rig);
            Handling(rig.Data, weapon, out _oneHandRecoil, out _sagDegrees, out _followRate);
            if (VRConfig.Hands.Value == HandsMode.WeaponArms && (rig.Data.ArmRight != null || rig.Data.ArmLeft != null) && rig.ArmRenderers.Length > 0)
            {
                _armIK = new ArmIK(system, this);
            }
            ActionType action = rig.Data.Action;
            if (rig.Data.SlidePath == null && (action == ActionType.SelfLoading || action == ActionType.Manual || action == ActionType.ShellFed))
            {
                // No part of this model moves when the action is worked: give it a handle that does.
                try
                {
                    _boltPiece = BoltPiece.Create(rig);
                }
                catch (System.Exception e)
                {
                    Log.WarnOnce("boltpiece:" + rig.Data.Key, $"Could not build a moving handle for {rig.Data.Key}: {e.Message}");
                }
            }
        }

        public bool IsPistolGrip => Rig.Data.IsPistol;

        /// <summary>
        /// Per class: one-handed recoil multiplier (two-handed is the baseline), droop in degrees and how fast the gun
        /// follows the wrist (1/s) when held in one hand. Pistols are made for one hand; long and heavy guns are not.
        /// </summary>
        private static void Handling(WeaponRigData d, Weapon w, out float oneHandRecoil, out float sag, out float follow)
        {
            float length = Mathf.Abs(Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis));
            if (d.IsPistol)
            {
                oneHandRecoil = 1.15f; sag = 0f; follow = 60f;
            }
            else if (d.IsMachineGun)
            {
                oneHandRecoil = 2.6f; sag = 11f; follow = 7f;
            }
            else if (d.Action == ActionType.SingleShot && w.configuration.ammo <= 1 && length > 0.4f)
            {
                oneHandRecoil = 1.6f; sag = 8f; follow = 8f; // launchers
            }
            else if (d.Action == ActionType.ShellFed || d.Action == ActionType.BreakOpen || d.Action == ActionType.Manual && w.configuration.projectilesPerShot > 1)
            {
                oneHandRecoil = 2.2f; sag = 6f; follow = 12f; // shotguns
            }
            else if (length < 0.32f)
            {
                oneHandRecoil = 1.5f; sag = 3f; follow = 22f; // SMGs, PDWs
            }
            else
            {
                oneHandRecoil = 1.9f; sag = 5f; follow = 14f; // rifles, DMRs, snipers
            }
        }

        // ---------------------------------------------------------------------------------------------

        private readonly GunCollider _gunCollider;

        public void Update()
        {
            _gunCollider?.Update();
            VRHand main = _system.Rig.Dominant;
            main.Role = HandRole.Weapon;
            if (main.Model != null)
            {
                main.Model.WeaponGripCurls = Rig.Data.GripCurls;
            }
            if (_system.Rig.Off.Model != null)
            {
                _system.Rig.Off.Model.WeaponSupportCurls = Rig.Data.SupportCurls;
            }
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
                // Either eye on the sight line, anywhere from a cheek weld to arms-length behind the sights.
                Vector3 eyePoint = Rig.ToWorld(d.EyePoint);
                Vector3 dir = Rig.DirToWorld(d.EyeDirection);
                float best = float.MaxValue;
                foreach (Vector3 e in Eyes(eyeL, eyeR))
                {
                    Vector3 rel = e - eyePoint;
                    float along = Vector3.Dot(rel, dir);
                    if (along < -0.6f || along > 0.15f)
                    {
                        continue;
                    }
                    best = Mathf.Min(best, (rel - dir * along).magnitude);
                }
                float angle = Vector3.Angle(head.forward, dir);
                aim = Aiming ? best < 0.07f && angle < 35f : best < 0.045f && angle < 25f;
            }
            else
            {
                Vector3 muzzle = Rig.ToWorld(d.MuzzlePoint);
                Vector3 axis = Rig.AimWorld;
                float best = float.MaxValue;
                foreach (Vector3 e in Eyes(eyeL, eyeR))
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

        private readonly Vector3[] _eyes = new Vector3[2];

        private Vector3[] Eyes(Vector3 left, Vector3 right)
        {
            _eyes[0] = left;
            _eyes[1] = right;
            return _eyes;
        }

        public void FlashCounter(string message) => _counter.Flash(message);

        /// <summary>Called by the off-hand grab logic.</summary>
        public void SetTwoHanded(bool on) => TwoHanded = on;

        /// <param name="fired">The weapon that fired: this one or its under-barrel / sub weapon (which kicks as itself).</param>
        public void OnShot(Weapon fired)
        {
            Weapon.Configuration c = (fired != null ? fired : Weapon).configuration;
            float strength = VRConfig.RecoilStrength.Value;
            if (Supported)
            {
                // Long guns: the support hand takes most of it; a pistol cupped in two hands a little.
                strength *= Rig.Data.IsPistol ? 0.8f : 1f - VRConfig.TwoHandRecoilReduction.Value;
            }
            else
            {
                strength *= Rig.Data.IsPistol ? 1f : _oneHandRecoil * (1f - VRConfig.TwoHandRecoilReduction.Value);
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
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            StepRecoil(dt); // also while untracked, so shots in a tracking gap do not bank up into one big jolt
            if (!main.HasPose)
            {
                // Never tracked yet: the game's pose stays, but the magazine, slide and hinge still show their real
                // state, and time-driven mechanics (a magazine leaving the well) must not freeze mid-air.
                ApplyPartOverrides();
                Reload.BeforeRender();
                return;
            }
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

            // Weight: unsupported long guns droop and trail the wrist a little; supported they follow it exactly.
            if (VRConfig.WeaponWeight.Value && !Supported && _sagDegrees > 0f)
            {
                Vector3 pitchAxis = gunRot * Vector3.Cross(d.Up, d.AimAxis).normalized;
                Quaternion target = Quaternion.AngleAxis(_sagDegrees, pitchAxis) * gunRot;
                if (!_carriedInit)
                {
                    _carriedRot = target;
                    _carriedInit = true;
                }
                _carriedRot = Quaternion.Slerp(_carriedRot, target, 1f - Mathf.Exp(-_followRate * dt));
                gunRot = _carriedRot;
            }
            else
            {
                _carriedRot = gunRot;
                _carriedInit = true;
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
            if (_armIK != null && _armIK.Active)
            {
                _armIK.Solve();
                SetHandModelsSuppressed(true);
            }
            else
            {
                HideArms();
            }

            Reload.BeforeRender();
            Sounds.Update();
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
            float fraction = SlideOffset / Mathf.Max(d.SlideTravel.magnitude, 1e-4f);
            if (SlideOffset > 0.0005f)
            {
                MovePart(Rig.Slide, d.SlideRestLocal, d.SlideTravel * fraction);
                // A separate charging handle travels with the bolt (its own stroke length).
                MovePart(Rig.Handle, d.HandleRestLocal, d.HandleTravel * Mathf.Clamp01(fraction));
            }
            _boltPiece?.SetOffset(Rig, d.SlideTravel * fraction);
            ApplyHinge(d);

            AmmoState ammo = ActiveAmmo ?? Ammo;
            if (Rig.MagazineParts.Length > 0 && ammo != null && ammo.UsesDetachableMag)
            {
                // While the magazine is out a prop represents it (sliding in or out, in the hand, on the ground).
                // Scaling hides mesh-renderer and skinned magazines alike.
                bool hide = !ammo.MagInserted;
                if (hide)
                {
                    foreach (Transform part in Rig.MagazineParts)
                    {
                        if (part != null) // a mod may swap or destroy magazine pieces at runtime
                        {
                            part.localScale = Vector3.one * 1e-4f;
                        }
                    }
                    _magHidden = true;
                }
                else if (_magHidden)
                {
                    RestoreMagazine();
                }
            }
        }

        /// <summary>Swings the hinged part about its own pivot, on the axis the mod's reload opens it.</summary>
        private void ApplyHinge(WeaponRigData d)
        {
            Transform hinge = Rig.Hinge;
            if (hinge == null)
            {
                return;
            }
            if (HingeOpenness <= 0.001f)
            {
                if (_hingeOverridden)
                {
                    hinge.localRotation = _hingeRestLocal;
                    _hingeOverridden = false;
                }
                return;
            }
            if (!_hingeOverridden)
            {
                _hingeRestLocal = hinge.localRotation;
                _hingeOverridden = true;
            }
            Vector3 axisWorld = Rig.DirToWorld(d.HingeAxis);
            Vector3 axisLocal = hinge.parent != null ? hinge.parent.InverseTransformDirection(axisWorld) : axisWorld;
            hinge.localRotation = Quaternion.AngleAxis(d.HingeOpenAngle * HingeOpenness, axisLocal) * _hingeRestLocal;
        }

        private void MovePart(Transform part, Vector3 restLocal, Vector3 travelGun)
        {
            if (part == null)
            {
                return;
            }
            Vector3 worldTravel = Rig.DirToWorld(travelGun);
            Transform parent = part.parent;
            part.localPosition = restLocal + (parent != null ? parent.InverseTransformVector(worldTravel) : worldTravel);
        }

        private void RestoreMagazine()
        {
            for (int i = 0; i < Rig.MagazineParts.Length; i++)
            {
                if (Rig.MagazineParts[i] != null)
                {
                    Rig.MagazineParts[i].localScale = _magOriginalScales[i];
                }
            }
            _magHidden = false;
        }

        private void SetHandModelsSuppressed(bool suppressed)
        {
            foreach (VRHand h in _system.Rig.Hands)
            {
                if (h.Model != null)
                {
                    h.Model.Suppressed = suppressed;
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
            try
            {
                Reload.Release();
            }
            catch (System.Exception e)
            {
                // The rest of the cleanup (parts, hands, renderers) must still run.
                Log.Exception("Releasing reload state", e);
            }
            if (_magHidden)
            {
                RestoreMagazine();
            }
            Sounds.Destroy();
            if (Rig.Slide != null)
            {
                Rig.Slide.localPosition = Rig.Data.SlideRestLocal;
            }
            if (Rig.Handle != null)
            {
                Rig.Handle.localPosition = Rig.Data.HandleRestLocal;
            }
            if (_hingeOverridden && Rig.Hinge != null)
            {
                Rig.Hinge.localRotation = _hingeRestLocal;
                _hingeOverridden = false;
            }
            HingeOpenness = 0f;
            _boltPiece?.Destroy();
            SetHandModelsSuppressed(false);
            if (_system.Rig.Dominant.Model != null)
            {
                _system.Rig.Dominant.Model.WeaponGripCurls = null;
            }
            if (_system.Rig.Off.Model != null)
            {
                _system.Rig.Off.Model.WeaponSupportCurls = null;
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
            _gunCollider?.Destroy();
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
