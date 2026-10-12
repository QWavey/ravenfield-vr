using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Refines where a slice of a mod's reload clip starts and ends, using the clip's samples when Unity allows
    /// reading them (Decompress On Load clips): the start snaps to the strongest onset near the animation keyframe,
    /// the end to where that sound has died away. Compressed/streamed clips keep the keyframe-based estimate.
    /// </summary>
    internal static class AudioSlicing
    {
        private const float FrameSeconds = 0.01f;

        public static void Refine(AudioClip clip, float keyTime, ref float start, ref float end)
        {
            if (clip == null || clip.loadType != AudioClipLoadType.DecompressOnLoad || clip.loadState != AudioDataLoadState.Loaded)
            {
                return;
            }
            int freq = clip.frequency;
            int channels = Mathf.Max(1, clip.channels);
            float windowStart = Mathf.Max(0f, keyTime - 0.25f);
            float windowEnd = Mathf.Min(clip.length, Mathf.Max(end, keyTime + 0.15f) + 0.05f);
            int offset = Mathf.FloorToInt(windowStart * freq);
            int count = Mathf.FloorToInt((windowEnd - windowStart) * freq);
            if (count <= freq * FrameSeconds * 4 || offset + count > clip.samples)
            {
                return;
            }
            var data = new float[count * channels];
            if (!clip.GetData(data, offset))
            {
                return;
            }

            int frameLen = Mathf.Max(1, Mathf.RoundToInt(freq * FrameSeconds));
            int frames = count / frameLen;
            var rms = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                double sum = 0;
                int baseIndex = f * frameLen * channels;
                int n = frameLen * channels;
                for (int i = 0; i < n; i++)
                {
                    float v = data[baseIndex + i];
                    sum += v * v;
                }
                rms[f] = Mathf.Sqrt((float)(sum / n));
            }

            // Onset: largest energy rise within [key - 0.25 s, key + 0.15 s].
            int searchEnd = Mathf.Min(frames - 1, Mathf.FloorToInt((keyTime + 0.15f - windowStart) / FrameSeconds));
            int onset = -1;
            float bestRise = 0f;
            float peak = 0f;
            for (int f = 1; f <= searchEnd; f++)
            {
                float rise = rms[f] - rms[f - 1];
                if (rise > bestRise)
                {
                    bestRise = rise;
                    onset = f;
                }
            }
            for (int f = 0; f < frames; f++)
            {
                peak = Mathf.Max(peak, rms[f]);
            }
            if (onset < 0 || peak < 1e-4f || bestRise < peak * 0.15f)
            {
                return;
            }
            float refinedStart = windowStart + onset * FrameSeconds - 0.015f;

            // End: the sound after the onset has decayed below 12% of its own peak for 3 frames.
            float localPeak = 0f;
            int f0 = onset;
            int quiet = 0;
            int endFrame = -1;
            for (int f = f0; f < frames; f++)
            {
                localPeak = Mathf.Max(localPeak, rms[f]);
                if (f > f0 + 3 && rms[f] < localPeak * 0.12f)
                {
                    if (++quiet >= 3)
                    {
                        endFrame = f;
                        break;
                    }
                }
                else
                {
                    quiet = 0;
                }
            }
            float refinedEnd = endFrame > 0 ? windowStart + endFrame * FrameSeconds + 0.03f : end;
            start = Mathf.Max(0f, refinedStart);
            end = Mathf.Clamp(refinedEnd, start + 0.08f, end);
        }
    }
}
