using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// A splitscreen player's first-person view, as the game's own player has it: the soldier's weapon with its first-person arms in front
    /// of that player's camera, moving with its own animations (fire, reload, aim, sprint, drawing it), and the soldier's own body not drawn
    /// in its own view (others still see it). The game makes an AI soldier's guns third-person only (Actor.SpawnWeapon: CullFpsObjects), so
    /// the extra players looked through their soldier's body at the gun held at its hip (the user, 2026-11-14: "other players dont have first
    /// person view").
    /// <para>
    /// The copy is the weapon's own prefab set up as the game sets up its player's (first-person layer, the team's arms), its script switched
    /// off (it is a picture: the soldier's real gun shoots), far below the map with a camera of its own that draws only the first-person layer
    /// over the player's part of the window. Each player's copy is apart from the others' (100 m), so no camera sees another's.
    /// </para>
    /// </summary>
    internal sealed class SplitView
    {
        private const int FirstPersonLayer = 18; // (Weapon.LAYER_FIRST_PERSON)

        private readonly SplitPlayer _p;
        private readonly GameObject _root;
        private readonly Camera _cam;
        private Weapon _source, _copy;
        private Animator _anim;
        private readonly HashSet<int> _params = new HashSet<int>();
        private int _lastAmmo = -1;
        private float _lastShot = -1f;
        private ParticleSystem[] _flash = new ParticleSystem[0];
        private bool _lastReloading;

        private static readonly int Fire = Animator.StringToHash("fire"), Aim = Animator.StringToHash("aim"), WantsToAim = Animator.StringToHash("wants to aim"),
            Reload = Animator.StringToHash("reload"), Reloading = Animator.StringToHash("reloading"), Tuck = Animator.StringToHash("tuck"),
            LoadedAmmo = Animator.StringToHash("loaded ammo"), NoAmmo = Animator.StringToHash("no ammo"), NoAmmoBlend = Animator.StringToHash("no ammo blend"),
            Unholster = Animator.StringToHash("unholster"), Random = Animator.StringToHash("random");

        public SplitView(SplitPlayer p)
        {
            _p = p;
            _root = new GameObject("RavenfieldVR Split View " + (p.Index + 1));
            UnityEngine.Object.DontDestroyOnLoad(_root);
            _root.transform.position = new Vector3(0f, -12000f - 100f * p.Index, 0f);
            var camGo = new GameObject("RavenfieldVR Split Camera View " + (p.Index + 1));
            camGo.transform.SetParent(_root.transform, false);
            _cam = camGo.AddComponent<Camera>();
            _cam.clearFlags = CameraClearFlags.Depth;
            _cam.cullingMask = 1 << FirstPersonLayer;
            _cam.stereoTargetEye = StereoTargetEyeMask.None;
            _cam.nearClipPlane = 0.01f;
            _cam.farClipPlane = 5f;
            _cam.allowHDR = false;
            Camera game = FpsActorController.instance != null ? FpsActorController.instance.fpViewModelCamera : null;
            _cam.fieldOfView = game != null ? game.fieldOfView : 60f;
        }

        /// <summary>Once a frame after the player's camera is placed: the right weapon, its animations, the camera over the player's part.</summary>
        public void Tick()
        {
            Actor a = _p.Actor;
            Camera world = _p.Cam;
            Weapon w = a != null && !a.dead && !a.fallenOver && world != null ? a.activeWeapon : null; // (none while down or knocked over: the view is over the body)
            if (w != null && w.weaponEntry == null)
            {
                w = null; // (no prefab to copy: a vehicle's own gun)
            }
            if (w != _source)
            {
                Swap(w);
            }
            bool shown = _copy != null && world != null && world.enabled;
            if (_cam.enabled != shown)
            {
                _cam.enabled = shown;
            }
            if (!shown)
            {
                if (world != null && world.fieldOfView != 70f)
                {
                    world.fieldOfView = 70f; // (not left zoomed after dying or getting in while aiming)
                }
                return;
            }
            _cam.rect = world.rect;
            _cam.depth = world.depth + 0.5f;
            // Aiming down the sights zooms the view as the game's own player's does (the weapon's aim field of view), eased.
            float fov = 70f;
            if (_p.Aim && w != null && !w.reloading)
            {
                try
                {
                    fov = Mathf.Clamp(w.GetAimFov(), 5f, 70f);
                }
                catch (Exception)
                {
                    fov = 50f;
                }
            }
            world.fieldOfView = Mathf.MoveTowards(world.fieldOfView, fov, 200f * Time.unscaledDeltaTime);
            Drive(w);
        }

        private void Swap(Weapon w)
        {
            if (_copy != null)
            {
                UnityEngine.Object.Destroy(_copy.gameObject);
            }
            _copy = null;
            _anim = null;
            _params.Clear();
            _source = w;
            _lastAmmo = -1;
            _lastShot = w != null ? w.lastFiredTimestamp : -1f;
            _lastReloading = false;
            if (w == null)
            {
                return;
            }
            GameObject go = null;
            try
            {
                go = UnityEngine.Object.Instantiate(w.weaponEntry.prefab);
                go.name = "RavenfieldVR Split View Weapon " + (_p.Index + 1);
                Weapon c = go.GetComponent<Weapon>();
                if (c == null)
                {
                    UnityEngine.Object.Destroy(go);
                    return;
                }
                c.enabled = false; // (a picture of the gun: its script would look for a user, aim and shoot; its Start never runs)
                foreach (MonoBehaviour mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (mb != null && !(mb is Weapon))
                    {
                        mb.enabled = false; // (a mod weapon's own scripts too)
                    }
                }
                foreach (AudioSource s in go.GetComponentsInChildren<AudioSource>(true))
                {
                    s.enabled = false; // (the real gun is heard)
                }
                foreach (SkinnedMeshRenderer smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    smr.updateWhenOffscreen = true; // (its bounds from where the prefab was made: the Quicksilver's gun was culled away, only the arms drawn)
                }
                foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
                {
                    r.allowOcclusionWhenDynamic = false;
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off; // (as the game sets its own player's gun, Weapon.FindRenderers)
                    r.receiveShadows = false;
                }
                foreach (Rigidbody rb in go.GetComponentsInChildren<Rigidbody>(true))
                {
                    rb.isKinematic = true; // (a picture does not fall)
                    rb.detectCollisions = false;
                }
                foreach (Light l in go.GetComponentsInChildren<Light>(true))
                {
                    l.enabled = false; // (12 km under the map it lights nothing; the real gun's flash lights the world)
                }
                foreach (Collider col in go.GetComponentsInChildren<Collider>(true))
                {
                    col.enabled = false;
                }
                if (c.thirdPersonTransform != null)
                {
                    c.thirdPersonTransform.gameObject.SetActive(false);
                }
                c.SetupFirstPerson();
                Arms(c, _p.Actor);
                go.transform.SetParent(_cam.transform, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                go.SetActive(true);
                ShowParts(c); // (after it is active: its parts' activeInHierarchy counts)
                if (c.configuration != null && c.configuration.scopeAimObject != null)
                {
                    c.configuration.scopeAimObject.SetActive(false); // (as Weapon.OnWeaponAnimatorActivated: shown only aiming)
                }
                _copy = c;
                var flash = new List<ParticleSystem>();
                if (c.configuration != null && c.configuration.muzzles != null)
                {
                    foreach (Transform m in c.configuration.muzzles)
                    {
                        if (m != null)
                        {
                            flash.AddRange(m.GetComponentsInChildren<ParticleSystem>(true));
                        }
                    }
                }
                _flash = flash.ToArray();
                _anim = c.animator != null ? c.animator : go.GetComponentInChildren<Animator>();
                if (_anim != null)
                {
                    _anim.enabled = true;
                    _anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                    foreach (AnimatorControllerParameter par in _anim.parameters)
                    {
                        _params.Add(par.nameHash);
                    }
                    SetFloat(Random, UnityEngine.Random.Range(0f, 100f));
                    Trigger(Unholster);
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("split-view-" + w.name, $"Splitscreen: no first-person view of {w.name} ({e.GetType().Name}: {e.Message}).");
                if (go != null)
                {
                    UnityEngine.Object.Destroy(go); // (a copy that failed half way is not left standing in the world)
                }
                _copy = null;
                _anim = null;
            }
        }

        /// <summary>
        /// A gun whose parts its own script switches on (the Quicksilver: 1 of 5 parts drawn, only the arms, with its script off): when the copy
        /// draws nothing but its arms, every part of its first-person model is switched on.
        /// </summary>
        private static void ShowParts(Weapon c)
        {
            int shown = 0;
            var all = new List<Renderer>();
            foreach (Renderer r in c.GetComponentsInChildren<Renderer>(true))
            {
                if (r == c.arms || r is ParticleSystemRenderer || c.thirdPersonTransform != null && r.transform.IsChildOf(c.thirdPersonTransform))
                {
                    continue;
                }
                all.Add(r);
                if (r.enabled && r.gameObject.activeInHierarchy)
                {
                    shown++;
                }
            }
            if (shown > 0 || all.Count == 0)
            {
                return;
            }
            foreach (Renderer r in all)
            {
                r.enabled = true;
                for (Transform t = r.transform; t != null && t != c.transform; t = t.parent)
                {
                    if (!t.gameObject.activeSelf)
                    {
                        t.gameObject.SetActive(true);
                    }
                }
            }
        }

        /// <summary>The team's arms on the copy, as Weapon.Equip gives the game's own player's.</summary>
        private static void Arms(Weapon c, Actor a)
        {
            if (c.arms == null || !c.allowArmMeshReplacement || a == null || ActorManager.instance == null)
            {
                return;
            }
            int team = Mathf.Clamp(a.team, 0, ActorManager.instance.actorSkin.Length - 1);
            ActorSkin skin = a.overrideActorSkin != null ? a.overrideActorSkin : ActorManager.instance.actorSkin[team];
            if (skin != null && skin.armSkin != null && skin.armSkin.mesh != null)
            {
                ActorManager.ApplyOverrideMeshSkin(c.arms, skin.armSkin, a.team);
            }
            else if (WeaponManager.instance != null && WeaponManager.instance.defaultArms != null)
            {
                c.arms.sharedMesh = WeaponManager.instance.defaultArms;
                c.arms.materials = new[] { ColorScheme.GetActorMaterial(a.team) };
            }
            c.arms.updateWhenOffscreen = true;
        }

        /// <summary>The copy's animations from the soldier's real gun: a shot as its magazine goes down, a reload as it starts one.</summary>
        private void Drive(Weapon w)
        {
            if (_anim == null || w == null)
            {
                return;
            }
            int ammo = w.ammo;
            // A shot by the time the gun took it (its magazine does not go down with infinite rounds: -1).
            float shot = w.lastFiredTimestamp;
            bool fired = _lastShot >= 0f && shot > _lastShot || _lastShot < 0f && _lastAmmo >= 0 && ammo >= 0 && ammo < _lastAmmo;
            _lastShot = shot;
            if (fired)
            {
                Trigger(Fire);
                foreach (ParticleSystem ps in _flash)
                {
                    if (ps != null)
                    {
                        ps.Play(false); // (the muzzle flash, as the game's own player's gun shows it)
                    }
                }
            }
            _lastAmmo = ammo;
            bool reloading = w.reloading;
            if (reloading && !_lastReloading)
            {
                Trigger(Reload);
            }
            _lastReloading = reloading;
            SetBool(Reloading, reloading);
            GameObject scope = _copy.configuration != null ? _copy.configuration.scopeAimObject : null;
            if (scope != null && scope.activeSelf != (_p.Aim && !reloading))
            {
                scope.SetActive(_p.Aim && !reloading); // (the scope's own picture while aiming, as Weapon.Update shows it)
            }
            SetBool(Aim, _p.Aim && !reloading);
            SetBool(WantsToAim, _p.Aim);
            SetBool(Tuck, _p.Sprint && !reloading);
            SetInt(LoadedAmmo, ammo);
            bool any = w.HasLoadedAmmo() || w.HasSpareAmmo(); // (as the game: infinite rounds count)
            SetBool(NoAmmo, !any);
            SetFloat(NoAmmoBlend, any ? 0f : 1f);
        }

        private void Trigger(int h)
        {
            if (_params.Contains(h))
            {
                _anim.SetTrigger(h);
            }
        }

        private void SetBool(int h, bool v)
        {
            if (_params.Contains(h))
            {
                _anim.SetBool(h, v);
            }
        }

        private void SetInt(int h, int v)
        {
            if (_params.Contains(h))
            {
                _anim.SetInteger(h, v);
            }
        }

        private void SetFloat(int h, float v)
        {
            if (_params.Contains(h))
            {
                _anim.SetFloat(h, v);
            }
        }

        /// <summary>Check: the weapon shown in this player's first-person view, or null.</summary>
        internal string ShownWeapon
        {
            get
            {
                if (_copy == null || !_cam.enabled)
                {
                    return null;
                }
                int drawn = 0, total = 0;
                var names = new List<string>();
                foreach (Renderer r in _copy.GetComponentsInChildren<Renderer>(true))
                {
                    total++;
                    if (r.enabled && r.gameObject.activeInHierarchy)
                    {
                        drawn++;
                        if (names.Count < 4 && r != _copy.arms)
                        {
                            names.Add(r.name);
                        }
                    }
                }
                string gun = _source != null ? _source.name.Replace("(Clone)", "") : "(its gun gone)";
                return $"{gun} in first person, {drawn}/{total} part(s) drawn ({string.Join(", ", names.ToArray())})";
            }
        }

        public void Destroy()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
            }
            _copy = null;
        }

        // ---- the soldier's own body, not drawn in its own view ----------------------------------------------------------------

        private Renderer[] _body;
        private float _bodyFresh;
        private Weapon _bodyWeapon;

        private static readonly List<Renderer> Hidden = new List<Renderer>();
        private static bool _hooked;

        /// <summary>Hooked once: before a player's camera draws, its own soldier's renderers are switched off, after it on again.</summary>
        public static void HookBodies()
        {
            if (_hooked)
            {
                return;
            }
            _hooked = true;
            Camera.onPreCull += BeforeDraw;
            Camera.onPostRender += AfterDraw;
        }

        public static void UnhookBodies()
        {
            if (!_hooked)
            {
                return;
            }
            AfterDraw(null);
            Camera.onPreCull -= BeforeDraw;
            Camera.onPostRender -= AfterDraw;
            _hooked = false;
        }

        private static void BeforeDraw(Camera cam)
        {
            SplitPlayer p = SplitPlayers.OwnerOf(cam);
            if (p == null || p.Actor == null || p.Actor.dead || p.Actor.fallenOver)
            {
                return;
            }
            AfterDraw(null); // (one camera's at a time)
            SplitView v = p.View;
            if (v == null)
            {
                return;
            }
            if (v._body == null || Time.unscaledTime >= v._bodyFresh || v._bodyWeapon != p.Actor.activeWeapon)
            {
                // (gathered once a second, or when the gun in its hands changes, not for every camera every frame)
                v._body = p.Actor.GetComponentsInChildren<Renderer>(true);
                v._bodyFresh = Time.unscaledTime + 1f;
                v._bodyWeapon = p.Actor.activeWeapon;
            }
            foreach (Renderer r in v._body)
            {
                if (r != null && r.enabled)
                {
                    r.enabled = false;
                    Hidden.Add(r);
                }
            }
        }

        private static void AfterDraw(Camera cam)
        {
            for (int i = 0; i < Hidden.Count; i++)
            {
                if (Hidden[i] != null)
                {
                    Hidden[i].enabled = true;
                }
            }
            Hidden.Clear();
        }
    }
}
