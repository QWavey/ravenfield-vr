using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// A hand-thrown grenade in slow motion flies as in real time until it first touches something, as a magazine does (RealTimeFlight). A
    /// grenade has no body: the game moves it in its own Update by game time, so one thrown hard in slow motion went at a fifth of the
    /// throw and dropped near the feet (slow-motion scout, 2026-11-08; the dud with its pin in already flew in real time). Here its step
    /// is taken in real time with real gravity, its velocity kept in real m/s; at its first touch (the game's own cast says so) it is
    /// given back to the world's pace and the game's step bounces it. Paused, it waits; slow motion ended, the world's pace on.
    /// </summary>
    internal static class RealTimeGrenades
    {
        private sealed class Flight
        {
            public float Fuse;
            public float Flown;
        }

        private static readonly Dictionary<GrenadeProjectile, Flight> Flying = new Dictionary<GrenadeProjectile, Flight>();
        private static readonly AccessTools.FieldRef<GrenadeProjectile, float> FuseOf = AccessTools.FieldRefAccess<GrenadeProjectile, float>("detonationTime");
        private static readonly AccessTools.FieldRef<GrenadeProjectile, Vector3> SpinAxisOf = AccessTools.FieldRefAccess<GrenadeProjectile, Vector3>("rotationAxis");
        private static readonly AccessTools.FieldRef<GrenadeProjectile, float> SpinOf = AccessTools.FieldRefAccess<GrenadeProjectile, float>("angularSpeed");
        private const int GameMask = 2101249; // (GrenadeProjectile.LAYER_MASK)

        internal static int Count => Flying.Count;

        /// <summary>Right after a throw by hand set its velocity (the hand's, real m/s): in slow motion it flies in real time from here.</summary>
        internal static void Begin(GrenadeProjectile g)
        {
            float s = Time.timeScale;
            if (g == null || s <= 0.01f || s >= 0.999f || Core.VRConfig.SlowedHands)
            {
                return;
            }
            if (Flying.Count > 32)
            {
                var gone = new List<GrenadeProjectile>();
                foreach (GrenadeProjectile k in Flying.Keys)
                {
                    if (k == null || !k.isActiveAndEnabled)
                    {
                        gone.Add(k);
                    }
                }
                foreach (GrenadeProjectile k in gone)
                {
                    Flying.Remove(k);
                }
            }
            Flying[g] = new Flight { Fuse = FuseOf(g), Flown = 0f };
        }

        /// <summary>Its step this frame: false when taken here (the game's skipped), true for the game's own.</summary>
        internal static bool Step(GrenadeProjectile g)
        {
            if (!Flying.TryGetValue(g, out Flight f))
            {
                return true;
            }
            if (!Mathf.Approximately(FuseOf(g), f.Fuse))
            {
                Flying.Remove(g); // (a pooled grenade thrown again, by anyone: its flight was another's)
                return true;
            }
            float s = Time.timeScale;
            if (s < 0.01f)
            {
                return false; // paused or the time stop: it waits as the world does
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            f.Flown += dt;
            Vector3 v = g.velocity + Physics.gravity * dt;
            Vector3 step = v * dt;
            if (s >= 0.999f || f.Flown > 3f || step.sqrMagnitude > 1e-10f && Physics.SphereCast(new Ray(g.transform.position, step), g.radius, out _, step.magnitude * 2f, GameMask))
            {
                // (Its velocity left as it is: the game's step moves it by velocity times its own slowed step, so it goes on at the world's
                // pace by itself. Scaled down here too, a bounce in slow motion went on at the scale squared: 0.4 m/s for 2; scout.)
                Flying.Remove(g);
                return true;
            }
            g.velocity = v;
            g.transform.position += step;
            g.transform.Rotate(SpinAxisOf(g), SpinOf(g) * dt); // (its tumble, as the game's step turns it)
            return false;
        }
    }

    /// <summary>
    /// Runs for every grenade in the match every frame: anything thrown here (a game update renaming a field its refs read: thrown from the
    /// static initializer on every use) gives the game its own step for good, said once, instead of breaking every grenade's Update.
    /// </summary>
    [HarmonyPatch(typeof(GrenadeProjectile), "Update")]
    internal static class RealTimeGrenadePatch
    {
        private static bool _broken;

        private static bool Prefix(GrenadeProjectile __instance)
        {
            if (_broken)
            {
                return true;
            }
            try
            {
                return RealTimeGrenades.Count == 0 || RealTimeGrenades.Step(__instance);
            }
            catch (System.Exception e)
            {
                _broken = true;
                Core.Log.ExceptionOnce("RealTimeGrenades.Step", e); // (grenades go at the world's pace from here, as without the mod)
                return true;
            }
        }
    }

    /// <summary>
    /// The same for what else the hand throws that the game moves itself (<c>Projectile.UpdatePosition</c>: a molotov, a C4 charge): in slow
    /// motion its step is taken in real time, through the game's own <c>Travel</c> (its hits, a charge sticking, a molotov bursting), until
    /// it stops on something; then the world's pace. Its first step, the game's muzzle travel, is the game's.
    /// </summary>
    internal static class RealTimeProjectiles
    {
        private sealed class Flight
        {
            public float Expire;
            public float Flown;
        }

        private static readonly Dictionary<Projectile, Flight> Flying = new Dictionary<Projectile, Flight>();
        private static readonly AccessTools.FieldRef<Projectile, float> ExpireOf = AccessTools.FieldRefAccess<Projectile, float>("expireTime");
        private static readonly System.Reflection.MethodInfo TravelMethod = AccessTools.Method(typeof(Projectile), "Travel");
        private static readonly object[] Args = new object[1];

        internal static int Count => Flying.Count;

        internal static void Begin(Projectile p)
        {
            float s = Time.timeScale;
            if (p == null || p is GrenadeProjectile || p is RigidbodyProjectile || TravelMethod == null || s <= 0.01f || s >= 0.999f || Core.VRConfig.SlowedHands)
            {
                return;
            }
            if (Flying.Count > 32)
            {
                var gone = new List<Projectile>();
                foreach (Projectile k in Flying.Keys)
                {
                    if (k == null || !k.isActiveAndEnabled)
                    {
                        gone.Add(k);
                    }
                }
                foreach (Projectile k in gone)
                {
                    Flying.Remove(k);
                }
            }
            Flying[p] = new Flight { Expire = ExpireOf(p) };
        }

        internal static bool Step(Projectile p)
        {
            if (!Flying.TryGetValue(p, out Flight f))
            {
                return true;
            }
            if (!Mathf.Approximately(ExpireOf(p), f.Expire))
            {
                Flying.Remove(p); // (pooled and fired again, by anyone)
                return true;
            }
            if (p.performInfantryInitialMuzzleTravel)
            {
                return true; // (its first step: the game's travel back from the muzzle)
            }
            float s = Time.timeScale;
            if (s < 0.01f)
            {
                return false;
            }
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            f.Flown += dt;
            if (s >= 0.999f || f.Flown > 3f)
            {
                Flying.Remove(p); // (velocity left as it is: the game's slowed step gives the world's pace)
                return true;
            }
            p.velocity += Physics.gravity * p.configuration.gravityMultiplier * dt;
            Args[0] = p.velocity * dt;
            bool on;
            try
            {
                on = (bool)TravelMethod.Invoke(p, Args);
            }
            catch (System.Exception e)
            {
                Core.Log.ExceptionOnce("RealTimeProjectiles.Travel", e);
                Flying.Remove(p);
                return true;
            }
            if (!on || !p.enabled)
            {
                // (Stopped on something: the world's pace from here, by the game's own slowed step; scaled too, it went at the scale squared.)
                Flying.Remove(p);
            }
            return false;
        }
    }

    /// <summary>Runs for every bullet in the match every frame: guarded as <see cref="RealTimeGrenadePatch"/>.</summary>
    [HarmonyPatch(typeof(Projectile), "UpdatePosition")]
    internal static class RealTimeProjectilePatch
    {
        private static bool _broken;

        private static bool Prefix(Projectile __instance)
        {
            if (_broken)
            {
                return true;
            }
            try
            {
                return RealTimeProjectiles.Count == 0 || RealTimeProjectiles.Step(__instance);
            }
            catch (System.Exception e)
            {
                _broken = true;
                Core.Log.ExceptionOnce("RealTimeProjectiles.Step", e); // (bullets go at the world's pace from here, as without the mod)
                return true;
            }
        }
    }

    /// <summary>
    /// A magazine the player lets go of in slow motion flies as it would in real time until it first touches something. The
    /// hand's velocity is measured in real time (the player's arms are not slowed), and the slowed world took it as game speed:
    /// at the game's slow motion (time scale 0.2) a magazine let go at 1 m/s went on at a fifth of that, stopped by the hand and
    /// sank there under a twenty-fifth of gravity ("the mag floats for one second in the air and then falls"; user, 2026-10-28).
    /// Here its velocity and spin are divided by the time scale and gravity is made as strong in game time as it is in real time
    /// (g / s^2), so the flight looks as without slow motion. At its first touch it is given back to the world's pace (its velocity
    /// times the time scale), so it settles with the slowed world and never rests under 25 times gravity. Slow motion switched on
    /// or off in flight is followed; after 3 s of real time it ends anyway. A thrown soldier's bones fly as one (BeginGroup): the
    /// first of them to touch something gives them all back, so his joints never pull between bones at two paces.
    /// </summary>
    internal sealed class RealTimeFlight : MonoBehaviour
    {
        private Rigidbody _body;
        /// <summary>The time scale the body's velocity is made out for (1: none).</summary>
        private float _scale = 1f;
        private float _maxSpin;
        /// <summary>Real seconds it has flown (a pause adds none: by the clock, a pause of over 3 s ended the flight mid-air on resume).</summary>
        private float _flown;
        /// <summary>The bodies flying as one with this one (a soldier's bones), or null.</summary>
        private RealTimeFlight[] _group;
        /// <summary>The scale it ended from, and the physics step it ended in: a touch's other handlers read it (ScaleAtTouch).</summary>
        private float _endedScale = 1f, _endedStep = -1f;

        /// <summary>
        /// The time scale the body's velocity was made out for at a touch now (1 when it is not flying in real time): a contact's
        /// speed times this is its speed at the world's pace (a thrown soldier's impact damage). Read in a collision callback, in
        /// whichever order the components get it.
        /// </summary>
        internal static float ScaleAtTouch(Rigidbody body)
        {
            RealTimeFlight f = body != null ? body.GetComponent<RealTimeFlight>() : null;
            if (f == null)
            {
                return 1f;
            }
            if (f.enabled)
            {
                return f._scale;
            }
            return Mathf.Approximately(f._endedStep, Time.fixedTime) ? f._endedScale : 1f;
        }

        /// <summary>Gives the body back to the world's pace now, if it flies (taken into a hand, it may be let go before a step ran).</summary>
        internal static void Stop(Rigidbody body)
        {
            RealTimeFlight f = body != null ? body.GetComponent<RealTimeFlight>() : null;
            if (f != null && f.enabled)
            {
                f.End();
            }
        }

        /// <summary>
        /// <see cref="Begin"/> for bodies flying as one (a thrown soldier's bones, each given the hand's velocity first). Returns
        /// what their velocity was multiplied by (1 when not in slow motion).
        /// </summary>
        internal static float BeginGroup(Rigidbody[] bodies)
        {
            float s = Time.timeScale;
            if (bodies == null || s <= 0f || s >= 0.999f || Core.VRConfig.SlowedHands)
            {
                return 1f;
            }
            var group = new System.Collections.Generic.List<RealTimeFlight>(bodies.Length);
            foreach (Rigidbody b in bodies)
            {
                if (b != null && !b.isKinematic && Begin(b) != 1f)
                {
                    group.Add(b.GetComponent<RealTimeFlight>());
                }
            }
            RealTimeFlight[] members = group.ToArray();
            foreach (RealTimeFlight f in members)
            {
                f._group = members;
            }
            return 1f / s;
        }

        /// <summary>
        /// Called right after the body was let go with its velocity: in slow motion it flies in real time from here. Returns what its
        /// velocity was multiplied by (1 when not in slow motion).
        /// </summary>
        internal static float Begin(Rigidbody body)
        {
            float s = Time.timeScale;
            // (Slowed hands: the hand lives in the slowed world, its velocity is in game units already, and what it lets go of
            // moves at the world's pace.)
            if (body == null || body.isKinematic || s <= 0f || s >= 0.999f || Core.VRConfig.SlowedHands)
            {
                return 1f; // (paused: let go with no time passing, it waits as the world does)
            }
            RealTimeFlight f = body.GetComponent<RealTimeFlight>() ?? body.gameObject.AddComponent<RealTimeFlight>();
            f._body = body;
            f._flown = 0f;
            if (f._scale >= 0.999f)
            {
                f._maxSpin = body.maxAngularVelocity;
            }
            f.enabled = true;
            f._group = null; // (alone unless BeginGroup says otherwise)
            f._scale = 1f; // the velocity just given is the hand's own, real-time one (also when let go again mid-flight)
            f.Rescale(s);
            return 1f / s;
        }

        /// <summary>The velocity made out for time scale <paramref name="to"/> instead of the one it was made out for.</summary>
        private void Rescale(float to)
        {
            float k = _scale / to;
            if (!_body.isKinematic)
            {
                _body.velocity *= k;
                _body.angularVelocity *= k;
            }
            _body.maxAngularVelocity = to >= 0.999f ? _maxSpin : _maxSpin / to; // (a 9 rad/s tumble is 45 in game time)
            _scale = to;
        }

        private void FixedUpdate()
        {
            if (_body == null || _body.isKinematic)
            {
                End(); // (taken into a hand again)
                return;
            }
            float s = Time.timeScale;
            if (s >= 0.999f || _flown > 3f)
            {
                End();
                return;
            }
            if (s < 0.01f)
            {
                return; // (the game's time stop, a katana finisher: no rescale by a zero scale, it waits as the world does)
            }
            _flown += Time.fixedDeltaTime / s; // (a physics step is fixedDeltaTime of game time, that over the scale in real time)
            if (!Mathf.Approximately(s, _scale))
            {
                Rescale(s);
            }
            if (_body.useGravity)
            {
                _body.AddForce(Physics.gravity * (1f / (s * s) - 1f), ForceMode.Acceleration); // (PhysX adds the one g itself)
            }
        }

        /// <summary>
        /// A touch gives it back to the world's pace, but not a touch of what moves in real time itself: the gun in the hand (its stand-in),
        /// the fists, the player's own body. Since loose magazines meet the held gun (Tasks 351) a magazine let go or knocked out beside
        /// the gun brushed it at once and hung in the air at a fifth of its speed, the float Tasks 352 had fixed (the autotest's slow-motion
        /// check, first run since, 2026-11-08: "0.10 m/s across ... HUNG IN THE AIR").
        /// </summary>
        private void OnCollisionEnter(Collision c)
        {
            Collider other = c.collider;
            if (other != null && (GunCollider.IsStandIn(other) || HandBody.Is(other) || PropPhysics.IsPlayerCollider(other)))
            {
                return;
            }
            // (Which touch ends it, once per thing: the autotest's magazine still hung after the gun's touch was let through, 2026-11-08.)
            if (enabled && other != null)
            {
                Core.Log.InfoOnce("rtf-end:" + other.name, $"Real-time flight of {name} ended by touching {other.name} (layer {other.gameObject.layer}, under {other.transform.root.name}).");
            }
            End();
        }

        private void End()
        {
            if (!enabled)
            {
                return; // (a touch on a body that is not flying: collision messages reach disabled components too)
            }
            _endedScale = _scale;
            _endedStep = Time.fixedTime;
            if (_body != null && _scale < 0.999f)
            {
                Rescale(1f);
            }
            enabled = false;
            RealTimeFlight[] group = _group;
            _group = null;
            if (group != null)
            {
                foreach (RealTimeFlight f in group)
                {
                    if (f != null && f != this)
                    {
                        f.End();
                    }
                }
            }
        }
    }
}
