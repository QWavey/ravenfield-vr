using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Rig;
using RavenfieldVR.Util;
using UnityEngine;
using UnityEngine.SceneManagement;

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
        private int _sceneHandle = -1;

        public LoadingView(VRRig rig, Camera uiCamera, CanvasManager canvases)
        {
            _rig = rig;
            _uiCamera = uiCamera;
            _canvasManager = canvases;
        }

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
                }
                Capture(scene);
                _levelSince = -1f;
                _readySince = -1f;
            }
            else if (Active)
            {
                if (scene.buildIndex == MainMenuIndex || scene.buildIndex == 0)
                {
                    End(); // back to the menu (loading was cancelled or failed)
                }
                else
                {
                    // The map's scene: keep the last loading picture up until the game shows something.
                    if (_levelSince < 0f)
                    {
                        _levelSince = now;
                    }
                    bool ready = GameContext.Player != null || GameContext.GameMenuOpen || LoadoutOpen() || GameManager.IsSpectating();
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
                        End();
                    }
                }
            }
            if (Active)
            {
                Place();
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
                    _captured.Add(c);
                }
                if (c.targetTexture == _texture && (top == null || c.depth >= top.depth))
                {
                    top = c;
                }
            }
            if (top == null)
            {
                return;
            }
            foreach (Canvas canvas in UnityEngine.Object.FindObjectsOfType<Canvas>())
            {
                if (canvas == null || !canvas.isRootCanvas || canvas.gameObject.scene != scene || _canvases.Contains(canvas))
                {
                    continue;
                }
                _canvases.Add(canvas);
                _canvasManager.RegisterOwn(canvas); // never turned into a world-space panel
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

        private void End()
        {
            Active = false;
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
            if (_screen != null)
            {
                _screen.SetActive(false);
            }
            Log.Info("Loading screen: done, the map is shown.");
        }

        public void Dispose()
        {
            if (Active)
            {
                End();
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
