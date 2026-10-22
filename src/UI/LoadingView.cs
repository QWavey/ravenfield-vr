using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using UnityEngine.XR;
using Valve.VR;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// Loading a map in the headset. The game's loading scene (build index 2) films a 3D decoration from ten metres away
    /// with a slowly swaying camera and lays "LOADING ..." over it; the rig followed that camera, so the loading screen
    /// hung far off (its canvas became a HUD frame 4 m out) and swayed, and once the map's scene took over, the headset
    /// showed whatever that scene's first camera cleared to (a cyan-blue sky for two or three seconds) until the game had
    /// set the level up. In VR the loading screen is drawn as it looks flat, into a picture shown on a screen 2.5 m in
    /// front of you in a dark room, from the moment the loading scene starts until the map has something to show (you
    /// are deployed, or the loadout or a menu is up); in between the last loading picture stays on the screen.
    /// The screen is SteamVR's own overlay, the compositor's skybox black meanwhile: the game freezes for up to two seconds
    /// while the map's scene comes in (2.1 s in check-1022-loading), and the compositor, given no frames, faded to its own
    /// dark blue-grey surroundings and back, over the loading screen the game drew (user, 2026-10-22: "black / with blue
    /// shades glitching in front of the player view when a map is loading"). The compositor draws its overlay and skybox
    /// itself, frozen game or not. The game's own quad stands in where the compositor cannot take the picture.
    /// </summary>
    public sealed class LoadingView : IDisposable
    {
        private const int LoadingSceneIndex = 2;
        private const int MainMenuIndex = 1;
        /// <summary>The screen's own layer, the only one drawn while loading (nothing else in the game uses it).</summary>
        private const int ScreenLayer = 31;
        private const float Distance = 2.5f;
        private const float Width = 2.4f;

        /// <summary>The world is blacked out and only the loading screen is drawn.</summary>
        public static bool Active { get; private set; }

        /// <summary>The game's loading scene (its canvases belong to the loading view while it runs).</summary>
        public static bool IsLoadingScene(Scene scene) => scene.buildIndex == LoadingSceneIndex;

        /// <summary>Self-test: searches for the loading scene's cameras and canvases since start.</summary>
        internal static int Captures { get; private set; }

        private readonly VRRig _rig;
        private readonly Camera _uiCamera;
        private readonly List<Camera> _captured = new List<Camera>();
        private readonly HashSet<Canvas> _canvases = new HashSet<Canvas>();
        private readonly CanvasManager _canvasManager;
        private RenderTexture _texture;
        private GameObject _screen;
        private Material _material;
        private int _uiMask;
        private bool _placed;
        private float _levelSince = -1f, _readySince = -1f;
        /// <summary>When the view ends: a moment after the fade to black has started, so the switch to the map is not seen.</summary>
        private float _endAt = -1f;
        private int _sceneHandle = -1;
        /// <summary>When the loading scene's cameras and canvases are looked for again (each search makes two new arrays).</summary>
        private float _nextCapture;

        // Frames while it is up (logged when it ends): in the headset a long frame is a frozen or vanishing loading screen.
        private int _frames, _over100, _over500;
        private float _longest, _shownAt;
        private double _dropsMs, _dropsLongest;
        private readonly System.Diagnostics.Stopwatch _dropsWatch = new System.Diagnostics.Stopwatch();

        // The compositor's loading screen (its overlay) and black skybox.
        private ulong _overlay;
        private bool _overlayFailed, _overlayShown, _skybox;
        private ETextureType _textureType;
        private RenderTexture _overlayTexture;
        private Texture2D _black;

        public LoadingView(VRRig rig, Camera uiCamera, CanvasManager canvases)
        {
            _rig = rig;
            _uiCamera = uiCamera;
            _canvasManager = canvases;
        }

        // Made once: a method group passed as an Action makes a new delegate on every call (every frame of the loading screen).
        private static readonly System.Action BuildDrops = Weapons.WeaponDrops.BuildDuringLoading;

        /// <summary>Per frame, before the canvases are converted (the loading screen's canvases are drawn into the picture).</summary>
        public void Update()
        {
            Scene scene = SceneManager.GetActiveScene();
            float now = Time.unscaledTime;
            if (scene.buildIndex == LoadingSceneIndex)
            {
                if (!Active)
                {
                    Begin();
                }
                if (scene.handle != _sceneHandle)
                {
                    _sceneHandle = scene.handle;
                    _canvases.Clear();
                    _nextCapture = 0f;
                }
                // Four times a second, not every frame: the canvas manager leaves this scene's canvases alone while the
                // loading view runs, so a canvas that appears in between is not taken from it.
                if (now >= _nextCapture)
                {
                    _nextCapture = now + 0.25f;
                    Capture(scene);
                    Captures++;
                }
                _levelSince = -1f;
                _readySince = -1f;
                _endAt = -1f; // loading again before a pending end came: stay
            }
            else if (Active)
            {
                if (scene.buildIndex == MainMenuIndex || scene.buildIndex == 0)
                {
                    FadeThenEnd(now); // back to the menu (loading was cancelled or failed)
                }
                else
                {
                    // The map's scene: keep the last loading picture up until the game shows something.
                    if (_levelSince < 0f)
                    {
                        _levelSince = now;
                    }
                    bool ready = GameContext.Player != null || GameContext.GameMenuOpen || LoadoutOpen() || Spectating();
                    if (!ready)
                    {
                        _readySince = -1f;
                    }
                    else if (_readySince < 0f)
                    {
                        _readySince = now;
                    }
                    // A few frames more: the first frames of a scene often still draw from where its camera started.
                    if (ready && now - _readySince > 0.4f || now - _levelSince > 20f)
                    {
                        FadeThenEnd(now);
                    }
                }
            }
            if (Active && _endAt >= 0f && now >= _endAt)
            {
                End();
            }
            if (Active)
            {
                Place();
                UpdateOverlay();
                float dt = Time.unscaledDeltaTime;
                _frames++;
                _longest = Mathf.Max(_longest, dt);
                _over100 += dt > 0.1f ? 1 : 0;
                _over500 += dt > 0.5f ? 1 : 0;
                _dropsWatch.Reset();
                _dropsWatch.Start();
                Log.Guard("loading drops", BuildDrops);
                _dropsWatch.Stop();
                double ms = _dropsWatch.Elapsed.TotalMilliseconds;
                _dropsMs += ms;
                _dropsLongest = Math.Max(_dropsLongest, ms);
            }
        }

        /// <summary>
        /// The headset fades to black (VRRig.FadeCut: 0.05 s down, back up after 0.12 s), and the dark room gives way to
        /// the map while it is black: the switch was a hard cut.
        /// </summary>
        private void FadeThenEnd(float now)
        {
            if (_endAt >= 0f)
            {
                return;
            }
            _rig.FadeCut();
            _endAt = now + 0.06f;
        }

        private static bool Spectating()
        {
            try
            {
                return GameManager.IsSpectating();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool LoadoutOpen()
        {
            try
            {
                return LoadoutUi.instance != null && LoadoutUi.IsOpen();
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void Begin()
        {
            Active = true;
            _placed = false;
            _endAt = -1f;
            _nextCapture = 0f;
            _rig.FadeCut(); // the loading screen comes up out of black instead of popping in
            if (_texture == null)
            {
                _texture = new RenderTexture(1600, 900, 24, RenderTextureFormat.ARGB32) { name = "RavenfieldVR Loading Screen", antiAliasing = 2 };
            }
            // Black until the loading screen has drawn its first picture.
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = _texture;
            GL.Clear(true, true, Color.black);
            RenderTexture.active = previous;
            if (_screen == null)
            {
                _screen = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _screen.name = "RavenfieldVR Loading Screen";
                UnityEngine.Object.Destroy(_screen.GetComponent<Collider>());
                _screen.layer = ScreenLayer;
                Shader shader = Shader.Find("Unlit/Texture") ?? Shader.Find("Sprites/Default");
                _material = new Material(shader) { mainTexture = _texture };
                Renderer r = _screen.GetComponent<Renderer>();
                r.sharedMaterial = _material;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                _screen.transform.SetParent(_rig.Root, false);
                _screen.transform.localScale = new Vector3(Width, Width * 9f / 16f, 1f);
            }
            _screen.SetActive(true);
            _frames = _over100 = _over500 = 0;
            _longest = 0f;
            _dropsMs = _dropsLongest = 0.0;
            _shownAt = Time.unscaledTime; // (before the overlay's first picture: its fade-in counts from here)
            CompositorBegin();
            _screen.GetComponent<Renderer>().enabled = !_overlayShown; // the overlay is the screen: not drawn twice
            _uiMask = _uiCamera.cullingMask;
            _uiCamera.cullingMask = 1 << ScreenLayer;
            VRRig.Blackout = true;
            Log.Info("Loading screen: shown on a screen in front of you.");
        }

        /// <summary>
        /// The loading scene's own cameras draw into the picture instead of the screen, and its canvases are drawn by them
        /// (into the picture) instead of being turned into a far-away HUD frame.
        /// </summary>
        private void Capture(Scene scene)
        {
            Camera top = null;
            foreach (Camera c in Camera.allCameras)
            {
                if (c == null || c.gameObject.scene != scene || c.transform.IsChildOf(_rig.Root))
                {
                    continue;
                }
                if (c.targetTexture == null)
                {
                    c.targetTexture = _texture;
                    // Drawn flat into the picture, not as a headset eye and not moved by the head: the camera suppressor
                    // leaves render-texture cameras alone, and this one was captured before it ran.
                    c.stereoTargetEye = StereoTargetEyeMask.None;
                    XRDevice.DisableAutoXRCameraTracking(c, true);
                    _captured.Add(c);
                }
                if (c.targetTexture == _texture && (top == null || c.depth >= top.depth))
                {
                    top = c;
                }
            }
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == null || !canvas.isRootCanvas || canvas.gameObject.scene != scene || _canvases.Contains(canvas))
                {
                    continue;
                }
                // Never turned into a world-space panel, even while the scene's camera is not up yet (the canvas
                // manager scans right after this and would otherwise take it).
                _canvasManager.RegisterOwn(canvas);
                if (top == null)
                {
                    continue; // drawn into the picture once there is a camera
                }
                _canvases.Add(canvas);
                if (canvas.renderMode == RenderMode.WorldSpace)
                {
                    continue; // part of the 3D scene: the camera films it anyway
                }
                canvas.renderMode = RenderMode.ScreenSpaceCamera;
                canvas.worldCamera = top;
                canvas.planeDistance = Mathf.Max(top.nearClipPlane * 2f, 0.5f);
                top.cullingMask |= 1 << canvas.gameObject.layer;
            }
        }

        /// <summary>The screen stands at eye height in front of where you look when it appears, and comes back in front of you when you turn away.</summary>
        private void Place()
        {
            if (_screen == null)
            {
                return;
            }
            Transform head = _rig.Head;
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up);
            if (flat.sqrMagnitude < 1e-4f)
            {
                flat = _rig.Root.forward;
            }
            flat.Normalize();
            Transform t = _screen.transform;
            Vector3 toScreen = Vector3.ProjectOnPlane(t.position - head.position, Vector3.up);
            bool away = _placed && toScreen.sqrMagnitude > 1e-4f && Vector3.Angle(flat, toScreen) > 70f;
            if (!_placed || away && Time.unscaledTime - _awaySince > 1.5f || !_rig.HeadTracked)
            {
                t.SetPositionAndRotation(head.position + flat * Distance, Quaternion.LookRotation(flat, Vector3.up));
                _placed = _rig.HeadTracked;
                _awaySince = Time.unscaledTime;
            }
            else if (!away)
            {
                _awaySince = Time.unscaledTime;
            }
        }

        private float _awaySince;

        /// <summary>The compositor's texture type for this graphics API (false where it cannot take Unity's textures here).</summary>
        private static bool TextureType(out ETextureType type)
        {
            switch (SystemInfo.graphicsDeviceType)
            {
                case GraphicsDeviceType.Direct3D11:
                    type = ETextureType.DirectX;
                    return true;
                case GraphicsDeviceType.OpenGLCore:
                    type = ETextureType.OpenGL;
                    return true;
                default:
                    type = ETextureType.Invalid;
                    return false;
            }
        }

        /// <summary>
        /// The compositor's skybox black and the loading screen as its overlay (as Valve's own SteamVR_LoadLevel does): drawn
        /// by SteamVR at the headset's rate even while the game is frozen. Any failure leaves the game's quad as the screen.
        /// </summary>
        private void CompositorBegin()
        {
            _overlayShown = false;
            if (!TextureType(out _textureType))
            {
                Log.InfoOnce("loadingoverlay:api", $"Loading screen: drawn by the game ({SystemInfo.graphicsDeviceType}: no compositor overlay).");
                return;
            }
            CVRCompositor compositor = OpenVR.Compositor;
            if (compositor != null)
            {
                if (_black == null)
                {
                    _black = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = "RavenfieldVR Loading Skybox" };
                    var pixels = new Color32[16];
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = new Color32(0, 0, 0, 255);
                    }
                    _black.SetPixels32(pixels);
                    _black.Apply(false, true);
                }
                var sky = new[] { new Texture_t { handle = _black.GetNativeTexturePtr(), eType = _textureType, eColorSpace = EColorSpace.Auto } };
                EVRCompositorError error = compositor.SetSkyboxOverride(sky);
                _skybox = error == EVRCompositorError.None;
                if (!_skybox)
                {
                    Log.WarnOnce("loadingskybox", $"Loading screen: the compositor's black skybox was refused ({error}).");
                }
            }
            CVROverlay overlay = OpenVR.Overlay;
            if (overlay == null || _overlayFailed)
            {
                return;
            }
            if (_overlay == 0)
            {
                EVROverlayError error = overlay.CreateOverlay("ravenfieldvr.loading", "Ravenfield VR loading", ref _overlay);
                if (error != EVROverlayError.None)
                {
                    _overlayFailed = true;
                    _overlay = 0;
                    Log.Warn($"Loading screen: no compositor overlay ({error}); the game draws it.");
                    return;
                }
                overlay.SetOverlayWidthInMeters(_overlay, Width);
                // Unity's pictures are upside down to the compositor (Valve's SteamVR_Overlay reads them this way too).
                var bounds = new VRTextureBounds_t { uMin = 0f, uMax = 1f, vMin = 1f, vMax = 0f };
                overlay.SetOverlayTextureBounds(_overlay, ref bounds);
            }
            if (_overlayTexture == null)
            {
                // Without the picture's multisampling: the compositor takes a plain texture.
                _overlayTexture = new RenderTexture(_texture.width, _texture.height, 0, RenderTextureFormat.ARGB32) { name = "RavenfieldVR Loading Overlay" };
                _overlayTexture.Create();
            }
            _overlayShown = UpdateOverlay(first: true) && overlay.ShowOverlay(_overlay) == EVROverlayError.None;
            Log.Info(_overlayShown ? "Loading screen: drawn by the compositor (overlay, black skybox)." : "Loading screen: the overlay did not show; the game draws it.");
        }

        /// <summary>The picture and the screen's place into the overlay (the rig's root is the tracking space's origin).</summary>
        private bool UpdateOverlay(bool first = false)
        {
            if (!_overlayShown && !first || _overlay == 0 || _screen == null)
            {
                return false;
            }
            CVROverlay overlay = OpenVR.Overlay;
            if (overlay == null)
            {
                return false;
            }
            Graphics.Blit(_texture, _overlayTexture);
            // Faded with the view as the game's quad was (the compositor's fade to black does not reach an overlay): in after
            // the cut's black (0.12 s, then 0.25 s, as VRRig.FadeCut), out over the end's 0.06 s (FadeThenEnd).
            float now = Time.unscaledTime;
            float alpha = Mathf.Clamp01((now - _shownAt - 0.12f) / 0.25f);
            if (_endAt >= 0f)
            {
                alpha = Mathf.Min(alpha, Mathf.Clamp01((_endAt - now) / 0.06f));
            }
            overlay.SetOverlayAlpha(_overlay, alpha);
            var texture = new Texture_t { handle = _overlayTexture.GetNativeTexturePtr(), eType = _textureType, eColorSpace = EColorSpace.Auto };
            EVROverlayError error = overlay.SetOverlayTexture(_overlay, ref texture);
            Transform root = _rig.Root, t = _screen.transform;
            HmdMatrix34_t pose = OpenVRMath.FromUnity(root.InverseTransformPoint(t.position), Quaternion.Inverse(root.rotation) * t.rotation);
            if (error == EVROverlayError.None)
            {
                error = overlay.SetOverlayTransformAbsolute(_overlay, ETrackingUniverseOrigin.TrackingUniverseStanding, ref pose);
            }
            if (error != EVROverlayError.None)
            {
                // The game's quad again.
                Log.WarnOnce("loadingoverlay:" + error, $"Loading screen: the overlay failed ({error}); the game draws it.");
                overlay.HideOverlay(_overlay);
                _overlayShown = false;
                _screen.GetComponent<Renderer>().enabled = true;
                return false;
            }
            return true;
        }

        private void CompositorEnd()
        {
            if (_overlayShown && _overlay != 0)
            {
                OpenVR.Overlay?.HideOverlay(_overlay);
            }
            _overlayShown = false;
            if (_skybox)
            {
                OpenVR.Compositor?.ClearSkyboxOverride();
                _skybox = false;
            }
            if (_screen != null)
            {
                _screen.GetComponent<Renderer>().enabled = true;
            }
        }

        private void End()
        {
            Active = false;
            _endAt = -1f;
            VRRig.Blackout = false;
            if (_uiCamera != null)
            {
                _uiCamera.cullingMask = _uiMask != 0 ? _uiMask : 1 << 5;
            }
            foreach (Camera c in _captured)
            {
                if (c != null && c.targetTexture == _texture)
                {
                    c.targetTexture = null; // only if it outlived its scene
                }
            }
            _captured.Clear();
            _canvases.Clear();
            CompositorEnd();
            if (_screen != null)
            {
                _screen.SetActive(false);
            }
            Log.Info($"Loading screen: done, the map is shown. Up {Time.unscaledTime - _shownAt:0.0} s, {_frames} frames, the longest {_longest * 1000f:0} ms "
                     + $"({_over100} over 100 ms, {_over500} over 500 ms); the drops' models {_dropsMs:0} ms in all, at most {_dropsLongest:0} ms in a frame.");
        }

        public void Dispose()
        {
            if (Active)
            {
                End();
            }
            if (_overlay != 0)
            {
                OpenVR.Overlay?.DestroyOverlay(_overlay);
                _overlay = 0;
            }
            if (_overlayTexture != null)
            {
                _overlayTexture.Release();
                UnityEngine.Object.Destroy(_overlayTexture);
            }
            if (_black != null)
            {
                UnityEngine.Object.Destroy(_black);
            }
            if (_screen != null)
            {
                UnityEngine.Object.Destroy(_screen);
            }
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material);
            }
            if (_texture != null)
            {
                _texture.Release();
                UnityEngine.Object.Destroy(_texture);
            }
        }
    }
}
