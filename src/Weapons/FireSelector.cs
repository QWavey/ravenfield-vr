using System.Collections.Generic;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Semi/auto selector for the player's weapons. A weapon only ever gets the modes its author gave it: an
    /// automatic weapon can be switched to semi and back (Weapon.SetSingleFire / SetAutoFire, the calls Ravenscript
    /// uses for the same thing); a semi-only weapon (shotguns, most pistols, rifles and DMRs authored without auto)
    /// is never made automatic. Weapons with sub-weapons keep the game's own fire-mode key, which cycles the mod's
    /// sub-weapons: that is how mods ship their own fire modes and under-barrel launchers.
    /// </summary>
    public static class FireSelector
    {
        private const float FadeSeconds = 0.025f;

        private static readonly HashSet<Weapon> SemiByPlayer = new HashSet<Weapon>();
        private static readonly List<CutShot> Cuts = new List<CutShot>();

        private struct CutShot
        {
            public AudioSource Source;
            public double EndDsp;
            public float Volume;
        }

        public static bool IsSemiByPlayer(Weapon w) => w != null && SemiByPlayer.Contains(w);

        /// <summary>True when the weapon has both modes (it was authored automatic).</summary>
        public static bool HasModes(Weapon w) => w != null && (w.configuration.auto || SemiByPlayer.Contains(w));

        /// <returns>The new mode ("SEMI" / "AUTO"), or null when the weapon only has one mode.</returns>
        public static string Toggle(Weapon w)
        {
            SemiByPlayer.RemoveWhere(x => x == null);
            if (w == null)
            {
                return null;
            }
            if (w.configuration.auto)
            {
                w.SetSingleFire();
                SemiByPlayer.Add(w);
                return "SEMI";
            }
            if (SemiByPlayer.Remove(w))
            {
                w.SetAutoFire();
                return "AUTO";
            }
            return null;
        }

        /// <summary>
        /// A weapon whose fire sound is a seamless automatic loop plays the whole clip on a semi shot (several
        /// shots' worth). Cut it to one firing cycle, with a short fade so the cut never clicks.
        /// </summary>
        public static void OnShot(Weapon w)
        {
            if (!SemiByPlayer.Contains(w) || w.audioMode != Weapon.AudioMode.Looped)
            {
                return;
            }
            AudioSource src = w.audio;
            if (src == null || src.clip == null || !src.isPlaying)
            {
                return;
            }
            float cycle = Mathf.Clamp(w.configuration.cooldown, 0.06f, 0.3f);
            for (int i = Cuts.Count - 1; i >= 0; i--)
            {
                if (Cuts[i].Source == src)
                {
                    src.volume = Cuts[i].Volume;
                    Cuts.RemoveAt(i);
                }
            }
            double end = AudioSettings.dspTime + cycle;
            src.SetScheduledEndTime(end + FadeSeconds);
            Cuts.Add(new CutShot { Source = src, EndDsp = end, Volume = src.volume });
        }

        public static void Update()
        {
            double now = AudioSettings.dspTime;
            for (int i = Cuts.Count - 1; i >= 0; i--)
            {
                CutShot c = Cuts[i];
                if (c.Source == null)
                {
                    Cuts.RemoveAt(i);
                    continue;
                }
                double past = now - c.EndDsp;
                if (past >= FadeSeconds || !c.Source.isPlaying)
                {
                    c.Source.Stop();
                    c.Source.volume = c.Volume;
                    Cuts.RemoveAt(i);
                }
                else if (past > 0)
                {
                    c.Source.volume = c.Volume * (1f - (float)(past / FadeSeconds));
                }
            }
        }

        /// <summary>Weapons are being torn down (death, respawn, level change).</summary>
        public static void Clear()
        {
            foreach (CutShot c in Cuts)
            {
                if (c.Source != null)
                {
                    c.Source.volume = c.Volume;
                }
            }
            Cuts.Clear();
            SemiByPlayer.Clear();
        }
    }
}
