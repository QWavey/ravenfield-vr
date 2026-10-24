using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // WeaponAnalyzer, part: the magazine, its parts, its well and the ammunition parked in the gun. The class and how a weapon is analysed as a whole: WeaponAnalyzer.cs.
    public static partial class WeaponAnalyzer
    {
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
                                                        && !Regex.IsMatch(name, @"box|can|弹箱", RegexOptions.IgnoreCase) // a "belt box" is the box (255.I3-7)
                                                        && !(LinkNotBelt.IsMatch(name) && !BeltWord.IsMatch(name));

        /// <summary>A "link" or "chain" named as a gun part (a trigger or bolt link, a sling chain) is not a belt (review, 2026-10-20).</summary>
        private static readonly Regex LinkNotBelt = new Regex(@"trigger|bolt|hammer|sear|safety|slide|charg|handle|grip|stock|sling|lever", RegexOptions.IgnoreCase);
        private static readonly Regex BeltWord = new Regex(@"belt|lenta|лента|弹链|ammo", RegexOptions.IgnoreCase);

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
                Transform firstByName = mag;
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
                else if (firstByName != null)
                {
                    // Measured before it is changed (unswept-areas hunt, 2026-10-24): the first name match was dropped and the
                    // next one is never tried; named here so a run shows which guns would get a magazine that way.
                    Transform next = NextMagByName(all, root, gunBody, muzzle, armRelated, gunBones, d.Action == ActionType.SingleShot, beltFed, firstByName, slide, sampler);
                    if (next != null)
                    {
                        d.Notes += $"(the name search dropped '{firstByName.name}'; '{next.name}' is named like a magazine too and would pass) ";
                    }
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
        /// How near a handgun's grip a spare magazine may wait: its reload's other hand holds the next one just behind the
        /// pistol, and at 28 cm it was drawn there in VR (user, 2026-10-16). Its own magazine, in the grip, is never one.
        /// </summary>
        private const float SpareNearHandgun = 0.12f;

        /// <summary>
        /// Spare magazines of the flat reload animation: drawn at rest where its other hand waits, a few tens of centimetres
        /// from the gun (out of the flat view), moved in by the reload. Magazine-like (by name, or the chosen magazine's
        /// size), not part of the chosen one. In VR they hung in the air by the gun.
        /// </summary>
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
                // (Never a grip, handle, key or release: the Panzerfaust 3's grips, 铁拳3 握把 and 前握把, move in its reload and
                // have its rocket's size, and were hidden as spares: both hands then held air; scouts, 2026-10-17.)
                if (size > 0.6f || !IsMagName(t.name, single) && (magSize < 0.01f || Mathf.Abs(size - magSize) > magSize * 0.4f)
                    || IsNotMag(t.name, single) || OpticName.IsMatch(t.name) || NotParkedName.IsMatch(t.name))
                {
                    continue;
                }
                // Nothing on the bore line: a suppressor on the muzzle moves in some reloads too (an HK416-B/S's), and is
                // the size of a magazine.
                // (Only ahead of the grip, where a suppressor is: the mwii-g17's spare waits 6 cm off the bore's line but 53 cm behind
                // the muzzle, and was kept drawn, floating beside the gun in the player's view; user, 2026-10-18.)
                Vector3 at = sampler.RelIdle[i].position;
                bool onBore = Vector3.ProjectOnPlane(at - d.MuzzlePoint, d.AimAxis).magnitude < 0.06f && Vector3.Dot(at - d.GripPoint, d.AimAxis) > 0f;
                if (onBore || Regex.IsMatch(t.name, @"suppress|silenc|muzzle|消音|枪口", RegexOptions.IgnoreCase))
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

        private static readonly Regex AmmoPartName = new Regex(@"gauge|guage|loader|follower|弹|patron", RegexOptions.IgnoreCase);
        private static readonly Regex TwoGunsName = new Regex(@"akimbo|dual|double|bundle", RegexOptions.IgnoreCase);
        // A part of the gun named with a magazine word all the same (the K13's "416F MAG KEY": its magazine release; the AT-1K's
        // 提把: its carrying handle), or a round in the gun (the M1014's "12_guage_in_gun").
        private static readonly Regex NotParkedName = new Regex(
            @"key|button|release|catch|latch|lever|handle|grip|in_?gun|chamber|把|柄|按钮|卡笋", RegexOptions.IgnoreCase);

        /// <summary>
        /// Ammunition the reload animation keeps parked beside the gun at rest and only brings in during the reload: a spare
        /// magazine (the HK416 family's behind the stock, the M417A2's 45 cm away, the FNX-45's "Magazine Tac", the BRN180's drum
        /// beside the stock), a loose round (the HK416's 子弹), a belt waiting behind the QJB201's stock. The game draws them
        /// where they wait, so in VR they floated next to the gun (user, 2026-10-18: "the 2nd mag floats"). The rules above take
        /// a spare only when it moves in the reload and is named or sized as one; these are found by where they are.
        /// Every drawn part is grouped with the parts it touches (within 2 cm); a group is parked ammunition when it is not the
        /// gun at the grip, holds a part named as ammunition or of the magazine's size, and nothing in it is the gun's: no part
        /// already chosen (the magazine, the moving parts, the belt), no mesh over 60 cm (a gun's body in one piece: the PATRIOT
        /// AR-15's launcher grenade is grouped with it and stays), nothing on the bore's line ahead of the grip (a loaded round,
        /// a suppressor), nothing where the seated magazine is, no sight, no key, button or handle (the K13's "416F MAG KEY",
        /// the AT-1K's 提把). Hidden while held, as spares are. Not for a weapon of two guns (an akimbo pair's second pistol
        /// hangs apart by design).
        /// </summary>
        private static void ParkedAmmo(WeaponRigData d, Weapon w, Transform[] all, Transform root, Transform gunBody, Transform muzzle,
            HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, RigidPose gunIdle)
        {
            if (gunBody == null || d.Action == ActionType.Melee || d.Action == ActionType.Throwable
                || TwoGunsName.IsMatch(d.Key ?? "") || w.configuration.muzzles != null && w.configuration.muzzles.Length > 1)
            {
                return;
            }
            // Each drawn mesh at rest with its own gun-space box (not the arms, not a laser's beam, nothing parked metres away).
            var parts = new List<Transform>();
            var boxes = new List<Bounds>();
            foreach (Transform t in all)
            {
                bool hasMesh = t.GetComponent<MeshFilter>() != null && t.GetComponent<MeshRenderer>() != null || t.GetComponent<SkinnedMeshRenderer>() != null;
                if (!hasMesh || armRelated.Contains(t) || !ShownAtIdle(t, sampler) || LaserBeamName.IsMatch(t.name)
                    || (sampler.Idle(t).position - gunIdle.position).sqrMagnitude > 1.5f * 1.5f)
                {
                    continue;
                }
                Bounds b = sampler.GunSpaceBoundsWhere(t, gunIdle, x => x != t);
                if (b.size.sqrMagnitude < 1e-8f || b.size.magnitude > 2f)
                {
                    continue;
                }
                parts.Add(t);
                boxes.Add(b);
            }
            if (parts.Count < 2)
            {
                return;
            }
            // The groups: parts that touch within 2 cm, through each other.
            int[] group = new int[parts.Count];
            for (int k = 0; k < parts.Count; k++)
            {
                group[k] = k;
            }
            int Find(int k)
            {
                while (group[k] != k)
                {
                    k = group[k] = group[group[k]];
                }
                return k;
            }
            for (int k = 0; k < parts.Count; k++)
            {
                Bounds near = boxes[k];
                near.Expand(0.04f); // within 2 cm
                for (int j = k + 1; j < parts.Count; j++)
                {
                    if (near.Intersects(boxes[j]))
                    {
                        group[Find(j)] = Find(k);
                    }
                }
            }
            // The gun: the group of the part at (or nearest) the grip.
            int seed = 0;
            for (int k = 1; k < boxes.Count; k++)
            {
                if (boxes[k].SqrDistance(d.GripPoint) < boxes[seed].SqrDistance(d.GripPoint))
                {
                    seed = k;
                }
            }
            int gun = Find(seed);
            // What is already chosen (the magazine and what goes with it, the belt, the moving parts, the spares).
            var chosen = new List<Transform>();
            var paths = new List<int[]> { d.MagazinePath, d.SlidePath, d.HandlePath, d.HingePath };
            paths.AddRange(d.MagazinePartPaths);
            paths.AddRange(d.BeltPartPaths);
            paths.AddRange(d.SparePartPaths);
            paths.AddRange(d.ChamberShellPaths); // a break action's shells in their chambers are drawn while they are loaded
            foreach (int[] path in paths)
            {
                Transform c = path != null ? WeaponRig.Resolve(root, path) : null;
                if (c != null)
                {
                    chosen.Add(c);
                }
            }
            // Where the seated magazine is (within 2 cm).
            Bounds seated = default;
            bool hasSeat = d.MagazinePath != null;
            if (hasSeat)
            {
                Vector3 c = d.MagBounds.center, e = d.MagBounds.extents;
                seated = new Bounds(d.MagSeat.TransformPoint(c), Vector3.zero);
                for (int q = 0; q < 8; q++)
                {
                    seated.Encapsulate(d.MagSeat.TransformPoint(c + new Vector3((q & 1) == 0 ? -e.x : e.x, (q & 2) == 0 ? -e.y : e.y, (q & 4) == 0 ? -e.z : e.z)));
                }
                seated.Expand(0.04f);
            }
            Vector3 magSize = Abs(d.MagBounds.size);
            float[] magDims = { magSize.x, magSize.y, magSize.z };
            System.Array.Sort(magDims);
            bool single = d.Action == ActionType.SingleShot;
            // Each part: the gun's (spoils its group), ammunition, or neither.
            var gunPart = new bool[parts.Count];
            var ammo = new bool[parts.Count];
            for (int k = 0; k < parts.Count; k++)
            {
                Transform t = parts[k];
                Vector3 at = boxes[k].center;
                bool onBore = Vector3.ProjectOnPlane(at - d.MuzzlePoint, d.AimAxis).magnitude < 0.06f && Vector3.Dot(at - d.GripPoint, d.AimAxis) > 0f;
                gunPart[k] = boxes[k].size.magnitude > 0.6f || onBore || chosen.Exists(c => t == c || t.IsChildOf(c) || c.IsChildOf(t))
                             || OpticName.IsMatch(t.name) || NotParkedName.IsMatch(t.name) || hasSeat && seated.Intersects(boxes[k])
                             || Regex.IsMatch(t.name, @"suppress|silenc|muzzle|消音|枪口", RegexOptions.IgnoreCase);
                Vector3 sz = boxes[k].size;
                float[] dims = { sz.x, sz.y, sz.z };
                System.Array.Sort(dims);
                bool magSized = magDims[2] > 0.02f && Mathf.Abs(dims[0] - magDims[0]) <= magDims[0] * 0.35f + 0.01f
                                && Mathf.Abs(dims[1] - magDims[1]) <= magDims[1] * 0.35f + 0.01f && Mathf.Abs(dims[2] - magDims[2]) <= magDims[2] * 0.35f + 0.01f;
                ammo[k] = magSized || IsMagName(t.name, single) || RoundName.IsMatch(t.name) || AmmoPartName.IsMatch(t.name);
            }
            var hide = new List<Transform>();
            var done = new HashSet<int> { gun };
            for (int k = 0; k < parts.Count; k++)
            {
                int g = Find(k);
                if (!done.Add(g))
                {
                    continue; // (the gun, or a group already judged)
                }
                var members = new List<int>();
                Bounds box = boxes[k];
                for (int j = 0; j < parts.Count; j++)
                {
                    if (Find(j) == g)
                    {
                        members.Add(j);
                        box.Encapsulate(boxes[j]);
                    }
                }
                if (!members.Exists(j => ammo[j]) || members.Exists(j => gunPart[j]) || box.size.magnitude > 0.6f)
                {
                    continue;
                }
                float gap = float.MaxValue;
                for (int j = 0; j < parts.Count; j++)
                {
                    if (Find(j) == gun)
                    {
                        gap = Mathf.Min(gap, Mathf.Sqrt(Gap(box, boxes[j])));
                    }
                }
                foreach (int j in members)
                {
                    hide.Add(parts[j]);
                }
                d.Notes += $"parked ammunition '{parts[members.Find(j => ammo[j])].name}'{(members.Count > 1 ? $" and {members.Count - 1} part(s) with it" : "")} ({gap * 100f:0} cm from the gun at rest) hidden while held ";
            }
            foreach (Transform t in hide)
            {
                if (hide.Exists(o => o != t && t.IsChildOf(o)))
                {
                    continue; // (hidden with its parent)
                }
                if (d.SparePartPaths.Count >= 16)
                {
                    d.Warnings.Add($"more parked ammunition than hidden: '{t.name}' and later ones stay drawn");
                    break;
                }
                d.SparePartPaths.Add(WeaponRig.PathOf(root, t));
            }
        }

        /// <summary>The squared distance between two boxes (0 when they touch or overlap).</summary>
        private static float Gap(Bounds a, Bounds b)
        {
            Vector3 d = Vector3.Max(Vector3.zero, Vector3.Max(a.min - b.max, b.min - a.max));
            return d.sqrMagnitude;
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
            Vector3 rightAbs = Vector3.Cross(d.Up, d.AimAxis);
            rightAbs = new Vector3(Mathf.Abs(rightAbs.x), Mathf.Abs(rightAbs.y), Mathf.Abs(rightAbs.z));
            // The node the slide hangs under may draw the charging handle itself, T and shaft in one mesh (the K13s: '416F BOLT',
            // 19 cm along the bore, 0.8 cm tall, a 6 cm T; its children the carrier, the slide, and a latch on the T). The latch was
            // taken for the T below, and racked, the latch came back and the T stayed ("nothing goes back", as Tasks 304; still-bolt
            // check, Tasks 307). The T is cut out of that mesh and comes back with the hand instead (the AR rule, run later, cuts
            // at the receiver's back end, from that mesh alone: tools/export_checks/parent_handle_rule.py takes the seven K13s and
            // Suzuya's two AR-15s, which were cut out of every mesh there, receiver too, and nothing else).
            Transform holder = slide != null ? slide.parent : null;
            if (holder != null && holder != root && holder != gunBody && (holder.GetComponent<MeshFilter>() != null || holder.GetComponent<SkinnedMeshRenderer>() != null))
            {
                Bounds own = sampler.GunSpaceBoundsWhere(holder, gunIdle, t => t != holder);
                float along = Vector3.Dot(own.size, aimAbs), tall = Vector3.Dot(own.size, upAbs), across = Vector3.Dot(own.size, rightAbs);
                if (along >= 0.12f && along <= 0.25f && tall <= 0.015f && across >= 0.03f)
                {
                    d.CarveFromPath = WeaponRig.PathOf(root, holder);
                    // Round the whole T: its wings reach 3.3 cm from the rack point (the K13's 6.1 cm T), and only that mesh is cut.
                    d.CarveRadius = Mathf.Clamp(across * 0.5f + 0.008f, 0.03f, 0.045f);
                    d.SlideMethod += $" (the charging handle is '{holder.name}', the slide's holder: cut out of it)";
                    return;
                }
            }
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
                if (length < 0.03f || length > 0.12f || above < 0f || above > 0.06f)
                {
                    continue; // a T is a few centimetres long and sits just over the bore, under the sights
                }
                // A T is flat (at most 1.5 cm tall) and rides just over the carrier's top (its middle at most 1.5 cm above it).
                // Without these the reload case took a sight: the M4A1 [SQUAD]'s rear sight '对象006' (4.5 cm tall, 3.3 cm over
                // the carrier), its ACOG's mount and its red dot's mount (2.1-2.2 cm tall, 1.8-2.3 cm over), so racking moved
                // the sight and not the handle (user, headset, 2026-10-18 evening: "the kimme racks"). The K13's T '416F ZG' is
                // 0.8 cm tall, 0.7 cm over its bolt.
                if (Vector3.Dot(b.size, upAbs) > 0.015f || Vector3.Dot(b.center - rearTop, d.Up) > 0.015f)
                {
                    continue;
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
                // No short T: the handle drawn whole, T and shaft in one part (the M4A1 [SQUAD] family's '对象003': 16.3 cm along the
                // bore, 1.1 cm tall, 5.5 cm across, on the carrier's rear top). Taken only by the short T's length it was never
                // found, the hand racked the carrier inside the receiver and the T the user pulled at stayed (user, 2026-10-20 night:
                // "cannot pull back the bolt thingy again"). Flat (1.3 cm at most: an M417A2's 1.5 cm part is left to its cut-out
                // handle) and across the receiver at its back; tools/export_checks/whole_t_rule.py takes the three squad M4A1s.
                float wholeGap = 0.07f;
                foreach (Transform t in all)
                {
                    if (!index.TryGetValue(t, out int i) || sampler.ReloadMax[i] > 0.01f || t == slide || t.IsChildOf(slide) || slide.IsChildOf(t)
                        || UnderOptic(t, gunBody)
                        || MagName.IsMatch(t.name) || OpticName.IsMatch(t.name) || !sampler.IdleVisible(t) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                    {
                        continue;
                    }
                    Bounds b = sampler.GunSpaceBounds(t, gunIdle);
                    float along = Vector3.Dot(b.size, aimAbs), tall = Vector3.Dot(b.size, upAbs), across = Vector3.Dot(b.size, rightAbs);
                    float above = Vector3.Dot(b.center - d.MuzzlePoint, d.Up);
                    if (along < 0.12f || along > 0.22f || tall > 0.013f || across < 0.03f || above < 0f || above > 0.06f
                        || Vector3.Dot(b.center - rearTop, d.Up) > 0.015f)
                    {
                        continue;
                    }
                    float gap = Mathf.Sqrt(b.SqrDistance(rearTop));
                    if (gap < wholeGap)
                    {
                        wholeGap = gap;
                        best = t;
                        bestBounds = b;
                    }
                }
                if (best == null)
                {
                    return;
                }
                d.SlideMethod += " (a whole handle)";
            }
            d.HandlePath = WeaponRig.PathOf(root, best);
            d.HandleRestLocal = sampler.IdleLocalPosition(best);
            d.HandleTravel = -d.AimAxis * Mathf.Min(0.075f, d.SlideTravel.magnitude > 1e-4f ? d.SlideTravel.magnitude : 0.075f);
            d.RackPoint = bestBounds.center - d.AimAxis * Mathf.Abs(Vector3.Dot(bestBounds.extents, aimAbs)) * 0.8f;
            d.SlideMethod += $" + its T handle '{best.name}' (still in the animations)";
        }

        /// <summary>
        /// An AR with no slide found and no handle named (its bolt baked into the receiver): the model's own charging handle is
        /// still there, a flat T across the receiver at its back, and it never moves. The rack point was the grip's guess
        /// (ArChargingHandle: 5 cm behind the grip point, 3 cm over the bore; the receiver's mesh cannot be measured in game),
        /// about 7 cm behind the back end of the MWII M4's own T, and a knob was added there: the two pull-back handles the user
        /// saw, the knob moving while the T stood still (user, 2026-10-18, task 269). Here the T is the handle: the hand takes
        /// its back end and it comes back with the hand, and no knob is added (HeldWeapon). The bar must run across the receiver
        /// (the M4's own T is 3.2 cm across; the flat plate and fin that stand at the same place on the holo and plain M4 are
        /// 1.1 cm across), and it must stand within 10 cm of the guess (the guess sat 8 to 10 cm behind the handle on other
        /// ARs, see ArChargingHandle).
        /// </summary>
        private static void ArOwnHandleNoSlide(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root,
            Transform gunBody, Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, RigidPose gunIdle)
        {
            if (d.Handgun || d.IsMachineGun || d.Action != ActionType.SelfLoading || d.SlidePath != null || d.HandlePath != null
                || !ArPattern.IsMatch(d.Key ?? ""))
            {
                return;
            }
            Vector3 boreAboveGrip = d.MuzzlePoint + d.AimAxis * Vector3.Dot(d.GripPoint - d.MuzzlePoint, d.AimAxis);
            Vector3 guess = boreAboveGrip - d.AimAxis * 0.05f + d.Up * 0.03f;
            Vector3 across = Vector3.Cross(d.Up, d.AimAxis).normalized;
            Vector3 aimAbs = new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z));
            Vector3 upAbs = new Vector3(Mathf.Abs(d.Up.x), Mathf.Abs(d.Up.y), Mathf.Abs(d.Up.z));
            Vector3 acrossAbs = new Vector3(Mathf.Abs(across.x), Mathf.Abs(across.y), Mathf.Abs(across.z));
            Transform best = null;
            Bounds bestBounds = default;
            bool bestWhole = false;
            float bestGap = 0.10f;
            foreach (Transform t in all)
            {
                if (!index.TryGetValue(t, out int i) || sampler.ReloadMax[i] > 0.01f || UnderOptic(t, gunBody) || MagName.IsMatch(t.name)
                    || OpticName.IsMatch(t.name) || !sampler.IdleVisible(t) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                {
                    continue;
                }
                Bounds b = sampler.GunSpaceBounds(t, gunIdle);
                float length = Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));
                float above = Vector3.Dot(b.center - d.MuzzlePoint, d.Up);
                float height = Vector3.Dot(b.size, upAbs), width = Vector3.Dot(b.size, acrossAbs), along = Vector3.Dot(b.size, aimAbs);
                // A T: a bar across the receiver, a few centimetres long, flat (at most 1.5 cm tall), riding in the receiver's top
                // under the sights (its middle at most 3.2 cm over the bore). Without the height and over-the-bore limits the MWII M4
                // holo's folded rear sight '..._rec_..._002' (2.0 cm tall, 3.4 cm over the bore, sitting on the rail) was taken,
                // and racking moved the sight (user, headset, 2026-10-18 evening: "the bolt doesn't go back, it's the sight which
                // goes back"). The same limits as ArStaticHandle's (task 271).
                bool shortT = length >= 0.03f && length <= 0.12f && above >= 0f && above <= 0.032f && height <= 0.015f && width >= 0.025f;
                // Or the whole charging handle in one part, its rod and its T together: 12-22 cm along the bore (longest that way),
                // its T 3 cm or more across, at most 2.2 cm tall, on the bore's line under the sights, its back end within 6 cm
                // of the guess along the bore. The MWII M4s have theirs so ('对象001_001', 17.3 x 5.6 x 1.8 cm): too long and too
                // tall for the T limits, it was never taken, and only the bolt carrier inside moved (user, headset, 2026-10-19
                // night: "able to rack the rack thing, but the thing doesn't go back", task 304). Over the export the same limits
                // take exactly the handle the analysis already knows by name on every AR that has one (mk18, m733, Suzuya, K13,
                // M4A1: tools/export_checks/ar_long_handle_rule.py) and on no other part.
                float aside = Mathf.Abs(Vector3.Dot(b.center - d.MuzzlePoint, across));
                float rearAlong = Vector3.Dot(b.center, d.AimAxis) - Mathf.Abs(Vector3.Dot(b.extents, aimAbs));
                bool wholeHandle = along >= 0.12f && along <= 0.22f && along >= Mathf.Max(height, width) && width >= 0.03f && height <= 0.022f
                                   && above >= 0f && above <= 0.032f && aside <= 0.015f && Mathf.Abs(rearAlong - Vector3.Dot(guess, d.AimAxis)) <= 0.06f;
                if (!shortT && !wholeHandle)
                {
                    continue;
                }
                float gap = Mathf.Sqrt(b.SqrDistance(guess));
                if (gap < bestGap)
                {
                    bestGap = gap;
                    best = t;
                    bestBounds = b;
                    bestWhole = wholeHandle && !shortT;
                }
            }
            if (best == null)
            {
                return;
            }
            d.HandlePath = WeaponRig.PathOf(root, best);
            d.HandleRestLocal = sampler.IdleLocalPosition(best);
            d.HandleTravel = -d.AimAxis * 0.075f;
            // The hand takes the T at its back end, as ArStaticHandle does; the T reaches forward from there. A whole handle's T is
            // the last centimetres of its 17 cm: taken 1.2 cm in from its back end (at 80% of its half length the hand sat 7 cm
            // forward, on the rod inside the receiver).
            float halfAlong = Mathf.Abs(Vector3.Dot(bestBounds.extents, aimAbs));
            d.RackPoint = bestBounds.center - d.AimAxis * (bestWhole ? halfAlong - 0.012f : halfAlong * 0.8f);
            d.RackFromHand = true;
            d.SlideTravel = d.HandleTravel;
            d.Warnings.RemoveAll(w => w.StartsWith("no moving slide/bolt", StringComparison.Ordinal));
            d.SlideMethod += $" + its own charging handle '{best.name}' (no moving slide: the model's T, moved with the hand)";
            Log.InfoOnce("ownhandle:" + d.Key, $"{d.Key}: its own charging handle '{best.name}' is the handle the hand racks ({bestGap * 100f:0.0} cm from the rack guess); no knob added.");
        }

        /// <summary>
        /// An AR with nothing found to move (no slide from the animations, no handle, no bolt): its bolt carrier is still in the
        /// model, a long narrow part on the bore line behind the magazine well, seen through the ejection port, and it moves
        /// back as the handle is racked. Without it nothing visible moved on the MWII M4s (the T could not be carved out of
        /// their receiver; user, headset, 2026-10-18: "the bolt doesn't go back"). Found by its shape: 5-20 cm along the
        /// aim, at least 2.5 times longer than wide, at most 3.2 cm wide or tall, its middle within 1.5 cm of the bore's
        /// height and 1 cm of its side, 5-22 cm ahead of the grip, not the magazine or a sight; exactly one, or nothing.
        /// Checked on the export (2026-10-19): where a bolt was found from motion (24 guns) the shape picked that bolt 6 times
        /// and nothing else ever; among the guns with nothing to move it finds a part only on the three MWII M4s, their
        /// '对象003_001' (8.5 cm long, 11.5 cm ahead of the grip, 0.2 cm over the bore; the M4A1's own bolt: 10.6 cm, 16 cm,
        /// 0.1 cm under).
        /// </summary>
        private static void ArBoltCarrier(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root,
            Transform gunBody, Transform muzzle, HashSet<Transform> armRelated, HashSet<Transform> gunBones, PoseSampler sampler, RigidPose gunIdle)
        {
            // Also after a charging handle was found with no slide (ArOwnHandleNoSlide, a handle by name): the carrier moves with
            // it as on a real AR. Before, a found handle stopped this rule, so the MWII M4s' T (taken as a whole handle since task
            // 304) would have moved alone with nothing in the ejection port.
            bool handleAlone = d.HandlePath != null && d.RackFromHand;
            if (d.Handgun || d.IsMachineGun || d.Action != ActionType.SelfLoading || d.SlidePath != null || d.HandlePath != null && !handleAlone || d.BoltPath != null
                || !ArPattern.IsMatch(d.Key ?? ""))
            {
                return;
            }
            Vector3 across = Vector3.Cross(d.Up, d.AimAxis).normalized;
            Vector3 aimAbs = new Vector3(Mathf.Abs(d.AimAxis.x), Mathf.Abs(d.AimAxis.y), Mathf.Abs(d.AimAxis.z));
            Vector3 upAbs = new Vector3(Mathf.Abs(d.Up.x), Mathf.Abs(d.Up.y), Mathf.Abs(d.Up.z));
            Vector3 acrossAbs = new Vector3(Mathf.Abs(across.x), Mathf.Abs(across.y), Mathf.Abs(across.z));
            Transform magazine = d.MagazinePath != null ? WeaponRig.Resolve(root, d.MagazinePath) : null;
            Transform best = null;
            Bounds bestBounds = default;
            int found = 0;
            foreach (Transform t in all)
            {
                // (The handle found alone may be its own carrier: excluding it, 2026-10-20, left the M4A1 family's charging handle
                // unrackable again; reverted.)
                if (!index.TryGetValue(t, out _) || magazine != null && t.IsChildOf(magazine)
                    || UnderOptic(t, gunBody) || MagName.IsMatch(t.name)
                    || OpticName.IsMatch(t.name) || !sampler.IdleVisible(t) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones))
                {
                    continue;
                }
                Bounds b = sampler.GunSpaceBounds(t, gunIdle);
                float length = Vector3.Dot(b.size, aimAbs), height = Vector3.Dot(b.size, upAbs), width = Vector3.Dot(b.size, acrossAbs);
                if (length < 0.05f || length > 0.20f || height > 0.032f || width > 0.032f || length < 2.5f * Mathf.Max(height, width))
                {
                    continue;
                }
                float above = Vector3.Dot(b.center - d.MuzzlePoint, d.Up);
                float aside = Mathf.Abs(Vector3.Dot(b.center - d.MuzzlePoint, across));
                float ahead = Vector3.Dot(b.center - d.GripPoint, d.AimAxis);
                if (Mathf.Abs(above) > 0.015f || aside > 0.01f || ahead < 0.05f || ahead > 0.22f)
                {
                    continue;
                }
                found++;
                best = t;
                bestBounds = b;
            }
            if (found != 1)
            {
                if (found > 1)
                {
                    d.Notes += $"{found} bolt-carrier-shaped parts on the bore line, none taken ";
                }
                return;
            }
            d.SlidePath = WeaponRig.PathOf(root, best);
            d.SlideRestLocal = sampler.IdleLocalPosition(best);
            d.SlideTravel = handleAlone && d.HandleTravel.sqrMagnitude > 1e-6f ? d.HandleTravel : -d.AimAxis * 0.075f;
            d.SlideMethod = handleAlone ? d.SlideMethod + $" + its bolt carrier by its shape '{best.name}', moved with it" : $"bolt carrier by its shape '{best.name}'";
            d.Warnings.RemoveAll(w => w.StartsWith("no moving slide/bolt", StringComparison.Ordinal));
            Log.InfoOnce("boltcarrier:" + d.Key, $"{d.Key}: its bolt carrier '{best.name}' ({Vector3.Dot(bestBounds.size, aimAbs) * 100f:0.0} cm long, "
                                                 + $"{Vector3.Dot(bestBounds.center - d.GripPoint, d.AimAxis) * 100f:0.0} cm ahead of the grip) moves back when the gun is racked.");
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
                return;
            }
            // A carrier made in one mesh with its handle (an AK's tab, the AS Val's hook, out to its right side): the vertex of
            // it farthest from the bore is the handle's tip; the hand takes it just inside that. The rack point sat in the
            // middle of the carrier, 8 to 10 cm behind the tab (AK-24, AS Val, ACE 32, AK-105; sandbox, 2026-10-18).
            float gripAlong = Vector3.Dot(d.GripPoint - d.MuzzlePoint, d.AimAxis);
            Bounds whole = sampler.GunSpaceBoundsNear(slide, gunIdle, 0.5f);
            if (Vector3.Dot(whole.center - d.GripPoint, d.AimAxis) > 0.27f)
            {
                return; // a pump's forend (31-35 cm ahead of the grip on the R870s): the hand closes round it, not on a tip
            }
            RigidPose gunInv = gunIdle.Inverse();
            float far = best;
            Vector3 tip = Vector3.zero;
            foreach (MeshFilter mf in slide.GetComponentsInChildren<MeshFilter>(true))
            {
                Mesh m = mf.sharedMesh;
                if (m == null || !m.isReadable || m.vertexCount == 0 || !ActiveUpTo(mf.transform, slide))
                {
                    continue;
                }
                Transform t = mf.transform;
                RigidPose idle = sampler.Idle(t);
                Vector3[] v = m.vertices;
                int step = Mathf.Max(1, v.Length / 6000);
                for (int i = 0; i < v.Length; i += step)
                {
                    Vector3 p = gunInv.TransformPoint(idle.TransformPoint(Vector3.Scale(v[i], t.lossyScale)));
                    float along = Vector3.Dot(p - d.MuzzlePoint, d.AimAxis);
                    if (along > -0.05f || along < gripAlong - 0.25f)
                    {
                        continue; // on the receiver only (not a piston rod's end out in the handguard)
                    }
                    float off = Off(p);
                    if (off > far)
                    {
                        far = off;
                        tip = p;
                    }
                }
            }
            if (far > best)
            {
                Vector3 outward = Vector3.ProjectOnPlane(tip - d.MuzzlePoint, d.AimAxis).normalized;
                d.RackPoint = tip - outward * 0.008f;
                d.SlideMethod += $" (taken by its tip farthest from the bore, {far * 100f:0.0} cm out: its handle)";
            }
        }

        /// <summary>
        /// The moving part's tip farthest from the bore could not be read here (its meshes are on the graphics card only), so
        /// the rack point stayed on the part's middle: HeldWeapon takes the tip from the part's drawn surface instead
        /// (<see cref="TipOfSlide"/>). The QJB 95 / 95-1's cocking hook under the carry handle is part of a 29 cm rod: the
        /// rack point sat on the bolt tube inside the receiver, 19 cm behind the hook the hand reached for, out of its reach
        /// (user, 2026-10-19 night: "not able to rack it", task 305). The same guns as RackOnOutstandingPiece's.
        /// </summary>
        internal static bool WantsDrawnSlideTip(WeaponRigData d, Transform slide)
        {
            if (slide == null || d.Handgun || d.SlidePath == null || d.HandlePath != null || d.CarveHandle || d.AddedHandle || d.RackFromHand || d.RackOnSurface
                || d.SlideMethod == null || d.SlideMethod.StartsWith("reload motion (charging handle)", StringComparison.Ordinal)
                || d.SlideMethod.Contains("(taken by") || ArPattern.IsMatch(d.Key ?? ""))
            {
                return false;
            }
            foreach (MeshFilter mf in slide.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh != null && !mf.sharedMesh.isReadable)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The rack point on the point of a moving part's surface (gun space) farthest from the bore, on the receiver (5 cm
        /// behind the muzzle to 25 cm behind the grip), when it stands at least 1.5 cm farther out than the rack point does now;
        /// just inside that tip, as RackOnOutstandingPiece takes it from the vertices. False (and the rack point left) when no
        /// point does, or for a pump's forend (its middle more than 27 cm ahead of the grip).
        /// </summary>
        internal static bool TipOfSlide(WeaponRigData d, List<Vector3> gunPoints, Vector3 current, out Vector3 rack)
        {
            rack = current;
            if (gunPoints == null || gunPoints.Count < 8)
            {
                return false;
            }
            Vector3 mean = Vector3.zero;
            foreach (Vector3 p in gunPoints)
            {
                mean += p;
            }
            mean /= gunPoints.Count;
            if (Vector3.Dot(mean - d.GripPoint, d.AimAxis) > 0.27f)
            {
                return false;
            }
            float Off(Vector3 p) => Vector3.ProjectOnPlane(p - d.MuzzlePoint, d.AimAxis).magnitude;
            float gripAlong = Vector3.Dot(d.GripPoint - d.MuzzlePoint, d.AimAxis);
            float best = Off(current) + 0.015f;
            bool found = false;
            Vector3 tip = current;
            foreach (Vector3 p in gunPoints)
            {
                float along = Vector3.Dot(p - d.MuzzlePoint, d.AimAxis);
                if (along > -0.05f || along < gripAlong - 0.25f)
                {
                    continue;
                }
                float off = Off(p);
                if (off > best)
                {
                    best = off;
                    tip = p;
                    found = true;
                }
            }
            if (!found)
            {
                return false;
            }
            Vector3 outward = Vector3.ProjectOnPlane(tip - d.MuzzlePoint, d.AimAxis).normalized;
            rack = tip - outward * 0.008f;
            return true;
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
                    || t.GetComponent<MeshRenderer>() == null || !index.TryGetValue(t, out int i) || !sampler.IdleVisible(t)
                    || PoseSampler.IsLaserModule(t, root, gunBody))
                {
                    continue; // a laser's or light's module is never the knob that moves with the slide
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

        /// <summary>
        /// A bolt beside its charging handle (the MG3s: 'mg3_bolt', a group of two meshes seen under the open cover, the sibling of
        /// 'mg3_charginghandle', the part the reload moves): racked, the handle came back and the bolt stayed (Tasks 307, the
        /// still-bolt check; tools/export_checks/still_bolts.py, and this rule over the export takes the two MG3s alone). It rides
        /// with the handle as a companion; the hand still takes the handle (the rack point is left alone).
        /// </summary>
        private static void BoltBesideHandle(WeaponRigData d, Transform root, PoseSampler sampler, RigidPose gunIdle)
        {
            Transform slide = WeaponRig.Resolve(root, d.SlidePath);
            if (slide == null || slide.parent == null || d.Action != ActionType.SelfLoading || d.BoltPath != null || d.SlideCompanionPaths.Count > 0
                || !HandleSlideName.IsMatch(slide.name))
            {
                return;
            }
            foreach (Transform t in slide.parent)
            {
                if (t == slide || !BoltGroupName.IsMatch(t.name) || BoltGroupNot.IsMatch(t.name)
                    || !t.GetComponentsInChildren<Renderer>(false).Any(r => r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer)))
                {
                    continue;
                }
                Bounds b = sampler.GunSpaceBoundsWhere(t, gunIdle, x => false);
                if (b.size.magnitude < 0.01f || b.size.magnitude > 0.3f)
                {
                    continue;
                }
                d.SlideCompanionPaths.Add(WeaponRig.PathOf(root, t));
                d.SlideCompanionRest.Add(sampler.IdleLocalPosition(t));
                d.Notes += $"bolt '{t.name}' beside the charging handle (moves with it) ";
                d.SlideMethod += $" + its bolt '{t.name}' moving with it";
                return;
            }
        }

        private static readonly Regex HandleSlideName = new Regex(@"charg(ing)?[ _-]?handle|cocking[ _-]?handle|拉机柄", RegexOptions.IgnoreCase);
        private static readonly Regex BoltGroupName = new Regex(@"(^|[^a-z])bolt($|[^a-z])|枪机", RegexOptions.IgnoreCase);
        private static readonly Regex BoltGroupNot = new Regex(@"catch|release|stop|lock|button|cover|knob", RegexOptions.IgnoreCase);

        /// <summary>Words a slide companion may add to the slide's name: not these.</summary>
        private static readonly Regex CompanionBlocked = new Regex(
            @"^(release|catch|stop|button|latch|selector|safety|switch|mag|magazine|sight|scope|optic|light|laser|beam|dot|lens|glass|magnif|zoom|reticle|torch|flashlight|trigger|spring|pin|screw|lod\d*)$",
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
        /// A shotgun shell by its size: 4 to 13 cm long, 1.5 cm thick or more and under half as thick as long (a magazine, a
        /// clip or a rocket is not one, nor a rifle's or a revolver's round, about 1.2 cm thick).
        /// </summary>
        internal static bool ShellSized(Bounds b)
        {
            Vector3 s = b.size;
            float longest = Mathf.Max(s.x, Mathf.Max(s.y, s.z));
            float second = s.x + s.y + s.z - longest - Mathf.Min(s.x, Mathf.Min(s.y, s.z));
            return longest >= 0.04f && longest <= 0.13f && second >= 0.015f && second <= longest * 0.5f;
        }

        /// <summary>
        /// Where a shotgun's shells go in, when its "magazine" is a shell (the reload animation's). A break action: into its
        /// chambers, where its shells sit at rest, along the bore, carried with the barrels (<see cref="ChamberShells"/>). A
        /// tube: at the loading port (<see cref="ShellPort"/>). Both were taken from the shell's first motion in the reload and
        /// where it waits at rest: the R870's shells were to go in 46 cm behind its grip and 29 cm under the bore (where its
        /// reload's shell waits, out of the flat view), the DOUBLE-BARREL's from above into the shut chamber; no shotgun could
        /// be loaded by hand (user, 2026-10-22).
        /// </summary>
        private static void AnalyzeShells(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root,
            HashSet<Transform> armRelated, PoseSampler sampler, RigidPose gunIdle, Weapon w)
        {
            Transform mag = WeaponRig.Resolve(root, d.MagazinePath);
            if (mag == null || !index.TryGetValue(mag, out int mi) || !ShellSized(d.MagBounds))
            {
                return;
            }
            if (d.Action == ActionType.BreakOpen && d.Hinge == HingeKind.BreakAction)
            {
                ChamberShells(d, all, index, root, mag, armRelated, sampler, gunIdle, w);
            }
            else if (d.Action == ActionType.ShellFed)
            {
                ShellPort(d, mi, sampler);
            }
        }

        /// <summary>
        /// A break action's chambers: the magazine's shell where it sits at rest (in its chamber) and the shells beside it
        /// across the bore, of its size and lying its way (the DOUBLE-BARREL's 'Shotgun Shell Left' / 'Right' 3.9 cm apart, the
        /// M1878's 'Shotgun Shell L' / 'R'), up to the gun's capacity. The chambers are in the barrels, so the seat swings open
        /// with them whatever the shells hang from, and a shell goes into its chamber along the bore.
        /// </summary>
        private static void ChamberShells(WeaponRigData d, Transform[] all, Dictionary<Transform, int> index, Transform root, Transform mag,
            HashSet<Transform> armRelated, PoseSampler sampler, RigidPose gunIdle, Weapon w)
        {
            Bounds magGun = sampler.GunSpaceBounds(mag, gunIdle);
            float offBore = Vector3.ProjectOnPlane(magGun.center - d.MuzzlePoint, d.AimAxis).magnitude;
            if (magGun.size.sqrMagnitude < 1e-8f || offBore > 0.06f)
            {
                d.Warnings.Add($"break action: its shell waits {offBore * 100f:0} cm off the bore at rest, not in a chamber (loaded where it waits)");
                return;
            }
            var chambers = new List<Transform> { mag };
            var centres = new List<Vector3> { magGun.center };
            int capacity = Mathf.Max(1, w.configuration.ammo);
            foreach (Transform t in all)
            {
                if (chambers.Count >= capacity)
                {
                    break;
                }
                if (t == mag || t.IsChildOf(mag) || mag.IsChildOf(t) || !index.ContainsKey(t) || armRelated.Contains(t)
                    || !HasRendererInSubtree(t) || !ShownAtIdle(t, sampler) || chambers.Exists(c => t.IsChildOf(c) || c.IsChildOf(t)))
                {
                    continue;
                }
                Bounds b = sampler.GunSpaceBounds(t, gunIdle);
                Vector3 across = b.center - magGun.center;
                // Its size within a quarter, level with it along the bore within 1.5 cm, a barrel's width or two beside it.
                if (Mathf.Abs(b.size.magnitude - magGun.size.magnitude) > magGun.size.magnitude * 0.25f
                    || Mathf.Abs(Vector3.Dot(across, d.AimAxis)) > 0.015f || across.magnitude < 0.01f || across.magnitude > 0.07f)
                {
                    continue;
                }
                chambers.Add(t);
                centres.Add(b.center);
            }
            d.SeatOnHinge = true;
            d.MagEjectAxis = -d.AimAxis;
            d.ChamberShellPaths.Clear();
            d.ChamberShellRest.Clear();
            for (int i = 0; i < chambers.Count; i++)
            {
                d.ChamberShellPaths.Add(WeaponRig.PathOf(root, chambers[i]));
                d.ChamberShellRest.Add(sampler.RelIdle[index[chambers[i]]]);
            }
            d.Notes += $"{chambers.Count} chamber shell(s) ({string.Join(", ", chambers.ConvertAll(c => "'" + c.name + "'").ToArray())}): loaded along the bore, opened with the barrels ";
        }

        /// <summary>
        /// A tube's loading port: where the reload pushes its shell nearest the bore under it, ahead of the grip and short of the
        /// muzzle, while the shell is drawn; seated as it lies there, going in the way its path went over its last 3 cm (forward
        /// into the tube, or up into the port). With no such point, half way from the grip to the forend, 3 cm under the bore,
        /// along it. The reload's shell itself is hidden while held, as the spare magazines are, wherever it waits at rest: it
        /// is the flat reload's prop, never the gun's look (the R870's floated 33 cm under its stock, the mw-m870's above its
        /// stock's comb, 4 cm off the bore; check-1022-shotguns). The shell in the hand is copied from it at its real size.
        /// </summary>
        private static void ShellPort(WeaponRigData d, int mi, PoseSampler sampler)
        {
            float OffBore(Vector3 p) => Vector3.ProjectOnPlane(p - d.MuzzlePoint, d.AimAxis).magnitude;
            Vector3 restCentre = sampler.RelIdle[mi].TransformPoint(d.MagBounds.center);
            if (d.MagazinePath != null)
            {
                d.SparePartPaths.Add(d.MagazinePath);
                d.Notes += $"its reload shell (waiting {OffBore(restCentre) * 100f:0} cm off the bore at rest) hidden while held ";
            }
            Vector3[] path = sampler.HasReload && sampler.ReloadTrajectory != null ? sampler.ReloadTrajectory[mi] : null;
            bool[] drawn = sampler.ReloadVisible?[mi];
            Quaternion[] turn = sampler.ReloadRotation?[mi];
            float muzzleAhead = Vector3.Dot(d.MuzzlePoint - d.GripPoint, d.AimAxis);
            int best = -1;
            float bestOff = 0.08f;
            for (int k = 0; path != null && k < path.Length; k++)
            {
                if (float.IsNaN(path[k].x) || drawn != null && k < drawn.Length && !drawn[k])
                {
                    continue;
                }
                Quaternion r = turn != null && k < turn.Length && turn[k].w * turn[k].w + turn[k].x * turn[k].x + turn[k].y * turn[k].y + turn[k].z * turn[k].z > 0.5f
                    ? turn[k] : sampler.RelIdle[mi].rotation;
                Vector3 c = path[k] + r * d.MagBounds.center;
                float below = -Vector3.Dot(c - d.MuzzlePoint, d.Up);
                float ahead = Vector3.Dot(c - d.GripPoint, d.AimAxis);
                float off = OffBore(c);
                if (below < 0.012f || ahead < -0.05f || ahead > muzzleAhead - 0.05f || off >= bestOff)
                {
                    continue;
                }
                best = k;
                bestOff = off;
            }
            if (best >= 0)
            {
                // (Recorded only from the step the shell first moved: unrecorded steps hold a zero quaternion.)
                Quaternion rot = turn != null && best < turn.Length && turn[best].w * turn[best].w + turn[best].x * turn[best].x
                    + turn[best].y * turn[best].y + turn[best].z * turn[best].z > 0.5f ? turn[best] : sampler.RelIdle[mi].rotation;
                Vector3 insert = d.AimAxis;
                for (int j = best - 1; j >= 0; j--)
                {
                    Vector3 step = path[best] - path[j];
                    if (!float.IsNaN(step.x) && step.magnitude >= 0.03f)
                    {
                        insert = step.normalized;
                        break;
                    }
                }
                // Into the gun: forward along the tube, or up into the port; any other last motion keeps the tube's line.
                if (Vector3.Dot(insert, d.AimAxis) < 0.3f && Vector3.Dot(insert, d.Up) < 0.5f)
                {
                    insert = d.AimAxis;
                }
                Vector3 c = path[best] + rot * d.MagBounds.center;
                d.MagSeat = new RigidPose(path[best], rot);
                d.MagEjectAxis = -insert;
                d.Notes += $"shells go in at the loading port, where the reload pushes its shell ({Vector3.Dot(c - d.GripPoint, d.AimAxis) * 100f:0} cm ahead of the grip, "
                           + $"{-Vector3.Dot(c - d.MuzzlePoint, d.Up) * 100f:0.0} cm under the bore), {Vector3.Angle(insert, d.AimAxis):0} deg off the bore ";
                return;
            }
            Vector3 port = EstimatedPort(d);
            // Lying along the bore: its long side turned onto it.
            Quaternion lying = d.MagSeat.rotation;
            Vector3 s = d.MagBounds.size;
            Vector3 longLocal = s.x >= s.y && s.x >= s.z ? Vector3.right : s.y >= s.z ? Vector3.up : Vector3.forward;
            Vector3 longGun = lying * longLocal;
            if (Vector3.Dot(longGun, d.AimAxis) < 0f)
            {
                longGun = -longGun;
            }
            Quaternion along = Quaternion.FromToRotation(longGun, d.AimAxis) * lying;
            d.MagSeat = new RigidPose(port - along * d.MagBounds.center, along);
            d.MagEjectAxis = -d.AimAxis;
            d.Warnings.Add("loading port estimated (the reload's shell never comes under the bore by the grip): half way to the forend, 3 cm under the bore");
        }

        /// <summary>
        /// A tube's loading port when no reload shows it: half way from the grip to the forend, 3 cm under the bore (the ports
        /// measured from the reloads of 2026-10-22: 17-30 cm ahead of the grip, 1.4-4.7 cm under the bore).
        /// </summary>
        private static Vector3 EstimatedPort(WeaponRigData d)
        {
            Vector3 forend = d.HasSupportPoint ? d.SupportPoint : d.GripPoint + d.AimAxis * 0.3f;
            Vector3 mid = Vector3.Lerp(d.GripPoint, forend, 0.5f);
            return d.MuzzlePoint + d.AimAxis * Vector3.Dot(mid - d.MuzzlePoint, d.AimAxis) - d.Up * 0.03f;
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
            paths.AddRange(d.ChamberShellPaths); // a break action's shells in their chambers are drawn while they are loaded
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
            if (float.IsNaN(axis.x) || float.IsNaN(axis.y) || float.IsNaN(axis.z) || !(axis.sqrMagnitude > 0.5f))
            {
                // A magazine part (or a holder of it) scaled to nothing on an axis: its points came back not finite and every
                // gate below let a NaN axis through to the eject line (analyzer hunt, 2026-10-21).
                return;
            }
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
                // Shown means drawn as well: a spare magazine with its renderer off (or scaled away) beside the bone leaned the axis
                // toward it (analyzer hunt, 2026-10-21).
                Renderer drawn = mf.GetComponent<Renderer>();
                if (shownNearOnly && (!ActiveUpTo(mf.transform, part) || (mf.transform.position - part.position).sqrMagnitude > 1.5f * 1.5f
                                      || drawn == null || !drawn.enabled || mf.transform.lossyScale.sqrMagnitude < 1e-8f))
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
        internal static bool AxisOf(List<Vector3> points, out Vector3 axisLocal, out float elongation)
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
            bool handgun = d.Slot == 1 || d.IsPistol && d.Slot != 0 && d.Slot != 3;
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
    }
}
