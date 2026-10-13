using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using HarmonyLib;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Analyses every weapon the game knows about (vanilla and every installed mod) once content has loaded and
    /// writes BepInEx/RavenfieldVR-weapon-report.txt: how each part was found, the sounds bound to each manual
    /// action, and anything that looks wrong. Runs when Debug.WeaponSelfTest is on or the game is started with
    /// -rfvr-selftest; works in flat mode and from the main menu.
    /// </summary>
    public static class WeaponSelfTest
    {
        public static string ReportPath => Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-weapon-report.txt");
        public static bool Running { get; private set; }

        public static string RigDumpPath => Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-rig-dump.txt");

        private static string[] _dumpFilters;

        /// <summary>
        /// -rfvr-dumprig=name1,name2 writes the full transform tree of matching weapons, with how each part moved
        /// and rotated in the fire and reload animations, to BepInEx/RavenfieldVR-rig-dump.txt.
        /// </summary>
        public static bool WantsRigDump(string key)
        {
            if (_dumpFilters == null)
            {
                string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-rfvr-dumprig=", StringComparison.OrdinalIgnoreCase));
                _dumpFilters = arg == null
                    ? new string[0]
                    : arg.Substring("-rfvr-dumprig=".Length).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).ToArray();
                if (_dumpFilters.Length > 0)
                {
                    try
                    {
                        File.Delete(RigDumpPath);
                    }
                    catch (Exception)
                    {
                        // Starts a fresh dump when possible; appending to an old one is harmless.
                    }
                }
            }
            foreach (string f in _dumpFilters)
            {
                if (f == "*" || key.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        public static void AppendRigDump(string text)
        {
            try
            {
                File.AppendAllText(RigDumpPath, text + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.WarnOnce("rigdump", "Writing the rig dump failed: " + e.Message);
            }
        }

        public static bool Requested =>
            VRConfig.WeaponSelfTest.Value || Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-rfvr-selftest", StringComparison.OrdinalIgnoreCase));

        public static IEnumerator Run()
        {
            if (Running)
            {
                yield break;
            }
            Running = true;
            // An unfocused game window would otherwise stop advancing frames and stall the run.
            bool runInBackground = Application.runInBackground;
            Application.runInBackground = true;
            Log.Info("Weapon self-test started.");
            List<WeaponManager.WeaponEntry> entries = WeaponManager.instance != null && WeaponManager.instance.allWeapons != null
                ? WeaponManager.instance.allWeapons.Where(e => e != null && e.prefab != null).ToList()
                : new List<WeaponManager.WeaponEntry>();

            var results = new List<Result>();
            var failures = new List<string>();
            float frameStart = Time.realtimeSinceStartup;
            foreach (WeaponManager.WeaponEntry entry in entries)
            {
                // If a weapon ever hangs the analysis, this file names it.
                TryWriteProgress(entry.name + " (" + (entry.sourceMod != null ? entry.sourceMod.title : "official") + ")");
                Weapon weapon = entry.prefab.GetComponent<Weapon>();
                if (weapon == null)
                {
                    failures.Add($"{entry.name}: prefab has no Weapon component");
                    continue;
                }
                try
                {
                    var main = new Result(entry, weapon, WeaponAnalyzer.Get(weapon, entry));
                    MeasureShape(main);
                    results.Add(main);
                    foreach (Weapon sub in weapon.GetComponentsInChildren<Weapon>(true))
                    {
                        if (sub != weapon)
                        {
                            results.Add(new Result(entry, sub, WeaponAnalyzer.Get(sub, entry)));
                        }
                    }
                }
                catch (Exception e)
                {
                    failures.Add($"{entry.name}: {e.GetType().Name}: {e.Message}");
                }
                if (results.Count % 50 == 0 && results.Count > 0)
                {
                    Log.Debug($"Weapon self-test: {results.Count} analysed.");
                }
                // Spread the work over frames so the game keeps responding.
                if (Time.realtimeSinceStartup - frameStart > 0.05f)
                {
                    yield return null;
                    frameStart = Time.realtimeSinceStartup;
                }
            }

            try
            {
                File.WriteAllText(ReportPath, BuildReport(results, failures), new UTF8Encoding(false));
                Log.Info($"Weapon self-test finished: {results.Count} weapons, {failures.Count} failures. Report: {ReportPath}");
            }
            catch (Exception e)
            {
                Log.Exception("Writing the weapon report failed", e);
            }
            string handShot = Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-hands.png");
            string handError = Rig.HandPreview.Capture(handShot);
            Log.Info(handError == null ? "Glove picture: " + handShot : "Glove picture failed: " + handError);
            Application.runInBackground = runInBackground;
            try
            {
                File.Delete(ProgressPath);
            }
            catch (Exception)
            {
                // Only a diagnostics file.
            }
            Running = false;
        }

        private static string ProgressPath => ReportPath + ".progress";

        private static void TryWriteProgress(string text)
        {
            try
            {
                File.WriteAllText(ProgressPath, text);
            }
            catch (Exception)
            {
                // Only a diagnostics file.
            }
        }

        private static string BuildReport(List<Result> results, List<string> failures)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Ravenfield VR {Core.PluginInfo.Version} weapon self-test, {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Unity {Application.unityVersion}. {results.Count} weapons analysed, {failures.Count} could not be analysed.");
            sb.AppendLine();

            int magWeapons = 0, slideWeapons = 0;
            var magMethods = new Dictionary<string, int>();
            var slideMethods = new Dictionary<string, int>();
            int shapes = 0, shapePieces = 0, readMeshes = 0, outlineMeshes = 0;
            int armRig = 0, sights = 0, ownSounds = 0, warned = 0, animated = 0, fingerPoses = 0, handles = 0, fromHand = 0, armChains = 0, kickable = 0, magWeapons2 = 0;
            var magOutFrom = new Dictionary<string, int>();
            var magInFrom = new Dictionary<string, int>();
            foreach (Result r in results)
            {
                WeaponRigData d = r.Data;
                animated += d.Animated ? 1 : 0;
                if (r.ShapePieces > 0)
                {
                    shapes++;
                    shapePieces += r.ShapePieces;
                    readMeshes += r.ReadableMeshes;
                    outlineMeshes += r.UnreadableMeshes;
                }
                if (d.Action == ActionType.SelfLoading || d.Action == ActionType.Manual)
                {
                    magWeapons++;
                    Count(magMethods, Bucket(d.MagMethod));
                    slideWeapons++;
                    Count(slideMethods, Bucket(d.SlideMethod));
                }
                armRig += d.HandsMethod == "arm rig" ? 1 : 0;
                fingerPoses += d.GripCurls != null ? 1 : 0;
                handles += d.HandlePath != null ? 1 : 0;
                fromHand += d.RackFromHand ? 1 : 0;
                armChains += d.ArmRight != null && d.ArmLeft != null ? 1 : 0;
                if (d.Action == ActionType.SelfLoading || d.Action == ActionType.Manual && d.MagazinePath != null)
                {
                    magWeapons2++;
                    kickable += d.MagKickable ? 1 : 0;
                    Count(magOutFrom, SoundSource(d, MechSound.MagOut));
                    Count(magInFrom, SoundSource(d, MechSound.MagIn));
                }
                sights += d.HasEyePoint ? 1 : 0;
                ownSounds += d.Cues.Count > 0 ? 1 : 0;
                warned += d.Warnings.Count > 0 ? 1 : 0;
            }
            sb.AppendLine("Summary");
            sb.AppendLine($"  animated rigs sampled ........ {animated}/{results.Count}");
            sb.AppendLine($"  hands from the arm rig ....... {armRig}/{results.Count}");
            sb.AppendLine($"  finger pose from the arm rig . {fingerPoses}/{results.Count}");
            sb.AppendLine($"  separate charging handles .... {handles}; handle from the mod's racking hand {fromHand}");
            sb.AppendLine($"  both arm chains for IK ....... {armChains}/{results.Count}");
            sb.AppendLine($"  magazine can be kicked out ... {kickable}/{magWeapons2} (the rest sit too deep in their well)");
            sb.AppendLine($"  sights lined up for aiming ... {sights}/{results.Count}");
            sb.AppendLine($"  own sounds bound to actions .. {ownSounds}/{results.Count}");
            sb.AppendLine($"  magazine out sound ({magWeapons2}) ... {Format(magOutFrom)}");
            sb.AppendLine($"  magazine in sound ({magWeapons2}) .... {Format(magInFrom)}");
            sb.AppendLine($"  weapons with warnings ........ {warned}/{results.Count}");
            sb.AppendLine($"  physics shape from the model . {shapes} weapons, {(shapes > 0 ? shapePieces / (float)shapes : 0f):0.0} pieces each; meshes read {readMeshes}, outline only {outlineMeshes}");
            sb.AppendLine($"  magazine found by ({magWeapons} magazine/bolt weapons): {Format(magMethods)}");
            sb.AppendLine($"  slide/bolt found by: {Format(slideMethods)}");
            sb.AppendLine();

            if (failures.Count > 0)
            {
                sb.AppendLine("Could not analyse");
                foreach (string f in failures)
                {
                    sb.AppendLine("  " + f);
                }
                sb.AppendLine();
            }

            foreach (IGrouping<string, Result> group in results
                         .GroupBy(r => r.Entry.sourceMod != null && !r.Entry.sourceMod.isOfficialContent ? r.Entry.sourceMod.title : "Ravenfield (official)")
                         .OrderBy(g => g.Key))
            {
                sb.AppendLine("== " + group.Key);
                foreach (Result r in group)
                {
                    WeaponRigData d = r.Data;
                    sb.AppendLine($"  {d.Key}");
                    sb.AppendLine($"    action {d.Action}{(d.IsPistol ? ", pistol grip" : "")}{(d.IsMachineGun ? ", machine gun" : "")}{(d.CyclesByHand ? ", cycled by hand" : "")}{(d.LockOnEmpty ? ", locks open on empty" : "")}, rig {(d.Animated ? "animated" : "static")}" +
                                  $"{(d.Hinge != HingeKind.None ? $", hinge {d.Hinge}{PartName(r.Weapon, d.HingePath)} {d.HingeOpenAngle:0}°" : "")}");
                    sb.AppendLine($"    magazine: {d.MagMethod}{PartName(r.Weapon, d.MagazinePath)}{(d.MagazinePath != null ? $", {d.MagProtrusion * 100f:0}% outside the gun, {(d.MagKickable ? "kickable" : "not kickable")}, {d.MagBounds.size.magnitude * 100f:0} cm" : "")}{(d.MagazinePartPaths.Count > 0 ? $" + {d.MagazinePartPaths.Count} piece(s)" : "")}{(d.MagazineToggles ? " (swapped by visibility)" : "")}");
                    sb.AppendLine($"    slide/bolt: {d.SlideMethod}{PartName(r.Weapon, d.SlidePath)}{(d.HandlePath != null ? ", handle" + PartName(r.Weapon, d.HandlePath) : "")}, travel {d.SlideTravel.magnitude * 100f:0.0} cm");
                    sb.AppendLine($"    hands: {d.HandsMethod}, finger pose {(d.GripCurls != null ? "from the rig " + Curls(d.GripCurls) : "generic")}" +
                                  $"{(d.SupportCurls != null ? " / support " + Curls(d.SupportCurls) : "")}; sights: {(d.HasScopeModel ? $"scope model (ocular measured, lens {d.ScopeRadius * 200f:0.0} cm)" : d.HasEyePoint ? d.EyeFromLine ? "sight line (eye distance estimated)" : "lined up" : "barrel line")}{OverBore(d)}");
                    if (r.ShapePieces > 0 || r.ShapeError != null)
                    {
                        sb.AppendLine("    " + Shape(r));
                    }
                    sb.AppendLine($"    reload timeline: mag out {T(d.TMagOut)}, mag in {T(d.TMagIn)}, slide back {T(d.TSlideBack)}, slide home {T(d.TSlideForward)}");
                    sb.AppendLine($"    sound sources: mag out {SoundSource(d, MechSound.MagOut)}, mag in {SoundSource(d, MechSound.MagIn)}, slide back {SoundSource(d, MechSound.SlideBack)}, slide home {SoundSource(d, MechSound.SlideForward)}");
                    if (d.Cues.Count > 0 || d.ShotCues.Count > 0)
                    {
                        var parts = d.Cues.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {string.Join(", ", kv.Value.Select(c => c.ToString()).ToArray())}").ToList();
                        if (d.ShotCues.Count > 0)
                        {
                            parts.Add("Shot: " + string.Join(", ", d.ShotCues.Select(c => c.ToString()).ToArray()));
                        }
                        sb.AppendLine("    sounds: " + string.Join(" | ", parts.ToArray()));
                    }
                    else
                    {
                        sb.AppendLine("    sounds: built-in mechanical sounds (the mod has no separable reload sounds)");
                    }
                    if (d.Notes.Trim().Length > 0)
                    {
                        sb.AppendLine("    notes: " + d.Notes.Trim());
                    }
                    foreach (string w in d.Warnings)
                    {
                        sb.AppendLine("    ! " + w);
                    }
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>The physics shape the held weapon will get (convex pieces of its own meshes), measured on the prefab.</summary>
        private static void MeasureShape(Result r)
        {
            try
            {
                var rig = new WeaponRig(r.Weapon, r.Data);
                WeaponRigData d = r.Data;
                foreach (Renderer rend in GunCollider.VisibleRenderers(rig))
                {
                    if (ShapeFit.Readable(rend))
                    {
                        r.ReadableMeshes++;
                    }
                    else
                    {
                        r.UnreadableMeshes++;
                    }
                }
                r.ShapePieces = GunCollider.Fit(rig, Runtime.OpenVRMath.SafeLookRotation(d.AimAxis, d.Up), d.GripPoint).Length;
            }
            catch (Exception e)
            {
                r.ShapeError = e.GetType().Name + ": " + e.Message;
            }
        }

        private static string Shape(Result r)
        {
            if (r.ShapeError != null)
            {
                return "physics shape: FAILED " + r.ShapeError;
            }
            if (r.ShapePieces == 0)
            {
                return "physics shape: -";
            }
            return $"physics shape: {r.ShapePieces} convex piece(s) of the model; meshes read {r.ReadableMeshes}, outline only {r.UnreadableMeshes}";
        }

        private static string T(float t) => t >= 0f ? t.ToString("0.00") + "s" : "-";

        /// <summary>Where the sight line runs relative to the bore (cm up, cm to the side), for guns with a known sight line.</summary>
        private static string OverBore(WeaponRigData d)
        {
            if (!d.HasEyePoint)
            {
                return "";
            }
            Vector3 rel = d.EyePoint - d.MuzzlePoint;
            Vector3 right = Vector3.Cross(d.Up, d.AimAxis).normalized;
            return $" ({Vector3.Dot(rel, d.Up) * 100f:0.0} cm over the bore, {Vector3.Dot(rel, right) * 100f:0.0} cm aside)";
        }

        private static string Curls(float[] c) => "[" + string.Join(" ", c.Select(v => v < 0f ? "-" : v.ToString("0.00")).ToArray()) + "]";

        private static string PartName(Weapon w, int[] path)
        {
            Transform t = path != null && w != null ? WeaponRig.Resolve(w.transform, path) : null;
            return t != null ? $" [{t.name}]" : "";
        }

        private sealed class Result
        {
            public readonly WeaponManager.WeaponEntry Entry;
            public readonly Weapon Weapon;
            public readonly WeaponRigData Data;
            public int ShapePieces, ReadableMeshes, UnreadableMeshes;
            public string ShapeError;

            public Result(WeaponManager.WeaponEntry entry, Weapon weapon, WeaponRigData data)
            {
                Entry = entry;
                Weapon = weapon;
                Data = data;
            }
        }

        private static string Bucket(string method)
        {
            int quote = method.IndexOf(" '", StringComparison.Ordinal);
            return quote > 0 ? method.Substring(0, quote) : method;
        }

        /// <summary>Where the sound for <paramref name="kind"/> comes from at runtime: the mod, a vanilla weapon, or synthesis.</summary>
        private static string SoundSource(WeaponRigData d, MechSound kind)
        {
            if (d.Cues.TryGetValue(kind, out List<SoundCue> cues) && cues.Count > 0)
            {
                return "own";
            }
            return SoundDonors.Find(d, kind) != null ? "vanilla donor" : "synthetic";
        }

        private static void Count(Dictionary<string, int> counts, string key)
        {
            counts.TryGetValue(key, out int n);
            counts[key] = n + 1;
        }

        private static string Format(Dictionary<string, int> counts) =>
            string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}").ToArray());
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnAllContentLoaded))]
    internal static class ContentLoadedPatch
    {
        private static void Postfix()
        {
            if (WeaponSelfTest.Requested && VRManager.Instance != null)
            {
                VRManager.Instance.StartCoroutine(WeaponSelfTest.Run());
            }
        }
    }
}
