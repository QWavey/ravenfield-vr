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

        /// <summary>
        /// Soldiers of the other side put <paramref name="ahead"/> m in front (spread across), facing the player, walking at a point
        /// <paramref name="stopShort"/> m short of them and firing at them (the game's own AI: its target the player). Respawned as the
        /// autotest brings soldiers (out of seats, a squad of their own where they had none). The ones brought, nearest first.
        /// </summary>
        private static List<Actor> BringEnemies(int count, float ahead, float stopShort)
        {
            Actor player = LocalPlayer.actor;
            VRRig rig = VRRig.Instance;
            var brought = new List<Actor>();
            if (player == null || rig == null || ActorManager.instance == null)
            {
                return brought;
            }
            Vector3 fwd = Flat(rig.Head.forward), side = Vector3.Cross(Vector3.up, fwd);
            List<Actor> enemies = ActorManager.instance.actors.Where(a => a != null && a.aiControlled && a.team != player.team).Take(count).ToList();
            for (int i = 0; i < enemies.Count; i++)
            {
                Actor a = enemies[i];
                float lateral = (i - (enemies.Count - 1) / 2f) * 7f + (i % 2 == 0 ? 1.5f : -1.5f);
                Vector3 at = player.Position() + fwd * (ahead + i * 5f) + side * lateral;
                if (Physics.Raycast(at + Vector3.up * 30f, Vector3.down, out RaycastHit g, 60f, LevelMask, QueryTriggerInteraction.Ignore))
                {
                    at = g.point;
                }
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
                a.SpawnAt(at, Quaternion.LookRotation(-fwd, Vector3.up));
                if (a.controller is AiActorController ai)
                {
                    if (ai.squad == null)
                    {
                        new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
                    }
                    ai.target = player;
                    ai.GotoDirect(player.Position() + fwd * stopShort + side * lateral * 0.5f, true);
                }
                brought.Add(a);
            }
            return brought;
        }

        private static Vector3 Chest(Actor a) => a.CenterPosition() + Vector3.up * 0.25f;

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
        /// The firefight (about 6 s): three soldiers brought 28-38 m out, walking in and firing; over the shoulder as they come, then up
        /// into the sights and a burst at the first (first person), the second on the gun cam (the face behind the firing gun), the
        /// third through the sights again, the helmet cam on what is left. With -rfvr-showcase-night, through the goggles.
        /// </summary>
        private static IEnumerator FightScene(ShowcaseRecorder rec, Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            if (held == null || VRRig.Instance == null)
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
            List<Actor> enemies = BringEnemies(3, 28f, 9f);
            report($"showcase fight: {enemies.Count} soldier(s) brought{(goggles ? $", the goggles {(NightVision.isEnabled ? "on" : "NOT ON")}" : "")}");
            if (enemies.Count == 0)
            {
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.9f);
            Actor Next() => enemies.FirstOrDefault(a => a != null && !a.dead) ?? enemies[enemies.Count - 1];
            rec.Start();
            int ammo = int.MaxValue;

            // They come: over the shoulder, the rifle at a low ready.
            rec.Mark = "contact";
            rec.Shot = () => ShowcaseCamera.OverShoulder(1.2f, 0.45f, 0.3f, 48f);
            yield return Film(rec, 1.0f, u => { TurnToward(Chest(Next()), 1.5f); HoldGun(0.15f, 0.4f); });

            // Up into the sights on the first, a burst.
            float aim = 0f;
            Actor target = Next();
            rec.Mark = "raise";
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(aim);
            yield return Film(rec, 0.35f, u => { aim = EaseOut(u); TurnToward(Chest(target), 4f); GunPose g = AimAt(Chest(target), aim); PlaceGun(g.At, g.Aim, true); });
            rec.Mark = "engage";
            yield return Film(rec, 0.8f, u =>
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
            });
            ReleaseTrigger();

            // The next on the gun cam: the face behind the gun as it fires.
            rec.Mark = "gun cam";
            rec.Shot = () => ShowcaseCamera.GunCam(0.06f, 0.07f, 0.08f, 66f);
            yield return Film(rec, 0.9f, u =>
            {
                target = Next();
                TurnToward(Chest(target), 4f);
                GunPose g = AimAt(Chest(target), 1f);
                PlaceGun(g.At, g.Aim, true);
                if (u > 0.15f)
                {
                    HoldTrigger(ref ammo, 0.2f);
                }
                if (u >= 0.6f)
                {
                    rec.Still("fight-gun-cam");
                }
            });
            ReleaseTrigger();

            // The last through the sights again, then the helmet cam on what is left.
            rec.Mark = "engage";
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(1f);
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
            });
            ReleaseTrigger();
            rec.Mark = "after";
            rec.Shot = () => ShowcaseCamera.HelmetCam();
            yield return Film(rec, 0.7f, u => { GunPose g = AimAt(Chest(Next()), 1f); PlaceGun(g.At, g.Aim, true); });
            int down = enemies.Count(a => a != null && a.dead);
            report($"showcase fight: {down} of {enemies.Count} down{(goggles ? ", through the goggles" : "")}");
            if (goggles && NightVision.isEnabled)
            {
                Input.VRGameInput.GogglesGesturePressed = true; // (off again for the next pass's own switch)
                yield return new WaitForSecondsRealtime(0.7f);
            }
        }
    }
}
