using System.Collections.Generic;
using UnityEngine;

namespace RavenfieldVR.Core
{
    /// <summary>
    /// HideTinyDistantObjects (to try, off by default): the level's objects that would be only a few pixels across are not drawn.
    /// Each eye draws everything it sees twice in multi-pass (with shadows on, a depth pass and then the colour pass), and the eyes'
    /// render was most of Medium's main thread in the user's session (3.5 ms against the Game preset's 0.97; Tasks 348). A prop with
    /// no LOD group is drawn all the way to the far clip (2 km on Medium) however small it has become: 1 m corner to corner at 300 m is about
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
        /// Hidden under about this many pixels of an eye across (its bounds' diagonal against its distance from the head), drawn again
        /// a quarter over it, so one on the line does not blink.
        /// </summary>
        internal const float Pixels = 3f;

        /// <summary>
        /// The angle (radians) under which an object is hidden: <see cref="Pixels"/> of the eyes as they are drawn now (0.004, 1 m
        /// corner to corner past 250 m, its bounds' diagonal as measured, on the user's Medium eye of 1357 px across 100 degrees; less on bigger eyes, so Ultra hides no object of
        /// 4-5 px), until the eyes are known.
        /// </summary>
        private static float _hideBelow = 0.004f;

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
        private static bool _scopedSeen;
        private static int _remeasure;
        private const int RemeasuredAPass = 64;

        /// <summary>The graphics bench's own choice while it runs (null: the setting's).</summary>
        internal static bool? Bench;

        // (Also the frame rate hold's first step, with its shorter shadows: three pixels of an object are the cheapest thing it can give up.)
        private static bool On => Bench ?? (VRConfig.HideTinyDistantObjects.Value || VRConfig.HoldFrameRate.Value && GraphicsPresets.HoldLevel >= 1);

        /// <summary>Whether tiny distant objects are being hidden now (the setting, the frame rate hold's first step, or the bench).</summary>
        internal static bool Active => On;

        /// <summary>How many objects are hidden now (said in the frame rate line).</summary>
        internal static int Count { get; private set; }

        /// <summary>
        /// Whether an object of <paramref name="size"/> (m) at <paramref name="distance"/> (m) is hidden next, from whether it is now:
        /// hidden under the angle of <see cref="Pixels"/>, shown again a quarter over it.
        /// </summary>
        internal static bool NextHidden(bool hidden, float size, float distance)
        {
            float angle = size / Mathf.Max(distance, 0.01f);
            return hidden ? angle < _hideBelow * 1.25f : angle < _hideBelow;
        }

        /// <summary>
        /// The angle of <see cref="Pixels"/> on an eye <paramref name="eyePixels"/> wide across <paramref name="fovRadians"/> (its
        /// horizontal field of view), kept between 0.002 and 0.008 (a reading gone wrong hides nothing much, nor everything).
        /// </summary>
        internal static float AngleFor(int eyePixels, float fovRadians) =>
            eyePixels > 0 && fovRadians > 0.1f ? Mathf.Clamp(Pixels * fovRadians / eyePixels, 0.002f, 0.008f) : 0.004f;

        /// <summary>
        /// The threshold from the eyes as drawn now: their texture's width over the left eye's horizontal field of view, read from its
        /// own projection (the head camera's fieldOfView stays Unity's default 60 and its aspect is the desktop window's in VR; review).
        /// </summary>
        private static void TakeThreshold(Camera eyes)
        {
            int width = UnityEngine.XR.XRSettings.eyeTextureWidth;
            if (eyes == null || width <= 0)
            {
                return;
            }
            Matrix4x4 m = eyes.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left);
            if (m.m00 > 0.05f)
            {
                _hideBelow = AngleFor(width, 2f * Mathf.Atan(1f / m.m00));
            }
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
                // Everything drawn again, the scan kept: the frame rate hold goes in and out of its first step, and a scan each time
                // would be a hitch each time (a level change or VR stopping forgets it: Restore).
                if (Count > 0)
                {
                    ShowAll();
                }
                // With the hold on, the level is scanned when it was meant to be, a few seconds in: left to the hold's first step,
                // the scan's whole-scene searches came in the very frame the frames were already late (graphics scout, 2026-11-03).
                if (_scanAt >= 0f && Time.unscaledTime >= _scanAt && VRConfig.HoldFrameRate.Value && Bench == null)
                {
                    Scan();
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
            // (The lens up or down is a pass at once: a tiny crate stayed hidden in the magnified view until the next one came.)
            bool scoped = now - Weapons.VRScope.LensDrawnAt < 0.5f;
            if (now >= _nextPass || scoped != _scopedSeen)
            {
                _scopedSeen = scoped;
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
            TakeThreshold(eyes);
            float farthest = _hideBelow * 1.25f * (eyes != null && eyes.farClipPlane > 1f ? eyes.farClipPlane : 4000f);
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
            TakeThreshold(rig.Camera); // (the render scale may have changed: the frame rate hold, the panel)
            // A magnified lens on: everything drawn (a crate hidden as three pixels of an eye is twelve through a 4x scope).
            bool scoped = Time.unscaledTime - Weapons.VRScope.LensDrawnAt < 0.5f;
            // A few are measured again each pass, in turn: a prop a map's script moved up to the player stayed hidden by where it stood at the scan, and
            // a bounds read of every one, five times a second, would cost more than hiding them saves.
            for (int n = 0; n < RemeasuredAPass && Tracked.Count > 0; n++)
            {
                _remeasure = (_remeasure + 1) % Tracked.Count;
                Entry moved = Tracked[_remeasure];
                if (moved.Renderer != null)
                {
                    moved.Centre = moved.Renderer.bounds.center;
                    Tracked[_remeasure] = moved;
                }
            }
            int hidden = 0;
            for (int i = Tracked.Count - 1; i >= 0; i--)
            {
                Entry e = Tracked[i];
                if (e.Renderer == null)
                {
                    // Gone mid-level (destroyed, broken): off the list, which the re-measure above went through in turn, slots and all (scout).
                    Tracked.RemoveAt(i);
                    continue;
                }
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
                hidden += hide && e.Renderer != null ? 1 : 0; // (one gone with its scene is no hidden object)
            }
            Count = hidden;
        }

        private static void ShowAll()
        {
            for (int i = 0; i < Tracked.Count; i++)
            {
                Entry e = Tracked[i];
                if (e.Hidden)
                {
                    if (e.Renderer != null)
                    {
                        e.Renderer.forceRenderingOff = false;
                    }
                    e.Hidden = false; // (so the next pass hides it again if it is still so small)
                    Tracked[i] = e;
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
