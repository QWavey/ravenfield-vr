using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;
using UnityEngine.EventSystems;

namespace RavenfieldVR.UI
{
    /// <summary>
    /// All VR UI: the UI camera that draws layer 5 on top of the world, the hidden event camera used for
    /// raycasts, world-space canvas conversion, the laser pointer input module and its visuals, the SteamVR
    /// keyboard bridge, the in-VR settings panel and the wrist HUD.
    /// </summary>
    public sealed class UISystem : IDisposable
    {
        public static UISystem Instance { get; private set; }

        private readonly VRRig _rig;
        public readonly Camera UiCamera;
        public readonly Camera EventCamera;
        public readonly CanvasManager Canvases;
        public readonly VRSettingsPanel Settings;
        public readonly ModKeysPanel ModKeys;
        public readonly LoadingView Loading;
        private readonly VRKeyboard _keyboard;
        private readonly LaserPointer _laser;
        private readonly WristHud _wrist;
        private readonly SquadPointing _squad;
        private readonly SeatSights _sights = new SeatSights();
        private VRInputModule _module;
        private EventSystem _eventSystem;
        private readonly List<BaseInputModule> _disabledModules = new List<BaseInputModule>();
        private Hand _pointerHand;

        // The menu laser's direction, steadied (rig space): a One-Euro filter, firm when the hand is still, following at
        // once when it moves. Simulated tremor at a panel 2 m away: 4.7 mm of jitter before, 1.1 mm after, and 5.4 mm
        // behind (3.8 mm unfiltered) while sweeping across it at 60 degrees a second.
        private Vector3 _laserDir, _laserDirVelocity;
        private bool _laserSteady;
        private Hand _laserHand;
        private const float LaserMinCutoff = 1f; // Hz
        private const float LaserBeta = 1f;      // Hz more per degree a second

        private Vector3 SteadyPointerDirection(VRHand pointer, bool live)
        {
            Vector3 raw = _rig.Root.InverseTransformDirection(pointer.Pointer.Forward);
            float dt = Time.unscaledDeltaTime;
            if (!live || !_laserSteady || _laserHand != pointer.Side || dt <= 1e-5f || dt > 0.25f)
            {
                // Starts where the hand points (a new pointing hand, back from a pause or a hitch).
                _laserDir = raw;
                _laserDirVelocity = Vector3.zero;
                _laserHand = pointer.Side;
                _laserSteady = live;
                return pointer.Pointer.Forward;
            }
            _laserDirVelocity = Vector3.Lerp(_laserDirVelocity, (raw - _laserDir) / dt, SmoothingAlpha(1f, dt));
            float cutoff = LaserMinCutoff + LaserBeta * _laserDirVelocity.magnitude * Mathf.Rad2Deg;
            _laserDir = Vector3.Lerp(_laserDir, raw, SmoothingAlpha(cutoff, dt)).normalized;
            return _rig.Root.TransformDirection(_laserDir);
        }

        /// <summary>One-Euro smoothing factor for a cutoff frequency over a frame of <paramref name="dt"/> seconds.</summary>
        private static float SmoothingAlpha(float cutoff, float dt) => 1f / (1f + 1f / (2f * Mathf.PI * cutoff * dt));
        private readonly List<Canvas> _targets = new List<Canvas>();

        public bool MenuMode { get; private set; }

        public UISystem(VRRig rig)
        {
            Instance = this;
            _rig = rig;
            _pointerHand = VRInput.Dominant;

            var uiGo = new GameObject("VR UI Camera");
            uiGo.transform.SetParent(rig.Root, false);
            UiCamera = uiGo.AddComponent<Camera>();
            UiCamera.stereoTargetEye = StereoTargetEyeMask.Both;
            UiCamera.clearFlags = CameraClearFlags.Depth;
            UiCamera.cullingMask = 1 << 5;
            UiCamera.depth = rig.Camera.depth + 1f;
            UiCamera.nearClipPlane = 0.01f;
            UiCamera.farClipPlane = 200f;
            UiCamera.allowHDR = false;
            UiCamera.allowMSAA = true;
            rig.Suppressor.Ignore(UiCamera);
            rig.AddHeadCamera(UiCamera); // the UI is drawn from exactly the same eye pose as the world

            var evGo = new GameObject("VR UI Event Camera");
            evGo.transform.SetParent(rig.Root, false);
            EventCamera = evGo.AddComponent<Camera>();
            EventCamera.enabled = false;
            EventCamera.stereoTargetEye = StereoTargetEyeMask.None;
            EventCamera.fieldOfView = 60f;
            EventCamera.nearClipPlane = 0.01f;
            EventCamera.farClipPlane = 50f;
            EventCamera.cullingMask = 0;
            rig.Suppressor.Ignore(EventCamera);

            Canvases = new CanvasManager(rig, EventCamera);
            Loading = new LoadingView(rig, UiCamera, Canvases);
            Settings = new VRSettingsPanel(rig, EventCamera);
            Canvases.RegisterOwn(Settings.Canvas);
            ModKeys = new ModKeysPanel(rig, EventCamera);
            Canvases.RegisterOwn(ModKeys.Canvas);
            _keyboard = new VRKeyboard();
            _laser = new LaserPointer();
            _wrist = new WristHud(rig);
            _squad = new SquadPointing(rig);
        }

        public void Update()
        {
            EnsureModule();
            Loading.Update(); // first: the loading screen's canvases are drawn into its picture, not converted
            Canvases.Update();
            Canvases.UpdateMatchBar();
            _keyboard.Update();

            if (VRInput.GetPressed(DigitalAction.VRMenu))
            {
                VRInput.Consume(DigitalAction.VRMenu);
                Settings.Toggle();
            }
            if (VRInput.GetPressed(DigitalAction.ModKeys))
            {
                VRInput.Consume(DigitalAction.ModKeys);
                ModKeys.Toggle();
            }
            if (Settings.IsOpen)
            {
                Settings.Refresh();
            }
            ModKeys.Update();

            MenuMode = Canvases.AnyMenuVisible || Settings.IsOpen || ModKeys.IsOpen || (!GameContext.InGame && Canvases.AnyVisible);
            VRGameInput.PanelOpen = Settings.IsOpen || ModKeys.IsOpen;

            // Whichever hand pulls its trigger becomes the pointer.
            if (_module != null && MenuMode)
            {
                Hand other = VRInput.Other(_pointerHand);
                if (_module.TriggerPressed(other) && _rig.Hand(other).Tracked)
                {
                    _pointerHand = other;
                }
                if (!_rig.Hand(_pointerHand).Tracked && _rig.Hand(other).Tracked)
                {
                    _pointerHand = other;
                }
            }

            foreach (VRHand h in _rig.Hands)
            {
                if (h.Role == HandRole.Pointer && (!MenuMode || h.Side != _pointerHand))
                {
                    h.Role = HandRole.Free;
                }
            }
            VRHand pointer = _rig.Hand(_pointerHand);
            if (MenuMode && pointer.Role == HandRole.Free)
            {
                pointer.Role = HandRole.Pointer;
            }

            if (_module != null)
            {
                _targets.Clear();
                Canvases.CollectVisible(_targets, menusOnly: true);
                if (Settings.IsOpen)
                {
                    _targets.Add(Settings.Canvas);
                }
                if (ModKeys.IsOpen)
                {
                    _targets.Add(ModKeys.Canvas);
                }
                _module.Targets = _targets;
                _module.PointerActive = MenuMode && pointer.Tracked;
                _module.PointerHand = _pointerHand;
                _module.PointerRay = new Ray(pointer.Pointer.position, SteadyPointerDirection(pointer, MenuMode && pointer.Tracked));
            }
            if (!MenuMode)
            {
                VRGameInput.PointerOverUI = false;
            }
        }

        /// <summary>Attaches the laser module to whichever EventSystem the current scene uses.</summary>
        private void EnsureModule()
        {
            EventSystem es = EventSystem.current;
            if (es == null)
            {
                es = UnityEngine.Object.FindObjectOfType<EventSystem>();
            }
            if (es == null || (es == _eventSystem && _module != null))
            {
                return;
            }
            RestoreModules();
            _eventSystem = es;
            _module = es.GetComponent<VRInputModule>() ?? es.gameObject.AddComponent<VRInputModule>();
            _module.EventCamera = EventCamera;
            foreach (BaseInputModule m in es.GetComponents<BaseInputModule>())
            {
                if (m != _module && m.enabled)
                {
                    m.enabled = false;
                    _disabledModules.Add(m);
                }
            }
            _module.enabled = true;
            es.UpdateModules();
            Log.Debug("VR input module attached to " + es.name);
        }

        private void RestoreModules()
        {
            foreach (BaseInputModule m in _disabledModules)
            {
                if (m != null)
                {
                    m.enabled = true;
                }
            }
            _disabledModules.Clear();
            if (_module != null)
            {
                UnityEngine.Object.Destroy(_module);
                _module = null;
            }
        }

        public void BeforeRender()
        {
            Canvases.BeforeRender();
            Settings.BeforeRender();
            _sights.BeforeRender(_rig.Head);
            ModKeys.BeforeRender();
            VRHand pointer = _rig.Hand(_pointerHand);
            bool show = _module != null && _module.PointerActive && pointer.Tracked;
            Vector3 end = Vector3.zero;
            bool hit = false;
            if (show)
            {
                // Drawn from this frame's hand against this frame's panels; the input pass ran earlier in the frame
                // from the previous pose, and mixing the two bent the line whenever the view moved.
                // The input pass's steadied direction (kept in rig space, so it turns with you), from where the hand is now.
                var ray = new Ray(pointer.Pointer.position, _laserSteady ? _rig.Root.TransformDirection(_laserDir) : pointer.Pointer.Forward);
                hit = _module.TryHit(ray, out end);
                if (!hit)
                {
                    end = ray.origin + ray.direction * 3f;
                }
            }
            _laser.Update(show, pointer.Pointer.position, end, hit, _module != null && _module.HoveredObject != null);
            _wrist.BeforeRender();
            _squad.BeforeRender();
        }

        public void OnSceneChanged()
        {
            Canvases.OnSceneChanged();
            _eventSystem = null;
        }

        public void Dispose()
        {
            RestoreModules();
            Loading.Dispose();
            Canvases.Dispose();
            Settings.Dispose();
            _sights.Restore();
            ModKeys.Dispose();
            _keyboard.Dispose();
            _laser.Destroy();
            _wrist.Destroy();
            _squad.Destroy();
            if (UiCamera != null)
            {
                UnityEngine.Object.Destroy(UiCamera.gameObject);
            }
            if (EventCamera != null)
            {
                UnityEngine.Object.Destroy(EventCamera.gameObject);
            }
            VRGameInput.PointerOverUI = false;
            VRGameInput.PanelOpen = false;
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }

    /// <summary>Laser line and hit dot, drawn by the UI camera.</summary>
    public sealed class LaserPointer
    {
        private readonly GameObject _go;
        private readonly LineRenderer _line;
        private readonly GameObject _dot;
        private readonly Material _material;

        public LaserPointer()
        {
            _go = new GameObject("RavenfieldVR Laser");
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _go.layer = 5;
            _material = new Material(Shader.Find("Sprites/Default"));
            _line = _go.AddComponent<LineRenderer>();
            _line.sharedMaterial = _material;
            _line.positionCount = 2;
            _line.useWorldSpace = true;
            _line.widthMultiplier = 0.0035f;
            _line.numCapVertices = 2;
            _line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _line.receiveShadows = false;

            _dot = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            UnityEngine.Object.Destroy(_dot.GetComponent<Collider>());
            _dot.layer = 5;
            _dot.transform.SetParent(_go.transform, false);
            _dot.transform.localScale = Vector3.one * 0.011f;
            _dot.GetComponent<Renderer>().sharedMaterial = _material;
            _go.SetActive(false);
        }

        public void Update(bool show, Vector3 origin, Vector3 end, bool hit, bool overInteractive)
        {
            if (_go.activeSelf != show)
            {
                _go.SetActive(show);
            }
            if (!show)
            {
                return;
            }
            Color c = VRConfig.ParseColor(VRConfig.LaserColor, new Color(0.91f, 0.71f, 0.29f));
            Color start = c;
            start.a = hit ? 0.9f : 0.35f;
            Color tail = c;
            tail.a = hit ? 0.6f : 0f;
            _line.startColor = start;
            _line.endColor = tail;
            _line.SetPosition(0, origin);
            _line.SetPosition(1, end);
            _dot.SetActive(hit);
            if (hit)
            {
                _dot.transform.position = end;
                float scale = overInteractive ? 0.014f : 0.009f;
                _dot.transform.localScale = Vector3.one * scale;
                _material.color = Color.white;
            }
        }

        public void Destroy()
        {
            if (_go != null)
            {
                UnityEngine.Object.Destroy(_go);
            }
        }
    }
}
