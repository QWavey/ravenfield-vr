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
    public sealed partial class HeldWeapon
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
        /// <summary>The flip-in magnifier, switched by the free hand's grip and slide rather than the Magnifier button.</summary>
        private readonly MagnifierFlip _magnifierFlip;

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

        /// <summary>
        /// Which of a break action's chambers hold a shell, live or fired (<see cref="WeaponRigData.ChamberShellPaths"/>): its
        /// shell is drawn there. Kept in step with the ammunition (the live shells and the fired ones not yet out); the reload
        /// says which chamber a shell went into.
        /// </summary>
        public bool[] ChamberFull = new bool[0];
        private bool _chambersApplied;
        private Vector3[] _chamberLocalPos;
        private Quaternion[] _chamberLocalRot;
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
            try
            {
                Sounds = new WeaponSoundPlayer(rig, system.Sounds);
                Reload = new ReloadInteractions(system, this);
                _magnifierFlip = new MagnifierFlip(system, this);
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
                // A throwable is held by its middle before its stand-in is made from the grip: made first, the first hold of each
                // kind had its stand-in about the animation's palm, off the grenade (gameplay workflow, 2026-10-24).
                _isKit = IsKit(weapon);
                if (rig.Data.Action == ActionType.Throwable && !_isKit)
                {
                    CentreGrenadeGrip(rig, weapon);
                }
                _gunCollider = GunCollider.Create(rig);
                // Renderers that draw only a shadow (helper boxes a mod's reload animation uses, shadow proxies of a flat
                // view model) put a dark blob on the ground under the player in VR, where the weapon is drawn in the world.
                // Not drawn at all while held: switched from ShadowsOnly to Off they drew their mesh, which ShadowsOnly
                // keeps invisible (gameplay workflow, 2026-10-24).
                foreach (Renderer r in weapon.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly && !r.forceRenderingOff)
                    {
                        r.forceRenderingOff = true;
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
                _supportHand = ModHand.Find(weapon.transform, rig.Data.ArmLeft);
                ActionType action = rig.Data.Action;
                RackPointGun = rig.Data.RackPoint;
                // Task 299: a made handle (a piece cut out of the receiver, or an added latch) is never put beside the gun's own handle
                // part (HandlePath): the hand takes that part and the mod moves it. The reload analysis set CarveHandle first (its
                // stroke rule runs before the named-handle rule), then named the model's handle, and a piece of receiver was cut out
                // beside the handle that already moved.
                bool ownHandle = rig.Data.HandlePath != null;
                if (rig.Data.SlidePath == null && rig.Data.HandlePath == null && (action == ActionType.SelfLoading || action == ActionType.Manual || action == ActionType.ShellFed)
                    || !ownHandle && (rig.Data.CarveHandle || rig.Data.AddedHandle))
                {
                    // No part of this model moves when the action is worked (or only a carrier inside the receiver does): give
                    // it a handle that does. Not when the model's own handle is the one that moves (its own part is the handle, found
                    // by ArOwnHandleNoSlide): a knob added beside it was the second pull-back handle (user, 2026-10-18, task 269).
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
            catch (System.Exception)
            {
                // What was built before the step that threw (lifecycle hunt, 2026-10-24): the attach's catch has no
                // weapon to Release, so a scope's camera and render texture, the round counter and the moving handle (no
                // parent) stayed, and the gun's shadow-only renderers and casing particles stayed switched off.
                UndoPartialBuild();
                throw;
            }
        }

        /// <summary>Undoes what a constructor that threw had built and changed; each step on its own.</summary>
        private void UndoPartialBuild()
        {
            void Step(string what, System.Action undo)
            {
                try
                {
                    undo();
                }
                catch (System.Exception e)
                {
                    Log.Exception("Undoing a failed weapon setup (" + what + ")", e);
                }
            }
            Step("stand-in", () => _gunCollider?.Destroy());
            Step("scope", () => _scope?.Destroy());
            Step("round counter", () => _counter?.Destroy());
            Step("moving handle", () => _boltPiece?.Destroy());
            Step("sounds", () => Sounds?.Destroy());
            Step("renderers", () =>
            {
                foreach (Renderer r in _shadowOnly)
                {
                    if (r != null)
                    {
                        r.forceRenderingOff = false;
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
            });
        }

        public bool IsPistolGrip => Rig.Data.IsPistol;

        /// <summary>What kind of weapon this is, for the hands' poses.</summary>
        private readonly WeaponClass _weaponClass;

        internal static WeaponClass ClassOf(WeaponRig rig, Weapon weapon)
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
                || Mathf.Abs(d.LengthFromGrip) <= 0.4f)
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
                    // A laser's dot or beam hung under the lens is not part of its picture: it keeps its own visibility. Listed with
                    // the lens, it would be hidden whenever the magnifier is flipped out or not aimed through.
                    if (_scopeLenses.Contains(child) || child is ParticleSystemRenderer || child is LineRenderer || child is TrailRenderer
                        || PoseSampler.IsLaserModule(child.transform, _scopeLenses[i].transform))
                    {
                        continue;
                    }
                    _scopeLenses.Add(child);
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

        /// <summary>Self-test: how many of the flat reload's spare magazines are drawn now (0 while held).</summary>
        internal int SparePartsShown()
        {
            int n = 0;
            foreach (Transform t in Rig.SpareParts)
            {
                n += t != null && t.lossyScale.sqrMagnitude > 1e-6f ? 1 : 0;
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
            if (!VRConfig.WeaponWeight.Value || _inertiaOmega <= 0f)
            {
                _lagPos = _lagVel = Vector3.zero;
                _lagPitch = _lagPitchVel = 0f;
                _lagInit = false;
                return;
            }
            if (!main.Tracked)
            {
                // Eased out, not dropped: zeroed in one frame, a heavy gun still lagging behind the hand jumped onto it the
                // moment tracking was lost (gameplay workflow, 2026-10-24). Its history starts again when tracking is back.
                float ease = Mathf.Exp(-12f * dt);
                _lagPos *= ease;
                _lagPitch *= ease;
                _lagVel = Vector3.zero;
                _lagPitchVel = 0f;
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
            // Here as well as in SnapHands: that runs only while the gun hand is tracked, and a re-grab made meanwhile took the
            // last hold's pose (hold review, 2026-10-20 night).
            if (_system.Rig.Off.Role != HandRole.Foregrip)
            {
                _supportHoldStarted = false;
            }
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
                main.Model.WeaponGripThumbBase = Rig.Data.GripThumbBase;
                main.Model.WeaponGripThumbCurl = Rig.Data.GripThumbCurl;
                main.Model.WeaponIsPistol = Rig.Data.IsPistol;
                main.Model.WeaponClass = _weaponClass;
            }
            // Not while that hand holds the second weapon: it set the pistol's class for its hand, and this wrote the main gun's
            // over it every frame (an HK416's rifle grip on the free hand's P320; second-weapon hunt, 2026-10-20 night).
            if (_system.Rig.Off.Model != null && (_system.Second == null || _system.Second.Hand != _system.Rig.Off))
            {
                _system.Rig.Off.Model.WeaponSupportCurls = Rig.Data.SupportCurls;
                _system.Rig.Off.Model.WeaponClass = _weaponClass;
            }
            Weapon active = Weapon.GetActiveSubWeapon() ?? Weapon;
            if (active != ActiveWeapon)
            {
                Reload.Release();
                _magnifierFlip.Release();
                ActiveWeapon = active;
                ActiveRig = active == Weapon ? Rig : SubWeaponRig(active);
                ActiveAmmo = active == Weapon ? Ammo : _system.StateFor(active, create: true);
                if (active != Weapon && ReferenceEquals(ActiveRig, Rig) && ActiveAmmo != null && !ReferenceEquals(ActiveAmmo, Ammo))
                {
                    ActiveAmmo.MagazineOf = Ammo; // a fire-mode variant on this model: this gun's magazine is its magazine
                }
            }
            UpdateAim();
            _magnifierFlip.Update();
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

        /// <summary>Self-test experiment: -rfvr-noslidepose leaves the animator's "loaded ammo" to the game.</summary>
        private static readonly bool NoSlidePose = System.Array.Exists(System.Environment.GetCommandLineArgs(), a => a.Equals("-rfvr-noslidepose", System.StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Many pistols show the slide locked back from the animator's "loaded ammo" being 0. A fresh magazine in a gun with
        /// its slide locked made the game count the rounds as loaded, and the animator ran the slide forward while the gun
        /// still said RACK (and needed racking). Held at 0 until the slide is released or racked; every frame, because the
        /// release does not always change the round count (and so never told the animator).
        /// </summary>
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

        /// <summary>
        /// Whether the free hand flips this weapon's magnifier (its part was found and it has a Magnifier key). The aim
        /// button then leaves that magnifier to the hand; a weapon without a found part keeps the button.
        /// </summary>
        public bool MagnifierByHand(Weapon weapon) => _magnifierFlip.HandlesWeapon(weapon);

        /// <summary>Autotest: the free hand's magnifier grip on this gun.</summary>
        internal MagnifierFlip MagnifierGrip => _magnifierFlip;

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

        /// <summary>Self-test: how far the nearer eye is from the sight's line (m; MaxValue: no eye within its reach) and the head's angle to it.</summary>
        internal float AimMiss { get; private set; } = float.MaxValue;
        internal float AimAngle { get; private set; }

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
                AimMiss = best;
                AimAngle = angle;
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
            _magnifierFlip.OnShot();
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
            Vector3 local = Runtime.RigidPose.Conjugate(GripWorld.rotation) * velocityWorld;
            _recoilPosVel += Vector3.ClampMagnitude(local * mass, 0.5f);
            _recoilRotVel.x += dip * mass;
        }

        /// <summary>Self-test: the furthest the gun was moved off the hand (metres) since <see cref="ResetKick"/>.</summary>
        internal float PeakShift { get; private set; }

        private Quaternion _steadyRot;
        private bool _steadyInit;
        private float _steadyWeight;
        private Quaternion _steadyPrevRaw;
        private float _steadyHandSpeed;
        private Vector3 _steadyOmega;
        /// <summary>The steadying setting of the current (or last) aim: the scope's through a magnifier, the aim's on iron sights.</summary>
        private float _steadyStrength;

        /// <summary>Self-test: how far (degrees) the steadied aim is behind the hand now.</summary>
        internal float SteadyLag { get; private set; }

        /// <summary>
        /// Aiming through a magnifying sight, or down iron sights or a red dot (AimSteadying, 2026-10-19), the hands' small shake is steadied (Tasks 230): at 4x a tremor of a tenth of a
        /// degree shook the picture by most of a degree. A one-euro style filter on the gun's turn: slow motion (a tremor) is
        /// smoothed, the more the stronger the zoom; a deliberate swing is followed at once; the gun never trails the hand by
        /// more than 5 degrees. Eased in and out with the aim; recoil is added after it, not smoothed.
        /// </summary>
        private Quaternion SteadyThroughScope(Quaternion gunRot, float dt)
        {
            float zoom = Aiming && Weapon != null ? 60f / Mathf.Max(1f, Weapon.GetAimFov()) : 1f;
            // Through a magnifier the scope setting; aimed down iron sights or a red dot (no magnification) the aim setting
            // (2026-10-19, user: "scope stabilisation when aiming because the guns shake very much (when not shooting) because
            // of my hands"; unsteadied, a 1 cm tremor of the off hand turned the barrel about 2.4 deg). The same filter: a
            // shake at 1x is cut above about 1.7 Hz, a swing opens it up to 8 Hz.
            float strength = zoom > 1.8f ? VRConfig.ScopeSteadying.Value : Aiming ? VRConfig.AimSteadying.Value : 0f;
            // The ramp is an on/off level in and out with the aim; the setting scales it at the end (2026-10-19). It used to be
            // the setting itself, eased again by the smoothstep below: 25% then gave about 16% of the filter, not a quarter.
            float want = strength > 0f && Aiming ? 1f : 0f;
            if (_weaponClass == WeaponClass.Launcher && VRConfig.ScopeSteadying.Value > 0f)
            {
                // A launcher is steadied whenever it is held, as through a 3x sight: a metre of tube on the shoulder showed
                // every tremor of the hands (user, 2026-10-16). A swing is still followed at once; recoil is not smoothed.
                zoom = Mathf.Max(zoom, 3f);
                strength = VRConfig.ScopeSteadying.Value;
                want = 1f;
            }
            if (want > 0f)
            {
                _steadyStrength = strength; // kept while it eases out after the aim, so the ease-out is smooth
            }
            _steadyWeight = Mathf.MoveTowards(_steadyWeight, want, dt * 5f);
            // A snap turn turns the whole rig, the gun with it, in one frame: started again there, not followed as a swing (the
            // 5-degree cap held the aim up to 5 degrees behind for a tenth of a second; support hunt, 2026-10-20).
            if (!_steadyInit || _steadyWeight <= 0f || RavenfieldVR.Rig.VRRig.LastSnapFrame == Time.frameCount)
            {
                _steadyRot = gunRot;
                _steadyPrevRaw = gunRot;
                _steadyOmega = Vector3.zero;
                _steadyHandSpeed = 0f;
                _steadyInit = true;
                SteadyLag = 0f;
                return gunRot;
            }
            // The hand's own turn, frame to frame, low-passed at about 2 Hz (Tasks 230 and 249.11, 2026-10-18). The speed was
            // the filter's lag over the frame time: a slow drift settled at 2.2 Hz on a 45 Hz headset and 2.9 Hz on a 90 Hz
            // one, not the 1.6 Hz floor, so the steadying depended on the refresh rate. Now the cutoff follows the hand's
            // speed and does not flicker with the frame time.
            // The turn is low-passed as a signed vector and only then its size is taken (2026-10-19). Its size each frame was
            // the shake's mean turn rate: a degree at 10 Hz read as about 40 deg/s, so the shake itself opened the cutoff and
            // the bigger the shake, the less it was steadied. Signed, the two halves of a shake cancel; a steady swing reads
            // the same as before.
            Quaternion turn = gunRot * Runtime.RigidPose.Conjugate(_steadyPrevRaw);
            turn.ToAngleAxis(out float turnDeg, out Vector3 turnAxis);
            if (turnDeg > 180f)
            {
                turnDeg -= 360f; // the shorter way round
            }
            // A NaN axis would stick in the filter for the whole aim, so it reads as no turn (GunCollider and PoseSampler guard
            // the same call the same way).
            Vector3 omega = Mathf.Abs(turnDeg) > 1e-5f && !float.IsNaN(turnAxis.x) && !float.IsInfinity(turnAxis.x)
                ? turnAxis * (turnDeg / Mathf.Max(dt, 1e-4f))
                : Vector3.zero;
            _steadyPrevRaw = gunRot;
            _steadyOmega += (omega - _steadyOmega) * (1f - Mathf.Exp(-2f * Mathf.PI * 2f * dt));
            _steadyHandSpeed = _steadyOmega.magnitude;
            // Cutoff frequency: 1 Hz for a tremor at 4x (lower at higher zoom), up to 8 Hz for a swing.
            // The zoom lowers the tremor's end only (2026-10-19, task 290): it divided the whole range, so at 12x a swing's ceiling
            // was 3.3 Hz and a pan at 30 deg/s trailed 2.5 deg (1.2 now); the shake is cut as before (0.65 Hz at 12x).
            float cutoff = Mathf.Lerp(1.6f / Mathf.Sqrt(Mathf.Clamp(zoom, 1.8f, 12f) / 2f), 8f, Mathf.InverseLerp(4f, 60f, _steadyHandSpeed));
            float alpha = 1f - Mathf.Exp(-2f * Mathf.PI * cutoff * dt);
            _steadyRot = Quaternion.Slerp(_steadyRot, gunRot, alpha);
            if (Quaternion.Angle(_steadyRot, gunRot) > 5f)
            {
                _steadyRot = Quaternion.RotateTowards(gunRot, _steadyRot, 5f);
            }
            // The setting is applied once, after the ease: at 1 the steadying is the full filter, as before; 0.25 is a quarter.
            float w = _steadyWeight * _steadyWeight * (3f - 2f * _steadyWeight) * _steadyStrength;
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
                Sounds.Update(); // a mechanical sound's slice still fades out at its end (it stopped there at full level: a click)
                return;
            }
            // A support hand that loses tracking (hidden behind the gun from the headset's cameras) still holds: the two-hand aim eases
            // out over a third of a second, not a tenth, which swung the barrel by the whole steer in a blink (support hunt, 2026-10-20).
            _twoHandBlend = Mathf.MoveTowards(_twoHandBlend, TwoHanded && off.Tracked ? 1f : 0f, dt * (TwoHanded && !off.Tracked ? 3f : 10f));

            WeaponRigData d = Rig.Data;
            RigidPose hand = HoldFrame(main, d);
            var gripInGun = new RigidPose(d.GripPoint, d.HasGripRotation ? d.GripRotation : Quaternion.LookRotation(d.AimAxis, d.Up));
            Quaternion gunRot = (hand.rotation * Runtime.RigidPose.Conjugate(gripInGun.rotation));

            if (_twoHandBlend > 0f && d.HasSupportPoint && !d.IsPistol)
            {
                Vector3 dirGun = (d.SupportPoint - d.GripPoint).normalized;
                Vector3 dirWorld = off.PalmCentre - hand.position; // palm centre to palm centre, as the support point is
                if (dirWorld.sqrMagnitude > 1e-6f && dirGun.sqrMagnitude > 0.5f)
                {
                    // Up from the gun hand's palm (the thumb's side), as worldFrame takes it from the hand: a palm saved
                    // rolled on the gun rolled the gun by that much when the off hand took the handguard (sandbox review).
                    Quaternion gunFrameBase = Quaternion.LookRotation(dirGun, gripInGun.rotation * Vector3.up);
                    Quaternion worldFrame = OpenVRMath.SafeLookRotation(dirWorld.normalized, hand.Up);
                    Quaternion twoHand = worldFrame * Runtime.RigidPose.Conjugate(gunFrameBase);
                    // Eased in and out: taking or letting go of the handguard no longer starts and stops the turn at full speed.
                    float e = _twoHandBlend * _twoHandBlend * (3f - 2f * _twoHandBlend);
                    // Only from a hand ahead of the grip, along the barrel the gun hand holds: drawn back past the grip (the hold
                    // lets go only at 60 cm of stretch), the support hand swung the barrel round at the player and flipped it. And
                    // faded in from 3 to 8 cm apart instead of off below 5 cm at once: choked up, the aim jumped by the whole steer
                    // in one frame (support hunt, 2026-10-20).
                    float ahead = Vector3.Dot(dirWorld.normalized, gunRot * dirGun);
                    e *= Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(-0.2f, 0.3f, ahead))
                         * Mathf.SmoothStep(0f, 1f, Mathf.InverseLerp(0.03f, 0.08f, dirWorld.magnitude));
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

            // Recoil rotates the gun about its grip and pushes it back along the barrel: in the bore's frame (the palm's
            // frame is the same unless a sandbox pose turned the hand on the gun; then the kick followed the fingers).
            Quaternion recoil = Quaternion.Euler(_recoilRot.x, _recoilRot.y, 0f);
            Quaternion boreInGun = Quaternion.LookRotation(d.AimAxis, d.Up);
            Quaternion boreFrame = gunRot * boreInGun * recoil;
            gunRot = boreFrame * Runtime.RigidPose.Conjugate(boreInGun);
            Quaternion aimFrame = gunRot * gripInGun.rotation;
            Vector3 gripWorld = hand.position + _lagPos + boreFrame * _recoilPos;
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

            bool part = Core.PerfProbe.Part("held: parts and hands"); // (timed apart in a profiled run)
            Reload.StepParts();
            ApplyPartOverrides();
            SnapHands(main, off);
            if (part)
            {
                Core.PerfProbe.End("held: parts and hands");
            }
            // The reload first (it sets this frame's keep-out and held palm for the gloves), then the arm: solved before it,
            // the arm went to last frame's glove placement (255.I2-7).
            part = Core.PerfProbe.Part("held: reload");
            Reload.BeforeRender();
            if (part)
            {
                Core.PerfProbe.End("held: reload");
            }
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

        private int _modeOptic = -1; // whether the model has an optic for its narrower sight modes (-1: not looked yet)

        private void UpdateModeScopes()
        {
            if (_modeScopes == null)
            {
                return;
            }
            int mode = Weapon.GetSightModeIndex();
            if (_modeOptic < 0)
            {
                // A narrower mode with no optic of its own in the model is only the flat game's zoom: its lens was a second disc
                // floating in front of the red dot, a visor that was not there (user, 2026-10-20 night: "some guns have a 2nd
                // visor ... happens on the patriot"; the PATRIOT TAC's Spec Ops mode). Only a gun whose model has one gets a
                // lens: parts the sight mode moves in (a magnifier flipping behind the dot) or a magnifier named in the model.
                _modeOptic = Rig.Data.SightPartPaths.Count > 0 || _magnifierFlip.Part != null ? 1 : 0;
                if (_modeOptic == 0)
                {
                    Log.InfoOnce("modelens-none:" + Rig.Data.Key, $"{Rig.Data.Key}: its narrower sight mode has no optic in the model (no part moves with the mode, no magnifier): no lens of the mod's is drawn for it.");
                }
            }
            for (int i = 0; i < _modeScopes.Length; i++)
            {
                SightLine line = Rig.Data.Sights[i];
                bool wanted = i == mode && line.Magnified && _modeOptic == 1;
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
        /// A kit carried like a bag (it throws an ammo box or medipack): no glove is shown on it; the bag hangs from the
        /// hand the way the game shows it.
        /// </summary>
        private readonly bool _isKit;
        /// <summary>Throwables already put in the fist by their middle (once per analysis).</summary>
        private static readonly HashSet<WeaponRigData> GrenadeInFist = new HashSet<WeaponRigData>();
        /// <summary>Hand-sized throwables held by their middle, with their half thickness (m).</summary>
        private static readonly Dictionary<WeaponRigData, float> InFistHollow = new Dictionary<WeaponRigData, float>();

        /// <summary>
        /// Where a hand holds a weapon by: its palm centre (a grip's), or for a hand-sized throwable held by its middle the fist's
        /// hollow. On the palm centre, which is on the palm's surface, half a grenade was in the palm (14 of 72 glove points in a
        /// frag, the palm pushed 4 cm off it; improve round, 2026-10-21). The hollow is where the curled fingers close round it.
        /// </summary>
        internal static RigidPose HoldFrame(VRHand hand, WeaponRigData d)
        {
            RigidPose frame = hand.GunFrame;
            if (d == null || !InFistHollow.TryGetValue(d, out float half))
            {
                return frame;
            }
            // From the hollow (on the palm's skin) out along the palm's normal by the skin it covers plus its own half thickness, as a
            // magazine rests on the palm (MagazineProp.HoldPose): at the hollow itself half of it was still in the palm.
            float zHold = VRHand.FistHollow.z, yHold = VRHand.FistHollow.y;
            float rest = RavenfieldVR.Rig.HandModel.PalmSurfaceFromGrip(zHold - half, zHold + half, yHold - half, yHold + half) + half;
            Vector3 held = hand.Palm.position + hand.Palm.rotation * new Vector3(-hand.Mirror, 0f, 0f) * rest;
            return new RigidPose(frame.position + (held - hand.PalmCentre), frame.rotation);
        }

        /// <summary>
        /// A grenade sits in the fist: its drawn middle where a grip goes. Held by where the throw animation's palm was, it hung
        /// against the side of the glove, in the air (user, 2026-10-16). Hand-sized things only; once per type (the data is
        /// shared). For the gun hand's draw and the free hand's.
        /// </summary>
        internal static void CentreGrenadeGrip(WeaponRig rig, Weapon weapon)
        {
            if (rig == null || weapon == null || GrenadeInFist.Contains(rig.Data))
            {
                return;
            }
            Bounds gb = GunCollider.VisibleBounds(rig, out bool gMeasured, ThrowableHandler.FindModPin(weapon.transform));
            if (!gMeasured)
            {
                return; // tried again on the next draw (255.I5-1)
            }
            // Without its pin or ring, which pulled the middle toward the top.
            GrenadeInFist.Add(rig.Data);
            if (Mathf.Max(gb.size.x, Mathf.Max(gb.size.y, gb.size.z)) < 0.2f)
            {
                InFistHollow[rig.Data] = Mathf.Clamp(Mathf.Min(gb.extents.x, Mathf.Min(gb.extents.y, gb.extents.z)), 0.01f, 0.06f);
            }
            if (Mathf.Max(gb.size.x, Mathf.Max(gb.size.y, gb.size.z)) < 0.2f && Vector3.Distance(gb.center, rig.Data.GripPoint) > 0.01f)
            {
                Log.Info($"{rig.Data.Key}: held by its middle, in the fist ({Vector3.Distance(gb.center, rig.Data.GripPoint) * 100f:0.0} cm from the animation's palm).");
                rig.Data.GripPoint = gb.center;
            }
        }

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

        /// <summary>The snap-off notes are said (once per hold: SnapHands runs every frame).</summary>
        private bool _saidGripSnapOff, _saidSupportSnapOff;

        // The support hand's hold, taken where it gripped and kept there until let go (user, 2026-10-20 night: "if a player grips,
        // dont make him able to move his hand anywhere else unless he let go of the gun"; task 324): the glove followed the hand
        // along the handguard every frame, and a gun with no support pose of its own left the glove on the tracked hand.
        private bool _supportHoldStarted;
        private float _supportSlideHeld = float.NaN; // along the aim from the weapon's support pose (m), taken at the grab
        private RigidPose _supportGrabInGun; // the glove at the grab, in the gun body's frame
        private bool _supportGrabOnGun; // the grab was on the handguard (within 5 cm of its line)

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
                if (!_saidGripSnapOff && main.Model.SnapPose == null && _gripHand != null && main.Role == HandRole.Weapon && _gripHand.TryPose(Rig.Weapon.transform, out RigidPose off6))
                {
                    _saidGripSnapOff = true; // once a hold: this runs every frame, and the strings and the pose were made every frame
                    // Said once: the glove then is not put on the grip pose (the RPG-7's hands "float while gripped", user,
                    // headset, 2026-10-18 evening; task 276).
                    Log.InfoOnce("gripsnapoff:" + Rig.Data.Key, $"{Rig.Data.Key}: the gun hand is not put on the weapon's own grip pose: its hand bone is {Vector3.Distance(off6.position, Rig.ToWorld(Rig.Data.GripPoint)) * 100f:0.0} cm from the grip point (6 cm at most).");
                }
            }
            if (off.Model == null)
            {
                return;
            }
            if (off.Role != HandRole.Foregrip)
            {
                _supportHoldStarted = false;
            }
            else if (!_supportHoldStarted && Rig.GunBody != null)
            {
                _supportHoldStarted = true;
                _supportSlideHeld = float.NaN;
                _supportGrabInGun = Rig.GunBodyPose.Inverse().Multiply(off.Model.TrackedGlovePose);
                // Held where it gripped only when it gripped on the gun: a grab is taken up to 12 cm off the handguard, and that
                // pose pinned the glove in the air beside the gun (hold review).
                Vector3 palm = off.PalmCentre;
                _supportGrabOnGun = Reload == null || Vector3.Distance(palm, Reload.HandguardPoint(palm)) < 0.05f;
            }
            if (_system.Second != null && _system.Second.Hand == off)
            {
                return; // that glove is on the second weapon's own grip pose (cleared here every frame, it grasped 4.3 cm off its grip)
            }
            if (_supportHand != null && off.Role == HandRole.Foregrip && _supportHand.TryPose(Rig.Weapon.transform, out RigidPose support)
                && (support.position - Rig.ToWorld(Rig.Data.SupportPoint)).sqrMagnitude < 0.12f * 0.12f)
            {
                if (off.Side == Hand.Right)
                {
                    support = MirrorAcrossGun(support); // the rig's support hand is a left hand
                }
                if (!Rig.Data.IsPistol && !SupportHeldInPlace())
                {
                    // Along the handguard to where the hand gripped, within the stretch between grip and muzzle: taken once, at
                    // the grab, and kept for the hold (it followed the hand along the barrel every frame; task 324).
                    Vector3 axis = Rig.AimWorld;
                    if (float.IsNaN(_supportSlideHeld))
                    {
                        float slide = Vector3.Dot(off.PalmCentre - support.position, axis);
                        float min = Vector3.Dot(Rig.ToWorld(Rig.Data.GripPoint) - support.position, axis) + 0.08f;
                        // The gun's length along the aim (the drawn barrel's front where the muzzle sits inside it, Tasks 266): the
                        // same projection as the muzzle point for every other gun.
                        float max = Vector3.Dot(Rig.ToWorld(Rig.Data.GripPoint + Rig.Data.AimAxis * Rig.Data.LengthFromGrip) - support.position, axis) - 0.04f;
                        _supportSlideHeld = Mathf.Clamp(slide, Mathf.Min(min, 0f), Mathf.Max(max, 0f));
                    }
                    support.position += axis * _supportSlideHeld;
                }
                off.Model.SnapPose = support;
            }
            else
            {
                if (!_saidSupportSnapOff && _supportHand != null && off.Role == HandRole.Foregrip && _supportHand.TryPose(Rig.Weapon.transform, out RigidPose off12))
                {
                    _saidSupportSnapOff = true;
                    Log.InfoOnce("supportsnapoff:" + Rig.Data.Key, $"{Rig.Data.Key}: the support hand is not put on the weapon's own support pose: its hand bone is {Vector3.Distance(off12.position, Rig.ToWorld(Rig.Data.SupportPoint)) * 100f:0.0} cm from the support point (12 cm at most; task 276); it stays where it gripped.");
                }
                // No pose of the weapon's own to take: the glove stays on the gun where it gripped (it followed the tracked hand
                // anywhere while holding; task 324).
                // Not a pistol's cup: that glove settles onto the cup by its grasp anchor (pinned, it froze where the hand grabbed,
                // up to 12 cm off the cup; hold review). Nor a grab made off the gun.
                off.Model.SnapPose = off.Role == HandRole.Foregrip && _supportHoldStarted && Rig.GunBody != null && !Rig.Data.IsPistol && _supportGrabOnGun
                    ? Rig.GunBodyPose.Multiply(_supportGrabInGun)
                    : (RigidPose?)null;
            }
        }

        private int _supportInPlace = -1;

        /// <summary>
        /// The support hand holds something that does not run along the barrel: the magazine (a PPSh's drum, a Thompson's), or
        /// a launcher's own front grip. It stays where the weapon's own pose puts it instead of sliding along the handguard to
        /// where the hand is (user, headset, 2026-10-18 evening: "ppshs can be held by the magazine but the hand can still move
        /// forward; same with the rocket launcher"). Worked out once per hold.
        /// </summary>
        internal bool SupportHeldInPlace()
        {
            if (_supportInPlace < 0)
            {
                WeaponRigData d = Rig.Data;
                bool onMag = false;
                if (d.MagBounds.size.sqrMagnitude > 1e-6f)
                {
                    Vector3 local = Runtime.RigidPose.Conjugate(d.MagSeat.rotation) * (d.SupportPoint - d.MagSeat.position);
                    Bounds b = d.MagBounds;
                    b.Expand(0.04f); // (2 cm all round: the palm's centre sits on the magazine's face, not inside it)
                    onMag = b.Contains(local);
                }
                _supportInPlace = onMag || IsLauncher(d, Weapon) ? 1 : 0;
                if (_supportInPlace == 1)
                {
                    Log.InfoOnce("supportinplace:" + d.Key, $"{d.Key}: the support hand stays where it holds the gun ({(onMag ? "on the magazine" : "on the launcher's grip")}), it does not slide along the barrel.");
                }
            }
            return _supportInPlace == 1;
        }

        /// <summary>Self-test: where the gun hand's glove, its snap pose and the weapon's own hand bones are, in the grip's aim frame (cm).</summary>
        internal string HandReport(VRHand main)
        {
            RigidPose g = GripWorld;
            Quaternion inv = Runtime.RigidPose.Conjugate(g.rotation);
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
        private RigidPose MirrorAcrossGun(RigidPose p) => MirrorAcrossGun(Rig, p);

        /// <summary>The same for any weapon's rig (the free hand's second weapon).</summary>
        internal static RigidPose MirrorAcrossGun(WeaponRig rig, RigidPose p)
        {
            Vector3 right = rig.DirToWorld(OpenVRMath.SafeLookRotation(rig.Data.AimAxis, rig.Data.Up) * Vector3.right);
            Vector3 centre = rig.ToWorld(rig.Data.MuzzlePoint);
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
            _releasedHold = true; // (a slide measurement still on the GPU adds no knob to a gun that is put away)
            // The physics stand-in first: whatever throws below, it must not be left at the gun's last pose, where dropped weapons
            // and magazines would bounce off an invisible gun-shaped box (it is DontDestroyOnLoad).
            _gunCollider?.Destroy();
            // The lenses and the round counter too (their objects have no parent): last, a step below that threw left them behind
            // (lifecycle hunt, 2026-10-20).
            _scope?.Destroy();
            if (_modeScopes != null)
            {
                foreach (VRScope s in _modeScopes)
                {
                    s?.Destroy();
                }
            }
            _counter.Destroy();
            // The hands next: whatever fails below, no glove may stay closed on a weapon that is gone.
            if (_system.Rig.Dominant.Model != null)
            {
                _system.Rig.Dominant.Model.Solid = null;
                _system.Rig.Dominant.Model.SnapPose = null;
                _system.Rig.Dominant.Model.HiddenByHeld = false;
            }
            if (_system.Rig.Off.Model != null && (_system.Second == null || _system.Second.Hand != _system.Rig.Off))
            {
                _system.Rig.Off.Model.Solid = null; // (not the second weapon's hand: its glove is on that weapon)
                _system.Rig.Off.Model.SnapPose = null;
            }
            _magnifierFlip.Release();
            try
            {
                Reload.Release();
            }
            catch (System.Exception e)
            {
                // The rest of the cleanup (parts, hands, renderers) must still run.
                Log.Exception("Releasing reload state", e);
            }
            Sounds.Destroy(); // before the steps below that can throw: a failed release left its audio object on the gun
            if (_magHidden)
            {
                RestoreMagazine();
            }
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
            RestoreChamberShells();
            RestoreSightParts();
            foreach (Renderer r in _scopeLenses)
            {
                if (r != null)
                {
                    r.enabled = true;
                }
            }
            _scopeLenses.Clear();
            Rig.ShowSpareParts();
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
                    r.forceRenderingOff = false;
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
                _system.Rig.Dominant.Model.WeaponGripThumbBase = -1f;
                _system.Rig.Dominant.Model.WeaponGripThumbCurl = -1f;
                _system.Rig.Dominant.Model.WeaponIsPistol = false;
                _system.Rig.Dominant.Model.WeaponClass = WeaponClass.None;
                _system.Rig.Dominant.Model.SnapPose = null;
                _system.Rig.Dominant.Model.Solid = null;
                _system.Rig.Dominant.Model.HiddenByHeld = false;
            }
            if (_system.Rig.Off.Model != null && (_system.Second == null || _system.Second.Hand != _system.Rig.Off))
            {
                // Not a hand holding the second weapon: its shape and grip class are that weapon's (wiped by a switch of the gun,
                // a pistol in the free hand was then held as a rifle; second-weapon hunt, 2026-10-21).
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
