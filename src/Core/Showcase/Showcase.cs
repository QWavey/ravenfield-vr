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
    /// means (scripted hands, the player's walk, weapons drawn and fired) and filmed by <see cref="ShowcaseRecorder"/> from the cameras
    /// of <see cref="ShowcaseCamera"/>, each scene into RavenfieldVR-shots/showcase-NAME. A scene is its setup (where to stand, which
    /// gun: once, before the take) and its performance, played twice from the same spot: the picture pass (frames, shots.txt), then
    /// the sound pass (audio.wav, sound.txt); tools/showcase_take.py --mux puts them together. -rfvr-showcase-scenes=a,b picks scenes
    /// (all by default). Test tooling only: nothing of it runs in play.
    /// </summary>
    internal static partial class Showcase
    {
        internal static readonly bool Requested = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-showcase", StringComparison.OrdinalIgnoreCase));

        private static string[] Picked()
        {
            string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-rfvr-showcase-scenes=", StringComparison.OrdinalIgnoreCase));
            return arg == null ? new string[0] : arg.Substring(arg.IndexOf('=') + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray();
        }

        private static readonly (string name, Func<Action<string>, IEnumerator> setup, Func<ShowcaseRecorder, Action<string>, IEnumerator> perform)[] Scenes =
        {
            ("pilot", PilotSetup, PilotScene),
            ("reload", RifleSetup("reload"), ReloadScene),
            ("inspect", RifleSetup("inspect"), InspectScene),
            ("grenade", RifleSetup("grenade"), GrenadeScene),
        };

        /// <summary>Every picked scene: its setup, the picture pass, the spot restored, the sound pass; <paramref name="report"/> gets the lines.</summary>
        internal static IEnumerator Run(Action<string> report)
        {
            string[] picked = Picked();
            Steady = true;
            foreach ((string name, Func<Action<string>, IEnumerator> setup, Func<ShowcaseRecorder, Action<string>, IEnumerator> perform) in Scenes)
            {
                if (picked.Length > 0 && !picked.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }
                Exception failed = null;
                yield return Guarded(setup(report), e => failed = e);
                if (failed != null)
                {
                    report($"showcase {name}: THE SETUP THREW {failed.GetType().Name}: {failed.Message}");
                    continue;
                }
                Spot spot = Spot.Take();
                string dir = Path.Combine(Shots.Dir, "showcase-" + name);
                foreach (bool sound in new[] { false, true })
                {
                    if (sound)
                    {
                        yield return spot.Restore(report);
                    }
                    var rec = new ShowcaseRecorder(dir, sound);
                    string pass = sound ? "sound" : "picture";
                    ShowcaseBody.Begin();
                    // (The scene's own errors said, the pass still finished: what it filmed is written either way.)
                    yield return Guarded(perform(rec, line => report($"[{pass}] {line}")), e => failed = e);
                    ShowcaseBody.End();
                    LetGoHands();
                    ReleaseTrigger();
                    SecondWeapon.TestTrigger = false;
                    Input.VRInput.SimulatedMove = Vector2.zero;
                    ShowcaseCamera.Shake.Reset();
                    yield return rec.Finish();
                    report($"showcase {name} {pass} pass: {rec.Frame} frames over {rec.Elapsed:0.0} s in {rec.Dir}"
                           + (failed != null ? $" (THE SCENE THREW {failed.GetType().Name}: {failed.Message})" : ""));
                    failed = null;
                }
            }
            Steady = false;
        }

        /// <summary>
        /// Where the player stood and faced and what they held when the setup was done, so the sound pass plays from where the picture
        /// pass did, with a full magazine again.
        /// </summary>
        private sealed class Spot
        {
            private Vector3 _position;
            private Quaternion _rotation;
            private float _yaw;
            private string _weapon;
            private int _slot = -1;

            internal static Spot Take()
            {
                var s = new Spot();
                Actor actor = LocalPlayer.actor;
                if (actor != null)
                {
                    s._position = actor.Position();
                    s._rotation = actor.transform.rotation;
                    HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                    s._slot = held != null && held.Weapon != null && actor.weapons != null ? Array.IndexOf(actor.weapons, held.Weapon) : -1;
                    WeaponManager.WeaponEntry entry = s._slot >= 0 ? actor.weapons[s._slot].weaponEntry : null;
                    s._weapon = entry != null ? entry.name : null;
                }
                s._yaw = VRRig.Instance != null ? VRRig.Instance.Yaw : 0f;
                return s;
            }

            internal IEnumerator Restore(Action<string> report)
            {
                Actor actor = LocalPlayer.actor;
                if (actor == null || actor.dead)
                {
                    report("showcase: THE PLAYER IS GONE before the sound pass");
                    yield break;
                }
                float off = Vector3.Distance(actor.Position(), _position);
                actor.SetPositionAndRotation(_position, _rotation);
                for (int k = 0; k < 4; k++)
                {
                    actor.ResupplyAmmo(); // (what the picture pass spent, a grenade above all, there again for the sound pass)
                }
                if (VRRig.Instance != null)
                {
                    VRRig.Instance.AddYaw(Mathf.DeltaAngle(VRRig.Instance.Yaw, _yaw));
                }
                if (_weapon != null)
                {
                    GiveRifle(_weapon);
                    yield return new WaitForSecondsRealtime(0.8f);
                }
                if (_slot >= 0)
                {
                    yield return Draw(_slot);
                }
                yield return new WaitForSecondsRealtime(0.6f);
                report($"showcase: back to the take's spot ({off:0.0} m off), facing and {_weapon ?? "the gun"} as before, for the sound pass");
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
        /// <paramref name="seconds"/> of the take: <paramref name="each"/> gets the share done (0..1) before each frame, the frame is
        /// filmed at its end. On the scene's schedule (<see cref="ShowcaseRecorder.Planned"/>): the picture pass in whole frames at its
        /// rate up to the step's planned end, the sound pass by the clock up to the same second (its share from the step's planned start,
        /// so a slow frame does not push every later step).
        /// </summary>
        private static IEnumerator Film(ShowcaseRecorder rec, float seconds, Action<float> each)
        {
            double from = rec.Planned, to = from + seconds;
            rec.Planned = to;
            if (!rec.Sound)
            {
                int first = rec.Frame, end = Math.Max(first + 1, (int)Math.Round(to * ShowcaseRecorder.Fps));
                for (int f = first; f < end; f++)
                {
                    KeepAlive();
                    each?.Invoke(end - first > 1 ? (f - first) / (float)(end - first - 1) : 1f);
                    yield return new WaitForEndOfFrame();
                    rec.TakeFrame();
                }
                yield break;
            }
            do
            {
                KeepAlive();
                each?.Invoke(seconds > 0f ? Mathf.Clamp01((float)((rec.Elapsed - from) / seconds)) : 1f);
                yield return new WaitForEndOfFrame();
                rec.TakeFrame();
            }
            while (rec.Elapsed < to);
        }

        /// <summary>
        /// One frame of the take, in both passes (a shot fired: called twice in a fast frame of the sound pass, it fired twice).
        /// </summary>
        private static IEnumerator Moment(ShowcaseRecorder rec, Action each)
        {
            rec.Planned += 1.0 / ShowcaseRecorder.Fps;
            KeepAlive();
            each?.Invoke();
            yield return new WaitForEndOfFrame();
            rec.TakeFrame();
        }

        /// <summary>The player kept at full health through a take (the battle's strays would end it).</summary>
        private static void KeepAlive()
        {
            Actor actor = LocalPlayer.actor;
            if (actor != null && !actor.dead)
            {
                actor.health = actor.maxHealth;
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
        /// looks (as the autotest's film holds it), with a little sway so it never stands frozen. <paramref name="support"/>: the other
        /// hand on the handguard.
        /// </summary>
        private static void HoldGun(float aim, float sway = 1f, bool support = true)
        {
            if (VRRig.Instance != null)
            {
                GunPose g = HipToEye(aim, sway);
                PlaceGun(g.At, g.Aim, support);
            }
        }

        /// <summary>Where the gun's sight goes and how its sight frame is turned (forward along the sight, up the gun's top).</summary>
        internal struct GunPose
        {
            public Vector3 At;
            public Quaternion Aim;

            internal GunPose(Vector3 at, Quaternion aim)
            {
                At = at;
                Aim = aim;
            }

            internal static GunPose Lerp(GunPose a, GunPose b, float k) => new GunPose(Vector3.Lerp(a.At, b.At, k), Quaternion.Slerp(a.Aim, b.Aim, k));
        }

        /// <summary>The pose of <see cref="HoldGun"/>: from the hip (0) to the sights at the eye (1).</summary>
        private static GunPose HipToEye(float aim, float sway = 1f)
        {
            Transform head = VRRig.Instance.Head;
            float t = Time.time; // (game time: the same sway in the picture and the sound pass)
            float k = Ease(aim);
            // Level, as a gun is held: the whole frame turned (its up to the world's), not only its line; at the hip pointed down a little
            // (low ready), on the sight level. (Turned by its line alone it kept the hand's cant: the gun cam's horizon rolled 45 degrees.)
            Vector3 flat = Flat(head.forward);
            float headPitch = -Mathf.Asin(Mathf.Clamp(head.forward.y, -1f, 1f)) * Mathf.Rad2Deg; // (+ looking down, as Euler's x)
            float pitch = headPitch + Mathf.Lerp(16f, 0f, k) + Mathf.Sin(t * 1.3f) * 0.6f * sway;
            float yaw = Mathf.Sin(t * 0.9f + 1f) * 0.8f * sway;
            Vector3 aimDir = Quaternion.LookRotation(flat, Vector3.up) * Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            Vector3 hip = head.position + head.right * 0.2f - head.up * 0.28f + head.forward * 0.3f;
            Vector3 eye = head.position + head.right * 0.032f + head.forward * 0.07f;
            return new GunPose(Vector3.Lerp(hip, eye, k), Quaternion.LookRotation(aimDir, Vector3.up));
        }

        /// <summary>
        /// The gun held where the head looks at it, not along its line: the sight at <paramref name="local"/> (in the head's level
        /// frame: x right, y up, z ahead), the muzzle turned <paramref name="yaw"/> degrees from straight ahead (minus: to the left,
        /// across a right-handed player's body) and <paramref name="pitch"/> (minus: up), and the gun's top tipped
        /// <paramref name="tiltAway"/> (0-1) away from the eyes, so its underside, its magazine well and the hands at it face the
        /// head. For the scenes that show the hands (a reload, a look over the gun).
        /// </summary>
        private static void HoldGunAt(Vector3 local, float yaw, float pitch, float tiltAway, bool support)
        {
            if (VRRig.Instance != null)
            {
                GunPose g = InFront(local, yaw, pitch, tiltAway);
                PlaceGun(g.At, g.Aim, support);
            }
        }

        /// <summary>The pose of <see cref="HoldGunAt"/>.</summary>
        private static GunPose InFront(Vector3 local, float yaw, float pitch, float tiltAway)
        {
            Transform head = VRRig.Instance.Head;
            Quaternion level = Quaternion.LookRotation(Flat(head.forward), Vector3.up);
            Vector3 target = head.position + level * local;
            Vector3 aimDir = level * Quaternion.Euler(pitch, yaw, 0f) * Vector3.forward;
            Vector3 away = Flat(target - head.position);
            Vector3 up = Vector3.ProjectOnPlane(Vector3.up + away * (tiltAway * 1.5f), aimDir);
            return new GunPose(target, Quaternion.LookRotation(aimDir, up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up));
        }

        private static Vector3 Flat(Vector3 v)
        {
            v = Vector3.ProjectOnPlane(v, Vector3.up);
            return v.sqrMagnitude > 1e-6f ? v.normalized : Vector3.forward;
        }

        /// <summary>
        /// The gun's sight put at <paramref name="target"/> with its sight frame turned to <paramref name="aimRot"/> (forward along the
        /// sight, up the gun's top): the gun hand placed from its grip as first measured, the other hand on the handguard where it is to
        /// be when <paramref name="support"/>.
        /// </summary>
        private static void PlaceGun(Vector3 target, Quaternion aimRot, bool support)
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
            Vector3 gunFwd = held.Rig.DirToWorld(dirGun).normalized, gunUp = held.Rig.DirToWorld(held.Rig.Data.Up).normalized;
            Quaternion gunNow = Quaternion.LookRotation(gunFwd, gunUp);
            Vector3 eyeNow = held.Rig.ToWorld(eyeGun);
            Runtime.RigidPose raw = dom.Raw;
            // The hand on the gun's grip as it was when first held (in the gun's sight frame), measured once per gun, standing: measured
            // every frame, the hand's offset to the gun carried the gun's one-step lag behind the hand while walking, each frame put the hand
            // right for the last one's lag, and the gun overshot and came back by turns: the gun cam swung 66 px a frame (takes 8-10).
            if (!ReferenceEquals(_gripOf, held))
            {
                _gripOf = held;
                Quaternion inv = Quaternion.Inverse(gunNow);
                _gripPos = inv * (raw.position - eyeNow);
                _gripRot = inv * raw.rotation;
            }
            Rig.VRHand.Scripted[(int)dom.Side] = new Runtime.RigidPose(target + aimRot * _gripPos, aimRot * _gripRot);
            _placedSight = new Runtime.RigidPose(target, aimRot);
            _placedGun = held;
            // The support hand where the handguard is to be (the gun's own geometry, turned to the aim about its sight at the target), not
            // where it is: put on the gun's own handguard, the hand followed every kick and two-handed aim kept it, the gun swung between
            // shots and stood pointing up after the burst (take 6, 2026-11-08).
            if (support)
            {
                Support(held, rig, target + aimRot * (Quaternion.Inverse(gunNow) * (held.Rig.ToWorld(held.Rig.Data.SupportPoint) - eyeNow)));
            }
        }

        private static HeldWeapon _gripOf, _placedGun;
        private static Vector3 _gripPos;
        private static Quaternion _gripRot;
        private static Runtime.RigidPose _placedSight;

        /// <summary>A point of the held gun (its own space) where the scene put the gun this frame (not where physics has it yet).</summary>
        private static Vector3 PlacedPoint(Vector3 gunPoint)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            if (held == null)
            {
                return Vector3.zero;
            }
            if (!ReferenceEquals(held, _placedGun))
            {
                return held.Rig.ToWorld(gunPoint);
            }
            SightLine sight = held.CurrentSight;
            Vector3 eyeGun = sight != null ? sight.EyePoint : held.Rig.Data.EyePoint;
            Vector3 dirGun = sight != null ? sight.EyeDirection : held.Rig.Data.EyeDirection;
            Quaternion gunNow = Quaternion.LookRotation(held.Rig.DirToWorld(dirGun).normalized, held.Rig.DirToWorld(held.Rig.Data.Up).normalized);
            return _placedSight.position + _placedSight.rotation * (Quaternion.Inverse(gunNow) * (held.Rig.ToWorld(gunPoint) - held.Rig.ToWorld(eyeGun)));
        }

        /// <summary>
        /// A long gun's other hand on its handguard, its grip closed (as the autotest's support checks take it): two-handed, as it is
        /// held. Left where it hung, the free hand stood in front of the face in the gun cam (2026-11-08).
        /// </summary>
        private static void Support(HeldWeapon held, VRRig rig, Vector3 at)
        {
            VRHand off = rig.Off;
            if (!held.Rig.Data.HasSupportPoint || held.Rig.Data.IsPistol || off == null)
            {
                return;
            }
            int side = (int)off.Side;
            Vector3 palmToRaw = off.Raw.position - off.Palm.position;
            Rig.VRHand.Scripted[side] = new Runtime.RigidPose(at + palmToRaw, off.Raw.rotation);
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

        /// <summary>
        /// A stage for a scene: dry, flat ground <paramref name="near"/> to <paramref name="far"/> m from the player's spawn, at least
        /// <paramref name="clear"/> m from every vehicle and vehicle spawner (a helicopter landed on the spawn's pad into the pilot's walk and
        /// a blast knocked the player over, 2026-11-08), 25 m from where soldiers spawn, room round it for the cameras (4 m every way), and
        /// the best view (<see cref="View"/>) wins. The player is put there. Its note, or a line in capitals when none was found.
        /// </summary>
        private static string Stage(float near = 30f, float far = 160f, float clear = 45f)
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
            float bestScore = -1f;
            Vector3 best = home;
            for (float r = near; r <= far; r += 10f)
            {
                for (int i = 0; i < 20; i++)
                {
                    Vector3 dir = Quaternion.Euler(0f, i * 18f + r, 0f) * Vector3.forward;
                    Vector3 top = home + dir * r + Vector3.up * 300f;
                    if (!Physics.Raycast(top, Vector3.down, out RaycastHit ground, 600f, LevelMask, QueryTriggerInteraction.Ignore)
                        || ground.normal.y < 0.94f || WaterLevel.IsInWater(ground.point + Vector3.up * 0.3f)
                        || keepOff.Any(p => (p - ground.point).sqrMagnitude < clear * clear))
                    {
                        continue;
                    }
                    Vector3 p0 = ground.point;
                    if (Physics.CheckCapsule(p0 + Vector3.up * 0.4f, p0 + Vector3.up * 1.8f, 0.4f, LevelMask, QueryTriggerInteraction.Ignore)
                        || NearestSpawn(p0) < 25f || !Room(p0, 2.5f))
                    {
                        continue;
                    }
                    float score = BestView(p0, 30f).score;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = p0;
                    }
                }
            }
            if (bestScore < 0f)
            {
                return clear > 30f ? Stage(near, far, 30f) : $"NO STAGE FOUND within {far:0} m clear of vehicles: filmed at the spawn";
            }
            actor.SetPositionAndRotation(best, actor.transform.rotation);
            return $"staged {Vector3.Distance(home, best):0} m from the spawn (view {bestScore:0}), clear of {keepOff.Count} vehicle(s) and spawner(s) by {clear:0} m";
        }

        private const int LevelMask = ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2);

        private static float NearestSpawn(Vector3 p)
        {
            SpawnPoint[] spawns = ActorManager.instance != null ? ActorManager.instance.spawnPoints : null;
            return spawns != null && spawns.Any(s => s != null) ? spawns.Where(s => s != null).Min(s => Vector3.Distance(s.transform.position, p)) : 999f;
        }

        /// <summary>Room round a spot for the cameras: nothing within <paramref name="metres"/> at head height every 45 degrees.</summary>
        private static bool Room(Vector3 feet, float metres)
        {
            Vector3 head = feet + Vector3.up * 1.6f;
            for (int k = 0; k < 8; k++)
            {
                if (Physics.Raycast(head, Quaternion.Euler(0f, k * 45f, 0f) * Vector3.forward, metres, LevelMask, QueryTriggerInteraction.Ignore))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// How good the view from <paramref name="feet"/> along <paramref name="yaw"/> is, or -1 when it will not do: a cone of seven rays
        /// (48 degrees wide) at knee and eye height, each to 60 m, its middle three clear for 25 m (a single ray slipped past a rock's
        /// edge and the pilot walked up to its face, take 7); dry ground under the walk's first 16 m, within 1.5 m of the feet's height;
        /// the score the cone's mean reach, plus up to 20 facing the nearest soldiers' spawn 40-200 m off (the battle in the picture).
        /// </summary>
        private static float View(Vector3 feet, float yaw)
        {
            float sum = 0f;
            for (int k = -3; k <= 3; k++)
            {
                Vector3 d = Quaternion.Euler(0f, yaw + k * 8f, 0f) * Vector3.forward;
                for (int level = 0; level < 2; level++)
                {
                    Vector3 from = feet + Vector3.up * (level == 0 ? 0.6f : 1.6f);
                    float reach = Physics.Raycast(from, d, out RaycastHit hit, 60f, LevelMask, QueryTriggerInteraction.Ignore) ? hit.distance : 60f;
                    if (Mathf.Abs(k) <= 1 && reach < 25f)
                    {
                        return -1f;
                    }
                    sum += reach;
                }
            }
            Vector3 fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            for (float m = 4f; m <= 16f; m += 4f)
            {
                Vector3 above = feet + fwd * m + Vector3.up * 1.6f;
                if (!Physics.Raycast(above, Vector3.down, out RaycastHit g, 4f, LevelMask, QueryTriggerInteraction.Ignore)
                    || Mathf.Abs(g.point.y - feet.y) > 1.5f || WaterLevel.IsInWater(g.point + Vector3.up * 0.3f))
                {
                    return -1f;
                }
            }
            float score = sum / 14f;
            SpawnPoint[] spawns = ActorManager.instance != null ? ActorManager.instance.spawnPoints : null;
            SpawnPoint nearest = spawns?.Where(s => s != null).OrderBy(s => Vector3.Distance(s.transform.position, feet)).FirstOrDefault();
            if (nearest != null)
            {
                float dist = Vector3.Distance(nearest.transform.position, feet);
                if (dist >= 40f && dist <= 200f)
                {
                    Vector3 to = Vector3.ProjectOnPlane(nearest.transform.position - feet, Vector3.up).normalized;
                    score += 20f * Mathf.Max(0f, Vector3.Dot(to, fwd));
                }
            }
            return score;
        }

        /// <summary>The best <see cref="View"/> round a spot every <paramref name="step"/> degrees: its score (-1: none) and yaw.</summary>
        private static (float score, float yaw) BestView(Vector3 feet, float step)
        {
            float bestScore = -1f, bestYaw = 0f;
            for (float yaw = 0f; yaw < 360f; yaw += step)
            {
                float v = View(feet, yaw);
                if (v > bestScore)
                {
                    bestScore = v;
                    bestYaw = yaw;
                }
            }
            return (bestScore, bestYaw);
        }

        /// <summary>
        /// During a showcase take the player is not knocked over (a blast ragdolled the camera's man and every later view tumbled,
        /// 2026-11-08) and not hurt. Test tooling only (<see cref="ShowcaseSteadyPatches"/>).
        /// </summary>
        internal static bool Steady;

        /// <summary>
        /// The player turned to the best view (<see cref="View"/>, every 10 degrees). The pilot's walk ended facing a staircase's wall, then
        /// a rock's face (takes 4 and 7). The turn in degrees, NaN when no direction would do (the player stays as they face).
        /// </summary>
        private static float FaceOpen()
        {
            VRRig rig = VRRig.Instance;
            Actor actor = LocalPlayer.actor;
            if (rig == null || actor == null)
            {
                return 0f;
            }
            (float score, float bestYaw) = BestView(actor.Position(), 10f);
            if (score < 0f)
            {
                return float.NaN;
            }
            Vector3 flat = Vector3.ProjectOnPlane(rig.Head.forward, Vector3.up);
            float now = Quaternion.LookRotation(flat.sqrMagnitude > 1e-6f ? flat : Vector3.forward).eulerAngles.y;
            float delta = Mathf.DeltaAngle(now, bestYaw);
            rig.AddYaw(delta);
            return delta;
        }

        /// <summary>Where the rig faces, for the scene's log: the rig's yaw, the head's, the body's, the mode (take 6's view turned 70 degrees in a second; take 7's held).</summary>
        private static string Facing()
        {
            VRRig rig = VRRig.Instance;
            Actor actor = LocalPlayer.actor;
            if (rig == null || rig.Head == null)
            {
                return "no rig";
            }
            Vector3 f = Vector3.ProjectOnPlane(rig.Head.forward, Vector3.up);
            float head = f.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(f).eulerAngles.y : float.NaN;
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            Vector3 g = held != null ? Vector3.ProjectOnPlane(held.Rig.DirToWorld(held.Rig.Data.AimAxis), Vector3.up) : Vector3.zero;
            float gun = g.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(g).eulerAngles.y : float.NaN;
            return $"rig yaw {rig.Yaw:0}, head {head:0}, gun {gun:0}, body {(actor != null ? actor.transform.eulerAngles.y : float.NaN):0}, mode {rig.Mode}";
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

        /// <summary>The pilot's setup: a stage clear of vehicles, the widest view, the game's PATRIOT drawn.</summary>
        private static IEnumerator PilotSetup(Action<string> report)
        {
            report("showcase pilot: " + Stage());
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase pilot: turned {FaceOpen():0} degrees to the widest view");
            string rifle = GiveRifle("PATRIOT", "RK-44");
            report($"showcase pilot: rifle {rifle ?? "NONE FOUND (the loadout's own)"}");
            yield return new WaitForSecondsRealtime(0.8f);
            int slot = GunSlot(0);
            if (slot < 0)
            {
                throw new InvalidOperationException("no gun in the loadout");
            }
            yield return Draw(slot);
        }

        /// <summary>
        /// The pilot (about 6.5 s, kept simple; walk 2.2 s, raise 0.55 s): walking with the rifle at the hip (the gun cam on the face), raising it (over the
        /// shoulder), three single shots through the sights and two from the helmet cam (the camera kicked each time), a burst on the
        /// helmet cam. (The orbit is gone: the user, "the orbit view is not good lets remove that".) For the recorder, the sound's sync
        /// and the cameras.
        /// </summary>
        private static IEnumerator PilotScene(ShowcaseRecorder rec, Action<string> report)
        {
            rec.Start();
            rec.Mark = "walk";
            rec.Shot = () => ShowcaseCamera.GunCam();
            Vector3 walkFrom = LocalPlayer.actor.Position();
            Input.VRInput.SimulatedMove = new Vector2(0f, 1f);
            report("showcase pilot: start: " + Facing());
            yield return Film(rec, 2.2f, u => HoldGun(0f));
            Input.VRInput.SimulatedMove = Vector2.zero;
            report($"showcase pilot: walked {Vector3.Distance(walkFrom, LocalPlayer.actor.Position()):0.0} m in 2.2 s; " + Facing());
            rec.Mark = "raise";
            rec.Shot = () => ShowcaseCamera.OverShoulder();
            yield return Film(rec, 0.55f, u => HoldGun(EaseOut(u), 0.6f));
            int shots = 0;
            for (int k = 0; k < 5; k++)
            {
                if (k == 0)
                {
                    rec.Shot = () => ShowcaseCamera.FirstPersonAim(1f);
                }
                else if (k == 3)
                {
                    rec.Shot = () => ShowcaseCamera.HelmetCam();
                }
                rec.Mark = "shot"; // (the frame a shot leaves: the mux lines the sound up by these)
                yield return Moment(rec, () => { HoldGun(1f, 0.3f); if (Fire()) { shots++; } });
                rec.Mark = "shots";
                yield return Film(rec, 0.26f, u => HoldGun(1f, 0.3f));
            }
            report("showcase pilot: after the shots: " + Facing());
            rec.Mark = "burst";
            int ammo = int.MaxValue;
            yield return Film(rec, 1.6f, u => { HoldGun(1f, 0.3f); HoldTrigger(ref ammo); });
            ReleaseTrigger();
            rec.Mark = "after";
            yield return Film(rec, 0.8f, u => HoldGun(1f, 0.3f));
            report($"showcase pilot: {shots} single shots fired; at the end: " + Facing());
        }
    }
}

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>The player kept on their feet and unhurt while <see cref="Showcase.Steady"/> (showcase takes only).</summary>
    [HarmonyLib.HarmonyPatch]
    internal static class ShowcaseSteadyPatches
    {
        private static bool Player(Actor a) => Showcase.Steady && a != null && ReferenceEquals(a, LocalPlayer.actor);

        [HarmonyLib.HarmonyPatch(typeof(Actor), nameof(Actor.FallOver))]
        [HarmonyLib.HarmonyPrefix]
        private static bool FallOver(Actor __instance) => !Player(__instance);

        [HarmonyLib.HarmonyPatch(typeof(Actor), nameof(Actor.KnockOver))]
        [HarmonyLib.HarmonyPrefix]
        private static bool KnockOver(Actor __instance) => !Player(__instance);

        [HarmonyLib.HarmonyPatch(typeof(Actor), nameof(Actor.Damage))]
        [HarmonyLib.HarmonyPrefix]
        private static bool Damage(Actor __instance, ref bool __result)
        {
            if (!Player(__instance))
            {
                return true;
            }
            __result = false;
            return false;
        }
    }
}
