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

        /// <summary>
        /// A suppressed camera also renders into one pixel, its viewport put back after, as its mask is (GameCamerasOnePixel, off by
        /// default: to try in a headset; the bench's own setting while it runs, AutoTest.PerfBench "game cameras 1 px", whatever the
        /// config says). What they still cost with nothing to draw (the user's log, 2026-10-28: "Camera" 0.33 ms, FP Camera 0.12,
        /// post-processing 0.11, first-person model 0.10) is the clear, image effects and post-processing over a full-size target;
        /// whether a 1 px viewport takes that away, and leaves the mirror and the game's aim alone, is what is to be measured
        /// (Tasks 348).
        /// </summary>
        internal static bool? TinyRect;

        private static bool OnePixel => TinyRect ?? VRConfig.GameCamerasOnePixel.Value;
        private readonly Dictionary<Camera, Rect> _rectDuringRender = new Dictionary<Camera, Rect>();

        public CameraSuppressor(Camera vrCamera)
        {
            _vrCamera = vrCamera;
            Camera.onPreCull += OnPreCull;
            Camera.onPostRender += OnPostRender;
        }

        /// <summary>Cameras created by the mod (scope lenses, UI event cameras) that must keep rendering.</summary>
        public void Ignore(Camera c) => _ignored.Add(c);

        private static readonly System.Predicate<Camera> IsGone = c => c == null;
        private static readonly List<Camera> GoneCameras = new List<Camera>();
        private static readonly List<PostProcessLayer> GoneLayers = new List<PostProcessLayer>();

        /// <summary>Call once per frame after the game's LateUpdate.</summary>
        public void Refresh()
        {
            // The game's post-processing switches are looked at a few times a second (a component lookup per camera every
            // frame before), and what went with an old map is forgotten then too.
            bool lookAround = Time.frameCount % 15 == 0;
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
                if (!lookAround)
                {
                    continue;
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
            if (!lookAround)
            {
                return;
            }
            _suppressed.RemoveWhere(IsGone);
            _ignored.RemoveWhere(IsGone); // a scope camera per draw of a scoped weapon, destroyed with it
            GoneCameras.Clear();
            foreach (Camera c in _originalEye.Keys)
            {
                if (c == null)
                {
                    GoneCameras.Add(c);
                }
            }
            foreach (Camera c in GoneCameras)
            {
                _originalEye.Remove(c);
            }
            GoneLayers.Clear();
            foreach (PostProcessLayer p in _ppLayers.Keys)
            {
                if (p == null)
                {
                    GoneLayers.Add(p);
                }
            }
            foreach (PostProcessLayer p in GoneLayers)
            {
                _ppLayers.Remove(p);
            }
        }

        private void OnPreCull(Camera c)
        {
            if (c == _vrCamera)
            {
                RestoreShadows(); // (the eyes always with the real distance: a suppressed render whose post-render never came left it at 0)
                RestoreRects();
                RestoreMasks();
                return;
            }
            if (!_suppressed.Contains(c) || c.targetTexture != null)
            {
                return;
            }
            // (Saved once: a render whose post-render never came left the mask at 0, and taken again as the real one it stayed 0 for
            // good, after VR too; review, 2026-10-30.)
            if (!_maskDuringRender.ContainsKey(c))
            {
                _maskDuringRender[c] = c.cullingMask;
            }
            c.cullingMask = 0;
            if (OnePixel && !_rectDuringRender.ContainsKey(c))
            {
                _rectDuringRender[c] = c.rect;
                c.pixelRect = new Rect(0f, 0f, 1f, 1f);
            }
            // And no shadows for it: a camera drawing nothing still rendered the light's shadow maps (the user's Medium, 2026-10-27:
            // the game's FP camera 0.38 ms of main thread a frame against 0.13 with the Game preset's shadows off, its first-person
            // model and post-processing cameras likewise). Put back after its render, as the scope lens does (VRScope).
            if (_shadowDistance < 0f)
            {
                _shadowDistance = QualitySettings.shadowDistance;
                QualitySettings.shadowDistance = 0f;
            }
        }

        /// <summary>The shadow distance while a suppressed camera renders (put back after it), or -1.</summary>
        private float _shadowDistance = -1f;

        private void RestoreShadows()
        {
            if (_shadowDistance >= 0f)
            {
                QualitySettings.shadowDistance = _shadowDistance;
                _shadowDistance = -1f;
            }
        }

        private void OnPostRender(Camera c)
        {
            if (_rectDuringRender.TryGetValue(c, out Rect rect))
            {
                c.rect = rect;
                _rectDuringRender.Remove(c);
            }
            if (_maskDuringRender.TryGetValue(c, out int mask))
            {
                c.cullingMask = mask;
                _maskDuringRender.Remove(c);
                if (_maskDuringRender.Count == 0)
                {
                    RestoreShadows();
                }
            }
        }

        /// <summary>Viewports whose post-render never came (a camera switched off in its render) put back.</summary>
        private void RestoreRects()
        {
            if (_rectDuringRender.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<Camera, Rect> kv in _rectDuringRender)
            {
                if (kv.Key != null)
                {
                    kv.Key.rect = kv.Value;
                }
            }
            _rectDuringRender.Clear();
        }

        /// <summary>
        /// A game camera's own culling mask: the one saved while its render zeroes it (a post-render that never came left it 0 until
        /// the eyes' next pre-cull, and the head camera, copying the game camera's mask, drew no world for that frame).
        /// </summary>
        internal int RealMask(Camera c) => _maskDuringRender.TryGetValue(c, out int saved) ? saved : c.cullingMask;

        /// <summary>Culling masks whose post-render never came put back (as <see cref="RestoreRects"/>).</summary>
        private void RestoreMasks()
        {
            if (_maskDuringRender.Count == 0)
            {
                return;
            }
            foreach (KeyValuePair<Camera, int> kv in _maskDuringRender)
            {
                if (kv.Key != null)
                {
                    kv.Key.cullingMask = kv.Value;
                }
            }
            _maskDuringRender.Clear();
        }

        public void Dispose()
        {
            RestoreShadows();
            RestoreRects();
            RestoreMasks();
            Camera.onPreCull -= OnPreCull;
            Camera.onPostRender -= OnPostRender;
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

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// The game's background camera (the sky and distant scenery behind the main camera's depth-only clear) does not draw while VR
    /// runs: the head camera takes its layers and sky into its own pass (VRRig.CopyCameraSettings), and the background camera's
    /// render, its mask zeroed by the suppressor, filled the desktop window under the mirror for nothing (0.11 ms of main thread
    /// a frame at 0.6 renders a frame, the frame rate line's camera times, 2026-10-27). After its own LateUpdate, which switches it
    /// on with the main camera every frame; its settings stay as the game keeps them, for the head camera to read.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(BackgroundCamera), "LateUpdate")]
    internal static class BackgroundCameraOffInVRPatch
    {
        private static void Postfix(BackgroundCamera __instance)
        {
            if (Core.VRManager.Active && __instance != null && __instance.camera != null && __instance.camera.enabled)
            {
                __instance.camera.enabled = false;
            }
        }
    }
}
