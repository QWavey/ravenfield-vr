using System;
using System.Collections;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using RavenfieldVR.UI;
using RavenfieldVR.Util;
using RavenfieldVR.Weapons;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// Boots SteamVR and drives every VR system in a fixed order each frame:
    /// Update (input, rig, interactions) -> LateUpdate (camera suppression, post-animation logic)
    /// -> onBeforeRender (final poses for rendering: rig, hands, weapon, UI).
    /// </summary>
    public sealed class VRManager : MonoBehaviour
    {
        public static VRManager Instance { get; private set; }
        public static bool Active => Instance != null && Instance._active;

        public bool RequestVR;

        public VRRig Rig { get; private set; }
        public WeaponSystem Weapons { get; private set; }
        public UISystem UI { get; private set; }

        private bool _active;
        private bool _subscribed;

        private void Awake()
        {
            Instance = this;
        }

        private IEnumerator Start()
        {
            // Let the graphics device finish its first frame before the XR display attaches to it.
            yield return null;
            yield return new WaitForEndOfFrame();
            if (!RequestVR)
            {
                Log.Info("VR disabled (config or install check); running flat.");
                yield break;
            }
            if (!ShouldStartVR(out string reason))
            {
                Log.Info("Not starting VR: " + reason + " Launch with -vr to force it.");
                yield break;
            }
            if (!OpenVRRuntime.Start())
            {
                Log.Warn("Continuing in flat mode: " + OpenVRRuntime.LastError);
                yield break;
            }

            bool inputReady = VRInput.Initialize();
            try
            {
                Rig = new VRRig();
                Weapons = new WeaponSystem(Rig);
                UI = new UISystem(Rig);
                CacheFrameDelegates();
            }
            catch (Exception e)
            {
                Log.Exception("Creating VR systems failed; shutting VR down", e);
                Shutdown();
                yield break;
            }

            VRGameInput.Enabled = inputReady;
            _active = true;
            ScriptKeys.Enabled = true;
            Application.onBeforeRender += OnBeforeRender;
            SceneManager.activeSceneChanged += OnSceneChanged;
            OpenVRRuntime.DashboardChanged += OnDashboard;
            VRConfig.RenderScale.SettingChanged += OnRenderSettingChanged;
            VRConfig.Mirror.SettingChanged += OnRenderSettingChanged;
            VRConfig.Graphics.SettingChanged += OnGraphicsPresetChanged;
            GraphicsPresets.Apply();
            _subscribed = true;
            Log.Info("VR active.");
            // Content may have finished loading before VR came up; the warm-up skips rigs already analysed.
            WeaponWarmup.Start();
        }

        /// <summary>
        /// OpenVR initialisation launches SteamVR, so only do it when VR is clearly wanted: a headset is present,
        /// SteamVR is already running, or the game was started with -vr. -novr always plays flat.
        /// </summary>
        private static bool ShouldStartVR(out string reason)
        {
            string[] args = Environment.GetCommandLineArgs();
            bool Has(string flag) => Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            if (Has("-novr"))
            {
                reason = "-novr on the command line.";
                return false;
            }
            if (Has("-vr"))
            {
                reason = null;
                return true;
            }
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("vrserver").Length > 0)
                {
                    reason = null;
                    return true;
                }
                // IsHmdPresent() can report true for an installed-but-idle headset driver (Oculus/Link, Steam Link),
                // and VR_Init would then pop SteamVR open during flat play, so probing is opt-in.
                if (VRConfig.AutoStartSteamVR.Value && Valve.VR.OpenVR.IsRuntimeInstalled() && Valve.VR.OpenVR.IsHmdPresent())
                {
                    reason = null;
                    return true;
                }
            }
            catch (Exception e)
            {
                reason = "could not query SteamVR (" + e.Message + ").";
                return false;
            }
            reason = "SteamVR is not running.";
            return false;
        }

        private void Update()
        {
            if (!_active)
            {
                return;
            }
            PerfProbe.Frame();
            OpenVRRuntime.PumpEvents();
            OpenVRRuntime.EnforceFramePacing();
            Log.Guard("Input", _inputUpdate);
            Log.Guard("Rig.Update", _rigUpdate);
            Log.Guard("UI.Update", _uiUpdate);
            Log.Guard("Weapons.Update", _weaponsUpdate);
        }

        // Per-frame work goes to Log.Guard as delegates made once: passing a method group allocates a new delegate
        // (garbage) on every call, ten times a frame.
        private Action _inputUpdate, _rigUpdate, _uiUpdate, _weaponsUpdate, _suppressor, _weaponsLate, _rigRender, _weaponsRender, _uiRender;
        private Action[] _handApply;

        private static void UpdateInput()
        {
            VRInput.Update();
            VRGameInput.Compute();
            ScriptKeys.Update();
            // Mod scripts that read Shift for sprinting (tactical sprint animations) see the VR sprint.
            ScriptKeys.SetHeld("left shift", VRGameInput.SprintActive && Util.GameContext.PlayerOnFoot);
        }

        private void CacheFrameDelegates()
        {
            _inputUpdate = UpdateInput;
            _rigUpdate = Rig.Update;
            _uiUpdate = UI.Update;
            _weaponsUpdate = Weapons.Update;
            _suppressor = Rig.Suppressor.Refresh;
            _weaponsLate = Weapons.LateUpdate;
            _rigRender = Rig.BeforeRender;
            _weaponsRender = Weapons.BeforeRender;
            _uiRender = UI.BeforeRender;
            _handApply = new Action[Rig.Hands.Length];
            for (int i = 0; i < Rig.Hands.Length; i++)
            {
                _handApply[i] = Rig.Hands[i].Model.Apply;
            }
        }

        private void LateUpdate()
        {
            if (!_active)
            {
                return;
            }
            Log.Guard("Suppressor", _suppressor);
            Log.Guard("Weapons.LateUpdate", _weaponsLate);
        }

        private void OnBeforeRender()
        {
            if (!_active)
            {
                return;
            }
            Log.Guard("Rig.BeforeRender", _rigRender);
            Log.Guard("Weapons.BeforeRender", _weaponsRender);
            Log.Guard("UI.BeforeRender", _uiRender);
            // The gun hand first: a support hand cupping it closes on where it is this frame.
            int first = (int)Input.VRInput.Dominant;
            Log.Guard("HandModel", _handApply[first]);
            for (int i = 0; i < _handApply.Length; i++)
            {
                if (i != first)
                {
                    Log.Guard("HandModel", _handApply[i]);
                }
            }
        }

        private void OnSceneChanged(Scene from, Scene to)
        {
            Log.Debug($"Scene {from.name} -> {to.name}");
            Weapons?.OnSceneChanged();
            UI?.OnSceneChanged();
        }

        // Whether the match is paused because the SteamVR dashboard is open (not by the player).
        private bool _pausedForDashboard;

        /// <summary>
        /// The SteamVR dashboard pauses the match behind it (time and sound), and closing it resumes. The game's own pause
        /// menu stays shut: the dashboard's button is not the game's menu button, and coming back from SteamVR to the
        /// game's menu read as the one button opening both.
        /// </summary>
        private void OnDashboard(bool open)
        {
            if (open)
            {
                if (GameContext.InGame && GameManager.instance != null && !GameManager.IsPaused())
                {
                    GameManager.PauseGame();
                    _pausedForDashboard = true;
                }
                return;
            }
            bool menuOpen = IngameMenuUi.instance != null && IngameMenuUi.IsOpen();
            if (_pausedForDashboard && GameManager.instance != null && GameManager.IsPaused() && !menuOpen)
            {
                GameManager.UnpauseGame();
            }
            _pausedForDashboard = false;
        }

        private void OnRenderSettingChanged(object sender, EventArgs e) => OpenVRRuntime.ApplyLiveSettings();

        private void OnGraphicsPresetChanged(object sender, EventArgs e) => GraphicsPresets.OnPresetChanged();

        private void Shutdown()
        {
            if (_subscribed)
            {
                Application.onBeforeRender -= OnBeforeRender;
                SceneManager.activeSceneChanged -= OnSceneChanged;
                OpenVRRuntime.DashboardChanged -= OnDashboard;
                VRConfig.RenderScale.SettingChanged -= OnRenderSettingChanged;
                VRConfig.Graphics.SettingChanged -= OnGraphicsPresetChanged;
                VRConfig.Mirror.SettingChanged -= OnRenderSettingChanged;
                _subscribed = false;
            }
            _active = false;
            ScriptKeys.Enabled = false;
            ScriptKeys.Clear();
            VRGameInput.Enabled = false;
            UI?.Dispose();
            Weapons?.Dispose();
            Rig?.Dispose();
            UI = null;
            Weapons = null;
            Rig = null;
            OpenVRRuntime.Stop();
        }

        private void OnApplicationQuit() => Shutdown();

        private void OnDestroy()
        {
            Shutdown();
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
