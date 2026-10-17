using System;
using System.IO;
using BepInEx;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Hand poses saved in the sandbox (tools/sandbox, "Save pose"): BepInEx/config/RavenfieldVR/poses/&lt;weapon&gt;.json.
    /// The gun hand's place and turn on the gun become the weapon's grip point and grip frame (the gun is held so that the
    /// glove's palm frame sits where the pose put it), its finger curls the weapon's own grip curls; the support hand's
    /// palm becomes the support point; a thumb posed there, the gun hand's thumb. A pose saved without changes is the
    /// analysis' own (with the weapon's overrides): nothing changes.
    /// </summary>
    internal static class SandboxPoses
    {
        [Serializable]
        private class Frame
        {
            public float[] position;
            public float[] rotation;
        }

        [Serializable]
        private class HandDoc
        {
            public Frame mod;
            public float thumb;
            public float[] curls;
            public bool thumbSet;
            public string on;
            public string role;
        }

        [Serializable]
        private class Hands
        {
            public HandDoc right;
            public HandDoc left;
        }

        [Serializable]
        private class Doc
        {
            public string key;
            public Hands hands;
        }

        public static string Dir => Path.Combine(Path.Combine(Paths.ConfigPath, "RavenfieldVR"), "poses");

        private static string Fmt(Vector3 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.#####}, {1:0.#####}, {2:0.#####})", v.x, v.y, v.z);

        private static string Fmt(Quaternion q) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.#####}, {1:0.#####}, {2:0.#####}, {3:0.#####})", q.x, q.y, q.z, q.w);

        public static void Apply(WeaponRigData d)
        {
            if (VRConfig.UseSandboxPoses == null || !VRConfig.UseSandboxPoses.Value || d == null || string.IsNullOrEmpty(d.Key))
            {
                return;
            }
            string path = Path.Combine(Dir, SandboxExport.SafeName(d.Key) + ".json");
            if (!File.Exists(path))
            {
                return;
            }
            try
            {
                Doc doc = JsonUtility.FromJson<Doc>(File.ReadAllText(path));
                HandDoc right = doc?.hands?.right;
                if (right?.mod?.position != null && right.mod.position.Length == 3 && right.mod.rotation != null && right.mod.rotation.Length == 4)
                {
                    d.GripPoint = new Vector3(right.mod.position[0], right.mod.position[1], right.mod.position[2]);
                    var q = new Quaternion(right.mod.rotation[0], right.mod.rotation[1], right.mod.rotation[2], right.mod.rotation[3]);
                    float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                    if (len > 1e-4f)
                    {
                        d.GripRotation = new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len);
                        d.HasGripRotation = true;
                    }
                    if (right.curls != null && right.curls.Length == 5)
                    {
                        d.GripCurls = (float[])right.curls.Clone();
                        if (right.thumbSet)
                        {
                            // Only a thumb posed in the sandbox: else the mod's own thumb rule (along a pistol's frame...).
                            d.GripThumbBase = Mathf.Clamp01(right.thumb);
                            d.GripThumbCurl = Mathf.Clamp01(right.curls[0]);
                        }
                    }
                }
                HandDoc left = doc?.hands?.left;
                if (left?.mod?.position != null && left.mod.position.Length == 3 && left.role == "support")
                {
                    d.SupportPoint = new Vector3(left.mod.position[0], left.mod.position[1], left.mod.position[2]);
                    d.HasSupportPoint = true;
                    if (left.curls != null && left.curls.Length == 5)
                    {
                        d.SupportCurls = (float[])left.curls.Clone();
                    }
                }
                d.Notes += "hands from the sandbox's saved pose ";
                // What it set, in the file's numbers (the autotest's check reads it back against the file).
                Log.Info($"{d.Key}: hand pose from the sandbox ({path}): grip {Fmt(d.GripPoint)}, palm {Fmt(d.GripRotation)}"
                         + $", curls {(d.GripCurls != null ? string.Join(" ", Array.ConvertAll(d.GripCurls, c => c.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) : "-")}"
                         + $", thumb {(d.GripThumbCurl >= 0f ? $"base {d.GripThumbBase:0.###} curl {d.GripThumbCurl:0.###}" : "the mod's")}"
                         + $", support {(left?.role == "support" && d.HasSupportPoint ? Fmt(d.SupportPoint) : "the analysis'")}.");
            }
            catch (Exception e)
            {
                Log.Warn($"The sandbox pose for {d.Key} could not be read ({path}): {e.Message}");
            }
        }
    }
}
