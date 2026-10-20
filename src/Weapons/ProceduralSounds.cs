using System;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    public enum MechSound { MagOut, MagIn, SlideBack, SlideForward, DryFire, Holster, ShellIn, Pouch, PinPull, Spoon, Casing, CasingSoft, MagKnock, MagScrape, MagLip }

    /// <summary>
    /// Mechanical weapon sounds synthesised at start-up (filtered noise bursts and damped metallic partials),
    /// played through a small pool of 3D sources routed to the game's first-person mixer group.
    /// </summary>
    public sealed class ProceduralSounds : IDisposable
    {
        private const int Rate = 44100;
        private readonly AudioClip[] _clips;
        private readonly AudioSource[] _pool = new AudioSource[8];
        private readonly GameObject _host;
        private int _next;

        public ProceduralSounds()
        {
            var rng = new System.Random(7351);
            _clips = new AudioClip[Enum.GetValues(typeof(MechSound)).Length];
            _clips[(int)MechSound.MagOut] = Build("mag_out", 0.16f, rng, (t, n) =>
                Thump(t, 140f, 0.035f) * 0.9f + Burst(t, n, 0.02f, 0.5f) + Ring(t, 820f, 0.03f) * 0.25f);
            _clips[(int)MechSound.MagIn] = Build("mag_in", 0.14f, rng, (t, n) =>
                Click(t, n, 0f, 0.6f) + Click(t, n, 0.055f, 1f) + Ring(t - 0.055f, 1250f, 0.025f) * 0.4f + Thump(t - 0.055f, 190f, 0.03f) * 0.5f);
            _clips[(int)MechSound.SlideBack] = Build("slide_back", 0.17f, rng, (t, n) =>
                Scrape(t, n, 0.13f) * 0.55f + Click(t, n, 0.13f, 0.8f) + Ring(t - 0.13f, 2100f, 0.02f) * 0.3f);
            _clips[(int)MechSound.SlideForward] = Build("slide_forward", 0.12f, rng, (t, n) =>
                Click(t, n, 0f, 1f) + Ring(t, 1850f, 0.04f) * 0.55f + Ring(t, 3150f, 0.025f) * 0.35f + Thump(t, 230f, 0.025f) * 0.4f);
            _clips[(int)MechSound.DryFire] = Build("dry_fire", 0.06f, rng, (t, n) =>
                Click(t, n, 0f, 0.7f) + Ring(t, 2600f, 0.012f) * 0.3f);
            _clips[(int)MechSound.Holster] = Build("holster", 0.22f, rng, (t, n) =>
                Scrape(t, n, 0.2f) * 0.25f + Thump(t - 0.16f, 110f, 0.04f) * 0.4f);
            _clips[(int)MechSound.ShellIn] = Build("shell_in", 0.1f, rng, (t, n) =>
                Click(t, n, 0f, 0.5f) + Click(t, n, 0.04f, 0.7f) + Ring(t - 0.04f, 1500f, 0.02f) * 0.3f);
            _clips[(int)MechSound.Pouch] = Build("pouch", 0.12f, rng, (t, n) =>
                Scrape(t, n, 0.1f) * 0.3f + Thump(t, 160f, 0.02f) * 0.3f);
            // Split ring dragged out of the fuse, then the spoon flicking off on the throw.
            _clips[(int)MechSound.PinPull] = Build("pin_pull", 0.09f, rng, (t, n) =>
                Scrape(t, n, 0.035f) * 0.18f + Click(t, n, 0.035f, 0.45f) + Ring(t - 0.035f, 3400f, 0.035f) * 0.22f);
            _clips[(int)MechSound.Spoon] = Build("spoon", 0.16f, rng, (t, n) =>
                Click(t, n, 0f, 0.35f) + Ring(t, 2650f, 0.09f) * 0.28f + Ring(t, 4100f, 0.05f) * 0.12f);
            // A spent brass case landing: a bright tink and a smaller bounce.
            _clips[(int)MechSound.Casing] = Build("casing", 0.14f, rng, (t, n) =>
                Click(t, n, 0f, 0.3f) + Ring(t, 4300f, 0.045f) * 0.4f + Ring(t, 6150f, 0.025f) * 0.25f
                + Click(t, n, 0.065f, 0.15f) + Ring(t - 0.065f, 4450f, 0.025f) * 0.15f);
            // The same case landing on earth, grass or sand: a dull tap, no ring. Low thump and noise low-passed twice
            // (spectral centre about 1 kHz; the brass tink's is about 9 kHz).
            float lp1 = 0f, lp2 = 0f;
            _clips[(int)MechSound.CasingSoft] = Build("casing_soft", 0.08f, rng, (t, n) =>
            {
                lp1 += (n - lp1) * 0.06f;
                lp2 += (lp1 - lp2) * 0.06f;
                return Thump(t, 180f, 0.015f) * 0.9f + lp2 * Env(t, 0.012f) * 6f;
            });

            // A magazine knocked against the gun: a dull metal thud with a short ring (steel on steel, not a click).
            _clips[(int)MechSound.MagKnock] = Build("mag_knock", 0.11f, rng, (t, n) =>
                Thump(t, 290f, 0.022f) * 0.7f + Click(t, n, 0f, 0.35f) + Ring(t, 1650f, 0.035f) * 0.3f + Ring(t, 2950f, 0.02f) * 0.15f);
            // Dragged along it: a soft scrape.
            _clips[(int)MechSound.MagScrape] = Build("mag_scrape", 0.14f, rng, (t, n) =>
                Scrape(t, n, 0.12f) * 0.35f + Ring(t, 2400f, 0.06f) * 0.05f);
            // Onto the magwell's lip: a small click.
            _clips[(int)MechSound.MagLip] = Build("mag_lip", 0.06f, rng, (t, n) =>
                Click(t, n, 0f, 0.5f) + Ring(t, 2300f, 0.02f) * 0.25f);

            _host = new GameObject("RavenfieldVR Sounds");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            for (int i = 0; i < _pool.Length; i++)
            {
                var child = new GameObject("voice" + i);
                child.transform.SetParent(_host.transform, false);
                var src = child.AddComponent<AudioSource>();
                src.playOnAwake = false;
                src.spatialBlend = 1f;
                src.minDistance = 0.3f;
                src.maxDistance = 15f;
                src.rolloffMode = AudioRolloffMode.Linear;
                src.dopplerLevel = 0f;
                _pool[i] = src;
            }
            RouteToMixer();
        }

        private void RouteToMixer()
        {
            try
            {
                foreach (AudioSource s in _pool)
                {
                    GameManager.SetOutputAudioMixer(s, AudioMixer.FirstPerson);
                }
            }
            catch (Exception e)
            {
                Log.Debug("Mixer routing unavailable yet: " + e.Message);
            }
        }

        // delay: seconds before it sounds (two clicks of one action played in one frame ran into one; sound hunt, 2026-10-20).
        public void Play(MechSound sound, Vector3 position, float volume = 1f, float delay = 0f)
        {
            AudioSource src = _pool[_next];
            _next = (_next + 1) % _pool.Length;
            if (src.outputAudioMixerGroup == null)
            {
                RouteToMixer();
            }
            src.transform.position = position;
            src.pitch = UnityEngine.Random.Range(0.94f, 1.06f);
            if (delay > 0f)
            {
                src.Stop(); // (its last one-shot, seven sounds ago)
                src.clip = _clips[(int)sound];
                src.volume = volume;
                src.PlayDelayed(delay);
                return;
            }
            src.volume = 1f; // (a delayed sound left its level on the source)
            src.PlayOneShot(_clips[(int)sound], volume);
        }

        // ---- synthesis helpers (t = seconds, n = white noise sample) --------------------------------

        private static AudioClip Build(string name, float seconds, System.Random rng, Func<float, float, float> fn)
        {
            int count = Mathf.CeilToInt(seconds * Rate);
            var data = new float[count];
            float peak = 1e-4f;
            float prevNoise = 0f;
            for (int i = 0; i < count; i++)
            {
                float white = (float)(rng.NextDouble() * 2.0 - 1.0);
                float hp = white - prevNoise; // first difference: brighter, metallic noise
                prevNoise = white;
                float t = i / (float)Rate;
                float v = fn(t, hp * 0.6f + white * 0.4f);
                data[i] = v;
                peak = Mathf.Max(peak, Mathf.Abs(v));
            }
            float gain = 0.9f / peak;
            for (int i = 0; i < count; i++)
            {
                data[i] *= gain;
                // 2 ms fade in/out avoids clicks at the clip edges.
                float edge = Mathf.Min(i, count - 1 - i) / (0.002f * Rate);
                if (edge < 1f)
                {
                    data[i] *= edge;
                }
            }
            AudioClip clip = AudioClip.Create("rfvr_" + name, count, 1, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static float Env(float t, float decay) => t < 0f ? 0f : Mathf.Exp(-t / decay);
        private static float Ring(float t, float freq, float decay) => Mathf.Sin(2f * Mathf.PI * freq * Mathf.Max(0f, t)) * Env(t, decay);
        private static float Thump(float t, float freq, float decay) => Mathf.Sin(2f * Mathf.PI * freq * Mathf.Max(0f, t) * (1f - Mathf.Clamp01(t) * 0.4f)) * Env(t, decay);
        private static float Burst(float t, float noise, float decay, float gain) => noise * Env(t, decay) * gain;
        private static float Click(float t, float noise, float at, float gain) => noise * Env(t - at, 0.004f) * gain;

        private static float Scrape(float t, float noise, float length)
        {
            if (t < 0f || t > length)
            {
                return 0f;
            }
            float shape = Mathf.Sin(Mathf.PI * t / length);
            float grain = 0.6f + 0.4f * Mathf.Sin(2f * Mathf.PI * 95f * t);
            return noise * shape * grain;
        }

        public void Dispose()
        {
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
            }
            foreach (AudioClip c in _clips)
            {
                if (c != null)
                {
                    UnityEngine.Object.Destroy(c);
                }
            }
        }
    }
}
