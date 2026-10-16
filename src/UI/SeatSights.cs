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
            // Seen from the eye but turned with the seat's camera (the gun): turned with the head, the reticle sat where you
            // looked while the turret was still swinging round to follow, and showed it on target before it was.
            Quaternion basis = _camera != null ? _camera.rotation : head.rotation;
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
                    Direction = rel / range,
                    Rotation = Quaternion.Inverse(cam.rotation) * t.rotation,
                    Range = range
                };
                foreach (Transform c in t.GetComponentsInChildren<Transform>(true))
                {
                    sight.Layers.Add(new KeyValuePair<GameObject, int>(c.gameObject, c.gameObject.layer));
                    c.gameObject.layer = 5;
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
            }
            _sights.Clear();
            _seat = null;
            _camera = null;
        }
    }
}
