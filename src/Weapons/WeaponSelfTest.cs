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

        private static string[] _only;

        /// <summary>
        /// -rfvr-only=name1,name2: the self-test and the autotest take only the weapons whose name (or prefab name) contains
        /// one of these, and the self-test quits when done: checking a few weapons takes about a minute instead of the
        /// whole collection's three or four.
        /// </summary>
        internal static string[] Only
        {
            get
            {
                if (_only == null)
                {
                    string arg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("-rfvr-only=", StringComparison.OrdinalIgnoreCase));
                    _only = arg == null
                        ? new string[0]
                        : arg.Substring("-rfvr-only=".Length).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(f => f.Trim()).Where(f => f.Length > 0).ToArray();
                }
                return _only;
            }
        }

        /// <summary>
        /// A weapon's id: its place in the game's weapon list, from 1 (the number on its pictures and sheets; the same in
        /// every run with the same mods). 0 when it is not in the list.
        /// </summary>
        internal static int IdOf(WeaponManager.WeaponEntry entry)
        {
            if (WeaponManager.instance == null || entry == null)
            {
                return 0;
            }
            int id = 0;
            foreach (WeaponManager.WeaponEntry e in WeaponManager.instance.allWeapons)
            {
                if (e == null || e.prefab == null)
                {
                    continue;
                }
                id++;
                if (ReferenceEquals(e, entry))
                {
                    return id;
                }
            }
            return 0;
        }

        /// <summary>
        /// Whether -rfvr-only lets this weapon through (always, without the option). Each comma-separated filter is one of:
        /// "#60" or "60" (the weapon with that id), "=name" (that exact name), "mod:text" (every weapon of a mod whose title
        /// or folder contains the text), or text found in the weapon's name, its prefab's name, its mod's title or folder.
        /// </summary>
        internal static bool Allows(WeaponManager.WeaponEntry entry)
        {
            if (Only.Length == 0)
            {
                return true;
            }
            int id = -1;
            foreach (string f in Only)
            {
                string id0 = f.StartsWith("#", StringComparison.Ordinal) ? f.Substring(1) : f;
                if (int.TryParse(id0, out int wanted))
                {
                    if (id < 0)
                    {
                        id = IdOf(entry);
                    }
                    if (id == wanted)
                    {
                        return true;
                    }
                    continue;
                }
                ModInformation mod = entry.sourceMod;
                bool InMod(string text) => mod != null && (mod.title != null && mod.title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
                                                           || mod.path != null && mod.path.Replace('\\', '/').IndexOf(text.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) >= 0
                                                           || mod.directoryName != null && mod.directoryName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
                if (f.StartsWith("=", StringComparison.Ordinal)
                        ? string.Equals(entry.name, f.Substring(1), StringComparison.OrdinalIgnoreCase)
                        : f.StartsWith("mod:", StringComparison.OrdinalIgnoreCase)
                            ? InMod(f.Substring(4))
                            : entry.name != null && entry.name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                              || entry.prefab != null && entry.prefab.name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
                              || InMod(f))
                {
                    return true;
                }
            }
            return false;
        }

        public static bool Requested =>
            VRConfig.WeaponSelfTest.Value || Environment.GetCommandLineArgs().Any(a => string.Equals(a, "-rfvr-selftest", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The steps run with a way out: one that threw past the per-weapon guard (a drop's preparing, the hand preview, the pose
        /// sheet) left Running set, so every later run returned at once, and the window kept running in the background; now logged
        /// and let go (self-test scout, 2026-11-26).
        /// </summary>
        public static IEnumerator Run()
        {
            if (Running)
            {
                yield break;
            }
            bool background = Application.runInBackground;
            IEnumerator steps = RunSteps();
            bool finished = false;
            try
            {
                while (true)
                {
                    object current;
                    try
                    {
                        if (!steps.MoveNext())
                        {
                            finished = true;
                            break;
                        }
                        current = steps.Current;
                    }
                    catch (Exception e)
                    {
                        Log.Exception("Weapon self-test stopped", e);
                        break;
                    }
                    yield return current;
                }
            }
            finally
            {
                if (!finished)
                {
                    Running = false;
                    Application.runInBackground = background;
                }
            }
        }

        private static IEnumerator RunSteps()
        {
            Running = true;
            // An unfocused game window would otherwise stop advancing frames and stall the run.
            bool runInBackground = Application.runInBackground;
            Application.runInBackground = true;
            Log.Info("Weapon self-test started.");
            List<WeaponManager.WeaponEntry> entries = WeaponManager.instance != null && WeaponManager.instance.allWeapons != null
                ? WeaponManager.instance.allWeapons.Where(e => e != null && e.prefab != null && Allows(e)).ToList()
                : new List<WeaponManager.WeaponEntry>();
            if (Only.Length > 0)
            {
                Log.Info($"Weapon self-test: only {entries.Count} weapon(s) matching {string.Join(", ", Only)}.");
            }

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
            if (SandboxExport.Requested)
            {
                // The sandbox's export run (tools/sandbox/export.py): written, then the game closes.
                SandboxExport.WriteIndex();
                Running = false;
                Application.runInBackground = runInBackground;
                Log.Info("Sandbox export finished: quitting.");
                Application.Quit();
                yield break;
            }
            // Dropped weapons: cost of the first drop of a weapon type (model, shape, cooking) against a drop made from
            // what the menu warm-up prepares.
            float coldSum = 0f, coldMax = 0f, warmSum = 0f, warmMax = 0f;
            int timed = 0;
            foreach (WeaponManager.WeaponEntry entry in entries.Take(40))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                WeaponDrops.Prepare(entry);
                float cold = (float)watch.Elapsed.TotalMilliseconds;
                float warm = WeaponDrops.TimePreparedDrop(entry);
                if (warm < 0f)
                {
                    continue;
                }
                timed++;
                coldSum += cold;
                coldMax = Mathf.Max(coldMax, cold);
                warmSum += warm;
                warmMax = Mathf.Max(warmMax, warm);
                yield return null;
            }
            if (timed > 0)
            {
                _dropTiming = $"  weapon drop cost ............. first drop {coldSum / timed:0.0} ms avg ({coldMax:0.0} max, warm-up measuring included), prepared {warmSum / timed:0.0} ms avg ({warmMax:0.0} max), {timed} weapons";
                AppendToReport(_dropTiming);
            }
            // The third-person model copied from its own part of the prefab against the whole prefab, same weapons.
            float partSum = 0f, partMax = 0f, wholeSum = 0f, wholeMax = 0f;
            int copies = 0, partFallback = 0;
            foreach (WeaponManager.WeaponEntry entry in entries.Take(40))
            {
                float whole = WeaponDrops.TimeModelCopy(entry, false);
                float part = WeaponDrops.TimeModelCopy(entry, true);
                if (whole < 0f)
                {
                    continue;
                }
                if (part < 0f)
                {
                    partFallback++;
                    part = whole; // that weapon still copies the whole prefab
                }
                copies++;
                partSum += part;
                partMax = Mathf.Max(partMax, part);
                wholeSum += whole;
                wholeMax = Mathf.Max(wholeMax, whole);
                yield return null;
            }
            if (copies > 0)
            {
                AppendToReport($"  dropped model copy ........... third-person part {partSum / copies:0.00} ms avg ({partMax:0.0} max), whole prefab {wholeSum / copies:0.00} ms avg ({wholeMax:0.0} max), {copies} weapons, {partFallback} needed the whole prefab");
            }
            string handShot = Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-hands.png");
            string handError = Rig.HandPreview.Capture(handShot);
            Log.Info(handError == null ? "Glove picture: " + handShot : "Glove picture failed: " + handError);
            string poseShot = Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-hand-poses.png");
            string poseError = Rig.HandPreview.CapturePoseSheet(poseShot);
            AppendToReport($"  hand poses ................... {Rig.HandPoses.Count} ({(poseError == null ? "RavenfieldVR-hand-poses.png" : "picture failed: " + poseError)})");
            AppendToReport($"  case and belt round meshes ... {Casings.MeshCheck()}");
            AppendToReport($"  cockpit controls ............. {Rig.CockpitControls.SelfCheck()}");
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
            if (Only.Length > 0 && !AutoTest.Requested)
            {
                Log.Info("Weapon self-test (filtered) done; quitting.");
                Application.Quit();
            }
        }

        private static string ProgressPath => ReportPath + ".progress";

        private static string _dropTiming;

        private static void AppendToReport(string line)
        {
            try
            {
                File.AppendAllText(ReportPath, Environment.NewLine + line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Log.Warn("Appending to the weapon report failed: " + e.Message);
            }
        }

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
            float shapeMs = 0f, shapeMaxMs = 0f;
            string shapeMaxName = "";
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
                    shapeMs += r.ShapeMs;
                    if (r.ShapeMs > shapeMaxMs)
                    {
                        shapeMaxMs = r.ShapeMs;
                        shapeMaxName = d.Key;
                    }
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
            sb.AppendLine($"  shape fitting time ........... {(shapes > 0 ? shapeMs / shapes : 0f):0.0} ms avg, {shapeMaxMs:0.0} ms max ({shapeMaxName})");
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
                    sb.AppendLine($"    muzzle flash: {MuzzleFlashInfo.Of(r.Weapon).Describe()}");
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
                var watch = System.Diagnostics.Stopwatch.StartNew();
                r.ShapePieces = GunCollider.Fit(rig, Runtime.OpenVRMath.SafeLookRotation(d.AimAxis, d.Up), d.GripPoint).Length;
                r.ShapeMs = (float)watch.Elapsed.TotalMilliseconds;
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
            public float ShapeMs;
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

        /// <summary>Where the sound for <paramref name="kind"/> comes from at runtime: the mod, a vanilla weapon, or a game clip.</summary>
        private static string SoundSource(WeaponRigData d, MechSound kind)
        {
            if (d.Cues.TryGetValue(kind, out List<SoundCue> cues) && cues.Count > 0)
            {
                return "own";
            }
            return SoundDonors.Find(d, kind) != null ? "vanilla donor" : "game clip";
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
            if (Rig.HandPreview.CloseupsRequested && VRManager.Instance != null)
            {
                VRManager.Instance.StartCoroutine(HandShots());
            }
        }

        /// <summary>-rfvr-handshots: the glove pictures, then quit (a quick look at the hand model without a test run).</summary>
        private static System.Collections.IEnumerator HandShots()
        {
            yield return null;
            string dir = BepInEx.Paths.BepInExRootPath;
            string error = Rig.HandPreview.Capture(System.IO.Path.Combine(dir, "RavenfieldVR-hands.png"))
                           ?? Rig.HandPreview.CaptureCloseups(System.IO.Path.Combine(dir, "RavenfieldVR-hand-closeups.png"))
                           ?? Rig.HandPreview.CapturePoseSheet(System.IO.Path.Combine(dir, "RavenfieldVR-hand-poses.png"));
            Log.Info(error == null ? "Hand pictures written." : "Hand pictures failed: " + error);
            yield return null;
            Application.Quit();
        }
    }
}
