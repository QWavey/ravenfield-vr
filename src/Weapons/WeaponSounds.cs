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
            // (A quarter of the peak, not half: with the slide's slam home more than twice as loud as the pull, the "first stroke"
            // was the slam, nothing came after it, and the pull's slice was left holding both; review of 2026-11-10.)
            int first = Array.FindIndex(env, e => e >= 0.25f * peak);
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

        /// <summary>
        /// For ONE clip that a gun plays for its pull and for its slide going home (so it holds both) in which <see cref="Split"/>
        /// heard no gap (a handle pulled and let go at once: the vanilla S-IND7's 0.68 s charging handle, lent to the Glock 17 mod
        /// for both, played whole on the pull and whole again on the release; the user's log, 2026-11-10): the quietest 10 ms
        /// between 30% and 70% of it, when a stroke of at least 30% of the peak stands on either side. -1: one stroke and its
        /// tail. Pure over the samples: the offline tests check it.
        /// </summary>
        internal static float SplitNoGap(float[] samples, int channels, int frequency)
        {
            int ch = Mathf.Max(1, channels);
            int win = Mathf.Max(1, frequency / 100);
            int n = samples.Length / ch / win;
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
                    m = Math.Max(m, Math.Abs(samples[k]));
                }
                env[i] = m;
                peak = Math.Max(peak, m);
            }
            if (peak < 1e-4f)
            {
                return -1f;
            }
            // The deepest dip with a stroke on either side: for every 10 ms, the loudest before it and the loudest after it; where
            // both are at least a quarter of the peak and it is at most 60% of the quieter one, the one lowest against its
            // strokes is the cut. (Before: the quietest point between 30% and 70% of the clip. The S-IND7's handle has both
            // strokes in its first part and a tail after: nothing was found, and the Glock played all of it on the pull and all
            // of it again on the release; user, 2026-11-10, 1.17.2.)
            var after = new float[n];
            float run = 0f;
            for (int i = n - 1; i >= 0; i--)
            {
                after[i] = run;
                run = Math.Max(run, env[i]);
            }
            float before = 0f, bestRatio = 0.6f;
            int best = -1;
            for (int i = 0; i < n; i++)
            {
                float quieter = Math.Min(before, after[i]);
                if (i >= 2 && i < n - 2 && quieter >= 0.25f * peak && env[i] <= bestRatio * quieter)
                {
                    bestRatio = env[i] / quieter;
                    best = i;
                }
                before = Math.Max(before, env[i]);
            }
            return best >= 0 ? (best + 0.5f) * win / frequency : -1f;
        }

        private static readonly Dictionary<int, float> KnownBoth = new Dictionary<int, float>();

        /// <summary>Where one clip bound to both strokes turns from the pull to the slide going home (<see cref="SplitNoGap"/>; a clip
        /// that cannot be read: after its first 55%), or -1.</summary>
        internal static float OfBoth(AudioClip clip)
        {
            if (clip == null || clip.length < 0.3f || clip.length >= WeaponAnalyzer.WholeReloadSeconds)
            {
                return -1f;
            }
            int id = clip.GetInstanceID();
            if (KnownBoth.TryGetValue(id, out float at))
            {
                return at;
            }
            at = -1f;
            try
            {
                if (Readable(clip))
                {
                    var data = new float[clip.samples * Mathf.Max(1, clip.channels)];
                    at = clip.GetData(data, 0) ? SplitNoGap(data, clip.channels, clip.frequency) : -1f;
                }
                else if (clip.length > 0.35f)
                {
                    at = clip.length * 0.55f;
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("rackboth-x:" + clip.name, $"Sound '{clip.name}' could not be read for its two strokes: {e.Message}");
            }
            KnownBoth[id] = at;
            return at;
        }

        /// <summary>
        /// For a pull's slice of a clip that cannot be read: where to cut it so it does not run on into the slide going home. The
        /// slice starts 0.06 s before its action's moment (<paramref name="sourceTime"/>, in the reload animation's seconds:
        /// WeaponAnalyzer.BuildSoundMap); the animation's slide-home moment, when it falls inside the slice, is the cut (20 ms
        /// before it); with none known, a slice over 0.3 s is cut after its first 55%. -1: left whole. Pure: the offline tests check it.
        /// </summary>
        internal static float GuessCut(float start, float length, float sourceTime, float slideHomeTime, out bool byAnimation)
        {
            byAnimation = false;
            if (slideHomeTime >= 0f && slideHomeTime > sourceTime)
            {
                float home = start + 0.06f + (slideHomeTime - sourceTime) - 0.02f;
                if (home > start + 0.05f && home < start + length - 0.03f)
                {
                    byAnimation = true;
                    return home;
                }
            }
            return length > 0.3f ? start + length * 0.55f : -1f;
        }

        private static float Find(AudioClip clip)
        {
            var data = new float[clip.samples * Mathf.Max(1, clip.channels)];
            return clip.GetData(data, 0) ? Split(data, clip.channels, clip.frequency) : -1f;
        }

        private static readonly Dictionary<long, float> KnownSlices = new Dictionary<long, float>();

        /// <summary>
        /// The same within a slice of a longer clip (a reload cut at its actions, a vanilla gun's lent to most guns for their slide): where the
        /// slice's two strokes turn, in the clip's own seconds, or -1. A pull's slice running on into the slide going home played both on the
        /// pull (the user's report on most guns, pistols too, 2026-11-08).
        /// </summary>
        internal static float OfSlice(AudioClip clip, float start, float length)
        {
            if (clip == null || length < 0.12f || length > 1.2f)
            {
                return -1f;
            }
            long key = ((long)clip.GetInstanceID() << 24) ^ Mathf.RoundToInt(start * 1000f);
            if (KnownSlices.TryGetValue(key, out float at))
            {
                return at;
            }
            at = -1f;
            try
            {
                if (Readable(clip))
                {
                    int ch = Mathf.Max(1, clip.channels);
                    int from = Mathf.Clamp(Mathf.RoundToInt(start * clip.frequency), 0, Mathf.Max(0, clip.samples - 1));
                    int frames = Mathf.Clamp(Mathf.RoundToInt(length * clip.frequency), 1, clip.samples - from);
                    var data = new float[frames * ch];
                    if (clip.GetData(data, from))
                    {
                        float rel = Split(data, ch, clip.frequency);
                        at = rel > 0f ? start + rel : -1f;
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarnOnce("racksplit-sx:" + clip.name, $"Sound '{clip.name}' could not be read for its strokes: {e.Message}");
            }
            KnownSlices[key] = at;
            Log.InfoOnce($"racksplit-s:{clip.name}@{start:0.00}", at > 0f
                ? $"Sound '{clip.name}' {start:0.00}+{length:0.00} s holds both strokes of a rack: back to {at:0.00} s, home after."
                : $"Sound '{clip.name}' {start:0.00}+{length:0.00} s: {(Readable(clip) ? "one stroke" : $"its samples cannot be read ({clip.loadType})")}.");
            return at;
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
                // (Once a gun: built on every draw before, its lists and four donor searches, for a line logged once.)
                if (rig?.Data == null || Log.Logged("soundlist:" + rig.Data.Key))
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
            if (kind == MechSound.SlideBack)
            {
                // (A new pull: the last one's rack is not this one's. Kept, a pull played whole left it, and the next release played the
                // home stroke of an earlier pull; sounds scout, 2026-11-12.)
                _rackClip = null;
                _rackSliceClip = null;
            }
            // (The showcase's sound event, said once here: a gun's own cue does not go through the voices' Play.)
            if (SoundVoices.Showcased != null && kind != MechSound.Casing && kind != MechSound.CasingSoft)
            {
                SoundVoices.Showcased(kind);
            }
            SoundVoices.InGunPlay++;
            try
            {
                PlayKind(kind, position, volume);
            }
            finally
            {
                SoundVoices.InGunPlay--;
                _kind = null;
            }
        }

        /// <summary>The action being played (for a rack's strokes, <see cref="Stroke"/>); null for the shot's cues.</summary>
        private MechSound? _kind;

        /// <summary>The last rack sound the slide's pull played its first stroke of: home plays its second.</summary>
        private AudioClip _rackClip;
        /// <summary>The same for a rack within a slice of a longer clip: the clip and where its second stroke runs (seconds).</summary>
        private AudioClip _rackSliceClip;
        private float _rackSliceFrom, _rackSliceTo;

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
            if (split <= 0f && clip.length > 0.3f && BoundTo(MechSound.SlideBack, clip) && BoundTo(MechSound.SlideForward, clip))
            {
                // One clip for the pull and for the slide going home, its own or lent, with no gap heard in it: it holds both.
                float cut = RackSplit.OfBoth(clip);
                if (!(cut > 0.03f && cut < clip.length - 0.03f) && clip.length > 0.45f)
                {
                    // No two strokes heard in it, and the gun still plays it for both: half each, so neither action plays the
                    // other's (the user's word on the Glock: "we only need the slide back sound and slide forth").
                    cut = clip.length * 0.5f;
                    Log.InfoOnce("rackboth-half:" + _rig.Data.Key + ":" + clip.name, $"{_rig.Data.Key}: '{clip.name}' ({clip.length:0.00} s) is played for its pull and its slide going home and no two strokes were heard in it: its first half for the pull, its second for home.");
                }
                if (cut > 0.03f && cut < clip.length - 0.03f)
                {
                    Log.InfoOnce("rackboth:" + _rig.Data.Key + ":" + clip.name, $"{_rig.Data.Key}: one sound '{clip.name}' ({clip.length:0.00} s) is its pull and its slide going home: the pull plays it to {cut:0.00} s, home the rest.");
                    return _kind == MechSound.SlideBack
                        ? PlaySlice(new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = 0f, Length = cut }, volume)
                        : PlaySlice(new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = cut, Length = clip.length - cut }, volume);
                }
            }
            if (split <= 0f && !RackSplit.Readable(clip) && _kind == MechSound.SlideBack && clip.length > 0.25f && HasOwnHomeApart(clip))
            {
                split = clip.length * 0.55f;
                Log.InfoOnce("rackguess:" + clip.name, $"{_rig.Data.Key}: its pull's sound '{clip.name}' cannot be read, and the gun has a home sound apart: its first 55% for the pull.");
            }
            if (split <= 0f && !RackSplit.Readable(clip) && _kind == MechSound.SlideBack && clip.length > 0.35f)
            {
                // The gun's one clip for its slide, too long for one stroke and not to be listened into: the pull plays its first
                // 55%. The rest is NOT kept for the slide going home (it may be the pull's own tail): home has the gun's kind's
                // sound, as for any gun with no home sound of its own.
                Log.InfoOnce("rackguess-w:" + clip.name, $"{_rig.Data.Key}: its pull's sound '{clip.name}' ({clip.length:0.00} s) cannot be read and is the gun's only slide sound: its first 55% for the pull.");
                return PlaySlice(new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = 0f, Length = clip.length * 0.55f }, volume);
            }
            if (split <= 0f && !RackSplit.Readable(clip) && _kind == MechSound.SlideForward && clip.length > 0.35f && IsPullClip(clip))
            {
                // The gun binds the SAME unreadable clip to its pull and to its slide going home (the QBZ-191/192's 0.36 s
                // bolt clip, seen in the user's log, 2026-11-10): the pull played its first 55% above, and home played all
                // of it again, pull and all. Home is the rest of it.
                float from = clip.length * 0.55f;
                return PlaySlice(new SoundCue { Kind = CueKind.ClipSlice, Clip = clip, Start = from, Length = clip.length - from }, volume);
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

        /// <summary>Whether the gun's own pull is bound to this clip (as a whole clip, not a slice of it).</summary>
        private bool IsPullClip(AudioClip clip)
        {
            if (!_rig.Data.Cues.TryGetValue(MechSound.SlideBack, out List<SoundCue> pull))
            {
                return false;
            }
            foreach (SoundCue c in pull)
            {
                if (c != null && (c.Kind != CueKind.ClipSlice && c.Clip == clip || c.Clips != null && Array.IndexOf(c.Clips, clip) >= 0))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Whether this action of the gun plays <paramref name="clip"/> whole: by its own cue, or with none of its own by the lent one.</summary>
        private bool BoundTo(MechSound kind, AudioClip clip)
        {
            if (_rig.Data.Cues.TryGetValue(kind, out List<SoundCue> cues) && cues.Count > 0)
            {
                foreach (SoundCue c in cues)
                {
                    if (Uses(c, clip))
                    {
                        return true;
                    }
                }
                return false;
            }
            return Uses(SoundDonors.Find(_rig.Data, kind), clip);
        }

        private static bool Uses(SoundCue c, AudioClip clip) =>
            c != null && (c.Kind != CueKind.ClipSlice && c.Clip == clip || c.Clips != null && Array.IndexOf(c.Clips, clip) >= 0);

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
                        Log.InfoOnce("sound:" + _rig.Data.Key + ":" + kind, $"{_rig.Data.Key}: {kind} played by its own sound.");
                        return;
                    }
                }
                else if (kind == MechSound.SlideForward && _rackClip != null && Stroke(_rackClip, volume))
                {
                    return; // (no home sound of its own: the second stroke of the rack its pull played)
                }
                if (kind == MechSound.SlideForward && !_rig.Data.Cues.ContainsKey(MechSound.SlideForward) && _rackSliceClip != null
                    && PlaySlice(new SoundCue { Kind = CueKind.ClipSlice, Clip = _rackSliceClip, Start = _rackSliceFrom, Length = _rackSliceTo - _rackSliceFrom }, volume))
                {
                    return; // (the second stroke of the pull's sliced rack: a vanilla gun's lent slice holds both)
                }
                // No sound of its own for this action: the same action of a vanilla weapon of its class.
                SoundCue donor = SoundDonors.Find(_rig.Data, kind);
                if (donor != null && PlayCue(donor, volume))
                {
                    Log.InfoOnce("sound:" + _rig.Data.Key + ":" + kind, $"{_rig.Data.Key}: {kind} played by the lent sound.");
                    return;
                }
                Log.InfoOnce("sound:" + _rig.Data.Key + ":" + kind, $"{_rig.Data.Key}: {kind} played by the game's own ({(donor == null ? "none lent" : "the lent one did not play")}).");
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
            if (_kind == MechSound.SlideBack)
            {
                // A pull's slice that runs on into the slide going home: its first stroke only (RackSplit.OfSlice).
                // (Each pull starts clean: the rest of an earlier pull's slice is not this one's home stroke.)
                _rackSliceClip = null;
                float split = RackSplit.OfSlice(cue.Clip, cue.Start, cue.Length);
                bool known = split > 0f; // (heard in the samples, or below: told by the gun's own animation)
                if (split <= 0f && !RackSplit.Readable(cue.Clip) && cue.Kind == CueKind.ClipSlice && cue.SourceTime > 0f) // (a slice cut from a reload's clip at its action's moment: only those carry it)
                {
                    // A mod's compressed clip cannot be listened into (most mods' are: the Glock 17's two reload clips, user,
                    // 2026-11-10: "the glock17 still has the pull back and forward sound when only pulled back"). Cut by what the
                    // gun's own reload animation says instead: where its slide goes home, when that falls inside the pull's slice;
                    // with no such moment known, a slice longer than a single stroke (0.3 s) is taken to run on into the next
                    // one and its first 55% is the pull, as for a whole clip (Stroke).
                    WeaponRigData d = _rig.Data;
                    // (The animation's moment only for the gun's own slice: a slice lent by another gun carries that gun's
                    // timeline, and this gun's slide-home moment against it cut the pull at a chance place.)
                    bool own = d.Cues.TryGetValue(MechSound.SlideBack, out List<SoundCue> mine) && mine.Exists(c => c != null && c.Clip == cue.Clip && Mathf.Abs(c.Start - cue.Start) < 0.001f);
                    split = RackSplit.GuessCut(cue.Start, cue.Length, cue.SourceTime, own ? d.TSlideForward : -1f, out bool byAnimation);
                    float home = byAnimation ? split : -1f;
                    known = byAnimation;
                    if (split > 0f)
                    {
                        Log.InfoOnce($"rackguess-s:{d.Key}:{cue.Clip.name}", $"{d.Key}: its pull's sound (a slice of '{cue.Clip.name}', {cue.Start:0.00}+{cue.Length:0.00} s) cannot be read: "
                            + (home > 0f && Mathf.Approximately(split, home) ? $"cut at {split:0.00} s, where its reload's slide goes home." : $"its first 55% for the pull (to {split:0.00} s)."));
                    }
                }
                if (split > cue.Start + 0.03f && split < cue.Start + cue.Length - 0.03f)
                {
                    // The rest is the slide going home only where the cut is known (heard, or the animation's): after a guessed
                    // 55% it may be the pull's own tail, and was played as the "home" stroke on every release.
                    _rackSliceClip = known ? cue.Clip : null;
                    _rackSliceFrom = split;
                    _rackSliceTo = cue.Start + cue.Length;
                    cue = new SoundCue { Kind = CueKind.ClipSlice, Clip = cue.Clip, Start = cue.Start, Length = split - cue.Start, Volume = cue.Volume };
                }
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

        /// <summary>
        /// A sound the gun carries on a source of its own (an M1's clip ping), played whole through the mod's source with that
        /// source's routing. Played on the gun's own source it came "sometimes" (the user, 2026-11-11): that object is switched
        /// off until the game's animation switches it on, and the game stops and restarts the source for its own sounds.
        /// </summary>
        /// <summary>A clip of the gun's own, through the private one-shot source routed as <paramref name="template"/>.</summary>
        internal bool PlayOwnClip(AudioClip clip, AudioSource template, float volume = 1f)
        {
            if (clip == null)
            {
                return false;
            }
            AudioSource player = OneShotSource(template);
            player.PlayOneShot(clip, Audible(template != null ? template.volume : 1f) * volume);
            return true;
        }

        internal bool PlayOwn(AudioSource own, float volume = 1f)
        {
            if (own == null || own.clip == null)
            {
                return false;
            }
            AudioSource player = OneShotSource(own);
            player.PlayOneShot(own.clip, Audible(own.volume) * volume);
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
