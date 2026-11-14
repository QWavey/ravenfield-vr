using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>One keyboard or mouse as Windows' Raw Input tells it apart from the others.</summary>
    internal sealed class RawDevice
    {
        public IntPtr Handle;
        public bool IsMouse;
        /// <summary>The order it was first heard in among its kind (0: the first keyboard / mouse).</summary>
        public int Order;
        public float LastActive;
        public readonly bool[] Keys = new bool[256];
        public bool Left, Right, Middle;
        public Vector2 Delta; // (mouse movement since the frame's read; taken by Consume)
        public int Wheel;

        public Vector2 ConsumeDelta()
        {
            Vector2 d = Delta;
            Delta = Vector2.zero;
            return d;
        }
    }

    /// <summary>
    /// Every keyboard and mouse apart, through Windows' Raw Input on the game's window: the game itself sees them merged into one, so two
    /// players at one PC could not each have their own (the user, 2026-11-14: "2 keyboard and mouse then needed"). Started only when a
    /// splitscreen player plays with keyboard and mouse. <see cref="BlockLegacy"/> keeps the merged keyboard and mouse away from the game
    /// (its own player is then fed from <see cref="FlatPlayerInput"/>), so one player's keys never move the other.
    /// </summary>
    internal static class RawInput
    {
        private const int WM_INPUT = 0x00FF, GWLP_WNDPROC = -4, RID_INPUT = 0x10000003, RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
        private const uint RIDEV_INPUTSINK = 0x00000100, RIDEV_NOLEGACY = 0x00000030, RIDEV_REMOVE = 0x00000001;

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort UsagePage, Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTHEADER
        {
            public uint Type, Size;
            public IntPtr Device, WParam;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct RAWMOUSE
        {
            [FieldOffset(0)] public ushort Flags;
            [FieldOffset(4)] public ushort ButtonFlags;
            [FieldOffset(6)] public short ButtonData;
            [FieldOffset(8)] public uint RawButtons;
            [FieldOffset(12)] public int LastX;
            [FieldOffset(16)] public int LastY;
            [FieldOffset(20)] public uint Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWKEYBOARD
        {
            public ushort MakeCode, Flags, Reserved, VKey;
            public uint Message, Extra;
        }

        private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "CallWindowProcW")]
        private static extern IntPtr CallWindowProc(IntPtr previous, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        private static IntPtr _window, _previous;
        private static WndProc _hook; // (kept alive: the window calls it)
        private static bool _blocking;
        private static readonly Dictionary<IntPtr, RawDevice> Devices = new Dictionary<IntPtr, RawDevice>();
        private static int _keyboards, _mice;
        private static IntPtr _buffer = IntPtr.Zero;
        private static uint _bufferSize;

        public static bool Started => _hook != null;

        /// <summary>The n-th keyboard or mouse heard (0: the first), or null.</summary>
        public static RawDevice Nth(bool mouse, int n)
        {
            foreach (RawDevice d in Devices.Values)
            {
                if (d.IsMouse == mouse && d.Order == n)
                {
                    return d;
                }
            }
            return null;
        }

        public static int Count(bool mouse) => mouse ? _mice : _keyboards;

        public static void Start()
        {
            if (_hook != null)
            {
                return;
            }
            try
            {
                _window = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
                if (_window == IntPtr.Zero)
                {
                    _window = GetActiveWindow();
                }
                _hook = Proc;
                _previous = SetWindowLongPtr(_window, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hook));
                Register(0);
                Log.Info("Splitscreen: keyboards and mice told apart (Raw Input on the game's window).");
            }
            catch (Exception e)
            {
                _hook = null;
                Log.Warn("Splitscreen: keyboards and mice could not be told apart: " + e.Message);
            }
        }

        /// <summary>
        /// The merged keyboard and mouse kept from the game (Windows sends it none), or given back. On while a splitscreen player plays with
        /// a keyboard and mouse: else that player's keys moved the game's own player too.
        /// </summary>
        public static void BlockLegacy(bool block)
        {
            if (_hook == null || block == _blocking)
            {
                return;
            }
            _blocking = block;
            Register(block ? RIDEV_NOLEGACY : 0);
            Log.Info(block ? "Splitscreen: the merged keyboard and mouse kept from the game (each player has their own)." : "Splitscreen: the keyboard and mouse back to the game.");
        }

        private static void Register(uint extra)
        {
            var devices = new[]
            {
                new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK | extra, Target = _window },
                new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = RIDEV_INPUTSINK | extra, Target = _window },
            };
            RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        public static void Stop()
        {
            if (_hook == null)
            {
                return;
            }
            try
            {
                BlockLegacy(false);
                var devices = new[]
                {
                    new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_REMOVE, Target = IntPtr.Zero },
                    new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = RIDEV_REMOVE, Target = IntPtr.Zero },
                };
                RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                SetWindowLongPtr(_window, GWLP_WNDPROC, _previous);
            }
            catch (Exception)
            {
            }
            _hook = null;
        }

        private static IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                try
                {
                    Read(lParam);
                }
                catch (Exception)
                {
                    // (never thrown back into the window's message loop)
                }
            }
            return CallWindowProc(_previous, hWnd, msg, wParam, lParam);
        }

        private static void Read(IntPtr handle)
        {
            uint header = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            uint size = 0;
            GetRawInputData(handle, RID_INPUT, IntPtr.Zero, ref size, header);
            if (size == 0)
            {
                return;
            }
            if (size > _bufferSize)
            {
                if (_buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_buffer);
                }
                _buffer = Marshal.AllocHGlobal((int)size);
                _bufferSize = size;
            }
            if (GetRawInputData(handle, RID_INPUT, _buffer, ref size, header) != size)
            {
                return;
            }
            var h = (RAWINPUTHEADER)Marshal.PtrToStructure(_buffer, typeof(RAWINPUTHEADER));
            IntPtr body = new IntPtr(_buffer.ToInt64() + header);
            bool mouse = h.Type == RIM_TYPEMOUSE;
            if (!mouse && h.Type != RIM_TYPEKEYBOARD)
            {
                return;
            }
            if (!Devices.TryGetValue(h.Device, out RawDevice d))
            {
                d = new RawDevice { Handle = h.Device, IsMouse = mouse, Order = mouse ? _mice++ : _keyboards++ };
                Devices[h.Device] = d;
            }
            d.LastActive = Time.unscaledTime;
            if (mouse)
            {
                var m = (RAWMOUSE)Marshal.PtrToStructure(body, typeof(RAWMOUSE));
                if ((m.Flags & 1) == 0) // (relative movement, not an absolute position)
                {
                    d.Delta += new Vector2(m.LastX, m.LastY);
                }
                ushort b = m.ButtonFlags;
                if ((b & 0x0001) != 0) d.Left = true;
                if ((b & 0x0002) != 0) d.Left = false;
                if ((b & 0x0004) != 0) d.Right = true;
                if ((b & 0x0008) != 0) d.Right = false;
                if ((b & 0x0010) != 0) d.Middle = true;
                if ((b & 0x0020) != 0) d.Middle = false;
                if ((b & 0x0400) != 0) d.Wheel += m.ButtonData;
            }
            else
            {
                var k = (RAWKEYBOARD)Marshal.PtrToStructure(body, typeof(RAWKEYBOARD));
                if (k.VKey < 256)
                {
                    d.Keys[k.VKey] = (k.Flags & 1) == 0; // (RI_KEY_BREAK: let go)
                }
            }
        }
    }
}
