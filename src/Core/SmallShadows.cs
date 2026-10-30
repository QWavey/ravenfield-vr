using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// SmallObjectsNoShadows (to try, off by default): the level's small objects, under 40 cm corner to corner, cast no shadow. With
    /// shadows on, every caster is drawn into the shadow maps once per eye in multi-pass, and the eyes' render on the main thread
    /// was most of Medium's frame in the user's session (3.5 ms against the Game preset's 0.97 with no shadows; Tasks 348): cans,
    /// rocks and small props add draw calls for shadows hardly seen. Mesh renderers only (a soldier's skinned body keeps his), not
    /// the mod's own layers (its UI and the first-person gun), active when scanned: a few seconds after a level loads, and when
    /// the setting is turned on. Each one is put back when it is turned off or VR stops. Said in the frame rate line when on.
    /// </summary>
    internal static class SmallShadows
    {
        private const float Small = 0.4f;
        private const int UiLayer = 5, FirstPersonLayer = 18;
        private static readonly List<Renderer> Trimmed = new List<Renderer>();
        private static bool _applied;
        private static float _scanAt = -1f;

        /// <summary>The graphics bench's own choice while it runs (null: the setting's).</summary>
        internal static bool? Bench;

        private static bool On => Bench ?? VRConfig.SmallObjectsNoShadows.Value;

        /// <summary>How many objects have their shadow off now (said in the frame rate line, so a session's log shows it took).</summary>
        internal static int Count => Trimmed.Count;

        /// <summary>
        /// A level loaded: its objects scanned a few seconds in (they are still being made), the old level's forgotten; one that
        /// lives on (DontDestroyOnLoad) is kept, to be given its shadow back when the setting is turned off (review, 2026-10-30).
        /// </summary>
        internal static void OnSceneChanged()
        {
            Trimmed.RemoveAll(r => r == null);
            _applied = false;
            _scanAt = Time.unscaledTime + 3f;
        }

        /// <summary>Each frame: the setting applied or undone when it changes, and the scan after a level load.</summary>
        internal static void Frame()
        {
            if (!On)
            {
                if (_applied || Trimmed.Count > 0)
                {
                    Restore();
                }
                return;
            }
            // A level's scan waits its few seconds (a scan at once on the load's first frame missed what the level made after it;
            // review, 2026-10-30); the setting turned on scans at once.
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
            int before = Trimmed.Count;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            foreach (MeshRenderer r in Object.FindObjectsOfType<MeshRenderer>())
            {
                int layer = r.gameObject.layer;
                if (r.shadowCastingMode != ShadowCastingMode.On || layer == UiLayer || layer == FirstPersonLayer || r.bounds.size.magnitude >= Small)
                {
                    continue;
                }
                r.shadowCastingMode = ShadowCastingMode.Off;
                Trimmed.Add(r);
            }
            Log.Info($"Small objects without shadows (to try): {Trimmed.Count - before} more under {Small * 100f:0} cm, {Trimmed.Count} in all "
                     + $"(the scan took {watch.Elapsed.TotalMilliseconds:0.0} ms).");
        }

        /// <summary>
        /// Every small object's shadow back (the setting off, VR stopping); not one the game changed since (to shadows only, as it does
        /// for a soldier's body seen from inside).
        /// </summary>
        internal static void Restore()
        {
            foreach (Renderer r in Trimmed)
            {
                if (r != null && r.shadowCastingMode == ShadowCastingMode.Off)
                {
                    r.shadowCastingMode = ShadowCastingMode.On;
                }
            }
            Trimmed.Clear();
            _applied = false;
        }
    }
}
