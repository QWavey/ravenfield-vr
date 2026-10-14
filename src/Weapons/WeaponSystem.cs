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
        public readonly WeaponDrops Drops;
        private readonly PouchDisplay _pouch;

        /// <summary>Self-test: the pouch as shown (where its magazine stands).</summary>
        internal PouchDisplay Pouch => _pouch;
        private readonly HolsterDisplay _holsters;
        private readonly MagReadout _readout;
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
            Drops = new WeaponDrops(this);
            _pouch = new PouchDisplay(this);
            _holsters = new HolsterDisplay(this);
            _readout = new MagReadout(this);
            if (Core.AutoTest.Requested)
            {
                Camera.onPreCull += CountFlatFrames;
            }
        }

        /// <summary>
        /// Self-test: frames the headset drew with the player's weapon not yet handled by the mod (where the flat game
        /// holds it) or with the weapon's own arms showing (only the gloves should).
        /// </summary>
        internal static int UnhandledFrames, ArmFrames;

        private void CountFlatFrames(Camera cam)
        {
            if (cam != Rig.Camera)
            {
                return;
            }
            Weapon w = GameContext.HeldPersonalWeapon;
            if (w == null || GameContext.GameMenuOpen)
            {
                return;
            }
            if (Held == null || Held.Weapon != w)
            {
                if (!_attachFailed.Contains(w))
                {
                    UnhandledFrames++;
                }
                return;
            }
            if (Held.ArmsShownByMod)
            {
                return; // the weapon's own arms are the chosen hands
            }
            foreach (Renderer r in Held.Rig.ArmRenderers)
            {
                if (r != null && r.enabled && r.gameObject.activeInHierarchy)
                {
                    ArmFrames++;
                    return;
                }
            }
        }

        // ---------------------------------------------------------------------------------------------

        public void Update()
        {
            Zones.Update();
            MagazineProp.Cull();
            Casings.Cull();
            GrenadeDud.Cull();
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
            UpdateLooseProps();
            Drops.Update();
            FireSelector.Update();
            UpdateDrivingWeapons();
            UpdateHolsters();
            UpdateGripToHold();
        }

        /// <summary>
        /// A free hand picks up any magazine, shell or rocket lying within reach, whatever weapon it belongs to (the
        /// held weapon's reload logic decides for the off hand while a weapon is drawn).
        /// </summary>
        private void UpdateLooseProps()
        {
            if (GameContext.GameMenuOpen)
            {
                return;
            }
            // A drawn gun's reload logic owns the off hand; a grenade in hand leaves it free for picking things up.
            bool offManaged = Held != null && Held.Rig.Data.Action != ActionType.Throwable;
            foreach (VRHand h in Rig.Hands)
            {
                if (!h.Tracked || h.Role != HandRole.Free || !(h.GrabPressed || h.GrabReaching) || offManaged && h == Rig.Off)
                {
                    continue;
                }
                Vector3 palm = h.Palm.position;
                MagazineProp prop = MagazineProp.NearestDropped(palm, 0.13f, null);
                GrenadeDud dud = GrenadeDud.Nearest(palm, 0.12f);
                if (dud != null && (prop == null || dud.Distance(palm) < prop.DistanceTo(palm)))
                {
                    VRInput.Consume(DigitalAction.Grab, h.Side);
                    if (dud.Refund(GameContext.PlayerActor))
                    {
                        Sounds.Play(MechSound.Pouch, palm, 0.6f);
                        h.Haptic(0.03f, 140f, 0.45f);
                    }
                    else
                    {
                        FlashMessage("FULL");
                        h.Haptic(0.12f, 35f, 0.6f);
                    }
                    continue;
                }
                if (prop == null)
                {
                    continue;
                }
                VRInput.Consume(DigitalAction.Grab, h.Side);
                prop.Attach(h);
                prop.FollowHand();
                h.Haptic(0.03f, 150f, 0.5f);
            }
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
            // A weapon drawn after this frame's Update (the game switches weapons in its own Update or LateUpdate) is
            // taken over now, before it is drawn: otherwise it showed for a frame where the flat game holds it, in the
            // weapon's own arms.
            Weapon now = GameContext.HeldPersonalWeapon;
            if (Held != null && (now != Held.Weapon || Held.Weapon == null))
            {
                ReleaseHeld();
            }
            if (Held == null && now != null && now.gameObject.activeInHierarchy)
            {
                AttachHeld(now);
                Held?.Update();
            }
            _throwables.BeforeRender();
            Zones.ComputeSpots(); // from this frame's head pose: what hangs on the body must not trail the head
            _pouch.BeforeRender(Held != null && Held.Weapon != null ? Held : null);
            _holsters.BeforeRender();
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
            _readout.BeforeRender(); // after every magazine has its pose for this frame
        }

        // ---------------------------------------------------------------------------------------------

        // Weapons whose VR handling could not be set up: not retried every frame (the game keeps handling them).
        private readonly HashSet<Weapon> _attachFailed = new HashSet<Weapon>();

        private void AttachHeld(Weapon weapon)
        {
            if (_attachFailed.Contains(weapon))
            {
                return;
            }
            try
            {
                WeaponRig rig = RigFor(weapon);
                AmmoState state = StateFor(weapon, create: true);
                Held = new HeldWeapon(this, weapon, rig, state);
                Log.Debug($"Holding {weapon.name} ({rig.Data.Action})");
            }
            catch (Exception e)
            {
                Held = null;
                _attachFailed.Add(weapon);
                _attachFailed.RemoveWhere(w => w == null);
                Log.Exception($"Setting up {weapon.name} for VR (it keeps its normal behaviour)", e);
            }
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
            AmmoState state = StateFor(weapon, create: true);
            state?.OnShot();
            FireSelector.OnShot(weapon);
            bool held = Held != null && (weapon == Held.Weapon || weapon.parentWeapon == Held.Weapon);
            if (held && state != null && state.Rig.CyclesByHand && VRConfig.Reload.Value == ReloadMode.Manual)
            {
                // Bolt / pump / lever: the fire animation would cycle the action (and play its sounds) by itself.
                // Cancel it so the action only moves, and only sounds, when the player works it; keep the
                // mod's gunshot sounds that were tied to that animation.
                Animator anim = weapon.animator;
                if (anim != null && anim.isActiveAndEnabled)
                {
                    anim.ResetTrigger(Weapon.FIRE_PARAMETER_HASH);
                }
                Held.Sounds.PlayShotCues();
            }
            if (held)
            {
                Held.OnShot(weapon);
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

        /// <summary>Autotest: the grenade pin and dud checks for the throwable in hand.</summary>
        internal System.Collections.IEnumerator ThrowableSelfCheck(HeldWeapon held, Action<string> report) => _throwables.SelfCheck(held, report);

        public void FlashMessage(string message)
        {
            Held?.FlashCounter(message);
        }

        /// <summary>
        /// Fire-mode button on foot. In order: the weapon's own fire-mode script (it switches with its own animation
        /// and sound when it sees its key), the mod's sub-weapons (the game cycles them), then our semi/auto
        /// selector for automatic weapons. Returns false to hand the press to the game.
        /// </summary>
        public bool HandleFireModePress(Weapon weapon)
        {
            if (weapon == null)
            {
                return false;
            }
            ScriptKey scripted = ScriptKeys.Find(weapon, ScriptKeyPurpose.FireMode);
            if (scripted != null)
            {
                ScriptKeys.Tap(scripted.Key, scripted.Script);
                Rig.Dominant.Haptic(0.02f, 200f, 0.5f);
                return true;
            }
            if (weapon.HasAnySubWeapons())
            {
                return false;
            }
            string mode = FireSelector.Toggle(weapon);
            if (mode == null)
            {
                FlashMessage("SEMI ONLY");
                Rig.Dominant.Haptic(0.02f, 60f, 0.3f);
                return true;
            }
            FlashMessage(mode);
            Sounds.Play(MechSound.DryFire, Rig.Dominant.Palm.position, 0.5f);
            Rig.Dominant.Haptic(0.02f, 200f, 0.5f);
            return true;
        }

        // ---------------------------------------------------------------------------------------------

        /// <summary>Driving a car: a holster grab draws that weapon (put it back to drive with the horn again).</summary>
        private void UpdateDrivingWeapons()
        {
            Actor actor = GameContext.PlayerActor;
            if (!DrivingWeapons.CanDraw(actor))
            {
                if (DrivingWeapons.Armed)
                {
                    DrivingWeapons.Reset();
                }
                return;
            }
            VRHand dom = Rig.Dominant;
            if (!dom.Tracked || !dom.GrabPressed || dom.Role != HandRole.Free && dom.Role != HandRole.Weapon || GameContext.GameMenuOpen)
            {
                return;
            }
            int slot = Zones.HolsterAt(dom.Palm.position);
            if (slot < 0 || actor.weapons == null || slot >= actor.weapons.Length)
            {
                return;
            }
            Weapon weapon = actor.weapons[slot];
            if (weapon == null)
            {
                dom.Haptic(0.05f, 40f, 0.3f);
                return;
            }
            VRInput.Consume(DigitalAction.Grab, dom.Side);
            if (DrivingWeapons.Armed && actor.activeWeapon == weapon)
            {
                ReleaseHeld();
                DrivingWeapons.PutAway(actor);
                dom.Role = HandRole.Free;
            }
            else
            {
                DrivingWeapons.Draw(actor, weapon);
            }
            Sounds.Play(MechSound.Holster, dom.Palm.position, 0.7f);
            dom.Haptic(0.05f, 100f, 0.6f);
        }

        private int _hoveredHolster = -1;

        private void UpdateHolsters()
        {
            if (!VRConfig.HolsterSwitching.Value || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen)
            {
                return;
            }
            VRHand dom = Rig.Dominant;
            if (!dom.Tracked || dom.Role == HandRole.Magazine)
            {
                _hoveredHolster = -1; // a hand holding a magazine has no room for a weapon
                return;
            }
            Actor actor = GameContext.PlayerActor;
            int slot = Zones.HolsterAt(dom.Palm.position, includeChest: Held == null || !Held.Aiming);
            // A short tick when the hand reaches a holster with something in it: holsters are found by feel.
            bool usable = slot >= 0 && actor != null && actor.weapons != null && slot < actor.weapons.Length && actor.weapons[slot] != null;
            if (usable && slot != _hoveredHolster)
            {
                dom.Haptic(0.012f, 220f, 0.35f);
            }
            _hoveredHolster = usable ? slot : -1;
            if (!dom.GrabPressed || slot < 0)
            {
                return;
            }
            if (actor == null || actor.weapons == null || slot >= actor.weapons.Length || actor.weapons[slot] == null)
            {
                dom.Haptic(0.05f, 40f, 0.3f);
                return;
            }
            VRInput.Consume(DigitalAction.Grab, dom.Side);
            if (actor.activeWeapon == actor.weapons[slot])
            {
                // Putting the weapon back where it lives: holster it and free the hand.
                HolsterToEmptyHand(actor);
                return;
            }
            actor.SwitchWeapon(slot);
            Sounds.Play(MechSound.Holster, dom.Palm.position, 0.7f);
            dom.Haptic(0.05f, 100f, 0.6f);
        }

        private void HolsterToEmptyHand(Actor actor)
        {
            if (actor.activeWeapon == null || actor.IsSeated())
            {
                return;
            }
            actor.HolsterActiveWeapon();
            VRHand dom = Rig.Dominant;
            if (dom.Role == HandRole.Weapon || dom.Role == HandRole.Throwable)
            {
                dom.Role = HandRole.Free;
            }
            Sounds.Play(MechSound.Holster, dom.Palm.position, 0.7f);
            dom.Haptic(0.05f, 100f, 0.5f);
        }

        private float _gripLetGoAt = -1f;
        private Vector3 _letGoVelocity, _letGoAngular, _letGoPalm;

        /// <summary>
        /// GripToHoldWeapon: letting go of the grip puts the weapon away and frees the hand. A loosened grip has a moment
        /// to close again before the weapon goes (a shift of the fingers must not drop it mid-fight).
        /// </summary>
        private void UpdateGripToHold()
        {
            if (!VRConfig.GripToHoldWeapon.Value || Held == null || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen)
            {
                _gripLetGoAt = -1f;
                return;
            }
            VRHand dom = Rig.Dominant;
            if (!dom.Tracked)
            {
                _gripLetGoAt = -1f; // out of view: never drop on a tracking gap
                return;
            }
            if (Held.Rig.Data.Action == ActionType.Throwable && Time.unscaledTime - _throwables.GripThrowAt < 0.5f)
            {
                _gripLetGoAt = -1f; // that release was a throw
                return;
            }
            if (dom.GrabReleased)
            {
                _gripLetGoAt = Time.unscaledTime;
                // The swing as the hand opens: 0.12 s later the hand has stopped and a throw would just drop.
                _letGoVelocity = dom.ThrowVelocity();
                _letGoAngular = dom.AngularVelocity;
                _letGoPalm = dom.Palm.position;
            }
            else if (dom.GrabHeld)
            {
                _gripLetGoAt = -1f;
            }
            if (_gripLetGoAt >= 0f && Time.unscaledTime - _gripLetGoAt > 0.12f)
            {
                _gripLetGoAt = -1f;
                Actor actor = GameContext.PlayerActor;
                if (actor == null)
                {
                    return;
                }
                // Let go at your body: back in its holster. Anywhere else it falls, like any object you open your hand on.
                if (VRConfig.ReleaseDropsWeapon.Value && !Zones.AtBody(_letGoPalm) && Held.Rig.Data.Action != ActionType.Throwable)
                {
                    LetFall(actor, dom, _letGoVelocity, _letGoAngular);
                }
                else
                {
                    HolsterToEmptyHand(actor);
                }
            }
        }

        /// <summary>Autotest: lets the held weapon fall as a released grip would; the slot it came from, or -1.</summary>
        internal int TestLetFall()
        {
            Actor actor = GameContext.PlayerActor;
            if (Held == null || actor == null || actor.weapons == null)
            {
                return -1;
            }
            int slot = Array.IndexOf(actor.weapons, Held.Weapon);
            LetFall(actor, Rig.Dominant, Rig.Dominant.ThrowVelocity(), Rig.Dominant.AngularVelocity);
            return slot;
        }

        /// <summary>The weapon leaves the hand as a physical object where it was held; it returns to its holster later.</summary>
        private void LetFall(Actor actor, VRHand dom, Vector3 velocity, Vector3 angularVelocity)
        {
            HeldWeapon held = Held;
            int slot = actor.weapons != null ? Array.IndexOf(actor.weapons, held.Weapon) : -1;
            if (slot < 0 || actor.IsSeated())
            {
                HolsterToEmptyHand(actor);
                return;
            }
            DroppedWeapon drop = Drops.DropHeld(held, slot, velocity, angularVelocity);
            if (drop == null)
            {
                HolsterToEmptyHand(actor);
                return;
            }
            ReleaseHeld();
            actor.HolsterActiveWeapon();
            actor.DropWeapon(slot);
            if (dom.Role == HandRole.Weapon || dom.Role == HandRole.Throwable)
            {
                dom.Role = HandRole.Free;
            }
            dom.Haptic(0.03f, 90f, 0.4f);
        }

        public void OnSceneChanged()
        {
            if (Held != null)
            {
                ReleaseHeld();
            }
            MagazineProp.DestroyAll();
            GrenadeDud.DestroyAll();
            Casings.DestroyAll();
            Drops.Clear();
            FireSelector.Clear();
            _ammo.Clear();
            _rigs.Clear();
        }

        public void Dispose()
        {
            Camera.onPreCull -= CountFlatFrames;
            if (Held != null)
            {
                ReleaseHeld();
            }
            MagazineProp.DestroyAll();
            GrenadeDud.DestroyAll();
            Casings.DestroyAll();
            Drops.Dispose();
            _pouch.Destroy();
            _holsters.Destroy();
            _readout.Destroy();
            FireSelector.Clear();
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
