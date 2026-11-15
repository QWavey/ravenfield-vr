using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// -rfvr-vehicleshots (with -rfvr-autotest -vr): every vehicle and turret the game has (its own and the loaded mods'), each made
    /// in front of the player, the VR player put in its seats (the driver's, each gunner's, the first passenger's) and pictured as the
    /// headset sees it; each driver seat's controls reported (a steering wheel the hands can turn, a flight stick); each open mounted
    /// gun taken by its handles, swung, let go (the user, 2026-11-15: "let me actually grab the standed things ... when i hold on them
    /// it moves with my hand but if i dont hold on them it doesnt move"; "you didnt do all vehicles"; "make also sure all steering
    /// wheels are turneable").
    /// </summary>
    internal static class VehicleShots
    {
        public static readonly bool Requested = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-vehicleshots", StringComparison.OrdinalIgnoreCase));

        internal static string Kind(Vehicle v)
        {
            if (v is Helicopter)
            {
                return "helicopter";
            }
            if (v is Airplane || v is Plane)
            {
                return "plane";
            }
            if (v is Tank)
            {
                return "tank";
            }
            if (v is ArcadeCar || v is Car)
            {
                return "car";
            }
            if (v is Boat)
            {
                return "boat";
            }
            return v.GetType().Name.ToLowerInvariant();
        }

        /// <summary>Every vehicle and turret prefab there is: the game's own, then the loaded mods' (by the mod manager's lists).</summary>
        private static List<(GameObject prefab, string from)> AllPrefabs()
        {
            var list = new List<(GameObject, string)>();
            var seen = new HashSet<GameObject>();
            void Add(GameObject g, string from)
            {
                if (g != null && seen.Add(g) && g.GetComponent<Vehicle>() != null)
                {
                    list.Add((g, from));
                }
            }
            foreach (GameObject g in ActorManager.instance.defaultVehiclePrefabs ?? new GameObject[0])
            {
                Add(g, "game");
            }
            foreach (GameObject g in ActorManager.instance.defaultTurretPrefabs ?? new GameObject[0])
            {
                Add(g, "game turret");
            }
            if (ModManager.instance != null)
            {
                foreach (string field in new[] { "vehiclePrefabs", "turretPrefabs" })
                {
                    try
                    {
                        if (HarmonyLib.AccessTools.Field(typeof(ModManager), field)?.GetValue(ModManager.instance) is System.Collections.IDictionary d)
                        {
                            foreach (System.Collections.DictionaryEntry e in d)
                            {
                                Add(e.Key as GameObject ?? e.Value as GameObject, "mod");
                                if (e.Value is IEnumerable<GameObject> many)
                                {
                                    foreach (GameObject g in many)
                                    {
                                        Add(g, "mod");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            return list;
        }

        private static string Controls(Seat seat)
        {
            Vehicle v = seat.vehicle;
            SteeringWheel own = v.GetComponentsInChildren<SteeringWheel>(true).FirstOrDefault(w => w.driverSeat == seat);
            int wheels = v.GetComponentsInChildren<SteeringWheel>(true).Length;
            bool stick = v.GetComponentsInChildren<Joystick>(true).Any(j => j.driverSeat == seat);
            string kind = Kind(v);
            if (own != null)
            {
                return "steering wheel (the hands turn it)";
            }
            if (v.GetComponentsInChildren<SteeringWheel>(true).Any(w => w.driverSeat == null && Vector3.Distance(w.transform.position, seat.transform.position) < 1.5f))
            {
                return "steering wheel, linked to no seat: taken as the driver's (the hands turn it)";
            }
            if (wheels > 0)
            {
                return $"a steering wheel NOT LINKED to this seat ({wheels} in the vehicle)";
            }
            if (stick)
            {
                return "flight stick";
            }
            if (kind == "helicopter" || kind == "plane")
            {
                return "flight stick at the hand rest (no stick of its own)";
            }
            // (Handlebars and boat helms are drawn without the game's wheel: whether one is in the model, by name.)
            string bars = v.GetComponentsInChildren<Transform>(true).Select(t => t.name).FirstOrDefault(n =>
                n.IndexOf("steer", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("handlebar", StringComparison.OrdinalIgnoreCase) >= 0
                || n.IndexOf("helm", StringComparison.OrdinalIgnoreCase) >= 0);
            return bars != null ? $"NO STEERING WHEEL linked (the model has '{bars}')" : "no wheel or stick (sticks and keys)";
        }

        internal static IEnumerator Run(Action<string> report)
        {
            Actor player = LocalPlayer.actor;
            Rig.VRRig rig0 = Rig.VRRig.Instance;
            if (player == null || ActorManager.instance == null || rig0 == null)
            {
                report("vehicles: NO PLAYER");
                yield break;
            }
            List<(GameObject prefab, string from)> prefabs = AllPrefabs();
            report($"vehicles: {prefabs.Count} vehicle and turret kinds ({prefabs.Count(p => p.from == "game")} the game's, {prefabs.Count(p => p.from == "game turret")} turrets, {prefabs.Count(p => p.from == "mod")} from mods)");
            Vector3 fwd = Vector3.ProjectOnPlane(rig0.Head.forward, Vector3.up).normalized;
            Vector3 place = player.Position() + fwd * 14f;
            int n = 0;
            foreach ((GameObject prefab, string from) in prefabs)
            {
                n++;
                if (player.dead)
                {
                    report("vehicles: the player died: stopped");
                    yield break;
                }
                if (player.IsSeated())
                {
                    player.LeaveSeat(false);
                    yield return new WaitForSecondsRealtime(0.4f);
                }
                Vector3 at = place;
                if (Physics.Raycast(at + Vector3.up * 50f, Vector3.down, out RaycastHit g, 120f, ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 12), QueryTriggerInteraction.Ignore))
                {
                    at = g.point + Vector3.up * 1.2f;
                }
                Vehicle v = null;
                try
                {
                    GameObject made = UnityEngine.Object.Instantiate(prefab, at, Quaternion.LookRotation(fwd, Vector3.up));
                    v = made.GetComponent<Vehicle>();
                }
                catch (Exception e)
                {
                    report($"vehicle {n} {prefab.name}: could not be made: {e.Message}");
                    continue;
                }
                yield return new WaitForSecondsRealtime(0.6f);
                if (v == null || v.seats == null || v.seats.Count == 0)
                {
                    report($"vehicle {n} {prefab.name} ({from}): no seats");
                    if (v != null)
                    {
                        UnityEngine.Object.Destroy(v.gameObject);
                    }
                    continue;
                }
                string tag = $"{n:00}-{Kind(v)}-{Clean(prefab.name)}";
                // The seats looked at: the driver's, every one with a gun, the first other one.
                var seats = new List<int> { 0 };
                for (int i = 1; i < v.seats.Count; i++)
                {
                    if (v.seats[i].HasAnyMountedWeapons() && seats.Count < 4)
                    {
                        seats.Add(i);
                    }
                }
                int passenger = Enumerable.Range(1, v.seats.Count - 1).FirstOrDefault(i => !seats.Contains(i));
                if (passenger > 0)
                {
                    seats.Add(passenger);
                }
                foreach (int si in seats)
                {
                    yield return SitAndLook(v, si, $"vehicle-{tag}-seat{si + 1}", $"{prefab.name} ({from}) seat {si + 1} of {v.seats.Count}", report);
                }
                if (player.IsSeated())
                {
                    player.LeaveSeat(false);
                    yield return new WaitForSecondsRealtime(0.3f);
                }
                UnityEngine.Object.Destroy(v.gameObject);
                yield return new WaitForSecondsRealtime(0.3f);
            }
            report("vehicles: done");
        }

        private static string Clean(string s) => new string(s.Where(c => char.IsLetterOrDigit(c)).Take(20).ToArray());

        private static IEnumerator SitAndLook(Vehicle v, int seatIndex, string name, string what, Action<string> report)
        {
            Actor player = LocalPlayer.actor;
            Seat seat = v.seats[seatIndex];
            if (player.IsSeated())
            {
                player.LeaveSeat(false);
                yield return new WaitForSecondsRealtime(0.3f);
            }
            bool seated;
            try
            {
                seated = player.EnterSeat(seat, true, true);
            }
            catch (Exception e)
            {
                report($"{what}: the seat threw: {e.Message}");
                yield break;
            }
            yield return new WaitForSecondsRealtime(1.6f);
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (!seated || !player.IsSeated() || rig == null || rig.Head == null)
            {
                report($"{what}: NOT SEATED");
                yield break;
            }
            Transform head = rig.Head;
            Vector3 local = seat.transform.InverseTransformPoint(head.position);
            string guns = seat.weapons != null && seat.weapons.Length > 0 ? string.Join("+", seat.weapons.Where(w => w != null).Select(w => w.GetType().Name)) : "none";
            string role = seat.IsDriverSeat() ? "driver: " + Controls(seat) : guns != "none" ? "gunner" : "passenger";
            report($"{what}: {role}; guns {guns}{(seat.enclosed ? ", enclosed" : "")}; head {local.y:0.00} m above the seat, {new Vector2(local.x, local.z).magnitude:0.00} m off its centre");
            Vector3 vfwd = v.transform.forward;
            Shots.Capture(name + "-ahead");
            yield return null;
            Shots.Capture(name + "-down", head.position, Quaternion.LookRotation(vfwd * 0.5f + Vector3.down, vfwd), 100f);
            yield return null;
            if (Rig.TurretGrip.Grabbable(seat, out Weapon gun))
            {
                yield return GrabCheck(seat, gun, name, what, report);
            }
            else if (seat.HasAnyMountedWeapons() && !seat.enclosed && seat.activeWeapon != null)
            {
                report($"{what}: its gun ({seat.activeWeapon.GetType().Name}) is aimed by the head (no handles to take: {(seat.handTargetL == null && seat.handTargetR == null ? "no hand targets" : "not a mounted turret")})");
            }
        }

        /// <summary>The gun taken by a handle, the hand swung 20 cm to the right, let go, the hand moved again: held it turns, let go it stays.</summary>
        private static IEnumerator GrabCheck(Seat seat, Weapon gun, string name, string what, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            Rig.VRHand hand = rig.Dominant;
            Transform handle = seat.handTargetR != null ? seat.handTargetR : seat.handTargetL;
            int side = (int)hand.Side;
            Transform muzzle = gun.CurrentMuzzle() != null ? gun.CurrentMuzzle() : gun.transform;
            Transform frame = seat.vehicle.transform;
            float Yaw()
            {
                Vector3 d = frame.InverseTransformDirection(muzzle.forward);
                return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; // (positive: to the vehicle's right)
            }
            Vector3 right = frame.right;
            Vector3 palmToRaw = hand.Raw.position - hand.Palm.position;
            Quaternion rot = hand.Raw.rotation;
            Rig.VRHand.Scripted[side] = new Runtime.RigidPose(handle.position + palmToRaw, rot);
            yield return null;
            yield return null;
            Input.VRInput.SimulateGrabPress(hand.Side);
            yield return new WaitForSecondsRealtime(0.4f);
            bool taken = Rig.TurretGrip.Instance != null && Rig.TurretGrip.Instance.Held;
            float yaw0 = Yaw();
            Vector3 start = handle.position;
            for (float t = 0f; t < 0.6f; t += Time.unscaledDeltaTime)
            {
                Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start + right * (0.2f * t / 0.6f) + palmToRaw, rot);
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.8f);
            float yawHeld = Yaw();
            Shots.Capture(name + "-held");
            Input.VRInput.SimulatedGrab[side] = false;
            yield return new WaitForSecondsRealtime(0.3f);
            bool letGo = Rig.TurretGrip.Instance == null || !Rig.TurretGrip.Instance.Held;
            float yawLet = Yaw();
            for (float t = 0f; t < 0.6f; t += Time.unscaledDeltaTime)
            {
                Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start - right * (0.2f * t / 0.6f) + palmToRaw, rot);
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.8f);
            float yawAfter = Yaw();
            Rig.VRHand.Scripted[side] = null;
            float turned = Mathf.DeltaAngle(yaw0, yawHeld), drift = Mathf.DeltaAngle(yawLet, yawAfter);
            report($"{what}: grab: {(taken ? "taken by its handle" : "NOT TAKEN by its handle")}; held, the hand 20 cm right turned it {turned:+0;-0} deg "
                   + $"{(turned < -2f ? "(to the left, as a mounted gun does)" : turned > 2f ? "(TO THE RIGHT)" : "(DID NOT TURN)")}; "
                   + $"{(letGo ? "let go" : "NOT LET GO")}, the hand moved again: {(Mathf.Abs(drift) < 2f ? "it stayed" : $"IT MOVED {drift:+0;-0} deg")}");
        }
    }
}
