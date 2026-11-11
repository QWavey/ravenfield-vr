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
        /// <summary>
        /// The player's own weapon put down in a swap (or whose holster slot was taken): the only copy of it, not culled by
        /// lifetime. Past six of them the oldest becomes an ordinary drop with a fresh lifetime (Cull).
        /// </summary>
        public bool Keep;
        /// <summary>
        /// The player's own weapon (put down in a swap, or let go of): its rounds come back to it even when no live owner is left
        /// (a loose magazine of a swapped-away rifle lost its rounds when the rifle was skipped here; 2026-10-19).
        /// </summary>
        public bool PlayerOwned;
        /// <summary>Another PC's player's own gun, shown where he let go of it in a shared match: not to be picked up here (it is his).</summary>
        public bool Shown;
        /// <summary>In a shared match, the number this PC's own dropped gun is told by (0: not told).</summary>
        public int NetId;
        /// <summary>Put down with its magazine out: it comes back without one (the rounds in that magazine are the magazine's).</summary>
        public bool MagOut;
        /// <summary>Put down locked back on nothing chambered: it comes back caught back, with its magazine's rounds loaded.</summary>
        public bool SlideBack;
        /// <summary>
        /// Put down with nothing chambered and its action closed (a pump or bolt gun fired and not worked again): it comes back
        /// still to be worked. The count alone made it "N+1" and its next trigger pull fired with no stroke (reload hunt, 2026-10-20).
        /// </summary>
        public bool EmptyChamber;
        /// <summary>
        /// A machine gun put down with its feed cover lifted, and with a box in whose belt was not laid: it comes back so.
        /// Taken up again it came back shut over a laid belt (its state made anew) and fired without the belt reload (bug
        /// hunt, 2026-10-22).
        /// </summary>
        public bool CoverOpen, BeltUnlaid;
        /// <summary>A break action or revolver put down: its fired cases not yet thrown out, and whether it lay open. They came back gone, and it came back shut.</summary>
        public int SpentCases;
        public bool ActionOpen;
        /// <summary>Radius from the root that holds every collider (rotation-invariant), so a far drop is skipped whole.</summary>
        public float Reach;
        /// <summary>Reach measured again after its first physics step (the colliders' boxes may not be set the frame they are added).</summary>
        public bool ReachChecked;
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
        /// <summary>The player had set it to semi (FireSelector): it comes back in semi.</summary>
        public bool Semi;
        /// <summary>Meshes baked for this copy (skinned parts), destroyed with it.</summary>
        public List<Mesh> OwnedMeshes;
        /// <summary>Body colliders (ragdolls, soldiers' hitboxes) this weapon no longer collides with.</summary>
        public readonly HashSet<Collider> IgnoredBodies = new HashSet<Collider>();
        public float NextBodyCheck;
        /// <summary>When <see cref="IgnoredBodies"/> was last forgotten (bodies switched off and on collide again: redone).</summary>
        public float BodiesForgottenAt;
        /// <summary>The <see cref="PropPhysics.PlayerGeneration"/> the player's colliders were last ignored for.</summary>
        public int PlayerGeneration = -1;
        /// <summary>
        /// The shape key of a model whose shape was never fitted: it lies on a box from its drawn bounds until the fit, made on a
        /// worker thread after the death frame, replaces it (null once it has its own shape; task 309).
        /// </summary>
        public string LateFitKey;
    }

    /// <summary>
    /// Fallen soldiers drop their weapon as a physical third-person model. Gripping a dropped weapon equips it
    /// into its loadout slot; the weapon you had in that slot is dropped in its place (with its ammo), so you can
    /// swap back. Models are inert copies of the weapon prefab (no mod scripts run). Drops expire and are capped.
    /// </summary>
    public sealed partial class WeaponDrops : IDisposable
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
                // A kept weapon that fell out of the world can never be picked up again: it only held a place among the kept ones
                // (pickup hunt, 2026-10-21).
                if (expired && (!d.Keep || d.Go.transform.position.y < -500f))
                {
                    Remove(d);
                }
            }
            // Weapons kept after a swap pile up only so far: past six, the oldest of them stops being kept. It is the only
            // copy of that weapon, so it is not deleted outright (that lost the first swapped-away gun of a long fight on the
            // seventh swap); it becomes an ordinary drop with a fresh lifetime, still grabbable, and goes on the normal timer.
            int kept = 0;
            foreach (DroppedWeapon d in _drops)
            {
                kept += d.Keep ? 1 : 0;
            }
            for (int i = 0; i < _drops.Count && kept > 6; i++)
            {
                if (_drops[i].Keep && _drops[i].OwnerSlot < 0)
                {
                    _drops[i].Keep = false;
                    _drops[i].DropTime = Time.time;
                    kept--;
                }
            }
            // Oldest first when over the cap; your own weapon waiting to go back to its holster is never one of them. The soldiers'
            // drops go before a weapon of yours that stopped being kept: just demoted above, it was the oldest in the list and
            // went in the same call, the only copy of a swapped-away gun lost on the seventh swap (pickup hunt, 2026-10-21).
            int count = 0;
            foreach (DroppedWeapon d in _drops)
            {
                count += d.OwnerSlot < 0 && !d.Keep ? 1 : 0;
            }
            for (int pass = 0; pass < 2 && count > max; pass++)
            {
                for (int i = 0; i < _drops.Count && count > max; i++)
                {
                    DroppedWeapon d = _drops[i];
                    if (d.OwnerSlot < 0 && !d.Keep && (pass == 1 || !d.PlayerOwned))
                    {
                        Remove(d);
                        i--;
                        count--;
                    }
                }
            }
        }

        /// <summary>
        /// Another PC's player's own gun shown where he let go of it (a shared match): a copy of its model, not to be picked up here, taken
        /// away again when he picks it up (<see cref="RemoveShown"/>). Null when this PC lacks the weapon.
        /// </summary>
        internal DroppedWeapon Show(WeaponManager.WeaponEntry entry, RigidPose pose, Vector3 velocity)
        {
            if (entry == null || entry.prefab == null)
            {
                return null;
            }
            DroppedWeapon d = Spawn(entry, pose, DropScale(Vector3.one), velocity, 0, 0, keep: true);
            if (d != null)
            {
                d.Shown = true;
            }
            return d;
        }

        /// <summary>A shown copy taken away (its owner picked his gun up again, or left).</summary>
        internal void RemoveShown(DroppedWeapon d)
        {
            if (d != null && d.Shown && _drops.Contains(d))
            {
                Remove(d);
            }
        }

        private void Remove(DroppedWeapon d)
        {
            _drops.Remove(d);
            if (d.NetId > 0)
            {
                Net.NetGame.OnOwnDropGone(d.NetId); // (picked up again, back to its holster, or gone: the others take theirs away)
            }
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

        public void Clear()
        {
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                Remove(_drops[i]);
            }
        }

        public void Dispose()
        {
            ParkedPartsCache.Write();
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
