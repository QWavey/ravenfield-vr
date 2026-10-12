using System.Collections;
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

        /// <summary>Set while the mod itself asks the game to reload (assisted mode / scripted refills).</summary>
        public static bool AllowGameReload;
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
            if (anim.isActiveAndEnabled && anim.isInitialized)
            {
                Step(anim);
            }
            if (VRManager.Instance != null)
            {
                VRManager.Instance.StartCoroutine(StepNextFrame(__instance, anim));
            }
        }

        private static IEnumerator StepNextFrame(Weapon weapon, Animator anim)
        {
            yield return null; // runs after this frame's Update calls, before its animation pass
            if (weapon != null && anim != null && anim.isActiveAndEnabled && weapon.user != null && weapon.user.activeWeapon == weapon)
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

    [HarmonyPatch(typeof(Weapon), nameof(Weapon.CanFire))]
    internal static class WeaponCanFirePatch
    {
        private static void Postfix(Weapon __instance, ref bool __result)
        {
            if (!__result || !WeaponHooks.PlayerWeaponInVR(__instance) || VRConfig.Reload.Value != ReloadMode.Manual)
            {
                return;
            }
            AmmoState state = WeaponSystem.Instance.StateFor(__instance, create: false);
            if (state != null && !state.CanFire)
            {
                __result = false;
                WeaponSystem.Instance.OnDryFire(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Weapon), nameof(Weapon.Shoot))]
    internal static class WeaponShootPatch
    {
        private static void Postfix(Weapon __instance, bool __result)
        {
            if (__result && WeaponHooks.PlayerWeaponInVR(__instance))
            {
                WeaponSystem.Instance.OnPlayerShot(__instance);
            }
        }
    }

    [HarmonyPatch(typeof(Weapon), nameof(Weapon.Reload))]
    internal static class WeaponReloadPatch
    {
        private static bool Prefix(Weapon __instance)
        {
            if (!WeaponHooks.PlayerWeaponInVR(__instance) || WeaponHooks.AllowGameReload || VRConfig.Reload.Value != ReloadMode.Manual)
            {
                return true;
            }
            AmmoState state = WeaponSystem.Instance.StateFor(__instance.GetActiveSubWeapon(), create: true);
            // Weapons without a manual model (infinite ammo, tools) keep the normal reload.
            return state == null || !state.Participates;
        }
    }

    [HarmonyPatch(typeof(Weapon), nameof(Weapon.ReloadDone))]
    internal static class WeaponReloadDonePatch
    {
        private static void Postfix(Weapon __instance)
        {
            if (WeaponHooks.PlayerWeaponInVR(__instance))
            {
                WeaponSystem.Instance.StateFor(__instance.GetActiveSubWeapon(), create: false)?.AdoptWeaponAmmo();
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
}
