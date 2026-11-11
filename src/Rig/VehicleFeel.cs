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
        private static float _nextBump, _nextHum;
        private static Vector3 _accelAverage;

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
            Hum(rig, v);
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
                _accelAverage = Vector3.zero;
                return;
            }
            if (velocity == _lastVelocity)
            {
                return; // (no physics step this frame: its speed changes only on those, 50 a second against 90 frames)
            }
            // Per physics step, not per frame: divided by the frame's time, a steady turn read as 1.8 times its pull on the frames with a
            // step and nothing on those without, a jolt every other frame.
            float dt = Mathf.Max(Time.fixedDeltaTime, Time.deltaTime, 1e-3f);
            // The change of speed beyond what the ride itself does: its acceleration against its own average of the last third of a
            // second. A crash or a hard landing is a spike; a hard turn (3 g in a plane, a fast corner) is steady and is not one
            // (counted whole, it rumbled every 0.12 s for as long as the turn lasted; vehicle scout, 2026-11-11).
            Vector3 accel = (velocity - _lastVelocity) / dt;
            _lastVelocity = velocity;
            float jolt = (accel - _accelAverage).magnitude;
            _accelAverage = Vector3.Lerp(_accelAverage, accel, 1f - Mathf.Exp(-dt / 0.3f));
            if (jolt > 25f && Time.unscaledTime >= _nextBump)
            {
                float strength = Mathf.Clamp01((jolt - 25f) / 150f);
                _nextBump = Time.unscaledTime + 0.12f;
                Rumble(rig, 0.06f + 0.1f * strength, 60f + 60f * strength, 0.25f + 0.6f * strength);
            }
        }

        /// <summary>
        /// A jolt read elsewhere (a vehicle another PC drives: from two of its told speeds, 15 a second): felt as a bump when the player
        /// sits in it. <paramref name="jolt"/>: m/s a second beyond the ride's own.
        /// </summary>
        internal static void OnJolt(Vehicle v, float jolt)
        {
            VRRig rig = VRRig.Instance;
            Actor actor = GameContext.PlayerActor;
            if (rig == null || actor == null || actor.dead || actor.seat == null || actor.seat.vehicle != v || jolt <= 25f || Time.unscaledTime < _nextBump)
            {
                return;
            }
            float strength = Mathf.Clamp01((jolt - 25f) / 150f);
            _nextBump = Time.unscaledTime + 0.12f;
            Rumble(rig, 0.06f + 0.1f * strength, 60f + 60f * strength, 0.25f + 0.6f * strength);
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

        /// <summary>
        /// The engine, faintly, in a hand on the wheel or the flight stick only: a tenth of a second ten times a second, its strength by
        /// the engine's power (idle almost nothing). Not in a hand that is free, nor with nothing running.
        /// </summary>
        private static void Hum(VRRig rig, Vehicle v)
        {
            if (Time.unscaledTime < _nextHum || v.engine == null)
            {
                return;
            }
            _nextHum = Time.unscaledTime + 0.1f;
            // (By the throttle as well as the engine running: power alone is on or off, the same at idle and at full throttle.)
            float push = Mathf.Max(Mathf.Abs(v.engine.targetThrottle), v.Velocity().magnitude / 40f); // (most vehicles do not set the throttle: their speed then)
            float power = Mathf.Clamp01(v.engine.power) * (0.3f + 0.7f * Mathf.Clamp01(push));
            if (power < 0.05f)
            {
                return;
            }
            foreach (VRHand hand in rig.Hands)
            {
                if (hand != null && hand.Tracked && hand.Role == HandRole.Cockpit)
                {
                    hand.Haptic(0.1f, 35f + 40f * power, 0.04f + 0.08f * power);
                }
            }
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
