using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Util;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Personal weapons from the driver seat of a car or Humvee and from any passenger seat, which the flat game
    /// mostly does not allow: reach to a holster and that weapon comes out; put it back and the car horn returns. While armed the game is told
    /// the seat allows personal weapons, so firing, reloading and aiming work as on a gun truck's bed, and the car
    /// still reads the driver's steering and throttle. Tanks and aircraft keep their own controls.
    /// </summary>
    public static class DrivingWeapons
    {
        public static bool Armed { get; private set; }

        /// <summary>
        /// Car and Humvee drivers, and every passenger seat that has no gun of its own (gunner seats use theirs;
        /// tank, helicopter and plane drivers keep their controls).
        /// </summary>
        public static bool CanDraw(Actor actor)
        {
            if (!VRManager.Active || !VRConfig.WeaponsWhileDriving.Value || actor == null || actor.dead || !actor.IsSeated())
            {
                return false;
            }
            Seat seat = actor.seat;
            if (HasGun(seat))
            {
                return false;
            }
            return !seat.IsDriverSeat() || seat.vehicle is Car || seat.vehicle is ArcadeCar;
        }

        /// <summary>
        /// The seat has a gun of its own. Not the car horn: the game keeps it among a car's mounted weapons, and every
        /// car and Humvee driver was refused a weapon (85-agent review).
        /// </summary>
        private static bool HasGun(Seat seat)
        {
            if (seat.weapons == null)
            {
                return false;
            }
            foreach (MountedWeapon w in seat.weapons)
            {
                if (w != null && !(w is CarHorn))
                {
                    return true;
                }
            }
            return false;
        }

        public static void Draw(Actor actor, Weapon weapon)
        {
            Armed = true;
            if (actor.activeWeapon != null && actor.activeWeapon != weapon)
            {
                actor.HolsterActiveWeapon();
            }
            actor.Unholster(weapon);
            weapon.Show();
        }

        public static void PutAway(Actor actor)
        {
            if (actor != null && actor.activeWeapon != null && !actor.activeWeapon.IsMountedWeapon())
            {
                actor.HolsterActiveWeapon();
            }
            Armed = false;
            if (actor != null && actor.IsSeated() && actor.seat.HasAnyMountedWeapons() && actor.seat.weapons[0] != null) // (a mod's seat can leave its first slot empty)
            {
                actor.Unholster(actor.seat.weapons[0]);
            }
        }

        /// <summary>Left the seat, died or the option was turned off.</summary>
        public static void Reset()
        {
            Armed = false;
        }
    }

    [HarmonyPatch(typeof(Seat), nameof(Seat.CanUsePersonalWeapons))]
    internal static class SeatPersonalWeaponsPatch
    {
        private static void Postfix(Seat __instance, ref bool __result)
        {
            if (!__result && DrivingWeapons.Armed && Core.VRManager.Active && __instance.occupant != null && __instance.occupant == GameContext.PlayerActor)
            {
                __result = true;
            }
        }
    }
}
