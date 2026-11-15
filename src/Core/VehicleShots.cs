using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// -rfvr-vehicleshots (with -rfvr-autotest -vr): the VR player put in each kind of vehicle on the map, at the wheel and on a
    /// passenger seat, and pictured as the headset sees it (ahead, to the side, down at the hands and legs) and from outside (the body
    /// in its seat). The user, 2026-11-15: "I wanna see what it looks like for VR players inside vehicles". Lines say where the head is
    /// against the seat, so a head through the roof or a body beside its seat shows in the words too.
    /// </summary>
    internal static class VehicleShots
    {
        public static readonly bool Requested = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-vehicleshots", StringComparison.OrdinalIgnoreCase));

        private static string Kind(Vehicle v)
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

        internal static IEnumerator Run(Action<string> report)
        {
            Actor player = LocalPlayer.actor;
            if (player == null || ActorManager.instance == null || ActorManager.instance.vehicles == null)
            {
                report("vehicles: NO PLAYER or no vehicles");
                yield break;
            }
            List<Vehicle> all = ActorManager.instance.vehicles.Where(v => v != null && !v.dead && v.seats != null && v.seats.Count > 0).ToList();
            report($"vehicles: on the map {string.Join(", ", all.GroupBy(Kind).Select(k => k.Count() + " " + k.Key))}");
            // One of each kind (the first that stands), the wheel and the first passenger seat.
            var picked = new List<(Vehicle v, int seat)>();
            foreach (IGrouping<string, Vehicle> kind in all.GroupBy(Kind))
            {
                Vehicle v = kind.FirstOrDefault();
                picked.Add((v, 0));
                if (v.seats.Count > 1)
                {
                    picked.Add((v, 1));
                }
            }
            if (picked.Count == 0)
            {
                report("vehicles: NO VEHICLE on the map");
                yield break;
            }
            foreach ((Vehicle v, int seatIndex) in picked.Take(10))
            {
                if (v == null || v.dead || player.dead)
                {
                    continue;
                }
                string name = $"vehicle-{Kind(v)}-{(seatIndex == 0 ? "driver" : "passenger")}";
                Seat seat = v.seats[seatIndex];
                if (seat.IsOccupied() && seat.occupant != player)
                {
                    seat.occupant.LeaveSeat(false); // (an AI soldier in it: out, so the player can sit)
                    yield return null;
                }
                if (player.IsSeated())
                {
                    player.LeaveSeat(false);
                    yield return new WaitForSecondsRealtime(0.5f);
                }
                bool seated;
                try
                {
                    seated = player.EnterSeat(seat, true, true);
                }
                catch (Exception e)
                {
                    report($"{name}: the seat threw: {e.Message}");
                    continue;
                }
                yield return new WaitForSecondsRealtime(2.5f); // (the rig settles into the seat, the view's fade done)
                Rig.VRRig rig = Rig.VRRig.Instance;
                if (!seated || !player.IsSeated() || rig == null || rig.Head == null)
                {
                    report($"{name} ({v.name}): NOT SEATED");
                    continue;
                }
                Transform head = rig.Head;
                Vector3 local = seat.transform.InverseTransformPoint(head.position);
                report($"{name} ({v.name}, seat {seatIndex + 1} of {v.seats.Count}{(seat.CanUsePersonalWeapons() ? ", own weapons" : "")}): head {local.y:0.00} m above the seat, "
                       + $"{new Vector2(local.x, local.z).magnitude:0.00} m off its centre; looking {Vector3.Angle(Vector3.ProjectOnPlane(head.forward, Vector3.up), Vector3.ProjectOnPlane(v.transform.forward, Vector3.up)):0} deg from the vehicle's front");
                Vector3 vfwd = v.transform.forward, vside = v.transform.right;
                Shots.Capture(name + "-1-ahead");
                yield return null;
                Shots.Capture(name + "-2-side", head.position, Quaternion.LookRotation(vside * (seatIndex == 0 ? -1f : 1f) + vfwd * 0.3f, Vector3.up), 100f);
                yield return null;
                Shots.Capture(name + "-3-down", head.position, Quaternion.LookRotation(vfwd * 0.45f + Vector3.down, vfwd), 100f);
                yield return null;
                // From outside: beside and a little ahead of the seat, at head height, looking at the head (the body in its seat).
                Vector3 outside = head.position + vside * (seatIndex == 0 ? -3f : 3f) + vfwd * 1.5f + Vector3.up * 0.6f;
                Shots.Capture(name + "-4-outside", outside, Quaternion.LookRotation(head.position - outside, Vector3.up), 60f);
                yield return new WaitForSecondsRealtime(0.3f);
            }
            if (player.IsSeated())
            {
                player.LeaveSeat(false);
            }
            report("vehicles: done");
        }
    }
}
