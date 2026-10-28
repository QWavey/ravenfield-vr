using System;
using System.Collections;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using RavenfieldVR.UI;
using RavenfieldVR.Util;
using RavenfieldVR.Weapons;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// Boots SteamVR and drives every VR system in a fixed order each frame:
    /// Update (input, rig, interactions) -> LateUpdate (camera suppression, post-animation logic)
    /// -> onBeforeRender (final poses for rendering: rig, hands, weapon, UI).
    /// </summary>
    public sealed class VRManager : MonoBehaviour
    {
        public static VRManager Instance { get; private set; }
        public static bool Active => Instance != null && Instance._active;

        public bool RequestVR;
        /// <summary>
        /// Why VR was switched off before it started (the install check, a failed patch), shown on the screen: the restart or
        /// reinstall it asks for was only in the log, and the game simply came up flat (round-4 scout). Null when VR was not
        /// asked for by the player's own choice (EnableVR off), which needs no notice.
        /// </summary>
        public string StartProblem;

        public VRRig Rig { get; private set; }
        public WeaponSystem Weapons { get; private set; }
        public UISystem UI { get; private set; }

        private bool _active;
        private bool _subscribed;

        private void Awake()
        {
            Instance = this;
            // The notice below draws with GUI, not GUILayout: no layout pass for it (it ran every frame, in VR too, where
            // OnGUI returns at once).
            useGUILayout = false;
        }

        private IEnumerator Start()
        {
            // Let the graphics device finish its first frame before the XR display attaches to it.
            yield return null;
            yield return new WaitForEndOfFrame();
            if (!RequestVR)
            {
                Log.Info("VR disabled (config or install check); running flat.");
                if (StartProblem != null)
                {
                    FlatNotice(StartProblem, retry: false); // (F6 cannot mend an install or a patch: no Try again)
                }
                yield break;
            }
            if (!_retry && !ShouldStartVR(out string reason))
            {
                Log.Info("Not starting VR: " + reason + " Launch with -vr to force it.");
                if (reason != null && !reason.StartsWith("-novr", StringComparison.Ordinal))
                {
                    FlatNotice("VR not started: " + reason + "\nStart SteamVR with the headset connected, then press Try again (F6).");
                }
                yield break;
            }
            if (!OpenVRRuntime.Start())
            {
                Log.Warn("Continuing in flat mode: " + OpenVRRuntime.LastError);
                FlatNotice("VR could not start: " + OpenVRRuntime.LastError + "\nConnect the headset and check SteamVR, then press Try again (F6).");
                yield break;
            }
            _flatNotice = null;

            bool inputReady;
            try
            {
                inputReady = VRInput.Initialize();
                Rig = new VRRig();
                Weapons = new WeaponSystem(Rig);
                UI = new UISystem(Rig);
                CacheFrameDelegates();
            }
            catch (Exception e)
            {
                Log.Exception("Creating VR systems failed; shutting VR down", e);
                Shutdown();
                // Re-arm the on-screen notice and the F6 retry: without this the game ran flat with the failure only in the
                // log (and VRInput.Initialize, which had been outside the try, left SteamVR up and unpumped if it threw).
                FlatNotice("VR could not be set up: " + e.Message + "\nPress Try again (F6).");
                yield break;
            }

            VRGameInput.Enabled = inputReady;
            _active = true;
            if (!inputReady)
            {
                // VR runs, the controllers do nothing: said in the headset and on the monitor, not only in the log (round-4 scout).
                string why = "Controller input did not start: " + (VRInput.InitError ?? "see the BepInEx log.") + " Restart SteamVR, then the game.";
                Log.Guard("Input notice", () => UI?.Notice?.Show(why, 30f));
                _activeNotice = why;
                _flatNoticeUntil = Time.unscaledTime + 40f;
            }
            FramePhases.Install(); // (where each frame's time goes, in the once-a-minute frame rate line)
            ScriptKeys.Enabled = true;
            Application.onBeforeRender += OnBeforeRender;
            SceneManager.activeSceneChanged += OnSceneChanged;
            OpenVRRuntime.DashboardChanged += OnDashboard;
            OpenVRRuntime.HeadsetWornChanged += OnHeadsetWorn;
            VRConfig.RenderScale.SettingChanged += OnRenderSettingChanged;
            VRConfig.Mirror.SettingChanged += OnRenderSettingChanged;
            VRConfig.Graphics.SettingChanged += OnGraphicsPresetChanged;
            // Subscribed is set first and the presets guarded: a preset that threw left every handler above subscribed for good
            // (Shutdown unsubscribes only when set) and the warm-up never started (lifecycle hunt, 2026-10-20).
            _subscribed = true;
            Log.Guard("GraphicsPresets.Apply", GraphicsPresets.Apply);
            Log.Info($"VR active ({Time.realtimeSinceStartup:0.0} s after the game started).");
            // Content may have finished loading before VR came up; the warm-up skips rigs already analysed.
            WeaponWarmup.Start();
        }

        /// <summary>
        /// OpenVR initialisation launches SteamVR, so only do it when VR is clearly wanted: a headset is present,
        /// SteamVR is already running, or the game was started with -vr. -novr always plays flat.
        /// </summary>
        private static bool ShouldStartVR(out string reason)
        {
            string[] args = Environment.GetCommandLineArgs();
            bool Has(string flag) => Array.Exists(args, a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
            if (Has("-novr"))
            {
                reason = "-novr on the command line.";
                return false;
            }
            if (Has("-vr"))
            {
                reason = null;
                return true;
            }
            try
            {
                System.Diagnostics.Process[] servers = System.Diagnostics.Process.GetProcessesByName("vrserver");
                bool serverUp = servers.Length > 0;
                foreach (System.Diagnostics.Process p in servers)
                {
                    p.Dispose(); // each holds an OS handle
                }
                if (serverUp)
                {
                    reason = null;
                    return true;
                }
                // IsHmdPresent() can report true for an installed-but-idle headset driver (Oculus/Link, Steam Link),
                // and VR_Init would then pop SteamVR open during flat play, so probing is opt-in.
                if (VRConfig.AutoStartSteamVR.Value && Valve.VR.OpenVR.IsRuntimeInstalled() && Valve.VR.OpenVR.IsHmdPresent())
                {
                    reason = null;
                    return true;
                }
            }
            catch (Exception e)
            {
                reason = "could not query SteamVR (" + e.Message + ").";
                return false;
            }
            reason = "SteamVR is not running.";
            return false;
        }

        // Flat play because VR did not start: said on the screen (it was only a log line, and the game simply came up flat
        // when SteamVR had not been started first), with F6 to try again once SteamVR runs (F7-F11 are the game's own).
        private string _flatNotice;
        private string _noticeFor, _noticeText;
        private float _flatNoticeUntil;
        private bool _retry, _retrying;
        private GUIStyle _noticeStyle, _noticeButton;

        /// <summary>A notice on the monitor with VR running (the controllers' input did not start), for 40 s.</summary>
        private string _activeNotice;
        /// <summary>The notice offers Try again (F6): not for what only a restart or a reinstall mends.</summary>
        private bool _noticeRetry = true;

        private void FlatNotice(string text, bool retry = true)
        {
            _flatNotice = text;
            _flatNoticeUntil = Time.unscaledTime + 40f;
            _retrying = false;
            _noticeRetry = retry;
        }

        private void OnGUI()
        {
            // Up while the cursor is free (the menus: its buttons can be clicked there); in a match, where the cursor is locked,
            // for 40 s, and F6 still tries again after that.
            string notice = _active ? _activeNotice : _flatNotice;
            if (notice == null || (_active || Cursor.lockState == CursorLockMode.Locked) && Time.unscaledTime > _flatNoticeUntil)
            {
                if (_active)
                {
                    _activeNotice = null; // (once gone, gone: OnGUI returns at once again)
                }
                return;
            }
            // The styles made once (GUI.skin is only there inside OnGUI): new ones on each of OnGUI's calls, several a frame.
            if (_noticeStyle == null)
            {
                _noticeStyle = new GUIStyle(GUI.skin.box) { fontSize = 20, alignment = TextAnchor.MiddleCenter, wordWrap = true };
                _noticeStyle.normal.textColor = Color.white;
                _noticeButton = new GUIStyle(GUI.skin.button) { fontSize = 18 };
            }
            // 120 px: three lines and the title (at 90 the last line was cut off; user, 2026-10-19).
            var box = new Rect(Screen.width * 0.15f, 20f, Screen.width * 0.7f, 120f);
            if (!ReferenceEquals(_noticeFor, notice))
            {
                _noticeFor = notice; // the box's text made when the notice changes, not on each of OnGUI's calls
                _noticeText = "Ravenfield VR\n" + notice;
            }
            GUI.Box(box, _noticeText, _noticeStyle);
            if (_retrying)
            {
                return;
            }
            GUIStyle button = _noticeButton;
            const float w = 220f, h = 40f;
            if (_active || !_noticeRetry)
            {
                // Nothing to try again: one button that puts the notice away.
                if (GUI.Button(new Rect(Screen.width * 0.5f - w * 0.5f, box.yMax + 8f, w, h), "OK", button))
                {
                    _activeNotice = null;
                    _flatNotice = null;
                }
                return;
            }
            // Try again starts VR in this game (no restart): connect the headset, then press it (user, 2026-10-19).
            if (GUI.Button(new Rect(Screen.width * 0.5f - w - 8f, box.yMax + 8f, w, h), "Try again (F6)", button))
            {
                RetryVR();
            }
            if (GUI.Button(new Rect(Screen.width * 0.5f + 8f, box.yMax + 8f, w, h), "Play flat", button))
            {
                _flatNotice = null;
            }
        }

        private void RetryVR()
        {
            Log.Info("Trying VR again.");
            _retrying = true;
            _retry = true;
            _flatNotice = "Starting VR...";
            StartCoroutine(Start());
        }

        private void Update()
        {
            if (!_active)
            {
                if (_flatNotice != null && _noticeRetry && !_retrying && UnityEngine.Input.GetKeyDown(KeyCode.F6))
                {
                    RetryVR();
                }
                return;
            }
            ModTime.Start();
            _modFrames++;
            PerfProbe.Frame();
            Log.Guard("Frame budget", FrameBudgetTick);
            // Guarded as the rest: one that threw ended the frame's Update before the input, rig and weapons ran.
            Log.Guard("VR events", PumpVREvents);
            if (_pauseAfterLoad)
            {
                Log.Guard("Pause after load", _pauseAfterLoadCheck ?? (_pauseAfterLoadCheck = PauseAfterLoad));
            }
            Log.Guard("Frame pacing", PaceFrames);
            Log.Guard("Input", _inputUpdate);
            Log.Guard("Rig.Update", _rigUpdate);
            Log.Guard("UI.Update", _uiUpdate);
            Log.Guard("Weapons.Update", _weaponsUpdate);
            ModTime.Stop();
        }

        // The mod's own main-thread time: its Update, LateUpdate and before-render work (not its patches inside the game's code
        // nor its coroutines). Logged by the input check beside the fps (the GPU time SteamVR reports through Link was 2-3 ms
        // at 50 fps in play, so whether the frame is the game's or the mod's had to be measured; task 322).
        private static readonly System.Diagnostics.Stopwatch ModTime = new System.Diagnostics.Stopwatch();
        private static long _modFrames;

        /// <summary>
        /// The mod's main-thread ms a frame since this caller's last call: each caller keeps its own marks (the input check every
        /// 2 s and the minute's frame-rate line shared one counter that each reset, so the minute's line said the last 2 s only
        /// while the input check ran; review, 2026-10-24).
        /// </summary>
        internal static float ModMsPerFrameSince(ref double msMark, ref long frameMark)
        {
            double ms = ModTime.Elapsed.TotalMilliseconds;
            long frames = _modFrames;
            float perFrame = frames > frameMark ? (float)((ms - msMark) / (frames - frameMark)) : 0f;
            msMark = ms;
            frameMark = frames;
            return perFrame;
        }

        // Per-frame work goes to Log.Guard as delegates made once: passing a method group allocates a new delegate
        // (garbage) on every call, ten times a frame.
        private Action _inputUpdate, _rigUpdate, _uiUpdate, _weaponsUpdate, _suppressor, _weaponsLate, _rigRender, _weaponsRender, _uiRender, _lensRender;
        private Action[] _handApply;
        private static readonly Action PumpVREvents = OpenVRRuntime.PumpEvents, PaceFrames = OpenVRRuntime.EnforceFramePacing, FrameBudgetTick = FrameBudget.Frame;

        private static void UpdateInput()
        {
            VRInput.Update();
            try
            {
                VRGameInput.Compute();
            }
            catch (Exception e)
            {
                // Caught here so the script keys below still run: thrown each frame, it left a weapon script's key tap held down
                // for good (input hunt, 2026-10-21).
                Log.ExceptionOnce("VRGameInput.Compute", e);
            }
            ScriptKeys.Update();
            // Mod scripts that read Shift for sprinting (tactical sprint animations) see the VR sprint.
            ScriptKeys.SetHeld("left shift", VRGameInput.SprintActive && Util.GameContext.PlayerOnFoot);
        }

        private void CacheFrameDelegates()
        {
            _inputUpdate = UpdateInput;
            _rigUpdate = Rig.Update;
            _uiUpdate = UI.Update;
            _weaponsUpdate = Weapons.Update;
            _suppressor = Rig.Suppressor.Refresh;
            _weaponsLate = Weapons.LateUpdate;
            _rigRender = Rig.BeforeRender;
            _weaponsRender = Weapons.BeforeRender;
            _uiRender = UI.BeforeRender;
            _lensRender = () => LensEffects.Instance?.Present();
            _handApply = new Action[Rig.Hands.Length];
            for (int i = 0; i < Rig.Hands.Length; i++)
            {
                _handApply[i] = Rig.Hands[i].Model.Apply;
            }
        }

        private void LateUpdate()
        {
            if (!_active)
            {
                return;
            }
            ModTime.Start();
            Log.Guard("Suppressor", _suppressor);
            Log.Guard("Weapons.LateUpdate", _weaponsLate);
            ModTime.Stop();
        }

        private void OnBeforeRender()
        {
            if (!_active)
            {
                return;
            }
            ModTime.Start();
            Log.Guard("Rig.BeforeRender", _rigRender);
            Log.Guard("Weapons.BeforeRender", _weaponsRender);
            Log.Guard("UI.BeforeRender", _uiRender);
            Log.Guard("LensEffects", _lensRender); // after the weapons: this frame's flashes and glow are in
            // The gun hand first: a support hand cupping it closes on where it is this frame.
            int first = (int)Input.VRInput.GunHand;
            Log.Guard("HandModel", _handApply[first]);
            for (int i = 0; i < _handApply.Length; i++)
            {
                if (i != first)
                {
                    Log.Guard("HandModel", _handApply[i]);
                }
            }
            ModTime.Stop();
        }

        private void OnSceneChanged(Scene from, Scene to)
        {
            Log.Debug($"Scene {from.name} -> {to.name}");
            // A level load unpauses the match (GameManager on every load): a pause the mod made is gone with it, and kept as set
            // it made the next headset or dashboard edge do the wrong thing (lifecycle hunt, 2026-10-21).
            _pausedForDashboard = false;
            _pausedForHeadset = false;
            _speedBeforePause = 1f;
            _pauseAfterLoad = true; // (the dashboard still open or the headset still off: paused again once in the match)
            Weapons?.OnSceneChanged();
            UI?.OnSceneChanged();
        }

        // Whether the match is paused because the SteamVR dashboard is open (not by the player).
        private bool _pausedForDashboard;

        /// <summary>The headset is off (its last worn edge said so).</summary>
        private bool _headsetOff;

        /// <summary>A level loaded: a pause for the dashboard or the headset is put back once the player is in the match.</summary>
        private bool _pauseAfterLoad;

        private Action _pauseAfterLoadCheck;

        /// <summary>
        /// The pause a level load took away (the game unpauses on every load, and the dashboard and headset edges came before
        /// it): with the dashboard still open or the headset still off, the match was played behind them (headset taken off on
        /// the menu, a match started from the monitor; round-4 scout). Once, when the player is first in the match; not again
        /// after that, so a match unpaused from the monitor on purpose stays so.
        /// </summary>
        private void PauseAfterLoad()
        {
            bool dashboard = OpenVRRuntime.DashboardOpen, off = _headsetOff;
            if (!dashboard && !off)
            {
                _pauseAfterLoad = false;
                return;
            }
            if (!GameContext.InGame || GameManager.instance == null)
            {
                return; // (loading, or not spawned yet)
            }
            _pauseAfterLoad = false;
            bool menuOpen = IngameMenuUi.instance != null && IngameMenuUi.IsOpen();
            if (!GameManager.IsPaused() && !menuOpen)
            {
                _speedBeforePause = Time.timeScale;
                GameManager.PauseGame();
                _pausedForDashboard = dashboard;
                _pausedForHeadset = off;
            }
        }

        /// <summary>
        /// The SteamVR dashboard pauses the match behind it (time and sound), and closing it resumes. The game's own pause
        /// menu stays shut: the dashboard's button is not the game's menu button, and coming back from SteamVR to the
        /// game's menu read as the one button opening both.
        /// </summary>
        private void OnDashboard(bool open)
        {
            if (open)
            {
                if (GameContext.InGame && GameManager.instance != null && !GameManager.IsPaused())
                {
                    _speedBeforePause = Time.timeScale;
                    GameManager.PauseGame();
                    _pausedForDashboard = true;
                }
                else if (_pausedForHeadset)
                {
                    _pausedForDashboard = true; // opened with the headset off: putting it back on must not run the match behind it
                }
                return;
            }
            bool menuOpen = IngameMenuUi.instance != null && IngameMenuUi.IsOpen();
            if (_pausedForDashboard && !_pausedForHeadset && GameManager.instance != null && GameManager.IsPaused() && !menuOpen)
            {
                Resume();
            }
            _pausedForDashboard = false;
        }

        private bool _pausedForHeadset;

        /// <summary>The game's speed when the mod paused it (slow motion is the game's time scale at 0.2).</summary>
        private float _speedBeforePause = 1f;

        /// <summary>
        /// The match goes on at the speed it had: the game's unpause sets the time scale to 1, so slow motion was switched off by
        /// every look at the dashboard or headset taken off (lifecycle review, 2026-10-21).
        /// </summary>
        private void Resume()
        {
            GameManager.UnpauseGame();
            if (_speedBeforePause > 0f && _speedBeforePause < 1f)
            {
                GameManager.SetTimeScale(_speedBeforePause);
            }
            _speedBeforePause = 1f;
        }

        /// <summary>
        /// Taking the headset off in a match pauses it, as the SteamVR dashboard does (the match went on and you were shot
        /// while it was off); putting it back on carries on, unless a menu was opened in the meantime.
        /// </summary>
        private void OnHeadsetWorn(bool worn)
        {
            _headsetOff = !worn;
            if (!worn)
            {
                if (GameContext.InGame && GameManager.instance != null && !GameManager.IsPaused())
                {
                    _speedBeforePause = Time.timeScale;
                    GameManager.PauseGame();
                    _pausedForHeadset = true;
                }
                else if (_pausedForDashboard)
                {
                    _pausedForHeadset = true; // closing the dashboard must not carry on while the headset is off
                }
                return;
            }
            bool menuOpen = IngameMenuUi.instance != null && IngameMenuUi.IsOpen();
            if (_pausedForHeadset && !_pausedForDashboard && GameManager.instance != null && GameManager.IsPaused() && !menuOpen)
            {
                Resume();
            }
            _pausedForHeadset = false;
        }

        private void OnRenderSettingChanged(object sender, EventArgs e) => OpenVRRuntime.ApplyLiveSettings();

        private void OnGraphicsPresetChanged(object sender, EventArgs e) => GraphicsPresets.OnPresetChanged();

        private void Shutdown()
        {
            if (_subscribed)
            {
                Application.onBeforeRender -= OnBeforeRender;
                SceneManager.activeSceneChanged -= OnSceneChanged;
                OpenVRRuntime.DashboardChanged -= OnDashboard;
                OpenVRRuntime.HeadsetWornChanged -= OnHeadsetWorn;
                VRConfig.RenderScale.SettingChanged -= OnRenderSettingChanged;
                VRConfig.Graphics.SettingChanged -= OnGraphicsPresetChanged;
                VRConfig.Mirror.SettingChanged -= OnRenderSettingChanged;
                _subscribed = false;
            }
            _active = false;
            if ((_pausedForDashboard || _pausedForHeadset) && GameManager.instance != null && GameManager.IsPaused() && !(IngameMenuUi.instance != null && IngameMenuUi.IsOpen()))
            {
                // A pause the mod made (the dashboard, the headset off) is undone as VR goes: forgotten, the match stayed paused
                // with nothing left to unpause it (review, 2026-10-20).
                Log.Guard("unpause on shutdown", Resume);
            }
            _pausedForDashboard = false; // and not undone in a later match by a dashboard or headset edge
            _pausedForHeadset = false;
            ScriptKeys.Enabled = false;
            ScriptKeys.Clear();
            VRGameInput.Enabled = false;
            VRGameInput.RestoreGameSwitches();
            VRGameInput.ResetSession();
            // Each guarded: one that threw left the rest undisposed and SteamVR running (bug hunt, 2026-10-22).
            UISystem ui = UI;
            WeaponSystem weapons = Weapons;
            VRRig rig = Rig;
            if (ui != null)
            {
                Log.Guard("UI.Dispose", ui.Dispose);
            }
            if (weapons != null)
            {
                Log.Guard("Weapons.Dispose", weapons.Dispose);
            }
            Log.Guard("DrivingWeapons.Reset", RavenfieldVR.Weapons.DrivingWeapons.Reset); // a weapon drawn in a car must not stay usable from the seat in flat play
            if (rig != null)
            {
                Log.Guard("Rig.Dispose", rig.Dispose);
            }
            UI = null;
            Weapons = null;
            Rig = null;
            VRInput.Shutdown();
            OpenVRRuntime.Stop();
        }

        private void OnApplicationQuit() => Shutdown();

        private void OnDestroy()
        {
            Shutdown();
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }
}
