using System;
using System.Collections;
using System.Collections.Generic;
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
            float deadline = Time.realtimeSinceStartup + 120f;
            while (LocalPlayer.actor == null && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            yield return new WaitForSecondsRealtime(4f);
            _context = "deploy";
            yield return Deploy();
            if (LocalPlayer.actor == null || LocalPlayer.actor.dead)
            {
                Results.Add("Could not deploy the player; weapon tests skipped.");
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
                if (VRManager.Active && UiShots)
                {
                    yield return SquadPointingCheck();
                    yield return LoadoutPickerCheck();
                    yield return PauseMenuCheck();
                    yield return WristViewCheck();
                    yield return BodyShots();
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
                    yield return WristViewCheck();
                }
                PerfProbe.Reset();
                yield return TestWeapons();
                _context = "idle";
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
            }
            report($"{name}: magazine hand {magHand}");
        }

        /// <summary>
        /// The same weapon held left-handed (the setting overridden in memory only): holstered, handedness switched,
        /// drawn into the left hand, pictured and measured, then back.
        /// </summary>
        /// <summary>
        /// The game's pause menu: the laser aimed at each of its buttons in turn (by the dominant hand), and what it is over
        /// then. Every button must be what the laser hits (only one of them took clicks in the headset). Nothing is clicked.
        /// </summary>
        /// <summary>
        /// The graphics bench (task 215): standing still in the match, each preset and then Ultra with one setting lowered
        /// at a time, 2 s to settle and 5 s measured: the frame time and the GPU time SteamVR's compositor measured. The
        /// saved settings are never changed (the player's are put back at the end).
        /// </summary>
        private static IEnumerator PerfBench()
        {
            UI.UISystem ui = UI.UISystem.Instance;
            var runs = new List<KeyValuePair<string, Func<string>>>
            {
                new KeyValuePair<string, Func<string>>("Medium", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Medium)),
                new KeyValuePair<string, Func<string>>("High", () => GraphicsPresets.ApplyForBench(GraphicsPreset.High)),
                new KeyValuePair<string, Func<string>>("Ultra", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra)),
                new KeyValuePair<string, Func<string>>("Ultra, reflections off", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, reflections: false)),
                new KeyValuePair<string, Func<string>>("Ultra, 2 cascades", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, cascades: 2)),
                new KeyValuePair<string, Func<string>>("Ultra, shadows 100 m", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, shadowDistance: 100f)),
                new KeyValuePair<string, Func<string>>("Ultra, shadow map High", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, shadowResolution: ShadowResolution.High)),
                new KeyValuePair<string, Func<string>>("Ultra, MSAA 2x", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, msaa: 2)),
                new KeyValuePair<string, Func<string>>("Ultra, scale 1.2", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, renderScale: 1.2f)),
                new KeyValuePair<string, Func<string>>("Ultra, 4 pixel lights", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, pixelLights: 4)),
                new KeyValuePair<string, Func<string>>("Ultra, LOD bias 1.5", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra, lodBias: 1.5f)),
                new KeyValuePair<string, Func<string>>("Ultra, no UI camera", () =>
                {
                    string d = GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra);
                    if (ui != null && ui.UiCamera != null)
                    {
                        ui.UiCamera.enabled = false;
                    }
                    return d + ", UI camera off";
                }),
                new KeyValuePair<string, Func<string>>("Ultra again", () => GraphicsPresets.ApplyForBench(GraphicsPreset.Ultra)),
            };
            Results.Add("Graphics bench (standing still; frame = CPU frame interval, GPU = the compositor's timing of our frames):");
            try
            {
                foreach (KeyValuePair<string, Func<string>> run in runs)
                {
                    if (ui != null && ui.UiCamera != null)
                    {
                        ui.UiCamera.enabled = true;
                    }
                    string applied = run.Value();
                    yield return new WaitForSecondsRealtime(2f);
                    PerfProbe.Reset();
                    yield return new WaitForSecondsRealtime(5f);
                    string summary = PerfProbe.Summary(out float cpu, out float gpu);
                    Results.Add($"  {run.Key,-26} {summary}   [{applied}]");
                    Metric($"bench {run.Key}: GPU ms", gpu);
                    Metric($"bench {run.Key}: frame ms", cpu);
                    Log.Info($"Bench {run.Key}: {summary}");
                }
            }
            finally
            {
                if (ui != null && ui.UiCamera != null)
                {
                    ui.UiCamera.enabled = true;
                }
                GraphicsPresets.Apply();
                Runtime.OpenVRRuntime.ApplyLiveSettings();
            }
        }

        /// <summary>The grab pictures of one weapon (see <see cref="GrabShots"/>).</summary>
        private static IEnumerator GrabPictures(HeldWeapon held, int index, string name, List<string> lines)
        {
            yield return new WaitForEndOfFrame();
            Shots.Capture($"weapon-{index:000}-{name}");
            ActionType action = held.Rig.Data.Action;
            if (action == ActionType.Manual || action == ActionType.SelfLoading || action == ActionType.ShellFed)
            {
                Vector3? rackCam = null;
                yield return held.Reload.RackGripSelfCheck(lines.Add, shot =>
                {
                    Shots.Capture($"{shot}-{index:000}-{name}");
                    RackCloseUps($"{shot}-{index:000}-{name}", held, ref rackCam);
                });
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
        /// An automatic held on the trigger for 1.6 s (Tasks 209 A-C): the lens flash limiter must keep each eye within
        /// six counted steps (three flashes) in any 1.1 s, as flash_sim.py's photosensitivity test, the rest soft flashes
        /// and the glow; the glow pictured a second in.
        /// </summary>
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
                if (shaky)
                {
                    Problem($"[{name}] held still ({how}) the weapon shakes: up to {maxA:0.00} deg and {maxM * 1000f:0.0} mm between frames (average {avgA:0.000} deg, {avgM * 1000f:0.00} mm)");
                }
                return $"{how}: up to {maxA:0.00} deg / {maxM * 1000f:0.0} mm a frame (average {avgA:0.000} deg / {avgM * 1000f:0.00} mm){(shaky ? " (SHAKES)" : "")}";
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
                    angles.Add(Quaternion.Angle(lastRot, body.rotation));
                    moves.Add(Vector3.Distance(lastPos, pos));
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
        /// The gun hand lets go while the other hand holds the handguard: the weapon stays drawn, in the other hand, taken
        /// by its grip, and that hand is the gun hand. Then given back.
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
            passed = sys.TestPassToOtherHand();
            yield return null;
            yield return null;
            yield return new WaitForSecondsRealtime(0.3f);
            HeldWeapon now = sys.Held;
            bool same = now != null && now.Weapon == weapon;
            bool roles = rig.Dominant == to && to.Role == Rig.HandRole.Weapon && from.Role == Rig.HandRole.Free;
            float gap = same ? Vector3.Distance(now.GripWorld.position, to.PalmCentre) : -1f;
            yield return new WaitForEndOfFrame();
            Shots.Capture($"pass-{name}");
            report($"passed to the other hand: {(passed ? "yes" : "NO")}, {(same ? "the weapon still drawn" : "THE WEAPON WAS PUT AWAY")}, "
                   + $"{(roles ? "that hand holds it" : $"HANDS WRONG (gun hand {rig.Dominant.Side} {rig.Dominant.Role}, other {rig.Off.Role})")}, grip {gap * 100f:0.0} cm from its palm");
            Metric("weapon passed to the other hand: grip cm from the palm", gap * 100f);
            if (!passed || !same || !roles || gap > 0.06f)
            {
                Problem($"[{name}] passing the weapon to the other hand failed (passed {passed}, still drawn {same}, hands right {roles}, grip {gap * 100f:0.0} cm)");
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
            if (shown != 0 && !modSlide)
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

        /// <summary>Aims the laser at the pause menu's button with this label and clicks it with the trigger, wobbling 1.5 degrees.</summary>
        private static IEnumerator ClickMenuButton(Rig.VRHand dom, string label, List<string> lines)
        {
            int side = (int)dom.Side;
            UnityEngine.UI.Button button = null;
            foreach (UnityEngine.UI.Button b in IngameMenuUi.instance.GetComponentsInChildren<UnityEngine.UI.Button>(false))
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
                        yield return null;
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                    yield return new WaitForEndOfFrame();
                    Transform h = rig.Head;
                    Shots.Capture($"{name}-mode{w.GetSightModeIndex()}", h.position + h.right * 0.032f, h.rotation, 50f);
                    Camera rt = w.renderTextureCamera;
                    string cam = rt == null ? "no scope camera of its own"
                        : $"its scope camera '{rt.name}' {(rt.enabled && rt.gameObject.activeInHierarchy ? "on" : "OFF")}, drawing into {(rt.targetTexture != null ? $"'{rt.targetTexture.name}'" : "THE SCREEN")}";
                    report($"sight mode {w.GetSightModeIndex()}: {(held.Aiming ? "aiming" : "NOT AIMING (the eye is not on its line)")}, game aiming {w.GetActiveSubWeapon().aiming}, {cam}, {held.ScopeLensState()}");
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
            if (slot < 0 || VRConfig.Hands.Value == HandsMode.WeaponArms)
            {
                yield break;
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
                report($"weapon's arms: gun hand {gunErr * 100f:0.0} cm off its grip, off hand {gripErr * 100f:0.0} cm off the handguard and {freeErr * 100f:0.0} cm off the controller"
                       + $"{(arms.Stretched ? " (a shoulder slid to reach)" : "")}{(torn ? " (NOT ON TARGET)" : "")}");
            }
            finally
            {
                if (VRConfig.TestHands.HasValue)
                {
                    actor.HolsterActiveWeapon();
                    VRConfig.TestHands = null;
                    actor.SwitchWeapon(slot);
                }
            }
            yield return new WaitForSecondsRealtime(1.5f); // drawn again with the VR hands
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
                    lines.Add($"a soldier walking through a magazine on the ground: {(magMoved < 0f ? "gone" : magMoved > 0.15f ? $"PUSHED {magMoved * 100f:0} cm" : $"stayed ({magMoved * 100f:0.0} cm)")}");
                    groundMag.Destroy();
                }
                Metric("dropped weapon: cm moved by a body walking through it", moved * 100f);
                lines.Add($"a soldier walking through a lying {target.Entry.name}: {(moved < 0f ? "gone" : moved > 0.15f ? $"PUSHED {moved * 100f:0} cm" : $"stayed ({moved * 100f:0.0} cm)")}");
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
                report("magazine set down gently: skipped (no magazine-fed weapon in hand or the off hand is busy)");
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
                for (int i = 0; i <= 40; i++)
                {
                    Vector3 gunAt = before + Vector3.up * 0.03f + across * Mathf.Lerp(-0.35f, 0.35f, i / 40f);
                    Rig.VRHand.Scripted[side] = new Runtime.RigidPose(gunAt - handToGun, start.rotation);
                    yield return null;
                }
                yield return new WaitForSecondsRealtime(0.3f);
                float moved = mag.Go != null ? Vector3.Distance(mag.Go.transform.position, before) : -1f;
                Metric("magazine set down gently: cm the gun pushed it", moved * 100f);
                report("magazine picked up, set down gently and the gun swept through it: "
                       + (moved < 0f ? "gone" : moved > 0.05f ? $"NOT LEFT ALONE (pushed {moved * 100f:0} cm)" : $"stayed ({moved * 100f:0.0} cm)"));
            }
            finally
            {
                Rig.VRHand.Scripted[side] = null;
                Input.VRInput.SimulatedGrab[offSide] = false;
                mag.Destroy();
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
            int recoilRifles = 0, recoilPistols = 0, leftRifles = 0, leftPistols = 0, rackRifles = 0, rackPistols = 0, rackManual = 0, passChecks = 0, slideLockChecks = 0, emptyReloadChecks = 0, keepOutChecks = 0, steadyMgs = 0, steadyRifles = 0, weightPistols = 0;
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
            int index = 0, meleeShots = 0, throwShots = 0, pistolShots = 0, launcherShots = 0, manualShots = 0, lensShots = 0;
            bool tacticalDone = false, dropDone = false, pistolPouchDone = false, sightPartsDone = false, armsDone = false, assistedDone = false;
            foreach (WeaponManager.WeaponEntry entry in entries)
            {
                // The weapon's id (its place in the game's list): the same number on its pictures in every run, however
                // the list was filtered.
                index = WeaponSelfTest.IdOf(entry);
                _context = entry.name;
                Actor actor = LocalPlayer.actor;
                if (actor == null || actor.dead)
                {
                    yield return Deploy();
                    actor = LocalPlayer.actor;
                    if (actor == null || actor.dead)
                    {
                        Results.Add($"{entry.name}: player could not respawn, stopping");
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
                    yield return held.Reload.SelfCheck(lines.Add);
                    if (VRManager.Active && GrabShots)
                    {
                        yield return GrabPictures(held, index, entry.name, lines);
                    }
                    else if (VRManager.Active)
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
                        if (want)
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
                        if (want && (Deep ? action == ActionType.Manual || action == ActionType.SelfLoading
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
                        }
                        if (want && held.Rig.Data.Hinge == HingeKind.FeedCover)
                        {
                            yield return held.Reload.CoverSelfCheck(lines.Add); // every feed cover opened by hand (the QJBs' could not be)
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
                        }
                        else if (want && held.Rig.Data.IsPistol && weightPistols++ < 1)
                        {
                            yield return WeightCheck(held, entry.name, lines.Add);
                        }
                        if (Deep && want && held.Rig.Data.HasEyePoint && action != ActionType.Melee && action != ActionType.Throwable)
                        {
                            yield return SightCheck(held, $"sight-{index:000}-{entry.name}", lines.Add);
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
                        if (Deep && held.Rig.Data.Action == ActionType.SingleShot && held.ActiveAmmo != null && !held.ActiveAmmo.UsesDetachableMag)
                        {
                            // Every launcher loaded by hand from the pouch at its muzzle (it ran only in the film, on a few,
                            // and the self-check seats the rocket with no hand at all).
                            yield return held.Reload.RocketCheck(lines.Add);
                        }
                        if (Deep && want && held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag)
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
                            yield return held.Reload.TacticalSelfCheck(lines.Add, name => Shots.Capture(name));
                            yield return held.Reload.H3vrReloadSelfCheck(lines.Add, name =>
                            {
                                Shots.Capture(name);
                                MagHandCloseups(name, lines.Add);
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
                            yield return PouchThrowCheck(lines.Add);
                            yield return DropCheck(lines.Add);
                            yield return ReachCheck(lines.Add);
                            yield return GroundDriftCheck(lines.Add);
                            yield return GentleMagazineCheck(lines.Add);
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
                            lines.Add($"cases thrown: {cases}{(cases == 0 && _shots > 0 ? " (NO CASES)" : "")}, case objects made so far {Casings.Built} for {Casings.Spawned} thrown{(Casings.Built > 40 ? " (POOL NOT REUSED)" : "")}");
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
                                if (oneImpulse <= 0f)
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
                                if (recoilLine.Contains(" - "))
                                {
                                    Problem($"[{entry.name}] {recoilLine}");
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
                VRConfig.TestReload = null;
            }
            int fired = _shots, used = before - w.ammo;
            Metric("assisted reloading: rounds used per shot (bolt and pump guns)", fired > 0 ? (double)used / fired : 0);
            report($"assisted reloading: {fired} shot(s), {used} round(s) used{(fired == 0 ? " (DID NOT FIRE)" : used < fired ? " (NOT ONE ROUND PER SHOT: endless ammo)" : "")}");
            w.ammo = before; // filled again for the checks that follow
            ammo?.AdoptWeaponAmmo();
        }

        /// <summary>
        /// Self-check lines flag failures in capitals: NOT SEATED, NOTHING CHAMBERED, NEVER LINED UP, TOO SMALL, TAKEN FROM THE
        /// BACK, STILL SHOWS... One list for the weapon lines and the films (the film's own test missed most of them).
        /// </summary>
        private static readonly Regex Failure = new Regex(@"\b(NOT|NOTHING|NO|NEVER) [A-Z]|TOO SMALL|FROM THE BACK|FROM THE SIDE|SEAT WRONG|BESIDE THE WELL|PASSES THROUGH|AHEAD OF THE HAND|STILL SHOWS|DRAWS NOTHING|FLOATS BESIDE|\(SHAKES\)|RAN FORWARD ON ITS OWN|STAYED BACK after|MOVES NOTHING");

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

        private static void Write(bool final = true)
        {
            _written |= final;
            var sb = new StringBuilder();
            sb.AppendLine($"Ravenfield VR {PluginInfo.Version} autotest, {DateTime.Now:yyyy-MM-dd HH:mm}, VR {(VRManager.Active ? "on" : "off")}");
            sb.AppendLine($"Problems: {Problems.Count}");
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
            try
            {
                File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
                if (final)
                {
                    Log.Info($"Autotest finished: {Problems.Count} problems. Report: {ReportPath}");
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
