using System;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// The game's own player on the first keyboard and mouse, read apart through Raw Input, while a splitscreen player plays with a second
    /// keyboard and mouse: Windows then keeps the merged keyboard and mouse from the game (<see cref="RawInput.BlockLegacy"/>), so the
    /// game's binds are answered from here (merged into SteelInput's reads as the headset's controllers are, GameInputBridge).
    /// </summary>
    internal static class FlatPlayerInput
    {
        public static bool Active;

        /// <summary>The controller the game's own player plays with while the merged ones are muted (-1: none), splitscreen.</summary>
        public static int Pad = -1;

        private static readonly int Count = Enum.GetValues(typeof(SteelInput.KeyBinds)).Length + 1;
        private static readonly float[] Axis = new float[Count];
        private static readonly bool[] Held = new bool[Count];
        private static readonly bool[] WasHeld = new bool[Count];
        private static int _wheel;

        /// <summary>How far the view turns for a count of the mouse (as the game's own mouse look feels).</summary>
        public static float MouseScale = 0.06f;

        public static void Compute()
        {
            Array.Copy(Held, WasHeld, Count);
            Array.Clear(Axis, 0, Count);
            Array.Clear(Held, 0, Count);
            RawDevice k = RawInput.Nth(false, 0), m = RawInput.Nth(true, 0);
            bool Key(int vk) => k != null && k.Keys[vk];
            float Pair(int plus, int minus) => (Key(plus) ? 1f : 0f) - (Key(minus) ? 1f : 0f);
            void Set(SteelInput.KeyBinds b, bool on) => Held[(int)b] |= on;
            void SetAxis(SteelInput.KeyBinds b, float v) => Axis[(int)b] = v;

            SetAxis(SteelInput.KeyBinds.Horizontal, Pair(0x44, 0x41)); // D, A
            SetAxis(SteelInput.KeyBinds.Vertical, Pair(0x57, 0x53)); // W, S
            SetAxis(SteelInput.KeyBinds.CarSteer, Pair(0x44, 0x41));
            SetAxis(SteelInput.KeyBinds.CarThrottle, Pair(0x57, 0x53));
            SetAxis(SteelInput.KeyBinds.HeliPitch, Pair(0x57, 0x53));
            SetAxis(SteelInput.KeyBinds.HeliRoll, Pair(0x44, 0x41));
            SetAxis(SteelInput.KeyBinds.HeliYaw, Pair(0x45, 0x51)); // E, Q
            SetAxis(SteelInput.KeyBinds.HeliThrottle, Pair(0x10, 0x11)); // Shift, Ctrl
            SetAxis(SteelInput.KeyBinds.PlanePitch, Pair(0x57, 0x53));
            SetAxis(SteelInput.KeyBinds.PlaneRoll, Pair(0x44, 0x41));
            SetAxis(SteelInput.KeyBinds.PlaneYaw, Pair(0x45, 0x51));
            SetAxis(SteelInput.KeyBinds.PlaneThrottle, Pair(0x10, 0x11));
            if (m != null)
            {
                Vector2 d = m.ConsumeDelta();
                SetAxis(SteelInput.KeyBinds.AimX, d.x * MouseScale);
                SetAxis(SteelInput.KeyBinds.AimY, -d.y * MouseScale);
                Set(SteelInput.KeyBinds.Fire, m.Left);
                Set(SteelInput.KeyBinds.Aim, m.Right);
                int wheel = m.Wheel - _wheel;
                _wheel = m.Wheel;
                Set(SteelInput.KeyBinds.NextWeapon, wheel < 0);
                Set(SteelInput.KeyBinds.PreviousWeapon, wheel > 0);
            }
            Set(SteelInput.KeyBinds.Reload, Key(0x52)); // R
            Set(SteelInput.KeyBinds.Use, Key(0x45)); // E
            Set(SteelInput.KeyBinds.Crouch, Key(0x43) || Key(0x11)); // C, Ctrl
            Set(SteelInput.KeyBinds.Sprint, Key(0x10)); // Shift
            Set(SteelInput.KeyBinds.Jump, Key(0x20)); // Space
            Set(SteelInput.KeyBinds.Kick, Key(0x46)); // F
            Set(SteelInput.KeyBinds.Map, Key(0x4D)); // M
            Set(SteelInput.KeyBinds.Scoreboard, Key(0x09)); // Tab
            Set(SteelInput.KeyBinds.Prone, Key(0x5A)); // Z
            Set(SteelInput.KeyBinds.TogglePauseMenu, Key(0x1B)); // Esc
            Set(SteelInput.KeyBinds.NextWeapon, Key(0x51)); // Q
            Set(SteelInput.KeyBinds.Weapon1, Key(0x31));
            Set(SteelInput.KeyBinds.Weapon2, Key(0x32));
            Set(SteelInput.KeyBinds.Weapon3, Key(0x33));
            Set(SteelInput.KeyBinds.Weapon4, Key(0x34));
            Set(SteelInput.KeyBinds.Weapon5, Key(0x35));
            Set(SteelInput.KeyBinds.OpenLoadout, Key(0x4C)); // L
            Set(SteelInput.KeyBinds.FireMode, Key(0x42)); // B
            if (Pad >= 0)
            {
                ReadPad(XInput.Read(Pad));
            }
        }

        /// <summary>The game's own player's controller, as the game's own controller layout has it.</summary>
        private static void ReadPad(PadState s)
        {
            if (!s.Connected)
            {
                return;
            }
            void Set(SteelInput.KeyBinds b, bool on) => Held[(int)b] |= on;
            void Stick(SteelInput.KeyBinds b, float v)
            {
                if (Mathf.Abs(v) > Mathf.Abs(Axis[(int)b]))
                {
                    Axis[(int)b] = v;
                }
            }
            Vector2 l = s.LeftStick, r = s.RightStick;
            Stick(SteelInput.KeyBinds.Horizontal, l.x);
            Stick(SteelInput.KeyBinds.Vertical, l.y);
            Stick(SteelInput.KeyBinds.CarSteer, l.x);
            Stick(SteelInput.KeyBinds.CarThrottle, s.RightTrigger - s.LeftTrigger);
            Stick(SteelInput.KeyBinds.HeliRoll, l.x);
            Stick(SteelInput.KeyBinds.HeliPitch, l.y);
            Stick(SteelInput.KeyBinds.HeliYaw, r.x);
            Stick(SteelInput.KeyBinds.HeliThrottle, r.y);
            Stick(SteelInput.KeyBinds.PlaneRoll, l.x);
            Stick(SteelInput.KeyBinds.PlanePitch, l.y);
            Stick(SteelInput.KeyBinds.PlaneYaw, r.x);
            Stick(SteelInput.KeyBinds.PlaneThrottle, s.RightTrigger - s.LeftTrigger);
            // The view turns as the mouse turns it: about 180 degrees a second at full tilt.
            float turn = PadTurn * Time.unscaledDeltaTime;
            Stick(SteelInput.KeyBinds.AimX, r.x * turn);
            Stick(SteelInput.KeyBinds.AimY, r.y * turn);
            Set(SteelInput.KeyBinds.Fire, s.RightTrigger > 0.4f);
            Set(SteelInput.KeyBinds.Aim, s.LeftTrigger > 0.4f);
            Set(SteelInput.KeyBinds.Jump, s.Has(PadState.A));
            Set(SteelInput.KeyBinds.Crouch, s.Has(PadState.B));
            Set(SteelInput.KeyBinds.Reload, s.Has(PadState.X));
            Set(SteelInput.KeyBinds.NextWeapon, s.Has(PadState.Y));
            Set(SteelInput.KeyBinds.Sprint, s.Has(PadState.LeftThumb));
            Set(SteelInput.KeyBinds.Kick, s.Has(PadState.RightThumb));
            Set(SteelInput.KeyBinds.Use, s.Has(PadState.RightShoulder));
            Set(SteelInput.KeyBinds.FireMode, s.Has(PadState.LeftShoulder));
            Set(SteelInput.KeyBinds.TogglePauseMenu, s.Has(PadState.Start));
            Set(SteelInput.KeyBinds.Scoreboard, s.Has(PadState.Back));
            Set(SteelInput.KeyBinds.Map, s.Has(PadState.Up));
            Set(SteelInput.KeyBinds.OpenLoadout, s.Has(PadState.Down));
        }

        /// <summary>The view's turn for a full right stick, in the game's aim units a second.</summary>
        public static float PadTurn = 45f; // (the game turns about 4 degrees per unit at its middle sensitivity: 180 degrees a second)

        public static float MergeAxis(SteelInput.KeyBinds b, float game) =>
            Mathf.Abs(Axis[(int)b]) > Mathf.Abs(game) ? Axis[(int)b] : game;

        public static bool MergeButton(SteelInput.KeyBinds b, bool game) => game || Held[(int)b];

        public static bool MergeButtonDown(SteelInput.KeyBinds b, bool game) => game || Held[(int)b] && !WasHeld[(int)b];

        public static bool MergeButtonUp(SteelInput.KeyBinds b, bool game) => game || !Held[(int)b] && WasHeld[(int)b];
    }
}
