using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>What a hand is currently doing; drives the hand pose and arbitrates grabs.</summary>
    public enum HandRole { Free, Weapon, Foregrip, Slide, Magazine, Throwable, Pointer, Cockpit, Holster }

    /// <summary>
    /// One tracked hand: world poses (raw controller, palm, pointer), derived gun frame, velocity history,
    /// the procedural model and the current interaction role.
    /// </summary>
    public sealed class VRHand
    {
        public readonly Hand Side;
        public readonly Transform Root;
        public HandModel Model { get; private set; }

        public bool Tracked { get; private set; }
        public RigidPose Raw { get; private set; }
        public RigidPose Palm { get; private set; }
        public RigidPose Pointer { get; private set; }

        /// <summary>World-space linear velocity including the rig's own motion.</summary>
        public Vector3 Velocity { get; private set; }
        public Vector3 AngularVelocity { get; private set; }

        public HandRole Role = HandRole.Free;

        /// <summary>Opaque reference to the object held in the current role (magazine prop, grabbed control…).</summary>
        public object Held;

        private const int HistoryLength = 10;
        private readonly Vector3[] _velHistory = new Vector3[HistoryLength];
        private readonly float[] _timeHistory = new float[HistoryLength];
        private int _historyIndex;
        private bool _hasTip, _hasGrip;
        private Vector3 _lastRigPosition;
        private bool _hasLastRig;

        public bool IsLeft => Side == Hand.Left;
        public float Mirror => IsLeft ? -1f : 1f;

        public VRHand(Hand side, Transform rigRoot)
        {
            Side = side;
            Root = new GameObject(side == Hand.Left ? "VR Hand L" : "VR Hand R").transform;
            Root.SetParent(rigRoot, false);
            Model = new HandModel(this);
        }

        /// <summary>Samples the controller poses (predicted for the next frame) and converts them to world space.</summary>
        public void UpdatePose(Transform rigRoot)
        {
            HandPose raw = VRInput.GetPose(Side, PoseKind.Raw);
            Tracked = raw.valid;
            if (!raw.valid)
            {
                Model.SetVisible(false);
                return;
            }

            RigidPose rig = RigidPose.Of(rigRoot);
            Raw = rig.Multiply(raw.pose);
            Root.SetPositionAndRotation(Raw.position, Raw.rotation);

            HandPose tip = VRInput.GetPose(Side, PoseKind.Tip);
            _hasTip = tip.valid;
            Pointer = tip.valid
                ? rig.Multiply(tip.pose)
                : Raw.Multiply(new RigidPose(Vector3.zero, Quaternion.Euler(MirrorEuler(VRConfig.ParseVector(VRConfig.LaserRotationOffset, new Vector3(40, 0, 0))))));

            HandPose grip = VRInput.GetPose(Side, PoseKind.Grip);
            _hasGrip = grip.valid;
            Vector3 palmPos = grip.valid ? rig.TransformPoint(grip.pose.position) : Raw.TransformPoint(new Vector3(0f, -0.025f, -0.055f));
            // The palm frame uses the pointer orientation: forward = where the hand points, up = back of the fist's top.
            Palm = new RigidPose(palmPos, Pointer.rotation);

            Vector3 rigVelocity = Vector3.zero;
            if (_hasLastRig && Time.unscaledDeltaTime > 1e-4f)
            {
                rigVelocity = (rigRoot.position - _lastRigPosition) / Time.unscaledDeltaTime;
                if (rigVelocity.sqrMagnitude > 2500f)
                {
                    rigVelocity = Vector3.zero; // teleport, not motion
                }
            }
            _lastRigPosition = rigRoot.position;
            _hasLastRig = true;

            Velocity = rig.rotation * raw.velocity + rigVelocity;
            AngularVelocity = rig.rotation * raw.angularVelocity;
            _velHistory[_historyIndex] = Velocity;
            _timeHistory[_historyIndex] = Time.unscaledTime;
            _historyIndex = (_historyIndex + 1) % HistoryLength;
        }

        /// <summary>
        /// Frame a held gun's grip is aligned to: origin at the palm, +Z along the barrel, +Y up the slide.
        /// </summary>
        public RigidPose GunFrame
        {
            get
            {
                Quaternion rot;
                if (VRConfig.GunAngle.Value == GunAngleMode.Auto && _hasTip)
                {
                    rot = Pointer.rotation;
                }
                else
                {
                    rot = Raw.rotation * Quaternion.Euler(MirrorEuler(VRConfig.ParseVector(VRConfig.GunRotationOffset, new Vector3(35, 0, 0))));
                }
                rot *= Quaternion.Euler(VRConfig.GunPitchTrim.Value, 0f, 0f);
                Vector3 offset = VRConfig.ParseVector(VRConfig.GunPositionOffset, Vector3.zero);
                offset.x *= Mirror;
                return new RigidPose(Palm.position + rot * offset, rot);
            }
        }

        public bool HasTipPose => _hasTip;
        public bool HasGripPose => _hasGrip;

        public Vector3 MirrorEuler(Vector3 euler) => IsLeft ? new Vector3(euler.x, -euler.y, -euler.z) : euler;

        /// <summary>Peak-weighted average velocity over the last ~0.1 s, used for throws.</summary>
        public Vector3 ThrowVelocity()
        {
            float now = Time.unscaledTime;
            Vector3 sum = Vector3.zero;
            Vector3 peak = Vector3.zero;
            int n = 0;
            for (int i = 0; i < HistoryLength; i++)
            {
                if (now - _timeHistory[i] > 0.1f)
                {
                    continue;
                }
                sum += _velHistory[i];
                if (_velHistory[i].sqrMagnitude > peak.sqrMagnitude)
                {
                    peak = _velHistory[i];
                }
                n++;
            }
            if (n == 0)
            {
                return Velocity;
            }
            Vector3 avg = sum / n;
            // Blend toward the peak: releases happen slightly after the fastest point of the swing.
            return Vector3.Lerp(avg, peak, 0.5f);
        }

        public bool GrabPressed => VRInput.GetPressed(DigitalAction.Grab, Side);
        public bool GrabHeld => VRInput.GetHeld(DigitalAction.Grab, Side);
        public bool GrabReleased => VRInput.GetReleased(DigitalAction.Grab, Side);
        public float Trigger => VRInput.GetTrigger(Side);
        public float Grip => VRInput.GetGrip(Side);

        public void Haptic(float duration, float frequency, float amplitude) => VRInput.Haptic(Side, duration, frequency, amplitude);

        public void Destroy()
        {
            Model.Destroy();
            if (Root != null)
            {
                Object.Destroy(Root.gameObject);
            }
        }
    }
}
