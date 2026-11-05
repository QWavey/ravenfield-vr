using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RavenfieldVR.Core;
using RavenfieldVR.Runtime;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // WeaponAnalyzer, part: when in the reload the mod plays its sounds. The class and how a weapon is analysed as a whole: WeaponAnalyzer.cs.
    public static partial class WeaponAnalyzer
    {
        // ---- sounds -----------------------------------------------------------------------------------

        /// <summary>
        /// Binds the mod's own reload/fire sounds to manual actions by matching the time each sound plays in the
        /// mod's animation to the time the matching part moves (mag leaves, mag seats, slide back, slide home).
        /// </summary>
        private static void BuildSoundMap(WeaponRigData d, PoseSampler sampler, Weapon w)
        {
            GameObject eventTarget = w.animator != null ? w.animator.gameObject : w.gameObject;
            _bank = eventTarget.GetComponent<SoundBank>();
            var keyframes = new List<KeyValuePair<MechSound, float>>();
            if (d.TMagOut >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.MagOut, d.TMagOut));
            if (d.TMagIn >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.MagIn, d.TMagIn));
            if (d.TSlideBack >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.SlideBack, d.TSlideBack));
            if (d.TSlideForward >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.SlideForward, d.TSlideForward));

            // A particle burst is no sound (MatchCues skips them too): one first in line took the mag-out slot and moved every
            // later sound one slot on.
            var reloadCues = new List<SampledCue>(sampler.ReloadCues).FindAll(c => c.Function != "EmitParticles");
            reloadCues.Sort((a, b) => a.Time.CompareTo(b.Time));
            // A clip as long as a reload is no action's sound: bound to the nearest action whole, the full reload played on a pulled bolt or
            // magazine (user, 2026-11-05: "full reload sound playing when pulling back bolt or pulliung out mag"). Sliced at the actions
            // below as the weapon's reload audio is, from when the animation starts it.
            var wholeClips = new List<KeyValuePair<AudioClip, float>>();
            reloadCues.RemoveAll(c =>
            {
                AudioClip whole = ClipOfCue(c, w);
                if (whole == null || whole.length < WholeReloadSeconds)
                {
                    return false;
                }
                wholeClips.Add(new KeyValuePair<AudioClip, float>(whole, c.Time));
                return true;
            });
            if (keyframes.Count > 0)
            {
                MatchCues(d, keyframes, reloadCues);
            }
            else if (reloadCues.Count > 0)
            {
                // No part motion to align with: the usual order is mag out, mag in, then the slide.
                MechSound[] order = { MechSound.MagOut, MechSound.MagIn, MechSound.SlideBack, MechSound.SlideForward };
                for (int i = 0; i < reloadCues.Count && i < order.Length; i++)
                {
                    AddCue(d, order[i], ToCue(reloadCues[i]));
                }
            }

            // Fire animation: sounds at the shot stay with the shot; later ones are the action cycling.
            foreach (SampledCue c in sampler.FireCues)
            {
                if (c.Time <= 0.1f)
                {
                    d.ShotCues.Add(ToCue(c));
                }
                else if (d.Action == ActionType.Manual && c.Function != "EmitParticles")
                {
                    bool nearForward = d.TFireSlideForward >= 0f && Mathf.Abs(c.Time - d.TFireSlideForward) < Mathf.Abs(c.Time - Mathf.Max(0f, d.TFireSlideBack));
                    MechSound kind = nearForward ? MechSound.SlideForward : MechSound.SlideBack;
                    if (!d.Cues.ContainsKey(kind))
                    {
                        AddCue(d, kind, ToCue(c));
                    }
                }
            }

            // The single reload clip: slice it at the keyframes (never played whole in manual mode).
            AudioClip reloadClip = w.reloadAudio != null ? w.reloadAudio.clip : null;
            if ((reloadClip != null || wholeClips.Count > 0) && keyframes.Count > 0)
            {
                keyframes.Sort((a, b) => a.Value.CompareTo(b.Value));
                for (int i = 0; i < keyframes.Count; i++)
                {
                    MechSound kind = keyframes[i].Key;
                    if (d.Cues.ContainsKey(kind))
                    {
                        continue;
                    }
                    float at = keyframes[i].Value;
                    // The long clip sounding at this moment of the reload, else the reload audio (it starts with the reload).
                    AudioClip clip = reloadClip;
                    float clipStart = 0f;
                    foreach (KeyValuePair<AudioClip, float> whole in wholeClips)
                    {
                        if (at >= whole.Value - 0.15f && at - whole.Value <= whole.Key.length)
                        {
                            clip = whole.Key;
                            clipStart = whole.Value;
                            break;
                        }
                    }
                    if (clip == null)
                    {
                        continue;
                    }
                    float t = Mathf.Max(0f, at - clipStart);
                    float next = i + 1 < keyframes.Count ? keyframes[i + 1].Value - clipStart : clip.length;
                    float start = Mathf.Max(0f, t - 0.06f);
                    float end = Mathf.Min(clip.length, Mathf.Min(next - 0.02f, t + 0.5f));
                    AudioSlicing.Refine(clip, t, ref start, ref end);
                    if (end - start >= 0.08f && start < clip.length)
                    {
                        AddCue(d, kind, new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = start, Length = end - start, SourceTime = at });
                    }
                }
            }
        }

        /// <summary>A clip this long is a whole reload (or most of one), not the sound of one action: the game's own actions are under 0.7 s.</summary>
        internal const float WholeReloadSeconds = 1f;

        /// <summary>What a sampled sound plays: an audio object's clip, or the sound bank's clip for its event; null where that is not one clip.</summary>
        private static AudioClip ClipOfCue(SampledCue c, Weapon w)
        {
            if (c.AudioObjectPath != null)
            {
                Transform t = WeaponRig.Resolve(w.transform, c.AudioObjectPath);
                AudioSource source = t != null ? t.GetComponent<AudioSource>() : null;
                return source != null ? source.clip : null;
            }
            if (_bank == null || _bank.clips == null || _bank.clips.Length == 0)
            {
                return null;
            }
            if (c.Function == "PlaySoundBank")
            {
                return c.IntParameter >= 0 && c.IntParameter < _bank.clips.Length ? _bank.clips[c.IntParameter] : null;
            }
            if (c.Function != "PlayRandom" && c.Function != "PlayNext")
            {
                return null;
            }
            // A bank of whole reloads, one drawn at random: the first stands for them all (the others are as long, cut alike).
            foreach (AudioClip clip in _bank.clips)
            {
                if (clip == null || clip.length < WholeReloadSeconds)
                {
                    return _bank.clips.Length == 1 ? clip : null;
                }
            }
            return _bank.clips[0];
        }

        private struct CueMatch
        {
            public float Cost;
            public int Keyframe;
            public int Cue;
        }

        /// <summary>
        /// One sound per action: each keyframe gets the cue that best fits it. Sounds lead the motion they belong
        /// to (the click comes as the part arrives), so a cue up to 0.6 s before a keyframe fits, while one after
        /// it fits only within 0.15 s and costs more. Near ties go to the usual reload order.
        /// </summary>
        private static void MatchCues(WeaponRigData d, List<KeyValuePair<MechSound, float>> keyframes, List<SampledCue> cues)
        {
            const float lead = 0.6f, lag = 0.15f;
            var matches = new List<CueMatch>();
            for (int k = 0; k < keyframes.Count; k++)
            {
                for (int c = 0; c < cues.Count; c++)
                {
                    if (cues[c].Function == "EmitParticles")
                    {
                        continue; // a particle burst is no sound: bound to a magazine or slide sound it left the action silent
                    }
                    float early = keyframes[k].Value - cues[c].Time;
                    if (early > lead || early < -lag)
                    {
                        continue;
                    }
                    float cost = early >= 0f ? early : -early * 2.5f;
                    matches.Add(new CueMatch { Cost = cost + OrderRank(keyframes[k].Key) * 0.02f, Keyframe = k, Cue = c });
                }
            }
            matches.Sort((a, b) => a.Cost.CompareTo(b.Cost));
            var keyUsed = new bool[keyframes.Count];
            var cueUsed = new bool[cues.Count];
            foreach (CueMatch m in matches)
            {
                if (keyUsed[m.Keyframe] || cueUsed[m.Cue])
                {
                    continue;
                }
                keyUsed[m.Keyframe] = cueUsed[m.Cue] = true;
                AddCue(d, keyframes[m.Keyframe].Key, ToCue(cues[m.Cue]));
            }
        }

        private static int OrderRank(MechSound kind)
        {
            switch (kind)
            {
                case MechSound.MagOut: return 0;
                case MechSound.MagIn: return 1;
                case MechSound.SlideForward: return 2;
                default: return 3;
            }
        }

        /// <summary>The weapon whose sound bank events are being resolved (set by BuildSoundMap).</summary>
        [ThreadStatic] private static SoundBank _bank;

        private static SoundCue ToCue(SampledCue c)
        {
            if (c.AudioObjectPath != null)
            {
                return new SoundCue { Kind = CueKind.AudioObject, Path = c.AudioObjectPath, SourceTime = c.Time };
            }
            var cue = new SoundCue
            {
                Kind = CueKind.Event,
                Function = c.Function,
                IntParameter = c.IntParameter,
                FloatParameter = c.FloatParameter,
                StringParameter = c.StringParameter,
                SourceTime = c.Time
            };
            // Animation events reach every component on the animator's object; its SoundBank holds the clips.
            if (_bank != null && _bank.clips != null && _bank.clips.Length > 0)
            {
                if (c.Function == "PlaySoundBank")
                {
                    if (c.IntParameter >= 0 && c.IntParameter < _bank.clips.Length && _bank.clips[c.IntParameter] != null)
                    {
                        cue.Clips = new[] { _bank.clips[c.IntParameter] };
                    }
                }
                else if (c.Function == "PlayRandom" || c.Function == "PlayNext")
                {
                    var clips = new List<AudioClip>();
                    foreach (AudioClip clip in _bank.clips)
                    {
                        if (clip != null)
                        {
                            clips.Add(clip);
                        }
                    }
                    cue.Clips = clips.Count > 0 ? clips.ToArray() : null;
                }
                cue.Volume = _bank.clipVolume;
            }
            return cue;
        }

        private static void AddCue(WeaponRigData d, MechSound kind, SoundCue cue)
        {
            if (!d.Cues.TryGetValue(kind, out List<SoundCue> list))
            {
                list = new List<SoundCue>();
                d.Cues[kind] = list;
            }
            list.Add(cue);
        }
    }
}
