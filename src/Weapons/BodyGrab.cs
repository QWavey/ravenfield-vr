using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// An empty hand takes hold of a soldier, living or dead, and can drag, swing and throw them (user, 2026-10-24: "I cannot grab
    /// people with my hand and throw them around with them taking damage"). As Boneworks does it (Tasks 334): its enemies are
    /// ragdolls (PuppetMaster) the hand takes hold of, and an impact past a threshold is blunt damage scaled by its speed
    /// (Enemy_Health's damageFromImpact / thr_Impact, ImpactSFX's bluntAttack). Here: the grip near a soldier knocks them over
    /// (the game's own ragdoll, Actor.FallOver), the hand holds the nearest ragdoll bone through a locked joint to a body that
    /// follows the hand, and while held and for a few seconds after a throw, every hard impact of that ragdoll (a wall, the
    /// ground, another soldier) hurts it (<see cref="BodyImpacts"/>). Held, the soldier does not get up.
    /// </summary>
    internal sealed class BodyGrab
    {
        /// <summary>How far from the fist's hollow a soldier's collider may be to be taken.</summary>
        private const float Reach = 0.13f;
        /// <summary>How long after the throw the body's impacts still hurt.</summary>
        private const float ImpactsAfterThrow = 3f;
        /// <summary>Stretched this far off the hand (wedged against a wall, the arm pulled on): let go.</summary>
        private const float MaxStretch = 0.6f;

        private readonly VRRig _rig;
        private readonly Hold[] _holds = new Hold[2];
        private readonly Rigidbody[] _anchors = new Rigidbody[2];
        private static readonly Collider[] Near = new Collider[48];

        private sealed class Hold
        {
            public Actor Actor;
            public Rigidbody Bone;
            public ConfigurableJoint Joint;
        }

        public BodyGrab(VRRig rig)
        {
            _rig = rig;
        }

        /// <summary>Whether a hand is holding a soldier now.</summary>
        internal bool Holding(VRHand hand) => _holds[(int)hand.Side] != null;

        /// <summary>Once a frame, after every other grab has had the press: free hands take hold of a soldier, held ones follow.</summary>
        public void Update()
        {
            for (int i = 0; i < 2; i++)
            {
                VRHand hand = _rig.Hands[i];
                if (hand == null)
                {
                    continue;
                }
                Hold hold = _holds[i];
                if (hold != null)
                {
                    Follow(i, hand, hold);
                }
                else if (hand.Role == HandRole.Free && hand.Held == null && hand.Tracked && hand.GrabReaching && !VRGameInput.GunInputBlocked
                         && GameContext.PlayerOnFoot)
                {
                    TryTake(i, hand);
                }
            }
        }

        private void TryTake(int i, VRHand hand)
        {
            Actor player = GameContext.PlayerActor;
            Vector3 palm = hand.PalmCentre;
            int n = Physics.OverlapSphereNonAlloc(palm, Reach, Near, Hitbox.ALL_HITBOX_LAYER_MASK, QueryTriggerInteraction.Collide);
            Actor best = null;
            float bestDistance = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                Collider c = Near[k];
                Hitbox hb = c != null ? c.GetComponent<Hitbox>() : null;
                if (hb == null || !(hb.parent is Actor a) || a == player || a.ragdoll == null)
                {
                    continue;
                }
                float d = (c.ClosestPoint(palm) - palm).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = a;
                }
            }
            if (best == null)
            {
                return;
            }
            try
            {
                if (!best.ragdoll.IsRagdoll())
                {
                    best.FallOver(); // the game's own: off a seat or ladder, weapon put away, ragdoll from the animated pose
                }
                Rigidbody bone = NearestBone(best, palm);
                if (bone == null)
                {
                    return;
                }
                Rigidbody anchor = Anchor(i);
                anchor.transform.SetPositionAndRotation(palm, hand.Palm.rotation);
                var joint = bone.gameObject.AddComponent<ConfigurableJoint>();
                joint.connectedBody = anchor;
                joint.autoConfigureConnectedAnchor = true; // held where it was taken: no snap of the bone into the fist
                joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
                joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Locked;
                joint.projectionMode = JointProjectionMode.PositionAndRotation; // a heavy body hanging from it does not stretch the joint
                joint.projectionDistance = 0.02f;
                joint.projectionAngle = 5f;
                joint.enablePreprocessing = false;
                _holds[i] = new Hold { Actor = best, Bone = bone, Joint = joint };
                BodyImpacts.Arm(best, float.PositiveInfinity, GameContext.PlayerActor);
                PropPhysics.IgnorePlayer(best.ragdoll.ragdollObject.GetComponentsInChildren<Collider>());
                hand.Role = HandRole.Body;
                hand.Held = best;
                VRInput.Consume(DigitalAction.Grab, hand.Side);
                hand.Haptic(0.08f, 80f, 0.8f);
                Log.InfoOnce("bodygrab", $"Took hold of {best.name} by {bone.name} ({(best.dead ? "dead" : "alive")}).");
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("BodyGrab.Take", e);
                Release(i, hand, thrown: false);
            }
        }

        private void Follow(int i, VRHand hand, Hold hold)
        {
            Actor a = hold.Actor;
            if (a == null || hold.Bone == null || hold.Joint == null || a.ragdoll == null || !a.ragdoll.IsRagdoll()
                || GameContext.PlayerActor == null || GameContext.PlayerActor.dead || !GameContext.PlayerOnFoot)
            {
                Release(i, hand, thrown: false);
                return;
            }
            if (!hand.GrabHeld)
            {
                Release(i, hand, thrown: true);
                return;
            }
            Rigidbody anchor = Anchor(i);
            Vector3 palm = hand.PalmCentre;
            // Moved by the physics (a kinematic body pulls what is jointed to it); it keeps up with the hand each step.
            anchor.MovePosition(palm);
            anchor.MoveRotation(hand.Palm.rotation);
            if ((hold.Bone.position - palm).sqrMagnitude > MaxStretch * MaxStretch * 4f)
            {
                Release(i, hand, thrown: false); // wedged or pulled through something: let go rather than tear the ragdoll
                return;
            }
            if (!a.dead)
            {
                a.fallAction.Start(); // held, not getting up (the game gets up when the fall timer is done and the body is still)
            }
        }

        private void Release(int i, VRHand hand, bool thrown)
        {
            Hold hold = _holds[i];
            _holds[i] = null;
            if (hold != null)
            {
                if (hold.Joint != null)
                {
                    Object.Destroy(hold.Joint);
                }
                if (hold.Actor != null)
                {
                    BodyImpacts.Arm(hold.Actor, Time.time + ImpactsAfterThrow, GameContext.PlayerActor);
                    if (thrown && hold.Actor.ragdoll != null && hold.Actor.ragdoll.rigidbodies != null)
                    {
                        // Thrown with the hand's motion (as everything let go: ThrowAndMelee), the whole body, not only the bone that
                        // was held: the rest of a ragdoll hanging from one joint lags far behind it.
                        Vector3 v = hand.ThrowVelocity() + hand.BodyThrowVelocity() * (VRConfig.ThrowVelocityMultiplier.Value - 1f);
                        foreach (Rigidbody rb in hold.Actor.ragdoll.rigidbodies)
                        {
                            if (rb != null)
                            {
                                rb.velocity = Vector3.Lerp(rb.velocity, v, 0.6f);
                            }
                        }
                    }
                    if (!hold.Actor.dead)
                    {
                        hold.Actor.fallAction.Start(); // down a moment after the throw too, before the game lets them get up
                    }
                }
            }
            if (hand != null && hand.Role == HandRole.Body)
            {
                hand.Role = HandRole.Free;
                hand.Held = null;
            }
        }

        /// <summary>Lets go of everything (scene change, VR stopping).</summary>
        public void ReleaseAll()
        {
            for (int i = 0; i < 2; i++)
            {
                Release(i, _rig.Hands[i], thrown: false);
            }
        }

        public void Destroy()
        {
            ReleaseAll();
            foreach (Rigidbody a in _anchors)
            {
                if (a != null)
                {
                    Object.Destroy(a.gameObject);
                }
            }
        }

        private Rigidbody Anchor(int i)
        {
            if (_anchors[i] == null)
            {
                var go = new GameObject("RavenfieldVR Body Grab " + (i == 0 ? "L" : "R"));
                Object.DontDestroyOnLoad(go);
                Rigidbody rb = go.AddComponent<Rigidbody>();
                rb.isKinematic = true; // no collider: it only carries the joint
                rb.interpolation = RigidbodyInterpolation.Interpolate;
                _anchors[i] = rb;
            }
            return _anchors[i];
        }

        private static Rigidbody NearestBone(Actor a, Vector3 palm)
        {
            Rigidbody best = null;
            float bestDistance = float.MaxValue;
            if (a.ragdoll.rigidbodies == null)
            {
                return null;
            }
            foreach (Rigidbody rb in a.ragdoll.rigidbodies)
            {
                if (rb == null)
                {
                    continue;
                }
                Collider c = rb.GetComponent<Collider>();
                Vector3 at = c != null && c.enabled ? c.ClosestPoint(palm) : rb.worldCenterOfMass;
                float d = (at - palm).sqrMagnitude;
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = rb;
                }
            }
            return best;
        }
    }

    /// <summary>
    /// On each ragdoll bone of a soldier the player has taken hold of: an impact faster than <see cref="Threshold"/> hurts the
    /// soldier, and a soldier it hits too (Boneworks' blunt impact damage: a threshold, then more the faster). Armed while held
    /// and for a few seconds after the throw; a soldier of the player's own team takes no damage.
    /// </summary>
    internal sealed class BodyImpacts : MonoBehaviour
    {
        /// <summary>Relative speed (m/s) below which an impact does nothing: a body dropped or dragged along the ground.</summary>
        internal const float Threshold = 5f;
        /// <summary>
        /// Health per (m/s)^2 past the threshold's square: Boneworks' Enemy_Health weighs an impact by its speed squared (its
        /// thr_Impact 10 and mod_Impact 0.1 are on relativeVelocity.sqrMagnitude), so a slam at twice the speed hurts four times
        /// as much and a shove barely at all: 6 m/s takes 3, 9 m/s 17, 10 m/s 23.
        /// </summary>
        internal const float PerSquare = 0.3f;
        /// <summary>The most one impact takes: a soldier has 100; two hard slams kill, one does not.</summary>
        internal const float Cap = 35f;

        private static readonly Dictionary<Actor, float> ArmedUntil = new Dictionary<Actor, float>();
        private static readonly Dictionary<Actor, float> LastHit = new Dictionary<Actor, float>();
        private static Actor _by;

        private Actor _owner;
        private Transform _ragdollRoot;

        /// <summary>The damage of an impact at this relative speed (pure: the offline tests check the curve).</summary>
        internal static float ImpactDamage(float speed) => speed <= Threshold ? 0f : Mathf.Min((speed * speed - Threshold * Threshold) * PerSquare, Cap);

        /// <summary>Arms (or keeps armed) every bone of the soldier until <paramref name="until"/> (game time).</summary>
        internal static void Arm(Actor a, float until, Actor by)
        {
            if (a == null || a.ragdoll == null || a.ragdoll.rigidbodies == null)
            {
                return;
            }
            _by = by;
            ArmedUntil[a] = until;
            foreach (Rigidbody rb in a.ragdoll.rigidbodies)
            {
                if (rb != null && rb.GetComponent<BodyImpacts>() == null)
                {
                    BodyImpacts b = rb.gameObject.AddComponent<BodyImpacts>();
                    b._owner = a;
                    b._ragdollRoot = a.ragdoll.ragdollObject != null ? a.ragdoll.ragdollObject.transform : null;
                }
            }
            if (ArmedUntil.Count > 64)
            {
                var gone = new List<Actor>();
                foreach (KeyValuePair<Actor, float> kv in ArmedUntil)
                {
                    if (kv.Key == null || kv.Value < Time.time)
                    {
                        gone.Add(kv.Key);
                    }
                }
                foreach (Actor g in gone)
                {
                    ArmedUntil.Remove(g);
                    LastHit.Remove(g);
                }
            }
        }

        /// <summary>A new scene or VR stopping: nothing stays armed.</summary>
        internal static void Clear()
        {
            ArmedUntil.Clear();
            LastHit.Clear();
            _by = null;
        }

        private void OnCollisionEnter(Collision c)
        {
            if (_owner == null || !ArmedUntil.TryGetValue(_owner, out float until) || Time.time > until)
            {
                return;
            }
            Collider other = c.collider;
            if (other == null || _ragdollRoot != null && other.transform.IsChildOf(_ragdollRoot) || PropPhysics.IsPlayerCollider(other))
            {
                return; // its own bones, the player's body and hands
            }
            float speed = c.relativeVelocity.magnitude;
            float damage = ImpactDamage(speed);
            if (damage <= 0f)
            {
                return;
            }
            Vector3 point = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            Vector3 dir = speed > 1e-3f ? -c.relativeVelocity / speed : Vector3.down;
            Hurt(_owner, damage, speed, point, dir);
            // A soldier it hits is hurt and knocked over as well (thrown into a squad).
            Hitbox hb = other.GetComponent<Hitbox>();
            if (hb != null && hb.parent is Actor hit && hit != _owner && hit != _by && !hit.dead)
            {
                Hurt(hit, damage, speed, point, -dir);
                if (hit.ragdoll != null && !hit.ragdoll.IsRagdoll())
                {
                    hit.KnockOver(-dir * Mathf.Min(speed, 15f) * 20f);
                }
            }
        }

        private static void Hurt(Actor a, float damage, float speed, Vector3 point, Vector3 dir)
        {
            if (a == null || a.dead || _by != null && a.team == _by.team)
            {
                return;
            }
            if (LastHit.TryGetValue(a, out float last) && Time.time - last < 0.15f)
            {
                return; // one impact, many bones touching at once: counted once
            }
            LastHit[a] = Time.time;
            try
            {
                var info = new DamageInfo(DamageInfo.DamageSourceType.Melee, _by, null)
                {
                    healthDamage = damage,
                    balanceDamage = damage * 4f,
                    point = point,
                    direction = dir,
                };
                a.Damage(info);
                Log.InfoOnce("bodyimpact:" + a.GetInstanceID(), $"{a.name}: thrown or slammed into something at {speed:0.0} m/s, {damage:0} damage.");
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("BodyImpacts.Hurt", e);
            }
        }
    }
}
