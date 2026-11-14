using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using RavenfieldVR.Core;
using RavenfieldVR.Net;
using UnityEngine;

namespace RavenfieldVR.Local
{
    /// <summary>
    /// Splitscreen with whole copies of the game (the user, 2026-11-14: "we need to have the same view as our main player ... same features
    /// + engine. Basically the same person cloned but player 2, the same we do local LAN multiplayer"). Each player at this PC plays their
    /// own copy of the game, borderless in their part of the screen; the first one hosts a LAN game on this PC and the others join it at
    /// this PC's own address. So every player has everything the game's own player has: its first-person view and HUD, its deploy screen
    /// and loadout, its engine, and every feature of the mod and of the multiplayer (they see each other, damage each other on the other
    /// side, share the vehicles).
    /// <para>
    /// Input: Windows gives raw keys to the window in front only. The host tells every keyboard and mouse apart (Raw Input) and sends each
    /// copy its player's binds every frame (UDP on this PC, from port 7151 up); a copy that happens to be in front gives its events to the
    /// host first (port 7150). Controllers are read by each copy itself (XInput works behind other windows): player n takes controller n.
    /// The copies' sound is off (the host plays the match's sound once, not two or four times over).
    /// </para>
    /// </summary>
    internal static class SplitGames
    {
        public const int GamePort = 7140, HostInputPort = 7150;

        private static readonly string[] Args = Environment.GetCommandLineArgs();

        /// <summary>This copy's player number when it was started by a host as one (1 to 3), else 0.</summary>
        public static readonly int ClientIndex = ArgInt("-rfvr-splitclient=", 0);

        private static readonly int ClientCount = ArgInt("-rfvr-splitn=", 2);
        private static readonly string HostAddress = ArgText("-rfvr-splithost=", "127.0.0.1:" + GamePort);

        public static bool IsClient => ClientIndex > 0;

        /// <summary>Try mode on the host: player 2 takes the first keyboard and mouse, the host's own player idle (Esc still its).</summary>
        public static bool TryMode;

        private static int ArgInt(string prefix, int fallback)
        {
            string a = Args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return a != null && int.TryParse(a.Substring(prefix.Length), out int v) ? v : fallback;
        }

        private static string ArgText(string prefix, string fallback)
        {
            string a = Args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return a != null ? a.Substring(prefix.Length) : fallback;
        }

        public static void Update()
        {
            try
            {
                if (IsClient)
                {
                    ClientUpdate();
                }
                else
                {
                    HostUpdate();
                }
            }
            catch (Exception e)
            {
                Log.ExceptionOnce("split-games", e);
            }
        }

        // ---- the host: player 1's copy ----------------------------------------------------------------------------------------------

        private sealed class Copy
        {
            public int Index;
            public Process Process;
            public IPEndPoint Input;
            public readonly FlatPlayerInput.Binds Binds = new FlatPlayerInput.Binds();
        }

        private static readonly List<Copy> Copies = new List<Copy>();
        private static bool _hosting, _weHosted;
        private static UdpClient _hostSocket;
        private static readonly byte[] Packet = new byte[16 + FlatPlayerInput.Count * 5];

        /// <summary>Whether the copies are running (the Local page shows it).</summary>
        public static bool Running => _hosting;

        private static void HostUpdate()
        {
            bool want = Splitscreen.On && Splitscreen.FullGames && SplitPlayers.InMatch && !(Multiplayer.Active && !Multiplayer.Hosting);
            if (want && !_hosting)
            {
                HostStart();
            }
            else if (!want && _hosting)
            {
                HostStop();
            }
            if (!_hosting)
            {
                return;
            }
            Copies.RemoveAll(c => c.Process == null || c.Process.HasExited);
            HostInput();
        }

        private static void HostStart()
        {
            if (Multiplayer.Active && !Multiplayer.LanHosting)
            {
                Multiplayer.Notice("Splitscreen with game copies needs a LAN game: leave the online game first (or use Soldiers splitscreen).");
                Splitscreen.On = false;
                return;
            }
            if (!Multiplayer.Active)
            {
                Multiplayer.HostLan(GamePort, 8);
                _weHosted = true;
            }
            _hosting = true;
            int n = Splitscreen.Players;
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string mods = Args.FirstOrDefault(a => a.StartsWith("-rfvr-mods=", StringComparison.OrdinalIgnoreCase));
            for (int i = 1; i < n; i++)
            {
                var c = new Copy { Index = i, Input = new IPEndPoint(IPAddress.Loopback, HostInputPort + i) };
                string args = $"-novr -nointro -screen-fullscreen 0 -rfvr-splitclient={i} -rfvr-splitn={n} -rfvr-splithost=127.0.0.1:{GamePort}"
                              + (mods != null ? " " + mods : "");
                try
                {
                    var start = new ProcessStartInfo(exe, args) { UseShellExecute = false, WorkingDirectory = System.IO.Path.GetDirectoryName(exe) };
                    // (Without the mod loader's own variables: inherited, its loader in the copy took itself for already run and loaded no
                    // mod, so the copy never joined; 2026-11-14 22:20.)
                    foreach (string key in start.EnvironmentVariables.Keys.Cast<string>().ToList())
                    {
                        if (key.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase))
                        {
                            start.EnvironmentVariables.Remove(key);
                        }
                    }
                    c.Process = Process.Start(start);
                    Copies.Add(c);
                    Log.Info($"Splitscreen: player {i + 1}'s own copy of the game started ({args}).");
                }
                catch (Exception e)
                {
                    Log.Warn($"Splitscreen: player {i + 1}'s copy of the game could not start: {e.Message}");
                }
            }
            try
            {
                _hostSocket = Quiet(new UdpClient(new IPEndPoint(IPAddress.Loopback, HostInputPort)));
            }
            catch (Exception e)
            {
                Log.Warn("Splitscreen: the copies' input port could not be opened: " + e.Message);
            }
            RawInput.Start();
            SplitPlayers.Patch(); // (the game's merged controllers muted: each copy reads its own)
            SplitPlayers.GamePadsMuted = true;
            FlatPlayerInput.Keys = 0;
            FlatPlayerInput.Pad = 0;
            FlatPlayerInput.OnlyPause = TryMode;
            if (!Runtime.OpenVRRuntime.Running)
            {
                PlaceWindow(0, n);
            }
            Log.Info($"Splitscreen: {n} players, each with a whole copy of the game (this one hosts on port {GamePort}).");
        }

        private static void HostStop()
        {
            foreach (Copy c in Copies)
            {
                try
                {
                    if (c.Process != null && !c.Process.HasExited)
                    {
                        c.Process.Kill();
                    }
                }
                catch (Exception)
                {
                }
            }
            Copies.Clear();
            _hostSocket?.Close();
            _hostSocket = null;
            _hosting = false;
            FlatPlayerInput.Active = false;
            FlatPlayerInput.OnlyPause = false;
            SplitPlayers.GamePadsMuted = false;
            RawInput.Stop();
            if (_weHosted)
            {
                _weHosted = false;
                Multiplayer.Leave();
            }
            if (!Runtime.OpenVRRuntime.Running)
            {
                RestoreWindow();
            }
            Log.Info("Splitscreen: the game copies closed, back to one screen.");
        }

        /// <summary>Once a frame: the copies' events taken in, then each copy sent its player's binds.</summary>
        private static void HostInput()
        {
            bool playing = Cursor.lockState == CursorLockMode.Locked && !GameManager.IsPaused();
            RawInput.BlockLegacy(playing);
            FlatPlayerInput.Active = playing;
            while (_hostSocket != null && _hostSocket.Available > 0)
            {
                IPEndPoint from = null;
                byte[] e = _hostSocket.Receive(ref from);
                if (e.Length >= 26 && e[0] == (byte)'E')
                {
                    RawInput.Inject(BitConverter.ToInt64(e, 1), e[9] != 0, BitConverter.ToUInt16(e, 10), BitConverter.ToUInt16(e, 12),
                                    BitConverter.ToInt16(e, 14), BitConverter.ToInt32(e, 16), BitConverter.ToInt32(e, 20), BitConverter.ToUInt16(e, 24));
                }
            }
            foreach (Copy c in Copies)
            {
                int keys = TryMode ? c.Index - 1 : c.Index;
                FlatPlayerInput.FromKeys(c.Binds, RawInput.Nth(false, keys), RawInput.Nth(true, keys), false);
                int length = Write(c.Binds);
                try
                {
                    _hostSocket?.Send(Packet, length, c.Input);
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// A UDP socket that does not fail its next receive after a send to a port nobody listens on yet (Windows reports that as "connection
        /// reset by peer" on UDP: the host sends to a copy still loading; check of 2026-11-14 22:15).
        /// </summary>
        private static UdpClient Quiet(UdpClient u)
        {
            try
            {
                const int SIO_UDP_CONNRESET = -1744830452;
                u.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
            }
            catch (Exception)
            {
            }
            return u;
        }

        /// <summary>A copy's own lines in BepInEx/RavenfieldVR-split-N.log (its BepInEx log does not reach the disk beside the host's).</summary>
        internal static void Note(string line)
        {
            Log.Info(line);
            if (!IsClient)
            {
                return;
            }
            try
            {
                string file = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, $"RavenfieldVR-split-{ClientIndex + 1}.log");
                System.IO.File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }
        }

        private static int Write(FlatPlayerInput.Binds b)
        {
            int at = 0;
            Packet[at++] = (byte)'B';
            Buffer.BlockCopy(BitConverter.GetBytes(b.MouseX), 0, Packet, at, 4);
            at += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(b.MouseY), 0, Packet, at, 4);
            at += 4;
            Buffer.BlockCopy(BitConverter.GetBytes(b.Wheel), 0, Packet, at, 4);
            at += 4;
            for (int i = 0; i < FlatPlayerInput.Count; i++)
            {
                Buffer.BlockCopy(BitConverter.GetBytes(b.Axis[i]), 0, Packet, at, 4);
                at += 4;
                Packet[at++] = (byte)(b.Held[i] ? 1 : 0);
            }
            return at;
        }

        // ---- a copy: player 2 to 4 ---------------------------------------------------------------------------------------------------

        private static UdpClient _clientSocket;
        private static IPEndPoint _hostInput;
        private static bool _joined, _clientSetUp, _heardHost, _saidMatch, _saidDeploy;
        private static float _joinAt = -1f, _lastFromHost, _placedUntil;
        private static readonly FlatPlayerInput.Binds Received = new FlatPlayerInput.Binds();

        private static void ClientUpdate()
        {
            if (!_clientSetUp)
            {
                _clientSetUp = true;
                Application.runInBackground = true;
                _clientSocket = Quiet(new UdpClient(new IPEndPoint(IPAddress.Loopback, HostInputPort + ClientIndex)));
                _hostInput = new IPEndPoint(IPAddress.Loopback, HostInputPort);
                RawInput.Start();
                RawInput.Forward = ForwardToHost;
                SplitPlayers.Patch();
                SplitPlayers.GamePadsMuted = true;
                FlatPlayerInput.Keys = -1;
                FlatPlayerInput.Pad = ClientIndex;
                FlatPlayerInput.Receive(Received);
                _lastFromHost = Time.unscaledTime + 60f; // (time to load and join before the host is missed)
                _placedUntil = Time.unscaledTime + 30f;
                Note($"Splitscreen: this is player {ClientIndex + 1}'s copy of the game; joining {HostAddress} once the menu is up.");
            }
            AudioListener.volume = 0f; // (the host plays the match's sound)
            // The window in its part of the screen, again for a while (the game sets its own size as it loads).
            if (Time.unscaledTime < _placedUntil && Time.frameCount % 30 == 0)
            {
                PlaceWindow(ClientIndex, ClientCount);
            }
            // Joined once the main menu is up.
            if (!_joined && GameManager.instance != null && GameManager.IsInMainMenu())
            {
                if (_joinAt < 0f)
                {
                    _joinAt = Time.unscaledTime + 1.5f;
                }
                else if (Time.unscaledTime >= _joinAt)
                {
                    _joined = true;
                    Multiplayer.JoinAddress(HostAddress);
                    Note("Splitscreen: joining the host's game at " + HostAddress + ".");
                    _placedUntil = Time.unscaledTime + 30f;
                }
            }
            // The host's binds for this player.
            while (_clientSocket != null && _clientSocket.Available > 0)
            {
                IPEndPoint from = null;
                byte[] p = _clientSocket.Receive(ref from);
                if (p.Length >= 13 + FlatPlayerInput.Count * 5 && p[0] == (byte)'B')
                {
                    if (!_heardHost)
                    {
                        _heardHost = true;
                        Note("Splitscreen: the host's input for this player is coming in.");
                    }
                    Read(p);
                    _lastFromHost = Time.unscaledTime;
                }
            }
            if (Time.unscaledTime - _lastFromHost > 10f)
            {
                Note("Splitscreen: the host's copy is gone; this copy closes.");
                Application.Quit();
            }
            bool playing = SplitPlayers.InMatch && !GameManager.IsPaused();
            if (SplitPlayers.InMatch && !_saidMatch)
            {
                _saidMatch = true;
                Note($"Splitscreen: in the host's match (multiplayer {(Multiplayer.Active ? "joined" : "NOT joined")}); window {Screen.width}x{Screen.height}.");
            }
            RawInput.BlockLegacy(playing);
            FlatPlayerInput.Active = playing;
            // The deploy screen by a key: this copy's window has no pointer (the host's has it), so fire or jump deploys.
            if (playing && LocalPlayer.actor != null && LocalPlayer.actor.dead && LoadoutUi.instance != null && LoadoutUi.IsOpen()
                && (FlatPlayerInput.WentDown(SteelInput.KeyBinds.Fire) || FlatPlayerInput.WentDown(SteelInput.KeyBinds.Jump)))
            {
                LoadoutUi.instance.OnDeployClick();
                if (!_saidDeploy)
                {
                    _saidDeploy = true;
                    Note("Splitscreen: deployed by this player's key.");
                }
            }
        }

        private static void Read(byte[] p)
        {
            int at = 1;
            Received.MouseX = BitConverter.ToSingle(p, at);
            at += 4;
            Received.MouseY = BitConverter.ToSingle(p, at);
            at += 4;
            Received.Wheel = BitConverter.ToInt32(p, at);
            at += 4;
            for (int i = 0; i < FlatPlayerInput.Count; i++)
            {
                Received.Axis[i] = BitConverter.ToSingle(p, at);
                at += 4;
                Received.Held[i] = p[at++] != 0;
            }
        }

        private static readonly byte[] Event = new byte[26];

        /// <summary>This copy is in front: its events go to the host, which tells the keyboards and mice apart for every player.</summary>
        private static void ForwardToHost(long device, bool mouse, ushort flags, ushort buttons, short data, int x, int y, ushort vkey)
        {
            Event[0] = (byte)'E';
            Buffer.BlockCopy(BitConverter.GetBytes(device), 0, Event, 1, 8);
            Event[9] = (byte)(mouse ? 1 : 0);
            Buffer.BlockCopy(BitConverter.GetBytes(flags), 0, Event, 10, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(buttons), 0, Event, 12, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(data), 0, Event, 14, 2);
            Buffer.BlockCopy(BitConverter.GetBytes(x), 0, Event, 16, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(y), 0, Event, 20, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(vkey), 0, Event, 24, 2);
            _clientSocket?.Send(Event, Event.Length, _hostInput);
        }

        // ---- the windows -------------------------------------------------------------------------------------------------------------

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        private const int GWL_STYLE = -16;
        private const uint SWP_NOZORDER = 0x0004, SWP_FRAMECHANGED = 0x0020, SWP_SHOWWINDOW = 0x0040;
        private const long WS_POPUP = 0x80000000L, WS_VISIBLE = 0x10000000L;

        /// <summary>This game's window borderless over part i of n of the screen (as the soldiers' parts: halves, or quarters for 3-4).</summary>
        private static void PlaceWindow(int i, int n)
        {
            IntPtr hwnd = RawInput.GameWindow();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            Rect part = SplitPlayers.Part(i, n); // (from the bottom, as Unity's viewports)
            int x = Mathf.RoundToInt(part.x * sw), w = Mathf.RoundToInt(part.width * sw), h = Mathf.RoundToInt(part.height * sh);
            int y = Mathf.RoundToInt((1f - part.y - part.height) * sh);
            if (GetWindowRect(hwnd, out RECT r) && r.Left == x && r.Top == y && r.Right - r.Left == w && r.Bottom - r.Top == h)
            {
                return;
            }
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != w || Screen.height != h)
            {
                Screen.SetResolution(w, h, FullScreenMode.Windowed);
            }
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(WS_POPUP | WS_VISIBLE));
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
        }

        private static void RestoreWindow()
        {
            Screen.SetResolution(GetSystemMetrics(0), GetSystemMetrics(1), FullScreenMode.FullScreenWindow);
        }
    }
}
