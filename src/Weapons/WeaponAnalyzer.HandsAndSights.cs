using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // WeaponAnalyzer, part: where the arm rig puts the hands, and the sights. The class and how a weapon is analysed as a whole: WeaponAnalyzer.cs.
    public static partial class WeaponAnalyzer
    {
        // ---- hands & sights ---------------------------------------------------------------------------

        private static void AnalyzeHands(WeaponRigData d, Transform handR, Transform handL, PoseSampler sampler, RigidPose gunIdle, Weapon w)
        {
            d.IsPistol = d.Slot == 1;
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
                // The gun's length, not the muzzle transform alone: the RPG-7V [PG-7V] family's muzzle sits 28 cm ahead of its grip
                // inside a tube that runs on past it, so it was cupped as a pistol and held with no steering or steadying (Tasks 266).
                float reach = d.LengthFromGrip;
                if (!d.Handgun && reach > 0.3f && d.Action == ActionType.SingleShot)
                {
                    // A shoulder-fired launcher's second grip can sit behind its trigger grip (the RPG-7's wooden grip): the
                    // animation's left hand is on it, and moved onto the tube in front the support glove got no pose and floated,
                    // the second grip unused (user, headset, 2026-10-18 evening: "the 2nd grip is ignored"; task 276). Kept, and
                    // not cupped as a pistol.
                    d.HasSupportPoint = true;
                    d.Notes += "support hand on its own grip behind the trigger grip (a launcher) ";
                }
                else if (!d.Handgun && reach > 0.3f)
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
                // A laser module's body, dot or beam is not the tube, even when its mount sits by the scope's name.
                bool laser = PoseSampler.LaserModuleName.IsMatch(t.name) || t.parent != null && t.parent != root && PoseSampler.LaserModuleName.IsMatch(t.parent.name);
                if (!named || laser)
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
    }
}
