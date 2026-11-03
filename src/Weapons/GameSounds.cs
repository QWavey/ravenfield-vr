using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// The game's own sounds for the small handling details (a magazine grinding on the well's edge, a dropped magazine landing, the
    /// bolt catching, a round pressed into a magazine): a slice of a weapon's own sound for an action (its reload's magazine in or out,
    /// its slide back or forward), else a vanilla weapon's of its kind (SoundDonors), played through the mod's 3D voices at a level
    /// and pitch. Never a synthesised sound (user, 2026-11-03: "Youre not allowed to make sounds on your own. Use game sounds"): with
    /// no game sound for it, nothing is played.
    /// </summary>
    internal static class GameSounds
    {
        /// <summary>The game's audio for an action of this weapon, as the clip and the part of it (s) that belongs to the action.</summary>
        internal static bool Find(WeaponRigData d, MechSound kind, out AudioClip clip, out float start, out float length)
        {
            clip = null;
            start = length = 0f;
            if (d == null)
            {
                return false;
            }
            // (Its own only with the mod's own sounds on, as WeaponSoundPlayer; a vanilla weapon's always, the game's own audio.)
            if (VRConfig.ModWeaponSounds.Value && d.Cues.TryGetValue(kind, out List<SoundCue> cues))
            {
                foreach (SoundCue cue in cues)
                {
                    if (Take(cue, out clip, out start, out length))
                    {
                        return true;
                    }
                }
            }
            SoundCue donor = SoundDonors.Find(d, kind);
            return donor != null && Take(donor, out clip, out start, out length);
        }

        private static bool Take(SoundCue cue, out AudioClip clip, out float start, out float length)
        {
            clip = null;
            start = length = 0f;
            if (cue == null)
            {
                return false;
            }
            if (cue.Kind == CueKind.ClipSlice && cue.Clip != null && cue.Length > 0.01f)
            {
                clip = cue.Clip;
                start = cue.Start;
                length = cue.Length;
                return true;
            }
            if (cue.Clips != null && cue.Clips.Length > 0)
            {
                clip = cue.Clips[cue.Clips.Length == 1 ? 0 : Random.Range(0, cue.Clips.Length)];
                if (clip != null && clip.length > 0.01f)
                {
                    length = clip.length;
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Plays part of the game's sound for <paramref name="kind"/> at <paramref name="at"/>: from <paramref name="from"/> for
        /// <paramref name="share"/> of it (shares of the action's part, 0..1), at <paramref name="volume"/> and
        /// <paramref name="pitch"/>. False when the game has no sound for it (nothing is played then).
        /// </summary>
        internal static bool PlayPart(WeaponRigData d, MechSound kind, Vector3 at, float from, float share, float volume, float pitch = 1f)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            if (sys == null || sys.Sounds == null || volume <= 0f || !Find(d, kind, out AudioClip clip, out float start, out float length))
            {
                return false;
            }
            from = Mathf.Clamp01(from);
            share = Mathf.Clamp(share, 0.02f, 1f - from);
            return sys.Sounds.PlayGameClip(clip, start + length * from, Mathf.Max(0.02f, length * share), at, volume, pitch);
        }
    }
}
