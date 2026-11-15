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

        /// <summary>
        /// The host's window this copy's window belongs to (-rfvr-splitowner): kept above it, out of the taskbar and Alt-Tab, so the
        /// screen reads as one window split in parts. (Inside it as a child, Unity's -parentHWND, the copies crashed as their window was
        /// torn down: NtUserDestroyWindow into Unity's own window procedure, 2026-11-15.)
        /// </summary>
        private static readonly long OwnerWindow = ArgLong("-rfvr-splitowner=");

        private static long ArgLong(string prefix)
        {
            string a = Args.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
            return a != null && long.TryParse(a.Substring(prefix.Length), out long v) ? v : 0;
        }

        /// <summary>This copy's host plays in a headset: the monitor is the copies' only.</summary>
        private static readonly bool VrHost = Args.Any(x => x.Equals("-rfvr-splitvrhost", StringComparison.OrdinalIgnoreCase));

        /// <summary>The check: the host starts its copies with their test script (CopyTest), each copy runs it.</summary>
        public static bool TestCopies;

        private static readonly bool Testing = Args.Any(a => a.Equals("-rfvr-splittest", StringComparison.OrdinalIgnoreCase));

        /// <summary>The copies' own log (the check reads it).</summary>
        internal static string CopyLog(int index) => System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, $"RavenfieldVR-split-{index + 1}.log");

        /// <summary>Try mode on the host: player 2 takes the first keyboard and mouse, the host's own player idle (Esc still its).</summary>
        public static bool TryMode;

        /// <summary>A number's bytes written in place (no array made for each: a mouse sends about a thousand events a second).</summary>
        private static void Put(byte[] to, int at, float v, int size) => Put(to, at, (long)new FloatBits { F = v }.I & 0xFFFFFFFFL, size);

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float F;
            [FieldOffset(0)] public int I;
        }

        private static void Put(byte[] to, int at, long v, int size)
        {
            for (int i = 0; i < size; i++)
            {
                to[at + i] = (byte)(v >> (8 * i));
            }
        }

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
            public readonly FlatPlayerInput.DeviceState State = new FlatPlayerInput.DeviceState();
        }

        private static readonly List<Copy> Copies = new List<Copy>();
        private static readonly List<int> Pending = new List<int>();

        /// <summary>Every player's game is in the match: the host says go, the copies show and deploy together (the user, 2026-11-15: "Loading
        /// player... at a fullscreen and only if all players loaded it will deploy everyone").</summary>
        private static bool _go;
        private static float _startedAt, _goAt;
        private static int _ready;

        /// <summary>The players in the match so far (player 1 counted), and all of them.</summary>
        internal static string Loading => _hosting && !_go ? $"Loading players ...   {_ready + 1} of {Splitscreen.Players} in"
            : StartsSplit && !_hosting && !_everHosted ? "Loading ..." : null;

        /// <summary>This game starts straight into a splitscreen match (try mode): its loading screen from its first frame, full screen.</summary>
        internal static readonly bool StartsSplit = Args.Any(a => a.Equals("-rfvr-splittry", StringComparison.OrdinalIgnoreCase));

        private static bool _everHosted, _earlyDone;

        /// <summary>Called as the mod starts: a game that starts straight into splitscreen goes full screen and shows "Loading" at once.</summary>
        internal static void Early()
        {
            if (_earlyDone || !StartsSplit)
            {
                return;
            }
            _earlyDone = true;
            Screen.SetResolution(GetSystemMetrics(0), GetSystemMetrics(1), FullScreenMode.FullScreenWindow);
            if (_hostDrawer == null)
            {
                _hostDrawer = new GameObject("RavenfieldVR Split Host");
                UnityEngine.Object.DontDestroyOnLoad(_hostDrawer);
                _hostDrawer.AddComponent<SplitCopyHost>();
            }
        }

        /// <summary>
        /// While the players load, every copy's window hidden by this game, each frame: a game shows its window itself as it starts, about
        /// 10 s before its mod is up to hide it (the copies showed one by one over "Loading players"; 2026-11-15).
        /// </summary>
        private static void HideCopies()
        {
            if (Copies.Count == 0)
            {
                return;
            }
            var pids = new HashSet<uint>();
            foreach (Copy c in Copies)
            {
                try
                {
                    if (c.Process != null && !c.Process.HasExited)
                    {
                        pids.Add((uint)c.Process.Id);
                    }
                }
                catch (Exception)
                {
                }
            }
            bool stole = false;
            IntPtr front = GetForegroundWindow();
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint pid);
                if (pids.Contains(pid) && IsWindowVisible(h))
                {
                    stole |= h == front;
                    ShowWindow(h, SW_HIDE);
                }
                return true;
            }, IntPtr.Zero);
            // A copy's window took the focus as it showed: back to this one ("the loading screen gets unfocused when the other copies
            // launch", the user 2026-11-15).
            IntPtr mine = RawInput.GameWindow();
            if (mine != IntPtr.Zero)
            {
                // Above every window while the players load (a copy's window, shown for a moment as it starts, does not cover it), and the
                // focus taken back when a copy took it.
                if (!_topmost)
                {
                    _topmost = true;
                    SetWindowPos(mine, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                }
                if (stole || front != mine && Time.unscaledTime - _startedAt < 240f && pids.Contains(FrontPid(front)))
                {
                    TakeFocus(mine);
                }
            }
        }

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        private static bool _topmost;
        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);
        private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001;

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint attach, uint to, bool on);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        private static uint FrontPid(IntPtr front)
        {
            GetWindowThreadProcessId(front, out uint pid);
            return pid;
        }

        /// <summary>
        /// This game's window to the front from behind: Windows refuses SetForegroundWindow to a program not in front, so the window in
        /// front's input is joined for the moment (the usual way; plain SetForegroundWindow did nothing, the loading screen stayed behind).
        /// </summary>
        private static void TakeFocus(IntPtr mine)
        {
            IntPtr front = GetForegroundWindow();
            uint frontThread = GetWindowThreadProcessId(front, out _), own = GetCurrentThreadId();
            bool joined = frontThread != 0 && frontThread != own && AttachThreadInput(frontThread, own, true);
            BringWindowToTop(mine);
            SetForegroundWindow(mine);
            if (joined)
            {
                AttachThreadInput(frontThread, own, false);
            }
        }

        /// <summary>After go: an ordinary window again (above the others no longer), in front.</summary>
        private static void EndTopmost()
        {
            if (!_topmost)
            {
                return;
            }
            _topmost = false;
            IntPtr mine = RawInput.GameWindow();
            if (mine != IntPtr.Zero)
            {
                SetWindowPos(mine, HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
                TakeFocus(mine);
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        /// <summary>Once a frame on the host: the copies' own logs say when each is in the match.</summary>
        private static void CheckReady()
        {
            if (_go || Time.frameCount % 30 != 0)
            {
                return;
            }
            int ready = 0;
            foreach (Copy c in Copies)
            {
                try
                {
                    string log = CopyLog(c.Index);
                    if (System.IO.File.Exists(log) && System.IO.File.ReadAllText(log).Contains("in the host's match"))
                    {
                        ready++;
                    }
                }
                catch (Exception)
                {
                }
            }
            _ready = ready;
            bool all = Pending.Count == 0 && ready >= Splitscreen.Players - 1;
            if (all || Time.unscaledTime - _startedAt > 240f)
            {
                _go = true;
                _goAt = Time.unscaledTime;
                EndTopmost();
                Log.Info(all ? $"Splitscreen: all {Splitscreen.Players} players in: go." : $"Splitscreen: {ready + 1} of {Splitscreen.Players} players in after 4 minutes: go without the rest.");
            }
        }
        private static float _nextStart;
        private static bool _embed;

        /// <summary>The next copy started when the one before has its mod up (its log written) or 25 s have passed.</summary>
        private static void StartNextCopy()
        {
            if (Pending.Count == 0 || Time.unscaledTime < _nextStart)
            {
                return;
            }
            // (5 s apart: the mod loader's files are taken in a game's first seconds only; waiting for each copy's mod to be up made
            // four players slow to start.)
            int i = Pending[0];
            Pending.RemoveAt(0);
            int n = Splitscreen.Players;
            try
            {
                System.IO.File.Delete(CopyLog(i));
            }
            catch (Exception)
            {
            }
            string exe = Process.GetCurrentProcess().MainModule.FileName;
            string mods = Args.FirstOrDefault(a => a.StartsWith("-rfvr-mods=", StringComparison.OrdinalIgnoreCase));
            var c = new Copy { Index = i, Input = new IPEndPoint(IPAddress.Loopback, HostInputPort + i) };
            Rect part = SplitPlayers.Part(Runtime.OpenVRRuntime.Running ? i - 1 : i, Runtime.OpenVRRuntime.Running ? n - 1 : n);
            int pw = Mathf.RoundToInt(part.width * GetSystemMetrics(0)), ph = Mathf.RoundToInt(part.height * GetSystemMetrics(1));
            IntPtr owner = _embed ? RawInput.GameWindow() : IntPtr.Zero;
            // (Borderless at its part's size, Unity's own -popupwindow; owned by this window, so it stays over it as one screen.)
            string args = $"-novr -nointro -screen-fullscreen 0 -popupwindow -screen-width {pw} -screen-height {ph}"
                          + (owner != IntPtr.Zero ? $" -rfvr-splitowner={owner.ToInt64()}" : "")
                          + $" -rfvr-splitclient={i} -rfvr-splitn={n} -rfvr-splithost=127.0.0.1:{GamePort}"
                          + (Runtime.OpenVRRuntime.Running ? " -rfvr-splitvrhost" : "")
                          + (TestCopies ? " -rfvr-splittest" : "")
                          + (mods != null ? " " + mods : "");
            try
            {
                // (Without the mod loader's own variables: inherited, its loader in the copy took itself for already run and loaded no
                // mod, so the copy never joined; 2026-11-14 22:20. Taken out of this game's own environment: they matter only as it
                // starts, and the copy is started through the shell so that it can start hidden.)
                foreach (System.Collections.DictionaryEntry v in Environment.GetEnvironmentVariables())
                {
                    string key = v.Key as string;
                    if (key != null && key.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase))
                    {
                        Environment.SetEnvironmentVariable(key, null);
                    }
                }
                // Hidden from its start (Windows' own start setting, which a game's first window takes): shown when every player is in,
                // over its part, so the screen is one from the start (the user, 2026-11-15: "the windows are only seperated on laoding
                // in ... they should start as one").
                var start = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = System.IO.Path.GetDirectoryName(exe),
                };
                c.Process = Process.Start(start);
                Copies.Add(c);
                Log.Info($"Splitscreen: player {i + 1}'s own copy of the game started ({args}).");
            }
            catch (Exception e)
            {
                Log.Warn($"Splitscreen: player {i + 1}'s copy of the game could not start: {e.Message}");
            }
            _nextStart = Time.unscaledTime + 5f;
        }
        private static bool _hosting, _weHosted, _f12Was, _hostRanInBackground;
        /// <summary>Try mode: the player the one keyboard and mouse play now (0: player 1; F12 the next).</summary>
        private static int _tryPlayer = 1;
        private static float _tryNoteUntil;

        /// <summary>Changes when a player's keyboard and mouse change hands (F12 in try mode): the copy forgets its mouse's last totals.</summary>
        private static byte _generation;

        /// <summary>The host's line in try mode: whose keyboard it is now.</summary>
        internal static string TryLine => TryMode && _hosting
            ? (Time.unscaledTime < _tryNoteUntil ? $"Your keyboard and mouse now play PLAYER {_tryPlayer + 1}" : $"Try mode: your keyboard and mouse play player {_tryPlayer + 1}.   F12: the next player")
            : null;
        private static UdpClient _hostSocket;
        private static GameObject _hostDrawer;
        private static readonly byte[] Packet = new byte[64];

        /// <summary>Whether the copies are running (the Local page shows it).</summary>
        public static bool Running => _hosting;

        private static void HostUpdate()
        {
            // Started in a match; kept through the next map's load (the copies follow the host as any joined player does: closing
            // them at each match's end made every map a fresh start of every copy; scout, 2026-11-14), stopped when splitscreen is
            // turned off or the host is back in the main menu.
            bool on = Splitscreen.On && Splitscreen.FullGames && !(Multiplayer.Active && !Multiplayer.Hosting);
            if (on && !_hosting && SplitPlayers.InMatch)
            {
                HostStart();
            }
            else if (_hosting && (!on || GameManager.IsInMainMenu()))
            {
                HostStop();
            }
            if (!_hosting)
            {
                return;
            }
            StartNextCopy();
            CheckReady();
            if (!_go)
            {
                HideCopies();
            }
            Copies.RemoveAll(c => c.Process == null || c.Process.HasExited);
            if (_embed)
            {
                SplitPlayers.OwnViewInto(SplitPlayers.Part(0, Splitscreen.Players)); // (the copies' windows over the other parts)
            }
            else if (!Runtime.OpenVRRuntime.Running)
            {
                Window(0, Splitscreen.Players);
            }
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
                _weHosted = Multiplayer.LanHosting;
                if (!_weHosted)
                {
                    Multiplayer.Notice($"Splitscreen: port {GamePort} could not be hosted on this PC (in use?): the copies cannot join.");
                    Splitscreen.On = false;
                    return;
                }
            }
            // (Kept running behind the copies' windows: Unity stops an unfocused game, and with the host stopped every copy lost its
            // keys and closed after 10 s when its player clicked into it; scout, 2026-11-14.)
            _hostRanInBackground = Application.runInBackground;
            Application.runInBackground = true;
            _hosting = true;
            int n = Splitscreen.Players;
            // One screen: this game borderless over the whole screen, its own view in its part; each copy's window owned by it, borderless
            // over its part, out of the taskbar. Four windows were confusing (the user, 2026-11-15: "can we make it one but its splitten?").
            // In VR the headset is player 1: the copies share the monitor, each its own window.
            _embed = !Runtime.OpenVRRuntime.Running;
            if (_embed)
            {
                Screen.SetResolution(GetSystemMetrics(0), GetSystemMetrics(1), FullScreenMode.FullScreenWindow);
            }
            // The copies one after another, each once the one before has its mod up: started together, two of three found the mod
            // loader's files taken ("sharing violation") and stayed in the menu with no mod (2026-11-15).
            Pending.Clear();
            _go = false;
            _everHosted = true;
            _ready = 0;
            _startedAt = Time.unscaledTime;
            for (int i = 1; i < n; i++)
            {
                Pending.Add(i);
            }
            _nextStart = Time.unscaledTime + (_embed ? 1.5f : 0f); // (the window full screen first: its handle given to the copies)
            try
            {
                _hostSocket = Quiet(new UdpClient(new IPEndPoint(IPAddress.Loopback, HostInputPort)));
            }
            catch (Exception e)
            {
                Log.Warn("Splitscreen: the copies' input port could not be opened: " + e.Message);
            }
            RawInput.Start();
            if (_hostDrawer == null)
            {
                _hostDrawer = new GameObject("RavenfieldVR Split Host");
                UnityEngine.Object.DontDestroyOnLoad(_hostDrawer);
                _hostDrawer.AddComponent<SplitCopyHost>();
            }
            SplitPlayers.Patch(); // (the game's merged controllers muted: each copy reads its own)
            SplitPlayers.GamePadsMuted = true;
            FlatPlayerInput.Keys = 0;
            FlatPlayerInput.Pad = 0;
            FlatPlayerInput.OnlyPause = TryMode;
            _placedScene = null; // (placed from now: Window)
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
            Pending.Clear();
            EndTopmost();
            if (_embed)
            {
                SplitPlayers.OwnViewBack();
                _embed = false;
            }
            _hostSocket?.Close();
            _hostSocket = null;
            _hosting = false;
            Application.runInBackground = _hostRanInBackground;
            FlatPlayerInput.Active = false;
            FlatPlayerInput.Keys = 0;
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
            if (TryMode)
            {
                // F12 hands the one keyboard and mouse to the other player (to try both with one keyboard).
                RawDevice k0 = RawInput.Nth(false, 0);
                bool f12 = k0 != null && k0.Keys[0x7B];
                if (f12 && !_f12Was)
                {
                    _tryPlayer = (_tryPlayer + 1) % Math.Max(2, Splitscreen.Players);
                    _generation++;
                    FlatPlayerInput.ResetMouse();
                    _tryNoteUntil = Time.unscaledTime + 4f;
                    Log.Info($"Splitscreen try: the keyboard and mouse now play player {_tryPlayer + 1}.");
                }
                _f12Was = f12;
                // (While the keyboard is player 2's, the host's player gets none of it, Esc either: it paused both games; F12 brings it back.)
                FlatPlayerInput.OnlyPause = false;
                FlatPlayerInput.Keys = _tryPlayer == 0 ? 0 : -1;
            }
            // No game window in front (another program): every key and button let go, as Windows does not tell the game their release
            // (held W kept a soldier walking after an alt-tab; scout, 2026-11-14).
            if (!AnyGameInFront())
            {
                RawInput.LetGo();
            }
            foreach (Copy c in Copies)
            {
                int keys = TryMode ? 0 : c.Index;
                if (TryMode && _tryPlayer != c.Index)
                {
                    // (Its player gets nothing meanwhile: its keys let go, its mouse where it was.)
                    Array.Clear(c.State.Keys, 0, 256);
                    c.State.Left = c.State.Right = c.State.Middle = c.State.X1 = c.State.X2 = false;
                }
                else
                {
                    c.State.CopyFrom(RawInput.Nth(false, keys), RawInput.Nth(true, keys));
                }
                int length = Write(c.State);
                try
                {
                    _hostSocket?.Send(Packet, length, c.Input);
                }
                catch (Exception)
                {
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        private static float _nextFrontLook;
        private static bool _frontWasGame = true;

        /// <summary>Whether the window in front is this game or one of its copies (asked a few times a second).</summary>
        private static bool AnyGameInFront()
        {
            if (Time.unscaledTime < _nextFrontLook)
            {
                return _frontWasGame;
            }
            _nextFrontLook = Time.unscaledTime + 0.2f;
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            bool game = pid == (uint)Process.GetCurrentProcess().Id;
            foreach (Copy c in Copies)
            {
                game |= c.Process != null && pid == (uint)c.Process.Id;
            }
            _frontWasGame = game;
            return game;
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
                string file = CopyLog(ClientIndex);
                System.IO.File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (Exception)
            {
            }
        }

        /// <summary>A player's keyboard and mouse as a packet: 'K', 32 bytes of keys (a bit each), the mouse's buttons, its totals and wheel.</summary>
        private static int Write(FlatPlayerInput.DeviceState d)
        {
            int at = 0;
            Packet[at++] = (byte)'K';
            for (int i = 0; i < 32; i++)
            {
                byte bits = 0;
                for (int j = 0; j < 8; j++)
                {
                    if (d.Keys[i * 8 + j])
                    {
                        bits |= (byte)(1 << j);
                    }
                }
                Packet[at++] = bits;
            }
            Packet[at++] = (byte)((d.Left ? 1 : 0) | (d.Right ? 2 : 0) | (d.Middle ? 4 : 0) | (d.X1 ? 8 : 0) | (d.X2 ? 16 : 0));
            Put(Packet, at, d.TotalX, 4);
            at += 4;
            Put(Packet, at, d.TotalY, 4);
            at += 4;
            Put(Packet, at, d.Wheel, 4);
            at += 4;
            Packet[at++] = _generation;
            Packet[at++] = (byte)(_go ? 1 : 0);
            return at;
        }

        // ---- a copy: player 2 to 4 ---------------------------------------------------------------------------------------------------

        private static UdpClient _clientSocket;
        private static IPEndPoint _hostInput;
        private static bool _joined, _clientSetUp, _heardHost, _saidMatch, _saidDeploy, _goFromHost, _shown;
        private static float _nextPlace;
        private static float _joinAt = -1f, _lastFromHost, _placedUntil;
        private static readonly FlatPlayerInput.DeviceState Received = new FlatPlayerInput.DeviceState();

        private static void ClientUpdate()
        {
            if (!_clientSetUp)
            {
                _clientSetUp = true;
                Application.runInBackground = true;
                // Light on the PC: four whole games share it, and a copy's view is a part of the screen (the user: "the copys are also
                // very slow").
                QualitySettings.SetQualityLevel(0, true);
                QualitySettings.shadows = ShadowQuality.Disable;
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = 60;
                _clientSocket = Quiet(new UdpClient(new IPEndPoint(IPAddress.Loopback, HostInputPort + ClientIndex)));
                _hostInput = new IPEndPoint(IPAddress.Loopback, HostInputPort);
                RawInput.Start();
                RawInput.Forward = ForwardToHost;
                SplitPlayers.Patch();
                SplitPlayers.GamePadsMuted = true;
                FlatPlayerInput.Keys = -1;
                FlatPlayerInput.Pad = ClientIndex;
                FlatPlayerInput.Receive(Received);
                SplitCopyMenu.Hook(Note);
                var drawer = new GameObject("RavenfieldVR Split Copy");
                UnityEngine.Object.DontDestroyOnLoad(drawer);
                drawer.AddComponent<SplitCopyHost>();
                _lastFromHost = Time.unscaledTime + 60f; // (time to load and join before the host is missed)
                _placedUntil = Time.unscaledTime + 30f;
                Note($"Splitscreen: this is player {ClientIndex + 1}'s copy of the game; joining {HostAddress} once the menu is up.");
            }
            AudioListener.volume = 0f; // (the host plays the match's sound)
            // A picture of this copy's own view on request (a file BepInEx/RavenfieldVR-split-N.shot): its window's own frame, for checks.
            if (Time.frameCount % 60 == 0)
            {
                string flag = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, $"RavenfieldVR-split-{ClientIndex + 1}.shot");
                if (System.IO.File.Exists(flag))
                {
                    try
                    {
                        System.IO.File.Delete(flag);
                        ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, $"RavenfieldVR-split-{ClientIndex + 1}.png"));
                        Note($"Splitscreen: picture taken; in match {SplitPlayers.InMatch}, player {(LocalPlayer.actor == null ? "none" : LocalPlayer.actor.dead ? "down" : "up")}, loadout screen {(LoadoutUi.instance != null && LoadoutUi.IsOpen() ? "open" : "closed")}, menu {(SplitCopyMenu.Open ? "open" : "closed")}, paused {GameManager.IsPaused()}");
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            // The window in its part of the screen, looked at once a second for as long as it plays (the game set its own size again
            // as it loaded and changed maps: the user saw the windows not in their halves, 2026-11-14).
            // Hidden while the players load (the host shows "Loading players"), shown and placed when the host says go. Kept hidden: the
            // game shows its window again as it loads.
            IntPtr own = RawInput.GameWindow();
            if (own != IntPtr.Zero && !_goFromHost && IsWindowVisible(own))
            {
                ShowWindow(own, SW_HIDE);
            }
            if (own != IntPtr.Zero && _shown != _goFromHost)
            {
                _shown = _goFromHost;
                ShowWindow(own, _shown ? SW_SHOWNOACTIVATE : SW_HIDE);
                if (_shown)
                {
                    _placedScene = null; // (placed again now: Window)
                    SplitDeploy.DeployNow = true; // (every player deployed together)
                    Note("Splitscreen: go from the host: shown and deploying.");
                }
            }
            if (_shown)
            {
                MakeOwned();
                if (VrHost)
                {
                    Window(ClientIndex - 1, ClientCount - 1); // (the host plays in the headset: the copies share the monitor)
                }
                else
                {
                    Window(ClientIndex, ClientCount);
                }
            }
            // No pointer here (the keys work the menus): the cursor not drawn over this window, and never held by it (held, it stayed
            // in the middle of the screen with no game in front; the user, 2026-11-15).
            if (SplitPlayers.InMatch)
            {
                Cursor.visible = false;
                if (Cursor.lockState != CursorLockMode.None)
                {
                    Cursor.lockState = CursorLockMode.None;
                }
                // The game's loadout screen needs the pointer: closed while this player is alive (its weapons are picked in its Esc menu).
                if (LocalPlayer.actor != null && !LocalPlayer.actor.dead && LoadoutUi.instance != null && LoadoutUi.IsOpen())
                {
                    LoadoutUi.Hide();
                }
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
                if (p.Length >= PacketLength && p[0] == (byte)'K')
                {
                    if (!_heardHost)
                    {
                        _heardHost = true;
                        Note("Splitscreen: the host's input for this player is coming in.");
                    }
                    if (!CopyTest.Running)
                    {
                        Read(p); // (a copy under test plays from its script instead, through the same binds)
                    }
                    _lastFromHost = Time.unscaledTime;
                }
            }
            if (!_saidMatch && Time.realtimeSinceStartup > 180f)
            {
                Note("Splitscreen: not in the host's match 3 minutes after starting; this copy closes.");
                Application.Quit();
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
            if (playing && _goFromHost)
            {
                SplitDeploy.Update(Note);
            }
            if (Testing && SplitPlayers.InMatch)
            {
                CopyTest.Update(Received, Note);
            }
        }

        private const int PacketLength = 1 + 32 + 1 + 12 + 1 + 1;
        private static int _lastGeneration = -1;

        private static void Read(byte[] p)
        {
            int at = 1;
            for (int i = 0; i < 32; i++)
            {
                byte bits = p[at++];
                for (int j = 0; j < 8; j++)
                {
                    Received.Keys[i * 8 + j] = (bits & (1 << j)) != 0;
                }
            }
            byte b = p[at++];
            Received.Left = (b & 1) != 0;
            Received.Right = (b & 2) != 0;
            Received.Middle = (b & 4) != 0;
            Received.X1 = (b & 8) != 0;
            Received.X2 = (b & 16) != 0;
            Received.TotalX = BitConverter.ToSingle(p, at);
            at += 4;
            Received.TotalY = BitConverter.ToSingle(p, at);
            at += 4;
            Received.Wheel = BitConverter.ToInt32(p, at);
            at += 4;
            if (p[at] != _lastGeneration)
            {
                _lastGeneration = p[at];
                FlatPlayerInput.ResetMouse(); // (the keyboard and mouse changed hands: no turn from the time it was the other player's)
            }
            at++;
            _goFromHost = p[at] != 0;
        }

        private static readonly byte[] Event = new byte[26];

        /// <summary>This copy is in front: its events go to the host, which tells the keyboards and mice apart for every player.</summary>
        private static void ForwardToHost(long device, bool mouse, ushort flags, ushort buttons, short data, int x, int y, ushort vkey)
        {
            Event[0] = (byte)'E';
            Put(Event, 1, device, 8);
            Event[9] = (byte)(mouse ? 1 : 0);
            Put(Event, 10, flags, 2);
            Put(Event, 12, buttons, 2);
            Put(Event, 14, data, 2);
            Put(Event, 16, x, 4);
            Put(Event, 20, y, 4);
            Put(Event, 24, vkey, 2);
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
        private const long WS_POPUP = 0x80000000L, WS_VISIBLE = 0x10000000L, WS_EX_TOOLWINDOW = 0x80L, WS_EX_APPWINDOW = 0x40000L;
        private const int GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8, SW_HIDE = 0, SW_SHOWNOACTIVATE = 4;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        private static bool _owned;

        /// <summary>This copy's window owned by the host's: above it always, not in the taskbar or Alt-Tab (once).</summary>
        private static void MakeOwned()
        {
            if (_owned || OwnerWindow == 0)
            {
                return;
            }
            IntPtr hwnd = RawInput.GameWindow();
            if (hwnd == IntPtr.Zero)
            {
                return;
            }
            _owned = true;
            long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr((ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW));
            SetWindowLongPtr(hwnd, GWLP_HWNDPARENT, new IntPtr(OwnerWindow));
        }

        /// <summary>This game's window borderless over part i of n of the screen (as the soldiers' parts: halves, or quarters for 3-4).</summary>
        /// <summary>
        /// Keeps this game's window over part i of n of the screen (halves, or quarters for 3-4): looked at a few times after it starts
        /// and after each map's load (Window), then left alone. Forced once a second it jumped between the middle and its half: Unity
        /// applies a new resolution a frame later, centred and framed, so every look found it moved and moved it again (the user,
        /// 2026-11-14: "jumping left to the middle of my screen the whole time").
        /// </summary>
        private static bool PlaceWindow(int i, int n)
        {
            IntPtr hwnd = RawInput.GameWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
            Rect part = SplitPlayers.Part(i, n); // (from the bottom, as Unity's viewports)
            int x = Mathf.RoundToInt(part.x * sw), w = Mathf.RoundToInt(part.width * sw), h = Mathf.RoundToInt(part.height * sh);
            int y = Mathf.RoundToInt((1f - part.y - part.height) * sh);
            if (GetWindowRect(hwnd, out RECT r) && r.Left == x && r.Top == y && r.Right - r.Left == w && r.Bottom - r.Top == h)
            {
                return true;
            }
            if (Screen.fullScreenMode != FullScreenMode.Windowed || Screen.width != w || Screen.height != h)
            {
                if (Time.frameCount - _sizedAt > 90)
                {
                    Screen.SetResolution(w, h, FullScreenMode.Windowed);
                    _sizedAt = Time.frameCount;
                }
                return false; // (moved once Unity has applied the size: a few frames on)
            }
            if (Time.frameCount - _sizedAt < 5)
            {
                return false;
            }
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(WS_POPUP | WS_VISIBLE));
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, SWP_NOZORDER | SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            return false;
        }

        private static int _sizedAt = -1000;
        private static float _placeUntil, _nextLook;
        private static string _placedScene;

        /// <summary>Once a frame: the window placed for 12 s after it starts and after each new map, a look every half second, then left alone.</summary>
        private static void Window(int i, int n)
        {
            string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            if (scene != _placedScene)
            {
                _placedScene = scene;
                _placeUntil = Time.unscaledTime + 12f;
            }
            if (Time.unscaledTime > _placeUntil || Time.unscaledTime < _nextLook)
            {
                return;
            }
            _nextLook = Time.unscaledTime + 0.25f;
            if (PlaceWindow(i, n) && Time.unscaledTime < _placeUntil - 2f)
            {
                _placeUntil = Time.unscaledTime + 2f; // (in place: looked at a little longer, then left)
            }
        }

        private static void RestoreWindow()
        {
            Screen.SetResolution(GetSystemMetrics(0), GetSystemMetrics(1), FullScreenMode.FullScreenWindow);
        }
    }

    /// <summary>A copy's own drawing: what to press while down (SplitDeploy).</summary>
    internal sealed class SplitCopyHost : MonoBehaviour
    {
        private static GUIStyle _style;

        private static GUIStyle _loading;

        private void OnGUI()
        {
            string loading = SplitGames.Loading;
            if (loading != null)
            {
                // Over the whole screen while the players' games load.
                if (_loading == null)
                {
                    _loading = new GUIStyle(GUI.skin.label) { fontSize = 40, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                    _loading.normal.textColor = Color.white;
                }
                GUI.depth = -1000;
                GUI.color = Color.black;
                GUI.DrawTexture(new Rect(0f, 0f, Screen.width, Screen.height), Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(new Rect(0f, 0f, Screen.width, Screen.height), loading, _loading);
                return;
            }
            SplitDeploy.Draw();
            SplitCopyMenu.Draw();
            string line = SplitGames.TryLine;
            if (line != null)
            {
                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label) { fontSize = 18, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                    _style.normal.textColor = Color.white;
                }
                var r = new Rect(0f, 70f, Screen.width, 34f);
                GUI.color = new Color(0f, 0f, 0f, 0.5f);
                GUI.DrawTexture(r, Texture2D.whiteTexture);
                GUI.color = Color.white;
                GUI.Label(r, line, _style);
            }
        }
    }
}
