using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Works out how a weapon prefab is rigged by sampling its animator on an inert clone (scripts, audio,
    /// cameras and lights stripped; animation events off): idle, fire, reload and aim poses tell which part is
    /// the gun body, which part reciprocates (slide/bolt/pump), which part leaves during a reload (magazine,
    /// rocket) and where the hands and sights sit. Names are only tie-breakers. Results are cached per prefab.
    /// </summary>
    public static class WeaponAnalyzer
    {
        private static readonly Dictionary<string, WeaponRigData> Cache = new Dictionary<string, WeaponRigData>();

        private static readonly Regex ArmName = new Regex(
            @"(^|[^a-z])(arm|forearm|upperarm|hand|wrist|finger|thumb|index|middle|ring|pinky|pinkie|little|clavicle|shoulder|elbow|palm|knuckle)",
            RegexOptions.IgnoreCase);
        private static readonly Regex MagName = new Regex(@"(^|[^a-z])(mag|magazine|clip|drum|shell|rocket|round|ammo)", RegexOptions.IgnoreCase);
        private static readonly Regex SlideName = new Regex(@"slide|bolt|charg|handle|carrier|pump|forend|fore-end|lever|breech", RegexOptions.IgnoreCase);
        private static readonly Regex MuzzleName = new Regex(@"muzzle|barrel ?end|flash", RegexOptions.IgnoreCase);
        private static readonly Regex FingerName = new Regex(@"index|middle|ring|pinky|pinkie|little|finger", RegexOptions.IgnoreCase);

        private const float Dt = 1f / 30f;

        public static string KeyOf(Weapon w)
        {
            string prefab = w.name.Replace("(Clone)", "").Trim();
            return w.weaponEntry != null ? w.weaponEntry.name + "#" + prefab : prefab;
        }

        public static WeaponRigData Get(Weapon w)
        {
            string key = KeyOf(w);
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
            if (c.ammo <= 0 || c.spareAmmo == -2 && c.ammo == -1)
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

        private static WeaponRigData Fallback(Weapon w, string key)
        {
            var d = new WeaponRigData { Key = key, Action = ClassifyAction(w), Notes = "fallback" };
            Transform muzzle = FindMuzzle(w);
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

        private static Transform FindMuzzle(Weapon w)
        {
            Transform[] muzzles = w.configuration.muzzles;
            if (muzzles != null)
            {
                foreach (Transform m in muzzles)
                {
                    if (m != null)
                    {
                        return m;
                    }
                }
            }
            foreach (Transform t in w.GetComponentsInChildren<Transform>(true))
            {
                if (MuzzleName.IsMatch(t.name))
                {
                    return t;
                }
            }
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

            // --- arm rig --------------------------------------------------------------------------
            var armBones = new HashSet<Transform>();
            if (w.arms != null && w.arms.bones != null)
            {
                foreach (Transform b in w.arms.bones)
                {
                    if (b != null)
                    {
                        armBones.Add(b);
                    }
                }
            }
            foreach (Transform t in all)
            {
                if (t != root && ArmName.IsMatch(t.name) && !MagName.IsMatch(t.name))
                {
                    armBones.Add(t);
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

            // --- gun body: topmost non-arm transform above the muzzle -----------------------------
            Transform muzzle = FindMuzzle(w);
            Transform gunBody = muzzle;
            for (Transform t = muzzle; t != null && t != root; t = t.parent)
            {
                if (armRelated.Contains(t))
                {
                    break;
                }
                gunBody = t;
            }
            d.GunBodyPath = WeaponRig.PathOf(root, gunBody) ?? new int[0];
            d.MuzzlePath = WeaponRig.PathOf(root, muzzle);

            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                bool hide = r == w.arms;
                if (!hide && r is SkinnedMeshRenderer smr && smr.bones != null)
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
                    hide = total > 0 && armCount * 2 > total;
                }
                if (!hide && !(r is SkinnedMeshRenderer) && armBones.Contains(r.transform) && !gunBody.IsChildOf(r.transform))
                {
                    hide = true;
                }
                if (hide)
                {
                    d.ArmRendererPaths.Add(WeaponRig.PathOf(root, r.transform));
                }
            }

            Transform handR = FindHand(armBones, right: true);
            Transform handL = FindHand(armBones, right: false);

            // --- sample the animator on an inert clone ----------------------------------------------
            Animator liveAnimator = w.animator != null ? w.animator : root.GetComponent<Animator>();
            var sampler = new PoseSampler(w, all, index, gunBody);
            bool sampled = liveAnimator != null && liveAnimator.runtimeAnimatorController != null && sampler.Run(d.Action, w.configuration);
            d.Animated = sampled;
            if (!sampled)
            {
                sampler.UseLivePosesAsIdle();
                d.Notes += "static ";
            }

            RigidPose gunIdle = sampler.Idle(gunBody);
            RigidPose muzzleIdle = sampler.Idle(muzzle);
            d.AimAxis = gunIdle.InverseTransformDirection(muzzleIdle.Forward).normalized;
            if (d.AimAxis.sqrMagnitude < 0.5f)
            {
                d.AimAxis = Vector3.forward;
            }
            Vector3 up = gunIdle.InverseTransformDirection(sampler.Idle(root).Up);
            up = Vector3.ProjectOnPlane(up, d.AimAxis);
            d.Up = up.sqrMagnitude > 1e-4f ? up.normalized : Vector3.ProjectOnPlane(Vector3.up, d.AimAxis).normalized;
            d.MuzzlePoint = gunIdle.InverseTransformPoint(muzzleIdle.position);

            // --- hands ------------------------------------------------------------------------------
            d.IsPistol = w.slot == 1;
            if (handR != null)
            {
                d.GripPoint = gunIdle.InverseTransformPoint(PalmOf(handR, sampler));
            }
            else
            {
                d.GripPoint = d.MuzzlePoint - d.AimAxis * (d.IsPistol ? 0.13f : 0.36f) - d.Up * 0.05f;
                d.Notes += "no-hand-bones ";
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
            float supportAlong = Vector3.Dot(d.SupportPoint - d.GripPoint, d.AimAxis);
            if (supportAlong < 0.07f)
            {
                d.IsPistol = true; // support hand cups the grip: no foregrip steering
            }

            // --- sights (ADS eye point) -------------------------------------------------------------
            if (sampled && sampler.HasAim && TryEyeInRoot(w, out Vector3 eyeInRoot))
            {
                RigidPose gunAim = sampler.AimGunBody;
                Vector3 eye = gunAim.InverseTransformPoint(eyeInRoot);
                Vector3 dir = gunAim.InverseTransformDirection(Vector3.forward).normalized;
                float behind = Vector3.Dot(eye - d.MuzzlePoint, d.AimAxis);
                float lateral = Vector3.ProjectOnPlane(eye - d.MuzzlePoint, d.AimAxis).magnitude;
                if (behind < -0.12f && behind > -1.5f && lateral < 0.2f && Vector3.Dot(dir, d.AimAxis) > 0.9f)
                {
                    d.EyePoint = eye;
                    d.EyeDirection = dir;
                    d.HasEyePoint = true;
                }
            }

            // --- reciprocating part (slide / bolt / pump) ------------------------------------------
            Transform slide = null;
            Vector3 slideVec = Vector3.zero;
            if (sampled)
            {
                float best = 0f;
                foreach (Transform t in all)
                {
                    if (!IsPartCandidate(t, root, gunBody, muzzle, armRelated))
                    {
                        continue;
                    }
                    int i = index[t];
                    float m = sampler.FireMax[i];
                    if (m < 0.004f)
                    {
                        continue;
                    }
                    Vector3 v = sampler.FireVec[i];
                    float along = Mathf.Abs(Vector3.Dot(v.normalized, d.AimAxis));
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
                    // Climb to the topmost ancestor that moves the same way, so the whole slide group moves.
                    float m0 = sampler.FireMax[index[slide]];
                    for (Transform p = slide.parent; p != null && p != gunBody && p != root; p = p.parent)
                    {
                        if (!index.TryGetValue(p, out int pi) || armRelated.Contains(p) || sampler.FireMax[pi] < m0 * 0.8f)
                        {
                            break;
                        }
                        slide = p;
                    }
                }
            }
            if (slide == null)
            {
                slide = FindByName(all, SlideName, root, gunBody, muzzle, armRelated);
            }
            if (slide != null)
            {
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
            }

            // --- magazine (or rocket / shell) -------------------------------------------------------
            Transform mag = null;
            if (sampled && sampler.HasReload)
            {
                float best = 0f;
                foreach (Transform t in all)
                {
                    if (!IsPartCandidate(t, root, gunBody, muzzle, armRelated) || (slide != null && (t == slide || t.IsChildOf(slide) || slide.IsChildOf(t))))
                    {
                        continue;
                    }
                    int i = index[t];
                    float m = sampler.ReloadMax[i];
                    if (m < 0.03f)
                    {
                        continue;
                    }
                    Vector3 idlePos = sampler.RelIdle[i].position;
                    bool nearGun = Vector3.Distance(idlePos, d.GripPoint) < 0.4f;
                    bool visible = sampler.IdleVisible(t);
                    float score = Mathf.Min(m, 0.4f) * (MagName.IsMatch(t.name) ? 3f : 1f) * (nearGun ? 1f : 0.2f) * (visible ? 1f : 0.3f);
                    if (score > best)
                    {
                        best = score;
                        mag = t;
                    }
                }
                if (mag != null)
                {
                    for (Transform p = mag.parent; p != null && p != gunBody && p != root; p = p.parent)
                    {
                        if (!index.TryGetValue(p, out int pi) || armRelated.Contains(p) || (slide != null && p == slide) || sampler.ReloadMax[pi] < sampler.ReloadMax[index[mag]] * 0.8f)
                        {
                            break;
                        }
                        mag = p;
                    }
                }
            }
            if (mag == null)
            {
                mag = FindByName(all, MagName, root, gunBody, muzzle, armRelated);
            }
            if (mag != null)
            {
                d.MagazinePath = WeaponRig.PathOf(root, mag);
                int mi = index[mag];
                d.MagSeat = sampler.RelIdle[mi];
                Vector3 eject = sampled && sampler.HasReload ? sampler.FirstDir[mi] : Vector3.zero;
                d.MagEjectAxis = eject.sqrMagnitude > 0.5f ? eject.normalized : -d.Up;
                Bounds mb = sampler.LocalBounds(mag);
                if (mb.size.sqrMagnitude > 1e-6f)
                {
                    d.MagBounds = mb;
                }
            }

            FinishVirtualParts(d);
            sampler.Dispose();
            return d;
        }

        /// <summary>Fills in grab points for parts that were not found so every interaction still works.</summary>
        private static void FinishVirtualParts(WeaponRigData d)
        {
            if (d.SlidePath == null)
            {
                d.RackPoint = d.Action == ActionType.Manual && d.HasSupportPoint && !d.IsPistol
                    ? d.SupportPoint
                    : d.GripPoint + d.Up * (d.IsPistol ? 0.07f : 0.08f) + d.AimAxis * (d.IsPistol ? -0.02f : 0.03f);
                d.SlideTravel = -d.AimAxis * (d.IsPistol ? 0.035f : 0.07f);
            }
            if (d.MagazinePath == null)
            {
                Vector3 seat = d.Action == ActionType.SingleShot
                    ? d.MuzzlePoint - d.AimAxis * 0.05f
                    : d.GripPoint + d.AimAxis * (d.IsPistol ? 0.0f : 0.1f) - d.Up * (d.IsPistol ? 0.02f : 0.05f);
                d.MagSeat = new RigidPose(seat, Quaternion.LookRotation(d.AimAxis, d.Up));
                d.MagEjectAxis = d.Action == ActionType.SingleShot ? d.AimAxis : -d.Up;
            }
            d.LockOnEmpty = d.Action == ActionType.SelfLoading && d.SlidePath != null || d.Action == ActionType.SelfLoading && d.IsPistol;
        }

        private static bool IsPartCandidate(Transform t, Transform root, Transform gunBody, Transform muzzle, HashSet<Transform> armRelated)
        {
            if (t == root || t == gunBody || armRelated.Contains(t) || gunBody.IsChildOf(t))
            {
                return false;
            }
            if (muzzle != null && muzzle != gunBody && (t == muzzle || t.IsChildOf(muzzle)))
            {
                return false;
            }
            if (t.GetComponent<ParticleSystem>() != null || t.GetComponent<Camera>() != null || t.GetComponent<Light>() != null)
            {
                return false;
            }
            return t.GetComponentInChildren<MeshRenderer>(true) != null || t.GetComponentInChildren<SkinnedMeshRenderer>(true) != null;
        }

        private static Transform FindByName(Transform[] all, Regex rx, Transform root, Transform gunBody, Transform muzzle, HashSet<Transform> armRelated)
        {
            foreach (Transform t in all)
            {
                if (rx.IsMatch(t.name) && IsPartCandidate(t, root, gunBody, muzzle, armRelated))
                {
                    return t;
                }
            }
            return null;
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

        /// <summary>
        /// Where the game camera sits in the weapon root's space at rest: weapon roots hang under
        /// shoulderParent/weaponParent below the camera parent; use their rest offsets (not the sway springs).
        /// </summary>
        private static bool TryEyeInRoot(Weapon w, out Vector3 eye)
        {
            eye = Vector3.zero;
            FpsActorController player = LocalPlayer.controller;
            if (player == null || player.fpParent == null)
            {
                return false;
            }
            PlayerFpParent fp = player.fpParent;
            Transform camParent = fp.fpCameraParent;
            Transform weaponParent = w.transform.parent;
            if (camParent == null || weaponParent == null || !weaponParent.IsChildOf(camParent))
            {
                return false;
            }
            RigidPose chain = RigidPose.Identity;
            var path = new List<Transform>();
            for (Transform t = weaponParent; t != null && t != camParent; t = t.parent)
            {
                path.Add(t);
            }
            path.Reverse();
            foreach (Transform t in path)
            {
                RigidPose local;
                if (t == fp.shoulderParent)
                {
                    local = new RigidPose(fp.shoulderParentOrigin, Quaternion.identity);
                }
                else if (t == fp.weaponParent)
                {
                    local = new RigidPose(fp.weaponParentOrigin, Quaternion.identity);
                }
                else
                {
                    local = RigidPose.LocalOf(t);
                }
                chain = chain.Multiply(local);
            }
            // Camera rests at the camera parent's origin; express it in weapon root space (root local = identity).
            eye = chain.InverseTransformPoint(Vector3.zero);
            return true;
        }
    }
}
