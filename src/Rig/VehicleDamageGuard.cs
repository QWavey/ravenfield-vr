using System;
using HarmonyLib;
using RavenfieldVR.Core;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// The game's own <c>FpsActorController.OnVehicleWasDamaged</c> threw a NullReferenceException when a shot hit the vehicle the VR
    /// player sat in (the vehicle check of 2026-11-15, every time a projectile hit it): thrown inside <c>Vehicle.Damage</c>'s event, it
    /// cut the hit's handling short. Caught here and logged once; the damage itself is already taken.
    /// </summary>
    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.OnVehicleWasDamaged))]
    internal static class VehicleDamageGuard
    {
        private static Exception Finalizer(Exception __exception)
        {
            if (__exception is NullReferenceException)
            {
                Log.WarnOnce("vehicle-damaged-nre", "The game's vehicle-was-damaged reaction threw (caught, the hit counts): " + __exception.Message);
                return null;
            }
            return __exception;
        }
    }
}
