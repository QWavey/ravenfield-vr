using HarmonyLib;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// The game's own FpsActorController.GetActiveCamera reads its third-person camera unguarded; while a joined game changes to the
    /// host's map that camera is already gone, and the map's line-of-sight triggers and the spectator camera threw a
    /// NullReferenceException each time they asked (the VR-joins-a-flat-host check, 2026-11-15). With no third-person camera it answers
    /// as it would with it off: the override camera if one is used, else the first-person one.
    /// </summary>
    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.GetActiveCamera))]
    internal static class ActiveCameraGuard
    {
        private static bool Prefix(FpsActorController __instance, ref Camera __result)
        {
            if (__instance == null)
            {
                return true;
            }
            // (The game's own order, each camera looked at only if it is still there.)
            if (__instance.IsUsingOverrideCamera() && __instance.overrideCamera != null)
            {
                __result = __instance.overrideCamera;
            }
            else if (__instance.tpCamera != null && __instance.tpCamera.enabled)
            {
                __result = __instance.tpCamera;
            }
            else
            {
                __result = __instance.fpCamera;
            }
            return false;
        }
    }
}
