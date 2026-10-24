using System.Collections.Generic;
using HarmonyLib;
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
    /// Only a hand that holds nothing takes hold: a hand on a gun, a tool, a grenade or a magazine never does (its role is not
    /// Free), the other, empty hand does (user, 2026-10-24).
    /// Also (user, 2026-10-24): a soldier held in front is a human shield (bullets hit him, his own team's too:
    /// <see cref="HumanShieldPatch"/>); taken by the head from behind, a living soldier is held still and a twist of the wrist
    /// breaks his neck (<see cref="NeckTwist"/>); a live grenade let go on a held body is strapped to it (<see cref="StrappedGrenade"/>).
    /// </summary>
    internal sealed class BodyGrab
    {
        /// <summary>How far from the fist's hollow a soldier's collider may be to be taken.</summary>
        private const float Reach = 0.13f;
        /// <summary>How long after the throw the body's impacts still hurt.</summary>
        private const float ImpactsAfterThrow = 3f;
        /// <summary>Stretched this far off the hand (wedged against a wall, the arm pulled on): let go.</summary>
        private const float MaxStretch = 0.6f;
        /// <summary>
        /// A held body's bones never move faster than the hand by more than this (m/s), and a throw leaves at most
        /// <see cref="MaxThrowSpeed"/>: a ragdoll pushed into the ground or a wall was shot out of it by the physics (a throw
        /// left at 49.6 m/s from a 6 m/s swing and one impact read 62 m/s in the autotest, 2026-10-24).
        /// </summary>
        private const float MaxOverHand = 15f, MaxThrowSpeed = 22f;
        /// <summary>
        /// The hand moved this far in one frame (a snap turn, a teleport, a long frame): the body is carried with it, turned as
        /// the hand turned, instead of dragged after it (dragged at the speed limit above, the stretch let go of it).
        /// </summary>
        private const float JumpDistance = 0.25f;
        /// <summary>The head within this of the fist's hollow, taken from behind: a neck hold, not a knock-down.</summary>
        private const float NeckReach = 0.22f;
        /// <summary>How far behind: the soldier's facing within this of the way from the player to him (degrees).</summary>
        private const float BehindDegrees = 70f;
        /// <summary>The wrist turned this far from where it took hold (degrees): the neck breaks.</summary>
        internal const float NeckTwist = 70f;
        /// <summary>How near to a held body's bone a live grenade must be let go to be strapped to it (m).</summary>
        internal const float StrapReach = 0.3f;

        private readonly VRRig _rig;
        private readonly Hold[] _holds = new Hold[2];
        private readonly Rigidbody[] _anchors = new Rigidbody[2];
        private static readonly Collider[] Near = new Collider[48];

        private sealed class Hold
        {
            public Actor Actor;
            public Rigidbody Bone;
            public ConfigurableJoint Joint;
            /// <summary>How far the bone's pivot was from the palm when taken (its stretch is measured from there).</summary>
            public float Reach;
            /// <summary>A living soldier held by the head from behind, standing (no joint, his AI paused).</summary>
            public bool Neck;
            public Quaternion NeckStart;
            /// <summary>Where the hand was last frame (to carry the body over a jump of the hand).</summary>
            public Vector3 LastPalm;
            public Quaternion LastRotation;
        }

        public BodyGrab(VRRig rig)
        {
            _rig = rig;
            _instance = this;
        }

        private static BodyGrab _instance;

        /// <summary>
        /// A held soldier slammed into something: each hand holding them feels it, harder the more it hurt (Boneworks' hands
        /// feel every impact of what they hold).
        /// </summary>
        internal static void FeelImpact(Actor a, float damage)
        {
            BodyGrab g = _instance;
            if (g == null || a == null)
            {
                return;
            }
            for (int i = 0; i < 2; i++)
            {
                if (g._holds[i] != null && g._holds[i].Actor == a && g._rig.Hands[i] != null)
                {
                    g._rig.Hands[i].Haptic(0.06f, 70f, Mathf.Clamp(0.3f + damage / BodyImpacts.Cap, 0.3f, 1f));
                }
            }
        }

        /// <summary>Whether a hand is holding a soldier now.</summary>
        internal bool Holding(VRHand hand) => _holds[(int)hand.Side] != null;

        /// <summary>Whether the player holds this soldier (in either hand, alive or dead): bullets hit him, whoever fired.</summary>
        internal static bool IsHeld(Actor a)
        {
            BodyGrab g = _instance;
            return g != null && a != null && (g._holds[0] != null && g._holds[0].Actor == a || g._holds[1] != null && g._holds[1].Actor == a);
        }

        /// <summary>Whether this hand holds a soldier by the neck (the autotest's execution check).</summary>
        internal static bool HoldsNeck(VRHand hand)
        {
            BodyGrab g = _instance;
            Hold h = g != null && hand != null ? g._holds[(int)hand.Side] : null;
            return h != null && h.Neck;
        }

        /// <summary>
        /// The bone of a body held as a ragdoll (either hand) nearest <paramref name="at"/> within <paramref name="within"/>, or
        /// null: where a live grenade let go there is strapped (<see cref="StrappedGrenade"/>).
        /// </summary>
        internal static Rigidbody StrapBone(Vector3 at, float within) => StrapBone(at, within, out _);

        /// <summary>The same, with the soldier whose bone it is.</summary>
        internal static Rigidbody StrapBone(Vector3 at, float within, out Actor carrier)
        {
            carrier = null;
            BodyGrab g = _instance;
            if (g == null)
            {
                return null;
            }
            Rigidbody best = null;
            float bestDistance = within * within;
            foreach (Hold h in g._holds)
            {
                if (h == null || h.Neck || h.Actor == null || h.Actor.ragdoll == null || h.Actor.ragdoll.rigidbodies == null)
                {
                    continue;
                }
                foreach (Rigidbody rb in h.Actor.ragdoll.rigidbodies)
                {
                    if (rb == null)
                    {
                        continue;
                    }
                    Collider c = rb.GetComponent<Collider>();
                    Vector3 on = c != null && c.enabled ? c.ClosestPoint(at) : rb.worldCenterOfMass;
                    float d = (on - at).sqrMagnitude;
                    if (d <= bestDistance)
                    {
                        bestDistance = d;
                        best = rb;
                        carrier = h.Actor;
                    }
                }
            }
            return best;
        }

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
            // A fist swung at punching speed punches (MeleeHandler's blows), even with its grip just closed: a grab is a reach.
            float punch = Mathf.Max(2.2f, VRConfig.MeleeSwingSpeed.Value);
            if (hand.BodyRelativeVelocity.sqrMagnitude > punch * punch)
            {
                return;
            }
            Actor player = GameContext.PlayerActor;
            Vector3 palm = hand.PalmCentre;
            int n = Physics.OverlapSphereNonAlloc(palm, Reach, Near, Hitbox.ALL_HITBOX_LAYER_MASK, QueryTriggerInteraction.Collide);
            Actor best = null;
            float bestDistance = float.MaxValue;
            for (int k = 0; k < n; k++)
            {
                Collider c = Near[k];
                if (c == null)
                {
                    continue;
                }
                // A standing soldier by its hitboxes; one already down (thrown, knocked over, dead) has only its ragdoll's
                // colliders enabled, on a hitbox layer too, which may carry no Hitbox: the soldier above them then.
                Hitbox hb = c.GetComponent<Hitbox>();
                Actor a = hb != null ? hb.parent as Actor : c.GetComponentInParent<Actor>();
                if (a == null || a == player || a.ragdoll == null)
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
                if (!best.dead && !best.ragdoll.IsRagdoll() && !best.IsSeated() && player != null && best.team != player.team
                    && _holds[1 - i] == null && FromBehind(best, player) && NearHead(best, palm))
                {
                    TakeNeck(i, hand, best);
                    return;
                }
                if (!best.ragdoll.IsRagdoll())
                {
                    best.FallOver(); // the game's own: off a seat or ladder, weapon put away, ragdoll from the animated pose
                }
                Attach(i, hand, best, palm);
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("BodyGrab.Take", e);
                Release(i, hand, thrown: false);
            }
        }

        /// <summary>The hand holds the ragdoll's nearest bone through a locked joint to a body that follows the hand.</summary>
        private void Attach(int i, VRHand hand, Actor best, Vector3 palm)
        {
            // Not the bone the other hand holds: two locked joints on one body pulled it between them every step.
            Rigidbody bone = NearestBone(best, palm, _holds[1 - i]?.Bone);
            if (bone == null)
            {
                return;
            }
            Rigidbody anchor = Anchor(i);
            anchor.transform.SetPositionAndRotation(palm, hand.Palm.rotation);
            // The game syncs transforms to the physics only on a step (autoSyncTransforms off): the joint below takes the
            // anchor's physics pose, which was still where it was last let go (the world's origin on a first grab).
            Physics.SyncTransforms();
            var joint = bone.gameObject.AddComponent<ConfigurableJoint>();
            joint.connectedBody = anchor;
            joint.autoConfigureConnectedAnchor = true; // held where it was taken: no snap of the bone into the fist
            joint.xMotion = joint.yMotion = joint.zMotion = ConfigurableJointMotion.Locked;
            joint.angularXMotion = joint.angularYMotion = joint.angularZMotion = ConfigurableJointMotion.Locked;
            joint.projectionMode = JointProjectionMode.PositionAndRotation; // a heavy body hanging from it does not stretch the joint
            joint.projectionDistance = 0.02f;
            joint.projectionAngle = 5f;
            joint.enablePreprocessing = false;
            _holds[i] = new Hold { Actor = best, Bone = bone, Joint = joint, Reach = (bone.position - palm).magnitude, LastPalm = palm, LastRotation = hand.Palm.rotation };
            BodyImpacts.Arm(best, float.PositiveInfinity, GameContext.PlayerActor); // (its bones' speed kept in check while armed)
            if (best.ragdoll.ragdollObject != null)
            {
                PropPhysics.IgnorePlayer(best.ragdoll.ragdollObject.GetComponentsInChildren<Collider>());
            }
            hand.Role = HandRole.Body;
            hand.Held = best;
            VRInput.Consume(DigitalAction.Grab, hand.Side);
            hand.Haptic(0.08f, 80f, 0.8f);
            Log.InfoOnce("bodygrab", $"Took hold of {best.name} by {bone.name} ({(best.dead ? "dead" : "alive")}).");
        }

        /// <summary>The player stands behind the soldier: the soldier faces away from him.</summary>
        private static bool FromBehind(Actor a, Actor player)
        {
            Vector3 away = Vector3.ProjectOnPlane(a.transform.position - player.transform.position, Vector3.up);
            Vector3 facing = Vector3.ProjectOnPlane(a.transform.forward, Vector3.up);
            return away.sqrMagnitude > 1e-4f && facing.sqrMagnitude > 1e-4f && Vector3.Angle(away, facing) < BehindDegrees;
        }

        private static Vector3 HeadOf(Actor a)
        {
            try
            {
                Transform head = a.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.Head) ?? a.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.Neck);
                if (head != null)
                {
                    return head.position;
                }
            }
            catch (System.Exception)
            {
                // a rig without those bones mapped: the centre's height below
            }
            return a.CenterPosition() + Vector3.up * 0.45f;
        }

        private static bool NearHead(Actor a, Vector3 palm) => (HeadOf(a) - palm).sqrMagnitude < NeckReach * NeckReach;

        /// <summary>
        /// Taken by the head from behind (an enemy: as every blow and throw of the mod, never the player's own team): the soldier
        /// stands held, his AI paused (he neither turns nor shoots); a twist of the wrist breaks his neck (<see cref="FollowNeck"/>),
        /// letting go lets him go.
        /// </summary>
        private void TakeNeck(int i, VRHand hand, Actor a)
        {
            if (a.controller != null)
            {
                a.controller.enabled = false;
            }
            _holds[i] = new Hold { Actor = a, Neck = true, NeckStart = hand.Palm.rotation };
            hand.Role = HandRole.Body;
            hand.Held = a;
            VRInput.Consume(DigitalAction.Grab, hand.Side);
            hand.Haptic(0.06f, 60f, 0.6f);
            Log.InfoOnce("neckhold", $"Took {a.name} by the neck from behind: a twist of the wrist breaks it.");
        }

        private void FollowNeck(int i, VRHand hand, Hold hold)
        {
            Actor a = hold.Actor;
            Actor player = GameContext.PlayerActor;
            if (a == null || player == null || player.dead || !GameContext.PlayerOnFoot || !hand.GrabHeld
                || (HeadOf(a) - hand.PalmCentre).sqrMagnitude > 0.45f * 0.45f)
            {
                Release(i, hand, thrown: false); // let go (or pulled away): his AI back
                return;
            }
            if (a.dead)
            {
                // Killed by something else meanwhile: a body to hold.
                _holds[i] = null;
                ResumeAi(a);
                Attach(i, hand, a, hand.PalmCentre);
                return;
            }
            float twist = Quaternion.Angle(hold.NeckStart, hand.Palm.rotation);
            if (twist < NeckTwist)
            {
                return;
            }
            // The neck breaks: a silent kill by the player, and the body stays in the hand.
            a.Kill(new DamageInfo(DamageInfo.DamageSourceType.Melee, player, null)
            {
                healthDamage = a.health + 100f,
                balanceDamage = 0f,
                point = HeadOf(a),
                direction = Vector3.ProjectOnPlane(a.transform.forward, Vector3.up).normalized
            });
            ResumeAi(a);
            hand.Haptic(0.12f, 30f, 1f);
            Log.InfoOnce("necksnap", $"Broke the neck of {a.name} (wrist turned {twist:0} degrees).");
            _holds[i] = null;
            if (a.ragdoll != null && a.ragdoll.IsRagdoll())
            {
                Attach(i, hand, a, hand.PalmCentre);
            }
            else
            {
                hand.Role = HandRole.Free;
                hand.Held = null;
            }
        }

        private static void ResumeAi(Actor a)
        {
            if (a != null && a.controller != null && !a.controller.enabled)
            {
                a.controller.enabled = true;
            }
        }

        private void Follow(int i, VRHand hand, Hold hold)
        {
            if (hold.Neck)
            {
                FollowNeck(i, hand, hold);
                return;
            }
            Actor a = hold.Actor;
            if (a == null || hold.Bone == null || hold.Joint == null || a.ragdoll == null || !a.ragdoll.IsRagdoll()
                || GameContext.PlayerActor == null || GameContext.PlayerActor.dead || !GameContext.PlayerOnFoot)
            {
                if (a != null && !a.dead && a.ragdoll != null && !a.ragdoll.IsRagdoll())
                {
                    // Held down by fallAction each frame, so something else stood them up (the user's mutators include No Knockdown).
                    Log.InfoOnce("bodygrab-up", $"Let go of {a.name}: stood up while held (a mutator that stops knockdowns does that).");
                }
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
            Quaternion rotation = hand.Palm.rotation;
            if ((palm - hold.LastPalm).sqrMagnitude > JumpDistance * JumpDistance)
            {
                CarryBody(a, hold.LastPalm, hold.LastRotation, palm, rotation);
                anchor.transform.SetPositionAndRotation(palm, rotation);
                anchor.position = palm;
                anchor.rotation = rotation;
                Physics.SyncTransforms();
            }
            hold.LastPalm = palm;
            hold.LastRotation = rotation;
            // Moved by the physics (a kinematic body pulls what is jointed to it); it keeps up with the hand each step.
            anchor.MovePosition(palm);
            anchor.MoveRotation(rotation);
            if ((hold.Bone.position - palm).magnitude > hold.Reach + MaxStretch)
            {
                Release(i, hand, thrown: false); // wedged or pulled through something: let go rather than tear the ragdoll
                return;
            }
            if (!a.dead)
            {
                a.fallAction.Start(); // held, not getting up (the game gets up when the fall timer is done and the body is still)
            }
            // Killed while held (a knife, a bullet), the game stiffened the joints again for the body (Actor.Die: SetDrive 50):
            // limp again while it hangs from the hand.
            if (a.ragdoll.jointDrive.positionSpring > 0f)
            {
                BodyImpacts.Limp(a);
            }
            // No bone faster than the hand by more than MaxOverHand: a body caught in the ground or a wall was shot out by the
            // physics before (see MaxOverHand).
            float limit = hand.Velocity.magnitude + MaxOverHand;
            Rigidbody[] bones = a.ragdoll.rigidbodies;
            if (bones != null)
            {
                foreach (Rigidbody rb in bones)
                {
                    if (rb != null && rb.velocity.sqrMagnitude > limit * limit)
                    {
                        rb.velocity = rb.velocity.normalized * limit;
                    }
                }
            }
        }

        private void Release(int i, VRHand hand, bool thrown)
        {
            Hold hold = _holds[i];
            _holds[i] = null;
            if (hold != null && hold.Neck)
            {
                ResumeAi(hold.Actor); // let go of the neck: he is free again
                hold = null;
            }
            if (hold != null)
            {
                if (hold.Joint != null)
                {
                    Object.Destroy(hold.Joint);
                }
                // Still held by the other hand: stays armed (not for 3 s only) and down; the other hand's let-go throws.
                Hold other = _holds[1 - i];
                bool stillHeld = other != null && other.Actor == hold.Actor;
                if (hold.Actor != null && !stillHeld && (hold.Actor.ragdoll == null || !hold.Actor.ragdoll.IsRagdoll()))
                {
                    BodyImpacts.Disarm(hold.Actor); // stood up or respawned under the hand: his joints as the game set them, now
                }
                else if (hold.Actor != null && !stillHeld)
                {
                    BodyImpacts.Arm(hold.Actor, Time.time + ImpactsAfterThrow, GameContext.PlayerActor);
                    if (thrown && hold.Actor.ragdoll.rigidbodies != null)
                    {
                        // Thrown with the hand's motion (as everything let go: ThrowAndMelee), the whole body, not only the bone that
                        // was held: the rest of a ragdoll hanging from one joint lags far behind it.
                        // One velocity for every bone (the joints' own pulls left in made the body tumble off at any speed), at most
                        // MaxThrowSpeed.
                        Vector3 v = Vector3.ClampMagnitude(ThrowableHandler.Released(hand), MaxThrowSpeed);
                        foreach (Rigidbody rb in hold.Actor.ragdoll.rigidbodies)
                        {
                            if (rb != null)
                            {
                                rb.velocity = v;
                                rb.angularVelocity *= 0.25f;
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

        /// <summary>The whole body moved and turned as the hand moved and turned between two frames (its pose kept).</summary>
        private static void CarryBody(Actor a, Vector3 fromPalm, Quaternion fromRotation, Vector3 toPalm, Quaternion toRotation)
        {
            Rigidbody[] bones = a.ragdoll != null ? a.ragdoll.rigidbodies : null;
            if (bones == null)
            {
                return;
            }
            Quaternion turn = toRotation * Quaternion.Inverse(fromRotation);
            foreach (Rigidbody rb in bones)
            {
                if (rb == null)
                {
                    continue;
                }
                Transform b = rb.transform;
                b.SetPositionAndRotation(toPalm + turn * (b.position - fromPalm), turn * b.rotation);
                rb.velocity = turn * rb.velocity;
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
            if (_instance == this)
            {
                _instance = null;
            }
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

        private static Rigidbody NearestBone(Actor a, Vector3 palm, Rigidbody taken)
        {
            Rigidbody best = null;
            float bestDistance = float.MaxValue;
            if (a.ragdoll.rigidbodies == null)
            {
                return null;
            }
            foreach (Rigidbody rb in a.ragdoll.rigidbodies)
            {
                if (rb == null || rb == taken)
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
        /// <summary>The most one impact takes: a soldier has 100; three hard slams kill, one does not.</summary>
        internal const float Cap = 35f;

        private static readonly Dictionary<Actor, float> ArmedUntil = new Dictionary<Actor, float>();

        /// <summary>
        /// The joint motors of soldiers made limp (see <see cref="Arm"/>), to put back once they are no longer armed. The game's
        /// soldiers are active ragdolls: knocked over, every joint is still driven toward the animated pose (ActiveRaggy.Update,
        /// a slerp drive of 700-1000), so a held soldier stayed stiff in his falling pose, "crammed together" with "no physics"
        /// (user, 2026-10-24). Held and for the seconds after a throw the motors are off and he hangs and flies as a body does.
        /// </summary>
        private static readonly Dictionary<Actor, JointDrive> SavedDrive = new Dictionary<Actor, JointDrive>();
        private static readonly Dictionary<Actor, float> LastHit = new Dictionary<Actor, float>();
        /// <summary>The bones with a live component (one each: a bone re-armed in the frame its old one goes gets one new one).</summary>
        private static readonly HashSet<Rigidbody> ArmedBones = new HashSet<Rigidbody>();
        private static Actor _by;

        private Actor _owner;
        private Transform _ragdollRoot;
        private Rigidbody _body;
        private float _depenetration;
        private bool _limited;
        /// <summary>Destroyed at the end of this frame: no more impacts through it.</summary>
        private bool _gone;

        /// <summary>
        /// While armed (held, and for the seconds after a throw), the bone is pushed out of what it sinks into gently and never
        /// moves faster than this: a thrown body landing in the ground was shot out of it by the physics, one bone at 180 m/s
        /// in the autotest (2026-10-24).
        /// </summary>
        private const float ArmedDepenetration = 5f, ArmedMaxSpeed = 25f;

        /// <summary>
        /// Only while armed: then the bone puts back what it changed and the component goes (it stayed on every bone of every
        /// soldier ever held, each running this every physics step for the rest of the match; perf pass, 2026-10-24).
        /// </summary>
        private void FixedUpdate()
        {
            if (_body == null)
            {
                Go();
                return;
            }
            if (_owner != null && ArmedUntil.TryGetValue(_owner, out float until))
            {
                if (Time.time <= until)
                {
                    if (!_limited)
                    {
                        _limited = true;
                        _depenetration = _body.maxDepenetrationVelocity;
                        _body.maxDepenetrationVelocity = ArmedDepenetration;
                    }
                    if (_body.velocity.sqrMagnitude > ArmedMaxSpeed * ArmedMaxSpeed)
                    {
                        _body.velocity = _body.velocity.normalized * ArmedMaxSpeed;
                    }
                    return;
                }
                Disarm(_owner); // the first of its bones to see the time is up: the soldier's joints back, once
            }
            if (_limited)
            {
                _body.maxDepenetrationVelocity = _depenetration;
            }
            Go();
        }

        private void Go()
        {
            if (!_gone)
            {
                _gone = true;
                ArmedBones.Remove(_body);
                Destroy(this);
            }
        }

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
            Limp(a);
            Transform root = a.ragdoll.ragdollObject != null ? a.ragdoll.ragdollObject.transform : null;
            foreach (Rigidbody rb in a.ragdoll.rigidbodies)
            {
                if (rb != null && ArmedBones.Add(rb))
                {
                    BodyImpacts b = rb.gameObject.AddComponent<BodyImpacts>();
                    b._owner = a;
                    b._body = rb;
                    b._ragdollRoot = root;
                }
            }
        }

        /// <summary>The soldier's joint motors off (kept as the game set them, to put back on <see cref="Disarm"/>).</summary>
        internal static void Limp(Actor a)
        {
            if (!SavedDrive.ContainsKey(a))
            {
                SavedDrive[a] = a.ragdoll.jointDrive;
            }
            a.ragdoll.SetDrive(0f, 1f);
        }

        /// <summary>
        /// No longer armed: the joints as the game would have them now (a living soldier's to get up with; a dead one's as
        /// Actor.Die sets them, not the living stiffness he had when taken, which held the body rigid in its last pose).
        /// The bones' components see it and go.
        /// </summary>
        internal static void Disarm(Actor a)
        {
            ArmedUntil.Remove(a);
            if (SavedDrive.TryGetValue(a, out JointDrive drive))
            {
                SavedDrive.Remove(a);
                Restore(a, drive);
            }
            if (ArmedUntil.Count == 0)
            {
                LastHit.Clear(); // (the soldiers hit by thrown bodies too: only the last 0.15 s matter)
            }
        }

        private static void Restore(Actor a, JointDrive drive)
        {
            if (a == null || a.ragdoll == null)
            {
                return;
            }
            if (a.dead)
            {
                a.ragdoll.SetDrive(DeadSpring, DeadDamper);
            }
            else
            {
                a.ragdoll.SetDrive(drive.positionSpring, drive.positionDamper);
            }
        }

        /// <summary>The joints of a dead soldier, as the game sets them (Actor.Die).</summary>
        private const float DeadSpring = 50f, DeadDamper = 1f;

        /// <summary>A new scene or VR stopping: nothing stays armed.</summary>
        internal static void Clear()
        {
            foreach (KeyValuePair<Actor, JointDrive> kv in SavedDrive)
            {
                Restore(kv.Key, kv.Value);
            }
            SavedDrive.Clear();
            ArmedUntil.Clear();
            LastHit.Clear();
            ArmedBones.RemoveWhere(b => b == null); // a scene's bones went with it; live ones go on their next step
            _by = null;
        }

        private void OnCollisionEnter(Collision c)
        {
            if (_gone || _owner == null || !ArmedUntil.TryGetValue(_owner, out float until) || Time.time > until)
            {
                return;
            }
            Collider other = c.collider;
            if (other == null || _ragdollRoot != null && other.transform.IsChildOf(_ragdollRoot) || OwnBone(other) || PropPhysics.IsPlayerCollider(other))
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
            BodyGrab.FeelImpact(_owner, damage);
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

        /// <summary>One of the owner's own ragdoll bones (also without a ragdoll root to test against).</summary>
        private bool OwnBone(Collider other)
        {
            Rigidbody body = other.attachedRigidbody;
            Rigidbody[] own = _owner.ragdoll != null ? _owner.ragdoll.rigidbodies : null;
            return body != null && own != null && System.Array.IndexOf(own, body) >= 0;
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

    /// <summary>
    /// A soldier held in front is a human shield (user, 2026-10-24: "human shield when people are shooting people can hold it
    /// infront"). The game lets a bot's bullets pass through its own team's soldiers (Projectile.CanHitHitbox), so a held enemy
    /// stopped none of his friends' bullets: a held soldier's hitboxes are hit by every bullet, and the hit hurts him (the game's
    /// Actor.Damage has no team check). His ragdoll stands between the hand and whoever shoots.
    /// </summary>
    [HarmonyPatch(typeof(Projectile), nameof(Projectile.CanHitHitbox))]
    internal static class HumanShieldPatch
    {
        private static void Postfix(Projectile __instance, Hitbox hitbox, ref bool __result)
        {
            if (__result || hitbox == null || !VRManager.Active)
            {
                return;
            }
            if (hitbox.parent is Actor a && a != __instance.killCredit && BodyGrab.IsHeld(a))
            {
                __result = true;
                if (!Log.Logged("humanshield")) // (its line not built again for every bullet after the first)
                {
                    Log.InfoOnce("humanshield", $"A shot hit {a.name}, held as a shield.");
                }
            }
        }
    }

    /// <summary>
    /// A live grenade let go on a held body sticks to the bone it was let go on (user, 2026-10-24: "Strapping grenade to a body
    /// and throwing it"): it follows the bone (and so the throw) until its own fuse sets it off. The game's grenade moves
    /// itself each frame (GrenadeProjectile.Update): after that, it is put back on the bone, its velocity the bone's (so it flies
    /// on with the body's motion if the body is gone).
    /// </summary>
    internal sealed class StrappedGrenade : MonoBehaviour
    {
        private Rigidbody _bone;
        private Projectile _projectile;
        private Actor _carrier;
        /// <summary>The carrier was seen dead: alive again later means respawned (actors are reused), the strap is off.</summary>
        private bool _carrierDied;
        private Vector3 _localPosition;
        private Quaternion _localRotation;

        /// <summary>Grenades strapped this session (the autotest's check).</summary>
        internal static int Count { get; private set; }

        internal static StrappedGrenade Strap(Projectile projectile, Rigidbody bone, Actor carrier, Vector3 at)
        {
            StrappedGrenade s = projectile.gameObject.AddComponent<StrappedGrenade>();
            s._projectile = projectile;
            s._carrier = carrier;
            s._bone = bone;
            Transform b = bone.transform;
            projectile.transform.position = at;
            s._localPosition = b.InverseTransformPoint(at);
            s._localRotation = Quaternion.Inverse(b.rotation) * projectile.transform.rotation;
            projectile.velocity = bone.velocity;
            Count++;
            Log.InfoOnce("grenadestrap", $"Strapped {projectile.name} to {bone.name}: it goes off with the body.");
            return s;
        }

        /// <summary>
        /// Gone off: the soldier it was strapped to dies at once, credited to the player (user, 2026-10-24: "they take damage
        /// and die directly (because the grenade is on them)"); everyone else takes the grenade's own explosion by distance,
        /// as the game (or the grenade's mod) sets it.
        /// </summary>
        internal void WentOff()
        {
            Actor a = _carrier;
            _carrier = null;
            Actor by = _projectile != null ? _projectile.killCredit : null;
            if (a == null || a.dead || by != null && a.team == by.team)
            {
                return; // (one of the thrower's own team: the grenade's own explosion only, as the game deals it)
            }
            try
            {
                a.Kill(new DamageInfo(DamageInfo.DamageSourceType.Explosion, by, _projectile != null ? _projectile.sourceWeapon : null)
                {
                    healthDamage = a.health + 100f,
                    balanceDamage = 500f,
                    isSplashDamage = true,
                    point = transform.position,
                    direction = Vector3.up
                });
                Log.InfoOnce("grenadestrap-kill", $"The grenade strapped to {a.name} went off on him.");
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("StrappedGrenade.WentOff", e);
            }
        }

        private void LateUpdate()
        {
            if (_bone == null || _projectile == null)
            {
                Destroy(this); // the body gone: the grenade flies on by itself
                return;
            }
            if (_carrier != null)
            {
                if (_carrier.dead)
                {
                    _carrierDied = true;
                }
                else if (_carrierDied)
                {
                    // Respawned elsewhere before it went off (the game reuses its soldiers): it stays where the body was.
                    _carrier = null;
                    Destroy(this);
                    return;
                }
            }
            Transform b = _bone.transform;
            transform.SetPositionAndRotation(b.TransformPoint(_localPosition), b.rotation * _localRotation);
            _projectile.velocity = _bone.velocity;
        }
    }

    /// <summary>A strapped grenade going off kills the soldier it is strapped to (<see cref="StrappedGrenade.WentOff"/>).</summary>
    [HarmonyPatch(typeof(GrenadeProjectile), nameof(GrenadeProjectile.Explode))]
    internal static class StrappedGrenadeExplodePatch
    {
        private static void Postfix(GrenadeProjectile __instance)
        {
            StrappedGrenade s = __instance != null ? __instance.GetComponent<StrappedGrenade>() : null;
            if (s != null)
            {
                s.WentOff();
            }
        }
    }
}
