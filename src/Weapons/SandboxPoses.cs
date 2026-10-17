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
                // Read with the mod's own JSON reader: JsonUtility left the nested hands empty without a word, and the game
                // kept the analysed grip (check-1017i, the M4A1 CCO's pose).
                object doc = MiniJson.Parse(File.ReadAllText(path));
                var hands = MiniJson.Obj(doc, "hands");
                var right = MiniJson.Obj(hands, "right");
                var left = MiniJson.Obj(hands, "left");
                var rightMod = MiniJson.Obj(right, "mod");
                float[] rp = MiniJson.Floats(rightMod, "position", 3), rq = MiniJson.Floats(rightMod, "rotation", 4);
                bool usedRight = false, usedLeft = false;
                if (rp != null && rq != null)
                {
                    d.GripPoint = new Vector3(rp[0], rp[1], rp[2]);
                    var q = new Quaternion(rq[0], rq[1], rq[2], rq[3]);
                    float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                    if (len > 1e-4f)
                    {
                        d.GripRotation = new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len);
                        d.HasGripRotation = true;
                    }
                    float[] curls = MiniJson.Floats(right, "curls", 5);
                    if (curls != null)
                    {
                        d.GripCurls = curls;
                        if (MiniJson.Bool(right, "thumbSet"))
                        {
                            // Only a thumb posed in the sandbox: else the mod's own thumb rule (along a pistol's frame...).
                            d.GripThumbBase = Mathf.Clamp01(MiniJson.Num(right, "thumb", 0.35f));
                            d.GripThumbCurl = Mathf.Clamp01(curls[0]);
                        }
                    }
                    usedRight = true;
                }
                float[] lp = MiniJson.Floats(MiniJson.Obj(left, "mod"), "position", 3);
                if (lp != null && MiniJson.Str(left, "role") == "support")
                {
                    d.SupportPoint = new Vector3(lp[0], lp[1], lp[2]);
                    d.HasSupportPoint = true;
                    float[] curls = MiniJson.Floats(left, "curls", 5);
                    if (curls != null)
                    {
                        d.SupportCurls = curls;
                    }
                    usedLeft = true;
                }
                if (!usedRight)
                {
                    Log.Warn($"The sandbox pose for {d.Key} has no usable right hand (hands.right.mod.position / rotation): the mod's own grip is kept ({path}).");
                }
                d.Notes += "hands from the sandbox's saved pose ";
                // What it set, in the file's numbers (the autotest's check reads it back against the file).
                Log.Info($"{d.Key}: hand pose from the sandbox ({path}): grip {Fmt(d.GripPoint)}, palm {Fmt(d.GripRotation)}"
                         + $", curls {(d.GripCurls != null ? string.Join(" ", Array.ConvertAll(d.GripCurls, c => c.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture))) : "-")}"
                         + $", thumb {(d.GripThumbCurl >= 0f ? $"base {d.GripThumbBase:0.###} curl {d.GripThumbCurl:0.###}" : "the mod's")}"
                         + $", support {(usedLeft ? Fmt(d.SupportPoint) : "the analysis'")}.");
            }
            catch (Exception e)
            {
                Log.Warn($"The sandbox pose for {d.Key} could not be read ({path}): {e.Message}");
            }
        }
    }
}
