using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RavenfieldVR.Rig;
using RavenfieldVR.Weapons;
using UnityEngine;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// Cover (the user, 2026-11-08: "then we do the other stuff like walking, etc grabbing etc covering etc."): the player behind the
    /// edge of a wall, soldiers out past it firing; a step out, a burst, back behind the wall, out again on the other camera.
    /// </summary>
    internal static partial class Showcase
    {
        /// <summary>The wall the cover scene found: which way it is from the player, and which side its open edge is on (+1 right).</summary>
        private static Vector3 _coverFacing = Vector3.forward;
        private static float _coverSide = 1f;

        /// <summary>
        /// A wall to stand behind with an open edge beside it: on flat dry ground clear of vehicles, a steep face 0.35-0.9 m in front
        /// at chest and at head height (the whole body behind it), and 1.3 m to one side nothing in front for 20 m (the edge to step
        /// out past); the best by the view past the edge. The player put there facing the wall.
        /// </summary>
        private static string StageCover(float near = 15f, float far = 170f)
        {
            Actor actor = LocalPlayer.actor;
            if (actor == null)
            {
                return "NO PLAYER to stage";
            }
            Vector3 home = actor.Position();
            var keepOff = new List<Vector3>();
            if (ActorManager.instance != null && ActorManager.instance.vehicles != null)
            {
                keepOff.AddRange(ActorManager.instance.vehicles.Where(v => v != null).Select(v => v.transform.position));
            }
            keepOff.AddRange(UnityEngine.Object.FindObjectsOfType<VehicleSpawner>().Select(s => s.transform.position));
            bool Wall(Vector3 from, Vector3 dir, float max) =>
                Physics.SphereCast(from, 0.12f, dir, out RaycastHit h, max, LevelMask, QueryTriggerInteraction.Ignore) && h.normal.y < 0.5f && h.distance >= 0.25f;
            bool Standable(Vector3 at, float y, out Vector3 ground)
            {
                ground = at;
                if (!Physics.Raycast(at + Vector3.up * 2.5f, Vector3.down, out RaycastHit g, 4f, LevelMask, QueryTriggerInteraction.Ignore)
                    || g.normal.y < 0.9f || Mathf.Abs(g.point.y - y) > 0.3f || WaterLevel.IsInWater(g.point + Vector3.up * 0.3f))
                {
                    return false;
                }
                ground = g.point;
                return !Physics.CheckCapsule(g.point + Vector3.up * 0.5f, g.point + Vector3.up * 1.7f, 0.3f, LevelMask, QueryTriggerInteraction.Ignore);
            }
            float bestScore = -1f;
            Vector3 best = home, bestDir = Vector3.forward;
            float bestSide = 1f;
            // Walls found by looking for them (a ray out at chest height from points on rings round the spawn) and walked along to
            // where they end: a point beside a wall at random almost never lay 0.35-0.9 m from it (Dustbowl: "no cover found").
            bool Level(Vector3 from, Vector3 d, float y)
            {
                for (float m = 8f; m <= 20f; m += 6f)
                {
                    if (!Physics.Raycast(from + d * m + Vector3.up * 3f, Vector3.down, out RaycastHit g, 8f, LevelMask, QueryTriggerInteraction.Ignore)
                        || Mathf.Abs(g.point.y - y) > 1.5f || g.normal.y < 0.8f)
                    {
                        return false;
                    }
                }
                return true;
            }
            for (float r = near; r <= far; r += 10f)
            {
                for (int i = 0; i < 24; i++)
                {
                    Vector3 p = home + Quaternion.Euler(0f, i * 15f + r * 3f, 0f) * Vector3.forward * r;
                    if (!Physics.Raycast(p + Vector3.up * 300f, Vector3.down, out RaycastHit top, 600f, LevelMask, QueryTriggerInteraction.Ignore)
                        || top.normal.y < 0.9f || keepOff.Any(v => (v - top.point).sqrMagnitude < 30f * 30f))
                    {
                        continue;
                    }
                    for (int k = 0; k < 12; k++)
                    {
                        Vector3 look = Quaternion.Euler(0f, k * 30f, 0f) * Vector3.forward;
                        if (!Physics.SphereCast(top.point + Vector3.up * 1.2f, 0.15f, look, out RaycastHit wallHit, 14f, LevelMask, QueryTriggerInteraction.Ignore)
                            || Mathf.Abs(wallHit.normal.y) > 0.3f)
                        {
                            continue;
                        }
                        Vector3 n = Vector3.ProjectOnPlane(wallHit.normal, Vector3.up).normalized;
                        Vector3 d = -n, along = Vector3.Cross(Vector3.up, d);
                        Vector3 atWall = wallHit.point + n * 0.6f;
                        foreach (float side in new[] { 1f, -1f })
                        {
                            // Along the wall to its end on this side (at most 10 m): the feet 0.9 m short of it, still behind it.
                            Vector3 s = along * side, endAt = atWall;
                            bool ended = false;
                            for (float m = 0.5f; m <= 10f; m += 0.5f)
                            {
                                endAt = atWall + s * m;
                                if (!Wall(endAt + Vector3.up * 1.2f, d, 1.1f))
                                {
                                    ended = true;
                                    break;
                                }
                            }
                            if (!ended || !Standable(endAt - s * 0.9f, top.point.y, out Vector3 feet)
                                || !Wall(feet + Vector3.up * 1.2f, d, 0.9f) || !Wall(feet + Vector3.up * 1.75f, d, 1.1f) || !Wall(feet - s * 0.6f + Vector3.up * 1.3f, d, 1.1f)
                                || !Standable(feet + s * 1.3f, feet.y, out Vector3 edge) || !Level(edge, d, feet.y)
                                || Physics.SphereCast(edge + Vector3.up * 1.4f, 0.25f, d, out RaycastHit _, 20f, LevelMask, QueryTriggerInteraction.Ignore)
                                || Physics.SphereCast(feet + Vector3.up * 1.4f, 0.2f, s, out RaycastHit _, 1.5f, LevelMask, QueryTriggerInteraction.Ignore)
                                || keepOff.Any(v => (v - feet).sqrMagnitude < 30f * 30f))
                            {
                                continue;
                            }
                            float reach = Physics.Raycast(edge + Vector3.up * 1.5f, d, out RaycastHit far2, 80f, LevelMask, QueryTriggerInteraction.Ignore) ? far2.distance : 80f;
                            float score = reach - r * 0.05f;
                            if (score > bestScore)
                            {
                                bestScore = score;
                                best = feet;
                                bestDir = d;
                                bestSide = side;
                            }
                        }
                    }
                }
            }
            if (bestScore < 0f)
            {
                return $"NO COVER FOUND within {far:0} m: filmed at the spawn";
            }
            actor.SetPositionAndRotation(best, Quaternion.LookRotation(bestDir, Vector3.up));
            _coverFacing = bestDir;
            _coverSide = bestSide;
            return $"behind a wall {Vector3.Distance(home, best):0} m from the spawn, its open edge on the {(bestSide > 0f ? "right" : "left")}, {bestScore:0} m clear past it";
        }

        /// <summary>The player turned to face the wall again (the stage turns the body, the head keeps its own yaw).</summary>
        private static float FaceCover()
        {
            VRRig rig = VRRig.Instance;
            if (rig == null)
            {
                return 0f;
            }
            float delta = Vector3.SignedAngle(Flat(rig.Head.forward), _coverFacing, Vector3.up);
            rig.AddYaw(delta);
            return delta;
        }

        private static IEnumerator CoverSetup(Action<string> report)
        {
            report("showcase cover: " + StageCover());
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase cover: turned {FaceCover():0} degrees to the wall");
            string rifle = GiveRifle(MontageGuns()[0], "PATRIOT", "RK-44");
            report($"showcase cover: rifle {rifle ?? "NO RIFLE FOUND (the loadout's own)"}");
            yield return new WaitForSecondsRealtime(0.8f);
            int slot = GunSlot(0);
            if (slot < 0)
            {
                throw new InvalidOperationException("no gun in the loadout");
            }
            yield return Draw(slot);
            FaceCover();
        }

        /// <summary>Behind the player on the wall's closed side, looking past the edge: the wall, the player's back, the open ground beyond.</summary>
        private static ShowcaseCamera.Shot BehindCover(float fov = 52f)
        {
            Transform h = VRRig.Instance != null ? VRRig.Instance.Head : null;
            if (h == null)
            {
                return ShowcaseCamera.Shot.None;
            }
            Vector3 side = Vector3.Cross(Vector3.up, _coverFacing) * _coverSide;
            // (Nearly on the edge's line: from the closed side the wall filled the left half of the picture, cover take 1.)
            Vector3 at = h.position - _coverFacing * 1.7f + side * 0.15f + Vector3.up * 0.35f;
            Vector3 look = h.position + side * 1.9f + _coverFacing * 12f;
            return new ShowcaseCamera.Shot { Valid = true, Name = "behind cover", Position = at, Rotation = Quaternion.LookRotation(look - at, Vector3.up), Fov = fov, ShowBody = true, Subject = h.position };
        }

        /// <summary>
        /// The cover scene (about 5.5 s): soldiers brought out past the edge, walking in and firing; behind the wall (the camera behind,
        /// the rifle low), a step out past the edge up into the sights, a burst, back behind the wall (helmet cam), out again with the gun
        /// cam on the face for a second burst, back in.
        /// </summary>
        private static IEnumerator CoverScene(ShowcaseRecorder rec, Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            VRRig rig = VRRig.Instance;
            Actor player = LocalPlayer.actor;
            if (held == null || rig == null || player == null)
            {
                report("showcase cover: NO GUN HELD");
                yield break;
            }
            FaceCover();
            // The soldiers out past the edge: placed as the fight's, seen from where the player will step out to.
            Vector3 side = Vector3.Cross(Vector3.up, _coverFacing) * _coverSide;
            Vector3 edgeEye = rig.Head.position + side * 1.3f;
            var places = new List<Vector3>();
            foreach (float ahead in new[] { 14f, 17f, 12f, 20f })
            {
                places.Clear();
                for (int i = 0; i < 3 && places.Count == i; i++)
                {
                    Vector3 at = player.Position() + side * (2.5f + i * 3.5f) + _coverFacing * (ahead + i * 2.5f);
                    if (Physics.Raycast(at + Vector3.up * 30f, Vector3.down, out RaycastHit g, 60f, LevelMask, QueryTriggerInteraction.Ignore)
                        && g.normal.y > 0.75f && Mathf.Abs(g.point.y - player.Position().y) < 3.5f && SeenFrom(edgeEye, g.point))
                    {
                        places.Add(g.point);
                    }
                }
                if (places.Count == 3)
                {
                    break;
                }
            }
            List<Actor> enemies = places.Count > 0 ? BringEnemies(places, 9f) : new List<Actor>();
            report($"showcase cover: {enemies.Count} soldier(s) brought past the edge{(enemies.Count == 0 ? " (NO PLACE IN SIGHT past the edge)" : "")}");
            yield return new WaitForSecondsRealtime(0.8f);
            Actor Next() => enemies.FirstOrDefault(a => a != null && !a.dead) ?? enemies.LastOrDefault();
            Vector3 Ahead() => Next() != null ? Chest(Next()) : rig.Head.position + _coverFacing * 20f + side * 1.3f;
            rec.Start();
            int ammo = int.MaxValue;
            float aim = 0f;

            // Behind the wall: the camera behind, the rifle low, the head turned a little toward the edge.
            rec.Mark = "behind cover";
            rec.Shot = () => BehindCover();
            yield return Film(rec, 1.1f, u =>
            {
                HoldGun(0.1f, 0.5f);
                if (u >= 0.5f)
                {
                    rec.Still("cover-behind");
                }
            });

            // Out past the edge and up into the sights (the step: the move stick sideways, its own walk).
            for (int peek = 0; peek < 2; peek++)
            {
                rec.Mark = "step out"; // (0.8 s: in 0.55 s the eyes were still at the wall, cover take 2)
                rec.Shot = peek == 0 ? (Func<ShowcaseCamera.Shot>)(() => ShowcaseCamera.FirstPersonAim(aim)) : () => OverShoulderAt(Ahead(), 0.75f, 0.32f * _coverSide, 0.1f, 38f); // (over the gun: the gun cam on the face read as firing into the sky)
                Input.VRInput.SimulatedMove = new Vector2(_coverSide, 0f);
                yield return Film(rec, 0.8f, u =>
                {
                    aim = EaseOut(u);
                    TurnToward(Ahead(), 3f);
                    GunPose g = AimAt(Ahead(), aim);
                    PlaceGun(g.At, g.Aim, true);
                });
                Input.VRInput.SimulatedMove = Vector2.zero;
                rec.Mark = "engage";
                yield return Film(rec, peek == 0 ? 0.9f : 0.8f, u =>
                {
                    TurnToward(Ahead(), 4f);
                    GunPose g = AimAt(Ahead(), 1f);
                    PlaceGun(g.At, g.Aim, true);
                    if (u > 0.08f)
                    {
                        HoldTrigger(ref ammo, 0.2f);
                    }
                    if (u >= 0.5f)
                    {
                        rec.Still(peek == 0 ? "cover-sights" : "cover-over-the-gun");
                    }
                });
                ReleaseTrigger();
                // Back behind the wall: the helmet cam, the rifle coming down, the view back to the wall.
                rec.Mark = "back in";
                rec.Shot = peek == 0 ? (Func<ShowcaseCamera.Shot>)(() => ShowcaseCamera.HelmetCam()) : () => BehindCover();
                Input.VRInput.SimulatedMove = new Vector2(-_coverSide, 0f);
                yield return Film(rec, 0.8f, u =>
                {
                    aim = 1f - EaseOut(u);
                    TurnToward(rig.Head.position + _coverFacing * 10f, 4f);
                    HoldGun(Mathf.Max(0.1f, aim), 0.4f);
                });
                Input.VRInput.SimulatedMove = Vector2.zero;
                if (peek == 0)
                {
                    yield return Film(rec, 0.35f, u => HoldGun(0.1f, 0.4f));
                }
            }
            int down = enemies.Count(a => a != null && a.dead);
            report($"showcase cover: two peeks past the edge, {down} of {enemies.Count} down");
        }
    }
}
