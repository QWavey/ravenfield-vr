using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Works out how any weapon (vanilla or modded) is rigged by sampling its animator on an inert clone:
    /// which part is the gun body, which part reciprocates when it fires (slide/bolt/pump, a mesh or a skinned
    /// bone), which parts leave or vanish during the reload (magazine and the pieces travelling with it), where the
    /// arm rig puts the hands and the sights, and when in the reload the mod plays its sounds. Names are only
    /// tie-breakers and fallbacks; every part has a virtual stand-in so nothing ever fails outright.
    /// </summary>
    public static class WeaponAnalyzer
    {
        private static readonly Dictionary<string, WeaponRigData> Cache = new Dictionary<string, WeaponRigData>();

        private static readonly Regex ArmName = new Regex(
            @"(^|[^a-z])(arm(?!ature|or|our)|forearm|upperarm|hand(?!guard|le|stop)|wrist|finger|thumb|index|middle|ring|pinky|pinkie|little|clavicle|shoulder|elbow|palm|knuckle)",
            RegexOptions.IgnoreCase);
        // Mod authors name parts in many languages: English, German, French, Spanish, Russian (Cyrillic and
        // transliterated) and Chinese (characters and pinyin abbreviations such as DX = 弹匣, QJ = 枪机).
        private static readonly Regex MagName = new Regex(
            @"(^|[^a-z])(mag(azine?)?s?($|[^a-z])|clip|drum|shell|rocket|round|ammo|cartridge|belt|cargador|chargeur|patron|lenta|zidan|danxia)|(^|[^a-z])dx($|[^a-z])"
            + @"|магазин|патрон|лента|弹匣|弹夹|弹鼓|弹链|子弹|弹药|弹壳",
            RegexOptions.IgnoreCase);
        /// <summary>
        /// Parts named after the magazine that are not the magazine: its release button or catch, the well it sits in, a
        /// pouch or a spare. Taking one of them for the magazine leaves the real one in the gun when it is ejected.
        /// </summary>
        private static readonly Regex NotMagName = new Regex(
            @"release|catch|button|latch|(^|[^a-z])well|magwell|housing|lever|pouch|holder|spare|switch|bolt ?stop|stopper|indicator|弹匣释放|卡笋"
            // Optics: a magnifier's "Mag 2x" glass is not a magazine.
            + @"|glass|lens|scope|sight|optic|reticle|magnif|zoom|(^|[^a-z0-9])\d+(\.\d+)?x($|[^a-z])|倍镜|瞄",
            RegexOptions.IgnoreCase);

        private static bool IsMagName(string name) => MagName.IsMatch(name) && !NotMagName.IsMatch(name);

        private static readonly Regex OpticName = new Regex(
            @"glass|lens|scope|sight|optic|reticle|magnif|zoom|(^|[^a-z])(clu|cmd ?unit|command ?unit)($|[^a-z])|倍镜|瞄", RegexOptions.IgnoreCase);

        /// <summary>
        /// Shows at rest: the part itself is active and not scaled away, and (when it carries meshes) at least one of them is.
        /// Reload helpers that only appear during the reload animation are not.
        /// </summary>
        private static bool ShownAtIdle(Transform t, PoseSampler sampler)
        {
            if (!sampler.IdleVisible(t))
            {
                return false;
            }
            bool anyRenderer = false;
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
                {
                    continue;
                }
                anyRenderer = true;
                if (r.enabled && r.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly && sampler.IdleVisible(r.transform))
                {
                    return true;
                }
            }
            return !anyRenderer; // a bone of the skinned gun mesh
        }

        private static readonly Regex SlideName = new Regex(
            @"slide|bolt|charg|handle|carrier|pump|forend|fore-end|lever|breech|chamber|cocking|verschluss|schlitten|culasse|corredera|cerrojo|zatvor"
            + @"|(^|[^a-z])(rama|qj|ljb|taotong|qiangji)($|[^a-z])|затвор|рама|枪机|套筒|拉机柄|机柄|护木",
            RegexOptions.IgnoreCase);
        private static readonly Regex ArmMeshName = new Regex(@"arm(?!ature|or)|hand(?!guard|le)|glove|sleeve|finger|wrist|body ?arms|手", RegexOptions.IgnoreCase);
        /// <summary>Camera / shake bones that animators parent into weapon rigs; they move a lot and are never parts.</summary>
        private static readonly Regex CameraName = new Regex(
            @"camera|(^|[\s_.])cam([\s_.\d]|$)|shake|sway|sight ?bone|aim ?(point|bone|target|helper)|tracker|(^|[\s_.])ik([\s_.]|$)|locator|dummy|helper",
            RegexOptions.IgnoreCase);
        private static readonly Regex MuzzleName = new Regex(@"muzzle|barrel ?end|flash|firepoint|fire ?point|projectile ?spawn", RegexOptions.IgnoreCase);
        private static readonly Regex FingerName = new Regex(@"index|middle|ring|pinky|pinkie|little|finger", RegexOptions.IgnoreCase);

        public static string KeyOf(Weapon w, WeaponManager.WeaponEntry entry = null)
        {
            string prefab = w.name.Replace("(Clone)", "").Trim();
            entry = entry ?? w.weaponEntry;
            return entry != null ? entry.name + "#" + prefab : prefab;
        }

        public static WeaponRigData Get(Weapon w) => Get(w, null);

        public static bool IsCached(Weapon w, WeaponManager.WeaponEntry entry) => Cache.ContainsKey(KeyOf(w, entry));

        /// <param name="w">Live weapon or weapon prefab.</param>
        /// <param name="entry">Pass when analysing a prefab (prefabs have no weaponEntry yet).</param>
        public static WeaponRigData Get(Weapon w, WeaponManager.WeaponEntry entry)
        {
            string key = KeyOf(w, entry);
            if (Cache.TryGetValue(key, out WeaponRigData cached))
            {
                return cached;
            }
            WeaponRigData data;
            try
            {
                data = Analyze(w, key);
            }
            catch (Exception e)
            {
                Log.Exception($"Weapon analysis failed for {key}; using geometric fallback", e);
                data = Fallback(w, key);
                data.Warnings.Add("analysis threw " + e.GetType().Name + ": " + e.Message);
            }
            WeaponOverrides.Apply(w, data);
            Cache[key] = data;
            if (VRConfig.DumpWeaponRigs.Value)
            {
                Log.Info("Weapon rig " + data.Describe());
            }
            else
            {
                Log.Debug("Weapon rig " + data.Describe());
            }
            return data;
        }

        public static void ClearCache() => Cache.Clear();

        // ---------------------------------------------------------------------------------------------

        private static ActionType ClassifyAction(Weapon w)
        {
            if (w is ThrowableWeapon)
            {
                return ActionType.Throwable;
            }
            if (w is MeleeWeapon)
            {
                return ActionType.Melee;
            }
            Weapon.Configuration c = w.configuration;
            if (c.ammo <= 0)
            {
                return ActionType.NoAmmo;
            }
            if (c.ammo == 1)
            {
                return ActionType.SingleShot;
            }
            if (c.advancedReload)
            {
                return ActionType.ShellFed;
            }
            if (!c.auto && c.cooldown >= 0.45f)
            {
                return ActionType.Manual;
            }
            return ActionType.SelfLoading;
        }

        /// <summary>
        /// advancedReload only means the mod drives its reload with allowedReloads chunks. A weapon whose reload
        /// visibly swaps a magazine and loads at least half its capacity per motion is magazine-fed (HK416, 1911,
        /// PPSh...), not a tube that takes rounds one by one.
        /// </summary>
        private static void ReclassifyAdvancedReload(WeaponRigData d, Weapon w)
        {
            Weapon.Configuration c = w.configuration;
            if (d.Action != ActionType.ShellFed)
            {
                return;
            }
            bool magazineMoves = d.MagazinePath != null && d.MagMethod.StartsWith("reload", StringComparison.Ordinal);
            int chunk = 0;
            if (c.allowedReloads != null)
            {
                foreach (int r in c.allowedReloads)
                {
                    chunk = Mathf.Max(chunk, r);
                }
            }
            // Chunks of 1-2 are shells (tube, double barrel); a chunk of half the capacity or more is a magazine or clip.
            if (chunk * 2 < c.ammo || chunk < 3 && !magazineMoves)
            {
                return;
            }
            d.Action = !c.auto && c.cooldown >= 0.45f ? ActionType.Manual : ActionType.SelfLoading;
            d.Notes += "magazine-fed advanced reload ";
        }

        private static WeaponRigData Fallback(Weapon w, string key)
        {
            var d = new WeaponRigData { Key = key, Action = ClassifyAction(w), Notes = "fallback" };
            d.CyclesByHand = d.Action == ActionType.Manual || d.Action == ActionType.ShellFed && !w.configuration.auto && w.configuration.cooldown >= 0.45f;
            Transform muzzle = FindMuzzle(w, null, out _);
            Transform root = w.transform;
            d.GunBodyPath = WeaponRig.PathOf(root, muzzle) ?? new int[0];
            d.MuzzlePath = d.GunBodyPath;
            d.AimAxis = Vector3.forward;
            d.Up = Vector3.up;
            d.IsPistol = w.slot == 1;
            d.GripPoint = new Vector3(0f, -0.04f, d.IsPistol ? -0.12f : -0.32f);
            d.SupportPoint = d.GripPoint + new Vector3(0f, 0f, d.IsPistol ? 0.01f : 0.22f);
            d.HasSupportPoint = true;
            FinishVirtualParts(d);
            return d;
        }

        /// <summary>Configured muzzle, else a muzzle-like name, else null (caller picks a geometric muzzle).</summary>
        private static Transform FindMuzzle(Weapon w, HashSet<Transform> armRelated, out string method)
        {
            Transform[] muzzles = w.configuration.muzzles;
            if (muzzles != null)
            {
                foreach (Transform m in muzzles)
                {
                    if (m != null && m.IsChildOf(w.transform))
                    {
                        method = "configured";
                        return m;
                    }
                }
            }
            foreach (Transform t in w.GetComponentsInChildren<Transform>(true))
            {
                if (MuzzleName.IsMatch(t.name) && (armRelated == null || !armRelated.Contains(t)))
                {
                    method = "name";
                    return t;
                }
            }
            method = "root";
            return w.transform;
        }

        private static WeaponRigData Analyze(Weapon w, string key)
        {
            Transform root = w.transform;
            var d = new WeaponRigData { Key = key, Action = ClassifyAction(w) };

            Transform[] all = root.GetComponentsInChildren<Transform>(true);
            var index = new Dictionary<Transform, int>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                index[all[i]] = i;
            }

            // --- arm rig ---------------------------------------------------------------------------
            // The arms mesh's bone list should be the arm skeleton, but exporters often list every bone of the
            // armature, gun bones included. Arm-named bones are arm bones (even when the gun hangs from the
            // hand); another listed bone counts only if it lies on an arm chain (below an arm-named bone, or
            // above one) and is not on the muzzle's chain or part-named (bolt, magazine...).
            var gunChain = new HashSet<Transform>();
            for (Transform t = FindMuzzle(w, null, out _); t != null && t != root; t = t.parent)
            {
                gunChain.Add(t);
            }
            var named = new HashSet<Transform>();
            foreach (Transform t in all)
            {
                if (t != root && ArmName.IsMatch(t.name) && !MagName.IsMatch(t.name) && !SlideName.IsMatch(t.name))
                {
                    named.Add(t);
                }
            }
            var armBones = new HashSet<Transform>(named);
            if (w.arms != null && w.arms.bones != null)
            {
                foreach (Transform b in w.arms.bones)
                {
                    if (b != null && b != root && !armBones.Contains(b) && OnArmChain(b, root, named, gunChain))
                    {
                        armBones.Add(b);
                    }
                }
            }
            var armRelated = new HashSet<Transform>(armBones);
            foreach (Transform b in armBones)
            {
                for (Transform p = b.parent; p != null && p != root; p = p.parent)
                {
                    armRelated.Add(p);
                }
            }
            // A separate third-person model carried inside the first-person prefab is never a moving part. Many mods
            // point thirdPersonTransform at the first-person gun itself; that one obviously stays.
            Transform tp = w.thirdPersonTransform;
            Transform configuredMuzzle = FindMuzzle(w, null, out _);
            bool tpCandidate = tp != null && tp != root && tp.IsChildOf(root) && !configuredMuzzle.IsChildOf(tp) && !gunChain.Contains(tp)
                && !armBones.Any(b => b.IsChildOf(tp));
            // Some mods point it at the visible gun and keep the muzzle on a separate, still branch: then that subtree is
            // the gun, and the gun's reference body (the still branch would make the whole recoiling gun a moving part).
            Transform tpGun = tpCandidate && HoldsTheOnlyGunModel(w, root, tp, armBones) ? tp : null;
            if (tpCandidate && tpGun == null)
            {
                foreach (Transform t in tp.GetComponentsInChildren<Transform>(true))
                {
                    armRelated.Add(t);
                }
            }

            // --- renderers: arm meshes (hidden in VR) and the bones that deform the gun -------------
            var gunBones = new HashSet<Transform>();
            var armRenderers = new HashSet<Renderer>();
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                bool hide = r == w.arms;
                var smr = r as SkinnedMeshRenderer;
                if (!hide && smr != null && smr.bones != null)
                {
                    int armCount = 0, total = 0;
                    foreach (Transform b in smr.bones)
                    {
                        if (b == null)
                        {
                            continue;
                        }
                        total++;
                        if (armBones.Contains(b))
                        {
                            armCount++;
                        }
                    }
                    // Exporters often skin the gun to the whole skeleton (mostly finger bones), so a majority of arm
                    // bones alone does not make an arm mesh: it must be (nearly) all arm bones, or arm-named.
                    float armShare = total > 0 ? (float)armCount / total : 0f;
                    hide = armShare >= 0.9f || armShare > 0.5f && ArmMeshName.IsMatch(r.name);
                }
                if (!hide && smr == null && armBones.Contains(r.transform))
                {
                    hide = true;
                }
                if (hide)
                {
                    armRenderers.Add(r);
                    int[] path = WeaponRig.PathOf(root, r.transform);
                    if (path != null)
                    {
                        d.ArmRendererPaths.Add(path);
                    }
                }
                else if (smr != null && smr.bones != null)
                {
                    foreach (Transform b in smr.bones)
                    {
                        if (b != null && !armBones.Contains(b))
                        {
                            gunBones.Add(b);
                        }
                    }
                }
            }

            // --- muzzle and gun body: topmost non-arm transform above the muzzle --------------------
            Transform muzzle = FindMuzzle(w, armRelated, out string muzzleMethod);
            if (muzzleMethod == "root")
            {
                Transform geometric = GeometricMuzzleHost(root, armRelated);
                if (geometric != null)
                {
                    muzzle = geometric;
                    muzzleMethod = "geometry";
                }
                d.Warnings.Add("no muzzle configured; barrel direction estimated from the mesh");
            }
            Transform gunBody = muzzle;
            for (Transform t = muzzle; t != null && t != root; t = t.parent)
            {
                if (armRelated.Contains(t))
                {
                    break;
                }
                gunBody = t;
            }
            if (tpGun != null && !muzzle.IsChildOf(tpGun))
            {
                gunBody = tpGun;
            }
            if (gunBody == muzzle)
            {
                // The muzzle hangs off the rig root instead of the gun bone (meshes under it are muzzle flashes):
                // take the gun mesh's own top bone.
                Transform host = GunMeshHost(root, armRelated, armBones, armRenderers);
                if (host != null && !host.IsChildOf(muzzle))
                {
                    gunBody = host;
                }
            }
            d.GunBodyPath = WeaponRig.PathOf(root, gunBody) ?? new int[0];
            d.MuzzlePath = WeaponRig.PathOf(root, muzzle);

            Transform handR = FindHand(armBones, right: true);
            Transform handL = FindHand(armBones, right: false);

            // --- sample the animator on an inert clone ----------------------------------------------
            Animator animator = w.animator != null ? w.animator : root.GetComponent<Animator>();
            bool canSample = animator != null && animator.runtimeAnimatorController != null && animator.transform == root;
            PoseSampler sampler = SampleFromRigidBody(w, all, index, muzzle, handR, canSample, d.Action, armRenderers, ref gunBody, out bool sampled);
            d.GunBodyPath = WeaponRig.PathOf(root, gunBody) ?? new int[0];
            try
            {
                d.Animated = sampled;
                if (!sampled)
                {
                    sampler.UseLivePosesAsIdle();
                    d.Notes += "static ";
                    if (animator == null || animator.runtimeAnimatorController == null)
                    {
                        d.Warnings.Add("no animator: parts found by name only");
                    }
                }

                RigidPose gunIdle = sampler.Idle(gunBody);
                RigidPose muzzleIdle = sampler.Idle(muzzle);
                if (muzzleMethod == "geometry")
                {
                    d.AimAxis = GeometricAxis(root, gunBody, armRelated, sampler, gunIdle);
                }
                else
                {
                    d.AimAxis = gunIdle.InverseTransformDirection(muzzleIdle.Forward).normalized;
                }
                if (d.AimAxis.sqrMagnitude < 0.5f)
                {
                    d.AimAxis = Vector3.forward;
                }
                Vector3 up = gunIdle.InverseTransformDirection(sampler.Idle(root).Up);
                up = Vector3.ProjectOnPlane(up, d.AimAxis);
                d.Up = up.sqrMagnitude > 1e-4f ? up.normalized : Vector3.ProjectOnPlane(Vector3.up, d.AimAxis).normalized;
                d.MuzzlePoint = gunIdle.InverseTransformPoint(muzzleIdle.position);
                float rootAngle = Vector3.Angle(gunIdle.TransformDirection(d.AimAxis), sampler.Idle(root).Forward);
                if (rootAngle > 25f)
                {
                    d.Warnings.Add($"barrel points {rootAngle:0}° away from the weapon's forward axis (check muzzle orientation)");
                }

                AnalyzeHands(d, handR, handL, sampler, gunIdle, w);
                AnalyzeSights(d, sampler);
                AlignEyeToScopeModel(d, root, armRelated, sampler, w, gunIdle);
                AnalyzeSightModes(d, sampler, w);
                AnalyzeSightParts(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler);
                Transform slide = AnalyzeSlide(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle);
                AnalyzeMagazine(d, all, index, root, gunBody, muzzle, slide, armRelated, gunBones, sampler, w);
                MeasureMagazineProtrusion(d, root, gunBody, armRelated, armRenderers, gunBones, sampler);
                MagKickRule(d, w);
                ReclassifyAdvancedReload(d, w);
                DetectHinge(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, w, gunIdle);
                FindBeltParts(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler);
                d.CyclesByHand = d.Action == ActionType.Manual
                                 || d.Action == ActionType.ShellFed && !w.configuration.auto && w.configuration.cooldown >= 0.45f;
                if (d.Animated && sampler.HasReload && (d.Action == ActionType.SelfLoading || d.Action == ActionType.Manual))
                {
                    RackStrokeFromHands(d, handR, handL, sampler, index, root, gunIdle);
                }
                d.GripCurls = FingerCurls(handR, sampler);
                d.SupportCurls = FingerCurls(handL, sampler);
                d.ArmRight = ArmChainOf(root, handR, handL, armBones, sampler, gunIdle, d);
                d.ArmLeft = ArmChainOf(root, handL, handR, armBones, sampler, gunIdle, d);
                BuildSoundMap(d, sampler, w);
                if (WeaponSelfTest.WantsRigDump(key))
                {
                    WeaponSelfTest.AppendRigDump(DumpRig(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler));
                }
            }
            finally
            {
                sampler.Dispose();
            }

            FinishVirtualParts(d);
            CheckSanity(d, root, gunBody, armRelated);
            return d;
        }

        // ---- hands & sights ---------------------------------------------------------------------------

        private static void AnalyzeHands(WeaponRigData d, Transform handR, Transform handL, PoseSampler sampler, RigidPose gunIdle, Weapon w)
        {
            d.IsPistol = w.slot == 1;
            if (handR != null)
            {
                d.GripPoint = gunIdle.InverseTransformPoint(PalmOf(handR, sampler));
            }
            else
            {
                d.GripPoint = d.MuzzlePoint - d.AimAxis * (d.IsPistol ? 0.13f : 0.36f) - d.Up * 0.05f;
                d.Warnings.Add("no right-hand bone: grip position estimated");
            }
            if (handL != null)
            {
                d.SupportPoint = gunIdle.InverseTransformPoint(PalmOf(handL, sampler));
                d.HasSupportPoint = true;
            }
            else
            {
                d.SupportPoint = d.GripPoint + d.AimAxis * (d.IsPistol ? 0.01f : 0.24f);
                d.HasSupportPoint = !d.IsPistol;
            }
            d.HandsMethod = handR != null && handL != null ? "arm rig" : handR != null ? "right hand only" : "estimated";
            if (Vector3.Dot(d.SupportPoint - d.GripPoint, d.AimAxis) < 0.07f)
            {
                d.IsPistol = true; // support hand cups the grip: no foregrip steering
            }
        }

        private const float ScopeEyeRelief = 0.08f;
        private static readonly Regex ScopeName = new Regex(@"scope|optic|telescop|ocular|eyepiece|(^|[^a-z])lens|acog|pso|leupold|nightforce|vortex|kahles|elcan|specter|(^|[^a-z])zf\s?\d", RegexOptions.IgnoreCase);

        /// <summary>
        /// Weapons with a 2D scope overlay: in flat play the overlay replaces the view, so the aim animation never had
        /// to line the scope model up with the camera, and the eye found from it can sit beside or behind the tube.
        /// The scope model itself says where the eye goes: its rear (ocular) end, on the tube's centre line.
        /// </summary>
        private static void AlignEyeToScopeModel(WeaponRigData d, Transform root, HashSet<Transform> armRelated, PoseSampler sampler, Weapon w, RigidPose gunIdle)
        {
            if (d.Action == ActionType.Melee || d.Action == ActionType.Throwable || d.Action == ActionType.NoAmmo || w.configuration.scopeAimObject == null)
            {
                return;
            }
            Weapon clone = root.GetComponent<Weapon>();
            Transform overlay = clone != null && clone.configuration.scopeAimObject != null ? clone.configuration.scopeAimObject.transform : null;
            Quaternion frame = Quaternion.LookRotation(d.AimAxis, d.Up);
            Quaternion inv = Quaternion.Inverse(frame);
            RigidPose gunInv = gunIdle.Inverse();
            bool any = false;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Transform t = mf.transform;
                if (mf.sharedMesh == null || mf.GetComponent<MeshRenderer>() == null || armRelated.Contains(t) || overlay != null && t.IsChildOf(overlay))
                {
                    continue;
                }
                // Its own name or its parent's: a whole model called "..._scoped" must not count as the scope.
                bool named = ScopeName.IsMatch(t.name) || t.parent != null && t.parent != root && ScopeName.IsMatch(t.parent.name);
                if (!named)
                {
                    continue;
                }
                Bounds mb = mf.sharedMesh.bounds;
                RigidPose idle = sampler.Idle(t);
                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    Vector3 gun = gunInv.TransformPoint(idle.TransformPoint(Vector3.Scale(corner, t.lossyScale)));
                    Vector3 q = inv * (gun - d.MuzzlePoint);
                    min = Vector3.Min(min, q);
                    max = Vector3.Max(max, q);
                    any = true;
                }
            }
            if (!any)
            {
                return;
            }
            Vector3 size = max - min;
            var ocularQ = new Vector3((min.x + max.x) * 0.5f, (min.y + max.y) * 0.5f, min.z);
            // A scope is a slim tube over the bore, behind the muzzle.
            if (size.z < 0.06f || size.z > 0.6f || size.x > 0.12f || size.y > 0.15f || ocularQ.y < -0.02f || ocularQ.y > 0.15f
                || Mathf.Abs(ocularQ.x) > 0.06f || max.z > 0.02f)
            {
                return;
            }
            Vector3 ocular = d.MuzzlePoint + frame * ocularQ;
            d.ScopeOcular = ocular;
            // The tube's narrowest width, capped at a typical eyepiece: the objective bell and the rings are wider.
            d.ScopeRadius = Mathf.Clamp(Mathf.Min(size.x, size.y) * 0.5f * 0.8f, 0.01f, 0.022f);
            d.HasScopeModel = true;
            d.EyeDirection = d.AimAxis; // a zeroed scope looks along the bore
            d.EyePoint = ocular - d.AimAxis * ScopeEyeRelief;
            d.HasEyePoint = true;
            d.EyeFromLine = false;
        }

        /// <summary>
        /// The eye line of each sight mode, from the gun's aim pose in that mode. A mode whose pose does not line its sight
        /// up keeps the default line (the mod only changes the zoom there); a mode that narrows the view is magnified.
        /// </summary>
        private static void AnalyzeSightModes(WeaponRigData d, PoseSampler sampler, Weapon w)
        {
            Weapon.SightMode[] modes = w.configuration.sightModes;
            if (modes == null || modes.Length < 2 || !d.HasEyePoint)
            {
                return;
            }
            d.Sights = new SightLine[modes.Length];
            bool haveEye = TryEyeInRoot(out Vector3 eyeInRoot);
            for (int i = 0; i < modes.Length; i++)
            {
                var line = new SightLine
                {
                    EyePoint = d.EyePoint,
                    EyeDirection = d.EyeDirection,
                    Name = modes[i].name ?? "",
                    Fov = modes[i].overrideFov ? modes[i].fov : w.configuration.aimFov,
                    Magnified = (modes[i].overrideFov ? modes[i].fov : w.configuration.aimFov) > 0.5f && (modes[i].overrideFov ? modes[i].fov : w.configuration.aimFov) < 32f
                };
                RigidPose? pose = sampler.AimModes != null && i < sampler.AimModes.Length ? sampler.AimModes[i] : null;
                if (i > 0 && haveEye && pose.HasValue)
                {
                    Vector3 eye = pose.Value.InverseTransformPoint(eyeInRoot);
                    Vector3 dir = pose.Value.InverseTransformDirection(Vector3.forward).normalized;
                    float behind = Vector3.Dot(eye - d.MuzzlePoint, d.AimAxis);
                    float lateral = Vector3.ProjectOnPlane(eye - d.MuzzlePoint, d.AimAxis).magnitude;
                    // A different line than the default one, and still a plausible sight line over this gun.
                    bool moved = Vector3.Distance(eye, d.EyePoint) > 0.008f || Vector3.Angle(dir, d.EyeDirection) > 1.5f;
                    if (moved && behind < -0.05f && behind > -1.6f && lateral < 0.25f && Vector3.Dot(dir, d.AimAxis) > 0.85f)
                    {
                        line.EyePoint = d.EyeFromLine ? eye + dir * Vector3.Dot(d.EyePoint - eye, dir) : eye;
                        line.EyeDirection = dir;
                        line.Measured = true;
                    }
                }
                d.Sights[i] = line;
            }
            int measured = d.Sights.Count(s => s.Measured), magnified = d.Sights.Count(s => s.Magnified);
            d.Notes += $"{modes.Length} sight modes ({measured} with their own line, {magnified} magnified) ";
        }

        /// <summary>
        /// The parts a sight mode moves relative to the gun (a magnifier flipping in behind a red dot): their pose in every
        /// mode, so they can stay where the selected mode puts them when the gun is not aimed (the animation only moves them
        /// in its aim pose, and in VR you aim only while your eye is at the sight).
        /// </summary>
        private static void AnalyzeSightParts(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler)
        {
            RigidPose[][] modes = sampler.ModePoses;
            if (modes == null || modes.Length < 2 || d.Sights == null)
            {
                return;
            }
            var moving = new List<Transform>();
            foreach (Transform t in all)
            {
                if (!IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones) || !sampler.IdleVisible(t))
                {
                    continue;
                }
                int i = index[t];
                float move = 0f, turn = 0f;
                for (int m = 1; m < modes.Length; m++)
                {
                    if (modes[m] == null || modes[0] == null)
                    {
                        continue;
                    }
                    move = Mathf.Max(move, Vector3.Distance(modes[m][i].position, modes[0][i].position));
                    turn = Mathf.Max(turn, Quaternion.Angle(modes[m][i].rotation, modes[0][i].rotation));
                }
                if (move >= 0.003f && move < 0.3f || turn >= 5f)
                {
                    moving.Add(t);
                }
            }
            // Only the top of each moving branch: its children ride along.
            foreach (Transform t in moving)
            {
                bool underAnother = false;
                for (Transform p = t.parent; p != null && p != root; p = p.parent)
                {
                    if (moving.Contains(p))
                    {
                        underAnother = true;
                        break;
                    }
                }
                if (underAnother || d.SightPartPaths.Count >= 8)
                {
                    continue;
                }
                d.SightPartPaths.Add(WeaponRig.PathOf(root, t));
            }
            if (d.SightPartPaths.Count == 0)
            {
                return;
            }
            d.SightPartPoses = new RigidPose[d.SightPartPaths.Count][];
            for (int p = 0; p < d.SightPartPaths.Count; p++)
            {
                int i = index[WeaponRig.Resolve(root, d.SightPartPaths[p])];
                d.SightPartPoses[p] = new RigidPose[modes.Length];
                for (int m = 0; m < modes.Length; m++)
                {
                    d.SightPartPoses[p][m] = modes[m] != null ? modes[m][i] : modes[0][i];
                }
            }
            d.Notes += $"{d.SightPartPaths.Count} part(s) moved by the sight mode (kept in place when not aiming) ";
        }

        private static void AnalyzeSights(WeaponRigData d, PoseSampler sampler)
        {
            if (d.Action == ActionType.Melee || d.Action == ActionType.Throwable || d.Action == ActionType.NoAmmo)
            {
                return; // nothing to aim down
            }
            if (!sampler.HasAim || !TryEyeInRoot(out Vector3 eyeInRoot))
            {
                return;
            }
            RigidPose gunAim = sampler.AimGunBody;
            Vector3 eye = gunAim.InverseTransformPoint(eyeInRoot);
            Vector3 dir = gunAim.InverseTransformDirection(Vector3.forward).normalized;
            float behind = Vector3.Dot(eye - d.MuzzlePoint, d.AimAxis);
            float lateral = Vector3.ProjectOnPlane(eye - d.MuzzlePoint, d.AimAxis).magnitude;
            if (behind < -0.12f && behind > -1.6f && lateral < 0.22f && Vector3.Dot(dir, d.AimAxis) > 0.9f)
            {
                d.EyePoint = eye;
                d.EyeDirection = dir;
                d.HasEyePoint = true;
            }
            else if (lateral < 0.08f && Vector3.Dot(dir, d.AimAxis) > 0.996f)
            {
                // The sights line up with the bore, only the distance is off (offsets in the mod's rig):
                // keep the line and put the eye where a shooter's eye goes behind this kind of gun.
                Vector3 overBore = Vector3.ProjectOnPlane(eye - d.MuzzlePoint, d.AimAxis);
                float gripAlong = Vector3.Dot(d.GripPoint - d.MuzzlePoint, d.AimAxis);
                d.EyePoint = d.MuzzlePoint + overBore + d.AimAxis * (gripAlong - (d.IsPistol ? 0.35f : 0.12f));
                d.EyeDirection = d.AimAxis;
                d.HasEyePoint = true;
                d.EyeFromLine = true;
            }
            else
            {
                d.Warnings.Add($"aim pose does not line the sights up with the barrel (eye {-behind:0.00} m behind the muzzle, "
                               + $"{lateral:0.00} m off the bore, {Vector3.Angle(dir, d.AimAxis):0}° off axis); aiming falls back to the barrel line");
            }
        }

        // ---- slide / bolt / pump ----------------------------------------------------------------------

        private static Transform AnalyzeSlide(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, RigidPose gunIdle)
        {
            Transform slide = null;
            Vector3 slideVec = Vector3.zero;
            if (d.Animated)
            {
                float best = 0f;
                foreach (Transform t in all)
                {
                    if (!IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                    {
                        continue;
                    }
                    int i = index[t];
                    float m = sampler.FireMax[i];
                    if (m < 0.004f || m > MaxBoltTravel(d) || !sampler.IdleVisible(t))
                    {
                        continue; // still, or a casing / flash that flies off when firing
                    }
                    Vector3 v = sampler.FireVec[i];
                    float along = Mathf.Abs(Vector3.Dot(v.normalized, d.AimAxis));
                    if (along < 0.6f)
                    {
                        continue; // slides and bolts run along the bore
                    }
                    float score = m * (0.4f + 0.6f * along) * (SlideName.IsMatch(t.name) ? 1.5f : 1f);
                    if (score > best)
                    {
                        best = score;
                        slide = t;
                        slideVec = v;
                    }
                }
                if (slide != null)
                {
                    float m0 = sampler.FireMax[index[slide]];
                    for (Transform p = slide.parent; p != null && p != gunBody && p != root; p = p.parent)
                    {
                        if (!index.TryGetValue(p, out int pi) || armRelated.Contains(p) || sampler.FireMax[pi] < m0 * 0.8f)
                        {
                            break;
                        }
                        slide = p;
                    }
                    d.SlideMethod = gunBones.Contains(slide) && !HasOwnRenderer(slide) ? "fire motion (skinned bone)" : "fire motion";
                }
            }
            if (slide == null && d.Animated && sampler.HasReload)
            {
                slide = ChargingHandleFromReload(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, out slideVec);
                if (slide != null)
                {
                    d.SlideMethod = "reload motion (charging handle)";
                }
            }
            if (slide == null)
            {
                slide = FindByName(all, SlideName, root, gunBody, muzzle, armRelated, gunBones);
                if (slide != null)
                {
                    d.SlideMethod = "name '" + slide.name + "'";
                    if (sampler.HasReload)
                    {
                        Vector3 rest = sampler.RelIdle[index[slide]].position;
                        float back = 0f;
                        foreach (Vector3 p in sampler.ReloadTrajectory[index[slide]])
                        {
                            back = Mathf.Max(back, Vector3.Dot(p - rest, -d.AimAxis));
                        }
                        if (back >= 0.015f && back <= MaxBoltTravel(d))
                        {
                            slideVec = -d.AimAxis * back;
                        }
                    }
                }
            }
            if (slide == null)
            {
                if (d.Action == ActionType.SelfLoading || d.Action == ActionType.Manual)
                {
                    d.Warnings.Add("no moving slide/bolt found: racking uses an invisible handle");
                }
                return null;
            }

            d.SlidePath = WeaponRig.PathOf(root, slide);
            d.SlideRestLocal = sampler.IdleLocalPosition(slide);
            float travel = slideVec.sqrMagnitude > 0f ? slideVec.magnitude * 1.15f : (d.IsPistol ? 0.035f : 0.07f);
            travel = Mathf.Clamp(travel, d.IsPistol ? 0.02f : 0.03f, 0.15f);
            Vector3 dir = slideVec.sqrMagnitude > 0f ? slideVec.normalized : -d.AimAxis;
            if (Vector3.Dot(dir, -d.AimAxis) < 0.5f)
            {
                dir = -d.AimAxis; // keep racking straight back even if the fire clip arcs
            }
            d.SlideTravel = dir * travel;
            Bounds b = sampler.GunSpaceBounds(slide, gunIdle);
            if (b.size.sqrMagnitude > 1e-6f)
            {
                float along = Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z))));
                d.RackPoint = b.center - d.AimAxis * along * 0.6f;
            }
            else
            {
                d.RackPoint = gunIdle.InverseTransformPoint(sampler.Idle(slide).position);
            }
            if (d.Action == ActionType.Manual && d.HasSupportPoint && Vector3.Distance(d.RackPoint, d.SupportPoint) < 0.12f)
            {
                d.RackPoint = d.SupportPoint; // pump action: rack where the support hand already is
            }

            // A separate charging handle the reload works (AR style): that is where the hand grabs, the bolt follows.
            if (d.Animated && sampler.HasReload && !d.SlideMethod.StartsWith("reload motion (charging handle)", StringComparison.Ordinal))
            {
                Transform handle = ChargingHandleFromReload(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, out Vector3 handleVec, slide);
                if (handle != null && Vector3.Distance(sampler.RelIdle[index[handle]].position, d.GripPoint) < 0.45f)
                {
                    d.HandlePath = WeaponRig.PathOf(root, handle);
                    d.HandleRestLocal = sampler.IdleLocalPosition(handle);
                    d.HandleTravel = handleVec;
                    Bounds hb = sampler.GunSpaceBounds(handle, gunIdle);
                    d.RackPoint = hb.size.sqrMagnitude > 1e-6f ? hb.center : gunIdle.InverseTransformPoint(sampler.Idle(handle).position);
                    d.SlideMethod += " + handle";
                }
            }

            // Keyframes: when the slide goes back / home in the reload and fire animations.
            int si = index[slide];
            if (sampler.HasReload)
            {
                SlideKeyframes(sampler.ReloadTrajectory[si], sampler.RelIdle[si].position, d.SlideTravel.normalized, out d.TSlideBack, out d.TSlideForward);
            }
            if (sampler.FireTrajectory != null)
            {
                SlideKeyframes(sampler.FireTrajectory[si], sampler.RelIdle[si].position, d.SlideTravel.normalized, out d.TFireSlideBack, out d.TFireSlideForward);
            }
            return slide;
        }

        /// <summary>Every transform of the rig with what the sampler saw it do (for -rfvr-dumprig).</summary>
        private static string DumpRig(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {d.Key}  action {d.Action}  aim {d.AimAxis:F2} up {d.Up:F2}  grip {d.GripPoint:F3}  muzzle {d.MuzzlePoint:F3}");
            sb.AppendLine("    flags: A arm  G gun body  M muzzle  C part candidate  h hidden at idle  b skinned bone");
            sb.AppendLine("    fire: move cm (along bore) rot deg @axis | reload: move cm, first dir (back, down) rot deg @axis");
            foreach (Transform t in all)
            {
                int i = index[t];
                int depth = 0;
                for (Transform p = t; p != root && p != null; p = p.parent)
                {
                    depth++;
                }
                string flags = (armRelated.Contains(t) ? "A" : "") + (t == gunBody ? "G" : "") + (t == muzzle ? "M" : "")
                               + (IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones) ? "C" : "")
                               + (sampler.IdleVisible(t) ? "" : "h") + (gunBones.Contains(t) ? "b" : "");
                string fire = "";
                if (sampler.FireMax[i] > 0.002f || sampler.FireRotMax[i] > 2f)
                {
                    fire = $" | fire {sampler.FireMax[i] * 100f:0.0}cm ({Vector3.Dot(sampler.FireVec[i].normalized, d.AimAxis):0.00}) rot {sampler.FireRotMax[i]:0}@{sampler.FireRotAxis[i]:F1}";
                }
                string reload = "";
                if (sampler.HasReload && (sampler.ReloadMax[i] > 0.005f || sampler.ReloadRotMax[i] > 5f))
                {
                    Vector3 fd = sampler.FirstDir[i];
                    reload = $" | reload {sampler.ReloadMax[i] * 100f:0.0}cm dir({Vector3.Dot(fd, -d.AimAxis):0.0},{Vector3.Dot(fd, -d.Up):0.0}) rot {sampler.ReloadRotMax[i]:0}@{sampler.ReloadRotAxis[i]:F1}";
                    Vector3 rest = sampler.RelIdle[i].position;
                    Vector3[] tr = sampler.ReloadTrajectory[i];
                    float back = 0f, side = 0f;
                    foreach (Vector3 p in tr)
                    {
                        float a = Vector3.Dot(p - rest, -d.AimAxis);
                        back = Mathf.Max(back, a);
                        side = Mathf.Max(side, ((p - rest) - -d.AimAxis * a).magnitude);
                    }
                    reload += $" back {back * 100f:0.0} side {side * 100f:0.0} end {(tr[tr.Length - 1] - rest).magnitude * 100f:0.0} grip {Vector3.Distance(rest, d.GripPoint) * 100f:0}";
                }
                string drawn = "";
                Renderer own = t.GetComponent<Renderer>();
                if (own != null && !(own is ParticleSystemRenderer))
                {
                    Material mat = own.sharedMaterial;
                    string look = mat == null ? "no material"
                        : $"{mat.shader.name}, queue {mat.renderQueue}{(mat.HasProperty("_Color") ? $", alpha {mat.color.a:0.00}" : "")}{(mat.HasProperty("_Mode") ? $", mode {mat.GetFloat("_Mode"):0}" : "")}";
                    Mesh mesh = own is SkinnedMeshRenderer skin ? skin.sharedMesh : t.GetComponent<MeshFilter>()?.sharedMesh;
                    string shape = mesh == null ? ""
                        : $", mesh '{mesh.name}' {Vector3.Scale(mesh.bounds.size, t.lossyScale).magnitude / Mathf.Max(1e-6f, root.lossyScale.x) * 100f:0.0} cm";
                    drawn = $" | {own.GetType().Name}{(own.enabled ? "" : " off")}, shadows {own.shadowCastingMode}, {look}{shape}";
                }
                sb.AppendLine($"{new string(' ', 2 + depth * 2)}{t.name} [{flags}]{fire}{reload}{drawn}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Where the mod's own reload racks: a stretch of the reload where a hand near the receiver moves straight back along
        /// the bore. Its start is the handle, its length the travel. Used when the bolt is baked into the receiver (no part
        /// moves), and when the part that moves when firing is a carrier inside the receiver: the hand then has to take the
        /// handle where the reload hand takes it, not reach into the gun.
        /// </summary>
        private static void RackStrokeFromHands(WeaponRigData d, Transform handR, Transform handL, PoseSampler sampler, Dictionary<Transform, int> index,
            Transform root, RigidPose gunIdle)
        {
            float bestLen = 0f;
            Vector3 bestStart = Vector3.zero;
            float bestEnd = -1f;
            int window = Mathf.RoundToInt(0.7f / PoseSampler.Dt);
            foreach (Transform hand in new[] { handL, handR })
            {
                if (hand == null || !index.TryGetValue(hand, out int hi))
                {
                    continue;
                }
                Vector3[] traj = sampler.ReloadTrajectory[hi];
                // The grasp sits a little ahead of the wrist bone, toward the fingers.
                Vector3 graspOffset = PalmOf(hand, sampler) - sampler.RelIdle[hi].position;
                for (int a = 0; a < traj.Length; a++)
                {
                    Vector3 start = traj[a] + graspOffset;
                    float alongBore = Vector3.Dot(start - d.MuzzlePoint, d.AimAxis);
                    float offBore = Vector3.ProjectOnPlane(start - d.MuzzlePoint, d.AimAxis).magnitude;
                    float gripAlong = Vector3.Dot(d.GripPoint - d.MuzzlePoint, d.AimAxis);
                    // On the receiver: within a hand's width of the bore, from behind the grip to ahead of it.
                    if (offBore > 0.16f || alongBore > -0.05f || alongBore < gripAlong - 0.25f)
                    {
                        continue;
                    }
                    // Follow the hand back until it first stops: that is the end of the bolt's travel (afterwards
                    // the hand lets go and moves away).
                    for (int b = a + 1; b < traj.Length && b <= a + window; b++)
                    {
                        float step = Vector3.Dot(traj[b] - traj[b - 1], -d.AimAxis);
                        Vector3 move = traj[b] - traj[a];
                        float back = Vector3.Dot(move, -d.AimAxis);
                        float side = (move - -d.AimAxis * back).magnitude;
                        if (side > Mathf.Max(0.012f, back * 0.45f) || step < -0.002f)
                        {
                            break;
                        }
                        bool stopped = step < 0.0015f && back >= 0.02f;
                        if (stopped || b == a + window || b == traj.Length - 1)
                        {
                            if (back > bestLen)
                            {
                                bestLen = back;
                                bestStart = start;
                                bestEnd = (b + 1) * PoseSampler.Dt;
                            }
                            break;
                        }
                    }
                }
            }
            if (bestLen < 0.03f)
            {
                return;
            }
            if (d.SlidePath != null)
            {
                // A moving part was found. A separate charging handle, or a pistol's slide, is where the hand takes it.
                if (d.HandlePath != null || d.IsPistol || Vector3.Distance(bestStart, d.RackPoint) < 0.03f)
                {
                    return;
                }
                // The rack point sits on the part that moves when firing; the reload hand takes hold somewhere else: the
                // part is a carrier inside the receiver, the handle you see is elsewhere (beside or above it).
                Transform slide = WeaponRig.Resolve(root, d.SlidePath);
                Bounds sb = slide != null ? sampler.GunSpaceBounds(slide, gunIdle) : new Bounds();
                bool onPart = sb.size.sqrMagnitude > 1e-6f && sb.SqrDistance(bestStart) < 0.015f * 0.015f;
                d.RackPoint = bestStart;
                d.RackOnSurface = true;
                d.CarveHandle = !onPart;
                d.SlideMethod += onPart ? " (taken where the reload hand takes it)" : " (inside the receiver: the handle the reload hand takes is cut out and moves)";
                return;
            }
            d.RackFromHand = true;
            d.RackOnSurface = true;
            d.RackPoint = bestStart;
            // A hand keeps moving a little after the bolt stops: cap at real bolt travel.
            d.SlideTravel = -d.AimAxis * Mathf.Clamp(bestLen, d.IsPistol ? 0.02f : 0.03f, d.IsPistol ? 0.045f : 0.11f);
            d.SlideMethod = "reload hand motion (bolt is part of the receiver mesh)";
            d.TSlideBack = bestEnd;
            d.Warnings.RemoveAll(w => w.StartsWith("no moving slide/bolt", StringComparison.Ordinal));
        }

        private static readonly Regex[] FingerPatterns =
        {
            new Regex(@"thumb|daumen|拇指", RegexOptions.IgnoreCase),
            new Regex(@"index|pointer|zeige|食指", RegexOptions.IgnoreCase),
            new Regex(@"middle|mittel|中指", RegexOptions.IgnoreCase),
            new Regex(@"(^|[^a-z])ring|无名指", RegexOptions.IgnoreCase),
            new Regex(@"pinky|pinkie|little|small|小指", RegexOptions.IgnoreCase),
        };

        /// <summary>
        /// How far each finger of the mod's hand is curled in the idle pose (the hand holding this gun), from the
        /// joint positions: 0 straight, 1 a full fist. Null when the hand has too few recognisable fingers.
        /// </summary>
        private static float[] FingerCurls(Transform hand, PoseSampler sampler)
        {
            if (hand == null)
            {
                return null;
            }
            var curls = new float[5];
            int found = 0;
            for (int f = 0; f < 5; f++)
            {
                curls[f] = -1f;
                Transform joint = null;
                foreach (Transform c in hand.GetComponentsInChildren<Transform>(true))
                {
                    if (c != hand && FingerPatterns[f].IsMatch(c.name) && (c.parent == hand || !FingerPatterns[f].IsMatch(c.parent.name)))
                    {
                        joint = c;
                        break;
                    }
                }
                if (joint == null)
                {
                    continue;
                }
                var chain = new List<Vector3> { sampler.Idle(hand).position };
                for (Transform j = joint; j != null && chain.Count < 5;)
                {
                    chain.Add(sampler.Idle(j).position);
                    Transform next = null;
                    foreach (Transform c in j)
                    {
                        if (FingerPatterns[f].IsMatch(c.name) || j.childCount == 1)
                        {
                            next = c;
                            break;
                        }
                    }
                    j = next;
                }
                if (chain.Count < 3)
                {
                    continue;
                }
                float bend = 0f;
                for (int i = 1; i < chain.Count - 1; i++)
                {
                    Vector3 a = chain[i] - chain[i - 1];
                    Vector3 b = chain[i + 1] - chain[i];
                    if (a.sqrMagnitude > 1e-8f && b.sqrMagnitude > 1e-8f)
                    {
                        bend += Vector3.Angle(a, b);
                    }
                }
                // Full curl: about 90 + 100 + 70 degrees over knuckle, middle and end joints (thumb about 120).
                int joints = chain.Count - 2;
                float full = f == 0 ? 40f * joints : (joints >= 3 ? 260f : joints == 2 ? 190f : 90f);
                curls[f] = Mathf.Clamp01(bend / full);
                found++;
            }
            if (found < 3 && !(found == 2 && curls[1] >= 0f && curls[4] >= 0f))
            {
                return null;
            }
            // Ravenfield's own arms (and many mods) have index, pinky and thumb only: the hidden middle and ring
            // fingers sit between them, closer to the pinky that wraps the grip.
            if (curls[1] >= 0f && curls[4] >= 0f)
            {
                if (curls[2] < 0f) curls[2] = Mathf.Lerp(curls[1], curls[4], 0.6f);
                if (curls[3] < 0f) curls[3] = Mathf.Lerp(curls[1], curls[4], 0.85f);
            }
            return curls;
        }

        private static readonly Regex BeltName = new Regex(@"belt|(^|[^a-z])links?($|[^a-z])|lenta|лента|弹链|chain|ammo ?strip", RegexOptions.IgnoreCase);

        /// <summary>
        /// A belt-fed gun (a feed cover) often models the belt between the box and the feed as its own mesh, apart from the
        /// box: it stayed on the gun when the box came out. Belt-named visible parts near the feed tray or the box, not part
        /// of the box, the slide or the cover itself.
        /// </summary>
        private static void FindBeltParts(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler)
        {
            if (d.Hinge != HingeKind.FeedCover || d.MagazinePath == null)
            {
                return;
            }
            var box = new List<Transform>();
            Transform mag = WeaponRig.Resolve(root, d.MagazinePath);
            if (mag != null)
            {
                box.Add(mag);
            }
            foreach (int[] p in d.MagazinePartPaths)
            {
                Transform t = WeaponRig.Resolve(root, p);
                if (t != null)
                {
                    box.Add(t);
                }
            }
            Transform slide = WeaponRig.Resolve(root, d.SlidePath);
            Transform hinge = WeaponRig.Resolve(root, d.HingePath);
            Vector3 tray = Vector3.Lerp(d.HingePivot, d.HingeGrabPoint, 0.5f);
            Vector3 boxCentre = d.MagSeat.TransformPoint(d.MagBounds.center);
            var found = new List<Transform>();
            foreach (Transform t in all)
            {
                if (!BeltName.IsMatch(t.name) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones) || !ShownAtIdle(t, sampler))
                {
                    continue;
                }
                if (box.Any(b => Related(t, b)) || Related(t, slide) || hinge != null && t == hinge)
                {
                    continue;
                }
                Vector3 at = sampler.RelIdle[index[t]].position;
                Bounds b = sampler.LocalBounds(t);
                if (Mathf.Min(Vector3.Distance(at, tray), Vector3.Distance(at, boxCentre)) > 0.35f || b.size.magnitude > 0.6f)
                {
                    continue;
                }
                if (found.Any(f => t.IsChildOf(f)))
                {
                    continue;
                }
                found.RemoveAll(f => f.IsChildOf(t));
                found.Add(t);
            }
            foreach (Transform t in found)
            {
                d.BeltPartPaths.Add(WeaponRig.PathOf(root, t));
            }
            if (found.Count > 0)
            {
                d.Notes += $"belt in the model: {string.Join(", ", found.Select(f => f.name).ToArray())} (hidden while no belt is laid) ";
            }
        }

        private static readonly Regex CoverName = new Regex(@"cover|lid|tray|feed|deckel|couvercle|tapa|крышк|盖", RegexOptions.IgnoreCase);
        private static readonly Regex CylinderName = new Regex(@"cylinder|crane|drum|trommel|barillet|tambor|барабан|弹巢|转轮", RegexOptions.IgnoreCase);

        /// <summary>
        /// Parts the reload swings open in place (the shooting hand stays on the frame): break-action barrels that
        /// carry the muzzle and tip on a cross axis, a revolver crane swinging on an axis along the bore, a feed cover
        /// lifting on a cross axis on top of the receiver. Revolvers and break-actions become BreakOpen weapons.
        /// </summary>
        private static void DetectHinge(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, Weapon w, RigidPose gunIdle)
        {
            Weapon.Configuration c = w.configuration;
            bool breakCandidate = !c.auto && c.ammo >= 1 && c.ammo <= 4 && d.Action != ActionType.SelfLoading
                                  || !c.auto && c.ammo == 2;
            bool revolverCandidate = d.IsPistol && !c.auto && c.ammo >= 4 && c.ammo <= 9;
            Transform mag = WeaponRig.Resolve(root, d.MagazinePath);
            Transform slide = WeaponRig.Resolve(root, d.SlidePath);
            Transform handle = WeaponRig.Resolve(root, d.HandlePath);
            Vector3 lateral = Vector3.Cross(d.Up, d.AimAxis).normalized;

            Transform breakPart = null, cylinderPart = null, coverPart = null;
            float breakRot = 0f, cylinderRot = 0f, coverRot = 0f;
            bool spinsInFire = false;
            foreach (Transform t in all)
            {
                if (!IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones) || !sampler.IdleVisible(t))
                {
                    continue;
                }
                if (Related(t, slide) || Related(t, handle) || mag != null && (t == mag || t.IsChildOf(mag)))
                {
                    continue;
                }
                int i = index[t];
                // A revolver's cylinder turns one chamber per shot about the bore axis.
                if (revolverCandidate && sampler.FireRotMax[i] >= 20f && Mathf.Abs(Vector3.Dot(sampler.FireRotAxis[i], d.AimAxis)) > 0.8f
                    && sampler.FireMax[i] < 0.02f)
                {
                    spinsInFire = true;
                }
                if (!sampler.HasReload)
                {
                    continue;
                }
                float rot = sampler.ReloadRotMax[i];
                if (rot < 15f || sampler.ReloadMax[i] > 0.04f)
                {
                    continue; // a hinge turns in place
                }
                // The hinge itself, not a piece riding on it.
                if (t.parent != null && t.parent != gunBody && index.TryGetValue(t.parent, out int pi)
                    && IsPartCandidate(t.parent, root, gunBody, muzzle, armRelated, gunBones) && sampler.ReloadRotMax[pi] > rot * 0.6f)
                {
                    continue;
                }
                Vector3 axis = sampler.ReloadRotAxis[i];
                float acrossBore = Mathf.Abs(Vector3.Dot(axis, lateral));
                float alongBore = Mathf.Abs(Vector3.Dot(axis, d.AimAxis));
                bool carriesMuzzle = muzzle != null && muzzle != root && muzzle.IsChildOf(t);
                float aboveBore = Vector3.Dot(sampler.RelIdle[i].position - d.MuzzlePoint, d.Up);
                if (carriesMuzzle && acrossBore > 0.75f && breakCandidate)
                {
                    if (rot > breakRot)
                    {
                        breakRot = rot;
                        breakPart = t;
                    }
                }
                else if (!carriesMuzzle && alongBore > 0.75f && rot >= 40f && revolverCandidate)
                {
                    if (rot > cylinderRot)
                    {
                        cylinderRot = rot;
                        cylinderPart = t;
                    }
                }
                else if (!carriesMuzzle && acrossBore > 0.75f && rot >= 30f && aboveBore > -0.01f
                         && (c.ammo >= 40 || CoverName.IsMatch(t.name)))
                {
                    float score = rot * (CoverName.IsMatch(t.name) ? 2f : 1f);
                    if (score > coverRot)
                    {
                        coverRot = score;
                        coverPart = t;
                    }
                }
            }
            if (revolverCandidate && cylinderPart == null)
            {
                // A cylinder named as such that the reload never swings out still marks a revolver.
                foreach (Transform t in all)
                {
                    if (CylinderName.IsMatch(t.name) && IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                    {
                        spinsInFire = true;
                        break;
                    }
                }
            }

            Transform hinge = null;
            if (breakPart != null)
            {
                d.Hinge = HingeKind.BreakAction;
                hinge = breakPart;
            }
            else if (cylinderPart != null)
            {
                d.Hinge = HingeKind.Cylinder;
                hinge = cylinderPart;
            }
            else if (coverPart != null)
            {
                d.Hinge = HingeKind.FeedCover;
                hinge = coverPart;
            }

            if (d.Hinge == HingeKind.BreakAction || d.Hinge == HingeKind.Cylinder || revolverCandidate && spinsInFire)
            {
                d.Action = ActionType.BreakOpen;
                d.Notes += d.Hinge == HingeKind.BreakAction ? "break action " : "revolver ";
            }
            d.IsMachineGun = d.Hinge == HingeKind.FeedCover || c.auto && c.ammo >= 60 && !d.IsPistol;

            if (hinge == null)
            {
                return;
            }
            int hi = index[hinge];
            d.HingePath = WeaponRig.PathOf(root, hinge);
            d.SeatOnHinge = mag != null && mag.IsChildOf(hinge);
            d.HingeAxis = sampler.ReloadRotAxis[hi].normalized;
            d.HingePivot = sampler.RelIdle[hi].position;
            d.HingeOpenAngle = Mathf.Min(sampler.ReloadRotMax[hi], d.Hinge == HingeKind.Cylinder ? 100f : d.Hinge == HingeKind.BreakAction ? 70f : 110f);
            Bounds b = sampler.GunSpaceBounds(hinge, gunIdle);
            Vector3 center = b.size.sqrMagnitude > 1e-6f ? b.center : d.HingePivot;
            // A cover is lifted at its rear latch.
            d.HingeGrabPoint = center - d.AimAxis * Mathf.Abs(Vector3.Dot(b.extents, Abs(d.AimAxis))) * 0.6f + d.Up * 0.01f;

            // When it opens and closes in the mod's reload: these carry the open / close sounds.
            float[] angle = sampler.ReloadAngle[hi];
            float max = sampler.ReloadRotMax[hi];
            int open = -1, close = -1;
            for (int k = 0; k < angle.Length; k++)
            {
                if (open < 0 && angle[k] > max * 0.5f)
                {
                    open = k;
                }
                else if (open >= 0 && angle[k] < max * 0.3f)
                {
                    close = k;
                    break;
                }
            }
            if (d.Action == ActionType.BreakOpen)
            {
                d.TSlideBack = open >= 0 ? (open + 1) * PoseSampler.Dt : -1f;
                d.TSlideForward = close >= 0 ? (close + 1) * PoseSampler.Dt : -1f;
            }
        }

        private static bool Related(Transform t, Transform part) => part != null && (t == part || t.IsChildOf(part) || part.IsChildOf(t));

        private static Vector3 Abs(Vector3 v) => new Vector3(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

        /// <summary>
        /// Upper arm, forearm and hand of one first-person arm. The forearm is the first ancestor a bone-length above
        /// the hand and the upper arm the next one (twist and wrist helpers in between are skipped); the root is the
        /// topmost bone that belongs to this arm alone, which IK moves so the shoulder never tears.
        /// </summary>
        private static ArmChain ArmChainOf(Transform root, Transform hand, Transform otherHand, HashSet<Transform> armBones, PoseSampler sampler,
            RigidPose gunIdle, WeaponRigData d)
        {
            if (hand == null)
            {
                return null;
            }
            Transform Ancestor(Transform from, float minDistance)
            {
                for (Transform a = from.parent; a != null && a != root; a = a.parent)
                {
                    if (Vector3.Distance(sampler.Idle(a).position, sampler.Idle(from).position) >= minDistance)
                    {
                        return a;
                    }
                }
                return null;
            }
            Transform lower = Ancestor(hand, 0.12f);
            Transform upper = lower != null ? Ancestor(lower, 0.12f) : null;
            if (lower == null || upper == null)
            {
                return null;
            }
            Transform top = upper;
            while (top.parent != null && top.parent != root && armBones.Contains(top.parent) && (otherHand == null || !otherHand.IsChildOf(top.parent)))
            {
                top = top.parent;
            }
            // Grasp geometry in hand-bone space, from the idle pose.
            RigidPose handIdle = sampler.Idle(hand);
            RigidPose handInv = handIdle.Inverse();
            Vector3 palm = handInv.TransformPoint(PalmOf(hand, sampler));
            Vector3 fingers = palm.sqrMagnitude > 1e-6f ? palm.normalized : Vector3.forward;
            // The palm faces the gun it holds: the back of the hand points away from the grip / support point.
            Vector3 toGun = handInv.TransformDirection(gunIdle.TransformPoint(d.MuzzlePoint * 0.5f + d.GripPoint * 0.5f) - handIdle.position);
            Vector3 back = -Vector3.ProjectOnPlane(toGun, fingers);
            back = back.sqrMagnitude > 1e-6f ? back.normalized : Vector3.up;
            return new ArmChain
            {
                Root = WeaponRig.PathOf(root, top),
                Upper = WeaponRig.PathOf(root, upper),
                Lower = WeaponRig.PathOf(root, lower),
                Hand = WeaponRig.PathOf(root, hand),
                FingerDir = fingers,
                BackDir = back,
                PalmPoint = palm
            };
        }

        private static float MaxBoltTravel(WeaponRigData d) => d.IsPistol ? 0.07f : 0.2f;

        /// <summary>
        /// Many rifles never move their bolt in the fire clip; their charging handle only moves in the reload.
        /// It is the visible part near the receiver that goes straight back along the bore and returns home.
        /// </summary>
        private static Transform ChargingHandleFromReload(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root,
            Transform gunBody, Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, out Vector3 travel,
            Transform exclude = null)
        {
            travel = Vector3.zero;
            Transform best = null;
            float bestScore = 0f;
            foreach (Transform t in all)
            {
                if (!IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones) || !sampler.IdleVisible(t) || MagName.IsMatch(t.name))
                {
                    continue;
                }
                if (exclude != null && (t == exclude || t.IsChildOf(exclude) || exclude.IsChildOf(t)))
                {
                    continue;
                }
                int i = index[t];
                float m = sampler.ReloadMax[i];
                Vector3 rest = sampler.RelIdle[i].position;
                // Within the receiver area: bullpups carry the handle well ahead of the grip.
                if (m < 0.015f || m > MaxBoltTravel(d) * 1.2f || Vector3.Distance(rest, d.GripPoint) > 0.6f)
                {
                    continue;
                }
                Vector3[] traj = sampler.ReloadTrajectory[i];
                float back = 0f, side = 0f;
                int backStep = 0;
                for (int k = 0; k < traj.Length; k++)
                {
                    Vector3 off = traj[k] - rest;
                    float a = Vector3.Dot(off, -d.AimAxis);
                    if (a > back)
                    {
                        back = a;
                        backStep = k;
                    }
                    side = Mathf.Max(side, (off - -d.AimAxis * a).magnitude);
                }
                // Came home, or is named like a handle, or ran dead straight along the bore (the clip may end
                // before the handle returns).
                bool cameHome = SlideName.IsMatch(t.name) || side < back * 0.2f;
                for (int k = backStep + 1; k < traj.Length && !cameHome; k++)
                {
                    cameHome = (traj[k] - rest).magnitude < 0.02f;
                }
                if (!cameHome)
                {
                    continue; // went back and stayed: not a handle that was worked
                }
                if (back < 0.015f || side > back * 0.5f)
                {
                    continue; // must travel (mostly) straight back
                }
                float score = back * (SlideName.IsMatch(t.name) ? 2f : 1f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = t;
                    travel = -d.AimAxis * back;
                }
            }
            if (best != null)
            {
                // Climb while the parent moves the same way (handle + its mesh).
                float m0 = sampler.ReloadMax[index[best]];
                for (Transform p = best.parent; p != null && p != gunBody && p != root; p = p.parent)
                {
                    if (!index.TryGetValue(p, out int pi) || armRelated.Contains(p) || sampler.ReloadMax[pi] < m0 * 0.8f || MagName.IsMatch(p.name))
                    {
                        break;
                    }
                    best = p;
                }
            }
            return best;
        }

        /// <summary>
        /// When the slide/bolt is fully back and when it slams home, from the last rearward stroke that returns
        /// home. A slide already locked back when the clip starts only gets a home time (slide release).
        /// </summary>
        private static void SlideKeyframes(Vector3[] trajectory, Vector3 rest, Vector3 travelDir, out float back, out float forward)
        {
            back = forward = -1f;
            int n = trajectory.Length;
            var along = new float[n];
            float max = 0f;
            for (int k = 0; k < n; k++)
            {
                float a = Vector3.Dot(trajectory[k] - rest, travelDir);
                // Parts scaled to zero while hidden sample as NaN; they count as "at rest".
                along[k] = float.IsNaN(a) || float.IsInfinity(a) ? 0f : a;
                if (along[k] > max)
                {
                    max = along[k];
                }
            }
            if (!(max >= 0.008f))
            {
                return;
            }
            float low = max * 0.25f, high = max * 0.6f;
            bool returned = false;
            int i = 0;
            while (i < n)
            {
                if (along[i] < high)
                {
                    i++;
                    continue;
                }
                int start = i;
                while (start > 0 && along[start - 1] >= low)
                {
                    start--;
                }
                int end = i;
                while (end < n && along[end] >= low)
                {
                    end++;
                }
                // Back within the clip's first moments = the crossfade into a locked-open pose, not a rack.
                bool lockedAtStart = start * PoseSampler.Dt <= 0.15f;
                float strokeMax = 0f;
                for (int k = start; k < end; k++)
                {
                    strokeMax = Mathf.Max(strokeMax, along[k]);
                }
                int peak = start;
                while (peak < end - 1 && along[peak] < strokeMax * 0.9f)
                {
                    peak++;
                }
                float strokeBack = lockedAtStart ? -1f : (peak + 1) * PoseSampler.Dt;
                if (end < n)
                {
                    back = strokeBack;
                    forward = (end + 1) * PoseSampler.Dt;
                    returned = true;
                }
                else if (!returned)
                {
                    back = strokeBack;
                }
                i = Mathf.Max(end, i + 1);
            }
        }

        // ---- magazine ---------------------------------------------------------------------------------

        private static void AnalyzeMagazine(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, Transform slide, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, Weapon w)
        {
            Transform mag = null;
            bool toggles = false;
            if (sampler.HasReload)
            {
                // 1. Parts that travel during the reload.
                float best = 0f;
                Transform skippedHelper = null;
                foreach (Transform t in all)
                {
                    if (!MagCandidate(t, root, gunBody, muzzle, slide, armRelated, gunBones))
                    {
                        continue;
                    }
                    int i = index[t];
                    float m = sampler.ReloadMax[i];
                    if (m < 0.03f || m > 1.5f)
                    {
                        continue; // still, or parked far away while hidden (belt links, spare rounds)
                    }
                    if (!ShownAtIdle(t, sampler))
                    {
                        // A reload helper the animation only shows while reloading (the RPG-7's "Reload_Cube": an 80 cm box
                        // around the tube). Taken for the rocket it was copied at full size into the pouch and the hand,
                        // where the gun's own animation no longer hid it.
                        skippedHelper = skippedHelper ?? t;
                        continue;
                    }
                    if (OpticName.IsMatch(t.name) && !MagName.IsMatch(t.name) || sampler.LocalBounds(t).size.magnitude > 2.2f)
                    {
                        // A sight that comes off in the reload (a Javelin's command unit), or nothing a hand could hold. Not
                        // tighter: an RPG-7V's rocket holder with its rocket measures 1.7 m (it fell to a piece of the scope).
                        continue;
                    }
                    Vector3 idlePos = sampler.RelIdle[i].position;
                    bool nearGun = Vector3.Distance(idlePos, d.GripPoint) < 0.45f;
                    float score = Mathf.Min(m, 0.4f) * (IsMagName(t.name) ? 3f : NotMagName.IsMatch(t.name) ? 0.3f : 1f) * (nearGun ? 1f : 0.2f);
                    if (score > best)
                    {
                        best = score;
                        mag = t;
                    }
                }
                if (skippedHelper != null)
                {
                    d.Notes += $"'{skippedHelper.name}' moves in the reload but is hidden at rest (helper, not the magazine) ";
                }
                if (mag != null)
                {
                    for (Transform p = mag.parent; p != null && p != gunBody && p != root; p = p.parent)
                    {
                        if (!index.TryGetValue(p, out int pi) || armRelated.Contains(p) || (slide != null && p == slide)
                            || sampler.ReloadMax[pi] < sampler.ReloadMax[index[mag]] * 0.8f || LooksLikeAWeapon(p)
                            || OpticName.IsMatch(p.name) && !MagName.IsMatch(p.name))
                        {
                            break; // never up into a sight the reload moves along (an RPG-7V's "shmelscope")
                        }
                        mag = p;
                    }
                    d.MagMethod = "reload motion";
                }

                // 2. Parts the reload hides and shows again (swap-style animations).
                if (mag == null)
                {
                    float bestHidden = 0f;
                    foreach (Transform t in all)
                    {
                        if (!MagCandidate(t, root, gunBody, muzzle, slide, armRelated, gunBones) || !sampler.IdleVisible(t))
                        {
                            continue;
                        }
                        int i = index[t];
                        if (Vector3.Distance(sampler.RelIdle[i].position, d.GripPoint) > 0.45f)
                        {
                            continue;
                        }
                        float hidden = HiddenSeconds(sampler.ReloadVisible[i]);
                        float score = hidden * (IsMagName(t.name) ? 3f : NotMagName.IsMatch(t.name) ? 0.3f : 1f);
                        if (hidden >= 0.15f && score > bestHidden)
                        {
                            bestHidden = score;
                            mag = t;
                        }
                    }
                    if (mag != null)
                    {
                        // Climb while the parent hides together with it.
                        for (Transform p = mag.parent; p != null && p != gunBody && p != root; p = p.parent)
                        {
                            if (!index.TryGetValue(p, out int pi) || armRelated.Contains(p) || HiddenSeconds(sampler.ReloadVisible[pi]) < 0.15f || LooksLikeAWeapon(p))
                            {
                                break;
                            }
                            mag = p;
                        }
                        toggles = true;
                        d.MagMethod = "reload visibility";
                    }
                }
            }
            if (mag == null)
            {
                mag = FindMagByName(all, root, gunBody, muzzle, armRelated, gunBones);
                if (mag != null && slide != null && (mag == slide || mag.IsChildOf(slide)))
                {
                    mag = null;
                }
                if (mag != null && !ShownAtIdle(mag, sampler))
                {
                    d.Notes += $"'{mag.name}' is named like a magazine but hidden at rest (not used) ";
                    mag = null;
                }
                float nameMagSize = mag != null ? sampler.LocalBounds(mag).size.magnitude : 0f;
                if (nameMagSize > 1e-4f && nameMagSize < 0.05f)
                {
                    mag = null; // a shell casing or a screw by its name: no magazine is that small (an invisible well is used)
                }
                if (mag != null)
                {
                    d.MagMethod = "name '" + mag.name + "'";
                }
            }
            if (mag == null)
            {
                if (d.Action == ActionType.SelfLoading || d.Action == ActionType.Manual && w.configuration.ammo > 1 && !w.configuration.advancedReload)
                {
                    d.Warnings.Add("no magazine found: insertion uses an invisible magazine well");
                }
                return;
            }

            int mi = index[mag];
            d.MagazinePath = WeaponRig.PathOf(root, mag);
            d.MagazineToggles = toggles;
            d.MagazineIsSkinned = !HasRendererInSubtree(mag) && gunBones.Contains(mag);
            if (d.MagMethod == "reload motion" && d.MagazineIsSkinned)
            {
                d.MagMethod = "reload motion (skinned bone)";
            }
            d.MagSeat = sampler.RelIdle[mi];
            Vector3 eject = sampler.HasReload && !toggles ? sampler.FirstDir[mi] : Vector3.zero;
            d.MagEjectAxis = eject.sqrMagnitude > 0.5f ? eject.normalized : -d.Up;
            Bounds mb = sampler.LocalBounds(mag);
            if (mb.size.sqrMagnitude > 1e-6f)
            {
                d.MagBounds = mb;
                MagAxisFromShape(d, mb, eject.sqrMagnitude > 0.5f);
            }

            if (sampler.HasReload)
            {
                CollectMagazineParts(d, all, index, root, gunBody, muzzle, slide, mag, armRelated, gunBones, sampler, toggles);
                MagKeyframes(d, all, index, root, gunBody, muzzle, slide, mag, armRelated, gunBones, sampler, toggles);
            }
        }

        /// <summary>
        /// A box magazine leaves its well along its own length. The reload animation's first motion is often something
        /// else (an AK magazine rocked forward off its front lug, a hand that tugs it sideways first), and an insert axis
        /// taken from that tilts the magazine in the hand and makes it go in crooked. A clearly elongated magazine that
        /// hangs below the gun uses its own long axis, pointing out of the gun; drums and boxes keep the animated one.
        /// </summary>
        private static void MagAxisFromShape(WeaponRigData d, Bounds local, bool animated)
        {
            // Out of the gun means away from the bore, toward the side the magazine hangs on (below for almost every gun,
            // above for a top-fed one). An animation whose first motion nudges the magazine up into the well before pulling
            // it would otherwise send an ejected magazine up through the grip, where it never comes out.
            Vector3 centreGun = d.MagSeat.TransformPoint(local.center);
            Vector3 fromBore = Vector3.ProjectOnPlane(centreGun - d.MuzzlePoint, d.AimAxis);
            Vector3 outward = fromBore.sqrMagnitude > 1e-4f ? fromBore.normalized : -d.Up;
            // Only for a magazine that clearly hangs off the bore (shells and rockets load along it) and a pull that clearly
            // points into the gun.
            if (fromBore.magnitude > 0.025f && Vector3.Dot(d.MagEjectAxis, outward) < -0.2f)
            {
                d.MagEjectAxis = Vector3.Reflect(d.MagEjectAxis, outward).normalized;
                d.Notes += "magazine pull pointed into the gun (turned around) ";
            }

            Vector3 s = local.size;
            int longest = s.x >= s.y && s.x >= s.z ? 0 : s.y >= s.z ? 1 : 2;
            float second = longest == 0 ? Mathf.Max(s.y, s.z) : longest == 1 ? Mathf.Max(s.x, s.z) : Mathf.Max(s.x, s.y);
            if (s[longest] < 0.05f || s[longest] < second * 1.5f)
            {
                return; // short, a drum or a block: no reliable axis of its own
            }
            Vector3 axisLocal = Vector3.zero;
            axisLocal[longest] = 1f;
            Vector3 axis = (d.MagSeat.rotation * axisLocal).normalized;
            if (Vector3.Dot(axis, outward) < 0f)
            {
                axis = -axis;
            }
            if (Vector3.Angle(axis, outward) > 60f)
            {
                return; // lies along the gun rather than hanging off it: trust the animation
            }
            if (animated && Vector3.Angle(axis, d.MagEjectAxis) > 8f)
            {
                d.Notes += $"magazine axis from its shape ({Vector3.Angle(axis, d.MagEjectAxis):0}° off the animated pull) ";
            }
            d.MagEjectAxis = axis;
        }

        /// <summary>How much of the seated magazine sticks out of the gun's own meshes along the eject axis.</summary>
        private static void MeasureMagazineProtrusion(WeaponRigData d, Transform root, Transform gunBody, HashSet<Transform> armRelated,
            HashSet<Renderer> armRenderers, HashSet<Transform> gunBones, PoseSampler sampler)
        {
            Transform mag = WeaponRig.Resolve(root, d.MagazinePath);
            if (mag == null)
            {
                return;
            }
            if (d.MagazineIsSkinned)
            {
                return; // part of the gun's own skinned mesh: bounds cannot tell them apart
            }
            RigidPose gunIdle = sampler.Idle(gunBody);
            var magParts = new List<Transform> { mag };
            foreach (int[] path in d.MagazinePartPaths)
            {
                Transform part = WeaponRig.Resolve(root, path);
                if (part != null)
                {
                    magParts.Add(part);
                }
            }
            // The gun's own meshes only: under the gun body, or skinned to its bones; never arms, helpers or the magazine.
            bool GunMesh(Transform t)
            {
                Renderer r = t.GetComponent<Renderer>();
                if (r == null || !r.enabled || armRenderers.Contains(r) || armRelated.Contains(t) || !sampler.IdleVisible(t) || CameraName.IsMatch(t.name))
                {
                    return false;
                }
                if (magParts.Any(m => t == m || t.IsChildOf(m)))
                {
                    return false;
                }
                if (t.IsChildOf(gunBody))
                {
                    return true;
                }
                var smr = r as SkinnedMeshRenderer;
                return smr != null && smr.bones != null && smr.bones.Any(b => b != null && (b == gunBody || gunBones.Contains(b) && !magParts.Any(m => b == m || b.IsChildOf(m))));
            }
            Bounds gun = sampler.GunSpaceBoundsWhere(root, gunIdle, t => !GunMesh(t));
            // The magazine in gun space: its own meshes, or its measured bounds at the seat for skinned magazines.
            Bounds mb = sampler.GunSpaceBounds(mag, gunIdle);
            if (mb.size.sqrMagnitude < 1e-6f)
            {
                mb = new Bounds(d.MagSeat.TransformPoint(d.MagBounds.center), Vector3.zero);
                foreach (Vector3 corner in Corners(d.MagBounds))
                {
                    mb.Encapsulate(d.MagSeat.TransformPoint(corner));
                }
            }
            if (gun.size.sqrMagnitude < 1e-6f || mb.size.sqrMagnitude < 1e-6f)
            {
                return;
            }
            Vector3 e = d.MagEjectAxis.normalized;
            float gunFar = MaxAlong(gun, e), magFar = MaxAlong(mb, e), magNear = -MaxAlong(mb, -e);
            float length = Mathf.Max(1e-3f, magFar - magNear);
            // Whole-mesh bounds also reach the stock and grip far from the well, and merged gun+magazine meshes
            // enclose the magazine, so this is shown in the report but does not decide the kick (see MagKickRule).
            d.MagProtrusion = Mathf.Clamp01((magFar - gunFar) / length);
        }

        private static readonly Regex DeepWellName = new Regex(@"vector|kriss", RegexOptions.IgnoreCase);

        /// <summary>
        /// A fresh magazine can knock the seated one out unless the well swallows it: pistols and magazine-in-grip
        /// designs (the magazine top sits at the grip), and KRISS Vector-style long wells. Per-weapon overrides win.
        /// </summary>
        private static void MagKickRule(WeaponRigData d, Weapon w)
        {
            if (d.MagazinePath == null && d.Action != ActionType.SelfLoading)
            {
                return;
            }
            // Where the magazine is, not where its transform's pivot is (many rigs pivot every part at the gun's origin).
            Vector3 magCentre = d.MagSeat.TransformPoint(d.MagBounds.center);
            // Only a grip the weapon's own hand measured can say the magazine is in it: an estimated grip on a long rifle can
            // land on the magazine well and wrongly made M4s "not kickable".
            bool gripMeasured = d.HandsMethod != "estimated";
            bool inGrip = gripMeasured && Mathf.Abs(Vector3.Dot(magCentre - d.GripPoint, d.AimAxis)) < 0.05f;
            // A handgun by its slot. A rifle whose support hand holds the magazine well is marked "pistol" (no handguard to
            // steer by), but its magazine still hangs out of the well in front of the grip.
            bool handgun = w.slot == 1 || d.IsPistol && w.slot != 0 && w.slot != 3;
            string names = w.name + " " + (w.weaponEntry != null ? w.weaponEntry.name : "");
            bool deepWell = DeepWellName.IsMatch(names);
            d.MagKickable = !handgun && !inGrip && !deepWell;
            if (!d.MagKickable)
            {
                d.Notes += $"mag kick off ({(handgun ? "handgun" : inGrip ? "magazine in the grip" : "deep well")}) ";
            }
        }

        private static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (int i = 0; i < 8; i++)
            {
                yield return b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            }
        }

        private static float MaxAlong(Bounds b, Vector3 dir)
        {
            float max = float.MinValue;
            foreach (Vector3 c in Corners(b))
            {
                max = Mathf.Max(max, Vector3.Dot(c, dir));
            }
            return max;
        }

        private static bool MagCandidate(Transform t, Transform root, Transform gunBody, Transform muzzle, Transform slide,
            HashSet<Transform> armRelated, HashSet<Transform> gunBones)
        {
            if (!IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
            {
                return false;
            }
            if (SlideName.IsMatch(t.name) && !MagName.IsMatch(t.name))
            {
                return false; // bolts, slides, charging handles
            }
            if (LooksLikeAWeapon(t))
            {
                return false; // akimbo: the second pistol moves in the reload too, but it is no magazine
            }
            for (Transform p = t.parent; p != null && p != root; p = p.parent)
            {
                if (SecondWeaponName.IsMatch(p.name) && !MagName.IsMatch(p.name))
                {
                    return false; // part of the other gun of a pair (even its own magazine is not this gun's)
                }
            }
            return slide == null || (t != slide && !t.IsChildOf(slide) && !slide.IsChildOf(t));
        }

        private static readonly Regex SecondWeaponName = new Regex(@"dual|akimbo|off ?hand|second(ary)? ?(gun|pistol|weapon)", RegexOptions.IgnoreCase);
        // Parts of a gun a magazine never has. ("Receiver" is left out: exported meshes are often named after the
        // receiver asset they came from, magazines included.)
        private static readonly Regex WeaponPartName = new Regex(@"slide|bolt|barrel|trigger|hammer|laser|muzzle", RegexOptions.IgnoreCase);

        /// <summary>
        /// A whole (second) weapon rather than a magazine: named as the other gun of a pair, or holding parts no magazine
        /// has (a slide, barrel, trigger, laser...). Taking it for the magazine dropped the second pistol on every reload.
        /// </summary>
        private static bool LooksLikeAWeapon(Transform t)
        {
            if (SecondWeaponName.IsMatch(t.name) && !MagName.IsMatch(t.name))
            {
                return true;
            }
            // A whole gun has several such parts; one stray name (a mesh exported under a part's name) is not enough.
            var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Transform c in t.GetComponentsInChildren<Transform>(true))
            {
                if (c == t || MagName.IsMatch(c.name))
                {
                    continue;
                }
                foreach (Match m in WeaponPartName.Matches(c.name))
                {
                    kinds.Add(m.Value);
                }
            }
            return kinds.Count >= 2;
        }

        private static float HiddenSeconds(bool[] visible)
        {
            if (visible == null)
            {
                return 0f;
            }
            int hidden = 0;
            foreach (bool v in visible)
            {
                if (!v)
                {
                    hidden++;
                }
            }
            return hidden * PoseSampler.Dt;
        }

        /// <summary>Separate objects that travel (or vanish) together with the magazine: rounds, follower, base plate.</summary>
        private static void CollectMagazineParts(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, Transform slide, Transform mag, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, bool toggles)
        {
            int mi = index[mag];
            Vector3[] magTraj = sampler.ReloadTrajectory[mi];
            foreach (Transform t in all)
            {
                if (t == mag || t.IsChildOf(mag) || mag.IsChildOf(t) || !MagCandidate(t, root, gunBody, muzzle, slide, armRelated, gunBones))
                {
                    continue;
                }
                int i = index[t];
                if (!sampler.IdleVisible(t) || Vector3.Distance(sampler.RelIdle[i].position, sampler.RelIdle[mi].position) > 0.15f)
                {
                    continue;
                }
                bool together;
                if (toggles)
                {
                    together = HiddenSeconds(sampler.ReloadVisible[i]) >= 0.15f && SameHiddenWindows(sampler.ReloadVisible[i], sampler.ReloadVisible[mi]);
                }
                else
                {
                    if (sampler.ReloadMax[i] < 0.03f)
                    {
                        continue;
                    }
                    Vector3 offsetIdle = sampler.RelIdle[i].position - sampler.RelIdle[mi].position;
                    float worst = 0f;
                    Vector3[] traj = sampler.ReloadTrajectory[i];
                    for (int k = 0; k < traj.Length; k++)
                    {
                        if (!sampler.ReloadVisible[i][k])
                        {
                            continue;
                        }
                        worst = Mathf.Max(worst, ((traj[k] - magTraj[k]) - offsetIdle).magnitude);
                    }
                    together = worst < 0.012f;
                }
                if (together)
                {
                    // Skip if an ancestor already made the list.
                    bool covered = false;
                    foreach (int[] p in d.MagazinePartPaths)
                    {
                        Transform other = WeaponRig.Resolve(root, p);
                        if (other != null && t.IsChildOf(other))
                        {
                            covered = true;
                            break;
                        }
                    }
                    if (!covered)
                    {
                        d.MagazinePartPaths.Add(WeaponRig.PathOf(root, t));
                    }
                }
            }
        }

        private static bool SameHiddenWindows(bool[] a, bool[] b)
        {
            int mismatches = 0;
            for (int k = 0; k < a.Length && k < b.Length; k++)
            {
                if (a[k] != b[k])
                {
                    mismatches++;
                }
            }
            return mismatches <= 3;
        }

        /// <summary>When in the mod's reload the magazine leaves and when one is seated again.</summary>
        private static void MagKeyframes(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, Transform slide, Transform mag, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, bool toggles)
        {
            int mi = index[mag];
            Vector3 seat = sampler.RelIdle[mi].position;
            Vector3[] traj = sampler.ReloadTrajectory[mi];
            bool[] vis = sampler.ReloadVisible[mi];
            int steps = traj.Length;
            d.ReloadDuration = steps * PoseSampler.Dt;

            int outStep = -1;
            for (int k = 0; k < steps; k++)
            {
                bool gone = toggles ? !vis[k] : (traj[k] - seat).magnitude > 0.015f || !vis[k];
                if (gone)
                {
                    outStep = k;
                    break;
                }
            }
            if (outStep < 0)
            {
                return;
            }
            d.TMagOut = (outStep + 1) * PoseSampler.Dt;

            // Same magazine coming home (or reappearing)...
            int peak = outStep;
            float peakDist = 0f;
            for (int k = outStep; k < steps; k++)
            {
                float dist = (traj[k] - seat).magnitude;
                if (dist > peakDist)
                {
                    peakDist = dist;
                    peak = k;
                }
            }
            for (int k = toggles ? outStep : peak; k < steps; k++)
            {
                bool home = toggles ? vis[k] : vis[k] && (traj[k] - seat).magnitude < 0.008f;
                if (home)
                {
                    d.TMagIn = (k + 1) * PoseSampler.Dt;
                    return;
                }
            }
            // ...or a second magazine arriving at the seat.
            foreach (Transform t in all)
            {
                if (t == mag || t.IsChildOf(mag) || !MagCandidate(t, root, gunBody, muzzle, slide, armRelated, gunBones))
                {
                    continue;
                }
                int i = index[t];
                if (sampler.ReloadMax[i] < 0.03f)
                {
                    continue;
                }
                Vector3[] other = sampler.ReloadTrajectory[i];
                for (int k = outStep + 1; k < steps; k++)
                {
                    if (sampler.ReloadVisible[i][k] && (other[k] - seat).magnitude < 0.015f)
                    {
                        d.TMagIn = (k + 1) * PoseSampler.Dt;
                        return;
                    }
                }
            }
        }

        // ---- sounds -----------------------------------------------------------------------------------

        /// <summary>
        /// Binds the mod's own reload/fire sounds to manual actions by matching the time each sound plays in the
        /// mod's animation to the time the matching part moves (mag leaves, mag seats, slide back, slide home).
        /// </summary>
        private static void BuildSoundMap(WeaponRigData d, PoseSampler sampler, Weapon w)
        {
            GameObject eventTarget = w.animator != null ? w.animator.gameObject : w.gameObject;
            _bank = eventTarget.GetComponent<SoundBank>();
            var keyframes = new List<KeyValuePair<MechSound, float>>();
            if (d.TMagOut >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.MagOut, d.TMagOut));
            if (d.TMagIn >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.MagIn, d.TMagIn));
            if (d.TSlideBack >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.SlideBack, d.TSlideBack));
            if (d.TSlideForward >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.SlideForward, d.TSlideForward));

            var reloadCues = new List<SampledCue>(sampler.ReloadCues);
            reloadCues.Sort((a, b) => a.Time.CompareTo(b.Time));
            if (keyframes.Count > 0)
            {
                MatchCues(d, keyframes, reloadCues);
            }
            else if (reloadCues.Count > 0)
            {
                // No part motion to align with: the usual order is mag out, mag in, then the slide.
                MechSound[] order = { MechSound.MagOut, MechSound.MagIn, MechSound.SlideBack, MechSound.SlideForward };
                for (int i = 0; i < reloadCues.Count && i < order.Length; i++)
                {
                    AddCue(d, order[i], ToCue(reloadCues[i]));
                }
            }

            // Fire animation: sounds at the shot stay with the shot; later ones are the action cycling.
            foreach (SampledCue c in sampler.FireCues)
            {
                if (c.Time <= 0.1f)
                {
                    d.ShotCues.Add(ToCue(c));
                }
                else if (d.Action == ActionType.Manual)
                {
                    bool nearForward = d.TFireSlideForward >= 0f && Mathf.Abs(c.Time - d.TFireSlideForward) < Mathf.Abs(c.Time - Mathf.Max(0f, d.TFireSlideBack));
                    MechSound kind = nearForward ? MechSound.SlideForward : MechSound.SlideBack;
                    if (!d.Cues.ContainsKey(kind))
                    {
                        AddCue(d, kind, ToCue(c));
                    }
                }
            }

            // The single reload clip: slice it at the keyframes (never played whole in manual mode).
            AudioClip clip = w.reloadAudio != null ? w.reloadAudio.clip : null;
            if (clip != null && keyframes.Count > 0)
            {
                keyframes.Sort((a, b) => a.Value.CompareTo(b.Value));
                for (int i = 0; i < keyframes.Count; i++)
                {
                    MechSound kind = keyframes[i].Key;
                    if (d.Cues.ContainsKey(kind))
                    {
                        continue;
                    }
                    float t = keyframes[i].Value;
                    float next = i + 1 < keyframes.Count ? keyframes[i + 1].Value : clip.length;
                    float start = Mathf.Max(0f, t - 0.06f);
                    float end = Mathf.Min(clip.length, Mathf.Min(next - 0.02f, t + 0.5f));
                    AudioSlicing.Refine(clip, t, ref start, ref end);
                    if (end - start >= 0.08f && start < clip.length)
                    {
                        AddCue(d, kind, new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = start, Length = end - start, SourceTime = t });
                    }
                }
            }
        }

        private struct CueMatch
        {
            public float Cost;
            public int Keyframe;
            public int Cue;
        }

        /// <summary>
        /// One sound per action: each keyframe gets the cue that best fits it. Sounds lead the motion they belong
        /// to (the click comes as the part arrives), so a cue up to 0.6 s before a keyframe fits, while one after
        /// it fits only within 0.15 s and costs more. Near ties go to the usual reload order.
        /// </summary>
        private static void MatchCues(WeaponRigData d, List<KeyValuePair<MechSound, float>> keyframes, List<SampledCue> cues)
        {
            const float lead = 0.6f, lag = 0.15f;
            var matches = new List<CueMatch>();
            for (int k = 0; k < keyframes.Count; k++)
            {
                for (int c = 0; c < cues.Count; c++)
                {
                    float early = keyframes[k].Value - cues[c].Time;
                    if (early > lead || early < -lag)
                    {
                        continue;
                    }
                    float cost = early >= 0f ? early : -early * 2.5f;
                    matches.Add(new CueMatch { Cost = cost + OrderRank(keyframes[k].Key) * 0.02f, Keyframe = k, Cue = c });
                }
            }
            matches.Sort((a, b) => a.Cost.CompareTo(b.Cost));
            var keyUsed = new bool[keyframes.Count];
            var cueUsed = new bool[cues.Count];
            foreach (CueMatch m in matches)
            {
                if (keyUsed[m.Keyframe] || cueUsed[m.Cue])
                {
                    continue;
                }
                keyUsed[m.Keyframe] = cueUsed[m.Cue] = true;
                AddCue(d, keyframes[m.Keyframe].Key, ToCue(cues[m.Cue]));
            }
        }

        private static int OrderRank(MechSound kind)
        {
            switch (kind)
            {
                case MechSound.MagOut: return 0;
                case MechSound.MagIn: return 1;
                case MechSound.SlideForward: return 2;
                default: return 3;
            }
        }

        /// <summary>The weapon whose sound bank events are being resolved (set by BuildSoundMap).</summary>
        [ThreadStatic] private static SoundBank _bank;

        private static SoundCue ToCue(SampledCue c)
        {
            if (c.AudioObjectPath != null)
            {
                return new SoundCue { Kind = CueKind.AudioObject, Path = c.AudioObjectPath, SourceTime = c.Time };
            }
            var cue = new SoundCue
            {
                Kind = CueKind.Event,
                Function = c.Function,
                IntParameter = c.IntParameter,
                FloatParameter = c.FloatParameter,
                StringParameter = c.StringParameter,
                SourceTime = c.Time
            };
            // Animation events reach every component on the animator's object; its SoundBank holds the clips.
            if (_bank != null && _bank.clips != null && _bank.clips.Length > 0)
            {
                if (c.Function == "PlaySoundBank")
                {
                    if (c.IntParameter >= 0 && c.IntParameter < _bank.clips.Length && _bank.clips[c.IntParameter] != null)
                    {
                        cue.Clips = new[] { _bank.clips[c.IntParameter] };
                    }
                }
                else if (c.Function == "PlayRandom" || c.Function == "PlayNext")
                {
                    var clips = new List<AudioClip>();
                    foreach (AudioClip clip in _bank.clips)
                    {
                        if (clip != null)
                        {
                            clips.Add(clip);
                        }
                    }
                    cue.Clips = clips.Count > 0 ? clips.ToArray() : null;
                }
                cue.Volume = _bank.clipVolume;
            }
            return cue;
        }

        private static void AddCue(WeaponRigData d, MechSound kind, SoundCue cue)
        {
            if (!d.Cues.TryGetValue(kind, out List<SoundCue> list))
            {
                list = new List<SoundCue>();
                d.Cues[kind] = list;
            }
            list.Add(cue);
        }

        // ---- shared helpers ---------------------------------------------------------------------------

        /// <summary>Fills in grab points for parts that were not found so every interaction still works.</summary>
        private static void FinishVirtualParts(WeaponRigData d)
        {
            if (d.SlidePath == null && !d.RackFromHand)
            {
                bool pump = d.Action == ActionType.Manual && d.HasSupportPoint && !d.IsPistol;
                d.RackPoint = pump
                    ? d.SupportPoint
                    : d.GripPoint + d.Up * (d.IsPistol ? 0.07f : 0.08f) + d.AimAxis * (d.IsPistol ? -0.02f : 0.03f);
                d.SlideTravel = -d.AimAxis * (d.IsPistol ? 0.035f : 0.07f);
                d.RackOnSurface = !pump; // an estimate above the grip: put on top of the receiver once the model is measured
            }
            if (d.MagazinePath == null)
            {
                Vector3 seat = d.Action == ActionType.SingleShot
                    ? d.MuzzlePoint - d.AimAxis * 0.05f
                    : d.GripPoint + d.AimAxis * (d.IsPistol ? 0.0f : 0.1f) - d.Up * (d.IsPistol ? 0.02f : 0.05f);
                d.MagSeat = new RigidPose(seat, Quaternion.LookRotation(d.AimAxis, d.Up));
                d.MagEjectAxis = d.Action == ActionType.SingleShot ? d.AimAxis : -d.Up;
            }
            d.LockOnEmpty = d.Action == ActionType.SelfLoading && (d.SlidePath != null || d.IsPistol);
            if (d.Action == ActionType.BreakOpen)
            {
                d.Warnings.RemoveAll(w => w.StartsWith("no moving slide/bolt", StringComparison.Ordinal));
            }
        }

        private static void CheckSanity(WeaponRigData d, Transform root, Transform gunBody, HashSet<Transform> armRelated)
        {
            float gripToMuzzle = Vector3.Distance(d.GripPoint, d.MuzzlePoint);
            if (gripToMuzzle > 2f)
            {
                d.Warnings.Add($"grip is {gripToMuzzle:0.0} m from the muzzle (very large model or misplaced muzzle)");
            }
            if (Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis) < -0.05f && d.Action != ActionType.Melee && d.Action != ActionType.Throwable)
            {
                d.Warnings.Add("muzzle sits behind the grip along the barrel axis (check muzzle placement)");
            }
            if (Vector3.Distance(d.MagSeat.position, d.GripPoint) > 0.6f && d.MagazinePath != null)
            {
                d.Warnings.Add("magazine sits far from the grip; the detected part may be wrong");
            }
        }

        private static bool HasOwnRenderer(Transform t) => t.GetComponent<Renderer>() != null;

        private static bool HasRendererInSubtree(Transform t) =>
            t.GetComponentInChildren<MeshRenderer>(true) != null || t.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;

        private static bool IsPartCandidate(Transform t, Transform root, Transform gunBody, Transform muzzle,
            HashSet<Transform> armRelated, HashSet<Transform> gunBones)
        {
            if (t == root || t == gunBody || armRelated.Contains(t) || gunBody.IsChildOf(t) || CameraName.IsMatch(t.name))
            {
                return false;
            }
            if (muzzle != null && muzzle != gunBody && (t == muzzle || t.IsChildOf(muzzle)))
            {
                return false;
            }
            if (t.GetComponent<ParticleSystem>() != null || t.GetComponent<Camera>() != null || t.GetComponent<Light>() != null
                || t.GetComponent<AudioSource>() != null && !HasRendererInSubtree(t))
            {
                return false;
            }
            if (t.parent == root && !gunBones.Contains(t) && !MagName.IsMatch(t.name) && !SlideName.IsMatch(t.name))
            {
                return false; // loose helper meshes at the rig root ("Cube", shadow casters) follow the rig, not the gun
            }
            // Either it carries a mesh, or it is a bone that deforms the (skinned) gun mesh.
            return HasRendererInSubtree(t) || gunBones.Contains(t);
        }

        private static Transform FindMagByName(Transform[] all, Transform root, Transform gunBody, Transform muzzle,
            HashSet<Transform> armRelated, HashSet<Transform> gunBones)
        {
            foreach (Transform t in all)
            {
                if (IsMagName(t.name) && IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                {
                    return t;
                }
            }
            return null;
        }

        private static Transform FindByName(Transform[] all, Regex rx, Transform root, Transform gunBody, Transform muzzle,
            HashSet<Transform> armRelated, HashSet<Transform> gunBones)
        {
            foreach (Transform t in all)
            {
                if (rx.IsMatch(t.name) && IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>Weapons without a configured muzzle: the mesh with the largest extent hosts the barrel.</summary>
        /// <summary>
        /// Samples the animations relative to <paramref name="gunBody"/>. When the muzzle turns relative to it
        /// (the gun model is animated inside a static holder, e.g. tilted for the reload or kicked up by the fire
        /// clip), the holder is not the gun: sample again one level closer to the muzzle.
        /// </summary>
        private static PoseSampler SampleFromRigidBody(Weapon w, Transform[] all, Dictionary<Transform, int> index, Transform muzzle,
            Transform gripHand, bool canSample, ActionType action, HashSet<Renderer> armRenderers, ref Transform gunBody, out bool sampled)
        {
            for (int attempt = 0; ; attempt++)
            {
                var sampler = new PoseSampler(w, all, index, gunBody);
                sampled = canSample && sampler.Run(action, w.configuration);
                if (!sampled || attempt >= 4 || gunBody == muzzle || !muzzle.IsChildOf(gunBody) || !index.TryGetValue(muzzle, out int mi))
                {
                    return sampler;
                }
                bool turnsInFire = sampler.FireRotMax[mi] > 10f;
                bool turnsInReload = sampler.HasReload && sampler.ReloadRotMax[mi] > 15f;
                if (!turnsInFire && !turnsInReload)
                {
                    return sampler;
                }
                Transform next = muzzle;
                while (next.parent != null && next.parent != gunBody)
                {
                    next = next.parent;
                }
                // Whose side is the shooting hand on? It holds the gun: if it moves with the turning part, that part is
                // the gun tilting inside a holder; if it stays with the holder, the turning part is a hinge (break-open
                // barrels, a cylinder crane, a feed cover) and the holder is the gun.
                bool gunTurns;
                if (gripHand != null && index.TryGetValue(gripHand, out int hi))
                {
                    float handMoves = Mathf.Max(turnsInFire ? sampler.FireMax[hi] : 0f, turnsInReload ? sampler.ReloadMax[hi] : 0f);
                    gunTurns = handMoves > 0.05f;
                }
                else
                {
                    gunTurns = !HasMeshOutside(gunBody, next, armRenderers);
                }
                if (!gunTurns)
                {
                    return sampler;
                }
                sampler.Dispose();
                gunBody = next;
            }
        }

        private static bool HasMeshOutside(Transform holder, Transform child, HashSet<Renderer> armRenderers)
        {
            foreach (Renderer r in holder.GetComponentsInChildren<Renderer>(true))
            {
                if ((r is MeshRenderer || r is SkinnedMeshRenderer) && !armRenderers.Contains(r) && !r.transform.IsChildOf(child))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether the "third-person" subtree is in fact the visible first-person gun: a real third-person model is a
        /// copy, so a comparable amount of gun mesh exists outside it. When (almost) all the non-arm mesh lives in that
        /// subtree, it is the gun, even if the mod keeps its muzzle on a separate branch.
        /// </summary>
        private static bool HoldsTheOnlyGunModel(Weapon w, Transform root, Transform tp, HashSet<Transform> armBones)
        {
            long inside = 0, outside = 0;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == w.arms || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer || !ShownAtRest(r, root))
                {
                    continue; // a real third-person copy is hidden in first person: only what is drawn counts
                }
                Mesh mesh;
                if (r is SkinnedMeshRenderer smr)
                {
                    mesh = smr.sharedMesh;
                    if (smr.bones != null && smr.bones.Length > 0 && smr.bones.All(b => b == null || armBones.Contains(b)))
                    {
                        continue; // an arms mesh (a gun skinned to the arms' skeleton has gun bones of its own)
                    }
                }
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    mesh = mf != null ? mf.sharedMesh : null;
                }
                if (mesh == null)
                {
                    continue;
                }
                if (r.transform.IsChildOf(tp))
                {
                    inside += mesh.vertexCount;
                }
                else
                {
                    outside += mesh.vertexCount;
                }
            }
            return inside > 0 && outside * 4 < inside;
        }

        /// <summary>Enabled and on an active branch up to the weapon root (works on prefabs outside any scene too).</summary>
        private static bool ShownAtRest(Renderer r, Transform root)
        {
            if (!r.enabled)
            {
                return false;
            }
            for (Transform t = r.transform; t != null && t != root; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool OnArmChain(Transform b, Transform root, HashSet<Transform> named, HashSet<Transform> gunChain)
        {
            if (named.Count == 0)
            {
                // Unnamed skeleton (Bone, Bone.001...): trust the arms mesh except for the gun's own bones.
                return !gunChain.Contains(b) && !MagName.IsMatch(b.name) && !SlideName.IsMatch(b.name);
            }
            if (gunChain.Contains(b) || MagName.IsMatch(b.name) || SlideName.IsMatch(b.name))
            {
                return false;
            }
            for (Transform p = b.parent; p != null && p != root; p = p.parent)
            {
                if (named.Contains(p))
                {
                    return true; // finger, twist or helper bone below an arm bone
                }
                if (gunChain.Contains(p))
                {
                    break; // a bone under the gun, even if the gun hangs from a hand
                }
            }
            foreach (Transform c in b.GetComponentsInChildren<Transform>(true))
            {
                if (c != b && named.Contains(c))
                {
                    return true; // shoulder / upper-arm bone above a named hand
                }
            }
            return false;
        }

        /// <summary>
        /// The bone that carries the gun mesh: for a skinned gun the topmost of its non-arm bones, otherwise the
        /// largest non-arm mesh climbed to its topmost non-arm ancestor.
        /// </summary>
        private static Transform GunMeshHost(Transform root, HashSet<Transform> armRelated, HashSet<Transform> armBones, HashSet<Renderer> armRenderers)
        {
            Transform best = null;
            float bestSize = 0f;
            foreach (SkinnedMeshRenderer smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (armRenderers.Contains(smr) || smr.bones == null || smr.sharedMesh == null)
                {
                    continue;
                }
                // The gun's own root bone: the non-arm bone that parents the most other gun bones (a lone helper
                // such as a sight or camera bone at the same depth parents none).
                var gunSet = new List<Transform>();
                foreach (Transform b in smr.bones)
                {
                    if (b != null && !armBones.Contains(b) && !armRelated.Contains(b))
                    {
                        gunSet.Add(b);
                    }
                }
                Transform top = null;
                int topCount = 0, topDepth = int.MaxValue;
                foreach (Transform b in gunSet)
                {
                    int count = 0;
                    foreach (Transform other in gunSet)
                    {
                        if (other.IsChildOf(b))
                        {
                            count++;
                        }
                    }
                    int depth = Depth(b, root);
                    if (count > topCount || count == topCount && depth < topDepth)
                    {
                        top = b;
                        topCount = count;
                        topDepth = depth;
                    }
                }
                if (top == null)
                {
                    continue;
                }
                float size = smr.sharedMesh.bounds.size.magnitude;
                if (size > bestSize)
                {
                    bestSize = size;
                    best = top;
                }
            }
            if (best != null)
            {
                return best;
            }
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Renderer mr = mf.GetComponent<Renderer>();
                if (mf.sharedMesh == null || armRelated.Contains(mf.transform) || mr != null && armRenderers.Contains(mr))
                {
                    continue;
                }
                float size = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).magnitude;
                if (size > bestSize)
                {
                    bestSize = size;
                    best = mf.transform;
                }
            }
            if (best != null)
            {
                while (best.parent != null && best.parent != root && !armRelated.Contains(best.parent))
                {
                    best = best.parent;
                }
            }
            return best;
        }

        private static int Depth(Transform t, Transform root)
        {
            int depth = 0;
            for (Transform p = t; p != null && p != root; p = p.parent)
            {
                depth++;
            }
            return depth;
        }

        private static Transform GeometricMuzzleHost(Transform root, HashSet<Transform> armRelated)
        {
            Transform best = null;
            float bestSize = 0f;
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || armRelated.Contains(mf.transform))
                {
                    continue;
                }
                float size = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).magnitude;
                if (size > bestSize)
                {
                    bestSize = size;
                    best = mf.transform;
                }
            }
            return best;
        }

        /// <summary>Barrel direction for meshes without a muzzle: the mesh's longest axis, signed toward the weapon's forward.</summary>
        private static Vector3 GeometricAxis(Transform root, Transform gunBody, HashSet<Transform> armRelated, PoseSampler sampler, RigidPose gunIdle)
        {
            Vector3 forwardGun = gunIdle.InverseTransformDirection(sampler.Idle(root).Forward);
            MeshFilter mf = gunBody.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                return forwardGun.normalized;
            }
            Vector3 size = Vector3.Scale(mf.sharedMesh.bounds.size, gunBody.lossyScale);
            Vector3 axis = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
            return Vector3.Dot(axis, forwardGun) < 0f ? -axis : axis;
        }

        private static Transform FindHand(HashSet<Transform> armBones, bool right)
        {
            Transform best = null;
            int bestScore = 0;
            foreach (Transform b in armBones)
            {
                string n = Regex.Replace(b.name.ToLowerInvariant(), "[^a-z]", " ").Trim();
                if (!n.Contains("hand") || FingerName.IsMatch(n))
                {
                    continue;
                }
                bool isRight = Regex.IsMatch(n, @"(^r\b|\br$|right|\bhand r\b|hand r$|^r hand)") || n.EndsWith(" r");
                bool isLeft = Regex.IsMatch(n, @"(^l\b|\bl$|left|\bhand l\b|hand l$|^l hand)") || n.EndsWith(" l");
                if (right ? !isRight : !isLeft)
                {
                    continue;
                }
                // Prefer the bone that actually has finger children (the real hand, not a twist helper).
                int score = 1;
                foreach (Transform c in b)
                {
                    if (FingerName.IsMatch(c.name) || c.name.ToLowerInvariant().Contains("thumb"))
                    {
                        score++;
                    }
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    best = b;
                }
            }
            return best;
        }

        /// <summary>Palm centre ≈ halfway between the hand bone and its finger roots.</summary>
        private static Vector3 PalmOf(Transform hand, PoseSampler sampler)
        {
            Vector3 handPos = sampler.Idle(hand).position;
            Vector3 sum = Vector3.zero;
            int n = 0;
            foreach (Transform c in hand)
            {
                if (FingerName.IsMatch(c.name))
                {
                    sum += sampler.Idle(c).position;
                    n++;
                }
            }
            return n > 0 ? Vector3.Lerp(handPos, sum / n, 0.55f) : handPos;
        }

        private static bool _eyeResolved;
        private static bool _eyeValid;
        private static Vector3 _eyeInRoot;

        /// <summary>
        /// Where the game camera sits in a weapon root's space at rest, from the player prefab: weapons hang under
        /// shoulderParent/weaponParent below the camera parent. Prefab values are the rest offsets (no sway), and
        /// using the prefab means analysis works from the main menu with no player spawned.
        /// </summary>
        private static bool TryEyeInRoot(out Vector3 eye)
        {
            if (!_eyeResolved)
            {
                try
                {
                    GameObject prefab = GameManager.instance != null ? GameManager.instance.playerPrefab : null;
                    // Only remember the answer once the game is far enough along to give one.
                    _eyeResolved = prefab != null || LocalPlayer.controller != null;
                    PlayerFpParent fp = prefab != null ? prefab.GetComponentInChildren<PlayerFpParent>(true) : null;
                    if (fp == null && LocalPlayer.controller != null)
                    {
                        fp = LocalPlayer.controller.fpParent;
                    }
                    Transform camParent = fp != null ? fp.fpCameraParent : null;
                    Transform weaponParent = fp != null ? fp.weaponParent : null;
                    if (camParent != null && weaponParent != null && weaponParent.IsChildOf(camParent))
                    {
                        var path = new List<Transform>();
                        for (Transform t = weaponParent; t != null && t != camParent; t = t.parent)
                        {
                            path.Add(t);
                        }
                        path.Reverse();
                        RigidPose chain = RigidPose.Identity;
                        foreach (Transform t in path)
                        {
                            chain = chain.Multiply(RigidPose.LocalOf(t));
                        }
                        Vector3 camLocal = fp.fpCamera != null && fp.fpCamera.transform.parent == camParent ? fp.fpCamera.transform.localPosition : Vector3.zero;
                        _eyeInRoot = chain.InverseTransformPoint(camLocal);
                        _eyeValid = true;
                    }
                }
                catch (Exception e)
                {
                    Log.Debug("Eye point from the player prefab failed: " + e.Message);
                }
            }
            eye = _eyeInRoot;
            return _eyeValid;
        }
    }
}
