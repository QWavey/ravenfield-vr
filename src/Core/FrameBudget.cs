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
    /// session's first minute and a half, mostly the menu. Always on: a bucket count a frame, no garbage.
    /// </summary>
    internal static class FrameBudget
    {
        private const float Window = 60f;
        private const float BucketMs = 0.25f;
        private static readonly int[] Buckets = new int[400]; // 0-100 ms; slower frames go in the last
        private static int _frames, _within, _gcAtStart = -1;
        private static float _seconds, _budgetMs;

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
            }
            Buckets[Mathf.Min((int)(ms / BucketMs), Buckets.Length - 1)]++;
            _frames++;
            // On time within a margin: frame pacing waits for the compositor, so a frame on time lands about one refresh interval
            // after the last, and one late at about two.
            if (ms <= _budgetMs * 1.2f)
            {
                _within++;
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

        private static void Report()
        {
            float hz = 1000f / _budgetMs;
            string preset = VRConfig.Graphics.Value.ToString();
            Log.Info($"Frame rate over the last minute of play: {100f * _within / _frames:0}% of {_frames} frames at the headset's {hz:0} Hz " +
                     $"({_budgetMs:0.0} ms), median {Percentile(0.5f):0.0} ms, 95th {Percentile(0.95f):0.0} ms; {preset}, render scale " +
                     $"{XRSettings.eyeTextureResolutionScale:0.0#}, MSAA {QualitySettings.antiAliasing}x, shadows " +
                     $"{(QualitySettings.shadows == ShadowQuality.Disable ? "off" : $"{QualitySettings.shadowDistance:0} m")}, LOD bias {QualitySettings.lodBias:0.0#}; " +
                     $"{System.GC.CollectionCount(0) - _gcAtStart} garbage collections ({GcMode()}); the mod's own work {VRManager.TakeModMsPerFrame():0.0} ms a frame.");
            System.Array.Clear(Buckets, 0, Buckets.Length);
            _frames = _within = 0;
            _seconds = _budgetMs = 0f;
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

        /// <summary>The frame time at this share of the window's frames (the upper edge of its bucket).</summary>
        private static float Percentile(float share)
        {
            int need = Mathf.CeilToInt(share * _frames);
            int seen = 0;
            for (int i = 0; i < Buckets.Length; i++)
            {
                seen += Buckets[i];
                if (seen >= need)
                {
                    return (i + 1) * BucketMs;
                }
            }
            return Buckets.Length * BucketMs;
        }
    }
}
