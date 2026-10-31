using System.Collections.Generic;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The mod's loose bodies (cases, magazines and weapons on the ground, duds) drawn without interpolation while they lie still:
    /// the physics resets every interpolated body each frame, sleeping or not (PhysicsResetInterpolatedTransformPosition, 0.33 ms
    /// of the user's Medium frames with some 260 bodies in the scene, up to 40 cases, 16 weapons and the magazines among them).
    /// Smoothed again a quarter of a second at most after one wakes (knocked, picked up), and at once while it is kinematic (in a
    /// hand, its owner moves it); each body as it was when VR stops.
    /// </summary>
    internal static class RestingBodies
    {
        private static readonly List<Rigidbody> Bodies = new List<Rigidbody>();
        private static float _next;

        /// <summary>One of the mod's loose bodies, made interpolated: looked after from now on.</summary>
        internal static void Add(Rigidbody body)
        {
            if (body != null && !Bodies.Contains(body))
            {
                Bodies.Add(body);
            }
        }

        /// <summary>Whether a body is drawn interpolated: unless it lies asleep and nothing holds it.</summary>
        internal static bool Interpolated(bool kinematic, bool sleeping) => kinematic || !sleeping;

        /// <summary>Each frame: every quarter of a second, the bodies looked at.</summary>
        internal static void Frame()
        {
            float now = Time.unscaledTime;
            if (now < _next)
            {
                return;
            }
            _next = now + 0.25f;
            for (int i = Bodies.Count - 1; i >= 0; i--)
            {
                Rigidbody b = Bodies[i];
                if (b == null)
                {
                    Bodies.RemoveAt(i);
                    continue;
                }
                RigidbodyInterpolation want = Interpolated(b.isKinematic, b.IsSleeping()) ? RigidbodyInterpolation.Interpolate : RigidbodyInterpolation.None;
                if (b.interpolation != want)
                {
                    b.interpolation = want;
                }
            }
        }

        /// <summary>VR stopping: every body interpolated again, as it was made.</summary>
        internal static void Restore()
        {
            foreach (Rigidbody b in Bodies)
            {
                if (b != null)
                {
                    b.interpolation = RigidbodyInterpolation.Interpolate;
                }
            }
            Bodies.Clear();
        }
    }
}
