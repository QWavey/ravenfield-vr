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

        /// <summary>Autotest: the weapon has a lens of the mod's (a second camera when aimed through).</summary>
        internal bool HasLensScope => _scope != null;
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
        /// <summary>How much of the droop and trail applies now (0 supported or shouldered .. 1 held in one hand).</summary>
        private float _sagWeight = -1f;

        /// <summary>
        /// The front of the gun is supported: steering with both hands, or the off hand on the handguard / cupping a
        /// pistol grip even when two-handed steering is switched off.
        /// </summary>
        public bool Supported => TwoHanded || _system.Rig.Off.Role == HandRole.Foregrip;

        /// <summary>The butt of the stock (gun-body space), or null for a gun without a stock behind the grip.</summary>
        private readonly Vector3? _stockButt;

        /// <summary>
        /// The stock is set in the shoulder pocket (Boneworks lets the stock bear on the body): the shoulder takes part of the
        /// kick and holds the gun up.
        /// </summary>
        public bool Shouldered
        {
            get
            {
                if (!_stockButt.HasValue || Rig.GunBody == null)
                {
                    return false;
                }
                // In within 12 cm of the pocket, out past 15: a stock held at the edge does not flick in and out. Measured where
                // the player holds the stock (the tracked hands' pose), not where the droop, the weight's lag and the recoil
                // draw it: on the drawn pose the droop this switches lifted or lowered the butt across the pocket's edge, the
                // stock left the shoulder, the droop came back, the butt went back in, and a launcher (its tube's end some
                // 50 cm behind the grip, moved about 7 cm by an 8 degree droop, more than the 3 cm band) rocked in a still
                // hand without being fired (Tasks 203).
                Vector3 butt = _buttFromHandSet ? _buttFromHand : Rig.ToWorld(_stockButt.Value);
                float distance = Vector3.Distance(butt, _system.Zones.DomShoulder);
                _shouldered = distance < (_shouldered ? 0.15f : 0.12f);
                return _shouldered;
            }
        }

        private bool _shouldered;
        /// <summary>The stock's butt where the tracked hands hold it this frame (before droop, weight lag and recoil).</summary>
        private Vector3 _buttFromHand;
        private bool _buttFromHandSet;

        // Visual overrides.
        public float SlideOffset;
        /// <summary>0 closed .. 1 fully open, for break-open barrels, a revolver crane or a feed cover.</summary>
        public float HingeOpenness;
        private Quaternion _hingeRestLocal;
        private bool _hingeOverridden;
        private Vector3[] _magOriginalScales;
        private bool _magHidden;
        private BoltPiece _boltPiece;

        /// <summary>
        /// Where the hand takes the slide, bolt or handle (gun-body space): the analysis' rack point, put on the drawn
        /// model's surface once it is measured when that point was only estimated.
        /// </summary>
        public Vector3 RackPointGun { get; private set; }
        private bool _rackSnapped;
        private readonly ArmIK _armIK;
        /// <summary>The weapon's own right / left arm bones as ArmIK resolves them (root, upper, lower, hand); null: no chain.</summary>
        private readonly Transform[] _armRightBones, _armLeftBones;

        private static Transform[] ResolveArm(Transform root, ArmChain chain) => chain == null ? null
            : new[] { WeaponRig.Resolve(root, chain.Root), WeaponRig.Resolve(root, chain.Upper), WeaponRig.Resolve(root, chain.Lower), WeaponRig.Resolve(root, chain.Hand) };

        /// <summary>That arm is drawn by ArmIK (the same test as its Arm.Valid: all four bones found).</summary>
        private static bool ArmDrawn(Transform[] bones) => bones != null && bones[0] != null && bones[1] != null && bones[2] != null && bones[3] != null;
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
            FindScopeLenses(weapon);
            GiveMuzzle(weapon, rig);
            int repaired = Util.MaterialFix.ApplyAll(weapon.gameObject);
            if (repaired > 0)
            {
                Log.InfoOnce("matfix:" + rig.Data.Key, $"{weapon.name}: repaired {repaired} renderer(s) with shaders this graphics API cannot draw.");
            }
            _gunCollider = GunCollider.Create(rig);
            // Renderers that draw only a shadow (helper boxes a mod's reload animation uses, shadow proxies of a flat
            // view model) put a dark blob on the ground under the player in VR, where the weapon is drawn in the world.
            foreach (Renderer r in weapon.GetComponentsInChildren<Renderer>(true))
            {
                if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    _shadowOnly.Add(r);
                }
            }
            // The flat game's casing particles give way to real cases (Casings), here and on its sub weapons.
            foreach (Weapon w in weapon.GetComponentsInChildren<Weapon>(true))
            {
                ParticleSystem[] casings = w.configuration != null ? w.configuration.casingParticles : null;
                if (casings == null)
                {
                    continue;
                }
                foreach (ParticleSystem p in casings)
                {
                    ParticleSystemRenderer pr = p != null ? p.GetComponent<ParticleSystemRenderer>() : null;
                    if (pr != null && pr.enabled)
                    {
                        pr.enabled = false;
                        _hiddenCasings.Add(pr);
                    }
                }
            }
            Handling(rig.Data, weapon, out _oneHandRecoil, out _sagDegrees, out _followRate);
            Inertia(rig.Data, weapon, out _inertiaOmega, out _inertiaLever);
            if (_inertiaOmega > 0f && _inertiaOmega < 14f)
            {
                // Picked up, a heavy weapon (a machine gun, a launcher) comes up into the hand over about half a second.
                _lagPos = Vector3.down * 0.05f;
                _lagPitch = 6f;
            }
            _weaponClass = ClassOf(rig, weapon);
            if (!rig.Data.Handgun && rig.Data.Action != ActionType.Melee && rig.Data.Action != ActionType.Throwable)
            {
                // The rearmost part of the drawn gun, 12 cm or more behind the grip, is a stock's butt.
                Bounds b = GunCollider.VisibleBounds(rig, out bool measured);
                float rear = float.MaxValue;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    rear = Mathf.Min(rear, Vector3.Dot(c - rig.Data.GripPoint, rig.Data.AimAxis));
                }
                if (measured && rear < -0.12f)
                {
                    _stockButt = rig.Data.GripPoint + rig.Data.AimAxis * rear + rig.Data.Up * 0.03f;
                }
            }
            if (VRConfig.HandsNow == HandsMode.WeaponArms && (rig.Data.ArmRight != null || rig.Data.ArmLeft != null) && rig.ArmRenderers.Length > 0)
            {
                _armIK = new ArmIK(system, this);
                _armRightBones = ResolveArm(weapon.transform, rig.Data.ArmRight);
                _armLeftBones = ResolveArm(weapon.transform, rig.Data.ArmLeft);
            }
            _gripHand = ModHand.Find(weapon.transform, rig.Data.ArmRight);
            _isKit = IsKit(weapon);
            _supportHand = ModHand.Find(weapon.transform, rig.Data.ArmLeft);
            if (rig.Data.Action == ActionType.Throwable && !_isKit && !GrenadeInFist.Contains(rig.Data))
            {
                // A grenade sits in the fist: its drawn middle where a grip goes. Held by where the throw animation's palm
                // was, it hung against the side of the glove, in the air (user, 2026-10-16). Hand-sized things only.
                Bounds gb = GunCollider.VisibleBounds(rig, out bool gMeasured, ThrowableHandler.FindModPin(weapon.transform));
                if (gMeasured)
                {
                    // Only once it measured something (a failed measure is tried again on the next draw; 255.I5-1), and
                    // without its pin or ring, which pulled the middle toward the top.
                    GrenadeInFist.Add(rig.Data);
                    if (Mathf.Max(gb.size.x, Mathf.Max(gb.size.y, gb.size.z)) < 0.2f && Vector3.Distance(gb.center, rig.Data.GripPoint) > 0.01f)
                    {
                        Log.Info($"{rig.Data.Key}: held by its middle, in the fist ({Vector3.Distance(gb.center, rig.Data.GripPoint) * 100f:0.0} cm from the animation's palm).");
                        rig.Data.GripPoint = gb.center;
                    }
                }
            }
            ActionType action = rig.Data.Action;
            RackPointGun = rig.Data.RackPoint;
            if (rig.Data.SlidePath == null && (action == ActionType.SelfLoading || action == ActionType.Manual || action == ActionType.ShellFed)
                || rig.Data.CarveHandle || rig.Data.AddedHandle)
            {
                // No part of this model moves when the action is worked (or only a carrier inside the receiver does): give
                // it a handle that does.
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

        /// <summary>What kind of weapon this is, for the hands' poses.</summary>
        private readonly WeaponClass _weaponClass;

        private static WeaponClass ClassOf(WeaponRig rig, Weapon weapon)
        {
            WeaponRigData d = rig.Data;
            switch (d.Action)
            {
                case ActionType.Throwable:
                    return WeaponClass.Throwable;
                case ActionType.Melee:
                    return GunCollider.VisibleBounds(rig, out _).size.magnitude > 0.55f ? WeaponClass.LongMelee : WeaponClass.Knife;
            }
            if (d.IsPistol)
            {
                return WeaponClass.Pistol;
            }
            if (d.IsMachineGun)
            {
                return WeaponClass.MachineGun;
            }
            return IsLauncher(d, weapon) ? WeaponClass.Launcher : WeaponClass.Rifle;
        }

        private static readonly System.Text.RegularExpressions.Regex LauncherName = new System.Text.RegularExpressions.Regex(
            @"rocket|missile|launcher|rpg|panzer|bazooka|at-?4|smaw|javelin|stinger|carl|law|grenade|thumper|m79|(^|[^a-z])gl($|[^a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// A launcher: one round, a long tube, and a round that explodes (a rocket, missile or grenade) or a launcher's name.
        /// Any one-round long gun was one, and a single-shot rifle got a launcher's hold and kick (255.I5-2).
        /// </summary>
        internal static bool IsLauncher(WeaponRigData d, Weapon w)
        {
            if (d.Action != ActionType.SingleShot || w == null || w.configuration == null || w.configuration.ammo > 1
                || Mathf.Abs(Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis)) <= 0.4f)
            {
                return false;
            }
            GameObject prefab = w.configuration.projectilePrefab;
            Projectile p = prefab != null ? prefab.GetComponent<Projectile>() : null;
            return p == null || p is ExplodingProjectile || p is GrenadeProjectile || LauncherName.IsMatch(w.name);
        }

        /// <summary>Parts a sight mode moves (a flip magnifier), kept where the selected mode puts them, aimed or not.</summary>
        private Transform[] _sightParts;
        private RigidPose[] _sightPartsRest;
        /// <summary>The mode the parts swing to, how far the swing is (0..1), where each part started it and where it is drawn now (gun space).</summary>
        private int _sightTarget = -1;
        private float _sightSwing = 1f;
        private RigidPose[] _sightFrom, _sightNow;

        private void ApplySightParts()
        {
            RigidPose[][] poses = Rig.Data.SightPartPoses;
            if (poses == null || Weapon == null)
            {
                return;
            }
            if (_sightParts == null)
            {
                _sightParts = new Transform[poses.Length];
                _sightPartsRest = new RigidPose[poses.Length];
                _sightFrom = new RigidPose[poses.Length];
                _sightNow = new RigidPose[poses.Length];
                for (int p = 0; p < poses.Length; p++)
                {
                    _sightParts[p] = WeaponRig.Resolve(Weapon.transform, Rig.Data.SightPartPaths[p]);
                    if (_sightParts[p] != null)
                    {
                        _sightPartsRest[p] = new RigidPose(_sightParts[p].localPosition, _sightParts[p].localRotation);
                    }
                }
            }
            int modes = poses[0].Length;
            int target = Mathf.Clamp(Weapon.GetSightModeIndex(), 0, modes - 1);
            if (_sightTarget < 0)
            {
                _sightTarget = target;
                _sightSwing = 1f;
            }
            else if (target != _sightTarget)
            {
                // Swung straight from where each part is drawn now (even mid-swing) to the new mode's place: stepping
                // through the modes in between swung a three-mode sight through its middle mode on the way back to the first.
                for (int p = 0; p < _sightParts.Length; p++)
                {
                    _sightFrom[p] = _sightNow[p];
                }
                _sightTarget = target;
                _sightSwing = 0f;
            }
            // Over about a sixth of a second, as a hand flips it, easing in and out (it started and stopped at full speed).
            _sightSwing = Mathf.MoveTowards(_sightSwing, 1f, Time.unscaledDeltaTime * 6f);
            float e = _sightSwing * _sightSwing * (3f - 2f * _sightSwing);
            RigidPose gun = Rig.GunBodyPose;
            for (int p = 0; p < _sightParts.Length; p++)
            {
                Transform t = _sightParts[p];
                if (t == null)
                {
                    continue;
                }
                RigidPose to = poses[p][_sightTarget];
                RigidPose local = e >= 1f ? to
                    : new RigidPose(Vector3.Lerp(_sightFrom[p].position, to.position, e), Quaternion.Slerp(_sightFrom[p].rotation, to.rotation, e));
                _sightNow[p] = local;
                gun.Multiply(local).ApplyTo(t);
            }
        }

        /// <summary>The gun model's own belt (see <see cref="WeaponRigData.BeltPartPaths"/>), with the scales it had.</summary>
        private Transform[] _beltParts;
        private Vector3[] _beltScales;
        private bool _beltHidden;

        /// <summary>How many belt parts the model has (0: the mod's own strip shows a laid belt).</summary>
        internal int BeltPartCount => Rig.Data.BeltPartPaths.Count;

        /// <summary>The model's belt shows only while a box is in and its belt lies on the feed tray.</summary>
        private void ApplyBeltParts(AmmoState ammo)
        {
            if (Rig.Data.BeltPartPaths.Count == 0 || ammo == null || !ammo.HasCover)
            {
                return;
            }
            if (_beltParts == null)
            {
                _beltParts = new Transform[Rig.Data.BeltPartPaths.Count];
                _beltScales = new Vector3[_beltParts.Length];
                for (int i = 0; i < _beltParts.Length; i++)
                {
                    _beltParts[i] = WeaponRig.Resolve(Weapon.transform, Rig.Data.BeltPartPaths[i]);
                    _beltScales[i] = _beltParts[i] != null ? _beltParts[i].localScale : Vector3.one;
                }
            }
            bool hide = !ammo.MagInserted || !ammo.BeltLaid;
            if (hide == _beltHidden)
            {
                return;
            }
            _beltHidden = hide;
            for (int i = 0; i < _beltParts.Length; i++)
            {
                if (_beltParts[i] != null)
                {
                    _beltParts[i].localScale = hide ? Vector3.one * 1e-4f : _beltScales[i];
                }
            }
        }

        /// <summary>
        /// A weapon whose mod left its muzzle list empty used up the round and played the shot, but nothing left the barrel
        /// (the game only fires from listed muzzles): it gets one at the muzzle the analysis found.
        /// </summary>
        private static void GiveMuzzle(Weapon weapon, WeaponRig rig)
        {
            Weapon.Configuration c = weapon.configuration;
            if (c == null || c.muzzles != null && c.muzzles.Length > 0 && c.muzzles[0] != null || rig.GunBody == null
                || rig.Data.Action == ActionType.Melee || rig.Data.Action == ActionType.Throwable)
            {
                return;
            }
            var muzzle = new GameObject("RavenfieldVR Muzzle").transform;
            rig.AttachInGunSpace(muzzle, rig.Data.MuzzlePoint, Quaternion.LookRotation(rig.Data.AimAxis, rig.Data.Up));
            c.muzzles = new[] { muzzle };
            Log.InfoOnce("muzzle:" + rig.Data.Key, $"{rig.Data.Key}: its mod lists no muzzle, so it shot nothing: given one at the analysed muzzle.");
        }

        /// <summary>Renderers that show the weapon's own scope camera's picture (the lens of a magnifier or scope).</summary>
        private readonly List<Renderer> _scopeLenses = new List<Renderer>();

        private void FindScopeLenses(Weapon weapon)
        {
            Camera cam = weapon.renderTextureCamera;
            RenderTexture rt = cam != null ? cam.targetTexture : null;
            if (rt == null)
            {
                return;
            }
            foreach (Renderer r in weapon.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null)
                    {
                        continue;
                    }
                    bool shows = false;
                    foreach (string prop in m.GetTexturePropertyNames())
                    {
                        shows |= m.GetTexture(prop) == rt;
                    }
                    if (shows && !_scopeLenses.Contains(r))
                    {
                        _scopeLenses.Add(r);
                    }
                }
            }
            // What is drawn on the lens goes with it (the HK416-B's magnifier: a portal over its picture, black seen off-axis).
            int lenses = _scopeLenses.Count;
            for (int i = 0; i < lenses; i++)
            {
                foreach (Renderer child in _scopeLenses[i].GetComponentsInChildren<Renderer>(true))
                {
                    if (!_scopeLenses.Contains(child) && !(child is ParticleSystemRenderer))
                    {
                        _scopeLenses.Add(child);
                    }
                }
            }
            if (_scopeLenses.Count > 0 && Rig.Data.Sights != null && Rig.Data.Sights.Length > 1)
            {
                Log.InfoOnce("scopelens:" + Rig.Data.Key, $"{Rig.Data.Key}: its scope picture ({_scopeLenses.Count} lens renderer(s)) is shown only in sight modes that look through it.");
            }
        }

        /// <summary>
        /// A magnifier with its own scope camera, flipped out of the line of sight (an HK416's G33 below its EOTech): its
        /// lens showed a black disc right where the eye looks over it (a portal drawn on the lens is black seen off-axis,
        /// and a camera that flips with it films the inside of the mount). Shown only while the selected sight mode's line
        /// runs through the camera and through the lens itself; otherwise the lens is clear glass. The HK416-B's camera
        /// does not move with the magnifier, so the camera alone said "in use" in both modes.
        /// </summary>
        private void UpdateScopeLenses()
        {
            if (_scopeLenses.Count == 0 || Rig.Data.Sights == null || Rig.Data.Sights.Length < 2 || Weapon == null)
            {
                return;
            }
            Camera cam = Weapon.renderTextureCamera;
            SightLine sight = CurrentSight;
            bool cameraOnLine = cam == null || sight == null || OffSightLine(sight, cam.transform.position) < 0.015f;
            foreach (Renderer r in _scopeLenses)
            {
                if (r == null)
                {
                    continue;
                }
                bool inUse = cameraOnLine && (sight == null || LensOnLine(sight, r));
                if (r.enabled != inUse)
                {
                    r.enabled = inUse;
                }
            }
        }

        private float OffSightLine(SightLine sight, Vector3 world) =>
            Vector3.ProjectOnPlane(Rig.ToGun(world) - sight.EyePoint, sight.EyeDirection).magnitude;

        /// <summary>The sight line passes through this lens (its centre within 1.5 cm, or within most of its radius).</summary>
        private bool LensOnLine(SightLine sight, Renderer lens)
        {
            Bounds b = lens.bounds; // a disabled renderer keeps its bounds
            float radius = Mathf.Max(b.extents.x, Mathf.Max(b.extents.y, b.extents.z));
            return OffSightLine(sight, b.center) < Mathf.Max(0.015f, radius * 0.6f);
        }

        /// <summary>Self-test: the scope lenses (shown or hidden) and how far the scope camera is off the selected sight line.</summary>
        internal string ScopeLensState()
        {
            if (_scopeLenses.Count == 0)
            {
                return "no lens showing a scope camera";
            }
            Camera cam = Weapon != null ? Weapon.renderTextureCamera : null;
            SightLine sight = CurrentSight;
            string off = cam != null && sight != null ? $"{OffSightLine(sight, cam.transform.position) * 100f:0.0} cm" : "unknown";
            string lensOff = sight != null && _scopeLenses.Count > 0 && _scopeLenses[0] != null
                ? $", lens {OffSightLine(sight, _scopeLenses[0].bounds.center) * 100f:0.0} cm off it"
                : "";
            int shown = 0;
            foreach (Renderer r in _scopeLenses)
            {
                shown += r != null && r.enabled ? 1 : 0;
            }
            return $"lens {shown}/{_scopeLenses.Count} shown ({string.Join(", ", _scopeLenses.ConvertAll(r => r != null ? r.name : "-"))}), scope camera {off} off the sight line{lensOff}, sights known {(Rig.Data.Sights != null ? Rig.Data.Sights.Length : 0)}";
        }

        private Transform[] _spareParts;
        private Vector3[] _spareScales;

        /// <summary>The flat reload's spare magazines are not drawn while the weapon is held (see WeaponRigData.SparePartPaths).</summary>
        private void HideSpareParts()
        {
            List<int[]> paths = Rig.Data.SparePartPaths;
            if (paths.Count == 0 || Weapon == null)
            {
                return;
            }
            if (_spareParts == null)
            {
                _spareParts = new Transform[paths.Count];
                _spareScales = new Vector3[paths.Count];
                for (int i = 0; i < paths.Count; i++)
                {
                    _spareParts[i] = WeaponRig.Resolve(Weapon.transform, paths[i]);
                    _spareScales[i] = _spareParts[i] != null ? _spareParts[i].localScale : Vector3.one;
                }
            }
            Vector3 tiny = Vector3.one * 1e-4f;
            foreach (Transform t in _spareParts)
            {
                if (t != null && t.localScale != tiny) // re-hidden only when an animation has scaled it back up
                {
                    t.localScale = tiny;
                }
            }
        }

        /// <summary>Self-test: how many of the flat reload's spare magazines are drawn now (0 while held).</summary>
        internal int SparePartsShown()
        {
            int n = 0;
            if (_spareParts != null)
            {
                foreach (Transform t in _spareParts)
                {
                    n += t != null && t.lossyScale.sqrMagnitude > 1e-6f ? 1 : 0;
                }
            }
            return n;
        }

        /// <summary>Self-test: how far (mm) the parts a sight mode moves are from where the selected mode puts them; -1 without such parts.</summary>
        internal float SightPartsOffMm()
        {
            RigidPose[][] poses = Rig.Data.SightPartPoses;
            if (poses == null || _sightParts == null)
            {
                return -1f;
            }
            int mode = Mathf.Clamp(Weapon.GetSightModeIndex(), 0, poses[0].Length - 1);
            float worst = 0f;
            for (int p = 0; p < _sightParts.Length; p++)
            {
                if (_sightParts[p] != null)
                {
                    worst = Mathf.Max(worst, Vector3.Distance(_sightParts[p].position, Rig.GunBodyPose.TransformPoint(poses[p][mode].position)));
                }
            }
            return worst * 1000f;
        }

        private void RestoreSightParts()
        {
            if (_sightParts == null)
            {
                return;
            }
            for (int p = 0; p < _sightParts.Length; p++)
            {
                if (_sightParts[p] != null)
                {
                    _sightParts[p].localPosition = _sightPartsRest[p].position;
                    _sightParts[p].localRotation = _sightPartsRest[p].rotation;
                }
            }
        }

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

        // ---- weight: inertia (Tasks 208 / 204) -------------------------------------------------------

        /// <summary>How quickly the weapon follows the hand's accelerations (rad/s; 0: at once). Heavier is slower.</summary>
        private readonly float _inertiaOmega;
        /// <summary>Degrees the muzzle dips per m/s² the hand accelerates upward (a long, heavy muzzle lags behind the grip).</summary>
        private readonly float _inertiaLever;
        private Vector3 _lagPos, _lagVel, _handAcc;
        private const int VelHist = 16;
        private readonly Vector3[] _velHist = new Vector3[VelHist];
        private readonly float[] _velHistT = new float[VelHist];
        private int _velHistHead, _velHistCount;
        private float _lagPitch, _lagPitchVel;
        private bool _lagInit;

        /// <summary>Self-test: how far the drawn grip lags the hand now (metres) and the muzzle dips (degrees).</summary>
        internal Vector3 InertiaLag => _lagPos;
        internal float InertiaPitch => _lagPitch;

        private static void Inertia(WeaponRigData d, Weapon w, out float omega, out float lever)
        {
            float length = Mathf.Abs(Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis));
            if (d.IsPistol)
            {
                omega = 0f; lever = 0f; // a pistol stays snappy
            }
            else if (d.IsMachineGun)
            {
                omega = 11f; lever = 0.7f;
            }
            else if (IsLauncher(d, w))
            {
                omega = 12f; lever = 0.6f; // launchers
            }
            else if (d.Action == ActionType.ShellFed || d.Action == ActionType.BreakOpen || d.Action == ActionType.Manual && w.configuration.projectilesPerShot > 1)
            {
                omega = 18f; lever = 0.4f; // shotguns
            }
            else if (length < 0.32f)
            {
                omega = 26f; lever = 0.2f; // SMGs, PDWs
            }
            else
            {
                omega = 20f; lever = 0.35f; // rifles, DMRs, snipers
            }
        }

        /// <summary>
        /// The weapon has mass: it lags the hand when the hand speeds up or stops, settles with a little overshoot, and a long
        /// heavy one's muzzle dips when the hand jerks up. Driven by the hand's acceleration only, so a steady hand (aiming,
        /// a steady sweep) has the weapon exactly in it. Held with both hands or against the shoulder it is much stiffer;
        /// pistols do not lag at all.
        /// </summary>
        private void StepInertia(VRHand main, float dt)
        {
            if (!VRConfig.WeaponWeight.Value || _inertiaOmega <= 0f || !main.Tracked)
            {
                _lagPos = _lagVel = Vector3.zero;
                _lagPitch = _lagPitchVel = 0f;
                _lagInit = false;
                return;
            }
            Vector3 v = main.BodyRelativeVelocity;
            float now = Time.unscaledTime;
            if (!_lagInit)
            {
                _velHistCount = 0;
                _handAcc = Vector3.zero;
                _lagInit = true;
            }
            // The change of the hand's velocity over about 50 ms, in real time: from one frame to the next, the few cm/s the
            // controller's velocity jitters were several m/s^2 and shook a launcher held still (255.I5-4, Tasks 203); over the
            // clamped step a long frame overstated it (255.B1-3).
            _velHistHead = (_velHistHead + 1) % VelHist;
            _velHist[_velHistHead] = v;
            _velHistT[_velHistHead] = now;
            _velHistCount = Mathf.Min(_velHistCount + 1, VelHist);
            int back = -1;
            for (int k = 1; k < _velHistCount; k++)
            {
                back = (_velHistHead - k + VelHist) % VelHist;
                if (now - _velHistT[back] >= 0.05f)
                {
                    break;
                }
            }
            Vector3 raw = back >= 0 && now - _velHistT[back] > 1e-4f ? (v - _velHist[back]) / (now - _velHistT[back]) : Vector3.zero;
            raw = Vector3.ClampMagnitude(raw, 40f); // a tracking jump is no acceleration
            // The controller's reported velocity jitters a few cm/s from frame to frame: differenced, that is several m/s^2
            // of acceleration nobody made, and the lag spring shook a heavy launcher held still (Tasks 203, "RPGs shake in
            // the hand without being fired"). A soft dead band under 1.2 m/s^2 and a slower smoothing: a real lift is
            // still felt, the tremor of the tracking is not.
            float rawMag = raw.magnitude;
            raw = rawMag > 1.2f ? raw * ((rawMag - 1.2f) / rawMag) : Vector3.zero;
            _handAcc = Vector3.Lerp(_handAcc, raw, 1f - Mathf.Exp(-12f * dt));
            bool supported = Supported || Shouldered;
            float omega = _inertiaOmega * (supported ? 2.2f : 1f);
            // A launcher's weight settles without bobbing: at 0.55 the spring overshot and the tube rocked at about 2 Hz with
            // every small move of the hand (user, 2026-10-16: "rpgs still have alot of shaking").
            bool launcher = _weaponClass == WeaponClass.Launcher;
            float zeta = launcher ? (supported ? 1f : 0.9f) : supported ? 0.8f : 0.55f;
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt / 0.006f), 1, 8);
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                Vector3 a = -omega * omega * _lagPos - 2f * zeta * omega * _lagVel - _handAcc;
                _lagVel += a * h;
                _lagPos += _lagVel * h;
                float wr = omega * 1.2f;
                // The forcing times the spring's stiffness, so the dip settles at the lever's degrees per m/s^2 (without it
                // the dip was a few thousandths of a degree: the muzzle never lagged a quick lift).
                float ap = -wr * wr * _lagPitch - 2f * zeta * wr * _lagPitchVel + wr * wr * _inertiaLever * (supported ? 0.3f : 1f) * Vector3.Dot(_handAcc, Vector3.up);
                _lagPitchVel += ap * h;
                _lagPitch += _lagPitchVel * h;
            }
            if (_lagPos.sqrMagnitude > 0.08f * 0.08f)
            {
                _lagPos = _lagPos.normalized * 0.08f;
                _lagVel = Vector3.ProjectOnPlane(_lagVel, _lagPos);
            }
            _lagPitch = Mathf.Clamp(_lagPitch, -10f, 10f);
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
            else if (IsLauncher(d, w))
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
        /// <summary>How far the drawn gun has gone over to where the world holds it (0 at the hand .. 1 at the stand-in).</summary>
        private float _blockBlend;
        private RigidPose _blockedPose;

        /// <summary>Self-test: the drawn gun is held back by the world this frame.</summary>
        internal bool HeldBackByWorld => _blockBlend > 0.5f;

        private bool _wasBlocked;
        /// <summary>Where the weapon's root was drawn last (world), for the next Update's checks (see Update).</summary>
        private RigidPose _renderedRoot;
        private bool _renderedRootSet;
        private float _contactKnockAt = -1f;
        /// <summary>Self-test: knocks felt when the held gun met the world.</summary>
        internal static int ContactKnocks { get; private set; }
        /// <summary>Shadow-only renderers switched off while the weapon is held (given back on release).</summary>
        private readonly List<Renderer> _shadowOnly = new List<Renderer>();
        /// <summary>The weapon's casing particles, hidden while held (real cases fly instead).</summary>
        private readonly List<ParticleSystemRenderer> _hiddenCasings = new List<ParticleSystemRenderer>();

        public void Update()
        {
            if (_renderedRootSet && Weapon != null && Weapon.transform.parent != null)
            {
                // The weapon hangs under the game's player, which moved since the weapon was drawn (walking 4 cm a frame,
                // running 8): the hands are still where they were drawn, so the grabs, the magazine's rail and the rack
                // compared a hand against a gun carried on by the step, and while walking or running magazines fell off
                // their rail and grabs missed (user, 2026-10-16: "when walking or running the hands glitch and it's almost
                // impossible to reload"). Back where it was drawn for this frame's checks; BeforeRender places it again.
                _renderedRoot.ApplyTo(Weapon.transform);
            }
            _gunCollider?.Update();
            VRHand main = _system.Rig.Dominant;
            if (main.Held is MagazineProp inHand && inHand.HeldBy == main)
            {
                // The game drew a weapon into a hand holding a magazine (a seat left, a respawn, the weapon button): the
                // magazine is let go. Taking the hand over left it hanging in the air for the rest of the map.
                inHand.Drop(main.ThrowVelocity(), main.AngularVelocity);
            }
            main.Role = HandRole.Weapon;
            if (main.Model != null)
            {
                main.Model.WeaponGripCurls = Rig.Data.GripCurls;
                main.Model.WeaponIsPistol = Rig.Data.IsPistol;
                main.Model.WeaponClass = _weaponClass;
            }
            if (_system.Rig.Off.Model != null)
            {
                _system.Rig.Off.Model.WeaponSupportCurls = Rig.Data.SupportCurls;
                _system.Rig.Off.Model.WeaponClass = _weaponClass;
            }
            Weapon active = Weapon.GetActiveSubWeapon() ?? Weapon;
            if (active != ActiveWeapon)
            {
                Reload.Release();
                ActiveWeapon = active;
                ActiveRig = active == Weapon ? Rig : SubWeaponRig(active);
                ActiveAmmo = active == Weapon ? Ammo : _system.StateFor(active, create: true);
                if (active != Weapon && ReferenceEquals(ActiveRig, Rig) && ActiveAmmo != null && !ReferenceEquals(ActiveAmmo, Ammo))
                {
                    ActiveAmmo.MagazineOf = Ammo; // a fire-mode variant on this model: this gun's magazine is its magazine
                }
            }
            UpdateAim();
            Reload.Update();
            HoldSlideLockPose();
            LeaveEmptyPose();
            TurnEmittersForward();
        }

        private Transform[] _emitters;
        private readonly HashSet<Transform> _emittersTurned = new HashSet<Transform>();

        /// <summary>
        /// A laser or flashlight that points back past the shooter is turned round to shine along the barrel: the MK18RII's
        /// light and laser lit up the ground behind the player (user, 2026-10-16), its emitters facing about 180 degrees from
        /// the bore. Checked every frame before the game's lasers cast their rays (a script may set them again); one tilted
        /// on purpose (under 120 degrees off) is left alone.
        /// </summary>
        private void TurnEmittersForward()
        {
            if (_emitters == null)
            {
                var found = new List<Transform>();
                foreach (LaserSight l in Weapon.GetComponentsInChildren<LaserSight>(true))
                {
                    found.Add(l.transform);
                }
                foreach (Light l in Weapon.GetComponentsInChildren<Light>(true))
                {
                    if (l.type == LightType.Spot && !found.Contains(l.transform))
                    {
                        found.Add(l.transform);
                    }
                }
                _emitters = found.ToArray();
            }
            if (_emitters.Length == 0 || Rig.GunBody == null)
            {
                return;
            }
            Vector3 aim = Rig.AimWorld;
            foreach (Transform e in _emitters)
            {
                if (e == null || !e.gameObject.activeInHierarchy || Vector3.Angle(e.forward, aim) <= 120f)
                {
                    continue;
                }
                e.localRotation *= Quaternion.Euler(0f, 180f, 0f);
                if (_emittersTurned.Add(e))
                {
                    Log.Info($"{Weapon.name}: '{e.name}' shone backwards, past the shooter; turned round to shine along the barrel.");
                }
            }
        }

        /// <summary>The animator's state (layer 0) last seen at rest with a round loaded, and the one it sat in empty.</summary>
        private int _loadedRestState, _emptyState;

        /// <summary>Self-test: the animator's state now against the loaded rest and empty poses it learned, and the game's round count.</summary>
        internal string AnimatorStateText
        {
            get
            {
                Weapon w = ActiveWeapon;
                if (w == null || !w.HasActiveAnimator() || w.animator.layerCount == 0)
                {
                    return "no animator";
                }
                Animator a = w.animator;
                int now = a.GetCurrentAnimatorStateInfo(0).fullPathHash;
                string which = now == _loadedRestState ? "the loaded rest pose" : now == _emptyState ? "THE EMPTY POSE" : $"state {now:X8}";
                return $"animator in {which}{(a.IsInTransition(0) ? " (in a transition)" : "")}, game rounds {w.ammo}";
            }
        }
        /// <summary>
        /// The loaded and empty poses per weapon type, kept past a holster: a gun drawn empty never learned its loaded pose in
        /// that holding, and the next frames stored the empty pose as the loaded one (255.B1-1).
        /// </summary>
        private static readonly Dictionary<WeaponRigData, (int loaded, int empty)> AnimatorPoses = new Dictionary<WeaponRigData, (int loaded, int empty)>();
        /// <summary>When the game's round count last went from none to some (-1: not waiting to check the pose).</summary>
        private float _refilledAt = -1f;
        private bool _wasEmpty;
        private float _lastShotAt = -10f;

        /// <summary>
        /// Many pistols' animators go to an empty pose (slide back) when the last round is fired and leave it only through
        /// the game's reload animation, which a hand reload never plays: racked or released, the slide stayed back until the
        /// first shot (FNX-45 family; user, 2026-10-16). A third of a second after the count goes from none to some, an
        /// animator still in the pose it held empty is sent to the one it last rested in loaded. Guns whose animator leaves
        /// that pose by itself, or never had one, are left alone (the states differ, or they are the same).
        /// </summary>
        private void LeaveEmptyPose()
        {
            Weapon w = ActiveWeapon;
            AmmoState ammo = ActiveAmmo;
            if (w == null || ammo == null || !ammo.Participates || !ammo.UsesChamber || !w.HasActiveAnimator() || w.animator.layerCount == 0)
            {
                return;
            }
            Animator a = w.animator;
            if (a.IsInTransition(0) || w.reloading || Reload.IsRacking)
            {
                return;
            }
            WeaponRigData key = ActiveRig != null ? ActiveRig.Data : Rig.Data;
            if (_loadedRestState == 0 && AnimatorPoses.TryGetValue(key, out (int loaded, int empty) known))
            {
                _loadedRestState = known.loaded;
                _emptyState = _emptyState != 0 ? _emptyState : known.empty;
            }
            int now = a.GetCurrentAnimatorStateInfo(0).fullPathHash;
            if (w.ammo <= 0)
            {
                if (Time.unscaledTime - _lastShotAt > 0.4f)
                {
                    _emptyState = now;
                    AnimatorPoses[key] = (_loadedRestState, now);
                }
                _wasEmpty = true;
                _refilledAt = -1f;
                return;
            }
            if (_wasEmpty)
            {
                _wasEmpty = false;
                _refilledAt = Time.unscaledTime;
            }
            if (_refilledAt >= 0f)
            {
                if (Time.unscaledTime - _refilledAt < 0.3f)
                {
                    return;
                }
                _refilledAt = -1f;
                if (now == _emptyState && _loadedRestState != 0 && _loadedRestState != now && a.HasState(0, _loadedRestState))
                {
                    a.CrossFadeInFixedTime(_loadedRestState, 0.08f, 0);
                    Log.InfoOnce($"empty-pose:{Rig.Data.Key}", $"{Rig.Data.Key}: the animator stayed in its empty pose with a round loaded; sent back to its loaded rest pose.");
                }
                return;
            }
            // Never the pose it held empty (a gun drawn empty is still in it for a moment after its first round is in).
            if (Time.unscaledTime - _lastShotAt > 0.6f && now != _emptyState)
            {
                _loadedRestState = now;
                AnimatorPoses[key] = (now, _emptyState);
            }
        }

        /// <summary>
        /// Many pistols show the slide locked back from the animator's "loaded ammo" being 0. A fresh magazine in a gun with
        /// its slide locked made the game count the rounds as loaded, and the animator ran the slide forward while the gun
        /// still said RACK (and needed racking). Held at 0 until the slide is released or racked; every frame, because the
        /// release does not always change the round count (and so never told the animator).
        /// </summary>
        /// <summary>Self-test experiment: -rfvr-noslidepose leaves the animator's "loaded ammo" to the game.</summary>
        private static readonly bool NoSlidePose = System.Array.Exists(System.Environment.GetCommandLineArgs(), a => a.Equals("-rfvr-noslidepose", System.StringComparison.OrdinalIgnoreCase));

        private void HoldSlideLockPose()
        {
            if (NoSlidePose)
            {
                return;
            }
            Weapon w = ActiveWeapon;
            AmmoState ammo = ActiveAmmo;
            if (w == null || ammo == null || !ammo.Participates || !w.notifyAnimatorAmmo || !w.HasActiveAnimator())
            {
                return;
            }
            int shown = ammo.SlideLocked ? 0 : w.ammo;
            if (w.animator.GetInteger(Weapon.LOADED_AMMO_PARAMETER_HASH) != shown)
            {
                w.animator.SetInteger(Weapon.LOADED_AMMO_PARAMETER_HASH, shown);
            }
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
            if (aim && !Aiming)
            {
                _system.Rig.Dominant.Haptic(0.012f, 160f, 0.18f); // the sight taken: a light tick (85-agent review)
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
            _lastShotAt = Time.unscaledTime;
            // A sub weapon kicks as itself: its own rig, class and one-handed multiplier, not the parent's (an under-barrel
            // launcher on a rifle got the rifle's gentle floor and climb; a fire-mode variant shares the parent rig and so
            // reads the same as it did).
            Weapon sub = fired != null && fired != Weapon ? fired : null;
            WeaponRig firedRig = sub != null ? SubWeaponRig(sub) : Rig;
            WeaponRigData fd = firedRig.Data;
            WeaponClass kind = sub != null ? ClassOf(firedRig, sub) : _weaponClass;
            float oneHand = _oneHandRecoil;
            if (sub != null)
            {
                Handling(fd, sub, out oneHand, out _, out _);
            }
            Weapon.Configuration c = (fired != null ? fired : Weapon).configuration;
            float strength = VRConfig.RecoilStrength.Value;
            if (Shouldered)
            {
                strength *= kind == WeaponClass.Launcher ? 0.9f : 0.75f; // the shoulder takes part of it (little of a launcher's)
            }
            if (Supported)
            {
                // Long guns: the support hand takes part of it; a pistol cupped in two hands a good part.
                strength *= fd.IsPistol ? 0.6f : 1f - VRConfig.TwoHandRecoilReduction.Value;
            }
            else
            {
                strength *= fd.Handgun ? oneHand : oneHand * (1f - VRConfig.TwoHandRecoilReduction.Value);
            }
            float kick = ShotKick(c, fd, kind, out float climbShare);
            float random = Mathf.Clamp(c.randomKick, 0f, 5f);
            // Critically damped spring peak = v0 / (omega * e), omega ~ 12 (StepRecoil): ~0.9 m/s per kick unit gives ~2.7 cm
            // back, ~180 deg/s per kick unit ~5.4 degrees of muzzle climb per shot, before the hand factors (a rifle in two
            // hands about 3.5 degrees and 2 cm, in one hand about 9; a pistol in one hand about 10). The stiffer spring and
            // smaller impulses before made it a 1-degree flick hardly anyone saw in the headset (Tasks 238). A pistol's kick is
            // more flip than push, a heavy rifle's more push than flip.
            _recoilPosVel += new Vector3(0f, 0.1f * kick, -0.9f * kick * Mathf.Max(0.3f, 2f - climbShare)) * strength;
            _recoilRotVel += new Vector2(-(180f * kick * climbShare + Random.Range(0f, 40f * random)), Random.Range(-25f, 25f) * random) * strength;
            PeakShotImpulse = Mathf.Max(PeakShotImpulse, 180f * kick * climbShare * strength);

            float amp = Mathf.Clamp01(0.35f + kick * 0.15f);
            _system.Rig.Dominant.Haptic(kind == WeaponClass.Launcher ? 0.18f : 0.03f + kick * 0.01f, kind == WeaponClass.Launcher ? 90f : 180f, amp);
            if (kind == WeaponClass.Launcher && !TwoHanded)
            {
                _system.Rig.Off.Haptic(0.12f, 90f, amp * 0.6f); // the whole launcher thumps, whichever hand is on it
            }
            _system.Rig.ShakeView(kick * (c.loud ? 1f : 0.6f)); // a suppressed gun jolts less (Tasks 211)
            if (TwoHanded)
            {
                _system.Rig.Off.Haptic(0.03f, 160f, amp * 0.7f);
            }
            Reload.OnShot();
            // The action throws the case out with the shot: a self-loading gun (or any gun with assisted reloading, whose
            // fire animation works the bolt), or one with no ammo to count (an infinite-ammo gatling still throws cases;
            // its flat casing particles are hidden like every other gun's).
            if (ActiveAmmo != null && ActiveAmmo.Participates ? ActiveAmmo.CyclesItself : ActiveRig.Data.Action == ActionType.NoAmmo)
            {
                Casings.Eject(this);
            }
        }

        /// <summary>
        /// How hard one shot kicks, in kick units (1: about 1.4 cm back and 3.4 degrees of climb before the hand factors):
        /// the most of the flat game's kick, the round's own push (its damage, muzzle speed and pellets stand in for calibre
        /// and load) and a floor for the kind of gun. Many mods leave the flat kick near nothing and shake the view with the
        /// fire animation instead, which VR does not play: their pistols and sniper rifles hardly moved in the hand.
        /// <paramref name="climbShare"/>: how much of it is muzzle flip (1 as before; a pistol more, a bolt rifle less).
        /// </summary>
        internal static float ShotKick(Weapon.Configuration c, WeaponRigData d, WeaponClass kind, out float climbShare)
        {
            float flat = Mathf.Clamp(c.kickback, 0f, 10f);
            float round = 0f;
            Projectile p = c.projectilePrefab != null ? c.projectilePrefab.GetComponent<Projectile>() : null;
            if (p != null && p.configuration != null)
            {
                float pellets = Mathf.Pow(Mathf.Max(1, c.projectilesPerShot), 0.7f);
                float power = Mathf.Clamp(p.configuration.damage * pellets / 60f, 0.25f, 3.5f);
                round = power * Mathf.Clamp(Mathf.Sqrt(Mathf.Max(p.configuration.speed, 1f) / 500f), 0.6f, 1.5f);
            }
            bool shotgun = c.projectilesPerShot > 1 && (d.Action == ActionType.ShellFed || d.Action == ActionType.BreakOpen || d.Action == ActionType.Manual);
            bool boltRifle = d.CyclesByHand && !shotgun && kind == WeaponClass.Rifle;
            float floor;
            switch (kind)
            {
                case WeaponClass.Pistol: floor = 0.9f; climbShare = 1.8f; break; // flips up in the hand
                case WeaponClass.MachineGun: floor = 0.7f; climbShare = 0.9f; break;
                // A launcher throws the tube up and back hard (user, 2026-10-16: "actually add more recoil when shot").
                case WeaponClass.Launcher: floor = 2.6f; climbShare = 1f; break;
                default:
                    floor = shotgun ? 1.6f : boltRifle ? 1.5f : 0.7f;
                    climbShare = boltRifle ? 0.85f : 1f;
                    break;
            }
            float kick = Mathf.Max(flat, round, floor);
            if (kind == WeaponClass.Launcher)
            {
                kick *= 1.4f;
            }
            return Mathf.Clamp(kick, 0.5f, 10f);
        }

        /// <summary>
        /// A push on the gun from the hands working it (world velocity in m/s, muzzle dip in deg/s): racking tugs it toward
        /// the pull, a slide slamming home or a magazine seating jolts it. The recoil spring takes it and settles it back.
        /// Light guns move more than heavy ones.
        /// </summary>
        internal void Nudge(Vector3 velocityWorld, float dip = 0f)
        {
            float mass = Rig.Data.Handgun ? 1.3f : Rig.Data.IsMachineGun ? 0.6f : 1f;
            Vector3 local = Quaternion.Inverse(GripWorld.rotation) * velocityWorld;
            _recoilPosVel += Vector3.ClampMagnitude(local * mass, 0.5f);
            _recoilRotVel.x += dip * mass;
        }

        /// <summary>Self-test: the furthest the gun was moved off the hand (metres) since <see cref="ResetKick"/>.</summary>
        internal float PeakShift { get; private set; }

        private Quaternion _steadyRot;
        private bool _steadyInit;
        private float _steadyWeight;

        /// <summary>Self-test: how far (degrees) the steadied aim is behind the hand now.</summary>
        internal float SteadyLag { get; private set; }

        /// <summary>
        /// Aiming through a magnifying sight, the hands' small shake is steadied (Tasks 230): at 4x a tremor of a tenth of a
        /// degree shook the picture by most of a degree. A one-euro style filter on the gun's turn: slow motion (a tremor) is
        /// smoothed, the more the stronger the zoom; a deliberate swing is followed at once; the gun never trails the hand by
        /// more than 5 degrees. Eased in and out with the aim; recoil is added after it, not smoothed.
        /// </summary>
        private Quaternion SteadyThroughScope(Quaternion gunRot, float dt)
        {
            float zoom = Aiming && Weapon != null ? 60f / Mathf.Max(1f, Weapon.GetAimFov()) : 1f;
            float strength = VRConfig.ScopeSteadying.Value;
            float want = strength > 0f && zoom > 1.8f ? strength : 0f;
            if (_weaponClass == WeaponClass.Launcher && strength > 0f)
            {
                // A launcher is steadied whenever it is held, as through a 3x sight: a metre of tube on the shoulder showed
                // every tremor of the hands (user, 2026-10-16). A swing is still followed at once; recoil is not smoothed.
                zoom = Mathf.Max(zoom, 3f);
                want = Mathf.Max(want, strength);
            }
            _steadyWeight = Mathf.MoveTowards(_steadyWeight, want, dt * 5f);
            if (!_steadyInit || _steadyWeight <= 0f)
            {
                _steadyRot = gunRot;
                _steadyInit = true;
                SteadyLag = 0f;
                return gunRot;
            }
            float behind = Quaternion.Angle(_steadyRot, gunRot);
            float speed = behind / Mathf.Max(dt, 1e-4f); // degrees a second the hand is turning away from the steadied aim
            // Cutoff frequency: 1 Hz for a tremor at 4x (lower at higher zoom), up to 8 Hz for a swing.
            float cutoff = Mathf.Lerp(1.6f, 8f, Mathf.InverseLerp(4f, 60f, speed)) / Mathf.Sqrt(Mathf.Clamp(zoom, 1.8f, 12f) / 2f);
            float alpha = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff * dt);
            _steadyRot = Quaternion.Slerp(_steadyRot, gunRot, alpha);
            if (Quaternion.Angle(_steadyRot, gunRot) > 5f)
            {
                _steadyRot = Quaternion.RotateTowards(gunRot, _steadyRot, 5f);
            }
            float w = _steadyWeight * _steadyWeight * (3f - 2f * _steadyWeight);
            Quaternion result = Quaternion.Slerp(gunRot, _steadyRot, w);
            SteadyLag = Quaternion.Angle(result, gunRot);
            return result;
        }

        private void StepRecoil(float dt)
        {
            // Critically damped springs: snappy kick, quick settle. A long gun in one hand comes back down more slowly (and
            // climbs more: the kick is stronger one-handed, see _oneHandRecoil).
            // Softer than before (260 / 150): the kick stays in view long enough to be seen and felt, and comes back in about
            // a quarter second.
            bool oneHandLong = VRConfig.WeaponWeight.Value && !Rig.Data.Handgun && !Supported && !Shouldered;
            float stiffness = oneHandLong ? 90f : 150f, damping = 2f * Mathf.Sqrt(stiffness);
            _recoilPosVel += (-stiffness * _recoilPos - damping * _recoilPosVel) * dt;
            _recoilPos += _recoilPosVel * dt;
            _recoilRotVel += (-stiffness * _recoilRot - damping * _recoilRotVel) * dt;
            _recoilRot += _recoilRotVel * dt;
            _recoilPos = Vector3.ClampMagnitude(_recoilPos, 0.12f);
            _recoilRot = Vector2.ClampMagnitude(_recoilRot, 25f);
            PeakKick = Mathf.Max(PeakKick, Mathf.Abs(_recoilRot.x));
            PeakBack = Mathf.Max(PeakBack, -_recoilPos.z);
            PeakShift = Mathf.Max(PeakShift, _recoilPos.magnitude);
        }

        /// <summary>Self-test: the largest muzzle climb (degrees) and push back (metres) since <see cref="ResetKick"/>.</summary>
        internal float PeakKick { get; private set; }
        internal float PeakBack { get; private set; }
        internal void ResetKick() => PeakKick = PeakBack = PeakShotImpulse = PeakShift = 0f;

        /// <summary>Self-test: the strongest muzzle-climb impulse one shot gave (deg/s) since <see cref="ResetKick"/>, whenever it fired.</summary>
        internal float PeakShotImpulse { get; private set; }

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
                Reload.StepParts();
                ApplyPartOverrides();
                Reload.BeforeRender();
                return;
            }
            _twoHandBlend = Mathf.MoveTowards(_twoHandBlend, TwoHanded && off.Tracked ? 1f : 0f, dt * 10f);

            WeaponRigData d = Rig.Data;
            RigidPose hand = main.GunFrame;
            var gripInGun = new RigidPose(d.GripPoint, d.HasGripRotation ? d.GripRotation : Quaternion.LookRotation(d.AimAxis, d.Up));
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
                    // Eased in and out: taking or letting go of the handguard no longer starts and stops the turn at full speed.
                    float e = _twoHandBlend * _twoHandBlend * (3f - 2f * _twoHandBlend);
                    gunRot = Quaternion.Slerp(gunRot, twoHand, e);
                }
            }
            if (_stockButt.HasValue)
            {
                // Where the player puts the stock, for the shoulder check below (the gun body on the grip at the hand).
                _buttFromHand = hand.position + gunRot * (_stockButt.Value - d.GripPoint);
                _buttFromHandSet = true;
            }

            // Weight: unsupported long guns droop and trail the wrist a little; supported they follow it exactly. The droop
            // comes and goes over a quarter of a second: switched on and off at once, setting the stock in the shoulder or
            // taking the handguard snapped the gun up by the whole droop (up to 11 degrees), and a stock held at the edge
            // of the shoulder pocket made it flick.
            float sagWant = VRConfig.WeaponWeight.Value && !Supported && !Shouldered && _sagDegrees > 0f ? 1f : 0f;
            _sagWeight = _sagWeight < 0f ? sagWant : Mathf.MoveTowards(_sagWeight, sagWant, dt * 4f); // drawn: as it should be at once
            if (_sagWeight > 0f)
            {
                float w = _sagWeight * _sagWeight * (3f - 2f * _sagWeight);
                Vector3 pitchAxis = gunRot * Vector3.Cross(d.Up, d.AimAxis).normalized;
                Quaternion target = Quaternion.AngleAxis(_sagDegrees * w, pitchAxis) * gunRot;
                if (!_carriedInit)
                {
                    _carriedRot = target;
                    _carriedInit = true;
                }
                _carriedRot = Quaternion.Slerp(_carriedRot, target, 1f - Mathf.Exp(-_followRate * dt));
                gunRot = Quaternion.Slerp(target, _carriedRot, w); // the trail fades with the droop
            }
            else
            {
                _carriedRot = gunRot;
                _carriedInit = true;
            }

            StepInertia(main, dt);
            if (main.Model != null && _lagPos.sqrMagnitude > 1e-8f)
            {
                main.Model.FollowOffset = _lagPos; // the hand stays on the grip of the lagging gun
                main.Model.FollowFrame = Time.frameCount;
            }
            if (Mathf.Abs(_lagPitch) > 1e-3f)
            {
                Vector3 pitchAxis = gunRot * Vector3.Cross(d.Up, d.AimAxis).normalized;
                gunRot = Quaternion.AngleAxis(_lagPitch, pitchAxis) * gunRot;
            }

            gunRot = SteadyThroughScope(gunRot, dt);

            // Recoil rotates the gun about its grip and pushes it back along the barrel.
            Quaternion recoil = Quaternion.Euler(_recoilRot.x, _recoilRot.y, 0f);
            Quaternion aimFrame = gunRot * gripInGun.rotation;
            aimFrame = aimFrame * recoil;
            gunRot = aimFrame * Quaternion.Inverse(gripInGun.rotation);
            Vector3 gripWorld = hand.position + _lagPos + aimFrame * _recoilPos;
            var desiredGunBody = new RigidPose(gripWorld - gunRot * d.GripPoint, gunRot);

            // The world holds the gun back (a wall, a crate, the ground, a sandbag it rests on): it is drawn where its physics
            // stand-in is stopped, not inside the obstacle where the hand is; the glove on the weapon's own grip pose stays
            // on it. The stand-in always chases the hand, so the gun comes back to the hand as soon as it is free.
            if (_gunCollider != null)
            {
                _gunCollider.SetHandPose(desiredGunBody);
                RigidPose held = default;
                bool blocked = VRConfig.WorldCollision.Value && _gunCollider.TryBlockedPose(out held);
                if (blocked)
                {
                    _blockedPose = held;
                }
                if (blocked && !_wasBlocked && Time.unscaledTime - _contactKnockAt > 0.25f)
                {
                    // The gun meeting a wall, a crate or the ground is felt, not only seen: a short knock, harder the faster
                    // the hand came in (at most four a second).
                    _contactKnockAt = Time.unscaledTime;
                    float amp = Mathf.Clamp01(0.2f + main.Velocity.magnitude * 0.35f);
                    main.Haptic(0.02f, 90f, amp);
                    if (TwoHanded)
                    {
                        off.Haptic(0.02f, 90f, amp * 0.6f);
                    }
                    ContactKnocks++;
                }
                _wasBlocked = blocked;
                _blockBlend = Mathf.MoveTowards(_blockBlend, blocked ? 1f : 0f, dt * (blocked ? 25f : 10f));
                if (_blockBlend > 0f)
                {
                    float e = _blockBlend * _blockBlend * (3f - 2f * _blockBlend);
                    desiredGunBody = new RigidPose(Vector3.Lerp(desiredGunBody.position, _blockedPose.position, e),
                        Quaternion.Slerp(desiredGunBody.rotation, _blockedPose.rotation, e));
                    gripWorld = desiredGunBody.TransformPoint(d.GripPoint);
                    aimFrame = desiredGunBody.rotation * gripInGun.rotation;
                }
            }
            GripWorld = new RigidPose(gripWorld, aimFrame);

            // Anchor: put the root where it has to be for the (animated) gun body to land on desiredGunBody.
            Transform root = Weapon.transform;
            RigidPose rootNow = RigidPose.Of(root);
            RigidPose gunInRoot = rootNow.Inverse().Multiply(RigidPose.Of(Rig.GunBody));
            desiredGunBody.Multiply(gunInRoot.Inverse()).ApplyTo(root);
            _renderedRoot = RigidPose.Of(root);
            _renderedRootSet = true;

            Reload.StepParts();
            ApplyPartOverrides();
            SnapHands(main, off);
            // The reload first (it sets this frame's keep-out and held palm for the gloves), then the arm: solved before it,
            // the arm went to last frame's glove placement (255.I2-7).
            Reload.BeforeRender();
            if (_armIK != null && _armIK.Active)
            {
                _armIK.Solve();
                // Only the glove whose arm the weapon draws gives way to it (the model's right arm is the gun arm): a model
                // with one arm chain hid both gloves, and the other hand was invisible for the whole hold.
                if (main.Model != null)
                {
                    main.Model.Suppressed = ArmDrawn(_armRightBones);
                }
                if (off.Model != null)
                {
                    off.Model.Suppressed = ArmDrawn(_armLeftBones);
                }
            }
            else
            {
                HideArms();
            }

            Sounds.Update();
            if (_scope != null)
            {
                // With several sight modes the lens shows only in a magnified one: switched to the red dot or the irons, its
                // dark disc stayed over them for as long as the weapon was held.
                bool magnified = Rig.Data.Sights == null || Rig.Data.Sights.Length < 2 || Weapon.GetAimFov() < 40f;
                _scope.SetShown(magnified);
                if (magnified)
                {
                    _scope.BeforeRender(Aiming);
                }
            }
            UpdateScopeLenses();
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
        private int _volumeTries;
        private float _volumeRetryAt;
        private bool _hullApplied;
        private System.Func<Vector3, bool> _supportSolid;
        private HandModel _supportGunHand;

        /// <summary>Self-test: the support hand on a pistol closes on the gun only, as before (to compare).</summary>
        internal static bool LegacySupportSolid;

        /// <summary>Self-test: the held gun's physics stand-in.</summary>
        internal GunCollider Collider => _gunCollider;
        private float _heldSince = -1f;
        private System.Func<Vector3, bool> _solid;

        /// <summary>The gun's solid the hands close on, as posed this frame (grown by a finger's radius); null until measured.</summary>
        internal System.Func<Vector3, bool> HandsSolid => _solid;

        /// <summary>Whether <see cref="HandsSolid"/> includes the seated magazine now (its shape changes with it).</summary>
        internal bool MagSolidShown => _magSolidShown;

        /// <summary>The gun's solid for a hand racking the action: recesses (an ejection port) closed over.</summary>
        private bool ClosedGunSolid(Vector3 p) => _volume.SolidClosed(p) || _magSolidShown && _magVolume.Solid(p);

        /// <summary>Self-test: the point is between the gun's two sides as seen from the side (inside a grip, not beside it).</summary>
        internal bool InsideGunFromSide(Vector3 world) => _volume != null && _volume.Ready && _volume.InsideSideView(world);

        /// <summary>The point is inside the drawn gun (its measured shape, not grown): what hides a muzzle flash from an eye.</summary>
        internal bool InsideDrawnGun(Vector3 world) => _volume != null && _volume.Ready && _volume.InsideModel(world);

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
            if (_volume != null && _volume.Failed)
            {
                // Its read-back or measuring failed: measured again a second later (GunVolume keeps no failed entry). Latched
                // before, the hands never got a solid for the rest of the hold.
                _volume = null;
                _volumeRetryAt = Time.unscaledTime + 1f;
            }
            // An infinite-magazine gun (ammo -1) is analysed as NoAmmo but is a gun all the same: it gets a hand solid too.
            // A kit keeps its hidden glove, and a tool with no ammo stays unmeasured as before.
            bool measurable = !_isKit && (Rig.Data.Action != ActionType.NoAmmo || Weapon.configuration != null && Weapon.configuration.ammo == -1);
            if (_volume == null && _volumeTries < 3 && Time.unscaledTime >= _volumeRetryAt && held > 0.25f && (atRest || held > 5f) && measurable)
            {
                _volumeTries++;
                _volume = GunVolume.For(Rig, frame, Rig.Data.GripPoint);
                if (_volume == null)
                {
                    _volumeRetryAt = Time.unscaledTime + 1f; // not built (an exception, a degenerate box): tried again, three times at most
                }
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
            if (!_rackSnapped)
            {
                _rackSnapped = true;
                SnapRackPoint();
            }
            if (!_knobChecked && SlideOffset < 0.002f && !Weapon.reloading)
            {
                _knobChecked = true; // with the slide home: measured with it locked back, it read as seen (rule review)
                KnobOnHiddenSlide();
            }
            UpdateSeatedMagSolid();
            FitMagazineLine();
            return _solid ?? (_solid = p => _volume.Solid(p) || _magSolidShown && _magVolume.Solid(p));
        }

        /// <summary>Analyses whose magazine line has been fitted to the measured shape (once each; a new analysis again).</summary>
        private static readonly HashSet<WeaponRigData> MagLineFitted = new HashSet<WeaponRigData>();

        /// <summary>
        /// The line a magazine slides in on, fitted to the gun as it is drawn: from the analysed line, tilted up to 24 degrees
        /// either way in the gun's side plane, the one along which the least of the magazine (its own drawn shape, a third
        /// to a quarter of the way out down to the whole rail) passes through the gun outside its well. The analysed line
        /// came from the reload animation's pull or the magazine's box and was off on whole families: magazines went into
        /// pistol grips, the HK416-D's and CZ Bren 2's through the trigger guard (user, 2026-10-16: "could be for all
        /// weapons"). Kept only when clearly better (at most 60% of the points through the gun, 3 fewer at least).
        /// </summary>
        private void FitMagazineLine()
        {
            WeaponRigData d = Rig.Data;
            if (MagLineFitted.Contains(d) || _volume == null || !_volume.Ready || _magVolume == null || !_magVolume.Ready
                || !_magSolidShown || Ammo == null || !Ammo.UsesDetachableMag || !Ammo.MagInserted || d.Action == ActionType.SingleShot
                || d.IsMachineGun || Ammo.HasCover // a machine gun's box hooks on beside the gun, it does not slide up a well
                || d.MagBounds.size.sqrMagnitude < 1e-6f || d.MagEjectAxis.sqrMagnitude < 0.5f || SlideOffset > 0.002f || Weapon.reloading
                || !ReferenceEquals(ActiveRig, Rig))
            {
                return;
            }
            MagLineFitted.Add(d);
            RigidPose seat = Rig.GunBodyPose.Multiply(d.MagSeat);
            Bounds mb = d.MagBounds;
            var points = new List<Vector3>();
            const int n = 5; // (6 cubed, 17 tilts and 3 depths were some 11 000 volume tests in one frame; 255.P1.5)
            for (int i = 0; i < n; i++)
            {
                for (int j = 0; j < n; j++)
                {
                    for (int k = 0; k < n; k++)
                    {
                        Vector3 q = mb.min + Vector3.Scale(mb.size, new Vector3((i + 0.5f) / n, (j + 0.5f) / n, (k + 0.5f) / n));
                        if (_magVolume.InsideModel(seat.TransformPoint(q)))
                        {
                            points.Add(q);
                        }
                    }
                }
            }
            if (points.Count < 10)
            {
                Log.Info($"{d.Key}: magazine line not fitted (only {points.Count} points of the magazine's drawn shape).");
                return;
            }
            Bounds well = mb;
            well.Expand(MagazineProp.WellPlay);
            Quaternion seatInv = Quaternion.Inverse(d.MagSeat.rotation);
            Vector3 right = Vector3.Cross(d.Up, d.AimAxis).normalized;
            Vector3 in0 = -d.MagEjectAxis.normalized;
            int Through(Vector3 inGun)
            {
                Vector3 inLocal = (seatInv * inGun).normalized;
                float half = Mathf.Abs(Vector3.Dot(mb.extents, new Vector3(Mathf.Abs(inLocal.x), Mathf.Abs(inLocal.y), Mathf.Abs(inLocal.z))));
                float rail = Mathf.Clamp(half * 0.9f, 0.03f, 0.09f); // as MagazineProp's rail
                int hits = 0;
                for (int s = 1; s <= 3; s++)
                {
                    float back = rail * (0.25f + 0.375f * (s - 1)); // a quarter, five eighths and the whole rail out
                    foreach (Vector3 q in points)
                    {
                        Vector3 p = q - inLocal * back;
                        if (!well.Contains(p) && _volume.InsideModel(seat.TransformPoint(p)))
                        {
                            hits++;
                        }
                    }
                }
                return hits;
            }
            int before = Through(in0), best = before;
            float bestAngle = 0f;
            // Coarse to fine: every 6 degrees out to 24, then 3 degrees either side of the best.
            for (int step = 1; step <= 4; step++)
            {
                for (int sign = -1; sign <= 1; sign += 2)
                {
                    float angle = sign * step * 6f;
                    int hits = Through(Quaternion.AngleAxis(angle, right) * in0);
                    if (hits < best)
                    {
                        best = hits;
                        bestAngle = angle;
                    }
                }
            }
            float coarse = bestAngle;
            for (int sign = -1; sign <= 1; sign += 2)
            {
                float angle = coarse + sign * 3f;
                if (Mathf.Abs(angle) > 24.01f || Mathf.Abs(angle) < 0.01f)
                {
                    continue;
                }
                int hits = Through(Quaternion.AngleAxis(angle, right) * in0);
                if (hits < best)
                {
                    best = hits;
                    bestAngle = angle;
                }
            }
            int total = points.Count * 3;
            // (Also a small tilt that clears it completely: 2 points through the trigger guard and none at 3 degrees was kept on
            // the six CZ Bren 2 BRs and the Q1911, whose magazines the user saw go in through the gun; 2026-10-17.)
            if (bestAngle != 0f && best <= before * 0.6f && (before - best >= 3 || best == 0 && before >= 2 && Mathf.Abs(bestAngle) <= 6.01f))
            {
                d.MagEjectAxis = -(Quaternion.AngleAxis(bestAngle, right) * in0).normalized;
                d.Notes += $"magazine line fitted to the drawn gun ({bestAngle:0} deg) ";
                Log.Info($"{d.Key}: magazine line fitted to the drawn gun, turned {bestAngle:0} deg in its side plane: {before}/{total} magazine points went through the gun on the way in, now {best}.");
            }
            else
            {
                Log.Info($"{d.Key}: magazine line kept ({before}/{total} magazine points through the gun on the way in; the best tilt, {bestAngle:0} deg, {best}).");
            }
        }

        /// <summary>
        /// An estimated rack point (from the reload hand, or virtual) can lie inside the receiver or float beside it: it is
        /// moved straight out from the bore onto the drawn model's surface, where a hand can take it and a handle can be cut
        /// out or added. A handle cut at the old point is cut again at the new one. A stroke of the reload hand that starts
        /// well off the model held no handle (an AS Val's left hand passed 10 cm over the front sight): the estimate above
        /// the grip is used instead.
        /// </summary>
        /// <summary>Per weapon type: how much of its slide is seen from outside at rest (-1: not measurable).</summary>
        private static readonly Dictionary<string, float> SlideSeen = new Dictionary<string, float>();
        private bool _knobChecked;

        /// <summary>
        /// A long gun whose slide (found by its fire motion) moves hidden in the receiver, with nothing else of it moving
        /// with the hand, gets a knob where the hand takes it, as a bolt baked into the receiver does: GREASER's bolt was 0%
        /// seen and PPSh-41's 3% (check-1017f), and racked, nothing visible moved. Measured once per weapon type, from four
        /// sides with and without the slide (a few small pictures).
        /// </summary>
        private void KnobOnHiddenSlide()
        {
            WeaponRigData d = Rig.Data;
            if (Rig.Slide == null || _boltPiece != null || d.HandlePath != null || d.CarveHandle || d.AddedHandle
                || Rig.SlideCompanions.Length > 0 || d.SlideMethod == null || !d.SlideMethod.StartsWith("fire motion", System.StringComparison.Ordinal))
            {
                return;
            }
            if (!SlideSeen.TryGetValue(d.Key, out float seen))
            {
                var mine = new List<Renderer>();
                foreach (Renderer r in Rig.Slide.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                    {
                        mine.Add(r);
                    }
                }
                if (mine.Count == 0)
                {
                    return; // a bone of the gun's skinned mesh: not measured this way
                }
                var arms = new HashSet<Renderer>(Rig.ArmRenderers);
                var gun = new List<Renderer>();
                foreach (Renderer r in Weapon.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.enabled && !arms.Contains(r) && (r is MeshRenderer || r is SkinnedMeshRenderer))
                    {
                        gun.Add(r);
                    }
                }
                seen = GunVolume.VisibleShare(gun, mine, Rig.AimWorld, Rig.DirToWorld(d.Up), out string views, 0.004f);
                SlideSeen[d.Key] = seen;
                Log.Info($"{d.Key}: its slide '{Rig.Slide.name}' is {(seen < 0f ? "not measurable" : $"{seen * 100f:0}% seen from outside ({views})")}.");
            }
            if (seen < 0f || seen >= 0.05f)
            {
                return;
            }
            try
            {
                _boltPiece = BoltPiece.CreateKnob(Rig, RackPointGun);
                Log.InfoOnce("hiddenknob:" + d.Key, $"{d.Key}: its slide moves hidden in the receiver: a knob added where the hand takes it.");
            }
            catch (System.Exception e)
            {
                _boltPiece = null;
                Log.WarnOnce("hiddenknob:" + d.Key, $"Could not add a knob for {d.Key}: {e.Message}");
            }
        }

        private void SnapRackPoint()
        {
            WeaponRigData d = Rig.Data;
            if (!d.RackOnSurface || _volume == null || !_volume.Ready)
            {
                return;
            }
            Vector3 p = d.RackPoint;
            float off = ToSurface(p, out Vector3 snapped);
            string how = off > 0f ? "onto the receiver" : "out of the receiver";
            if (d.RackFromHand && off > 0.05f)
            {
                p = WeaponAnalyzer.RackEstimate(d);
                ToSurface(p, out snapped);
                how = (float.IsPositiveInfinity(off) ? "the reload hand's stroke started where no model lies toward the bore"
                          : $"the reload hand's stroke started {off * 100f:0.0} cm off the model") + ", no handle there: the estimate above the grip instead";
            }
            float moved = Vector3.Distance(snapped, d.RackPoint);
            if (moved < 0.003f)
            {
                return;
            }
            RackPointGun = snapped;
            Log.InfoOnce("racksnap:" + d.Key, $"{d.Key}: rack point put on the model's surface ({moved * 100f:0.0} cm, {how}).");
            if (_boltPiece != null)
            {
                _boltPiece.Destroy();
                try
                {
                    _boltPiece = BoltPiece.Create(Rig, RackPointGun);
                }
                catch (System.Exception e)
                {
                    _boltPiece = null;
                    Log.WarnOnce("boltpiece2:" + d.Key, $"Could not build a moving handle for {d.Key}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// <paramref name="p"/> (gun space) moved straight out from the bore onto the model's surface. Returns how far
        /// outside the model it was (0 when it was inside and is moved out; infinity when no model lies between it and the bore).
        /// </summary>
        private float ToSurface(Vector3 p, out Vector3 snapped)
        {
            WeaponRigData d = Rig.Data;
            Vector3 bore = d.MuzzlePoint + d.AimAxis * Vector3.Dot(p - d.MuzzlePoint, d.AimAxis);
            Vector3 outward = p - bore;
            outward = outward.sqrMagnitude > 1e-6f ? outward.normalized : d.Up;
            bool Inside(Vector3 gun) => _volume.InsideModel(Rig.ToWorld(gun));
            snapped = p;
            if (Inside(p))
            {
                for (float s = 0.002f; s <= 0.08f; s += 0.002f)
                {
                    if (!Inside(p + outward * s))
                    {
                        snapped = p + outward * s;
                        break;
                    }
                }
                return 0f;
            }
            // In toward the bore until the model: a reload hand taken over the top put the point 8 to 15 cm above the
            // receiver (an MK46's), past a search of 6 cm, and the racking hand hung in the air there.
            float reach = Mathf.Min(0.2f, (p - bore).magnitude);
            for (float s = 0.002f; s <= reach; s += 0.002f)
            {
                if (Inside(p - outward * s))
                {
                    snapped = p - outward * (s - 0.002f);
                    return s - 0.002f;
                }
            }
            return float.PositiveInfinity; // nothing toward the bore: off the model however near it is
        }

        /// <summary>
        /// Self-test: where everything drawn on the weapon is, in gun space: the middle of each mesh drawn (not the
        /// weapon's own arms) and each bone a drawn skinned mesh hangs on. Two of these, before and after a pull, show
        /// what visibly moved.
        /// </summary>
        internal Dictionary<Object, Vector3> DrawnLayout()
        {
            var at = new Dictionary<Object, Vector3>();
            if (Rig.GunBody == null || Weapon == null)
            {
                return at;
            }
            var arms = new HashSet<Renderer>(Rig.ArmRenderers);
            foreach (Renderer r in Weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || arms.Contains(r) || r.transform.lossyScale.sqrMagnitude < 1e-6f)
                {
                    continue;
                }
                if (r is MeshRenderer)
                {
                    at[r] = Rig.ToGun(r.bounds.center);
                }
                else if (r is SkinnedMeshRenderer smr && smr.bones != null)
                {
                    foreach (Transform b in smr.bones)
                    {
                        if (b != null && !at.ContainsKey(b))
                        {
                            at[b] = Rig.ToGun(b.position);
                        }
                    }
                }
            }
            return at;
        }

        /// <summary>
        /// Self-test (Task 232): with the magazine out, the gun's skinned meshes as drawn now, and their vertices that lie in
        /// the seated magazine's box (grown 1 cm): how many, by the bone that carries most of each. The leftover magazine
        /// the ARS MP9 / HK416 still draw is among them. A bone the mod hid (scaled to nothing) folds its vertices onto its
        /// pivot: counted apart. Empty when nothing is drawn there.
        /// </summary>
        internal string SkinnedInMagazineSpace()
        {
            if (Rig.GunBody == null || Weapon == null)
            {
                return "";
            }
            RigidPose seat = Rig.GunBodyPose.Multiply(Rig.Data.MagSeat);
            Bounds box = Rig.Data.MagBounds;
            box.Expand(0.02f);
            var arms = new HashSet<Renderer>(Rig.ArmRenderers);
            var shown = new Dictionary<string, int>();
            int folded = 0;
            foreach (SkinnedMeshRenderer smr in Weapon.GetComponentsInChildren<SkinnedMeshRenderer>(false))
            {
                if (!smr.enabled || arms.Contains(smr) || smr.sharedMesh == null || !smr.sharedMesh.isReadable)
                {
                    continue;
                }
                var baked = new Mesh();
                try
                {
                    smr.BakeMesh(baked); // with the renderer's scale: only its position and rotation are left to apply
                    Vector3[] v = baked.vertices;
                    BoneWeight[] w = smr.sharedMesh.boneWeights;
                    Transform[] bones = smr.bones;
                    Vector3 at = smr.transform.position;
                    Quaternion rot = smr.transform.rotation;
                    for (int i = 0; i < v.Length; i++)
                    {
                        if (!box.Contains(seat.InverseTransformPoint(at + rot * v[i])))
                        {
                            continue;
                        }
                        Transform bone = i < w.Length && w[i].boneIndex0 >= 0 && w[i].boneIndex0 < bones.Length ? bones[w[i].boneIndex0] : null;
                        if (bone != null && bone.lossyScale.sqrMagnitude < 1e-6f)
                        {
                            folded++;
                            continue;
                        }
                        string key = $"'{smr.name}' bone '{(bone != null ? bone.name : "?")}'";
                        shown[key] = shown.TryGetValue(key, out int c) ? c + 1 : 1;
                    }
                }
                finally
                {
                    Object.Destroy(baked);
                }
            }
            if (shown.Count == 0)
            {
                return folded > 0 ? $"nothing drawn ({folded} vertices of hidden bones folded there)" : "";
            }
            var list = new List<KeyValuePair<string, int>>(shown);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new List<string>();
            for (int i = 0; i < list.Count && i < 4; i++)
            {
                parts.Add($"{list[i].Value} vertices of {list[i].Key}");
            }
            return string.Join(", ", parts.ToArray()) + (folded > 0 ? $" ({folded} of hidden bones folded there)" : "");
        }

        /// <summary>Self-test: what the hand sees move when it works the action.</summary>
        internal string VisibleHandle =>
            Rig.Handle != null ? "the charging handle"
            : _boltPiece != null && _boltPiece.CarrierOnly && Rig.Slide != null ? "the gun's own carrier with its handle (nothing cut out, no knob added)"
            : _boltPiece != null && _boltPiece.ShownPieces == 0 ? (Rig.Slide != null ? "the carrier inside only (THE HANDLE IS NOT SHOWN)" : "NOTHING VISIBLE (the handle's pieces are gone)")
            : _boltPiece != null && _boltPiece.Carved ? "a piece cut out of the receiver" + (Rig.Slide != null ? " (and the carrier inside)" : "")
            : _boltPiece != null ? "an added handle" + (Rig.Slide != null ? " (and the carrier inside)" : "")
            : Rig.Slide != null ? "the slide / bolt"
            : "NOTHING VISIBLE";

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

        /// <summary>Self-test: the weapon's own arms (arm-IK mode), or null.</summary>
        internal ArmIK Arms => _armIK;

        /// <summary>The weapon's own arms are shown as the hands (the arm-IK mode), not hidden.</summary>
        internal bool ArmsShownByMod => _armIK != null && _armIK.Active;

        /// <summary>Self-test: the measured solid (null until measured).</summary>
        internal GunVolume Volume => _volume;

        private GunVolume _partVolume;
        private bool _partTried;
        private System.Func<Vector3, bool> _partSolid;

        /// <summary>The part a racking hand holds: a separate charging handle, else the slide, bolt or pump.</summary>
        private Transform WorkedPart => Rig.Data.HandlePath != null && Rig.Handle != null ? Rig.Handle : Rig.Slide;

        /// <summary>
        /// The hand working the action (racking a slide, pulling a bolt or charging handle, pumping) closes on the part it
        /// holds, measured on its own the first time it is taken and moving with it.
        /// </summary>
        private System.Func<Vector3, bool> PartSolid()
        {
            Transform part = WorkedPart;
            if (part == null)
            {
                return null;
            }
            if (_partVolume == null && !_partTried)
            {
                _partTried = true;
                var renderers = new List<Renderer>();
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(false))
                {
                    if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer) && !GunCollider.AddedByMod(r.transform, Rig.Weapon.transform))
                    {
                        renderers.Add(r);
                    }
                }
                _partVolume = GunVolume.Measure("part:" + Rig.Data.Key + ":" + part.name, renderers, part, 3);
            }
            if (_partVolume == null || !_partVolume.Ready)
            {
                return null;
            }
            _partVolume.SetPose(part.position, part.rotation);
            return _partSolid ?? (_partSolid = _partVolume.Solid);
        }

        private System.Func<Vector3, bool> _rackSolid, _drawnHandleSolid;
        private Vector3 _drawnHandleAt, _drawnHandleAcross;

        /// <summary>On the drawn handle: a bar 4 cm across the gun, 9 mm round (grown by a finger's radius like the solids).</summary>
        private bool OnDrawnHandle(Vector3 p)
        {
            Vector3 v = p - _drawnHandleAt;
            float t = Mathf.Clamp(Vector3.Dot(v, _drawnHandleAcross), -0.02f, 0.02f);
            float r = 0.009f + GunVolume.Grow;
            return (v - _drawnHandleAcross * t).sqrMagnitude < r * r;
        }
        private Vector3 _partRestPos;
        private Quaternion _partRestInv;
        private HandModel _rackGunHand;

        /// <summary>
        /// What a hand working the action closes on: the part where it is now, the rest of the gun round it (the receiver
        /// under a bolt knob, the stock behind a charging handle, the frame under a slide: the gun's solid with the part's
        /// rest place taken out, since the part has moved), and the gun hand's fingers. On the part alone the fingers went
        /// on into the receiver: 16 points of a hand over an AK's top, 15 of one on an AR's charging handle from above
        /// (tools/hands/grip_check.py). The part alone when the gun is not measured.
        /// </summary>
        private System.Func<Vector3, bool> RackSolid(VRHand main)
        {
            System.Func<Vector3, bool> part = PartSolid();
            System.Func<Vector3, bool> gun = HandSolid();
            if (part == null && gun != null && _boltPiece != null)
            {
                // A handle the mod draws (cut out of the receiver, or a knob added): no part of the model to measure, so
                // the hand closed on nothing and curled through it. A bar across the gun where the handle is on its stroke.
                WeaponRigData dh = Rig.Data;
                Vector3 dir = dh.SlideTravel.sqrMagnitude > 1e-6f ? dh.SlideTravel.normalized : -dh.AimAxis;
                _drawnHandleAt = Rig.ToWorld(RackPointGun + dir * SlideOffset);
                _drawnHandleAcross = Rig.DirToWorld(Vector3.Cross(dh.Up, dh.AimAxis).normalized);
                _rackGunHand = main.Model != null && main.Role == HandRole.Weapon ? main.Model : null;
                return _drawnHandleSolid ?? (_drawnHandleSolid = p => OnDrawnHandle(p) || ClosedGunSolid(p)
                                                                     || _rackGunHand != null && _rackGunHand.NearSkin(p, GunVolume.Grow));
            }
            if (part == null || gun == null)
            {
                return part;
            }
            WeaponRigData d = Rig.Data;
            Transform worked = WorkedPart;
            Vector3 travel = Vector3.zero;
            if (SlideOffset > 0.0005f)
            {
                float fraction = SlideOffset / Mathf.Max(d.SlideTravel.magnitude, 1e-4f);
                travel = worked == Rig.Handle ? d.HandleTravel * Mathf.Clamp01(fraction) : d.SlideTravel * fraction;
            }
            _partRestPos = worked.position - Rig.DirToWorld(travel);
            _partRestInv = Quaternion.Inverse(worked.rotation);
            _rackGunHand = main.Model != null && main.Role == HandRole.Weapon ? main.Model : null;
            return _rackSolid ?? (_rackSolid = p => _partSolid(p)
                                                     || ClosedGunSolid(p) && !_partVolume.SolidAt(p, _partRestPos, _partRestInv)
                                                     || _rackGunHand != null && _rackGunHand.NearSkin(p, GunVolume.Grow));
        }

        /// <summary>Self-test: the worked part's shape has been measured (or there is none to measure).</summary>
        internal bool PartShapeReady => WorkedPart == null || _partTried && (_partVolume == null || _partVolume.Ready);

        /// <summary>Self-test: how much of a hand working the action is buried in the part it holds.</summary>
        internal string PartClipping(VRHand hand, out int buried, out int touching)
        {
            buried = touching = -1;
            if (_partTried && _partVolume == null)
            {
                return "not measured (the part draws no mesh of its own: a bone of the gun's skinned mesh, or built by the mod)";
            }
            if (_partVolume == null || !_partVolume.Ready || hand.Model == null)
            {
                return "not measured (the part's shape is not ready)";
            }
            hand.Model.Measure(_partVolume.InsideModel, _partVolume.Solid, out buried, out int samples, out touching);
            return $"{buried}/{samples} points inside the part, {touching}/4 fingers on it";
        }

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
        /// <summary>Throwables already put in the fist by their middle (once per analysis).</summary>
        private static readonly HashSet<WeaponRigData> GrenadeInFist = new HashSet<WeaponRigData>();

        /// <summary>Self-test: this weapon is carried like a bag (no glove on it).</summary>
        internal bool IsKitForTest => _isKit;

        private static bool IsKit(Weapon weapon)
        {
            GameObject projectile = weapon.configuration != null ? weapon.configuration.projectilePrefab : null;
            if (projectile != null && (projectile.GetComponentInChildren<Ammobox>(true) != null || projectile.GetComponentInChildren<Medipack>(true) != null
                                       || projectile.GetComponentInChildren<ResupplyCrate>(true) != null))
            {
                return true;
            }
            // The name only for something that fires no bullets: a gun named 'Medic SMG' or 'AK kit' lost its glove (hidden
            // and frozen for the whole hold).
            if (projectile != null && projectile.GetComponentInChildren<Projectile>(true) != null && weapon.configuration.ammo > 1)
            {
                return false;
            }
            if (weapon is MeleeWeapon)
            {
                return false; // a blade is held in the glove: a 'Medic Knife' (no projectile) matched the name below
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
                    main.Model.SolidAnchor = Rig.GunBody;
                    // Solved again as a racked slide moves (a finger or thumb that lay on it stayed where it was).
                    main.Model.SolidVersion = (_magSolidShown ? 1 : 0) + 2 * Mathf.RoundToInt(SlideOffset * 500f);
                }
                else if (main.Model.Solid == _solid)
                {
                    main.Model.Solid = null; // only what this weapon set: a magazine in that hand keeps its own
                }
            }
            if (off.Model != null)
            {
                System.Func<Vector3, bool> part = off.Role == HandRole.Slide ? RackSolid(main) : null;
                if (part != null)
                {
                    off.Model.Solid = part;
                    off.Model.SolidAnchor = WorkedPart;
                    // The gun round the part moves against it as it slides: solved again every 2 mm of travel.
                    off.Model.SolidVersion = Mathf.RoundToInt(SlideOffset * 500f);
                }
                else if (off.Model.Solid != null && (off.Model.Solid == _partSolid || off.Model.Solid == _rackSolid))
                {
                    off.Model.Solid = null;
                }
                if (off.Role == HandRole.Foregrip && solid != null && Rig.Data.IsPistol && main.Model != null)
                {
                    // Cupping a pistol: the support hand closes on the gun and on the gun hand's fingers round its grip.
                    // The gun hand's model is read through a field so the solid lambda captures only this, not a fresh
                    // closure object every frame this branch runs.
                    _supportGunHand = main.Model;
                    off.Model.Solid = _supportSolid ?? (_supportSolid = p => _solid(p) || !LegacySupportSolid && _supportGunHand.NearSkin(p, GunVolume.Grow));
                    off.Model.SolidAnchor = Rig.GunBody;
                    // Solved again when the gun hand's skin moves against the gun (or the seated magazine comes or goes), not
                    // every frame: all five fingers were solved again each frame with nothing moved (about 0.2 ms; Tasks 248).
                    off.Model.SolidVersion = unchecked(main.Model.SkinKey(Rig.GunBody) * 2 + (_magSolidShown ? 1 : 0));
                }
                else if (off.Role == HandRole.Foregrip)
                {
                    off.Model.Solid = solid;
                    off.Model.SolidAnchor = Rig.GunBody;
                    off.Model.SolidVersion = _magSolidShown ? 1 : 0;
                }
                else if (off.Model.Solid == _solid || off.Model.Solid != null && off.Model.Solid == _supportSolid)
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
                ActiveRig.ToWorld((ReferenceEquals(ActiveRig, Rig) ? RackPointGun : a.RackPoint) + a.SlideTravel.normalized * SlideOffset),
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
            // A charging handle apart from the bolt (a separate part, or one cut out of an AR's receiver) comes back only with
            // the hand: when the bolt locks open on an empty magazine, the handle stays forward (it showed pulled back).
            float handle = Reload != null && !Reload.IsRacking && (Rig.Handle != null || (d.CarveHandle || d.AddedHandle) && d.SlidePath != null) ? 0f : fraction;
            // Locked open with the bolt under the racked handle: the handle home (or where the hand has it), the bolt back.
            bool boltAlone = BoltLockedAlone;
            float slideShare = boltAlone && !Reload.IsRacking ? 0f : fraction;
            if (SlideOffset > 0.0005f)
            {
                MovePart(Rig.Slide, d.SlideRestLocal, d.SlideTravel * slideShare);
                MoveCompanions(d.SlideTravel * slideShare);
                MovePart(Rig.Handle, d.HandleRestLocal, d.HandleTravel * Mathf.Clamp01(handle));
                _slideMoved = true;
            }
            else
            {
                if (_slideMoved)
                {
                    // Home once when the offset drops to nothing in one step (a bolt let go near the front, a sub weapon
                    // switched mid-slam): the slide kept its last drawn place, up to 1.5 cm back. Only once: after that the
                    // gun's own animation moves it again (its shots).
                    MovePart(Rig.Slide, d.SlideRestLocal, Vector3.zero);
                    MoveCompanions(Vector3.zero);
                    _slideMoved = false;
                }
                if (Rig.Handle != null)
                {
                    MovePart(Rig.Handle, d.HandleRestLocal, Vector3.zero);
                }
            }
            // (Its own statement: hung as an "else if" between the two above, the slide was sent home every frame it was
            // moved, and no slide, bolt or handle could be pulled back by hand: Tasks 254.)
            if (boltAlone)
            {
                // Its place under the handle: back by what the handle is short of its full travel.
                MovePart(Rig.Bolt, d.BoltRestLocal, d.SlideTravel * (1f - Mathf.Clamp01(slideShare)));
                _boltMoved = true;
            }
            else if (_boltMoved)
            {
                MovePart(Rig.Bolt, d.BoltRestLocal, Vector3.zero); // rides on the handle again
                _boltMoved = false;
            }
            _boltPiece?.SetOffset(Rig, d.SlideTravel * ((d.CarveHandle || d.AddedHandle) && d.SlidePath != null ? handle : fraction));
            ApplyHinge(d);
            ApplySightParts();

            AmmoState ammo = ActiveAmmo ?? Ammo;
            ApplyBeltParts(ammo);
            HideSpareParts();
            if (Rig.MagazineParts.Length > 0 && ammo != null && ammo.UsesDetachableMag)
            {
                // While the magazine is out a prop represents it (sliding in or out, in the hand, on the ground).
                // Scaling hides mesh-renderer and skinned magazines alike.
                bool hide = !ammo.MagInserted;
                if (hide)
                {
                    Vector3 tiny = Vector3.one * 1e-4f;
                    foreach (Transform part in Rig.MagazineParts)
                    {
                        if (part != null && part.localScale != tiny) // a mod may swap or destroy magazine pieces at runtime
                        {
                            part.localScale = tiny;
                        }
                    }
                    _magHidden = true;
                }
                else if (_magHidden)
                {
                    RestoreMagazine();
                }
            }
            if (Rig.Magazine != null && ammo != null && ammo.Participates && ammo.UsesDetachableMag && ammo.MagInserted && !d.SeatOnHinge
                && VRConfig.ReloadNow == ReloadMode.Manual && ReferenceEquals(ActiveRig, Rig) && !(Weapon != null && Weapon.reloading))
            {
                // The seated magazine sits on its seat on the gun body, where a fresh one slides in. On many models it hangs
                // off the armature, not the gun body, and the flat animation's poses (loaded, empty, slide locked) moved it a
                // few centimetres against the gun: a fresh magazine went in beside the well (an M1911's 4 cm, a P220's 4.4).
                Rig.GunBodyPose.Multiply(d.MagSeat).ApplyTo(Rig.Magazine);
            }
            bool rocketLoaded = Rig.Magazine != null && ammo != null && d.Action == ActionType.SingleShot && !ammo.UsesDetachableMag && ammo.MagRounds > 0
                                && VRConfig.ReloadNow == ReloadMode.Manual && ReferenceEquals(ActiveRig, Rig) && !(Weapon != null && Weapon.reloading)
                                && (d.MagSeat.position - d.GripPoint).sqrMagnitude < 1.5f * 1.5f;
            if (rocketLoaded)
            {
                // A launcher loaded by hand shows its rocket on its seat: the game parks a fired one away (the Panzerfaust 3's
                // warhead 30 m off) and only its own reload animation, which a hand-loaded launcher never plays, brings it
                // back; the launcher looked empty once loaded.
                Rig.GunBodyPose.Multiply(d.MagSeat).ApplyTo(Rig.Magazine);
            }
            ShowLoadedRocket(rocketLoaded);
        }

        private readonly HashSet<Renderer> _rocketRenderersOn = new HashSet<Renderer>();
        private readonly HashSet<GameObject> _rocketObjectsOn = new HashSet<GameObject>();
        private readonly Dictionary<Transform, Vector3> _rocketScalesRestored = new Dictionary<Transform, Vector3>();

        /// <summary>
        /// While a launcher is loaded, its rocket as it shows loaded is drawn: parts the game shrank to nothing get their scale
        /// back, renderers and objects it switched off after the shot are switched on (the Panzerfaust 3's warhead stayed
        /// hidden on a launcher loaded by hand: placing it on its seat was not enough). What this switched on is switched
        /// off again once it is fired, so the game's own hiding is left as it was (it hides nothing a second time).
        /// </summary>
        private void ShowLoadedRocket(bool loaded)
        {
            if (loaded)
            {
                for (int i = 0; i < Rig.MagazineParts.Length && i < Rig.MagazinePartScales.Length; i++)
                {
                    Transform p = Rig.MagazineParts[i];
                    if (p == null || Rig.MagazineHideOnly.Contains(p) || p.localScale.sqrMagnitude > 1e-6f || Rig.MagazinePartScales[i].sqrMagnitude < 1e-6f)
                    {
                        continue;
                    }
                    if (!_rocketScalesRestored.ContainsKey(p))
                    {
                        _rocketScalesRestored[p] = p.localScale;
                    }
                    p.localScale = Rig.MagazinePartScales[i];
                }
                foreach (Renderer r in Rig.MagShownLoaded)
                {
                    if (r == null)
                    {
                        continue;
                    }
                    if (!r.gameObject.activeSelf)
                    {
                        r.gameObject.SetActive(true);
                        _rocketObjectsOn.Add(r.gameObject);
                    }
                    if (!r.enabled)
                    {
                        r.enabled = true;
                        _rocketRenderersOn.Add(r);
                    }
                }
                return;
            }
            if (_rocketRenderersOn.Count == 0 && _rocketObjectsOn.Count == 0 && _rocketScalesRestored.Count == 0)
            {
                return;
            }
            foreach (Renderer r in _rocketRenderersOn)
            {
                if (r != null)
                {
                    r.enabled = false;
                }
            }
            foreach (GameObject g in _rocketObjectsOn)
            {
                if (g != null)
                {
                    g.SetActive(false);
                }
            }
            foreach (KeyValuePair<Transform, Vector3> kv in _rocketScalesRestored)
            {
                if (kv.Key != null)
                {
                    kv.Key.localScale = kv.Value;
                }
            }
            _rocketRenderersOn.Clear();
            _rocketObjectsOn.Clear();
            _rocketScalesRestored.Clear();
        }

        private bool _slideMoved;
        private bool _boltMoved;

        /// <summary>
        /// The action is locked open and the slide part is a handle carrying the bolt: the bolt alone stays back, the handle
        /// is home (an M4's charging handle on an empty magazine).
        /// </summary>
        internal bool BoltLockedAlone => Rig.Bolt != null && Rig.Slide != null && Reload != null && (ActiveAmmo ?? Ammo) is AmmoState a && a.SlideLocked
                                         && ReferenceEquals(ActiveRig, Rig);

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

        /// <summary>The slide's companions (a knob beside it in the model) moved by the slide's travel, each from its own rest.</summary>
        private void MoveCompanions(Vector3 travelGun)
        {
            Transform[] parts = Rig.SlideCompanions;
            for (int i = 0; i < parts.Length && i < Rig.Data.SlideCompanionRest.Count; i++)
            {
                MovePart(parts[i], Rig.Data.SlideCompanionRest[i], travelGun);
            }
        }

        private void MovePart(Transform part, Vector3 restLocal, Vector3 travelGun)
        {
            if (part == null)
            {
                return;
            }
            Vector3 worldTravel = Rig.DirToWorld(travelGun);
            Transform parent = part.parent;
            Vector3 target = restLocal + (parent != null ? parent.InverseTransformVector(worldTravel) : worldTravel);
            if (part.localPosition != target) // a slide or handle at rest holds the same value every frame
            {
                part.localPosition = target;
            }
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
            // The physics stand-in with the hands: whatever throws below, it must not be left at the gun's last pose, where
            // dropped weapons and magazines would bounce off an invisible gun-shaped box (it is DontDestroyOnLoad).
            _gunCollider?.Destroy();
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
            MoveCompanions(Vector3.zero);
            if (Rig.Handle != null)
            {
                Rig.Handle.localPosition = Rig.Data.HandleRestLocal;
            }
            if (Rig.Bolt != null)
            {
                Rig.Bolt.localPosition = Rig.Data.BoltRestLocal; // a bolt left locked back stayed back (255.B1-2, B7-1)
                _boltMoved = false;
            }
            if (_hingeOverridden && Rig.Hinge != null)
            {
                Rig.Hinge.localRotation = _hingeRestLocal;
                _hingeOverridden = false;
            }
            HingeOpenness = 0f;
            RestoreSightParts();
            foreach (Renderer r in _scopeLenses)
            {
                if (r != null)
                {
                    r.enabled = true;
                }
            }
            _scopeLenses.Clear();
            if (_spareParts != null)
            {
                for (int i = 0; i < _spareParts.Length; i++)
                {
                    if (_spareParts[i] != null)
                    {
                        _spareParts[i].localScale = _spareScales[i];
                    }
                }
            }
            if (_beltHidden && _beltParts != null)
            {
                for (int i = 0; i < _beltParts.Length; i++)
                {
                    if (_beltParts[i] != null)
                    {
                        _beltParts[i].localScale = _beltScales[i];
                    }
                }
                _beltHidden = false;
            }
            foreach (Renderer r in _shadowOnly)
            {
                if (r != null)
                {
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
                }
            }
            _shadowOnly.Clear();
            foreach (ParticleSystemRenderer pr in _hiddenCasings)
            {
                if (pr != null)
                {
                    pr.enabled = true;
                }
            }
            _hiddenCasings.Clear();
            _boltPiece?.Destroy();
            SetHandModelsSuppressed(false);
            if (_system.Rig.Dominant.Model != null)
            {
                _system.Rig.Dominant.Model.WeaponGripCurls = null;
                _system.Rig.Dominant.Model.WeaponIsPistol = false;
                _system.Rig.Dominant.Model.WeaponClass = WeaponClass.None;
                _system.Rig.Dominant.Model.SnapPose = null;
                _system.Rig.Dominant.Model.Solid = null;
                _system.Rig.Dominant.Model.HiddenByHeld = false;
            }
            if (_system.Rig.Off.Model != null)
            {
                _system.Rig.Off.Model.WeaponSupportCurls = null;
                _system.Rig.Off.Model.SnapPose = null;
                _system.Rig.Off.Model.Solid = null;
                _system.Rig.Off.Model.WeaponClass = WeaponClass.None;
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
