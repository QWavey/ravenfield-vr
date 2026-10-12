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

        private readonly VRRig _rig;
        private Seat _seat;
        private SteeringWheel _wheel;
        private Transform _stick;

        private readonly bool[] _onWheel = new bool[2];
        private readonly Vector3[] _wheelStart = new Vector3[2];
        private readonly float[] _wheelBase = new float[2];
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
            foreach (VRHand hand in _rig.Hands)
            {
                UpdateHand(hand);
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
                    hand.Role = HandRole.Cockpit;
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    hand.Haptic(0.03f, 120f, 0.4f);
                }
                else if (_stick != null && _stickHand == null && Vector3.Distance(palm, _stick.position) < 0.16f)
                {
                    _stickHand = hand.Side;
                    _stickStartLocal = Quaternion.Inverse(_seat.transform.rotation) * palm;
                    hand.Role = HandRole.Cockpit;
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
                }
                else
                {
                    Vector3 now = OnWheelPlane(palm);
                    float angle = Vector3.SignedAngle(_wheelStart[i], now, _wheel.transform.forward);
                    float maxRot = Mathf.Max(5f, _wheel.maxRotation);
                    float handSteer = Mathf.Clamp(_wheelBase[i] + angle / maxRot, -1f, 1f);
                    int holders = (_onWheel[0] ? 1 : 0) + (_onWheel[1] ? 1 : 0);
                    _steer = holders == 2 ? Mathf.Lerp(_steer, handSteer, 0.5f) : handSteer;
                    Vector3 rim = _wheel.transform.position + now.normalized * Mathf.Clamp(now.magnitude, 0.12f, 0.25f);
                    hand.Model.OverridePose(new RigidPose(rim, Quaternion.LookRotation(_wheel.transform.forward, now.normalized)));
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
                    Vector3 local = Quaternion.Inverse(_seat.transform.rotation) * palm - _stickStartLocal;
                    VehicleInput.FlightStick = new Vector2(Mathf.Clamp(local.x / StickThrow, -1f, 1f), Mathf.Clamp(local.z / StickThrow, -1f, 1f));
                    hand.Model.OverridePose(new RigidPose(_stick.position, _seat.transform.rotation));
                }
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
