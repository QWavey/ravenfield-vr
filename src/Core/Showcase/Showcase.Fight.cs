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
    /// Phase 2 (the user, 2026-11-08: "enemys infront walking towards and shooting at you while you shoot back etc, maybe Nightvision"):
    /// soldiers of the other side brought in front, walking at the player and firing, the player shooting back one after another;
    /// at night through the goggles too (-rfvr-showcase-night, the goggles switched on before the take).
    /// </summary>
    internal static partial class Showcase
    {
        /// <summary>The gun at the eye aimed at a point (blended from the hip by <paramref name="aim"/>), the head turned toward it.</summary>
        private static GunPose AimAt(Vector3 point, float aim)
        {
            Transform head = VRRig.Instance.Head;
            Vector3 eye = head.position + head.right * 0.032f + head.forward * 0.07f;
            Vector3 dir = point - eye;
            GunPose at = new GunPose(eye, Quaternion.LookRotation(dir.sqrMagnitude > 1e-6f ? dir.normalized : head.forward, Vector3.up));
            return aim >= 0.999f ? at : GunPose.Lerp(HipToEye(0f, 0f), at, aim);
        }

        /// <summary>The player turned toward a point, at most <paramref name="degreesPerFrame"/> a frame (a head following its target).</summary>
        private static void TurnToward(Vector3 point, float degreesPerFrame = 3f)
        {
            VRRig rig = VRRig.Instance;
            if (rig == null)
            {
                return;
            }
            Vector3 flat = Vector3.ProjectOnPlane(rig.Head.forward, Vector3.up), to = Vector3.ProjectOnPlane(point - rig.Head.position, Vector3.up);
            if (flat.sqrMagnitude < 1e-6f || to.sqrMagnitude < 1e-6f)
            {
                return;
            }
            rig.AddYaw(Mathf.Clamp(Vector3.SignedAngle(flat, to, Vector3.up), -degreesPerFrame, degreesPerFrame));
        }

        /// <summary>A soldier's chest seen from the eye: nothing of the level between (rocks, a crest of the ground).</summary>
        private static bool SeenFrom(Vector3 eye, Vector3 feet) => !Physics.Linecast(eye, feet + Vector3.up * 1.25f, LevelMask, QueryTriggerInteraction.Ignore);

        /// <summary>
        /// Where <paramref name="count"/> soldiers can stand in front of the player, each in plain sight of the eyes, on ground about the
        /// player's height: the nearest distance and the smallest turn from the facing that has room for all of them (11-18 m out,
        /// up to 60 degrees off). Last night's soldiers were put 28 m out behind a crest of the canyon's rim and none was seen.
        /// </summary>
        private static List<Vector3> FightPlaces(int count, out float yaw)
        {
            yaw = 0f;
            Actor player = LocalPlayer.actor;
            VRRig rig = VRRig.Instance;
            var places = new List<Vector3>();
            Vector3 eye = rig.Head.position, fwd0 = Flat(rig.Head.forward);
            foreach (float ahead in new[] { 13f, 15f, 11f, 18f })
            {
                foreach (float turn in new[] { 0f, 20f, -20f, 40f, -40f, 60f, -60f })
                {
                    Vector3 fwd = Quaternion.AngleAxis(turn, Vector3.up) * fwd0, side = Vector3.Cross(Vector3.up, fwd);
                    places.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        float lateral = (i - (count - 1) / 2f) * 4.5f;
                        Vector3 at = player.Position() + fwd * (ahead + i * 2.5f) + side * lateral;
                        if (!Physics.Raycast(at + Vector3.up * 30f, Vector3.down, out RaycastHit g, 60f, LevelMask, QueryTriggerInteraction.Ignore)
                            || g.normal.y < 0.75f || Mathf.Abs(g.point.y - player.Position().y) > 3.5f || !SeenFrom(eye, g.point))
                        {
                            break;
                        }
                        places.Add(g.point);
                    }
                    if (places.Count == count)
                    {
                        yaw = turn;
                        return places;
                    }
                }
            }
            places.Clear();
            return places;
        }

        /// <summary>
        /// Soldiers of the other side put on <paramref name="places"/>, facing the player, walking at a point <paramref name="stopShort"/>
        /// m short of them and firing at them (the game's own AI: its target the player). Respawned as the autotest brings soldiers (out
        /// of seats, a squad of their own where they had none). The ones brought, in the order of the places.
        /// </summary>
        private static List<Actor> BringEnemies(List<Vector3> places, float stopShort)
        {
            Actor player = LocalPlayer.actor;
            var brought = new List<Actor>();
            if (player == null || ActorManager.instance == null)
            {
                return brought;
            }
            List<Actor> enemies = ActorManager.instance.actors.Where(a => a != null && a.aiControlled && a.team != player.team).Take(places.Count).ToList();
            for (int i = 0; i < enemies.Count; i++)
            {
                Actor a = enemies[i];
                Vector3 at = places[i], toPlayer = Flat(player.Position() - at);
                if (a.IsSeated())
                {
                    a.LeaveSeat(false);
                }
                if (a.transform.parent != a.originalParent)
                {
                    a.transform.SetParent(a.originalParent, true);
                }
                if (a.controller != null)
                {
                    a.controller.enabled = true;
                }
                a.SpawnAt(at, Quaternion.LookRotation(toPlayer, Vector3.up));
                if (a.controller is AiActorController ai)
                {
                    if (ai.squad == null)
                    {
                        new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
                    }
                    ai.target = player;
                    Vector3 stop = player.Position() - toPlayer * stopShort;
                    ai.GotoDirect(Vector3.Lerp(stop, at, 0.15f) + Vector3.Cross(Vector3.up, toPlayer) * ((i % 2 == 0 ? 1f : -1f) * 1.5f), true);
                }
                brought.Add(a);
            }
            return brought;
        }

        /// <summary>A soldier's middle (where a burst at center mass goes).</summary>
        private static Vector3 Chest(Actor a) => a.CenterPosition() + Vector3.up * 0.12f;

        /// <summary>Over the player's shoulder at a point (the soldiers coming), wider and higher than the walking one: they in the picture.</summary>
        private static ShowcaseCamera.Shot OverShoulderAt(Vector3 point, float back = 1.7f, float side = 0.65f, float up = 0.45f, float fov = 50f)
        {
            Transform h = VRRig.Instance != null ? VRRig.Instance.Head : null;
            if (h == null)
            {
                return ShowcaseCamera.Shot.None;
            }
            Vector3 flat = Flat(point - h.position), right = Vector3.Cross(Vector3.up, flat);
            Vector3 at = h.position - flat * back + right * side + Vector3.up * up;
            return new ShowcaseCamera.Shot { Valid = true, Name = "over the shoulder", Position = at, Rotation = Quaternion.LookRotation(point - at, Vector3.up), Fov = fov, ShowBody = true, Subject = h.position };
        }

        /// <summary>
        /// The reverse shot: behind a soldier's shoulder, looking back at the player who fires at him (the muzzle flashes at the camera).
        /// </summary>
        private static ShowcaseCamera.Shot OverEnemy(Actor a, float fov = 30f)
        {
            Transform h = VRRig.Instance != null ? VRRig.Instance.Head : null;
            if (h == null || a == null)
            {
                return ShowcaseCamera.Shot.None;
            }
            Vector3 chest = Chest(a), toPlayer = Flat(h.position - chest), right = Vector3.Cross(Vector3.up, toPlayer);
            Vector3 at = chest - toPlayer * 1.6f + right * 0.7f + Vector3.up * 0.5f;
            Vector3 look = Vector3.Lerp(chest, h.position, 0.7f);
            return new ShowcaseCamera.Shot { Valid = true, Name = "over the enemy", Position = at, Rotation = Quaternion.LookRotation(look - at, Vector3.up), Fov = fov, ShowBody = true, Subject = chest };
        }

        /// <summary>The firefight's setup: the stage, the best view, the rifle (the first of -rfvr-showcase-guns, else the PATRIOT) drawn.</summary>
        private static IEnumerator FightSetup(Action<string> report)
        {
            report("showcase fight: " + Stage(40f, 160f));
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase fight: turned {FaceOpen():0} degrees to the best view");
            string gun = MontageGuns()[0];
            string rifle = GiveRifle(gun, "patriot");
            report($"showcase fight: rifle {rifle ?? "NO RIFLE FOUND (the loadout's own)"}");
            yield return new WaitForSecondsRealtime(0.8f);
            int slot = GunSlot(0);
            if (slot < 0)
            {
                throw new InvalidOperationException("no gun in the loadout");
            }
            yield return Draw(slot);
        }

        /// <summary>
        /// The firefight (about 6 s): three soldiers brought 11-18 m out where the eyes see them, walking in and firing; over the
        /// shoulder as they come, then up into the sights and a burst at the first (first person), the reverse shot behind the second as
        /// the player fires at him, the third on the gun cam (the face behind the firing gun), the helmet cam on what is left. With
        /// -rfvr-showcase-night, through the goggles.
        /// </summary>
        private static IEnumerator FightScene(ShowcaseRecorder rec, Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            if (held == null || VRRig.Instance == null || LocalPlayer.actor == null)
            {
                report("showcase fight: NO GUN HELD");
                yield break;
            }
            // Before the take (both passes alike): the goggles down at night, the soldiers brought and on their way.
            bool goggles = ShowcaseNightPatch.Night && !NightVision.isEnabled;
            if (goggles)
            {
                Input.VRGameInput.GogglesGesturePressed = true;
                yield return new WaitForSecondsRealtime(0.7f);
            }
            List<Vector3> places = FightPlaces(3, out float yaw);
            if (places.Count == 0)
            {
                report("showcase fight: NO PLACE IN SIGHT for the soldiers (11-18 m out, 60 degrees either way)");
                yield break;
            }
            VRRig.Instance.AddYaw(yaw);
            List<Actor> enemies = BringEnemies(places, 6f);
            Vector3 eye0 = VRRig.Instance.Head.position;
            report($"showcase fight: {enemies.Count} soldier(s) brought at {string.Join(", ", places.Select(p => $"{Vector3.Distance(eye0, p):0} m"))}, turned {yaw:0}"
                   + (goggles ? $", the goggles {(NightVision.isEnabled ? "on" : "NOT ON")}" : ""));
            if (enemies.Count == 0)
            {
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.6f);
            Actor Next() => enemies.FirstOrDefault(a => a != null && !a.dead) ?? enemies[enemies.Count - 1];
            Vector3 Middle() => enemies.Where(a => a != null).Select(Chest).Aggregate(Vector3.zero, (s, p) => s + p) / Mathf.Max(1, enemies.Count(a => a != null));
            rec.Start();
            int ammo = int.MaxValue;

            // They come: over the shoulder, the rifle at a low ready.
            rec.Mark = "contact";
            rec.Shot = () => OverShoulderAt(Middle());
            yield return Film(rec, 1.1f, u =>
            {
                TurnToward(Middle(), 1.5f);
                HoldGun(0.15f, 0.4f);
                if (u >= 0.6f)
                {
                    rec.Still("fight-contact");
                }
            });

            // Up into the sights on the first, a burst.
            float aim = 0f;
            Actor target = Next();
            rec.Mark = "raise";
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(aim);
            yield return Film(rec, 0.35f, u => { aim = EaseOut(u); TurnToward(Chest(target), 4f); GunPose g = AimAt(Chest(target), aim); PlaceGun(g.At, g.Aim, true); });
            rec.Mark = "engage";
            string aimNote = null;
            yield return Film(rec, 0.9f, u =>
            {
                target = Next();
                TurnToward(Chest(target), 4f);
                GunPose g = AimAt(Chest(target), 1f);
                PlaceGun(g.At, g.Aim, true);
                HoldTrigger(ref ammo, 0.2f);
                if (u >= 0.4f)
                {
                    rec.Still("fight-sights");
                }
                if (u >= 0.5f && aimNote == null)
                {
                    // (How far off the target the rounds leave: the muzzle's own line against the line to his chest.)
                    Weapon w = held.Weapon != null ? held.Weapon.GetActiveSubWeapon() : null;
                    Transform m = w != null ? w.CurrentMuzzle() : null;
                    aimNote = m == null ? "no muzzle" : $"muzzle {Vector3.Angle(m.forward, Chest(target) - m.position):0.0} deg off the chest at {Vector3.Distance(m.position, Chest(target)):0} m, sight {Vector3.Angle(g.Aim * Vector3.forward, Chest(target) - g.At):0.0} deg";
                }
            });
            ReleaseTrigger();

            // The reverse: behind the next one as the player fires at him (the camera set where it starts: on a falling soldier it
            // would swing with his ragdoll).
            ShowcaseCamera.Shot reverse = OverEnemy(Next());
            rec.Mark = "reverse";
            rec.Shot = () => reverse;
            yield return Film(rec, 0.9f, u =>
            {
                target = Next();
                TurnToward(Chest(target), 4f);
                GunPose g = AimAt(Chest(target), 1f);
                PlaceGun(g.At, g.Aim, true);
                if (u > 0.1f)
                {
                    HoldTrigger(ref ammo, 0.2f);
                }
                if (u >= 0.5f)
                {
                    rec.Still("fight-reverse");
                }
            });
            ReleaseTrigger();

            // The last on the gun cam: the face behind the gun as it fires.
            rec.Mark = "gun cam";
            rec.Shot = () => ShowcaseCamera.GunCam(0.06f, 0.07f, 0.08f, 66f);
            yield return Film(rec, 0.8f, u =>
            {
                target = Next();
                TurnToward(Chest(target), 4f);
                GunPose g = AimAt(Chest(target), 1f);
                PlaceGun(g.At, g.Aim, true);
                if (u > 0.12f)
                {
                    HoldTrigger(ref ammo, 0.2f);
                }
                if (u >= 0.6f)
                {
                    rec.Still("fight-gun-cam");
                }
            });
            ReleaseTrigger();
            rec.Mark = "after";
            rec.Shot = () => ShowcaseCamera.HelmetCam();
            yield return Film(rec, 0.7f, u => { GunPose g = AimAt(Chest(Next()), 1f); PlaceGun(g.At, g.Aim, true); });
            int down = enemies.Count(a => a != null && a.dead);
            Vector3 eye1 = VRRig.Instance.Head.position;
            report($"showcase fight: {down} of {enemies.Count} down{(goggles ? ", through the goggles" : "")}; the soldiers at "
                   + string.Join(", ", enemies.Select(a => a == null ? "?" : $"{Vector3.Distance(eye1, a.Position()):0} m{(a.dead ? " (down)" : $" ({a.health:0} health{(SeenFrom(eye1, a.Position()) ? "" : ", out of sight")})")}"))
                   + $"; {aimNote}");
            if (goggles && NightVision.isEnabled)
            {
                Input.VRGameInput.GogglesGesturePressed = true; // (off again for the next pass's own switch)
                yield return new WaitForSecondsRealtime(0.7f);
            }
        }
    }
}
