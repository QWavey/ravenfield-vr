using System;
using System.Collections.Generic;
using RavenfieldVR.Core;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Real sounds for weapons that have none of their own for an action: the same action of a vanilla weapon of the
    /// same kind (rifle magazines and bolts from the RK-44, pistols from the S-IND7 / Automatico, break-opens from the
    /// Thumper). Only self-contained cues (clip slices, resolved sound-bank clips) can be lent.
    /// </summary>
    internal static class SoundDonors
    {
        private static readonly Dictionary<string, WeaponRigData> Donors = new Dictionary<string, WeaponRigData>();
        private static readonly HashSet<string> Missing = new HashSet<string>();

        private static readonly string[] Pistol = { "S-IND7", "AUTOMATICO", "INGRESS 700" };
        private static readonly string[] Rifle = { "RK-44", "AUTOMATICO", "PATRIOT", "SUPERNOVA", "SIGNAL DMR" };
        private static readonly string[] Opening = { "THUMPER" };

        public static SoundCue Find(WeaponRigData d, MechSound kind)
        {
            if (kind != MechSound.MagOut && kind != MechSound.MagIn && kind != MechSound.SlideBack && kind != MechSound.SlideForward)
            {
                return null;
            }
            string[] names = d.Action == ActionType.BreakOpen ? Opening : d.IsPistol ? Pistol : Rifle;
            foreach (string name in names)
            {
                WeaponRigData donor = Donor(name);
                if (donor == null || donor == d || !donor.Cues.TryGetValue(kind, out List<SoundCue> cues))
                {
                    continue;
                }
                foreach (SoundCue cue in cues)
                {
                    if (cue.SelfContained)
                    {
                        return cue;
                    }
                }
            }
            return null;
        }

        private static WeaponRigData Donor(string name)
        {
            if (Donors.TryGetValue(name, out WeaponRigData data))
            {
                return data;
            }
            if (Missing.Contains(name) || WeaponManager.instance == null || WeaponManager.instance.allWeapons == null)
            {
                return null;
            }
            foreach (WeaponManager.WeaponEntry e in WeaponManager.instance.allWeapons)
            {
                if (e == null || e.prefab == null || !string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (e.sourceMod != null && !e.sourceMod.isOfficialContent)
                {
                    continue; // only the game's own weapons lend sounds
                }
                Weapon w = e.prefab.GetComponent<Weapon>();
                if (w == null)
                {
                    break;
                }
                try
                {
                    data = WeaponAnalyzer.Get(w, e);
                    Donors[name] = data;
                    return data;
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("donor:" + name, $"Could not analyse {name} for its sounds: {ex.Message}");
                    break;
                }
            }
            Missing.Add(name);
            return null;
        }

        public static void Clear()
        {
            Donors.Clear();
            Missing.Clear();
        }
    }
}
