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

        /// <summary>
        /// A weapon picked up again (a new instance of the same entry) gets the player's semi mode back. Set on the
        /// configuration directly: SetSingleFire on a weapon that has not run Start yet took its volume for the authored one.
        /// </summary>
        public static void RestoreSemi(Weapon w)
        {
            SemiByPlayer.RemoveWhere(IsGone); // (the dropped instance this one replaces: one dead weapon a drop or swap, kept to a toggle)
            if (w == null || w.configuration == null || !w.configuration.auto)
            {
                return;
            }
            w.configuration.auto = false;
            SemiByPlayer.Add(w);
        }

        private static readonly System.Predicate<Weapon> IsGone = x => x == null;

        /// <summary>True when the weapon has both modes (it was authored automatic).</summary>
        public static bool HasModes(Weapon w) => w != null && (w.configuration.auto || SemiByPlayer.Contains(w));

        /// <returns>The new mode ("SEMI" / "AUTO"), or null when the weapon only has one mode.</returns>
        public static string Toggle(Weapon w)
        {
            SemiByPlayer.RemoveWhere(IsGone);
            if (w == null)
            {
                return null;
            }
            if (w.configuration.auto)
            {
                w.SetSingleFire();
                SemiByPlayer.Add(w);
                MoveLever(w, SwitchSemi);
                return "SEMI";
            }
            if (SemiByPlayer.Remove(w))
            {
                w.SetAutoFire();
                MoveLever(w, SwitchAuto);
                return "AUTO";
            }
            return null;
        }

        private static readonly int SwitchSemi = Animator.StringToHash("switchsemi"), SwitchAuto = Animator.StringToHash("switchauto");

        /// <summary>A gun drawn again in semi: the animator starts over on its authored lever, which read auto on a gun firing single shots.</summary>
        internal static void ShowLever(Weapon w)
        {
            if (w != null && SemiByPlayer.Contains(w))
            {
                MoveLever(w, SwitchSemi);
            }
        }

        /// <summary>
        /// The gun's own selector lever, where its animator has the triggers for it: the XM8's, the MG3's and the QBZ-95's clips move it and
        /// no script of theirs ever plays them, so it stayed on auto (read from the bundles, 2026-11-06). A gun with a fire-mode script of its
        /// own never comes here.
        /// </summary>
        private static void MoveLever(Weapon w, int trigger)
        {
            Animator animator = w.animator;
            if (animator == null || !animator.isActiveAndEnabled)
            {
                return;
            }
            foreach (AnimatorControllerParameter p in animator.parameters)
            {
                if (p.nameHash == trigger && p.type == AnimatorControllerParameterType.Trigger)
                {
                    animator.ResetTrigger(trigger == SwitchSemi ? SwitchAuto : SwitchSemi); // (one still waiting, in a state with no way out: it took the lever the wrong way later)
                    animator.SetTrigger(trigger);
                    return;
                }
            }
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

        /// <summary>
        /// The mod's weapons are being torn down (level change, VR stopped). A weapon still alive goes back to the automatic
        /// fire it was authored with: forgotten while still set to semi, it was semi-only from then on (VR stopped mid-match:
        /// the flat game has no selector, and a VR restart no longer counted it automatic; bug hunt, 2026-10-23).
        /// </summary>
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
            foreach (Weapon w in SemiByPlayer)
            {
                if (w == null || w.configuration == null)
                {
                    continue;
                }
                try
                {
                    w.SetAutoFire(); // as Toggle: the loop and the volume too
                }
                catch (System.Exception e)
                {
                    w.configuration.auto = true; // (no audio source, or one gone: the mode alone)
                    Core.Log.ExceptionOnce("FireSelector.Clear", e);
                }
            }
            SemiByPlayer.Clear();
        }
    }
}
