using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RavenfieldVR.Rig;
using RavenfieldVR.Weapons;
using UnityEngine;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// The showcase director (docs/Showcase-Plan.md), with -rfvr-showcase (and -rfvr-autotest -vr): scenes staged with the autotest's
    /// means (scripted hands, the player's walk, weapons drawn and fired) and filmed frame by frame by <see cref="ShowcaseRecorder"/>
    /// from the cameras of <see cref="ShowcaseCamera"/>, each scene into RavenfieldVR-shots/showcase-NAME (frames, audio.wav, shots.txt).
    /// -rfvr-showcase-scenes=a,b picks scenes (all by default). Test tooling only: nothing of it runs in play.
    /// </summary>
    internal static class Showcase
    {
        internal static readonly bool Requested = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-showcase", StringComparison.OrdinalIgnoreCase));

        private static string[] Picked()
        {
            string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-rfvr-showcase-scenes=", StringComparison.OrdinalIgnoreCase));
            return arg == null ? new string[0] : arg.Substring(arg.IndexOf('=') + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        }

        private static readonly (string name, Func<ShowcaseRecorder, Action<string>, IEnumerator> scene)[] Scenes =
        {
            ("pilot", PilotScene),
        };

        /// <summary>Every picked scene, one take each; <paramref name="report"/> gets a line per take.</summary>
        internal static IEnumerator Run(Action<string> report)
        {
            string[] picked = Picked();
            foreach ((string name, Func<ShowcaseRecorder, Action<string>, IEnumerator> scene) in Scenes)
            {
                if (picked.Length > 0 && !picked.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                var rec = new ShowcaseRecorder(Path.Combine(Shots.Dir, "showcase-" + name));
                IEnumerator body = scene(rec, report);
                Exception failed = null;
                // (The scene's own errors said, the take still finished: its frames and sound are written either way.)
                yield return Guarded(body, e => failed = e);
                LetGoHands();
                Input.VRInput.SimulatedMove = Vector2.zero;
                ShowcaseCamera.Shake.Reset();
                yield return rec.Finish();
                report($"showcase {name}: {rec.Frame} frames ({rec.Frame / (float)ShowcaseRecorder.Fps:0.0} s) in {rec.Dir}"
                       + (failed != null ? $" (THE SCENE THREW {failed.GetType().Name}: {failed.Message})" : ""));
            }
        }

        private static IEnumerator Guarded(IEnumerator body, Action<Exception> failed)
        {
            while (true)
            {
                object current;
                try
                {
                    if (!body.MoveNext())
                    {
                        yield break;
                    }
                    current = body.Current;
                }
                catch (Exception e)
                {
                    failed(e);
                    yield break;
                }
                yield return current;
            }
        }

        // ---- the means the scenes share --------------------------------------------------------------------------------------

        /// <summary>
        /// <paramref name="seconds"/> of the take, frame by frame: <paramref name="each"/> gets the share done (0..1) before each frame;
        /// the frame is filmed at its end. Timed in frames, not by the clock: the take runs at its own rate.
        /// </summary>
        private static IEnumerator Film(ShowcaseRecorder rec, float seconds, Action<float> each)
        {
            int frames = Mathf.Max(1, Mathf.RoundToInt(seconds * ShowcaseRecorder.Fps));
            for (int f = 0; f < frames; f++)
            {
                each?.Invoke(frames > 1 ? f / (float)(frames - 1) : 1f);
                yield return new WaitForEndOfFrame();
                rec.TakeFrame();
            }
        }

        /// <summary>Smooth in and out (no jerk at either end), for every move.</summary>
        private static float Ease(float u)
        {
            u = Mathf.Clamp01(u);
            return u * u * u * (u * (u * 6f - 15f) + 10f);
        }

        /// <summary>The weapon in the slot drawn into the gun hand; waits (real time, before the take) until it is held.</summary>
        private static IEnumerator Draw(int slot)
        {
            Actor actor = LocalPlayer.actor;
            if (actor == null || actor.weapons == null || slot < 0 || slot >= actor.weapons.Length || actor.weapons[slot] == null)
            {
                yield break;
            }
            actor.SwitchWeapon(slot);
            for (float until = Time.realtimeSinceStartup + 6f; Time.realtimeSinceStartup < until; )
            {
                HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                if (held != null && held.Weapon == actor.weapons[slot])
                {
                    break;
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(1f);
        }

        /// <summary>
        /// The gun hand put so the gun's sight sits at <paramref name="aim"/> of the way from the hip to the eye, pointing where the head
        /// looks (as the autotest's film holds it), with a little sway so it never stands frozen.
        /// </summary>
        private static void HoldGun(float aim, float sway = 1f)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            VRRig rig = VRRig.Instance;
            if (held == null || rig == null)
            {
                return;
            }
            VRHand dom = rig.Dominant;
            SightLine sight = held.CurrentSight;
            Vector3 eyeGun = sight != null ? sight.EyePoint : held.Rig.Data.EyePoint;
            Vector3 dirGun = sight != null ? sight.EyeDirection : held.Rig.Data.EyeDirection;
            Transform head = rig.Head;
            Runtime.RigidPose raw = dom.Raw;
            float t = Time.unscaledTime;
            Quaternion swayRot = Quaternion.Euler(Mathf.Sin(t * 1.3f) * 0.6f * sway, Mathf.Sin(t * 0.9f + 1f) * 0.8f * sway, 0f);
            Quaternion fix = Quaternion.FromToRotation(held.Rig.DirToWorld(dirGun), swayRot * head.forward);
            Vector3 hip = head.position + head.right * 0.2f - head.up * 0.28f + head.forward * 0.3f;
            Vector3 eye = head.position + head.right * 0.032f + head.forward * 0.07f;
            Vector3 target = Vector3.Lerp(hip, eye, Ease(aim));
            Rig.VRHand.Scripted[(int)dom.Side] = new Runtime.RigidPose(target + fix * (raw.position - held.Rig.ToWorld(eyeGun)), fix * raw.rotation);
            Support(held, rig);
        }

        /// <summary>
        /// A long gun's other hand on its handguard, its grip closed (as the autotest's support checks take it): two-handed, as it is
        /// held. Left where it hung, the free hand stood in front of the face in the gun cam (2026-11-08).
        /// </summary>
        private static void Support(HeldWeapon held, VRRig rig)
        {
            VRHand off = rig.Off;
            if (!held.Rig.Data.HasSupportPoint || held.Rig.Data.IsPistol || off == null)
            {
                return;
            }
            int side = (int)off.Side;
            Vector3 palmToRaw = off.Raw.position - off.Palm.position;
            Rig.VRHand.Scripted[side] = new Runtime.RigidPose(held.Rig.ToWorld(held.Rig.Data.SupportPoint) + palmToRaw, off.Raw.rotation);
            if (off.Role == HandRole.Free)
            {
                Input.VRInput.SimulatedGrab[side] = true;
                off.Role = HandRole.Foregrip;
                held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
            }
        }

        /// <summary>Both hands let go of what the scene put them on (after a take).</summary>
        private static void LetGoHands()
        {
            Rig.VRHand.Scripted[0] = Rig.VRHand.Scripted[1] = null;
            Input.VRInput.SimulatedGrab[0] = Input.VRInput.SimulatedGrab[1] = false;
        }

        /// <summary>One shot of the held gun (the trigger pulled and let go), the camera kicked with it. False when it did not fire.</summary>
        private static bool Fire(float trauma = 0.32f)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            Weapon active = held != null && held.Weapon != null ? held.Weapon.GetActiveSubWeapon() : null;
            if (active == null || active.ammo == 0)
            {
                return false;
            }
            bool fired = active.Fire(active.CurrentMuzzle().forward, true);
            active.StopFire();
            if (fired)
            {
                ShowcaseCamera.Shake.Add(trauma, 1f);
            }
            return fired;
        }

        /// <summary>The trigger held this frame (full auto fires at its own rate); the camera shakes with each round that leaves.</summary>
        private static void HoldTrigger(ref int lastAmmo, float trauma = 0.22f)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            Weapon active = held != null && held.Weapon != null ? held.Weapon.GetActiveSubWeapon() : null;
            if (active == null)
            {
                return;
            }
            active.Fire(active.CurrentMuzzle().forward, true);
            if (active.ammo < lastAmmo)
            {
                ShowcaseCamera.Shake.Add(trauma, 0.8f);
            }
            lastAmmo = active.ammo;
        }

        private static void ReleaseTrigger()
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            held?.Weapon?.GetActiveSubWeapon()?.StopFire();
        }

        /// <summary>
        /// A rifle for the scene: the first of the game's weapons that is a primary, fires full auto, has no scope (its aim view 40
        /// degrees or wider) and a magazine of 20 or more, put in the primary slot. Its name, or null when there is none.
        /// </summary>
        private static string GiveRifle(params string[] prefer)
        {
            Actor actor = LocalPlayer.actor;
            if (actor == null || WeaponManager.instance == null)
            {
                return null;
            }
            // By name first (the game's own PATRIOT, an M4: the filter alone took the AUTOMATICO AKIMBO, two pistols), else the first fit.
            foreach (string name in prefer)
            {
                WeaponManager.WeaponEntry named = WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null
                    && string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase));
                if (named != null)
                {
                    actor.EquipNewWeaponEntry(named, Mathf.Clamp((int)named.slot, 0, actor.weapons.Length - 1), true);
                    return named.name;
                }
            }
            foreach (WeaponManager.WeaponEntry e in WeaponManager.instance.allWeapons)
            {
                Weapon w = e != null && e.prefab != null ? e.prefab.GetComponent<Weapon>() : null;
                if (w == null || w is MeleeWeapon || w is ThrowableWeapon || e.slot != WeaponManager.WeaponSlot.Primary || w.configuration == null
                    || e.name.IndexOf("akimbo", StringComparison.OrdinalIgnoreCase) >= 0
                    || !w.configuration.auto || w.configuration.ammo < 20 || w.configuration.aimFov < 40f || w.configuration.scopeAimObject != null)
                {
                    continue;
                }
                actor.EquipNewWeaponEntry(e, 0, true);
                return e.name;
            }
            return null;
        }

        /// <summary>Somewhere open to film: off a wall or a vehicle (the autotest's own spot finder). Its note, or null when it stayed.</summary>
        private static string OpenGround()
        {
            Actor actor = LocalPlayer.actor;
            return actor != null && VRRig.Instance != null ? AutoTest.MoveOffVehicles(actor, VRRig.Instance) : null;
        }

        /// <summary>The first slot holding a gun of the given kind (primary 0, secondary 1), else any gun.</summary>
        private static int GunSlot(int prefer)
        {
            Actor actor = LocalPlayer.actor;
            if (actor == null || actor.weapons == null)
            {
                return -1;
            }
            bool Gun(Weapon w) => w != null && !(w is MeleeWeapon) && !(w is ThrowableWeapon) && w.configuration != null && w.configuration.ammo > 1;
            if (prefer >= 0 && prefer < actor.weapons.Length && Gun(actor.weapons[prefer]))
            {
                return prefer;
            }
            for (int i = 0; i < actor.weapons.Length; i++)
            {
                if (Gun(actor.weapons[i]))
                {
                    return i;
                }
            }
            return -1;
        }

        // ---- the scenes ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The pilot (about 10 s): walking with the rifle at the hip (the gun cam on the face), raising it (over the shoulder), single
        /// shots (first person, the camera kicked), a burst (a low orbit). For the recorder's pacing, the sound's sync and the cameras.
        /// </summary>
        private static IEnumerator PilotScene(ShowcaseRecorder rec, Action<string> report)
        {
            string moved = OpenGround();
            if (moved != null)
            {
                report("showcase pilot: " + moved);
                yield return new WaitForSecondsRealtime(0.6f);
            }
            string rifle = GiveRifle("PATRIOT", "RK-44");
            report($"showcase pilot: rifle {rifle ?? "NONE FOUND (the loadout's own)"}");
            yield return new WaitForSecondsRealtime(0.8f);
            int slot = GunSlot(0);
            if (slot < 0)
            {
                report("showcase pilot: NO GUN in the loadout");
                yield break;
            }
            yield return Draw(slot);
            rec.Start();
            rec.Mark = "walk";
            rec.Shot = () => ShowcaseCamera.GunCam();
            Vector3 walkFrom = LocalPlayer.actor.Position();
            Input.VRInput.SimulatedMove = new Vector2(0f, 1f);
            yield return Film(rec, 3f, u => HoldGun(0f));
            Input.VRInput.SimulatedMove = Vector2.zero;
            report($"showcase pilot: walked {Vector3.Distance(walkFrom, LocalPlayer.actor.Position()):0.0} m in 3 s");
            rec.Mark = "raise";
            rec.Shot = () => ShowcaseCamera.OverShoulder();
            yield return Film(rec, 1.2f, u => HoldGun(u, 0.6f));
            rec.Mark = "shots";
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(1f);
            int shots = 0;
            for (int k = 0; k < 5; k++)
            {
                rec.Mark = "shot"; // (the frame a shot leaves: the sync check finds its sound's start near it)
                yield return Film(rec, 0.02f, u => { HoldGun(1f, 0.3f); if (Fire()) { shots++; } });
                rec.Mark = "shots";
                yield return Film(rec, 0.3f, u => HoldGun(1f, 0.3f));
            }
            rec.Mark = "burst";
            Vector3 centre = LocalPlayer.actor != null ? LocalPlayer.actor.Position() : Vector3.zero;
            Vector3 face = VRRig.Instance != null ? VRRig.Instance.Head.forward : Vector3.forward;
            float start = Quaternion.LookRotation(Vector3.ProjectOnPlane(face, Vector3.up)).eulerAngles.y + 55f;
            float angle = start;
            rec.Shot = () => ShowcaseCamera.Orbit(centre, 2.8f, 1.05f, angle, 1.25f);
            int ammo = int.MaxValue;
            yield return Film(rec, 1.6f, u => { angle = start + 40f * Ease(u); HoldGun(1f, 0.3f); HoldTrigger(ref ammo); });
            ReleaseTrigger();
            yield return Film(rec, 0.8f, u => { angle = start + 40f + 8f * u; HoldGun(1f, 0.3f); });
            report($"showcase pilot: {shots} single shots fired");
        }
    }
}
