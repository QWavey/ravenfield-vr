using System;
using System.Collections;
using System.Linq;
using RavenfieldVR.Rig;
using RavenfieldVR.Weapons;
using UnityEngine;

namespace RavenfieldVR.Core.Showcase
{
    /// <summary>
    /// Vehicles (the user, 2026-11-09: "vehicles driving at me (enemys) and i shoot at them with RPGs and then at Helicopters too"): an
    /// enemy jeep driven at the player and a rocket into it, then an enemy helicopter flying in low and a second rocket. The vehicles are
    /// the map's own, put out in front with an enemy soldier at the controls (his AI paused) and moved along by the scene (the vehicle's
    /// own physics, its speed set each frame), so they come where the cameras look.
    /// </summary>
    internal static partial class Showcase
    {
        /// <summary>The launcher's rocket speed (m/s), for leading a moving vehicle; 60 when it cannot be read.</summary>
        private static float RocketSpeed(Weapon w)
        {
            GameObject prefab = w != null && w.configuration != null ? w.configuration.projectilePrefab : null;
            Projectile p = prefab != null ? prefab.GetComponent<Projectile>() : null;
            return p != null && p.configuration != null && p.configuration.speed > 1f ? p.configuration.speed : 60f;
        }

        /// <summary>A vehicle of the map put at a point facing a direction, still, with an enemy soldier at its first seat (AI paused).</summary>
        private static bool Crewed(Vehicle v, Vector3 at, Vector3 facing)
        {
            Actor player = LocalPlayer.actor;
            Quaternion rot = Quaternion.LookRotation(facing, Vector3.up);
            v.transform.SetPositionAndRotation(at, rot);
            if (v.rigidbody != null)
            {
                v.rigidbody.position = at;
                v.rigidbody.rotation = rot;
                v.rigidbody.velocity = Vector3.zero;
                v.rigidbody.angularVelocity = Vector3.zero;
            }
            // (Any living enemy, out of whatever seat he was in: in the sound pass the free ones were gone, the picture pass's crew
            // still in the wrecks, and the jeep came with no one at the wheel.)
            Actor crew = ActorManager.instance.actors.Where(a => a != null && a.aiControlled && !a.dead && a.team != player.team && (!a.IsSeated() || a.seat.vehicle != v))
                                                     .OrderBy(a => a.IsSeated() ? 1 : 0).FirstOrDefault();
            if (crew == null || v.seats == null || v.seats.Count == 0)
            {
                return false;
            }
            if (crew.IsSeated())
            {
                crew.LeaveSeat(false);
            }
            if (crew.transform.parent != crew.originalParent)
            {
                crew.transform.SetParent(crew.originalParent, true);
            }
            if (crew.controller != null)
            {
                crew.controller.enabled = true;
            }
            crew.SpawnAt(at + Vector3.up * 2f, rot);
            bool seated = crew.EnterSeat(v.seats[0], true, true);
            if (crew.controller != null)
            {
                crew.controller.enabled = false; // (his AI paused: the scene drives)
            }
            return seated;
        }

        /// <summary>The scene's launcher: the first of the named ones there is (workshop RPG-7s, else -rfvr-showcase-guns), in the primary slot.</summary>
        private static string GiveLauncher()
        {
            Actor actor = LocalPlayer.actor;
            if (actor == null || WeaponManager.instance == null)
            {
                return null;
            }
            foreach (string name in new[] { "RPG-7", "RPG-7V [PG-7V]", "RPG-7V" }.Concat(MontageGuns()))
            {
                WeaponManager.WeaponEntry e = WeaponManager.instance.allWeapons.FirstOrDefault(w => w != null && w.prefab != null && string.Equals(w.name, name, StringComparison.OrdinalIgnoreCase));
                if (e != null)
                {
                    actor.EquipNewWeaponEntry(e, 0, true);
                    return e.name;
                }
            }
            return null;
        }

        /// <summary>
        /// The rocket's flight in slow motion (the user, on trailer v7: "also not slowmotion while it flew"): time down to 0.15 as it leaves,
        /// a camera close behind it looking at the vehicle until it arrives (by game time), the vehicle finished there, the blast from
        /// <paramref name="hitCam"/> as time comes back to full speed.
        /// </summary>
        private static IEnumerator RocketInto(ShowcaseRecorder rec, Vehicle v, Action drive, Vector3 hitCam, string name, Action<Vehicle> finish, float speed)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            Weapon w = held != null && held.Weapon != null ? held.Weapon.GetActiveSubWeapon() : null;
            Vector3 from = w != null && w.CurrentMuzzle() != null ? w.CurrentMuzzle().position : VRRig.Instance.Head.position;
            Projectile flying = UnityEngine.Object.FindObjectsOfType<Projectile>().Where(x => x != null && x.gameObject.activeInHierarchy)
                                          .OrderBy(x => (x.transform.position - from).sqrMagnitude).FirstOrDefault();
            if (flying != null && (flying.transform.position - from).sqrMagnitude > 25f)
            {
                flying = null; // (not this shot's)
            }
            Vector3 at = flying != null ? flying.transform.position : from;
            Vector3 dir = (v.transform.position - at).normalized;
            float flight = Vector3.Distance(at, v.transform.position) / Mathf.Max(1f, speed);
            const float slow = 0.15f; // (at 0.3 the 295 m/s rocket crossed 60 m in 0.6 s of video)
            GameManager.SetTimeScale(slow);
            rec.Mark = name + " rocket";
            rec.Shot = () =>
            {
                Vector3 cam = at - dir * 2.2f + Vector3.up * 0.35f;
                return new ShowcaseCamera.Shot { Valid = true, Name = "behind the rocket", Position = cam, Rotation = Quaternion.LookRotation(v.transform.position + Vector3.up * 0.6f - cam, Vector3.up), Fov = 55f, ShowBody = true };
            };
            float t0 = Time.time;
            yield return Film(rec, Mathf.Clamp(flight / slow, 0.4f, 3f), u =>
            {
                drive();
                if (flying != null && flying.gameObject.activeInHierarchy)
                {
                    at = flying.transform.position;
                    dir = flying.transform.forward;
                }
                else
                {
                    at = Vector3.MoveTowards(at, v.transform.position, speed * Time.deltaTime);
                }
                if (Time.time - t0 >= flight)
                {
                    finish(v);
                }
            });
            finish(v);
            rec.Mark = name + " hit";
            rec.Shot = () => ShowcaseCamera.Fixed(hitCam, v.transform.position + Vector3.up * 0.8f, 52f, "the blast");
            yield return Film(rec, 1.4f, u =>
            {
                GameManager.SetTimeScale(Mathf.Lerp(slow, 1f, Ease(Mathf.Min(1f, u * 1.3f))));
                if (u >= 0.3f)
                {
                    rec.Still("vehicles-" + name + "-hit");
                }
            });
            GameManager.SetTimeScale(1f);
        }

        private static IEnumerator VehiclesSetup(Action<string> report)
        {
            report("showcase vehicles: " + Stage(40f, 160f));
            yield return new WaitForSecondsRealtime(1.2f);
            report($"showcase vehicles: turned {FaceOpen():0} degrees to the best view");
            string launcher = GiveLauncher();
            report($"showcase vehicles: launcher {launcher ?? "NO LAUNCHER FOUND"}");
            yield return new WaitForSecondsRealtime(0.8f);
            // The launcher's own slot: GunSlot takes guns of more than one round, and drew the revolver (the user, on trailer v7: "you used a
            // reveolver and not rpg to shoot them").
            Actor actor = LocalPlayer.actor;
            int slot = launcher != null && actor != null && actor.weapons != null && actor.weapons.Length > 0 && actor.weapons[0] != null ? 0 : -1; // (GiveLauncher's slot)
            if (slot < 0)
            {
                throw new InvalidOperationException("no launcher in the loadout");
            }
            yield return Draw(slot);
            report($"showcase vehicles: drawn {(WeaponSystem.Instance != null && WeaponSystem.Instance.Held != null && WeaponSystem.Instance.Held.Weapon != null ? WeaponSystem.Instance.Held.Weapon.name : "NOTHING")} (slot {slot})");
        }

        /// <summary>
        /// The vehicle scene (about 8 s): an enemy jeep comes at the player from 60 m (over the shoulder), up into the sights with the lead
        /// for its speed, the rocket, the hit from the side (a camera beside the jeep); a second rocket put in; an enemy helicopter flies
        /// in low (over the shoulder, looking up), the sights on it, the rocket, the hit from below. A vehicle the rocket did not stop is
        /// finished as the rocket arrives (the trailer's blast in the picture).
        /// </summary>
        private static IEnumerator VehiclesScene(ShowcaseRecorder rec, Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            VRRig rig = VRRig.Instance;
            Actor player = LocalPlayer.actor;
            if (held == null || rig == null || player == null || ActorManager.instance == null || ActorManager.instance.vehicles == null)
            {
                report("showcase vehicles: NO GUN HELD");
                yield break;
            }
            Weapon w = held.Weapon != null ? held.Weapon.GetActiveSubWeapon() : null;
            float rocket = RocketSpeed(w);
            Vector3 fwd = Flat(rig.Head.forward), side = Vector3.Cross(Vector3.up, fwd);
            // (A jeep is the game's ArcadeCar: Dustbowl's 50 vehicles had no Car. Then a tank; for the air, a helicopter, then a plane.)
            Vehicle car = ActorManager.instance.vehicles.FirstOrDefault(v => v != null && !v.dead && (v is ArcadeCar || v is Car))
                          ?? ActorManager.instance.vehicles.FirstOrDefault(v => v != null && !v.dead && v is Tank);
            Vehicle heli = ActorManager.instance.vehicles.FirstOrDefault(v => v != null && !v.dead && v is Helicopter)
                           ?? ActorManager.instance.vehicles.FirstOrDefault(v => v != null && !v.dead && (v is Airplane || v is Plane));
            report($"showcase vehicles: on the map {string.Join(", ", ActorManager.instance.vehicles.Where(v => v != null).GroupBy(v => v.GetType().Name).Select(k => k.Count() + " " + k.Key))}");
            if (car == null)
            {
                report("showcase vehicles: NO CAR on the map");
                yield break;
            }
            Vector3 carAt = player.Position() + fwd * 60f + side * 4f;
            if (Physics.Raycast(carAt + Vector3.up * 40f, Vector3.down, out RaycastHit g, 80f, LevelMask, QueryTriggerInteraction.Ignore))
            {
                carAt = g.point + Vector3.up * 0.6f;
            }
            bool carCrewed = Crewed(car, carAt, -fwd);
            if (w != null)
            {
                w.ammo = Mathf.Max(1, w.configuration != null ? w.configuration.ammo : 1); // (loaded: the sound pass found the tube empty after the picture pass)
            }
            yield return new WaitForSecondsRealtime(0.5f);
            rec.Start();
            void Drive(Vehicle v, Vector3 to, float speed, bool flat)
            {
                if (v == null || v.dead || v.rigidbody == null)
                {
                    return;
                }
                Vector3 d = to - v.rigidbody.position;
                if (flat)
                {
                    d = Flat(d);
                }
                if (d.sqrMagnitude < 4f)
                {
                    return;
                }
                Vector3 vel = d.normalized * speed;
                v.rigidbody.velocity = flat ? new Vector3(vel.x, v.rigidbody.velocity.y, vel.z) : vel;
                v.rigidbody.angularVelocity = Vector3.zero;
                v.rigidbody.MoveRotation(Quaternion.Slerp(v.rigidbody.rotation, Quaternion.LookRotation(flat ? d.normalized : Flat(d), Vector3.up), 0.2f));
            }
            Vector3 Lead(Vehicle v)
            {
                Vector3 at = v.transform.position + Vector3.up * 0.8f;
                float t = Vector3.Distance(rig.Head.position, at) / rocket;
                return at + (v.rigidbody != null ? v.rigidbody.velocity * t : Vector3.zero);
            }
            void Finish(Vehicle v)
            {
                if (v != null && !v.dead)
                {
                    var info = new DamageInfo(DamageInfo.DamageSourceType.Explosion, player, w) { healthDamage = 100000f, point = v.transform.position, direction = fwd };
                    v.Damage(info);
                    if (!v.dead)
                    {
                        v.Die(info); // (the helicopter took the damage and flew on: destroyed outright)
                    }
                }
            }
            Vector3 home = player.Position() + fwd * 3f;

            // The jeep comes: over the shoulder, the launcher low.
            rec.Mark = "jeep incoming";
            rec.Shot = () => OverShoulderAt(car.transform.position + Vector3.up, 1.6f, 0.6f, 0.4f, 46f);
            yield return Film(rec, 1.3f, u => { Drive(car, home, 13f, true); TurnToward(car.transform.position, 2f); HoldGun(0.15f, 0.3f); });
            // Up into the sights, the lead, the rocket.
            float aim = 0f;
            rec.Mark = "aim";
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(aim);
            yield return Film(rec, 0.45f, u => { Drive(car, home, 13f, true); aim = EaseOut(u); TurnToward(car.transform.position, 4f); GunPose p = AimAt(Lead(car), aim); PlaceGun(p.At, p.Aim, true); });
            rec.Mark = "fire";
            bool fired = false;
            yield return Moment(rec, () => { Drive(car, home, 13f, true); GunPose p = AimAt(Lead(car), 1f); PlaceGun(p.At, p.Aim, true); fired = Fire(0.5f); });
            Vector3 hitCam = car.transform.position + side * 9f + fwd * 4f + Vector3.up * 1.5f;
            yield return RocketInto(rec, car, () => { if (!car.dead) { Drive(car, home, 13f, true); } }, hitCam, "jeep", Finish, rocket);
            report($"showcase vehicles: the jeep {(car.dead ? "destroyed" : "NOT DESTROYED")} ({(carCrewed ? "an enemy at the wheel" : "no driver")}), {(fired ? "the rocket fired" : "THE ROCKET DID NOT FIRE")}, rocket {rocket:0} m/s");
            if (heli == null)
            {
                report("showcase vehicles: NO HELICOPTER on the map");
                yield break;
            }

            // A rocket in again (the reload is another scene's), the helicopter out in front, high and coming.
            if (w != null)
            {
                w.ammo = Mathf.Max(1, w.configuration != null ? w.configuration.ammo : 1);
            }
            Vector3 heliAt = player.Position() + fwd * 110f - side * 25f + Vector3.up * 30f;
            bool heliCrewed = Crewed(heli, heliAt, Flat(player.Position() - heliAt));
            Vector3 over = player.Position() + fwd * 8f + Vector3.up * 22f;
            rec.Mark = "heli incoming";
            rec.Shot = () => OverShoulderAt(heli.transform.position, 1.4f, 0.5f, 0.2f, 44f);
            yield return Film(rec, 1.3f, u => { Drive(heli, over, 22f, false); TurnToward(heli.transform.position, 3f); HoldGun(0.3f, 0.3f); });
            aim = 0f;
            rec.Mark = "aim";
            rec.Shot = () => ShowcaseCamera.FirstPersonAim(aim);
            yield return Film(rec, 0.45f, u => { Drive(heli, over, 22f, false); aim = EaseOut(u); TurnToward(heli.transform.position, 4f); GunPose p = AimAt(Lead(heli), aim); PlaceGun(p.At, p.Aim, true); });
            rec.Mark = "fire";
            fired = false;
            yield return Moment(rec, () => { Drive(heli, over, 22f, false); GunPose p = AimAt(Lead(heli), 1f); PlaceGun(p.At, p.Aim, true); fired = Fire(0.5f); });
            Vector3 lowCam = player.Position() + fwd * 4f + side * 6f + Vector3.up * 0.8f;
            yield return RocketInto(rec, heli, () => { if (!heli.dead) { Drive(heli, over, 22f, false); } }, lowCam, "heli", Finish, rocket);
            report($"showcase vehicles: the {(heli is Helicopter ? "helicopter" : "plane")} {(heli.dead ? "destroyed" : "NOT DESTROYED")} ({(heliCrewed ? "an enemy at the controls" : "no pilot")}), {(fired ? "the rocket fired" : "THE ROCKET DID NOT FIRE")}");
        }
    }
}
