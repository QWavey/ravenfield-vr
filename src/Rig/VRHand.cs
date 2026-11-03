using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>What a hand is currently doing; drives the hand pose and arbitrates grabs.</summary>
    public enum HandRole { Free, Weapon, Foregrip, Slide, Magazine, Throwable, Pointer, Cockpit, Holster, Hinge, Pin, Belt, Optic, Magnifier, Body }

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

        /// <summary>
        /// The controller's own pointer, never slowed (Hands in slow motion: Slowed): what is input rather than the body, the menu
        /// laser and the walking direction, follows the controller at once. <see cref="Pointer"/> otherwise.
        /// </summary>
        public RigidPose TrackedPointer { get; private set; }

        /// <summary>
        /// World-space linear velocity including the rig's own motion: the hand's motion in the world (throws, a carried body, a push),
        /// in game units for slowed hands (Hands in slow motion: Slowed).
        /// </summary>
        public Vector3 Velocity { get; private set; }

        /// <summary>
        /// The hand's velocity against the player's body (the rig carried by walking, jumping, a vehicle left out): what a
        /// held weapon's weight lags behind (a jump or a landing pitched a one-handed heavy gun; 85-agent regression guard). The
        /// controller's own, in real time, also for slowed hands: the gestures read it (a swing, a slap, a reach).
        /// </summary>
        public Vector3 BodyRelativeVelocity { get; private set; }
        public Vector3 AngularVelocity { get; private set; }

        /// <summary>The controller's own spin, without the rig's turn: for wrist gestures (a smooth turn is no flick). Real time, also for slowed hands.</summary>
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
        /// <summary>The frame before was taken as a jump of the rig (its measure not kept as the one to compare with).</summary>
        private bool _lastRigJumped;
        private Matrix4x4 _lastRigMatrix = Matrix4x4.identity;
        private bool _hasLastRig;
        private RigidPose _lastRawLocal;
        private bool _hasLastRaw;

        // The slowed hand (Hands in slow motion: Slowed): where the hand is drawn and acts, in tracking space, its velocity and spin
        // in game units, and whether it is following (it is not at full speed once it has caught up).
        private RigidPose _slowPose;
        private Vector3 _slowVelocity, _slowSpin;
        // Catching up at full speed (CatchUp): the slowed hand's offset from the controller (tracking space) and its turn against
        // it (the controller's own frame), settling to nothing on the slowed hand's spring, with the offset's velocity.
        private bool _catching;
        private Vector3 _catchOff, _catchVelocity;
        private Quaternion _catchTurn = Quaternion.identity;
        /// <summary>The controller's place last frame (tracking space): a jump of it, not of the slowed hand, is a recentring.</summary>
        private Vector3 _slowTarget;
        private bool _slowActive;

        /// <summary>
        /// A slowed hand's spring (1/s of game time: a 50 ms lag at full speed, a quarter second at the game's 0.2), top speed (m/s,
        /// game time) and top turn (rad/s): a body living in the slowed world, which moves no faster in it than a real arm does in
        /// ours. At the game's slow motion a hand swung at 5 m/s goes at 2: it trails the controller and catches up when it stops.
        /// </summary>
        private const float SlowSpring = 20f, SlowTopSpeed = 10f, SlowTopTurn = 30f;

        /// <summary>How far (m) a slowed hand may trail its controller: further, it is dragged along at this distance.</summary>
        private const float SlowLeash = 1.5f;

        /// <summary>
        /// One step (<paramref name="step"/> s of game time) of a slowed hand toward <paramref name="target"/>: a critically damped
        /// spring (the target taken as still for the step), never faster than <see cref="SlowTopSpeed"/>. The velocity carried on is
        /// the spring's at the end of the step, not the step's average (fed back, the average overshot the controller by 4%; the
        /// offline tests caught it). Pure: the offline tests check it converges without overshoot and keeps to the top speed.
        /// </summary>
        internal static Vector3 SpringStep(Vector3 position, ref Vector3 velocity, Vector3 target, float step)
        {
            if (step <= 1e-6f)
            {
                return position;
            }
            float decay = Mathf.Exp(-SlowSpring * step);
            Vector3 off = position - target;
            Vector3 lead = (velocity + SlowSpring * off) * step;
            Vector3 moved = target + (off + lead) * decay - position;
            float top = SlowTopSpeed * step;
            if (moved.sqrMagnitude > top * top)
            {
                moved = moved.normalized * top; // at the top speed: the arm's limit, not the spring
                velocity = moved / step;
            }
            else
            {
                velocity = Vector3.ClampMagnitude((velocity - SlowSpring * lead) * decay, SlowTopSpeed);
            }
            return position + moved;
        }

        /// <summary>
        /// Whether the hand follows the controller slowed (Slowed, in slow motion, or still catching up after it): the controller's
        /// pose <paramref name="local"/> becomes the slowed hand's, and its velocity and spin (tracking space, game units) are given.
        /// Not in a pause (time scale 0: the menus and the laser at full speed). A sustained fast swing may leave it well behind: it
        /// keeps following, dragged along at <see cref="SlowLeash"/> (it snapped to the controller at 1.5 m before, a jump of the hand
        /// and the gun; round 11); only a jump of the controller itself (tracking recentred) starts it afresh where the controller is.
        /// </summary>
        private bool Slowed(ref RigidPose local, out Vector3 velocity, out Vector3 spin)
        {
            velocity = spin = Vector3.zero;
            // (Switched to Normal mid slow motion: caught up at full speed, as when slow motion ends, not snapped to the controller.)
            float s = Time.timeScale > 0f && !VRConfig.SlowedHands ? 1f : Time.timeScale, dt = Time.unscaledDeltaTime;
            // (Catching up: by the offset itself. Against the controller as it is now, a hand moving 11 mm a frame never came within 2 mm.)
            bool caughtUp = _catching
                ? _catchOff.sqrMagnitude < 0.002f * 0.002f && Quaternion.Angle(_catchTurn, Quaternion.identity) < 0.5f
                : (_slowPose.position - local.position).sqrMagnitude < 0.002f * 0.002f && Quaternion.Angle(_slowPose.rotation, local.rotation) < 0.5f;
            bool jumped = _slowActive && (local.position - _slowTarget).sqrMagnitude > 0.5f * 0.5f;
            _slowTarget = local.position;
            if (s <= 0f || s >= 0.999f && (!_slowActive || caughtUp) || dt <= 0f || jumped)
            {
                _slowActive = _catching = false;
                return false;
            }
            // (A long frame, a hitch or a load, is taken as a tenth of a second: it dropped the hand to the controller before.)
            dt = Mathf.Min(dt, 0.1f);
            if (s >= 0.999f)
            {
                CatchUp(ref local, dt, out velocity, out spin);
                return true;
            }
            if (!_slowActive || _catching)
            {
                // From where the hand is drawn: no jump as slow motion starts (or starts again while catching up).
                _slowPose = _slowActive ? _slowPose : local;
                _slowActive = true;
                _catching = false;
                _slowVelocity = _slowSpin = Vector3.zero;
            }
            float step = dt * Mathf.Min(s, 1f); // the frame in game time
            float decay = Mathf.Exp(-SlowSpring * step);
            _slowPose.position = SpringStep(_slowPose.position, ref _slowVelocity, local.position, step);
            // On a leash of 1.5 m: a swing held past the top speed drags the hand along at that distance, continuously (the trail had
            // no bound, or snapped to the controller; round 11), its velocity taking the pull.
            Vector3 trail = _slowPose.position - local.position;
            if (trail.sqrMagnitude > SlowLeash * SlowLeash)
            {
                Vector3 leashed = local.position + trail.normalized * SlowLeash;
                if (step > 1e-6f)
                {
                    _slowVelocity += (leashed - _slowPose.position) / step;
                }
                _slowPose.position = leashed;
            }
            Quaternion before = _slowPose.rotation;
            float angle = Quaternion.Angle(before, local.rotation);
            _slowPose.rotation = Quaternion.RotateTowards(before, local.rotation, Mathf.Min(angle * (1f - decay), SlowTopTurn * Mathf.Rad2Deg * step));
            (_slowPose.rotation * Runtime.RigidPose.Conjugate(before)).ToAngleAxis(out float turned, out Vector3 axis);
            if (turned > 180f)
            {
                turned -= 360f;
            }
            _slowSpin = step > 1e-6f && Mathf.Abs(turned) > 1e-4f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x) ? axis * (turned * Mathf.Deg2Rad / step) : Vector3.zero;
            local = _slowPose;
            velocity = _slowVelocity;
            spin = _slowSpin;
            return true;
        }

        /// <summary>
        /// The slowed hand catching up at full speed (slow motion over, or the hands switched to Normal in it): its offset from the
        /// controller settles on the same spring in the controller's own frame (<see cref="CatchStep"/>), so it closes whatever the
        /// hand does. Followed in the world as in slow motion, the spring trailed a moving hand by 2v/20 (10 cm at 1 m/s, 30 at 3) and
        /// let go only once the hand was held still: the slow motion stayed on the hands a little after it went off (user,
        /// 2026-11-03). Its velocity and spin: the drawn hand's own over the frame, the controller's and the offset's together.
        /// </summary>
        private void CatchUp(ref RigidPose local, float dt, out Vector3 velocity, out Vector3 spin)
        {
            if (!_catching)
            {
                // From rest against the controller: the hand takes the controller's pace at once, as the world takes its own.
                _catching = true;
                _catchOff = _slowPose.position - local.position;
                _catchTurn = Runtime.RigidPose.Conjugate(local.rotation) * _slowPose.rotation;
                _catchVelocity = Vector3.zero;
            }
            CatchStep(ref _catchOff, ref _catchVelocity, ref _catchTurn, dt);
            RigidPose was = _slowPose;
            _slowPose = new RigidPose(local.position + _catchOff, local.rotation * _catchTurn);
            velocity = (_slowPose.position - was.position) / dt;
            (_slowPose.rotation * Runtime.RigidPose.Conjugate(was.rotation)).ToAngleAxis(out float turned, out Vector3 axis);
            if (turned > 180f)
            {
                turned -= 360f;
            }
            spin = Mathf.Abs(turned) > 1e-4f && !float.IsNaN(axis.x) && !float.IsInfinity(axis.x) ? axis * (turned * Mathf.Deg2Rad / dt) : Vector3.zero;
            _slowVelocity = velocity;
            _slowSpin = spin;
            local = _slowPose;
        }

        /// <summary>
        /// One step (<paramref name="step"/> s) of a catching-up hand's offset from its controller: the position offset on the slowed
        /// hand's spring toward none, the turn eased toward none as the slowed hand's is. The controller's motion is not in it, so
        /// it settles while the hand moves. Pure: the offline tests check it closes a slow-motion trail within half a second.
        /// </summary>
        internal static void CatchStep(ref Vector3 off, ref Vector3 velocity, ref Quaternion turn, float step)
        {
            off = SpringStep(off, ref velocity, Vector3.zero, step);
            float angle = Quaternion.Angle(turn, Quaternion.identity);
            if (angle > 1e-3f)
            {
                turn = Quaternion.RotateTowards(turn, Quaternion.identity, Mathf.Min(angle * (1f - Mathf.Exp(-SlowSpring * step)), SlowTopTurn * Mathf.Rad2Deg * step));
            }
        }

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

            // Slowed with the world (Hands in slow motion: Slowed): the hand's pose from here on is the slowed one, the controller's
            // tip and grip carried on it as they sit on the controller.
            RigidPose tracked = rawLocal;
            bool slowed = Slowed(ref rawLocal, out Vector3 slowVelocity, out Vector3 slowSpin);
            RigidPose rig = RigidPose.Of(rigRoot);
            Raw = rig.Multiply(rawLocal);
            Root.SetPositionAndRotation(Raw.position, Raw.rotation);

            HandPose tip = raw.valid ? VRInput.GetPose(Side, PoseKind.Tip) : default;
            if (tip.valid && slowed)
            {
                _tipOnRaw = tracked.Inverse().Multiply(tip.pose);
                _tipOnRawKnown = true;
                Pointer = Raw.Multiply(_tipOnRaw);
            }
            else if (tip.valid)
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
            TrackedPointer = slowed ? rig.Multiply(tracked).Multiply(Raw.Inverse().Multiply(Pointer)) : Pointer;

            HandPose grip = raw.valid ? VRInput.GetPose(Side, PoseKind.Grip) : default;
            Vector3 palmPos;
            if (grip.valid && slowed)
            {
                _gripOnRaw = new RigidPose(tracked.InverseTransformPoint(grip.pose.position), Quaternion.identity);
                _gripOnRawKnown = true;
                palmPos = Raw.TransformPoint(_gripOnRaw.position);
            }
            else if (grip.valid)
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
                // The jump is not kept as the one to compare with (kept, the frame after it read as a jump back and was dropped too:
                // after a snap turn in a car at 25 m/s, two frames without the car's motion; round-4 scout): the frame before it
                // stays, for one frame. A second change as large in a row is the rig's new motion, kept from then on.
                // (The second is kept, not zeroed and then kept: two frames of a steady new motion were lost; round-6 scout.)
                Vector3 measured = rigVelocity;
                if ((measured - _lastRigMeasured).sqrMagnitude > 400f && !_lastRigJumped)
                {
                    rigVelocity = Vector3.zero;
                    rigSpin = Vector3.zero;
                    _lastRigJumped = true;
                }
                else
                {
                    _lastRigMeasured = measured;
                    _lastRigJumped = false;
                }
            }
            else
            {
                _lastRigMeasured = Vector3.zero;
                _lastRigJumped = false;
            }
            _lastRigRotation = steadyRotation;
            _lastRigMatrix = steadyRoot;
            _hasLastRig = true;

            Vector3 own = raw.valid ? rig.rotation * raw.velocity : Scripted[(int)Side].HasValue ? ScriptedVelocity[(int)Side] : Vector3.zero;
            Vector3 ownSpin = raw.valid ? rig.rotation * raw.angularVelocity : Vector3.zero;
            if (TestVelocityNoise > 0f)
            {
                own += Random.insideUnitSphere * TestVelocityNoise;
            }
            // The hand's motion in the world: the controller's, or the slowed hand's.
            Vector3 moving = own, spinning = ownSpin;
            if (slowed)
            {
                // In game units, as everything in the slowed world: the slowed hand's own, and the rig's (measured over real time) over
                // the time scale. What it lets go of then moves at the world's pace with no real-time flight (RealTimeFlight).
                // (Catching up after a switch to Normal: real-time units, as Normal hands' are; the follower then ran at full speed.)
                float pace = VRConfig.SlowedHands ? 1f / Mathf.Clamp(Time.timeScale, 0.05f, 1f) : 1f;
                moving = rig.rotation * slowVelocity;
                spinning = rig.rotation * slowSpin;
                rigVelocity *= pace;
                rigSpin *= pace;
            }
            // The gestures' (a flick, a slap, a swing, a punch, a reach) are the controller's own, in real time, slowed hands or
            // not: what the player does. In game units (the slowed hand's) every one of them fired at a fifth of its speed (round 11).
            BodyRelativeVelocity = own;
            OwnAngularVelocity = ownSpin;
            Velocity = moving + rigVelocity;
            AngularVelocity = spinning + rigSpin;
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
