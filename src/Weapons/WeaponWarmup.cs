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

        private static readonly string[] LoadoutKeys = { "primary", "secondary", "gear1", "gear2", "gear3" };

        /// <summary>The saved loadout's weapons (name hashes), as the last pass found them.</summary>
        private static readonly HashSet<int> Loadout = new HashSet<int>();

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
                foreach (WeaponManager.WeaponEntry entry in entries)
                {
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
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
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
                        analysed++;
                        Warmed.Add(entry);
                    }
                    catch (System.Exception e)
                    {
                        Warmed.Add(entry); // not tried again every pass
                        Log.WarnOnce("warmup:" + entry.name, $"Pre-analysing {entry.name} failed: {e.Message}");
                    }
                    // One analysis is a long frame (tens of milliseconds); the frames after it are left alone so the
                    // menu averages about 6 ms of warm-up per frame instead of one hitch after another.
                    int rest = Mathf.Clamp(Mathf.CeilToInt((float)watch.Elapsed.TotalMilliseconds / 6f), 1, 45);
                    for (int i = 0; i < rest; i++)
                    {
                        yield return null;
                    }
                }
                Log.Info($"Pre-analysed {analysed} weapon rigs (of {entries.Count} weapons; the rest were ready) and {_fitted - fittedAtStart} physics shapes in {Time.realtimeSinceStartup - passStarted:0.0} s.");
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
        }

        private static void Postfix() => WeaponWarmup.Start();
    }
}
