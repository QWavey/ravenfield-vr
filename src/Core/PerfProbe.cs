using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// Measures the mod's own per-frame work (every Log.Guard section: CPU time and managed allocations) and the
    /// frame time, for -rfvr-perf and the autotest. Off by default; costs nothing then.
    /// </summary>
    public static class PerfProbe
    {
        private sealed class Section
        {
            public long Ticks, MaxTicks, Calls, Allocated, AllocatingCalls;
            public long StartTicks, StartBytes;
            public string MaxAt;
        }

        /// <summary>
        /// What the autotest is doing (the weapon under test), stamped on each section's slowest call and on the slowest frames:
        /// the report gave a 312 ms call and a 564 ms frame with nothing to say where they came from.
        /// </summary>
        private static string _context;
        private static float _contextSince;

        public static void SetContext(string what)
        {
            _context = what;
            _contextSince = Time.realtimeSinceStartup;
        }

        private static string Where => _context == null ? null : $"{_context} +{Time.realtimeSinceStartup - _contextSince:0.0} s";

        private const int SlowestKept = 6;
        private static readonly List<KeyValuePair<float, string>> Slowest = new List<KeyValuePair<float, string>>(SlowestKept + 1);

        public static bool Enabled;
        private static readonly Dictionary<string, Section> Sections = new Dictionary<string, Section>();
        private static readonly List<float> FrameTimes = new List<float>(20000);
        /// <summary>The GPU time SteamVR's compositor measured for each of our frames (its own last frame's timing).</summary>
        private static readonly List<float> GpuTimes = new List<float>(20000);
        private static uint _lastTimingFrame;
        private static uint _missed, _dropped, _reprojected, _timedFrames;
        private static int _gcStart = -1;

        /// <summary>A part of a section timed on its own, only in a profiled run (no cost otherwise): Begin when on, End after.</summary>
        public static bool Part(string name)
        {
            if (!Enabled)
            {
                return false;
            }
            Begin(name);
            return true;
        }

        public static void Begin(string name)
        {
            if (!Sections.TryGetValue(name, out Section s))
            {
                s = new Section();
                Sections[name] = s;
            }
            s.StartBytes = GC.GetTotalMemory(false);
            s.StartTicks = Stopwatch.GetTimestamp();
        }

        public static void End(string name)
        {
            long now = Stopwatch.GetTimestamp();
            if (!Sections.TryGetValue(name, out Section s))
            {
                return;
            }
            long ticks = now - s.StartTicks;
            s.Ticks += ticks;
            if (ticks > s.MaxTicks)
            {
                s.MaxTicks = ticks;
                s.MaxAt = Where; // a string only on a new slowest call, which stops happening once the run has warmed up
            }
            s.Calls++;
            long bytes = GC.GetTotalMemory(false) - s.StartBytes;
            if (bytes > 0)
            {
                s.Allocated += bytes; // a collection inside the section shows as negative and is skipped
                s.AllocatingCalls++;
            }
        }

        public static void Frame()
        {
            if (!Enabled)
            {
                return;
            }
            if (_gcStart < 0)
            {
                _gcStart = GC.CollectionCount(0);
            }
            float frameMs = Time.unscaledDeltaTime * 1000f;
            FrameTimes.Add(frameMs);
            if (Slowest.Count < SlowestKept || frameMs > Slowest[Slowest.Count - 1].Key)
            {
                // (A loop, not FindIndex with a lambda: capturing frameMs allocated a closure every probed frame, garbage
                // counted in the very GC figures this probe reports.)
                int at = 0;
                while (at < Slowest.Count && Slowest[at].Key >= frameMs)
                {
                    at++;
                }
                Slowest.Insert(at, new KeyValuePair<float, string>(frameMs, Where ?? "-"));
                if (Slowest.Count > SlowestKept)
                {
                    Slowest.RemoveAt(SlowestKept);
                }
            }
            SampleCompositor();
        }

        /// <summary>
        /// The compositor's timing of the last frame it got from us: how long the GPU took to draw it (both eyes, the
        /// game's cameras included), and whether it was shown late, dropped or reprojected. The mod's own sections only
        /// measure its main-thread work; this is what the headset's frame rate actually comes from.
        /// </summary>
        private static void SampleCompositor()
        {
            try
            {
                Valve.VR.CVRCompositor comp = Valve.VR.OpenVR.Compositor;
                if (comp == null)
                {
                    return;
                }
                var t = new Valve.VR.Compositor_FrameTiming { m_nSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Valve.VR.Compositor_FrameTiming)) };
                if (!comp.GetFrameTiming(ref t, 0) || t.m_nFrameIndex == _lastTimingFrame)
                {
                    return;
                }
                _lastTimingFrame = t.m_nFrameIndex;
                _timedFrames++;
                if (t.m_flTotalRenderGpuMs > 0f)
                {
                    GpuTimes.Add(t.m_flTotalRenderGpuMs);
                }
                _missed += t.m_nNumMisPresented;
                _dropped += t.m_nNumDroppedFrames;
                _reprojected += t.m_nReprojectionFlags != 0 ? 1u : 0u;
            }
            catch (Exception)
            {
                // no compositor (flat mode): nothing to sample
            }
        }

        public static void Reset()
        {
            Sections.Clear();
            FrameTimes.Clear();
            GpuTimes.Clear();
            Slowest.Clear();
            _missed = _dropped = _reprojected = _timedFrames = 0;
            _gcStart = GC.CollectionCount(0);
        }

        /// <summary>
        /// Perf bench: the frames since the last <see cref="Reset"/>, summed up. The medians are what a setting costs (a few
        /// hitches in a 5 s sample pulled the average of one far off another of the same setting); the 95th shows the hitches.
        /// </summary>
        internal static string Summary(out float cpuMedian, out float gpuMedian)
        {
            cpuMedian = gpuMedian = -1f;
            if (FrameTimes.Count == 0)
            {
                return "no frames";
            }
            List<float> cpu = FrameTimes.OrderBy(f => f).ToList();
            cpuMedian = cpu[cpu.Count / 2];
            string s = $"frame {cpuMedian,6:0.00} ms (avg {FrameTimes.Average(),6:0.00}, 95th {cpu[(int)(cpu.Count * 0.95f)],6:0.00})";
            if (GpuTimes.Count > 0)
            {
                List<float> gpu = GpuTimes.OrderBy(f => f).ToList();
                gpuMedian = gpu[gpu.Count / 2];
                s += $", GPU {gpuMedian,6:0.00} ms (95th {gpu[(int)(gpu.Count * 0.95f)],6:0.00})";
            }
            return s + $", {FrameTimes.Count} frames, {_dropped} dropped, {_reprojected} reprojected";
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            double ms = 1000.0 / Stopwatch.Frequency;
            int frames = FrameTimes.Count;
            if (frames > 0)
            {
                List<float> sorted = FrameTimes.OrderBy(f => f).ToList();
                sb.AppendLine($"Frames: {frames}, frame time avg {FrameTimes.Average():0.00} ms, median {sorted[frames / 2]:0.00} ms, " +
                              $"95th {sorted[(int)(frames * 0.95f)]:0.00} ms, 99th {sorted[(int)(frames * 0.99f)]:0.00} ms, max {sorted[frames - 1]:0.00} ms");
                sb.AppendLine($"Gen-0 garbage collections during the run: {GC.CollectionCount(0) - Math.Max(0, _gcStart)}");
                if (Slowest.Count > 0)
                {
                    sb.AppendLine("Slowest frames: " + string.Join("; ", Slowest.Select(k => $"{k.Key:0} ms ({k.Value})")));
                }
            }
            if (GpuTimes.Count > 0)
            {
                List<float> gpu = GpuTimes.OrderBy(f => f).ToList();
                int n = gpu.Count;
                sb.AppendLine($"GPU per frame (SteamVR compositor, {n} frames): avg {GpuTimes.Average():0.00} ms, median {gpu[n / 2]:0.00} ms, 95th {gpu[(int)(n * 0.95f)]:0.00} ms, " +
                              $"99th {gpu[(int)(n * 0.99f)]:0.00} ms, max {gpu[n - 1]:0.00} ms; of {_timedFrames} timed frames: {_missed} shown late, {_dropped} dropped, {_reprojected} reprojected");
            }
            // Bytes per call is an average: a few weapon switches (analysis, pouch rebuild) can carry it on their own.
            // The share of calls that allocated at all tells steady-state garbage from those one-off spikes.
            sb.AppendLine("Mod sections (per call): avg ms, max ms, calls, bytes allocated per call, % of calls that allocated");
            foreach (KeyValuePair<string, Section> kv in Sections.OrderByDescending(kv => kv.Value.Ticks))
            {
                Section s = kv.Value;
                if (s.Calls == 0)
                {
                    continue;
                }
                sb.AppendLine($"  {kv.Key,-24} {s.Ticks * ms / s.Calls,8:0.000} {s.MaxTicks * ms,8:0.00} {s.Calls,8} {s.Allocated / (double)s.Calls,10:0} {100.0 * s.AllocatingCalls / s.Calls,8:0.0}%"
                              + (s.MaxAt != null && s.MaxTicks * ms > 2.0 ? $"   slowest at {s.MaxAt}" : ""));
            }
            return sb.ToString();
        }
    }
}
