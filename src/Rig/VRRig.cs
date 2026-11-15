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
        private readonly UnderwaterVeil _underwater;
        private readonly LensEffects _lens;
        private readonly CockpitControls _cockpit;
        private readonly TurretGrip _turretGrip;
        private readonly VehicleInterior _interior;
        private HullInside _hullInside;

        /// <summary>For the vehicle check: the closed vehicle's own body drawn from inside.</summary>
        internal HullInside Hull => _hullInside;
        private readonly VehicleTouchEnter _touchEnter;

        /// <summary>The steering wheel and flight stick the hands grab in a seat.</summary>
        public CockpitControls Cockpit => _cockpit;

        public RigMode Mode { get; private set; } = RigMode.None;
        public Transform Head => Camera.transform;
        public RigidPose HeadPose => RigidPose.Of(Camera.transform);
        public Vector3 HeadLocal => Camera.transform.localPosition;

        /// <summary>The hand holding the drawn weapon (<see cref="VRInput.GunHand"/>): the dominant hand unless the weapon was passed over.</summary>
        public VRHand Dominant => Hands[(int)VRInput.GunHand];
        /// <summary>The other hand: support grip, magazines, racking.</summary>
        public VRHand Off => Hands[(int)VRInput.FreeHand];
        /// <summary>The off hand as set in the settings (the wrist display stays on its wrist wherever the weapon is).</summary>
        public VRHand SetOff => Hands[(int)VRInput.OffHand];
        /// <summary>The dominant hand as set in the settings (turret aiming).</summary>
        public VRHand SetDominant => Hands[(int)VRInput.Dominant];
        public VRHand Hand(Hand side) => Hands[(int)side];

        /// <summary>World yaw of the tracking space while on foot (degrees).</summary>
        public float Yaw { get; private set; }

        private float _seatYawOffset;
        private object _anchorKey;
        private RigidPose _neutralHead = RigidPose.Identity;
        private Seat _aliveSeat;
        private float _diedInSeatAt = -1f;
        /// <summary>The head's neutral pose in the rig's root (where the anchor puts the eyes; its forward is the seat's or the sight's).</summary>
        internal RigidPose NeutralHead => _neutralHead;
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

        /// <summary>Showcase only: the eyes' post-processing (the grade, the goggles' green), to be mirrored by its camera; null when none.</summary>
        internal PostProcessLayer EyePostProcess => _postProcess;

        /// <summary>Showcase only: the post-processing resources a new layer needs (the eyes' own way of getting them).</summary>
        internal static object PostProcessResources(PostProcessLayer from) => from != null && PpResources != null ? PpResources.GetValue(from) : null;

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

            try
            {
                Suppressor = new CameraSuppressor(Camera);
                Hands[0] = new VRHand(Input.Hand.Left, Root);
                Hands[1] = new VRHand(Input.Hand.Right, Root);
                _vignette = new ComfortVignette(Camera.transform);
                try
                {
                    _lens = new LensEffects(this); // before DamageFeedback and MudSplash: they hand it their effects
                }
                catch (System.Exception e)
                {
                    Log.Exception("The on-screen effects could not be set up; playing without them", e);
                }
                _damage = new DamageFeedback(Camera.transform);
                try
                {
                    _mud = new MudSplash(Camera.transform);
                    _underwater = new UnderwaterVeil(Camera.transform);
                }
                catch (System.Exception e)
                {
                    // A look, not a function: VR starts without it rather than not at all.
                    Log.Exception("Explosion dirt could not be set up; playing without it", e);
                }
                _cockpit = new CockpitControls(this);
                _turretGrip = new TurretGrip(this);
                _interior = new VehicleInterior(this);
                _hullInside = new HullInside(Camera);
                _touchEnter = new VehicleTouchEnter(this);
            }
            catch
            {
                // Torn down before it goes up to VRManager (which shuts VR down without a rig to dispose): the root, kept across
                // scenes, and its head camera at depth 100 stayed and could paint over the flat game (lifecycle hunt, 2026-10-20).
                Dispose();
                throw;
            }
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
                // A view entered before the headset reported its first pose has a meaningless neutral pose (and on foot a
                // yaw worked out from an untracked head: the view stayed turned by wherever the headset faced).
                _neutralPending = true;
            }
        }

        private bool _neutralPending;
        /// <summary>The on-foot yaw was worked out before the headset's first pose (from an untracked head): worked out again at it.</summary>
        private bool _yawFromUntrackedHead;

        // ---- view shake when shooting (Tasks 211) -------------------------------------------------------------

        private float _shakePitch, _shakeYaw, _shakeAt = -10f;
        private float _shakeBudget;

        /// <summary>Self-test: the shake's largest turn of the view so far (degrees).</summary>
        internal static float PeakShake;

        /// <summary>
        /// A shot jolts the view a little (ViewShake): a fraction of a degree, up and a touch sideways, gone in about 60 ms,
        /// never a movement of the camera, never a sway. Bigger for a harder kick; on full auto it is capped (a budget that
        /// refills over a second), so a long burst does not become a steady wobble. Moving the view on its own is what makes
        /// people sick in VR: off at 0, low by default.
        /// </summary>
        public void ShakeView(float kick)
        {
            float strength = VRConfig.ViewShake.Value;
            if (strength <= 0f || Mode != RigMode.OnFoot)
            {
                return;
            }
            float now = Time.unscaledTime;
            _shakeBudget = Mathf.Min(1f, _shakeBudget + (now - _shakeAt) * 1.2f);
            float jolt = Mathf.Clamp(0.25f + 0.2f * kick, 0.2f, 0.8f) * strength * Mathf.Clamp01(0.25f + _shakeBudget);
            _shakeBudget = Mathf.Max(0f, _shakeBudget - 0.35f);
            _shakePitch = Mathf.Min(0.8f, Mathf.Max(_shakePitch * Mathf.Exp(-(now - _shakeAt) / 0.02f), 0f) + jolt);
            _shakeYaw = UnityEngine.Random.Range(-0.3f, 0.3f) * jolt;
            _shakeAt = now;
            PeakShake = Mathf.Max(PeakShake, _shakePitch);
        }

        /// <summary>
        /// The root's pose this frame before the view shake turned it (the hands take the rig's motion from it: the shake of every
        /// shot turned the rig about the eyes, and a magazine let go right after a shot, an empty pistol's, flew off with it;
        /// 2026-10-20, task 315).
        /// </summary>
        private Matrix4x4 _steadyRoot = Matrix4x4.identity;
        private Quaternion _steadyRootRotation = Quaternion.identity;
        private int _steadyFrame = -1;

        /// <summary>
        /// The frame a snap turn was applied in: a jump of the rig, not motion (the hands give nothing let go its speed, the gun's
        /// stand-in jumps with it). Flagged where it happens: an angle per frame told it from a smooth turn only at some frame
        /// rates (360 deg/s at 45 Hz is 8 degrees a frame; review, 2026-10-20).
        /// </summary>
        internal static int LastSnapFrame = -10;
        /// <summary>The last snap turn's angle about the rig's up axis (degrees, signed), for what is carried in the world through it.</summary>
        internal static float LastSnapDegrees;

        /// <summary>A snap turn in this frame or the one before (read in Update, before this frame's placement).</summary>
        internal static bool SnappedRecently => Time.frameCount - LastSnapFrame <= 1;

        private Matrix4x4 _shakeUndo = Matrix4x4.identity;
        private Quaternion _shakeUndoRotation = Quaternion.identity;

        /// <summary>
        /// What takes a world pose of the last placed frame out of that frame's view shake (identity without one): the held gun's
        /// physics stand-in follows the gun as the hand holds it, not as the shot's jolt turned the view (it swung through what lay
        /// by it on every shot; task 315).
        /// </summary>
        internal static void ShakeUndo(out Matrix4x4 undo, out Quaternion rotation)
        {
            VRRig rig = Instance;
            undo = rig != null ? rig._shakeUndo : Matrix4x4.identity;
            rotation = rig != null ? rig._shakeUndoRotation : Quaternion.identity;
        }

        /// <summary><paramref name="root"/>'s pose this frame without the view shake (its own pose when it is not this rig's or none was kept).</summary>
        internal static Matrix4x4 SteadyRootOf(Transform root, out Quaternion rotation)
        {
            VRRig rig = Instance;
            if (rig != null && rig.Root == root && rig._steadyFrame == Time.frameCount)
            {
                rotation = rig._steadyRootRotation;
                return rig._steadyRoot;
            }
            rotation = root.rotation;
            return root.localToWorldMatrix;
        }

        /// <summary>The jolt, fading (a 20 ms time constant: gone within about 60 ms), turned about the eyes.</summary>
        private void ApplyViewShake()
        {
            float age = Time.unscaledTime - _shakeAt;
            if (age > 0.08f || _shakePitch <= 0f || Camera == null)
            {
                return;
            }
            float k = Mathf.Exp(-age / 0.02f);
            Vector3 eyes = Camera.transform.position;
            Root.RotateAround(eyes, Camera.transform.right, -_shakePitch * k);
            Root.RotateAround(eyes, Vector3.up, _shakeYaw * k);
        }

        /// <summary>The game's camera is lowered by a physical crouch (still, while it eases back up after it).</summary>
        private bool _crouchLowered;
        private float _crouchCompensation;
        private float _crouchReleasedAt = -1f;

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
            _turretGrip?.Update(); // (an open mounted gun by its handles)
            Log.Guard("Rig.HullInside", () => _hullInside?.Update()); // (a closed vehicle's own walls drawn from inside: no see-through hull)
            Log.Guard("Rig.Interior", () => _interior?.Update()); // (a closed vehicle's inside, its handwheels and buttons)
            VehicleFeel.Update(this);
            _touchEnter.Update(); // after the cockpit: a hand on the rim is not also a hand at a door (and on foot only, so never both)
            if (Mode == RigMode.OnFoot && player != null && player.actor != null && player.actor.IsSeated())
            {
                // Seated by a hand at a door just now: the seat's mode from this frame. One frame of the on-foot follow ran on the
                // seated actor and the view was placed from the old yaw (rig hunt, 2026-10-21).
                DetermineMode(player, out mode, out key);
                if (mode != Mode || !ReferenceEquals(key, _anchorKey))
                {
                    OnModeChanged(Mode, mode, key, player);
                }
            }
            if (Mode == RigMode.OnFoot && player != null)
            {
                RoomscaleFollow(player);
                UpdateHeightCalibration(player);
                RaiseSavedHeight();
                UpdatePhysicalCrouch(player);
                AlignMovementReference(player);
            }
            else
            {
                _hasLastHeadLocal = false;
                VRGameInput.PhysicalCrouch = false;
                _physicalCrouch = false;
            }

            bool dead = player != null && (player.actor == null || player.actor.dead); // no body (between death and respawn) counts as dead
            if (_wasDead && !dead)
            {
                VRGameInput.ResetToggles();
            }
            _wasDead = dead;

            // (Not a stick held down into a menu or a pause: nothing moves there, and the tunnel stayed over the menu until it was let go.)
            float moving = Mode == RigMode.OnFoot && !GameContext.GameMenuOpen && !GameManager.IsPaused() ? VRGameInput.MoveStick.magnitude : 0f;
            moving = Mathf.Max(moving, SeatTurnMotion(player));
            // (Through the goggles the edge of the view stays dark: the game's tube picture, which in VR was a flat frame, as the tunnel draws it.)
            _vignette.Update(Mathf.Max(moving * (VRGameInput.SprintActive ? 1f : 0.75f), Mathf.Abs(TurnInputThisFrame)), VRConfig.Vignette.Value,
                NightVision.isEnabled ? VRConfig.GogglesTube.Value : 0f);
            _damage.Update();
            _mud?.Update();
            _underwater?.Update();
            UpdateGunShade(player);
        }

        /// <summary>The sun with the hands and gun taken out of its light (still set: the mask to put back), and since when the shade test has said otherwise.</summary>
        private Light _shadedSun;
        private int _sunMask;
        private float _shadeChangeSince = -1f;

        /// <summary>
        /// The flat game keeps its view model out of the sun in shade: <c>FPLightIntensity</c> blacks the sun while the first-person camera alone
        /// renders, by the game's own ray toward it. The eyes draw the gun in the world's pass, where that hook never runs, so with shadows off a gun
        /// and the arms stayed sunlit inside a building (2026-11-07). The same here by the light's culling mask: the layer the hands and gun are on is
        /// left out of the sun while the game says the player is in shade. A moment's wait each way: at a pillar's edge the test flips with the head.
        /// </summary>
        private void UpdateGunShade(FpsActorController player)
        {
            Light sun = GameManager.instance != null ? GameManager.instance.sceneSunlight : null;
            bool shade = sun != null && player != null && player.fpLightIntensity != null && player.fpLightIntensity.isInShadow;
            if (_shadedSun != null && _shadedSun != sun)
            {
                RestoreSun();
            }
            if (shade == (_shadedSun != null))
            {
                _shadeChangeSince = -1f;
                return;
            }
            float now = Time.unscaledTime;
            if (_shadeChangeSince < 0f)
            {
                _shadeChangeSince = now;
            }
            if (now - _shadeChangeSince < 0.2f)
            {
                return;
            }
            _shadeChangeSince = -1f;
            if (shade)
            {
                _shadedSun = sun;
                _sunMask = sun.cullingMask;
                sun.cullingMask = _sunMask & ~(1 << FirstPersonLayer);
            }
            else
            {
                RestoreSun();
            }
        }

        private void RestoreSun()
        {
            if (_shadedSun != null)
            {
                _shadedSun.cullingMask = _sunMask;
            }
            _shadedSun = null;
        }

        private void DetermineMode(FpsActorController player, out RigMode mode, out object key)
        {
            key = null;
            if (player == null || GameManager.IsSpectating() || player.inPhotoMode)
            {
                UnshrinkHead(); // (photo mode or spectating while down: the head stayed shrunk; scout)
            }
            if (player != null && !GameManager.IsSpectating() && !player.inPhotoMode)
            {
                Actor actor = player.actor;
                // On foot the view is your own body's, whichever camera the game has active: a mutator's camera
                // (bodycam, cinematic shake) or the third-person toggle must not take the headset with it.
                if (actor != null && !actor.dead && !actor.fallenOver && !actor.IsSeated() && !actor.IsOnLadder() && !player.playingVictoryAnimation)
                {
                    UnshrinkHead();
                    mode = RigMode.OnFoot;
                    key = player;
                    return;
                }
                // Knocked over: the view at his own head as he falls and lies, not the game's camera behind him (the user, 2026-11-11:
                // "when I fall I want to fall in first person"). The head's place only; its turn is the player's own (no roll).
                // (Kept for a frame whose head is not found: it fell through to the feet's view and back, a blackout each way, and the
                // fall's heading was latched again from the turned lying body; falling scout, 2026-11-12. Not over a match's end.)
                if (actor != null && !actor.dead && actor.fallenOver && VRConfig.FallInFirstPerson.Value && !player.playingVictoryAnimation
                    && (FallenHead(actor) != null || Mode == RigMode.Anchored && ReferenceEquals(_anchorKey, actor.ragdoll) && _hasFallPose))
                {
                    mode = RigMode.Anchored;
                    key = actor.ragdoll;
                    return;
                }
                UnshrinkHead(); // (up again, dead, or in a seat: his head as it is drawn)
                // Killed in a seat: the view stays in it for a moment, fading to dark red, before the game's camera behind takes it (it cut
                // at once from inside the vehicle to outside; vehicles scout, 2026-11-15).
                if (actor != null && !actor.dead && actor.IsSeated())
                {
                    _aliveSeat = actor.seat;
                    _diedInSeatAt = -1f;
                }
                else if (actor != null && actor.dead && _aliveSeat != null)
                {
                    if (_diedInSeatAt < 0f)
                    {
                        _diedInSeatAt = Time.unscaledTime;
                        OpenVR.Compositor?.FadeToColor(1.2f, 0.25f, 0f, 0f, 0.9f, false);
                    }
                    if (Time.unscaledTime - _diedInSeatAt < 1.4f && _aliveSeat != null && _aliveSeat.transform != null)
                    {
                        mode = RigMode.Anchored;
                        key = _aliveSeat;
                        return;
                    }
                    _aliveSeat = null; // (the change of view fades back in by itself: a fade here was cut by its black blip)
                }
                else if (actor == null || !actor.dead)
                {
                    _aliveSeat = null;
                }
                Camera active = player.GetActiveCamera();
                // actor != null here: without a body (a frame between death and respawn) the actor-based checks below would throw;
                // the camera fallbacks further down take over instead (review 2026-10-21).
                if (active != null && actor != null && active == player.fpCamera && player.firstPerson && !player.IsUsingOverrideCamera() && !player.playingVictoryAnimation)
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
                if (active != null && actor != null && actor.IsSeated() && player.IsUsingOverrideCamera())
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
            Camera cam = null;
            try
            {
                // The game's getter tests the player with ?. (a destroyed one passes and throws): Unity's own test here,
                // so the main menu after "Quit to menu" does not throw every frame.
                SpectatorCamera spectator = SpectatorCamera.instance;
                if (spectator != null && spectator.camera != null && spectator.camera.isActiveAndEnabled)
                {
                    cam = spectator.camera;
                }
                else if (LocalPlayer.controller != null)
                {
                    cam = LocalPlayer.controller.GetActiveCamera();
                }
            }
            catch (Exception e) when (e is NullReferenceException || e is MissingReferenceException)
            {
                // "Quit to menu": the game still points at the destroyed player and throws reading its camera, every
                // frame. The view mode then never left OnFoot and the hands froze; the cameras below take over.
            }
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

        private Seat _anchorSeat;
        private RigidPose _lastFallPose;
        private bool _hasFallPose;

        private void OnModeChanged(RigMode from, RigMode to, object key, FpsActorController player)
        {
            Seat seatNow = player != null && player.actor != null && !player.actor.dead && player.actor.IsSeated() ? player.actor.seat : null;
            if (from == RigMode.Anchored && to == RigMode.Anchored && seatNow != null && ReferenceEquals(seatNow, _anchorSeat)
                && !(key is ActiveRaggy) && !(_anchorKey is ActiveRaggy))
            {
                // The same seat, only its camera swapped (its gun taken up or put away, the game's third-person toggle): the view
                // turned by the stick stayed only until then, snapped to the seat's front behind a blackout each time (seat view
                // scout, 2026-11-12). The new camera is taken; the turn, the neutral head and the toggles stay.
                Log.Info($"View: the seat's camera changed ({key})");
                _anchorKey = key;
                return;
            }
            _anchorSeat = to == RigMode.Anchored ? seatNow : null;
            // (The "taller than saved" count starts again: it is counted on foot only, so begun before a seat, a ladder or a death
            // and never ended, its three seconds were long over on the first frame back and the saved eye height was raised at
            // once, written to the settings too; scout, 2026-11-11.)
            _tallerSince = -1f;
            // Logged (sparingly): a view that keeps flipping between modes is exactly the kind of glitch to see here.
            if (++_modeChanges <= 30 || _modeChanges % 100 == 0)
            {
                Log.Info($"View: {from} -> {to} ({key}) [{_modeChanges}]");
            }
            Input.VRInput.DiagnoseFor(10f);
            foreach (VRHand h in Hands)
            {
                h.ForgetMotion();
            }
            // A physical crouch's compensation is the on-foot view's: kept through a death, a seat or a ladder, the first frame
            // back on foot standing raised the view by the old crouch and let it fall over a tenth of a second (rig hunt).
            _crouchLowered = false;
            _crouchCompensation = 0f;
            _crouchReleasedAt = -1f;
            bool cut = from != RigMode.None;
            if (from == RigMode.Anchored || to == RigMode.Anchored)
            {
                // Into or out of a seat or onto a ladder: a crouch or prone toggled on foot before it kept the player
                // down (and the view low) after getting out.
                VRGameInput.ResetToggles();
            }
            // (Out of the fall: the way he looked while lying, turns made there included, carried onto his feet; reset to the body's,
            // a turn made lying was undone on getting up; view scout, 2026-11-11.)
            bool fromFall = from == RigMode.Anchored && _anchorKey is ActiveRaggy;
            float fallHeading = _fallYaw + _seatYawOffset - _neutralHead.rotation.eulerAngles.y;
            if (!(key is ActiveRaggy))
            {
                _hasFallPose = false;
            }
            if (to == RigMode.Anchored && key is ActiveRaggy && player != null && player.actor != null)
            {
                _fallYaw = player.actor.transform.eulerAngles.y; // (latched: getting up the game turns the body to the lying spine)
            }
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
                // Face the direction the game spawned / dropped us in (or, up from a fall, the way he looked while lying).
                Yaw = fromFall ? fallHeading : player.transform.eulerAngles.y - Camera.transform.localEulerAngles.y;
                _yawFromUntrackedHead = !HeadTracked;
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
            // The snap latch follows the stick also while turning is blocked (a menu, a panel, a cockpit grip): let go meanwhile,
            // the first push after was lost (locomotion scout, 2026-11-12).
            float heldX = Mathf.Abs(VRInput.TurnStick.x);
            if (heldX < 0.35f)
            {
                _snapLatch = false;
            }
            // Nor while the match is paused behind the SteamVR dashboard or with the headset off (no menu of the game's is open
            // then, so GameMenuOpen stays false): a stick pushed meanwhile turned the view (rig hunt, 2026-10-20 night).
            if (VRConfig.Turn.Value == TurnMode.Off || GameContext.GameMenuOpen || VRGameInput.SticksOnPanel
                || GameManager.instance != null && GameManager.IsPaused())
            {
                return;
            }
            bool allowed = Mode == RigMode.OnFoot || (Mode == RigMode.Anchored && !VehicleInput.BlocksTurning(GameContext.SeatKind));
            // Not while a hand holds a car's wheel or a flight stick: the turn swings the hand round the head (a 30 degree
            // snap about 30 cm), which read as tens of degrees of steering and held the car at full lock (85-agent review).
            allowed &= !(Hands[0] != null && Hands[0].Role == HandRole.Cockpit || Hands[1] != null && Hands[1].Role == HandRole.Cockpit);
            allowed &= !VehicleInterior.AimsGun; // (the right stick turns the turret there, not the view)
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
                    LastSnapFrame = Time.frameCount;
                    LastSnapDegrees = delta;
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
                    delta = scaled * VRConfig.SmoothTurnSpeed.Value * Mathf.Min(Time.unscaledDeltaTime, 0.1f); // a stalled frame is no big turn
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

        /// <summary>Self-test: added to the head's position for room-scale walking (the virtual headset never sways).</summary>
        internal static Vector3 TestHeadOffset;

        private void RoomscaleFollow(FpsActorController player)
        {
            Vector3 hl = HeadLocal + TestHeadOffset;
            // Paused (the in-game menu, the SteamVR dashboard, the headset off: all PauseGame) or a menu open: the head moves
            // on its own there (a lean, a headset carried about), and the character walked with it while the match was frozen
            // (only walls stopped it). The head is still tracked, so the first step after the pause starts from where it is.
            if (!VRConfig.RoomscaleMovement.Value || VRConfig.SeatedMode.Value || GameManager.IsPaused() || GameContext.GameMenuOpen)
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
                if (sq <= 1e-7f && player.controller != null && player.IsMovementEnabled())
                {
                    // Under a third of a millimetre: kept, adding up until it is a step. Dropped every frame, a slow lean
                    // (under 3 cm a second) never moved the body, and the body was left behind the head.
                    return;
                }
                // Ignore tracking jumps (> 35 cm in one frame).
                if (sq > 1e-7f && sq < 0.1225f && player.controller != null && player.IsMovementEnabled())
                {
                    Vector3 before = player.transform.position;
                    // Pressed into the ground while on it: a move along the floor alone broke the ground contact, the
                    // next physics step landed the player again, and every landing plays a footstep: steps all the time
                    // while standing still (the head never stops moving a little).
                    if (player.controller.OnGround())
                    {
                        d.y = -0.01f;
                    }
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
        /// height. A window that cannot gather enough samples starts over instead of averaging in whatever comes later
        /// (a crouch, a seated moment).
        /// </summary>
        private void UpdateHeightCalibration(FpsActorController player)
        {
            if (_calibrationStart < 0f || Time.unscaledTime < _calibrationStart)
            {
                return;
            }
            float elapsed = Time.unscaledTime - _calibrationStart;
            float y = HeadLocal.y;
            // (Not in a pause or a menu: it can run on for a long time now, and a headset carried about or laid on a shelf there is no standing height.)
            if (HeadTracked && y > 0.5f && _calibration.Count < _calibration.Capacity && !GameManager.IsPaused() && !GameContext.GameMenuOpen)
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
                    // Measured again, as a window short of samples is: ended here it waited for a change of view with nothing said, physical
                    // crouch off and the eyes at the game's own height all the while (spawned sitting, or the headset on a desk; rig scout, 2026-11-07).
                    if (!_oddHeightLogged)
                    {
                        _oddHeightLogged = true;
                        Log.Info($"Eye height {h:0.00} m does not look like standing; not saved, measuring on (or use Calibrate now, or Seated play).");
                    }
                    _calibration.Clear();
                    _calibrationStart = Time.unscaledTime;
                    return;
                }
                _oddHeightLogged = false;
                EndCalibration();
            }
            else if (elapsed > 6f)
            {
                // Not enough tracked samples: a fresh window, with nothing of the old one (the headset put on later: given up here it was
                // never tried again until a change of view, and physical crouch stayed off with no word; rig scout, 2026-11-06).
                _calibration.Clear();
                _calibrationStart = Time.unscaledTime;
            }
        }

        private bool _oddHeightLogged;

        /// <summary>How far above the saved eye height a head has to stay before that height is taken to be wrong (m).</summary>
        private const float TallerThanSaved = 0.12f;

        private float _tallerSince = -1f;

        /// <summary>
        /// Nobody stands taller than they stand: a head staying well above the saved height for three seconds means it was taken crouched or
        /// sitting (the first spawn's two seconds, say). Left, the view stood 0.6 m too high and physical crouch never fired (rig scout, 2026-11-06).
        /// </summary>
        private void RaiseSavedHeight()
        {
            float saved = VRConfig.EyeHeight.Value;
            float y = HeadLocal.y;
            // (Not paused or in a menu: the head moves on its own there, a headset carried about or laid on a shelf, as RoomscaleFollow says.)
            if (saved <= 0.3f || VRConfig.SeatedMode.Value || !HeadTracked || AutoTest.Requested || Shots.Enabled || y < saved + TallerThanSaved || y > 2.1f
                || GameManager.IsPaused() || GameContext.GameMenuOpen)
            {
                _tallerSince = -1f;
                return;
            }
            if (_tallerSince < 0f)
            {
                _tallerSince = Time.unscaledTime;
            }
            else if (Time.unscaledTime - _tallerSince >= 3f)
            {
                _tallerSince = -1f;
                VRConfig.EyeHeight.Value = Mathf.Round(y * 100f) / 100f;
                Log.Info($"Eye height raised to {VRConfig.EyeHeight.Value:0.00} m: the head stayed {y - saved:0.00} m above the saved {saved:0.00} m.");
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
            else
            {
                // Said, not silent (sweep: a press with the headset off the head or out of tracking did nothing, and the panel
                // still read "auto" with no reason).
                string why = $"Eye height not saved: your head reads {y:0.00} m above the floor. Stand or sit as you play, then press again.";
                Log.Info(why);
                Log.Guard("Eye height notice", () => VRManager.Instance?.UI?.Notice?.Show(why, 6f));
            }
        }

        /// <summary>Re-centres seated/follow views on the current head pose.</summary>
        public void Recenter()
        {
            _neutralHead = new RigidPose(HeadLocal, Quaternion.Euler(0f, Camera.transform.localEulerAngles.y, 0f));
            _seatYawOffset = 0f;
            // On foot the heading stays: the body's yaw it was set from is turned only at spawn and on leaving a seat or a ladder,
            // so after turning by stick the view swung back by the whole turn (review, 2026-10-20). OnModeChanged sets it then.
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

        private Quaternion _lastSeatTurn;
        private bool _hasSeatTurn;
        private float _seatTurnAt, _seatTurnRate;
        private Seat _seatTurnSeat;

        /// <summary>
        /// How hard the seat turns, 0..1, for the tunnel (<see cref="VRConfig.VehicleVignette"/>): from 40 degrees a second, full at 160.
        /// Read from the seat's own turn frame to frame, so a vehicle another PC drives (moved by the word, its body still) counts too.
        /// The research of 2026-11-12 (headset makers' comfort advice, racing games' fixed horizon and crash tunnel): the turn the body
        /// does not feel is what makes riders sick; a steady drive keeps the whole view.
        /// </summary>
        private float SeatTurnMotion(FpsActorController player)
        {
            Seat seat = Mode == RigMode.Anchored && player != null && player.actor != null ? player.actor.seat : null;
            if (seat == null || !VRConfig.VehicleVignette.Value || GameContext.GameMenuOpen || GameManager.IsPaused() || Time.deltaTime <= 0f)
            {
                _hasSeatTurn = false;
                return 0f;
            }
            // Over the time since the seat last moved, not since the last frame: a vehicle turns in physics steps (50 a second against
            // 90 frames), so frame by frame the rate read nothing, then nearly twice the turn, and the tunnel pulsed (seat view scout;
            // VehicleFeel's jolt had the same fix). Still for over a tenth of a second: not turning.
            // (The vehicle's own turn, not the seat's: a turret seat turns with its gun, and the tunnel came on as the gunner aimed.)
            Quaternion now = seat.vehicle != null ? seat.vehicle.transform.rotation : seat.transform.rotation;
            float t = Time.time;
            if (!_hasSeatTurn || seat != _seatTurnSeat)
            {
                _seatTurnSeat = seat;
                _lastSeatTurn = now;
                _seatTurnAt = t;
                _seatTurnRate = 0f;
                _hasSeatTurn = true;
                return 0f;
            }
            float angle = Quaternion.Angle(_lastSeatTurn, now);
            if (angle > 0.01f)
            {
                float dt = t - _seatTurnAt;
                _seatTurnRate = dt > 1e-4f ? Mathf.Min(angle / dt, 2000f) : _seatTurnRate;
                _lastSeatTurn = now;
                _seatTurnAt = t;
            }
            else if (t - _seatTurnAt > 0.1f)
            {
                _seatTurnRate = 0f;
            }
            return Mathf.Clamp01((_seatTurnRate - 40f) / 120f);
        }

        private float _offHandYaw;
        private bool _offHandYawSet;

        private void AlignMovementReference(FpsActorController player)
        {
            float yaw;
            if (VRConfig.MoveDirection.Value == MoveReference.OffHand && SetOff.Tracked)
            {
                Vector3 f = SetOff.TrackedPointer.Forward; // the hand with the move stick, wherever the weapon is (its controller: input, never slowed)
                // Steeper than 60 degrees up or down the hand's heading is held as it was: past straight up its flat part turns round,
                // and forward on the stick walked backwards (it swung wildly just before, too; locomotion scout, 2026-11-12).
                bool steep = Mathf.Abs(f.y) > 0.866f;
                f.y = 0f;
                if (!steep && f.sqrMagnitude > 1e-4f)
                {
                    _offHandYaw = Quaternion.LookRotation(f).eulerAngles.y;
                    _offHandYawSet = true;
                }
                yaw = _offHandYawSet ? _offHandYaw : Head.eulerAngles.y;
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
            if (_yawFromUntrackedHead && HeadTracked)
            {
                // The first pose of a view entered on foot before it (the headset asleep at the spawn, VR started in a match): the
                // view faced the body plus wherever the headset then faced, until the next seat, ladder or respawn (round-4 scout).
                // Turns made with the stick before it are dropped with it.
                _yawFromUntrackedHead = false;
                if (Mode == RigMode.OnFoot && player != null)
                {
                    Yaw = player.transform.eulerAngles.y - Camera.transform.localEulerAngles.y;
                }
            }
            Camera reference = null;
            try
            {
                reference = PlaceOrigin(player);
                _steadyRoot = Root.localToWorldMatrix;
                _steadyRootRotation = Root.rotation;
                _steadyFrame = Time.frameCount;
                ApplyViewShake();
                _shakeUndo = _steadyRoot * Root.worldToLocalMatrix;
                _shakeUndoRotation = _steadyRootRotation * Runtime.RigidPose.Conjugate(Root.rotation);
            }
            catch (Exception e)
            {
                // Whatever goes wrong placing the play space, the hands keep following the controllers: one exception
                // here every frame froze both hands in the main menu after "Quit to menu".
                Log.ExceptionOnce("Rig.PlaceOrigin", e);
            }

            foreach (VRHand h in Hands)
            {
                h.UpdatePose(Root);
            }
            _cockpit.BeforeRender();
            _turretGrip?.BeforeRender();

            UpdateHudPose(Mathf.Min(Time.unscaledDeltaTime, 0.1f));
            if (player != null && (Mode == RigMode.OnFoot || Mode == RigMode.Anchored) && player.fpCamera != null)
            {
                // Game logic reads the head through the game camera; it also projects the HUD markers, so it takes
                // the HUD frame's (lazily following) orientation and the markers stay on their targets. The use ray and the
                // ladder check take the head's own orientation instead (UseRayHeadAimPatch, LadderHeadAimPatch).
                player.fpCamera.transform.SetPositionAndRotation(HudPose.position, HudPose.rotation);
            }
            try
            {
                UpdateBodyShadows(player);
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("Rig.BodyShadows", e); // the body's drawing must never stop the hands placed below
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

        /// <summary>
        /// On foot: the player's physics body (the character controller's transform, moved only in the physics step), where it was as
        /// the rig was last placed, and the game's smoothing of the view then (SmoothPlayerMovement: the view drawn between two steps).
        /// The rig is placed once a frame from the smoothed view: a thing that must move with the player in each physics step (a held
        /// body's grab anchor, BodyGrab) takes the hand's place against the body from these, and the body's place in the step.
        /// </summary>
        internal Transform PlacedBody { get; private set; }
        internal Vector3 PlacedBodyAt { get; private set; }
        internal Vector3 PlacedSmoothing { get; private set; }

        private Camera PlaceOrigin(FpsActorController player)
        {
            Vector3 hl = HeadLocal;
            switch (Mode)
            {
                case RigMode.OnFoot:
                {
                    if (player == null)
                    {
                        return null; // destroyed with its map; the mode changes next Update
                    }
                    Transform anchorT = player.fpParent != null ? player.fpParent.fpCameraParent : player.fpCameraRoot;
                    Vector3 anchor = anchorT.position;
                    PlacedBody = player.transform;
                    PlacedBodyAt = player.transform.position;
                    PlacedSmoothing = player.smoothPlayerMovement != null ? player.smoothPlayerMovement.GetInterpolatedSmoothWorldMovementDelta() : Vector3.zero;
                    Quaternion r = Quaternion.Euler(0f, Yaw, 0f);
                    Vector3 origin = anchor - r * new Vector3(hl.x, 0f, hl.z);
                    float compensation = 0f;
                    // Also while the game eases its camera back up after the crouch lets go (a fifth of a second): the
                    // compensation stopping at once dropped the view by the whole crouch depth on standing up.
                    if (_physicalCrouch)
                    {
                        _crouchLowered = true;
                        _crouchReleasedAt = -1f;
                    }
                    else if (_crouchLowered)
                    {
                        if (_crouchReleasedAt < 0f)
                        {
                            _crouchReleasedAt = Time.time;
                        }
                        // Until the game stands again: under a low overhang it refuses to, and at the old 0.6 s the view dropped by the
                        // whole crouch while the player stood (review, 2026-10-20). 3 s still ends a crouch the game keeps for itself.
                        // Game time, as the game's own stand-up: a pause of over 3 s just after standing ended it while the game was
                        // still crouched, and the view sank by up to 0.7 m until the match went on (rig hunt, 2026-10-21).
                        if (player.cameraHeight >= GameStandEyeHeight - 0.01f || Time.time - _crouchReleasedAt > 3f)
                        {
                            _crouchLowered = false;
                        }
                    }
                    float gameDrop = Mathf.Max(0f, GameStandEyeHeight - player.cameraHeight);
                    // With a crouch or prone toggle on as well: the deeper of the two drops, never both. Uncompensated the view sank
                    // a crouch's depth below the real head, and with prone below the ground (rig hunt, 2026-10-20 night). The
                    // toggle alone (standing) still lowers the view by the game's crouch: the real drop is then nothing.
                    float wantCompensation = !_crouchLowered ? 0f
                        : !VRGameInput.CrouchToggled && !VRGameInput.ProneToggled ? gameDrop
                        : Mathf.Min(gameDrop, Mathf.Max(0f, EyeHeight - hl.y));
                    // Eased at the game's own crouch speed (4 m/s): a crouch or prone toggle while physically crouched switched it off at
                    // once and the view jumped by up to a metre (review, 2026-10-20).
                    _crouchCompensation = Mathf.MoveTowards(_crouchCompensation, wantCompensation, 4f * Mathf.Min(Time.unscaledDeltaTime, 0.1f));
                    compensation = _crouchCompensation;
                    origin.y = anchor.y - EyeHeight + compensation;
                    Root.SetPositionAndRotation(origin, r);
                    return player.fpCamera;
                }
                case RigMode.Anchored:
                {
                    if (player == null && !(_anchorKey is Camera keyCam && keyCam != null))
                    {
                        return null; // the player went since Update: the view stays where it was, not at the world's origin for a frame
                    }
                    RigidPose anchor = AnchorPose(player, out Camera cam);
                    RigidPose placed = anchor.Multiply(_neutralHead.Inverse());
                    if (Mathf.Abs(_seatYawOffset) > 1e-4f)
                    {
                        // Turned about the head where it is, not about the neutral head point: leaning 15 cm and turning 90 degrees
                        // carried the head 21 cm across the cabin (review, 2026-10-20).
                        Vector3 head = placed.TransformPoint(HeadLocal);
                        Quaternion q = Quaternion.AngleAxis(_seatYawOffset, anchor.rotation * Vector3.up);
                        placed = new RigidPose(head + q * (placed.position - head), q * placed.rotation);
                    }
                    placed.ApplyTo(Root);
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

        private Camera _springOf;
        private FirstPersonCameraLook _spring;

        /// <summary>
        /// Where a seat's camera sits, without the game's head bob: a cabin camera with <c>FirstPersonCameraLook</c> rides a spring the vehicle's
        /// acceleration pushes, up to 12 cm, and the eyes took it, so the whole view slid about the cabin as the vehicle sped up, braked or hit
        /// something, the head not moving (seats scout, 2026-11-08). Its rest, as the game keeps it, from the camera's parent.
        /// </summary>
        private Vector3 SeatCameraRest(Camera cam)
        {
            if (!ReferenceEquals(_springOf, cam))
            {
                _springOf = cam;
                _spring = cam.GetComponent<FirstPersonCameraLook>();
            }
            Transform t = cam.transform;
            return _spring != null && t.parent != null ? t.parent.TransformPoint(_spring.originPosition) : t.position;
        }

        private Seat _restSeat;
        private Camera _restCam;
        private Vector3 _restLocal;
        private bool _restOutside, _eyesAtSeat;
        private float _restTakenAt;

        /// <summary>
        /// Where the eyes sit in a seat whose vehicle has a camera of its own: where that camera rests in the vehicle, taken once as the
        /// seat is taken and carried with the seat from then on. Taken from the camera every frame, the view swung around the turret
        /// with a minigun's camera on its turning gun (the user, 2026-11-11, the Blackhawk: "the minigun is trippy"). A camera of the
        /// vehicle's more than 1.5 m from the seat's own eye point is an outside view (an aircraft's chase camera): the eyes are the
        /// seat's then, in the cockpit (the user: planes felt "like 2d when im actually 3d").
        /// </summary>
        private Vector3 SeatEyes(Seat seat, Camera cam, Vector3 rest)
        {
            Transform t = seat.transform;
            bool fresh = !ReferenceEquals(_restSeat, seat) || !ReferenceEquals(_restCam, cam);
            if (fresh)
            {
                _restTakenAt = Time.unscaledTime;
            }
            else if (Time.unscaledTime - _restTakenAt < 0.5f)
            {
                // (Taken again for the first half second, the outside verdict too: on the first frame a turret may still turn to its
                // rest or a camera settle, and that one frame's place was kept for the whole ride; scouts.)
                var eye = new Vector3(0f, 0.85f, 0.2f);
                _restOutside = (rest - t.TransformPoint(eye)).magnitude > 1.5f;
                _restLocal = _restOutside ? eye : t.InverseTransformPoint(rest);
            }
            if (fresh)
            {
                _restSeat = seat;
                _restCam = cam;
                var seatEye = new Vector3(0f, 0.85f, 0.2f); // (the game's own eye point in a seat: FpsActorController.StartSeated)
                _restOutside = (rest - t.TransformPoint(seatEye)).magnitude > 1.5f;
                _restLocal = _restOutside ? seatEye : t.InverseTransformPoint(rest);
                Log.Info($"View in {(seat.vehicle != null ? seat.vehicle.name : "a seat")}: {(_restOutside ? $"its camera '{cam.name}' is outside ({(rest - t.TransformPoint(seatEye)).magnitude:0.0} m from the seat): the seat's own eyes" : $"its camera '{cam.name}' at its place in the vehicle")}.");
            }
            _eyesAtSeat = _restOutside;
            return t.TransformPoint(_restLocal);
        }

        private RigidPose AnchorPose(FpsActorController player, out Camera cam)
        {
            _eyesAtSeat = false; // (set again below for an outside camera; stale from a seat left, it hid the body on a ladder)
            cam = player != null ? player.fpCamera : null;
            if (_anchorKey is Camera overrideCam && overrideCam != null)
            {
                cam = overrideCam;
                // Take the camera's position but the vehicle's orientation: turret cameras rotate with the turret,
                // and following that rotation would make head-aimed turrets chase their own view.
                Seat seatOfCam = player != null && player.actor.IsSeated() ? player.actor.seat : null;
                Quaternion rot = seatOfCam != null ? seatOfCam.transform.rotation : overrideCam.transform.rotation;
                Vector3 rest = SeatCameraRest(overrideCam);
                _eyesAtSeat = false;
                if (seatOfCam != null)
                {
                    rest = SeatEyes(seatOfCam, overrideCam, rest);
                }
                return Level(new RigidPose(rest, rot));
            }
            if (player == null)
            {
                return RigidPose.Identity;
            }
            if (_anchorKey is ActiveRaggy)
            {
                if (player.actor == null)
                {
                    return RigidPose.Identity; // (gone between the frame's choice and its placing)
                }
                Transform head = FallenHead(player.actor);
                if (head != null)
                {
                    ShrinkHead(head);
                    // (A little above the head bone, at the eyes: inside the skull the face's own mesh filled the view.)
                    _lastFallPose = new RigidPose(head.position + Vector3.up * 0.08f, Quaternion.Euler(0f, _fallYaw, 0f));
                    _hasFallPose = true;
                    return _lastFallPose;
                }
                if (_hasFallPose)
                {
                    return _lastFallPose; // (a frame with no head found: where it last was)
                }
            }
            Transform root = player.fpCameraRoot;
            if (_anchorKey is Seat seat && seat != null)
            {
                return Level(new RigidPose(root.position, seat.transform.rotation));
            }
            // Ladder: camera root hangs off the actor; keep the actor's facing.
            return new RigidPose(root.position, Quaternion.Euler(0f, player.actor.transform.eulerAngles.y, 0f));
        }

        private Transform _shrunkHead;
        /// <summary>The body's yaw as the fall began (the view's, while lying: the game turns the body to the spine on getting up).</summary>
        private float _fallYaw;
        private Vector3 _shrunkHeadScale;

        /// <summary>
        /// The fallen player's own head (and what hangs on it: a helmet, goggles) drawn as a point while the view is in it: the eyes sat
        /// inside the skull and its faces and the helmet's filled the view. Given back as he gets up (<see cref="UnshrinkHead"/>).
        /// </summary>
        private void ShrinkHead(Transform head)
        {
            if (_shrunkHead == head)
            {
                return;
            }
            UnshrinkHead();
            _shrunkHead = head;
            _shrunkHeadScale = head.localScale;
            head.localScale = _shrunkHeadScale * 0.001f;
        }

        internal void UnshrinkHead()
        {
            if (_shrunkHead != null)
            {
                _shrunkHead.localScale = _shrunkHeadScale;
            }
            _shrunkHead = null;
        }

        /// <summary>A fallen soldier's head as it lies (the ragdoll's, while it is one), or null.</summary>
        private static Transform FallenHead(Actor actor)
        {
            try
            {
                // (The DRAWN head: getting up, the game's animated head is "active" at once while the drawn body still eases out of
                // the ragdoll's pose for half a second, and the view jumped ahead of it; scout, 2026-11-11.)
                return actor.ragdoll == null ? null
                    : actor.ragdoll.IsRenderingRagdollTransforms() ? actor.ragdoll.HumanBoneTransformRagdoll(HumanBodyBones.Head)
                    : actor.ragdoll.HumanBoneTransformActive(HumanBodyBones.Head);
            }
            catch (Exception)
            {
                return null;
            }
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
            int mask = ((Suppressor != null ? Suppressor.RealMask(reference) : reference.cullingMask) | (1 << FirstPersonLayer)) & ~(1 << UiLayer);
            CameraClearFlags clear = reference.clearFlags;
            Color background = reference.backgroundColor;
            float far = Mathf.Max(reference.farClipPlane, VRConfig.MinFarClip.Value);

            if (clear == CameraClearFlags.Depth || clear == CameraClearFlags.Nothing)
            {
                // The game draws sky / distant scenery with a separate camera behind this one; do it in one pass.
                Camera backgroundCam = BackgroundCamera.instance != null ? BackgroundCamera.instance.camera : null;
                if (backgroundCam != null)
                {
                    mask |= ((Suppressor != null ? Suppressor.RealMask(backgroundCam) : backgroundCam.cullingMask) | (1 << BackgroundLayer)) & ~(1 << UiLayer);
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
            // (HDR as the game's camera whenever post-processing draws: its grading and the night vision read past 1.)
            bool postProcessing = VRConfig.PostProcessing.Value || NightVision.isEnabled;
            Camera.allowHDR = !postProcessing && GraphicsPresets.AllowHdr.HasValue ? GraphicsPresets.AllowHdr.Value : reference.allowHDR;
            Camera.allowMSAA = GraphicsPresets.ForceMsaa ?? reference.allowMSAA;
            Camera.renderingPath = reference.renderingPath;
            // Only what the game camera itself asks for: forcing a depth texture makes the forward renderer draw every
            // opaque object once more per eye, which in multi-pass stereo cost more frame time than anything else here.
            Camera.depthTextureMode = reference.depthTextureMode;
            Camera.useOcclusionCulling = reference.useOcclusionCulling;
            Camera.layerCullSpherical = reference.layerCullSpherical;
            // layerCullDistances returns a fresh array on every read: copy it when the source camera changes and every five
            // seconds after that (the game sets them when its options apply or a level starts; twice a second was 320 bytes of
            // garbage a second; allocation hunt, 2026-10-24).
            if (reference != _cullSource || Time.frameCount >= _nextCullCopy)
            {
                _cullSource = reference;
                _nextCullCopy = Time.frameCount + 450;
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

        private Camera _ppSourceOf;
        private PostProcessLayer _ppSource;
        private bool _gogglesLogged;

        /// <summary>Self-test: the eyes' post-processing is running and can see this volume.</summary>
        internal bool EyesSee(PostProcessVolume volume) =>
            _postProcess != null && _postProcess.enabled && volume != null && (_postProcess.volumeLayer.value & 1 << volume.gameObject.layer) != 0;

        /// <summary>
        /// The game's picture in the eyes. It has two post-process layers, and only one holds its look: each world camera gets a layer that sees
        /// the ambient occlusion volume alone (PostProcessingManager.RegisterWorldCamera, layer 29), and a separate last camera,
        /// <see cref="PostProcessingManager.finalLayer"/>, sees layer 28: the grade, the bloom and the night vision volume the goggles switch on
        /// (read in the game's files, 2026-11-07). The eyes took the world camera's mask, so they never saw the goggles, nor the grade with
        /// PostProcessing on (user, 2026-11-07: "doesnt work at all it just does nothing and isnt even in my vr view"). On while the goggles are;
        /// the world camera's own layer joins only with PostProcessing on (ambient occlusion cannot be seen through the goggles and costs a depth pass).
        /// </summary>
        private void UpdatePostProcessing(Camera reference)
        {
            bool goggles = NightVision.isEnabled;
            bool want = VRConfig.PostProcessing.Value || goggles;
            // (The layer is looked up only when wanted, and once per reference camera: a GetComponent every frame.)
            if (want && !ReferenceEquals(_ppSourceOf, reference))
            {
                _ppSourceOf = reference;
                _ppSource = reference.GetComponent<PostProcessLayer>();
            }
            PostProcessingManager manager = want ? PostProcessingManager.instance : null;
            PostProcessLayer final = manager != null ? manager.finalLayer : null;
            if (!want || final == null || PpResources == null)
            {
                if (_postProcess != null && _postProcess.enabled)
                {
                    _postProcess.enabled = false;
                }
                if (want && goggles && !_gogglesLogged)
                {
                    _gogglesLogged = true;
                    Log.Warn("Night vision is on, and the game's post-processing could not be found: the eyes show no green.");
                }
                _gogglesLogged &= goggles;
                return;
            }
            if (_postProcess == null)
            {
                object resources = manager.ppResources != null ? manager.ppResources : PpResources.GetValue(final);
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
            bool all = VRConfig.PostProcessing.Value;
            LayerMask volumes = final.volumeLayer;
            if (all && _ppSource != null)
            {
                volumes |= _ppSource.volumeLayer;
            }
            _postProcess.volumeLayer = volumes;
            _postProcess.volumeTrigger = Camera.transform;
            // Temporal AA smears in VR; fall back to FXAA when the game uses it. (Through the goggles none: the eyes have MSAA, and the grain hides it.)
            _postProcess.antialiasingMode = !all ? PostProcessLayer.Antialiasing.None
                : final.antialiasingMode == PostProcessLayer.Antialiasing.TemporalAntialiasing ? PostProcessLayer.Antialiasing.FastApproximateAntialiasing
                : final.antialiasingMode;
            _postProcess.stopNaNPropagation = true;
            if (!_postProcess.enabled)
            {
                _postProcess.enabled = true;
            }
            if (goggles != _gogglesLogged)
            {
                // (For the next play log: the one line that says whether the eyes took the goggles' volume.)
                _gogglesLogged = goggles;
                if (goggles)
                {
                    Log.Info($"Night vision on: the eyes' post-processing sees layers {volumes.value:X8}, HDR {Camera.allowHDR}.");
                }
            }
        }

        /// <summary>The player whose body was switched to shadow only for the view (see <see cref="UpdateBodyShadows"/>).</summary>
        private FpsActorController _bodyShadowsOwner;

        /// <summary>The body's renderers switched off with their shadows (BodyShadow off), to switch on again.</summary>
        private readonly List<Renderer> _hiddenBody = new List<Renderer>();

        /// <summary>
        /// On foot the head sits at the body's eye point, so the body must not be drawn into the view. The game draws it
        /// (shadows on) for a third-person camera and for an override camera that renders the player (bodycam, cinematic
        /// shake), and the head camera takes the game camera's layers, the body's among them. With BodyShadow on, the body keeps
        /// its shadow, as it does in first person; off (the default), it is hidden outright, on foot and on a first-person seat or
        /// ladder. Once the view is no longer at the body's eyes, the game's own rule is put back: the override's rendering while
        /// one is on, else the first- or third-person state.
        /// </summary>
        private void UpdateBodyShadows(FpsActorController player)
        {
            if (Mode == RigMode.Anchored && _anchorKey is ActiveRaggy && player != null && player.actor != null)
            {
                // Lying, the view is at his own head: the ragdoll's own mesh and what hangs on it are drawn there (the game's
                // third-person copies are off while it is a ragdoll), and filled the view (view scout, 2026-11-11).
                Hide(player.actor.skinnedRendererRagdoll);
                if (player.actor.accessoryRagdollRenderer != null)
                {
                    foreach (Renderer accessory in player.actor.accessoryRagdollRenderer)
                    {
                        Hide(accessory);
                    }
                }
                _bodyShadowsOwner = player;
                return;
            }
            bool drawn = player != null && (player.IsUsingOverrideCamera() ? player.cameraRenderOptions.renderPlayerActor : !player.firstPerson);
            bool shadow = VRConfig.BodyShadow.Value;
            // On a seat or a ladder the head is at the body's eyes too while the game does not draw the body (its first-person
            // seat camera; its arms on the wheel in their own animation): hidden there as well. A third-person seat camera draws it.
            bool atEyes = Mode == RigMode.OnFoot || Mode == RigMode.Anchored && (!drawn || _eyesAtSeat); // (an outside camera's body is not drawn at the seat's eyes)
            if (atEyes && player != null && ((Mode == RigMode.OnFoot || _eyesAtSeat) && drawn || !shadow)) // (also at an outside camera's seat eyes with BodyShadow on: the body drawn from inside it; scout)
            {
                if (shadow)
                {
                    // Every frame, not once: the game switches the body back to drawn whenever it sets a camera.
                    player.EnableFirstPersonRenderingMode();
                    Log.InfoOnce("Rig.BodyShadows", "On foot the body is kept out of the view (the game draws it for a third-person or override camera): shadow only.");
                }
                else
                {
                    // Not even its shadow (user, 2026-10-24: "alot of shjadoing and things flying around infront of my view" on every
                    // preset with shadows, none on the game's Fastest): the body's head sits at the eyes and its arms and its guns'
                    // third-person copies follow the game's animation, so their shadow lay across the hands and the gun and moved on
                    // its own. ShadowsOnly to Off would draw the meshes: hidden outright, every frame for weapons picked up since.
                    HideBody(player);
                    Log.InfoOnce("Rig.BodyHidden", "From the body's eyes the body and its shadow are hidden (BodyShadow off): its shadow fell across the view.");
                }
                _bodyShadowsOwner = player;
                return;
            }
            RestoreBody();
        }

        /// <summary>The body drawn again and the game's own rule put back (off the body's eyes, BodyShadow on, the rig gone).</summary>
        private void RestoreBody()
        {
            ShowBody();
            FpsActorController owner = _bodyShadowsOwner;
            _bodyShadowsOwner = null;
            if (owner == null)
            {
                return; // not set, or the player was destroyed with its map (its renderers go with it)
            }
            try
            {
                if (owner.IsUsingOverrideCamera() ? owner.cameraRenderOptions.renderPlayerActor : !owner.firstPerson)
                {
                    owner.EnableThirdPersonRenderingMode();
                }
                else
                {
                    owner.EnableFirstPersonRenderingMode();
                }
            }
            catch (System.NullReferenceException)
            {
                // The game's own call walks the body's renderers, and one destroyed under it (a vehicle's part the body held, gone with
                // the vehicle) threw each time: the rest is the game's own to set on its next change.
                Log.InfoOnce("Rig.RestoreBody.gone", "Body drawn again: one of its renderers was already destroyed (the game's call skipped).");
            }
        }

        /// <summary>
        /// Hides the player's body, its accessories and its weapons' third-person copies, shadows and all (only what is still
        /// drawn: one read a renderer each frame).
        /// </summary>
        private void HideBody(FpsActorController player)
        {
            Hide(player.thirdpersonRenderers);
            Actor a = player.actor;
            if (a == null)
            {
                return;
            }
            if (a.accessoryRenderer != null)
            {
                foreach (Renderer r in a.accessoryRenderer)
                {
                    Hide(r);
                }
            }
            if (a.weaponImposterRenderers != null)
            {
                foreach (Renderer[] rs in a.weaponImposterRenderers.Values)
                {
                    Hide(rs);
                }
            }
        }

        private void Hide(Renderer[] rs)
        {
            if (rs == null)
            {
                return;
            }
            foreach (Renderer r in rs)
            {
                Hide(r);
            }
        }

        private void Hide(Renderer r)
        {
            if (r != null && !r.forceRenderingOff)
            {
                r.forceRenderingOff = true;
                if (!_hiddenBody.Contains(r))
                {
                    _hiddenBody.Add(r);
                }
            }
        }

        /// <summary>Puts back what <see cref="HideBody"/> hid (off foot, BodyShadow on, the rig gone); the game's own shadow rule follows.</summary>
        private void ShowBody()
        {
            if (_hiddenBody.Count == 0)
            {
                return;
            }
            foreach (Renderer r in _hiddenBody)
            {
                if (r != null)
                {
                    r.forceRenderingOff = false;
                }
            }
            _hiddenBody.Clear();
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
            UnshrinkHead(); // (the rig going while he lay: his head drawn again)
            // Null-safe: also the teardown of a rig whose construction threw part-way. Each step on its own, as VRManager's: one
            // that threw left the rest, the rig's root with its eye cameras too, alive in flat play.
            Log.Guard("Rig.Suppressor.Dispose", () => Suppressor?.Dispose());
            Log.Guard("Rig.Vignette.Destroy", () => _vignette?.Destroy());
            Log.Guard("Rig.Cockpit.Destroy", () => _cockpit?.Destroy());
            Log.Guard("Rig.Interior.Destroy", () => _interior?.Destroy());
            Log.Guard("Rig.TurretGrip.Destroy", () => _turretGrip?.Destroy());
            Log.Guard("Rig.HullInside.Destroy", () => _hullInside?.Destroy());
            Log.Guard("Rig.TouchEnter.Destroy", () => _touchEnter?.Destroy());
            Log.Guard("Rig.Damage.Destroy", () => _damage?.Destroy());
            Log.Guard("Rig.Mud.Destroy", () => _mud?.Destroy());
            Log.Guard("Rig.Underwater.Destroy", () => _underwater?.Destroy());
            Log.Guard("Rig.Lens.Destroy", () => _lens?.Destroy());
            Log.Guard("Rig.ShowBody", RestoreBody); // VR switched off on foot: the flat game's body and its rule again
            Log.Guard("Rig.Sun", RestoreSun);
            foreach (VRHand h in Hands)
            {
                if (h != null)
                {
                    Log.Guard("Rig.Hand.Destroy", h.Destroy);
                }
            }
            if (Root != null)
            {
                UnityEngine.Object.Destroy(Root.gameObject);
            }
            Log.Guard("Rig.Compositor.Fade", () => OpenVR.Compositor?.FadeToColor(0f, 0f, 0f, 0f, 0f, false));
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

    /// <summary>
    /// The goggles' tube vignette is a flat screen picture: in VR it became a 72-degree frame floating in front of the eyes.
    /// Hidden there; the green grade (<see cref="VRRig"/>'s post-processing) covers the whole view.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(NightVision), nameof(NightVision.ToggleEnable))]
    internal static class NightVisionVignettePatch
    {
        private static void Postfix(NightVision __instance)
        {
            if (VRManager.Active && __instance != null && __instance.vignette != null)
            {
                __instance.vignette.enabled = false;
            }
        }
    }

    /// <summary>
    /// The goggles' fade as they come down is a flat screen picture as well (a black image the game shows for half a second on each switch, in
    /// its <c>LateUpdate</c>): in VR that was a black rectangle in front of the view, the world around it. Hidden there.
    /// </summary>
    [HarmonyLib.HarmonyPatch(typeof(NightVision), "LateUpdate")]
    internal static class NightVisionFadePatch
    {
        private static void Postfix(NightVision __instance)
        {
            if (VRManager.Active && __instance != null && __instance.blackOverlay != null && __instance.blackOverlay.enabled)
            {
                __instance.blackOverlay.enabled = false;
            }
        }
    }

    /// <summary>
    /// The game's use ray and ladder check read the view from the game camera. On foot that camera carries the HUD frame,
    /// which trails the head by up to 35 degrees while turning, so the rays went where the head had been. Those checks run
    /// with the head's own orientation (the position already matches) and the frame is put back after them: the HUD
    /// markers keep the lazy frame.
    /// </summary>
    internal static class HeadAimedChecks
    {
        private static bool OnFootHere(FpsActorController player)
        {
            VRRig rig = VRRig.Instance;
            return VRManager.Active && rig != null && rig.Mode == RigMode.OnFoot && player != null && player.fpCamera != null;
        }

        /// <summary>Turns the game camera to the head for a check; returns the orientation it had (the HUD frame's).</summary>
        internal static Quaternion Begin(FpsActorController player)
        {
            Quaternion frame = Quaternion.identity;
            if (OnFootHere(player))
            {
                Transform cam = player.fpCamera.transform;
                frame = cam.rotation;
                cam.rotation = VRRig.Instance.Head.rotation;
            }
            return frame;
        }

        /// <summary>Puts back the orientation <see cref="Begin"/> took.</summary>
        internal static void End(FpsActorController player, Quaternion frame)
        {
            if (OnFootHere(player))
            {
                player.fpCamera.transform.rotation = frame;
            }
        }
    }

    [HarmonyPatch(typeof(FpsActorController), "SampleUseRay")]
    internal static class UseRayHeadAimPatch
    {
        private static void Prefix(FpsActorController __instance, out Quaternion __state)
        {
            __state = HeadAimedChecks.Begin(__instance);
        }

        private static void Postfix(FpsActorController __instance, Quaternion __state)
        {
            HeadAimedChecks.End(__instance, __state);
        }
    }

    [HarmonyPatch(typeof(FpsActorController), "TryGetOnLadder")]
    internal static class LadderHeadAimPatch
    {
        private static void Prefix(FpsActorController __instance, out Quaternion __state)
        {
            __state = HeadAimedChecks.Begin(__instance);
        }

        private static void Postfix(FpsActorController __instance, Quaternion __state)
        {
            HeadAimedChecks.End(__instance, __state);
        }
    }
}
