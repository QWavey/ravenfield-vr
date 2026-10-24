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
    public static partial class WeaponAnalyzer
    {
        private static readonly Dictionary<string, WeaponRigData> Cache = new Dictionary<string, WeaponRigData>();
        /// <summary>Keys whose analysis could not be set up (the geometric fallback is kept): analysed again at the next scene.</summary>
        private static readonly HashSet<string> Failed = new HashSet<string>();

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
        private static readonly Regex LauncherRoundName = new Regex(@"rocket|missile|warhead|projectile|ракета|导弹|火箭|战斗部|弹药|弹头|炮弹", RegexOptions.IgnoreCase); // (弹药, 弹头, 炮弹: ammunition, warhead, shell; the Panzerfaust 3's warhead is 弹药)
        /// <summary>
        /// A launcher's parts that its tube swap moves and are never its rocket: a safety, a cap or lid, a sling, a carry handle
        /// (保险, 桶盖, 提把; the AT-1K's controller unit with its safety was taken for its rocket). Launchers only: on any other
        /// gun a "Mag_Cap" lost its magazine name (review 253.7).
        /// </summary>
        private static readonly Regex LauncherNotRound = new Regex(
            @"safety|(^|[^a-z])(end ?)?caps?($|[^a-z])|(^|[^a-z])lid($|[^a-z])|sling|carry ?handle|保险|桶盖|提把"
            // Nor its stock (枪托): the Panzerfaust 3's was taken for its rocket, and a stock came out of the pouch to load (2026-10-20).
            + @"|(^|[^a-z])(stock|butt)($|[^a-z])|枪托", RegexOptions.IgnoreCase);
        /// <summary>
        /// Parts named after the magazine that are not the magazine: its release button or catch, the well it sits in, a
        /// pouch or a spare. Taking one of them for the magazine leaves the real one in the gun when it is ejected.
        /// </summary>
        private static readonly Regex NotMagName = new Regex(
            @"release|catch|button|latch|controll?er|控制器|(^|[^a-z])well|magwell|housing|lever|pouch|holder|spare|switch|bolt ?stop|stopper|indicator|弹匣释放|卡笋"
            // A laser or light module is no magazine either: it is never named as one, so it must not be scored as one by size.
            + @"|laser|镭射|激光|flash ?light|torch"
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
            // The weapon's slot (primary, secondary...): a prefab's Weapon.slot is -1 until the game equips it, so the menu's
            // warm-up (and the sandbox export) analysed every pistol as "not a handgun" and the match used that cached result;
            // the entry knows it.
            int slot = w.slot >= 0 ? w.slot : entry != null ? (int)entry.slot : -1;
            WeaponRigData data;
            try
            {
                data = Analyze(w, key, slot);
            }
            catch (Exception e)
            {
                Log.Exception($"Weapon analysis failed for {key}; using geometric fallback", e);
                data = Fallback(w, key, slot);
                data.Warnings.Add("analysis threw " + e.GetType().Name + ": " + e.Message);
                Failed.Add(key);
            }
            WeaponOverrides.Apply(w, data);
            if (data.ExportGlb != null)
            {
                SandboxExport.Write(data, w.transform, w, data.ExportGlb, data.ExportNote);
                data.ExportGlb = null;
            }
            MagLineTable.Apply(data); // after the export: the offline scans measure the analysed line
            SandboxPoses.Apply(data); // hands saved in the sandbox (tools/sandbox) for this weapon
            Cache[key] = data;
            if (!Failed.Contains(key))
            {
                ParkedPartsCache.Put(key, w, data.SparePartPaths); // a prefab's: its first drop next session needs no analysis
            }
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

        public static void ClearCache()
        {
            Cache.Clear();
            Failed.Clear();
        }

        /// <summary>The analysis under this key failed and is the fallback (analysed again after <see cref="ForgetFailed"/>).</summary>
        internal static bool AnalysisFailed(string key) => key != null && Failed.Contains(key);

        /// <summary>
        /// Drops the analyses whose setup threw, so that the next scene analyses them again: the fallback was kept for the whole
        /// session before, with no retry (2026-10-19). Called at each scene change (WeaponSystem.OnSceneChanged).
        /// </summary>
        public static void ForgetFailed()
        {
            foreach (string key in Failed)
            {
                Cache.Remove(key);
            }
            Failed.Clear();
        }

        /// <summary>
        /// One rule of the analysis: a rule that throws leaves its own result out, and the rules after it still run. The rules
        /// used to share one try, so a single throw replaced the whole rig with the geometric fallback (2026-10-19).
        /// </summary>
        private static void Guarded(WeaponRigData d, string rule, Action step)
        {
            try
            {
                step();
            }
            catch (Exception e)
            {
                Log.Exception($"Weapon rule {rule} failed for {d.Key}; the rest of its analysis stands", e);
                d.Warnings.Add(rule + " threw " + e.GetType().Name + ": " + e.Message);
            }
        }

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
            d.Action = !AuthoredAuto(w) && c.cooldown >= 0.45f ? ActionType.Manual : ActionType.SelfLoading;
            d.Notes += "magazine-fed advanced reload ";
        }

        private static WeaponRigData Fallback(Weapon w, string key, int slot)
        {
            var d = new WeaponRigData { Key = key, Action = ClassifyAction(w), Notes = "fallback", Slot = slot };
            // AuthoredAuto as the full analysis (a gun the player switched to semi is still an automatic one; scout, 2026-10-24).
            d.CyclesByHand = d.Action == ActionType.Manual || d.Action == ActionType.ShellFed && !AuthoredAuto(w) && w.configuration.cooldown >= 0.45f;
            Transform muzzle = FindMuzzle(w, null, out _);
            Transform root = w.transform;
            d.GunBodyPath = WeaponRig.PathOf(root, muzzle) ?? new int[0];
            d.MuzzlePath = d.GunBodyPath;
            d.AimAxis = Vector3.forward;
            d.Up = Vector3.up;
            d.IsPistol = d.Slot == 1;
            d.Handgun = d.IsPistol;
            d.GripPoint = new Vector3(0f, -0.04f, d.IsPistol ? -0.12f : -0.32f);
            d.SupportPoint = d.GripPoint + new Vector3(0f, 0f, d.IsPistol ? 0.01f : 0.22f);
            // As AnalyzeHands estimates a gun with no left-hand bone: a pistol's cup is only a guess, so its support glove settles
            // onto it (ReloadInteractions' cup anchor); set for every pistol here, the fallback's support glove followed the real
            // hand freely (bug hunt 2026-10-19, task 288).
            d.HasSupportPoint = !d.IsPistol;
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

        private static WeaponRigData Analyze(Weapon w, string key, int slot)
        {
            Transform root = w.transform;
            var d = new WeaponRigData { Key = key, Action = ClassifyAction(w), Slot = slot };

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
                d.Up = up.sqrMagnitude > 1e-4f ? up.normalized : UpAcrossBore(d.AimAxis, root, sampler, gunIdle, d);
                d.Up = UprightByModel(d.Up, d.AimAxis, d);
                d.MuzzlePoint = gunIdle.InverseTransformPoint(muzzleIdle.position);
                // Guarded as the rules below are: a throw here replaced every result after it with the fallback (analyzer hunt,
                // 2026-10-21).
                Guarded(d, "muzzle onto barrel", () => PullMuzzleOntoBarrel(d, root, armRelated, sampler, gunIdle));
                float rootAngle = Vector3.Angle(gunIdle.TransformDirection(d.AimAxis), sampler.Idle(root).Forward);
                if (rootAngle > 25f)
                {
                    d.Warnings.Add($"barrel points {rootAngle:0}° away from the weapon's forward axis (check muzzle orientation)");
                }

                // Each rule is guarded on its own (2026-10-19): a rule that throws leaves its result out, and the rules after it
                // still run. The rules used to share this try, so one throw replaced the whole rig with the fallback.
                Guarded(d, "hands", () => AnalyzeHands(d, handR, handL, sampler, gunIdle, w));
                Guarded(d, "sights", () => AnalyzeSights(d, sampler));
                Guarded(d, "eye", () => AlignEyeToScopeModel(d, root, armRelated, sampler, w, gunIdle));
                Guarded(d, "sight modes", () => AnalyzeSightModes(d, sampler, w));
                Guarded(d, "sight parts", () => AnalyzeSightParts(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler));
                Transform slide = null;
                Guarded(d, "slide", () => slide = AnalyzeSlide(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle));
                Guarded(d, "magazine", () => AnalyzeMagazine(d, all, index, root, gunBody, muzzle, slide, armRelated, gunBones, sampler, w));
                Guarded(d, "magazine axis", () => KeepMagazineAxisInSidePlane(d));
                Guarded(d, "magazine protrusion", () => MeasureMagazineProtrusion(d, root, gunBody, armRelated, armRenderers, gunBones, sampler));
                Guarded(d, "advanced reload", () => ReclassifyAdvancedReload(d, w));
                Guarded(d, "hinge", () => DetectHinge(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, w, gunIdle));
                Guarded(d, "shells", () => AnalyzeShells(d, all, index, root, armRelated, sampler, gunIdle, w));
                Guarded(d, "belt", () => FindBeltParts(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler));
                d.CyclesByHand = d.Action == ActionType.Manual
                                 || d.Action == ActionType.ShellFed && !AuthoredAuto(w) && w.configuration != null && w.configuration.cooldown >= 0.45f;
                if (d.Animated && sampler.HasReload && (d.Action == ActionType.SelfLoading || d.Action == ActionType.Manual))
                {
                    Guarded(d, "rack stroke", () => RackStrokeFromHands(d, handR, handL, sampler, index, root, gunIdle, armRenderers));
                }
                Guarded(d, "charging handle", () => NamedChargingHandle(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle));
                Guarded(d, "static handle", () => ArStaticHandle(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle));
                Guarded(d, "own handle", () => ArOwnHandleNoSlide(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle));
                Guarded(d, "bolt carrier", () => ArBoltCarrier(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle));
                Guarded(d, "side knob", () => SideKnobByName(d, all, root, sampler, gunIdle));
                Guarded(d, "outstanding piece", () => RackOnOutstandingPiece(d, root, sampler, gunIdle));
                Guarded(d, "slide companions", () => FindSlideCompanions(d, all, index, root, gunBody, sampler, gunIdle));
                Guarded(d, "bolt beside handle", () => BoltBesideHandle(d, root, sampler, gunIdle));
                Guarded(d, "reload rounds", () => CollectReloadRounds(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle)); // after every part is chosen
                Guarded(d, "parked ammo", () => ParkedAmmo(d, w, all, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle));
                // Last: only where the hands go. Run right after the hands, the moved grip point changed the rules that measure
                // from it, and the Panzerfaust 3's rocket became its grip (it did not fire; check-1018d).
                Guarded(d, "named grips", () => HandsOnNamedGrips(d, all, armRelated, sampler, gunIdle));
                Guarded(d, "finger curls", () =>
                {
                    d.GripCurls = FingerCurls(handR, sampler);
                    d.SupportCurls = FingerCurls(handL, sampler);
                });
                Guarded(d, "arm chains", () =>
                {
                    d.ArmRight = ArmChainOf(root, handR, handL, armBones, sampler, gunIdle, d);
                    d.ArmLeft = ArmChainOf(root, handL, handR, armBones, sampler, gunIdle, d);
                });
                Guarded(d, "sounds", () => BuildSoundMap(d, sampler, w));
                if (WeaponSelfTest.WantsRigDump(key))
                {
                    Guarded(d, "rig dump", () => rigDump = DumpRig(d, all, index, root, gunBody, muzzle, armRelated, gunBones, sampler, gunIdle));
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

            Guarded(d, "virtual parts", () => FinishVirtualParts(d));
            Guarded(d, "magazine kick", () => MagKickRule(d, w)); // after the virtual magazine's seat is set: before, a virtual magazine was judged at no seat
            Guarded(d, "charging handle (AR)", () => ArChargingHandle(d, root, gunBody, armRenderers));
            Guarded(d, "bolt inside", () => BoltInsideOnSurface(d));
            Guarded(d, "side handle", () => ForwardSideHandle(d));
            Guarded(d, "sanity", () => CheckSanity(d, root, gunBody, armRelated));
            // Written in Get, after the weapon's overrides (config gripForward, pitch, slide...): the sandbox holds the gun
            // where the game does (written here, an overridden gun's sandbox grip was the analysis' and a saved pose undid
            // the override; sandbox review).
            d.ExportGlb = exportGlb;
            d.ExportNote = exportNote;
            if (rigDump != null)
            {
                // Written once every rule has run: with what they chose (the slide, the handle, the rack point, the magazine).
                WeaponSelfTest.AppendRigDump(rigDump + DumpChoices(d, root));
            }
            return d;
        }

        /// <summary>A shotgun's pump (the R870s', the 76 EAGLE's, the ARS M500's bone): the only exported parts so named.</summary>
        private static readonly Regex PumpName = new Regex(@"pump|fore-?end", RegexOptions.IgnoreCase);
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
                // On the knob itself: the band's point nearest its mean (the mean of a curved or hollow knob lay in the air
                // beside it, CZ Bren 2 BR Raid; scouts, 2026-10-17).
                Vector3 mean = sum / n;
                Vector3 onKnob = mean;
                float nearest = float.MaxValue;
                for (int i = 0; i < pts.Length; i++)
                {
                    float d2 = (pts[i] - mean).sqrMagnitude;
                    if (Vector3.Dot(pts[i], left) > most - 0.012f && d2 < nearest)
                    {
                        nearest = d2;
                        onKnob = pts[i];
                    }
                }
                d.SideKnob = onKnob;
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
                // On the knob's own tip, not snapped onto the receiver: BoltInsideOnSurface (run before) had asked for the snap,
                // which moved the point 3.2-3.6 cm inboard, off the knob the carve cuts out (Bren hunt, 2026-10-19, task 302).
                d.RackOnSurface = false;
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
        private static void ArChargingHandle(WeaponRigData d, Transform root, Transform gunBody, HashSet<Renderer> armRenderers)
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
            if (UpperReceiverRear(d, root, gunBody, armRenderers, out float rear))
            {
                // The handle's T is the upper receiver's back end: measured there. 5 cm behind the grip point was on the
                // buffer tube behind the receiver of the M417A2 and Suzuya's AR-15s (their grip point low on a slanted grip),
                // 8 to 10 cm behind their handle, and the hand closed on air (game pictures and sandbox, 2026-10-18).
                d.RackPoint = d.MuzzlePoint + d.AimAxis * (rear + 0.012f) + d.Up * 0.03f;
                d.Notes += $"AR charging handle at the upper receiver's back end, {(Vector3.Dot(d.GripPoint - d.MuzzlePoint, d.AimAxis) - rear) * 100f:0.0} cm behind the grip point ";
            }
            d.RackOnSurface = true; // onto the top of the receiver once the model is measured
            d.CarveHandle = true;
            if (d.SlidePath == null)
            {
                d.RackFromHand = true;
                d.SlideTravel = -d.AimAxis * 0.075f;
            }
            d.SlideMethod += " (AR-pattern: the charging handle at the back of the receiver)";
        }

        private static readonly Regex FrontGripName = new Regex(@"前握|fore_?grip|front_?grip|vert(ical)?_?grip|(^|[^a-z])vfg", RegexOptions.IgnoreCase);
        private static readonly Regex PistolGripName = new Regex(@"握把|pistol_?grip", RegexOptions.IgnoreCase);

        /// <summary>
        /// The hands onto the grips the model names, when the animation's palm waits off them: the Panzerfaust 3's hands
        /// hung 6 to 8 cm under its 铁拳3 握把 and 铁拳3 前握把 (game pictures, 2026-10-18). A pistol grip (握把, pistol grip,
        /// grip) takes the grip point, a front grip (前握把, foregrip, vertical grip) the support point, when the palm is
        /// more than 4 cm from that part: on its back face (the shooter's side) at its middle, 1.2 cm off it, where a palm
        /// closes round it. A palm already on its grip, as on most guns, is left as it is. Launchers only, and only grips named
        /// as such: for every gun and any "grip", the MK46's right hand went onto its vertical foregrip ("GRIP", 30 cm from
        /// its pistol grip) and the MK18 RIS III's onto its Grip-N-Rip charging handle (export check, 2026-10-18).
        /// </summary>
        private static void HandsOnNamedGrips(WeaponRigData d, Transform[] all, HashSet<Transform> armRelated, PoseSampler sampler, RigidPose gunIdle)
        {
            if (d.Action != ActionType.SingleShot)
            {
                return;
            }
            bool Find(bool front, out Bounds box, out string name)
            {
                box = default;
                name = null;
                float best = float.MaxValue;
                foreach (Transform t in all)
                {
                    bool isFront = FrontGripName.IsMatch(t.name);
                    if (front != isFront || !front && !PistolGripName.IsMatch(t.name) || armRelated.Contains(t)
                        || Regex.IsMatch(t.name, @"charg|handle|bolt|knob|tape", RegexOptions.IgnoreCase)
                        || t.GetComponent<Renderer>() == null || !ShownAtIdle(t, sampler))
                    {
                        continue;
                    }
                    Bounds b = sampler.GunSpaceBoundsWhere(t, gunIdle, x => x != t);
                    float size = b.size.magnitude;
                    if (size < 0.03f || size > 0.3f)
                    {
                        continue; // a grip a hand holds (not a tape decal or the whole stock)
                    }
                    float dist = b.SqrDistance(front ? d.SupportPoint : d.GripPoint);
                    if (dist < best)
                    {
                        best = dist;
                        box = b;
                        name = t.name;
                    }
                }
                return name != null;
            }
            Vector3 OnBack(Bounds b)
            {
                Vector3 a = new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z));
                return b.center - d.AimAxis * (Vector3.Dot(b.extents, a) + 0.012f);
            }
            if (Find(false, out Bounds grip, out string gripName))
            {
                float off = Mathf.Sqrt(grip.SqrDistance(d.GripPoint));
                if (off > 0.04f)
                {
                    d.GripPoint = OnBack(grip);
                    d.Notes += $"grip point onto '{gripName}' (the animation's palm was {off * 100f:0} cm off it) ";
                }
            }
            if (d.HasSupportPoint && Find(true, out Bounds fore, out string foreName))
            {
                float off = Mathf.Sqrt(fore.SqrDistance(d.SupportPoint));
                if (off > 0.04f)
                {
                    d.SupportPoint = OnBack(fore);
                    d.Notes += $"support point onto '{foreName}' (the animation's palm was {off * 100f:0} cm off it) ";
                }
            }
        }

        /// <summary>
        /// Where an AR's upper receiver ends at the back, along the bore from the muzzle (negative: behind it): from over the
        /// receiver 5 cm ahead of the grip point, back along the gun's top (2.5 to 7 cm above the bore, within 2.5 cm of its
        /// centre line) for as long as it goes on without a gap over 1.5 cm. Behind it are the buffer tube, lower than that, and
        /// the stock past the gap. The parts as they stand (the sampler is gone by now), in the gun body's frame; meshes that
        /// cannot be read leave it unmeasured.
        /// </summary>
        private static bool UpperReceiverRear(WeaponRigData d, Transform root, Transform gunBody, HashSet<Renderer> armRenderers, out float rear)
        {
            rear = 0f;
            if (gunBody == null || root == null)
            {
                return false;
            }
            RigidPose toGun = new RigidPose(gunBody.position, gunBody.rotation).Inverse();
            Vector3 right = Vector3.Cross(d.Up, d.AimAxis).normalized;
            float start = Vector3.Dot(d.GripPoint - d.MuzzlePoint, d.AimAxis) + 0.05f;
            var along = new List<float>();
            foreach (MeshFilter mf in root.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                MeshRenderer mr = mf.GetComponent<MeshRenderer>();
                if (m == null || !m.isReadable || m.vertexCount == 0 || mr == null || armRenderers.Contains(mr) || OpticName.IsMatch(mf.name))
                {
                    continue;
                }
                Vector3[] v = m.vertices;
                int step = Mathf.Max(1, v.Length / 20000);
                for (int i = 0; i < v.Length; i += step)
                {
                    Vector3 c = toGun.TransformPoint(mf.transform.TransformPoint(v[i])) - d.MuzzlePoint;
                    float a = Vector3.Dot(c, d.AimAxis);
                    float up = Vector3.Dot(c, d.Up);
                    if (a <= start + 0.01f && a > start - 0.3f && up >= 0.025f && up <= 0.07f && Mathf.Abs(Vector3.Dot(c, right)) <= 0.025f)
                    {
                        along.Add(a);
                    }
                }
            }
            if (along.Count < 8)
            {
                return false;
            }
            along.Sort();
            along.Reverse(); // front to back
            if (along[0] < start - 0.015f)
            {
                return false; // nothing on the receiver where the walk starts: not an AR's upper as expected
            }
            float at = along[0];
            foreach (float a in along)
            {
                if (at - a > 0.015f)
                {
                    break;
                }
                at = Mathf.Min(at, a);
            }
            rear = at;
            return true;
        }
    }
}
