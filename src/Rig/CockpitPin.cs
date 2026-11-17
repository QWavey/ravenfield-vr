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
        private static readonly float[] Way = { 1f, 1f }; // per hand: which way along its bar the fist lies (kept: see Set)

        /// <summary>Every pin's owner forgotten (the rig taken down: a dead owner kept every later pin of that hand off; scout).</summary>
        internal static void Reset()
        {
            Owner[0] = Owner[1] = null;
            Way[0] = Way[1] = 1f;
        }

        /// <summary>Pins <paramref name="hand"/>'s glove at <paramref name="point"/> (world) for <paramref name="owner"/>, or with null lets
        /// go of a pin that owner set.</summary>
        internal static void Set(object owner, VRHand hand, Vector3? point) => Set(owner, hand, point, null);

        /// <summary>
        /// As <see cref="Set(object, VRHand, Vector3?)"/>, the fist also turned so the bar it holds (a rim's tangent, the hatch's bar,
        /// world) runs through its hollow: the palm frame's up (the back of the fist's top: a held handle's line) laid along the bar,
        /// whichever way along it is nearer, so the hand turns the least and never flips. Without, a hand pinned on a rim kept the
        /// controller's turn and the rim ran through its fingers.
        /// </summary>
        internal static void Set(object owner, VRHand hand, Vector3? point, Vector3? bar)
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
                Quaternion turn = Quaternion.identity;
                if (bar.HasValue)
                {
                    // Which way along the bar: the nearer, but only changed past a margin (with the palm's up square across the bar
                    // the nearer way swapped with each tremor of the hand and the fist spun half a turn; scout).
                    Vector3 up = hand.Palm.rotation * Vector3.up;
                    float dot = Vector3.Dot(up.normalized, bar.Value.normalized);
                    if (dot > 0.2f)
                    {
                        Way[i] = 1f;
                    }
                    else if (dot < -0.2f)
                    {
                        Way[i] = -1f;
                    }
                    turn = Onto(up, bar.Value * Way[i]);
                }
                // (Turned about the palm's middle, which goes onto the grip point.)
                hand.Model.SnapPose = new RigidPose(point.Value + turn * (glove.position - hand.PalmCentre), turn * glove.rotation);
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

        /// <summary>
        /// The least turn laying <paramref name="up"/> along <paramref name="bar"/> (either way along it, the nearer). Pure (offline tests).
        /// </summary>
        internal static Quaternion AlongBar(Vector3 up, Vector3 bar)
        {
            if (up.sqrMagnitude < 1e-8f || bar.sqrMagnitude < 1e-8f)
            {
                return Quaternion.identity;
            }
            Vector3 b = bar.normalized;
            if (Vector3.Dot(up, b) < 0f)
            {
                b = -b;
            }
            return Onto(up, b);
        }

        /// <summary>The least turn laying <paramref name="up"/> onto <paramref name="bar"/> (this way along it). Written out, axis and half angle.</summary>
        private static Quaternion Onto(Vector3 up, Vector3 bar)
        {
            if (up.sqrMagnitude < 1e-8f || bar.sqrMagnitude < 1e-8f)
            {
                return Quaternion.identity;
            }
            Vector3 b = bar.normalized;
            // (Written out, axis and half angle: Unity's FromToRotation is native, and the offline tests run without the engine.
            // The dot is never negative here, so the turn is at most 90 degrees and never the undefined half turn.)
            Vector3 u = up.normalized;
            Vector3 axis = Vector3.Cross(u, b);
            float sin = axis.magnitude;
            if (sin < 1e-6f)
            {
                if (Vector3.Dot(u, b) > 0f)
                {
                    return Quaternion.identity;
                }
                // (Exactly opposite: half a turn about any axis square to it.)
                Vector3 square = Vector3.Cross(u, Mathf.Abs(u.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
                return new Quaternion(square.x, square.y, square.z, 0f);
            }
            float angle = Mathf.Atan2(sin, Vector3.Dot(u, b));
            Vector3 n = axis / sin * Mathf.Sin(angle * 0.5f);
            return new Quaternion(n.x, n.y, n.z, Mathf.Cos(angle * 0.5f));
        }

        /// <summary>The tangent of a rim (its turning axis) at a point on it: the line a hand on the rim holds.</summary>
        internal static Vector3 RimTangent(Vector3 centre, Vector3 axis, Vector3 onRim) => Vector3.Cross(axis, onRim - centre).normalized;

        /// <summary>The point on a rim (centre, turning axis, radius) nearest the palm: where a hand on the rim closes. Pure (offline tests).</summary>
        internal static Vector3 OnRim(Vector3 centre, Vector3 axis, float radius, Vector3 palm)
        {
            Vector3 r = Vector3.ProjectOnPlane(palm - centre, axis);
            return r.sqrMagnitude < 1e-8f ? centre + Vector3.Cross(axis, Vector3.right).normalized * radius : centre + r.normalized * radius;
        }
    }
}
