using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>Hooks that let the VR ammo model and sight-to-eye aiming drive the game's weapons.</summary>
    internal static class WeaponHooks
    {
        /// <summary>
        /// A weapon the player carries, in VR. Seat-mounted guns (vehicle LMGs, turrets) are left to the game: they
        /// reload, aim and show their sights as designed.
        /// </summary>
        public static bool PlayerWeaponInVR(Weapon w) =>
            VRManager.Active && WeaponSystem.Instance != null && w != null && w.UserIsPlayer() && !w.IsMountedWeapon();
    }

    /// <summary>
    /// In VR you spawn with empty hands (SpawnEmptyHanded): the game draws your first weapon as you spawn, and the hand
    /// took it at once, so you started every life holding the primary whether you reached for it or not. It goes back to
    /// its holster before the hand takes it; reach for a holster to draw.
    /// </summary>
    [HarmonyPatch(typeof(Actor), nameof(Actor.SpawnAt))]
    internal static class ActorSpawnEmptyHandedPatch
    {
        private static void Postfix(Actor __instance)
        {
            if (!VRManager.Active || __instance == null || __instance.aiControlled || !VRConfig.SpawnEmptyHanded.Value
                || !VRConfig.HolsterSwitching.Value || !ReferenceEquals(__instance, LocalPlayer.actor))
            {
                return;
            }
            try
            {
                if (__instance.activeWeapon != null && !__instance.IsSeated())
                {
                    __instance.HolsterActiveWeapon();
                }
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("Actor.SpawnAt (VR, empty hands)", e); // the spawn goes on, weapon in hand
            }
        }
    }

    /// <summary>
    /// The game's laser sight, switched on for good (forceOn), reads its weapon's user without checking there is one: on a
    /// weapon without a user (a copy being measured, a weapon put down) it threw every frame (an MK18 RIII's). Such a
    /// laser stays off; so does one whose mod left out its beam or dot.
    /// </summary>
    [HarmonyPatch(typeof(LaserSight), "LateUpdate")]
    internal static class LaserSightWithoutUserPatch
    {
        private static bool Prefix(LaserSight __instance)
        {
            bool broken = __instance.beamTransform == null || __instance.dotObject == null;
            if (!broken && !(__instance.forceOn && __instance.weapon != null && __instance.weapon.user == null))
            {
                return true;
            }
            if (__instance.beamTransform != null)
            {
                __instance.beamTransform.localScale = new Vector3(1f, 1f, 0f);
            }
            if (__instance.dotObject != null && __instance.dotObject.activeSelf)
            {
                __instance.dotObject.SetActive(false);
            }
            return false;
        }
    }

    /// <summary>
    /// In VR the weapon is drawn by your hand, not by an animation: the draw clip (raising the gun, often working the
    /// bolt or slide on the way) is skipped by stepping the animator past it with its events muted. The game's own
    /// draw delay before the weapon can fire runs on a separate timer and stays as it is.
    /// The animator is stepped in short increments because the state machine takes one transition per update
    /// (trigger -> draw state, then draw -> idle on exit time), and once more on the next frame because an animator
    /// enabled this frame only initialises in the coming animation pass.
    /// </summary>
    [HarmonyPatch(typeof(Weapon), nameof(Weapon.Unholster))]
    internal static class WeaponUnholsterPatch
    {
        private const int Steps = 25;
        private const float StepSeconds = 0.1f;

        private static void Postfix(Weapon __instance)
        {
            if (!VRConfig.SkipDrawAnimation.Value || !WeaponHooks.PlayerWeaponInVR(__instance))
            {
                return;
            }
            Animator anim = __instance.animator;
            if (anim == null || anim.runtimeAnimatorController == null)
            {
                return;
            }
            try
            {
                if (anim.isActiveAndEnabled && anim.isInitialized)
                {
                    Step(anim);
                }
                if (VRManager.Instance != null)
                {
                    VRManager.Instance.StartCoroutine(StepNextFrame(__instance, anim));
                }
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("Weapon.Unholster (VR)", e); // the draw goes on, with its animation
            }
        }

        private static IEnumerator StepNextFrame(Weapon weapon, Animator anim)
        {
            yield return null; // runs after this frame's Update calls, before its animation pass
            // The free hand's second weapon as well: it played its whole draw clip in the hand (its shape could not be measured
            // until the game's draw time ran out, and the glove stayed open beside it; user, 2026-10-21: "the 2nd object
            // sometimes isnt in the hand and floats in the air").
            SecondWeapon second = WeaponSystem.Instance != null ? WeaponSystem.Instance.Second : null;
            if (weapon != null && anim != null && anim.isActiveAndEnabled && weapon.user != null
                && (weapon.user.activeWeapon == weapon || second != null && second.Weapon == weapon))
            {
                Step(anim);
            }
        }

        private static void Step(Animator anim)
        {
            bool events = anim.fireEvents;
            try
            {
                anim.fireEvents = false;
                for (int i = 0; i < Steps; i++)
                {
                    anim.Update(StepSeconds);
                }
            }
            finally
            {
                anim.fireEvents = events;
            }
        }
    }

    /// <summary>
    /// A fire-mode switch the game makes by cycling a weapon's variants (semi / auto copies, sub weapons) holds the trigger
    /// back for the weapon's changeFireModeTime (0.3 s by default, more on some), the length of the flat game's selector
    /// animation. In VR that animation is not shown, and the wait was felt as a lag on every switch to auto or semi (user,
    /// 2026-10-21). The player's own switch is ready after a twentieth of a second.
    /// </summary>
    [HarmonyPatch(typeof(Weapon), nameof(Weapon.UnholsterSubWeapon))]
    internal static class WeaponFireModeSwitchPatch
    {
        private const float Ready = 0.05f;

        private static void Postfix(Weapon __instance, bool switchedFromSubWeapon)
        {
            // (Not a weapon whose fire-mode change is a reload: UnholsterDone finishes that reload, and early it refilled at once.)
            if (!switchedFromSubWeapon || !WeaponHooks.PlayerWeaponInVR(__instance) || __instance.configuration == null
                || __instance.configuration.changeFireModeTime <= Ready || __instance.configuration.changeFireModeIsReload)
            {
                return;
            }
            __instance.CancelInvoke("UnholsterDone");
            __instance.Invoke("UnholsterDone", Ready);
        }
    }

    [HarmonyPatch(typeof(Weapon), nameof(Weapon.CanFire))]
    internal static class WeaponCanFirePatch
    {
        private static void Postfix(Weapon __instance, ref bool __result)
        {
            if (!__result || !WeaponHooks.PlayerWeaponInVR(__instance) || VRConfig.ReloadNow != ReloadMode.Manual)
            {
                return;
            }
            try
            {
                AmmoState state = WeaponSystem.Instance.StateFor(__instance, create: false);
                if (state != null && !state.CanFire)
                {
                    __result = false;
                    WeaponSystem.Instance.OnDryFire(__instance);
                }
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("Weapon.CanFire (VR)", e); // never into the game's own trigger code
            }
        }
    }

    [HarmonyPatch(typeof(Weapon), nameof(Weapon.Shoot))]
    internal static class WeaponShootPatch
    {
        private static Weapon _firedBy;
        private static Transform _firedMuzzle;

        /// <summary>
        /// The muzzle <paramref name="w"/>'s last shot left from: Shoot fires the current muzzle and then moves on to the next
        /// barrel (NextMuzzle), so after it CurrentMuzzle names the barrel that has not fired yet. Null when not recorded.
        /// </summary>
        internal static Transform FiredMuzzleOf(Weapon w) => w != null && ReferenceEquals(w, _firedBy) ? _firedMuzzle : null;

        private static void Prefix(Weapon __instance)
        {
            if (!WeaponHooks.PlayerWeaponInVR(__instance))
            {
                return;
            }
            try
            {
                _firedBy = __instance;
                _firedMuzzle = __instance.CurrentMuzzle();
            }
            catch (System.Exception e)
            {
                _firedBy = null;
                Log.ExceptionOnce("Weapon.Shoot prefix (VR)", e); // never into the game's own shot
            }
        }

        private static void Postfix(Weapon __instance, bool __result)
        {
            // Not a grenade let go or a melee swing (both run Shoot too): they got a gunshot's kick, view jolt and buzz.
            if (__result && !(__instance is ThrowableWeapon) && !(__instance is MeleeWeapon) && WeaponHooks.PlayerWeaponInVR(__instance))
            {
                try
                {
                    WeaponSystem.Instance.OnPlayerShot(__instance);
                }
                catch (System.Exception e)
                {
                    // Recoil, ammo, cases: a mod error is logged once and the game's own shot goes on.
                    Log.ExceptionOnce("Weapon.Shoot (VR)", e);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Weapon), nameof(Weapon.Reload))]
    internal static class WeaponReloadPatch
    {
        private static bool Prefix(Weapon __instance)
        {
            if (!WeaponHooks.PlayerWeaponInVR(__instance) || VRConfig.ReloadNow != ReloadMode.Manual)
            {
                return true;
            }
            try
            {
                AmmoState state = WeaponSystem.Instance.StateFor(__instance.GetActiveSubWeapon(), create: true);
                // Weapons without a manual model (infinite ammo, tools) keep the normal reload.
                return state == null || !state.Participates;
            }
            catch (System.Exception e)
            {
                // As the patches round it: a mod error (making the rig or the ammo state) must not throw out of the game's own
                // reload, which then did nothing at every press; it reloads the game's way instead (bug hunt, 2026-10-22).
                Log.ExceptionOnce("Weapon.Reload (VR)", e);
                return true;
            }
        }
    }

    [HarmonyPatch(typeof(Weapon), nameof(Weapon.ReloadDone))]
    internal static class WeaponReloadDonePatch
    {
        /// <summary>
        /// A gun reloaded by hand is not filled behind the hand: a mod's "unholster is reload" or "change fire mode is reload" reached the
        /// game's fill past <see cref="WeaponReloadPatch"/>, and with the state adopted after it a gun holstered with its magazine out came back
        /// with one in, the rounds off the reserve, the old magazine still in the hand (reload scout, 2026-11-06). The first draw has no
        /// state yet: filled as authored.
        /// </summary>
        private static bool Prefix(Weapon __instance, out bool __state)
        {
            __state = false;
            if (!WeaponHooks.PlayerWeaponInVR(__instance) || VRConfig.ReloadNow != ReloadMode.Manual)
            {
                return true;
            }
            try
            {
                AmmoState state = WeaponSystem.Instance.StateFor(__instance.GetActiveSubWeapon(), create: false);
                __state = state != null && state.Participates;
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("Weapon.ReloadDone prefix (VR)", e);
            }
            return !__state;
        }

        private static void Postfix(Weapon __instance, bool __state)
        {
            if (!__state && WeaponHooks.PlayerWeaponInVR(__instance))
            {
                try
                {
                    WeaponSystem.Instance.StateFor(__instance.GetActiveSubWeapon(), create: false)?.AdoptWeaponAmmo();
                }
                catch (System.Exception e)
                {
                    Log.ExceptionOnce("Weapon.ReloadDone (VR)", e);
                }
            }
        }
    }

    /// <summary>The 2D scope overlay (and the renderer blackout that goes with it) has no place in VR.</summary>
    [HarmonyPatch(typeof(Weapon), "Update")]
    internal static class WeaponUpdatePatch
    {
        private static void Prefix(Weapon __instance)
        {
            if (WeaponHooks.PlayerWeaponInVR(__instance))
            {
                __instance.aimAmount = 0f;
                if (__instance.showScopeObject && __instance.nonScopeRenderers != null)
                {
                    foreach (Renderer r in __instance.nonScopeRenderers)
                    {
                        if (r != null)
                        {
                            r.enabled = true;
                        }
                    }
                    __instance.showScopeObject = false;
                }
                if (__instance.configuration.scopeAimObject != null && __instance.configuration.scopeAimObject.activeSelf)
                {
                    __instance.configuration.scopeAimObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// No sprint tuck in VR: Update sets the animator's "tuck" while the player sprints, the pose of a gun lowered across the
        /// chest that also holds back its firing animation. The hand holds the gun now and it fires while running (task 314).
        /// </summary>
        private static void Postfix(Weapon __instance)
        {
            if (__instance.animator != null && __instance.unholstered && WeaponHooks.PlayerWeaponInVR(__instance)
                && __instance.animator.isActiveAndEnabled && __instance.animator.runtimeAnimatorController != null)
            {
                __instance.animator.SetBool(Weapon.TUCK_PARAMETER_HASH, false);
            }
        }
    }

    /// <summary>Aiming down sights = bringing the sights to your eye.</summary>
    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.Aiming))]
    internal static class AimingPatch
    {
        private static void Postfix(FpsActorController __instance, ref bool __result)
        {
            if (!VRManager.Active || WeaponSystem.Instance == null || !VRGameInput.Enabled)
            {
                return;
            }
            Actor actor = __instance.actor;
            if (actor.IsSeated() && !actor.seat.CanUsePersonalWeapons())
            {
                __result = false;
                return;
            }
            bool eye = WeaponSystem.Instance.Held != null && WeaponSystem.Instance.Held.Aiming;
            __result = eye && __instance.inputEnabled && !__instance.IsCursorFree() && !__instance.IsKicking()
                       && !actor.IsSwimming() && !actor.parachuteDeployed && !GameManager.gameOver;
        }
    }

    /// <summary>Swimming follows the head (including pitch, so you can dive) instead of the third-person camera.</summary>
    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.SwimInput))]
    internal static class SwimInputPatch
    {
        private static void Postfix(ref Vector3 __result)
        {
            if (!VRManager.Active || !VRGameInput.Enabled || Rig.VRRig.Instance == null)
            {
                return;
            }
            Transform head = Rig.VRRig.Instance.Head;
            Vector2 stick = VRGameInput.MoveStick;
            __result = head.forward * stick.y + head.right * stick.x;
        }
    }

    /// <summary>
    /// The player's third-person body, the shadow you see in first person, turned the gun it holds to where the head looked:
    /// Actor.UpdateFacing aims the body's look-at (ik.aimPoint, which turns the spine and the arms that hold the gun) at
    /// FacingDirection, the head's camera, while the VR gun points wherever the hand holds it (user, 2026-10-18 evening: "the
    /// shadow from my player turns his weapon based where im looking and not where my gun is actualy pointing"; Task 275).
    /// While a gun is drawn in VR the look-at goes along its barrel (the aim axis of the gun that fires, the muzzle's forward;
    /// an under-barrel launcher while it is the active one) instead, with no prone offset: a prone shooter's eye is on the
    /// barrel. Bullets already leave along the muzzle, the view is the headset's, and the body's own turn (its legs, its
    /// steps) still follows the head, as the game turns it, within 50 degrees (less crouched or prone). On a ladder or in an
    /// obstacle climb the game keeps its own direction; with no gun drawn, or one with no barrel (a grenade, a blade or a
    /// tool; an infinite-ammo gun has one), and for bots and other actors, nothing changes.
    /// </summary>
    [HarmonyPatch(typeof(Actor), "UpdateFacing")]
    internal static class ThirdPersonAimPatch
    {
        private static readonly AccessTools.FieldRef<Actor, ActorIk> IkOf = AccessTools.FieldRefAccess<Actor, ActorIk>("ik");
        private static readonly AccessTools.FieldRef<Actor, TimedAction> ClimbOf = AccessTools.FieldRefAccess<Actor, TimedAction>("climbObstacleAnimationAction");
        private static readonly HashSet<WeaponRigData> Reported = new HashSet<WeaponRigData>();

        /// <summary>A content reload made new rigs: the old ones (kept here) go.</summary>
        internal static void ForgetReported() => Reported.Clear();

        private static void Postfix(Actor __instance)
        {
            if (!VRManager.Active || __instance == null || __instance.aiControlled || __instance.dead || !ReferenceEquals(__instance, LocalPlayer.actor))
            {
                return;
            }
            try
            {
                WeaponSystem system = WeaponSystem.Instance;
                HeldWeapon held = system != null ? system.Held : null;
                if (held == null || held.Weapon == null || held.Weapon != __instance.activeWeapon || !system.Rig.Dominant.HasPose
                    || __instance.IsOnLadder() || !ClimbOf(__instance).TrueDone())
                {
                    return; // the game's own point, the head's facing, stays
                }
                // The gun that fires: an under-barrel launcher while it is the active one, else the held gun (the rig Casings
                // reads too). Bullets leave along its muzzle (Actor.UpdateWeapon fires with the muzzle direction), so the body
                // aims along its barrel.
                WeaponRig rig = held.ActiveRig;
                Weapon firing = held.ActiveWeapon;
                // A grenade, a blade or a tool has no barrel to aim along: the body keeps looking where the head does. An
                // infinite-ammo gun is classed NoAmmo too, but it has a barrel (HeldWeapon measures it the same way: ammo == -1).
                ActionType action = rig.Data.Action;
                bool infinite = firing.configuration != null && firing.configuration.ammo == -1;
                if (action == ActionType.Throwable || action == ActionType.Melee || (action == ActionType.NoAmmo && !infinite))
                {
                    return;
                }
                ActorIk ik = IkOf(__instance);
                Vector3 barrel = rig.AimWorld;
                if (ik == null || barrel.sqrMagnitude < 0.5f)
                {
                    return;
                }
                barrel.Normalize();
                ik.aimPoint = __instance.transform.position + barrel * 100f; // the game's own 100 m point, along the barrel
                if (Reported.Add(rig.Data)) // once per weapon kind, not every frame
                {
                    Log.InfoOnce("body-aim:" + rig.Data.Key, $"{rig.Data.Key}: the third-person body aims along its barrel (at the first aim the head was {Vector3.Angle(barrel, __instance.controller.FacingDirection()):0} degrees off it).");
                }
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("Actor.UpdateFacing (VR body aim)", e); // the body keeps the game's own aim
            }
        }
    }
}
