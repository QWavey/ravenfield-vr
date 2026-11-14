using System;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// The game's own binds (SteelInput.KeyBinds) answered for this game's player from ONE keyboard and mouse and/or one controller, merged
    /// into SteelInput's reads as the headset's controllers are (GameInputBridge). Used when the players at this PC each have their own
    /// devices: the soldiers splitscreen and the game copies splitscreen (<see cref="SplitGames"/>).
    /// <para>
    /// The keys are the game's own: every bind is evaluated from the game's own key bindings (Options, Controls: each bind's positive and
    /// negative key, or its mouse axis and whether it is inverted), as SteelInput.Axis.GetValue does, only from this player's keyboard and
    /// mouse instead of the merged ones (the user, 2026-11-14: "these are not the standard controls in ravenfield. please use the standard
    /// ones"; and D walked left, the mouse looked the wrong way: the game's sideways binds are positive to the left, which its own bindings
    /// carry).
    /// </para>
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

        /// <summary>Try mode: the game's own player gets only its pause menu key; the rest of that keyboard is player 2's.</summary>
        public static bool OnlyPause;

        /// <summary>The controller this game's player plays with while the merged ones are muted (-1: none).</summary>
        public static int Pad = -1;

        /// <summary>The keyboard and mouse (in Raw Input's order) this game's player has (-1: none: a copy fed by the host).</summary>
        public static int Keys = 0;

        internal static readonly int Count = Enum.GetValues(typeof(SteelInput.KeyBinds)).Length + 1;

        /// <summary>The mod's own bind past the game's: switch team (T, the controller's D-pad right), for the splitscreen copies.</summary>
        internal static readonly int SwitchTeam = Count - 1;

        private static readonly float[] Axis = new float[Count];
        private static readonly bool[] Held = new bool[Count];
        private static readonly bool[] WasHeld = new bool[Count];

        /// <summary>Unity's "Mouse X/Y" per count of the mouse (its input manager's sensitivity: the game turns it as its own mouse look).</summary>
        public static float MouseAxisPerCount = 0.1f;

        /// <summary>The view's turn for a full right stick, in the game's aim units a second (about 4 degrees a unit: 180 degrees a second).</summary>
        public static float PadTurn = 45f;

        // ---- one keyboard and mouse's state, as the host reads it and sends it to a copy ---------------------------------------------

        /// <summary>One player's keyboard and mouse: keys held (by Windows' virtual key), mouse buttons, the mouse's running totals.</summary>
        internal sealed class DeviceState
        {
            public readonly bool[] Keys = new bool[256];
            public bool Left, Right, Middle, X1, X2;
            public float TotalX, TotalY; // (running totals of the mouse's counts: a copy turns by the change since it last read)
            public int Wheel;

            public void CopyFrom(RawDevice k, RawDevice m)
            {
                if (k != null)
                {
                    Array.Copy(k.Keys, Keys, 256);
                }
                else
                {
                    Array.Clear(Keys, 0, 256);
                }
                if (m != null)
                {
                    Left = m.Left;
                    Right = m.Right;
                    Middle = m.Middle;
                    X1 = m.X1;
                    X2 = m.X2;
                    TotalX = m.TotalX;
                    TotalY = m.TotalY;
                    Wheel = m.Wheel;
                }
                else
                {
                    Left = Right = Middle = X1 = X2 = false;
                }
            }
        }

        // ---- the game's own bindings, evaluated from one keyboard and mouse ------------------------------------------------------------

        /// <summary>Windows' virtual key for one of Unity's key codes (0: none, or a mouse button: see Down).</summary>
        internal static int VirtualKey(KeyCode k)
        {
            int c = (int)k;
            if (c >= (int)KeyCode.A && c <= (int)KeyCode.Z) return 0x41 + c - (int)KeyCode.A;
            if (c >= (int)KeyCode.Alpha0 && c <= (int)KeyCode.Alpha9) return 0x30 + c - (int)KeyCode.Alpha0;
            if (c >= (int)KeyCode.Keypad0 && c <= (int)KeyCode.Keypad9) return 0x60 + c - (int)KeyCode.Keypad0;
            if (c >= (int)KeyCode.F1 && c <= (int)KeyCode.F15) return 0x70 + c - (int)KeyCode.F1;
            switch (k)
            {
                case KeyCode.Space: return 0x20;
                case KeyCode.Return: case KeyCode.KeypadEnter: return 0x0D;
                case KeyCode.Escape: return 0x1B;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Backspace: return 0x08;
                case KeyCode.LeftShift: case KeyCode.RightShift: return 0x10;
                case KeyCode.LeftControl: case KeyCode.RightControl: return 0x11;
                case KeyCode.LeftAlt: case KeyCode.RightAlt: case KeyCode.AltGr: return 0x12;
                case KeyCode.CapsLock: return 0x14;
                case KeyCode.UpArrow: return 0x26;
                case KeyCode.DownArrow: return 0x28;
                case KeyCode.LeftArrow: return 0x25;
                case KeyCode.RightArrow: return 0x27;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.Home: return 0x24;
                case KeyCode.End: return 0x23;
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.BackQuote: return 0xC0;
                case KeyCode.Minus: return 0xBD;
                case KeyCode.Equals: return 0xBB;
                case KeyCode.LeftBracket: return 0xDB;
                case KeyCode.RightBracket: return 0xDD;
                case KeyCode.Backslash: return 0xDC;
                case KeyCode.Semicolon: return 0xBA;
                case KeyCode.Quote: return 0xDE;
                case KeyCode.Comma: return 0xBC;
                case KeyCode.Period: return 0xBE;
                case KeyCode.Slash: return 0xBF;
                case KeyCode.KeypadPlus: return 0x6B;
                case KeyCode.KeypadMinus: return 0x6D;
                case KeyCode.KeypadMultiply: return 0x6A;
                case KeyCode.KeypadDivide: return 0x6F;
                case KeyCode.KeypadPeriod: return 0x6E;
                default: return 0;
            }
        }

        private static bool Down(DeviceState s, KeyCode k)
        {
            switch (k)
            {
                case KeyCode.None: return false;
                case KeyCode.Mouse0: return s.Left;
                case KeyCode.Mouse1: return s.Right;
                case KeyCode.Mouse2: return s.Middle;
                case KeyCode.Mouse3: return s.X1;
                case KeyCode.Mouse4: return s.X2;
            }
            int vk = VirtualKey(k);
            return vk > 0 && s.Keys[vk];
        }

        /// <summary>
        /// One bind's value from one keyboard and mouse, as SteelInput.Axis.GetValueUnity gives it from the merged ones: its positive key
        /// minus its negative key, or its mouse axis (this frame's counts) as Unity's input manager gives it, inverted when the binding is.
        /// </summary>
        private static float Value(SteelInput.Axis a, DeviceState s, float dx, float dy, int wheel)
        {
            if (a.useUnityAxis)
            {
                string name = a.unityAxis;
                // As Unity's input manager gives them: "Mouse X" positive to the right (Windows' counts too), "Mouse Y" positive up (Windows'
                // counts down); the binding's own invert then as the game applies it (its aim binding is "Mouse X, inverted"). The user,
                // 2026-11-14, with a real mouse: "left and right turning is opposite" with the sign turned round here.
                float v = name == "Mouse X" ? dx * MouseAxisPerCount
                    : name == "Mouse Y" ? -dy * MouseAxisPerCount // (Windows counts down as positive, Unity up)
                    : name == "Mouse ScrollWheel" ? (SteelInput.instance != null && !SteelInput.instance.scrollEnabled ? 0f : wheel / 120f * 0.1f) // (the game's own scroll gate: not while scoped or in a menu)
                    : 0f; // (the joystick axes: each copy reads its own controller, ReadPad)
                if (a.invertUnityAxis)
                {
                    v = -v;
                }
                if (a.applyDeadzone && Mathf.Abs(v) <= 0.1f)
                {
                    v = 0f;
                }
                return v;
            }
            return (Down(s, a.posKeyCode) ? 1f : 0f) - (Down(s, a.negKeyCode) ? 1f : 0f);
        }

        /// <summary>A key or mouse button pressed on a state, the one the game's own binding gives a bind (positive or negative side): the check's way to press "move right".</summary>
        internal static void PressBind(DeviceState s, SteelInput.KeyBinds bind, bool positive)
        {
            SteelInput.Axis a = SteelInput.GetInput(bind);
            if (a.useUnityAxis && a.unityAxis == "Mouse ScrollWheel")
            {
                bool up = positive != a.invertUnityAxis;
                s.Wheel += up ? 120 : -120; // (one notch of the wheel, the way that makes the bind positive)
                return;
            }
            KeyCode k = positive ? a.posKeyCode : a.negKeyCode;
            switch (k)
            {
                case KeyCode.Mouse0: s.Left = true; return;
                case KeyCode.Mouse1: s.Right = true; return;
                case KeyCode.Mouse2: s.Middle = true; return;
            }
            int vk = VirtualKey(k);
            if (vk > 0)
            {
                s.Keys[vk] = true;
            }
        }

        /// <summary>The game's own key for a bind, as a name (the copies' "what to press" line).</summary>
        internal static string KeyName(SteelInput.KeyBinds bind)
        {
            try
            {
                SteelInput.Axis a = SteelInput.GetInput(bind);
                KeyCode k = a.posKeyCode != KeyCode.None ? a.posKeyCode : a.negKeyCode;
                return k == KeyCode.Mouse0 ? "Left mouse" : k == KeyCode.Mouse1 ? "Right mouse" : k.ToString();
            }
            catch (Exception)
            {
                return bind.ToString();
            }
        }

        // ---- this game's player ---------------------------------------------------------------------------------------------------

        private static readonly DeviceState Own = new DeviceState();
        private static DeviceState _received;
        private static float _lastMouseX, _lastMouseY;
        private static int _lastWheel;
        private static bool _mouseKnown;

        /// <summary>A copy's keyboard and mouse from the host (SplitGames): this game's player plays from it from now on.</summary>
        internal static void Receive(DeviceState s) => _received = s;

        /// <summary>Once a frame before the game reads its binds (GameInputBridge): this frame's binds from the player's devices.</summary>
        public static void Compute()
        {
            Array.Copy(Held, WasHeld, Count);
            Array.Clear(Axis, 0, Count);
            Array.Clear(Held, 0, Count);
            DeviceState s;
            _current = null;
            if (Keys >= 0)
            {
                Own.CopyFrom(RawInput.Nth(false, Keys), RawInput.Nth(true, Keys));
                s = Own;
            }
            else
            {
                s = _received;
            }
            _current = s;
            if (s != null)
            {
                // The mouse's move: the change in its running total since the last frame (a lost or repeated packet loses or doubles nothing).
                float dx = 0f, dy = 0f;
                int wheel = 0;
                if (_mouseKnown)
                {
                    dx = s.TotalX - _lastMouseX;
                    dy = s.TotalY - _lastMouseY;
                    wheel = s.Wheel - _lastWheel;
                }
                _lastMouseX = s.TotalX;
                _lastMouseY = s.TotalY;
                _lastWheel = s.Wheel;
                _mouseKnown = true;
                int binds = Count - 1;
                for (int i = 0; i < binds; i++)
                {
                    var bind = (SteelInput.KeyBinds)i;
                    if (OnlyPause && bind != SteelInput.KeyBinds.TogglePauseMenu)
                    {
                        continue;
                    }
                    float v;
                    try
                    {
                        v = Value(SteelInput.GetInput(bind), s, dx, dy, wheel);
                    }
                    catch (Exception)
                    {
                        continue; // (a bind this game version has no binding for)
                    }
                    Axis[i] = v;
                    Held[i] = v > 0f; // (as SteelInput.Axis.Update: held while its value is above zero)
                }
                Held[SwitchTeam] = !OnlyPause && s.Keys[0x54]; // T
            }
            if (Pad >= 0 && !OnlyPause)
            {
                ReadPad(XInput.Read(Pad));
            }
            if (InvertY)
            {
                Axis[(int)SteelInput.KeyBinds.AimY] = -Axis[(int)SteelInput.KeyBinds.AimY];
            }
            AfterCompute?.Invoke();
        }

        /// <summary>Called once the frame's binds are made (a copy's own menu reads them, then keeps them from the game while open).</summary>
        internal static Action AfterCompute;

        /// <summary>The player's own setting: up and down of the mouse and right stick turned round (a copy's menu).</summary>
        public static bool InvertY;

        /// <summary>Every bind let go for the game this frame (a copy's menu is open: its keys move the menu, not the soldier).</summary>
        internal static void Suppress()
        {
            Array.Clear(Axis, 0, Count);
            Array.Clear(Held, 0, Count);
        }

        /// <summary>One bind let go for the game this frame (a copy's Esc opens its own menu, it does not pause the game).</summary>
        internal static void Suppress(SteelInput.KeyBinds b)
        {
            Axis[(int)b] = 0f;
            Held[(int)b] = false;
        }

        /// <summary>A bind's value this frame (a copy's menu reads A/D and W/S by the game's own bindings).</summary>
        internal static float AxisOf(SteelInput.KeyBinds b) => Axis[(int)b];

        private static DeviceState _current;

        /// <summary>A key of this player's keyboard held now, by Windows' virtual key (Esc for a copy's menu: the game reads Esc itself).</summary>
        internal static bool RawKey(int vk) => _current != null && _current.Keys[vk];

        /// <summary>Forgets the mouse's last totals (a new keyboard and mouse, or a new host): the first frame after turns nothing.</summary>
        public static void ResetMouse() => _mouseKnown = false;

        /// <summary>
        /// The player's controller, as Ravenfield's own controller layout (Steam Input) has it. The game's sideways binds are positive to
        /// the left (its Steam pad actions give -x), so the sticks' x is turned round for them.
        /// </summary>
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
            Stick(SteelInput.KeyBinds.Horizontal, -l.x);
            Stick(SteelInput.KeyBinds.Vertical, l.y);
            Stick(SteelInput.KeyBinds.CarSteer, -l.x);
            Stick(SteelInput.KeyBinds.CarThrottle, s.RightTrigger - s.LeftTrigger);
            Stick(SteelInput.KeyBinds.HeliRoll, -l.x);
            Stick(SteelInput.KeyBinds.HeliPitch, l.y);
            Stick(SteelInput.KeyBinds.HeliYaw, -r.x);
            Stick(SteelInput.KeyBinds.HeliThrottle, r.y);
            Stick(SteelInput.KeyBinds.PlaneRoll, -l.x);
            Stick(SteelInput.KeyBinds.PlanePitch, l.y);
            Stick(SteelInput.KeyBinds.PlaneYaw, -r.x);
            Stick(SteelInput.KeyBinds.PlaneThrottle, s.RightTrigger - s.LeftTrigger);
            float turn = PadTurn * Time.unscaledDeltaTime;
            Stick(SteelInput.KeyBinds.AimX, -r.x * turn);
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
            Held[SwitchTeam] |= s.Has(PadState.Right);
        }

        /// <summary>A bind held this frame by this player.</summary>
        internal static bool IsHeld(SteelInput.KeyBinds b) => Held[(int)b];

        internal static bool WentDown(SteelInput.KeyBinds b) => Held[(int)b] && !WasHeld[(int)b];

        internal static bool WentDown(int bind) => Held[bind] && !WasHeld[bind];

        public static float MergeAxis(SteelInput.KeyBinds b, float game) =>
            Mathf.Abs(Axis[(int)b]) > Mathf.Abs(game) ? Axis[(int)b] : game;

        public static bool MergeButton(SteelInput.KeyBinds b, bool game) => game || Held[(int)b];

        public static bool MergeButtonDown(SteelInput.KeyBinds b, bool game) => game || Held[(int)b] && !WasHeld[(int)b];

        public static bool MergeButtonUp(SteelInput.KeyBinds b, bool game) => game || !Held[(int)b] && WasHeld[(int)b];
    }
}
