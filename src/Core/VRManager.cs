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
            }
            catch (Exception e)
            {
                Log.Exception("Creating VR systems failed; shutting VR down", e);
                Shutdown();
                yield break;
            }

            VRGameInput.Enabled = inputReady;
            _active = true;
            Application.onBeforeRender += OnBeforeRender;
            SceneManager.activeSceneChanged += OnSceneChanged;
            OpenVRRuntime.DashboardChanged += OnDashboard;
            VRConfig.RenderScale.SettingChanged += OnRenderSettingChanged;
            VRConfig.Mirror.SettingChanged += OnRenderSettingChanged;
            _subscribed = true;
            Log.Info("VR active.");
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
            OpenVRRuntime.PumpEvents();
            OpenVRRuntime.EnforceFramePacing();
            VRInput.Update();
            VRGameInput.Compute();
            Log.Guard("Rig.Update", Rig.Update);
            Log.Guard("UI.Update", UI.Update);
            Log.Guard("Weapons.Update", Weapons.Update);
        }

        private void LateUpdate()
        {
            if (!_active)
            {
                return;
            }
            Log.Guard("Suppressor", Rig.Suppressor.Refresh);
            Log.Guard("Weapons.LateUpdate", Weapons.LateUpdate);
        }

        private void OnBeforeRender()
        {
            if (!_active)
            {
                return;
            }
            Log.Guard("Rig.BeforeRender", Rig.BeforeRender);
            Log.Guard("Weapons.BeforeRender", Weapons.BeforeRender);
            Log.Guard("UI.BeforeRender", UI.BeforeRender);
            foreach (VRHand hand in Rig.Hands)
            {
                Log.Guard("HandModel", hand.Model.Apply);
            }
        }

        private void OnSceneChanged(Scene from, Scene to)
        {
            Log.Debug($"Scene {from.name} -> {to.name}");
            Weapons?.OnSceneChanged();
            UI?.OnSceneChanged();
        }

        private void OnDashboard(bool open)
        {
            if (open && GameContext.InGame && IngameMenuUi.instance != null && !IngameMenuUi.IsOpen())
            {
                IngameMenuUi.Show();
            }
        }

        private void OnRenderSettingChanged(object sender, EventArgs e) => OpenVRRuntime.ApplyLiveSettings();

        private void Shutdown()
        {
            if (_subscribed)
            {
                Application.onBeforeRender -= OnBeforeRender;
                SceneManager.activeSceneChanged -= OnSceneChanged;
                OpenVRRuntime.DashboardChanged -= OnDashboard;
                VRConfig.RenderScale.SettingChanged -= OnRenderSettingChanged;
                VRConfig.Mirror.SettingChanged -= OnRenderSettingChanged;
                _subscribed = false;
            }
            _active = false;
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
