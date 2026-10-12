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
        private float _calibrationSum;
        private int _calibrationSamples;
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

            Suppressor = new CameraSuppressor(Camera);
            Hands[0] = new VRHand(Input.Hand.Left, Root);
            Hands[1] = new VRHand(Input.Hand.Right, Root);
            _vignette = new ComfortVignette(Camera.transform);
            _cockpit = new CockpitControls(this);
        }

        // ---------------------------------------------------------------------------------------------
        // Update: input-driven rig changes (turning, roomscale body moves, crouch, calibration)
        // ---------------------------------------------------------------------------------------------

        public void Update()
        {
            UpdateFade();
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
        }

        private void DetermineMode(FpsActorController player, out RigMode mode, out object key)
        {
            key = null;
            if (player != null && !GameManager.IsSpectating() && !player.inPhotoMode)
            {
                Actor actor = player.actor;
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

        private static Camera FollowTarget()
        {
            Camera cam = LocalPlayer.GetCurrentCameraIncludeSpectator();
            if (cam != null && cam.isActiveAndEnabled)
            {
                return cam;
            }
            cam = GameContext.GameMainCamera;
            if (cam != null && cam.isActiveAndEnabled && cam != Instance?.Camera)
            {
                return cam;
            }
            // Fall back to the highest-depth screen camera.
            Camera best = null;
            foreach (Camera c in Camera.allCameras)
            {
                if (c != Instance?.Camera && c.targetTexture == null && (best == null || c.depth > best.depth))
                {
                    best = c;
                }
            }
            return best;
        }

        private void OnModeChanged(RigMode from, RigMode to, object key, FpsActorController player)
        {
            Log.Debug($"Rig mode {from} -> {to} ({key})");
            bool cut = from != RigMode.None;
            Mode = to;
            _anchorKey = key;
            Vector3 hl = HeadLocal;
            Quaternion headYaw = Quaternion.Euler(0f, Camera.transform.localEulerAngles.y, 0f);
            _neutralHead = new RigidPose(hl, headYaw);
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
            if (VRConfig.Turn.Value == TurnMode.Off || GameContext.GameMenuOpen)
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

        private void UpdateHeightCalibration(FpsActorController player)
        {
            if (_calibrationStart < 0f || Time.unscaledTime < _calibrationStart)
            {
                return;
            }
            float y = HeadLocal.y;
            if (y > 0.5f)
            {
                _calibrationSum += y;
                _calibrationSamples++;
            }
            if (Time.unscaledTime > _calibrationStart + 1f && _calibrationSamples > 10)
            {
                float h = _calibrationSum / _calibrationSamples;
                VRConfig.EyeHeight.Value = Mathf.Round(h * 100f) / 100f;
                Log.Info($"Calibrated eye height: {VRConfig.EyeHeight.Value:0.00} m");
                _calibrationStart = -1f;
                _calibrationSum = 0f;
                _calibrationSamples = 0;
            }
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
                if (h > 0.3f)
                {
                    return h;
                }
                // Before calibration, treat the current height as standing height.
                return Mathf.Max(0.5f, HeadLocal.y);
            }
        }

        private void UpdatePhysicalCrouch(FpsActorController player)
        {
            if (!VRConfig.PhysicalCrouch.Value || VRConfig.SeatedMode.Value || VRConfig.EyeHeight.Value <= 0.3f)
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
            FpsActorController player = GameContext.Player;
            Camera reference = PlaceOrigin(player);

            foreach (VRHand h in Hands)
            {
                h.UpdatePose(Root);
            }

            if (player != null && (Mode == RigMode.OnFoot || Mode == RigMode.Anchored) && player.fpCamera != null)
            {
                // Game logic reads the head through the game camera.
                player.fpCamera.transform.SetPositionAndRotation(Head.position, Head.rotation);
            }

            if (reference != null)
            {
                CopyCameraSettings(reference, player);
            }
        }

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
                    var anchor = new RigidPose(cam.transform.position, Quaternion.Euler(0f, cam.transform.eulerAngles.y, 0f));
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
            Camera.farClipPlane = far;
            Camera.nearClipPlane = VRConfig.NearClip.Value;
            Camera.allowHDR = reference.allowHDR;
            Camera.allowMSAA = reference.allowMSAA;
            Camera.renderingPath = reference.renderingPath;
            Camera.depthTextureMode = reference.depthTextureMode | DepthTextureMode.Depth;
            Camera.useOcclusionCulling = reference.useOcclusionCulling;
            Camera.layerCullSpherical = reference.layerCullSpherical;
            float[] cull = reference.layerCullDistances;
            if (cull != null && cull.Length == 32)
            {
                cull[UiLayer] = 0f;
                cull[FirstPersonLayer] = 0f;
                Camera.layerCullDistances = cull;
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
