using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Mounted guns within reach taken by their handles (the user, 2026-11-15: "let me actually grab the standed things like standed
    /// turrets with my fingers and when i hold on them it moves with my hand but if i dont hold on them it doesnt move (also on car mgs
    /// like razorback car mg)"; "make sure modded turrets on vehicles work too or modded turrets that are standing. Everything").
    /// <list type="bullet">
    /// <item>The handles: the seat's hand targets (where the game puts a soldier's hands); a gun with none (many mods' guns) gets two
    /// at the rear of its own model, either side.</item>
    /// <item>Held, the gun turns as a rigid body round its pivot with the hands: by the angle the hands go round the pivot, sideways and
    /// up and down (handles behind the pivot: to the right turns it left; handles in front: right; under it: the up and down only,
    /// the sideways by the hand's own turn). Let go, it stays where it points.</item>
    /// <item>Reloaded by hand (Manual reload): the cocking handle on the receiver's right pulled back 7 cm; a gun with no magazine size
    /// never needs it.</item>
    /// <item>Which guns: a mounted turret (stabilised or not) of the player's seat with its pivot within arm's reach of the head; a
    /// tank's or a gunship's turret, out of reach, keeps TurretAim's head or hand aim.</item>
    /// </list>
    /// </summary>
    public sealed class TurretGrip
    {
        /// <summary>How near the palm must be to a handle to take it (m).</summary>
        private const float Reach = 0.17f;
        /// <summary>The pivot of a gun taken by hand, from the head: further is a turret aimed from a sight (a tank's, a gunship's).</summary>
        private const float ArmsLength = 1.7f; // (a ZU-23's seated gunner: its pivot 1.3 to 1.6 m from the eyes)
        /// <summary>Under this from the pivot (across a turning axis), the hands do not swing that way: the hand's own turn does.</summary>
        private const float ShortArm = 0.2f; // (closer, 20 cm of hand swung a gun 96 degrees: twitchy)
        private const float TrackingGrace = 0.2f;

        internal static TurretGrip Instance;

        private readonly VRRig _rig;
        private Seat _seat;
        private Weapon _gun;
        private Transform _frame; // what the gun turns in (the vehicle, the nest)
        private Transform _pivotT;
        /// <summary>The handles in the pivot's own frame (so they move with the gun), whether from the seat or made from the model.</summary>
        private readonly List<Vector3> _handlesLocal = new List<Vector3>(2);
        private bool _madeHandles;
        private readonly bool[] _holding = new bool[2];
        private readonly float[] _trackedAt = new float[2];
        private readonly Vector3[] _palmInFrame = new Vector3[2];
        private readonly Quaternion[] _palmTurnInFrame = { Quaternion.identity, Quaternion.identity };
        private Transform _palmFrame;
        private int _heldCount = -1;
        private Vector3 _gripAtGrab;
        private Quaternion _turnAtGrab = Quaternion.identity;
        private Vector3 _aimAtGrab;
        private Vector3? _aim; // the wanted aim, frame space; null: not held

        public TurretGrip(VRRig rig)
        {
            _rig = rig;
            Instance = this;
        }

        /// <summary>This gun is aimed by its handles (held or not): TurretAim gives it no head aim.</summary>
        internal static bool Governs(Weapon turret) => Instance != null && Instance._gun != null && turret == Instance._gun;

        /// <summary>Where the held gun is wanted to point (world), or null: not held, it stays as it is.</summary>
        internal static Vector3? Direction(Weapon turret)
        {
            if (!Governs(turret) || Instance._aim == null || Instance._frame == null)
            {
                return null;
            }
            return Instance._frame.rotation * Instance._aim.Value;
        }

        /// <summary>Whether the seat's gun is one taken by hand (see the class), from where the head is now.</summary>
        internal static bool Grabbable(Seat seat, out Weapon gun)
        {
            gun = null;
            if (seat == null || !VRConfig.GrabMountedGuns.Value)
            {
                return false;
            }
            MountedWeapon w = seat.activeWeapon;
            if (!(w is MountedTurret || w is MountedStabilizedTurret))
            {
                return false;
            }
            // Not a driver's gun of something that drives (a tank's main gun, a gunship's: the hands are on the levers or the stick), nor
            // one aimed through a sight of its own (the gunner looks through its camera). A gun nest's one seat is its driver's.
            Vehicle v = seat.vehicle;
            if (seat.IsDriverSeat() && (v is Car || v is ArcadeCar || v is Tank || v is Helicopter || v is Airplane || v is Plane || v is Boat)
                || w.overrideCamera != null)
            {
                return false;
            }
            Transform pivot = Pivot(w);
            VRRig rig = VRRig.Instance;
            if (pivot == null || rig == null || rig.Head == null || Vector3.Distance(pivot.position, rig.Head.position) > ArmsLength)
            {
                return false;
            }
            gun = w;
            return true;
        }

        internal static Transform Pivot(Weapon gun)
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

        private Transform Muzzle() => AimReference(_gun);

        /// <summary>
        /// The transform whose forward the game steers the gun by (TurretAim's reference): a stabilised turret's pitch part, else its
        /// muzzle. Read from the muzzle alone, a gun whose muzzle part faces another way was swung the wrong way.
        /// </summary>
        internal static Transform AimReference(Weapon gun)
        {
            if (gun is MountedStabilizedTurret s && s.pitchTransform != null)
            {
                return s.pitchTransform;
            }
            return gun.CurrentMuzzle() != null ? gun.CurrentMuzzle() : gun.transform;
        }

        /// <summary>
        /// The handles: the seat's hand targets near the gun, else two made at the rear of the gun's own model (its renderers under the
        /// pivot, the point furthest back along the barrel, 12 cm either side of it).
        /// </summary>
        private void FindHandles()
        {
            _handlesLocal.Clear();
            _madeHandles = false;
            foreach (Transform h in new[] { _seat.handTargetL, _seat.handTargetR })
            {
                if (h != null && Vector3.Distance(h.position, _pivotT.position) < 1.5f)
                {
                    _handlesLocal.Add(_pivotT.InverseTransformPoint(h.position));
                }
            }
            if (_handlesLocal.Count > 0)
            {
                // (The targets move with the gun when they are its children; else they are taken where they sit at the start, in the gun.)
                return;
            }
            _madeHandles = true;
            Transform m = Muzzle();
            Vector3 aim = m.forward, right = m.right, pivot = _pivotT.position;
            float back = -0.5f;
            foreach (Renderer r in _pivotT.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || r is ParticleSystemRenderer)
                {
                    continue;
                }
                Bounds b = r.bounds;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 c = b.center + Vector3.Scale(b.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                    back = Mathf.Min(back, Vector3.Dot(c - pivot, aim));
                }
            }
            back = Mathf.Max(back, -1.2f); // (a gun's rear within reach of its gunner)
            Vector3 rear = pivot + aim * (back + 0.05f);
            _handlesLocal.Add(_pivotT.InverseTransformPoint(rear - right * 0.12f));
            _handlesLocal.Add(_pivotT.InverseTransformPoint(rear + right * 0.12f));
        }

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
                _pivotT = gun != null ? Pivot(gun) : null;
                _palmFrame = null;
                if (_gun != null && _pivotT != null)
                {
                    FindHandles();
                    Log.InfoOnce("turret-grip:" + _seat.vehicle.name + ":" + _gun.name,
                        $"Mounted gun {_gun.name} on {_seat.vehicle.name}: taken by hand, {(_madeHandles ? "handles made at the rear of its model" : _handlesLocal.Count + " handle(s) from the seat")}, "
                        + $"{(ReloadsByHand(_gun) ? "reloaded by its cocking handle" : "no reload by hand")}.");
                }
            }
            if (_gun == null || _frame == null || _pivotT == null || GameContext.GameMenuOpen || GameManager.IsPaused())
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
                bool keeps = Time.unscaledTime - _trackedAt[i] < TrackingGrace && hand.GrabHeld;
                bool squeeze = !_holding[i] && _cockHand != i && hand.Tracked && hand.Role == HandRole.Free && VRInput.GetHeldUnclaimed(DigitalAction.Grab, hand.Side);
                float toCock = ReloadsByHand(_gun) ? Vector3.Distance(palm, InFrame(CockPoint())) : float.MaxValue;
                float toHandle = HandleDistance(palm);
                if (squeeze && toCock < CockReach && toCock <= toHandle)
                {
                    // The cocking handle: pulled back, the gun reloads (the game's own reload, its time and sound).
                    _cockHand = i;
                    _cockStart = palm;
                    _cockPulled = false;
                    Take(hand);
                    hand.Haptic(0.02f, 160f, 0.4f);
                }
                else if (_cockHand == i)
                {
                    if (!keeps)
                    {
                        _cockHand = -1;
                        Free(hand);
                    }
                    else if (!_cockPulled && Vector3.Dot(palm - _cockStart, -AimNow()) > CockPull)
                    {
                        _cockPulled = true;
                        hand.Haptic(0.04f, 90f, 0.8f); // (the bolt's clack)
                        ReloadNow(_gun);
                    }
                }
                else if (squeeze && toHandle < Reach)
                {
                    _holding[i] = true;
                    Take(hand);
                    hand.Haptic(0.03f, 120f, 0.5f);
                }
                else if (_holding[i] && !keeps)
                {
                    _holding[i] = false;
                    Free(hand);
                }
                held += _holding[i] ? 1 : 0;
            }
            if (ReloadsByHand(_gun) && _gun.ammo <= 0 && !_gun.reloading)
            {
                if (!_toldEmpty)
                {
                    _toldEmpty = true;
                    UI.UISystem.Instance?.Notice?.Show("Empty: pull the cocking handle on the gun's right side back to reload.", 3f);
                }
            }
            else if (_gun.ammo > 0)
            {
                _toldEmpty = false;
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
            Vector3 pivot = InFrame(_pivotT.position);
            if (held != _heldCount)
            {
                // Taken (or a hand added or let go): from the gun's aim as it is, no jump.
                _heldCount = held;
                _aimAtGrab = AimNow();
                _gripAtGrab = grip - pivot;
                _turnAtGrab = PalmTurnInFrame(first);
            }
            _aim = Swing(_aimAtGrab, _gripAtGrab, grip - pivot, PalmTurnInFrame(first) * Quaternion.Inverse(_turnAtGrab));
        }

        /// <summary>
        /// The gun turned with the grip as one rigid body round its pivot (all in the frame, up is up): sideways by the angle the grip
        /// went round the upright axis, then up and down by the angle it went round the gun's own across axis. Where the grip sits
        /// (nearly) on an axis, that axis's turn is the hand's own instead.
        /// </summary>
        internal static Vector3 Swing(Vector3 aim0, Vector3 grip0, Vector3 grip1, Quaternion handTurn)
        {
            Vector3 up = Vector3.up;
            Vector3 h0 = Vector3.ProjectOnPlane(grip0, up), h1 = Vector3.ProjectOnPlane(grip1, up);
            float yaw;
            if (h0.magnitude >= ShortArm && h1.magnitude >= ShortArm)
            {
                yaw = Vector3.SignedAngle(h0, h1, up);
            }
            else
            {
                Vector3 f = handTurn * Vector3.forward;
                yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg; // (the hand's own turn sideways)
            }
            Vector3 aimFlat = Vector3.ProjectOnPlane(aim0, up);
            Vector3 across = Vector3.Cross(up, aimFlat.sqrMagnitude > 1e-4f ? aimFlat.normalized : Vector3.forward); // the gun's right
            // The grip with the sideways turn taken off, against where it was, round the across axis.
            Vector3 unturned = Quaternion.AngleAxis(-yaw, up) * grip1;
            Vector3 v0 = Vector3.ProjectOnPlane(grip0, across), v1 = Vector3.ProjectOnPlane(unturned, across);
            float pitch;
            if (v0.magnitude >= ShortArm && v1.magnitude >= ShortArm)
            {
                pitch = Vector3.SignedAngle(v0, v1, across);
            }
            else
            {
                Vector3 f = handTurn * Vector3.forward;
                pitch = -Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg;
            }
            return (Quaternion.AngleAxis(yaw, up) * Quaternion.AngleAxis(pitch, across) * aim0).normalized;
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

        /// <summary>The handles where they are now (world).</summary>
        internal List<Vector3> HandlesWorld()
        {
            var list = new List<Vector3>(2);
            if (_pivotT != null)
            {
                foreach (Vector3 l in _handlesLocal)
                {
                    list.Add(_pivotT.TransformPoint(l));
                }
            }
            return list;
        }

        internal bool MadeHandles => _madeHandles;

        /// <summary>For the vehicle check: where the hands want the gun (world), null when not held.</summary>
        internal Vector3? WantedWorld => _aim != null && _frame != null ? _frame.rotation * _aim.Value : (Vector3?)null;

        private float HandleDistance(Vector3 palm)
        {
            float best = float.MaxValue;
            foreach (Vector3 l in _handlesLocal)
            {
                best = Mathf.Min(best, Vector3.Distance(palm, InFrame(_pivotT.TransformPoint(l))));
            }
            return best;
        }

        // ---- reloading by hand ---------------------------------------------------------------------------------------------

        private const float CockReach = 0.13f, CockPull = 0.07f;
        private int _cockHand = -1;
        private Vector3 _cockStart;
        private bool _cockPulled, _toldEmpty;
        private static bool _reloadAllowed;

        /// <summary>
        /// This gun is reloaded by hand (taken by its handles, Manual reload, and it has a magazine to run dry: a gun with no
        /// magazine size never needs it). The user, 2026-11-15: "reloadable manually or if not needed to reload, then its not needed".
        /// </summary>
        internal static bool ReloadsByHand(Weapon gun) =>
            Governs(gun) && VRConfig.ReloadNow == ReloadMode.Manual && gun.configuration != null && gun.configuration.ammo > 0;

        /// <summary>The game's own reload of a gun reloaded by hand waits for the hand (not its automatic one, nor the reload key).</summary>
        internal static bool HoldsReload(Weapon gun) => ReloadsByHand(gun) && !_reloadAllowed;

        private static void ReloadNow(Weapon gun)
        {
            if (gun.reloading || gun.ammo >= gun.configuration.ammo || !gun.HasSpareAmmo() && !gun.HasInfiniteSpareAmmo())
            {
                return;
            }
            _reloadAllowed = true;
            try
            {
                gun.Reload();
            }
            finally
            {
                _reloadAllowed = false;
            }
        }

        /// <summary>The cocking handle: the receiver's right side, between the handles and the pivot (world).</summary>
        private Vector3 CockPoint()
        {
            Transform m = Muzzle();
            Vector3 handles = Vector3.zero;
            List<Vector3> hs = HandlesWorld();
            foreach (Vector3 h in hs)
            {
                handles += h;
            }
            handles = hs.Count > 0 ? handles / hs.Count : _pivotT.position - m.forward * 0.4f;
            return Vector3.Lerp(handles, _pivotT.position, 0.4f) + m.right * 0.12f + m.up * 0.04f;
        }

        /// <summary>For the vehicle check: where the cocking handle is (world).</summary>
        internal Vector3 CockPointWorld => _gun != null && _pivotT != null ? CockPoint() : Vector3.zero;

        // ---- hands ---------------------------------------------------------------------------------------------------------

        private static void Take(VRHand hand)
        {
            hand.Role = HandRole.Cockpit;
            hand.Model.CockpitStick = true; // (the fist round a grip)
            VRInput.Consume(DigitalAction.Grab, hand.Side);
        }

        private static void Free(VRHand hand)
        {
            if (hand.Role == HandRole.Cockpit)
            {
                hand.Role = HandRole.Free;
            }
            hand.Model.CockpitStick = false;
        }

        internal bool Held => _holding[0] || _holding[1];

        /// <summary>That hand holds the mounted gun by a handle (its trigger fires it).</summary>
        internal static bool HoldsWith(Hand side) => Instance != null && Instance._gun != null && (int)side < 2 && Instance._holding[(int)side];

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

        private void ReleaseAll()
        {
            for (int i = 0; i < 2; i++)
            {
                VRHand hand = _rig.Hands[i];
                if (hand != null && (_holding[i] || _cockHand == i))
                {
                    Free(hand);
                }
                _holding[i] = false;
            }
            _cockHand = -1;
            _aim = null;
            _heldCount = -1;
        }
    }
}
