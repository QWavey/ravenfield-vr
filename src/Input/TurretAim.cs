using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Input
{
    /// <summary>
    /// Mounted guns, tank turrets and stabilised turrets normally turn from mouse deltas, which VR zeroes.
    /// In VR they steer toward where you look (or point, if configured), using the same proportional
    /// steering the game uses for AI gunners.
    /// </summary>
    internal static class TurretAim
    {
        public static Vector3? Direction()
        {
            if (!VRManager.Active || !VRGameInput.Enabled || VRRig.Instance == null)
            {
                return null;
            }
            VRRig rig = VRRig.Instance;
            // The hand that fires the mounted gun (the gun hand, after a pass), so the aim and the trigger are the same hand (2026-10-19).
            if (VRConfig.TurretAim.Value == TurretAimMode.Hand && rig.Dominant.Tracked)
            {
                return rig.Dominant.Pointer.Forward;
            }
            return rig.Head.forward;
        }

        public static bool Steer(Weapon turret, Transform reference, float gain, float clamp, ref Vector2 result)
        {
            // Not while the laser is on one of the mod's panels either: the gun followed the head turned to read it, as the flat
            // game's mouse aim does not on a free cursor (the game's own input reads no aim in VR then; vehicle hunt, 2026-10-20).
            if (turret.user == null || turret.user.aiControlled || IngameMenuUi.IsOpen() || GameManager.gameOver || VRGameInput.GunInputBlocked)
            {
                return true;
            }
            Vector3? dir = Direction();
            if (dir == null)
            {
                return true;
            }
            if (reference == null)
            {
                reference = turret.transform; // an empty muzzle slot: the game fires from the turret itself then too
            }
            Vector3 local = reference.worldToLocalMatrix.MultiplyVector(dir.Value).normalized;
            // Steered by the angle still to turn (radians), not its sine: the same close to the target, but the sine falls
            // back to nothing as the target goes behind, so a turret asked to look round hardly turned.
            float yaw = Mathf.Atan2(local.x, local.z);
            float pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude);
            result = new Vector2(Mathf.Clamp(yaw * gain, -clamp, clamp), Mathf.Clamp(pitch * gain, -clamp, clamp));
            return false;
        }
    }

    [HarmonyPatch(typeof(MountedTurret), "GetInput")]
    internal static class MountedTurretAimPatch
    {
        private static bool Prefix(MountedTurret __instance, ref Vector2 __result) =>
            TurretAim.Steer(__instance, __instance.CurrentMuzzle(), 5f, 10f, ref __result);
    }

    [HarmonyPatch(typeof(TankTurret), "GetInput")]
    internal static class TankTurretAimPatch
    {
        private static bool Prefix(TankTurret __instance, ref Vector2 __result) =>
            TurretAim.Steer(__instance, __instance.CurrentMuzzle(), 3f, 10f, ref __result);
    }

    [HarmonyPatch(typeof(MountedStabilizedTurret), "GetInput")]
    internal static class StabilizedTurretAimPatch
    {
        private static bool Prefix(MountedStabilizedTurret __instance, ref Vector2 __result) =>
            TurretAim.Steer(__instance, __instance.pitchTransform, 5f, 3f, ref __result);
    }

    /// <summary>
    /// A weapon's own animation event asking its sound bank for a clip it does not have (three RPG-7V variants in the
    /// rocket film) threw IndexOutOfRangeException from the game's SoundBank: skipped, said once per bank.
    /// </summary>
    [HarmonyPatch(typeof(SoundBank), "PlaySoundBank")]
    internal static class SoundBankIndexGuardPatch
    {
        private static readonly System.Collections.Generic.HashSet<int> Reported = new System.Collections.Generic.HashSet<int>();

        private static bool Prefix(SoundBank __instance, int index)
        {
            if (__instance.clips != null && index >= 0 && index < __instance.clips.Length)
            {
                return true;
            }
            if (Reported.Add(__instance.GetInstanceID()))
            {
                Log.Warn($"'{__instance.name}' asked its sound bank for clip {index} of {(__instance.clips != null ? __instance.clips.Length : 0)}: skipped.");
            }
            return false;
        }
    }

    /// <summary>
    /// A deployed sentry gun's aim blend (the game's AimTransform) with its idle or aim transform missing threw a
    /// NullReferenceException every frame for as long as the turret stood (seven sentry kits in the weapons run): it is
    /// skipped then, the turret keeps its pose.
    /// </summary>
    [HarmonyPatch(typeof(AimTransform), "LateUpdate")]
    internal static class AimTransformGuardPatch
    {
        private static readonly System.Collections.Generic.HashSet<int> Reported = new System.Collections.Generic.HashSet<int>();

        private static bool Prefix(AimTransform __instance)
        {
            if (__instance.idleTransform != null && __instance.aimTransform != null)
            {
                return true;
            }
            if (Reported.Add(__instance.GetInstanceID())) // every frame for a standing turret: nothing built after the first
            {
                Log.Warn($"'{__instance.name}' blends its aim between transforms that are gone: left as it is.");
            }
            return false;
        }
    }
}
