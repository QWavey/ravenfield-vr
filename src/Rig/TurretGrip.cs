using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Open mounted guns taken by their handles (the user, 2026-11-15: "let me actually grab the standed things like standed turrets
    /// with my fingers and when i hold on them it moves with my hand but if i dont hold on them it doesnt move (also on car mgs like
    /// razorback car mg)"). A grip near one of the gun's handles (the seat's hand targets, where the game puts a soldier's hands) takes
    /// it; while held the gun swings round its pivot as the handles are moved (handles to the right, muzzle to the left, as a real
    /// mounted gun), and let go it stays where it points. Guns whose handles sit almost on the pivot follow the hand's turn instead.
    /// Enclosed seats (a helicopter's gunner, a tank's turret) keep TurretAim's head or hand aim.
    /// </summary>
    public sealed class TurretGrip
    {
        /// <summary>How near the palm must be to a handle to take it (m).</summary>
        private const float Reach = 0.16f;
        /// <summary>Under this from the pivot the handles are no lever: the gun follows the hand's turn.</summary>
        private const float ShortLever = 0.2f;
        private const float TrackingGrace = 0.2f;

        internal static TurretGrip Instance;

        private readonly VRRig _rig;
        private Seat _seat;
        private Weapon _gun;
        private Transform _frame; // the vehicle (or the nest): what the gun turns in
        private readonly bool[] _holding = new bool[2];
        private readonly float[] _trackedAt = new float[2];
        private readonly Vector3[] _palmInFrame = new Vector3[2];
        private readonly Quaternion[] _palmTurnInFrame = { Quaternion.identity, Quaternion.identity };
        private Transform _palmFrame;
        private int _heldCount = -1;
        private Quaternion _offset = Quaternion.identity; // lever direction to aim, at the grab (frame space)
        private Quaternion _turnAtGrab = Quaternion.identity;
        private Vector3 _aimAtGrab;
        private bool _lever;
        private Vector3? _aim; // the wanted aim, frame space; null: not held

        public TurretGrip(VRRig rig)
        {
            _rig = rig;
            Instance = this;
        }

        /// <summary>This gun is aimed by its handles (held or not): TurretAim gives it no head aim.</summary>
        internal static bool Governs(Weapon turret) =>
            Instance != null && Instance._gun != null && turret == Instance._gun;

        /// <summary>Where the held gun is wanted to point (world), or null: not held, it stays as it is.</summary>
        internal static Vector3? Direction(Weapon turret)
        {
            if (!Governs(turret) || Instance._aim == null || Instance._frame == null)
            {
                return null;
            }
            return Instance._frame.rotation * Instance._aim.Value;
        }

        internal static bool Grabbable(Seat seat, out Weapon gun)
        {
            gun = null;
            if (seat == null || seat.enclosed || !VRConfig.GrabMountedGuns.Value)
            {
                return false;
            }
            MountedWeapon w = seat.activeWeapon;
            if (!(w is MountedTurret || w is MountedStabilizedTurret) || seat.handTargetL == null && seat.handTargetR == null)
            {
                return false;
            }
            gun = w;
            return true;
        }

        private static Transform Pivot(Weapon gun)
        {
            if (gun is MountedTurret t)
            {
                return t.pitchTransform != null ? t.pitchTransform : t.bearingTransform;
            }
            if (gun is MountedStabilizedTurret s)
            {
                return s.pitchTransform != null ? s.pitchTransform : s.bearingTransform;
            }
            return null;
        }

        private static Transform Frame(Weapon gun)
        {
            Transform bearing = gun is MountedTurret t ? t.bearingTransform : gun is MountedStabilizedTurret s ? s.bearingTransform : null;
            if (bearing != null && bearing.parent != null)
            {
                return bearing.parent;
            }
            return gun.transform.parent != null ? gun.transform.parent : gun.transform;
        }

        private Transform Muzzle() => _gun.CurrentMuzzle() != null ? _gun.CurrentMuzzle() : _gun.transform;

        public void Update()
        {
            Actor actor = GameContext.PlayerActor;
            Seat seat = actor != null && !actor.dead && actor.IsSeated() ? actor.seat : null;
            Weapon gun = null;
            if (seat != null && !Grabbable(seat, out gun))
            {
                seat = null;
            }
            if (seat != _seat || gun != _gun)
            {
                ReleaseAll();
                _seat = seat;
                _gun = gun;
                _frame = gun != null ? Frame(gun) : null;
                _palmFrame = null;
            }
            if (_gun == null || _frame == null || GameContext.GameMenuOpen || GameManager.IsPaused())
            {
                if (_gun == null)
                {
                    _aim = null;
                }
                return;
            }
            int held = 0;
            foreach (VRHand hand in _rig.Hands)
            {
                int i = (int)hand.Side;
                if (hand.Tracked)
                {
                    _trackedAt[i] = Time.unscaledTime;
                }
                Vector3 palm = PalmInFrame(hand);
                if (!_holding[i] && hand.Tracked && hand.Role == HandRole.Free && VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side)
                    && NearHandle(palm))
                {
                    _holding[i] = true;
                    hand.Role = HandRole.Cockpit;
                    hand.Model.CockpitStick = true; // (the fist round a grip)
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    hand.Haptic(0.03f, 120f, 0.5f);
                }
                else if (_holding[i] && !(Time.unscaledTime - _trackedAt[i] < TrackingGrace && hand.GrabHeld))
                {
                    Release(hand);
                }
                held += _holding[i] ? 1 : 0;
            }
            if (held == 0)
            {
                _aim = null; // let go: the gun stays where it points
                _heldCount = 0;
                return;
            }
            Vector3 grip = Vector3.zero;
            VRHand first = null;
            foreach (VRHand hand in _rig.Hands)
            {
                if (_holding[(int)hand.Side])
                {
                    grip += PalmInFrame(hand);
                    first = first ?? hand;
                }
            }
            grip /= held;
            Transform pivotT = Pivot(_gun);
            Vector3 pivot = pivotT != null ? InFrame(pivotT.position) : grip + AimNow();
            Vector3 lever = pivot - grip;
            if (held != _heldCount)
            {
                // Taken (or a hand added or let go): from the gun's aim as it is, no jump.
                _heldCount = held;
                _aimAtGrab = AimNow();
                _lever = lever.magnitude >= ShortLever;
                _offset = _lever ? Quaternion.FromToRotation(lever.normalized, _aimAtGrab) : Quaternion.identity;
                _turnAtGrab = PalmTurnInFrame(first);
            }
            _aim = _lever ? (_offset * lever.normalized).normalized
                          : (PalmTurnInFrame(first) * Quaternion.Inverse(_turnAtGrab) * _aimAtGrab).normalized;
        }

        /// <summary>The palms against the vehicle as both are drawn (VRRig poses the hands just before): read by the next Update.</summary>
        public void BeforeRender()
        {
            if (_frame == null)
            {
                return;
            }
            foreach (VRHand hand in _rig.Hands)
            {
                _palmInFrame[(int)hand.Side] = InFrame(hand.Palm.position);
                _palmTurnInFrame[(int)hand.Side] = Quaternion.Inverse(_frame.rotation) * hand.Palm.rotation;
            }
            _palmFrame = _frame;
        }

        private Vector3 InFrame(Vector3 world) => Quaternion.Inverse(_frame.rotation) * (world - _frame.position);

        private Vector3 AimNow() => (Quaternion.Inverse(_frame.rotation) * Muzzle().forward).normalized;

        private Vector3 PalmInFrame(VRHand hand) => _palmFrame == _frame ? _palmInFrame[(int)hand.Side] : InFrame(hand.Palm.position);

        private Quaternion PalmTurnInFrame(VRHand hand) =>
            _palmFrame == _frame ? _palmTurnInFrame[(int)hand.Side] : Quaternion.Inverse(_frame.rotation) * hand.Palm.rotation;

        private bool NearHandle(Vector3 palm)
        {
            foreach (Transform h in new[] { _seat.handTargetL, _seat.handTargetR })
            {
                if (h != null && Vector3.Distance(palm, InFrame(h.position)) < Reach)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>For the vehicle check: the handles' places (world), the left then the right (either may be null).</summary>
        internal Transform[] Handles => _seat != null ? new[] { _seat.handTargetL, _seat.handTargetR } : new Transform[0];

        internal bool Held => _holding[0] || _holding[1];

        /// <summary>A shot of the held gun: a short hard buzz in each hand on it.</summary>
        internal static void Kick()
        {
            if (Instance == null)
            {
                return;
            }
            foreach (VRHand hand in Instance._rig.Hands)
            {
                if (hand != null && Instance._holding[(int)hand.Side])
                {
                    hand.Haptic(0.025f, 70f, 0.7f);
                }
            }
        }

        /// <summary>That hand holds the mounted gun by a handle (its trigger fires it).</summary>
        internal static bool HoldsWith(Hand side) => Instance != null && Instance._gun != null && (int)side < 2 && Instance._holding[(int)side];

        private void Release(VRHand hand)
        {
            int i = (int)hand.Side;
            if (!_holding[i])
            {
                return;
            }
            _holding[i] = false;
            if (hand.Role == HandRole.Cockpit)
            {
                hand.Role = HandRole.Free;
            }
            hand.Model.CockpitStick = false;
        }

        private void ReleaseAll()
        {
            foreach (VRHand hand in _rig.Hands)
            {
                if (hand != null)
                {
                    Release(hand);
                }
            }
            _aim = null;
            _heldCount = -1;
        }
    }
}
