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
            _watching = true;
            VRManager.Instance.StartCoroutine(SpinWatch());
            // The player goes from seat to seat (the next vehicle's included) and never stands among them: stood up beside each, a
            // vehicle made next knocked him over and he fell under the map (the user, 2026-11-15: "i got bugged under the map or in the
            // water ... the vehicles could have pushed me down"). Each vehicle is held still (kinematic): it neither tips nor pushes.
            Vector3 start = player.Position();
            Vehicle previous = null;
            int n = 0;
            foreach ((GameObject prefab, string from) in prefabs)
            {
                n++;
                if (player.dead)
                {
                    report("vehicles: the player DIED: stopped");
                    break;
                }
                Vehicle v = null;
                try
                {
                    // Made ahead of where he started, by its own size: its nearest side 8 m off (the last one, which he sits in, is
                    // taken away only once he is in the next).
                    GameObject made = UnityEngine.Object.Instantiate(prefab, start + fwd * 40f + Vector3.up * 30f, Quaternion.LookRotation(fwd, Vector3.up));
                    v = made.GetComponent<Vehicle>();
                    Rigidbody body = made.GetComponent<Rigidbody>();
                    if (body != null)
                    {
                        body.isKinematic = true;
                    }
                    Bounds bb = new Bounds(made.transform.position, Vector3.zero);
                    foreach (Renderer r in made.GetComponentsInChildren<Renderer>())
                    {
                        bb.Encapsulate(r.bounds);
                    }
                    float size = Mathf.Min(Mathf.Max(bb.extents.x, bb.extents.z), 25f);
                    Vector3 to = start + fwd * (8f + size) + Vector3.Cross(Vector3.up, fwd) * ((n % 2 == 0 ? 1f : -1f) * (4f + size));
                    float lift = made.transform.position.y - bb.min.y + 0.05f;
                    if (Physics.Raycast(to + Vector3.up * 60f, Vector3.down, out RaycastHit g2, 160f, ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 12), QueryTriggerInteraction.Ignore))
                    {
                        to = g2.point + Vector3.up * lift;
                    }
                    made.transform.position = to;
                    if (body != null)
                    {
                        body.position = to;
                    }
                }
                catch (Exception e)
                {
                    report($"vehicle {n} {prefab.name}: could not be made: {e.Message}");
                    continue;
                }
                yield return new WaitForSecondsRealtime(0.5f);
                if (v == null || v.seats == null || v.seats.Count == 0)
                {
                    report($"vehicle {n} {prefab.name} ({from}): no seats");
                    if (v != null)
                    {
                        Remove(v);
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
                // Out of the last vehicle first, and it taken away (a seat of another vehicle entered from a seat threw in the game:
                // its camera switched, he stayed in the old seat). He stands by the last one's place, the other side from this one.
                if (player.IsSeated())
                {
                    player.LeaveSeat(false);
                    yield return new WaitForSecondsRealtime(0.2f);
                }
                if (previous != null)
                {
                    Remove(previous);
                    previous = null;
                }
                foreach (int si in seats)
                {
                    yield return SitAndLook(v, si, $"vehicle-{tag}-seat{si + 1}", $"{prefab.name} ({from}) seat {si + 1} of {v.seats.Count}", report);
                }
                previous = v;
                if (player.fallenOver)
                {
                    report("vehicles: the player was KNOCKED OVER");
                }
            }
            if (player.IsSeated())
            {
                player.LeaveSeat(false);
                yield return new WaitForSecondsRealtime(0.3f);
            }
            if (previous != null)
            {
                Remove(previous);
            }
            _watching = false;
            report($"vehicles: the view's biggest turn in half a second: {_spinMax:0} deg{(_spinMax > 120f ? " (A SPIN, during " + _spinAt + ")" : "")}");
            report("vehicles: done");
        }

        private static bool _watching;
        private static float _spinMax;
        private static string _spinAt = "", _step = "";

        /// <summary>The view's biggest turn within half a second over the run (a seat entered or left turns it once, to the seat's way).</summary>
        private static IEnumerator SpinWatch()
        {
            var yaws = new Queue<(float t, float yaw)>();
            float total = 0f, last = float.NaN;
            while (_watching)
            {
                Rig.VRRig rig = Rig.VRRig.Instance;
                if (rig != null && rig.Head != null)
                {
                    Vector3 f = rig.Head.forward;
                    float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
                    if (!float.IsNaN(last) && Mathf.Abs(Mathf.DeltaAngle(last, yaw)) < 45f)
                    {
                        total += Mathf.DeltaAngle(last, yaw); // (unwrapped: a full turn adds up to 360; a seat's one-frame cut left out)
                    }
                    last = yaw;
                    yaws.Enqueue((Time.unscaledTime, total));
                    while (yaws.Count > 0 && Time.unscaledTime - yaws.Peek().t > 0.5f)
                    {
                        yaws.Dequeue();
                    }
                    foreach ((float t, float y) in yaws)
                    {
                        float turn = Mathf.Abs(total - y);
                        if (turn > _spinMax)
                        {
                            _spinMax = turn;
                            _spinAt = _step;
                        }
                    }
                }
                yield return null;
            }
        }

        /// <summary>A made vehicle taken out the game's way (its lists let go first: deleted alone, the game's vehicle checks threw).</summary>
        private static void Remove(Vehicle v)
        {
            try
            {
                ActorManager.DropVehicle(v);
            }
            catch (Exception)
            {
            }
            UnityEngine.Object.Destroy(v.gameObject);
        }

        private static string Clean(string s) => new string(s.Where(c => char.IsLetterOrDigit(c)).Take(20).ToArray());

        private static IEnumerator SitAndLook(Vehicle v, int seatIndex, string name, string what, Action<string> report)
        {
            Actor player = LocalPlayer.actor;
            Seat seat = v.seats[seatIndex];
            _step = what;
            for (float until = Time.realtimeSinceStartup + 4f; !player.CanEnterSeat() && Time.realtimeSinceStartup < until;)
            {
                yield return null; // (the game's wait after leaving a seat: entered at once, a seat now and then refused the player)
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
            if (!seated && player.IsSeated() && player.seat != seat)
            {
                // (Refused from another seat: out of it first, the game's wait, again.)
                player.LeaveSeat(false);
                for (float until = Time.realtimeSinceStartup + 4f; !player.CanEnterSeat() && Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                }
                seated = player.EnterSeat(seat, true, true);
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
            if (seat.IsDriverSeat() && rig.Cockpit != null && rig.Cockpit.RimWorld(out _, out _, out _))
            {
                yield return WheelCheck(name, what, report);
            }
            if (Rig.VehicleInterior.Instance != null && Rig.VehicleInterior.Instance.Shown)
            {
                yield return InteriorCheck(v, seat, name, what, report);
            }
            if (Rig.TurretGrip.Grabbable(seat, out Weapon gun))
            {
                yield return GrabCheck(seat, gun, name, what, report);
            }
            else if (seat.HasAnyMountedWeapons() && !seat.enclosed && seat.activeWeapon != null)
            {
                Transform pv = Rig.TurretGrip.Pivot(seat.activeWeapon);
                float reach = pv != null && rig.Head != null ? Vector3.Distance(pv.position, rig.Head.position) : -1f;
                report($"{what}: its gun ({seat.activeWeapon.GetType().Name}) is not taken by hand ({(pv == null ? "no turning parts" : seat.activeWeapon.overrideCamera != null ? "aimed through its own sight" : $"its pivot {reach:0.0} m from the eyes")})");
            }
        }

        /// <summary>The driver's wheel: the hand on its rim (at the top), turned 60 degrees round it: the steering and the drawn wheel follow.</summary>
        private static IEnumerator WheelCheck(string name, string what, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            rig.Cockpit.RimWorld(out Vector3 centre, out Vector3 axis, out float radius);
            Rig.VRHand hand = rig.Dominant;
            int side = (int)hand.Side;
            Vector3 up = Vector3.ProjectOnPlane(rig.Root.up, axis).normalized;
            if (up.sqrMagnitude < 0.5f)
            {
                up = Vector3.ProjectOnPlane(Vector3.forward, axis).normalized;
            }
            Vector3 palmToRaw = hand.Raw.position - hand.Palm.position;
            Quaternion rot = hand.Raw.rotation;
            Rig.VRHand.Scripted[side] = new Runtime.RigidPose(centre + up * radius + palmToRaw, rot);
            yield return null;
            yield return null;
            Input.VRInput.SimulateGrabPress(hand.Side);
            yield return new WaitForSecondsRealtime(0.3f);
            bool taken = hand.Role == Rig.HandRole.Cockpit;
            for (float t = 0f; t < 0.8f; t += Time.unscaledDeltaTime)
            {
                Vector3 at = centre + Quaternion.AngleAxis(-60f * Mathf.Clamp01(t / 0.8f), axis) * up * radius;
                Rig.VRHand.Scripted[side] = new Runtime.RigidPose(at + palmToRaw, rot);
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.3f);
            float steer = Input.VehicleInput.WheelSteer ?? 0f;
            Shots.Capture(name + "-wheel-turned");
            Input.VRInput.SimulatedGrab[side] = false;
            Rig.VRHand.Scripted[side] = null;
            yield return new WaitForSecondsRealtime(0.3f);
            report($"{what}: wheel (radius {radius:0.00} m): {(taken ? "taken" : "NOT TAKEN")}; turned 60 deg: steering {steer:+0.00;-0.00}{(Mathf.Abs(steer) < 0.1f ? " (DID NOT STEER)" : "")}");
        }

        /// <summary>
        /// A closed vehicle's inside: pictured to the left and behind; the head turned 40 degrees moves no gun; the traverse handwheel
        /// cranked half a turn turns the turret; the alarm pictured with the vehicle burning for a moment.
        /// </summary>
        private static IEnumerator InteriorCheck(Vehicle v, Seat seat, string name, string what, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            Transform head = rig.Head;
            Vector3 hf = v.transform.forward;
            Shots.Capture(name + "-inside-left", head.position, Quaternion.LookRotation(-v.transform.right + hf * 0.3f, Vector3.up), 100f);
            yield return null;
            Shots.Capture(name + "-inside-back", head.position, Quaternion.LookRotation(-hf, Vector3.up), 100f);
            yield return null;
            Weapon gun = seat.activeWeapon is MountedTurret || seat.activeWeapon is MountedStabilizedTurret || seat.activeWeapon is TankTurret ? seat.activeWeapon : null;
            string line = $"{what}: inside shown, hull solid from inside ({(rig.Hull != null ? rig.Hull.Twins : 0)} inward twins, {(rig.Hull != null ? rig.Hull.DrawnBack : 0)} drawn back side out)";
            if (gun != null)
            {
                Transform aim = Rig.TurretGrip.AimReference(gun);
                float Yaw()
                {
                    Vector3 d = v.transform.InverseTransformDirection(aim.forward);
                    return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
                }
                // Nothing touched for 1.2 s: the gun stays (the head is never read for it).
                float y0 = Yaw();
                yield return new WaitForSecondsRealtime(1.2f);
                float headMoved = Mathf.DeltaAngle(y0, Yaw());
                // The traverse wheel cranked half a turn by the gun hand.
                Transform wheel = Rig.VehicleInterior.Instance.TraverseWheel;
                Rig.VRHand hand = rig.Dominant;
                int side = (int)hand.Side;
                Vector3 palmToRaw = hand.Raw.position - hand.PalmCentre;
                Quaternion rot = hand.Raw.rotation;
                float y1 = Yaw();
                Vector3 hubAt = wheel.parent.InverseTransformPoint(wheel.position);
                Quaternion hubTurn = Quaternion.Inverse(wheel.parent.rotation) * wheel.rotation; // (fixed now: the wheel itself turns as cranked)
                for (float t = 0f; t <= 1.2f; t += Time.unscaledDeltaTime)
                {
                    float a = (t < 0.2f ? 0f : Mathf.Clamp01((t - 0.2f) / 1f) * 180f);
                    Vector3 rim = wheel.parent.TransformPoint(hubAt + hubTurn * (Quaternion.Euler(0f, 0f, -a) * new Vector3(0f, 0.11f, 0f)));
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(rim + palmToRaw, rot);
                    if (t > 0.1f && !Input.VRInput.SimulatedGrab[side])
                    {
                        Input.VRInput.SimulateGrabPress(hand.Side);
                    }
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(1.5f);
                Input.VRInput.SimulatedGrab[side] = false;
                Rig.VRHand.Scripted[side] = null;
                float cranked = Mathf.DeltaAngle(y1, Yaw());
                line += $"; nothing touched: the gun {(Mathf.Abs(headMoved) < 3f ? "stayed" : $"MOVED {headMoved:+0;-0} deg")}; the traverse wheel half a turn: the turret turned {cranked:+0;-0} deg{(Mathf.Abs(cranked) < 10f ? " (DID NOT TURN)" : "")}";
            }
            Rig.VehicleInterior.TestAlarm = true;
            yield return new WaitForSecondsRealtime(0.2f);
            Shots.Capture(name + "-inside-alarm");
            yield return new WaitForSecondsRealtime(0.15f);
            Shots.Capture(name + "-inside-alarm2");
            Rig.VehicleInterior.TestAlarm = false;
            report(line + "; the alarm pictured");
        }

        /// <summary>
        /// The gun taken by a handle, the hand swung 20 cm to the right (or turned 25 degrees, handles under the gun), let go, moved again:
        /// held it turns the way a rigid gun round its pivot does, let go it stays. Then, for a gun reloaded by hand: emptied, left alone
        /// (no reload of its own), its cocking handle pulled back by the other hand.
        /// </summary>
        private static IEnumerator GrabCheck(Seat seat, Weapon gun, string name, string what, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            Rig.TurretGrip grip = Rig.TurretGrip.Instance;
            yield return null; // (the grip finds the gun and its handles on its update)
            List<Vector3> handles = grip != null ? grip.HandlesWorld() : new List<Vector3>();
            if (handles.Count == 0)
            {
                report($"{what}: grab: NO HANDLES found");
                yield break;
            }
            Rig.VRHand hand = rig.Dominant;
            int side = (int)hand.Side;
            Transform muzzle = Rig.TurretGrip.AimReference(gun);
            Transform realMuzzle = gun.CurrentMuzzle() != null ? gun.CurrentMuzzle() : gun.transform;
            report($"{what}: grab: the gun's muzzle part {Vector3.Angle(realMuzzle.forward, muzzle.forward):0} deg off the way it is steered");
            Transform frame = seat.vehicle.transform;
            float Yaw()
            {
                Vector3 d = frame.InverseTransformDirection(muzzle.forward);
                return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg; // (positive: to the vehicle's right)
            }
            Vector3 handle = handles[handles.Count - 1];
            Transform pivot = Rig.TurretGrip.Pivot(gun);
            Vector3 right = frame.right;
            Vector3 palmToRaw = hand.Raw.position - hand.Palm.position;
            Quaternion rot = hand.Raw.rotation;
            Rig.VRHand.Scripted[side] = new Runtime.RigidPose(handle + palmToRaw, rot);
            yield return null;
            yield return null;
            // Which way it should turn, from where the palm really is (the grip swings the gun by the palm, not the handle's point).
            Vector3 arm = Vector3.ProjectOnPlane(frame.InverseTransformPoint(hand.Palm.position) - frame.InverseTransformPoint(pivot.position), Vector3.up);
            Vector3 aimFlat = Vector3.ProjectOnPlane(frame.InverseTransformDirection(muzzle.forward), Vector3.up).normalized;
            bool under = arm.magnitude < 0.2f;
            float expect = under ? 1f : Mathf.Sign(Vector3.Dot(arm, aimFlat)); // (behind the pivot (negative): the hand right turns it left)
            Input.VRInput.SimulateGrabPress(hand.Side);
            yield return new WaitForSecondsRealtime(0.4f);
            bool taken = grip.Held;
            float yaw0 = Yaw();
            for (float t = 0f; t < 0.6f; t += Time.unscaledDeltaTime)
            {
                float k = Mathf.Clamp01(t / 0.6f);
                Rig.VRHand.Scripted[side] = under
                    ? new Runtime.RigidPose(handle + palmToRaw, Quaternion.AngleAxis(25f * k, frame.up) * rot)
                    : new Runtime.RigidPose(handle + right * (0.2f * k) + palmToRaw, rot);
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.8f);
            float yawHeld = Yaw();
            Vector3? want = grip.WantedWorld;
            float wantYaw = want != null ? Mathf.Atan2(frame.InverseTransformDirection(want.Value).x, frame.InverseTransformDirection(want.Value).z) * Mathf.Rad2Deg : float.NaN;
            report($"{what}: grab numbers: palm {Vector3.Dot(arm, aimFlat):+0.00;-0.00} m along the barrel from the pivot, {arm.magnitude:0.00} m off it; the hands want {Mathf.DeltaAngle(yaw0, wantYaw):+0;-0} deg, the gun went {Mathf.DeltaAngle(yaw0, yawHeld):+0;-0} deg");
            Shots.Capture(name + "-held");
            Input.VRInput.SimulatedGrab[side] = false;
            yield return new WaitForSecondsRealtime(0.3f);
            bool letGo = !grip.Held;
            float yawLet = Yaw();
            for (float t = 0f; t < 0.6f; t += Time.unscaledDeltaTime)
            {
                Rig.VRHand.Scripted[side] = new Runtime.RigidPose(handle - right * (0.2f * Mathf.Clamp01(t / 0.6f)) + palmToRaw, rot);
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.8f);
            float yawAfter = Yaw();
            Rig.VRHand.Scripted[side] = null;
            float turned = Mathf.DeltaAngle(yaw0, yawHeld), drift = Mathf.DeltaAngle(yawLet, yawAfter);
            bool right_way = turned * expect > 4f;
            report($"{what}: grab ({(grip.MadeHandles ? "handles made from the model" : "the seat's handles")}, {(under ? "under the gun: the hand turned 25 deg" : expect < 0 ? "behind the pivot: the hand 20 cm right" : "in front of the pivot: the hand 20 cm right")}): "
                   + $"{(taken ? "taken" : "NOT TAKEN")}; turned it {turned:+0;-0} deg {(right_way ? "(the right way)" : "(NOT THE RIGHT WAY)")}; "
                   + $"{(letGo ? "let go" : "NOT LET GO")}, the hand moved again: {(Mathf.Abs(drift) < 2f ? "it stayed" : $"IT MOVED {drift:+0;-0} deg")}");
            if (!Rig.TurretGrip.ReloadsByHand(gun))
            {
                report($"{what}: reload: {(gun.configuration != null && gun.configuration.ammo <= 0 ? "none needed (no magazine)" : "the game's (not Manual reload)")}");
                yield break;
            }
            // Reload by hand: emptied; no reload of its own in 2.5 s; the other hand on the cocking handle, pulled 10 cm back.
            gun.ammo = 0;
            yield return new WaitForSecondsRealtime(2.5f);
            bool waited = gun.ammo == 0 && !gun.reloading;
            Rig.VRHand other = rig.Hands[1 - side];
            int os = (int)other.Side;
            Vector3 cock = grip.CockPointWorld;
            Vector3 back = -muzzle.forward;
            Vector3 otherToRaw = other.Raw.position - other.Palm.position;
            Quaternion orot = other.Raw.rotation;
            Rig.VRHand.Scripted[os] = new Runtime.RigidPose(cock + otherToRaw, orot);
            yield return null;
            yield return null;
            Input.VRInput.SimulateGrabPress(other.Side);
            yield return new WaitForSecondsRealtime(0.3f);
            for (float t = 0f; t < 0.4f; t += Time.unscaledDeltaTime)
            {
                Rig.VRHand.Scripted[os] = new Runtime.RigidPose(cock + back * (0.1f * Mathf.Clamp01(t / 0.4f)) + otherToRaw, orot);
                yield return null;
            }
            yield return new WaitForSecondsRealtime(0.3f);
            bool reloading = gun.reloading || gun.ammo > 0;
            float cockGap = Vector3.Distance(other.PalmCentre, grip.CockPointWorld);
            Input.VRInput.SimulatedGrab[os] = false;
            Rig.VRHand.Scripted[os] = null;
            float until2 = Time.realtimeSinceStartup + 8f;
            while (gun.reloading && Time.realtimeSinceStartup < until2)
            {
                yield return null;
            }
            report($"{what}: reload by hand: emptied, {(waited ? "waited for the hand" : "RELOADED BY ITSELF")}; the cocking handle pulled: {(reloading ? $"reloaded ({gun.ammo} of {gun.configuration.ammo})" : $"NO RELOAD (spare {gun.spareAmmo}{(gun.HasInfiniteSpareAmmo() ? ", infinite" : "")}, the palm {cockGap:0.00} m from the handle)")}");
        }
    }
}
