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
    /// palm becomes the support point; a thumb posed there, the gun hand's thumb. A hand is taken only as the job it was
    /// saved as: the gun hand as the grip (or no job, in an old file), the support hand as the support and only where a
    /// support hand can be (SupportAllowed). A pose saved without changes is the analysis' own (with the weapon's overrides):
    /// nothing changes.
    /// </summary>
    internal static class SandboxPoses
    {
        public static string Dir => Path.Combine(Path.Combine(Paths.ConfigPath, "RavenfieldVR"), "poses");

        private static string Fmt(Vector3 v) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.#####}, {1:0.#####}, {2:0.#####})", v.x, v.y, v.z);

        private static string Fmt(Quaternion q) => string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:0.#####}, {1:0.#####}, {2:0.#####}, {3:0.#####})", q.x, q.y, q.z, q.w);

        /// <summary>
        /// How far a long gun's support point lies ahead of its grip along the aim (metres). The analysis keeps the support
        /// that far forward on the forend, and the sandbox's own check (tools/sandbox/app/scene.py Scene.support_ok) uses the same.
        /// </summary>
        private const float MinSupportAhead = 0.07f;

        /// <summary>
        /// Whether a saved support hand may be the support point (review B10#1), the sandbox's own rule (tools/sandbox/app/scene.py
        /// Scene.support_ok): never on a gun with no support point; anywhere within 5 cm of the analysed support point; else a
        /// pistol's support hand cups the grip (within 12 cm of it), and a long gun's must be MinSupportAhead ahead of the grip
        /// along the aim (a support behind the grip turns the barrel away from the off hand). The grip is the one the game holds:
        /// the saved gun hand's when it was taken. Called before the analysed support point is overwritten. (The two sides took
        /// different rules: a pistol's support anywhere was kept here and warned about in the sandbox.)
        /// </summary>
        private static bool SupportAllowed(WeaponRigData d, Vector3 support)
        {
            if (!d.HasSupportPoint)
            {
                return false;
            }
            if (Vector3.Distance(support, d.SupportPoint) < 0.05f)
            {
                return true;
            }
            if (d.IsPistol)
            {
                return Vector3.Distance(support, d.GripPoint) < 0.12f;
            }
            return Vector3.Dot(support - d.GripPoint, d.AimAxis) >= MinSupportAhead;
        }

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
                // The file is found by a file-safe name, which two weapon kinds can share: the pose saved for another one is not
                // taken (bridge scout, 2026-10-24). A file from before the key was saved is taken as before.
                string savedFor = MiniJson.Str(doc, "key");
                if (!string.IsNullOrEmpty(savedFor) && savedFor != d.Key)
                {
                    Log.WarnOnce("sandbox-pose-key:" + d.Key, $"The sandbox pose file {path} was saved for {savedFor}, not {d.Key}: not used.");
                    return;
                }
                var hands = MiniJson.Obj(doc, "hands");
                var right = MiniJson.Obj(hands, "right");
                var left = MiniJson.Obj(hands, "left");
                var rightMod = MiniJson.Obj(right, "mod");
                float[] rp = MiniJson.Floats(rightMod, "position", 3), rq = MiniJson.Floats(rightMod, "rotation", 4);
                bool usedRight = false, usedLeft = false;
                // Only a gun hand saved as the grip is the game's grip point: one saved on a magazine, a slide or in the air is not
                // (review B10#0). A file from before the role was saved has none, and its hand is the grip as it always was.
                string rightRole = MiniJson.Str(right, "role");
                bool rightIsGrip = rightRole == null || rightRole == "grip";
                if (rp != null && rq != null && rightIsGrip)
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
                else if (rp != null && rq != null)
                {
                    Log.WarnOnce("sandbox-pose-grip:" + d.Key, $"The sandbox pose for {d.Key} saves the gun hand as '{rightRole}', not as the grip: the mod keeps its analysed grip ({path}).");
                }
                float[] lp = MiniJson.Floats(MiniJson.Obj(left, "mod"), "position", 3);
                if (lp != null && MiniJson.Str(left, "role") == "support")
                {
                    var support = new Vector3(lp[0], lp[1], lp[2]);
                    if (SupportAllowed(d, support))
                    {
                        d.SupportPoint = support;
                        d.HasSupportPoint = true;
                        float[] curls = MiniJson.Floats(left, "curls", 5);
                        if (curls != null)
                        {
                            d.SupportCurls = curls;
                        }
                        usedLeft = true;
                    }
                    else
                    {
                        string why = !d.HasSupportPoint ? "on a gun with no support point: the mod keeps none"
                            : d.IsPistol ? "more than 12 cm from the pistol grip: the mod keeps the analysed support point"
                            : "not ahead of the grip on this long gun: the mod keeps the analysed support point";
                        Log.WarnOnce("sandbox-pose-support:" + d.Key, $"The sandbox pose for {d.Key} saves the support hand at {Fmt(support)}, {why} ({path}).");
                    }
                }
                if (!usedRight && (rp == null || rq == null))
                {
                    Log.Warn($"The sandbox pose for {d.Key} has no usable right hand (hands.right.mod.position / rotation): the mod's own grip is kept ({path}).");
                }
                if (!usedRight && !usedLeft)
                {
                    return; // (nothing of it was used: the notes and the log said the sandbox's pose was)
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
