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
    /// </summary>
    internal static class WeaponWarmup
    {
        /// <summary>Weapons fully prepared here (analysis, shape fit, dropped model), by rig key.</summary>
        private static readonly HashSet<string> Warmed = new HashSet<string>();

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
        private static void PrefitShape(Weapon weapon, WeaponRigData data)
        {
            if (data.Action == ActionType.NoAmmo)
            {
                return;
            }
            var rig = new WeaponRig(weapon, data);
            ShapeFit.Bake(GunCollider.Fit(rig, Runtime.OpenVRMath.SafeLookRotation(data.AimAxis, data.Up), data.GripPoint));
            _fitted++;
        }

        private static int _fitted;

        private static readonly string[] LoadoutKeys = { "primary", "secondary", "gear1", "gear2", "gear3" };

        /// <summary>
        /// The weapons of the saved loadout go first: with hundreds of mod weapons the warm-up takes a while, and a match
        /// started soon after the game came up drew them before it got there (every first draw hitched).
        /// </summary>
        private static void PutLoadoutFirst(List<WeaponManager.WeaponEntry> entries)
        {
            var wanted = new HashSet<int>();
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

        private static bool InMatch() => GameManager.IsIngame(out bool noGameplay) && !noGameplay;

        private static IEnumerator Run()
        {
            _running = true;
            var entries = new List<WeaponManager.WeaponEntry>();
            int analysed = 0;
            float started = Time.realtimeSinceStartup;
            do
            {
                _rescan = false;
                entries.Clear();
                if (WeaponManager.instance != null && WeaponManager.instance.allWeapons != null)
                {
                    entries.AddRange(WeaponManager.instance.allWeapons);
                }
                PutLoadoutFirst(entries);
                foreach (WeaponManager.WeaponEntry entry in entries)
                {
                    // Only in menus: in a match the frame rate matters more than the cache. While the menu is in use
                    // (pointing along it, clicking, scrolling) it gets every frame: the warm-up goes on once it has been left
                    // alone for a moment (picking a map or a setting lagged otherwise). Both are checked together: a map that
                    // loads within that moment must not get an analysis at the start of the match.
                    while (InMatch() || Time.unscaledTime - UI.VRInputModule.LastUsed < 1.5f && VRManager.Active)
                    {
                        yield return InMatch() ? new WaitForSecondsRealtime(1f) : null;
                    }
                    if (entry == null || entry.prefab == null)
                    {
                        continue;
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
                    Weapon weapon = entry.prefab.GetComponent<Weapon>();
                    // Skipped only once all of it was done here: analysed elsewhere first (a sound donor, a draw), its shape fit and
                    // the dropped model were never prepared, and the first draw in the match did them (the hitch this avoids).
                    if (weapon == null || Warmed.Contains(WeaponAnalyzer.KeyOf(weapon, entry)))
                    {
                        continue;
                    }
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        WeaponRigData data = WeaponAnalyzer.Get(weapon, entry);
                        PrefitShape(weapon, data);
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
                        Warmed.Add(WeaponAnalyzer.KeyOf(weapon, entry));
                    }
                    catch (System.Exception e)
                    {
                        Warmed.Add(WeaponAnalyzer.KeyOf(weapon, entry)); // not tried again every pass
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
            }
            while (_rescan && VRManager.Active);
            Log.Info($"Pre-analysed {analysed} weapon rigs (of {entries.Count} weapons; the rest were ready) and {_fitted} physics shapes in {Time.realtimeSinceStartup - started:0.0} s.");
            _running = false;
        }
    }

    [HarmonyPatch(typeof(GameManager), nameof(GameManager.OnAllContentLoaded))]
    internal static class WarmupContentLoadedPatch
    {
        private static void Postfix() => WeaponWarmup.Start();
    }
}
