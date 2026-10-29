using RavenfieldVR.Rig;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The fingers of one hand of a weapon's first-person arms (Hands = Weapon's arms), moved with the controller while that
    /// hand holds nothing of the gun: the weapon's left hand off the gun kept the animation's grip of this gun's handguard in
    /// the air, a pose of its own for every weapon (user, 2026-10-29: "the hand pose of the left hand is used for the hand on
    /// the gun ... before i even touched the weapon and its custom for each weapon so my hand looks weird"). On the gun and on
    /// a magazine of it the animation's and the reload's poses stay as they are (user: "the gun held and support hand held
    /// weapon positions need to stay (plus mag being held)"). Also both hands of the empty-hand arms (<see cref="EmptyArms"/>).
    /// </summary>
    internal sealed class ArmFingers
    {
        /// <summary>Thumb to little finger, up to three joints each (by name, as the analysis finds them); a missing finger is null.</summary>
        private readonly Transform[][] _fingers;
        /// <summary>Each joint's pose from the animation, and what this class wrote to it last frame.</summary>
        private readonly Quaternion[][] _fingerBase, _fingerWritten;
        private bool _fingersWritten;

        public ArmFingers(Transform hand)
        {
            _fingers = Chains(hand);
            _fingerBase = new Quaternion[5][];
            _fingerWritten = new Quaternion[5][];
            for (int f = 0; f < 5; f++)
            {
                int n = _fingers[f] != null ? _fingers[f].Length : 0;
                _fingerBase[f] = new Quaternion[n];
                _fingerWritten[f] = new Quaternion[n];
            }
        }

        /// <summary>Each finger's joints under the hand, thumb to little finger (the analysis's names and walk); null where none is found.</summary>
        private static Transform[][] Chains(Transform hand)
        {
            var chains = new Transform[5][];
            Transform[] under = hand.GetComponentsInChildren<Transform>(true);
            for (int f = 0; f < 5; f++)
            {
                Transform first = null;
                foreach (Transform t in under)
                {
                    if (t != hand && WeaponAnalyzer.FingerPatterns[f].IsMatch(t.name) && (t.parent == hand || !WeaponAnalyzer.FingerPatterns[f].IsMatch(t.parent.name)))
                    {
                        first = t;
                        break;
                    }
                }
                var joints = new System.Collections.Generic.List<Transform>();
                for (Transform j = first; j != null && joints.Count < 3;)
                {
                    joints.Add(j);
                    Transform next = null;
                    foreach (Transform c in j)
                    {
                        if (WeaponAnalyzer.FingerPatterns[f].IsMatch(c.name) || j.childCount == 1)
                        {
                            next = c;
                            break;
                        }
                    }
                    j = next;
                }
                // Two joints at least, or one with a child to aim it by: a bone's direction is to the next one.
                chains[f] = joints.Count >= 2 || joints.Count == 1 && joints[0].childCount > 0 ? joints.ToArray() : null;
            }
            return chains;
        }

        /// <summary>How far each joint bends in a full fist (degrees): the fingers' knuckle, middle and end joints, the thumb's.</summary>
        private static readonly float[] FingerFist = { 80f, 95f, 70f }, ThumbFist = { 0f, 45f, 45f };
        /// <summary>A hand at rest is never ruler-straight (the VR glove's rest curls, thumb to little finger).</summary>
        private static readonly float[] RestCurl = { 0.1f, 0.12f, 0.16f, 0.2f, 0.24f };

        /// <summary>
        /// The fingers follow the controller as the VR glove's do (the trigger the index, the grip the others, or the controller's
        /// own finger tracking), eased into the animation's pose by <paramref name="onGun"/> (1: the animation's grip as it is).
        /// Each finger bends in its own plane (the animation's curl gives it, else across the palm), each joint to its share of
        /// a fist. The animation's pose is read fresh whenever an animator wrote one, so nothing compounds frame to frame.
        /// </summary>
        public void Pose(VRHand vrHand, Transform hand, ArmChain c, float onGun)
        {
            if (_fingers == null || hand == null || c == null)
            {
                return;
            }
            Input.Hand side = vrHand.Side;
            bool skeleton = Input.VRInput.HasSkeleton(side);
            float grip = Input.VRInput.GetGrip(side), trigger = Input.VRInput.GetTrigger(side);
            Vector3 fingerDir = hand.rotation * c.FingerDir, palm = -(hand.rotation * c.BackDir);
            var dir = new Vector3[3];
            var baseAngle = new float[3];
            for (int f = 0; f < 5; f++)
            {
                Transform[] j = _fingers[f];
                if (j == null)
                {
                    continue;
                }
                for (int s = 0; s < j.Length; s++)
                {
                    Quaternion now = j[s].localRotation;
                    if (!_fingersWritten || Quaternion.Angle(now, _fingerWritten[f][s]) > 0.05f)
                    {
                        _fingerBase[f][s] = now; // the animator's pose this frame (or the first one seen)
                    }
                    j[s].localRotation = _fingerBase[f][s];
                }
                if (onGun < 0.999f)
                {
                    float curl = skeleton ? Input.VRInput.GetCurl(side, f) : f == 0 ? grip * 0.6f : f == 1 ? trigger : grip;
                    curl = Mathf.Clamp01(Mathf.Max(curl, RestCurl[f]));
                    for (int s = 0; s < j.Length; s++)
                    {
                        Vector3 to = s + 1 < j.Length ? j[s + 1].position : j[s].childCount > 0 ? j[s].GetChild(0).position : j[s].position + (s > 0 ? dir[s - 1] : fingerDir) * 0.02f;
                        dir[s] = to - j[s].position;
                    }
                    // The bend plane: the animation's own curl, else across the palm; turned so a positive angle closes the finger.
                    Vector3 axis = j.Length >= 2 ? Vector3.Cross(dir[0], dir[1]) : Vector3.zero;
                    if (axis.sqrMagnitude < 1e-10f || Vector3.Angle(dir[0], dir[1]) < 8f)
                    {
                        axis = Vector3.Cross(dir[0], palm);
                    }
                    if (axis.sqrMagnitude < 1e-10f)
                    {
                        continue;
                    }
                    axis.Normalize();
                    if (Vector3.Dot(Vector3.Cross(axis, dir[0]), palm) < 0f && f != 0)
                    {
                        axis = -axis;
                    }
                    float[] fist = f == 0 ? ThumbFist : FingerFist;
                    for (int s = 0; s < j.Length; s++)
                    {
                        Vector3 incoming = s > 0 ? dir[s - 1] : Vector3.ProjectOnPlane(fingerDir, axis);
                        baseAngle[s] = Vector3.SignedAngle(Vector3.ProjectOnPlane(incoming, axis), Vector3.ProjectOnPlane(dir[s], axis), axis);
                    }
                    // Each joint by its difference from the animation's (a turn about the shared axis keeps the joints after it as
                    // they are relative to it). The thumb's root stays where the animation has it (it does not bend in that plane).
                    for (int s = 0; s < j.Length; s++)
                    {
                        if (f == 0 && s == 0)
                        {
                            continue;
                        }
                        j[s].rotation = Quaternion.AngleAxis(curl * fist[Mathf.Min(s, 2)] - baseAngle[s], axis) * j[s].rotation;
                    }
                    for (int s = 0; s < j.Length; s++)
                    {
                        j[s].localRotation = Quaternion.Slerp(j[s].localRotation, _fingerBase[f][s], onGun);
                    }
                }
                for (int s = 0; s < j.Length; s++)
                {
                    _fingerWritten[f][s] = j[s].localRotation;
                }
            }
            _fingersWritten = true;
        }
    }
}
