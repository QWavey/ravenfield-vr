using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RavenfieldVR.Core;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace RavenfieldVR.Runtime
{
    /// <summary>
    /// Brings up Valve's OpenVR Unity XR provider (XRSDKOpenVR.dll) without Unity's XR Management package:
    /// pushes the provider's user settings over its native API, then creates and starts the
    /// "OpenVR Display" and "OpenVR Input" subsystems Unity discovered from UnitySubsystemsManifest.json.
    /// Also owns the OpenVR event pump (quit, dashboard, keyboard, device changes).
    /// </summary>
    public static class OpenVRRuntime
    {
        public static bool Running { get; private set; }
        public static string LastError { get; private set; }
        public static XRDisplaySubsystem Display { get; private set; }
        public static XRInputSubsystem InputSubsystem { get; private set; }

        public static event Action<bool> DashboardChanged;
        public static event Action<VREvent_t> EventReceived;

        public static bool DashboardOpen { get; private set; }

        /// <summary>Unity's XR tracking origin mode, for diagnostics.</summary>
        public static string TrackingOriginName => InputSubsystem != null ? InputSubsystem.GetTrackingOriginMode().ToString() : "none";

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct UserDefinedSettings
        {
            public ushort stereoRenderingMode;
            public ushort initializationType;
            public ushort mirrorViewMode;
            [MarshalAs(UnmanagedType.LPStr)] public string editorAppKey;
            [MarshalAs(UnmanagedType.LPStr)] public string actionManifestPath;
            [MarshalAs(UnmanagedType.LPStr)] public string applicationName;
        }

        [DllImport("XRSDKOpenVR", CharSet = CharSet.Auto)]
        private static extern void SetUserDefinedSettings(UserDefinedSettings settings);

        [DllImport("XRSDKOpenVR", CharSet = CharSet.Auto)]
        private static extern EVRInitError GetInitializationResult();

        [DllImport("XRSDKOpenVR", CharSet = CharSet.Auto)]
        private static extern void SetMirrorViewMode(ushort mirrorViewMode);

        private const ushort InitTypeScene = 1;
        private static VREvent_t _event;
        private static readonly uint EventSize = (uint)Marshal.SizeOf(typeof(VREvent_t));

        /// <summary>Starts SteamVR rendering. Returns false (with <see cref="LastError"/>) and leaves the game flat on failure.</summary>
        public static bool Start()
        {
            if (Running)
            {
                return true;
            }
            try
            {
                if (!OpenVR.IsRuntimeInstalled())
                {
                    return Fail("SteamVR is not installed.");
                }

                var settings = new UserDefinedSettings
                {
                    stereoRenderingMode = (ushort)(VRConfig.Stereo.Value == StereoMode.SinglePassInstanced ? 1 : 0),
                    initializationType = InitTypeScene,
                    mirrorViewMode = (ushort)VRConfig.Mirror.Value,
                    // Builds must leave the key empty so SteamVR identifies the process (steam.app.636480 when launched from Steam).
                    editorAppKey = null,
                    // The action manifest is set by VRInput directly, with an absolute path.
                    actionManifestPath = null,
                    applicationName = "Ravenfield VR"
                };
                SetUserDefinedSettings(settings);

                Display = CreateSubsystem<XRDisplaySubsystemDescriptor, XRDisplaySubsystem>("OpenVR Display");
                if (Display == null)
                {
                    return Fail("Unity did not register the 'OpenVR Display' subsystem. Check that ravenfield_Data/UnitySubsystems/XRSDKOpenVR/UnitySubsystemsManifest.json exists.");
                }

                EVRInitError init = GetInitializationResult();
                if (init != EVRInitError.None)
                {
                    Display.Destroy();
                    Display = null;
                    return Fail($"OpenVR failed to initialise: {init} ({OpenVR.GetStringForHmdError(init)}). Is the headset connected and SteamVR running?");
                }

                InputSubsystem = CreateSubsystem<XRInputSubsystemDescriptor, XRInputSubsystem>("OpenVR Input");
                Display.Start();
                InputSubsystem?.Start();

                if (InputSubsystem != null && !InputSubsystem.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor))
                {
                    Log.Warn("Could not switch OpenVR to floor tracking; heights will be relative to the seated origin.");
                }
                // Make the compositor render in the standing (floor) space too, whatever the XR plugin did; the
                // controllers follow whichever space it ends up in (VRInput.Universe).
                OpenVR.Compositor?.SetTrackingSpace(ETrackingUniverseOrigin.TrackingUniverseStanding);

                ApplyLiveSettings();
                Running = true;
                LastError = null;
                Log.Info($"SteamVR running: {XRSettings.loadedDeviceName} {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} per eye, " +
                         $"{XRDevice.refreshRate:0} Hz, stereo {XRSettings.stereoRenderingMode}.");
                return true;
            }
            catch (DllNotFoundException e)
            {
                return Fail("XRSDKOpenVR.dll or one of its dependencies (openvr_api.dll, ucrtbased.dll) could not be loaded: " + e.Message);
            }
            catch (Exception e)
            {
                return Fail("Unexpected VR start-up error: " + e);
            }
        }

        /// <summary>Settings that can change while running (render scale, mirror, frame pacing).</summary>
        public static void ApplyLiveSettings()
        {
            XRSettings.eyeTextureResolutionScale = Mathf.Clamp(VRConfig.RenderScale.Value, 0.5f, 2f);
            try
            {
                SetMirrorViewMode((ushort)VRConfig.Mirror.Value);
            }
            catch (EntryPointNotFoundException)
            {
                // Older provider builds lack the runtime mirror switch; the start-up value still applies.
            }
            EnforceFramePacing();
        }

        /// <summary>
        /// The SteamVR compositor paces frames. Desktop VSync would cap the game at the monitor refresh rate and
        /// cause judder, so it stays off while VR runs (the game's options re-apply it on change).
        /// </summary>
        public static void EnforceFramePacing()
        {
            if (QualitySettings.vSyncCount != 0)
            {
                QualitySettings.vSyncCount = 0;
            }
            if (Application.targetFrameRate != -1)
            {
                Application.targetFrameRate = -1;
            }
        }

        public static void Stop()
        {
            if (!Running)
            {
                return;
            }
            Running = false;
            try
            {
                InputSubsystem?.Stop();
                Display?.Stop();
                InputSubsystem?.Destroy();
                Display?.Destroy();
            }
            catch (Exception e)
            {
                Log.Exception("Stopping OpenVR", e);
            }
            InputSubsystem = null;
            Display = null;
        }

        /// <summary>Drains the OpenVR event queue. Called once per frame from the main thread.</summary>
        public static void PumpEvents()
        {
            if (!Running)
            {
                return;
            }
            CVRSystem system = OpenVR.System;
            if (system == null)
            {
                return;
            }
            for (int i = 0; i < 64 && system.PollNextEvent(ref _event, EventSize); i++)
            {
                switch ((EVREventType)_event.eventType)
                {
                    case EVREventType.VREvent_Quit:
                        if (Core.AutoTest.Requested)
                        {
                            // The virtual headset used for automated runs makes SteamVR open room setup, which asks the
                            // running app to quit; the test keeps going.
                            Log.Info("SteamVR asked the game to quit; ignored during the autotest.");
                            break;
                        }
                        Log.Info("SteamVR is shutting down; exiting the game.");
                        system.AcknowledgeQuit_Exiting();
                        Application.Quit();
                        break;
                    case EVREventType.VREvent_DashboardActivated:
                        SetDashboard(true);
                        break;
                    case EVREventType.VREvent_DashboardDeactivated:
                        SetDashboard(false);
                        break;
                }
                try
                {
                    EventReceived?.Invoke(_event);
                }
                catch (Exception e)
                {
                    Log.Exception("VR event handler", e);
                }
            }
        }

        private static void SetDashboard(bool open)
        {
            if (DashboardOpen == open)
            {
                return;
            }
            DashboardOpen = open;
            DashboardChanged?.Invoke(open);
        }

        private static TSubsystem CreateSubsystem<TDescriptor, TSubsystem>(string id)
            where TDescriptor : ISubsystemDescriptor
            where TSubsystem : ISubsystem
        {
            var descriptors = new List<TDescriptor>();
            SubsystemManager.GetSubsystemDescriptors(descriptors);
            foreach (TDescriptor d in descriptors)
            {
                if (d.id == id)
                {
                    return (TSubsystem)d.Create();
                }
            }
            var ids = new List<string>();
            foreach (TDescriptor d in descriptors)
            {
                ids.Add(d.id);
            }
            Log.Warn($"Subsystem '{id}' not found. Registered {typeof(TDescriptor).Name}s: [{string.Join(", ", ids)}]");
            return default;
        }

        private static bool Fail(string message)
        {
            LastError = message;
            Log.Error(message);
            return false;
        }
    }
}
