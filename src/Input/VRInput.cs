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
        Squad, Map, Loadout, Scoreboard, Pause, VRMenu, Countermeasures, ModKeys,
        // The game's slow motion (its Slowmotion key): it had no button in VR (user, 2026-10-18 evening: "theres no button for
        // slowmotion rn"; task 273). A pressed twice on Touch (B is the magazine release); the right thumbstick's double click before
        // (user, 2026-10-19 night: "do another button, i dont want middle double press"; task 313).
        SlowMotion
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

        /// <summary>
        /// A level loaded: a squeeze still held from the last one claims nothing in this one (a grip closed on nothing and kept
        /// closed through the load took the first cockpit rim the hand reached; input scout, 2026-11-03).
        /// </summary>
        internal static void OnSceneChanged() => System.Array.Clear(Unclaimed, 0, Unclaimed.Length);

        private static readonly float[] Trigger = new float[2];
        private static readonly float[] Grip = new float[2];
        /// <summary>The grip tells how far it is squeezed (an analog grip), not only whether it clicked.</summary>
        private static readonly bool[] GripAnalog = new bool[2];
        /// <summary>When the grip's click let go while the hand still held (Time.unscaledTime; -1: it has not), and for how long
        /// a squeeze still above GripLetGoBelow keeps the hold from then.</summary>
        private static readonly float[] GripClickGone = { -1f, -1f };
        private const float GripKeepTime = 0.15f;
        public static Vector2 Move { get; private set; }
        public static Vector2 TurnStick { get; private set; }

        private static readonly float[][] Curl = { new float[5], new float[5] };
        private static readonly bool[] SkeletonValid = new bool[2];
        private static readonly string[] ControllerTypes = { "", "" };

        /// <summary>
        /// A Touch controller in either hand (its slow motion is A pressed twice), or none known yet (as <see cref="IsTouch"/>):
        /// while the type was unknown every A press, slide releases and fire-mode presses too, switched slow motion.
        /// </summary>
        internal static bool AnyOculusTouch => string.IsNullOrEmpty(ControllerTypes[0]) && string.IsNullOrEmpty(ControllerTypes[1])
                                               || IsTouchType(ControllerTypes[0]) || IsTouchType(ControllerTypes[1]);

        private static bool IsTouchType(string type) => !string.IsNullOrEmpty(type)
                                                        && (type.IndexOf("oculus", StringComparison.OrdinalIgnoreCase) >= 0
                                                            || type.IndexOf("touch", StringComparison.OrdinalIgnoreCase) >= 0);
        private static readonly bool[] HapticOkLogged = new bool[2];
        private static readonly bool[] HapticFailLogged = new bool[2];
        // The pulse playing on each hand: until when, and how strong (after the Touch minimums).
        private static readonly float[] HapticUntil = new float[2];
        private static readonly float[] HapticAmplitude = new float[2];

        /// <summary>
        /// Whether a pulse of <paramref name="amplitude"/> may start now, with a pulse of <paramref name="runningAmplitude"/> playing
        /// until <paramref name="runningUntil"/>: not when it is weaker. SteamVR replaces a running vibration with the newest, so a
        /// 1 cm rack tick (45 ms on Touch) cut the slide's click short, as a rail tick did a magazine's seat; Boneworks' haptics keep
        /// the strongest playing (its Haptor's stack policy, noDoubleHaptic).
        /// </summary>
        internal static bool HapticAllowed(float now, float amplitude, float runningUntil, float runningAmplitude)
            => now >= runningUntil || amplitude >= runningAmplitude - 1e-3f;
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

        /// <summary>Why the last <see cref="Initialize"/> failed (for the notice in the headset), or null.</summary>
        public static string InitError { get; private set; }

        /// <summary>Extracts the embedded manifest + default bindings, registers them with SteamVR and resolves handles.</summary>
        public static bool Initialize()
        {
            Ready = false; // (a start that fails part way is not left with the last session's handles)
            InitError = null;
            try
            {
                string dir = Path.Combine(VRPlugin.PluginDir, "input");
                ExtractManifests(dir);
                ManifestPath = Path.GetFullPath(Path.Combine(dir, "actions.json"));

                CVRInput input = OpenVR.Input;
                if (input == null)
                {
                    Log.Error("OpenVR.Input is unavailable; controller input disabled.");
                    InitError = "SteamVR's input is unavailable.";
                    return false;
                }
                EVRInputError err = input.SetActionManifestPath(ManifestPath);
                if (err != EVRInputError.None)
                {
                    Log.Error($"SteamVR rejected the action manifest ({err}): {ManifestPath}");
                    InitError = $"SteamVR rejected the controller bindings ({err}).";
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
                InitError = "setting it up failed: " + e.Message;
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

        /// <summary>
        /// The tracking space the compositor renders the headset in (Unity's XR plugin picks it). Controller poses are
        /// read in the same space, so hands and head always agree, whether that is standing (floor) or seated.
        /// </summary>
        public static ETrackingUniverseOrigin Universe { get; private set; } = ETrackingUniverseOrigin.TrackingUniverseStanding;

        /// <summary>Refreshes action state. Idempotent within a frame.</summary>
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
                if (err != _lastActionStateError)
                {
                    // Once per error (its key and message were built every frame the input kept failing, only to be dropped).
                    _lastActionStateError = err;
                    Log.WarnOnce("uas:" + err, "UpdateActionState failed: " + err);
                }
                ClearEdges();
                return;
            }

            _noDataSince = -1f;
            bool recovering = _heldBeforeOutageSaved;
            _heldBeforeOutageSaved = false;
            float now = Time.unscaledTime;
            var data = new InputDigitalActionData_t();
            // Each hand's reads first. One controller can drop out alone (its link, its battery, asleep): its actions read
            // inactive, and its buttons go through that gap as the whole input's do in ClearEdges. Read as let go at once, a
            // dropout dropped the gun from a hand holding it by the grip, and the button still down when it came back was
            // pressed again (Pause, FireMode, Sprint toggled; input hunt, 2026-10-23).
            for (int h = 0; h < 2; h++)
            {
                bool alive = false;
                for (int a = 0; a < DigitalCount; a++)
                {
                    bool ok = input.GetDigitalActionData(DigitalHandle[a], ref data, DigitalSize, HandSource[h]) == EVRInputError.None && data.bActive;
                    RawState[a, h] = ok && data.bState;
                    alive |= ok;
                }
                HandRecovering[h] = alive && HandOutageSaved[h];
                if (alive)
                {
                    HandAliveAt[h] = now;
                    HandOutageSaved[h] = false;
                }
                else if (now - HandAliveAt[h] > OutageGrace && !HandOutageSaved[h])
                {
                    for (int a = 0; a < DigitalCount; a++)
                    {
                        HeldBeforeOutage[a, h] = Held[a, h]; // still down when the hand is back: not pressed again
                    }
                    HandOutageSaved[h] = true;
                }
                HandAlive[h] = alive;
            }
            // A hold on the grip is kept until the grip is clearly released (GripLetGoBelow), on controllers that tell how far it is
            // squeezed: SteamVR's click lets go just under where it took hold, and a grip easing off while the hand pushed a
            // magazine in or pulled a slide let go and took hold again ("parts of the hand or mags are glitching around (this
            // happens randomly with force or smth)", user, 2026-11-11). Last frame's squeeze decides (it is read below). For
            // GripKeepTime at most after the click let go: an Index controller's grip reads a third and more with the fingers only
            // lying round it, and a hold kept for as long as that would never let go there.
            float letGo = Core.VRConfig.GripLetGoBelow != null ? Core.VRConfig.GripLetGoBelow.Value : 0f;
            for (int h = 0; h < 2; h++)
            {
                if (RawState[(int)DigitalAction.Grab, h] || !Held[(int)DigitalAction.Grab, h])
                {
                    GripClickGone[h] = -1f;
                    continue;
                }
                if (GripClickGone[h] < 0f)
                {
                    GripClickGone[h] = Time.unscaledTime;
                }
                if (letGo > 0f && GripAnalog[h] && Grip[h] > letGo && HandAlive[h] && Time.unscaledTime - GripClickGone[h] < GripKeepTime)
                {
                    RawState[(int)DigitalAction.Grab, h] = true;
                }
            }
            for (int a = 0; a < DigitalCount; a++)
            {
                for (int slot = 0; slot < 3; slot++)
                {
                    bool was = Held[a, slot];
                    bool state, pressed, released;
                    if (slot < 2)
                    {
                        state = HandButton(HandAlive[slot], RawState[a, slot], was, now - HandAliveAt[slot],
                            (recovering || HandRecovering[slot]) && HeldBeforeOutage[a, slot], out pressed, out released);
                        HandState[slot] = state;
                    }
                    else
                    {
                        // The any-hand slot from the two hand states just made (every binding is on a hand): a third SteamVR call
                        // per action was 17 of the 51 calls a frame (performance review 2026-10-19, measured 2026-10-21).
                        state = HandState[0] || HandState[1];
                        bool back = recovering && HeldBeforeOutage[a, 2] || HandRecovering[0] && HeldBeforeOutage[a, 0] || HandRecovering[1] && HeldBeforeOutage[a, 1];
                        pressed = state && !was && !back;
                        released = !state && was;
                    }
                    Held[a, slot] = state;
                    Pressed[a, slot] = pressed;
                    Released[a, slot] = released;
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
                if (Trigger[h] <= 0f && (Held[(int)DigitalAction.Fire, h] || Held[(int)DigitalAction.Use, h]))
                {
                    Trigger[h] = 1f; // a trigger that only clicks: held, the finger stayed open between the shots' flashes (hands scout, 2026-11-06)
                }
                GripAnalog[h] = input.GetAnalogActionData(_gripPull, ref analog, AnalogSize, HandSource[h]) == EVRInputError.None && analog.bActive;
                Grip[h] = GripAnalog[h] ? analog.x : (Held[(int)DigitalAction.Grab, h] ? 1f : 0f);
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

        /// <summary>Logs the input check every 2 s for the next <paramref name="seconds"/> (e.g. after the view changes).</summary>
        public static void DiagnoseFor(float seconds)
        {
            _diagnoseUntil = Mathf.Max(_diagnoseUntil, Time.unscaledTime + seconds);
        }

        private static int _diagFrames;
        private static float _diagWindowStart;

        private static readonly Compositor_FrameTiming[] Timings = new Compositor_FrameTiming[64];

        /// <summary>
        /// The compositor's view of the last frames: the GPU's time per frame (the game's cameras and the mod's, both eyes), set
        /// against the line's fps (1000 / fps ms a frame). Near equal, the frame rate is the GPU's (the graphics preset is what to lower); well under, it
        /// is the CPU's (the battle's soldiers, the mod's main-thread work). In play the log showed 31-46 fps and nothing to
        /// tell which (task 322).
        /// </summary>
        private static void AppendGpuTiming(StringBuilder sb)
        {
            try
            {
                CVRCompositor comp = OpenVR.Compositor;
                if (comp == null)
                {
                    return;
                }
                Timings[0].m_nSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Compositor_FrameTiming));
                uint n = comp.GetFrameTimings(Timings);
                float gpu = 0f;
                int timed = 0, reprojected = 0;
                uint dropped = 0;
                for (int i = 0; i < n && i < Timings.Length; i++)
                {
                    if (Timings[i].m_flTotalRenderGpuMs > 0f)
                    {
                        gpu += Timings[i].m_flTotalRenderGpuMs;
                        timed++;
                    }
                    reprojected += Core.PerfProbe.Reused(Timings[i].m_nReprojectionFlags, Timings[i].m_nNumFramePresents) ? 1 : 0;
                    dropped += Timings[i].m_nNumDroppedFrames;
                }
                if (timed > 0)
                {
                    // (Not against m_flClientFrameIntervalMs: through Link it read 3.2 ms at 89 fps, not the time between frames. The
                    // line's fps is the frame time to set it against: 1000 / fps.)
                    sb.Append($", gpu {gpu / timed:0.0} ms a frame ({n} frames, {reprojected} reprojected, {dropped} dropped)");
                }
            }
            catch (Exception)
            {
                // no compositor timing (flat mode, an old runtime)
            }
        }

        /// <summary>
        /// For the first minute and a half of a session, every 2 s: what SteamVR Input returns for each hand (pose,
        /// trigger, grab) next to what the system reports for the controller devices, so a headset that shows no hands
        /// can be told apart from a binding that delivers nothing.
        /// </summary>
        private static double _diagModMs;
        private static long _diagModFrames;

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
            // (The first line has no window behind it: its frames counted since the game started, over the time since then, read 0 fps.)
            sb.Append($", space {Universe}, unity origin {OpenVRRuntime.TrackingOriginName}, {(_diagWindowStart > 0f ? (_diagFrames / Mathf.Max(0.01f, now - _diagWindowStart)).ToString("0") : "?")} fps");
            _diagFrames = 0;
            _diagWindowStart = now;
            sb.Append($", mod {VRManager.ModMsPerFrameSince(ref _diagModMs, ref _diagModFrames):0.0} ms a frame");
            sb.Append(Weapons.WeaponDrops.TakeDropTiming());
            AppendGpuTiming(sb);
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

        /// <summary>
        /// A frame without fresh action data has no presses or releases (last frame's must not repeat). Held buttons and the
        /// sticks keep their last state through a short gap (a hiccup must not drop the gun from a hand that holds it by the
        /// grip), but not longer: after half a second without data they count as let go (a stick kept walking the player).
        /// </summary>
        private static void ClearEdges()
        {
            Array.Clear(Pressed, 0, Pressed.Length);
            Array.Clear(Released, 0, Released.Length);
            if (_noDataSince < 0f)
            {
                _noDataSince = Time.unscaledTime;
            }
            else if (Time.unscaledTime - _noDataSince > OutageGrace)
            {
                if (!_heldBeforeOutageSaved)
                {
                    // What was down when the outage cleared it: still down when the input comes back, it is not pressed again
                    // (a grip that had taken a magazine took another, and Pause, FireMode and Sprint toggled again; input hunt).
                    for (int a = 0; a < DigitalCount; a++)
                    {
                        for (int slot = 0; slot < 3; slot++)
                        {
                            HeldBeforeOutage[a, slot] = Held[a, slot];
                        }
                    }
                    _heldBeforeOutageSaved = true;
                    // The hands' own outages are this one too: a hand still reading inactive on the first frame back saved
                    // its buttons (zeroed here) over this, and a button held through it was pressed again when that hand came
                    // back (review, 2026-10-24).
                    HandOutageSaved[0] = HandOutageSaved[1] = true;
                }
                Array.Clear(Held, 0, Held.Length);
                Array.Clear(Trigger, 0, Trigger.Length);
                Array.Clear(Grip, 0, Grip.Length);
                Move = Vector2.zero;
                TurnStick = Vector2.zero;
            }
        }

        private static float _noDataSince = -1f;
        private static EVRInputError _lastActionStateError = EVRInputError.None;
        private static readonly bool[] HandState = new bool[2];
        private static readonly bool[,] HeldBeforeOutage = new bool[DigitalCount, 3];
        private static bool _heldBeforeOutageSaved;

        /// <summary>How long held buttons keep their state through a gap in the input (all of it, or one hand's).</summary>
        private const float OutageGrace = 0.5f;
        // Per hand: this frame's reads, whether any action of it read active, when it last did, its held buttons saved when its
        // gap passed the grace, and whether it is back this frame from such a gap.
        private static readonly bool[,] RawState = new bool[DigitalCount, 2];
        private static readonly bool[] HandAlive = new bool[2];
        private static readonly float[] HandAliveAt = new float[2];
        private static readonly bool[] HandOutageSaved = new bool[2];
        private static readonly bool[] HandRecovering = new bool[2];

        /// <summary>
        /// VR stopped: nothing is read from the SteamVR that is gone, and no button state is kept for the next start. Ready stayed
        /// set, so reads went to the old session's input, and a grip held as VR stopped read as let go (and the edges after it
        /// fired) on the first frame of the next start (bug hunt, 2026-10-23).
        /// </summary>
        public static void Shutdown()
        {
            Ready = false;
            LastUpdateFrame = -1;
            foreach (Array a in new Array[] { Held, Pressed, Released, Consumed, Unclaimed, HeldSince, HeldBeforeOutage, RawState, HandState,
                                             HandAlive, HandAliveAt, HandOutageSaved, HandRecovering, Trigger, Grip, HapticUntil, HapticAmplitude })
            {
                Array.Clear(a, 0, a.Length);
            }
            // (What a test run pressed and had not been read yet, and its held buttons: left, the next VR start began with them.)
            Array.Clear(PressPending, 0, PressPending.Length);
            Array.Clear(SimulatedGrab, 0, SimulatedGrab.Length);
            Array.Clear(SimulatedFire, 0, SimulatedFire.Length);
            Array.Clear(SimulatedUse, 0, SimulatedUse.Length);
            GripClickGone[0] = GripClickGone[1] = -1f;
            Array.Clear(Curl[0], 0, Curl[0].Length);
            Array.Clear(Curl[1], 0, Curl[1].Length);
            _heldBeforeOutageSaved = false;
            _noDataSince = -1f;
            _lastActionStateError = EVRInputError.None;
            Move = Vector2.zero;
            TurnStick = Vector2.zero;
            ResetGunHand();
        }

        /// <summary>
        /// One button of one hand this frame (returns whether it is held; sets its press and release). With the hand's reads in
        /// (<paramref name="alive"/>) it is what it reads, a press kept back when <paramref name="suppressPress"/> (it was down
        /// before a gap and still is). With the hand dropped out it keeps its state for <see cref="OutageGrace"/> without edges,
        /// then counts as let go without a release edge, as the whole input's gap does (<see cref="ClearEdges"/>).
        /// </summary>
        internal static bool HandButton(bool alive, bool raw, bool was, float sinceAlive, bool suppressPress, out bool pressed, out bool released)
        {
            if (alive)
            {
                pressed = raw && !was && !suppressPress;
                released = !raw && was;
                return raw;
            }
            pressed = false;
            released = false;
            return sinceAlive <= OutageGrace && was;
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

        /// <summary>The controller type as SteamVR answers it, read into one buffer (a new one every 2 s for the session before).</summary>
        private static readonly StringBuilder ControllerTypeText = new StringBuilder(64);

        private static bool SameText(StringBuilder sb, string s)
        {
            if (s == null || sb.Length != s.Length)
            {
                return false;
            }
            for (int i = 0; i < s.Length; i++)
            {
                if (sb[i] != s[i])
                {
                    return false;
                }
            }
            return true;
        }

        private static void RefreshControllerTypes(CVRInput input)
        {
            var poseData = new InputPoseActionData_t();
            var info = new InputOriginInfo_t();
            StringBuilder sb = ControllerTypeText;
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
                if (perr != ETrackedPropertyError.TrackedProp_Success)
                {
                    continue; // a failed query keeps the type known (it fell back to unknown, and Touch's slow motion with it)
                }
                if (SameText(sb, ControllerTypes[h]))
                {
                    continue; // the usual answer: no string made
                }
                string type = sb.ToString();
                ControllerTypes[h] = type;
                Log.Info($"{(Hand)h} controller: {type}");
            }
        }

        // ---- digital ------------------------------------------------------------------------------

        public static bool GetHeld(DigitalAction a) => Held[Slot(a), 2];
        public static bool GetHeld(DigitalAction a, Hand h) => Held[Slot(a), (int)h]
                                                               || (int)h < 2 && (a == DigitalAction.Grab && SimulatedGrab[(int)h] || a == DigitalAction.Use && SimulatedUse[(int)h]
                                                                                 || a == DigitalAction.Fire && SimulatedFire[(int)h]);

        /// <summary>Autotest: the gun hand's trigger held as Fire (firing while sprinting, task 314).</summary>
        internal static readonly bool[] SimulatedFire = new bool[2];

        /// <summary>Autotest: a closed grip on a simulated hand (the virtual headset has no controllers to squeeze).</summary>
        internal static readonly bool[] SimulatedGrab = new bool[2];

        /// <summary>
        /// Autotest: the trigger of a simulated hand held as its Use action (the support hand's trigger: the pump, the
        /// under-barrel launcher, lights; 255.I6-2). Its pressed edge comes from <see cref="SimulatePress"/>.
        /// </summary>
        internal static readonly bool[] SimulatedUse = new bool[2];

        public static bool GetPressed(DigitalAction a) => Pressed[Slot(a), 2] && !Consumed[Slot(a), 2];
        public static bool GetPressed(DigitalAction a, Hand h) => Pressed[Slot(a), (int)h] && !Consumed[Slot(a), (int)h];

        public static bool GetReleased(DigitalAction a) => Released[Slot(a), 2];
        public static bool GetReleased(DigitalAction a, Hand h) => Released[Slot(a), (int)h];

        /// <summary>Seconds the action has been held (0 when not held).</summary>
        public static float HeldTime(DigitalAction a, Hand h) =>
            Held[Slot(a), (int)h] ? Time.unscaledTime - HeldSince[Slot(a) * 3 + (int)h] : 0f;

        /// <summary>
        /// Pressed within the last <paramref name="seconds"/>, still held, and nothing has taken the press yet. Lets a
        /// reaching hand close a moment early and still catch the bolt or magazine it arrives at.
        /// </summary>
        public static bool GetPressedRecently(DigitalAction a, Hand h, float seconds) =>
            GetHeld(a, h) && Unclaimed[Slot(a), (int)h] && !Consumed[Slot(a), (int)h] && Time.unscaledTime - HeldSince[Slot(a) * 3 + (int)h] <= seconds;

        /// <summary>
        /// Still closed, and nothing has taken this squeeze yet, however long ago it closed. A grip shut while the hand was still
        /// on its way to the steering wheel (or after it, with the hand reaching in) takes the wheel when the hand arrives; the
        /// edge alone only caught a squeeze made at the rim.
        /// </summary>
        public static bool GetHeldUnclaimed(DigitalAction a, Hand h) =>
            GetHeld(a, h) && Unclaimed[Slot(a), (int)h] && !Consumed[Slot(a), (int)h];

        private static readonly bool[,] PressPending = new bool[DigitalCount, 3];

        /// <summary>Autotest: a button press on a simulated hand, seen as pressed on the next input update.</summary>
        internal static void SimulatePress(DigitalAction a, Hand h)
        {
            PressPending[Slot(a), (int)h] = true;
            PressPending[Slot(a), 2] = true;
        }

        /// <summary>Autotest: the grip of a simulated hand closes now (held until <see cref="SimulatedGrab"/> is cleared).</summary>
        internal static void SimulateGrabPress(Hand h)
        {
            SimulatedGrab[(int)h] = true;
            // A real squeeze has a pressed edge too (the pouch only answers squeezes made at it).
            PressPending[(int)DigitalAction.Grab, (int)h] = true;
            Unclaimed[(int)DigitalAction.Grab, (int)h] = true;
            HeldSince[(int)DigitalAction.Grab * 3 + (int)h] = Time.unscaledTime;
        }

        /// <summary>Marks this frame's press as handled so the game bridge does not also act on it.</summary>
        public static void Consume(DigitalAction a, Hand h)
        {
            int i = Slot(a);
            Consumed[i, (int)h] = true;
            Consumed[i, 2] = true;
            Unclaimed[i, (int)h] = false;
            Unclaimed[i, 2] = false;
        }

        public static void Consume(DigitalAction a)
        {
            int i = Slot(a);
            for (int s = 0; s < 3; s++)
            {
                Consumed[i, s] = true;
                Unclaimed[i, s] = false;
            }
        }

        public static bool IsConsumed(DigitalAction a) => Consumed[Slot(a), 2];

        // ---- analog / skeleton --------------------------------------------------------------------

        public static float GetTrigger(Hand h) => Math.Max(Trigger[(int)h], SimulatedTrigger[(int)h]);

        /// <summary>Autotest: how far a simulated hand's trigger finger is pulled (the finger pose only; it does not fire).</summary>
        internal static readonly float[] SimulatedTrigger = new float[2];
        public static float GetGrip(Hand h) => Math.Max(Grip[(int)h], SimulatedGrip[(int)h]);

        /// <summary>Autotest: how far a simulated hand's grip is closed (a fist without the Grab action: task 336's punch check).</summary>
        internal static readonly float[] SimulatedGrip = new float[2];

        /// <summary>Autotest: the move stick pushed (the soldier check walks with a body in the hand), used while not zero.</summary>
        internal static Vector2 SimulatedMove;

        /// <summary>The move stick: the simulated one while the autotest pushes it, else the controller's.</summary>
        public static Vector2 MoveOrSimulated => SimulatedMove != Vector2.zero ? SimulatedMove : Move;
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
            float now = Time.unscaledTime;
            if (!HapticAllowed(now, amplitude, HapticUntil[(int)h], HapticAmplitude[(int)h]))
            {
                return; // a stronger pulse is still playing on this hand: it is not cut short by a weaker one
            }
            HapticUntil[(int)h] = now + duration;
            HapticAmplitude[(int)h] = amplitude;
            EVRInputError err = input.TriggerHapticVibrationAction(_haptic, 0f, Mathf.Max(0.001f, duration), Mathf.Clamp(frequency, 1f, 320f),
                amplitude, HandSource[(int)h]);
            if (err != EVRInputError.None)
            {
                // (A pulse that was not played holds no weaker one off: its time was kept, and the ticks after a failed strong
                // pulse were dropped for as long as it would have lasted; scout, 2026-11-11.)
                HapticUntil[(int)h] = 0f;
                HapticAmplitude[(int)h] = 0f;
                if (!HapticFailLogged[(int)h])
                {
                    // Latched as the success line is: the key and message were built on every failed pulse only to be dropped.
                    HapticFailLogged[(int)h] = true;
                    Log.Warn($"Vibration on the {h} controller failed: {err}");
                }
            }
            else if (!HapticOkLogged[(int)h])
            {
                // Latched here instead of InfoOnce, which built the key and message string on every pulse (two per shot) only
                // to drop them.
                HapticOkLogged[(int)h] = true;
                Log.Info($"Vibration on the {h} controller works ({ControllerTypes[(int)h]}).");
            }
        }

        /// <summary>Whether that hand's controller is a Touch (Quest, Rift): its haptics have a floor of strength and one pitch.</summary>
        internal static bool HandIsTouch(Hand h) => IsTouch(h);

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

        /// <summary>The hand set as dominant in the settings (where the weapon goes by default; movement and menus stay with it).</summary>
        public static Hand Dominant => VRConfig.LeftHanded ? Hand.Left : Hand.Right;
        public static Hand OffHand => VRConfig.LeftHanded ? Hand.Right : Hand.Left;
        public static Hand Other(Hand h) => h == Hand.Left ? Hand.Right : Hand.Left;

        private static Hand _gunHand = Hand.Right;
        private static Hand _gunHandSetFor = Hand.Right;

        /// <summary>
        /// The hand that holds the drawn weapon: the dominant hand, unless the weapon was passed to the other hand or drawn
        /// with it. Changing the handedness setting starts over from the new dominant hand.
        /// </summary>
        public static Hand GunHand
        {
            get
            {
                Hand dominant = Dominant;
                if (_gunHandSetFor != dominant)
                {
                    _gunHandSetFor = dominant;
                    _gunHand = dominant;
                }
                return _gunHand;
            }
        }

        /// <summary>The hand that does not hold the weapon (support grip, magazines, use, squad orders, map).</summary>
        public static Hand FreeHand => Other(GunHand);

        public static void SetGunHand(Hand h)
        {
            _ = GunHand; // settle the handedness first
            _gunHand = h;
        }

        /// <summary>A new VR session starts with the weapon's buttons on the dominant hand.</summary>
        private static void ResetGunHand() => _gunHand = _gunHandSetFor = Dominant;

        /// <summary>The weapon is in the hand that is not the dominant one: its buttons trade places (see <see cref="Slot"/>).</summary>
        public static bool GunHandSwapped => GunHand != Dominant;

        /// <summary>
        /// The input slot an action is read from. With the weapon in the other hand, the weapon's buttons and that hand's own
        /// buttons trade places on both controllers (trigger: fire / use; lower face button: slide release / squad; upper face
        /// button: magazine release / map), so the hand holding the weapon fires and releases it and the free hand uses,
        /// orders and opens the map. Sticks, grips, pause and menus stay where they are.
        /// </summary>
        private static int Slot(DigitalAction a)
        {
            if (!GunHandSwapped)
            {
                return (int)a;
            }
            switch (a)
            {
                case DigitalAction.Fire: return (int)DigitalAction.Use;
                case DigitalAction.Use: return (int)DigitalAction.Fire;
                case DigitalAction.SlideRelease: return (int)DigitalAction.Squad;
                case DigitalAction.Squad: return (int)DigitalAction.SlideRelease;
                case DigitalAction.MagRelease: return (int)DigitalAction.Map;
                case DigitalAction.Map: return (int)DigitalAction.MagRelease;
                default: return (int)a;
            }
        }
    }
}
