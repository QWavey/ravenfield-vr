using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Owns everything weapon-related in VR: which weapon is in the hand, the per-weapon ammo states, the
    /// reload / throw / melee handlers, holster switching, body zones and the procedural mechanical sounds.
    /// </summary>
    public sealed class WeaponSystem : IDisposable
    {
        public static WeaponSystem Instance { get; private set; }

        public readonly VRRig Rig;
        public readonly ProceduralSounds Sounds;
        public readonly BodyZones Zones;
        private readonly ThrowableHandler _throwables;
        private readonly MeleeHandler _melee;
        private readonly Dictionary<Weapon, AmmoState> _ammo = new Dictionary<Weapon, AmmoState>();
        private readonly Dictionary<Weapon, WeaponRig> _rigs = new Dictionary<Weapon, WeaponRig>();

        public HeldWeapon Held { get; private set; }
        private float _lastDryFire;

        public WeaponSystem(VRRig rig)
        {
            Instance = this;
            Rig = rig;
            Sounds = new ProceduralSounds();
            Zones = new BodyZones(rig);
            _throwables = new ThrowableHandler(this);
            _melee = new MeleeHandler(this);
        }

        // ---------------------------------------------------------------------------------------------

        public void Update()
        {
            Zones.Update();
            MagazineProp.Cull();
            VRGameInput.SuppressFire = false;

            Weapon weapon = GameContext.HeldPersonalWeapon;
            if (Held != null && (weapon != Held.Weapon || Held.Weapon == null))
            {
                ReleaseHeld();
            }
            if (Held == null && weapon != null && weapon.gameObject.activeInHierarchy)
            {
                AttachHeld(weapon);
            }

            if (Held != null)
            {
                Held.Update();
                if (Held.Rig.Data.Action == ActionType.Throwable)
                {
                    _throwables.Update(Held);
                }
                UpdateSubWeaponReloadTarget();
            }
            UpdateHolsters();
        }

        public void LateUpdate()
        {
            List<Weapon> dead = null;
            foreach (KeyValuePair<Weapon, AmmoState> kv in _ammo)
            {
                if (kv.Key == null)
                {
                    (dead ??= new List<Weapon>()).Add(kv.Key);
                    continue;
                }
                if (kv.Key.gameObject.activeInHierarchy)
                {
                    kv.Value.Reconcile();
                }
            }
            if (dead != null)
            {
                foreach (Weapon w in dead)
                {
                    _ammo.Remove(w);
                    _rigs.Remove(w);
                }
            }
        }

        public void BeforeRender()
        {
            if (Held != null && Held.Weapon != null)
            {
                Held.BeforeRender();
                if (Held.Rig.Data.Action == ActionType.Melee)
                {
                    _melee.BeforeRender(Held);
                }
            }
            foreach (VRHand h in Rig.Hands)
            {
                if (h.Held is MagazineProp prop && (Held == null || h != Rig.Off))
                {
                    prop.FollowHand();
                    if (!h.GrabHeld)
                    {
                        prop.Drop(h.ThrowVelocity(), h.AngularVelocity);
                    }
                }
            }
        }

        // ---------------------------------------------------------------------------------------------

        private void AttachHeld(Weapon weapon)
        {
            WeaponRig rig = RigFor(weapon);
            AmmoState state = StateFor(weapon, create: true);
            Held = new HeldWeapon(this, weapon, rig, state);
            Log.Debug($"Holding {weapon.name} ({rig.Data.Action})");
        }

        private void ReleaseHeld()
        {
            Log.Guard("ReleaseHeld", () => Held.Release());
            _throwables.Release();
            _melee.Release();
            Held = null;
        }

        /// <summary>
        /// Under-barrel launchers and other sub weapons share the parent model but have their own ammo;
        /// the reload logic then targets the active sub weapon.
        /// </summary>
        private void UpdateSubWeaponReloadTarget()
        {
            Weapon active = Held.Weapon.GetActiveSubWeapon();
            if (active != null && active != Held.Weapon)
            {
                StateFor(active, create: true);
            }
        }

        public WeaponRig RigFor(Weapon weapon)
        {
            if (!_rigs.TryGetValue(weapon, out WeaponRig rig))
            {
                rig = new WeaponRig(weapon, WeaponAnalyzer.Get(weapon));
                _rigs[weapon] = rig;
            }
            return rig;
        }

        public AmmoState StateFor(Weapon weapon, bool create)
        {
            if (weapon == null)
            {
                return null;
            }
            if (_ammo.TryGetValue(weapon, out AmmoState s))
            {
                return s;
            }
            if (!create || !weapon.UserIsPlayer())
            {
                return null;
            }
            s = new AmmoState(weapon, RigFor(weapon).Data);
            _ammo[weapon] = s;
            return s;
        }

        public void OnPlayerShot(Weapon weapon)
        {
            StateFor(weapon, create: true)?.OnShot();
            if (Held != null && (weapon == Held.Weapon || weapon.parentWeapon == Held.Weapon))
            {
                Held.OnShot();
            }
        }

        public void OnDryFire(Weapon weapon)
        {
            // CanFire is polled every frame while the trigger is held; click once per trigger pull.
            if (VRInput.GetPressed(DigitalAction.Fire, VRInput.Dominant) && Time.unscaledTime - _lastDryFire > 0.15f)
            {
                _lastDryFire = Time.unscaledTime;
                Sounds.Play(MechSound.DryFire, Rig.Dominant.Palm.position, 0.8f);
                Rig.Dominant.Haptic(0.01f, 250f, 0.25f);
            }
        }

        public void FlashMessage(string message)
        {
            Held?.FlashCounter(message);
        }

        // ---------------------------------------------------------------------------------------------

        private void UpdateHolsters()
        {
            if (!VRConfig.HolsterSwitching.Value || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen)
            {
                return;
            }
            VRHand dom = Rig.Dominant;
            if (!dom.Tracked || !dom.GrabPressed)
            {
                return;
            }
            int slot = Zones.HolsterAt(dom.Palm.position);
            if (slot < 0)
            {
                return;
            }
            Actor actor = GameContext.PlayerActor;
            if (actor == null || actor.weapons == null || slot >= actor.weapons.Length || actor.weapons[slot] == null)
            {
                dom.Haptic(0.05f, 40f, 0.3f);
                return;
            }
            VRInput.Consume(DigitalAction.Grab, dom.Side);
            if (actor.activeWeapon != actor.weapons[slot])
            {
                actor.SwitchWeapon(slot);
                Sounds.Play(MechSound.Holster, dom.Palm.position, 0.7f);
                dom.Haptic(0.05f, 100f, 0.6f);
            }
        }

        public void OnSceneChanged()
        {
            if (Held != null)
            {
                ReleaseHeld();
            }
            MagazineProp.DestroyAll();
            _ammo.Clear();
            _rigs.Clear();
        }

        public void Dispose()
        {
            if (Held != null)
            {
                ReleaseHeld();
            }
            MagazineProp.DestroyAll();
            Zones.Destroy();
            Sounds.Dispose();
            VRGameInput.SuppressFire = false;
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
