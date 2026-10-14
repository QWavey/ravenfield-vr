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
            if (VRConfig.TurretAim.Value == TurretAimMode.Hand && rig.Dominant.Tracked)
            {
                return rig.Dominant.Pointer.Forward;
            }
            return rig.Head.forward;
        }

        public static bool Steer(Weapon turret, Transform reference, float gain, float clamp, ref Vector2 result)
        {
            if (turret.user == null || turret.user.aiControlled || IngameMenuUi.IsOpen() || GameManager.gameOver)
            {
                return true;
            }
            Vector3? dir = Direction();
            if (dir == null)
            {
                return true;
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
}
