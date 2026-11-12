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
        internal readonly Sandbox.SandboxPanel SandboxPanel;
        internal readonly MultiplayerPanel MultiplayerPanel;
        internal readonly StartChoicePanel StartChoice;
        private static bool _startChoiceShown; // (once each time the game starts: it survives an F6 reload of the mod's VR)
        private readonly Sandbox.SandboxTool _sandboxTool;
        internal Sandbox.SandboxTool SandboxTool => _sandboxTool;
        public readonly LoadingView Loading;
        private readonly VRKeyboard _keyboard;
        private readonly LaserPointer _laser;
        private readonly WristHud _wrist;
        private readonly SquadPointing _squad;
        private readonly SeatSights _sights = new SeatSights();
        /// <summary>Text held in front of the eyes for a while (a start-up problem the player has to know of).</summary>
        internal HeadNotice Notice { get; private set; }
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
            Vector3 raw = _rig.Root.InverseTransformDirection(pointer.TrackedPointer.Forward);
            float dt = Time.unscaledDeltaTime;
            if (!live || !_laserSteady || _laserHand != pointer.Side || dt <= 1e-5f || dt > 0.25f)
            {
                // Starts where the hand points (a new pointing hand, back from a pause or a hitch).
                _laserDir = raw;
                _laserDirVelocity = Vector3.zero;
                _laserHand = pointer.Side;
                _laserSteady = live;
                return pointer.TrackedPointer.Forward;
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
            try
            {
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
                // No occlusion culling for it: a map's baked occlusion is for the world, and a panel behind a baked occluder (a wall
                // between the head and a converted menu, never between it and a panel at arm's length) was culled by it; on a map
                // with baked occlusion it was a query per eye too.
                UiCamera.useOcclusionCulling = false;
                // No shadow maps for it (the UI layer casts and takes none): rendered for it as for the eyes, they doubled its cost with
                // a preset's shadows on (0.53 ms of main thread a frame with Game, about 1.0 with Medium or Ultra; the user's session,
                // 2026-10-27).
                uiGo.AddComponent<Weapons.VRScope.ScopeNoShadows>();
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
                SandboxPanel = new Sandbox.SandboxPanel(rig, EventCamera);
                Sandbox.Sandbox.InitMode();
                Canvases.RegisterOwn(SandboxPanel.Canvas);
                _sandboxTool = new Sandbox.SandboxTool(rig);
                Net.NetGame.Init();
                MultiplayerPanel = new MultiplayerPanel(rig, EventCamera);
                Canvases.RegisterOwn(MultiplayerPanel.Canvas);
                StartChoice = new StartChoicePanel(rig, EventCamera);
                Canvases.RegisterOwn(StartChoice.Canvas);
                _keyboard = new VRKeyboard();
                _laser = new LaserPointer();
                _wrist = new WristHud(rig);
                Notice = new HeadNotice();
                _squad = new SquadPointing(rig);
            }
            catch
            {
                Dispose(); // VRManager never gets this object to dispose: what was built goes here
                throw;
            }
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
                ModKeys.Close(); // (one panel at a time: the two overlapped, the nearer taking the laser; panels scout, 2026-11-06)
                MultiplayerPanel.Close();
                // With the sandbox's tool in hand (or its panel up) the menu button is the sandbox's: the panel is where the tool is set.
                if (SandboxPanel.IsOpen && Sandbox.Sandbox.Armed == Sandbox.Sandbox.ToolKind.None)
                {
                    // (The tool put away with the panel still up: the button is the settings' again at once, not after a second press.)
                    SandboxPanel.Close();
                    Settings.Open();
                }
                else if (SandboxPanel.IsOpen || (Sandbox.Sandbox.Armed != Sandbox.Sandbox.ToolKind.None && !Settings.IsOpen))
                {
                    SandboxPanel.Toggle();
                }
                else
                {
                    Settings.Toggle();
                }
            }
            if (VRInput.GetPressed(DigitalAction.ModKeys))
            {
                VRInput.Consume(DigitalAction.ModKeys);
                Settings.Close();
                SandboxPanel.Close();
                MultiplayerPanel.Close();
                ModKeys.Toggle();
            }
            if (Settings.IsOpen && (Settings.Changed || Time.unscaledTime >= _nextSettingsRefresh))
            {
                _nextSettingsRefresh = Time.unscaledTime + 2f; // at once after a press; otherwise for changes made elsewhere
                Settings.Refresh();
            }
            ModKeys.Update(_module != null && _module.PressActive);
            SandboxPanel.Update(_module != null && _module.PressActive);
            MultiplayerPanel.Update(_module != null && _module.PressActive);
            StartChoice.Update(_module != null && _module.PressActive);
            Net.Multiplayer.Update();

            // The choice of single player or multiplayer, over the main menu, once each time the game starts.
            if (!_startChoiceShown && !AutoTest.Requested && !GameContext.InGame && MainMenu.instance != null && Canvases.AnyMenuVisible)
            {
                _startChoiceShown = true;
                if (!Net.Multiplayer.Active)
                {
                    StartChoice.Open();
                }
            }
            // A player who joined a game waits for the host outside a match: the game's own menus are the host's to work, his
            // are hidden and the multiplayer panel stands open with what the host is choosing.
            // (Only in the menu's own scene: in the first frames of the host's match the player is not there yet, the panel was
            // opened again right after the scene change had shut it, and stood in the match with the laser on.)
            bool following = MultiplayerPanel.Following;
            Canvases.HideMenus = following;
            Canvases.ApplyHideMenus();
            if (following && !MultiplayerPanel.IsOpen && !Settings.IsOpen && !ModKeys.IsOpen && !SandboxPanel.IsOpen)
            {
                StartChoice.Close();
                MultiplayerPanel.Open();
            }
            // One panel at a time: the start's choice gives way to any other that is opened over it.
            if (StartChoice.IsOpen && (Settings.IsOpen || ModKeys.IsOpen || SandboxPanel.IsOpen || MultiplayerPanel.IsOpen))
            {
                StartChoice.Close();
            }

            MenuMode = Canvases.AnyMenuVisible || Settings.IsOpen || ModKeys.IsOpen || SandboxPanel.IsOpen || MultiplayerPanel.IsOpen || StartChoice.IsOpen
                       || (!GameContext.InGame && Canvases.AnyVisible);
            VRGameInput.PanelOpen = Settings.IsOpen || ModKeys.IsOpen || SandboxPanel.IsOpen || MultiplayerPanel.IsOpen || StartChoice.IsOpen;
            Sandbox.Sandbox.Update();
            _sandboxTool.Update(MenuMode);

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
                if (StartChoice.IsOpen)
                {
                    _targets.Add(StartChoice.Canvas); // (alone: the game's menu waits behind the choice)
                }
                else
                {
                    Canvases.CollectVisible(_targets, menusOnly: true);
                }
                if (Settings.IsOpen)
                {
                    _targets.Add(Settings.Canvas);
                }
                if (ModKeys.IsOpen)
                {
                    _targets.Add(ModKeys.Canvas);
                }
                if (SandboxPanel.IsOpen)
                {
                    _targets.Add(SandboxPanel.Canvas);
                }
                if (MultiplayerPanel.IsOpen)
                {
                    _targets.Add(MultiplayerPanel.Canvas);
                }
                _module.Targets = _targets;
                _module.PointerActive = MenuMode && pointer.Tracked;
                _module.PointerHand = _pointerHand;
                // (The controller's own pointer: with slowed hands in slow motion the laser is input, not the body, and does not lag.)
                _module.PointerRay = new Ray(pointer.TrackedPointer.position, SteadyPointerDirection(pointer, MenuMode && pointer.Tracked));
            }
            if (!MenuMode)
            {
                VRGameInput.PointerOverUI = false;
            }
        }

        private float _nextSettingsRefresh;
        private float _nextEventSystemSearch;

        /// <summary>Attaches the laser module to whichever EventSystem the current scene uses.</summary>
        private void EnsureModule()
        {
            EventSystem es = EventSystem.current;
            if (es == null && Time.unscaledTime >= _nextEventSystemSearch)
            {
                _nextEventSystemSearch = Time.unscaledTime + 0.5f; // a scene-wide search: not every frame while there is none
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

        private static readonly System.Action AvatarStep = Net.NetAvatar.BeforeRender;

        public void BeforeRender()
        {
            // (The notice first: a step below that threw each frame left it up for good, never timed out; HUD scout, 2026-11-12.)
            Notice?.BeforeRender(_rig.Head);
            // (The other players' hands, guns and heads on their own guard, before the panels: after ten other steps in one guard,
            // any of them throwing each frame stopped every remote hand; remote visuals scout, 2026-11-12.)
            Log.Guard("remote avatars", AvatarStep);
            Canvases.BeforeRender();
            Settings.BeforeRender();
            _sights.BeforeRender(_rig.Head);
            ModKeys.BeforeRender();
            SandboxPanel.BeforeRender();
            MultiplayerPanel.BeforeRender();
            StartChoice.BeforeRender();
            _sandboxTool.BeforeRender();
            VRHand pointer = _rig.Hand(_pointerHand);
            bool show = _module != null && _module.PointerActive && pointer.Tracked;
            Vector3 end = Vector3.zero;
            bool hit = false;
            if (show)
            {
                // Drawn from this frame's hand against this frame's panels; the input pass ran earlier in the frame
                // from the previous pose, and mixing the two bent the line whenever the view moved.
                // The input pass's steadied direction (kept in rig space, so it turns with you), from where the hand is now.
                var ray = new Ray(pointer.TrackedPointer.position, _laserSteady ? _rig.Root.TransformDirection(_laserDir) : pointer.TrackedPointer.Forward);
                hit = _module.TryHit(ray, out end);
                if (!hit)
                {
                    end = ray.origin + ray.direction * 3f;
                }
            }
            _laser.Update(show, pointer.TrackedPointer.position, end, hit, _module != null && _module.HoveredObject != null);
            Log.Guard("UI.Wrist", WristStep ?? (WristStep = _wrist.BeforeRender)); // (each on its own guard, as the steps before them)
            Log.Guard("UI.Squad", SquadStep ?? (SquadStep = _squad.BeforeRender));
        }

        private System.Action WristStep, SquadStep;

        public void OnSceneChanged()
        {
            // (First, and on its own: a throw further down left a multiplayer PC waiting for a scene change that had already come.)
            Log.Guard("Net.OnSceneChanged", Net.NetGame.OnSceneChanged);
            // A panel opened in the menu or while loading does not stay up in the match (its laser and pointer hand with it).
            if (Settings.IsOpen)
            {
                Settings.Close();
            }
            if (ModKeys.IsOpen)
            {
                ModKeys.Close();
            }
            if (SandboxPanel.IsOpen)
            {
                SandboxPanel.Close();
            }
            if (MultiplayerPanel.IsOpen)
            {
                MultiplayerPanel.Close();
            }
            if (StartChoice.IsOpen)
            {
                StartChoice.Close();
            }
            Sandbox.Sandbox.OnSceneChanged();
            _sandboxTool.OnSceneChanged();
            Canvases.OnSceneChanged();
            _squad.OnSceneChanged(); // the capture points' flags and any hand sign in progress belong to the old scene
            _eventSystem = null;
        }

        public void Dispose()
        {
            // Each step on its own and only what was made: also the teardown of a constructor that threw part way, whose laser
            // and squad markers (DontDestroyOnLoad) stayed with Instance still set, one more set on every F6 retry (UI scout).
            Log.Guard("UI.RestoreModules", RestoreModules);
            Log.Guard("UI.Loading.Dispose", () => Loading?.Dispose());
            Log.Guard("UI.Canvases.Dispose", () => Canvases?.Dispose());
            Log.Guard("UI.Settings.Dispose", () => Settings?.Dispose());
            Log.Guard("UI.Sights.Restore", () => _sights?.Restore());
            Log.Guard("UI.ModKeys.Dispose", () => ModKeys?.Dispose());
            Log.Guard("UI.Sandbox.Dispose", () => SandboxPanel?.Dispose());
            Log.Guard("UI.Multiplayer.Dispose", () => MultiplayerPanel?.Dispose());
            Log.Guard("UI.StartChoice.Dispose", () => StartChoice?.Dispose());
            Log.Guard("UI.SandboxTool.Destroy", () => _sandboxTool?.Destroy());
            Log.Guard("UI.Keyboard.Dispose", () => _keyboard?.Dispose());
            Log.Guard("UI.Laser.Destroy", () => _laser?.Destroy());
            Log.Guard("UI.Wrist.Destroy", () => _wrist?.Destroy());
            Log.Guard("UI.Notice.Destroy", () => Notice?.Destroy());
            Log.Guard("UI.Squad.Destroy", () => _squad?.Destroy());
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
            _material = new Material(Shader.Find("Sprites/Default")) { color = Color.white }; // the line's colours tint it; set once
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
            // Parsed once per value of the setting (it was parsed from its text every frame the laser showed).
            string colorText = VRConfig.LaserColor.Value;
            if (!ReferenceEquals(colorText, _colorText))
            {
                _colorText = colorText;
                _color = VRConfig.ParseColor(VRConfig.LaserColor, UiKit.Accent);
            }
            Color c = _color;
            Color start = c;
            start.a = hit ? 0.9f : 0.35f;
            Color tail = c;
            tail.a = hit ? 0.6f : 0f;
            _line.startColor = start;
            _line.endColor = tail;
            _line.SetPosition(0, origin);
            _line.SetPosition(1, end);
            if (_dot.activeSelf != hit)
            {
                _dot.SetActive(hit);
            }
            if (hit)
            {
                _dot.transform.position = end;
                float scale = overInteractive ? 0.014f : 0.009f;
                _dot.transform.localScale = Vector3.one * scale;
            }
        }

        private string _colorText;
        private Color _color;

        public void Destroy()
        {
            if (_go != null)
            {
                UnityEngine.Object.Destroy(_go);
            }
            if (_material != null)
            {
                UnityEngine.Object.Destroy(_material); // (made here, not an asset: it outlived the laser)
            }
        }
    }
}
