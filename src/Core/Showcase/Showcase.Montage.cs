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
    /// The montage (the user, 2026-11-08: "we need a montage of these guns like you pull the mag out of a primary, then put it in in a
    /// secondary in another clip and pull the rack in gun to head view on a primary again and shoot in helmet view or smth ... pretty
    /// epic, so we have much stuff fast ... like 20 seconds long ... modded are also allowable"): one game run, many guns, each a beat
    /// of its own (its own take: setup unfilmed, one quick action, one camera), cut to half a second to a second each in the edit.
    /// The guns: -rfvr-showcase-guns=A,B,C (names as the game lists them, without case; vrshots --guns), else vanilla ones; a gun
    /// not loaded is skipped. Beats rotate through the actions, so neighbouring cuts differ.
    /// </summary>
    internal static partial class Showcase
    {
        private enum Beat { Swap, Rack, ShotsHelmet, WalkFireGunCam, AimShots, Turn }

        // The gun cam for shooting only, walking as it fires; the hands' work in first person (the user: "from the gun to head cam when
        // reloading or smth else like racking the hand is in the face which is bad"; "sometimes shoot while walking (when gun to head cam)").
        private static readonly Beat[] BeatOrder = { Beat.Swap, Beat.Rack, Beat.ShotsHelmet, Beat.WalkFireGunCam, Beat.AimShots, Beat.Swap, Beat.Turn, Beat.WalkFireGunCam };

        private static string[] MontageGuns()
        {
            string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-rfvr-showcase-guns=", StringComparison.OrdinalIgnoreCase));
            string[] guns = arg == null ? new string[0] : arg.Substring(arg.IndexOf('=') + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
            return guns.Length > 0 ? guns : new[] { "patriot", "rk-44", "ingress 700", "signal dmr", "automatico", "jolt-20-s", "m1 garret", "s-ind7" };
        }

        /// <summary>The montage's beats as scenes: montage-01-gun-action, ... (picked by -rfvr-showcase-scenes=montage).</summary>
        private static IEnumerable<(string name, Func<Action<string>, IEnumerator> setup, Func<ShowcaseRecorder, Action<string>, IEnumerator> perform)> MontageScenes()
        {
            string[] guns = MontageGuns();
            for (int i = 0; i < guns.Length; i++)
            {
                string gun = guns[i];
                Beat beat = BeatOrder[i % BeatOrder.Length];
                bool first = i == 0;
                string safe = new string(gun.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_').ToArray()).Trim('_');
                yield return ($"montage-{i + 1:00}-{safe}-{beat.ToString().ToLowerInvariant()}", report => MontageSetup(gun, first, report), (rec, report) => MontageBeat(rec, report, beat, gun));
            }
        }

        /// <summary>A beat's setup, unfilmed: the stage once (the first beat), the gun given by name and drawn, both hands as held.</summary>
        private static IEnumerator MontageSetup(string gun, bool first, Action<string> report)
        {
            if (first)
            {
                report("showcase montage: " + Stage());
                yield return new WaitForSecondsRealtime(1.2f);
                report($"showcase montage: turned {FaceOpen():0} degrees to the best view");
            }
            LetGoHands();
            Actor actor = LocalPlayer.actor;
            WeaponManager.WeaponEntry entry = WeaponManager.instance != null
                ? WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null && string.Equals(e.name, gun, StringComparison.OrdinalIgnoreCase))
                : null;
            if (actor == null || entry == null)
            {
                throw new InvalidOperationException($"the gun \"{gun}\" is not loaded");
            }
            int slot = Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1);
            actor.EquipNewWeaponEntry(entry, slot, true);
            yield return new WaitForSecondsRealtime(0.4f);
            yield return Draw(slot);
            for (int k = 0; k < 4; k++)
            {
                actor.ResupplyAmmo();
            }
            report($"showcase montage: {entry.name} drawn");
        }

        /// <summary>One beat of the montage: a quick action on its own camera (about 1-2 s each).</summary>
        private static IEnumerator MontageBeat(ShowcaseRecorder rec, Action<string> report, Beat beat, string gun)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            VRRig rig = VRRig.Instance;
            if (held == null || rig == null)
            {
                report($"showcase montage {gun}: NO GUN HELD");
                yield break;
            }
            VRHand off = rig.Off;
            int side = (int)off.Side;
            Vector3 well = held.Rig.Data.MagSeat.position;
            var lines = new List<string>();
            var life = new ShowcaseGunLife();
            rec.Start();
            switch (beat)
            {
                case Beat.Swap:
                {
                    // Presented and the magazine changed in one flowing move (SwapFilm), the eyes on the hands.
                    rec.Mark = "present";
                    rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(well), LookAtHands * 0.9f);
                    yield return Film(rec, 0.3f, u => life.Place(rec, GunPose.Lerp(HipToEye(0.3f, 0f), Presented(), EaseOut(u)), null, off, true));
                    Input.VRInput.SimulatedGrab[side] = false;
                    yield return Film(rec, 0.06f, u => life.Place(rec, Presented(), null, off, false));
                    if (off.Role != HandRole.Free)
                    {
                        held.SetTwoHanded(false);
                        off.Role = HandRole.Free;
                    }
                    Runtime.RigidPose? from = Rig.VRHand.Scripted[side];
                    yield return Choreography(rec, 1.36, held.Reload.SwapFilm(lines.Add, note =>
                    {
                        rec.Mark = note;
                        if (note == "inserting")
                        {
                            rec.Still($"montage-{beat.ToString().ToLowerInvariant()}");
                        }
                        KeepAlive();
                        life.Place(rec, Presented(), note, off, false);
                        rec.TakeFrame();
                    }, () => rec.Elapsed, from));
                    break;
                }
                case Beat.Rack:
                {
                    // Brought up in front, the free hand racks it, the eyes on it (first person: on the gun cam the hand was in the face).
                    // A pistol closer than a rifle, and the view narrowed onto the work (montage 7: the P220 small in the middle of the picture).
                    bool pistol = held.Rig.Data.IsPistol;
                    GunPose half = pistol ? InFront(new Vector3(0.04f, -0.12f, 0.31f), -18f, -6f, 0.2f) : InFront(new Vector3(0.05f, -0.15f, 0.40f), -22f, -6f, 0.2f);
                    Vector3 rear = held.Rig.Data.EyePoint;
                    rec.Mark = "ready";
                    rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(rear), LookAtHands * 0.9f, pistol ? 46f : 54f);
                    yield return Film(rec, 0.25f, u => life.Place(rec, GunPose.Lerp(HipToEye(0.3f, 0f), half, EaseOut(u)), null, off, true));
                    Input.VRInput.SimulatedGrab[side] = false;
                    yield return Film(rec, 0.06f, u => life.Place(rec, half, null, off, false));
                    if (off.Role != HandRole.Free)
                    {
                        held.SetTwoHanded(false);
                        off.Role = HandRole.Free;
                    }
                    Runtime.RigidPose? from = Rig.VRHand.Scripted[side];
                    double rackTime = held.ActiveAmmo != null && held.Rig.Data.CyclesByHand ? 1.11 : 0.93;
                    yield return Choreography(rec, rackTime, held.Reload.RackFilm(lines.Add, note =>
                    {
                        rec.Mark = "rack " + note;
                        if (note == "pulling back")
                        {
                            rec.Still("montage-rack");
                        }
                        KeepAlive();
                        life.Place(rec, half, "rack " + note, off, false);
                        rec.TakeFrame();
                    }, () => rec.Elapsed, from));
                    break;
                }
                case Beat.ShotsHelmet:
                case Beat.AimShots:
                {
                    // Up to the sights fast, three shots: on the helmet cam, or through the sights.
                    float aim = 0f;
                    rec.Mark = "raise";
                    rec.Shot = beat == Beat.ShotsHelmet ? (Func<ShowcaseCamera.Shot>)(() => ShowcaseCamera.HelmetCam()) : () => ShowcaseCamera.FirstPersonAim(aim);
                    yield return Film(rec, 0.3f, u => { aim = EaseOut(u); HoldGun(aim, 0.2f); });
                    int shots = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        rec.Mark = "shot";
                        yield return Moment(rec, () => { HoldGun(1f, 0.2f); if (Fire()) { shots++; } rec.Still($"montage-{beat.ToString().ToLowerInvariant()}"); });
                        rec.Mark = "shots";
                        yield return Film(rec, 0.22f, u => HoldGun(1f, 0.2f));
                        if (held.ActiveAmmo != null && held.Rig.Data.CyclesByHand)
                        {
                            held.Reload.CycleForTest(); // (a bolt or pump between shots, unseen at this pace)
                        }
                    }
                    lines.Add($"{shots} shot(s)");
                    break;
                }
                case Beat.WalkFireGunCam:
                {
                    // Walking forward and firing with the camera on the gun: the face behind it, every round shaking the picture.
                    rec.Mark = "burst";
                    rec.Shot = () => ShowcaseCamera.GunCam(0.06f, 0.07f, 0.08f, 66f);
                    Input.VRInput.SimulatedMove = new Vector2(0f, 0.8f);
                    int ammo = int.MaxValue, before = held.ActiveAmmo != null ? held.ActiveAmmo.MagRounds : 0;
                    bool auto = held.Weapon != null && held.Weapon.GetActiveSubWeapon() != null && held.Weapon.GetActiveSubWeapon().configuration.auto;
                    if (auto)
                    {
                        yield return Film(rec, 1.1f, u => { HoldGun(1f, 0.25f); HoldTrigger(ref ammo, 0.18f); if (u >= 0.5f) { rec.Still("montage-walk-fire-gun-cam"); } });
                        ReleaseTrigger();
                    }
                    else
                    {
                        for (int k = 0; k < 4; k++)
                        {
                            rec.Mark = "shot";
                            yield return Moment(rec, () => { HoldGun(1f, 0.25f); Fire(); });
                            rec.Mark = "burst";
                            yield return Film(rec, 0.2f, u => HoldGun(1f, 0.25f));
                        }
                    }
                    rec.Mark = "after";
                    yield return Film(rec, 0.3f, u => HoldGun(1f, 0.25f));
                    Input.VRInput.SimulatedMove = Vector2.zero;
                    lines.Add($"walking, {before - (held.ActiveAmmo != null ? held.ActiveAmmo.MagRounds : 0)} round(s)");
                    break;
                }
                case Beat.Turn:
                {
                    // Turned over in both hands, the eyes on it.
                    float yaw = 35f, tilt = 0.2f;
                    GunPose Turned() => InFront(new Vector3(0.02f, -0.14f, 0.42f), yaw, -8f, tilt);
                    Vector3 middle = Vector3.Lerp(well, held.Rig.Data.EyePoint, 0.5f);
                    rec.Mark = "turn";
                    rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(middle), 0.75f);
                    yield return Film(rec, 0.3f, u => { GunPose g = GunPose.Lerp(HipToEye(0.3f, 0f), Turned(), EaseOut(u)); PlaceGun(g.At, g.Aim, true); });
                    yield return Film(rec, 1.1f, u =>
                    {
                        float e = Ease(u);
                        yaw = Mathf.Lerp(35f, -45f, e);
                        tilt = 0.2f + 0.4f * Mathf.Sin(e * Mathf.PI);
                        GunPose g = Turned();
                        PlaceGun(g.At, g.Aim, true);
                        if (u >= 0.5f)
                        {
                            rec.Still("montage-turn");
                        }
                    });
                    break;
                }
            }
            report($"showcase montage {gun} ({beat}): " + string.Join("; ", lines.ToArray()));
        }
    }
}
