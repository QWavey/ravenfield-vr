using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.XR;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// Keeps the game's own screen cameras alive for game logic (they stay enabled, keep their transforms,
    /// projections and culling masks) while stopping them from drawing: they are taken off the HMD
    /// (stereoTargetEye = None) and their culling mask is zeroed only for the duration of their render.
    /// Render-texture cameras (minimap, scopes, reflections, previews) are left untouched.
    /// </summary>
    public sealed class CameraSuppressor : IDisposable
    {
        private readonly Camera _vrCamera;
        private readonly Dictionary<Camera, int> _maskDuringRender = new Dictionary<Camera, int>();
        private readonly HashSet<Camera> _suppressed = new HashSet<Camera>();
        private readonly Dictionary<Camera, StereoTargetEyeMask> _originalEye = new Dictionary<Camera, StereoTargetEyeMask>();
        private readonly Dictionary<PostProcessLayer, bool> _ppLayers = new Dictionary<PostProcessLayer, bool>();
        private Camera[] _buffer = new Camera[32];
        private readonly HashSet<Camera> _ignored = new HashSet<Camera>();

        public CameraSuppressor(Camera vrCamera)
        {
            _vrCamera = vrCamera;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
        }

        /// <summary>Cameras created by the mod (scope lenses, UI event cameras) that must keep rendering.</summary>
        public void Ignore(Camera c) => _ignored.Add(c);

        /// <summary>Call once per frame after the game's LateUpdate.</summary>
        public void Refresh()
        {
            int count = Camera.allCamerasCount;
            if (_buffer.Length < count)
            {
                _buffer = new Camera[count * 2];
            }
            count = Camera.GetAllCameras(_buffer);
            for (int i = 0; i < count; i++)
            {
                Camera c = _buffer[i];
                if (c == null || c == _vrCamera || c.targetTexture != null || _ignored.Contains(c))
                {
                    continue;
                }
                if (_suppressed.Add(c))
                {
                    _originalEye[c] = c.stereoTargetEye;
                    XRDevice.DisableAutoXRCameraTracking(c, true);
                }
                if (c.stereoTargetEye != StereoTargetEyeMask.None)
                {
                    c.stereoTargetEye = StereoTargetEyeMask.None;
                }
                PostProcessLayer pp = c.GetComponent<PostProcessLayer>();
                if (pp != null && pp.enabled)
                {
                    if (!_ppLayers.ContainsKey(pp))
                    {
                        _ppLayers[pp] = true;
                    }
                    pp.enabled = false;
                }
            }
            _suppressed.RemoveWhere(c => c == null);
        }

        private void OnPreCull(Camera c)
        {
            if (c == _vrCamera || !_suppressed.Contains(c) || c.targetTexture != null)
            {
                return;
            }
            _maskDuringRender[c] = c.cullingMask;
            c.cullingMask = 0;
        }

        private void OnPostRender(Camera c)
        {
            if (_maskDuringRender.TryGetValue(c, out int mask))
            {
                c.cullingMask = mask;
                _maskDuringRender.Remove(c);
            }
        }

        public void Dispose()
        {
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
            foreach (KeyValuePair<Camera, int> kv in _maskDuringRender)
            {
                if (kv.Key != null)
                {
                    kv.Key.cullingMask = kv.Value;
                }
            }
            foreach (Camera c in _suppressed)
            {
                if (c == null)
                {
                    continue;
                }
                if (_originalEye.TryGetValue(c, out StereoTargetEyeMask eye))
                {
                    c.stereoTargetEye = eye;
                }
                XRDevice.DisableAutoXRCameraTracking(c, false);
            }
            foreach (KeyValuePair<PostProcessLayer, bool> kv in _ppLayers)
            {
                if (kv.Key != null)
                {
                    kv.Key.enabled = kv.Value;
                }
            }
            _suppressed.Clear();
            _maskDuringRender.Clear();
            _ppLayers.Clear();
            Log.Info("Game cameras restored.");
        }
    }
}
