using System.Runtime.InteropServices;
using RavenfieldVR.Util;
using UnityEngine;
using UnityEngine.XR;
using Valve.VR;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// Once a minute of play, a log line saying how many frames kept to the headset's rate, with the graphics settings they were
    /// drawn with. Below the headset's rate SteamVR reprojects, and moving things ghost and lag in front of the view (user,
    /// 2026-10-24: on every preset with shadows, never on the game's own Fastest quality); the input check's fps covers only a
    /// session's first minute and a half (mostly the menu) and 10 s after each change of view. Always on: a bucket count a frame, no garbage.
    /// </summary>
    internal static class FrameBudget
    {
        private const float Window = 60f;
        private const float BucketMs = 0.25f;
        private static readonly int[] Buckets = new int[400]; // 0-100 ms; slower frames go in the last
        private static int _frames, _within, _gcAtStart = -1;
        private static float _seconds, _budgetMs;
        private static double _modMs;
        private static long _modFrames;

        // The GPU's time for each frame the compositor got (both eyes and the game's cameras): over the budget with the CPU
        // under it, a preset is too heavy; under it with frames still late, the CPU (the game, the mod) is the cause.
        private static readonly int[] GpuBuckets = new int[400];
        private static int _gpuFrames;
        private static uint _lastTimedFrame;
        private static readonly uint TimingSize = (uint)Marshal.SizeOf(typeof(Compositor_FrameTiming));

        // The last few seconds as well, for the wrist display: picking a preset on the panel shows its effect at once.
        private const float ShortWindow = 3f;
        private static int _shortFrames, _shortWithin, _shortGpuFrames;
        private static float _shortSeconds, _shortGpuMs;

        /// <summary>The share of frames on time over the last few seconds of play (0-100), or -1 before the first.</summary>
        internal static int RecentPercent { get; private set; } = -1;

        /// <summary>The headset's refresh rate the share is measured against (0 before the first frame of play).</summary>
        internal static int RecentHz { get; private set; }

        /// <summary>
        /// The GPU's average time a frame over the last few seconds, in tenths of a millisecond (-1: not timed). Over the
        /// refresh interval, the preset is too heavy; under it with frames late, the CPU is the cause.
        /// </summary>
        internal static int RecentGpuTenths { get; private set; } = -1;

        public static void Frame()
        {
            if (!GameContext.InGame || GameContext.GameMenuOpen || Time.timeScale <= 0f)
            {
                return; // play only: a menu, the loadout screen or a pause draws far less
            }
            float dt = Time.unscaledDeltaTime;
            if (dt <= 0f || dt > 1f)
            {
                return; // (a loading hitch is not the preset's)
            }
            float ms = dt * 1000f;
            if (_budgetMs <= 0f)
            {
                _budgetMs = 1000f / RefreshRate(); // once a window
                _gcAtStart = System.GC.CollectionCount(0);
                // The mod's own time counted from here (the first window's figure was an average since VR started, menus and
                // loading included; review, 2026-10-24).
                VRManager.ModMsPerFrameSince(ref _modMs, ref _modFrames);
            }
            Buckets[Mathf.Min((int)(ms / BucketMs), Buckets.Length - 1)]++;
            _frames++;
            // On time within a margin: frame pacing waits for the compositor, so a frame on time lands about one refresh interval
            // after the last, and one late at about two.
            bool onTime = ms <= _budgetMs * 1.2f;
            if (onTime)
            {
                _within++;
            }
            SampleGpu();
            _shortFrames++;
            _shortWithin += onTime ? 1 : 0;
            _shortSeconds += dt;
            if (_shortSeconds >= ShortWindow)
            {
                RecentPercent = Mathf.RoundToInt(100f * _shortWithin / _shortFrames);
                GraphicsPresets.OnRecentFrames(RecentPercent);
                RecentHz = Mathf.RoundToInt(1000f / _budgetMs);
                RecentGpuTenths = _shortGpuFrames > 0 ? Mathf.RoundToInt(10f * _shortGpuMs / _shortGpuFrames) : -1;
                _shortFrames = _shortWithin = _shortGpuFrames = 0;
                _shortSeconds = _shortGpuMs = 0f;
            }
            _seconds += dt;
            if (_seconds >= Window)
            {
                Report();
            }
        }

        /// <summary>
        /// The headset's refresh rate: SteamVR's display frequency (Unity's XRDevice.refreshRate read 0 Hz through Link in the
        /// user's log), else Unity's, else 90.
        /// </summary>
        private static float RefreshRate()
        {
            try
            {
                ETrackedPropertyError error = ETrackedPropertyError.TrackedProp_Success;
                float hz = OpenVR.System?.GetFloatTrackedDeviceProperty(OpenVR.k_unTrackedDeviceIndex_Hmd, ETrackedDeviceProperty.Prop_DisplayFrequency_Float, ref error) ?? 0f;
                if (error == ETrackedPropertyError.TrackedProp_Success && hz > 1f)
                {
                    return hz;
                }
            }
            catch (System.Exception)
            {
                // no OpenVR system (flat mode): Unity's below
            }
            return XRDevice.refreshRate > 1f ? XRDevice.refreshRate : 90f;
        }

        private static void SampleGpu()
        {
            try
            {
                CVRCompositor comp = OpenVR.Compositor;
                if (comp == null)
                {
                    return;
                }
                var t = new Compositor_FrameTiming { m_nSize = TimingSize };
                if (!comp.GetFrameTiming(ref t, 0) || t.m_nFrameIndex == _lastTimedFrame || t.m_flTotalRenderGpuMs <= 0f)
                {
                    return;
                }
                _lastTimedFrame = t.m_nFrameIndex;
                GpuBuckets[Mathf.Min((int)(t.m_flTotalRenderGpuMs / BucketMs), GpuBuckets.Length - 1)]++;
                _gpuFrames++;
                _shortGpuMs += t.m_flTotalRenderGpuMs;
                _shortGpuFrames++;
            }
            catch (System.Exception)
            {
                // no compositor (flat mode): no GPU times
            }
        }

        private static void Report()
        {
            float hz = 1000f / _budgetMs;
            string preset = VRConfig.Graphics.Value.ToString();
            string gpu = _gpuFrames > 0
                ? $"GPU median {Percentile(GpuBuckets, _gpuFrames, 0.5f):0.0} ms, 95th {Percentile(GpuBuckets, _gpuFrames, 0.95f):0.0} ms"
                : "GPU not timed";
            Log.Info($"Frame rate over the last minute of play: {100f * _within / _frames:0}% of {_frames} frames at the headset's {hz:0} Hz " +
                     $"({_budgetMs:0.0} ms), median {Percentile(Buckets, _frames, 0.5f):0.0} ms, 95th {Percentile(Buckets, _frames, 0.95f):0.0} ms, {gpu}; " +
                     $"{preset}, {XRSettings.eyeTextureWidth}x{XRSettings.eyeTextureHeight} per eye (render scale {XRSettings.eyeTextureResolutionScale:0.0#}), " +
                     $"MSAA {QualitySettings.antiAliasing}x, shadows " +
                     $"{(QualitySettings.shadows == ShadowQuality.Disable ? "off" : $"{QualitySettings.shadowDistance:0} m")}, LOD bias {QualitySettings.lodBias:0.0#}; " +
                     $"{System.GC.CollectionCount(0) - _gcAtStart} garbage collections ({GcMode()}); the mod's own work {VRManager.ModMsPerFrameSince(ref _modMs, ref _modFrames):0.0} ms a frame; "
                     + CpuSpeed() + "; "
                     + FramePhases.TakeReport());
            System.Array.Clear(Buckets, 0, Buckets.Length);
            System.Array.Clear(GpuBuckets, 0, GpuBuckets.Length);
            _frames = _within = _gpuFrames = 0;
            _seconds = _budgetMs = 0f;
        }

        private static double _firstCpuMs = -1;
        private static uint _cpuSink; // (the sum's result kept, so the loop is not optimised away)

        /// <summary>
        /// How fast the CPU runs now: the time a fixed sum takes (the least of three runs, about a quarter of a millisecond each),
        /// against the session's first minute. The user's play of 1.14.0 (2026-10-28) grew 2-6 times slower in every phase over ten
        /// minutes, the engine's own steps too, with no more bodies, renderers or memory: a CPU slowing down (heat, the power plan)
        /// looks like that, more work does not. This tells the two apart.
        /// </summary>
        private static string CpuSpeed()
        {
            double best = double.MaxValue;
            var watch = new System.Diagnostics.Stopwatch();
            for (int run = 0; run < 3; run++)
            {
                watch.Restart();
                uint x = 2463534242u + (uint)run;
                float f = 1f;
                for (int i = 0; i < 100000; i++)
                {
                    x ^= x << 13;
                    x ^= x >> 17;
                    x ^= x << 5;
                    f = f * 0.999f + (x & 1023) * 1e-3f;
                }
                _cpuSink += x + (uint)f;
                best = System.Math.Min(best, watch.Elapsed.TotalMilliseconds);
            }
            if (_firstCpuMs < 0 && best > 0)
            {
                _firstCpuMs = best;
            }
            return $"CPU speed {100.0 * _firstCpuMs / System.Math.Max(best, 1e-6):0}% of the session's first minute ({best:0.00} ms for a fixed sum)";
        }

        /// <summary>
        /// The game's collector: incremental in slices of up to 3 ms a frame (its boot.config gc-max-time-slice=3), a third of a
        /// 90 Hz frame while a collection runs. Logged to weigh a shorter slice (a frame kept) against collections that then
        /// fall behind and stop the world.
        /// </summary>
        private static string GcMode()
        {
            try
            {
                return UnityEngine.Scripting.GarbageCollector.isIncremental
                    ? $"incremental, {UnityEngine.Scripting.GarbageCollector.incrementalTimeSliceNanoseconds / 1e6:0.#} ms a frame"
                    : "not incremental";
            }
            catch (System.Exception)
            {
                return "mode unknown";
            }
        }

        /// <summary>The time at this share of a window's frames (the upper edge of its bucket).</summary>
        private static float Percentile(int[] buckets, int count, float share)
        {
            int need = Mathf.CeilToInt(share * count);
            int seen = 0;
            for (int i = 0; i < buckets.Length; i++)
            {
                seen += buckets[i];
                if (seen >= need)
                {
                    return (i + 1) * BucketMs;
                }
            }
            return buckets.Length * BucketMs;
        }
    }
}
