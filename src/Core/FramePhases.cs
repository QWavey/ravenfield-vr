using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.LowLevel;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// Where a frame's time goes, for the once-a-minute frame rate line (FrameBudget): a time stamp at the start of each of the
    /// player loop's top-level phases (the time from one to the next is the phase's), and each camera's render on the main thread
    /// (its pre-cull to its last post-render). Play ran CPU-bound (13.3 ms a frame with the GPU at 3.8, 34 mods, 2026-10-27) and
    /// the mod's own sections were 0.8 ms of it: this says which of the engine's phases, and which camera, the rest is. Always
    /// on, a stamp per phase, no garbage.
    /// </summary>
    internal static class FramePhases
    {
        private struct Marker { }

        // One slot per marker: the start of each top-level phase, and of each step inside it (a slot's time runs to the next stamp).
        private static string[] _names = new string[0];
        private static int[] _phaseOf = new int[0];
        private static long[] _ticks = new long[0];
        private static string[] _phaseNames = new string[0];
        private static double[] _phaseMs = new double[0];
        private static int[] _order = new int[0];
        private static int _last = -1;
        private static long _lastStamp;
        private static int _frames;
        private static bool _installed;
        /// <summary>
        /// This frame is play, as the frame rate line counts it (in a match, no menu, not paused): only such frames are timed, the
        /// loading screen's and the menus' would have mixed into the averages (round-3 scout, 2026-10-28).
        /// </summary>
        private static bool _counting;
        private static int _reports;

        private sealed class CameraTime
        {
            public string Name;
            public long Ticks;
            public long Start;
            public int Renders;
        }

        private static readonly Dictionary<Camera, CameraTime> Cameras = new Dictionary<Camera, CameraTime>();

        // The mod's own guarded sections (Log.Guard), time only, in every session: the autotest's PerfProbe names them, but only in
        // its runs, and the user's play spent 0.8-1.3 ms a frame on the mod against the autotest's 0.4-0.5 (2026-10-27).
        private sealed class SectionTime
        {
            public string Name;
            public long Ticks;
            public long Start;
        }

        private static readonly Dictionary<string, SectionTime> Sections = new Dictionary<string, SectionTime>();
        private static readonly List<SectionTime> SortedSections = new List<SectionTime>();
        private static int _mainThread = -1;

        internal static void SectionBegin(string name)
        {
            if (_mainThread < 0 || !_counting || System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThread)
            {
                return; // (not installed yet, a worker's, or not a play frame)
            }
            if (!Sections.TryGetValue(name, out SectionTime s))
            {
                s = Sections[name] = new SectionTime { Name = name };
            }
            s.Start = Stopwatch.GetTimestamp();
        }

        internal static void SectionEnd(string name)
        {
            if (_mainThread < 0 || System.Threading.Thread.CurrentThread.ManagedThreadId != _mainThread || !Sections.TryGetValue(name, out SectionTime s) || s.Start == 0)
            {
                return;
            }
            s.Ticks += Stopwatch.GetTimestamp() - s.Start;
            s.Start = 0;
        }
        private static readonly List<CameraTime> Sorted = new List<CameraTime>();

        /// <summary>The markers put into the player loop (once: a later SetPlayerLoop by the game or a mod drops them, and the line says so).</summary>
        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            _mainThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (root.subSystemList == null)
                {
                    return;
                }
                int n = root.subSystemList.Length;
                var names = new List<string>();
                var phaseOf = new List<int>();
                _phaseNames = new string[n];
                _phaseMs = new double[n];
                for (int i = 0; i < n; i++)
                {
                    PlayerLoopSystem phase = root.subSystemList[i];
                    _phaseNames[i] = phase.type != null ? phase.type.Name : "phase " + i;
                    var list = new List<PlayerLoopSystem>();
                    list.Add(MarkerFor(names.Count, i == 0));
                    names.Add(_phaseNames[i] + " (own)");
                    phaseOf.Add(i);
                    foreach (PlayerLoopSystem step in phase.subSystemList ?? new PlayerLoopSystem[0])
                    {
                        list.Add(MarkerFor(names.Count, false));
                        names.Add(step.type != null ? step.type.Name : "?");
                        phaseOf.Add(i);
                        list.Add(step);
                    }
                    phase.subSystemList = list.ToArray();
                    root.subSystemList[i] = phase;
                }
                _names = names.ToArray();
                _phaseOf = phaseOf.ToArray();
                _ticks = new long[_names.Length];
                _order = new int[_names.Length];
                PlayerLoop.SetPlayerLoop(root);
                Camera.onPreCull += PreCull;
                Camera.onPostRender += PostRender;
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("FramePhases.Install", e);
            }
        }

        private static PlayerLoopSystem MarkerFor(int slot, bool frameStart) =>
            new PlayerLoopSystem { type = typeof(Marker), updateDelegate = () => Stamp(slot, frameStart) };

        private static void Stamp(int slot, bool frameStart)
        {
            long now = Stopwatch.GetTimestamp();
            if (_last >= 0 && _counting)
            {
                _ticks[_last] += now - _lastStamp;
            }
            if (frameStart)
            {
                _counting = Util.GameContext.InGame && !Util.GameContext.GameMenuOpen && Time.timeScale > 0f;
                if (_counting)
                {
                    _frames++;
                }
            }
            _last = slot;
            _lastStamp = now;
        }

        private static void PreCull(Camera c)
        {
            if (c == null || !_counting)
            {
                return;
            }
            if (!Cameras.TryGetValue(c, out CameraTime t))
            {
                t = Cameras[c] = new CameraTime { Name = c.name };
            }
            t.Start = Stopwatch.GetTimestamp();
            t.Renders++;
        }

        private static void PostRender(Camera c)
        {
            // (Only in play frames, as PreCull: a menu's or a pause's frames added their whole interval to each camera; round-6 scout.)
            if (!_counting || c == null || !Cameras.TryGetValue(c, out CameraTime t) || t.Start == 0)
            {
                return;
            }
            long now = Stopwatch.GetTimestamp();
            t.Ticks += now - t.Start;
            t.Start = now; // (a second eye's post-render adds its own part)
        }

        /// <summary>The window's breakdown (ms a frame per phase, the costliest cameras), then counted afresh.</summary>
        internal static string TakeReport()
        {
            if (_frames == 0)
            {
                // (The markers dropped by a later SetPlayerLoop, or no play: what was gathered is let go all the same, or the camera
                // table grew with every camera of every map.)
                Cameras.Clear();
                Sections.Clear();
                System.Array.Clear(_ticks, 0, _ticks.Length);
                return "phases not timed";
            }
            double perMs = 1000.0 / Stopwatch.Frequency / _frames;
            var s = new System.Text.StringBuilder("phases a frame:");
            System.Array.Clear(_phaseMs, 0, _phaseMs.Length);
            for (int k = 0; k < _names.Length; k++)
            {
                _phaseMs[_phaseOf[k]] += _ticks[k] * perMs;
                _order[k] = k;
            }
            for (int i = 0; i < _phaseNames.Length; i++)
            {
                s.Append(' ').Append(_phaseNames[i]).Append(' ').Append(_phaseMs[i].ToString("0.00"));
            }
            // The costliest steps whatever their phase (where a phase's time is spent).
            System.Array.Sort(_order, (a, b) => _ticks[b].CompareTo(_ticks[a]));
            s.Append("; costliest steps:");
            for (int k = 0; k < _order.Length && k < 10; k++)
            {
                int slot = _order[k];
                s.Append(' ').Append(_names[slot]).Append(' ').Append((_ticks[slot] * perMs).ToString("0.00")).Append(',');
            }
            System.Array.Clear(_ticks, 0, _ticks.Length);
            Sorted.Clear();
            foreach (KeyValuePair<Camera, CameraTime> kv in Cameras)
            {
                if (kv.Value.Renders > 0)
                {
                    Sorted.Add(kv.Value);
                }
            }
            Sorted.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
            s.Append("; cameras a frame (main thread):");
            for (int i = 0; i < Sorted.Count && i < 8; i++)
            {
                CameraTime t = Sorted[i];
                s.Append(' ').Append(t.Name).Append(' ').Append((t.Ticks * perMs).ToString("0.00")).Append(" ms (")
                 .Append(((float)t.Renders / _frames).ToString("0.#")).Append("x),");
            }
            s.Append($" {Sorted.Count} camera(s) rendered");
            // The eyes' depth texture (copied from the game's camera): on, the forward renderer draws every opaque object once more per
            // eye, so it is worth seeing whether anything still asks for it with post-processing off.
            Camera eyes = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Camera : null;
            if (eyes != null && eyes.depthTextureMode != DepthTextureMode.None)
            {
                s.Append($" (the eyes draw a {eyes.depthTextureMode} texture)");
            }
            // The mod's costliest sections (inclusive: a section inside another counts in both).
            SortedSections.Clear();
            foreach (KeyValuePair<string, SectionTime> kv in Sections)
            {
                if (kv.Value.Ticks > 0)
                {
                    SortedSections.Add(kv.Value);
                }
            }
            SortedSections.Sort((a, b) => b.Ticks.CompareTo(a.Ticks));
            s.Append("; the mod's costliest sections:");
            for (int i = 0; i < SortedSections.Count && i < 6; i++)
            {
                s.Append(' ').Append(SortedSections[i].Name).Append(' ').Append((SortedSections[i].Ticks * perMs).ToString("0.00")).Append(',');
            }
            foreach (KeyValuePair<string, SectionTime> kv in Sections)
            {
                kv.Value.Ticks = 0;
            }
            // What the scene holds (a scan once a minute): a session whose frames grew slower minute by minute (a 70-gun autotest,
            // 7.8 to 25.5 ms, every phase two to four times dearer, physics most; 2026-10-27) tells here whether bodies, renderers
            // or cameras piled up, or the same scene simply ran slower (a hot laptop).
            // The scene's counts every fifth report only: walking every renderer (5000 in the user's session) was a frame of its own.
            if (_reports++ % 5 == 0)
            {
                try
                {
                    Rigidbody[] bodies = Object.FindObjectsOfType<Rigidbody>();
                    int awake = 0;
                    foreach (Rigidbody b in bodies)
                    {
                        awake += b != null && !b.isKinematic && !b.IsSleeping() ? 1 : 0;
                    }
                    s.Append($"; scene: {bodies.Length} bodies ({awake} awake), {Object.FindObjectsOfType<Renderer>().Length} renderers, "
                             + $"{Camera.allCamerasCount} cameras on, {System.GC.GetTotalMemory(false) / (1024 * 1024)} MB managed");
                    UI.CanvasManager canvases = UI.UISystem.Instance != null ? UI.UISystem.Instance.Canvases : null;
                    if (canvases != null)
                    {
                        s.Append(", ").Append(canvases.Counts()); // (what the UI camera draws: its time is in the cameras above)
                    }
                }
                catch (System.Exception)
                {
                    // (counted next time)
                }
            }
            Cameras.Clear(); // (cameras of an old map forgotten with the window)
            _frames = 0;
            return s.ToString();
        }
    }
}
