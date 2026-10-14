using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityStandardAssets.Characters.FirstPerson;
using Valve.VR;

namespace RavenfieldVR.Rig
{
    public enum RigMode
    {
        /// <summary>No reference camera yet.</summary>
        None,
        /// <summary>Walking soldier: head locked to the body's eye point horizontally, body follows real steps.</summary>
        OnFoot,
        /// <summary>Seat, ladder or vehicle camera: rig rigidly attached, head moves freely around a neutral pose.</summary>
        Anchored,
        /// <summary>Third-person, kill cam, spectator, menus: rig follows a camera's position and yaw.</summary>
        Follow
    }

    /// <summary>
    /// The VR camera rig. Owns the only HMD camera and both hands, places the tracking origin every frame from
    /// whatever the game is currently looking through, turns the player, calibrates height and writes the head
    /// pose back into the game camera so game logic (use ray, ladders, squad orders, audio) follows the head.
    /// </summary>
    public sealed class VRRig : IDisposable
    {
        public static VRRig Instance { get; private set; }

        private const int UiLayer = 5;
        private const int FirstPersonLayer = 18;
        private const int BackgroundLayer = 19;
        private const float GameStandEyeHeight = 1.53f;

        public readonly Transform Root;
        public readonly Camera Camera;
        public readonly VRHand[] Hands = new VRHand[2];
        public readonly CameraSuppressor Suppressor;
        private readonly ComfortVignette _vignette;
        private readonly DamageFeedback _damage;
        private readonly MudSplash _mud;
        private readonly CockpitControls _cockpit;

        public RigMode Mode { get; private set; } = RigMode.None;
        public Transform Head => Camera.transform;
        public RigidPose HeadPose => RigidPose.Of(Camera.transform);
        public Vector3 HeadLocal => Camera.transform.localPosition;

        public VRHand Dominant => Hands[(int)VRInput.Dominant];
        public VRHand Off => Hands[(int)VRInput.OffHand];
        public VRHand Hand(Hand side) => Hands[(int)side];

        /// <summary>World yaw of the tracking space while on foot (degrees).</summary>
        public float Yaw { get; private set; }

        private float _seatYawOffset;
        private object _anchorKey;
        private RigidPose _neutralHead = RigidPose.Identity;
        private Vector3 _lastHeadLocal;
        private bool _hasLastHeadLocal;
        private bool _snapLatch;
        private bool _wasDead;

        // Height calibration.
        private float _calibrationStart = -1f;
        private bool _physicalCrouch;

        // Fade state for camera cuts.
        private float _fadeInAt = -1f;

        private PostProcessLayer _postProcess;
        private static readonly FieldInfo PpResources = AccessTools.Field(typeof(PostProcessLayer), "m_Resources");

        public float TurnInputThisFrame { get; private set; }

        public VRRig()
        {
            Instance = this;
            var root = new GameObject("VR Rig");
            UnityEngine.Object.DontDestroyOnLoad(root);
            Root = root.transform;

            var headGo = new GameObject("VR Head");
            headGo.transform.SetParent(Root, false);
            Camera = headGo.AddComponent<Camera>();
            Camera.stereoTargetEye = StereoTargetEyeMask.Both;
            Camera.nearClipPlane = VRConfig.NearClip.Value;
            Camera.depth = 100f;
            Camera.tag = "Untagged";
            AddHeadCamera(Camera);

            Suppressor = new CameraSuppressor(Camera);
            Hands[0] = new VRHand(Input.Hand.Left, Root);
            Hands[1] = new VRHand(Input.Hand.Right, Root);
            _vignette = new ComfortVignette(Camera.transform);
            _damage = new DamageFeedback(Camera.transform);
            try
            {
                _mud = new MudSplash(Camera.transform);
            }
            catch (System.Exception e)
            {
                // A look, not a function: VR starts without it rather than not at all.
                Log.Exception("Explosion dirt could not be set up; playing without it", e);
            }
            _cockpit = new CockpitControls(this);
        }

        // ---------------------------------------------------------------------------------------------
        // Update: input-driven rig changes (turning, roomscale body moves, crouch, calibration)
        // ---------------------------------------------------------------------------------------------

        // ---------------------------------------------------------------------------------------------
        // Head pose: driven from SteamVR directly, in the same space as the controllers
        // ---------------------------------------------------------------------------------------------

        private readonly System.Collections.Generic.List<Camera> _headCameras = new System.Collections.Generic.List<Camera>();
        private TrackedDevicePose_t _hmdRender, _hmdGame;

        /// <summary>Whether SteamVR has delivered a valid headset pose at least once.</summary>
        public bool HeadTracked { get; private set; }

        /// <summary>
        /// A camera that renders the headset view. Unity's implicit XR camera tracking is switched off for it: the
        /// rig sets its pose from SteamVR's render pose, so the head can never disagree with the controllers (which
        /// come from SteamVR in the same tracking space) and never depends on how the XR plugin applies tracking.
        /// </summary>
        public void AddHeadCamera(Camera c)
        {
            if (c == null || _headCameras.Contains(c))
            {
                return;
            }
            _headCameras.Add(c);
            UnityEngine.XR.XRDevice.DisableAutoXRCameraTracking(c, true);
            if (HeadTracked)
            {
                c.transform.localPosition = Camera.transform.localPosition;
                c.transform.localRotation = Camera.transform.localRotation;
            }
        }

        /// <summary>The pose the compositor renders this frame with, applied to every head camera.</summary>
        private void UpdateHeadPose()
        {
            CVRCompositor compositor = OpenVR.Compositor;
            if (compositor == null
                || compositor.GetLastPoseForTrackedDeviceIndex(OpenVR.k_unTrackedDeviceIndex_Hmd, ref _hmdRender, ref _hmdGame) != EVRCompositorError.None
                || !_hmdRender.bPoseIsValid)
            {
                return; // asleep or lost: keep the last pose
            }
            OpenVRMath.ToUnity(_hmdRender.mDeviceToAbsoluteTracking, out Vector3 position, out Quaternion rotation);
            foreach (Camera c in _headCameras)
            {
                if (c != null)
                {
                    Transform t = c.transform;
                    t.localPosition = position;
                    t.localRotation = rotation;
                }
            }
            if (!HeadTracked)
            {
                HeadTracked = true;
                // A view entered before the headset reported its first pose has a meaningless neutral pose.
                _neutralPending = Mode == RigMode.Anchored || Mode == RigMode.Follow;
            }
        }

        private bool _neutralPending;

        // ---------------------------------------------------------------------------------------------
        // HUD frame: follows the head lazily instead of being glued to it
        // ---------------------------------------------------------------------------------------------

        /// <summary>Where the game HUD frame (and the game camera) look from: the eyes, with a lazily following orientation.</summary>
        public RigidPose HudPose { get; private set; } = RigidPose.Identity;

        private Quaternion _hudRotation = Quaternion.identity;
        private RigMode _hudMode = RigMode.None;

        private void UpdateHudPose(float dt)
        {
            Transform head = Head;
            Quaternion target = head.rotation;
            if (_hudMode != Mode || Mode != RigMode.OnFoot)
            {
                // Seated, the game aims turrets along this camera (FacingDirection): it must be exactly the head.
                _hudMode = Mode;
                _hudRotation = target;
                HudPose = new RigidPose(head.position, target);
                return;
            }
            // Reading the HUD and small head motions leave the frame where it is; a real turn pulls it along, faster
            // the further it trails, and it never trails by more than 35 degrees.
            float angle = Quaternion.Angle(_hudRotation, target);
            float rate = angle < 3f ? 0f : angle < 15f ? 5f : 14f;
            _hudRotation = Quaternion.Slerp(_hudRotation, target, 1f - Mathf.Exp(-rate * dt));
            if (Quaternion.Angle(_hudRotation, target) > 35f)
            {
                _hudRotation = Quaternion.RotateTowards(target, _hudRotation, 35f);
            }
            HudPose = new RigidPose(head.position, _hudRotation);
        }

        public void Update()
        {
            UpdateHeadPose();
            UpdateFade();
            IntroStage.Update();
            FpsActorController player = GameContext.Player;
            DetermineMode(player, out RigMode mode, out object key);
            if (mode != Mode || !ReferenceEquals(key, _anchorKey))
            {
                OnModeChanged(Mode, mode, key, player);
            }

            HandleTurning(player);
            _cockpit.Update();
            if (Mode == RigMode.OnFoot && player != null)
            {
                RoomscaleFollow(player);
                UpdateHeightCalibration(player);
                UpdatePhysicalCrouch(player);
                AlignMovementReference(player);
            }
            else
            {
                _hasLastHeadLocal = false;
                VRGameInput.PhysicalCrouch = false;
                _physicalCrouch = false;
            }

            bool dead = player != null && player.actor.dead;
            if (_wasDead && !dead)
            {
                VRGameInput.ResetToggles();
            }
            _wasDead = dead;

            float moving = Mode == RigMode.OnFoot ? VRGameInput.MoveStick.magnitude : 0f;
            _vignette.Update(Mathf.Max(moving * (VRGameInput.SprintActive ? 1f : 0.75f), Mathf.Abs(TurnInputThisFrame)), VRConfig.Vignette.Value);
            _damage.Update();
            _mud?.Update();
        }

        private void DetermineMode(FpsActorController player, out RigMode mode, out object key)
        {
            key = null;
            if (player != null && !GameManager.IsSpectating() && !player.inPhotoMode)
            {
                Actor actor = player.actor;
                // On foot the view is your own body's, whichever camera the game has active: a mutator's camera
                // (bodycam, cinematic shake) or the third-person toggle must not take the headset with it.
                if (actor != null && !actor.dead && !actor.fallenOver && !actor.IsSeated() && !actor.IsOnLadder() && !player.playingVictoryAnimation)
                {
                    mode = RigMode.OnFoot;
                    key = player;
                    return;
                }
                Camera active = player.GetActiveCamera();
                if (active != null && active == player.fpCamera && player.firstPerson && !player.IsUsingOverrideCamera() && !player.playingVictoryAnimation)
                {
                    if (actor.dead)
                    {
                        mode = RigMode.Follow;
                        key = player.tpCamera;
                        return;
                    }
                    if (actor.IsSeated())
                    {
                        mode = RigMode.Anchored;
                        key = actor.seat;
                        return;
                    }
                    if (actor.IsOnLadder())
                    {
                        mode = RigMode.Anchored;
                        key = actor;
                        return;
                    }
                    mode = RigMode.OnFoot;
                    key = player;
                    return;
                }
                if (active != null && actor.IsSeated() && player.IsUsingOverrideCamera())
                {
                    // Vehicle-provided cameras (turret sights, cockpit views) behave like a seat.
                    mode = RigMode.Anchored;
                    key = active;
                    return;
                }
                if (active != null)
                {
                    mode = RigMode.Follow;
                    key = active;
                    return;
                }
            }

            Camera cam = FollowTarget();
            mode = cam != null ? RigMode.Follow : RigMode.None;
            key = cam;
        }

        private static Camera[] CameraBuffer = new Camera[16];

        private static Camera FollowTarget()
        {
            Camera cam = LocalPlayer.GetCurrentCameraIncludeSpectator();
            if (cam != null && cam.isActiveAndEnabled && !IsOwnCamera(cam))
            {
                return cam;
            }
            cam = GameContext.GameMainCamera;
            if (cam != null && cam.isActiveAndEnabled && !IsOwnCamera(cam))
            {
                return cam;
            }
            // Fall back to the highest-depth screen camera (a reused buffer: Camera.allCameras makes a new array per call).
            int count = Camera.allCamerasCount;
            if (CameraBuffer.Length < count)
            {
                CameraBuffer = new Camera[count * 2];
            }
            count = Camera.GetAllCameras(CameraBuffer);
            Camera best = null;
            for (int i = 0; i < count; i++)
            {
                Camera c = CameraBuffer[i];
                if (c != null && !IsOwnCamera(c) && c.targetTexture == null && (best == null || c.depth > best.depth))
                {
                    best = c;
                }
            }
            return best;
        }

        /// <summary>
        /// The mod's own cameras (head, UI, event) ride on the rig: following one moves the rig, which moves the camera,
        /// and the view spins forever.
        /// </summary>
        private static bool IsOwnCamera(Camera c)
        {
            VRRig rig = Instance;
            return rig != null && (c == rig.Camera || rig._headCameras.Contains(c) || rig.Root != null && c.transform.IsChildOf(rig.Root));
        }

        private int _modeChanges;

        private void OnModeChanged(RigMode from, RigMode to, object key, FpsActorController player)
        {
            // Logged (sparingly): a view that keeps flipping between modes is exactly the kind of glitch to see here.
            if (++_modeChanges <= 30 || _modeChanges % 100 == 0)
            {
                Log.Info($"View: {from} -> {to} ({key}) [{_modeChanges}]");
            }
            Input.VRInput.DiagnoseFor(10f);
            bool cut = from != RigMode.None;
            Mode = to;
            _anchorKey = key;
            Vector3 hl = HeadLocal;
            Quaternion headYaw = Quaternion.Euler(0f, Camera.transform.localEulerAngles.y, 0f);
            _neutralHead = new RigidPose(hl, headYaw);
            _neutralPending = !HeadTracked;
            _seatYawOffset = 0f;
            _hasLastHeadLocal = false;

            if (to == RigMode.OnFoot && player != null)
            {
                // Face the direction the game spawned / dropped us in.
                Yaw = player.transform.eulerAngles.y - Camera.transform.localEulerAngles.y;
                if (_calibrationStart < 0f && VRConfig.EyeHeight.Value <= 0f)
                {
                    _calibrationStart = Time.unscaledTime + 1.5f;
                }
            }
            if (cut)
            {
                FadeCut();
            }
        }

        private void HandleTurning(FpsActorController player)
        {
            TurnInputThisFrame = 0f;
            if (VRConfig.Turn.Value == TurnMode.Off || GameContext.GameMenuOpen || VRGameInput.SticksOnPanel)
            {
                return;
            }
            bool allowed = Mode == RigMode.OnFoot || (Mode == RigMode.Anchored && !VehicleInput.BlocksTurning(GameContext.SeatKind));
            if (!allowed)
            {
                return;
            }
            Vector2 stick = VRInput.TurnStick;
            float x = stick.x;
            if (Mathf.Abs(x) < Mathf.Abs(stick.y) && VRConfig.StickJumpCrouch.Value && Mode == RigMode.OnFoot)
            {
                x = 0f; // a vertical push is jump/crouch, not a turn
            }

            float delta = 0f;
            if (VRConfig.Turn.Value == TurnMode.Snap)
            {
                if (!_snapLatch && Mathf.Abs(x) > 0.7f)
                {
                    _snapLatch = true;
                    delta = Mathf.Sign(x) * VRConfig.SnapTurnAngle.Value;
                }
                else if (_snapLatch && Mathf.Abs(x) < 0.35f)
                {
                    _snapLatch = false;
                }
            }
            else
            {
                float dz = VRConfig.StickDeadzone.Value;
                if (Mathf.Abs(x) > dz)
                {
                    float scaled = Mathf.Sign(x) * (Mathf.Abs(x) - dz) / (1f - dz);
                    delta = scaled * VRConfig.SmoothTurnSpeed.Value * Time.unscaledDeltaTime;
                    TurnInputThisFrame = Mathf.Abs(scaled);
                }
            }
            if (delta != 0f)
            {
                AddYaw(delta);
            }
        }

        /// <summary>Rotates the player around the head (snap/smooth turn, rotating platforms).</summary>
        public void AddYaw(float degrees)
        {
            if (Mode == RigMode.Anchored)
            {
                _seatYawOffset += degrees;
            }
            else
            {
                Yaw += degrees;
            }
        }

        private void RoomscaleFollow(FpsActorController player)
        {
            Vector3 hl = HeadLocal;
            if (!VRConfig.RoomscaleMovement.Value || VRConfig.SeatedMode.Value)
            {
                _lastHeadLocal = hl;
                _hasLastHeadLocal = true;
                return;
            }
            if (_hasLastHeadLocal)
            {
                Vector3 dLocal = hl - _lastHeadLocal;
                dLocal.y = 0f;
                Vector3 d = Quaternion.Euler(0f, Yaw, 0f) * dLocal;
                float sq = d.sqrMagnitude;
                // Ignore sub-millimetre jitter and tracking jumps (> 35 cm in one frame).
                if (sq > 1e-7f && sq < 0.1225f && player.controller != null && player.IsMovementEnabled())
                {
                    Vector3 before = player.transform.position;
                    player.controller.Move(d);
                    Vector3 actual = player.transform.position - before;
                    // Keep these steps out of the game's fixed-step smoothing so head motion stays 1:1.
                    SmoothPlayerMovement spm = player.smoothPlayerMovement;
                    if (spm != null)
                    {
                        spm.lastFixedWorldPos += actual;
                        spm.nextFixedWorldPos += actual;
                    }
                }
            }
            _lastHeadLocal = hl;
            _hasLastHeadLocal = true;
        }

        private readonly List<float> _calibration = new List<float>(160);

        /// <summary>
        /// Standing eye height from two seconds after spawning: the 75th percentile of the head heights seen (looking
        /// down at your hands lowers the head a little; that must not count), only if it is a plausible standing
        /// height. A window that cannot gather enough samples gives up instead of averaging in whatever comes later
        /// (a crouch, a seated moment), and is tried again on the next spawn.
        /// </summary>
        private void UpdateHeightCalibration(FpsActorController player)
        {
            if (_calibrationStart < 0f || Time.unscaledTime < _calibrationStart)
            {
                return;
            }
            float elapsed = Time.unscaledTime - _calibrationStart;
            float y = HeadLocal.y;
            if (HeadTracked && y > 0.5f && _calibration.Count < _calibration.Capacity)
            {
                _calibration.Add(y);
            }
            if (elapsed < 2f)
            {
                return;
            }
            if (_calibration.Count >= 30)
            {
                _calibration.Sort();
                float h = _calibration[(int)(_calibration.Count * 0.75f)];
                if (h >= 1.0f && h <= 2.1f)
                {
                    VRConfig.EyeHeight.Value = Mathf.Round(h * 100f) / 100f;
                    Log.Info($"Calibrated eye height: {VRConfig.EyeHeight.Value:0.00} m");
                }
                else
                {
                    Log.Info($"Eye height {h:0.00} m does not look like standing; not saved (use Calibrate now, or Seated play).");
                }
                EndCalibration();
            }
            else if (elapsed > 6f)
            {
                EndCalibration(); // not enough tracked samples: try again on the next spawn
            }
        }

        private void EndCalibration()
        {
            _calibrationStart = -1f;
            _calibration.Clear();
        }

        /// <summary>Uses the current head height as standing (or seated) eye height.</summary>
        public void CalibrateHeightNow()
        {
            float y = HeadLocal.y;
            if (y > 0.3f)
            {
                VRConfig.EyeHeight.Value = Mathf.Round(y * 100f) / 100f;
                Log.Info($"Eye height set to {VRConfig.EyeHeight.Value:0.00} m");
            }
        }

        /// <summary>Re-centres seated/follow views on the current head pose.</summary>
        public void Recenter()
        {
            _neutralHead = new RigidPose(HeadLocal, Quaternion.Euler(0f, Camera.transform.localEulerAngles.y, 0f));
            _seatYawOffset = 0f;
            FpsActorController player = GameContext.Player;
            if (Mode == RigMode.OnFoot && player != null)
            {
                Yaw = player.transform.eulerAngles.y - Camera.transform.localEulerAngles.y;
            }
        }

        private float EyeHeight
        {
            get
            {
                float h = VRConfig.EyeHeight.Value;
                if (h > 0.3f && !(HeadLocal.y < 0.3f && (AutoTest.Requested || Shots.Enabled)))
                {
                    return h;
                }
                // Automated runs on SteamVR's virtual headset: it reports the head on the floor (0, 0, 0), and with a
                // player's calibrated height the view would sit under the map with the hands below the ground.
                // Before calibration, treat the current height as standing height, whatever it is: a floor set too
                // high (or a headset reporting floor level) must still put the eyes at the game's eye height.
                return HeadLocal.y;
            }
        }

        private void UpdatePhysicalCrouch(FpsActorController player)
        {
            if (!VRConfig.PhysicalCrouch.Value || VRConfig.SeatedMode.Value || VRConfig.EyeHeight.Value <= 0.3f
                || HeadLocal.y < 0.3f && (AutoTest.Requested || Shots.Enabled)) // the virtual headset's head on the floor is not a crouch
            {
                _physicalCrouch = false;
                VRGameInput.PhysicalCrouch = false;
                return;
            }
            float ratio = HeadLocal.y / VRConfig.EyeHeight.Value;
            float threshold = VRConfig.PhysicalCrouchRatio.Value;
            if (!_physicalCrouch && ratio < threshold)
            {
                _physicalCrouch = true;
            }
            else if (_physicalCrouch && ratio > threshold + 0.07f)
            {
                _physicalCrouch = false;
            }
            VRGameInput.PhysicalCrouch = _physicalCrouch;
        }

        private void AlignMovementReference(FpsActorController player)
        {
            float yaw;
            if (VRConfig.MoveDirection.Value == MoveReference.OffHand && Off.Tracked)
            {
                Vector3 f = Off.Pointer.Forward;
                f.y = 0f;
                yaw = f.sqrMagnitude > 1e-4f ? Quaternion.LookRotation(f).eulerAngles.y : Head.eulerAngles.y;
            }
            else
            {
                yaw = Head.eulerAngles.y;
            }
            Quaternion flat = Quaternion.Euler(0f, yaw, 0f);
            Transform root = player.fpCameraRoot;
            if (root != null)
            {
                root.rotation = flat;
            }
            FirstPersonController fpc = player.controller;
            if (fpc != null && fpc.cameraParent != null && fpc.cameraParent != root)
            {
                fpc.cameraParent.rotation = flat;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // BeforeRender: final placement for this frame
        // ---------------------------------------------------------------------------------------------

        public void BeforeRender()
        {
            UpdateHeadPose();
            if (_neutralPending)
            {
                _neutralPending = false;
                Recenter();
            }
            FpsActorController player = GameContext.Player;
            Camera reference = PlaceOrigin(player);

            foreach (VRHand h in Hands)
            {
                h.UpdatePose(Root);
            }
            _cockpit.BeforeRender();

            UpdateHudPose(Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            if (player != null && (Mode == RigMode.OnFoot || Mode == RigMode.Anchored) && player.fpCamera != null)
            {
                // Game logic reads the head through the game camera; it also projects the HUD markers, so it takes
                // the HUD frame's (lazily following) orientation and the markers stay on their targets.
                player.fpCamera.transform.SetPositionAndRotation(HudPose.position, HudPose.rotation);
            }

            if (reference != null)
            {
                CopyCameraSettings(reference, player);
            }
            if (Blackout)
            {
                // Loading: a dark room around the loading screen (whatever the scenes' cameras clear to stays unseen).
                Camera.cullingMask = 0;
                Camera.clearFlags = CameraClearFlags.SolidColor;
                Camera.backgroundColor = Color.black;
            }
        }

        /// <summary>Draw nothing of the world (set while a map loads; see UI.LoadingView).</summary>
        public static bool Blackout;

        /// <summary>The main menu (build index 1), whose background camera drifts and turns over a map.</summary>
        private static bool InMainMenu => UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex == 1;

        // Where the view stays in the main menu, and for which camera it was taken.
        private Camera _stillFor;
        private RigidPose _stillAnchor;
        private Vector3 _stillLastSeen;

        private Camera PlaceOrigin(FpsActorController player)
        {
            Vector3 hl = HeadLocal;
            switch (Mode)
            {
                case RigMode.OnFoot:
                {
                    Transform anchorT = player.fpParent != null ? player.fpParent.fpCameraParent : player.fpCameraRoot;
                    Vector3 anchor = anchorT.position;
                    Quaternion r = Quaternion.Euler(0f, Yaw, 0f);
                    Vector3 origin = anchor - r * new Vector3(hl.x, 0f, hl.z);
                    float compensation = 0f;
                    if (_physicalCrouch && !VRGameInput.CrouchToggled && !VRGameInput.ProneToggled)
                    {
                        compensation = GameStandEyeHeight - player.cameraHeight;
                    }
                    origin.y = anchor.y - EyeHeight + compensation;
                    Root.SetPositionAndRotation(origin, r);
                    return player.fpCamera;
                }
                case RigMode.Anchored:
                {
                    RigidPose anchor = AnchorPose(player, out Camera cam);
                    anchor.rotation *= Quaternion.Euler(0f, _seatYawOffset, 0f);
                    anchor.Multiply(_neutralHead.Inverse()).ApplyTo(Root);
                    return cam;
                }
                case RigMode.Follow:
                {
                    Camera cam = _anchorKey as Camera;
                    if (cam == null)
                    {
                        return null;
                    }
                    if (!IntroStage.TryGetAnchor(cam, out RigidPose anchor))
                    {
                        anchor = new RigidPose(cam.transform.position, Quaternion.Euler(0f, cam.transform.eulerAngles.y, 0f));
                        if (InMainMenu)
                        {
                            // The menu's background camera drifts and turns over the map: following it turned the world
                            // around you while you read the menu. You stay where it first was; only a real cut (the camera
                            // put somewhere else, two metres or more in one frame) moves you, through a fade.
                            bool cutHere = ReferenceEquals(_stillFor, cam) && Vector3.Distance(anchor.position, _stillLastSeen) > 2f;
                            if (!ReferenceEquals(_stillFor, cam) || cutHere)
                            {
                                if (cutHere)
                                {
                                    FadeCut();
                                }
                                _stillFor = cam;
                                _stillAnchor = anchor;
                            }
                            _stillLastSeen = anchor.position;
                            anchor = _stillAnchor;
                        }
                        else
                        {
                            _stillFor = null;
                        }
                    }
                    anchor.Multiply(_neutralHead.Inverse()).ApplyTo(Root);
                    return cam;
                }
                default:
                    return null;
            }
        }

        private RigidPose AnchorPose(FpsActorController player, out Camera cam)
        {
            cam = player != null ? player.fpCamera : null;
            if (_anchorKey is Camera overrideCam && overrideCam != null)
            {
                cam = overrideCam;
                // Take the camera's position but the vehicle's orientation: turret cameras rotate with the turret,
                // and following that rotation would make head-aimed turrets chase their own view.
                Seat seatOfCam = player != null && player.actor.IsSeated() ? player.actor.seat : null;
                Quaternion rot = seatOfCam != null ? seatOfCam.transform.rotation : overrideCam.transform.rotation;
                return Level(new RigidPose(overrideCam.transform.position, rot));
            }
            if (player == null)
            {
                return RigidPose.Identity;
            }
            Transform root = player.fpCameraRoot;
            if (_anchorKey is Seat seat && seat != null)
            {
                return Level(new RigidPose(root.position, seat.transform.rotation));
            }
            // Ladder: camera root hangs off the actor; keep the actor's facing.
            return new RigidPose(root.position, Quaternion.Euler(0f, player.actor.transform.eulerAngles.y, 0f));
        }

        private static RigidPose Level(RigidPose p)
        {
            if (!VRConfig.SmoothVehicleHorizon.Value)
            {
                return p;
            }
            return new RigidPose(p.position, Quaternion.Euler(0f, p.rotation.eulerAngles.y, 0f));
        }

        private Camera _cullSource;
        private int _nextCullCopy;

        private void CopyCameraSettings(Camera reference, FpsActorController player)
        {
            // UI (layer 5) is drawn by the UI camera on top of the world; the weapon layer is drawn here with real depth.
            int mask = (reference.cullingMask | (1 << FirstPersonLayer)) & ~(1 << UiLayer);
            CameraClearFlags clear = reference.clearFlags;
            Color background = reference.backgroundColor;
            float far = Mathf.Max(reference.farClipPlane, VRConfig.MinFarClip.Value);

            if (clear == CameraClearFlags.Depth || clear == CameraClearFlags.Nothing)
            {
                // The game draws sky / distant scenery with a separate camera behind this one; do it in one pass.
                Camera backgroundCam = BackgroundCamera.instance != null ? BackgroundCamera.instance.camera : null;
                if (backgroundCam != null)
                {
                    mask |= (backgroundCam.cullingMask | (1 << BackgroundLayer)) & ~(1 << UiLayer);
                    far = Mathf.Max(far, backgroundCam.farClipPlane);
                }
                Camera scenery = SceneryCamera.instance != null ? SceneryCamera.instance.camera : null;
                if (scenery != null && scenery.clearFlags == CameraClearFlags.SolidColor)
                {
                    clear = CameraClearFlags.SolidColor;
                    background = scenery.backgroundColor;
                }
                else
                {
                    clear = RenderSettings.skybox != null ? CameraClearFlags.Skybox : CameraClearFlags.SolidColor;
                }
            }

            Camera.cullingMask = mask;
            Camera.clearFlags = clear;
            Camera.backgroundColor = background;
            Camera.farClipPlane = Mathf.Min(far, GraphicsPresets.FarClipCap);
            Camera.nearClipPlane = VRConfig.NearClip.Value;
            Camera.allowHDR = reference.allowHDR;
            Camera.allowMSAA = GraphicsPresets.ForceMsaa ?? reference.allowMSAA;
            Camera.renderingPath = reference.renderingPath;
            // Only what the game camera itself asks for: forcing a depth texture makes the forward renderer draw every
            // opaque object once more per eye, which in multi-pass stereo cost more frame time than anything else here.
            Camera.depthTextureMode = reference.depthTextureMode;
            Camera.useOcclusionCulling = reference.useOcclusionCulling;
            Camera.layerCullSpherical = reference.layerCullSpherical;
            // layerCullDistances returns a fresh array on every read: copy it when the source camera changes and
            // twice a second after that, not every frame.
            if (reference != _cullSource || Time.frameCount >= _nextCullCopy)
            {
                _cullSource = reference;
                _nextCullCopy = Time.frameCount + 45;
                float[] cull = reference.layerCullDistances;
                if (cull != null && cull.Length == 32)
                {
                    cull[UiLayer] = 0f;
                    cull[FirstPersonLayer] = 0f;
                    Camera.layerCullDistances = cull;
                }
            }
            UpdatePostProcessing(reference);
        }

        private void UpdatePostProcessing(Camera reference)
        {
            bool want = VRConfig.PostProcessing.Value;
            PostProcessLayer source = reference.GetComponent<PostProcessLayer>();
            if (!want || source == null || PpResources == null)
            {
                if (_postProcess != null && _postProcess.enabled)
                {
                    _postProcess.enabled = false;
                }
                return;
            }
            if (_postProcess == null)
            {
                object resources = PpResources.GetValue(source);
                if (resources == null)
                {
                    return;
                }
                GameObject go = Camera.gameObject;
                go.SetActive(false);
                _postProcess = go.AddComponent<PostProcessLayer>();
                PpResources.SetValue(_postProcess, resources);
                go.SetActive(true);
            }
            _postProcess.volumeLayer = source.volumeLayer;
            _postProcess.volumeTrigger = Camera.transform;
            // Temporal AA smears in VR; fall back to FXAA when the game uses it.
            _postProcess.antialiasingMode = source.antialiasingMode == PostProcessLayer.Antialiasing.TemporalAntialiasing
                ? PostProcessLayer.Antialiasing.FastApproximateAntialiasing
                : source.antialiasingMode;
            _postProcess.stopNaNPropagation = true;
            if (!_postProcess.enabled)
            {
                _postProcess.enabled = true;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Fades
        // ---------------------------------------------------------------------------------------------

        /// <summary>Quick fade through black to hide camera cuts and teleports.</summary>
        public void FadeCut()
        {
            CVRCompositor comp = OpenVR.Compositor;
            if (comp == null)
            {
                return;
            }
            comp.FadeToColor(0.05f, 0f, 0f, 0f, 1f, false);
            _fadeInAt = Time.unscaledTime + 0.12f;
        }

        private void UpdateFade()
        {
            if (_fadeInAt > 0f && Time.unscaledTime >= _fadeInAt)
            {
                _fadeInAt = -1f;
                OpenVR.Compositor?.FadeToColor(0.25f, 0f, 0f, 0f, 0f, false);
            }
        }

        public void Dispose()
        {
            Suppressor.Dispose();
            _vignette.Destroy();
            _damage.Destroy();
            _mud?.Destroy();
            foreach (VRHand h in Hands)
            {
                h.Destroy();
            }
            if (Root != null)
            {
                UnityEngine.Object.Destroy(Root.gameObject);
            }
            OpenVR.Compositor?.FadeToColor(0f, 0f, 0f, 0f, 0f, false);
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }

    /// <summary>Rotating platforms add yaw through MouseLook; in VR that yaw turns the rig instead.</summary>
    [HarmonyPatch(typeof(MouseLook), nameof(MouseLook.ApplyScriptedRotation))]
    internal static class ScriptedRotationPatch
    {
        private static bool Prefix(Vector2 input)
        {
            VRRig rig = VRRig.Instance;
            if (rig == null || !VRGameInput.Enabled)
            {
                return true;
            }
            rig.AddYaw(input.x);
            return false;
        }
    }
}
