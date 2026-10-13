using System.Collections.Generic;
using System.Text.RegularExpressions;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// One of the weapon's own first-person hands, read live from its arm rig: the bones keep animating under the hidden
    /// arm meshes, so they show exactly how this gun is held right now. The frame is measured from the bones themselves
    /// (wrist to the finger roots, and toward the thumb root), so it does not depend on how the rig's bone axes were
    /// exported. Expressed in the glove's convention: origin at the middle of the palm, +Z toward the fingers, +Y toward
    /// the thumb.
    /// </summary>
    internal sealed class ModHand
    {
        private static readonly Regex Thumb = new Regex(@"thumb|daumen|拇指", RegexOptions.IgnoreCase);
        private static readonly Regex Finger = new Regex(@"index|middle|ring|pinky|pinkie|little|finger|食指|中指|无名指|小指", RegexOptions.IgnoreCase);

        private readonly Transform _hand;
        private readonly Transform[] _fingers;
        private readonly Transform _thumb;

        private ModHand(Transform hand, Transform[] fingers, Transform thumb)
        {
            _hand = hand;
            _fingers = fingers;
            _thumb = thumb;
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
            if (toThumb.sqrMagnitude < 1e-8f)
            {
                return false;
            }
            // The same palm point the analysis took as the weapon's grip (between the wrist and the finger roots).
            pose = new RigidPose(Vector3.Lerp(wrist, roots, 0.55f), OpenVRMath.SafeLookRotation(along.normalized, toThumb.normalized));
            return true;
        }
    }
}
