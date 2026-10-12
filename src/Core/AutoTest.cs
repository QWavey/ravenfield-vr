using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using HarmonyLib;
using RavenfieldVR.Util;
using RavenfieldVR.Weapons;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// -rfvr-autotest: plays a match by itself to exercise the mod. Start with
    /// <c>ravenfield.exe -map island2023 -nointro -rfvr-autotest [-vr]</c>. Every installed mutator is switched on,
    /// the player deploys, every weapon is equipped in turn, its manual reload runs through the real code
    /// (VR only) and it fires a few shots; then the game quits. BepInEx/RavenfieldVR-autotest.txt lists every
    /// exception (with the weapon being tested), what each reload did, and the mod's per-frame timings.
    /// </summary>
    public static class AutoTest
    {
        public static string ReportPath => Path.Combine(Paths.BepInExRootPath, "RavenfieldVR-autotest.txt");

        private static readonly bool RequestedOnCommandLine = Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-autotest", StringComparison.OrdinalIgnoreCase));

        /// <summary>Started with -rfvr-autotest (read once: it is checked every frame).</summary>
        public static bool Requested => RequestedOnCommandLine;

        private static bool _started;
        private static bool _written;
        private static int _tested;
        private static string _context = "startup";
        private static readonly List<string> Problems = new List<string>();
        private static readonly List<string> Results = new List<string>();
        private static readonly HashSet<string> Seen = new HashSet<string>();

        public static void Install()
        {
            if (!Requested)
            {
                return;
            }
            PerfProbe.Enabled = true;
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) => VRManager.Instance?.StartCoroutine(WaitForMatch());
            // A run cut short (window closed, game quit) still leaves a report.
            Application.quitting += () =>
            {
                if (_started && !_written)
                {
                    Results.Add("Run ended early (the game quit before the test finished).");
                    Write();
                }
            };
            Log.ExceptionLogged += (context, e) => Problem($"[{_context}] {context}: {e.GetType().Name}: {e.Message}\n      {FirstFrames(e.StackTrace)}");
            Application.logMessageReceived += (condition, stack, type) =>
            {
                if (type == LogType.Exception || type == LogType.Error && condition.IndexOf("RavenfieldVR", StringComparison.Ordinal) >= 0)
                {
                    Problem($"[{_context}] {type}: {condition}\n      {FirstFrames(stack)}");
                }
            };
        }

        private static string FirstFrames(string stack) =>
            string.Join(" | ", (stack ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Take(4).ToArray());

        private static void Problem(string text)
        {
            if (Seen.Add(text))
            {
                Problems.Add(text);
            }
        }

        /// <summary>Content is loaded and the match is about to start: switch on every installed mutator.</summary>
        public static void EnableAllMutators()
        {
            if (!Requested || ModManager.instance == null || GameManager.instance == null)
            {
                return;
            }
            var active = GameManager.instance.gameInfo.activeMutators;
            int added = 0;
            foreach (MutatorEntryData m in ModManager.instance.loadedMutators)
            {
                if (m == null || active.Contains(m) || ModManager.instance.builtInMutators.Contains(m))
                {
                    continue;
                }
                active.Add(m);
                added++;
            }
            Results.Add($"Mutators switched on: {added} ({string.Join(", ", active.Select(m => m.name).ToArray())})");
        }

        private static IEnumerator WaitForMatch()
        {
            // The scene finishes setting up over a few frames.
            for (int i = 0; i < 300 && !_started; i++)
            {
                if (GameManager.IsIngame(out bool noGameplay) && !noGameplay && ActorManager.instance != null)
                {
                    _started = true;
                    yield return Run();
                    yield break;
                }
                yield return null;
            }
        }

        private static IEnumerator Run()
        {
            Application.runInBackground = true;
            float deadline = Time.realtimeSinceStartup + 120f;
            while (LocalPlayer.actor == null && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }
            yield return new WaitForSecondsRealtime(4f);
            _context = "deploy";
            yield return Deploy();
            if (LocalPlayer.actor == null || LocalPlayer.actor.dead)
            {
                Results.Add("Could not deploy the player; weapon tests skipped.");
            }
            else
            {
                PerfProbe.Reset();
                yield return TestWeapons();
                _context = "idle";
                yield return new WaitForSecondsRealtime(5f);
            }
            Write();
            Application.Quit();
        }

        private static IEnumerator Deploy()
        {
            for (int attempt = 0; attempt < 6 && (LocalPlayer.actor == null || LocalPlayer.actor.dead); attempt++)
            {
                if (LoadoutUi.instance != null && LoadoutUi.IsOpen())
                {
                    LoadoutUi.instance.OnDeployClick();
                }
                float until = Time.realtimeSinceStartup + 12f;
                while (LocalPlayer.actor != null && LocalPlayer.actor.dead && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }
            }
        }

        private static IEnumerator TestWeapons()
        {
            List<WeaponManager.WeaponEntry> entries = WeaponManager.instance.allWeapons.Where(e => e != null && e.prefab != null).ToList();
            Results.Add($"Weapons to test: {entries.Count} (VR {(VRManager.Active ? "on" : "off")})");
            foreach (WeaponManager.WeaponEntry entry in entries)
            {
                _context = entry.name;
                Actor actor = LocalPlayer.actor;
                if (actor == null || actor.dead)
                {
                    yield return Deploy();
                    actor = LocalPlayer.actor;
                    if (actor == null || actor.dead)
                    {
                        Results.Add($"{entry.name}: player could not respawn, stopping");
                        yield break;
                    }
                }
                Weapon weapon = null;
                try
                {
                    weapon = actor.EquipNewWeaponEntry(entry, Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1), true);
                }
                catch (Exception e)
                {
                    Problem($"[{entry.name}] equip: {e.GetType().Name}: {e.Message}");
                }
                if (weapon == null)
                {
                    // Usually the soldier died or was knocked down that moment: wait, respawn if needed, try once more.
                    yield return new WaitForSecondsRealtime(1f);
                    actor = LocalPlayer.actor;
                    if (actor == null || actor.dead)
                    {
                        yield return Deploy();
                        actor = LocalPlayer.actor;
                    }
                    if (actor != null && !actor.dead)
                    {
                        try
                        {
                            weapon = actor.EquipNewWeaponEntry(entry, Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1), true);
                        }
                        catch (Exception e)
                        {
                            Problem($"[{entry.name}] equip (retry): {e.GetType().Name}: {e.Message}");
                        }
                    }
                }
                var lines = new List<string>();
                if (VRManager.Active && VRConfig.SkipDrawAnimation.Value)
                {
                    // Two frames in, a skipped draw has the weapon idle already.
                    yield return null;
                    yield return null;
                    string drawClip = DrawClipPlaying(weapon);
                    if (drawClip != null)
                    {
                        Problem($"[{entry.name}] draw animation still playing ({drawClip})");
                        lines.Add("draw animation playing: " + drawClip);
                    }
                }
                yield return new WaitForSecondsRealtime(0.5f);
                HeldWeapon held = WeaponSystem.Instance != null ? WeaponSystem.Instance.Held : null;
                if (held != null && held.Weapon == weapon)
                {
                    string model = ModelCheck(weapon, held.Rig, entry);
                    if (model != null)
                    {
                        Problem($"[{entry.name}] no visible model: {model}");
                        lines.Add("NO VISIBLE MODEL");
                    }
                    yield return held.Reload.SelfCheck(lines.Add);
                    if (held.Rig.Data.Action == ActionType.Throwable && weapon is ThrowableWeapon)
                    {
                        var pinLines = new List<string>();
                        yield return WeaponSystem.Instance.ThrowableSelfCheck(held, pinLines.Add);
                        foreach (string l in pinLines)
                        {
                            lines.Add(l);
                            if (l.Contains("LIVE GRENADE WITH") || l.Contains("NOT REFUNDED") || l.Contains("NO GRENADE"))
                            {
                                Problem($"[{entry.name}] grenade pin: {l}");
                            }
                        }
                    }
                }
                if (weapon != null && weapon.gameObject.activeInHierarchy)
                {
                    // The game keeps a drawn weapon from firing for its draw delay (about a second): wait it out.
                    Weapon ready = weapon.GetActiveSubWeapon() ?? weapon;
                    float until = Time.realtimeSinceStartup + 3f;
                    while (ready != null && !ready.unholstered && Time.realtimeSinceStartup < until)
                    {
                        yield return null;
                    }
                    bool counts = held != null && held.Weapon == weapon && held.ActiveAmmo != null && held.ActiveAmmo.Participates
                                  && held.Rig.Data.Action != ActionType.Throwable;
                    _shots = 0;
                    Weapon listened = counts ? ready : null;
                    listened?.onSpawnProjectiles.AddListener(CountShot);
                    for (int i = 0; i < 3; i++)
                    {
                        try
                        {
                            Weapon active = weapon.GetActiveSubWeapon();
                            active.Fire(active.CurrentMuzzle().forward, true);
                            active.StopFire();
                        }
                        catch (Exception e)
                        {
                            Problem($"[{entry.name}] fire: {e.GetType().Name}: {e.Message} {FirstFrames(e)}");
                        }
                        yield return new WaitForSecondsRealtime(0.25f);
                        if (counts && held.ActiveAmmo.Rig.CyclesByHand)
                        {
                            held.Reload.CycleForTest(); // a bolt or pump needs working between shots
                        }
                    }
                    listened?.onSpawnProjectiles.RemoveListener(CountShot);
                    if (counts)
                    {
                        lines.Add($"fired {Mathf.Min(_shots, 3)}/3");
                        if (_shots == 0)
                        {
                            Problem($"[{entry.name}] DID NOT FIRE (counter {held.ActiveAmmo.CounterText()})");
                        }
                    }
                    if (FireSelector.HasModes(weapon))
                    {
                        FireSelector.Toggle(weapon);
                        FireSelector.Toggle(weapon);
                    }
                }
                foreach (string l in lines)
                {
                    if (Failure.IsMatch(l) && !l.Contains("LIVE GRENADE WITH") && !l.Contains("NOT REFUNDED") && !l.Contains("NO GRENADE"))
                    {
                        Problem($"[{entry.name}] {l}");
                    }
                }
                Results.Add($"{entry.name}: {(weapon != null ? "equipped" : "NOT EQUIPPED")}{(lines.Count > 0 ? "; " + string.Join("; ", lines.ToArray()) : "")}");
                if (++_tested % 25 == 0)
                {
                    Write(final: false); // progress on disk in case the run is cut short
                }
            }
        }

        /// <summary>
        /// Null when the held weapon has something drawable (an enabled, active, non-arm renderer with a mesh, a material
        /// and a real scale); otherwise what its renderers look like, to see why it is invisible.
        /// </summary>
        private static string ModelCheck(Weapon weapon, WeaponRig rig, WeaponManager.WeaponEntry entry)
        {
            var arms = new HashSet<Renderer>(rig.ArmRenderers);
            // Only weapons that have something to see: zombie claws and other invisible "weapons" show nothing by design.
            var armPaths = new HashSet<string>();
            foreach (Renderer r in rig.ArmRenderers)
            {
                if (r != null)
                {
                    armPaths.Add(PathOf(weapon.transform, r.transform));
                }
            }
            int expected = 0;
            foreach (Renderer r in entry.prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is ParticleSystemRenderer) && !(r is LineRenderer) && !(r is TrailRenderer) && r.enabled
                    && ActiveInPrefab(r.transform, entry.prefab.transform) && !armPaths.Contains(PathOf(entry.prefab.transform, r.transform)))
                {
                    expected++;
                }
            }
            if (expected == 0)
            {
                return null;
            }
            int total = 0, disabled = 0, inactive = 0, armsOnly = 0, noMesh = 0, zeroScale = 0, noMaterial = 0;
            var names = new List<string>();
            foreach (Renderer r in weapon.GetComponentsInChildren<Renderer>(true))
            {
                if (r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer || r.name.StartsWith("RavenfieldVR"))
                {
                    continue; // effects, and the mod's own counter / pin / scope objects
                }
                total++;
                if (names.Count < 6)
                {
                    names.Add(r.name);
                }
                if (arms.Contains(r)) { armsOnly++; continue; }
                if (!r.gameObject.activeInHierarchy) { inactive++; continue; }
                if (!r.enabled || r.forceRenderingOff) { disabled++; continue; }
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) { noMesh++; continue; }
                if (r.transform.lossyScale.sqrMagnitude < 1e-6f) { zeroScale++; continue; }
                bool material = false;
                foreach (Material m in r.sharedMaterials)
                {
                    material |= m != null;
                }
                if (!material) { noMaterial++; continue; }
                return null;
            }
            return $"{total} renderers ({armsOnly} counted as arms, {inactive} inactive, {disabled} disabled, {noMesh} without mesh, "
                   + $"{zeroScale} scaled to zero, {noMaterial} without material): {string.Join(", ", names.ToArray())}";
        }

        private static int _shots;
        private static void CountShot(Projectile[] projectiles) => _shots++;

        /// <summary>Self-check lines flag failures in capitals: NOT SEATED, NOTHING CHAMBERED, DROPPED MAGAZINE NOT GRABBABLE...</summary>
        private static readonly Regex Failure = new Regex(@"\b(NOT|NOTHING|NO) [A-Z]");

        private static string FirstFrames(Exception e)
        {
            string[] frames = (e.StackTrace ?? "").Split('\n');
            return frames.Length > 0 ? "at " + string.Join(" < ", frames.Take(3).Select(f => f.Trim()).ToArray()) : "";
        }

        private static string PathOf(Transform root, Transform t)
        {
            var sb = new StringBuilder();
            for (Transform p = t; p != null && p != root; p = p.parent)
            {
                sb.Insert(0, "/" + p.name);
            }
            return sb.ToString();
        }

        private static bool ActiveInPrefab(Transform t, Transform root)
        {
            for (Transform p = t; p != null; p = p.parent)
            {
                if (!p.gameObject.activeSelf)
                {
                    return false;
                }
                if (p == root)
                {
                    break;
                }
            }
            return true;
        }

        private static readonly Regex DrawClip = new Regex(@"draw|unholster|equip|pull ?out|take ?out|deploy|raise", RegexOptions.IgnoreCase);

        /// <summary>The name of a draw-like clip the weapon's animator is in (or blending into), or null.</summary>
        private static string DrawClipPlaying(Weapon weapon)
        {
            Animator anim = weapon != null ? weapon.animator : null;
            if (anim == null || !anim.isActiveAndEnabled || anim.runtimeAnimatorController == null)
            {
                return null;
            }
            for (int layer = 0; layer < anim.layerCount; layer++)
            {
                foreach (AnimatorClipInfo c in anim.GetCurrentAnimatorClipInfo(layer))
                {
                    if (c.clip != null && c.weight > 0.5f && DrawClip.IsMatch(c.clip.name))
                    {
                        return c.clip.name;
                    }
                }
                if (anim.IsInTransition(layer))
                {
                    foreach (AnimatorClipInfo c in anim.GetNextAnimatorClipInfo(layer))
                    {
                        if (c.clip != null && DrawClip.IsMatch(c.clip.name))
                        {
                            return c.clip.name;
                        }
                    }
                }
            }
            return null;
        }

        private static void Write(bool final = true)
        {
            _written |= final;
            var sb = new StringBuilder();
            sb.AppendLine($"Ravenfield VR {PluginInfo.Version} autotest, {DateTime.Now:yyyy-MM-dd HH:mm}, VR {(VRManager.Active ? "on" : "off")}");
            sb.AppendLine($"Problems: {Problems.Count}");
            foreach (string p in Problems)
            {
                sb.AppendLine("  ! " + p);
            }
            sb.AppendLine();
            sb.AppendLine("Performance");
            sb.AppendLine(PerfProbe.Report());
            sb.AppendLine("Results");
            foreach (string r in Results)
            {
                sb.AppendLine("  " + r);
            }
            try
            {
                File.WriteAllText(ReportPath, sb.ToString(), new UTF8Encoding(false));
                if (final)
                {
                    Log.Info($"Autotest finished: {Problems.Count} problems. Report: {ReportPath}");
                }
            }
            catch (Exception e)
            {
                Log.Exception("Writing the autotest report failed", e);
            }
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnAllContentLoaded))]
    internal static class AutoTestMutatorsPatch
    {
        private static void Prefix() => AutoTest.EnableAllMutators();
    }
}
