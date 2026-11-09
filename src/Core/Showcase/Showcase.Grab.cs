using System;
using System.Collections;
using System.Linq;
using RavenfieldVR.Rig;
using RavenfieldVR.Weapons;
using UnityEngine;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// Grabbing (the user, 2026-11-08: "walking, etc grabbing etc covering"): a soldier of the other side right in front, the free hand
    /// takes him by the chest and throws him (the mod's own hold on a body, as the autotest's soldier check does it), the rifle up and a
    /// shot after him.
    /// </summary>
    internal static partial class Showcase
    {
        /// <summary>
        /// The third-person body aims where the head looks, not along the barrel (Weapons.ThirdPersonAimPatch), while a scene holds the
        /// gun down at the hip: following a barrel pointed at the ground, the body bent double over the soldier it took hold of (grab
        /// takes, 2026-11-09: the user, "grab positions are very wrong").
        /// </summary>
        internal static bool BodyLooksAhead;

        /// <summary>
        /// A soldier of the other side put <paramref name="ahead"/> m in front facing the player, his AI paused so he stands (the
        /// autotest's Bring: out of a seat, a squad where he had none, his look set). Null when none could be put there.
        /// </summary>
        private static Actor StandBefore(float ahead)
        {
            Actor player = LocalPlayer.actor;
            VRRig rig = VRRig.Instance;
            if (player == null || rig == null || ActorManager.instance == null)
            {
                return null;
            }
            Vector3 flat = Flat(rig.Head.forward);
            Vector3 at = rig.Head.position + flat * ahead;
            at.y = player.Position().y;
            if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.down, out RaycastHit g, 4f, LevelMask, QueryTriggerInteraction.Ignore))
            {
                at = g.point;
            }
            Actor a = ActorManager.instance.actors.Where(x => x != null && x.aiControlled && x.team != player.team && x.ragdoll != null && x.controller != null)
                                                  .OrderBy(x => x.dead ? 2 : x.IsSeated() ? 1 : 0).FirstOrDefault();
            if (a == null)
            {
                return null;
            }
            if (a.IsSeated())
            {
                a.LeaveSeat(false);
            }
            if (a.transform.parent != a.originalParent)
            {
                a.transform.SetParent(a.originalParent, true);
            }
            a.controller.enabled = true;
            a.SpawnAt(at, Quaternion.LookRotation(-flat, Vector3.up));
            if (a.controller is AiActorController ai)
            {
                if (ai.squad == null)
                {
                    new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
                }
                ai.facingDirectionVector = -flat;
            }
            a.controller.enabled = false;
            return a;
        }

        private static IEnumerator GrabSetup(Action<string> report)
        {
            report("showcase grab: " + Stage(30f, 160f));
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase grab: turned {FaceOpen():0} degrees to the best view");
            string rifle = GiveRifle(MontageGuns()[0], "PATRIOT", "RK-44");
            report($"showcase grab: rifle {rifle ?? "NO RIFLE FOUND (the loadout's own)"}");
            yield return new WaitForSecondsRealtime(0.8f);
            int slot = GunSlot(0);
            if (slot < 0)
            {
                throw new InvalidOperationException("no gun in the loadout");
            }
            yield return Draw(slot);
        }

        /// <summary>From the side, a little ahead: the player and the soldier both in the picture, the throw going across it.</summary>
        private static ShowcaseCamera.Shot SideOn(Vector3 between, Vector3 flat, float sign, float fov = 50f)
        {
            Vector3 side = Vector3.Cross(Vector3.up, flat) * sign;
            Vector3 at = between + side * 2.4f + flat * 0.9f + Vector3.up * 0.15f;
            return new ShowcaseCamera.Shot { Valid = true, Name = "side on", Position = at, Rotation = Quaternion.LookRotation(between + flat * 0.6f - at, Vector3.up), Fov = fov, ShowBody = true, Subject = between };
        }

        /// <summary>
        /// The grab (about 4 s): the soldier standing in front, the free hand off the handguard to his chest (first person), the grip
        /// closed, lifted (side on), thrown up and away (side on, the camera set), his flight followed (first person), the rifle up
        /// and a shot after him (helmet cam).
        /// </summary>
        private static IEnumerator GrabScene(ShowcaseRecorder rec, Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            VRRig rig = VRRig.Instance;
            if (held == null || rig == null)
            {
                report("showcase grab: NO GUN HELD");
                yield break;
            }
            VRHand off = rig.Off;
            int side = (int)off.Side;
            BodyLooksAhead = true; // (upright, not bent after the lowered barrel; off again after the pass, Showcase.Run)
            // The rifle down at the hip, pointing at the ground, before he is brought (slowly: no bash on the way): held out at low
            // ready, it bashed the soldier dead as he appeared at arm's length (Dustbowl take 1, the gore mod's blood everywhere).
            Transform head = rig.Head;
            Quaternion level = Quaternion.LookRotation(Flat(head.forward), Vector3.up);
            float gs = off.IsLeft ? 1f : -1f; // (the gun hand's side)
            GunPose Low() => new GunPose(head.position + level * new Vector3(0.24f * gs, -0.66f, 0.10f), level * Quaternion.Euler(62f, -20f * gs, 0f));
            GunPose from0 = HipToEye(0.15f, 0f);
            for (float t = 0f; t < 0.6f; t += Time.unscaledDeltaTime)
            {
                GunPose g0 = GunPose.Lerp(from0, Low(), Ease(t / 0.6f));
                PlaceGun(g0.At, g0.Aim, false);
                yield return null;
            }
            Input.VRInput.SimulatedGrab[side] = false;
            if (off.Role != HandRole.Free)
            {
                held.SetTwoHanded(false);
                off.Role = HandRole.Free;
            }
            // Every other soldier nearby quiet for the take (their AI paused, back after): standing paused at arm's length, he was shot
            // dead by the player's own side before the hand came (grab take of 14:34, the picture pass), as the autotest's soldier check
            // found before.
            var quiet = new System.Collections.Generic.List<Actor>();
            if (ActorManager.instance != null)
            {
                foreach (Actor a in ActorManager.instance.actors)
                {
                    if (a != null && a.aiControlled && !a.dead && a.controller != null && a.controller.enabled && (a.Position() - rig.Head.position).sqrMagnitude < 150f * 150f)
                    {
                        a.controller.enabled = false;
                        quiet.Add(a);
                    }
                }
            }
            Actor soldier = StandBefore(0.7f);
            if (soldier == null)
            {
                foreach (Actor a in quiet)
                {
                    if (a != null && a.controller != null)
                    {
                        a.controller.enabled = true;
                    }
                }
                report("showcase grab: NO SOLDIER to take hold of");
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.6f);
            Vector3 flat = Flat(rig.Head.forward);
            Runtime.RigidPose raw = off.Raw;
            Vector3 palmFromRaw = off.PalmCentre - raw.position;
            Quaternion grip = Quaternion.LookRotation(flat, Vector3.up) * Quaternion.Euler(0f, 0f, off.IsLeft ? 80f : -80f); // the palm toward him
            void PalmAt(Vector3 palm, Quaternion r, Vector3 velocity = default)
            {
                Rig.VRHand.Scripted[side] = new Runtime.RigidPose(palm - r * Quaternion.Inverse(raw.rotation) * palmFromRaw, r);
                Rig.VRHand.ScriptedVelocity[side] = velocity;
            }
            void PlaceLow()
            {
                GunPose g0 = Low();
                PlaceGun(g0.At, g0.Aim, false);
            }
            // By the collar (high on the chest, below the neck), as a hand takes a man to throw him; at the middle of the chest the hand
            // sat low and the side view looked like a hug.
            Vector3 chest = soldier.CenterPosition() + Vector3.up * 0.35f;
            Vector3 start = off.PalmCentre;
            Vector3 between = Vector3.Lerp(rig.Head.position, chest, 0.5f) - Vector3.up * 0.4f;
            float camSide = off.IsLeft ? -1f : 1f; // (on the gun hand's side: the free arm reaching toward the camera's far side)
            ShowcaseCamera.Shot sideOn = SideOn(between, flat, camSide);
            rec.Start();

            // The free hand to his chest, the eyes on him.
            rec.Mark = "reach";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(chest, 0.35f, 60f);
            yield return Film(rec, 0.4f, u =>
            {
                PlaceLow();
                PalmAt(Vector3.Lerp(start, chest, EaseOut(u)), grip);
            });
            // The grip closed on him (the mod's own hold on a body).
            rec.Mark = "grab";
            Input.VRInput.SimulateGrabPress(off.Side);
            bool took = false;
            yield return Film(rec, 0.25f, u =>
            {
                PlaceLow();
                PalmAt(chest, grip);
                took |= off.Role == HandRole.Body;
                if (u >= 0.6f)
                {
                    rec.Still("grab-taken");
                }
            });
            // Lifted, side on.
            rec.Mark = "lift";
            rec.Shot = () => sideOn;
            Vector3 lifted = chest + Vector3.up * 0.16f - flat * 0.15f; // (lifted and pulled in a little: the throw starts within reach)
            yield return Film(rec, 0.3f, u => { PlaceLow(); PalmAt(Vector3.Lerp(chest, lifted, EaseOut(u)), grip); });
            // Thrown: the hand swung up and away at 6 m/s for eight frames, the grip opened.
            rec.Mark = "throw";
            Vector3 v = (flat + Vector3.up * 0.6f).normalized * 6f;
            // (The hand moves 0.3 m while it gives him 6 m/s, the game throwing by the hand's speed: moved the 0.8 m that speed makes in
            // eight frames, the weapon's hand went past the arm's reach and flew off the arm, grab take 2.)
            Vector3 swing = v.normalized * 0.3f;
            yield return Film(rec, 8f / 60f, u => { PlaceLow(); PalmAt(lifted + swing * u, grip, v); });
            Input.VRInput.SimulatedGrab[side] = false;
            Rig.VRHand.ScriptedVelocity[side] = Vector3.zero;
            rec.Mark = "flying";
            yield return Film(rec, 0.45f, u =>
            {
                PlaceLow();
                PalmAt(Vector3.Lerp(lifted + swing, lifted - flat * 0.1f, Ease(u)), grip); // (and back in, following through)
                if (u >= 0.5f)
                {
                    rec.Still("grab-thrown");
                }
            });
            // After him: the eyes and the rifle on where he lands, the hand back on the handguard, a shot.
            Rigidbody body = soldier.ragdoll != null && soldier.ragdoll.IsRagdoll() ? soldier.ragdoll.MainRigidbody() : null;
            Vector3 lastWhere = lifted + v * 0.5f;
            Vector3 thrownFrom = lifted - flat * 0.1f;
            Vector3 Where()
            {
                Vector3 w = body != null ? body.position : soldier.CenterPosition();
                // (Taken away by the game, he stood 2.7 km off and the rifle swung after him: the last place he was seen kept.)
                if ((w - rig.Head.position).sqrMagnitude < 40f * 40f && (body != null || !soldier.dead))
                {
                    lastWhere = w;
                }
                return lastWhere;
            }
            float aim = 0f;
            rec.Mark = "after him";
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(aim);
            yield return Film(rec, 0.5f, u =>
            {
                aim = EaseOut(u);
                TurnToward(Where(), 4f);
                GunPose g = AimAt(Where() + Vector3.up * 0.1f, aim);
                // The free hand back to the handguard over the first 0.3 (eased, not put there in a frame), then on it.
                bool on = u >= 0.3f;
                PlaceGun(g.At, g.Aim, on);
                if (!on)
                {
                    PalmAt(Vector3.Lerp(thrownFrom, PlacedPoint(held.Rig.Data.SupportPoint), Ease(u / 0.3f)), Quaternion.Slerp(grip, raw.rotation, u / 0.3f));
                }
            });
            rec.Mark = "shot";
            rec.Shot = () => ShowcaseCamera.HelmetCam();
            bool fired = false;
            yield return Moment(rec, () => { GunPose g = AimAt(Where() + Vector3.up * 0.1f, 1f); PlaceGun(g.At, g.Aim, true); fired = Fire(); });
            rec.Mark = "after";
            yield return Film(rec, 0.6f, u => { GunPose g = AimAt(Where() + Vector3.up * 0.1f, 1f); PlaceGun(g.At, g.Aim, true); });
            float far = Vector3.Distance(rig.Head.position, Where());
            report($"showcase grab: {(took ? "took hold of the soldier" : "THE HAND DID NOT TAKE HOLD")}, thrown {far:0.0} m off, {(fired ? "a shot after him" : "THE SHOT DID NOT FIRE")}");
            if (soldier.controller != null)
            {
                soldier.controller.enabled = true; // (his AI back: the game goes on with him)
            }
            foreach (Actor a in quiet)
            {
                if (a != null && a.controller != null)
                {
                    a.controller.enabled = true;
                }
            }
        }
    }
}
