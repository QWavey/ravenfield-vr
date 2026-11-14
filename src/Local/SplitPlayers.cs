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
        /// <summary>Its keyboard and mouse pair (1: the second keyboard and mouse) when it has no controller, else -1.</summary>
        public int KeysPair = -1;
        public Rect Viewport;
        public int Kills, Deaths;
        public bool WasDead;
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

        private static bool InMatch => ActorManager.instance != null && LocalPlayer.actor != null && GameManager.instance != null && !GameManager.instance.isInNoGameplayScene;

        /// <summary>Once a frame (Multiplayer.Update): the players made or taken away, their controllers read.</summary>
        public static void Update()
        {
            bool want = Splitscreen.On && InMatch;
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
                if (a.dead)
                {
                    Vector3 beside = me.Position() + Quaternion.Euler(0f, p.Yaw, 0f) * new Vector3(1.5f * p.Index, 0f, 0f);
                    a.SpawnAt(beside, Quaternion.Euler(0f, p.Yaw, 0f), a.controller.GetLoadout());
                }
                if (a.controller is AiActorController ai && ai.squad == null)
                {
                    new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
                }
                p.Cam = MakeCamera(p);
                Log.Info($"Splitscreen: player {p.Index + 1} joined the match as a soldier of team {a.team} (controller {PadOf(p) + 1}).");
            }
            if (_host == null)
            {
                _host = new GameObject("RavenfieldVR Splitscreen");
                UnityEngine.Object.DontDestroyOnLoad(_host);
                _host.AddComponent<SplitScreenHost>();
            }
        }

        /// <summary>The controller of a player: the first free ones in order, from <see cref="Splitscreen.FirstPad"/>.</summary>
        internal static int PadOf(SplitPlayer p) => Splitscreen.FirstPad + p.Index - 1;

        private static Camera MakeCamera(SplitPlayer p)
        {
            var go = new GameObject("RavenfieldVR Split Camera " + (p.Index + 1));
            UnityEngine.Object.DontDestroyOnLoad(go);
            Camera cam = go.AddComponent<Camera>();
            Camera source = FpsActorController.instance != null && FpsActorController.instance.fpCamera != null ? FpsActorController.instance.fpCamera : Camera.main;
            if (source != null)
            {
                cam.CopyFrom(source);
            }
            cam.targetTexture = null;
            cam.stereoTargetEye = StereoTargetEyeMask.None; // (the monitor, not the headset)
            cam.nearClipPlane = 0.2f; // (inside the soldier's own head: its face is not drawn)
            cam.fieldOfView = 70f;
            cam.depth = 50 + p.Index;
            cam.enabled = true;
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
            foreach (SplitPlayer p in Players)
            {
                p.KeysPair = p.Simulated.HasValue || XInput.Read(PadOf(p)).Connected ? -1 : ++pair;
            }
            bool keys = pair > 0 && !Runtime.OpenVRRuntime.Running;
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
            FlatPlayerInput.Active = keys && playing;
        }

        private static void ReadInput(SplitPlayer p)
        {
            if (p.KeysPair > 0)
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
            p.NextWeapon = Pressed(PadState.Y);
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
            if (a.dead != p.WasDead)
            {
                if (a.dead)
                {
                    p.Deaths++;
                }
                p.WasDead = a.dead;
            }
            if (a.dead)
            {
                return;
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
                        a.SwitchWeapon(slot);
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
                a.EnterSeat(best, true, true);
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
                HudIntoOwnPart();
                Rect mine = Part(0, shown);
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

        private static Canvas _hud;
        private static RenderMode _hudMode;
        private static Camera _hudCamera;
        private static float _hudPlane;

        /// <summary>
        /// The game's own display (health, rounds, the score) drawn in the game's own player's part of the window, through its camera:
        /// as an overlay it spread over the whole window and over the other players' parts (the first check's pictures, 2026-11-14).
        /// </summary>
        private static void HudIntoOwnPart()
        {
            FpsActorController f = FpsActorController.instance;
            Canvas c = IngameUI.instance != null ? IngameUI.instance.canvas : null;
            Camera cam = f != null ? f.fpViewModelCamera != null && f.fpViewModelCamera.enabled ? f.fpViewModelCamera : f.fpCamera : null;
            if (c == null || cam == null)
            {
                return;
            }
            if (_hud != c)
            {
                HudBack();
                _hud = c;
                _hudMode = c.renderMode;
                _hudCamera = c.worldCamera;
                _hudPlane = c.planeDistance;
            }
            if (c.renderMode != RenderMode.ScreenSpaceCamera || c.worldCamera != cam)
            {
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = cam;
                c.planeDistance = cam.nearClipPlane + 0.02f;
            }
        }

        private static void HudBack()
        {
            if (_hud != null)
            {
                _hud.renderMode = _hudMode;
                _hud.worldCamera = _hudCamera;
                _hud.planeDistance = _hudPlane;
            }
            _hud = null;
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
        internal static void LateTick()
        {
            foreach (SplitPlayer p in Players)
            {
                if (p.Cam == null || p.Actor == null)
                {
                    continue;
                }
                Actor a = p.Actor;
                if (!a.dead)
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
            }
        }

        internal static void DrawHud()
        {
            foreach (SplitPlayer p in Players)
            {
                Actor a = p.Actor;
                if (a == null)
                {
                    continue;
                }
                // Unity's viewport is from the bottom; the GUI's from the top.
                Rect r = new Rect(p.Viewport.x * Screen.width, (1f - p.Viewport.y - p.Viewport.height) * Screen.height,
                                  p.Viewport.width * Screen.width, p.Viewport.height * Screen.height);
                GUI.color = Color.white;
                if (a.dead)
                {
                    GUI.Label(new Rect(r.x + 20f, r.y + r.height * 0.5f, r.width - 40f, 40f), $"Player {p.Index + 1}: down. Back at the next wave.");
                    continue;
                }
                GUI.Label(new Rect(r.center.x - 8f, r.center.y - 12f, 20f, 24f), "+");
                Weapon w = a.activeWeapon;
                string ammo = w != null ? w.reloading ? "reloading" : $"{w.ammo}/{w.spareAmmo}" : "";
                string weapon = w != null ? w.name.Replace("(Clone)", "").Trim() : "";
                GUI.Label(new Rect(r.x + 16f, r.yMax - 52f, r.width - 32f, 24f), $"Player {p.Index + 1}   Health {Mathf.CeilToInt(a.health)}");
                GUI.Label(new Rect(r.x + 16f, r.yMax - 30f, r.width - 32f, 24f), $"{weapon}   {ammo}");
            }
        }

        // ---- the AI's answers, for the players' soldiers ----------------------------------------------------------------

        private static void Patch()
        {
            if (_harmony != null)
            {
                return;
            }
            _harmony = new Harmony(PluginInfo.Guid + ".split");
            Type ai = typeof(AiActorController);
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.Velocity)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitVelocity)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.FacingDirection)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitFacing)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.WantsToFire)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitFire)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.Crouch)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitCrouch)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.Prone)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitNo)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.Jump)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitJump)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.Aiming)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitAim)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.HoldingSprint)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitSprint)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.IsSprinting)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitSprint)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.Reload)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitNo)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.CarInput)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitDrive)));
            _harmony.Patch(AccessTools.Method(ai, nameof(AiActorController.BoatInput)), postfix: new HarmonyMethod(typeof(SplitPlayers), nameof(SplitDrive)));
        }

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
