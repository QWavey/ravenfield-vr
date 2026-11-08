using System;
using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    public enum CueKind
    {
        /// <summary>Replay one of the mod's animation sound events (SoundBank.PlaySoundBank / PlayRandom / PlayNext…).</summary>
        Event,
        /// <summary>Play the clip of an AudioSource object the mod's animation switches on.</summary>
        AudioObject,
        /// <summary>Play only the part of the mod's reload audio clip that belongs to this action.</summary>
        ClipSlice
    }

    /// <summary>One piece of a weapon's own audio, bound to a manual action.</summary>
    public sealed class SoundCue
    {
        public CueKind Kind;
        public string Function;
        public int IntParameter;
        public float FloatParameter;
        public string StringParameter;
        public int[] Path;
        public AudioClip Clip;
        public float Start;
        public float Length;
        /// <summary>For sound-bank events: the clip(s) the event plays (one for PlaySoundBank, the bank for PlayRandom).</summary>
        public AudioClip[] Clips;
        public float Volume = 1f;

        /// <summary>Playable without the weapon's own sound bank (so it can also be lent to another weapon).</summary>
        public bool SelfContained => Kind == CueKind.ClipSlice && Clip != null || Clips != null && Clips.Length > 0;
        /// <summary>Where on the mod's timeline the sound was found (seconds), for the report.</summary>
        public float SourceTime;

        public override string ToString()
        {
            switch (Kind)
            {
                case CueKind.Event: return $"{Function}({IntParameter})@{SourceTime:0.00}s";
                case CueKind.AudioObject: return $"audio-object@{SourceTime:0.00}s";
                default: return $"slice {Start:0.00}-{Start + Length:0.00}s of '{(Clip != null ? Clip.name : "?")}'";
            }
        }
    }

    /// <summary>
    /// Where a short clip holding both strokes of a rack (the slide back, then home) turns from one to the other: the quietest point of the
    /// gap after its first loud stroke, before a second one (user, 2026-11-08: "when i slide the slide back, the slide back and forth slide
    /// plays and not only slide back", on most guns and pistols: a gun's whole rack sound was bound to its pull). Read from the clip's samples
    /// once (a clip decompressed on load: others cannot be read, <see cref="Readable"/>); -1 for one stroke, or not read.
    /// </summary>
    internal static class RackSplit
    {
        private static readonly Dictionary<int, float> Known = new Dictionary<int, float>();

        internal static bool Readable(AudioClip clip) =>
            clip != null && clip.loadType == AudioClipLoadType.DecompressOnLoad && clip.loadState == AudioDataLoadState.Loaded;

        internal static float Of(AudioClip clip)
        {
            if (clip == null || clip.length >= WeaponAnalyzer.WholeReloadSeconds)
            {
                return -1f;
            }
            int id = clip.GetInstanceID();
            if (Known.TryGetValue(id, out float at))
            {
                return at;
            }
            at = -1f;
            try
            {
                at = Readable(clip) ? Find(clip) : -1f;
            }
            catch (Exception e)
            {
                Log.WarnOnce("racksplit-x:" + clip.name, $"Sound '{clip.name}' could not be read for its strokes: {e.Message}");
            }
            Known[id] = at;
            Log.InfoOnce("racksplit:" + clip.name, at > 0f
                ? $"Sound '{clip.name}' ({clip.length:0.00} s) holds both strokes of a rack: back to {at:0.00} s, home after."
                : $"Sound '{clip.name}' ({clip.length:0.00} s): {(Readable(clip) ? "one stroke" : $"its samples cannot be read ({clip.loadType})")}.");
            return at;
        }

        /// <summary>
        /// The loudest point of each 10 ms; the first stroke where it first passes half the clip's peak; then a gap under a fifth of the peak
        /// for 30 ms or more, followed by a second stroke over 40% of it. The cut: the gap's quietest 10 ms. Pure over the samples: the
        /// offline tests check it.
        /// </summary>
        internal static float Split(float[] samples, int channels, int frequency)
        {
            int ch = Mathf.Max(1, channels);
            int win = Mathf.Max(1, frequency / 100);
            int frames = samples.Length / ch;
            int n = frames / win;
            if (n < 8)
            {
                return -1f;
            }
            var env = new float[n];
            float peak = 0f;
            for (int i = 0; i < n; i++)
            {
                float m = 0f;
                for (int k = i * win * ch, end = (i + 1) * win * ch; k < end; k++)
                {
                    float a = Math.Abs(samples[k]);
                    if (a > m)
                    {
                        m = a;
                    }
                }
                env[i] = m;
                peak = Math.Max(peak, m);
            }
            if (peak < 1e-4f)
            {
                return -1f;
            }
            int first = Array.FindIndex(env, e => e >= 0.5f * peak);
            int quietFrom = -1, quietFor = 0;
            for (int i = first + 1; i < n; i++)
            {
                if (env[i] < 0.2f * peak)
                {
                    if (quietFrom < 0)
                    {
                        quietFrom = i;
                    }
                    quietFor++;
                    continue;
                }
                if (quietFor >= 3 && env[i] >= 0.4f * peak)
                {
                    int best = quietFrom;
                    for (int k = quietFrom; k < i; k++)
                    {
                        if (env[k] < env[best])
                        {
                            best = k;
                        }
                    }
                    return (best + 0.5f) * win / frequency;
                }
                quietFrom = -1;
                quietFor = 0;
            }
            return -1f;
        }

        private static float Find(AudioClip clip)
        {
            var data = new float[clip.samples * Mathf.Max(1, clip.channels)];
            return clip.GetData(data, 0) ? Split(data, clip.channels, clip.frequency) : -1f;
        }
    }

    /// <summary>
    /// Plays a held weapon's own sounds for manual actions, one action at a time: a reload clip is never played
    /// end to end, only the slice that belongs to what the player just did. With none of its own, the game's sound for the action
    /// (GameSounds).
    /// </summary>
    public sealed class WeaponSoundPlayer
    {
        private const float FadeTime = 0.03f;
        /// <summary>How much of a clip as long as a whole reload an action plays, where no cut of it could be made (see <see cref="Whole"/>).</summary>
        internal const float WholeClipPart = 0.6f;

        private readonly WeaponRig _rig;
        private readonly SoundVoices _voices;
        // Two sources: slices are cut and faded at their end, one-shot clips must never be (a faded-out slice used to
        // leave the shared volume at zero, silencing every sound after it).
        // Two slice sources: a new slice cuts the one before it (long slices must not pile up), except one started a moment ago,
        // which goes on in the other source (an en-bloc clip pressed home released the bolt in the same frame: its seating click
        // was cut by the bolt's; sound hunt, 2026-10-20).
        private readonly AudioSource[] _slices = new AudioSource[2];
        private AudioSource _oneShot;
        private readonly double[] _sliceStopDsp = new double[2];
        private readonly double[] _sliceStartDsp = { -10.0, -10.0 };
        private readonly float[] _sliceBaseVolume = { 1f, 1f };
        private int _sliceNow;

        public WeaponSoundPlayer(WeaponRig rig, SoundVoices voices)
        {
            _rig = rig;
            _voices = voices;
            // Which of its own clips sounds for which hand action, once per gun at its first draw (a user's log said nothing of it: "the slide
            // back and forth plays" on a pull could not be traced to a clip; 2026-11-08).
            Log.Guard("sound list", () =>
            {
                if (rig?.Data == null)
                {
                    return;
                }
                string Describe(SoundCue c)
                {
                    if (c.Kind == CueKind.ClipSlice && c.Clip != null)
                    {
                        return $"'{c.Clip.name}' {c.Start:0.00}+{c.Length:0.00} s";
                    }
                    if (c.Clips != null && c.Clips.Length > 0)
                    {
                        var names = new List<string>();
                        foreach (AudioClip x in c.Clips)
                        {
                            if (x != null)
                            {
                                names.Add($"'{x.name}' {x.length:0.00} s");
                            }
                        }
                        return string.Join("/", names.ToArray());
                    }
                    return c.ToString();
                }
                var bound = new List<string>();
                foreach (KeyValuePair<MechSound, List<SoundCue>> kv in rig.Data.Cues)
                {
                    var clips = new List<string>();
                    foreach (SoundCue c in kv.Value)
                    {
                        clips.Add(Describe(c));
                    }
                    bound.Add($"{kv.Key} {string.Join(" + ", clips.ToArray())}");
                }
                // And what a vanilla gun lends it for the hand's main actions it has no sound of its own for (most guns' slide).
                var lent = new List<string>();
                foreach (MechSound k in new[] { MechSound.SlideBack, MechSound.SlideForward, MechSound.MagOut, MechSound.MagIn })
                {
                    SoundCue donor = rig.Data.Cues.ContainsKey(k) ? null : SoundDonors.Find(rig.Data, k);
                    if (donor != null)
                    {
                        lent.Add($"{k} {Describe(donor)}");
                    }
                }
                Log.InfoOnce("soundlist:" + rig.Data.Key, $"{rig.Data.Key}: its own sounds: {(bound.Count > 0 ? string.Join("; ", bound.ToArray()) : "none")}"
                                                           + (lent.Count > 0 ? $"; lent: {string.Join("; ", lent.ToArray())}" : "") + ".");
            });
        }

        public void Play(MechSound kind, Vector3 position, float volume = 1f)
        {
            _kind = kind;
            try
            {
                PlayKind(kind, position, volume);
            }
            finally
            {
                _kind = null;
            }
        }

        /// <summary>The action being played (for a rack's strokes, <see cref="Stroke"/>); null for the shot's cues.</summary>
        private MechSound? _kind;

        /// <summary>The last rack sound the slide's pull played its first stroke of: home plays its second.</summary>
        private AudioClip _rackClip;

        /// <summary>
        /// A rack's one stroke of a clip holding both: the pull its first, home its second (home only from the clip the pull played: a gun's
        /// own home sound may have two parts of its own). A clip whose samples cannot be read: on a gun with a home sound of its own apart
        /// from it (so the pull's clip is the whole rack), its first 55%. False: played as it is.
        /// </summary>
        private bool Stroke(AudioClip clip, float volume)
        {
            if (clip == null || _kind != MechSound.SlideBack && _kind != MechSound.SlideForward || clip.length >= WeaponAnalyzer.WholeReloadSeconds)
            {
                return false;
            }
            float split = RackSplit.Of(clip);
            if (split <= 0f && !RackSplit.Readable(clip) && _kind == MechSound.SlideBack && clip.length > 0.25f && HasOwnHomeApart(clip))
            {
                split = clip.length * 0.55f;
                Log.InfoOnce("rackguess:" + clip.name, $"{_rig.Data.Key}: its pull's sound '{clip.name}' cannot be read, and the gun has a home sound apart: its first 55% for the pull.");
            }
            if (split <= 0f)
            {
                return false;
            }
            if (_kind == MechSound.SlideBack)
            {
                _rackClip = clip;
                return PlaySlice(new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = 0f, Length = split }, volume);
            }
            return clip == _rackClip && PlaySlice(new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = split, Length = clip.length - split }, volume);
        }

        private bool HasOwnHomeApart(AudioClip back)
        {
            if (!_rig.Data.Cues.TryGetValue(MechSound.SlideForward, out List<SoundCue> home))
            {
                return false;
            }
            foreach (SoundCue c in home)
            {
                if (c.Clip != null && c.Clip != back || c.Clips != null && c.Clips.Length > 0 && Array.IndexOf(c.Clips, back) < 0)
                {
                    return true;
                }
            }
            return false;
        }

        private void PlayKind(MechSound kind, Vector3 position, float volume)
        {
            if (VRConfig.ModWeaponSounds.Value)
            {
                if (_rig.Data.Cues.TryGetValue(kind, out List<SoundCue> cues) && cues.Count > 0)
                {
                    bool played = false;
                    foreach (SoundCue cue in cues)
                    {
                        played |= PlayCue(cue, volume);
                    }
                    if (played)
                    {
                        return;
                    }
                }
                else if (kind == MechSound.SlideForward && _rackClip != null && Stroke(_rackClip, volume))
                {
                    return; // (no home sound of its own: the second stroke of the rack its pull played)
                }
                // No sound of its own for this action: the same action of a vanilla weapon of its class.
                SoundCue donor = SoundDonors.Find(_rig.Data, kind);
                if (donor != null && PlayCue(donor, volume))
                {
                    return;
                }
            }
            _voices.Play(kind, position, volume, 0f, _rig.Data);
        }

        /// <summary>An action only some guns have a clip for (the cover, the belt): that clip, else <paramref name="otherwise"/> as it always sounded.</summary>
        public void Play(MechSound kind, MechSound otherwise, Vector3 position, float volume = 1f) =>
            Play(_rig.Data.Cues.ContainsKey(kind) ? kind : otherwise, position, volume);

        /// <summary>Sound events the suppressed fire animation would have played at the shot.</summary>
        public void PlayShotCues()
        {
            if (!VRConfig.ModWeaponSounds.Value)
            {
                return;
            }
            foreach (SoundCue cue in _rig.Data.ShotCues)
            {
                PlayCue(cue);
            }
        }

        // volume: the caller's level on top of the cue's own (a light's switch is the rack's sample at 0.15: it played the whole
        // rack at full volume on every toggle; sound hunt, 2026-10-20).
        private bool PlayCue(SoundCue cue, float volume = 1f)
        {
            try
            {
                switch (cue.Kind)
                {
                    case CueKind.Event:
                        // The clip the event would play, through our own source: no dependence on the mod's sound
                        // bank being set up (or its index being valid) at runtime.
                        return cue.Clips != null && cue.Clips.Length > 0 ? PlayClip(cue, volume) : SendEvent(cue);
                    case CueKind.AudioObject:
                        return PlayAudioObject(cue, volume);
                    case CueKind.ClipSlice:
                        return PlaySlice(cue, volume);
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("cue:" + _rig.Data.Key + cue, $"Weapon sound {cue} of {_rig.Data.Key} failed: {e.Message}");
            }
            return false;
        }

        private GameObject EventTarget
        {
            get
            {
                Weapon w = _rig.Weapon;
                return w.animator != null ? w.animator.gameObject : w.gameObject;
            }
        }

        private bool SendEvent(SoundCue cue)
        {
            GameObject target = EventTarget;
            SoundBank bank = target.GetComponent<SoundBank>();
            if (cue.Function != "EmitParticles" && (bank == null || bank.clips == null || bank.clips.Length == 0
                                                    || cue.Function == "PlaySoundBank" && (cue.IntParameter < 0 || cue.IntParameter >= bank.clips.Length)))
            {
                return false; // nothing valid to play: the mod's own event would throw; use a donor sound instead
            }
            if (cue.Function != "EmitParticles")
            {
                // Always the bank's clip through our own source: the mod's source is often switched off, muted or faded
                // out between its animations, and a message to it would count as played while nothing is heard.
                AudioClip clip = cue.Function == "PlaySoundBank" ? bank.clips[cue.IntParameter] : bank.clips[UnityEngine.Random.Range(0, bank.clips.Length)];
                if (clip == null)
                {
                    return false;
                }
                if (Whole(clip))
                {
                    return PlayStartOf(clip, Audible(bank.clipVolume));
                }
                if (Stroke(clip, Audible(bank.clipVolume)))
                {
                    return true;
                }
                AudioSource src = OneShotSource(_rig.Weapon.reloadAudio != null ? _rig.Weapon.reloadAudio : _rig.Weapon.audio);
                src.PlayOneShot(clip, Audible(bank.clipVolume));
                return true;
            }
            switch (cue.Function)
            {
                case "EmitParticles":
                    // Particles (a casing puff) make no sound: not counted as played, so the action still sounds.
                    target.SendMessage(cue.Function, cue.IntParameter, SendMessageOptions.DontRequireReceiver);
                    return false;
                case "PlaySoundBank":
                    target.SendMessage(cue.Function, cue.IntParameter, SendMessageOptions.DontRequireReceiver);
                    return true;
                case "PlayRandom":
                case "PlayNext":
                    target.SendMessage(cue.Function, SendMessageOptions.DontRequireReceiver);
                    return true;
                default:
                    return false;
            }
        }

        private bool PlayClip(SoundCue cue, float volume)
        {
            AudioClip clip = cue.Clips[cue.Clips.Length == 1 ? 0 : UnityEngine.Random.Range(0, cue.Clips.Length)];
            if (clip == null)
            {
                return false;
            }
            AudioSource template = _rig.Weapon.reloadAudio != null ? _rig.Weapon.reloadAudio : _rig.Weapon.audio;
            if (Whole(clip))
            {
                return PlayStartOf(clip, cue.Volume * volume);
            }
            if (Stroke(clip, cue.Volume * volume))
            {
                return true;
            }
            AudioSource src = OneShotSource(template);
            src.PlayOneShot(clip, cue.Volume * Audible(template != null ? template.volume : 1f) * volume);
            return true;
        }

        /// <summary>
        /// A clip as long as a whole reload (WeaponAnalyzer sliced the ones it could at the actions): no action plays all of it, whichever way
        /// its cue reached here. A fire animation's cycle sound and an event found only at runtime are not sliced ahead.
        /// </summary>
        private static bool Whole(AudioClip clip) => clip.length >= WeaponAnalyzer.WholeReloadSeconds;

        private bool PlayStartOf(AudioClip clip, float volume) =>
            PlaySlice(new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = 0f, Length = WholeClipPart }, volume);

        /// <summary>
        /// A mod's volume as a level to play at: some rigs keep their reload source at zero and only raise it while
        /// their own reload animation runs, which would make every manual action silent.
        /// </summary>
        private static float Audible(float volume) => volume > 0.05f ? Mathf.Min(volume, 1f) : 1f;

        private bool PlayAudioObject(SoundCue cue, float volume)
        {
            Transform t = WeaponRig.Resolve(_rig.Weapon.transform, cue.Path);
            AudioSource src = t != null ? t.GetComponent<AudioSource>() : null;
            if (src == null || src.clip == null)
            {
                return false;
            }
            if (Whole(src.clip))
            {
                return PlayStartOf(src.clip, Audible(src.volume) * volume);
            }
            if (Stroke(src.clip, Audible(src.volume) * volume))
            {
                return true;
            }
            // The object is normally inactive until the animation enables it, so play its clip through our source.
            AudioSource player = OneShotSource(src);
            player.PlayOneShot(src.clip, Audible(src.volume) * volume);
            return true;
        }

        private bool PlaySlice(SoundCue cue, float volume)
        {
            if (cue.Clip == null || cue.Length <= 0f)
            {
                return false;
            }
            AudioSource template = _rig.Weapon.reloadAudio;
            double now = AudioSettings.dspTime;
            AudioSource current = _slices[_sliceNow];
            if (current != null && current.isPlaying && now - _sliceStartDsp[_sliceNow] < 0.25)
            {
                _sliceNow = 1 - _sliceNow; // the one before plays on
            }
            int i = _sliceNow;
            AudioSource src = Configure(ref _slices[i], template);
            src.Stop();
            src.clip = cue.Clip;
            src.time = Mathf.Clamp(cue.Start, 0f, Mathf.Max(0f, cue.Clip.length - 0.01f));
            _sliceBaseVolume[i] = Audible(template != null ? template.volume : 1f) * volume;
            src.volume = _sliceBaseVolume[i];
            src.Play();
            _sliceStartDsp[i] = now;
            _sliceStopDsp[i] = now + cue.Length;
            src.SetScheduledEndTime(_sliceStopDsp[i] + FadeTime);
            return true;
        }

        /// <summary>The source for whole clips (PlayOneShot): full volume, never cut by a slice.</summary>
        private AudioSource OneShotSource(AudioSource template)
        {
            AudioSource src = Configure(ref _oneShot, template);
            src.volume = 1f;
            return src;
        }

        /// <summary>A private source on the weapon that copies the routing of the mod's own source.</summary>
        private AudioSource Configure(ref AudioSource source, AudioSource template)
        {
            if (source == null)
            {
                var go = new GameObject("RavenfieldVR Weapon Audio");
                go.transform.SetParent(_rig.GunBody, false);
                source = go.AddComponent<AudioSource>();
                source.playOnAwake = false;
            }
            if (template != null)
            {
                source.outputAudioMixerGroup = template.outputAudioMixerGroup;
                source.spatialBlend = template.spatialBlend;
                source.minDistance = template.minDistance;
                source.maxDistance = template.maxDistance;
                source.rolloffMode = template.rolloffMode;
                source.priority = template.priority;
                source.pitch = Mathf.Abs(template.pitch) > 0.2f ? template.pitch : 1f;
            }
            else if (source.outputAudioMixerGroup == null)
            {
                try
                {
                    GameManager.SetOutputAudioMixer(source, AudioMixer.FirstPerson);
                }
                catch (Exception)
                {
                    // Mixer not ready yet: the default output still plays the sound.
                }
            }
            return source;
        }

        /// <summary>Fades a slice out over its last few milliseconds so cuts never click.</summary>
        public void Update()
        {
            for (int i = 0; i < _slices.Length; i++)
            {
                AudioSource s = _slices[i];
                if (s == null || !s.isPlaying || s.clip == null)
                {
                    continue;
                }
                double remaining = _sliceStopDsp[i] - AudioSettings.dspTime;
                if (remaining < FadeTime)
                {
                    s.volume = _sliceBaseVolume[i] * Mathf.Clamp01((float)(remaining + FadeTime) / (2f * FadeTime));
                }
            }
        }

        public void Destroy()
        {
            foreach (AudioSource s in _slices)
            {
                if (s != null)
                {
                    UnityEngine.Object.Destroy(s.gameObject);
                }
            }
            if (_oneShot != null)
            {
                UnityEngine.Object.Destroy(_oneShot.gameObject);
            }
        }
    }
}
