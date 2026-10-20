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
    /// Plays a held weapon's own sounds for manual actions, one action at a time: a reload clip is never played
    /// end to end, only the slice that belongs to what the player just did. Falls back to synthesised sounds.
    /// </summary>
    public sealed class WeaponSoundPlayer
    {
        private const float FadeTime = 0.03f;

        private readonly WeaponRig _rig;
        private readonly ProceduralSounds _fallback;
        // Two sources: slices are cut and faded at their end, one-shot clips must never be (a faded-out slice used to
        // leave the shared volume at zero, silencing every sound after it).
        private AudioSource _slice;
        private AudioSource _oneShot;
        private double _sliceStopDsp;
        private float _sliceBaseVolume = 1f;

        public WeaponSoundPlayer(WeaponRig rig, ProceduralSounds fallback)
        {
            _rig = rig;
            _fallback = fallback;
        }

        public void Play(MechSound kind, Vector3 position, float volume = 1f)
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
                // No sound of its own for this action: the same action of a vanilla weapon of its class.
                SoundCue donor = SoundDonors.Find(_rig.Data, kind);
                if (donor != null && PlayCue(donor, volume))
                {
                    return;
                }
            }
            _fallback.Play(kind, position, volume);
        }

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
            AudioSource src = OneShotSource(template);
            src.PlayOneShot(clip, cue.Volume * Audible(template != null ? template.volume : 1f) * volume);
            return true;
        }

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
            AudioSource src = SliceSource(template);
            src.Stop();
            src.clip = cue.Clip;
            src.time = Mathf.Clamp(cue.Start, 0f, Mathf.Max(0f, cue.Clip.length - 0.01f));
            _sliceBaseVolume = Audible(template != null ? template.volume : 1f) * volume;
            src.volume = _sliceBaseVolume;
            src.Play();
            _sliceStopDsp = AudioSettings.dspTime + cue.Length;
            src.SetScheduledEndTime(_sliceStopDsp + FadeTime);
            return true;
        }

        /// <summary>The source for reload-clip slices (stopped, scheduled and faded per slice).</summary>
        private AudioSource SliceSource(AudioSource template) => Configure(ref _slice, template);

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
            if (_slice == null || !_slice.isPlaying || _slice.clip == null)
            {
                return;
            }
            double remaining = _sliceStopDsp - AudioSettings.dspTime;
            if (remaining < FadeTime)
            {
                _slice.volume = _sliceBaseVolume * Mathf.Clamp01((float)(remaining + FadeTime) / (2f * FadeTime));
            }
        }

        public void Destroy()
        {
            if (_slice != null)
            {
                UnityEngine.Object.Destroy(_slice.gameObject);
            }
            if (_oneShot != null)
            {
                UnityEngine.Object.Destroy(_oneShot.gameObject);
            }
        }
    }
}
