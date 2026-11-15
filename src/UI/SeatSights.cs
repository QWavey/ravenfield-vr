using System.Collections.Generic;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// 3D sight graphics vehicles put right in front of the seat camera on the seat-HUD layer (tank gunner reticles,
    /// range scales). At their real distance the two eyes disagree about them and the barrel can poke through them;
    /// here they are shown as a collimated sight: same direction and angular size from the eye, but far out and drawn
    /// by the UI camera over the world. Restored when the player leaves the seat.
    /// </summary>
    internal sealed class SeatSights
    {
        private const float Distance = 80f;
        private const float NearRange = 2.5f;

        private sealed class Sight
        {
            public Transform T;
            public Vector3 LocalPosition;
            public Quaternion LocalRotation;
            public Vector3 LocalScale;
            public Vector3 Direction;      // in seat-camera space
            public Quaternion Rotation;    // relative to the seat camera
            public float Range;
            public readonly List<KeyValuePair<GameObject, int>> Layers = new List<KeyValuePair<GameObject, int>>();
            public readonly List<KeyValuePair<Renderer, bool>> Shown = new List<KeyValuePair<Renderer, bool>>();
        }

        private bool _hidden;

        /// <summary>
        /// The sights' frame: the seat's gun's own aim (its muzzle or pitch part), so they show where the gun points (the user,
        /// 2026-11-15: in the APC and most tanks "the crosshair moves with" the view: the seat camera of those sits on the hull,
        /// not the gun, and with none the head was taken). Else the seat camera, else the head.
        /// </summary>
        private Quaternion Basis(Transform head)
        {
            MountedWeapon w = _seat != null ? _seat.activeWeapon : null;
            if (w is MountedTurret || w is MountedStabilizedTurret || w is TankTurret)
            {
                Transform aim = Rig.TurretGrip.AimReference(w);
                if (aim != null)
                {
                    return aim.rotation;
                }
            }
            return _camera != null ? _camera.rotation : head.rotation;
        }

        private readonly List<Sight> _sights = new List<Sight>();
        private Seat _seat;
        /// <summary>The seat's own camera, which turns with the gun it sights for.</summary>
        private Transform _camera;

        public void BeforeRender(Transform head)
        {
            Actor actor = GameContext.PlayerActor;
            Seat seat = actor != null && !actor.dead && actor.IsSeated() ? actor.seat : null;
            if (seat != _seat)
            {
                Restore();
                _seat = seat;
                Capture();
            }
            // Inside a closed vehicle's cabin the periscope is the sight (its own reticle and aim mark): the game's, drawn over
            // everything, stood through the cabin's walls and over the periscope. Hidden while the cabin is shown.
            bool hide = Rig.VehicleInterior.Instance != null && Rig.VehicleInterior.Instance.Shown;
            if (hide != _hidden)
            {
                _hidden = hide;
                foreach (Sight s in _sights)
                {
                    foreach (KeyValuePair<Renderer, bool> r in s.Shown)
                    {
                        if (r.Key != null)
                        {
                            r.Key.enabled = !hide && r.Value;
                        }
                    }
                }
            }
            if (hide)
            {
                return;
            }
            // Seen from the eye but turned with the gun: turned with the head, the reticle sat where you looked while the turret
            // was still swinging round to follow, and showed it on target before it was.
            Quaternion basis = Basis(head);
            foreach (Sight s in _sights)
            {
                if (s.T == null)
                {
                    continue;
                }
                s.T.SetPositionAndRotation(head.position + basis * (s.Direction * Distance), basis * s.Rotation);
                Vector3 scale = s.LocalScale * (Distance / s.Range);
                if (s.T.localScale != scale) // the same every frame (captured once); the game changing it is still put right
                {
                    s.T.localScale = scale;
                }
            }
        }

        private void Capture()
        {
            if (_seat == null || _seat.camera == null || _seat.vehicle == null)
            {
                return;
            }
            Transform cam = _seat.camera.transform;
            _camera = cam;
            Quaternion basis = Basis(cam); // (the frame the sights are kept in: the gun's where it has one)
            foreach (Renderer r in _seat.vehicle.GetComponentsInChildren<Renderer>(true))
            {
                if (r.gameObject.layer != Seat.LAYER_HUD || r is ParticleSystemRenderer || r.GetComponentInParent<Canvas>() != null)
                {
                    continue;
                }
                Transform t = r.transform;
                if (_sights.Exists(s => t.IsChildOf(s.T)))
                {
                    continue; // moves with an ancestor already taken
                }
                Vector3 rel = cam.InverseTransformPoint(t.position);
                float range = rel.magnitude;
                if (range < 0.02f || range > NearRange)
                {
                    continue;
                }
                var sight = new Sight
                {
                    T = t,
                    LocalPosition = t.localPosition,
                    LocalRotation = t.localRotation,
                    LocalScale = t.localScale,
                    Direction = Runtime.RigidPose.Conjugate(basis) * (cam.rotation * (rel / range)),
                    Rotation = Runtime.RigidPose.Conjugate(basis) * t.rotation,
                    Range = range
                };
                foreach (Transform c in t.GetComponentsInChildren<Transform>(true))
                {
                    sight.Layers.Add(new KeyValuePair<GameObject, int>(c.gameObject, c.gameObject.layer));
                    c.gameObject.layer = 5;
                }
                foreach (Renderer sr in t.GetComponentsInChildren<Renderer>(true))
                {
                    sight.Shown.Add(new KeyValuePair<Renderer, bool>(sr, sr.enabled));
                }
                _sights.Add(sight);
            }
        }

        public void Restore()
        {
            foreach (Sight s in _sights)
            {
                if (s.T == null)
                {
                    continue;
                }
                s.T.localPosition = s.LocalPosition;
                s.T.localRotation = s.LocalRotation;
                s.T.localScale = s.LocalScale;
                foreach (KeyValuePair<GameObject, int> l in s.Layers)
                {
                    if (l.Key != null)
                    {
                        l.Key.layer = l.Value;
                    }
                }
                foreach (KeyValuePair<Renderer, bool> r in s.Shown)
                {
                    if (r.Key != null)
                    {
                        r.Key.enabled = r.Value; // (as the game had it: hidden in a cabin)
                    }
                }
            }
            _sights.Clear();
            _hidden = false;
            _seat = null;
            _camera = null;
        }
    }
}
