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
        /// <summary>
        /// The gun held up in front of the chest, across the body, its top tipped away so the magazine well and the hands at it face
        /// the eyes. Higher and flatter than first tried (reload take 1: held low and steep under a steep look, the picture was mostly
        /// ground).
        /// </summary>
        private static GunPose Presented() => InFront(new Vector3(0.04f, -0.17f, 0.40f), -34f, -4f, 0.45f);

        /// <summary>The gun put in its presented pose (<see cref="Presented"/>), the support hand on it or not.</summary>
        private static void PlacePresented(bool support)
        {
            GunPose g = Presented();
            PlaceGun(g.At, g.Aim, support);
        }

        /// <summary>How far the eyes turn from straight ahead to the gun held presented (0-1): enough to keep the horizon in the picture.</summary>
        private const float LookAtHands = 0.6f;

        /// <summary>A rifle scene's setup: a stage clear of vehicles, the best view, the game's PATRIOT drawn.</summary>
        private static Func<Action<string>, IEnumerator> RifleSetup(string scene) => report => RifleSetupSteps(scene, report);

        private static IEnumerator RifleSetupSteps(string scene, Action<string> report)
        {
            report($"showcase {scene}: " + Stage());
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase {scene}: turned {FaceOpen():0} degrees to the best view");
            // The first of -rfvr-showcase-guns (a workshop rifle: the user, "concentrate on more modded weapons"), else the PATRIOT.
            string rifle = GiveRifle(MontageGuns()[0], "PATRIOT", "RK-44");
            report($"showcase {scene}: rifle {rifle ?? "NO RIFLE FOUND (the loadout's own)"}");
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
            Vector3 underWell = well - held.Rig.Data.Up.normalized * 0.10f;
            rec.Start();

            // Up in front of the chest, the eyes going down to the well (0.5 s).
            float look = 0f;
            rec.Mark = "present";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(middle), look);
            yield return Film(rec, 0.5f, u =>
            {
                look = Ease(u) * LookAtHands;
                GunPose g = GunPose.Lerp(HipToEye(0f, 0f), Presented(), EaseOut(u));
                PlaceGun(g.At, g.Aim, true);
                if (u >= 0.95f)
                {
                    rec.Still("reload-presented");
                }
            });

            // The support hand lets go of the handguard (the grip opened: the reload code takes it as free).
            VRHand off = rig.Off;
            var life = new ShowcaseGunLife(); // (the gun hand alive while the other works: the user, "too stiff")
            int side = (int)off.Side;
            Input.VRInput.SimulatedGrab[side] = false;
            rec.Mark = "release"; // (not "let go": the reload's own last move is called that)
            yield return Film(rec, 0.12f, u => life.Place(rec, Presented(), null, off, false));
            if (off.Role != HandRole.Free)
            {
                report($"showcase reload: the support hand still {off.Role} after letting go (made free)");
                held.SetTwoHanded(false);
                off.Role = HandRole.Free;
            }

            // The reload by hand: reach, grab, out, held, in, seated, let go (3.25 s of game time).
            var lines = new System.Collections.Generic.List<string>();
            Runtime.RigidPose? start = Rig.VRHand.Scripted[side], lastOff = start;
            yield return Choreography(rec, 3.25, held.Reload.ReloadFilm(lines.Add, note =>
            {
                lastOff = Rig.VRHand.Scripted[side] ?? lastOff; // (it clears the hand's pose when it ends: the regrip eases from this)
                rec.Mark = note;
                if (note == "held" || note == "pushing in")
                {
                    rec.Still(note == "held" ? "reload-magazine-out" : "reload-magazine-in-first-person");
                }
                // The close-up aimed 10 cm under the well and further off (take 1: aimed at the empty well, the magazine and the hand
                // holding it were at the picture's bottom edge).
                bool closeUp = note == "pulling out" || note == "held";
                rec.Shot = closeUp
                    ? (Func<ShowcaseCamera.Shot>)(() => ShowcaseCamera.CloseUp(underWell, new Vector3(-0.55f, 0.02f, 0.06f), 50f))
                    : () => ShowcaseCamera.FirstPersonLook(PlacedPoint(well), LookAtHands);
                KeepAlive();
                life.Place(rec, Presented(), note, off, false);
                rec.TakeFrame();
            }, () => rec.Elapsed, start));
            // (Each of its seven moves runs a frame or so past its time, in both passes alike: the next step's planned end absorbs it.)

            // The rack (0.93 s): the free hand on the charging handle, pulled back hard and let go; the eyes on the gun's rear.
            Vector3 rear = held.Rig.Data.EyePoint;
            yield return Choreography(rec, 0.93, held.Reload.RackFilm(lines.Add, note =>
            {
                lastOff = Rig.VRHand.Scripted[side] ?? lastOff;
                rec.Mark = "rack " + note;
                // From the gun's right, above and ahead of the handle: from the eyes the free arm's sleeve crossed the lens (take 1).
                rec.Shot = () => ShowcaseCamera.CloseUp(rear, new Vector3(0.34f, 0.17f, 0.24f), 48f);
                if (note == "let go")
                {
                    rec.Still("reload-racked");
                }
                KeepAlive();
                life.Place(rec, Presented(), "rack " + note, off, false);
                rec.TakeFrame();
            }, () => rec.Elapsed, lastOff));
            report("showcase reload: " + string.Join("; ", lines.ToArray()));

            // The hand back on the handguard (0.3 s, eased from where the reload left it), then up to the sights (0.4 s).
            Runtime.RigidPose? from = lastOff;
            rec.Mark = "regrip";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(middle), LookAtHands);
            yield return Film(rec, 0.3f, u =>
            {
                PlacePresented(true);
                if (from.HasValue && Rig.VRHand.Scripted[side].HasValue)
                {
                    Rig.VRHand.Scripted[side] = Runtime.RigidPose.Lerp(from.Value, Rig.VRHand.Scripted[side].Value, Ease(u));
                }
            });
            float aim = 0f;
            rec.Mark = "raise";
            rec.Shot = () => ShowcaseCamera.Blend(ShowcaseCamera.FirstPersonLook(PlacedPoint(middle), LookAtHands), ShowcaseCamera.FirstPersonAim(aim), aim);
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
            // Over the throwing shoulder, wide enough for the arm cocked back with the grenade (the helmet cam on that side saw only
            // the helmet, Coastline take 1).
            rec.Shot = () => ShowcaseCamera.OverShoulder(1.25f, 0.62f * -sign, 0.32f, 54f);
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
            // (11 m/s and higher: at 14 m/s the grenade went off 30 m out behind the rocks, the blast out of the picture.)
            Vector3 throwV = (Flat(head.forward) + Vector3.up * 0.55f).normalized * 11f;
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
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(last, 0.85f, 56f);
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
        /// The pistols the pistol scene can try, best first: secondaries that fire one round a pull (not automatic), 6-21 rounds, no
        /// pair. (By name it took the "SL-DEFENDER", a primary, and the scene drew the loadout's sniper: pistol take 1.) Whether each
        /// is a pistol is the mod's own reading of it once drawn (<c>IsPistol</c>).
        /// </summary>
        private static System.Collections.Generic.List<WeaponManager.WeaponEntry> SidearmCandidates()
        {
            var list = new System.Collections.Generic.List<WeaponManager.WeaponEntry>();
            if (WeaponManager.instance == null)
            {
                return list;
            }
            foreach (WeaponManager.WeaponEntry e in WeaponManager.instance.allWeapons)
            {
                Weapon w = e != null && e.prefab != null ? e.prefab.GetComponent<Weapon>() : null;
                if (w != null && !(w is MeleeWeapon) && !(w is ThrowableWeapon) && e.slot == WeaponManager.WeaponSlot.Secondary && w.configuration != null
                    && !w.configuration.auto && w.configuration.ammo >= 6 && w.configuration.ammo <= 21
                    && e.name.IndexOf("akimbo", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    list.Add(e);
                }
            }
            return list;
        }

        private static int _sidearmSlot = -1;

        /// <summary>The pistol scene's setup: the stage, the best view, a pistol in its slot and nothing in the hands.</summary>
        private static IEnumerator PistolSetup(Action<string> report)
        {
            report("showcase pistol: " + Stage());
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase pistol: turned {FaceOpen():0} degrees to the best view");
            GiveRifle("PATRIOT", "RK-44");
            Actor actor = LocalPlayer.actor;
            string tried = "";
            _sidearmSlot = -1;
            foreach (WeaponManager.WeaponEntry e in SidearmCandidates())
            {
                int slot = Mathf.Clamp((int)e.slot, 0, actor.weapons.Length - 1);
                actor.EquipNewWeaponEntry(e, slot, true);
                yield return new WaitForSecondsRealtime(0.4f);
                yield return Draw(slot); // (drawn once: the mod reads it, its grip and rack point measured before the take)
                HeldWeapon drawn = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                // A pistol with a slide to rack: a chamber and a magazine (take 2 drew the .357 CONDOR, a revolver: nothing to rack).
                if (drawn != null && drawn.Weapon == actor.weapons[slot] && drawn.Rig.Data.IsPistol && drawn.ActiveAmmo != null
                    && drawn.ActiveAmmo.UsesChamber && drawn.ActiveAmmo.UsesDetachableMag)
                {
                    _sidearmSlot = slot;
                    report($"showcase pistol: {e.name} in slot {slot}{(tried.Length > 0 ? $" (not pistols: {tried})" : "")}");
                    break;
                }
                tried += (tried.Length > 0 ? ", " : "") + e.name;
            }
            if (_sidearmSlot < 0)
            {
                throw new InvalidOperationException($"no pistol among the game's secondaries (tried: {tried})");
            }
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
            float rise = 0.06f;
            yield return Until(rec, () => sys.Held != null && sys.Held.Weapon == pistolWeapon, 1f, () =>
            {
                rise = Mathf.Min(0.14f, rise + 0.002f);
                Rig.VRHand.Scripted[side] = new Runtime.RigidPose(holster + Vector3.up * rise, down);
            });
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
            yield return Choreography(rec, 0.93, held.Reload.RackFilm(lines.Add, note =>
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
            }, () => rec.Elapsed, lastOff));
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

        /// <summary>
        /// Slow motion with the hands at their own speed (about 7 s of video): a burst on the helmet cam; time sinks to a fifth (the
        /// game's own slow motion: its sound sinks with it) while the trigger is held, seen beside the gun's receiver, the cases
        /// drifting out; the trigger let go, the hands change the magazine at full speed in the slowed world (first person); time back,
        /// a last shot.
        /// </summary>
        private static IEnumerator SlowmoScene(ShowcaseRecorder rec, Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            VRRig rig = VRRig.Instance;
            if (held == null || rig == null || GameManager.instance == null)
            {
                report("showcase slowmo: NO GUN HELD");
                yield break;
            }
            Vector3 receiver = Vector3.Lerp(held.Rig.Data.EyePoint, held.Rig.Data.MagSeat.position, 0.5f);
            Vector3 well = held.Rig.Data.MagSeat.position;
            const float slow = 0.2f;
            rec.Start();
            int ammo = int.MaxValue;

            // Full speed, the burst starting (0.6 s).
            rec.Mark = "burst";
            rec.Shot = () => ShowcaseCamera.HelmetCam();
            yield return Film(rec, 0.6f, u => { HoldGun(1f, 0.2f); HoldTrigger(ref ammo); });

            // Time sinking to a fifth over 0.35 s of video, then held there (1.4 s), the trigger still held: beside the receiver.
            rec.Mark = "slowing";
            rec.Shot = () => ShowcaseCamera.CloseUp(receiver, new Vector3(0.34f, 0.07f, 0.16f), 44f);
            yield return Film(rec, 0.35f, u =>
            {
                GameManager.SetTimeScale(Mathf.Lerp(1f, slow, EaseOut(u)));
                HoldGun(1f, 0.2f);
                HoldTrigger(ref ammo);
            });
            rec.Mark = "slow burst";
            yield return Film(rec, 1.4f, u =>
            {
                HoldGun(1f, 0.2f);
                HoldTrigger(ref ammo, 0.12f);
                if (u >= 0.5f)
                {
                    rec.Still("slowmo-cases");
                }
            });
            ReleaseTrigger();

            // In the slowed world, the hands at full speed: presented, the magazine out and in (first person, on the video's clock).
            rec.Mark = "present";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(PlacedPoint(well), LookAtHands);
            yield return Film(rec, 0.3f, u =>
            {
                GunPose g = GunPose.Lerp(HipToEye(1f, 0f), Presented(), EaseOut(u));
                PlaceGun(g.At, g.Aim, true);
            });
            VRHand off = rig.Off;
            var life = new ShowcaseGunLife(); // (the gun hand alive while the other works: the user, "too stiff")
            int side = (int)off.Side;
            Input.VRInput.SimulatedGrab[side] = false;
            rec.Mark = "release";
            yield return Film(rec, 0.1f, u => life.Place(rec, Presented(), null, off, false));
            if (off.Role != HandRole.Free)
            {
                held.SetTwoHanded(false);
                off.Role = HandRole.Free;
            }
            var lines = new System.Collections.Generic.List<string>();
            Runtime.RigidPose? lastOff = Rig.VRHand.Scripted[side];
            // The natural swap (the montage's: dropped, the pouch, in with a slap; the user on ReloadFilm's: "it kindoff stutters").
            yield return Choreography(rec, 1.36, held.Reload.SwapFilm(lines.Add, note =>
            {
                lastOff = Rig.VRHand.Scripted[side] ?? lastOff;
                rec.Mark = "slow " + note;
                if (note == "inserting")
                {
                    rec.Still("slowmo-magazine-in");
                }
                KeepAlive();
                life.Place(rec, Presented(), note, off, false);
                rec.TakeFrame();
            }, () => rec.Elapsed, lastOff));
            report("showcase slowmo: " + string.Join("; ", lines.ToArray()));

            // Time back over 0.3 s as the hand goes back and the rifle comes up; a shot at full speed.
            Runtime.RigidPose? from = lastOff;
            float aim = 0f;
            rec.Mark = "back";
            rec.Shot = () => ShowcaseCamera.Blend(ShowcaseCamera.FirstPersonLook(PlacedPoint(well), LookAtHands), ShowcaseCamera.FirstPersonAim(aim), aim);
            yield return Film(rec, 0.45f, u =>
            {
                GameManager.SetTimeScale(Mathf.Lerp(slow, 1f, Ease(Mathf.Min(1f, u * 1.5f))));
                aim = EaseOut(u);
                GunPose g = GunPose.Lerp(Presented(), HipToEye(1f, 0.3f), aim);
                PlaceGun(g.At, g.Aim, true);
                if (from.HasValue && Rig.VRHand.Scripted[side].HasValue)
                {
                    Rig.VRHand.Scripted[side] = Runtime.RigidPose.Lerp(from.Value, Rig.VRHand.Scripted[side].Value, Ease(Mathf.Min(1f, u * 2f)));
                }
            });
            GameManager.SetTimeScale(1f);
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(1f);
            rec.Mark = "shot";
            bool fired = false;
            yield return Moment(rec, () => { HoldGun(1f, 0.3f); fired = Fire(); });
            rec.Mark = "after";
            yield return Film(rec, 0.5f, u => HoldGun(1f, 0.3f));
            report($"showcase slowmo: slowed to {slow:0.0}, the magazine changed at full speed, {(fired ? "a last shot" : "THE LAST SHOT DID NOT FIRE")}");
        }

        /// <summary>The scope scene's setup: the stage, the best view (toward the battle), the game's bolt-action Recon LRR drawn.</summary>
        private static IEnumerator ScopeSetup(Action<string> report)
        {
            report("showcase scope: " + Stage());
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase scope: turned {FaceOpen():0} degrees to the best view");
            string rifle = GiveRifle("recon lrr");
            report($"showcase scope: rifle {rifle ?? "NO RIFLE FOUND (the loadout's own)"}");
            yield return new WaitForSecondsRealtime(0.8f);
            int slot = GunSlot(0);
            if (slot < 0)
            {
                throw new InvalidOperationException("no gun in the loadout");
            }
            yield return Draw(slot);
        }

        /// <summary>
        /// Through the scope (about 4.5 s): the bolt-action rifle up to the eye, the view going into its own lens (the mod's magnified
        /// picture), a shot; lowered a little, the free hand works the bolt back and home (first person, looking at it); into the
        /// scope again, a second shot.
        /// </summary>
        private static IEnumerator ScopeScene(ShowcaseRecorder rec, Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            VRRig rig = VRRig.Instance;
            if (held == null || rig == null)
            {
                report("showcase scope: NO GUN HELD");
                yield break;
            }
            Vector3 bolt = held.Rig.Data.EyePoint;
            rec.Start();
            float aim = 0f;
            rec.Mark = "aim";
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(aim, 62f, 46f);
            yield return Film(rec, 0.55f, u =>
            {
                aim = EaseOut(u);
                HoldGun(aim, 0.25f);
            });
            rec.Mark = "steady";
            yield return Film(rec, 0.35f, u => { HoldGun(1f, 0.15f); if (u >= 0.5f) { rec.Still("scope-view"); } });
            rec.Mark = "shot";
            bool first = false;
            yield return Moment(rec, () => { HoldGun(1f, 0.15f); first = Fire(0.45f); });
            rec.Mark = "recoil";
            yield return Film(rec, 0.4f, u => HoldGun(1f, 0.15f));

            // Lowered from the eye, the bolt worked by the free hand (back and home), the eyes on it.
            GunPose low = InFront(new Vector3(0.05f, -0.16f, 0.4f), -10f, -4f, 0.15f);
            float down = 0f;
            rec.Mark = "lower";
            rec.Shot = () => ShowcaseCamera.Blend(ShowcaseCamera.FirstPersonAim(1f, 62f, 46f), ShowcaseCamera.FirstPersonLook(PlacedPoint(bolt), 0.7f), down);
            yield return Film(rec, 0.3f, u =>
            {
                down = EaseOut(u);
                GunPose g = GunPose.Lerp(HipToEye(1f, 0.15f), low, down);
                PlaceGun(g.At, g.Aim, true);
            });
            VRHand off = rig.Off;
            int side = (int)off.Side;
            Input.VRInput.SimulatedGrab[side] = false;
            rec.Mark = "release";
            yield return Film(rec, 0.08f, u => PlaceGun(low.At, low.Aim, false));
            if (off.Role != HandRole.Free)
            {
                held.SetTwoHanded(false);
                off.Role = HandRole.Free;
            }
            var lines = new System.Collections.Generic.List<string>();
            Runtime.RigidPose? lastOff = Rig.VRHand.Scripted[side];
            yield return Choreography(rec, 1.11, held.Reload.RackFilm(lines.Add, note =>
            {
                lastOff = Rig.VRHand.Scripted[side] ?? lastOff;
                rec.Mark = "bolt " + note;
                if (note == "pulling back")
                {
                    rec.Still("scope-bolt-back");
                }
                KeepAlive();
                PlaceGun(low.At, low.Aim, false);
                rec.TakeFrame();
            }, () => rec.Elapsed, lastOff));
            report("showcase scope: " + string.Join("; ", lines.ToArray()));

            // Into the scope again (0.45 s, the hand back on the stock's front), a second shot.
            Runtime.RigidPose? from = lastOff;
            float up = 0f;
            rec.Mark = "aim";
            rec.Shot = () => ShowcaseCamera.Blend(ShowcaseCamera.FirstPersonLook(PlacedPoint(bolt), 0.7f), ShowcaseCamera.FirstPersonAim(1f, 62f, 46f), up);
            yield return Film(rec, 0.45f, u =>
            {
                up = EaseOut(u);
                GunPose g = GunPose.Lerp(low, HipToEye(1f, 0.15f), up);
                PlaceGun(g.At, g.Aim, true);
                if (from.HasValue && Rig.VRHand.Scripted[side].HasValue)
                {
                    Rig.VRHand.Scripted[side] = Runtime.RigidPose.Lerp(from.Value, Rig.VRHand.Scripted[side].Value, Ease(u));
                }
            });
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(1f, 62f, 46f);
            rec.Mark = "shot";
            bool second = false;
            yield return Moment(rec, () => { HoldGun(1f, 0.15f); second = Fire(0.45f); });
            rec.Mark = "after";
            yield return Film(rec, 0.5f, u => HoldGun(1f, 0.15f));
            report($"showcase scope: {(first ? "a shot" : "THE FIRST SHOT DID NOT FIRE")}, the bolt worked, {(second ? "a second shot" : "THE SECOND SHOT DID NOT FIRE")}");
        }

        /// <summary>The hands scene's setup: the stage, the best view, nothing in the hands.</summary>
        private static IEnumerator HandsSetup(Action<string> report)
        {
            report("showcase hands: " + Stage());
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase hands: turned {FaceOpen():0} degrees to the best view");
            if (LocalPlayer.actor != null && LocalPlayer.actor.HasUnholsteredWeapon())
            {
                LocalPlayer.actor.HolsterActiveWeapon();
            }
            yield return new WaitForSecondsRealtime(0.8f);
        }

        /// <summary>
        /// The player's own hands (about 3.5 s), the VR opener: both gloves come up in front of the face, palms to the eyes, close into
        /// fists and open, turn over to their backs; then the helmet cam on the same (the body's arms up, looking at its hands). The
        /// hands stay 35 cm out, clear of the goggles' and the chest pouch's grabs.
        /// </summary>
        private static IEnumerator HandsScene(ShowcaseRecorder rec, Action<string> report)
        {
            VRRig rig = VRRig.Instance;
            if (rig == null)
            {
                yield break;
            }
            Transform head = rig.Head;
            Quaternion level = Quaternion.LookRotation(Flat(head.forward), Vector3.up);
            Vector3 fwd = level * Vector3.forward, right = level * Vector3.right;
            VRHand[] hands = rig.Hands;
            var rawFromPalm = new Runtime.RigidPose[2];
            var start = new Runtime.RigidPose[2];
            for (int i = 0; i < 2; i++)
            {
                rawFromPalm[i] = hands[i].Palm.Inverse().Multiply(hands[i].Raw);
                start[i] = Rig.VRHand.Scripted[i] ?? hands[i].Raw;
            }
            // The palm frame (fingers +Z, thumb side +Y, the palm toward -X on the right hand, mirrored on the left): fingers up, palm to the
            // eyes, thumb outward; turned by `turn` degrees about the fingers (180: the back of the hand to the eyes).
            Runtime.RigidPose Palm(int i, float lift, float turn, float spread)
            {
                bool left = hands[i].IsLeft;
                Vector3 side = left ? -right : right;
                Vector3 at = head.position + fwd * 0.36f + side * (0.1f + spread) + Vector3.up * (-0.1f + lift);
                Quaternion faceIn = Quaternion.LookRotation(Vector3.up, side);
                Quaternion r = Quaternion.AngleAxis(turn * (left ? 1f : -1f), Vector3.up) * faceIn;
                return new Runtime.RigidPose(at, r).Multiply(rawFromPalm[i]);
            }
            void Pose(float lift, float turn, float spread, float k)
            {
                for (int i = 0; i < 2; i++)
                {
                    Runtime.RigidPose to = Palm(i, lift, turn, spread);
                    Rig.VRHand.Scripted[i] = k < 1f ? Runtime.RigidPose.Lerp(start[i], to, k) : to;
                }
            }
            rec.Start();
            Vector3 between = head.position + fwd * 0.36f - Vector3.up * 0.06f;
            rec.Mark = "raise";
            rec.Shot = () => ShowcaseCamera.FirstPersonLook(between, 0.35f);
            yield return Film(rec, 0.6f, u => Pose(-0.08f * (1f - u), 0f, 0.02f, EaseOut(u)));
            rec.Mark = "fists";
            Input.VRInput.SimulatedGrab[0] = Input.VRInput.SimulatedGrab[1] = true;
            yield return Film(rec, 0.45f, u => { Pose(0f, 0f, 0.02f, 1f); if (u >= 0.6f) { rec.Still("hands-fists"); } });
            rec.Mark = "open";
            Input.VRInput.SimulatedGrab[0] = Input.VRInput.SimulatedGrab[1] = false;
            yield return Film(rec, 0.35f, u => Pose(0f, 0f, 0.02f, 1f));
            rec.Mark = "turn";
            yield return Film(rec, 0.8f, u => { Pose(0.02f * u, 165f * Ease(u), 0.02f + 0.04f * u, 1f); if (u >= 0.5f) { rec.Still("hands-turning"); } });
            rec.Mark = "helmet";
            rec.Shot = () => ShowcaseCamera.HelmetCam(0.4f, 0.16f, 0.1f);
            yield return Film(rec, 0.7f, u => { Pose(0.02f, 165f - 25f * u, 0.06f, 1f); if (u >= 0.5f) { rec.Still("hands-helmet-cam"); } });
            report("showcase hands: raised, closed, opened and turned over");
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
