using System.Collections.Generic;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Magazine lines measured offline on the guns' real meshes (tools/export_checks/mag_line_scan.py: the magazine slid out
    /// against the gun's side outline at 1 mm) where they are clearly better than the analysed line. The in-game fit counts on
    /// depth-rendered volumes and missed it on the M4A1 family: 17-20 points out of place in game, 63-200 offline, kept, and the
    /// magazines went in a little through the gun near the end (user, 2026-10-20). It also tilts only fore/aft, and these lines
    /// lean sideways too. Applied after the analysis, the user's overrides and the export (the scans read the line the game
    /// analysed); the table holds the measured line itself, in the gun's frame.
    /// </summary>
    internal static partial class MagLineTable
    {
        private static readonly HashSet<WeaponRigData> Applied = new HashSet<WeaponRigData>();

        /// <summary>Whether this rig's magazine line came from the table (the in-game fit leaves it alone).</summary>
        public static bool From(WeaponRigData d) => d != null && Applied.Contains(d);

        public static void Apply(WeaponRigData d)
        {
            if (d == null || d.Key == null || !Lines.TryGetValue(d.Key, out Vector3 line) || line.sqrMagnitude < 0.5f)
            {
                return;
            }
            // The line itself, in the gun's frame (the well does not move): as a tilt on top of the analysed line it landed elsewhere
            // when the game's analysis took its line from another source than the export's (review, 2026-10-20).
            float turned = d.MagEjectAxis.sqrMagnitude > 0.5f ? Vector3.Angle(d.MagEjectAxis, line) : 0f;
            d.MagEjectAxis = line.normalized;
            d.Notes += $"magazine line from the offline table ({turned:0.0} deg from the analysed one) ";
            Applied.Add(d);
            Log.InfoOnce("maglinetable:" + d.Key, $"{d.Key}: magazine line from the offline table, {turned:0.0} deg from the analysed one.");
        }
    }
}
