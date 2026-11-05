using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>An animation event (or an audio object switched on by the animation) seen while sampling.</summary>
    public struct SampledCue
    {
        public float Time;
        public string Function;
        public int IntParameter;
        public float FloatParameter;
        public string StringParameter;
        /// <summary>Hierarchy path of an AudioSource object the animation activated (when Function is null).</summary>
        public int[] AudioObjectPath;
    }

    /// <summary>
    /// Builds an inert copy of a weapon (instantiated under an inactive parent so no script ever wakes up, then
    /// stripped of scripts, audio, cameras, lights, physics and visible renderers) and steps its animator manually
    /// through idle, fire, reload and aim. Records, relative to the gun body: every transform's trajectory, which
    /// objects the animation hides or reveals, and which sound events / audio objects fire and when.
    /// </summary>
    internal sealed class PoseSampler : IDisposable
    {
        public const float Dt = 1f / 30f;

        /// <summary>
        /// Whitelisted animation event functions with a receiver in the game that only make noise or particles
        /// (SoundBank, AnimationParticleEmitter), so they are safe to replay on the live weapon.
        /// </summary>
        public static readonly HashSet<string> CueFunctions = new HashSet<string>
        {
            "PlaySoundBank", "PlayRandom", "PlayNext", "EmitParticles"
        };

        private readonly Weapon _weapon;
        private readonly Transform _liveRoot;
        private readonly Transform[] _live;
        private readonly Dictionary<Transform, int> _index;
        private readonly int _gunIndex;

        private GameObject _holder;
        private Transform _cloneRoot;
        private Transform[] _clone;
        private Animator _anim;
        private HashSet<int> _params;
        private bool[] _hasPlayOnAwakeAudio;

        private readonly RigidPose[] _idle;
        private readonly Vector3[] _idleLocalPos;
        private readonly bool[] _idleVisible;
        public readonly RigidPose[] RelIdle;

        public readonly float[] FireMax;
        public readonly Vector3[] FireVec;
        public readonly float[] ReloadMax;
        public readonly Vector3[] FirstDir;
        /// <summary>Largest rotation (degrees) of each part away from its idle orientation, in gun space, and its axis.</summary>
        public readonly float[] FireRotMax;
        public readonly Vector3[] FireRotAxis;
        public readonly float[] ReloadRotMax;
        public readonly Vector3[] ReloadRotAxis;
        /// <summary>[transform][step] rotation away from idle (degrees) during the reload.</summary>
        public float[][] ReloadAngle;

        /// <summary>[transform][step] gun-relative positions during the reload (null when no reload was sampled).</summary>
        public Vector3[][] ReloadTrajectory;
        /// <summary>[transform][step] visibility during the reload.</summary>
        public bool[][] ReloadVisible;
        /// <summary>
        /// [transform][step] gun-relative rotations during the reload: how a shell lies where the reload pushes it into a tube
        /// (its seat there, nose first; WeaponAnalyzer.ShellPort). Kept only for parts that draw something and move a centimetre
        /// or more in the reload, from that step on (null for the others): for every part of every weapon it doubled the
        /// sampler's garbage, up to about 1.8 MB an analysis.
        /// </summary>
        public Quaternion[][] ReloadRotation;
        private bool[] _drawsSomething;
        /// <summary>Kept in <see cref="ReloadRotation"/> from the first step whatever they draw or move: the support hand and its fingers.</summary>
        private readonly bool[] _keepRotation;
        public Vector3[][] FireTrajectory;
        public int ReloadSteps;
        public int FireSteps;

        public readonly List<SampledCue> ReloadCues = new List<SampledCue>();
        public readonly List<SampledCue> FireCues = new List<SampledCue>();

        public bool HasReload;
        public bool HasAim;
        public RigidPose AimGunBody;
        /// <summary>
        /// The aim pose for each of the weapon's sight modes (index = mode; null where a mode was not sampled): a flipped-in
        /// magnifier, a canted or offset sight moves the gun to a different line in front of the eye.
        /// </summary>
        public RigidPose?[] AimModes;
        /// <summary>
        /// Per sight mode (aiming), every transform's pose relative to the gun body: what a sight mode moves (a flip
        /// magnifier swung in front of a red dot), for showing it there when not aiming too. Null without sight modes.
        /// </summary>
        public RigidPose[][] ModePoses;

        public PoseSampler(Weapon weapon, Transform[] live, Dictionary<Transform, int> index, Transform gunBody)
        {
            _weapon = weapon;
            _liveRoot = weapon.transform;
            _live = live;
            _index = index;
            _gunIndex = index.TryGetValue(gunBody, out int gi) ? gi : 0;
            int n = live.Length;
            _idle = new RigidPose[n];
            _idleLocalPos = new Vector3[n];
            _idleVisible = new bool[n];
            RelIdle = new RigidPose[n];
            FireMax = new float[n];
            FireVec = new Vector3[n];
            ReloadMax = new float[n];
            FirstDir = new Vector3[n];
            FireRotMax = new float[n];
            FireRotAxis = new Vector3[n];
            ReloadRotMax = new float[n];
            ReloadRotAxis = new Vector3[n];
            _keepRotation = new bool[n];
        }

        /// <summary>
        /// Before <see cref="Run"/>: <paramref name="bone"/> and everything under it get their reload rotations kept (the support hand
        /// and its fingers: how the animation holds the magazine; WeaponAnalyzer.MagHoldOf). Bones draw nothing, so they were left out.
        /// </summary>
        public void KeepRotationsUnder(Transform bone)
        {
            if (bone == null)
            {
                return;
            }
            foreach (Transform t in bone.GetComponentsInChildren<Transform>(true))
            {
                if (_index.TryGetValue(t, out int i))
                {
                    _keepRotation[i] = true;
                }
            }
        }

        public bool Run(ActionType action, Weapon.Configuration config)
        {
            try
            {
                if (!BuildClone())
                {
                    return false;
                }
                SetLayerWeight(1f);
                SetBool(Weapon.NO_AMMO_PARAMETER_HASH, false);
                SetInt(Weapon.LOADED_AMMO_PARAMETER_HASH, Mathf.Max(1, config.ammo));
                if (Has(Weapon.RANDOM_PARAMETER_HASH))
                {
                    // The game draws this 0-100 each frame, and mods pick a fire or reload clip by it. Left at 0, a first choice written as
                    // "random > 0" never played here: the AS Val's bolt (8 cm in the fire clip) was never seen and it was racked by nothing.
                    // 1, under every mod's own thresholds: the guns that sampled the first clip at 0 go on sampling it.
                    _anim.SetFloat(Weapon.RANDOM_PARAMETER_HASH, 1f);
                }
                SetTrigger(Weapon.UNHOLSTER_PARAMETER_HASH);
                Step(2.5f);
                CaptureIdle();
                SampleAmmoDrawing(config);

                // Fire: record the reciprocating motion and any sound events of the shot.
                SetTrigger(Weapon.FIRE_PARAMETER_HASH);
                const int fireSteps = 24;
                FireSteps = fireSteps;
                FireTrajectory = Alloc(fireSteps);
                var tracker = new EventTracker(_anim);
                for (int k = 0; k < fireSteps; k++)
                {
                    StepOnce(Dt);
                    float t = (k + 1) * Dt;
                    Record(k, FireTrajectory, null, FireMax, FireVec, null, FireRotMax, FireRotAxis, null);
                    tracker.Collect(t, FireCues);
                }
                ResetTrigger(Weapon.FIRE_PARAMETER_HASH);
                Step(1.5f);

                if (action != ActionType.NoAmmo && action != ActionType.Melee && action != ActionType.Throwable
                    && (Has(Weapon.RELOAD_PARAMETER_HASH) || Has(Weapon.RELOADING_PARAMETER_HASH)))
                {
                    SampleReload(config);
                }

                if (Has(Weapon.AIM_PARAMETER_HASH))
                {
                    _anim.Rebind();
                    SetLayerWeight(1f);
                    // Rebind keeps parameter values: clear what the reload left set, or advanced-reload
                    // weapons stay in their reload loop and the "aim" pose is really a reload pose.
                    SetBool(Weapon.RELOADING_PARAMETER_HASH, false);
                    ResetTrigger(Weapon.RELOAD_PARAMETER_HASH);
                    ResetTrigger(Weapon.FIRE_PARAMETER_HASH);
                    SetBool(Weapon.NO_AMMO_PARAMETER_HASH, false);
                    SetInt(Weapon.LOADED_AMMO_PARAMETER_HASH, Mathf.Max(1, config.ammo));
                    Step(1.5f);
                    SetBool(Weapon.AIM_PARAMETER_HASH, true);
                    Step(1.2f);
                    AimGunBody = RootRelative(_clone[_gunIndex]);
                    HasAim = true;
                    SampleSightModes(config);
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Exception($"Animator sampling failed for {_weapon.name}", e);
                // What was recorded before the throw is the clone's, and the live pose becomes the idle now: kept, the rules
                // that read the reload or the fire track paired them with that idle (unswept-areas hunt, 2026-10-24).
                HasReload = false;
                FireTrajectory = null;
                return false;
            }
        }

        /// <summary>Holds the aim and steps through every sight mode the weapon has, recording the gun's aim pose in each.</summary>
        private void SampleSightModes(Weapon.Configuration config)
        {
            int modes = config.sightModes != null ? config.sightModes.Length : 0;
            if (modes < 2 || !Has(Weapon.SIGHT_MODE_PARAMETER_HASH))
            {
                return;
            }
            AimModes = new RigidPose?[modes];
            AimModes[0] = AimGunBody;
            ModePoses = new RigidPose[modes][];
            ModePoses[0] = GunRelativeAll();
            bool smooth = Has(Weapon.SMOOTH_SIGHT_MODE_PARAMETER_HASH);
            for (int i = 1; i < modes; i++)
            {
                SetInt(Weapon.SIGHT_MODE_PARAMETER_HASH, i);
                if (smooth)
                {
                    _anim.SetFloat(Weapon.SMOOTH_SIGHT_MODE_PARAMETER_HASH, i);
                }
                Step(1.0f);
                AimModes[i] = RootRelative(_clone[_gunIndex]);
                ModePoses[i] = GunRelativeAll();
            }
            SetInt(Weapon.SIGHT_MODE_PARAMETER_HASH, 0);
            if (smooth)
            {
                _anim.SetFloat(Weapon.SMOOTH_SIGHT_MODE_PARAMETER_HASH, 0f);
            }
        }

        /// <summary>Whether this gun's animator draws parts by its round count: gone at none that are there at full.</summary>
        public bool AmmoDrawsParts { get; private set; }

        /// <summary>
        /// With no rounds, what goes: on a layer of their own the "loaded ammo" clips scale the QJB201's 11 rounds of belt away and park the
        /// MK46's 12 out of sight. Six parts or more is a belt drawn so, not a bolt caught back with its handle and spring.
        /// </summary>
        private void SampleAmmoDrawing(Weapon.Configuration config)
        {
            if (!Has(Weapon.LOADED_AMMO_PARAMETER_HASH) || _anim.layerCount < 2)
            {
                return;
            }
            SetInt(Weapon.LOADED_AMMO_PARAMETER_HASH, 0);
            Step(0.5f);
            int gone = 0;
            RigidPose gunInv = RootRelative(_clone[_gunIndex]).Inverse();
            for (int i = 0; i < _clone.Length; i++)
            {
                if (_idleVisible[i] && (!Visible(_clone[i]) || (gunInv.Multiply(RootRelative(_clone[i])).position - RelIdle[i].position).sqrMagnitude > 0.05f * 0.05f))
                {
                    gone++;
                }
            }
            AmmoDrawsParts = gone >= 6;
            SetInt(Weapon.LOADED_AMMO_PARAMETER_HASH, Mathf.Max(1, config.ammo));
            Step(0.5f);
        }

        private void SampleReload(Weapon.Configuration config)
        {
            SetLayerWeight(0f);
            SetBool(Weapon.NO_AMMO_PARAMETER_HASH, false);
            SetInt(Weapon.LOADED_AMMO_PARAMETER_HASH, 0);
            SetTrigger(Weapon.RELOAD_PARAMETER_HASH);
            if (config.advancedReload)
            {
                SetBool(Weapon.RELOADING_PARAMETER_HASH, true);
                int motion = 1;
                if (config.allowedReloads != null)
                {
                    foreach (int r in config.allowedReloads)
                    {
                        motion = Mathf.Max(motion, r);
                    }
                }
                SetInt(Weapon.RELOAD_MOTION_PARAMETER_HASH, motion);
            }
            // The whole reload the game waits for, up to 12 s: at 6 s a machine gun's or a shotgun's long reload lost its end
            // (the magazine in, the bolt home).
            float duration = Mathf.Clamp(config.reloadTime, 0.5f, 12f) + 0.4f;
            int steps = Mathf.CeilToInt(duration / Dt);
            ReloadSteps = steps;
            ReloadTrajectory = Alloc(steps);
            ReloadVisible = new bool[_clone.Length][];
            ReloadAngle = new float[_clone.Length][];
            ReloadRotation = new Quaternion[_clone.Length][];
            for (int i = 0; i < _clone.Length; i++)
            {
                ReloadVisible[i] = new bool[steps];
                ReloadAngle[i] = new float[steps];
            }
            var tracker = new EventTracker(_anim);
            var wasActive = new bool[_clone.Length];
            for (int i = 0; i < _clone.Length; i++)
            {
                wasActive[i] = _clone[i].gameObject.activeInHierarchy;
            }
            for (int k = 0; k < steps; k++)
            {
                StepOnce(Dt);
                float t = (k + 1) * Dt;
                Record(k, ReloadTrajectory, ReloadVisible, ReloadMax, null, FirstDir, ReloadRotMax, ReloadRotAxis, ReloadAngle, ReloadRotation);
                tracker.Collect(t, ReloadCues);
                // Audio objects the animation switches on (their AudioSource would play on awake).
                for (int i = 0; i < _clone.Length; i++)
                {
                    bool active = _clone[i].gameObject.activeInHierarchy;
                    if (active && !wasActive[i] && _hasPlayOnAwakeAudio[i])
                    {
                        ReloadCues.Add(new SampledCue { Time = t, AudioObjectPath = WeaponRig.PathOf(_cloneRoot, _clone[i]) });
                    }
                    wasActive[i] = active;
                }
            }
            ResetTrigger(Weapon.RELOAD_PARAMETER_HASH);
            SetBool(Weapon.RELOADING_PARAMETER_HASH, false);
            HasReload = true;
            SetLayerWeight(1f);
        }

        private Vector3[][] Alloc(int steps)
        {
            var a = new Vector3[_clone.Length][];
            for (int i = 0; i < a.Length; i++)
            {
                a[i] = new Vector3[steps];
            }
            return a;
        }

        private bool BuildClone()
        {
            _holder = new GameObject("RavenfieldVR Weapon Analysis");
            _holder.SetActive(false);
            _holder.transform.position = new Vector3(0f, -10000f, 0f);
            GameObject clone = UnityEngine.Object.Instantiate(_weapon.gameObject, _holder.transform, false);
            _cloneRoot = clone.transform;
            _cloneRoot.localPosition = Vector3.zero;
            _cloneRoot.localRotation = Quaternion.identity;
            _cloneRoot.localScale = _weapon.transform.localScale;
            clone.SetActive(true);

            _clone = _cloneRoot.GetComponentsInChildren<Transform>(true);
            if (_clone.Length != _live.Length)
            {
                Log.Warn($"Clone hierarchy of {_weapon.name} differs from the weapon ({_clone.Length} vs {_live.Length}).");
                return false;
            }
            _hasPlayOnAwakeAudio = new bool[_clone.Length];
            _drawsSomething = new bool[_clone.Length];
            for (int i = 0; i < _clone.Length; i++)
            {
                AudioSource a = _clone[i].GetComponent<AudioSource>();
                _hasPlayOnAwakeAudio[i] = a != null && a.playOnAwake && a.clip != null;
                _drawsSomething[i] = _clone[i].GetComponentInChildren<Renderer>(true) != null;
            }

            // Nothing in the clone may run, render, sound, collide or emit.
            MonoBehaviour[] scripts = clone.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = scripts.Length - 1; i >= 0; i--)
            {
                UnityEngine.Object.DestroyImmediate(scripts[i]);
            }
            // Disable rather than destroy: other components (FlareLayer, AudioListener…) may depend on these,
            // and a failed destroy would leave a live camera rendering into the headset.
            foreach (AudioSource a in clone.GetComponentsInChildren<AudioSource>(true))
            {
                a.playOnAwake = false;
                a.enabled = false;
            }
            foreach (Camera c in clone.GetComponentsInChildren<Camera>(true))
            {
                c.enabled = false;
            }
            foreach (AudioListener l in clone.GetComponentsInChildren<AudioListener>(true))
            {
                l.enabled = false;
            }
            foreach (Light l in clone.GetComponentsInChildren<Light>(true))
            {
                l.enabled = false;
            }
            foreach (Collider c in clone.GetComponentsInChildren<Collider>(true))
            {
                c.enabled = false;
            }
            foreach (Rigidbody r in clone.GetComponentsInChildren<Rigidbody>(true))
            {
                r.isKinematic = true;
                r.detectCollisions = false;
            }
            foreach (ParticleSystem p in clone.GetComponentsInChildren<ParticleSystem>(true))
            {
                ParticleSystem.MainModule main = p.main;
                main.playOnAwake = false;
            }
            foreach (Renderer r in clone.GetComponentsInChildren<Renderer>(true))
            {
                // Not drawn, but its own on/off kept: a rig that hides a part by switching its renderer off (a magazine in
                // the reload, a spare at rest) is read as hiding it.
                r.forceRenderingOff = true;
                _renderers[r.transform] = r;
            }

            _anim = clone.GetComponent<Animator>();
            if (_anim == null || _anim.runtimeAnimatorController == null)
            {
                return false;
            }
            _anim.fireEvents = false;
            _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _anim.applyRootMotion = false;
            _holder.SetActive(true);
            _anim.Rebind();
            _params = new HashSet<int>();
            foreach (AnimatorControllerParameter p in _anim.parameters)
            {
                _params.Add(p.nameHash);
            }
            _anim.Update(0f);
            return true;
        }

        private bool Has(int hash) => _params != null && _params.Contains(hash);

        private void SetTrigger(int hash)
        {
            if (Has(hash))
            {
                _anim.SetTrigger(hash);
            }
        }

        private void ResetTrigger(int hash)
        {
            if (Has(hash))
            {
                _anim.ResetTrigger(hash);
            }
        }

        private void SetBool(int hash, bool v)
        {
            if (Has(hash))
            {
                _anim.SetBool(hash, v);
            }
        }

        private void SetInt(int hash, int v)
        {
            if (Has(hash))
            {
                _anim.SetInteger(hash, v);
            }
        }

        private void SetLayerWeight(float w)
        {
            if (_anim.layerCount > 1)
            {
                _anim.SetLayerWeight(1, w);
            }
        }

        private void Step(float seconds)
        {
            for (float t = 0f; t < seconds - 1e-4f; t += Dt)
            {
                _anim.Update(Mathf.Min(Dt, seconds - t));
            }
        }

        private void StepOnce(float dt) => _anim.Update(dt);

        /// <summary>Every transform's pose relative to the gun body, as the animator has them now.</summary>
        private RigidPose[] GunRelativeAll()
        {
            RigidPose gunInv = RootRelative(_clone[_gunIndex]).Inverse();
            var poses = new RigidPose[_clone.Length];
            for (int i = 0; i < _clone.Length; i++)
            {
                poses[i] = gunInv.Multiply(RootRelative(_clone[i]));
            }
            return poses;
        }

        private RigidPose RootRelative(Transform t)
        {
            Quaternion inv = Quaternion.Inverse(_cloneRoot.rotation);
            return new RigidPose(inv * (t.position - _cloneRoot.position), inv * t.rotation);
        }

        private bool Visible(Transform t) => t.gameObject.activeInHierarchy && t.lossyScale.sqrMagnitude > 1e-6f
                                              && (!_renderers.TryGetValue(t, out Renderer r) || r == null || r.enabled);

        private readonly Dictionary<Transform, Renderer> _renderers = new Dictionary<Transform, Renderer>();

        private void CaptureIdle()
        {
            for (int i = 0; i < _clone.Length; i++)
            {
                _idle[i] = RootRelative(_clone[i]);
                _idleLocalPos[i] = _clone[i].localPosition;
                _idleVisible[i] = Visible(_clone[i]);
            }
            RigidPose gunInv = _idle[_gunIndex].Inverse();
            for (int i = 0; i < _clone.Length; i++)
            {
                RelIdle[i] = gunInv.Multiply(_idle[i]);
            }
        }

        private void Record(int step, Vector3[][] trajectory, bool[][] visible, float[] max, Vector3[] vecAtMax, Vector3[] firstDir,
            float[] rotMax, Vector3[] rotAxis, float[][] angles, Quaternion[][] rotations = null)
        {
            RigidPose gunInv = RootRelative(_clone[_gunIndex]).Inverse();
            for (int i = 0; i < _clone.Length; i++)
            {
                RigidPose pose = RootRelative(_clone[i]);
                // Rotation away from idle in gun space: cylinders, hinged covers, break-open barrels, bolt handles.
                Quaternion rel0 = gunInv.rotation * pose.rotation;
                Quaternion delta = rel0 * Quaternion.Inverse(RelIdle[i].rotation);
                delta.ToAngleAxis(out float angle, out Vector3 axis);
                if (float.IsNaN(angle) || float.IsNaN(axis.x) || float.IsInfinity(axis.x))
                {
                    angle = 0f;
                }
                else if (angle > 180f)
                {
                    angle = 360f - angle;
                    axis = -axis;
                }
                if (angles != null)
                {
                    angles[i][step] = angle;
                }
                if (angle > rotMax[i])
                {
                    rotMax[i] = angle;
                    rotAxis[i] = axis;
                }
                Vector3 rel = gunInv.TransformPoint(pose.position);
                if (float.IsNaN(rel.x) || float.IsNaN(rel.y) || float.IsNaN(rel.z))
                {
                    // Degenerate (zero-scaled) transforms: hold the last good position.
                    rel = step > 0 ? trajectory[i][step - 1] : RelIdle[i].position;
                }
                trajectory[i][step] = rel;
                if (visible != null)
                {
                    visible[i][step] = Visible(_clone[i]);
                }
                Vector3 d = rel - RelIdle[i].position;
                float m = d.magnitude;
                if (rotations != null && (_keepRotation[i] || _drawsSomething[i] && (rotations[i] != null || m > 0.01f)))
                {
                    if (rotations[i] == null)
                    {
                        rotations[i] = new Quaternion[trajectory[i].Length]; // from the step it first moved (zero before)
                    }
                    rotations[i][step] = rel0;
                }
                if (m > max[i])
                {
                    max[i] = m;
                    if (vecAtMax != null)
                    {
                        vecAtMax[i] = d;
                    }
                }
                if (firstDir != null && firstDir[i] == Vector3.zero && m > 0.015f)
                {
                    firstDir[i] = d / m;
                }
            }
        }

        /// <summary>No animator: the live pose is the only pose there is.</summary>
        public void UseLivePosesAsIdle()
        {
            Quaternion inv = Quaternion.Inverse(_liveRoot.rotation);
            for (int i = 0; i < _live.Length; i++)
            {
                Transform t = _live[i];
                _idle[i] = new RigidPose(inv * (t.position - _liveRoot.position), inv * t.rotation);
                _idleLocalPos[i] = t.localPosition;
                // Not activeInHierarchy: a prefab outside any scene (the warm-up analyses those) reads false everywhere,
                // and every part counted as hidden at rest for the whole session.
                _idleVisible[i] = WeaponAnalyzer.ActiveUpTo(t, _liveRoot);
            }
            RigidPose gunInv = _idle[_gunIndex].Inverse();
            for (int i = 0; i < _live.Length; i++)
            {
                RelIdle[i] = gunInv.Multiply(_idle[i]);
            }
        }

        public int IndexOf(Transform live) => _index.TryGetValue(live, out int i) ? i : -1;
        public RigidPose Idle(Transform live) => _index.TryGetValue(live, out int i) ? _idle[i] : RigidPose.Identity;
        public Vector3 IdleLocalPosition(Transform live) => _index.TryGetValue(live, out int i) ? _idleLocalPos[i] : live.localPosition;
        public bool IdleVisible(Transform live) => _index.TryGetValue(live, out int i) && _idleVisible[i];

        /// <summary>Axis-aligned bounds (gun-body space) of every mesh under <paramref name="live"/> at idle.</summary>
        public Bounds GunSpaceBounds(Transform live, RigidPose gunIdle)
        {
            RigidPose gunInv = gunIdle.Inverse();
            return MeshBounds(live, (t, p) => gunInv.TransformPoint(Idle(t).TransformPoint(Vector3.Scale(p, t.lossyScale))));
        }

        /// <summary>
        /// Laser module names: the body, dot and beam of a laser (镭射 and 激光 are laser in Chinese mod names), a flashlight.
        /// "Surefire X300" is the light-and-laser module of the FNX-45 models (its flashlight and laser variants carry no other name).
        /// </summary>
        internal static readonly Regex LaserModuleName = new Regex(@"laser|beam|镭射|激光|flash ?light|torch|surefire", RegexOptions.IgnoreCase);
        /// <summary>A part named just "light" (a flashlight's own mesh). Only the part's own name counts: a group named "light" may hold the gun's parts.</summary>
        private static readonly Regex LightPartName = new Regex(@"(^|[^a-z])light($|[^a-z])", RegexOptions.IgnoreCase);

        /// <summary>
        /// True when <paramref name="t"/> is a laser or light module: its own name, or a group above it below
        /// <paramref name="stop"/> (not counting it). A transform that <paramref name="keep"/> hangs under is the gun's own group,
        /// never a module, so a group that holds the whole gun (a model named after its laser) does not take every part with it.
        /// </summary>
        public static bool IsLaserModule(Transform t, Transform stop, Transform keep = null)
        {
            if (t == null)
            {
                return false;
            }
            if (LaserModuleName.IsMatch(t.name) || LightPartName.IsMatch(t.name))
            {
                return true;
            }
            for (Transform p = t.parent; p != null && p != stop; p = p.parent)
            {
                if (keep != null && keep.IsChildOf(p))
                {
                    break;
                }
                if (LaserModuleName.IsMatch(p.name))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Gun-space bounds of the meshes on <paramref name="live"/> within <paramref name="radius"/> of it (at idle): a sight's
        /// reticle quad that hangs metres out in front (a collimated dot: 6 m off on a P220's RMR) is not part of the slide.
        /// A laser module on the part is not the part either: its beam is a long thin mesh that can run far ahead of it, so
        /// left in, it would stretch the box the racking grab point is taken from.
        /// </summary>
        public Bounds GunSpaceBoundsNear(Transform live, RigidPose gunIdle, float radius)
        {
            Vector3 centre = Idle(live).position;
            Bounds near = GunSpaceBoundsWhere(live, gunIdle,
                t => (Idle(t).position - centre).sqrMagnitude > radius * radius || t != live && IsLaserModule(t, live));
            return near.size.sqrMagnitude > 1e-8f ? near : GunSpaceBounds(live, gunIdle);
        }

        /// <summary>One gun-space box per mesh under <paramref name="root"/> that <paramref name="skip"/> does not exclude.</summary>
        public List<Bounds> GunSpaceBoxes(Transform root, RigidPose gunIdle, Func<Transform, bool> skip)
        {
            RigidPose gunInv = gunIdle.Inverse();
            Func<Transform, Vector3, Vector3> toSpace = (t, p) => gunInv.TransformPoint(Idle(t).TransformPoint(Vector3.Scale(p, t.lossyScale)));
            var boxes = new List<Bounds>();
            void Add(Mesh mesh, Transform t)
            {
                if (mesh != null && !skip(t))
                {
                    bool any = false;
                    var b = new Bounds();
                    Encapsulate(ref b, ref any, mesh.bounds, t, toSpace);
                    boxes.Add(b);
                }
            }
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Add(mf.sharedMesh, mf.transform);
            }
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Add(smr.sharedMesh, smr.transform);
            }
            return boxes;
        }

        /// <summary>Gun-space bounds of the meshes under <paramref name="root"/> that <paramref name="skip"/> does not exclude.</summary>
        public Bounds GunSpaceBoundsWhere(Transform root, RigidPose gunIdle, Func<Transform, bool> skip)
        {
            RigidPose gunInv = gunIdle.Inverse();
            bool any = false;
            var b = new Bounds();
            Func<Transform, Vector3, Vector3> toSpace = (t, p) => gunInv.TransformPoint(Idle(t).TransformPoint(Vector3.Scale(p, t.lossyScale)));
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null && !skip(mf.transform))
                {
                    Encapsulate(ref b, ref any, mf.sharedMesh.bounds, mf.transform, toSpace);
                }
            }
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh != null && !skip(smr.transform))
                {
                    Encapsulate(ref b, ref any, smr.sharedMesh.bounds, smr.transform, toSpace);
                }
            }
            return any ? b : new Bounds();
        }

        /// <summary>
        /// Bounds of the meshes under <paramref name="live"/> that show at idle, in its own idle space. Rigs often keep a
        /// second, hidden magazine (the fresh one their reload brings in) parked somewhere under the magazine bone; it is
        /// not part of the magazine's shape. All meshes count when none is visible at idle.
        /// </summary>
        public Bounds LocalBounds(Transform live) => LocalBounds(live, null);

        /// <summary>
        /// <see cref="LocalBounds(Transform)"/> without the meshes under any of <paramref name="skip"/> (pieces that hang apart
        /// from the part at rest: the PKM box's belt links under it).
        /// </summary>
        public Bounds LocalBounds(Transform live, ICollection<Transform> skip)
        {
            RigidPose selfInv = Idle(live).Inverse();
            Func<Transform, Vector3, Vector3> toSpace = (t, p) => selfInv.TransformPoint(Idle(t).TransformPoint(Vector3.Scale(p, t.lossyScale)));
            // Pieces a mod parks far away at rest to hide them (the PKM/PKP box's belt links wait 25 m off until the
            // reload brings them in) are not part of what is there: they made the box measure 25 m.
            Func<Transform, bool> near = t => selfInv.TransformPoint(Idle(t).position).sqrMagnitude < ParkedAway * ParkedAway && !UnderAny(t, skip);
            Bounds shown = MeshBounds(live, toSpace, t => IdleVisible(t) && near(t));
            if (shown.size.sqrMagnitude > 1e-8f)
            {
                return shown;
            }
            Bounds nearby = MeshBounds(live, toSpace, near);
            return nearby.size.sqrMagnitude > 1e-8f ? nearby : MeshBounds(live, toSpace);
        }

        /// <summary>
        /// Each mesh under <paramref name="live"/> that shows at rest and is not parked away, with its box in the part's own rest
        /// space (as <see cref="LocalBounds(Transform)"/> measures them).
        /// </summary>
        public List<KeyValuePair<Transform, Bounds>> ShownMeshBoxes(Transform live)
        {
            RigidPose selfInv = Idle(live).Inverse();
            Func<Transform, Vector3, Vector3> toSpace = (t, p) => selfInv.TransformPoint(Idle(t).TransformPoint(Vector3.Scale(p, t.lossyScale)));
            var boxes = new List<KeyValuePair<Transform, Bounds>>();
            void Add(Transform t, Mesh mesh)
            {
                if (mesh == null || !IdleVisible(t) || selfInv.TransformPoint(Idle(t).position).sqrMagnitude >= ParkedAway * ParkedAway)
                {
                    return;
                }
                bool any = false;
                var b = new Bounds();
                Encapsulate(ref b, ref any, mesh.bounds, t, toSpace);
                boxes.Add(new KeyValuePair<Transform, Bounds>(t, b));
            }
            foreach (MeshFilter mf in live.GetComponentsInChildren<MeshFilter>(true))
            {
                Add(mf.transform, mf.sharedMesh);
            }
            foreach (SkinnedMeshRenderer smr in live.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                Add(smr.transform, smr.sharedMesh);
            }
            return boxes;
        }

        /// <summary>Whether <paramref name="t"/> is one of <paramref name="tops"/> or under one.</summary>
        internal static bool UnderAny(Transform t, ICollection<Transform> tops)
        {
            if (tops == null || tops.Count == 0)
            {
                return false;
            }
            foreach (Transform top in tops)
            {
                if (top != null && (t == top || t.IsChildOf(top)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>A piece further than this from its part at rest is parked out of sight, not part of the part.</summary>
        private const float ParkedAway = 1.5f;

        private static Bounds MeshBounds(Transform live, Func<Transform, Vector3, Vector3> toSpace, Func<Transform, bool> include = null)
        {
            bool any = false;
            var b = new Bounds();
            foreach (MeshFilter mf in live.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null && (include == null || include(mf.transform)))
                {
                    Encapsulate(ref b, ref any, mf.sharedMesh.bounds, mf.transform, toSpace);
                }
            }
            foreach (SkinnedMeshRenderer smr in live.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh != null && (include == null || include(smr.transform)))
                {
                    Encapsulate(ref b, ref any, smr.sharedMesh.bounds, smr.transform, toSpace);
                }
            }
            return any ? b : new Bounds();
        }

        private static void Encapsulate(ref Bounds b, ref bool any, Bounds local, Transform t, Func<Transform, Vector3, Vector3> toSpace)
        {
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(c.x + ((i & 1) == 0 ? -e.x : e.x), c.y + ((i & 2) == 0 ? -e.y : e.y), c.z + ((i & 4) == 0 ? -e.z : e.z));
                Vector3 p = toSpace(t, corner);
                if (!any)
                {
                    b = new Bounds(p, Vector3.zero);
                    any = true;
                }
                else
                {
                    b.Encapsulate(p);
                }
            }
        }

        public void Dispose()
        {
            if (_holder != null)
            {
                UnityEngine.Object.DestroyImmediate(_holder);
                _holder = null;
            }
        }

        /// <summary>
        /// Detects animation events crossed between two manual animator updates by tracking each playing clip's time
        /// (events are disabled on the clone, so this is how we learn when the modder's sounds would fire).
        /// </summary>
        private sealed class EventTracker
        {
            private readonly Animator _anim;
            private readonly Dictionary<AnimationClip, float> _lastTime = new Dictionary<AnimationClip, float>();
            private readonly List<AnimatorClipInfo> _infos = new List<AnimatorClipInfo>();

            public EventTracker(Animator anim)
            {
                _anim = anim;
            }

            public void Collect(float t, List<SampledCue> output)
            {
                var seen = new HashSet<AnimationClip>();
                for (int layer = 0; layer < _anim.layerCount; layer++)
                {
                    AnimatorStateInfo state = _anim.GetCurrentAnimatorStateInfo(layer);
                    _anim.GetCurrentAnimatorClipInfo(layer, _infos);
                    Scan(state, _infos, t, output, seen);
                    if (_anim.IsInTransition(layer))
                    {
                        AnimatorStateInfo next = _anim.GetNextAnimatorStateInfo(layer);
                        _anim.GetNextAnimatorClipInfo(layer, _infos);
                        Scan(next, _infos, t, output, seen);
                    }
                }
                // Clips that stopped playing restart from zero next time.
                var stale = new List<AnimationClip>();
                foreach (AnimationClip c in _lastTime.Keys)
                {
                    if (!seen.Contains(c))
                    {
                        stale.Add(c);
                    }
                }
                foreach (AnimationClip c in stale)
                {
                    _lastTime.Remove(c);
                }
            }

            private void Scan(AnimatorStateInfo state, List<AnimatorClipInfo> infos, float t, List<SampledCue> output, HashSet<AnimationClip> seen)
            {
                foreach (AnimatorClipInfo info in infos)
                {
                    AnimationClip clip = info.clip;
                    if (clip == null || info.weight < 0.3f || seen.Contains(clip))
                    {
                        continue;
                    }
                    seen.Add(clip);
                    float length = Mathf.Max(clip.length, 1e-4f);
                    float now = state.normalizedTime * length;
                    bool hadPrevious = _lastTime.TryGetValue(clip, out float prev);
                    _lastTime[clip] = now;
                    AnimationEvent[] events = clip.events;
                    if (events == null || events.Length == 0)
                    {
                        continue;
                    }
                    // First seen: only what it crossed in this step. A clip already playing when the tracker started (the
                    // idle after the draw) replayed every event of its earlier loops, and the idle's sounds became shot or
                    // reload cues.
                    float stepped = Dt * Mathf.Max(1f, Mathf.Abs(state.speed * state.speedMultiplier));
                    float from = hadPrevious ? prev : Mathf.Max(-1e-4f, now - stepped);
                    foreach (AnimationEvent e in events)
                    {
                        if (!CueFunctions.Contains(e.functionName))
                        {
                            continue;
                        }
                        // Event times repeat every loop; check each loop crossing in (from, now]. A clip that does not loop plays its
                        // events once: still current past its end (crossfading out, held), its start-of-clip events were added again
                        // at each length, a shot's sound read as a slide cue a clip later (unswept-areas hunt, 2026-10-24).
                        float lastLoop = clip.isLooping ? now : 0f;
                        for (float loopStart = clip.isLooping ? Mathf.Floor(Mathf.Max(0f, from) / length) * length : 0f; loopStart <= lastLoop; loopStart += length)
                        {
                            float et = loopStart + e.time;
                            if (et > from && et <= now)
                            {
                                output.Add(new SampledCue
                                {
                                    Time = t - (now - et),
                                    Function = e.functionName,
                                    IntParameter = e.intParameter,
                                    FloatParameter = e.floatParameter,
                                    StringParameter = e.stringParameter
                                });
                            }
                        }
                    }
                }
            }
        }
    }
}
