using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Contact for a drawn hand, the way Boneworks' physics hand behaves (its PhysHand is a rigidbody pushed toward the tracked
    /// hand, so it stops where it meets a gun, and CheckStuck/SetHand put it back on the tracked hand once it is held too far
    /// off): swept from where the glove was drawn toward where the hand puts it, stopped at the last clear step, the rest of the
    /// move slid along the given axes. Searching out from wherever the tracked hand is now let a fast swipe through a thin gun
    /// land clear on the far side and draw the glove there, and left a hand pushed in deeper than the search inside (user,
    /// 2026-10-23: "hands glitch through the gun with enough force").
    /// Pure (the glove test is passed in) so the offline tests check it on made-up solids. Shifts are from where the tracked
    /// hand puts the glove.
    /// </summary>
    internal static class GloveSweep
    {
        /// <summary>Steps a sweep takes at most (a fast hand's 8 cm a frame is 16 of 5 mm).</summary>
        private const int MaxSteps = 80;

        /// <summary>
        /// The glove's shift this frame: from <paramref name="from"/> (where it was drawn) toward <paramref name="aim"/> (where the
        /// hand, and what it holds, want it) to the last clear step, then what is left slid along each of <paramref name="axes"/>.
        /// A last place that is now blocked (the fingers closed onto the gun, the gun moved onto them) is first freed the
        /// shortest way within <paramref name="unstick"/>: away from the aim, then along the axes. Null when it cannot be: the
        /// caller searches a way out instead. Let go to <paramref name="aim"/> when that is clear and the glove is held more than
        /// <paramref name="release"/> from it (the real hand went round or through to the far side).
        /// </summary>
        internal static Vector3? Shift(System.Func<Vector3, bool> blocked, Vector3 from, Vector3 aim, Vector3[] axes,
                                       float step, float release, float unstick)
        {
            if (blocked(from))
            {
                Vector3? freed = Unstick(blocked, from, aim, axes, step, unstick);
                if (!freed.HasValue)
                {
                    return null;
                }
                from = freed.Value;
            }
            Vector3 drawn = Advance(blocked, from, aim - from, step);
            if (axes != null)
            {
                foreach (Vector3 axis in axes)
                {
                    Vector3 part = axis * Vector3.Dot(aim - drawn, axis);
                    if (part.sqrMagnitude > 1e-10f)
                    {
                        drawn = Advance(blocked, drawn, part, step);
                    }
                }
            }
            if ((drawn - aim).sqrMagnitude > release * release && !blocked(aim))
            {
                return aim;
            }
            return drawn;
        }

        /// <summary>From <paramref name="from"/> along <paramref name="delta"/> in steps of about <paramref name="step"/>: the last clear point.</summary>
        internal static Vector3 Advance(System.Func<Vector3, bool> blocked, Vector3 from, Vector3 delta, float step)
        {
            int steps = Mathf.Clamp(Mathf.CeilToInt(delta.magnitude / step), 1, MaxSteps);
            Vector3 last = from;
            for (int i = 1; i <= steps; i++)
            {
                Vector3 p = from + delta * ((float)i / steps);
                if (blocked(p))
                {
                    return last;
                }
                last = p;
            }
            return last;
        }

        private static readonly Vector3[] UnstickDirs = new Vector3[7];

        /// <summary>The nearest clear point to <paramref name="from"/> within <paramref name="reach"/>: away from the aim first, then both ways along each axis.</summary>
        private static Vector3? Unstick(System.Func<Vector3, bool> blocked, Vector3 from, Vector3 aim, Vector3[] axes, float step, float reach)
        {
            Vector3 away = from - aim;
            int n = 0;
            if (away.sqrMagnitude > 1e-10f)
            {
                UnstickDirs[n++] = away.normalized;
            }
            if (axes != null)
            {
                for (int i = 0; i < axes.Length && n + 1 < UnstickDirs.Length; i++)
                {
                    UnstickDirs[n++] = axes[i];
                    UnstickDirs[n++] = -axes[i];
                }
            }
            for (float s = step; s <= reach + 1e-5f; s += step)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector3 p = from + UnstickDirs[i] * s;
                    if (!blocked(p))
                    {
                        return p;
                    }
                }
            }
            return null;
        }
    }
}
