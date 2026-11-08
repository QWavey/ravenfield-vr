using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using RavenfieldVR.Util;
using RavenfieldVR.Weapons;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// -rfvr-autotest: plays a match by itself to exercise the mod. Start with
    /// <c>ravenfield.exe -map island2023 -nointro -rfvr-autotest [-vr]</c>. Every installed mutator is switched on,
    /// the player deploys, every weapon is equipped in turn, its manual reload runs through the real code
    /// (VR only) and it fires a few shots; then the game quits. BepInEx/RavenfieldVR-autotest.txt lists every
    /// exception (with the weapon being tested), what each reload did, and the mod's per-frame timings.
    /// </summary>
    public static class AutoTest
    {
        public static string ReportPath => Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-autotest.txt");

        private static readonly bool RequestedOnCommandLine = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-autotest", StringComparison.OrdinalIgnoreCase));

        /// <summary>A test run (started with -rfvr-autotest): logging only such a run wants (the analyser's scope pictures of every gun).</summary>
        internal static bool TestRun => RequestedOnCommandLine;

        /// <summary>Started with -rfvr-autotest (read once: it is checked every frame).</summary>
        public static bool Requested => RequestedOnCommandLine;

        private static bool _started;
        private static bool _written;
        private static int _tested;
        private static string _context = "startup";
        private static readonly List<string> Problems = new List<string>();
        private static readonly List<string> Results = new List<string>();
        private static readonly HashSet<string> Seen = new HashSet<string>();

        /// <summary>
        /// -rfvr-deep (with -rfvr-only): every check on every weapon, not a few of each kind: rack grips, mag kicks and
        /// tactical reloads, pistol pouches, recoil, and a magazine taken all the way round with side pictures.
        /// </summary>
        internal static readonly bool Deep = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-deep", StringComparison.OrdinalIgnoreCase));

        private static readonly bool MenuTestRequested = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-menutest", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// -rfvr-grabshots (with -rfvr-autotest -vr): every weapon only drawn and pictured where the hand takes it to reload:
        /// its slide, bolt or handle racked (three views) and its magazine all the way round (seated, dropped, in the hand,
        /// lined up, half in, home). For checking every grab by eye.
        /// </summary>
        private static readonly bool GrabShots = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-grabshots", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// -rfvr-uishots (with -rfvr-autotest -vr): only the screens: loading, the pause menu and its options, the loadout,
        /// squad pointing, the wrist view, the body (holsters), damage, dirt and the muzzle lens flash; then quit.
        /// </summary>
        private static readonly bool UiShots = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-uishots", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// -rfvr-grabcheck (with -rfvr-autotest -vr): only the soldier checks (grab, throw, fist, a gun swung into one) with the first
        /// rifle of the loadout drawn, then the report and quit (user, 2026-10-24: "is grabbing people possible rn? and throwing them
        /// too? and hitting them with any weapon/fists? ... only focus on that").
        /// </summary>
        private static readonly bool GrabCheckOnly = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-grabcheck", StringComparison.OrdinalIgnoreCase));
        /// <summary>-rfvr-walkcheck (with -rfvr-grabcheck): only the soldier check's walks with a soldier held (a minute).</summary>
        private static readonly bool WalkCheckOnly = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-walkcheck", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// -rfvr-flashfilm (with -rfvr-autotest -vr, and -rfvr-only= for the guns): films what each eye gets while each gun
        /// shoots (its own muzzle flash and the lens flash on the view), frame by frame, into RavenfieldVR-shots/film-*.
        /// </summary>
        private static readonly bool FlashFilmRequested = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-flashfilm", StringComparison.OrdinalIgnoreCase));

        /// <summary>-rfvr-flashcheck (with -rfvr-autotest -vr): every weapon fired, its lens flashes counted against its shots.</summary>
        private static readonly bool FlashCheckRequested = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-flashcheck", StringComparison.OrdinalIgnoreCase));

        /// <summary>-rfvr-filmonemag (with -rfvr-flashfilm): each gun aimed and fired on full auto until one magazine is empty, nothing else.</summary>
        private static readonly bool FilmOneMag = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-filmonemag", StringComparison.OrdinalIgnoreCase));

        /// <summary>-rfvr-perfbench (with -rfvr-autotest -vr): only the graphics bench, then quit.</summary>
        private static readonly bool PerfBenchRequested = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-perfbench", StringComparison.OrdinalIgnoreCase));

        public static void Install()
        {
            if (MenuTestRequested)
            {
                Application.runInBackground = true;
                UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
                {
                    if (scene.buildIndex == 1 && !_menuTested)
                    {
                        _menuTested = true;
                        VRManager.Instance?.StartCoroutine(MenuTest());
                    }
                };
            }
            if (!Requested)
            {
                return;
            }
            PerfProbe.Enabled = true;
            // From the first frame: a test started minimized (or behind another game) must not pause while loading.
            Application.runInBackground = true;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => VRManager.Instance?.StartCoroutine(WaitForMatch());
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
            {
                if (scene.buildIndex == 2)
                {
                    VRManager.Instance?.StartCoroutine(LoadShots());
                }
            };
            // A run cut short (window closed, game quit) still leaves a report.
            Application.quitting += () =>
            {
                if (_started && !_written)
                {
                    Results.Add("Run ended early (the game quit before the test finished).");
                    Write();
                }
            };
            Log.ExceptionLogged += (context, e) => Problem($"[{_context}] {context}: {e.GetType().Name}: {e.Message}\n      {FirstFrames(e.StackTrace)}");
            Application.logMessageReceived += (condition, stack, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error && condition.IndexOf("RavenfieldVR", StringComparison.Ordinal) >= 0)
                {
                    Problem($"[{_context}] {type}: {condition}\n      {FirstFrames(stack)}");
                }
            };
        }

        /// <summary>
        /// Pictures every half second from the loading screen to the first moment of the match (no sky-coloured or empty
        /// frame may show while the map loads; the loading screen stands in front of you in the dark).
        /// </summary>
        private static IEnumerator LoadShots()
        {
            for (int i = 0; i < 90 && !GameContext.InGame; i++)
            {
                yield return new WaitForSecondsRealtime(0.5f);
                yield return new WaitForEndOfFrame();
                if (VRManager.Active)
                {
                    Shots.Capture($"load-{i:00}{(UI.LoadingView.Active ? "-loading" : "")}");
                }
            }
        }

        private static bool _menuTested;

        /// <summary>
        /// -rfvr-menutest (with VR): ten seconds in the main menu while the weapon warm-up runs. A picture every second (the
        /// view must stay still: the menu's background camera drifts and turns), how far the view moved, and the frame
        /// times, written to BepInEx/RavenfieldVR-menutest.txt; then the game quits.
        /// </summary>
        private static IEnumerator MenuTest()
        {
            yield return new WaitForSecondsRealtime(3f);
            var frames = new List<float>();
            float start = Time.realtimeSinceStartup, nextShot = start;
            int shot = 0;
            Rig.VRRig rig = Rig.VRRig.Instance;
            Vector3 firstPos = rig != null ? rig.Root.position : Vector3.zero;
            Quaternion firstRot = rig != null ? rig.Root.rotation : Quaternion.identity;
            float drift = 0f, turn = 0f;
            int walksBefore = UI.VRInputModule.Walks;
            bool afterShot = false;
            while (Time.realtimeSinceStartup - start < 10f)
            {
                if (!afterShot)
                {
                    frames.Add(Time.unscaledDeltaTime * 1000f);
                }
                afterShot = false; // the frame a picture was taken in carries its cost (rendering and saving): not the menu's
                if (rig != null)
                {
                    drift = Mathf.Max(drift, Vector3.Distance(rig.Root.position, firstPos));
                    turn = Mathf.Max(turn, Quaternion.Angle(rig.Root.rotation, firstRot));
                }
                if (Time.realtimeSinceStartup >= nextShot && VRManager.Active)
                {
                    nextShot += 1f;
                    yield return new WaitForEndOfFrame();
                    Shots.Capture($"menu-{shot++:00}");
                    afterShot = true;
                }
                yield return null;
            }
            frames.Sort();
            float P(float q) => frames.Count > 0 ? frames[Mathf.Clamp((int)(frames.Count * q), 0, frames.Count - 1)] : 0f;
            int over = frames.Count(f => f > 1000f / 90f + 1f);
            var sb = new StringBuilder();
            sb.AppendLine($"Ravenfield VR {PluginInfo.Version} menu test, {DateTime.Now:yyyy-MM-dd HH:mm}, VR {(VRManager.Active ? "on" : "off")}");
            sb.AppendLine($"View moved: {drift * 100f:0.0} cm, turned {turn:0.0} deg over 10 s (should be 0: the menu's background camera is not followed)");
            sb.AppendLine($"Frames: {frames.Count}, ms p50 {P(0.5f):0.0}, p95 {P(0.95f):0.0}, p99 {P(0.99f):0.0}, max {P(1f):0.0}; over the 90 Hz budget: {over}");
            sb.AppendLine($"Pictures: {shot} (BepInEx/RavenfieldVR-shots/menu-*.png)");
            sb.AppendLine($"Laser: the panels' hierarchies walked {(UI.VRInputModule.Walks - walksBefore) / 10f:0.0} times a second (at most 10, plus panel changes; it was twice a frame)");
            try
            {
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-menutest.txt"), sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Exception("Writing the menu test report failed", e);
            }
            Log.Info("Menu test: " + sb.ToString().Replace(Environment.NewLine, " | "));
            Application.Quit();
        }

        private static string FirstFrames(string stack) =>
            string.Join(" | ", (stack ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4).ToArray());

        private static void Problem(string text)
        {
            if (Seen.Add(text))
            {
                Problems.Add(text);
            }
        }

        /// <summary>Content is loaded and the match is about to start: switch on every installed mutator.</summary>
        public static void EnableAllMutators()
        {
            if (!Requested || ModManager.instance == null || GameManager.instance == null)
            {
                return;
            }
            var active = GameManager.instance.gameInfo.activeMutators;
            int added = 0;
            foreach (MutatorEntryData m in ModManager.instance.loadedMutators)
            {
                if (m == null || active.Contains(m) || ModManager.instance.builtInMutators.Contains(m))
                {
                    continue;
                }
                active.Add(m);
                added++;
            }
            Results.Add($"Mutators switched on: {added} ({string.Join(", ", active.Select(m => m.name).ToArray())})");
        }

        private static IEnumerator WaitForMatch()
        {
            // The scene finishes setting up over a few frames.
            for (int i = 0; i < 300 && !_started; i++)
            {
                if (GameManager.IsIngame(out bool noGameplay) && !noGameplay && ActorManager.instance != null)
                {
                    _started = true;
                    yield return Safely(Run(), e =>
                    {
                        // A check that throws ended the whole run silently: no report, no quit, the game open at the match
                        // until the runner's timeout. Said as a problem, written, and the game quits as at the end.
                        Problem($"[{_context}] the autotest stopped: {e.GetType().Name}: {e.Message} | {FirstFrames(e.StackTrace)}");
                        Write();
                        Application.Quit();
                    });
                    yield break;
                }
                yield return null;
            }
        }

        /// <summary>
        /// Runs a coroutine and the coroutines it yields, catching what any of them throws (Unity drops a coroutine that
        /// throws without a word). Yield instructions that are not enumerators go to Unity as usual.
        /// </summary>
        private static IEnumerator Safely(IEnumerator body, Action<Exception> failed)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(body);
            while (stack.Count > 0)
            {
                IEnumerator top = stack.Peek();
                bool more;
                object current;
                try
                {
                    more = top.MoveNext();
                    current = more ? top.Current : null;
                }
                catch (Exception e)
                {
                    failed(e);
                    yield break;
                }
                if (!more)
                {
                    stack.Pop();
                    continue;
                }
                if (current is IEnumerator nested)
                {
                    stack.Push(nested);
                    continue;
                }
                yield return current;
            }
        }

        private static IEnumerator Run()
        {
            Application.runInBackground = true;
            // The hand reload checks for the run, whatever reload mode the player saved: with Assisted saved, the magazine, belt and
            // cover checks returned without a line and every gun "passed" its hand reload untried (round-7 scout). (A run-time
            // override: the saved setting stays.)
            VRConfig.TestReload = ReloadMode.Manual;
            VRConfig.TestSlowHands = SlowMotionHands.Normal; // (the slow-motion checks measure the real-time hands; the slowed ones have their own)
            _matchAt = Time.realtimeSinceStartup;
            float deadline = Time.realtimeSinceStartup + 120f;
            while (LocalPlayer.actor == null && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            yield return new WaitForSecondsRealtime(4f);
            _context = "deploy";
            yield return Deploy();
            _deployedAt = Time.realtimeSinceStartup;
            if (LocalPlayer.actor == null || LocalPlayer.actor.dead)
            {
                Results.Add("Could not deploy the player; weapon tests skipped.");
                Problem("[deploy] the player could not be deployed: no weapon or check ran"); // (counted: the run read "Problems: 0"; round-7 scout)
            }
            else
            {
                if (VRManager.Active && VRConfig.SpawnEmptyHanded.Value && VRConfig.HolsterSwitching.Value)
                {
                    // A moment after the spawn: the hands must be empty, every weapon in its holster.
                    yield return new WaitForSecondsRealtime(0.5f);
                    Weapon inHand = LocalPlayer.actor != null ? LocalPlayer.actor.activeWeapon : null;
                    Results.Add($"Spawned with empty hands: {(inHand == null ? "yes" : $"NO, holding {inHand.name}")}");
                    if (inHand != null)
                    {
                        Problem($"[spawn] the player spawned holding {inHand.name} (SpawnEmptyHanded is on)");
                    }
                }
                if (VRManager.Active)
                {
                    yield return PresetSwitchCheck();
                    yield return HoldStepCheck();
                }
                if (VRManager.Active && !VRConfig.BodyShadow.Value && LocalPlayer.controller != null && LocalPlayer.controller.thirdpersonRenderers != null)
                {
                    // The player's own body and its shadow hidden on foot (its shadow crossed the hands; task 336).
                    int drawn = 0, all = 0;
                    foreach (Renderer r in LocalPlayer.controller.thirdpersonRenderers)
                    {
                        if (r != null)
                        {
                            all++;
                            drawn += r.forceRenderingOff ? 0 : 1;
                        }
                    }
                    Results.Add($"The player's body on foot (BodyShadow off): {(drawn == 0 ? $"hidden, {all} renderer(s)" : $"{drawn} of {all} renderer(s) NOT HIDDEN")}");
                    if (drawn > 0)
                    {
                        Problem($"[body] {drawn} of the player's body renderers still drawn on foot: their shadow crosses the view");
                    }
                }
                if (VRManager.Active && UiShots)
                {
                    yield return SquadPointingCheck();
                    yield return LoadoutPickerCheck();
                    yield return PauseMenuCheck();
                    GameSoundsCheck();
                    yield return SettingsPagesCheck();
                    yield return WristViewCheck();
                    yield return BodyShots();
                    Write();
                    Application.Quit();
                    yield break;
                }
                if (VRManager.Active && GrabCheckOnly)
                {
                    _context = "grab check";
                    yield return DrawFirstGun();
                    // Its failures counted as the weapon checks' are (ReportWeapon): straight into the results, a soldier line
                    // in capitals was read and never counted ("Problems: 0" over THE PHYSICS BLEW UP, 2026-10-24).
                    yield return BodyGrabCheck(l =>
                    {
                        if (Failure.IsMatch(l))
                        {
                            Problem("[soldiers] " + l);
                        }
                        Results.Add(l);
                    });
                    Write();
                    Application.Quit();
                    yield break;
                }
                if (VRManager.Active && GrabShots)
                {
                    PerfProbe.Reset();
                    yield return TestWeapons();
                    Write();
                    Application.Quit();
                    yield break;
                }
                if (VRManager.Active && FlashCheckRequested)
                {
                    yield return FlashCheck();
                    Write();
                    Application.Quit();
                    yield break;
                }
                if (VRManager.Active && FlashFilmRequested)
                {
                    yield return FlashFilm();
                    Write();
                    for (float until = Time.realtimeSinceStartup + 30f; EyeFilm.Writing > 0 && Time.realtimeSinceStartup < until;)
                    {
                        yield return null; // quitting while a frame writer is in Unity's encoder crashed the game
                    }
                    Application.Quit();
                    yield break;
                }
                if (VRManager.Active && PerfBenchRequested)
                {
                    yield return PerfBench();
                    Write();
                    Application.Quit();
                    yield break;
                }
                if (VRManager.Active)
                {
                    yield return StandingStillCheck();
                    yield return SquadPointingCheck();
                    yield return LoadoutPickerCheck();
                    yield return PauseMenuCheck();
                    GameSoundsCheck();
                    yield return SettingsPagesCheck();
                    yield return WristViewCheck();
                }
                PerfProbe.Reset();
                PerfProbe.SetContext("checks before the weapons");
                yield return TestWeapons();
                _context = "idle";
                PerfProbe.SetContext("idle at the end");
                yield return new WaitForSecondsRealtime(5f);
            }
            Write();
            Application.Quit();
        }

        /// <summary>Pictures of the body: hands and gloves, the holsters looked down at and from behind, the damage ring.</summary>
        private static IEnumerator BodyShots()
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            WeaponSystem sys = WeaponSystem.Instance;
            if (rig == null || sys == null)
            {
                yield break;
            }
            yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForEndOfFrame();
            Transform head = rig.Head;
            Shots.Capture("body-view");
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Shots.Capture("body-looking-down", head.position, Quaternion.LookRotation(flat * 0.35f + Vector3.down, flat), 100f);
            Vector3 behind = head.position - flat * 0.9f + Vector3.up * 0.15f;
            Shots.Capture("body-from-behind", behind, Quaternion.LookRotation(head.position - Vector3.up * 0.35f - behind), 80f);
            Vector3 side = head.position + Vector3.Cross(Vector3.up, flat) * 0.8f - Vector3.up * 0.4f;
            Shots.Capture("body-from-the-side", side, Quaternion.LookRotation(head.position - Vector3.up * 0.55f - side), 80f);
            Rig.DamageFeedback damage = Rig.DamageFeedback.Instance;
            if (damage != null)
            {
                damage.OnVignette(0.85f, 3f);
                damage.OnHitDirection(60f, false);
                // The glow's picture is made on a worker and shows a frame or two later.
                yield return new WaitForEndOfFrame();
                for (int wait = 0; wait < 6 && Rig.LensEffects.Instance != null && !Rig.LensEffects.Instance.ShowsHit; wait++)
                {
                    yield return null;
                    yield return new WaitForEndOfFrame();
                }
                bool hitShown = Rig.LensEffects.Instance != null && Rig.LensEffects.Instance.ShowsHit;
                Results.Add($"Hit from the front left: the glow {(hitShown ? "shown" : "NOT SHOWN")}");
                if (!hitShown)
                {
                    Problem("[hit glow] a hit from the side showed no glow (" + (Rig.LensEffects.Instance != null ? Rig.LensEffects.Instance.DebugState : "no lens effects") + ")");
                }
                Shots.Capture("damage-from-front-left");
                Rig.LensEffects.Instance?.SavePictures("lens-eyes-hit-from-front-left");
                yield return new WaitForSecondsRealtime(2.5f); // the ring fades
                // Low health: the red edge pulsing with the heartbeat and the colour drained (all gone when healed).
                Rig.DamageFeedback.TestHealthShare = 0.12f;
                try
                {
                    yield return new WaitForSecondsRealtime(1.2f);
                    yield return new WaitForEndOfFrame();
                    Shots.Capture("low-health");
                    Rig.LensEffects.Instance?.SavePictures("lens-eyes-low-health");
                }
                finally
                {
                    Rig.DamageFeedback.TestHealthShare = -1f;
                }
                yield return new WaitForSecondsRealtime(1.2f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("low-health-healed");
            }
            Rig.MudSplash mud = Rig.MudSplash.Instance;
            if (mud != null)
            {
                // A grenade going off two metres ahead and to the left: dirt on the view, gone four seconds later.
                Vector3 blast = head.position + (flat * 1.7f - Vector3.Cross(Vector3.up, flat) * 0.8f) - Vector3.up * 1.3f;
                mud.Splash(blast, 6f, false);
                yield return new WaitForSecondsRealtime(0.15f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("mud-0s");
                int first = mud.VisibleCount;
                yield return new WaitForSecondsRealtime(1.5f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("mud-1-5s");
                yield return new WaitForSecondsRealtime(2.6f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("mud-4s");
                int left = mud.VisibleCount;
                mud.Splash(blast, 6f, true);
                yield return new WaitForSecondsRealtime(0.15f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("water-splash-0s");
                // Coming up out of the water (Tasks 209 I): the approved drops, shown even with the option off.
                Rig.LensEffects lens = Rig.LensEffects.Instance;
                if (lens != null)
                {
                    // Shown with the option off without writing the player's setting (BepInEx saves every change).
                    Rig.LensEffects.TestDrops = true;
                    try
                    {
                        lens.WaterDrops();
                        yield return new WaitForSecondsRealtime(0.4f);
                        yield return new WaitForEndOfFrame();
                        Shots.Capture("water-drops-surfacing");
                        lens.SavePictures("lens-eyes-water-drops");
                        Results.Add($"Water drops after surfacing: {(lens.DropsShown > 0f ? "shown" : "NOT SHOWN")}");
                        yield return new WaitForSecondsRealtime(3f);
                        yield return new WaitForEndOfFrame();
                        Shots.Capture("water-drops-3s");
                    }
                    finally
                    {
                        Rig.LensEffects.TestDrops = false;
                    }
                }
                Metric("explosion dirt: splats on the view right after a blast 2 m away", first);
                Metric("explosion dirt: splats left 4 s later", left);
                Results.Add($"Explosion dirt: {first} spots right after a blast 2 m away, {left} still fading after 4 s{(first == 0 ? " (NO DIRT)" : "")}");
                if (first == 0)
                {
                    Problem("[explosion dirt] no dirt on the view after a blast 2 m away");
                }
            }
        }

        /// <summary>Melee: the blade the swing detection uses, measured on the held weapon.</summary>
        private static string MeleeCheck(HeldWeapon held)
        {
            MeleeHandler.MeasureBlade(held.Rig, out Vector3 dir, out float length);
            float offAim = Vector3.Angle(dir, held.Rig.Data.AimAxis);
            return length > 0.12f ? $"melee: blade {length * 100f:0} cm from the grip, {offAim:0}° off the aim axis" : "melee: NO MEASURABLE BLADE";
        }

        /// <summary>Close-ups of the support hand on the handguard, and how much of it is buried in the model.</summary>
        private static void SupportCloseups(HeldWeapon held, string name, Action<string> report)
        {
            Rig.VRHand off = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Off : null;
            if (off == null || off.Model == null)
            {
                return;
            }
            Runtime.RigidPose g = held.GripWorld;
            Vector3 up = g.rotation * Vector3.up, right = g.rotation * Vector3.right, fwd = g.rotation * Vector3.forward;
            Vector3 c = off.Model.Root.position;
            void Shot(string side, Vector3 from) => Shots.Capture($"{name}-{side}", from, Quaternion.LookRotation(c - from, up), 40f);
            Shot("out", c - right * 0.3f + up * 0.05f);
            Shot("in", c + right * 0.3f + up * 0.05f);
            Shot("under", c - up * 0.28f - fwd * 0.08f);
            string support = held.HandClipping(off, out int sBuried, out _, out int sTouching);
            string what = held.Rig.Data.IsPistol ? "support hand on a pistol" : "support hand on a long gun";
            Metric(what + ": points inside the gun", sBuried);
            Worst.Add(new KeyValuePair<string, int>($"{held.Rig.Data.Key}: {what}, {sTouching}/4 fingers on it", sBuried));
            Metric(what + ": fingers on the gun (of 4)", sTouching);
            report($"support hand ({off.Role}): {support}");
        }

        /// <summary>Close-ups of the off hand holding magazines, and how much of it is buried in them.</summary>
        private static void MagHandCloseups(string name, Action<string> report)
        {
            Rig.VRHand off = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Off : null;
            if (off == null || off.Model == null || !(off.Held is MagazineProp))
            {
                return;
            }
            Vector3 c = off.Model.Root.position;
            Vector3 up = Vector3.up;
            Vector3 right = off.Palm.rotation * Vector3.right;
            void Shot(string side, Vector3 from) => Shots.Capture($"{name}-{side}", from, Quaternion.LookRotation(c - from, up), 45f);
            Shot("hand-out", c - right * 0.28f * off.Mirror + up * 0.06f);
            Shot("hand-in", c + right * 0.28f * off.Mirror + up * 0.06f);
            string magHand = MagazineProp.HandClipping(off, out int mBuried, out _, out int mTouching);
            Metric("magazine hand: points inside the magazines", mBuried);
            Worst.Add(new KeyValuePair<string, int>($"{name}: magazine hand, {mTouching}/4 fingers on them", mBuried));
            Metric("magazine hand: fingers on the magazines (of 4)", mTouching);
            if (off.Held is MagazineProp shown && shown.Volume != null)
            {
                Metric("magazine solid: main-thread ms per magazine type", shown.Volume.RenderMs);
                Metric("magazine solid: longest main-thread frame ms", shown.Volume.RenderFrameMs);
            }
            report($"{name}: magazine hand {magHand}");
        }

        /// <summary>
        /// Switched from a preset back to Game in play, the game's own quality comes back: the user's play of 2026-10-28 kept Medium's
        /// shadows, MSAA and LOD under the Game preset (Unity ignores re-applying the Quality level already set). Game, Medium, Game
        /// again, through the setting as the panel changes it; the saved preset and render scale are put back after.
        /// </summary>
        private static IEnumerator PresetSwitchCheck()
        {
            GraphicsPreset savedPreset = VRConfig.Graphics.Value;
            float savedScale = VRConfig.RenderScale.Value;
            string Quality() => string.Format(CultureInfo.InvariantCulture, "shadows {0} {1:0} m, MSAA {2}x, LOD {3:0.0#}, lights {4}",
                QualitySettings.shadows, QualitySettings.shadowDistance, QualitySettings.antiAliasing, QualitySettings.lodBias, QualitySettings.pixelLightCount);
            try
            {
                VRConfig.Graphics.Value = GraphicsPreset.Game; // (the change applies it: GraphicsPresets.OnPresetChanged)
                yield return null;
                string game = Quality();
                VRConfig.Graphics.Value = GraphicsPreset.Medium;
                yield return null;
                string medium = Quality();
                VRConfig.Graphics.Value = GraphicsPreset.Game;
                yield return null;
                string back = Quality();
                bool kept = back != game;
                string line = $"preset switch in play: Game ({game}), Medium ({medium}), Game again ({back}){(kept ? " (NOT PUT BACK: the game's own quality was lost)" : "")}"
                              + (medium == game ? " (Medium changed nothing: not measured)" : "");
                Results.Add(line);
                if (kept)
                {
                    Problem("[presets] " + line);
                }
            }
            finally
            {
                VRConfig.Graphics.Value = savedPreset;
                VRConfig.RenderScale.Value = savedScale; // (a preset picked sets its own scale: the player's kept)
            }
        }

        /// <summary>
        /// The frame rate hold's steps on made-up windows (Medium, the hold on): late frames with the GPU at 3.3 of 11.1 ms (the
        /// user's CPU-bound Medium) hold the preset down to step 5 and never lower the render scale; with the GPU at 10 ms the last
        /// step lowers it; the GPU back at 4 ms gives it back. The hold, the preset and the scale are put back after.
        /// </summary>
        private static IEnumerator HoldStepCheck()
        {
            GraphicsPreset savedPreset = VRConfig.Graphics.Value;
            float savedScale = VRConfig.RenderScale.Value;
            bool savedHold = VRConfig.HoldFrameRate.Value;
            try
            {
                VRConfig.Graphics.Value = GraphicsPreset.Medium;
                VRConfig.HoldFrameRate.Value = true;
                yield return null;
                float full = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
                for (int i = 0; i < 16; i++)
                {
                    GraphicsPresets.OnRecentFrames(40, 3.3f, 11.1f);
                }
                int cpuLevel = GraphicsPresets.HoldLevel;
                float cpuScale = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
                for (int i = 0; i < 4; i++)
                {
                    GraphicsPresets.OnRecentFrames(40, 10f, 11.1f);
                }
                int gpuLevel = GraphicsPresets.HoldLevel;
                float gpuScale = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
                GraphicsPresets.OnRecentFrames(40, 4f, 11.1f);
                int backLevel = GraphicsPresets.HoldLevel;
                float backScale = UnityEngine.XR.XRSettings.eyeTextureResolutionScale;
                bool ok = cpuLevel == 5 && Mathf.Approximately(cpuScale, full) && gpuLevel == 6 && gpuScale < full - 0.01f
                          && backLevel == 5 && Mathf.Approximately(backScale, full);
                string line = string.Format(CultureInfo.InvariantCulture,
                    "frame rate hold: CPU-bound windows held it to step {0} at scale {1:0.00} (of {2:0.00}), GPU-bound to step {3} at {4:0.00}, the GPU back under the line to step {5} at {6:0.00}{7}",
                    cpuLevel, cpuScale, full, gpuLevel, gpuScale, backLevel, backScale, ok ? "" : " (NOT HELD RIGHT: the render scale lowered for the CPU, or not for the GPU)");
                Results.Add(line);
                if (!ok)
                {
                    Problem("[presets] " + line);
                }
            }
            finally
            {
                VRConfig.HoldFrameRate.Value = false;
                GraphicsPresets.OnRecentFrames(100); // (off: the hold back to step 0, the scale as set)
                VRConfig.HoldFrameRate.Value = savedHold;
                VRConfig.Graphics.Value = savedPreset;
                VRConfig.RenderScale.Value = savedScale;
            }
        }

        /// <summary>
        /// What the frame rate work of 2026-10-31 leaves on by default, read as the scene has it (not from the code that set it): the
        /// terrain drawn instanced with the mod's presets (as at the game's own top terrain quality), no image effect left on a game
        /// camera that draws nothing in VR, and the UI camera without shadows.
        /// </summary>
        private static void RenderingStateCheck(UI.UISystem ui)
        {
            int terrains = 0, instanced = 0;
            foreach (Terrain t in Terrain.activeTerrains)
            {
                if (t != null)
                {
                    terrains++;
                    instanced += t.drawInstanced ? 1 : 0;
                }
            }
            int effects = 0;
            var names = new List<string>();
            Camera eyes = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Camera : null;
            foreach (Camera c in Camera.allCameras)
            {
                if (c == null || c == eyes || c.targetTexture != null || ui != null && c == ui.UiCamera)
                {
                    continue;
                }
                foreach (MonoBehaviour mb in c.GetComponents<MonoBehaviour>())
                {
                    if (mb != null && mb.enabled && Rig.CameraSuppressor.ImageEffect(mb.GetType()))
                    {
                        effects++;
                        names.Add($"{mb.GetType().Name} on {c.name}");
                    }
                }
            }
            bool uiNoShadows = ui != null && ui.UiCamera != null && ui.UiCamera.GetComponent<Weapons.VRScope.ScopeNoShadows>() != null;
            bool ownPreset = VRConfig.Graphics.Value != GraphicsPreset.Game, older = GraphicsPresets.OlderMap();
            Results.Add($"Rendering state: terrain drawn instanced {instanced}/{terrains}{(!ownPreset ? " (the Game preset: as the game set it)" : older ? " (a map the game gives its fallback terrain material: as the game set it)" : "")}, "
                        + $"image effects on the game's cameras {effects}{(effects > 0 ? " (" + string.Join(", ", names.ToArray()) + ")" : "")}, "
                        + $"UI camera {(uiNoShadows ? "without shadows" : "WITH SHADOWS")}");
            if (ownPreset && !older && instanced < terrains)
            {
                Problem($"[rendering] {terrains - instanced} of {terrains} terrains not drawn instanced with the {VRConfig.Graphics.Value} preset");
            }
            if (effects > 0)
            {
                Problem($"[rendering] image effects still on the game's cameras: {string.Join(", ", names.ToArray())}");
            }
            if (!uiNoShadows)
            {
                Problem("[rendering] the UI camera renders with shadows");
            }
        }

        /// <summary>
        /// The graphics bench (task 215): standing still in the match with the world frozen, each preset and its variants, 1.5 s
        /// to settle and 4 s measured, the whole list twice: the frame time and the GPU time SteamVR's compositor measured
        /// (medians). The saved settings are never changed (the player's are put back at the end).
        /// </summary>
        private static IEnumerator PerfBench()
        {
            UI.UISystem ui = UI.UISystem.Instance;
            // (A few frames in: the suppressor looks at the game's cameras every 15 frames, and a level's first frames still set them up.)
            for (int f = 0; f < 16; f++)
            {
                yield return null;
            }
            RenderingStateCheck(ui);
            // The game's own quality first (what the user plays smoothly), each preset beside its version before 2026-10-24 (the
            // old values, as near as the bench can set them: terrain detail and draw distances stay the new preset's), then what
            // High's parts cost one by one, and Low again at the end (the scene drifting during the bench shows there).
            var runs = new List<KeyValuePair<string, Func<string>>>
            {
                new KeyValuePair<string, Func<string>>("Game (own quality)", () => GraphicsPresets.ApplyGameForBench(1f)),
                new KeyValuePair<string, Func<string>>("Low", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Low)),
                new KeyValuePair<string, Func<string>>("old Low", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Low, renderScale: 0.8f, msaa: 0,
                    shadows: ShadowQuality.HardOnly, shadowDistance: 35f, cascades: 1, shadowResolution: ShadowResolution.Low, lodBias: 0.6f, pixelLights: 1, hdr: true)),
                new KeyValuePair<string, Func<string>>("Medium", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Medium)),
                new KeyValuePair<string, Func<string>>("old Medium", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Medium, renderScale: 1f, msaa: 2,
                    shadows: ShadowQuality.All, shadowDistance: 60f, cascades: 2, shadowResolution: ShadowResolution.Medium, lodBias: 1f, pixelLights: 2, hdr: true)),
                new KeyValuePair<string, Func<string>>("High", () => GraphicsPresets.ApplyForBench(GraphicsPreset.High)),
                // (The old High and Ultra forced 16x texture filtering, cfaa7f7~1: without it their old cost read 3 ms low.)
                new KeyValuePair<string, Func<string>>("old High", () => GraphicsPresets.ApplyForBench(GraphicsPreset.High, renderScale: 1.2f, msaa: 4,
                    shadowDistance: 100f, cascades: 2, shadowResolution: ShadowResolution.High, lodBias: 1.5f, pixelLights: 4, reflections: true, softParticles: true,
                    anisotropic: AnisotropicFiltering.ForceEnable)),
                new KeyValuePair<string, Func<string>>("Ultra", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra)),
                new KeyValuePair<string, Func<string>>("old Ultra", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, renderScale: 1.4f, msaa: 4,
                    shadowDistance: 150f, cascades: 4, shadowResolution: ShadowResolution.VeryHigh, lodBias: 2f, pixelLights: 6,
                    anisotropic: AnisotropicFiltering.ForceEnable)),
                // What Ultra pays for: the forced 16x texture filter it had until 2026-10-24 (2.9 of its 8.9 ms then; its terrain,
                // reflections and soft particles cost nothing measurable), and what would bring it under 11.1 ms at the Quest 3's
                // size over Link (12.2 ms at 2528x2704 an eye).
                new KeyValuePair<string, Func<string>>("Ultra, anisotropic forced", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, anisotropic: AnisotropicFiltering.ForceEnable)),
                new KeyValuePair<string, Func<string>>("Ultra, MSAA 2x", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, msaa: 2)),
                new KeyValuePair<string, Func<string>>("Ultra, scale 1.1", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, renderScale: 1.1f)),
                new KeyValuePair<string, Func<string>>("High, shadows off", () => GraphicsPresets.ApplyForBench(GraphicsPreset.High, shadows: ShadowQuality.Disable)),
                new KeyValuePair<string, Func<string>>("High, MSAA off", () => GraphicsPresets.ApplyForBench(GraphicsPreset.High, msaa: 0)),
                new KeyValuePair<string, Func<string>>("High, scale 0.8", () => GraphicsPresets.ApplyForBench(GraphicsPreset.High, renderScale: 0.8f)),
                new KeyValuePair<string, Func<string>>("High, LOD bias 0.4", () => GraphicsPresets.ApplyForBench(GraphicsPreset.High, lodBias: 0.4f)),
                // What Medium's parts cost one by one (Tasks 348: the user's Medium ran at half rate on the CPU, its eyes 3.5 ms of main
                // thread against Game's 0.97): each variant leaves out one of them.
                new KeyValuePair<string, Func<string>>("Medium, shadows off", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Medium, shadows: ShadowQuality.Disable)),
                new KeyValuePair<string, Func<string>>("Medium, LOD bias 0.3", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Medium, lodBias: 0.3f)),
                new KeyValuePair<string, Func<string>>("Medium, Low's grass and trees", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Medium, terrainOf: GraphicsPreset.Low)),
                new KeyValuePair<string, Func<string>>("Medium, no pixel lights", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Medium, pixelLights: 0)),
                new KeyValuePair<string, Func<string>>("Medium, MSAA off", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Medium, msaa: 0)),
                // What the terrain drawn instanced (on in the presets since 2026-10-31, as at the game's own top terrain quality) saves:
                // Medium with it off, as the game's lower terrain qualities draw it.
                new KeyValuePair<string, Func<string>>("Medium, terrain not instanced", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Medium, terrainInstanced: false)),
                // The level's small objects without their shadows (SmallObjectsNoShadows, to try): fewer casters in the shadow
                // passes Medium draws once per eye.
                new KeyValuePair<string, Func<string>>("Medium, small objects without shadows", () =>
                {
                    string d = GraphicsPresets.ApplyForBench(GraphicsPreset.Medium);
                    SmallShadows.Bench = true;
                    return d + ", small objects without shadows";
                }),
                // The level's objects only a few pixels across not drawn (HideTinyDistantObjects, to try): fewer draws in each eye's passes.
                new KeyValuePair<string, Func<string>>("Medium, tiny distant objects hidden", () =>
                {
                    string d = GraphicsPresets.ApplyForBench(GraphicsPreset.Medium);
                    TinyObjects.Bench = true;
                    return d + ", tiny distant objects hidden";
                }),
                // The level's repeated objects drawn instanced (InstanceRepeatedObjects, to try): fewer draws for copies of one mesh.
                new KeyValuePair<string, Func<string>>("Medium, repeated objects instanced", () =>
                {
                    string d = GraphicsPresets.ApplyForBench(GraphicsPreset.Medium);
                    InstancedObjects.Bench = true;
                    return d + ", repeated objects instanced";
                }),
                // All of the settings to try at once on Medium (the game's cameras into 1 px, small objects without shadows, tiny distant
                // objects hidden, repeated objects instanced): what they save together, beside each one alone above.
                new KeyValuePair<string, Func<string>>("Medium, every setting to try", () =>
                {
                    string d = GraphicsPresets.ApplyForBench(GraphicsPreset.Medium);
                    Rig.CameraSuppressor.TinyRect = true;
                    SmallShadows.Bench = true;
                    TinyObjects.Bench = true;
                    InstancedObjects.Bench = true;
                    return d + ", every setting to try";
                }),
                new KeyValuePair<string, Func<string>>("High, no UI camera", () =>
                {
                    string d = GraphicsPresets.ApplyForBench(GraphicsPreset.High);
                    if (ui != null && ui.UiCamera != null)
                    {
                        ui.UiCamera.enabled = false;
                    }
                    return d + ", UI camera off";
                }),
                // The game's own screen cameras (drawing nothing in VR, kept on for the game's logic) rendering into one pixel instead
                // of the whole target: their clears, image effects and post-processing (about 0.7 ms of main thread in the user's
                // log of 2026-10-28; Tasks 348). Only the bench sets it: in play once measured.
                new KeyValuePair<string, Func<string>>("Game, game cameras 1 px", () =>
                {
                    string d = GraphicsPresets.ApplyGameForBench(1f);
                    Rig.CameraSuppressor.TinyRect = true;
                    return d + ", the game's suppressed cameras into 1 px";
                }),
                new KeyValuePair<string, Func<string>>("Low again", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Low)),
            };
            // The world held still for it (the game's own pause: drawing goes on, nothing moves), so every setting draws the same
            // picture; and every setting measured twice, the whole list once and then again. Live, the match moved under the
            // bench (fights, effects, bots coming into view): one 5 s sample of High with LOD bias 0.4 read 30 ms where High
            // itself read 11, and Medium read slower than High (2026-10-24).
            const int Rounds = 2;
            var samples = new Dictionary<string, List<KeyValuePair<float, float>>>();
            var lines = new Dictionary<string, string>();
            bool froze = GameManager.instance != null && !GameManager.IsPaused();
            if (froze)
            {
                GameManager.PauseGame();
            }
            // (Already paused, by the dashboard or the headset taken off, the world is frozen as well.)
            bool frozen = GameManager.instance != null && GameManager.IsPaused();
            Results.Add($"Graphics bench (standing still, the world {(frozen ? "frozen" : "NOT FROZEN")}, {Rounds} rounds; frame = CPU frame interval, "
                        + "GPU = the compositor's timing of our frames; medians):");
            try
            {
                for (int round = 0; round < Rounds; round++)
                {
                    foreach (KeyValuePair<string, Func<string>> run in runs)
                    {
                        if (ui != null && ui.UiCamera != null)
                        {
                            ui.UiCamera.enabled = true;
                        }
                        Rig.CameraSuppressor.TinyRect = false; // (only its own run sets it)
                        SmallShadows.Bench = false;
                        TinyObjects.Bench = false;
                        InstancedObjects.Bench = false;
                        string applied = run.Value();
                        yield return new WaitForSecondsRealtime(1.5f);
                        PerfProbe.Reset();
                        yield return new WaitForSecondsRealtime(4f);
                        string summary = PerfProbe.Summary(out float cpu, out float gpu);
                        if (!samples.TryGetValue(run.Key, out List<KeyValuePair<float, float>> got))
                        {
                            samples[run.Key] = got = new List<KeyValuePair<float, float>>();
                        }
                        got.Add(new KeyValuePair<float, float>(cpu, gpu));
                        lines[run.Key] = $"{summary}   [{applied}]";
                        Log.Info($"Bench {run.Key} (round {round + 1}): {summary}");
                    }
                }
            }
            finally
            {
                if (froze && GameManager.instance != null && GameManager.IsPaused())
                {
                    GameManager.UnpauseGame();
                }
                if (ui != null && ui.UiCamera != null)
                {
                    ui.UiCamera.enabled = true;
                }
                Rig.CameraSuppressor.TinyRect = null; // (the config's again)
                SmallShadows.Bench = null;
                TinyObjects.Bench = null;
                InstancedObjects.Bench = null;
                GraphicsPresets.Apply();
                Runtime.OpenVRRuntime.ApplyLiveSettings();
            }
            // Each setting: the mean of its rounds' medians, the rounds' GPU times beside it (far apart: the scene was not still),
            // and the last round's whole line.
            foreach (KeyValuePair<string, Func<string>> run in runs)
            {
                if (!samples.TryGetValue(run.Key, out List<KeyValuePair<float, float>> got) || got.Count == 0)
                {
                    continue;
                }
                float cpu = got.Average(s => s.Key);
                // A round with no GPU timing reads -1: averaged in, a 9 ms and a missing round printed 4 (round-7 scout).
                List<float> gpuRounds = got.Where(s => s.Value >= 0f).Select(s => s.Value).ToList();
                string gpuText = gpuRounds.Count > 0 ? gpuRounds.Average().ToString("0.00", CultureInfo.InvariantCulture) : "n/a";
                string rounds = string.Join(" / ", got.Select(s => s.Value >= 0f ? s.Value.ToString("0.00", CultureInfo.InvariantCulture) : "n/a"));
                // (The frame interval's rounds too: the user's Medium is held back by the processor, and only the GPU's spread said
                // whether a difference was the setting's or the scene's; tools/bench_report.py weighs both.)
                string frameRounds = string.Join(" / ", got.Select(s => s.Key.ToString("0.00", CultureInfo.InvariantCulture)));
                Results.Add(string.Format(CultureInfo.InvariantCulture, "  {0,-26} frame {1,6:0.00} ms, GPU {2,6} ms (GPU by round {3}) (frame by round {5})   last: {4}",
                                          run.Key, cpu, gpuText, rounds, lines[run.Key], frameRounds));
                // (Medians since 2026-10-24, named so: the runs before held averages under "GPU ms" and "frame ms".)
                if (gpuRounds.Count > 0)
                {
                    Metric($"bench {run.Key}: GPU median ms", gpuRounds.Average());
                }
                Metric($"bench {run.Key}: frame median ms", cpu);
            }
        }

        /// <summary>The grab pictures of one weapon (see <see cref="GrabShots"/>).</summary>
        private static IEnumerator GrabPictures(HeldWeapon held, int index, string name, List<string> lines)
        {
            yield return new WaitForEndOfFrame();
            Shots.Capture($"weapon-{index:000}-{name}");
            // The whole gun from its side, a metre around it: what the reload animation parks beside or behind the gun (a
            // spare magazine, a belt, a drum) floats there, out of the head's view (user, 2026-10-18: "the 2nd mag floats").
            WeaponRigData rd = held.Rig.Data;
            MagazineSideShot(held, $"weapon-side-{index:000}-{name}", held.Rig.ToWorld((rd.GripPoint + rd.MuzzlePoint) * 0.5f), 1.3f);
            ActionType action = held.Rig.Data.Action;
            if (action == ActionType.Manual || action == ActionType.SelfLoading || action == ActionType.ShellFed)
            {
                Vector3? rackCam = null;
                yield return held.Reload.RackGripSelfCheck(lines.Add, shot =>
                {
                    Shots.Capture($"{shot}-{index:000}-{name}");
                    RackCloseUps($"{shot}-{index:000}-{name}", held, ref rackCam);
                });
                // The support hand slid onto the handle as well (only the runs without pictures had it, so a one-weapon run
                // could not check a gun that failed it).
                yield return held.Reload.SupportSlideSelfCheck(lines.Add);
            }
            if (held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag)
            {
                yield return held.Reload.MagazineDeepCheck(lines.Add, (shot, at, distance) => MagazineSideShot(held, $"{shot}-{index:000}-{name}", at, distance));
            }
            if (action == ActionType.SingleShot && held.ActiveAmmo != null && !held.ActiveAmmo.UsesDetachableMag)
            {
                // Every launcher loaded by hand in the full picture runs too (the check sat only in the runs without
                // pictures, so the full runs never loaded a rocket by hand).
                yield return held.Reload.RocketCheck(lines.Add);
            }
        }

        /// <summary>Close-ups from both sides of the gun, 25 cm out, at a world point: a shell going into its port or chamber.</summary>
        private static void PointCloseUps(string file, HeldWeapon held, Vector3 at)
        {
            Runtime.RigidPose g = held.GripWorld;
            Vector3 up = g.rotation * Vector3.up, right = g.rotation * Vector3.right;
            Shots.Capture(file + "-left", at - right * 0.25f + up * 0.08f, Quaternion.LookRotation(right * 0.25f - up * 0.08f, up), 45f);
            Shots.Capture(file + "-right", at + right * 0.25f + up * 0.08f, Quaternion.LookRotation(-right * 0.25f - up * 0.08f, up), 45f);
        }

        /// <summary>
        /// The rack grip's close-ups from the gun's left and right. Aimed at the hand on the part at rest, and from the same
        /// place on the gun for the half pull: aimed at the moving hand, the hand stayed put in the picture and the whole gun
        /// seemed to slide forward (the picture reviews read it as the wrong part moving).
        /// </summary>
        private static void RackCloseUps(string file, HeldWeapon held, ref Vector3? onGun)
        {
            Transform body = held.Rig.GunBody;
            Rig.VRHand rackHand = Rig.VRRig.Instance.Off;
            // On what the hand holds (its grasp point), not its wrist: the cameras showed the cuff and the gun's grip.
            Vector3 grasp = rackHand.Model.GraspPointWorld != Vector3.zero ? rackHand.Model.GraspPointWorld : rackHand.Model.Root.position;
            Vector3 c = onGun.HasValue && body != null ? body.TransformPoint(onGun.Value) : grasp;
            if (!onGun.HasValue && body != null)
            {
                onGun = body.InverseTransformPoint(c);
            }
            Runtime.RigidPose g = held.GripWorld;
            Vector3 up = g.rotation * Vector3.up, right = g.rotation * Vector3.right;
            Shots.Capture(file + "-left", c - right * 0.25f + up * 0.08f, Quaternion.LookRotation(right * 0.25f - up * 0.08f, up), 45f);
            Shots.Capture(file + "-right", c + right * 0.25f + up * 0.08f, Quaternion.LookRotation(-right * 0.25f - up * 0.08f, up), 45f);
        }

        /// <summary>
        /// The flash film (-rfvr-flashfilm): for each gun, what the two eyes get frame by frame: standing still (both eyes
        /// should be equally bright), three shots from the hip, three aimed through the sight, and on an automatic the
        /// trigger held for 1.2 s. Without -rfvr-only: a pistol, a rifle, a shotgun and a machine gun from the list.
        /// </summary>
        private static IEnumerator FlashFilm()
        {
            List<WeaponManager.WeaponEntry> entries = WeaponManager.instance.allWeapons.Where(e => e != null && e.prefab != null && WeaponSelfTest.Allows(e)).ToList();
            if (WeaponSelfTest.Only.Length == 0)
            {
                Weapon W(WeaponManager.WeaponEntry e) => e.prefab.GetComponent<Weapon>();
                var pick = new List<WeaponManager.WeaponEntry>();
                void Add(Func<Weapon, WeaponManager.WeaponEntry, bool> want)
                {
                    WeaponManager.WeaponEntry e = entries.FirstOrDefault(x => W(x) != null && W(x).configuration != null && !(W(x) is MeleeWeapon)
                                                                             && !(W(x) is ThrowableWeapon) && want(W(x), x) && !pick.Contains(x));
                    if (e != null)
                    {
                        pick.Add(e);
                    }
                }
                Add((w, e) => e.slot == WeaponManager.WeaponSlot.Secondary && !w.configuration.auto && w.configuration.projectilesPerShot <= 1);
                Add((w, e) => e.slot == WeaponManager.WeaponSlot.Primary && w.configuration.auto && w.configuration.projectilesPerShot <= 1);
                Add((w, e) => w.configuration.projectilesPerShot > 1);
                Add((w, e) => w.configuration.auto && w.configuration.ammo >= 80);
                entries = pick;
            }
            Results.Add($"Flash film: {entries.Count} gun(s), colour space {QualitySettings.activeColorSpace}, stereo {UnityEngine.XR.XRSettings.stereoRenderingMode}");
            if (Rig.VRRig.Instance != null)
            {
                // Facing the most open way (the spawn can face a tank's side): the flash is seen against the scene.
                Transform h0 = Rig.VRRig.Instance.Head;
                float bestYaw = 0f, bestFree = -1f;
                for (int k = 0; k < 12; k++)
                {
                    Vector3 dir = Quaternion.Euler(0f, k * 30f, 0f) * Vector3.ProjectOnPlane(h0.forward, Vector3.up).normalized;
                    float free = Physics.Raycast(h0.position, dir, out RaycastHit hit, 60f, ~0, QueryTriggerInteraction.Ignore) ? hit.distance : 60f;
                    if (free > bestFree + 0.5f)
                    {
                        bestFree = free;
                        bestYaw = k * 30f;
                    }
                }
                Rig.VRRig.Instance.AddYaw(bestYaw);
                Results.Add($"film: turned {bestYaw:0} degrees to face {bestFree:0} m of open view");
                yield return new WaitForSecondsRealtime(0.3f);
            }
            foreach (WeaponManager.WeaponEntry entry in entries)
            {
                int index = WeaponSelfTest.IdOf(entry);
                _context = "film " + entry.name;
                Actor actor = LocalPlayer.actor;
                if (actor == null || actor.dead)
                {
                    yield return Deploy();
                    actor = LocalPlayer.actor;
                    if (actor == null || actor.dead)
                    {
                        Results.Add($"film {entry.name}: the player could not respawn, stopping");
                        Problem($"[{entry.name}] film: the player could not respawn; the films after it were not made");
                        yield break;
                    }
                }
                Weapon weapon = null;
                try
                {
                    weapon = actor.EquipNewWeaponEntry(entry, Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1), true);
                }
                catch (Exception e)
                {
                    Problem($"[{entry.name}] film equip: {e.GetType().Name}: {e.Message}");
                }
                for (float until = Time.realtimeSinceStartup + 4f; weapon != null && !weapon.unholstered && Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.6f);
                HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                Rig.VRRig rig = Rig.VRRig.Instance;
                if (weapon == null || held == null || held.Weapon != weapon || rig == null)
                {
                    Results.Add($"film {entry.name}: not held, skipped");
                    continue;
                }
                string dir = Path.Combine(Shots.Dir, $"film-{index:000}-{Regex.Replace(entry.name, "[^A-Za-z0-9_.-]+", "_")}");
                // After the UI camera, the last to draw each eye (the on-screen effects are on its layer).
                Camera last = UI.UISystem.Instance != null && UI.UISystem.Instance.UiCamera != null ? UI.UISystem.Instance.UiCamera : rig.Camera;
                EyeFilm film = EyeFilm.Begin(last, dir);
                Rig.VRHand dom = rig.Dominant;
                int side = (int)dom.Side;
                int shown = LensFlash.Instance != null ? LensFlash.Instance.Shown : 0;
                int fired = 0;
                bool aimed = false, hip = true;
                // Each frame: the gun held pointing straight ahead, from the hip (low right) or aimed through its sight (the
                // sight 7 cm in front of the right eye); the gun follows the hand with a little lag, so it is set every frame.
                IEnumerator Frames(float seconds)
                {
                    float until = Time.realtimeSinceStartup + seconds;
                    do
                    {
                        if (aimed || hip)
                        {
                            Weapons.SightLine sight = held.CurrentSight;
                            Vector3 eyeGun = sight != null ? sight.EyePoint : held.Rig.Data.EyePoint;
                            Vector3 dirGun = sight != null ? sight.EyeDirection : held.Rig.Data.EyeDirection;
                            Transform head = rig.Head;
                            Runtime.RigidPose raw = dom.Raw;
                            Quaternion fix = Quaternion.FromToRotation(held.Rig.DirToWorld(dirGun), head.forward);
                            Vector3 target = aimed ? head.position + head.right * 0.032f + head.forward * 0.07f
                                : head.position + head.right * 0.2f - head.up * 0.28f + head.forward * 0.3f;
                            Rig.VRHand.Scripted[side] = new Runtime.RigidPose(target + fix * (raw.position - held.Rig.ToWorld(eyeGun)), fix * raw.rotation);
                        }
                        yield return null;
                    }
                    while (Time.realtimeSinceStartup < until);
                }
                void Shoot()
                {
                    Weapon active = weapon.GetActiveSubWeapon();
                    if (active == null || active.ammo == 0)
                    {
                        return;
                    }
                    try
                    {
                        if (active.Fire(active.CurrentMuzzle().forward, true))
                        {
                            fired++;
                        }
                        active.StopFire();
                    }
                    catch (Exception e)
                    {
                        Problem($"[{entry.name}] film fire: {e.GetType().Name}: {e.Message}");
                    }
                }
                void Cycle()
                {
                    if (held.ActiveAmmo != null && held.ActiveAmmo.Rig.CyclesByHand)
                    {
                        held.Reload.CycleForTest(); // a bolt or pump between shots
                    }
                }
                try
                {
                    film.Note("still");
                    yield return Frames(0.5f);
                    for (int k = 1; k <= 3 && !FilmOneMag; k++)
                    {
                        film.Note($"hip shot {k}");
                        Shoot();
                        yield return Frames(0.12f);
                        film.Note("hip");
                        yield return Frames(0.33f);
                        Cycle();
                    }
                    if (held.Rig.Data.HasEyePoint)
                    {
                        aimed = true;
                        hip = false;
                        film.Note("aiming");
                        yield return Frames(0.6f);
                        for (int k = 1; k <= 3 && !FilmOneMag; k++)
                        {
                            film.Note($"aimed shot {k}");
                            Shoot();
                            yield return Frames(0.12f);
                            film.Note("aimed");
                            yield return Frames(0.33f);
                            Cycle();
                        }
                    }
                    Weapon auto = weapon.GetActiveSubWeapon();
                    if (auto != null && auto.configuration != null && auto.configuration.auto)
                    {
                        film.Note("burst");
                        int magStart = auto.ammo;
                        float burstStart = Time.realtimeSinceStartup;
                        // One magazine: the trigger held until it is empty (at most 40 s); else a 1.2 s burst.
                        for (float until = burstStart + (FilmOneMag ? 40f : 1.2f); Time.realtimeSinceStartup < until && auto.ammo != 0;)
                        {
                            auto.Fire(auto.CurrentMuzzle().forward, true); // the trigger held: it fires at its own rate
                            yield return Frames(0f);
                        }
                        auto.StopFire();
                        if (FilmOneMag)
                        {
                            Results.Add($"film {entry.name}: one magazine on full auto, {magStart} -> {auto.ammo} rounds in {Time.realtimeSinceStartup - burstStart:0.0} s");
                        }
                        film.Note("after burst");
                        yield return Frames(0.4f);
                    }
                }
                finally
                {
                    Rig.VRHand.Scripted[side] = null;
                }
                yield return film.Finish();
                if (!FilmOneMag)
                {
                    yield return ReloadFilm(held, index, entry.name);
                }
                Rig.LensEffects lens = Rig.LensEffects.Instance;
                int flashes = (LensFlash.Instance != null ? LensFlash.Instance.Shown : 0) - shown;
                Results.Add($"film {entry.name}: {film.Frames} frames per eye in {dir}, {fired} single shot(s) fired, {flashes} lens flash(es) "
                            + $"(muzzle flash: {MuzzleFlashInfo.Of(weapon.GetActiveSubWeapon()).Describe()}), lens worker {(lens != null ? lens.WorkMs : 0f):0.0} ms a frame (most {(lens != null ? lens.WorkMsMax : 0f):0.0})");
                if (flashes == 0 && fired > 0)
                {
                    Problem($"[{entry.name}] film: {fired} shots, no lens flash");
                }
                yield return new WaitForSecondsRealtime(0.3f);
            }
        }

        /// <summary>
        /// The reload film (with the flash film): a close-up from the gun's side at the magazine well, every frame, while the
        /// off hand pulls the magazine out by hand and pushes it back in (ReloadInteractions.ReloadFilm).
        /// </summary>
        private static IEnumerator ReloadFilm(HeldWeapon held, int index, string name)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig == null || held == null || held.ActiveAmmo == null
                || !held.ActiveAmmo.UsesDetachableMag && held.Rig.Data.Action != ActionType.SingleShot)
            {
                yield break; // a magazine pulled by hand, or a launcher's rocket put in by hand
            }
            string dir = Path.Combine(Shots.Dir, $"reload-{index:000}-{Regex.Replace(name, "[^A-Za-z0-9_.-]+", "_")}");
            var go = new GameObject("RavenfieldVR Film Camera");
            var rt = new RenderTexture(640, 480, 24);
            EyeFilm film = null;
            try
            {
                Camera cam = go.AddComponent<Camera>();
                cam.enabled = false;
                rig.Suppressor.Ignore(cam);
                cam.CopyFrom(rig.Camera);
                cam.stereoTargetEye = StereoTargetEyeMask.None;
                cam.cullingMask = rig.Camera.cullingMask;
                cam.fieldOfView = 50f;
                cam.nearClipPlane = 0.02f;
                cam.targetTexture = rt;
                film = EyeFilm.Begin(cam, dir, true);
                // From the gun's left (or right, when the left is inside one of its meshes), a little below the well: the
                // same place on the gun every frame.
                Runtime.RigidPose g = held.GripWorld;
                Vector3 up = g.rotation * Vector3.up, right = g.rotation * Vector3.right;
                Vector3 well = held.Rig.ToWorld(held.Rig.Data.MagSeat.position);
                float away = 0.42f;
                if (held.Rig.Data.Action == ActionType.SingleShot)
                {
                    // A launcher: the tube's mouth and the 20 cm in front of it, where the rocket goes in, from 1.1 m off its
                    // side (42 cm off the seat the camera sat inside a HYDRA's tube and filmed a red wall).
                    well = held.Rig.ToWorld(held.Rig.Data.MuzzlePoint) + held.Rig.AimWorld * 0.2f;
                    away = 1.1f;
                }
                Vector3 from = well - right * away - up * 0.06f;
                if (InsideWeapon(held, from))
                {
                    from = well + right * away - up * 0.06f;
                }
                Vector3 fromGun = held.Rig.ToGun(from), wellGun = held.Rig.ToGun(well), upGun = Quaternion.Inverse(held.Rig.GunBody.rotation) * up;
                var lines = new List<string>();
                yield return held.Reload.ReloadFilm(lines.Add, note =>
                {
                    film.Note(note);
                    Vector3 f = held.Rig.ToWorld(fromGun), w = held.Rig.ToWorld(wellGun);
                    go.transform.SetPositionAndRotation(f, Quaternion.LookRotation(w - f - held.Rig.DirToWorld(upGun) * 0.05f, held.Rig.DirToWorld(upGun)));
                    cam.Render();
                });
                Results.Add($"film {name}: {string.Join("; ", lines)}, {film.Frames} close-up frames in {dir}");
                foreach (string l in lines)
                {
                    if (Failure.IsMatch(l))
                    {
                        Problem($"[{name}] {l}");
                    }
                }
            }
            finally
            {
                if (film != null)
                {
                    film.StopRecording();
                }
            }
            if (film != null)
            {
                yield return film.Finish();
            }
            UnityEngine.Object.Destroy(go);
            rt.Release();
            UnityEngine.Object.Destroy(rt);
        }

        /// <summary>
        /// -rfvr-flashcheck: every weapon drawn and fired (three single shots, and the trigger held a second on an automatic):
        /// how many shots, how many lens flashes, and its own muzzle flash. A weapon whose flash plays on every shot must
        /// flash on (nearly) every shot; one without a muzzle flash of its own has none by design (Tasks 212) and is listed.
        /// </summary>
        private static IEnumerator FlashCheck()
        {
            List<WeaponManager.WeaponEntry> entries = WeaponManager.instance.allWeapons.Where(e => e != null && e.prefab != null && WeaponSelfTest.Allows(e)).ToList();
            Results.Add($"Lens flash check: {entries.Count} weapon(s)");
            var none = new List<string>();
            int checkedGuns = 0, short_ = 0;
            foreach (WeaponManager.WeaponEntry entry in entries)
            {
                Weapon proto = entry.prefab.GetComponent<Weapon>();
                if (proto == null || proto is MeleeWeapon || proto is ThrowableWeapon)
                {
                    continue;
                }
                _context = "flash check " + entry.name;
                Actor actor = LocalPlayer.actor;
                if (actor == null || actor.dead)
                {
                    yield return Deploy();
                    actor = LocalPlayer.actor;
                    if (actor == null || actor.dead)
                    {
                        Results.Add("flash check: the player could not respawn, stopping");
                        Problem("[flash check] the player could not respawn; the flash check stopped part way");
                        break;
                    }
                }
                Weapon weapon = null;
                try
                {
                    weapon = actor.EquipNewWeaponEntry(entry, Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1), true);
                }
                catch (Exception e)
                {
                    Problem($"[{entry.name}] flash check equip: {e.GetType().Name}: {e.Message}");
                }
                for (float until = Time.realtimeSinceStartup + 4f; weapon != null && !weapon.unholstered && Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.3f);
                HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                if (weapon == null || held == null || held.Weapon != weapon || LensFlash.Instance == null)
                {
                    Results.Add($"  {entry.name}: not held, skipped");
                    continue;
                }
                Weapon active = weapon.GetActiveSubWeapon();
                MuzzleFlashInfo info = MuzzleFlashInfo.Of(active);
                int shots0 = LensFlash.Instance.ShotsSeen, flashes0 = LensFlash.Instance.Shown;
                Rig.VRRig.PeakShake = 0f; // the view's jolt on these shots (Tasks 211: a fraction of a degree, capped)
                for (int k = 0; k < 3 && active.ammo != 0; k++)
                {
                    try
                    {
                        active.Fire(active.CurrentMuzzle().forward, true);
                        active.StopFire();
                    }
                    catch (Exception e)
                    {
                        Problem($"[{entry.name}] flash check fire: {e.GetType().Name}: {e.Message}");
                        break;
                    }
                    yield return new WaitForSecondsRealtime(0.35f);
                    if (held.ActiveAmmo != null && held.ActiveAmmo.Rig.CyclesByHand)
                    {
                        held.Reload.CycleForTest();
                    }
                }
                int singleShots = LensFlash.Instance.ShotsSeen - shots0, singleFlashes = LensFlash.Instance.Shown - flashes0;
                float singleJolt = Rig.VRRig.PeakShake; // the largest turn of the view on those single shots (Tasks 211)
                int burstShots = 0, burstFlashes = 0;
                if (active.configuration != null && active.configuration.auto && active.ammo != 0)
                {
                    int s1 = LensFlash.Instance.ShotsSeen, f1 = LensFlash.Instance.Shown;
                    for (float until = Time.realtimeSinceStartup + 1f; Time.realtimeSinceStartup < until && active.ammo != 0;)
                    {
                        active.Fire(active.CurrentMuzzle().forward, true);
                        yield return null;
                    }
                    active.StopFire();
                    yield return new WaitForSecondsRealtime(0.3f);
                    burstShots = LensFlash.Instance.ShotsSeen - s1;
                    burstFlashes = LensFlash.Instance.Shown - f1;
                }
                checkedGuns++;
                int shots = singleShots + burstShots, flashes = singleFlashes + burstFlashes;
                bool expected = info.Any;
                bool tooFew = expected && shots > 0 && flashes < Mathf.FloorToInt(shots * (info.Particles ? Mathf.Min(1f, info.Chance) : 1f) * 0.8f);
                string line = $"  {entry.name}: singles {singleFlashes}/{singleShots} flashed, held trigger {burstFlashes}/{burstShots} flashed; muzzle flash: {info.Describe()}"
                              + $"; view jolt {singleJolt:0.00} deg on a single shot"
                              + (tooFew ? " (TOO FEW LENS FLASHES)" : !expected ? " (no muzzle flash of its own: no lens flash by design)" : "")
                              + (shots == 0 ? " (DID NOT FIRE)" : "");
                Results.Add(line);
                if (expected && shots > 0)
                {
                    Metric("lens flash check: share of shots that flashed (weapons with a muzzle flash)", (double)flashes / shots);
                }
                Weapon fired = held.Weapon != null ? held.Weapon.GetActiveSubWeapon() ?? held.Weapon : null;
                if (shots == 0 && !(fired != null && fired.targetTracker != null && !fired.targetTracker.CanFire()))
                {
                    // (A lock-on launcher fires only at a locked target: none in front of the test.) Counted as 1.0 before
                    // and never a problem (bug hunt 251.17).
                    Problem($"[{entry.name}] flash check: did not fire");
                }
                if (!expected)
                {
                    none.Add(entry.name);
                }
                if (tooFew)
                {
                    short_++;
                    Problem($"[{entry.name}] lens flash on {flashes} of {shots} shots (its muzzle flash: {info.Describe()})");
                }
            }
            Results.Add($"Lens flash check: {checkedGuns} guns fired, {short_} with too few lens flashes, {none.Count} without a muzzle flash of their own: {string.Join(", ", none)}");
        }

        /// <summary>
        /// An automatic held on the trigger for 1.6 s (Tasks 209 A-C): the lens flash limiter must keep each eye within
        /// six counted steps (three flashes) in any 1.1 s, as flash_sim.py's photosensitivity test, the rest soft flashes
        /// and the glow; the glow pictured a second in.
        /// </summary>
        private static IEnumerator LensBurstCheck(Weapon w, int index, string name, List<string> lines)
        {
            LensFlash lf = LensFlash.Instance;
            var log = new List<LensFlash.TestFlash>();
            lf.TestLog = log;
            int shotsBefore = _shots;
            float start = Time.realtimeSinceStartup;
            bool pictured = false;
            try
            {
                while (Time.realtimeSinceStartup - start < 1.6f && w != null && w.ammo != 0)
                {
                    w.Fire(w.CurrentMuzzle().forward, true); // the trigger held: an automatic fires at its own rate
                    yield return null;
                    if (!pictured && Time.realtimeSinceStartup - start > 1f)
                    {
                        pictured = true;
                        yield return new WaitForEndOfFrame();
                        Shots.Capture($"lensburst-{index:000}-{name}");
                        Rig.LensEffects.Instance?.SavePictures($"lens-eyes-burst-{index:000}-{name}");
                    }
                }
            }
            finally
            {
                if (w != null)
                {
                    w.StopFire();
                }
                lf.TestLog = null;
            }
            float took = Time.realtimeSinceStartup - start;
            int shots = _shots - shotsBefore;
            int[] worst = new int[2];
            int[] full = new int[2];
            for (int e = 0; e < 2; e++)
            {
                List<LensFlash.TestFlash> eye = log.FindAll(f => f.Eye == e);
                full[e] = eye.FindAll(f => f.Steps > 0).Count;
                for (int a = 0; a < eye.Count; a++)
                {
                    int sum = 0;
                    for (int b = a; b < eye.Count && eye[b].Time - eye[a].Time < 1.1f; b++)
                    {
                        sum += eye[b].Steps;
                    }
                    worst[e] = Mathf.Max(worst[e], sum);
                }
            }
            bool over = VRConfig.ReduceFlashing.Value && (worst[0] > 6 || worst[1] > 6);
            float softMax = 0f;
            foreach (LensFlash.TestFlash f in log)
            {
                if (f.Steps == 0)
                {
                    softMax = Mathf.Max(softMax, f.Shown);
                }
            }
            Metric("lens flash limiter: most counted steps in any 1.1 s of a held burst (budget 6)", Mathf.Max(worst[0], worst[1]));
            lines.Add($"lens flash burst: {shots} shots in {took:0.0} s, flashes L {log.FindAll(f => f.Eye == 0).Count} / R {log.FindAll(f => f.Eye == 1).Count} "
                      + $"(full L {full[0]} / R {full[1]}), most counted steps in any 1.1 s L {worst[0]} / R {worst[1]} (budget 6), "
                      + $"strongest soft flash {softMax:0.00}{(over ? " (OVER THE FLASH LIMIT)" : "")}");
            if (over)
            {
                Problem($"[{name}] the lens flash limiter let {Mathf.Max(worst[0], worst[1])} steps through in 1.1 s of a held burst (budget 6)");
            }
            if (full[0] != full[1])
            {
                // One decision for both eyes (a limiter each drifted apart: one eye flashed full, the other soft; Tasks 225).
                Problem($"[{name}] the eyes flashed differently in a held burst: full flashes L {full[0]} / R {full[1]}");
            }
            yield return new WaitForSecondsRealtime(0.3f);
        }

        /// <summary>
        /// A magazine close-up from the gun's left side, else its right: when the left one sits inside one of the weapon's
        /// meshes, or draws one flat colour (an akimbo pair's other gun there).
        /// </summary>
        private static void MagazineSideShot(HeldWeapon held, string file, Vector3 at, float distance)
        {
            Runtime.RigidPose g = held.GripWorld;
            Vector3 up = g.rotation * Vector3.up, right = g.rotation * Vector3.right, fwd = g.rotation * Vector3.forward;
            // Only the sides whose camera is clear of the weapon's meshes and sees the spot (no glove right in front of the
            // lens, no wall between: 35 of the review's pictures showed only the back of the free glove or a building), the
            // left first, then the right, then from below and ahead on either side.
            var poses = new List<(Vector3?, Quaternion?)>();
            Vector3[] candidates =
            {
                at - right * distance + up * 0.03f,
                at + right * distance + up * 0.03f,
                at + (-right * 0.8f - up * 0.45f + fwd * 0.4f).normalized * distance,
                at + (right * 0.8f - up * 0.45f + fwd * 0.4f).normalized * distance,
                at + (-right * 0.7f + fwd * 0.7f).normalized * distance,
            };
            foreach (Vector3 from in candidates)
            {
                if (!InsideWeapon(held, from) && !SightBlocked(from, at))
                {
                    poses.Add((from, Quaternion.LookRotation(at - from, up)));
                }
            }
            if (poses.Count == 0)
            {
                Vector3 from = at - right * distance + up * 0.03f;
                poses.Add((from, Quaternion.LookRotation(at - from, up)));
            }
            Shots.CaptureFirstClear(file, poses.ToArray(), 45f);
        }

        /// <summary>
        /// Something stands between a picture camera and the spot it shows: a glove along the line (all but the last 4 cm,
        /// where a hand holding the magazine is what the picture is of) or level geometry (a wall, a tree).
        /// </summary>
        private static bool SightBlocked(Vector3 from, Vector3 at)
        {
            Vector3 d = at - from;
            float length = d.magnitude;
            if (length < 1e-3f)
            {
                return false;
            }
            Vector3 dir = d / length;
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig != null)
            {
                foreach (Rig.VRHand hand in rig.Hands)
                {
                    if (hand == null || hand.Model == null)
                    {
                        continue;
                    }
                    for (float t = 0f; t < length - 0.04f; t += 0.01f)
                    {
                        if (hand.Model.NearSkin(from + dir * t, 0.01f))
                        {
                            return true;
                        }
                    }
                }
            }
            int mask = Physics.DefaultRaycastLayers & ~((1 << 2) | (1 << 5) | (1 << 8) | (1 << 10) | (1 << 16) | (1 << 17) | (1 << 18));
            int props = Weapons.PropPhysics.Layer;
            if (props >= 0)
            {
                mask &= ~(1 << props);
            }
            return Physics.Linecast(from, at - dir * 0.05f, mask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// A picture camera at <paramref name="p"/> would sit inside one of the weapon's own meshes (its arms left out): an
        /// akimbo pair's other pistol beside the gun filled the magazine close-ups with flat colour.
        /// </summary>
        private static bool InsideWeapon(HeldWeapon held, Vector3 p)
        {
            if (held.Weapon == null)
            {
                return false;
            }
            var arms = new HashSet<Renderer>(held.Rig.ArmRenderers);
            foreach (Renderer r in held.Weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || arms.Contains(r))
                {
                    continue;
                }
                MeshFilter mf = r is MeshRenderer ? r.GetComponent<MeshFilter>() : null;
                Mesh mesh = mf != null ? mf.sharedMesh : null;
                if (mesh == null)
                {
                    if (r.bounds.Contains(p) && r.bounds.size.magnitude < 0.6f)
                    {
                        return true;
                    }
                    continue;
                }
                Bounds local = mesh.bounds;
                local.Expand(0.02f / Mathf.Max(1e-4f, r.transform.lossyScale.x));
                if (local.Contains(r.transform.InverseTransformPoint(p)))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A still hand must hold a still weapon (Tasks 203: RPGs shook in the hand without firing). Held 1.5 s one-handed,
        /// then with the support hand on it: the most the gun body turns and moves from one frame to the next.
        /// </summary>
        private static IEnumerator SteadyCheck(HeldWeapon held, string name, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig == null || held.Rig.GunBody == null)
            {
                yield break;
            }
            string Measure(string how, List<float> angles, List<float> moves)
            {
                float maxA = angles.Count > 0 ? angles.Max() : 0f, maxM = moves.Count > 0 ? moves.Max() : 0f;
                float avgA = angles.Count > 0 ? angles.Average() : 0f, avgM = moves.Count > 0 ? moves.Average() : 0f;
                Metric($"held still ({how}): most turn between frames, degrees", maxA);
                Metric($"held still ({how}): most move between frames, mm", maxM * 1000f);
                bool shaky = maxA > 0.2f || maxM > 0.002f;
                // How many frames went over: one is a jump (the PKP's one 0.38 deg frame, 2026-10-25, none in the run before), a
                // shake keeps going over.
                int over = 0;
                for (int i = 0; i < angles.Count && i < moves.Count; i++)
                {
                    over += angles[i] > 0.2f || moves[i] > 0.002f ? 1 : 0;
                }
                string overNote = shaky ? $", {over} of {angles.Count} frames over" : "";
                if (shaky)
                {
                    Problem($"[{name}] held still ({how}) the weapon shakes: up to {maxA:0.00} deg and {maxM * 1000f:0.0} mm between frames (average {avgA:0.000} deg, {avgM * 1000f:0.00} mm{overNote})");
                }
                return $"{how}: up to {maxA:0.00} deg / {maxM * 1000f:0.0} mm a frame (average {avgA:0.000} deg / {avgM * 1000f:0.00} mm){(shaky ? $" (SHAKES{overNote})" : "")}";
            }
            IEnumerator Sample(List<float> angles, List<float> moves)
            {
                yield return new WaitForSecondsRealtime(0.5f); // settled first
                Transform body = held.Rig.GunBody;
                Vector3 lastPos = body.position - rig.Root.position;
                Quaternion lastRot = body.rotation;
                for (float until = Time.realtimeSinceStartup + 1.5f; Time.realtimeSinceStartup < until;)
                {
                    yield return new WaitForEndOfFrame();
                    if (body == null)
                    {
                        yield break;
                    }
                    Vector3 pos = body.position - rig.Root.position; // the rig itself may be carried by the player's body
                    // As much as a 90 Hz headset frame would show: a frame longer than that (the busy PC's 30 fps) carried three
                    // frames of a slow drift and read as a shake (the PKP's 0.4 deg frames, 2026-10-25; 0.00 at 13 ms frames).
                    float toHeadsetFrame = Mathf.Min(1f, (1f / 90f) / Mathf.Max(Time.unscaledDeltaTime, 1e-4f));
                    angles.Add(Quaternion.Angle(lastRot, body.rotation) * toHeadsetFrame);
                    moves.Add(Vector3.Distance(lastPos, pos) * toHeadsetFrame);
                    lastPos = pos;
                    lastRot = body.rotation;
                }
            }
            var a1 = new List<float>();
            var m1 = new List<float>();
            yield return Sample(a1, m1);
            string line = "steady: " + Measure("one hand", a1, m1);
            // The same with a real controller's tracking noise in its velocity (2 cm/s): the scripted hand reports none, and
            // the shake a player saw on the RPGs never showed here (Tasks 203).
            var a3 = new List<float>();
            var m3 = new List<float>();
            Rig.VRHand.TestVelocityNoise = 0.02f;
            try
            {
                yield return Sample(a3, m3);
            }
            finally
            {
                Rig.VRHand.TestVelocityNoise = 0f;
            }
            line += "; " + Measure("one hand, tracking noise", a3, m3);
            Rig.VRHand off = rig.Off;
            if (held.Rig.Data.HasSupportPoint && off.Role == Rig.HandRole.Free)
            {
                Input.VRInput.SimulatedGrab[(int)off.Side] = true;
                off.Role = Rig.HandRole.Foregrip;
                held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
                var a2 = new List<float>();
                var m2 = new List<float>();
                yield return Sample(a2, m2);
                line += "; " + Measure("two hands", a2, m2);
                Input.VRInput.SimulatedGrab[(int)off.Side] = false;
                yield return null;
            }
            report(line);
        }

        /// <summary>
        /// The support hand on the gun, pulled away (user, 2026-10-19: "the hand cant go anywhere else except that grip (only if the
        /// user pulls hard away)"; task 282): moved 20 cm across the gun in 0.3 s and held there, it stays on (the gun follows,
        /// with a buzz); yanked 80 cm across in a quarter of a second, it lets go. Either way round is a problem.
        /// </summary>
        private static IEnumerator SupportPullCheck(HeldWeapon held, string name, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            Rig.VRHand off = rig != null ? rig.Off : null;
            if (off == null || !held.Rig.Data.HasSupportPoint || held.Rig.Data.IsPistol || off.Role != Rig.HandRole.Free)
            {
                yield break;
            }
            int side = (int)off.Side;
            // The palm put on the gun's support point first, as a real grab takes it there (from the hand's simulated place
            // 40 cm off, a rear grip or a magazine hold measured its stretch from there and an 80 cm yank added only 50 cm:
            // check-1019h's RPG-7 and MG3 "still holds" were the test's).
            Vector3 palmToRaw = off.Raw.position - off.Palm.position;
            Runtime.RigidPose start = new Runtime.RigidPose(held.Rig.ToWorld(held.Rig.Data.SupportPoint) + palmToRaw, off.Raw.rotation);
            Rig.VRHand.Scripted[side] = start;
            yield return null;
            yield return null;
            Vector3 across = Vector3.Cross(held.Rig.AimWorld, Vector3.up);
            across = across.sqrMagnitude > 1e-4f ? across.normalized : rig.Head.right;
            bool stayed = true, letGo = false;
            float letGoAt = -1f;
            Input.VRInput.SimulatedGrab[side] = true;
            off.Role = Rig.HandRole.Foregrip;
            held.SetTwoHanded(VRConfig.TwoHandedAiming.Value);
            float glideMax = -1f;
            try
            {
                yield return new WaitForSecondsRealtime(0.4f);
                // Slid 10 cm along the gun with the grip closed: the glove stays where it gripped (task 324: it followed the hand
                // along the handguard). Measured on the glove's snap pose in the gun body's frame.
                Transform body = held.Rig.GunBody;
                Vector3? pinned = off.Model != null && off.Model.SnapPose.HasValue && body != null ? body.InverseTransformPoint(off.Model.SnapPose.Value.position) : (Vector3?)null;
                if (pinned.HasValue)
                {
                    glideMax = 0f;
                    for (float t = 0f; t < 0.5f && off.Role == Rig.HandRole.Foregrip; t += Time.unscaledDeltaTime)
                    {
                        Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start.position - held.Rig.AimWorld * (0.1f * Mathf.Clamp01(t / 0.25f)), start.rotation);
                        yield return null;
                        if (off.Model.SnapPose.HasValue && body != null)
                        {
                            glideMax = Mathf.Max(glideMax, Vector3.Distance(body.TransformPoint(pinned.Value), off.Model.SnapPose.Value.position));
                        }
                        else
                        {
                            glideMax = 1f; // let go of its snap: the glove went to the tracked hand
                        }
                    }
                    Rig.VRHand.Scripted[side] = start;
                    yield return new WaitForSecondsRealtime(0.2f);
                }
                for (float t = 0f; t < 0.9f; t += Time.unscaledDeltaTime)
                {
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start.position + across * (0.2f * Mathf.Clamp01(t / 0.3f)), start.rotation);
                    yield return null;
                    stayed &= off.Role == Rig.HandRole.Foregrip;
                }
                Rig.VRHand.Scripted[side] = start;
                yield return new WaitForSecondsRealtime(0.4f);
                for (float t = 0f; t < 0.6f && off.Role == Rig.HandRole.Foregrip; t += Time.unscaledDeltaTime)
                {
                    float k = Mathf.Clamp01(t / 0.25f);
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start.position + across * (0.8f * k), start.rotation);
                    yield return null;
                    if (off.Role != Rig.HandRole.Foregrip)
                    {
                        letGoAt = 0.8f * k;
                    }
                }
                letGo = off.Role != Rig.HandRole.Foregrip;
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
                Input.VRInput.SimulatedGrab[side] = false;
            }
            yield return null; // the grip opens: the hand is free again
            string glide = glideMax < 0f ? "no snap pose to measure" : glideMax < 0.01f ? $"the glove stays where it gripped ({glideMax * 100f:0.0} cm)" : $"the glove NOT HELD where it gripped (moved {glideMax * 100f:0.0} cm)";
            report($"support pull: slid 10 cm along the gun, {glide}; 20 cm across {(stayed ? "stays on the gun" : "LET GO")}; yanked 80 cm across {(letGo ? letGoAt > 0f ? $"lets go at about {letGoAt * 100f:0} cm" : "lets go" : "STILL HOLDS")}");
            if (glideMax >= 0.01f)
            {
                Problem($"[{name}] support hand: the glove followed the hand {glideMax * 100f:0.0} cm along the gun while gripping (it should stay where it gripped)");
            }
            if (!stayed)
            {
                Problem($"[{name}] support hand: let go of the gun on a 20 cm pull (it should stay on until pulled hard)");
            }
            if (!letGo)
            {
                Problem($"[{name}] support hand: still on the gun after an 80 cm yank (a hard pull should let go)");
            }
        }

        /// <summary>
        /// Task 301: the free hand grips the flip magnifier at its middle, slides it 6 cm across the bore and back twice (two
        /// flips: the magnifier ends as it was), then lets go held still (no flip). The hand must take it, flip once per slide,
        /// and be free afterwards.
        /// </summary>
        private static IEnumerator MagnifierHandCheck(HeldWeapon held, string name, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            Rig.VRHand off = rig != null ? rig.Off : null;
            Weapons.MagnifierFlip grip = held.MagnifierGrip;
            if (off == null || off.Role != Rig.HandRole.Free || !grip.HandlesWeapon(held.Weapon))
            {
                yield break;
            }
            int side = (int)off.Side;
            Transform part = grip.Part;
            Renderer r = part.GetComponentInChildren<Renderer>();
            Vector3 centre = r != null ? r.bounds.center : part.position;
            Vector3 palmToRaw = off.Raw.position - off.Palm.position;
            var start = new Runtime.RigidPose(centre + palmToRaw, off.Raw.rotation);
            Vector3 across = held.Rig.DirToWorld(Vector3.Cross(held.Rig.Data.Up, held.Rig.Data.AimAxis).normalized);
            int before = grip.Flips;
            bool took = false;
            var perSlide = new List<int>();
            try
            {
                Rig.VRHand.Scripted[side] = start;
                yield return null;
                yield return null;
                Input.VRInput.SimulateGrabPress(off.Side);
                yield return null;
                yield return null;
                took = off.Role == Rig.HandRole.Magnifier;
                for (int pass = 0; pass < 2 && took; pass++)
                {
                    int at = grip.Flips;
                    for (float t = 0f; t < 0.3f; t += Time.unscaledDeltaTime)
                    {
                        Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start.position + across * (0.06f * Mathf.Clamp01(t / 0.2f)), start.rotation);
                        yield return null;
                    }
                    for (float t = 0f; t < 0.3f; t += Time.unscaledDeltaTime)
                    {
                        Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start.position + across * (0.06f * (1f - Mathf.Clamp01(t / 0.2f))), start.rotation);
                        yield return null;
                    }
                    perSlide.Add(grip.Flips - at);
                    yield return new WaitForSecondsRealtime(0.3f);
                }
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
                Input.VRInput.SimulatedGrab[side] = false;
            }
            int atLetGo = grip.Flips;
            yield return null;
            yield return null;
            bool freed = off.Role != Rig.HandRole.Magnifier;
            int onLetGo = grip.Flips - atLetGo;
            report($"magnifier by hand ('{part.name}'): {(took ? "taken" : "NOT TAKEN")}, flips per 6 cm slide {(perSlide.Count > 0 ? string.Join(", ", perSlide) : "-")}, "
                   + $"on letting go {onLetGo}, hand {(freed ? "free again" : "STILL ON THE MAGNIFIER")} ({grip.Flips - before} in all)");
            if (!took)
            {
                Problem($"[{name}] magnifier: the free hand at its middle did not take it");
            }
            else if (perSlide.Exists(n => n != 1))
            {
                Problem($"[{name}] magnifier: a 6 cm slide across the bore flipped it {string.Join(", ", perSlide)} time(s) (once each expected)");
            }
            if (onLetGo != 0 || !freed)
            {
                Problem($"[{name}] magnifier: letting go {(onLetGo != 0 ? "flipped it" : "left the hand on it")}");
            }
        }

        /// <summary>
        /// Weight (Tasks 208): the gun hand lifted at 1 m/s for 0.3 s and stopped. A heavier weapon lags the lift more and
        /// swings past more after the stop; a pistol stays in the hand. Measured one-handed.
        /// </summary>
        private static IEnumerator WeightCheck(HeldWeapon held, string name, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig == null || !VRConfig.WeaponWeight.Value || rig.Off.Role != Rig.HandRole.Free)
            {
                yield break;
            }
            Rig.VRHand dom = rig.Dominant;
            int side = (int)dom.Side;
            Runtime.RigidPose start = dom.Raw;
            float maxLag = 0f, maxOver = 0f, maxDip = 0f, minPitch = 0f;
            try
            {
                for (float t = 0f; t < 0.3f; t += Time.unscaledDeltaTime)
                {
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start.position + Vector3.up * t, start.rotation);
                    Rig.VRHand.ScriptedVelocity[side] = Vector3.up;
                    yield return null;
                    maxLag = Mathf.Max(maxLag, -held.InertiaLag.y);
                    maxDip = Mathf.Max(maxDip, held.InertiaPitch);
                }
                Runtime.RigidPose top = new Runtime.RigidPose(start.position + Vector3.up * 0.3f, start.rotation);
                for (float t = 0f; t < 0.6f; t += Time.unscaledDeltaTime)
                {
                    Rig.VRHand.Scripted[side] = top;
                    Rig.VRHand.ScriptedVelocity[side] = Vector3.zero;
                    yield return null;
                    maxOver = Mathf.Max(maxOver, held.InertiaLag.y);
                    minPitch = Mathf.Min(minPitch, held.InertiaPitch);
                }
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
                Rig.VRHand.ScriptedVelocity[side] = Vector3.zero;
            }
            Metric("weight: lag lifting 1 m/s, cm", maxLag * 100f);
            Metric("weight: swing past after stopping, cm", maxOver * 100f);
            report($"weight: lifting at 1 m/s it lags up to {maxLag * 100f:0.0} cm, the muzzle dips {maxDip:0.0} deg; stopped, it swings {maxOver * 100f:0.0} cm past (muzzle {-minPitch:0.0} deg up)");
            if (held.Rig.Data.IsPistol && (maxLag > 0.005f || maxOver > 0.005f))
            {
                Problem($"[{name}] a pistol lags the hand ({maxLag * 100f:0.0} cm)");
            }
            yield return new WaitForSecondsRealtime(0.4f);
        }

        /// <summary>Footsteps and landings the game played (counted by <see cref="CountFootstepsPatch"/>).</summary>
        internal static int Footsteps;

        /// <summary>
        /// Standing still with the head swaying a millimetre or two, as it does in a headset: no footsteps. The room-scale
        /// step moved the body along the floor only, which lost the ground contact, and every landing played a step.
        /// </summary>
        private static IEnumerator StandingStillCheck()
        {
            if (GameContext.Player == null)
            {
                Results.Add("Standing still: skipped (no player)");
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.5f);
            Footsteps = 0;
            float until = Time.realtimeSinceStartup + 4f;
            int frame = 0;
            try
            {
                while (Time.realtimeSinceStartup < until)
                {
                    frame++;
                    Rig.VRRig.TestHeadOffset = new Vector3(Mathf.Sin(frame * 0.7f), 0f, Mathf.Cos(frame * 0.45f)) * 0.0015f;
                    yield return null;
                }
            }
            finally
            {
                Rig.VRRig.TestHeadOffset = Vector3.zero;
            }
            Results.Add($"Standing still with the head swaying 1.5 mm for 4 s: {Footsteps} footstep(s)");
            if (Footsteps > 0)
            {
                Problem($"[footsteps] {Footsteps} footstep(s) while standing still");
            }
        }

        /// <summary>
        /// The gun hand lets go while the other hand holds the handguard: the weapon stays drawn, in the other hand, held where
        /// that hand holds it (its grip where it was against that hand: user, 2026-11-03, it is not snapped into the hand by its
        /// grip any more), and that hand is the gun hand. Then given back.
        /// </summary>
        private static IEnumerator PassHandCheck(HeldWeapon held, string name, Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (sys == null || rig == null || !held.Rig.Data.HasSupportPoint)
            {
                yield break;
            }
            Weapon weapon = held.Weapon;
            Rig.VRHand from = rig.Dominant, to = rig.Off;
            int toSide = (int)to.Side;
            bool passed;
            Input.VRInput.SimulatedGrab[toSide] = true; // the other hand's grip closed on the handguard
            to.Role = Rig.HandRole.Foregrip;
            Vector3 gripBefore = Quaternion.Inverse(to.Palm.rotation) * (held.GripWorld.position - to.Palm.position);
            passed = sys.TestPassToOtherHand();
            yield return null;
            yield return null;
            yield return new WaitForSecondsRealtime(0.3f);
            HeldWeapon now = sys.Held;
            bool same = now != null && now.Weapon == weapon;
            bool roles = rig.Dominant == to && to.Role == Rig.HandRole.Weapon && from.Role == Rig.HandRole.Free;
            // How far the grip moved against the hand that took it (it stays where it was: held there, not by the grip).
            float gap = same ? Vector3.Distance(Quaternion.Inverse(to.Palm.rotation) * (now.GripWorld.position - to.Palm.position), gripBefore) : -1f;
            yield return new WaitForEndOfFrame();
            Shots.Capture($"pass-{name}");
            report($"passed to the other hand: {(passed ? "yes" : "NO")}, {(same ? "the weapon still drawn" : "THE WEAPON WAS PUT AWAY")}, "
                   + $"{(roles ? "that hand holds it" : $"HANDS WRONG (gun hand {rig.Dominant.Side} {rig.Dominant.Role}, other {rig.Off.Role})")}, "
                   + $"the grip moved {gap * 100f:0.0} cm against that hand");
            Metric("weapon passed to the other hand: grip moved cm against that hand", gap * 100f);
            if (!passed || !same || !roles || gap > 0.03f)
            {
                Problem($"[{name}] passing the weapon to the other hand failed (passed {passed}, still drawn {same}, hands right {roles}, grip moved {gap * 100f:0.0} cm)");
            }
            // Given back: the first hand takes it again.
            Input.VRInput.SimulatedGrab[toSide] = false;
            sys.TakeIntoHand(from);
            yield return null;
            yield return new WaitForSecondsRealtime(0.3f);
            if (sys.Held == null || sys.Held.Weapon != weapon || rig.Dominant != from)
            {
                report("passed back: THE WEAPON DID NOT COME BACK to the first hand");
            }
        }

        /// <summary>
        /// Two weapons at once (Tasks 252 p): with this gun drawn, the free hand takes another kind of weapon from the loadout
        /// as a second one. It must show, sit in that hand, fire from that hand's trigger and use its own rounds, go back into
        /// its holster when the hand lets go, and the drawn gun must stay drawn the whole time.
        /// </summary>
        private static IEnumerator SecondWeaponCheck(HeldWeapon held, Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Actor actor = GameContext.PlayerActor;
            if (sys == null || actor == null || actor.weapons == null || held.Weapon == null || sys.Second != null)
            {
                yield break;
            }
            Weapon other = null, grenade = null;
            foreach (Weapon w in actor.weapons)
            {
                if (w != null && w != held.Weapon && SecondWeapon.CanPair(held.Weapon, w))
                {
                    if (w is ThrowableWeapon)
                    {
                        grenade = grenade ?? w;
                    }
                    else
                    {
                        other = other ?? w;
                    }
                }
            }
            if (grenade != null)
            {
                yield return SecondGrenadeCheck(held, grenade, report); // a grenade in the free hand beside the gun (252 p)
            }
            if (other == null)
            {
                report("second weapon: no other kind of weapon in the loadout to pair with");
                yield break;
            }
            Rig.VRHand off = sys.Rig.Off;
            if (off.Role != Rig.HandRole.Free || off.Held != null)
            {
                yield break;
            }
            int side = (int)off.Side;
            Input.VRInput.SimulatedGrab[side] = true;
            try
            {
                sys.BeginSecond(other, off);
                yield return new WaitForSecondsRealtime(Mathf.Clamp(other.configuration.unholsterTime, 0.2f, 1.5f) + 0.3f);
                SecondWeapon second = sys.Second;
                bool drawn = second != null && other.gameObject.activeInHierarchy && !other.isHidden;
                WeaponRig r = second != null ? second.Rig : null;
                float offHand = r != null && r.GunBody != null ? Vector3.Distance(r.ToWorld(r.Data.GripPoint), off.GunFrame.position) : -1f;
                int ammoBefore = other.ammo;
                SecondWeapon.TestTrigger = true;
                yield return new WaitForSecondsRealtime(0.6f);
                SecondWeapon.TestTrigger = false;
                yield return null;
                int shots = second != null ? second.Shots : 0;
                // Its own arms (the game's model, both hands on the gun) stay hidden in the free hand (Task 267).
                int armsShown = -1;
                if (r != null)
                {
                    armsShown = 0;
                    foreach (Renderer ar in r.ArmRenderers)
                    {
                        if (ar != null && ar.enabled)
                        {
                            armsShown++;
                        }
                    }
                }
                bool mainStays = sys.Held != null && sys.Held.Weapon == held.Weapon && actor.activeWeapon == held.Weapon;
                Input.VRInput.SimulatedGrab[side] = false;
                yield return null;
                yield return null;
                bool putAway = sys.Second == null && !other.gameObject.activeSelf;
                bool mainAfter = sys.Held != null && sys.Held.Weapon == held.Weapon;
                Metric("second weapon: cm from the free hand's grip", offHand * 100f);
                report($"second weapon ({other.name}): {(drawn ? "drawn in the free hand" : "NOT DRAWN")}, {offHand * 100f:0.0} cm from the hand's grip{(offHand > 0.03f || offHand < 0f ? " (NOT IN THE HAND)" : "")}, "
                       + $"{(shots > 0 ? $"fired {shots} shot(s), ammo {ammoBefore} -> {other.ammo}" : "NOT FIRED")}, the drawn gun {(mainStays ? "stays drawn" : "WAS PUT AWAY")}, "
                       + $"its own arms {(armsShown == 0 ? "hidden" : armsShown < 0 ? "not measured" : $"SHOWING ({armsShown})")}, "
                       + $"let go: {(putAway ? "back in its holster" : "NOT PUT AWAY")}{(mainAfter ? "" : ", the drawn gun NOT IN THE HAND afterwards")}");
            }
            finally
            {
                SecondWeapon.TestTrigger = false;
                Input.VRInput.SimulatedGrab[side] = false;
            }
            yield return new WaitForSecondsRealtime(0.3f);
        }

        /// <summary>
        /// The second weapon with the gun in either hand (user, 2026-10-21: "taking a pistol with the right hand while holding the
        /// weapon in the left hand or opposite around, the 2nd object sometimes isnt in the hand and floats in the air"): taken
        /// from its holster and picked up from the ground, each frame for 1.5 s from the take, how far its drawn model is from
        /// the hand holding it, the hand moving; with pictures.
        /// </summary>
        private static IEnumerator SecondWeaponFloatCheck(HeldWeapon held, Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Actor actor = GameContext.PlayerActor;
            if (sys == null || actor == null || actor.weapons == null || held.Weapon == null || sys.Second != null)
            {
                yield break;
            }
            Weapon main = held.Weapon, other = null;
            if (main is MeleeWeapon || main is ThrowableWeapon || held.Rig.Data.Action == ActionType.Melee || held.Rig.Data.Action == ActionType.Throwable
                || held.Rig.Data.Action == ActionType.NoAmmo || HeldWeapon.IsLauncher(held.Rig.Data, main))
            {
                // A gun passed to the other hand (a knife is not passed by its handguard; a modded one is no MeleeWeapon). Not a
                // launcher either: the SMAW was not taken up by the other hand here (Tasks 326 w, to look at on its own).
                yield break;
            }
            foreach (Weapon w in actor.weapons)
            {
                if (w != null && w != main && !(w is ThrowableWeapon) && SecondWeapon.CanPair(main, w))
                {
                    other = w;
                    break;
                }
            }
            if (other == null)
            {
                yield break;
            }
            WeaponManager.WeaponEntry otherEntry = other.weaponEntry;
            Input.Hand startHand = Input.VRInput.GunHand;
            var parts = new List<string>();
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1)
                {
                    // The gun to the other hand, as letting go while that hand holds the handguard does.
                    sys.TakeIntoHand(sys.Rig.Off);
                    float until = Time.realtimeSinceStartup + 2f;
                    while (Time.realtimeSinceStartup < until && (sys.Held == null || sys.Held.Weapon != main))
                    {
                        yield return null;
                    }
                    if (sys.Held == null || sys.Held.Weapon != main)
                    {
                        parts.Add("gun not taken up by the other hand");
                        break;
                    }
                }
                for (int way = 0; way < 2; way++)
                {
                    // Found again each time: a pickup replaces the pistol of that slot (the old one is destroyed).
                    other = null;
                    foreach (Weapon w in actor.weapons)
                    {
                        if (w != null && w != main && !(w is ThrowableWeapon) && SecondWeapon.CanPair(main, w))
                        {
                            other = w;
                            break;
                        }
                    }
                    if (other == null)
                    {
                        parts.Add("no second weapon left to take");
                        break;
                    }
                    Rig.VRHand hand = sys.Rig.Off;
                    if (hand.Role != Rig.HandRole.Free || hand.Held != null || sys.Second != null)
                    {
                        parts.Add($"{hand.Side} hand busy");
                        continue;
                    }
                    int side = (int)hand.Side;
                    Runtime.RigidPose start = hand.Raw;
                    string label = $"{(pass == 0 ? "usual" : "swapped")}-{(way == 0 ? "holster" : "ground")}";
                    try
                    {
                        if (way == 0)
                        {
                            Input.VRInput.SimulatedGrab[side] = true;
                            sys.BeginSecond(other, hand);
                        }
                        else
                        {
                            // A pistol of that kind lying at the hand, taken up by it (it goes beside the gun, the one it replaces
                            // to the ground).
                            DroppedWeapon lying = otherEntry != null
                                ? sys.Drops.Spawn(otherEntry, new Runtime.RigidPose(hand.Palm.position + hand.Palm.Forward * 0.03f, hand.Palm.rotation), Vector3.one, Vector3.zero, -1, -1)
                                : null;
                            if (lying == null)
                            {
                                parts.Add("no pistol to lay down");
                                continue;
                            }
                            if (lying.Go.GetComponent<Rigidbody>() is Rigidbody lyingBody)
                            {
                                lyingBody.isKinematic = true; // it waits at the hand
                            }
                            yield return new WaitForSecondsRealtime(0.25f);
                            Input.VRInput.SimulatedGrab[side] = true;
                            Input.VRInput.SimulateGrabPress(hand.Side);
                            float until = Time.realtimeSinceStartup + 1f;
                            while (Time.realtimeSinceStartup < until && sys.Second == null)
                            {
                                yield return null;
                            }
                            if (sys.Second == null)
                            {
                                parts.Add($"{label}: NOT PICKED UP into the {hand.Side} hand");
                                if (lying.Go != null)
                                {
                                    UnityEngine.Object.Destroy(lying.Go);
                                }
                                continue;
                            }
                        }
                        SecondWeapon second = sys.Second;
                        Weapon drawn = second != null ? second.Weapon : null;
                        WeaponRig r = second != null ? second.Rig : null;
                        var arms = r != null ? new HashSet<Renderer>(r.ArmRenderers) : new HashSet<Renderer>();
                        float worstModel = 0f, worstGrip = 0f, worstAt = 0f, graspSum = 0f, graspMax = 0f;
                        int frames = 0, posed = 0, graspN = 0, snapped = 0;
                        string glove = "";
                        float began = Time.realtimeSinceStartup;
                        while (Time.realtimeSinceStartup - began < 1.5f && sys.Second == second && second != null)
                        {
                            float t = Time.realtimeSinceStartup - began;
                            // The hand moving as a hand does (20 cm side to side, at about 1 Hz).
                            Vector3 sway = start.rotation * new Vector3(Mathf.Sin(t * 6.3f) * 0.1f, 0f, 0f);
                            Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start.position + sway, start.rotation);
                            yield return new WaitForEndOfFrame(); // after this frame's render: where it was drawn
                            frames++;
                            if (r == null || r.GunBody == null || drawn == null)
                            {
                                continue;
                            }
                            Bounds b = default;
                            bool any = false;
                            foreach (Renderer rr in drawn.GetComponentsInChildren<Renderer>())
                            {
                                if (rr == null || !rr.enabled || !rr.gameObject.activeInHierarchy || arms.Contains(rr) || rr is ParticleSystemRenderer)
                                {
                                    continue;
                                }
                                if (any)
                                {
                                    b.Encapsulate(rr.bounds);
                                }
                                else
                                {
                                    b = rr.bounds;
                                    any = true;
                                }
                            }
                            if (!any)
                            {
                                continue;
                            }
                            posed++;
                            float model = Mathf.Sqrt(b.SqrDistance(hand.PalmCentre)); // 0 when the hand is inside the drawn model's box
                            float grip = Vector3.Distance(r.ToWorld(r.Data.GripPoint), hand.GunFrame.position);
                            if (model > worstModel)
                            {
                                worstModel = model;
                                worstAt = t;
                            }
                            worstGrip = Mathf.Max(worstGrip, grip);
                            if (t > 0.5f && hand.Model != null)
                            {
                                // The drawn glove's palm centre (its root, as placed) against the weapon's grip, once it has settled, and
                                // whether it is on the weapon's own grip pose (as the gun hand's glove; left on the tracked hand it was not).
                                float grasp = Vector3.Distance(hand.Model.Root.position, r.ToWorld(r.Data.GripPoint));
                                snapped += hand.Model.SnapPose.HasValue ? 1 : 0;
                                graspSum += grasp;
                                graspMax = Mathf.Max(graspMax, grasp);
                                graspN++;
                            }
                            if (frames == 3 || Mathf.Abs(t - 0.8f) < 0.012f)
                            {
                                glove += (glove.Length > 0 ? ", then " : "") + $"{t:0.00} s: {second.GloveState}";
                                Vector3 look = hand.PalmCentre;
                                Vector3 from = look + Vector3.up * 0.15f + Vector3.ProjectOnPlane(sys.Rig.Head.forward, Vector3.up).normalized * 0.55f;
                                Shots.Capture($"second-float-{label}-{frames:000}-{drawn.name}", from, Quaternion.LookRotation(look - from), 70f);
                            }
                        }
                        Metric($"second weapon {label}: worst cm of its drawn model from the hand", worstModel * 100f);
                        parts.Add($"{label} (gun in the {Input.VRInput.GunHand} hand, {(drawn != null ? drawn.name : "?")} in the {hand.Side}): drawn model up to "
                                  + $"{worstModel * 100f:0.0} cm from the hand{(worstModel > 0.03f ? $" at {worstAt:0.00} s (FLOATING)" : "")}, grip up to {worstGrip * 100f:0.0} cm off, "
                                  + $"{posed}/{frames} frames drawn; the glove's palm {(graspN > 0 ? graspSum / graspN * 100f : -1f):0.0} cm from its grip "
                                  + $"(most {graspMax * 100f:0.0}), on the weapon's own grip pose {snapped}/{graspN} frames; the hand {glove}");
                    }
                    finally
                    {
                        Rig.VRHand.Scripted[side] = null;
                        Input.VRInput.SimulatedGrab[side] = false;
                    }
                    yield return null;
                    yield return null;
                    yield return new WaitForSecondsRealtime(0.3f);
                }
            }
            if (Input.VRInput.GunHand != startHand)
            {
                sys.TakeIntoHand(sys.Rig.Hands[(int)startHand]);
            }
            // The gun back in the hand it started in, drawn again by its slot if it did not come back by itself.
            float back = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < back && (sys.Held == null || sys.Held.Weapon != main))
            {
                if (main != null && actor.activeWeapon != main && Time.realtimeSinceStartup > back - 1.5f)
                {
                    int slot = System.Array.IndexOf(actor.weapons, main);
                    if (slot >= 0)
                    {
                        actor.SwitchWeapon(slot);
                    }
                }
                yield return null;
            }
            report("second weapon with the gun in either hand: " + string.Join("; ", parts));
        }

        /// <summary>
        /// A grenade taken by the free hand beside the drawn gun (252 p): drawn in that hand, its pin pulled with that hand's
        /// trigger, thrown live (the projectile leaves with the throw), the hand empty after, and the gun still drawn.
        /// </summary>
        private static IEnumerator SecondGrenadeCheck(HeldWeapon held, Weapon grenade, Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Actor actor = GameContext.PlayerActor;
            Rig.VRHand off = sys.Rig.Off;
            if (off.Role != Rig.HandRole.Free || off.Held != null || sys.Second != null || grenade.ammo == 0)
            {
                report($"second grenade: skipped ({(grenade.ammo == 0 ? "none left" : "the off hand is busy")})");
                yield break;
            }
            int side = (int)off.Side;
            Input.VRInput.SimulatedGrab[side] = true;
            int before = grenade.ammo;
            try
            {
                sys.BeginSecond(grenade, off);
                yield return new WaitForSecondsRealtime(Mathf.Clamp(grenade.configuration.unholsterTime, 0.2f, 1.5f) + 0.3f);
                SecondWeapon second = sys.Second;
                bool drawn = second != null && grenade.gameObject.activeInHierarchy && !grenade.isHidden;
                SecondWeapon.TestTrigger = true;
                yield return null;
                yield return null;
                bool armed = second != null && second.Armed;
                // Thrown up and away from the player, over the gun.
                Transform head = sys.Rig.Head;
                Vector3 throwV = (Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized + Vector3.up * 0.6f).normalized * 9f;
                second?.TestThrow(throwV);
                int live = second != null ? second.LastLiveCount : 0;
                SecondWeapon.TestTrigger = false;
                yield return null;
                yield return null;
                bool emptied = sys.Second == null;
                bool mainStays = sys.Held != null && sys.Held.Weapon == held.Weapon && actor.activeWeapon == held.Weapon;
                report($"second grenade ({grenade.name}): {(drawn ? "drawn in the free hand" : "NOT DRAWN")}, {(armed ? "pin pulled by its trigger" : "PIN NOT PULLED")}, "
                       + $"{(live > 0 ? $"thrown live ({live} projectile(s)), {before} -> {grenade.ammo} left" : "NOTHING THROWN")}, {(emptied ? "the hand empty after" : "STILL IN THE HAND after the throw")}, "
                       + $"the drawn gun {(mainStays ? "stays drawn" : "WAS PUT AWAY")}");
            }
            finally
            {
                SecondWeapon.TestTrigger = false;
                Input.VRInput.SimulatedGrab[side] = false;
            }
            yield return new WaitForSecondsRealtime(3.5f); // the grenade goes off far from the player before the next check
        }

        /// <summary>
        /// A pistol's slide locked back with a magazine in: its animator must show no round loaded, or the slide runs
        /// forward while the gun still says RACK.
        /// </summary>
        private static IEnumerator SlideLockPoseCheck(HeldWeapon held, Action<string> report)
        {
            Weapon w = held.ActiveWeapon;
            AmmoState ammo = held.ActiveAmmo;
            if (w == null || ammo == null || !ammo.UsesDetachableMag || !w.notifyAnimatorAmmo || !w.HasActiveAnimator())
            {
                yield break;
            }
            bool locked = ammo.SlideLocked, chambered = ammo.Chambered;
            ammo.SlideLocked = true;
            ammo.Chambered = false;
            ammo.Sync();
            yield return null;
            yield return null;
            int shown = w.animator.GetInteger(Weapon.LOADED_AMMO_PARAMETER_HASH);
            // Where the mod moves the slide part itself it stays back whatever the animator shows (the ARS Glock 19's
            // animator is driven by a script of its own).
            bool modSlide = held.ActiveRig.Data.SlidePath != null && held.SlideOffset > 0.0005f;
            report($"slide locked with a magazine in ({ammo.MagRounds} rounds): the animator shows {shown} loaded ({(shown == 0 ? "slide held back" : modSlide ? "the mod holds the slide part back" : "THE SLIDE RUNS FORWARD")})");
            // (A belt gun's animator is told the box's rounds, slide or no slide: it draws its belt by them; HoldSlideLockPose.)
            bool beltDrawn = ammo.HasCover && ammo.MagInserted;
            if (shown != 0 && !modSlide && !beltDrawn)
            {
                Problem($"[{w.name}] with the slide locked and a magazine in, the animator shows {shown} rounds loaded");
            }
            ammo.SlideLocked = locked;
            ammo.Chambered = chambered;
            ammo.Sync();
        }

        /// <summary>
        /// The wrist view, the wrist held looked at for 3.5 s (a test hook: the null driver's hands cannot turn): the ring
        /// fills for 2 s, then the scoreboard, the map and the match bar open and stay open without one flicker, and the
        /// game never counts a menu open meanwhile (the focused scoreboard did: the keys let go, it closed, it opened again,
        /// every other frame). Let go, all of it closes.
        /// </summary>
        private static IEnumerator WristViewCheck()
        {
            if (GameContext.Player == null || ScoreboardUi.instance == null || MinimapUi.instance == null)
            {
                Results.Add("Wrist view: skipped (no player or no scoreboard)");
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.3f);
            float start = Time.realtimeSinceStartup;
            float openedAt = -1f, ringAtOne = -1f, longestFrame = 0f;
            int frames = 0, scoreToggles = 0, mapToggles = 0, menuFrames = 0, scoreClosed = 0, mapClosed = 0;
            bool lastScore = false, lastMap = false;
            float barMin = 2f;
            UI.CanvasManager canvases = VRManager.Instance != null && VRManager.Instance.UI != null ? VRManager.Instance.UI.Canvases : null;
            BodyZones.TestWristLook = true;
            try
            {
                while (Time.realtimeSinceStartup - start < 3.6f)
                {
                    yield return null;
                    longestFrame = Mathf.Max(longestFrame, Time.unscaledDeltaTime);
                    float t = Time.realtimeSinceStartup - start;
                    if (t > 1f && t < 1.1f)
                    {
                        yield return new WaitForEndOfFrame();
                        Shots.Capture("wrist-view-ring-half");
                    }
                    if (ringAtOne < 0f && Input.VRGameInput.WristLookProgress >= 1f)
                    {
                        ringAtOne = t;
                    }
                    if (openedAt < 0f && Input.VRGameInput.WristLookGesture)
                    {
                        openedAt = t;
                    }
                    // Measured from 0.3 s after it opened (the map slides in over a few frames).
                    if (openedAt < 0f || t - openedAt < 0.3f)
                    {
                        continue;
                    }
                    bool score = ScoreboardUi.instance.canvas.enabled;
                    bool map = MinimapUi.instance.canvas.enabled;
                    if (frames > 0)
                    {
                        scoreToggles += score != lastScore ? 1 : 0;
                        mapToggles += map != lastMap ? 1 : 0;
                    }
                    lastScore = score;
                    lastMap = map;
                    frames++;
                    scoreClosed += score ? 0 : 1;
                    mapClosed += map ? 0 : 1;
                    menuFrames += GameContext.GameMenuOpen ? 1 : 0;
                    if (canvases != null)
                    {
                        barMin = Mathf.Min(barMin, canvases.MatchBarAlpha);
                    }
                }
                yield return new WaitForEndOfFrame();
                Shots.Capture("wrist-view-open");
            }
            finally
            {
                BodyZones.TestWristLook = false;
            }
            yield return new WaitForSecondsRealtime(0.6f);
            bool closed = !Input.VRGameInput.WristLookGesture && !ScoreboardUi.instance.canvas.enabled && !MinimapUi.instance.canvas.enabled;
            float barAfter = canvases != null ? canvases.MatchBarAlpha : -1f;
            Results.Add($"Wrist view held 3.6 s: ring full at {ringAtOne:0.00} s, opened at {openedAt:0.00} s; over {frames} frames open: scoreboard closed {scoreClosed} / toggled {scoreToggles}, "
                        + $"map closed {mapClosed} / toggled {mapToggles}, a game menu counted open {menuFrames}, match bar alpha at least {barMin:0.00}; let go: {(closed ? "all closed" : "STILL OPEN")}, match bar {barAfter:0.00}");
            Metric("wrist view: flicker (scoreboard and map toggles while held)", scoreToggles + mapToggles);
            Metric("wrist view: seconds before it opens", openedAt);
            // Seen a frame late at worst: a slow run (the null driver at 12 frames a second, hitches of most of a second)
            // opened it at 2.8 s.
            if (openedAt < 1.8f || openedAt > 2.4f + longestFrame || frames == 0 || scoreToggles + mapToggles > 0 || scoreClosed + mapClosed > 0 || menuFrames > 0 || !closed)
            {
                Problem($"[wrist view] opened at {openedAt:0.00} s, {frames} frames: scoreboard closed {scoreClosed}/toggled {scoreToggles}, map closed {mapClosed}/toggled {mapToggles}, menu {menuFrames}, closed after {closed}");
            }
            if (barMin >= 0f && barMin < 0.9f || barAfter > 0.1f)
            {
                Problem($"[match bar] alpha {barMin:0.00} while the wrist view was open, {barAfter:0.00} after (want 1 and 0)");
            }
        }

        /// <summary>
        /// The game's pause menu: the laser aimed at each of its buttons in turn (by the dominant hand), and what it is over
        /// then. Every button must be what the laser hits (only one of them took clicks in the headset). Nothing is clicked.
        /// </summary>
        private static IEnumerator PauseMenuCheck()
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig == null || IngameMenuUi.instance == null)
            {
                Results.Add("Pause menu: skipped (no pause menu)");
                yield break;
            }
            IngameMenuUi.Show();
            yield return new WaitForSecondsRealtime(0.8f);
            Rig.VRHand dom = rig.Dominant;
            int side = (int)dom.Side;
            var lines = new List<string>();
            int bad = 0;
            try
            {
                foreach (UnityEngine.UI.Button b in IngameMenuUi.instance.GetComponentsInChildren<UnityEngine.UI.Button>(false))
                {
                    if (!b.isActiveAndEnabled || !b.interactable)
                    {
                        continue;
                    }
                    Vector3 target = ((RectTransform)b.transform).position;
                    for (int k = 0; k < 12; k++)
                    {
                        // The pointer turned onto the button from where the hand is (corrected each frame).
                        Runtime.RigidPose raw = dom.Raw;
                        Quaternion turn = Quaternion.FromToRotation(dom.Pointer.Forward, (target - dom.Pointer.position).normalized);
                        Rig.VRHand.Scripted[side] = new Runtime.RigidPose(dom.Pointer.position + turn * (raw.position - dom.Pointer.position), turn * raw.rotation);
                        yield return null;
                    }
                    GameObject over = UI.VRInputModule.Hovered;
                    bool ok = over != null && (over == b.gameObject || over.transform.IsChildOf(b.transform));
                    bad += ok ? 0 : 1;
                    string label = b.GetComponentInChildren<UnityEngine.UI.Text>() != null ? b.GetComponentInChildren<UnityEngine.UI.Text>().text : b.name;
                    lines.Add($"'{label.Trim()}' {(ok ? "under the laser" : over == null ? "NOTHING UNDER THE LASER" : $"COVERED BY '{over.name}' ({over.GetComponentInParent<Canvas>()?.rootCanvas.name})")}");
                }
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
            }
            // Real clicks with the trigger, the hand wobbling a little as a trigger pull does: Resume must close the menu,
            // Options must open the options.
            yield return ClickMenuButton(dom, "RESUME", lines);
            lines.Add($"after Resume: the menu is {(IngameMenuUi.IsOpen() ? "STILL OPEN" : "closed")}");
            bad += IngameMenuUi.IsOpen() ? 1 : 0;
            IngameMenuUi.Show();
            yield return new WaitForSecondsRealtime(0.6f);
            yield return ClickMenuButton(dom, "OPTIONS", lines);
            yield return new WaitForSecondsRealtime(0.5f);
            bool options = Options.IsOpen();
            // Open is not enough: the panel must be drawn in front of you (it once opened where the main menu had put it).
            UI.CanvasManager canvases = VRManager.Instance != null && VRManager.Instance.UI != null ? VRManager.Instance.UI.Canvases : null;
            Canvas panel = Options.instance != null ? Options.instance.GetComponent<Canvas>() : null;
            bool shown = options && canvases != null && canvases.InView(panel, 45f, 4f);
            lines.Add($"after Options: the options are {(options ? shown ? "open, in front of the eyes" : "open but OUT OF SIGHT" : "NOT OPEN")}"
                      + (canvases != null ? $" ({canvases.Describe(panel)})" : ""));
            bad += shown ? 0 : 1;
            if (options)
            {
                yield return new WaitForEndOfFrame();
                Shots.Capture("pause-menu-options");
                yield return new WaitForSecondsRealtime(1.5f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("pause-menu-options-2s");
                lines.Add("options tabs: " + OptionsTabs());
                Options.SaveAndClose();
            }
            IngameMenuUi.Hide();
            Results.Add("Pause menu: " + string.Join("; ", lines));
            if (bad > 0)
            {
                Problem($"[pause menu] {bad} button(s) the laser cannot reach: {string.Join("; ", lines)}");
            }
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// <summary>
        /// The options' tab pages (nested canvases): drawn or not, sorting, alpha, size. In the headset the panel came up
        /// with its tab buttons and Apply but an empty page.
        /// </summary>
        private static string OptionsTabs()
        {
            Options o = Options.instance;
            if (o == null)
            {
                return "no options";
            }
            var parts = new List<string>();
            foreach (Canvas tab in new[] { o.gameOptions, o.inputOptions, o.videoOptions })
            {
                if (tab == null)
                {
                    parts.Add("-");
                    continue;
                }
                int drawn = 0, total = 0;
                foreach (UnityEngine.UI.Graphic g in tab.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
                {
                    total++;
                    if (g.enabled && g.canvasRenderer != null && !g.canvasRenderer.cull && g.canvasRenderer.GetInheritedAlpha() > 0.01f)
                    {
                        drawn++;
                    }
                }
                string groups = "";
                for (Transform t = tab.transform; t != null; t = t.parent)
                {
                    CanvasGroup cg = t.GetComponent<CanvasGroup>();
                    if (cg != null)
                    {
                        groups += $" group '{t.name}' alpha {cg.alpha:0.00}";
                    }
                }
                Rect r = ((RectTransform)tab.transform).rect;
                parts.Add($"{tab.name}: {(tab.enabled ? "on" : "off")}, {(tab.overrideSorting ? $"own order {tab.sortingOrder}" : "root's order")} (root {tab.rootCanvas.sortingOrder}), "
                          + $"{drawn}/{total} graphics drawn, rect {r.width:0}x{r.height:0} at {tab.transform.localPosition}, layer {tab.gameObject.layer}{groups}");
            }
            if (o.background != null)
            {
                parts.Add($"background {(o.background.enabled ? "on" : "off")} {o.background.color} texture {(o.background.texture != null ? o.background.texture.name : "none")}, sibling {o.background.transform.GetSiblingIndex()}");
            }
            // The Game page in detail: masks, scale, where its graphics are drawn against the panel, their shaders.
            if (o.gameOptions != null)
            {
                Canvas tab = o.gameOptions;
                Transform panel = tab.rootCanvas.transform;
                var masks = tab.GetComponentsInChildren<UnityEngine.UI.Mask>(true);
                var rects = tab.GetComponentsInChildren<UnityEngine.UI.RectMask2D>(true);
                int enabledMasks = 0;
                foreach (var m in masks)
                {
                    enabledMasks += m.enabled ? 1 : 0;
                }
                string detail = $"Game page: scale {tab.transform.localScale}, sibling {tab.transform.GetSiblingIndex()}, masks {masks.Length} ({enabledMasks} on), rect clips {rects.Length}";
                int shown = 0;
                foreach (UnityEngine.UI.Graphic g in tab.GetComponentsInChildren<UnityEngine.UI.Graphic>(false))
                {
                    if (shown++ >= 4)
                    {
                        break;
                    }
                    Vector3 local = panel.InverseTransformPoint(g.transform.position);
                    detail += $"; '{g.name}' at ({local.x:0},{local.y:0},{local.z:0}) shader {(g.materialForRendering != null ? g.materialForRendering.shader.name : "none")} alpha {g.canvasRenderer.GetInheritedAlpha():0.00} stencil {(g.materialForRendering != null && g.materialForRendering.HasProperty("_Stencil") ? g.materialForRendering.GetFloat("_Stencil") : -1f):0}";
                }
                parts.Add(detail);
            }
            return string.Join("; ", parts);
        }

        /// <summary>Which of the game clips the mod's sounds are cut from are loaded (one not loaded stays silent, GameSounds).</summary>
        private static void GameSoundsCheck()
        {
            var missing = new List<string>();
            int loaded = RavenfieldVR.Weapons.GameSounds.CountLoaded(missing);
            Results.Add($"Game sounds: {loaded} cuts loaded" + (missing.Count > 0 ? "; NOT LOADED: " + string.Join(", ", missing.ToArray()) : ""));
        }

        /// <summary>
        /// The VR settings panel's tabs, by their own buttons pressed with the laser: Weapons and Display show their pages and Comfort
        /// the first again, each pictured, and there are as many as ShowAdvanced says (2026-11-04).
        /// </summary>
        private static IEnumerator SettingsPagesCheck()
        {
            UI.VRSettingsPanel panel = UI.UISystem.Instance != null ? UI.UISystem.Instance.Settings : null;
            Rig.VRHand dom = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Dominant : null;
            if (panel == null || dom == null)
            {
                Results.Add("VR settings pages: skipped (no settings panel)");
                yield break;
            }
            var lines = new List<string>();
            panel.Open();
            try
            {
                yield return new WaitForSecondsRealtime(0.5f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("vr-settings-1");
                yield return ClickMenuButton(dom, "Weapons", lines, panel.Canvas.transform);
                yield return new WaitForSecondsRealtime(0.3f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("vr-settings-2");
                bool weapons = panel.ShownPage == 1;
                yield return ClickMenuButton(dom, "Display", lines, panel.Canvas.transform);
                yield return new WaitForSecondsRealtime(0.3f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("vr-settings-3");
                bool display = panel.ShownPage == 2;
                yield return ClickMenuButton(dom, "Comfort", lines, panel.Canvas.transform);
                yield return new WaitForSecondsRealtime(0.3f);
                bool comfort = panel.ShownPage == 0;
                bool tabs = panel.PageCount == (VRConfig.ShowAdvanced.Value ? 7 : 3);
                bool all = weapons && display && comfort;
                Results.Add($"VR settings pages: {panel.PageCount} tabs{(tabs ? "" : ", NOT AS MANY AS SHOWADVANCED SAYS")}, "
                            + $"{(all ? "each shows its page" : "A TAB DID NOT SHOW ITS PAGE")}{(lines.Count > 0 ? "; " + string.Join("; ", lines.ToArray()) : "")}");
                if (!all || !tabs)
                {
                    Problem("[ui] the VR settings panel's tabs do not switch");
                }
            }
            finally
            {
                panel.Close();
            }
        }

        /// <summary>Aims the laser at the button with this label (on the pause menu, or under <paramref name="root"/>) and clicks it with the trigger, wobbling 1.5 degrees.</summary>
        private static IEnumerator ClickMenuButton(Rig.VRHand dom, string label, List<string> lines, Transform root = null)
        {
            int side = (int)dom.Side;
            UnityEngine.UI.Button button = null;
            foreach (UnityEngine.UI.Button b in (root != null ? root : IngameMenuUi.instance.transform).GetComponentsInChildren<UnityEngine.UI.Button>(false))
            {
                UnityEngine.UI.Text t = b.GetComponentInChildren<UnityEngine.UI.Text>();
                if (t != null && t.text.Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
                {
                    button = b;
                }
            }
            if (button == null)
            {
                lines.Add($"click '{label}': NO SUCH BUTTON");
                yield break;
            }
            Vector3 target = ((RectTransform)button.transform).position;
            try
            {
                for (int k = 0; k < 14; k++)
                {
                    float wobble = k >= 9 && k <= 11 ? 1.5f : 0f; // the controller dips while the trigger is pulled
                    Runtime.RigidPose raw = dom.Raw;
                    Vector3 want = Quaternion.AngleAxis(wobble, Vector3.right) * (target - dom.Pointer.position).normalized;
                    Quaternion turn = Quaternion.FromToRotation(dom.Pointer.Forward, want);
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(dom.Pointer.position + turn * (raw.position - dom.Pointer.position), turn * raw.rotation);
                    Input.VRInput.SimulatedTrigger[side] = k >= 8 && k <= 10 ? 1f : 0f;
                    yield return null;
                }
            }
            finally
            {
                Input.VRInput.SimulatedTrigger[side] = 0f;
                Rig.VRHand.Scripted[side] = null;
            }
            yield return null;
        }

        /// <summary>
        /// The loadout's weapon list (the submenu with every weapon for a slot): pictures over a second and a half after it
        /// opens, and where its panel is (it once turned 3D, then jumped flat to the left with a grey box where the list was).
        /// </summary>
        private static IEnumerator LoadoutPickerCheck()
        {
            if (LoadoutUi.instance == null)
            {
                Results.Add("Loadout weapon list: skipped (no loadout screen)");
                yield break;
            }
            LoadoutUi.Show(false);
            yield return new WaitForSecondsRealtime(0.8f);
            yield return new WaitForEndOfFrame();
            Shots.Capture("loadout-0-open");
            LoadoutUi.instance.OpenWeaponSelector(0);
            var where = new List<string>();
            foreach (float t in new[] { 0.1f, 0.5f, 1.5f })
            {
                yield return new WaitForSecondsRealtime(t);
                yield return new WaitForEndOfFrame();
                Shots.Capture($"loadout-weapon-list-{t:0.0}s");
                WeaponSelectionUi list = LoadoutUi.instance.primaryWeaponSelection;
                Canvas c = list != null ? list.GetComponentInParent<Canvas>() : null;
                Canvas root = c != null ? c.rootCanvas : null;
                where.Add(root == null ? "no canvas"
                    : $"{t:0.0}s: '{root.name}' {root.renderMode}, {(root.enabled ? "on" : "OFF")}, list {(list.gameObject.activeInHierarchy ? "shown" : "HIDDEN")} at {root.transform.position}");
            }
            LoadoutUi.instance.CancelWeaponSelection();
            LoadoutUi.Hide();
            Results.Add("Loadout weapon list: " + string.Join("; ", where));
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// <summary>
        /// Squad orders by pointing: the squad order screen up, the off hand held out straight (it must count as pointing,
        /// the order written on it), then at the chest (it must not; "point with a hand" on it). A picture of each.
        /// </summary>
        private static IEnumerator SquadPointingCheck()
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig == null || SquadPointUi.instance == null)
            {
                Results.Add("Squad pointing: skipped (no squad order screen)");
                yield break;
            }
            Rig.VRHand off = rig.Off;
            int side = (int)off.Side;
            Runtime.RigidPose raw = off.Raw;
            Vector3 palmFromRaw = raw.InverseTransformPoint(off.PalmCentre);
            Quaternion pointerInRaw = Quaternion.Inverse(raw.rotation) * off.Pointer.rotation;
            Transform head = rig.Head;
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Runtime.RigidPose HandAt(Vector3 palm, Vector3 pointing)
            {
                Quaternion rawRot = Quaternion.LookRotation(pointing, Vector3.up) * Quaternion.Inverse(pointerInRaw);
                return new Runtime.RigidPose(palm - rawRot * palmFromRaw, rawRot);
            }
            SquadPointUi.Show(SquadPointUi.SquadPointOrderBaseType.PointOrder);
            string straight, chest;
            try
            {
                Vector3 outThere = head.position + (flat * 0.9f + Vector3.down * 0.25f).normalized * 0.58f;
                Rig.VRHand.Scripted[side] = HandAt(outThere, (outThere - head.position).normalized);
                for (int i = 0; i < 20; i++)
                {
                    yield return null;
                }
                UI.SquadPointing.Compute();
                bool pointing = UI.SquadPointing.Pointing;
                yield return new WaitForSecondsRealtime(0.3f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("squad-pointing-straight");
                straight = pointing ? "counts as pointing" : "DOES NOT COUNT AS POINTING";

                Vector3 atChest = head.position + flat * 0.22f + Vector3.down * 0.35f;
                Rig.VRHand.Scripted[side] = HandAt(atChest, flat);
                for (int i = 0; i < 20; i++)
                {
                    yield return null;
                }
                UI.SquadPointing.Compute();
                bool still = UI.SquadPointing.Pointing;
                yield return new WaitForSecondsRealtime(0.3f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("squad-pointing-at-chest");
                chest = still ? "STILL COUNTS AS POINTING" : "does not count";
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
                SquadPointUi.Hide(false);
            }
            Results.Add($"Squad pointing: hand held out straight {straight}; hand at the chest {chest}");
            if (straight.StartsWith("DOES") || chest.StartsWith("STILL"))
            {
                Problem($"[squad] pointing: held out straight {straight}, at the chest {chest}");
            }
            yield return new WaitForSecondsRealtime(0.3f);
        }

        /// <summary>
        /// Aimed, the hand shaken as a tremor does (0.3 deg at 9 Hz about the head's right axis, from the aimed pose), and how much
        /// of it reaches the barrel: the steadying (ScopeSteadying through a magnifier, AimSteadying on iron sights) is measured
        /// where it acts (bug hunt 2026-10-19: no run had shaken an aimed gun; SteadyCheck never aims and only adds velocity
        /// noise). With the setting on, more coming through than halfway between a working filter's share and all of it is a problem.
        /// </summary>
        private static IEnumerator AimedTremor(HeldWeapon held, string name, int side, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            Runtime.RigidPose aimed = Rig.VRHand.Scripted[side].Value;
            Vector3 axis = rig.Head.right;
            Vector3 up = rig.Head.up;
            const float amplitude = 0.3f, hz = 9f;
            float magnified = 60f / Mathf.Max(1f, held.Weapon.GetAimFov());
            float setting = magnified > 1.8f ? VRConfig.ScopeSteadying.Value : VRConfig.AimSteadying.Value;
            var pitch = new List<float>();
            float t0 = Time.realtimeSinceStartup;
            bool stillAiming = true;
            while (Time.realtimeSinceStartup - t0 < 2f)
            {
                float t = Time.realtimeSinceStartup - t0;
                Rig.VRHand.Scripted[side] = new Runtime.RigidPose(aimed.position, Quaternion.AngleAxis(amplitude * Mathf.Sin(2f * Mathf.PI * hz * t), axis) * aimed.rotation);
                yield return new WaitForEndOfFrame();
                if (held.Rig.GunBody == null)
                {
                    yield break;
                }
                stillAiming &= held.Aiming;
                if (t > 0.5f)
                {
                    // The barrel's pitch about the same axis (up the head's up, in degrees).
                    Vector3 aim = held.Rig.AimWorld;
                    pitch.Add(Mathf.Asin(Mathf.Clamp(Vector3.Dot(aim, up), -1f, 1f)) * Mathf.Rad2Deg);
                }
            }
            Rig.VRHand.Scripted[side] = aimed;
            if (pitch.Count < 10)
            {
                yield break;
            }
            float through = (pitch.Max() - pitch.Min()) / 2f / amplitude * 100f;
            string which = magnified > 1.8f ? $"scope {magnified:0.0}x, ScopeSteadying {setting * 100f:0}%" : $"{magnified:0.0}x sight, AimSteadying {setting * 100f:0}%";
            Metric("aimed tremor reaching the barrel, %", through);
            report($"aimed tremor (0.3 deg at 9 Hz): {through:0}% reaches the barrel ({which}{(stillAiming ? "" : "; the aim was lost meanwhile")})");
            // The line between what a working filter lets through (100 - 100 * setting) and what none does (100): at 25% a working one
            // gives 75-77% and failed the fixed 75 (round-7 scout).
            if (setting > 0f && stillAiming && through > 100f - 50f * setting)
            {
                Problem($"[{name}] aimed, a hand tremor is not steadied: {through:0}% of it reaches the barrel ({which})");
            }
        }

        /// <summary>
        /// Deep run: the gun raised until its sight line lies before the right eye, a picture through it for each sight mode
        /// (a magnifier flipped in or out), and what the mod and the game make of it: aiming or not, and the weapon's own
        /// scope camera (on or off, what it draws into).
        /// </summary>
        private static IEnumerator SightCheck(HeldWeapon held, string name, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig == null || held == null || !held.Rig.Data.HasEyePoint || held.Rig.GunBody == null)
            {
                yield break;
            }
            Rig.VRHand dom = rig.Dominant;
            int side = (int)dom.Side;
            Weapon w = held.Weapon;
            int start = w.GetSightModeIndex();
            try
            {
                for (int m = 0; m < 4; m++)
                {
                    Weapons.SightLine sight = held.CurrentSight;
                    Vector3 eyeGun = sight != null ? sight.EyePoint : held.Rig.Data.EyePoint;
                    Vector3 dirGun = sight != null ? sight.EyeDirection : held.Rig.Data.EyeDirection;
                    for (int k = 0; k < 30; k++)
                    {
                        // Turned about the sight's eye point onto the eye's line of sight, then moved so the eye point is 7 cm
                        // in front of the right eye (the gun follows the hand with a little lag: corrected every frame).
                        Transform head = rig.Head;
                        Vector3 eye = head.position + head.right * 0.032f;
                        Runtime.RigidPose raw = dom.Raw;
                        Vector3 eyeW = held.Rig.ToWorld(eyeGun);
                        Quaternion fix = Quaternion.FromToRotation(held.Rig.DirToWorld(dirGun), head.forward);
                        Vector3 target = eye + head.forward * 0.07f;
                        Rig.VRHand.Scripted[side] = new Runtime.RigidPose(target + fix * (raw.position - eyeW), fix * raw.rotation);
                        // A steadied gun (a launcher always is) eases toward the hand: corrected every frame, the next correction
                        // chased a gun still on its way and overshot, and the RPG-7 never came onto the eye's line (NOT AIMING in
                        // every run). The last ones wait for the gun to settle first.
                        if (k >= 24)
                        {
                            yield return new WaitForSecondsRealtime(0.15f);
                        }
                        yield return null;
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                    yield return new WaitForEndOfFrame();
                    Transform h = rig.Head;
                    Shots.Capture($"{name}-mode{w.GetSightModeIndex()}", h.position + h.right * 0.032f, h.rotation, 50f);
                    Camera rt = w.renderTextureCamera;
                    string cam = rt == null ? "no scope camera of its own"
                        : $"its scope camera '{rt.name}' {(rt.enabled && rt.gameObject.activeInHierarchy ? "on" : "OFF")}, drawing into {(rt.targetTexture != null ? $"'{rt.targetTexture.name}'" : "THE SCREEN")}";
                    string miss = held.AimMiss < 1f ? $"the eye {held.AimMiss * 100f:0.0} cm off its line, {held.AimAngle:0} deg" : "no eye within its reach";
                    report($"sight mode {w.GetSightModeIndex()}: {(held.Aiming ? "aiming" : "NOT AIMING (the eye is not on its line)")} ({miss}), game aiming {w.GetActiveSubWeapon().aiming}, {cam}, {held.ScopeLensState()}");
                    if (m == 0 && held.Aiming && Rig.VRHand.Scripted[side].HasValue)
                    {
                        yield return AimedTremor(held, name, side, report);
                    }
                    if (!w.HasAnyAltSightModes() || !w.NextSightMode())
                    {
                        break;
                    }
                    yield return new WaitForSecondsRealtime(0.4f);
                    if (w.GetSightModeIndex() == start)
                    {
                        break;
                    }
                }
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
            }
            for (int k = 0; k < 6 && w.GetSightModeIndex() != start && w.NextSightMode(); k++)
            {
            }
            yield return new WaitForSecondsRealtime(0.3f);
        }

        /// <summary>
        /// Deep run, once: the cost of a scope's lens (255.P1.2). Aimed through the first gun with a lens of the mod's (its eye
        /// point 7 cm before the right eye, as the sight check holds it), 1 s to settle and 4 s measured with the lens as made,
        /// its camera off, rendered every other frame, and without shadows: frame and GPU time each.
        /// </summary>
        private static IEnumerator ScopeBench(HeldWeapon held, string name, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig == null || held == null || !held.HasLensScope || !held.Rig.Data.HasEyePoint || held.Rig.GunBody == null)
            {
                yield break;
            }
            Rig.VRHand dom = rig.Dominant;
            int side = (int)dom.Side;
            string[] modes = { "lens as set", "lens camera off", "lens every other frame", "lens with shadows" };
            var line = new System.Text.StringBuilder($"scope bench on {name}:");
            try
            {
                for (int mode = 0; mode < modes.Length; mode++)
                {
                    Weapons.VRScope.BenchMode = mode;
                    float settleUntil = Time.realtimeSinceStartup + 1f, until = settleUntil + 4f;
                    bool measuring = false;
                    while (Time.realtimeSinceStartup < until)
                    {
                        if (!measuring && Time.realtimeSinceStartup >= settleUntil)
                        {
                            measuring = true;
                            PerfProbe.Reset();
                        }
                        Weapons.SightLine sight = held.CurrentSight;
                        Vector3 eyeGun = sight != null ? sight.EyePoint : held.Rig.Data.EyePoint;
                        Vector3 dirGun = sight != null ? sight.EyeDirection : held.Rig.Data.EyeDirection;
                        Transform head = rig.Head;
                        Vector3 eye = head.position + head.right * 0.032f;
                        Runtime.RigidPose raw = dom.Raw;
                        Vector3 eyeW = held.Rig.ToWorld(eyeGun);
                        Quaternion fix = Quaternion.FromToRotation(held.Rig.DirToWorld(dirGun), head.forward);
                        Rig.VRHand.Scripted[side] = new Runtime.RigidPose(eye + head.forward * 0.07f + fix * (raw.position - eyeW), fix * raw.rotation);
                        yield return null;
                    }
                    string summary = PerfProbe.Summary(out float cpu, out float gpu);
                    Metric($"scope bench {modes[mode]}: frame ms", cpu);
                    Metric($"scope bench {modes[mode]}: GPU ms", gpu);
                    line.Append($" {modes[mode]} {summary}{(mode == 0 ? $" ({(held.Aiming ? "aiming" : "NOT AIMING")})" : "")};");
                }
            }
            finally
            {
                Weapons.VRScope.BenchMode = -1; // (the config's again)
                Rig.VRHand.Scripted[side] = null;
            }
            report(line.ToString());
            Results.Add("  " + line);
            yield return new WaitForSecondsRealtime(0.3f);
        }

        /// <summary>
        /// The same weapon held left-handed (the setting overridden in memory only): holstered, handedness switched,
        /// drawn into the left hand, pictured and measured, then back.
        /// </summary>
        private static IEnumerator LeftHandCheck(Weapon weapon, string name, Action<string> report)
        {
            Actor actor = LocalPlayer.actor;
            int slot = actor != null && actor.weapons != null ? Array.IndexOf(actor.weapons, weapon) : -1;
            if (slot < 0 || VRConfig.LeftHanded)
            {
                yield break;
            }
            try
            {
                actor.HolsterActiveWeapon();
                yield return new WaitForSecondsRealtime(0.4f);
                VRConfig.TestLeftHanded = true;
                actor.SwitchWeapon(slot);
                float until = Time.realtimeSinceStartup + 4f;
                HeldWeapon held = null;
                while (Time.realtimeSinceStartup < until)
                {
                    held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                    if (held != null && held.Weapon == weapon && held.Volume != null && held.Volume.Ready)
                    {
                        break;
                    }
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.5f);
                if (held == null || held.Weapon != weapon || Rig.VRRig.Instance == null)
                {
                    report("left-handed: the weapon did NOT COME INTO THE LEFT HAND");
                    yield break;
                }
                Rig.VRHand gunHand = Rig.VRRig.Instance.Dominant;
                yield return new WaitForEndOfFrame();
                Shots.Capture(name);
                GripCloseups(held, name, _ => { });
                string clip = held.HandClipping(gunHand, out int buried, out _, out int touching);
                Metric("left-handed gun hand: points inside the gun", buried);
                Metric("left-handed gun hand: fingers on the gun (of 4)", touching);
                report($"left-handed: held in the {gunHand.Side} hand, {clip}");
                _clipPending = null;
            }
            finally
            {
                if (VRConfig.TestLeftHanded.HasValue)
                {
                    actor.HolsterActiveWeapon();
                    VRConfig.TestLeftHanded = null;
                    actor.SwitchWeapon(slot);
                }
            }
            yield return new WaitForSecondsRealtime(1.5f); // drawn again into the right hand
        }

        /// <summary>
        /// The weapon's own arms instead of the VR hands (the setting overridden in memory only): holstered, drawn again with
        /// the arms, pictured; the gun arm must hold the authored grip and the off arm reach the handguard and then the
        /// controller without tearing (each hand bone on its target).
        /// </summary>
        private static IEnumerator WeaponArmsCheck(Weapon weapon, string name, Action<string> report)
        {
            Actor actor = LocalPlayer.actor;
            int slot = actor != null && actor.weapons != null ? Array.IndexOf(actor.weapons, weapon) : -1;
            if (slot < 0)
            {
                yield break; // (run with Weapon's arms already chosen too: it left no line and checked nothing then; review)
            }
            try
            {
                actor.HolsterActiveWeapon();
                yield return new WaitForSecondsRealtime(0.4f);
                VRConfig.TestHands = HandsMode.WeaponArms;
                actor.SwitchWeapon(slot);
                HeldWeapon held = null;
                for (float until = Time.realtimeSinceStartup + 4f; Time.realtimeSinceStartup < until; )
                {
                    held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                    if (held != null && held.Weapon == weapon)
                    {
                        break;
                    }
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.6f);
                ArmIK arms = held != null && held.Weapon == weapon ? held.Arms : null;
                if (arms == null || !arms.Active)
                {
                    report("weapon's arms: NOT SHOWN (no arm chain found for this weapon)");
                    yield break;
                }
                Rig.VRHand off = Rig.VRRig.Instance.Off;
                Input.VRInput.SimulatedGrab[(int)off.Side] = true;
                off.Role = Rig.HandRole.Foregrip;
                held.SetTwoHanded(true);
                yield return new WaitForSecondsRealtime(0.4f);
                yield return new WaitForEndOfFrame();
                Shots.Capture(name + "-on-the-handguard");
                float gunErr = arms.GunHandError, gripErr = arms.OffHandError;
                Input.VRInput.SimulatedGrab[(int)off.Side] = false;
                yield return null;
                yield return new WaitForSecondsRealtime(0.4f);
                yield return new WaitForEndOfFrame();
                Shots.Capture(name + "-free-hand");
                float freeErr = arms.OffHandError;
                Metric("weapon's arms: cm the gun hand is off its grip", gunErr * 100f);
                Metric("weapon's arms: cm the off hand is off its target", Mathf.Max(gripErr, freeErr) * 100f);
                bool torn = gunErr > 0.02f || gripErr > 0.02f || freeErr > 0.02f;
                // A magazine of this gun in the off hand: the hand on it as the gun's reload holds it (MagHoldPose; user, 2026-10-29).
                string magNote = "";
                MagHoldPose hold = held.Rig.Data.MagHold;
                if (held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag)
                {
                    if (hold == null)
                    {
                        magNote = "; a magazine in the off hand: no hold pose in its reload (the hand goes where the controller is)";
                    }
                    else
                    {
                        held.SetTwoHanded(false); // (set for the handguard step above)
                        MagazineProp mag = MagazineProp.Create(held.ActiveRig, PropKind.Magazine, 0);
                        try
                        {
                            mag.Attach(off);
                            yield return new WaitForSecondsRealtime(0.4f);
                            yield return new WaitForEndOfFrame();
                            Shots.Capture(name + "-holding-a-magazine");
                            float magErr = arms.OffHandError;
                            Metric("weapon's arms: cm the off hand is off its magazine hold", magErr * 100f);
                            bool offMag = !arms.OnMagazine || magErr > 0.02f;
                            torn |= offMag;
                            magNote = $"; holding a magazine: {magErr * 100f:0.0} cm off the hold its reload has (from {hold.Step * PoseSampler.Dt:0.00} s, "
                                      + $"{hold.FingerPaths.Length} finger bones){(offMag ? " (NOT ON THE MAGAZINE)" : "")}";
                        }
                        finally
                        {
                            mag?.Destroy();
                            off.Held = null;
                            off.Role = Rig.HandRole.Free;
                        }
                    }
                }
                // Off the gun the weapon's hand moves its fingers with the controller (ArmFingers; user, 2026-10-29: its handguard grip held
                // in the air): squeezed, they close. Not two-handed for it (the handguard step set it: the hand counted as on the gun).
                held.SetTwoHanded(false);
                yield return new WaitForSecondsRealtime(0.3f);
                // (The controller's own finger tracking, when it has it, wins over the squeeze simulated here: not a fault then.)
                bool tracking = Input.VRInput.HasSkeleton(off.Side);
                float open = arms.OffFingerBend;
                Input.VRInput.SimulatedGrip[(int)off.Side] = 1f;
                Input.VRInput.SimulatedTrigger[(int)off.Side] = 1f;
                string fingerNote;
                try
                {
                    yield return new WaitForSecondsRealtime(0.3f);
                    yield return new WaitForEndOfFrame();
                    Shots.Capture(name + "-free-hand-fist");
                    float fist = arms.OffFingerBend;
                    if (open < 0f || fist < 0f)
                    {
                        fingerNote = "; the weapon's left hand has no fingers found by name (left as its animation has them)";
                    }
                    else if (tracking)
                    {
                        fingerNote = $"; the free hand's fingers {open:0} deg open, {fist:0} squeezed (the controller's finger tracking: the squeeze not simulated)";
                    }
                    else
                    {
                        Metric("weapon's arms: degrees the free hand's fingers close with the grip", fist - open);
                        bool closing = fist - open >= 15f;
                        torn |= !closing;
                        fingerNote = $"; the free hand's fingers {open:0} deg open, {fist:0} squeezed{(closing ? "" : " (FINGERS NOT CLOSING)")}";
                    }
                }
                finally
                {
                    Input.VRInput.SimulatedGrip[(int)off.Side] = 0f;
                    Input.VRInput.SimulatedTrigger[(int)off.Side] = 0f;
                }
                // Put away: the arms stay on the empty hands (EmptyArms; user, 2026-10-29: "i wanna have the arms the whole time").
                actor.HolsterActiveWeapon();
                yield return new WaitForSecondsRealtime(0.6f);
                yield return new WaitForEndOfFrame();
                Shots.Capture(name + "-empty-hands");
                EmptyArms empty = WeaponSystem.Instance != null ? WeaponSystem.Instance.EmptyHandArms : null;
                string emptyNote;
                if (empty == null || !empty.Shown)
                {
                    emptyNote = "; put away: THE EMPTY-HAND ARMS NOT SHOWN";
                    torn = true;
                }
                else
                {
                    bool gloves = true;
                    foreach (Rig.VRHand h in Rig.VRRig.Instance.Hands)
                    {
                        gloves &= h.Model == null || h.Model.Suppressed;
                    }
                    // (The hand bone against its IK target: the reach; where the target sits on the controller is the picture's to show.)
                    bool solved = empty.HandError >= 0f;
                    if (solved)
                    {
                        Metric("weapon's arms: cm the empty-hand arms' hands are off their targets", empty.HandError * 100f);
                    }
                    bool off2 = empty.HandError > 0.02f;
                    torn |= off2 || !gloves;
                    emptyNote = $"; put away, the arms stay on the empty hands, "
                                + (solved ? $"{empty.HandError * 100f:0.0} cm off their targets on the controllers" : "not solved (a hand with no pose)")
                                + $"{(off2 ? " (NOT ON THE CONTROLLERS)" : "")}{(gloves ? "" : " (GLOVES NOT HIDDEN)")}";
                    // With no copy (spawned empty-handed, respawned): taken from a carried weapon within its 2 s retry.
                    empty.Destroy();
                    yield return new WaitForSecondsRealtime(2.6f);
                    bool again = empty.Shown;
                    torn |= !again;
                    emptyNote += again ? "; with no copy, taken again from a carried weapon" : "; with no copy: THE ARMS NOT TAKEN FROM A CARRIED WEAPON";
                }
                report($"weapon's arms: gun hand {gunErr * 100f:0.0} cm off its grip, off hand {gripErr * 100f:0.0} cm off the handguard and {freeErr * 100f:0.0} cm off the controller"
                       + $"{(arms.Stretched ? " (a shoulder slid to reach)" : "")}{magNote}{fingerNote}{emptyNote}{(torn ? " (NOT ON TARGET)" : "")}");
            }
            finally
            {
                if (Rig.VRRig.Instance != null)
                {
                    Input.VRInput.SimulatedGrab[(int)Rig.VRRig.Instance.Off.Side] = false; // (a step that stopped between its set and clear)
                }
                if (VRConfig.TestHands.HasValue)
                {
                    actor.HolsterActiveWeapon();
                    VRConfig.TestHands = null;
                    actor.SwitchWeapon(slot);
                }
            }
            yield return new WaitForSecondsRealtime(1.5f); // drawn again in the chosen Hands mode
        }

        private static string _clipNow;
        private static HeldWeapon _clipPending;

        /// <summary>
        /// The gun hand measured against the model with hands closing on it (taken with the close-ups) and again posed the
        /// old way, by the weapon's hand pose alone, for comparison.
        /// </summary>
        private static IEnumerator ClipCompare(Action<string> report)
        {
            HeldWeapon held = _clipPending;
            _clipPending = null;
            if (held == null || Rig.VRRig.Instance == null)
            {
                yield break;
            }
            Rig.HandModel.ClosingDisabled = true;
            yield return new WaitForSecondsRealtime(0.4f);
            string before = held.HandClipping(Rig.VRRig.Instance.Dominant, out int oldBuried, out _, out int oldTouching);
            Metric("gun hand (old posing): points inside the gun", oldBuried);
            Metric("gun hand (old posing): fingers on the gun (of 4)", oldTouching);
            Rig.HandModel.ClosingDisabled = false;
            yield return new WaitForSecondsRealtime(0.3f);
            report($"gun hand: {_clipNow} (posed by the weapon's hand alone: {before})");
            // How fast the hands' solid answers (every finger and palm test is one query): this way and the earlier way.
            {
                GunVolume v = held.Volume;
                var rng = new System.Random(7);
                Vector3 c = held.GripWorld.position;
                var pts = new Vector3[20000];
                for (int i = 0; i < pts.Length; i++)
                {
                    pts[i] = c + new Vector3((float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f, (float)rng.NextDouble() - 0.5f) * 0.3f;
                }
                double Time(bool legacy)
                {
                    GunVolume.LegacyQueries = legacy;
                    int solid = 0;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    for (int r = 0; r < 3; r++)
                    {
                        foreach (Vector3 p in pts)
                        {
                            solid += v.Solid(p) ? 1 : 0;
                        }
                    }
                    GunVolume.LegacyQueries = false;
                    return watch.Elapsed.TotalMilliseconds * 1e6 / (3.0 * pts.Length) + solid * 0.0; // ns per query
                }
                Time(false); // warm up the JIT
                double now = Time(false), earlier = Time(true);
                int agree = 0;
                foreach (Vector3 p in pts)
                {
                    GunVolume.LegacyQueries = true;
                    bool a = v.Solid(p);
                    GunVolume.LegacyQueries = false;
                    agree += a == v.Solid(p) ? 1 : 0;
                }
                Metric("hand solid query: ns", now);
                Metric("hand solid query (earlier way): ns", earlier);
                Metric("hand solid query: % same answers as the earlier way", 100.0 * agree / pts.Length);
            }
            // The trigger finger pulled all the way: it must curl onto the trigger, not into the frame.
            Rig.VRHand gunHand = Rig.VRRig.Instance.Dominant;
            Input.VRInput.SimulatedTrigger[(int)gunHand.Side] = 1f;
            yield return new WaitForSecondsRealtime(0.35f);
            string pulled = held.HandClipping(gunHand, out int pulledBuried, out _, out _);
            Rig.HandModel.LegacyTriggerFinger = true;
            yield return new WaitForSecondsRealtime(0.35f);
            held.HandClipping(gunHand, out int legacyBuried, out _, out _);
            Rig.HandModel.LegacyTriggerFinger = false;
            Input.VRInput.SimulatedTrigger[(int)gunHand.Side] = 0f;
            Metric("gun hand, trigger pulled: points inside the gun", pulledBuried);
            Metric("gun hand, trigger pulled (earlier finger): points inside the gun", legacyBuried);
            report($"trigger pulled: {pulled} (the earlier trigger finger: {legacyBuried} points inside)");
            yield return new WaitForSecondsRealtime(0.2f);
            GunCollider shape = held.Collider;
            if (shape != null && held.Volume != null && held.Volume.Ready)
            {
                string now = shape.Match(held.Volume, out float excess, out float covered);
                string kind = shape.ShapeBefore != null ? "rendered model" : "readable meshes";
                if (shape.ShapeRejected != null)
                {
                    report($"physics shape: the rendered-model fit was not kept ({shape.ShapeRejected})");
                }
                Metric($"physics shape ({kind}): % outside the model", excess);
                Metric($"physics shape ({kind}): % of the model covered", covered);
                if (shape.ShapeBefore != null)
                {
                    Metric("physics shape report comparison: ms saved in play", shape.ReportMatchMs);
                    Metric("physics shape (replaced): % outside the model", shape.ShapeBeforeExcess);
                    Metric("physics shape (replaced): % of the model covered", shape.ShapeBeforeCovered);
                }
                report(shape.ShapeBefore != null
                    ? $"physics shape from the rendered model: {now} (before: {shape.ShapeBefore})"
                    : $"physics shape from the meshes: {now}");
            }
        }

        /// <summary>Close-ups of the gun hand on its grip: from the back of the hand, from the fingertips' side and from below.</summary>
        private static void GripCloseups(HeldWeapon held, string name, Action<string> report)
        {
            Runtime.RigidPose g = held.GripWorld;
            Vector3 fwd = g.rotation * Vector3.forward, up = g.rotation * Vector3.up, right = g.rotation * Vector3.right;
            Vector3 c = g.position + fwd * 0.03f - up * 0.02f;
            void Shot(string side, Vector3 from) => Shots.Capture($"{name}-{side}", from, Quaternion.LookRotation(c - from, up), 40f);
            Shot("out", c + right * 0.3f + up * 0.05f);
            Shot("in", c - right * 0.3f + up * 0.05f);
            Shot("under", c - up * 0.28f + fwd * 0.1f + right * 0.06f);
            if (Rig.VRRig.Instance != null)
            {
                report("hand: " + held.HandReport(Rig.VRRig.Instance.Dominant));
            }
            if (Rig.VRRig.Instance != null && held.Volume != null && held.Volume.Ready)
            {
                _clipNow = held.HandClipping(Rig.VRRig.Instance.Dominant, out int buried, out _, out int touching);
                _clipPending = held;
                Metric("gun hand: points inside the gun", buried);
                Worst.Add(new KeyValuePair<string, int>($"{held.Rig.Data.Key}: gun hand, {touching}/4 fingers on it", buried));
                Metric("gun hand: fingers on the gun (of 4)", touching);
                Metric(held.Rig.Data.IsPistol ? "gun hand: pistol thumb on the gun (0/1)" : "gun hand: rifle thumb on the grip (0/1)", Rig.VRRig.Instance.Dominant.Model.ThumbTouching ? 1 : 0);
                Metric("hand solid: main-thread ms per weapon", held.Volume.RenderMs);
                Metric("hand solid: longest main-thread frame ms", held.Volume.RenderFrameMs); // one view a frame (255.P1.1)
                Metric("hand solid: total ms per weapon", held.Volume.BuildMs);
                Metric("physics shape choice: main-thread ms per weapon type", held.ShapeChoiceMs);
            }
            // The weapon's own arms where its author animated them, for comparison.
            Renderer[] arms = held.Rig.ArmRenderers;
            if (arms.Length > 0)
            {
                bool[] was = arms.Select(r => r != null && r.enabled).ToArray();
                foreach (Renderer r in arms)
                {
                    if (r != null)
                    {
                        r.enabled = true;
                    }
                }
                Shot("modarms-out", c + right * 0.3f + up * 0.05f);
                Shot("modarms-in", c - right * 0.3f + up * 0.05f);
                for (int i = 0; i < arms.Length; i++)
                {
                    if (arms[i] != null)
                    {
                        arms[i].enabled = was[i];
                    }
                }
            }
        }

        /// <summary>
        /// Forgiving grab: the off hand squeezes, and a tenth of a second later a dropped weapon is within reach. It must be
        /// taken (into its slot) as a reaching hand would take it.
        /// </summary>
        private static IEnumerator ReachCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Actor actor = LocalPlayer.actor;
            Rig.VRHand off = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Off : null;
            if (sys == null || actor == null || off == null || off.Role != Rig.HandRole.Free)
            {
                report($"reaching grab: skipped (off hand {(off != null ? off.Role.ToString() : "missing")})");
                yield break;
            }
            WeaponManager.WeaponEntry entry = WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null && e.slot == WeaponManager.WeaponSlot.Secondary);
            if (entry == null)
            {
                report("reaching grab: skipped (no secondary weapon installed)");
                yield break;
            }
            Input.VRInput.SimulateGrabPress(off.Side);
            yield return new WaitForSecondsRealtime(0.1f);
            DroppedWeapon drop = sys.Drops.Spawn(entry, new Runtime.RigidPose(off.Palm.position + off.Palm.Forward * 0.04f, off.Palm.rotation), Vector3.one, Vector3.zero, -1, -1);
            if (drop != null && drop.Go != null)
            {
                drop.Go.GetComponent<Rigidbody>().isKinematic = true; // hold it in reach for the check
            }
            float until = Time.realtimeSinceStartup + 0.4f;
            while (drop != null && drop.Go != null && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
            Input.VRInput.SimulatedGrab[(int)off.Side] = false;
            bool taken = drop == null || drop.Go == null;
            report($"reaching grab: {(taken ? "a weapon arriving just after the squeeze was taken" : "NOT TAKEN (the squeeze came a moment early)")}");
            if (!taken)
            {
                UnityEngine.Object.Destroy(drop.Go);
            }
        }

        /// <summary>
        /// The gun against the world: a wall put in front of the player and the gun hand pushed 50 cm into it (the gun must
        /// stop at the wall, its muzzle outside), then the hand pushed down into the ground (the gun stops on it and the
        /// player must not be moved).
        /// </summary>
        private static IEnumerator WorldCollisionCheck(HeldWeapon held, Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            Actor actor = LocalPlayer.actor;
            if (rig == null || actor == null || held == null || held.Collider == null)
            {
                report("gun against the world: skipped (no physics stand-in)");
                yield break;
            }
            Rig.VRHand dom = rig.Dominant;
            int side = (int)dom.Side;
            Transform head = rig.Head;
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            GameObject wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "RavenfieldVR Test Wall";
            wall.transform.SetPositionAndRotation(head.position + flat * 0.75f - Vector3.up * 0.3f, Quaternion.LookRotation(flat));
            wall.transform.localScale = new Vector3(1.2f, 1.2f, 0.15f);
            Collider wallCollider = wall.GetComponent<Collider>();
            Runtime.RigidPose start = dom.Raw;
            Vector3 before = actor.Position();
            Vector3 afterWall = before, beforeGround = before;
            bool heldBack = false, muzzleIn = false;
            int knocksBefore = HeldWeapon.ContactKnocks, knocks = 0;
            try
            {
                for (int i = 0; i <= 30; i++)
                {
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start.position + flat * (0.5f * i / 30f), start.rotation);
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.3f);
                yield return new WaitForEndOfFrame();
                afterWall = actor.Position();
                heldBack = held.HeldBackByWorld;
                knocks = HeldWeapon.ContactKnocks - knocksBefore;
                Vector3 muzzle = held.Rig.ToWorld(held.Rig.Data.MuzzlePoint);
                muzzleIn = wallCollider.bounds.Contains(muzzle) && (wallCollider.ClosestPoint(muzzle) - muzzle).sqrMagnitude < 1e-6f;
                Shots.Capture("world-wall");
                Shots.Capture("world-wall-side", head.position + Vector3.Cross(Vector3.up, flat) * 0.9f, Quaternion.LookRotation(-Vector3.Cross(Vector3.up, flat)), 70f);
                // Down into the ground, the muzzle first.
                UnityEngine.Object.Destroy(wall);
                wall = null;
                yield return new WaitForSecondsRealtime(0.3f);
                beforeGround = actor.Position();
                Quaternion down = Quaternion.LookRotation(Vector3.down * 0.8f + flat * 0.2f, flat) * Quaternion.Inverse(Quaternion.LookRotation(dom.GunFrame.Forward, dom.GunFrame.Up)) * start.rotation;
                for (int i = 0; i <= 40; i++)
                {
                    float u = i / 40f;
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(start.position - Vector3.up * (0.9f * u), Quaternion.Slerp(start.rotation, down, u));
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.6f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("world-ground");
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
                if (wall != null)
                {
                    UnityEngine.Object.Destroy(wall);
                }
            }
            float moved = Vector3.ProjectOnPlane(actor.Position() - beforeGround, Vector3.up).magnitude;
            float movedByWall = Vector3.ProjectOnPlane(afterWall - before, Vector3.up).magnitude;
            Log.Info($"World collision check: player moved {movedByWall * 100f:0.0} cm during the wall push, {Vector3.ProjectOnPlane(beforeGround - afterWall, Vector3.up).magnitude * 100f:0.0} cm after it, {moved * 100f:0.0} cm during the ground push");
            Metric("gun pushed into a wall: muzzle inside it (0/1)", muzzleIn ? 1 : 0);
            Metric("gun pushed into the ground: cm the player moved", moved * 100f);
            report($"gun against the world: pushed into a wall it {(heldBack ? "stopped at the wall" : "was NOT HELD BACK")}{(muzzleIn ? " (MUZZLE INSIDE THE WALL)" : "")}"
                   + $"{(heldBack ? knocks > 0 ? $", a knock felt ({knocks})" : ", NO KNOCK felt" : "")}; "
                   + $"pushed into the ground the player moved {moved * 100f:0.0} cm{(moved > 0.05f ? " (NOT STILL)" : "")}");
            yield return new WaitForSecondsRealtime(0.5f);
        }

        /// <summary>Lets the held weapon fall as an opened hand would, pictures it, and waits for it to go back to its holster.</summary>
        private static IEnumerator DropCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Actor actor = LocalPlayer.actor;
            if (sys == null || actor == null)
            {
                yield break;
            }
            // Let go in a forward swing (2.5 m/s): it should fly on with the hand's speed, then lie still on the ground.
            Rig.VRHand dom = Rig.VRRig.Instance.Dominant;
            Vector3 swing = Vector3.ProjectOnPlane(Rig.VRRig.Instance.Head.forward, Vector3.up).normalized * 2.5f;
            Runtime.RigidPose start = dom.Raw;
            for (int i = 0; i < 6; i++)
            {
                Rig.VRHand.Scripted[(int)dom.Side] = new Runtime.RigidPose(start.position + swing * (0.011f * i), start.rotation);
                Rig.VRHand.ScriptedVelocity[(int)dom.Side] = swing;
                yield return null;
            }
            int slot = sys.TestLetFall();
            Rig.VRHand.Scripted[(int)dom.Side] = null;
            Rig.VRHand.ScriptedVelocity[(int)dom.Side] = Vector3.zero;
            if (slot < 0)
            {
                report("let go: NOTHING DROPPED");
                yield break;
            }
            DroppedWeapon thrown = sys.Drops.Newest;
            Rigidbody body = thrown != null && thrown.Go != null ? thrown.Go.GetComponent<Rigidbody>() : null;
            float speed = body != null ? body.velocity.magnitude : 0f;
            yield return new WaitForSecondsRealtime(0.7f);
            yield return new WaitForEndOfFrame();
            Shots.Capture("dropped-weapon");
            Transform head = Rig.VRRig.Instance.Head;
            Shots.Capture("dropped-weapon-looking-down", head.position, Quaternion.LookRotation(Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized * 0.6f + Vector3.down), 90f);
            bool gone = actor.weapons[slot] == null;
            string drift = "";
            if (thrown != null && thrown.Go != null)
            {
                yield return new WaitForSecondsRealtime(1.5f);
                drift = DriftLine("thrown weapon", thrown.Go, 4f);
                yield return new WaitForSecondsRealtime(4f);
                drift = drift.Replace("{to}", Fmt(thrown.Go));
            }
            float until = Time.realtimeSinceStartup + VRConfig.WeaponReturnSeconds.Value + 3f;
            while (actor.weapons[slot] == null && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
            report($"let go: {(gone ? "fell out of the hand" : "STILL IN THE LOADOUT")}, left the hand at {speed:0.0} m/s{(speed < 1.2f ? " (NOT THROWN with the swing)" : "")}, {(actor.weapons[slot] != null ? "back in its holster" : "NEVER CAME BACK")}");
            if (drift.Length > 0)
            {
                report(drift);
            }
        }

        private static readonly Dictionary<GameObject, Vector3> DriftFrom = new Dictionary<GameObject, Vector3>();

        /// <summary>Starts watching how far a resting object moves; completed by replacing {to} with <see cref="Fmt"/> later.</summary>
        private static string DriftLine(string what, GameObject go, float seconds)
        {
            DriftFrom[go] = go.transform.position;
            return $"{what} on the ground: {{to}} in {seconds:0} s";
        }

        private static string Fmt(GameObject go)
        {
            if (go == null || !DriftFrom.TryGetValue(go, out Vector3 from))
            {
                return "gone";
            }
            Vector3 d = go.transform.position - from;
            DriftFrom.Remove(go);
            Metric("dropped weapon on the ground: cm moved", d.magnitude * 100f);
            return d.magnitude > 0.15f ? $"MOVED {d.magnitude * 100f:0} cm (up {d.y * 100f:0} cm)" : $"still ({d.magnitude * 100f:0.0} cm)";
        }

        /// <summary>The first rifle (or else any gun) of the loadout drawn into the hand, for <see cref="GrabCheckOnly"/>.</summary>
        private static IEnumerator DrawFirstGun()
        {
            Actor actor = LocalPlayer.actor;
            if (actor == null || actor.weapons == null)
            {
                yield break;
            }
            int slot = -1;
            for (int i = 0; i < actor.weapons.Length && slot < 0; i++)
            {
                Weapon w = actor.weapons[i];
                if (w != null && !(w is MeleeWeapon) && !(w is ThrowableWeapon))
                {
                    slot = i;
                }
            }
            if (slot < 0)
            {
                Results.Add("grab check: no gun in the loadout to draw");
                yield break;
            }
            actor.SwitchWeapon(slot);
            for (float until = Time.realtimeSinceStartup + 6f; Time.realtimeSinceStartup < until; )
            {
                HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                if (held != null && held.Weapon == actor.weapons[slot])
                {
                    break;
                }
                yield return null;
            }
            yield return new WaitForSecondsRealtime(1f);
            Results.Add($"grab check: drew {(actor.activeWeapon != null ? actor.activeWeapon.name : "nothing")}");
        }

        /// <summary>
        /// The player put on open ground when a vehicle stands within 3 m of where the checks in front take place: rings of 5 to 25 m round
        /// him, the first ground with no vehicle within 4 m of its front and nothing in the way 3 m ahead. A note, or null when nothing was near.
        /// </summary>
        private static string MoveOffVehicles(Actor player, Rig.VRRig rig)
        {
            const int vehicles = 1 << 12;
            int level = ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2) & ~vehicles;
            Vector3 flat = Vector3.ProjectOnPlane(rig.Head.forward, Vector3.up).normalized;
            Vector3 side = Vector3.Cross(Vector3.up, flat);
            Vector3 chest = player.transform.position + Vector3.up * 1.1f;
            // (Walls too: on 2026-11-08 the GREASER's run stood with a building's wall along its left, the soldiers brought into it knocked over.)
            bool vehicleNear = Physics.OverlapSphere(rig.Head.position + flat * 1f, 3f, vehicles, QueryTriggerInteraction.Ignore).Length > 0;
            bool wallNear = Physics.Raycast(chest, flat, 2.5f, level, QueryTriggerInteraction.Ignore)
                            || Physics.Raycast(chest, side, 1.2f, level, QueryTriggerInteraction.Ignore) || Physics.Raycast(chest, -side, 1.2f, level, QueryTriggerInteraction.Ignore);
            if (!vehicleNear && !wallNear)
            {
                return null;
            }
            Vector3 from = player.transform.position;
            for (int ring = 1; ring <= 5; ring++)
            {
                for (int k = 0; k < 12; k++)
                {
                    Vector3 at = from + Quaternion.Euler(0f, k * 30f, 0f) * Vector3.forward * (ring * 5f);
                    if (!Physics.Raycast(at + Vector3.up * 4f, Vector3.down, out RaycastHit ground, 8f, ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2), QueryTriggerInteraction.Ignore)
                        || ground.rigidbody != null || ground.normal.y < 0.85f)
                    {
                        continue;
                    }
                    Vector3 eye = ground.point + Vector3.up * 1.5f, waist = ground.point + Vector3.up * 1.1f;
                    if (Physics.OverlapSphere(eye + flat * 1f, 4f, vehicles, QueryTriggerInteraction.Ignore).Length > 0
                        || Physics.Raycast(eye, flat, 3f, level | vehicles, QueryTriggerInteraction.Ignore) || Physics.Raycast(waist, flat, 3f, level | vehicles, QueryTriggerInteraction.Ignore)
                        || Physics.Raycast(waist, side, 1.5f, level, QueryTriggerInteraction.Ignore) || Physics.Raycast(waist, -side, 1.5f, level, QueryTriggerInteraction.Ignore)
                        || Physics.CheckCapsule(ground.point + Vector3.up * 0.4f, ground.point + Vector3.up * 1.7f, 0.35f, level, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }
                    player.SetPositionAndRotation(ground.point, player.transform.rotation);
                    return $"{(vehicleNear ? "a vehicle" : "a wall")} was where the soldiers are brought: moved {Vector3.Distance(from, ground.point):0} m to open ground first";
                }
            }
            return $"{(vehicleNear ? "a vehicle" : "a wall")} is where the soldiers are brought, and no open ground was found within 25 m";
        }

        /// <summary>
        /// Soldiers taken hold of, thrown, punched and more (user, 2026-10-24: "I cannot grab people with my hand and throw them
        /// around", "punch them with parts of my weapons or my hands if they are fists", then "screenshot the boxing and boxing
        /// while holding a gun", "make sure that i cannot grab people while I have a gun equipped", "human shield", "Execution
        /// from behind turning the neck", "Strapping grenade to a body and throwing it"; task 336). Each step brings an enemy to
        /// arm's length (respawned there standing, its AI paused, the living first: a dead one was moved off by the game's own
        /// respawn mid-check) and checks it is there before acting; every moment is pictured from the eye and from the side
        /// (grab-NN-*.png). The enemies brought are killed at the end (one left at arm's length would fight the rest of the run).
        /// </summary>
        private static IEnumerator BodyGrabCheck(Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            Actor player = LocalPlayer.actor;
            Rig.VRHand off = rig != null ? rig.Off : null;
            Rig.VRHand dom = rig != null ? rig.Dominant : null;
            WeaponSystem sys = WeaponSystem.Instance;
            if (rig == null || player == null || off == null || dom == null || sys == null || off.Role != Rig.HandRole.Free || off.Held != null
                || ActorManager.instance == null)
            {
                report($"soldier grab: skipped (off hand {(off != null ? off.Role.ToString() : "missing")})");
                yield break;
            }
            // A vehicle where the soldiers are brought: open ground first. Beside island2023's tank every step after the first few failed with
            // "the soldier could not be brought: 4.5 m from the spot", a shield, a neck, a strap and a drawn gun all NOT HELD (2026-11-08).
            string movedNote = MoveOffVehicles(player, rig);
            if (movedNote != null)
            {
                report("soldier grab: " + movedNote);
                yield return new WaitForSecondsRealtime(0.6f);
            }
            var enemies = new List<Actor>();
            foreach (Actor a in ActorManager.instance.actors)
            {
                if (a != null && a != player && a.aiControlled && a.team != player.team && a.ragdoll != null && a.controller != null)
                {
                    enemies.Add(a);
                }
            }
            if (enemies.Count == 0)
            {
                report("soldier grab: skipped (no enemy bot in the match)");
                yield break;
            }
            Vector3 headAt = rig.Head.position;
            Vector3 flat = Vector3.ProjectOnPlane(rig.Head.forward, Vector3.up).normalized;
            Vector3 sideAxis = Vector3.Cross(Vector3.up, flat).normalized;
            int os = (int)off.Side, ds = (int)dom.Side;
            Runtime.RigidPose offRaw = off.Raw, domRaw = dom.Raw;
            Vector3 offPalmFromRaw = off.PalmCentre - offRaw.position;
            Vector3 domPalmFromRaw = dom.PalmCentre - domRaw.position;
            var paused = new List<Actor>();
            var used = new HashSet<Actor>();
            string broughtNote = "";
            MeleeHandler.RecordNotes = true;
            // Every other bot of the match quiet for the check (their AI paused, put back after): the player's own team shot the
            // enemies brought to arm's length, so a shield died before a shot and soldiers lost 30-40 before their throw, none of
            // it the grab's doing (2026-10-24). The shield's shooter fires by hand, without his AI.
            var quiet = new List<Actor>();
            foreach (Actor a in ActorManager.instance.actors)
            {
                if (a != null && a != player && a.aiControlled && a.controller != null && a.controller.enabled)
                {
                    a.controller.enabled = false;
                    quiet.Add(a);
                }
            }

            // Who comes next: the living on foot first, then the seated, the dead last (respawned, the game may take a dead one away).
            int Rank(Actor a) => a.dead ? 2 : a.IsSeated() ? 1 : 0;
            // (The game's soldiers always have five weapon slots: an armed one has something in one.)
            bool Armed(Actor a) => a.activeWeapon != null || a.weapons != null && a.weapons.Any(w => w != null);
            Actor Next(bool needWeapon)
            {
                Actor best = null;
                foreach (Actor a in enemies)
                {
                    if (a == null || used.Contains(a) || needWeapon && !Armed(a))
                    {
                        continue;
                    }
                    if (best == null || Rank(a) < Rank(best))
                    {
                        best = a;
                    }
                }
                if (best == null)
                {
                    used.Clear(); // all used: round again
                    foreach (Actor a in enemies)
                    {
                        if (a != null && !(needWeapon && !Armed(a)) && (best == null || Rank(a) < Rank(best)))
                        {
                            best = a;
                        }
                    }
                }
                if (best != null)
                {
                    used.Add(best);
                }
                return best;
            }

            Vector3 Spot(float ahead)
            {
                Vector3 at = headAt + flat * ahead;
                at.y = player.transform.position.y;
                if (Physics.Raycast(at + Vector3.up * 1.5f, Vector3.down, out RaycastHit ground, 4f, ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2), QueryTriggerInteraction.Ignore))
                {
                    at.y = ground.point.y;
                }
                return at;
            }

            // Respawned standing at the spot (the game's own respawn: full health, out of any seat), its AI paused; then checked it
            // is there (respawned once more if the game moved it off). False: it is not.
            IEnumerator Bring(Actor a, float ahead, bool facingAway, bool[] there)
            {
                Vector3 at = Spot(ahead);
                // The earlier steps' soldiers still standing at the spot sent away first: left there, the next grab took the one
                // from the step before (alive, standing in the same place: "took hold of Lens, NOT DOWN", 2026-10-24).
                int away = 0;
                foreach (Actor p in paused)
                {
                    if (p != null && p != a && !p.dead && (Vector3.ProjectOnPlane(p.CenterPosition() - at, Vector3.up)).sqrMagnitude < 4f)
                    {
                        p.SpawnAt(Spot(-20f) + sideAxis * (2f * ++away), Quaternion.LookRotation(flat, Vector3.up));
                    }
                }
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    // Out of a seat first: a seated soldier hangs under his seat, and the respawn leaves him there (he went on
                    // riding the vehicle, away from the spot); leaving the seat puts him back under his own parent.
                    if (a.IsSeated())
                    {
                        a.LeaveSeat(false);
                    }
                    if (a.transform.parent != a.originalParent)
                    {
                        a.transform.SetParent(a.originalParent, true);
                    }
                    // Its AI on while it respawns (paused just after, as before): paused already (the quiet bots), it kept its old
                    // facing and turned the body back to it, and the neck check's soldier no longer faced away (2026-10-24).
                    if (a.controller != null)
                    {
                        a.controller.enabled = true;
                    }
                    a.SpawnAt(at, Quaternion.LookRotation(facingAway ? flat : -flat, Vector3.up));
                    // A bot that had died has no squad until the game's own spawner gives it one: the game's AI then read the
                    // missing squad every frame (NullReferenceException in AiActorController.GetWeaponEffectivenessAgainstTarget).
                    if (a.controller is AiActorController ai && ai.squad == null)
                    {
                        new Squad(new[] { a }, ActorManager.RandomSpawnPointForTeam(a.team), null, null, 0f);
                    }
                    a.controller.enabled = false;
                    // And looking the way he was put (paused, the AI's last look stayed as it was: the game turns his body toward his
                    // look once it is 50 degrees off, and the neck check's soldier ended up facing 94-107 degrees off away).
                    if (a.controller is AiActorController looker)
                    {
                        looker.facingDirectionVector = facingAway ? flat : -flat;
                    }
                    if (!paused.Contains(a))
                    {
                        paused.Add(a);
                    }
                    yield return new WaitForSecondsRealtime(0.6f);
                    if (!a.dead && (Vector3.ProjectOnPlane(a.CenterPosition() - at, Vector3.up)).sqrMagnitude < 1f)
                    {
                        there[0] = true;
                        yield break;
                    }
                }
                there[0] = false;
                broughtNote = $"{a.name}: {(a.dead ? "dead" : $"{Vector3.ProjectOnPlane(a.CenterPosition() - at, Vector3.up).magnitude:0.0} m from the spot")}"
                              + $"{(a.IsSeated() ? ", seated" : "")}{(a.transform.parent != null ? $", under {a.transform.parent.name}" : "")}";
            }

            // The palm put at a point with the hand turned: the palm's offset from the controller turns with the hand (put with the
            // offset of the hand as it rested, a turned hand missed the head it was put at by a hand's width).
            void OffAt(Vector3 palm, Quaternion? rotation = null, Vector3 velocity = default)
            {
                Quaternion r = rotation ?? offRaw.rotation;
                Rig.VRHand.Scripted[os] = new Runtime.RigidPose(palm - r * Quaternion.Inverse(offRaw.rotation) * offPalmFromRaw, r);
                Rig.VRHand.ScriptedVelocity[os] = velocity;
            }

            void DomAt(Vector3 palm, Quaternion rotation, Vector3 velocity = default)
            {
                Rig.VRHand.Scripted[ds] = new Runtime.RigidPose(palm - rotation * Quaternion.Inverse(domRaw.rotation) * domPalmFromRaw, rotation);
                Rig.VRHand.ScriptedVelocity[ds] = velocity;
            }

            // The gun hand carried to a point slowly (under a metre a second: no bash on the way, as a hand moving a gun into
            // a soldier's chest would not be).
            // The free hand carried to a point at a hand's pace (under a metre a second), turned as it rested: put there in one
            // frame, a held soldier was carried a metre in a frame and hurt by the move (2026-10-24).
            IEnumerator OffTo(Vector3 palm)
            {
                Vector3 from = off.PalmCentre;
                // (At most 3 s: from a hand sent far off, or to a body flung far away, the move never ended and the run waited out its
                // timeout; a soldier run, 2026-10-25.)
                float seconds = Mathf.Clamp((palm - from).magnitude / 0.8f, 0.3f, 3f);
                for (float start = Time.realtimeSinceStartup, k = 0f; k < 1f;)
                {
                    k = Mathf.Clamp01((Time.realtimeSinceStartup - start) / seconds);
                    OffAt(Vector3.Lerp(from, palm, k));
                    yield return null;
                }
            }

            // A held body watched for some seconds (the hand left where it is): result[0] its fastest bone (m/s), result[1] how far its
            // centre rose above where it started (m); note[0] (when given) which bone was fastest and in which frame.
            IEnumerator HeldBodyFor(Actor a, float seconds, float[] result, string[] note = null)
            {
                float from = a.CenterPosition().y;
                int frame = 0;
                for (float start = Time.realtimeSinceStartup; Time.realtimeSinceStartup < start + seconds && a.ragdoll.rigidbodies != null;)
                {
                    yield return null;
                    frame++;
                    foreach (Rigidbody rb in a.ragdoll.rigidbodies)
                    {
                        float s = rb != null ? rb.velocity.magnitude : 0f;
                        if (s > result[0])
                        {
                            result[0] = s;
                            if (note != null)
                            {
                                note[0] = $"{rb.name}, frame {frame}";
                            }
                        }
                    }
                    result[1] = Mathf.Max(result[1], a.CenterPosition().y - from);
                }
            }

            IEnumerator DomTo(Vector3 palm, Quaternion rotation)
            {
                Vector3 from = dom.PalmCentre;
                Quaternion fromRotation = Rig.VRHand.Scripted[ds].HasValue ? Rig.VRHand.Scripted[ds].Value.rotation : domRaw.rotation;
                float seconds = Mathf.Clamp((palm - from).magnitude / 0.8f, 0.3f, 3f); // (at most 3 s, as OffTo)
                for (float start = Time.realtimeSinceStartup, k = 0f; k < 1f;)
                {
                    k = Mathf.Clamp01((Time.realtimeSinceStartup - start) / seconds);
                    DomAt(Vector3.Lerp(from, palm, k), Quaternion.Slerp(fromRotation, rotation, k));
                    yield return null;
                }
            }

            // The gun hand down at the hip: a gun held out into a soldier who appears there is a bash.
            Quaternion hipRotation = Quaternion.LookRotation(flat * 0.3f + Vector3.down, flat);
            Vector3 hip = headAt - Vector3.up * 0.7f + sideAxis * 0.3f * (dom.IsLeft ? -1f : 1f);
            void GunToHip() => DomAt(hip, hipRotation);

            IEnumerator Snap(string name, Vector3 focus)
            {
                yield return new WaitForEndOfFrame();
                Shots.Capture("grab-" + name);
                Vector3 from = focus + sideAxis * 2.2f + Vector3.up * 0.5f - flat * 0.6f;
                Shots.Capture("grab-" + name + "-side", from, Quaternion.LookRotation(focus - from, Vector3.up), 70f);
            }

            void HandAt(Rig.VRHand hand, Vector3 palm, Quaternion rotation, Vector3 velocity = default)
            {
                if (hand == off)
                {
                    OffAt(palm, rotation, velocity);
                }
                else
                {
                    DomAt(palm, rotation, velocity);
                }
            }

            // A strike: the hand driven from a point along a way at a speed, until the target is hurt or the way is done. Each frame's
            // step from the frame time itself (a slow frame made one step of the whole punch). hit[0]: it hurt.
            IEnumerator Strike(Rig.VRHand hand, Vector3 from, Vector3 way, float distance, float speed, Quaternion rotation, Actor target, bool[] hit)
            {
                float hp = target.health;
                Vector3 at = from;
                while ((at - from).sqrMagnitude < distance * distance && target.health >= hp - 1f && !target.dead)
                {
                    at += way * Mathf.Min(speed * Mathf.Max(Time.unscaledDeltaTime, 0.011f), 0.12f);
                    HandAt(hand, at, rotation, way * speed);
                    yield return null;
                }
                yield return null;
                hit[0] = target.dead || target.health < hp - 1f;
            }

            // A punch: a closed empty hand driven through the soldier's centre at 7 m/s from 45 cm before it.
            IEnumerator Punch(Rig.VRHand hand, Actor target, Vector3 sideOffset, bool[] hit)
            {
                Vector3 from = target.CenterPosition() + sideOffset - flat * 0.45f;
                Quaternion fistRotation = Quaternion.LookRotation(flat, Vector3.up);
                Input.VRInput.SimulatedGrip[(int)hand.Side] = 1f;
                for (int i = 0; i < 4; i++)
                {
                    HandAt(hand, from, fistRotation); // closed and still first
                    yield return null;
                }
                yield return Strike(hand, from, flat, 0.6f, 7f, fistRotation, target, hit);
            }

            // The free hand put on a soldier and its grip closed; took[0]: it holds a body (half a second at most).
            IEnumerator Take(Vector3 at, bool[] took)
            {
                OffAt(at);
                yield return null;
                Input.VRInput.SimulateGrabPress(off.Side);
                for (float until = Time.realtimeSinceStartup + 0.5f; off.Role != Rig.HandRole.Body && Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                }
                took[0] = off.Role == Rig.HandRole.Body;
            }

            // The free hand's grip opened, and the hand back where it rests.
            IEnumerator LetGo()
            {
                Input.VRInput.SimulatedGrab[os] = false;
                yield return null;
                OffAt(offRaw.position + offPalmFromRaw);
            }

            // The free hand swung at a velocity for eight frames (a throw when the grip opens right after). fastest[0]: the
            // fastest bone of the held body while swung (a blow-up starting in the hand tells itself apart from one in flight).
            IEnumerator Swing(Vector3 velocity, string pictureAtFive, Actor watch = null, float[] fastest = null)
            {
                Vector3 palm = off.PalmCentre;
                for (int i = 0; i < 8; i++)
                {
                    palm += velocity * 0.011f;
                    OffAt(palm, null, velocity);
                    yield return null;
                    if (fastest != null && watch != null && watch.ragdoll != null && watch.ragdoll.rigidbodies != null)
                    {
                        foreach (Rigidbody rb in watch.ragdoll.rigidbodies)
                        {
                            fastest[0] = rb != null ? Mathf.Max(fastest[0], rb.velocity.magnitude) : fastest[0];
                        }
                    }
                    if (i == 5 && pictureAtFive != null)
                    {
                        yield return Snap(pictureAtFive, palm);
                    }
                }
            }

            string Health(Actor a) => a.dead ? "dead" : a.health.ToString("0");

            // Whether a soldier carries another weapon than his gun out (not a grenade): one he can draw when it is wrested.
            bool HasOtherWeapon(Actor a)
            {
                if (a == null || a.weapons == null || a.activeWeapon == null)
                {
                    return false;
                }
                foreach (Weapon w in a.weapons)
                {
                    if (w != null && w != a.activeWeapon && !(w is ThrowableWeapon))
                    {
                        return true;
                    }
                }
                return false;
            }

            // Whether a collider of soldier a lies within radius of point (his arms and rifle as well as his body).
            bool HisWithin(Actor a, Vector3 point, float radius)
            {
                foreach (Collider c in Physics.OverlapSphere(point, radius, Hitbox.ALL_HITBOX_LAYER_MASK, QueryTriggerInteraction.Collide))
                {
                    if (c != null && BodyGrab.HurtableOf(c) == a)
                    {
                        return true;
                    }
                }
                return false;
            }

            try
            {
                var there = new bool[1];
                var hit = new bool[1];
                GunToHip();
                yield return new WaitForSecondsRealtime(0.7f);
                HeldWeapon gun = sys.Held;
                if (WalkCheckOnly)
                {
                    goto Walks;
                }

                // 0. The gun hand never takes hold of a soldier: the gun put to one's chest and its grip closed.
                Actor first = Next(false);
                if (gun != null && gun.Weapon != null && first != null)
                {
                    // The gun turned to point ahead while no one is in front (turned on the way, its muzzle's sweep knocked the
                    // soldier over and the check took that for a grab).
                    yield return DomTo(hip + flat * 0.1f, Quaternion.LookRotation(flat, Vector3.up));
                    yield return new WaitForSecondsRealtime(0.6f);
                    yield return Bring(first, 0.8f, false, there);
                    if (there[0])
                    {
                        yield return DomTo(first.CenterPosition() - flat * 0.12f, Quaternion.LookRotation(flat, Vector3.up));
                        Input.VRInput.SimulateGrabPress(dom.Side);
                        yield return new WaitForSecondsRealtime(0.5f);
                        bool took = dom.Role == Rig.HandRole.Body || BodyGrab.IsHeld(first);
                        yield return Snap("00-gun-hand-does-not-grab", first.CenterPosition());
                        Input.VRInput.SimulatedGrab[ds] = false;
                        report($"gun hand: {(took ? "GRABBED A SOLDIER WITH THE GUN IN THE HAND" : $"the grip on the gun ({(gun.Weapon != null ? gun.Weapon.name : "gone since")}) takes no soldier")}");
                    }
                    else
                    {
                        report($"gun hand: skipped (the soldier could not be brought: {broughtNote})");
                    }
                    GunToHip();
                    yield return new WaitForSecondsRealtime(0.7f);
                }
                else
                {
                    report($"gun hand: skipped ({(gun == null || gun.Weapon == null ? "no gun in the hand" : "no soldier")})");
                }

                // 1. Taken hold of and thrown (the free hand; the other holds the gun).
                Actor held = Next(false);
                yield return Bring(held, 0.8f, false, there);
                if (!there[0])
                {
                    report($"soldier grab: skipped (the soldier could not be brought: {broughtNote})");
                }
                else
                {
                    yield return Snap("01-soldier-in-front", held.CenterPosition());
                    yield return Take(held.CenterPosition(), hit);
                    bool grabbed = hit[0];
                    bool down = held.ragdoll.IsRagdoll();
                    yield return new WaitForSecondsRealtime(0.3f); // hanging from the hand
                    yield return Snap("02-taken-hold-of", off.PalmCentre);
                    float before = held.health;
                    float fastest = 0f;
                    string fastestNote = "";
                    int overCap = 0; // frames with a bone past the 25 m/s cap (one whip, or a body that keeps flying apart)
                    var swungFastest = new float[1];
                    yield return Swing((flat + Vector3.up * 0.6f).normalized * 6f, "03-swinging", held, swungFastest);
                    Vector3 palm = off.PalmCentre;
                    Input.VRInput.SimulatedGrab[os] = false;
                    yield return null;
                    yield return null;
                    Rigidbody main = held.ragdoll.IsRagdoll() ? held.ragdoll.MainRigidbody() : null;
                    float flew = main != null ? main.velocity.magnitude : 0f;
                    bool letGo = off.Role == Rig.HandRole.Free;
                    yield return Snap("04-let-go", main != null ? main.position : palm);
                    OffAt(offRaw.position + offPalmFromRaw);
                    bool flyingShot = false;
                    for (float start = Time.realtimeSinceStartup; Time.realtimeSinceStartup < start + 2f;)
                    {
                        if (held.ragdoll.rigidbodies != null)
                        {
                            bool over = false;
                            foreach (Rigidbody rb in held.ragdoll.rigidbodies)
                            {
                                float s = rb != null ? rb.velocity.magnitude : 0f;
                                over |= s > 25.5f;
                                if (s > fastest)
                                {
                                    fastest = s;
                                    fastestNote = $"{rb.name}, {Time.realtimeSinceStartup - start:0.00} s after, {(held.dead ? "dead" : "alive")}";
                                }
                            }
                            overCap += over ? 1 : 0;
                        }
                        if (!flyingShot && Time.realtimeSinceStartup > start + 0.35f)
                        {
                            flyingShot = true;
                            yield return Snap("05-flying", main != null ? main.position : palm);
                        }
                        yield return null;
                    }
                    yield return Snap("06-landed", main != null ? main.position : palm);
                    Metric("soldier thrown: body m/s", flew);
                    bool blewUp = fastest > 100f || overCap > 20;
                    report($"soldier grab: {(grabbed ? $"took hold of {held.name}" : "NOT GRABBED")}, {(down ? "down as a ragdoll" : "NOT DOWN")}; "
                           + $"{(letGo ? "let go" : "NOT LET GO")} in a 6 m/s swing, the body at {flew:0.0} m/s{(grabbed && flew < 2f ? " (NOT THROWN with the swing)" : "")}"
                           // (Bones are held to 25 m/s while armed, each physics step; a limp limb flicking off the ground as it is let go
                           // goes past it inside a step, 18-53 m/s for a few frames in 20 runs. A blow-up was the body flying apart: 180,
                           // 834-2325 m/s, 190-260 frames past the cap, 2026-10-24.)
                           + $"{(blewUp ? $" (THE PHYSICS BLEW UP: a bone at {fastest:0} m/s" : $", fastest bone after {fastest:0.0} m/s")} ({fastestNote}; {overCap} frame(s) past the cap){(blewUp ? ")" : "")}, "
                           + $"health {before:0} -> {Health(held)} after landing; fastest bone while swung {swungFastest[0]:0.0} m/s");
                }

                // 1b. Slammed into the ground while still held: a held soldier is hurt by what he is slammed into, not only once
                // thrown (user, 2026-10-24: "slamming people against things while still holding them also makes them take damage").
                Actor slammed = Next(false);
                yield return Bring(slammed, 0.8f, false, there);
                if (!there[0])
                {
                    report($"slam while held: skipped (the soldier could not be brought: {broughtNote})");
                }
                else
                {
                    yield return Take(slammed.CenterPosition(), hit);
                    bool holdingSlam = hit[0];
                    Vector3 high = headAt + flat * 0.5f;
                    yield return OffTo(high);
                    yield return new WaitForSecondsRealtime(0.4f);
                    float slamFrom = slammed.health;
                    // Straight down at 8 m/s to 20 cm over the ground, still gripping (stops at the first hurt).
                    float drop = Mathf.Max(0.3f, high.y - Spot(0.5f).y - 0.2f);
                    yield return Strike(off, off.PalmCentre, Vector3.down, drop, 8f, offRaw.rotation, slammed, hit);
                    yield return new WaitForSecondsRealtime(0.3f);
                    bool slamHurt = slammed.dead || slammed.health < slamFrom - 1f;
                    bool stillHeld = BodyGrab.IsHeld(slammed);
                    yield return Snap("06b-slammed-while-held", slammed.CenterPosition());
                    yield return LetGo();
                    report($"slam while held: {(holdingSlam ? $"{slammed.name} held" : "NOT HELD")}, slammed down at 8 m/s: "
                           + $"{(slamHurt ? $"hurt ({slamFrom:0} -> {Health(slammed)})" : "NOT HURT BY THE SLAM")}, {(stillHeld ? "still held" : "let go")}");
                }

                // 1c. Held in a hand that trembles as a tracked controller does (2 mm and 1 degree a frame, 2 s): the user's held soldiers
                // "go crazy and fling in the air" (2026-10-25), and the scripted hand, perfectly still, never showed it.
                Actor shaken = Next(false);
                yield return Bring(shaken, 0.8f, false, there);
                if (!there[0])
                {
                    report($"held in a trembling hand: skipped (the soldier could not be brought: {broughtNote})");
                }
                else
                {
                    yield return Take(shaken.CenterPosition(), hit);
                    bool holdingShaken = hit[0];
                    yield return new WaitForSecondsRealtime(0.5f); // hanging from the hand first
                    Vector3 still = off.PalmCentre;
                    var tremor = new System.Random(7); // (the same tremor every run)
                    float Noise() => (float)(tremor.NextDouble() * 2.0 - 1.0);
                    float shakeFastest = 0f, risen = 0f, hipsFrom = shaken.CenterPosition().y;
                    int shakeFrames = 0, wildFrames = 0;
                    for (float start = Time.realtimeSinceStartup; Time.realtimeSinceStartup < start + 2f && shaken.ragdoll.rigidbodies != null;)
                    {
                        OffAt(still + new Vector3(Noise(), Noise(), Noise()) * 0.002f, Quaternion.Euler(Noise(), Noise(), Noise()) * offRaw.rotation);
                        yield return null;
                        float frameFastest = 0f;
                        foreach (Rigidbody rb in shaken.ragdoll.rigidbodies)
                        {
                            frameFastest = Mathf.Max(frameFastest, rb != null ? rb.velocity.magnitude : 0f);
                        }
                        shakeFrames++;
                        wildFrames += frameFastest > 6f ? 1 : 0;
                        shakeFastest = Mathf.Max(shakeFastest, frameFastest);
                        risen = Mathf.Max(risen, shaken.CenterPosition().y - hipsFrom);
                    }
                    bool heldOn = BodyGrab.IsHeld(shaken);
                    yield return Snap("06c-held-in-a-trembling-hand", shaken.CenterPosition());
                    Metric("held in a trembling hand: fastest bone m/s", shakeFastest);
                    Metric("held in a trembling hand: frames with a bone over 6 m/s", wildFrames);
                    bool wild = shakeFastest > 10f || wildFrames > shakeFrames / 10;
                    report($"held in a trembling hand (2 mm, 1 deg a frame, 2 s): {(holdingShaken ? $"{shaken.name} held" : "NOT HELD")}, fastest bone {shakeFastest:0.0} m/s, "
                           + $"{wildFrames} of {shakeFrames} frames with a bone over 6 m/s, the body rose {risen * 100f:0} cm"
                           + $"{(wild ? " (NOT STILL: the body went wild)" : "")}{(heldOn ? "" : ", NOT HELD ON")}");

                    // A snap turn while holding him: the hand swung 45 degrees round the head in one frame and turned with it, as the
                    // rig turns it (a hand near the head moves under the 25 cm a jump needs, but turns all the same).
                    if (heldOn)
                    {
                        Quaternion snap = Quaternion.AngleAxis(45f, Vector3.up);
                        OffAt(headAt + snap * (still - headAt), snap * offRaw.rotation);
                        float[] turned = new float[2];
                        string[] turnNote = { "" };
                        yield return HeldBodyFor(shaken, 1f, turned, turnNote);
                        bool turnHeld = BodyGrab.IsHeld(shaken);
                        Metric("held through a snap turn: fastest bone m/s", turned[0]);
                        report($"held through a 45 degree snap turn: fastest bone {turned[0]:0.0} m/s ({turnNote[0]}), the body rose {turned[1] * 100f:0} cm"
                               + $"{(turned[0] > 10f || turned[1] > 0.5f ? " (NOT STILL: the body went wild)" : "")}{(turnHeld ? "" : ", NOT HELD ON")}");
                        // And a quick turn of the wrist (90 degrees in 0.2 s and back, about 450 a second): under the snap turn's 20 a
                        // frame, the hand's turn reaches the body through the hold.
                        Vector3 wristAt = headAt + snap * (still - headAt);
                        Quaternion wristFrom = snap * offRaw.rotation;
                        Vector3 forearm = snap * flat;
                        float[] twisted = new float[2];
                        string[] twistNote = { "" };
                        float twistBase = shaken.CenterPosition().y;
                        int twistFrame = 0;
                        for (float start = Time.realtimeSinceStartup, k = 0f; k < 1f && shaken.ragdoll.rigidbodies != null;)
                        {
                            k = Mathf.Clamp01((Time.realtimeSinceStartup - start) / 0.4f);
                            float angle = 90f * (k < 0.5f ? k * 2f : 2f - k * 2f);
                            OffAt(wristAt, Quaternion.AngleAxis(angle, forearm) * wristFrom);
                            yield return null;
                            twistFrame++;
                            foreach (Rigidbody rb in shaken.ragdoll.rigidbodies)
                            {
                                float s = rb != null ? rb.velocity.magnitude : 0f;
                                if (s > twisted[0])
                                {
                                    twisted[0] = s;
                                    twistNote[0] = $"{rb.name}, frame {twistFrame} of the turn at {angle:0} deg";
                                }
                            }
                            twisted[1] = Mathf.Max(twisted[1], shaken.CenterPosition().y - twistBase);
                        }
                        yield return HeldBodyFor(shaken, 0.5f, twisted, twistNote);
                        Metric("held through a quick wrist turn: fastest bone m/s", twisted[0]);
                        report($"held through a quick wrist turn (90 degrees in 0.2 s and back): fastest bone {twisted[0]:0.0} m/s ({twistNote[0]}), the body rose {twisted[1] * 100f:0} cm"
                               + $"{(twisted[0] > 10f || twisted[1] > 0.5f ? " (NOT STILL: the body went wild)" : "")}{(BodyGrab.IsHeld(shaken) ? "" : ", NOT HELD ON")}");
                    }
                    yield return LetGo();
                    yield return new WaitForSecondsRealtime(1.5f); // down on the ground

                    // A body lying on the ground taken with the hand a little into the ground (a controller is not stopped by the
                    // terrain), then lifted to the chest at a hand's pace.
                    Rigidbody lying = shaken.ragdoll.IsRagdoll() ? shaken.ragdoll.MainRigidbody() : null;
                    if (lying != null)
                    {
                        Vector3 reach = lying.worldCenterOfMass;
                        if (Physics.Raycast(reach + Vector3.up * 0.5f, Vector3.down, out RaycastHit floor, 2f, ~Hitbox.ALL_HITBOX_LAYER_MASK & ~(1 << 2), QueryTriggerInteraction.Ignore))
                        {
                            reach.y = Mathf.Min(reach.y, floor.point.y + 0.05f); // 5 cm over the ground at most: the hand dips under the body
                        }
                        yield return Take(reach - Vector3.up * 0.08f, hit);
                        bool tookLying = hit[0];
                        float[] lifted = new float[2];
                        if (tookLying)
                        {
                            // Lifted at a hand's pace (as OffTo), watched all the way up, then held at the chest.
                            Vector3 liftFrom = off.PalmCentre, liftTo = headAt + flat * 0.45f - Vector3.up * 0.3f;
                            float liftSeconds = Mathf.Clamp((liftTo - liftFrom).magnitude / 0.8f, 0.3f, 3f);
                            for (float start = Time.realtimeSinceStartup, k = 0f; k < 1f && shaken.ragdoll.rigidbodies != null;)
                            {
                                k = Mathf.Clamp01((Time.realtimeSinceStartup - start) / liftSeconds);
                                OffAt(Vector3.Lerp(liftFrom, liftTo, k));
                                yield return null;
                                foreach (Rigidbody rb in shaken.ragdoll.rigidbodies)
                                {
                                    lifted[0] = Mathf.Max(lifted[0], rb != null ? rb.velocity.magnitude : 0f);
                                }
                            }
                            yield return HeldBodyFor(shaken, 0.8f, lifted);
                        }
                        Metric("lying body taken into the ground and lifted: fastest bone m/s", lifted[0]);
                        report($"lying body taken 8 cm into the ground and lifted: {(tookLying ? "taken" : "NOT TAKEN")}, fastest bone {lifted[0]:0.0} m/s held at the chest"
                               + $"{(lifted[0] > 10f ? " (NOT STILL: the body went wild)" : "")}");
                        yield return LetGo();
                    }
                }

                // 1d. A soldier taken as he runs (user, 2026-10-25: "all the npcs i grabbed were walking or running and then flinged around
                // in my hand and formed themselves long bodys and flinged in the air"): the game ragdolls him with his running speed in
                // every bone, then the hand holds one of them. Knocked over running at 6 m/s across the hand, taken on the next frame.
                Actor runner = Next(false);
                yield return Bring(runner, 0.8f, false, there);
                if (!there[0])
                {
                    report($"taken running: skipped (the soldier could not be brought: {broughtNote})");
                }
                else
                {
                    Vector3 run = sideAxis * 6f;
                    Vector3 grabAt = runner.CenterPosition();
                    runner.FallOver();
                    foreach (Rigidbody rb in runner.ragdoll.rigidbodies)
                    {
                        if (rb != null)
                        {
                            rb.velocity = run;
                        }
                    }
                    // The body's span (its two bones farthest apart): a body pulled apart at its joints is "long".
                    float Span()
                    {
                        float most = 0f;
                        Rigidbody[] bones = runner.ragdoll.rigidbodies;
                        for (int a = 0; bones != null && a < bones.Length; a++)
                        {
                            for (int b = a + 1; b < bones.Length; b++)
                            {
                                if (bones[a] != null && bones[b] != null)
                                {
                                    most = Mathf.Max(most, (bones[a].position - bones[b].position).magnitude);
                                }
                            }
                        }
                        return most;
                    }
                    float restSpan = Span();
                    yield return Take(grabAt + run * 0.011f, hit);
                    bool tookRunner = hit[0];
                    float[] ran = new float[2];
                    float longest = restSpan;
                    if (tookRunner)
                    {
                        float riseFrom = runner.CenterPosition().y;
                        for (float start = Time.realtimeSinceStartup; Time.realtimeSinceStartup < start + 1.2f && runner.ragdoll.rigidbodies != null;)
                        {
                            yield return null;
                            foreach (Rigidbody rb in runner.ragdoll.rigidbodies)
                            {
                                ran[0] = Mathf.Max(ran[0], rb != null ? rb.velocity.magnitude : 0f);
                            }
                            ran[1] = Mathf.Max(ran[1], runner.CenterPosition().y - riseFrom);
                            longest = Mathf.Max(longest, Span());
                        }
                    }
                    yield return Snap("06d-taken-running", runner.CenterPosition());
                    yield return LetGo();
                    Metric("taken running: fastest bone m/s", ran[0]);
                    Metric("taken running: longest span / at rest", restSpan > 0f ? longest / restSpan : 0f);
                    bool stretched = restSpan > 0f && longest > restSpan * 1.25f;
                    report($"taken running (6 m/s across the hand): {(tookRunner ? $"{runner.name} taken" : "NOT TAKEN")}, fastest bone {ran[0]:0.0} m/s, the body rose {ran[1] * 100f:0} cm, "
                           + $"its span {restSpan:0.00} -> {longest:0.00} m{(stretched ? " (NOT WHOLE: pulled apart at the joints)" : "")}"
                           + $"{(ran[0] > 10f || ran[1] > 0.5f ? " (NOT STILL: the body went wild)" : "")}");
                }

                // 2. A fist while the other hand holds the gun (at the hip).
                Actor boxer = Next(false);
                yield return Bring(boxer, 0.8f, false, there);
                if (!there[0])
                {
                    report($"fist (gun in the other hand): skipped (the soldier could not be brought: {broughtNote})");
                }
                else
                {
                    yield return Snap("07-fist-ready-gun-in-other-hand", boxer.CenterPosition());
                    float hp = boxer.health;
                    yield return Punch(off, boxer, Vector3.zero, hit);
                    yield return Snap("08-fist-hit-gun-in-other-hand", boxer.CenterPosition());
                    Input.VRInput.SimulatedGrip[os] = 0f;
                    report($"fist (gun in the other hand): {(hit[0] ? $"punched {boxer.name}, health {hp:0} -> {Health(boxer)}" : "NO HIT on the enemy at 7 m/s")}"
                           + (hit[0] ? "" : $" [last fist frame: {MeleeHandler.FistNote}; last sweep: {MeleeHandler.CastNote}]"));
                    OffAt(offRaw.position + offPalmFromRaw);
                }

                // 3. Boxing with both hands: the gun put away first.
                Weapon drawn = player.activeWeapon;
                player.HolsterActiveWeapon();
                yield return new WaitForSecondsRealtime(0.8f);
                if (dom.Role != Rig.HandRole.Free || dom.Held != null)
                {
                    report($"boxing: skipped (the gun hand is still {dom.Role})");
                }
                else
                {
                    Actor sparring = boxer != null && !boxer.dead ? boxer : Next(false);
                    yield return Bring(sparring, 0.8f, false, there);
                    if (!there[0])
                    {
                        report($"boxing: skipped (the soldier could not be brought: {broughtNote})");
                    }
                    else
                    {
                        float hp = sparring.health;
                        DomAt(sparring.CenterPosition() - flat * 0.45f + sideAxis * 0.12f * (dom.IsLeft ? -1f : 1f), Quaternion.LookRotation(flat, Vector3.up));
                        OffAt(sparring.CenterPosition() - flat * 0.45f + sideAxis * 0.12f * (off.IsLeft ? -1f : 1f), Quaternion.LookRotation(flat, Vector3.up));
                        Input.VRInput.SimulatedGrip[ds] = 1f;
                        Input.VRInput.SimulatedGrip[os] = 1f;
                        yield return new WaitForSecondsRealtime(0.4f);
                        yield return Snap("09-boxing-guard", sparring.CenterPosition());
                        yield return Punch(dom, sparring, sideAxis * 0.1f * (dom.IsLeft ? -1f : 1f), hit);
                        bool right = hit[0];
                        yield return Snap("10-boxing-gun-hand-punch", sparring.CenterPosition());
                        yield return new WaitForSecondsRealtime(0.4f);
                        // Unbalanced by the first, he was still stumbling or falling and the second punch, aimed at where he was when it
                        // began, passed him (2 of 19 runs): as a boxer would, wait for him to come to rest (1.5 s at most).
                        for (float until = Time.realtimeSinceStartup + 1.5f; Time.realtimeSinceStartup < until && !sparring.dead;)
                        {
                            Rigidbody main = sparring.ragdoll != null && sparring.ragdoll.IsRagdoll() ? sparring.ragdoll.MainRigidbody() : null;
                            if ((main != null ? main.velocity : sparring.Velocity()).sqrMagnitude < 0.5f * 0.5f)
                            {
                                break;
                            }
                            yield return null;
                        }
                        yield return Punch(off, sparring, sideAxis * 0.1f * (off.IsLeft ? -1f : 1f), hit);
                        bool left = hit[0];
                        yield return Snap("11-boxing-other-hand-punch", sparring.CenterPosition());
                        Input.VRInput.SimulatedGrip[ds] = 0f;
                        Input.VRInput.SimulatedGrip[os] = 0f;
                        report($"boxing (no gun): {(right ? "the gun hand's fist hit" : "the gun hand's fist NO HIT")}, {(left ? "the other fist hit" : "the other fist NO HIT")}, "
                               + $"{sparring.name} {hp:0} -> {Health(sparring)}"
                               + (right && left ? "" : $" [last fist frame: {MeleeHandler.FistNote}; last sweep: {MeleeHandler.CastNote}]"));
                    }
                    OffAt(offRaw.position + offPalmFromRaw);
                }
                // The gun back in the hand for the bash.
                if (drawn != null && player.weapons != null)
                {
                    int slot = Array.IndexOf(player.weapons, drawn);
                    if (slot >= 0)
                    {
                        player.SwitchWeapon(slot);
                        yield return new WaitForSecondsRealtime(1.2f);
                    }
                }
                GunToHip();
                yield return new WaitForSecondsRealtime(0.7f);

                // 4. The gun swung sideways through a soldier's chest.
                gun = sys.Held;
                Actor bashed = Next(false);
                if (gun == null || gun.Weapon == null || gun.Rig.Data.Action == ActionType.Melee || gun.Rig.Data.Action == ActionType.Throwable)
                {
                    report("gun bash: skipped (no gun in the hand)");
                }
                else
                {
                    Quaternion along = Quaternion.LookRotation(flat, Vector3.up); // the gun pointing ahead, across the swing
                    Vector3 centre = Spot(0.8f) + Vector3.up * 1.25f;
                    Vector3 swingFrom = centre - flat * 0.45f - sideAxis * 0.6f;
                    DomAt(swingFrom, along); // there before the soldier and past the bash's half-second cooldown
                    yield return new WaitForSecondsRealtime(0.7f);
                    yield return Bring(bashed, 0.8f, false, there);
                    if (!there[0])
                    {
                        report($"gun bash: skipped (the soldier could not be brought: {broughtNote})");
                    }
                    else
                    {
                        yield return Snap("12-gun-ready", bashed.CenterPosition());
                        float hp = bashed.health;
                        yield return Strike(dom, swingFrom, sideAxis, 1.2f, 6f, along, bashed, hit);
                        yield return Snap("13-gun-swung", bashed.CenterPosition());
                        report($"gun bash: {(hit[0] ? $"{gun.Weapon.name} swung into {bashed.name} at 6 m/s, health {hp:0} -> {Health(bashed)}" : $"NO HIT with {gun.Weapon.name} swung through the enemy at 6 m/s [last sweep: {MeleeHandler.CastNote}]")}");
                    }
                    GunToHip();
                    yield return new WaitForSecondsRealtime(0.7f);
                }

                // 5. A human shield: a soldier held up in front, his friend shooting at the player through him.
                Actor shield = Next(false);
                Actor shooter = Next(true);
                if (shooter == shield)
                {
                    // The used list came round to the same soldier (it starts again when all were used): any other armed enemy. The
                    // step was skipped in about half the runs (2026-10-24).
                    shooter = enemies.FirstOrDefault(e => e != null && e != shield && Armed(e));
                }
                if (shield == null || shooter == null || shield == shooter)
                {
                    report("human shield: skipped (two enemies are needed)");
                }
                else
                {
                    yield return Bring(shield, 0.8f, false, there);
                    bool shieldThere = there[0];
                    yield return Bring(shooter, 7f, false, there);
                    if (!shieldThere || !there[0])
                    {
                        report($"human shield: skipped (the soldiers could not be brought: {broughtNote})");
                    }
                    else
                    {
                        if (shooter.activeWeapon == null && shooter.weapons != null)
                        {
                            for (int s = 0; s < shooter.weapons.Length && shooter.activeWeapon == null; s++)
                            {
                                if (shooter.weapons[s] != null)
                                {
                                    shooter.SwitchWeapon(s);
                                }
                            }
                            yield return new WaitForSecondsRealtime(1f);
                        }
                        if (shooter.activeWeapon == null)
                        {
                            // Respawned with nothing in his hands and his AI paused, nothing draws one ("0 shot(s) ... (no weapon)",
                            // 2026-10-24): one of the game's own guns, as the knife is given below.
                            WeaponManager.WeaponEntry gunEntry = WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null
                                && e.prefab.GetComponent<Weapon>() != null && e.prefab.GetComponent<MeleeWeapon>() == null && e.prefab.GetComponent<ThrowableWeapon>() == null);
                            if (gunEntry != null)
                            {
                                shooter.EquipNewWeaponEntry(gunEntry, 0, true);
                                yield return new WaitForSecondsRealtime(1f);
                            }
                        }
                        yield return Take(shield.CenterPosition(), hit);
                        bool holding = hit[0];
                        float liftedFrom = shield.health;
                        // Held up in front of the chest, lifted there at a hand's pace.
                        Vector3 front = headAt + flat * 0.45f - Vector3.up * 0.2f;
                        yield return OffTo(front);
                        yield return new WaitForSecondsRealtime(0.8f);
                        yield return Snap("14-human-shield", front);
                        Weapon w = shooter.activeWeapon;
                        float shieldHp = shield.health, playerHp = player.health;
                        string lifted = $"lifted {liftedFrom:0} -> {Health(shield)}{(shield.dead ? " (THE SHIELD DIED BEFORE THE SHOTS)" : "")}";
                        int shots = 0;
                        // How the held body moves as the shots hit it: each bullet that hits a ragdoll bone pushes it with the
                        // gun's impact force (Projectile, 200 by default), and the user's held soldiers "fling in the air" (2026-10-25).
                        float[] shotFly = new float[2];
                        if (w != null && holding)
                        {
                            Vector3 chest = headAt - Vector3.up * 0.35f;
                            for (int s = 0; s < 6; s++)
                            {
                                Vector3 muzzle = shooter.CenterPosition() + Vector3.up * 0.3f;
                                try
                                {
                                    w.SpawnProjectile((chest - muzzle).normalized, muzzle, false);
                                    shots++;
                                }
                                catch (Exception e)
                                {
                                    report($"human shield: the shot threw {e.GetType().Name}: {e.Message}");
                                    break;
                                }
                                yield return HeldBodyFor(shield, 0.15f, shotFly);
                            }
                            yield return HeldBodyFor(shield, 0.4f, shotFly);
                            Metric("human shield: fastest bone while shot, m/s", shotFly[0]);
                        }
                        yield return Snap("15-human-shield-shot", front);
                        bool stopped = shield.dead || shield.health < shieldHp - 1f;
                        bool playerHit = player.health < playerHp - 1f;
                        report($"human shield: {(holding ? $"{shield.name} held up in front ({lifted})" : "NOT HELD")}, {shots} shot(s) by {shooter.name} ({(w != null ? w.name : "no weapon")}) at the player through him: "
                               + $"{(stopped ? $"the shield took them ({shieldHp:0} -> {Health(shield)})" : "NO SHOT STOPPED BY THE SHIELD")}, "
                               + $"{(playerHit ? $"THE PLAYER WAS HIT ({playerHp:0} -> {player.health:0})" : "the player unhurt")}"
                               + $"; fastest bone while shot {shotFly[0]:0.0} m/s, the body rose {shotFly[1] * 100f:0} cm{(shotFly[0] > 10f ? " (NOT STILL: the body went wild)" : "")}");
                        yield return LetGo();
                        yield return new WaitForSecondsRealtime(0.3f);
                    }
                }

                // 6. Taken by the neck from behind and the neck turned.
                Actor victim = Next(false);
                yield return Bring(victim, 0.6f, true, there);
                if (!there[0])
                {
                    report($"neck: skipped (the soldier could not be brought: {broughtNote})");
                }
                else
                {
                    Vector3 head = victim.CenterPosition() + Vector3.up * 0.45f;
                    try
                    {
                        Transform h = victim.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.Head);
                        head = h != null ? h.position : head;
                    }
                    catch (Exception)
                    {
                        // a rig without the head mapped: above the centre
                    }
                    Quaternion grip = Quaternion.LookRotation(flat, Vector3.up);
                    OffAt(head, grip);
                    yield return null;
                    Input.VRInput.SimulateGrabPress(off.Side);
                    yield return new WaitForSecondsRealtime(0.4f);
                    bool neck = BodyGrab.HoldsNeck(off) && !victim.ragdoll.IsRagdoll();
                    string neckNote = $"palm {(off.PalmCentre - head).magnitude * 100f:0} cm from the head, facing {Vector3.Angle(Vector3.ProjectOnPlane(victim.transform.forward, Vector3.up), flat):0} deg off away, "
                                      + $"hand {off.Role}, {(victim.ragdoll.IsRagdoll() ? "knocked down" : "standing")}";
                    yield return Snap("16-neck-hold-from-behind", head);
                    for (int i = 1; i <= 8; i++)
                    {
                        OffAt(head, Quaternion.AngleAxis(-12f * i, flat) * grip); // the wrist turned, 96 degrees in all
                        yield return null;
                    }
                    yield return new WaitForSecondsRealtime(0.3f);
                    bool snapped = victim.dead;
                    yield return Snap("17-neck-turned", head);
                    yield return LetGo();
                    report($"neck: {(neck ? $"{victim.name} held by the neck from behind, standing" : $"NOT HELD BY THE NECK ({neckNote})")}, {(snapped ? "the turn broke it (dead)" : "NOT KILLED by the turn")}");
                }

                // 7. A live grenade strapped to a held body, and the body thrown.
                int grenadeSlot = -1;
                for (int s = 0; player.weapons != null && s < player.weapons.Length; s++)
                {
                    if (player.weapons[s] is ThrowableWeapon tw && ThrowableHandler.HasPin(tw))
                    {
                        grenadeSlot = s;
                        break;
                    }
                }
                Actor carrier = Next(false);
                if (grenadeSlot < 0)
                {
                    report("grenade strap: skipped (no grenade in the loadout)");
                }
                else
                {
                    yield return Bring(carrier, 0.8f, false, there);
                    if (!there[0])
                    {
                        report($"grenade strap: skipped (the soldier could not be brought: {broughtNote})");
                    }
                    else
                    {
                        yield return Take(carrier.CenterPosition(), hit);
                        bool holding = hit[0];
                        Vector3 front = headAt + flat * 0.5f - Vector3.up * 0.25f;
                        OffAt(front);
                        player.SwitchWeapon(grenadeSlot);
                        for (float until = Time.realtimeSinceStartup + 4f; (sys.Held == null || sys.Held.Weapon != player.weapons[grenadeSlot]) && Time.realtimeSinceStartup < until;)
                        {
                            yield return null;
                        }
                        yield return new WaitForSecondsRealtime(0.5f);
                        HeldWeapon grenade = sys.Held;
                        bool drawnGrenade = grenade != null && grenade.Weapon == player.weapons[grenadeSlot];
                        // Ready to leave the hand (drawn, the game lets it be thrown), as the pin check waits too.
                        for (float until = Time.realtimeSinceStartup + 3f; drawnGrenade && (!grenade.Weapon.unholstered || !grenade.Weapon.CanFire())
                                                                             && Time.realtimeSinceStartup < until;)
                        {
                            yield return null;
                        }
                        Rigidbody body = carrier.ragdoll.IsRagdoll() ? carrier.ragdoll.MainRigidbody() : null;
                        int strappedBefore = StrappedGrenade.Count;
                        int live = 0;
                        if (holding && drawnGrenade && body != null)
                        {
                            yield return DomTo(body.worldCenterOfMass - flat * 0.05f, Quaternion.LookRotation(flat, Vector3.up));
                            yield return new WaitForSecondsRealtime(0.2f);
                            live = sys.Throwables.TestLetGoLive(grenade);
                            yield return new WaitForSecondsRealtime(0.2f);
                        }
                        bool strapped = StrappedGrenade.Count > strappedBefore;
                        string strapLook = StrappedGrenade.LastNote(); // (where it sits on the body, and its countdown)
                        // Not down as a ragdoll, nothing was tried: say why (a run's "0 live grenade(s)" had no grenade let go at all).
                        string notStrapped = body != null ? $"{live} live grenade(s) left the hand"
                            : BodyGrab.HoldsNeck(off) ? "held by the neck, standing: nothing let go" : "he was not down as a ragdoll: nothing let go";
                        yield return Snap("18-grenade-strapped", body != null ? body.position : front);
                        // Thrown away with the free hand.
                        yield return Swing((flat + Vector3.up * 0.7f).normalized * 9f, null);
                        Input.VRInput.SimulatedGrab[os] = false;
                        DomAt(hip, hipRotation);
                        yield return new WaitForSecondsRealtime(0.4f);
                        yield return Snap("19-strapped-body-thrown", body != null ? body.position : front);
                        float hp = carrier.health;
                        for (float until = Time.realtimeSinceStartup + 6f; !carrier.dead && Time.realtimeSinceStartup < until;)
                        {
                            yield return null;
                        }
                        yield return Snap("20-strapped-grenade-went-off", body != null ? body.position : front);
                        report($"grenade strap: {(holding ? $"{carrier.name} held" : "NOT HELD")}, {(drawnGrenade ? "a grenade drawn in the other hand" : "NO GRENADE DRAWN")}, "
                               + $"{(strapped ? $"strapped to the body ({strapLook})" : $"NOT STRAPPED ({notStrapped})")}, thrown; "
                               + $"{(carrier.dead ? "it went off on him (dead)" : $"NOT KILLED by it ({hp:0} -> {carrier.health:0})")}");
                        OffAt(offRaw.position + offPalmFromRaw);
                    }
                }

                // 8. A soldier held up by one hand is still hit by the other: its fist and a knife (user, 2026-10-24: "when i grab
                // enemys and then take a knife, i want to be able to stab them, collisions like kifes, fists and the other weapons
                // should still work with the enemys while grabbed").
                Actor heldUp = Next(false);
                yield return Bring(heldUp, 0.8f, false, there);
                if (!there[0])
                {
                    report($"hits on a held soldier: skipped (the soldier could not be brought: {broughtNote})");
                }
                else
                {
                    if (player.activeWeapon != null)
                    {
                        player.HolsterActiveWeapon();
                        yield return new WaitForSecondsRealtime(0.8f);
                    }
                    yield return Take(heldUp.CenterPosition(), hit);
                    bool holding = hit[0];
                    Vector3 up = headAt + flat * 0.5f - Vector3.up * 0.15f;
                    OffAt(up);
                    yield return new WaitForSecondsRealtime(0.6f);
                    Rigidbody body = heldUp.ragdoll.IsRagdoll() ? heldUp.ragdoll.MainRigidbody() : null;
                    yield return Snap("21-held-up", body != null ? body.worldCenterOfMass : up);
                    // The free hand's fist into the held body.
                    float hp = heldUp.health;
                    bool fistHit = false;
                    if (holding && body != null && dom.Role == Rig.HandRole.Free)
                    {
                        Vector3 at0 = body.worldCenterOfMass - flat * 0.45f;
                        Quaternion fist = Quaternion.LookRotation(flat, Vector3.up);
                        Input.VRInput.SimulatedGrip[ds] = 1f;
                        yield return DomTo(at0, fist);
                        yield return Strike(dom, at0, flat, 0.6f, 7f, fist, heldUp, hit);
                        fistHit = hit[0];
                        yield return Snap("22-held-soldier-punched", body.worldCenterOfMass);
                        Input.VRInput.SimulatedGrip[ds] = 0f;
                        yield return DomTo(hip, hipRotation);
                    }
                    // A knife in the free hand into the held body (one of the game's own knives when the loadout has none).
                    bool knifeHit = false, knifeTried = false;
                    string knifeNotTried = "the soldier was already dead or not held";
                    string knifeNote = "";
                    int knifeSlot = -1;
                    for (int s = 0; player.weapons != null && s < player.weapons.Length; s++)
                    {
                        if (player.weapons[s] is MeleeWeapon && !(player.weapons[s] is Airhorn)) // (the horn hits only on its trigger, as its own blast)
                        {
                            knifeSlot = s;
                        }
                    }
                    if (knifeSlot < 0 && player.weapons != null)
                    {
                        WeaponManager.WeaponEntry knifeEntry = WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null && e.prefab.GetComponent<MeleeWeapon>() != null
                                                                                                                    && e.prefab.GetComponent<Airhorn>() == null);
                        // A slot of its own: an empty one, else one that is neither the grenade's (step 7) nor the gun's.
                        knifeSlot = Array.IndexOf(player.weapons, null);
                        for (int s = player.weapons.Length - 1; s >= 0 && knifeSlot < 0; s--)
                        {
                            if (!(player.weapons[s] is ThrowableWeapon) && s != Array.IndexOf(player.weapons, drawn))
                            {
                                knifeSlot = s;
                            }
                        }
                        if (knifeEntry != null && knifeSlot >= 0)
                        {
                            Weapon replaced = player.weapons[knifeSlot];
                            player.EquipNewWeaponEntry(knifeEntry, knifeSlot, false);
                            knifeNote = $" ({knifeEntry.name}, given for the check{(replaced != null ? $" in place of {replaced.name}" : "")})";
                        }
                        else
                        {
                            knifeSlot = -1;
                        }
                    }
                    if (holding && body != null && knifeSlot >= 0 && !heldUp.dead)
                    {
                        player.SwitchWeapon(knifeSlot);
                        for (float until = Time.realtimeSinceStartup + 4f; (sys.Held == null || !(sys.Held.Weapon is MeleeWeapon)) && Time.realtimeSinceStartup < until;)
                        {
                            yield return null;
                        }
                        yield return new WaitForSecondsRealtime(0.6f);
                        if (sys.Held != null && sys.Held.Weapon is MeleeWeapon)
                        {
                            Quaternion stab = Quaternion.LookRotation(flat, Vector3.up);
                            Vector3 at0 = body.worldCenterOfMass - flat * 0.6f;
                            yield return DomTo(at0, stab);
                            yield return Snap("23-knife-ready", body.worldCenterOfMass);
                            yield return Strike(dom, at0, flat, 0.7f, 6f, stab, heldUp, hit);
                            knifeHit = hit[0];
                            knifeTried = true;
                            yield return Snap("24-held-soldier-stabbed", body.worldCenterOfMass);
                        }
                        else
                        {
                            knifeNotTried = "THE KNIFE NOT IN THE HAND";
                        }
                    }
                    yield return LetGo();
                    report($"hits on a held soldier: {(holding ? $"{heldUp.name} held up by one hand" : "NOT HELD")}; "
                           + $"{(fistHit ? "the other hand's fist hit him" : "the other hand's fist NO HIT")}; "
                           + $"{(knifeSlot < 0 ? "knife: none to be had" : knifeHit ? $"a knife{knifeNote} stabbed him" : !knifeTried ? $"knife: not tried ({knifeNotTried})" : $"the knife{knifeNote} NO HIT")}; health {hp:0} -> {Health(heldUp)}");
                }

                // 9. A gun drawn into the hand that holds a soldier (the weapon button, a respawn): the soldier is let go and the
                // hand is clean, as a held magazine is let go then. Before, the body stayed on the gun hand's joint and the soldier
                // on the hand after it let go, and that hand never grabbed or punched again (2026-10-24).
                int gunSlot = drawn != null && player.weapons != null ? Array.IndexOf(player.weapons, drawn) : -1;
                if (player.activeWeapon != null)
                {
                    player.HolsterActiveWeapon();
                    yield return new WaitForSecondsRealtime(0.8f);
                }
                Actor drawnOn = Next(false);
                if (gunSlot < 0 || dom.Role != Rig.HandRole.Free)
                {
                    report($"gun drawn into a holding hand: skipped ({(gunSlot < 0 ? "no gun to draw" : $"the gun hand is {dom.Role}")})");
                }
                else
                {
                    yield return Bring(drawnOn, 0.8f, false, there);
                    if (!there[0])
                    {
                        report($"gun drawn into a holding hand: skipped (the soldier could not be brought: {broughtNote})");
                    }
                    else
                    {
                        yield return DomTo(drawnOn.CenterPosition(), Quaternion.LookRotation(flat, Vector3.up));
                        Input.VRInput.SimulateGrabPress(dom.Side);
                        for (float until = Time.realtimeSinceStartup + 0.5f; dom.Role != Rig.HandRole.Body && Time.realtimeSinceStartup < until;)
                        {
                            yield return null;
                        }
                        bool holding = dom.Role == Rig.HandRole.Body;
                        player.SwitchWeapon(gunSlot);
                        for (float until = Time.realtimeSinceStartup + 4f; (sys.Held == null || sys.Held.Weapon != drawn) && Time.realtimeSinceStartup < until;)
                        {
                            yield return null;
                        }
                        yield return null;
                        bool letGo = !BodyGrab.IsHeld(drawnOn);
                        bool clean = !(dom.Held is Actor);
                        yield return Snap("25-gun-drawn-into-holding-hand", drawnOn.CenterPosition());
                        Input.VRInput.SimulatedGrab[ds] = false;
                        report($"gun drawn into a holding hand: {(holding ? $"{drawnOn.name} held by the gun hand" : "NOT HELD")}, {(sys.Held != null && sys.Held.Weapon == drawn ? $"{drawn.name} drawn" : "THE GUN NOT DRAWN")}; "
                               + $"{(letGo ? "the soldier let go" : "NOT LET GO")}, {(clean ? "the hand clean" : "THE SOLDIER LEFT ON THE HAND")}");
                    }
                }

                Walks:
                // 10. Walking with a soldier held (user, 2026-10-25: "they only appear when walking while having them grabbed then they
                // turn like crazy and fling away", and pulled "long"): taken, held, the move stick pushed 1.5 s forward and 1.5 s back with
                // the hand carried along as a real hand is (the scripted pose is in the world), then a second standing. Twice: at the
                // chest, the feet on the ground, and held high, the feet off it. Last: the player may end a little off the spot.
                float SpanOf(Actor a)
                {
                    float most = 0f;
                    Rigidbody[] bones = a.ragdoll.rigidbodies;
                    for (int x = 0; bones != null && x < bones.Length; x++)
                    {
                        for (int y = x + 1; y < bones.Length; y++)
                        {
                            if (bones[x] != null && bones[y] != null)
                            {
                                most = Mathf.Max(most, (bones[x].position - bones[y].position).magnitude);
                            }
                        }
                    }
                    return most;
                }
                // How far apart its most stretched joint is (the two anchor points of a ragdoll joint, m): a body pulled apart at its
                // joints, not one that unfolds to its full length (its span grows then too).
                float GapOf(Actor a)
                {
                    float most = 0f;
                    ConfigurableJoint[] joints = a.ragdoll.joints;
                    for (int k = 0; joints != null && k < joints.Length; k++)
                    {
                        ConfigurableJoint j = joints[k];
                        if (j != null && j.connectedBody != null)
                        {
                            // The physics' poses (Rigidbody), not the drawn ones: a held body is drawn extrapolated, each bone carried on by its
                            // own speed, and the drawn joints stood apart by a frame's difference of speeds.
                            Rigidbody self = j.GetComponent<Rigidbody>(), other = j.connectedBody;
                            Vector3 mine = self != null ? self.position + self.rotation * Vector3.Scale(j.anchor, j.transform.lossyScale) : j.transform.TransformPoint(j.anchor);
                            Vector3 theirs = other.position + other.rotation * Vector3.Scale(j.connectedAnchor, other.transform.lossyScale);
                            most = Mathf.Max(most, (mine - theirs).magnitude);
                        }
                    }
                    return most;
                }
                // run: sprinting; turn: degrees a second of smooth turning instead of walking (one way, then back).
                IEnumerator WalkWith(string how, float up, bool run = false, float turn = 0f)
                {
                    Actor walked = Next(false);
                    yield return Bring(walked, 0.8f, false, there);
                    if (!there[0] || off.Role != Rig.HandRole.Free)
                    {
                        report($"walking with a soldier held {how}: skipped ({(!there[0] ? $"the soldier could not be brought: {broughtNote}" : $"the free hand is {off.Role}")})");
                        yield break;
                    }
                    BodyImpacts.RecordContacts = true; // (before the grab: its bones get the recorder as they are armed)
                    yield return Snap("25b-before-taking-" + how.Replace(", ", "-").Replace(" ", "-"), walked.CenterPosition());
                    BodyGrab.WhyNot = "no press seen";
                    yield return Take(walked.CenterPosition(), hit);
                    bool tookWalked = hit[0];
                    float walkFastest = 0f, walkRise = 0f, walkRest = 0f, walkLongest = 0f, walkedMetres = 0f, walkGap = 0f, walkSpin = 0f, walkLag = 0f;
                    string walkNote = "", spinNote = "";
                    Quaternion lastTurn = Quaternion.identity;
                    bool hadTurn = false;
                    int turnedFrames = 0;
                    int carriesFrom = BodyGrab.Carries;
                    bool heldWalking = false;
                    Vector3? walkStart = null;
                    if (tookWalked)
                    {
                        yield return OffTo(rig.Head.position + flat * 0.3f + Vector3.up * up);
                        yield return new WaitForSecondsRealtime(0.4f);
                        Transform root = rig.Root;
                        Vector3 palmInRig = root.InverseTransformPoint(off.PalmCentre);
                        Quaternion turnInRig = Quaternion.Inverse(root.rotation) * offRaw.rotation;
                        walkRest = walkLongest = SpanOf(walked);
                        BodyImpacts.HardestImpulse = 0f; // the walk's contacts, not the grab's
                        BodyImpacts.HardestContact = "none";
                        AnchorProbe.MostJerk = 0f;
                        Vector3 walkFrom = player.transform.position;
                        walkStart = walkFrom;
                        Rigidbody lagBone = BodyGrab.HeldBone(off);
                        float restOff = lagBone != null ? (lagBone.worldCenterOfMass - off.PalmCentre).magnitude : 0f;
                        float heightFrom = walked.CenterPosition().y - player.transform.position.y; // (against the player's feet: the ground may slope)
                        int frame = 0;
                        try
                        {
                            for (float start = Time.realtimeSinceStartup; Time.realtimeSinceStartup < start + 4f && walked.ragdoll.rigidbodies != null;)
                            {
                                float t = Time.realtimeSinceStartup - start;
                                float way = t < 1.5f ? 1f : t < 3f ? -1f : 0f;
                                if (turn > 0f)
                                {
                                    rig.AddYaw(way * turn * Mathf.Min(Time.unscaledDeltaTime, 0.1f)); // as the smooth turn does it, once a frame
                                }
                                else
                                {
                                    Input.VRInput.SimulatedMove = new Vector2(0f, way);
                                }
                                Input.VRGameInput.TestSprint = run && way != 0f; AnchorProbe.Steady = t > 0.3f && t < 1.3f || t > 1.8f && t < 2.8f; // (the walk's steady stretches)
                                OffAt(root.TransformPoint(palmInRig), root.rotation * turnInRig);
                                yield return null;
                                frame++;
                                foreach (Rigidbody rb in walked.ragdoll.rigidbodies)
                                {
                                    float s = rb != null ? rb.velocity.magnitude : 0f;
                                    if (s > walkFastest)
                                    {
                                        walkFastest = s;
                                        walkNote = $"{rb.name}, frame {frame}";
                                    }
                                }
                                walkRise = Mathf.Max(walkRise, walked.CenterPosition().y - player.transform.position.y - heightFrom);
                                if (lagBone != null && BodyGrab.HeldBone(off) == lagBone)
                                {
                                    walkLag = Mathf.Max(walkLag, Mathf.Abs((lagBone.worldCenterOfMass - off.PalmCentre).magnitude - restOff));
                                }
                                walkLongest = Mathf.Max(walkLongest, SpanOf(walked));
                                walkGap = Mathf.Max(walkGap, GapOf(walked));
                                // How fast the body is seen to turn: its main bone's orientation from one frame to the next (the physics' angular
                                // velocity of a small bone read after a step ran to millions of deg/s with the bone turning far less).
                                Rigidbody main = walked.ragdoll.MainRigidbody();
                                if (main != null)
                                {
                                    if (hadTurn && Time.unscaledDeltaTime > 1e-4f)
                                    {
                                        float seen = Quaternion.Angle(lastTurn, main.rotation) / Time.unscaledDeltaTime;
                                        if (seen > walkSpin)
                                        {
                                            walkSpin = seen;
                                            Rigidbody heldBy = BodyGrab.HeldBone(off);
                                            spinNote = $"{main.name}{(heldBy == main ? ", the held bone" : heldBy != null ? $", held by {heldBy.name}" : "")} at {t:0.00} s, "
                                                       + $"its spin {main.angularVelocity.magnitude * Mathf.Rad2Deg:0} deg/s, {(walked.ragdoll.IsRagdoll() ? "a ragdoll" : "NOT A RAGDOLL")}, "
                                                       + $"{BodyGrab.Carries - carriesFrom} carries so far";
                                        }
                                        turnedFrames += seen > 720f ? 1 : 0;
                                    }
                                    lastTurn = main.rotation;
                                    hadTurn = true;
                                }
                                walkedMetres = Mathf.Max(walkedMetres, Vector3.ProjectOnPlane(player.transform.position - walkFrom, Vector3.up).magnitude);
                            }
                        }
                        finally
                        {
                            Input.VRInput.SimulatedMove = Vector2.zero;
                            Input.VRGameInput.TestSprint = false;
                        }
                        heldWalking = BodyGrab.IsHeld(walked);
                        BodyImpacts.RecordContacts = false;
                    }
                    yield return Snap("26-walking-with-a-soldier-" + how.Replace(", ", "-").Replace(" ", "-"), walked.CenterPosition());
                    yield return LetGo();
                    // The player back where the walk began: the spots the next steps bring their soldiers to are put from where he
                    // stood at the start, and up to a metre off them the next soldier stood out of the scripted hand's reach ("NOT
                    // TAKEN ... 1.66 m from the palm", 2026-10-25).
                    if (walkStart.HasValue)
                    {
                        yield return new WaitForSecondsRealtime(0.3f); // (the let-go body clear of the hand first)
                        player.SetPositionAndRotation(walkStart.Value, player.transform.rotation);
                        yield return new WaitForSecondsRealtime(0.3f);
                    }
                    Metric($"walking with a soldier held {how}: fastest bone m/s", walkFastest);
                    Metric($"walking with a soldier held {how}: longest span / at rest", walkRest > 0f ? walkLongest / walkRest : 0f);
                    bool walkStretched = walkGap > 0.1f; // (a joint 10 cm apart)
                    Metric($"walking with a soldier held {how}: widest joint gap, cm", walkGap * 100f);
                    Metric($"walking with a soldier held {how}: the held bone off its place against the hand, cm", walkLag * 100f);
                    Metric($"walking with a soldier held {how}: the body's fastest seen turn, deg/s", walkSpin);
                    report($"walking with a soldier held {how} ({(turn > 0f ? $"turning {turn:0} deg/s one way 1.5 s, then back" : "1.5 s forward, 1.5 s back")}): {(tookWalked ? $"{walked.name} taken" : $"NOT TAKEN ({BodyGrab.WhyNot}; {(walked.CenterPosition() - off.PalmCentre).magnitude:0.00} m from the palm)")}, the player went {walkedMetres:0.0} m"
                           + $"{(tookWalked && turn <= 0f && walkedMetres < 1f ? " (NOT WALKED: the stick moved nothing)" : "")}, fastest bone {walkFastest:0.0} m/s ({walkNote}), the body turned at most {walkSpin:0} deg/s ({spinNote}; {turnedFrames} frames over 720 deg/s), "
                           + $"the body rose {walkRise * 100f:0} cm, its span {walkRest:0.00} -> {walkLongest:0.00} m, its widest joint gap {walkGap * 100f:0} cm{(walkStretched ? " (NOT WHOLE: pulled apart at the joints)" : "")}"
                           + $", the held bone at most {walkLag * 100f:0} cm off its place against the hand (the hand's lead and the body's sag)"
                           + $", hardest contact: {BodyImpacts.HardestContact}, while walking steadily the hand's anchor at most {AnchorProbe.MostJerk:0.00} m/s off its last step"
                           + $"{(walkFastest > 10f || walkRise > 0.5f ? " (NOT STILL: the body went wild)" : "")}{(tookWalked && !heldWalking ? ", NOT HELD ON" : "")}");
                }
                // A punch with the fist squeezed by the grip (the Grab press, as a controller's grip button gives it) away from the soldier,
                // then swung into him at 3 m/s: a punch, not a hold (user, 2026-10-25: "punching enemies doesn't work"; the fists above
                // close by the grip's pull alone, which never takes hold).
                // The gun put away for the squeezed fists: drawn (the gun-drawn step leaves it in the gun hand), the off hand's squeeze
                // took it instead, the off hand stayed a weapon hand and every step after was skipped (final test, 2026-10-27).
                int punchGunSlot = player.activeWeapon != null && player.weapons != null ? Array.IndexOf(player.weapons, player.activeWeapon) : -1;
                if (player.activeWeapon != null)
                {
                    player.HolsterActiveWeapon();
                    yield return new WaitForSecondsRealtime(0.8f);
                }
                Actor punchedAt = Next(false);
                // 1.2 m ahead: brought at 0.8 m, "90 cm before his middle" put the fist 10 cm behind the player's own middle.
                yield return Bring(punchedAt, 1.2f, false, there);
                if (!there[0] || off.Role != Rig.HandRole.Free)
                {
                    report($"punch with a squeezed fist: skipped ({(!there[0] ? $"the soldier could not be brought: {broughtNote}" : $"the free hand is {off.Role}")})");
                }
                else
                {
                    // (90 cm: a soldier aiming at the player holds his arms and rifle out, and a squeeze within reach of them takes hold.)
                    Vector3 punchFrom = punchedAt.CenterPosition() - flat * 0.9f;
                    OffAt(punchFrom, Quaternion.LookRotation(flat, Vector3.up));
                    yield return null;
                    yield return null;
                    Input.VRInput.SimulateGrabPress(off.Side); // squeezed 90 cm before his middle
                    Input.VRInput.SimulatedGrip[os] = 1f; // (a grip button pressed is pulled all the way too: the fist)
                    yield return new WaitForSecondsRealtime(0.1f);
                    yield return Strike(off, punchFrom, flat, 1.1f, 3f, Quaternion.LookRotation(flat, Vector3.up), punchedAt, hit);
                    bool punchHurt = hit[0];
                    yield return new WaitForSecondsRealtime(0.2f);
                    bool punchTook = BodyGrab.IsHeld(punchedAt);
                    Input.VRInput.SimulatedGrip[os] = 0f;
                    yield return LetGo();
                    report($"punch with a squeezed fist (squeezed 90 cm before his middle, 3 m/s): {(punchHurt ? "hit him" : "NO HIT")}, {(punchTook ? "NOT A PUNCH: took hold of him instead" : "not taken hold of")}"
                           + (punchHurt ? "" : $" [last fist frame: {MeleeHandler.FistNote}; last sweep: {MeleeHandler.CastNote}; {(punchedAt.CenterPosition() - punchFrom).magnitude:0.00} m from the start]"));
                }
                // And squeezed 45 cm before his middle, then thrown at him (3.5 m/s) and slowing on him (1 m/s): a punch that slows on
                // its target is no reach for him (it took hold of him so: user, 2026-10-25).
                Actor pushedAt = Next(false);
                yield return Bring(pushedAt, 0.8f, false, there);
                if (there[0] && off.Role == Rig.HandRole.Free)
                {
                    // Squeezed where none of him is in the hand's reach (18 cm) but some is near (35 cm): a squeeze in reach of his arm,
                    // held out with his rifle, rightly takes hold at once. Searched back from 45 cm along the line to him.
                    // (No further than 75 cm: he stands 80 cm ahead, and past that the fist was in or behind the player himself.)
                    float pushBack = 0.45f;
                    for (float back = 0.45f; back <= 0.75f; back += 0.05f)
                    {
                        pushBack = back;
                        if (!HisWithin(pushedAt, pushedAt.CenterPosition() - flat * back, 0.2f))
                        {
                            break;
                        }
                    }
                    Vector3 pushFrom = pushedAt.CenterPosition() - flat * pushBack;
                    OffAt(pushFrom, Quaternion.LookRotation(flat, Vector3.up));
                    yield return null;
                    yield return null;
                    Input.VRInput.SimulateGrabPress(off.Side);
                    Input.VRInput.SimulatedGrip[os] = 1f;
                    // (Squeezed at rest, then thrown: pressed in the frame the throw began, the squeeze was made at 3.5 m/s and read as a
                    // reach slowing into him, which takes hold.)
                    yield return new WaitForSecondsRealtime(0.1f);
                    for (float d = 0f; d < pushBack + 0.05f;)
                    {
                        float v = d < pushBack - 0.2f ? 3.5f : 1f;
                        d += v * Mathf.Max(Time.unscaledDeltaTime, 0.011f);
                        OffAt(pushFrom + flat * d, Quaternion.LookRotation(flat, Vector3.up), flat * v);
                        yield return null;
                    }
                    yield return new WaitForSecondsRealtime(0.2f);
                    bool pushTook = BodyGrab.IsHeld(pushedAt);
                    Input.VRInput.SimulatedGrip[os] = 0f;
                    yield return LetGo();
                    report($"squeezed fist slowing on him (squeezed {pushBack * 100f:0} cm before his middle, out of reach of him, 3.5 then 1 m/s): {(pushTook ? "NOT A PUNCH: took hold of him" : "not taken hold of")}");
                }
                else
                {
                    report($"squeezed fist slowing on him: skipped ({(!there[0] ? $"the soldier could not be brought: {broughtNote}" : $"the free hand is {off.Role}")})");
                }
                // And the other way round: a quick reach squeezed on the way, at 3 m/s, slowing to him (a reach for a soldier: it takes
                // hold; spent by speed alone, it grabbed the air: "my hand grabs the air many times", user, 2026-10-25).
                Actor reachedFor = Next(false);
                yield return Bring(reachedFor, 0.8f, false, there);
                if (there[0] && off.Role == Rig.HandRole.Free)
                {
                    Vector3 reachFrom = reachedFor.CenterPosition() - flat * 0.55f;
                    OffAt(reachFrom, Quaternion.LookRotation(flat, Vector3.up));
                    yield return null;
                    yield return null;
                    bool pressed = false;
                    BodyGrab.WhyNot = "no take tried";
                    float reachHp = reachedFor.health;
                    for (float d = 0f; d < 0.5f;)
                    {
                        float v = d < 0.2f ? 3f : Mathf.Lerp(3f, 0.4f, (d - 0.2f) / 0.3f);
                        d += v * Mathf.Max(Time.unscaledDeltaTime, 0.011f);
                        OffAt(reachFrom + flat * d, Quaternion.LookRotation(flat, Vector3.up), flat * v);
                        if (!pressed && d >= 0.15f)
                        {
                            pressed = true;
                            Input.VRInput.SimulateGrabPress(off.Side); // squeezed on the way, 40 cm before his middle, at 3 m/s
                            Input.VRInput.SimulatedGrip[os] = 1f;
                        }
                        yield return null;
                    }
                    yield return new WaitForSecondsRealtime(0.3f);
                    bool reachTook = BodyGrab.IsHeld(reachedFor);
                    Input.VRInput.SimulatedGrip[os] = 0f;
                    yield return LetGo();
                    report($"quick reach squeezed on the way (3 m/s, slowing to him): {(reachTook ? "took hold of him" : "NOT TAKEN: the reach grabbed the air")}"
                           + (reachTook ? "" : $" [{BodyGrab.WhyNot}; {(reachedFor.health < reachHp - 0.5f ? "a blow landed on the way" : "no blow")}, "
                                               + $"{Time.unscaledTime - MeleeHandler.LastHandBlow[os]:0.00} s since the hand's last blow; {(reachedFor.CenterPosition() - off.PalmCentre).magnitude:0.00} m from the palm]"));
                }
                else
                {
                    report($"quick reach squeezed on the way: skipped ({(!there[0] ? $"the soldier could not be brought: {broughtNote}" : $"the free hand is {off.Role}")})");
                }

                // The gun back in the gun hand for the walks (carried as a player carries it) and the gun's sweep through a soldier.
                if (punchGunSlot >= 0 && sys.Held == null)
                {
                    player.SwitchWeapon(punchGunSlot);
                    for (float until = Time.realtimeSinceStartup + 4f; sys.Held == null && Time.realtimeSinceStartup < until;)
                    {
                        yield return null;
                    }
                    GunToHip();
                }
                yield return WalkWith("at the chest", -0.3f);
                yield return WalkWith("at the chest, running", -0.3f, run: true);
                yield return WalkWith("at the chest, turning", -0.3f, turn: VRConfig.SmoothTurnSpeed.Value);

                // 11. Collisions (user, 2026-10-25: "the enemies/friendlies don't have collisions with my gun or hand", "guns go
                // through them"): the gun swept through a soldier the other hand holds up, and a closed fist (the grip pulled, no
                // grab) swept through a body on the ground. Each: did it meet him, how far he was pushed, nothing flown off.
                float FastestBone(Actor a)
                {
                    float most = 0f;
                    foreach (Rigidbody rb in a.ragdoll.rigidbodies)
                    {
                        most = rb != null ? Mathf.Max(most, rb.velocity.magnitude) : most;
                    }
                    return most;
                }
                Actor gunPushed = Next(false);
                yield return Bring(gunPushed, 0.8f, false, there);
                if (!there[0] || off.Role != Rig.HandRole.Free || sys.Held == null)
                {
                    report($"gun through a held soldier: skipped ({(!there[0] ? $"the soldier could not be brought: {broughtNote}" : sys.Held == null ? "no gun in the hand" : $"the free hand is {off.Role}")})");
                }
                else
                {
                    BodyImpacts.RecordContacts = true; // (before the grab: its bones get the recorder as they are armed)
                    yield return Take(gunPushed.CenterPosition(), hit);
                    bool heldForGun = hit[0];
                    yield return OffTo(rig.Head.position + flat * 0.45f - Vector3.up * 0.3f);
                    yield return new WaitForSecondsRealtime(0.5f);
                    BodyImpacts.HardestImpulse = 0f;
                    BodyImpacts.HardestContact = "none";
                    Rigidbody chestBone = gunPushed.ragdoll.MainRigidbody();
                    Vector3 sweepFrom = gunPushed.CenterPosition() - sideAxis * 0.5f, chestFrom = chestBone != null ? chestBone.position : Vector3.zero;
                    float gunFastest = 0f, gunMoved = 0f;
                    for (float d = 0f; d < 1f && heldForGun;)
                    {
                        d += 1.5f * Mathf.Max(Time.unscaledDeltaTime, 0.011f);
                        DomAt(sweepFrom + sideAxis * d, Quaternion.LookRotation(flat, Vector3.up), sideAxis * 1.5f);
                        yield return null;
                        gunFastest = Mathf.Max(gunFastest, FastestBone(gunPushed));
                        gunMoved = chestBone != null ? Mathf.Max(gunMoved, (chestBone.position - chestFrom).magnitude) : gunMoved;
                    }
                    string gunContact = BodyImpacts.HardestContact;
                    BodyImpacts.RecordContacts = false;
                    GunToHip();
                    yield return Snap("27-gun-swept-through-a-held-soldier", gunPushed.CenterPosition());
                    report($"gun through a held soldier (swept 1 m at 1.5 m/s across his middle): {(heldForGun ? $"{gunPushed.name} held" : "NOT HELD")}, "
                           + $"{(gunContact.Contains("Gun Collider") ? "the gun met him" : "THE GUN WENT THROUGH HIM")} (hardest contact: {gunContact}), "
                           + $"his chest pushed {gunMoved * 100f:0} cm, fastest bone {gunFastest:0.0} m/s{(gunFastest > 10f ? " (NOT STILL: the body went wild)" : "")}");
                    yield return LetGo();
                }
                Actor fistPushed = Next(false);
                yield return Bring(fistPushed, 0.8f, false, there);
                if (!there[0] || off.Role != Rig.HandRole.Free)
                {
                    report($"fist through a body on the ground: skipped ({(!there[0] ? $"the soldier could not be brought: {broughtNote}" : $"the free hand is {off.Role}")})");
                }
                else
                {
                    // A body: knocked down, let go, killed where he lies (a living one stands up again soon, at once under a
                    // mutator that stops knockdowns).
                    yield return Take(fistPushed.CenterPosition(), hit);
                    yield return LetGo();
                    if (!fistPushed.dead)
                    {
                        fistPushed.Kill(new DamageInfo(DamageInfo.DamageSourceType.Unknown, null, null) { healthDamage = fistPushed.health + 100f });
                    }
                    yield return new WaitForSecondsRealtime(1.5f);
                    Rigidbody lyingBone = fistPushed.ragdoll.MainRigidbody();
                    bool lying = fistPushed.ragdoll.IsRagdoll() && lyingBone != null;
                    float fistFastest = 0f, fistMoved = 0f;
                    if (lying)
                    {
                        BodyImpacts.RecordContacts = true;
                        Vector3 torso = lyingBone.worldCenterOfMass, lyingFrom = lyingBone.position;
                        Vector3 fistFrom = torso - sideAxis * 0.4f + Vector3.up * 0.05f;
                        Input.VRInput.SimulatedGrip[os] = 1f; // a fist, no grab press
                        for (float d = 0f; d < 0.8f;)
                        {
                            d += 2f * Mathf.Max(Time.unscaledDeltaTime, 0.011f);
                            OffAt(fistFrom + sideAxis * d, Quaternion.LookRotation(sideAxis, Vector3.up), sideAxis * 2f);
                            yield return null;
                            fistFastest = Mathf.Max(fistFastest, FastestBone(fistPushed));
                            fistMoved = Mathf.Max(fistMoved, (lyingBone.position - lyingFrom).magnitude);
                        }
                        Input.VRInput.SimulatedGrip[os] = 0f;
                        BodyImpacts.RecordContacts = false;
                    }
                    yield return Snap("28-fist-swept-through-a-body", fistPushed.CenterPosition());
                    report($"fist through a body on the ground (swept 0.8 m at 2 m/s through his hips): {(lying ? $"{fistPushed.name} down" : "NOT DOWN as a ragdoll")}, "
                           + $"his hips pushed {fistMoved * 100f:0} cm{(lying && fistMoved < 0.03f ? " (NOT PUSHED: the fist went through him)" : "")}, fastest bone {fistFastest:0.0} m/s"
                           + $"{(fistFastest > 10f ? " (NOT STILL: the body went wild)" : "")}");
                    OffAt(offRaw.position + offPalmFromRaw);
                }

                // 12. The player's own side (user, 2026-10-27: "things are working on enemies more than on friendlies, check that it
                // works on everyone"): a friend taken hold of, punched, and taken by the neck from behind, as the enemies above.
                var friends = new List<Actor>();
                foreach (Actor a in ActorManager.instance.actors)
                {
                    if (a != null && a != player && a.aiControlled && a.team == player.team && a.ragdoll != null && a.controller != null && !a.dead)
                    {
                        friends.Add(a);
                    }
                }
                friends.Sort((x, y) => (x.CenterPosition() - player.transform.position).sqrMagnitude.CompareTo((y.CenterPosition() - player.transform.position).sqrMagnitude));
                if (friends.Count == 0)
                {
                    report("friends: skipped (no bot on the player's side)");
                }
                else if (off.Role != Rig.HandRole.Free)
                {
                    report($"friends: skipped (the free hand is {off.Role})");
                }
                else
                {
                    Actor FriendAt(int k) => friends[k % friends.Count];
                    Actor taken = FriendAt(0);
                    yield return Bring(taken, 0.8f, false, there);
                    string takenNote = "the soldier could not be brought: " + broughtNote;
                    bool friendTaken = false;
                    if (there[0])
                    {
                        yield return Take(taken.CenterPosition(), hit);
                        friendTaken = hit[0];
                        takenNote = friendTaken ? $"{taken.name} taken" : $"NOT TAKEN ({BodyGrab.WhyNot})";
                        yield return LetGo();
                    }
                    Actor punched = FriendAt(1);
                    string punchNote = "skipped";
                    yield return Bring(punched, 0.8f, false, there);
                    if (there[0])
                    {
                        float hp = punched.health;
                        yield return Punch(off, punched, Vector3.zero, hit);
                        Input.VRInput.SimulatedGrip[os] = 0f;
                        punchNote = hit[0] ? $"punched {punched.name}, health {hp:0} -> {Health(punched)}" : "NO HIT on the friend at 7 m/s";
                        OffAt(offRaw.position + offPalmFromRaw);
                    }
                    Actor necked = FriendAt(2);
                    string neckNote = "skipped";
                    yield return Bring(necked, 0.6f, true, there);
                    if (there[0])
                    {
                        Vector3 head = necked.CenterPosition() + Vector3.up * 0.45f;
                        try
                        {
                            Transform h = necked.ragdoll.HumanBoneTransformAnimated(HumanBodyBones.Head);
                            head = h != null ? h.position : head;
                        }
                        catch (Exception)
                        {
                            // a rig without the head mapped: above the centre
                        }
                        Quaternion grip = Quaternion.LookRotation(flat, Vector3.up);
                        OffAt(head, grip);
                        yield return null;
                        Input.VRInput.SimulateGrabPress(off.Side);
                        yield return new WaitForSecondsRealtime(0.4f);
                        bool friendNeck = BodyGrab.HoldsNeck(off) && !necked.ragdoll.IsRagdoll();
                        for (int i = 1; i <= 8; i++)
                        {
                            OffAt(head, Quaternion.AngleAxis(-12f * i, flat) * grip);
                            yield return null;
                        }
                        yield return new WaitForSecondsRealtime(0.3f);
                        neckNote = $"{(friendNeck ? "held by the neck" : "NOT HELD BY THE NECK")}, {(necked.dead ? "the turn broke it (dead)" : "NOT KILLED by the turn")}";
                        yield return LetGo();
                    }
                    report($"friends ({friends.Count} on the player's side): grab: {takenNote}; fist: {punchNote}; neck ({necked.name}): {neckNote}");
                }

                // 13. A soldier's gun (Tasks 346): a hand swung through it knocks it aside, a squeeze under its middle pulls its magazine
                // (he must reload), a squeeze toward its end wrests it from him (a second soldier: the first one's gun is empty).
                // A soldier whose gun has rounds in a magazine to pull (not a blade, a grenade, an empty or single-round gun).
                bool MagazineGun(Actor a)
                {
                    Weapon w = a != null ? a.activeWeapon : null;
                    return w != null && w.configuration != null && w.configuration.ammo > 1 && w.ammo > 0 && !(w is MeleeWeapon) && !(w is ThrowableWeapon);
                }
                Actor gunman = Next(true);
                for (int tries = 0; tries < 4 && gunman != null && !MagazineGun(gunman); tries++)
                {
                    gunman = Next(true);
                }
                yield return Bring(gunman, 0.9f, false, there);
                Weapon hisGun = gunman != null ? gunman.activeWeapon : null;
                if (!there[0] || off.Role != Rig.HandRole.Free || hisGun == null)
                {
                    report($"soldier's gun: skipped ({(!there[0] ? $"the soldier could not be brought: {broughtNote}" : hisGun == null ? "he has no gun out" : $"the free hand is {off.Role}")})");
                }
                else
                {
                    string knockNote = "no point on his gun";
                    if (SoldierGuns.GunPoint(gunman, false, out Vector3 swingAt))
                    {
                        bool knocked = false;
                        Vector3 swingFrom = swingAt - sideAxis * 0.35f;
                        for (float d = 0f; d < 0.7f && !knocked;)
                        {
                            d += 3f * Mathf.Max(Time.unscaledDeltaTime, 0.011f);
                            OffAt(swingFrom + sideAxis * d, Quaternion.LookRotation(flat, Vector3.up), sideAxis * 3f);
                            yield return null;
                            knocked = SoldierGuns.Knocked(gunman, out _, out _);
                        }
                        knockNote = knocked ? "knocked aside" : "NOT KNOCKED ASIDE by a 3 m/s swing through it";
                        OffAt(offRaw.position + offPalmFromRaw);
                        yield return new WaitForSecondsRealtime(0.9f); // (the knock eased back)
                    }
                    string magNote = "no point under it";
                    int roundsBefore = hisGun != null ? hisGun.ammo : 0;
                    if (hisGun != null && SoldierGuns.GunPoint(gunman, true, out Vector3 magAt))
                    {
                        OffAt(magAt, Quaternion.LookRotation(flat, Vector3.up));
                        yield return null;
                        yield return null;
                        Input.VRInput.SimulateGrabPress(off.Side);
                        yield return new WaitForSecondsRealtime(0.25f);
                        bool emptied = roundsBefore > 0 && hisGun != null && hisGun.ammo == 0;
                        bool inHand = off.Held is MagazineProp;
                        // (A tube, a break action or a launcher has no magazine to pull, nor a gun whose kind is not analysed yet: the squeeze
                        // wrests the gun instead, as meant. A magazine pulled always comes into the hand now.)
                        bool wrestedInstead = hisGun == null || gunman.activeWeapon != hisGun;
                        magNote = emptied ? $"magazine pulled ({roundsBefore} rounds), {(inHand ? "in the hand" : "NO MAGAZINE IN THE HAND")}"
                            : wrestedInstead ? "his gun has no magazine to pull (or its kind not analysed yet): wrested instead"
                            : $"NO MAGAZINE PULLED ({(BodyGrab.IsHeld(gunman) ? "took hold of him instead" : "nothing taken")})";
                        yield return LetGo();
                        if (BodyGrab.IsHeld(gunman))
                        {
                            yield return new WaitForSecondsRealtime(0.2f);
                        }
                    }
                    // A soldier with another weapon to draw (one with none only has his gun knocked aside: the game's AI needs one).
                    Actor disarmed = Next(true);
                    for (int tries = 0; tries < 4 && disarmed != null && !HasOtherWeapon(disarmed); tries++)
                    {
                        disarmed = Next(true);
                    }
                    bool canWrest = disarmed != null && HasOtherWeapon(disarmed);
                    string wrestNote = "skipped";
                    yield return Bring(disarmed, 0.9f, false, there);
                    Weapon taken = disarmed != null ? disarmed.activeWeapon : null;
                    if (there[0] && taken != null && off.Role == Rig.HandRole.Free && SoldierGuns.GunPoint(disarmed, false, out Vector3 wrestAt))
                    {
                        OffAt(wrestAt, Quaternion.LookRotation(flat, Vector3.up));
                        yield return null;
                        yield return null;
                        Input.VRInput.SimulateGrabPress(off.Side);
                        yield return new WaitForSecondsRealtime(0.4f);
                        bool gone = taken == null || disarmed.activeWeapon != taken;
                        bool knocked = SoldierGuns.Knocked(disarmed, out _, out _);
                        wrestNote = gone ? $"wrested from {disarmed.name}, who drew his {(disarmed.activeWeapon != null ? disarmed.activeWeapon.name : "NOTHING")}"
                            : !canWrest ? (knocked ? $"{disarmed.name} has no other weapon: his gun knocked aside instead" : $"NOT KNOCKED ASIDE ({disarmed.name} has no other weapon)")
                            : $"NOT WRESTED from {disarmed.name} ({(knocked ? "knocked aside instead" : BodyGrab.IsHeld(disarmed) ? "took hold of him instead" : "nothing")})";
                        yield return LetGo();
                    }
                    report($"soldier's gun ({gunman.name}'s {(hisGun != null ? hisGun.name : "gun")}): {knockNote}; {magNote}; {wrestNote}");
                }
            }
            finally
            {
                Input.VRInput.SimulatedMove = Vector2.zero;
                Input.VRGameInput.TestSprint = false;
                BodyImpacts.RecordContacts = false;
                MeleeHandler.RecordNotes = false;
                Input.VRInput.SimulatedGrab[os] = false;
                Input.VRInput.SimulatedGrab[ds] = false;
                Input.VRInput.SimulatedGrip[os] = 0f;
                Input.VRInput.SimulatedGrip[ds] = 0f;
                Rig.VRHand.Scripted[os] = null;
                Rig.VRHand.Scripted[ds] = null;
                Rig.VRHand.ScriptedVelocity[os] = Vector3.zero;
                Rig.VRHand.ScriptedVelocity[ds] = Vector3.zero;
                foreach (Actor a in paused)
                {
                    if (a != null)
                    {
                        if (!a.dead)
                        {
                            a.Damage(new DamageInfo(DamageInfo.DamageSourceType.Scripted, null, null) { healthDamage = 10000f, balanceDamage = 0f, point = a.transform.position, direction = Vector3.down });
                        }
                        if (a.controller != null)
                        {
                            a.controller.enabled = true;
                        }
                    }
                }
                foreach (Actor a in quiet)
                {
                    if (a != null && a.controller != null)
                    {
                        a.controller.enabled = true;
                    }
                }
            }
        }

        /// <summary>
        /// The goggles reach the eyes: switched by the gesture's own flag, the game's volume is on and the eye camera's post-processing can see it
        /// (user, 2026-11-07: "Night vision doesnt work at all"; the eyes had a layer that could only see the AO volume, and no check said so).
        /// </summary>
        private static IEnumerator GogglesCheck(Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            ToggleableItem goggles = GameManager.instance != null ? GameManager.instance.defaultNightVisionGoggles : null;
            var volume = goggles != null ? goggles.GetComponentInChildren<UnityEngine.Rendering.PostProcessing.PostProcessVolume>(true) : null;
            if (rig == null || volume == null)
            {
                report("goggles: skipped (no rig or no goggles' volume)");
                yield break;
            }
            bool was = NightVision.isEnabled;
            Input.VRGameInput.GogglesGesturePressed = true;
            yield return new WaitForSecondsRealtime(0.7f);
            bool switched = NightVision.isEnabled != was, active = volume.isActiveAndEnabled, seen = rig.EyesSee(volume);
            Input.VRGameInput.GogglesGesturePressed = true;
            yield return new WaitForSecondsRealtime(0.7f);
            string line = $"goggles: switched {switched}, the game's volume on {active} (layer {volume.gameObject.layer}), the eyes see it {seen}, back off {NightVision.isEnabled == was}";
            if (!switched || !active || !seen)
            {
                line += " (NOT IN THE EYES)";
                Problem(line);
            }
            report(line);
        }

        /// <summary>
        /// The drawn gun fires while sprinting: the game drops the trigger while sprinting unless allowFireWhileSprinting is set
        /// (user, 2026-10-19 night and 2026-10-20: "when running I cannot shoot", task 314).
        /// </summary>
        private static IEnumerator SprintFireCheck(Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            FpsActorController fps = LocalPlayer.actor != null ? LocalPlayer.actor.controller as FpsActorController : null;
            Weapon w = held != null ? held.Weapon : null;
            if (fps == null || w == null || w.ammo <= 0 || held.Rig.Data.Action == ActionType.SingleShot)
            {
                report("fire while sprinting: skipped (no loaded gun in the hand)");
                yield break;
            }
            int side = (int)Input.VRInput.GunHand;
            int before = w.ammo;
            bool sprinting = false;
            Input.VRGameInput.TestSprint = true;
            try
            {
                for (float until = Time.realtimeSinceStartup + 0.4f; Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                }
                sprinting = fps.IsSprinting();
                Input.VRInput.SimulatedFire[side] = true;
                for (float until = Time.realtimeSinceStartup + 0.5f; Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                }
            }
            finally
            {
                Input.VRInput.SimulatedFire[side] = false;
                Input.VRGameInput.TestSprint = false;
            }
            int shots = Mathf.Max(0, before - w.ammo);
            Metric("fire while sprinting: shots in half a second", shots);
            string line = $"fire while sprinting: sprinting {sprinting}, {shots} shot(s) in 0.5 s with the trigger held";
            if (sprinting && shots == 0)
            {
                line += " (DID NOT FIRE)";
                Problem(line);
            }
            report(line);
        }

        /// <summary>
        /// Still hands must not move in the world because the view shook or the player turned about the head: what they let go
        /// takes their velocity, and the rig's origin's motion stood in for it, so magazines flew off while turning and right after
        /// a shot (2026-10-20, task 315). The simulated hands hold still in the rig, so their velocity is the rig's at the hand.
        /// </summary>
        private static IEnumerator HandVelocityCheck(Action<string> report)
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig == null || rig.Mode != Rig.RigMode.OnFoot)
            {
                yield break;
            }
            Rig.VRHand hand = rig.Off;
            float Peak(float current, Rig.VRHand h) => Mathf.Max(current, h.Velocity.magnitude);
            for (int i = 0; i < 5; i++)
            {
                yield return null;
            }
            float shake = 0f, snap = 0f, smooth = 0f;
            rig.ShakeView(1f);
            for (int i = 0; i < 8; i++)
            {
                yield return null;
                shake = Peak(shake, hand);
            }
            Rig.VRRig.LastSnapFrame = Time.frameCount; // as the snap turn's input flags it
            rig.AddYaw(15f);
            for (int i = 0; i < 4; i++)
            {
                yield return null;
                snap = Peak(snap, hand);
            }
            Rig.VRRig.LastSnapFrame = Time.frameCount;
            rig.AddYaw(-15f);
            for (int i = 0; i < 4; i++)
            {
                yield return null;
            }
            // A smooth turn of about 120 degrees a second: the hand, some 0.5 m from the head, moves about 1 m/s.
            float turned = 0f;
            for (int i = 0; i < 30; i++)
            {
                float step = 120f * Mathf.Clamp(Time.unscaledDeltaTime, 0.005f, 0.03f);
                rig.AddYaw(step);
                turned += step;
                yield return null;
                if (i >= 3)
                {
                    smooth = Peak(smooth, hand);
                }
            }
            rig.AddYaw(-turned);
            Metric("still hand: m/s after a shot's view shake", shake);
            Metric("still hand: m/s after a 15 deg snap turn", snap);
            Metric("still hand: m/s in a 120 deg/s smooth turn", smooth);
            string line = $"still hand's velocity: {shake:0.00} m/s after a shot's view shake, {snap:0.00} m/s after a 15 deg snap turn, {smooth:0.00} m/s in a 120 deg/s smooth turn";
            if (shake > 0.3f || snap > 1f || smooth > 2.5f)
            {
                line += " (TOO FAST: things let go would fly off)";
                Problem(line);
            }
            report(line);
        }

        /// <summary>
        /// Weapons a fallen soldier drops, put on the ground near the player: after they settle they must stay where they
        /// lie.
        /// </summary>
        private static IEnumerator GroundDriftCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Actor actor = LocalPlayer.actor;
            if (sys == null || actor == null || sys.Drops == null)
            {
                yield break;
            }
            Transform head = Rig.VRRig.Instance.Head;
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            var drops = new List<DroppedWeapon>();
            int n = 0;
            foreach (WeaponManager.WeaponEntry e in WeaponManager.instance.allWeapons)
            {
                if (e == null || e.prefab == null || e.slot != WeaponManager.WeaponSlot.Primary)
                {
                    continue;
                }
                Vector3 above = actor.Position() + fwd * (1.4f + 0.5f * n) + Vector3.Cross(Vector3.up, fwd) * (0.6f * (n - 1)) + Vector3.up * 1.5f;
                if (!Physics.Raycast(above, Vector3.down, out RaycastHit hit, 5f, ~0, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }
                DroppedWeapon d = sys.Drops.Spawn(e, new Runtime.RigidPose(hit.point + Vector3.up * 0.25f, Quaternion.LookRotation(Vector3.Cross(Vector3.up, fwd), Vector3.up)), Vector3.one, Vector3.zero, -1, -1);
                if (d != null)
                {
                    drops.Add(d);
                    report($"dropped {e.name}: physics shape from the {(WeaponDrops.UsesRenderedShape(e) ? "rendered model" : "meshes")}, {d.Colliders.Count} pieces");
                }
                if (++n >= 3)
                {
                    break;
                }
            }
            yield return new WaitForSecondsRealtime(2f);
            var lines = new List<string>();
            foreach (DroppedWeapon d in drops)
            {
                if (d.Go != null)
                {
                    lines.Add(DriftLine(d.Entry.name, d.Go, 5f));
                }
            }
            yield return new WaitForSecondsRealtime(5f);
            for (int i = 0; i < lines.Count && i < drops.Count; i++)
            {
                lines[i] = lines[i].Replace("{to}", Fmt(drops[i].Go));
            }
            // A soldier walking through a lying weapon: a body-sized kinematic capsule on the soldiers' movement layer
            // sweeps through the first one; the weapon must stay where it lies.
            DroppedWeapon target = drops.Count > 0 && drops[0].Go != null ? drops[0] : null;
            if (target != null)
            {
                Vector3 at = target.Go.transform.position;
                Vector3 across = Vector3.Cross(Vector3.up, fwd).normalized;
                var walker = new GameObject("RavenfieldVR Test Walker") { layer = 17 };
                var capsule = walker.AddComponent<CapsuleCollider>();
                capsule.radius = 0.3f;
                capsule.height = 1.8f;
                capsule.center = Vector3.up * 0.9f;
                var walkerBody = walker.AddComponent<Rigidbody>();
                walkerBody.isKinematic = true;
                walkerBody.interpolation = RigidbodyInterpolation.Interpolate;
                Vector3 from = at - across * 1.4f - Vector3.up * 0.3f;
                walkerBody.position = from;
                walker.transform.position = from;
                Vector3 before = at;
                // A magazine on the ground on the same path.
                MagazineProp groundMag = null;
                HeldWeapon heldNow = sys.Held;
                if (heldNow != null && heldNow.ActiveAmmo != null && heldNow.ActiveAmmo.UsesDetachableMag
                    && Physics.Raycast(at + across * 0.6f + Vector3.up * 1f, Vector3.down, out RaycastHit magHit, 3f, ~0, QueryTriggerInteraction.Ignore))
                {
                    groundMag = MagazineProp.Create(heldNow.ActiveRig, PropKind.Magazine, 0);
                    if (groundMag != null && groundMag.Go != null)
                    {
                        groundMag.Go.transform.position = magHit.point + Vector3.up * 0.08f;
                        groundMag.Drop(Vector3.zero, Vector3.zero);
                        yield return new WaitForSecondsRealtime(1.5f);
                    }
                }
                Vector3 magBefore = groundMag != null && groundMag.Go != null ? groundMag.Go.transform.position : Vector3.zero;
                const float WalkTime = 1.2f;
                for (float t = 0f; t < WalkTime; t += Time.fixedDeltaTime)
                {
                    yield return new WaitForFixedUpdate();
                    walkerBody.MovePosition(from + across * (2.8f * Mathf.Clamp01(t / WalkTime)));
                }
                yield return new WaitForSecondsRealtime(0.5f);
                float moved = target.Go != null ? Vector3.Distance(target.Go.transform.position, before) : -1f;
                UnityEngine.Object.Destroy(walker);
                if (groundMag != null)
                {
                    float magMoved = groundMag.Go != null ? Vector3.Distance(groundMag.Go.transform.position, magBefore) : -1f;
                    Metric("dropped magazine: cm moved by a body walking through it", magMoved * 100f);
                    lines.Add($"a soldier walking through a magazine on the ground: {(magMoved < 0f ? "GONE (vanished in the check)" : magMoved > 0.15f ? $"PUSHED {magMoved * 100f:0} cm" : $"stayed ({magMoved * 100f:0.0} cm)")}");
                    groundMag.Destroy();
                }
                Metric("dropped weapon: cm moved by a body walking through it", moved * 100f);
                lines.Add($"a soldier walking through a lying {target.Entry.name}: {(moved < 0f ? "GONE (vanished in the check)" : moved > 0.15f ? $"PUSHED {moved * 100f:0} cm" : $"stayed ({moved * 100f:0.0} cm)")}");
            }
            // A type whose dropped shape was never fitted, as in a match started before the warm-up reached it (task 309): it is
            // not fitted on the death frame any more; it lies on a box and gets its own shape a moment later, fitted on a worker.
            WeaponManager.WeaponEntry cold = drops.Count > 0 ? drops[0].Entry : null;
            if (cold != null && WeaponDrops.ForgetFittedShape(cold)
                && Physics.Raycast(actor.Position() + fwd * 1.2f + Vector3.up * 1.5f, Vector3.down, out RaycastHit coldHit, 5f, ~0, QueryTriggerInteraction.Ignore))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                DroppedWeapon c = sys.Drops.Spawn(cold, new Runtime.RigidPose(coldHit.point + Vector3.up * 0.25f, Quaternion.LookRotation(Vector3.Cross(Vector3.up, fwd), Vector3.up)), Vector3.one, Vector3.zero, -1, -1);
                float spawnMs = (float)watch.Elapsed.TotalMilliseconds;
                if (c != null)
                {
                    bool late = c.LateFitKey != null;
                    float started = Time.realtimeSinceStartup;
                    while (c.LateFitKey != null && c.Go != null && Time.realtimeSinceStartup - started < 3f)
                    {
                        yield return null;
                    }
                    float took = Time.realtimeSinceStartup - started;
                    Metric("cold weapon drop: ms on its frame", spawnMs);
                    string line = !late
                        ? $"a drop of {cold.name} with its fitted shape forgotten: fitted at once ({spawnMs:0} ms), NOT on the late path"
                        : c.LateFitKey == null
                            ? $"a drop of {cold.name} with its fitted shape forgotten: {spawnMs:0} ms on its frame, on a box for {took:0.00} s, then its own shape ({c.Colliders.Count} pieces)"
                            : $"a drop of {cold.name} with its fitted shape forgotten: {spawnMs:0} ms on its frame, STILL ON ITS BOX after 3 s";
                    if (!late || c.LateFitKey != null)
                    {
                        Problem(line);
                    }
                    lines.Add(line);
                    if (c.Go != null)
                    {
                        UnityEngine.Object.Destroy(c.Go);
                    }
                }
            }
            foreach (string l in lines)
            {
                report(l);
            }
            foreach (DroppedWeapon d in drops)
            {
                if (d.Go != null)
                {
                    UnityEngine.Object.Destroy(d.Go);
                }
            }
        }

        /// <summary>
        /// A magazine lying in front of you is picked up, set down again where it lay without a toss, and the gun is swept
        /// through it: it must stay put. Picking it up switches its colliders off, and Unity forgets that the gun ignores
        /// it; it was once still marked "lying still, ignored" and the gun shoved it away.
        /// </summary>
        private static IEnumerator GentleMagazineCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Rig.VRRig rig = Rig.VRRig.Instance;
            HeldWeapon held = sys != null ? sys.Held : null;
            Rig.VRHand off = rig != null ? rig.Off : null, dom = rig != null ? rig.Dominant : null;
            if (held == null || held.Collider == null || held.ActiveAmmo == null || !held.ActiveAmmo.UsesDetachableMag
                || off == null || off.Held != null || off.Role != Rig.HandRole.Free)
            {
                report("magazine set down gently: skipped (no magazine-fed weapon in hand or the off hand is busy: "
                       + (off == null ? "no off hand" : $"{off.Role}, holding {(off.Held != null ? off.Held.GetType().Name : "nothing")}") + ")");
                yield break;
            }
            Transform head = rig.Head;
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (!Physics.Raycast(head.position + fwd * 0.6f, Vector3.down, out RaycastHit ground, 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                report("magazine set down gently: skipped (no ground in front)");
                yield break;
            }
            MagazineProp mag = MagazineProp.Create(held.ActiveRig, PropKind.Magazine, 0);
            if (mag == null || mag.Go == null)
            {
                yield break;
            }
            int side = (int)dom.Side, offSide = (int)off.Side;
            try
            {
                mag.Go.transform.position = ground.point + Vector3.up * 0.06f;
                mag.Drop(Vector3.zero, Vector3.zero);
                yield return new WaitForSecondsRealtime(1.5f); // it settles, and the gun ignores it from now on
                Vector3 spot = mag.Go.transform.position;
                Quaternion lie = mag.Go.transform.rotation;
                // Picked up (the grip held, so the reload logic keeps it in the hand) ...
                Input.VRInput.SimulatedGrab[offSide] = true;
                mag.Attach(off);
                yield return new WaitForSecondsRealtime(0.3f);
                // ... and set down where it lay, without a toss.
                mag.Go.transform.SetPositionAndRotation(spot, lie);
                mag.Drop(Vector3.zero, Vector3.zero);
                Input.VRInput.SimulatedGrab[offSide] = false;
                yield return new WaitForSecondsRealtime(1.2f);
                Vector3 before = mag.Go.transform.position;
                // The gun swept sideways through it, the middle of the receiver passing where it lies.
                Runtime.RigidPose start = dom.Raw;
                WeaponRigData d = held.Rig.Data;
                Vector3 handToGun = held.Rig.ToWorld(d.GripPoint + d.AimAxis * 0.15f) - start.position;
                Vector3 across = Vector3.Cross(Vector3.up, fwd);
                // Since Tasks 351 (the user, 2026-10-28: loose magazines "dont collide with" guns) the gun meets a loose magazine and pushes it,
                // at its own speed and 1 m/s more at most: what must not happen is a fling at the hand's speed. It expected the gun to pass
                // through, and was not run again until 2026-11-08 ("NOT LEFT ALONE (pushed 46 cm)" for a push it was meant to give).
                Rigidbody magBody = mag.Go.GetComponent<Rigidbody>();
                float sweepStart = Time.realtimeSinceStartup, fastest = 0f;
                for (int i = 0; i <= 40; i++)
                {
                    Vector3 gunAt = before + Vector3.up * 0.03f + across * Mathf.Lerp(-0.35f, 0.35f, i / 40f);
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(gunAt - handToGun, start.rotation);
                    yield return null;
                    fastest = magBody != null ? Mathf.Max(fastest, magBody.velocity.magnitude) : fastest;
                }
                float sweepSpeed = 0.7f / Mathf.Max(0.05f, Time.realtimeSinceStartup - sweepStart);
                for (float until = Time.realtimeSinceStartup + 0.3f; Time.realtimeSinceStartup < until;)
                {
                    yield return null;
                    fastest = magBody != null ? Mathf.Max(fastest, magBody.velocity.magnitude) : fastest;
                }
                float moved = mag.Go != null ? Vector3.Distance(mag.Go.transform.position, before) : -1f;
                Metric("magazine set down gently: cm the gun pushed it", moved * 100f);
                Metric("magazine set down gently: fastest m/s the gun gave it", fastest);
                report("magazine picked up, set down gently and the gun swept through it: "
                       + (moved < 0f ? "GONE (vanished in the check)"
                          : fastest > sweepSpeed + 1.5f ? $"NOT PUSHED GENTLY: flung at {fastest:0.0} m/s by a gun sweeping at {sweepSpeed:0.0} m/s, {moved * 100f:0} cm"
                          : moved > 0.05f ? $"pushed {moved * 100f:0} cm, at most {fastest:0.0} m/s (the gun at {sweepSpeed:0.0} m/s)" : $"stayed ({moved * 100f:0.0} cm)"));
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
                Input.VRInput.SimulatedGrab[offSide] = false;
                mag.Destroy();
            }
        }

        /// <summary>
        /// A magazine let go at 1 m/s in slow motion (the game's 0.2) flies as in real time until it touches something (RealTimeFlight):
        /// about 1 m/s across and a real-time fall in its first quarter second, not a fifth of the speed and hanging in the air (user,
        /// 2026-10-28: "the mag floats for one second in the air"); and slow motion ended in its flight, it goes on at the same speed
        /// (no five-fold jump).
        /// </summary>
        private static IEnumerator SlowMotionMagazineCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Rig.VRRig rig = Rig.VRRig.Instance;
            HeldWeapon held = sys != null ? sys.Held : null;
            if (held == null || rig == null || held.ActiveAmmo == null || !held.ActiveAmmo.UsesDetachableMag || Time.timeScale <= 0f)
            {
                report("magazine let go in slow motion: skipped (no magazine-fed weapon in hand, or paused)");
                yield break;
            }
            Transform head = rig.Head;
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            // On the free hand's side, clear of the gun: 50 cm in front of the head is where the gun is held, and since loose magazines meet
            // the gun (Tasks 351) the magazine was dropped onto it and stopped there ("0.11 m/s across ... HUNG IN THE AIR", 2026-11-08: slower
            // than even the slowed world's pace).
            Vector3 side = Vector3.Cross(Vector3.up, fwd).normalized * (rig.Off.IsLeft ? -1f : 1f);
            Vector3 start = head.position + fwd * 0.5f + side * 0.45f - Vector3.up * 0.2f;
            if (Physics.Raycast(start, fwd, 1.2f, ~0, QueryTriggerInteraction.Ignore) || !Physics.Raycast(start, Vector3.down, out RaycastHit floor, 4f, ~0, QueryTriggerInteraction.Ignore)
                || floor.distance < 0.8f)
            {
                report("magazine let go in slow motion: skipped (no clear room in front)");
                yield break;
            }
            MagazineProp mag = MagazineProp.Create(held.ActiveRig, PropKind.Magazine, 0);
            if (mag == null || mag.Go == null)
            {
                yield break;
            }
            float scaleBefore = Time.timeScale;
            try
            {
                GameManager.SetTimeScale(0.2f);
                yield return null;
                mag.Go.transform.position = start;
                mag.Drop(fwd * 1f, Vector3.zero);
                float t0 = Time.realtimeSinceStartup;
                yield return new WaitForSecondsRealtime(0.25f);
                yield return new WaitForEndOfFrame();
                float dt = Mathf.Max(1e-3f, Time.realtimeSinceStartup - t0);
                Vector3 p1 = mag.Go.transform.position;
                float across = Vector3.ProjectOnPlane(p1 - start, Vector3.up).magnitude / dt, fell = start.y - p1.y, realFall = 0.5f * 9.81f * dt * dt;
                // Slow motion ends in its flight: the same real-time speed on.
                GameManager.SetTimeScale(1f);
                yield return new WaitForSecondsRealtime(0.04f);
                yield return new WaitForEndOfFrame();
                Vector3 p2 = mag.Go.transform.position;
                float t2 = Time.realtimeSinceStartup;
                yield return new WaitForSecondsRealtime(0.08f);
                yield return new WaitForEndOfFrame();
                float after = Vector3.ProjectOnPlane(mag.Go.transform.position - p2, Vector3.up).magnitude / Mathf.Max(1e-3f, Time.realtimeSinceStartup - t2);
                Metric("magazine let go in slow motion: real-time m/s across (let go at 1)", across);
                Metric("magazine let go in slow motion: share of a real-time fall in 0.25 s", realFall > 0f ? fell / realFall : 0f);
                Metric("magazine let go in slow motion: real-time m/s across after slow motion ended", after);
                bool hung = across < 0.6f || fell < realFall * 0.5f, jumped = after > 1.6f;
                string line = $"magazine let go at 1 m/s in slow motion (0.2): {across:0.00} m/s across and fell {fell * 100f:0} cm in its first {dt:0.00} s "
                              + $"(real time: 1 m/s, {realFall * 100f:0} cm){(hung ? " (HUNG IN THE AIR: the slowed world took the hand's speed as its own)" : "")}; "
                              + $"slow motion ended in its flight: {after:0.00} m/s on{(jumped ? " (JUMPED: its speed made out for slow motion was kept)" : "")}";
                if (hung || jumped)
                {
                    Problem(line);
                }
                report(line);
            }
            finally
            {
                GameManager.SetTimeScale(scaleBefore > 0f ? scaleBefore : 1f);
                mag.Destroy();
            }
        }

        /// <summary>
        /// Magazines loaded by hand (experimental, switched on for the check only): a magazine from the pouch comes from the reserve as
        /// without the option, and with the reserve out an empty one, again and again (the user, 2026-11-03: "when i dont have any mags
        /// left that are full, I can take infinite empty mags"). The reserve is put back as it was.
        /// </summary>
        private static void HandLoadingCheck(Action<string> report)
        {
            HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
            AmmoState a = held != null ? held.Ammo : null;
            bool? before = VRConfig.TestLoadByHand;
            VRConfig.TestLoadByHand = true;
            int spare = a != null && a.Weapon != null ? a.SpareAmmo : 0;
            try
            {
                if (a == null || !HandLoading.Applies(a) || a.InfiniteSpare)
                {
                    report("magazines loaded by hand: skipped (no box-fed gun with a reserve in the hand)");
                    return;
                }
                a.SpareAmmo = a.Capacity * 2;
                int full = HandLoading.PouchMagazineRounds(a.RoundsForFreshMagazine());
                a.SpareAmmo = 0;
                int empty1 = HandLoading.PouchMagazineRounds(a.RoundsForFreshMagazine()), empty2 = HandLoading.PouchMagazineRounds(a.RoundsForFreshMagazine());
                bool fromReserve = full >= a.Capacity - 1 && full <= a.Capacity;
                bool empties = empty1 == 0 && empty2 == 0;
                string line = $"magazines loaded by hand: {(fromReserve ? $"a magazine from the reserve holds {full}" : $"THE RESERVE'S MAGAZINE HELD {full} OF {a.Capacity}")}, "
                              + $"{(empties ? "with the reserve out empty ones, again and again" : $"WITH THE RESERVE OUT NOT EMPTY ({empty1}, {empty2})")}";
                if (!fromReserve || !empties)
                {
                    Problem(line);
                }
                report(line);
            }
            finally
            {
                if (a != null && a.Weapon != null)
                {
                    a.SpareAmmo = spare;
                }
                VRConfig.TestLoadByHand = before;
            }
        }

        /// <summary>
        /// Hands in slow motion: Slowed (the user, 2026-10-28: "when its on slowed the hands etc are delayed too in slowmo, like boneworks
        /// does it"). In the game's slow motion (0.2) the free hand, moved 30 cm at once, trails the controller (under half way after a
        /// tenth of a second) and catches up (over 90% in 1.5 s); a magazine it lets go of at 1 m/s moves at the world's pace (about
        /// 0.2 m/s in real time, no real-time flight). The saved setting is left as it was.
        /// </summary>
        private static IEnumerator SlowedHandsCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Rig.VRRig rig = Rig.VRRig.Instance;
            HeldWeapon held = sys != null ? sys.Held : null;
            Rig.VRHand off = rig != null ? rig.Off : null;
            if (held == null || rig == null || off == null || off.Held != null || off.Role != Rig.HandRole.Free || Time.timeScale <= 0f)
            {
                report("slowed hands: skipped (no weapon in hand, the free hand busy, or paused)");
                yield break;
            }
            int os = (int)off.Side;
            Transform root = rig.Root;
            Vector3 flat = Vector3.ProjectOnPlane(rig.Head.forward, Vector3.up).normalized;
            Vector3 aside = Vector3.Cross(Vector3.up, flat) * (off.IsLeft ? -1f : 1f);
            Runtime.RigidPose from = Runtime.RigidPose.Of(root).Inverse().Multiply(new Runtime.RigidPose(rig.Head.position + flat * 0.35f + aside * 0.15f - Vector3.up * 0.25f,
                Quaternion.LookRotation(flat, Vector3.up)));
            Runtime.RigidPose to = new Runtime.RigidPose(from.position + root.InverseTransformDirection(aside) * 0.3f, from.rotation);
            float scaleBefore = Time.timeScale;
            SlowMotionHands? modeBefore = VRConfig.TestSlowHands;
            MagazineProp mag = null;
            try
            {
                VRConfig.TestSlowHands = SlowMotionHands.Slowed;
                GameManager.SetTimeScale(0.2f);
                Rig.VRHand.Scripted[os] = Runtime.RigidPose.Of(root).Multiply(from);
                yield return new WaitForSecondsRealtime(0.5f);
                Vector3 startLocal = root.InverseTransformPoint(off.Raw.position);
                Rig.VRHand.Scripted[os] = Runtime.RigidPose.Of(root).Multiply(to);
                yield return new WaitForSecondsRealtime(0.1f);
                float early = Vector3.Distance(root.InverseTransformPoint(off.Raw.position), startLocal) / 0.3f;
                yield return new WaitForSecondsRealtime(1.4f);
                float late = Vector3.Distance(root.InverseTransformPoint(off.Raw.position), startLocal) / 0.3f;
                Metric("slowed hands: share of a 30 cm move after 0.1 s in slow motion", early);
                Metric("slowed hands: share of a 30 cm move after 1.5 s in slow motion", late);
                bool instant = early > 0.5f, stuck = late < 0.9f;
                string line = $"slowed hands in slow motion (0.2): a 30 cm move of the controller, {early * 100f:0}% of it after 0.1 s, {late * 100f:0}% after 1.5 s"
                              + (instant ? " (NOT SLOWED: the hand went with the controller)" : "") + (stuck ? " (NEVER CAUGHT UP with the controller)" : "");
                // A swing held past the top speed (4.5 m/s for a second, back and forth along the side): the hand stays on its 1.5 m
                // leash and never jumps (it snapped to the controller at 1.5 m before; round 11).
                Vector3 sideLocal = root.InverseTransformDirection(aside);
                float worstGap = 0f, worstJump = 0f;
                Vector3 lastHand = root.InverseTransformPoint(off.Raw.position);
                for (float t0 = Time.realtimeSinceStartup, u = 0f; u < 1f; u = Time.realtimeSinceStartup - t0)
                {
                    float x = Mathf.PingPong(u * 4.5f, 1.2f); // (4.5 m/s, turning every 1.2 m)
                    Runtime.RigidPose swing = new Runtime.RigidPose(to.position + sideLocal * x, to.rotation);
                    Rig.VRHand.Scripted[os] = Runtime.RigidPose.Of(root).Multiply(swing);
                    yield return null;
                    Vector3 handNow = root.InverseTransformPoint(off.Raw.position);
                    worstGap = Mathf.Max(worstGap, Vector3.Distance(handNow, swing.position));
                    worstJump = Mathf.Max(worstJump, Vector3.Distance(handNow, lastHand));
                    lastHand = handNow;
                }
                Metric("slowed hands: farthest behind the controller in a 4.5 m/s swing (m)", worstGap);
                Metric("slowed hands: largest move in a frame in a 4.5 m/s swing (m)", worstJump);
                bool snapped = worstJump > 0.25f, unleashed = worstGap > 1.55f;
                line += $"; a 4.5 m/s swing for a second: at most {worstGap:0.00} m behind, at most {worstJump * 100f:0} cm in a frame"
                        + (snapped ? " (JUMPED: the hand snapped to the controller)" : "") + (unleashed ? " (NOT HELD within 1.5 m of the controller)" : "");
                stuck |= snapped || unleashed;
                // A magazine let go from the slowed hand at 1 m/s: the world's pace, no real-time flight.
                if (held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag)
                {
                    Vector3 start = off.Raw.position + flat * 0.1f;
                    if (!Physics.Raycast(start, flat, 1.2f, ~0, QueryTriggerInteraction.Ignore) && Physics.Raycast(start, Vector3.down, out RaycastHit floor, 4f, ~0, QueryTriggerInteraction.Ignore)
                        && floor.distance > 0.6f)
                    {
                        mag = MagazineProp.Create(held.ActiveRig, PropKind.Magazine, 0);
                        if (mag != null && mag.Go != null)
                        {
                            mag.Go.transform.position = start;
                            mag.Drop(flat * 1f, Vector3.zero);
                            float t0 = Time.realtimeSinceStartup;
                            yield return new WaitForFixedUpdate();
                            // Its own speed just after the let-go (game units): 1 is the world's pace from the drop; more is a sped-up throw, and a
                            // later rise a push (the slowed hand catching up runs into it: 0.92 m/s across on 2026-11-08, 0.18 the day before).
                            float launched = mag.Body != null ? mag.Body.velocity.magnitude : -1f;
                            yield return new WaitForSecondsRealtime(0.25f);
                            yield return new WaitForEndOfFrame();
                            float across = Vector3.ProjectOnPlane(mag.Go.transform.position - start, Vector3.up).magnitude / Mathf.Max(1e-3f, Time.realtimeSinceStartup - t0);
                            Metric("slowed hands: real-time m/s across of a magazine let go at 1", across);
                            bool realTime = across > 0.6f;
                            line += $"; a magazine let go at 1 m/s: {across:0.00} m/s across in real time (left at {launched:0.00} m/s){(realTime ? " (NOT AT THE WORLD'S PACE: the slowed hand's throw was sped up again)" : "")}";
                            instant |= realTime;
                        }
                    }
                }
                // Slow motion off with the hand still moving (1 m/s along the side): it is back on the controller within half a second
                // while moving (it trailed by 2v/20 and let the slow motion go only once the hand was held still; user, 2026-11-03).
                GameManager.SetTimeScale(1f);
                float exitGap = 0f;
                for (float t0 = Time.realtimeSinceStartup, u = 0f; u < 0.7f; u = Time.realtimeSinceStartup - t0)
                {
                    Runtime.RigidPose moving = new Runtime.RigidPose(to.position + sideLocal * u, to.rotation);
                    Rig.VRHand.Scripted[os] = Runtime.RigidPose.Of(root).Multiply(moving);
                    yield return null;
                    if (u > 0.5f)
                    {
                        exitGap = Mathf.Max(exitGap, Vector3.Distance(root.InverseTransformPoint(off.Raw.position), moving.position));
                    }
                }
                Metric("slowed hands: behind the controller 0.5 s after slow motion ends, moving at 1 m/s (m)", exitGap);
                bool lingers = exitGap > 0.02f;
                line += $"; slow motion off while moving at 1 m/s: {exitGap * 100f:0.0} cm behind after 0.5 s{(lingers ? " (STILL SLOWED: the hand had not caught up)" : "")}";
                stuck |= lingers;
                if (instant || stuck)
                {
                    Problem(line);
                }
                report(line);
            }
            finally
            {
                Rig.VRHand.Scripted[os] = null;
                VRConfig.TestSlowHands = modeBefore;
                GameManager.SetTimeScale(scaleBefore > 0f ? scaleBefore : 1f);
                mag?.Destroy();
            }
        }

        /// <summary>
        /// Running, a magazine taken into the free hand from beside it (not from the pouch: it eases in from where it was) stays with the
        /// hand through its ease: drawn from a start kept in the world it trailed behind the running player and caught up again (user,
        /// 2026-10-28: "because of walking forward the mag goes a little bit backwards but corrects its path again"). Measured in the
        /// hand's own frame against where the magazine sits once the ease is over.
        /// </summary>
        private static IEnumerator RunningMagazineEaseCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Rig.VRRig rig = Rig.VRRig.Instance;
            HeldWeapon held = sys != null ? sys.Held : null;
            Rig.VRHand off = rig != null ? rig.Off : null;
            if (held == null || rig == null || rig.Mode != Rig.RigMode.OnFoot || held.ActiveAmmo == null || !held.ActiveAmmo.UsesDetachableMag
                || off == null || off.Held != null || off.Role != Rig.HandRole.Free)
            {
                report("magazine taken while running: skipped (no magazine-fed weapon in hand, not on foot, or the free hand is busy)");
                yield break;
            }
            Actor player = LocalPlayer.actor;
            Transform root = rig.Root;
            int os = (int)off.Side;
            // The free hand out to its side, away from the gun, held still against the player while he runs.
            Vector3 flat = Vector3.ProjectOnPlane(rig.Head.forward, Vector3.up).normalized;
            Vector3 aside = Vector3.Cross(Vector3.up, flat) * (off.IsLeft ? -1f : 1f);
            Runtime.RigidPose handInRig = Runtime.RigidPose.Of(root).Inverse().Multiply(new Runtime.RigidPose(rig.Head.position + flat * 0.35f + aside * 0.3f - Vector3.up * 0.25f,
                Quaternion.LookRotation(flat, Vector3.up)));
            void HoldHand() => Rig.VRHand.Scripted[os] = Runtime.RigidPose.Of(root).Multiply(handInRig);
            Vector3 walkFrom = player != null ? player.transform.position : Vector3.zero;
            MagazineProp mag = null;
            try
            {
                HoldHand();
                Input.VRInput.SimulatedMove = new Vector2(0f, 1f);
                Input.VRGameInput.TestSprint = true;
                for (float until = Time.realtimeSinceStartup + 0.8f; Time.realtimeSinceStartup < until;)
                {
                    HoldHand();
                    yield return null;
                }
                mag = MagazineProp.Create(held.ActiveRig, PropKind.Magazine, 0);
                if (mag == null || mag.Go == null)
                {
                    yield break;
                }
                mag.NotFresh(); // (as one caught or picked up: it eases into the hand)
                // 15 cm beside the palm, in the hand's own frame.
                Vector3 offsetInHand = new Vector3(0f, 0.15f, 0f);
                mag.Go.transform.SetPositionAndRotation(off.Palm.position + off.Palm.rotation * offsetInHand, off.Palm.rotation);
                Input.VRInput.SimulatedGrab[os] = true;
                mag.Attach(off);
                float speed = 0f;
                Vector3 lastAt = player != null ? player.transform.position : Vector3.zero;
                float lastT = Time.realtimeSinceStartup;
                var rel = new List<Vector3>();
                for (float until = Time.realtimeSinceStartup + 0.3f; Time.realtimeSinceStartup < until;)
                {
                    HoldHand();
                    yield return new WaitForEndOfFrame();
                    Runtime.RigidPose palm = off.Palm;
                    rel.Add(Quaternion.Inverse(palm.rotation) * (mag.Go.transform.position - palm.position));
                    if (player != null)
                    {
                        float now = Time.realtimeSinceStartup;
                        speed = Mathf.Max(speed, Vector3.ProjectOnPlane(player.transform.position - lastAt, Vector3.up).magnitude / Mathf.Max(1e-3f, now - lastT));
                        lastAt = player.transform.position;
                        lastT = now;
                    }
                    yield return null;
                }
                Vector3 settled = rel.Count > 0 ? rel[rel.Count - 1] : Vector3.zero;
                float start = (offsetInHand - settled).magnitude, worst = 0f;
                foreach (Vector3 r in rel)
                {
                    worst = Mathf.Max(worst, (r - settled).magnitude);
                }
                float trailed = Mathf.Max(0f, worst - start);
                Metric("magazine taken while running: cm it trailed past its start in the hand", trailed * 100f);
                string line = $"magazine taken into the free hand while running ({speed:0.0} m/s): at most {worst * 100f:0} cm from its place in the hand against "
                              + $"{start * 100f:0} cm at the start{(trailed > 0.04f ? $" (TRAILED {trailed * 100f:0} cm behind the running player)" : "")}";
                if (speed < 1f)
                {
                    line += " (NOT RUN: the stick moved nothing; not measured)";
                }
                else if (trailed > 0.04f)
                {
                    Problem(line);
                }
                report(line);
            }
            finally
            {
                Input.VRInput.SimulatedMove = Vector2.zero;
                Input.VRGameInput.TestSprint = false;
                Input.VRInput.SimulatedGrab[os] = false;
                Rig.VRHand.Scripted[os] = null;
                mag?.Destroy();
                if (player != null)
                {
                    player.SetPositionAndRotation(walkFrom, player.transform.rotation); // (back where it began: the checks after it are placed from there)
                }
            }
        }

        /// <summary>
        /// A magazine let go with the hand pushed into the ground, 15 cm and 40 cm deep: it must come out on top and lie still,
        /// not be flung by PhysX pushing it out (a push over 25 cm was skipped; prop physics hunt, 2026-10-21).
        /// </summary>
        private static IEnumerator BuriedMagazineCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Rig.VRRig rig = Rig.VRRig.Instance;
            HeldWeapon held = sys != null ? sys.Held : null;
            if (held == null || rig == null || held.ActiveAmmo == null || !held.ActiveAmmo.UsesDetachableMag)
            {
                report("magazine let go in the ground: skipped (no magazine-fed weapon in hand)");
                yield break;
            }
            Transform head = rig.Head;
            Vector3 fwd = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            if (!Physics.Raycast(head.position + fwd * 0.6f, Vector3.down, out RaycastHit ground, 3f, ~0, QueryTriggerInteraction.Ignore))
            {
                report("magazine let go in the ground: skipped (no ground in front)");
                yield break;
            }
            if (ground.rigidbody != null)
            {
                // (A vehicle's hull is no ground: a magazine let go in a moving body passes through it on purpose. The weapon run of 2026-11-08 stood
                // by a tank and took its hull for the ground: "NOT LAID OUT ... ends -1152 cm above the ground".)
                report($"magazine let go in the ground: skipped (in front is {ground.collider.name}, a body, not ground)");
                yield break;
            }
            foreach (float depth in new[] { 0.15f, 0.4f })
            {
                MagazineProp mag = MagazineProp.Create(held.ActiveRig, PropKind.Magazine, 0);
                if (mag == null || mag.Go == null)
                {
                    yield break;
                }
                float top = 0f;
                Vector3 start;
                try
                {
                    mag.Go.transform.position = ground.point - Vector3.up * depth;
                    mag.Drop(Vector3.zero, Vector3.zero);
                    start = mag.Go.transform.position;
                    Vector3 last = start;
                    float until = Time.realtimeSinceStartup + 1.5f;
                    while (Time.realtimeSinceStartup < until && mag.Go != null)
                    {
                        yield return null;
                        if (mag.Go == null)
                        {
                            break;
                        }
                        Vector3 now = mag.Go.transform.position;
                        if (Time.deltaTime > 1e-4f)
                        {
                            top = Mathf.Max(top, Vector3.Distance(now, last) / Time.deltaTime);
                        }
                        last = now;
                    }
                    if (mag.Go == null)
                    {
                        report($"magazine let go {depth * 100f:0} cm in the ground: GONE");
                        continue;
                    }
                    Vector3 end = mag.Go.transform.position;
                    // Against the ground where it ends (put back toward the eyes, it lies up to 30 cm nearer, on sloping ground).
                    float groundY = Physics.Raycast(end + Vector3.up * 0.5f, Vector3.down, out RaycastHit under, 2f, ~PropPhysics.BodyMask & ~(1 << mag.Go.layer),
                                                    QueryTriggerInteraction.Ignore) ? under.point.y : ground.point.y;
                    float above = end.y - groundY;
                    float across = Vector2.Distance(new Vector2(end.x, end.z), new Vector2(ground.point.x, ground.point.z));
                    bool ok = top < 3f && above > -0.05f && across < 0.5f;
                    Metric($"magazine let go {depth * 100f:0} cm in the ground: top speed m/s", top);
                    report($"magazine let go {depth * 100f:0} cm in the ground: {(ok ? "came out and lay still" : "NOT LAID OUT")} "
                           + $"(top speed {top:0.0} m/s, ends {above * 100f:0} cm above the ground and {across * 100f:0} cm aside; moved out "
                           + $"{(start - (ground.point - Vector3.up * depth)).magnitude * 100f:0} cm on release)");
                }
                finally
                {
                    mag.Destroy();
                }
            }
            // A weapon the same way (a soldier's death drop, or the player's let-go gun, with its hand in the ground).
            WeaponManager.WeaponEntry entry = held.Weapon != null ? held.Weapon.weaponEntry : null;
            DroppedWeapon gun = entry != null
                ? sys.Drops.Spawn(entry, new Runtime.RigidPose(ground.point - Vector3.up * 0.3f, Quaternion.LookRotation(Vector3.Cross(Vector3.up, fwd), Vector3.up)),
                                  Vector3.one, Vector3.zero, -1, -1, open: head.position)
                : null;
            // The control: the same drop 40 cm aside with no point in the open given (as before the fix), to show the case is real.
            Vector3 aside = Vector3.Cross(Vector3.up, fwd) * 0.4f;
            RaycastHit ground2 = default;
            DroppedWeapon control = entry != null && Physics.Raycast(ground.point + aside + Vector3.up, Vector3.down, out ground2, 2f, ~0, QueryTriggerInteraction.Ignore)
                ? sys.Drops.Spawn(entry, new Runtime.RigidPose(ground2.point - Vector3.up * 0.3f, Quaternion.LookRotation(Vector3.Cross(Vector3.up, fwd), Vector3.up)),
                                  Vector3.one, Vector3.zero, -1, -1)
                : null;
            if (gun != null && gun.Go != null)
            {
                yield return new WaitForSecondsRealtime(1.5f);
                if (gun.Go == null)
                {
                    report("weapon let go 30 cm in the ground: GONE");
                    yield break;
                }
                // Against the ground where it ends, as the magazine's above: put back toward the eyes, it lies nearer, and on a slope
                // a GREASER 5 cm above the terrain read as 12 cm under the ground where it was let go (2026-10-25). Buried, the
                // ground over it is still found.
                Vector3 gunEnd = gun.Go.transform.position;
                float groundY = Physics.Raycast(gunEnd + Vector3.up * 0.5f, Vector3.down, out RaycastHit floor, 2f, ~PropPhysics.BodyMask & ~(1 << gun.Go.layer),
                                                QueryTriggerInteraction.Ignore) ? floor.point.y : ground.point.y;
                float above = gunEnd.y - groundY;
                Metric("weapon let go 30 cm in the ground: cm above the ground after 1.5 s", above * 100f);
                string under = "";
                if (above < -0.1f)
                {
                    // What the floor test found under it (a lower floor there keeps it from being put back up).
                    var names = new List<string>();
                    foreach (RaycastHit h in Physics.RaycastAll(gun.Go.transform.position + Vector3.up * 0.01f, Vector3.down, 3f, ~PropPhysics.BodyMask, QueryTriggerInteraction.Ignore))
                    {
                        names.Add($"{h.collider.name} ({h.collider.GetType().Name}, layer {h.collider.gameObject.layer}, {h.distance * 100f:0} cm{(h.collider.transform.IsChildOf(gun.Go.transform) ? ", its own" : "")})");
                    }
                    under = "; under it: " + (names.Count > 0 ? string.Join(", ", names) : "nothing within 3 m");
                }
                string without = control != null && control.Go != null ? $"; without the fix it ends {(control.Go.transform.position.y - ground2.point.y) * 100f:0} cm above" : "";
                report($"weapon let go 30 cm in the ground: {(above > -0.1f && above < 1f ? "came out on top" : "NOT LAID OUT")} (ends {above * 100f:0} cm above the ground{without}{under})");
                UnityEngine.Object.Destroy(gun.Go);
            }
            if (control != null && control.Go != null)
            {
                UnityEngine.Object.Destroy(control.Go);
            }
        }

        /// <summary>
        /// The pouch through the hand path: the off hand in the pouch with the grip open takes nothing; squeezing takes a
        /// magazine; swinging and opening the hand throws it.
        /// </summary>
        private static IEnumerator PouchThrowCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Rig.VRRig rig = Rig.VRRig.Instance;
            Rig.VRHand off = rig != null ? rig.Off : null;
            if (sys == null || off == null || sys.Held == null || off.Role != Rig.HandRole.Free || off.Held != null || !sys.Held.ActiveAmmo.UsesDetachableMag)
            {
                report("pouch: skipped (no magazine-fed weapon in hand or the off hand is busy)");
                yield break;
            }
            int side = (int)off.Side;
            Runtime.RigidPose raw = off.Raw;
            Vector3 palmFromRaw = raw.InverseTransformPoint(off.Palm.position);
            Runtime.RigidPose At(Vector3 palm) => new Runtime.RigidPose(palm - raw.rotation * palmFromRaw, raw.rotation);
            try
            {
                // Reach for the magazine where it is shown (a player grabs what they see, not the zone's middle).
                Rig.VRHand.Scripted[side] = At(sys.Zones.Pouch);
                yield return new WaitForSecondsRealtime(0.3f);
                Vector3 shown = sys.Pouch.Shown ? sys.Pouch.GrabPoint : sys.Zones.Pouch;
                report($"pouch: the shown magazine's grab point is {Vector3.Distance(shown, sys.Zones.Pouch) * 100f:0.0} cm from the pouch point "
                       + $"({(sys.Zones.InPouch(shown) ? "inside" : "OUTSIDE")} the grab zone), {(rig.Head.position.y - shown.y) * 100f:0} cm under the eyes");
                Rig.VRHand.Scripted[side] = At(shown);
                yield return new WaitForSecondsRealtime(0.4f);
                bool tookOnTouch = off.Held != null;
                if (off.Held is MagazineProp early)
                {
                    early.Destroy();
                }
                Input.VRInput.SimulateGrabPress(off.Side);
                yield return null;
                yield return null;
                var mag = off.Held as MagazineProp;
                bool took = mag != null;
                float flown = 0f;
                if (took)
                {
                    Vector3 swing = Vector3.ProjectOnPlane(rig.Head.forward, Vector3.up).normalized * 3f + Vector3.up * 1f;
                    Vector3 from = sys.Zones.Pouch;
                    for (int i = 0; i < 6; i++)
                    {
                        Rig.VRHand.Scripted[side] = At(from + swing * (0.011f * i));
                        Rig.VRHand.ScriptedVelocity[side] = swing;
                        yield return null;
                    }
                    Input.VRInput.SimulatedGrab[side] = false;
                    yield return null;
                    yield return null;
                    Vector3 released = mag.Go != null ? mag.Go.transform.position : Vector3.zero;
                    yield return new WaitForSecondsRealtime(0.5f);
                    flown = mag.Go != null ? Vector3.Distance(mag.Go.transform.position, released) : 0f;
                    string magShape = mag.ShapeMatch(out float magExcess, out float magCovered);
                    if (magShape != null)
                    {
                        string source = mag.BoxShaped ? "meshes' boxes" : "model";
                        Metric($"magazine shape ({source}): % outside the model", magExcess);
                        Metric($"magazine shape ({source}): % of the model covered", magCovered);
                        report($"thrown magazine's physics shape (from the {source}): {magShape}");
                    }
                    if (mag.Go != null)
                    {
                        sys.Held.ActiveAmmo.ReturnSpare(mag.Rounds);
                        mag.Rounds = 0;
                        mag.Destroy();
                    }
                }
                report($"pouch: a hand in the pouch with the grip open takes {(tookOnTouch ? "A MAGAZINE (TAKEN BY TOUCH)" : "nothing")}; squeezing takes {(took ? "a magazine" : "NOTHING")}{(took ? flown > 0.6f ? $"; let go in a swing it flew {flown:0.0} m" : $"; let go in a swing it moved only {flown:0.0} m (NOT THROWN)" : "")}");
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
                Rig.VRHand.ScriptedVelocity[side] = Vector3.zero;
                Input.VRInput.SimulatedGrab[side] = false;
            }
        }

        private static IEnumerator Deploy()
        {
            for (int attempt = 0; attempt < 6 && (LocalPlayer.actor == null || LocalPlayer.actor.dead); attempt++)
            {
                if (LoadoutUi.instance != null && LoadoutUi.IsOpen())
                {
                    LoadoutUi.instance.OnDeployClick();
                }
                float until = Time.realtimeSinceStartup + 12f;
                while (LocalPlayer.actor != null && LocalPlayer.actor.dead && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }
            }
        }

        private static IEnumerator TestWeapons()
        {
            List<WeaponManager.WeaponEntry> entries = WeaponManager.instance.allWeapons.Where(e => e != null && e.prefab != null && WeaponSelfTest.Allows(e)).ToList();
            Results.Add($"Weapons to test: {entries.Count} (VR {(VRManager.Active ? "on" : "off")}{(WeaponSelfTest.Only.Length > 0 ? ", only " + string.Join(", ", WeaponSelfTest.Only) : "")})");
            if (VRManager.Active)
            {
                yield return BodyShots();
            }
            int every = WeaponSelfTest.Only.Length > 0 ? 1 : Mathf.Max(1, entries.Count / 28); // a filtered run pictures every weapon
            int recoilRifles = 0, recoilPistols = 0, leftRifles = 0, leftPistols = 0, rackRifles = 0, rackPistols = 0, rackManual = 0, passChecks = 0, slideLockChecks = 0, emptyReloadChecks = 0, secondChecks = 0, keepOutChecks = 0, pumpChecks = 0, emptyPouchChecks = 0, offLineChecks = 0, steadyMgs = 0, steadyRifles = 0, weightPistols = 0;
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-quick", StringComparison.OrdinalIgnoreCase)))
            {
                // Quick run: the weapons that get a picture, the first few melee weapons, grenades and pistols, and the
                // first rifle (tactical reload and the let-go check).
                int n = 0;
                entries = entries.Where(e =>
                {
                    n++;
                    Weapon w = e.prefab.GetComponent<Weapon>();
                    return n % every == 0 || w is MeleeWeapon || w is ThrowableWeapon || e.slot == WeaponManager.WeaponSlot.Primary && n < 40
                           || w != null && !(w is ThrowableWeapon) && w.configuration.ammo == 1; // launchers: pictured for what hangs under them
                }).Take(70).ToList();
                Results.Add($"Quick run: {entries.Count} weapons");
                every = Mathf.Max(1, entries.Count / 28);
            }
            // Made ready first as the menu's warm-up makes them in play (this run starts a map at once, so the warm-up never ran):
            // the numbers are then those of a match after the menu, not each weapon's first analysis and shape fits in the middle
            // of its checks (2026-10-21). The profiler starts again after it.
            PerfProbe.SetContext("preparing the weapons as the menu does");
            while (!CodeWarmup.Step(50f))
            {
                yield return null; // the mod's code compiled as the menu compiles it, before the weapons there too
            }
            Results.Add("Compiled ahead: " + CodeWarmup.Summary);
            var prepared = System.Diagnostics.Stopwatch.StartNew();
            foreach (WeaponManager.WeaponEntry e in entries)
            {
                try
                {
                    WeaponWarmup.Prepare(e.prefab.GetComponent<Weapon>(), e);
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("autotest-prepare:" + e.name, $"Preparing {e.name} as the menu does failed: {ex.Message}");
                }
                yield return null;
            }
            Results.Add($"Prepared as the menu's warm-up does: {entries.Count} weapons in {prepared.Elapsed.TotalSeconds:0.0} s");
            PerfProbe.Reset();
            int index = 0, meleeShots = 0, throwShots = 0, pistolShots = 0, launcherShots = 0, manualShots = 0, lensShots = 0;
            bool tacticalDone = false, dropDone = false, pistolPouchDone = false, sightPartsDone = false, armsDone = false, assistedDone = false, scopeBenchDone = false, magnifierHandDone = false;
            foreach (WeaponManager.WeaponEntry entry in entries)
            {
                // The weapon's id (its place in the game's list): the same number on its pictures in every run, however
                // the list was filtered.
                index = WeaponSelfTest.IdOf(entry);
                _context = entry.name;
                PerfProbe.SetContext(entry.name);
                Actor actor = LocalPlayer.actor;
                if (actor == null || actor.dead)
                {
                    yield return Deploy();
                    actor = LocalPlayer.actor;
                    if (actor == null || actor.dead)
                    {
                        Results.Add($"{entry.name}: player could not respawn, stopping");
                        Problem($"[{entry.name}] the player could not respawn: this weapon and the rest were not tested");
                        yield break;
                    }
                }
                Weapon weapon = null;
                try
                {
                    weapon = actor.EquipNewWeaponEntry(entry, Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1), true);
                }
                catch (Exception e)
                {
                    Problem($"[{entry.name}] equip: {e.GetType().Name}: {e.Message}");
                }
                if (weapon == null)
                {
                    // Usually the soldier died or was knocked down that moment: wait, respawn if needed, try once more.
                    yield return new WaitForSecondsRealtime(1f);
                    actor = LocalPlayer.actor;
                    if (actor == null || actor.dead)
                    {
                        yield return Deploy();
                        actor = LocalPlayer.actor;
                    }
                    if (actor != null && !actor.dead)
                    {
                        try
                        {
                            weapon = actor.EquipNewWeaponEntry(entry, Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1), true);
                        }
                        catch (Exception e)
                        {
                            Problem($"[{entry.name}] equip (retry): {e.GetType().Name}: {e.Message}");
                        }
                    }
                }
                var lines = new List<string>();
                if (VRManager.Active && VRConfig.SkipDrawAnimation.Value)
                {
                    // Two frames in, a skipped draw has the weapon idle already.
                    yield return null;
                    yield return null;
                    string drawClip = DrawClipPlaying(weapon);
                    if (drawClip != null)
                    {
                        Problem($"[{entry.name}] draw animation still playing ({drawClip})");
                        lines.Add("draw animation playing: " + drawClip);
                    }
                }
                yield return new WaitForSecondsRealtime(0.5f);
                HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                if (held != null && held.Weapon == weapon)
                {
                    string model = ModelCheck(weapon, held.Rig, entry);
                    if (model != null)
                    {
                        Problem($"[{entry.name}] no visible model: {model}");
                        lines.Add("NO VISIBLE MODEL");
                    }
                    int shellIndex = index;
                    yield return held.Reload.SelfCheck(lines.Add, VRManager.Active && GrabShots
                        ? (name, at) => PointCloseUps($"{name}-{shellIndex:000}-{entry.name}", held, at)
                        : (Action<string, Vector3>)null);
                    // The picture runs take the grab pictures, and then every other per-gun check too: they skipped them all,
                    // so the full runs never ran the empty-reload, cover, second-weapon or keep-out checks (255.I6-3).
                    bool pictured = VRManager.Active && GrabShots;
                    if (pictured)
                    {
                        yield return GrabPictures(held, index, entry.name, lines);
                    }
                    if (VRManager.Active)
                    {
                        // A picture of the weapon in the (simulated) hands: a spread across the list, plus melee, grenades
                        // and pistols.
                        ActionType action = held.Rig.Data.Action;
                        bool want = index % every == 0
                                    || action == ActionType.Melee && meleeShots++ < 4
                                    || action == ActionType.Throwable && throwShots++ < 3
                                    || held.Rig.Data.IsPistol && action == ActionType.SelfLoading && pistolShots++ < 4
                                    || action == ActionType.SingleShot && launcherShots++ < 8
                                    || action == ActionType.Manual && manualShots++ < 3; // pump and bolt guns: their rack grip too
                        if (want && !pictured)
                        {
                            yield return new WaitForEndOfFrame();
                            Shots.Capture($"weapon-{index:000}-{entry.name}");
                            if (action == ActionType.SingleShot && Rig.VRRig.Instance != null)
                            {
                                // Under a launcher: nothing (no box, no shadow of one) may show on the ground or at the pouch.
                                Transform head = Rig.VRRig.Instance.Head;
                                Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
                                Shots.Capture($"weapon-{index:000}-{entry.name}-looking-down", head.position, Quaternion.LookRotation(flat * 0.3f + Vector3.down, flat), 100f);
                                lines.Add($"launcher: rocket from {held.Rig.Data.MagMethod}, {held.Rig.Data.MagBounds.size.magnitude * 100f:0} cm");
                            }
                            GripCloseups(held, $"grip-{index:000}-{entry.name}", lines.Add);
                            yield return ClipCompare(lines.Add);
                        }
                        if (action == ActionType.Melee)
                        {
                            lines.Add(MeleeCheck(held));
                        }
                        if (want && action == ActionType.SelfLoading && (held.Rig.Data.IsPistol ? leftPistols++ < 1 : leftRifles++ < 1))
                        {
                            int leftIndex = index;
                            yield return LeftHandCheck(held.Weapon, $"lefthanded-{leftIndex:000}-{entry.name}", lines.Add);
                            held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                            if (held == null || held.Weapon != weapon)
                            {
                                lines.Add("left-handed check: THE WEAPON WAS NOT BACK IN THE HAND afterwards");
                                ReportWeapon(entry.name, weapon, lines);
                                continue;
                            }
                        }
                        if (want && (action == ActionType.SelfLoading || action == ActionType.SingleShot) && secondChecks++ < (Deep ? 6 : 2))
                        {
                            yield return SecondWeaponCheck(held, lines.Add); // a second weapon in the free hand (Tasks 252 p)
                            yield return SecondWeaponFloatCheck(held, lines.Add); // and with the gun in either hand, measured each frame
                            held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                            if (held == null || held.Weapon != weapon)
                            {
                                lines.Add("second weapon check: THE WEAPON WAS NOT BACK IN THE HAND afterwards");
                                ReportWeapon(entry.name, weapon, lines);
                                continue;
                            }
                        }
                        if (want && action == ActionType.SelfLoading && !held.Rig.Data.IsPistol && passChecks++ < (Deep ? 3 : 1))
                        {
                            yield return PassHandCheck(held, $"{index:000}-{entry.name}", lines.Add);
                            held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                            if (held == null || held.Weapon != weapon)
                            {
                                lines.Add("pass check: THE WEAPON WAS NOT BACK IN THE HAND afterwards");
                                ReportWeapon(entry.name, weapon, lines);
                                continue;
                            }
                        }
                        // Rack grips on rifles, pistols and pump / bolt guns (a few of each), not only the first rifles.
                        if (!pictured && want && (Deep ? action == ActionType.Manual || action == ActionType.SelfLoading
                                : action == ActionType.Manual ? rackManual++ < 3
                                : action == ActionType.SelfLoading && (held.Rig.Data.IsPistol ? rackPistols++ < 3 : rackRifles++ < 4)))
                        {
                            int rackIndex = index;
                            Vector3? rackCam = null;
                            yield return held.Reload.RackGripSelfCheck(lines.Add, name =>
                            {
                                Shots.Capture($"{name}-{rackIndex:000}-{entry.name}");
                                RackCloseUps($"{name}-{rackIndex:000}-{entry.name}", held, ref rackCam);
                            });
                            yield return held.Reload.SupportSlideSelfCheck(lines.Add); // the support hand slid onto the handle (Tasks 322)
                        }
                        if (held.Reload.WorksByPump && (Deep || pumpChecks++ < 3))
                        {
                            yield return held.Reload.PumpTriggerSelfCheck(lines.Add); // the pump by the support hand's trigger (255.I6-2)
                        }
                        if (want && held.Rig.Data.Hinge == HingeKind.FeedCover)
                        {
                            yield return held.Reload.CoverSelfCheck(lines.Add); // every feed cover opened by hand (the QJBs' could not be)
                        }
                        if (held.Rig.Data.Hinge == HingeKind.FeedCover)
                        {
                            int beltIndex = index;
                            yield return held.Reload.MgHandReloadSelfCheck(lines.Add, (shot, at, distance) => MagazineSideShot(held, $"{shot}-{beltIndex:000}-{entry.name}", at, distance)); // the whole belt reload through the hand's own grabs (252 af, o), the belt measured against the box
                        }
                        if (want && action == ActionType.SelfLoading)
                        {
                            int shotIndex = index;
                            yield return held.Reload.TwoHandSelfCheck(lines.Add, name =>
                            {
                                Shots.Capture($"{name}-{shotIndex:000}-{entry.name}");
                                SupportCloseups(held, $"support-{shotIndex:000}-{entry.name}", lines.Add);
                            });
                        }
                        // Launchers, machine guns and a rifle to compare: held still, the weapon must be still.
                        if (want && (action == ActionType.SingleShot && held.Rig.Data.HasSupportPoint || held.Rig.Data.IsMachineGun && steadyMgs++ < 2
                                     || action == ActionType.SelfLoading && !held.Rig.Data.IsPistol && steadyRifles++ < 1))
                        {
                            yield return SteadyCheck(held, entry.name, lines.Add);
                            yield return WeightCheck(held, entry.name, lines.Add);
                            yield return SupportPullCheck(held, entry.name, lines.Add);
                        }
                        else if (want && held.Rig.Data.IsPistol && weightPistols++ < 1)
                        {
                            yield return WeightCheck(held, entry.name, lines.Add);
                        }
                        if (Deep && want && held.Rig.Data.HasEyePoint && action != ActionType.Melee && action != ActionType.Throwable)
                        {
                            yield return SightCheck(held, $"sight-{index:000}-{entry.name}", lines.Add);
                        }
                        if (Deep && !scopeBenchDone && held.HasLensScope)
                        {
                            scopeBenchDone = true;
                            yield return ScopeBench(held, entry.name, lines.Add); // what the lens's second camera costs (255.P1.2)
                        }
                        // After the rack and two-hand checks: it locks the slide and lets it go again, which must not shape
                        // what they measure.
                        if (want && held.Rig.Data.IsPistol && slideLockChecks++ < (Deep ? 6 : 2))
                        {
                            yield return SlideLockPoseCheck(held, lines.Add);
                        }
                        if (want && action == ActionType.SelfLoading && (Deep || emptyReloadChecks++ < 6))
                        {
                            // Every self-loader in a full run, a few in a quick one: the slide stays back on a fresh magazine,
                            // comes home when released, and follows a hand rack (Tasks 252, 254).
                            yield return held.Reload.EmptyReloadSelfCheck(lines.Add);
                        }
                        if (held.Rig.Data.EnBloc)
                        {
                            yield return held.Reload.EnBlocSelfCheck(lines.Add); // clip in through the open action, thrown out empty (255.I6-5)
                        }
                        if (held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag && (held.Rig.Data.EnBloc || emptyPouchChecks++ < (Deep ? 4 : 1)))
                        {
                            yield return held.Reload.EmptyAtPouchSelfCheck(lines.Add); // an emptied magazine is not handed back (255.I6-6)
                        }
                        if (want && held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag && !held.Rig.Data.IsMachineGun && (Deep || offLineChecks++ < 4))
                        {
                            yield return held.Reload.OffLinePullSelfCheck(lines.Add); // taken 7 cm off the well's line (255.I6-7)
                        }
                        if (!pictured && Deep && held.Rig.Data.Action == ActionType.SingleShot && held.ActiveAmmo != null && !held.ActiveAmmo.UsesDetachableMag)
                        {
                            // Every launcher loaded by hand from the pouch at its muzzle (it ran only in the film, on a few,
                            // and the self-check seats the rocket with no hand at all).
                            yield return held.Reload.RocketCheck(lines.Add);
                        }
                        if (!pictured && Deep && want && held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag)
                        {
                            int magIndex = index;
                            HeldWeapon magHeld = held;
                            yield return held.Reload.MagazineDeepCheck(lines.Add, (name, at, distance) => MagazineSideShot(magHeld, $"{name}-{magIndex:000}-{entry.name}", at, distance));
                        }
                        if (want && held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag && (Deep || keepOutChecks++ < 4))
                        {
                            int koIndex = index;
                            yield return held.Reload.MagazineKeepOutCheck(lines.Add, name => Shots.Capture($"{name}-{koIndex:000}-{entry.name}"));
                        }
                        if ((Deep || !tacticalDone) && held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag && !held.Rig.Data.IsPistol && held.Rig.Data.MagKickable)
                        {
                            tacticalDone = true;
                            // Named with the gun (a later gun's pictures overwrote these: RECON LRR's mag-kick pictures were
                            // the mrad's; 252 ae).
                            int magIndex = index;
                            yield return held.Reload.TacticalSelfCheck(lines.Add, name => Shots.Capture($"{name}-{magIndex:000}-{entry.name}"));
                            yield return held.Reload.H3vrReloadSelfCheck(lines.Add, name =>
                            {
                                Shots.Capture($"{name}-{magIndex:000}-{entry.name}");
                                MagHandCloseups($"{name}-{magIndex:000}-{entry.name}", lines.Add);
                            });
                        }
                        if (!armsDone && want && action == ActionType.SelfLoading && !held.Rig.Data.IsPistol
                            && held.Rig.Data.ArmRight != null && held.Rig.Data.ArmLeft != null && held.Rig.ArmRenderers.Length > 0)
                        {
                            armsDone = true;
                            yield return WeaponArmsCheck(held.Weapon, $"weapon-arms-{index:000}-{entry.name}", lines.Add);
                            held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                            if (held == null || held.Weapon != weapon)
                            {
                                lines.Add("weapon's arms check: THE WEAPON WAS NOT BACK IN THE HAND afterwards");
                                ReportWeapon(entry.name, weapon, lines);
                                continue;
                            }
                        }
                        if (!sightPartsDone && held.Rig.Data.SightPartPaths.Count > 0 && !held.Aiming)
                        {
                            // A flip magnifier switched in with the gun held low (not aiming): it must stay flipped in.
                            sightPartsDone = true;
                            int before = weapon.GetSightModeIndex();
                            weapon.NextSightMode();
                            yield return new WaitForSecondsRealtime(0.4f);
                            yield return new WaitForEndOfFrame();
                            Shots.Capture($"sight-mode-{weapon.GetSightModeIndex()}-not-aiming-{index:000}-{entry.name}");
                            float off = held.SightPartsOffMm();
                            Metric("sight parts: mm from the selected mode's place when not aiming", off);
                            lines.Add($"sight mode {before} -> {weapon.GetSightModeIndex()} not aiming: {held.Rig.Data.SightPartPaths.Count} part(s) {(off <= 2f ? "where the mode puts them" : $"NOT IN PLACE ({off:0.0} mm off)")}");
                            for (int k = 0; k < 6 && weapon.GetSightModeIndex() != before && weapon.NextSightMode(); k++)
                            {
                                yield return null;
                            }
                        }
                        if (!magnifierHandDone && held.MagnifierGrip.HandlesWeapon(weapon))
                        {
                            // The flip magnifier by the free hand's grip (task 301), once a run on the first gun that has one.
                            magnifierHandDone = true;
                            yield return MagnifierHandCheck(held, entry.name, lines.Add);
                        }
                        if ((Deep || !pistolPouchDone) && held.Rig.Data.IsPistol && held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag)
                        {
                            // A pistol's magazine from the pouch too (pistol magazines were shown low and hard to take).
                            pistolPouchDone = true;
                            yield return PouchThrowCheck(lines.Add);
                        }
                        if (!assistedDone && held.Rig.Data.CyclesByHand && held.ActiveAmmo != null && held.ActiveAmmo.Participates)
                        {
                            assistedDone = true;
                            yield return AssistedAmmoCheck(held, lines.Add);
                        }
                        if (!dropDone && tacticalDone && action == ActionType.SelfLoading && !held.Rig.Data.IsPistol)
                        {
                            dropDone = true;
                            yield return WorldCollisionCheck(held, lines.Add);
                            // The two magazine checks need the gun in the hand: after the drop checks it is in its holster, and
                            // both were skipped in every run.
                            yield return GentleMagazineCheck(lines.Add);
                            yield return SlowMotionMagazineCheck(lines.Add);
                            yield return SlowedHandsCheck(lines.Add);
                            HandLoadingCheck(lines.Add);
                            yield return RunningMagazineEaseCheck(lines.Add);
                            yield return BuriedMagazineCheck(lines.Add);
                            yield return PouchThrowCheck(lines.Add);
                            yield return DropCheck(lines.Add);
                            yield return ReachCheck(lines.Add);
                            yield return GroundDriftCheck(lines.Add);
                            yield return HandVelocityCheck(lines.Add);
                            yield return SprintFireCheck(lines.Add);
                            yield return GogglesCheck(lines.Add);
                            yield return BodyGrabCheck(lines.Add);
                            weapon = actor.weapons != null && (int)entry.slot < actor.weapons.Length ? actor.weapons[Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1)] : null;
                            if (weapon != null && actor.activeWeapon != weapon)
                            {
                                actor.SwitchWeapon(Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1));
                                yield return new WaitForSecondsRealtime(1f);
                            }
                        }
                    }
                    if (held != null && held.IsKitForTest && Rig.VRRig.Instance != null && VRManager.Active)
                    {
                        bool shown = Rig.VRRig.Instance.Dominant.Model.Visible;
                        lines.Add(shown ? "bag: glove NOT HIDDEN on it" : "bag: carried without a glove");
                        Metric("bags carried with the glove hidden (0/1)", shown ? 0 : 1);
                    }
                    if (held.Rig.Data.Action == ActionType.Throwable && weapon is ThrowableWeapon)
                    {
                        var pinLines = new List<string>();
                        yield return WeaponSystem.Instance.ThrowableSelfCheck(held, pinLines.Add);
                        foreach (string l in pinLines)
                        {
                            lines.Add(l);
                            if (l.Contains("LIVE GRENADE WITH") || l.Contains("NOT REFUNDED") || l.Contains("NO GRENADE"))
                            {
                                Problem($"[{entry.name}] grenade pin: {l}");
                            }
                        }
                    }
                }
                if (weapon != null && weapon.gameObject.activeInHierarchy)
                {
                    // The game keeps a drawn weapon from firing for its draw delay (about a second): wait it out.
                    Weapon ready = weapon.GetActiveSubWeapon() ?? weapon;
                    // A launcher's draw takes longer (the game's unholster time, on scaled time: an RPG-7V's ran past 3 s).
                    float drawTime = ready != null && ready.configuration != null ? ready.configuration.unholsterTime / Mathf.Max(0.1f, Time.timeScale) : 0f;
                    float until = Time.realtimeSinceStartup + Mathf.Max(3f, drawTime + 1f);
                    while (ready != null && !ready.unholstered && Time.realtimeSinceStartup < until)
                    {
                        yield return null;
                    }
                    bool counts = held != null && held.Weapon == weapon && held.ActiveAmmo != null && held.ActiveAmmo.Participates
                                  && held.Rig.Data.Action != ActionType.Throwable;
                    _shots = 0;
                    int casesBefore = Casings.Spawned;
                    float shotKick = 0f, shotBack = 0f, shotImpulse = 0f;
                    Weapon listened = counts ? ready : null;
                    listened?.onSpawnProjectiles.AddListener(CountShot);
                    string atPull = null, atFire = null;
                    for (int i = 0; i < 3; i++)
                    {
                        // A slow gun (a bolt sniper's 1.5 s) is still cooling down from the check before, or from the last
                        // shot: waited out, or it "did not fire".
                        // Also a launcher still in its own reload animation after the test loaded it (an RPG-7V's): the
                        // game cannot fire until it ends.
                        Weapon cooling = weapon.GetActiveSubWeapon();
                        for (float coolUntil = Time.realtimeSinceStartup + 3f; cooling != null && (cooling.CoolingDown() || cooling.reloading || !cooling.unholstered || !cooling.CanFire())
                                                                               && Time.realtimeSinceStartup < coolUntil;)
                        {
                            yield return null;
                        }
                        if (cooling != null && atPull == null && !cooling.CanFire())
                        {
                            atPull = $"at the pull: can fire False, reloading {cooling.reloading}, cooling down {cooling.CoolingDown()}, locked {cooling.isLocked}, "
                                     + $"unholstered {cooling.unholstered}, ammo {cooling.ammo}, fired this pull {cooling.hasFiredSingleRoundThisTrigger}, "
                                     + $"mod can fire {(held != null && held.ActiveAmmo != null ? held.ActiveAmmo.CanFire.ToString() : "-")}";
                        }
                        held?.ResetKick(); // each shot on its own: shots that stack up are not one shot's kick
                        int flashesBefore = LensFlash.Instance != null ? LensFlash.Instance.Shown : 0;
                        try
                        {
                            Weapon active = weapon.GetActiveSubWeapon();
                            bool fired = active.Fire(active.CurrentMuzzle().forward, true);
                            if (!fired && atFire == null)
                            {
                                // Read before StopFire, which clears the pull's state.
                                Weapon sub = active.GetActiveSubWeapon();
                                atFire = $"Fire returned false: sub weapon {(sub == active ? "itself" : sub != null ? sub.name : "none")}, unholstered {active.unholstered}, "
                                         + $"overheating {active.isOverheating}, locked {active.isLocked}, can fire now {active.CanFire()}, reloading {active.reloading}, "
                                         + $"fired this pull {active.hasFiredSingleRoundThisTrigger}, ammo {active.ammo}, holding fire {active.holdingFire}, "
                                         + $"script took it {(active.onFireScriptable != null && active.onFireScriptable.isConsumed)}, "
                                         + $"muzzles {(active.configuration.muzzles != null ? active.configuration.muzzles.Length : 0)}, type {active.GetType().Name}";
                            }
                            active.StopFire();
                        }
                        catch (Exception e)
                        {
                            Problem($"[{entry.name}] fire: {e.GetType().Name}: {e.Message} {FirstFrames(e)}");
                        }
                        if (i == 0 && VRManager.Active && LensFlash.Instance != null && lensShots < (Deep || GrabShots ? 1000 : 12))
                        {
                            // The muzzle lens flash of the first shot, pictured when it shows: its picture is made on a
                            // worker and comes a frame or two after the shot (it is gone 60 ms later).
                            yield return new WaitForEndOfFrame();
                            for (int wait = 0; wait < 4 && LensFlash.Instance.Shown > flashesBefore && Rig.LensEffects.Instance != null
                                                && !Rig.LensEffects.Instance.ShowsFlash; wait++)
                            {
                                yield return null;
                                yield return new WaitForEndOfFrame();
                            }
                            bool flashed = LensFlash.Instance.Shown > flashesBefore;
                            if (flashed)
                            {
                                lensShots++;
                                Shots.Capture($"lensflash-{index:000}-{entry.name}");
                                Rig.LensEffects.Instance?.SavePictures($"lens-eyes-{index:000}-{entry.name}");
                            }
                            MuzzleFlashInfo flashInfo = MuzzleFlashInfo.Of(weapon.GetActiveSubWeapon());
                            lines.Add($"muzzle flash: {flashInfo.Describe()}; lens flash {(flashed ? $"shown, strength {LensFlash.Instance.LastPower:0.00}" : "none")}");
                            Metric("muzzle lens flash: strength of a first shot", flashed ? LensFlash.Instance.LastPower : 0f);
                        }
                        yield return new WaitForSecondsRealtime(0.25f);
                        if (held != null)
                        {
                            shotKick = Mathf.Max(shotKick, held.PeakKick);
                            shotBack = Mathf.Max(shotBack, held.PeakBack);
                            shotImpulse = Mathf.Max(shotImpulse, held.PeakShotImpulse);
                        }
                        if (counts && held.ActiveAmmo.Rig.CyclesByHand)
                        {
                            held.Reload.CycleForTest(); // a bolt or pump needs working between shots
                        }
                    }
                    int firedSingles = _shots;
                    Weapon burstWeapon = weapon.GetActiveSubWeapon();
                    if ((Deep || GrabShots) && VRManager.Active && counts && burstWeapon != null && burstWeapon.configuration != null
                        && burstWeapon.configuration.auto && LensFlash.Instance != null)
                    {
                        yield return LensBurstCheck(burstWeapon, index, entry.name, lines);
                    }
                    listened?.onSpawnProjectiles.RemoveListener(CountShot);
                    _shots = firedSingles;
                    if (counts)
                    {
                        lines.Add($"fired {Mathf.Min(_shots, 3)}/3");
                        if (VRManager.Active && Casings.Ejects(held) && held.Rig.Data.Action != ActionType.BreakOpen) // a break action drops them when opened
                        {
                            int cases = Casings.Spawned - casesBefore;
                            Metric("cases thrown per shot", _shots > 0 ? (double)cases / Mathf.Min(_shots, 3) : 0);
                            lines.Add($"cases thrown: {cases}{(cases == 0 && _shots > 0 ? " (NO CASES)" : "")}, case objects made so far {Casings.Built} for {Casings.Spawned} thrown{(Casings.Built > 40 ? " (POOL NOT REUSED)" : "")}"
                                      + $"{(Casings.ThrowsOwnShells(held) ? ", the gun's own shell" : "")}");
                        }
                        Weapon lockOn = weapon.GetActiveSubWeapon();
                        if (_shots == 0 && lockOn != null && lockOn.targetTracker != null && !lockOn.targetTracker.CanFire())
                        {
                            // A lock-on launcher fires only at a target it has locked (the game's tracker follows the head
                            // while aiming through the sight); there is none in front of the test.
                            lines.Add("not fired: needs a lock-on (no target here)");
                        }
                        else if (_shots == 0)
                        {
                            Weapon w0 = weapon.GetActiveSubWeapon();
                            AmmoState a0 = held.ActiveAmmo;
                            Problem($"[{entry.name}] DID NOT FIRE (counter {a0.CounterText()}; game: can fire {w0.CanFire()}, ammo {w0.ammo}, reloading {w0.reloading}, "
                                    + $"locked {w0.isLocked}, fired this pull {w0.hasFiredSingleRoundThisTrigger}, cooling down {w0.CoolingDown()} (cooldown {w0.configuration.cooldown:0.00} s), "
                                    + $"charge {w0.configuration.useChargeTime}, auto {w0.configuration.auto}, muzzles {(w0.configuration.muzzles != null ? w0.configuration.muzzles.Length : 0)}, "
                                    + $"projectile {(w0.configuration.projectilePrefab != null ? w0.configuration.projectilePrefab.name : "NONE")}; mod: can fire {a0.CanFire}, chambered {a0.Chambered}, bolt open {a0.BoltOpen}, "
                                    + $"slide locked {a0.SlideLocked}, magazine {a0.MagRounds}; a script took the shot {(w0.onFireScriptable != null && w0.onFireScriptable.isConsumed)}, "
                                    + $"unholstered {w0.unholstered}, overheating {w0.isOverheating}){(atPull != null ? "; " + atPull : "")}{(atFire != null ? "; " + atFire : "")}");
                        }
                        else if (VRManager.Active && held.Rig.Data.HasSupportPoint && (Deep || (held.Rig.Data.IsPistol ? recoilPistols++ < 2 : recoilRifles++ < 4)))
                        {
                            float oneKick = shotKick, oneBack = shotBack, oneImpulse = shotImpulse;
                            string recoilLine = null;
                            yield return TwoHandRecoil(held, weapon, (kick, back, impulse) =>
                            {
                                recoilLine = $"recoil: one hand {oneKick:0.0} deg climb, {oneBack * 100f:0.0} cm back; two hands {kick:0.0} deg, {back * 100f:0.0} cm";
                                Metric("recoil one hand: degrees of climb", oneKick);
                                Metric("recoil two hands: degrees of climb", kick);
                                // Compared by the impulse each shot gave: a shot that lands late (a revolver's hammer) would
                                // otherwise be measured before its kick.
                                recoilLine += $"; impulse per shot one hand {oneImpulse:0} deg/s, two hands {impulse:0} deg/s";
                                if (VRConfig.RecoilStrength.Value <= 0f)
                                {
                                    recoilLine += " - recoil is switched off in the settings (Recoil 0%)"; // not a fault of the gun
                                }
                                else if (oneImpulse <= 0f)
                                {
                                    recoilLine += " - NO RECOIL one-handed (shots fired, none reached the recoil)";
                                }
                                else if (oneImpulse > 0f && impulse > 0f && impulse >= oneImpulse)
                                {
                                    recoilLine += " - TWO HANDS NOT STEADIER";
                                }
                            });
                            if (recoilLine != null)
                            {
                                lines.Add(recoilLine);
                                // (Recoil switched off in the settings is the player's choice, not the gun's fault: said once at
                                // the top, not a problem on every gun, where it buried the real ones.)
                                if (recoilLine.Contains(" - ") && VRConfig.RecoilStrength.Value > 0f)
                                {
                                    Problem($"[{entry.name}] {recoilLine}");
                                }
                                else if (VRConfig.RecoilStrength.Value <= 0f && !_recoilOffSaid)
                                {
                                    _recoilOffSaid = true;
                                    Problem("recoil is switched off in the settings (Recoil 0%): no gun's recoil was measured");
                                }
                            }
                        }
                    }
                    if (FireSelector.HasModes(weapon))
                    {
                        FireSelector.Toggle(weapon);
                        FireSelector.Toggle(weapon);
                    }
                }
                ReportWeapon(entry.name, weapon, lines);
            }
        }

        private static bool _recoilOffSaid;

        /// <summary>
        /// One weapon's lines into the results, failures also into the problems. Also called before a check gives up on the
        /// weapon early (it is no longer in the hand): those lines, the failure first of all, were lost.
        /// </summary>
        private static void ReportWeapon(string name, Weapon weapon, List<string> lines)
        {
            foreach (string l in lines)
            {
                if (Failure.IsMatch(l) && !l.Contains("LIVE GRENADE WITH") && !l.Contains("NOT REFUNDED") && !l.Contains("NO GRENADE"))
                {
                    Problem($"[{name}] {l}");
                }
            }
            Results.Add($"{name}: {(weapon != null ? "equipped" : "NOT EQUIPPED")}{(lines.Count > 0 ? "; " + string.Join("; ", lines.ToArray()) : "")}");
            if (++_tested % 25 == 0)
            {
                Write(final: false); // progress on disk in case the run is cut short
            }
        }

        /// <summary>
        /// Null when the held weapon has something drawable (an enabled, active, non-arm renderer with a mesh, a material
        /// and a real scale); otherwise what its renderers look like, to see why it is invisible.
        /// </summary>
        private static string ModelCheck(Weapon weapon, WeaponRig rig, WeaponManager.WeaponEntry entry)
        {
            var arms = new HashSet<Renderer>(rig.ArmRenderers);
            // Only weapons that have something to see: zombie claws and other invisible "weapons" show nothing by design.
            var armPaths = new HashSet<string>();
            foreach (Renderer r in rig.ArmRenderers)
            {
                if (r != null)
                {
                    armPaths.Add(PathOf(weapon.transform, r.transform));
                }
            }
            int expected = 0;
            foreach (Renderer r in entry.prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is ParticleSystemRenderer) && !(r is LineRenderer) && !(r is TrailRenderer) && r.enabled
                    && ActiveInPrefab(r.transform, entry.prefab.transform) && !armPaths.Contains(PathOf(entry.prefab.transform, r.transform)))
                {
                    expected++;
                }
            }
            if (expected == 0)
            {
                return null;
            }
            int total = 0, disabled = 0, inactive = 0, armsOnly = 0, noMesh = 0, zeroScale = 0, noMaterial = 0;
            var names = new List<string>();
            foreach (Renderer r in weapon.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer || r.name.StartsWith("RavenfieldVR"))
                {
                    continue; // effects, and the mod's own counter / pin / scope objects
                }
                total++;
                if (names.Count < 6)
                {
                    names.Add(r.name);
                }
                if (arms.Contains(r)) { armsOnly++; continue; }
                if (!r.gameObject.activeInHierarchy) { inactive++; continue; }
                if (!r.enabled || r.forceRenderingOff) { disabled++; continue; }
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) { noMesh++; continue; }
                if (r.transform.lossyScale.sqrMagnitude < 1e-6f) { zeroScale++; continue; }
                bool material = false;
                foreach (Material m in r.sharedMaterials)
                {
                    material |= m != null;
                }
                if (!material) { noMaterial++; continue; }
                return null;
            }
            return $"{total} renderers ({armsOnly} counted as arms, {inactive} inactive, {disabled} disabled, {noMesh} without mesh, "
                   + $"{zeroScale} scaled to zero, {noMaterial} without material): {string.Join(", ", names.ToArray())}";
        }

        /// <summary>
        /// Recoil with the support hand on: the off hand takes the handguard, three more shots, and the kick is measured
        /// for <paramref name="result"/> (muzzle climb in degrees, push back in metres).
        /// </summary>
        private static IEnumerator TwoHandRecoil(HeldWeapon held, Weapon weapon, Action<float, float, float> result)
        {
            Rig.VRHand off = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Off : null;
            if (off == null || off.Role != Rig.HandRole.Free)
            {
                yield break;
            }
            Input.VRInput.SimulatedGrab[(int)off.Side] = true;
            off.Role = Rig.HandRole.Foregrip;
            held.SetTwoHanded(true);
            yield return new WaitForSecondsRealtime(0.3f);
            float kick = 0f, back = 0f, impulse = 0f;
            for (int i = 0; i < 3; i++)
            {
                held.ResetKick();
                try
                {
                    Weapon active = weapon.GetActiveSubWeapon();
                    active.Fire(active.CurrentMuzzle().forward, true);
                    active.StopFire();
                }
                catch (Exception)
                {
                    // reported by the one-handed shots already
                }
                yield return new WaitForSecondsRealtime(0.25f);
                kick = Mathf.Max(kick, held.PeakKick);
                back = Mathf.Max(back, held.PeakBack);
                impulse = Mathf.Max(impulse, held.PeakShotImpulse);
                if (held.ActiveAmmo != null && held.ActiveAmmo.Rig.CyclesByHand)
                {
                    held.Reload.CycleForTest();
                }
            }
            result(kick, back, impulse);
            Input.VRInput.SimulatedGrab[(int)off.Side] = false;
            yield return null; // the grip opens: the handguard is let go
            yield return null;
        }

        private static int _shots;
        private static void CountShot(Projectile[] projectiles) => _shots++;

        /// <summary>
        /// A bolt or pump gun with assisted reloading (the setting overridden in memory only): its fire animation works the
        /// action, so every shot must use a round. The mod once never chambered again in that mode and put every shot after
        /// the first back (endless ammo). The gun is filled again afterwards.
        /// </summary>
        private static IEnumerator AssistedAmmoCheck(HeldWeapon held, Action<string> report)
        {
            Weapon w = held.Weapon.GetActiveSubWeapon() ?? held.Weapon;
            AmmoState ammo = held.ActiveAmmo;
            int before = w.ammo;
            if (before < 3)
            {
                yield break;
            }
            ReloadMode? reloadBefore = VRConfig.TestReload; // (the run's Manual, put back after: cleared, the rest of the run used the saved mode)
            VRConfig.TestReload = ReloadMode.Assisted;
            _shots = 0;
            w.onSpawnProjectiles.AddListener(CountShot);
            try
            {
                for (int i = 0; i < 3; i++)
                {
                    // The game's own cooldown (the bolt or pump cycling by itself) between shots.
                    for (float until = Time.realtimeSinceStartup + 2.5f; !w.CanFire() && Time.realtimeSinceStartup < until; )
                    {
                        yield return null;
                    }
                    w.Fire(w.CurrentMuzzle().forward, true);
                    w.StopFire();
                    yield return new WaitForSecondsRealtime(0.15f);
                }
            }
            finally
            {
                w.onSpawnProjectiles.RemoveListener(CountShot);
                VRConfig.TestReload = reloadBefore;
            }
            int fired = _shots, used = before - w.ammo;
            Metric("assisted reloading: rounds used per shot (bolt and pump guns)", fired > 0 ? (double)used / fired : 0);
            report($"assisted reloading: {fired} shot(s), {used} round(s) used{(fired == 0 ? " (DID NOT FIRE)" : used < fired ? " (NOT ONE ROUND PER SHOT: endless ammo)" : "")}");
            w.ammo = before; // filled again for the checks that follow
            ammo?.AdoptWeaponAmmo();
        }

        /// <summary>
        /// Self-check lines flag failures in capitals: NOT SEATED, NOTHING CHAMBERED, NEVER LINED UP, TOO SMALL, TAKEN FROM THE
        /// BACK, STILL SHOWS... One list for the weapon lines and the films (the film's own test missed most of them). The magazine
        /// keep-out's WENT THROUGH / WENT INTO THE GUN, a picked-up magazine's LOST ROUNDS and the locked handle OUT OF THE GRAB were
        /// written into the weapon lines but never counted as problems (every report since 2026-10-17: the HK416s' "through the gun"
        /// was in the lines, not in the problems; improve round, 2026-10-22). tools/tests/failure_phrases_test.py keeps it in step.
        /// </summary>
        private static readonly Regex Failure = new Regex(@"\b(NOT|NOTHING|NO|NEVER) [A-Z]|TOO SMALL|\(FLOATING\)|\bGONE\b|HUNG IN THE AIR|\(JUMPED:|\(TRAILED |FROM THE BACK|FROM THE SIDE|SEAT WRONG|BESIDE THE WELL|PASSES THROUGH|AHEAD OF THE HAND|STILL SHOWS|DRAWS NOTHING|FLOATS BESIDE|\(SHAKES\)|RAN FORWARD ON ITS OWN|STAYED BACK after|MOVES NOTHING|THE GRIP ALONE WORKS|THE HAND LET GO OF THE RACK|HANDED BACK|AN EMPTY MAGAZINE FROM|CLIP TAKEN WITH THE BOLT SHUT|WENT THROUGH|WENT INTO THE GUN|LOST ROUNDS|OUT OF THE GRAB|THROUGH THE GRIP|HIDDEN IN THE GUN|GLOVES OVERLAP|ONTO THE RAIL BESIDE THE GRIP|WAS PUT AWAY|STILL IN THE HAND|STILL IN THE LOADOUT|TAKEN BY TOUCH|INSIDE THE WALL|\bTHE SCREEN\b|SHOWING \(|WITH THE GUN IN THE HAND|THE PLAYER WAS HIT|BLEW UP|LEFT ON THE HAND|DIED BEFORE THE SHOTS|INSIDE THE BODY|DRAWS LESS THAN AN EMPTY");

        private static string FirstFrames(Exception e)
        {
            string[] frames = (e.StackTrace ?? "").Split('\n');
            return frames.Length > 0 ? "at " + string.Join(" < ", frames.Take(3).Select(f => f.Trim()).ToArray()) : "";
        }

        private static string PathOf(Transform root, Transform t)
        {
            var sb = new StringBuilder();
            for (Transform p = t; p != null && p != root; p = p.parent)
            {
                sb.Insert(0, "/" + p.name);
            }
            return sb.ToString();
        }

        private static bool ActiveInPrefab(Transform t, Transform root)
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                if (!p.gameObject.activeSelf)
                {
                    return false;
                }
                if (p == root)
                {
                    break;
                }
            }
            return true;
        }

        private static readonly Regex DrawClip = new Regex(@"draw|unholster|equip|pull ?out|take ?out|deploy|raise", RegexOptions.IgnoreCase);

        /// <summary>The name of a draw-like clip the weapon's animator is in (or blending into), or null.</summary>
        private static string DrawClipPlaying(Weapon weapon)
        {
            Animator anim = weapon != null ? weapon.animator : null;
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null)
            {
                return null;
            }
            for (int layer = 0; layer < anim.layerCount; layer++)
            {
                foreach (AnimatorClipInfo c in anim.GetCurrentAnimatorClipInfo(layer))
                {
                    if (c.clip != null && c.weight > 0.5f && DrawClip.IsMatch(c.clip.name))
                    {
                        return c.clip.name;
                    }
                }
                if (anim.IsInTransition(layer))
                {
                    foreach (AnimatorClipInfo c in anim.GetNextAnimatorClipInfo(layer))
                    {
                        if (c.clip != null && DrawClip.IsMatch(c.clip.name))
                        {
                            return c.clip.name;
                        }
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// Every measurement the run takes, summed per name, for one block at the top of the report: how improve rounds
        /// are compared from run to run.
        /// </summary>
        private static readonly SortedDictionary<string, double[]> Metrics = new SortedDictionary<string, double[]>(StringComparer.Ordinal);

        /// <summary>Every measured hand (what, points buried in the model): the worst are listed for the next fixes.</summary>
        private static readonly List<KeyValuePair<string, int>> Worst = new List<KeyValuePair<string, int>>();

        internal static void Metric(string name, double value)
        {
            if (double.IsNaN(value) || value < 0)
            {
                return;
            }
            if (!Metrics.TryGetValue(name, out double[] m))
            {
                Metrics[name] = m = new double[] { 0, 0, double.MinValue };
            }
            m[0] += value;
            m[1]++;
            m[2] = Math.Max(m[2], value);
        }

        private static float _matchAt, _deployedAt;

        /// <summary>Where the header's count of skipped checks goes once the report is built.</summary>
        private const string SkippedToken = "<skipped>";

        private static void Write(bool final = true)
        {
            _written |= final;
            var sb = new StringBuilder();
            sb.AppendLine($"Ravenfield VR {PluginInfo.Version} autotest, {DateTime.Now:yyyy-MM-dd HH:mm}, VR {(VRManager.Active ? "on" : "off")}");
            // And how many checks were skipped (counted from the report's own lines once it is built): a run whose soldier checks all
            // skipped (the soldier not brought, the off hand busy) read "Problems: 0" as if it had tested them (sweep, 2026-10-29).
            sb.AppendLine($"Problems: {Problems.Count}, weapons reported: {_tested}, " + SkippedToken); // (a run that tested nothing read as clean)
            // Where a run's time goes (the fast test mode is judged by it; seconds after the game started).
            sb.AppendLine($"Timeline: content ready {Core.TestHost.ContentReadyAt:0.0} s{(Core.TestHost.Mods != null ? " (fast test mode)" : "")}, match "
                          + $"{_matchAt:0.0} s, player deployed {_deployedAt:0.0} s, this report {Time.realtimeSinceStartup:0.0} s");
            foreach (string p in Problems)
            {
                sb.AppendLine("  ! " + p);
            }
            sb.AppendLine();
            // Written as they stand (the report is rewritten as the run goes on).
            sb.AppendLine($"Frames drawn with a new weapon not yet in the VR hand: {WeaponSystem.UnhandledFrames}; with the weapon's own (flat) arms showing: {WeaponSystem.ArmFrames}");
            if (Metrics.Count > 0)
            {
                sb.AppendLine("Measurements (total / count / average / max)");
                foreach (KeyValuePair<string, double[]> m in Metrics)
                {
                    sb.AppendLine($"  {m.Key,-44} {m.Value[0],9:0.##} {m.Value[1],5} {m.Value[0] / Math.Max(1, m.Value[1]),9:0.###} {m.Value[2],9:0.##}");
                }
                if (Worst.Count > 0)
                {
                    sb.AppendLine("  Hands most buried in what they hold (of 72 points):");
                    foreach (KeyValuePair<string, int> w in Worst.Where(w => w.Value > 0).OrderByDescending(w => w.Value).Take(5))
                    {
                        sb.AppendLine($"    {w.Value,3}  {w.Key}");
                    }
                }
                sb.AppendLine();
            }
            sb.AppendLine("Performance");
            sb.AppendLine(PerfProbe.Report());
            sb.AppendLine("Results");
            foreach (string r in Results)
            {
                sb.AppendLine("  " + r);
            }
            int skipped = 0;
            var skippedNames = new List<string>();
            foreach (string reportLine in sb.ToString().Split('\n'))
            {
                int at = reportLine.IndexOf(": skipped", StringComparison.OrdinalIgnoreCase);
                if (at > 0)
                {
                    skipped++;
                    string what = reportLine.Substring(0, at).Trim().TrimStart('!', ' ');
                    if (skippedNames.Count < 12 && !skippedNames.Contains(what))
                    {
                        skippedNames.Add(what);
                    }
                }
            }
            sb.Replace(SkippedToken, $"checks skipped: {skipped}{(skippedNames.Count > 0 ? " (" + string.Join("; ", skippedNames.ToArray()) + (skipped > skippedNames.Count ? "; ..." : "") + ")" : "")}");
            try
            {
                File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
                if (final)
                {
                    Log.Info($"Autotest finished: {Problems.Count} problems, {skipped} checks skipped. Report: {ReportPath}");
                }
            }
            catch (Exception e)
            {
                Log.Exception("Writing the autotest report failed", e);
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnAllContentLoaded))]
    internal static class AutoTestMutatorsPatch
    {
        private static void Prefix() => AutoTest.EnableAllMutators();
    }

    /// <summary>
    /// Autotest: a small match (four soldiers a side), enough for weapons to be dropped by the fallen and few enough that
    /// the checks are not interrupted by firefights.
    /// </summary>
    [HarmonyPatch(typeof(GameManager), nameof(GameManager.StartLevel))]
    internal static class AutoTestMatchSizePatch
    {
        private static void Prefix(GameModeParameters parameters)
        {
            if (!AutoTest.Requested)
            {
                return;
            }
            GameModeParameters p = parameters ?? GameManager.instance?.gameModeParameters;
            if (p?.configuration == null)
            {
                return;
            }
            p.configuration.SetInt("countTeam0", 4);
            p.configuration.SetInt("countTeam1", 4);
        }
    }

    /// <summary>Autotest: the player cannot be hurt (being shot dead cut checks short and forced respawns).</summary>
    [HarmonyPatch(typeof(Actor), nameof(Actor.Damage))]
    internal static class AutoTestInvulnerablePatch
    {
        private static bool Prefix(Actor __instance, ref bool __result)
        {
            if (AutoTest.Requested && __instance != null && !__instance.aiControlled)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>Self-test: counts the game's footsteps and landings for the player (nothing else).</summary>
    [HarmonyPatch(typeof(UnityStandardAssets.Characters.FirstPerson.FirstPersonController))]
    internal static class CountFootstepsPatch
    {
        [HarmonyPrefix, HarmonyPatch("PlayFootStepAudio")]
        private static void Step() => AutoTest.Footsteps++;

        [HarmonyPrefix, HarmonyPatch("PlayLandingSound")]
        private static void Landing() => AutoTest.Footsteps++;
    }
}
