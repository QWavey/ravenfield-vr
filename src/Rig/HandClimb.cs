using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Util;
using UnityEngine;
using FirstPersonController = UnityStandardAssets.Characters.FirstPerson.FirstPersonController;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Climbing by hand, as Boneworks does it (the user, 2026-11-23: "maybe we are missing something. Features or things in
    /// boneworks"): a free hand squeezed against the level itself (a wall, a ledge, a roof's edge, a rock; never a soldier, a
    /// vehicle or anything with a physics body) holds on there. While it holds, the body moves against the hand: pull down and
    /// the player rises, push and he moves away; two hands on together share the pull. The game's own movement step (walking,
    /// gravity) is held off while a hand holds, so the player hangs; let go and he keeps the pull's speed, so a heave over a
    /// ledge carries him onto it. The hand is drawn closed where it took hold (CockpitPin). Setting <see cref="VRConfig.ClimbByHand"/>.
    /// Runs after the weapons each frame: a squeeze another system took (a holster, a pouch, a gun, a door, a soldier) is not a climb.
    /// </summary>
    internal static class HandClimb
    {
        /// <summary>How near (m) the palm must be to the level's surface to take hold of it.</summary>
        private const float Reach = 0.07f;
        /// <summary>The most the body is moved in one frame (a tracking jump is not a climb).</summary>
        private const float MaxStep = 0.35f;
        /// <summary>The fastest a let-go carries on (m/s).</summary>
        private const float MaxFling = 5f;

        private static readonly object Owner = new object();
        private static readonly bool[] Holding = new bool[2];
        private static readonly Vector3[] Anchor = new Vector3[2];     // where the hand took hold (world)
        private static readonly Vector3[] Grip = new Vector3[2];       // where the glove closes (world, on the surface)
        private static readonly Vector3[] LastRel = new Vector3[2];    // the hand against the body last frame, for the let-go speed
        private static Vector3 _pullVelocity;
        private static readonly Collider[] Hits = new Collider[16];
        /// <summary>The soldier the hands hold on for: another one (a takeover, a new life with no dead frame) ends the hold.</summary>
        private static Actor _holder;
        /// <summary>When each hand's squeeze on the level began (-1: none): it holds on only after <see cref="TakeHoldAfter"/>, if nothing
        /// else took the hand meanwhile (a gun or magazine in reach, a holster, getting into a vehicle; scout).</summary>
        private static readonly float[] SqueezeAt = { -1f, -1f };
        private const float TakeHoldAfter = 0.2f;

        /// <summary>Whether a hand holds on to the level now: the game's movement step is held off (<see cref="ClimbHoldsMovementPatch"/>).</summary>
        internal static bool Climbing => Holding[0] || Holding[1];

        /// <summary>Once a frame, after the weapons.</summary>
        internal static void Update(VRRig rig)
        {
            FpsActorController player = GameContext.Player;
            bool can = rig != null && rig.Mode == RigMode.OnFoot && player != null && player.actor != null && !player.actor.dead
                       && !player.actor.IsSeated() && player.controller != null && player.IsMovementEnabled() && VRConfig.ClimbByHand.Value
                       && !GameContext.GameMenuOpen && !GameManager.IsPaused();
            for (int i = 0; i < 2; i++)
            {
                VRHand hand = rig != null ? rig.Hands[i] : null;
                if (hand == null)
                {
                    continue;
                }
                bool sameSoldier = player != null && ReferenceEquals(player.actor, _holder);
                if (Holding[i] && (!can || !sameSoldier || !hand.Tracked || !hand.GrabHeld || hand.Role != HandRole.Free))
                {
                    // (A swing only on a real let-go: tracked and the grip opened. A controller lost behind the body mid-pull, a death or a
                    // takeover flung the body at the last pull's speed; scout.)
                    LetGo(i, hand, player, can && sameSoldier && hand.Tracked && !hand.GrabHeld);
                    continue;
                }
                if (Holding[i])
                {
                    continue;
                }
                if (!can || !hand.Tracked || !hand.GrabHeld || hand.Role != HandRole.Free || hand.Held != null)
                {
                    SqueezeAt[i] = -1f;
                    continue;
                }
                if (hand.GrabPressed)
                {
                    SqueezeAt[i] = Time.unscaledTime; // (not consumed: a pickup in reach may still take this squeeze)
                }
                if (SqueezeAt[i] < 0f || Time.unscaledTime - SqueezeAt[i] < TakeHoldAfter)
                {
                    continue;
                }
                SqueezeAt[i] = -1f;
                if (TouchesLevel(hand.Palm.position, out Vector3 onSurface))
                {
                    if (!Climbing)
                    {
                        _holder = player.actor;
                    }
                    Holding[i] = true;
                    Anchor[i] = hand.Raw.position;
                    Grip[i] = onSurface;
                    LastRel[i] = hand.Raw.position - player.transform.position;
                    hand.Haptic(0.05f, 120f, 0.5f);
                    if (!Climbing || !Holding[1 - i])
                    {
                        FreezeFall(player);
                    }
                    Log.InfoOnce("climb", "Climbing: a hand took hold of the level; the body moves against it.");
                }
            }
            if (!Climbing || player == null || player.controller == null)
            {
                return;
            }
            // The body moved so each holding hand is back where it took hold: the mean of their pulls.
            Vector3 pull = Vector3.zero, handSpeed = Vector3.zero;
            int n = 0;
            float dt = Mathf.Max(Time.deltaTime, 1e-4f);
            for (int i = 0; i < 2; i++)
            {
                if (!Holding[i])
                {
                    continue;
                }
                VRHand hand = rig.Hands[i];
                pull += Anchor[i] - hand.Raw.position;
                Vector3 rel = hand.Raw.position - player.transform.position;
                handSpeed += (rel - LastRel[i]) / dt;
                LastRel[i] = rel;
                CockpitPin.Set(Owner, hand, Grip[i]);
                n++;
            }
            if (n == 0)
            {
                return;
            }
            pull /= n;
            // (The body's speed is the opposite of the hands' against it, their mean, eased once a frame: a frame's jitter is not a swing.)
            _pullVelocity = Vector3.Lerp(_pullVelocity, -handSpeed / n, 1f - Mathf.Exp(-12f * dt));
            if (pull.sqrMagnitude > MaxStep * MaxStep)
            {
                pull = pull.normalized * MaxStep;
            }
            Vector3 before = player.transform.position;
            player.controller.Move(pull);
            Vector3 moved = player.transform.position - before;
            SmoothPlayerMovement spm = player.smoothPlayerMovement;
            if (spm != null)
            {
                // (Kept out of the game's fixed-step smoothing, as the room-scale steps are: the view follows the pull 1:1.)
                spm.lastFixedWorldPos += moved;
                spm.nextFixedWorldPos += moved;
            }
        }

        /// <summary>A hand lets go: the pin goes, and with no hand left holding the body carries on at the pull's speed.</summary>
        private static void LetGo(int i, VRHand hand, FpsActorController player, bool fling)
        {
            Holding[i] = false;
            CockpitPin.Set(Owner, hand, null);
            if (!Climbing && fling && player != null && player.controller != null)
            {
                Vector3 v = Vector3.ClampMagnitude(_pullVelocity, MaxFling);
                MoveVelocity(player.controller) = v;
                hand.Haptic(0.03f, 90f, 0.3f);
            }
            if (!Climbing)
            {
                _pullVelocity = Vector3.zero;
            }
        }

        /// <summary>Everything let go of (the rig going, a death, a seat): the pins too.</summary>
        internal static void Reset(VRRig rig)
        {
            for (int i = 0; i < 2; i++)
            {
                if (Holding[i] && rig != null && rig.Hands[i] != null)
                {
                    CockpitPin.Set(Owner, rig.Hands[i], null);
                }
                Holding[i] = false;
                SqueezeAt[i] = -1f;
            }
            _pullVelocity = Vector3.zero;
            _holder = null;
        }

        private static readonly AccessTools.FieldRef<FirstPersonController, Vector3> MoveVelocity =
            AccessTools.FieldRefAccess<FirstPersonController, Vector3>("m_MoveVelocity");

        /// <summary>Taking hold: the fall's speed so far stopped (hanging on, the body does not drop on at it).</summary>
        private static void FreezeFall(FpsActorController player)
        {
            if (player != null && player.controller != null)
            {
                MoveVelocity(player.controller) = Vector3.zero;
            }
        }

        /// <summary>
        /// The palm on the level itself: the nearest point of a solid collider within reach that has no physics body and belongs to
        /// no soldier and no vehicle (a magazine, a prop, a gun, a ragdoll, a vehicle's hull are not climbed).
        /// </summary>
        private static bool TouchesLevel(Vector3 palm, out Vector3 onSurface)
        {
            onSurface = palm;
            int n = Physics.OverlapSphereNonAlloc(palm, Reach, Hits, ~0, QueryTriggerInteraction.Ignore);
            for (int k = 0; k < n; k++)
            {
                if (Hits[k] != null && Hits[k].GetComponentInParent<Vehicle>() != null)
                {
                    return false; // (a hand on a vehicle is getting in or holding on to it, never a climb up the wall beside it; scout)
                }
            }
            float best = float.MaxValue;
            bool found = false;
            for (int k = 0; k < n; k++)
            {
                Collider c = Hits[k];
                if (c == null || c.isTrigger || c.attachedRigidbody != null || c is CharacterController
                    || c.GetComponentInParent<Actor>() != null || c.GetComponentInParent<Vehicle>() != null
                    || VRRig.Instance != null && c.transform.IsChildOf(VRRig.Instance.Root))
                {
                    continue;
                }
                // (ClosestPoint works on boxes, spheres, capsules and convex meshes only: on terrain or a concave mesh the glove closes
                // where the palm is, which touches it.)
                bool simple = c is BoxCollider || c is SphereCollider || c is CapsuleCollider || c is MeshCollider mc && mc.convex;
                Vector3 p = simple ? c.ClosestPoint(palm) : palm;
                float d = (p - palm).sqrMagnitude;
                if (d < best)
                {
                    best = d;
                    onSurface = p;
                    found = true;
                }
            }
            return found;
        }
    }

    /// <summary>While a hand holds on to the level, the game's movement step (walking, gravity, jumping) is held off: the player hangs.</summary>
    [HarmonyPatch(typeof(FirstPersonController), "FixedUpdate")]
    internal static class ClimbHoldsMovementPatch
    {
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(FirstPersonController __instance)
        {
            if (!HandClimb.Climbing || !VRManager.Active)
            {
                return true;
            }
            FpsActorController player = GameContext.Player;
            return player == null || !ReferenceEquals(player.controller, __instance);
        }
    }
}
