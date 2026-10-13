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

        /// <summary>Pictures of the body: hands and gloves, the holsters looked down at and from behind, the damage ring.</summary>
        private static IEnumerator BodyShots()
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            WeaponSystem sys = WeaponSystem.Instance;
            if (rig == null || sys == null)
            {
                yield break;
            }
            yield return new WaitForSecondsRealtime(1f);
            yield return new WaitForEndOfFrame();
            Transform head = rig.Head;
            Shots.Capture("body-view");
            Vector3 flat = Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized;
            Shots.Capture("body-looking-down", head.position, Quaternion.LookRotation(flat * 0.35f + Vector3.down, flat), 100f);
            Vector3 behind = head.position - flat * 0.9f + Vector3.up * 0.15f;
            Shots.Capture("body-from-behind", behind, Quaternion.LookRotation(head.position - Vector3.up * 0.35f - behind), 80f);
            Vector3 side = head.position + Vector3.Cross(Vector3.up, flat) * 0.8f - Vector3.up * 0.4f;
            Shots.Capture("body-from-the-side", side, Quaternion.LookRotation(head.position - Vector3.up * 0.55f - side), 80f);
            Rig.DamageFeedback damage = Rig.DamageFeedback.Instance;
            if (damage != null)
            {
                damage.OnVignette(0.85f, 3f);
                damage.OnHitDirection(60f, false);
                yield return new WaitForSecondsRealtime(0.08f);
                yield return new WaitForEndOfFrame();
                Shots.Capture("damage-from-front-left");
            }
        }

        /// <summary>Melee: the blade the swing detection uses, measured on the held weapon.</summary>
        private static string MeleeCheck(HeldWeapon held)
        {
            MeleeHandler.MeasureBlade(held.Rig, out Vector3 dir, out float length);
            float offAim = Vector3.Angle(dir, held.Rig.Data.AimAxis);
            return length > 0.12f ? $"melee: blade {length * 100f:0} cm from the grip, {offAim:0}° off the aim axis" : "melee: NO MEASURABLE BLADE";
        }

        /// <summary>Close-ups of the support hand on the handguard, and how much of it is buried in the model.</summary>
        private static void SupportCloseups(HeldWeapon held, string name, Action<string> report)
        {
            Rig.VRHand off = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Off : null;
            if (off == null || off.Model == null)
            {
                return;
            }
            Runtime.RigidPose g = held.GripWorld;
            Vector3 up = g.rotation * Vector3.up, right = g.rotation * Vector3.right, fwd = g.rotation * Vector3.forward;
            Vector3 c = off.Model.Root.position;
            void Shot(string side, Vector3 from) => Shots.Capture($"{name}-{side}", from, Quaternion.LookRotation(c - from, up), 40f);
            Shot("out", c - right * 0.3f + up * 0.05f);
            Shot("in", c + right * 0.3f + up * 0.05f);
            Shot("under", c - up * 0.28f - fwd * 0.08f);
            report($"support hand ({off.Role}): {held.HandClipping(off)}");
        }

        private static string _clipNow;
        private static HeldWeapon _clipPending;

        /// <summary>
        /// The gun hand measured against the model with hands closing on it (taken with the close-ups) and again posed the
        /// old way, by the weapon's hand pose alone, for comparison.
        /// </summary>
        private static IEnumerator ClipCompare(Action<string> report)
        {
            HeldWeapon held = _clipPending;
            _clipPending = null;
            if (held == null || Rig.VRRig.Instance == null)
            {
                yield break;
            }
            Rig.HandModel.ClosingDisabled = true;
            yield return new WaitForSecondsRealtime(0.4f);
            string before = held.HandClipping(Rig.VRRig.Instance.Dominant);
            Rig.HandModel.ClosingDisabled = false;
            yield return new WaitForSecondsRealtime(0.3f);
            report($"gun hand: {_clipNow} (posed by the weapon's hand alone: {before})");
        }

        /// <summary>Close-ups of the gun hand on its grip: from the back of the hand, from the fingertips' side and from below.</summary>
        private static void GripCloseups(HeldWeapon held, string name, Action<string> report)
        {
            Runtime.RigidPose g = held.GripWorld;
            Vector3 fwd = g.rotation * Vector3.forward, up = g.rotation * Vector3.up, right = g.rotation * Vector3.right;
            Vector3 c = g.position + fwd * 0.03f - up * 0.02f;
            void Shot(string side, Vector3 from) => Shots.Capture($"{name}-{side}", from, Quaternion.LookRotation(c - from, up), 40f);
            Shot("out", c + right * 0.3f + up * 0.05f);
            Shot("in", c - right * 0.3f + up * 0.05f);
            Shot("under", c - up * 0.28f + fwd * 0.1f + right * 0.06f);
            if (Rig.VRRig.Instance != null)
            {
                report("hand: " + held.HandReport(Rig.VRRig.Instance.Dominant));
            }
            if (Rig.VRRig.Instance != null && held.Volume != null && held.Volume.Ready)
            {
                _clipNow = held.HandClipping(Rig.VRRig.Instance.Dominant);
                _clipPending = held;
            }
            // The weapon's own arms where its author animated them, for comparison.
            Renderer[] arms = held.Rig.ArmRenderers;
            if (arms.Length > 0)
            {
                bool[] was = arms.Select(r => r != null && r.enabled).ToArray();
                foreach (Renderer r in arms)
                {
                    if (r != null)
                    {
                        r.enabled = true;
                    }
                }
                Shot("modarms-out", c + right * 0.3f + up * 0.05f);
                Shot("modarms-in", c - right * 0.3f + up * 0.05f);
                for (int i = 0; i < arms.Length; i++)
                {
                    if (arms[i] != null)
                    {
                        arms[i].enabled = was[i];
                    }
                }
            }
        }

        /// <summary>
        /// Forgiving grab: the off hand squeezes, and a tenth of a second later a dropped weapon is within reach. It must be
        /// taken (into its slot) as a reaching hand would take it.
        /// </summary>
        private static IEnumerator ReachCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Actor actor = LocalPlayer.actor;
            Rig.VRHand off = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Off : null;
            if (sys == null || actor == null || off == null || off.Role != Rig.HandRole.Free)
            {
                report($"reaching grab: skipped (off hand {(off != null ? off.Role.ToString() : "missing")})");
                yield break;
            }
            WeaponManager.WeaponEntry entry = WeaponManager.instance.allWeapons.FirstOrDefault(e => e != null && e.prefab != null && e.slot == WeaponManager.WeaponSlot.Secondary);
            if (entry == null)
            {
                report("reaching grab: skipped (no secondary weapon installed)");
                yield break;
            }
            Input.VRInput.SimulateGrabPress(off.Side);
            yield return new WaitForSecondsRealtime(0.1f);
            DroppedWeapon drop = sys.Drops.Spawn(entry, new Runtime.RigidPose(off.Palm.position + off.Palm.Forward * 0.04f, off.Palm.rotation), Vector3.one, Vector3.zero, -1, -1);
            if (drop != null && drop.Go != null)
            {
                drop.Go.GetComponent<Rigidbody>().isKinematic = true; // hold it in reach for the check
            }
            float until = Time.realtimeSinceStartup + 0.4f;
            while (drop != null && drop.Go != null && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
            Input.VRInput.SimulatedGrab[(int)off.Side] = false;
            bool taken = drop == null || drop.Go == null;
            report($"reaching grab: {(taken ? "a weapon arriving just after the squeeze was taken" : "NOT TAKEN (the squeeze came a moment early)")}");
            if (!taken)
            {
                UnityEngine.Object.Destroy(drop.Go);
            }
        }

        /// <summary>Lets the held weapon fall as an opened hand would, pictures it, and waits for it to go back to its holster.</summary>
        private static IEnumerator DropCheck(Action<string> report)
        {
            WeaponSystem sys = WeaponSystem.Instance;
            Actor actor = LocalPlayer.actor;
            if (sys == null || actor == null)
            {
                yield break;
            }
            int slot = sys.TestLetFall();
            if (slot < 0)
            {
                report("let go: NOTHING DROPPED");
                yield break;
            }
            yield return new WaitForSecondsRealtime(0.7f);
            yield return new WaitForEndOfFrame();
            Shots.Capture("dropped-weapon");
            Transform head = Rig.VRRig.Instance.Head;
            Shots.Capture("dropped-weapon-looking-down", head.position, Quaternion.LookRotation(Vector3.ProjectOnPlane(head.forward, Vector3.up).normalized * 0.6f + Vector3.down), 90f);
            bool gone = actor.weapons[slot] == null;
            float until = Time.realtimeSinceStartup + VRConfig.WeaponReturnSeconds.Value + 3f;
            while (actor.weapons[slot] == null && Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
            report($"let go: {(gone ? "fell out of the hand" : "STILL IN THE LOADOUT")}, {(actor.weapons[slot] != null ? "back in its holster" : "NEVER CAME BACK")}");
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
            if (VRManager.Active)
            {
                yield return BodyShots();
            }
            int every = Mathf.Max(1, entries.Count / 28);
            if (Environment.GetCommandLineArgs().Any(a => a.Equals("-rfvr-quick", StringComparison.OrdinalIgnoreCase)))
            {
                // Quick run: the weapons that get a picture, the first few melee weapons, grenades and pistols, and the
                // first rifle (tactical reload and the let-go check).
                int n = 0;
                entries = entries.Where(e =>
                {
                    n++;
                    Weapon w = e.prefab.GetComponent<Weapon>();
                    return n % every == 0 || w is MeleeWeapon || w is ThrowableWeapon || e.slot == WeaponManager.WeaponSlot.Primary && n < 40;
                }).Take(60).ToList();
                Results.Add($"Quick run: {entries.Count} weapons");
                every = Mathf.Max(1, entries.Count / 28);
            }
            int index = 0, meleeShots = 0, throwShots = 0, pistolShots = 0;
            bool tacticalDone = false, dropDone = false;
            foreach (WeaponManager.WeaponEntry entry in entries)
            {
                index++;
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
                    if (VRManager.Active)
                    {
                        // A picture of the weapon in the (simulated) hands: a spread across the list, plus melee, grenades
                        // and pistols.
                        ActionType action = held.Rig.Data.Action;
                        bool want = index % every == 0
                                    || action == ActionType.Melee && meleeShots++ < 4
                                    || action == ActionType.Throwable && throwShots++ < 3
                                    || held.Rig.Data.IsPistol && action == ActionType.SelfLoading && pistolShots++ < 4;
                        if (want)
                        {
                            yield return new WaitForEndOfFrame();
                            Shots.Capture($"weapon-{index:000}-{entry.name}");
                            GripCloseups(held, $"grip-{index:000}-{entry.name}", lines.Add);
                            yield return ClipCompare(lines.Add);
                        }
                        if (action == ActionType.Melee)
                        {
                            lines.Add(MeleeCheck(held));
                        }
                        if (want && !held.Rig.Data.IsPistol && action == ActionType.SelfLoading)
                        {
                            int shotIndex = index;
                            yield return held.Reload.TwoHandSelfCheck(lines.Add, name =>
                            {
                                Shots.Capture($"{name}-{shotIndex:000}-{entry.name}");
                                SupportCloseups(held, $"support-{shotIndex:000}-{entry.name}", lines.Add);
                            });
                        }
                        if (!tacticalDone && held.ActiveAmmo != null && held.ActiveAmmo.UsesDetachableMag && !held.Rig.Data.IsPistol && held.Rig.Data.MagKickable)
                        {
                            tacticalDone = true;
                            yield return held.Reload.TacticalSelfCheck(lines.Add, name => Shots.Capture(name));
                        }
                        if (!dropDone && tacticalDone && action == ActionType.SelfLoading && !held.Rig.Data.IsPistol)
                        {
                            dropDone = true;
                            yield return DropCheck(lines.Add);
                            yield return ReachCheck(lines.Add);
                            weapon = actor.weapons != null && (int)entry.slot < actor.weapons.Length ? actor.weapons[Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1)] : null;
                            if (weapon != null && actor.activeWeapon != weapon)
                            {
                                actor.SwitchWeapon(Mathf.Clamp((int)entry.slot, 0, actor.weapons.Length - 1));
                                yield return new WaitForSecondsRealtime(1f);
                            }
                        }
                    }
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
