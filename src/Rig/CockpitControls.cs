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
        private float _steer;

        private Hand? _stickHand;
        private Vector3 _stickStartLocal;

        public CockpitControls(VRRig rig)
        {
            _rig = rig;
        }

        public void Update()
        {
            Actor actor = GameContext.PlayerActor;
            Seat seat = actor != null && !actor.dead && actor.IsSeated() && actor.seat.IsDriverSeat() ? actor.seat : null;
            if (seat != _seat)
            {
                ReleaseAll();
                _seat = seat;
                FindControls();
            }
            if (_seat == null || GameContext.GameMenuOpen)
            {
                ReleaseAll();
                return;
            }
            if ((_onWheel[0] || _onWheel[1]) && _wheel == null || _stickHand != null && _stick == null)
            {
                ReleaseAll(); // the vehicle was destroyed under the hands
                FindControls();
            }
            foreach (VRHand hand in _rig.Hands)
            {
                UpdateHand(hand);
            }
            // Two hands on the rim share it equally. Pulled half way to each hand in turn, the hand updated second won:
            // (left + 2 right) / 3, so turning with the left hand while the right held still steered a third as much.
            if (_onWheel[0] && _onWheel[1])
            {
                _steer = (_handSteer[0] + _handSteer[1]) * 0.5f;
            }
            else if (_onWheel[0] || _onWheel[1])
            {
                _steer = _handSteer[_onWheel[0] ? 0 : 1];
            }
            VehicleInput.WheelSteer = _onWheel[0] || _onWheel[1] ? _steer : (float?)null;
        }

        private void FindControls()
        {
            _wheel = null;
            _stick = null;
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
        }

        private void UpdateHand(VRHand hand)
        {
            int i = (int)hand.Side;
            if (!hand.Tracked)
            {
                return;
            }
            Vector3 palm = hand.Palm.position;
            if (hand.GrabPressed && hand.Role == HandRole.Free)
            {
                if (_wheel != null && NearWheel(palm))
                {
                    _onWheel[i] = true;
                    _wheelStart[i] = OnWheelPlane(palm);
                    _wheelBase[i] = _steer;
                    _wheelTurned[i] = 0f;
                    hand.Role = HandRole.Cockpit;
                    hand.Model.CockpitStick = false;
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    hand.Haptic(0.03f, 120f, 0.4f);
                }
                else if (_stick != null && _stickHand == null && Vector3.Distance(palm, _stick.position) < 0.16f)
                {
                    _stickHand = hand.Side;
                    _stickStartLocal = InSeat(palm);
                    hand.Role = HandRole.Cockpit;
                    hand.Model.CockpitStick = true;
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    hand.Haptic(0.03f, 120f, 0.4f);
                }
            }

            if (_onWheel[i])
            {
                if (!hand.GrabHeld)
                {
                    _onWheel[i] = false;
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
                    Vector3 now = OnWheelPlane(palm);
                    float maxRot = Mathf.Max(5f, _wheel.maxRotation);
                    _wheelTurned[i] = AddTurn(_wheelTurned[i], _wheelStart[i], now, _wheel.transform.forward, _wheelBase[i], maxRot);
                    _wheelStart[i] = now;
                    _handSteer[i] = Mathf.Clamp(_wheelBase[i] + _wheelTurned[i] / maxRot, -1f, 1f);
                }
            }

            if (_stickHand == hand.Side)
            {
                if (!hand.GrabHeld)
                {
                    _stickHand = null;
                    hand.Role = HandRole.Free;
                    VehicleInput.FlightStick = null;
                }
                else
                {
                    Vector3 local = InSeat(palm) - _stickStartLocal;
                    var throw01 = new Vector2(Mathf.Clamp(local.x / StickThrow, -1f, 1f), Mathf.Clamp(local.z / StickThrow, -1f, 1f));
                    // A small centre zone (6 mm), rescaled so the input still starts from nothing: the hand's own tremor
                    // (2-3 mm) was a steady 2-4% of stick and the aircraft drifted.
                    VehicleInput.FlightStick = Input.VRGameInput.ApplyDeadzone(throw01, StickDeadzone);
                }
            }
        }

        /// <summary>After the game's own wheel animation: the rim sits exactly where the hands turned it.</summary>
        public void BeforeRender()
        {
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
        internal static Vector3 InFrame(Transform frame, Vector3 world) => Quaternion.Inverse(frame.rotation) * (world - frame.position);

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
                bool ok = stick < 0.01f && Mathf.Abs(Mathf.Abs(steer) - 1f) < 0.01f;
                return $"hand still in a moving seat: stick {stick:0.000}; rim turned 270 deg: steering {steer:+0.00;-0.00}{(ok ? "" : " (NOT RIGHT)")}";
            }
            finally
            {
                Object.Destroy(seat);
            }
        }

        private bool NearWheel(Vector3 palm)
        {
            Vector3 rel = palm - _wheel.transform.position;
            float axial = Mathf.Abs(Vector3.Dot(rel, _wheel.transform.forward));
            float radial = Vector3.ProjectOnPlane(rel, _wheel.transform.forward).magnitude;
            return axial < 0.12f && radial < 0.32f;
        }

        private Vector3 OnWheelPlane(Vector3 palm)
        {
            Vector3 v = Vector3.ProjectOnPlane(palm - _wheel.transform.position, _wheel.transform.forward);
            return v.sqrMagnitude > 1e-6f ? v : _wheel.transform.up * 0.15f;
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
            _stickHand = null;
            _steer = 0f;
            VehicleInput.WheelSteer = null;
            VehicleInput.FlightStick = null;
        }
    }
}
