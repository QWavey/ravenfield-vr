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
            lock (RawInput.Gate)
            {
                Vector2 d = Delta;
                Delta = Vector2.zero;
                return d;
            }
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

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int size);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRegisteredRawInputDevices([Out] RAWINPUTDEVICE[] devices, ref uint count, uint size);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>The mouse and keyboard as the game (Unity) had them registered before (Unity reads its mouse through Raw Input too), put back on Stop.</summary>
        private static RAWINPUTDEVICE? _hadMouse, _hadKeyboard;

        private static IntPtr _window, _previous;
        private static WndProc _hook; // (kept alive: the window calls it)
        private static bool _blocking, _failed;
        private static bool _installed;
        private static readonly Dictionary<IntPtr, RawDevice> Devices = new Dictionary<IntPtr, RawDevice>();
        private static int _keyboards, _mice;
        private static IntPtr _buffer = IntPtr.Zero;
        private static uint _bufferSize;

        public static bool Started => _installed;

        /// <summary>Check: WM_INPUT messages the window had, and how many could not be read (with the last reason).</summary>
        internal static int Messages, Errors;
        internal static string LastError;
        /// <summary>The thread the window's messages come on, against Unity's (its window may have a thread of its own).</summary>
        internal static int ProcThread, MainThread;

        internal static readonly object Gate = new object();

        /// <summary>The n-th keyboard or mouse heard (0: the first), or null.</summary>
        public static RawDevice Nth(bool mouse, int n)
        {
            lock (Gate)
            {
                foreach (RawDevice d in Devices.Values)
                {
                    if (d.IsMouse == mouse && d.Order == n)
                    {
                        return d;
                    }
                }
            }
            return null;
        }

        public static int Count(bool mouse) => mouse ? _mice : _keyboards;

        /// <summary>Check: a key held on any keyboard heard.</summary>
        internal static bool AnyKeyHeld(int vk)
        {
            lock (Gate)
            {
                foreach (RawDevice d in Devices.Values)
                {
                    if (!d.IsMouse && d.Keys[vk])
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        [StructLayout(LayoutKind.Explicit, Size = 40)]
        private struct KeyInput
        {
            [FieldOffset(0)] public int Type;
            [FieldOffset(8)] public ushort VKey;
            [FieldOffset(12)] public uint Flags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, KeyInput[] inputs, int size);

        /// <summary>Check: a key pressed or let go through Windows (F13, which nothing uses), as a keyboard would send it.</summary>
        internal static bool SendKey(ushort vk, bool down)
        {
            var i = new[] { new KeyInput { Type = 1, VKey = vk, Flags = down ? 0u : 2u } };
            return SendInput(1, i, Marshal.SizeOf(typeof(KeyInput))) == 1;
        }

        /// <summary>Check: the window Raw Input listens on (zero: none found).</summary>
        internal static IntPtr Window => _installed ? _window : IntPtr.Zero;

        public static void Start()
        {
            if (_installed || _failed)
            {
                return; // (tried once: not every frame)
            }
            try
            {
                _window = GameWindow();
                if (_window == IntPtr.Zero)
                {
                    throw new InvalidOperationException("no game window");
                }
                MainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                RememberUnitysOwn();
                ClearHeld();
                _hook = _hook ?? Proc; // (kept for good once made: the window may still call it after a restore that did not take)
                _previous = SetWindowLongPtr(_window, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_hook));
                if (_previous == IntPtr.Zero)
                {
                    throw new InvalidOperationException("the window's procedure could not be replaced");
                }
                _installed = true;
                if (!Register(0))
                {
                    Log.Warn("Splitscreen: Raw Input registration failed (error " + Marshal.GetLastWin32Error() + "): keyboards and mice not told apart.");
                }
                else
                {
                    Log.Info("Splitscreen: keyboards and mice told apart (Raw Input on the game's window).");
                }
            }
            catch (Exception e)
            {
                _failed = true;
                Log.Warn("Splitscreen: keyboards and mice could not be told apart: " + e.Message);
            }
        }

        /// <summary>
        /// The merged keyboard and mouse kept from the game (Windows sends it none), or given back. On while a splitscreen player plays with
        /// a keyboard and mouse: else that player's keys moved the game's own player too.
        /// </summary>
        public static void BlockLegacy(bool block)
        {
            if (!_installed || block == _blocking)
            {
                return;
            }
            _blocking = block;
            Register(block ? RIDEV_NOLEGACY : 0);
            Log.Info(block ? "Splitscreen: the merged keyboard and mouse kept from the game (each player has their own)." : "Splitscreen: the keyboard and mouse back to the game.");
        }

        private static bool Register(uint extra)
        {
            var devices = new[]
            {
                new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_INPUTSINK | extra, Target = _window },
                new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = RIDEV_INPUTSINK | extra, Target = _window },
            };
            return RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
        }

        /// <summary>Every key and button let go and movement dropped: a let-go not heard while stopped kept a soldier walking or firing.</summary>
        private static void ClearHeld()
        {
            lock (Gate)
            {
                foreach (RawDevice d in Devices.Values)
                {
                    Array.Clear(d.Keys, 0, d.Keys.Length);
                    d.Left = d.Right = d.Middle = false;
                    d.Delta = Vector2.zero;
                }
            }
        }

        public static void Stop()
        {
            if (!_installed)
            {
                return;
            }
            try
            {
                BlockLegacy(false);
                // Unity's own registration back as it was (taking it away left the game's mouse look dead), else ours removed.
                var devices = new[]
                {
                    _hadMouse ?? new RAWINPUTDEVICE { UsagePage = 1, Usage = 2, Flags = RIDEV_REMOVE, Target = IntPtr.Zero },
                    _hadKeyboard ?? new RAWINPUTDEVICE { UsagePage = 1, Usage = 6, Flags = RIDEV_REMOVE, Target = IntPtr.Zero },
                };
                RegisterRawInputDevices(devices, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
                SetWindowLongPtr(_window, GWLP_WNDPROC, _previous);
            }
            catch (Exception)
            {
            }
            _installed = false;
            ClearHeld();
        }

        /// <summary>
        /// The game's own window: the Unity player's window of this process (Process.MainWindowHandle came back empty for it in the
        /// check, 2026-11-14: "no game window"), else the main window, else the active one.
        /// </summary>
        private static IntPtr GameWindow()
        {
            uint me = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero, anyVisible = IntPtr.Zero;
            var name = new System.Text.StringBuilder(64);
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pid != me)
                {
                    return true;
                }
                name.Length = 0;
                GetClassName(h, name, name.Capacity);
                if (name.ToString() == "UnityWndClass")
                {
                    found = h;
                    return false;
                }
                if (anyVisible == IntPtr.Zero && IsWindowVisible(h))
                {
                    anyVisible = h;
                }
                return true;
            }, IntPtr.Zero);
            if (found != IntPtr.Zero)
            {
                return found;
            }
            IntPtr main = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle;
            return main != IntPtr.Zero ? main : anyVisible != IntPtr.Zero ? anyVisible : GetActiveWindow();
        }

        private static void RememberUnitysOwn()
        {
            _hadMouse = _hadKeyboard = null;
            uint size = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)), count = 0;
            GetRegisteredRawInputDevices(null, ref count, size);
            if (count == 0)
            {
                return;
            }
            var had = new RAWINPUTDEVICE[count];
            if (GetRegisteredRawInputDevices(had, ref count, size) == uint.MaxValue)
            {
                return;
            }
            foreach (RAWINPUTDEVICE d in had)
            {
                if (d.UsagePage == 1 && d.Usage == 2)
                {
                    _hadMouse = d;
                }
                else if (d.UsagePage == 1 && d.Usage == 6)
                {
                    _hadKeyboard = d;
                }
            }
        }

        private static IntPtr Proc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_INPUT)
            {
                Messages++;
                ProcThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
                try
                {
                    lock (Gate)
                    {
                        Read(lParam);
                    }
                }
                catch (Exception e)
                {
                    // (never thrown back into the window's message loop)
                    Errors++;
                    LastError = e.GetType().Name + ": " + e.Message;
                }
                if (_blocking)
                {
                    // Kept from Unity too: it reads its mouse look from Raw Input, all mice merged, so the second player's mouse turned the
                    // game's own player (that one is answered from FlatPlayerInput instead).
                    return DefWindowProc(hWnd, msg, wParam, lParam);
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
            // (Read field by field, no structure boxed: a gaming mouse sends a thousand of these a second. RAWINPUTHEADER: Type, Size,
            // then the device handle at 8; the body after the header.)
            int type = Marshal.ReadInt32(_buffer, 0);
            IntPtr device = Marshal.ReadIntPtr(_buffer, 8);
            int body = (int)header;
            bool mouse = type == RIM_TYPEMOUSE;
            if (!mouse && type != RIM_TYPEKEYBOARD)
            {
                return;
            }
            if (!Devices.TryGetValue(device, out RawDevice d))
            {
                d = new RawDevice { Handle = device, IsMouse = mouse, Order = mouse ? _mice++ : _keyboards++ };
                Devices[device] = d;
            }
            d.LastActive = Environment.TickCount / 1000f; // (not Unity's clock: the window's messages may come on a thread of their own)
            if (mouse)
            {
                // RAWMOUSE: usFlags at 0, usButtonFlags at 4, usButtonData at 6, lLastX at 12, lLastY at 16.
                var m = new RAWMOUSE
                {
                    Flags = (ushort)Marshal.ReadInt16(_buffer, body),
                    ButtonFlags = (ushort)Marshal.ReadInt16(_buffer, body + 4),
                    ButtonData = Marshal.ReadInt16(_buffer, body + 6),
                    LastX = Marshal.ReadInt32(_buffer, body + 12),
                    LastY = Marshal.ReadInt32(_buffer, body + 16),
                };
                if ((m.Flags & 1) == 0 && _blocking) // (relative movement, not an absolute position; only while playing, else a menu's movement landed as one turn)
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
                // RAWKEYBOARD: MakeCode at 0, Flags at 2, Reserved at 4, VKey at 6.
                var k = new RAWKEYBOARD { Flags = (ushort)Marshal.ReadInt16(_buffer, body + 2), VKey = (ushort)Marshal.ReadInt16(_buffer, body + 6) };
                if (k.VKey < 256)
                {
                    d.Keys[k.VKey] = (k.Flags & 1) == 0; // (RI_KEY_BREAK: let go)
                }
            }
        }
    }
}
