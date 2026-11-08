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
        /// The reload, in first person with the hands in the picture (about 6 s): the rifle brought up in front of the chest and tipped
        /// toward the eyes, the eyes on the magazine well; the support hand lets go of the handguard and comes down to the magazine,
        /// pulls it out, holds it, pushes it home (the autotest's own reload by hand, <c>ReloadInteractions.ReloadFilm</c>, timed by game
        /// time here); a close-up from the gun's side while the magazine is out; the charging handle racked by hand (RackFilm); the hand back on the handguard, the rifle up to the
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
                if (u >= 0.95f)
                {
                    rec.Still("reload-presented");
                }
            });

            // The support hand lets go of the handguard (the grip opened: the reload code takes it as free).
            VRHand off = rig.Off;
            int side = (int)off.Side;
            Input.VRInput.SimulatedGrab[side] = false;
            rec.Mark = "release"; // (not "let go": the reload's own last move is called that)
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
            Runtime.RigidPose? start = Rig.VRHand.Scripted[side], lastOff = start;
            yield return held.Reload.ReloadFilm(lines.Add, note =>
            {
                lastOff = Rig.VRHand.Scripted[side] ?? lastOff; // (it clears the hand's pose when it ends: the regrip eases from this)
                rec.Mark = note;
                if (note == "held" || note == "pushing in")
                {
                    rec.Still(note == "held" ? "reload-magazine-out" : "reload-magazine-in-first-person");
                }
                bool closeUp = note == "pulling out" || note == "held";
                rec.Shot = closeUp
                    ? (Func<ShowcaseCamera.Shot>)(() => ShowcaseCamera.CloseUp(well, new Vector3(-0.42f, -0.06f, 0.02f), 46f))
                    : () => ShowcaseCamera.FirstPersonLook(PlacedPoint(well), 0.9f);
                KeepAlive();
                HoldGunAt(new Vector3(0.04f, -0.24f, 0.36f), -28f, -12f, 0.55f, false);
                rec.TakeFrame();
            }, () => Time.time, start);
            // (Each of its seven moves runs a frame or so past its time, in both passes alike: the next step's planned end absorbs it.)

            // The rack (0.93 s): the free hand on the charging handle, pulled back hard and let go; the eyes on the gun's rear.
            rec.Planned += 0.93; // (its moves: 0.35 + 0.08 + 0.2 + 0.3 s)
            Vector3 rear = held.Rig.Data.EyePoint;
            yield return held.Reload.RackFilm(lines.Add, note =>
            {
                lastOff = Rig.VRHand.Scripted[side] ?? lastOff;
                rec.Mark = "rack " + note;
                rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(rear), 0.8f);
                if (note == "let go")
                {
                    rec.Still("reload-racked");
                }
                KeepAlive();
                HoldGunAt(new Vector3(0.04f, -0.24f, 0.36f), -28f, -12f, 0.55f, false);
                rec.TakeFrame();
            }, () => Time.time, lastOff);
            report("showcase reload: " + string.Join("; ", lines.ToArray()));

            // The hand back on the handguard (0.3 s, eased from where the reload left it), then up to the sights (0.4 s).
            Runtime.RigidPose? from = lastOff;
            rec.Mark = "regrip";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(middle), 0.8f);
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
            rec.Shot = () => ShowcaseCamera.Blend(ShowcaseCamera.FirstPersonLook(PlacedPoint(middle), 0.8f), ShowcaseCamera.FirstPersonAim(aim), aim);
            yield return Film(rec, 0.4f, u =>
            {
                aim = EaseOut(u);
                GunPose g = GunPose.Lerp(Presented(), HipToEye(1f, 0.3f), aim);
                PlaceGun(g.At, g.Aim, true);
            });
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(1f);
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
                if (u >= 0.5f)
                {
                    rec.Still("inspect-turned");
                }
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

        /// <summary>
        /// A grenade from the free hand beside the rifle (about 5 s plus the fuse): the free hand takes a grenade from the loadout
        /// (the mod's second weapon, as in play), brings it up in front (first person, looking at it), pulls the pin with its trigger,
        /// winds up beside the head and throws (the helmet cam on the throwing side), then the eyes follow it out to the blast.
        /// </summary>
        private static IEnumerator GrenadeScene(ShowcaseRecorder rec, Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Actor actor = LocalPlayer.actor;
            VRRig rig = VRRig.Instance;
            HeldWeapon held = sys != null ? sys.Held : null;
            Weapon grenade = null;
            if (held != null && actor != null && actor.weapons != null)
            {
                foreach (Weapon w in actor.weapons)
                {
                    if (w is ThrowableWeapon && w.ammo != 0 && SecondWeapon.CanPair(held.Weapon, w))
                    {
                        grenade = w;
                        break;
                    }
                }
            }
            if (grenade == null || rig == null || sys.Second != null)
            {
                report("showcase grenade: NO GRENADE to take beside the rifle");
                yield break;
            }
            VRHand off = rig.Off;
            int side = (int)off.Side;
            float sign = off.IsLeft ? 1f : -1f; // (the free hand's side: left for a right-handed player)
            Transform head = rig.Head;
            Quaternion level = Quaternion.LookRotation(Flat(head.forward), Vector3.up);
            Quaternion grip = level * Quaternion.Euler(-25f, 12f * -sign, 0f);
            Vector3 At(float x, float y, float z) => head.position + level * new Vector3(x * -sign, y, z);
            Vector3 chest = At(-0.10f, -0.32f, 0.30f), show = At(-0.05f, -0.20f, 0.30f), back = At(-0.30f, 0.08f, -0.10f), release = At(-0.16f, 0.06f, 0.48f), follow = At(-0.02f, -0.28f, 0.45f);
            Runtime.RigidPose startRaw = Rig.VRHand.Scripted[side] ?? off.Raw;
            void OffAt(Vector3 p, Quaternion r) => Rig.VRHand.Scripted[side] = new Runtime.RigidPose(p, r);

            rec.Start();
            // The free hand lets go of the handguard and brings a grenade up (0.6 s: the grenade's own draw).
            Input.VRInput.SimulatedGrab[side] = false;
            rec.Mark = "draw";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(off.Palm.position, 0.7f);
            bool begun = false;
            yield return Film(rec, 0.6f, u =>
            {
                HoldGun(0.12f, 0.3f, false);
                if (!begun && off.Role == HandRole.Free)
                {
                    begun = true;
                    Input.VRInput.SimulatedGrab[side] = true;
                    sys.BeginSecond(grenade, off);
                }
                OffAt(Vector3.Lerp(Vector3.Lerp(startRaw.position, chest, EaseOut(Mathf.Min(1f, u * 2f))), show, Ease(Mathf.Max(0f, u * 2f - 1f))),
                      Quaternion.Slerp(startRaw.rotation, grip, EaseOut(u)));
            });
            SecondWeapon second = sys.Second;
            if (second == null)
            {
                report($"showcase grenade: THE FREE HAND DID NOT TAKE THE GRENADE (its role {off.Role})");
                yield break;
            }

            // The pin, pulled with that hand's trigger (0.3 s).
            rec.Mark = "pin";
            SecondWeapon.TestTrigger = true;
            yield return Film(rec, 0.3f, u => { HoldGun(0.12f, 0.3f, false); OffAt(show, grip); if (u >= 0.5f) { rec.Still("grenade-pin"); } });
            bool armed = second.Armed;

            // Wound up beside the head (0.35 s), thrown (0.15 s: let go at its end, up and away at 14 m/s), followed through.
            rec.Mark = "wind up";
            rec.Shot = () => ShowcaseCamera.HelmetCam(0.34f, 0.17f * -sign, 0.1f);
            yield return Film(rec, 0.35f, u =>
            {
                HoldGun(0.12f, 0.3f, false);
                OffAt(Vector3.Lerp(show, back, EaseOut(u)), grip * Quaternion.Euler(-40f * EaseOut(u), 0f, 0f));
                if (u >= 0.9f)
                {
                    rec.Still("grenade-wind-up");
                }
            });
            rec.Mark = "throw";
            yield return Film(rec, 0.15f, u => { HoldGun(0.12f, 0.3f, false); OffAt(Vector3.Lerp(back, release, u * u), grip * Quaternion.Euler(Mathf.Lerp(-40f, 30f, u), 0f, 0f)); });
            Vector3 throwV = (Flat(head.forward) + Vector3.up * 0.45f).normalized * 14f;
            second.TestThrow(throwV);
            SecondWeapon.TestTrigger = false;
            int live = second.LastLiveCount;
            GrenadeProjectile flying = null;
            float nearest = float.MaxValue;
            foreach (GrenadeProjectile g in UnityEngine.Object.FindObjectsOfType<GrenadeProjectile>())
            {
                float d = Vector3.Distance(g.transform.position, off.Palm.position);
                if (d < nearest)
                {
                    nearest = d;
                    flying = g;
                }
            }
            float fuse = flying != null && flying.configuration != null ? Mathf.Clamp(flying.configuration.lifetime, 1f, 5f) : 3.5f;

            // Followed out to the blast: the eyes after it, the hand coming down; held 0.9 s past the fuse.
            rec.Mark = "flight";
            Vector3 last = release + throwV.normalized * 3f;
            bool blown = false;
            float blastAt = 2f;
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(last, 0.55f, 60f);
            yield return Film(rec, 0.25f, u => { HoldGun(0.12f, 0.3f, false); OffAt(Vector3.Lerp(release, follow, EaseOut(u)), grip); last = flying != null ? flying.transform.position : last; });
            Input.VRInput.SimulatedGrab[side] = false;
            yield return Film(rec, fuse - 0.25f + 0.9f, u =>
            {
                HoldGun(0.12f, 0.3f, false);
                OffAt(follow, grip); // (left where the throw ended: put back on the handguard at once, the glove jumped there in the picture)
                // (Exploded, the projectile turns its own component off and stays active: its renderers off, its smoke playing.)
                if (flying != null && flying.enabled && flying.gameObject.activeInHierarchy)
                {
                    last = flying.transform.position;
                }
                else if (!blown)
                {
                    blown = true;
                    rec.Mark = "blast";
                    ShowcaseCamera.Shake.Add(0.45f, 0.4f);
                    blastAt = u;
                }
                if (blown && u >= blastAt + 0.06f)
                {
                    rec.Still("grenade-blast");
                }
            });
            report($"showcase grenade ({grenade.name}): {(armed ? "pin pulled" : "PIN NOT PULLED")}, {(live > 0 ? $"thrown live, fuse {fuse:0.0} s" : "NOTHING THROWN")}, "
                   + $"{(blown ? "went off in the picture" : "DID NOT GO OFF IN THE PICTURE")}");
        }

        /// <summary>
        /// A pistol for the pistol scene: by name first, else the first secondary that is a pistol (6-21 rounds, not automatic, no
        /// pair), put in its slot. Its name and slot, or null.
        /// </summary>
        private static (string name, int slot)? GiveSidearm(params string[] prefer)
        {
            Actor actor = LocalPlayer.actor;
            if (actor == null || WeaponManager.instance == null)
            {
                return null;
            }
            WeaponManager.WeaponEntry pick = null;
            foreach (string name in prefer)
            {
                pick = WeaponManager.instance.allWeapons.Find(e => e != null && e.prefab != null && string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase));
                if (pick != null)
                {
                    break;
                }
            }
            if (pick == null)
            {
                pick = WeaponManager.instance.allWeapons.Find(e =>
                {
                    Weapon w = e != null && e.prefab != null ? e.prefab.GetComponent<Weapon>() : null;
                    return w != null && !(w is MeleeWeapon) && !(w is ThrowableWeapon) && e.slot == WeaponManager.WeaponSlot.Secondary && w.configuration != null
                           && !w.configuration.auto && w.configuration.ammo >= 6 && w.configuration.ammo <= 21
                           && e.name.IndexOf("akimbo", StringComparison.OrdinalIgnoreCase) < 0;
                });
            }
            if (pick == null)
            {
                return null;
            }
            int slot = Mathf.Clamp((int)pick.slot, 0, actor.weapons.Length - 1);
            actor.EquipNewWeaponEntry(pick, slot, true);
            return (pick.name, slot);
        }

        private static int _sidearmSlot = -1;

        /// <summary>The pistol scene's setup: the stage, the best view, a pistol in its slot and nothing in the hands.</summary>
        private static IEnumerator PistolSetup(Action<string> report)
        {
            report("showcase pistol: " + Stage());
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase pistol: turned {FaceOpen():0} degrees to the best view");
            GiveRifle("PATRIOT", "RK-44");
            (string name, int slot)? pistol = GiveSidearm("SL-DEFENDER", "76 EAGLE");
            if (pistol == null)
            {
                throw new InvalidOperationException("no pistol among the game's weapons");
            }
            _sidearmSlot = pistol.Value.slot;
            report($"showcase pistol: {pistol.Value.name} in slot {pistol.Value.slot}");
            yield return Draw(_sidearmSlot); // (drawn once so its grip and rack point are measured before the take)
            LocalPlayer.actor.HolsterActiveWeapon();
            yield return new WaitForSecondsRealtime(0.8f);
        }

        /// <summary>
        /// The pistol (about 4.5 s): the hand goes down to the hip and draws (a camera at the hip), the pistol comes up to the chest and
        /// the free hand racks its slide (first person, looking at it), both hands close on it and it comes up to the sights, three
        /// quick shots (through the sights, then the helmet cam).
        /// </summary>
        private static IEnumerator PistolScene(ShowcaseRecorder rec, Action<string> report)
        {
            Actor actor = LocalPlayer.actor;
            VRRig rig = VRRig.Instance;
            WeaponSystem sys = WeaponSystem.Instance;
            if (actor == null || rig == null || sys == null || _sidearmSlot < 0)
            {
                report("showcase pistol: NO PISTOL");
                yield break;
            }
            VRHand dom = rig.Dominant;
            int side = (int)dom.Side;
            float sign = dom.IsLeft ? -1f : 1f;
            Transform head = rig.Head;
            Quaternion level = Quaternion.LookRotation(Flat(head.forward), Vector3.up);
            Vector3 holster = head.position + level * new Vector3(0.21f * sign, -0.72f, -0.02f);
            Quaternion down = level * Quaternion.Euler(70f, 0f, 0f); // (the hand pointing down at the holster)
            Runtime.RigidPose startRaw = Rig.VRHand.Scripted[side] ?? dom.Raw;
            Vector3 camAt = holster + level * new Vector3(0.28f * sign, 0.22f, 0.62f);
            rec.Start();

            // Down to the holster (0.3 s), seen from beside the hip.
            rec.Mark = "reach";
            rec.Shot = () => ShowcaseCamera.Fixed(camAt, holster + Vector3.up * 0.15f, 52f, "hip cam");
            yield return Film(rec, 0.3f, u => Rig.VRHand.Scripted[side] = new Runtime.RigidPose(Vector3.Lerp(startRaw.position, holster, EaseOut(u)),
                Quaternion.Slerp(startRaw.rotation, down, EaseOut(u))));
            // Drawn: the grip closed on the holster (the game's own draw of that slot), held until the pistol is in the hand.
            rec.Mark = "draw";
            Input.VRInput.SimulatedGrab[side] = true;
            actor.SwitchWeapon(_sidearmSlot);
            yield return Film(rec, 0.2f, u =>
            {
                Rig.VRHand.Scripted[side] = new Runtime.RigidPose(holster + Vector3.up * 0.06f * u, down);
                if (u >= 0.6f)
                {
                    rec.Still("pistol-draw-hip-cam");
                }
            });
            // The game's own draw takes the weapon's unholster time: the hand rises on out of the holster until the pistol is in it
            // (at most 1 s; the autotest's Draw waits up to 6 s for the same).
            Weapon pistolWeapon = actor.weapons[_sidearmSlot];
            for (int f = 0; f < 60 && (sys.Held == null || sys.Held.Weapon != pistolWeapon); f++)
            {
                float rise = 0.06f + 0.002f * f;
                yield return Moment(rec, () => Rig.VRHand.Scripted[side] = new Runtime.RigidPose(holster + Vector3.up * rise, down));
            }
            HeldWeapon held = sys.Held;
            if (held == null || !held.Rig.Data.IsPistol)
            {
                report($"showcase pistol: THE PISTOL IS NOT IN THE HAND ({(held != null ? held.Weapon.name : "nothing held")})");
                yield break;
            }
            Vector3 slide = Vector3.Lerp(held.Rig.Data.EyePoint, held.Rig.Data.MuzzlePoint, 0.4f);

            // Up to the chest, the eyes on it (0.35 s); the free hand racks the slide (0.93 s).
            GunPose chest = InFront(new Vector3(0.06f * sign, -0.22f, 0.32f), -18f * sign, -8f, 0.25f);
            Runtime.RigidPose drawnAt = Rig.VRHand.Scripted[side].Value;
            rec.Mark = "up";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(slide), 0.75f);
            yield return Film(rec, 0.35f, u =>
            {
                PlaceGun(chest.At, chest.Aim, false);
                Rig.VRHand.Scripted[side] = Runtime.RigidPose.Lerp(drawnAt, Rig.VRHand.Scripted[side].Value, EaseOut(u));
            });
            var lines = new System.Collections.Generic.List<string>();
            int offSide = (int)rig.Off.Side;
            Runtime.RigidPose? lastOff = Rig.VRHand.Scripted[offSide] ?? rig.Off.Raw;
            rec.Planned += 0.93;
            yield return held.Reload.RackFilm(lines.Add, note =>
            {
                lastOff = Rig.VRHand.Scripted[offSide] ?? lastOff;
                rec.Mark = "rack " + note;
                if (note == "let go")
                {
                    rec.Still("pistol-racked");
                }
                KeepAlive();
                PlaceGun(chest.At, chest.Aim, false);
                rec.TakeFrame();
            }, () => Time.time, lastOff);
            report("showcase pistol: " + string.Join("; ", lines.ToArray()));

            // Both hands on it and up to the sights (0.4 s): the free hand cups the grip.
            PistolCup = true;
            float aim = 0f;
            rec.Mark = "aim";
            rec.Shot = () => ShowcaseCamera.Blend(ShowcaseCamera.FirstPersonLook(PlacedPoint(slide), 0.75f), ShowcaseCamera.FirstPersonAim(aim), aim);
            Runtime.RigidPose? cupFrom = lastOff;
            yield return Film(rec, 0.4f, u =>
            {
                aim = EaseOut(u);
                GunPose g = GunPose.Lerp(chest, HipToEye(1f, 0.3f), aim);
                PlaceGun(g.At, g.Aim, true);
                if (cupFrom.HasValue && Rig.VRHand.Scripted[offSide].HasValue)
                {
                    Rig.VRHand.Scripted[offSide] = Runtime.RigidPose.Lerp(cupFrom.Value, Rig.VRHand.Scripted[offSide].Value, Ease(u));
                }
            });
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(1f);
            int shots = 0;
            for (int k = 0; k < 3; k++)
            {
                if (k == 2)
                {
                    rec.Shot = () => ShowcaseCamera.HelmetCam();
                }
                rec.Mark = "shot";
                yield return Moment(rec, () => { HoldGun(1f, 0.3f); if (Fire(0.26f)) { shots++; } rec.Still(k < 2 ? "pistol-shot" : "pistol-shot-helmet-cam"); });
                rec.Mark = "shots";
                yield return Film(rec, 0.2f, u => HoldGun(1f, 0.3f));
            }
            yield return Film(rec, 0.35f, u => HoldGun(1f, 0.3f));
            PistolCup = false;
            report($"showcase pistol: drawn, racked, {shots} shot(s)");
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
