using System.Collections.Generic;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// HideTinyDistantObjects (to try, off by default): the level's objects that would be only a few pixels across are not drawn.
    /// Each eye draws everything it sees twice in multi-pass (with shadows on, a depth pass and then the colour pass), and the eyes'
    /// render was most of Medium's main thread in the user's session (3.5 ms against the Game preset's 0.97; Tasks 348). A prop with
    /// no LOD group is drawn all the way to the far clip (2 km on Medium) however small it has become: a 1 m crate at 300 m is about
    /// two pixels of an eye, four draws a frame. Mesh renderers only, standing still (none under a rigid body, an animator or
    /// animation, a soldier, a weapon or a vehicle), not static-batched (switching one member off splits its batch into more draws), not in a LOD group (its LOD
    /// culls it), not the mod's layers. Everything is drawn while a magnified scope lens is (its view is many times larger), and when
    /// the setting is turned off or VR stops. Scanned a few seconds after a level loads (or when turned on), once: a level's standing
    /// props are there from its start, and a scan again every minute was a frame's hitch of its own (review); the frame rate line
    /// says how many are hidden.
    /// </summary>
    internal static class TinyObjects
    {
        /// <summary>
        /// Hidden under this size against its distance (its bounds' diagonal over the distance from the head, radians: 0.004 is a
        /// 1 m crate past 250 m, about three pixels of the user's Medium eye), drawn again over <see cref="ShowAbove"/>, so one
        /// on the line does not blink.
        /// </summary>
        internal const float HideBelow = 0.004f, ShowAbove = 0.005f;

        private const int UiLayer = 5, FirstPersonLayer = 18;

        private struct Entry
        {
            public Renderer Renderer;
            public Vector3 Centre;
            public float Size;
            public bool Hidden;
        }

        private static readonly List<Entry> Tracked = new List<Entry>();
        // The scan's scratch: what holds a moving or living thing, and what a LOD group already culls (kept between scans).
        private static readonly HashSet<Transform> Skip = new HashSet<Transform>();
        private static readonly HashSet<Renderer> InLod = new HashSet<Renderer>();
        private static bool _applied;
        private static float _scanAt = -1f, _nextPass;

        /// <summary>The graphics bench's own choice while it runs (null: the setting's).</summary>
        internal static bool? Bench;

        private static bool On => Bench ?? VRConfig.HideTinyDistantObjects.Value;

        /// <summary>How many objects are hidden now (said in the frame rate line).</summary>
        internal static int Count { get; private set; }

        /// <summary>
        /// Whether an object of <paramref name="size"/> (m) at <paramref name="distance"/> (m) is hidden next, from whether it is now:
        /// hidden under <see cref="HideBelow"/>, shown again over <see cref="ShowAbove"/>.
        /// </summary>
        internal static bool NextHidden(bool hidden, float size, float distance)
        {
            float angle = size / Mathf.Max(distance, 0.01f);
            return hidden ? angle < ShowAbove : angle < HideBelow;
        }

        /// <summary>A level loaded: the old level's forgotten (one that lives on is drawn again), the new one scanned a few seconds in.</summary>
        internal static void OnSceneChanged()
        {
            Restore();
            _scanAt = Time.unscaledTime + 4f; // (a second after SmallShadows' scan, a second before InstancedObjects': not all in one frame)
        }

        /// <summary>Each frame: the setting applied or undone when it changes, the scans, and every fifth of a second a pass.</summary>
        internal static void Frame()
        {
            if (!On)
            {
                if (_applied || Tracked.Count > 0)
                {
                    Restore();
                }
                return;
            }
            float now = Time.unscaledTime;
            if (_scanAt >= 0f)
            {
                if (now < _scanAt)
                {
                    return;
                }
                Scan();
            }
            else if (!_applied)
            {
                Scan();
            }
            if (now >= _nextPass)
            {
                _nextPass = now + 0.2f;
                Pass();
            }
        }

        private static void Scan()
        {
            _scanAt = -1f;
            _applied = true;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            int before = Tracked.Count;
            ShowAll(); // (shown and scanned again in the same frame: nothing is drawn in between)
            Tracked.Clear();
            // What moves or lives, gathered once (a search of each renderer's parents for five kinds cost tens of ms in a level of
            // some 5000 renderers), and the renderers LOD groups cull themselves.
            Skip.Clear();
            InLod.Clear();
            foreach (Rigidbody b in Object.FindObjectsOfType<Rigidbody>())
            {
                Skip.Add(b.transform);
            }
            foreach (Actor a in Object.FindObjectsOfType<Actor>())
            {
                Skip.Add(a.transform);
            }
            foreach (Weapon w in Object.FindObjectsOfType<Weapon>())
            {
                Skip.Add(w.transform);
            }
            foreach (Vehicle v in Object.FindObjectsOfType<Vehicle>())
            {
                Skip.Add(v.transform);
            }
            // (Moved by an animator or an animation, a door or a lift: its place taken at the scan would go stale; review.)
            foreach (Animator an in Object.FindObjectsOfType<Animator>())
            {
                Skip.Add(an.transform);
            }
            foreach (Animation an in Object.FindObjectsOfType<Animation>())
            {
                Skip.Add(an.transform);
            }
            foreach (LODGroup g in Object.FindObjectsOfType<LODGroup>())
            {
                foreach (LOD lod in g.GetLODs())
                {
                    foreach (Renderer lr in lod.renderers)
                    {
                        if (lr != null)
                        {
                            InLod.Add(lr);
                        }
                    }
                }
            }
            // Bigger than this is never so small within the eyes' far clip (2 km on Medium: past 8 m an object is always drawn, and
            // watching it only lengthened each pass).
            Camera eyes = Rig.VRRig.Instance != null ? Rig.VRRig.Instance.Camera : null;
            float farthest = HideBelow * (eyes != null && eyes.farClipPlane > 1f ? eyes.farClipPlane : 4000f);
            foreach (MeshRenderer r in Object.FindObjectsOfType<MeshRenderer>())
            {
                int layer = r.gameObject.layer;
                if (!r.enabled || r.forceRenderingOff || layer == UiLayer || layer == FirstPersonLayer || r.isPartOfStaticBatch || InLod.Contains(r)
                    || UnderSkipped(r.transform))
                {
                    continue; // (one already switched off is someone else's: the player's own body parts, a hidden part)
                }
                Bounds b = r.bounds;
                float size = b.size.magnitude;
                if (size < 0.01f || size > farthest)
                {
                    continue;
                }
                Tracked.Add(new Entry { Renderer = r, Centre = b.center, Size = size });
            }
            _nextPass = 0f;
            if (before == 0)
            {
                Log.Info($"Tiny distant objects hidden (to try): {Tracked.Count} objects watched (the scan took {watch.Elapsed.TotalMilliseconds:0.0} ms).");
            }
        }

        private static bool UnderSkipped(Transform t)
        {
            for (; t != null; t = t.parent)
            {
                if (Skip.Contains(t))
                {
                    return true;
                }
            }
            return false;
        }

        private static void Pass()
        {
            Rig.VRRig rig = Rig.VRRig.Instance;
            if (rig == null || rig.Head == null)
            {
                return;
            }
            Vector3 head = rig.Head.position;
            // A magnified lens on: everything drawn (a crate hidden as three pixels of an eye is twelve through a 4x scope).
            bool scoped = Time.unscaledTime - Weapons.VRScope.LensDrawnAt < 0.5f;
            int hidden = 0;
            for (int i = 0; i < Tracked.Count; i++)
            {
                Entry e = Tracked[i];
                bool hide = !scoped && NextHidden(e.Hidden, e.Size, Vector3.Distance(e.Centre, head));
                if (hide != e.Hidden)
                {
                    if (e.Renderer == null)
                    {
                        continue;
                    }
                    e.Renderer.forceRenderingOff = hide;
                    e.Hidden = hide;
                    Tracked[i] = e;
                }
                hidden += hide ? 1 : 0;
            }
            Count = hidden;
        }

        private static void ShowAll()
        {
            for (int i = 0; i < Tracked.Count; i++)
            {
                Entry e = Tracked[i];
                if (e.Hidden && e.Renderer != null)
                {
                    e.Renderer.forceRenderingOff = false;
                }
            }
            Count = 0;
        }

        /// <summary>Every hidden object drawn again (the setting off, VR stopping, a level change).</summary>
        internal static void Restore()
        {
            ShowAll();
            Tracked.Clear();
            _applied = false;
            _scanAt = -1f;
        }
    }
}
