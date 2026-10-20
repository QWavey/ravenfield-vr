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
    /// lean sideways too. Applied after the analysis, the user's overrides and the export: the scans read the analysed line,
    /// and the table's tilts are relative to it.
    /// </summary>
    internal static partial class MagLineTable
    {
        private static readonly HashSet<WeaponRigData> Applied = new HashSet<WeaponRigData>();

        /// <summary>Whether this rig's magazine line came from the table (the in-game fit leaves it alone).</summary>
        public static bool From(WeaponRigData d) => d != null && Applied.Contains(d);

        public static void Apply(WeaponRigData d)
        {
            if (d == null || d.Key == null || !Tilts.TryGetValue(d.Key, out Vector2 tilt) || d.MagEjectAxis.sqrMagnitude < 0.5f)
            {
                return;
            }
            // As the scan turns it: about right = aim x up (the up made square to the aim), then about the aim.
            Vector3 aim = d.AimAxis.normalized;
            Vector3 up = (d.Up - aim * Vector3.Dot(d.Up, aim)).normalized;
            Vector3 right = Vector3.Cross(aim, up).normalized;
            Vector3 eject = d.MagEjectAxis.normalized;
            eject = Quaternion.AngleAxis(tilt.x, right) * eject;
            eject = Quaternion.AngleAxis(tilt.y, aim) * eject;
            d.MagEjectAxis = eject.normalized;
            d.Notes += $"magazine line from the offline table ({tilt.x:+0.0;-0.0} fore/aft, {tilt.y:+0.0;-0.0} sideways) ";
            Applied.Add(d);
            Log.InfoOnce("maglinetable:" + d.Key, $"{d.Key}: magazine line from the offline table, turned {tilt.x:+0.0;-0.0} deg fore/aft and {tilt.y:+0.0;-0.0} deg sideways.");
        }
    }
}
