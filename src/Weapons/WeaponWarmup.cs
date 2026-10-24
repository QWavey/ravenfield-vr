using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using RavenfieldVR.Core;
using UnityEngine;

namespace RavenfieldVR.Weapons
{
    /// <summary>
    /// Analysing a weapon rig samples its animations on a copy of the weapon and takes tens of milliseconds, which is
    /// a visible hitch in a headset the first time a weapon type is drawn in a match. In VR every installed weapon is
    /// analysed ahead of time while the main menu is up, so drawing, picking up and switching weapons in a match never
    /// waits for it. Paced to about 6 ms per frame on average, paused while the menu is being used and while a match runs.
    /// The weapon list is watched after each pass: a weapon that appears later (a workshop mod subscribed in the menu) is
    /// analysed and prepared then, not on its first death in a match (task 263, 2026-10-18).
    /// </summary>
    internal static class WeaponWarmup
    {
        /// <summary>
        /// Weapons fully prepared here (analysis, shape fit, dropped model). By entry, as WeaponDrops keys its model templates:
        /// a weapon re-created under the same name (a mod reload) has a template of its own to build.
        /// </summary>
        private static readonly HashSet<WeaponManager.WeaponEntry> Warmed = new HashSet<WeaponManager.WeaponEntry>();

        private static bool _running;
        private static bool _rescan;

        /// <summary>
        /// A content reload cleared what <see cref="Prepare"/> made (analyses, shapes, dropped-model templates): every weapon is
        /// prepared again. The built-in weapons keep their entry objects through a reload (WeaponManager.ClearContentModWeapons
        /// lists the same ones), so they stayed "warmed" with nothing ready, and their first draw and first drop in the next
        /// match paid it all (bug hunt, 2026-10-22).
        /// </summary>
        internal static void Forget() => Warmed.Clear();

        public static void Start()
        {
            if (!VRManager.Active || VRManager.Instance == null || WeaponSelfTest.Running)
            {
                return;
            }
            if (_running)
            {
                _rescan = true; // more content loaded mid-run: go over the weapon list again when this pass ends
                return;
            }
            VRManager.Instance.StartCoroutine(Run());
        }

        /// <summary>
        /// The held weapon's physics shape is fitted from its meshes (tens of thousands of vertices on detailed models): done
        /// here too, so drawing the weapon in a match finds it ready.
        /// </summary>
        private static void PrefitShape(Weapon weapon, WeaponRigData data, WeaponManager.WeaponEntry entry)
        {
            if (data.Action == ActionType.NoAmmo)
            {
                return;
            }
            var rig = new WeaponRig(weapon, data);
            ShapeFit.Bake(GunCollider.Fit(rig, Runtime.OpenVRMath.SafeLookRotation(data.AimAxis, data.Up), data.GripPoint));
            _fitted++;
            ActionType action = data.Action;
            // Only the loadout's weapons: each cut keeps a copy of the receiver's mesh, and hundreds of mod weapons would keep
            // hundreds of them.
            // (No cut when the model's own handle is the one taken: HeldWeapon, task 299.)
            if (Loadout.Contains(entry.nameHash)
                && (data.SlidePath == null && data.HandlePath == null && (action == ActionType.SelfLoading || action == ActionType.Manual || action == ActionType.ShellFed)
                    || data.CarveHandle && data.HandlePath == null))
            {
                BoltPiece.Prewarm(rig); // the handle cut out of the receiver, as the draw makes it (HeldWeapon)
            }
        }

        private static int _fitted;

        private sealed class StepResult
        {
            public bool Done;
            public System.Exception Error;
        }

        /// <summary>
        /// <see cref="Prepare"/> spread over frames for the menu: the analysis, the physics shape, the dropped model and each sub
        /// weapon's analysis in a frame of its own, and the frames after each left alone in proportion to its time (about 6 ms
        /// of warm-up a frame on average). In one frame a weapon's whole preparation was one long frame: the user's menu ran at
        /// 16-40 fps while it went on (the input check's "mod 16-22 ms a frame", 2026-10-24 log). A match starting in between
        /// stops it with the weapon not marked done (its first draw does the rest, as for a weapon not reached yet).
        /// </summary>
        private static IEnumerator PrepareInSteps(Weapon weapon, WeaponManager.WeaponEntry entry, StepResult result)
        {
            if (weapon == null || entry == null || Warmed.Contains(entry))
            {
                result.Done = true;
                yield break;
            }
            WeaponRigData data = null;
            var steps = new List<System.Action>
            {
                () => data = WeaponAnalyzer.Get(weapon, entry),
                () => PrefitShape(weapon, data, entry)
            };
            if (VRConfig.EnemyWeaponDrops.Value || VRConfig.ShowHolsters.Value)
            {
                steps.Add(() => WeaponDrops.Prepare(entry)); // the model a fallen soldier drops (and the holstered copy)
            }
            foreach (Weapon sub in weapon.GetComponentsInChildren<Weapon>(true))
            {
                if (sub != weapon)
                {
                    steps.Add(() =>
                    {
                        if (sub != null && !WeaponAnalyzer.IsCached(sub, entry))
                        {
                            WeaponAnalyzer.Get(sub, entry);
                        }
                    });
                }
            }
            var watch = new System.Diagnostics.Stopwatch();
            foreach (System.Action step in steps)
            {
                if (InMatch())
                {
                    yield break; // a match began: not now
                }
                watch.Restart();
                System.Exception error = null;
                try
                {
                    step();
                }
                catch (System.Exception e)
                {
                    error = e;
                }
                if (error != null)
                {
                    result.Error = error;
                    yield break;
                }
                int rest = Mathf.Clamp(Mathf.CeilToInt((float)watch.Elapsed.TotalMilliseconds / 6f), 1, 45);
                for (int i = 0; i < rest; i++)
                {
                    yield return null;
                }
            }
            result.Done = true;
        }

        /// <summary>
        /// One weapon made ready as the menu does it: its analysis, its physics shape, its dropped model and
        /// its sub weapons' analyses. The autotest calls it for the weapons it tests, so its numbers are a match after the menu
        /// (it starts a map at once, and the first draw of each weapon paid what the menu does in play).
        /// </summary>
        internal static void Prepare(Weapon weapon, WeaponManager.WeaponEntry entry)
        {
            if (weapon == null || entry == null || Warmed.Contains(entry))
            {
                return;
            }
            WeaponRigData data = WeaponAnalyzer.Get(weapon, entry);
            PrefitShape(weapon, data, entry);
            if (VRConfig.EnemyWeaponDrops.Value || VRConfig.ShowHolsters.Value)
            {
                WeaponDrops.Prepare(entry); // the model a fallen soldier drops (and the holstered copy)
            }
            foreach (Weapon sub in weapon.GetComponentsInChildren<Weapon>(true))
            {
                if (sub != weapon && !WeaponAnalyzer.IsCached(sub, entry))
                {
                    WeaponAnalyzer.Get(sub, entry);
                }
            }
            Warmed.Add(entry);
        }

        private static readonly string[] LoadoutKeys = { "primary", "secondary", "gear1", "gear2", "gear3" };

        /// <summary>The saved loadout's weapons (name hashes), as the last pass found them.</summary>
        private static readonly HashSet<int> Loadout = new HashSet<int>();

        /// <summary>
        /// Then the weapons the bots can carry (either team's weapon pool in the match settings): a match started before the
        /// warm-up was through had soldiers dying with weapons it had not reached, and their dropped copy was built on the death
        /// frame (the user's session of 2026-10-19 night: 47-83 ms drops of the K13 Spectre, QBZ192 and mk18riii; task 309). The
        /// order within each group stays the game's.
        /// </summary>
        private static void PutBotWeaponsNext(List<WeaponManager.WeaponEntry> entries)
        {
            TeamInfo[] teams = GameManager.instance != null && GameManager.instance.gameInfo != null ? GameManager.instance.gameInfo.team : null;
            if (teams == null)
            {
                return;
            }
            int start = 0;
            while (start < entries.Count && entries[start] != null && Loadout.Contains(entries[start].nameHash))
            {
                start++; // after the loadout's own
            }
            PutBotWeaponsFirst(entries, start);
        }

        /// <summary>The weapons from <paramref name="start"/> on that the bots can carry moved before the rest (each group in its order).</summary>
        private static void PutBotWeaponsFirst(List<WeaponManager.WeaponEntry> entries, int start)
        {
            TeamInfo[] teams = GameManager.instance != null && GameManager.instance.gameInfo != null ? GameManager.instance.gameInfo.team : null;
            if (teams == null)
            {
                return;
            }
            var pool = new List<WeaponManager.WeaponEntry>();
            for (int i = entries.Count - 1; i >= start; i--)
            {
                WeaponManager.WeaponEntry e = entries[i];
                bool carried = false;
                foreach (TeamInfo team in teams)
                {
                    carried |= e != null && team != null && team.availableWeapons != null && team.availableWeapons.Contains(e);
                }
                if (carried)
                {
                    pool.Insert(0, e);
                    entries.RemoveAt(i);
                }
            }
            entries.InsertRange(start, pool);
        }

        /// <summary>
        /// The weapons of the saved loadout go first: with hundreds of mod weapons the warm-up takes a while, and a match
        /// started soon after the game came up drew them before it got there (every first draw hitched).
        /// </summary>
        private static void PutLoadoutFirst(List<WeaponManager.WeaponEntry> entries)
        {
            HashSet<int> wanted = Loadout;
            wanted.Clear();
            foreach (string key in LoadoutKeys)
            {
                if (PlayerPrefs.HasKey(key))
                {
                    wanted.Add(PlayerPrefs.GetInt(key));
                }
            }
            if (LoadoutUi.instance != null && LoadoutUi.instance.loadout != null)
            {
                WeaponManager.LoadoutSet l = LoadoutUi.instance.loadout;
                foreach (WeaponManager.WeaponEntry e in new[] { l.primary, l.secondary, l.gear1, l.gear2, l.gear3 })
                {
                    if (e != null)
                    {
                        wanted.Add(e.nameHash);
                    }
                }
            }
            wanted.Remove(-1);
            if (wanted.Count == 0)
            {
                return;
            }
            var first = new List<WeaponManager.WeaponEntry>();
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] != null && wanted.Contains(entries[i].nameHash))
                {
                    first.Insert(0, entries[i]);
                    entries.RemoveAt(i);
                }
            }
            entries.InsertRange(0, first);
        }

        /// <summary>Whether the game's weapon list is no longer the one a pass took: an entry added, removed or re-created.</summary>
        private static bool WeaponListChanged(List<WeaponManager.WeaponEntry> taken)
        {
            if (WeaponManager.instance == null || WeaponManager.instance.allWeapons == null)
            {
                return false;
            }
            int i = 0;
            foreach (WeaponManager.WeaponEntry entry in WeaponManager.instance.allWeapons)
            {
                if (i >= taken.Count || taken[i] != entry)
                {
                    return true;
                }
                i++;
            }
            return i != taken.Count;
        }

        private static bool InMatch() => GameManager.IsIngame(out bool noGameplay) && !noGameplay;

        private static IEnumerator Run()
        {
            _running = true;
            var entries = new List<WeaponManager.WeaponEntry>();
            var taken = new List<WeaponManager.WeaponEntry>(); // the list this pass works from, in the game's order
            float started = Time.realtimeSinceStartup;
            do
            {
                _rescan = false;
                float passStarted = Time.realtimeSinceStartup;
                int analysed = 0;
                int fittedAtStart = _fitted;
                entries.Clear();
                if (WeaponManager.instance != null && WeaponManager.instance.allWeapons != null)
                {
                    entries.AddRange(WeaponManager.instance.allWeapons);
                }
                if (entries.Count == 0 && Time.realtimeSinceStartup - started < 60f && VRManager.Active)
                {
                    // Nothing to analyse yet: VR can come up before the game has filled its weapon list, and this pass then ended
                    // with nothing analysed (the log's "Pre-analysed 0 weapon rigs ... in 0,0 s", task 148, 2026-10-18). Wait for
                    // the list and go round again, for up to a minute.
                    yield return new WaitForSecondsRealtime(0.5f);
                    _rescan = true;
                    continue;
                }
                taken.Clear();
                taken.AddRange(entries);
                PutLoadoutFirst(entries);
                PutBotWeaponsNext(entries);
                for (int index = 0; index < entries.Count; index++)
                {
                    // The bots' pools again every ten weapons: at the start of a pass (the game's content just loaded) they are still
                    // the defaults, and the match's own weapon settings came later (review, 2026-10-20).
                    if (index > 0 && index % 10 == 0)
                    {
                        PutBotWeaponsFirst(entries, index);
                    }
                    WeaponManager.WeaponEntry entry = entries[index];
                    if (entry == null || entry.prefab == null)
                    {
                        continue;
                    }
                    // Checked before the waits below: a pass goes over every entry, and one already prepared must not wait for the
                    // menu to be idle or for a read-back (a stuck read-back held each such entry for its full two seconds).
                    Weapon weapon = entry.prefab.GetComponent<Weapon>();
                    // Skipped only once all of it was done here: analysed elsewhere first (a sound donor, a draw), its shape fit and
                    // the dropped model were never prepared, and the first draw in the match did them (the hitch this avoids).
                    if (weapon == null || Warmed.Contains(entry))
                    {
                        continue;
                    }
                    // Only in menus: in a match the frame rate matters more than the cache. While the menu is in use
                    // (pointing along it, clicking, scrolling) it gets every frame: the warm-up goes on once it has been left
                    // alone for a moment (picking a map or a setting lagged otherwise). Both are checked together: a map that
                    // loads within that moment must not get an analysis at the start of the match.
                    while (InMatch() || Time.unscaledTime - UI.VRInputModule.LastUsed < 1.5f && VRManager.Active)
                    {
                        yield return InMatch() ? new WaitForSecondsRealtime(1f) : null;
                    }
                    // Models being measured hold their read-back pictures until done: a few at a time.
                    for (float until = Time.realtimeSinceStartup + 2f; GunVolume.InFlight > 4 && Time.realtimeSinceStartup < until;)
                    {
                        yield return null; // (never stuck: a read-back that never returns does not hold the warm-up up)
                    }
                    while (InMatch())
                    {
                        yield return new WaitForSecondsRealtime(1f); // a match that started during the wait above: not now
                    }
                    var result = new StepResult();
                    yield return PrepareInSteps(weapon, entry, result);
                    if (result.Error != null)
                    {
                        Warmed.Add(entry); // not tried again every pass
                        Log.WarnOnce("warmup:" + entry.name, $"Pre-analysing {entry.name} failed: {result.Error.Message}");
                    }
                    else if (result.Done)
                    {
                        analysed++;
                        Warmed.Add(entry);
                    }
                    // (Stopped part way because a match started: not marked, its first draw does the rest.)
                }
                Log.Info($"Pre-analysed {analysed} weapon rigs (of {entries.Count} weapons; the rest were ready) and {_fitted - fittedAtStart} physics shapes in {Time.realtimeSinceStartup - passStarted:0.0} s.");
                ParkedPartsCache.Write(); // what this pass analysed, for the next session's first drops
                // A weapon that appears after this pass took the list (a workshop mod subscribed or loaded in the menu, a weapon
                // unlocked) was never analysed: its first death in a match built the dropped model there, a hitch of tens of
                // milliseconds (WeaponDrops.Spawn). The list is checked every two seconds and the pass goes round again when it has
                // changed; the entries already prepared are skipped at once. Not during a self-test, whose timings are its own.
                while (!_rescan && VRManager.Active)
                {
                    yield return new WaitForSecondsRealtime(2f);
                    if (!_rescan && !WeaponSelfTest.Running && WeaponListChanged(taken))
                    {
                        _rescan = true;
                    }
                }
            }
            while (_rescan && VRManager.Active);
            _running = false;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnAllContentLoaded))]
    internal static class WarmupContentLoadedPatch
    {
        /// <summary>
        /// A reload of the content (a workshop mod updated or toggled in the menu) creates the weapons again under the same names.
        /// The caches keyed by name kept the old rig, hand volume and shape, and the new weapon used them until the game restarted
        /// (2026-10-19). They are dropped before the warm-up analyses the new weapons. (Each is refilled when it is next asked for.)
        /// </summary>
        private static void Prefix()
        {
            WeaponAnalyzer.ClearCache();
            GunVolume.ClearCache();
            ShapeFit.ClearCache();
            SoundDonors.Clear();
            WeaponDrops.ClearShapes();
            MagazineProp.ClearLooks();
            HeldWeapon.ClearNameCaches();
            BoltPiece.ClearCache();
            ThirdPersonAimPatch.ForgetReported();
            WeaponWarmup.Forget(); // and so prepared again by the warm-up the postfix starts
        }

        private static void Postfix() => WeaponWarmup.Start();
    }
}
