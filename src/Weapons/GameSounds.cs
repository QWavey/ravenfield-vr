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

    /// <summary>
    /// A dropped magazine landing (user, 2026-11-03, idea 5: they hit the ground without a sound, the cases with one): the end of its
    /// gun's own magazine-in sound, the metal knock of it seating, lower and as loud as it hit, duller and quieter on earth or grass.
    /// Game audio only (GameSounds): a gun with none of its own lends a vanilla weapon's.
    /// </summary>
    internal sealed class MagazineClatter : MonoBehaviour
    {
        internal MagazineProp Prop;
        private float _last = -1f;

        private void OnCollisionEnter(Collision c)
        {
            if (Prop == null || Prop.HeldBy != null || Prop.Data == null || Prop.OnRail)
            {
                return;
            }
            float speed = c.relativeVelocity.magnitude;
            float now = Time.unscaledTime;
            if (speed < 0.8f || now - _last < 0.12f)
            {
                return;
            }
            _last = now;
            bool soft = CaseSound.IsSoft(c.collider);
            float loud = Mathf.Clamp01((speed - 0.8f) / 4f);
            float volume = soft ? 0.1f + 0.15f * loud : 0.18f + 0.37f * loud;
            float pitch = (soft ? 0.66f : 0.8f) * Random.Range(0.95f, 1.05f);
            Vector3 at = c.contactCount > 0 ? c.GetContact(0).point : transform.position;
            GameSounds.PlayPart(Prop.Data, MechSound.MagIn, at, 0.6f, 0.4f, volume, pitch);
        }
    }
}
