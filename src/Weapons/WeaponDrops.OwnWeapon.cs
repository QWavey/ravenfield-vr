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
    // WeaponDrops, part: the player's own weapon let go of, and given back.
    // The class, the drops' lifetime and teardown: WeaponDrops.cs.
    public sealed partial class WeaponDrops
    {
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
            OutOfTheGround(root.transform, colliders, WeaponSystem.Instance != null && WeaponSystem.Instance.Rig != null && WeaponSystem.Instance.Rig.Head != null
                ? WeaponSystem.Instance.Rig.Head.position : (Vector3?)null);
            PutDownState(w, out bool magOut, out bool slideBack);
            var drop = new DroppedWeapon
            {
                Go = root,
                Colliders = colliders,
                Entry = w.weaponEntry,
                Ammo = RoundsIn(w),
                Spare = Mathf.Max(0, w.spareAmmo),
                SubAmmo = SubAmmoOf(w),
                Key = held.Rig.Data.Key,
                DropTime = Time.time,
                OwnerSlot = slot,
                ReturnAt = Time.time + Mathf.Max(1f, VRConfig.WeaponReturnSeconds.Value),
                OwnedMeshes = baked,
                Semi = FireSelector.IsSemiByPlayer(w),
                PlayerOwned = true,
                MagOut = magOut,
                SlideBack = slideBack,
                EmptyChamber = EmptyChamberOf(w)
            };
            BeltStateOf(w, out drop.CoverOpen, out drop.BeltUnlaid);
            drop.Reach = ReachOf(root.transform, colliders);
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
        private bool _playerWasDead;

        /// <summary>
        /// The player died: the weapons of theirs lying about (kept after a swap, or waiting to go back to a holster) are no longer
        /// the only copies, since the respawn hands out the loadout again. Kept, they stayed beside a full loadout, and one waiting
        /// for its holster was made a kept copy by the first frame on foot (pickup hunt, 2026-10-21). They become ordinary drops,
        /// still grabbable, on the normal timer from now.
        /// </summary>
        private void LetGoOfOwnDrops()
        {
            foreach (DroppedWeapon d in _drops)
            {
                if (d.OwnerSlot >= 0 || d.Keep || d.PlayerOwned)
                {
                    d.OwnerSlot = -1;
                    d.Keep = false;
                    d.PlayerOwned = false;
                    d.DropTime = Time.time;
                }
            }
        }

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
                    // Its slot is taken (you picked something else up into it), so it cannot go back to the holster. It is
                    // still the only copy of that weapon, though, so it is kept like a swapped-away one rather than left to
                    // expire and vanish from both the ground and the loadout 90 s after the drop (85-agent review).
                    d.OwnerSlot = -1;
                    d.Keep = true;
                    d.PlayerOwned = true;
                    continue;
                }
                // Into the holster, not the hand: the slot must not look like the active one while it is spawned. Always, not only
                // with nothing drawn: a stale slot left by an earlier drop, with another gun in the hand, drew this one and holstered
                // the gun in use (2026-10-19).
                int active = actor.activeWeaponSlot;
                actor.activeWeaponSlot = -1;
                Weapon w;
                try
                {
                    w = actor.EquipNewWeaponEntry(d.Entry, d.OwnerSlot, false);
                }
                finally
                {
                    actor.activeWeaponSlot = active;
                }
                if (w == null)
                {
                    // The game made nothing: an ordinary drop the player can pick up, not removed (it was gone, rounds and all) and
                    // not tried again every frame (each try drops what is in that slot).
                    Log.WarnOnce("returnfailed:" + d.Entry.name, $"Returning {d.Entry.name} to its holster: the game spawned nothing; it stays as a drop.");
                    d.OwnerSlot = -1;
                    d.Keep = true;
                    // Still yours, as a slot taken above: unmarked, once no longer kept the cap culled it with the soldiers' drops
                    // instead of after them, and its magazines' rounds did not go back into it (bug hunt, 2026-10-22).
                    d.PlayerOwned = true;
                    continue;
                }
                RestoreAmmo(w, d);
                _sys.Sounds.Play(MechSound.Holster, _sys.Rig.Head.position - Vector3.up * 0.5f, 0.6f);
                Remove(d);
            }
        }
    }
}
