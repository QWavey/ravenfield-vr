using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;

namespace RavenfieldVR.Tests
{
    /// <summary>
    /// Offline timing of GunVolume's per-point solid tests on a synthetic volume (five views of a 30 x 8 x 4 cm box, a hole
    /// through it), through the built RavenfieldVR.dll: ns per SolidAt call, and the hit count over a fixed set of points (it
    /// must not change with a speed-up). Args: the game's Managed folder, the DLL. Build and run: tools/tests/bench.ps1.
    /// </summary>
    internal static class GunVolumeBench
    {
        private static int Main(string[] args)
        {
            string managed = args[0], built = System.IO.Path.GetDirectoryName(args[1]);
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string file = new AssemblyName(e.Name).Name + ".dll";
                foreach (string dir in new[] { built, managed })
                {
                    string p = System.IO.Path.Combine(dir, file);
                    if (System.IO.File.Exists(p))
                    {
                        return Assembly.LoadFrom(p);
                    }
                }
                return null;
            };
            return Run(Assembly.LoadFrom(args[1]));
        }

        private static int Run(Assembly mod)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            Type volType = mod.GetType("RavenfieldVR.Weapons.GunVolume");
            Type viewType = volType.GetNestedType("View", all);
            object vol = FormatterServices.GetUninitializedObject(volType);
            // Five axis views of the box (identity-ish rotations built without Unity's native Quaternion calls).
            UnityEngine.Quaternion[] rots =
            {
                new UnityEngine.Quaternion(0f, 0.7071068f, 0f, 0.7071068f), new UnityEngine.Quaternion(0f, -0.7071068f, 0f, 0.7071068f),
                new UnityEngine.Quaternion(0.7071068f, 0f, 0f, 0.7071068f), new UnityEngine.Quaternion(-0.7071068f, 0f, 0f, 0.7071068f),
                new UnityEngine.Quaternion(0f, 0f, 0f, 1f),
            };
            Array views = Array.CreateInstance(viewType, rots.Length);
            const float texel = 0.0015f;
            for (int n = 0; n < rots.Length; n++)
            {
                object v = FormatterServices.GetUninitializedObject(viewType);
                const int w = 300, h = 300;
                var near = new float[w * h];
                var far = new float[w * h];
                for (int k = 0; k < near.Length; k++)
                {
                    int i = k % w, j = k / w;
                    bool hole = Math.Abs(i - w / 2) < 15 && Math.Abs(j - h / 2) < 15;
                    near[k] = hole ? 1f : -0.15f;
                    far[k] = hole ? -1f : 0.15f;
                }
                Set(v, "ToView", rots[n]);
                Set(v, "WorldToView", rots[n]);
                Set(v, "MinX", -w * texel / 2f);
                Set(v, "MinY", -h * texel / 2f);
                Set(v, "Texel", texel);
                Set(v, "InvTexel", 1f / texel);
                Set(v, "W", w);
                Set(v, "H", h);
                Set(v, "Near", near);
                Set(v, "Far", far);
                Set(v, "BareNear", near);
                Set(v, "BareFar", far);
                views.SetValue(v, n);
            }
            Set(vol, "_views", views);
            Set(vol, "_ready", true);
            Set(vol, "_box", new UnityEngine.Bounds(UnityEngine.Vector3.zero, new UnityEngine.Vector3(0.5f, 0.5f, 0.5f)));
            if (volType.GetField("_boxMin", all) != null)
            {
                Set(vol, "_boxMin", new UnityEngine.Vector3(-0.25f, -0.25f, -0.25f));
                Set(vol, "_boxMax", new UnityEngine.Vector3(0.25f, 0.25f, 0.25f));
            }
            Set(vol, "_inv", new UnityEngine.Quaternion(0f, 0f, 0f, 1f));
            MethodInfo solidAt = volType.GetMethod("SolidAt", all);
            var at = (Func<UnityEngine.Vector3, UnityEngine.Vector3, UnityEngine.Quaternion, bool>)Delegate.CreateDelegate(
                typeof(Func<UnityEngine.Vector3, UnityEngine.Vector3, UnityEngine.Quaternion, bool>), vol, solidAt);
            var rng = new Random(7);
            var pts = new UnityEngine.Vector3[2000000];
            for (int i = 0; i < pts.Length; i++)
            {
                pts[i] = new UnityEngine.Vector3((float)(rng.NextDouble() - 0.5) * 0.5f, (float)(rng.NextDouble() - 0.5) * 0.5f, (float)(rng.NextDouble() - 0.5) * 0.5f);
            }
            UnityEngine.Vector3 origin = UnityEngine.Vector3.zero;
            var identity = new UnityEngine.Quaternion(0f, 0f, 0f, 1f);
            int hits = 0;
            for (int i = 0; i < 200000; i++)
            {
                hits += at(pts[i], origin, identity) ? 1 : 0; // warm up the JIT
            }
            double best = double.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                hits = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < pts.Length; i++)
                {
                    if (at(pts[i], origin, identity))
                    {
                        hits++;
                    }
                }
                sw.Stop();
                best = Math.Min(best, sw.Elapsed.TotalMilliseconds * 1e6 / pts.Length);
            }
            Console.WriteLine($"SolidAt: {best:0.0} ns a call (best of 5 x {pts.Length} points), {hits} hits");

            // The test as it was before 2026-10-22, in managed code (its box check was Bounds.Contains, a native call that does
            // not run outside Unity): divides and Math.Floor per view. Same data: its hit count must equal the one above.
            var vs = new (UnityEngine.Quaternion rot, float minX, float minY, int w, int h, float[] near, float[] far)[views.Length];
            for (int n = 0; n < views.Length; n++)
            {
                object v = views.GetValue(n);
                vs[n] = ((UnityEngine.Quaternion)Get(v, "ToView"), (float)Get(v, "MinX"), (float)Get(v, "MinY"), (int)Get(v, "W"), (int)Get(v, "H"),
                         (float[])Get(v, "Near"), (float[])Get(v, "Far"));
            }
            bool Old(UnityEngine.Vector3 p)
            {
                if (p.x < -0.25f || p.y < -0.25f || p.z < -0.25f || p.x > 0.25f || p.y > 0.25f || p.z > 0.25f)
                {
                    return false;
                }
                foreach (var v in vs)
                {
                    UnityEngine.Vector3 q = v.rot * p;
                    int i = (int)Math.Floor((q.x - v.minX) / texel), j = (int)Math.Floor((q.y - v.minY) / texel);
                    if (i < 0 || j < 0 || i >= v.w || j >= v.h)
                    {
                        return false;
                    }
                    int k = j * v.w + i;
                    if (q.z < v.near[k] || q.z > v.far[k])
                    {
                        return false;
                    }
                }
                return true;
            }
            double oldBest = double.MaxValue;
            int oldHits = 0;
            for (int round = 0; round < 5; round++)
            {
                oldHits = 0;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < pts.Length; i++)
                {
                    if (Old(pts[i]))
                    {
                        oldHits++;
                    }
                }
                sw.Stop();
                oldBest = Math.Min(oldBest, sw.Elapsed.TotalMilliseconds * 1e6 / pts.Length);
            }
            Console.WriteLine($"Old test (managed copy, without its native box call): {oldBest:0.0} ns a call, {oldHits} hits"
                              + (oldHits == hits ? " (same hits)" : $" (DIFFERENT: {hits - oldHits:+0;-0})"));
            // The new per-view maths inline the same way (multiplies, a cast): like for like with the copy above.
            float inv = 1f / texel;
            bool New(UnityEngine.Vector3 p)
            {
                if (p.x < -0.25f || p.y < -0.25f || p.z < -0.25f || p.x > 0.25f || p.y > 0.25f || p.z > 0.25f)
                {
                    return false;
                }
                foreach (var v in vs)
                {
                    UnityEngine.Vector3 q = v.rot * p;
                    float fx = (q.x - v.minX) * inv, fy = (q.y - v.minY) * inv;
                    if (fx < 0f || fy < 0f || fx >= v.w || fy >= v.h)
                    {
                        return false;
                    }
                    int k = (int)fy * v.w + (int)fx;
                    if (q.z < v.near[k] || q.z > v.far[k])
                    {
                        return false;
                    }
                }
                return true;
            }
            double newBest = double.MaxValue;
            for (int round = 0; round < 5; round++)
            {
                var sw = Stopwatch.StartNew();
                int h = 0;
                for (int i = 0; i < pts.Length; i++)
                {
                    if (New(pts[i]))
                    {
                        h++;
                    }
                }
                sw.Stop();
                newBest = Math.Min(newBest, sw.Elapsed.TotalMilliseconds * 1e6 / pts.Length);
            }
            Console.WriteLine($"New per-view maths (managed copy, inline): {newBest:0.0} ns a call");
            return oldHits == hits ? 0 : 1;
        }

        private static object Get(object o, string name) =>
            o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).GetValue(o);

        private static void Set(object o, string name, object value)
        {
            FieldInfo f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f == null)
            {
                throw new MissingFieldException(o.GetType().Name, name);
            }
            f.SetValue(o, value);
        }
    }
}
