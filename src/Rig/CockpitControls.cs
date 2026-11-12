using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Physical cockpit controls for drivers: grip the steering wheel and turn it, or grip the flight stick and
    /// deflect it. The game's own wheel/stick animation follows because it reads the same input vectors.
    /// Uses the vehicle's SteeringWheel / Joystick components, falling back to the seat's hand IK targets.
    /// </summary>
    public sealed class CockpitControls
    {
        private const float StickThrow = 0.08f;
        /// <summary>The hand's tilt for a full throw of the stick: sin(25 degrees).</summary>
        private const float StickTiltFull = 0.4226f;
        private Quaternion _stickStartTurn = Quaternion.identity;
        /// <summary>The stick's centre zone, as a share of its throw (6 mm of 8 cm).</summary>
        private const float StickDeadzone = 0.075f;

        private readonly VRRig _rig;
        private Seat _seat;
        private SteeringWheel _wheel;
        private Transform _stick;

        private readonly bool[] _onWheel = new bool[2];
        private readonly Vector3[] _wheelStart = new Vector3[2];
        private readonly float[] _wheelBase = new float[2];
        /// <summary>How far each hand has turned the rim since taking it (degrees, added up frame by frame: never wraps).</summary>
        private readonly float[] _wheelTurned = new float[2];
        /// <summary>Where each hand on the rim puts the steering this frame.</summary>
        private readonly float[] _handSteer = new float[2];
        /// <summary>How far each hand moved the rim this frame (degrees of steering, before the lock).</summary>
        private readonly float[] _handMove = new float[2];
        private float _steer;

        /// <summary>
        /// The rim, in the wheel's own frame: its centre (on the turning axis, which the wheel's transform sets) and its radius.
        /// The transform's origin is where the wheel turns, not where the rim sits along that axis: the hands on the rim were
        /// measured against the origin before, so a rim set back from it was out of reach for one hand.
        /// </summary>
        private Vector3 _rimLocal;
        private float _rimRadius = DefaultRimRadius;

        /// <summary>A rim of a car or Humvee wheel when its mesh cannot be read: the wheel's origin with this radius.</summary>
        private const float DefaultRimRadius = 0.2f;
        private static readonly List<Vector3> RimPoints = new List<Vector3>(); // MeasureRim's wheel-space vertices, reused
        /// <summary>How far a hand may sit off the rim's plane (along the turning axis) and off the rim itself and still take it.</summary>
        private const float RimPlaneReach = 0.12f;
        private const float RimRadialReach = 0.12f;

        /// <summary>
        /// Each palm in the seat's frame, taken when the palm itself is (at the render, the hands just posed, the seat where it is
        /// drawn): the controls read the hands against the seat from this pair. In Update the palm is still the last render's
        /// while the seat has moved with this frame's physics, and the difference counted as hand motion: at speed a hand held
        /// still pinned the stick at full throw (it read the seat's travel per frame, 0.5 m at 30 m/s against an 8 cm throw), and
        /// the wheel crept through a corner (the car's turn read as the hand's). Bug hunt, 2026-10-22 (the vehicle scout's two
        /// majors of 2026-10-21).
        /// </summary>
        private readonly Vector3[] _palmInSeat = new Vector3[2];
        /// <summary>The palm's turn in the seat's frame, from the same render (the stick's tilt reads it with its place).</summary>
        private readonly Quaternion[] _palmTurnInSeat = { Quaternion.identity, Quaternion.identity };
        private Seat _palmSeat; // the seat the pair was taken in (none yet for a seat just entered: the palm is read as before)

        private Hand? _stickHand;

        // The plane's throttle lever, at the other hand's rest by the seat (the pilot's off hand: where the game puts it on the
        // throttle). Pushed forward for more, pulled back for less and past the line for the airbrake; it stays where let go.
        private Transform _throttle;
        private Hand? _throttleHand;
        private float _throttleAtGrab, _throttleZ0, _throttleShown;
        private const float ThrottleThrow = 0.12f; // metres of hand travel from cruise to full
        private float _throttleClickAt;

        private static float StickThrottle() => Mathf.Clamp(Input.VRGameInput.MoveStick.y, -1f, 1f);
        private Vector3 _stickStartLocal;
        private Vector3 _stickLocal; // the stick's last deflection (seat space), kept over a pause
        private bool _wasPaused;

        public CockpitControls(VRRig rig)
        {
            _rig = rig;
        }

        public void Update()
        {
            Actor actor = GameContext.PlayerActor;
            Seat seat = actor != null && !actor.dead && actor.IsSeated() && actor.seat.IsDriverSeat() ? actor.seat : null;
            if (seat == null)
            {
                // (Also when the seat was destroyed under him: a destroyed seat equals null, so "another seat" never fired and the
                // lever's setting waited for the next plane; review, 2026-11-12.)
                VehicleInput.Throttle = null;
            }
            if (seat != _seat)
            {
                ReleaseAll();
                VehicleInput.Throttle = null; // (another seat: the last plane's lever is not this one's)
                _seat = seat;
                _palmSeat = null; // back in the same seat, its pair was the last ride's (a stale grab offset the stick until let go)
                FindControls();
            }
            if (_seat == null || GameContext.GameMenuOpen)
            {
                ReleaseAll();
                return;
            }
            if ((_onWheel[0] || _onWheel[1]) && _wheel == null || _stickHand != null && _stick == null || _throttleHand != null && _throttle == null)
            {
                ReleaseAll(); // the vehicle was destroyed under the hands
                FindControls();
            }
            // Paused with no menu (the SteamVR dashboard, the headset off): the hands on the wheel and stick move nothing, and
            // carry on from where they are when the match goes on. Before, their motion was added up through the pause and the
            // turn or stick throw landed at once on resume (vehicles hunt, 2026-10-21).
            if (GameManager.IsPaused())
            {
                _wasPaused = true;
                return;
            }
            if (_wasPaused)
            {
                _wasPaused = false;
                foreach (VRHand hand in _rig.Hands)
                {
                    int i = (int)hand.Side;
                    if (_onWheel[i])
                    {
                        _wheelStart[i] = OnWheelPlaneInSeat(PalmInSeat(hand), out _);
                    }
                    if (_stickHand == hand.Side)
                    {
                        _stickStartLocal = PalmInSeat(hand) - _stickLocal;
                    }
                    if (_throttleHand == hand.Side)
                    {
                        _throttleAtGrab = VehicleInput.Throttle ?? StickThrottle();
                        _throttleZ0 = PalmInSeat(hand).z;
                    }
                }
            }
            foreach (VRHand hand in _rig.Hands)
            {
                UpdateHand(hand);
            }
            // Two hands on the rim: the hand moving it this frame steers, and the other is carried along (its angle is moved to
            // the steering, so it adds no pull of its own). Averaging the two let a hand that held still drag the wheel back:
            // turning with one hand while the other rested steered about half as far, and the other hand's own turn never
            // counted. Both hands turning together still steer the same amount.
            if (_onWheel[0] && _onWheel[1])
            {
                int lead = Mathf.Abs(_handMove[0]) >= Mathf.Abs(_handMove[1]) ? 0 : 1;
                int follow = 1 - lead;
                _steer = _handSteer[lead];
                _wheelBase[follow] += _steer - _handSteer[follow];
                _handSteer[follow] = _steer;
            }
            else if (_onWheel[0] || _onWheel[1])
            {
                _steer = _handSteer[_onWheel[0] ? 0 : 1];
            }
            else
            {
                _steer = 0f; // let go: the next grab starts from the centre, not the old turn (the car jerked to it)
            }
            VehicleInput.WheelSteer = _onWheel[0] || _onWheel[1] ? _steer : (float?)null;
        }

        private void FindControls()
        {
            _wheel = null;
            _stick = null;
            _throttle = null;
            if (_seat == null)
            {
                return;
            }
            foreach (SteeringWheel w in _seat.vehicle.GetComponentsInChildren<SteeringWheel>())
            {
                if (w.driverSeat == _seat)
                {
                    _wheel = w;
                    break;
                }
            }
            _rimLocal = Vector3.zero;
            _rimRadius = DefaultRimRadius;
            if (_wheel != null)
            {
                bool measured = MeasureRim(_wheel, out Vector3 rim, out float radius);
                if (measured)
                {
                    _rimLocal = rim;
                    _rimRadius = radius;
                }
                // Once per wheel: where the rim really sits, to read against the hands in the next test.
                Log.InfoOnce("wheel-rim:" + _seat.vehicle.name + ":" + _wheel.name,
                    $"Steering wheel {_wheel.name} of {_seat.vehicle.name}: rim {(measured ? "measured" : "not measured, the origin used")}, "
                    + $"{rim.z:+0.00;-0.00} m along the turning axis and {new Vector2(rim.x, rim.y).magnitude:0.00} m across it from the origin, radius {radius:0.00} m.");
            }
            foreach (Joystick j in _seat.vehicle.GetComponentsInChildren<Joystick>())
            {
                if (j.driverSeat == _seat)
                {
                    _stick = j.transform;
                    break;
                }
            }
            VehicleKind kind = GameContext.SeatKind;
            if (_stick == null && (kind == VehicleKind.Helicopter || kind == VehicleKind.Airplane))
            {
                _stick = VRConfig.LeftHanded ? _seat.handTargetL : _seat.handTargetR;
            }
            if (kind == VehicleKind.Airplane)
            {
                Transform offRest = VRConfig.LeftHanded ? _seat.handTargetR : _seat.handTargetL;
                _throttle = offRest != _stick ? offRest : null;
            }
        }

        /// <summary>How long a hand on the wheel or stick may go untracked before it counts as gone (s).</summary>
        private const float TrackingGrace = 0.2f;

        private readonly float[] _trackedAt = new float[2];

        private void UpdateHand(VRHand hand)
        {
            int i = (int)hand.Side;
            // A hand that loses tracking lets go: the last steering or stick deflection stayed in, and the vehicle kept
            // turning (or the aircraft kept rolling) until the controller came back. After a moment: a flicker of a frame or two dropped the wheel
            // at full lock and straightened the car at once, the grip still shut and no new squeeze taking it back (cockpit scout, 2026-11-07).
            if (hand.Tracked)
            {
                _trackedAt[i] = Time.unscaledTime;
            }
            bool holding = Time.unscaledTime - _trackedAt[i] < TrackingGrace && hand.GrabHeld;
            // Read against the seat from the render that posed it (PalmInSeat): grabs, the stick and the wheel alike.
            Vector3 palm = PalmInSeat(hand);
            // A grip that closed on the way to the rim takes it too (GetHeldUnclaimed), not only a squeeze made at the rim: the
            // second hand usually reaches with its fist already shut, and the edge alone never caught that.
            if (hand.Tracked && VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side) && hand.Role == HandRole.Free)
            {
                if (_wheel != null && NearWheelInSeat(palm))
                {
                    if (!_onWheel[1 - i])
                    {
                        // The first hand on it takes the rim where it is: steered by the thumbstick, the game had turned it, and
                        // the grab began from the centre, the car's steering snapping straight (cockpit scout, 2026-10-22).
                        _steer = Mathf.Clamp(_wheel.turn, -1f, 1f);
                    }
                    _onWheel[i] = true;
                    _wheelStart[i] = OnWheelPlaneInSeat(palm, out _);
                    _wheelBase[i] = _steer;
                    _wheelTurned[i] = 0f;
                    _handSteer[i] = _steer;
                    _handMove[i] = 0f;
                    hand.Role = HandRole.Cockpit;
                    hand.Model.CockpitStick = false;
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    hand.Haptic(0.03f, 120f, 0.4f);
                }
                else if (_stick != null && _stickHand == null && Vector3.Distance(palm, InSeat(_stick.position)) < 0.16f)
                {
                    _stickHand = hand.Side;
                    _stickStartLocal = palm;
                    _stickStartTurn = PalmTurnInSeat(hand);
                    _stickLocal = Vector3.zero; // (a pause before the first move keeps the stick centred, not the last grab's throw)
                    hand.Role = HandRole.Cockpit;
                    hand.Model.CockpitStick = true;
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    hand.Haptic(0.03f, 120f, 0.4f);
                }
                else if (_throttle != null && _throttleHand == null && _stickHand != hand.Side
                         && Vector3.Distance(palm, InSeat(_throttle.position)) < 0.16f)
                {
                    _throttleHand = hand.Side;
                    // (From the throttle as it is: the stick's, if no lever was set; from cruise, a stick held at 0.8 dropped to 0 at the grab.)
                    _throttleAtGrab = VehicleInput.Throttle ?? StickThrottle();
                    _throttleShown = _throttleAtGrab;
                    _throttleZ0 = palm.z;
                    VehicleInput.Throttle = _throttleAtGrab;
                    hand.Role = HandRole.Cockpit;
                    hand.Model.CockpitStick = true;
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    hand.Haptic(0.03f, 120f, 0.4f);
                }
            }

            if (_throttleHand == hand.Side)
            {
                if (!holding)
                {
                    _throttleHand = null; // (let go: the lever stays where it is, VehicleInput.Throttle kept)
                    hand.Role = HandRole.Free;
                }
                else
                {
                    float value = Mathf.Clamp(_throttleAtGrab + (palm.z - _throttleZ0) / ThrottleThrow, -1f, 1f);
                    if (Mathf.Abs(value) < 0.06f)
                    {
                        value = 0f; // (a notch at cruise: found by feel, held without effort)
                    }
                    // A click in the hand at the notch and at the airbrake's line, crossed either way.
                    if ((value == 0f != (_throttleShown == 0f) || value < -0.3f != _throttleShown < -0.3f) && Time.unscaledTime - _throttleClickAt > 0.15f)
                    {
                        _throttleClickAt = Time.unscaledTime; // (spaced: a hand resting on a line clicked with its tremor)
                        hand.Haptic(0.02f, 160f, 0.5f);
                    }
                    _throttleShown = value;
                    VehicleInput.Throttle = value;
                }
            }

            if (_onWheel[i])
            {
                if (!holding)
                {
                    _onWheel[i] = false;
                    _handMove[i] = 0f;
                    hand.Role = HandRole.Free;
                    int other = 1 - i;
                    if (_onWheel[other])
                    {
                        // The other hand carries on from where the wheel is now (it had its own idea while shared).
                        _wheelBase[other] = _steer;
                        _wheelTurned[other] = 0f;
                        _handSteer[other] = _steer;
                    }
                }
                else
                {
                    // Added up from the last frame's hand position: one angle from where the hand took the rim wrapped at
                    // half a turn, and turning past it (hand over hand) threw the steering to full opposite lock.
                    Vector3 now = OnWheelPlaneInSeat(palm, out Vector3 axis);
                    float maxRot = Mathf.Max(5f, _wheel.maxRotation);
                    // Only a step of a third of a degree or more is counted (the smaller ones gather until they are one), and none from a
                    // hand near the hub, where the angle means nothing: a hand resting on the rim walked the steering by its own tremor,
                    // some 6 degrees in five seconds (cockpit scout, 2026-11-11).
                    float rimRadius = _wheel.transform.TransformVector(new Vector3(_rimRadius, 0f, 0f)).magnitude;
                    float step = Vector3.SignedAngle(_wheelStart[i], now, axis);
                    if (now.magnitude < rimRadius * 0.45f)
                    {
                        _wheelStart[i] = now; // (near the hub: the hand's way round is not read, and starts again from here)
                    }
                    else if (Mathf.Abs(step) >= 0.33f)
                    {
                        _wheelTurned[i] = AddTurn(_wheelTurned[i], _wheelStart[i], now, axis, _wheelBase[i], maxRot);
                        _wheelStart[i] = now;
                    }
                    float before = _handSteer[i];
                    _handSteer[i] = Mathf.Clamp(_wheelBase[i] + _wheelTurned[i] / maxRot, -1f, 1f);
                    _handMove[i] = _handSteer[i] - before;
                }
            }

            if (_stickHand == hand.Side)
            {
                if (!holding)
                {
                    _stickHand = null;
                    hand.Role = HandRole.Free;
                    VehicleInput.FlightStick = null;
                }
                else
                {
                    Vector3 local = palm - _stickStartLocal;
                    _stickLocal = local;
                    var throw01 = new Vector2(Mathf.Clamp(local.x / StickThrow, -1f, 1f), Mathf.Clamp(local.z / StickThrow, -1f, 1f));
                    // And the hand's tilt since it took the stick, as a real stick is worked from the wrist (the user, 2026-11-11, planes: "I
                    // need better controlls"): its up leaning forward is nose down, to the right is roll right, 25 degrees a full throw.
                    // Per axis the larger of the two counts (a push forward tilts the hand too: not added twice).
                    Quaternion turn = PalmTurnInSeat(hand) * Quaternion.Inverse(_stickStartTurn);
                    Vector3 up = turn * Vector3.up;
                    var tilt01 = new Vector2(Mathf.Clamp(up.x / StickTiltFull, -1f, 1f), Mathf.Clamp(up.z / StickTiltFull, -1f, 1f));
                    throw01 = new Vector2(Mathf.Abs(tilt01.x) > Mathf.Abs(throw01.x) ? tilt01.x : throw01.x,
                                          Mathf.Abs(tilt01.y) > Mathf.Abs(throw01.y) ? tilt01.y : throw01.y);
                    // A small centre zone (6 mm), rescaled so the input still starts from nothing: the hand's own tremor
                    // (2-3 mm) was a steady 2-4% of stick and the aircraft drifted.
                    VehicleInput.FlightStick = Input.VRGameInput.ApplyDeadzoneSquare(throw01, StickDeadzone);
                }
            }
        }

        /// <summary>After the game's own wheel animation: the rim sits exactly where the hands turned it.</summary>
        public void BeforeRender()
        {
            // The palms against the seat as both are now (VRRig poses the hands just before this): read by the next Update.
            if (_seat != null)
            {
                foreach (VRHand hand in _rig.Hands)
                {
                    _palmInSeat[(int)hand.Side] = InSeat(hand.Palm.position);
                    _palmTurnInSeat[(int)hand.Side] = Runtime.RigidPose.Conjugate(_seat.transform.rotation) * hand.Palm.rotation;
                }
                _palmSeat = _seat;
            }
            if (_wheel == null || !(_onWheel[0] || _onWheel[1]))
            {
                return;
            }
            _wheel.turn = _steer; // the game's wheel animation carries on smoothly from here when let go
            Vector3 euler = _wheel.transform.localEulerAngles;
            euler.z = _steer * _wheel.maxRotation;
            _wheel.transform.localEulerAngles = euler;
        }

        /// <summary>
        /// A point in the seat's own frame (its position taken off, then its turn). Only the turn was taken off before, so the
        /// aircraft's own travel counted as moving the hand: at 30 m/s the 8 cm stick was at full throw within 3 ms.
        /// </summary>
        private Vector3 InSeat(Vector3 world) => InFrame(_seat.transform, world);

        /// <summary>A world point in <paramref name="frame"/>'s position and turn (its scale left out).</summary>
        internal static Vector3 InFrame(Transform frame, Vector3 world) => Runtime.RigidPose.Conjugate(frame.rotation) * (world - frame.position);

        /// <summary>A hand's turn of the rim so far, plus this frame's (from <paramref name="from"/> to <paramref name="to"/>), held at the stops.</summary>
        internal static float AddTurn(float turned, Vector3 from, Vector3 to, Vector3 axis, float steerAtTake, float maxRotation)
        {
            turned += Vector3.SignedAngle(from, to, axis);
            return Mathf.Clamp(turned, (-1f - steerAtTake) * maxRotation, (1f - steerAtTake) * maxRotation);
        }

        /// <summary>
        /// Self-test: a hand held still in a seat that flies 30 m in a frame and turns must give no stick, and a rim turned
        /// 270 degrees clockwise in 10-degree steps (120 degrees to the lock) must stay at full right lock.
        /// </summary>
        internal static string SelfCheck()
        {
            var seat = new GameObject("RavenfieldVR stick check");
            try
            {
                Transform t = seat.transform;
                t.SetPositionAndRotation(new Vector3(100f, 50f, -20f), Quaternion.Euler(5f, 30f, 0f));
                Vector3 hand = t.TransformPoint(new Vector3(0.2f, -0.3f, 0.4f));
                Vector3 start = InFrame(t, hand);
                t.SetPositionAndRotation(t.position + t.forward * 30f, t.rotation * Quaternion.Euler(0f, 15f, 3f));
                hand = t.TransformPoint(new Vector3(0.2f, -0.3f, 0.4f)); // the hand moved with the seat
                float stick = (InFrame(t, hand) - start).magnitude / StickThrow;
                float turned = 0f, steer = 0f;
                for (int a = 10; a <= 270; a += 10)
                {
                    // Clockwise seen from the driver (the wheel faces down -forward): about -forward.
                    Vector3 from = Quaternion.AngleAxis(-(a - 10), Vector3.forward) * Vector3.up;
                    Vector3 to = Quaternion.AngleAxis(-a, Vector3.forward) * Vector3.up;
                    turned = AddTurn(turned, from, to, Vector3.forward, 0f, 120f);
                    steer = Mathf.Clamp(turned / 120f, -1f, 1f);
                }
                // In play the palm comes from the last render and the seat has moved since (the frame's physics): a still hand
                // read against the moved seat (the old pairing) against the pair taken together at the render (PalmInSeat). An
                // aircraft speeding up from 0 to 30 m/s: 0.5 m more travel in this frame than at the grab.
                Vector3 palmAtRender = t.TransformPoint(new Vector3(0.2f, -0.3f, 0.4f));
                Vector3 paired = InFrame(t, palmAtRender);
                t.position += t.forward * 0.5f;
                float stale = (InFrame(t, palmAtRender) - paired).magnitude / StickThrow;
                float pairedThrow = (paired - InFrame(t, t.TransformPoint(new Vector3(0.2f, -0.3f, 0.4f)))).magnitude / StickThrow;
                bool ok = stick < 0.01f && Mathf.Abs(Mathf.Abs(steer) - 1f) < 0.01f && pairedThrow < 0.01f;
                return $"hand still in a moving seat: stick {stick:0.000}; speeding up 30 m/s, the palm read with the seat of its render: stick "
                       + $"{pairedThrow:0.000} (with the moved seat it read {stale:0.0} of full throw); rim turned 270 deg: steering {steer:+0.00;-0.00}"
                       + (ok ? "" : " (NOT RIGHT)");
            }
            finally
            {
                Object.Destroy(seat);
            }
        }

        /// <summary>A hand near the wheel's rim (within <paramref name="margin"/> more than the rim takes): the weapons' holster
        /// grab yields to it, so a hand reaching for the rim does not draw the pistol at the hip beside it.</summary>
        public bool WheelReaches(Vector3 palm, float margin) => _seat != null && _wheel != null && WithinRim(palm, margin);

        /// <summary>The hand's palm in the seat's frame, from the render that posed it (see <see cref="_palmInSeat"/>).</summary>
        private Vector3 PalmInSeat(VRHand hand) => _palmSeat == _seat ? _palmInSeat[(int)hand.Side] : InSeat(hand.Palm.position);

        /// <summary>The palm's turn in the seat's frame from that same render (in a banking aircraft a degree a frame off before; scout).</summary>
        private Quaternion PalmTurnInSeat(VRHand hand) =>
            _palmSeat == _seat ? _palmTurnInSeat[(int)hand.Side] : Runtime.RigidPose.Conjugate(_seat.transform.rotation) * hand.Palm.rotation;

        /// <summary>The rim's centre and turning axis in the seat's frame (fixed to the vehicle as the seat is: read now).</summary>
        private void WheelInSeat(out Vector3 centre, out Vector3 axis)
        {
            Transform wt = _wheel.transform;
            centre = InSeat(wt.TransformPoint(_rimLocal));
            axis = Runtime.RigidPose.Conjugate(_seat.transform.rotation) * wt.forward;
        }

        /// <summary><see cref="WithinRim"/> for a palm in the seat's frame.</summary>
        private bool NearWheelInSeat(Vector3 palmInSeat)
        {
            WheelInSeat(out Vector3 centre, out Vector3 axis);
            Vector3 rel = palmInSeat - centre;
            float radius = _wheel.transform.TransformVector(new Vector3(_rimRadius, 0f, 0f)).magnitude;
            return Mathf.Abs(Vector3.Dot(rel, axis)) < RimPlaneReach && Vector3.ProjectOnPlane(rel, axis).magnitude < radius + RimRadialReach;
        }

        /// <summary>The palm's place on the rim's plane, from the rim's centre (the seat's frame; the turn is read about <paramref name="axis"/>).</summary>
        private Vector3 OnWheelPlaneInSeat(Vector3 palmInSeat, out Vector3 axis)
        {
            WheelInSeat(out Vector3 centre, out axis);
            Vector3 v = Vector3.ProjectOnPlane(palmInSeat - centre, axis);
            return v.sqrMagnitude > 1e-6f ? v : Runtime.RigidPose.Conjugate(_seat.transform.rotation) * _wheel.transform.up * 0.15f;
        }

        /// <summary>
        /// Within reach of the rim: near its plane along the turning axis and within its radius (plus a little) of the rim's
        /// centre, not of the transform's origin.
        /// </summary>
        private bool WithinRim(Vector3 palm, float margin)
        {
            Transform wt = _wheel.transform;
            Vector3 centre = wt.TransformPoint(_rimLocal);
            Vector3 rel = palm - centre;
            float axial = Mathf.Abs(Vector3.Dot(rel, wt.forward));
            float radial = Vector3.ProjectOnPlane(rel, wt.forward).magnitude;
            float radius = wt.TransformVector(new Vector3(_rimRadius, 0f, 0f)).magnitude;
            return axial < RimPlaneReach + margin && radial < radius + RimRadialReach + margin;
        }

        /// <summary>
        /// Where the rim is, from the wheel's own mesh: the outer ring of its vertices gives the rim's plane along the turning
        /// axis, its centre across the axis and its radius. Returns false (the origin and <see cref="DefaultRimRadius"/> stay) when
        /// the mesh cannot be read or the ring is not a sane rim.
        /// </summary>
        private static bool MeasureRim(SteeringWheel wheel, out Vector3 centre, out float radius)
        {
            centre = Vector3.zero;
            radius = DefaultRimRadius;
            Transform wt = wheel.transform;
            // Every vertex in the wheel's space, read once: the mesh's vertices getter copies the whole array, and the three
            // passes below each read it again (seat-entry garbage; rig scout, 2026-10-23).
            List<Vector3> points = RimPoints;
            points.Clear();
            float outer = 0f;
            foreach (MeshFilter mf in wt.GetComponentsInChildren<MeshFilter>(false))
            {
                if (mf.sharedMesh == null || !mf.sharedMesh.isReadable)
                {
                    continue;
                }
                Matrix4x4 toWheel = wt.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                foreach (Vector3 v in mf.sharedMesh.vertices)
                {
                    Vector3 p = toWheel.MultiplyPoint3x4(v);
                    outer = Mathf.Max(outer, new Vector2(p.x, p.y).magnitude);
                    points.Add(p);
                }
            }
            if (points.Count < 12 || outer < 0.1f)
            {
                return false;
            }
            // The rim is the outermost ring: the vertices within 6% of the outer radius, averaged.
            Vector3 sum = Vector3.zero;
            int ring = 0;
            foreach (Vector3 p in points)
            {
                if (new Vector2(p.x, p.y).magnitude >= outer * 0.94f)
                {
                    sum += p;
                    ring++;
                }
            }
            if (ring < 8)
            {
                return false;
            }
            Vector3 mean = sum / ring;
            float spread = 0f;
            foreach (Vector3 p in points)
            {
                if (new Vector2(p.x, p.y).magnitude >= outer * 0.94f)
                {
                    spread += new Vector2(p.x - mean.x, p.y - mean.y).magnitude;
                }
            }
            points.Clear();
            float r = spread / ring;
            // Sane for a car or Humvee wheel: a rim of 10-30 cm, centred near the transform across the axis.
            if (r < 0.10f || r > 0.30f || new Vector2(mean.x, mean.y).magnitude > 0.15f || Mathf.Abs(mean.z) > 0.5f)
            {
                return false;
            }
            centre = new Vector3(mean.x, mean.y, mean.z);
            radius = r;
            return true;
        }

        private void ReleaseAll()
        {
            for (int i = 0; i < 2; i++)
            {
                if (_onWheel[i] || _stickHand == (Hand)i)
                {
                    VRHand h = _rig.Hands[i];
                    if (h.Role == HandRole.Cockpit)
                    {
                        h.Role = HandRole.Free;
                    }
                }
                _onWheel[i] = false;
            }
            if (_throttleHand.HasValue)
            {
                VRHand h = _rig.Hands[(int)_throttleHand.Value];
                if (h.Role == HandRole.Cockpit)
                {
                    h.Role = HandRole.Free;
                }
            }
            _throttleHand = null; // (the lever's setting stays: a menu or a pause does not move it)
            _stickHand = null;
            _steer = 0f;
            VehicleInput.WheelSteer = null;
            VehicleInput.FlightStick = null;
        }
    }
}
