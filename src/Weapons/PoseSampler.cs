using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Builds an inert copy of a weapon (instantiated under an inactive parent so no script ever wakes up,
    /// then stripped of scripts, audio, cameras, lights, physics and visible renderers) and steps its animator
    /// manually through idle, fire, reload and aim to record how every transform moves relative to the gun body.
    /// </summary>
    internal sealed class PoseSampler : IDisposable
    {
        private readonly Weapon _weapon;
        private readonly Transform[] _live;
        private readonly Dictionary<Transform, int> _index;
        private readonly int _gunIndex;

        private GameObject _holder;
        private Transform _cloneRoot;
        private Transform[] _clone;
        private Animator _anim;
        private HashSet<int> _params;

        private RigidPose[] _idle;        // root-relative
        private Vector3[] _idleLocalPos;
        private bool[] _idleVisible;
        public RigidPose[] RelIdle;        // gun-body relative
        public float[] FireMax;
        public Vector3[] FireVec;
        public float[] ReloadMax;
        public Vector3[] FirstDir;
        public bool HasReload;
        public bool HasAim;
        public RigidPose AimGunBody;

        public PoseSampler(Weapon weapon, Transform[] live, Dictionary<Transform, int> index, Transform gunBody)
        {
            _weapon = weapon;
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
                SetTrigger(Weapon.UNHOLSTER_PARAMETER_HASH);
                Step(2.5f);
                CaptureIdle();

                SetTrigger(Weapon.FIRE_PARAMETER_HASH);
                for (int k = 0; k < 18; k++)
                {
                    Step(Dt);
                    Accumulate(FireMax, FireVec, null);
                }
                ResetTrigger(Weapon.FIRE_PARAMETER_HASH);
                Step(1.5f);

                if (action != ActionType.NoAmmo && action != ActionType.Melee && action != ActionType.Throwable
                    && (Has(Weapon.RELOAD_PARAMETER_HASH) || Has(Weapon.RELOADING_PARAMETER_HASH)))
                {
                    SetLayerWeight(0f);
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
                    float duration = Mathf.Clamp(config.reloadTime, 0.5f, 6f) + 0.4f;
                    for (float t = 0f; t < duration; t += Dt)
                    {
                        Step(Dt);
                        Accumulate(ReloadMax, null, FirstDir);
                    }
                    ResetTrigger(Weapon.RELOAD_PARAMETER_HASH);
                    SetBool(Weapon.RELOADING_PARAMETER_HASH, false);
                    HasReload = true;
                    SetLayerWeight(1f);
                }

                if (Has(Weapon.AIM_PARAMETER_HASH))
                {
                    _anim.Rebind();
                    SetLayerWeight(1f);
                    SetInt(Weapon.LOADED_AMMO_PARAMETER_HASH, Mathf.Max(1, config.ammo));
                    Step(1.5f);
                    SetBool(Weapon.AIM_PARAMETER_HASH, true);
                    Step(1.2f);
                    AimGunBody = RootRelative(_clone[_gunIndex]);
                    HasAim = true;
                }
                return true;
            }
            catch (Exception e)
            {
                Log.Exception($"Animator sampling failed for {_weapon.name}", e);
                return false;
            }
        }

        private const float Dt = 1f / 30f;

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
                r.enabled = false;
            }

            _clone = _cloneRoot.GetComponentsInChildren<Transform>(true);
            if (_clone.Length != _live.Length)
            {
                Log.Warn($"Clone hierarchy of {_weapon.name} differs from the live weapon ({_clone.Length} vs {_live.Length}).");
                return false;
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

        private RigidPose RootRelative(Transform t)
        {
            Quaternion inv = Quaternion.Inverse(_cloneRoot.rotation);
            return new RigidPose(inv * (t.position - _cloneRoot.position), inv * t.rotation);
        }

        private void CaptureIdle()
        {
            for (int i = 0; i < _clone.Length; i++)
            {
                _idle[i] = RootRelative(_clone[i]);
                _idleLocalPos[i] = _clone[i].localPosition;
                _idleVisible[i] = _clone[i].gameObject.activeInHierarchy && _clone[i].lossyScale.sqrMagnitude > 1e-6f;
            }
            RigidPose gunInv = _idle[_gunIndex].Inverse();
            for (int i = 0; i < _clone.Length; i++)
            {
                RelIdle[i] = gunInv.Multiply(_idle[i]);
            }
        }

        private void Accumulate(float[] max, Vector3[] vecAtMax, Vector3[] firstDir)
        {
            RigidPose gunInv = RootRelative(_clone[_gunIndex]).Inverse();
            for (int i = 0; i < _clone.Length; i++)
            {
                Vector3 rel = gunInv.TransformPoint(RootRelative(_clone[i]).position);
                Vector3 d = rel - RelIdle[i].position;
                float m = d.magnitude;
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
            Transform root = _weapon.transform;
            Quaternion inv = Quaternion.Inverse(root.rotation);
            for (int i = 0; i < _live.Length; i++)
            {
                Transform t = _live[i];
                _idle[i] = new RigidPose(inv * (t.position - root.position), inv * t.rotation);
                _idleLocalPos[i] = t.localPosition;
                _idleVisible[i] = t.gameObject.activeInHierarchy;
            }
            RigidPose gunInv = _idle[_gunIndex].Inverse();
            for (int i = 0; i < _live.Length; i++)
            {
                RelIdle[i] = gunInv.Multiply(_idle[i]);
            }
        }

        public RigidPose Idle(Transform live) => _index.TryGetValue(live, out int i) ? _idle[i] : RigidPose.Identity;
        public Vector3 IdleLocalPosition(Transform live) => _index.TryGetValue(live, out int i) ? _idleLocalPos[i] : live.localPosition;
        public bool IdleVisible(Transform live) => _index.TryGetValue(live, out int i) && _idleVisible[i];

        /// <summary>Axis-aligned bounds (gun-body space) of every mesh under <paramref name="live"/> at idle.</summary>
        public Bounds GunSpaceBounds(Transform live, RigidPose gunIdle)
        {
            RigidPose gunInv = gunIdle.Inverse();
            return MeshBounds(live, (t, p) => gunInv.TransformPoint(Idle(t).TransformPoint(Vector3.Scale(p, t.lossyScale))));
        }

        /// <summary>Axis-aligned bounds in <paramref name="live"/>'s own (unscaled) space at idle.</summary>
        public Bounds LocalBounds(Transform live)
        {
            RigidPose selfInv = Idle(live).Inverse();
            return MeshBounds(live, (t, p) => selfInv.TransformPoint(Idle(t).TransformPoint(Vector3.Scale(p, t.lossyScale))));
        }

        private static Bounds MeshBounds(Transform live, Func<Transform, Vector3, Vector3> toSpace)
        {
            bool any = false;
            var b = new Bounds();
            foreach (MeshFilter mf in live.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null)
                {
                    Encapsulate(ref b, ref any, mf.sharedMesh.bounds, mf.transform, toSpace);
                }
            }
            foreach (SkinnedMeshRenderer smr in live.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh != null)
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
    }
}
