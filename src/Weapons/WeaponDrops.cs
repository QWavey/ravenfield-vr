using System;
using System.Collections.Generic;
using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>A weapon lying in the world that can be picked up.</summary>
    public sealed class DroppedWeapon
    {
        public GameObject Go;
        public Collider Collider;
        public WeaponManager.WeaponEntry Entry;
        /// <summary>Rounds loaded (-1 = default) and in reserve (-1 = default) for whoever picks it up.</summary>
        public int Ammo = -1;
        public int Spare = -1;
        public float DropTime;
    }

    /// <summary>
    /// Fallen soldiers drop their weapon as a physical third-person model. Gripping a dropped weapon equips it
    /// into its loadout slot; the weapon you had in that slot is dropped in its place (with its ammo), so you can
    /// swap back. Models are inert copies of the weapon prefab (no mod scripts run). Drops expire and are capped.
    /// </summary>
    public sealed class WeaponDrops : IDisposable
    {
        public static WeaponDrops Instance { get; private set; }

        private const float PickupReach = 0.11f;
        private readonly List<DroppedWeapon> _drops = new List<DroppedWeapon>();
        private readonly WeaponSystem _sys;
        private readonly bool[] _hovering = new bool[2];

        public WeaponDrops(WeaponSystem sys)
        {
            Instance = this;
            _sys = sys;
        }

        public int Count => _drops.Count;
        private readonly VRHand[] _handPair = new VRHand[2];

        // ---- spawning ---------------------------------------------------------------------------------

        /// <summary>Called just before a soldier dies (its weapons are destroyed right after).</summary>
        public void OnActorDying(Actor actor, DamageInfo info)
        {
            if (!VRConfig.EnemyWeaponDrops.Value || actor == null || !actor.aiControlled)
            {
                return;
            }
            Weapon w = actor.activeWeapon;
            if (w == null || w.weaponEntry == null || w.weaponEntry.prefab == null || w.IsMountedWeapon() || w.thirdPersonTransform == null)
            {
                return;
            }
            Transform tp = w.thirdPersonTransform;
            Vector3 velocity = actor.Velocity() + Vector3.ClampMagnitude(info.impactForce * 0.004f, 3f) + UnityEngine.Random.insideUnitSphere * 0.4f;
            int spare = w.configuration.spareAmmo >= 0
                ? Mathf.RoundToInt(w.configuration.spareAmmo * VRConfig.DroppedWeaponAmmo.Value)
                : -1;
            Spawn(w.weaponEntry, new RigidPose(tp.position, tp.rotation), tp.lossyScale, velocity, Mathf.Max(0, w.ammo), spare);
        }

        // Stripped third-person models, built once per weapon type: copying the whole weapon prefab (scripts,
        // animator, first-person rig) on every bot death cost over a millisecond each time.
        private static readonly Dictionary<WeaponManager.WeaponEntry, GameObject> Templates = new Dictionary<WeaponManager.WeaponEntry, GameObject>();
        private static GameObject _templateRoot;

        private static GameObject ModelFor(WeaponManager.WeaponEntry entry)
        {
            if (!Templates.TryGetValue(entry, out GameObject template) || template == null)
            {
                template = BuildWorldModel(entry);
                if (template == null)
                {
                    return null;
                }
                if (_templateRoot == null)
                {
                    _templateRoot = new GameObject("RavenfieldVR Drop Templates");
                    _templateRoot.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(_templateRoot);
                }
                template.transform.SetParent(_templateRoot.transform, false);
                Templates[entry] = template;
            }
            GameObject model = UnityEngine.Object.Instantiate(template);
            model.SetActive(true);
            return model;
        }

        public DroppedWeapon Spawn(WeaponManager.WeaponEntry entry, RigidPose pose, Vector3 scale, Vector3 velocity, int ammo, int spare)
        {
            GameObject model = ModelFor(entry);
            if (model == null)
            {
                return null;
            }
            var root = new GameObject("RavenfieldVR Dropped " + entry.name);
            int layer = PropPhysics.Layer;
            root.layer = layer >= 0 ? layer : 0; // bullets pass through a weapon on the ground
            model.transform.SetParent(root.transform, false);
            model.transform.localPosition = Vector3.zero;
            model.transform.localRotation = Quaternion.identity;
            model.transform.localScale = scale.sqrMagnitude > 1e-6f ? scale : Vector3.one;
            pose.ApplyTo(root.transform);

            Bounds local = LocalRendererBounds(root.transform);
            var box = root.AddComponent<BoxCollider>();
            box.center = local.center;
            box.size = Vector3.Max(local.size, new Vector3(0.05f, 0.05f, 0.05f));
            var body = root.AddComponent<Rigidbody>();
            body.mass = 3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.velocity = velocity;
            body.angularVelocity = UnityEngine.Random.insideUnitSphere * 2f;
            PropPhysics.IgnorePlayer(box);

            var drop = new DroppedWeapon { Go = root, Collider = box, Entry = entry, Ammo = ammo, Spare = spare, DropTime = Time.time };
            _drops.Add(drop);
            Cull();
            return drop;
        }

        /// <summary>The weapon's third-person model, copied from the prefab without running any of its scripts.</summary>
        private static GameObject BuildWorldModel(WeaponManager.WeaponEntry entry)
        {
            var holder = new GameObject("RavenfieldVR Drop Build");
            holder.SetActive(false);
            try
            {
                GameObject clone = UnityEngine.Object.Instantiate(entry.prefab, holder.transform, false);
                Weapon w = clone.GetComponent<Weapon>();
                Transform tp = w != null ? w.thirdPersonTransform : null;
                if (tp == null || !tp.IsChildOf(clone.transform))
                {
                    return null;
                }
                Vector3 tpEuler = w.thirdPersonRotation;
                Vector3 tpOffset = w.thirdPersonOffset;
                float tpScale = w.thirdPersonScale;

                MonoBehaviour[] scripts = clone.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = scripts.Length - 1; i >= 0; i--)
                {
                    UnityEngine.Object.DestroyImmediate(scripts[i]);
                }
                foreach (AudioSource a in tp.GetComponentsInChildren<AudioSource>(true))
                {
                    a.playOnAwake = false;
                    a.enabled = false;
                }
                foreach (Camera c in tp.GetComponentsInChildren<Camera>(true))
                {
                    c.enabled = false;
                }
                foreach (Light l in tp.GetComponentsInChildren<Light>(true))
                {
                    l.enabled = false;
                }
                foreach (ParticleSystem p in tp.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystem.MainModule main = p.main;
                    main.playOnAwake = false;
                    p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                foreach (Collider c in tp.GetComponentsInChildren<Collider>(true))
                {
                    c.enabled = false;
                }
                foreach (Rigidbody r in tp.GetComponentsInChildren<Rigidbody>(true))
                {
                    UnityEngine.Object.DestroyImmediate(r);
                }

                // Same local pose the game gives the third-person model under the weapon root.
                var pivot = new GameObject(entry.name);
                pivot.transform.SetParent(holder.transform, false);
                tp.SetParent(pivot.transform, false);
                tp.localPosition = tpOffset;
                tp.localEulerAngles = tpEuler;
                tp.localScale = Vector3.one * tpScale;
                tp.gameObject.SetActive(true);
                foreach (Renderer r in tp.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = true;
                    Util.MaterialFix.Apply(r); // the prefab's materials, without the mod's own runtime shader fixes
                }
                pivot.transform.SetParent(null, false);
                return pivot;
            }
            catch (Exception e)
            {
                Log.WarnOnce("dropbuild:" + entry.name, $"Could not build a dropped model for {entry.name}: {e.Message}");
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(holder);
            }
        }

        private static Bounds LocalRendererBounds(Transform root)
        {
            bool any = false;
            var b = new Bounds(Vector3.zero, Vector3.zero);
            Matrix4x4 toLocal = root.worldToLocalMatrix;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>())
            {
                if (r is ParticleSystemRenderer)
                {
                    continue;
                }
                Bounds wb = r.bounds;
                Vector3 c = wb.center, e = wb.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = toLocal.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
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
            return b;
        }

        // ---- picking up -------------------------------------------------------------------------------

        public void Update()
        {
            Cull();
            Actor actor = GameContext.PlayerActor;
            if (actor == null || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen || _drops.Count == 0)
            {
                return;
            }
            VRRig rig = _sys.Rig;
            _handPair[0] = rig.Dominant;
            _handPair[1] = rig.Off;
            foreach (VRHand hand in _handPair)
            {
                if (!hand.Tracked)
                {
                    continue;
                }
                bool canGrab = hand.Role == HandRole.Free || hand == rig.Dominant && (hand.Role == HandRole.Weapon || hand.Role == HandRole.Throwable);
                DroppedWeapon near = canGrab ? Nearest(hand.Palm.position) : null;
                int i = (int)hand.Side;
                if (near != null && !_hovering[i])
                {
                    hand.Haptic(0.01f, 160f, 0.2f); // "you can grab this"
                }
                _hovering[i] = near != null;
                if (near != null && hand.GrabPressed)
                {
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    PickUp(near, hand, actor);
                    return;
                }
            }
        }

        private DroppedWeapon Nearest(Vector3 palm)
        {
            DroppedWeapon best = null;
            float bestDist = PickupReach;
            foreach (DroppedWeapon d in _drops)
            {
                if (d.Go == null || d.Collider == null)
                {
                    continue;
                }
                float dist = Vector3.Distance(d.Collider.ClosestPoint(palm), palm);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = d;
                }
            }
            return best;
        }

        private void PickUp(DroppedWeapon drop, VRHand hand, Actor actor)
        {
            if (!actor.CanUnholsterWeapon())
            {
                hand.Haptic(0.08f, 40f, 0.4f);
                return;
            }
            int slot = SlotFor(drop.Entry, actor);
            Weapon current = actor.weapons[slot];
            if (current != null && current.weaponEntry != null && current.weaponEntry.prefab != null)
            {
                // Swap: what you had in that slot goes where the picked-up weapon was.
                var pose = new RigidPose(drop.Go.transform.position + Vector3.up * 0.1f, drop.Go.transform.rotation);
                Spawn(current.weaponEntry, pose, Vector3.one, Vector3.up * 0.5f, Mathf.Max(0, current.ammo), Mathf.Max(0, current.spareAmmo));
            }

            Weapon equipped = actor.EquipNewWeaponEntry(drop.Entry, slot, true);
            if (equipped != null)
            {
                if (drop.Ammo >= 0 && equipped.configuration.ammo > 0)
                {
                    equipped.ammo = Mathf.Clamp(drop.Ammo, 0, equipped.configuration.ammo);
                }
                if (drop.Spare >= 0 && equipped.configuration.spareAmmo >= 0)
                {
                    equipped.spareAmmo = drop.Spare;
                }
                equipped.OnAmmoChanged();
            }
            Remove(drop);
            _sys.Sounds.Play(MechSound.Holster, hand.Palm.position, 0.8f);
            hand.Haptic(0.05f, 100f, 0.7f);
            Log.Debug($"Picked up {drop.Entry.name} into slot {slot + 1}");
        }

        private static int SlotFor(WeaponManager.WeaponEntry entry, Actor actor)
        {
            switch (entry.slot)
            {
                case WeaponManager.WeaponSlot.Primary: return 0;
                case WeaponManager.WeaponSlot.Secondary: return 1;
                case WeaponManager.WeaponSlot.LargeGear: return 4;
                default:
                    if (actor.weapons[2] == null) return 2;
                    if (actor.weapons[3] == null) return 3;
                    return 2;
            }
        }

        // ---- lifetime ---------------------------------------------------------------------------------

        private void Cull()
        {
            float lifetime = VRConfig.DroppedWeaponLifetime.Value;
            int max = Mathf.Max(0, VRConfig.MaxDroppedWeapons.Value);
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                DroppedWeapon d = _drops[i];
                if (d.Go == null || Time.time - d.DropTime > lifetime || d.Go.transform.position.y < -500f)
                {
                    Remove(d);
                }
            }
            // Oldest first when over the cap.
            while (_drops.Count > max)
            {
                Remove(_drops[0]);
            }
        }

        private void Remove(DroppedWeapon d)
        {
            _drops.Remove(d);
            if (d.Go != null)
            {
                UnityEngine.Object.Destroy(d.Go);
            }
        }

        public void Clear()
        {
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                Remove(_drops[i]);
            }
        }

        public void Dispose()
        {
            Clear();
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }

    [HarmonyPatch(typeof(Actor), "Die", typeof(DamageInfo), typeof(bool))]
    internal static class ActorDiePatch
    {
        private static void Prefix(Actor __instance, DamageInfo info, bool isSilentKill)
        {
            if (!VRManager.Active || isSilentKill || __instance.dead || WeaponDrops.Instance == null)
            {
                return;
            }
            Log.Guard("weapon drop", () => WeaponDrops.Instance.OnActorDying(__instance, info));
        }
    }
}
