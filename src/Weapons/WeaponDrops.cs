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
        private Rigidbody _body;
        /// <summary>Its rigidbody (looked up once).</summary>
        public Rigidbody Body => _body != null ? _body : Go != null ? _body = Go.GetComponent<Rigidbody>() : null;
        /// <summary>The model's own shape (convex pieces: receiver, barrel, stock...), so it lies flat and is grabbed where it is.</summary>
        public List<Collider> Colliders;
        public WeaponManager.WeaponEntry Entry;
        /// <summary>Rounds loaded (-1 = default) and in reserve (-1 = default) for whoever picks it up.</summary>
        public int Ammo = -1;
        public int Spare = -1;
        /// <summary>Rounds loaded in each under-barrel weapon (a launcher's grenade), by index; null = their default.</summary>
        public int[] SubAmmo;
        /// <summary>The key the weapon's rig is analysed under (magazines name it), when known.</summary>
        public string Key;
        public float DropTime;
        /// <summary>
        /// The player's own weapon, let go of away from the body: the loadout slot it came from (-1 for a bot's weapon). It
        /// goes back to that holster after a while or when you walk away from it.
        /// </summary>
        public int OwnerSlot = -1;
        public float ReturnAt;
        /// <summary>Meshes baked for this copy (skinned parts), destroyed with it.</summary>
        public List<Mesh> OwnedMeshes;
        /// <summary>Body colliders (ragdolls, soldiers' hitboxes) this weapon no longer collides with.</summary>
        public readonly HashSet<Collider> IgnoredBodies = new HashSet<Collider>();
        public float NextBodyCheck;
        /// <summary>When <see cref="IgnoredBodies"/> was last forgotten (bodies switched off and on collide again: redone).</summary>
        public float BodiesForgottenAt;
        /// <summary>The <see cref="PropPhysics.PlayerGeneration"/> the player's colliders were last ignored for.</summary>
        public int PlayerGeneration = -1;
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

        /// <summary>Every weapon lying in the world now.</summary>
        internal IReadOnlyList<DroppedWeapon> All => _drops;
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
            // The copy holds the model at the game's own offset, turn and scale under the weapon's pivot: the pivot goes
            // where the soldier's hand holds it. At the model's own pose and scale both counted twice (a half-size weapon
            // dropped at a quarter size, off to the side).
            Transform hold = tp.parent != null ? tp.parent : tp;
            Vector3 holdScale = tp.parent != null ? tp.parent.lossyScale : Vector3.one;
            Vector3 velocity = actor.Velocity() + Vector3.ClampMagnitude(info.impactForce * 0.004f, 3f) + UnityEngine.Random.insideUnitSphere * 0.4f;
            int spare = w.configuration.spareAmmo >= 0
                ? Mathf.RoundToInt(w.configuration.spareAmmo * VRConfig.DroppedWeaponAmmo.Value)
                : -1;
            Spawn(w.weaponEntry, new RigidPose(hold.position, hold.rotation), holdScale, velocity, Mathf.Max(0, w.ammo), spare);
        }

        // Stripped third-person models, built once per weapon type: copying the whole weapon prefab (scripts,
        // animator, first-person rig) on every bot death cost over a millisecond each time.
        private static readonly Dictionary<WeaponManager.WeaponEntry, GameObject> Templates = new Dictionary<WeaponManager.WeaponEntry, GameObject>();
        private static GameObject _templateRoot;

        /// <summary>Weapons that have no third-person model to copy: not tried again (the copy instantiates the whole prefab).</summary>
        private static readonly HashSet<WeaponManager.WeaponEntry> NoModel = new HashSet<WeaponManager.WeaponEntry>();

        internal static GameObject ModelFor(WeaponManager.WeaponEntry entry)
        {
            if (entry == null || NoModel.Contains(entry))
            {
                return null;
            }
            if (!Templates.TryGetValue(entry, out GameObject template) || template == null)
            {
                template = BuildWorldModel(entry);
                if (template == null)
                {
                    NoModel.Add(entry);
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

        /// <summary>The weapon dropped last (self-test).</summary>
        internal DroppedWeapon Newest => _drops.Count > 0 ? _drops[_drops.Count - 1] : null;

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

            List<Collider> boxes = ShapeFit.AddColliders(root, FitModel(entry, root.transform, model.transform.localScale));
            var body = root.AddComponent<Rigidbody>();
            body.mass = 3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.velocity = velocity;
            body.angularVelocity = UnityEngine.Random.insideUnitSphere * 2f;
            SetUpBody(body, boxes);
            PropPhysics.IgnorePlayer(boxes);

            var drop = new DroppedWeapon { Go = root, Colliders = boxes, Entry = entry, Ammo = ammo, Spare = spare, DropTime = Time.time };
            // The dying soldier's ragdoll and hitboxes are around the weapon right now: ignored before the first physics
            // step, or that step pushes the weapon out of them and it skids or floats away.
            PropPhysics.IgnoreBodiesNear(boxes, root.transform.position, 1.5f, drop.IgnoredBodies);
            _drops.Add(drop);
            Cull();
            return drop;
        }

        /// <summary>The weapon's third-person model, copied from the prefab without running any of its scripts.</summary>
        private static GameObject BuildWorldModel(WeaponManager.WeaponEntry entry)
        {
            // Only the third-person model is needed: copy that part of the prefab, not the whole weapon (first-person
            // rig, arms, animator, sounds), which cost tens of milliseconds the first time a bot dropped a weapon type.
            GameObject fast = BuildWorldModelFromSubtree(entry);
            return fast != null ? fast : BuildWorldModelWhole(entry);
        }

        /// <summary>Self-test: how long copying the third-person model takes, from its part of the prefab or from the whole prefab.</summary>
        internal static float TimeModelCopy(WeaponManager.WeaponEntry entry, bool partOnly)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            GameObject copy = partOnly ? BuildWorldModelFromSubtree(entry) : BuildWorldModelWhole(entry);
            float ms = (float)watch.Elapsed.TotalMilliseconds;
            if (copy == null)
            {
                return -1f;
            }
            UnityEngine.Object.Destroy(copy);
            return ms;
        }

        private static GameObject BuildWorldModelWhole(WeaponManager.WeaponEntry entry)
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

        /// <summary>
        /// The third-person model copied on its own from the prefab asset (no instance of the whole weapon). Null when it
        /// cannot stand alone (its skinned parts use bones outside it): the whole-prefab copy is used then.
        /// </summary>
        private static GameObject BuildWorldModelFromSubtree(WeaponManager.WeaponEntry entry)
        {
            Weapon w = entry.prefab.GetComponent<Weapon>();
            Transform tpAsset = w != null ? w.thirdPersonTransform : null;
            if (tpAsset == null || !tpAsset.IsChildOf(entry.prefab.transform) || tpAsset == entry.prefab.transform)
            {
                return null;
            }
            foreach (SkinnedMeshRenderer smr in tpAsset.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.rootBone != null && !smr.rootBone.IsChildOf(tpAsset))
                {
                    return null;
                }
                foreach (Transform bone in smr.bones)
                {
                    if (bone != null && !bone.IsChildOf(tpAsset))
                    {
                        return null;
                    }
                }
            }
            var holder = new GameObject("RavenfieldVR Drop Build");
            holder.SetActive(false); // nothing in the copy wakes up
            try
            {
                GameObject copy = UnityEngine.Object.Instantiate(tpAsset.gameObject, holder.transform, false);
                Transform tp = copy.transform;
                MonoBehaviour[] scripts = copy.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = scripts.Length - 1; i >= 0; i--)
                {
                    UnityEngine.Object.DestroyImmediate(scripts[i]);
                }
                foreach (AudioSource a in copy.GetComponentsInChildren<AudioSource>(true))
                {
                    a.playOnAwake = false;
                    a.enabled = false;
                }
                foreach (Camera c in copy.GetComponentsInChildren<Camera>(true))
                {
                    c.enabled = false;
                }
                foreach (Light l in copy.GetComponentsInChildren<Light>(true))
                {
                    l.enabled = false;
                }
                foreach (ParticleSystem ps in copy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystem.MainModule main = ps.main;
                    main.playOnAwake = false;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                foreach (Collider c in copy.GetComponentsInChildren<Collider>(true))
                {
                    c.enabled = false;
                }
                foreach (Rigidbody r in copy.GetComponentsInChildren<Rigidbody>(true))
                {
                    UnityEngine.Object.DestroyImmediate(r);
                }
                // Same local pose the game gives the third-person model under the weapon root.
                var pivot = new GameObject(entry.name);
                pivot.transform.SetParent(holder.transform, false);
                tp.SetParent(pivot.transform, false);
                tp.localPosition = w.thirdPersonOffset;
                tp.localEulerAngles = w.thirdPersonRotation;
                tp.localScale = Vector3.one * w.thirdPersonScale;
                copy.SetActive(true);
                foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = true;
                    Util.MaterialFix.Apply(r);
                }
                pivot.transform.SetParent(null, false);
                return pivot;
            }
            catch (Exception e)
            {
                Log.WarnOnce("dropbuild-fast:" + entry.name, $"Copying only the third-person model of {entry.name} failed ({e.Message}); copying the whole weapon.");
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(holder);
            }
        }

        /// <summary>
        /// The model's shape as boxes in the drop's own space (fitted once per weapon and scale); a single box around its
        /// renderers when nothing could be measured.
        /// </summary>
        /// <summary>
        /// Menu warm-up: the world model a fallen soldier drops and its physics shape (hulls cooked), built ahead so a bot
        /// dying with this weapon in a match costs no model copy, no mesh reading and no cooking.
        /// </summary>
        /// <summary>Self-test: what a drop of <paramref name="entry"/> costs once prepared (model copy, cached shape, colliders), in ms.</summary>
        internal static float TimePreparedDrop(WeaponManager.WeaponEntry entry)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            GameObject model = ModelFor(entry);
            if (model == null)
            {
                return -1f;
            }
            var root = new GameObject("RavenfieldVR Drop Timing");
            model.transform.SetParent(root.transform, false);
            ShapeFit.AddColliders(root, FitModel(entry, root.transform, Vector3.one));
            float ms = (float)watch.Elapsed.TotalMilliseconds;
            UnityEngine.Object.Destroy(root);
            return ms;
        }

        internal static void Prepare(WeaponManager.WeaponEntry entry)
        {
            GameObject model = ModelFor(entry);
            if (model == null)
            {
                return;
            }
            try
            {
                var root = new GameObject("RavenfieldVR Drop Warm-up");
                model.transform.SetParent(root.transform, false);
                ShapeFit.Bake(FitModel(entry, root.transform, Vector3.one));
                MeasureRenderedShape(entry, root.transform);
                UnityEngine.Object.Destroy(root);
            }
            catch (Exception e)
            {
                UnityEngine.Object.Destroy(model);
                Log.WarnOnce("dropwarm:" + entry.name, $"Preparing the dropped model of {entry.name} failed: {e.Message}");
            }
        }

        /// <summary>Per weapon type whose third-person meshes cannot be read: its shape fitted to the rendered model (scale 1).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<WeaponManager.WeaponEntry, ShapeFit.Piece[]> RenderedShapes =
            new System.Collections.Concurrent.ConcurrentDictionary<WeaponManager.WeaponEntry, ShapeFit.Piece[]>();
        private static readonly Dictionary<string, ShapeFit.Piece[]> ScaledShapes = new Dictionary<string, ShapeFit.Piece[]>();

        /// <summary>
        /// A dropped model whose meshes cannot be read only gets boxes from their bounds: during the warm-up its drawn model
        /// is measured (rendered as depth, the hull fitted on a worker thread) and its drops use that shape from then on.
        /// Only the fitted pieces are kept.
        /// </summary>
        private static void MeasureRenderedShape(WeaponManager.WeaponEntry entry, Transform root)
        {
            if (RenderedShapes.ContainsKey(entry))
            {
                return;
            }
            var renderers = new List<Renderer>();
            bool unreadable = false;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                {
                    renderers.Add(r);
                    unreadable |= !ShapeFit.Readable(r);
                }
            }
            if (!unreadable || renderers.Count == 0)
            {
                return;
            }
            GunVolume v = GunVolume.Measure("tp:" + entry.name, renderers, root, 5, 0.04f, 12);
            if (v == null)
            {
                return;
            }
            void Keep(GunVolume done)
            {
                if (done.HullPieces != null && done.HullPieces.Length > 0)
                {
                    RenderedShapes[entry] = done.HullPieces;
                }
            }
            if (v.Ready)
            {
                Keep(v);
            }
            else
            {
                v.OnReady = Keep;
            }
        }

        /// <summary>The rendered-model shape of this type at this scale (the pieces were measured at scale 1).</summary>
        private static ShapeFit.Piece[] ScaledRenderedShape(WeaponManager.WeaponEntry entry, Vector3 scale)
        {
            if (!RenderedShapes.TryGetValue(entry, out ShapeFit.Piece[] pieces))
            {
                return null;
            }
            string key = entry.name + "@" + scale.ToString("F3");
            if (ScaledShapes.TryGetValue(key, out ShapeFit.Piece[] scaled))
            {
                return scaled;
            }
            scaled = new ShapeFit.Piece[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                var pts = new Vector3[pieces[i].Points.Length];
                var b = new Bounds();
                for (int j = 0; j < pts.Length; j++)
                {
                    pts[j] = Vector3.Scale(pieces[i].Points[j], scale);
                    if (j == 0)
                    {
                        b = new Bounds(pts[0], Vector3.zero);
                    }
                    else
                    {
                        b.Encapsulate(pts[j]);
                    }
                }
                scaled[i] = new ShapeFit.Piece { Points = pts, Bounds = b };
            }
            ShapeFit.Bake(scaled);
            ScaledShapes[key] = scaled;
            return scaled;
        }

        /// <summary>Self-test: drops of this type use the shape fitted to the rendered model.</summary>
        internal static bool UsesRenderedShape(WeaponManager.WeaponEntry entry) => RenderedShapes.ContainsKey(entry);

        private static ShapeFit.Piece[] FitModel(WeaponManager.WeaponEntry entry, Transform root, Vector3 scale)
        {
            ShapeFit.Piece[] rendered = ScaledRenderedShape(entry, scale);
            if (rendered != null)
            {
                return rendered;
            }
            var renderers = new List<Renderer>(root.GetComponentsInChildren<Renderer>());
            string key = "drop:" + entry.name + "@" + scale.x.ToString("F2");
            ShapeFit.Piece[] boxes = ShapeFit.Fit(key, renderers, root.worldToLocalMatrix, 0.04f, 12);
            if (boxes.Length > 0)
            {
                return boxes;
            }
            Bounds b = LocalRendererBounds(root, renderers);
            return new[] { ShapeFit.BoxPiece(b.center, Vector3.Max(b.size, new Vector3(0.05f, 0.05f, 0.05f))) };
        }

        private static Bounds LocalRendererBounds(Transform root, List<Renderer> renderers)
        {
            bool any = false;
            var b = new Bounds(Vector3.zero, Vector3.zero);
            Matrix4x4 toLocal = root.worldToLocalMatrix;
            foreach (Renderer r in renderers)
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
            PropPhysics.TrackPlayer();
            IgnoreBodies();
            Actor actor = GameContext.PlayerActor;
            if (actor == null || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen || _drops.Count == 0)
            {
                return;
            }
            ReturnOwnDrops(actor);
            if (_drops.Count == 0)
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
                if (near != null && (hand.GrabPressed || hand.GrabReaching))
                {
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    PickUp(near, hand, actor);
                    return;
                }
            }
        }


        /// <summary>
        /// A weapon on the ground does not collide with bodies: one spawned in a dying soldier's hand was shoved out of
        /// the ragdoll (or carried along by it) and soldiers walking past kicked lying weapons away, so they drifted off.
        /// Bodies near a drop are found every frame for its first half second, then four times a second.
        /// </summary>
        private void IgnoreBodies()
        {
            float now = Time.time;
            foreach (DroppedWeapon d in _drops)
            {
                if (d.Go == null || d.Colliders == null || now < d.NextBodyCheck)
                {
                    continue;
                }
                d.NextBodyCheck = now - d.DropTime < 0.5f ? now : now + 0.25f;
                // A ragdoll or a soldier whose colliders were switched off and on collides again (Unity forgets ignored
                // pairs then): every two seconds the bodies around are ignored afresh.
                if (now - d.BodiesForgottenAt > 2f)
                {
                    d.BodiesForgottenAt = now;
                    d.IgnoredBodies.Clear();
                }
                PropPhysics.IgnoreBodiesNear(d.Colliders, d.Go.transform.position, 1.5f, d.IgnoredBodies);
                if (d.PlayerGeneration != PropPhysics.PlayerGeneration)
                {
                    d.PlayerGeneration = PropPhysics.PlayerGeneration;
                    PropPhysics.IgnorePlayer(d.Colliders);
                }
            }
        }

        private static PhysicMaterial _dropMaterial;

        /// <summary>Weapons lie where they fall: grippy, barely bouncing.</summary>
        private static void SetUpBody(Rigidbody body, List<Collider> colliders)
        {
            if (_dropMaterial == null)
            {
                _dropMaterial = new PhysicMaterial("RavenfieldVR Dropped Weapon")
                {
                    staticFriction = 0.9f,
                    dynamicFriction = 0.7f,
                    bounciness = 0.05f,
                    frictionCombine = PhysicMaterialCombine.Maximum,
                    bounceCombine = PhysicMaterialCombine.Minimum
                };
            }
            foreach (Collider c in colliders)
            {
                if (c != null)
                {
                    c.sharedMaterial = _dropMaterial;
                }
            }
            body.angularDrag = 0.5f;
        }

        /// <summary>What each under-barrel weapon has loaded (null without any).</summary>
        private static int[] SubAmmoOf(Weapon w)
        {
            if (w == null || w.alternativeWeapons == null || w.alternativeWeapons.Count == 0)
            {
                return null;
            }
            var ammo = new int[w.alternativeWeapons.Count];
            for (int i = 0; i < ammo.Length; i++)
            {
                Weapon sub = w.alternativeWeapons[i];
                ammo[i] = sub != null ? Mathf.Max(0, sub.ammo) : -1;
            }
            return ammo;
        }

        /// <summary>
        /// The rounds a dropped weapon had, back in the weapon made from it: loaded (a chambered round on top of a full
        /// magazine included: clamped to the magazine, a 30+1 rifle came back 29+1 after every drop), the reserve, and
        /// what each under-barrel weapon had loaded (a launcher came back with its default grenade).
        /// </summary>
        private static void RestoreAmmo(Weapon w, DroppedWeapon d)
        {
            if (d.Ammo >= 0 && w.configuration.ammo > 0)
            {
                int most = w.configuration.ammo + (VRConfig.ChamberPlusOne.Value ? 1 : 0);
                w.ammo = Mathf.Clamp(d.Ammo, 0, most);
            }
            if (d.Spare >= 0 && w.configuration.spareAmmo >= 0)
            {
                w.spareAmmo = d.Spare;
            }
            if (d.SubAmmo != null && w.alternativeWeapons != null)
            {
                for (int i = 0; i < d.SubAmmo.Length && i < w.alternativeWeapons.Count; i++)
                {
                    Weapon sub = w.alternativeWeapons[i];
                    if (sub != null && d.SubAmmo[i] >= 0 && sub.configuration.ammo > 0)
                    {
                        sub.ammo = Mathf.Clamp(d.SubAmmo[i], 0, sub.configuration.ammo + (VRConfig.ChamberPlusOne.Value ? 1 : 0));
                        sub.OnAmmoChanged();
                    }
                }
            }
            w.OnAmmoChanged();
            // Its rig state (chamber, magazine) is taken from the game's count again.
            WeaponSystem.Instance?.StateFor(w, create: false)?.AdoptWeaponAmmo();
        }

        /// <summary>
        /// Rounds of a magazine whose weapon was let fall: into that drop's reserve (they come back with it). The player's
        /// own drop of that kind, the newest first.
        /// </summary>
        public void ReturnRoundsToOwnDrop(string key, int rounds)
        {
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                DroppedWeapon d = _drops[i];
                if (d.OwnerSlot < 0 || d.Entry == null || d.Spare < 0)
                {
                    continue;
                }
                bool same = d.Key != null ? d.Key == key : key != null && key.StartsWith(d.Entry.name + "#", System.StringComparison.Ordinal);
                if (!same)
                {
                    continue;
                }
                int cap = d.Entry.prefab != null && d.Entry.prefab.GetComponent<Weapon>() is Weapon proto && proto.configuration.spareAmmo > 0
                    ? proto.configuration.spareAmmo
                    : int.MaxValue;
                d.Spare = Mathf.Max(d.Spare, (int)System.Math.Min(cap, (long)d.Spare + rounds));
                return;
            }
        }

        private DroppedWeapon Nearest(Vector3 palm)
        {
            DroppedWeapon best = null;
            float bestDist = PickupReach;
            foreach (DroppedWeapon d in _drops)
            {
                if (d.Go == null || d.Colliders == null)
                {
                    continue;
                }
                foreach (Collider c in d.Colliders)
                {
                    float dist = c != null ? Vector3.Distance(c.ClosestPoint(palm), palm) : float.MaxValue;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = d;
                    }
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
            int slot = drop.OwnerSlot >= 0 && drop.OwnerSlot < actor.weapons.Length ? drop.OwnerSlot : SlotFor(drop.Entry, actor);
            Weapon current = actor.weapons[slot];
            if (current != null && current.weaponEntry != null && current.weaponEntry.prefab != null)
            {
                // Swap: what you had in that slot goes where the picked-up weapon was. Made first: a weapon without a
                // model to copy cannot be put down, so nothing is picked up (it was replaced and gone, rounds and all).
                var pose = new RigidPose(drop.Go.transform.position + Vector3.up * 0.1f, drop.Go.transform.rotation);
                DroppedWeapon swapped = Spawn(current.weaponEntry, pose, Vector3.one, Vector3.up * 0.5f, Mathf.Max(0, current.ammo), Mathf.Max(0, current.spareAmmo));
                if (swapped == null)
                {
                    hand.Haptic(0.08f, 40f, 0.4f);
                    return;
                }
                swapped.SubAmmo = SubAmmoOf(current);
                swapped.Key = WeaponAnalyzer.KeyOf(current);
            }
            // Into the hand that picked it up: taken with the free hand, it went into the other one.
            _sys.TakeIntoHand(hand);

            Weapon equipped = actor.EquipNewWeaponEntry(drop.Entry, slot, true);
            if (equipped != null)
            {
                RestoreAmmo(equipped, drop);
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
                if (d.Go == null)
                {
                    Remove(d);
                    continue;
                }
                bool expired = Time.time - d.DropTime > lifetime || d.Go.transform.position.y < -500f;
                if (expired && d.OwnerSlot >= 0)
                {
                    // Your own weapon is the only copy of it (its slot was emptied when it left the hand): it goes back to
                    // its holster, never away. Deleted after 90 s (in a vehicle, where it is not returned) or after falling
                    // out of the world, it was gone from the loadout for good.
                    d.ReturnAt = 0f;
                    continue;
                }
                if (expired)
                {
                    Remove(d);
                }
            }
            // Oldest first when over the cap; your own weapon waiting to go back to its holster is never one of them.
            int count = 0;
            foreach (DroppedWeapon d in _drops)
            {
                count += d.OwnerSlot < 0 ? 1 : 0;
            }
            for (int i = 0; i < _drops.Count && count > max; i++)
            {
                if (_drops[i].OwnerSlot < 0)
                {
                    Remove(_drops[i]);
                    i--;
                    count--;
                }
            }
        }

        private void Remove(DroppedWeapon d)
        {
            _drops.Remove(d);
            if (d.Go != null)
            {
                UnityEngine.Object.Destroy(d.Go);
            }
            if (d.OwnedMeshes != null)
            {
                foreach (Mesh m in d.OwnedMeshes)
                {
                    UnityEngine.Object.Destroy(m);
                }
                d.OwnedMeshes = null;
            }
        }

        // ---- your own weapon, let go of ----------------------------------------------------------------

        /// <summary>
        /// The weapon in your hand falls where you let go of it: a copy of exactly what you held (the first-person model,
        /// its magazine if one is in), at the pose you held it, with your hand's velocity and the gun's own shape for
        /// physics. Its rounds stay with it. Null when there was nothing to copy.
        /// </summary>
        public DroppedWeapon DropHeld(HeldWeapon held, int slot, Vector3 velocity, Vector3 angularVelocity)
        {
            WeaponRig rig = held.Rig;
            WeaponRigData d = rig.Data;
            Weapon w = held.Weapon;
            if (w == null || w.weaponEntry == null || rig.GunBody == null)
            {
                return null;
            }
            Quaternion frame = OpenVRMath.SafeLookRotation(d.AimAxis, d.Up);
            var root = new GameObject("RavenfieldVR Dropped " + w.weaponEntry.name);
            new RigidPose(rig.ToWorld(d.GripPoint), rig.GunBody.rotation * frame).ApplyTo(root.transform);
            var baked = new List<Mesh>();
            bool magIn = held.Ammo == null || !held.Ammo.UsesDetachableMag || held.Ammo.MagInserted;
            if (!CopyHeldModel(rig, root.transform, magIn, baked))
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }
            int layer = PropPhysics.Layer;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer >= 0 ? layer : 0;
            }
            // The held gun's own physics shape, fitted in exactly this frame (the stand-in's current one: when the
            // rendered-model fit replaced the meshes' it is the better one), and the magazine when it shows in the gun.
            GunCollider held_ = GunCollider.Current;
            ShapeFit.Piece[] pieces = held_ != null && held_.Holds(rig) && held_.Pieces != null ? held_.Pieces : GunCollider.Fit(rig, frame, d.GripPoint);
            if (magIn && rig.MagazineParts.Length > 0)
            {
                var magRenderers = new List<Renderer>();
                foreach (Transform part in rig.MagazineParts)
                {
                    if (part != null)
                    {
                        foreach (Renderer r in part.GetComponentsInChildren<Renderer>(false))
                        {
                            if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                            {
                                magRenderers.Add(r);
                            }
                        }
                    }
                }
                Matrix4x4 frameFromWorld = Matrix4x4.TRS(rig.ToWorld(d.GripPoint), rig.GunBody.rotation * frame, Vector3.one).inverse;
                ShapeFit.Piece[] mag = ShapeFit.Fit("heldmag:" + d.Key, magRenderers, frameFromWorld, 0.015f, 8);
                if (mag.Length > 0)
                {
                    var all = new ShapeFit.Piece[pieces.Length + mag.Length];
                    pieces.CopyTo(all, 0);
                    mag.CopyTo(all, pieces.Length);
                    pieces = all;
                }
            }
            List<Collider> colliders = ShapeFit.AddColliders(root, pieces);
            var body = root.AddComponent<Rigidbody>();
            body.mass = 3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.velocity = velocity;
            body.angularVelocity = angularVelocity;
            SetUpBody(body, colliders);
            PropPhysics.IgnorePlayer(colliders);
            var drop = new DroppedWeapon
            {
                Go = root,
                Colliders = colliders,
                Entry = w.weaponEntry,
                Ammo = Mathf.Max(0, w.ammo),
                Spare = Mathf.Max(0, w.spareAmmo),
                SubAmmo = SubAmmoOf(w),
                Key = held.Rig.Data.Key,
                DropTime = Time.time,
                OwnerSlot = slot,
                ReturnAt = Time.time + Mathf.Max(1f, VRConfig.WeaponReturnSeconds.Value),
                OwnedMeshes = baked
            };
            PropPhysics.IgnoreBodiesNear(colliders, root.transform.position, 1.5f, drop.IgnoredBodies);
            _drops.Add(drop);
            return drop;
        }

        /// <summary>Copies the weapon's visible meshes (no arms, nothing the mod added except its moving bolt piece) under <paramref name="root"/>.</summary>
        private static bool CopyHeldModel(WeaponRig rig, Transform root, bool magazineIn, List<Mesh> baked)
        {
            var skip = new HashSet<Renderer>(rig.ArmRenderers);
            if (!magazineIn)
            {
                foreach (Transform part in rig.MagazineParts)
                {
                    if (part != null)
                    {
                        foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                        {
                            skip.Add(r);
                        }
                    }
                }
            }
            bool any = false;
            foreach (Renderer r in rig.Weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || skip.Contains(r) || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
                {
                    continue;
                }
                if (GunCollider.AddedByMod(r.transform, rig.Weapon.transform) && !r.name.StartsWith("RavenfieldVR Bolt", StringComparison.Ordinal))
                {
                    continue;
                }
                Mesh mesh;
                Vector3 scale;
                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null)
                    {
                        continue;
                    }
                    mesh = new Mesh { name = smr.sharedMesh.name + " (dropped)" };
                    smr.BakeMesh(mesh);
                    if (mesh.vertexCount == 0)
                    {
                        UnityEngine.Object.Destroy(mesh);
                        continue;
                    }
                    baked.Add(mesh);
                    scale = Vector3.one; // baked with the renderer's scale
                }
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                    {
                        continue;
                    }
                    mesh = mf.sharedMesh;
                    scale = r.transform.lossyScale;
                    if (scale.sqrMagnitude < 1e-8f)
                    {
                        continue; // scaled away (a hidden part)
                    }
                }
                var part = new GameObject(r.name);
                part.transform.SetParent(root, false);
                part.transform.SetPositionAndRotation(r.transform.position, r.transform.rotation);
                part.transform.localScale = scale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = part.AddComponent<MeshRenderer>();
                mr.sharedMaterials = Util.MaterialFix.Fix(r.sharedMaterials);
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Your own dropped weapon goes back to its holster when you leave it lying for a while or walk away from it, as
        /// long as its slot is still free (pick something else up into it and the dropped one stays a normal drop).
        /// </summary>
        private void ReturnOwnDrops(Actor actor)
        {
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                DroppedWeapon d = _drops[i];
                if (d.OwnerSlot < 0 || d.Go == null)
                {
                    continue;
                }
                bool due = Time.time >= d.ReturnAt || (d.Go.transform.position - actor.Position()).sqrMagnitude > 400f;
                if (!due)
                {
                    continue;
                }
                if (actor.dead || d.OwnerSlot >= actor.weapons.Length || actor.weapons[d.OwnerSlot] != null)
                {
                    d.OwnerSlot = -1; // its place is taken: an ordinary drop from now on
                    continue;
                }
                // Into the holster, not the hand: the slot must not look like the active one while it is spawned.
                int active = actor.activeWeaponSlot;
                if (actor.activeWeapon == null)
                {
                    actor.activeWeaponSlot = -1;
                }
                Weapon w;
                try
                {
                    w = actor.EquipNewWeaponEntry(d.Entry, d.OwnerSlot, false);
                }
                finally
                {
                    actor.activeWeaponSlot = active;
                }
                if (w != null)
                {
                    RestoreAmmo(w, d);
                    _sys.Sounds.Play(MechSound.Holster, _sys.Rig.Head.position - Vector3.up * 0.5f, 0.6f);
                }
                Remove(d);
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
