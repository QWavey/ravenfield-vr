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

    /// <summary>
    /// A seat entered with a personal weapon drawn under <see cref="DrivingWeapons"/> (a switch to another seat of the vehicle): put away
    /// first, and Armed cleared, as the game puts a drawn weapon away for a seat that allows none. Armed made the new seat answer that it allows
    /// personal weapons, so the game kept the pistol out and drew the seat's own weapon over it (a car driver's horn, a gunner's gun): the
    /// pistol stayed shown, Armed stayed set in a car's driver seat, and every seated button went to the horn, seat switching too (vehicle
    /// scout, 2026-11-08). In the new seat a weapon is drawn again from its holster as before.
    /// </summary>
    [HarmonyPatch(typeof(Actor), nameof(Actor.EnterSeat))]
    internal static class EnterSeatPutAwayPatch
    {
        private static void Prefix(Actor __instance)
        {
            if (!DrivingWeapons.Armed || !Core.VRManager.Active || __instance == null || !ReferenceEquals(__instance, GameContext.PlayerActor))
            {
                return;
            }
            try
            {
                if (__instance.activeWeapon != null && !__instance.activeWeapon.IsMountedWeapon())
                {
                    __instance.HolsterActiveWeapon();
                }
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("Actor.EnterSeat (VR, put away)", e); // the seat is entered all the same
            }
            DrivingWeapons.Reset();
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
