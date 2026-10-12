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
        }

        public static bool Enabled;
        private static readonly Dictionary<string, Section> Sections = new Dictionary<string, Section>();
        private static readonly List<float> FrameTimes = new List<float>(20000);
        private static int _gcStart = -1;

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
            s.MaxTicks = Math.Max(s.MaxTicks, ticks);
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
            FrameTimes.Add(Time.unscaledDeltaTime * 1000f);
        }

        public static void Reset()
        {
            Sections.Clear();
            FrameTimes.Clear();
            _gcStart = GC.CollectionCount(0);
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
                sb.AppendLine($"  {kv.Key,-24} {s.Ticks * ms / s.Calls,8:0.000} {s.MaxTicks * ms,8:0.00} {s.Calls,8} {s.Allocated / (double)s.Calls,10:0} {100.0 * s.AllocatingCalls / s.Calls,8:0.0}%");
            }
            return sb.ToString();
        }
    }
}
