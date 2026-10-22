using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>What a hand is currently doing; drives the hand pose and arbitrates grabs.</summary>
    public enum HandRole { Free, Weapon, Foregrip, Slide, Magazine, Throwable, Pointer, Cockpit, Holster, Hinge, Pin, Belt, Optic, Magnifier }

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

        /// <summary>
        /// The hand has a usable pose: tracked, or held at its last tracked pose (moving with the body) while the
        /// controller is out of view, so a held gun stays put instead of snapping to the flat-screen viewmodel.
        /// </summary>
        public bool HasPose { get; private set; }
        public RigidPose Raw { get; private set; }
        public RigidPose Palm { get; private set; }
        public RigidPose Pointer { get; private set; }

        /// <summary>World-space linear velocity including the rig's own motion.</summary>
        public Vector3 Velocity { get; private set; }

        /// <summary>
        /// The hand's velocity against the player's body (the rig carried by walking, jumping, a vehicle left out): what a
        /// held weapon's weight lags behind (a jump or a landing pitched a one-handed heavy gun; 85-agent regression guard).
        /// </summary>
        public Vector3 BodyRelativeVelocity { get; private set; }
        public Vector3 AngularVelocity { get; private set; }

        /// <summary>The controller's own spin, without the rig's turn: for wrist gestures (a smooth turn is no flick).</summary>
        public Vector3 OwnAngularVelocity { get; private set; }

        public HandRole Role = HandRole.Free;

        /// <summary>Opaque reference to the object held in the current role (magazine prop, grabbed control…).</summary>
        public object Held;

        private const int HistoryLength = 10;
        private readonly Vector3[] _velHistory = new Vector3[HistoryLength];
        /// <summary>The same, without the player's own movement (walking, riding, the rig being carried).</summary>
        private readonly Vector3[] _bodyVelHistory = new Vector3[HistoryLength];
        private readonly float[] _timeHistory = new float[HistoryLength];
        private int _historyIndex;
        private bool _hasTip, _hasGrip;

        // Where the controller's tip and grip poses sit on it, from the last frame they were tracked: with tracking lost they keep
        // that place (the stand-in offsets differ from them, so a held gun stepped as tracking went and again as it came back;
        // rig hunt, 2026-10-21).
        private RigidPose _tipOnRaw, _gripOnRaw;
        private bool _tipOnRawKnown, _gripOnRawKnown;
        private Quaternion _lastRigRotation = Quaternion.identity;
        private Vector3 _lastRigMeasured;
        private Matrix4x4 _lastRigMatrix = Matrix4x4.identity;
        private bool _hasLastRig;
        private RigidPose _lastRawLocal;
        private bool _hasLastRaw;

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
            RigidPose rawLocal;
            if (raw.valid)
            {
                rawLocal = raw.pose;
                _lastRawLocal = rawLocal;
                _hasLastRaw = true;
                Tracked = true;
            }
            else if (AutoTest.Requested && SimulatedLocal(rigRoot, out rawLocal))
            {
                // Automated runs on the virtual headset have no controllers: hold the hands where a player would, so
                // the whole held-weapon path (and what the mirror shows) is the real VR one.
                Tracked = true;
            }
            else if (_hasLastRaw)
            {
                rawLocal = _lastRawLocal;
                Tracked = false;
            }
            else
            {
                Tracked = false;
                HasPose = false;
                Model.SetVisible(false);
                return;
            }
            HasPose = true;

            RigidPose rig = RigidPose.Of(rigRoot);
            Raw = rig.Multiply(rawLocal);
            Root.SetPositionAndRotation(Raw.position, Raw.rotation);

            HandPose tip = raw.valid ? VRInput.GetPose(Side, PoseKind.Tip) : default;
            if (tip.valid)
            {
                Pointer = rig.Multiply(tip.pose);
                _tipOnRaw = Raw.Inverse().Multiply(Pointer);
                _tipOnRawKnown = true;
            }
            else
            {
                Pointer = _tipOnRawKnown
                    ? Raw.Multiply(_tipOnRaw)
                    : Raw.Multiply(new RigidPose(Vector3.zero, Quaternion.Euler(MirrorEuler(VRConfig.ParseVector(VRConfig.LaserRotationOffset, new Vector3(40, 0, 0))))));
            }
            _hasTip = tip.valid || _tipOnRawKnown;

            HandPose grip = raw.valid ? VRInput.GetPose(Side, PoseKind.Grip) : default;
            Vector3 palmPos;
            if (grip.valid)
            {
                palmPos = rig.TransformPoint(grip.pose.position);
                _gripOnRaw = new RigidPose(Raw.InverseTransformPoint(palmPos), Quaternion.identity);
                _gripOnRawKnown = true;
            }
            else
            {
                palmPos = Raw.TransformPoint(_gripOnRawKnown ? _gripOnRaw.position : new Vector3(0f, -0.025f, -0.055f));
            }
            _hasGrip = grip.valid || _gripOnRawKnown;
            // The palm frame uses the pointer orientation: forward = where the hand points, up = back of the fist's top.
            Palm = new RigidPose(palmPos, Pointer.rotation);

            Vector3 rigVelocity = Vector3.zero, rigSpin = Vector3.zero;
            Matrix4x4 steadyRoot = VRRig.SteadyRootOf(rigRoot, out Quaternion steadyRotation);
            if (_hasLastRig && Time.unscaledDeltaTime > 1e-4f)
            {
                // The rig's own motion where the hand is: its move plus its turn about wherever it turned (a turn goes about the
                // head). The rig origin's own motion stood in for it, and the origin is where the play area's centre is: in the
                // user's log 1.6 m from where they stand, so a smooth turn of 120 degrees a second moved it 3.4 m/s while the hands
                // moved about 1, and sideways; a magazine or a gun let go while turning flew off with it, and a small snap turn
                // gave a one-frame jump of up to some 40 m/s (2026-10-20, task 315). A snap turn is a jump, not motion.
                // Without the view shake: a shot's jolt turns the rig about the eyes for a few frames, which is no motion of the hand.
                // (A snap turn, flagged by the rig, or a turn no smooth turn makes in a frame: a jump.)
                if (VRRig.LastSnapFrame != Time.frameCount && Quaternion.Angle(_lastRigRotation, steadyRotation) <= 45f)
                {
                    Vector3 local = rigRoot.InverseTransformPoint(palmPos);
                    rigVelocity = (steadyRoot.MultiplyPoint3x4(local) - _lastRigMatrix.MultiplyPoint3x4(local)) / Time.unscaledDeltaTime;
                    // And its turn: what is let go while turning keeps turning with it (the controller's own spin left it out).
                    (steadyRotation * Runtime.RigidPose.Conjugate(_lastRigRotation)).ToAngleAxis(out float turned, out Vector3 turnAxis);
                    if (turned > 180f)
                    {
                        turned -= 360f;
                    }
                    if (Mathf.Abs(turned) > 1e-3f && !float.IsNaN(turnAxis.x) && !float.IsInfinity(turnAxis.x))
                    {
                        rigSpin = turnAxis * (turned * Mathf.Deg2Rad / Time.unscaledDeltaTime);
                    }
                }
                // A jump of the rig (a teleport, a respawn without a mode change) is no motion: told by a change of over 20 m/s from
                // the frame before, not by the speed, which a vehicle reaches (a 15 m/s cap dropped a fast car's or a jet's speed
                // from what was let go inside it; review, 2026-10-20). Half a throw's peak would take a jump in.
                Vector3 measured = rigVelocity;
                if ((measured - _lastRigMeasured).sqrMagnitude > 400f)
                {
                    rigVelocity = Vector3.zero;
                    rigSpin = Vector3.zero;
                }
                _lastRigMeasured = measured;
            }
            else
            {
                _lastRigMeasured = Vector3.zero;
            }
            _lastRigRotation = steadyRotation;
            _lastRigMatrix = steadyRoot;
            _hasLastRig = true;

            Vector3 own = raw.valid ? rig.rotation * raw.velocity : Scripted[(int)Side].HasValue ? ScriptedVelocity[(int)Side] : Vector3.zero;
            if (TestVelocityNoise > 0f)
            {
                own += Random.insideUnitSphere * TestVelocityNoise;
            }
            BodyRelativeVelocity = own;
            Velocity = own + rigVelocity;
            OwnAngularVelocity = raw.valid ? rig.rotation * raw.angularVelocity : Vector3.zero;
            AngularVelocity = OwnAngularVelocity + rigSpin;
            _velHistory[_historyIndex] = Velocity;
            _bodyVelHistory[_historyIndex] = Velocity - rigVelocity;
            _timeHistory[_historyIndex] = Time.unscaledTime;
            _historyIndex = (_historyIndex + 1) % HistoryLength;
        }

        /// <summary>Autotest: a scripted world pose (and velocity) for a simulated hand, instead of where it rests.</summary>
        internal static readonly RigidPose?[] Scripted = new RigidPose?[2];
        internal static readonly Vector3[] ScriptedVelocity = new Vector3[2];

        /// <summary>
        /// Self-test: random noise added to every hand's reported velocity (m/s), as a real controller's tracking has; the
        /// null driver's hands report none, and a weapon shaking from it went unseen (Tasks 203).
        /// </summary>
        internal static float TestVelocityNoise;

        /// <summary>
        /// Autotest hands, in rig space: the gun hand low and forward of the head, the other hand further out where a
        /// handguard would be, both pointing where the head faces.
        /// </summary>
        private bool SimulatedLocal(Transform rigRoot, out RigidPose local)
        {
            local = default;
            if (Scripted[(int)Side].HasValue)
            {
                local = RigidPose.Of(rigRoot).Inverse().Multiply(Scripted[(int)Side].Value);
                return true;
            }
            VRRig rig = VRRig.Instance;
            if (rig == null || rig.Camera == null)
            {
                return false;
            }
            Transform head = rig.Head;
            Vector3 headLocal = rigRoot.InverseTransformPoint(head.position);
            float yaw = (Runtime.RigidPose.Conjugate(rigRoot.rotation) * head.rotation).eulerAngles.y;
            Quaternion facing = Quaternion.Euler(0f, yaw, 0f);
            bool gunHand = Side == VRInput.GunHand;
            var offset = gunHand ? new Vector3(0.16f, -0.26f, 0.40f) : new Vector3(0.08f, -0.29f, 0.62f);
            offset.x *= Side == Hand.Right ? 1f : -1f; // each hand on its own side of the body
            Quaternion pointer = facing * Quaternion.Euler(gunHand ? 4f : 10f, 0f, 0f);
            Quaternion laser = Quaternion.Euler(MirrorEuler(VRConfig.ParseVector(VRConfig.LaserRotationOffset, new Vector3(40, 0, 0))));
            local = new RigidPose(headLocal + facing * offset, pointer * Runtime.RigidPose.Conjugate(laser));
            return true;
        }

        /// <summary>
        /// The hollow of the closed fist in the hand model's palm space (right hand; origin = palm centre, palm facing -X):
        /// where a gripped handle runs through the curled fingers.
        /// </summary>
        public static readonly Vector3 FistHollow = new Vector3(-0.026f, 0f, 0.034f);

        /// <summary>
        /// Middle of the hand. <see cref="Palm"/> is the controller's grip pose, the middle of its handle, so it lies inside
        /// the real fist; the palm itself sits beside it, where the hand model puts its palm and a gun its grip hand.
        /// </summary>
        public Vector3 PalmCentre => Palm.TransformPoint(new Vector3(-FistHollow.x * Mirror, -FistHollow.y, -FistHollow.z));

        /// <summary>
        /// Frame a held gun's grip is aligned to: origin at the palm centre (where the weapon's own hand holds its grip),
        /// +Z along the barrel, +Y up the slide.
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
                return new RigidPose(PalmCentre + rot * offset, rot);
            }
        }

        public bool HasTipPose => _hasTip;
        public bool HasGripPose => _hasGrip;

        public Vector3 MirrorEuler(Vector3 euler) => IsLeft ? new Vector3(euler.x, -euler.y, -euler.z) : euler;

        /// <summary>Peak-weighted average velocity over the last ~0.1 s, used for throws.</summary>
        public Vector3 ThrowVelocity() => WeightedVelocity(_velHistory);

        /// <summary>
        /// Forgets the hand's recent motion (getting into or out of a seat): a weapon let go just after leaving a moving car
        /// flew off with the car's speed.
        /// </summary>
        public void ForgetMotion()
        {
            for (int i = 0; i < HistoryLength; i++)
            {
                _velHistory[i] = Vector3.zero;
                _bodyVelHistory[i] = Vector3.zero;
                _timeHistory[i] = float.NegativeInfinity;
            }
            _hasLastRig = false;
        }

        /// <summary>
        /// The same against the player's own body: for deciding what a motion was (a gentle placing or a throw), which must
        /// not change because the player is running. What leaves the hand still takes <see cref="ThrowVelocity"/>.
        /// </summary>
        public Vector3 BodyThrowVelocity() => WeightedVelocity(_bodyVelHistory);

        private Vector3 WeightedVelocity(Vector3[] history)
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
                sum += history[i];
                if (history[i].sqrMagnitude > peak.sqrMagnitude)
                {
                    peak = history[i];
                }
                n++;
            }
            if (n == 0)
            {
                return history[(_historyIndex + HistoryLength - 1) % HistoryLength];
            }
            Vector3 avg = sum / n;
            // Blend toward the peak: releases happen slightly after the fastest point of the swing.
            return Vector3.Lerp(avg, peak, 0.5f);
        }

        public bool GrabPressed => VRInput.GetPressed(DigitalAction.Grab, Side);
        /// <summary>The grip closed in the last quarter second and nothing took it yet (a forgiving grab).</summary>
        public bool GrabReaching => VRInput.GetPressedRecently(DigitalAction.Grab, Side, 0.25f);
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
