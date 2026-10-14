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

        private static readonly bool MenuTestRequested = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-menutest", StringComparison.OrdinalIgnoreCase));

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
            while (Time.realtimeSinceStartup - start < 10f)
            {
                frames.Add(Time.unscaledDeltaTime * 1000f);
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
                    yield return Run();
                    yield break;
                }
                yield return null;
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
                yield return new WaitForSecondsRealtime(0.08f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("damage-from-front-left");
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
            List<WeaponManager.WeaponEntry> entries = WeaponManager.instance.allWeapons.Where(e => e != null && e.prefab != null).ToList();
            Results.Add($"Weapons to test: {entries.Count} (VR {(VRManager.Active ? "on" : "off")})");
            if (VRManager.Active)
            {
                yield return BodyShots();
            }
            int every = Mathf.Max(1, entries.Count / 28);
            int recoilRifles = 0, recoilPistols = 0, leftRifles = 0, leftPistols = 0, rackRifles = 0, rackPistols = 0, rackManual = 0;
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
            int index = 0, meleeShots = 0, throwShots = 0, pistolShots = 0, launcherShots = 0, manualShots = 0;
            bool tacticalDone = false, dropDone = false, pistolPouchDone = false;
            foreach (WeaponManager.WeaponEntry entry in entries)
            {
                index++;
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
                                continue;
                            }
                        }
                        // Rack grips on rifles, pistols and pump / bolt guns (a few of each), not only the first rifles.
                        if (want && (action == ActionType.Manual ? rackManual++ < 3
                                : action == ActionType.SelfLoading && (held.Rig.Data.IsPistol ? rackPistols++ < 3 : rackRifles++ < 4)))
                        {
                            int rackIndex = index;
                            yield return held.Reload.RackGripSelfCheck(lines.Add, name =>
                            {
                                Shots.Capture($"{name}-{rackIndex:000}-{entry.name}");
                                Rig.VRHand rackHand = Rig.VRRig.Instance.Off;
                                Vector3 c = rackHand.Model.Root.position;
                                Runtime.RigidPose g = held.GripWorld;
                                Vector3 up = g.rotation * Vector3.up, right = g.rotation * Vector3.right;
                                Shots.Capture($"{name}-{rackIndex:000}-{entry.name}-left", c - right * 0.25f + up * 0.08f, Quaternion.LookRotation(c - (c - right * 0.25f + up * 0.08f), up), 45f);
                                Shots.Capture($"{name}-{rackIndex:000}-{entry.name}-right", c + right * 0.25f + up * 0.08f, Quaternion.LookRotation(c - (c + right * 0.25f + up * 0.08f), up), 45f);
                            });
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
                        if (!tacticalDone && held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag && !held.Rig.Data.IsPistol && held.Rig.Data.MagKickable)
                        {
                            tacticalDone = true;
                            yield return held.Reload.TacticalSelfCheck(lines.Add, name => Shots.Capture(name));
                            yield return held.Reload.H3vrReloadSelfCheck(lines.Add, name =>
                            {
                                Shots.Capture(name);
                                MagHandCloseups(name, lines.Add);
                            });
                        }
                        if (!pistolPouchDone && held.Rig.Data.IsPistol && held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag)
                        {
                            // A pistol's magazine from the pouch too (pistol magazines were shown low and hard to take).
                            pistolPouchDone = true;
                            yield return PouchThrowCheck(lines.Add);
                        }
                        if (!dropDone && tacticalDone && action == ActionType.SelfLoading && !held.Rig.Data.IsPistol)
                        {
                            dropDone = true;
                            yield return PouchThrowCheck(lines.Add);
                            yield return DropCheck(lines.Add);
                            yield return ReachCheck(lines.Add);
                            yield return GroundDriftCheck(lines.Add);
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
                    float until = Time.realtimeSinceStartup + 3f;
                    while (ready != null && !ready.unholstered && Time.realtimeSinceStartup < until)
                    {
                        yield return null;
                    }
                    bool counts = held != null && held.Weapon == weapon && held.ActiveAmmo != null && held.ActiveAmmo.Participates
                                  && held.Rig.Data.Action != ActionType.Throwable;
                    _shots = 0;
                    float shotKick = 0f, shotBack = 0f, shotImpulse = 0f;
                    Weapon listened = counts ? ready : null;
                    listened?.onSpawnProjectiles.AddListener(CountShot);
                    for (int i = 0; i < 3; i++)
                    {
                        held?.ResetKick(); // each shot on its own: shots that stack up are not one shot's kick
                        try
                        {
                            Weapon active = weapon.GetActiveSubWeapon();
                            active.Fire(active.CurrentMuzzle().forward, true);
                            active.StopFire();
                        }
                        catch (Exception e)
                        {
                            Problem($"[{entry.name}] fire: {e.GetType().Name}: {e.Message} {FirstFrames(e)}");
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
                    listened?.onSpawnProjectiles.RemoveListener(CountShot);
                    if (counts)
                    {
                        lines.Add($"fired {Mathf.Min(_shots, 3)}/3");
                        if (_shots == 0)
                        {
                            Problem($"[{entry.name}] DID NOT FIRE (counter {held.ActiveAmmo.CounterText()})");
                        }
                        else if (VRManager.Active && held.Rig.Data.HasSupportPoint && (held.Rig.Data.IsPistol ? recoilPistols++ < 2 : recoilRifles++ < 4))
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
                foreach (string l in lines)
                {
                    if (Failure.IsMatch(l) && !l.Contains("LIVE GRENADE WITH") && !l.Contains("NOT REFUNDED") && !l.Contains("NO GRENADE"))
                    {
                        Problem($"[{entry.name}] {l}");
                    }
                }
                Results.Add($"{entry.name}: {(weapon != null ? "equipped" : "NOT EQUIPPED")}{(lines.Count > 0 ? "; " + string.Join("; ", lines.ToArray()) : "")}");
                if (++_tested % 25 == 0)
                {
                    Write(final: false); // progress on disk in case the run is cut short
                }
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

        /// <summary>Self-check lines flag failures in capitals: NOT SEATED, NOTHING CHAMBERED, DROPPED MAGAZINE NOT GRABBABLE...</summary>
        private static readonly Regex Failure = new Regex(@"\b(NOT|NOTHING|NO) [A-Z]");

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
}
