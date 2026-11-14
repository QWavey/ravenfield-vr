using System;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// The game's own binds (SteelInput.KeyBinds) answered for this game's player from one keyboard and mouse and/or one controller,
    /// merged into SteelInput's reads as the headset's controllers are (GameInputBridge). Used when the players at this PC each have their
    /// own devices: the soldiers splitscreen (the game's own player on the first keyboard and mouse, read apart through Raw Input) and the
    /// game copies splitscreen (<see cref="SplitGames"/>: the host reads every keyboard and mouse and sends each copy its player's binds,
    /// <see cref="Receive"/>).
    /// </summary>
    internal static class FlatPlayerInput
    {
        /// <summary>This game's player plays from its own devices (the game's own merged keyboard and mouse kept from it meanwhile).</summary>
        public static bool Active
        {
            get => _active;
            set
            {
                if (value && !_active)
                {
                    ResetMouse(); // (back from a menu: the mouse's moves in it do not land as one turn)
                }
                _active = value;
            }
        }

        private static bool _active;

        /// <summary>Try mode: the game's own player gets only Esc (the pause menu) from the first keyboard; the rest is player 2's.</summary>
        public static bool OnlyPause;

        /// <summary>The controller this game's player plays with while the merged ones are muted (-1: none).</summary>
        public static int Pad = -1;

        /// <summary>The keyboard and mouse (in Raw Input's order) this game's player has (-1: none: a copy fed by the host).</summary>
        public static int Keys = 0;

        internal static readonly int Count = Enum.GetValues(typeof(SteelInput.KeyBinds)).Length + 1;
        private static readonly float[] Axis = new float[Count];
        private static readonly bool[] Held = new bool[Count];
        private static readonly bool[] WasHeld = new bool[Count];

        /// <summary>How far the view turns for a count of the mouse (as the game's own mouse look feels).</summary>
        public static float MouseScale = 0.06f;

        /// <summary>The view's turn for a full right stick, in the game's aim units a second (about 4 degrees a unit: 180 degrees a second).</summary>
        public static float PadTurn = 45f;

        // ---- the binds of one keyboard and mouse, as the host computes them for itself and for each copy ------------------------------

        /// <summary>One player's binds from a keyboard and mouse: held buttons and axes; the mouse's look as running totals (see Compute).</summary>
        internal sealed class Binds
        {
            public readonly float[] Axis = new float[Count];
            public readonly bool[] Held = new bool[Count];
            public float MouseX, MouseY; // (running totals of the mouse's counts: a copy turns by the change since it last read)
            public int Wheel;
        }

        /// <summary>A keyboard and mouse's binds (WASD, the mouse, R, E, C, Shift, Space, F, M, Tab, Z, Esc, Q, 1-5, L, B).</summary>
        internal static void FromKeys(Binds b, RawDevice k, RawDevice m, bool onlyPause)
        {
            Array.Clear(b.Axis, 0, Count);
            Array.Clear(b.Held, 0, Count);
            bool Key(int vk) => k != null && k.Keys[vk];
            float Pair(int plus, int minus) => (Key(plus) ? 1f : 0f) - (Key(minus) ? 1f : 0f);
            void Set(SteelInput.KeyBinds bind, bool on) => b.Held[(int)bind] |= on;
            void SetAxis(SteelInput.KeyBinds bind, float v) => b.Axis[(int)bind] = v;
            if (m != null)
            {
                b.MouseX = m.TotalX;
                b.MouseY = m.TotalY;
                b.Wheel = m.Wheel;
            }
            if (onlyPause)
            {
                Set(SteelInput.KeyBinds.TogglePauseMenu, Key(0x1B));
                return;
            }
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
                Set(SteelInput.KeyBinds.Fire, m.Left);
                Set(SteelInput.KeyBinds.Aim, m.Right);
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
        }

        // ---- this game's player ---------------------------------------------------------------------------------------------------

        private static readonly Binds Own = new Binds();
        private static Binds _received;
        private static float _lastMouseX, _lastMouseY;
        private static int _lastWheel;
        private static bool _mouseKnown;

        /// <summary>A copy's binds from the host (SplitGames): this game's player plays from them from now on.</summary>
        internal static void Receive(Binds b) => _received = b;

        /// <summary>Once a frame before the game reads its binds (GameInputBridge): this frame's binds from the player's devices.</summary>
        public static void Compute()
        {
            Array.Copy(Held, WasHeld, Count);
            Binds b;
            if (Keys >= 0)
            {
                FromKeys(Own, RawInput.Nth(false, Keys), RawInput.Nth(true, Keys), OnlyPause);
                b = Own;
            }
            else
            {
                b = _received;
            }
            Array.Clear(Axis, 0, Count);
            Array.Clear(Held, 0, Count);
            if (b != null)
            {
                Array.Copy(b.Axis, Axis, Count);
                Array.Copy(b.Held, Held, Count);
                // The mouse's turn: the change in its running total since the last frame (a lost or repeated packet loses or doubles nothing).
                if (_mouseKnown)
                {
                    Axis[(int)SteelInput.KeyBinds.AimX] = (b.MouseX - _lastMouseX) * MouseScale;
                    Axis[(int)SteelInput.KeyBinds.AimY] = -(b.MouseY - _lastMouseY) * MouseScale;
                    int wheel = b.Wheel - _lastWheel;
                    Held[(int)SteelInput.KeyBinds.NextWeapon] |= wheel < 0;
                    Held[(int)SteelInput.KeyBinds.PreviousWeapon] |= wheel > 0;
                }
                _lastMouseX = b.MouseX;
                _lastMouseY = b.MouseY;
                _lastWheel = b.Wheel;
                _mouseKnown = true;
            }
            if (Pad >= 0 && !OnlyPause)
            {
                ReadPad(XInput.Read(Pad));
            }
        }

        /// <summary>Forgets the mouse's last totals (a new keyboard and mouse, or a new host): the first frame after turns nothing.</summary>
        public static void ResetMouse() => _mouseKnown = false;

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

        /// <summary>A bind held this frame by this player (for the copies' deploy by key).</summary>
        internal static bool IsHeld(SteelInput.KeyBinds b) => Held[(int)b];

        internal static bool WentDown(SteelInput.KeyBinds b) => Held[(int)b] && !WasHeld[(int)b];

        public static float MergeAxis(SteelInput.KeyBinds b, float game) =>
            Mathf.Abs(Axis[(int)b]) > Mathf.Abs(game) ? Axis[(int)b] : game;

        public static bool MergeButton(SteelInput.KeyBinds b, bool game) => game || Held[(int)b];

        public static bool MergeButtonDown(SteelInput.KeyBinds b, bool game) => game || Held[(int)b] && !WasHeld[(int)b];

        public static bool MergeButtonUp(SteelInput.KeyBinds b, bool game) => game || !Held[(int)b] && WasHeld[(int)b];
    }
}
