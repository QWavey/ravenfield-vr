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

        private static Vehicle _smoothedOf;
        private static readonly System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Rigidbody, RigidbodyInterpolation>> Smoothed =
            new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Rigidbody, RigidbodyInterpolation>>();

        /// <summary>
        /// The vehicle sat in drawn smoothly: physics steps 60 times a second (GameManager), the headset draws 90, and a vehicle whose
        /// bodies are not interpolated moved its seat, the view and the cabin only at each step, a judder against the headset's frames
        /// (the user, 2026-11-17: "its still very weird in the vehicle"). Every body of it (a tank's turret is its own, on a joint)
        /// interpolated while sat in, each as it was once out or the vehicle changed. (The game interpolates a vehicle's main body
        /// itself, GameManager.SetupVehiclePrefab: what this adds is the other bodies, a turret on its own joint, a mod's parts.) Only the drawn pose: physics is the same. Not
        /// another PC's vehicle (kinematic, moved by the network).
        /// </summary>
        /// <summary>The bodies given back as the game had them (the rig going: VR off in a seat left them interpolated; scout).</summary>
        internal static void Release() => Smooth(null);

        private static void Smooth(Vehicle v)
        {
            Vehicle want = v != null && !v.dead && v.rigidbody != null && !v.rigidbody.isKinematic ? v : null;
            if (ReferenceEquals(want, _smoothedOf))
            {
                return;
            }
            foreach (System.Collections.Generic.KeyValuePair<Rigidbody, RigidbodyInterpolation> was in Smoothed)
            {
                if (was.Key != null)
                {
                    was.Key.interpolation = was.Value; // (as the game had it)
                }
            }
            Smoothed.Clear();
            _smoothedOf = want;
            if (want == null)
            {
                return;
            }
            int made = 0;
            foreach (Rigidbody rb in want.GetComponentsInChildren<Rigidbody>())
            {
                if (rb == null || rb.isKinematic || rb.GetComponentInParent<Actor>() != null)
                {
                    continue; // (not the soldiers' ragdoll bones)
                }
                Smoothed.Add(new System.Collections.Generic.KeyValuePair<Rigidbody, RigidbodyInterpolation>(rb, rb.interpolation));
                if (rb.interpolation == RigidbodyInterpolation.None)
                {
                    rb.interpolation = RigidbodyInterpolation.Interpolate;
                    made++;
                }
            }
            if (made > 0)
            {
                Log.InfoOnce("vehicle-smooth:" + want.name, $"Vehicle {want.name}: {made} of its {Smoothed.Count} bodies moved only at the physics steps (60 a second, not interpolated): drawn interpolated while sat in, for the headset's frames.");
            }
        }

        /// <summary>Once a frame from the rig (any seat of a vehicle, not only the driver's).</summary>
        internal static void Update(VRRig rig)
        {
            Actor actor = GameContext.PlayerActor;
            Vehicle v = actor != null && !actor.dead && actor.IsSeated() && actor.seat != null ? actor.seat.vehicle : null;
            Smooth(v);
            Readout(rig, actor, v);
            if (v == null || v.dead || v.isTurret || GameManager.IsPaused() || rig == null)
            {
                _of = null;
                if (v == null)
                {
                    _saidSeat = null; // (out of it: getting back into the same vehicle is not a seat change)
                }
                return;
            }
            Hum(rig, v);
            Burning(rig, v);
            SaySeat(rig, actor.seat);
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
            // (Burning, its own small damage ticks are not hits: the burn pulse says it; a real hit, stronger, still comes through. Scout.)
            if (v.burning && strength < 0.08f)
            {
                return;
            }
            if (Time.unscaledTime - _hitAt < 0.05f && strength < _hitStrength)
            {
                return; // (a burst: one pulse each 50 ms, a stronger one through at once; each a call into SteamVR before)
            }
            _hitAt = Time.unscaledTime;
            _hitStrength = strength;
            Rumble(rig, 0.04f + 0.12f * strength, 90f + 60f * strength, 0.2f + 0.7f * strength);
        }

        private static float _nextBurnPulse;
        private static TextMesh _readout;
        private static readonly RaycastHit[] Hits = new RaycastHit[16];

        /// <summary>For the vehicle check: the pilot's readout as shown (null: not shown).</summary>
        internal static string ReadoutText => _readout != null && _readout.gameObject.activeSelf ? _readout.text : null;
        private static float _nextReadout;
        private static int _shownSpeed = -1, _shownHeight = -1;

        /// <summary>
        /// A pilot's speed and height, low in front of the view (the vehicles scout, 2026-11-15: the only readout was on the wrist, and
        /// lifting it meant letting go of the stick). Five times a second, its text built only when a number changed.
        /// </summary>
        private static void Readout(VRRig rig, Actor actor, Vehicle v)
        {
            bool pilot = rig != null && v != null && !v.dead && actor != null && actor.seat != null && actor.seat.IsDriverSeat()
                         && (v is Helicopter || v is Airplane || v is Plane) && !GameContext.GameMenuOpen;
            if (!pilot)
            {
                if (_readout != null && _readout.gameObject.activeSelf)
                {
                    _readout.gameObject.SetActive(false);
                }
                return;
            }
            if (_readout == null)
            {
                var go = new GameObject("RavenfieldVR Flight Readout");
                Object.DontDestroyOnLoad(go);
                _readout = go.AddComponent<TextMesh>();
                Font font = Resources.GetBuiltinResource<Font>("Arial.ttf");
                _readout.font = font;
                go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                _readout.anchor = TextAnchor.MiddleCenter;
                _readout.alignment = TextAlignment.Center;
                _readout.fontSize = 64;
                _readout.characterSize = 0.0045f;
                _readout.color = new Color(0.75f, 1f, 0.75f, 0.85f);
            }
            if (!_readout.gameObject.activeSelf)
            {
                _readout.gameObject.SetActive(true);
            }
            // Under the eyes, in the seat's frame (it rides with the aircraft, not with the head).
            Transform root = rig.Root;
            Runtime.RigidPose neutral = rig.NeutralHead;
            Vector3 eyes = root.TransformPoint(neutral.position);
            Quaternion facing = root.rotation * Quaternion.Euler(0f, neutral.rotation.eulerAngles.y, 0f);
            _readout.transform.SetPositionAndRotation(eyes + facing * new Vector3(0f, -0.3f, 0.55f), facing * Quaternion.Euler(25f, 0f, 0f));
            if (Time.unscaledTime < _nextReadout)
            {
                return;
            }
            _nextReadout = Time.unscaledTime + 0.2f;
            int speed = Mathf.RoundToInt(v.Velocity().magnitude * 3.6f);
            int height = -1;
            // (The nearest hit that is not the aircraft itself: its gear or rotor read "0 m"; scout.)
            float nearest = float.MaxValue;
            int hits = Physics.RaycastNonAlloc(v.transform.position, Vector3.down, Hits, 2000f, ~(1 << 12), QueryTriggerInteraction.Ignore);
            for (int i = 0; i < hits; i++)
            {
                if (Hits[i].collider != null && Hits[i].collider.GetComponentInParent<Vehicle>() != v && Hits[i].distance < nearest)
                {
                    nearest = Hits[i].distance;
                }
            }
            if (nearest < float.MaxValue)
            {
                height = Mathf.RoundToInt(nearest);
            }
            if (speed != _shownSpeed || height != _shownHeight)
            {
                _shownSpeed = speed;
                _shownHeight = height;
                _readout.text = height >= 0 ? $"{speed} km/h    {height} m" : $"{speed} km/h";
            }
        }
        private static Seat _saidSeat;
        private static bool? _hoverWas;

        /// <summary>
        /// A seat changed within the vehicle: which one now (the seat key or the stick's click moved him, and nothing said where); and
        /// a helicopter's auto-hover turned on or off (it changes the handling: a click in the hands and a word). Vehicles scout, 2026-11-15.
        /// </summary>
        private static void SaySeat(VRRig rig, Seat seat)
        {
            if (!ReferenceEquals(seat, _saidSeat))
            {
                bool sameVehicle = _saidSeat != null && seat != null && _saidSeat.vehicle == seat.vehicle;
                _saidSeat = seat;
                if (sameVehicle)
                {
                    int i = seat.vehicle.seats.IndexOf(seat);
                    string kind = seat.IsDriverSeat() ? "Driver" : seat.HasAnyMountedWeapons() ? "Gunner" : "Passenger";
                    UI.UISystem.Instance?.Notice?.Show($"{kind} seat ({i + 1} of {seat.vehicle.seats.Count})", 1.5f);
                    foreach (VRHand hand in rig.Hands)
                    {
                        if (hand != null && hand.Tracked)
                        {
                            hand.Haptic(0.02f, 160f, 0.35f);
                        }
                    }
                }
            }
            FpsActorController player = FpsActorController.instance;
            bool? hover = player != null && seat != null && seat.vehicle is Helicopter && seat.IsDriverSeat() ? player.helicopterAutoHoverEnabled : (bool?)null;
            if (hover != null && _hoverWas != null && hover != _hoverWas)
            {
                UI.UISystem.Instance?.Notice?.Show(hover.Value ? "Auto-hover on" : "Auto-hover off", 1.5f);
                foreach (VRHand hand in rig.Hands)
                {
                    if (hand != null && hand.Tracked)
                    {
                        hand.Haptic(0.03f, 120f, 0.5f);
                    }
                }
            }
            _hoverWas = hover;
        }

        /// <summary>
        /// The vehicle burning (it will blow up): a slow pulse in both hands every 1.2 s until it is left or put out. A closed
        /// vehicle's inside also flashes red; an open one (a car, a helicopter) had nothing to say it (vehicles scout, 2026-11-15).
        /// </summary>
        private static void Burning(VRRig rig, Vehicle v)
        {
            if (!v.burning || v.dead || Time.unscaledTime < _nextBurnPulse)
            {
                return;
            }
            _nextBurnPulse = Time.unscaledTime + 1.2f;
            foreach (VRHand hand in rig.Hands)
            {
                if (hand != null && hand.Tracked)
                {
                    hand.Haptic(0.12f, 45f, 0.7f);
                }
            }
        }

        /// <summary>
        /// The engine, faintly, in a hand on the wheel or the flight stick only: a tenth of a second ten times a second, its strength by
        /// the engine's power (idle almost nothing). Not in a hand that is free, nor with nothing running.
        /// </summary>
        private static void Hum(VRRig rig, Vehicle v)
        {
            if (Time.unscaledTime < _rumbleUntil)
            {
                return; // (a hit or a bump plays: not covered by the hum, which the controller would let win as the stronger)
            }
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
            // The hum's note from the engine's own sound (invention 5, 2026-11-14): its pitch is what the game plays for the revs, so a
            // revving tank and an idling truck feel apart, as they sound apart; by the power alone where it has no sound playing.
            AudioSource sound = v.engine.audio;
            float frequency = sound != null && sound.isPlaying && sound.pitch > 0.01f
                ? 30f + 55f * Mathf.InverseLerp(0.6f, 1.8f, sound.pitch)
                : 35f + 40f * power;
            foreach (VRHand hand in rig.Hands)
            {
                // (Not on a Touch controller: its haptics have a floor of 0.35 and one pitch, and the faint hum was a strong constant
                // buzz there; bumps and hits still come. Vehicle feel scout, 2026-11-14.)
                if (hand != null && hand.Tracked && hand.Role == HandRole.Cockpit && !Input.VRInput.HandIsTouch(hand.Side))
                {
                    hand.Haptic(0.1f, frequency, 0.04f + 0.08f * power);
                }
            }
        }

        private static float _hitAt = -10f, _hitStrength, _rumbleUntil = -10f;

        private static void Rumble(VRRig rig, float seconds, float frequency, float amplitude)
        {
            _rumbleUntil = Mathf.Max(_rumbleUntil, Time.unscaledTime + seconds); // (the hum waits: its pulse dropped a weaker hit that came after)
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
