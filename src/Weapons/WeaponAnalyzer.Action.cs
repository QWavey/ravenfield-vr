using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // WeaponAnalyzer, part: the slide, bolt or pump and its charging handle. The class and how a weapon is analysed as a whole: WeaponAnalyzer.cs.
    public static partial class WeaponAnalyzer
    {
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
                bool flatParent = false;
                if (parent != null)
                {
                    Vector3 s = parent.lossyScale;
                    // A holder scaled to nothing on an axis (a flattened helper): the division put the slide's rest up to a
                    // million times the travel away (analyzer hunt, 2026-10-21). Its rest is left as sampled then.
                    flatParent = Mathf.Min(Mathf.Abs(s.x), Mathf.Min(Mathf.Abs(s.y), Mathf.Abs(s.z))) < 1e-4f;
                    localDelta = Quaternion.Inverse(sampler.Idle(parent).rotation) * worldDelta;
                    localDelta = new Vector3(localDelta.x / Mathf.Max(Mathf.Abs(s.x), 1e-6f), localDelta.y / Mathf.Max(Mathf.Abs(s.y), 1e-6f), localDelta.z / Mathf.Max(Mathf.Abs(s.z), 1e-6f));
                }
                if (!flatParent)
                {
                    d.SlideRestLocal += localDelta;
                }
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

            // A pump is its own handle: no separate one. The ARS M500's reload moves an 'Ejector' bone at the receiver, which
            // became its handle, 18 cm behind its moving pump ("the part under the hand does not move" in every run). A pump with
            // no mesh of its own (a bone of the skinned gun) is racked where the support hand holds it.
            bool pump = PumpName.IsMatch(slide.name) && d.HasSupportPoint;
            if (pump && !HasRendererInSubtree(slide))
            {
                d.RackPoint = d.SupportPoint;
                d.Notes += $"pump '{slide.name}' (a bone): racked where the support hand holds it ";
            }

            // A separate charging handle the reload works (AR style): that is where the hand grabs, the bolt follows.
            if (d.Animated && sampler.HasReload && !d.Handgun && !pump && !d.SlideMethod.StartsWith("reload motion (charging handle)", StringComparison.Ordinal)) // (not a handgun: its slide is the moving part, never a handle's bolt; pistol hunt)
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
            Transform root, RigidPose gunIdle, HashSet<Renderer> armRenderers)
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
                if (!onPart)
                {
                    // A handle is cut out only where there is gun to cut: the reload hand's palm centre can wait a hand's
                    // width off the handle it holds, and the rack point then hung in the air, 5 to 8 cm from every part, on
                    // every gun this took (AS Val 11 cm out to its side, AK-24 under its receiver by the magazine, ACE 32
                    // beside it, AK-105; sandbox sweep and scouts, 2026-10-17). The stroke is left then: the piece of the
                    // moving part that sticks out (RackOnOutstandingPiece) is the handle.
                    float nearest = float.MaxValue;
                    // (Not the arms; activeInHierarchy is no test here: the analysed prefab is never active in a scene.)
                    foreach (Bounds b in sampler.GunSpaceBoxes(root, gunIdle, t => t.GetComponent<Renderer>() is Renderer r && armRenderers.Contains(r)))
                    {
                        if (b.size.sqrMagnitude > 1e-8f && b.size.magnitude < 2f)
                        {
                            nearest = Mathf.Min(nearest, Mathf.Sqrt(b.SqrDistance(bestStart)));
                        }
                    }
                    if (nearest > 0.025f)
                    {
                        d.Notes += $"the reload hand's rack stroke starts {nearest * 100f:0} cm from every part (in the air): not used ";
                        return;
                    }
                }
                d.RackPoint = bestStart;
                d.RackOnSurface = true;
                d.CarveHandle = !onPart;
                d.SlideMethod += onPart ? " (taken where the reload hand takes it)" : " (inside the receiver: the handle the reload hand takes is cut out and moves)";
                return;
            }
            d.RackFromHand = true;
            d.RackOnSurface = true;
            d.RackPoint = bestStart;
            // A hand keeps moving a little after the bolt stops: capped at a bolt's travel (said when it binds; sweep).
            float boltMax = d.Handgun ? 0.045f : 0.11f;
            d.SlideTravel = -d.AimAxis * Mathf.Clamp(bestLen, d.Handgun ? 0.02f : 0.03f, boltMax);
            if (bestLen > boltMax)
            {
                d.Notes += $"the reload hand's pull of {bestLen * 100f:0.0} cm capped at {boltMax * 100f:0.0} cm of bolt travel ";
            }
            d.SlideMethod = "reload hand motion (bolt is part of the receiver mesh)";
            d.TSlideBack = bestEnd;
            d.Warnings.RemoveAll(w => w.StartsWith("no moving slide/bolt", StringComparison.Ordinal));
        }

        internal static readonly Regex[] FingerPatterns =
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

        /// <summary>Names a belt with no belt name is never taken for (FindBeltParts' shape rule): the gun's own parts by the box.</summary>
        private static readonly Regex UnnamedBeltNot = new Regex(@"tray|feed|cover|lid|release|trigger|stock|grip|mag", RegexOptions.IgnoreCase);

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
            if (found.Count == 0 && gunBody != null)
            {
                // A belt with no belt name: the MG3's '对象010', from the top of the box hanging at its left across the feed tray
                // into the receiver. It is not part of the box, so it stayed on the gun when the box came out, and the mod laid its
                // own belt beside it, at a guessed place (user, 2026-10-19 night: "the old magbelt is still there and doesnt
                // dissapear after opening and the new one places wrongly", task 312). A part touching the box (within 1.5 cm) that
                // runs across the receiver over the bore's line (4 cm or more across, 15 cm at most along the bore), under the
                // cover's top, smaller than the box. Over the export it is the MG3's alone (tools/export_checks/belt_part_rule.py).
                RigidPose gunIdle = sampler.Idle(gunBody);
                Vector3 across = Vector3.Cross(d.Up, d.AimAxis).normalized;
                Vector3 aimAbs = new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z));
                Vector3 acrossAbs = new Vector3(Mathf.Abs(across.x), Mathf.Abs(across.y), Mathf.Abs(across.z));
                Vector3 upAbs = new Vector3(Mathf.Abs(d.Up.x), Mathf.Abs(d.Up.y), Mathf.Abs(d.Up.z));
                Bounds boxBounds = default;
                bool anyBox = false;
                foreach (Transform b in box)
                {
                    Bounds bb = sampler.GunSpaceBounds(b, gunIdle);
                    if (bb.size.sqrMagnitude < 1e-8f)
                    {
                        continue;
                    }
                    if (anyBox)
                    {
                        boxBounds.Encapsulate(bb);
                    }
                    else
                    {
                        boxBounds = bb;
                        anyBox = true;
                    }
                }
                float coverTop = float.PositiveInfinity;
                if (hinge != null)
                {
                    Bounds cb = sampler.GunSpaceBounds(hinge, gunIdle);
                    coverTop = Vector3.Dot(cb.center, d.Up) + Vector3.Dot(cb.extents, upAbs);
                }
                float boreAcross = Vector3.Dot(d.MuzzlePoint, across);
                foreach (Transform t in all)
                {
                    if (!anyBox || !index.ContainsKey(t) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones) || !ShownAtIdle(t, sampler)
                        || UnnamedBeltNot.IsMatch(t.name) || box.Any(b => Related(t, b)) || Related(t, slide) || hinge != null && Related(t, hinge)
                        || t.GetComponent<Renderer>() == null)
                    {
                        continue;
                    }
                    Bounds b = sampler.GunSpaceBoundsWhere(t, gunIdle, x => x != t); // its own mesh only
                    if (b.size.sqrMagnitude < 1e-8f)
                    {
                        continue;
                    }
                    float gap = Mathf.Sqrt(Gap(b, boxBounds));
                    float acrossC = Vector3.Dot(b.center, across), acrossE = Vector3.Dot(b.extents, acrossAbs);
                    bool crosses = acrossC - acrossE <= boreAcross && boreAcross <= acrossC + acrossE;
                    bool under = Vector3.Dot(b.center, d.Up) + Vector3.Dot(b.extents, upAbs) <= coverTop + 0.005f;
                    bool shaped = 2f * acrossE >= 0.04f && Vector3.Dot(b.size, aimAbs) <= 0.15f && b.size.magnitude <= 0.8f * boxBounds.size.magnitude;
                    if (gap <= 0.015f && crosses && under && shaped && !found.Any(f => t.IsChildOf(f)))
                    {
                        found.RemoveAll(f => f.IsChildOf(t));
                        found.Add(t);
                    }
                }
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
        /// <summary>A catch on a cover, named after it: it turns as the cover opens, further, and is no cover.</summary>
        private static readonly Regex CoverCatchName = new Regex(@"latch|catch|clasp|button|(^|[^a-z])lock($|[^a-z])|защ[её]лк|卡扣|锁", RegexOptions.IgnoreCase);
        private static readonly Regex CylinderName = new Regex(@"cylinder|crane|drum|trommel|barillet|tambor|барабан|弹巢|转轮", RegexOptions.IgnoreCase);

        /// <summary>A feed cover is at least this long; a shorter part that turns is rated down in proportion.</summary>
        private const float CoverLength = 0.08f;

        /// <summary>
        /// How a part that turns in the reload rates as the feed cover, the best rating taking it. <paramref name="longest"/> is the
        /// part's longest side, 0 when it has no mesh of its own to measure. The QJB201's 3 cm "Topcover_Latch" (90 degrees, doubled for
        /// its name) beat the 13 cm "Cap" that is its real cover (73 degrees): the hand opened the latch and the cover stayed shut, which
        /// is "some MGS cant be opened" (user, 2026-11-05).
        /// </summary>
        internal static float CoverScore(string name, float turned, float longest)
        {
            if (CoverCatchName.IsMatch(name))
            {
                return 0f;
            }
            float size = longest > 1e-3f ? Mathf.Clamp01(longest / CoverLength) : 1f;
            return turned * (CoverName.IsMatch(name) ? 2f : 1f) * size;
        }

        /// <summary>The part sits next to a catch named after a cover: whatever the modder called it, that is the cover.</summary>
        private static bool BesideCoverCatch(Transform part)
        {
            if (part.parent == null)
            {
                return false;
            }
            foreach (Transform other in part.parent)
            {
                if (other != part && CoverName.IsMatch(other.name) && CoverCatchName.IsMatch(other.name))
                {
                    return true;
                }
            }
            return false;
        }

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
                         && (c.ammo >= 50 && authoredAuto || CoverName.IsMatch(t.name) && !NotFeedCoverName.IsMatch(t.name) || BesideCoverCatch(t)))
                {
                    Vector3 size = sampler.GunSpaceBoundsNear(t, gunIdle, 0.8f).size;
                    float score = CoverScore(t.name, rot, Mathf.Max(size.x, Mathf.Max(size.y, size.z)));
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
        /// The support hand on the magazine as the reload animation holds it (the user, 2026-10-29: "cant we do the real poses on how
        /// the mag gets also held in the animations"): the step where the hand bone is nearest the magazine part while the magazine is
        /// out of its seat (2 cm or more) and shown, within 20 cm, and the palm on the magazine (within 4 cm of its box); the hand
        /// bone's pose in the magazine part's frame then and its fingers' local turns. Null when the reload never brings the hand to
        /// it (a magazine swapped by a second object, no reload): the hand then holds it as the glove does.
        /// The palm on the box, not the hand bone near the part's pivot alone: a step with the hand beside the magazine, reaching
        /// past it or on a second magazine the reload swaps in, was taken as the hold, and the weapon's hand sat off the magazine
        /// in the player's hand, turned or beside it (user, 2026-11-03: "the hand positions on some mags are sometimes bugged").
        /// </summary>
        private static MagHoldPose MagHoldOf(WeaponRigData d, Transform root, Transform hand, Dictionary<Transform, int> index, PoseSampler sampler)
        {
            if (hand == null || d.MagazinePath == null || !sampler.HasReload || sampler.ReloadRotation == null)
            {
                return null;
            }
            Transform mag = WeaponRig.Resolve(root, d.MagazinePath);
            if (mag == null || !index.TryGetValue(mag, out int mi) || !index.TryGetValue(hand, out int hi))
            {
                return null;
            }
            Quaternion[] magRot = sampler.ReloadRotation[mi], handRot = sampler.ReloadRotation[hi];
            Vector3[] magPos = sampler.ReloadTrajectory[mi], handPos = sampler.ReloadTrajectory[hi];
            if (magRot == null || handRot == null)
            {
                return null;
            }
            bool Set(Quaternion q) => q.x != 0f || q.y != 0f || q.z != 0f || q.w != 0f; // (zero before the part first moved)
            Vector3 seat = sampler.RelIdle[mi].position;
            // The palm (hand-bone space, from the arm chain measured just before) against the magazine's box in its own frame.
            Vector3? palmLocal = d.ArmLeft != null ? d.ArmLeft.PalmPoint : (Vector3?)null;
            Bounds box = d.MagBounds;
            int best = -1;
            float bestDistance = 0.2f, bestPalm = -1f;
            for (int s = 0; s < sampler.ReloadSteps; s++)
            {
                if (!sampler.ReloadVisible[mi][s] || (magPos[s] - seat).sqrMagnitude < 0.02f * 0.02f || !Set(magRot[s]) || !Set(handRot[s]))
                {
                    continue;
                }
                float palm = -1f;
                if (palmLocal.HasValue)
                {
                    Vector3 inMag = Quaternion.Inverse(magRot[s]) * (handPos[s] + handRot[s] * palmLocal.Value - magPos[s]);
                    Vector3 outside = Vector3.Max(Abs(inMag - box.center) - box.extents, Vector3.zero);
                    palm = outside.magnitude;
                    if (palm > MagHoldPalmReach)
                    {
                        continue;
                    }
                }
                float distance = Vector3.Distance(handPos[s], magPos[s]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestPalm = palm;
                    best = s;
                }
            }
            if (best < 0)
            {
                if (palmLocal.HasValue)
                {
                    Log.InfoOnce("maghold-none:" + d.Key, $"{d.Key}: its reload never has the support hand's palm on the magazine (within "
                                                         + $"{MagHoldPalmReach * 100f:0} cm of it): the magazine is held as the glove holds it.");
                }
                return null;
            }
            var paths = new List<int[]>();
            var locals = new List<Quaternion>();
            foreach (Transform f in hand.GetComponentsInChildren<Transform>(true))
            {
                if (f == hand || f.parent == null || !index.TryGetValue(f, out int fi) || !index.TryGetValue(f.parent, out int pi))
                {
                    continue;
                }
                Quaternion[] fr = sampler.ReloadRotation[fi], pr = sampler.ReloadRotation[pi];
                if (fr == null || pr == null || !Set(fr[best]) || !Set(pr[best]))
                {
                    continue;
                }
                paths.Add(WeaponRig.PathOf(hand, f));
                locals.Add(RigidPose.Conjugate(pr[best]) * fr[best]);
            }
            var hold = new MagHoldPose
            {
                HandInMag = new RigidPose(magPos[best], magRot[best]).Inverse().Multiply(new RigidPose(handPos[best], handRot[best])),
                FingerPaths = paths.ToArray(),
                FingerLocal = locals.ToArray(),
                Step = best,
                Distance = bestDistance
            };
            Log.InfoOnce("maghold:" + d.Key, $"{d.Key}: the support hand holds the magazine as its reload does at {best * PoseSampler.Dt:0.00} s "
                                            + $"({bestDistance * 100f:0} cm from the magazine part{(bestPalm >= 0f ? $", the palm {bestPalm * 100f:0.0} cm off its box" : "")}, "
                                            + $"{paths.Count} finger bone(s)).");
            return hold;
        }

        /// <summary>How far (m) the palm may be from the magazine's box at the step taken as the reload's hold of it.</summary>
        private const float MagHoldPalmReach = 0.04f;

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
    }
}
