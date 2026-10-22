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
    /// <summary>A weapon lying in the world that can be picked up.</summary>
    public sealed class DroppedWeapon
    {
        public GameObject Go;
        private Rigidbody _body;
        /// <summary>Its rigidbody (looked up once).</summary>
        public Rigidbody Body => _body != null ? _body : Go != null ? _body = Go.GetComponent<Rigidbody>() : null;
        /// <summary>The model's own shape (convex pieces: receiver, barrel, stock...), so it lies flat and is grabbed where it is.</summary>
        public List<Collider> Colliders;
        public WeaponManager.WeaponEntry Entry;
        /// <summary>Rounds loaded (-1 = default) and in reserve (-1 = default) for whoever picks it up.</summary>
        public int Ammo = -1;
        public int Spare = -1;
        /// <summary>
        /// The player's own weapon put down in a swap (or whose holster slot was taken): the only copy of it, not culled by
        /// lifetime. Past six of them the oldest becomes an ordinary drop with a fresh lifetime (Cull).
        /// </summary>
        public bool Keep;
        /// <summary>
        /// The player's own weapon (put down in a swap, or let go of): its rounds come back to it even when no live owner is left
        /// (a loose magazine of a swapped-away rifle lost its rounds when the rifle was skipped here; 2026-10-19).
        /// </summary>
        public bool PlayerOwned;
        /// <summary>Put down with its magazine out: it comes back without one (the rounds in that magazine are the magazine's).</summary>
        public bool MagOut;
        /// <summary>Put down locked back on nothing chambered: it comes back caught back, with its magazine's rounds loaded.</summary>
        public bool SlideBack;
        /// <summary>
        /// Put down with nothing chambered and its action closed (a pump or bolt gun fired and not worked again): it comes back
        /// still to be worked. The count alone made it "N+1" and its next trigger pull fired with no stroke (reload hunt, 2026-10-20).
        /// </summary>
        public bool EmptyChamber;
        /// <summary>Radius from the root that holds every collider (rotation-invariant), so a far drop is skipped whole.</summary>
        public float Reach;
        /// <summary>Reach measured again after its first physics step (the colliders' boxes may not be set the frame they are added).</summary>
        public bool ReachChecked;
        /// <summary>Rounds loaded in each under-barrel weapon (a launcher's grenade), by index; null = their default.</summary>
        public int[] SubAmmo;
        /// <summary>The key the weapon's rig is analysed under (magazines name it), when known.</summary>
        public string Key;
        public float DropTime;
        /// <summary>
        /// The player's own weapon, let go of away from the body: the loadout slot it came from (-1 for a bot's weapon). It
        /// goes back to that holster after a while or when you walk away from it.
        /// </summary>
        public int OwnerSlot = -1;
        public float ReturnAt;
        /// <summary>The player had set it to semi (FireSelector): it comes back in semi.</summary>
        public bool Semi;
        /// <summary>Meshes baked for this copy (skinned parts), destroyed with it.</summary>
        public List<Mesh> OwnedMeshes;
        /// <summary>Body colliders (ragdolls, soldiers' hitboxes) this weapon no longer collides with.</summary>
        public readonly HashSet<Collider> IgnoredBodies = new HashSet<Collider>();
        public float NextBodyCheck;
        /// <summary>When <see cref="IgnoredBodies"/> was last forgotten (bodies switched off and on collide again: redone).</summary>
        public float BodiesForgottenAt;
        /// <summary>The <see cref="PropPhysics.PlayerGeneration"/> the player's colliders were last ignored for.</summary>
        public int PlayerGeneration = -1;
        /// <summary>
        /// The shape key of a model whose shape was never fitted: it lies on a box from its drawn bounds until the fit, made on a
        /// worker thread after the death frame, replaces it (null once it has its own shape; task 309).
        /// </summary>
        public string LateFitKey;
    }

    /// <summary>
    /// Fallen soldiers drop their weapon as a physical third-person model. Gripping a dropped weapon equips it
    /// into its loadout slot; the weapon you had in that slot is dropped in its place (with its ammo), so you can
    /// swap back. Models are inert copies of the weapon prefab (no mod scripts run). Drops expire and are capped.
    /// </summary>
    public sealed class WeaponDrops : IDisposable
    {
        public static WeaponDrops Instance { get; private set; }

        private const float PickupReach = 0.11f;
        private readonly List<DroppedWeapon> _drops = new List<DroppedWeapon>();
        private readonly WeaponSystem _sys;
        private readonly bool[] _hovering = new bool[2];

        public WeaponDrops(WeaponSystem sys)
        {
            Instance = this;
            _sys = sys;
        }

        public int Count => _drops.Count;

        /// <summary>Every weapon lying in the world now.</summary>
        internal IReadOnlyList<DroppedWeapon> All => _drops;
        private readonly VRHand[] _handPair = new VRHand[2];

        // ---- spawning ---------------------------------------------------------------------------------

        /// <summary>Called just before a soldier dies (its weapons are destroyed right after).</summary>
        public void OnActorDying(Actor actor, DamageInfo info)
        {
            if (!VRConfig.EnemyWeaponDrops.Value || actor == null || !actor.aiControlled)
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
            if (watch.Elapsed.TotalMilliseconds > 30.0)
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
            Vector3 tiny = Vector3.one * 1e-4f;
            int hidden = 0;
            foreach (int[] p in spare)
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
                    continue; // not in the third-person model, so never drawn in a copy
                }
                var rest = new int[p.Length - tpPath.Length];
                Array.Copy(p, tpPath.Length, rest, 0, rest.Length);
                Transform part = WeaponRig.Resolve(model, rest);
                if (part != null)
                {
                    part.localScale = tiny;
                    // Its meshes are switched off as well: at 1e-4 they still have their vertices at the pivot, where the part
                    // waits, and the dropped weapon's physics shape is fitted from the enabled renderers only (ShapeFit.AddPoints),
                    // so a hidden part would leave an invisible collider out beside the gun (Task 268).
                    foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                    {
                        r.enabled = false;
                    }
                    hidden++;
                }
            }
            if (hidden > 0)
            {
                Log.InfoOnce("parkedcopy:" + entry.name, $"Copies of {entry.name} (holstered and dropped): {hidden} parked ammunition part(s) hidden, as in the hand.");
            }
        }

        /// <summary>The weapon dropped last (self-test).</summary>
        internal DroppedWeapon Newest => _drops.Count > 0 ? _drops[_drops.Count - 1] : null;

        /// <summary>The last <see cref="Spawn"/>'s parts in ms (the death frame's log line; task 309).</summary>
        private static float _lastModelMs, _lastShapeMs, _lastColliderMs, _lastGroundMs, _lastIgnoreMs, _lastCullMs;
        private static bool _lastLateFit;

        /// <summary>The prefab's analysis when the last template build ran it (part of the model's ms; -1: not run).</summary>
        private static float _lastAnalysisMs = -1f;

        // keep: never culled (your own weapon, swapped for one picked up), set before the cull this ends with: with MaxDroppedWeapons
        // at 0 that deleted the swapped rifle, rounds and all, inside this call (drops hunt, 2026-10-20).
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
            var body = root.AddComponent<Rigidbody>();
            body.mass = 3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
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

            var drop = new DroppedWeapon { Go = root, Colliders = boxes, Entry = entry, Ammo = ammo, Spare = spare, DropTime = Time.time, Keep = keep };
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

        // False: it failed (the idle builder then gives the type up: it went back in line and copied the model again every turn).
        /// <summary>
        /// Menu warm-up: the world model a fallen soldier drops and its physics shape (hulls cooked), built ahead so a bot
        /// dying with this weapon in a match costs no model copy, no mesh reading and no cooking.
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

        /// <summary>
        /// The name a weapon entry's shape caches are kept under: its display name and its own number. Two entries can share a
        /// display name (the squad's M4A1 and the M4A1 of the analysis key m4), and a shape fitted for one was handed to the other
        /// when the caches were keyed by the name alone (bug hunt 2026-10-19; Task 262 gap 3).
        /// </summary>
        private static string CacheNameOf(WeaponManager.WeaponEntry entry)
        {
            if (!EntryNumbers.TryGetValue(entry, out int number))
            {
                number = EntryNumbers.Count + 1;
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
            void Keep(GunVolume done)
            {
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
            RenderedShapes.Clear();
            ScaledShapes.Clear();
            LateFitsUnderway.Clear();
            LateGather.Clear();
            _lateGeneration++; // fits still on a worker are of the old models
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
                int cooked = 0;
                for (; fit.Baked < fit.Pieces.Length && cooked < 3; fit.Baked++, cooked++)
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

        // ---- picking up -------------------------------------------------------------------------------

        public void Update()
        {
            Log.Guard("idle drop builds", BuildWhileIdle);
            ParkedPartsCache.WriteIfDue();
            BakePendingShape();
            UpdateLateFits();
            Cull();
            PropPhysics.TrackPlayer();
            IgnoreBodies();
            Actor actor = GameContext.PlayerActor;
            bool dead = actor != null && actor.dead;
            if (dead && !_playerWasDead)
            {
                LetGoOfOwnDrops();
            }
            _playerWasDead = dead;
            if (actor == null || !GameContext.PlayerOnFoot || GameContext.GameMenuOpen || _drops.Count == 0)
            {
                return;
            }
            ReturnOwnDrops(actor);
            if (_drops.Count == 0)
            {
                return;
            }
            VRRig rig = _sys.Rig;
            _handPair[0] = rig.Dominant;
            _handPair[1] = rig.Off;
            foreach (VRHand hand in _handPair)
            {
                if (!hand.Tracked)
                {
                    continue;
                }
                bool canGrab = hand.Role == HandRole.Free || hand == rig.Dominant && (hand.Role == HandRole.Weapon || hand.Role == HandRole.Throwable);
                DroppedWeapon near = canGrab ? Nearest(hand.Palm.position) : null;
                int i = (int)hand.Side;
                if (near != null && !_hovering[i])
                {
                    hand.Haptic(0.01f, 160f, 0.2f); // "you can grab this"
                }
                _hovering[i] = near != null;
                if (near != null && (hand.GrabPressed || hand.GrabReaching))
                {
                    VRInput.Consume(DigitalAction.Grab, hand.Side);
                    PickUp(near, hand, actor);
                    return;
                }
            }
        }


        /// <summary>
        /// A weapon on the ground does not collide with bodies: one spawned in a dying soldier's hand was shoved out of
        /// the ragdoll (or carried along by it) and soldiers walking past kicked lying weapons away, so they drifted off.
        /// Bodies near a drop are found every frame for its first half second, then four times a second.
        /// </summary>
        private void IgnoreBodies()
        {
            float now = Time.time;
            foreach (DroppedWeapon d in _drops)
            {
                if (d.Go == null || d.Colliders == null || now < d.NextBodyCheck)
                {
                    continue;
                }
                d.NextBodyCheck = now - d.DropTime < 0.5f ? now : now + 0.25f;
                // A ragdoll or a soldier whose colliders were switched off and on collides again (Unity forgets ignored
                // pairs then): every two seconds the bodies around are ignored afresh.
                if (now - d.BodiesForgottenAt > 2f)
                {
                    d.BodiesForgottenAt = now;
                    d.IgnoredBodies.Clear();
                }
                PropPhysics.IgnoreBodiesNear(d.Colliders, d.Go.transform.position, 1.5f, d.IgnoredBodies);
                if (d.PlayerGeneration != PropPhysics.PlayerGeneration)
                {
                    d.PlayerGeneration = PropPhysics.PlayerGeneration;
                    PropPhysics.IgnorePlayer(d.Colliders);
                }
            }
        }

        private static PhysicMaterial _dropMaterial;

        /// <summary>Weapons lie where they fall: grippy, barely bouncing.</summary>
        private static void SetUpBody(Rigidbody body, List<Collider> colliders)
        {
            if (_dropMaterial == null)
            {
                _dropMaterial = new PhysicMaterial("RavenfieldVR Dropped Weapon")
                {
                    staticFriction = 0.9f,
                    dynamicFriction = 0.7f,
                    bounciness = 0.05f,
                    frictionCombine = PhysicMaterialCombine.Maximum,
                    bounceCombine = PhysicMaterialCombine.Minimum
                };
            }
            foreach (Collider c in colliders)
            {
                if (c != null)
                {
                    c.sharedMaterial = _dropMaterial;
                }
            }
            body.angularDrag = 0.5f;
        }

        /// <summary>
        /// The rounds in a weapon as it is put down: with its slide locked open the game is told none are loaded (so a fresh
        /// magazine does not run the slide forward), but the magazine in it still holds its rounds. Taken from the game's
        /// count alone, a gun dropped locked open with a fresh magazine came back empty (255.B4-1).
        /// </summary>
        private static int RoundsIn(Weapon w)
        {
            if (w == null)
            {
                return 0;
            }
            AmmoState st = WeaponSystem.Instance != null ? WeaponSystem.Instance.StateFor(w, create: false) : null;
            if (st != null && st.Participates && st.SlideLocked && !st.Chambered)
            {
                return st.MagInserted ? st.MagRounds : 0;
            }
            return Mathf.Max(0, w.ammo);
        }

        /// <summary>
        /// Whether a weapon is put down with its magazine out, and with its slide locked back on nothing chambered (both false
        /// without a rig state, as for a weapon nothing of the mod's has handled).
        /// </summary>
        private static void PutDownState(Weapon w, out bool magOut, out bool slideBack)
        {
            AmmoState st = WeaponSystem.Instance != null ? WeaponSystem.Instance.StateFor(w, create: false) : null;
            magOut = st != null && st.UsesDetachableMag && !st.MagInserted;
            slideBack = st != null && st.Participates && st.SlideLocked && !st.Chambered;
        }

        /// <summary>Nothing chambered, the action closed and not locked back, rounds left to feed: the next round needs a stroke.</summary>
        private static bool EmptyChamberOf(Weapon w)
        {
            AmmoState st = w != null && WeaponSystem.Instance != null ? WeaponSystem.Instance.StateFor(w, create: false) : null;
            return st != null && st.Participates && st.UsesChamber && st.Rig.Action != ActionType.SingleShot && !st.Chambered && !st.SlideLocked
                   && st.MagRounds > 0 && (st.MagInserted || !st.UsesDetachableMag);
        }

        /// <summary>What each under-barrel weapon has loaded (null without any).</summary>
        private static int[] SubAmmoOf(Weapon w)
        {
            if (w == null || w.alternativeWeapons == null || w.alternativeWeapons.Count == 0)
            {
                return null;
            }
            var ammo = new int[w.alternativeWeapons.Count];
            for (int i = 0; i < ammo.Length; i++)
            {
                Weapon sub = w.alternativeWeapons[i];
                ammo[i] = sub != null ? RoundsIn(sub) : -1;
            }
            return ammo;
        }

        /// <summary>
        /// The rounds a dropped weapon had, back in the weapon made from it: loaded (a chambered round on top of a full
        /// magazine included: clamped to the magazine, a 30+1 rifle came back 29+1 after every drop), the reserve, and
        /// what each under-barrel weapon had loaded (a launcher came back with its default grenade).
        /// </summary>
        private static void RestoreAmmo(Weapon w, DroppedWeapon d)
        {
            if (d.Semi)
            {
                FireSelector.RestoreSemi(w); // picked up or back in its holster: still in semi, as the player left it
            }
            if (d.Ammo >= 0 && w.configuration.ammo > 0)
            {
                int most = w.configuration.ammo + (VRConfig.ChamberPlusOne.Value ? 1 : 0);
                w.ammo = Mathf.Clamp(d.Ammo, 0, most);
            }
            if (d.Spare >= 0 && w.configuration.spareAmmo >= 0)
            {
                w.spareAmmo = d.Spare;
            }
            if (d.SubAmmo != null && w.alternativeWeapons != null)
            {
                for (int i = 0; i < d.SubAmmo.Length && i < w.alternativeWeapons.Count; i++)
                {
                    Weapon sub = w.alternativeWeapons[i];
                    if (sub != null && d.SubAmmo[i] >= 0 && sub.configuration.ammo > 0)
                    {
                        sub.ammo = Mathf.Clamp(d.SubAmmo[i], 0, sub.configuration.ammo + (VRConfig.ChamberPlusOne.Value ? 1 : 0));
                        sub.OnAmmoChanged();
                    }
                }
            }
            w.OnAmmoChanged();
            // Its rig state (chamber, magazine) is taken from the game's count again. A gun put down with its magazine out or
            // locked back gets its state made here and set as it was put down: AdoptWeaponAmmo alone reads a magazine in and a
            // round chambered from the count, so a magazine-out pistol came back with its magazine seated (a fresh one was
            // refused) and a locked-back gun came back with its slide free and a round chambered (30 came back 29+1).
            AmmoState state = WeaponSystem.Instance?.StateFor(w, create: d.MagOut || d.SlideBack || d.EmptyChamber);
            state?.AdoptWeaponAmmo();
            if (state != null && d.EmptyChamber && !d.MagOut && !d.SlideBack)
            {
                // Every round back in the magazine or tube, the chamber empty: it is worked before it fires, as it was put down.
                state.Chambered = false;
                state.MagRounds = Mathf.Clamp(d.Ammo, 0, state.Capacity);
                state.Sync();
            }
            if (state != null && (d.MagOut || d.SlideBack))
            {
                state.Chambered = !d.SlideBack && d.Ammo > 0;
                state.MagInserted = !d.MagOut;
                state.MagRounds = d.SlideBack && !d.MagOut ? Mathf.Clamp(d.Ammo, 0, state.Capacity) : 0;
                if (d.SlideBack)
                {
                    state.SlideLocked = true;
                }
                state.Sync();
                Log.InfoOnce("putdown:" + d.Entry.name, $"{d.Entry.name} comes back as it was put down: magazine {(d.MagOut ? "out" : "in")}, slide {(d.SlideBack ? "locked back" : "free")}.");
            }
        }

        /// <summary>
        /// Rounds of a magazine whose weapon was let fall: into that drop's reserve (they come back with it). The player's
        /// own drop of that kind, the newest first.
        /// </summary>
        public void ReturnRoundsToOwnDrop(string key, int rounds)
        {
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                DroppedWeapon d = _drops[i];
                // A bot's drop takes no rounds back; the player's own (a swapped-away rifle is Keep with no owner slot) does.
                if (d.Entry == null || d.Spare < 0 || (d.OwnerSlot < 0 && !d.PlayerOwned))
                {
                    continue;
                }
                bool same = d.Key != null ? d.Key == key : key != null && key.StartsWith(d.Entry.name + "#", System.StringComparison.Ordinal);
                if (!same)
                {
                    continue;
                }
                int cap = d.Entry.prefab != null && d.Entry.prefab.GetComponent<Weapon>() is Weapon proto && proto.configuration.spareAmmo > 0
                    ? proto.configuration.spareAmmo
                    : int.MaxValue;
                d.Spare = Mathf.Max(d.Spare, (int)System.Math.Min(cap, (long)d.Spare + rounds));
                return;
            }
        }

        /// <summary>
        /// A radius about the drop's root that holds every one of its colliders. The colliders are rigidly parented to the
        /// root at unit scale, so the distance from the root to a collider's centre does not change as the drop tumbles; the
        /// world-box extent over-bounds the collider in any orientation. Over-estimating only skips less, never a reachable
        /// drop, so <see cref="Nearest"/> stays exact.
        /// </summary>
        private static float ReachOf(Transform root, List<Collider> colliders)
        {
            float reach = 0f;
            if (colliders != null)
            {
                Vector3 origin = root.position;
                foreach (Collider c in colliders)
                {
                    if (c == null)
                    {
                        continue;
                    }
                    float r = (c.bounds.center - origin).magnitude + c.bounds.extents.magnitude;
                    if (r > reach)
                    {
                        reach = r;
                    }
                }
            }
            return reach;
        }

        private DroppedWeapon Nearest(Vector3 palm)
        {
            DroppedWeapon best = null;
            float bestDist = PickupReach;
            foreach (DroppedWeapon d in _drops)
            {
                if (d.Go == null || d.Colliders == null)
                {
                    continue;
                }
                if (!d.ReachChecked && Time.time - d.DropTime > 0.1f)
                {
                    // Measured as it was made, from colliders added that frame: boxes not yet set gave a reach too short, and the drop
                    // was skipped below while the hand was at its far end (drops hunt, 2026-10-20). Once more, after the physics step.
                    d.ReachChecked = true;
                    d.Reach = Mathf.Max(d.Reach, ReachOf(d.Go.transform, d.Colliders));
                }
                // One native read for the whole drop first: its root cannot have a collider within bestDist of the palm when
                // the root is farther than bestDist + its reach (every collider lies within reach of the root).
                if (d.Reach > 0f && (d.Go.transform.position - palm).sqrMagnitude > (bestDist + d.Reach) * (bestDist + d.Reach))
                {
                    continue;
                }
                foreach (Collider c in d.Colliders)
                {
                    // Its box first (cheap): the exact closest point only for a collider within reach (every collider of
                    // every drop, for both hands, every frame).
                    if (c == null || c.bounds.SqrDistance(palm) > bestDist * bestDist)
                    {
                        continue;
                    }
                    float dist = Vector3.Distance(c.ClosestPoint(palm), palm);
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        best = d;
                    }
                }
            }
            return best;
        }

        private void PickUp(DroppedWeapon drop, VRHand hand, Actor actor)
        {
            if (!actor.CanUnholsterWeapon())
            {
                hand.Haptic(0.08f, 40f, 0.4f);
                return;
            }
            int slot = drop.OwnerSlot >= 0 && drop.OwnerSlot < actor.weapons.Length ? drop.OwnerSlot : SlotFor(drop.Entry, actor);
            Weapon current = actor.weapons[slot];
            if (current != null && current.weaponEntry != null && current.weaponEntry.prefab != null)
            {
                // Swap: what you had in that slot goes where the picked-up weapon was. Made first: a weapon without a
                // model to copy cannot be put down, so nothing is picked up (it was replaced and gone, rounds and all).
                // At the player's feet, lying flat across the body, a little to the side: spawned 10 cm over the weapon being
                // picked up, it fell onto it and knocked it about while the hand held it (user, 2026-10-19 night, task 308).
                Vector3 bodyForward = Vector3.ProjectOnPlane(_sys.Rig.Head.forward, Vector3.up);
                bodyForward = bodyForward.sqrMagnitude > 1e-4f ? bodyForward.normalized : Vector3.forward;
                Vector3 bodyRight = Vector3.Cross(Vector3.up, bodyForward);
                Vector3 feet = actor.Position() + Vector3.up * 0.15f + bodyRight * 0.2f + bodyForward * 0.15f;
                var pose = new RigidPose(feet, Quaternion.LookRotation(bodyRight, Vector3.up));
                DroppedWeapon swapped = Spawn(current.weaponEntry, pose, Vector3.one, Vector3.down * 0.3f, RoundsIn(current), Mathf.Max(0, current.spareAmmo), keep: true);
                if (swapped == null)
                {
                    hand.Haptic(0.08f, 40f, 0.4f);
                    return;
                }
                swapped.SubAmmo = SubAmmoOf(current);
                swapped.Keep = true; // your rifle, swapped for a fallen enemy's shotgun, was deleted 90 s later (85-agent review)
                swapped.PlayerOwned = true;
                swapped.Key = WeaponAnalyzer.KeyOf(current);
                swapped.Semi = FireSelector.IsSemiByPlayer(current);
                PutDownState(current, out bool swapMagOut, out bool swapSlideBack);
                swapped.MagOut = swapMagOut;
                swapped.SlideBack = swapSlideBack;
                swapped.EmptyChamber = EmptyChamberOf(current);
            }
            // A weapon picked up by the free hand while the gun is held goes beside it when the two pair (primary, secondary,
            // launcher, grenade; never two of a kind): it is put in its slot holstered, then held in this hand (Tasks 152: "pick
            // up the pistol while the main weapon is held"). Anything else is taken up as before, the gun going to its holster.
            bool beside = hand != _sys.Rig.Dominant && _sys.Second == null && _sys.Held != null && _sys.Held.Weapon != null
                          && slot != actor.activeWeaponSlot;
            if (!beside)
            {
                // Into the hand that picked it up: taken with the free hand, it went into the other one.
                _sys.TakeIntoHand(hand);
            }

            Weapon equipped = actor.EquipNewWeaponEntry(drop.Entry, slot, !beside);
            // The slot follows the gun drawn into it, as the game's own switch sets it: the pairing check and the holster reads
            // compare with it (a stale slot swapped the drawn gun out for the second pickup, 2026-10-19).
            if (equipped != null && actor.activeWeapon == equipped)
            {
                actor.activeWeaponSlot = slot;
            }
            if (equipped == null)
            {
                // The game made nothing (its spawn failed): the drop stays where it lies, not deleted with the rounds in it
                // (drops hunt, 2026-10-20).
                Log.WarnOnce("pickupfailed:" + drop.Entry.name, $"Picking up {drop.Entry.name}: the game spawned nothing; it stays on the ground.");
                return;
            }
            RestoreAmmo(equipped, drop);
            Remove(drop);
            if (beside && SecondWeapon.CanPair(_sys.Held.Weapon, equipped))
            {
                _sys.BeginSecond(equipped, hand); // the holster sound and the haptic come with it
            }
            else
            {
                if (beside)
                {
                    _sys.TakeIntoHand(hand);
                    if (equipped != null)
                    {
                        actor.SwitchWeapon(slot); // as a holstered weapon taken with the free hand (WeaponSystem)
                    }
                }
                _sys.Sounds.Play(MechSound.Holster, hand.Palm.position, 0.8f);
                hand.Haptic(0.05f, 100f, 0.7f);
            }
            Log.Debug($"Picked up {drop.Entry.name} into slot {slot + 1}");
        }

        private static int SlotFor(WeaponManager.WeaponEntry entry, Actor actor)
        {
            switch (entry.slot)
            {
                case WeaponManager.WeaponSlot.Primary: return 0;
                case WeaponManager.WeaponSlot.Secondary: return 1;
                // The launcher's slot is 3 (the loadout's gear2, SecondWeapon.Kind); 4 is the loadout's gear3, a small gun's.
                case WeaponManager.WeaponSlot.LargeGear: return 3;
                default:
                    if (actor.weapons[2] == null) return 2;
                    if (actor.weapons[4] == null) return 4;
                    return 2;
            }
        }

        // ---- lifetime ---------------------------------------------------------------------------------

        private void Cull()
        {
            float lifetime = VRConfig.DroppedWeaponLifetime.Value;
            int max = Mathf.Max(0, VRConfig.MaxDroppedWeapons.Value);
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                DroppedWeapon d = _drops[i];
                if (d.Go == null)
                {
                    Remove(d);
                    continue;
                }
                bool expired = Time.time - d.DropTime > lifetime || d.Go.transform.position.y < -500f;
                if (expired && d.OwnerSlot >= 0)
                {
                    // Your own weapon is the only copy of it (its slot was emptied when it left the hand): it goes back to
                    // its holster, never away. Deleted after 90 s (in a vehicle, where it is not returned) or after falling
                    // out of the world, it was gone from the loadout for good.
                    d.ReturnAt = 0f;
                    continue;
                }
                // A kept weapon that fell out of the world can never be picked up again: it only held a place among the kept ones
                // (pickup hunt, 2026-10-21).
                if (expired && (!d.Keep || d.Go.transform.position.y < -500f))
                {
                    Remove(d);
                }
            }
            // Weapons kept after a swap pile up only so far: past six, the oldest of them stops being kept. It is the only
            // copy of that weapon, so it is not deleted outright (that lost the first swapped-away gun of a long fight on the
            // seventh swap); it becomes an ordinary drop with a fresh lifetime, still grabbable, and goes on the normal timer.
            int kept = 0;
            foreach (DroppedWeapon d in _drops)
            {
                kept += d.Keep ? 1 : 0;
            }
            for (int i = 0; i < _drops.Count && kept > 6; i++)
            {
                if (_drops[i].Keep && _drops[i].OwnerSlot < 0)
                {
                    _drops[i].Keep = false;
                    _drops[i].DropTime = Time.time;
                    kept--;
                }
            }
            // Oldest first when over the cap; your own weapon waiting to go back to its holster is never one of them. The soldiers'
            // drops go before a weapon of yours that stopped being kept: just demoted above, it was the oldest in the list and
            // went in the same call, the only copy of a swapped-away gun lost on the seventh swap (pickup hunt, 2026-10-21).
            int count = 0;
            foreach (DroppedWeapon d in _drops)
            {
                count += d.OwnerSlot < 0 && !d.Keep ? 1 : 0;
            }
            for (int pass = 0; pass < 2 && count > max; pass++)
            {
                for (int i = 0; i < _drops.Count && count > max; i++)
                {
                    DroppedWeapon d = _drops[i];
                    if (d.OwnerSlot < 0 && !d.Keep && (pass == 1 || !d.PlayerOwned))
                    {
                        Remove(d);
                        i--;
                        count--;
                    }
                }
            }
        }

        private void Remove(DroppedWeapon d)
        {
            _drops.Remove(d);
            if (d.Go != null)
            {
                UnityEngine.Object.Destroy(d.Go);
            }
            if (d.OwnedMeshes != null)
            {
                foreach (Mesh m in d.OwnedMeshes)
                {
                    UnityEngine.Object.Destroy(m);
                }
                d.OwnedMeshes = null;
            }
        }

        // ---- your own weapon, let go of ----------------------------------------------------------------

        /// <summary>
        /// The weapon in your hand falls where you let go of it: a copy of exactly what you held (the first-person model,
        /// its magazine if one is in), at the pose you held it, with your hand's velocity and the gun's own shape for
        /// physics. Its rounds stay with it. Null when there was nothing to copy.
        /// </summary>
        public DroppedWeapon DropHeld(HeldWeapon held, int slot, Vector3 velocity, Vector3 angularVelocity)
        {
            WeaponRig rig = held.Rig;
            WeaponRigData d = rig.Data;
            Weapon w = held.Weapon;
            if (w == null || w.weaponEntry == null || rig.GunBody == null)
            {
                return null;
            }
            Quaternion frame = OpenVRMath.SafeLookRotation(d.AimAxis, d.Up);
            var root = new GameObject("RavenfieldVR Dropped " + w.weaponEntry.name);
            new RigidPose(rig.ToWorld(d.GripPoint), rig.GunBody.rotation * frame).ApplyTo(root.transform);
            var baked = new List<Mesh>();
            bool magIn = held.Ammo == null || !held.Ammo.UsesDetachableMag || held.Ammo.MagInserted;
            if (!CopyHeldModel(rig, root.transform, magIn, baked))
            {
                UnityEngine.Object.Destroy(root);
                return null;
            }
            int layer = PropPhysics.Layer;
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer >= 0 ? layer : 0;
            }
            // The held gun's own physics shape, fitted in exactly this frame (the stand-in's current one: when the
            // rendered-model fit replaced the meshes' it is the better one), and the magazine when it shows in the gun.
            GunCollider held_ = GunCollider.Current;
            ShapeFit.Piece[] pieces = held_ != null && held_.Holds(rig) && held_.Pieces != null ? held_.Pieces : GunCollider.Fit(rig, frame, d.GripPoint);
            if (magIn && rig.MagazineParts.Length > 0)
            {
                var magRenderers = new List<Renderer>();
                foreach (Transform part in rig.MagazineParts)
                {
                    if (part != null)
                    {
                        foreach (Renderer r in part.GetComponentsInChildren<Renderer>(false))
                        {
                            if (r.enabled && (r is MeshRenderer || r is SkinnedMeshRenderer))
                            {
                                magRenderers.Add(r);
                            }
                        }
                    }
                }
                Matrix4x4 frameFromWorld = Matrix4x4.TRS(rig.ToWorld(d.GripPoint), rig.GunBody.rotation * frame, Vector3.one).inverse;
                ShapeFit.Piece[] mag = ShapeFit.Fit("heldmag:" + d.Key, magRenderers, frameFromWorld, 0.015f, 8);
                if (mag.Length > 0)
                {
                    var all = new ShapeFit.Piece[pieces.Length + mag.Length];
                    pieces.CopyTo(all, 0);
                    mag.CopyTo(all, pieces.Length);
                    pieces = all;
                }
            }
            List<Collider> colliders = ShapeFit.AddColliders(root, pieces);
            var body = root.AddComponent<Rigidbody>();
            body.mass = 3f;
            body.interpolation = RigidbodyInterpolation.Interpolate;
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            body.velocity = velocity;
            body.angularVelocity = angularVelocity;
            SetUpBody(body, colliders);
            PropPhysics.IgnorePlayer(colliders);
            OutOfTheGround(root.transform, colliders, WeaponSystem.Instance != null && WeaponSystem.Instance.Rig != null && WeaponSystem.Instance.Rig.Head != null
                ? WeaponSystem.Instance.Rig.Head.position : (Vector3?)null);
            PutDownState(w, out bool magOut, out bool slideBack);
            var drop = new DroppedWeapon
            {
                Go = root,
                Colliders = colliders,
                Entry = w.weaponEntry,
                Ammo = RoundsIn(w),
                Spare = Mathf.Max(0, w.spareAmmo),
                SubAmmo = SubAmmoOf(w),
                Key = held.Rig.Data.Key,
                DropTime = Time.time,
                OwnerSlot = slot,
                ReturnAt = Time.time + Mathf.Max(1f, VRConfig.WeaponReturnSeconds.Value),
                OwnedMeshes = baked,
                Semi = FireSelector.IsSemiByPlayer(w),
                PlayerOwned = true,
                MagOut = magOut,
                SlideBack = slideBack,
                EmptyChamber = EmptyChamberOf(w)
            };
            drop.Reach = ReachOf(root.transform, colliders);
            PropPhysics.IgnoreBodiesNear(colliders, root.transform.position, 1.5f, drop.IgnoredBodies);
            _drops.Add(drop);
            return drop;
        }

        /// <summary>Copies the weapon's visible meshes (no arms, nothing the mod added except its moving bolt piece) under <paramref name="root"/>.</summary>
        private static bool CopyHeldModel(WeaponRig rig, Transform root, bool magazineIn, List<Mesh> baked)
        {
            var skip = new HashSet<Renderer>(rig.ArmRenderers);
            if (!magazineIn)
            {
                foreach (Transform part in rig.MagazineParts)
                {
                    if (part != null)
                    {
                        foreach (Renderer r in part.GetComponentsInChildren<Renderer>(true))
                        {
                            skip.Add(r);
                        }
                    }
                }
            }
            bool any = false;
            foreach (Renderer r in rig.Weapon.GetComponentsInChildren<Renderer>(false))
            {
                if (!r.enabled || skip.Contains(r) || r is ParticleSystemRenderer || r is LineRenderer || r is TrailRenderer)
                {
                    continue;
                }
                if (GunCollider.AddedByMod(r.transform, rig.Weapon.transform) && !r.name.StartsWith("RavenfieldVR Bolt", StringComparison.Ordinal))
                {
                    continue;
                }
                Mesh mesh;
                Vector3 scale;
                if (r is SkinnedMeshRenderer smr)
                {
                    if (smr.sharedMesh == null)
                    {
                        continue;
                    }
                    mesh = new Mesh { name = smr.sharedMesh.name + " (dropped)" };
                    smr.BakeMesh(mesh);
                    if (mesh.vertexCount == 0)
                    {
                        UnityEngine.Object.Destroy(mesh);
                        continue;
                    }
                    baked.Add(mesh);
                    scale = Vector3.one; // baked with the renderer's scale
                }
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                    {
                        continue;
                    }
                    mesh = mf.sharedMesh;
                    scale = r.transform.lossyScale;
                    if (scale.sqrMagnitude < 1e-8f)
                    {
                        continue; // scaled away (a hidden part)
                    }
                }
                var part = new GameObject(r.name);
                part.transform.SetParent(root, false);
                part.transform.SetPositionAndRotation(r.transform.position, r.transform.rotation);
                part.transform.localScale = scale;
                part.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = part.AddComponent<MeshRenderer>();
                mr.sharedMaterials = Util.MaterialFix.Fix(r.sharedMaterials);
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Your own dropped weapon goes back to its holster when you leave it lying for a while or walk away from it, as
        /// long as its slot is still free (pick something else up into it and the dropped one stays a normal drop).
        /// </summary>
        private bool _playerWasDead;

        /// <summary>
        /// The player died: the weapons of theirs lying about (kept after a swap, or waiting to go back to a holster) are no longer
        /// the only copies, since the respawn hands out the loadout again. Kept, they stayed beside a full loadout, and one waiting
        /// for its holster was made a kept copy by the first frame on foot (pickup hunt, 2026-10-21). They become ordinary drops,
        /// still grabbable, on the normal timer from now.
        /// </summary>
        private void LetGoOfOwnDrops()
        {
            foreach (DroppedWeapon d in _drops)
            {
                if (d.OwnerSlot >= 0 || d.Keep || d.PlayerOwned)
                {
                    d.OwnerSlot = -1;
                    d.Keep = false;
                    d.PlayerOwned = false;
                    d.DropTime = Time.time;
                }
            }
        }

        private void ReturnOwnDrops(Actor actor)
        {
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                DroppedWeapon d = _drops[i];
                if (d.OwnerSlot < 0 || d.Go == null)
                {
                    continue;
                }
                bool due = Time.time >= d.ReturnAt || (d.Go.transform.position - actor.Position()).sqrMagnitude > 400f;
                if (!due)
                {
                    continue;
                }
                if (actor.dead || d.OwnerSlot >= actor.weapons.Length || actor.weapons[d.OwnerSlot] != null)
                {
                    // Its slot is taken (you picked something else up into it), so it cannot go back to the holster. It is
                    // still the only copy of that weapon, though, so it is kept like a swapped-away one rather than left to
                    // expire and vanish from both the ground and the loadout 90 s after the drop (85-agent review).
                    d.OwnerSlot = -1;
                    d.Keep = true;
                    d.PlayerOwned = true;
                    continue;
                }
                // Into the holster, not the hand: the slot must not look like the active one while it is spawned. Always, not only
                // with nothing drawn: a stale slot left by an earlier drop, with another gun in the hand, drew this one and holstered
                // the gun in use (2026-10-19).
                int active = actor.activeWeaponSlot;
                actor.activeWeaponSlot = -1;
                Weapon w;
                try
                {
                    w = actor.EquipNewWeaponEntry(d.Entry, d.OwnerSlot, false);
                }
                finally
                {
                    actor.activeWeaponSlot = active;
                }
                if (w == null)
                {
                    // The game made nothing: an ordinary drop the player can pick up, not removed (it was gone, rounds and all) and
                    // not tried again every frame (each try drops what is in that slot).
                    Log.WarnOnce("returnfailed:" + d.Entry.name, $"Returning {d.Entry.name} to its holster: the game spawned nothing; it stays as a drop.");
                    d.OwnerSlot = -1;
                    d.Keep = true;
                    // Still yours, as a slot taken above: unmarked, once no longer kept the cap culled it with the soldiers' drops
                    // instead of after them, and its magazines' rounds did not go back into it (bug hunt, 2026-10-22).
                    d.PlayerOwned = true;
                    continue;
                }
                RestoreAmmo(w, d);
                _sys.Sounds.Play(MechSound.Holster, _sys.Rig.Head.position - Vector3.up * 0.5f, 0.6f);
                Remove(d);
            }
        }

        public void Clear()
        {
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                Remove(_drops[i]);
            }
        }

        public void Dispose()
        {
            ParkedPartsCache.Write();
            Clear();
            if (Instance == this)
            {
                Instance = null;
            }
        }
    }

    [HarmonyPatch(typeof(Actor), "Die", typeof(DamageInfo), typeof(bool))]
    internal static class ActorDiePatch
    {
        private static void Prefix(Actor __instance, DamageInfo info, bool isSilentKill)
        {
            if (!VRManager.Active || isSilentKill || __instance.dead || WeaponDrops.Instance == null)
            {
                return;
            }
            Log.Guard("weapon drop", () => WeaponDrops.Instance.OnActorDying(__instance, info));
        }
    }
}
