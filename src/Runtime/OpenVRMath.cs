using UnityEngine;
using Valve.VR;

namespace RavenfieldVR.Runtime
{
    /// <summary>Conversions between OpenVR's right-handed (-Z forward) space and Unity's left-handed (+Z forward) space.</summary>
    public static class OpenVRMath
    {
        public static void ToUnity(in HmdMatrix34_t p, out Vector3 position, out Quaternion rotation)
        {
            // Mirror the Z axis: negate the third row and third column (the [2,2] element keeps its sign).
            var forward = new Vector3(-p.m2, -p.m6, p.m10);
            var up = new Vector3(p.m1, p.m5, -p.m9);
            position = new Vector3(p.m3, p.m7, -p.m11);
            rotation = SafeLookRotation(forward, up);
        }

        public static Vector3 ToUnityVector(in HmdVector3_t v) => new Vector3(v.v0, v.v1, -v.v2);

        /// <summary>Angular velocity is an axial vector, so mirroring Z flips the sign of X and Y instead.</summary>
        public static Vector3 ToUnityAngular(in HmdVector3_t w) => new Vector3(-w.v0, -w.v1, w.v2);

        public static Quaternion SafeLookRotation(Vector3 forward, Vector3 up)
        {
            if (forward.sqrMagnitude < 1e-10f)
            {
                return Quaternion.identity;
            }
            if (up.sqrMagnitude < 1e-10f || Vector3.Cross(forward, up).sqrMagnitude < 1e-10f)
            {
                up = Mathf.Abs(forward.normalized.y) > 0.95f ? Vector3.forward : Vector3.up;
            }
            return Quaternion.LookRotation(forward, up);
        }
    }

    /// <summary>A rigid transform (position + rotation) with the composition helpers Unity lacks in 2020.3.</summary>
    public struct RigidPose
    {
        public Vector3 position;
        public Quaternion rotation;

        public RigidPose(Vector3 position, Quaternion rotation)
        {
            this.position = position;
            this.rotation = rotation;
        }

        public static RigidPose Identity => new RigidPose(Vector3.zero, Quaternion.identity);

        public static RigidPose Of(Transform t) => new RigidPose(t.position, t.rotation);
        public static RigidPose LocalOf(Transform t) => new RigidPose(t.localPosition, t.localRotation);

        public Vector3 Forward => rotation * Vector3.forward;
        public Vector3 Up => rotation * Vector3.up;
        public Vector3 Right => rotation * Vector3.right;

        /// <summary>this * other: apply <paramref name="other"/> expressed in this pose's space.</summary>
        public RigidPose Multiply(in RigidPose other) =>
            new RigidPose(position + rotation * other.position, rotation * other.rotation);

        public RigidPose Inverse()
        {
            Quaternion inv = Conjugate(rotation);
            return new RigidPose(inv * -position, inv);
        }

        public Vector3 TransformPoint(Vector3 p) => position + rotation * p;
        public Vector3 InverseTransformPoint(Vector3 p) => Conjugate(rotation) * (p - position);
        public Vector3 TransformDirection(Vector3 d) => rotation * d;
        public Vector3 InverseTransformDirection(Vector3 d) => Conjugate(rotation) * d;

        /// <summary>
        /// The inverse of a rotation (a unit quaternion, as every pose's is: q * v assumes it too): its conjugate. Quaternion.Inverse
        /// is a native call in this Unity, made once per point by the hands and the magazine keep-out; a default pose's zero
        /// quaternion also gave NaN there (divided by its zero length), here a zero (improve round, 2026-10-22).
        /// </summary>
        public static Quaternion Conjugate(Quaternion q) => new Quaternion(-q.x, -q.y, -q.z, q.w);

        public void ApplyTo(Transform t) => t.SetPositionAndRotation(position, rotation);

        public static RigidPose Lerp(in RigidPose a, in RigidPose b, float t) =>
            new RigidPose(Vector3.Lerp(a.position, b.position, t), Quaternion.Slerp(a.rotation, b.rotation, t));

        public override string ToString() => $"({position.x:0.000}, {position.y:0.000}, {position.z:0.000}) {rotation.eulerAngles}";
    }
}
