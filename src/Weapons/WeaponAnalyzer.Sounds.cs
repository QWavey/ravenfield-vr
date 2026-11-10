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
    /// <summary>What a clip's own name says it is the sound of.</summary>
    internal enum CueName { Unknown, MagOut, MagIn, Slide, Other, CoverUp, CoverDown, BeltOn, BeltOff, Shell, Selector, DryFire, Knock, Grab, TakeMag }

    /// <summary>
    /// The sound of an action, read from its clip's name. Cues were matched to the actions by time alone, and 11 of the sounds over the mods
    /// went to the wrong action: the MRAD's bolt on its magazine, the PPSh's magazine going in as it came out (2026-11-06, the names of all
    /// 344 sound banks read from the bundles). Words as mods use them, the Chinese ones included. Pure: the offline tests check it.
    /// </summary>
    internal static class CueNames
    {
        private const string Holder = @"(mag(azine)?|clip|box|drum)[ _-]?";
        private static readonly Regex MagOut = new Regex(Holder + @"(out|off|release|drop|unlock|eject)|拔出|拆弹匣|弹匣出来", RegexOptions.IgnoreCase);
        private static readonly Regex MagIn = new Regex(Holder + @"(in|insert|attach|load|tap|hit)(?![a-z])|vstavka|插入|插弹匣|安弹匣|按弹匣", RegexOptions.IgnoreCase);
        private static readonly Regex Slide = new Regex(@"bolt|slide|charg|cock|(^|[^a-z])(rack|pull)|chamber|pump|break[ _-]?(open|close)|dosyl|拉栓|枪栓|上膛|空挂|拉开", RegexOptions.IgnoreCase);
        private static readonly Regex Other = new Regex(
            @"inspect|tuck|holster|select|switch|(^|[^a-z])(ads|aim|look|draw|raise|fire|drop)($|[^a-z])|get[ _-]?mag|mag[ _-]?take|检视|切换|切出|步话机|握护木|折叠镜",
            RegexOptions.IgnoreCase);
        private static readonly Regex ClosingWord = new Regex(@"forward|close|release|home|pump[ _-]?in|(^|[^a-z])go($|[^a-z])|关闭|释放|复位", RegexOptions.IgnoreCase);
        private static readonly Regex Showing = new Regex(@"inspect|检视", RegexOptions.IgnoreCase);
        private const string Cover = @"(^|[^a-z]|top)(cap|cover|lid)[ _-]?";
        private static readonly Regex CoverUp = new Regex(Cover + @"(up|open|unlock)", RegexOptions.IgnoreCase);
        private static readonly Regex CoverDown = new Regex(Cover + @"(down|close|shut|lock)", RegexOptions.IgnoreCase);
        private static readonly Regex BeltOn = new Regex(@"ammo[ _-]?up|belt[ _-]?(deploy|lay|on)", RegexOptions.IgnoreCase);
        private static readonly Regex BeltOff = new Regex(@"ammo[ _-]?(down|out)|belt[ _-]?(out|off)", RegexOptions.IgnoreCase);
        private static readonly Regex ShellIn = new Regex(@"shell[ _-]?(in|insert|load)(?![a-z])|sideload|^m\d+_load", RegexOptions.IgnoreCase);
        private static readonly Regex SelectorWord = new Regex(@"select|切换单发|切换连发", RegexOptions.IgnoreCase);
        private static readonly Regex DryFireWord = new Regex(@"dry[ _-]?fire|empty[ _-]?click", RegexOptions.IgnoreCase);
        private static readonly Regex KnockWord = new Regex(@"(mag|box)[ _-]?(kick|knock|konck|peng|hip)", RegexOptions.IgnoreCase);
        private static readonly Regex GrabWord = new Regex(@"(ar|hand)_grab|握护木", RegexOptions.IgnoreCase);
        private static readonly Regex TakeMagWord = new Regex(@"get[ _-]?mag|mag[ _-]?take|mag[a-z ]* and get", RegexOptions.IgnoreCase);

        internal static CueName Of(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return CueName.Unknown;
            }
            // In this order: showing the gun off is no action ("Hecate bolt open inspect"); "MINIMI AMMO UP AND SHELL" is a belt's, "Hecate mag release"
            // and "191 MAG DROP" a magazine's, "charging_handle_release" a bolt's.
            return Showing.IsMatch(name) ? CueName.Other : BeltOn.IsMatch(name) ? CueName.BeltOn : BeltOff.IsMatch(name) ? CueName.BeltOff
                : CoverUp.IsMatch(name) ? CueName.CoverUp : CoverDown.IsMatch(name) ? CueName.CoverDown : ShellIn.IsMatch(name) ? CueName.Shell
                : SelectorWord.IsMatch(name) ? CueName.Selector : DryFireWord.IsMatch(name) ? CueName.DryFire : KnockWord.IsMatch(name) ? CueName.Knock
                : GrabWord.IsMatch(name) ? CueName.Grab : TakeMagWord.IsMatch(name) ? CueName.TakeMag
                : MagOut.IsMatch(name) ? CueName.MagOut : MagIn.IsMatch(name) ? CueName.MagIn : Slide.IsMatch(name) ? CueName.Slide
                : Other.IsMatch(name) ? CueName.Other : CueName.Unknown;
        }

        /// <summary>One of the three actions a reload's motion gives a time to.</summary>
        internal static bool IsReloadAction(CueName said) => said == CueName.MagOut || said == CueName.MagIn || said == CueName.Slide;

        /// <summary>The sound a clip of this name is, on a gun that already has <paramref name="hasSlideBack"/>; null where its name says none.</summary>
        internal static MechSound? SoundOf(CueName said, string name, bool hasSlideBack)
        {
            switch (said)
            {
                case CueName.MagOut: return MechSound.MagOut;
                case CueName.MagIn: return MechSound.MagIn;
                case CueName.Slide: return Closing(name) || hasSlideBack ? MechSound.SlideForward : MechSound.SlideBack;
                case CueName.CoverUp: return MechSound.CoverOpen;
                case CueName.CoverDown: return MechSound.CoverShut;
                case CueName.BeltOn: return MechSound.BeltOn;
                case CueName.BeltOff: return MechSound.BeltOff;
                case CueName.Shell: return MechSound.ShellIn;
                case CueName.Selector: return MechSound.Selector;
                case CueName.DryFire: return MechSound.DryFire;
                case CueName.Knock: return MechSound.MagKnock;
                case CueName.Grab: return MechSound.HandGrab;
                case CueName.TakeMag: return MechSound.Pouch;
                default: return null;
            }
        }

        /// <summary>A bolt's clip for its way home, not back.</summary>
        internal static bool Closing(string name) => ClosingWord.IsMatch(name);

        internal static CueName ActionOf(MechSound kind) =>
            kind == MechSound.MagOut ? CueName.MagOut : kind == MechSound.MagIn ? CueName.MagIn
            : kind == MechSound.CoverOpen ? CueName.CoverUp : kind == MechSound.CoverShut ? CueName.CoverDown : CueName.Slide;

        /// <summary>
        /// Whether a clip named <paramref name="said"/> may sound for <paramref name="kind"/>. Named for an action the gun does not have it may:
        /// the M1878's break latch is a "Hecate mag release", and it has no magazine. <paramref name="gunHasIt"/>: the gun has that action.
        /// </summary>
        internal static bool Allows(CueName said, MechSound kind, bool gunHasIt) =>
            said == CueName.Unknown || said == ActionOf(kind) || IsReloadAction(said) && !gunHasIt;
    }

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
            if (d.TCoverOpen >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.CoverOpen, d.TCoverOpen));
            if (d.TCoverShut >= 0f) keyframes.Add(new KeyValuePair<MechSound, float>(MechSound.CoverShut, d.TCoverShut));

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
                if (CueNames.Of(whole.name) != CueName.Other) // (the ARS MP9's 4.8 s inspect sound was its magazine going in)
                {
                    wholeClips.Add(new KeyValuePair<AudioClip, float>(whole, c.Time));
                }
                return true;
            });
            var said = new CueName[reloadCues.Count];
            for (int i = 0; i < said.Length; i++)
            {
                AudioClip clip = ClipOfCue(reloadCues[i], w);
                said[i] = clip != null ? CueNames.Of(clip.name) : CueName.Unknown;
                // A short clip named as a whole reload is no one hand action's: under a second it is not sliced at the actions (WholeReloadSeconds),
                // and bound by its time it played whole for one of them (the M1 GARRET's 'Garand Reload Full', 0.78 s, on every bolt going home,
                // a clip pushed in with it; 2026-11-08). Left to a vanilla gun's sound for the action.
                if (clip != null && clip.length < WholeReloadSeconds && WholeReloadName.IsMatch(clip.name))
                {
                    said[i] = CueName.Other;
                }
            }
            var taken = new bool[reloadCues.Count];
            if (keyframes.Count > 0)
            {
                MatchCues(d, keyframes, reloadCues, said, taken);
                AssignNamed(d, reloadCues, said, taken, w);
            }
            else if (reloadCues.Count > 0)
            {
                // No part motion to align with: what a clip is named for first, then the usual order: mag out, mag in, the slide.
                AssignNamed(d, reloadCues, said, taken, w);
                MechSound[] order = { MechSound.MagOut, MechSound.MagIn, MechSound.SlideBack, MechSound.SlideForward };
                int next = 0;
                for (int i = 0; i < reloadCues.Count; i++)
                {
                    if (taken[i] || said[i] != CueName.Unknown && !CueNames.IsReloadAction(said[i]))
                    {
                        continue;
                    }
                    while (next < order.Length && d.Cues.ContainsKey(order[next]))
                    {
                        next++;
                    }
                    if (next == order.Length)
                    {
                        break;
                    }
                    AddCue(d, order[next], ToCue(reloadCues[i]));
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
                        // (A long clip named for one action is that action's alone: the PPSh's 1.3 s "mag_in_click" on nothing else.)
                        if (at >= whole.Value - 0.15f && at - whole.Value <= whole.Key.length && CueNames.Allows(CueNames.Of(whole.Key.name), kind, true))
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
                    // (Not back into the action before this one: 40 ms after its moment at the earliest.)
                    float notBefore = i > 0 ? keyframes[i - 1].Value - clipStart + 0.04f : 0f;
                    AudioSlicing.Refine(clip, t, ref start, ref end, notBefore);
                    if (end - start >= 0.08f && start < clip.length)
                    {
                        AddCue(d, kind, new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = start, Length = end - start, SourceTime = at });
                    }
                }
            }
            FillFromBank(d);
            // The gun's own fire-mode sound, which the game plays only on a change to its sub weapon, for a selector with no clip named for it.
            AudioClip modeClip = w.changeFireModeAudio != null ? w.changeFireModeAudio.clip : null;
            if (!d.Cues.ContainsKey(MechSound.Selector) && modeClip != null && modeClip.length < WholeReloadSeconds)
            {
                AddCue(d, MechSound.Selector, new SoundCue { Kind = CueKind.ClipSlice, Clip = modeClip, Start = 0f, Length = modeClip.length });
            }
        }

        /// <summary>A clip this long is a whole reload (or most of one), not the sound of one action: the game's own actions are under 0.7 s.</summary>
        internal const float WholeReloadSeconds = 1f;

        /// <summary>A clip named as a whole reload, not one action of it ("Garand Reload Full", "AK_ReloadEmpty", "tactical reload").</summary>
        internal static readonly Regex WholeReloadName = new Regex(@"reload[ _-]*(full|empty|complete|whole|tactical|tac)($|[^a-z])|(full|empty|complete|tactical)[ _-]*reload",
            RegexOptions.IgnoreCase);

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
        private static void MatchCues(WeaponRigData d, List<KeyValuePair<MechSound, float>> keyframes, List<SampledCue> cues, CueName[] said, bool[] taken)
        {
            const float lead = 0.6f, lag = 0.15f;
            var matches = new List<CueMatch>();
            bool Has(CueName action) => keyframes.Any(k => CueNames.ActionOf(k.Key) == action);
            for (int k = 0; k < keyframes.Count; k++)
            {
                for (int c = 0; c < cues.Count; c++)
                {
                    if (cues[c].Function == "EmitParticles")
                    {
                        continue; // a particle burst is no sound: bound to a magazine or slide sound it left the action silent
                    }
                    float early = keyframes[k].Value - cues[c].Time;
                    if (early > lead || early < -lag || !CueNames.Allows(said[c], keyframes[k].Key, Has(said[c])))
                    {
                        continue;
                    }
                    float cost = early >= 0f ? early : -early * 2.5f;
                    matches.Add(new CueMatch { Cost = cost + OrderRank(keyframes[k].Key) * 0.02f, Keyframe = k, Cue = c });
                }
            }
            matches.Sort((a, b) => a.Cost.CompareTo(b.Cost));
            var keyUsed = new bool[keyframes.Count];
            foreach (CueMatch m in matches)
            {
                if (keyUsed[m.Keyframe] || taken[m.Cue])
                {
                    continue;
                }
                keyUsed[m.Keyframe] = taken[m.Cue] = true;
                AddCue(d, keyframes[m.Keyframe].Key, ToCue(cues[m.Cue]));
            }
        }

        /// <summary>
        /// A clip named for an action that has no sound of its own yet goes to it, whenever in the reload it plays: out of the time window the
        /// action fell to a vanilla donor with the gun's own clip in its bank, and with no part motion at all the first clip was taken for the
        /// magazine coming out whatever it was (the ARS Glock's magazine out on its magazine in, that one on its slide).
        /// </summary>
        private static void AssignNamed(WeaponRigData d, List<SampledCue> cues, CueName[] said, bool[] taken, Weapon w)
        {
            for (int i = 0; i < cues.Count; i++)
            {
                if (taken[i] || !CueNames.IsReloadAction(said[i]))
                {
                    continue;
                }
                AudioClip clip = ClipOfCue(cues[i], w);
                MechSound? kind = CueNames.SoundOf(said[i], clip != null ? clip.name : "", d.Cues.ContainsKey(MechSound.SlideBack));
                if (kind.HasValue && !d.Cues.ContainsKey(kind.Value))
                {
                    taken[i] = true;
                    AddCue(d, kind.Value, ToCue(cues[i]));
                }
            }
        }

        /// <summary>
        /// What the gun's own sound bank has by name for what is still without a sound: the cover and the belt, which the mod's reload plays at
        /// no motion of ours (the MK46's CAP UP, the PKM's AMMO UP: the cover sounded like the bolt), a shell going in, and an action the sampled
        /// reload did not reach (a gun's bolt clip played only in its other reload). Long clips are left to the slices.
        /// </summary>
        private static void FillFromBank(WeaponRigData d)
        {
            if (_bank == null || _bank.clips == null)
            {
                return;
            }
            for (int i = 0; i < _bank.clips.Length; i++)
            {
                AudioClip clip = _bank.clips[i];
                if (clip == null)
                {
                    continue;
                }
                MechSound? kind = CueNames.SoundOf(CueNames.Of(clip.name), clip.name, d.Cues.ContainsKey(MechSound.SlideBack));
                if (kind.HasValue && !d.Cues.ContainsKey(kind.Value) && (clip.length < WholeReloadSeconds || kind == MechSound.Pouch)) // (a "get mag" runs a second)
                {
                    AddCue(d, kind.Value, ToCue(new SampledCue { Function = "PlaySoundBank", IntParameter = i }));
                }
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
