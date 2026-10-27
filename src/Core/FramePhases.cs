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

        private static string[] _names = new string[0];
        private static long[] _ticks = new long[0];
        private static int _last = -1;
        private static long _lastStamp;
        private static int _frames;
        private static bool _installed;

        private sealed class CameraTime
        {
            public string Name;
            public long Ticks;
            public long Start;
            public int Renders;
        }

        private static readonly Dictionary<Camera, CameraTime> Cameras = new Dictionary<Camera, CameraTime>();
        private static readonly List<CameraTime> Sorted = new List<CameraTime>();

        /// <summary>The markers put into the player loop (once: a later SetPlayerLoop by the game or a mod drops them, and the line says so).</summary>
        internal static void Install()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            try
            {
                PlayerLoopSystem root = PlayerLoop.GetCurrentPlayerLoop();
                if (root.subSystemList == null)
                {
                    return;
                }
                int n = root.subSystemList.Length;
                _names = new string[n];
                _ticks = new long[n];
                for (int i = 0; i < n; i++)
                {
                    PlayerLoopSystem phase = root.subSystemList[i];
                    _names[i] = phase.type != null ? phase.type.Name : "phase " + i;
                    int index = i;
                    var marker = new PlayerLoopSystem { type = typeof(Marker), updateDelegate = () => Stamp(index) };
                    var list = new List<PlayerLoopSystem>(phase.subSystemList ?? new PlayerLoopSystem[0]);
                    list.Insert(0, marker);
                    phase.subSystemList = list.ToArray();
                    root.subSystemList[i] = phase;
                }
                PlayerLoop.SetPlayerLoop(root);
                Camera.onPreCull += PreCull;
                Camera.onPostRender += PostRender;
            }
            catch (System.Exception e)
            {
                Log.ExceptionOnce("FramePhases.Install", e);
            }
        }

        private static void Stamp(int phase)
        {
            long now = Stopwatch.GetTimestamp();
            if (_last >= 0)
            {
                _ticks[_last] += now - _lastStamp;
            }
            if (phase == 0)
            {
                _frames++;
            }
            _last = phase;
            _lastStamp = now;
        }

        private static void PreCull(Camera c)
        {
            if (c == null)
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
            if (c == null || !Cameras.TryGetValue(c, out CameraTime t) || t.Start == 0)
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
                return "phases not timed";
            }
            double perMs = 1000.0 / Stopwatch.Frequency / _frames;
            var s = new System.Text.StringBuilder("phases a frame:");
            for (int i = 0; i < _names.Length; i++)
            {
                s.Append(' ').Append(_names[i]).Append(' ').Append((_ticks[i] * perMs).ToString("0.00"));
                _ticks[i] = 0;
            }
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
            Cameras.Clear(); // (cameras of an old map forgotten with the window)
            _frames = 0;
            return s.ToString();
        }
    }
}
