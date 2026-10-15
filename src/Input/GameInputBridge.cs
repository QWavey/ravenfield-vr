using System;
using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Util;
using UnityEngine;
using KeyBinds = SteelInput.KeyBinds;

namespace RavenfieldVR.Input
{
    /// <summary>
    /// Translates SteamVR actions into Ravenfield's SteelInput key binds once per frame, by context
    /// (on foot, seated, dead, menu). Values are merged into the game's own reads by the patches below,
    /// so keyboard/mouse keeps working alongside the controllers.
    /// </summary>
    public static class VRGameInput
    {
        private static readonly int BindCount = MaxBind() + 1;
        private static readonly float[] Axis = new float[BindCount];
        private static readonly bool[] Held = new bool[BindCount];
        private static readonly bool[] WasHeld = new bool[BindCount];

        /// <summary>True while VR input should be merged into the game (VR running and input ready).</summary>
        public static bool Enabled;

        public static bool SprintActive { get; private set; }
        public static bool CrouchToggled { get; set; }
        public static bool ProneToggled { get; set; }

        /// <summary>Set by the locomotion system when the player physically crouches.</summary>
        public static bool PhysicalCrouch { get; set; }

        /// <summary>Set by the hand system: off-hand wrist turned towards the face (enlarges the minimap).</summary>
        public static bool WristLookGesture { get; set; }

        /// <summary>The wrist is turned toward the eyes right now (before the hold that opens the wrist view).</summary>
        public static bool WristLooking { get; set; }

        /// <summary>How far the hold at the wrist has gone toward opening the wrist view (0..1; the ring on the wrist).</summary>
        public static float WristLookProgress { get; set; }

        /// <summary>Set by the hand system: grip pressed with the off hand at the side of the head (night vision).</summary>
        public static bool GogglesGesturePressed { get; set; }

        /// <summary>Set by the UI module while a laser pointer is over a menu, so triggers don't also act in game.</summary>
        public static bool PointerOverUI { get; set; }

        /// <summary>Set by the UI system while one of the mod's own panels (VR settings, Mod keys) is open in game.</summary>
        public static bool PanelOpen { get; set; }

        /// <summary>The laser is on one of the mod's open panels, so the sticks scroll it and do nothing else.</summary>
        public static bool SticksOnPanel => PanelOpen && PointerOverUI;

        /// <summary>Weapon systems set this while the dominant trigger must not fire (e.g. holding a grenade pin).</summary>
        public static bool SuppressFire { get; set; }

        public static Vector2 MoveStick { get; private set; }
        public static Vector2 TurnStickRaw { get; private set; }

        /// <summary>Whether the scoreboard comes up with the map (the scoreboard button switches it); never on its own.</summary>
        private static bool _scoreboardWithMap = true;
        private static bool _jumpLatch;
        private static bool _downLatch;
        private static float _downSince;
        private static bool _proneTriggeredThisHold;
        private static float _idleSince;
        private static bool _sprintClearsStance;
        private static int _computedFrame = -1;

        private static int MaxBind()
        {
            int max = 0;
            foreach (int v in Enum.GetValues(typeof(KeyBinds)))
            {
                max = Math.Max(max, v);
            }
            return max;
        }

        public static void Compute()
        {
            if (!Enabled || _computedFrame == Time.frameCount)
            {
                return;
            }
            _computedFrame = Time.frameCount;
            // Taken every frame: the gesture made while dead or in a menu (a hand at the headset, adjusting it) stayed
            // latched until the next frame in play and switched the night vision on or off then.
            bool goggles = GogglesGesturePressed;
            GogglesGesturePressed = false;
            Array.Copy(Held, WasHeld, BindCount);
            Array.Clear(Held, 0, BindCount);
            Array.Clear(Axis, 0, BindCount);

            float dz = VRConfig.StickDeadzone.Value;
            // Scrolling one of the mod's panels uses the sticks: they must not also walk, jump or crouch.
            bool scrollingPanel = SticksOnPanel;
            MoveStick = scrollingPanel ? Vector2.zero : ApplyDeadzone(VRInput.Move, dz);
            TurnStickRaw = scrollingPanel ? Vector2.zero : VRInput.TurnStick;

            // The hand holding the weapon fires it; the other uses (with the weapon passed to the other hand, VRInput has the
            // two hands' buttons trade places).
            Hand dom = VRInput.GunHand;
            Hand off = VRInput.FreeHand;
            bool menu = GameContext.GameMenuOpen || !GameContext.InGame;

            // Always available: menus and respawn.
            Set(KeyBinds.TogglePauseMenu, VRInput.GetPressed(DigitalAction.Pause) || LowerButtonDoublePressed());
            Set(KeyBinds.OpenLoadout, VRInput.GetPressed(DigitalAction.Loadout));
            Set(KeyBinds.SquadLeaderKit, VRInput.GetPressed(DigitalAction.Map));
            // In VR the scoreboard only ever shows together with the map (the wrist look): on its own it covered the
            // view while aiming. The scoreboard button chooses whether it comes up with the map.
            if (VRInput.GetPressed(DigitalAction.Scoreboard))
            {
                _scoreboardWithMap = !_scoreboardWithMap;
            }
            // Held as the peek (the game's own SteamVR binding does the same): the Scoreboard key opens it focused, which frees
            // the cursor, which counts as a menu here, which let the key go, which closed it: the whole wrist view (map,
            // scoreboard, match bar, wrist display) came and went every other frame.
            Set(KeyBinds.PeekScoreboard, _scoreboardWithMap && !menu && WristLookGesture && GameContext.InGame);
            // Clicking a menu or one of the mod's panels with the laser must not also pull the gun's trigger.
            Set(KeyBinds.Fire, VRInput.GetHeld(DigitalAction.Fire, dom) && !SuppressFire && !((menu || PanelOpen) && PointerOverUI));

            if (menu)
            {
                _leaveHeldSince = -1f; // the hold to leave a seat starts again after the menu, not counting the time in it
                return;
            }

            Actor actor = GameContext.PlayerActor;
            if (actor == null || actor.dead)
            {
                _leaveHeldSince = -1f;
                return;
            }

            // Not while a panel of the mod is under the laser: that trigger clicks the panel (and entered or left vehicles).
            bool onPanel = PanelOpen && PointerOverUI;
            if (onPanel || !actor.IsSeated())
            {
                _leaveHeldSince = -1f;
            }
            Set(KeyBinds.Use, !onPanel && (actor.IsSeated() ? HeldToLeaveSeat(off) : VRInput.GetHeld(DigitalAction.Use, off)));
            Set(KeyBinds.Call, VRInput.GetHeld(DigitalAction.Squad));
            Set(KeyBinds.NextWeapon, VRInput.GetPressed(DigitalAction.WeaponNext));
            Set(KeyBinds.Countermeasures, VRInput.GetPressed(DigitalAction.Countermeasures));
            Set(KeyBinds.Goggles, goggles);
            Set(KeyBinds.Map, WristLookGesture);

            if (actor.IsSeated())
            {
                ComputeSeated();
                return;
            }
            ComputeOnFoot(actor);
        }

        private static readonly float[] LowerPressedAt = { -1f, -1f };

        /// <summary>
        /// The lower face button (A or X) pressed twice quickly opens the game's menu. Done here rather than as a SteamVR
        /// double click: as a binding, every slide release waited out the double-click window and the squad button lost its
        /// hold (squad orders need it held). The first press still does its job; the second is taken.
        /// </summary>
        private static bool LowerButtonDoublePressed()
        {
            bool open = false;
            for (int h = 0; h < 2; h++)
            {
                var hand = (Hand)h;
                // Whichever the weapon's hand: the lower button is slide release on one controller and squad on the other.
                if (!VRInput.GetPressed(DigitalAction.SlideRelease, hand) && !VRInput.GetPressed(DigitalAction.Squad, hand))
                {
                    continue;
                }
                float now = Time.unscaledTime;
                if (LowerPressedAt[h] >= 0f && now - LowerPressedAt[h] < 0.4f)
                {
                    LowerPressedAt[h] = -1f;
                    VRInput.Consume(DigitalAction.SlideRelease, hand);
                    VRInput.Consume(DigitalAction.Squad, hand);
                    open = true;
                }
                else
                {
                    LowerPressedAt[h] = now;
                }
            }
            return open;
        }

        /// <summary>How long the Use trigger is held in a seat before the player gets out.</summary>
        private const float LeaveSeatHold = 1f;
        private static float _leaveHeldSince = -1f;
        private static int _leaveTicks;

        /// <summary>
        /// In a seat, Use (the off hand's trigger) gets you out only when held for a second, with ticks building up in
        /// the hand: a fist closed on the wheel squeezes the trigger too, and a single click threw the driver out of the
        /// car. A hand on the wheel or the flight stick never counts.
        /// </summary>
        private static bool HeldToLeaveSeat(Hand off)
        {
            Rig.VRHand hand = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Hand(off) : null;
            bool held = VRInput.GetHeld(DigitalAction.Use, off) && (hand == null || hand.Role != Rig.HandRole.Cockpit);
            if (!held)
            {
                _leaveHeldSince = -1f;
                return false;
            }
            if (_leaveHeldSince < 0f)
            {
                _leaveHeldSince = Time.unscaledTime;
                _leaveTicks = 0;
            }
            float t = (Time.unscaledTime - _leaveHeldSince) / LeaveSeatHold;
            if (t >= 1f)
            {
                return true; // stays held: the game sees one press, and nothing more on foot until the trigger is let go
            }
            int tick = Mathf.FloorToInt(t * 5f);
            if (tick > _leaveTicks)
            {
                _leaveTicks = tick;
                hand?.Haptic(0.02f, 120f + 60f * tick, 0.15f + 0.15f * tick);
            }
            return false;
        }

        private static void ComputeOnFoot(Actor actor)
        {
            // FirstPersonControllerInput feeds -Horizontal as the strafe input, so positive stick X is negative Horizontal.
            SetAxis(KeyBinds.Horizontal, -MoveStick.x);
            SetAxis(KeyBinds.Vertical, MoveStick.y);
            Set(KeyBinds.Kick, VRInput.GetHeld(DigitalAction.Kick));

            HandleWeaponButtons(actor.activeWeapon);

            UpdateSprint(actor);
            UpdateStickJumpCrouch(actor);
        }

        /// <summary>Fire mode / sights and the assisted reload for the personal weapon in hand.</summary>
        private static void HandleWeaponButtons(Weapon weapon)
        {
            if (VRInput.GetPressed(DigitalAction.FireMode))
            {
                // Looking through the sights: flip the magnifier / alternate sight. Otherwise: the weapon's own
                // fire-mode script, the mod's sub-weapon modes, else our semi/auto selector (automatic weapons only).
                Weapons.WeaponSystem system = Weapons.WeaponSystem.Instance;
                bool aiming = system != null && system.Held != null && system.Held.Aiming;
                ScriptKey magnifier = aiming ? ScriptKeys.Find(weapon, ScriptKeyPurpose.Magnifier) : null;
                if (magnifier != null)
                {
                    ScriptKeys.Tap(magnifier.Key, magnifier.Script);
                }
                else if (weapon != null && weapon.HasAnyAltSightModes() && (aiming || !weapon.HasAnySubWeapons() && !Weapons.FireSelector.HasModes(weapon)
                                                                             && ScriptKeys.Find(weapon, ScriptKeyPurpose.FireMode) == null))
                {
                    Set(KeyBinds.NextScope, true);
                }
                else if (system == null || !system.HandleFireModePress(weapon)) // the weapon's own fire-mode key works with the selector off too
                {
                    Set(KeyBinds.FireMode, true);
                }
            }
            if (VRConfig.ReloadNow == ReloadMode.Assisted && VRInput.GetPressed(DigitalAction.MagRelease, VRInput.GunHand))
            {
                Set(KeyBinds.Reload, true);
            }
        }

        private static void ComputeSeated()
        {
            Actor actor = GameContext.PlayerActor;
            if (actor != null && (Weapons.DrivingWeapons.Armed || GameContext.HeldPersonalWeapon != null))
            {
                // A personal weapon in hand (drawn from the seat, or a seat the game lets you shoot from): its buttons
                // work the weapon, the car keeps the sticks and the wheel.
                HandleWeaponButtons(actor.activeWeapon);
                SprintActive = false;
                _jumpLatch = _downLatch = false;
                return;
            }
            // Vehicle axes are produced by the CarInput/HelicopterInput/AirplaneInput patches; here only buttons.
            if (VRInput.GetPressed(DigitalAction.SlideRelease, VRInput.GunHand))
            {
                Set(KeyBinds.Countermeasures, true);
            }
            // B reloads a mounted gun (a car's LMG, a turret), as R does on the keyboard.
            if (VRInput.GetPressed(DigitalAction.MagRelease, VRInput.GunHand))
            {
                Set(KeyBinds.Reload, true);
            }
            if (VRInput.GetPressed(DigitalAction.FireMode))
            {
                Set(GameContext.SeatKind == VehicleKind.Helicopter ? KeyBinds.AutoHover : KeyBinds.FireMode, true);
            }
            SprintActive = false;
            _jumpLatch = _downLatch = false;
        }

        private static void UpdateSprint(Actor actor)
        {
            if (VRConfig.Sprint.Value == SprintMode.Hold)
            {
                SprintActive = VRInput.GetHeld(DigitalAction.Sprint);
                return;
            }
            if (VRInput.GetPressed(DigitalAction.Sprint))
            {
                SprintActive = !SprintActive;
                if (SprintActive)
                {
                    // Pressed standing still: a moment to push the stick (it was switched off again in the same frame), and
                    // the crouch or prone stays until the sprint really starts.
                    _idleSince = Time.unscaledTime + 0.25f;
                    _sprintClearsStance = true;
                }
            }
            // Toggle sprint ends once the stick has been released for a moment, like most VR shooters.
            if (MoveStick.sqrMagnitude < 0.04f)
            {
                if (Time.unscaledTime - _idleSince > 0.35f)
                {
                    SprintActive = false;
                    _sprintClearsStance = false;
                }
            }
            else
            {
                _idleSince = Mathf.Max(_idleSince, Time.unscaledTime);
                if (SprintActive && _sprintClearsStance)
                {
                    _sprintClearsStance = false;
                    CrouchToggled = false;
                    ProneToggled = false;
                }
            }
        }

        private static void UpdateStickJumpCrouch(Actor actor)
        {
            if (!VRConfig.StickJumpCrouch.Value)
            {
                return;
            }
            float y = TurnStickRaw.y;
            float x = Mathf.Abs(TurnStickRaw.x);

            // Jump: a deliberate upward push that is mostly vertical (so snap turns don't jump).
            if (!_jumpLatch && y > 0.8f && x < 0.5f)
            {
                _jumpLatch = true;
                Set(KeyBinds.Jump, true);
                if (actor.stance != Actor.Stance.Stand)
                {
                    CrouchToggled = false;
                    ProneToggled = false;
                }
            }
            else if (_jumpLatch && y < 0.4f)
            {
                _jumpLatch = false;
            }

            // Down: a flick toggles crouch, holding toggles prone.
            if (!_downLatch && y < -0.8f && x < 0.5f)
            {
                _downLatch = true;
                _downSince = Time.unscaledTime;
                _proneTriggeredThisHold = false;
            }
            else if (_downLatch)
            {
                if (!_proneTriggeredThisHold && Time.unscaledTime - _downSince > 0.6f)
                {
                    _proneTriggeredThisHold = true;
                    ProneToggled = !ProneToggled;
                    CrouchToggled = false;
                    SprintActive = false;
                }
                if (y > -0.4f)
                {
                    _downLatch = false;
                    if (!_proneTriggeredThisHold)
                    {
                        if (ProneToggled)
                        {
                            ProneToggled = false;
                            CrouchToggled = true;
                        }
                        else
                        {
                            CrouchToggled = !CrouchToggled;
                        }
                        SprintActive = false;
                    }
                }
            }
        }

        public static Vector2 ApplyDeadzone(Vector2 v, float deadzone)
        {
            float m = v.magnitude;
            if (m <= deadzone || m < 1e-4f)
            {
                return Vector2.zero;
            }
            // Rescale so output starts at 0 right after the dead zone instead of jumping.
            float scaled = Mathf.Clamp01((m - deadzone) / (1f - deadzone));
            return v / m * scaled;
        }

        private static void Set(KeyBinds b, bool state)
        {
            if (state)
            {
                Held[(int)b] = true;
            }
        }

        private static void SetAxis(KeyBinds b, float v) => Axis[(int)b] = v;

        public static float MergeAxis(KeyBinds b, float game)
        {
            int i = (int)b;
            if (b == KeyBinds.AimX || b == KeyBinds.AimY || b == KeyBinds.Lean)
            {
                // Looking and leaning come from head tracking.
                return 0f;
            }
            float v = Axis[i];
            if (v == 0f && Held[i])
            {
                v = 1f;
            }
            return Mathf.Clamp(game + v, -1f, 1f);
        }

        public static bool MergeButton(KeyBinds b, bool game) => (game && !VROnly(b)) || Held[(int)b];
        public static bool MergeButtonDown(KeyBinds b, bool game) => (game && !VROnly(b)) || (Held[(int)b] && !WasHeld[(int)b]);
        public static bool MergeButtonUp(KeyBinds b, bool game) => (game && !VROnly(b)) || (!Held[(int)b] && WasHeld[(int)b]);

        /// <summary>
        /// Binds only the VR mapping may press: the scoreboard (and its peek) would otherwise come up from a gamepad or
        /// Steam Input layout the headset's controllers also feed, right in front of the sights.
        /// </summary>
        private static bool VROnly(KeyBinds b) => b == KeyBinds.Scoreboard || b == KeyBinds.PeekScoreboard;

        /// <summary>Resets toggles when the player respawns, gets into or out of a seat, or onto or off a ladder.</summary>
        public static void ResetToggles()
        {
            SprintActive = false;
            CrouchToggled = false;
            ProneToggled = false;
        }
    }

    [HarmonyPatch(typeof(SteelInput), nameof(SteelInput.Update))]
    internal static class SteelInputUpdatePatch
    {
        private static void Prefix()
        {
            if (!VRGameInput.Enabled)
            {
                return;
            }
            // Thrown out of here, the exception would stop the game's own input update too: every button dead for good.
            try
            {
                VRInput.Update();
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("VRInput.Update", e);
            }
            try
            {
                VRGameInput.Compute();
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("VRGameInput.Compute", e);
            }
        }
    }

    [HarmonyPatch(typeof(SteelInput), nameof(SteelInput.GetAxis))]
    internal static class SteelInputGetAxisPatch
    {
        private static void Postfix(KeyBinds input, ref float __result)
        {
            if (VRGameInput.Enabled)
            {
                __result = VRGameInput.MergeAxis(input, __result);
            }
        }
    }

    [HarmonyPatch(typeof(SteelInput), nameof(SteelInput.GetButton))]
    internal static class SteelInputGetButtonPatch
    {
        private static void Postfix(KeyBinds input, ref bool __result)
        {
            if (VRGameInput.Enabled)
            {
                __result = VRGameInput.MergeButton(input, __result);
            }
        }
    }

    [HarmonyPatch(typeof(SteelInput), nameof(SteelInput.GetButtonDown))]
    internal static class SteelInputGetButtonDownPatch
    {
        private static void Postfix(KeyBinds input, ref bool __result)
        {
            if (VRGameInput.Enabled)
            {
                __result = VRGameInput.MergeButtonDown(input, __result);
            }
        }
    }

    [HarmonyPatch(typeof(SteelInput), nameof(SteelInput.GetButtonUp))]
    internal static class SteelInputGetButtonUpPatch
    {
        private static void Postfix(KeyBinds input, ref bool __result)
        {
            if (VRGameInput.Enabled)
            {
                __result = VRGameInput.MergeButtonUp(input, __result);
            }
        }
    }

    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.Crouch))]
    internal static class CrouchPatch
    {
        private static void Postfix(FpsActorController __instance, ref bool __result)
        {
            if (VRGameInput.Enabled && __instance.allowChangeStance && !VRGameInput.ProneToggled)
            {
                __result |= VRGameInput.CrouchToggled || VRGameInput.PhysicalCrouch;
            }
        }
    }

    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.Prone))]
    internal static class PronePatch
    {
        private static void Postfix(FpsActorController __instance, ref bool __result)
        {
            if (VRGameInput.Enabled && __instance.allowChangeStance)
            {
                __result |= VRGameInput.ProneToggled;
            }
        }
    }

    [HarmonyPatch(typeof(FpsActorController), nameof(FpsActorController.HoldingSprint))]
    internal static class HoldingSprintPatch
    {
        private static void Postfix(FpsActorController __instance, ref bool __result)
        {
            if (VRGameInput.Enabled && __instance.allowSprint)
            {
                __result |= VRGameInput.SprintActive;
            }
        }
    }
}
