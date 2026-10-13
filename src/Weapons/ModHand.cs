using System.Collections.Generic;
using System.Text.RegularExpressions;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// One of the weapon's own first-person hands, read live from its arm rig: the bones keep animating under the hidden
    /// arm meshes, so they show exactly how this gun is held right now. The frame is measured from the bones themselves
    /// (wrist to the finger roots, and across the knuckles from the little finger to the index), so it does not depend on
    /// how the rig's bone axes were exported. Expressed in the glove's convention: origin at the middle of the palm, +Z
    /// toward the fingers, +Y toward the thumb side of the knuckles.
    /// </summary>
    internal sealed class ModHand
    {
        private static readonly Regex Thumb = new Regex(@"thumb|daumen|拇指", RegexOptions.IgnoreCase);
        private static readonly Regex Finger = new Regex(@"index|middle|ring|pinky|pinkie|little|finger|食指|中指|无名指|小指", RegexOptions.IgnoreCase);
        private static readonly Regex IndexName = new Regex(@"index|食指", RegexOptions.IgnoreCase);
        private static readonly Regex PinkyName = new Regex(@"pinky|pinkie|little|小指", RegexOptions.IgnoreCase);

        private readonly Transform _hand;
        private readonly Transform[] _fingers;
        private readonly Transform _thumb;
        private readonly Transform _index, _pinky;

        private ModHand(Transform hand, Transform[] fingers, Transform thumb)
        {
            _hand = hand;
            _fingers = fingers;
            _thumb = thumb;
            foreach (Transform f in fingers)
            {
                if (_index == null && IndexName.IsMatch(f.name))
                {
                    _index = f;
                }
                else if (_pinky == null && PinkyName.IsMatch(f.name))
                {
                    _pinky = f;
                }
            }
            if (_index == null || _pinky == null)
            {
                // Numbered fingers: the index is the one nearest the thumb, the little finger the one farthest from it.
                Transform near = null, far = null;
                float nearD = float.MaxValue, farD = -1f;
                foreach (Transform f in fingers)
                {
                    float d = (f.position - thumb.position).sqrMagnitude;
                    if (d < nearD)
                    {
                        nearD = d;
                        near = f;
                    }
                    if (d > farD)
                    {
                        farD = d;
                        far = f;
                    }
                }
                _index = near;
                _pinky = far != near ? far : null;
            }
        }

        /// <summary>The hand of <paramref name="chain"/> with its finger and thumb roots, or null when they are not recognisable.</summary>
        public static ModHand Find(Transform weaponRoot, ArmChain chain)
        {
            Transform hand = chain != null ? WeaponRig.Resolve(weaponRoot, chain.Hand) : null;
            if (hand == null)
            {
                return null;
            }
            var fingers = new List<Transform>();
            Transform thumb = null;
            foreach (Transform c in hand)
            {
                if (Thumb.IsMatch(c.name))
                {
                    thumb = thumb ?? c;
                }
                else if (Finger.IsMatch(c.name))
                {
                    fingers.Add(c);
                }
            }
            return fingers.Count >= 2 && thumb != null ? new ModHand(hand, fingers.ToArray(), thumb) : null;
        }

        /// <summary>Self-test: the bones this hand is read from, in a frame given by <paramref name="toFrame"/> (cm).</summary>
        internal string Describe(System.Func<Vector3, Vector3> toFrame)
        {
            string P(Transform t)
            {
                Vector3 v = toFrame(t.position) * 100f;
                return $"{t.name} ({v.x:0.0} {v.y:0.0} {v.z:0.0})";
            }
            var sb = new System.Text.StringBuilder();
            sb.Append("wrist ").Append(P(_hand)).Append(", thumb ").Append(P(_thumb));
            foreach (Transform f in _fingers)
            {
                if (f != null)
                {
                    sb.Append(", ").Append(P(f));
                }
            }
            return sb.ToString();
        }

        /// <summary>The palm centre and frame right now; false while the hand is collapsed or hidden away.</summary>
        public bool TryPose(Transform weaponRoot, out RigidPose pose)
        {
            pose = default;
            if (_hand == null || _thumb == null)
            {
                return false;
            }
            Vector3 wrist = _hand.position;
            Vector3 roots = Vector3.zero;
            int n = 0;
            foreach (Transform f in _fingers)
            {
                if (f != null)
                {
                    roots += f.position;
                    n++;
                }
            }
            if (n == 0)
            {
                return false;
            }
            roots /= n;
            Vector3 along = roots - wrist;
            if (along.sqrMagnitude < 1e-6f || along.sqrMagnitude > 0.04f)
            {
                return false; // scaled away or stretched by an animation: not a hand pose to copy
            }
            Vector3 toThumb = Vector3.ProjectOnPlane(_thumb.position - wrist, along);
            // Across the knuckles is what turns the hand about its length: the thumb's root sits toward the palm on most
            // rigs and would roll the glove (palm down on a pistol grip), so it only tells which way is the thumb's side.
            Vector3 across = _index != null && _pinky != null ? Vector3.ProjectOnPlane(_index.position - _pinky.position, along) : toThumb;
            if (across.sqrMagnitude < 1e-8f)
            {
                across = toThumb;
            }
            else if (Vector3.Dot(across, _thumb.position - wrist) < 0f)
            {
                across = -across;
            }
            if (across.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            // The same palm point the analysis took as the weapon's grip (between the wrist and the finger roots).
            pose = new RigidPose(Vector3.Lerp(wrist, roots, 0.55f), OpenVRMath.SafeLookRotation(along.normalized, across.normalized));
            return true;
        }
    }
}
