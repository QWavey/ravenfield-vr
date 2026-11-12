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
    // WeaponDrops, part: picking a dropped weapon up, from the ground or into a holster.
    // The class, the drops' lifetime and teardown: WeaponDrops.cs.
    public sealed partial class WeaponDrops
    {
        // ---- picking up -------------------------------------------------------------------------------

        private System.Action _buildWhileIdle;

        public void Update()
        {
            Log.Guard("idle drop builds", _buildWhileIdle ?? (_buildWhileIdle = BuildWhileIdle)); // (one delegate, not one a frame)
            ParkedPartsCache.WriteIfDue();
            BakePendingShape();
            UpdateLateFits();
            Cull();
            PropPhysics.TrackPlayer();
            IgnoreBodies();
            Actor actor = GameContext.PlayerActor;
            bool dead = actor != null && actor.dead;
            if (dead && !_playerWasDead)
            {
                LetGoOfOwnDrops();
            }
            _playerWasDead = dead;
            // (Nor in a match paused with no menu, the dashboard's or the headset's: a grip there swapped weapons in the frozen match,
            // as holsters, grips and props are already kept from; round-6 scout.)
            if (actor == null || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen || GameManager.IsPaused() || _drops.Count == 0)
            {
                return;
            }
            ReturnOwnDrops(actor);
            if (_drops.Count == 0)
            {
                return;
            }
            VRRig rig = _sys.Rig;
            _handPair[0] = rig.Dominant;
            _handPair[1] = rig.Off;
            foreach (VRHand hand in _handPair)
            {
                if (!hand.Tracked)
                {
                    continue;
                }
                bool canGrab = hand.Role == HandRole.Free || hand == rig.Dominant && (hand.Role == HandRole.Weapon || hand.Role == HandRole.Throwable);
                DroppedWeapon near = canGrab ? Nearest(hand.Palm.position) : null;
                int i = (int)hand.Side;
                if (near != null && !_hovering[i])
                {
                    hand.Haptic(0.01f, 160f, 0.2f); // "you can grab this"
                }
                _hovering[i] = near != null;
                if (near != null && (hand.GrabPressed || hand.GrabReaching))
                {
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    PickUp(near, hand, actor);
                    return;
                }
            }
        }


        /// <summary>
        /// A weapon on the ground does not collide with bodies: one spawned in a dying soldier's hand was shoved out of
        /// the ragdoll (or carried along by it) and soldiers walking past kicked lying weapons away, so they drifted off.
        /// Bodies near a drop are found every frame for its first half second, then four times a second.
        /// </summary>
        private void IgnoreBodies()
        {
            float now = Time.time;
            foreach (DroppedWeapon d in _drops)
            {
                if (d.Go == null || d.Colliders == null || now < d.NextBodyCheck)
                {
                    continue;
                }
                d.NextBodyCheck = now - d.DropTime < 0.5f ? now : now + 0.25f;
                // A ragdoll or a soldier whose colliders were switched off and on collides again (Unity forgets ignored
                // pairs then): every two seconds the bodies around are ignored afresh.
                if (now - d.BodiesForgottenAt > 2f)
                {
                    d.BodiesForgottenAt = now;
                    d.IgnoredBodies.Clear();
                }
                PropPhysics.IgnoreBodiesNear(d.Colliders, d.Go.transform.position, 1.5f, d.IgnoredBodies);
                if (d.PlayerGeneration != PropPhysics.PlayerGeneration)
                {
                    d.PlayerGeneration = PropPhysics.PlayerGeneration;
                    PropPhysics.IgnorePlayer(d.Colliders);
                }
            }
        }

        private static PhysicMaterial _dropMaterial;

        /// <summary>Weapons lie where they fall: grippy, barely bouncing.</summary>
        private static void SetUpBody(Rigidbody body, List<Collider> colliders)
        {
            if (_dropMaterial == null)
            {
                _dropMaterial = new PhysicMaterial("RavenfieldVR Dropped Weapon")
                {
                    staticFriction = 0.9f,
                    dynamicFriction = 0.7f,
                    bounciness = 0.05f,
                    frictionCombine = PhysicMaterialCombine.Maximum,
                    bounceCombine = PhysicMaterialCombine.Minimum
                };
            }
            foreach (Collider c in colliders)
            {
                if (c != null)
                {
                    c.sharedMaterial = _dropMaterial;
                }
            }
            body.angularDrag = 0.5f;
        }

        /// <summary>
        /// The rounds in a weapon as it is put down: with its slide locked open the game is told none are loaded (so a fresh
        /// magazine does not run the slide forward), but the magazine in it still holds its rounds. Taken from the game's
        /// count alone, a gun dropped locked open with a fresh magazine came back empty (255.B4-1).
        /// </summary>
        private static int RoundsIn(Weapon w)
        {
            if (w == null)
            {
                return 0;
            }
            AmmoState st = WeaponSystem.Instance != null ? WeaponSystem.Instance.StateFor(w, create: false) : null;
            if (st != null && st.Participates && st.SlideLocked && !st.Chambered)
            {
                return st.MagInserted ? st.MagRounds : 0;
            }
            return Mathf.Max(0, w.ammo);
        }

        /// <summary>
        /// Whether a weapon is put down with its magazine out, and with its slide locked back on nothing chambered (both false
        /// without a rig state, as for a weapon nothing of the mod's has handled).
        /// </summary>
        private static void PutDownState(Weapon w, out bool magOut, out bool slideBack, out bool slideHome)
        {
            AmmoState st = WeaponSystem.Instance != null ? WeaponSystem.Instance.StateFor(w, create: false) : null;
            magOut = st != null && st.UsesDetachableMag && !st.MagInserted;
            slideBack = st != null && st.Participates && st.SlideLocked && !st.Chambered;
            slideHome = st != null && st.HomeOnEmpty;
        }

        /// <summary>A machine gun's feed cover lifted, and a box in with its belt not laid on the tray, as it is put down.</summary>
        private static void BeltStateOf(Weapon w, out bool coverOpen, out bool beltUnlaid)
        {
            AmmoState st = w != null && WeaponSystem.Instance != null ? WeaponSystem.Instance.StateFor(w, create: false) : null;
            bool cover = st != null && st.Participates && st.HasCover;
            coverOpen = cover && st.CoverOpen;
            beltUnlaid = cover && st.MagInserted && !st.BeltLaid && !st.BeltOnlyLifted; // (lifted only, it read as never laid: the counter said LAY BELT over the belt it had)
        }

        /// <summary>Nothing chambered, the action closed and not locked back, rounds left to feed: the next round needs a stroke.</summary>
        private static bool EmptyChamberOf(Weapon w)
        {
            AmmoState st = w != null && WeaponSystem.Instance != null ? WeaponSystem.Instance.StateFor(w, create: false) : null;
            return st != null && st.Participates && st.UsesChamber && st.Rig.Action != ActionType.SingleShot && !st.Chambered && !st.SlideLocked
                   && st.MagRounds > 0 && (st.MagInserted || !st.UsesDetachableMag);
        }

        /// <summary>What each under-barrel weapon has loaded (null without any).</summary>
        private static int[] SubAmmoOf(Weapon w)
        {
            if (w == null || w.alternativeWeapons == null || w.alternativeWeapons.Count == 0)
            {
                return null;
            }
            var ammo = new int[w.alternativeWeapons.Count];
            for (int i = 0; i < ammo.Length; i++)
            {
                Weapon sub = w.alternativeWeapons[i];
                ammo[i] = sub != null ? RoundsIn(sub) : -1;
            }
            return ammo;
        }

        /// <summary>
        /// The rounds a dropped weapon had, back in the weapon made from it: loaded (a chambered round on top of a full
        /// magazine included: clamped to the magazine, a 30+1 rifle came back 29+1 after every drop), the reserve, and
        /// what each under-barrel weapon had loaded (a launcher came back with its default grenade).
        /// </summary>
        private static void RestoreAmmo(Weapon w, DroppedWeapon d)
        {
            if (d.Semi)
            {
                FireSelector.RestoreSemi(w); // picked up or back in its holster: still in semi, as the player left it
            }
            if (d.Ammo >= 0 && w.configuration.ammo > 0)
            {
                int most = w.configuration.ammo + (VRConfig.ChamberPlusOne.Value ? 1 : 0);
                w.ammo = Mathf.Clamp(d.Ammo, 0, most);
            }
            if (d.Spare >= 0 && w.configuration.spareAmmo >= 0)
            {
                w.spareAmmo = d.Spare;
            }
            if (d.SubAmmo != null && w.alternativeWeapons != null)
            {
                for (int i = 0; i < d.SubAmmo.Length && i < w.alternativeWeapons.Count; i++)
                {
                    Weapon sub = w.alternativeWeapons[i];
                    if (sub != null && d.SubAmmo[i] >= 0 && sub.configuration.ammo > 0)
                    {
                        sub.ammo = Mathf.Clamp(d.SubAmmo[i], 0, sub.configuration.ammo + (VRConfig.ChamberPlusOne.Value ? 1 : 0));
                        sub.OnAmmoChanged();
                    }
                }
            }
            w.OnAmmoChanged();
            // Its rig state (chamber, magazine) is taken from the game's count again. A gun put down with its magazine out or
            // locked back gets its state made here and set as it was put down: AdoptWeaponAmmo alone reads a magazine in and a
            // round chambered from the count, so a magazine-out pistol came back with its magazine seated (a fresh one was
            // refused) and a locked-back gun came back with its slide free and a round chambered (30 came back 29+1).
            AmmoState state = WeaponSystem.Instance?.StateFor(w, create: d.MagOut || d.SlideBack || d.SlideHome || d.EmptyChamber || d.CoverOpen || d.BeltUnlaid || d.SpentCases > 0 || d.ActionOpen
                                                                         || d.BoltOpen || d.SpentInChamber);
            state?.AdoptWeaponAmmo();
            if (state != null && (d.SpentCases > 0 || d.ActionOpen))
            {
                state.SpentCases = d.SpentCases;
                state.Open = d.ActionOpen;
                state.Sync();
            }
            if (state != null && d.EmptyChamber && !d.MagOut && !d.SlideBack)
            {
                // Every round back in the magazine or tube, the chamber empty: it is worked before it fires, as it was put down.
                state.Chambered = false;
                state.MagRounds = Mathf.Clamp(d.Ammo, 0, state.Capacity);
                state.Sync();
            }
            if (state != null && (d.MagOut || d.SlideBack))
            {
                state.Chambered = !d.SlideBack && d.Ammo > 0;
                state.MagInserted = !d.MagOut;
                state.MagRounds = d.SlideBack && !d.MagOut ? Mathf.Clamp(d.Ammo, 0, state.Capacity) : 0;
                if (d.SlideBack)
                {
                    state.SlideLocked = true;
                }
                state.Sync();
                Log.InfoOnce("putdown:" + d.Entry.name, $"{d.Entry.name} comes back as it was put down: magazine {(d.MagOut ? "out" : "in")}, slide {(d.SlideBack ? "locked back" : "free")}.");
            }
            if (state != null && (d.BoltOpen || d.SpentInChamber))
            {
                // (After the rest: AdoptWeaponAmmo shut the bolt and took the count's round as chambered.)
                if (d.SpentInChamber)
                {
                    state.Chambered = false;
                    state.SpentInChamber = true;
                }
                state.BoltOpen = d.BoltOpen;
                state.BoltLeftAt = d.BoltLeftAt;
                state.Sync();
            }
            if (state != null && d.SlideHome && !d.SlideBack && state.SlideLocked)
            {
                state.SlideLocked = false; // (AdoptWeaponAmmo catches an empty gun's slide, as for a spawn: this one was put down home)
                state.Sync();
            }
            if (state != null && state.HasCover && (d.CoverOpen || d.BeltUnlaid))
            {
                // Its cover and belt as they were (AdoptWeaponAmmo shuts the cover over a laid belt).
                state.CoverOpen = d.CoverOpen;
                state.BeltLaid = !d.BeltUnlaid; // (no box in: a box put in sets it again)
                state.Sync();
                Log.InfoOnce("putdowncover:" + d.Entry.name, $"{d.Entry.name} comes back as it was put down: cover {(d.CoverOpen ? "open" : "shut")}, belt {(state.BeltLaid ? "laid" : "not laid")}.");
            }
        }

        /// <summary>The cases a gun's chambers still hold and whether its action lies open, as it is put down.</summary>
        private static void ChamberStateOf(Weapon w, out int spent, out bool open)
        {
            AmmoState st = WeaponSystem.Instance?.StateFor(w, create: false);
            spent = st != null ? st.SpentCases : 0;
            open = st != null && st.Open;
        }

        /// <summary>The bolt left open by hand, how far, and a fired case in the chamber, as the gun is put down (see DroppedWeapon.BoltOpen).</summary>
        private static void BoltStateOf(Weapon w, DroppedWeapon d)
        {
            AmmoState st = WeaponSystem.Instance?.StateFor(w, create: false);
            d.BoltOpen = st != null && st.Participates && st.BoltOpen;
            d.BoltLeftAt = st != null ? st.BoltLeftAt : 0f;
            d.SpentInChamber = st != null && st.Participates && st.SpentInChamber;
        }

        /// <summary>
        /// Rounds of a magazine whose weapon was let fall: into that drop's reserve (they come back with it). The player's
        /// own drop of that kind, the newest first.
        /// </summary>
        public void ReturnRoundsToOwnDrop(string key, int rounds)
        {
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                DroppedWeapon d = _drops[i];
                // A bot's drop takes no rounds back; the player's own (a swapped-away rifle is Keep with no owner slot) does.
                if (d.Entry == null || d.Spare < 0 || (d.OwnerSlot < 0 && !d.PlayerOwned))
                {
                    continue;
                }
                bool same = d.Key != null ? d.Key == key : key != null && key.StartsWith(d.Entry.name + "#", System.StringComparison.Ordinal);
                if (!same)
                {
                    continue;
                }
                int cap = d.Entry.prefab != null && d.Entry.prefab.GetComponent<Weapon>() is Weapon proto && proto.configuration.spareAmmo > 0
                    ? proto.configuration.spareAmmo
                    : int.MaxValue;
                d.Spare = Mathf.Max(d.Spare, (int)System.Math.Min(cap, (long)d.Spare + rounds));
                return;
            }
        }

        /// <summary>
        /// A radius about the drop's root that holds every one of its colliders. The colliders are rigidly parented to the
        /// root at unit scale, so the distance from the root to a collider's centre does not change as the drop tumbles; the
        /// world-box extent over-bounds the collider in any orientation. Over-estimating only skips less, never a reachable
        /// drop, so <see cref="Nearest(Vector3)"/> stays exact.
        /// </summary>
        private static float ReachOf(Transform root, List<Collider> colliders)
        {
            float reach = 0f;
            if (colliders != null)
            {
                Vector3 origin = root.position;
                foreach (Collider c in colliders)
                {
                    if (c == null)
                    {
                        continue;
                    }
                    float r = (c.bounds.center - origin).magnitude + c.bounds.extents.magnitude;
                    if (r > reach)
                    {
                        reach = r;
                    }
                }
            }
            return reach;
        }

        private DroppedWeapon Nearest(Vector3 palm) => Nearest(palm, out _);

        /// <summary>A dropped gun within reach, nearer the palm than <paramref name="than"/>: a squeeze there is for the gun.</summary>
        internal bool GunNearer(Vector3 palm, float than) => Nearest(palm, out float distance) != null && distance < than;

        private DroppedWeapon Nearest(Vector3 palm, out float bestDist)
        {
            DroppedWeapon best = null;
            bestDist = PickupReach;
            foreach (DroppedWeapon d in _drops)
            {
                if (d.Go == null || d.Colliders == null || d.Shown)
                {
                    continue;
                }
                if (!d.ReachChecked && Time.time - d.DropTime > 0.1f)
                {
                    // Measured as it was made, from colliders added that frame: boxes not yet set gave a reach too short, and the drop
                    // was skipped below while the hand was at its far end (drops hunt, 2026-10-20). Once more, after the physics step.
                    d.ReachChecked = true;
                    d.Reach = Mathf.Max(d.Reach, ReachOf(d.Go.transform, d.Colliders));
                }
                // One native read for the whole drop first: its root cannot have a collider within bestDist of the palm when
                // the root is farther than bestDist + its reach (every collider lies within reach of the root).
                if (d.Reach > 0f && (d.Go.transform.position - palm).sqrMagnitude > (bestDist + d.Reach) * (bestDist + d.Reach))
                {
                    continue;
                }
                foreach (Collider c in d.Colliders)
                {
                    // Its box first (cheap): the exact closest point only for a collider within reach (every collider of
                    // every drop, for both hands, every frame).
                    if (c == null || c.bounds.SqrDistance(palm) > bestDist * bestDist)
                    {
                        continue;
                    }
                    float dist = Vector3.Distance(c.ClosestPoint(palm), palm);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = d;
                    }
                }
            }
            return best;
        }

        /// <summary>A drop taken into a hand as a squeeze on it takes it (a gun wrested from a soldier: SoldierGuns).</summary>
        internal void TakeInto(DroppedWeapon drop, VRHand hand)
        {
            Actor actor = GameContext.PlayerActor;
            if (drop != null && hand != null && actor != null && !actor.dead)
            {
                PickUp(drop, hand, actor);
            }
        }

        private void PickUp(DroppedWeapon drop, VRHand hand, Actor actor)
        {
            if (!actor.CanUnholsterWeapon())
            {
                hand.Haptic(0.08f, 40f, 0.4f);
                return;
            }
            int slot = drop.OwnerSlot >= 0 && drop.OwnerSlot < actor.weapons.Length ? drop.OwnerSlot : SlotFor(drop.Entry, actor);
            Weapon current = actor.weapons[slot];
            if (current != null && current.weaponEntry != null && current.weaponEntry.prefab != null)
            {
                // Swap: what you had in that slot goes where the picked-up weapon was. Made first: a weapon without a
                // model to copy cannot be put down, so nothing is picked up (it was replaced and gone, rounds and all).
                // At the player's feet, lying flat across the body, a little to the side: spawned 10 cm over the weapon being
                // picked up, it fell onto it and knocked it about while the hand held it (user, 2026-10-19 night, task 308).
                Vector3 bodyForward = Vector3.ProjectOnPlane(_sys.Rig.Head.forward, Vector3.up);
                bodyForward = bodyForward.sqrMagnitude > 1e-4f ? bodyForward.normalized : Vector3.forward;
                Vector3 bodyRight = Vector3.Cross(Vector3.up, bodyForward);
                Vector3 feet = actor.Position() + Vector3.up * 0.15f + bodyRight * 0.2f + bodyForward * 0.15f;
                var pose = new RigidPose(feet, Quaternion.LookRotation(bodyRight, Vector3.up));
                // The free hand's gun mid-reload: the rounds it took for that reload go with it.
                bool reloading = _sys.Second != null && _sys.Second.Weapon == current;
                int inReload = reloading ? _sys.Second.ReloadRounds : 0;
                DroppedWeapon swapped = Spawn(current.weaponEntry, pose, Vector3.one, Vector3.down * 0.3f, RoundsIn(current), Mathf.Max(0, current.spareAmmo) + inReload, keep: true);
                if (swapped == null)
                {
                    hand.Haptic(0.08f, 40f, 0.4f);
                    return;
                }
                if (reloading)
                {
                    _sys.Second.TakeReloadRounds(); // now the drop's
                }
                swapped.SubAmmo = SubAmmoOf(current);
                swapped.Keep = true; // your rifle, swapped for a fallen enemy's shotgun, was deleted 90 s later (85-agent review)
                swapped.PlayerOwned = true;
                swapped.Key = WeaponAnalyzer.KeyOf(current);
                swapped.Semi = FireSelector.IsSemiByPlayer(current);
                PutDownState(current, out bool swapMagOut, out bool swapSlideBack, out bool swapSlideHome);
                swapped.MagOut = swapMagOut;
                if (swapMagOut)
                {
                    HideMagazine(swapped);
                }
                swapped.SlideBack = swapSlideBack;
                swapped.SlideHome = swapSlideHome;
                swapped.EmptyChamber = EmptyChamberOf(current);
                BeltStateOf(current, out swapped.CoverOpen, out swapped.BeltUnlaid);
                ChamberStateOf(current, out swapped.SpentCases, out swapped.ActionOpen);
                BoltStateOf(current, swapped);
                Net.NetGame.OnOwnDrop(swapped, Vector3.zero); // (shown on the other PCs as a gun let go is: a swapped-away one was not; scout)
            }
            // A weapon picked up by the free hand while the gun is held goes beside it when the two pair (primary, secondary,
            // launcher, grenade; never two of a kind): it is put in its slot holstered, then held in this hand (Tasks 152: "pick
            // up the pistol while the main weapon is held"). Anything else is taken up as before, the gun going to its holster.
            // (The slot of the gun really drawn: a map's trigger or a script draws one with EquipNewWeaponEntry, which never sets activeWeaponSlot,
            // and from the stale one a pickup beside it threw away the gun in the hand; pickups scout, 2026-11-08. The game's own equip below reads
            // it too.)
            int heldSlot = _sys.Held != null && _sys.Held.Weapon != null ? System.Array.IndexOf(actor.weapons, _sys.Held.Weapon) : -1;
            if (heldSlot >= 0 && heldSlot != actor.activeWeaponSlot)
            {
                actor.activeWeaponSlot = heldSlot;
            }
            bool beside = hand != _sys.Rig.Dominant && _sys.Second == null && _sys.Held != null && _sys.Held.Weapon != null
                          && slot != actor.activeWeaponSlot;
            Hand gunHandBefore = VRInput.GunHand;
            if (!beside)
            {
                // Into the hand that picked it up: taken with the free hand, it went into the other one.
                _sys.TakeIntoHand(hand);
            }

            Weapon equipped = actor.EquipNewWeaponEntry(drop.Entry, slot, !beside);
            // The slot follows the gun drawn into it, as the game's own switch sets it: the pairing check and the holster reads
            // compare with it (a stale slot swapped the drawn gun out for the second pickup, 2026-10-19).
            if (equipped != null && actor.activeWeapon == equipped)
            {
                actor.activeWeaponSlot = slot;
            }
            if (equipped == null)
            {
                // The game made nothing (its spawn failed): the drop stays where it lies, not deleted with the rounds in it
                // (drops hunt, 2026-10-20).
                Log.WarnOnce("pickupfailed:" + drop.Entry.name, $"Picking up {drop.Entry.name}: the game spawned nothing; it stays on the ground.");
                VRInput.SetGunHand(gunHandBefore); // (the gun held before is taken up again in its own hand, not the one that reached)
                return;
            }
            RestoreAmmo(equipped, drop);
            Remove(drop);
            _sys.Zones.LoadoutChanged(); // (its holster at once: asked earlier this frame, the slot had none until the next)
            if (beside && SecondWeapon.CanPair(_sys.Held.Weapon, equipped))
            {
                _sys.BeginSecond(equipped, hand); // the holster sound and the haptic come with it
            }
            else
            {
                if (beside)
                {
                    _sys.TakeIntoHand(hand);
                    if (equipped != null)
                    {
                        actor.SwitchWeapon(slot); // as a holstered weapon taken with the free hand (WeaponSystem)
                    }
                }
                _sys.Sounds.Play(MechSound.Holster, hand.Palm.position, 0.8f);
                hand.Haptic(0.05f, 100f, 0.7f);
            }
            Log.Debug($"Picked up {drop.Entry.name} into slot {slot + 1}");
        }

        private static bool Kit(Actor actor, int slot) =>
            actor.hasAmmoBox && actor.ammoBoxSlot == slot || actor.hasMedipack && actor.medipackSlot == slot;

        private static int SlotFor(WeaponManager.WeaponEntry entry, Actor actor)
        {
            switch (entry.slot)
            {
                case WeaponManager.WeaponSlot.Primary: return 0;
                case WeaponManager.WeaponSlot.Secondary: return 1;
                // The launcher's slot is 3 (the loadout's gear2, SecondWeapon.Kind); 4 is the loadout's gear3, a small gun's.
                case WeaponManager.WeaponSlot.LargeGear: return 3;
                default:
                    if (actor.weapons[2] == null) return 2;
                    if (actor.weapons[4] == null) return 4;
                    // Both taken: not the ammo bag or medic kit, which the swap laid on the floor in the gun's place (pickups scout, 2026-11-08).
                    return Kit(actor, 2) && !Kit(actor, 4) ? 4 : 2;
            }
        }
    }
}
