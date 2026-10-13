using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;
using Valve.VR;

namespace RavenfieldVR.Input
{
    public enum Hand { Left = 0, Right = 1 }

    /// <summary>Boolean actions declared in actions.json (order must match <see cref="VRInput.DigitalNames"/>).</summary>
    public enum DigitalAction
    {
        Fire, Grab, MagRelease, SlideRelease, FireMode, Sprint, Use, WeaponNext, Kick,
        Squad, Map, Loadout, Scoreboard, Pause, VRMenu, Countermeasures, ModKeys
    }

    public enum PoseKind { Raw, Grip, Tip }

    /// <summary>Tracked pose of one hand in tracking (rig-local) space.</summary>
    public struct HandPose
    {
        public bool valid;
        public RigidPose pose;
        public Vector3 velocity;
        public Vector3 angularVelocity;
    }

    /// <summary>
    /// SteamVR Input front end. Loads the action manifest, refreshes action state once per frame and exposes
    /// per-hand digital edges, analog values, poses, finger curls and haptics.
    /// </summary>
    public static class VRInput
    {
        public static bool Ready { get; private set; }
        public static int LastUpdateFrame { get; private set; } = -1;

        internal static readonly string[] DigitalNames = Enum.GetNames(typeof(DigitalAction));
        private static readonly int DigitalCount = DigitalNames.Length;

        private static ulong _set;
        private static readonly ulong[] HandSource = new ulong[2];
        private static readonly ulong[] DigitalHandle = new ulong[DigitalCount];
        private static ulong _triggerPull, _gripPull, _move, _turn, _pose, _poseGrip, _poseTip, _haptic;
        private static readonly ulong[] SkeletonHandle = new ulong[2];
        private static VRActiveActionSet_t[] _activeSets;

        // [action, hand] for hand-restricted reads, plus an "any hand" slot at index 2.
        private static readonly bool[,] Held = new bool[DigitalCount, 3];
        private static readonly bool[,] Pressed = new bool[DigitalCount, 3];
        private static readonly bool[,] Released = new bool[DigitalCount, 3];
        private static readonly bool[,] Consumed = new bool[DigitalCount, 3];
        private static readonly float[] HeldSince = new float[DigitalCount * 3];
        // A press nothing has taken yet, kept while the button stays down (Consume clears it): a grab pressed a moment
        // before the hand reaches the part still takes it.
        private static readonly bool[,] Unclaimed = new bool[DigitalCount, 3];

        private static readonly float[] Trigger = new float[2];
        private static readonly float[] Grip = new float[2];
        public static Vector2 Move { get; private set; }
        public static Vector2 TurnStick { get; private set; }

        private static readonly float[][] Curl = { new float[5], new float[5] };
        private static readonly bool[] SkeletonValid = new bool[2];
        private static readonly string[] ControllerTypes = { "", "" };
        private static float _nextTypeRefresh;

        private static readonly uint DigitalSize = (uint)Marshal.SizeOf(typeof(InputDigitalActionData_t));
        private static readonly uint AnalogSize = (uint)Marshal.SizeOf(typeof(InputAnalogActionData_t));
        private static readonly uint PoseSize = (uint)Marshal.SizeOf(typeof(InputPoseActionData_t));
        private static readonly uint SetSize = (uint)Marshal.SizeOf(typeof(VRActiveActionSet_t));
        private static readonly uint OriginInfoSize = (uint)Marshal.SizeOf(typeof(InputOriginInfo_t));

        private static readonly string[] ManifestFiles =
        {
            "actions.json", "bindings_oculus_touch.json", "bindings_knuckles.json",
            "bindings_vive_controller.json", "bindings_hpmotioncontroller.json", "bindings_rift.json"
        };

        public static string ManifestPath { get; private set; }

        /// <summary>Extracts the embedded manifest + default bindings, registers them with SteamVR and resolves handles.</summary>
        public static bool Initialize()
        {
            try
            {
                string dir = Path.Combine(VRPlugin.PluginDir, "input");
                ExtractManifests(dir);
                ManifestPath = Path.GetFullPath(Path.Combine(dir, "actions.json"));

                CVRInput input = OpenVR.Input;
                if (input == null)
                {
                    Log.Error("OpenVR.Input is unavailable; controller input disabled.");
                    return false;
                }
                EVRInputError err = input.SetActionManifestPath(ManifestPath);
                if (err != EVRInputError.None)
                {
                    Log.Error($"SteamVR rejected the action manifest ({err}): {ManifestPath}");
                    return false;
                }

                Check(input.GetActionSetHandle("/actions/main", ref _set), "/actions/main");
                Check(input.GetInputSourceHandle("/user/hand/left", ref HandSource[0]), "/user/hand/left");
                Check(input.GetInputSourceHandle("/user/hand/right", ref HandSource[1]), "/user/hand/right");
                for (int i = 0; i < DigitalCount; i++)
                {
                    Check(input.GetActionHandle("/actions/main/in/" + DigitalNames[i], ref DigitalHandle[i]), DigitalNames[i]);
                }
                Check(input.GetActionHandle("/actions/main/in/TriggerPull", ref _triggerPull), "TriggerPull");
                Check(input.GetActionHandle("/actions/main/in/GripPull", ref _gripPull), "GripPull");
                Check(input.GetActionHandle("/actions/main/in/Move", ref _move), "Move");
                Check(input.GetActionHandle("/actions/main/in/TurnStick", ref _turn), "TurnStick");
                Check(input.GetActionHandle("/actions/main/in/Pose", ref _pose), "Pose");
                Check(input.GetActionHandle("/actions/main/in/PoseGrip", ref _poseGrip), "PoseGrip");
                Check(input.GetActionHandle("/actions/main/in/PoseTip", ref _poseTip), "PoseTip");
                Check(input.GetActionHandle("/actions/main/in/SkeletonLeft", ref SkeletonHandle[0]), "SkeletonLeft");
                Check(input.GetActionHandle("/actions/main/in/SkeletonRight", ref SkeletonHandle[1]), "SkeletonRight");
                Check(input.GetActionHandle("/actions/main/out/Haptic", ref _haptic), "Haptic");

                _activeSets = new[] { new VRActiveActionSet_t { ulActionSet = _set, ulRestrictedToDevice = OpenVR.k_ulInvalidInputValueHandle, nPriority = 0 } };
                Ready = true;
                _diagnoseUntil = Time.unscaledTime + 90f;
                Log.Info("SteamVR Input ready: " + ManifestPath);
                return true;
            }
            catch (Exception e)
            {
                Log.Exception("SteamVR Input initialisation", e);
                return false;
            }
        }

        private static void Check(EVRInputError err, string what)
        {
            if (err != EVRInputError.None)
            {
                Log.Warn($"SteamVR Input: handle for {what} failed ({err}).");
            }
        }

        private static void ExtractManifests(string dir)
        {
            Directory.CreateDirectory(dir);
            Assembly asm = Assembly.GetExecutingAssembly();
            foreach (string file in ManifestFiles)
            {
                using (Stream s = asm.GetManifestResourceStream("RavenfieldVR.Input." + file))
                {
                    if (s == null)
                    {
                        Log.Warn("Embedded input resource missing: " + file);
                        continue;
                    }
                    string content;
                    using (var reader = new StreamReader(s, Encoding.UTF8))
                    {
                        content = reader.ReadToEnd();
                    }
                    if (VRConfig.LeftHanded && file.StartsWith("bindings_"))
                    {
                        string controllerType = file.Substring("bindings_".Length, file.Length - "bindings_".Length - ".json".Length);
                        content = BindingsWriter.MirrorToLeftHanded(content, controllerType);
                    }
                    string target = Path.Combine(dir, file);
                    // Only rewrite when changed so SteamVR does not see a "new" manifest every launch.
                    if (!File.Exists(target) || File.ReadAllText(target, Encoding.UTF8) != content)
                    {
                        File.WriteAllText(target, content, new UTF8Encoding(false));
                    }
                }
            }
        }

        /// <summary>Refreshes action state. Idempotent within a frame.</summary>
        /// <summary>
        /// The tracking space the compositor renders the headset in (Unity's XR plugin picks it). Controller poses are
        /// read in the same space, so hands and head always agree, whether that is standing (floor) or seated.
        /// </summary>
        public static ETrackingUniverseOrigin Universe { get; private set; } = ETrackingUniverseOrigin.TrackingUniverseStanding;

        public static void Update()
        {
            if (!Ready || LastUpdateFrame == Time.frameCount)
            {
                return;
            }
            LastUpdateFrame = Time.frameCount;
            CVRCompositor compositor = OpenVR.Compositor;
            if (compositor != null)
            {
                Universe = compositor.GetTrackingSpace();
            }
            CVRInput input = OpenVR.Input;
            if (input == null)
            {
                ClearEdges();
                return;
            }
            EVRInputError err = input.UpdateActionState(_activeSets, SetSize);
            Diagnose(input, err);
            if (err != EVRInputError.None && err != EVRInputError.NoData)
            {
                Log.WarnOnce("uas:" + err, "UpdateActionState failed: " + err);
                ClearEdges();
                return;
            }

            float now = Time.unscaledTime;
            var data = new InputDigitalActionData_t();
            for (int a = 0; a < DigitalCount; a++)
            {
                for (int slot = 0; slot < 3; slot++)
                {
                    ulong restrict = slot < 2 ? HandSource[slot] : OpenVR.k_ulInvalidInputValueHandle;
                    bool state = input.GetDigitalActionData(DigitalHandle[a], ref data, DigitalSize, restrict) == EVRInputError.None
                                 && data.bActive && data.bState;
                    bool was = Held[a, slot];
                    Held[a, slot] = state;
                    Pressed[a, slot] = state && !was;
                    Released[a, slot] = !state && was;
                    Consumed[a, slot] = false;
                    if (PressPending[a, slot])
                    {
                        PressPending[a, slot] = false;
                        Pressed[a, slot] = true;
                    }
                    if (Pressed[a, slot])
                    {
                        HeldSince[a * 3 + slot] = now;
                        Unclaimed[a, slot] = true;
                    }
                    else if (!state && !(a == (int)DigitalAction.Grab && slot < 2 && SimulatedGrab[slot]))
                    {
                        Unclaimed[a, slot] = false;
                    }
                }
            }

            var analog = new InputAnalogActionData_t();
            for (int h = 0; h < 2; h++)
            {
                Trigger[h] = input.GetAnalogActionData(_triggerPull, ref analog, AnalogSize, HandSource[h]) == EVRInputError.None && analog.bActive ? analog.x : 0f;
                Grip[h] = input.GetAnalogActionData(_gripPull, ref analog, AnalogSize, HandSource[h]) == EVRInputError.None && analog.bActive ? analog.x : (Held[(int)DigitalAction.Grab, h] ? 1f : 0f);
            }
            Move = input.GetAnalogActionData(_move, ref analog, AnalogSize, OpenVR.k_ulInvalidInputValueHandle) == EVRInputError.None && analog.bActive
                ? new Vector2(analog.x, analog.y) : Vector2.zero;
            TurnStick = input.GetAnalogActionData(_turn, ref analog, AnalogSize, OpenVR.k_ulInvalidInputValueHandle) == EVRInputError.None && analog.bActive
                ? new Vector2(analog.x, analog.y) : Vector2.zero;

            UpdateSkeletons(input);
            if (now >= _nextTypeRefresh)
            {
                _nextTypeRefresh = now + 2f;
                RefreshControllerTypes(input);
            }

            if (VRConfig.DebugLogging.Value)
            {
                for (int a = 0; a < DigitalCount; a++)
                {
                    if (Pressed[a, 2])
                    {
                        Log.Debug("pressed " + DigitalNames[a]);
                    }
                }
            }
        }

        private static float _diagnoseUntil = -1f, _nextDiagnose;

        /// <summary>
        /// For the first minute and a half of a session, every 2 s: what SteamVR Input returns for each hand (pose,
        /// trigger, grab) next to what the system reports for the controller devices, so a headset that shows no hands
        /// can be told apart from a binding that delivers nothing.
        /// </summary>
        /// <summary>Logs the input check every 2 s for the next <paramref name="seconds"/> (e.g. after the view changes).</summary>
        public static void DiagnoseFor(float seconds)
        {
            _diagnoseUntil = Mathf.Max(_diagnoseUntil, Time.unscaledTime + seconds);
        }

        private static int _diagFrames;
        private static float _diagWindowStart;

        private static void Diagnose(CVRInput input, EVRInputError updateError)
        {
            float now = Time.unscaledTime;
            _diagFrames++;
            if (now > _diagnoseUntil || now < _nextDiagnose)
            {
                return;
            }
            _nextDiagnose = now + 2f;
            var sb = new StringBuilder("Input check: UpdateActionState " + updateError);
            sb.Append($", space {Universe}, unity origin {OpenVRRuntime.TrackingOriginName}, {_diagFrames / Mathf.Max(0.01f, now - _diagWindowStart):0} fps");
            _diagFrames = 0;
            _diagWindowStart = now;
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig != null && rig.Camera != null)
            {
                Vector3 head = rig.Camera.transform.localPosition;
                sb.Append($", head local ({head.x:0.00}, {head.y:0.00}, {head.z:0.00}), mode {rig.Mode}");
            }
            CVRSystem system = OpenVR.System;
            var pose = new InputPoseActionData_t();
            for (int h = 0; h < 2; h++)
            {
                EVRInputError pe = input.GetPoseActionDataForNextFrame(_pose, Universe, ref pose, PoseSize, HandSource[h]);
                uint device = system != null
                    ? system.GetTrackedDeviceIndexForControllerRole(h == 0 ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand)
                    : OpenVR.k_unTrackedDeviceIndexInvalid;
                bool connected = system != null && device != OpenVR.k_unTrackedDeviceIndexInvalid && system.IsTrackedDeviceConnected(device);
                OpenVRMath.ToUnity(pose.pose.mDeviceToAbsoluteTracking, out Vector3 at, out Quaternion _);
                sb.Append(h == 0 ? " | left: " : " | right: ")
                  .Append($"pose {pe} active={pose.bActive} valid={pose.pose.bPoseIsValid} at ({at.x:0.00}, {at.y:0.00}, {at.z:0.00}), device {device} connected={connected}, ")
                  .Append($"trigger {Trigger[h]:0.00}, grab {Held[(int)DigitalAction.Grab, h]}, type '{ControllerTypes[h]}'");
            }
            Log.Info(sb.ToString());
        }

        /// <summary>A frame without fresh action data has no presses or releases (last frame's must not repeat).</summary>
        private static void ClearEdges()
        {
            Array.Clear(Pressed, 0, Pressed.Length);
            Array.Clear(Released, 0, Released.Length);
        }

        private static void UpdateSkeletons(CVRInput input)
        {
            var summary = new VRSkeletalSummaryData_t();
            for (int h = 0; h < 2; h++)
            {
                if (SkeletonHandle[h] == 0 || input.GetSkeletalSummaryData(SkeletonHandle[h], EVRSummaryType.FromDevice, ref summary) != EVRInputError.None)
                {
                    SkeletonValid[h] = false;
                    continue;
                }
                SkeletonValid[h] = true;
                float[] c = Curl[h];
                c[0] = summary.flFingerCurl0;
                c[1] = summary.flFingerCurl1;
                c[2] = summary.flFingerCurl2;
                c[3] = summary.flFingerCurl3;
                c[4] = summary.flFingerCurl4;
            }
        }

        private static void RefreshControllerTypes(CVRInput input)
        {
            var poseData = new InputPoseActionData_t();
            var info = new InputOriginInfo_t();
            var sb = new StringBuilder(64);
            for (int h = 0; h < 2; h++)
            {
                if (input.GetPoseActionDataForNextFrame(_pose, Universe, ref poseData, PoseSize, HandSource[h]) != EVRInputError.None
                    || !poseData.bActive)
                {
                    continue;
                }
                if (input.GetOriginTrackedDeviceInfo(poseData.activeOrigin, ref info, OriginInfoSize) != EVRInputError.None)
                {
                    continue;
                }
                ETrackedPropertyError perr = ETrackedPropertyError.TrackedProp_Success;
                sb.Length = 0;
                OpenVR.System?.GetStringTrackedDeviceProperty(info.trackedDeviceIndex, ETrackedDeviceProperty.Prop_ControllerType_String, sb, (uint)sb.Capacity, ref perr);
                string type = perr == ETrackedPropertyError.TrackedProp_Success ? sb.ToString() : "";
                if (type != ControllerTypes[h])
                {
                    ControllerTypes[h] = type;
                    Log.Info($"{(Hand)h} controller: {type}");
                }
            }
        }

        // ---- digital ------------------------------------------------------------------------------

        public static bool GetHeld(DigitalAction a) => Held[(int)a, 2];
        public static bool GetHeld(DigitalAction a, Hand h) => Held[(int)a, (int)h] || a == DigitalAction.Grab && (int)h < 2 && SimulatedGrab[(int)h];

        /// <summary>Autotest: a closed grip on a simulated hand (the virtual headset has no controllers to squeeze).</summary>
        internal static readonly bool[] SimulatedGrab = new bool[2];

        public static bool GetPressed(DigitalAction a) => Pressed[(int)a, 2] && !Consumed[(int)a, 2];
        public static bool GetPressed(DigitalAction a, Hand h) => Pressed[(int)a, (int)h] && !Consumed[(int)a, (int)h];

        public static bool GetReleased(DigitalAction a) => Released[(int)a, 2];
        public static bool GetReleased(DigitalAction a, Hand h) => Released[(int)a, (int)h];

        /// <summary>Seconds the action has been held (0 when not held).</summary>
        public static float HeldTime(DigitalAction a, Hand h) =>
            Held[(int)a, (int)h] ? Time.unscaledTime - HeldSince[(int)a * 3 + (int)h] : 0f;

        /// <summary>
        /// Pressed within the last <paramref name="seconds"/>, still held, and nothing has taken the press yet. Lets a
        /// reaching hand close a moment early and still catch the bolt or magazine it arrives at.
        /// </summary>
        public static bool GetPressedRecently(DigitalAction a, Hand h, float seconds) =>
            GetHeld(a, h) && Unclaimed[(int)a, (int)h] && !Consumed[(int)a, (int)h] && Time.unscaledTime - HeldSince[(int)a * 3 + (int)h] <= seconds;

        private static readonly bool[,] PressPending = new bool[DigitalCount, 3];

        /// <summary>Autotest: a button press on a simulated hand, seen as pressed on the next input update.</summary>
        internal static void SimulatePress(DigitalAction a, Hand h)
        {
            PressPending[(int)a, (int)h] = true;
            PressPending[(int)a, 2] = true;
        }

        /// <summary>Autotest: the grip of a simulated hand closes now (held until <see cref="SimulatedGrab"/> is cleared).</summary>
        internal static void SimulateGrabPress(Hand h)
        {
            SimulatedGrab[(int)h] = true;
            Unclaimed[(int)DigitalAction.Grab, (int)h] = true;
            HeldSince[(int)DigitalAction.Grab * 3 + (int)h] = Time.unscaledTime;
        }

        /// <summary>Marks this frame's press as handled so the game bridge does not also act on it.</summary>
        public static void Consume(DigitalAction a, Hand h)
        {
            Consumed[(int)a, (int)h] = true;
            Consumed[(int)a, 2] = true;
            Unclaimed[(int)a, (int)h] = false;
            Unclaimed[(int)a, 2] = false;
        }

        public static void Consume(DigitalAction a)
        {
            for (int s = 0; s < 3; s++)
            {
                Consumed[(int)a, s] = true;
                Unclaimed[(int)a, s] = false;
            }
        }

        public static bool IsConsumed(DigitalAction a) => Consumed[(int)a, 2];

        // ---- analog / skeleton --------------------------------------------------------------------

        public static float GetTrigger(Hand h) => Math.Max(Trigger[(int)h], SimulatedTrigger[(int)h]);

        /// <summary>Autotest: how far a simulated hand's trigger finger is pulled (the finger pose only; it does not fire).</summary>
        internal static readonly float[] SimulatedTrigger = new float[2];
        public static float GetGrip(Hand h) => Grip[(int)h];
        public static bool HasSkeleton(Hand h) => SkeletonValid[(int)h];

        /// <summary>Finger curl 0..1 (0 thumb .. 4 pinky).</summary>
        public static float GetCurl(Hand h, int finger) => Curl[(int)h][finger];

        public static string ControllerType(Hand h) => ControllerTypes[(int)h];

        // ---- poses --------------------------------------------------------------------------------

        /// <summary>Pose predicted for the next displayed frame, in the standing (floor) tracking universe.</summary>
        public static HandPose GetPose(Hand h, PoseKind kind)
        {
            var result = new HandPose();
            if (!Ready)
            {
                return result;
            }
            CVRInput input = OpenVR.Input;
            if (input == null)
            {
                return result;
            }
            ulong action = kind == PoseKind.Raw ? _pose : kind == PoseKind.Grip ? _poseGrip : _poseTip;
            if (action == 0)
            {
                return result;
            }
            var data = new InputPoseActionData_t();
            if (input.GetPoseActionDataForNextFrame(action, Universe, ref data, PoseSize, HandSource[(int)h]) != EVRInputError.None
                || !data.bActive || !data.pose.bPoseIsValid)
            {
                return result;
            }
            OpenVRMath.ToUnity(data.pose.mDeviceToAbsoluteTracking, out Vector3 p, out Quaternion r);
            result.valid = true;
            result.pose = new RigidPose(p, r);
            result.velocity = OpenVRMath.ToUnityVector(data.pose.vVelocity);
            result.angularVelocity = OpenVRMath.ToUnityAngular(data.pose.vAngularVelocity);
            return result;
        }

        // ---- haptics ------------------------------------------------------------------------------

        /// <summary>Vibrates one controller.</summary>
        /// <param name="h">Which hand.</param>
        /// <param name="duration">Seconds.</param>
        /// <param name="frequency">Hz (1..320).</param>
        /// <param name="amplitude">0..1, scaled by the HapticStrength setting.</param>
        public static void Haptic(Hand h, float duration, float frequency, float amplitude)
        {
            if (!Ready || _haptic == 0)
            {
                return;
            }
            float strength = VRConfig.HapticStrength.Value;
            if (strength <= 0f)
            {
                return;
            }
            amplitude = Mathf.Clamp01(amplitude * strength);
            if (IsTouch(h))
            {
                // Quest / Rift Touch over SteamVR's Oculus driver: pulses of a few milliseconds or at low strength are
                // lost in its haptics buffer and never felt, so every pulse gets a felt minimum (scaled by the setting).
                duration = Mathf.Max(duration, 0.045f);
                amplitude = Mathf.Max(amplitude, 0.35f * Mathf.Clamp01(strength));
                frequency = frequency < 120f ? 160f : 320f;
            }
            CVRInput input = OpenVR.Input;
            if (input == null)
            {
                return;
            }
            EVRInputError err = input.TriggerHapticVibrationAction(_haptic, 0f, Mathf.Max(0.001f, duration), Mathf.Clamp(frequency, 1f, 320f),
                amplitude, HandSource[(int)h]);
            if (err != EVRInputError.None)
            {
                Log.WarnOnce("haptic:" + h + err, $"Vibration on the {h} controller failed: {err}");
            }
            else
            {
                Log.InfoOnce("haptic-ok:" + h, $"Vibration on the {h} controller works ({ControllerTypes[(int)h]}).");
            }
        }

        /// <summary>Oculus Touch controllers (Quest 1-3, Rift S), as SteamVR reports them, or before any type is known.</summary>
        private static bool IsTouch(Hand h)
        {
            string type = ControllerTypes[(int)h];
            return string.IsNullOrEmpty(type) || type.IndexOf("oculus", StringComparison.OrdinalIgnoreCase) >= 0
                   || type.IndexOf("touch", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Opens SteamVR's binding UI for this game (in the headset).</summary>
        public static void OpenBindingUI()
        {
            if (!Ready)
            {
                return;
            }
            EVRInputError err = OpenVR.Input.OpenBindingUI(null, _set, OpenVR.k_ulInvalidInputValueHandle, false);
            if (err != EVRInputError.None)
            {
                Log.Warn("Could not open the SteamVR binding UI: " + err);
            }
        }

        public static Hand Dominant => VRConfig.LeftHanded ? Hand.Left : Hand.Right;
        public static Hand OffHand => VRConfig.LeftHanded ? Hand.Right : Hand.Left;
        public static Hand Other(Hand h) => h == Hand.Left ? Hand.Right : Hand.Left;
    }
}
