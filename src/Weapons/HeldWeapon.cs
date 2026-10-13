using System.Collections.Generic;
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
        /// <summary>Lenses for the magnified sight modes (a flip-in magnifier), by mode; shown while that mode is selected.</summary>
        private readonly VRScope[] _modeScopes;
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
        /// <summary>The weapon's own right and left hands (bones still animated under the hidden arm meshes).</summary>
        private readonly ModHand _gripHand, _supportHand;

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
            else if (VRConfig.VRScopes.Value && rig.Data.Sights != null && weapon.renderTextureCamera == null)
            {
                _modeScopes = new VRScope[rig.Data.Sights.Length];
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
            _gripHand = ModHand.Find(weapon.transform, rig.Data.ArmRight);
            _isKit = IsKit(weapon);
            _supportHand = ModHand.Find(weapon.transform, rig.Data.ArmLeft);
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
        /// The rig the reload works on while a sub weapon is active. A fire-mode variant (semi/auto as a second weapon of
        /// the same model) has no magazine of its own in the analysis: it feeds from the parent's magazine and well, so it
        /// uses the parent's geometry (its own ammo). Under-barrel launchers and other different actions keep their own.
        /// </summary>
        private WeaponRig SubWeaponRig(Weapon sub)
        {
            WeaponRig own = _system.RigFor(sub);
            WeaponRigData d = own.Data;
            bool noMagazine = d.MagazinePath == null && d.SlidePath == null;
            bool sameAction = d.Action == Rig.Data.Action;
            if (noMagazine && sameAction && Rig.Data.MagazinePath != null)
            {
                Log.InfoOnce("subrig:" + d.Key, $"{d.Key}: fire-mode variant of {Rig.Data.Key}, reloads with its magazine.");
                return Rig;
            }
            return own;
        }

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
                main.Model.WeaponIsPistol = Rig.Data.IsPistol;
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
                ActiveRig = active == Weapon ? Rig : SubWeaponRig(active);
                ActiveAmmo = active == Weapon ? Ammo : _system.StateFor(active, create: true);
            }
            UpdateAim();
            Reload.Update();
        }

        /// <summary>The sight the weapon uses right now (its selected sight mode), or null for its only sight.</summary>
        public SightLine CurrentSight
        {
            get
            {
                SightLine[] sights = Rig.Data.Sights;
                if (sights == null || Weapon == null)
                {
                    return null;
                }
                int mode = Weapon.GetSightModeIndex();
                return mode >= 0 && mode < sights.Length ? sights[mode] : sights[0];
            }
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
                // Either eye on the sight line, anywhere from a cheek weld to arms-length behind the sights. With several
                // sights (a magnifier flipped in, a canted dot) the selected one's line.
                SightLine sight = CurrentSight;
                Vector3 eyePoint = Rig.ToWorld(sight != null ? sight.EyePoint : d.EyePoint);
                Vector3 dir = Rig.DirToWorld(sight != null ? sight.EyeDirection : d.EyeDirection);
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
                strength *= Rig.Data.IsPistol ? _oneHandRecoil : _oneHandRecoil * (1f - VRConfig.TwoHandRecoilReduction.Value);
            }
            // Every shot kicks a little, even from weapons whose flat-screen kick is tiny.
            float kick = Mathf.Max(0.5f, Mathf.Clamp(c.kickback, 0f, 10f));
            float random = Mathf.Clamp(c.randomKick, 0f, 5f);
            // Critically damped spring peak = v0 / (omega * e), omega ~ 16: ~0.6 m/s per kick unit gives ~1.4 cm back,
            // ~150 deg/s per kick unit gives ~3.4 degrees of muzzle climb per shot (before the hand factors).
            _recoilPosVel += new Vector3(0f, 0.08f * kick, -0.6f * kick) * strength;
            _recoilRotVel += new Vector2(-(150f * kick + Random.Range(0f, 40f * random)), Random.Range(-25f, 25f) * random) * strength;

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
            PeakKick = Mathf.Max(PeakKick, Mathf.Abs(_recoilRot.x));
            PeakBack = Mathf.Max(PeakBack, -_recoilPos.z);
        }

        /// <summary>Self-test: the largest muzzle climb (degrees) and push back (metres) since <see cref="ResetKick"/>.</summary>
        internal float PeakKick { get; private set; }
        internal float PeakBack { get; private set; }
        internal void ResetKick() => PeakKick = PeakBack = 0f;

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
                Vector3 dirWorld = off.PalmCentre - hand.position; // palm centre to palm centre, as the support point is
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
            SnapHands(main, off);
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
            UpdateModeScopes();
            _counter.BeforeRender(ActiveAmmo);
            DrawDebugZones();
        }

        private void UpdateModeScopes()
        {
            if (_modeScopes == null)
            {
                return;
            }
            int mode = Weapon.GetSightModeIndex();
            for (int i = 0; i < _modeScopes.Length; i++)
            {
                SightLine line = Rig.Data.Sights[i];
                bool wanted = i == mode && line.Magnified;
                if (wanted && _modeScopes[i] == null)
                {
                    _modeScopes[i] = new VRScope(_system.Rig, Rig, line);
                }
                if (_modeScopes[i] != null)
                {
                    _modeScopes[i].SetShown(wanted);
                    if (wanted)
                    {
                        _modeScopes[i].BeforeRender(Aiming);
                    }
                }
            }
        }

        /// <summary>
        /// Boneworks-style grips: the gun hand's glove takes the weapon's own hand pose on its grip, the support hand the
        /// weapon's support pose on the handguard (slid to wherever you hold it) or on the pistol grip it cups.
        /// </summary>
        private GunVolume _volume;
        private bool _volumeTried;
        private bool _hullApplied;

        /// <summary>Self-test: the held gun's physics stand-in.</summary>
        internal GunCollider Collider => _gunCollider;
        private float _heldSince = -1f;
        private System.Func<Vector3, bool> _solid;

        /// <summary>
        /// The model's solid for the hands to close on, measured a moment after the weapon is in hand (its idle pose, every
        /// part in place) and kept per weapon type. Null until measured or when it cannot be.
        /// </summary>
        private System.Func<Vector3, bool> HandSolid()
        {
            if (_heldSince < 0f)
            {
                _heldSince = Time.unscaledTime;
            }
            Quaternion frame = OpenVRMath.SafeLookRotation(Rig.Data.AimAxis, Rig.Data.Up);
            // Measured with the weapon at rest (drawn, not reloading, slide home, hinge shut): whatever is displaced then
            // stays in its shape for the session. A gun that never comes to rest (empty, slide locked) is measured anyway.
            float held = Time.unscaledTime - _heldSince;
            bool atRest = Weapon.unholstered && !Weapon.reloading && SlideOffset < 0.002f && HingeOpenness < 0.01f;
            if (_volume == null && !_volumeTried && held > 0.25f && (atRest || held > 5f) && Rig.Data.Action != ActionType.NoAmmo)
            {
                _volumeTried = true;
                _volume = GunVolume.For(Rig, frame, Rig.Data.GripPoint);
            }
            if (_volume == null || !_volume.Ready || Rig.GunBody == null)
            {
                return null;
            }
            if (!_hullApplied && _volume.HullPieces != null && _volume.HullPieces.Length > 0 && _gunCollider != null)
            {
                _hullApplied = true;
                ChooseShape();
            }
            _volume.SetPose(Rig.ToWorld(Rig.Data.GripPoint), Rig.GunBody.rotation * frame);
            UpdateSeatedMagSolid();
            return _solid ?? (_solid = p => _volume.Solid(p) || _magSolidShown && _magVolume.Solid(p));
        }

        /// <summary>Self-test: how long choosing the physics shape took on the main thread (-1 when no choice was made).</summary>
        internal float ShapeChoiceMs { get; private set; } = -1f;

        /// <summary>Per weapon type: the shape fitted to the rendered model matched it better than the one from the meshes.</summary>
        private static readonly Dictionary<string, bool> RenderedShapeBetter = new Dictionary<string, bool>();

        /// <summary>
        /// The physics stand-in becomes the shape that matches the visible model best. Unreadable meshes only gave boxes:
        /// the rendered-model fit replaces them. Readable meshes are compared once per weapon type (share of the model
        /// covered minus share of the shape outside it) and the better fit is kept.
        /// </summary>
        private void ChooseShape()
        {
            string key = Rig.Data.Key;
            if (_volume.Unreadable)
            {
                _gunCollider.UseShape(_volume.HullPieces, _volume);
                return;
            }
            if (RenderedShapeBetter.TryGetValue(key, out bool known))
            {
                if (known)
                {
                    _gunCollider.UseShape(_volume.HullPieces, null);
                }
                return;
            }
            ShapeFit.Piece[] meshes = _gunCollider.Pieces;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // Coarse sampling (4³ per piece, ~150 surface points): enough to tell the fits apart without a hitch.
            string before = _gunCollider.Match(_volume, 4, 150, out float excess0, out float covered0);
            _gunCollider.UseShape(_volume.HullPieces, null);
            string after = _gunCollider.Match(_volume, 4, 150, out float excess1, out float covered1);
            ShapeChoiceMs = (float)watch.Elapsed.TotalMilliseconds;
            bool better = covered1 - excess1 > covered0 - excess0 + 5f; // a clear margin, not noise
            RenderedShapeBetter[key] = better;
            if (better)
            {
                _gunCollider.ShapeBefore = before;
                _gunCollider.ShapeBeforeExcess = excess0;
                _gunCollider.ShapeBeforeCovered = covered0;
            }
            else
            {
                _gunCollider.UseShape(meshes, null);
                _gunCollider.ShapeRejected = after;
            }
            Log.Info($"{key}: physics shape from the {(better ? "rendered model" : "meshes")} (meshes: {before}; rendered model: {after}).");
        }

        private GunVolume _magVolume;
        private bool _magVolumeTried, _magSolidShown;
        private Transform _magFrame;

        /// <summary>
        /// The seated magazine is measured on its own (it is a prop of its own once out, so it is not part of the gun's
        /// solid): while it is in the gun and shown, the hands close on it too (a magazine-held gun, a hand on the well).
        /// </summary>
        private void UpdateSeatedMagSolid()
        {
            _magSolidShown = false;
            if (_magFrame == null)
            {
                foreach (Transform part in Rig.MagazineParts)
                {
                    if (part != null)
                    {
                        _magFrame = part;
                        break;
                    }
                }
                if (_magFrame == null)
                {
                    return;
                }
            }
            if (_magHidden || !_magFrame.gameObject.activeInHierarchy)
            {
                return;
            }
            if (_magVolume == null && !_magVolumeTried)
            {
                _magVolumeTried = true;
                var renderers = new List<Renderer>();
                foreach (Transform part in Rig.MagazineParts)
                {
                    if (part != null)
                    {
                        foreach (Renderer r in part.GetComponentsInChildren<Renderer>(false))
                        {
                            if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                            {
                                renderers.Add(r);
                            }
                        }
                    }
                }
                _magVolume = GunVolume.Measure("seated:" + Rig.Data.Key, renderers, _magFrame, 3);
            }
            if (_magVolume != null && _magVolume.Ready)
            {
                _magVolume.SetPose(_magFrame.position, _magFrame.rotation);
                _magSolidShown = true;
            }
        }

        /// <summary>Self-test: inside the gun or its seated magazine (not grown).</summary>
        private bool InsideModel(Vector3 p) => _volume.InsideModel(p) || _magSolidShown && _magVolume.InsideModel(p);

        /// <summary>Self-test: the measured solid (null until measured).</summary>
        internal GunVolume Volume => _volume;

        /// <summary>Self-test: how much of a hand on this weapon is buried in its model, and how many fingers touch it.</summary>
        internal string HandClipping(VRHand hand) => HandClipping(hand, out _, out _, out _);

        internal string HandClipping(VRHand hand, out int buried, out int samples, out int touching)
        {
            buried = samples = touching = -1;
            if (_volume == null || !_volume.Ready || hand.Model == null)
            {
                return "not measured";
            }
            hand.Model.Measure(InsideModel, _solid ?? _volume.Solid, out buried, out samples, out touching);
            return $"{buried}/{samples} points inside the gun, {touching}/4 fingers on it";
        }

        /// <summary>
        /// A kit carried like a bag (it throws an ammo box or medipack): no glove is shown on it; the bag hangs from the
        /// hand the way the game shows it.
        /// </summary>
        private readonly bool _isKit;

        private static bool IsKit(Weapon weapon)
        {
            GameObject projectile = weapon.configuration != null ? weapon.configuration.projectilePrefab : null;
            if (projectile != null && (projectile.GetComponentInChildren<Ammobox>(true) != null || projectile.GetComponentInChildren<Medipack>(true) != null
                                       || projectile.GetComponentInChildren<ResupplyCrate>(true) != null))
            {
                return true;
            }
            return System.Text.RegularExpressions.Regex.IsMatch(weapon.name, @"\b(bag|kit|medkit|medic|ammo ?box|first ?aid)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private void SnapHands(VRHand main, VRHand off)
        {
            if (main.Model != null)
            {
                main.Model.HiddenByHeld = _isKit;
            }
            System.Func<Vector3, bool> solid = HandSolid();
            if (main.Model != null)
            {
                if (main.Role == HandRole.Weapon || main.Role == HandRole.Throwable)
                {
                    main.Model.Solid = solid;
                }
                else if (main.Model.Solid == _solid)
                {
                    main.Model.Solid = null; // only what this weapon set: a magazine in that hand keeps its own
                }
            }
            if (off.Model != null)
            {
                if (off.Role == HandRole.Foregrip)
                {
                    off.Model.Solid = solid;
                }
                else if (off.Model.Solid == _solid)
                {
                    off.Model.Solid = null;
                }
            }
            if (main.Model != null)
            {
                // Only while the weapon's own hand is on its grip: an animation that takes it away (an assisted reload,
                // an inspect) must not pull the glove off the tracked hand.
                main.Model.SnapPose = _gripHand != null && main.Role == HandRole.Weapon && _gripHand.TryPose(Rig.Weapon.transform, out RigidPose grip)
                                      && (grip.position - Rig.ToWorld(Rig.Data.GripPoint)).sqrMagnitude < 0.06f * 0.06f
                    ? main.Side == Hand.Right ? grip : MirrorAcrossGun(grip) // the rig's gun hand is a right hand
                    : (RigidPose?)null;
            }
            if (off.Model == null)
            {
                return;
            }
            if (_supportHand != null && off.Role == HandRole.Foregrip && _supportHand.TryPose(Rig.Weapon.transform, out RigidPose support)
                && (support.position - Rig.ToWorld(Rig.Data.SupportPoint)).sqrMagnitude < 0.12f * 0.12f)
            {
                if (off.Side == Hand.Right)
                {
                    support = MirrorAcrossGun(support); // the rig's support hand is a left hand
                }
                if (!Rig.Data.IsPistol)
                {
                    // Along the handguard to where the hand actually is, within the stretch between grip and muzzle.
                    Vector3 axis = Rig.AimWorld;
                    float slide = Vector3.Dot(off.PalmCentre - support.position, axis);
                    float min = Vector3.Dot(Rig.ToWorld(Rig.Data.GripPoint) - support.position, axis) + 0.08f;
                    float max = Vector3.Dot(Rig.ToWorld(Rig.Data.MuzzlePoint) - support.position, axis) - 0.04f;
                    support.position += axis * Mathf.Clamp(slide, Mathf.Min(min, 0f), Mathf.Max(max, 0f));
                }
                off.Model.SnapPose = support;
            }
            else
            {
                off.Model.SnapPose = null;
            }
        }

        /// <summary>Self-test: where the gun hand's glove, its snap pose and the weapon's own hand bones are, in the grip's aim frame (cm).</summary>
        internal string HandReport(VRHand main)
        {
            RigidPose g = GripWorld;
            Quaternion inv = Quaternion.Inverse(g.rotation);
            Vector3 F(Vector3 w) => inv * (w - g.position);
            string P(Vector3 w)
            {
                Vector3 v = F(w) * 100f;
                return $"({v.x:0.0} {v.y:0.0} {v.z:0.0})";
            }
            string D(Vector3 d)
            {
                Vector3 v = inv * d;
                return $"({v.x:0.00} {v.y:0.00} {v.z:0.00})";
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("grip point ").Append(P(Rig.ToWorld(Rig.Data.GripPoint)));
            HandModel model = main.Model;
            if (model != null)
            {
                if (model.SnapPose.HasValue)
                {
                    RigidPose s = model.SnapPose.Value;
                    sb.Append("; snap ").Append(P(s.position)).Append(" fingers ").Append(D(s.Forward)).Append(" thumb ").Append(D(s.rotation * Vector3.up));
                }
                else
                {
                    sb.Append("; no snap");
                }
                Transform r = model.Root;
                sb.Append("; glove ").Append(P(r.position)).Append(" fingers ").Append(D(r.forward)).Append(" thumb ").Append(D(r.up));
                sb.Append("; ").Append(model.Describe());
            }
            sb.Append(_gripHand != null ? "; weapon's hand: " + _gripHand.Describe(F) : "; no weapon hand");
            sb.Append(_volume == null ? "; no solid" : $"; solid {(_volume.Ready ? "ready" : "measuring")}, {_volume.Texels} texels, {_volume.RenderMs:0} ms on the main thread, {_volume.BuildMs:0} ms in all");
            return sb.ToString();
        }

        /// <summary>
        /// A hand pose reflected across the gun's vertical middle plane (through the bore): the weapon's right-hand pose
        /// for a left glove and the other way round.
        /// </summary>
        private RigidPose MirrorAcrossGun(RigidPose p)
        {
            Vector3 right = Rig.DirToWorld(OpenVRMath.SafeLookRotation(Rig.Data.AimAxis, Rig.Data.Up) * Vector3.right);
            Vector3 centre = Rig.ToWorld(Rig.Data.MuzzlePoint);
            Vector3 position = p.position - 2f * Vector3.Dot(p.position - centre, right) * right;
            Vector3 forward = Vector3.Reflect(p.rotation * Vector3.forward, right);
            Vector3 up = Vector3.Reflect(p.rotation * Vector3.up, right);
            return new RigidPose(position, OpenVRMath.SafeLookRotation(forward, up));
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
            // The hands first: whatever fails below, no glove may stay closed on a weapon that is gone.
            if (_system.Rig.Dominant.Model != null)
            {
                _system.Rig.Dominant.Model.Solid = null;
                _system.Rig.Dominant.Model.SnapPose = null;
                _system.Rig.Dominant.Model.HiddenByHeld = false;
            }
            if (_system.Rig.Off.Model != null)
            {
                _system.Rig.Off.Model.Solid = null;
                _system.Rig.Off.Model.SnapPose = null;
            }
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
                _system.Rig.Dominant.Model.WeaponIsPistol = false;
                _system.Rig.Dominant.Model.SnapPose = null;
                _system.Rig.Dominant.Model.Solid = null;
                _system.Rig.Dominant.Model.HiddenByHeld = false;
            }
            if (_system.Rig.Off.Model != null)
            {
                _system.Rig.Off.Model.WeaponSupportCurls = null;
                _system.Rig.Off.Model.SnapPose = null;
                _system.Rig.Off.Model.Solid = null;
            }
            foreach (Renderer r in Rig.ArmRenderers)
            {
                if (r != null && !Weapon.isHidden)
                {
                    r.enabled = true;
                }
            }
            _scope?.Destroy();
            if (_modeScopes != null)
            {
                foreach (VRScope s in _modeScopes)
                {
                    s?.Destroy();
                }
            }
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
