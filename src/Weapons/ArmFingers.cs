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

        /// <summary>How far each joint of a pulled finger bends past the animation's pose at a full pull (degrees, knuckle to tip).</summary>
        private static readonly float[] PullBend = { 14f, 18f, 12f };

        /// <summary>
        /// One finger of a hand on the gun pulled on top of the animation's pose, by <paramref name="amount"/> (0-1: the trigger):
        /// the gun hand's index follows the trigger, as the VR glove's does; the hand and its other fingers keep the grip the gun's
        /// animation gives them (the user: "the gun held ... weapon positions need to stay"). It bent nowhere before, however hard
        /// the trigger was pulled. In the finger's own curl plane, the animation's pose read fresh as in <see cref="Pose"/>.
        /// </summary>
        public void Pull(Transform hand, ArmChain c, int finger, float amount)
        {
            Transform[] j = _fingers[finger];
            if (j == null || hand == null || c == null)
            {
                return;
            }
            for (int s = 0; s < j.Length; s++)
            {
                Quaternion now = j[s].localRotation;
                if (!_fingersWritten || Quaternion.Angle(now, _fingerWritten[finger][s]) > 0.05f)
                {
                    _fingerBase[finger][s] = now;
                }
                j[s].localRotation = _fingerBase[finger][s];
            }
            if (amount > 0.01f && j.Length >= 2)
            {
                Vector3 palm = -(hand.rotation * c.BackDir);
                Vector3 d0 = j[1].position - j[0].position;
                Vector3 d1 = j.Length > 2 ? j[2].position - j[1].position : j[1].childCount > 0 ? j[1].GetChild(0).position - j[1].position : d0;
                Vector3 axis = Vector3.Angle(d0, d1) >= 8f ? Vector3.Cross(d0, d1) : Vector3.Cross(d0, palm);
                if (axis.sqrMagnitude > 1e-10f)
                {
                    axis.Normalize();
                    if (Vector3.Dot(Vector3.Cross(axis, d0), palm) < 0f)
                    {
                        axis = -axis;
                    }
                    for (int s = 0; s < j.Length; s++)
                    {
                        j[s].rotation = Quaternion.AngleAxis(Mathf.Clamp01(amount) * PullBend[Mathf.Min(s, 2)], axis) * j[s].rotation;
                    }
                }
            }
            for (int s = 0; s < j.Length; s++)
            {
                _fingerWritten[finger][s] = j[s].localRotation;
            }
            _fingersWritten = true;
        }

        /// <summary>
        /// The joints' rotations now as this frame's last write: called after something else turned them on top of <see cref="Pose"/>
        /// (ArmIK's magazine hold), so the next frame does not take that for an animator's pose and keep it as the base.
        /// </summary>
        public void Remember()
        {
            for (int f = 0; f < 5; f++)
            {
                Transform[] j = _fingers[f];
                for (int s = 0; j != null && s < j.Length; s++)
                {
                    _fingerWritten[f][s] = j[s].localRotation;
                }
            }
        }

        /// <summary>
        /// Self-test: how far the fingers are bent toward <paramref name="palm"/> (the palm's side), on average (degrees summed over
        /// each finger's joints, a joint bent back counted against it: unsigned, a finger bent backward read as closing; review);
        /// -1: none found.
        /// </summary>
        internal float MeanBend(Vector3 palm)
        {
            float sum = 0f;
            int n = 0;
            foreach (Transform[] j in _fingers)
            {
                if (j == null)
                {
                    continue;
                }
                float bend = 0f;
                for (int s = 0; s + 1 < j.Length; s++)
                {
                    Vector3 a = j[s + 1].position - j[s].position;
                    Vector3 b = s + 2 < j.Length ? j[s + 2].position - j[s + 1].position
                        : j[s + 1].childCount > 0 ? j[s + 1].GetChild(0).position - j[s + 1].position : Vector3.zero;
                    if (a.sqrMagnitude > 1e-10f && b.sqrMagnitude > 1e-10f)
                    {
                        float angle = Vector3.Angle(a, b);
                        bend += Vector3.Dot(b.normalized - a.normalized, palm) >= 0f ? angle : -angle;
                    }
                }
                sum += bend;
                n++;
            }
            return n > 0 ? sum / n : -1f;
        }

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

        /// <summary>
        /// One finger laid in a relaxed hand's shape and bent to <paramref name="curl"/> of a fist, then eased into the animation's
        /// pose by <paramref name="onGun"/>. Off the gun the weapon's fingers kept their shape from its grip, only bent in the grip's
        /// own plane: the thumb stayed wrapped round a handguard that was not there and the fingers spread as on the gun (user,
        /// 2026-11-03: "on some hands the things are very glitched (thumb etc) because they lay in another position on the gun ...
        /// cant we use the normal hand positions ... but when holding guns they fastly fade in the positions they are supposed to
        /// stay in"). Each joint is aimed anew: a finger along the hand's way (wrist to knuckles), curling toward the palm; the
        /// thumb out beside the index and a little toward the palm, curling across it. On the gun the animation's pose stays.
        /// </summary>
        private void Relax(int f, Transform[] j, Transform hand, Vector3 fingers, Vector3 palm, float curl, float onGun)
        {
            Vector3 start = fingers;
            if (f == 0)
            {
                // Out to its own side (where its root sits against the hand's middle line), a little toward the palm.
                Vector3 side = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(j[0].position - hand.position, fingers), palm);
                start = (fingers * 0.75f + (side.sqrMagnitude > 1e-8f ? side.normalized : Vector3.zero) * 0.55f + palm * 0.35f).normalized;
            }
            Vector3 axis = Vector3.Cross(start, palm); // (a positive turn about it takes the finger toward the palm)
            if (axis.sqrMagnitude > 1e-8f)
            {
                axis.Normalize();
                float[] fist = f == 0 ? ThumbFist : FingerFist;
                float angle = 0f;
                for (int s = 0; s < j.Length; s++)
                {
                    angle += curl * fist[Mathf.Min(s, 2)];
                    Transform next = s + 1 < j.Length ? j[s + 1] : j[s].childCount > 0 ? j[s].GetChild(0) : null;
                    if (next == null)
                    {
                        continue;
                    }
                    Vector3 have = next.position - j[s].position;
                    if (have.sqrMagnitude > 1e-10f)
                    {
                        j[s].rotation = Quaternion.FromToRotation(have, Quaternion.AngleAxis(angle, axis) * start) * j[s].rotation;
                    }
                }
            }
            for (int s = 0; s < j.Length; s++)
            {
                j[s].localRotation = Quaternion.Slerp(j[s].localRotation, _fingerBase[f][s], onGun);
            }
        }

        /// <summary>
        /// The hand's way along its fingers: from the hand bone to the middle of the four fingers' first joints (their knuckles
        /// stay put whatever the fingers do); the analysis's own finger direction where no finger was found.
        /// </summary>
        private Vector3 FingerWay(Transform hand, ArmChain c)
        {
            Vector3 sum = Vector3.zero;
            int n = 0;
            for (int f = 1; f < 5; f++)
            {
                if (_fingers[f] != null)
                {
                    sum += _fingers[f][0].position;
                    n++;
                }
            }
            Vector3 way = n > 0 ? sum / n - hand.position : Vector3.zero;
            return way.sqrMagnitude > 1e-8f ? way.normalized : hand.rotation * c.FingerDir;
        }

        /// <summary>How far each joint bends in a full fist (degrees): the fingers' knuckle, middle and end joints, the thumb's.</summary>
        private static readonly float[] FingerFist = { 80f, 95f, 70f }, ThumbFist = { 0f, 45f, 45f };
        /// <summary>A hand at rest is never ruler-straight (the VR glove's rest curls, thumb to little finger).</summary>
        private static readonly float[] RestCurl = { 0.1f, 0.12f, 0.16f, 0.2f, 0.24f };

        /// <summary>
        /// The fingers follow the controller as the VR glove's do (the trigger the index, the grip the others, or the controller's
        /// own finger tracking), in a relaxed hand's shape (<see cref="Relax"/>), eased into the animation's pose by
        /// <paramref name="onGun"/> (1: the animation's grip as it is). The animation's pose is read fresh whenever an animator wrote
        /// one, so nothing compounds frame to frame.
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
            Vector3 fingers = FingerWay(hand, c);
            Vector3 palm = Vector3.ProjectOnPlane(-(hand.rotation * c.BackDir), fingers);
            palm = palm.sqrMagnitude > 1e-8f ? palm.normalized : -(hand.rotation * c.BackDir);
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
                    Relax(f, j, hand, fingers, palm, curl, onGun);
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
