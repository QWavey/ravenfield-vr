using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Input
{
    /// <summary>
    /// Vehicle control vectors from the VR controllers, in the game's conventions:
    /// car (steer right, throttle), helicopter / airplane (yaw right, collective or throttle, roll right, nose down).
    /// Physical cockpit controls (grabbed steering wheel / flight stick) override the sticks when held.
    /// </summary>
    public static class VehicleInput
    {
        /// <summary>Set by <see cref="RavenfieldVR.Rig.CockpitControls"/> while a hand holds the steering wheel; -1..1.</summary>
        public static float? WheelSteer;

        /// <summary>Set while a hand holds the flight stick: x = roll right, y = nose down; -1..1.</summary>
        public static Vector2? FlightStick;

        public static bool BlocksTurning(VehicleKind kind) =>
            kind == VehicleKind.Helicopter || kind == VehicleKind.Airplane;

        public static Vector2 Car()
        {
            Vector2 left = VRGameInput.MoveStick;
            float steer = WheelSteer ?? left.x;
            return new Vector2(steer, left.y);
        }

        public static Vector4 Helicopter()
        {
            Vector2 left = VRGameInput.MoveStick;
            Vector2 right = VRGameInput.ApplyDeadzone(VRInput.TurnStick, VRConfig.StickDeadzone.Value);
            Vector2 cyclic = FlightStick ?? right;
            return new Vector4(left.x, left.y, cyclic.x, cyclic.y);
        }

        public static Vector4 Airplane()
        {
            Vector2 left = VRGameInput.MoveStick;
            Vector2 right = VRGameInput.ApplyDeadzone(VRInput.TurnStick, VRConfig.StickDeadzone.Value);
            Vector2 stick = FlightStick ?? right;
            return new Vector4(left.x, left.y, stick.x, stick.y);
        }

        internal static bool ShouldDrive(FpsActorController c) =>
            VRGameInput.Enabled && c.inputEnabled && c.movementInputEnabled && !GameManager.gameOver && !GameContext.GameMenuOpen;
    }

    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.CarInput))]
    internal static class CarInputPatch
    {
        private static void Postfix(FpsActorController __instance, ref Vector2 __result)
        {
            if (VehicleInput.ShouldDrive(__instance))
            {
                __result = Vehicle.Clamp2(__result + VehicleInput.Car());
            }
        }
    }

    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.HelicopterInput))]
    internal static class HelicopterInputPatch
    {
        private static void Postfix(FpsActorController __instance, ref Vector4 __result)
        {
            if (VehicleInput.ShouldDrive(__instance))
            {
                // Auto-hover doubles player input on top of its stabilisation; match that.
                float gain = __instance.helicopterAutoHoverEnabled ? 2f : 1f;
                __result += VehicleInput.Helicopter() * gain;
            }
        }
    }

    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.AirplaneInput))]
    internal static class AirplaneInputPatch
    {
        private static void Postfix(FpsActorController __instance, ref Vector4 __result)
        {
            if (VehicleInput.ShouldDrive(__instance))
            {
                __result += VehicleInput.Airplane();
            }
        }
    }
}
