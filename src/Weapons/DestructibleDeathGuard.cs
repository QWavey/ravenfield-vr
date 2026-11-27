using System;
using HarmonyLib;
using Lua;
using RavenfieldVR.Core;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The game's own <c>Destructible.Die</c> (EA40, 2026-11-27) invokes its <c>onDeath</c> event with no check: the event is made in
    /// <c>Awake</c> now (the update's "onDeath available in lua Awake"), and a destructible whose Awake did not make it (no Ravenscript
    /// events yet as it woke) threw a NullReferenceException out of Die into the bullet's hit (<c>Projectile.Hit</c> to
    /// <c>Destructible.Shatter</c>; the autotest of 2026-11-27, AUTOMATICO AKIMBO's shots). <c>Damage</c> makes a missing
    /// <c>onTakeDamage</c> on the spot; Die gets the same here before it runs, and a throw past that is caught and logged once
    /// (by then the destructible is dead and its parts switched: only the event is lost).
    /// </summary>
    [HarmonyPatch(typeof(Destructible), "Die")]
    internal static class DestructibleDeathGuard
    {
        private static void Prefix(Destructible __instance)
        {
            try
            {
                if (__instance != null && __instance.onDeath == null && RavenscriptManager.instance != null && RavenscriptManager.events != null)
                {
                    __instance.onDeath = RavenscriptManager.events.CreateEvent<DamageInfo>();
                }
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("Destructible.Die (made its event)", e);
            }
        }

        private static Exception Finalizer(Exception __exception)
        {
            if (__exception is NullReferenceException)
            {
                Log.WarnOnce("destructible-die-nre", "The game's destructible death threw (caught, the hit counts): " + __exception.Message);
                return null;
            }
            return __exception;
        }
    }
}
