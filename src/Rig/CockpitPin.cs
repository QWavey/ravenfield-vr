using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// A hand holding a vehicle's control is drawn on it: the glove pinned with its grasp point on the handle, the rim, the lever's
    /// knob (the user, 2026-11-15: "i want that my hand actually grabs the things and not just floats in the air while grabbing, this
    /// is also the issue with the boats and car steering wheels"). The controller moves on as tracked and drives the control; only
    /// the drawn hand (and the weapon's arm with it, HandModel.WithSnap) sits on the grip, turned as the hand is.
    /// Each pin is the setter's own: a system clears only what it pinned, never a gun's grip or a soldier held by the other system.
    /// </summary>
    internal static class CockpitPin
    {
        private static readonly object[] Owner = new object[2];

        /// <summary>Pins <paramref name="hand"/>'s glove at <paramref name="point"/> (world) for <paramref name="owner"/>, or with null lets
        /// go of a pin that owner set.</summary>
        internal static void Set(object owner, VRHand hand, Vector3? point)
        {
            if (hand == null || hand.Model == null)
            {
                return;
            }
            int i = (int)hand.Side;
            if (i < 0 || i > 1)
            {
                return;
            }
            if (point.HasValue && hand.Tracked)
            {
                if (Owner[i] != null && !ReferenceEquals(Owner[i], owner))
                {
                    return; // (held by another system's control this frame: it keeps the hand)
                }
                RigidPose glove = hand.Model.TrackedGlovePose;
                hand.Model.SnapPose = new RigidPose(glove.position + (point.Value - hand.PalmCentre), glove.rotation);
                Owner[i] = owner;
            }
            else if (ReferenceEquals(Owner[i], owner))
            {
                hand.Model.SnapPose = null;
                Owner[i] = null;
            }
        }

        /// <summary>Lets go of every pin <paramref name="owner"/> set (its seat left, the rig going).</summary>
        internal static void ClearAll(object owner, VRHand[] hands)
        {
            if (hands == null)
            {
                return;
            }
            foreach (VRHand hand in hands)
            {
                Set(owner, hand, null);
            }
        }

        /// <summary>The point on a rim (centre, turning axis, radius) nearest the palm: where a hand on the rim closes. Pure (offline tests).</summary>
        internal static Vector3 OnRim(Vector3 centre, Vector3 axis, float radius, Vector3 palm)
        {
            Vector3 r = Vector3.ProjectOnPlane(palm - centre, axis);
            return r.sqrMagnitude < 1e-8f ? centre + Vector3.Cross(axis, Vector3.right).normalized * radius : centre + r.normalized * radius;
        }
    }
}
