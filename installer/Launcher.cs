// Ravenfield VR launcher: asks "Play in VR / Play flat" and the VR graphics preset, then starts the game.
// Installed next to ravenfield.exe. Steam runs it through the game's launch option `"...\RavenfieldVR-Launcher.exe" %command%`,
// so pressing Play in Steam opens it; it waits for the game so Steam keeps showing Ravenfield as running.
// The "Ravenfield VR" entry Setup adds to Steam's Play list passes -vr, which starts VR without asking.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Valve.VR;

namespace RavenfieldVR.Launcher
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                var game = GameCommand.FromArgs(args);
                var settings = LauncherSettings.Load(game.GameDir);
                bool showAnyway = (Control.ModifierKeys & Keys.Shift) != 0;
                // Picked in Steam's Play list ("Ravenfield VR" passes -vr): already decided, no need to ask again.
                if (game.Requested.HasValue && !showAnyway)
                {
                    return game.RunChecked(game.Requested.Value, settings.Preset);
                }
                if (settings.Remember && !showAnyway)
                {
                    return game.RunChecked(settings.Mode, settings.Preset);
                }
                using (var form = new LauncherForm(game, settings))
                {
                    Application.Run(form);
                    return form.ExitCode;
                }
            }
            catch (Exception e)
            {
                MessageBox.Show("Ravenfield VR launcher: " + e.Message, "Ravenfield VR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal enum PlayMode { VR, Flat }

    /// <summary>The game executable and its arguments, from Steam's %command% or from the launcher's own folder.</summary>
    internal sealed class GameCommand
    {
        public string Exe;
        public string GameDir;
        public List<string> Args = new List<string>();
        /// <summary>-vr / -novr in the command (Steam's "Ravenfield VR" Play entry passes -vr); null when not given.</summary>
        public PlayMode? Requested;

        public static GameCommand FromArgs(string[] args)
        {
            var cmd = new GameCommand();
            int first = 0;
            if (args.Length > 0 && args[0].EndsWith("ravenfield.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(args[0]))
            {
                cmd.Exe = Path.GetFullPath(args[0]);
                first = 1;
            }
            else
            {
                cmd.Exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ravenfield.exe");
            }
            if (!File.Exists(cmd.Exe))
            {
                throw new FileNotFoundException("ravenfield.exe was not found next to the launcher.", cmd.Exe);
            }
            cmd.GameDir = Path.GetDirectoryName(cmd.Exe);
            for (int i = first; i < args.Length; i++)
            {
                // Our own switches are decided here.
                if (args[i].Equals("-vr", StringComparison.OrdinalIgnoreCase))
                {
                    cmd.Requested = PlayMode.VR;
                }
                else if (args[i].Equals("-novr", StringComparison.OrdinalIgnoreCase))
                {
                    cmd.Requested = PlayMode.Flat;
                }
                else
                {
                    cmd.Args.Add(args[i]);
                }
            }
            return cmd;
        }

        /// <summary>
        /// <see cref="Run"/>, but VR only once a headset is there: started without one, the game came up flat with "VR could not
        /// start: Init_HmdNotFound" (user, 2026-10-19, Quest 3 not connected). Waits for it, or plays flat, or nothing (0).
        /// </summary>
        public int RunChecked(PlayMode mode, string preset)
        {
            if (mode == PlayMode.VR)
            {
                PlayMode? chosen = HeadsetWaitForm.Wait(GameDir);
                if (chosen == null)
                {
                    return 0;
                }
                mode = chosen.Value;
            }
            return Run(mode, preset);
        }

        /// <summary>Starts the game and waits for it, so Steam's "playing" state and overlay stay correct.</summary>
        public int Run(PlayMode mode, string preset)
        {
            if (mode == PlayMode.VR)
            {
                PluginConfig.SetGraphicsPreset(GameDir, preset);
            }
            var all = new List<string>(Args) { mode == PlayMode.VR ? "-vr" : "-novr" };
            var psi = new ProcessStartInfo(Exe, string.Join(" ", all.Select(Quote).ToArray()))
            {
                UseShellExecute = false,
                WorkingDirectory = GameDir
            };
            using (Process p = Process.Start(psi))
            {
                p.WaitForExit();
                return p.ExitCode;
            }
        }

        /// <summary>
        /// Quotes one argument the way CommandLineToArgvW reads it back: backslashes are literal except before a quote,
        /// where they (and the quote) are escaped, so "D:\Mods\" survives instead of swallowing the next argument.
        /// </summary>
        private static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            {
                return a;
            }
            var sb = new System.Text.StringBuilder("\"");
            int slashes = 0;
            foreach (char c in a)
            {
                if (c == '\\')
                {
                    slashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', slashes * 2 + 1).Append('"');
                }
                else
                {
                    sb.Append('\\', slashes).Append(c);
                }
                slashes = 0;
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }
    }

    /// <summary>The launcher's remembered choice, next to the mod's config.</summary>
    internal sealed class LauncherSettings
    {
        public PlayMode Mode = PlayMode.VR;
        public string Preset = "Medium";
        public bool Remember;
        private string _path;

        public static LauncherSettings Load(string gameDir)
        {
            var s = new LauncherSettings { _path = Path.Combine(gameDir, "BepInEx", "config", "RavenfieldVR.launcher.ini") };
            try
            {
                if (File.Exists(s._path))
                {
                    foreach (string line in File.ReadAllLines(s._path))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0)
                        {
                            continue;
                        }
                        string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                        if (key == "mode") s.Mode = value == "Flat" ? PlayMode.Flat : PlayMode.VR;
                        else if (key == "preset" && PluginConfig.Presets.Contains(value)) s.Preset = value;
                        else if (key == "remember") s.Remember = value == "true";
                    }
                }
                else
                {
                    s.Preset = PluginConfig.GetGraphicsPreset(gameDir) ?? s.Preset;
                }
            }
            catch (Exception)
            {
                // A broken settings file just means asking again.
            }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                File.WriteAllText(_path, $"mode={Mode}\r\npreset={Preset}\r\nremember={(Remember ? "true" : "false")}\r\n", new UTF8Encoding(false));
            }
            catch (Exception)
            {
                // Not remembering is harmless.
            }
        }
    }

    /// <summary>Reads and writes the GraphicsPreset entry of the mod's BepInEx config.</summary>
    internal static class PluginConfig
    {
        public static readonly string[] Presets = { "Low", "Medium", "High", "Ultra", "Game" };
        private const string Section = "[Rendering]";
        private const string Key = "GraphicsPreset";

        private static string PathOf(string gameDir) => Path.Combine(gameDir, "BepInEx", "config", "com.qwavey.ravenfieldvr.cfg");

        public static string GetGraphicsPreset(string gameDir)
        {
            string path = PathOf(gameDir);
            if (!File.Exists(path))
            {
                return null;
            }
            bool inSection = false;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.StartsWith("["))
                {
                    inSection = line.Equals(Section, StringComparison.OrdinalIgnoreCase);
                }
                else if (inSection && line.StartsWith(Key + " ") || inSection && line.StartsWith(Key + "="))
                {
                    string value = line.Substring(line.IndexOf('=') + 1).Trim();
                    return Presets.Contains(value) ? value : null;
                }
            }
            return null;
        }

        /// <summary>The render scale each preset picks (the mod's GraphicsPresets profiles).</summary>
        private static readonly Dictionary<string, string> PresetRenderScale = new Dictionary<string, string>
        {
            ["Low"] = "0.8", ["Medium"] = "1", ["High"] = "1.2", ["Ultra"] = "1.4"
        };

        /// <summary>
        /// Sets the preset. A different preset also sets its render scale, as picking it in the headset does (the
        /// mod only reacts to a change made while it runs); launching again with the same preset keeps a render
        /// scale adjusted by hand.
        /// </summary>
        public static void SetGraphicsPreset(string gameDir, string preset)
        {
            bool changed = !string.Equals(GetGraphicsPreset(gameDir), preset, StringComparison.OrdinalIgnoreCase);
            SetEntry(gameDir, Key, preset);
            if (changed && PresetRenderScale.TryGetValue(preset, out string scale))
            {
                SetEntry(gameDir, "RenderScale", scale);
            }
        }

        /// <summary>Sets one [Rendering] entry in place (BepInEx keeps the rest of the file); adds it when missing.</summary>
        private static void SetEntry(string gameDir, string key, string value)
        {
            string path = PathOf(gameDir);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : new List<string>();
            int sectionAt = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("["))
                {
                    if (sectionAt >= 0)
                    {
                        break;
                    }
                    if (line.Equals(Section, StringComparison.OrdinalIgnoreCase))
                    {
                        sectionAt = i;
                    }
                }
                else if (sectionAt >= 0 && (line.StartsWith(key + " ") || line.StartsWith(key + "=")))
                {
                    lines[i] = key + " = " + value;
                    File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
                    return;
                }
            }
            if (sectionAt < 0)
            {
                lines.Add("");
                lines.Add(Section);
                sectionAt = lines.Count - 1;
            }
            lines.Insert(sectionAt + 1, key + " = " + value);
            File.WriteAllLines(path, lines.ToArray(), new UTF8Encoding(false));
        }
    }

    internal static class SteamVR
    {
        public static string Status()
        {
            if (Process.GetProcessesByName("vrserver").Length > 0 || Process.GetProcessesByName("vrmonitor").Length > 0)
            {
                return "SteamVR is running.";
            }
            string vrPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "openvr", "openvrpaths.vrpath");
            return File.Exists(vrPath)
                ? "SteamVR will start when the game starts in VR."
                : "SteamVR was not found. Install it from Steam to play in VR.";
        }
    }

    /// <summary>What the launcher saw and chose, for finding out why a start went the way it did (BepInEx\RavenfieldVR-launcher.log).</summary>
    internal static class LauncherLog
    {
        private static bool _started;

        public static void Write(string gameDir, string line)
        {
            try
            {
                string path = Path.Combine(gameDir, "BepInEx", "RavenfieldVR-launcher.log");
                string text = DateTime.Now.ToString("HH:mm:ss ") + line + "\r\n";
                if (_started)
                {
                    File.AppendAllText(path, text);
                }
                else
                {
                    File.WriteAllText(path, DateTime.Now.ToString("yyyy-MM-dd ") + "launcher started\r\n" + text); // one start per file
                    _started = true;
                }
            }
            catch (Exception)
            {
                // No log is harmless.
            }
        }
    }

    /// <summary>
    /// Asks SteamVR whether a headset is connected, as a background app: that never starts SteamVR and is not shown in the
    /// headset. Uses the game's own openvr_api.dll (Valve's bindings, openvr_api.cs, are built into the launcher).
    /// </summary>
    internal static class HeadsetCheck
    {
        internal enum State { Ready, NoHeadset, NoSteamVR, Unknown }

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        private static bool? _loaded;

        private static bool Load(string gameDir)
        {
            if (_loaded == null)
            {
                string dll = Path.Combine(gameDir, "ravenfield_Data", "Plugins", "x86_64", "openvr_api.dll");
                _loaded = Environment.Is64BitProcess && File.Exists(dll) && LoadLibrary(dll) != IntPtr.Zero;
            }
            return _loaded.Value;
        }

        /// <summary>Unknown (no openvr_api.dll, an error this does not know) lets the game try as before.</summary>
        public static State Probe(string gameDir)
        {
            try
            {
                if (!Load(gameDir) || !OpenVR.IsRuntimeInstalled())
                {
                    return State.Unknown;
                }
                var error = EVRInitError.None;
                CVRSystem system = OpenVR.Init(ref error, EVRApplicationType.VRApplication_Background);
                if (error == EVRInitError.Init_NoServerForBackgroundApp)
                {
                    return State.NoSteamVR;
                }
                if (error == EVRInitError.Init_HmdNotFound || error == EVRInitError.Init_HmdNotFoundPresenceFailed)
                {
                    return State.NoHeadset;
                }
                if (error != EVRInitError.None)
                {
                    return State.Unknown;
                }
                // Connected from here: always let go again, or the launcher stays a SteamVR app for as long as the game runs.
                try
                {
                    if (system == null)
                    {
                        return State.Unknown;
                    }
                    bool hmd = system.IsTrackedDeviceConnected(OpenVR.k_unTrackedDeviceIndex_Hmd)
                               && system.GetTrackedDeviceClass(OpenVR.k_unTrackedDeviceIndex_Hmd) == ETrackedDeviceClass.HMD;
                    return hmd ? State.Ready : State.NoHeadset;
                }
                finally
                {
                    OpenVR.Shutdown();
                }
            }
            catch (Exception)
            {
                return State.Unknown;
            }
        }

        /// <summary>Starts SteamVR (its vrstartup, else through Steam).</summary>
        public static void StartSteamVR()
        {
            try
            {
                string startup = Path.Combine(OpenVR.RuntimePath() ?? "", "bin", "win64", "vrstartup.exe");
                Process.Start(new ProcessStartInfo(File.Exists(startup) ? startup : "steam://rungameid/250820") { UseShellExecute = true });
            }
            catch (Exception)
            {
                // The waiting window says what to do by hand.
            }
        }
    }

    /// <summary>
    /// VR was chosen and no headset is connected: this window waits for it and starts the game in VR by itself once it is
    /// there, or plays flat, or starts anyway, or cancels.
    /// </summary>
    internal sealed class HeadsetWaitForm : Form
    {
        private readonly string _gameDir;
        private readonly Label _status;
        private readonly System.Windows.Forms.Timer _timer = new System.Windows.Forms.Timer { Interval = 1500 };
        private bool _probing;
        private DateTime _steamVRStartedAt = DateTime.MinValue;
        private PlayMode? _choice;

        /// <summary>VR with a headset there (no window), else what the player picks in the window; null to start nothing.</summary>
        public static PlayMode? Wait(string gameDir)
        {
            // At most 4 s: a SteamVR that is starting or hung can keep the connection waiting, and pressing Play then looked dead.
            // Not answered in time, the game tries as it did before this check.
            var probe = System.Threading.Tasks.Task.Run(() => HeadsetCheck.Probe(gameDir));
            HeadsetCheck.State first = probe.Wait(TimeSpan.FromSeconds(4)) ? probe.Result : HeadsetCheck.State.Unknown;
            LauncherLog.Write(gameDir, "VR asked for; SteamVR says: " + first + (probe.IsCompleted ? "" : " (no answer in 4 s)"));
            if (first == HeadsetCheck.State.Ready || first == HeadsetCheck.State.Unknown)
            {
                return PlayMode.VR;
            }
            using (var form = new HeadsetWaitForm(gameDir, first))
            {
                form.ShowDialog();
                LauncherLog.Write(gameDir, "waiting window closed: " + (form._choice?.ToString() ?? "cancelled") + (form._last != first ? " (last: " + form._last + ")" : ""));
                return form._choice;
            }
        }

        private HeadsetCheck.State _last;

        private HeadsetWaitForm(string gameDir, HeadsetCheck.State first)
        {
            _gameDir = gameDir;
            Text = "Ravenfield VR";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            MinimizeBox = true;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = LauncherForm.Panel;
            ForeColor = LauncherForm.TextC;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 262);
            KeyPreview = true;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(Path.Combine(gameDir, "ravenfield.exe"));
            }
            catch (Exception)
            {
                // Default icon.
            }

            Controls.Add(new Label { Text = "Waiting for the headset", Font = new Font("Segoe UI Semibold", 17f), AutoSize = true, Location = new Point(28, 20), ForeColor = LauncherForm.TextC });
            _status = new Label { AutoSize = false, Location = new Point(31, 62), Size = new Size(500, 24), ForeColor = LauncherForm.Accent };
            Controls.Add(_status);
            // Sized to its text, and the buttons and the window follow it on load: a fixed height cut the last line off with
            // the display scaled.
            var hint = new Label
            {
                Text = "Put on your Quest 3 (or other headset) and connect it to this PC: Steam Link, Air Link, a Link cable or Virtual Desktop. " +
                       "Ravenfield starts in VR by itself as soon as SteamVR sees the headset.",
                Location = new Point(31, 90), AutoSize = true, MaximumSize = new Size(500, 0), ForeColor = LauncherForm.Muted, Font = new Font("Segoe UI", 9.25f)
            };
            Controls.Add(hint);

            Button flat = LauncherForm.FlatButton("Play flat", new Rectangle(31, 196, 156, 44), LauncherForm.Raised, LauncherForm.TextC);
            flat.Click += (s, e) => Finish(PlayMode.Flat);
            Button anyway = LauncherForm.FlatButton("Start VR anyway", new Rectangle(199, 196, 170, 44), LauncherForm.Raised, LauncherForm.TextC);
            anyway.Click += (s, e) => Finish(PlayMode.VR);
            Button cancel = LauncherForm.FlatButton("Cancel", new Rectangle(381, 196, 148, 44), LauncherForm.Raised, LauncherForm.TextC);
            cancel.Click += (s, e) => Finish(null);
            Controls.Add(flat);
            Controls.Add(anyway);
            Controls.Add(cancel);
            Load += (s, e) =>
            {
                int top = hint.Bottom + hint.Height / 4;
                foreach (Button b in new[] { flat, anyway, cancel })
                {
                    b.Top = top;
                }
                ClientSize = new Size(ClientSize.Width, flat.Bottom + flat.Height / 2);
            };
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    Finish(null);
                }
            };

            ShowState(first);
            _timer.Tick += async (s, e) =>
            {
                if (_probing)
                {
                    return;
                }
                _probing = true;
                HeadsetCheck.State state = await System.Threading.Tasks.Task.Run(() => HeadsetCheck.Probe(_gameDir));
                _probing = false;
                if (!IsDisposed && _timer.Enabled)
                {
                    ShowState(state);
                }
            };
            _timer.Start();
        }

        private void ShowState(HeadsetCheck.State state)
        {
            if (state != _last)
            {
                LauncherLog.Write(_gameDir, "SteamVR says: " + state);
                _last = state;
            }
            switch (state)
            {
                case HeadsetCheck.State.Ready:
                    _status.Text = "Headset found. Starting Ravenfield in VR...";
                    Finish(PlayMode.VR);
                    break;
                case HeadsetCheck.State.NoSteamVR:
                    // Started at once, and again if it is gone 30 s later (closed, or restarted by Steam Link or Virtual Desktop).
                    if (_steamVRStartedAt == DateTime.MinValue || DateTime.UtcNow - _steamVRStartedAt > TimeSpan.FromSeconds(30))
                    {
                        _steamVRStartedAt = DateTime.UtcNow;
                        HeadsetCheck.StartSteamVR();
                    }
                    _status.Text = "Starting SteamVR...";
                    break;
                case HeadsetCheck.State.NoHeadset:
                    _status.Text = "SteamVR is running, but no headset is connected.";
                    break;
                default:
                    // Could not ask SteamVR any more: let the game try.
                    Finish(PlayMode.VR);
                    break;
            }
        }

        private void Finish(PlayMode? choice)
        {
            _timer.Stop();
            _choice = choice;
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _timer.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>One window, the mod's own look: gunmetal panel, off-white type, one amber accent for the choice.</summary>
    internal sealed class LauncherForm : Form
    {
        internal static readonly Color Panel = Color.FromArgb(27, 30, 33);
        internal static readonly Color Raised = Color.FromArgb(42, 46, 51);
        internal static readonly Color RaisedHover = Color.FromArgb(56, 61, 67);
        internal static readonly Color TextC = Color.FromArgb(226, 228, 223);
        internal static readonly Color Muted = Color.FromArgb(150, 156, 160);
        internal static readonly Color Accent = Color.FromArgb(201, 160, 74);
        internal static readonly Color AccentText = Color.FromArgb(24, 22, 18);

        private readonly GameCommand _game;
        private readonly LauncherSettings _settings;
        private readonly Dictionary<string, Button> _presetButtons = new Dictionary<string, Button>();
        private readonly CheckBox _remember;

        public int ExitCode { get; private set; }

        public LauncherForm(GameCommand game, LauncherSettings settings)
        {
            _game = game;
            _settings = settings;
            Text = "Ravenfield VR";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Panel;
            ForeColor = TextC;
            Font = new Font("Segoe UI", 10f);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(560, 318);
            KeyPreview = true;
            try
            {
                Icon = Icon.ExtractAssociatedIcon(game.Exe);
            }
            catch (Exception)
            {
                // Default icon.
            }

            Controls.Add(new Label { Text = "Ravenfield VR", Font = new Font("Segoe UI Semibold", 20f), AutoSize = true, Location = new Point(28, 20), ForeColor = TextC });
            Controls.Add(new Label { Text = SteamVR.Status(), AutoSize = true, Location = new Point(31, 64), ForeColor = Muted });

            Controls.Add(new Label { Text = "VR graphics", AutoSize = true, Location = new Point(31, 108), ForeColor = TextC });
            int x = 31;
            foreach (string preset in PluginConfig.Presets)
            {
                string p = preset;
                Button b = FlatButton(preset, new Rectangle(x, 134, 92, 36), Raised, TextC);
                b.Click += (s, e) => SelectPreset(p);
                _presetButtons[preset] = b;
                Controls.Add(b);
                x += 100;
            }
            Controls.Add(new Label
            {
                Text = "Low keeps the frame rate highest, Ultra looks sharpest. You can change it in the VR settings in game. \"Game\" uses the game's own graphics menu.",
                Location = new Point(31, 176), Size = new Size(500, 40), ForeColor = Muted, Font = new Font("Segoe UI", 8.75f)
            });

            _remember = new CheckBox
            {
                Text = "Start this way from now on (hold Shift while starting to see this again)",
                AutoSize = true, Location = new Point(31, 220), ForeColor = Muted, Checked = settings.Remember, FlatStyle = FlatStyle.Flat
            };
            Controls.Add(_remember);

            Button vr = FlatButton("Play in VR", new Rectangle(31, 258, 240, 44), Accent, AccentText);
            vr.Font = new Font("Segoe UI Semibold", 11f);
            vr.Click += (s, e) => Play(PlayMode.VR);
            Button flat = FlatButton("Play flat", new Rectangle(287, 258, 242, 44), Raised, TextC);
            flat.Font = new Font("Segoe UI Semibold", 11f);
            flat.Click += (s, e) => Play(PlayMode.Flat);
            Controls.Add(vr);
            Controls.Add(flat);
            AcceptButton = settings.Mode == PlayMode.Flat ? flat : vr;
            SelectPreset(settings.Preset);
            KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape)
                {
                    ExitCode = 0;
                    Close();
                }
            };
        }

        internal static Button FlatButton(string text, Rectangle bounds, Color back, Color fore)
        {
            var b = new Button { Text = text, Bounds = bounds, BackColor = back, ForeColor = fore, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = back == Accent ? Color.FromArgb(214, 176, 96) : RaisedHover;
            b.FlatAppearance.MouseDownBackColor = Accent;
            return b;
        }

        private void SelectPreset(string preset)
        {
            _settings.Preset = preset;
            foreach (KeyValuePair<string, Button> kv in _presetButtons)
            {
                bool on = kv.Key == preset;
                kv.Value.BackColor = on ? Accent : Raised;
                kv.Value.ForeColor = on ? AccentText : TextC;
                kv.Value.FlatAppearance.MouseOverBackColor = on ? Accent : RaisedHover;
            }
        }

        private void Play(PlayMode mode)
        {
            _settings.Mode = mode;
            _settings.Remember = _remember.Checked;
            _settings.Save();
            Hide();
            try
            {
                ExitCode = _game.RunChecked(mode, _settings.Preset);
            }
            catch (Exception e)
            {
                MessageBox.Show("Could not start Ravenfield: " + e.Message, "Ravenfield VR", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ExitCode = 1;
            }
            Close();
        }
    }
}
