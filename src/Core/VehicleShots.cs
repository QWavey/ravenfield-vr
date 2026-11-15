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
            // (-rfvr-vehicles=a,b: only the kinds whose name holds one of the words: a quick look at a few.)
            string only = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-rfvr-vehicles=", StringComparison.OrdinalIgnoreCase));
            if (only != null)
            {
                string[] words = only.Substring("-rfvr-vehicles=".Length).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                prefabs = prefabs.Where(p => words.Any(w => p.prefab.name.IndexOf(w.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            }
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
                    if (v != null)
                    {
                        v.maxHealth = v.health = 1e7f; // (the match goes on round it: enemy fire burned a parked VPK with the player in it)
                    }
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
            _watching = false;
            // Last (it kills the player): killed in a seat, the view stays in it for a moment before the game's camera takes it.
            if (player.IsSeated() && !player.dead)
            {
                Rig.VRRig rig = Rig.VRRig.Instance;
                string seatName = player.seat.vehicle.name;
                player.KillSilently();
                float stayed = 0f, killedAt = Time.realtimeSinceStartup;
                bool after = false;
                while (Time.realtimeSinceStartup - killedAt < 3f)
                {
                    if (rig != null && rig.Mode == Rig.RigMode.Anchored)
                    {
                        stayed = Time.realtimeSinceStartup - killedAt;
                    }
                    else if (stayed > 0f)
                    {
                        after = true;
                    }
                    yield return null;
                }
                report($"vehicles: killed in a seat ({seatName}): the view stayed in it {stayed:0.0} s{(stayed < 1f ? " (DID NOT STAY)" : stayed > 2.5f ? " (STUCK IN THE SEAT)" : "")}, then {(after ? "the game's camera" : "NOT HANDED OVER")}");
            }
            else if (player.IsSeated())
            {
                player.LeaveSeat(false);
                yield return new WaitForSecondsRealtime(0.3f);
            }
            if (previous != null)
            {
                Remove(previous);
            }
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
            if (seat.IsDriverSeat() && (v is Helicopter || v is Airplane || v is Plane))
            {
                yield return new WaitForSecondsRealtime(0.3f);
                string read = Rig.VehicleFeel.ReadoutText;
                report($"{what}: pilot readout: {(string.IsNullOrEmpty(read) ? "NOT SHOWN" : "\"" + read + "\"")}");
            }
            if (seat.IsDriverSeat() && rig.Cockpit != null && rig.Cockpit.ThrottleLever != null)
            {
                yield return LeverCheck(seat, what, report);
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

        /// <summary>The plane's throttle or helicopter's collective: the off hand takes it, pushes 6 cm forward, lets go: the setting stays.</summary>
        private static IEnumerator LeverCheck(Seat seat, string what, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            Transform lever = rig.Cockpit.ThrottleLever;
            Rig.VRHand hand = rig.Hands[1 - (int)rig.Dominant.Side];
            int side = (int)hand.Side;
            Vector3 toRaw = hand.Raw.position - hand.Palm.position;
            Quaternion rot = hand.Raw.rotation;
            Vector3 start = lever.position, fwd = seat.transform.forward;
            Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start + toRaw, rot);
            yield return null;
            yield return null;
            Input.VRInput.SimulateGrabPress(hand.Side);
            yield return new WaitForSecondsRealtime(0.3f);
            for (float t = 0f; t < 0.5f; t += Time.unscaledDeltaTime)
            {
                Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start + fwd * (0.06f * Mathf.Clamp01(t / 0.5f)) + toRaw, rot);
                yield return null;
            }
            float held = Input.VehicleInput.Throttle ?? float.NaN;
            Input.VRInput.SimulatedGrab[side] = false;
            Rig.VRHand.Scripted[side] = null;
            yield return new WaitForSecondsRealtime(0.4f);
            float kept = Input.VehicleInput.Throttle ?? float.NaN;
            string kind = seat.vehicle is Helicopter ? "collective" : "throttle";
            report($"{what}: {kind} lever: pushed 6 cm, {(float.IsNaN(held) ? "NOT TAKEN" : $"set {held:+0.00;-0.00}")}; let go: {(float.IsNaN(kept) ? "DROPPED" : $"kept {kept:+0.00;-0.00}")}");
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
            bool onWheel = rig.Cockpit.OnWheel;
            float inner = rig.Cockpit.SteerNow;
            float palmOff = Vector3.Distance(hand.Palm.position, centre);
            Shots.Capture(name + "-wheel-turned");
            Input.VRInput.SimulatedGrab[side] = false;
            Rig.VRHand.Scripted[side] = null;
            yield return new WaitForSecondsRealtime(0.3f);
            report($"{what}: wheel (radius {radius:0.00} m): {(taken ? "taken" : "NOT TAKEN")}; turned 60 deg: steering {steer:+0.00;-0.00}{(Mathf.Abs(steer) < 0.1f ? $" (DID NOT STEER: on the wheel {onWheel}, its steer {inner:+0.00;-0.00}, the palm {palmOff:0.00} m from the centre)" : "")}");
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
            // What is drawn within half a metre before the eyes (a pillar stood there in the tank).
            var near = new List<string>();
            foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
            {
                if (r == null || !r.enabled || !r.isVisible || r.GetComponentInParent<Rig.VRRig>() != null || r.transform.root.name == "VR Rig")
                {
                    continue;
                }
                Vector3 to = r.bounds.ClosestPoint(head.position) - head.position;
                if (to.magnitude < 0.5f && (to.sqrMagnitude < 1e-4f || Vector3.Dot(to.normalized, head.forward) > 0.3f))
                {
                    Transform t = r.transform;
                    string path = t.name;
                    for (int k = 0; k < 2 && t.parent != null; k++)
                    {
                        t = t.parent;
                        path = t.name + "/" + path;
                    }
                    near.Add($"{path} ({r.GetType().Name}, {r.bounds.size.magnitude:0.0} m, layer {r.gameObject.layer})");
                }
            }
            if (rig.Hull != null)
            {
                rig.Hull.ShowParts(false, true);
                yield return null;
                Shots.Capture(name + "-x-no-backs");
                rig.Hull.ShowParts(true, false);
                yield return null;
                Shots.Capture(name + "-x-no-twins");
                rig.Hull.ShowParts(true, true);
                yield return null;
            }
            report($"{what}: drawn before the eyes: {(near.Count == 0 ? "nothing" : string.Join("; ", near.Take(12)))}");
            Shots.Capture(name + "-inside-left", head.position, Quaternion.LookRotation(-v.transform.right + hf * 0.3f, Vector3.up), 100f);
            yield return null;
            Shots.Capture(name + "-inside-back", head.position, Quaternion.LookRotation(-hf, Vector3.up), 100f);
            yield return null;
            Weapon gun = seat.activeWeapon is MountedTurret || seat.activeWeapon is MountedStabilizedTurret || seat.activeWeapon is TankTurret ? seat.activeWeapon : null;
            string scopeState = Rig.VehicleInterior.Instance.SavePeriscope(Shots.FileFor(name + "-periscope"));
            // The sight's zoom knob pressed by the gun hand: x3 (the picture saved), pressed again: back to x1.
            string zoom = "";
            Transform zoomKnob = Rig.VehicleInterior.Instance.ZoomKnob;
            if (zoomKnob != null)
            {
                Rig.VRHand zh = rig.Dominant;
                int zs = (int)zh.Side;
                Vector3 zRaw = zh.Raw.position - zh.PalmCentre;
                bool[] states = new bool[2];
                for (int press = 0; press < 2 && zoomKnob != null; press++)
                {
                    for (float t = 0f; t < 0.35f && zoomKnob != null; t += Time.unscaledDeltaTime)
                    {
                        Rig.VRHand.Scripted[zs] = new Runtime.RigidPose(zoomKnob.position + zRaw, zh.Raw.rotation);
                        yield return null;
                    }
                    Rig.VRHand.Scripted[zs] = null;
                    yield return new WaitForSecondsRealtime(0.25f);
                    states[press] = Rig.VehicleInterior.Instance.Zoomed;
                    if (press == 0)
                    {
                        Rig.VehicleInterior.Instance.SavePeriscope(Shots.FileFor(name + "-periscope-zoom"));
                    }
                }
                zoom = $"; sight zoom knob: {(states[0] ? "x3" : "DID NOT ZOOM")}, again: {(!states[1] ? "x1" : "STAYED ZOOMED")}";
            }
            // The rangefinder's button pressed: the readout shows the range along the gun's line.
            Transform rangeKnob = Rig.VehicleInterior.Instance.RangeButton;
            if (rangeKnob != null)
            {
                Rig.VRHand rh = rig.Dominant;
                int rs = (int)rh.Side;
                Vector3 rRaw = rh.Raw.position - rh.PalmCentre;
                for (float t = 0f; t < 0.3f && rangeKnob != null; t += Time.unscaledDeltaTime)
                {
                    Rig.VRHand.Scripted[rs] = new Runtime.RigidPose(rangeKnob.position + rRaw, rh.Raw.rotation);
                    yield return null;
                }
                Rig.VRHand.Scripted[rs] = null;
                yield return new WaitForSecondsRealtime(0.3f);
                string shown = Rig.VehicleInterior.Instance.Readout;
                float mark = Rig.VehicleInterior.Instance.AimMarkDrop;
                zoom += $"; rangefinder: \"{shown}\"{(shown.StartsWith("RANGE") ? "" : " (NO RANGE SHOWN)")}, aim mark {(float.IsNaN(mark) ? "none" : $"{mark * 1000f:0} mm under the centre")}";
            }
            string line = $"{what}: {scopeState}; periscope picture {Rig.VehicleInterior.Instance.PeriscopeBrightness():0.00} bright{(Rig.VehicleInterior.Instance.PeriscopeBrightness() < 0.05f ? " (BLACK)" : "")}, near plane {Rig.VehicleInterior.Instance.PeriscopeNear:0.0} m; inside shown, hull solid from inside ({(rig.Hull != null ? rig.Hull.Twins : 0)} inward twins, {(rig.Hull != null ? rig.Hull.DrawnBack : 0)} drawn back side out){zoom}";
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
                if (wheel == null || wheel.parent == null)
                {
                    // (With what the seat's check had gathered: its periscope and zoom lines were lost with it.)
                    report(line + "; the inside's traverse wheel is GONE (the inside taken down)");
                    yield break;
                }
                Vector3 hubAt = wheel.parent.InverseTransformPoint(wheel.position);
                Quaternion hubTurn = Quaternion.Inverse(wheel.parent.rotation) * wheel.rotation; // (fixed now: the wheel itself turns as cranked)
                for (float t = 0f; t <= 1.2f; t += Time.unscaledDeltaTime)
                {
                    float a = (t < 0.2f ? 0f : Mathf.Clamp01((t - 0.2f) / 1f) * 180f);
                    if (wheel == null || wheel.parent == null)
                    {
                        break; // (the inside was taken down meanwhile)
                    }
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
                // The elevation wheel half a turn: the gun up or down (a turn is 30 degrees, within the gun's own limits).
                Transform ew = Rig.VehicleInterior.Instance.ElevationWheel;
                if (ew != null && ew.parent != null)
                {
                    float Pitch() => Mathf.Asin(Mathf.Clamp(v.transform.InverseTransformDirection(aim.forward).y, -1f, 1f)) * Mathf.Rad2Deg;
                    float p1 = Pitch();
                    Vector3 eAt = ew.parent.InverseTransformPoint(ew.position);
                    Quaternion eTurn = Quaternion.Inverse(ew.parent.rotation) * ew.rotation;
                    for (float t = 0f; t <= 1.2f; t += Time.unscaledDeltaTime)
                    {
                        if (ew == null || ew.parent == null)
                        {
                            break;
                        }
                        float a = t < 0.2f ? 0f : Mathf.Clamp01((t - 0.2f) / 1f) * 180f;
                        Vector3 rim = ew.parent.TransformPoint(eAt + eTurn * (Quaternion.Euler(0f, 0f, a) * new Vector3(0f, 0.11f, 0f)));
                        Rig.VRHand.Scripted[side] = new Runtime.RigidPose(rim + palmToRaw, rot);
                        if (t > 0.1f && !Input.VRInput.SimulatedGrab[side])
                        {
                            Input.VRInput.SimulateGrabPress(hand.Side);
                        }
                        yield return null;
                    }
                    yield return new WaitForSecondsRealtime(1.2f);
                    Input.VRInput.SimulatedGrab[side] = false;
                    Rig.VRHand.Scripted[side] = null;
                    float raised = Pitch() - p1;
                    line += $"; the elevation wheel half a turn: the gun {raised:+0;-0} deg{(Mathf.Abs(raised) < 3f ? " (DID NOT MOVE)" : "")}";
                }
            }
            // The buttons, pressed by the gun hand: fire (rounds go), forward (the drive input goes forward).
            {
                Rig.VRHand hand = rig.Dominant;
                int side = (int)hand.Side;
                Vector3 toRaw = hand.Raw.position - hand.PalmCentre;
                Quaternion rot = hand.Raw.rotation;
                Transform fire = Rig.VehicleInterior.Instance.FireButton;
                if (fire != null && gun != null)
                {
                    int before = gun.ammo;
                    for (float t = 0f; t < 0.6f; t += Time.unscaledDeltaTime)
                    {
                        Rig.VRHand.Scripted[side] = new Runtime.RigidPose(fire.position + toRaw, rot);
                        yield return null;
                    }
                    bool pressed = Rig.VehicleInterior.Fire;
                    Rig.VRHand.Scripted[side] = null;
                    yield return new WaitForSecondsRealtime(0.3f);
                    line += $"; fire button: {(pressed ? "pressed" : "NOT PRESSED")}, ammo {before} -> {gun.ammo}{(gun.ammo >= before && !gun.reloading ? " (DID NOT FIRE)" : "")}";
                    // A cannon in Manual reload waits empty for its loader: a shell taken from the rack and pushed into the breech loads it.
                    Vector3 shellAt = Rig.VehicleInterior.Instance.FirstShell, mouth = Rig.VehicleInterior.Instance.BreechMouth;
                    if (shellAt != Vector3.zero && mouth != Vector3.zero)
                    {
                        Core.ReloadMode? kept = Core.VRConfig.TestReload;
                        Core.VRConfig.TestReload = Core.ReloadMode.Manual;
                        Rig.VehicleInterior.TestLoadByHand = true;
                        yield return new WaitForSecondsRealtime(1f);
                        bool waited = gun.ammo <= 0 && !gun.reloading;
                        string readout = Rig.VehicleInterior.Instance.Readout;
                        bool took = false;
                        for (float t = 0f; t < 1.4f; t += Time.unscaledDeltaTime)
                        {
                            // (At the shell, gripped; then over 0.8 s its nose to the breech's mouth, the hand a shell's half-length behind.)
                            Vector3 to = Vector3.Lerp(shellAt, mouth - rot * Vector3.forward * 0.3f, Mathf.Clamp01((t - 0.3f) / 0.8f));
                            Rig.VRHand.Scripted[side] = new Runtime.RigidPose(to + toRaw, rot);
                            if (t > 0.1f && !Input.VRInput.SimulatedGrab[side])
                            {
                                Input.VRInput.SimulateGrabPress(hand.Side);
                            }
                            took |= Rig.VehicleInterior.Instance.Carrying;
                            if (t > 0.4f && !Rig.VehicleInterior.Instance.Carrying && gun.ammo > 0)
                            {
                                break; // (rammed home)
                            }
                            yield return null;
                        }
                        Input.VRInput.SimulatedGrab[side] = false;
                        Rig.VRHand.Scripted[side] = null;
                        yield return new WaitForSecondsRealtime(0.3f);
                        Core.VRConfig.TestReload = kept;
                        Rig.VehicleInterior.TestLoadByHand = false;
                        line += $"; loaded by hand: {(waited ? "waited empty" : $"DID NOT WAIT (ammo {gun.ammo}, reloading {gun.reloading})")} (readout \"{readout}\"), shell {(took ? "taken" : "NOT TAKEN")}, gun {(gun.ammo > 0 ? "loaded" : "NOT LOADED")}";
                    }
                }
                Transform forward = Rig.VehicleInterior.Instance.ForwardButton;
                if (forward != null)
                {
                    float drive = 0f;
                    for (float t = 0f; t < 0.4f; t += Time.unscaledDeltaTime)
                    {
                        Rig.VRHand.Scripted[side] = new Runtime.RigidPose(forward.position + toRaw, rot);
                        drive = Mathf.Max(drive, Input.VehicleInput.DriveButtons.y);
                        yield return null;
                    }
                    Rig.VRHand.Scripted[side] = null;
                    yield return new WaitForSecondsRealtime(0.2f);
                    line += $"; forward button: drive {drive:+0.0;-0.0}{(drive < 0.5f ? " (DID NOT DRIVE)" : "")}";
                }
            }
            // The cabin's steering wheel (a wheeled closed vehicle's driver): turned 60 degrees by hand, the steering reads it.
            Transform sw = Rig.VehicleInterior.Instance.CabinSteeringWheel;
            if (sw != null && sw.parent != null)
            {
                Rig.VRHand hand = rig.Dominant;
                int side = (int)hand.Side;
                Vector3 toRaw = hand.Raw.position - hand.PalmCentre;
                Quaternion rot = hand.Raw.rotation;
                Vector3 at = sw.parent.InverseTransformPoint(sw.position);
                Quaternion turn = Quaternion.Inverse(sw.parent.rotation) * sw.rotation;
                float steer = 0f;
                for (float t = 0f; t <= 0.9f; t += Time.unscaledDeltaTime)
                {
                    if (sw == null || sw.parent == null)
                    {
                        break;
                    }
                    float a = t < 0.15f ? 0f : Mathf.Clamp01((t - 0.15f) / 0.6f) * 60f;
                    Vector3 rim = sw.parent.TransformPoint(at + turn * (Quaternion.Euler(0f, 0f, -a) * new Vector3(0f, 0.11f, 0f)));
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(rim + toRaw, rot);
                    if (t > 0.08f && !Input.VRInput.SimulatedGrab[side])
                    {
                        Input.VRInput.SimulateGrabPress(hand.Side);
                    }
                    steer = Input.VehicleInput.WheelSteer ?? steer;
                    yield return null;
                }
                Input.VRInput.SimulatedGrab[side] = false;
                Rig.VRHand.Scripted[side] = null;
                yield return new WaitForSecondsRealtime(0.2f);
                bool back = Input.VehicleInput.WheelSteer == null;
                // (The hand went from the rim's top to its right: clockwise from the seat, a right turn: positive.)
                line += $"; cabin steering wheel 60 deg clockwise: steering {steer:+0.00;-0.00}{(steer < 0.2f ? (steer < -0.2f ? " (STEERED LEFT)" : " (DID NOT STEER)") : "")}, let go: {(back ? "straight" : "STILL TURNED")}";
            }
            // A tank driver's right steering lever pushed 25 degrees forward by the gun hand: the drive goes forward (and left: the
            // right track alone), let go it springs back.
            Transform lever = Rig.VehicleInterior.Instance.RightLever;
            if (lever != null && lever.parent != null)
            {
                Rig.VRHand hand = rig.Dominant;
                int side = (int)hand.Side;
                Vector3 toRaw = hand.Raw.position - hand.PalmCentre;
                Quaternion rot = hand.Raw.rotation;
                Vector3 pivot = lever.parent.InverseTransformPoint(lever.position);
                Vector2 most = Vector2.zero;
                for (float t = 0f; t <= 0.8f; t += Time.unscaledDeltaTime)
                {
                    if (lever == null || lever.parent == null)
                    {
                        break;
                    }
                    float a = t < 0.15f ? 0f : Mathf.Clamp01((t - 0.15f) / 0.5f) * 25f;
                    Vector3 knob = lever.parent.TransformPoint(pivot + Quaternion.Euler(a, 0f, 0f) * new Vector3(0f, 0.3f, 0f));
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(knob + toRaw, rot);
                    if (t > 0.08f && !Input.VRInput.SimulatedGrab[side])
                    {
                        Input.VRInput.SimulateGrabPress(hand.Side);
                    }
                    if (Input.VehicleInput.DriveButtons.y > most.y)
                    {
                        most = Input.VehicleInput.DriveButtons;
                    }
                    yield return null;
                }
                Input.VRInput.SimulatedGrab[side] = false;
                Rig.VRHand.Scripted[side] = null;
                yield return new WaitForSecondsRealtime(0.5f);
                float after = Input.VehicleInput.DriveButtons.magnitude;
                bool throttle = Rig.VehicleInterior.Instance.ThrottleOnly;
                // (A track's lever alone turns to the other side; a wheeled driver's throttle only drives.)
                string turnFault = throttle ? (Mathf.Abs(most.x) > 0.1f ? " (THE THROTTLE STEERED)" : "") : most.x > -0.1f ? " (DID NOT TURN LEFT)" : "";
                line += $"; {(throttle ? "throttle lever" : "right steering lever")} 25 deg forward: drive ({most.x:+0.00;-0.00}, {most.y:+0.00;-0.00}){(most.y < 0.3f ? " (DID NOT DRIVE)" : turnFault)}, let go: {(after < 0.05f ? "back in the middle" : "STILL PUSHED")}";
            }
            // The cabin light's switch flicked by the gun hand: the red combat light (pictured), flicked again: the warm light.
            Transform flick = Rig.VehicleInterior.Instance.LightSwitch;
            if (flick != null)
            {
                Rig.VRHand fh = rig.Dominant;
                int fs = (int)fh.Side;
                Vector3 fRaw = fh.Raw.position - fh.PalmCentre;
                bool[] red = new bool[2];
                for (int f = 0; f < 2 && flick != null; f++)
                {
                    for (float t = 0f; t < 0.3f && flick != null; t += Time.unscaledDeltaTime)
                    {
                        Rig.VRHand.Scripted[fs] = new Runtime.RigidPose(flick.position + fRaw, fh.Raw.rotation);
                        yield return null;
                    }
                    Rig.VRHand.Scripted[fs] = null;
                    yield return new WaitForSecondsRealtime(0.25f);
                    red[f] = Rig.VehicleInterior.Instance.CombatLightOn;
                    if (f == 0)
                    {
                        Shots.Capture(name + "-inside-combat-light");
                    }
                }
                line += $"; light switch: {(red[0] ? "red combat light" : "DID NOT SWITCH")}, again: {(!red[1] ? "warm light" : "STAYED RED")}";
            }
            line += $"; ammo readout \"{Rig.VehicleInterior.Instance.Readout}\", ready lamp {Rig.VehicleInterior.Instance.ReadyState}; fitted: {Rig.VehicleInterior.Instance.Fit}";
            line += $"; ammo rack: {Rig.VehicleInterior.Instance.RackState}";
            Rig.VehicleInterior.TestAlarm = true;
            yield return new WaitForSecondsRealtime(0.2f);
            Shots.Capture(name + "-inside-alarm");
            yield return new WaitForSecondsRealtime(0.15f);
            Shots.Capture(name + "-inside-alarm2");
            Rig.VehicleInterior.TestAlarm = false;
            // The badly hurt hull's look: smoke under the roof, a hit's sparks (pictured; its vehicles cannot be hurt).
            Rig.VehicleInterior.TestSmoke = true;
            yield return new WaitForSecondsRealtime(1.5f);
            Rig.VehicleInterior.Instance?.TestHit();
            yield return new WaitForSecondsRealtime(0.08f);
            Shots.Capture(name + "-inside-damaged", head.position, Quaternion.LookRotation(hf + Vector3.up * 0.3f, Vector3.up), 100f);
            Rig.VehicleInterior.TestSmoke = false;
            Actor player = LocalPlayer.actor;
            // The hatch handle gripped and pulled 12 cm down: out of the seat (the last step in the seat; the check sits in it again next).
            Transform hatch = Rig.VehicleInterior.Instance.HatchHandle;
            if (hatch != null && hatch.parent != null)
            {
                Rig.VRHand hh = rig.Dominant;
                int hs = (int)hh.Side;
                Vector3 hRaw = hh.Raw.position - hh.PalmCentre;
                Vector3 bar = hatch.position;
                Vector3 downward = -hatch.parent.up;
                for (float t = 0f; t < 1f && player.IsSeated(); t += Time.unscaledDeltaTime)
                {
                    float pulled = Mathf.Clamp01((t - 0.2f) / 0.5f) * 0.12f;
                    Rig.VRHand.Scripted[hs] = new Runtime.RigidPose(bar + downward * pulled + hRaw, hh.Raw.rotation);
                    if (t > 0.1f && !Input.VRInput.SimulatedGrab[hs])
                    {
                        Input.VRInput.SimulateGrabPress(hh.Side);
                    }
                    yield return null;
                }
                Input.VRInput.SimulatedGrab[hs] = false;
                Rig.VRHand.Scripted[hs] = null;
                yield return new WaitForSecondsRealtime(0.3f);
                line += $"; hatch pulled: {(!player.IsSeated() ? "out of the seat" : "STILL SEATED")}";
            }
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
            Vector3 aim0 = frame.InverseTransformDirection(muzzle.forward);
            float Yaw()
            {
                // (Positive: to the gun's own right, from where it pointed at the start.)
                Vector3 d = frame.InverseTransformDirection(muzzle.forward);
                return Vector3.SignedAngle(Vector3.ProjectOnPlane(aim0, Vector3.up), Vector3.ProjectOnPlane(d, Vector3.up), Vector3.up);
            }
            Vector3 handle = handles[handles.Count - 1];
            Transform pivot = Rig.TurretGrip.Pivot(gun);
            // (The gun's own right: a gun facing backwards on its vehicle turned the "wrong way" for the vehicle's right.)
            Vector3 right = Vector3.Cross(frame.up, Vector3.ProjectOnPlane(muzzle.forward, frame.up)).normalized;
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
            float wantYaw = want != null ? Vector3.SignedAngle(Vector3.ProjectOnPlane(aim0, Vector3.up), Vector3.ProjectOnPlane(frame.InverseTransformDirection(want.Value), Vector3.up), Vector3.up) : float.NaN; // (as Yaw: from the start aim)
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
