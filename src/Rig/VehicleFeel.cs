using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// What a vehicle does felt in the hands (the user, 2026-11-11: "Improve all vehicle feelings in general"): a hit on the vehicle
    /// the player sits in, by its damage; a bump, a crash or a hard landing, by how sharply its speed changed. Both hands, the one on
    /// the wheel or the stick harder. Nothing in vehicles was felt before. Off with the controllers' vibration (HapticStrength 0).
    /// </summary>
    internal static class VehicleFeel
    {
        private static Vehicle _of;
        private static Vector3 _lastVelocity;
        private static float _nextBump;

        /// <summary>Once a frame from the rig (any seat of a vehicle, not only the driver's).</summary>
        internal static void Update(VRRig rig)
        {
            Actor actor = GameContext.PlayerActor;
            Vehicle v = actor != null && !actor.dead && actor.IsSeated() && actor.seat != null ? actor.seat.vehicle : null;
            if (v == null || v.dead || v.isTurret || GameManager.IsPaused() || rig == null)
            {
                _of = null;
                return;
            }
            if (v.rigidbody == null || v.rigidbody.isKinematic)
            {
                _of = null; // (another PC drives it: its speed comes in steps 15 times a second, no jolt to read from them; hits still felt)
                return;
            }
            Vector3 velocity = v.rigidbody.velocity;
            if (!ReferenceEquals(_of, v))
            {
                _of = v;
                _lastVelocity = velocity;
                return;
            }
            float dt = Mathf.Max(Time.deltaTime, 1e-3f);
            // The change of speed beyond what driving does: a car brakes at about 8 m/s each second, a crash stops it in a tenth.
            float jolt = (velocity - _lastVelocity).magnitude / dt;
            _lastVelocity = velocity;
            if (jolt > 25f && Time.unscaledTime >= _nextBump)
            {
                float strength = Mathf.Clamp01((jolt - 25f) / 150f);
                _nextBump = Time.unscaledTime + 0.12f;
                Rumble(rig, 0.06f + 0.1f * strength, 60f + 60f * strength, 0.25f + 0.6f * strength);
            }
        }

        /// <summary>A hit on the vehicle the player sits in: by the damage, a little for small arms, hard for a rocket.</summary>
        internal static void OnHit(Vehicle v, float damage)
        {
            VRRig rig = VRRig.Instance;
            Actor actor = GameContext.PlayerActor;
            if (rig == null || actor == null || actor.dead || actor.seat == null || actor.seat.vehicle != v || damage <= 0f)
            {
                return;
            }
            float strength = Mathf.Clamp01(damage / Mathf.Max(100f, v.maxHealth * 0.3f));
            Rumble(rig, 0.04f + 0.12f * strength, 90f + 60f * strength, 0.2f + 0.7f * strength);
        }

        private static void Rumble(VRRig rig, float seconds, float frequency, float amplitude)
        {
            foreach (VRHand hand in rig.Hands)
            {
                if (hand != null && hand.Tracked)
                {
                    hand.Haptic(seconds, frequency, hand.Role == HandRole.Cockpit ? amplitude : amplitude * 0.6f);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Vehicle), nameof(Vehicle.Damage))]
    internal static class VehicleFeelHitPatch
    {
        private static void Prefix(Vehicle __instance, out float __state) => __state = __instance.health;

        private static void Postfix(Vehicle __instance, float __state)
        {
            if (VRManager.Active && __instance.health < __state)
            {
                VehicleFeel.OnHit(__instance, __state - __instance.health);
            }
        }
    }
}
