using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    public enum MechSound { MagOut, MagIn, SlideBack, SlideForward, DryFire, Holster, ShellIn, Pouch, PinPull, Spoon, Casing, CasingSoft, MagKnock, MagLip, Selector }

    /// <summary>
    /// The mod's 3D voices: a small pool of sources routed to the game's first-person mixer group, playing the game's own audio
    /// (GameSounds picks the clip and the part of it). Nothing is synthesised (user, 2026-11-04: "switch all built in sounds we
    /// created to game sounds").
    /// </summary>
    public sealed class SoundVoices : IDisposable
    {
        private readonly AudioSource[] _pool = new AudioSource[8];
        /// <summary>Each voice's fade for a slice of a game clip (PlayGameClip): in and out, so its cut ends do not click.</summary>
        private readonly SliceFader[] _faders = new SliceFader[8];
        private readonly GameObject _host;
        private readonly QueuedSounds _queued;
        private int _next;

        public SoundVoices()
        {
            _host = new GameObject("RavenfieldVR Sounds");
            UnityEngine.Object.DontDestroyOnLoad(_host);
            _queued = _host.AddComponent<QueuedSounds>();
            _queued.Voices = this;
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
                _faders[i] = child.AddComponent<SliceFader>();
                _faders[i].Src = src;
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

        /// <summary>
        /// Until when each voice's slice is sounding (unscaled time): a later sound takes another voice, or it cut the slice short
        /// and took over its fade (round-5 scout).
        /// </summary>
        private readonly float[] _busyUntil = new float[8]; // (one a voice of _pool)

        /// <summary>The next voice round the pool with nothing sounding on it; with all eight busy, the one free soonest.</summary>
        private int TakeVoice()
        {
            float now = Time.unscaledTime;
            int soonest = _next;
            for (int tried = 0; tried < _pool.Length; tried++)
            {
                int i = (_next + tried) % _pool.Length;
                if (_busyUntil[i] <= now)
                {
                    _next = (i + 1) % _pool.Length;
                    return i;
                }
                if (_busyUntil[i] < _busyUntil[soonest])
                {
                    soonest = i;
                }
            }
            _next = (soonest + 1) % _pool.Length;
            return soonest;
        }

        /// <summary>
        /// The game's sound for <paramref name="sound"/> (GameSounds.Pick) at a place. <paramref name="delay"/>: seconds before it sounds
        /// (two clicks of one action played in one frame ran into one; sound hunt, 2026-10-20). <paramref name="weapon"/>: the gun it
        /// belongs to, for its own sound and its kind's; none for a sound of no gun in the hand (a soldier's, a pouch's).
        /// </summary>
        public void Play(MechSound sound, Vector3 position, float volume = 1f, float delay = 0f, WeaponRigData weapon = null)
        {
            if (delay > 0f)
            {
                _queued.Add(Time.unscaledTime + delay, sound, position, volume, weapon);
                return;
            }
            if (GameSounds.Pick(sound, weapon, out GameSlice s))
            {
                PlayGameClip(s.Clip, s.Start, s.Length, position, volume * s.Gain, s.Pitch * UnityEngine.Random.Range(0.97f, 1.03f));
            }
        }

        /// <summary>
        /// A part of one of the game's own clips (a weapon's reload sound sliced to one detail) through a 3D voice of the pool, from
        /// <paramref name="start"/> for <paramref name="length"/> seconds of it, at a level and pitch. False for a clip it cannot
        /// play (none, or not loaded).
        /// </summary>
        public bool PlayGameClip(AudioClip clip, float start, float length, Vector3 position, float volume, float pitch)
        {
            if (clip == null || clip.loadState == AudioDataLoadState.Failed || length <= 0f)
            {
                return false;
            }
            int voice = TakeVoice();
            AudioSource src = _pool[voice];
            if (src.outputAudioMixerGroup == null)
            {
                RouteToMixer();
            }
            pitch = Mathf.Clamp(pitch, 0.5f, 2f);
            src.Stop();
            src.transform.position = position;
            src.clip = clip;
            src.time = Mathf.Clamp(start, 0f, Mathf.Max(0f, clip.length - 0.01f));
            src.pitch = pitch;
            float real = length / pitch;
            // Faded in and out (SliceFader) where it is cut: begun in the middle of the clip and stopped before its end, the waveform
            // jumped and clicked, as the weapon's own slices did before they were faded (WeaponSoundPlayer). A clip's own start and end
            // need none, and a fade would only eat the attack of a click that starts at once.
            _faders[voice].Begin(Mathf.Clamp01(volume), real, start > 0.001f, start + length < clip.length - 0.005f);
            src.Play();
            // (Cut at its end, once faded out: a slice of a reload sound, not the rest of the reload after it.)
            src.SetScheduledEndTime(AudioSettings.dspTime + real + SliceFader.Tail);
            _busyUntil[voice] = Time.unscaledTime + real + SliceFader.Tail;
            return true;
        }

        public void Dispose()
        {
            if (_host != null)
            {
                UnityEngine.Object.Destroy(_host);
            }
        }
    }

    /// <summary>Sounds waiting for their moment (SoundVoices.Play with a delay), played from here when it comes.</summary>
    internal sealed class QueuedSounds : MonoBehaviour
    {
        private struct Waiting
        {
            public float At;
            public MechSound Sound;
            public Vector3 Position;
            public float Volume;
            public WeaponRigData Weapon;
        }

        internal SoundVoices Voices;
        private readonly List<Waiting> _waiting = new List<Waiting>();

        internal void Add(float at, MechSound sound, Vector3 position, float volume, WeaponRigData weapon) =>
            _waiting.Add(new Waiting { At = at, Sound = sound, Position = position, Volume = volume, Weapon = weapon });

        private void Update()
        {
            float now = Time.unscaledTime;
            for (int i = _waiting.Count - 1; i >= 0; i--)
            {
                Waiting w = _waiting[i];
                if (w.At <= now)
                {
                    _waiting.RemoveAt(i);
                    Voices.Play(w.Sound, w.Position, w.Volume, 0f, w.Weapon);
                }
            }
        }
    }

    /// <summary>
    /// A voice's level over a slice of a game clip: up from nothing in its first 12 ms and down to nothing over its last 30 ms (the voice
    /// stops <see cref="Tail"/> after that). Set each frame; Unity ramps a source's level within the mix, so a frame's step is smooth.
    /// </summary>
    internal sealed class SliceFader : MonoBehaviour
    {
        /// <summary>How long after the slice's end the voice stops (it is faded out by then).</summary>
        internal const float Tail = 0.02f;
        private const float FadeIn = 0.012f, FadeOut = 0.03f;

        internal AudioSource Src;
        internal bool Active;
        private float _level, _from, _until;
        private bool _fadeIn, _fadeOut;

        internal void Begin(float level, float length, bool fadeIn = true, bool fadeOut = true)
        {
            _level = level;
            _from = Time.unscaledTime;
            _until = _from + length;
            _fadeIn = fadeIn;
            _fadeOut = fadeOut;
            Active = true;
            if (Src != null)
            {
                Src.volume = fadeIn ? 0f : level;
            }
        }

        private void Update()
        {
            if (!Active || Src == null)
            {
                return;
            }
            float now = Time.unscaledTime;
            float up = _fadeIn ? Mathf.Clamp01((now - _from) / FadeIn) : 1f;
            float down = _fadeOut ? Mathf.Clamp01((_until - now) / FadeOut) : 1f;
            Src.volume = _level * Mathf.Min(up, down);
            if (now >= _until + Tail)
            {
                Active = false;
            }
        }
    }
}
