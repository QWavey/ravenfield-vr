using UnityEngine;

namespace RavenfieldVR.Rig
{
    /// <summary>
    /// How a closed vehicle's inside is painted (the user, 2026-11-15: "the interiors are still bad"): real armour is painted a pale
    /// grey-green inside, worn and grimy; drawn with the hull's own outside materials the inside showed its camouflage turned round.
    /// One grime texture made in code once (no asset to download), laid over the walls and over the cabin's own metal and paint.
    /// </summary>
    internal static class InteriorLook
    {
        private static Texture2D _grime;

        /// <summary>
        /// Soft grime: wide blotches of dirt, finer mottling, darker streaks running down (as from leaks and hands), on a near-white
        /// base, so it darkens and varies whatever colour it is laid over. 256 px, tiling, made once.
        /// </summary>
        internal static Texture2D Grime()
        {
            if (_grime != null)
            {
                return _grime;
            }
            const int n = 256;
            _grime = new Texture2D(n, n, TextureFormat.RGB24, true) { name = "RavenfieldVR interior grime", wrapMode = TextureWrapMode.Repeat, anisoLevel = 2 };
            var px = new Color[n * n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float u = (float)x / n, v = (float)y / n;
                    float blotch = Tiled(u, v, 3f, 11.3f);
                    float mottle = Tiled(u, v, 17f, 47.1f);
                    float streak = Mathf.Clamp01(Tiled(u * 0.25f, v, 24f, 3.7f) * 1.6f - 0.75f); // (long down the v direction)
                    float k = 0.86f + 0.12f * blotch + 0.05f * mottle - 0.22f * streak;
                    k = Mathf.Clamp(k, 0.55f, 1f);
                    px[y * n + x] = new Color(k, k * 0.99f, k * 0.96f);
                }
            }
            _grime.SetPixels(px);
            _grime.Apply(true, true); // (no longer readable: the memory goes to the GPU only)
            Object.DontDestroyOnLoad(_grime);
            return _grime;
        }

        /// <summary>Perlin noise that tiles over the unit square at <paramref name="freq"/> cells (blended from the four wrapped corners).</summary>
        private static float Tiled(float u, float v, float freq, float seed)
        {
            float a = Mathf.PerlinNoise(u * freq + seed, v * freq + seed);
            float b = Mathf.PerlinNoise((u - 1f) * freq + seed, v * freq + seed);
            float c = Mathf.PerlinNoise(u * freq + seed, (v - 1f) * freq + seed);
            float d = Mathf.PerlinNoise((u - 1f) * freq + seed, (v - 1f) * freq + seed);
            return Mathf.Lerp(Mathf.Lerp(a, b, u), Mathf.Lerp(c, d, u), v);
        }

        /// <summary>
        /// The inside's paint: a pale grey-green with a fifth of the vehicle's own colour (a sand tank's inside is a little sandy), worn
        /// matt, lit a little from within (the hull's inner faces turn away from the sun and were black).
        /// </summary>
        internal static Material Paint(Color vehicleTint)
        {
            Color pale = Color.Lerp(new Color(0.68f, 0.71f, 0.64f), new Color(vehicleTint.r, vehicleTint.g, vehicleTint.b) * 1.6f, 0.2f);
            pale.a = 1f;
            var m = new Material(Shader.Find("Standard")) { name = "RavenfieldVR interior paint", color = pale, mainTexture = Grime() };
            m.mainTextureScale = new Vector2(2f, 2f);
            m.SetFloat("_Glossiness", 0.22f);
            m.SetFloat("_Metallic", 0.1f);
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", pale * 0.16f);
            return m;
        }
    }
}
