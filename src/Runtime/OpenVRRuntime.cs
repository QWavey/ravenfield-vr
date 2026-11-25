using System;
using System.Collections.Generic;
using System.Linq;
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
        /// <summary>The headset was taken off (false) or put back on (true), for headsets with a sensor for it.</summary>
        public static event Action<bool> HeadsetWornChanged;
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

                Running = true; // (before the settings: they apply only while VR runs)
                ApplyLiveSettings();
                IdentifyAsVRApp();
                LastError = null;
                // SteamVR's own figures: Unity's eye size and refresh rate read 0x0 and 0 Hz this early (the user's log through
                // Link), and the frame-rate estimates need the pixels the headset really asks for.
                uint eyeWidth = 0, eyeHeight = 0;
                OpenVR.System?.GetRecommendedRenderTargetSize(ref eyeWidth, ref eyeHeight);
                ETrackedPropertyError hzError = ETrackedPropertyError.TrackedProp_Success;
                float hz = OpenVR.System?.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref hzError) ?? 0f;
                Log.Info($"SteamVR running: {XRSettings.loadedDeviceName}, {eyeWidth}x{eyeHeight} per eye at 100% (SteamVR's resolution setting "
                         + $"included), render scale {XRSettings.eyeTextureResolutionScale:0.0#}, {hz:0} Hz, stereo {XRSettings.stereoRenderingMode}.");
                // The tracking universe, which SteamVR keys a room's play area by (tools/vrshots.py gives the null driver one,
                // so its room setup does not open and close the game; task 279).
                ETrackedPropertyError universeError = ETrackedPropertyError.TrackedProp_Success;
                ulong universe = OpenVR.System?.GetUint64TrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_CurrentUniverseId_Uint64, ref universeError) ?? 0;
                Log.Info($"Tracking universe: {universe} ({universeError}).");
                return true;
            }
            catch (DllNotFoundException e)
            {
                Stop(); // what was created before the throw (audit, issue #1)
                return Fail("XRSDKOpenVR.dll or one of its dependencies (openvr_api.dll) could not be loaded: " + e.Message);
            }
            catch (Exception e)
            {
                Stop(); // the display and input subsystems started before the throw were left running, never stopped (issue #1)
                return Fail("Unexpected VR start-up error: " + e);
            }
        }

        /// <summary>Settings that can change while running (render scale, mirror, frame pacing).</summary>
        public static void ApplyLiveSettings()
        {
            if (!Running)
            {
                return; // (played flat after a failed start: a render scale changed there turned the game's VSync and frame cap off; scout)
            }
            XRSettings.eyeTextureResolutionScale = GraphicsPresets.EffectiveRenderScale(); // (the setting's, less at the frame rate hold's last step)
            try
            {
                SetMirrorViewMode((ushort)VRConfig.Mirror.Value);
            }
            catch (Exception e) when (e is EntryPointNotFoundException || e is DllNotFoundException)
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
                if (_gameVSync < 0)
                {
                    _gameVSync = QualitySettings.vSyncCount;
                }
                QualitySettings.vSyncCount = 0;
            }
            if (Application.targetFrameRate != -1)
            {
                if (_gameFrameRate == int.MinValue)
                {
                    _gameFrameRate = Application.targetFrameRate;
                }
                Application.targetFrameRate = -1;
            }
            if (Time.maximumDeltaTime > MaxCatchUp + 1e-4f)
            {
                if (_gameMaxCatchUp < 0f)
                {
                    _gameMaxCatchUp = Time.maximumDeltaTime;
                }
                Time.maximumDeltaTime = MaxCatchUp;
            }
        }

        /// <summary>
        /// The game lets physics catch up a third of a second after a long frame (its Maximum Allowed Timestep, 0.333 s, read
        /// from ravenfield_Data/globalgamemanagers): up to 20 of its 60 Hz steps in the frame after a hitch (a weapon's first
        /// draw, 290 ms at worst in the autotest), and that frame was a second hitch in the headset right behind the first. A
        /// tenth of a second while VR runs (6 steps at most): game time slows for that moment instead, and only below 10 fps.
        /// The game's own value is put back when VR stops.
        /// </summary>
        private const float MaxCatchUp = 0.1f;
        private static float _gameMaxCatchUp = -1f;
        /// <summary>The game's VSync and frame cap before VR took them over (put back when VR stops: a start failed part way ran flat uncapped).</summary>
        private static int _gameVSync = -1, _gameFrameRate = int.MinValue;

        // Also after a start that failed part way: its subsystems exist with Running still false.
        public static void Stop()
        {
            if (_gameVSync >= 0)
            {
                QualitySettings.vSyncCount = _gameVSync;
                _gameVSync = -1;
            }
            if (_gameFrameRate != int.MinValue)
            {
                Application.targetFrameRate = _gameFrameRate;
                _gameFrameRate = int.MinValue;
            }
            if (!Running && Display == null && InputSubsystem == null)
            {
                return;
            }
            Running = false;
            // Each step on its own: one that threw skipped the rest, and the display was left running (and owning the
            // compositor) when only the input's stop failed.
            Log.Guard("Stopping OpenVR input", () => InputSubsystem?.Stop());
            Log.Guard("Stopping OpenVR display", () => Display?.Stop());
            Log.Guard("Destroying OpenVR input", () => InputSubsystem?.Destroy());
            Log.Guard("Destroying OpenVR display", () => Display?.Destroy());
            InputSubsystem = null;
            Display = null;
            _startDashboardCheckAt = -1f; // (a VR start later in the session gets its own 20 s: SteamVR opens the dashboard again then)
            _dashboardHides = 0;
            DashboardOpen = false; // left open when SteamVR went, the next start's first opening was taken as no change (lifecycle hunt)
            if (_gameMaxCatchUp > 0f)
            {
                Time.maximumDeltaTime = _gameMaxCatchUp;
                _gameMaxCatchUp = -1f;
            }
        }

        private static float _startDashboardCheckAt = -1f;

        /// <summary>
        /// Tells SteamVR this process is a VR application of its own (a temporary manifest with key com.qwavey.ravenfieldvr). Started
        /// by Steam, Ravenfield is a flat game to SteamVR ("valve.steam.desktopgame.636480"), and with SteamVR Home off SteamVR stood
        /// its library dashboard over the game at every start, in the headset and in the test runs alike; the user had to close it
        /// by hand (2026-11-14, priority one).
        /// </summary>
        private static void IdentifyAsVRApp()
        {
            const string key = "com.qwavey.ravenfieldvr";
            try
            {
                if (OpenVR.Applications == null)
                {
                    return;
                }
                string dir = System.IO.Path.Combine(VRPlugin.PluginDir, "input");
                System.IO.Directory.CreateDirectory(dir);
                string manifest = System.IO.Path.Combine(dir, "ravenfieldvr.vrmanifest");
                string exe = System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName;
                string actions = System.IO.Path.Combine(dir, "actions.json");
                string Json(string v) => v.Replace("\\", "\\\\").Replace("\"", "\\\"");
                string text = "{\n  \"source\": \"builtin\",\n  \"applications\": [{\n"
                              + $"    \"app_key\": \"{key}\",\n"
                              + "    \"launch_type\": \"binary\",\n"
                              + $"    \"binary_path_windows\": \"{Json(exe)}\",\n"
                              + $"    \"action_manifest_path\": \"{Json(actions)}\",\n"
                              + "    \"is_dashboard_overlay\": false,\n"
                              + "    \"strings\": { \"en_us\": { \"name\": \"Ravenfield VR\", \"description\": \"Ravenfield in VR\" } }\n"
                              + "  }]\n}\n";
                System.IO.File.WriteAllText(manifest, text);
                EVRApplicationError added = OpenVR.Applications.AddApplicationManifest(manifest, true);
                EVRApplicationError named = OpenVR.Applications.IdentifyApplication((uint)System.Diagnostics.Process.GetCurrentProcess().Id, key);
                Log.Info($"SteamVR told this is the VR application {key}: manifest {added}, identified {named}.");
            }
            catch (System.Exception e)
            {
                Log.Warn("SteamVR not told this is a VR application: " + e.Message);
            }
        }

        private static int _dashboardHides;
        private static float _nextDashboardLook;
        private static readonly bool DashCheckOnly = System.Environment.GetCommandLineArgs()
            .Any(a => a.Equals("-rfvr-dashcheck", System.StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The first 20 seconds after VR came up: SteamVR's dashboard, opened over the game at its start (it takes the game for a flat
        /// one and, with SteamVR Home off, shows its library; the user had to close it by hand at every start, 2026-11-14, priority
        /// one), is closed with SteamVR's own tool (vrcmd --hidedashboard; OpenVR has no call to close it). Asked once a second, three
        /// closings at most; a dashboard the player opens later is left alone. With -rfvr-dashcheck the game says what it saw and quits.
        /// </summary>
        private static void CheckStartDashboard()
        {
            float now = Time.realtimeSinceStartup;
            if (_startDashboardCheckAt < 0f)
            {
                _startDashboardCheckAt = now + 20f;
                _nextDashboardLook = now + 1f;
                return;
            }
            if (_startDashboardCheckAt == float.MaxValue || now < _nextDashboardLook)
            {
                return;
            }
            _nextDashboardLook = now + 1f;
            bool open;
            try
            {
                open = OpenVR.Overlay != null && OpenVR.Overlay.IsDashboardVisible();
            }
            catch (System.Exception e)
            {
                Log.Warn("SteamVR's dashboard not asked about: " + e.Message);
                _startDashboardCheckAt = float.MaxValue;
                return;
            }
            if (open && _dashboardHides < 3 && VRConfig.NoSteamVRTheater.Value && !ThroughMetaLink())
            {
                _dashboardHides++;
                HideDashboard();
            }
            if (DashCheckOnly && now >= _startDashboardCheckAt && now < 50f)
            {
                return; // (the check's game stays up past 40 s: a quicker end reads to vrshots as a start that failed)
            }
            if (now >= _startDashboardCheckAt)
            {
                _startDashboardCheckAt = float.MaxValue;
                Log.Info($"SteamVR's dashboard at the start: {(open ? "OPEN OVER THE GAME" : "shut")} ({_dashboardHides} closing(s) asked).");
                if (DashCheckOnly)
                {
                    Application.Quit();
                }
            }
        }

        private static int _metaLink = -1; // -1 not asked yet, 0 no, 1 yes

        /// <summary>
        /// The headset runs through SteamVR's Oculus driver (a Quest on Meta's Link, a Rift). There the dashboard is not closed with
        /// vrcmd: at all three starts on the user's Quest 3 (2026-11-15, 19:52, 19:58, 20:00) SteamVR's compositor lost the game's
        /// frames 0.06 to 3 s after the tool connected ("AcquireSync FAILED with WAIT_TIMEOUT", "ReleaseSync FAILED 0x887a0001") and
        /// the headset showed one frozen frame for the rest of the session ("it just shows a frozen image"); the test runs on the
        /// null driver never did. The dashboard there is closed by its own button.
        /// </summary>
        private static bool ThroughMetaLink()
        {
            if (_metaLink < 0)
            {
                string system = "";
                bool read = false;
                try
                {
                    var sb = new System.Text.StringBuilder(64);
                    ETrackedPropertyError err = ETrackedPropertyError.TrackedProp_Success;
                    OpenVR.System?.GetStringTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_TrackingSystemName_String, sb, 64, ref err);
                    system = sb.ToString();
                    read = OpenVR.System != null && err == ETrackedPropertyError.TrackedProp_Success;
                }
                catch (System.Exception e)
                {
                    Log.Warn("The headset's tracking system not read: " + e.Message);
                }
                int link = system.IndexOf("oculus", System.StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0;
                if (!read)
                {
                    return link == 1; // (not kept: a failed first read was taken as "not Meta Link" for the session; runtime scout)
                }
                _metaLink = link;
                Log.Info($"Headset tracking system: '{system}'{(_metaLink == 1 ? " (Meta Link: SteamVR's dashboard is not closed by vrcmd at the start, it froze the picture)" : "")}.");
            }
            return _metaLink == 1;
        }

        private static void HideDashboard()
        {
            try
            {
                string runtime = OpenVR.RuntimePath();
                string tool = string.IsNullOrEmpty(runtime) ? null : System.IO.Path.Combine(runtime, "bin", "win64", "vrcmd.exe");
                if (tool == null || !System.IO.File.Exists(tool))
                {
                    Log.Warn("SteamVR's dashboard is open over the game and its tool to close it was not found (" + (tool ?? "no SteamVR path") + ").");
                    return;
                }
                var start = new System.Diagnostics.ProcessStartInfo(tool, "--hidedashboard")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                };
                System.Diagnostics.Process.Start(start)?.Dispose();
                Log.Info($"SteamVR's dashboard was open over the game: closed (vrcmd --hidedashboard, {_dashboardHides} of 3).");
            }
            catch (System.Exception e)
            {
                Log.Warn("SteamVR's dashboard could not be closed: " + e.Message);
            }
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
            CheckStartDashboard();
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
                    case EVREventType.VREvent_TrackedDeviceUserInteractionEnded:
                    case EVREventType.VREvent_TrackedDeviceUserInteractionStarted:
                        if (_event.trackedDeviceIndex == OpenVR.k_unTrackedDeviceIndex_Hmd && !Core.AutoTest.Requested)
                        {
                            bool worn = (EVREventType)_event.eventType == EVREventType.VREvent_TrackedDeviceUserInteractionStarted;
                            Log.Info(worn ? "Headset put back on." : "Headset taken off.");
                            Raise(HeadsetWornChanged, worn, "Headset worn handler");
                        }
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
            Raise(DashboardChanged, open, "Dashboard handler");
        }

        /// <summary>
        /// Guarded as the other events' handlers are: one that threw left the pump, and with it the mod's whole Update for
        /// that frame (input, rig, UI, weapons), and the rest of the queued events waited a frame (bug hunt, 2026-10-22).
        /// </summary>
        private static void Raise(Action<bool> handler, bool value, string context)
        {
            try
            {
                handler?.Invoke(value);
            }
            catch (Exception e)
            {
                Log.Exception(context, e);
            }
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
