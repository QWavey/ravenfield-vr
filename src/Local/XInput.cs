using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>One controller's state this frame (sticks and triggers from -1..1 / 0..1, a dead zone taken off).</summary>
    internal struct PadState
    {
        public bool Connected;
        public Vector2 LeftStick, RightStick;
        public float LeftTrigger, RightTrigger;
        public ushort Buttons;

        public const ushort Up = 0x0001, Down = 0x0002, Left = 0x0004, Right = 0x0008, Start = 0x0010, Back = 0x0020, LeftThumb = 0x0040,
            RightThumb = 0x0080, LeftShoulder = 0x0100, RightShoulder = 0x0200, A = 0x1000, B = 0x2000, X = 0x4000, Y = 0x8000;

        public bool Has(ushort button) => (Buttons & button) != 0;
    }

    /// <summary>
    /// Xbox-style controllers read straight from Windows (XInput), up to four, each by its own number: Unity's own input merges the
    /// controllers and the game reads them for its one player. The splitscreen players' controllers (<see cref="SplitPlayers"/>).
    /// </summary>
    internal static class XInput
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Gamepad
        {
            public ushort Buttons;
            public byte LeftTrigger, RightTrigger;
            public short LX, LY, RX, RY;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct State
        {
            public uint Packet;
            public Gamepad Pad;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState14(uint index, out State state);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
        private static extern uint GetState910(uint index, out State state);

        /// <summary>0: xinput1_4, 1: xinput9_1_0, 2: none on this PC.</summary>
        private static int _library;

        public static PadState Read(int index)
        {
            var p = new PadState();
            if (index < 0 || index > 3 || _library > 1)
            {
                return p;
            }
            State s;
            uint result;
            try
            {
                result = _library == 0 ? GetState14((uint)index, out s) : GetState910((uint)index, out s);
            }
            catch (Exception)
            {
                _library++; // (that library is not on this PC: the next one, then none)
                return p;
            }
            if (result != 0)
            {
                return p; // (not connected)
            }
            p.Connected = true;
            p.Buttons = s.Pad.Buttons;
            p.LeftStick = Stick(s.Pad.LX, s.Pad.LY);
            p.RightStick = Stick(s.Pad.RX, s.Pad.RY);
            p.LeftTrigger = s.Pad.LeftTrigger / 255f;
            p.RightTrigger = s.Pad.RightTrigger / 255f;
            return p;
        }

        /// <summary>A stick's -1..1, its dead zone (a quarter) taken off and the rest stretched to the full range.</summary>
        private static Vector2 Stick(short x, short y)
        {
            var v = new Vector2(x / 32767f, y / 32767f);
            float m = v.magnitude;
            if (m < 0.24f)
            {
                return Vector2.zero;
            }
            return v / m * Mathf.Min(1f, (m - 0.24f) / 0.76f);
        }
    }
}
