using UnityEngine;

namespace RavenfieldVR.Net
{
    /// <summary>
    /// IMA ADPCM, mono: 16-bit samples to 4 bits each and back. Each frame starts from its own predictor and step index, so a frame
    /// lost on the line costs that frame only. Pure: the offline tests check it.
    /// </summary>
    internal static class Adpcm
    {
        private static readonly int[] IndexTable = { -1, -1, -1, -1, 2, 4, 6, 8, -1, -1, -1, -1, 2, 4, 6, 8 };

        private static readonly int[] StepTable =
        {
            7, 8, 9, 10, 11, 12, 13, 14, 16, 17, 19, 21, 23, 25, 28, 31, 34, 37, 41, 45, 50, 55, 60, 66, 73, 80, 88, 97, 107, 118, 130, 143,
            157, 173, 190, 209, 230, 253, 279, 307, 337, 371, 408, 449, 494, 544, 598, 658, 724, 796, 876, 963, 1060, 1166, 1282, 1411,
            1552, 1707, 1878, 2066, 2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358, 5894, 6484, 7132, 7845, 8630, 9493,
            10442, 11487, 12635, 13899, 15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
        };

        private static int _index; // the encoder's step index, carried from frame to frame

        /// <summary>Samples (an even count) into half as many bytes; the frame's start state out.</summary>
        internal static void Encode(short[] samples, byte[] packed, out short predictorOut, out byte indexOut)
        {
            int predictor = samples[0];
            int index = _index;
            predictorOut = (short)predictor;
            indexOut = (byte)index;
            for (int i = 0; i < samples.Length; i++)
            {
                int step = StepTable[index];
                int diff = samples[i] - predictor;
                int code = 0;
                if (diff < 0)
                {
                    code = 8;
                    diff = -diff;
                }
                int delta = step >> 3;
                if (diff >= step)
                {
                    code |= 4;
                    diff -= step;
                    delta += step;
                }
                step >>= 1;
                if (diff >= step)
                {
                    code |= 2;
                    diff -= step;
                    delta += step;
                }
                step >>= 1;
                if (diff >= step)
                {
                    code |= 1;
                    delta += step;
                }
                predictor += (code & 8) != 0 ? -delta : delta;
                predictor = Mathf.Clamp(predictor, -32768, 32767);
                index = Mathf.Clamp(index + IndexTable[code], 0, 88);
                if ((i & 1) == 0)
                {
                    packed[i >> 1] = (byte)code;
                }
                else
                {
                    packed[i >> 1] |= (byte)(code << 4);
                }
            }
            _index = index;
        }

        internal static void Decode(byte[] packed, short predictorIn, byte indexIn, short[] samples)
        {
            int predictor = predictorIn;
            int index = Mathf.Clamp(indexIn, 0, 88);
            for (int i = 0; i < samples.Length; i++)
            {
                int code = (i & 1) == 0 ? packed[i >> 1] & 15 : packed[i >> 1] >> 4;
                int step = StepTable[index];
                int delta = step >> 3;
                if ((code & 4) != 0)
                {
                    delta += step;
                }
                if ((code & 2) != 0)
                {
                    delta += step >> 1;
                }
                if ((code & 1) != 0)
                {
                    delta += step >> 2;
                }
                predictor += (code & 8) != 0 ? -delta : delta;
                predictor = Mathf.Clamp(predictor, -32768, 32767);
                index = Mathf.Clamp(index + IndexTable[code], 0, 88);
                samples[i] = (short)predictor;
            }
        }
    }
}
