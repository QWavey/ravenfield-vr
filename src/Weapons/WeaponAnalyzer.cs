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
            @"(^|[^a-z])(arm(?!ature|or|our)|forearm(?![\s_.-]*(grip|wood|guard))|upperarm|hand(?!guard|le|stop|[\s_.-]*(grip|rest|rail))|wrist|finger|thumb(?![\s_.-]*(hole|rest))|index|middle|ring|pinky|pinkie|little|clavicle|shoulder(?![\s_.-]*(stock|pad|rest))|elbow|palm|knuckle)",
            RegexOptions.IgnoreCase);
        // Mod authors name parts in many languages: English, German, French, Spanish, Russian (Cyrillic and
        // transliterated) and Chinese (characters and pinyin abbreviations such as DX = 弹匣, QJ = 枪机).
        private static readonly Regex MagName = new Regex(
            @"(^|[^a-z])(mag(azine?)?s?($|[^a-z])|clip|drum|shell|rocket|round|ammo|cartridge|belt|cargador|chargeur|patron|lenta|zidan|danxia)|(^|[^a-z])dx($|[^a-z])"
            // A machine gun's ammunition box (弹箱: the PKM's "PKM d弹箱" scored as an unnamed part against its belt).
            // (Not a bare "box": modelling tools name every primitive "Box001".)
            + @"|ammo[ _]?(box|can)|box[ _]?mag|弹箱"
            + @"|магазин|патрон|лента|弹匣|弹夹|弹鼓|弹链|子弹|弹药|弹壳",
            RegexOptions.IgnoreCase);
        /// <summary>
        /// A launcher's round by its name: a missile, warhead or projectile (导弹, 火箭, 战斗部, ракета). Launchers only: on any
        /// other gun a "MissileTube" or "Projectile_FX" part was taken for its magazine by the name fallback (review 253.9).
        /// </summary>
        private static readonly Regex LauncherRoundName = new Regex(@"rocket|missile|warhead|projectile|ракета|导弹|火箭|战斗部", RegexOptions.IgnoreCase);
        /// <summary>
        /// A launcher's parts that its tube swap moves and are never its rocket: a safety, a cap or lid, a sling, a carry handle
        /// (保险, 桶盖, 提把; the AT-1K's controller unit with its safety was taken for its rocket). Launchers only: on any other
        /// gun a "Mag_Cap" lost its magazine name (review 253.7).
        /// </summary>
        private static readonly Regex LauncherNotRound = new Regex(
            @"safety|(^|[^a-z])(end ?)?caps?($|[^a-z])|(^|[^a-z])lid($|[^a-z])|sling|carry ?handle|保险|桶盖|提把", RegexOptions.IgnoreCase);
        /// <summary>
        /// Parts named after the magazine that are not the magazine: its release button or catch, the well it sits in, a
        /// pouch or a spare. Taking one of them for the magazine leaves the real one in the gun when it is ejected.
        /// </summary>
        private static readonly Regex NotMagName = new Regex(
            @"release|catch|button|latch|controll?er|控制器|(^|[^a-z])well|magwell|housing|lever|pouch|holder|spare|switch|bolt ?stop|stopper|indicator|弹匣释放|卡笋"
            // Optics: a magnifier's "Mag 2x" glass is not a magazine; a calibre is not a magnification ("mag_9x19",
            // "7.62x39 mag" were taken for optics and their magazines lost).
            + @"|glass|lens|scope|sight|optic|reticle|magnif|zoom|(^|[^a-z0-9.])(?!(\d+\.\d+|9|10)x(1[7-9]|[2-9]\d|1\d\d)(?!\d))\d+(\.\d+)?x($|[^a-z])|倍镜|瞄",
            RegexOptions.IgnoreCase);

        private static float Longest(Bounds b) => Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));

        /// <summary>Rifles loaded with an en-bloc clip, by name.</summary>
        private static readonly Regex EnBlocName = new Regex(@"garand|garret(?!t)|(^|[^a-z])m1 ?rifle", RegexOptions.IgnoreCase);

        private static bool IsMagName(string name) => MagName.IsMatch(name) && !NotMagName.IsMatch(name);

        /// <summary>A magazine (or on a launcher, its rocket) by its name: the launcher words count only on a launcher.</summary>
        private static bool IsMagName(string name, bool launcher) =>
            (MagName.IsMatch(name) || launcher && LauncherRoundName.IsMatch(name)) && !IsNotMag(name, launcher);

        /// <summary>Named after the magazine but not it (on a launcher also its safety, caps, lid, sling, carry handle).</summary>
        private static bool IsNotMag(string name, bool launcher) => NotMagName.IsMatch(name) || launcher && LauncherNotRound.IsMatch(name);

        /// <summary>A single round, case or shell (never a slide, bolt or handle).</summary>
        private static readonly Regex RoundName = new Regex(
            @"(^|[^a-z])(bullets?|rounds?|cartridges?|casings?|shells?|zidan)($|[^a-z])|子弹|弹壳|弹头", RegexOptions.IgnoreCase);

        private static readonly Regex TriggerName = new Regex(@"trigger|abzug|g[aâ]chette|gatillo|спуск|扳机", RegexOptions.IgnoreCase);
        private static readonly Regex BarrelName = new Regex(@"barrel|lauf|canon|ca[nñ]on|ствол|枪管", RegexOptions.IgnoreCase);

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
        private static readonly Regex MuzzleName = new Regex(@"muzzle|barrel ?end|flash(?![\s_.-]*(light|lamp))|firepoint|fire ?point|projectile ?spawn", RegexOptions.IgnoreCase);
        private static readonly Regex FingerName = new Regex(@"index|middle|ring|pinky|pinkie|little|finger", RegexOptions.IgnoreCase);

        public static string KeyOf(Weapon w, WeaponManager.WeaponEntry entry = null)
        {
            string prefab = w.name.Replace("(Clone)", "").Trim();
            entry = entry ?? w.weaponEntry;
            string key = entry != null ? entry.name + "#" + prefab : prefab;
            // A sub weapon (a fire mode or an attachment on its parent's model) also by its place under the topmost weapon:
            // two subs of one name under one entry shared one analysis (bug hunt 251.19). The path is the same on the prefab
            // and on its clone.
            Transform top = null;
            for (Transform t = w.transform.parent; t != null; t = t.parent)
            {
                if (t.GetComponent<Weapon>() != null)
                {
                    top = t;
                }
            }
            if (top != null)
            {
                var path = new System.Text.StringBuilder();
                for (Transform t = w.transform; t != null && t != top; t = t.parent)
                {
                    path.Insert(0, "/" + t.GetSiblingIndex());
                }
                key += path.ToString();
            }
            return key;
        }

        public static WeaponRigData Get(Weapon w) => Get(w, null);

        public static bool IsCached(Weapon w, WeaponManager.WeaponEntry entry) => Cache.ContainsKey(KeyOf(w, entry));

        /// <param name="w">Live weapon or weapon prefab.</param>
        /// <param name="entry">Pass when analysing a prefab (prefabs have no weaponEntry yet).</param>
        public static WeaponRigData Get(Weapon w, WeaponManager.WeaponEntry entry)
        {
            string key = KeyOf(w, entry);
            // (A weapon the sandbox export wants is analysed again once: analysed before by the menu's warm-up, it was
            // never written.)
            if (Cache.TryGetValue(key, out WeaponRigData cached) && !(SandboxExport.Wants(key) && !SandboxExport.Exported(key)))
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
            SandboxPoses.Apply(data); // hands saved in the sandbox (tools/sandbox) for this weapon
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
            if (!AuthoredAuto(w) && c.cooldown >= 0.45f)
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
        /// <summary>
        /// Reloaded a magazine's worth at a time: self-loading, or an advanced reload that loads half the capacity or more
        /// per motion (still classed as shell-fed while the magazine is analysed; see ReclassifyAdvancedReload).
        /// </summary>
        private static bool MagazineSizedReload(WeaponRigData d, Weapon w)
        {
            if (d.Action == ActionType.SelfLoading)
            {
                return true;
            }
            Weapon.Configuration c = w.configuration;
            if (d.Action != ActionType.ShellFed || c.projectilesPerShot > 1 || c.allowedReloads == null)
            {
                return false;
            }
            int chunk = 0;
            foreach (int r in c.allowedReloads)
            {
                chunk = Mathf.Max(chunk, r);
            }
            return chunk >= 3 && chunk * 2 >= c.ammo;
        }

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
            d.Action = !AuthoredAuto(w) && c.cooldown >= 0.45f ? ActionType.Manual : ActionType.SelfLoading;
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
            d.Handgun = d.IsPistol;
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
            // A gun part with an arm-like name (a shotgun's forearm, a grip called "Grip_Hand"): a mesh of its own, not on
            // the arms' skeleton, and no arm-named transform above or below it. Taken for an arm, it was hidden in VR, and
            // the receiver above it stopped being a candidate for the gun's body and its parts.
            var armListed = new HashSet<Transform>();
            if (w.arms != null && w.arms.bones != null)
            {
                foreach (Transform b in w.arms.bones)
                {
                    if (b != null)
                    {
                        armListed.Add(b);
                    }
                }
            }
            var lone = new List<Transform>();
            foreach (Transform t in named)
            {
                if (LoneArmNamedMesh(t, root, named, armListed))
                {
                    lone.Add(t);
                }
            }
            foreach (Transform t in lone)
            {
                named.Remove(t);
                d.Warnings.Add($"'{t.name}' has an arm-like name but is a part of the gun (a mesh of its own, not on the arms)");
            }
            // An unnamed skeleton (Bone, Bone.001...): a bone the gun's own skinned mesh uses is the gun's, even when the
            // arms mesh lists it too (all of the gun's bones counted as arm bones and the gun mesh was hidden).
            var gunSkinned = new HashSet<Transform>();
            if (named.Count == 0)
            {
                foreach (SkinnedMeshRenderer s in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (s != w.arms && s.bones != null && !ArmMeshName.IsMatch(s.name))
                    {
                        foreach (Transform b in s.bones)
                        {
                            if (b != null)
                            {
                                gunSkinned.Add(b);
                            }
                        }
                    }
                }
            }
            var armBones = new HashSet<Transform>(named);
            if (w.arms != null && w.arms.bones != null)
            {
                foreach (Transform b in w.arms.bones)
                {
                    if (b != null && b != root && !armBones.Contains(b) && !gunSkinned.Contains(b) && OnArmChain(b, root, named, gunChain))
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
            if (tpCandidate && tpGun == null && !HoldsShownGunParts(tp, root))
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
            else if (muzzleMethod == "name")
            {
                d.Warnings.Add($"no muzzle configured; the muzzle is the part named '{muzzle.name}'");
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
            string rigDump = null;
            byte[] exportGlb = null;
            string exportNote = null;
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
                    else if (animator.transform != root)
                    {
                        d.Warnings.Add("the animator is not on this weapon's root (a sub-weapon on its parent's rig): parts found by name only");
                    }
                }

                RigidPose gunIdle = sampler.Idle(gunBody);
                RigidPose muzzleIdle = sampler.Idle(muzzle);
                if (muzzleMethod == "geometry")
                {
                    d.AimAxis = GeometricAxis(root, muzzle, sampler, gunIdle, d.Warnings);
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
                d.Up = UprightByModel(d.Up, d.AimAxis, d);
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
                KeepMagazineAxisInSidePlane(d);
                MeasureMagazineProtrusion(d, root, gunBody, armRelated, armRenderers, gunBones, sampler);
                ReclassifyAdvancedReload(d, w);
                DetectHinge(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, w, gunIdle);
                FindBeltParts(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler);
                d.CyclesByHand = d.Action == ActionType.Manual
                                 || d.Action == ActionType.ShellFed && !AuthoredAuto(w) && w.configuration.cooldown >= 0.45f;
                if (d.Animated && sampler.HasReload && (d.Action == ActionType.SelfLoading || d.Action == ActionType.Manual))
                {
                    RackStrokeFromHands(d, handR, handL, sampler, index, root, gunIdle);
                }
                NamedChargingHandle(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle);
                ArStaticHandle(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle);
                SideKnobByName(d, all, root, sampler, gunIdle);
                RackOnOutstandingPiece(d, root, sampler, gunIdle);
                FindSlideCompanions(d, all, index, root, gunBody, sampler, gunIdle);
                CollectReloadRounds(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle); // after every part is chosen
                d.GripCurls = FingerCurls(handR, sampler);
                d.SupportCurls = FingerCurls(handL, sampler);
                d.ArmRight = ArmChainOf(root, handR, handL, armBones, sampler, gunIdle, d);
                d.ArmLeft = ArmChainOf(root, handL, handR, armBones, sampler, gunIdle, d);
                BuildSoundMap(d, sampler, w);
                if (WeaponSelfTest.WantsRigDump(key))
                {
                    rigDump = DumpRig(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle);
                }
                if (SandboxExport.Wants(key))
                {
                    // The drawn model at rest for the sandbox (tools/sandbox); written with rig.json once every rule has run.
                    try
                    {
                        exportGlb = SandboxExport.BuildGlb(d, root, armRenderers, sampler, gunIdle, out exportNote);
                    }
                    catch (Exception e)
                    {
                        Log.Warn($"Sandbox export of {key} failed: {e.Message}");
                    }
                }
            }
            finally
            {
                sampler.Dispose();
            }

            FinishVirtualParts(d);
            MagKickRule(d, w); // after the virtual magazine's seat is set: before, a virtual magazine was judged at no seat
            ArChargingHandle(d);
            BoltInsideOnSurface(d);
            ForwardSideHandle(d);
            CheckSanity(d, root, gunBody, armRelated);
            if (exportGlb != null)
            {
                SandboxExport.Write(d, root, w, exportGlb, exportNote);
            }
            if (rigDump != null)
            {
                // Written once every rule has run: with what they chose (the slide, the handle, the rack point, the magazine).
                WeaponSelfTest.AppendRigDump(rigDump + DumpChoices(d, root));
            }
            return d;
        }

        /// <summary>A charging handle by its name (an AR's T-handle, a side cocking handle); never a magazine ("chargeur").</summary>
        private static readonly Regex ChargingName = new Regex(@"charg|cocking ?handle|拉机柄|拉柄|上膛", RegexOptions.IgnoreCase);

        /// <summary>
        /// A charging handle the animations never move, found by its name: the M733's "mk18_拉机柄" sat still while its
        /// reload slid the bolt, so the hand was given a piece cut out of the receiver at a guessed place (it landed on the
        /// buffer tube, with a dark knob behind the bolt) and the real handle never moved. The named part is taken as the
        /// handle: where the hand grabs (its back end, the T), and it comes straight back with the hand, the bolt with it.
        /// </summary>
        private static void NamedChargingHandle(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root,
            Transform gunBody, Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, RigidPose gunIdle)
        {
            // (Machine guns too: their named charging or cocking handle was never taken, and the hand worked the bolt
            // carrier inside; user, 2026-10-16: "MG charging handles don't work"; 255.I4-2.)
            if (d.Handgun || d.HandlePath != null || d.Action != ActionType.SelfLoading)
            {
                return;
            }
            Transform slide = d.SlidePath != null ? WeaponRig.Resolve(root, d.SlidePath) : null;
            Transform best = null;
            Bounds bestBounds = default;
            foreach (Transform t in all)
            {
                if (!ChargingName.IsMatch(t.name) || MagName.IsMatch(t.name) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                {
                    continue;
                }
                // A handle on the bolt carrier already moves with the bolt (an AK's): nothing to add.
                if (slide != null && (t == slide || t.IsChildOf(slide) || slide.IsChildOf(t)))
                {
                    continue;
                }
                // The topmost named part: its children (the handle's mesh) go with it.
                if (best != null && t.IsChildOf(best))
                {
                    continue;
                }
                Bounds b = sampler.GunSpaceBoundsNear(t, gunIdle, 0.5f);
                float length = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                if (length < 0.02f || length > 0.3f || Vector3.Distance(b.center, d.GripPoint) > 0.35f)
                {
                    continue; // no model of its own, the whole receiver, or far from the hands
                }
                if (best == null || best.IsChildOf(t))
                {
                    best = t;
                    bestBounds = b;
                }
            }
            if (best == null)
            {
                return;
            }
            d.HandlePath = WeaponRig.PathOf(root, best);
            d.HandleRestLocal = sampler.IdleLocalPosition(best);
            d.HandleTravel = -d.AimAxis * 0.075f;
            // The grip of the handle is its back end (an AR's T sits at the rear of the upper receiver).
            float half = Mathf.Abs(Vector3.Dot(bestBounds.extents, new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z))));
            d.RackPoint = bestBounds.center - d.AimAxis * half * 0.8f;
            if (d.SlidePath == null)
            {
                d.RackFromHand = true;
                d.SlideTravel = d.HandleTravel;
            }
            d.SlideMethod += $" + handle by name '{best.name}'";
        }

        /// <summary>
        /// A rifle racked by its bolt alone (no charging handle of its own in the model): the grab zone was the bolt's centre,
        /// inside the receiver (a CZ Bren 2's: "racking works, but the hitbox is inside the gun"). It goes out onto the
        /// receiver's surface over the bolt once the model is measured, where a hand closes on something.
        /// </summary>
        private static void BoltInsideOnSurface(WeaponRigData d)
        {
            if (d.Handgun || d.Action != ActionType.SelfLoading || d.SlidePath == null || d.HandlePath != null || d.CarveHandle
                || d.RackFromHand || d.SlideMethod == null || !d.SlideMethod.StartsWith("fire motion", StringComparison.Ordinal))
            {
                return;
            }
            d.RackOnSurface = true;
        }

        /// <summary>
        /// A CZ Bren 2 whose model has its charging handle (a part named so, the knob in its slot on the left with its rod
        /// and the bolt under it): the knob is the left-most part of that mesh. The name search above leaves it out (the bolt
        /// is its child, and with its rod it is longer than a handle), and the hand was put 10 cm behind the knob.
        /// </summary>
        private static void SideKnobByName(WeaponRigData d, Transform[] all, Transform root, PoseSampler sampler, RigidPose gunIdle)
        {
            if (d.Handgun || d.Action != ActionType.SelfLoading || !Bren2Pattern.IsMatch(d.Key ?? ""))
            {
                return;
            }
            Vector3 left = -Vector3.Cross(d.Up.normalized, d.AimAxis.normalized).normalized;
            RigidPose gunInv = gunIdle.Inverse();
            foreach (Transform t in all)
            {
                if (!ChargingName.IsMatch(t.name) || MagName.IsMatch(t.name))
                {
                    continue;
                }
                MeshFilter mf = t.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null || !mf.sharedMesh.isReadable || mf.sharedMesh.vertexCount == 0)
                {
                    continue;
                }
                Vector3[] v = mf.sharedMesh.vertices;
                RigidPose idle = sampler.Idle(t);
                Vector3 scale = t.lossyScale;
                var pts = new Vector3[v.Length];
                float most = float.MinValue;
                for (int i = 0; i < v.Length; i++)
                {
                    pts[i] = gunInv.TransformPoint(idle.TransformPoint(Vector3.Scale(v[i], scale)));
                    most = Mathf.Max(most, Vector3.Dot(pts[i], left));
                }
                // The knob: the points within 12 mm of the left-most.
                Vector3 sum = Vector3.zero;
                int n = 0;
                for (int i = 0; i < pts.Length; i++)
                {
                    if (Vector3.Dot(pts[i], left) > most - 0.012f)
                    {
                        sum += pts[i];
                        n++;
                    }
                }
                if (n == 0 || Vector3.Distance(sum / n, d.GripPoint) > 0.45f)
                {
                    continue;
                }
                d.SideKnob = sum / n;
                d.HasSideKnob = true;
                d.CarveFromPath = WeaponRig.PathOf(root, t);
                return;
            }
        }

        /// <summary>CZ Bren 2 (805 Bren) by name: its charging handle sits on the left side at the front of the receiver.</summary>
        private static readonly Regex Bren2Pattern = new Regex(@"(?<![a-z])(cz ?bren|bren ?2|cz ?805)(?![a-z0-9])", RegexOptions.IgnoreCase);

        /// <summary>
        /// A CZ Bren 2's charging handle is not on its bolt: it sits on the left side of the receiver, at its front, ahead
        /// of the magazine well. The model has no handle, so the grab zone was on the receiver over the internal bolt
        /// (further back and on top): "still the wrong bolt pull-back zone". It goes to the handle's place, a little above
        /// the bore on the left, put on the model's surface once it is measured; the bolt inside still moves with the hand.
        /// </summary>
        private static void ForwardSideHandle(WeaponRigData d)
        {
            if (d.Handgun || d.IsMachineGun || d.Action != ActionType.SelfLoading || d.SlidePath == null || d.HandlePath != null
                || !Bren2Pattern.IsMatch(d.Key ?? ""))
            {
                return;
            }
            Vector3 aim = d.AimAxis.normalized;
            Vector3 up = d.Up.normalized;
            Vector3 left = -Vector3.Cross(up, aim).normalized;
            Vector3 magCentre = d.MagSeat.TransformPoint(d.MagBounds.center);
            float along = Vector3.Dot(magCentre - d.MuzzlePoint, aim) + 0.05f;
            if (d.HasSupportPoint)
            {
                along = Mathf.Min(along, Vector3.Dot(d.SupportPoint - d.MuzzlePoint, aim) - 0.06f);
            }
            if (d.HasSideKnob)
            {
                // The model's own knob, cut out of the handle's mesh (not the receiver), comes back with the hand; the bolt
                // under it moves with the stroke as before (Tasks 235: the hand was 10 cm behind the knob, the knob still).
                d.RackPoint = d.SideKnob;
                d.CarveHandle = true;
                d.SlideMethod += " + its own side handle (left, front of the receiver)";
                return;
            }
            Vector3 bore = d.MuzzlePoint + aim * along;
            d.RackPoint = bore + up * 0.015f + left * 0.03f;
            d.RackOnSurface = true;
            // A handle there that comes back with the hand: only the bolt inside moved, and the hand pulled at a smooth
            // receiver ("the bolt is wrong", Tasks 235).
            d.AddedHandle = true;
            d.SlideMethod += " + handle added at the Bren 2's place (left, front of the receiver)";
        }

        /// <summary>
        /// Open-bolt guns by name: their bolt rests back, cocked, and runs forward to fire, so a bolt held back at idle is
        /// their real rest (the held-back rule flipped the PPSh-41's; rule review, 2026-10-17).
        /// </summary>
        private static readonly Regex OpenBoltName = new Regex(
            @"ppsh|pps-?43|(?<![a-z])sten(?![a-z])|grease ?gun|greaser|(?<![a-z])m3a?1?(?![a-z0-9])|(?<![a-z])uzi|mp ?40|mp ?38|thompson|tommy|sterling|(?<![a-z])mac-?10|ingram|(?<![a-z])m60|(?<![a-z])pkm?(?![a-z])|(?<![a-z])rpk|(?<![a-z])mg ?42|(?<![a-z])mg ?3(?![0-9])|(?<![a-z])bren(?! ?2)|lewis|(?<![a-z])dp-?2?7?(?![a-z])|m249|minimi|negev",
            RegexOptions.IgnoreCase);

        /// <summary>AR-pattern rifles by name (M4/M16/AR-15/HK416/MK18/M733 and their kin).</summary>
        private static readonly Regex ArPattern = new Regex(
            // Not inside a word ("19m4mwii holo", "mk18riii" count, "gram4" does not), not a longer number ("m40", "m45").
            @"(?<![a-z])(m4|m4a1|m16|ar-?15|ar-?10|hk ?416|hk ?417|m ?417|mr ?308|mr ?762|lmt ?308|mk ?18|mk ?12|m733|m727|xm177|car-?15|cqbr|urgi|sr-?25|m110|mcx|honey ?badger|ddm4|m27|g28|lvoa|k13)(?!\d)",
            RegexOptions.IgnoreCase);

        /// <summary>
        /// An AR-pattern rifle is racked by its charging handle, at the top of the back of the upper receiver, pulled
        /// straight back. Without a part of its own that moves, the rack point came from where the reload animation's hand
        /// went, and many reloads slap the bolt catch on the side instead: the hand took the gun at the ejection port and
        /// only the bolt slid in it, the charging handle never moved. Here the handle is cut out of the receiver at its
        /// place and comes back with the hand (the bolt, if the model has one, moves with it).
        /// </summary>
        private static void ArChargingHandle(WeaponRigData d)
        {
            if (d.Handgun || d.Action != ActionType.SelfLoading || d.HandlePath != null || d.IsMachineGun || !ArPattern.IsMatch(d.Key ?? ""))
            {
                return;
            }
            // (Also "... carrying the bolt": the closing bracket in the test sent that one to the estimate and an added knob;
            // 255.B4-2.)
            if (d.SlidePath != null && d.SlideMethod != null && d.SlideMethod.StartsWith("reload motion (charging handle", StringComparison.Ordinal))
            {
                // The model's own charging handle, found from the reload, already comes back with the hand, taken at its
                // back end: the rule's place on the receiver (5 cm behind the grip) put the hand 3.3 cm from it, on an added
                // knob, and the M4A1's own handle seemed not to move (user, 2026-10-16).
                d.SlideMethod += " (AR-pattern: its own handle)";
                return;
            }
            Vector3 boreAboveGrip = d.MuzzlePoint + d.AimAxis * Vector3.Dot(d.GripPoint - d.MuzzlePoint, d.AimAxis);
            d.RackPoint = boreAboveGrip - d.AimAxis * 0.05f + d.Up * 0.03f;
            d.RackOnSurface = true; // onto the top of the receiver once the model is measured
            d.CarveHandle = true;
            if (d.SlidePath == null)
            {
                d.RackFromHand = true;
                d.SlideTravel = -d.AimAxis * 0.075f;
            }
            d.SlideMethod += " (AR-pattern: the charging handle at the back of the receiver)";
        }

        // ---- hands & sights ---------------------------------------------------------------------------

        private static void AnalyzeHands(WeaponRigData d, Transform handR, Transform handL, PoseSampler sampler, RigidPose gunIdle, Weapon w)
        {
            d.IsPistol = w.slot == 1;
            d.Handgun = d.IsPistol;
            if (handR != null)
            {
                d.GripPoint = gunIdle.InverseTransformPoint(PalmOf(handR, sampler));
                d.GripDown = GripDownOf(handR, sampler, gunIdle, d);
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
            float ahead = Vector3.Dot(d.SupportPoint - d.GripPoint, d.AimAxis), longReach = Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis);
            if (d.IsPistol && handL != null && ahead > 0.15f && longReach > 0.3f)
            {
                // A long gun the animation holds with both hands, the left well forward, is a rifle whatever slot it is in (a
                // mod's "[Sec]" copy of a rifle): taken for a handgun by its slot, its charging handle was never looked for and
                // the bolt inside the receiver was taken by its name (the three [Sec] BRN180s moved nothing; weapons-1017).
                d.IsPistol = false;
                d.Handgun = false;
                d.Notes += "a long gun in the secondary slot, held two-handed: a rifle ";
            }
            if (Vector3.Dot(d.SupportPoint - d.GripPoint, d.AimAxis) < 0.07f)
            {
                float reach = Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis);
                if (!d.Handgun && reach > 0.3f)
                {
                    // A long gun's support hand is never behind its grip: the PPSh-41's reload rests the left hand on the
                    // magazine, and taken as a pistol the support hand cupped a gun hand that was not there, the gun held in
                    // the air (user, 2026-10-16). On the forend instead, as the estimate without a left hand.
                    d.SupportPoint = d.GripPoint + d.AimAxis * Mathf.Min(0.24f, reach * 0.45f);
                    d.HasSupportPoint = true;
                    d.Notes += "support hand on the forend (the animation's left hand was behind the grip) ";
                }
                else
                {
                    d.IsPistol = true; // support hand cups the grip: no foregrip steering
                }
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
                d.EyePoint = d.MuzzlePoint + overBore + d.AimAxis * (gripAlong - (d.Handgun ? 0.35f : 0.12f));
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
                    if (TriggerName.IsMatch(t.name))
                    {
                        continue; // a trigger moves along the bore when it fires, and is not something to rack (an M1878's)
                    }
                    if (RoundName.IsMatch(t.name))
                    {
                        continue; // a round the fire animation feeds or throws (the MP9's loose 单独子弹) is not the bolt
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
                    // A small piece riding on a much bigger part that moves the same way when it fires is not the slide, the
                    // bigger part is: INGRESS 700's 'Charge port' (a 4 cm mesh, 8.3 cm of fire travel) sits on its 24 cm
                    // 'Slide_high' (4.4 cm), was taken for the slide, and moved alone, unseen (252 ae review).
                    Transform up = slide.parent;
                    if (up != null && up != gunBody && up != root && index.TryGetValue(up, out int ui) && !armRelated.Contains(up)
                        && HasOwnRenderer(up) && sampler.IdleVisible(up) && sampler.FireMax[ui] >= 0.015f && sampler.FireMax[ui] <= MaxBoltTravel(d)
                        && Vector3.Dot(sampler.FireVec[ui].normalized, slideVec.normalized) > 0.8f
                        && sampler.FireMax[ui] <= sampler.FireMax[index[slide]] + 0.002f) // the piece rides on it: a body moving more than its bolt is not the slide
                    {
                        Transform piece = slide;
                        float pieceSize = sampler.GunSpaceBoundsWhere(piece, gunIdle, x => x != piece).size.magnitude;
                        float upSize = sampler.GunSpaceBoundsWhere(up, gunIdle, x => x != up).size.magnitude;
                        if (pieceSize > 1e-4f && upSize >= pieceSize * 3f)
                        {
                            d.Notes += $"slide: '{up.name}' ({upSize * 100f:0} cm) carries the piece '{piece.name}' ({pieceSize * 100f:0} cm) that moved most ";
                            slide = up;
                            slideVec = sampler.FireVec[ui];
                        }
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
                slide = FindSlideByName(all, root, gunBody, muzzle, armRelated, gunBones);
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
            Vector3 restShift = Vector3.zero; // gun space: from the idle place to the rest (a pistol caught locked back)
            float forward = Vector3.Dot(slideVec, d.AimAxis);
            float reloadEnds = 0f;
            if (sampler.HasReload && index.TryGetValue(slide, out int si0))
            {
                Vector3[] tr = sampler.ReloadTrajectory[si0];
                reloadEnds = tr.Length > 0 ? Vector3.Dot(tr[tr.Length - 1] - sampler.RelIdle[si0].position, d.AimAxis) : 0f;
            }
            if (d.SlideMethod != null && d.SlideMethod.StartsWith("fire motion", StringComparison.Ordinal)
                && forward > 0.015f && Vector3.Dot(slideVec.normalized, d.AimAxis) > 0.8f && (d.Handgun || reloadEnds >= forward * 0.8f)
                && !OpenBoltName.IsMatch(d.Key ?? ""))
            {
                // Caught held back at idle: the fire motion takes the slide toward the muzzle from there (and the reload ends
                // there too). Racked from that rest it went back past its stop: FA1911 20R 82 mm at half a pull and 112 mm
                // locked back; the ARS MP9's bolt stayed 44 mm back after the release while its animator was in its loaded
                // pose (check-1017f); the PPSh's bolt was racked 9 cm back from its back place. The rest is the forward place,
                // racked back from there by the same distance.
                Vector3 worldDelta = gunIdle.rotation * slideVec;
                Transform parent = slide.parent;
                Vector3 localDelta = worldDelta;
                if (parent != null)
                {
                    Vector3 s = parent.lossyScale;
                    localDelta = Quaternion.Inverse(sampler.Idle(parent).rotation) * worldDelta;
                    localDelta = new Vector3(localDelta.x / Mathf.Max(Mathf.Abs(s.x), 1e-6f), localDelta.y / Mathf.Max(Mathf.Abs(s.y), 1e-6f), localDelta.z / Mathf.Max(Mathf.Abs(s.z), 1e-6f));
                }
                d.SlideRestLocal += localDelta;
                restShift = slideVec;
                d.Notes += $"slide held back at idle (the fire motion runs {slideVec.magnitude * 100f:0.0} cm toward the muzzle): its rest is the forward place ";
                slideVec = -slideVec;
            }
            float travel = slideVec.sqrMagnitude > 0f ? slideVec.magnitude * 1.15f : (d.Handgun ? 0.035f : 0.07f);
            travel = Mathf.Clamp(travel, d.Handgun ? 0.02f : 0.03f, 0.15f);
            Vector3 dir = slideVec.sqrMagnitude > 0f ? slideVec.normalized : -d.AimAxis;
            if (Vector3.Dot(dir, -d.AimAxis) < 0.5f)
            {
                dir = -d.AimAxis; // keep racking straight back even if the fire clip arcs
            }
            d.SlideTravel = dir * travel;
            Bounds b = sampler.GunSpaceBoundsNear(slide, gunIdle, 0.5f);
            if (b.size.sqrMagnitude > 1e-6f)
            {
                float along = Mathf.Abs(Vector3.Dot(b.extents, new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z))));
                d.RackPoint = b.center - d.AimAxis * along * 0.6f;
                if (!d.Handgun && d.SlideMethod != null && d.SlideMethod.StartsWith("reload motion (charging handle)", StringComparison.Ordinal)
                    && ArPattern.IsMatch(d.Key ?? ""))
                {
                    // An AR's own charging handle is taken by its T, at the handle's very back, behind the upper receiver:
                    // 60% back along it put the place to take it 3-4 cm inside the receiver, out of reach of a hand kept
                    // out of the gun (M4A1 [SQUAD] family; user, 2026-10-16).
                    d.RackPoint = b.center - d.AimAxis * (along + 0.005f);
                }
            }
            else
            {
                d.RackPoint = gunIdle.InverseTransformPoint(sampler.Idle(slide).position);
            }
            d.RackPoint += restShift;
            if (d.Action == ActionType.Manual && d.HasSupportPoint && Vector3.Distance(d.RackPoint, d.SupportPoint) < 0.12f)
            {
                d.RackPoint = d.SupportPoint; // pump action: rack where the support hand already is
            }

            // A separate charging handle the reload works (AR style): that is where the hand grabs, the bolt follows.
            if (d.Animated && sampler.HasReload && !d.SlideMethod.StartsWith("reload motion (charging handle)", StringComparison.Ordinal))
            {
                Transform handle = ChargingHandleFromReload(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, out Vector3 handleVec, slide);
                if (handle != null && slide.IsChildOf(handle) && Vector3.Distance(sampler.RelIdle[index[handle]].position, d.GripPoint) < 0.45f)
                {
                    // The handle carries the bolt (a PKM's 拉机柄 is its bolt's parent): it is what the hand racks, the bolt
                    // rides on it. As a separate handle the bolt would move twice (the handle's travel plus its own).
                    d.SlidePath = WeaponRig.PathOf(root, handle);
                    d.SlideRestLocal = sampler.IdleLocalPosition(handle);
                    d.SlideTravel = -d.AimAxis * Mathf.Clamp(handleVec.magnitude, 0.03f, 0.15f);
                    Bounds cb = sampler.GunSpaceBoundsNear(handle, gunIdle, 0.5f);
                    d.RackPoint = cb.size.sqrMagnitude > 1e-6f ? cb.center : gunIdle.InverseTransformPoint(sampler.Idle(handle).position);
                    if (cb.size.sqrMagnitude > 1e-6f)
                    {
                        // A long handle on top is taken at its back end, as a separate one is (255.B4-2).
                        Vector3 aimAbs = new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z));
                        float half = Mathf.Abs(Vector3.Dot(cb.extents, aimAbs));
                        if (half > 0.04f && Vector3.Dot(cb.center - d.MuzzlePoint, d.Up) > -0.005f)
                        {
                            d.RackPoint = cb.center - d.AimAxis * half * 0.8f;
                        }
                    }
                    d.SlideMethod = "reload motion (charging handle carrying the bolt)";
                    // The bolt itself, for the action locked open: it alone stays back, the handle goes home (an M4's
                    // charging handle stayed back on an empty magazine; user, 2026-10-16).
                    d.BoltPath = WeaponRig.PathOf(root, slide);
                    d.BoltRestLocal = sampler.IdleLocalPosition(slide);
                    handle = null;
                }
                if (handle != null && d.Handgun)
                {
                    // A pistol is racked by its slide's back: a part its reload moves in the slide's front half is the barrel
                    // or the guide rod (GLOCK 43X: '43X_QG0', 4 cm under the bore at the muzzle), and taken as the handle the
                    // hand pinched the slide's front with its palm grip switched off (252 ae review, check-1017f).
                    Transform h = handle;
                    Bounds hb = sampler.GunSpaceBoundsWhere(h, gunIdle, x => x != h);
                    Bounds slb = sampler.GunSpaceBoundsNear(slide, gunIdle, 0.5f);
                    if (hb.size.sqrMagnitude > 1e-8f && slb.size.sqrMagnitude > 1e-8f && Vector3.Dot(hb.center - slb.center, d.AimAxis) > 0f)
                    {
                        d.Notes += $"'{h.name}' moved by the reload sits in the slide's front half: not a handle ";
                        handle = null;
                    }
                }
                if (handle != null && Vector3.Distance(sampler.RelIdle[index[handle]].position, d.GripPoint) < 0.45f)
                {
                    d.HandlePath = WeaponRig.PathOf(root, handle);
                    d.HandleRestLocal = sampler.IdleLocalPosition(handle);
                    d.HandleTravel = handleVec;
                    Bounds hb = sampler.GunSpaceBoundsNear(handle, gunIdle, 0.5f);
                    d.RackPoint = hb.size.sqrMagnitude > 1e-6f ? hb.center : gunIdle.InverseTransformPoint(sampler.Idle(handle).position);
                    if (hb.size.sqrMagnitude > 1e-6f)
                    {
                        // A long handle on top of the receiver (an AR's T with its rod, a G36's under the carry handle) is
                        // taken at its back end, as the name rule does: at its middle the hand took an M4 over the ejection
                        // port (Tasks 236). A side knob (short across the bore) stays where it is.
                        Vector3 aimAbs = new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z));
                        float half = Mathf.Abs(Vector3.Dot(hb.extents, aimAbs));
                        float above = Vector3.Dot(hb.center - d.MuzzlePoint, d.Up);
                        if (half > 0.04f && above > -0.005f)
                        {
                            d.RackPoint = hb.center - d.AimAxis * half * 0.8f;
                            d.SlideMethod += " + handle (taken at its back end)";
                        }
                        else
                        {
                            d.SlideMethod += " + handle";
                        }
                    }
                    else
                    {
                        d.SlideMethod += " + handle";
                    }
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
            Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, RigidPose gunIdle)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {d.Key}  action {d.Action}  aim {d.AimAxis:F2} up {d.Up:F2}  grip {d.GripPoint:F3}  muzzle {d.MuzzlePoint:F3}");
            sb.AppendLine("    flags: A arm  G gun body  M muzzle  C part candidate  h hidden at idle  b skinned bone");
            sb.AppendLine("    fire: move cm (along bore) rot deg @axis | reload: move cm, first dir (back, down) rot deg @axis");
            sb.AppendLine("    @ its own mesh's centre in cm from the muzzle (back along the bore, up, right) and its size (along x up x across)");
            if (!string.IsNullOrEmpty(d.DumpLines))
            {
                sb.Append(d.DumpLines);
            }
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
                    // Where it is (the dump had sizes only: whether a knob touches its carrier, or a T sits at the carrier's
                    // rear top, could not be told from it; 252 ae reviews).
                    Transform self = t;
                    Bounds ob = sampler.GunSpaceBoundsWhere(t, gunIdle, x => x != self);
                    if (ob.size.sqrMagnitude > 1e-10f)
                    {
                        drawn += " " + WhereInGun(d, ob);
                    }
                }
                Camera cam = t.GetComponent<Camera>();
                if (cam != null)
                {
                    RenderTexture rt = cam.targetTexture;
                    drawn += $" | CAMERA{(cam.enabled ? "" : " off")}{(t.gameObject.activeSelf ? "" : " (object inactive)")}, fov {cam.fieldOfView:0.#}, depth {cam.depth:0}, mask 0x{cam.cullingMask:X8}, "
                             + $"near {cam.nearClipPlane:0.###}, target {(rt == null ? "screen" : $"'{rt.name}' {rt.width}x{rt.height}")}";
                }
                sb.AppendLine($"{new string(' ', 2 + depth * 2)}{t.name} [{flags}]{fire}{reload}{drawn}");
            }
            return sb.ToString();
        }

        /// <summary>A gun-space box as the rig dump gives it: its centre from the muzzle (back, up, right) and its size, in cm.</summary>
        private static string WhereInGun(WeaponRigData d, Bounds b)
        {
            Vector3 right = Vector3.Cross(d.Up, d.AimAxis).normalized;
            Vector3 c = b.center - d.MuzzlePoint;
            float Across(Vector3 axis) => Vector3.Dot(b.size, new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z))) * 100f;
            return $"@ back {Vector3.Dot(c, -d.AimAxis) * 100f:0.0} up {Vector3.Dot(c, d.Up) * 100f:0.0} right {Vector3.Dot(c, right) * 100f:0.0} "
                   + $"size {Across(d.AimAxis):0.0}x{Across(d.Up):0.0}x{Across(right):0.0}";
        }

        /// <summary>The rig dump's last lines: what the rules chose, once all of them ran.</summary>
        private static string DumpChoices(WeaponRigData d, Transform root)
        {
            string Name(int[] path) => path == null ? "none" : "'" + (WeaponRig.Resolve(root, path)?.name ?? "?") + "'";
            Vector3 right = Vector3.Cross(d.Up, d.AimAxis).normalized;
            Vector3 rp = d.RackPoint - d.MuzzlePoint;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"    chosen: slide {Name(d.SlidePath)} ({d.SlideMethod}) travel {d.SlideTravel.magnitude * 100f:0.0} cm; handle {Name(d.HandlePath)} travel {d.HandleTravel.magnitude * 100f:0.0} cm; bolt {Name(d.BoltPath)}");
            sb.AppendLine($"    rack point: back {Vector3.Dot(rp, -d.AimAxis) * 100f:0.0} up {Vector3.Dot(rp, d.Up) * 100f:0.0} right {Vector3.Dot(rp, right) * 100f:0.0} cm from the muzzle; "
                          + $"on the surface {d.RackOnSurface}, carved {d.CarveHandle}, added knob {d.AddedHandle}; handgun {d.Handgun}, pistol {d.IsPistol}");
            sb.AppendLine($"    magazine {Name(d.MagazinePath)}: pull {d.MagEjectAxis:F2}, box {d.MagBounds.size * 100f:F1} cm");
            sb.AppendLine($"    notes: {d.Notes}");
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
                // The grasp sits a little ahead of the wrist bone, toward the fingers. Both in gun space: PalmOf is in the
                // weapon root's, and taken as it was it shifted every stroke by the gun body's offset from the root (an AS
                // Val's rack point hung 10 cm over its front sight).
                Vector3 graspOffset = gunIdle.InverseTransformPoint(PalmOf(hand, sampler)) - sampler.RelIdle[hi].position;
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
                // A moving part was found. A separate charging handle, or a pistol's slide, is where the hand takes it; so is
                // a part found as the very thing the reload hand pulls (its bone sits a few centimetres off the handle it
                // holds, and the handle was taken for a carrier inside: a knob was added there while the model's own handle
                // moved elsewhere, on the f2000s, MG3s, MRADs, RK-44 and M4A1s; user, 2026-10-16).
                if (d.HandlePath != null || d.Handgun || Vector3.Distance(bestStart, d.RackPoint) < 0.03f
                    // (Also "... carrying the bolt": the closing bracket in the test cut a block out of the M4A1 CCO / EOT family's
                    // receiver while their own handle stood still; weapons-1017.)
                    || d.SlideMethod != null && d.SlideMethod.StartsWith("reload motion (charging handle", StringComparison.Ordinal))
                {
                    return;
                }
                // The rack point sits on the part that moves when firing; the reload hand takes hold somewhere else: the
                // part is a carrier inside the receiver, the handle you see is elsewhere (beside or above it).
                Transform slide = WeaponRig.Resolve(root, d.SlidePath);
                Bounds sb = slide != null ? sampler.GunSpaceBoundsNear(slide, gunIdle, 0.5f) : new Bounds();
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
            d.SlideTravel = -d.AimAxis * Mathf.Clamp(bestLen, d.Handgun ? 0.02f : 0.03f, d.Handgun ? 0.045f : 0.11f);
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
                if (!IsBeltPiece(t.name) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones) || !ShownAtIdle(t, sampler))
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
            // A belt piece is not also a spare magazine: hidden every frame as a spare, the laid belt never showed (255.I3-3).
            d.SparePartPaths.RemoveAll(p => found.Any(f => Related(f, WeaponRig.Resolve(root, p))));
            foreach (Transform t in found)
            {
                d.BeltPartPaths.Add(WeaponRig.PathOf(root, t));
            }
            if (found.Count > 0)
            {
                d.Notes += $"belt in the model: {string.Join(", ", found.Select(f => f.name).ToArray())} (hidden while no belt is laid) ";
            }
        }

        // As words: "lid" inside "Slide" or "Solid" made rifles feed-cover guns (255.I3-6).
        /// <summary>Covers that are no feed cover: a rifle's dust or ejection-port cover, a sight's or scope's lens cap.</summary>
        private static readonly Regex NotFeedCoverName = new Regex(@"dust|port|eject|lens|scope|sight|optic|防尘", RegexOptions.IgnoreCase);
        private static readonly Regex CoverName = new Regex(@"cover|(^|[^a-z])lid($|[^a-z])|(^|[^a-z])tray($|[^a-z])|feed ?(cover|tray|lid)|deckel|couvercle|tapa|крышк|盖", RegexOptions.IgnoreCase);
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
            bool authoredAuto = AuthoredAuto(w);
            bool breakCandidate = !authoredAuto && c.ammo >= 1 && c.ammo <= 4 && d.Action != ActionType.SelfLoading
                                  || !authoredAuto && c.ammo == 2;
            bool revolverCandidate = d.Handgun && !authoredAuto && c.ammo >= 4 && c.ammo <= 9;
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
                // The barrels of a break action: carrying the muzzle, or named so, or reaching it (an M1878's muzzle flash
                // hangs off the gun body, not off the barrels that tip open).
                bool barrels = carriesMuzzle || breakCandidate && acrossBore > 0.75f
                               && (BarrelName.IsMatch(t.name) || sampler.GunSpaceBoundsNear(t, gunIdle, 0.8f).SqrDistance(d.MuzzlePoint) < 0.06f * 0.06f);
                if (barrels && acrossBore > 0.75f && breakCandidate)
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
                         // A belt-fed gun by its size (50 or more, automatic) or a part named a cover: at 40 rounds, a rifle's
                         // flip sight or dust cover made it a feed-cover gun that needed a laid belt to fire (255.I4-5).
                         // A dust, port or lens cover by name is not a feed cover either.
                         && (c.ammo >= 50 && authoredAuto || CoverName.IsMatch(t.name) && !NotFeedCoverName.IsMatch(t.name)))
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
                if (d.Hinge == HingeKind.BreakAction && d.SlidePath != null)
                {
                    // Nothing to rack on a break action: what was taken for its slide moved in the fire animation for
                    // another reason (a hammer falling, the trigger).
                    d.SlidePath = null;
                    d.HandlePath = null;
                    d.SlideMethod = "none (break action)";
                }
            }
            d.IsMachineGun = d.Hinge == HingeKind.FeedCover || authoredAuto && c.ammo >= 60 && !d.Handgun;

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
            Bounds b = sampler.GunSpaceBoundsNear(hinge, gunIdle, 0.8f);
            Vector3 center = b.size.sqrMagnitude > 1e-6f ? b.center : d.HingePivot;
            // A cover is lifted at its rear latch; barrels are pushed down from underneath, half way along.
            d.HingeGrabPoint = d.Hinge == HingeKind.BreakAction
                ? center - d.Up * Mathf.Abs(Vector3.Dot(b.extents, Abs(d.Up))) * 0.8f
                : center - d.AimAxis * Mathf.Abs(Vector3.Dot(b.extents, Abs(d.AimAxis))) * 0.6f + d.Up * 0.01f;

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

        // A rifle's carrier up to 23 cm on a shot (the ak12h 1p87's runs 21 cm straight back and was taken for a casing
        // flying off: the gun got an added knob instead of it; Tasks 245). Casings and flashes go sideways and much farther.
        /// <summary>
        /// One of its own meshes is rocket-sized (15-160 cm along its longest side): the container holds a rocket, not only
        /// pieces that add up to that size round it (bug hunt 251.13).
        /// </summary>
        private static bool HoldsRocketSizedMesh(Transform t)
        {
            foreach (Renderer r in t.GetComponentsInChildren<Renderer>(true))
            {
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>() is MeshFilter mf ? mf.sharedMesh : null;
                if (mesh == null)
                {
                    continue;
                }
                Vector3 size = Vector3.Scale(mesh.bounds.size, r.transform.lossyScale);
                float longest = Mathf.Max(Mathf.Abs(size.x), Mathf.Max(Mathf.Abs(size.y), Mathf.Abs(size.z)));
                if (longest > 0.15f && longest < 1.6f)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The weapon's authored fire mode: the player's selector switches an automatic weapon to single fire at runtime
        /// (configuration.auto false), and analysed then it read as a manual or non-machine gun (bug hunt 251.12).
        /// </summary>
        internal static bool AuthoredAuto(Weapon w) => w != null && w.configuration != null && (w.configuration.auto || FireSelector.IsSemiByPlayer(w));

        private static float MaxBoltTravel(WeaponRigData d) => d.Handgun ? 0.07f : 0.23f;

        /// <summary>Its own mesh is the size of a cocking handle (2-30 cm), not a receiver or a bolt group's housing.</summary>
        private static bool HandleSized(Transform t)
        {
            MeshFilter mf = t.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                return false;
            }
            Vector3 size = Vector3.Scale(mf.sharedMesh.bounds.size, t.lossyScale);
            float longest = Mathf.Max(Mathf.Abs(size.x), Mathf.Max(Mathf.Abs(size.y), Mathf.Abs(size.z)));
            return longest > 0.02f && longest < 0.3f;
        }

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
                // (A part above the slide only when it is handle-sized itself: a PKM's charging handle carries its bolt as a
                // child, and taken for "the bolt's own hierarchy" it was never found; a receiver is far longer.)
                if (exclude != null && (t == exclude || t.IsChildOf(exclude) || exclude.IsChildOf(t) && !HandleSized(t)))
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
                    // Never up into a part that carries the bolt and is not handle-sized itself (a bolt group's housing):
                    // taken for the handle, the whole group was racked as one (review 253.6).
                    if (!index.TryGetValue(p, out int pi) || armRelated.Contains(p) || sampler.ReloadMax[pi] < m0 * 0.8f || MagName.IsMatch(p.name)
                        || exclude != null && exclude.IsChildOf(p) && !HandleSized(p))
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

        /// <summary>
        /// The gun's up from its gun body's own frame: the body's axis nearest the weapon root's up, when within 25 degrees.
        /// The root's up as the flat idle pose leaves it is canted on many rigs (0-8 degrees on most, the 1911's 22): the gun
        /// sat rolled in the hand and its magazine came up the grip from the side (the user's pictures, 2026-10-16). It reads
        /// the gun body's axes only, not the mesh: a body bone whose rest is rolled against its own mesh would be followed
        /// (review 253.5; the 1911 and the RPG-7V check out in the pictures). Per-weapon pitch and gripUp overrides pivot in this
        /// frame (review 253.3).
        /// </summary>
        private static Vector3 UprightByModel(Vector3 up, Vector3 aim, WeaponRigData d)
        {
            Vector3 best = up;
            float bestDot = 0f;
            foreach (Vector3 axis in new[] { Vector3.right, Vector3.left, Vector3.up, Vector3.down, Vector3.forward, Vector3.back })
            {
                Vector3 inPlane = Vector3.ProjectOnPlane(axis, aim);
                if (inPlane.sqrMagnitude < 0.5f)
                {
                    continue; // along the bore
                }
                float dot = Vector3.Dot(inPlane.normalized, up);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    best = inPlane.normalized;
                }
            }
            float cant = Mathf.Acos(Mathf.Clamp(bestDot, -1f, 1f)) * Mathf.Rad2Deg;
            if (cant < 0.5f || cant > 25f)
            {
                return up;
            }
            d.Notes += $"up {cant:0} deg off the model's axis: put upright ";
            return best;
        }

        /// <summary>
        /// A magazine goes up into its well in the gun's side plane (along the bore and up). Its axis from the shape or the
        /// reload tipped sideways put the 1911's 23 degrees out of that plane: it came up beside the grip instead of into it
        /// (the user's pictures, 2026-10-16; measured by the seating check). One more than 45 degrees out is a side-feeder
        /// (a Sten's, from the left) and keeps its axis.
        /// </summary>
        private static void KeepMagazineAxisInSidePlane(WeaponRigData d)
        {
            if (d.Action == ActionType.SingleShot || d.MagazinePath == null || d.MagEjectAxis.sqrMagnitude < 0.5f)
            {
                return;
            }
            Vector3 right = Vector3.Cross(d.Up, d.AimAxis).normalized;
            float tilt = Mathf.Asin(Mathf.Clamp01(Mathf.Abs(Vector3.Dot(d.MagEjectAxis.normalized, right)))) * Mathf.Rad2Deg;
            Vector3 inPlane = Vector3.ProjectOnPlane(d.MagEjectAxis, right);
            if (tilt < 1f || tilt > 45f || inPlane.sqrMagnitude < 1e-4f)
            {
                return;
            }
            d.MagEjectAxis = inPlane.normalized;
            d.Notes += $"magazine axis {tilt:0} deg out of the side plane, put back into it ";
        }

        /// <summary>A belt or a piece of one by its name (a plain "ammo" part on a belt-fed gun is its belt: the PKM's AMMO.001).</summary>
        private static bool IsBeltPiece(string name) => (BeltName.IsMatch(name) || Regex.IsMatch(name, @"^ammo([ ._]?\d+)?$", RegexOptions.IgnoreCase))
                                                        && !Regex.IsMatch(name, @"box|can|弹箱", RegexOptions.IgnoreCase); // a "belt box" is the box (255.I3-7)

        private static void AnalyzeMagazine(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, Transform slide, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, Weapon w)
        {
            // Belt-fed by its size (the feed cover is found after this): a box of 40 rounds or more on an automatic gun.
            bool beltFed = w.configuration != null && w.configuration.ammo >= 40 && w.configuration.auto;
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
                    float fromGrip = Vector3.Distance(sampler.RelIdle[i].position, d.GripPoint);
                    bool launcher = d.Action == ActionType.SingleShot;
                    // A launcher's rocket by its name may be parked away by the reload (that is how its animation hides it:
                    // the Panzerfaust 3's warhead "弹药" waits 30 m off while a new tube comes in).
                    // (A rocket's name only: a spare round or shell carried away by the reload is still parked away; review 253.8.)
                    bool parkedRocket = launcher && LauncherRoundName.IsMatch(t.name) && !IsNotMag(t.name, true) && m > 1.5f;
                    if (launcher && IsNotMag(t.name, true))
                    {
                        continue; // a launcher's controller, sight, safety or cap that its tube swap moves is never its rocket
                    }
                    if (m < 0.03f || m > 1.5f && fromGrip > 0.3f && !parkedRocket)
                    {
                        // Still, or parked far away (belt links, spare rounds). A magazine sitting in the gun may well fly
                        // far in the reload: a G17's is thrown 1.6 m, and the spare one in the other hand was taken instead.
                        continue;
                    }
                    if (launcher && (gunBody != null && gunBody.IsChildOf(t) || sampler.LocalBounds(t).Contains(sampler.Idle(t).Inverse().TransformPoint(
                            gunBody != null ? sampler.Idle(gunBody).TransformPoint(d.GripPoint) : d.GripPoint))))
                    {
                        // Never the launcher's grip unit or what holds its tube: the Panzerfaust 3's reload moves its grip
                        // and sight onto a new tube, and that unit was taken for the rocket (the whole launcher in the hand).
                        continue;
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
                    // In the gun at rest: a reload with two magazines (the old one out, a fresh one in) keeps the fresh one
                    // where the flat view's other hand waits, a few tens of centimetres off.
                    // (A launcher's rocket sits out at the tube's mouth, far from the grip: not counted against it.)
                    // (Only a part named as its round: an unnamed far part on a single-round gun keeps the discount; review 253.2.)
                    float inGun = launcher && IsMagName(t.name, true) ? 1f : Mathf.Lerp(1f, 0.15f, Mathf.InverseLerp(0.22f, 0.45f, fromGrip));
                    float named = IsMagName(t.name, launcher) ? 3f : IsNotMag(t.name, launcher) ? 0.3f : 1f;
                    if (beltFed && IsBeltPiece(t.name))
                    {
                        // A belt-fed gun's belt (or a link of it) is never its box: taken for it, only the belt came out into the
                        // hand, or a link too small to see and only the round count showed (user, 2026-10-16). Demoted, a belt
                        // near the grip still beat a box 45 cm out (255.I4-1).
                        continue;
                    }
                    if (!launcher && sampler.LocalBounds(t).size.magnitude < 0.05f)
                    {
                        continue; // nothing a hand holds: a sub-centimetre box was an invisible stand-in (255.I3-2)
                    }
                    float score = Mathf.Min(m, 0.4f) * named * inGun;
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
                bool roundsMagazine = false;
                if (mag != null && MagazineSizedReload(d, w) && RoundName.IsMatch(mag.name))
                {
                    // The round on top of the magazine moved (an ARS Glock 19's "Bullet" bone, stripped as it chambers) and
                    // the magazine itself did not: the magazine holding it is the magazine. Taken for the round, a single
                    // cartridge came out of the pouch and the real magazine never left the gun.
                    for (Transform p = mag.parent; p != null && p != gunBody && p != root; p = p.parent)
                    {
                        if (IsMagName(p.name) && !RoundName.IsMatch(p.name) && !armRelated.Contains(p))
                        {
                            d.Notes += $"magazine '{p.name}' holds the round '{mag.name}' the reload moved ";
                            mag = p;
                            roundsMagazine = true;
                            break;
                        }
                    }
                }
                if (mag != null)
                {
                    // (Not up from a magazine taken for its round: it did not move, and every parent would pass.)
                    for (Transform p = mag.parent; !roundsMagazine && p != null && p != gunBody && p != root; p = p.parent)
                    {
                        if (!index.TryGetValue(p, out int pi) || armRelated.Contains(p) || (slide != null && p == slide)
                            || gunBody != null && gunBody.IsChildOf(p) // never up into what holds the gun body (the launcher itself)
                            || IsNotMag(p.name, d.Action == ActionType.SingleShot) // nor a launcher's controller, sight or safety
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
                        if (beltFed && IsBeltPiece(t.name) || d.Action != ActionType.SingleShot && sampler.LocalBounds(t).size.magnitude < 0.05f)
                        {
                            continue; // a belt piece, or a speck (255.I4-1, I3-2)
                        }
                        float hidden = HiddenSeconds(sampler.ReloadVisible[i]);
                        bool single = d.Action == ActionType.SingleShot;
                        float score = hidden * (IsMagName(t.name, single) ? 3f : IsNotMag(t.name, single) ? 0.3f : 1f);
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
                mag = FindMagByName(all, root, gunBody, muzzle, armRelated, gunBones, d.Action == ActionType.SingleShot, beltFed);
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

            if (d.Action == ActionType.SingleShot && Longest(sampler.LocalBounds(mag)) < 0.12f)
            {
                // A launcher's rocket found as a near-empty part (the scoped RPG-7V's "RPG-7_Rocket" holds only its 3 mm
                // safety pin; the rocket's mesh is its sibling): the container above it that moves with it in the reload
                // and holds a rocket-sized mesh, never the launcher itself. The rocket in the hand was the made stand-in.
                Vector3 gripWorld = gunBody != null ? sampler.Idle(gunBody).TransformPoint(d.GripPoint) : Vector3.zero;
                for (Transform up = mag.parent; up != null && up != gunBody && up != root; up = up.parent)
                {
                    Bounds upBounds = sampler.LocalBounds(up);
                    if (gunBody != null && (gunBody.IsChildOf(up) || upBounds.Contains(sampler.Idle(up).Inverse().TransformPoint(gripWorld))))
                    {
                        break; // the launcher itself (it holds the grip or the gun body): the Panzerfaust 3 handed out its whole launcher
                    }
                    float size = Longest(upBounds);
                    if (size > 0.15f && size < 1.6f && index.ContainsKey(up) && HoldsRocketSizedMesh(up) && !IsNotMag(up.name, true))
                    {
                        d.Notes += $"rocket taken as '{up.name}', {size * 100f:0} cm (its part '{mag.name}' is too small for one) ";
                        mag = up;
                        break;
                    }
                    if (size >= 1.6f)
                    {
                        break;
                    }
                }
            }

            int mi = index[mag];
            d.MagazinePath = WeaponRig.PathOf(root, mag);
            d.MagazineName = mag.name;
            CollectSpareMagazines(d, all, index, root, gunBody, muzzle, slide, mag, armRelated, gunBones, sampler);
            d.MagazineToggles = toggles;
            d.MagazineIsSkinned = !HasRendererInSubtree(mag) && gunBones.Contains(mag);
            if (d.MagMethod == "reload motion" && d.MagazineIsSkinned)
            {
                d.MagMethod = "reload motion (skinned bone)";
            }
            d.MagSeat = sampler.RelIdle[mi];
            d.MagRestPos = d.MagSeat.position;
            if (d.Action == ActionType.SingleShot && sampler.HasReload && (d.MagSeat.position - d.GripPoint).sqrMagnitude > 1.5f * 1.5f)
            {
                // A launcher's rocket parked away at rest (the Panzerfaust 3's warhead 30 m off): seated where its reload
                // leaves it, on the gun. Before, only a live pose taken while it sat loaded seated it, and a launcher first
                // drawn empty kept a seat 30 m away (review 253.1).
                Vector3[] path = sampler.ReloadTrajectory[mi];
                if (path != null && path.Length > 0)
                {
                    Vector3 end = path[path.Length - 1];
                    if ((end - d.GripPoint).sqrMagnitude < 1.5f * 1.5f && !float.IsNaN(end.x))
                    {
                        d.MagSeat = new RigidPose(end, d.MagSeat.rotation);
                        d.Notes += "rocket seated where its reload leaves it (parked away at rest) ";
                    }
                }
            }
            float OffBore(Vector3 p) => Vector3.ProjectOnPlane(p - d.MuzzlePoint, d.AimAxis).magnitude;
            if ((d.Action == ActionType.SelfLoading || d.Action == ActionType.Manual || EnBlocName.IsMatch(d.Key ?? ""))
                && sampler.HasReload && OffBore(d.MagSeat.position) > 0.25f)
            {
                // Any magazine parked away from the gun at rest is seated where its reload leaves it, on the gun, as a
                // launcher's rocket is: the M1 GARRET's clip waited 50 cm beside the gun, and the fresh one was seated there,
                // in the air (check-1017f, the seating pictures show the clip alone).
                // Where the reload brings it nearest the bore: in the gun (the M1's reload parks the clip away again at its end).
                Vector3[] path = sampler.ReloadTrajectory[mi];
                Vector3 end = d.MagSeat.position;
                if (path != null)
                {
                    foreach (Vector3 p in path)
                    {
                        if (!float.IsNaN(p.x) && OffBore(p) < OffBore(end))
                        {
                            end = p;
                        }
                    }
                }
                if (OffBore(end) < 0.12f)
                {
                    d.Notes += $"magazine parked {OffBore(d.MagSeat.position) * 100f:0} cm off the bore at rest: seated where its reload brings it into the gun ";
                    d.MagSeat = new RigidPose(end, d.MagSeat.rotation);
                }
                else
                {
                    d.Warnings.Add($"magazine seat {OffBore(d.MagSeat.position) * 100f:0} cm off the bore (its reload comes no nearer than {OffBore(end) * 100f:0} cm)");
                }
            }
            else if (sampler.HasReload && OffBore(d.MagSeat.position) > 0.25f)
            {
                d.Notes += $"magazine {OffBore(d.MagSeat.position) * 100f:0} cm off the bore at rest, left there ({d.Action}) ";
            }
            Vector3 eject = sampler.HasReload && !toggles ? sampler.FirstDir[mi] : Vector3.zero;
            d.MagEjectAxis = eject.sqrMagnitude > 0.5f ? eject.normalized : -d.Up;
            Bounds mb = sampler.LocalBounds(mag);
            if (mb.size.sqrMagnitude > 1e-6f)
            {
                d.MagBounds = mb;
                MagAxisFromShape(d, mb, eject.sqrMagnitude > 0.5f, mag);
            }

            if (sampler.HasReload)
            {
                CollectMagazineParts(d, all, index, root, gunBody, muzzle, slide, mag, armRelated, gunBones, sampler, toggles);
                MagKeyframes(d, all, index, root, gunBody, muzzle, slide, mag, armRelated, gunBones, sampler, toggles);
            }
            // A skinned gun's other magazine bones where its magazine sits (the ARS guns' "MagSwap", the magazine their own
            // reload brings in, drawn in the same place): hidden with it, not copied (Tasks 232).
            if (d.MagazineIsSkinned)
            {
                Vector3 magAt = sampler.RelIdle[mi].position;
                foreach (Transform t in all)
                {
                    if (t == mag || !gunBones.Contains(t) || t.IsChildOf(mag) || mag.IsChildOf(t) || !index.TryGetValue(t, out int ti)
                        || t.name.IndexOf("mag", StringComparison.OrdinalIgnoreCase) < 0 || NotMagName.IsMatch(t.name))
                    {
                        continue;
                    }
                    if (Vector3.Distance(sampler.RelIdle[ti].position, magAt) < 0.03f)
                    {
                        d.MagHideOnlyPaths.Add(WeaponRig.PathOf(root, t));
                        d.Notes += $"'{t.name}' hidden with the magazine (a second one drawn in its place) ";
                    }
                }
            }
            // What of it shows while it sits loaded, at rest (one warhead of five on an RPG-7V made for all of them).
            var magTransforms = new List<Transform> { mag };
            foreach (int[] p in d.MagazinePartPaths)
            {
                Transform part = WeaponRig.Resolve(root, p);
                if (part != null && !magTransforms.Contains(part))
                {
                    magTransforms.Add(part);
                }
            }
            foreach (Transform part in magTransforms)
            {
                foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                {
                    // A static rig's idle is the live pose, whose visibility reads only active objects: a renderer the game
                    // keeps switched off (four warheads of five) counted as shown and was switched on once loaded (review 253.10).
                    if (!(r is ParticleSystemRenderer) && sampler.IdleVisible(r.transform) && (d.Animated || r.enabled))
                    {
                        d.MagShownAtIdle.Add(WeaponRig.PathOf(root, r.transform));
                    }
                }
            }
        }

        /// <summary>
        /// Spare magazines of the flat reload animation: drawn at rest where its other hand waits, a few tens of centimetres
        /// from the gun (out of the flat view), moved in by the reload. Magazine-like (by name, or the chosen magazine's
        /// size), not part of the chosen one. In VR they hung in the air by the gun.
        /// </summary>
        /// <summary>
        /// How near a handgun's grip a spare magazine may wait: its reload's other hand holds the next one just behind the
        /// pistol, and at 28 cm it was drawn there in VR (user, 2026-10-16). Its own magazine, in the grip, is never one.
        /// </summary>
        private const float SpareNearHandgun = 0.12f;

        private static void CollectSpareMagazines(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, Transform slide, Transform mag, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler)
        {
            float magSize = sampler.LocalBounds(mag).size.magnitude;
            var found = new List<Transform>();
            foreach (Transform t in all)
            {
                if (t == mag || t.IsChildOf(mag) || mag.IsChildOf(t) || !index.TryGetValue(t, out int i)
                    || !MagCandidate(t, root, gunBody, muzzle, slide, armRelated, gunBones) || !ShownAtIdle(t, sampler)
                    || sampler.ReloadMax[i] < 0.03f || Vector3.Distance(sampler.RelIdle[i].position, d.GripPoint) < (d.Handgun ? SpareNearHandgun : 0.28f))
                {
                    continue;
                }
                float size = sampler.LocalBounds(t).size.magnitude;
                bool single = d.Action == ActionType.SingleShot;
                if (size > 0.6f || !IsMagName(t.name, single) && (magSize < 0.01f || Mathf.Abs(size - magSize) > magSize * 0.4f)
                    || IsNotMag(t.name, single) || OpticName.IsMatch(t.name))
                {
                    continue;
                }
                // Nothing on the bore line: a suppressor on the muzzle moves in some reloads too (an HK416-B/S's), and is
                // the size of a magazine.
                Vector3 at = sampler.RelIdle[i].position;
                if (Vector3.ProjectOnPlane(at - d.MuzzlePoint, d.AimAxis).magnitude < 0.06f || Regex.IsMatch(t.name, @"suppress|silenc|muzzle|消音|枪口", RegexOptions.IgnoreCase))
                {
                    continue;
                }
                found.Add(t);
            }
            foreach (Transform t in found)
            {
                bool underAnother = false;
                foreach (Transform o in found)
                {
                    underAnother |= o != t && t.IsChildOf(o);
                }
                if (underAnother || d.SparePartPaths.Count >= 4)
                {
                    continue;
                }
                d.SparePartPaths.Add(WeaponRig.PathOf(root, t));
                d.Notes += $"spare magazine '{t.name}' ({Vector3.Distance(sampler.RelIdle[index[t]].position, d.GripPoint) * 100f:0} cm from the grip) hidden while held ";
            }
        }

        /// <summary>
        /// An AR whose reload animation moves its bolt carrier, not its T charging handle (the model's T never moves in its
        /// animations): the carrier was taken for "its own handle", and racked, the carrier slid back in the ejection port
        /// while the T stood still (the M4A1 [SQUAD] family, prefab M4A1squad; user, 2026-10-16; picture review of
        /// weapons-1017). The T is the small part at the moving part's rear top that the reload leaves where it is: it is the
        /// handle now, moved by the stroke, and the carrier follows.
        /// </summary>
        private static void ArStaticHandle(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, RigidPose gunIdle)
        {
            // Also a bolt found by its fire motion with no handle (K13: its bolt '416F QJ' moved unseen in the receiver while
            // its T '416F ZG', 6 cm behind the bolt's rear top, stood still; check-1017f).
            if (d.Handgun || d.HandlePath != null || d.SlidePath == null || d.SlideMethod == null || !sampler.HasReload
                || !(d.SlideMethod.StartsWith("reload motion (charging handle)", StringComparison.Ordinal) || d.SlideMethod == "fire motion")
                || !ArPattern.IsMatch(d.Key ?? ""))
            {
                return;
            }
            Transform slide = WeaponRig.Resolve(root, d.SlidePath);
            Bounds sb = slide != null ? sampler.GunSpaceBoundsNear(slide, gunIdle, 0.5f) : new Bounds();
            if (sb.size.sqrMagnitude < 1e-6f)
            {
                return;
            }
            Vector3 aimAbs = new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z));
            Vector3 upAbs = new Vector3(Mathf.Abs(d.Up.x), Mathf.Abs(d.Up.y), Mathf.Abs(d.Up.z));
            Vector3 rearTop = sb.center - d.AimAxis * Mathf.Abs(Vector3.Dot(sb.extents, aimAbs)) + d.Up * Mathf.Abs(Vector3.Dot(sb.extents, upAbs));
            Transform best = null;
            Bounds bestBounds = default;
            // The fire-motion case reaches a little further (the K13's T sits 6 cm behind its bolt), but only for a flat part (a
            // T is at most 1.5 cm tall; a forward assist beside the bolt is not).
            bool fireCase = d.SlideMethod == "fire motion";
            float bestGap = fireCase ? 0.07f : 0.05f;
            foreach (Transform t in all)
            {
                if (!index.TryGetValue(t, out int i) || sampler.ReloadMax[i] > 0.01f || t == slide || t.IsChildOf(slide) || slide.IsChildOf(t)
                    || UnderOptic(t, gunBody)
                    || MagName.IsMatch(t.name) || OpticName.IsMatch(t.name) || !sampler.IdleVisible(t) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                {
                    continue;
                }
                Bounds b = sampler.GunSpaceBounds(t, gunIdle);
                float length = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                float above = Vector3.Dot(b.center - d.MuzzlePoint, d.Up);
                if (length < 0.03f || length > 0.12f || above < 0f || above > 0.06f || fireCase && Vector3.Dot(b.size, upAbs) > 0.015f)
                {
                    continue; // a T is a few centimetres long and sits just over the bore, under the sights
                }
                float gap = Mathf.Sqrt(b.SqrDistance(rearTop));
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = t;
                    bestBounds = b;
                }
            }
            if (best == null)
            {
                return;
            }
            d.HandlePath = WeaponRig.PathOf(root, best);
            d.HandleRestLocal = sampler.IdleLocalPosition(best);
            d.HandleTravel = -d.AimAxis * Mathf.Min(0.075f, d.SlideTravel.magnitude > 1e-4f ? d.SlideTravel.magnitude : 0.075f);
            d.RackPoint = bestBounds.center - d.AimAxis * Mathf.Abs(Vector3.Dot(bestBounds.extents, aimAbs)) * 0.8f;
            d.SlideMethod += $" + its T handle '{best.name}' (still in the animations)";
        }

        /// <summary>A part under (or itself) a sight or optic by name, up to the gun body: the K13 ACOG's rear sight 'K13_STGHT1'
        /// (a typo, under 'K13_SIGHT2') was taken for its T handle (check-1017g).</summary>
        private static bool UnderOptic(Transform t, Transform gunBody)
        {
            for (Transform p = t; p != null && p != gunBody; p = p.parent)
            {
                if (OpticName.IsMatch(p.name))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The point on a gun-space box's face that looks away from the bore along <paramref name="n"/>: on the face of the
        /// box's axis most along it (the box's corner along a diagonal lay in the air beside a thin lever; rule review).
        /// </summary>
        private static Vector3 OuterFace(Bounds b, Vector3 n)
        {
            Vector3 a = new Vector3(Mathf.Abs(n.x), Mathf.Abs(n.y), Mathf.Abs(n.z));
            int axis = a.x >= a.y && a.x >= a.z ? 0 : a.y >= a.z ? 1 : 2;
            Vector3 at = b.center;
            at[axis] += Mathf.Sign(n[axis]) * b.extents[axis];
            return at;
        }

        /// <summary>
        /// Where a hand takes a long gun's slide when the slide itself is what it racks (no separate handle): on the small
        /// piece of it that sticks out of the receiver farthest from the bore (a paddle, knob or lever), not at the middle of
        /// the carrier inside. The grab point sat on the receiver above the carrier, 5-10 cm from the handle the hand would
        /// take: on the QBZ192 / QBZU191 family's paddle, the PKM / PKP family's and QJB201's levers (picture review of
        /// weapons-1017).
        /// </summary>
        private static void RackOnOutstandingPiece(WeaponRigData d, Transform root, PoseSampler sampler, RigidPose gunIdle)
        {
            if (d.Handgun || d.SlidePath == null || d.HandlePath != null || d.CarveHandle || d.AddedHandle || d.RackFromHand || d.RackOnSurface
                || d.SlideMethod == null || d.SlideMethod.StartsWith("reload motion (charging handle)", StringComparison.Ordinal) || ArPattern.IsMatch(d.Key ?? ""))
            {
                return;
            }
            Transform slide = WeaponRig.Resolve(root, d.SlidePath);
            if (slide == null)
            {
                return;
            }
            float Off(Vector3 p) => Vector3.ProjectOnPlane(p - d.MuzzlePoint, d.AimAxis).magnitude;
            float best = Off(d.RackPoint) + 0.015f;
            Transform piece = null;
            Vector3 at = d.RackPoint;
            foreach (MeshFilter mf in slide.GetComponentsInChildren<MeshFilter>(true))
            {
                // Its own mesh only: with its children's, a 0.0 cm piece took the box of the cap under it and the point
                // sat in the air beside both (xm8 family, 252 ae review).
                Transform self = mf.transform;
                Bounds b = sampler.GunSpaceBoundsWhere(self, gunIdle, t => t != self);
                if (b.size.sqrMagnitude < 1e-8f || b.size.magnitude > 0.12f)
                {
                    continue; // only a piece a hand takes (not the carrier or a long rod)
                }
                float off = Off(b.center);
                if (off > best)
                {
                    best = off;
                    piece = self;
                    // On its outer face (away from the bore), where the fingers take it, not at its middle.
                    Vector3 n = Vector3.ProjectOnPlane(b.center - d.MuzzlePoint, d.AimAxis);
                    n = n.sqrMagnitude > 1e-8f ? n.normalized : d.Up;
                    at = OuterFace(b, n);
                }
            }
            if (piece != null)
            {
                d.RackPoint = at;
                d.SlideMethod += $" (taken by '{piece.name}', its piece sticking out)";
            }
        }

        /// <summary>
        /// Parts that ride with the slide but hang beside it in the model, named after it: the Vector's cocking knob
        /// ('..._vector_slide_rotation_9x19...') is a sibling of its bolt ('..._vector_slide_9x19...') and no animation moves
        /// either, so the bolt slid unseen inside the receiver while the knob the hand held stood still (252 ae). Such a part
        /// carries every word of the slide's name and one or two more (not a release, catch, magazine, sight or level of
        /// detail), stays still in every animation, is no bigger than the slide and touches it. It moves with the slide, and
        /// the hand takes it (its outer face). A name match that fails the rest is said in the notes.
        /// </summary>
        private static void FindSlideCompanions(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            PoseSampler sampler, RigidPose gunIdle)
        {
            d.SlideCompanionPaths.Clear();
            d.SlideCompanionRest.Clear();
            Transform slide = WeaponRig.Resolve(root, d.SlidePath);
            if (slide == null || gunBody == null || d.HandlePath != null || d.BoltPath != null || d.CarveHandle || d.AddedHandle
                || !StrongSlideName.IsMatch(slide.name))
            {
                return;
            }
            HashSet<string> slideWords = NameWords(slide.name);
            Bounds sb = sampler.GunSpaceBoundsNear(slide, gunIdle, 0.5f);
            if (slideWords.Count == 0 || sb.size.sqrMagnitude < 1e-6f)
            {
                return;
            }
            Bounds reach = sb;
            reach.Expand(0.06f); // 3 cm each way
            Transform first = null;
            Bounds firstBox = default;
            foreach (Transform t in all)
            {
                if (t == slide || t == gunBody || !t.IsChildOf(gunBody) || t.IsChildOf(slide) || slide.IsChildOf(t)
                    || t.GetComponent<MeshRenderer>() == null || !index.TryGetValue(t, out int i) || !sampler.IdleVisible(t))
                {
                    continue;
                }
                HashSet<string> words = NameWords(t.name);
                if (!words.IsSupersetOf(slideWords))
                {
                    continue;
                }
                var extra = words.Where(w => !slideWords.Contains(w)).ToList();
                if (extra.Count == 0 || extra.Count > 2 || extra.Any(w => CompanionBlocked.IsMatch(w)))
                {
                    continue; // a copy of it (another level of detail) or a release, catch or sight named after it
                }
                Transform self = t;
                Bounds b = sampler.GunSpaceBoundsWhere(t, gunIdle, x => x != self);
                string why = sampler.FireMax[i] > 0.002f || sampler.FireRotMax[i] > 2f || sampler.HasReload && (sampler.ReloadMax[i] > 0.005f || sampler.ReloadRotMax[i] > 5f)
                        ? "an animation moves it"
                    : b.size.magnitude < 0.01f ? "too small"
                    : b.size.magnitude > sb.size.magnitude * 1.05f || b.size.magnitude > 0.25f ? "bigger than the slide"
                    : !reach.Intersects(b) ? $"{Mathf.Sqrt(sb.SqrDistance(b.ClosestPoint(sb.center))) * 100f:0.0} cm from the slide"
                    : null;
                if (why != null)
                {
                    d.Notes += $"slide companion '{t.name}' left out ({why}) ";
                    continue;
                }
                d.SlideCompanionPaths.Add(WeaponRig.PathOf(root, t));
                d.SlideCompanionRest.Add(sampler.IdleLocalPosition(t));
                d.Notes += $"slide companion '{t.name}' (moves with the slide) ";
                if (first == null)
                {
                    first = t;
                    firstBox = b;
                }
                if (d.SlideCompanionPaths.Count >= 3)
                {
                    break;
                }
            }
            if (first != null)
            {
                // The hand takes the companion (a knob on the outside), not the slide inside the receiver.
                Vector3 n = Vector3.ProjectOnPlane(firstBox.center - d.MuzzlePoint, d.AimAxis);
                n = n.sqrMagnitude > 1e-8f ? n.normalized : d.Up;
                d.RackPoint = OuterFace(firstBox, n);
                d.SlideMethod += $" + '{first.name}' moving with it (named after it; taken there)";
            }
        }

        /// <summary>Words a slide companion may add to the slide's name: not these.</summary>
        private static readonly Regex CompanionBlocked = new Regex(
            @"^(release|catch|stop|button|latch|selector|safety|switch|mag|magazine|sight|scope|optic|light|laser|trigger|spring|pin|screw|lod\d*)$",
            RegexOptions.IgnoreCase);

        /// <summary>A part name's words (lower case; numbers alone and level-of-detail tags left out).</summary>
        private static HashSet<string> NameWords(string name)
        {
            var words = new HashSet<string>();
            foreach (string w in Regex.Split(name.ToLowerInvariant(), @"[^a-z0-9\u0400-\u04ff\u4e00-\u9fff]+"))
            {
                if (w.Length >= 2 && !Regex.IsMatch(w, @"^\d+$") && !Regex.IsMatch(w, @"^lod\d*$"))
                {
                    words.Add(w);
                }
            }
            return words;
        }

        /// <summary>
        /// Rounds and shells of the flat reload animation: they wait at rest beside the gun, out of the flat view, until the
        /// reload's hand brings them in (an MW M870's two shells hung in the air by the gun hand in VR). Small, drawn at
        /// rest, carried far by the reload, outside the gun at rest, and no magazine, slide or handle: not drawn while
        /// held, like the spare magazines.
        /// </summary>
        private static void CollectReloadRounds(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform gunBody,
            Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, RigidPose gunIdle)
        {
            if (!sampler.HasReload || gunBody == null)
            {
                return;
            }
            // The gun at rest without what its reload moves, drawn and not parked away: what the rounds wait outside of.
            Vector3 bodyAt = sampler.Idle(gunBody).position;
            Bounds still = sampler.GunSpaceBoundsWhere(gunBody, gunIdle, t => !index.TryGetValue(t, out int k) || sampler.ReloadMax[k] > 0.03f
                                                                              || !sampler.IdleVisible(t) || (sampler.Idle(t).position - bodyAt).sqrMagnitude > 1.5f * 1.5f);
            if (still.size.sqrMagnitude < 1e-6f)
            {
                return;
            }
            still.Expand(0.01f); // the M870's second shell waited 3 cm beside the receiver
            // Everything already chosen: the magazine and what travels with it, the belt, the slide, handle and hinge, the
            // spare magazines.
            var paths = new List<int[]> { d.MagazinePath, d.SlidePath, d.HandlePath, d.HingePath };
            paths.AddRange(d.MagazinePartPaths);
            paths.AddRange(d.BeltPartPaths);
            paths.AddRange(d.SparePartPaths);
            var parts = new List<Transform>();
            foreach (int[] path in paths)
            {
                Transform part = path != null ? WeaponRig.Resolve(root, path) : null;
                if (part != null)
                {
                    parts.Add(part);
                }
            }
            var found = new List<Transform>();
            foreach (Transform t in all)
            {
                // Under the gun's body only (a sight or bayonet on the rig's root is no round), and nothing named as a part.
                if (!index.TryGetValue(t, out int i) || sampler.ReloadMax[i] < 0.15f || !t.IsChildOf(gunBody) || !HasRendererInSubtree(t) || !ShownAtIdle(t, sampler)
                    || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones) || parts.Exists(p => t == p || t.IsChildOf(p) || p.IsChildOf(t))
                    || OpticName.IsMatch(t.name) || SlideName.IsMatch(t.name) || ChargingName.IsMatch(t.name))
                {
                    continue;
                }
                Bounds b = sampler.GunSpaceBounds(t, gunIdle);
                if (b.size.magnitude > 0.15f || b.size.sqrMagnitude < 1e-8f || still.Contains(b.center)
                    || Vector3.ProjectOnPlane(b.center - d.MuzzlePoint, d.AimAxis).magnitude < 0.035f)
                {
                    continue;
                }
                found.Add(t);
            }
            foreach (Transform t in found)
            {
                if (found.Exists(o => o != t && t.IsChildOf(o)))
                {
                    continue;
                }
                if (d.SparePartPaths.Count >= 8)
                {
                    d.Warnings.Add($"more reload props than hidden: '{t.name}' and later ones stay drawn");
                    break;
                }
                d.SparePartPaths.Add(WeaponRig.PathOf(root, t));
                d.Notes += $"reload prop '{t.name}' ({sampler.ReloadMax[index[t]] * 100f:0} cm moved by the reload, beside the gun at rest) hidden while held ";
            }
        }

        /// <summary>
        /// A box magazine leaves its well along its own length. The reload animation's first motion is often something
        /// else (an AK magazine rocked forward off its front lug, a hand that tugs it sideways first), and an insert axis
        /// taken from that tilts the magazine in the hand and makes it go in crooked. A clearly elongated magazine that
        /// hangs below the gun uses its own long axis, pointing out of the gun; drums and boxes keep the animated one.
        /// </summary>
        private static void MagAxisFromShape(WeaponRigData d, Bounds local, bool animated, Transform magPart = null)
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

            if (magPart != null && WeaponSelfTest.WantsRigDump(d.Key))
            {
                d.DumpLines += MagAxisDiagnosis(d, magPart, local, animated);
            }
            Vector3 s = local.size;
            int longest = s.x >= s.y && s.x >= s.z ? 0 : s.y >= s.z ? 1 : 2;
            float second = longest == 0 ? Mathf.Max(s.y, s.z) : longest == 1 ? Mathf.Max(s.x, s.z) : Mathf.Max(s.x, s.y);
            Vector3 axisLocal = Vector3.zero;
            axisLocal[longest] = 1f;
            bool fromVertices = false;
            if (magPart != null && PrincipalAxis(magPart, out Vector3 principal, out float elongation) && elongation >= 1.5f && s[longest] >= 0.05f)
            {
                fromVertices = true;
                // The magazine's own long axis from its vertices: a pistol magazine (or a raked one) modelled in place, in a
                // frame lined up with the gun, has its bounding box upright, and the box's longest side pointed straight
                // down from the bore instead of along the rake. Slid in on that line, it travelled in front of the grip
                // and seemed to float there (M1911, P220).
                if (Vector3.Angle(principal, axisLocal) > 4f)
                {
                    d.Notes += $"magazine axis from its vertices ({Vector3.Angle(principal, axisLocal) % 180f:0}° off its box) ";
                }
                axisLocal = principal;
                // A curved magazine (an AK's, a QBZ's) goes into its well along its top, not along the chord of its curve:
                // slid on the chord, the fresh one came in 20 degrees too flat, in front of the well (QBZ 95-1 family).
                // Not a pistol's: its magazine is straight, and the round and the follower plate at its top skewed the top
                // slices: the 1911 10R's "top" leaned 24 degrees the other way from its body's own axis, and the fresh one came
                // in across the front strap (check-1017f; its body, the plain 1911's and the D1911's all rake about 0.2 the
                // way the vertex axis does).
                Vector3 intoGunLocal = Quaternion.Inverse(d.MagSeat.rotation) * -outward;
                if (!d.Handgun && TopTangent(magPart, principal, intoGunLocal, out Vector3 top))
                {
                    float bend = Vector3.Angle(top, Vector3.Dot(top, principal) < 0f ? -principal : principal);
                    if (bend > 5f && bend < 35f)
                    {
                        d.Notes += $"curved magazine: in along its top ({bend:0}° off its chord) ";
                        axisLocal = top;
                    }
                }
            }
            else if (s[longest] < 0.05f || s[longest] < second * 1.5f)
            {
                return; // short, a drum or a block: no reliable axis of its own
            }
            Vector3 axis = (d.MagSeat.rotation * axisLocal).normalized;
            if (Vector3.Dot(axis, outward) < 0f)
            {
                axis = -axis;
            }
            if (Vector3.Angle(axis, outward) > 60f)
            {
                return; // lies along the gun rather than hanging off it: trust the animation
            }
            if (!fromVertices && d.Handgun && d.GripDown.sqrMagnitude > 0.5f && Vector3.Angle(axis, d.GripDown) < 45f)
            {
                // A pistol whose magazine cannot be read: it runs up the grip, along the line of the gun hand's knuckles
                // (its box stands upright: a dropped GLOCK 43X or P320s magazine slid straight down, out through the front
                // of the raked grip, and a fresh one came in on that line beside the well).
                d.MagEjectAxis = d.GripDown;
                d.Notes += $"magazine axis along the grip (the gun hand's knuckles; its box was {Vector3.Angle(axis, d.GripDown):0}° off) ";
                return;
            }
            // Only a handgun's (its grip is raked): a long gun's magazine box is its well's line, and its reload animation's
            // pull swings it out on an arc, 18 degrees toward the grip on the HK416-B/D: slid in on that, the magazine went in
            // behind the well, through the trigger guard (user, 2026-10-16).
            if (!fromVertices && animated && d.Handgun && Vector3.Angle(axis, d.MagEjectAxis) < 20f)
            {
                // Its vertices could not be read, so this is only its bounding box, which stands upright, straight down
                // from the bore, while a pistol's grip is raked: a dropped magazine slid straight down and its front
                // poked out through the front of the grip (P320s). The animated pull, close to it, follows the rake.
                d.Notes += $"magazine axis from the animated pull (its shape unreadable; the box is {Vector3.Angle(axis, d.MagEjectAxis):0}° off it) ";
                return;
            }
            if (animated && Vector3.Angle(axis, d.MagEjectAxis) > 8f)
            {
                d.Notes += $"magazine axis from its shape ({Vector3.Angle(axis, d.MagEjectAxis):0}° off the animated pull) ";
            }
            d.MagEjectAxis = axis;
        }

        /// <summary>
        /// The long axis of a part's own meshes (in its frame), from their vertices: the direction they spread along most.
        /// <paramref name="elongation"/>: how much longer than wide (ratio of the spreads). False when no mesh can be read.
        /// </summary>
        internal static bool PrincipalAxis(Transform part, out Vector3 axisLocal, out float elongation)
        {
            // From the meshes shown at rest near the part, as its bounds are (PoseSampler.LocalBounds): a PKM box's belt
            // links parked 25 m away under it turned the axis toward them. All of them when none is shown.
            return PrincipalAxis(part, true, out axisLocal, out elongation) || PrincipalAxis(part, false, out axisLocal, out elongation);
        }

        /// <summary>
        /// The direction of a magazine's centreline at its top end (the end toward <paramref name="intoGunLocal"/>): the
        /// centroids of thin slices across its long axis, from a quarter of its length to its top. Same as the long axis
        /// for a straight magazine. False when its meshes cannot be read.
        /// </summary>
        internal static bool TopTangent(Transform part, Vector3 axisLocal, Vector3 intoGunLocal, out Vector3 tangentLocal)
        {
            tangentLocal = axisLocal;
            List<Vector3> points = MeshPoints(part, true);
            if (points.Count < 8)
            {
                points = MeshPoints(part, false);
            }
            if (points.Count < 32)
            {
                return false;
            }
            Vector3 axis = Vector3.Dot(axisLocal, intoGunLocal) >= 0f ? axisLocal.normalized : -axisLocal.normalized; // toward the top
            float lo = float.MaxValue, hi = float.MinValue;
            foreach (Vector3 p in points)
            {
                float t = Vector3.Dot(p, axis);
                lo = Mathf.Min(lo, t);
                hi = Mathf.Max(hi, t);
            }
            float length = hi - lo;
            if (length < 0.05f)
            {
                return false;
            }
            // Slices well inside the ends: the top round and the feed lips (and a base plate's lip) are left out.
            bool Centroid(float from, float to, out Vector3 c)
            {
                c = Vector3.zero;
                int n = 0;
                foreach (Vector3 p in points)
                {
                    float f = (hi - Vector3.Dot(p, axis)) / length; // 0 at the top, 1 at the base
                    if (f >= from && f < to)
                    {
                        c += p;
                        n++;
                    }
                }
                if (n < 4)
                {
                    return false;
                }
                c /= n;
                return true;
            }
            if (!Centroid(0.08f, 0.2f, out Vector3 upper) || !Centroid(0.3f, 0.42f, out Vector3 lower))
            {
                return false;
            }
            Vector3 dir = upper - lower;
            if (dir.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            tangentLocal = dir.normalized;
            return true;
        }

        /// <summary>The vertices of a part's meshes in its frame (a few thousand at most), as <see cref="PrincipalAxis(Transform, bool, out Vector3, out float)"/> takes them.</summary>
        private static List<Vector3> MeshPoints(Transform part, bool shownNearOnly)
        {
            var points = new List<Vector3>();
            foreach (MeshFilter mf in part.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable || m.vertexCount == 0)
                {
                    continue;
                }
                if (shownNearOnly && (!ActiveUpTo(mf.transform, part) || (mf.transform.position - part.position).sqrMagnitude > 1.5f * 1.5f))
                {
                    continue;
                }
                Vector3[] v = m.vertices;
                int step = Mathf.Max(1, v.Length / 4000);
                for (int i = 0; i < v.Length; i += step)
                {
                    points.Add(part.InverseTransformPoint(mf.transform.TransformPoint(v[i])));
                }
            }
            return points;
        }

        /// <summary>
        /// Rig dump: what the magazine's axis is made of. The animated pull, the box's longest side, the vertex axis with and
        /// without the parked pieces, the top tangent, and every mesh under the magazine with whether it counts (active up to
        /// the magazine, its renderer on, near it) and its own long axis, in gun space.
        /// </summary>
        private static string MagAxisDiagnosis(WeaponRigData d, Transform part, Bounds local, bool animated)
        {
            var sb = new System.Text.StringBuilder();
            Vector3 Gun(Vector3 axisLocal) => (d.MagSeat.rotation * axisLocal).normalized;
            sb.AppendLine($"    magazine axis of '{part.name}': animated pull {(animated ? d.MagEjectAxis.ToString("F2") : "none")}, box {local.size * 100f:F1} cm");
            foreach (bool near in new[] { true, false })
            {
                if (PrincipalAxis(part, near, out Vector3 p, out float e))
                {
                    string top = TopTangent(part, p, Quaternion.Inverse(d.MagSeat.rotation) * d.Up, out Vector3 tt)
                        ? $", top tangent {Gun(tt):F2} ({Vector3.Angle(tt, Vector3.Dot(tt, p) < 0f ? -p : p):0}° off)" : "";
                    sb.AppendLine($"      vertex axis ({(near ? "shown near" : "all meshes")}): {Gun(p):F2}, {e:0.0} times longer than wide{top}");
                }
            }
            foreach (MeshFilter mf in part.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                bool active = ActiveUpTo(mf.transform, part);
                bool counts = m != null && m.isReadable && active && (mf.transform.position - part.position).sqrMagnitude <= 1.5f * 1.5f;
                string own = "";
                if (m != null && m.isReadable && m.vertexCount > 0)
                {
                    var pts = new List<Vector3>();
                    Vector3[] v = m.vertices;
                    for (int i = 0; i < v.Length; i += Mathf.Max(1, v.Length / 2000))
                    {
                        pts.Add(part.InverseTransformPoint(mf.transform.TransformPoint(v[i])));
                    }
                    Vector3 mean = Vector3.zero;
                    foreach (Vector3 q in pts)
                    {
                        mean += q;
                    }
                    mean /= Mathf.Max(1, pts.Count);
                    own = AxisOf(pts, out Vector3 a, out float e) ? $", own axis {Gun(a):F2} ({e:0.0}x), centre {d.MagSeat.TransformPoint(mean) * 100f:F1} cm" : "";
                }
                sb.AppendLine($"      mesh '{mf.name}' ({(m == null ? "no mesh" : m.isReadable ? m.vertexCount + " vertices" : "unreadable")}): "
                              + $"{(active ? "active" : "INACTIVE")}, renderer {(mr == null ? "none" : mr.enabled ? "on" : "OFF")}, {(counts ? "counts" : "left out")}{own}");
            }
            return sb.ToString();
        }

        private static bool PrincipalAxis(Transform part, bool shownNearOnly, out Vector3 axisLocal, out float elongation)
        {
            axisLocal = Vector3.up;
            elongation = 0f;
            var points = new List<Vector3>();
            foreach (MeshFilter mf in part.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable || m.vertexCount == 0)
                {
                    continue;
                }
                if (shownNearOnly && (!ActiveUpTo(mf.transform, part) || (mf.transform.position - part.position).sqrMagnitude > 1.5f * 1.5f))
                {
                    continue;
                }
                Vector3[] v = m.vertices;
                int step = Mathf.Max(1, v.Length / 4000); // a few thousand points describe the shape well enough
                for (int i = 0; i < v.Length; i += step)
                {
                    points.Add(part.InverseTransformPoint(mf.transform.TransformPoint(v[i])));
                }
            }
            return AxisOf(points, out axisLocal, out elongation);
        }

        /// <summary>The direction a set of points spreads along most, and how much more than across it (ratio of the spreads).</summary>
        private static bool AxisOf(List<Vector3> points, out Vector3 axisLocal, out float elongation)
        {
            axisLocal = Vector3.up;
            elongation = 0f;
            if (points.Count < 8)
            {
                return false;
            }
            Vector3 mean = Vector3.zero;
            foreach (Vector3 p in points)
            {
                mean += p;
            }
            mean /= points.Count;
            // Covariance (symmetric 3x3).
            float xx = 0, xy = 0, xz = 0, yy = 0, yz = 0, zz = 0;
            foreach (Vector3 p in points)
            {
                Vector3 q = p - mean;
                xx += q.x * q.x; xy += q.x * q.y; xz += q.x * q.z;
                yy += q.y * q.y; yz += q.y * q.z; zz += q.z * q.z;
            }
            Vector3 Mul(Vector3 a) => new Vector3(xx * a.x + xy * a.y + xz * a.z, xy * a.x + yy * a.y + yz * a.z, xz * a.x + yz * a.y + zz * a.z);
            // Power iteration from the box's longest side.
            Vector3 v1 = xx >= yy && xx >= zz ? Vector3.right : yy >= zz ? Vector3.up : Vector3.forward;
            for (int i = 0; i < 40; i++)
            {
                Vector3 n = Mul(v1);
                if (n.sqrMagnitude < 1e-20f)
                {
                    return false;
                }
                v1 = n.normalized;
            }
            float l1 = Vector3.Dot(v1, Mul(v1));
            // The largest spread across it: the same on the plane at right angles to the first axis.
            Vector3 v2 = Vector3.Cross(v1, Mathf.Abs(v1.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            for (int i = 0; i < 40; i++)
            {
                Vector3 n = Vector3.ProjectOnPlane(Mul(v2), v1);
                if (n.sqrMagnitude < 1e-20f)
                {
                    break;
                }
                v2 = n.normalized;
            }
            float l2 = Vector3.Dot(v2, Mul(v2));
            if (l1 <= 0f)
            {
                return false;
            }
            axisLocal = v1;
            elongation = l2 > 1e-12f ? Mathf.Sqrt(l1 / l2) : 100f;
            return true;
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
            Bounds mb = sampler.GunSpaceBoundsNear(mag, gunIdle, 0.5f);
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
            d.MagKickable = !handgun && !inGrip && !deepWell && !d.EnBloc; // an en-bloc clip is not knocked out by the next one
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

            // A particle burst is no sound (MatchCues skips them too): one first in line took the mag-out slot and moved every
            // later sound one slot on.
            var reloadCues = new List<SampledCue>(sampler.ReloadCues).FindAll(c => c.Function != "EmitParticles");
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
                else if (d.Action == ActionType.Manual && c.Function != "EmitParticles")
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
                    if (cues[c].Function == "EmitParticles")
                    {
                        continue; // a particle burst is no sound: bound to a magazine or slide sound it left the action silent
                    }
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

        /// <summary>Where a handle that was not found is guessed: above the grip (put onto the receiver once it is measured).</summary>
        internal static Vector3 RackEstimate(WeaponRigData d) =>
            d.GripPoint + d.Up * (d.Handgun ? 0.07f : 0.08f) + d.AimAxis * (d.Handgun ? -0.02f : 0.03f);

        /// <summary>Fills in grab points for parts that were not found so every interaction still works.</summary>
        private static void FinishVirtualParts(WeaponRigData d)
        {
            if (d.SlidePath == null && !d.RackFromHand)
            {
                bool pump = d.Action == ActionType.Manual && d.HasSupportPoint && !d.Handgun;
                d.RackPoint = pump ? d.SupportPoint : RackEstimate(d);
                d.SlideTravel = -d.AimAxis * (d.Handgun ? 0.035f : 0.07f);
                d.RackOnSurface = !pump; // an estimate above the grip: put on top of the receiver once the model is measured
            }
            if (d.MagazinePath == null)
            {
                Vector3 seat = d.Action == ActionType.SingleShot
                    ? d.MuzzlePoint - d.AimAxis * 0.05f
                    : d.GripPoint + d.AimAxis * (d.Handgun ? 0.0f : 0.1f) - d.Up * (d.Handgun ? 0.02f : 0.05f);
                d.MagSeat = new RigidPose(seat, Quaternion.LookRotation(d.AimAxis, d.Up));
                d.MagEjectAxis = d.Action == ActionType.SingleShot ? d.AimAxis : -d.Up;
            }
            d.LockOnEmpty = d.Action == ActionType.SelfLoading && (d.SlidePath != null || d.Handgun);
            // (A Garand classed Manual too: every Manual gun cycles by hand in the analysis, so "Manual && !CyclesByHand" never
            // held; 255.B4-3.)
            if (d.MagazinePath != null && !d.Handgun && EnBlocName.IsMatch(d.Key ?? "") && (d.Action == ActionType.SelfLoading || d.Action == ActionType.Manual))
            {
                // An M1 Garand (the vanilla "M1 GARRET"): its clip goes in from the top, through the open action, and the
                // bolt locks open after the last round (user, 2026-10-16: it did not lock open, a clip could not be put in).
                d.EnBloc = true;
                d.Action = ActionType.SelfLoading;
                d.CyclesByHand = false; // a self-loader: it cycles itself
                d.LockOnEmpty = true;
                d.MagEjectAxis = d.Up;
                d.Notes += "en-bloc clip (from the top, the bolt locks open, the empty clip flies out) ";
            }
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
            HashSet<Transform> armRelated, HashSet<Transform> gunBones, bool launcher, bool beltFed = false)
        {
            foreach (Transform t in all)
            {
                if (IsMagName(t.name, launcher) && !(beltFed && IsBeltPiece(t.name)) && IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                {
                    return t;
                }
            }
            return null;
        }

        /// <summary>Named like a slide but not one: a catch, release, stop, button, selector, safety, carry handle, sling.</summary>
        private static readonly Regex NotSlideName = new Regex(@"release|catch|stop|button|latch|selector|safety|switch|carry ?handle|sling|保险", RegexOptions.IgnoreCase);
        private static readonly Regex StrongSlideName = new Regex(@"slide|bolt|carrier|schlitten|verschluss|culasse|corredera|cerrojo|затвор|枪机|套筒", RegexOptions.IgnoreCase);

        /// <summary>
        /// The slide by its name when no motion found it: never a bolt catch, a bolt release, a magazine-release lever or a
        /// carry handle (the first name in the tree used to be taken, whatever it was; 255.I4-3). A slide, bolt or carrier
        /// name first, then any other slide-like name, and a charging handle's name last: the handle is found as the handle
        /// on its own (NamedChargingHandle), and taken for the slide it took the bolt's place (the PATRIOT's 'Charging
        /// Handle' over its 'Chamber Lid': the bolt never moved and nothing visible did; weapons-1017).
        /// </summary>
        private static Transform FindSlideByName(Transform[] all, Transform root, Transform gunBody, Transform muzzle,
            HashSet<Transform> armRelated, HashSet<Transform> gunBones)
        {
            Transform weak = null, charging = null;
            foreach (Transform t in all)
            {
                if (!SlideName.IsMatch(t.name) || NotSlideName.IsMatch(t.name) || MagName.IsMatch(t.name) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                {
                    continue;
                }
                if (ChargingName.IsMatch(t.name))
                {
                    charging = charging ?? t;
                }
                else if (StrongSlideName.IsMatch(t.name))
                {
                    return t;
                }
                else
                {
                    weak = weak ?? t;
                }
            }
            return weak ?? charging;
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
        /// <summary>
        /// Drawn in first person and named like a moving part (slide, bolt, magazine, trigger): the visible gun's own parts.
        /// A mod pointing thirdPersonTransform at the visible receiver, with the magazine and stock on other branches, had
        /// the receiver, slide and well taken for a hidden third-person copy: no slide, magazine or hinge found.
        /// </summary>
        private static bool HoldsShownGunParts(Transform tp, Transform root)
        {
            foreach (Renderer r in tp.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer || !ShownAtRest(r, root))
                {
                    continue;
                }
                for (Transform t = r.transform; t != null && t != tp.parent; t = t.parent)
                {
                    if (SlideName.IsMatch(t.name) || MagName.IsMatch(t.name) || TriggerName.IsMatch(t.name))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>An arm-named transform with a mesh of its own, not on the arms' skeleton, with no arm-named relative.</summary>
        private static bool LoneArmNamedMesh(Transform t, Transform root, HashSet<Transform> named, HashSet<Transform> armListed)
        {
            if (armListed.Contains(t) || t.GetComponent<MeshFilter>() == null || t.GetComponent<MeshRenderer>() == null)
            {
                return false;
            }
            for (Transform p = t.parent; p != null && p != root; p = p.parent)
            {
                if (named.Contains(p))
                {
                    return false;
                }
            }
            foreach (Transform c in t.GetComponentsInChildren<Transform>(true))
            {
                if (c != t && named.Contains(c))
                {
                    return false;
                }
            }
            return true;
        }

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
        /// <summary>Active itself and every parent up to (not counting) <paramref name="root"/>: works on prefabs outside any scene.</summary>
        internal static bool ActiveUpTo(Transform t, Transform root)
        {
            for (; t != null && t != root; t = t.parent)
            {
                if (!t.gameObject.activeSelf)
                {
                    return false;
                }
            }
            return true;
        }

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
            // Twice at most: first only opaque meshes; if there are none, any but a glow, beam or HUD shader (a gun body on a
            // transparent material was never the host, and a small opaque part or nothing was; review 253.12).
            MeshFilter[] filters = root.GetComponentsInChildren<MeshFilter>(true);
            for (int pass = 0; pass < 2 && best == null; pass++)
            {
                foreach (MeshFilter mf in filters)
                {
                    Renderer mr = mf.GetComponent<Renderer>();
                    if (mf.sharedMesh == null || armRelated.Contains(mf.transform) || mr != null && armRenderers.Contains(mr))
                    {
                        continue;
                    }
                    float size = Vector3.Scale(mf.sharedMesh.bounds.size, mf.transform.lossyScale).magnitude;
                    if (size > 2.5f || mr != null && (pass == 0 ? GunCollider.SeeThrough(mr) : GlowShader(mr)))
                    {
                        // A laser's beam, a sight's glow or glass is not the gun: the HK416-B/D's 10 m laser beam was taken
                        // for the gun body, and every part's motion was measured against the laser sight.
                        continue;
                    }
                    if (size > bestSize)
                    {
                        bestSize = size;
                        best = mf.transform;
                    }
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

        /// <summary>A beam, glow, HUD or particle shader: never a gun's body, whatever else is see-through.</summary>
        private static bool GlowShader(Renderer r)
        {
            Material m = r.sharedMaterial;
            string shader = m != null && m.shader != null ? m.shader.name : "";
            return shader.IndexOf("Additive", StringComparison.OrdinalIgnoreCase) >= 0 || shader.IndexOf("HUD", StringComparison.OrdinalIgnoreCase) >= 0
                   || shader.IndexOf("Particle", StringComparison.OrdinalIgnoreCase) >= 0 || shader.IndexOf("Glow", StringComparison.OrdinalIgnoreCase) >= 0;
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
        /// <remarks>
        /// Measured on the largest mesh (the one the muzzle was put on), not on the gun body: the body is often an empty
        /// holder, and the weapon's own forward came back unchecked (a barrel off that axis was never reported).
        /// </remarks>
        private static Vector3 GeometricAxis(Transform root, Transform host, PoseSampler sampler, RigidPose gunIdle, List<string> warnings)
        {
            Vector3 forwardGun = gunIdle.InverseTransformDirection(sampler.Idle(root).Forward);
            MeshFilter mf = host != null ? host.GetComponent<MeshFilter>() : null;
            if (mf == null || mf.sharedMesh == null)
            {
                warnings.Add("barrel direction: no mesh to measure, the weapon's forward axis is used");
                return forwardGun.normalized;
            }
            Vector3 size = Vector3.Scale(mf.sharedMesh.bounds.size, host.lossyScale);
            Vector3 local = size.x >= size.y && size.x >= size.z ? Vector3.right : size.y >= size.z ? Vector3.up : Vector3.forward;
            Vector3 axis = gunIdle.InverseTransformDirection(sampler.Idle(host).TransformDirection(local)).normalized;
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
                // The side as a word of its own anywhere in the name ('Bip01 R Hand', 'ValveBiped.Bip01_R_Hand', 'Hand_R'),
                // spelt out, or run into the word hand ('RHand', 'HandL'): a 3ds Max biped's hands were missed.
                bool isRight = Regex.IsMatch(n, @"(\br\b|right|\brhand\b|\bhandr\b)");
                bool isLeft = Regex.IsMatch(n, @"(\bl\b|left|\blhand\b|\bhandl\b)");
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
        /// <summary>
        /// Down the grip as the weapon's own hand holds it: from the index finger's knuckle to the little finger's, in the
        /// gun's upright plane, pointing away from the bore. Zero without both fingers or when the line lies along the gun.
        /// </summary>
        private static Vector3 GripDownOf(Transform hand, PoseSampler sampler, RigidPose gunIdle, WeaponRigData d)
        {
            Transform index = null, little = null;
            foreach (Transform c in hand)
            {
                string n = c.name.ToLowerInvariant();
                if (index == null && n.Contains("index"))
                {
                    index = c;
                }
                else if (little == null && (n.Contains("pinky") || n.Contains("pinkie") || n.Contains("little")))
                {
                    little = c;
                }
            }
            if (index == null || little == null)
            {
                return Vector3.zero;
            }
            Vector3 down = gunIdle.InverseTransformDirection(sampler.Idle(little).position - sampler.Idle(index).position);
            Vector3 side = Vector3.Cross(d.Up, d.AimAxis).normalized;
            down = Vector3.ProjectOnPlane(down, side);
            if (down.sqrMagnitude < 1e-6f)
            {
                return Vector3.zero;
            }
            down.Normalize();
            if (Vector3.Dot(down, -d.Up) < 0.5f)
            {
                return Vector3.zero; // more along the gun than down it: not a grip's line
            }
            return down;
        }

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
