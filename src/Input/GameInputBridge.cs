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

        /// <summary>
        /// The gun's own buttons (fire mode, sight, assisted reload) stay off: the game's cursor is out, or the laser is on one of
        /// the mod's panels. The panels never free the cursor, so GameMenuOpen alone let those presses through (bug hunt 2026-10-19).
        /// Also while the match is paused with no menu (the SteamVR dashboard, the headset taken off): a grenade, the knife, a gun
        /// swung or the free hand's gun still acted then (throw hunt, 2026-10-21).
        /// </summary>
        public static bool GunInputBlocked => GameContext.GameMenuOpen || GameManager.IsPaused() || (PanelOpen && PointerOverUI);

        /// <summary>Weapon systems set this while the dominant trigger must not fire (e.g. holding a grenade pin).</summary>
        public static bool SuppressFire { get; set; }

        /// <summary>
        /// The support hand's trigger fires the weapon's active sub weapon (an under-barrel launcher switched to while that
        /// trigger is held; ReloadInteractions). Set every frame it applies.
        /// </summary>
        public static bool SupportFire { get; set; }

        /// <summary>
        /// The support hand holds a gun whose parts its trigger works (a pump, an under-barrel launcher, lights and lasers):
        /// that trigger is not the game's Use meanwhile (it entered vehicles). Set every frame it applies.
        /// </summary>
        public static bool SupportTriggerOwned { get; set; }

        /// <summary>
        /// <see cref="SupportTriggerOwned"/> only for a light's or a laser's switch (a press steps it), not to work the gun: held
        /// for a second, that trigger still leaves a seat. With a lit rifle's handguard held in a car, the player could not get
        /// out with either trigger (vehicles hunt, 2026-10-20). Set every frame it applies.
        /// </summary>
        public static bool SupportTriggerLightsOnly { get; set; }

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

        private static bool _squadWasHeld, _squadFromPanel;

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
            MoveStick = scrollingPanel ? Vector2.zero : ApplyDeadzone(VRInput.MoveOrSimulated, dz);
            TurnStickRaw = scrollingPanel ? Vector2.zero : VRInput.TurnStick;

            // The hand holding the weapon fires it; the other uses (with the weapon passed to the other hand, VRInput has the
            // two hands' buttons trade places).
            Hand dom = VRInput.GunHand;
            Hand off = VRInput.FreeHand;
            // A squad hold begun with the laser on one of the mod's panels never orders (the game commits it on the release); one begun
            // off a panel finishes as it began if the laser comes onto a panel meanwhile. Tracked before the returns below: its edge
            // is every frame's (round-7 scout).
            bool squadHeld = VRInput.GetHeld(DigitalAction.Squad);
            if (!squadHeld)
            {
                _squadFromPanel = false;
            }
            else if (!_squadWasHeld)
            {
                _squadFromPanel = PanelOpen && PointerOverUI;
            }
            _squadWasHeld = squadHeld;
            // (The match paused behind the SteamVR dashboard or with the headset off counts too: a stick flick there toggled crouch
            // or prone for when it resumed; rig hunt, 2026-10-20 night.)
            bool menu = GameContext.GameMenuOpen || !GameContext.InGame || GameManager.instance != null && GameManager.IsPaused();

            // Always available: menus and respawn. The double press is read on every frame: behind the Pause test it was skipped
            // on a frame that also had a Pause press, and that lower press was lost from the count (Tasks 160).
            bool lowerDouble = LowerButtonDoublePressed();
            if (lowerDouble)
            {
                _squadFromPanel = true; // (the second press opens the menu: its hold is no squad order too, until it is let go; input scout)
            }
            // The long and double outputs of a button stay where its binding is when the gun changes hands (Slot moves the single
            // only): with the gun in the other hand, the magazine release's long press paused the match and its double opened the
            // loadout. Not then (input scout, 2026-11-03; the menus stay on the other button's double press).
            bool swapped = VRInput.GunHandSwapped;
            Set(KeyBinds.TogglePauseMenu, VRInput.GetPressed(DigitalAction.Pause) && !swapped || lowerDouble);
            Set(KeyBinds.OpenLoadout, VRInput.GetPressed(DigitalAction.Loadout) && !swapped);
            Set(KeyBinds.SquadLeaderKit, VRInput.GetPressed(DigitalAction.Map));
            // In VR the scoreboard only ever shows together with the map (the wrist look): on its own it covered the
            // view while aiming. The scoreboard button chooses whether it comes up with the map. The Vive wands have no scoreboard
            // button (bindings_vive_controller.json): there it stays with the map, the default.
            if (VRInput.GetPressed(DigitalAction.Scoreboard))
            {
                _scoreboardWithMap = !_scoreboardWithMap;
            }
            // Held as the peek (the game's own SteamVR binding does the same): the Scoreboard key opens it focused, which frees
            // the cursor, which counts as a menu here, which let the key go, which closed it: the whole wrist view (map,
            // scoreboard, match bar, wrist display) came and went every other frame.
            // Not in a seat (a car, a tank, a helicopter): the hands on the wheel or the stick turn the wrist and the scoreboard came up
            // over the road (the user, 2026-11-11: "when I turn my hand in a car a scoreboard shows ... can we remove that in cars").
            bool seated = LocalPlayer.actor != null && LocalPlayer.actor.IsSeated();
            Set(KeyBinds.PeekScoreboard, _scoreboardWithMap && !menu && !PanelOpen && WristLookGesture && GameContext.InGame && !seated);
            // Clicking a menu or one of the mod's panels with the laser must not also pull the gun's trigger.
            // (Not while the match is paused with no menu either: the game's own fire check looks at the cursor, not the pause, and a
            // trigger held into the dashboard fired; vehicles hunt, 2026-10-21.)
            // A trigger already held in the hand that just became the gun hand (the gun passed to it, or drawn into it) fires only
            // after it is let go and pulled again: it fired at once, a shot nobody pulled for (input hunt, 2026-10-21).
            if (dom != _fireHand)
            {
                _fireHand = dom;
                _fireHeldThroughSwap = VRInput.GetHeld(DigitalAction.Fire, dom);
                // And the old gun hand's trigger, still pulled, is the free hand's Use now: not a Use press until let go (the gun
                // passed mid-burst set the game's use ray going; input scout, 2026-11-03).
                _useHeldThroughSwap = VRInput.GetHeld(DigitalAction.Use, off);
            }
            else if (!VRInput.GetHeld(DigitalAction.Fire, dom))
            {
                _fireHeldThroughSwap = false;
            }
            else if (GameManager.IsPaused() || (menu || PanelOpen) && PointerOverUI)
            {
                // Held through a pause (the dashboard, or the very press on Resume that closes the menu): let go first, or the match came
                // back with a shot nobody pulled for (input scout, 2026-11-04). The same for a trigger held on a panel: the laser swept off it,
                // or the panel closed by the other hand, fired the gun at whatever the laser was on (UI scout, 2026-11-07).
                _fireHeldThroughSwap = true;
            }
            if (!VRInput.GetHeld(DigitalAction.Use, off))
            {
                _useHeldThroughSwap = false;
            }
            Set(KeyBinds.Fire, (VRInput.GetHeld(DigitalAction.Fire, dom) && !_fireHeldThroughSwap || SupportFire) && !SuppressFire
                               && !((menu || PanelOpen) && PointerOverUI) && !GameManager.IsPaused());
            // Slow motion in a menu and while dead too, as the flat game's key works there: set after the returns below, it could
            // not be switched in the loadout menu or after dying (user, 2026-10-19 night, task 313). (A toggle in the game: one
            // press on, one off.)
            // On Touch it is A pressed twice (user, 2026-10-20: "double a pressed for on and off"; B held brought up Steam). There
            // SlowMotion is A itself: the second press within 0.4 s toggles it. Counted here, not by SteamVR's double click, which
            // held every single A press (the slide release) back until its double-click time ran out.
            bool slowMotion;
            if (VRInput.AnyOculusTouch)
            {
                slowMotion = false;
                if (VRInput.GetPressed(DigitalAction.SlowMotion))
                {
                    float now = Time.unscaledTime;
                    slowMotion = now - _slowFirstPress <= 0.4f;
                    _slowFirstPress = slowMotion ? -10f : now;
                    if (slowMotion)
                    {
                        // The two presses were slide-release presses too (A's complex button), each a fire-mode press on an
                        // unlocked gun: the switch the first one made at once is put back, and the second's (its single may
                        // come on the release, after this) is not taken (SlowToggledAt).
                        _slowToggledAt = now;
                        Weapon quick = _quickSwitchWeapon; // read before CancelModePress, which clears it
                        CancelModePress();
                        _quickSwitchWeapon = quick;
                        if (_quickSwitchWeapon != null && now - _quickSwitchAt < 0.6f)
                        {
                            UndoQuickSwitch(_quickSwitchWeapon);
                        }
                        _quickSwitchWeapon = null;
                    }
                }
            }
            else
            {
                slowMotion = VRInput.GetPressed(DigitalAction.SlowMotion);
            }
            // Not while the match is paused: the game's key sets the time scale to 0.2 or 1 with no pause check of its own, so a
            // press with the headset off (no menu open) ran the match on (lifecycle hunt, 2026-10-21).
            Set(KeyBinds.Slowmotion, slowMotion && !GameManager.IsPaused());

            if (menu)
            {
                _leaveHeldSince = -1f; // the hold to leave a seat starts again after the menu, not counting the time in it
                _leaveArmed = false;
                // A stick held down into the menu and let go there: on resume its hold (timed in unscaled time, the menu's
                // too) toggled prone with no press (rig hunt, 2026-10-20 night). That hold toggles nothing now.
                _proneTriggeredThisHold |= _downLatch;
                CancelModePress(); // a fire-mode press made before the menu does not switch the mode once it has closed
                SprintActive = false; // (a toggled sprint held Shift for mod scripts through the menu; input hunt, 2026-10-21)
                return;
            }

            Actor actor = GameContext.PlayerActor;
            if (actor == null || actor.dead)
            {
                _leaveHeldSince = -1f;
                _leaveArmed = false;
                CancelModePress();
                SprintActive = false; // the next life does not start sprinting
                return;
            }

            // Not while a panel of the mod is under the laser: that trigger clicks the panel (and entered or left vehicles).
            bool onPanel = PanelOpen && PointerOverUI;
            if (onPanel || !actor.IsSeated())
            {
                _leaveHeldSince = -1f;
                _leaveArmed = false; // a seat taken with the trigger still held: let go once before it counts
            }
            Set(KeyBinds.Use, !onPanel && (actor.IsSeated() ? HeldToLeaveSeat(actor) : VRInput.GetHeld(DigitalAction.Use, off) && !SupportTriggerOwned && !_useHeldThroughSwap));
            // (Next weapon, squad and countermeasures with the laser on a panel: its stick or trackpad click switched the weapon behind
            // it, started an order or fired the countermeasures; round-7 scout.)
            Set(KeyBinds.Call, squadHeld && !_squadFromPanel);
            Set(KeyBinds.NextWeapon, !onPanel && VRInput.GetPressed(DigitalAction.WeaponNext));
            Set(KeyBinds.Countermeasures, !onPanel && VRInput.GetPressed(DigitalAction.Countermeasures));
            Set(KeyBinds.Goggles, goggles);
            Set(KeyBinds.Map, WristLookGesture && !PanelOpen); // (nor under the mod's panel, as Use, Call and Kick)

            if (actor.IsSeated())
            {
                ComputeSeated();
                return;
            }
            ComputeOnFoot(actor);
        }

        private static readonly float[] LowerPressedAt = { -1f, -1f };

        /// <summary>The gun hand the trigger was last read for, and whether its trigger was already held when it became the gun hand.</summary>
        private static Hand _fireHand = Hand.Right;
        private static bool _fireHeldThroughSwap;
        /// <summary>The free hand's trigger was already held when the gun left that hand (it was the gun's trigger): no Use until let go.</summary>
        private static bool _useHeldThroughSwap;

        /// <summary>When the gun hand's A was pressed for a fire-mode switch still waiting out the double press (-1: none).</summary>
        private static float _modePressAt = -1f;
        /// <summary>The trigger was held while that press waited: it flips a sight at most, the fire mode stays (Task 265).</summary>
        private static bool _modePressFiring;
        /// <summary>The weapon the waiting press was made on (a press waiting on another weapon never switches the new one).</summary>
        private static Weapon _modePressWeapon;
        private const float DoublePressWindow = 0.4f;
        /// <summary>The semi / auto switch a Touch A tap made at once, put back if the tap turns out to start slow motion's double press.</summary>
        private static Weapon _quickSwitchWeapon;
        /// <summary>When the gun hand's A switched the fire mode on its press (-1: no push waiting for its single or long press).</summary>
        private static float _pushSwitchedAt = -1f;
        private static Weapon _pushWeapon;
        private static float _quickSwitchAt = -10f;
        private static bool _undoCyclePending;

        /// <summary>Drops a fire-mode press still waiting out its double-press window: the weapon or context it was made in has gone.</summary>
        private static void CancelModePress()
        {
            _modePressAt = -1f;
            _modePressFiring = false;
            _modePressWeapon = null;
            _quickSwitchWeapon = null; // (nor is a switch made at once put back after a menu)
            _undoCyclePending = false;
        }

        /// <summary>
        /// The press's fire-mode switch can be put back by making it once more: the mod's semi / auto selector, or the game's cycle
        /// of a gun with exactly one variant beside it. Not a weapon script's key nor a longer cycle.
        /// </summary>
        private static bool SwitchUndoable(Weapon weapon)
        {
            if (weapon == null)
            {
                return false;
            }
            if (ScriptKeys.Find(weapon, ScriptKeyPurpose.FireMode) != null)
            {
                // A weapon's own fire-mode script: a second tap puts back a two-mode one, the usual semi / auto toggle (the usm_m4
                // M4A1s' fireMode lists "AUTO SEMI", the APC45's and MP9's FRfiremode flips isAuto). Waited out before, the M4A1s'
                // switch lagged by A's release and the double-press wait (user, 2026-10-21 night: "on some guns ... a delay").
                return ScriptModeCount(weapon) <= 2;
            }
            if (weapon.HasAnySubWeapons())
            {
                return weapon.alternativeWeapons != null && weapon.alternativeWeapons.Count == 1;
            }
            return VRConfig.FireSelector.Value && Weapons.FireSelector.HasModes(weapon);
        }

        /// <summary>The press has fire modes to switch: the weapon's own fire-mode script, the game's variants, or the semi / auto selector.</summary>
        private static bool HasFireModes(Weapon weapon) =>
            weapon != null && (ScriptKeys.Find(weapon, ScriptKeyPurpose.FireMode) != null || weapon.HasAnySubWeapons()
                               || VRConfig.FireSelector.Value && Weapons.FireSelector.HasModes(weapon));

        /// <summary>
        /// The modes a weapon's fire-mode script cycles: the common fireMode script's list (its DataContainer's FIREMODE_MODES,
        /// "AUTO SEMI"), else two (a script with no list is taken for the usual semi / auto toggle).
        /// </summary>
        private static int ScriptModeCount(Weapon weapon)
        {
            var data = weapon.GetComponent<Ravenfield.Mods.Data.DataContainer>();
            if (data != null && data.HasString("FIREMODE_MODES"))
            {
                string modes = data.GetString("FIREMODE_MODES");
                return modes == null ? 2 : modes.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            }
            return 2;
        }

        /// <summary>Puts back a switch made at once (by slow motion's double press, or a press that turned out to be a hold for the sight).</summary>
        private static void UndoQuickSwitch(Weapon weapon)
        {
            if (weapon == null)
            {
                return;
            }
            ScriptKey script = ScriptKeys.Find(weapon, ScriptKeyPurpose.FireMode);
            if (script != null)
            {
                ScriptKeys.Tap(script.Key, script.Script); // the script's two-mode toggle once more
            }
            else if (weapon.HasAnySubWeapons())
            {
                _undoCyclePending = true; // the game's key once more: back to the variant it was on
            }
            else
            {
                Weapons.WeaponSystem.Instance?.UndoSelectorToggle(weapon);
            }
            _quickSwitchWeapon = null;
        }

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
                // On Touch the slow-motion button is that lower button (A, or X left-handed: the bindings mirror), and its double
                // press is the slow motion: counted here too, it opened the pause menu with every switch (input hunt, 2026-10-20).
                // By the hand, not the frame: the slide release is A's complex single, which may fire on the release, a frame
                // after the slow motion's press. Pause stays on Y held, and the other hand's lower button still opens it.
                if (VRInput.AnyOculusTouch && hand == VRInput.Dominant)
                {
                    LowerPressedAt[h] = -1f;
                    continue;
                }
                float now = Time.unscaledTime;
                if (LowerPressedAt[h] >= 0f && now - LowerPressedAt[h] < DoublePressWindow)
                {
                    LowerPressedAt[h] = -1f;
                    _modePressAt = -1f; // a double press: the menu, not a fire-mode switch
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
        // Let go at least once since the seat was taken: the trigger that entered the car, still held, counted toward leaving it
        // and threw the player out a second later (vehicles hunt, 2026-10-20).
        private static bool _leaveArmed;
        private static int _leaveTicks;

        /// <summary>A seat this far (metres) to one side of the vehicle's middle belongs to the hand on that side.</summary>
        private const float SeatSideReach = 0.15f;

        /// <summary>
        /// In a seat, the trigger of the hand on the seat's side gets you out, and only when held for a second, with ticks
        /// building up in the hand: a fist closed on the wheel squeezes the trigger too, and a single click threw the driver
        /// out of the car. A driver on the left leaves with the left hand, a passenger on the right with the right, and a seat
        /// in the middle keeps the off hand. A hand on the wheel or the flight stick never counts.
        /// </summary>
        private static bool HeldToLeaveSeat(Actor actor)
        {
            Hand leave = LeaveHand(actor);
            Rig.VRHand hand = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Hand(leave) : null;
            bool held = LeaveTriggerHeld(leave) && (hand == null || hand.Role != Rig.HandRole.Cockpit) && (!SupportTriggerOwned || SupportTriggerLightsOnly);
            if (!held || !_leaveArmed)
            {
                _leaveHeldSince = -1f;
                _leaveArmed |= !LeaveTriggerHeld(leave);
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
                // The trigger that got out may be the gun hand's: still pulled on foot it fired the gun as it came up (vehicles scout, 2026-11-05).
                _fireHeldThroughSwap = true;
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

        /// <summary>
        /// The hand that gets out of this seat: the one on the seat's side of the vehicle, read from where the seat sits across
        /// the vehicle; a seat in the middle keeps the off hand. Use is read from the off hand's trigger (the bindings mirror for
        /// left-handed play, and with the weapon passed over they trade places: VRInput.Slot), and the gun hand's trigger is the
        /// gun's Fire. So the gun hand's trigger leaves a seat only where it fires nothing; otherwise the off hand does it.
        /// </summary>
        private static Hand LeaveHand(Actor actor)
        {
            Hand hand = VRInput.FreeHand;
            Seat seat = actor.seat;
            if (seat != null && seat.vehicle != null)
            {
                float side = seat.vehicle.transform.InverseTransformPoint(seat.transform.position).x;
                if (side > SeatSideReach)
                {
                    hand = Hand.Right;
                }
                else if (side < -SeatSideReach)
                {
                    hand = Hand.Left;
                }
            }
            if (hand == VRInput.GunHand && !GunTriggerLeaves(actor))
            {
                hand = VRInput.FreeHand;
            }
            return hand;
        }

        /// <summary>
        /// The gun hand's trigger may leave the seat only where it fires nothing: no weapon in hand, and no gun or horn in the seat
        /// (a car's horn is a mounted weapon too, and the trigger sounds it).
        /// </summary>
        private static bool GunTriggerLeaves(Actor actor) =>
            (actor.seat.weapons == null || actor.seat.weapons.Length == 0) && !(Weapons.DrivingWeapons.Armed || GameContext.HeldPersonalWeapon != null);

        /// <summary>The leaving trigger of this hand: Use for the off hand, Fire for the gun hand (only where <see cref="LeaveHand"/> allows it).</summary>
        // The pulled trigger counts too: on the Vive wands the off hand's trigger is a complex button (a click Use, held Squad), so
        // Use was never held for the second and no seat could be left with it (input hunt, 2026-10-20).
        private static bool LeaveTriggerHeld(Hand leave) =>
            (leave == VRInput.GunHand ? VRInput.GetHeld(DigitalAction.Fire, leave) : VRInput.GetHeld(DigitalAction.Use, leave))
            || VRInput.GetTrigger(leave) > 0.9f;

        /// <summary>The game's seat keys in seat order: Seat1 takes seat 0, and so on (FpsActorController.UpdateInput).</summary>
        private static readonly KeyBinds[] SeatKeys =
        {
            KeyBinds.Seat1, KeyBinds.Seat2, KeyBinds.Seat3, KeyBinds.Seat4, KeyBinds.Seat5, KeyBinds.Seat6, KeyBinds.Seat7
        };

        /// <summary>
        /// The seat after the player's, cycling through the vehicle's seats and skipping any the player cannot take: a seat
        /// switched off, a taken seat when the vehicle does not allow swaps, and a seat past the seventh (no game key for it).
        /// -1: no seat to switch to.
        /// </summary>
        private static int NextSeatIndex(Seat current)
        {
            Vehicle vehicle = current != null ? current.vehicle : null;
            if (vehicle == null || !vehicle.allowPlayerSeatChange || vehicle.seats == null)
            {
                return -1;
            }
            // Locked or another team's (a map's trigger locks a car with the player in it): the game leaves the seat first and then refuses
            // the next one, and the player was in neither.
            Actor sitting = current.occupant;
            if (vehicle.isLocked || sitting != null && vehicle.ownerTeam >= 0 && vehicle.ownerTeam != sitting.team)
            {
                return -1;
            }
            int here = vehicle.seats.IndexOf(current);
            int count = vehicle.seats.Count;
            for (int step = 1; step < count; step++)
            {
                int index = (here + step) % count;
                Seat seat = vehicle.seats[index];
                if (seat == null || !seat.gameObject.activeInHierarchy || seat.IsOccupied() && !vehicle.allowPlayerSeatSwap)
                {
                    continue;
                }
                if (index >= SeatKeys.Length)
                {
                    continue; // no game key for it: the next one (a seat past the seventh ended the cycle there)
                }
                return index;
            }
            return -1;
        }

        private static void ComputeOnFoot(Actor actor)
        {
            // FirstPersonControllerInput feeds -Horizontal as the strafe input, so positive stick X is negative Horizontal.
            SetAxis(KeyBinds.Horizontal, -MoveStick.x);
            SetAxis(KeyBinds.Vertical, MoveStick.y);
            Set(KeyBinds.Kick, !(PanelOpen && PointerOverUI) && VRInput.GetHeld(DigitalAction.Kick)); // (not with the laser on a panel)

            HandleWeaponButtons(actor.activeWeapon);

            UpdateSprint(actor);
            UpdateStickJumpCrouch(actor);
        }

        /// <summary>Fire mode / sights and the assisted reload for the personal weapon in hand.</summary>
        private static void HandleWeaponButtons(Weapon weapon)
        {
            // The menu or one of the mod's panels under the laser takes these buttons: a press made then does nothing to the gun,
            // and a fire-mode press still waiting out its window is dropped rather than switching the mode after the panel closed
            // (bug hunt 2026-10-19: the A press and the assisted reload went through an open panel). Nor knocked down: the game keeps the gun as active with
            // its object off, and the press went to a script that was not listening, buzzed, and was logged as a switch (script keys scout, 2026-11-07).
            Actor down = GameContext.PlayerActor;
            if (GunInputBlocked || down != null && down.fallenOver)
            {
                CancelModePress();
                return;
            }
            // The fire mode does not change while the trigger is held (Task 265; user, 2026-10-18: "some primarys when shot pause
            // shooting ... the user needs to press shot again, so full autoing is sometimes not possible"). A plain press of A, or
            // its long press, made during a burst switched an automatic gun to single shots (or a fire-mode variant to its semi
            // copy), and the game then fired one round per trigger pull (Weapon.CanFire: hasFiredSingleRoundThisTrigger) until the
            // trigger was let go and pressed again. A press made while the trigger is held still flips the sight (it stops no
            // burst); a grenade or melee weapon is not held back: a katana sheathes with its long press.
            bool firing = !(weapon is ThrowableWeapon || weapon is MeleeWeapon)
                          && (VRInput.GetHeld(DigitalAction.Fire, VRInput.GunHand) || SupportFire);
            // A plain press of the slide-release button (A on the gun hand) switches the fire mode too, whenever there is no
            // locked slide for it to release: the long press alone was found by nobody ("no full auto / single switch";
            // user, 2026-10-16).
            // Taken only once the double-press window has passed: a second press opens the pause menu, and the switch it would
            // have made is dropped (two presses switched the mode and opened the menu; 255.B5-1). On Touch the gun hand's A never
            // opens the menu (its double press is slow motion), so there it switches at once: the wait, after A's single that
            // already comes on the release, made every switch to auto or semi lag (user, 2026-10-21: "the switch to full auto or
            // semi has delays"). A second press that turns slow motion on or off puts a semi / auto switch back.
            bool modeNow = false;
            float now = Time.unscaledTime;
            bool longPress = VRInput.GetPressed(DigitalAction.FireMode) && !VRInput.GunHandSwapped; // (the free hand's button after a swap: see the menus)
            // On Touch the gun hand's A reaches the mod twice: its raw press at once (the SlowMotion click), and the slide release's
            // single only when A is let go (A's complex button has its long press bound). A switch a second press can put back is
            // made on the press now: taken on the release it lagged by as long as A was held (user, 2026-10-21 night: "theres still
            // a delay between auto and manual switching"). The single or long press that the same push gives later is not taken
            // again (it would switch straight back).
            bool single = VRInput.GetPressed(DigitalAction.SlideRelease, VRInput.GunHand);
            // While aiming, a tap still switches the fire mode and the button held flips the sight (a magnifier, an alternate sight):
            // the press used to flip the sight whenever the gun was aimed, so the fire mode could not be switched looking through it
            // (user, 2026-10-21 night: "when aiming and pressing the switch ... it wants to switch sights and doesnt do the switch").
            Weapons.WeaponSystem system = Weapons.WeaponSystem.Instance;
            bool aiming = system != null && system.Held != null && system.Held.Aiming;
            // Asked only when a long press needs it: worked out every aimed frame, its two script-key lookups each built a list
            // and walked every key (input hunt, 2026-10-23).
            bool SightToFlip() => aiming && weapon != null && !system.Held.MagnifierByHand(weapon)
                                  && (ScriptKeys.Find(weapon, ScriptKeyPurpose.Magnifier) != null || weapon.HasAnyAltSightModes());
            if (_pushSwitchedAt >= 0f && (now - _pushSwitchedAt >= 3f || single || longPress))
            {
                if (now - _pushSwitchedAt < 3f)
                {
                    if (longPress && _pushWeapon == weapon && SightToFlip())
                    {
                        // Held while aiming: the sight flips, and the switch the press made is put back (the press cannot know yet
                        // whether it is a tap or a hold).
                        UndoQuickSwitch(weapon);
                    }
                    else
                    {
                        longPress = false; // (this push's long press: it already switched)
                    }
                    single = false; // (this push's single: it already switched)
                }
                _pushSwitchedAt = -1f;
                _pushWeapon = null;
            }
            if (VRInput.AnyOculusTouch && VRInput.GunHand == VRInput.Dominant && VRInput.GetPressed(DigitalAction.SlowMotion, VRInput.GunHand))
            {
                _pushSwitchedAt = -1f; // a new push: whatever the last one gave has come (or was lost to a menu)
                _pushWeapon = null;
                if (now - _slowToggledAt >= 1.2f && !firing && SlideReleaseSwitchesMode(weapon) && SwitchUndoable(weapon))
                {
                    modeNow = true;
                    _pushSwitchedAt = now;
                    _pushWeapon = weapon;
                }
            }
            if (!modeNow && single && SlideReleaseSwitchesMode(weapon)
                && !(VRInput.AnyOculusTouch && now - _slowToggledAt < 1.2f)) // (its single comes on the release: a second press held long too)
            {
                // Only a switch a second press can put back exactly (the semi / auto selector, or a gun with one variant beside
                // it): a weapon script's or a longer variant cycle keeps the wait, or slow motion's double press left it switched
                // (review of the day's changes, 2026-10-21).
                if (VRInput.AnyOculusTouch && VRInput.GunHand == VRInput.Dominant && SwitchUndoable(weapon))
                {
                    modeNow = true;
                }
                else
                {
                    _modePressAt = Time.unscaledTime;
                    _modePressFiring = false;
                    _modePressWeapon = weapon;
                }
            }
            // A press waiting out its window on another weapon (drawn, or passed to the other hand meanwhile) is dropped: the switch
            // would land on a weapon the player never pressed for (bug hunt 2026-10-19).
            if (_modePressAt >= 0f && _modePressWeapon != weapon)
            {
                CancelModePress();
            }
            if (firing && _modePressAt >= 0f)
            {
                _modePressFiring = true; // the trigger was pulled while this press waited: it may flip a sight, never the mode
            }
            bool modeByRelease = _modePressAt >= 0f && Time.unscaledTime - _modePressAt >= DoublePressWindow;
            if (modeByRelease)
            {
                _modePressAt = -1f;
                _modePressWeapon = null;
                if (!SlideReleaseSwitchesMode(weapon))
                {
                    // The slide locked meanwhile: the press is dropped here, not passed on (the slide release only sees a press made
                    // in its own frame, and this one was made on an unlocked gun). A new press releases the slide.
                    modeByRelease = false;
                }
            }
            if (_undoCyclePending)
            {
                _undoCyclePending = false;
                Set(KeyBinds.FireMode, true); // the variant switched by the first tap of slow motion's double press, put back
            }
            if (longPress || modeByRelease || modeNow)
            {
                // The fire mode (the weapon's own fire-mode script, the game's variants, else our semi / auto selector), aiming or
                // not. The sight (the magnifier / alternate sight) when the button is held while aiming, or on a gun with no fire
                // modes to switch.
                bool modes = HasFireModes(weapon);
                bool wantSight = aiming ? longPress || !modes : !modes;
                ScriptKey magnifier = wantSight && aiming ? ScriptKeys.Find(weapon, ScriptKeyPurpose.Magnifier) : null;
                if (wantSight && aiming && system.Held.MagnifierByHand(weapon))
                {
                    // The free hand flips this weapon's magnifier (Weapons.MagnifierFlip found its part), so the press does
                    // nothing here: no tap, no sight-mode or fire-mode step. Only a weapon whose magnifier part cannot be found
                    // keeps the button, so the magnifier can always be switched.
                }
                else if (magnifier != null)
                {
                    ScriptKeys.Tap(magnifier.Key, magnifier.Script);
                }
                else if (wantSight && weapon != null && weapon.HasAnyAltSightModes())
                {
                    Set(KeyBinds.NextScope, true);
                }
                else if (!firing && !(modeByRelease && _modePressFiring))
                {
                    bool handled = system != null && system.HandleFireModePress(weapon);
                    if (!handled) // the weapon's own fire-mode key works with the selector off too
                    {
                        Set(KeyBinds.FireMode, true);
                    }
                    if (modeNow && weapon != null && (handled || weapon.HasAnySubWeapons()))
                    {
                        // Made at once (the selector, a two-mode script, or the game's one variant): slow motion's double press
                        // puts it back (UndoQuickSwitch).
                        _quickSwitchWeapon = weapon;
                        _quickSwitchAt = Time.unscaledTime;
                    }
                    // Which way and when, for the next play log (user, 2026-10-21 night: "theres still a delay between auto and
                    // manual switching"): on A's press, its release, after the double-press wait, or A held.
                    string when = modeNow ? (_pushSwitchedAt == now ? "on A's press" : "on A's release") : modeByRelease ? "after the double-press wait" : "A held";
                    string way = !handled ? (weapon != null && weapon.HasAnySubWeapons() ? "the game's variant cycle" : "the game's key")
                                 : system.LastPressWasSelector ? "the selector" : "the weapon's own script key";
                    if (weapon != null) // (with no weapon in hand nothing was switched: the line claimed a switch on '?')
                    {
                        Log.Info($"Fire mode switched on {weapon.name} by {way}, {when}.");
                    }
                }
            }
            if (VRConfig.ReloadNow == ReloadMode.Assisted && VRInput.GetPressed(DigitalAction.MagRelease, VRInput.GunHand))
            {
                Set(KeyBinds.Reload, true);
            }
        }

        /// <summary>
        /// The slide-release press may switch the fire mode: a gun with modes to switch (its own fire-mode script, sub weapons,
        /// or automatic for the selector), not a grenade or a melee weapon (A pulls a pin), and no slide or bolt locked open.
        /// The trigger does not stop the press here: HandleWeaponButtons keeps the mode change out of a burst (Task 265), and a
        /// sight flip still goes through.
        /// </summary>
        private static bool SlideReleaseSwitchesMode(Weapon weapon)
        {
            if (weapon == null || weapon is ThrowableWeapon || weapon is MeleeWeapon)
            {
                return false;
            }
            Weapons.HeldWeapon held = Weapons.WeaponSystem.Instance != null ? Weapons.WeaponSystem.Instance.Held : null;
            if (held == null || held.Weapon != weapon || held.ActiveAmmo != null && (held.ActiveAmmo.SlideLocked || held.ActiveAmmo.BoltOpen))
            {
                return false;
            }
            return ScriptKeys.Find(weapon, ScriptKeyPurpose.FireMode) != null || weapon.HasAnySubWeapons()
                   || VRConfig.FireSelector.Value && Weapons.FireSelector.HasModes(weapon);
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
            // No weapon in hand from the seat: a fire-mode press made on foot does not switch anything here.
            CancelModePress();
            // Vehicle axes are produced by the CarInput/HelicopterInput/AirplaneInput patches; here only buttons. The laser on one
            // of the mod's panels takes them, as it does the gun's (2026-10-19: A fired countermeasures through an open panel).
            if (GunInputBlocked)
            {
                SprintActive = false;
                _jumpLatch = _downLatch = false;
                return;
            }
            // Seat switching: Sprint has no job in a seat (it is read on foot only, in UpdateSprint), so its press moves the
            // player to the next seat. The game's own seat keys take it, swapping with an AI passenger the same way they do.
            // Sprint is the left stick's click on the controllers and the Vive wand's trackpad click.
            // Not with a hand on the wheel or stick: a stray click of the left stick left the seat at speed, the engine off or an AI at the
            // controls (vehicles scout, 2026-11-06). Let go and click.
            Rig.VRRig seatRig = Rig.VRRig.Instance;
            bool steering = seatRig != null && (seatRig.Hands[0].Role == Rig.HandRole.Cockpit || seatRig.Hands[1].Role == Rig.HandRole.Cockpit);
            if (VRInput.GetPressed(DigitalAction.Sprint) && !steering)
            {
                int next = NextSeatIndex(actor.seat);
                if (next >= 0)
                {
                    Set(SeatKeys[next], true);
                }
            }
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
            // The gun fires while running: the game's WantsToFire drops the trigger while sprinting and for 0.2 s after
            // (FpsActorController.sprintCannotFireAction) unless allowFireWhileSprinting is set, which it is here for the VR
            // player, whose gun is held by the hand, not lowered by a sprint pose. Ending the sprint on the trigger (the first
            // try at this) still left it unable to shoot while running (user, 2026-10-19 night and 2026-10-20: "when running I
            // cannot shoot", task 314). The weapon's own tuck pose is kept off in WeaponUpdatePatch.
            if (actor != null && actor.controller is FpsActorController fps && !fps.allowFireWhileSprinting)
            {
                fps.allowFireWhileSprinting = true;
                _sprintFireSetOn = fps;
            }
            if (TestSprint)
            {
                SprintActive = true; // autotest: sprinting with no stick to push (task 314)
                return;
            }
            if (VRConfig.Sprint.Value == SprintMode.Hold)
            {
                SprintActive = VRInput.GetHeld(DigitalAction.Sprint);
                // Sprinting ends a crouch or prone the stick toggled, as the toggle mode does once the run starts: returned before
                // that, held sprint kept the stance and did not run (bridge scout, 2026-10-24).
                if (SprintActive && MoveStick.sqrMagnitude >= 0.04f)
                {
                    CrouchToggled = false;
                    ProneToggled = false;
                }
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

        /// <summary>
        /// <see cref="ApplyDeadzone"/> for an input whose axes each go to 1 on their own (a square gate: the cockpit's flight stick,
        /// each axis its own throw): the same round dead zone and rescale, but not held inside the unit circle, each axis to 1
        /// instead. Held in the circle, the stick pushed fully into a corner gave 0.71 roll and 0.71 pitch, never both at full as
        /// the flat game's keys give (improve round, 2026-10-22).
        /// </summary>
        public static Vector2 ApplyDeadzoneSquare(Vector2 v, float deadzone)
        {
            float m = v.magnitude;
            if (m <= deadzone || m < 1e-4f)
            {
                return Vector2.zero;
            }
            Vector2 r = v / m * ((m - deadzone) / Mathf.Max(1e-4f, 1f - deadzone));
            return new Vector2(Mathf.Clamp(r.x, -1f, 1f), Mathf.Clamp(r.y, -1f, 1f));
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

        private static float _slowFirstPress = -10f;

        /// <summary>When slow motion was last switched by A's double press (Touch): that press's slide release switches no fire mode.</summary>
        private static float _slowToggledAt = -10f;

        /// <summary>Autotest: sprint as if the stick were clicked and pushed.</summary>
        internal static bool TestSprint;

        /// <summary>The player controller whose allowFireWhileSprinting was switched on for VR (switched back off when VR stops).</summary>
        private static FpsActorController _sprintFireSetOn;

        /// <summary>VR is stopping: the game's own switches set for VR go back, so flat play blocks fire while sprinting again.</summary>
        public static void RestoreGameSwitches()
        {
            if (_sprintFireSetOn != null)
            {
                _sprintFireSetOn.allowFireWhileSprinting = false;
            }
            _sprintFireSetOn = null;
        }

        /// <summary>
        /// VR is stopping: this session's toggles, stick latches and pending switches go with it. Kept, a crouch toggled before VR
        /// stopped crouched the soldier the moment VR started again (the "try again" key), and a latch or a quick switch waiting
        /// to be undone carried over (input hunt, 2026-10-23).
        /// </summary>
        public static void ResetSession()
        {
            ResetToggles();
            _jumpLatch = _downLatch = false;
            _modePressAt = -1f;
            _modePressWeapon = null;
            _modePressFiring = false;
            _quickSwitchWeapon = null;
            _pushSwitchedAt = -1f;
            _pushWeapon = null;
            _undoCyclePending = false;
            _leaveHeldSince = -1f;
            _leaveArmed = false;
            _leaveTicks = 0;
            // (And what was held: kept, the next session's first frame sent the game a release for a key let go while VR was down.)
            System.Array.Clear(Held, 0, Held.Length);
            System.Array.Clear(WasHeld, 0, WasHeld.Length);
            _useHeldThroughSwap = false;
        }

        /// <summary>Resets toggles when the player respawns, gets into or out of a seat, or onto or off a ladder.</summary>
        public static void ResetToggles()
        {
            SprintActive = false;
            CrouchToggled = false;
            ProneToggled = false;
            // A stick held down through it is spent: its latch outlived a death, and letting it go after the respawn crouched the
            // fresh soldier (input hunt, 2026-10-20). A new push down starts a new hold.
            _proneTriggeredThisHold = true;
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
