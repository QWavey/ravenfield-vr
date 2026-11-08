using System;
using System.Collections;
using RavenfieldVR.Rig;
using RavenfieldVR.Weapons;
using UnityEngine;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// The scenes that show the hands (the user, 2026-11-08: "more vr scenes where i can actually see hands"), each a short single clip
    /// on the scene schedule (<see cref="ShowcaseRecorder.Planned"/>), cut tight in the edit (tools/trailer.py; the user: "the
    /// transitioning between views is too slow"). docs/Showcase-Plan.md has the shot list.
    /// </summary>
    internal static partial class Showcase
    {
        /// <summary>The gun held up in front of the chest, its top tipped away so the magazine well and the hands at it face the eyes.</summary>
        private static GunPose Presented() => InFront(new Vector3(0.04f, -0.24f, 0.36f), -28f, -12f, 0.55f);

        /// <summary>A rifle scene's setup: a stage clear of vehicles, the best view, the game's PATRIOT drawn.</summary>
        private static Func<Action<string>, IEnumerator> RifleSetup(string scene) => report => RifleSetupSteps(scene, report);

        private static IEnumerator RifleSetupSteps(string scene, Action<string> report)
        {
            report($"showcase {scene}: " + Stage());
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase {scene}: turned {FaceOpen():0} degrees to the best view");
            string rifle = GiveRifle("PATRIOT", "RK-44");
            report($"showcase {scene}: rifle {rifle ?? "NONE FOUND (the loadout's own)"}");
            yield return new WaitForSecondsRealtime(0.8f);
            int slot = GunSlot(0);
            if (slot < 0)
            {
                throw new InvalidOperationException("no gun in the loadout");
            }
            yield return Draw(slot);
        }

        /// <summary>
        /// The reload, in first person with the hands in the picture (about 5 s): the rifle brought up in front of the chest and tipped
        /// toward the eyes, the eyes on the magazine well; the support hand lets go of the handguard and comes down to the magazine,
        /// pulls it out, holds it, pushes it home (the autotest's own reload by hand, <c>ReloadInteractions.ReloadFilm</c>, timed by game
        /// time here); a close-up from the gun's side while the magazine is out; the hand back on the handguard, the rifle up to the
        /// sights, two shots.
        /// </summary>
        private static IEnumerator ReloadScene(ShowcaseRecorder rec, Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            VRRig rig = VRRig.Instance;
            if (held == null || rig == null || held.Reload == null)
            {
                report("showcase reload: NO GUN HELD");
                yield break;
            }
            Vector3 well = held.Rig.Data.MagSeat.position;
            Vector3 middle = Vector3.Lerp(well, held.Rig.Data.EyePoint, 0.35f);
            rec.Start();

            // Up in front of the chest, the eyes going down to the well (0.5 s).
            float look = 0f;
            rec.Mark = "present";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(middle), look);
            yield return Film(rec, 0.5f, u =>
            {
                look = Ease(u) * 0.9f;
                GunPose g = GunPose.Lerp(HipToEye(0f, 0f), Presented(), EaseOut(u));
                PlaceGun(g.At, g.Aim, true);
            });

            // The support hand lets go of the handguard (the grip opened: the reload code takes it as free).
            VRHand off = rig.Off;
            int side = (int)off.Side;
            Input.VRInput.SimulatedGrab[side] = false;
            rec.Mark = "let go";
            yield return Film(rec, 0.12f, u => HoldGunAt(new Vector3(0.04f, -0.24f, 0.36f), -28f, -12f, 0.55f, false));
            if (off.Role != HandRole.Free)
            {
                report($"showcase reload: the support hand still {off.Role} after letting go (made free)");
                held.SetTwoHanded(false);
                off.Role = HandRole.Free;
            }

            // The reload by hand: reach, grab, out, held, in, seated, let go (3.25 s of game time).
            var lines = new System.Collections.Generic.List<string>();
            rec.Planned += 3.25; // (its moves' times: 0.45 + 0.15 + 0.8 + 0.4 + 0.8 + 0.3 + 0.35 s)
            Runtime.RigidPose? start = Rig.VRHand.Scripted[side];
            yield return held.Reload.ReloadFilm(lines.Add, note =>
            {
                rec.Mark = note;
                bool closeUp = note == "pulling out" || note == "held";
                rec.Shot = closeUp
                    ? (Func<ShowcaseCamera.Shot>)(() => ShowcaseCamera.CloseUp(well, new Vector3(-0.42f, -0.06f, 0.02f), 46f))
                    : () => ShowcaseCamera.FirstPersonLook(PlacedPoint(well), 0.9f);
                KeepAlive();
                HoldGunAt(new Vector3(0.04f, -0.24f, 0.36f), -28f, -12f, 0.55f, false);
                rec.TakeFrame();
            }, () => Time.time, start);
            // (Each of its seven moves runs a frame or so past its time, in both passes alike: the next step's planned end absorbs it.)
            report("showcase reload: " + string.Join("; ", lines.ToArray()));

            // The hand back on the handguard (0.3 s, eased from where the reload left it), then up to the sights (0.4 s).
            Runtime.RigidPose? from = Rig.VRHand.Scripted[side];
            rec.Mark = "regrip";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(middle), 0.9f * (1f - look));
            yield return Film(rec, 0.3f, u =>
            {
                HoldGunAt(new Vector3(0.04f, -0.24f, 0.36f), -28f, -12f, 0.55f, true);
                if (from.HasValue && Rig.VRHand.Scripted[side].HasValue)
                {
                    Rig.VRHand.Scripted[side] = Runtime.RigidPose.Lerp(from.Value, Rig.VRHand.Scripted[side].Value, Ease(u));
                }
            });
            float aim = 0f;
            rec.Mark = "raise";
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(aim);
            yield return Film(rec, 0.4f, u =>
            {
                aim = EaseOut(u);
                GunPose g = GunPose.Lerp(Presented(), HipToEye(1f, 0.3f), aim);
                PlaceGun(g.At, g.Aim, true);
            });
            int shots = 0;
            for (int k = 0; k < 2; k++)
            {
                rec.Mark = "shot";
                yield return Moment(rec, () => { HoldGun(1f, 0.3f); if (Fire()) { shots++; } });
                rec.Mark = "shots";
                yield return Film(rec, 0.28f, u => HoldGun(1f, 0.3f));
            }
            report($"showcase reload: {shots} shot(s) after it");
        }

        /// <summary>
        /// A look over the rifle in both hands (about 3 s), first person: held up in front, turned from its right side to its left and
        /// tipped, as a player looks a new gun over; then the helmet cam as it comes up to the sights, one shot.
        /// </summary>
        private static IEnumerator InspectScene(ShowcaseRecorder rec, Action<string> report)
        {
            if (WeaponSystem.Instance == null || WeaponSystem.Instance.Held == null)
            {
                report("showcase inspect: NO GUN HELD");
                yield break;
            }
            Vector3 middle = Vector3.Lerp(WeaponSystem.Instance.Held.Rig.Data.MagSeat.position, WeaponSystem.Instance.Held.Rig.Data.EyePoint, 0.5f);
            rec.Start();
            float yaw = 30f, tilt = 0.2f, look = 0f;
            GunPose Turned() => InFront(new Vector3(0.02f, -0.14f, 0.42f), yaw, -8f, tilt);
            rec.Mark = "bring up";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(middle), look);
            yield return Film(rec, 0.45f, u =>
            {
                look = 0.8f * Ease(u);
                GunPose g = GunPose.Lerp(HipToEye(0f, 0f), Turned(), EaseOut(u));
                PlaceGun(g.At, g.Aim, true);
            });
            rec.Mark = "turn";
            yield return Film(rec, 1.6f, u =>
            {
                float e = Ease(u);
                yaw = Mathf.Lerp(30f, -50f, e);
                tilt = 0.2f + 0.45f * Mathf.Sin(e * Mathf.PI);
                GunPose g = Turned();
                PlaceGun(g.At, g.Aim, true);
            });
            float aim = 0f;
            rec.Mark = "raise";
            rec.Shot = () => ShowcaseCamera.HelmetCam();
            yield return Film(rec, 0.45f, u =>
            {
                aim = EaseOut(u);
                GunPose g = GunPose.Lerp(Turned(), HipToEye(1f, 0.3f), aim);
                PlaceGun(g.At, g.Aim, true);
            });
            rec.Mark = "shot";
            bool fired = false;
            yield return Moment(rec, () => { HoldGun(1f, 0.3f); fired = Fire(); });
            rec.Mark = "after";
            yield return Film(rec, 0.5f, u => HoldGun(1f, 0.3f));
            report($"showcase inspect: turned over, {(fired ? "one shot" : "THE SHOT DID NOT FIRE")}");
        }

        /// <summary>Fast in, slow out: a hand bringing something somewhere (a raise is quick, then settles).</summary>
        private static float EaseOut(float u)
        {
            u = Mathf.Clamp01(u);
            float a = 1f - u;
            return 1f - a * a * a;
        }
    }
}
