using System.Collections.Generic;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// InstanceRepeatedObjects (to try, off by default): the level's materials drawn by more than one mesh renderer are drawn
    /// instanced, so copies of one mesh with one material go in one draw instead of one each (each draw is paid twice an eye with
    /// shadows on, in multi-pass: the eyes' render was most of Medium's main thread in the user's session, Tasks 348). Only a
    /// material whose renderers are all left to it: none static-batched (those are one combined mesh already) and none lightmapped
    /// (their lighting stays as baked), not the mod's layers. A shader with no instanced variant draws as before; each material
    /// is put back when the setting is turned off, the level changes or VR stops. The frame rate line says how many it changed.
    /// </summary>
    internal static class InstancedObjects
    {
        private const int UiLayer = 5, FirstPersonLayer = 18;

        private static readonly List<Material> Changed = new List<Material>();
        private static bool _applied;
        private static float _scanAt = -1f;

        /// <summary>The graphics bench's own choice while it runs (null: the setting's).</summary>
        internal static bool? Bench;

        private static bool On => Bench ?? VRConfig.InstanceRepeatedObjects.Value;

        /// <summary>How many materials are drawn instanced by this now (said in the frame rate line).</summary>
        internal static int Count => Changed.Count;

        /// <summary>
        /// Whether a renderer leaves its materials free to be instanced: not static-batched, not lightmapped (index -1, or 65534
        /// and up: none), not on the mod's UI or first-person layer.
        /// </summary>
        internal static bool Eligible(int layer, bool staticBatched, int lightmapIndex) =>
            layer != UiLayer && layer != FirstPersonLayer && !staticBatched && (lightmapIndex < 0 || lightmapIndex >= 0xFFFE);

        /// <summary>A level loaded: the old level's materials given back, the new one scanned a few seconds in.</summary>
        internal static void OnSceneChanged()
        {
            Restore();
            _scanAt = Time.unscaledTime + 5f; // (after SmallShadows' and TinyObjects' scans, not in the same frame)
        }

        /// <summary>Each frame: the setting applied or undone when it changes, and the scan after a level load.</summary>
        internal static void Frame()
        {
            if (!On)
            {
                if (_applied || Changed.Count > 0)
                {
                    Restore();
                }
                return;
            }
            if (_scanAt >= 0f)
            {
                if (Time.unscaledTime >= _scanAt)
                {
                    Scan();
                }
            }
            else if (!_applied)
            {
                Scan();
            }
        }

        private static void Scan()
        {
            _scanAt = -1f;
            _applied = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            // How many eligible renderers draw each material, and the materials any ineligible one draws (left alone).
            var uses = new Dictionary<Material, int>();
            var barred = new HashSet<Material>();
            var counted = new HashSet<Material>();
            // Every renderer of the loaded scenes, switched off ones too: one switched on later (a lightmapped part) still bars its
            // material (review); only the ones drawn now count toward "drawn by several".
            foreach (MeshRenderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (r == null || !r.gameObject.scene.isLoaded)
                {
                    continue; // (a prefab or another asset)
                }
                bool eligible = Eligible(r.gameObject.layer, r.isPartOfStaticBatch, r.lightmapIndex)
                               && (r.realtimeLightmapIndex < 0 || r.realtimeLightmapIndex >= 0xFFFE); // (nor lit by realtime GI's maps)
                bool drawn = r.enabled && r.gameObject.activeInHierarchy;
                counted.Clear();
                foreach (Material m in r.sharedMaterials)
                {
                    if (m == null)
                    {
                        continue;
                    }
                    if (!eligible)
                    {
                        barred.Add(m);
                    }
                    else if (drawn && counted.Add(m)) // (once a renderer: one using it in two slots is no second object)
                    {
                        uses.TryGetValue(m, out int n);
                        uses[m] = n + 1;
                    }
                }
            }
            foreach (KeyValuePair<Material, int> kv in uses)
            {
                Material m = kv.Key;
                if (kv.Value < 2 || barred.Contains(m) || m.enableInstancing)
                {
                    continue; // (one already instanced is its author's: left on)
                }
                m.enableInstancing = true;
                Changed.Add(m);
            }
            Log.Info($"Repeated objects instanced (to try): {Changed.Count} materials of {uses.Count} (the scan took {watch.Elapsed.TotalMilliseconds:0.0} ms).");
        }

        /// <summary>Every material this changed put back as it was.</summary>
        internal static void Restore()
        {
            foreach (Material m in Changed)
            {
                if (m != null)
                {
                    m.enableInstancing = false;
                }
            }
            Changed.Clear();
            _applied = false;
            _scanAt = -1f;
        }
    }
}
