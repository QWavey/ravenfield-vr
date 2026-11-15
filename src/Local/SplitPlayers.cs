using System;
using System.Collections.Generic;
using HarmonyLib;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>One player at this PC beside the game's own (<see cref="SplitPlayers"/>).</summary>
    internal sealed class SplitPlayer
    {
        public int Index; // 1..3 (0 is the game's own player)
        public Actor Actor;
        public float Yaw, Pitch;
        public Camera Cam;
        public bool Fire, Aim, Crouch, Sprint, Jump, Reload, NextWeapon, Seat;
        public Vector2 Move;
        public ushort LastButtons;
        /// <summary>Input fed by a check instead of a controller (null: the controller).</summary>
        public PadState? Simulated;
        /// <summary>Its first-person view: the weapon in its hands and its own body not drawn for it.</summary>
        internal SplitView View;
        /// <summary>Its keyboard and mouse pair (1: the second keyboard and mouse) when it has no controller, else -1.</summary>
        public int KeysPair = -1;
        public Rect Viewport;
        public int Kills, Deaths;
        public bool WasDead;
        /// <summary>Its life beyond moving and shooting (SplitLife): when it went down, where it comes back, its markers and flashes.</summary>
        public float DiedAt, HitMarkUntil, HurtUntil;
        public bool SpawnAtFlag, KillMark, FirePressed, LastFire;
        public Vector3 HurtFrom;
        /// <summary>A weapon slot picked by its number key (-1: none this frame).</summary>
        public int SlotPick = -1;
        public int LastWheel;
    }

    /// <summary>
    /// The extra players of a splitscreen match (2 to 4 at this PC). Each is a soldier of the player's team made by the game
    /// (ActorManager.CreateAIActor) and driven by its own controller through the AI's answers (where it walks, where it looks, firing,
    /// crouching, jumping, aiming, sprinting): the multiplayer's stand-ins are moved the same way. Its bullets, its damage, its death and
    /// its respawn are the game's own, as for any soldier. Each has a camera at its head, in its own part of the window, with a small
    /// display of its health, its weapon and its rounds. Flat: the game's own player takes the first part. In VR the headset is the game's
    /// own player and the others share the monitor.
    /// </summary>
    internal static class SplitPlayers
    {
        private static readonly List<SplitPlayer> Players = new List<SplitPlayer>();
        private static readonly Dictionary<Actor, SplitPlayer> ByActor = new Dictionary<Actor, SplitPlayer>();
        private static Harmony _harmony;
        private static GameObject _host;
        private static readonly Dictionary<Camera, Rect> MainRects = new Dictionary<Camera, Rect>();

        /// <summary>The players beside the game's own, while a splitscreen match runs (self-test).</summary>
        internal static IReadOnlyList<SplitPlayer> All => Players;

        public static bool Running => Players.Count > 0;

        internal static bool InMatch => ActorManager.instance != null && LocalPlayer.actor != null && GameManager.instance != null && !GameManager.instance.isInNoGameplayScene;

        /// <summary>Once a frame (Multiplayer.Update): the players made or taken away, their controllers read.</summary>
        public static void Update()
        {
            // (Not on a PC that joined an online game: the host owns every soldier there and this PC none, so its own would clash. A
            // host's splitscreen players are soldiers of his, seen as any on the others' PCs.)
            bool want = Splitscreen.On && !Splitscreen.FullGames && !SplitGames.IsClient && InMatch && !(Net.Multiplayer.Active && !Net.Multiplayer.Hosting);
            if (!want)
            {
                if (Players.Count > 0)
                {
                    Teardown();
                }
                return;
            }
            try
            {
                Patch();
                EnsurePlayers(Splitscreen.Players - 1);
                AssignDevices();
                foreach (SplitPlayer p in Players)
                {
                    ReadInput(p);
                    Act(p);
                }
                Layout();
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("splitscreen", e);
            }
        }

        private static void EnsurePlayers(int count)
        {
            while (Players.Count > count)
            {
                Remove(Players[Players.Count - 1]);
            }
            Actor me = LocalPlayer.actor;
            while (Players.Count < count)
            {
                var p = new SplitPlayer { Index = Players.Count + 1 };
                Actor a = ActorManager.instance.CreateAIActor(me.team);
                a.name = "Player " + (p.Index + 1);
                p.Actor = a;
                p.Yaw = me.controller != null ? Quaternion.LookRotation(me.controller.FacingDirection()).eulerAngles.y : 0f;
                ByActor[a] = p;
                Players.Add(p);
                try
                {
                    if (a.dead)
                    {
                        Vector3 beside = OnGroundBeside(me.Position(), p.Yaw, p.Index, out string where);
                        Log.Info($"Splitscreen: player {p.Index + 1} starts {where}.");
                        a.SpawnAt(beside, Quaternion.Euler(0f, p.Yaw, 0f), a.controller.GetLoadout());
                    }
                    if (a.controller is AiActorController ai && ai.squad == null)
                    {
                        new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
                    }
                    p.Cam = MakeCamera(p);
                }
                catch (Exception e)
                {
                    // Not left half made with no camera, and not made again every frame: splitscreen off, said why.
                    Remove(p);
                    Splitscreen.On = false;
                    Log.Warn($"Splitscreen: player {p.Index + 1} could not be made ({e.GetType().Name}: {e.Message}); splitscreen turned off.");
                    return;
                }
                Log.Info($"Splitscreen: player {p.Index + 1} joined the match as a soldier of team {a.team} (controller {PadOf(p) + 1}).");
            }
            if (_host == null)
            {
                _host = new GameObject("RavenfieldVR Splitscreen");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _host.AddComponent<SplitScreenHost>();
            }
        }

        /// <summary>
        /// Where an extra player starts: beside the game's own player on the same ground, else just behind it. Put 1.5 m to the side
        /// unchecked, one stood on a wall's edge or a ledge, fell over and slid, and could not shoot (check, 2026-11-14: "walked 8.3 m,
        /// fired 0").
        /// </summary>
        private static Vector3 OnGroundBeside(Vector3 me, float yaw, int index, out string where)
        {
            Quaternion turn = Quaternion.Euler(0f, yaw, 0f);
            string[] names = { "to the right", "to the left", "behind" };
            Vector3[] offsets = { new Vector3(1.5f * index, 0f, 0f), new Vector3(-1.5f * index, 0f, 0f), new Vector3(0f, 0f, -1.5f * index) };
            for (int k = 0; k < offsets.Length; k++)
            {
                Vector3 offset = offsets[k];
                Vector3 at = me + turn * offset;
                // (from head height down: nothing in the way from where the player stands, and the ground there no more than half a metre off)
                if (Physics.Raycast(me + Vector3.up * 1.5f, (at + Vector3.up * 1.5f - (me + Vector3.up * 1.5f)).normalized, offset.magnitude + 0.4f, ~0, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.down, out RaycastHit hit, 3f, ~0, QueryTriggerInteraction.Ignore)
                    && Mathf.Abs(hit.point.y - me.y) < 0.5f)
                {
                    where = $"{names[k]} of the player, {offset.magnitude:0.0} m";
                    return hit.point + Vector3.up * 0.05f;
                }
            }
            // (each its own place: on one spot they knocked each other over)
            where = "behind the player (no clear ground beside it)";
            return me + turn * new Vector3(0.9f * (index - 2), 0f, -1.2f);
        }

        /// <summary>The controller of a player: the first free ones in order, from <see cref="Splitscreen.FirstPad"/>.</summary>
        internal static int PadOf(SplitPlayer p) => Splitscreen.FirstPad + p.Index - 1;

        private static Camera MakeCamera(SplitPlayer p)
        {
            var go = new GameObject("RavenfieldVR Split Camera " + (p.Index + 1));
            UnityEngine.Object.DontDestroyOnLoad(go);
            try
            {
                return SetUpCamera(p, go);
            }
            catch (Exception)
            {
                UnityEngine.Object.Destroy(go); // (not left drawing over the screen with no player)
                p.View?.Destroy();
                p.View = null;
                throw;
            }
        }

        private static Camera SetUpCamera(SplitPlayer p, GameObject go)
        {
            Camera cam = go.AddComponent<Camera>();
            Camera source = FpsActorController.instance != null && FpsActorController.instance.fpCamera != null ? FpsActorController.instance.fpCamera : Camera.main;
            if (source != null)
            {
                cam.CopyFrom(source);
            }
            cam.targetTexture = null;
            cam.stereoTargetEye = StereoTargetEyeMask.None; // (the monitor, not the headset)
            cam.nearClipPlane = 0.08f; // (its own body is not drawn in its view: SplitView)
            cam.fieldOfView = 70f;
            cam.depth = 50 + p.Index;
            cam.enabled = true;
            p.View = new SplitView(p);
            SplitView.HookBodies();
            return cam;
        }

        private static void Remove(SplitPlayer p)
        {
            Players.Remove(p);
            if (p.Actor != null)
            {
                ByActor.Remove(p.Actor);
                try
                {
                    if (!p.Actor.dead)
                    {
                        if (p.Actor.IsSeated())
                        {
                            p.Actor.LeaveSeat(false); // (its seat free again for the others)
                        }
                        p.Actor.KillSilently();
                    }
                    p.Actor.isScheduledToSpawn = true; // (no longer a player: kept off the field, not brought back by the waves)
                }
                catch (Exception)
                {
                }
            }
            if (p.Cam != null)
            {
                UnityEngine.Object.Destroy(p.Cam.gameObject);
            }
            p.View?.Destroy();
            p.View = null;
        }

        public static void Teardown()
        {
            for (int i = Players.Count - 1; i >= 0; i--)
            {
                Remove(Players[i]);
            }
            foreach (KeyValuePair<Camera, Rect> kv in MainRects)
            {
                if (kv.Key != null)
                {
                    kv.Key.rect = kv.Value;
                }
            }
            MainRects.Clear();
            HudBack();
            SplitView.UnhookBodies();
            FlatPlayerInput.Active = false;
            RawInput.Stop();
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
                _host = null;
            }
            Log.Info("Splitscreen: back to one screen.");
        }

        /// <summary>
        /// Each player's controller if it has one, else the next keyboard and mouse (the game's own player keeps the first). With any on
        /// a keyboard, the keyboards and mice are told apart and kept from the game (its own player fed from the first; FlatPlayerInput),
        /// except while the game's menu is open, where the mouse is the game's again.
        /// </summary>
        private static void AssignDevices()
        {
            int pair = 0;
            bool anyKeys = false;
            foreach (SplitPlayer p in Players)
            {
                // (Try mode: player 2 takes the first keyboard and mouse, the game's own player gets none.)
                p.KeysPair = p.Simulated.HasValue || XInput.Read(PadOf(p)).Connected ? -1 : TryMode && p.Index == 1 ? 0 : ++pair;
                anyKeys |= p.KeysPair >= 0;
            }
            bool keys = anyKeys && !Runtime.OpenVRRuntime.Running;
            // (Only while the game holds the mouse for play: its menu, the deploy map after a death and the loadout need the pointer, and
            // with the merged mouse kept from it the game's own player could not click "Deploy".)
            bool playing = Cursor.lockState == CursorLockMode.Locked && !GameManager.IsPaused();
            if (keys)
            {
                RawInput.Start();
                RawInput.BlockLegacy(playing);
            }
            else if (RawInput.Started)
            {
                RawInput.BlockLegacy(false);
            }
            // A real controller in a splitscreen player's hands: the game's own pad input (Steam Input and Unity's joystick axes, every
            // controller merged) muted, else that controller moved the game's own player too (scout, 2026-11-14). The game's own player
            // on a controller then reads its own one (the one before FirstPad) through FlatPlayerInput.
            bool pads = false;
            foreach (SplitPlayer p in Players)
            {
                pads |= p.KeysPair < 0 && !p.Simulated.HasValue;
            }
            GamePadsMuted = pads;
            FlatPlayerInput.Pad = pads && Splitscreen.FirstPad > 0 ? Splitscreen.FirstPad - 1 : -1;
            FlatPlayerInput.OnlyPause = TryMode;
            FlatPlayerInput.Active = (keys || FlatPlayerInput.Pad >= 0) && playing;
        }

        /// <summary>
        /// Try mode (-rfvr-splittry, the user 2026-11-14: "i control the 2nd player only ... i wanna test how its for the other players"):
        /// player 2 on the first keyboard and mouse (or the first controller), the game's own player standing idle; Esc still pauses.
        /// </summary>
        internal static bool TryMode;

        /// <summary>The game's own controller input muted: each controller belongs to one player (see AssignDevices).</summary>
        internal static bool GamePadsMuted;

        private static void MuteSteamPads(ref float __result)
        {
            if (GamePadsMuted)
            {
                __result = 0f;
            }
        }

        private static void MuteUnityPads(ref SteelInput.Axis __instance, ref float __result)
        {
            if (GamePadsMuted && __result != 0f
                && (__instance.useUnityAxis ? __instance.unityAxis != null && __instance.unityAxis.Contains("Joy")
                    : __instance.posKeyCode >= KeyCode.JoystickButton0 || __instance.negKeyCode >= KeyCode.JoystickButton0))
            {
                __result = 0f;
            }
        }

        private static void ReadInput(SplitPlayer p)
        {
            if (p.KeysPair >= 0)
            {
                ReadKeys(p);
                return;
            }
            PadState pad = p.Simulated ?? XInput.Read(PadOf(p));
            float dt = Mathf.Min(Time.unscaledDeltaTime, 0.05f);
            p.Move = pad.LeftStick;
            float lookScale = pad.LeftTrigger > 0.5f ? 0.45f : 1f; // (slower aiming down the sights)
            p.Yaw += pad.RightStick.x * 180f * lookScale * dt;
            p.Pitch = Mathf.Clamp(p.Pitch + pad.RightStick.y * 120f * lookScale * dt, -80f, 80f);
            p.Fire = pad.RightTrigger > 0.5f;
            p.Aim = pad.LeftTrigger > 0.5f;
            bool Pressed(ushort b) => pad.Has(b) && (p.LastButtons & b) == 0;
            p.Jump = Pressed(PadState.A);
            if (Pressed(PadState.B))
            {
                p.Crouch = !p.Crouch;
            }
            if (Pressed(PadState.LeftThumb))
            {
                p.Sprint = !p.Sprint;
            }
            if (p.Move.sqrMagnitude < 0.04f)
            {
                p.Sprint = false; // (stopping ends a sprint)
            }
            p.Reload = Pressed(PadState.X);
            p.NextWeapon = Pressed(PadState.Y);
            p.Seat = Pressed(PadState.RightShoulder);
            p.LastButtons = pad.Buttons;
        }

        /// <summary>A player on its own keyboard and mouse (WASD, the mouse, R, Space, C, Shift, Q), told apart by Raw Input.</summary>
        private static void ReadKeys(SplitPlayer p)
        {
            RawDevice k = RawInput.Nth(false, p.KeysPair), m = RawInput.Nth(true, p.KeysPair);
            bool Key(int vk) => k != null && k.Keys[vk];
            p.Move = new Vector2((Key(0x44) ? 1f : 0f) - (Key(0x41) ? 1f : 0f), (Key(0x57) ? 1f : 0f) - (Key(0x53) ? 1f : 0f));
            if (m != null)
            {
                Vector2 d = m.ConsumeDelta();
                p.Yaw += d.x * 0.09f;
                p.Pitch = Mathf.Clamp(p.Pitch - d.y * 0.09f, -80f, 80f);
            }
            p.Fire = m != null && m.Left;
            p.Aim = m != null && m.Right;
            p.SlotPick = -1;
            for (int n = 0; n < 5; n++)
            {
                if (Key(0x31 + n))
                {
                    p.SlotPick = n; // (1 to 5: that weapon slot, as the game's own number keys)
                }
            }
            int wheel = m != null ? m.Wheel : 0;
            bool wheelNext = wheel < p.LastWheel;
            p.LastWheel = wheel;
            ushort buttons = (ushort)((Key(0x20) ? PadState.A : 0) | (Key(0x43) ? PadState.B : 0) | (Key(0x10) ? PadState.LeftThumb : 0)
                                      | (Key(0x52) ? PadState.X : 0) | (Key(0x51) ? PadState.Y : 0) | (Key(0x45) ? PadState.RightShoulder : 0));
            bool Pressed(ushort b) => (buttons & b) != 0 && (p.LastButtons & b) == 0;
            p.Jump = Pressed(PadState.A);
            if (Pressed(PadState.B))
            {
                p.Crouch = !p.Crouch;
            }
            p.Sprint = Key(0x10) && p.Move.y > 0f;
            p.Reload = Pressed(PadState.X);
            p.NextWeapon = Pressed(PadState.Y) || wheelNext;
            p.Seat = Pressed(PadState.RightShoulder);
            p.LastButtons = buttons;
        }

        private static void Act(SplitPlayer p)
        {
            Actor a = p.Actor;
            if (a == null)
            {
                return;
            }
            p.FirePressed = p.Fire && !p.LastFire;
            p.LastFire = p.Fire;
            if (a.dead != p.WasDead)
            {
                if (a.dead)
                {
                    p.Deaths++;
                    p.DiedAt = Time.time;
                    p.Crouch = p.Sprint = false; // (comes back standing, as the game's own player does)
                }
                p.WasDead = a.dead;
            }
            if (a.dead)
            {
                SplitLife.WhileDown(p); // (back in by its own button, SplitLife)
                return;
            }
            if (a.fallenOver)
            {
                return; // (knocked over: the game gets it up)
            }
            if (p.SlotPick >= 0 && a.weapons != null && p.SlotPick < a.weapons.Length && a.weapons[p.SlotPick] != null && p.SlotPick != a.activeWeaponSlot)
            {
                _switching = true;
                try
                {
                    a.SwitchWeapon(p.SlotPick);
                }
                finally
                {
                    _switching = false;
                }
            }
            if (p.Seat)
            {
                EnterOrLeave(p, a);
            }
            if (p.Reload && a.activeWeapon != null && !a.activeWeapon.reloading)
            {
                a.activeWeapon.Reload();
            }
            if (p.NextWeapon && a.weapons != null)
            {
                int n = a.weapons.Length;
                for (int k = 1; k <= n; k++)
                {
                    int slot = (a.activeWeaponSlot + k) % n;
                    if (a.weapons[slot] != null)
                    {
                        _switching = true;
                        try
                        {
                            a.SwitchWeapon(slot);
                        }
                        finally
                        {
                            _switching = false;
                        }
                        break;
                    }
                }
            }
        }

        /// <summary>Into the nearest free seat within 4 m (a driver's first), or out of the one it sits in.</summary>
        private static void EnterOrLeave(SplitPlayer p, Actor a)
        {
            if (a.IsSeated())
            {
                a.LeaveSeat(false);
                return;
            }
            Seat best = null;
            float bestScore = float.MaxValue;
            foreach (Vehicle v in UnityEngine.Object.FindObjectsOfType<Vehicle>())
            {
                if (v == null || v.dead || v.seats == null)
                {
                    continue;
                }
                foreach (Seat s in v.seats)
                {
                    if (s == null || s.IsOccupied())
                    {
                        continue;
                    }
                    float d = Vector3.Distance(s.transform.position, a.Position());
                    float score = d - (s.IsDriverSeat() ? 1f : 0f);
                    if (d < 4f && score < bestScore)
                    {
                        best = s;
                        bestScore = score;
                    }
                }
            }
            if (best != null)
            {
                _seating = true;
                try
                {
                    a.EnterSeat(best, true, true);
                }
                finally
                {
                    _seating = false;
                }
            }
        }

        /// <summary>Each player's part of the window, the game's own cameras put in the first in flat play.</summary>
        private static void Layout()
        {
            bool vr = Runtime.OpenVRRuntime.Running;
            int shown = Players.Count + (vr ? 0 : 1);
            int first = vr ? 0 : 1;
            if (!vr)
            {
                Rect mine = Part(0, shown);
                HudIntoOwnPart(mine);
                foreach (Camera c in OwnCameras())
                {
                    if (!MainRects.ContainsKey(c))
                    {
                        MainRects[c] = c.rect;
                    }
                    if (c.rect != mine)
                    {
                        c.rect = mine;
                    }
                }
            }
            foreach (SplitPlayer p in Players)
            {
                p.Viewport = Part(p.Index - 1 + first, shown);
                if (p.Cam != null && p.Cam.rect != p.Viewport)
                {
                    p.Cam.rect = p.Viewport;
                }
            }
        }

        /// <summary>The game's own screen-wide canvases drawn through the first player's camera, each with how it was.</summary>
        private static readonly Dictionary<Canvas, (RenderMode mode, Camera camera, float plane)> Moved = new Dictionary<Canvas, (RenderMode, Camera, float)>();
        private static float _nextCanvasLook;
        /// <summary>The camera the game's displays are drawn through, in the first player's part: only their layers (the game's view-model camera draws no UI layer, so they were not seen).</summary>
        private static Camera _hudCam;

        /// <summary>
        /// The game's own displays (health and rounds, the score bar, the deploy map and loadout after a death, its menus) drawn in the game's
        /// own player's part of the window, through its camera: as overlays they spread over the whole window and over the other players'
        /// parts (the check's pictures, 2026-11-14). Looked for once a second (a canvas the game makes later comes along).
        /// </summary>
        private static void HudIntoOwnPart(Rect part)
        {
            if (_hudCam == null)
            {
                var go = new GameObject("RavenfieldVR Split Camera HUD");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.transform.position = new Vector3(0f, -20000f, 0f); // (away from the world: it draws only the displays' layers)
                _hudCam = go.AddComponent<Camera>();
                _hudCam.clearFlags = CameraClearFlags.Depth;
                _hudCam.cullingMask = 0;
                _hudCam.stereoTargetEye = StereoTargetEyeMask.None;
                _hudCam.nearClipPlane = 0.1f;
                _hudCam.farClipPlane = 10f;
                _hudCam.depth = 90f; // (over the game's own cameras in that part)
                _hudCam.allowHDR = false;
                _hudCam.allowMSAA = false;
            }
            Camera cam = _hudCam;
            if (cam.rect != part)
            {
                cam.rect = part;
            }
            if (Time.unscaledTime >= _nextCanvasLook)
            {
                _nextCanvasLook = Time.unscaledTime + 1f;
                foreach (Canvas c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c == null || !c.isRootCanvas || Moved.ContainsKey(c) || c.renderMode != RenderMode.ScreenSpaceOverlay
                        || c.name.StartsWith("RavenfieldVR", StringComparison.Ordinal))
                    {
                        continue;
                    }
                    Moved[c] = (c.renderMode, c.worldCamera, c.planeDistance);
                    cam.cullingMask |= 1 << c.gameObject.layer;
                }
            }
            foreach (Canvas c in Moved.Keys)
            {
                if (c != null && (c.renderMode != RenderMode.ScreenSpaceCamera || c.worldCamera != cam))
                {
                    c.renderMode = RenderMode.ScreenSpaceCamera;
                    c.worldCamera = cam;
                    c.planeDistance = 1f;
                }
            }
        }

        private static void HudBack()
        {
            foreach (KeyValuePair<Canvas, (RenderMode mode, Camera camera, float plane)> kv in Moved)
            {
                if (kv.Key != null)
                {
                    kv.Key.renderMode = kv.Value.mode;
                    kv.Key.worldCamera = kv.Value.camera;
                    kv.Key.planeDistance = kv.Value.plane;
                }
            }
            Moved.Clear();
            if (_hudCam != null)
            {
                UnityEngine.Object.Destroy(_hudCam.gameObject);
                _hudCam = null;
            }
        }

        /// <summary>
        /// The game's own player's view and display drawn in one part of its window only (the game copies splitscreen: the copies' windows
        /// sit over the other parts of this one window). Cheap when already so: called once a frame.
        /// </summary>
        internal static void OwnViewInto(Rect part)
        {
            HudIntoOwnPart(part);
            foreach (Camera c in OwnCameras())
            {
                if (!MainRects.ContainsKey(c))
                {
                    MainRects[c] = c.rect;
                }
                if (c.rect != part)
                {
                    c.rect = part;
                }
            }
        }

        /// <summary>The game's own view over its whole window again.</summary>
        internal static void OwnViewBack()
        {
            foreach (KeyValuePair<Camera, Rect> kv in MainRects)
            {
                if (kv.Key != null)
                {
                    kv.Key.rect = kv.Value;
                }
            }
            MainRects.Clear();
            HudBack();
        }

        private static IEnumerable<Camera> OwnCameras()
        {
            FpsActorController f = FpsActorController.instance;
            if (f == null)
            {
                yield break;
            }
            if (f.fpCamera != null)
            {
                yield return f.fpCamera;
            }
            if (f.fpViewModelCamera != null)
            {
                yield return f.fpViewModelCamera;
            }
            if (f.tpCamera != null)
            {
                yield return f.tpCamera;
            }
        }

        /// <summary>Part <paramref name="i"/> of <paramref name="n"/> (Unity's viewport, 0..1 from the bottom left).</summary>
        internal static Rect Part(int i, int n)
        {
            if (n <= 1)
            {
                return new Rect(0f, 0f, 1f, 1f);
            }
            if (n == 2)
            {
                return Splitscreen.Layout == SplitLayout.SideBySide ? new Rect(i * 0.5f, 0f, 0.5f, 1f) : new Rect(0f, i == 0 ? 0.5f : 0f, 1f, 0.5f);
            }
            // Three or four: quarters, the first top left, then top right, bottom left, bottom right.
            float x = i % 2 == 0 ? 0f : 0.5f, y = i < 2 ? 0.5f : 0f;
            return new Rect(x, y, 0.5f, 0.5f);
        }

        /// <summary>After the game moved the soldiers: each camera at its soldier's eyes.</summary>
        /// <summary>Where a player starts beside the game's own one (SplitLife's respawn as well).</summary>
        internal static Vector3 GroundBeside(Vector3 me, float yaw, int index, out string where) => OnGroundBeside(me, yaw, index, out where);

        /// <summary>The splitscreen player a soldier is, or null.</summary>
        internal static SplitPlayer PlayerOf(Actor a) => a != null && Players.Count > 0 && ByActor.TryGetValue(a, out SplitPlayer p) ? p : null;

        /// <summary>The splitscreen player a camera is the world view of, or null.</summary>
        internal static SplitPlayer OwnerOf(Camera cam)
        {
            if (cam == null)
            {
                return null;
            }
            foreach (SplitPlayer p in Players)
            {
                if (p.Cam == cam)
                {
                    return p;
                }
            }
            return null;
        }

        internal static void LateTick()
        {
            foreach (SplitPlayer p in Players)
            {
                if (p.Cam == null || p.Actor == null)
                {
                    continue;
                }
                Actor a = p.Actor;
                if (!a.dead && !a.fallenOver)
                {
                    float eye = a.IsSeated() ? 1.2f : p.Crouch ? 1.0f : 1.6f;
                    p.Cam.transform.SetPositionAndRotation(a.Position() + Vector3.up * eye, Quaternion.Euler(-p.Pitch, p.Yaw, 0f));
                }
                else
                {
                    // Dead: looking down at where it fell from above, until the game brings it back.
                    p.Cam.transform.SetPositionAndRotation(a.CenterPosition() + Vector3.up * 3f - Quaternion.Euler(0f, p.Yaw, 0f) * Vector3.forward * 2f,
                                                           Quaternion.Euler(55f, p.Yaw, 0f));
                }
                try
                {
                    p.View?.Tick();
                }
                catch (Exception e)
                {
                    Log.ExceptionOnce("split-view", e);
                }
            }
        }

        internal static void DrawHud()
        {
            foreach (SplitPlayer p in Players)
            {
                if (p.Actor == null)
                {
                    continue;
                }
                // Unity's viewport is from the bottom; the GUI's from the top.
                Rect r = new Rect(p.Viewport.x * Screen.width, (1f - p.Viewport.y - p.Viewport.height) * Screen.height,
                                  p.Viewport.width * Screen.width, p.Viewport.height * Screen.height);
                SplitLife.Draw(p, r);
            }
        }

        // ---- the AI's answers, for the players' soldiers ----------------------------------------------------------------

        internal static void Patch()
        {
            if (_harmony != null)
            {
                return;
            }
            _harmony = new Harmony(PluginInfo.Guid + ".split");
            Type ai = typeof(AiActorController);
            // Each on its own: one the game lacks (another version) is said and the others still applied.
            void One(Type type, string method, string patch, bool prefix)
            {
                try
                {
                    var target = AccessTools.Method(type, method);
                    if (target == null)
                    {
                        Log.Warn($"Splitscreen: {type.Name}.{method} not found in this game version; its answer for the players is not taken.");
                        return;
                    }
                    var hm = new HarmonyMethod(typeof(SplitPlayers), patch);
                    _harmony.Patch(target, prefix: prefix ? hm : null, postfix: prefix ? null : hm);
                }
                catch (Exception e)
                {
                    Log.Warn($"Splitscreen: {type.Name}.{method} could not be patched: {e.Message}");
                }
            }
            One(ai, nameof(AiActorController.Velocity), nameof(SplitVelocity), false);
            One(ai, nameof(AiActorController.FacingDirection), nameof(SplitFacing), false);
            One(ai, nameof(AiActorController.WantsToFire), nameof(SplitFire), false);
            One(ai, nameof(AiActorController.Crouch), nameof(SplitCrouch), false);
            One(ai, nameof(AiActorController.Prone), nameof(SplitNo), false);
            One(ai, nameof(AiActorController.Jump), nameof(SplitJump), false);
            One(ai, nameof(AiActorController.Aiming), nameof(SplitAim), false);
            One(ai, nameof(AiActorController.HoldingSprint), nameof(SplitSprint), false);
            One(ai, nameof(AiActorController.IsSprinting), nameof(SplitSprint), false);
            One(ai, nameof(AiActorController.Reload), nameof(SplitNo), false);
            One(ai, nameof(AiActorController.CarInput), nameof(SplitDrive), false);
            One(ai, nameof(AiActorController.BoatInput), nameof(SplitDrive), false);
            One(typeof(Actor), nameof(Actor.SwitchWeapon), nameof(SplitSwitch), true);
            One(typeof(Actor), nameof(Actor.EnterSeat), nameof(SplitSeat), true);
            One(typeof(Actor), nameof(Actor.Damage), nameof(SplitDamaged), false);
            One(typeof(SteelInput.Axis), nameof(SteelInput.Axis.GetValueSteam), nameof(MuteSteamPads), false);
            One(typeof(SteelInput.Axis), nameof(SteelInput.Axis.GetValueUnity), nameof(MuteUnityPads), false);
        }

        private static bool _switching, _seating;

        /// <summary>Hit markers, kills and hurt flashes for the splitscreen players (SplitLife).</summary>
        private static void SplitDamaged(Actor __instance, DamageInfo info)
        {
            if (Players.Count == 0)
            {
                return;
            }
            try
            {
                SplitLife.OnDamage(__instance, info);
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("split-damage", e);
            }
        }

        /// <summary>
        /// A splitscreen soldier gets into a vehicle only when its player asks (RB or E): its squad's AI took a nearby car by itself, and
        /// the player found itself driving off with no gun in its hands (check, 2026-11-14: "walked 14.7 m, fired 0 ... seated").
        /// </summary>
        private static bool SplitSeat(Actor __instance, ref bool __result)
        {
            if (_seating || Players.Count == 0 || __instance == null || !ByActor.ContainsKey(__instance))
            {
                return true;
            }
            __result = false;
            return false;
        }

        /// <summary>
        /// A splitscreen soldier holds the gun its player picked: its AI's own choice is not taken (the net stand-ins' AI switched their
        /// guns back and forth twice a second, NetGame.OnSwitchWeapon; scout, 2026-11-14).
        /// </summary>
        private static bool SplitSwitch(Actor __instance) => _switching || Players.Count == 0 || __instance == null || !ByActor.ContainsKey(__instance);

        private static bool Mine(AiActorController c, out SplitPlayer p)
        {
            p = null;
            return Players.Count > 0 && c != null && c.actor != null && ByActor.TryGetValue(c.actor, out p);
        }

        private static void SplitVelocity(AiActorController __instance, ref Vector3 __result)
        {
            if (!Mine(__instance, out SplitPlayer p))
            {
                return;
            }
            float speed = p.Sprint ? 6.5f : p.Crouch ? 1.8f : 4f;
            Quaternion yaw = Quaternion.Euler(0f, p.Yaw, 0f);
            __result = yaw * new Vector3(p.Move.x, 0f, p.Move.y) * speed;
        }

        private static void SplitFacing(AiActorController __instance, ref Vector3 __result)
        {
            if (Mine(__instance, out SplitPlayer p))
            {
                __result = Quaternion.Euler(-p.Pitch, p.Yaw, 0f) * Vector3.forward;
            }
        }

        private static void SplitFire(AiActorController __instance, ref bool __result)
        {
            if (Mine(__instance, out SplitPlayer p))
            {
                // A gun that is not automatic fires once per pull: held down, its trigger is pulled again about ten times a second (the AI
                // holding "fire" fired a pistol or a shotgun once; the local check of 2026-11-14).
                Weapon w = __instance.actor.activeWeapon;
                bool auto = w == null || w.configuration == null || w.configuration.auto;
                __result = p.Fire && (auto || Time.frameCount % 6 < 3);
            }
        }

        private static void SplitCrouch(AiActorController __instance, ref bool __result)
        {
            if (Mine(__instance, out SplitPlayer p))
            {
                __result = p.Crouch;
            }
        }

        private static void SplitJump(AiActorController __instance, ref bool __result)
        {
            if (Mine(__instance, out SplitPlayer p))
            {
                __result = p.Jump;
            }
        }

        private static void SplitAim(AiActorController __instance, ref bool __result)
        {
            if (Mine(__instance, out SplitPlayer p))
            {
                __result = p.Aim;
            }
        }

        private static void SplitSprint(AiActorController __instance, ref bool __result)
        {
            if (Mine(__instance, out SplitPlayer p))
            {
                __result = p.Sprint;
            }
        }

        /// <summary>A car or a boat: the left stick steers and drives.</summary>
        private static void SplitDrive(AiActorController __instance, ref Vector2 __result)
        {
            if (Mine(__instance, out SplitPlayer p))
            {
                __result = p.Move;
            }
        }

        /// <summary>The AI's own wish (to lie down, to reload on its own): never for a player's soldier, whose controller decides.</summary>
        private static void SplitNo(AiActorController __instance, ref bool __result)
        {
            if (Mine(__instance, out _))
            {
                __result = false;
            }
        }
    }

    /// <summary>The splitscreen's per-frame hooks that need a component: the cameras after the game's movement, the players' displays.</summary>
    internal sealed class SplitScreenHost : MonoBehaviour
    {
        private void LateUpdate() => SplitPlayers.LateTick();

        private void OnGUI() => SplitPlayers.DrawHud();
    }
}
