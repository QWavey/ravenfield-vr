using System;
using System.IO;
using BepInEx;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    [Serializable]
    internal sealed class WeaponOverride
    {
        /// <summary>Case-insensitive substring of the weapon's name or prefab name.</summary>
        public string match = "";
        /// <summary>SelfLoading, Manual, ShellFed, SingleShot, BreakOpen, Throwable, Melee or NoAmmo; empty keeps the detected one.</summary>
        public string action = "";
        /// <summary>Name of the magazine transform; empty keeps the detected one.</summary>
        public string magazine = "";
        /// <summary>Name of the slide/bolt/pump transform; empty keeps the detected one.</summary>
        public string slide = "";
        /// <summary>Slide travel in metres; 0 keeps the detected value.</summary>
        public float slideTravel;
        /// <summary>"true"/"false" to force slide lock on empty; empty keeps the detected value.</summary>
        public string lockOnEmpty = "";
        /// <summary>Metres the grip point is shifted along the barrel (+ forward) and up.</summary>
        public float gripForward;
        public float gripUp;
        /// <summary>Degrees of extra barrel pitch for this weapon only.</summary>
        public float pitch;
        /// <summary>"true"/"false": whether a fresh magazine can knock the seated one out; empty keeps the detected value.</summary>
        public string magKick = "";
    }

    [Serializable]
    internal sealed class WeaponOverrideFile
    {
        public WeaponOverride[] weapons = new WeaponOverride[0];
    }

    /// <summary>
    /// Per-weapon corrections for mods the automatic analysis gets wrong, read from
    /// BepInEx/config/RavenfieldVR.weapons.json (created with an example entry on first run).
    /// </summary>
    public static class WeaponOverrides
    {
        private static WeaponOverrideFile _file;
        private static DateTime _loadedStamp;
        public static string FilePath => Path.Combine(Paths.ConfigPath, "RavenfieldVR.weapons.json");

        private static void EnsureLoaded()
        {
            try
            {
                string path = FilePath;
                if (!File.Exists(path))
                {
                    var example = new WeaponOverrideFile
                    {
                        weapons = new[]
                        {
                            new WeaponOverride { match = "example: put part of a weapon name here", action = "Manual", slide = "bolt", slideTravel = 0.09f }
                        }
                    };
                    File.WriteAllText(path, JsonUtility.ToJson(example, true));
                }
                DateTime stamp = File.GetLastWriteTimeUtc(path);
                if (_file != null && stamp == _loadedStamp)
                {
                    return;
                }
                _file = JsonUtility.FromJson<WeaponOverrideFile>(File.ReadAllText(path)) ?? new WeaponOverrideFile();
                _loadedStamp = stamp;
            }
            catch (Exception e)
            {
                Log.WarnOnce("overrides", "Could not read " + FilePath + ": " + e.Message);
                _file = new WeaponOverrideFile();
            }
        }

        public static void Apply(Weapon w, WeaponRigData d)
        {
            EnsureLoaded();
            if (_file.weapons == null)
            {
                return;
            }
            string names = (w.name + " " + (w.weaponEntry != null ? w.weaponEntry.name : "") + " " + w.displayName).ToLowerInvariant();
            foreach (WeaponOverride o in _file.weapons)
            {
                if (o == null || string.IsNullOrEmpty(o.match) || o.match.StartsWith("example") || !names.Contains(o.match.ToLowerInvariant()))
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(o.action) && Enum.IsDefined(typeof(ActionType), o.action))
                {
                    d.Action = (ActionType)Enum.Parse(typeof(ActionType), o.action);
                }
                if (!string.IsNullOrEmpty(o.magazine))
                {
                    Transform t = FindChild(w.transform, o.magazine);
                    if (t != null)
                    {
                        d.MagazinePath = WeaponRig.PathOf(w.transform, t);
                    }
                }
                if (!string.IsNullOrEmpty(o.slide))
                {
                    Transform t = FindChild(w.transform, o.slide);
                    if (t != null)
                    {
                        d.SlidePath = WeaponRig.PathOf(w.transform, t);
                        d.SlideRestLocal = t.localPosition;
                    }
                }
                if (o.slideTravel > 0f)
                {
                    d.SlideTravel = d.SlideTravel.normalized * o.slideTravel;
                }
                if (o.lockOnEmpty == "true" || o.lockOnEmpty == "false")
                {
                    d.LockOnEmpty = o.lockOnEmpty == "true";
                }
                if (o.magKick == "true" || o.magKick == "false")
                {
                    d.MagKickable = o.magKick == "true";
                }
                d.GripPoint += d.AimAxis * o.gripForward + d.Up * o.gripUp;
                if (o.pitch != 0f)
                {
                    Quaternion q = Quaternion.AngleAxis(-o.pitch, Vector3.Cross(d.Up, d.AimAxis));
                    d.AimAxis = q * d.AimAxis;
                    d.Up = q * d.Up;
                }
                d.Notes += "override(" + o.match + ") ";
            }
        }

        private static Transform FindChild(Transform root, string name)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                if (string.Equals(t.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return t;
                }
            }
            return null;
        }
    }
}
