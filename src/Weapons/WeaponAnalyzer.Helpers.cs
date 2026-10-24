using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // WeaponAnalyzer, part: helpers the other parts share. The class and how a weapon is analysed as a whole: WeaponAnalyzer.cs.
    public static partial class WeaponAnalyzer
    {
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
                // A tube gun with no shell of its own (the ARS M500 (Bot Friendly)) loads at its port like the others: under the
                // receiver 10 cm ahead of the grip, its shells went in 10 cm under the bore (check-1022-shotguns).
                bool tube = d.Action == ActionType.ShellFed && !d.Handgun;
                Vector3 seat = d.Action == ActionType.SingleShot
                    ? d.MuzzlePoint - d.AimAxis * 0.05f
                    : tube ? EstimatedPort(d)
                    : d.GripPoint + d.AimAxis * (d.Handgun ? 0.0f : 0.1f) - d.Up * (d.Handgun ? 0.02f : 0.05f);
                d.MagSeat = new RigidPose(seat, Quaternion.LookRotation(d.AimAxis, d.Up));
                d.MagEjectAxis = d.Action == ActionType.SingleShot ? d.AimAxis : tube ? -d.AimAxis : -d.Up; // a shell goes into the tube along it
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
            // A laser's or a light's module (its body, dot or beam, a flashlight) is never the slide, bolt, charging handle,
            // magazine or sight part. Those are picked by name and by motion, and a module's name or its beam (a line) can
            // mislead both. The name is checked up the chain too, but not above a group that holds the gun body itself.
            if (t.GetComponent<LineRenderer>() != null || t.GetComponent<TrailRenderer>() != null || PoseSampler.IsLaserModule(t, root, gunBody))
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

        /// <summary>Whether <paramref name="t"/> or one above it is a bone of the arms mesh.</summary>
        private static bool OnListedArm(Transform t, Transform root, HashSet<Transform> armListed)
        {
            for (Transform p = t; p != null && p != root; p = p.parent)
            {
                if (armListed.Contains(p))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Whether a part found by its magazine name would be taken (the name search's own tests): measured for the notes only,
        /// as the search takes the first match and drops it without trying the next.
        /// </summary>
        private static Transform NextMagByName(Transform[] all, Transform root, Transform gunBody, Transform muzzle, HashSet<Transform> armRelated,
            HashSet<Transform> gunBones, bool launcher, bool beltFed, Transform first, Transform slide, PoseSampler sampler)
        {
            bool past = false;
            foreach (Transform t in all)
            {
                if (t == first)
                {
                    past = true;
                    continue;
                }
                if (!past || !IsMagName(t.name, launcher) || beltFed && IsBeltPiece(t.name) || !IsPartCandidate(t, root, gunBody, muzzle, armRelated, gunBones)
                    || slide != null && (t == slide || t.IsChildOf(slide)) || !ShownAtIdle(t, sampler))
                {
                    continue;
                }
                float size = sampler.LocalBounds(t).size.magnitude;
                if (size <= 1e-4f || size >= 0.05f)
                {
                    return t;
                }
            }
            return null;
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

        /// <summary>
        /// The gun's up when its root's up lies along the bore (an under-barrel grenade launcher modelled barrel-up: the HK416-D's
        /// M320, the QBZ192-D's QLG, the PATRIOT GL): the weapon it hangs under gives it, else the first gun axis across the
        /// bore. World up projected was zero for them (their bore is the gun's up), so every frame built on it was degenerate
        /// and the sandbox could not open them (sandbox export 2026-10-17).
        /// </summary>
        private static Vector3 UpAcrossBore(Vector3 aim, Transform root, PoseSampler sampler, RigidPose gunIdle, WeaponRigData d)
        {
            Weapon parent = root.parent != null ? root.parent.GetComponentInParent<Weapon>() : null;
            if (parent != null)
            {
                Vector3 upLocal = root.InverseTransformDirection(parent.transform.up);
                Vector3 fromParent = Vector3.ProjectOnPlane(gunIdle.InverseTransformDirection(sampler.Idle(root).TransformDirection(upLocal)), aim);
                if (fromParent.sqrMagnitude > 0.01f)
                {
                    d.Notes += "up from the weapon it hangs under (its own up is its bore) ";
                    return fromParent.normalized;
                }
            }
            foreach (Vector3 axis in new[] { Vector3.up, Vector3.forward, Vector3.right })
            {
                Vector3 across = Vector3.ProjectOnPlane(axis, aim);
                if (across.sqrMagnitude > 0.01f)
                {
                    d.Warnings.Add("up: the weapon's own up lies along its bore; a gun axis across it is taken");
                    return across.normalized;
                }
            }
            return Vector3.up;
        }

        private static readonly Regex LaserBeamName = new Regex(@"laser|beam", RegexOptions.IgnoreCase);

        /// <summary>
        /// A muzzle its mod put far ahead of the drawn barrel (every FNX-45 a metre out, the PPSh-41 94 cm, the sawed-off
        /// M1878 where the long barrel ended, a PKM's where its parked suppressor would be; 43 of 286 guns more than 5 cm,
        /// sandbox export 2026-10-17): the mod's own uses take the barrel's end instead. They are the gun's length (its swing
        /// and weight, the pistol-or-rifle rule: the FNX-45 was taken for "a long gun in the secondary slot"), how far
        /// along the handguard the support hand may slide (out into the air), the collider's envelope and the reports. The
        /// game still fires from its own muzzle. Only past 15 cm: a flash point a few centimetres ahead is normal.
        /// </summary>
        private static void PullMuzzleOntoBarrel(WeaponRigData d, Transform root, HashSet<Transform> armRelated, PoseSampler sampler, RigidPose gunIdle)
        {
            if (d.Action == ActionType.Melee || d.Action == ActionType.Throwable || d.AimAxis.sqrMagnitude < 0.5f)
            {
                return;
            }
            Vector3 aim = d.AimAxis.normalized;
            // What is drawn at rest on the gun: not the arms, not a laser's beam, not a part parked far away; and only what
            // hangs together with the gun body (the FNX-45's "Sight (1)" waits 27 cm out in front of the pistol: counted, the
            // muzzle stayed 34 cm ahead of the grip and the pistol stayed "a rifle").
            List<Bounds> boxes = sampler.GunSpaceBoxes(root, gunIdle, t => armRelated.Contains(t) || !ShownAtIdle(t, sampler)
                || LaserBeamName.IsMatch(t.name) || (sampler.Idle(t).position - gunIdle.position).sqrMagnitude > 1.5f * 1.5f);
            if (boxes.Count == 0)
            {
                return;
            }
            int seed = 0;
            for (int i = 1; i < boxes.Count; i++)
            {
                if (boxes[i].SqrDistance(Vector3.zero) < boxes[seed].SqrDistance(Vector3.zero))
                {
                    seed = i; // the box at (or nearest) the gun body's origin
                }
            }
            var inGun = new bool[boxes.Count];
            inGun[seed] = true;
            for (bool grew = true; grew;)
            {
                grew = false;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (inGun[i])
                    {
                        continue;
                    }
                    Bounds near = boxes[i];
                    near.Expand(0.04f); // touching within 2 cm
                    for (int j = 0; j < boxes.Count && !inGun[i]; j++)
                    {
                        if (inGun[j] && near.Intersects(boxes[j]))
                        {
                            inGun[i] = grew = true;
                        }
                    }
                }
            }
            float front = float.NegativeInfinity;
            for (int k = 0; k < boxes.Count; k++)
            {
                if (!inGun[k])
                {
                    continue;
                }
                Vector3 lo = boxes[k].min, hi = boxes[k].max;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) == 0 ? lo.x : hi.x, (i & 2) == 0 ? lo.y : hi.y, (i & 4) == 0 ? lo.z : hi.z);
                    front = Mathf.Max(front, Vector3.Dot(corner, aim));
                }
            }
            float ahead = Vector3.Dot(d.MuzzlePoint, aim) - front;
            if (ahead > 0.15f)
            {
                d.MuzzlePoint -= aim * (ahead - 0.01f);
                d.Notes += $"muzzle {ahead * 100f:0} cm ahead of the drawn barrel: taken at the barrel's end ";
            }
            else if (ahead > 0.05f)
            {
                d.Notes += $"muzzle {ahead * 100f:0} cm ahead of the drawn barrel (kept: {boxes.Count} meshes, {System.Array.FindAll(inGun, x => x).Length} on the gun) ";
            }
            else if (ahead < -0.3f && d.Action == ActionType.SingleShot)
            {
                // A one-round launcher whose muzzle transform sits well inside its drawn tube (the RPG-7V [PG-7V] family's at the
                // tube's centre). Its length was the muzzle's, so the cupped-grip rule (AnalyzeHands) took it for a pistol: no foregrip
                // steering, no launcher steadying, the tube held by one hand (Tasks 266).
                d.BarrelFrontAlong = front;
                d.Notes += $"muzzle {-ahead * 100f:0} cm inside the drawn barrel: the barrel's front is its length ";
            }

            // A bore that runs beside the whole drawn gun: no drawn part within a centimetre of it over the 30 cm behind the muzzle.
            // The "Bot Friendly" ARS guns (MP9, HK416, Glock 19) set their muzzle up for a bot's hold, a metre ahead and 5-7 cm to
            // one side; pulled back along the bore above, it kept that side offset, so the bore and the grip estimated from it
            // (they have no arms) ran beside the gun, and the hand held the gun 7-12 cm off its grip (hold_points_audit over the
            // export, task 293). Guns are symmetric about the bore's upright plane: the muzzle is centred across the drawn gun, only
            // when that brings the bore onto it (over the export only those three guns; a sawed-off's muzzle on one of its two
            // barrels, or a laser's tip ahead of the barrel, are never moved: their bores pass through the drawn gun).
            Vector3 upAcross = Vector3.ProjectOnPlane(d.Up, aim);
            if (upAcross.sqrMagnitude > 1e-4f)
            {
                Vector3 across = Vector3.Cross(upAcross.normalized, aim).normalized;
                bool BoreTouches(Vector3 muzzlePoint)
                {
                    for (int s = 0; s <= 12; s++)
                    {
                        Vector3 p = muzzlePoint - aim * (0.3f * s / 12f);
                        foreach (Bounds b in boxes)
                        {
                            if (b.SqrDistance(p) < 0.01f * 0.01f)
                            {
                                return true;
                            }
                        }
                    }
                    return false;
                }
                if (!BoreTouches(d.MuzzlePoint))
                {
                    float lo = float.PositiveInfinity, hi = float.NegativeInfinity;
                    for (int k = 0; k < boxes.Count; k++)
                    {
                        if (!inGun[k])
                        {
                            continue;
                        }
                        Vector3 bmin = boxes[k].min, bmax = boxes[k].max;
                        for (int i = 0; i < 8; i++)
                        {
                            float c = Vector3.Dot(new Vector3((i & 1) == 0 ? bmin.x : bmax.x, (i & 2) == 0 ? bmin.y : bmax.y, (i & 4) == 0 ? bmin.z : bmax.z), across);
                            lo = Mathf.Min(lo, c);
                            hi = Mathf.Max(hi, c);
                        }
                    }
                    float offset = (lo + hi) * 0.5f - Vector3.Dot(d.MuzzlePoint, across);
                    Vector3 centred = d.MuzzlePoint + across * offset;
                    if (lo <= hi && BoreTouches(centred))
                    {
                        d.MuzzlePoint = centred;
                        d.Notes += $"bore {Mathf.Abs(offset) * 100f:0} cm beside the drawn gun: centred across it ";
                    }
                }
            }
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
