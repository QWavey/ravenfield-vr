using System;
using System.Collections.Generic;
using HarmonyLib;
using RavenfieldVR.Core;
using RavenfieldVR.Input;
using RavenfieldVR.Rig;
using RavenfieldVR.Runtime;
using RavenfieldVR.Util;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    // WeaponDrops, part: a dead soldier's weapon made into a world copy: templates, models, shapes and their fits.
    // The class, the drops' lifetime and teardown: WeaponDrops.cs.
    public sealed partial class WeaponDrops
    {
        // ---- spawning ---------------------------------------------------------------------------------

        /// <summary>
        /// A dropped copy of a living soldier's gun where his hand holds it, with its rounds (a hand wrested it from him: SoldierGuns).
        /// Null when it could not be made. Kept (never culled before the hand takes it), as the player's own swapped weapon is.
        /// </summary>
        internal DroppedWeapon DropFromHands(Actor actor, Weapon w, Vector3 velocity)
        {
            if (actor == null || w == null || w.weaponEntry == null || w.weaponEntry.prefab == null || w.IsMountedWeapon() || w.thirdPersonTransform == null)
            {
                return null;
            }
            Transform tp = w.thirdPersonTransform;
            Transform hold = tp.parent != null ? tp.parent : tp; // (as a death's drop: the model at the game's own offset under the pivot)
            Vector3 holdScale = DropScale(tp.parent != null ? tp.parent.lossyScale : Vector3.one);
            int spare = w.configuration.spareAmmo >= 0 ? Mathf.RoundToInt(w.configuration.spareAmmo * VRConfig.DroppedWeaponAmmo.Value) : -1;
            return Spawn(w.weaponEntry, new RigidPose(hold.position, hold.rotation), holdScale, velocity, Mathf.Max(0, w.ammo), spare, keep: true);
        }

        /// <summary>Called just before a soldier dies (its weapons are destroyed right after).</summary>
        public void OnActorDying(Actor actor, DamageInfo info)
        {
            // (None kept at all: no drop built on the death frame only for the cull at its end to destroy it again.)
            if (!VRConfig.EnemyWeaponDrops.Value || VRConfig.MaxDroppedWeapons.Value <= 0 || actor == null || !actor.aiControlled)
            {
                return;
            }
            Weapon w = actor.activeWeapon;
            if (w == null || w.weaponEntry == null || w.weaponEntry.prefab == null || w.IsMountedWeapon() || w.thirdPersonTransform == null)
            {
                return;
            }
            Transform tp = w.thirdPersonTransform;
            // The copy holds the model at the game's own offset, turn and scale under the weapon's pivot: the pivot goes
            // where the soldier's hand holds it. At the model's own pose and scale both counted twice (a half-size weapon
            // dropped at a quarter size, off to the side).
            Transform hold = tp.parent != null ? tp.parent : tp;
            Vector3 holdScale = tp.parent != null ? tp.parent.lossyScale : Vector3.one;
            Vector3 velocity = actor.Velocity() + Vector3.ClampMagnitude(info.impactForce * 0.004f, 3f) + UnityEngine.Random.insideUnitSphere * 0.4f;
            int spare = w.configuration.spareAmmo >= 0
                ? Mathf.RoundToInt(w.configuration.spareAmmo * VRConfig.DroppedWeaponAmmo.Value)
                : -1;
            // A soldier's hand bone carries its scale with a wobble in the third decimal (0.999, 1.000, 1.001): taken as it was, nearly
            // every drop missed the shape kept per scale and re-cooked its colliders on the death frame (a hitch on kills; user,
            // 2026-10-19 night, task 309). Within 1.5% of 1 it is 1 (the warm-up's shapes), else rounded to 1%.
            holdScale = DropScale(holdScale);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int collections = GC.CollectionCount(0);
            bool hadModel = Templates.ContainsKey(w.weaponEntry);
            Spawn(w.weaponEntry, new RigidPose(hold.position, hold.rotation), holdScale, velocity, Mathf.Max(0, w.ammo), spare, open: actor.CenterPosition());
            float ms = (float)watch.Elapsed.TotalMilliseconds;
            _deathDrops++;
            _deathDropMs += ms;
            _deathDropMax = Mathf.Max(_deathDropMax, ms);
            if (ms > 20f)
            {
                // Its parts, to tell which one a slow kill spent its time on (task 309): the model's copy (its first one builds the
                // template), the shape, the colliders, and whether a garbage collection ran in between.
                Log.Info($"Weapon drop of {w.weaponEntry.name} took {ms:0} ms on the death frame (scale {holdScale.x:0.00}; shape "
                         + $"{(RenderedShapes.ContainsKey(w.weaponEntry) ? "rendered" : _lastLateFit ? "fitted later (a box till then)" : "fitted")}; "
                         + $"model {_lastModelMs:0} ms{(hadModel ? "" : _lastAnalysisMs >= 0f ? $" with its first template (its analysis {_lastAnalysisMs:0} ms of it)" : " with its first template")}, "
                         + $"shape {_lastShapeMs:0} ms, colliders {_lastColliderMs:0} ms, ignoring the player and bodies {_lastIgnoreMs:0} ms, "
                         + $"the ground test {_lastGroundMs:0} ms, the cull {_lastCullMs:0} ms"
                         + $"{(GC.CollectionCount(0) != collections ? ", a garbage collection ran" : "")}).");
            }
        }

        // Every death frame's drop, for the input check's line (only those over 20 ms were logged: the cost of the many small ones
        // in a fight was unknown; 2026-10-21).
        private static int _deathDrops;
        private static float _deathDropMs, _deathDropMax;

        /// <summary>The death-frame drops since the last call: how many, their average and longest ms ("" when none).</summary>
        internal static string TakeDropTiming()
        {
            if (_deathDrops == 0)
            {
                return "";
            }
            string s = $", drops {_deathDrops} ({_deathDropMs / _deathDrops:0.0} ms avg, {_deathDropMax:0} max)";
            _deathDrops = 0;
            _deathDropMs = _deathDropMax = 0f;
            return s;
        }

        /// <summary>A drop's scale snapped: 1 within 1.5%, else each axis to the nearest 1% (so drops share the shapes kept per scale).</summary>
        internal static Vector3 DropScale(Vector3 s)
        {
            if (Mathf.Abs(s.x - 1f) < 0.015f && Mathf.Abs(s.y - 1f) < 0.015f && Mathf.Abs(s.z - 1f) < 0.015f)
            {
                return Vector3.one;
            }
            return new Vector3(Mathf.Round(s.x * 100f) / 100f, Mathf.Round(s.y * 100f) / 100f, Mathf.Round(s.z * 100f) / 100f);
        }

        // Stripped third-person models, built once per weapon type: copying the whole weapon prefab (scripts,
        // animator, first-person rig) on every bot death cost over a millisecond each time.
        private static readonly Dictionary<WeaponManager.WeaponEntry, GameObject> Templates = new Dictionary<WeaponManager.WeaponEntry, GameObject>();
        private static GameObject _templateRoot;

        /// <summary>Weapons that have no third-person model to copy: not tried again (the copy instantiates the whole prefab).</summary>
        private static readonly HashSet<WeaponManager.WeaponEntry> NoModel = new HashSet<WeaponManager.WeaponEntry>();

        internal static GameObject ModelFor(WeaponManager.WeaponEntry entry)
        {
            GameObject template = TemplateFor(entry);
            if (template == null)
            {
                return null;
            }
            GameObject model = UnityEngine.Object.Instantiate(template);
            model.SetActive(true);
            return model;
        }

        /// <summary>The stripped model a drop of this type copies, built the first time (null: the type has none).</summary>
        private static GameObject TemplateFor(WeaponManager.WeaponEntry entry)
        {
            if (entry == null || NoModel.Contains(entry))
            {
                return null;
            }
            if (!Templates.TryGetValue(entry, out GameObject template) || template == null)
            {
                template = BuildWorldModel(entry);
                if (template == null)
                {
                    NoModel.Add(entry);
                    return null;
                }
                HideParkedAmmo(entry, template);
                StripStrays(entry, template);
                // Its copies cast no shadows (they still take them): each lying or holstered weapon's pieces were drawn into the shadow
                // maps once per eye with a preset's shadows on, for a shadow few would see at Medium's resolution (perf scouts,
                // 2026-10-27; the user's Medium ran at half rate on the CPU, Tasks 348). One of only shadows has nothing left to draw.
                foreach (Renderer r in template.GetComponentsInChildren<Renderer>(true))
                {
                    if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
                    {
                        r.enabled = false;
                    }
                    r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }
                if (_templateRoot == null)
                {
                    _templateRoot = new GameObject("RavenfieldVR Drop Templates");
                    _templateRoot.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(_templateRoot);
                }
                template.transform.SetParent(_templateRoot.transform, false);
                Templates[entry] = template;
            }
            return template;
        }

        private static readonly Queue<WeaponManager.WeaponEntry> LoadingBuilds = new Queue<WeaponManager.WeaponEntry>();
        private static int _loadingBuildFrame = -100;

        /// <summary>
        /// While the loading screen is up: the drop templates of the weapons the bots can carry (both teams' pools in the match's
        /// settings) and of the player's loadout, built about 20 ms a frame. A match started before the menu's warm-up was through
        /// built them on soldiers' death frames, 24-39 ms each (the user's sessions of 2026-10-20: 58 and 16 drops over 20 ms,
        /// "model ... with its first template"; task 309). A dropped frame on the loading screen does not matter.
        /// </summary>
        internal static void BuildDuringLoading()
        {
            if (!VRConfig.EnemyWeaponDrops.Value && !VRConfig.ShowHolsters.Value)
            {
                return;
            }
            if (Time.frameCount - _loadingBuildFrame > 30)
            {
                // A new loading screen: the match's pools as they are now.
                LoadingBuilds.Clear();
                var seen = new HashSet<WeaponManager.WeaponEntry>();
                TeamInfo[] teams = GameManager.instance != null && GameManager.instance.gameInfo != null ? GameManager.instance.gameInfo.team : null;
                if (teams != null)
                {
                    foreach (TeamInfo team in teams)
                    {
                        if (team == null || team.availableWeapons == null)
                        {
                            continue;
                        }
                        foreach (WeaponManager.WeaponEntry e in team.availableWeapons.All())
                        {
                            if (e != null && e.prefab != null && seen.Add(e) && !NoModel.Contains(e))
                            {
                                LoadingBuilds.Enqueue(e);
                            }
                        }
                    }
                }
                WeaponManager.LoadoutSet loadout = LoadoutUi.instance != null ? LoadoutUi.instance.loadout : null;
                if (loadout != null)
                {
                    foreach (WeaponManager.WeaponEntry e in new[] { loadout.primary, loadout.secondary, loadout.gear1, loadout.gear2, loadout.gear3 })
                    {
                        if (e != null && e.prefab != null && seen.Add(e) && !NoModel.Contains(e))
                        {
                            LoadingBuilds.Enqueue(e);
                        }
                    }
                }
                if (LoadingBuilds.Count > 0)
                {
                    Log.Info($"Loading screen: building the dropped models of {LoadingBuilds.Count} weapon type(s) the match can drop.");
                }
            }
            _loadingBuildFrame = Time.frameCount;
            DrainBuilds(20.0);
        }


        /// <summary>
        /// The types the living soldiers carry that the loading screen did not get to, built while the player is dead or in a menu.
        /// The loading screen queues every type the match's pools hold (286 with the user's workshop weapons) and was gone with
        /// most still to build; a type whose third-person model needs the whole prefab copied then cost 235-300 ms on a soldier's
        /// death frame (the PKP, PKM, Type 80, BRN180s; the user's session of 2026-10-20 night). Only the carried ones (the next
        /// drops; looked at again every 5 s), and a slow one at most once a second: a menu freezing a quarter of a second on
        /// every frame is worse in the headset than the hitch it saves.
        /// </summary>
        internal static void BuildWhileIdle()
        {
            if (Time.frameCount - _loadingBuildFrame <= 30 || !(GameContext.GameMenuOpen || !GameContext.PlayerAlive)
                || !VRConfig.EnemyWeaponDrops.Value && !VRConfig.ShowHolsters.Value)
            {
                return; // the loading screen is building, or the player is in play
            }
            float now = Time.unscaledTime;
            if (now >= _nextCarriedLook)
            {
                _nextCarriedLook = now + 5f;
                QueueCarried();
            }
            if (IdleBuilds.Count == 0 || now < _nextIdleBuild)
            {
                return;
            }
            WeaponManager.WeaponEntry e = IdleBuilds.Dequeue();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Its model, then its shape, then its analysis, one a turn (as the loading screen goes). The shape step never ran:
                // a model built went back in line, and the next turn found it built with no shape and put it back again, for good
                // (the second test could never hold where the first had not).
                if (!Templates.ContainsKey(e))
                {
                    if (TemplateFor(e) != null)
                    {
                        IdleBuilds.Enqueue(e); // back in line for its shape (a type with no model is not)
                    }
                }
                else if (!HasShape(e))
                {
                    if (Prepare(e))
                    {
                        IdleBuilds.Enqueue(e); // and its analysis, if the model took its parked parts from the cache
                    }
                    else
                    {
                        IdleFailed.Add(e); // not queued again (QueueCarried looks it over every 5 s)
                    }
                }
                else if (NeedsAnalysis(e, out Weapon prefab))
                {
                    if (GameContext.GameMenuOpen)
                    {
                        WeaponAnalyzer.Get(prefab, e); // a pickup of the type then waits for no analysis
                    }
                    else
                    {
                        // Not while spectating: an analysis is 25-270 ms, and dead in the follow view each one froze the moving
                        // picture in the headset once a second (the user's session of 2026-10-20 night: 2-8 ms a frame on
                        // average there). The respawn and loadout screens take them.
                        IdleBuilds.Enqueue(e);
                        _nextIdleBuild = now + 0.5f;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WarnOnce("idledrop:" + e.name, $"Building the dropped model of {e.name} while idle failed: {ex.Message}");
                NoModel.Add(e);
            }
            // In the follow view (dead, the headset still showing the match) a step over 4 ms waits a second too: a template or a
            // shape fit of 5-30 ms ran on every frame there, each one a third of a 90 Hz frame or more (perf scouts, 2026-10-27).
            double took = watch.Elapsed.TotalMilliseconds;
            if (took > 30.0 || !GameContext.GameMenuOpen && took > 4.0)
            {
                _nextIdleBuild = now + 1f;
            }
        }

        /// <summary>Whether the type's prefab has no analysis yet (its drop's model took its parked parts from <see cref="ParkedPartsCache"/>).</summary>
        private static bool NeedsAnalysis(WeaponManager.WeaponEntry e, out Weapon prefab)
        {
            prefab = e.prefab != null ? e.prefab.GetComponent<Weapon>() : null;
            return prefab != null && !WeaponAnalyzer.IsCached(prefab, e);
        }

        private static readonly Queue<WeaponManager.WeaponEntry> IdleBuilds = new Queue<WeaponManager.WeaponEntry>();
        /// <summary>Types whose shape could not be prepared while idle: their drops fit the shape late, on their own.</summary>
        private static readonly HashSet<WeaponManager.WeaponEntry> IdleFailed = new HashSet<WeaponManager.WeaponEntry>();
        private static float _nextIdleBuild, _nextCarriedLook;

        /// <summary>The types the living soldiers carry whose dropped model, shape or analysis is not ready, queued for <see cref="BuildWhileIdle"/>.</summary>
        private static void QueueCarried()
        {
            List<Actor> actors = ActorManager.instance != null ? ActorManager.instance.actors : null;
            if (actors == null)
            {
                return;
            }
            var queued = new HashSet<WeaponManager.WeaponEntry>(IdleBuilds);
            var looked = new HashSet<WeaponManager.WeaponEntry>();
            int added = 0;
            foreach (Actor a in actors)
            {
                if (a == null || a.dead || a.weapons == null)
                {
                    continue;
                }
                foreach (Weapon w in a.weapons)
                {
                    WeaponManager.WeaponEntry e = w != null ? w.weaponEntry : null;
                    // A type whose model a death frame built (its shape fitted later, its parked parts from the cache) still has
                    // its shape and its analysis to go.
                    if (e != null && e.prefab != null && looked.Add(e) && !queued.Contains(e) && !NoModel.Contains(e) && !IdleFailed.Contains(e)
                        && (!Templates.ContainsKey(e) || !HasShape(e) || NeedsAnalysis(e, out _)))
                    {
                        queued.Add(e);
                        IdleBuilds.Enqueue(e);
                        added++;
                    }
                }
            }
            if (added > 0)
            {
                Log.InfoOnce("idlebuild:" + added + ":" + IdleBuilds.Count, $"Dropped models to build while idle: {added} type(s) the soldiers carry ({IdleBuilds.Count} queued).");
            }
        }

        /// <summary>Builds queued drop templates (and their shapes, one fit a frame) for about <paramref name="budgetMs"/>.</summary>
        private static void DrainBuilds(double budgetMs)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            bool fitted = false;
            while (LoadingBuilds.Count > 0 && watch.Elapsed.TotalMilliseconds < budgetMs)
            {
                WeaponManager.WeaponEntry e = LoadingBuilds.Peek();
                if (Templates.ContainsKey(e) && HasShape(e))
                {
                    if (!NeedsAnalysis(e, out Weapon prefab))
                    {
                        LoadingBuilds.Dequeue();
                        continue; // ready from the menu's warm-up or an earlier match
                    }
                    if (fitted)
                    {
                        break; // one heavy step a frame
                    }
                    // Its analysis too: a model that took its parked parts from the cache (ParkedPartsCache) did not run it, and the
                    // type's first pickup or draw in the match then did (25-270 ms in the hand). Before the cache the model's
                    // build ran it here as a side effect.
                    LoadingBuilds.Dequeue();
                    try
                    {
                        WeaponAnalyzer.Get(prefab, e);
                    }
                    catch (Exception ex)
                    {
                        Log.WarnOnce("loadinganalysis:" + e.name, $"Analysing {e.name} on the loading screen failed: {ex.Message}");
                    }
                    fitted = true;
                    continue;
                }
                if (Templates.ContainsKey(e) && fitted)
                {
                    break; // one shape fitted a frame (a fit takes tens of milliseconds)
                }
                LoadingBuilds.Dequeue();
                try
                {
                    if (Templates.ContainsKey(e))
                    {
                        // Its shape too, as the warm-up prepares it: the first drop of the type then gets its own shape at once.
                        Prepare(e);
                        fitted = true;
                    }
                    else if (TemplateFor(e) != null)
                    {
                        LoadingBuilds.Enqueue(e); // back in line for its shape (a type with no model is not)
                    }
                }
                catch (Exception ex)
                {
                    Log.WarnOnce("loadingdrop:" + e.name, $"Building the dropped model of {e.name} on the loading screen failed: {ex.Message}");
                    NoModel.Add(e);
                }
            }
        }

        private static readonly System.Text.RegularExpressions.Regex BeamName =
            new System.Text.RegularExpressions.Regex(@"beam|(?<![a-z])cone|volumetric|light ?shaft|light ?ray|flare(?! ?gun)|glow", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// Hides in the template what a holstered or dropped copy must not draw: a laser's beam or a light's cone (by name: the
        /// FNX-45 laser variants' 'laser_beam' reached out of the holster across the view), and a small part standing apart from the
        /// gun's body, more than 5 cm from the cluster of touching parts grown from the biggest (the FNX-45 RDS's 'Sight (1)' parked
        /// 27 cm in front stretched the copy's box, which turned and placed it wrongly and hid it from the eyes test: user,
        /// 2026-10-19 night: "fnx 45 is in front of my view when holding a primary", task 303). Only parts under 40% of the body's
        /// size: an akimbo pair's second gun, as big as the first, stays (tools/export_checks/stray_parts_rule.py).
        /// </summary>
        private static void StripStrays(WeaponManager.WeaponEntry entry, GameObject template)
        {
            var parts = new List<Renderer>();
            var boxes = new List<Bounds>();
            int beams = 0, strays = 0;
            // A weapon named like a beam (a flare gun) keeps its parts: their names say flare too (review, 2026-10-20).
            bool beamNamedWeapon = BeamName.IsMatch(entry.name ?? "") || (entry.name ?? "").IndexOf("flare", StringComparison.OrdinalIgnoreCase) >= 0;
            foreach (Renderer r in template.GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer) || !r.enabled)
                {
                    continue;
                }
                if (!beamNamedWeapon && (BeamName.IsMatch(r.name) || r.transform.parent != null && BeamName.IsMatch(r.transform.parent.name)))
                {
                    r.enabled = false;
                    beams++;
                    continue;
                }
                Mesh mesh = r is SkinnedMeshRenderer smr ? smr.sharedMesh : r.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null)
                {
                    continue;
                }
                // From the mesh's own box (the template may be inactive, and an inactive renderer's bounds are empty).
                Bounds local = mesh.bounds;
                Matrix4x4 m = r.transform.localToWorldMatrix;
                var b = new Bounds(m.MultiplyPoint3x4(local.center), Vector3.zero);
                for (int c = 0; c < 8; c++)
                {
                    b.Encapsulate(m.MultiplyPoint3x4(local.center + Vector3.Scale(local.extents, new Vector3((c & 1) == 0 ? -1 : 1, (c & 2) == 0 ? -1 : 1, (c & 4) == 0 ? -1 : 1))));
                }
                if (b.size.sqrMagnitude < 1e-10f)
                {
                    continue; // scaled away (parked ammunition hidden by scale)
                }
                parts.Add(r);
                boxes.Add(b);
            }
            if (parts.Count >= 2)
            {
                int seed = 0;
                for (int i = 1; i < boxes.Count; i++)
                {
                    Vector3 s = boxes[i].size, best = boxes[seed].size;
                    if (s.x * s.y * s.z > best.x * best.y * best.z)
                    {
                        seed = i;
                    }
                }
                var inBody = new bool[parts.Count];
                inBody[seed] = true;
                Bounds body = boxes[seed];
                for (bool grew = true; grew;)
                {
                    grew = false;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        if (inBody[i])
                        {
                            continue;
                        }
                        for (int j = 0; j < parts.Count; j++)
                        {
                            if (inBody[j] && Gap(boxes[i], boxes[j]) <= 0.05f)
                            {
                                inBody[i] = grew = true;
                                body.Encapsulate(boxes[i]);
                                break;
                            }
                        }
                    }
                }
                // The parts apart from the body, in their own clusters of touching parts, each judged whole: an akimbo pair's second
                // gun is many small parts (the P220 akimbos' twenty) and must stay as one.
                float bodySize = body.size.magnitude;
                var group = new int[parts.Count];
                for (int i = 0; i < parts.Count; i++)
                {
                    group[i] = inBody[i] ? 0 : -1;
                }
                int groups = 0;
                for (int i = 0; i < parts.Count; i++)
                {
                    if (group[i] != -1)
                    {
                        continue;
                    }
                    groups++;
                    group[i] = groups;
                    Bounds g = boxes[i];
                    var members = new List<int> { i };
                    for (bool grew = true; grew;)
                    {
                        grew = false;
                        for (int k = 0; k < parts.Count; k++)
                        {
                            if (group[k] != -1)
                            {
                                continue;
                            }
                            foreach (int m2 in members)
                            {
                                if (Gap(boxes[k], boxes[m2]) <= 0.05f)
                                {
                                    group[k] = groups;
                                    members.Add(k);
                                    g.Encapsulate(boxes[k]);
                                    grew = true;
                                    break;
                                }
                            }
                        }
                    }
                    // Small beside the body, or parked far from it (farther than half the body's size: the FNX-45 RDS's sight 27 cm in
                    // front of a 20 cm pistol) and not as big as it (a second gun is).
                    float size = g.size.magnitude;
                    if (size < 0.4f * bodySize || Gap(g, body) > 0.5f * bodySize && size < 0.7f * bodySize)
                    {
                        foreach (int m2 in members)
                        {
                            parts[m2].enabled = false;
                            strays++;
                        }
                    }
                }
            }
            if (beams + strays > 0)
            {
                Log.InfoOnce("strays:" + entry.name, $"{entry.name}: its holstered and dropped copy leaves out {beams} beam(s) and {strays} part(s) parked away from the gun.");
            }
        }

        /// <summary>The distance between two boxes (0 when they touch).</summary>
        private static float Gap(Bounds a, Bounds b) => Vector3.Max(Vector3.zero, Vector3.Max(a.min - b.max, b.min - a.max)).magnitude;

        /// <summary>
        /// Hides in the template the parked ammunition the held gun hides (<see cref="WeaponRigData.SparePartPaths"/>, put away
        /// by <see cref="WeaponRig.HideSpareParts"/>). A holstered or dropped copy drew it floating beside the gun: the spare
        /// magazine, drum or round the reload brings in (user, 2026-10-18 evening: "some mags of other weapons float in the air
        /// if they are not equipped"; Task 268). The copy is the third-person model only, so the parked parts inside that model
        /// are the ones hidden: their paths run from the weapon's root, and the model's own path is where each one starts.
        /// </summary>
        private static void HideParkedAmmo(WeaponManager.WeaponEntry entry, GameObject template)
        {
            Weapon prefab = entry.prefab != null ? entry.prefab.GetComponent<Weapon>() : null;
            Transform tpAsset = prefab != null ? prefab.thirdPersonTransform : null;
            if (tpAsset == null || template.transform.childCount == 0)
            {
                return;
            }
            int[] tpPath = WeaponRig.PathOf(prefab.transform, tpAsset);
            if (tpPath == null)
            {
                return;
            }
            // The analysis the warm-up made of this prefab, or the parts an earlier session's analysis found (ParkedPartsCache:
            // analysed here on a soldier's death frame, the type's first drop cost 235-300 ms); else it is analysed here once.
            List<int[]> spare;
            if (WeaponAnalyzer.IsCached(prefab, entry) || !ParkedPartsCache.TryGet(WeaponAnalyzer.KeyOf(prefab, entry), prefab.transform, out spare))
            {
                bool analysing = !WeaponAnalyzer.IsCached(prefab, entry);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                spare = WeaponAnalyzer.Get(prefab, entry).SparePartPaths;
                if (analysing)
                {
                    _lastAnalysisMs = (float)watch.Elapsed.TotalMilliseconds;
                }
            }
            else
            {
                Log.InfoOnce("parkedcache-hit", $"Copies of {entry.name}: parked ammunition from the cache of earlier sessions, no analysis on this frame (the first such type this session).");
            }
            Transform model = template.transform.GetChild(0); // the template's only child: the third-person model
            int hidden = HideInWorldModel(model, tpPath, spare);
            if (hidden > 0)
            {
                Log.InfoOnce("parkedcopy:" + entry.name, $"Copies of {entry.name} (holstered and dropped): {hidden} parked ammunition part(s) hidden, as in the hand.");
            }
        }

        /// <summary>
        /// The magazine hidden on a copy of a weapon type's world model (<see cref="ModelFor"/>: a drop's, a holster's): its parts found
        /// from an analysis already made (never one run here). The number hidden; -1 when it cannot be told (no analysis, no model).
        /// </summary>
        internal static int HideMagazineOnModel(GameObject model, WeaponManager.WeaponEntry entry)
        {
            Weapon prefab = entry?.prefab != null ? entry.prefab.GetComponent<Weapon>() : null;
            Transform tpAsset = prefab != null ? prefab.thirdPersonTransform : null;
            if (tpAsset == null || model == null || model.transform.childCount == 0 || !WeaponAnalyzer.IsCached(prefab, entry))
            {
                return -1;
            }
            int[] tpPath = WeaponRig.PathOf(prefab.transform, tpAsset);
            WeaponRigData d = WeaponAnalyzer.Get(prefab, entry);
            if (tpPath == null || d.MagazinePath == null)
            {
                return -1;
            }
            var paths = new List<int[]> { d.MagazinePath };
            paths.AddRange(d.MagazinePartPaths);
            return HideInWorldModel(model.transform.GetChild(0), tpPath, paths); // (the template's third-person model)
        }

        /// <summary>
        /// Hides the parts of a weapon's analysis (paths in its first-person prefab) in a copy of its third-person model, where the
        /// two share them (many mods' third-person model is the first-person gun itself): scaled to nothing and their meshes off
        /// (at 1e-4 the vertices still sit at the pivot, and a dropped weapon's shape is fitted from the enabled renderers, so a
        /// hidden part would leave an invisible collider beside the gun; Task 268). A part outside the third-person model is never
        /// drawn in a copy. Returns how many were hidden.
        /// </summary>
        private static int HideInWorldModel(Transform model, int[] tpPath, IEnumerable<int[]> paths)
        {
            Vector3 tiny = Vector3.one * 1e-4f;
            int hidden = 0;
            foreach (int[] p in paths)
            {
                if (p == null || p.Length <= tpPath.Length)
                {
                    continue;
                }
                bool inside = true;
                for (int i = 0; i < tpPath.Length && inside; i++)
                {
                    inside = p[i] == tpPath[i];
                }
                if (!inside)
                {
                    continue;
                }
                var rest = new int[p.Length - tpPath.Length];
                Array.Copy(p, tpPath.Length, rest, 0, rest.Length);
                Transform part = WeaponRig.Resolve(model, rest);
                if (part != null)
                {
                    part.localScale = tiny;
                    foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                    {
                        r.enabled = false;
                    }
                    hidden++;
                }
            }
            return hidden;
        }

        /// <summary>
        /// A weapon put on the ground with its magazine out (swapped for one picked up) shows none: the copy is of the prefab, which
        /// has its magazine in, and it lay there with one and came back without (Tasks 289). Only from an analysis already made
        /// (the weapon was held: it is), so the swap's frame never runs one; the drop's shape keeps the magazine (fitted per type).
        /// </summary>
        internal static void HideMagazine(DroppedWeapon drop)
        {
            if (drop == null || drop.Go == null || drop.Go.transform.childCount == 0)
            {
                return;
            }
            int hidden = HideMagazineOnModel(drop.Go.transform.GetChild(0).gameObject, drop.Entry); // (the drop's copy of the template)
            if (hidden < 0)
            {
                return;
            }
            Log.InfoOnce("dropmagout:" + drop.Entry.name, $"{drop.Entry.name} put down with its magazine out: "
                                                       + (hidden > 0 ? $"{hidden} magazine part(s) hidden on the ground copy." : "the magazine is not in its third-person copy (a model of its own): left as it is."));
        }

        /// <summary>The weapon dropped last (self-test).</summary>
        internal DroppedWeapon Newest => _drops.Count > 0 ? _drops[_drops.Count - 1] : null;

        /// <summary>The last <see cref="Spawn"/>'s parts in ms (the death frame's log line; task 309).</summary>
        private static float _lastModelMs, _lastShapeMs, _lastColliderMs, _lastGroundMs, _lastIgnoreMs, _lastCullMs;
        private static bool _lastLateFit;

        /// <summary>The prefab's analysis when the last template build ran it (part of the model's ms; -1: not run).</summary>
        private static float _lastAnalysisMs = -1f;

        /// <summary>
        /// Let go with the hand under the ground, or a dying soldier's weapon in it: it fell through the world (terrain is a surface,
        /// not a solid). Put back on the surface between it and <paramref name="open"/> (the player's eyes, the soldier's chest).
        /// </summary>
        private static void OutOfTheGround(Transform root, List<Collider> pieces, Vector3? open)
        {
            // The floor test from its pivot alone (the hand's hold): the physics sync for the pieces' bounds only when there is no
            // floor under it. On a death frame the ragdoll has just moved many transforms, and syncing them all on every drop
            // cost about 0.14 ms a drop (autotest, 2026-10-21).
            if (open == null || pieces.Count == 0 || !PropPhysics.NothingUnder(root.position, pieces))
            {
                return;
            }
            Physics.SyncTransforms(); // the pieces were added this frame: their bounds are read from the transforms
            PropPhysics.BackToward(root, pieces, open.Value, PropPhysics.BoundsOf(pieces), 0.8f);
        }

        // keep: never culled (your own weapon, swapped for one picked up), set before the cull this ends with: with MaxDroppedWeapons
        // at 0 that deleted the swapped rifle, rounds and all, inside this call (drops hunt, 2026-10-20).
        public DroppedWeapon Spawn(WeaponManager.WeaponEntry entry, RigidPose pose, Vector3 scale, Vector3 velocity, int ammo, int spare, bool keep = false,
                                   Vector3? open = null)
        {
            var parts = System.Diagnostics.Stopwatch.StartNew();
            _lastAnalysisMs = -1f;
            GameObject model = ModelFor(entry);
            _lastModelMs = (float)parts.Elapsed.TotalMilliseconds;
            if (model == null)
            {
                return null;
            }
            var root = new GameObject("RavenfieldVR Dropped " + entry.name);
            DroppedWeapon drop;
            try
            {
                int layer = PropPhysics.Layer;
                root.layer = layer >= 0 ? layer : 0; // bullets pass through a weapon on the ground
                model.transform.SetParent(root.transform, false);
                model.transform.localPosition = Vector3.zero;
                model.transform.localRotation = Quaternion.identity;
                model.transform.localScale = scale.sqrMagnitude > 1e-6f ? scale : Vector3.one;
                pose.ApplyTo(root.transform);

                parts.Reset();
                parts.Start();
                ShapeFit.Piece[] shape = FitModel(entry, root.transform, model.transform.localScale, out string lateKey);
                _lastShapeMs = (float)parts.Elapsed.TotalMilliseconds;
                _lastLateFit = lateKey != null;
                parts.Reset();
                parts.Start();
                List<Collider> boxes = ShapeFit.AddColliders(root, shape);
                _lastColliderMs = (float)parts.Elapsed.TotalMilliseconds;
                root.AddComponent<LandingSound>();
                var body = root.AddComponent<Rigidbody>();
                body.mass = 3f;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                RestingBodies.Add(body); // (no interpolation while it lies still)
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                body.velocity = velocity;
                body.angularVelocity = UnityEngine.Random.insideUnitSphere * 2f;
                SetUpBody(body, boxes);
                parts.Reset();
                parts.Start();
                PropPhysics.IgnorePlayer(boxes);
                _lastIgnoreMs = (float)parts.Elapsed.TotalMilliseconds;
                parts.Reset();
                parts.Start();
                OutOfTheGround(root.transform, boxes, open);
                _lastGroundMs = (float)parts.Elapsed.TotalMilliseconds;

                drop = new DroppedWeapon { Go = root, Colliders = boxes, Entry = entry, Ammo = ammo, Spare = spare, DropTime = Time.time, Keep = keep };
                drop.Reach = ReachOf(root.transform, boxes);
                // The dying soldier's ragdoll and hitboxes are around the weapon right now: ignored before the first physics
                // step, or that step pushes the weapon out of them and it skids or floats away.
                parts.Reset();
                parts.Start();
                PropPhysics.IgnoreBodiesNear(boxes, root.transform.position, 1.5f, drop.IgnoredBodies);
                _lastIgnoreMs += (float)parts.Elapsed.TotalMilliseconds;
                if (lateKey != null)
                {
                    drop.LateFitKey = lateKey;
                    if (LateFitsUnderway.Add(lateKey))
                    {
                        LateGather.Enqueue(drop);
                    }
                }
                _drops.Add(drop);
            }
            catch
            {
                // Half made (a shape fit, a collider or the body threw; the death patch's guard logs it): not left lying at the
                // death spot for the rest of the match, untracked and never culled (bug hunt, 2026-10-23).
                UnityEngine.Object.Destroy(root);
                throw;
            }
            parts.Reset();
            parts.Start();
            Cull();
            _lastCullMs = (float)parts.Elapsed.TotalMilliseconds;
            return drop;
        }

        /// <summary>The weapon's third-person model, copied from the prefab without running any of its scripts.</summary>
        private static GameObject BuildWorldModel(WeaponManager.WeaponEntry entry)
        {
            // Only the third-person model is needed: copy that part of the prefab, not the whole weapon (first-person
            // rig, arms, animator, sounds), which cost tens of milliseconds the first time a bot dropped a weapon type.
            GameObject fast = BuildWorldModelFromSubtree(entry);
            return fast != null ? fast : BuildWorldModelWhole(entry);
        }

        /// <summary>Self-test: how long copying the third-person model takes, from its part of the prefab or from the whole prefab.</summary>
        internal static float TimeModelCopy(WeaponManager.WeaponEntry entry, bool partOnly)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            GameObject copy = partOnly ? BuildWorldModelFromSubtree(entry) : BuildWorldModelWhole(entry);
            float ms = (float)watch.Elapsed.TotalMilliseconds;
            if (copy == null)
            {
                return -1f;
            }
            UnityEngine.Object.Destroy(copy);
            return ms;
        }

        private static GameObject BuildWorldModelWhole(WeaponManager.WeaponEntry entry)
        {
            var holder = new GameObject("RavenfieldVR Drop Build");
            holder.SetActive(false);
            try
            {
                GameObject clone = UnityEngine.Object.Instantiate(entry.prefab, holder.transform, false);
                Weapon w = clone.GetComponent<Weapon>();
                Transform tp = w != null ? w.thirdPersonTransform : null;
                if (tp == null || !tp.IsChildOf(clone.transform))
                {
                    return null;
                }
                Vector3 tpEuler = w.thirdPersonRotation;
                Vector3 tpOffset = w.thirdPersonOffset;
                float tpScale = w.thirdPersonScale;
                // The game's own arms are never part of a copy, even when the third-person model holds them (Task 267, 2026-10-18).
                // The arms that showed beside the second weapon stay out of a copy. The renderer alone, as the analysis takes it.
                SkinnedMeshRenderer ownArms = w.arms;

                MonoBehaviour[] scripts = clone.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = scripts.Length - 1; i >= 0; i--)
                {
                    UnityEngine.Object.DestroyImmediate(scripts[i]);
                }
                foreach (AudioSource a in tp.GetComponentsInChildren<AudioSource>(true))
                {
                    a.playOnAwake = false;
                    a.enabled = false;
                }
                foreach (Camera c in tp.GetComponentsInChildren<Camera>(true))
                {
                    c.enabled = false;
                }
                foreach (Light l in tp.GetComponentsInChildren<Light>(true))
                {
                    l.enabled = false;
                }
                foreach (ParticleSystem p in tp.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystem.MainModule main = p.main;
                    main.playOnAwake = false;
                    p.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                foreach (Collider c in tp.GetComponentsInChildren<Collider>(true))
                {
                    c.enabled = false;
                }
                foreach (Rigidbody r in tp.GetComponentsInChildren<Rigidbody>(true))
                {
                    UnityEngine.Object.DestroyImmediate(r);
                }

                // Same local pose the game gives the third-person model under the weapon root.
                var pivot = new GameObject(entry.name);
                pivot.transform.SetParent(holder.transform, false);
                tp.SetParent(pivot.transform, false);
                tp.localPosition = tpOffset;
                tp.localEulerAngles = tpEuler;
                tp.localScale = Vector3.one * tpScale;
                tp.gameObject.SetActive(true);
                foreach (Renderer r in tp.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = r != ownArms;
                    Util.MaterialFix.Apply(r); // the prefab's materials, without the mod's own runtime shader fixes
                }
                pivot.transform.SetParent(null, false);
                return pivot;
            }
            catch (Exception e)
            {
                Log.WarnOnce("dropbuild:" + entry.name, $"Could not build a dropped model for {entry.name}: {e.Message}");
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(holder);
            }
        }

        /// <summary>
        /// The third-person model copied on its own from the prefab asset (no instance of the whole weapon). Null when it
        /// cannot stand alone (its skinned parts use bones outside it): the whole-prefab copy is used then.
        /// </summary>
        private static GameObject BuildWorldModelFromSubtree(WeaponManager.WeaponEntry entry)
        {
            Weapon w = entry.prefab.GetComponent<Weapon>();
            Transform tpAsset = w != null ? w.thirdPersonTransform : null;
            if (tpAsset == null || !tpAsset.IsChildOf(entry.prefab.transform) || tpAsset == entry.prefab.transform)
            {
                return null;
            }
            SkinnedMeshRenderer armsAsset = w.arms; // the game's own arms: never copied (Task 267)
            foreach (SkinnedMeshRenderer smr in tpAsset.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.rootBone != null && !smr.rootBone.IsChildOf(tpAsset))
                {
                    return null;
                }
                foreach (Transform bone in smr.bones)
                {
                    if (bone != null && !bone.IsChildOf(tpAsset))
                    {
                        return null;
                    }
                }
            }
            var holder = new GameObject("RavenfieldVR Drop Build");
            holder.SetActive(false); // nothing in the copy wakes up
            try
            {
                GameObject copy = UnityEngine.Object.Instantiate(tpAsset.gameObject, holder.transform, false);
                Transform tp = copy.transform;
                // The same arms renderer in the copy: found by its path from the third-person model (null: not part of it).
                Transform armsCopyRoot = armsAsset != null ? WeaponRig.Resolve(tp, WeaponRig.PathOf(tpAsset, armsAsset.transform)) : null;
                SkinnedMeshRenderer armsCopy = armsCopyRoot != null ? armsCopyRoot.GetComponent<SkinnedMeshRenderer>() : null;
                MonoBehaviour[] scripts = copy.GetComponentsInChildren<MonoBehaviour>(true);
                for (int i = scripts.Length - 1; i >= 0; i--)
                {
                    UnityEngine.Object.DestroyImmediate(scripts[i]);
                }
                foreach (AudioSource a in copy.GetComponentsInChildren<AudioSource>(true))
                {
                    a.playOnAwake = false;
                    a.enabled = false;
                }
                foreach (Camera c in copy.GetComponentsInChildren<Camera>(true))
                {
                    c.enabled = false;
                }
                foreach (Light l in copy.GetComponentsInChildren<Light>(true))
                {
                    l.enabled = false;
                }
                foreach (ParticleSystem ps in copy.GetComponentsInChildren<ParticleSystem>(true))
                {
                    ParticleSystem.MainModule main = ps.main;
                    main.playOnAwake = false;
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                }
                foreach (Collider c in copy.GetComponentsInChildren<Collider>(true))
                {
                    c.enabled = false;
                }
                foreach (Rigidbody r in copy.GetComponentsInChildren<Rigidbody>(true))
                {
                    UnityEngine.Object.DestroyImmediate(r);
                }
                // Same local pose the game gives the third-person model under the weapon root.
                var pivot = new GameObject(entry.name);
                pivot.transform.SetParent(holder.transform, false);
                tp.SetParent(pivot.transform, false);
                tp.localPosition = w.thirdPersonOffset;
                tp.localEulerAngles = w.thirdPersonRotation;
                tp.localScale = Vector3.one * w.thirdPersonScale;
                copy.SetActive(true);
                foreach (Renderer r in copy.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = r != armsCopy;
                    Util.MaterialFix.Apply(r);
                }
                pivot.transform.SetParent(null, false);
                return pivot;
            }
            catch (Exception e)
            {
                Log.WarnOnce("dropbuild-fast:" + entry.name, $"Copying only the third-person model of {entry.name} failed ({e.Message}); copying the whole weapon.");
                return null;
            }
            finally
            {
                UnityEngine.Object.Destroy(holder);
            }
        }

        /// <summary>Self-test: what a drop of <paramref name="entry"/> costs once prepared (model copy, cached shape, colliders), in ms.</summary>
        internal static float TimePreparedDrop(WeaponManager.WeaponEntry entry)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            GameObject model = ModelFor(entry);
            if (model == null)
            {
                return -1f;
            }
            var root = new GameObject("RavenfieldVR Drop Timing");
            model.transform.SetParent(root.transform, false);
            ShapeFit.AddColliders(root, FitModel(entry, root.transform, Vector3.one, out _, quick: false));
            float ms = (float)watch.Elapsed.TotalMilliseconds;
            UnityEngine.Object.Destroy(root);
            return ms;
        }

        /// <summary>
        /// Menu warm-up: the world model a fallen soldier drops and its physics shape (hulls cooked), built ahead so a bot
        /// dying with this weapon in a match costs no model copy, no mesh reading and no cooking. False: it failed (the idle
        /// builder then gives the type up: it went back in line and copied the model again every turn).
        /// </summary>
        internal static bool Prepare(WeaponManager.WeaponEntry entry)
        {
            GameObject model = ModelFor(entry);
            if (model == null)
            {
                return false;
            }
            GameObject root = null;
            try
            {
                root = new GameObject("RavenfieldVR Drop Warm-up");
                model.transform.SetParent(root.transform, false);
                ShapeFit.Bake(FitModel(entry, root.transform, Vector3.one, out _, quick: false));
                MeasureRenderedShape(entry, root.transform);
                return true;
            }
            catch (Exception e)
            {
                Log.WarnOnce("dropwarm:" + entry.name, $"Preparing the dropped model of {entry.name} failed: {e.Message}");
                return false;
            }
            finally
            {
                // The root as well as the model on a failure: the root was left behind each time (bug hunt, 2026-10-20 night).
                UnityEngine.Object.Destroy(root != null ? root : model);
            }
        }

        /// <summary>Whether a drop of this type at scale 1 has its shape (fitted or rendered) without fitting on its frame.</summary>
        private static bool HasShape(WeaponManager.WeaponEntry entry) =>
            RenderedShapes.ContainsKey(entry) || ShapeFit.TryCached("drop:" + CacheNameOf(entry) + "@1.00", out _);

        /// <summary>Per weapon type whose third-person meshes cannot be read: its shape fitted to the rendered model (scale 1).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<WeaponManager.WeaponEntry, ShapeFit.Piece[]> RenderedShapes =
            new System.Collections.Concurrent.ConcurrentDictionary<WeaponManager.WeaponEntry, ShapeFit.Piece[]>();
        /// <summary>
        /// The model's shape as boxes in the drop's own space (fitted once per weapon and scale); a single box around its
        /// renderers when nothing could be measured.
        /// </summary>
        private static readonly Dictionary<string, ShapeFit.Piece[]> ScaledShapes = new Dictionary<string, ShapeFit.Piece[]>();
        private static readonly Dictionary<WeaponManager.WeaponEntry, int> EntryNumbers = new Dictionary<WeaponManager.WeaponEntry, int>();
        /// <summary>The last number given (never reset: emptied on a content reload, the table must not give a number twice).</summary>
        private static int _entryNumbers;

        /// <summary>
        /// The name a weapon entry's shape caches are kept under: its display name and its own number. Two entries can share a
        /// display name (the squad's M4A1 and the M4A1 of the analysis key m4), and a shape fitted for one was handed to the other
        /// when the caches were keyed by the name alone (bug hunt 2026-10-19; Task 262 gap 3).
        /// </summary>
        private static string CacheNameOf(WeaponManager.WeaponEntry entry)
        {
            if (!EntryNumbers.TryGetValue(entry, out int number))
            {
                number = ++_entryNumbers;
                EntryNumbers[entry] = number;
            }
            return entry.name + "#" + number;
        }

        /// <summary>
        /// A dropped model whose meshes cannot be read only gets boxes from their bounds: during the warm-up its drawn model
        /// is measured (rendered as depth, the hull fitted on a worker thread) and its drops use that shape from then on.
        /// Only the fitted pieces are kept.
        /// </summary>
        private static void MeasureRenderedShape(WeaponManager.WeaponEntry entry, Transform root)
        {
            if (RenderedShapes.ContainsKey(entry))
            {
                return;
            }
            var renderers = new List<Renderer>();
            bool unreadable = false;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(false))
            {
                if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                {
                    renderers.Add(r);
                    unreadable |= !ShapeFit.Readable(r);
                }
            }
            if (!unreadable || renderers.Count == 0)
            {
                return;
            }
            GunVolume v = GunVolume.Measure("tp:" + CacheNameOf(entry), renderers, root, 5, 0.04f, 12, now: true); // the warm-up's root goes this frame
            if (v == null)
            {
                return;
            }
            // Of the content as it is now: a read-back finishing after a content reload put the old model's hull back into the
            // cleared table, and the reloaded type kept it for the session (the late fits already check this; bug hunt, 2026-10-23).
            int generation = _lateGeneration;
            void Keep(GunVolume done)
            {
                if (generation != System.Threading.Volatile.Read(ref _lateGeneration))
                {
                    return;
                }
                if (done.HullPieces != null && done.HullPieces.Length > 0)
                {
                    RenderedShapes[entry] = done.HullPieces;
                    // Cooked soon on the main thread (this runs on the measuring worker), at scale 1: arriving after the warm-up's own
                    // bake, this shape was cooked on the first drop of the type instead, on a soldier's death frame (task 309).
                    PendingBake.Enqueue(entry);
                }
            }
            // The callback first, then the check: checked first, a read-back finished in between (the synchronous path) called no
            // callback and the shape was never taken (review, 2026-10-20). Taken twice is harmless (the same pieces, one bake).
            v.OnReady = Keep;
            if (v.Ready)
            {
                Keep(v);
            }
        }

        /// <summary>Rendered shapes measured on the worker, waiting to be cooked at scale 1 on the main thread (one a frame).</summary>
        private static readonly System.Collections.Concurrent.ConcurrentQueue<WeaponManager.WeaponEntry> PendingBake =
            new System.Collections.Concurrent.ConcurrentQueue<WeaponManager.WeaponEntry>();

        /// <summary>One measured rendered shape cooked at scale 1 (main thread, from Update).</summary>
        private static void BakePendingShape()
        {
            if (PendingBake.TryDequeue(out WeaponManager.WeaponEntry entry) && entry != null && RenderedShapes.TryGetValue(entry, out ShapeFit.Piece[] pieces))
            {
                ScaledPieces(CacheNameOf(entry), pieces, Vector3.one);
            }
        }

        /// <summary>The rendered-model shape of this type at this scale (the pieces were measured at scale 1).</summary>
        private static ShapeFit.Piece[] ScaledRenderedShape(WeaponManager.WeaponEntry entry, Vector3 scale)
        {
            return RenderedShapes.TryGetValue(entry, out ShapeFit.Piece[] pieces) ? ScaledPieces(CacheNameOf(entry), pieces, scale) : null;
        }

        /// <summary><paramref name="pieces"/> (fitted at scale 1) at <paramref name="scale"/>, made once per name and scale.</summary>
        private static ShapeFit.Piece[] ScaledPieces(string name, ShapeFit.Piece[] pieces, Vector3 scale)
        {
            string key = name + "@" + scale.ToString("F3");
            if (ScaledShapes.TryGetValue(key, out ShapeFit.Piece[] scaled))
            {
                return scaled;
            }
            scaled = new ShapeFit.Piece[pieces.Length];
            for (int i = 0; i < pieces.Length; i++)
            {
                var pts = new Vector3[pieces[i].Points.Length];
                var b = new Bounds();
                for (int j = 0; j < pts.Length; j++)
                {
                    pts[j] = Vector3.Scale(pieces[i].Points[j], scale);
                    if (j == 0)
                    {
                        b = new Bounds(pts[0], Vector3.zero);
                    }
                    else
                    {
                        b.Encapsulate(pts[j]);
                    }
                }
                scaled[i] = new ShapeFit.Piece { Points = pts, Bounds = b };
            }
            ShapeFit.Bake(scaled);
            ScaledShapes[key] = scaled;
            return scaled;
        }

        /// <summary>
        /// Autotest: forgets the shape fitted for this type's unit-scale drops, so its next drop takes the path of a type the warm-up
        /// has not reached (a box, then the shape fitted after the frame; task 309). False when its drops use a rendered shape.
        /// </summary>
        internal static bool ForgetFittedShape(WeaponManager.WeaponEntry entry)
        {
            if (entry == null || RenderedShapes.ContainsKey(entry))
            {
                return false;
            }
            ShapeFit.Forget("drop:" + CacheNameOf(entry) + "@1.00");
            return true;
        }

        /// <summary>Self-test: drops of this type use the shape fitted to the rendered model.</summary>
        internal static bool UsesRenderedShape(WeaponManager.WeaponEntry entry) => RenderedShapes.ContainsKey(entry);

        /// <summary>
        /// Drops the models and shapes kept for the weapon types before a content reload (2026-10-19): the shapes are keyed by
        /// name, the models and the no-model marks by entry, and the reloaded weapons are new. Each is made again when a drop
        /// next asks for it. The copies already dropped are not touched (they are instances, not the templates).
        /// </summary>
        internal static void ClearShapes()
        {
            foreach (GameObject template in Templates.Values)
            {
                if (template != null)
                {
                    UnityEngine.Object.Destroy(template);
                }
            }
            Templates.Clear();
            NoModel.Clear();
            // A type whose preparation threw is tried again with the reloaded content (kept, it was never idle-prepared again
            // for the session and its kills fell back to the box and late fit; bug hunt, 2026-10-23).
            IdleFailed.Clear();
            IdleBuilds.Clear(); // (the old content's entries: queued again from what the soldiers carry now)
            EntryNumbers.Clear(); // (the replaced content's entries; the numbers go on from the last one)
            foreach (ShapeFit.Piece[] pieces in RenderedShapes.Values)
            {
                ShapeFit.Retire(pieces); // (their meshes destroyed once no drop in the world collides with them)
            }
            foreach (ShapeFit.Piece[] pieces in ScaledShapes.Values)
            {
                ShapeFit.Retire(pieces);
            }
            RenderedShapes.Clear();
            ScaledShapes.Clear();
            LateFitsUnderway.Clear();
            LateGather.Clear();
            System.Threading.Interlocked.Increment(ref _lateGeneration); // fits and measurements still on a worker are of the old models
            while (PendingBake.TryDequeue(out _))
            {
                // (shapes of the old models waiting to be cooked)
            }
            _lateBaking = null;
            // The copies still lying on their box get their fit started again: their pending one was just dropped, and they kept
            // the box for good (threads hunt, 2026-10-21).
            if (Instance != null)
            {
                foreach (DroppedWeapon d in Instance._drops)
                {
                    if (d.Go != null && d.LateFitKey != null && LateFitsUnderway.Add(d.LateFitKey))
                    {
                        LateGather.Enqueue(d);
                    }
                }
            }
        }

        /// <summary>
        /// The physics shape of a dropped copy under <paramref name="root"/>. <paramref name="quick"/> (a drop in a match): a shape
        /// never fitted is not fitted now, on the soldier's death frame (reading every vertex of the model and slicing it into
        /// hulls took tens to hundreds of milliseconds on a type the menu's warm-up had not reached: a hitch on kills, user,
        /// 2026-10-19 night, task 309). It gets a box from its drawn bounds, and <paramref name="lateKey"/> names the shape to fit
        /// later on a worker thread (<see cref="UpdateLateFits"/>).
        /// </summary>
        private static ShapeFit.Piece[] FitModel(WeaponManager.WeaponEntry entry, Transform root, Vector3 scale, out string lateKey, bool quick = true)
        {
            lateKey = null;
            ShapeFit.Piece[] rendered = ScaledRenderedShape(entry, scale);
            if (rendered != null)
            {
                return rendered;
            }
            string name = CacheNameOf(entry);
            // All three axes when it is stretched: keyed by x alone, a 0.5/1/0.5 drop took a uniform 0.5 drop's shape (drops hunt,
            // 2026-10-20). A uniform scale keeps its key (the unit one is "@1.00").
            bool uniform = Mathf.Abs(scale.x - scale.y) < 0.005f && Mathf.Abs(scale.x - scale.z) < 0.005f;
            string key = "drop:" + name + "@" + scale.x.ToString("F2") + (uniform ? "" : "," + scale.y.ToString("F2") + "," + scale.z.ToString("F2"));
            if (!ShapeFit.TryCached(key, out _) && ShapeFit.TryCached("drop:" + name + "@1.00", out ShapeFit.Piece[] unit) && unit.Length > 0)
            {
                // The warm-up's unit-scale fit, scaled (the same pieces, fitted again on the death frame for any other scale;
                // 255.P7-2). Scaled for the first time on a death frame, its hulls were cooked there: a box till the late fit.
                if (quick && !ScaledShapes.ContainsKey("fit:" + name + "@" + scale.ToString("F3")))
                {
                    lateKey = key;
                    var quickRenderers = new List<Renderer>(root.GetComponentsInChildren<Renderer>());
                    quickRenderers.RemoveAll(r => !r.enabled);
                    Bounds sb = LocalRendererBounds(root, quickRenderers);
                    ShapeFit.Piece scaledBox = ShapeFit.BoxPiece(sb.center, Vector3.Max(sb.size, new Vector3(0.05f, 0.05f, 0.05f)));
                    scaledBox.CookFailed = true; // a plain BoxCollider meanwhile
                    return new[] { scaledBox };
                }
                return ScaledPieces("fit:" + name, unit, scale);
            }
            // What is drawn only: the parts the template switched off (a beam, a part parked away) get no collider (task 303).
            var renderers = new List<Renderer>(root.GetComponentsInChildren<Renderer>());
            renderers.RemoveAll(r => !r.enabled);
            if (quick && !ShapeFit.TryCached(key, out _))
            {
                lateKey = key;
                Bounds qb = LocalRendererBounds(root, renderers);
                ShapeFit.Piece box = ShapeFit.BoxPiece(qb.center, Vector3.Max(qb.size, new Vector3(0.05f, 0.05f, 0.05f)));
                box.CookFailed = true; // a plain BoxCollider: no hull mesh made (and left behind) or cooked for a stand-in this short-lived
                return new[] { box };
            }
            ShapeFit.Piece[] boxes = ShapeFit.Fit(key, renderers, root.worldToLocalMatrix, 0.04f, 12);
            if (boxes.Length > 0)
            {
                return boxes;
            }
            Bounds b = LocalRendererBounds(root, renderers);
            return new[] { ShapeFit.BoxPiece(b.center, Vector3.Max(b.size, new Vector3(0.05f, 0.05f, 0.05f))) };
        }

        /// <summary>A dropped model's shape fitted after its death frame: points read on the main thread, hulls fitted on a worker.</summary>
        private sealed class LateFit
        {
            public string Key;
            public List<Vector3> Points;
            public ShapeFit.Piece[] Pieces;
            public int Baked;
            /// <summary>The <see cref="_lateGeneration"/> it was started in: a fit of a model from before a content reload is dropped.</summary>
            public int Generation;
        }

        private static int _lateGeneration;

        /// <summary>Shape keys being fitted later (one fit per key, however many copies lie on their box meanwhile).</summary>
        private static readonly HashSet<string> LateFitsUnderway = new HashSet<string>();
        private static readonly Queue<DroppedWeapon> LateGather = new Queue<DroppedWeapon>();
        private static readonly System.Collections.Concurrent.ConcurrentQueue<LateFit> LateFitted = new System.Collections.Concurrent.ConcurrentQueue<LateFit>();
        private static LateFit _lateBaking;

        /// <summary>
        /// One step a frame of the shapes left to fit after a death frame: a copy's points read (main thread; its own drawn
        /// renderers, in its root's space, as the full fit reads them), the hulls fitted on a worker, cooked three at a time, then
        /// every copy of that shape still on its box gets them (task 309).
        /// </summary>
        private void UpdateLateFits()
        {
            if (_lateBaking == null && LateFitted.TryDequeue(out LateFit done) && done.Generation == _lateGeneration)
            {
                _lateBaking = done;
            }
            if (_lateBaking != null)
            {
                LateFit fit = _lateBaking;
                // One hull a frame at least, then more while the frame's cooking is under 1 ms (three at most): counted only, a
                // frame with three large hulls paid for three synchronous PhysX cooks (sweep round 2).
                long cookStart = System.Diagnostics.Stopwatch.GetTimestamp();
                long cookBudget = System.Diagnostics.Stopwatch.Frequency / 1000;
                int cooked = 0;
                for (; fit.Baked < fit.Pieces.Length && (cooked == 0 || cooked < 3 && System.Diagnostics.Stopwatch.GetTimestamp() - cookStart < cookBudget);
                     fit.Baked++, cooked++)
                {
                    ShapeFit.Bake(new[] { fit.Pieces[fit.Baked] });
                }
                if (fit.Baked >= fit.Pieces.Length)
                {
                    _lateBaking = null;
                    ShapeFit.Store(fit.Key, fit.Pieces);
                    LateFitsUnderway.Remove(fit.Key);
                    foreach (DroppedWeapon d in _drops)
                    {
                        if (d.LateFitKey != fit.Key)
                        {
                            continue;
                        }
                        if (fit.Pieces.Length > 0)
                        {
                            SwapShape(d, fit.Pieces);
                        }
                        else
                        {
                            d.LateFitKey = null; // nothing to fit: its box is its shape, as a drop of it made from now on gets
                        }
                    }
                }
                return;
            }
            if (LateGather.Count == 0)
            {
                return;
            }
            DroppedWeapon from = LateGather.Dequeue();
            if (from.Go == null || from.LateFitKey == null)
            {
                // Gone before its points were read: the next copy of the type lying on its box starts the fit again.
                if (from.LateFitKey != null)
                {
                    LateFitsUnderway.Remove(from.LateFitKey);
                    DroppedWeapon other = _drops.Find(d => d.Go != null && d.LateFitKey == from.LateFitKey);
                    if (other != null && LateFitsUnderway.Add(other.LateFitKey))
                    {
                        LateGather.Enqueue(other);
                    }
                }
                return;
            }
            var renderers = new List<Renderer>(from.Go.GetComponentsInChildren<Renderer>());
            renderers.RemoveAll(r => !r.enabled);
            List<Vector3> points;
            try
            {
                points = ShapeFit.Gather(renderers, from.Go.transform.worldToLocalMatrix);
            }
            catch (Exception)
            {
                points = new List<Vector3>(); // nothing to fit: it keeps its box
            }
            var late = new LateFit { Key = from.LateFitKey, Points = points, Generation = _lateGeneration };
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    late.Pieces = ShapeFit.FitCloud(late.Points, 0.04f, 12);
                }
                catch (Exception)
                {
                    late.Pieces = new ShapeFit.Piece[0]; // it keeps its box, as a model with nothing to fit does
                }
                late.Points = null;
                LateFitted.Enqueue(late);
            });
        }

        /// <summary>A copy lying on its box gets its fitted shape: the new colliders, the drop's material, and the bodies and the player ignored again.</summary>
        private static void SwapShape(DroppedWeapon d, ShapeFit.Piece[] pieces)
        {
            d.LateFitKey = null;
            if (d.Go == null)
            {
                return;
            }
            List<Collider> fitted = ShapeFit.AddColliders(d.Go, pieces);
            if (d.Colliders != null)
            {
                foreach (Collider c in d.Colliders)
                {
                    if (c != null)
                    {
                        UnityEngine.Object.Destroy(c);
                    }
                }
            }
            d.Colliders = fitted;
            if (d.Body != null)
            {
                SetUpBody(d.Body, fitted);
                d.Body.WakeUp(); // it settles onto its own shape instead of hovering on the box's
            }
            PropPhysics.IgnorePlayer(fitted);
            foreach (Collider c in fitted)
            {
                // The held gun's stand-in had it marked as resting (ignored by its rigidbody): the new colliders are not ignored
                // by Unity yet, so that mark is forgotten and it is judged again (a gun touching it shoved it otherwise).
                GunCollider.Current?.OnPropDropped(c);
            }
            d.IgnoredBodies.Clear();
            PropPhysics.IgnoreBodiesNear(fitted, d.Go.transform.position, 1.5f, d.IgnoredBodies);
            d.Reach = ReachOf(d.Go.transform, fitted);
        }

        private static Bounds LocalRendererBounds(Transform root, List<Renderer> renderers)
        {
            bool any = false;
            var b = new Bounds(Vector3.zero, Vector3.zero);
            Matrix4x4 toLocal = root.worldToLocalMatrix;
            foreach (Renderer r in renderers)
            {
                if (r is ParticleSystemRenderer)
                {
                    continue;
                }
                Bounds wb = r.bounds;
                Vector3 c = wb.center, e = wb.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 p = toLocal.MultiplyPoint3x4(c + Vector3.Scale(e, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1)));
                    if (!any)
                    {
                        b = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else
                    {
                        b.Encapsulate(p);
                    }
                }
            }
            return b;
        }
    }
}
