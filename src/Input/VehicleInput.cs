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

        /// <summary>
        /// A plane's throttle lever as the hand left it (<see cref="RavenfieldVR.Rig.CockpitControls"/>): -1..1 as the game reads it (0 cruise,
        /// 1 full, under -0.3 the airbrake). It stays where it was let go, as a real lever does (VTOL VR's, the research of 2026-11-12);
        /// a push of the left stick takes the throttle back from it. Null: none set, the stick as before.
        /// </summary>
        public static float? Throttle;

        /// <summary>
        /// A plane's stick as the hand let go of it: held there, as a trimmed stick holds the attitude it was flying, not sprung back
        /// to the middle (the invention of 2026-11-14: the release sets the trim; VTOL VR's players name the stick that does not stay
        /// as its pitfall). A push of the thumbstick takes the stick back from it; a new seat clears it. Null: none.
        /// </summary>
        public static Vector2? Trim;

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
            // The raw stick, zeroed while it scrolls one of the mod's panels (it pitched and rolled the aircraft meanwhile).
            Vector2 right = VRGameInput.ApplyDeadzone(VRGameInput.TurnStickRaw, VRConfig.StickDeadzone.Value);
            Vector2 cyclic = FlightStick ?? right;
            return new Vector4(left.x, left.y, cyclic.x, cyclic.y);
        }

        public static Vector4 Airplane()
        {
            Vector2 left = VRGameInput.MoveStick;
            // The raw stick, zeroed while it scrolls one of the mod's panels (it pitched and rolled the aircraft meanwhile).
            Vector2 right = VRGameInput.ApplyDeadzone(VRGameInput.TurnStickRaw, VRConfig.StickDeadzone.Value);
            // The thumbstick with a gentler middle (35% linear, 65% cubed): a plane rolled and pitched hard at a small push, and holding
            // a line was a fight (the user, 2026-11-11: "I need better controlls"). Full at the edge as before; the grabbed stick as it is.
            // (The game reads AirplaneInput for a helicopter's cockpit stick too: there it is the helicopter's own, linear, or the stick
            // drawn showed a quarter of the throw the helicopter was given; scout.)
            bool plane = GameContext.SeatKind == VehicleKind.Airplane;
            if (Trim.HasValue && (!plane || right.sqrMagnitude > 0.3f * 0.3f))
            {
                Trim = null; // (the thumbstick pushed, or not a plane: the trim is let go)
            }
            Vector2 stick = FlightStick ?? Trim ?? (plane ? new Vector2(Expo(right.x), Expo(right.y)) : right);
            float throttle = left.y;
            if (Throttle.HasValue)
            {
                if (!plane || Mathf.Abs(left.y) > 0.3f)
                {
                    Throttle = null; // (the stick pushed: it has the throttle again, the lever's setting dropped)
                }
                else
                {
                    throttle = Throttle.Value;
                }
            }
            return new Vector4(left.x, throttle, stick.x, stick.y);
        }

        /// <summary>A stick's value with a gentler middle and the same ends. Pure: the offline tests check it.</summary>
        internal static float Expo(float v) => 0.35f * v + 0.65f * v * v * v;

        internal static bool ShouldDrive(FpsActorController c) =>
            VRGameInput.Enabled && c.inputEnabled && c.movementInputEnabled && !GameManager.gameOver && !GameContext.GameMenuOpen
            && !GameManager.IsPaused(); // (paused: the wheel and stick stand still, and so do the thumbsticks; scout)
    }

    /// <summary>
    /// A mortar's range: the game takes it from the mouse wheel alone (<c>FpsActorController.RangeInput</c>), so in VR it stayed at its default
    /// (2026-11-08). The right stick up and down while sitting at one, a full push about 60 m a second.
    /// </summary>
    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.RangeInput))]
    internal static class MortarRangePatch
    {
        private const float MetresPerSecond = 60f;

        private static void Postfix(FpsActorController __instance, ref float __result)
        {
            Actor actor = __instance.actor;
            if (!VehicleInput.ShouldDrive(__instance) || actor == null || !actor.IsSeated() || !(actor.seat.activeWeapon is Mortar mortar) || mortar.rangeChangeSpeed <= 0f)
            {
                return;
            }
            float y = VRGameInput.ApplyDeadzone(VRGameInput.TurnStickRaw, VRConfig.StickDeadzone.Value).y;
            __result += y * MetresPerSecond * Time.deltaTime / mortar.rangeChangeSpeed;
        }
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
